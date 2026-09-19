# Phase 4 Slice 4.1B: Production Rollout & Verification Tooling

This directory contains the exact, reproducible, and sanitized deployment artifacts and verification scripts used for the **Phase 4 Slice 4.1B** rollout on `IProgramDb2026` and `IProgramDb2027`.

> [!IMPORTANT]
> **Controlled Rollout Tooling Only:**
> These scripts represent deployment and verification tooling executed under strict maintenance windows. They are not invoked by the normal application runtime.

---

## Script Manifest & Execution Order

1. **`01_staged_sync_migration.sql`**
   - Applies the nullable `SyncId UNIQUEIDENTIFIER NULL` column to all 11 approved business tables with filtered unique indexes (`WHERE [SyncId] IS NOT NULL`).
   - Generates zero database default constraints.
   - Records `20260919161502_AddSyncIdToBusinessEntities` in `[dbo].[__EFMigrationsHistory]`.

2. **`02_controlled_production_backfill.sql`**
   - Explicit, idempotent DML populating `SyncId` using `NEWID()` strictly `WHERE [SyncId] IS NULL`.
   - Never alters existing non-null GUIDs.
   - Re-run safe and idempotent (GUID generation via `NEWID()` is intentionally non-deterministic, while the target row scoping and overall procedure are strictly idempotent and stable once assigned).

3. **`03_finalization_sync_migration.sql`**
   - Drops staged filtered indexes.
   - Alters `SyncId` to `NOT NULL` across all 11 tables (omitting `defaultValue` in scaffolding to ensure zero default constraint is added).
   - Creates standard unfiltered `UNIQUE NONCLUSTERED INDEX` on `[SyncId]`.
   - Records `20260919163939_FinalizeSyncIdNotNull` in `[dbo].[__EFMigrationsHistory]`.

4. **`04_azure_sync_metadata_migration.sql`**
   - Creates isolated `[sync]` schema tables on Azure: `ServerState`, `ServerChangeFeed`, `Tombstones`, and `ProcessedOperations`.
   - Creates dedicated auxiliary history table `[sync].[__EFMigrationsHistory_AzureSync]`.
   - Records `20260919155715_InitialAzureSyncSchema`.

5. **`verify_production_sync_state.ps1`**
   - Strictly **READ-ONLY** verification tool.
   - Inspects migration records, null/duplicate counts, row counts, `IDENT_CURRENT` counters, PK/FK health, SHA-256 data fingerprints, and checks for zero presence of local sync tables on Azure.
