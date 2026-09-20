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
using Moq.Protected;
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

#nullable disable
        private class FakeDbTransaction : System.Data.Common.DbTransaction
        {
            protected override System.Data.Common.DbConnection DbConnection => null;
            public override System.Data.IsolationLevel IsolationLevel => System.Data.IsolationLevel.ReadCommitted;
            public override void Commit() { }
            public override void Rollback() { }
            public override Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public override Task RollbackAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        }

        private class FakeDbParameterCollection : System.Data.Common.DbParameterCollection
        {
            private readonly List<System.Data.Common.DbParameter> _parameters = new();
            public override int Count => _parameters.Count;
            public override object SyncRoot => this;
            public override int Add(object value) { _parameters.Add((System.Data.Common.DbParameter)value); return _parameters.Count - 1; }
            public override void AddRange(Array values) { foreach (var v in values) Add(v); }
            public override void Clear() => _parameters.Clear();
            public override bool Contains(object value) => _parameters.Contains((System.Data.Common.DbParameter)value);
            public override bool Contains(string value) => _parameters.Any(p => p.ParameterName == value);
            public override void CopyTo(Array array, int index) => ((System.Collections.ICollection)_parameters).CopyTo(array, index);
            public override System.Collections.IEnumerator GetEnumerator() => _parameters.GetEnumerator();
            public override int IndexOf(object value) => _parameters.IndexOf((System.Data.Common.DbParameter)value);
            public override int IndexOf(string parameterName) => _parameters.FindIndex(p => p.ParameterName == parameterName);
            public override void Insert(int index, object value) => _parameters.Insert(index, (System.Data.Common.DbParameter)value);
            public override void Remove(object value) => _parameters.Remove((System.Data.Common.DbParameter)value);
            public override void RemoveAt(int index) => _parameters.RemoveAt(index);
            public override void RemoveAt(string parameterName) { int idx = IndexOf(parameterName); if (idx >= 0) _parameters.RemoveAt(idx); }
            protected override System.Data.Common.DbParameter GetParameter(int index) => _parameters[index];
            protected override System.Data.Common.DbParameter GetParameter(string parameterName) => _parameters.First(p => p.ParameterName == parameterName);
            protected override void SetParameter(int index, System.Data.Common.DbParameter value) => _parameters[index] = value;
            protected override void SetParameter(string parameterName, System.Data.Common.DbParameter value) { int idx = IndexOf(parameterName); if (idx >= 0) _parameters[idx] = value; else _parameters.Add(value); }
        }

        private class FakeDbParameter : System.Data.Common.DbParameter
        {
            public override System.Data.DbType DbType { get; set; }
            public override System.Data.ParameterDirection Direction { get; set; }
            public override bool IsNullable { get; set; }
            public override string ParameterName { get; set; } = string.Empty;
            public override string SourceColumn { get; set; } = string.Empty;
            public override object Value { get; set; }
            public override bool SourceColumnNullMapping { get; set; }
            public override int Size { get; set; }
            public override void ResetDbType() { }
        }

        private class FakeDbCommand : System.Data.Common.DbCommand
        {
            private readonly FakeDbParameterCollection _parameters = new();
            public override string CommandText { get; set; } = string.Empty;
            public override int CommandTimeout { get; set; }
            public override System.Data.CommandType CommandType { get; set; }
            public override bool DesignTimeVisible { get; set; }
            public override System.Data.UpdateRowSource UpdatedRowSource { get; set; }
            protected override System.Data.Common.DbConnection DbConnection { get; set; }
            protected override System.Data.Common.DbParameterCollection DbParameterCollection => _parameters;
            protected override System.Data.Common.DbTransaction DbTransaction { get; set; }
            public override void Cancel() { }
            public override int ExecuteNonQuery() => 1;
            public override object ExecuteScalar() => 1L;
            public override Task<object> ExecuteScalarAsync(CancellationToken cancellationToken) => Task.FromResult<object>(1L);
            public override void Prepare() { }
            protected override System.Data.Common.DbParameter CreateDbParameter() => new FakeDbParameter();
            protected override System.Data.Common.DbDataReader ExecuteDbDataReader(System.Data.CommandBehavior behavior) => throw new NotImplementedException();
        }
