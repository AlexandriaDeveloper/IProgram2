#nullable enable
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Auth.Infrastructure;
using Auth.Infrastructure.Sync.Authoritative;
using Auth.Infrastructure.Sync.Push;
using Core.Exceptions;
using Core.Interfaces;
using Core.Models;
using Core.Models.Sync;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Persistence.Repository;
using Xunit;

namespace Auth.UnitTests
{
    [Trait("Category", "LocalDbRequired")]
    [Collection("AuthoritativeDailyTrackingIntegration")]
    public class AuthoritativeDailyTrackingIntegrationTests : IAsyncLifetime
    {
        private const string MasterConnStr = "Server=localhost;Database=master;Integrated Security=True;TrustServerCertificate=True;";
        public const string RemoteConnStr2026 = "Server=localhost;Database=IProgramRemoteSync2026_SmokeTest;Integrated Security=True;TrustServerCertificate=True;";
        public const string RemoteConnStr2027 = "Server=localhost;Database=IProgramRemoteSync2027_SmokeTest;Integrated Security=True;TrustServerCertificate=True;";

        private readonly AuthoritativeDailyMutationTracker _tracker = new(NullLogger<AuthoritativeDailyMutationTracker>.Instance);
        private readonly AzurePushTransactionCoordinator _pushCoordinator = new(NullLogger<AzurePushTransactionCoordinator>.Instance);

        public async Task InitializeAsync()
        {
            await EnsureDatabaseAndSchemaAsync("IProgramRemoteSync2026_SmokeTest", "2026");
            await EnsureDatabaseAndSchemaAsync("IProgramRemoteSync2027_SmokeTest", "2027");
        }

        public Task DisposeAsync() => Task.CompletedTask;

        private async Task EnsureDatabaseAndSchemaAsync(string dbName, string canonicalId)
        {
            await using var masterConn = new SqlConnection(MasterConnStr);
            await masterConn.OpenAsync();

            await using var checkDbCmd = masterConn.CreateCommand();
            checkDbCmd.CommandText = $@"
                IF DB_ID('{dbName}') IS NULL
                BEGIN
                    CREATE DATABASE [{dbName}];
                    ALTER DATABASE [{dbName}] SET COMPATIBILITY_LEVEL = 120;
                END;";
            await checkDbCmd.ExecuteNonQueryAsync();

            var connStr = $"Server=localhost;Database={dbName};Integrated Security=True;TrustServerCertificate=True;";
            await using var conn = new SqlConnection(connStr);
            await conn.OpenAsync();

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                IF OBJECT_ID('dbo.Daily', 'U') IS NULL
                BEGIN
                    CREATE TABLE [dbo].[Daily] (
                        [Id] INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                        [Name] NVARCHAR(100) NOT NULL,
                        [DailyDate] DATETIME2 NOT NULL,
                        [Closed] BIT NOT NULL CONSTRAINT [DF_{dbName}_Daily_Closed] DEFAULT(0),
                        [CreatedBy] NVARCHAR(100) NULL,
                        [CreatedAt] DATETIME2 NOT NULL,
                        [UpdatedBy] NVARCHAR(100) NULL,
                        [UpdatedAt] DATETIME2 NULL,
                        [DeactivatedBy] NVARCHAR(100) NULL,
                        [DeactivatedAt] DATETIME2 NULL,
                        [IsActive] BIT NOT NULL CONSTRAINT [DF_{dbName}_Daily_IsActive] DEFAULT(1),
                        [SyncId] UNIQUEIDENTIFIER NOT NULL
                    );
                    CREATE UNIQUE INDEX [IX_{dbName}_Daily_SyncId] ON [dbo].[Daily]([SyncId]);
                END;

                IF OBJECT_ID('dbo.Departments', 'U') IS NULL
                BEGIN
                    CREATE TABLE [dbo].[Departments] (
                        [Id] INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                        [Name] NVARCHAR(100) NOT NULL,
                        [SyncId] UNIQUEIDENTIFIER NOT NULL CONSTRAINT [DF_{dbName}_Dept_SyncId] DEFAULT(NEWID()),
                        [CreatedBy] NVARCHAR(100) NULL,
                        [CreatedAt] DATETIME2 NOT NULL CONSTRAINT [DF_{dbName}_Dept_CreatedAt] DEFAULT(SYSUTCDATETIME()),
                        [UpdatedBy] NVARCHAR(100) NULL,
                        [UpdatedAt] DATETIME2 NULL,
                        [DeactivatedBy] NVARCHAR(100) NULL,
                        [DeactivatedAt] DATETIME2 NULL,
                        [IsActive] BIT NOT NULL CONSTRAINT [DF_{dbName}_Dept_IsActive] DEFAULT(1)
                    );
                END
                ELSE
                BEGIN
                    IF COL_LENGTH('dbo.Departments', 'CreatedAt') IS NULL
                    BEGIN
                        ALTER TABLE [dbo].[Departments] ADD
                            [CreatedBy] NVARCHAR(100) NULL,
                            [CreatedAt] DATETIME2 NOT NULL CONSTRAINT [DF_{dbName}_Dept_CreatedAt_Alter] DEFAULT(SYSUTCDATETIME()),
                            [UpdatedBy] NVARCHAR(100) NULL,
                            [UpdatedAt] DATETIME2 NULL,
                            [DeactivatedBy] NVARCHAR(100) NULL,
                            [DeactivatedAt] DATETIME2 NULL,
                            [IsActive] BIT NOT NULL CONSTRAINT [DF_{dbName}_Dept_IsActive_Alter] DEFAULT(1);
                    END
                END;

                IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = 'sync') EXEC('CREATE SCHEMA [sync];');

                IF OBJECT_ID('sync.ServerState', 'U') IS NULL
                BEGIN
                    CREATE TABLE [sync].[ServerState] (
                        [DatabaseId] NVARCHAR(32) NOT NULL PRIMARY KEY,
                        [CurrentVersion] BIGINT NOT NULL,
                        [LastUpdatedUtc] DATETIME2 NOT NULL
                    );
                END;

                IF NOT EXISTS (SELECT 1 FROM [sync].[ServerState] WHERE DatabaseId = '{canonicalId}')
                BEGIN
                    INSERT INTO [sync].[ServerState] ([DatabaseId], [CurrentVersion], [LastUpdatedUtc])
                    VALUES ('{canonicalId}', 0, SYSUTCDATETIME());
                END;

                IF OBJECT_ID('sync.ServerChangeFeed', 'U') IS NULL
                BEGIN
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
                    CREATE INDEX [IX_{dbName}_ServerChangeFeed_Pull] ON [sync].[ServerChangeFeed]([DatabaseId], [ServerVersion]);
                END;

                IF OBJECT_ID('sync.Tombstones', 'U') IS NULL
                BEGIN
                    CREATE TABLE [sync].[Tombstones] (
                        [DatabaseId] NVARCHAR(32) NOT NULL,
                        [EntityType] NVARCHAR(50) NOT NULL,
                        [EntitySyncId] UNIQUEIDENTIFIER NOT NULL,
                        [NaturalKey] NVARCHAR(50) NULL,
                        [ServerVersion] BIGINT NOT NULL,
                        [DeletedAtUtc] DATETIME2 NOT NULL,
                        CONSTRAINT [PK_{dbName}_Tombstones] PRIMARY KEY ([DatabaseId], [EntityType], [EntitySyncId])
                    );
                    CREATE INDEX [IX_{dbName}_Tombstones_Pull] ON [sync].[Tombstones]([DatabaseId], [ServerVersion]);
                END;

                IF OBJECT_ID('sync.ProcessedOperations', 'U') IS NULL
                BEGIN
                    CREATE TABLE [sync].[ProcessedOperations] (
                        [DatabaseId] NVARCHAR(32) NOT NULL,
                        [ClientOperationId] UNIQUEIDENTIFIER NOT NULL,
                        [DeviceId] UNIQUEIDENTIFIER NOT NULL,
                        [CommandName] NVARCHAR(100) NOT NULL,
                        [RequestHash] VARCHAR(64) NOT NULL,
                        [EntityType] NVARCHAR(50) NOT NULL,
                        [EntitySyncId] UNIQUEIDENTIFIER NOT NULL,
                        [ProcessedAtUtc] DATETIME2 NOT NULL,
                        [ResultStatus] NVARCHAR(20) NOT NULL,
                        [ResponseJson] NVARCHAR(MAX) NULL,
                        CONSTRAINT [PK_{dbName}_ProcessedOperations] PRIMARY KEY ([DatabaseId], [ClientOperationId])
                    );
                END;";
            await cmd.ExecuteNonQueryAsync();
        }

        private class TestIsolatedDatabaseBindingGuard : IAuthoritativeDatabaseBindingGuard
        {
            public void ValidateAuthoritativeAzureBinding(string canonicalDatabaseId, string? serverOrDataSource, string? physicalDbName)
            {
                if (canonicalDatabaseId != "2026" && canonicalDatabaseId != "2027")
                {
                    throw new AuthoritativeBindingException($"Invalid canonical databaseId '{canonicalDatabaseId}'.");
                }

                if (physicalDbName != null && !physicalDbName.Contains(canonicalDatabaseId, StringComparison.OrdinalIgnoreCase))
                {
                    throw new AuthoritativeBindingException($"Year mismatch: Canonical ID '{canonicalDatabaseId}' bound to database '{physicalDbName}'.");
                }
            }
        }

