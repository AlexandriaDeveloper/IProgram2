-- ==============================================================================
-- PHASE 4 — SLICE 4.1B: Controlled Rollout Tooling
-- Stage B: Controlled Production Backfill (Deterministic, Safe, Resumable)
-- Applied to: IProgramDb2026, IProgramDb2027
-- 
-- Rules:
-- 1. ONLY rows where SyncId IS NULL are assigned a GUID.
-- 2. Existing non-null SyncIds are NEVER modified.
-- 3. Generates RFC 4122 v4 GUIDs natively inside SQL Server engine.
-- 4. Re-run safe (idempotent).
-- ==============================================================================

BEGIN TRANSACTION;

-- 1. Daily
UPDATE [dbo].[Daily]
SET [SyncId] = NEWID()
WHERE [SyncId] IS NULL;

-- 2. Form
UPDATE [dbo].[Form]
SET [SyncId] = NEWID()
WHERE [SyncId] IS NULL;

-- 3. FormDetails
UPDATE [dbo].[FormDetails]
SET [SyncId] = NEWID()
WHERE [SyncId] IS NULL;

-- 4. Departments
UPDATE [dbo].[Departments]
SET [SyncId] = NEWID()
WHERE [SyncId] IS NULL;

-- 5. Employees
UPDATE [dbo].[Employees]
SET [SyncId] = NEWID()
WHERE [SyncId] IS NULL;

-- 6. EmployeeBank
UPDATE [dbo].[EmployeeBank]
SET [SyncId] = NEWID()
WHERE [SyncId] IS NULL;

-- 7. EmployeeNetPays
UPDATE [dbo].[EmployeeNetPays]
SET [SyncId] = NEWID()
WHERE [SyncId] IS NULL;

-- 8. EmployeeWatchLists
UPDATE [dbo].[EmployeeWatchLists]
SET [SyncId] = NEWID()
WHERE [SyncId] IS NULL;

-- 9. DailyReference
UPDATE [dbo].[DailyReference]
SET [SyncId] = NEWID()
WHERE [SyncId] IS NULL;

-- 10. FormRefernce
UPDATE [dbo].[FormRefernce]
SET [SyncId] = NEWID()
WHERE [SyncId] IS NULL;

-- 11. EmployeeRefernce
UPDATE [dbo].[EmployeeRefernce]
SET [SyncId] = NEWID()
WHERE [SyncId] IS NULL;

COMMIT TRANSACTION;
GO