#nullable restore

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
            protected override System.Data.Common.DbTransaction BeginDbTransaction(System.Data.IsolationLevel isolationLevel) => new FakeDbTransaction();
            protected override System.Data.Common.DbCommand CreateDbCommand() => new FakeDbCommand();
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

        #region 9. Authoritative Optimistic Concurrency Guard Tests (P0)

#nullable disable
        private class FakeDbDataReader : System.Data.Common.DbDataReader
        {
            private readonly List<object[]> _rows;
            private int _currentIndex = -1;

            public FakeDbDataReader(List<object[]> rows)
            {
                _rows = rows;
            }

            public override int Depth => 0;
            public override bool NextResult() => false;
            public override Task<bool> NextResultAsync(CancellationToken cancellationToken) => Task.FromResult(false);

            public override int FieldCount => _rows.Count > 0 ? _rows[0].Length : 0;
            public override bool HasRows => _rows.Count > 0;
            public override bool IsClosed => false;
            public override int RecordsAffected => _rows.Count;

            public override bool Read()
            {
                _currentIndex++;
                return _currentIndex < _rows.Count;
            }

            public override Task<bool> ReadAsync(CancellationToken cancellationToken)
            {
                return Task.FromResult(Read());
            }

            public override object this[int ordinal] => _rows[_currentIndex][ordinal] ?? DBNull.Value;
            public override object this[string name] => throw new NotImplementedException();
            public override bool GetBoolean(int ordinal) => Convert.ToBoolean(_rows[_currentIndex][ordinal]);
            public override byte GetByte(int ordinal) => Convert.ToByte(_rows[_currentIndex][ordinal]);
            public override long GetBytes(int ordinal, long dataOffset, byte[] buffer, int bufferOffset, int length) => throw new NotImplementedException();
            public override char GetChar(int ordinal) => Convert.ToChar(_rows[_currentIndex][ordinal]);
            public override long GetChars(int ordinal, long dataOffset, char[] buffer, int bufferOffset, int length) => throw new NotImplementedException();
            public override string GetDataTypeName(int ordinal) => "text";
            public override DateTime GetDateTime(int ordinal) => Convert.ToDateTime(_rows[_currentIndex][ordinal]);
            public override decimal GetDecimal(int ordinal) => Convert.ToDecimal(_rows[_currentIndex][ordinal]);
            public override double GetDouble(int ordinal) => Convert.ToDouble(_rows[_currentIndex][ordinal]);
            public override Type GetFieldType(int ordinal) => typeof(object);
            public override float GetFloat(int ordinal) => Convert.ToSingle(_rows[_currentIndex][ordinal]);
            public override Guid GetGuid(int ordinal) => (Guid)_rows[_currentIndex][ordinal];
            public override short GetInt16(int ordinal) => Convert.ToInt16(_rows[_currentIndex][ordinal]);
            public override int GetInt32(int ordinal) => Convert.ToInt32(_rows[_currentIndex][ordinal]);
            public override long GetInt64(int ordinal) => Convert.ToInt64(_rows[_currentIndex][ordinal]);
            public override string GetName(int ordinal) => ordinal.ToString();
            public override int GetOrdinal(string name) => 0;
            public override string GetString(int ordinal) => _rows[_currentIndex][ordinal]?.ToString() ?? string.Empty;
            public override object GetValue(int ordinal) => _rows[_currentIndex][ordinal] ?? DBNull.Value;
            public override int GetValues(object[] values) => throw new NotImplementedException();
            public override bool IsDBNull(int ordinal) => _rows[_currentIndex][ordinal] == null || _rows[_currentIndex][ordinal] == DBNull.Value;
            public override System.Collections.IEnumerator GetEnumerator() => throw new NotImplementedException();
        }

        private class ConfigurableFakeDbCommand : FakeDbCommand
        {
            public Func<string, object> ScalarHandler { get; set; }
            public Func<string, System.Data.Common.DbDataReader> ReaderHandler { get; set; }

            public override Task<object> ExecuteScalarAsync(CancellationToken cancellationToken)
            {
                if (ScalarHandler != null)
                {
                    return Task.FromResult(ScalarHandler(CommandText));
                }
                return base.ExecuteScalarAsync(cancellationToken);
            }

            protected override System.Data.Common.DbDataReader ExecuteDbDataReader(System.Data.CommandBehavior behavior)
            {
                if (ReaderHandler != null)
                {
                    return ReaderHandler(CommandText);
                }
                return new FakeDbDataReader(new List<object[]>());
            }

            protected override Task<System.Data.Common.DbDataReader> ExecuteDbDataReaderAsync(System.Data.CommandBehavior behavior, CancellationToken cancellationToken)
            {
                return Task.FromResult(ExecuteDbDataReader(behavior));
            }
        }
