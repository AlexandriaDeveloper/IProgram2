#nullable enable
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Api.Controllers;
using Auth.Infrastructure.Sync;
using Auth.Infrastructure.Sync.Push;
using Core.Interfaces;
using Core.Models.Sync;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using Xunit;

namespace Auth.UnitTests
{
    public class SyncStatusUnitTests
    {
        private class TestDbParameter : DbParameter
        {
            public override DbType DbType { get; set; }
            public override ParameterDirection Direction { get; set; }
            public override bool IsNullable { get; set; }
            [System.Diagnostics.CodeAnalysis.AllowNull]
            public override string ParameterName { get; set; } = string.Empty;
            [System.Diagnostics.CodeAnalysis.AllowNull]
            public override string SourceColumn { get; set; } = string.Empty;
            public override object? Value { get; set; }
            public override bool SourceColumnNullMapping { get; set; }
            public override int Size { get; set; }
            public override void ResetDbType() { }
        }

        private class TestDbParameterCollection : DbParameterCollection
        {
            private readonly List<object?> _parameters = new();
            public override int Count => _parameters.Count;
            public override object SyncRoot => _parameters;
            public override int Add(object? value) { _parameters.Add(value); return _parameters.Count - 1; }
            public override void AddRange(Array values) { foreach (var v in values) Add(v); }
            public override void Clear() => _parameters.Clear();
            public override bool Contains(object? value) => _parameters.Contains(value);
            public override bool Contains(string value) => false;
            public override void CopyTo(Array array, int index) => ((System.Collections.ICollection)_parameters).CopyTo(array, index);
            public override System.Collections.IEnumerator GetEnumerator() => _parameters.GetEnumerator();
            protected override DbParameter GetParameter(int index) => (DbParameter)_parameters[index]!;
            protected override DbParameter GetParameter(string parameterName) => (DbParameter)_parameters[0]!;
            public override int IndexOf(object? value) => _parameters.IndexOf(value);
            public override int IndexOf(string parameterName) => 0;
            public override void Insert(int index, object? value) => _parameters.Insert(index, value);
            public override void Remove(object? value) => _parameters.Remove(value);
            public override void RemoveAt(int index) => _parameters.RemoveAt(index);
            public override void RemoveAt(string parameterName) { }
            protected override void SetParameter(int index, DbParameter value) => _parameters[index] = value;
            protected override void SetParameter(string parameterName, DbParameter value) { }
        }

        private class TestDbCommand : DbCommand
        {
            public object? ScalarResult { get; set; }
            [System.Diagnostics.CodeAnalysis.AllowNull]
            public override string CommandText { get; set; } = string.Empty;
            public override int CommandTimeout { get; set; }
            public override CommandType CommandType { get; set; }
            public override bool DesignTimeVisible { get; set; }
            public override UpdateRowSource UpdatedRowSource { get; set; }
            protected override DbConnection? DbConnection { get; set; }
            protected override DbParameterCollection DbParameterCollection { get; } = new TestDbParameterCollection();
            protected override DbTransaction? DbTransaction { get; set; }

            public override void Cancel() { }
            public override int ExecuteNonQuery() => 0;
            public override object? ExecuteScalar() => ScalarResult;
            public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken) => Task.FromResult(ScalarResult);
            public override void Prepare() { }
            protected override DbParameter CreateDbParameter() => new TestDbParameter();
            protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw new NotImplementedException();
        }

        private class TestDbConnection : DbConnection
        {
            public TestDbCommand Command { get; } = new TestDbCommand();
            [System.Diagnostics.CodeAnalysis.AllowNull]
            public override string ConnectionString { get; set; } = string.Empty;
            public override string Database => "TestDb";
            public override string DataSource => "TestServer";
            public override string ServerVersion => "1.0";
            public override ConnectionState State => ConnectionState.Open;
            public override void ChangeDatabase(string databaseName) { }
            public override void Close() { }
            public override void Open() { }
            public override Task OpenAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotImplementedException();
            protected override DbCommand CreateDbCommand() => Command;
        }

