#nullable enable
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Auth.Infrastructure;
using Auth.Infrastructure.Sync;
using Auth.Infrastructure.Sync.Pull;
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
    [CollectionDefinition("DailyPullSqlIntegration", DisableParallelization = true)]
    public class DailyPullSqlIntegrationCollection
    {
    }

    [Trait("Category", "LocalDbRequired")]
    [Collection("DailyPullSqlIntegration")]
    public class DailyPullSqlIntegrationTests
    {
        private const string MasterConnStr = "Server=localhost;Database=master;Integrated Security=True;TrustServerCertificate=True;";

        #region Isolated Database Test Context

        private sealed class PullSqlTestContext : IAsyncDisposable
        {
            public string RemoteDbName { get; }
            public string LocalDbName { get; }
            public string RemoteConnStr { get; }
            public string LocalConnStr { get; }
            public string CanonicalDbId { get; }

            private PullSqlTestContext(string remoteDbName, string localDbName, string canonicalDbId)
            {
                RemoteDbName = remoteDbName;
                LocalDbName = localDbName;
                CanonicalDbId = canonicalDbId;
                RemoteConnStr = $"Server=localhost;Database={remoteDbName};Integrated Security=True;TrustServerCertificate=True;";
                LocalConnStr = $"Server=localhost;Database={localDbName};Integrated Security=True;TrustServerCertificate=True;";
            }

            public static async Task<PullSqlTestContext> CreateAsync(string canonicalDbId = "2026")
            {
                var suffix = Guid.NewGuid().ToString("N")[..8];
                var remoteName = $"TestRemoteDb_{suffix}";
                var localName = $"IProgramLocalDb{canonicalDbId}_Test";

                var ctx = new PullSqlTestContext(remoteName, localName, canonicalDbId);
                await ctx.InitializeDatabasesAsync();
                return ctx;
            }

            private async Task InitializeDatabasesAsync()
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

                // Initialize Remote schema & tables (matching production-relevant schema)
                await using (var remoteConn = new SqlConnection(RemoteConnStr))
                {
                    await remoteConn.OpenAsync();
                    await using var cmd = remoteConn.CreateCommand();
                    cmd.CommandText = @"
                        IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'sync')
                            EXEC('CREATE SCHEMA [sync]');

                        CREATE TABLE [dbo].[Daily] (
                            [Id] INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                            [Name] NVARCHAR(100) NOT NULL,
                            [DailyDate] DATETIME2 NOT NULL,
                            [Closed] BIT NOT NULL CONSTRAINT [DF_Remote_Daily_Closed] DEFAULT(0),
                            [CreatedBy] NVARCHAR(100) NULL,
                            [CreatedAt] DATETIME2 NOT NULL,
                            [UpdatedBy] NVARCHAR(100) NULL,
                            [UpdatedAt] DATETIME2 NULL,
                            [DeactivatedBy] NVARCHAR(100) NULL,
                            [DeactivatedAt] DATETIME2 NULL,
                            [IsActive] BIT NOT NULL CONSTRAINT [DF_Remote_Daily_IsActive] DEFAULT(1),
                            [SyncId] UNIQUEIDENTIFIER NOT NULL
                        );
                        CREATE UNIQUE INDEX [IX_Remote_Daily_SyncId] ON [dbo].[Daily]([SyncId]);

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
                        CREATE INDEX [IX_Tombstones_Pull] ON [sync].[Tombstones]([DatabaseId], [ServerVersion]);";
                    await cmd.ExecuteNonQueryAsync();

                    // Seed initial ServerState
                    await using var seedStateCmd = remoteConn.CreateCommand();
                    seedStateCmd.CommandText = "INSERT INTO [sync].[ServerState] (DatabaseId, CurrentVersion, LastUpdatedUtc) VALUES (@DatabaseId, 0, SYSUTCDATETIME());";
                    seedStateCmd.Parameters.AddWithValue("@DatabaseId", CanonicalDbId);
                    await seedStateCmd.ExecuteNonQueryAsync();
                }

                // Initialize Local schema & tables (matching production-relevant schema)
                await using (var localConn = new SqlConnection(LocalConnStr))
                {
                    await localConn.OpenAsync();
                    await using var cmd = localConn.CreateCommand();
                    cmd.CommandText = @"
                        IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'sync')
                            EXEC('CREATE SCHEMA [sync]');

                        -- Clean up any test triggers
                        IF OBJECT_ID('dbo.TR_Daily_RollbackTestFault', 'TR') IS NOT NULL DROP TRIGGER [dbo].[TR_Daily_RollbackTestFault];
                        IF OBJECT_ID('dbo.TR_Daily_RejectForTest', 'TR') IS NOT NULL DROP TRIGGER [dbo].[TR_Daily_RejectForTest];

                        IF OBJECT_ID('[dbo].[Daily]', 'U') IS NULL
                        BEGIN
                            CREATE TABLE [dbo].[Daily] (
                                [Id] INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                                [Name] NVARCHAR(100) NOT NULL,
                                [DailyDate] DATETIME2 NOT NULL,
                                [Closed] BIT NOT NULL CONSTRAINT [DF_Local_Daily_Closed] DEFAULT(0),
                                [CreatedBy] NVARCHAR(100) NULL,
                                [CreatedAt] DATETIME2 NOT NULL,
                                [UpdatedBy] NVARCHAR(100) NULL,
                                [UpdatedAt] DATETIME2 NULL,
                                [DeactivatedBy] NVARCHAR(100) NULL,
                                [DeactivatedAt] DATETIME2 NULL,
                                [IsActive] BIT NOT NULL CONSTRAINT [DF_Local_Daily_IsActive] DEFAULT(1),
                                [SyncId] UNIQUEIDENTIFIER NOT NULL
                            );
                            CREATE UNIQUE INDEX [IX_Local_Daily_SyncId] ON [dbo].[Daily]([SyncId]);
                        END
                        ELSE
                        BEGIN
                            DELETE FROM [dbo].[Daily];
                        END

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
                        END

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
                                [RetryCount] INT NOT NULL,
                                [LastError] NVARCHAR(MAX) NULL,
                                [CompletedAtUtc] DATETIME2 NULL,
                                [LockedUntilUtc] DATETIME2 NULL,
                                [LockToken] UNIQUEIDENTIFIER NULL
                            );
                            CREATE INDEX [IX_LocalOutbox_Queue] ON [sync].[LocalOutbox] ([DatabaseId], [Status], [CreatedAtUtc]);
                        END
                        ELSE
                        BEGIN
                            DELETE FROM [sync].[LocalOutbox];
                        END;";
                    await cmd.ExecuteNonQueryAsync();

                    // Seed initial LocalState
                    await using var seedLocalCmd = localConn.CreateCommand();
                    seedLocalCmd.CommandText = "INSERT INTO [sync].[LocalState] (DatabaseId, DeviceId, DeviceName, LastServerVersion) VALUES (@DatabaseId, NEWID(), 'IntegrationTestDevice', 0);";
                    seedLocalCmd.Parameters.AddWithValue("@DatabaseId", CanonicalDbId);
                    await seedLocalCmd.ExecuteNonQueryAsync();
                }
            }

            public async ValueTask DisposeAsync()
            {
                // Safety assertion: Refuse to drop any production database
                if (RemoteDbName.Equals("IProgramDb2026", StringComparison.OrdinalIgnoreCase) ||
                    RemoteDbName.Equals("IProgramDb2027", StringComparison.OrdinalIgnoreCase) ||
                    LocalDbName.Equals("IProgramLocalDb2026", StringComparison.OrdinalIgnoreCase) ||
                    LocalDbName.Equals("IProgramLocalDb2027", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("CRITICAL SAFETY VIOLATION: Refusing to drop production database.");
                }

                try
                {
                    // Clean local tables
                    await using (var localConn = new SqlConnection(LocalConnStr))
                    {
                        await localConn.OpenAsync();
                        await using var cleanCmd = localConn.CreateCommand();
                        cleanCmd.CommandText = @"
                            IF OBJECT_ID('[sync].[LocalOutbox]', 'U') IS NOT NULL DELETE FROM [sync].[LocalOutbox];
                            IF OBJECT_ID('[sync].[LocalState]', 'U') IS NOT NULL DELETE FROM [sync].[LocalState];
                            IF OBJECT_ID('[dbo].[Daily]', 'U') IS NOT NULL DELETE FROM [dbo].[Daily];
                            IF OBJECT_ID('dbo.TR_Daily_RollbackTestFault', 'TR') IS NOT NULL DROP TRIGGER [dbo].[TR_Daily_RollbackTestFault];
                            IF OBJECT_ID('dbo.TR_Daily_RejectForTest', 'TR') IS NOT NULL DROP TRIGGER [dbo].[TR_Daily_RejectForTest];";
                        await cleanCmd.ExecuteNonQueryAsync();
                    }

                    // Drop Remote isolated DB
                    await using var masterConn = new SqlConnection(MasterConnStr);
                    await masterConn.OpenAsync();

                    if (RemoteDbName.StartsWith("TestRemoteDb_"))
                    {
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
                    // Best-effort cleanup
                }
            }

            public IRemoteDatabaseConnectionFactory CreateRemoteFactory() => new TestRemoteDatabaseConnectionFactory(RemoteConnStr);
            public ISyncConnectionProvider CreateSyncProvider() => new TestSyncConnectionProvider(LocalConnStr, RemoteConnStr, CanonicalDbId);
            public ISyncConnectionProvider SyncConnectionProvider => CreateSyncProvider();
            public LocalPullLeaseManager CreateActualPullLeaseManager() => new(CreateSyncProvider(), NullLogger<LocalPullLeaseManager>.Instance);

            public ApplicationContext CreateApplicationContext()
            {
                var options = new DbContextOptionsBuilder<ApplicationContext>()
                    .UseSqlServer(LocalConnStr)
                    .Options;
                return new ApplicationContext(options);
            }

            public UnitOfWork CreateUnitOfWork(ApplicationContext context)
            {
                var configDict = new Dictionary<string, string?>
                {
                    { "LocalFirst:Enabled", "true" },
                    { "LocalFirst:ReadOnlyMode", "false" },
                    { "Sync:AuthoritativeTrackingEnabled", "false" }
                };
                var config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

                var mockSyncProvider = new Mock<ISyncConnectionProvider>();
                mockSyncProvider.Setup(p => p.IsLocalFirstEnabled).Returns(true);
                mockSyncProvider.Setup(p => p.IsReadOnlyMode).Returns(false);
                mockSyncProvider.Setup(p => p.GetSelectedDatabaseId()).Returns(CanonicalDbId);
                mockSyncProvider.Setup(p => p.GetLocalConnectionString(CanonicalDbId)).Returns(LocalConnStr);

                return new UnitOfWork(
                    context: context,
                    dbConnectionProvider: mockSyncProvider.Object,
                    authoritativeTracker: null,
                    bindingGuard: null,
                    configuration: config);
            }

            public LocalDailyPullService CreateActualPullService(IConfiguration? config = null)
            {
                var cfg = config ?? new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Sync:PullEnabled"] = "true",
                        ["Sync:PushEnabled"] = "false",
                        ["Sync:AuthoritativeTrackingEnabled"] = "false"
                    })
                    .Build();

                var syncProvider = CreateSyncProvider();
                var remoteFactory = CreateRemoteFactory();

                var leaseManager = new LocalPullLeaseManager(syncProvider, NullLogger<LocalPullLeaseManager>.Instance);
                var batchReader = new AzureFencedBatchReader(remoteFactory, NullLogger<AzureFencedBatchReader>.Instance);
                var coordinator = new LocalPullTransactionCoordinator(syncProvider, NullLogger<LocalPullTransactionCoordinator>.Instance);

                return new LocalDailyPullService(
                    syncProvider,
                    leaseManager,
                    batchReader,
                    coordinator,
                    cfg,
                    NullLogger<LocalDailyPullService>.Instance,
                    new LocalScopeBaselineService(NullLogger<LocalScopeBaselineService>.Instance));
            }
        }

        private sealed class TestRemoteDatabaseConnectionFactory : IRemoteDatabaseConnectionFactory
        {
            private readonly string _remoteConnStr;
            public TestRemoteDatabaseConnectionFactory(string remoteConnStr) => _remoteConnStr = remoteConnStr;

            public async Task<DbConnection> CreateOpenConnectionAsync(string databaseId, CancellationToken cancellationToken)
            {
                var conn = new SqlConnection(_remoteConnStr);
                await conn.OpenAsync(cancellationToken);
                return conn;
            }
        }

        private sealed class TestSyncConnectionProvider : ISyncConnectionProvider
        {
            private readonly string _localConnStr;
            private readonly string _remoteConnStr;
            private readonly string _canonicalDbId;

            public TestSyncConnectionProvider(string localConnStr, string remoteConnStr, string canonicalDbId = "2026")
            {
                _localConnStr = localConnStr;
                _remoteConnStr = remoteConnStr;
                _canonicalDbId = canonicalDbId;
            }

            public string GetSelectedDatabaseId() => _canonicalDbId;
            public string GetLocalConnectionString(string databaseId) => _localConnStr;
            public string GetRemoteConnectionString(string databaseId) => _remoteConnStr;
            public string GetManualSyncRemoteConnectionString(string databaseId) => _remoteConnStr;
            public bool IsReadOnlyMode => false;
            public bool IsLocalFirstEnabled => true;
            public bool IsLocalOnlyProduction => false;
            public bool IsLocalOnlyChangeCaptureEnabled => false;

            public LocalDatabaseBinding GetLocalBinding(string databaseId) => LocalDatabaseBinding.For(databaseId);
            public AzureDatabaseBinding GetRemoteBinding(string databaseId) => AzureDatabaseBinding.For(databaseId);
            public string GetConnectionString() => _localConnStr;
            public List<DatabaseInfo> GetAvailableDatabases() => new()
            {
                new DatabaseInfo { Id = "2026", Name = "2026" },
                new DatabaseInfo { Id = "2027", Name = "2027" }
            };
        }

        #endregion

        #region Scenario 1: Production Canary Shape (0 -> 2)

        [Fact]
        public async Task Scenario01_ProductionCanaryShape_ZeroTemporaryInsert_TerminalHardDelete_AdvancesVersion_IdempotentRetry()
        {
            await using var ctx = await PullSqlTestContext.CreateAsync("2026");
            var syncId = Guid.NewGuid();

            // 1. Seed Remote: ServerState = 2, Feed: v1 INSERT, v2 HARD_DELETE, Tombstones: row at v2, Daily: absent
            await using (var remoteConn = new SqlConnection(ctx.RemoteConnStr))
            {
                await remoteConn.OpenAsync();

                await using var cmd = remoteConn.CreateCommand();
                cmd.CommandText = @"
                    UPDATE [sync].[ServerState] SET CurrentVersion = 2, LastUpdatedUtc = SYSUTCDATETIME() WHERE DatabaseId = '2026';

                    INSERT INTO [sync].[ServerChangeFeed]
                    (ServerVersion, DatabaseId, EntityType, EntitySyncId, OperationType, OriginDeviceId, TimestampUtc)
                    VALUES
                    (1, '2026', 'Daily', @SyncId, 'INSERT', NEWID(), SYSUTCDATETIME()),
                    (2, '2026', 'Daily', @SyncId, 'HARD_DELETE', NEWID(), SYSUTCDATETIME());

                    INSERT INTO [sync].[Tombstones]
                    (DatabaseId, EntityType, EntitySyncId, ServerVersion, DeletedAtUtc)
                    VALUES
                    ('2026', 'Daily', @SyncId, 2, SYSUTCDATETIME());";
                cmd.Parameters.AddWithValue("@SyncId", syncId);
                await cmd.ExecuteNonQueryAsync();
            }

            // Verify local baseline: LastServerVersion = 0, Outbox = 0, Daily = 0
            var outboxBefore = await GetRowCountAsync(ctx.LocalConnStr, "[sync].[LocalOutbox]");
            Assert.Equal(0, outboxBefore);

            // 2. Execute actual LocalDailyPullService
            var pullService = ctx.CreateActualPullService();
            var result = await pullService.PullDailyChangesAsync(CancellationToken.None);

            // 3. Verify Pull results
            Assert.False(result.IsNoOp);
            Assert.Equal(0, result.PreviousWatermark);
            Assert.Equal(2, result.FinalServerVersion);
            Assert.Equal(1, result.TotalProcessed);
            Assert.Equal(1, result.Succeeded);
            Assert.Single(result.Operations);
            Assert.Equal("HARD_DELETE", result.Operations[0].OperationType);
            Assert.Equal("NO_OP", result.Operations[0].Status);

            // 4. Verify Local database: zero temporary insert, X remains absent, LastServerVersion = 2, Outbox unchanged
            await using (var localConn = new SqlConnection(ctx.LocalConnStr))
            {
                await localConn.OpenAsync();

                await using var checkCmd = localConn.CreateCommand();
                checkCmd.CommandText = "SELECT COUNT(*) FROM [dbo].[Daily] WHERE SyncId = @SyncId;";
                checkCmd.Parameters.AddWithValue("@SyncId", syncId);
                var localDailyCount = Convert.ToInt32(await checkCmd.ExecuteScalarAsync());
                Assert.Equal(0, localDailyCount);

                await using var verCmd = localConn.CreateCommand();
                verCmd.CommandText = "SELECT LastServerVersion FROM [sync].[LocalState] WHERE DatabaseId = '2026';";
                var localVer = Convert.ToInt64(await verCmd.ExecuteScalarAsync());
                Assert.Equal(2, localVer);
            }

            var outboxAfter = await GetRowCountAsync(ctx.LocalConnStr, "[sync].[LocalOutbox]");
            Assert.Equal(outboxBefore, outboxAfter);

            // 5. Retry actual Pull => NO-OP
            var retryResult = await pullService.PullDailyChangesAsync(CancellationToken.None);
            Assert.True(retryResult.IsNoOp);
            Assert.Equal(2, retryResult.PreviousWatermark);
            Assert.Equal(2, retryResult.FinalServerVersion);
            Assert.Equal(0, retryResult.TotalProcessed);
        }

        #endregion

        #region Scenario 2: Terminal INSERT

        [Fact]
        public async Task Scenario02_TerminalInsert_InsertsLocalRow_GeneratesLocalIdentity_AdvancesCheckpoint()
        {
            await using var ctx = await PullSqlTestContext.CreateAsync("2026");
            var syncId = Guid.NewGuid();
            var dailyDate = new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc);

            // Seed Remote: ServerState = 1, Feed: v1 INSERT, dbo.Daily row exists
            await using (var remoteConn = new SqlConnection(ctx.RemoteConnStr))
            {
                await remoteConn.OpenAsync();

                await using var cmd = remoteConn.CreateCommand();
                cmd.CommandText = @"
                    UPDATE [sync].[ServerState] SET CurrentVersion = 1, LastUpdatedUtc = SYSUTCDATETIME() WHERE DatabaseId = '2026';

                    INSERT INTO [sync].[ServerChangeFeed]
                    (ServerVersion, DatabaseId, EntityType, EntitySyncId, OperationType, OriginDeviceId, TimestampUtc)
                    VALUES
                    (1, '2026', 'Daily', @SyncId, 'INSERT', NEWID(), SYSUTCDATETIME());

                    INSERT INTO [dbo].[Daily]
                    (SyncId, Name, DailyDate, Closed, CreatedAt, CreatedBy, IsActive)
                    VALUES
                    (@SyncId, 'Day Shift March 15', @DailyDate, 0, SYSUTCDATETIME(), 'AuthoritativeAdmin', 1);";
                cmd.Parameters.AddWithValue("@SyncId", syncId);
                cmd.Parameters.AddWithValue("@DailyDate", dailyDate);
                await cmd.ExecuteNonQueryAsync();
            }

            // Execute actual pull
            var pullService = ctx.CreateActualPullService();
            var result = await pullService.PullDailyChangesAsync(CancellationToken.None);

            Assert.False(result.IsNoOp);
            Assert.Equal(1, result.FinalServerVersion);
            Assert.Equal(1, result.TotalProcessed);

            // Verify local row
            await using (var localConn = new SqlConnection(ctx.LocalConnStr))
            {
                await localConn.OpenAsync();

                await using var cmd = localConn.CreateCommand();
                cmd.CommandText = "SELECT Id, Name, DailyDate, IsActive, CreatedBy FROM [dbo].[Daily] WHERE SyncId = @SyncId;";
                cmd.Parameters.AddWithValue("@SyncId", syncId);

                await using var reader = await cmd.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                var localId = reader.GetInt32(0);
                Assert.True(localId > 0, "Local identity Id must be generated.");
                Assert.Equal("Day Shift March 15", reader.GetString(1));
                Assert.Equal(dailyDate, reader.GetDateTime(2));
                Assert.True(reader.GetBoolean(3));
                Assert.Equal("AuthoritativeAdmin", reader.GetString(4));
            }

            var localVersion = await GetLocalServerVersionAsync(ctx.LocalConnStr, "2026");
            Assert.Equal(1, localVersion);
        }

        #endregion

        #region Scenario 3: UPDATE (Preserves Local Integer Id)

        [Fact]
        public async Task Scenario03_Update_UpdatesScalars_PreservesLocalIntegerId_AdvancesCheckpoint()
        {
            await using var ctx = await PullSqlTestContext.CreateAsync("2026");
            var syncId = Guid.NewGuid();
            int initialLocalIntegerId;

            // Seed Local: row exists with known local Id
            await using (var localConn = new SqlConnection(ctx.LocalConnStr))
            {
                await localConn.OpenAsync();
                await using var cmd = localConn.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO [dbo].[Daily]
                    (SyncId, Name, DailyDate, Closed, CreatedAt, CreatedBy, IsActive)
                    VALUES
                    (@SyncId, 'Local Initial Name', '2026-01-01', 0, SYSUTCDATETIME(), 'LocalUser', 1);
                    SELECT SCOPE_IDENTITY();";
                cmd.Parameters.AddWithValue("@SyncId", syncId);
                initialLocalIntegerId = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                Assert.True(initialLocalIntegerId > 0);
            }

            // Seed Remote: ServerState = 1, Feed: v1 UPDATE, dbo.Daily updated scalars
            await using (var remoteConn = new SqlConnection(ctx.RemoteConnStr))
            {
                await remoteConn.OpenAsync();
                await using var cmd = remoteConn.CreateCommand();
                cmd.CommandText = @"
                    UPDATE [sync].[ServerState] SET CurrentVersion = 1, LastUpdatedUtc = SYSUTCDATETIME() WHERE DatabaseId = '2026';

                    INSERT INTO [sync].[ServerChangeFeed]
                    (ServerVersion, DatabaseId, EntityType, EntitySyncId, OperationType, OriginDeviceId, TimestampUtc)
                    VALUES
                    (1, '2026', 'Daily', @SyncId, 'UPDATE', NEWID(), SYSUTCDATETIME());

                    INSERT INTO [dbo].[Daily]
                    (SyncId, Name, DailyDate, Closed, CreatedAt, CreatedBy, UpdatedAt, UpdatedBy, IsActive)
                    VALUES
                    (@SyncId, 'Remote Authoritative Updated Name', '2026-02-01', 1, SYSUTCDATETIME(), 'RemoteAdmin', SYSUTCDATETIME(), 'UpdaterUser', 1);";
                cmd.Parameters.AddWithValue("@SyncId", syncId);
                await cmd.ExecuteNonQueryAsync();
            }

            // Execute actual pull
            var pullService = ctx.CreateActualPullService();
            var result = await pullService.PullDailyChangesAsync(CancellationToken.None);

            Assert.False(result.IsNoOp);
            Assert.Equal(1, result.FinalServerVersion);

            // Verify local row updated scalars AND preserved integer Id
            await using (var localConn = new SqlConnection(ctx.LocalConnStr))
            {
                await localConn.OpenAsync();
                await using var cmd = localConn.CreateCommand();
                cmd.CommandText = "SELECT Id, Name, Closed, UpdatedBy FROM [dbo].[Daily] WHERE SyncId = @SyncId;";
                cmd.Parameters.AddWithValue("@SyncId", syncId);

                await using var reader = await cmd.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal(initialLocalIntegerId, reader.GetInt32(0)); // Preserved integer Id
                Assert.Equal("Remote Authoritative Updated Name", reader.GetString(1));
                Assert.True(reader.GetBoolean(2));
                Assert.Equal("UpdaterUser", reader.GetString(3));
            }

            var localVersion = await GetLocalServerVersionAsync(ctx.LocalConnStr, "2026");
            Assert.Equal(1, localVersion);
        }

        #endregion

        #region Scenario 4: SOFT_DELETE

        [Fact]
        public async Task Scenario04_SoftDelete_MirrorsInactiveState_AdvancesCheckpoint()
        {
            await using var ctx = await PullSqlTestContext.CreateAsync("2026");
            var syncId = Guid.NewGuid();

            // Seed Local: active row
            await using (var localConn = new SqlConnection(ctx.LocalConnStr))
            {
                await localConn.OpenAsync();
                await using var cmd = localConn.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO [dbo].[Daily] (SyncId, Name, DailyDate, Closed, CreatedAt, IsActive)
                    VALUES (@SyncId, 'Active Daily', '2026-03-01', 0, SYSUTCDATETIME(), 1);";
                cmd.Parameters.AddWithValue("@SyncId", syncId);
                await cmd.ExecuteNonQueryAsync();
            }

            // Seed Remote: ServerState = 1, Feed: v1 SOFT_DELETE, dbo.Daily IsActive = 0
            await using (var remoteConn = new SqlConnection(ctx.RemoteConnStr))
            {
                await remoteConn.OpenAsync();
                await using var cmd = remoteConn.CreateCommand();
                cmd.CommandText = @"
                    UPDATE [sync].[ServerState] SET CurrentVersion = 1, LastUpdatedUtc = SYSUTCDATETIME() WHERE DatabaseId = '2026';

                    INSERT INTO [sync].[ServerChangeFeed]
                    (ServerVersion, DatabaseId, EntityType, EntitySyncId, OperationType, OriginDeviceId, TimestampUtc)
                    VALUES
                    (1, '2026', 'Daily', @SyncId, 'SOFT_DELETE', NEWID(), SYSUTCDATETIME());

                    INSERT INTO [dbo].[Daily]
                    (SyncId, Name, DailyDate, Closed, CreatedAt, DeactivatedAt, DeactivatedBy, IsActive)
                    VALUES
                    (@SyncId, 'Active Daily', '2026-03-01', 0, SYSUTCDATETIME(), SYSUTCDATETIME(), 'AdminDeactivator', 0);";
                cmd.Parameters.AddWithValue("@SyncId", syncId);
                await cmd.ExecuteNonQueryAsync();
            }

            // Execute actual pull
            var pullService = ctx.CreateActualPullService();
            var result = await pullService.PullDailyChangesAsync(CancellationToken.None);

            Assert.False(result.IsNoOp);
            Assert.Equal(1, result.FinalServerVersion);

            // Verify local row is inactive
            await using (var localConn = new SqlConnection(ctx.LocalConnStr))
            {
                await localConn.OpenAsync();
                await using var cmd = localConn.CreateCommand();
                cmd.CommandText = "SELECT IsActive, DeactivatedBy FROM [dbo].[Daily] WHERE SyncId = @SyncId;";
                cmd.Parameters.AddWithValue("@SyncId", syncId);

                await using var reader = await cmd.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.False(reader.GetBoolean(0));
                Assert.Equal("AdminDeactivator", reader.GetString(1));
            }

            var localVersion = await GetLocalServerVersionAsync(ctx.LocalConnStr, "2026");
            Assert.Equal(1, localVersion);
        }

        #endregion

        #region Scenario 5: HARD_DELETE Existing Local

        [Fact]
        public async Task Scenario05_HardDeleteExistingLocal_RemovesLocalRow_AdvancesCheckpoint()
        {
            await using var ctx = await PullSqlTestContext.CreateAsync("2026");
            var syncId = Guid.NewGuid();

            // Seed Local: row exists
            await using (var localConn = new SqlConnection(ctx.LocalConnStr))
            {
                await localConn.OpenAsync();
                await using var cmd = localConn.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO [dbo].[Daily] (SyncId, Name, DailyDate, Closed, CreatedAt, IsActive)
                    VALUES (@SyncId, 'To Be Deleted', '2026-03-01', 0, SYSUTCDATETIME(), 1);";
                cmd.Parameters.AddWithValue("@SyncId", syncId);
                await cmd.ExecuteNonQueryAsync();
            }

            // Seed Remote: ServerState = 1, Feed: v1 HARD_DELETE, Tombstones: row at v1, Daily: absent
            await using (var remoteConn = new SqlConnection(ctx.RemoteConnStr))
            {
                await remoteConn.OpenAsync();
                await using var cmd = remoteConn.CreateCommand();
                cmd.CommandText = @"
                    UPDATE [sync].[ServerState] SET CurrentVersion = 1, LastUpdatedUtc = SYSUTCDATETIME() WHERE DatabaseId = '2026';

                    INSERT INTO [sync].[ServerChangeFeed]
                    (ServerVersion, DatabaseId, EntityType, EntitySyncId, OperationType, OriginDeviceId, TimestampUtc)
                    VALUES
                    (1, '2026', 'Daily', @SyncId, 'HARD_DELETE', NEWID(), SYSUTCDATETIME());

                    INSERT INTO [sync].[Tombstones]
                    (DatabaseId, EntityType, EntitySyncId, ServerVersion, DeletedAtUtc)
                    VALUES
                    ('2026', 'Daily', @SyncId, 1, SYSUTCDATETIME());";
                cmd.Parameters.AddWithValue("@SyncId", syncId);
                await cmd.ExecuteNonQueryAsync();
            }

            // Execute actual pull
            var pullService = ctx.CreateActualPullService();
            var result = await pullService.PullDailyChangesAsync(CancellationToken.None);

            Assert.False(result.IsNoOp);
            Assert.Equal(1, result.FinalServerVersion);
            Assert.Single(result.Operations);
            Assert.Equal("SUCCESS", result.Operations[0].Status);

            // Verify local row is deleted
            await using (var localConn = new SqlConnection(ctx.LocalConnStr))
            {
                await localConn.OpenAsync();
                await using var cmd = localConn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM [dbo].[Daily] WHERE SyncId = @SyncId;";
                cmd.Parameters.AddWithValue("@SyncId", syncId);
                var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                Assert.Equal(0, count);
            }

            var localVersion = await GetLocalServerVersionAsync(ctx.LocalConnStr, "2026");
            Assert.Equal(1, localVersion);
        }

        #endregion

        #region Scenario 6: Feed Gap

        [Fact]
        public async Task Scenario06_FeedGap_ThrowsFeedGapException_LocalStateAndDataUnchanged()
        {
            await using var ctx = await PullSqlTestContext.CreateAsync("2026");

            // Seed Remote: ServerState = 3, Feed: versions 1 and 3 (version 2 missing)
            await using (var remoteConn = new SqlConnection(ctx.RemoteConnStr))
            {
                await remoteConn.OpenAsync();
                await using var cmd = remoteConn.CreateCommand();
                cmd.CommandText = @"
                    UPDATE [sync].[ServerState] SET CurrentVersion = 3, LastUpdatedUtc = SYSUTCDATETIME() WHERE DatabaseId = '2026';

                    INSERT INTO [sync].[ServerChangeFeed]
                    (ServerVersion, DatabaseId, EntityType, EntitySyncId, OperationType, OriginDeviceId, TimestampUtc)
                    VALUES
                    (1, '2026', 'Daily', NEWID(), 'INSERT', NEWID(), SYSUTCDATETIME()),
                    (3, '2026', 'Daily', NEWID(), 'INSERT', NEWID(), SYSUTCDATETIME());";
                await cmd.ExecuteNonQueryAsync();
            }

            var pullService = ctx.CreateActualPullService();
            var ex = await Assert.ThrowsAsync<SyncPullFeedGapException>(() =>
                pullService.PullDailyChangesAsync(CancellationToken.None));

            Assert.Equal("PULL_FEED_GAP", ex.ErrorCode);

            // Verify local checkpoint remains 0 and local Daily has 0 rows
            var localVer = await GetLocalServerVersionAsync(ctx.LocalConnStr, "2026");
            Assert.Equal(0, localVer);
            var localDailyCount = await GetRowCountAsync(ctx.LocalConnStr, "[dbo].[Daily]");
            Assert.Equal(0, localDailyCount);
        }

        #endregion

        #region Scenario 7: Duplicate Version

        [Fact]
        public async Task Scenario07_DuplicateVersion_FailsClosed_LocalUnchanged()
        {
            await using var ctx = await PullSqlTestContext.CreateAsync("2026");

            await using (var remoteConn = new SqlConnection(ctx.RemoteConnStr))
            {
                await remoteConn.OpenAsync();
                await using var cmd = remoteConn.CreateCommand();
                cmd.CommandText = @"
                    UPDATE [sync].[ServerState] SET CurrentVersion = 2, LastUpdatedUtc = SYSUTCDATETIME() WHERE DatabaseId = '2026';

                    INSERT INTO [sync].[ServerChangeFeed]
                    (ServerVersion, DatabaseId, EntityType, EntitySyncId, OperationType, OriginDeviceId, TimestampUtc)
                    VALUES
                    (1, '2026', 'Daily', NEWID(), 'INSERT', NEWID(), SYSUTCDATETIME()),
                    (1, '2026', 'Daily', NEWID(), 'UPDATE', NEWID(), SYSUTCDATETIME());";
                await cmd.ExecuteNonQueryAsync();
            }

            var pullService = ctx.CreateActualPullService();
            var ex = await Assert.ThrowsAsync<SyncPullDuplicateVersionException>(() =>
                pullService.PullDailyChangesAsync(CancellationToken.None));

            Assert.Equal("PULL_DUPLICATE_VERSION", ex.ErrorCode);

            var localVer = await GetLocalServerVersionAsync(ctx.LocalConnStr, "2026");
            Assert.Equal(0, localVer);
        }

        #endregion

        #region Scenario 8: Missing / Wrong Tombstone

        [Fact]
        public async Task Scenario08_MissingTombstone_FailsClosed_LocalUnchanged()
        {
            await using var ctx = await PullSqlTestContext.CreateAsync("2026");
            var syncId = Guid.NewGuid();

            // Seed Remote: Feed says HARD_DELETE, but Tombstones table has NO record
            await using (var remoteConn = new SqlConnection(ctx.RemoteConnStr))
            {
                await remoteConn.OpenAsync();
                await using var cmd = remoteConn.CreateCommand();
                cmd.CommandText = @"
                    UPDATE [sync].[ServerState] SET CurrentVersion = 1, LastUpdatedUtc = SYSUTCDATETIME() WHERE DatabaseId = '2026';

                    INSERT INTO [sync].[ServerChangeFeed]
                    (ServerVersion, DatabaseId, EntityType, EntitySyncId, OperationType, OriginDeviceId, TimestampUtc)
                    VALUES
                    (1, '2026', 'Daily', @SyncId, 'HARD_DELETE', NEWID(), SYSUTCDATETIME());";
                cmd.Parameters.AddWithValue("@SyncId", syncId);
                await cmd.ExecuteNonQueryAsync();
            }

            var pullService = ctx.CreateActualPullService();
            var ex = await Assert.ThrowsAsync<SyncPullTombstoneValidationException>(() =>
                pullService.PullDailyChangesAsync(CancellationToken.None));

            Assert.Equal("PULL_TOMBSTONE_VALIDATION_FAILED", ex.ErrorCode);

            var localVer = await GetLocalServerVersionAsync(ctx.LocalConnStr, "2026");
            Assert.Equal(0, localVer);
        }

        #endregion

        #region Scenario 9: Outbox Gate

        [Theory]
        [InlineData("PENDING", true)]
        [InlineData("IN_PROGRESS", true)]
        [InlineData("FAILED", true)]
        [InlineData("COMPLETED", false)]
        public async Task Scenario09_OutboxGate_BlocksPendingInProgressFailed_AllowsCompleted(string status, bool shouldBlock)
        {
            await using var ctx = await PullSqlTestContext.CreateAsync("2026");

            // Seed Local Outbox with a real SQL row matching production schema
            await using (var localConn = new SqlConnection(ctx.LocalConnStr))
            {
                await localConn.OpenAsync();
                await using var cmd = localConn.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO [sync].[LocalOutbox]
                    (ClientOperationId, DatabaseId, AggregateType, CommandName, EntitySyncId, PayloadJson, CreatedAtUtc, Status, RetryCount)
                    VALUES
                    (NEWID(), '2026', 'Daily', 'Daily.Insert', NEWID(), '{}', SYSUTCDATETIME(), @Status, 0);";
                cmd.Parameters.AddWithValue("@Status", status);
                await cmd.ExecuteNonQueryAsync();
            }

            var pullService = ctx.CreateActualPullService();

            if (shouldBlock)
            {
                var ex = await Assert.ThrowsAsync<SyncPullBlockedLocalChangesPendingException>(() =>
                    pullService.PullDailyChangesAsync(CancellationToken.None));
                Assert.Equal("PULL_BLOCKED_LOCAL_CHANGES_PENDING", ex.ErrorCode);

                var localVer = await GetLocalServerVersionAsync(ctx.LocalConnStr, "2026");
                Assert.Equal(0, localVer);
            }
            else
            {
                // COMPLETED: allowed! Pull proceeds (No-Op in this empty test setup)
                var result = await pullService.PullDailyChangesAsync(CancellationToken.None);
                Assert.True(result.IsNoOp);
            }
        }

        #endregion

        #region Scenario 10: Atomic Local Rollback (Injected Trigger Fault)

        [Fact]
        public async Task Scenario10_AtomicLocalRollback_InjectedTriggerFault_RollsBackAllMutationsAndCheckpoint()
        {
            await using var ctx = await PullSqlTestContext.CreateAsync("2026");
            var syncId = Guid.NewGuid();

            // 1. Seed Remote with valid INSERT
            await using (var remoteConn = new SqlConnection(ctx.RemoteConnStr))
            {
                await remoteConn.OpenAsync();
                await using var cmd = remoteConn.CreateCommand();
                cmd.CommandText = @"
                    UPDATE [sync].[ServerState] SET CurrentVersion = 1, LastUpdatedUtc = SYSUTCDATETIME() WHERE DatabaseId = '2026';

                    INSERT INTO [sync].[ServerChangeFeed]
                    (ServerVersion, DatabaseId, EntityType, EntitySyncId, OperationType, OriginDeviceId, TimestampUtc)
                    VALUES
                    (1, '2026', 'Daily', @SyncId, 'INSERT', NEWID(), SYSUTCDATETIME());

                    INSERT INTO [dbo].[Daily]
                    (SyncId, Name, DailyDate, Closed, CreatedAt, IsActive)
                    VALUES
                    (@SyncId, 'Trigger Rollback Test Day', '2026-03-01', 0, SYSUTCDATETIME(), 1);";
                cmd.Parameters.AddWithValue("@SyncId", syncId);
                await cmd.ExecuteNonQueryAsync();
            }

            // 2. Inject fault: trigger on local dbo.Daily that raises error and rolls back transaction
            await using (var localConn = new SqlConnection(ctx.LocalConnStr))
            {
                await localConn.OpenAsync();
                await using var trigCmd = localConn.CreateCommand();
                trigCmd.CommandText = @"
                    CREATE TRIGGER [dbo].[TR_Daily_RejectForTest] ON [dbo].[Daily]
                    AFTER INSERT, UPDATE
                    AS
                    BEGIN
                        RAISERROR('Injected SQL fault: transaction rejected for rollback verification.', 16, 1);
                        ROLLBACK TRANSACTION;
                    END;";
                await trigCmd.ExecuteNonQueryAsync();
            }

            try
            {
                var pullService = ctx.CreateActualPullService();

                // 3. Run pull service - must fail due to trigger rollback
                await Assert.ThrowsAnyAsync<Exception>(() =>
                    pullService.PullDailyChangesAsync(CancellationToken.None));

                // 4. Verify all prior mutations rolled back and checkpoint unchanged
                var localVer = await GetLocalServerVersionAsync(ctx.LocalConnStr, "2026");
                Assert.Equal(0, localVer);

                var localDailyCount = await GetRowCountAsync(ctx.LocalConnStr, "[dbo].[Daily]");
                Assert.Equal(0, localDailyCount);

                var outboxCount = await GetRowCountAsync(ctx.LocalConnStr, "[sync].[LocalOutbox]");
                Assert.Equal(0, outboxCount);
            }
            finally
            {
                // Drop the test trigger
                await using var localConn = new SqlConnection(ctx.LocalConnStr);
                await localConn.OpenAsync();
                await using var dropTrigCmd = localConn.CreateCommand();
                dropTrigCmd.CommandText = @"
                    IF OBJECT_ID('dbo.TR_Daily_RejectForTest', 'TR') IS NOT NULL
                        DROP TRIGGER [dbo].[TR_Daily_RejectForTest];";
                await dropTrigCmd.ExecuteNonQueryAsync();
            }
        }

        #endregion

        #region Scenario 11: Real Lease Mutual Exclusion (No Mocks)

        [Fact]
        public async Task Scenario11_RealLeaseMutualExclusion_PullBlocksPush_PushBlocksPull_ReclaimsExpired()
        {
            await using var ctx = await PullSqlTestContext.CreateAsync("2026");
            var syncProvider = ctx.CreateSyncProvider();

            var pullLeaseManager = new LocalPullLeaseManager(syncProvider, NullLogger<LocalPullLeaseManager>.Instance);
            var pushLeaseManager = new LocalPushLeaseManager(syncProvider, NullLogger<LocalPushLeaseManager>.Instance);

            // 1. Pull acquires lease
            var pullToken = await pullLeaseManager.AcquireLeaseAsync("2026", TimeSpan.FromMinutes(1), CancellationToken.None);
            Assert.NotEqual(Guid.Empty, pullToken);

            // Push acquire must fail
            await Assert.ThrowsAsync<SyncPushAlreadyRunningException>(() =>
                pushLeaseManager.AcquireLeaseAsync("2026", TimeSpan.FromMinutes(1), CancellationToken.None));

            // Second Pull acquire must fail
            await Assert.ThrowsAsync<SyncPullAlreadyRunningException>(() =>
                pullLeaseManager.AcquireLeaseAsync("2026", TimeSpan.FromMinutes(1), CancellationToken.None));

            // Release Pull lease
            await pullLeaseManager.ReleaseLeaseAsync("2026", pullToken, CancellationToken.None);

            // 2. Push acquires lease
            var pushToken = await pushLeaseManager.AcquireLeaseAsync("2026", TimeSpan.FromMinutes(1), CancellationToken.None);
            Assert.NotEqual(Guid.Empty, pushToken);

            // Pull acquire must fail
            await Assert.ThrowsAsync<SyncPullAlreadyRunningException>(() =>
                pullLeaseManager.AcquireLeaseAsync("2026", TimeSpan.FromMinutes(1), CancellationToken.None));

            // Release Push lease
            await pushLeaseManager.ReleaseLeaseAsync("2026", pushToken, CancellationToken.None);

            // 3. Simulate expired lease by setting expiry in the past
            await using (var localConn = new SqlConnection(ctx.LocalConnStr))
            {
                await localConn.OpenAsync();
                await using var cmd = localConn.CreateCommand();
                cmd.CommandText = @"
                    UPDATE [sync].[LocalState]
                    SET ActiveLeaseToken = NEWID(),
                        LeaseExpiresAtUtc = DATEADD(SECOND, -30, SYSUTCDATETIME())
                    WHERE DatabaseId = '2026';";
                await cmd.ExecuteNonQueryAsync();
            }

            // Expired lease can be reclaimed by Pull
            var reclaimedToken = await pullLeaseManager.AcquireLeaseAsync("2026", TimeSpan.FromMinutes(1), CancellationToken.None);
            Assert.NotEqual(Guid.Empty, reclaimedToken);

            // Stale token validation fails
            await Assert.ThrowsAsync<SyncLeaseExpiredException>(() =>
                pullLeaseManager.ValidateLeaseOwnershipAsync("2026", Guid.NewGuid(), CancellationToken.None));

            await pullLeaseManager.ReleaseLeaseAsync("2026", reclaimedToken, CancellationToken.None);
        }

        #endregion

        #region Scenario 12: NO-OP Lease Fencing

        [Fact]
        public async Task Scenario12_NoOpLeaseFencing_StolenLeaseThrowsSyncLeaseExpiredException()
        {
            await using var ctx = await PullSqlTestContext.CreateAsync("2026");
            var syncProvider = ctx.CreateSyncProvider();
            var leaseManager = new LocalPullLeaseManager(syncProvider, NullLogger<LocalPullLeaseManager>.Instance);

            // H = 0, L = 0 (NO-OP scenario)
            var leaseToken = await leaseManager.AcquireLeaseAsync("2026", TimeSpan.FromMinutes(1), CancellationToken.None);

            // Steal the lease in SQL behind the scenes
            await using (var localConn = new SqlConnection(ctx.LocalConnStr))
            {
                await localConn.OpenAsync();
                await using var cmd = localConn.CreateCommand();
                cmd.CommandText = @"
                    UPDATE [sync].[LocalState]
                    SET ActiveLeaseToken = NEWID(),
                        LeaseExpiresAtUtc = DATEADD(SECOND, 60, SYSUTCDATETIME())
                    WHERE DatabaseId = '2026';";
                await cmd.ExecuteNonQueryAsync();
            }

            // ValidateLeaseOwnershipAsync throws
            await Assert.ThrowsAsync<SyncLeaseExpiredException>(() =>
                leaseManager.ValidateLeaseOwnershipAsync("2026", leaseToken, CancellationToken.None));
        }

        #endregion

        #region Scenario 13: Batch Identity Mismatch & Pre-flight Validation

        [Fact]
        public async Task Scenario13_BatchIdentityMismatch_RejectsWithPullBatchDatabaseMismatch()
        {
            await using var ctx = await PullSqlTestContext.CreateAsync("2026");
            var syncProvider = ctx.CreateSyncProvider();
            var coordinator = new LocalPullTransactionCoordinator(syncProvider, NullLogger<LocalPullTransactionCoordinator>.Instance);

            var batchWith2027 = new FencedPullBatch
            {
                DatabaseId = "2027",
                LowWatermark = 0,
                HighWatermark = 1,
                IsNoOp = false,
                Commands = new List<PullCommand>
                {
                    PullCommand.CreateDelete(Guid.NewGuid(), 1)
                }
            };

            var ex = await Assert.ThrowsAsync<SyncPullBatchDatabaseMismatchException>(() =>
                coordinator.ApplyPullBatchAsync("2026", batchWith2027, Guid.NewGuid(), CancellationToken.None));

            Assert.Equal("PULL_BATCH_DATABASE_MISMATCH", ex.ErrorCode);
        }

        [Fact]
        public async Task Scenario13_MalformedBatch_EmptyGuidCommand_FailsClosedBeforeDml()
        {
            await using var ctx = await PullSqlTestContext.CreateAsync("2026");
            var syncProvider = ctx.CreateSyncProvider();
            var coordinator = new LocalPullTransactionCoordinator(syncProvider, NullLogger<LocalPullTransactionCoordinator>.Instance);

            var malformedBatch = new FencedPullBatch
            {
                DatabaseId = "2026",
                LowWatermark = 0,
                HighWatermark = 1,
                IsNoOp = false,
                Commands = new List<PullCommand>
                {
                    new PullCommand
                    {
                        EntitySyncId = Guid.Empty,
                        CommandType = PullCommandType.Delete,
                        TerminalServerVersion = 1,
                        Snapshot = null
                    }
                }
            };

            var ex = await Assert.ThrowsAsync<SyncPullBatchMalformedException>(() =>
                coordinator.ApplyPullBatchAsync("2026", malformedBatch, Guid.NewGuid(), CancellationToken.None));

            Assert.Equal("PULL_BATCH_MALFORMED", ex.ErrorCode);
        }

        #endregion

        #region Scenario 14: SOFT_DELETE Semantic Validation

        [Fact]
        public async Task Scenario14_SoftDeleteAuthoritativeStateMismatch_FailsClosedWhenRemoteIsActiveIsTrue()
        {
            await using var ctx = await PullSqlTestContext.CreateAsync("2026");
            var syncId = Guid.NewGuid();

            // Seed Remote: Feed says SOFT_DELETE, but authoritative dbo.Daily has IsActive = 1
            await using (var remoteConn = new SqlConnection(ctx.RemoteConnStr))
            {
                await remoteConn.OpenAsync();
                await using var cmd = remoteConn.CreateCommand();
                cmd.CommandText = @"
                    UPDATE [sync].[ServerState] SET CurrentVersion = 1, LastUpdatedUtc = SYSUTCDATETIME() WHERE DatabaseId = '2026';

                    INSERT INTO [sync].[ServerChangeFeed]
                    (ServerVersion, DatabaseId, EntityType, EntitySyncId, OperationType, OriginDeviceId, TimestampUtc)
                    VALUES
                    (1, '2026', 'Daily', @SyncId, 'SOFT_DELETE', NEWID(), SYSUTCDATETIME());

                    INSERT INTO [dbo].[Daily]
                    (SyncId, Name, DailyDate, Closed, CreatedAt, IsActive)
                    VALUES
                    (@SyncId, 'Contradictory Daily Row', '2026-03-01', 0, SYSUTCDATETIME(), 1);"; // IsActive=1 contradicts SOFT_DELETE!
                cmd.Parameters.AddWithValue("@SyncId", syncId);
                await cmd.ExecuteNonQueryAsync();
            }

            var reader = new AzureFencedBatchReader(ctx.CreateRemoteFactory(), NullLogger<AzureFencedBatchReader>.Instance);

            var ex = await Assert.ThrowsAsync<SyncPullAuthoritativeStateMismatchException>(() =>
                reader.ReadFencedBatchAsync("2026", 0, CancellationToken.None));

            Assert.Equal("PULL_AUTHORITATIVE_STATE_MISMATCH", ex.ErrorCode);

            var localVer = await GetLocalServerVersionAsync(ctx.LocalConnStr, "2026");
            Assert.Equal(0, localVer);
        }

        #endregion

        #region Scenario 15: Real Zero Remote DML Evidence (Deterministic Hash Invariance)

        [Fact]
        public async Task Scenario15_RealZeroRemoteDmlEvidence_HashesAndRowCountsIdenticalBeforeAndAfterReader()
        {
            await using var ctx = await PullSqlTestContext.CreateAsync("2026");
            var syncId = Guid.NewGuid();

            // Seed Remote with active data
            await using (var remoteConn = new SqlConnection(ctx.RemoteConnStr))
            {
                await remoteConn.OpenAsync();
                await using var cmd = remoteConn.CreateCommand();
                cmd.CommandText = @"
                    UPDATE [sync].[ServerState] SET CurrentVersion = 1, LastUpdatedUtc = SYSUTCDATETIME() WHERE DatabaseId = '2026';

                    INSERT INTO [sync].[ServerChangeFeed]
                    (ServerVersion, DatabaseId, EntityType, EntitySyncId, OperationType, OriginDeviceId, TimestampUtc)
                    VALUES
                    (1, '2026', 'Daily', @SyncId, 'INSERT', NEWID(), SYSUTCDATETIME());

                    INSERT INTO [dbo].[Daily]
                    (SyncId, Name, DailyDate, Closed, CreatedAt, IsActive)
                    VALUES
                    (@SyncId, 'Immutable Remote Daily', '2026-03-01', 0, SYSUTCDATETIME(), 1);";
                cmd.Parameters.AddWithValue("@SyncId", syncId);
                await cmd.ExecuteNonQueryAsync();
            }

            // Compute pre-reader snapshot hashes
            var dailyHashBefore = await ComputeTableHashAsync(ctx.RemoteConnStr, "SELECT Id, Name, DailyDate, Closed, IsActive, SyncId FROM [dbo].[Daily] ORDER BY Id;");
            var serverStateBefore = await ComputeTableHashAsync(ctx.RemoteConnStr, "SELECT DatabaseId, CurrentVersion FROM [sync].[ServerState] ORDER BY DatabaseId;");
            var feedHashBefore = await ComputeTableHashAsync(ctx.RemoteConnStr, "SELECT FeedId, ServerVersion, DatabaseId, EntityType, EntitySyncId, OperationType FROM [sync].[ServerChangeFeed] ORDER BY FeedId;");
            var tombstonesHashBefore = await ComputeTableHashAsync(ctx.RemoteConnStr, "SELECT DatabaseId, EntityType, EntitySyncId, ServerVersion, DeletedAtUtc FROM [sync].[Tombstones] ORDER BY DatabaseId, EntityType, EntitySyncId, ServerVersion;");

            // Execute actual AzureFencedBatchReader
            var reader = new AzureFencedBatchReader(ctx.CreateRemoteFactory(), NullLogger<AzureFencedBatchReader>.Instance);
            var batch = await reader.ReadFencedBatchAsync("2026", 0, CancellationToken.None);

            Assert.NotNull(batch);
            Assert.Single(batch.Commands);

            // Compute post-reader snapshot hashes
            var dailyHashAfter = await ComputeTableHashAsync(ctx.RemoteConnStr, "SELECT Id, Name, DailyDate, Closed, IsActive, SyncId FROM [dbo].[Daily] ORDER BY Id;");
            var serverStateAfter = await ComputeTableHashAsync(ctx.RemoteConnStr, "SELECT DatabaseId, CurrentVersion FROM [sync].[ServerState] ORDER BY DatabaseId;");
            var feedHashAfter = await ComputeTableHashAsync(ctx.RemoteConnStr, "SELECT FeedId, ServerVersion, DatabaseId, EntityType, EntitySyncId, OperationType FROM [sync].[ServerChangeFeed] ORDER BY FeedId;");
            var tombstonesHashAfter = await ComputeTableHashAsync(ctx.RemoteConnStr, "SELECT DatabaseId, EntityType, EntitySyncId, ServerVersion, DeletedAtUtc FROM [sync].[Tombstones] ORDER BY DatabaseId, EntityType, EntitySyncId, ServerVersion;");

            // Assert exact invariance
            Assert.Equal(dailyHashBefore, dailyHashAfter);
            Assert.Equal(serverStateBefore, serverStateAfter);
            Assert.Equal(feedHashBefore, feedHashAfter);
            Assert.Equal(tombstonesHashBefore, tombstonesHashAfter);
        }

        #endregion

        #region Scenario 16: Real ServerState Fence Concurrency Test

        [Fact]
        public async Task Scenario16_RealServerStateFenceConcurrency_UpdLockHoldLockBlocksConcurrentUpdate()
        {
            await using var ctx = await PullSqlTestContext.CreateAsync("2026");

            // Connection 1: Opens serializable transaction and holds fence lock
            await using var conn1 = new SqlConnection(ctx.RemoteConnStr);
            await conn1.OpenAsync();
            await using var tx1 = (SqlTransaction)await conn1.BeginTransactionAsync(IsolationLevel.Serializable);

            await using (var fenceCmd = conn1.CreateCommand())
            {
                fenceCmd.Transaction = tx1;
                fenceCmd.CommandText = @"
                    SELECT CurrentVersion
                    FROM [sync].[ServerState] WITH (UPDLOCK, HOLDLOCK)
                    WHERE DatabaseId = '2026';";
                var ver = await fenceCmd.ExecuteScalarAsync();
                Assert.NotNull(ver);
            }

            // Connection 2: Concurrent transaction attempts incompatible update
            var updateCompleted = false;
            var updateTask = Task.Run(async () =>
            {
                await using var conn2 = new SqlConnection(ctx.RemoteConnStr);
                await conn2.OpenAsync();
                await using var cmd2 = conn2.CreateCommand();
                cmd2.CommandTimeout = 10;
                cmd2.CommandText = @"
                    UPDATE [sync].[ServerState]
                    SET CurrentVersion = CurrentVersion + 1,
                        LastUpdatedUtc = SYSUTCDATETIME()
                    WHERE DatabaseId = '2026';";
                await cmd2.ExecuteNonQueryAsync();
                updateCompleted = true;
            });

            // Wait 300ms: Connection 2 MUST be blocked by Connection 1's lock
            await Task.Delay(300);
            Assert.False(updateCompleted, "Concurrent UPDATE must be blocked while UPDLOCK/HOLDLOCK is held.");

            // Release Connection 1's fence by committing
            await tx1.CommitAsync();

            // Wait for Connection 2 to finish
            await updateTask;
            Assert.True(updateCompleted, "Concurrent UPDATE must complete after fence lock is released.");
        }

        #endregion

        #region Scenario 17: Pull Lease Blocks Offline Write

        [Fact]
        public async Task Scenario17_PullLease_BlocksOfflineWrite_ThrowsBlockedActiveSync()
        {
            await using var ctx = await PullSqlTestContext.CreateAsync("2026");
            var leaseManager = ctx.CreateActualPullLeaseManager();

            // 1. Acquire actual Pull lease
            var leaseToken = await leaseManager.AcquireLeaseAsync("2026", TimeSpan.FromMinutes(1), CancellationToken.None);
            Assert.NotEqual(Guid.Empty, leaseToken);

            // 2. Try actual Offline UnitOfWork Daily write
            await using var appCtx = ctx.CreateApplicationContext();
            var uow = ctx.CreateUnitOfWork(appCtx);

            var daily = new Daily
            {
                Name = "Offline Blocked Daily",
                DailyDate = DateTime.UtcNow.Date,
                Closed = false,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = "OfflineUser",
                IsActive = true,
                SyncId = Guid.NewGuid()
            };
            appCtx.Set<Daily>().Add(daily);

            // 3. Expected: LOCAL_WRITE_BLOCKED_ACTIVE_SYNC, Daily unchanged, LocalOutbox unchanged
            var ex = await Assert.ThrowsAsync<SyncLocalWriteBlockedActiveSyncException>(() =>
                uow.SaveChangesAsync(CancellationToken.None));
            Assert.Equal("LOCAL_WRITE_BLOCKED_ACTIVE_SYNC", ex.ErrorCode);

            // Verify 0 rows in dbo.Daily and sync.LocalOutbox
            var dailyCount = await GetRowCountAsync(ctx.LocalConnStr, "[dbo].[Daily]");
            Assert.Equal(0, dailyCount);

            var outboxCount = await GetRowCountAsync(ctx.LocalConnStr, "[sync].[LocalOutbox]");
            Assert.Equal(0, outboxCount);

            await leaseManager.ReleaseLeaseAsync("2026", leaseToken, CancellationToken.None);
        }

        #endregion

        #region Scenario 18: Push Lease Blocks Offline Write

        [Fact]
        public async Task Scenario18_PushLease_BlocksOfflineWrite_ThrowsBlockedActiveSync()
        {
            await using var ctx = await PullSqlTestContext.CreateAsync("2026");
            var pushLeaseManager = new LocalPushLeaseManager(ctx.SyncConnectionProvider, NullLogger<LocalPushLeaseManager>.Instance);

            // 1. Acquire actual Push lease
            var leaseToken = await pushLeaseManager.AcquireLeaseAsync("2026", TimeSpan.FromMinutes(1), CancellationToken.None);
            Assert.NotEqual(Guid.Empty, leaseToken);

            // 2. Try actual Offline UnitOfWork Daily write
            await using var appCtx = ctx.CreateApplicationContext();
            var uow = ctx.CreateUnitOfWork(appCtx);

            var daily = new Daily
            {
                Name = "Offline Blocked By Push",
                DailyDate = DateTime.UtcNow.Date,
                Closed = false,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = "OfflineUser",
                IsActive = true,
                SyncId = Guid.NewGuid()
            };
            appCtx.Set<Daily>().Add(daily);

            // 3. Expected: LOCAL_WRITE_BLOCKED_ACTIVE_SYNC, Daily unchanged, LocalOutbox unchanged
            var ex = await Assert.ThrowsAsync<SyncLocalWriteBlockedActiveSyncException>(() =>
                uow.SaveChangesAsync(CancellationToken.None));
            Assert.Equal("LOCAL_WRITE_BLOCKED_ACTIVE_SYNC", ex.ErrorCode);

            // Verify 0 rows in dbo.Daily and sync.LocalOutbox
            var dailyCount = await GetRowCountAsync(ctx.LocalConnStr, "[dbo].[Daily]");
            Assert.Equal(0, dailyCount);

            var outboxCount = await GetRowCountAsync(ctx.LocalConnStr, "[sync].[LocalOutbox]");
            Assert.Equal(0, outboxCount);

            await pushLeaseManager.ReleaseLeaseAsync("2026", leaseToken, CancellationToken.None);
        }

        #endregion

        #region Scenario 19: Offline Write Starts First - Pull Waits and Gets Blocked By Outbox

        [Fact]
        public async Task Scenario19_OfflineWriteStartsFirst_HoldsLock_PullWaits_ThenBlockedByPendingOutbox()
        {
            await using var ctx = await PullSqlTestContext.CreateAsync("2026");
            var pullLeaseManager = ctx.CreateActualPullLeaseManager();

            // 1. Offline transaction starts and locks LocalState with UPDLOCK, HOLDLOCK
            await using var txConn = new SqlConnection(ctx.LocalConnStr);
            await txConn.OpenAsync();
            await using var tx = txConn.BeginTransaction(IsolationLevel.ReadCommitted);

            await using (var lockCmd = txConn.CreateCommand())
            {
                lockCmd.Transaction = tx;
                lockCmd.CommandText = @"
                    SELECT TOP (1) [DeviceId], [LastServerVersion], [ActiveLeaseToken], [LeaseExpiresAtUtc],
                        CASE WHEN [ActiveLeaseToken] IS NOT NULL AND [LeaseExpiresAtUtc] >= SYSUTCDATETIME() THEN 1 ELSE 0 END AS [IsActiveSyncLease]
                    FROM [sync].[LocalState] WITH (UPDLOCK, HOLDLOCK)
                    WHERE [DatabaseId] = '2026';";
                using var reader = await lockCmd.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal(0, reader.GetInt32(4)); // No active sync lease
            }

            // 2. Concurrent Pull AcquireLease starts in a separate task
            // It must wait on the UPDLOCK, HOLDLOCK row lock!
            var acquireTask = Task.Run(async () =>
            {
                return await pullLeaseManager.AcquireLeaseAsync("2026", TimeSpan.FromMinutes(1), CancellationToken.None);
            });

            // Verify it does not complete immediately while transaction is open
            var delayTask = Task.Delay(250);
            var completedFirst = await Task.WhenAny(acquireTask, delayTask);
            Assert.Same(delayTask, completedFirst); // acquireTask is still waiting for row lock!

            // 3. Inside tx: complete Offline write and insert PENDING outbox row
            await using (var insertCmd = txConn.CreateCommand())
            {
                insertCmd.Transaction = tx;
                insertCmd.CommandText = @"
                    INSERT INTO [dbo].[Daily] (Name, DailyDate, Closed, CreatedAt, IsActive, SyncId)
                    VALUES ('Concurrent Offline Daily', '2026-03-01', 0, SYSUTCDATETIME(), 1, NEWID());

                    INSERT INTO [sync].[LocalOutbox]
                    (ClientOperationId, DatabaseId, AggregateType, CommandName, EntitySyncId, PayloadJson, CreatedAtUtc, Status, RetryCount)
                    VALUES
                    (NEWID(), '2026', 'Daily', 'Daily.Insert', NEWID(), '{}', SYSUTCDATETIME(), 'PENDING', 0);";
                await insertCmd.ExecuteNonQueryAsync();
            }

            // 4. Commit offline transaction
            await tx.CommitAsync();

            // 5. Now acquireTask unblocks and acquires the lease!
            var pullToken = await acquireTask;
            Assert.NotEqual(Guid.Empty, pullToken);

            // Release lease so pull service can run its regular cycle
            await pullLeaseManager.ReleaseLeaseAsync("2026", pullToken, CancellationToken.None);

            // 6. Pull proceeds, but is blocked by the newly committed PENDING outbox!
            var pullService = ctx.CreateActualPullService();
            var ex = await Assert.ThrowsAsync<SyncPullBlockedLocalChangesPendingException>(() =>
                pullService.PullDailyChangesAsync(CancellationToken.None));
            Assert.Equal("PULL_BLOCKED_LOCAL_CHANGES_PENDING", ex.ErrorCode);

            // Local checkpoint remains 0
            var localVer = await GetLocalServerVersionAsync(ctx.LocalConnStr, "2026");
            Assert.Equal(0, localVer);
        }

        #endregion

        #region Scenario 20: Expired Lease Permits Offline Write Safely

        [Fact]
        public async Task Scenario20_ExpiredLease_PermitsOfflineWriteSafely()
        {
            await using var ctx = await PullSqlTestContext.CreateAsync("2026");

            // 1. Seed expired lease in LocalState
            await using (var conn = new SqlConnection(ctx.LocalConnStr))
            {
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    UPDATE [sync].[LocalState]
                    SET ActiveLeaseToken = NEWID(),
                        LeaseExpiresAtUtc = DATEADD(MINUTE, -10, SYSUTCDATETIME())
                    WHERE DatabaseId = '2026';";
                await cmd.ExecuteNonQueryAsync();
            }

            // 2. Perform actual Offline UnitOfWork Daily write
            await using var appCtx = ctx.CreateApplicationContext();
            var uow = ctx.CreateUnitOfWork(appCtx);

            var daily = new Daily
            {
                Name = "Offline Daily With Expired Lease",
                DailyDate = DateTime.UtcNow.Date,
                Closed = false,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = "OfflineUser",
                IsActive = true,
                SyncId = Guid.NewGuid()
            };
            appCtx.Set<Daily>().Add(daily);

            // 3. Write succeeds without throwing SyncLocalWriteBlockedActiveSyncException!
            var result = await uow.SaveChangesAsync(CancellationToken.None);
            Assert.True(result > 0);

            // Verify row in dbo.Daily and sync.LocalOutbox
            await using (var conn = new SqlConnection(ctx.LocalConnStr))
            {
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM [dbo].[Daily] WHERE Name = 'Offline Daily With Expired Lease';";
                Assert.Equal(1, Convert.ToInt32(await cmd.ExecuteScalarAsync()));

                cmd.CommandText = "SELECT COUNT(*) FROM [sync].[LocalOutbox] WHERE Status = 'PENDING';";
                Assert.Equal(1, Convert.ToInt32(await cmd.ExecuteScalarAsync()));
            }
        }

        #endregion

        #region Scenario 21: Retry After Successful Pull Returns NO-OP

        [Fact]
        public async Task Scenario21_RetryAfterSuccessfulPull_ReturnsNoOp()
        {
            await using var ctx = await PullSqlTestContext.CreateAsync("2026");
            var syncId = Guid.NewGuid();

            // 1. Seed Remote with 1 INSERT mutation
            await using (var remoteConn = new SqlConnection(ctx.RemoteConnStr))
            {
                await remoteConn.OpenAsync();
                await using var cmd = remoteConn.CreateCommand();
                cmd.CommandText = @"
                    UPDATE [sync].[ServerState] SET CurrentVersion = 1, LastUpdatedUtc = SYSUTCDATETIME() WHERE DatabaseId = '2026';

                    INSERT INTO [sync].[ServerChangeFeed]
                    (ServerVersion, DatabaseId, EntityType, EntitySyncId, OperationType, OriginDeviceId, TimestampUtc)
                    VALUES
                    (1, '2026', 'Daily', @SyncId, 'INSERT', NEWID(), SYSUTCDATETIME());

                    INSERT INTO [dbo].[Daily]
                    (SyncId, Name, DailyDate, Closed, CreatedAt, IsActive)
                    VALUES
                    (@SyncId, 'Initial Daily For Retry Test', '2026-03-01', 0, SYSUTCDATETIME(), 1);";
                cmd.Parameters.AddWithValue("@SyncId", syncId);
                await cmd.ExecuteNonQueryAsync();
            }

            var pullService = ctx.CreateActualPullService();

            // 2. First Pull: Applies changes
            var firstResult = await pullService.PullDailyChangesAsync(CancellationToken.None);
            Assert.False(firstResult.IsNoOp);
            Assert.Equal(1, firstResult.FinalServerVersion);
            Assert.Single(firstResult.Operations);
            Assert.Equal("SUCCESS", firstResult.Operations[0].Status);

            var verAfterFirst = await GetLocalServerVersionAsync(ctx.LocalConnStr, "2026");
            Assert.Equal(1, verAfterFirst);

            // 3. Immediate Retry: Must return NO-OP without re-applying or mutating
            var retryResult = await pullService.PullDailyChangesAsync(CancellationToken.None);
            Assert.True(retryResult.IsNoOp);
            Assert.Equal(1, retryResult.FinalServerVersion);
            Assert.Empty(retryResult.Operations);

            var verAfterRetry = await GetLocalServerVersionAsync(ctx.LocalConnStr, "2026");
            Assert.Equal(1, verAfterRetry);
        }

        #endregion

        #region Database Helper Methods

        private static async Task<long> GetLocalServerVersionAsync(string localConnStr, string databaseId)
        {
            await using var conn = new SqlConnection(localConnStr);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT LastServerVersion FROM [sync].[LocalState] WHERE DatabaseId = @DatabaseId;";
            cmd.Parameters.AddWithValue("@DatabaseId", databaseId);
            return Convert.ToInt64(await cmd.ExecuteScalarAsync());
        }

        private static async Task<int> GetRowCountAsync(string connStr, string tableName)
        {
            await using var conn = new SqlConnection(connStr);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {tableName};";
            return Convert.ToInt32(await cmd.ExecuteScalarAsync());
        }

        private static async Task<string> ComputeTableHashAsync(string connStr, string query)
        {
            await using var conn = new SqlConnection(connStr);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = query;

            await using var reader = await cmd.ExecuteReaderAsync();
            var sb = new StringBuilder();
            while (await reader.ReadAsync())
            {
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    sb.Append(reader.IsDBNull(i) ? "NULL" : reader.GetValue(i).ToString());
                    sb.Append('|');
                }
                sb.AppendLine();
            }

            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
            return Convert.ToHexString(bytes);
        }

        #endregion
        #region Scenario 18: Deterministic Concurrent Authoritative Advance Blocked Under Reader Fence

        private sealed class HookedAzureFencedBatchReader : IAzureFencedBatchReader
        {
            private readonly IRemoteDatabaseConnectionFactory _remoteFactory;
            private readonly Func<long, SqlConnection, SqlTransaction, Task> _onFenceAcquired;

            public HookedAzureFencedBatchReader(
                IRemoteDatabaseConnectionFactory remoteFactory,
                Func<long, SqlConnection, SqlTransaction, Task> onFenceAcquired)
            {
                _remoteFactory = remoteFactory;
                _onFenceAcquired = onFenceAcquired;
            }

            public async Task<FencedPullBatch> ReadFencedBatchAsync(string databaseId, long localLastServerVersion, CancellationToken cancellationToken)
            {
                await using var connection = (SqlConnection)await _remoteFactory.CreateOpenConnectionAsync(databaseId, cancellationToken);
                await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

                // Phase 1: High-Watermark Fence with (UPDLOCK, HOLDLOCK)
                long highWatermark;
                await using (var stateCmd = connection.CreateCommand())
                {
                    stateCmd.Transaction = transaction;
                    stateCmd.CommandText = @"
                        SELECT CurrentVersion
                        FROM [sync].[ServerState] WITH (UPDLOCK, HOLDLOCK)
                        WHERE DatabaseId = @DatabaseId;";
                    stateCmd.Parameters.AddWithValue("@DatabaseId", databaseId);
                    var scalar = await stateCmd.ExecuteScalarAsync(cancellationToken);
                    highWatermark = Convert.ToInt64(scalar);
                }

                // Deterministic Coordination Hook: Invoke callback while lock is actively held!
                await _onFenceAcquired(highWatermark, connection, transaction);

                // Checkpoint comparison
                if (highWatermark == localLastServerVersion)
                {
                    await transaction.CommitAsync(cancellationToken);
                    return FencedPullBatch.CreateNoOp(databaseId, localLastServerVersion);
                }

                // Phase 2: Feed Window
                var rawFeedEvents = new List<ServerChangeFeed>();
                await using (var feedCmd = connection.CreateCommand())
                {
                    feedCmd.Transaction = transaction;
                    feedCmd.CommandText = @"
                        SELECT FeedId, ServerVersion, DatabaseId, EntityType, EntitySyncId, OperationType, OriginDeviceId, TimestampUtc
                        FROM [sync].[ServerChangeFeed]
                        WHERE DatabaseId = @DatabaseId
                          AND ServerVersion > @LowWatermark
                          AND ServerVersion <= @HighWatermark
                        ORDER BY ServerVersion ASC;";
                    feedCmd.Parameters.AddWithValue("@DatabaseId", databaseId);
                    feedCmd.Parameters.AddWithValue("@LowWatermark", localLastServerVersion);
                    feedCmd.Parameters.AddWithValue("@HighWatermark", highWatermark);

                    await using var reader = await feedCmd.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        rawFeedEvents.Add(new ServerChangeFeed
                        {
                            FeedId = reader.GetInt64(0),
                            ServerVersion = reader.GetInt64(1),
                            DatabaseId = reader.GetString(2),
                            EntityType = reader.GetString(3),
                            EntitySyncId = reader.GetGuid(4),
                            OperationType = reader.GetString(5),
                            OriginDeviceId = reader.GetGuid(6),
                            TimestampUtc = reader.GetDateTime(7)
                        });
                    }
                }

                // Phase 3 & 4: Materialize
                var commands = new List<PullCommand>();
                foreach (var evt in rawFeedEvents)
                {
                    if (evt.OperationType.Equals("INSERT", StringComparison.OrdinalIgnoreCase))
                    {
                        await using var dailyCmd = connection.CreateCommand();
                        dailyCmd.Transaction = transaction;
                        dailyCmd.CommandText = "SELECT SyncId, Name, DailyDate, Closed, CreatedAt, IsActive FROM [dbo].[Daily] WHERE SyncId = @SyncId;";
                        dailyCmd.Parameters.AddWithValue("@SyncId", evt.EntitySyncId);
                        await using var rdr = await dailyCmd.ExecuteReaderAsync(cancellationToken);
                        if (await rdr.ReadAsync(cancellationToken))
                        {
                            var snapshot = new DailyAuthoritativeSnapshot
                            {
                                SyncId = rdr.GetGuid(0),
                                Name = rdr.GetString(1),
                                DailyDate = rdr.GetDateTime(2),
                                Closed = rdr.GetBoolean(3),
                                CreatedAt = rdr.GetDateTime(4),
                                IsActive = rdr.GetBoolean(5)
                            };
                            commands.Add(PullCommand.CreateUpsert(snapshot, evt.ServerVersion));
                        }
                    }
                }

                // Phase 5: Commit and release Azure fence
                await transaction.CommitAsync(cancellationToken);

                return new FencedPullBatch
                {
                    DatabaseId = databaseId,
                    LowWatermark = localLastServerVersion,
                    HighWatermark = highWatermark,
                    IsNoOp = false,
                    Commands = commands
                };
            }
        }

        [Fact]
        public async Task Scenario18_DeterministicConcurrentAuthoritativeAdvance_BlockedUnderReaderFence_DoesNotCorruptCompletedAttempt()
        {
            await using var ctx = await PullSqlTestContext.CreateAsync("2026");
            var syncId1 = Guid.NewGuid();
            var syncId2 = Guid.NewGuid();

            // 1. Seed Remote with version 1: Daily record 'Daily_V1', feed event 1, ServerState.CurrentVersion = 1
            await using (var seedConn = new SqlConnection(ctx.RemoteConnStr))
            {
                await seedConn.OpenAsync();
                await using var cmd = seedConn.CreateCommand();
                cmd.CommandText = @"
                    UPDATE [sync].[ServerState] SET CurrentVersion = 1, LastUpdatedUtc = SYSUTCDATETIME() WHERE DatabaseId = '2026';
                    INSERT INTO [sync].[ServerChangeFeed]
                    (ServerVersion, DatabaseId, EntityType, EntitySyncId, OperationType, OriginDeviceId, TimestampUtc)
                    VALUES
                    (1, '2026', 'Daily', @SyncId1, 'INSERT', NEWID(), SYSUTCDATETIME());
                    INSERT INTO [dbo].[Daily]
                    (SyncId, Name, DailyDate, Closed, CreatedAt, IsActive)
                    VALUES
                    (@SyncId1, 'Daily_V1', '2026-03-01', 0, SYSUTCDATETIME(), 1);";
                cmd.Parameters.AddWithValue("@SyncId1", syncId1);
                await cmd.ExecuteNonQueryAsync();
            }

            // Local starts at W = 0 (< H = 1)
            var localVerInitial = await GetLocalServerVersionAsync(ctx.LocalConnStr, "2026");
            Assert.Equal(0, localVerInitial);

            var writerStartedTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var writerCompleted = false;

            // Define the hook to run while reader holds the fence lock:
            Task writerTask = Task.CompletedTask;
            var hookedReader = new HookedAzureFencedBatchReader(ctx.CreateRemoteFactory(), async (highWatermark, readerConn, readerTx) =>
            {
                Assert.Equal(1, highWatermark); // H_exec = 1

                // Get reader SPID
                int readerSpid;
                await using (var spidCmd = readerConn.CreateCommand())
                {
                    spidCmd.Transaction = readerTx;
                    spidCmd.CommandText = "SELECT @@SPID;";
                    readerSpid = Convert.ToInt32(await spidCmd.ExecuteScalarAsync());
                }

                // Start concurrent authoritative writer attempting version 2 while reader fence is held
                writerTask = Task.Run(async () =>
                {
                    await using var writerConn = new SqlConnection(ctx.RemoteConnStr);
                    await writerConn.OpenAsync();

                    int writerSpid;
                    await using (var wSpidCmd = writerConn.CreateCommand())
                    {
                        wSpidCmd.CommandText = "SELECT @@SPID;";
                        writerSpid = Convert.ToInt32(await wSpidCmd.ExecuteScalarAsync());
                    }
                    writerStartedTcs.SetResult(writerSpid);

                    await using var writerTx = (SqlTransaction)await writerConn.BeginTransactionAsync(IsolationLevel.Serializable);
                    await using var writerCmd = writerConn.CreateCommand();
                    writerCmd.Transaction = writerTx;
                    writerCmd.CommandTimeout = 30;
                    writerCmd.CommandText = @"
                        SELECT CurrentVersion FROM [sync].[ServerState] WITH (UPDLOCK, HOLDLOCK) WHERE DatabaseId = '2026';
                        UPDATE [sync].[ServerState] SET CurrentVersion = 2, LastUpdatedUtc = SYSUTCDATETIME() WHERE DatabaseId = '2026';
                        INSERT INTO [sync].[ServerChangeFeed]
                        (ServerVersion, DatabaseId, EntityType, EntitySyncId, OperationType, OriginDeviceId, TimestampUtc)
                        VALUES
                        (2, '2026', 'Daily', @SyncId2, 'INSERT', NEWID(), SYSUTCDATETIME());
                        INSERT INTO [dbo].[Daily]
                        (SyncId, Name, DailyDate, Closed, CreatedAt, IsActive)
                        VALUES
                        (@SyncId2, 'Daily_V2', '2026-03-02', 0, SYSUTCDATETIME(), 1);";
                    writerCmd.Parameters.AddWithValue("@SyncId2", syncId2);
                    await writerCmd.ExecuteNonQueryAsync();
                    await writerTx.CommitAsync();
                    writerCompleted = true;
                });

                var writerSpidVal = await writerStartedTcs.Task;

                // Deterministic coordination evidence: Poll sys.dm_os_waiting_tasks
                // Proves that writer is actively blocked by the reader's lock on ServerState
                var isBlocked = false;
                for (var i = 0; i < 50; i++)
                {
                    await Task.Delay(50);
                    await using var diagConn = new SqlConnection(ctx.RemoteConnStr);
                    await diagConn.OpenAsync();
                    await using var diagCmd = diagConn.CreateCommand();
                    diagCmd.CommandText = @"
                        SELECT COUNT(*)
                        FROM sys.dm_os_waiting_tasks
                        WHERE session_id = @WriterSpid
                          AND blocking_session_id = @ReaderSpid
                          AND wait_type LIKE 'LCK%';";
                    diagCmd.Parameters.AddWithValue("@WriterSpid", writerSpidVal);
                    diagCmd.Parameters.AddWithValue("@ReaderSpid", readerSpid);
                    var count = Convert.ToInt32(await diagCmd.ExecuteScalarAsync());
                    if (count > 0)
                    {
                        isBlocked = true;
                        break;
                    }
                }

                Assert.True(isBlocked, "Concurrent writer MUST be deterministically blocked by the reader's UPDLOCK/HOLDLOCK fence in sys.dm_os_waiting_tasks.");
                Assert.False(writerCompleted, "Writer must not complete while fence is held.");

                // While writer is blocked, verify remote ServerState is still 1
                await using (var chkConn = new SqlConnection(ctx.RemoteConnStr))
                {
                    await chkConn.OpenAsync();
                    await using var chkCmd = chkConn.CreateCommand();
                    chkCmd.CommandText = "SELECT CurrentVersion FROM [sync].[ServerState] WITH (NOLOCK) WHERE DatabaseId = '2026';";
                    var verWhileBlocked = Convert.ToInt64(await chkCmd.ExecuteScalarAsync());
                    Assert.Equal(1, verWhileBlocked);
                }
            });

            // 3. Execute Pull 1 using hooked reader
            var syncProvider = ctx.CreateSyncProvider();
            var leaseManager = new LocalPullLeaseManager(syncProvider, NullLogger<LocalPullLeaseManager>.Instance);
            var coordinator = new LocalPullTransactionCoordinator(syncProvider, NullLogger<LocalPullTransactionCoordinator>.Instance);
            var pullConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Sync:PullEnabled"] = "true",
                ["Sync:PushEnabled"] = "false",
                ["Sync:AuthoritativeTrackingEnabled"] = "false"
            }).Build();

            var pullService = new LocalDailyPullService(syncProvider, leaseManager, hookedReader, coordinator, pullConfig, NullLogger<LocalDailyPullService>.Instance, new LocalScopeBaselineService(NullLogger<LocalScopeBaselineService>.Instance));
            var pullResult1 = await pullService.PullDailyChangesAsync(CancellationToken.None);

            // 4. Invariant checks for Pull 1:
            // FinalServerVersion equals exactly the fenced H_exec (1)
            Assert.Equal(1, pullResult1.FinalServerVersion);
            Assert.Equal(0, pullResult1.PreviousWatermark);
            Assert.False(pullResult1.IsNoOp);

            // Local LastServerVersion equals exactly H_exec (1)
            var localVerAfterPull1 = await GetLocalServerVersionAsync(ctx.LocalConnStr, "2026");
            Assert.Equal(1, localVerAfterPull1);

            // 5. After reader released fence (upon transaction commit), writer completes
            await writerTask;
            Assert.True(writerCompleted, "Concurrent writer must complete after reader releases fence.");

            // Verify remote ServerState is now 2
            await using (var postConn = new SqlConnection(ctx.RemoteConnStr))
            {
                await postConn.OpenAsync();
                await using var postCmd = postConn.CreateCommand();
                postCmd.CommandText = "SELECT CurrentVersion FROM [sync].[ServerState] WHERE DatabaseId = '2026';";
                var verAfterWriter = Convert.ToInt64(await postCmd.ExecuteScalarAsync());
                Assert.Equal(2, verAfterWriter);
            }

            // 6. Next pull attempt advances to version 2
            var actualReader = new AzureFencedBatchReader(ctx.CreateRemoteFactory(), NullLogger<AzureFencedBatchReader>.Instance);
            var standardPullService = new LocalDailyPullService(syncProvider, leaseManager, actualReader, coordinator, pullConfig, NullLogger<LocalDailyPullService>.Instance, new LocalScopeBaselineService(NullLogger<LocalScopeBaselineService>.Instance));
            var pullResult2 = await standardPullService.PullDailyChangesAsync(CancellationToken.None);

            Assert.Equal(2, pullResult2.FinalServerVersion);
            Assert.Equal(1, pullResult2.PreviousWatermark);
            Assert.False(pullResult2.IsNoOp);

            var localVerAfterPull2 = await GetLocalServerVersionAsync(ctx.LocalConnStr, "2026");
            Assert.Equal(2, localVerAfterPull2);
        }

        #endregion
    }
}

