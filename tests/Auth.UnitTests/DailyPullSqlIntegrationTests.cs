#nullable enable
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Auth.Infrastructure.Sync.Pull;
using Auth.Infrastructure.Sync.Push;
using Core.Exceptions;
using Core.Interfaces;
using Core.Models.Sync;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Auth.UnitTests
{
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
                var remoteName = $"IProgramPullRemote_{suffix}";
                var localName = $"IProgramPullLocal_{suffix}";

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
                        CREATE DATABASE [{LocalDbName}];
                        ALTER DATABASE [{LocalDbName}] SET COMPATIBILITY_LEVEL = 120;";
                    await createCmd.ExecuteNonQueryAsync();
                }

                // Initialize Remote schema & tables
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
                            [DatabaseId] VARCHAR(10) NOT NULL PRIMARY KEY,
                            [CurrentVersion] BIGINT NOT NULL,
                            [LastUpdatedUtc] DATETIME2 NOT NULL
                        );

                        CREATE TABLE [sync].[ServerChangeFeed] (
                            [FeedId] BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                            [ServerVersion] BIGINT NOT NULL,
                            [DatabaseId] VARCHAR(10) NOT NULL,
                            [EntityType] VARCHAR(50) NOT NULL,
                            [EntitySyncId] UNIQUEIDENTIFIER NOT NULL,
                            [OperationType] VARCHAR(20) NOT NULL,
                            [OriginDeviceId] UNIQUEIDENTIFIER NOT NULL,
                            [TimestampUtc] DATETIME2 NOT NULL
                        );
                        CREATE UNIQUE INDEX [IX_Remote_Feed_Ver] ON [sync].[ServerChangeFeed]([DatabaseId], [ServerVersion]);

                        CREATE TABLE [sync].[Tombstones] (
                            [TombstoneId] BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                            [DatabaseId] VARCHAR(10) NOT NULL,
                            [EntityType] VARCHAR(50) NOT NULL,
                            [EntitySyncId] UNIQUEIDENTIFIER NOT NULL,
                            [ServerVersion] BIGINT NOT NULL,
                            [DeletedAtUtc] DATETIME2 NOT NULL
                        );
                        CREATE UNIQUE INDEX [IX_Remote_Tombstone_Ver] ON [sync].[Tombstones]([DatabaseId], [EntityType], [EntitySyncId], [ServerVersion]);";
                    await cmd.ExecuteNonQueryAsync();

                    // Seed initial ServerState
                    await using var seedStateCmd = remoteConn.CreateCommand();
                    seedStateCmd.CommandText = "INSERT INTO [sync].[ServerState] (DatabaseId, CurrentVersion, LastUpdatedUtc) VALUES (@DatabaseId, 0, SYSUTCDATETIME());";
                    seedStateCmd.Parameters.AddWithValue("@DatabaseId", CanonicalDbId);
                    await seedStateCmd.ExecuteNonQueryAsync();
                }

                // Initialize Local schema & tables
                await using (var localConn = new SqlConnection(LocalConnStr))
                {
                    await localConn.OpenAsync();
                    await using var cmd = localConn.CreateCommand();
                    cmd.CommandText = @"
                        IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'sync')
                            EXEC('CREATE SCHEMA [sync]');

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

                        CREATE TABLE [sync].[LocalState] (
                            [DatabaseId] VARCHAR(10) NOT NULL PRIMARY KEY,
                            [LastServerVersion] BIGINT NOT NULL,
                            [LastSuccessfulPullUtc] DATETIME2 NULL,
                            [LastSyncAttemptUtc] DATETIME2 NULL,
                            [LastSyncError] NVARCHAR(MAX) NULL,
                            [ActiveLeaseToken] UNIQUEIDENTIFIER NULL,
                            [LeaseExpiresAtUtc] DATETIME2 NULL,
                            [RowVersion] ROWVERSION
                        );

                        CREATE TABLE [sync].[LocalOutbox] (
                            [OutboxId] BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                            [DatabaseId] VARCHAR(10) NOT NULL,
                            [EntityType] VARCHAR(50) NOT NULL,
                            [EntitySyncId] UNIQUEIDENTIFIER NOT NULL,
                            [OperationType] VARCHAR(20) NOT NULL,
                            [Status] VARCHAR(20) NOT NULL,
                            [CreatedAtUtc] DATETIME2 NOT NULL,
                            [ProcessedAtUtc] DATETIME2 NULL
                        );";
                    await cmd.ExecuteNonQueryAsync();

                    // Seed initial LocalState
                    await using var seedLocalCmd = localConn.CreateCommand();
                    seedLocalCmd.CommandText = "INSERT INTO [sync].[LocalState] (DatabaseId, LastServerVersion) VALUES (@DatabaseId, 0);";
                    seedLocalCmd.Parameters.AddWithValue("@DatabaseId", CanonicalDbId);
                    await seedLocalCmd.ExecuteNonQueryAsync();
                }
            }

            public async ValueTask DisposeAsync()
            {
                // Safety assertion: Refuse to drop any database that is not an isolated test variant
                if (!RemoteDbName.StartsWith("IProgramPullRemote_") || !LocalDbName.StartsWith("IProgramPullLocal_"))
                {
                    throw new InvalidOperationException("CRITICAL SAFETY VIOLATION: Refusing to drop database without test prefix.");
                }

                try
                {
                    await using var masterConn = new SqlConnection(MasterConnStr);
                    await masterConn.OpenAsync();

                    await using var dropCmd = masterConn.CreateCommand();
                    dropCmd.CommandText = $@"
                        IF DB_ID('{RemoteDbName}') IS NOT NULL
                        BEGIN
                            ALTER DATABASE [{RemoteDbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                            DROP DATABASE [{RemoteDbName}];
                        END;
                        IF DB_ID('{LocalDbName}') IS NOT NULL
                        BEGIN
                            ALTER DATABASE [{LocalDbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                            DROP DATABASE [{LocalDbName}];
                        END;";
                    await dropCmd.ExecuteNonQueryAsync();
                }
                catch
                {
                    // Best-effort cleanup
                }
            }

            public IRemoteDatabaseConnectionFactory CreateRemoteFactory() => new TestRemoteDatabaseConnectionFactory(RemoteConnStr);
            public ISyncConnectionProvider CreateSyncProvider() => new TestSyncConnectionProvider(LocalConnStr, RemoteConnStr, CanonicalDbId);

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
                    NullLogger<LocalDailyPullService>.Instance);
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
            public bool IsReadOnlyMode => false;
            public bool IsLocalFirstEnabled => true;

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

            // Drop unique index temporarily to insert duplicate version in feed
            await using (var remoteConn = new SqlConnection(ctx.RemoteConnStr))
            {
                await remoteConn.OpenAsync();
                await using var cmd = remoteConn.CreateCommand();
                cmd.CommandText = @"
                    DROP INDEX [IX_Remote_Feed_Ver] ON [sync].[ServerChangeFeed];

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

            // Seed Local Outbox with a real SQL row
            await using (var localConn = new SqlConnection(ctx.LocalConnStr))
            {
                await localConn.OpenAsync();
                await using var cmd = localConn.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO [sync].[LocalOutbox]
                    (DatabaseId, EntityType, EntitySyncId, OperationType, Status, CreatedAtUtc)
                    VALUES
                    ('2026', 'Daily', NEWID(), 'INSERT', @Status, SYSUTCDATETIME());";
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
            var tombstonesHashBefore = await ComputeTableHashAsync(ctx.RemoteConnStr, "SELECT TombstoneId, DatabaseId, EntityType, EntitySyncId, ServerVersion FROM [sync].[Tombstones] ORDER BY TombstoneId;");

            // Execute actual AzureFencedBatchReader
            var reader = new AzureFencedBatchReader(ctx.CreateRemoteFactory(), NullLogger<AzureFencedBatchReader>.Instance);
            var batch = await reader.ReadFencedBatchAsync("2026", 0, CancellationToken.None);

            Assert.NotNull(batch);
            Assert.Single(batch.Commands);

            // Compute post-reader snapshot hashes
            var dailyHashAfter = await ComputeTableHashAsync(ctx.RemoteConnStr, "SELECT Id, Name, DailyDate, Closed, IsActive, SyncId FROM [dbo].[Daily] ORDER BY Id;");
            var serverStateAfter = await ComputeTableHashAsync(ctx.RemoteConnStr, "SELECT DatabaseId, CurrentVersion FROM [sync].[ServerState] ORDER BY DatabaseId;");
            var feedHashAfter = await ComputeTableHashAsync(ctx.RemoteConnStr, "SELECT FeedId, ServerVersion, DatabaseId, EntityType, EntitySyncId, OperationType FROM [sync].[ServerChangeFeed] ORDER BY FeedId;");
            var tombstonesHashAfter = await ComputeTableHashAsync(ctx.RemoteConnStr, "SELECT TombstoneId, DatabaseId, EntityType, EntitySyncId, ServerVersion FROM [sync].[Tombstones] ORDER BY TombstoneId;");

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
    }
}
