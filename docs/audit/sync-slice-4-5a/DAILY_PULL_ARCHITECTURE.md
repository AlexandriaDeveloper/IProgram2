# Slice 4.5A — Daily Pull Engine Foundation Architecture

## Executive Summary
This document provides the architectural reference and design specification for **Slice 4.5A — Daily Pull Engine Foundation** in `AlexandriaDeveloper/IProgram2`.

The engine establishes safe, resilient state-convergence synchronization from Azure Authoritative SQL Database (`sync.ServerChangeFeed`, `sync.ServerState`, `sync.Tombstones`, and `dbo.Daily`) to Local SQL Server 2014 (`sync.LocalState`, `dbo.Daily`).

---

## 1. Metadata-Only Feed & Prohibition of Naive Event Replay

In the current schema, `sync.ServerChangeFeed` stores only operation metadata:
* `FeedId` (bigint)
* `ServerVersion` (bigint)
* `DatabaseId` (nvarchar(32))
* `EntityType` (nvarchar(50))
* `EntitySyncId` (uniqueidentifier)
* `OperationType` (nvarchar(20)) — `INSERT`, `UPDATE`, `SOFT_DELETE`, `HARD_DELETE`
* `OriginDeviceId` (uniqueidentifier)
* `TimestampUtc` (datetime2)

**Critical Architectural Fact:** The feed contains **no business payload snapshot**. 

Attempting naive event replay (fetching and applying each intermediate feed event one by one) is fundamentally broken. For example, in the production cutover canary lifecycle:
* **Version 1:** `INSERT` canary Daily record ($SyncId = X$).
* **Version 2:** `HARD_DELETE` canary Daily record ($SyncId = X$).

The physical row for $X$ on Azure no longer exists in `dbo.Daily`; it has been tombstoned in `sync.Tombstones`. If an engine attempted to replay Version 1 by reading Azure `dbo.Daily`, the query would return no rows, causing an unexpected failure or data corruption.

Therefore, the Pull Engine implements **Fenced State-Convergence Pull**.

---

## 2. Remote High-Watermark Fence

To guarantee serializability and prevent race conditions during pull batch materialization:

1. **Short Azure Transaction:** The engine opens a read transaction with `IsolationLevel.Serializable` on Azure SQL.
2. **Lock Order Compatibility:** Consistent with Authoritative Tracking locking rules (`ServerState` first), the engine locks the target year's `ServerState` row:
   ```sql
   SELECT CurrentVersion
   FROM [sync].[ServerState] WITH (UPDLOCK, HOLDLOCK)
   WHERE DatabaseId = @DatabaseId;
   ```
3. **High-Watermark ($H$):** While this lock is held, no concurrent authoritative online mutation (`AuthoritativeDailyMutationTracker`) or client push (`AzurePushTransactionCoordinator`) can advance `ServerVersion` or insert new feed records for that database.
4. **Checkpoint Comparison:**
   * $H = \text{Azure ServerVersion}$
   * $L = \text{LocalState.LastServerVersion}$
   * If $H == L$: The engine commits the transaction immediately and returns **NO-OP SUCCESS** without opening local write transactions.
   * If $H < L$: The engine fails closed with `PULL_CHECKPOINT_AHEAD_OF_SERVER`.

---

## 3. Feed Window Validation & Full-Window Coalescing

Inside the fenced transaction, the feed window $(L, H]$ is queried:
```sql
SELECT FeedId, ServerVersion, DatabaseId, EntityType, EntitySyncId, OperationType, OriginDeviceId, TimestampUtc
FROM [sync].[ServerChangeFeed]
WHERE DatabaseId = @DatabaseId
  AND ServerVersion > @LowWatermark
  AND ServerVersion <= @HighWatermark
ORDER BY ServerVersion ASC;
```

### Strict Sequence Validation
The feed sequence must be continuous without gaps or duplicates:
* Exactly covers $L+1, L+2, \dots, H$.
* Any gap throws `PULL_FEED_GAP`.
* Any duplicate `ServerVersion` throws `PULL_DUPLICATE_VERSION`.
* Any unsupported entity (`EntityType != "Daily"`) throws `PULL_UNSUPPORTED_ENTITY_TYPE`.
* Any unrecognized operation throws `PULL_UNSUPPORTED_OPERATION_TYPE`.

### Coalescing Algorithm
Events in the range $(L, H]$ are grouped by `EntitySyncId`:
$$\text{TerminalEvent}(SyncId) = \arg\max_{e \in \text{Events}(SyncId)} (e.\text{ServerVersion})$$

