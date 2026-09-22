# Slice 4.5C — Isolated Local Cutover Rehearsal Runbook & Specification

## 1. Overview & Objective

**Slice 4.5C: Isolated Local Cutover Rehearsal** proves the end-to-end viability of the offline read-write pilot, restart durability, and push/pull sync convergence using **strictly local, transient, isolated SQL Server databases**, with **zero connection to Azure Production**.

The primary architectural question answered by this rehearsal is:
> **Can IProgram start, authenticate, read, perform the already-approved Daily offline-write pilot, survive restart, then Push/Pull and converge using isolated databases, without depending on Azure during normal local operation?**

---

## 2. Strict Safety & Isolation Boundaries

1. **SQL Compatibility Invariant**:
   - All rehearsal databases strictly target **SQL Server 2014 compatibility (`COMPATIBILITY_LEVEL = 120`)**.
2. **Physical Isolation**:
   - Only transient rehearsal databases (`_Test` and `_SmokeTest` suffixes) are provisioned on localhost.
   - Operational local databases (`IProgramLocalDb2026`, `IProgramLocalDb2027`) and operational Azure databases (`IProgramDb2026`, `IProgramDb2027`) are **never accessed, connected to, or modified**.
3. **Ephemeral Identity & Cryptographic Security**:
   - Zero access or fallback to committed `appsettings.json` `Token.Key` or `dotnet user-secrets`.
   - Ephemeral in-memory JWT signing key (64+ bytes / 512+ bits entropy) generated dynamically at runtime and injected into process environment (`Token__Key`).
   - Zero hardcoded passwords or PBKDF2 hashes; dynamic CSPRNG test password generated at runtime and hashed using ASP.NET Core Identity v3 RFC2898DeriveBytes (HMAC-SHA512, 100,000 iterations, 16-byte salt, 32-byte subkey).
   - Sensitive credentials and keys are never logged, printed, or persisted.
   - Static AST machine-checkable guard assertion validates zero `dotnet user-secrets` invocations and zero `appsettings.json` token lookups.
4. **Process Environment Snapshot & Verification**:
   - Process environment variables are snapshotted prior to rehearsal execution.
   - Environment variables are explicitly cleared between test phases to prevent configuration leakage.
   - Teardown restores the snapshot and performs a deterministic bit-for-bit assertion verifying all keys are properly restored/cleared.
5. **Committed Defaults Invariant**:
   - Committed application settings in `src/Api/appsettings.json` remain unchanged:
     - `Sync:AuthoritativeTrackingEnabled = true`
     - `Sync:PullEnabled = false`
     - `Sync:PushEnabled = false`
     - `LocalFirst:Enabled = false`
6. **Testing Environment & Remote Tripwire**:
   - LocalFirst/Pull/Push flags and connection strings are supplied exclusively to a dedicated API process in the `Testing` environment.
   - When remote is supposed to be unavailable (Phases B, C, D, G), remote connection strings point to an unreachable tripwire endpoint (`127.0.0.1:59999`) to fail closed if any unexpected remote access is attempted.

---

## 3. Rehearsal Architecture & Topology

```
+--------------------------------------------------------------------------------------------------+
|                                  TRANSIENT ISOLATED FIXTURES                                     |
|                                                                                                  |
|   +--------------------------+        +---------------------------+        +-----------------+   |
|   |  Client A (Isolated DB)  |        | Isolated Authoritative DB |        |    Client B     |   |
|   |  IProgramLocalDb2026_Test|        | IProgramRemoteSync2026_Tst|        | (2nd Client DB) |   |
|   |  IProgramLocalDb2027_Test|        | IProgramRemoteSync2027_Tst|        |                 |   |
|   +------------+-------------+        +-------------+-------------+        +--------+--------+   |
|                |                                    ^                               ^            |
|       (Phase C: Offline Write)                      |                               |            |
|       (Phase D: Restart Durability)                 |                               |            |
|                |                                    |                               |            |
|                +------------ (Phase E: Push) -------+                               |            |
|                                                     |                               |            |
|                                                     +------ (Phase F: Pull) --------+            |
|                                                               (Convergence)                      |
+--------------------------------------------------------------------------------------------------+
```

---

## 4. Rehearsal Phases & Verification Matrix

