#nullable enable
using System;
using System.Collections.Generic;
using Microsoft.Data.SqlClient;
using System.Threading;
using System.Threading.Tasks;
using Auth.Infrastructure;
using Auth.Infrastructure.Services;
using Auth.Infrastructure.Sync;
using Auth.Infrastructure.Sync.Authoritative;
using Auth.Infrastructure.Sync.Push;
using Auth.Infrastructure.Sync.Pull;
using Core.Exceptions;
using Core.Interfaces;
using Core.Models;
using Core.Models.Sync;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Persistence.Repository;
using Xunit;

namespace Auth.UnitTests
{
    [CollectionDefinition("LocalOnlyProductionSqlIntegration", DisableParallelization = true)]
    public class LocalOnlyProductionSqlIntegrationCollection
    {
    }

    [Trait("Category", "LocalDbRequired")]
    [Collection("LocalOnlyProductionSqlIntegration")]
    public class LocalOnlyProductionSqlIntegrationTests
    {
        private const string MasterConnStr = "Server=localhost;Database=master;Integrated Security=True;TrustServerCertificate=True;";

        private sealed class LocalOnlyFixtureContext : IAsyncDisposable
        {
            public string LocalDbName { get; }
            public string LocalConnStr { get; }
            public string CanonicalDbId { get; } = "2026";

            private LocalOnlyFixtureContext(string localDbName)
            {
                LocalDbName = localDbName;
                LocalConnStr = $"Server=localhost;Database={localDbName};Integrated Security=True;TrustServerCertificate=True;";
            }

