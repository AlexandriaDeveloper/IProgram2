using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Auth.Infrastructure;
using Auth.Infrastructure.Services;
using Auth.Infrastructure.Sync;
using Core.Exceptions;
using Core.Interfaces;
using Core.Models;
using Core.Models.Sync;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;

namespace Auth.UnitTests
{
    public class LocalBootstrapFoundationTests
    {
        [Fact]
        public void Canonical2026_MapsToCorrect_LocalAndRemoteNames()
        {
            var local = LocalDatabaseBinding.For2026();
            var remote = AzureDatabaseBinding.For2026();

            Assert.Equal("2026", local.CanonicalDatabaseId);
            Assert.Equal("IProgramLocalDb2026", local.ExpectedDatabaseName);

            Assert.Equal("2026", remote.CanonicalDatabaseId);
            Assert.Equal("IProgramDb2026", remote.ExpectedDatabaseName);

            Assert.Equal("IProgramLocalDb2026", DatabaseBindingValidator.GetExpectedLocalDatabaseName("2026"));
            Assert.Equal("IProgramDb2026", DatabaseBindingValidator.GetExpectedRemoteDatabaseName("2026"));
        }

        [Fact]
        public void Canonical2027_MapsToCorrect_LocalAndRemoteNames()
        {
            var local = LocalDatabaseBinding.For2027();
            var remote = AzureDatabaseBinding.For2027();

            Assert.Equal("2027", local.CanonicalDatabaseId);
            Assert.Equal("IProgramLocalDb2027", local.ExpectedDatabaseName);

            Assert.Equal("2027", remote.CanonicalDatabaseId);
            Assert.Equal("IProgramDb2027", remote.ExpectedDatabaseName);

            Assert.Equal("IProgramLocalDb2027", DatabaseBindingValidator.GetExpectedLocalDatabaseName("2027"));
            Assert.Equal("IProgramDb2027", DatabaseBindingValidator.GetExpectedRemoteDatabaseName("2027"));
        }

        [Theory]
        [InlineData("2026", "IProgramLocalDb2027")]
        [InlineData("2027", "IProgramLocalDb2026")]
        public void CrossYearBindings_AreStrictlyRejected_Local(string canonicalId, string mismatchName)
        {
            Assert.Throws<PhysicalDatabaseMismatchException>(() => new LocalDatabaseBinding(canonicalId, mismatchName));
        }

        [Theory]
        [InlineData("2026", "IProgramDb2027")]
        [InlineData("2027", "IProgramDb2026")]
        public void CrossYearBindings_AreStrictlyRejected_Remote(string canonicalId, string mismatchName)
        {
            Assert.Throws<InvalidOperationException>(() => new AzureDatabaseBinding(canonicalId, mismatchName));
        }

        [Theory]
        [InlineData("2026", "IProgramDb2026")]
        [InlineData("2027", "IProgramDb2027")]
        public void LocalBinding_RejectsRemoteAzureNames(string canonicalId, string remoteName)
        {
            Assert.Throws<PhysicalDatabaseMismatchException>(() => new LocalDatabaseBinding(canonicalId, remoteName));
        }

        [Theory]
        [InlineData("2026", "IProgramLocalDb2026")]
        [InlineData("2027", "IProgramLocalDb2027")]
        public void RemoteBinding_RejectsLocalNames(string canonicalId, string localName)
        {
            Assert.Throws<PhysicalDatabaseMismatchException>(() => new AzureDatabaseBinding(canonicalId, localName));
        }

        [Theory]
        [InlineData("2025")]
        [InlineData("2028")]
        [InlineData("unknown")]
        [InlineData("")]
        public void UnknownDatabaseId_FailsClosed(string unknownId)
        {
            Assert.ThrowsAny<Exception>(() => LocalDatabaseBinding.For(unknownId));
            Assert.ThrowsAny<Exception>(() => AzureDatabaseBinding.For(unknownId));
            Assert.ThrowsAny<Exception>(() => DatabaseBindingValidator.GetExpectedLocalDatabaseName(unknownId));
            Assert.ThrowsAny<Exception>(() => DatabaseBindingValidator.GetExpectedRemoteDatabaseName(unknownId));
        }

