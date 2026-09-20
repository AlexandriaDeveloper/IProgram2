#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Auth.Infrastructure;
using Auth.Infrastructure.Sync.Authoritative;
using Core.Exceptions;
using Core.Interfaces;
using Core.Models;
using Core.Models.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Persistence.Repository;
using Xunit;

namespace Auth.UnitTests
{
    public class AuthoritativeTrackingUnitTests
    {
        #region 1. AuthoritativeDatabaseBindingGuard Tests

        [Fact]
        public void BindingGuard_ValidAzure2026_Succeeds()
        {
            var guard = new AuthoritativeDatabaseBindingGuard();
            // Should not throw
            guard.ValidateAuthoritativeAzureBinding("2026", "iprogram-sql-server.database.windows.net", "IProgramDb2026");
        }

        [Fact]
        public void BindingGuard_ValidAzure2027_Succeeds()
        {
            var guard = new AuthoritativeDatabaseBindingGuard();
            // Should not throw
            guard.ValidateAuthoritativeAzureBinding("2027", "tcp:iprogram-sql-server.database.windows.net,1433", "IProgramDb2027");
        }

        [Theory]
        [InlineData("localhost")]
        [InlineData("127.0.0.1")]
        [InlineData("::1")]
        [InlineData("[::1]")]
        [InlineData("DESKTOP-TEST\\SQLEXPRESS")]
        [InlineData("(localdb)\\MSSQLLocalDB")]
        [InlineData("internal-sql.corp.net")]
        public void BindingGuard_LocalOrNonAzureEndpoint_ThrowsAuthoritativeBindingException(string endpoint)
        {
            var guard = new AuthoritativeDatabaseBindingGuard();
            Assert.Throws<AuthoritativeBindingException>(() =>
                guard.ValidateAuthoritativeAzureBinding("2026", endpoint, "IProgramDb2026"));
        }

        [Fact]
        public void BindingGuard_YearMismatch2026Targeting2027_ThrowsAuthoritativeBindingException()
        {
            var guard = new AuthoritativeDatabaseBindingGuard();
            Assert.Throws<AuthoritativeBindingException>(() =>
                guard.ValidateAuthoritativeAzureBinding("2026", "server.database.windows.net", "IProgramDb2027"));
        }

        [Fact]
        public void BindingGuard_YearMismatch2027Targeting2026_ThrowsAuthoritativeBindingException()
        {
            var guard = new AuthoritativeDatabaseBindingGuard();
            Assert.Throws<AuthoritativeBindingException>(() =>
                guard.ValidateAuthoritativeAzureBinding("2027", "server.database.windows.net", "IProgramDb2026"));
        }

        [Fact]
        public void BindingGuard_LocalDatabaseName_ThrowsAuthoritativeBindingException()
        {
            var guard = new AuthoritativeDatabaseBindingGuard();
            Assert.Throws<AuthoritativeBindingException>(() =>
                guard.ValidateAuthoritativeAzureBinding("2026", "server.database.windows.net", "IProgramLocalDb2026"));
        }

        [Theory]
        [InlineData("")]
        [InlineData(" ")]
        [InlineData("2028")]
        [InlineData("2025")]
        public void BindingGuard_InvalidCanonicalId_ThrowsAuthoritativeBindingException(string canonicalId)
        {
            var guard = new AuthoritativeDatabaseBindingGuard();
            Assert.Throws<AuthoritativeBindingException>(() =>
                guard.ValidateAuthoritativeAzureBinding(canonicalId, "server.database.windows.net", "IProgramDb2026"));
        }

        #endregion

        #region 2. AuthoritativeTrackingSafetyInterceptor Tests

        private ApplicationContext CreateContextWithInterceptor(
            bool authoritativeTrackingEnabled,
            bool isLocalFirst,
            bool isReadOnly)
        {
            var syncProviderMock = new Mock<ISyncConnectionProvider>();
            syncProviderMock.Setup(p => p.IsLocalFirstEnabled).Returns(isLocalFirst);
            syncProviderMock.Setup(p => p.IsReadOnlyMode).Returns(isReadOnly);
            syncProviderMock.Setup(p => p.GetSelectedDatabaseId()).Returns("2026");

            var configDict = new Dictionary<string, string?>
            {
                { "Sync:AuthoritativeTrackingEnabled", authoritativeTrackingEnabled.ToString() }
            };
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

            var interceptor = new AuthoritativeTrackingSafetyInterceptor(syncProviderMock.Object, configuration);

            var options = new DbContextOptionsBuilder<ApplicationContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .AddInterceptors(interceptor)
                .Options;

            return new ApplicationContext(options);
        }

