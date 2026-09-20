# Slice 4.4A — Authoritative Azure Daily Mutation Tracking Architecture & Rollout Prerequisites

## 1. Executive Summary
Slice 4.4A establishes authoritative mutation tracking on Azure SQL databases (`IProgramDb2026`, `IProgramDb2027`) during Online mode (`LocalFirst.Enabled = false`, `ReadOnlyMode = false`). 

Whenever an application-authorized business mutation on `Daily` (INSERT, UPDATE, SOFT_DELETE, HARD_DELETE) occurs through `ApplicationContext` / `UnitOfWork`, the tracking coordinator atomically commits:
1. Lock `[sync].[ServerState]` via `UPDLOCK, HOLDLOCK` and capture `startingServerVersion`
2. Validate tombstone resurrection conditions
3. Business mutation on `[dbo].[Daily]`
4. Tombstone entry in `[sync].[Tombstones]` strictly upon `HARD_DELETE`
5. Versioned audit entry in `[sync].[ServerChangeFeed]` with `OriginDeviceId = Guid.Empty` (Server origin)
6. Version update on `[sync].[ServerState].CurrentVersion`
7. Atomic commit and EF state acceptance

This guarantees `ServerState.CurrentVersion` remains the authoritative, monotonically increasing single source of truth for all changes to `Daily`.

---

## 2. Clarification of "Authoritative" Scope & Boundaries
- **Application-Authorized Boundary:**
  Slice 4.4A guarantees tracking for application-authorized Online Daily writes routed through `ApplicationContext` / `UnitOfWork`. It intentionally operates at the application/ORM boundary.
- **DBA / External Tool Boundary:**
  Slice 4.4A does NOT intercept manual, direct SQL mutations executed outside the application (e.g. DBA opening SSMS and executing `UPDATE dbo.Daily`).
- **Production Rollout Security Requirement:**
  Production deployment of Slice 4.4A requires one of the following architectural controls:
  1. Revoke/prohibit direct `Daily` DML permissions from all non-application database principals (restricting write access strictly to the application connection pool user), OR
  2. Future database-level tracking/trigger design.
- **No SQL Triggers in 4.4A:**
  Slice 4.4A strictly avoids SQL triggers to maintain predictable lock duration and compatibility with SQL Server 2014 (Level 120).
- **Legacy Migration Status:**
  `LegacyMigration` and `DataMigrationService` remain completely disabled.
- **Runtime Raw SQL Audit:**
  Zero runtime raw SQL Daily mutation exists inside the application codebase outside the approved `AzurePushTransactionCoordinator` (Slice 4.3C), which coordinates its own versioning and change feed entries. Verified by static architecture unit tests.

---

## 3. Lock Ordering Architecture (Zero Deadlock Guarantee)
To eliminate Lock Order Inversion between Online authoritative writes and Offline Push (Slice 4.3C):

### Online Authoritative Path (UnitOfWork):
1. **Validate physical Azure binding** BEFORE opening network connection (`SqlConnectionStringBuilder` pre-open guard).
2. **Begin transaction.**
3. **Defense-in-depth binding verification** on opened connection.
4. **Prepare Phase (`PrepareAuthoritativeBatchAsync`):**
   - Acquire `[sync].[ServerState] WITH (UPDLOCK, HOLDLOCK) WHERE DatabaseId = @DatabaseId`.
   - Read `startingServerVersion`.
   - Check `[sync].[Tombstones] WITH (UPDLOCK, HOLDLOCK)` for resurrection prevention.
5. **Business Save (`_context.SaveChangesAsync`):**
   - Apply EF Core Daily DML while `ServerState` lock is already held.
6. **Complete Phase (`CompleteAuthoritativeBatchAsync`):**
   - Insert `[sync].[Tombstones]` (if hard delete).
   - Insert `[sync].[ServerChangeFeed]` entries with contiguous versions.
   - Update `[sync].[ServerState]` final `CurrentVersion`.
7. **Commit transaction** atomically on the same `DbConnection` and `DbTransaction`.
8. **AcceptAllChanges** in EF ChangeTracker.

### Offline Push Path (AzurePushTransactionCoordinator):
1. Check `[sync].[ProcessedOperations] WITH (UPDLOCK, HOLDLOCK)`
2. Lock `[sync].[ServerState] WITH (UPDLOCK, HOLDLOCK)`
3. Check version conflict (`currentServerVersion == expectedServerVersion`)
4. Check `[sync].[Tombstones] WITH (UPDLOCK, HOLDLOCK)`
5. Execute raw Daily DML
6. Insert `[sync].[ServerChangeFeed]`
7. Update `[sync].[ServerState]` final `CurrentVersion`
8. Insert `[sync].[ProcessedOperations]`
9. Commit transaction.

### Symmetrical Lock Hierarchy:
Both pathways acquire `ServerState WITH (UPDLOCK, HOLDLOCK)` BEFORE touching `[dbo].[Daily]`. Whichever session acquires `ServerState` first serializes access. The second session blocks safely on `ServerState` without holding any locks on `Daily`, rendering deadlocks (SQL Error 1205) mathematically impossible.