        private ApplicationContext CreateContext(
            string connectionString,
            bool trackingEnabled,
            ISyncConnectionProvider syncProvider)
        {
            var configDict = new Dictionary<string, string?>
            {
                { "Sync:AuthoritativeTrackingEnabled", trackingEnabled.ToString() }
            };
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

            var interceptor = new AuthoritativeTrackingSafetyInterceptor(syncProvider, configuration);

            var options = new DbContextOptionsBuilder<ApplicationContext>()
                .UseSqlServer(connectionString)
                .AddInterceptors(interceptor)
                .Options;

            return new ApplicationContext(options);
        }

        private UnitOfWork CreateUnitOfWork(
            ApplicationContext context,
            ISyncConnectionProvider syncProvider,
            IAuthoritativeDatabaseBindingGuard? guard = null,
            bool trackingEnabled = true)
        {
            var configDict = new Dictionary<string, string?>
            {
                { "Sync:AuthoritativeTrackingEnabled", trackingEnabled.ToString() }
            };
            var config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

            return new UnitOfWork(
                context: context,
                dbConnectionProvider: syncProvider,
                authoritativeTracker: _tracker,
                bindingGuard: guard ?? new TestIsolatedDatabaseBindingGuard(),
                configuration: config);
        }

        private async Task<long> GetServerVersionAsync(string connStr, string dbId)
        {
            await using var conn = new SqlConnection(connStr);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT CurrentVersion FROM [sync].[ServerState] WHERE DatabaseId = @DatabaseId;";
            cmd.Parameters.AddWithValue("@DatabaseId", dbId);
            return Convert.ToInt64(await cmd.ExecuteScalarAsync());
        }

        #region Scenario 1: Online INSERT

        [Fact]
        public async Task Scenario01_OnlineInsert_RecordsDaily_IncrementsServerVersion_ChangeFeedServerOrigin()
        {
            var syncProviderMock = new Mock<ISyncConnectionProvider>();
            syncProviderMock.Setup(p => p.IsLocalFirstEnabled).Returns(false);
            syncProviderMock.Setup(p => p.IsReadOnlyMode).Returns(false);
            syncProviderMock.Setup(p => p.GetSelectedDatabaseId()).Returns("2026");

            using var context = CreateContext(RemoteConnStr2026, trackingEnabled: true, syncProviderMock.Object);
            var uow = CreateUnitOfWork(context, syncProviderMock.Object);

            var startVersion = await GetServerVersionAsync(RemoteConnStr2026, "2026");
            var syncId = Guid.NewGuid();

            var daily = new Daily
            {
                Name = "Online Insert Daily 2026",
                DailyDate = DateTime.UtcNow.Date,
                Closed = false,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                SyncId = syncId
            };

            context.Set<Daily>().Add(daily);
            var saved = await uow.SaveChangesAsync();
            Assert.True(saved > 0);

            var newVersion = await GetServerVersionAsync(RemoteConnStr2026, "2026");
            Assert.Equal(startVersion + 1, newVersion);

            // Verify ServerChangeFeed entry
            await using var conn = new SqlConnection(RemoteConnStr2026);
            await conn.OpenAsync();
            await using var feedCmd = conn.CreateCommand();
            feedCmd.CommandText = @"
                SELECT ServerVersion, EntityType, EntitySyncId, OperationType, OriginDeviceId
                FROM [sync].[ServerChangeFeed]
                WHERE DatabaseId = '2026' AND EntitySyncId = @EntitySyncId;";
            feedCmd.Parameters.AddWithValue("@EntitySyncId", syncId);

            await using var reader = await feedCmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(newVersion, reader.GetInt64(0));
            Assert.Equal("Daily", reader.GetString(1));
            Assert.Equal(syncId, reader.GetGuid(2));
            Assert.Equal("INSERT", reader.GetString(3));
            Assert.Equal(AuthoritativeDailyMutationTracker.ServerOriginDeviceId, reader.GetGuid(4));
            Assert.Equal(Guid.Empty, reader.GetGuid(4));
            await reader.CloseAsync();

            // Verify NO tombstone and NO ProcessedOperations
            await using var checkCmd = conn.CreateCommand();
            checkCmd.CommandText = @"
                SELECT (SELECT COUNT(1) FROM [sync].[Tombstones] WHERE EntitySyncId = @SyncId),
                       (SELECT COUNT(1) FROM [sync].[ProcessedOperations] WHERE EntitySyncId = @SyncId);";
            checkCmd.Parameters.AddWithValue("@SyncId", syncId);
            await using var checkReader = await checkCmd.ExecuteReaderAsync();
            Assert.True(await checkReader.ReadAsync());
            Assert.Equal(0, checkReader.GetInt32(0)); // Zero tombstones
            Assert.Equal(0, checkReader.GetInt32(1)); // Zero processed operations
        }

        #endregion

        #region Scenario 2: Online UPDATE

        [Fact]
        public async Task Scenario02_OnlineUpdate_UpdatesDaily_IncrementsServerVersion_ChangeFeedUpdate()
        {
            var syncProviderMock = new Mock<ISyncConnectionProvider>();
            syncProviderMock.Setup(p => p.IsLocalFirstEnabled).Returns(false);
            syncProviderMock.Setup(p => p.IsReadOnlyMode).Returns(false);
            syncProviderMock.Setup(p => p.GetSelectedDatabaseId()).Returns("2026");

            var syncId = Guid.NewGuid();

            // Setup existing daily
            using (var setupContext = CreateContext(RemoteConnStr2026, trackingEnabled: true, syncProviderMock.Object))
            {
                var setupUow = CreateUnitOfWork(setupContext, syncProviderMock.Object);
                setupContext.Set<Daily>().Add(new Daily
                {
                    Name = "Daily Before Update",
                    DailyDate = DateTime.UtcNow.Date,
                    Closed = false,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow,
                    SyncId = syncId
                });
                await setupUow.SaveChangesAsync();
            }

            var verBeforeUpdate = await GetServerVersionAsync(RemoteConnStr2026, "2026");

            // Perform UPDATE
            using (var updateContext = CreateContext(RemoteConnStr2026, trackingEnabled: true, syncProviderMock.Object))
            {
                var updateUow = CreateUnitOfWork(updateContext, syncProviderMock.Object);
                var existing = await updateContext.Set<Daily>().FirstAsync(d => d.SyncId == syncId);
                existing.Name = "Daily After Online Update";
                existing.UpdatedAt = DateTime.UtcNow;

                var saved = await updateUow.SaveChangesAsync();
                Assert.True(saved > 0);
            }

            var verAfterUpdate = await GetServerVersionAsync(RemoteConnStr2026, "2026");
            Assert.Equal(verBeforeUpdate + 1, verAfterUpdate);

            // Verify ChangeFeed UPDATE entry
            await using var conn = new SqlConnection(RemoteConnStr2026);
            await conn.OpenAsync();
            await using var feedCmd = conn.CreateCommand();
            feedCmd.CommandText = @"
                SELECT ServerVersion, OperationType, OriginDeviceId
                FROM [sync].[ServerChangeFeed]
                WHERE DatabaseId = '2026' AND EntitySyncId = @EntitySyncId AND ServerVersion = @ServerVersion;";
            feedCmd.Parameters.AddWithValue("@EntitySyncId", syncId);
            feedCmd.Parameters.AddWithValue("@ServerVersion", verAfterUpdate);

            await using var reader = await feedCmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(verAfterUpdate, reader.GetInt64(0));
            Assert.Equal("UPDATE", reader.GetString(1));
            Assert.Equal(Guid.Empty, reader.GetGuid(2));
        }

        #endregion

        #region Scenario 3: Online SOFT_DELETE