        [Fact]
        public async Task SafetyInterceptor_GateOff_DirectDailyMutation_Allowed()
        {
            using var context = CreateContextWithInterceptor(
                authoritativeTrackingEnabled: false,
                isLocalFirst: false,
                isReadOnly: false);

            var daily = new Daily
            {
                Id = 1,
                Name = "Daily Test Gate Off",
                DailyDate = DateTime.UtcNow,
                SyncId = Guid.NewGuid(),
                IsActive = true
            };

            context.Set<Daily>().Add(daily);
            var saved = await context.SaveChangesAsync();
            Assert.True(saved > 0);
        }

        [Fact]
        public async Task SafetyInterceptor_GateOn_DirectDailyMutation_OutsideScope_ThrowsAuthoritativeWriteScopeException()
        {
            using var context = CreateContextWithInterceptor(
                authoritativeTrackingEnabled: true,
                isLocalFirst: false,
                isReadOnly: false);

            var daily = new Daily
            {
                Id = 2,
                Name = "Daily Direct Mutation Blocked",
                DailyDate = DateTime.UtcNow,
                SyncId = Guid.NewGuid(),
                IsActive = true
            };

            context.Set<Daily>().Add(daily);

            await Assert.ThrowsAsync<AuthoritativeWriteScopeException>(async () =>
            {
                await context.SaveChangesAsync();
            });
        }

        [Fact]
        public async Task SafetyInterceptor_GateOn_DirectDailyMutation_InsideScope_Succeeds()
        {
            using var context = CreateContextWithInterceptor(
                authoritativeTrackingEnabled: true,
                isLocalFirst: false,
                isReadOnly: false);

            var daily = new Daily
            {
                Id = 3,
                Name = "Daily Inside Scope Allowed",
                DailyDate = DateTime.UtcNow,
                SyncId = Guid.NewGuid(),
                IsActive = true
            };

            context.Set<Daily>().Add(daily);

            using (AuthoritativeWriteScopeContext.BeginScope())
            {
                var saved = await context.SaveChangesAsync();
                Assert.True(saved > 0);
            }
        }

        [Fact]
        public async Task SafetyInterceptor_GateOn_DirectNonDailyMutation_OutsideScope_Allowed()
        {
            using var context = CreateContextWithInterceptor(
                authoritativeTrackingEnabled: true,
                isLocalFirst: false,
                isReadOnly: false);

            var dept = new Department
            {
                Id = 10,
                Name = "Engineering Department"
            };

            context.Departments.Add(dept);

            // Direct SaveChanges on non-Daily entity must succeed even when AuthoritativeTrackingEnabled = true
            var saved = await context.SaveChangesAsync();
            Assert.True(saved > 0);
        }

        [Fact]
        public async Task SafetyInterceptor_OfflineMode_IgnoresAuthoritativeScope()
        {
            using var context = CreateContextWithInterceptor(
                authoritativeTrackingEnabled: true,
                isLocalFirst: true, // Offline mode
                isReadOnly: false);

            var daily = new Daily
            {
                Id = 4,
                Name = "Daily In Offline Mode",
                DailyDate = DateTime.UtcNow,
                SyncId = Guid.NewGuid(),
                IsActive = true
            };

            context.Set<Daily>().Add(daily);

            // AuthoritativeTrackingSafetyInterceptor only guards Online mode; in offline mode it does not throw
            var saved = await context.SaveChangesAsync();
            Assert.True(saved > 0);
        }

        #endregion

        #region 3. SyncId Validation & Immutability Tests in UnitOfWork

