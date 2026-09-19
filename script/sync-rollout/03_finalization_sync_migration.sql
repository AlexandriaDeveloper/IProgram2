-- ==============================================================================
-- PHASE 4 — SLICE 4.1B: Controlled Rollout Tooling
-- Stage D: Finalization Migration (Enforce NOT NULL and Create UNIQUE Indexes)
-- Applied to: IProgramDb2026, IProgramDb2027
-- 
-- Rules:
-- 1. Drops staged filtered indexes.
-- 2. Alters SyncId column to NOT NULL across all 11 tables.
-- 3. Drops any default constraints if present (strictly 0 DB defaults generated).
-- 4. Creates standard UNIQUE NONCLUSTERED INDEX on SyncId.
-- ==============================================================================

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260919163939_FinalizeSyncIdNotNull'
)
BEGIN
    DROP INDEX [IX_FormRefernce_SyncId] ON [FormRefernce];
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260919163939_FinalizeSyncIdNotNull'
)
BEGIN
    DROP INDEX [IX_FormDetails_SyncId] ON [FormDetails];
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260919163939_FinalizeSyncIdNotNull'
)
BEGIN
    DROP INDEX [IX_Form_SyncId] ON [Form];
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260919163939_FinalizeSyncIdNotNull'
)
BEGIN
    DROP INDEX [IX_EmployeeWatchLists_SyncId] ON [EmployeeWatchLists];
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260919163939_FinalizeSyncIdNotNull'
)
BEGIN
    DROP INDEX [IX_Employees_SyncId] ON [Employees];
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260919163939_FinalizeSyncIdNotNull'
)
BEGIN
    DROP INDEX [IX_EmployeeRefernce_SyncId] ON [EmployeeRefernce];
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260919163939_FinalizeSyncIdNotNull'
)
BEGIN
    DROP INDEX [IX_EmployeeNetPays_SyncId] ON [EmployeeNetPays];
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260919163939_FinalizeSyncIdNotNull'
)
BEGIN
    DROP INDEX [IX_EmployeeBank_SyncId] ON [EmployeeBank];
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260919163939_FinalizeSyncIdNotNull'
)
BEGIN
    DROP INDEX [IX_Departments_SyncId] ON [Departments];
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260919163939_FinalizeSyncIdNotNull'
)
BEGIN
    DROP INDEX [IX_DailyReference_SyncId] ON [DailyReference];
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260919163939_FinalizeSyncIdNotNull'
)
BEGIN
    DROP INDEX [IX_Daily_SyncId] ON [Daily];
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260919163939_FinalizeSyncIdNotNull'
)
BEGIN
    DECLARE @var0 sysname;
    SELECT @var0 = [d].[name]
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[FormRefernce]') AND [c].[name] = N'SyncId');
    IF @var0 IS NOT NULL EXEC(N'ALTER TABLE [FormRefernce] DROP CONSTRAINT [' + @var0 + '];');
    ALTER TABLE [FormRefernce] ALTER COLUMN [SyncId] uniqueidentifier NOT NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260919163939_FinalizeSyncIdNotNull'
)
BEGIN
    DECLARE @var1 sysname;
    SELECT @var1 = [d].[name]
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[FormDetails]') AND [c].[name] = N'SyncId');
    IF @var1 IS NOT NULL EXEC(N'ALTER TABLE [FormDetails] DROP CONSTRAINT [' + @var1 + '];');
    ALTER TABLE [FormDetails] ALTER COLUMN [SyncId] uniqueidentifier NOT NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260919163939_FinalizeSyncIdNotNull'
)
BEGIN
    DECLARE @var2 sysname;
    SELECT @var2 = [d].[name]
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[Form]') AND [c].[name] = N'SyncId');
    IF @var2 IS NOT NULL EXEC(N'ALTER TABLE [Form] DROP CONSTRAINT [' + @var2 + '];');
    ALTER TABLE [Form] ALTER COLUMN [SyncId] uniqueidentifier NOT NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260919163939_FinalizeSyncIdNotNull'
)
BEGIN
    DECLARE @var3 sysname;
    SELECT @var3 = [d].[name]
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[EmployeeWatchLists]') AND [c].[name] = N'SyncId');
    IF @var3 IS NOT NULL EXEC(N'ALTER TABLE [EmployeeWatchLists] DROP CONSTRAINT [' + @var3 + '];');
    ALTER TABLE [EmployeeWatchLists] ALTER COLUMN [SyncId] uniqueidentifier NOT NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260919163939_FinalizeSyncIdNotNull'
)
BEGIN
    DECLARE @var4 sysname;
    SELECT @var4 = [d].[name]
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[Employees]') AND [c].[name] = N'SyncId');
    IF @var4 IS NOT NULL EXEC(N'ALTER TABLE [Employees] DROP CONSTRAINT [' + @var4 + '];');
    ALTER TABLE [Employees] ALTER COLUMN [SyncId] uniqueidentifier NOT NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260919163939_FinalizeSyncIdNotNull'
)
BEGIN
    DECLARE @var5 sysname;
    SELECT @var5 = [d].[name]
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[EmployeeRefernce]') AND [c].[name] = N'SyncId');
    IF @var5 IS NOT NULL EXEC(N'ALTER TABLE [EmployeeRefernce] DROP CONSTRAINT [' + @var5 + '];');
    ALTER TABLE [EmployeeRefernce] ALTER COLUMN [SyncId] uniqueidentifier NOT NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260919163939_FinalizeSyncIdNotNull'
)
BEGIN
    DECLARE @var6 sysname;
    SELECT @var6 = [d].[name]
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[EmployeeNetPays]') AND [c].[name] = N'SyncId');
    IF @var6 IS NOT NULL EXEC(N'ALTER TABLE [EmployeeNetPays] DROP CONSTRAINT [' + @var6 + '];');
    ALTER TABLE [EmployeeNetPays] ALTER COLUMN [SyncId] uniqueidentifier NOT NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260919163939_FinalizeSyncIdNotNull'
)
BEGIN
    DECLARE @var7 sysname;
    SELECT @var7 = [d].[name]
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[EmployeeBank]') AND [c].[name] = N'SyncId');
    IF @var7 IS NOT NULL EXEC(N'ALTER TABLE [EmployeeBank] DROP CONSTRAINT [' + @var7 + '];');
    ALTER TABLE [EmployeeBank] ALTER COLUMN [SyncId] uniqueidentifier NOT NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260919163939_FinalizeSyncIdNotNull'
)
BEGIN
    DECLARE @var8 sysname;
    SELECT @var8 = [d].[name]
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[Departments]') AND [c].[name] = N'SyncId');
    IF @var8 IS NOT NULL EXEC(N'ALTER TABLE [Departments] DROP CONSTRAINT [' + @var8 + '];');
    ALTER TABLE [Departments] ALTER COLUMN [SyncId] uniqueidentifier NOT NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260919163939_FinalizeSyncIdNotNull'
)
BEGIN
    DECLARE @var9 sysname;
    SELECT @var9 = [d].[name]
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[DailyReference]') AND [c].[name] = N'SyncId');
    IF @var9 IS NOT NULL EXEC(N'ALTER TABLE [DailyReference] DROP CONSTRAINT [' + @var9 + '];');
    ALTER TABLE [DailyReference] ALTER COLUMN [SyncId] uniqueidentifier NOT NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260919163939_FinalizeSyncIdNotNull'
)
BEGIN
    DECLARE @var10 sysname;
    SELECT @var10 = [d].[name]
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[Daily]') AND [c].[name] = N'SyncId');
    IF @var10 IS NOT NULL EXEC(N'ALTER TABLE [Daily] DROP CONSTRAINT [' + @var10 + '];');
    ALTER TABLE [Daily] ALTER COLUMN [SyncId] uniqueidentifier NOT NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260919163939_FinalizeSyncIdNotNull'
)
BEGIN
    CREATE UNIQUE INDEX [IX_FormRefernce_SyncId] ON [FormRefernce] ([SyncId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260919163939_FinalizeSyncIdNotNull'
)
BEGIN
    CREATE UNIQUE INDEX [IX_FormDetails_SyncId] ON [FormDetails] ([SyncId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260919163939_FinalizeSyncIdNotNull'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Form_SyncId] ON [Form] ([SyncId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260919163939_FinalizeSyncIdNotNull'
)
BEGIN
    CREATE UNIQUE INDEX [IX_EmployeeWatchLists_SyncId] ON [EmployeeWatchLists] ([SyncId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260919163939_FinalizeSyncIdNotNull'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Employees_SyncId] ON [Employees] ([SyncId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260919163939_FinalizeSyncIdNotNull'
)
BEGIN
    CREATE UNIQUE INDEX [IX_EmployeeRefernce_SyncId] ON [EmployeeRefernce] ([SyncId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260919163939_FinalizeSyncIdNotNull'
)
BEGIN
    CREATE UNIQUE INDEX [IX_EmployeeNetPays_SyncId] ON [EmployeeNetPays] ([SyncId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260919163939_FinalizeSyncIdNotNull'
)
BEGIN
    CREATE UNIQUE INDEX [IX_EmployeeBank_SyncId] ON [EmployeeBank] ([SyncId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260919163939_FinalizeSyncIdNotNull'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Departments_SyncId] ON [Departments] ([SyncId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260919163939_FinalizeSyncIdNotNull'
)
BEGIN
    CREATE UNIQUE INDEX [IX_DailyReference_SyncId] ON [DailyReference] ([SyncId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260919163939_FinalizeSyncIdNotNull'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Daily_SyncId] ON [Daily] ([SyncId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260919163939_FinalizeSyncIdNotNull'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260919163939_FinalizeSyncIdNotNull', N'8.0.11');
END;
GO

COMMIT;
GO
