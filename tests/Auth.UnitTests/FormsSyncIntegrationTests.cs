#nullable enable
using System;
using System.Collections.Generic;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Auth.Infrastructure.Sync.Pull;
using Auth.Infrastructure.Sync.Push;
using Core.Exceptions;
using Core.Interfaces;
using Core.Models.Sync;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Auth.UnitTests
{
    [CollectionDefinition("FormsSyncIntegration", DisableParallelization = true)]
    public class FormsSyncIntegrationCollection
    {
    }

    [Trait("Category", "LocalDbRequired")]
    [Collection("FormsSyncIntegration")]
    public class FormsSyncIntegrationTests
    {
        private const string MasterConnStr = "Server=localhost;Database=master;Integrated Security=True;TrustServerCertificate=True;";

        private sealed class FormsSyncTestContext : IAsyncDisposable
        {
            public string RemoteDbName { get; }
            public string LocalDbName { get; }
            public string RemoteConnStr { get; }
            public string LocalConnStr { get; }
            public string CanonicalDbId { get; } = "2026";
            public Guid DeviceId { get; } = Guid.NewGuid();

            private FormsSyncTestContext(string remoteDbName, string localDbName)
            {
                RemoteDbName = remoteDbName;
                LocalDbName = localDbName;
                RemoteConnStr = $"Server=localhost;Database={remoteDbName};Integrated Security=True;TrustServerCertificate=True;";
                LocalConnStr = $"Server=localhost;Database={localDbName};Integrated Security=True;TrustServerCertificate=True;";
            }

            public static async Task<FormsSyncTestContext> CreateAsync()
            {
                var suffix = Guid.NewGuid().ToString("N")[..8];
                var remoteName = $"TestRemoteForms_{suffix}";
                var localName = "IProgramLocalDb2026_Test";

                var ctx = new FormsSyncTestContext(remoteName, localName);
                await ctx.InitializeAsync();
                return ctx;
            }

            private async Task InitializeAsync()
            {
                await using (var masterConn = new SqlConnection(MasterConnStr))
                {
                    await masterConn.OpenAsync();
                    await using var createCmd = masterConn.CreateCommand();
                    createCmd.CommandText = $@"
                        CREATE DATABASE [{RemoteDbName}];
                        ALTER DATABASE [{RemoteDbName}] SET COMPATIBILITY_LEVEL = 120;
                        IF DB_ID('{LocalDbName}') IS NULL
                        BEGIN
                            CREATE DATABASE [{LocalDbName}];
                            ALTER DATABASE [{LocalDbName}] SET COMPATIBILITY_LEVEL = 120;
                        END;";
                    await createCmd.ExecuteNonQueryAsync();
                }

                // Initialize Remote
                await using (var remoteConn = new SqlConnection(RemoteConnStr))
                {
                    await remoteConn.OpenAsync();
                    await using var cmd = remoteConn.CreateCommand();
                    cmd.CommandText = @"
                        IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'sync')
                            EXEC('CREATE SCHEMA [sync]');

                        CREATE TABLE [dbo].[Employee] (
                            [Id] NVARCHAR(14) NOT NULL PRIMARY KEY,
                            [Name] NVARCHAR(100) NOT NULL
                        );
                        INSERT INTO [dbo].[Employee] (Id, Name) VALUES ('12345678901234', 'Integration Test Employee');

                        CREATE TABLE [dbo].[Daily] (
                            [Id] INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                            [Name] NVARCHAR(100) NOT NULL,
                            [DailyDate] DATETIME2 NOT NULL,
                            [Closed] BIT NOT NULL DEFAULT(0),
                            [CreatedBy] NVARCHAR(100) NULL,
                            [CreatedAt] DATETIME2 NOT NULL,
                            [UpdatedBy] NVARCHAR(100) NULL,
                            [UpdatedAt] DATETIME2 NULL,
                            [DeactivatedBy] NVARCHAR(100) NULL,
                            [DeactivatedAt] DATETIME2 NULL,
                            [IsActive] BIT NOT NULL DEFAULT(1),
                            [SyncId] UNIQUEIDENTIFIER NOT NULL
                        );
                        CREATE UNIQUE INDEX [IX_Daily_SyncId] ON [dbo].[Daily]([SyncId]);

                        CREATE TABLE [dbo].[Form] (
                            [Id] INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                            [SyncId] UNIQUEIDENTIFIER NOT NULL,
                            [DailyId] INT NULL,
                            [Name] NVARCHAR(200) NOT NULL,
                            [Description] NVARCHAR(MAX) NULL,
                            [Index] INT NOT NULL,
                            [CreatedBy] NVARCHAR(100) NULL,
                            [CreatedAt] DATETIME2 NOT NULL,
                            [UpdatedBy] NVARCHAR(100) NULL,
                            [UpdatedAt] DATETIME2 NULL,
                            [DeactivatedBy] NVARCHAR(100) NULL,
                            [DeactivatedAt] DATETIME2 NULL,
                            [IsActive] BIT NOT NULL DEFAULT(1),
                            CONSTRAINT [FK_Form_Daily] FOREIGN KEY ([DailyId]) REFERENCES [dbo].[Daily]([Id])
                        );
                        CREATE UNIQUE INDEX [IX_Form_SyncId] ON [dbo].[Form]([SyncId]);

                        CREATE TABLE [dbo].[FormDetails] (
                            [Id] INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                            [SyncId] UNIQUEIDENTIFIER NOT NULL,
                            [FormId] INT NOT NULL,
                            [EmployeeId] NVARCHAR(14) NOT NULL,
                            [Amount] FLOAT NOT NULL,
                            [OrderNum] INT NOT NULL,
                            [IsReviewed] BIT NOT NULL DEFAULT(0),
                            [IsReviewedBy] NVARCHAR(100) NULL,
                            [ReviewedAt] DATETIME2 NULL,
                            [IsSummaryReviewed] BIT NOT NULL DEFAULT(0),
                            [IsSummaryReviewedBy] NVARCHAR(100) NULL,
                            [SummaryReviewedAt] DATETIME2 NULL,
                            [ReviewComments] NVARCHAR(MAX) NULL,
                            [SummaryComments] NVARCHAR(MAX) NULL,
                            [SummaryReviewMethod] NVARCHAR(50) NULL,
                            [CreatedBy] NVARCHAR(100) NULL,
                            [CreatedAt] DATETIME2 NOT NULL,
                            [UpdatedBy] NVARCHAR(100) NULL,
                            [UpdatedAt] DATETIME2 NULL,
                            [DeactivatedBy] NVARCHAR(100) NULL,
                            [DeactivatedAt] DATETIME2 NULL,
                            [IsActive] BIT NOT NULL DEFAULT(1),
                            CONSTRAINT [FK_FormDetails_Form] FOREIGN KEY ([FormId]) REFERENCES [dbo].[Form]([Id])
                        );
                        CREATE UNIQUE INDEX [IX_FormDetails_SyncId] ON [dbo].[FormDetails]([SyncId]);

                        CREATE TABLE [dbo].[FormRefernce] (
                            [Id] INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                            [SyncId] UNIQUEIDENTIFIER NOT NULL,
                            [FormId] INT NOT NULL,
                            [ReferencePath] NVARCHAR(MAX) NOT NULL,
                            [CreatedBy] NVARCHAR(100) NULL,
                            [CreatedAt] DATETIME2 NOT NULL,
                            [UpdatedBy] NVARCHAR(100) NULL,
                            [UpdatedAt] DATETIME2 NULL,
                            [DeactivatedBy] NVARCHAR(100) NULL,
                            [DeactivatedAt] DATETIME2 NULL,
                            [IsActive] BIT NOT NULL DEFAULT(1),
                            CONSTRAINT [FK_FormRefernce_Form] FOREIGN KEY ([FormId]) REFERENCES [dbo].[Form]([Id])
                        );
                        CREATE UNIQUE INDEX [IX_FormRefernce_SyncId] ON [dbo].[FormRefernce]([SyncId]);

                        CREATE TABLE [sync].[ServerState] (
                            [DatabaseId] NVARCHAR(32) NOT NULL PRIMARY KEY,
                            [CurrentVersion] BIGINT NOT NULL,
                            [LastUpdatedUtc] DATETIME2 NOT NULL
                        );

                        CREATE TABLE [sync].[ServerChangeFeed] (
                            [FeedId] BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                            [ServerVersion] BIGINT NOT NULL,
                            [DatabaseId] NVARCHAR(32) NOT NULL,
                            [EntityType] NVARCHAR(50) NOT NULL,
                            [EntitySyncId] UNIQUEIDENTIFIER NOT NULL,
                            [OperationType] NVARCHAR(20) NOT NULL,
                            [OriginDeviceId] UNIQUEIDENTIFIER NOT NULL,
                            [TimestampUtc] DATETIME2 NOT NULL
                        );
                        CREATE INDEX [IX_ServerChangeFeed_Pull] ON [sync].[ServerChangeFeed]([DatabaseId], [ServerVersion]);

                        CREATE TABLE [sync].[Tombstones] (
                            [DatabaseId] NVARCHAR(32) NOT NULL,
                            [EntityType] NVARCHAR(50) NOT NULL,
                            [EntitySyncId] UNIQUEIDENTIFIER NOT NULL,
                            [NaturalKey] NVARCHAR(50) NULL,
                            [ServerVersion] BIGINT NOT NULL,
                            [DeletedAtUtc] DATETIME2 NOT NULL,
                            CONSTRAINT [PK_Tombstones] PRIMARY KEY ([DatabaseId], [EntityType], [EntitySyncId])
                        );

                        CREATE TABLE [sync].[ProcessedOperations] (
                            [Id] BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                            [DatabaseId] NVARCHAR(32) NOT NULL,
                            [ClientOperationId] UNIQUEIDENTIFIER NOT NULL,
                            [DeviceId] UNIQUEIDENTIFIER NOT NULL,
                            [CommandName] NVARCHAR(100) NOT NULL,
                            [RequestHash] NVARCHAR(128) NOT NULL,
                            [EntityType] NVARCHAR(50) NOT NULL,
                            [EntitySyncId] UNIQUEIDENTIFIER NOT NULL,
                            [ProcessedAtUtc] DATETIME2 NOT NULL,
                            [ResultStatus] NVARCHAR(20) NOT NULL,
                            [ResponseJson] NVARCHAR(MAX) NOT NULL
                        );
                        CREATE UNIQUE INDEX [IX_ProcessedOperations_Idempotency] ON [sync].[ProcessedOperations]([DatabaseId], [ClientOperationId]);

                        INSERT INTO [sync].[ServerState] (DatabaseId, CurrentVersion, LastUpdatedUtc)
                        VALUES ('2026', 0, SYSUTCDATETIME());
                    ";
                    await cmd.ExecuteNonQueryAsync();
                }

                // Initialize Local
                await using (var localConn = new SqlConnection(LocalConnStr))
                {
                    await localConn.OpenAsync();
                    await using var cmd = localConn.CreateCommand();
                    cmd.CommandText = $@"
                        IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'sync')
                            EXEC('CREATE SCHEMA [sync]');

                        IF OBJECT_ID('[dbo].[Employee]', 'U') IS NULL
                        BEGIN
                            CREATE TABLE [dbo].[Employee] (
                                [Id] NVARCHAR(14) NOT NULL PRIMARY KEY,
                                [Name] NVARCHAR(100) NOT NULL
                            );
                        END;
                        IF NOT EXISTS (SELECT 1 FROM [dbo].[Employee] WHERE [Id] = '12345678901234')
                        BEGIN
                            INSERT INTO [dbo].[Employee] (Id, Name) VALUES ('12345678901234', 'Integration Test Employee');
                        END;

                        IF OBJECT_ID('[dbo].[FormRefernce]', 'U') IS NOT NULL DROP TABLE [dbo].[FormRefernce];
                        IF OBJECT_ID('[dbo].[FormDetails]', 'U') IS NOT NULL DROP TABLE [dbo].[FormDetails];
                        IF OBJECT_ID('[dbo].[Form]', 'U') IS NOT NULL DROP TABLE [dbo].[Form];
                        IF OBJECT_ID('[dbo].[Daily]', 'U') IS NOT NULL DROP TABLE [dbo].[Daily];

                        CREATE TABLE [dbo].[Daily] (
                            [Id] INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                            [Name] NVARCHAR(100) NOT NULL,
                            [DailyDate] DATETIME2 NOT NULL,
                            [Closed] BIT NOT NULL DEFAULT(0),
                            [CreatedBy] NVARCHAR(100) NULL,
                            [CreatedAt] DATETIME2 NOT NULL,
                            [UpdatedBy] NVARCHAR(100) NULL,
                            [UpdatedAt] DATETIME2 NULL,
                            [DeactivatedBy] NVARCHAR(100) NULL,
                            [DeactivatedAt] DATETIME2 NULL,
                            [IsActive] BIT NOT NULL DEFAULT(1),
                            [SyncId] UNIQUEIDENTIFIER NOT NULL
                        );
                        CREATE UNIQUE INDEX [IX_Daily_SyncId] ON [dbo].[Daily]([SyncId]);

                        CREATE TABLE [dbo].[Form] (
                            [Id] INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                            [SyncId] UNIQUEIDENTIFIER NOT NULL,
                            [DailyId] INT NULL,
                            [Name] NVARCHAR(200) NOT NULL,
                            [Description] NVARCHAR(MAX) NULL,
                            [Index] INT NOT NULL,
                            [CreatedBy] NVARCHAR(100) NULL,
                            [CreatedAt] DATETIME2 NOT NULL,
                            [UpdatedBy] NVARCHAR(100) NULL,
                            [UpdatedAt] DATETIME2 NULL,
                            [DeactivatedBy] NVARCHAR(100) NULL,
                            [DeactivatedAt] DATETIME2 NULL,
                            [IsActive] BIT NOT NULL DEFAULT(1),
                            CONSTRAINT [FK_Form_Daily] FOREIGN KEY ([DailyId]) REFERENCES [dbo].[Daily]([Id])
                        );
                        CREATE UNIQUE INDEX [IX_Form_SyncId] ON [dbo].[Form]([SyncId]);

                        CREATE TABLE [dbo].[FormDetails] (
                            [Id] INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                            [SyncId] UNIQUEIDENTIFIER NOT NULL,
                            [FormId] INT NOT NULL,
                            [EmployeeId] NVARCHAR(14) NOT NULL,
                            [Amount] FLOAT NOT NULL,
                            [OrderNum] INT NOT NULL,
                            [IsReviewed] BIT NOT NULL DEFAULT(0),
                            [IsReviewedBy] NVARCHAR(100) NULL,
                            [ReviewedAt] DATETIME2 NULL,
                            [IsSummaryReviewed] BIT NOT NULL DEFAULT(0),
                            [IsSummaryReviewedBy] NVARCHAR(100) NULL,
                            [SummaryReviewedAt] DATETIME2 NULL,
                            [ReviewComments] NVARCHAR(MAX) NULL,
                            [SummaryComments] NVARCHAR(MAX) NULL,
                            [SummaryReviewMethod] NVARCHAR(50) NULL,
                            [CreatedBy] NVARCHAR(100) NULL,
                            [CreatedAt] DATETIME2 NOT NULL,
                            [UpdatedBy] NVARCHAR(100) NULL,
                            [UpdatedAt] DATETIME2 NULL,
                            [DeactivatedBy] NVARCHAR(100) NULL,
                            [DeactivatedAt] DATETIME2 NULL,
                            [IsActive] BIT NOT NULL DEFAULT(1),
                            CONSTRAINT [FK_FormDetails_Form] FOREIGN KEY ([FormId]) REFERENCES [dbo].[Form]([Id])
                        );
                        CREATE UNIQUE INDEX [IX_FormDetails_SyncId] ON [dbo].[FormDetails]([SyncId]);

                        CREATE TABLE [dbo].[FormRefernce] (
                            [Id] INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                            [SyncId] UNIQUEIDENTIFIER NOT NULL,
                            [FormId] INT NOT NULL,
                            [ReferencePath] NVARCHAR(MAX) NOT NULL,
                            [CreatedBy] NVARCHAR(100) NULL,
                            [CreatedAt] DATETIME2 NOT NULL,
                            [UpdatedBy] NVARCHAR(100) NULL,
                            [UpdatedAt] DATETIME2 NULL,
                            [DeactivatedBy] NVARCHAR(100) NULL,
                            [DeactivatedAt] DATETIME2 NULL,
                            [IsActive] BIT NOT NULL DEFAULT(1),
                            CONSTRAINT [FK_FormRefernce_Form] FOREIGN KEY ([FormId]) REFERENCES [dbo].[Form]([Id])
                        );
                        CREATE UNIQUE INDEX [IX_FormRefernce_SyncId] ON [dbo].[FormRefernce]([SyncId]);

                        IF OBJECT_ID('[sync].[LocalState]', 'U') IS NULL
                        BEGIN
                            CREATE TABLE [sync].[LocalState] (
                                [DatabaseId] NVARCHAR(32) NOT NULL PRIMARY KEY,
                                [DeviceId] UNIQUEIDENTIFIER NOT NULL,
                                [DeviceName] NVARCHAR(100) NOT NULL,
                                [LastSuccessfulPushUtc] DATETIME2 NULL,
                                [LastSuccessfulPullUtc] DATETIME2 NULL,
                                [LastServerVersion] BIGINT NOT NULL,
                                [ActiveLeaseToken] UNIQUEIDENTIFIER NULL,
                                [LeaseExpiresAtUtc] DATETIME2 NULL,
                                [LastSyncError] NVARCHAR(MAX) NULL,
                                [LastSyncAttemptUtc] DATETIME2 NULL
                            );
                        END
                        ELSE
                        BEGIN
                            DELETE FROM [sync].[LocalState];
                        END;

                        IF OBJECT_ID('[sync].[LocalOutbox]', 'U') IS NULL
                        BEGIN
                            CREATE TABLE [sync].[LocalOutbox] (
                                [ClientOperationId] UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
                                [DatabaseId] NVARCHAR(32) NOT NULL,
                                [AggregateType] NVARCHAR(50) NOT NULL,
                                [CommandName] NVARCHAR(100) NOT NULL,
                                [EntitySyncId] UNIQUEIDENTIFIER NOT NULL,
                                [PayloadJson] NVARCHAR(MAX) NOT NULL,
                                [CreatedAtUtc] DATETIME2 NOT NULL,
                                [Status] NVARCHAR(20) NOT NULL,
                                [RetryCount] INT NOT NULL DEFAULT 0,
                                [LastError] NVARCHAR(MAX) NULL,
                                [CompletedAtUtc] DATETIME2 NULL,
                                [LockedUntilUtc] DATETIME2 NULL,
                                [LockToken] UNIQUEIDENTIFIER NULL
                            );
                        END
                        ELSE
                        BEGIN
                            DELETE FROM [sync].[LocalOutbox];
                        END;

                        INSERT INTO [sync].[LocalState] (DatabaseId, DeviceId, DeviceName, LastServerVersion)
                        VALUES ('2026', '{DeviceId}', 'FormsTestDevice', 0);
                    ";
                    await cmd.ExecuteNonQueryAsync();
                }
            }

            public async ValueTask DisposeAsync()
            {
                try
                {
                    await using (var localConn = new SqlConnection(LocalConnStr))
                    {
                        await localConn.OpenAsync();
                        await using var cleanCmd = localConn.CreateCommand();
                        cleanCmd.CommandText = @"
                            IF OBJECT_ID('[dbo].[FormRefernce]', 'U') IS NOT NULL DELETE FROM [dbo].[FormRefernce];
                            IF OBJECT_ID('[dbo].[FormDetails]', 'U') IS NOT NULL DELETE FROM [dbo].[FormDetails];
                            IF OBJECT_ID('[dbo].[Form]', 'U') IS NOT NULL DELETE FROM [dbo].[Form];
                            IF OBJECT_ID('[dbo].[Daily]', 'U') IS NOT NULL DELETE FROM [dbo].[Daily];
                            IF OBJECT_ID('[dbo].[Employee]', 'U') IS NOT NULL DELETE FROM [dbo].[Employee];
                            IF OBJECT_ID('[sync].[LocalOutbox]', 'U') IS NOT NULL DELETE FROM [sync].[LocalOutbox];
                            IF OBJECT_ID('[sync].[LocalState]', 'U') IS NOT NULL DELETE FROM [sync].[LocalState];";
                        await cleanCmd.ExecuteNonQueryAsync();
                    }

                    if (RemoteDbName.StartsWith("TestRemoteForms_"))
                    {
                        await using var masterConn = new SqlConnection(MasterConnStr);
                        await masterConn.OpenAsync();
                        await using var dropCmd = masterConn.CreateCommand();
                        dropCmd.CommandText = $@"
                            IF DB_ID('{RemoteDbName}') IS NOT NULL
                            BEGIN
                                ALTER DATABASE [{RemoteDbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                                DROP DATABASE [{RemoteDbName}];
                            END;";
                        await dropCmd.ExecuteNonQueryAsync();
                    }

                    SqlConnection.ClearAllPools();
                }
                catch
                {
                    // Best effort cleanup
                }
            }
        }

        private sealed class TestRemoteDatabaseConnectionFactory : IRemoteDatabaseConnectionFactory
        {
            private readonly string _remoteConnStr;
            public TestRemoteDatabaseConnectionFactory(string remoteConnStr) => _remoteConnStr = remoteConnStr;

            public async Task<System.Data.Common.DbConnection> CreateOpenConnectionAsync(string databaseId, CancellationToken cancellationToken)
            {
                var conn = new SqlConnection(_remoteConnStr);
                await conn.OpenAsync(cancellationToken);
                return conn;
            }
        }

        private static ISyncConnectionProvider CreateMockProvider(FormsSyncTestContext ctx)
        {
            var mock = new Mock<ISyncConnectionProvider>();
            mock.Setup(p => p.GetSelectedDatabaseId()).Returns(ctx.CanonicalDbId);
            mock.Setup(p => p.GetRemoteConnectionString(ctx.CanonicalDbId)).Returns(ctx.RemoteConnStr);
            mock.Setup(p => p.GetLocalConnectionString(ctx.CanonicalDbId)).Returns(ctx.LocalConnStr);
            mock.Setup(p => p.IsReadOnlyMode).Returns(false);
            return mock.Object;
        }

        [Fact]
        public async Task Test01_FormsPull_WithForeignKeys_SucceedsAndPreservesRelationships()
        {
            await using var ctx = await FormsSyncTestContext.CreateAsync();
            var provider = CreateMockProvider(ctx);
            var remoteFactory = new TestRemoteDatabaseConnectionFactory(ctx.RemoteConnStr);

            var dailySyncId = Guid.NewGuid();
            var formSyncId = Guid.NewGuid();
            var detailsSyncId = Guid.NewGuid();
            var refSyncId = Guid.NewGuid();

            // Populate Remote data
            await using (var conn = new SqlConnection(ctx.RemoteConnStr))
            {
                await conn.OpenAsync();

                // 1. Daily
                await using var cmdDaily = conn.CreateCommand();
                cmdDaily.CommandText = @"
                    INSERT INTO [dbo].[Daily] (Name, DailyDate, CreatedAt, SyncId, IsActive)
                    OUTPUT INSERTED.Id
                    VALUES ('Daily 1', SYSUTCDATETIME(), SYSUTCDATETIME(), @DailySyncId, 1);";
                cmdDaily.Parameters.AddWithValue("@DailySyncId", dailySyncId);
                var dailyId = (int)(await cmdDaily.ExecuteScalarAsync())!;

                // 2. Form
                await using var cmdForm = conn.CreateCommand();
                cmdForm.CommandText = @"
                    INSERT INTO [dbo].[Form] (SyncId, DailyId, Name, [Index], CreatedAt, IsActive)
                    OUTPUT INSERTED.Id
                    VALUES (@FormSyncId, @DailyId, 'Test Form 1', 1, SYSUTCDATETIME(), 1);";
                cmdForm.Parameters.AddWithValue("@FormSyncId", formSyncId);
                cmdForm.Parameters.AddWithValue("@DailyId", dailyId);
                var formId = (int)(await cmdForm.ExecuteScalarAsync())!;

                // 3. FormDetails
                await using var cmdDetails = conn.CreateCommand();
                cmdDetails.CommandText = @"
                    INSERT INTO [dbo].[FormDetails] (SyncId, FormId, EmployeeId, Amount, OrderNum, CreatedAt, IsActive)
                    VALUES (@DetailsSyncId, @FormId, '12345678901234', 500.0, 1, SYSUTCDATETIME(), 1);";
                cmdDetails.Parameters.AddWithValue("@DetailsSyncId", detailsSyncId);
                cmdDetails.Parameters.AddWithValue("@FormId", formId);
                await cmdDetails.ExecuteNonQueryAsync();

                // 4. FormRefernce
                await using var cmdRef = conn.CreateCommand();
                cmdRef.CommandText = @"
                    INSERT INTO [dbo].[FormRefernce] (SyncId, FormId, ReferencePath, CreatedAt, IsActive)
                    VALUES (@RefSyncId, @FormId, 'Content/ref1.pdf', SYSUTCDATETIME(), 1);";
                cmdRef.Parameters.AddWithValue("@RefSyncId", refSyncId);
                cmdRef.Parameters.AddWithValue("@FormId", formId);
                await cmdRef.ExecuteNonQueryAsync();

                // ServerChangeFeed (versions 1, 2, 3, 4)
                await using var cmdFeed = conn.CreateCommand();
                cmdFeed.CommandText = @"
                    INSERT INTO [sync].[ServerChangeFeed] (ServerVersion, DatabaseId, EntityType, EntitySyncId, OperationType, OriginDeviceId, TimestampUtc)
                    VALUES
                    (1, '2026', 'Daily', @DailySyncId, 'INSERT', NEWID(), SYSUTCDATETIME()),
                    (2, '2026', 'Form', @FormSyncId, 'INSERT', NEWID(), SYSUTCDATETIME()),
                    (3, '2026', 'FormDetails', @DetailsSyncId, 'INSERT', NEWID(), SYSUTCDATETIME()),
                    (4, '2026', 'FormRefernce', @RefSyncId, 'INSERT', NEWID(), SYSUTCDATETIME());

                    UPDATE [sync].[ServerState] SET CurrentVersion = 4 WHERE DatabaseId = '2026';
                ";
                cmdFeed.Parameters.AddWithValue("@DailySyncId", dailySyncId);
                cmdFeed.Parameters.AddWithValue("@FormSyncId", formSyncId);
                cmdFeed.Parameters.AddWithValue("@DetailsSyncId", detailsSyncId);
                cmdFeed.Parameters.AddWithValue("@RefSyncId", refSyncId);
                await cmdFeed.ExecuteNonQueryAsync();
            }

            // Execute Pull
            var reader = new AzureFencedBatchReader(remoteFactory, NullLogger<AzureFencedBatchReader>.Instance);
            var batch = await reader.ReadFencedBatchAsync("2026", 0, CancellationToken.None);

            Assert.Equal(0, batch.LowWatermark);
            Assert.Equal(4, batch.HighWatermark);
            Assert.Equal(4, batch.Commands.Count);

            var leaseToken = Guid.NewGuid();
            // Set active lease in LocalState
            await using (var localConn = new SqlConnection(ctx.LocalConnStr))
            {
                await localConn.OpenAsync();
                await using var cmd = localConn.CreateCommand();
                cmd.CommandText = $"UPDATE [sync].[LocalState] SET ActiveLeaseToken = '{leaseToken}', LeaseExpiresAtUtc = DATEADD(minute, 5, SYSUTCDATETIME()) WHERE DatabaseId = '2026';";
                await cmd.ExecuteNonQueryAsync();
            }

            var coordinator = new LocalPullTransactionCoordinator(provider, NullLogger<LocalPullTransactionCoordinator>.Instance);
            var result = await coordinator.ApplyPullBatchAsync("2026", batch, leaseToken, CancellationToken.None);

            Assert.Equal(4, result.FinalServerVersion);
            Assert.Equal(4, result.Succeeded);

            // Verify Local Database State and Foreign Key Integrity
            await using (var localConn = new SqlConnection(ctx.LocalConnStr))
            {
                await localConn.OpenAsync();

                // Check Daily
                await using var cmdDaily = localConn.CreateCommand();
                cmdDaily.CommandText = "SELECT Id, Name FROM [dbo].[Daily] WHERE SyncId = @SyncId;";
                cmdDaily.Parameters.AddWithValue("@SyncId", dailySyncId);
                await using var readerDaily = await cmdDaily.ExecuteReaderAsync();
                Assert.True(await readerDaily.ReadAsync());
                var localDailyId = readerDaily.GetInt32(0);
                Assert.Equal("Daily 1", readerDaily.GetString(1));
                await readerDaily.CloseAsync();

                // Check Form & its DailyId FK
                await using var cmdForm = localConn.CreateCommand();
                cmdForm.CommandText = "SELECT Id, DailyId, Name FROM [dbo].[Form] WHERE SyncId = @SyncId;";
                cmdForm.Parameters.AddWithValue("@SyncId", formSyncId);
                await using var readerForm = await cmdForm.ExecuteReaderAsync();
                Assert.True(await readerForm.ReadAsync());
                var localFormId = readerForm.GetInt32(0);
                Assert.Equal(localDailyId, readerForm.GetInt32(1));
                Assert.Equal("Test Form 1", readerForm.GetString(2));
                await readerForm.CloseAsync();

                // Check FormDetails & its FormId FK
                await using var cmdDetails = localConn.CreateCommand();
                cmdDetails.CommandText = "SELECT Id, FormId, EmployeeId, Amount FROM [dbo].[FormDetails] WHERE SyncId = @SyncId;";
                cmdDetails.Parameters.AddWithValue("@SyncId", detailsSyncId);
                await using var readerDetails = await cmdDetails.ExecuteReaderAsync();
                Assert.True(await readerDetails.ReadAsync());
                Assert.Equal(localFormId, readerDetails.GetInt32(1));
                Assert.Equal("12345678901234", readerDetails.GetString(2));
                Assert.Equal(500.0, readerDetails.GetDouble(3));
                await readerDetails.CloseAsync();

                // Check FormRefernce & its FormId FK
                await using var cmdRef = localConn.CreateCommand();
                cmdRef.CommandText = "SELECT Id, FormId, ReferencePath FROM [dbo].[FormRefernce] WHERE SyncId = @SyncId;";
                cmdRef.Parameters.AddWithValue("@SyncId", refSyncId);
                await using var readerRef = await cmdRef.ExecuteReaderAsync();
                Assert.True(await readerRef.ReadAsync());
                Assert.Equal(localFormId, readerRef.GetInt32(1));
                Assert.Equal("Content/ref1.pdf", readerRef.GetString(2));
                await readerRef.CloseAsync();

                // Check Local Checkpoint
                await using var cmdCp = localConn.CreateCommand();
                cmdCp.CommandText = "SELECT LastServerVersion FROM [sync].[LocalState] WHERE DatabaseId = '2026';";
                var lastVersion = (long)(await cmdCp.ExecuteScalarAsync())!;
                Assert.Equal(4, lastVersion);
            }
        }

        [Fact]
        public async Task Test02_FormsPull_ArchiveForm_HandlesNullDailyId()
        {
            await using var ctx = await FormsSyncTestContext.CreateAsync();
            var provider = CreateMockProvider(ctx);
            var remoteFactory = new TestRemoteDatabaseConnectionFactory(ctx.RemoteConnStr);

            var archiveFormSyncId = Guid.NewGuid();

            await using (var conn = new SqlConnection(ctx.RemoteConnStr))
            {
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO [dbo].[Form] (SyncId, DailyId, Name, [Index], CreatedAt, IsActive)
                    VALUES (@SyncId, NULL, 'Archived Annual Form', 99, SYSUTCDATETIME(), 1);

                    INSERT INTO [sync].[ServerChangeFeed] (ServerVersion, DatabaseId, EntityType, EntitySyncId, OperationType, OriginDeviceId, TimestampUtc)
                    VALUES (1, '2026', 'Form', @SyncId, 'INSERT', NEWID(), SYSUTCDATETIME());

                    UPDATE [sync].[ServerState] SET CurrentVersion = 1 WHERE DatabaseId = '2026';
                ";
                cmd.Parameters.AddWithValue("@SyncId", archiveFormSyncId);
                await cmd.ExecuteNonQueryAsync();
            }

            var reader = new AzureFencedBatchReader(remoteFactory, NullLogger<AzureFencedBatchReader>.Instance);
            var batch = await reader.ReadFencedBatchAsync("2026", 0, CancellationToken.None);

            Assert.Single(batch.Commands);
            Assert.Equal("Form", batch.Commands[0].EntityType);
            Assert.Null(batch.Commands[0].FormSnapshot!.DailySyncId);

            var leaseToken = Guid.NewGuid();
            await using (var localConn = new SqlConnection(ctx.LocalConnStr))
            {
                await localConn.OpenAsync();
                await using var cmd = localConn.CreateCommand();
                cmd.CommandText = $"UPDATE [sync].[LocalState] SET ActiveLeaseToken = '{leaseToken}', LeaseExpiresAtUtc = DATEADD(minute, 5, SYSUTCDATETIME()) WHERE DatabaseId = '2026';";
                await cmd.ExecuteNonQueryAsync();
            }

            var coordinator = new LocalPullTransactionCoordinator(provider, NullLogger<LocalPullTransactionCoordinator>.Instance);
            var result = await coordinator.ApplyPullBatchAsync("2026", batch, leaseToken, CancellationToken.None);
            Assert.Equal(1, result.FinalServerVersion);
            Assert.Equal(1, result.Succeeded);

            await using (var localConn = new SqlConnection(ctx.LocalConnStr))
            {
                await localConn.OpenAsync();
                await using var cmd = localConn.CreateCommand();
                cmd.CommandText = "SELECT DailyId, Name FROM [dbo].[Form] WHERE SyncId = @SyncId;";
                cmd.Parameters.AddWithValue("@SyncId", archiveFormSyncId);
                await using var rdr = await cmd.ExecuteReaderAsync();
                Assert.True(await rdr.ReadAsync());
                Assert.True(rdr.IsDBNull(0));
                Assert.Equal("Archived Annual Form", rdr.GetString(1));
            }
        }

        [Fact]
        public async Task Test03_FormsPush_FormAndDetails_ReplaysSuccessfullyToRemote()
        {
            await using var ctx = await FormsSyncTestContext.CreateAsync();

            var dailySyncId = Guid.NewGuid();
            var formSyncId = Guid.NewGuid();
            var detailsSyncId = Guid.NewGuid();
            var opIdForm = Guid.NewGuid();
            var opIdDetails = Guid.NewGuid();

            // Setup Daily in Remote and Local
            await using (var remConn = new SqlConnection(ctx.RemoteConnStr))
            {
                await remConn.OpenAsync();
                await using var cmd = remConn.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO [dbo].[Daily] (Name, DailyDate, CreatedAt, SyncId, IsActive)
                    VALUES ('Daily Baseline', SYSUTCDATETIME(), SYSUTCDATETIME(), @DailySyncId, 1);
                    UPDATE [sync].[ServerState] SET CurrentVersion = 1 WHERE DatabaseId = '2026';
                ";
                cmd.Parameters.AddWithValue("@DailySyncId", dailySyncId);
                await cmd.ExecuteNonQueryAsync();
            }

            // Local DB has Daily and Outbox for Form & FormDetails
            await using (var locConn = new SqlConnection(ctx.LocalConnStr))
            {
                await locConn.OpenAsync();
                await using var cmd = locConn.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO [dbo].[Daily] (Name, DailyDate, CreatedAt, SyncId, IsActive)
                    VALUES ('Daily Baseline', SYSUTCDATETIME(), SYSUTCDATETIME(), @DailySyncId, 1);
                    UPDATE [sync].[LocalState] SET LastServerVersion = 1 WHERE DatabaseId = '2026';
                ";
                cmd.Parameters.AddWithValue("@DailySyncId", dailySyncId);
                await cmd.ExecuteNonQueryAsync();
            }

            // Build outbox payloads
            var formPayload = JsonSerializer.Serialize(new SortedDictionary<string, object?>
            {
                ["baseServerVersion"] = 1L,
                ["createdAtUtc"] = DateTime.UtcNow.ToString("O"),
                ["databaseId"] = "2026",
                ["deviceId"] = ctx.DeviceId.ToString(),
                ["entityData"] = new SortedDictionary<string, object?>
                {
                    ["DailySyncId"] = dailySyncId.ToString(),
                    ["Name"] = "Offline Form 1",
                    ["Description"] = "Created Offline",
                    ["Index"] = 1,
                    ["IsActive"] = true,
                    ["CreatedAt"] = DateTime.UtcNow.ToString("O"),
                    ["CreatedBy"] = "offline_user"
                },
                ["entitySyncId"] = formSyncId.ToString(),
                ["entityType"] = "Form",
                ["operationType"] = "INSERT",
                ["schemaVersion"] = 1
            });

            var detailsPayload = JsonSerializer.Serialize(new SortedDictionary<string, object?>
            {
                ["baseServerVersion"] = 1L,
                ["createdAtUtc"] = DateTime.UtcNow.ToString("O"),
                ["databaseId"] = "2026",
                ["deviceId"] = ctx.DeviceId.ToString(),
                ["entityData"] = new SortedDictionary<string, object?>
                {
                    ["FormSyncId"] = formSyncId.ToString(),
                    ["EmployeeId"] = "12345678901234",
                    ["Amount"] = 750.25,
                    ["OrderNum"] = 1,
                    ["IsActive"] = true,
                    ["CreatedAt"] = DateTime.UtcNow.ToString("O"),
                    ["CreatedBy"] = "offline_user"
                },
                ["entitySyncId"] = detailsSyncId.ToString(),
                ["entityType"] = "FormDetails",
                ["operationType"] = "INSERT",
                ["schemaVersion"] = 1
            });

            var formOutbox = new LocalOutbox
            {
                DatabaseId = "2026",
                ClientOperationId = opIdForm,
                CommandName = "Form.Insert",
                AggregateType = "Form",
                EntitySyncId = formSyncId,
                PayloadJson = formPayload,
                CreatedAtUtc = DateTime.UtcNow,
                Status = "IN_PROGRESS"
            };

            var detailsOutbox = new LocalOutbox
            {
                DatabaseId = "2026",
                ClientOperationId = opIdDetails,
                CommandName = "FormDetails.Insert",
                AggregateType = "FormDetails",
                EntitySyncId = detailsSyncId,
                PayloadJson = detailsPayload,
                CreatedAtUtc = DateTime.UtcNow,
                Status = "IN_PROGRESS"
            };

            var hashForm = LocalOutboxPushService.ComputeRequestHash("2026", ctx.DeviceId, "Form.Insert", "Form", formSyncId, formPayload);
            var hashDetails = LocalOutboxPushService.ComputeRequestHash("2026", ctx.DeviceId, "FormDetails.Insert", "FormDetails", detailsSyncId, detailsPayload);

            var pushCoordinator = new AzurePushTransactionCoordinator(NullLogger<AzurePushTransactionCoordinator>.Instance);

            await using (var conn = new SqlConnection(ctx.RemoteConnStr))
            {
                await conn.OpenAsync();

                var formResult = await pushCoordinator.ApplyOperationAsync(conn, "2026", formOutbox, 1L, hashForm, ctx.DeviceId, CancellationToken.None);
                Assert.Equal(2L, formResult.ServerVersion);
                Assert.False(formResult.IsReplay);

                var detailsResult = await pushCoordinator.ApplyOperationAsync(conn, "2026", detailsOutbox, 2L, hashDetails, ctx.DeviceId, CancellationToken.None);
                Assert.Equal(3L, detailsResult.ServerVersion);
                Assert.False(detailsResult.IsReplay);

                // Verify Form in Remote DB
                await using var cmdForm = conn.CreateCommand();
                cmdForm.CommandText = "SELECT Name, DailyId FROM [dbo].[Form] WHERE SyncId = @SyncId;";
                cmdForm.Parameters.AddWithValue("@SyncId", formSyncId);
                await using var rdrForm = await cmdForm.ExecuteReaderAsync();
                Assert.True(await rdrForm.ReadAsync());
                Assert.Equal("Offline Form 1", rdrForm.GetString(0));
                Assert.False(rdrForm.IsDBNull(1));
                await rdrForm.CloseAsync();

                // Verify FormDetails in Remote DB
                await using var cmdDet = conn.CreateCommand();
                cmdDet.CommandText = "SELECT EmployeeId, Amount FROM [dbo].[FormDetails] WHERE SyncId = @SyncId;";
                cmdDet.Parameters.AddWithValue("@SyncId", detailsSyncId);
                await using var rdrDet = await cmdDet.ExecuteReaderAsync();
                Assert.True(await rdrDet.ReadAsync());
                Assert.Equal("12345678901234", rdrDet.GetString(0));
                Assert.Equal(750.25, rdrDet.GetDouble(1));
                await rdrDet.CloseAsync();

                // Check ServerChangeFeed
                await using var cmdFeed = conn.CreateCommand();
                cmdFeed.CommandText = "SELECT COUNT(*) FROM [sync].[ServerChangeFeed] WHERE EntityType IN ('Form', 'FormDetails');";
                var feedCount = (int)(await cmdFeed.ExecuteScalarAsync())!;
                Assert.Equal(2, feedCount);
            }
        }

        [Fact]
        public async Task Test04_FormsPush_BothChanged_ThrowsConflictRiskException()
        {
            await using var ctx = await FormsSyncTestContext.CreateAsync();

            // Set Remote CurrentVersion to 5
            await using (var remConn = new SqlConnection(ctx.RemoteConnStr))
            {
                await remConn.OpenAsync();
                await using var cmd = remConn.CreateCommand();
                cmd.CommandText = "UPDATE [sync].[ServerState] SET CurrentVersion = 5 WHERE DatabaseId = '2026';";
                await cmd.ExecuteNonQueryAsync();
            }

            var pushCoordinator = new AzurePushTransactionCoordinator(NullLogger<AzurePushTransactionCoordinator>.Instance);

            var formSyncId = Guid.NewGuid();
            var payload = JsonSerializer.Serialize(new SortedDictionary<string, object?>
            {
                ["baseServerVersion"] = 2L,
                ["createdAtUtc"] = DateTime.UtcNow.ToString("O"),
                ["databaseId"] = "2026",
                ["deviceId"] = ctx.DeviceId.ToString(),
                ["entityData"] = new SortedDictionary<string, object?>
                {
                    ["DailySyncId"] = (string?)null,
                    ["Name"] = "Conflict Form",
                    ["Description"] = "Conflict",
                    ["Index"] = 1,
                    ["IsActive"] = true,
                    ["CreatedAt"] = DateTime.UtcNow.ToString("O"),
                    ["CreatedBy"] = "offline_user"
                },
                ["entitySyncId"] = formSyncId.ToString(),
                ["entityType"] = "Form",
                ["operationType"] = "INSERT",
                ["schemaVersion"] = 1
            });

            var outbox = new LocalOutbox
            {
                DatabaseId = "2026",
                ClientOperationId = Guid.NewGuid(),
                AggregateType = "Form",
                CommandName = "Form.Insert",
                EntitySyncId = formSyncId,
                PayloadJson = payload,
                CreatedAtUtc = DateTime.UtcNow,
                Status = "IN_PROGRESS"
            };

            await using var conn = new SqlConnection(ctx.RemoteConnStr);
            await conn.OpenAsync();

            var ex = await Assert.ThrowsAsync<SyncConflictRiskException>(() =>
                pushCoordinator.ApplyOperationAsync(conn, "2026", outbox, 2L, "testhash", ctx.DeviceId, CancellationToken.None));

            Assert.Equal("BOTH_CHANGED_CONFLICT_RISK", ex.ErrorCode);
            Assert.Equal(2L, ex.LocalVersion);
            Assert.Equal(5L, ex.ServerVersion);
        }
    }
}