        [Fact]
        public async Task UnitOfWork_Online_SyncIdMutation_ThrowsAuthoritativeTrackingException()
        {
            var options = new DbContextOptionsBuilder<ApplicationContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .Options;

            using var context = new ApplicationContext(options);

            var originalSyncId = Guid.NewGuid();
            var daily = new Daily
            {
                Id = 101,
                Name = "Immutable SyncId Daily",
                DailyDate = DateTime.UtcNow,
                SyncId = originalSyncId,
                IsActive = true
            };

            context.Set<Daily>().Add(daily);
            await context.SaveChangesAsync();

            // Simulate updating SyncId
            daily.SyncId = Guid.NewGuid();

            var syncProviderMock = new Mock<ISyncConnectionProvider>();
            syncProviderMock.Setup(p => p.IsLocalFirstEnabled).Returns(false);
            syncProviderMock.Setup(p => p.IsReadOnlyMode).Returns(false);
            syncProviderMock.Setup(p => p.GetSelectedDatabaseId()).Returns("2026");

            var configDict = new Dictionary<string, string?>
            {
                { "Sync:AuthoritativeTrackingEnabled", "true" }
            };
            var config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

            var trackerMock = new Mock<IAuthoritativeDailyMutationTracker>();
            var guardMock = new Mock<IAuthoritativeDatabaseBindingGuard>();

            var uow = new UnitOfWork(context, syncProviderMock.Object, trackerMock.Object, guardMock.Object, config);

            var ex = await Assert.ThrowsAsync<AuthoritativeTrackingException>(async () =>
            {
                await uow.SaveChangesAsync();
            });

            Assert.Contains("SyncId is immutable", ex.Message);
        }

        [Fact]
        public async Task UnitOfWork_Online_EmptySyncIdOnModification_ThrowsAuthoritativeTrackingException()
        {
            var options = new DbContextOptionsBuilder<ApplicationContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .Options;

            using var context = new ApplicationContext(options);

            var daily = new Daily
            {
                Id = 102,
                Name = "Daily With SyncId",
                DailyDate = DateTime.UtcNow,
                SyncId = Guid.NewGuid(),
                IsActive = true
            };

            context.Set<Daily>().Add(daily);
            await context.SaveChangesAsync();

            // Set empty SyncId on modify
            daily.SyncId = Guid.Empty;
            daily.Name = "Updated Name";

            var syncProviderMock = new Mock<ISyncConnectionProvider>();
            syncProviderMock.Setup(p => p.IsLocalFirstEnabled).Returns(false);
            syncProviderMock.Setup(p => p.IsReadOnlyMode).Returns(false);
            syncProviderMock.Setup(p => p.GetSelectedDatabaseId()).Returns("2026");

            var configDict = new Dictionary<string, string?>
            {
                { "Sync:AuthoritativeTrackingEnabled", "true" }
            };
            var config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

            var trackerMock = new Mock<IAuthoritativeDailyMutationTracker>();
            var guardMock = new Mock<IAuthoritativeDatabaseBindingGuard>();

            var uow = new UnitOfWork(context, syncProviderMock.Object, trackerMock.Object, guardMock.Object, config);

            await Assert.ThrowsAsync<AuthoritativeTrackingException>(async () =>
            {
                await uow.SaveChangesAsync();
            });
        }

        #endregion

        #region 4. Tracker Unit Parameter Validations

        [Fact]
        public async Task Tracker_NullConnectionOrTransaction_ThrowsArgumentNullException()
        {
            var tracker = new AuthoritativeDailyMutationTracker(NullLogger<AuthoritativeDailyMutationTracker>.Instance);

            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                tracker.TrackDailyMutationsAsync(null!, null!, "2026", new List<CapturedAuthoritativeDailyMutation>(), CancellationToken.None));
        }

        [Theory]
        [InlineData("")]
        [InlineData(" ")]
        [InlineData(null)]
        public async Task Tracker_EmptyDatabaseId_ThrowsAuthoritativeTrackingException(string? dbId)
        {
            var tracker = new AuthoritativeDailyMutationTracker(NullLogger<AuthoritativeDailyMutationTracker>.Instance);
            var connMock = new Mock<System.Data.Common.DbConnection>();
            var transMock = new Mock<System.Data.Common.DbTransaction>();

            await Assert.ThrowsAsync<AuthoritativeTrackingException>(() =>
                tracker.TrackDailyMutationsAsync(connMock.Object, transMock.Object, dbId!, new List<CapturedAuthoritativeDailyMutation>(), CancellationToken.None));
        }