| Phase | Description | Key Invariants Tested | Expected Outcome |
|---|---|---|---|
| **Phase A** | Bootstrap / Write-Gate Readiness | SQL 2014 compatibility (120); missing/unverified manifest fails closed (`IsWriteAllowed=0`); verified ready permits writes. | **PASS**: Write gate enforces strict fail-closed boundary. |
| **Phase B** | Local Runtime with Remote Unavailable | Dedicated loopback API in `Testing` (`LocalFirst=true`, remote tripwire); synthetic login; `runtimeMode == "OfflineReadWritePilot"`. | **PASS**: Local auth and local reads succeed without remote access. |
| **Phase C** | Transactional Offline Daily Write | `POST /api/Daily` commits 1 `Daily` + 1 `LocalOutbox` atomically; trigger fault rolls back both 100%; out-of-scope mutation rejected with 403. | **PASS**: Atomic commit, fault rollback, and scope isolation proven. |
| **Phase D** | Restart Durability (Outage Proof) | Stop API process, restart against same local DB (still tripwire remote); re-auth; verify Daily & pending outbox persist; second offline write. | **PASS**: Complete data durability across process restarts. |
| **Phase E** | Isolated Push | Point to isolated remote peer; `Sync:PushEnabled=true`; `POST /api/sync/push` advances remote `ServerState` & `ChangeFeed`; outbox `Completed`. | **PASS**: Idempotent push succeeds without duplicate remote mutations. |
| **Phase F** | Isolated Pull / Convergence | Client B (`W=0`); `Sync:PullEnabled=true`; `POST /api/sync/pull` converges Client B to remote `H_exec`; cryptographic hash matches Client A. | **PASS**: Full second-local convergence with zero-mutation idempotent retry. |
| **Phase G** | Final Restart / Local Usability | Restart Client B with `Pull=false`, `Push=false`, remote tripwire; verify local reads without remote access. | **PASS**: Complete local operability verified post-convergence. |
| **Phase H** | Clean Teardown | Terminate all API processes; drop all 6 rehearsal databases; verify 0 operational databases touched. | **PASS**: Zero persistent footprint, zero operational contamination. |

---

## 5. Execution Command

```powershell
# 1. Verify AST syntax
powershell -ExecutionPolicy Bypass -File script/local-bootstrap/verify_script_syntax.ps1

# 2. Run automated rehearsal
powershell -ExecutionPolicy Bypass -File script/sync-rollout/test_isolated_local_cutover_rehearsal.ps1
```

---

## 6. Execution Evidence & Verified Invariants

### Rehearsal Run Summary
- **Execution Date**: 2026-09-22
- **Baseline Master SHA**: `3499fce7f5ceeaef933ca21823775fb4966a6dbd`
- **SQL Server Compatibility Level**: 120 (SQL Server 2014) verified across all 6 fixtures.
- **Operational Databases Touched**: `0` (Zero Azure, zero local operational DBs).
- **Overall Result**: **PASS (All 8 Phases A–H succeeded)**

### Verified Invariants
1. **Bootstrap / Write Gate Readiness (Phase A)**:
   - Missing manifest fails closed.
   - Unverified manifest (`IsWriteAllowed = false`) blocks writes fail-closed.
   - Verified manifest (`VERIFIED_READY`, `IsWriteAllowed = true`) enables writes.
2. **Local Runtime with Remote Unavailable (Phase B)**:
   - Dedicated API on port 5105 in `Testing` environment started cleanly.
   - Synthetic admin authenticated against local ASP.NET Core Identity tables.
   - `runtimeMode: "OfflineReadWritePilot"`, `isLocalFirst: true`, `isReadOnly: false`.
   - Local reads served without touching the unreachable tripwire remote (`127.0.0.1:59999`).
3. **Transactional Offline Daily Write & Rollback (Phase C)**:
   - Approved `POST /api/Daily` mutation committed 1 `dbo.Daily` and 1 `sync.LocalOutbox` row atomically.
   - Fault injection proof: trigger on `sync.LocalOutbox` verified that an outbox failure causes 100% rollback of both business and outbox rows.
   - Fail-closed write scope guard: mutation on out-of-scope entity (`Employee`) was rejected fail-closed with 403 `OFFLINE_WRITE_SCOPE_BLOCKED`.
4. **Restart Durability (Phase D)**:
   - Process terminated and restarted against the same Client A local database with remote tripwire active.
   - Re-authentication succeeded, and both the Daily mutation and pending outbox record remained 100% persisted.
   - Second offline mutation (`PUT /api/Daily`) succeeded with 2 pending outbox records queued.
5. **Isolated Push (Phase E)**:
   - Process restarted with `Sync:PushEnabled = true` pointing to isolated remote peer.
   - `POST /api/sync/push` pushed 2 pending outbox records, advanced remote `ServerState` to version 2, created 2 `ServerChangeFeed` entries, and transitioned local outbox to `COMPLETED`.
   - Idempotent push retry confirmed: `succeeded: 0`, 0 duplicate writes, remote version unchanged.
6. **Isolated Pull / Convergence (Phase F)**:
   - Second client (Client B) started at watermark 0 with empty Daily table.
   - `POST /api/sync/pull` converged Client B from watermark 0 to final server version 2.
   - Parity verified: **100% Cryptographic Data Hash Match** (`0DBA02AC4B7AFD929393E4A34935A29975BCDE1EF1F7296CC5F57C345CF746A8`) between Client B and Remote Peer.
   - Idempotent pull retry confirmed: `isNoOp: true`, watermark unchanged, 0 business mutations.
7. **Final Restart / Local Usability (Phase G)**:
   - Process restarted against converged Client B with `Pull=false`, `Push=false`, and remote tripwire active.
   - Re-authenticated and served local reads directly from local data without remote access.
8. **Clean Teardown & Audit (Phase H)**:
   - All API processes cleanly terminated.
   - Pre-rehearsal process environment restored from initial snapshot and verified bit-for-bit with deterministic assertions (`EnvironmentCleanupVerified = true`).
   - All 6 transient rehearsal databases dropped.
   - Audit confirmed 0 operational databases touched.