            public static async Task<LocalOnlyFixtureContext> CreateAsync()
            {
                var suffix = Guid.NewGuid().ToString("N")[..8];
                var localName = $"LocalOnlyFixture_2026_{suffix}";

                var ctx = new LocalOnlyFixtureContext(localName);
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
                        IF DB_ID('{LocalDbName}') IS NOT NULL
                        BEGIN
                            ALTER DATABASE [{LocalDbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                            DROP DATABASE [{LocalDbName}];
                        END;
                        CREATE DATABASE [{LocalDbName}];
                        ALTER DATABASE [{LocalDbName}] SET COMPATIBILITY_LEVEL = 120;";
                    await createCmd.ExecuteNonQueryAsync();
                }

                await using (var conn = new SqlConnection(LocalConnStr))
                {
                    await conn.OpenAsync();
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"
                        IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'sync')
                            EXEC('CREATE SCHEMA [sync]');

                        -- AspNetUsers
                        CREATE TABLE [dbo].[AspNetUsers] (
                            [Id] NVARCHAR(450) NOT NULL PRIMARY KEY,
                            [UserName] NVARCHAR(256) NULL,
                            [NormalizedUserName] NVARCHAR(256) NULL,
                            [Email] NVARCHAR(256) NULL,
                            [NormalizedEmail] NVARCHAR(256) NULL,
                            [EmailConfirmed] BIT NOT NULL DEFAULT(0),
                            [PasswordHash] NVARCHAR(MAX) NULL,
                            [SecurityStamp] NVARCHAR(MAX) NULL,
                            [ConcurrencyStamp] NVARCHAR(MAX) NULL,
                            [PhoneNumber] NVARCHAR(MAX) NULL,
                            [PhoneNumberConfirmed] BIT NOT NULL DEFAULT(0),
                            [TwoFactorEnabled] BIT NOT NULL DEFAULT(0),
                            [LockoutEnd] DATETIMEOFFSET NULL,
                            [LockoutEnabled] BIT NOT NULL DEFAULT(0),
                            [AccessFailedCount] INT NOT NULL DEFAULT(0),
                            [DisplayName] NVARCHAR(MAX) NULL,
                            [DisplayImage] NVARCHAR(MAX) NULL
                        );
                        CREATE UNIQUE INDEX [IX_AspNetUsers_NormalizedUserName] ON [dbo].[AspNetUsers]([NormalizedUserName]) WHERE [NormalizedUserName] IS NOT NULL;

                        -- AspNetRoles
                        CREATE TABLE [dbo].[AspNetRoles] (
                            [Id] NVARCHAR(450) NOT NULL PRIMARY KEY,
                            [Name] NVARCHAR(256) NULL,
                            [NormalizedName] NVARCHAR(256) NULL,
                            [ConcurrencyStamp] NVARCHAR(MAX) NULL
                        );

                        -- AspNetUserRoles
                        CREATE TABLE [dbo].[AspNetUserRoles] (
                            [UserId] NVARCHAR(450) NOT NULL,
                            [RoleId] NVARCHAR(450) NOT NULL,
                            PRIMARY KEY ([UserId], [RoleId]),
                            CONSTRAINT [FK_AspNetUserRoles_AspNetUsers] FOREIGN KEY ([UserId]) REFERENCES [dbo].[AspNetUsers]([Id]) ON DELETE CASCADE,
                            CONSTRAINT [FK_AspNetUserRoles_AspNetRoles] FOREIGN KEY ([RoleId]) REFERENCES [dbo].[AspNetRoles]([Id]) ON DELETE CASCADE
                        );

                        -- Departments
                        CREATE TABLE [dbo].[Departments] (
                            [Id] INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                            [Name] NVARCHAR(200) NOT NULL,
                            [CreatedBy] NVARCHAR(100) NULL,
                            [CreatedAt] DATETIME2 NOT NULL DEFAULT(SYSUTCDATETIME()),
                            [UpdatedBy] NVARCHAR(100) NULL,
                            [UpdatedAt] DATETIME2 NULL,
                            [DeactivatedBy] NVARCHAR(100) NULL,
                            [DeactivatedAt] DATETIME2 NULL,
                            [IsActive] BIT NOT NULL DEFAULT(1),
                            [SyncId] UNIQUEIDENTIFIER NOT NULL DEFAULT(NEWID())
                        );
                        CREATE UNIQUE INDEX [IX_Departments_SyncId] ON [dbo].[Departments]([SyncId]);

                        -- Employees
                        CREATE TABLE [dbo].[Employees] (
                            [Id] NVARCHAR(14) NOT NULL PRIMARY KEY,
                            [Name] NVARCHAR(100) NOT NULL,
                            [TegaraCode] INT NULL,
                            [TabCode] INT NULL,
                            [Collage] NVARCHAR(15) NULL,
                            [Section] NVARCHAR(25) NULL,
                            [Email] NVARCHAR(250) NULL,
                            [DepartmentId] INT NULL,
                            [CreatedBy] NVARCHAR(100) NULL,
                            [CreatedAt] DATETIME2 NOT NULL DEFAULT(SYSUTCDATETIME()),
                            [UpdatedBy] NVARCHAR(100) NULL,
                            [UpdatedAt] DATETIME2 NULL,
                            [DeactivatedBy] NVARCHAR(100) NULL,
                            [DeactivatedAt] DATETIME2 NULL,
                            [IsActive] BIT NOT NULL DEFAULT(1),
                            [SyncId] UNIQUEIDENTIFIER NOT NULL DEFAULT(NEWID()),
                            CONSTRAINT [FK_Employees_Departments] FOREIGN KEY ([DepartmentId]) REFERENCES [dbo].[Departments]([Id]) ON DELETE SET NULL
                        );
                        CREATE UNIQUE INDEX [IX_Employees_SyncId] ON [dbo].[Employees]([SyncId]);

                        -- Daily
                        CREATE TABLE [dbo].[Daily] (
                            [Id] INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                            [Name] NVARCHAR(200) NOT NULL,
                            [DailyDate] DATETIME2 NOT NULL,
                            [Closed] BIT NOT NULL DEFAULT(0),
                            [CreatedBy] NVARCHAR(100) NULL,
                            [CreatedAt] DATETIME2 NOT NULL DEFAULT(SYSUTCDATETIME()),
                            [UpdatedBy] NVARCHAR(100) NULL,
                            [UpdatedAt] DATETIME2 NULL,
                            [DeactivatedBy] NVARCHAR(100) NULL,
                            [DeactivatedAt] DATETIME2 NULL,
                            [IsActive] BIT NOT NULL DEFAULT(1),
                            [SyncId] UNIQUEIDENTIFIER NOT NULL DEFAULT(NEWID())
                        );
                        CREATE UNIQUE INDEX [IX_Daily_SyncId] ON [dbo].[Daily]([SyncId]);

                        -- Form
                        CREATE TABLE [dbo].[Form] (
                            [Id] INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                            [SyncId] UNIQUEIDENTIFIER NOT NULL DEFAULT(NEWID()),
                            [DailyId] INT NULL,
                            [Name] NVARCHAR(200) NOT NULL,
                            [Description] NVARCHAR(MAX) NULL,
                            [Index] INT NULL,
                            [CreatedBy] NVARCHAR(100) NULL,
                            [CreatedAt] DATETIME2 NOT NULL DEFAULT(SYSUTCDATETIME()),
                            [UpdatedBy] NVARCHAR(100) NULL,
                            [UpdatedAt] DATETIME2 NULL,
                            [DeactivatedBy] NVARCHAR(100) NULL,
                            [DeactivatedAt] DATETIME2 NULL,
                            [IsActive] BIT NOT NULL DEFAULT(1),
                            CONSTRAINT [FK_Form_Daily] FOREIGN KEY ([DailyId]) REFERENCES [dbo].[Daily]([Id]) ON DELETE CASCADE
                        );
                        CREATE UNIQUE INDEX [IX_Form_SyncId] ON [dbo].[Form]([SyncId]);

                        -- FormDetails
                        CREATE TABLE [dbo].[FormDetails] (
                            [Id] INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                            [SyncId] UNIQUEIDENTIFIER NOT NULL DEFAULT(NEWID()),
                            [FormId] INT NOT NULL,
                            [EmployeeId] NVARCHAR(14) NOT NULL,
                            [Amount] FLOAT NOT NULL,
                            [OrderNum] INT NOT NULL,
                            [IsReviewed] BIT NOT NULL DEFAULT(0),
                            [IsReviewedBy] NVARCHAR(100) NULL,
                            [ReviewedAt] DATETIME2 NULL,
                            [ReviewComments] NVARCHAR(MAX) NULL,
                            [IsSummaryReviewed] BIT NOT NULL DEFAULT(0),
                            [IsSummaryReviewedBy] NVARCHAR(100) NULL,
                            [SummaryReviewedAt] DATETIME2 NULL,
                            [SummaryComments] NVARCHAR(MAX) NULL,
                            [SummaryReviewMethod] NVARCHAR(20) NULL,
                            [CreatedBy] NVARCHAR(100) NULL,
                            [CreatedAt] DATETIME2 NOT NULL DEFAULT(SYSUTCDATETIME()),
                            [UpdatedBy] NVARCHAR(100) NULL,
                            [UpdatedAt] DATETIME2 NULL,
                            [DeactivatedBy] NVARCHAR(100) NULL,
                            [DeactivatedAt] DATETIME2 NULL,
                            [IsActive] BIT NOT NULL DEFAULT(1),
                            CONSTRAINT [FK_FormDetails_Form] FOREIGN KEY ([FormId]) REFERENCES [dbo].[Form]([Id]) ON DELETE CASCADE,
                            CONSTRAINT [FK_FormDetails_Employees] FOREIGN KEY ([EmployeeId]) REFERENCES [dbo].[Employees]([Id])
                        );
                        CREATE UNIQUE INDEX [IX_FormDetails_SyncId] ON [dbo].[FormDetails]([SyncId]);

                        -- sync schema infrastructure tables
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

                        INSERT INTO [sync].[LocalState] ([DatabaseId], [DeviceId], [DeviceName], [LastServerVersion])
                        VALUES ('2026', NEWID(), 'TestDevice', 10);

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
                    ";
                    await cmd.ExecuteNonQueryAsync();
                }
            }