        [Fact]
        public async Task Scenario03_OnlineSoftDelete_SetsIsActiveFalse_IncrementsVersion_NoTombstone()
        {
            var syncProviderMock = new Mock<ISyncConnectionProvider>();
            syncProviderMock.Setup(p => p.IsLocalFirstEnabled).Returns(false);
            syncProviderMock.Setup(p => p.IsReadOnlyMode).Returns(false);
            syncProviderMock.Setup(p => p.GetSelectedDatabaseId()).Returns("2026");

            var syncId = Guid.NewGuid();

            // Setup existing active daily
            using (var setupContext = CreateContext(RemoteConnStr2026, trackingEnabled: true, syncProviderMock.Object))
            {
                var setupUow = CreateUnitOfWork(setupContext, syncProviderMock.Object);
                setupContext.Set<Daily>().Add(new Daily
                {
                    Name = "Daily Active For SoftDelete",
                    DailyDate = DateTime.UtcNow.Date,
                    Closed = false,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow,
                    SyncId = syncId
                });
                await setupUow.SaveChangesAsync();
            }

            var verBeforeSoftDel = await GetServerVersionAsync(RemoteConnStr2026, "2026");

            // Perform SOFT_DELETE
            using (var delContext = CreateContext(RemoteConnStr2026, trackingEnabled: true, syncProviderMock.Object))
            {
                var delUow = CreateUnitOfWork(delContext, syncProviderMock.Object);
                var existing = await delContext.Set<Daily>().FirstAsync(d => d.SyncId == syncId);
                existing.IsActive = false;
                existing.DeactivatedAt = DateTime.UtcNow;

                var saved = await delUow.SaveChangesAsync();
                Assert.True(saved > 0);
            }

            var verAfterSoftDel = await GetServerVersionAsync(RemoteConnStr2026, "2026");
            Assert.Equal(verBeforeSoftDel + 1, verAfterSoftDel);

            // Verify ChangeFeed SOFT_DELETE entry
            await using var conn = new SqlConnection(RemoteConnStr2026);
            await conn.OpenAsync();
            await using var feedCmd = conn.CreateCommand();
            feedCmd.CommandText = @"
                SELECT ServerVersion, OperationType, OriginDeviceId
                FROM [sync].[ServerChangeFeed]
                WHERE DatabaseId = '2026' AND EntitySyncId = @EntitySyncId AND ServerVersion = @ServerVersion;";
            feedCmd.Parameters.AddWithValue("@EntitySyncId", syncId);
            feedCmd.Parameters.AddWithValue("@ServerVersion", verAfterSoftDel);

            await using var reader = await feedCmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(verAfterSoftDel, reader.GetInt64(0));
            Assert.Equal("SOFT_DELETE", reader.GetString(1));
            Assert.Equal(Guid.Empty, reader.GetGuid(2));
            await reader.CloseAsync();

            // Verify NO tombstone for soft delete
            await using var tombCmd = conn.CreateCommand();
            tombCmd.CommandText = "SELECT COUNT(1) FROM [sync].[Tombstones] WHERE EntitySyncId = @SyncId;";
            tombCmd.Parameters.AddWithValue("@SyncId", syncId);
            var tombCount = Convert.ToInt32(await tombCmd.ExecuteScalarAsync());
            Assert.Equal(0, tombCount);
        }

        #endregion

        #region Scenario 4: Online HARD_DELETE

        [Fact]
        public async Task Scenario04_OnlineHardDelete_DeletesDaily_IncrementsVersion_CreatesTombstone()
        {
            var syncProviderMock = new Mock<ISyncConnectionProvider>();
            syncProviderMock.Setup(p => p.IsLocalFirstEnabled).Returns(false);
            syncProviderMock.Setup(p => p.IsReadOnlyMode).Returns(false);
            syncProviderMock.Setup(p => p.GetSelectedDatabaseId()).Returns("2026");

            var syncId = Guid.NewGuid();

            // Setup existing daily
            using (var setupContext = CreateContext(RemoteConnStr2026, trackingEnabled: true, syncProviderMock.Object))
            {
                var setupUow = CreateUnitOfWork(setupContext, syncProviderMock.Object);
                setupContext.Set<Daily>().Add(new Daily
                {
                    Name = "Daily For Hard Delete",
                    DailyDate = DateTime.UtcNow.Date,
                    Closed = false,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow,
                    SyncId = syncId
                });
                await setupUow.SaveChangesAsync();
            }

            var verBeforeHardDel = await GetServerVersionAsync(RemoteConnStr2026, "2026");

            // Perform HARD_DELETE
            using (var hardDelContext = CreateContext(RemoteConnStr2026, trackingEnabled: true, syncProviderMock.Object))
            {
                var hardDelUow = CreateUnitOfWork(hardDelContext, syncProviderMock.Object);
                var existing = await hardDelContext.Set<Daily>().FirstAsync(d => d.SyncId == syncId);
                hardDelContext.Set<Daily>().Remove(existing);

                var saved = await hardDelUow.SaveChangesAsync();
                Assert.True(saved > 0);
            }

            var verAfterHardDel = await GetServerVersionAsync(RemoteConnStr2026, "2026");
            Assert.Equal(verBeforeHardDel + 1, verAfterHardDel);

            await using var conn = new SqlConnection(RemoteConnStr2026);
            await conn.OpenAsync();

            // Verify Daily is physically absent
            await using var dailyCmd = conn.CreateCommand();
            dailyCmd.CommandText = "SELECT COUNT(1) FROM [dbo].[Daily] WHERE SyncId = @SyncId;";
            dailyCmd.Parameters.AddWithValue("@SyncId", syncId);
            var dailyCount = Convert.ToInt32(await dailyCmd.ExecuteScalarAsync());
            Assert.Equal(0, dailyCount);

            // Verify Tombstone exists
            await using var tombCmd = conn.CreateCommand();
            tombCmd.CommandText = @"
                SELECT ServerVersion, EntityType, NaturalKey, DeletedAtUtc
                FROM [sync].[Tombstones]
                WHERE DatabaseId = '2026' AND EntitySyncId = @SyncId;";
            tombCmd.Parameters.AddWithValue("@SyncId", syncId);
            await using var tombReader = await tombCmd.ExecuteReaderAsync();
            Assert.True(await tombReader.ReadAsync());
            Assert.Equal(verAfterHardDel, tombReader.GetInt64(0));
            Assert.Equal("Daily", tombReader.GetString(1));
            Assert.True(tombReader.IsDBNull(2)); // NaturalKey is NULL
            await tombReader.CloseAsync();

            // Verify ChangeFeed has HARD_DELETE
            await using var feedCmd = conn.CreateCommand();
            feedCmd.CommandText = @"
                SELECT ServerVersion, OperationType, OriginDeviceId
                FROM [sync].[ServerChangeFeed]
                WHERE DatabaseId = '2026' AND EntitySyncId = @EntitySyncId AND ServerVersion = @ServerVersion;";
            feedCmd.Parameters.AddWithValue("@EntitySyncId", syncId);
            feedCmd.Parameters.AddWithValue("@ServerVersion", verAfterHardDel);
            await using var feedReader = await feedCmd.ExecuteReaderAsync();
            Assert.True(await feedReader.ReadAsync());
            Assert.Equal(verAfterHardDel, feedReader.GetInt64(0));
            Assert.Equal("HARD_DELETE", feedReader.GetString(1));
            Assert.Equal(Guid.Empty, feedReader.GetGuid(2));
        }

        #endregion

        #region Scenario 5: Transaction Rollback Proof

