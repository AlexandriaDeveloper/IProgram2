IF OBJECT_ID(N'[sync].[__EFMigrationsHistory_LocalSync]') IS NULL
BEGIN
    IF SCHEMA_ID(N'sync') IS NULL EXEC(N'CREATE SCHEMA [sync];');
    CREATE TABLE [sync].[__EFMigrationsHistory_LocalSync] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory_LocalSync] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
GO

IF SCHEMA_ID(N'sync') IS NULL EXEC(N'CREATE SCHEMA [sync];');
GO

IF OBJECT_ID(N'[sync].[BootstrapManifest]') IS NULL
BEGIN
    CREATE TABLE [sync].[BootstrapManifest] (
        [Id] int NOT NULL IDENTITY,
        [DatabaseId] nvarchar(32) NOT NULL,
        [BootstrapTimestampUtc] datetime2 NOT NULL,
        [AzureServerSource] nvarchar(255) NULL,
        [TargetLocalEngine] nvarchar(100) NULL,
        [MigrationHistoryHash] nvarchar(64) NULL,
        [TableCheckJson] nvarchar(max) NULL,
        [IdentityCheckJson] nvarchar(max) NULL,
        [Status] nvarchar(20) NOT NULL,
        [IsWriteAllowed] bit NOT NULL,
        CONSTRAINT [PK_BootstrapManifest] PRIMARY KEY ([Id])
    );
END;
GO

IF OBJECT_ID(N'[sync].[LocalOutbox]') IS NULL
BEGIN
    CREATE TABLE [sync].[LocalOutbox] (
        [ClientOperationId] uniqueidentifier NOT NULL,
        [DatabaseId] nvarchar(32) NOT NULL,
        [AggregateType] nvarchar(50) NOT NULL,
        [CommandName] nvarchar(100) NOT NULL,
        [EntitySyncId] uniqueidentifier NOT NULL,
        [PayloadJson] nvarchar(max) NOT NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [Status] nvarchar(20) NOT NULL,
        [RetryCount] int NOT NULL,
        [LastError] nvarchar(max) NULL,
        [CompletedAtUtc] datetime2 NULL,
        [LockedUntilUtc] datetime2 NULL,
        [LockToken] uniqueidentifier NULL,
        CONSTRAINT [PK_LocalOutbox] PRIMARY KEY ([ClientOperationId])
    );
    CREATE INDEX [IX_LocalOutbox_Queue] ON [sync].[LocalOutbox] ([DatabaseId], [Status], [CreatedAtUtc]);
END;
GO

IF OBJECT_ID(N'[sync].[LocalState]') IS NULL
BEGIN
    CREATE TABLE [sync].[LocalState] (
        [DatabaseId] nvarchar(32) NOT NULL,
        [DeviceId] uniqueidentifier NOT NULL,
        [DeviceName] nvarchar(100) NOT NULL,
        [LastSuccessfulPushUtc] datetime2 NULL,
        [LastSuccessfulPullUtc] datetime2 NULL,
        [LastServerVersion] bigint NOT NULL,
        [ActiveLeaseToken] uniqueidentifier NULL,
        [LeaseExpiresAtUtc] datetime2 NULL,
        [LastSyncError] nvarchar(max) NULL,
        [LastSyncAttemptUtc] datetime2 NULL,
        CONSTRAINT [PK_LocalState] PRIMARY KEY ([DatabaseId])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [sync].[__EFMigrationsHistory_LocalSync] 
    WHERE [MigrationId] = N'20260919155658_InitialLocalSyncSchema'
)
BEGIN
    INSERT INTO [sync].[__EFMigrationsHistory_LocalSync] ([MigrationId], [ProductVersion])
    VALUES (N'20260919155658_InitialLocalSyncSchema', N'8.0.11');
END;
GO

COMMIT;
GO