            public async ValueTask DisposeAsync()
            {
                try
                {
                    SqlConnection.ClearAllPools();

                    await using var masterConn = new SqlConnection(MasterConnStr);
                    await masterConn.OpenAsync();
                    await using var dropCmd = masterConn.CreateCommand();
                    dropCmd.CommandText = $@"
                        IF DB_ID('{LocalDbName}') IS NOT NULL
                        BEGIN
                            ALTER DATABASE [{LocalDbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                            DROP DATABASE [{LocalDbName}];
                        END;";
                    await dropCmd.ExecuteNonQueryAsync();
                }
                catch
                {
                    // Best effort cleanup of transient fixture
                }
            }
        }

        private static IConfiguration CreateLocalOnlyConfiguration(LocalOnlyFixtureContext ctx)
        {
            var inMemorySettings = new Dictionary<string, string?>
            {
                { "ConnectionStrings:DefaultConnection", "Server=127.0.0.1,65534;Database=DISABLED_REMOTE_TRIPWIRE_2026;Connect Timeout=1;" },
                { "ConnectionStrings:CON2027", "Server=127.0.0.1,65534;Database=DISABLED_REMOTE_TRIPWIRE_2027;Connect Timeout=1;" },
                { "ConnectionStrings:LocalConnection2026", ctx.LocalConnStr },
                { "ConnectionStrings:LocalConnection2027", ctx.LocalConnStr },
                { "DatabaseSettings:Databases:0:Id", "2026" },
                { "DatabaseSettings:Databases:0:Name", "بيانات 2026" },
                { "DatabaseSettings:Databases:0:ConnectionStringName", "DefaultConnection" },
                { "DatabaseSettings:Databases:1:Id", "2027" },
                { "DatabaseSettings:Databases:1:Name", "بيانات 2027" },
                { "DatabaseSettings:Databases:1:ConnectionStringName", "CON2027" },
                { "LocalFirst:Enabled", "true" },
                { "LocalFirst:ReadOnlyMode", "false" },
                { "LocalFirst:LocalOnlyProduction", "true" },
                { "LocalFirst:Mode", "LocalOnlyProduction" },
                { "LocalFirst:SqlServerInstance", "localhost" },
                { "LocalFirst:Databases:0:Id", "2026" },
                { "LocalFirst:Databases:0:LocalDatabaseName", ctx.LocalDbName },
                { "LocalFirst:Databases:0:LocalConnectionStringName", "LocalConnection2026" },
                { "LocalFirst:Databases:1:Id", "2027" },
                { "LocalFirst:Databases:1:LocalDatabaseName", ctx.LocalDbName },
                { "LocalFirst:Databases:1:LocalConnectionStringName", "LocalConnection2027" },
                { "Sync:AuthoritativeTrackingEnabled", "false" },
                { "Sync:PullEnabled", "false" },
                { "Sync:PushEnabled", "false" }
            };

            return new ConfigurationBuilder()
                .AddInMemoryCollection(inMemorySettings)
                .Build();
        }

        // =========================================================================
        // P0-2: Real SQL Integration Proof for Direct Local Business Writes
        // =========================================================================

        [Fact]
        public async Task LocalOnlyProduction_DatabaseTopology_AndCompatibility_IsSql2014Level120()
        {
            await using var fixture = await LocalOnlyFixtureContext.CreateAsync();

            await using var conn = new SqlConnection(fixture.LocalConnStr);
            await conn.OpenAsync();

            // 1. Assert DB_NAME() is the expected transient fixture catalog (never operational DBs)
            await using var cmdDb = conn.CreateCommand();
            cmdDb.CommandText = "SELECT DB_NAME();";
            var activeDbName = (string)(await cmdDb.ExecuteScalarAsync())!;
            Assert.Equal(fixture.LocalDbName, activeDbName);
            Assert.StartsWith("LocalOnlyFixture_2026_", activeDbName);

            // 2. Assert SQL Server compatibility level is 120 (SQL Server 2014)
            await using var cmdCompat = conn.CreateCommand();
            cmdCompat.CommandText = "SELECT compatibility_level FROM sys.databases WHERE name = DB_NAME();";
            var compatLevel = Convert.ToInt32(await cmdCompat.ExecuteScalarAsync());
            Assert.Equal(120, compatLevel);
        }

        [Fact]
        public async Task LocalOnlyProduction_DirectBusinessWrites_Succeed_WithoutLocalOutbox_Or_AuthoritativeTracking()
        {
            await using var fixture = await LocalOnlyFixtureContext.CreateAsync();
            var config = CreateLocalOnlyConfiguration(fixture);

            var mockAccessor = new Mock<IHttpContextAccessor>();
            var context = new DefaultHttpContext();
            context.Request.Headers["X-Database-Id"] = "2026";
            mockAccessor.Setup(a => a.HttpContext).Returns(context);

            var dbProvider = new DbConnectionProvider(mockAccessor.Object, config);
            Assert.True(dbProvider.IsLocalOnlyProduction);
            Assert.False(dbProvider.IsReadOnlyMode);

            var options = new DbContextOptionsBuilder<ApplicationContext>()
                .UseSqlServer(fixture.LocalConnStr, o => o.UseCompatibilityLevel(120))
                .Options;

            var mockTracker = new Mock<IAuthoritativeDailyMutationTracker>();
            var mockGuard = new Mock<IAuthoritativeDatabaseBindingGuard>();
            var mockBaseline = new Mock<ILocalScopeBaselineService>();

            var employeeNationalId = "29501011234567";
            var deptSyncId = Guid.NewGuid();
            var empSyncId = Guid.NewGuid();
            var dailySyncId = Guid.NewGuid();
            var formSyncId = Guid.NewGuid();
            var detailsSyncId = Guid.NewGuid();

            int deptId;
            int dailyId;
            int formId;
            int detailsId;

            // 1. Representative Department Write
            using (var dbContext = new ApplicationContext(options))
            {
                var uow = new UnitOfWork(
                    dbContext, dbProvider, mockTracker.Object, mockGuard.Object, config, mockBaseline.Object);

                var dept = new Department
                {
                    Name = "General Management Department",
                    SyncId = deptSyncId,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                };

                dbContext.Departments.Add(dept);
                var saveCount = await uow.SaveChangesAsync();
                Assert.True(saveCount > 0);
                deptId = dept.Id;
                Assert.True(deptId > 0);
            }

            // 2. Employee INSERT
            using (var dbContext = new ApplicationContext(options))
            {
                var uow = new UnitOfWork(
                    dbContext, dbProvider, mockTracker.Object, mockGuard.Object, config, mockBaseline.Object);

                var emp = new Employee
                {
                    Id = employeeNationalId,
                    Name = "Mahmoud Hassan",
                    TegaraCode = 101,
                    TabCode = 202,
                    Collage = "Engineering",
                    Section = "Systems",
                    DepartmentId = deptId,
                    SyncId = empSyncId,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                };

                dbContext.Employees.Add(emp);
                var saveCount = await uow.SaveChangesAsync();
                Assert.True(saveCount > 0);
            }

            // 3. Employee UPDATE
            using (var dbContext = new ApplicationContext(options))
            {
                var uow = new UnitOfWork(
                    dbContext, dbProvider, mockTracker.Object, mockGuard.Object, config, mockBaseline.Object);

                var emp = await dbContext.Employees.FirstAsync(e => e.Id == employeeNationalId);
                emp.Name = "Mahmoud Hassan (Promoted)";
                emp.Section = "Senior Systems";
                emp.UpdatedAt = DateTime.UtcNow;

                var saveCount = await uow.SaveChangesAsync();
                Assert.True(saveCount > 0);
            }

            // 4. Daily INSERT
            using (var dbContext = new ApplicationContext(options))
            {
                var uow = new UnitOfWork(
                    dbContext, dbProvider, mockTracker.Object, mockGuard.Object, config, mockBaseline.Object);

                var daily = new Daily
                {
                    Name = "Direct Local Daily Batch 2026-09-24",
                    DailyDate = new DateTime(2026, 9, 24),
                    Closed = false,
                    SyncId = dailySyncId,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                };

                dbContext.Set<Daily>().Add(daily);
                var saveCount = await uow.SaveChangesAsync();
                Assert.True(saveCount > 0);
                dailyId = daily.Id;
                Assert.True(dailyId > 0);
            }

            // 5. Form INSERT (referencing Daily)
            using (var dbContext = new ApplicationContext(options))
            {
                var uow = new UnitOfWork(
                    dbContext, dbProvider, mockTracker.Object, mockGuard.Object, config, mockBaseline.Object);

                var form = new Form
                {
                    DailyId = dailyId,
                    Name = "Production Form No. 1",
                    Description = "Direct Local Persistence Form",
                    Index = 1,
                    SyncId = formSyncId,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                };

                dbContext.Set<Form>().Add(form);
                var saveCount = await uow.SaveChangesAsync();
                Assert.True(saveCount > 0);
                formId = form.Id;
                Assert.True(formId > 0);
            }

            // 6. FormDetails INSERT (referencing Form and Employee)
            using (var dbContext = new ApplicationContext(options))
            {
                var uow = new UnitOfWork(
                    dbContext, dbProvider, mockTracker.Object, mockGuard.Object, config, mockBaseline.Object);

                var details = new FormDetails
                {
                    FormId = formId,
                    EmployeeId = employeeNationalId,
                    Amount = 2500.75,
                    OrderNum = 1,
                    IsReviewed = false,
                    SyncId = detailsSyncId,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                };

                dbContext.Set<FormDetails>().Add(details);
                var saveCount = await uow.SaveChangesAsync();
                Assert.True(saveCount > 0);
                detailsId = details.Id;
                Assert.True(detailsId > 0);
            }

            // 7. Verify all rows exist in the local database with expected relationships
            using (var dbContext = new ApplicationContext(options))
            {
                var loadedDept = await dbContext.Departments.FirstOrDefaultAsync(d => d.Id == deptId);
                Assert.NotNull(loadedDept);
                Assert.Equal("General Management Department", loadedDept!.Name);

                var loadedEmp = await dbContext.Employees.FirstOrDefaultAsync(e => e.Id == employeeNationalId);
                Assert.NotNull(loadedEmp);
                Assert.Equal("Mahmoud Hassan (Promoted)", loadedEmp!.Name);
                Assert.Equal("Senior Systems", loadedEmp.Section);
                Assert.Equal(deptId, loadedEmp.DepartmentId);

                var loadedDaily = await dbContext.Set<Daily>().FirstOrDefaultAsync(d => d.Id == dailyId);
                Assert.NotNull(loadedDaily);
                Assert.Equal("Direct Local Daily Batch 2026-09-24", loadedDaily!.Name);

                var loadedForm = await dbContext.Set<Form>().Include(f => f.FormDetails).FirstOrDefaultAsync(f => f.Id == formId);
                Assert.NotNull(loadedForm);
                Assert.Equal(dailyId, loadedForm!.DailyId);
                Assert.Single(loadedForm.FormDetails);

                var loadedDetail = loadedForm.FormDetails.First();
                Assert.Equal(employeeNationalId, loadedDetail.EmployeeId);
                Assert.Equal(2500.75, loadedDetail.Amount);
            }

            // 8. Prove Invariant: sync.LocalOutbox count remains exactly ZERO
            await using (var sqlConn = new SqlConnection(fixture.LocalConnStr))
            {
                await sqlConn.OpenAsync();
                await using var cmdOutbox = sqlConn.CreateCommand();
                cmdOutbox.CommandText = "SELECT COUNT(*) FROM [sync].[LocalOutbox];";
                var outboxCount = (int)(await cmdOutbox.ExecuteScalarAsync())!;
                Assert.Equal(0, outboxCount);

                // 9. Prove Invariant: No ServerChangeFeed or ProcessedOperations rows are created
                await using var cmdFeed = sqlConn.CreateCommand();
                cmdFeed.CommandText = "SELECT COUNT(*) FROM [sync].[ServerChangeFeed];";
                var feedCount = (int)(await cmdFeed.ExecuteScalarAsync())!;
                Assert.Equal(0, feedCount);

                await using var cmdProc = sqlConn.CreateCommand();
                cmdProc.CommandText = "SELECT COUNT(*) FROM [sync].[ProcessedOperations];";
                var procCount = (int)(await cmdProc.ExecuteScalarAsync())!;
                Assert.Equal(0, procCount);
            }

            // 10. Prove Invariant: Authoritative tracker was NEVER invoked
            mockTracker.Verify(t => t.PrepareAuthoritativeBatchAsync(
                It.IsAny<System.Data.Common.DbConnection>(),
                It.IsAny<System.Data.Common.DbTransaction>(),
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<CapturedAuthoritativeDailyMutation>>(),
                It.IsAny<CancellationToken>()), Times.Never);
            mockTracker.Verify(t => t.TrackDailyMutationsAsync(
                It.IsAny<System.Data.Common.DbConnection>(),
                It.IsAny<System.Data.Common.DbTransaction>(),
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<CapturedAuthoritativeDailyMutation>>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }

        // =========================================================================
        // P0-3: Zero-Remote Runtime Evidence & Fail-Closed Guards
        // =========================================================================

        [Fact]
        public async Task LocalOnlyProduction_RemoteOperations_FailClosed_WithZeroRemoteConnections()
        {
            await using var fixture = await LocalOnlyFixtureContext.CreateAsync();
            var config = CreateLocalOnlyConfiguration(fixture);

            var mockAccessor = new Mock<IHttpContextAccessor>();
            var context = new DefaultHttpContext();
            context.Request.Headers["X-Database-Id"] = "2026";
            mockAccessor.Setup(a => a.HttpContext).Returns(context);

            var provider = new DbConnectionProvider(mockAccessor.Object, config);

            // A. GetRemoteConnectionString throws fail-closed exception
            var exRemote = Assert.Throws<InvalidOperationException>(
                () => provider.GetRemoteConnectionString("2026"));
            Assert.Contains("AZURE_REMOTE_DISABLED_IN_LOCAL_ONLY_PRODUCTION", exRemote.Message);

            // B. GetManualSyncRemoteConnectionString throws fail-closed exception
            var exManual = Assert.Throws<InvalidOperationException>(
                () => provider.GetManualSyncRemoteConnectionString("2026"));
            Assert.Contains("MANUAL_SYNC_REMOTE_DISABLED_IN_LOCAL_ONLY_PRODUCTION", exManual.Message);

            // C. PushPendingOutboxAsync throws fail-closed exception before opening any connection
            var mockRemoteFactory = new Mock<IRemoteDatabaseConnectionFactory>();
            var mockPushTxCoord = new Mock<IAzurePushTransactionCoordinator>();
            var mockPushLease = new Mock<ILocalPushLeaseManager>();
            var mockBaseline = new Mock<ILocalScopeBaselineService>();
            var pushService = new LocalOutboxPushService(
                provider, mockRemoteFactory.Object, mockPushTxCoord.Object, mockPushLease.Object, config, NullLogger<LocalOutboxPushService>.Instance, mockBaseline.Object);

            var exPush = await Assert.ThrowsAsync<InvalidOperationException>(
                () => pushService.PushPendingOutboxAsync(CancellationToken.None, isExplicitManual: true));
            Assert.Contains("SYNC_PUSH_DISABLED_IN_LOCAL_ONLY_PRODUCTION", exPush.Message);
            mockRemoteFactory.Verify(f => f.CreateOpenConnectionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

            // D. PullDailyChangesAsync throws fail-closed exception before opening any connection
            var mockPullLease = new Mock<ILocalPullLeaseManager>();
            var mockPullTxCoord = new Mock<ILocalPullTransactionCoordinator>();
            var mockBatchReader = new Mock<IAzureFencedBatchReader>();
            var pullService = new LocalDailyPullService(
                provider, mockPullLease.Object, mockBatchReader.Object, mockPullTxCoord.Object, config, NullLogger<LocalDailyPullService>.Instance, mockBaseline.Object);

            var exPull = await Assert.ThrowsAsync<InvalidOperationException>(
                () => pullService.PullDailyChangesAsync(CancellationToken.None, isExplicitManual: true));
            Assert.Contains("SYNC_PULL_DISABLED_IN_LOCAL_ONLY_PRODUCTION", exPull.Message);
            mockRemoteFactory.Verify(f => f.CreateOpenConnectionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

            // E. CheckOnlineStatusAsync throws fail-closed exception before opening any connection
            var statusService = new SyncStatusService(
                provider, mockRemoteFactory.Object, mockBaseline.Object, NullLogger<SyncStatusService>.Instance);

            var exStatus = await Assert.ThrowsAsync<InvalidOperationException>(
                () => statusService.CheckOnlineStatusAsync("2026", CancellationToken.None));
            Assert.Contains("ONLINE_CHECK_DISABLED_IN_LOCAL_ONLY_PRODUCTION", exStatus.Message);
            mockRemoteFactory.Verify(f => f.CreateOpenConnectionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        // =========================================================================
        // P0-2: Zero-Remote Runtime Acceptance (Startup -> Local Read -> Login -> Business Write -> Logout -> Audit)
        // =========================================================================

        [Fact]
        public async Task LocalOnlyProduction_FullLifecycle_ZeroRemoteAcceptance_Audited()
        {
            await using var fixture = await LocalOnlyFixtureContext.CreateAsync();
            var config = CreateLocalOnlyConfiguration(fixture);

            var mockAccessor = new Mock<IHttpContextAccessor>();
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Headers["X-Database-Id"] = "2026";
            mockAccessor.Setup(a => a.HttpContext).Returns(httpContext);

            var dbProvider = new DbConnectionProvider(mockAccessor.Object, config);
            Assert.True(dbProvider.IsLocalOnlyProduction);
            Assert.False(dbProvider.IsReadOnlyMode);
            Assert.True(dbProvider.IsLocalFirstEnabled);

            // Clear in-memory audit tracker to isolate this lifecycle run
            ConnectionAuditTracker.Clear();

            var interceptor = new ReadOnlyDbConnectionInterceptor(dbProvider);
            var options = new DbContextOptionsBuilder<ApplicationContext>()
                .UseSqlServer(fixture.LocalConnStr, o => o.UseCompatibilityLevel(120))
                .AddInterceptors(interceptor)
                .Options;

            // 1. Startup & Identity Services
            using (var dbContext = new ApplicationContext(options))
            {
                var userStore = new UserStore<ApplicationUser>(dbContext);
                var roleStore = new RoleStore<IdentityRole>(dbContext);
                var passwordHasher = new PasswordHasher<ApplicationUser>();
                var userManager = new UserManager<ApplicationUser>(
                    userStore, null!, passwordHasher, null!, null!, null!, null!, null!, null!);
                var roleManager = new RoleManager<IdentityRole>(
                    roleStore, null!, null!, null!, null!);
                var mockSignIn = new Mock<SignInManager<ApplicationUser>>(
                    userManager,
                    new Mock<IHttpContextAccessor>().Object,
                    new Mock<IUserClaimsPrincipalFactory<ApplicationUser>>().Object,
                    null!, null!, null!, null!);
                mockSignIn.Setup(s => s.SignOutAsync()).Returns(Task.CompletedTask);

                var accountRepo = new AccountRepository(
                    userManager,
                    roleManager,
                    mockSignIn.Object,
                    dbContext,
                    dbProvider);

                // Seed test user in isolated local fixture DB
                var testUser = new ApplicationUser
                {
                    Id = Guid.NewGuid().ToString(),
                    UserName = "fixtureadmin",
                    NormalizedUserName = "FIXTUREADMIN",
                    Email = "admin@fixture.local",
                    NormalizedEmail = "ADMIN@FIXTURE.LOCAL",
                    SecurityStamp = Guid.NewGuid().ToString()
                };
                testUser.PasswordHash = passwordHasher.HashPassword(testUser, "LocalProduction123!");
                dbContext.Users.Add(testUser);
                await dbContext.SaveChangesAsync();

                // 2. Runtime status / local read
                var initialDepts = await dbContext.Departments.ToListAsync();
                Assert.Empty(initialDepts);

                // 3. Login / authenticated context
                var loggedInUser = await accountRepo.Login("fixtureadmin", "LocalProduction123!");
                Assert.NotNull(loggedInUser);
                Assert.Equal("fixtureadmin", loggedInUser!.UserName);

                // 4. Real Business write on Local fixture
                var mockTracker = new Mock<IAuthoritativeDailyMutationTracker>();
                var mockGuard = new Mock<IAuthoritativeDatabaseBindingGuard>();
                var mockBaseline = new Mock<ILocalScopeBaselineService>();
                var uow = new UnitOfWork(
                    dbContext, dbProvider, mockTracker.Object, mockGuard.Object, config, mockBaseline.Object);

                var dept = new Department
                {
                    Name = "Acceptance Test Dept",
                    SyncId = Guid.NewGuid(),
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                };
                dbContext.Departments.Add(dept);
                await uow.SaveChangesAsync();
                Assert.True(dept.Id > 0);

                var emp = new Employee
                {
                    Id = "29901011234567",
                    Name = "Acceptance Test Employee",
                    DepartmentId = dept.Id,
                    SyncId = Guid.NewGuid(),
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                };
                dbContext.Employees.Add(emp);
                await uow.SaveChangesAsync();

                var daily = new Daily
                {
                    Name = "Acceptance Daily",
                    DailyDate = new DateTime(2026, 9, 24),
                    Closed = false,
                    SyncId = Guid.NewGuid(),
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                };
                dbContext.Set<Daily>().Add(daily);
                await uow.SaveChangesAsync();
                Assert.True(daily.Id > 0);

                var form = new Form
                {
                    DailyId = daily.Id,
                    Name = "Acceptance Form",
                    SyncId = Guid.NewGuid(),
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                };
                dbContext.Set<Form>().Add(form);
                await uow.SaveChangesAsync();
                Assert.True(form.Id > 0);

                var formDetails = new FormDetails
                {
                    FormId = form.Id,
                    EmployeeId = emp.Id,
                    Amount = 1500.50,
                    OrderNum = 1,
                    SyncId = Guid.NewGuid(),
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                };
                dbContext.Set<FormDetails>().Add(formDetails);
                await uow.SaveChangesAsync();
                Assert.True(formDetails.Id > 0);

                // Verify persistence via read
                var persistedForm = await dbContext.Set<Form>()
                    .Include(f => f.FormDetails)
                    .FirstOrDefaultAsync(f => f.Id == form.Id);
                Assert.NotNull(persistedForm);
                Assert.Single(persistedForm!.FormDetails);

                // 5. Logout / session end
                await accountRepo.SignOut();
                mockSignIn.Verify(s => s.SignOutAsync(), Times.Once);
            }

            // 6. Prove ZERO remote / sync operations allowed
            var mockRemoteFactory = new Mock<IRemoteDatabaseConnectionFactory>();
            var mockPushTxCoord = new Mock<IAzurePushTransactionCoordinator>();
            var mockPushLease = new Mock<ILocalPushLeaseManager>();
            var mockBaseline2 = new Mock<ILocalScopeBaselineService>();
            var pushService = new LocalOutboxPushService(
                dbProvider, mockRemoteFactory.Object, mockPushTxCoord.Object, mockPushLease.Object, config, NullLogger<LocalOutboxPushService>.Instance, mockBaseline2.Object);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => pushService.PushPendingOutboxAsync(CancellationToken.None, isExplicitManual: true));

            var mockPullLease = new Mock<ILocalPullLeaseManager>();
            var mockPullTxCoord = new Mock<ILocalPullTransactionCoordinator>();
            var mockBatchReader = new Mock<IAzureFencedBatchReader>();
            var pullService = new LocalDailyPullService(
                dbProvider, mockPullLease.Object, mockBatchReader.Object, mockPullTxCoord.Object, config, NullLogger<LocalDailyPullService>.Instance, mockBaseline2.Object);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => pullService.PullDailyChangesAsync(CancellationToken.None, isExplicitManual: true));

            var statusService = new SyncStatusService(
                dbProvider, mockRemoteFactory.Object, mockBaseline2.Object, NullLogger<SyncStatusService>.Instance);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => statusService.CheckOnlineStatusAsync("2026", CancellationToken.None));

            mockRemoteFactory.Verify(f => f.CreateOpenConnectionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

            // 7. Audit Tracker Verification:
            // - Every connection was local loopback only
            // - Every connection targeted the local fixture database
            // - ZERO Azure connection attempts
            // - ZERO disallowed connection attempts
            var records = ConnectionAuditTracker.GetRecords();
            Assert.NotEmpty(records);
            Assert.All(records, r => Assert.True(r.Allowed));
            Assert.All(records, r => Assert.True(r.IsLocal));
            Assert.All(records, r => Assert.False(r.IsFallbackEndpoint));
            Assert.All(records, r => Assert.Equal(fixture.LocalDbName, r.Database, ignoreCase: true));

            var disallowed = records.Where(r => !r.Allowed || !r.IsLocal || r.IsFallbackEndpoint).ToList();
            Assert.Empty(disallowed);
        }

        [Fact]
        public async Task Test08_ChangeCaptureEnabled_AtomicallyCommitsBusinessAndOutboxRecords()
        {
            await using var fixture = await LocalOnlyFixtureContext.CreateAsync();

            var configDict = new Dictionary<string, string?>
            {
                { "LocalFirst:Enabled", "true" },
                { "LocalFirst:ReadOnlyMode", "false" },
                { "LocalFirst:LocalOnlyProduction", "true" },
                { "Sync:LocalOnlyChangeCaptureEnabled", "true" }
            };
            var config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

            var mockProvider = new Mock<ISyncConnectionProvider>();
            mockProvider.Setup(p => p.IsLocalFirstEnabled).Returns(true);
            mockProvider.Setup(p => p.IsReadOnlyMode).Returns(false);
            mockProvider.Setup(p => p.IsLocalOnlyProduction).Returns(true);
            mockProvider.Setup(p => p.IsLocalOnlyChangeCaptureEnabled).Returns(true);
            mockProvider.Setup(p => p.GetSelectedDatabaseId()).Returns("2026");

            var options = new DbContextOptionsBuilder<ApplicationContext>()
                .UseSqlServer(fixture.LocalConnStr)
                .Options;

            var deptSyncId = Guid.NewGuid();
            var dailySyncId = Guid.NewGuid();

            await using (var context = new ApplicationContext(options))
            {
                var uow = new UnitOfWork(context, mockProvider.Object, configuration: config);

                var dept = new Department
                {
                    Name = "Atomic Capture Department",
                    SyncId = deptSyncId,
                    IsActive = true
                };
                var daily = new Daily
                {
                    Name = "Atomic Daily",
                    DailyDate = DateTime.UtcNow,
                    SyncId = dailySyncId,
                    IsActive = true
                };

                context.Departments.Add(dept);
                context.Set<Daily>().Add(daily);

                var saved = await uow.SaveChangesAsync();
                Assert.True(saved >= 2);
            }

            // Verify physical SQL state: both business rows and LocalOutbox records committed atomically
            await using (var conn = new SqlConnection(fixture.LocalConnStr))
            {
                await conn.OpenAsync();

                // 1. Verify business tables
                await using var cmdDept = conn.CreateCommand();
                cmdDept.CommandText = "SELECT COUNT(*) FROM [dbo].[Departments] WHERE [SyncId] = @SyncId;";
                cmdDept.Parameters.AddWithValue("@SyncId", deptSyncId);
                var deptCount = (int)await cmdDept.ExecuteScalarAsync();
                Assert.Equal(1, deptCount);

                await using var cmdDaily = conn.CreateCommand();
                cmdDaily.CommandText = "SELECT COUNT(*) FROM [dbo].[Daily] WHERE [SyncId] = @SyncId;";
                cmdDaily.Parameters.AddWithValue("@SyncId", dailySyncId);
                var dailyCount = (int)await cmdDaily.ExecuteScalarAsync();
                Assert.Equal(1, dailyCount);

                // 2. Verify [sync].[LocalOutbox] rows
                await using var cmdOutbox = conn.CreateCommand();
                cmdOutbox.CommandText = @"
                    SELECT [AggregateType], [CommandName], [EntitySyncId], [Status], [PayloadJson]
                    FROM [sync].[LocalOutbox]
                    WHERE [DatabaseId] = '2026'
                    ORDER BY [CreatedAtUtc] ASC;";
                await using var reader = await cmdOutbox.ExecuteReaderAsync();

                var outboxRecords = new List<(string AggType, string Cmd, Guid SyncId, string Status, string Payload)>();
                while (await reader.ReadAsync())
                {
                    outboxRecords.Add((
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetGuid(2),
                        reader.GetString(3),
                        reader.GetString(4)
                    ));
                }

                Assert.Equal(2, outboxRecords.Count);
                Assert.All(outboxRecords, r => Assert.Equal("PENDING", r.Status));

                // Department (rank 0) and Daily (rank 0)
                var outboxSyncIds = outboxRecords.Select(r => r.SyncId).ToHashSet();
                Assert.Contains(deptSyncId, outboxSyncIds);
                Assert.Contains(dailySyncId, outboxSyncIds);

                foreach (var rec in outboxRecords)
                {
                    Assert.Contains("INSERT", rec.Payload);
                    Assert.Contains(rec.SyncId.ToString(), rec.Payload);
                }
            }
        }

        [Fact]
        public async Task Test09_ChangeCaptureEnabled_OutboxFailureRollsBackBusinessWrites()
        {
            await using var fixture = await LocalOnlyFixtureContext.CreateAsync();

            var configDict = new Dictionary<string, string?>
            {
                { "LocalFirst:Enabled", "true" },
                { "LocalFirst:ReadOnlyMode", "false" },
                { "LocalFirst:LocalOnlyProduction", "true" },
                { "Sync:LocalOnlyChangeCaptureEnabled", "true" }
            };
            var config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

            // Provide invalid/unsupported databaseId so LocalState query fails or throws
            var mockProvider = new Mock<ISyncConnectionProvider>();
            mockProvider.Setup(p => p.IsLocalFirstEnabled).Returns(true);
            mockProvider.Setup(p => p.IsReadOnlyMode).Returns(false);
            mockProvider.Setup(p => p.IsLocalOnlyProduction).Returns(true);
            mockProvider.Setup(p => p.IsLocalOnlyChangeCaptureEnabled).Returns(true);
            mockProvider.Setup(p => p.GetSelectedDatabaseId()).Returns("9999"); // Missing in LocalState!

            var options = new DbContextOptionsBuilder<ApplicationContext>()
                .UseSqlServer(fixture.LocalConnStr)
                .Options;

            var deptSyncId = Guid.NewGuid();

            await using (var context = new ApplicationContext(options))
            {
                var uow = new UnitOfWork(context, mockProvider.Object, configuration: config);

                var dept = new Department
                {
                    Name = "Rollback Department",
                    SyncId = deptSyncId,
                    IsActive = true
                };
                context.Departments.Add(dept);

                // Outbox coordination should fail because LocalState row for '9999' is absent
                await Assert.ThrowsAnyAsync<Exception>(() => uow.SaveChangesAsync());
            }

            // Verify business table: Department must NOT exist in the database (rolled back)
            await using (var conn = new SqlConnection(fixture.LocalConnStr))
            {
                await conn.OpenAsync();
                await using var cmdDept = conn.CreateCommand();
                cmdDept.CommandText = "SELECT COUNT(*) FROM [dbo].[Departments] WHERE [SyncId] = @SyncId;";
                cmdDept.Parameters.AddWithValue("@SyncId", deptSyncId);
                var deptCount = (int)await cmdDept.ExecuteScalarAsync();
                Assert.Equal(0, deptCount);
            }
        }
    }
}