        [Fact]
        public async Task Scenario05_TransactionRollback_TriggerFaultOnFeed_RollsBackBusinessDailyAndState()
        {
            var syncProviderMock = new Mock<ISyncConnectionProvider>();
            syncProviderMock.Setup(p => p.IsLocalFirstEnabled).Returns(false);
            syncProviderMock.Setup(p => p.IsReadOnlyMode).Returns(false);
            syncProviderMock.Setup(p => p.GetSelectedDatabaseId()).Returns("2026");

            var startVersion = await GetServerVersionAsync(RemoteConnStr2026, "2026");
            var syncId = Guid.NewGuid();

            await using var conn = new SqlConnection(RemoteConnStr2026);
            await conn.OpenAsync();

            // Install fault trigger on ServerChangeFeed using EXEC to comply with compatibility level 120
            await using var triggerCmd = conn.CreateCommand();
            triggerCmd.CommandText = @"
                IF OBJECT_ID('sync.TR_TestFault_ServerChangeFeed', 'TR') IS NOT NULL
                    DROP TRIGGER [sync].[TR_TestFault_ServerChangeFeed];
                EXEC('
                    CREATE TRIGGER [sync].[TR_TestFault_ServerChangeFeed]
                    ON [sync].[ServerChangeFeed]
                    AFTER INSERT
                    AS
                    BEGIN
                        IF EXISTS (SELECT 1 FROM inserted WHERE EntitySyncId = ''" + syncId + @"'')
                        BEGIN
                            RAISERROR(''TEST_TRIGGER_FAULT: Simulating transient metadata failure'', 16, 1);
                            ROLLBACK TRANSACTION;
                        END
                    END;
                ');";
            await triggerCmd.ExecuteNonQueryAsync();

            try
            {
                using var context = CreateContext(RemoteConnStr2026, trackingEnabled: true, syncProviderMock.Object);
                var uow = CreateUnitOfWork(context, syncProviderMock.Object);

                context.Set<Daily>().Add(new Daily
                {
                    Name = "Daily Destined For Rollback",
                    DailyDate = DateTime.UtcNow.Date,
                    Closed = false,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow,
                    SyncId = syncId
                });

                // SaveChanges must throw due to trigger failure
                await Assert.ThrowsAnyAsync<Exception>(async () =>
                {
                    await uow.SaveChangesAsync();
                });

                // VERIFICATION: Business Daily must be ROLLED BACK
                await using var checkDailyCmd = conn.CreateCommand();
                checkDailyCmd.CommandText = "SELECT COUNT(1) FROM [dbo].[Daily] WHERE SyncId = @SyncId;";
                checkDailyCmd.Parameters.AddWithValue("@SyncId", syncId);
                var dailyCount = Convert.ToInt32(await checkDailyCmd.ExecuteScalarAsync());
                Assert.Equal(0, dailyCount);

                // VERIFICATION: ServerState must remain UNCHANGED
                var endVersion = await GetServerVersionAsync(RemoteConnStr2026, "2026");
                Assert.Equal(startVersion, endVersion);

                // VERIFICATION: ServerChangeFeed must NOT have row
                await using var checkFeedCmd = conn.CreateCommand();
                checkFeedCmd.CommandText = "SELECT COUNT(1) FROM [sync].[ServerChangeFeed] WHERE EntitySyncId = @SyncId;";
                checkFeedCmd.Parameters.AddWithValue("@SyncId", syncId);
                var feedCount = Convert.ToInt32(await checkFeedCmd.ExecuteScalarAsync());
                Assert.Equal(0, feedCount);
            }
            finally
            {
                // Remove fault trigger
                await using var dropCmd = conn.CreateCommand();
                dropCmd.CommandText = @"
                    IF OBJECT_ID('sync.TR_TestFault_ServerChangeFeed', 'TR') IS NOT NULL
                        DROP TRIGGER [sync].[TR_TestFault_ServerChangeFeed];";
                await dropCmd.ExecuteNonQueryAsync();
            }
        }

        #endregion

        #region Scenario 6: Multiple Daily Mutations in One SaveChanges

        [Fact]
        public async Task Scenario06_MultipleDailyMutations_SingleSaveChanges_ProducesSequentialUniqueVersions()
        {
            var syncProviderMock = new Mock<ISyncConnectionProvider>();
            syncProviderMock.Setup(p => p.IsLocalFirstEnabled).Returns(false);
            syncProviderMock.Setup(p => p.IsReadOnlyMode).Returns(false);
            syncProviderMock.Setup(p => p.GetSelectedDatabaseId()).Returns("2026");

            var startVersion = await GetServerVersionAsync(RemoteConnStr2026, "2026");

            var dynamicIds = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() }.OrderBy(g => g).ToList();
            var syncId1 = dynamicIds[0];
            var syncId2 = dynamicIds[1];
            var syncId3 = dynamicIds[2];

            using (var context = CreateContext(RemoteConnStr2026, trackingEnabled: true, syncProviderMock.Object))
            {
                var uow = CreateUnitOfWork(context, syncProviderMock.Object);

                context.Set<Daily>().Add(new Daily
                {
                    Name = "Multi Daily 1",
                    DailyDate = DateTime.UtcNow.Date,
                    Closed = false,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow,
                    SyncId = syncId1
                });

                context.Set<Daily>().Add(new Daily
                {
                    Name = "Multi Daily 2",
                    DailyDate = DateTime.UtcNow.Date,
                    Closed = false,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow,
                    SyncId = syncId2
                });

                context.Set<Daily>().Add(new Daily
                {
                    Name = "Multi Daily 3",
                    DailyDate = DateTime.UtcNow.Date,
                    Closed = false,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow,
                    SyncId = syncId3
                });

                var saved = await uow.SaveChangesAsync();
                Assert.Equal(3, saved);
            }

            var finalVersion = await GetServerVersionAsync(RemoteConnStr2026, "2026");
            Assert.Equal(startVersion + 3, finalVersion);

            // Verify sequential versions in ServerChangeFeed
            await using var conn = new SqlConnection(RemoteConnStr2026);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT ServerVersion, EntitySyncId
                FROM [sync].[ServerChangeFeed]
                WHERE DatabaseId = '2026' AND EntitySyncId IN (@S1, @S2, @S3)
                ORDER BY ServerVersion ASC;";
            cmd.Parameters.AddWithValue("@S1", syncId1);
            cmd.Parameters.AddWithValue("@S2", syncId2);
            cmd.Parameters.AddWithValue("@S3", syncId3);

            var feedRows = new List<(long Version, Guid SyncId)>();
            await using (var reader = await cmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    feedRows.Add((reader.GetInt64(0), reader.GetGuid(1)));
                }
            }

            Assert.Equal(3, feedRows.Count);
            Assert.Equal(startVersion + 1, feedRows[0].Version);
            Assert.Equal(startVersion + 2, feedRows[1].Version);
            Assert.Equal(startVersion + 3, feedRows[2].Version);

