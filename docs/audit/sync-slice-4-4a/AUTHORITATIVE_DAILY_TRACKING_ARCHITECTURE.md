# Slice 4.4A — Authoritative Azure Daily Mutation Tracking Architecture & Rollout Prerequisites

## 1. Executive Summary
Slice 4.4A establishes authoritative mutation tracking on Azure SQL databases (`IProgramDb2026`, `IProgramDb2027`) during Online mode (`LocalFirst.Enabled = false`, `ReadOnlyMode = false`). 

Whenever a business mutation on `Daily` (INSERT, UPDATE, SOFT_DELETE, HARD_DELETE) occurs on the authoritative Azure database, the tracking coordinator atomically commits:
1. Business mutation on `[dbo].[Daily]`
2. Version increment on `[sync].[ServerState].CurrentVersion` (locked via `UPDLOCK, HOLDLOCK`)
3. Versioned audit entry in `[sync].[ServerChangeFeed]` with `OriginDeviceId = Guid.Empty`
4. Tombstone entry in `[sync].[Tombstones]` strictly upon `HARD_DELETE` (with `NaturalKey = NULL`, `ServerVersion`, `DeletedAtUtc`)

This ensures `ServerState.CurrentVersion` remains the authoritative, monotonically increasing single source of truth for all changes to `Daily`.

---

## 2. Hard Scope & Invariants
- **Scope:** `Daily` entity ONLY. Zero Form, Employee, or Department sync.
- **Online Mode Only:** Active only when `LocalFirst.Enabled == false`, `ReadOnlyMode == false`, and `Sync:AuthoritativeTrackingEnabled == true`.
- **Existing Behavior Preserved:** When `Sync:AuthoritativeTrackingEnabled == false`, `ApplicationContext.SaveChangesAsync` executes its existing standard pipeline without interference.
- **Fail-Closed Direct Bypass Prevention:** Direct calls to `_context.SaveChangesAsync` that modify `Daily` outside of `AuthoritativeWriteScopeContext` are intercepted and blocked by `AuthoritativeTrackingSafetyInterceptor` (throws `AuthoritativeWriteScopeException`). Non-Daily mutations (e.g. Employee, Form) continue unhindered.
- **SyncId Immutability & Generation:** Every Daily mutation must possess a non-empty `SyncId`. `SyncId` is immutable across updates and soft/hard deletes. Modifying `SyncId` fails closed (`AuthoritativeTrackingException`).
- **Resurrection Prevention:** Inserting a `Daily` whose `SyncId` already exists in `[sync].[Tombstones]` is rejected fail-closed (`AuthoritativeTrackingException`).
- **Single Physical Transaction:** Business entity changes and sync metadata are executed on the same `DbConnection` and `DbTransaction` within EF Core execution strategy (`EnableRetryOnFailure` compatible). A metadata failure triggers a complete transaction rollback of business changes.
- **Zero Double Tracking of Offline Push:** Offline Push (`AzurePushTransactionCoordinator`) operates via raw SQL and does not route through `UnitOfWork` or `ApplicationContext.ChangeTracker`, guaranteeing no duplicate feeds or versions.
- **No ProcessedOperations for Online Writes:** `[sync].[ProcessedOperations]` is exclusively dedicated to offline push idempotency; online writes do NOT write to `ProcessedOperations`.

---

## 3. Protocol Constants
- `OriginDeviceId = Guid.Empty`: Represents `SERVER_ORIGIN` / `AUTHORITATIVE_ONLINE_WRITE`.
  In contrast, offline push from client devices (Slice 4.3C) strictly enforces a non-empty client `DeviceId`.

---

## 4. Multi-Mutation Sequential Versioning
When a single `SaveChangesAsync` batch contains multiple Daily mutations:
1. Mutations are captured from the `ChangeTracker` and sorted deterministically: `EntitySyncId ASC`.
2. `[sync].[ServerState]` is locked using `UPDLOCK, HOLDLOCK`.
3. For $N$ mutations starting at version $V$, each mutation receives a distinct sequential version:
   $$\text{Mutation}_1 \to V + 1$$
   $$\text{Mutation}_2 \to V + 2$$
   $$\dots$$
   $$\text{Mutation}_N \to V + N$$
4. `ServerState.CurrentVersion` is updated to $V + N$ with a single transaction timestamp (`transactionTimestampUtc`).

---

## 5. Physical Azure Binding Guard
- `IAuthoritativeDatabaseBindingGuard` enforces fail-closed validation of server endpoints and physical database names.
- In production, `AuthoritativeDatabaseBindingGuard` requires `*.database.windows.net` and validates `IProgramDb2026` / `IProgramDb2027` against canonical database IDs `2026` / `2027`.
- Localhost or non-Azure endpoints are strictly forbidden in production configuration.
- Integration tests inject isolated test guards against dedicated test databases (`IProgramRemoteSync2026_SmokeTest`, `IProgramRemoteSync2027_SmokeTest`).

---

## 6. Mandatory Production Push Rollout Prerequisites
**CRITICAL ARCHITECTURAL POLICY:**
Production Push (`Sync:PushEnabled = true`) cannot and MUST NOT be enabled in production until the following sequential gates are verified:

1. **AuthoritativeTrackingEnabled Rollout & Verification:**
   Deploy Slice 4.4A with `Sync:AuthoritativeTrackingEnabled = true` to production Azure environments.
2. **ServerState Baseline Verification:**
   Verify that `[sync].[ServerState]` rows exist for `2026` and `2027` with valid non-negative initial baselines on production Azure databases (`IProgramDb2026`, `IProgramDb2027`).
3. **Continuous Authoritative Feed Ingestion:**
   Verify that all live web client / server-side mutations to `Daily` are actively generating sequential `ServerChangeFeed` entries without missed versions.
4. **Subsequent Push/Pull Activation:**
   Only after the authoritative change feed is proven continuous and complete in production can `Sync:PushEnabled` and Pull synchronization be enabled for local client devices.

---

## 7. Configuration Safety Defaults
Both feature gates remain **disabled** (`false`) by default:
- `Sync:AuthoritativeTrackingEnabled = false`
- `Sync:PushEnabled = false`