#nullable restore

        private class ConfigurableCountingDbConnection : CountingDbConnection
        {
            public Func<string, object?>? ScalarHandler { get; set; }
            public Func<string, System.Data.Common.DbDataReader>? ReaderHandler { get; set; }

            public ConfigurableCountingDbConnection() : base("Server=localhost;Database=IProgramTest;Integrated Security=True;") { }

            protected override System.Data.Common.DbCommand CreateDbCommand()
            {
                return new ConfigurableFakeDbCommand
                {
                    ScalarHandler = this.ScalarHandler!,
                    ReaderHandler = this.ReaderHandler!
                };
            }
        }

        [Fact]
        public void AuthoritativeConcurrencyConflictException_HasExpectedErrorCode()
        {
            var ex = new AuthoritativeConcurrencyConflictException("Test conflict");
            Assert.Equal("AUTHORITATIVE_CONCURRENCY_CONFLICT", ex.ErrorCode);
        }

        [Fact]
        public async Task Tracker_PrepareBatch_MissingOriginalSnapshotOnUpdate_ThrowsAuthoritativeTrackingException()
        {
            var tracker = new AuthoritativeDailyMutationTracker(NullLogger<AuthoritativeDailyMutationTracker>.Instance);
            var conn = new ConfigurableCountingDbConnection
            {
                ScalarHandler = sql => 1L // Return server version 1
            };
            var trans = new FakeDbTransaction();

            var mutations = new List<CapturedAuthoritativeDailyMutation>
            {
                new CapturedAuthoritativeDailyMutation
                {
                    Daily = new Daily { Name = "Updated Name" },
                    OperationType = "UPDATE",
                    EntitySyncId = Guid.NewGuid(),
                    OriginalSnapshot = null // Missing snapshot!
                }
            };

            var ex = await Assert.ThrowsAsync<AuthoritativeTrackingException>(() =>
                tracker.PrepareAuthoritativeBatchAsync(conn, trans, "2026", mutations, CancellationToken.None));

            Assert.Contains("OriginalSnapshot is required", ex.Message);
        }

        [Fact]
        public async Task Tracker_PrepareBatch_MissingRowOnUpdate_ThrowsAuthoritativeConcurrencyConflictException()
        {
            var tracker = new AuthoritativeDailyMutationTracker(NullLogger<AuthoritativeDailyMutationTracker>.Instance);
            var conn = new ConfigurableCountingDbConnection
            {
                ScalarHandler = sql => 1L,
                ReaderHandler = sql => new FakeDbDataReader(new List<object[]>()) // Empty reader: row not found!
            };
            var trans = new FakeDbTransaction();

            var mutations = new List<CapturedAuthoritativeDailyMutation>
            {
                new CapturedAuthoritativeDailyMutation
                {
                    Daily = new Daily { Name = "Updated Name" },
                    OperationType = "UPDATE",
                    EntitySyncId = Guid.NewGuid(),
                    OriginalSnapshot = new AuthoritativeDailyOriginalSnapshot
                    {
                        SyncId = Guid.NewGuid(),
                        Name = "Original Name",
                        DailyDate = DateTime.UtcNow,
                        Closed = false,
                        CreatedAt = DateTime.UtcNow,
                        CreatedBy = "Admin",
                        IsActive = true
                    }
                }
            };

            var ex = await Assert.ThrowsAsync<AuthoritativeConcurrencyConflictException>(() =>
                tracker.PrepareAuthoritativeBatchAsync(conn, trans, "2026", mutations, CancellationToken.None));

            Assert.Equal("AUTHORITATIVE_CONCURRENCY_CONFLICT", ex.ErrorCode);
            Assert.Contains("no longer exists", ex.Message);
        }

        [Fact]
        public async Task Tracker_PrepareBatch_ScalarFieldMismatch_ThrowsAuthoritativeConcurrencyConflictException()
        {
            var tracker = new AuthoritativeDailyMutationTracker(NullLogger<AuthoritativeDailyMutationTracker>.Instance);
            var syncId = Guid.NewGuid();
            var now = DateTime.UtcNow;

            var conn = new ConfigurableCountingDbConnection
            {
                ScalarHandler = sql => 1L,
                ReaderHandler = sql => new FakeDbDataReader(new List<object?[]>
                {
                    new object?[]
                    {
                        "Modified By Push Concurrent", // 0: Name (mismatched!)
                        now,                           // 1: DailyDate
                        false,                         // 2: Closed
                        now,                           // 3: CreatedAt
                        "Admin",                       // 4: CreatedBy
                        null,                          // 5: UpdatedAt
                        null,                          // 6: UpdatedBy
                        null,                          // 7: DeactivatedAt
                        null,                          // 8: DeactivatedBy
                        true                           // 9: IsActive
                    }
                })
            };
            var trans = new FakeDbTransaction();

            var mutations = new List<CapturedAuthoritativeDailyMutation>
            {
                new CapturedAuthoritativeDailyMutation
                {
                    Daily = new Daily { Name = "Online New Name", SyncId = syncId },
                    OperationType = "UPDATE",
                    EntitySyncId = syncId,
                    OriginalSnapshot = new AuthoritativeDailyOriginalSnapshot
                    {
                        SyncId = syncId,
                        Name = "Original Name Before Push",
                        DailyDate = now,
                        Closed = false,
                        CreatedAt = now,
                        CreatedBy = "Admin",
                        IsActive = true
                    }
                }
            };

            var ex = await Assert.ThrowsAsync<AuthoritativeConcurrencyConflictException>(() =>
                tracker.PrepareAuthoritativeBatchAsync(conn, trans, "2026", mutations, CancellationToken.None));

            Assert.Equal("AUTHORITATIVE_CONCURRENCY_CONFLICT", ex.ErrorCode);
            Assert.Contains("Database current values do not match original snapshot", ex.Message);
            Assert.Contains("Field 'Name' differed", ex.Message);
        }

        [Fact]
        public void UnitOfWork_CapturesOriginalSnapshot_DirectlyFromEntityEntry()
        {
            var options = new DbContextOptionsBuilder<ApplicationContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .Options;

            var syncId = Guid.NewGuid();
            var originalDate = new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc);
            var createdAt = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);

            using var context = new ApplicationContext(options);
            var daily = new Daily
            {
                Id = 501,
                Name = "Original Name Before Modification",
                DailyDate = originalDate,
                Closed = false,
                CreatedAt = createdAt,
                CreatedBy = "Seeder",
                IsActive = true,
                SyncId = syncId
            };
            context.Set<Daily>().Add(daily);
            context.SaveChanges();

            // Mutate the entity in the tracking graph
            daily.Name = "Modified Name";
            daily.Closed = true;

            var entry = context.Entry(daily);
            Assert.Equal(EntityState.Modified, entry.State);

            var snapshot = UnitOfWork.CaptureDailyOriginalSnapshot(entry);

            // Snapshot MUST reflect the original values, NOT the modified values
            Assert.Equal(syncId, snapshot.SyncId);
            Assert.Equal("Original Name Before Modification", snapshot.Name);
            Assert.Equal(originalDate, snapshot.DailyDate);
            Assert.False(snapshot.Closed);
            Assert.Equal(createdAt, snapshot.CreatedAt);
            Assert.Equal("Seeder", snapshot.CreatedBy);
            Assert.True(snapshot.IsActive);
        }

        #endregion
    }
}