        [Fact]
        public void LocalFirstDisabled_PreservesCurrentRemoteRouting()
        {
            // Default configuration: LocalFirst:Enabled is false
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    { "ConnectionStrings:DefaultConnection", "Server=localhost;Database=IProgramDb2026;Trusted_Connection=True;" },
                    { "ConnectionStrings:CON2027", "Server=localhost;Database=IProgramDb2027;Trusted_Connection=True;" },
                    { "ConnectionStrings:LocalConnection2026", "Server=localhost;Database=IProgramLocalDb2026;Trusted_Connection=True;" },
                    { "ConnectionStrings:LocalConnection2027", "Server=localhost;Database=IProgramLocalDb2027;Trusted_Connection=True;" },
                    { "DatabaseSettings:Databases:0:Id", "2026" },
                    { "DatabaseSettings:Databases:0:ConnectionStringName", "DefaultConnection" },
                    { "DatabaseSettings:Databases:1:Id", "2027" },
                    { "DatabaseSettings:Databases:1:ConnectionStringName", "CON2027" },
                    { "LocalFirst:Enabled", "false" }
                })
                .Build();

            var httpContext = new DefaultHttpContext();
            var httpContextAccessor = new HttpContextAccessor { HttpContext = httpContext };
            var provider = new DbConnectionProvider(httpContextAccessor, config);

            Assert.False(provider.IsLocalFirstEnabled);

            // Default route (2026)
            var conn2026 = provider.GetConnectionString();
            Assert.Contains("Database=IProgramDb2026", conn2026);

            // Explicit 2027 selection via header
            httpContext.Request.Headers["X-Db-Selection"] = "2027";
            httpContext.Items.Clear(); // clear per-request cache
            var conn2027 = provider.GetConnectionString();
            Assert.Contains("Database=IProgramDb2027", conn2027);

