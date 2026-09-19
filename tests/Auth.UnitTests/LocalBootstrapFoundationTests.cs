using System;
using System.Collections.Generic;
using Auth.Infrastructure.Services;
using Auth.Infrastructure.Sync;
using Core.Exceptions;
using Core.Interfaces;
using Core.Models.Sync;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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
                    { "ConnectionStrings:LocalConnection2026", "Server=localhost\\SQLEXPRESS;Database=IProgramLocalDb2026;Trusted_Connection=True;" },
                    { "ConnectionStrings:LocalConnection2027", "Server=localhost\\SQLEXPRESS;Database=IProgramLocalDb2027;Trusted_Connection=True;" },
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
                    { "ConnectionStrings:LocalConnection2026", "Server=localhost\\SQLEXPRESS;Database=IProgramLocalDb2026;Trusted_Connection=True;" },
                    { "ConnectionStrings:LocalConnection2027", "Server=localhost\\SQLEXPRESS;Database=IProgramLocalDb2027;Trusted_Connection=True;" },
                    { "DatabaseSettings:Databases:0:Id", "2026" },
                    { "DatabaseSettings:Databases:0:ConnectionStringName", "DefaultConnection" },
                    { "DatabaseSettings:Databases:1:Id", "2027" },
                    { "DatabaseSettings:Databases:1:ConnectionStringName", "CON2027" },
                    { "LocalFirst:Enabled", "true" },
                    { "LocalFirst:SqlServerInstance", "localhost\\SQLEXPRESS" }
                })
                .Build();

            var httpContext = new DefaultHttpContext();
            var httpContextAccessor = new HttpContextAccessor { HttpContext = httpContext };
            var provider = new DbConnectionProvider(httpContextAccessor, config);

            Assert.True(provider.IsLocalFirstEnabled);

            // Default route (2026) routes to LOCAL database
            var conn2026 = provider.GetConnectionString();
            Assert.Contains("Database=IProgramLocalDb2026", conn2026);
            Assert.Contains("localhost\\SQLEXPRESS", conn2026);

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
                .UseSqlServer("Server=localhost\\SQLEXPRESS;Database=IProgramLocalDb2026;Trusted_Connection=True;TrustServerCertificate=True")
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
                TargetLocalEngine = "SQLEXPRESS",
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
    }
}