            // Deterministic order: EntitySyncId ASC
            Assert.Equal(syncId1, feedRows[0].SyncId);
            Assert.Equal(syncId2, feedRows[1].SyncId);
            Assert.Equal(syncId3, feedRows[2].SyncId);
        }

        #endregion

        #region Scenario 7 & 8: Direct SaveChanges Bypass vs Non-Daily Allowed

        [Fact]
        public async Task Scenario07_DirectDailySaveChangesBypass_BlockedFailClosed()
        {
            var syncProviderMock = new Mock<ISyncConnectionProvider>();
            syncProviderMock.Setup(p => p.IsLocalFirstEnabled).Returns(false);
            syncProviderMock.Setup(p => p.IsReadOnlyMode).Returns(false);
            syncProviderMock.Setup(p => p.GetSelectedDatabaseId()).Returns("2026");

            using var context = CreateContext(RemoteConnStr2026, trackingEnabled: true, syncProviderMock.Object);

            context.Set<Daily>().Add(new Daily
            {
                Name = "Direct SaveChanges Daily Bypass Attempt",
                DailyDate = DateTime.UtcNow.Date,
                Closed = false,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                SyncId = Guid.NewGuid()
            });

            // Calling context.SaveChangesAsync directly outside AuthoritativeWriteScopeContext must be blocked
            await Assert.ThrowsAsync<AuthoritativeWriteScopeException>(async () =>
            {
                await context.SaveChangesAsync();
            });
        }

        [Fact]
        public async Task Scenario08_DirectNonDailySaveChanges_RemainsAllowed()
        {
            var syncProviderMock = new Mock<ISyncConnectionProvider>();
            syncProviderMock.Setup(p => p.IsLocalFirstEnabled).Returns(false);
            syncProviderMock.Setup(p => p.IsReadOnlyMode).Returns(false);
            syncProviderMock.Setup(p => p.GetSelectedDatabaseId()).Returns("2026");

            using var context = CreateContext(RemoteConnStr2026, trackingEnabled: true, syncProviderMock.Object);

            context.Departments.Add(new Department
            {
                Name = "Direct Department Allowed"
            });

            // Direct SaveChanges on non-Daily entity is allowed
            var saved = await context.SaveChangesAsync();
            Assert.True(saved > 0);
        }

        #endregion

        #region Scenario 9: Missing ServerState Fails Closed

        [Fact]
        public async Task Scenario09_MissingServerState_EntireTransactionFailsClosedAndRollsBack()
        {
            // We use year 2027 and temporarily remove its ServerState record
            await using var conn = new SqlConnection(RemoteConnStr2027);
            await conn.OpenAsync();
            await using var delStateCmd = conn.CreateCommand();
            delStateCmd.CommandText = "DELETE FROM [sync].[ServerState] WHERE DatabaseId = '2027';";
            await delStateCmd.ExecuteNonQueryAsync();

            try
            {
                var syncProviderMock = new Mock<ISyncConnectionProvider>();
                syncProviderMock.Setup(p => p.IsLocalFirstEnabled).Returns(false);
                syncProviderMock.Setup(p => p.IsReadOnlyMode).Returns(false);
                syncProviderMock.Setup(p => p.GetSelectedDatabaseId()).Returns("2027");

                using var context = CreateContext(RemoteConnStr2027, trackingEnabled: true, syncProviderMock.Object);
                var uow = CreateUnitOfWork(context, syncProviderMock.Object);

                var syncId = Guid.NewGuid();
                context.Set<Daily>().Add(new Daily
                {
                    Name = "Daily With Missing ServerState",
                    DailyDate = DateTime.UtcNow.Date,
                    Closed = false,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow,
                    SyncId = syncId
                });

                var ex = await Assert.ThrowsAsync<AuthoritativeTrackingException>(async () =>
                {
                    await uow.SaveChangesAsync();
                });

                Assert.Contains("ServerState record not found", ex.Message);

                // Verify business Daily was NOT committed (rolled back)
                await using var checkCmd = conn.CreateCommand();
                checkCmd.CommandText = "SELECT COUNT(1) FROM [dbo].[Daily] WHERE SyncId = @SyncId;";
                checkCmd.Parameters.AddWithValue("@SyncId", syncId);
                var count = Convert.ToInt32(await checkCmd.ExecuteScalarAsync());
                Assert.Equal(0, count);
            }
            finally
            {
                // Restore ServerState for 2027
                await using var restoreCmd = conn.CreateCommand();
                restoreCmd.CommandText = @"
                    IF NOT EXISTS (SELECT 1 FROM [sync].[ServerState] WHERE DatabaseId = '2027')
                        INSERT INTO [sync].[ServerState] ([DatabaseId], [CurrentVersion], [LastUpdatedUtc])
                        VALUES ('2027', 0, SYSUTCDATETIME());";
                await restoreCmd.ExecuteNonQueryAsync();
            }
        }

        #endregion

        #region Scenario 10: Wrong Database Year Binding Fail Closed

        [Fact]
        public async Task Scenario10_WrongDatabaseYearBinding_RejectedFailClosed()
        {
            var syncProviderMock = new Mock<ISyncConnectionProvider>();
            syncProviderMock.Setup(p => p.IsLocalFirstEnabled).Returns(false);
            syncProviderMock.Setup(p => p.IsReadOnlyMode).Returns(false);
            syncProviderMock.Setup(p => p.GetSelectedDatabaseId()).Returns("2027"); // Mismatch: 2027 selected

            // Connecting to 2026 database with 2027 canonical ID
            using var context = CreateContext(RemoteConnStr2026, trackingEnabled: true, syncProviderMock.Object);
            var uow = CreateUnitOfWork(context, syncProviderMock.Object);

            var syncId = Guid.NewGuid();
            context.Set<Daily>().Add(new Daily
            {
                Name = "Mismatched Binding Daily",
                DailyDate = DateTime.UtcNow.Date,
                Closed = false,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                SyncId = syncId
            });

            var ex = await Assert.ThrowsAsync<AuthoritativeBindingException>(async () =>
            {
                await uow.SaveChangesAsync();
            });

            Assert.Contains("Year mismatch", ex.Message);
        }

        #endregion

        #region Scenario 11: Tombstone Resurrection Prevention

        [Fact]
        public async Task Scenario11_TombstoneResurrectionPrevention_InsertWithExistingTombstone_FailsClosed()
        {
            var syncProviderMock = new Mock<ISyncConnectionProvider>();
            syncProviderMock.Setup(p => p.IsLocalFirstEnabled).Returns(false);
            syncProviderMock.Setup(p => p.IsReadOnlyMode).Returns(false);
            syncProviderMock.Setup(p => p.GetSelectedDatabaseId()).Returns("2026");

            var syncId = Guid.NewGuid();

            // Pre-seed a tombstone for this SyncId
            await using var conn = new SqlConnection(RemoteConnStr2026);
            await conn.OpenAsync();
            await using var seedTombCmd = conn.CreateCommand();
            seedTombCmd.CommandText = @"
                INSERT INTO [sync].[Tombstones] ([DatabaseId], [EntityType], [EntitySyncId], [NaturalKey], [ServerVersion], [DeletedAtUtc])
                VALUES ('2026', 'Daily', @SyncId, NULL, 999, SYSUTCDATETIME());";
            seedTombCmd.Parameters.AddWithValue("@SyncId", syncId);
            await seedTombCmd.ExecuteNonQueryAsync();

            using var context = CreateContext(RemoteConnStr2026, trackingEnabled: true, syncProviderMock.Object);
            var uow = CreateUnitOfWork(context, syncProviderMock.Object);

            context.Set<Daily>().Add(new Daily
            {
                Name = "Resurrected Daily Attempt",
                DailyDate = DateTime.UtcNow.Date,
                Closed = false,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                SyncId = syncId
            });

            var ex = await Assert.ThrowsAsync<AuthoritativeTrackingException>(async () =>
            {
                await uow.SaveChangesAsync();
            });

            Assert.Contains("a tombstone already exists", ex.Message);

            // Verify Daily was NOT inserted (fail closed)
            await using var checkCmd = conn.CreateCommand();
            checkCmd.CommandText = "SELECT COUNT(1) FROM [dbo].[Daily] WHERE SyncId = @SyncId;";
            checkCmd.Parameters.AddWithValue("@SyncId", syncId);
            var count = Convert.ToInt32(await checkCmd.ExecuteScalarAsync());
            Assert.Equal(0, count);
        }

        #endregion

        #region Scenario 12: Push Regression & No Double Tracking

        [Fact]
        public async Task Scenario12_PushRegression_NoDoubleTracking_ExactSingleVersionIncrement()
        {
            var syncId = Guid.NewGuid();
            var devId = Guid.NewGuid();
            var clientOpId = Guid.NewGuid();

            var payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                entityType = "Daily",
                operationType = "INSERT",
                databaseId = "2026",
                deviceId = devId,
                entitySyncId = syncId,
                baseServerVersion = 0,
                entityData = new
                {
                    SyncId = syncId,
                    Name = "Pushed Daily via Push Coordinator",
                    DailyDate = "2026-06-01T00:00:00Z",
                    Closed = false,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow.ToString("O")
                }
            });
            var hash = LocalOutboxPushService.ComputeRequestHash("2026", devId, "Daily.Insert", "Daily", syncId, payload);

            var outbox = new LocalOutbox
            {
                DatabaseId = "2026",
                ClientOperationId = clientOpId,
                CommandName = "Daily.Insert",
                AggregateType = "Daily",
                EntitySyncId = syncId,
                PayloadJson = payload,
                CreatedAtUtc = DateTime.UtcNow,
                Status = "IN_PROGRESS"
            };

            await using var conn = new SqlConnection(RemoteConnStr2026);
            await conn.OpenAsync();

            var curVer = await GetServerVersionAsync(RemoteConnStr2026, "2026");

            // Execute push coordinator directly (Slice 4.3C)
            var result = await _pushCoordinator.ApplyOperationAsync(conn, "2026", outbox, curVer, hash, devId, CancellationToken.None);

            Assert.False(result.IsReplay);
            Assert.Equal(curVer + 1, result.ServerVersion);

            // Verify ServerState incremented exactly once (curVer + 1)
            var postPushVer = await GetServerVersionAsync(RemoteConnStr2026, "2026");
            Assert.Equal(curVer + 1, postPushVer);

            // Verify exactly ONE feed entry created with OriginDeviceId == devId (NOT Guid.Empty)
            await using var feedCmd = conn.CreateCommand();
            feedCmd.CommandText = @"
                SELECT ServerVersion, OperationType, OriginDeviceId
                FROM [sync].[ServerChangeFeed]
                WHERE DatabaseId = '2026' AND EntitySyncId = @EntitySyncId;";
            feedCmd.Parameters.AddWithValue("@EntitySyncId", syncId);

            var feedRows = new List<(long Version, string OpType, Guid DevId)>();
            await using (var reader = await feedCmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    feedRows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetGuid(2)));
                }
            }

            Assert.Single(feedRows);
            Assert.Equal(curVer + 1, feedRows[0].Version);
            Assert.Equal("INSERT", feedRows[0].OpType);
            Assert.Equal(devId, feedRows[0].DevId);
            Assert.NotEqual(Guid.Empty, feedRows[0].DevId); // Origin is client DeviceId, proving no double tracking!
        }

        #endregion

        #region Scenario 13: Real Concurrent Online <-> Push Test (Zero Deadlock)

        [Fact]
        public async Task Scenario13_ConcurrentOnlineVsPush_NoDeadlock_DeterministicOutcome()
        {
            // Run multiple concurrent races to test lock arbitration under contention
            for (int iteration = 1; iteration <= 3; iteration++)
            {
                var baseDailySyncId = Guid.NewGuid();
                var devId = Guid.NewGuid();
                var clientOpId = Guid.NewGuid();

                // 1. Pre-seed a base Daily row directly on remote DB
                var startingVersion = await GetServerVersionAsync(RemoteConnStr2026, "2026");
                await using (var seedConn = new SqlConnection(RemoteConnStr2026))
                {
                    await seedConn.OpenAsync();
                    await using var seedCmd = seedConn.CreateCommand();
                    seedCmd.CommandText = @"
                        INSERT INTO [dbo].[Daily] ([Name], [DailyDate], [Closed], [CreatedBy], [CreatedAt], [IsActive], [SyncId])
                        VALUES (@Name, '2026-06-01T00:00:00Z', 0, 'Seed', SYSUTCDATETIME(), 1, @SyncId);";
                    seedCmd.Parameters.AddWithValue("@Name", $"Base Daily {iteration}");
                    seedCmd.Parameters.AddWithValue("@SyncId", baseDailySyncId);
                    await seedCmd.ExecuteNonQueryAsync();
                }

                // 2. Prepare Session 1: Online authoritative write via UnitOfWork
                var syncProviderMock = new Mock<ISyncConnectionProvider>();
                syncProviderMock.Setup(p => p.IsLocalFirstEnabled).Returns(false);
                syncProviderMock.Setup(p => p.IsReadOnlyMode).Returns(false);
                syncProviderMock.Setup(p => p.GetSelectedDatabaseId()).Returns("2026");

                using var onlineContext = CreateContext(RemoteConnStr2026, trackingEnabled: true, syncProviderMock.Object);
                var onlineUow = CreateUnitOfWork(onlineContext, syncProviderMock.Object);

                var onlineDaily = await onlineContext.Set<Daily>().FirstAsync(d => d.SyncId == baseDailySyncId);
                onlineDaily.Name = $"Updated By Online Race {iteration}";

                // 3. Prepare Session 2: Offline Push mutation via AzurePushTransactionCoordinator
                var pushPayload = System.Text.Json.JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    entityType = "Daily",
                    operationType = "UPDATE",
                    databaseId = "2026",
                    deviceId = devId,
                    entitySyncId = baseDailySyncId,
                    baseServerVersion = startingVersion,
                    entityData = new
                    {
                        SyncId = baseDailySyncId,
                        Name = $"Updated By Push Race {iteration}",
                        DailyDate = "2026-06-01T00:00:00Z",
                        Closed = false,
                        IsActive = true,
                        UpdatedAt = DateTime.UtcNow.ToString("O")
                    }
                });
                var pushHash = LocalOutboxPushService.ComputeRequestHash("2026", devId, "Daily.Update", "Daily", baseDailySyncId, pushPayload);
                var outbox = new LocalOutbox
                {
                    DatabaseId = "2026",
                    ClientOperationId = clientOpId,
                    CommandName = "Daily.Update",
                    AggregateType = "Daily",
                    EntitySyncId = baseDailySyncId,
                    PayloadJson = pushPayload,
                    CreatedAtUtc = DateTime.UtcNow,
                    Status = "IN_PROGRESS"
                };

                // 4. Concurrently launch both operations across separate connections with 15s timeout
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var ct = cts.Token;

                Exception? onlineException = null;
                Exception? pushException = null;
                RemoteApplyResult? pushResult = null;

                // Use TaskCompletionSource to coordinate simultaneous start
                var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                var onlineTask = Task.Run(async () =>
                {
                    await barrier.Task;
                    try
                    {
                        await onlineUow.SaveChangesAsync(ct);
                    }
                    catch (Exception ex)
                    {
                        onlineException = ex;
                    }
                }, ct);

                var pushTask = Task.Run(async () =>
                {
                    await barrier.Task;
                    try
                    {
                        await using var pushConn = new SqlConnection(RemoteConnStr2026);
                        await pushConn.OpenAsync(ct);
                        pushResult = await _pushCoordinator.ApplyOperationAsync(pushConn, "2026", outbox, startingVersion, pushHash, devId, ct);
                    }
                    catch (Exception ex)
                    {
                        pushException = ex;
                    }
                }, ct);

                // Release barrier to trigger both operations concurrently
                barrier.SetResult();
                await Task.WhenAll(onlineTask, pushTask);

                // 5. Verify NO SQL Error 1205 (Deadlock victim) on either side
                if (onlineException != null)
                {
                    var sqlEx = ExtractSqlException(onlineException);
                    if (sqlEx != null)
                    {
                        Assert.NotEqual(1205, sqlEx.Number);
                    }
                }
                if (pushException != null)
                {
                    var sqlEx = ExtractSqlException(pushException);
                    if (sqlEx != null)
                    {
                        Assert.NotEqual(1205, sqlEx.Number);
                    }
                }

                // 6. Validate outcome: Exactly one of the two legitimate outcomes occurred
                var finalVersion = await GetServerVersionAsync(RemoteConnStr2026, "2026");

                if (onlineException == null && pushException is SyncVersionConflictException conflictEx)
                {
                    // OUTCOME A: Online acquired ServerState first
                    // Online advanced version -> Push detected SYNC_VERSION_CONFLICT -> zero Push business write
                    Assert.Equal("SYNC_VERSION_CONFLICT", conflictEx.ErrorCode);
                    Assert.Equal(startingVersion + 1, finalVersion);

                    // Verify Daily holds Online's mutation
                    await using var verifyConn = new SqlConnection(RemoteConnStr2026);
                    await verifyConn.OpenAsync();
                    await using var checkCmd = verifyConn.CreateCommand();
                    checkCmd.CommandText = "SELECT Name FROM [dbo].[Daily] WHERE SyncId = @SyncId;";
                    checkCmd.Parameters.AddWithValue("@SyncId", baseDailySyncId);
                    var actualName = (string?)await checkCmd.ExecuteScalarAsync();
                    Assert.Equal($"Updated By Online Race {iteration}", actualName);

                    // Verify ChangeFeed has exactly 1 entry for this version with Server origin (Guid.Empty)
                    await using var feedCmd = verifyConn.CreateCommand();
                    feedCmd.CommandText = @"
                        SELECT ServerVersion, OriginDeviceId
                        FROM [sync].[ServerChangeFeed]
                        WHERE DatabaseId = '2026' AND ServerVersion = @Ver;";
                    feedCmd.Parameters.AddWithValue("@Ver", startingVersion + 1);
                    await using (var reader = await feedCmd.ExecuteReaderAsync())
                    {
                        Assert.True(await reader.ReadAsync());
                        Assert.Equal(Guid.Empty, reader.GetGuid(1));
                        Assert.False(await reader.ReadAsync());
                    }

                    // Verify ProcessedOperations has NO success record for Push clientOpId
                    await using var procCmd = verifyConn.CreateCommand();
                    procCmd.CommandText = "SELECT COUNT(1) FROM [sync].[ProcessedOperations] WHERE DatabaseId = '2026' AND ClientOperationId = @OpId;";
                    procCmd.Parameters.AddWithValue("@OpId", clientOpId);
                    var procCount = Convert.ToInt32(await procCmd.ExecuteScalarAsync());
                    Assert.Equal(0, procCount);
                }
                else if (onlineException is AuthoritativeConcurrencyConflictException concConflictEx && pushException == null)
                {
                    // OUTCOME B: Push acquired ServerState first
                    // Push succeeded (version + 1) -> Online waited safely -> Online detected stale OriginalSnapshot -> FAILED CLOSED
                    Assert.Equal("AUTHORITATIVE_CONCURRENCY_CONFLICT", concConflictEx.ErrorCode);
                    Assert.NotNull(pushResult);
                    Assert.False(pushResult!.IsReplay);
                    Assert.Equal(startingVersion + 1, pushResult.ServerVersion);
                    Assert.Equal(startingVersion + 1, finalVersion); // ServerVersion remains V+1

                    // Daily was updated by Push, and Online was rejected (NO lost update!)
                    await using var verifyConn = new SqlConnection(RemoteConnStr2026);
                    await verifyConn.OpenAsync();
                    await using var checkCmd = verifyConn.CreateCommand();
                    checkCmd.CommandText = "SELECT Name FROM [dbo].[Daily] WHERE SyncId = @SyncId;";
                    checkCmd.Parameters.AddWithValue("@SyncId", baseDailySyncId);
                    var actualName = (string?)await checkCmd.ExecuteScalarAsync();
                    Assert.Equal($"Updated By Push Race {iteration}", actualName);

                    // Verify exactly ONE feed entry in ChangeFeed at V+1 from Push (zero Online feed)
                    await using var feedCmd = verifyConn.CreateCommand();
                    feedCmd.CommandText = @"
                        SELECT ServerVersion, OriginDeviceId
                        FROM [sync].[ServerChangeFeed]
                        WHERE DatabaseId = '2026' AND ServerVersion >= @V1
                        ORDER BY ServerVersion ASC;";
                    feedCmd.Parameters.AddWithValue("@V1", startingVersion + 1);

                    var feedList = new List<(long Version, Guid DevId)>();
                    await using (var reader = await feedCmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            feedList.Add((reader.GetInt64(0), reader.GetGuid(1)));
                        }
                    }

                    Assert.Single(feedList);
                    Assert.Equal(startingVersion + 1, feedList[0].Version);
                    Assert.Equal(devId, feedList[0].DevId); // From Push

                    // Verify ProcessedOperations contains SUCCESS for Push
                    await using var procCmd = verifyConn.CreateCommand();
                    procCmd.CommandText = "SELECT ResultStatus FROM [sync].[ProcessedOperations] WHERE DatabaseId = '2026' AND ClientOperationId = @OpId;";
                    procCmd.Parameters.AddWithValue("@OpId", clientOpId);
                    var resultStatus = (string?)await procCmd.ExecuteScalarAsync();
                    Assert.Equal("SUCCESS", resultStatus);
                }
                else
                {
                    Assert.Fail($"Unexpected outcome in race {iteration}: OnlineException={onlineException?.GetType().Name} ({onlineException?.Message}), PushException={pushException?.GetType().Name} ({pushException?.Message})");
                }
            }
        }

        #endregion

        #region Scenario 14: Real Concurrent Online <-> Online Concurrency Test

        [Fact]
        public async Task Scenario14_ConcurrentOnlineVsOnline_StaleWriteRejection_ExactlyOneSucceeds()
        {
            for (int iteration = 1; iteration <= 3; iteration++)
            {
                var baseDailySyncId = Guid.NewGuid();

                // 1. Pre-seed a base Daily row on remote DB
                var startingVersion = await GetServerVersionAsync(RemoteConnStr2026, "2026");
                await using (var seedConn = new SqlConnection(RemoteConnStr2026))
                {
                    await seedConn.OpenAsync();
                    await using var seedCmd = seedConn.CreateCommand();
                    seedCmd.CommandText = @"
                        INSERT INTO [dbo].[Daily] ([Name], [DailyDate], [Closed], [CreatedBy], [CreatedAt], [IsActive], [SyncId])
                        VALUES (@Name, '2026-06-01T00:00:00Z', 0, 'Seed', SYSUTCDATETIME(), 1, @SyncId);";
                    seedCmd.Parameters.AddWithValue("@Name", $"Base Daily OnlineRace {iteration}");
                    seedCmd.Parameters.AddWithValue("@SyncId", baseDailySyncId);
                    await seedCmd.ExecuteNonQueryAsync();
                }

                // 2. Prepare Session A
                var syncProviderMockA = new Mock<ISyncConnectionProvider>();
                syncProviderMockA.Setup(p => p.IsLocalFirstEnabled).Returns(false);
                syncProviderMockA.Setup(p => p.IsReadOnlyMode).Returns(false);
                syncProviderMockA.Setup(p => p.GetSelectedDatabaseId()).Returns("2026");

                using var contextA = CreateContext(RemoteConnStr2026, trackingEnabled: true, syncProviderMockA.Object);
                var uowA = CreateUnitOfWork(contextA, syncProviderMockA.Object);
                var dailyA = await contextA.Set<Daily>().FirstAsync(d => d.SyncId == baseDailySyncId);
                dailyA.Name = $"Updated By Session A {iteration}";

                // 3. Prepare Session B (reads same base Daily in same initial state)
                var syncProviderMockB = new Mock<ISyncConnectionProvider>();
                syncProviderMockB.Setup(p => p.IsLocalFirstEnabled).Returns(false);
                syncProviderMockB.Setup(p => p.IsReadOnlyMode).Returns(false);
                syncProviderMockB.Setup(p => p.GetSelectedDatabaseId()).Returns("2026");

                using var contextB = CreateContext(RemoteConnStr2026, trackingEnabled: true, syncProviderMockB.Object);
                var uowB = CreateUnitOfWork(contextB, syncProviderMockB.Object);
                var dailyB = await contextB.Set<Daily>().FirstAsync(d => d.SyncId == baseDailySyncId);
                dailyB.Name = $"Updated By Session B {iteration}";

                // 4. Concurrently launch both SaveChanges with 15s timeout
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var ct = cts.Token;

                Exception? exA = null;
                Exception? exB = null;

                var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                var taskA = Task.Run(async () =>
                {
                    await barrier.Task;
                    try
                    {
                        await uowA.SaveChangesAsync(ct);
                    }
                    catch (Exception ex)
                    {
                        exA = ex;
                    }
                }, ct);

                var taskB = Task.Run(async () =>
                {
                    await barrier.Task;
                    try
                    {
                        await uowB.SaveChangesAsync(ct);
                    }
                    catch (Exception ex)
                    {
                        exB = ex;
                    }
                }, ct);

                barrier.SetResult();
                await Task.WhenAll(taskA, taskB);

                // 5. Zero SQL 1205 deadlocks
                if (exA != null)
                {
                    var sqlEx = ExtractSqlException(exA);
                    if (sqlEx != null) Assert.NotEqual(1205, sqlEx.Number);
                }
                if (exB != null)
                {
                    var sqlEx = ExtractSqlException(exB);
                    if (sqlEx != null) Assert.NotEqual(1205, sqlEx.Number);
                }

                // 6. Exactly ONE succeeds, the second fails with AUTHORITATIVE_CONCURRENCY_CONFLICT
                bool aWon = exA == null && exB is AuthoritativeConcurrencyConflictException conflictB && conflictB.ErrorCode == "AUTHORITATIVE_CONCURRENCY_CONFLICT";
                bool bWon = exB == null && exA is AuthoritativeConcurrencyConflictException conflictA && conflictA.ErrorCode == "AUTHORITATIVE_CONCURRENCY_CONFLICT";

                Assert.True(aWon ^ bWon, $"Exactly one writer must succeed. exA={exA?.Message}, exB={exB?.Message}");

                // 7. Verify ServerVersion advanced by exactly +1
                var finalVersion = await GetServerVersionAsync(RemoteConnStr2026, "2026");
                Assert.Equal(startingVersion + 1, finalVersion);

                // 8. Verify exactly ONE ChangeFeed row for startingVersion + 1
                await using var verifyConn = new SqlConnection(RemoteConnStr2026);
                await verifyConn.OpenAsync();
                await using var feedCmd = verifyConn.CreateCommand();
                feedCmd.CommandText = @"
                    SELECT ServerVersion, OriginDeviceId
                    FROM [sync].[ServerChangeFeed]
                    WHERE DatabaseId = '2026' AND ServerVersion = @Ver;";
                feedCmd.Parameters.AddWithValue("@Ver", startingVersion + 1);

                int feedCount = 0;
                await using (var reader = await feedCmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        feedCount++;
                        Assert.Equal(Guid.Empty, reader.GetGuid(1));
                    }
                }
                Assert.Equal(1, feedCount);

                // 9. Verify final Daily matches the winner's value (no silent overwrite!)
                await using var checkCmd = verifyConn.CreateCommand();
                checkCmd.CommandText = "SELECT Name FROM [dbo].[Daily] WHERE SyncId = @SyncId;";
                checkCmd.Parameters.AddWithValue("@SyncId", baseDailySyncId);
                var actualName = (string?)await checkCmd.ExecuteScalarAsync();

                string expectedWinnerName = aWon ? $"Updated By Session A {iteration}" : $"Updated By Session B {iteration}";
                Assert.Equal(expectedWinnerName, actualName);
            }
        }

        #endregion

        #region Scenario 15: Multi-Mutation Batch Atomicity on Concurrency Conflict

        [Fact]
        public async Task Scenario15_MultiMutationBatch_StaleItemFailsEntireBatch_RollsBackAll()
        {
            var daily1SyncId = Guid.NewGuid();
            var daily2SyncId = Guid.NewGuid();
            var daily3SyncId = Guid.NewGuid();

            // 1. Pre-seed Daily 1 and Daily 2 directly in DB
            var startingVersion = await GetServerVersionAsync(RemoteConnStr2026, "2026");
            await using (var seedConn = new SqlConnection(RemoteConnStr2026))
            {
                await seedConn.OpenAsync();
                await using var seedCmd = seedConn.CreateCommand();
                seedCmd.CommandText = @"
                    INSERT INTO [dbo].[Daily] ([Name], [DailyDate], [Closed], [CreatedBy], [CreatedAt], [IsActive], [SyncId])
                    VALUES 
                        ('Original Daily 1', '2026-06-01T00:00:00Z', 0, 'Seed', SYSUTCDATETIME(), 1, @SyncId1),
                        ('Original Daily 2', '2026-06-01T00:00:00Z', 0, 'Seed', SYSUTCDATETIME(), 1, @SyncId2);";
                seedCmd.Parameters.AddWithValue("@SyncId1", daily1SyncId);
                seedCmd.Parameters.AddWithValue("@SyncId2", daily2SyncId);
                await seedCmd.ExecuteNonQueryAsync();
            }

            // 2. Prepare UnitOfWork context that loads both Daily 1 and Daily 2
            var syncProviderMock = new Mock<ISyncConnectionProvider>();
            syncProviderMock.Setup(p => p.IsLocalFirstEnabled).Returns(false);
            syncProviderMock.Setup(p => p.IsReadOnlyMode).Returns(false);
            syncProviderMock.Setup(p => p.GetSelectedDatabaseId()).Returns("2026");

            using var context = CreateContext(RemoteConnStr2026, trackingEnabled: true, syncProviderMock.Object);
            var uow = CreateUnitOfWork(context, syncProviderMock.Object);

            var d1 = await context.Set<Daily>().FirstAsync(d => d.SyncId == daily1SyncId);
            var d2 = await context.Set<Daily>().FirstAsync(d => d.SyncId == daily2SyncId);

            // 3. External concurrent actor modifies Daily 1 in DB, making Context's snapshot of Daily 1 stale!
            await using (var extConn = new SqlConnection(RemoteConnStr2026))
            {
                await extConn.OpenAsync();
                await using var extCmd = extConn.CreateCommand();
                extCmd.CommandText = @"
                    UPDATE [dbo].[Daily]
                    SET [Name] = 'External Actor Concurrently Modified Daily 1', [UpdatedAt] = SYSUTCDATETIME()
                    WHERE [SyncId] = @SyncId;";
                extCmd.Parameters.AddWithValue("@SyncId", daily1SyncId);
                await extCmd.ExecuteNonQueryAsync();
            }

            // 4. Batch in Context:
            // - Daily 1: Updated (stale!)
            // - Daily 2: Updated (fresh/valid)
            // - Daily 3: Added (fresh/valid)
            d1.Name = "Context Trying To Update Stale Daily 1";
            d2.Name = "Context Updating Valid Daily 2";
            context.Set<Daily>().Add(new Daily
            {
                Name = "Context Adding Valid Daily 3",
                DailyDate = DateTime.UtcNow.Date,
                Closed = false,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                SyncId = daily3SyncId
            });

            // 5. Calling SaveChangesAsync must FAIL CLOSED on the entire batch!
            var ex = await Assert.ThrowsAsync<AuthoritativeConcurrencyConflictException>(async () =>
            {
                await uow.SaveChangesAsync();
            });

            Assert.Equal("AUTHORITATIVE_CONCURRENCY_CONFLICT", ex.ErrorCode);
            Assert.Contains(daily1SyncId.ToString(), ex.Message);

            // 6. Verify ServerState.CurrentVersion did NOT advance (zero increments)
            var finalVersion = await GetServerVersionAsync(RemoteConnStr2026, "2026");
            Assert.Equal(startingVersion, finalVersion);

            // 7. Verify zero new feed rows created for this batch
            await using (var verifyConn = new SqlConnection(RemoteConnStr2026))
            {
                await verifyConn.OpenAsync();
                await using var feedCmd = verifyConn.CreateCommand();
                feedCmd.CommandText = @"
                    SELECT COUNT(1) FROM [sync].[ServerChangeFeed]
                    WHERE DatabaseId = '2026' AND ServerVersion > @StartingVer;";
                feedCmd.Parameters.AddWithValue("@StartingVer", startingVersion);
                var feedCount = Convert.ToInt32(await feedCmd.ExecuteScalarAsync());
                Assert.Equal(0, feedCount);

                // 8. Verify Daily 1 retained external actor's value
                await using var checkD1Cmd = verifyConn.CreateCommand();
                checkD1Cmd.CommandText = "SELECT Name FROM [dbo].[Daily] WHERE SyncId = @SyncId;";
                checkD1Cmd.Parameters.AddWithValue("@SyncId", daily1SyncId);
                var actualD1Name = (string?)await checkD1Cmd.ExecuteScalarAsync();
                Assert.Equal("External Actor Concurrently Modified Daily 1", actualD1Name);

                // 9. Verify Daily 2 was NOT modified (retained original value)
                await using var checkD2Cmd = verifyConn.CreateCommand();
                checkD2Cmd.CommandText = "SELECT Name FROM [dbo].[Daily] WHERE SyncId = @SyncId;";
                checkD2Cmd.Parameters.AddWithValue("@SyncId", daily2SyncId);
                var actualD2Name = (string?)await checkD2Cmd.ExecuteScalarAsync();
                Assert.Equal("Original Daily 2", actualD2Name);

                // 10. Verify Daily 3 was NOT inserted
                await using var checkD3Cmd = verifyConn.CreateCommand();
                checkD3Cmd.CommandText = "SELECT COUNT(1) FROM [dbo].[Daily] WHERE SyncId = @SyncId;";
                checkD3Cmd.Parameters.AddWithValue("@SyncId", daily3SyncId);
                var d3Count = Convert.ToInt32(await checkD3Cmd.ExecuteScalarAsync());
                Assert.Equal(0, d3Count);

                // 11. Zero tombstones created
                await using var tombCmd = verifyConn.CreateCommand();
                tombCmd.CommandText = "SELECT COUNT(1) FROM [sync].[Tombstones] WHERE DatabaseId = '2026' AND EntitySyncId IN (@S1, @S2, @S3);";
                tombCmd.Parameters.AddWithValue("@S1", daily1SyncId);
                tombCmd.Parameters.AddWithValue("@S2", daily2SyncId);
                tombCmd.Parameters.AddWithValue("@S3", daily3SyncId);
                var tombCount = Convert.ToInt32(await tombCmd.ExecuteScalarAsync());
                Assert.Equal(0, tombCount);
            }
        }

        #endregion

        #region Scenario 16: Single-Tick DateTime Difference Triggers Concurrency Conflict

        [Fact]
        public async Task Scenario16_SingleTickDateTimeDifference_TriggersAuthoritativeConcurrencyConflict()
        {
            var dailySyncId = Guid.NewGuid();
            var startingVersion = await GetServerVersionAsync(RemoteConnStr2026, "2026");

            // Base timestamp T rounded to 100ns / 7 digits precision (SQL datetime2(7) resolution)
            var baseTime = new DateTime(2026, 6, 15, 10, 30, 0, DateTimeKind.Utc);
            var updatedTimeT = baseTime.AddTicks(1234567); // Exact 7-decimal-digit fraction

            // 1. Pre-seed Daily in DB with UpdatedAt = T
            await using (var seedConn = new SqlConnection(RemoteConnStr2026))
            {
                await seedConn.OpenAsync();
                await using var seedCmd = seedConn.CreateCommand();
                seedCmd.CommandText = @"
                    INSERT INTO [dbo].[Daily] ([Name], [DailyDate], [Closed], [CreatedBy], [CreatedAt], [UpdatedAt], [UpdatedBy], [IsActive], [SyncId])
                    VALUES ('Initial Daily Name', '2026-06-01T00:00:00Z', 0, 'Seed', SYSUTCDATETIME(), @UpdatedAt, 'Seed', 1, @SyncId);";
                seedCmd.Parameters.Add(new SqlParameter("@UpdatedAt", SqlDbType.DateTime2) { Value = updatedTimeT });
                seedCmd.Parameters.AddWithValue("@SyncId", dailySyncId);
                await seedCmd.ExecuteNonQueryAsync();
            }

            // 2. UnitOfWork Online context reads Daily; OriginalSnapshot captures UpdatedAt = T
            var syncProviderMock = new Mock<ISyncConnectionProvider>();
            syncProviderMock.Setup(p => p.IsLocalFirstEnabled).Returns(false);
            syncProviderMock.Setup(p => p.IsReadOnlyMode).Returns(false);
            syncProviderMock.Setup(p => p.GetSelectedDatabaseId()).Returns("2026");

            using var context = CreateContext(RemoteConnStr2026, trackingEnabled: true, syncProviderMock.Object);
            var uow = CreateUnitOfWork(context, syncProviderMock.Object);

            var loadedDaily = await context.Set<Daily>().FirstAsync(d => d.SyncId == dailySyncId);
            Assert.Equal(updatedTimeT.Ticks, loadedDaily.UpdatedAt?.Ticks);

            // 3. Before SaveChanges, external connection updates ONLY: UpdatedAt = T.AddTicks(1)
            var updatedTimeTPlusOneTick = updatedTimeT.AddTicks(1);
            await using (var extConn = new SqlConnection(RemoteConnStr2026))
            {
                await extConn.OpenAsync();
                await using var extCmd = extConn.CreateCommand();
                extCmd.CommandText = @"
                    UPDATE [dbo].[Daily]
                    SET [UpdatedAt] = @NewUpdatedAt
                    WHERE [SyncId] = @SyncId;";
                extCmd.Parameters.Add(new SqlParameter("@NewUpdatedAt", SqlDbType.DateTime2) { Value = updatedTimeTPlusOneTick });
                extCmd.Parameters.AddWithValue("@SyncId", dailySyncId);
                await extCmd.ExecuteNonQueryAsync();
            }

            // 4. Online context tries to modify Name
            loadedDaily.Name = "Online Attempting Overwrite After Single Tick Shift";

            // 5. Must FAIL CLOSED with AuthoritativeConcurrencyConflictException
            var ex = await Assert.ThrowsAsync<AuthoritativeConcurrencyConflictException>(async () =>
            {
                await uow.SaveChangesAsync();
            });

            Assert.Equal("AUTHORITATIVE_CONCURRENCY_CONFLICT", ex.ErrorCode);
            Assert.Contains("UpdatedAt", ex.Message);

            // 6. Verify ServerState.CurrentVersion was NOT advanced
            var finalVersion = await GetServerVersionAsync(RemoteConnStr2026, "2026");
            Assert.Equal(startingVersion, finalVersion);

            // 7. Verify zero new feed rows created for this failed write
            await using (var verifyConn = new SqlConnection(RemoteConnStr2026))
            {
                await verifyConn.OpenAsync();
                await using var feedCmd = verifyConn.CreateCommand();
                feedCmd.CommandText = @"
                    SELECT COUNT(1) FROM [sync].[ServerChangeFeed]
                    WHERE DatabaseId = '2026' AND ServerVersion > @StartingVer;";
                feedCmd.Parameters.AddWithValue("@StartingVer", startingVersion);
                var feedCount = Convert.ToInt32(await feedCmd.ExecuteScalarAsync());
                Assert.Equal(0, feedCount);

                // 8. Verify Daily Name was NOT modified
                await using var checkCmd = verifyConn.CreateCommand();
                checkCmd.CommandText = "SELECT [Name], [UpdatedAt] FROM [dbo].[Daily] WHERE [SyncId] = @SyncId;";
                checkCmd.Parameters.AddWithValue("@SyncId", dailySyncId);
                await using var reader = await checkCmd.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                var actualName = reader.GetString(0);
                var actualUpdatedAt = reader.GetDateTime(1);

                Assert.Equal("Initial Daily Name", actualName);
                Assert.Equal(updatedTimeTPlusOneTick.Ticks, actualUpdatedAt.Ticks);
            }
        }

        #endregion

        private static SqlException? ExtractSqlException(Exception? ex)
        {
            while (ex != null)
            {
                if (ex is SqlException sqlEx) return sqlEx;
                ex = ex.InnerException;
            }
            return null;
        }
    }
}