Only the **terminal authoritative state** is materialized and applied:
* Intermediate mutations for the same entity within the window are discarded.
* If an entity was inserted and subsequently hard-deleted within the window (e.g. canary), the terminal state is `HARD_DELETE`. No intermediate insert is performed locally.

---

## 4. Terminal State Materialization

While still holding the Azure `ServerState` fence:

### Case A: Terminal Event is `HARD_DELETE`
1. Verifies that `sync.Tombstones` contains exactly one record for `(DatabaseId, EntityType='Daily', EntitySyncId, ServerVersion=TerminalVersion)`.
2. Verifies that authoritative `dbo.Daily` does **not** contain a row for this `SyncId` (fail-closed if active row exists).
3. Produces a `DeleteCommand(SyncId)`. Locally, deleting an absent row is an idempotent no-op.

### Case B: Terminal Event is `INSERT`, `UPDATE`, or `SOFT_DELETE`
1. Verifies that `sync.Tombstones` does **not** contain an entry for this `SyncId`.
2. Reads the full authoritative snapshot from `dbo.Daily`:
   ```sql
   SELECT SyncId, Name, DailyDate, Closed, CreatedAt, CreatedBy, UpdatedAt, UpdatedBy, DeactivatedAt, DeactivatedBy, IsActive
   FROM [dbo].[Daily]
   WHERE SyncId = @SyncId;
   ```
3. Fails closed (`PULL_AUTHORITATIVE_ROW_MISSING`) if the row cannot be read.
4. Produces an `UpsertCommand(Snapshot)`.

### Release of Azure Fence
Once all commands are materialized into an immutable in-memory batch, the Azure read transaction commits and closes. **The remote fence lock is completely released before any local transactions begin.**

---

## 5. Shared Local Lease & Mutual Exclusion

Pull and Push share the exact same lease columns on `[sync].[LocalState]`:
* `ActiveLeaseToken` (uniqueidentifier)
* `LeaseExpiresAtUtc` (datetime2)
* `LastSyncAttemptUtc` (datetime2)

```sql
UPDATE [sync].[LocalState]
SET ActiveLeaseToken = @LeaseToken,
    LeaseExpiresAtUtc = DATEADD(SECOND, @DurationSec, SYSUTCDATETIME()),
    LastSyncAttemptUtc = SYSUTCDATETIME()
WHERE DatabaseId = @DatabaseId
  AND (ActiveLeaseToken IS NULL OR LeaseExpiresAtUtc < SYSUTCDATETIME() OR ActiveLeaseToken = @LeaseToken);
```

### Safety Guarantees
* **Active Push blocks Pull:** If Push holds a valid unexpired lease, the query updates 0 rows, throwing `SYNC_PULL_ALREADY_RUNNING`.
* **Active Pull blocks Push:** If Pull holds the lease, Push's acquire query updates 0 rows, throwing `SYNC_PUSH_ALREADY_RUNNING`.
* **Concurrency Protection:** Two concurrent Pull sessions cannot run simultaneously on the same database.
* **Expired Lease Reclaim:** Safely reclaims leases whose `LeaseExpiresAtUtc < SYSUTCDATETIME()`.
* **Privacy / Security:** Full `LeaseToken` values are never emitted to logs; only masked prefixes (e.g. `12345678***`) are logged.

---

## 6. Outbox Conflict Gate

Prior to applying any server mutations locally, the engine inspects `[sync].[LocalOutbox]`:
```sql
SELECT COUNT(*)
FROM [sync].[LocalOutbox]
WHERE DatabaseId = @DatabaseId
  AND Status IN ('PENDING', 'IN_PROGRESS', 'FAILED');
```

* If any unmerged local operations exist, Pull is blocked fail-closed with:
  `PULL_BLOCKED_LOCAL_CHANGES_PENDING`
  *Rationale:* Bidirectional conflict resolution is not yet implemented. Overwriting local state while outbound mutations are pending would risk silent data loss.
* **Completed Rows Allowed:** Records with `Status = 'COMPLETED'` represent historical acknowledged operations and do **not** block Pull.

---

## 7. Local Atomic Apply & Checkpoint Atomicity

Local application runs inside a single atomic transaction on Local SQL Server 2014 (`IsolationLevel.ReadCommitted`):

1. **Lock & Verify LocalState:**
   ```sql
   SELECT LastServerVersion, ActiveLeaseToken, LeaseExpiresAtUtc
   FROM [sync].[LocalState] WITH (UPDLOCK, HOLDLOCK)
   WHERE DatabaseId = @DatabaseId;
   ```
   Verifies `LastServerVersion == L` and lease ownership is still valid. If changed mid-pull, aborts with `PULL_LOCAL_CHECKPOINT_CHANGED`.
