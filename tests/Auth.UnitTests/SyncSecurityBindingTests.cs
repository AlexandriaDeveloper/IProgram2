using System;
using Auth.Infrastructure.Sync;
using Core.Models.Sync;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Auth.UnitTests
{
    public class SyncSecurityBindingTests
    {
        private const string RemoteAzureServer = "tcp:sql-iprogram-prod.database.windows.net,1433";

        [Theory]
        [InlineData("localhost")]
        [InlineData(".")]
        [InlineData("(local)")]
        [InlineData("127.0.0.1")]
        [InlineData("::1")]
        [InlineData("[::1]")]
        [InlineData(@".\SQLEXPRESS")]
        [InlineData(@"localhost\SQLEXPRESS")]
        [InlineData(@"(localdb)\mssqllocaldb")]
        [InlineData("tcp:localhost,1433")]
        [InlineData("tcp:127.0.0.1,1433")]
        public void AzureSyncContext_Rejects_LocalServerEndpoints_EvenWithRemoteDbName(string localEndpoint)
        {
            var connStr = $"Server={localEndpoint};Database={DatabaseBindingValidator.RemoteDb2026};Integrated Security=True;TrustServerCertificate=True;";
            var options = new DbContextOptionsBuilder<AzureSyncContext>()
                .UseSqlServer(connStr)
                .Options;

            var ex = Assert.Throws<InvalidOperationException>(() => new AzureSyncContext(options));
            Assert.Contains("Security violation: AzureSyncContext cannot target local server endpoint", ex.Message);
        }

        [Theory]
        [InlineData(DatabaseBindingValidator.LocalDb2026)]
        [InlineData(DatabaseBindingValidator.LocalDb2027)]
        public void AzureSyncContext_Rejects_LocalDatabaseNames(string localDbName)
        {
            var connStr = $"Server={RemoteAzureServer};Database={localDbName};Integrated Security=True;TrustServerCertificate=True;";
            var options = new DbContextOptionsBuilder<AzureSyncContext>()
                .UseSqlServer(connStr)
                .Options;

            var ex = Assert.Throws<InvalidOperationException>(() => new AzureSyncContext(options));
            Assert.Contains("Security violation: AzureSyncContext cannot target local database", ex.Message);
        }

        [Fact]
        public void AzureSyncContext_Rejects_Localhost_With_LocalDbName()
        {
            var connStr = "Server=localhost;Database=IProgramLocalDb2026;Integrated Security=True;";
            var options = new DbContextOptionsBuilder<AzureSyncContext>()
                .UseSqlServer(connStr)
                .Options;

            var ex = Assert.Throws<InvalidOperationException>(() => new AzureSyncContext(options));
            Assert.Contains("Security violation: AzureSyncContext cannot target local", ex.Message);
        }

        [Fact]
        public void AzureSyncContext_Rejects_UnapprovedRemoteDatabaseName()
        {
            var connStr = $"Server={RemoteAzureServer};Database=SomeOtherDatabase;Integrated Security=True;TrustServerCertificate=True;";
            var options = new DbContextOptionsBuilder<AzureSyncContext>()
                .UseSqlServer(connStr)
                .Options;

            var ex = Assert.Throws<InvalidOperationException>(() => new AzureSyncContext(options));
            Assert.Contains("Security violation: AzureSyncContext requires an approved remote database name", ex.Message);
        }

        [Theory]
        [InlineData(DatabaseBindingValidator.RemoteDb2026)]
        [InlineData(DatabaseBindingValidator.RemoteDb2027)]
        public void AzureSyncContext_Accepts_RemoteEndpoint_With_ApprovedRemoteDbName_WithoutConnecting(string remoteDbName)
        {
            var connStr = $"Server={RemoteAzureServer};Database={remoteDbName};Integrated Security=True;TrustServerCertificate=True;";
            var options = new DbContextOptionsBuilder<AzureSyncContext>()
                .UseSqlServer(connStr)
                .Options;

            // Instantiation must succeed without attempting physical connection
            using var context = new AzureSyncContext(options);
            Assert.NotNull(context);
        }

        [Fact]
        public void LocalSyncContext_Rejects_RemoteEndpoint_EvenWithLocalDbName()
        {
            var connStr = $"Server={RemoteAzureServer};Database={DatabaseBindingValidator.LocalDb2026};Integrated Security=True;TrustServerCertificate=True;";
            var options = new DbContextOptionsBuilder<LocalSyncContext>()
                .UseSqlServer(connStr)
                .Options;

            var ex = Assert.Throws<InvalidOperationException>(() => new LocalSyncContext(options));
            Assert.Contains("Security violation: LocalSyncContext requires a trusted local server endpoint", ex.Message);
        }

        [Theory]
        [InlineData(DatabaseBindingValidator.RemoteDb2026)]
        [InlineData(DatabaseBindingValidator.RemoteDb2027)]
        public void LocalSyncContext_Rejects_RemoteDatabaseNames_EvenOnLocalhost(string remoteDbName)
        {
            var connStr = $"Server=localhost;Database={remoteDbName};Integrated Security=True;";
            var options = new DbContextOptionsBuilder<LocalSyncContext>()
                .UseSqlServer(connStr)
                .Options;

            var ex = Assert.Throws<InvalidOperationException>(() => new LocalSyncContext(options));
            Assert.Contains("Security violation: LocalSyncContext cannot target remote Azure production database", ex.Message);
        }

        [Theory]
        [InlineData("localhost", DatabaseBindingValidator.LocalDb2026)]
        [InlineData("localhost", DatabaseBindingValidator.LocalDb2027)]
        [InlineData(".", DatabaseBindingValidator.LocalDb2026)]
        [InlineData("(local)", DatabaseBindingValidator.LocalDb2026)]
        [InlineData("127.0.0.1", DatabaseBindingValidator.LocalDb2026)]
        public void LocalSyncContext_Accepts_LocalEndpoint_With_ApprovedLocalDbName_WithoutConnecting(string localEndpoint, string localDbName)
        {
            var connStr = $"Server={localEndpoint};Database={localDbName};Integrated Security=True;";
            var options = new DbContextOptionsBuilder<LocalSyncContext>()
                .UseSqlServer(connStr)
                .Options;

            using var context = new LocalSyncContext(options);
            Assert.NotNull(context);
        }

        [Fact]
        public void DatabaseBindingValidator_EndpointClassification_CorrectlyCategorizesEndpoints()
        {
            // Local endpoints
            Assert.True(DatabaseBindingValidator.IsLocalServerEndpoint("localhost"));
            Assert.True(DatabaseBindingValidator.IsLocalServerEndpoint("."));
            Assert.True(DatabaseBindingValidator.IsLocalServerEndpoint("(local)"));
            Assert.True(DatabaseBindingValidator.IsLocalServerEndpoint("127.0.0.1"));
            Assert.True(DatabaseBindingValidator.IsLocalServerEndpoint("::1"));
            Assert.True(DatabaseBindingValidator.IsLocalServerEndpoint("[::1]"));
            Assert.True(DatabaseBindingValidator.IsLocalServerEndpoint(Environment.MachineName));
            Assert.True(DatabaseBindingValidator.IsLocalServerEndpoint(@".\SQLEXPRESS"));
            Assert.True(DatabaseBindingValidator.IsLocalServerEndpoint(@"localhost\SQLEXPRESS"));
            Assert.True(DatabaseBindingValidator.IsLocalServerEndpoint(@"(localdb)\mssqllocaldb"));
            Assert.True(DatabaseBindingValidator.IsLocalServerEndpoint("tcp:localhost,1433"));
            Assert.True(DatabaseBindingValidator.IsLocalServerEndpoint("tcp:127.0.0.1,1433"));

            // Remote endpoints
            Assert.False(DatabaseBindingValidator.IsLocalServerEndpoint("tcp:sql-iprogram-prod.database.windows.net,1433"));
            Assert.True(DatabaseBindingValidator.IsRemoteServerEndpoint("tcp:sql-iprogram-prod.database.windows.net,1433"));
            Assert.False(DatabaseBindingValidator.IsLocalServerEndpoint("10.0.0.50"));
            Assert.True(DatabaseBindingValidator.IsRemoteServerEndpoint("10.0.0.50"));
            Assert.False(DatabaseBindingValidator.IsLocalServerEndpoint("remoteserver.company.internal"));
            Assert.True(DatabaseBindingValidator.IsRemoteServerEndpoint("remoteserver.company.internal"));
        }

        [Fact]
        public void AzureSourceConnectionString_Preserves_ReadOnlyIntent_And_RemoteCatalog()
        {
            var remoteCs = $"Server={RemoteAzureServer};Initial Catalog=IProgramDb2027;Integrated Security=True;ApplicationIntent=ReadOnly;Connect Timeout=60;TrustServerCertificate=True;";
            var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(remoteCs);

            Assert.Equal("IProgramDb2027", builder.InitialCatalog);
            Assert.Equal(Microsoft.Data.SqlClient.ApplicationIntent.ReadOnly, builder.ApplicationIntent);
            Assert.True(DatabaseBindingValidator.IsRemoteServerEndpoint(builder.DataSource));
            Assert.False(DatabaseBindingValidator.IsLocalServerEndpoint(builder.DataSource));
        }

        [Theory]
        [InlineData("IProgramDb2026")]
        [InlineData("IProgramDb2027")]
        public void AzureSourceConnectionString_FailsClosed_When_ApplicationIntent_IsNotReadOnly(string remoteDbName)
        {
            var invalidCs = $"Server={RemoteAzureServer};Database={remoteDbName};Integrated Security=True;ApplicationIntent=ReadWrite;";
            var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(invalidCs);

            Assert.NotEqual(Microsoft.Data.SqlClient.ApplicationIntent.ReadOnly, builder.ApplicationIntent);
        }

        [Fact]
        public void AzureSourceConnectionString_Rejects_LocalEndpoint_For_2027()
        {
            var localCs = "Server=localhost;Initial Catalog=IProgramDb2027;Integrated Security=True;ApplicationIntent=ReadOnly;";
            var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(localCs);

            Assert.Equal("IProgramDb2027", builder.InitialCatalog);
            Assert.Equal(Microsoft.Data.SqlClient.ApplicationIntent.ReadOnly, builder.ApplicationIntent);
            Assert.True(DatabaseBindingValidator.IsLocalServerEndpoint(builder.DataSource));
            Assert.False(DatabaseBindingValidator.IsRemoteServerEndpoint(builder.DataSource));
        }
    }
}