---

## 4. Hard Scope & Invariants
- **Scope:** `Daily` entity ONLY. Zero Form, Employee, or Department sync.
- **Online Mode Only:** Active only when `LocalFirst.Enabled == false`, `ReadOnlyMode == false`, and `Sync:AuthoritativeTrackingEnabled == true`.
- **Existing Behavior Preserved:** When `Sync:AuthoritativeTrackingEnabled == false`, `ApplicationContext.SaveChangesAsync` executes its existing standard pipeline without interference.
- **Fail-Closed Missing Dependencies:** When `Sync:AuthoritativeTrackingEnabled == true` in Online mode, missing `ISyncConnectionProvider`, `IAuthoritativeDailyMutationTracker`, or `IAuthoritativeDatabaseBindingGuard` immediately throws `AuthoritativeTrackingConfigurationException` (`AUTHORITATIVE_TRACKING_CONFIGURATION_INVALID`). Fallback to standard save is strictly prohibited.
- **Fail-Closed Direct Bypass Prevention:** Direct calls to `_context.SaveChangesAsync` modifying `Daily` outside `AuthoritativeWriteScopeContext` are intercepted and blocked by `AuthoritativeTrackingSafetyInterceptor` (throws `AuthoritativeWriteScopeException`).
- **Nested Scope Hardening:** `AuthoritativeWriteScopeContext` uses an `AsyncLocal<int>` depth counter with idempotent disposal, preventing accidental scope deactivation in nested call hierarchies.
- **Pre-Open Physical Azure Binding:** `UnitOfWork` validates `DataSource` and `InitialCatalog` via `SqlConnectionStringBuilder` BEFORE `OpenAsync()` or `BeginTransactionAsync()`, preventing any network socket creation if the target endpoint is invalid or non-Azure.
- **SyncId Immutability & Generation:** Every Daily mutation must possess a non-empty `SyncId`. `SyncId` is immutable across updates and soft/hard deletes. Modifying `SyncId` fails closed (`AuthoritativeTrackingException`).
- **Resurrection Prevention:** Inserting a `Daily` whose `SyncId` already exists in `[sync].[Tombstones]` is rejected fail-closed (`AuthoritativeTrackingException`).
- **Zero Double Tracking of Offline Push:** Offline Push (`AzurePushTransactionCoordinator`) operates via raw SQL and does not route through `UnitOfWork` or `ApplicationContext.ChangeTracker`, guaranteeing no duplicate feeds or versions.
- **No ProcessedOperations for Online Writes:** `[sync].[ProcessedOperations]` is exclusively dedicated to offline push idempotency; online writes do NOT write to `ProcessedOperations`.

---

## 5. Protocol Constants
- `OriginDeviceId = Guid.Empty`: Represents `SERVER_ORIGIN` / `AUTHORITATIVE_ONLINE_WRITE`.
  In contrast, offline push from client devices (Slice 4.3C) strictly enforces a non-empty client `DeviceId`.

---

## 6. Multi-Mutation Sequential Versioning
When a single `SaveChangesAsync` batch contains multiple Daily mutations:
1. Mutations are captured from the `ChangeTracker` and sorted deterministically: `EntitySyncId ASC`.
2. `[sync].[ServerState]` is locked using `UPDLOCK, HOLDLOCK` during the prepare phase.
3. For $N$ mutations starting at version $V$, each mutation receives a distinct sequential version:
   $$\text{Mutation}_1 \to V + 1$$
   $$\text{Mutation}_2 \to V + 2$$
   $$\dots$$
   $$\text{Mutation}_N \to V + N$$
4. `ServerState.CurrentVersion` is updated to $V + N$ with a single transaction timestamp (`transactionTimestampUtc`).

---

## 7. Mandatory Production Push Rollout Prerequisites
**CRITICAL ARCHITECTURAL POLICY:**
Production Push (`Sync:PushEnabled = true`) cannot and MUST NOT be enabled in production until the following sequential gates are verified:

1. **AuthoritativeTrackingEnabled Rollout & Verification:**
   Deploy Slice 4.4A with `Sync:AuthoritativeTrackingEnabled = true` to production Azure environments.
2. **ServerState Baseline Verification:**
   Verify that `[sync].[ServerState]` rows exist for `2026` and `2027` with valid non-negative initial baselines on production Azure databases (`IProgramDb2026`, `IProgramDb2027`).
3. **Continuous Authoritative Feed Ingestion:**
   Verify that all live web client / server-side mutations to `Daily` are actively generating sequential `ServerChangeFeed` entries without missed versions.
4. **Principal Permissions Lockdown:**
   Ensure non-application principals cannot perform raw DML on `Daily`.
5. **Subsequent Push/Pull Activation:**
   Only after the authoritative change feed is proven continuous and complete in production can `Sync:PushEnabled` and Pull synchronization be enabled for local client devices.

---

## 8. Configuration Safety Defaults
Both feature gates remain **disabled** (`false`) by default:
- `Sync:AuthoritativeTrackingEnabled = false`
- `Sync:PushEnabled = false`
