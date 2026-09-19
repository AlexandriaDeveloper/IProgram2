-- ==============================================================================
-- PHASE 4 — SLICE 4.1B: Controlled Rollout Tooling
-- Stage E: Azure Sync Metadata Schema Creation
-- Applied to: IProgramDb2026, IProgramDb2027
-- 
-- Creates Azure-only metadata tables in schema [sync]:
-- 1. sync.ProcessedOperations
-- 2. sync.ServerChangeFeed
-- 3. sync.ServerState
-- 4. sync.Tombstones
-- 5. sync.__EFMigrationsHistory_AzureSync (dedicated history table)
-- ==============================================================================

IF OBJECT_ID(N'[sync].[__EFMigrationsHistory_AzureSync]') IS NULL
BEGIN
    IF SCHEMA_ID(N'sync') IS NULL EXEC(N'CREATE SCHEMA [sync];');
    CREATE TABLE [sync].[__EFMigrationsHistory_AzureSync] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory_AzureSync] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [sync].[__EFMigrationsHistory_AzureSync]
    WHERE [MigrationId] = N'20260919155715_InitialAzureSyncSchema'
)
BEGIN
    IF SCHEMA_ID(N'sync') IS NULL EXEC(N'CREATE SCHEMA [sync];');
END;
GO

IF NOT EXISTS (
    SELECT * FROM [sync].[__EFMigrationsHistory_AzureSync]
    WHERE [MigrationId] = N'20260919155715_InitialAzureSyncSchema'
)
BEGIN
    CREATE TABLE [sync].[ProcessedOperations] (
        [DatabaseId] nvarchar(32) NOT NULL,
        [ClientOperationId] uniqueidentifier NOT NULL,
        [DeviceId] uniqueidentifier NOT NULL,
        [CommandName] nvarchar(100) NOT NULL,
        [RequestHash] varchar(64) NOT NULL,
        [EntityType] nvarchar(50) NOT NULL,
        [EntitySyncId] uniqueidentifier NOT NULL,
        [ProcessedAtUtc] datetime2 NOT NULL,
        [ResultStatus] nvarchar(20) NOT NULL,
        [ResponseJson] nvarchar(max) NULL,
        CONSTRAINT [PK_ProcessedOperations] PRIMARY KEY ([DatabaseId], [ClientOperationId])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [sync].[__EFMigrationsHistory_AzureSync]
    WHERE [MigrationId] = N'20260919155715_InitialAzureSyncSchema'
)
BEGIN
    CREATE TABLE [sync].[ServerChangeFeed] (
        [FeedId] bigint NOT NULL IDENTITY,
        [ServerVersion] bigint NOT NULL,
        [DatabaseId] nvarchar(32) NOT NULL,
        [EntityType] nvarchar(50) NOT NULL,
        [EntitySyncId] uniqueidentifier NOT NULL,
        [OperationType] nvarchar(20) NOT NULL,
        [OriginDeviceId] uniqueidentifier NOT NULL,
        [TimestampUtc] datetime2 NOT NULL,
        CONSTRAINT [PK_ServerChangeFeed] PRIMARY KEY ([FeedId])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [sync].[__EFMigrationsHistory_AzureSync]
    WHERE [MigrationId] = N'20260919155715_InitialAzureSyncSchema'
)
BEGIN
    CREATE TABLE [sync].[ServerState] (
        [DatabaseId] nvarchar(32) NOT NULL,
        [CurrentVersion] bigint NOT NULL,
        [LastUpdatedUtc] datetime2 NOT NULL,
        CONSTRAINT [PK_ServerState] PRIMARY KEY ([DatabaseId])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [sync].[__EFMigrationsHistory_AzureSync]
    WHERE [MigrationId] = N'20260919155715_InitialAzureSyncSchema'
)
BEGIN
    CREATE TABLE [sync].[Tombstones] (
        [DatabaseId] nvarchar(32) NOT NULL,
        [EntityType] nvarchar(50) NOT NULL,
        [EntitySyncId] uniqueidentifier NOT NULL,
        [NaturalKey] nvarchar(50) NULL,
        [ServerVersion] bigint NOT NULL,
        [DeletedAtUtc] datetime2 NOT NULL,
        CONSTRAINT [PK_Tombstones] PRIMARY KEY ([DatabaseId], [EntityType], [EntitySyncId])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [sync].[__EFMigrationsHistory_AzureSync]
    WHERE [MigrationId] = N'20260919155715_InitialAzureSyncSchema'
)
BEGIN
    CREATE INDEX [IX_ServerChangeFeed_Pull] ON [sync].[ServerChangeFeed] ([DatabaseId], [ServerVersion]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [sync].[__EFMigrationsHistory_AzureSync]
    WHERE [MigrationId] = N'20260919155715_InitialAzureSyncSchema'
)
BEGIN
    CREATE INDEX [IX_Tombstones_Pull] ON [sync].[Tombstones] ([DatabaseId], [ServerVersion]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [sync].[__EFMigrationsHistory_AzureSync]
    WHERE [MigrationId] = N'20260919155715_InitialAzureSyncSchema'
)
BEGIN
    INSERT INTO [sync].[__EFMigrationsHistory_AzureSync] ([MigrationId], [ProductVersion])
    VALUES (N'20260919155715_InitialAzureSyncSchema', N'8.0.11');
END;
GO

COMMIT;
GO
