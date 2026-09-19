-- ==============================================================================
-- PHASE 4 — SLICE 4.1B: Controlled Rollout Tooling
-- Stage A: Staged Additive Migration (Nullable SyncId + Filtered Unique Index)
-- Applied to: IProgramDb2026, IProgramDb2027
-- ==============================================================================

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260919161502_AddSyncIdToBusinessEntities'
)
BEGIN
    ALTER TABLE [FormRefernce] ADD [SyncId] uniqueidentifier NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260919161502_AddSyncIdToBusinessEntities'
)
BEGIN
    ALTER TABLE [FormDetails] ADD [SyncId] uniqueidentifier NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260919161502_AddSyncIdToBusinessEntities'
)
BEGIN
    ALTER TABLE [Form] ADD [SyncId] uniqueidentifier NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260919161502_AddSyncIdToBusinessEntities'
)
BEGIN
    ALTER TABLE [EmployeeWatchLists] ADD [SyncId] uniqueidentifier NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260919161502_AddSyncIdToBusinessEntities'
)
BEGIN
    ALTER TABLE [Employees] ADD [SyncId] uniqueidentifier NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260919161502_AddSyncIdToBusinessEntities'
)
BEGIN
    ALTER TABLE [EmployeeRefernce] ADD [SyncId] uniqueidentifier NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260919161502_AddSyncIdToBusinessEntities'
)
BEGIN
    ALTER TABLE [EmployeeNetPays] ADD [SyncId] uniqueidentifier NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260919161502_AddSyncIdToBusinessEntities'
)
BEGIN
    ALTER TABLE [EmployeeBank] ADD [SyncId] uniqueidentifier NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260919161502_AddSyncIdToBusinessEntities'
)
BEGIN
    ALTER TABLE [Departments] ADD [SyncId] uniqueidentifier NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260919161502_AddSyncIdToBusinessEntities'
)
BEGIN
    ALTER TABLE [DailyReference] ADD [SyncId] uniqueidentifier NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260919161502_AddSyncIdToBusinessEntities'
)
BEGIN
    ALTER TABLE [Daily] ADD [SyncId] uniqueidentifier NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260919161502_AddSyncIdToBusinessEntities'
)
BEGIN
    CREATE UNIQUE INDEX [IX_FormRefernce_SyncId] ON [FormRefernce] ([SyncId]) WHERE [SyncId] IS NOT NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260919161502_AddSyncIdToBusinessEntities'
)
BEGIN
    CREATE UNIQUE INDEX [IX_FormDetails_SyncId] ON [FormDetails] ([SyncId]) WHERE [SyncId] IS NOT NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260919161502_AddSyncIdToBusinessEntities'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Form_SyncId] ON [Form] ([SyncId]) WHERE [SyncId] IS NOT NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260919161502_AddSyncIdToBusinessEntities'
)
BEGIN
    CREATE UNIQUE INDEX [IX_EmployeeWatchLists_SyncId] ON [EmployeeWatchLists] ([SyncId]) WHERE [SyncId] IS NOT NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260919161502_AddSyncIdToBusinessEntities'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Employees_SyncId] ON [Employees] ([SyncId]) WHERE [SyncId] IS NOT NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260919161502_AddSyncIdToBusinessEntities'
)
BEGIN
    CREATE UNIQUE INDEX [IX_EmployeeRefernce_SyncId] ON [EmployeeRefernce] ([SyncId]) WHERE [SyncId] IS NOT NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260919161502_AddSyncIdToBusinessEntities'
)
BEGIN
    CREATE UNIQUE INDEX [IX_EmployeeNetPays_SyncId] ON [EmployeeNetPays] ([SyncId]) WHERE [SyncId] IS NOT NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260919161502_AddSyncIdToBusinessEntities'
)
BEGIN
    CREATE UNIQUE INDEX [IX_EmployeeBank_SyncId] ON [EmployeeBank] ([SyncId]) WHERE [SyncId] IS NOT NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260919161502_AddSyncIdToBusinessEntities'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Departments_SyncId] ON [Departments] ([SyncId]) WHERE [SyncId] IS NOT NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260919161502_AddSyncIdToBusinessEntities'
)
BEGIN
    CREATE UNIQUE INDEX [IX_DailyReference_SyncId] ON [DailyReference] ([SyncId]) WHERE [SyncId] IS NOT NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260919161502_AddSyncIdToBusinessEntities'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Daily_SyncId] ON [Daily] ([SyncId]) WHERE [SyncId] IS NOT NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260919161502_AddSyncIdToBusinessEntities'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260919161502_AddSyncIdToBusinessEntities', N'8.0.11');
END;
GO

COMMIT;
GO
