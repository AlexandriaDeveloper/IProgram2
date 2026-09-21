#nullable enable
using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Auth.Infrastructure;
using Auth.Infrastructure.Sync.Authoritative;
using Core.Exceptions;
using Core.Interfaces;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Moq;
using Persistence.Repository;
using Xunit;

namespace Auth.UnitTests
{
    /// <summary>
    /// Fast unit tests verifying real AuthoritativeCutoverGuard parameter validation
    /// and non-relational early exit (runs in all test discovery environments including Linux CI).
    /// </summary>
    public class RealAuthoritativeCutoverGuardUnitTests
    {
        [Fact]
        public void ValidateCutoverState_NullContext_ThrowsArgumentNullException()
        {
            var bindingMock = new Mock<IAuthoritativeDatabaseBindingGuard>();
            var guard = new AuthoritativeCutoverGuard(bindingMock.Object);

            Assert.Throws<ArgumentNullException>(() => guard.ValidateCutoverState(null!, "2026"));
        }

        [Fact]
        public async Task ValidateCutoverStateAsync_NullContext_ThrowsArgumentNullException()
        {
            var bindingMock = new Mock<IAuthoritativeDatabaseBindingGuard>();
            var guard = new AuthoritativeCutoverGuard(bindingMock.Object);

            await Assert.ThrowsAsync<ArgumentNullException>(async () =>
                await guard.ValidateCutoverStateAsync(null!, "2026"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("2028")]
        [InlineData("abc")]
        public void ValidateCutoverState_InvalidDatabaseId_ThrowsAuthoritativeCutoverStateUnverifiable(string? dbId)
        {
            var bindingMock = new Mock<IAuthoritativeDatabaseBindingGuard>();
            var guard = new AuthoritativeCutoverGuard(bindingMock.Object);

            var options = new DbContextOptionsBuilder<ApplicationContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            using var context = new ApplicationContext(options);

            var ex = Assert.Throws<AuthoritativeCutoverGuardException>(() => guard.ValidateCutoverState(context, dbId!));
            Assert.Equal(AuthoritativeCutoverErrorCodes.AuthoritativeCutoverStateUnverifiable, ex.ErrorCode);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("2028")]
        [InlineData("abc")]
        public async Task ValidateCutoverStateAsync_InvalidDatabaseId_ThrowsAuthoritativeCutoverStateUnverifiable(string? dbId)
        {
            var bindingMock = new Mock<IAuthoritativeDatabaseBindingGuard>();
            var guard = new AuthoritativeCutoverGuard(bindingMock.Object);

            var options = new DbContextOptionsBuilder<ApplicationContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            using var context = new ApplicationContext(options);

            var ex = await Assert.ThrowsAsync<AuthoritativeCutoverGuardException>(async () =>
                await guard.ValidateCutoverStateAsync(context, dbId!));
            Assert.Equal(AuthoritativeCutoverErrorCodes.AuthoritativeCutoverStateUnverifiable, ex.ErrorCode);
        }

        [Fact]
        public void ValidateCutoverState_NonRelationalDatabase_ExitsSafelyWithoutQuery()
        {
            var bindingMock = new Mock<IAuthoritativeDatabaseBindingGuard>(MockBehavior.Strict);
            var guard = new AuthoritativeCutoverGuard(bindingMock.Object);

            var options = new DbContextOptionsBuilder<ApplicationContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            using var context = new ApplicationContext(options);

            // Must not call bindingMock or throw because context.Database.IsRelational() is false
            guard.ValidateCutoverState(context, "2026");
        }

        [Fact]
        public async Task ValidateCutoverStateAsync_NonRelationalDatabase_ExitsSafelyWithoutQuery()
        {
            var bindingMock = new Mock<IAuthoritativeDatabaseBindingGuard>(MockBehavior.Strict);
            var guard = new AuthoritativeCutoverGuard(bindingMock.Object);

            var options = new DbContextOptionsBuilder<ApplicationContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            using var context = new ApplicationContext(options);

            // Must not call bindingMock or throw because context.Database.IsRelational() is false
            await guard.ValidateCutoverStateAsync(context, "2026");
        }
    }

    /// <summary>
    /// Shared isolated SQL test database fixture for RealAuthoritativeCutoverGuardIntegrationTests.
    /// Creates a single isolated temp DB per class run and drops it completely in Dispose.
    /// </summary>
    public class LocalSqlCutoverTestFixture : IDisposable
    {
        public const string MasterConnStr = "Server=localhost;Database=master;Integrated Security=True;TrustServerCertificate=True;";
        public string DbName { get; }
        public string TestConnStr { get; }

        public LocalSqlCutoverTestFixture()
        {
            DbName = "CutoverGuard_Test_" + Guid.NewGuid().ToString("N");
            TestConnStr = $"Server=localhost;Database={DbName};Integrated Security=True;TrustServerCertificate=True;";

            using (var conn = new SqlConnection(MasterConnStr))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"CREATE DATABASE [{DbName}];";
                cmd.ExecuteNonQuery();
            }

            using (var conn = new SqlConnection(TestConnStr))
            {
                conn.Open();
                using var cmdSchema = conn.CreateCommand();
                cmdSchema.CommandText = "CREATE SCHEMA [sync];";
                cmdSchema.ExecuteNonQuery();

                using var cmdTable = conn.CreateCommand();
                cmdTable.CommandText = @"
                    CREATE TABLE [sync].[ServerState] (
                        [DatabaseId] NVARCHAR(50) NOT NULL,
                        [CurrentVersion] BIGINT NULL
                    );";
                cmdTable.ExecuteNonQuery();
            }
        }

        public void ResetTable()
        {
            using var conn = new SqlConnection(TestConnStr);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                IF OBJECT_ID('[sync].[ServerState]', 'U') IS NULL
                BEGIN
                    CREATE TABLE [sync].[ServerState] (
                        [DatabaseId] NVARCHAR(50) NOT NULL,
                        [CurrentVersion] BIGINT NULL
                    );
                END
                ELSE
                BEGIN
                    DELETE FROM [sync].[ServerState];
                END";
            cmd.ExecuteNonQuery();
        }

        public void DropTable()
        {
            using var conn = new SqlConnection(TestConnStr);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "IF OBJECT_ID('[sync].[ServerState]', 'U') IS NOT NULL DROP TABLE [sync].[ServerState];";
            cmd.ExecuteNonQuery();
        }

        public void Dispose()
        {
            SqlConnection.ClearAllPools();
            try
            {
                using var conn = new SqlConnection(MasterConnStr);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $@"
                    IF DB_ID('{DbName}') IS NOT NULL
                    BEGIN
                        ALTER DATABASE [{DbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                        DROP DATABASE [{DbName}];
                    END";
                cmd.ExecuteNonQuery();
            }
            catch
            {
                // Best effort cleanup in test fixture disposal
            }
        }
    }

    /// <summary>
    /// Architectural Verification Tests testing the REAL concrete AuthoritativeCutoverGuard
    /// against an isolated, temporary local SQL Server database (zero Azure connectivity, zero operational DBs).
    /// Covers Architect Scenarios A through H.
    /// </summary>
    [Trait("Category", "LocalDbRequired")]
    public class RealAuthoritativeCutoverGuardIntegrationTests : IClassFixture<LocalSqlCutoverTestFixture>
    {
        private readonly LocalSqlCutoverTestFixture _fixture;

        public RealAuthoritativeCutoverGuardIntegrationTests(LocalSqlCutoverTestFixture fixture)
        {
            _fixture = fixture;
        }

        private ApplicationContext CreateContext()
        {
            var options = new DbContextOptionsBuilder<ApplicationContext>()
                .UseSqlServer(_fixture.TestConnStr)
                .Options;
            return new ApplicationContext(options);
        }

        private void SetServerState(string dbId, long? version, int rowCount = 1)
        {
            _fixture.ResetTable();

            using var conn = new SqlConnection(_fixture.TestConnStr);
            conn.Open();

            for (int i = 0; i < rowCount; i++)
            {
                using var cmdIns = conn.CreateCommand();
                cmdIns.CommandText = "INSERT INTO [sync].[ServerState] ([DatabaseId], [CurrentVersion]) VALUES (@id, @ver);";
                cmdIns.Parameters.AddWithValue("@id", dbId);
                cmdIns.Parameters.AddWithValue("@ver", (object?)version ?? DBNull.Value);
                cmdIns.ExecuteNonQuery();
            }
        }

        #region Scenario A — CurrentVersion = 2 Throws CUTOVER_COMMITTED_TRACKING_DISABLED

        [Fact]
        public void ScenarioA_Sync_CurrentVersion2_ThrowsCutoverCommittedTrackingDisabled()
        {
            SetServerState("2026", 2);

            var bindingMock = new Mock<IAuthoritativeDatabaseBindingGuard>();
            var guard = new AuthoritativeCutoverGuard(bindingMock.Object);
            using var context = CreateContext();

            var ex = Assert.Throws<AuthoritativeCutoverGuardException>(() =>
                guard.ValidateCutoverState(context, "2026"));

            Assert.Equal(AuthoritativeCutoverErrorCodes.CutoverCommittedTrackingDisabled, ex.ErrorCode);
        }

        [Fact]
        public async Task ScenarioA_Async_CurrentVersion2_ThrowsCutoverCommittedTrackingDisabled()
        {
            SetServerState("2026", 2);

            var bindingMock = new Mock<IAuthoritativeDatabaseBindingGuard>();
            var guard = new AuthoritativeCutoverGuard(bindingMock.Object);
            using var context = CreateContext();

            var ex = await Assert.ThrowsAsync<AuthoritativeCutoverGuardException>(async () =>
                await guard.ValidateCutoverStateAsync(context, "2026"));

            Assert.Equal(AuthoritativeCutoverErrorCodes.CutoverCommittedTrackingDisabled, ex.ErrorCode);
        }

        #endregion

        #region Scenario B — CurrentVersion = 0 Allows Normal Save

        [Fact]
        public void ScenarioB_Sync_CurrentVersion0_AllowsSave()
        {
            SetServerState("2026", 0);

            var bindingMock = new Mock<IAuthoritativeDatabaseBindingGuard>();
            var guard = new AuthoritativeCutoverGuard(bindingMock.Object);
            using var context = CreateContext();

            // Pre-cutover: CurrentVersion == 0 must succeed without throwing
            guard.ValidateCutoverState(context, "2026");
        }

        [Fact]
        public async Task ScenarioB_Async_CurrentVersion0_AllowsSave()
        {
            SetServerState("2026", 0);

            var bindingMock = new Mock<IAuthoritativeDatabaseBindingGuard>();
            var guard = new AuthoritativeCutoverGuard(bindingMock.Object);
            using var context = CreateContext();

            // Pre-cutover: CurrentVersion == 0 must succeed without throwing
            await guard.ValidateCutoverStateAsync(context, "2026");
        }

        #endregion

        #region Scenario C — ServerState Missing Throws AUTHORITATIVE_CUTOVER_STATE_UNVERIFIABLE

        [Fact]
        public void ScenarioC_Sync_MissingServerState_ThrowsUnverifiable()
        {
            SetServerState("2026", null, rowCount: 0);

            var bindingMock = new Mock<IAuthoritativeDatabaseBindingGuard>();
            var guard = new AuthoritativeCutoverGuard(bindingMock.Object);
            using var context = CreateContext();

            var ex = Assert.Throws<AuthoritativeCutoverGuardException>(() =>
                guard.ValidateCutoverState(context, "2026"));

            Assert.Equal(AuthoritativeCutoverErrorCodes.AuthoritativeCutoverStateUnverifiable, ex.ErrorCode);
            Assert.Contains("missing", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task ScenarioC_Async_MissingServerState_ThrowsUnverifiable()
        {
            SetServerState("2026", null, rowCount: 0);

            var bindingMock = new Mock<IAuthoritativeDatabaseBindingGuard>();
            var guard = new AuthoritativeCutoverGuard(bindingMock.Object);
            using var context = CreateContext();

            var ex = await Assert.ThrowsAsync<AuthoritativeCutoverGuardException>(async () =>
                await guard.ValidateCutoverStateAsync(context, "2026"));

            Assert.Equal(AuthoritativeCutoverErrorCodes.AuthoritativeCutoverStateUnverifiable, ex.ErrorCode);
            Assert.Contains("missing", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        #endregion

        #region Scenario D — Duplicate ServerState Rows Throws AUTHORITATIVE_CUTOVER_STATE_UNVERIFIABLE

        [Fact]
        public void ScenarioD_Sync_DuplicateServerStateRows_ThrowsUnverifiable()
        {
            SetServerState("2026", 2, rowCount: 2);

            var bindingMock = new Mock<IAuthoritativeDatabaseBindingGuard>();
            var guard = new AuthoritativeCutoverGuard(bindingMock.Object);
            using var context = CreateContext();

            var ex = Assert.Throws<AuthoritativeCutoverGuardException>(() =>
                guard.ValidateCutoverState(context, "2026"));

            Assert.Equal(AuthoritativeCutoverErrorCodes.AuthoritativeCutoverStateUnverifiable, ex.ErrorCode);
            Assert.Contains("duplicate", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task ScenarioD_Async_DuplicateServerStateRows_ThrowsUnverifiable()
        {
            SetServerState("2026", 2, rowCount: 2);

            var bindingMock = new Mock<IAuthoritativeDatabaseBindingGuard>();
            var guard = new AuthoritativeCutoverGuard(bindingMock.Object);
            using var context = CreateContext();

            var ex = await Assert.ThrowsAsync<AuthoritativeCutoverGuardException>(async () =>
                await guard.ValidateCutoverStateAsync(context, "2026"));

            Assert.Equal(AuthoritativeCutoverErrorCodes.AuthoritativeCutoverStateUnverifiable, ex.ErrorCode);
            Assert.Contains("duplicate", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        #endregion

        #region Scenario E — Negative or Null CurrentVersion Throws AUTHORITATIVE_CUTOVER_STATE_UNVERIFIABLE

        [Fact]
        public void ScenarioE_Sync_NegativeCurrentVersion_ThrowsUnverifiable()
        {
            SetServerState("2026", -1);

            var bindingMock = new Mock<IAuthoritativeDatabaseBindingGuard>();
            var guard = new AuthoritativeCutoverGuard(bindingMock.Object);
            using var context = CreateContext();

            var ex = Assert.Throws<AuthoritativeCutoverGuardException>(() =>
                guard.ValidateCutoverState(context, "2026"));

            Assert.Equal(AuthoritativeCutoverErrorCodes.AuthoritativeCutoverStateUnverifiable, ex.ErrorCode);
            Assert.Contains("invalid", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task ScenarioE_Async_NegativeCurrentVersion_ThrowsUnverifiable()
        {
            SetServerState("2026", -1);

            var bindingMock = new Mock<IAuthoritativeDatabaseBindingGuard>();
            var guard = new AuthoritativeCutoverGuard(bindingMock.Object);
            using var context = CreateContext();

            var ex = await Assert.ThrowsAsync<AuthoritativeCutoverGuardException>(async () =>
                await guard.ValidateCutoverStateAsync(context, "2026"));

            Assert.Equal(AuthoritativeCutoverErrorCodes.AuthoritativeCutoverStateUnverifiable, ex.ErrorCode);
            Assert.Contains("invalid", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ScenarioE_Sync_NullCurrentVersion_ThrowsUnverifiable()
        {
            SetServerState("2026", null);

            var bindingMock = new Mock<IAuthoritativeDatabaseBindingGuard>();
            var guard = new AuthoritativeCutoverGuard(bindingMock.Object);
            using var context = CreateContext();

            var ex = Assert.Throws<AuthoritativeCutoverGuardException>(() =>
                guard.ValidateCutoverState(context, "2026"));

            Assert.Equal(AuthoritativeCutoverErrorCodes.AuthoritativeCutoverStateUnverifiable, ex.ErrorCode);
            Assert.Contains("null or invalid", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task ScenarioE_Async_NullCurrentVersion_ThrowsUnverifiable()
        {
            SetServerState("2026", null);

            var bindingMock = new Mock<IAuthoritativeDatabaseBindingGuard>();
            var guard = new AuthoritativeCutoverGuard(bindingMock.Object);
            using var context = CreateContext();

            var ex = await Assert.ThrowsAsync<AuthoritativeCutoverGuardException>(async () =>
                await guard.ValidateCutoverStateAsync(context, "2026"));

            Assert.Equal(AuthoritativeCutoverErrorCodes.AuthoritativeCutoverStateUnverifiable, ex.ErrorCode);
            Assert.Contains("null or invalid", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        #endregion

        #region Scenario F — Wrong Physical Binding Wrapped to AUTHORITATIVE_CUTOVER_STATE_UNVERIFIABLE

        [Fact]
        public void ScenarioF_Sync_BindingException_WrappedToAuthoritativeCutoverStateUnverifiable()
        {
            SetServerState("2026", 0);

            var bindingMock = new Mock<IAuthoritativeDatabaseBindingGuard>();
            bindingMock
                .Setup(b => b.ValidateAuthoritativeAzureBinding("2026", It.IsAny<string>(), It.IsAny<string>()))
                .Throws(new AuthoritativeBindingException("Simulated Azure endpoint binding mismatch"));

            var guard = new AuthoritativeCutoverGuard(bindingMock.Object);
            using var context = CreateContext();

            var ex = Assert.Throws<AuthoritativeCutoverGuardException>(() =>
                guard.ValidateCutoverState(context, "2026"));

            Assert.Equal(AuthoritativeCutoverErrorCodes.AuthoritativeCutoverStateUnverifiable, ex.ErrorCode);
            Assert.Contains("physical database binding mismatch", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.IsType<AuthoritativeBindingException>(ex.InnerException);
        }

        [Fact]
        public async Task ScenarioF_Async_BindingException_WrappedToAuthoritativeCutoverStateUnverifiable()
        {
            SetServerState("2026", 0);

            var bindingMock = new Mock<IAuthoritativeDatabaseBindingGuard>();
            bindingMock
                .Setup(b => b.ValidateAuthoritativeAzureBinding("2026", It.IsAny<string>(), It.IsAny<string>()))
                .Throws(new AuthoritativeBindingException("Simulated Azure endpoint binding mismatch"));

            var guard = new AuthoritativeCutoverGuard(bindingMock.Object);
            using var context = CreateContext();

            var ex = await Assert.ThrowsAsync<AuthoritativeCutoverGuardException>(async () =>
                await guard.ValidateCutoverStateAsync(context, "2026"));

            Assert.Equal(AuthoritativeCutoverErrorCodes.AuthoritativeCutoverStateUnverifiable, ex.ErrorCode);
            Assert.Contains("physical database binding mismatch", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.IsType<AuthoritativeBindingException>(ex.InnerException);
        }

        #endregion

        #region Scenario G — Connection or Query Failure Wrapped to AUTHORITATIVE_CUTOVER_STATE_UNVERIFIABLE

        [Fact]
        public void ScenarioG_Sync_QueryFailure_WrappedToAuthoritativeCutoverStateUnverifiable()
        {
            _fixture.DropTable();

            var bindingMock = new Mock<IAuthoritativeDatabaseBindingGuard>();
            var guard = new AuthoritativeCutoverGuard(bindingMock.Object);
            using var context = CreateContext();

            var ex = Assert.Throws<AuthoritativeCutoverGuardException>(() =>
                guard.ValidateCutoverState(context, "2026"));

            Assert.Equal(AuthoritativeCutoverErrorCodes.AuthoritativeCutoverStateUnverifiable, ex.ErrorCode);
            Assert.Contains("database query failure", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(ex.InnerException);
        }

        [Fact]
        public async Task ScenarioG_Async_QueryFailure_WrappedToAuthoritativeCutoverStateUnverifiable()
        {
            _fixture.DropTable();

            var bindingMock = new Mock<IAuthoritativeDatabaseBindingGuard>();
            var guard = new AuthoritativeCutoverGuard(bindingMock.Object);
            using var context = CreateContext();

            var ex = await Assert.ThrowsAsync<AuthoritativeCutoverGuardException>(async () =>
                await guard.ValidateCutoverStateAsync(context, "2026"));

            Assert.Equal(AuthoritativeCutoverErrorCodes.AuthoritativeCutoverStateUnverifiable, ex.ErrorCode);
            Assert.Contains("database query failure", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(ex.InnerException);
        }

        #endregion
    }
}
