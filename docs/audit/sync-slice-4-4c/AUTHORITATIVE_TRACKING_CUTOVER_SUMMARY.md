# Slice 4.4C — Controlled Authoritative Tracking Production Cutover Summary

## 1. Executive Summary
Authoritative Azure Daily Mutation Tracking was successfully activated in a controlled, fail-closed operational cutover across production databases (`IProgramDb2026` and `IProgramDb2027`).

All canary mutations (INSERT and HARD_DELETE) traversed the approved application data path:
`ApplicationContext → UnitOfWork → AuthoritativeDailyMutationTracker`.
Zero direct SQL business DML was executed. Both canaries were created and completely hard-deleted, leaving production business tables in 100% exact parity with local replicas.

---

## 2. Cutover Reconciliation Matrix

| Dimension / Metric | Year 2026 | Year 2027 |
| :--- | :--- | :--- |
| **Pre-Cutover Azure ServerVersion** | 0 | 0 |
| **Pre-Cutover Local LastServerVersion** | 0 | 0 |
| **Canary SyncId Hash (Truncated)** | `318B361637144461` | `910E347505586896` |
| **Post-INSERT Azure ServerVersion** | 1 (v0 + 1) | 1 (v0 + 1) |
| **INSERT Feed Entry** | `OperationType = INSERT`, `OriginDeviceId = Guid.Empty` | `OperationType = INSERT`, `OriginDeviceId = Guid.Empty` |
| **Post-HARD_DELETE Azure ServerVersion** | 2 (v0 + 2) | 2 (v0 + 2) |
| **HARD_DELETE Feed Entry** | `OperationType = HARD_DELETE`, `OriginDeviceId = Guid.Empty` | `OperationType = HARD_DELETE`, `OriginDeviceId = Guid.Empty` |
| **Tombstone Created** | Yes (`ServerVersion = 2`, `EntityType = Daily`) | Yes (`ServerVersion = 2`, `EntityType = Daily`) |
| **ProcessedOperations Count** | 0 | 0 |
| **LocalOutbox Mutations** | 0 | 0 |
| **Azure Daily Rows (Post-Purge)** | 30 | 14 |
| **Local Daily Rows** | 30 | 14 |
| **Daily Table Hash Match** | **EXACT MATCH (100%)** | **EXACT MATCH (100%)** |
| **Post-Cutover Classification** | **`TRACKED_VERSION_GAP`** | **`TRACKED_VERSION_GAP`** |
| **Local LastServerVersion Post-Cutover** | 0 (Unmodified, awaiting Pull) | 0 (Unmodified, awaiting Pull) |

---

## 3. Cross-Year Isolation Proof
- **Canary 2026**: Executed solely within `IProgramDb2026`. During its lifecycle, `IProgramDb2027` ServerVersion remained invariant at `0` and ChangeFeed remained empty.
- **Canary 2027**: Executed solely within `IProgramDb2027`. During its lifecycle, `IProgramDb2026` ServerVersion remained invariant at `2` with zero cross-contamination.

---

## 4. Phase 8 — Fail-Closed Post-Cutover Protection Design

### The Problem
Once cutover is committed, `ServerState.CurrentVersion` advances beyond `0` on Azure production. If `Sync:AuthoritativeTrackingEnabled` is accidentally rolled back to `false` in an Online production environment, raw EF Core `SaveChangesAsync` would silently execute untracked business DML, re-introducing untracked drift.

### Design Recommendation for System Architect Review
We evaluated three non-breaking architectural options:

1. **Option A (Startup / Health Check Probe Guard) [Recommended]**:
   - During application startup or via an ASP.NET Core Health Check probe (`AuthoritativeCutoverReadinessCheck`), query Azure `ServerState.CurrentVersion`.
   - If `CurrentVersion > 0` (indicating cutover has occurred) and `Sync:AuthoritativeTrackingEnabled == false` while in Online mode (not LocalFirst, not ReadOnlyMode):
     - Log fatal error and fail startup or fail the health check (`CUTOVER_COMMITTED_TRACKING_DISABLED`).
   - *Impact*: Zero schema changes, zero database DDL, does not affect dev/test (where `CurrentVersion == 0` or LocalDb is used).

2. **Option B (AuthoritativeTrackingSafetyInterceptor Invariant)**:
   - In `AuthoritativeTrackingSafetyInterceptor`, maintain a cached boolean flag indicating whether the connected database has `CurrentVersion > 0`. If `isTrackingEnabled == false` but `CurrentVersion > 0`, throw `AuthoritativeWriteScopeException`.
   - *Trade-off*: Requires a one-time cached query on the connection.

3. **Option C (Deployment Environment Flag)**:
   - Introduce an operational environment variable `Sync__CutoverCommitted=true` set in production Azure App Service / container configuration.
   - If `CutoverCommitted == true` and `AuthoritativeTrackingEnabled == false`, throw at startup.

---

## 5. Repository Safety Invariants
- `src/Api/appsettings.json`:
  - `Sync:AuthoritativeTrackingEnabled = false` (Committed default preserved)
  - `Sync:PushEnabled = false` (Committed default preserved)
  - `LegacyMigration:Enabled = false` (Committed default preserved)
- LocalState / Outbox:
  - `LastServerVersion` intentionally maintained at `0` across both local databases.
  - Zero manual updates to local sync tables.