        [Fact]
        public async Task Tracker_EmptySyncIdInMutations_ThrowsAuthoritativeTrackingException()
        {
            var tracker = new AuthoritativeDailyMutationTracker(NullLogger<AuthoritativeDailyMutationTracker>.Instance);
            var connMock = new Mock<System.Data.Common.DbConnection>();
            var transMock = new Mock<System.Data.Common.DbTransaction>();

            var mutations = new List<CapturedAuthoritativeDailyMutation>
            {
                new CapturedAuthoritativeDailyMutation
                {
                    Daily = new Daily { Name = "Bad Daily" },
                    OperationType = "INSERT",
                    EntitySyncId = Guid.Empty
                }
            };

            await Assert.ThrowsAsync<AuthoritativeTrackingException>(() =>
                tracker.TrackDailyMutationsAsync(connMock.Object, transMock.Object, "2026", mutations, CancellationToken.None));
        }

        [Fact]
        public void Tracker_ServerOriginDeviceId_IsGuidEmpty()
        {
            Assert.Equal(Guid.Empty, AuthoritativeDailyMutationTracker.ServerOriginDeviceId);
        }

        #endregion

        #region 5. AuthoritativeWriteScopeContext Hardening Tests (P1)

        [Fact]
        public void NestedAuthoritativeScope_DepthCounterAndIdempotentDispose_Safe()
        {
            Assert.False(AuthoritativeWriteScopeContext.IsActive);

            var outer = AuthoritativeWriteScopeContext.BeginScope();
            Assert.True(AuthoritativeWriteScopeContext.IsActive);

            var inner = AuthoritativeWriteScopeContext.BeginScope();
            Assert.True(AuthoritativeWriteScopeContext.IsActive);

            // Disposing inner scope must leave outer scope active
            inner.Dispose();
            Assert.True(AuthoritativeWriteScopeContext.IsActive);

            // Disposing outer scope makes it inactive
            outer.Dispose();
            Assert.False(AuthoritativeWriteScopeContext.IsActive);

            // Idempotent dispose must not drive depth below 0
            outer.Dispose();
            inner.Dispose();
            Assert.False(AuthoritativeWriteScopeContext.IsActive);

            // New scope immediately becomes active (not blocked by previous double-dispose)
            using (AuthoritativeWriteScopeContext.BeginScope())
            {
                Assert.True(AuthoritativeWriteScopeContext.IsActive);
            }
            Assert.False(AuthoritativeWriteScopeContext.IsActive);
        }

        [Fact]
        public async Task AuthoritativeScope_AsyncFlowIsolation_MaintainsScopeCorrectly()
        {
            Assert.False(AuthoritativeWriteScopeContext.IsActive);

            using (AuthoritativeWriteScopeContext.BeginScope())
            {
                Assert.True(AuthoritativeWriteScopeContext.IsActive);

                await Task.Run(() =>
                {
                    // Async flow inherits ambient scope
                    Assert.True(AuthoritativeWriteScopeContext.IsActive);
                });

                Assert.True(AuthoritativeWriteScopeContext.IsActive);
            }

            Assert.False(AuthoritativeWriteScopeContext.IsActive);
        }

        #endregion

        #region 6. Tracking Configuration Fail-Closed Tests (P0)

        [Fact]
        public async Task UnitOfWork_Online_MissingSyncConnectionProvider_ThrowsAuthoritativeTrackingConfigurationException()
        {
            var options = new DbContextOptionsBuilder<ApplicationContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .Options;
            using var context = new ApplicationContext(options);

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { { "Sync:AuthoritativeTrackingEnabled", "true" } })
                .Build();

            var mockDbProvider = new Mock<IDbConnectionProvider>(); // Not ISyncConnectionProvider!
            var trackerMock = new Mock<IAuthoritativeDailyMutationTracker>();
            var guardMock = new Mock<IAuthoritativeDatabaseBindingGuard>();

            var uow = new UnitOfWork(context, mockDbProvider.Object, trackerMock.Object, guardMock.Object, config);

            context.Set<Daily>().Add(new Daily { Name = "Test Daily", DailyDate = DateTime.UtcNow, SyncId = Guid.NewGuid(), IsActive = true });