        private class TestableSyncStatusService : SyncStatusService
        {
            public LocalSyncStatusDto MockLocalStatus { get; set; } = new LocalSyncStatusDto();
            public int MockPendingDaily { get; set; } = 0;
            public int MockPendingForms { get; set; } = 0;

            public TestableSyncStatusService(
                ISyncConnectionProvider syncConnectionProvider,
                IRemoteDatabaseConnectionFactory remoteConnectionFactory,
                ILocalScopeBaselineService scopeBaselineService)
                : base(
                    syncConnectionProvider,
                    remoteConnectionFactory,
                    scopeBaselineService,
                    NullLogger<SyncStatusService>.Instance)
            {
            }

            public override Task<LocalSyncStatusDto> GetLocalStatusAsync(string databaseId, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(MockLocalStatus);
            }

            protected override DbConnection CreateLocalConnection(string connectionString)
            {
                return new TestDbConnection();
            }

            protected override Task<(int PendingDaily, int PendingForms)> GetPendingCountsByScopeAsync(
                DbConnection connection,
                string databaseId,
                CancellationToken cancellationToken)
            {
                return Task.FromResult((MockPendingDaily, MockPendingForms));
            }
        }

        private static IConfiguration CreateConfig()
        {
            return new ConfigurationBuilder().Build();
        }

        #region 1. Validation and Constructor Tests

        [Theory]
        [InlineData("")]
        [InlineData(" ")]
        [InlineData("2025")]
        [InlineData("2028")]
        [InlineData("master")]
        public async Task GetLocalStatusAsync_InvalidDatabaseId_ThrowsArgumentException(string invalidDbId)
        {
            var syncProviderMock = new Mock<ISyncConnectionProvider>();
            var remoteFactoryMock = new Mock<IRemoteDatabaseConnectionFactory>();
            var baselineServiceMock = new Mock<ILocalScopeBaselineService>();

            var service = new SyncStatusService(
                syncProviderMock.Object,
                remoteFactoryMock.Object,
                baselineServiceMock.Object,
                NullLogger<SyncStatusService>.Instance);

            await Assert.ThrowsAsync<ArgumentException>(() => service.GetLocalStatusAsync(invalidDbId, CancellationToken.None));
        }

        [Theory]
        [InlineData("")]
        [InlineData(" ")]
        [InlineData("2025")]
        [InlineData("2028")]
        [InlineData("master")]
        public async Task CheckOnlineStatusAsync_InvalidDatabaseId_ThrowsArgumentException(string invalidDbId)
        {
            var syncProviderMock = new Mock<ISyncConnectionProvider>();
            var remoteFactoryMock = new Mock<IRemoteDatabaseConnectionFactory>();
            var baselineServiceMock = new Mock<ILocalScopeBaselineService>();

            var service = new SyncStatusService(
                syncProviderMock.Object,
                remoteFactoryMock.Object,
                baselineServiceMock.Object,
                NullLogger<SyncStatusService>.Instance);

            await Assert.ThrowsAsync<ArgumentException>(() => service.CheckOnlineStatusAsync(invalidDbId, CancellationToken.None));
        }

        [Fact]
        public void Constructor_NullArguments_ThrowsArgumentNullException()
        {
            var syncProvider = new Mock<ISyncConnectionProvider>().Object;
            var remoteFactory = new Mock<IRemoteDatabaseConnectionFactory>().Object;
            var baselineService = new Mock<ILocalScopeBaselineService>().Object;
            var logger = NullLogger<SyncStatusService>.Instance;

            Assert.Throws<ArgumentNullException>(() => new SyncStatusService(null!, remoteFactory, baselineService, logger));
            Assert.Throws<ArgumentNullException>(() => new SyncStatusService(syncProvider, null!, baselineService, logger));
            Assert.Throws<ArgumentNullException>(() => new SyncStatusService(syncProvider, remoteFactory, null!, logger));
            Assert.Throws<ArgumentNullException>(() => new SyncStatusService(syncProvider, remoteFactory, baselineService, null!));
        }

