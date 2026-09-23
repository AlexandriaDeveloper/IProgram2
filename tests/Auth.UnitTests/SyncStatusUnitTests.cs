#nullable enable
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Api.Controllers;
using Auth.Infrastructure.Services;
using Auth.Infrastructure.Sync;
using Auth.Infrastructure.Sync.Push;
using Core.Exceptions;
using Core.Interfaces;
using Core.Models.Sync;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
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

        private class TestDbDataReader : DbDataReader
        {
            private readonly List<string> _items;
            private int _currentIndex = -1;

            public TestDbDataReader(List<string>? items = null)
            {
                _items = items ?? new List<string>();
            }

            public override int FieldCount => 1;
            public override bool HasRows => _items.Count > 0;
            public override bool IsClosed => false;
            public override int RecordsAffected => _items.Count;
            public override int Depth => 0;

            public override bool Read()
            {
                _currentIndex++;
                return _currentIndex < _items.Count;
            }

            public override Task<bool> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(Read());
            public override bool NextResult() => false;

            public override string GetString(int ordinal) => _items[_currentIndex];
            public override object GetValue(int ordinal) => _items[_currentIndex];
            public override bool IsDBNull(int ordinal) => false;
            public override string GetName(int ordinal) => "EntityType";
            public override int GetOrdinal(string name) => 0;
            public override string GetDataTypeName(int ordinal) => "nvarchar";
            public override Type GetFieldType(int ordinal) => typeof(string);
            public override object this[int ordinal] => _items[_currentIndex];
            public override object this[string name] => _items[_currentIndex];
            public override int GetValues(object[] values) { values[0] = _items[_currentIndex]; return 1; }
            public override bool GetBoolean(int ordinal) => false;
            public override byte GetByte(int ordinal) => 0;
            public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => 0;
            public override char GetChar(int ordinal) => ' ';
            public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => 0;
            public override Guid GetGuid(int ordinal) => Guid.Empty;
            public override short GetInt16(int ordinal) => 0;
            public override int GetInt32(int ordinal) => 0;
            public override long GetInt64(int ordinal) => 0;
            public override float GetFloat(int ordinal) => 0;
            public override double GetDouble(int ordinal) => 0;
            public override decimal GetDecimal(int ordinal) => 0;
            public override DateTime GetDateTime(int ordinal) => DateTime.MinValue;
            public override System.Collections.IEnumerator GetEnumerator() => _items.GetEnumerator();
        }

        private class TestDbCommand : DbCommand
        {
            public object? ScalarResult { get; set; }
            public List<string> FeedEntityTypes { get; set; } = new();
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
            protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => new TestDbDataReader(FeedEntityTypes);
            protected override Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken) =>
                Task.FromResult<DbDataReader>(new TestDbDataReader(FeedEntityTypes));
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

        private static IConfiguration CreateConfig(Dictionary<string, string?>? initialData = null)
        {
            return new ConfigurationBuilder()
                .AddInMemoryCollection(initialData ?? new Dictionary<string, string?>())
                .Build();
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

        [Fact]
        public void SyncController_Constructor_NullSyncStatusService_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() => new SyncController(
                new Mock<ILocalOutboxPushService>().Object,
                new Mock<ILocalDailyPullService>().Object,
                new Mock<ISyncConnectionProvider>().Object,
                CreateConfig(),
                NullLogger<SyncController>.Instance,
                new Mock<ILocalScopeBaselineService>().Object,
                syncStatusService: null!));
        }

        #endregion

        #region 2. Dedicated Manual Remote Connection Source Tests (P0-1)

        [Fact]
        public void DbConnectionProvider_GetManualSyncRemoteConnectionString_WhenMissing_ThrowsInvalidOperationException()
        {
            var config = CreateConfig();
            var httpContextAccessorMock = new Mock<IHttpContextAccessor>();
            var provider = new DbConnectionProvider(httpContextAccessorMock.Object, config);

            var ex = Assert.Throws<InvalidOperationException>(() => provider.GetManualSyncRemoteConnectionString("2026"));
            Assert.Equal("MANUAL_SYNC_REMOTE_NOT_CONFIGURED", ex.Message);
        }

        [Fact]
        public void DbConnectionProvider_GetManualSyncRemoteConnectionString_WhenCatalogMismatch_ThrowsPhysicalDatabaseMismatchException()
        {
            var config = CreateConfig(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ManualSyncRemote2026"] = "Server=tcp:iprogram.database.windows.net,1433;Database=IProgramDb2027;User Id=usr;Password=pwd;"
            });
            var httpContextAccessorMock = new Mock<IHttpContextAccessor>();
            var provider = new DbConnectionProvider(httpContextAccessorMock.Object, config);

            Assert.Throws<PhysicalDatabaseMismatchException>(() => provider.GetManualSyncRemoteConnectionString("2026"));
        }

        [Fact]
        public void DbConnectionProvider_GetManualSyncRemoteConnectionString_WhenNonAzure_ThrowsInvalidOperationException()
        {
            var config = CreateConfig(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ManualSyncRemote2026"] = "Server=localhost;Database=IProgramDb2026;User Id=usr;Password=pwd;"
            });
            var httpContextAccessorMock = new Mock<IHttpContextAccessor>();
            var provider = new DbConnectionProvider(httpContextAccessorMock.Object, config);

            Assert.Throws<InvalidOperationException>(() => provider.GetManualSyncRemoteConnectionString("2026"));
        }

        [Fact]
        public async Task AzureRemoteDatabaseConnectionFactory_InLocalFirst_ResolvesManualSyncRemoteSource()
        {
            var syncProviderMock = new Mock<ISyncConnectionProvider>();
            syncProviderMock.Setup(p => p.IsLocalFirstEnabled).Returns(true);
            syncProviderMock.Setup(p => p.GetManualSyncRemoteConnectionString("2026"))
                .Throws(new InvalidOperationException("MANUAL_SYNC_REMOTE_NOT_CONFIGURED"));

            var factory = new AzureRemoteDatabaseConnectionFactory(syncProviderMock.Object);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => factory.CreateOpenConnectionAsync("2026", CancellationToken.None));
            Assert.Equal("MANUAL_SYNC_REMOTE_NOT_CONFIGURED", ex.Message);
            syncProviderMock.Verify(p => p.GetManualSyncRemoteConnectionString("2026"), Times.Once);
            syncProviderMock.Verify(p => p.GetRemoteConnectionString(It.IsAny<string>()), Times.Never);
        }

        #endregion

        #region 3. Controller Mode Guards & Sanitization Tests (P0-6, P1)

        [Fact]
        public async Task SyncController_GetLocalStatus_WhenNotLocalFirst_Returns403()
        {
            var syncProviderMock = new Mock<ISyncConnectionProvider>();
            syncProviderMock.Setup(p => p.IsLocalFirstEnabled).Returns(false); // Online mode

            var controller = new SyncController(
                new Mock<ILocalOutboxPushService>().Object,
                new Mock<ILocalDailyPullService>().Object,
                syncProviderMock.Object,
                CreateConfig(),
                NullLogger<SyncController>.Instance,
                new Mock<ILocalScopeBaselineService>().Object,
                new Mock<ISyncStatusService>().Object);

            var result = await controller.GetLocalStatus(CancellationToken.None);
            var objResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(403, objResult.StatusCode);
        }

        [Fact]
        public async Task SyncController_CheckOnlineStatus_WhenNotLocalFirst_Returns403()
        {
            var syncProviderMock = new Mock<ISyncConnectionProvider>();
            syncProviderMock.Setup(p => p.IsLocalFirstEnabled).Returns(false); // Online mode

            var controller = new SyncController(
                new Mock<ILocalOutboxPushService>().Object,
                new Mock<ILocalDailyPullService>().Object,
                syncProviderMock.Object,
                CreateConfig(),
                NullLogger<SyncController>.Instance,
                new Mock<ILocalScopeBaselineService>().Object,
                new Mock<ISyncStatusService>().Object);

            var result = await controller.CheckOnlineStatus(CancellationToken.None);
            var objResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(403, objResult.StatusCode);
        }

        [Fact]
        public async Task SyncController_PullDaily_WhenNotLocalFirst_Returns403()
        {
            var syncProviderMock = new Mock<ISyncConnectionProvider>();
            syncProviderMock.Setup(p => p.IsLocalFirstEnabled).Returns(false); // Online mode
            syncProviderMock.Setup(p => p.IsReadOnlyMode).Returns(false);

            var controller = new SyncController(
                new Mock<ILocalOutboxPushService>().Object,
                new Mock<ILocalDailyPullService>().Object,
                syncProviderMock.Object,
                CreateConfig(),
                NullLogger<SyncController>.Instance,
                new Mock<ILocalScopeBaselineService>().Object,
                new Mock<ISyncStatusService>().Object);

            var result = await controller.PullDaily(CancellationToken.None);
            var objResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(403, objResult.StatusCode);
        }

        [Fact]
        public async Task SyncController_PullDaily_WhenReadOnly_Returns403()
        {
            var syncProviderMock = new Mock<ISyncConnectionProvider>();
            syncProviderMock.Setup(p => p.IsLocalFirstEnabled).Returns(true);
            syncProviderMock.Setup(p => p.IsReadOnlyMode).Returns(true); // ReadOnly mode

            var controller = new SyncController(
                new Mock<ILocalOutboxPushService>().Object,
                new Mock<ILocalDailyPullService>().Object,
                syncProviderMock.Object,
                CreateConfig(),
                NullLogger<SyncController>.Instance,
                new Mock<ILocalScopeBaselineService>().Object,
                new Mock<ISyncStatusService>().Object);

            var result = await controller.PullDaily(CancellationToken.None);
            var objResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(403, objResult.StatusCode);
        }

        [Fact]
        public async Task SyncController_CheckOnlineStatus_WhenManualRemoteNotConfigured_Returns503Sanitized()
        {
            var syncProviderMock = new Mock<ISyncConnectionProvider>();
            syncProviderMock.Setup(p => p.IsLocalFirstEnabled).Returns(true);
            syncProviderMock.Setup(p => p.GetSelectedDatabaseId()).Returns("2026");

            var statusServiceMock = new Mock<ISyncStatusService>();
            statusServiceMock.Setup(s => s.CheckOnlineStatusAsync("2026", It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("MANUAL_SYNC_REMOTE_NOT_CONFIGURED"));

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
            Assert.Equal(503, objResult.StatusCode);
        }

        #endregion

        #region 4. Status Derivation Logic & Canonical States (P0-2, P0-3)

        [Fact]
        public async Task CheckOnlineStatusAsync_RemoteUnreachable_ReturnsUnknown_WithSanitizedError()
        {
            var syncProviderMock = new Mock<ISyncConnectionProvider>();
            syncProviderMock.Setup(p => p.GetLocalConnectionString("2026")).Returns("Server=mock;Database=mock;");

            var remoteFactoryMock = new Mock<IRemoteDatabaseConnectionFactory>();
            remoteFactoryMock.Setup(f => f.CreateOpenConnectionAsync("2026", It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("TCP Connection failed to server secret-endpoint.database.windows.net"));

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
            // Sanitized error - does NOT leak raw endpoint or exception details
            Assert.Equal("تعذر الاتصال بالخادم السحابي", result.ErrorMessage);
            Assert.DoesNotContain("secret-endpoint", result.ErrorMessage);
        }

        [Fact]
        public async Task CheckOnlineStatusAsync_LocalPending_WhenVersionsEqualAndPendingGreaterThanZero_ReturnsLocalPending()
        {
            var syncProviderMock = new Mock<ISyncConnectionProvider>();
            syncProviderMock.Setup(p => p.GetLocalConnectionString("2026")).Returns("Server=mock;Database=mock;");

            var testRemoteConn = new TestDbConnection();
            testRemoteConn.Command.ScalarResult = 8L; // V == 8, matching W == 8

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
                MockLocalStatus = new LocalSyncStatusDto { DatabaseId = "2026", LastServerVersion = 8L, PendingCount = 3 },
                MockPendingDaily = 3,
                MockPendingForms = 0
            };

            var result = await service.CheckOnlineStatusAsync("2026", CancellationToken.None);

            Assert.True(result.IsOnline);
            var dailyScope = result.Scopes.Find(s => s.Scope == "Daily");
            Assert.NotNull(dailyScope);
            Assert.Equal("LOCAL_PENDING", dailyScope!.Status);
            Assert.Equal("LOCAL_PENDING", result.OverallStatus);
        }

        [Fact]
        public async Task CheckOnlineStatusAsync_FormsScopeNotBaselined_NeverReturnsUpToDateOverall()
        {
            // P0-2: When Daily=UP_TO_DATE and Forms=NOT_BASELINED, overall must be NOT_BASELINED
            var syncProviderMock = new Mock<ISyncConnectionProvider>();
            syncProviderMock.Setup(p => p.GetLocalConnectionString("2026")).Returns("Server=mock;Database=mock;");

            var testRemoteConn = new TestDbConnection();
            testRemoteConn.Command.ScalarResult = 8L; // Remote V == 8, local W == 8, pending == 0

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
                MockLocalStatus = new LocalSyncStatusDto { DatabaseId = "2026", LastServerVersion = 8L, PendingCount = 0 },
                MockPendingDaily = 0,
                MockPendingForms = 0
            };

            var result = await service.CheckOnlineStatusAsync("2026", CancellationToken.None);

            Assert.True(result.IsOnline);
            var dailyScope = result.Scopes.Find(s => s.Scope == "Daily");
            var formsScope = result.Scopes.Find(s => s.Scope == "Forms");

            Assert.Equal("UP_TO_DATE", dailyScope!.Status);
            Assert.Equal("NOT_BASELINED", formsScope!.Status);

            // Precedence: NOT_BASELINED > UP_TO_DATE. Overall must NOT be UP_TO_DATE!
            Assert.Equal("NOT_BASELINED", result.OverallStatus);
        }

        [Fact]
        public async Task CheckOnlineStatusAsync_SyncStateError_WhenLocalVersionAheadOfServer()
        {
            // W (10) > V (8) => SYNC_STATE_ERROR
            var syncProviderMock = new Mock<ISyncConnectionProvider>();
            syncProviderMock.Setup(p => p.GetLocalConnectionString("2026")).Returns("Server=mock;Database=mock;");

            var testRemoteConn = new TestDbConnection();
            testRemoteConn.Command.ScalarResult = 8L; // Server = 8

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
                MockLocalStatus = new LocalSyncStatusDto { DatabaseId = "2026", LastServerVersion = 10L, PendingCount = 0 }, // W = 10 > 8
                MockPendingDaily = 0,
                MockPendingForms = 0
            };

            var result = await service.CheckOnlineStatusAsync("2026", CancellationToken.None);

            Assert.Equal("SYNC_STATE_ERROR", result.OverallStatus);
            var dailyScope = result.Scopes.Find(s => s.Scope == "Daily");
            Assert.Equal("SYNC_STATE_ERROR", dailyScope!.Status);
        }

        [Fact]
        public async Task CheckOnlineStatusAsync_SyncStateError_WhenFailedOutboxRowsExist()
        {
            var syncProviderMock = new Mock<ISyncConnectionProvider>();
            syncProviderMock.Setup(p => p.GetLocalConnectionString("2026")).Returns("Server=mock;Database=mock;");

            var testRemoteConn = new TestDbConnection();
            testRemoteConn.Command.ScalarResult = 8L;

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
                MockLocalStatus = new LocalSyncStatusDto { DatabaseId = "2026", LastServerVersion = 8L, FailedCount = 1 }, // FAILED = 1
                MockPendingDaily = 0,
                MockPendingForms = 0
            };

            var result = await service.CheckOnlineStatusAsync("2026", CancellationToken.None);

            Assert.Equal("SYNC_STATE_ERROR", result.OverallStatus);
        }

        [Fact]
        public async Task CheckOnlineStatusAsync_SyncStateError_WhenOrphanInProgressExists()
        {
            var syncProviderMock = new Mock<ISyncConnectionProvider>();
            syncProviderMock.Setup(p => p.GetLocalConnectionString("2026")).Returns("Server=mock;Database=mock;");

            var testRemoteConn = new TestDbConnection();
            testRemoteConn.Command.ScalarResult = 8L;

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
                MockLocalStatus = new LocalSyncStatusDto
                {
                    DatabaseId = "2026",
                    LastServerVersion = 8L,
                    InProgressCount = 1,
                    HasOrphanInProgress = true // Orphan in progress without valid active lease
                },
                MockPendingDaily = 0,
                MockPendingForms = 0
            };

            var result = await service.CheckOnlineStatusAsync("2026", CancellationToken.None);

            Assert.Equal("SYNC_STATE_ERROR", result.OverallStatus);
        }

        [Fact]
        public async Task CheckOnlineStatusAsync_ServerChangeFeed_WhenDailyOnlyChanged_DailyRemoteNewer_FormsRemainsNotBaselined()
        {
            // P0-3: Attribute remote changes via ServerChangeFeed metadata query
            var syncProviderMock = new Mock<ISyncConnectionProvider>();
            syncProviderMock.Setup(p => p.GetLocalConnectionString("2026")).Returns("Server=mock;Database=mock;");

            var testRemoteConn = new TestDbConnection();
            testRemoteConn.Command.ScalarResult = 15L; // Server = 15 > 8
            testRemoteConn.Command.FeedEntityTypes = new List<string> { "Daily" }; // Only Daily changed in feed

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
                MockLocalStatus = new LocalSyncStatusDto { DatabaseId = "2026", LastServerVersion = 8L, PendingCount = 0 },
                MockPendingDaily = 0,
                MockPendingForms = 0
            };

            var result = await service.CheckOnlineStatusAsync("2026", CancellationToken.None);

            var dailyScope = result.Scopes.Find(s => s.Scope == "Daily");
            var formsScope = result.Scopes.Find(s => s.Scope == "Forms");

            Assert.Equal("REMOTE_NEWER", dailyScope!.Status);
            Assert.Equal("NOT_BASELINED", formsScope!.Status);
            Assert.Equal("REMOTE_NEWER", result.OverallStatus);
        }

        [Fact]
        public async Task CheckOnlineStatusAsync_ServerChangeFeed_WhenFormsOnlyChanged_DailyUpToDate_FormsRemainsNotBaselined()
        {
            // P0-3: If only Forms changed remotely, Daily must NOT be marked REMOTE_NEWER!
            var syncProviderMock = new Mock<ISyncConnectionProvider>();
            syncProviderMock.Setup(p => p.GetLocalConnectionString("2026")).Returns("Server=mock;Database=mock;");

            var testRemoteConn = new TestDbConnection();
            testRemoteConn.Command.ScalarResult = 15L; // Server = 15 > 8
            testRemoteConn.Command.FeedEntityTypes = new List<string> { "Form" }; // Only Forms changed in feed

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
                MockLocalStatus = new LocalSyncStatusDto { DatabaseId = "2026", LastServerVersion = 8L, PendingCount = 0 },
                MockPendingDaily = 0,
                MockPendingForms = 0
            };

            var result = await service.CheckOnlineStatusAsync("2026", CancellationToken.None);

            var dailyScope = result.Scopes.Find(s => s.Scope == "Daily");
            var formsScope = result.Scopes.Find(s => s.Scope == "Forms");

            Assert.Equal("UP_TO_DATE", dailyScope!.Status); // Daily did not change in (8, 15]!
            Assert.Equal("NOT_BASELINED", formsScope!.Status); // Forms is not baselined
            Assert.Equal("NOT_BASELINED", result.OverallStatus); // Cannot be UP_TO_DATE when Forms is not baselined
        }

        [Fact]
        public async Task CheckOnlineStatusAsync_ServerChangeFeed_WhenFeedGapped_ReturnsSyncStateError()
        {
            // V (15) > W (8), but ServerChangeFeed returns 0 rows => feed gap / inconsistency => SYNC_STATE_ERROR
            var syncProviderMock = new Mock<ISyncConnectionProvider>();
            syncProviderMock.Setup(p => p.GetLocalConnectionString("2026")).Returns("Server=mock;Database=mock;");

            var testRemoteConn = new TestDbConnection();
            testRemoteConn.Command.ScalarResult = 15L;
            testRemoteConn.Command.FeedEntityTypes = new List<string>(); // Empty feed!

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
                MockLocalStatus = new LocalSyncStatusDto { DatabaseId = "2026", LastServerVersion = 8L, PendingCount = 0 },
                MockPendingDaily = 0,
                MockPendingForms = 0
            };

            var result = await service.CheckOnlineStatusAsync("2026", CancellationToken.None);

            Assert.Equal("SYNC_STATE_ERROR", result.OverallStatus);
        }

        [Fact]
        public async Task CheckOnlineStatusAsync_WhenDailyHasRemoteChangesAndLocalPending_ReturnsBothChanged()
        {
            var syncProviderMock = new Mock<ISyncConnectionProvider>();
            syncProviderMock.Setup(p => p.GetLocalConnectionString("2026")).Returns("Server=mock;Database=mock;");

            var testRemoteConn = new TestDbConnection();
            testRemoteConn.Command.ScalarResult = 15L; // Server = 15 > 8
            testRemoteConn.Command.FeedEntityTypes = new List<string> { "Daily" };

            var remoteFactoryMock = new Mock<IRemoteDatabaseConnectionFactory>();
            remoteFactoryMock.Setup(f => f.CreateOpenConnectionAsync("2026", It.IsAny<CancellationToken>()))
                .ReturnsAsync(testRemoteConn);

            var baselineServiceMock = new Mock<ILocalScopeBaselineService>();
            baselineServiceMock.Setup(b => b.GetScopeStatusAsync(It.IsAny<DbConnection>(), It.IsAny<DbTransaction?>(), "2026", "Daily", It.IsAny<CancellationToken>()))
                .ReturnsAsync(SyncScopeBaselineStatus.Baselined);
            baselineServiceMock.Setup(b => b.GetScopeStatusAsync(It.IsAny<DbConnection>(), It.IsAny<DbTransaction?>(), "2026", "Forms", It.IsAny<CancellationToken>()))
                .ReturnsAsync(SyncScopeBaselineStatus.Baselined);

            var service = new TestableSyncStatusService(
                syncProviderMock.Object,
                remoteFactoryMock.Object,
                baselineServiceMock.Object)
            {
                MockLocalStatus = new LocalSyncStatusDto { DatabaseId = "2026", LastServerVersion = 8L, PendingCount = 2 },
                MockPendingDaily = 2,
                MockPendingForms = 0
            };

            var result = await service.CheckOnlineStatusAsync("2026", CancellationToken.None);

            var dailyScope = result.Scopes.Find(s => s.Scope == "Daily");
            var formsScope = result.Scopes.Find(s => s.Scope == "Forms");

            Assert.Equal("BOTH_CHANGED", dailyScope!.Status);
            Assert.Equal("UP_TO_DATE", formsScope!.Status);
            Assert.Equal("BOTH_CHANGED", result.OverallStatus);
            Assert.Equal(2, result.TotalPendingCount);
            Assert.Equal(2, dailyScope.PendingCount);
        }

        [Fact]
        public async Task CheckOnlineStatusAsync_WhenFormsHasRemoteChangesAndLocalPending_WhenBaselined_ReturnsBothChanged()
        {
            var syncProviderMock = new Mock<ISyncConnectionProvider>();
            syncProviderMock.Setup(p => p.GetLocalConnectionString("2026")).Returns("Server=mock;Database=mock;");

            var testRemoteConn = new TestDbConnection();
            testRemoteConn.Command.ScalarResult = 15L; // Server = 15 > 8
            testRemoteConn.Command.FeedEntityTypes = new List<string> { "Form" };

            var remoteFactoryMock = new Mock<IRemoteDatabaseConnectionFactory>();
            remoteFactoryMock.Setup(f => f.CreateOpenConnectionAsync("2026", It.IsAny<CancellationToken>()))
                .ReturnsAsync(testRemoteConn);

            var baselineServiceMock = new Mock<ILocalScopeBaselineService>();
            baselineServiceMock.Setup(b => b.GetScopeStatusAsync(It.IsAny<DbConnection>(), It.IsAny<DbTransaction?>(), "2026", "Daily", It.IsAny<CancellationToken>()))
                .ReturnsAsync(SyncScopeBaselineStatus.Baselined);
            baselineServiceMock.Setup(b => b.GetScopeStatusAsync(It.IsAny<DbConnection>(), It.IsAny<DbTransaction?>(), "2026", "Forms", It.IsAny<CancellationToken>()))
                .ReturnsAsync(SyncScopeBaselineStatus.Baselined);

            var service = new TestableSyncStatusService(
                syncProviderMock.Object,
                remoteFactoryMock.Object,
                baselineServiceMock.Object)
            {
                MockLocalStatus = new LocalSyncStatusDto { DatabaseId = "2026", LastServerVersion = 8L, PendingCount = 4 },
                MockPendingDaily = 0,
                MockPendingForms = 4
            };

            var result = await service.CheckOnlineStatusAsync("2026", CancellationToken.None);

            var dailyScope = result.Scopes.Find(s => s.Scope == "Daily");
            var formsScope = result.Scopes.Find(s => s.Scope == "Forms");

            Assert.Equal("UP_TO_DATE", dailyScope!.Status);
            Assert.Equal("BOTH_CHANGED", formsScope!.Status);
            Assert.Equal("BOTH_CHANGED", result.OverallStatus);
            Assert.Equal(4, formsScope.PendingCount);
        }

        #endregion
    }
}