            var ex = await Assert.ThrowsAsync<AuthoritativeTrackingConfigurationException>(() => uow.SaveChangesAsync());
            Assert.Equal("AUTHORITATIVE_TRACKING_CONFIGURATION_INVALID", ex.ErrorCode);
            Assert.Contains("ISyncConnectionProvider dependency is missing", ex.Message);
        }

        [Fact]
        public async Task UnitOfWork_Online_MissingTrackerDependency_ThrowsAuthoritativeTrackingConfigurationException()
        {
            var options = new DbContextOptionsBuilder<ApplicationContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .Options;
            using var context = new ApplicationContext(options);

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { { "Sync:AuthoritativeTrackingEnabled", "true" } })
                .Build();

            var syncProviderMock = new Mock<ISyncConnectionProvider>();
            syncProviderMock.Setup(p => p.IsLocalFirstEnabled).Returns(false);
            syncProviderMock.Setup(p => p.IsReadOnlyMode).Returns(false);
            syncProviderMock.Setup(p => p.GetSelectedDatabaseId()).Returns("2026");

            var guardMock = new Mock<IAuthoritativeDatabaseBindingGuard>();

            var uow = new UnitOfWork(context, syncProviderMock.Object, authoritativeTracker: null, guardMock.Object, config);

            context.Set<Daily>().Add(new Daily { Name = "Test Daily", DailyDate = DateTime.UtcNow, SyncId = Guid.NewGuid(), IsActive = true });

            var ex = await Assert.ThrowsAsync<AuthoritativeTrackingConfigurationException>(() => uow.SaveChangesAsync());
            Assert.Equal("AUTHORITATIVE_TRACKING_CONFIGURATION_INVALID", ex.ErrorCode);
            Assert.Contains("IAuthoritativeDailyMutationTracker dependency is missing", ex.Message);
        }

        [Fact]
        public async Task UnitOfWork_Online_MissingBindingGuardDependency_ThrowsAuthoritativeTrackingConfigurationException()
        {
            var options = new DbContextOptionsBuilder<ApplicationContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .Options;
            using var context = new ApplicationContext(options);

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { { "Sync:AuthoritativeTrackingEnabled", "true" } })
                .Build();

            var syncProviderMock = new Mock<ISyncConnectionProvider>();
            syncProviderMock.Setup(p => p.IsLocalFirstEnabled).Returns(false);
            syncProviderMock.Setup(p => p.IsReadOnlyMode).Returns(false);
            syncProviderMock.Setup(p => p.GetSelectedDatabaseId()).Returns("2026");

            var trackerMock = new Mock<IAuthoritativeDailyMutationTracker>();

            var uow = new UnitOfWork(context, syncProviderMock.Object, trackerMock.Object, bindingGuard: null, config);

            context.Set<Daily>().Add(new Daily { Name = "Test Daily", DailyDate = DateTime.UtcNow, SyncId = Guid.NewGuid(), IsActive = true });

            var ex = await Assert.ThrowsAsync<AuthoritativeTrackingConfigurationException>(() => uow.SaveChangesAsync());
            Assert.Equal("AUTHORITATIVE_TRACKING_CONFIGURATION_INVALID", ex.ErrorCode);
            Assert.Contains("IAuthoritativeDatabaseBindingGuard dependency is missing", ex.Message);
        }

        #endregion

        #region 7. Pre-Open Physical Azure Binding Guard Tests (P0)

        private class CountingDbConnection : System.Data.Common.DbConnection
        {
            private string _connectionString;
            public int OpenCount { get; private set; }

            public CountingDbConnection(string connectionString)
            {
                _connectionString = connectionString;
            }

            [System.Diagnostics.CodeAnalysis.AllowNull]
            public override string ConnectionString
            {
                get => _connectionString;
                set => _connectionString = value ?? string.Empty;
            }

            public override string Database => "IProgramDb2026";
            public override string DataSource => "localhost";
            public override string ServerVersion => "15.0";
            public override System.Data.ConnectionState State => System.Data.ConnectionState.Closed;

            public override void Open()
            {
                OpenCount++;
            }

            public override Task OpenAsync(CancellationToken cancellationToken)
            {
                OpenCount++;
                return Task.CompletedTask;
            }

            public override void Close() { }
            public override void ChangeDatabase(string databaseName) { }
            protected override System.Data.Common.DbTransaction BeginDbTransaction(System.Data.IsolationLevel isolationLevel) => throw new NotImplementedException();
            protected override System.Data.Common.DbCommand CreateDbCommand() => throw new NotImplementedException();
        }

        [Fact]
        public async Task PreOpenBindingGuard_ZeroConnectionsOpenedOnMismatch()
        {
            // Endpoint points to local/unauthorized server
            var countingConn = new CountingDbConnection("Server=localhost;Database=IProgramLocalDb2026;Integrated Security=True;TrustServerCertificate=True;");

            var options = new DbContextOptionsBuilder<ApplicationContext>()
                .UseSqlServer(countingConn)
                .Options;

            using var context = new ApplicationContext(options);

            var syncProviderMock = new Mock<ISyncConnectionProvider>();
            syncProviderMock.Setup(p => p.IsLocalFirstEnabled).Returns(false);
            syncProviderMock.Setup(p => p.IsReadOnlyMode).Returns(false);
            syncProviderMock.Setup(p => p.GetSelectedDatabaseId()).Returns("2026");

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { { "Sync:AuthoritativeTrackingEnabled", "true" } })
                .Build();

            var trackerMock = new Mock<IAuthoritativeDailyMutationTracker>();
            var productionGuard = new AuthoritativeDatabaseBindingGuard();

            var uow = new UnitOfWork(context, syncProviderMock.Object, trackerMock.Object, productionGuard, config);

            // Add a Daily mutation to trigger authoritative coordination
            context.Set<Daily>().Add(new Daily
            {
                Name = "Daily Attempting Invalid Server",
                DailyDate = DateTime.UtcNow,
                SyncId = Guid.NewGuid(),
                IsActive = true
            });

            // Must throw AuthoritativeBindingException BEFORE opening connection
            var ex = await Assert.ThrowsAsync<AuthoritativeBindingException>(() => uow.SaveChangesAsync());
            Assert.Equal("AUTHORITATIVE_BINDING_MISMATCH", ex.ErrorCode);

            // Open count MUST be exactly 0 (network boundary protected before connection open)
            Assert.Equal(0, countingConn.OpenCount);
            Assert.Equal(System.Data.ConnectionState.Closed, countingConn.State);
        }

        #endregion

        #region 8. Runtime Raw-DML Audit Test (P1)

        [Fact]
        public void RawDmlAudit_NoDirectDailyDmlOutsideApprovedPushCoordinator()
        {
            // Locate repository root from test execution directory
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var dir = new System.IO.DirectoryInfo(baseDir);
            while (dir != null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName, "src")))
            {
                dir = dir.Parent;
            }

            Assert.NotNull(dir);
            var srcDir = System.IO.Path.Combine(dir.FullName, "src");

            // Scan all .cs files in src
            var csFiles = System.IO.Directory.GetFiles(srcDir, "*.cs", System.IO.SearchOption.AllDirectories);
            Assert.NotEmpty(csFiles);

            // Regex pattern matching direct DML statements on [dbo].[Daily] or Daily
            var dmlPattern = new System.Text.RegularExpressions.Regex(
                @"\b(INSERT\s+INTO|UPDATE|DELETE(\s+FROM)?)\s+(\[?dbo\]?\.)?\[?Daily\]?\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

            var violations = new List<string>();

            foreach (var file in csFiles)
            {
                var fileName = System.IO.Path.GetFileName(file);

                // AzurePushTransactionCoordinator is the approved coordinator for Slice 4.3C
                if (string.Equals(fileName, "AzurePushTransactionCoordinator.cs", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var lines = System.IO.File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];

                    // Ignore single-line comments
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("//") || trimmed.StartsWith("///") || trimmed.StartsWith("*"))
                    {
                        continue;
                    }

                    if (dmlPattern.IsMatch(line))
                    {
                        violations.Add($"{System.IO.Path.GetRelativePath(dir.FullName, file)}:L{i + 1}: {trimmed}");
                    }
                }
            }

            Assert.True(violations.Count == 0,
                $"Detected unauthorized direct Daily DML in runtime application code outside AzurePushTransactionCoordinator:\n" +
                string.Join("\n", violations));
        }

        #endregion
    }
}