        #endregion

        #region 2. Controller Status Endpoints Tests

        [Fact]
        public async Task SyncController_GetLocalStatus_WhenServiceNull_Returns503()
        {
            var controller = new SyncController(
                new Mock<ILocalOutboxPushService>().Object,
                new Mock<ILocalDailyPullService>().Object,
                new Mock<ISyncConnectionProvider>().Object,
                CreateConfig(),
                NullLogger<SyncController>.Instance,
                new Mock<ILocalScopeBaselineService>().Object,
                syncStatusService: null);

            var result = await controller.GetLocalStatus(CancellationToken.None);
            var objResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(503, objResult.StatusCode);
        }

        [Fact]
        public async Task SyncController_CheckOnlineStatus_WhenServiceNull_Returns503()
        {
            var controller = new SyncController(
                new Mock<ILocalOutboxPushService>().Object,
                new Mock<ILocalDailyPullService>().Object,
                new Mock<ISyncConnectionProvider>().Object,
                CreateConfig(),
                NullLogger<SyncController>.Instance,
                new Mock<ILocalScopeBaselineService>().Object,
                syncStatusService: null);

            var result = await controller.CheckOnlineStatus(CancellationToken.None);
            var objResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(503, objResult.StatusCode);
        }

        [Theory]
        [InlineData("")]
        [InlineData(" ")]
        [InlineData("invalid")]
        [InlineData("2025")]
        public async Task SyncController_GetLocalStatus_InvalidDatabase_Returns400(string invalidDb)
        {
            var syncProviderMock = new Mock<ISyncConnectionProvider>();
            syncProviderMock.Setup(p => p.GetSelectedDatabaseId()).Returns(invalidDb);

            var statusServiceMock = new Mock<ISyncStatusService>();

            var controller = new SyncController(
                new Mock<ILocalOutboxPushService>().Object,
                new Mock<ILocalDailyPullService>().Object,
                syncProviderMock.Object,
                CreateConfig(),
                NullLogger<SyncController>.Instance,
                new Mock<ILocalScopeBaselineService>().Object,
                statusServiceMock.Object);

            var result = await controller.GetLocalStatus(CancellationToken.None);
            var objResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(400, objResult.StatusCode);
        }

        [Theory]
        [InlineData("")]
        [InlineData(" ")]
        [InlineData("invalid")]
        [InlineData("2025")]
        public async Task SyncController_CheckOnlineStatus_InvalidDatabase_Returns400(string invalidDb)
        {
            var syncProviderMock = new Mock<ISyncConnectionProvider>();
            syncProviderMock.Setup(p => p.GetSelectedDatabaseId()).Returns(invalidDb);

            var statusServiceMock = new Mock<ISyncStatusService>();

            var controller = new SyncController(
                new Mock<ILocalOutboxPushService>().Object,
                new Mock<ILocalDailyPullService>().Object,
                syncProviderMock.Object,
                CreateConfig(),
                NullLogger<SyncController>.Instance,
                new Mock<ILocalScopeBaselineService>().Object,
                statusServiceMock.Object);

            var result = await controller.CheckOnlineStatus(CancellationToken.None);
            var objResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(400, objResult.StatusCode);
        }

