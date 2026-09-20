# Slice 4.3C — Production Push Rollout Blockers & Authoritative Mutation Tracking

## 1. Executive Summary & Purpose
This document specifies the architectural prerequisites and mandatory rollout blockers that must be resolved before `Sync:PushEnabled` can be enabled in production environments (`Sync:PushEnabled = true`).

During Slice 4.3C, the Push mechanism (`LocalOutboxPushService` -> `AzurePushTransactionCoordinator`) was implemented with strict fail-closed guards, single-transaction atomic coordinator, idempotent replays, queue ordering, lease fencing, and error propagation. However, pushing mutations to production Azure SQL databases (`IProgramDb2026`, `IProgramDb2027`) remains **BLOCKED** by architecture design.

---

## 2. Hard Rollout Blockers

### Blocker 1: Authoritative Remote Schema Provisioning
- **Requirement:** The remote sync infrastructure tables:
  - `[sync].[ServerState]`
  - `[sync].[ServerChangeFeed]`
  - `[sync].[ProcessedOperations]`
  must be formally created, verified, and baseline-initialized on `IProgramDb2026` and `IProgramDb2027` with compatibility level 120+.
- **Risk if bypassed:** Pushing to production Azure without authoritative sync tables will result in fatal SQL errors (`Invalid object name 'sync.ServerState'`) and immediate push termination.

### Blocker 2: Server-Side Authoritative Mutation Tracking (Legacy & Direct Writes)
- **Problem:** In production, if web clients, administrators, or legacy background services write directly to Azure `[dbo].[Daily]` without going through the sync coordinator:
  - `[sync].[ServerState].CurrentVersion` will NOT increment.
  - `[sync].[ServerChangeFeed]` will NOT record the change.
  - Offline clients pushing subsequent changes with `ExpectedServerVersion` will overwrite concurrent server-side edits undetected (silent lost updates).
- **Required Architecture Resolution:**
  - Either all Azure writes to `[dbo].[Daily]` must flow exclusively through the sync coordinator/API.
  - OR SQL Server-level triggers / change tracking on `[dbo].[Daily]` must automatically increment `ServerState.CurrentVersion` and write to `ServerChangeFeed` upon any non-sync mutation.

### Blocker 3: Pull Architecture & Bi-directional State Foundation
- **Requirement:** Pushing local changes safely assumes the local database has a mechanism to ingest remote changes before pushing (`Pull`). Slice 4.3C is strictly **Push-only**; bi-directional conflict resolution and pull synchronization are scheduled for future slices (Slice 4.4+).
- **Risk if bypassed:** Without Pull, an offline device that operates for multiple days will experience persistent version conflicts if any other device has pushed changes.

### Blocker 4: Production Azure Binding & Secret Governance
- **Requirement:** Production deployment requires validated Azure bindings strictly bound to production Azure SQL endpoints (`.database.windows.net`), with isolated credentials managed via Azure Key Vault or machine environment variables (`SETUP_MACHINE_SECRETS.md`), with zero credential leakage in logs or responses.

---

## 3. Production Default Policy
- `Sync:PushEnabled` MUST remain `false` in:
  - `appsettings.json`
  - `appsettings.Production.json`
  - Environment defaults
- Any push request while `Sync:PushEnabled != true` MUST fail closed with HTTP 403 `SYNC_PUSH_DISABLED`.