2. **Re-verify Outbox:** Confirms no pending outbox operations were queued during batch materialization.
3. **Apply Business Mutations by SyncId:**
   * **UPSERT:** Queries `[dbo].[Daily]` by `SyncId`.
     * If exists: Updates scalar fields (`Name`, `DailyDate`, `Closed`, `CreatedAt`, `CreatedBy`, `UpdatedAt`, `UpdatedBy`, `DeactivatedAt`, `DeactivatedBy`, `IsActive`). Preserves local integer `Id`.
     * If not exists: Inserts row with `SyncId` and scalar fields, allowing SQL Server to generate the local identity `Id`.
   * **HARD_DELETE:** `DELETE FROM [dbo].[Daily] WHERE SyncId = @SyncId;` (0 rows affected is an idempotent no-op).
4. **Advance Checkpoint:**
   ```sql
   UPDATE [sync].[LocalState]
   SET LastServerVersion = @HighWatermark,
       LastSuccessfulPullUtc = SYSUTCDATETIME(),
       LastSyncAttemptUtc = SYSUTCDATETIME(),
       LastSyncError = NULL
   WHERE DatabaseId = @DatabaseId
     AND ActiveLeaseToken = @LeaseToken
     AND LeaseExpiresAtUtc >= SYSUTCDATETIME();
   ```
5. **Commit Transaction:** Business mutations and checkpoint advance are strictly atomic. If any mutation fails, the transaction rolls back completely and `LastServerVersion` remains at $L$.
6. **No Local Outbox Generated:** Pull mutations are server-origin reconciliations. They bypass the offline write UnitOfWork pipeline; zero records are created in `[sync].[LocalOutbox]`.

---

## 8. Crash Recovery & Retry Idempotency

* **Crash Before Local Commit:**
  The local transaction rolls back automatically. No business rows are partially committed. `LocalState.LastServerVersion` remains at $L$. On restart, Pull cleanly re-reads from $L$.
* **Crash After Local Commit:**
  `LocalState.LastServerVersion` has been advanced to $H$. On restart or retry, Pull reads $L = H$ and $H = H$, detects $H == L$, and immediately returns NO-OP SUCCESS with zero side effects.

---

## 9. Identity Decoupling: Why Integer Id is Never Sync Identity

In `IProgram2`, `dbo.Daily.Id` is an integer auto-increment identity (`IDENTITY(1,1)`).
* Azure SQL Database and Local SQL Server independently assign integer IDs.
* Forcing Azure integer IDs on local SQL Server would require hazardous `SET IDENTITY_INSERT` toggles, cause primary key collisions with existing local offline rows, and break foreign keys.
* Sync identity is strictly defined by the globally unique `SyncId` (`uniqueidentifier`). All matching, UPSERT, and DELETE operations are indexed and executed exclusively by `SyncId`.

---

## 10. Verification Matrix Summary

The implementation includes automated test coverage across 36 distinct architectural scenarios:
1. Feature gate disabled rejection
2. $L == H$ no-op verification
3. $H < L$ fail-closed verification
4. Canary v1 INSERT $\to$ v2 HARD_DELETE coalescing and version advance
5. Terminal INSERT local application
6. Terminal UPDATE scalar preservation of integer `Id`
7. Terminal SOFT_DELETE `IsActive = false` mirroring
8. Terminal HARD_DELETE deletion of existing row
9. Terminal HARD_DELETE idempotent no-op on absent row
10. INSERT $\to$ UPDATE single coalesced upsert
11. INSERT $\to$ UPDATE $\to$ SOFT_DELETE inactive coalesced snapshot
12. Multi-entity contiguous version application
13. Feed gap detection and rollback
14. Duplicate ServerVersion rejection
15. Unsupported EntityType rejection
16. Unsupported OperationType rejection
17. Missing Tombstone fail-closed
18. Tombstone version mismatch fail-closed
19. Missing authoritative row fail-closed
20. Conflict between active mutation and tombstone fail-closed
21. PENDING outbox blocking
22. IN_PROGRESS outbox blocking
23. FAILED outbox blocking
24. COMPLETED outbox allowed
25. LocalOutbox count invariance before and after pull
26. Stale or stolen lease commit rejection
27. Concurrent pull session prevention
28. Active Push lease blocking Pull
29. Active Pull lease blocking Push
30. Local checkpoint change mid-pull rollback
31. Injected local DML fault rollback
32. Wrong Azure database year binding failure
33. Wrong Local database year binding failure
34. Database 2026 and 2027 isolation
35. Remote `(UPDLOCK, HOLDLOCK)` fence locking pattern
36. Retry after successful commit returning NO-OP