        [Fact]
        public async Task SyncController_GetLocalStatus_Success_ReturnsOk()
        {
            var syncProviderMock = new Mock<ISyncConnectionProvider>();
            syncProviderMock.Setup(p => p.GetSelectedDatabaseId()).Returns("2026");

            var statusServiceMock = new Mock<ISyncStatusService>();
            statusServiceMock.Setup(s => s.GetLocalStatusAsync("2026", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new LocalSyncStatusDto
                {
                    DatabaseId = "2026",
                    PendingCount = 3,
                    LastServerVersion = 42
                });

            var controller = new SyncController(
                new Mock<ILocalOutboxPushService>().Object,
                new Mock<ILocalDailyPullService>().Object,
                syncProviderMock.Object,
                CreateConfig(),
                NullLogger<SyncController>.Instance,
                new Mock<ILocalScopeBaselineService>().Object,
                statusServiceMock.Object);

            var result = await controller.GetLocalStatus(CancellationToken.None);
            var okResult = Assert.IsType<OkObjectResult>(result);
            var dto = Assert.IsType<LocalSyncStatusDto>(okResult.Value);
            Assert.Equal("2026", dto.DatabaseId);
            Assert.Equal(3, dto.PendingCount);
            Assert.Equal(42, dto.LastServerVersion);
        }

        [Fact]
        public async Task SyncController_CheckOnlineStatus_Success_ReturnsOk()
        {
            var syncProviderMock = new Mock<ISyncConnectionProvider>();
            syncProviderMock.Setup(p => p.GetSelectedDatabaseId()).Returns("2026");

            var statusServiceMock = new Mock<ISyncStatusService>();
            statusServiceMock.Setup(s => s.CheckOnlineStatusAsync("2026", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OnlineSyncStatusDto
                {
                    DatabaseId = "2026",
                    IsOnline = true,
                    OverallStatus = "UP_TO_DATE"
                });

            var controller = new SyncController(
                new Mock<ILocalOutboxPushService>().Object,
                new Mock<ILocalDailyPullService>().Object,
                syncProviderMock.Object,
                CreateConfig(),
                NullLogger<SyncController>.Instance,
                new Mock<ILocalScopeBaselineService>().Object,
                statusServiceMock.Object);

            var result = await controller.CheckOnlineStatus(CancellationToken.None);
            var okResult = Assert.IsType<OkObjectResult>(result);
            var dto = Assert.IsType<OnlineSyncStatusDto>(okResult.Value);
            Assert.Equal("2026", dto.DatabaseId);
            Assert.True(dto.IsOnline);
            Assert.Equal("UP_TO_DATE", dto.OverallStatus);
        }

        #endregion

        #region 3. Status Derivation Logic Tests

        [Fact]
        public async Task CheckOnlineStatusAsync_RemoteUnreachable_ReturnsUnknown_WithIsOnlineFalse()
        {
            var syncProviderMock = new Mock<ISyncConnectionProvider>();
            syncProviderMock.Setup(p => p.GetLocalConnectionString("2026")).Returns("Server=mock;Database=mock;");

            var remoteFactoryMock = new Mock<IRemoteDatabaseConnectionFactory>();
            remoteFactoryMock.Setup(f => f.CreateOpenConnectionAsync("2026", It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("Network down: cannot reach Azure."));

            var baselineServiceMock = new Mock<ILocalScopeBaselineService>();
            baselineServiceMock.Setup(b => b.GetScopeStatusAsync(It.IsAny<DbConnection>(), It.IsAny<DbTransaction?>(), "2026", "Daily", It.IsAny<CancellationToken>()))
                .ReturnsAsync(SyncScopeBaselineStatus.Baselined);
            baselineServiceMock.Setup(b => b.GetScopeStatusAsync(It.IsAny<DbConnection>(), It.IsAny<DbTransaction?>(), "2026", "Forms", It.IsAny<CancellationToken>()))
                .ReturnsAsync(SyncScopeBaselineStatus.NotBaselined);

            var service = new TestableSyncStatusService(
                syncProviderMock.Object,
                remoteFactoryMock.Object,
                baselineServiceMock.Object)
            {
                MockLocalStatus = new LocalSyncStatusDto { DatabaseId = "2026", LastServerVersion = 8, PendingCount = 1 },
                MockPendingDaily = 1,
                MockPendingForms = 0
            };

            var result = await service.CheckOnlineStatusAsync("2026", CancellationToken.None);

            Assert.False(result.IsOnline);
            Assert.Equal("UNKNOWN", result.OverallStatus);
            Assert.Contains("تعذر الاتصال بالسحابة", result.ErrorMessage);
            Assert.Equal(2, result.Scopes.Count);

            var dailyScope = result.Scopes.Find(s => s.Scope == "Daily");
            Assert.NotNull(dailyScope);
            Assert.Equal("UNKNOWN", dailyScope!.Status);
            Assert.True(dailyScope.IsBaselined);

            var formsScope = result.Scopes.Find(s => s.Scope == "Forms");
            Assert.NotNull(formsScope);
            Assert.Equal("NOT_BASELINED", formsScope!.Status);
            Assert.False(formsScope.IsBaselined);
        }

        [Fact]
        public async Task CheckOnlineStatusAsync_FormsScopeNotBaselined_AlwaysReturnsNotBaselined_EvenIfVersionsMatch()
        {
            // CRITICAL INVARIANT: Forms must display NOT_BASELINED until a separately authorized baseline occurs.
            // Never infer UP_TO_DATE merely from W == V.
            var syncProviderMock = new Mock<ISyncConnectionProvider>();
            syncProviderMock.Setup(p => p.GetLocalConnectionString("2026")).Returns("Server=mock;Database=mock;");

            var testRemoteConn = new TestDbConnection();
            testRemoteConn.Command.ScalarResult = 8L; // Remote version = 8

            var remoteFactoryMock = new Mock<IRemoteDatabaseConnectionFactory>();
            remoteFactoryMock.Setup(f => f.CreateOpenConnectionAsync("2026", It.IsAny<CancellationToken>()))
                .ReturnsAsync(testRemoteConn);

            var baselineServiceMock = new Mock<ILocalScopeBaselineService>();
            baselineServiceMock.Setup(b => b.GetScopeStatusAsync(It.IsAny<DbConnection>(), It.IsAny<DbTransaction?>(), "2026", "Daily", It.IsAny<CancellationToken>()))
                .ReturnsAsync(SyncScopeBaselineStatus.Baselined);
            baselineServiceMock.Setup(b => b.GetScopeStatusAsync(It.IsAny<DbConnection>(), It.IsAny<DbTransaction?>(), "2026", "Forms", It.IsAny<CancellationToken>()))
                .ReturnsAsync(SyncScopeBaselineStatus.NotBaselined); // Forms NOT baselined!

            var service = new TestableSyncStatusService(
                syncProviderMock.Object,
                remoteFactoryMock.Object,
                baselineServiceMock.Object)
            {
                MockLocalStatus = new LocalSyncStatusDto { DatabaseId = "2026", LastServerVersion = 8L, PendingCount = 0 }, // W == 8, matching server V == 8!
                MockPendingDaily = 0,
                MockPendingForms = 0
            };

            var result = await service.CheckOnlineStatusAsync("2026", CancellationToken.None);

            Assert.True(result.IsOnline);
            var formsScope = result.Scopes.Find(s => s.Scope == "Forms");
            Assert.NotNull(formsScope);
            Assert.Equal("NOT_BASELINED", formsScope!.Status);
            Assert.False(formsScope.IsBaselined);
            Assert.Contains("NOT_BASELINED", formsScope.Message);

            var dailyScope = result.Scopes.Find(s => s.Scope == "Daily");
            Assert.NotNull(dailyScope);
            Assert.Equal("UP_TO_DATE", dailyScope!.Status);
            Assert.True(dailyScope.IsBaselined);
        }

        [Fact]
        public async Task CheckOnlineStatusAsync_DailyScope_RemoteNewer_ReturnsRemoteNewer()
        {
            var syncProviderMock = new Mock<ISyncConnectionProvider>();
            syncProviderMock.Setup(p => p.GetLocalConnectionString("2026")).Returns("Server=mock;Database=mock;");

            var testRemoteConn = new TestDbConnection();
            testRemoteConn.Command.ScalarResult = 15L; // Server = 15

            var remoteFactoryMock = new Mock<IRemoteDatabaseConnectionFactory>();
            remoteFactoryMock.Setup(f => f.CreateOpenConnectionAsync("2026", It.IsAny<CancellationToken>()))
                .ReturnsAsync(testRemoteConn);

            var baselineServiceMock = new Mock<ILocalScopeBaselineService>();
            baselineServiceMock.Setup(b => b.GetScopeStatusAsync(It.IsAny<DbConnection>(), It.IsAny<DbTransaction?>(), "2026", "Daily", It.IsAny<CancellationToken>()))
                .ReturnsAsync(SyncScopeBaselineStatus.Baselined);
            baselineServiceMock.Setup(b => b.GetScopeStatusAsync(It.IsAny<DbConnection>(), It.IsAny<DbTransaction?>(), "2026", "Forms", It.IsAny<CancellationToken>()))
                .ReturnsAsync(SyncScopeBaselineStatus.NotBaselined);

            var service = new TestableSyncStatusService(
                syncProviderMock.Object,
                remoteFactoryMock.Object,
                baselineServiceMock.Object)
            {
                MockLocalStatus = new LocalSyncStatusDto { DatabaseId = "2026", LastServerVersion = 8L, PendingCount = 0 }, // Local = 8 < 15
                MockPendingDaily = 0,
                MockPendingForms = 0
            };

            var result = await service.CheckOnlineStatusAsync("2026", CancellationToken.None);

            Assert.True(result.IsOnline);
            var dailyScope = result.Scopes.Find(s => s.Scope == "Daily");
            Assert.NotNull(dailyScope);
            Assert.Equal("REMOTE_NEWER", dailyScope!.Status);
            Assert.Equal("REMOTE_NEWER", result.OverallStatus);
        }

        [Fact]
        public async Task CheckOnlineStatusAsync_DailyScope_BothChanged_ReturnsBothChanged()
        {
            var syncProviderMock = new Mock<ISyncConnectionProvider>();
            syncProviderMock.Setup(p => p.GetLocalConnectionString("2026")).Returns("Server=mock;Database=mock;");

            var testRemoteConn = new TestDbConnection();
            testRemoteConn.Command.ScalarResult = 15L; // Server = 15

            var remoteFactoryMock = new Mock<IRemoteDatabaseConnectionFactory>();
            remoteFactoryMock.Setup(f => f.CreateOpenConnectionAsync("2026", It.IsAny<CancellationToken>()))
                .ReturnsAsync(testRemoteConn);

            var baselineServiceMock = new Mock<ILocalScopeBaselineService>();
            baselineServiceMock.Setup(b => b.GetScopeStatusAsync(It.IsAny<DbConnection>(), It.IsAny<DbTransaction?>(), "2026", "Daily", It.IsAny<CancellationToken>()))
                .ReturnsAsync(SyncScopeBaselineStatus.Baselined);
            baselineServiceMock.Setup(b => b.GetScopeStatusAsync(It.IsAny<DbConnection>(), It.IsAny<DbTransaction?>(), "2026", "Forms", It.IsAny<CancellationToken>()))
                .ReturnsAsync(SyncScopeBaselineStatus.NotBaselined);

            var service = new TestableSyncStatusService(
                syncProviderMock.Object,
                remoteFactoryMock.Object,
                baselineServiceMock.Object)
            {
                MockLocalStatus = new LocalSyncStatusDto { DatabaseId = "2026", LastServerVersion = 8L, PendingCount = 2 }, // Local = 8 < 15 AND pending = 2
                MockPendingDaily = 2,
                MockPendingForms = 0
            };

            var result = await service.CheckOnlineStatusAsync("2026", CancellationToken.None);

            Assert.True(result.IsOnline);
            var dailyScope = result.Scopes.Find(s => s.Scope == "Daily");
            Assert.NotNull(dailyScope);
            Assert.Equal("BOTH_CHANGED", dailyScope!.Status);
            Assert.Equal("BOTH_CHANGED", result.OverallStatus);
        }

        #endregion
    }
}