            // Explicit remote methods
            Assert.Equal("Server=localhost;Database=IProgramDb2026;Trusted_Connection=True;", provider.GetRemoteConnectionString("2026"));
            Assert.Equal("Server=localhost;Database=IProgramDb2027;Trusted_Connection=True;", provider.GetRemoteConnectionString("2027"));
        }

        [Fact]
        public void LocalFirstEnabled_SelectsLocalOperationalTarget()
        {
            // LocalFirst:Enabled is true
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    { "ConnectionStrings:DefaultConnection", "Server=localhost;Database=IProgramDb2026;Trusted_Connection=True;" },
                    { "ConnectionStrings:CON2027", "Server=localhost;Database=IProgramDb2027;Trusted_Connection=True;" },
                    { "ConnectionStrings:LocalConnection2026", "Server=localhost;Database=IProgramLocalDb2026;Trusted_Connection=True;" },
                    { "ConnectionStrings:LocalConnection2027", "Server=localhost;Database=IProgramLocalDb2027;Trusted_Connection=True;" },
                    { "DatabaseSettings:Databases:0:Id", "2026" },
                    { "DatabaseSettings:Databases:0:ConnectionStringName", "DefaultConnection" },
                    { "DatabaseSettings:Databases:1:Id", "2027" },
                    { "DatabaseSettings:Databases:1:ConnectionStringName", "CON2027" },
                    { "LocalFirst:Enabled", "true" },
                    { "LocalFirst:SqlServerInstance", "localhost" }
                })
                .Build();

            var httpContext = new DefaultHttpContext();
            var httpContextAccessor = new HttpContextAccessor { HttpContext = httpContext };
            var provider = new DbConnectionProvider(httpContextAccessor, config);

            Assert.True(provider.IsLocalFirstEnabled);

            // Default route (2026) routes to LOCAL database
            var conn2026 = provider.GetConnectionString();
            Assert.Contains("Database=IProgramLocalDb2026", conn2026);
            Assert.Contains("Server=localhost", conn2026);

            // Explicit 2027 selection routes to LOCAL 2027 database
            httpContext.Request.Headers["X-Db-Selection"] = "2027";
            httpContext.Items.Clear();
            var conn2027 = provider.GetConnectionString();
            Assert.Contains("Database=IProgramLocalDb2027", conn2027);

            // Explicit remote methods remain available for sync scopes
            Assert.Contains("Database=IProgramDb2026", provider.GetRemoteConnectionString("2026"));
            Assert.Contains("Database=IProgramDb2027", provider.GetRemoteConnectionString("2027"));
        }

        [Fact]
        public void LocalSyncContext_RejectsConnectionToAzureProductionDatabases()
        {
            // Attempting to configure LocalSyncContext pointing to remote Azure DB name must throw InvalidOperationException
            var options2026 = new DbContextOptionsBuilder<LocalSyncContext>()
                .UseSqlServer("Server=localhost;Database=IProgramDb2026;Trusted_Connection=True;TrustServerCertificate=True")
                .Options;

            var ex2026 = Assert.Throws<InvalidOperationException>(() => new LocalSyncContext(options2026));
            Assert.Contains("Security violation: LocalSyncContext cannot target remote Azure production database", ex2026.Message);

            var options2027 = new DbContextOptionsBuilder<LocalSyncContext>()
                .UseSqlServer("Server=localhost;Database=IProgramDb2027;Trusted_Connection=True;TrustServerCertificate=True")
                .Options;

            var ex2027 = Assert.Throws<InvalidOperationException>(() => new LocalSyncContext(options2027));
            Assert.Contains("Security violation: LocalSyncContext cannot target remote Azure production database", ex2027.Message);
        }

        [Fact]
        public void LocalSyncContext_AcceptsLocalDatabaseConnection()
        {
            var optionsLocal = new DbContextOptionsBuilder<LocalSyncContext>()
                .UseSqlServer("Server=localhost;Database=IProgramLocalDb2026;Trusted_Connection=True;TrustServerCertificate=True")
                .Options;

            using var localContext = new LocalSyncContext(optionsLocal);
            Assert.NotNull(localContext);
        }

        [Fact]
        public void BootstrapWriteGate_BlocksWrite_WhenManifestMissing()
        {
            var options = new DbContextOptionsBuilder<LocalSyncContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            using var context = new LocalSyncContext(options);
            var gate = new LocalBootstrapWriteGate(context);

            var result = gate.EvaluateReadiness("2026");
            Assert.Equal(BootstrapReadinessStatus.ManifestMissing, result.Status);
            Assert.False(result.IsWriteAllowed);
            Assert.False(gate.IsWriteAllowed("2026"));

            var ex = Assert.Throws<BootstrapNotVerifiedException>(() => gate.EnsureWriteAllowed("2026"));
            Assert.Equal("2026", ex.DatabaseId);
            Assert.Contains("manifest is missing", ex.Message);
        }

        [Fact]
        public void BootstrapWriteGate_BlocksWrite_WhenManifestNotVerified()
        {
            var options = new DbContextOptionsBuilder<LocalSyncContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            using var context = new LocalSyncContext(options);
            context.BootstrapManifests.Add(new LocalBootstrapManifest
            {
                DatabaseId = "2026",
                Status = "FAILED_MISMATCH",
                IsWriteAllowed = false,
                BootstrapTimestampUtc = DateTime.UtcNow
            });
            context.SaveChanges();

            var gate = new LocalBootstrapWriteGate(context);

            var result = gate.EvaluateReadiness("2026");
            Assert.Equal(BootstrapReadinessStatus.Unverified, result.Status);
            Assert.False(result.IsWriteAllowed);
            Assert.False(gate.IsWriteAllowed("2026"));

            var ex = Assert.Throws<BootstrapNotVerifiedException>(() => gate.EnsureWriteAllowed("2026"));
            Assert.Equal("2026", ex.DatabaseId);
            Assert.Contains("FAILED_MISMATCH", ex.Message);
        }

        [Fact]
        public void BootstrapWriteGate_AllowsWrite_WhenManifestVerifiedReady()
        {
            var options = new DbContextOptionsBuilder<LocalSyncContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            using var context = new LocalSyncContext(options);
            context.BootstrapManifests.Add(new LocalBootstrapManifest
            {
                DatabaseId = "2026",
                Status = "VERIFIED_READY",
                IsWriteAllowed = true,
                BootstrapTimestampUtc = DateTime.UtcNow
            });
            context.SaveChanges();

            var gate = new LocalBootstrapWriteGate(context);

            var result = gate.EvaluateReadiness("2026");
            Assert.Equal(BootstrapReadinessStatus.VerifiedReady, result.Status);
            Assert.True(result.IsWriteAllowed);
            Assert.True(gate.IsWriteAllowed("2026"));

            // EnsureWriteAllowed succeeds without throwing
            gate.EnsureWriteAllowed("2026");
        }

        [Fact]
        public void ReadinessDiagnostic_DoesNotEmitSecrets()
        {
            var options = new DbContextOptionsBuilder<LocalSyncContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            using var context = new LocalSyncContext(options);
            context.BootstrapManifests.Add(new LocalBootstrapManifest
            {
                DatabaseId = "2026",
                Status = "VERIFIED_READY",
                IsWriteAllowed = true,
                AzureServerSource = "iprogram-sql-prod-01.database.windows.net",
                TargetLocalEngine = "localhost",
                BootstrapTimestampUtc = DateTime.UtcNow
            });
            context.SaveChanges();

            var gate = new LocalBootstrapWriteGate(context);
            var result = gate.EvaluateReadiness("2026");

            // Diagnostic outputs must not contain sensitive tokens
            Assert.DoesNotContain("password", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("User ID", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("AccountKey", result.Message, StringComparison.OrdinalIgnoreCase);
        }

        // --- NEW TESTS FOR PR #17 REVIEW HARDENING ---

        [Fact]
        public void LocalSyncContextFactory_CreatesExplicitYearBoundContext_WithoutAmbientHttpContext()
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    { "ConnectionStrings:LocalConnection2026", "Server=localhost;Database=IProgramLocalDb2026;Trusted_Connection=True;" },
                    { "ConnectionStrings:LocalConnection2027", "Server=localhost;Database=IProgramLocalDb2027;Trusted_Connection=True;" },
                    { "DatabaseSettings:Databases:0:Id", "2026" },
                    { "DatabaseSettings:Databases:0:ConnectionStringName", "LocalConnection2026" }
                })
                .Build();

            // Null HttpContextAccessor: proves factory does not depend on ambient HttpContext
            var httpContextAccessor = new HttpContextAccessor { HttpContext = null };
            var provider = new DbConnectionProvider(httpContextAccessor, config);
            var factory = new LocalSyncContextFactory(provider);

            using var ctx2026 = factory.Create("2026");
            Assert.NotNull(ctx2026);
            Assert.Equal("IProgramLocalDb2026", ctx2026.Database.GetDbConnection().Database);

            using var ctx2027 = factory.Create("2027");
            Assert.NotNull(ctx2027);
            Assert.Equal("IProgramLocalDb2027", ctx2027.Database.GetDbConnection().Database);
        }

        [Theory]
        [InlineData("")]
        [InlineData(" ")]
        [InlineData("2028")]
        [InlineData("unknown")]
        public void LocalSyncContextFactory_RejectsInvalidYear_FailsClosed(string invalidYear)
        {
            var config = new ConfigurationBuilder().Build();
            var provider = new DbConnectionProvider(new HttpContextAccessor(), config);
            var factory = new LocalSyncContextFactory(provider);

            Assert.Throws<InvalidDatabaseSelectionException>(() => factory.Create(invalidYear));
        }

        [Fact]
        public void LocalBootstrapWriteGate_UsesExplicitFactory_IsolationBetweenYears()
        {
            // Context for 2026 has verified manifest, context for 2027 has no manifest
            var mockFactory = new Mock<ILocalSyncContextFactory>();

            var options2026 = new DbContextOptionsBuilder<LocalSyncContext>()
                .UseInMemoryDatabase("isolation_test_2026")
                .Options;
            var ctx2026 = new LocalSyncContext(options2026);
            ctx2026.BootstrapManifests.Add(new LocalBootstrapManifest
            {
                DatabaseId = "2026",
                Status = "VERIFIED_READY",
                IsWriteAllowed = true
            });
            ctx2026.SaveChanges();

            var options2027 = new DbContextOptionsBuilder<LocalSyncContext>()
                .UseInMemoryDatabase("isolation_test_2027")
                .Options;
            var ctx2027 = new LocalSyncContext(options2027);
            // 2027 has no manifest

            mockFactory.Setup(f => f.Create("2026")).Returns(new LocalSyncContext(options2026));
            mockFactory.Setup(f => f.Create("2027")).Returns(new LocalSyncContext(options2027));

            var gate = new LocalBootstrapWriteGate(mockFactory.Object);

            // 2026 is allowed
            Assert.True(gate.IsWriteAllowed("2026"));

            // 2027 is blocked because it has its own isolated context and cannot see 2026 manifest
            Assert.False(gate.IsWriteAllowed("2027"));
        }

        [Fact]
        public void ValidateConnectionString_MalformedString_ThrowsDatabaseConfigurationException_WithoutSecrets()
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    { "ConnectionStrings:BadConn", "Server=localhost;Database=IProgramLocalDb2026;Password=SuperSecretPassword123!;InvalidToken==" },
                    { "DatabaseSettings:Databases:0:Id", "2026" },
                    { "DatabaseSettings:Databases:0:ConnectionStringName", "BadConn" },
                    { "LocalFirst:Enabled", "true" }
                })
                .Build();

            var provider = new DbConnectionProvider(new HttpContextAccessor(), config);

            var ex = Assert.Throws<DatabaseConfigurationException>(() => provider.GetLocalConnectionString("2026"));

            // Must NOT contain the secret password
            Assert.DoesNotContain("SuperSecretPassword123!", ex.Message);
            Assert.Contains("Malformed connection string", ex.Message);
        }

        [Fact]
        public void ValidateConnectionString_MissingInitialCatalog_ThrowsDatabaseConfigurationException()
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    { "ConnectionStrings:NoCatalog", "Server=localhost;Trusted_Connection=True;" },
                    { "DatabaseSettings:Databases:0:Id", "2026" },
                    { "DatabaseSettings:Databases:0:ConnectionStringName", "NoCatalog" },
                    { "LocalFirst:Enabled", "true" }
                })
                .Build();

            var provider = new DbConnectionProvider(new HttpContextAccessor(), config);

            var ex = Assert.Throws<DatabaseConfigurationException>(() => provider.GetLocalConnectionString("2026"));
            Assert.Contains("InitialCatalog / Database is missing or empty", ex.Message);
        }

        [Fact]
        public void ValidateConnectionString_LocalTarget_RejectsRemoteServerEndpoint()
        {
            // Local target pointing to remote host (e.g. azure server) must be rejected
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    { "ConnectionStrings:RemoteHostLocalDb", "Server=iprogram-sql-prod-01.database.windows.net;Database=IProgramLocalDb2026;Trusted_Connection=True;" },
                    { "DatabaseSettings:Databases:0:Id", "2026" },
                    { "DatabaseSettings:Databases:0:ConnectionStringName", "RemoteHostLocalDb" },
                    { "LocalFirst:Enabled", "true" }
                })
                .Build();

            var provider = new DbConnectionProvider(new HttpContextAccessor(), config);

            var ex = Assert.Throws<PhysicalDatabaseMismatchException>(() => provider.GetLocalConnectionString("2026"));
            Assert.Contains("cannot point to non-local server endpoint", ex.Message);
        }

        [Fact]
        public void ValidateConnectionString_LocalTarget_RejectsWrongYearCatalog()
        {
            // 2026 configured pointing to 2027 local database
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    { "ConnectionStrings:LocalWrongYear", "Server=localhost;Database=IProgramLocalDb2027;Trusted_Connection=True;" },
                    { "DatabaseSettings:Databases:0:Id", "2026" },
                    { "DatabaseSettings:Databases:0:ConnectionStringName", "LocalWrongYear" },
                    { "LocalFirst:Enabled", "true" }
                })
                .Build();

            var provider = new DbConnectionProvider(new HttpContextAccessor(), config);

            var ex = Assert.Throws<PhysicalDatabaseMismatchException>(() => provider.GetLocalConnectionString("2026"));
            Assert.Contains("Physical database mismatch", ex.Message);
        }

        [Fact]
        public void ApplicationContext_LocalFirstDisabled_AllowsWrites_SyncAndAsync()
        {
            // LocalFirst is disabled => interceptor allows writes normally
            var mockSyncProvider = new Mock<ISyncConnectionProvider>();
            mockSyncProvider.Setup(p => p.IsLocalFirstEnabled).Returns(false);

            var mockGate = new Mock<ILocalBootstrapWriteGate>();
            var interceptor = new LocalBootstrapWriteGateInterceptor(mockSyncProvider.Object, mockGate.Object);

            var options = new DbContextOptionsBuilder<ApplicationContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .AddInterceptors(interceptor)
                .Options;

            using var context = new ApplicationContext(options);
            context.Departments.Add(new Department { Name = "Dept A", SyncId = Guid.NewGuid() });

            // Sync SaveChanges must succeed
            var saved = context.SaveChanges();
            Assert.Equal(1, saved);

            // Gate must not have been invoked
            mockGate.Verify(g => g.EnsureWriteAllowed(It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task ApplicationContext_LocalFirstDisabled_AllowsWritesAsync()
        {
            var mockSyncProvider = new Mock<ISyncConnectionProvider>();
            mockSyncProvider.Setup(p => p.IsLocalFirstEnabled).Returns(false);

            var mockGate = new Mock<ILocalBootstrapWriteGate>();
            var interceptor = new LocalBootstrapWriteGateInterceptor(mockSyncProvider.Object, mockGate.Object);

            var options = new DbContextOptionsBuilder<ApplicationContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .AddInterceptors(interceptor)
                .Options;

            using var context = new ApplicationContext(options);
            context.Departments.Add(new Department { Name = "Dept Async", SyncId = Guid.NewGuid() });

            var saved = await context.SaveChangesAsync();
            Assert.Equal(1, saved);

            mockGate.Verify(g => g.EnsureWriteAllowed(It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public void ApplicationContext_LocalFirstEnabled_MissingManifest_BlocksWrites_Sync()
        {
            var mockSyncProvider = new Mock<ISyncConnectionProvider>();
            mockSyncProvider.Setup(p => p.IsLocalFirstEnabled).Returns(true);
            mockSyncProvider.Setup(p => p.GetSelectedDatabaseId()).Returns("2026");

            var mockGate = new Mock<ILocalBootstrapWriteGate>();
            mockGate.Setup(g => g.EnsureWriteAllowed("2026"))
                .Throws(new BootstrapNotVerifiedException("2026", "Bootstrap manifest is missing. Local writes are blocked."));

            var interceptor = new LocalBootstrapWriteGateInterceptor(mockSyncProvider.Object, mockGate.Object);

            var options = new DbContextOptionsBuilder<ApplicationContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .AddInterceptors(interceptor)
                .Options;

            using var context = new ApplicationContext(options);
            context.Departments.Add(new Department { Name = "Dept Blocked", SyncId = Guid.NewGuid() });

            var ex = Assert.Throws<BootstrapNotVerifiedException>(() => context.SaveChanges());
            Assert.Equal("2026", ex.DatabaseId);
            Assert.Contains("manifest is missing", ex.Message);
        }

        [Fact]
        public async Task ApplicationContext_LocalFirstEnabled_MissingManifest_BlocksWrites_Async()
        {
            var mockSyncProvider = new Mock<ISyncConnectionProvider>();
            mockSyncProvider.Setup(p => p.IsLocalFirstEnabled).Returns(true);
            mockSyncProvider.Setup(p => p.GetSelectedDatabaseId()).Returns("2026");

            var mockGate = new Mock<ILocalBootstrapWriteGate>();
            mockGate.Setup(g => g.EnsureWriteAllowed("2026"))
                .Throws(new BootstrapNotVerifiedException("2026", "Bootstrap manifest is missing. Local writes are blocked."));

            var interceptor = new LocalBootstrapWriteGateInterceptor(mockSyncProvider.Object, mockGate.Object);

            var options = new DbContextOptionsBuilder<ApplicationContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .AddInterceptors(interceptor)
                .Options;

            using var context = new ApplicationContext(options);
            context.Departments.Add(new Department { Name = "Dept Blocked Async", SyncId = Guid.NewGuid() });

            var ex = await Assert.ThrowsAsync<BootstrapNotVerifiedException>(() => context.SaveChangesAsync());
            Assert.Equal("2026", ex.DatabaseId);
            Assert.Contains("manifest is missing", ex.Message);
        }

        [Fact]
        public void ApplicationContext_LocalFirstEnabled_VerifiedManifest_AllowsWrites_SyncAndAsync()
        {
            var mockSyncProvider = new Mock<ISyncConnectionProvider>();
            mockSyncProvider.Setup(p => p.IsLocalFirstEnabled).Returns(true);
            mockSyncProvider.Setup(p => p.GetSelectedDatabaseId()).Returns("2026");

            var mockGate = new Mock<ILocalBootstrapWriteGate>();
            mockGate.Setup(g => g.EnsureWriteAllowed("2026")); // Does not throw

            var interceptor = new LocalBootstrapWriteGateInterceptor(mockSyncProvider.Object, mockGate.Object);

            var options = new DbContextOptionsBuilder<ApplicationContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .AddInterceptors(interceptor)
                .Options;

            using var context = new ApplicationContext(options);
            context.Departments.Add(new Department { Name = "Dept Allowed", SyncId = Guid.NewGuid() });

            var saved = context.SaveChanges();
            Assert.Equal(1, saved);
            mockGate.Verify(g => g.EnsureWriteAllowed("2026"), Times.Once);
        }
    }
}
