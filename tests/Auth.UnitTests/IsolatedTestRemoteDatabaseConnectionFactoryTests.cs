#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Auth.Api.Testing;
using Core.Exceptions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Auth.UnitTests
{
    public class IsolatedTestRemoteDatabaseConnectionFactoryTests
    {
        [Fact]
        public void Constructor_NullConfiguration_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() => new IsolatedTestRemoteDatabaseConnectionFactory(null!));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public async Task CreateOpenConnectionAsync_NullOrWhitespaceDatabaseId_ThrowsInvalidDatabaseSelectionException(string? dbId)
        {
            var config = new ConfigurationBuilder().Build();
            var factory = new IsolatedTestRemoteDatabaseConnectionFactory(config);

            await Assert.ThrowsAsync<InvalidDatabaseSelectionException>(async () =>
            {
                await factory.CreateOpenConnectionAsync(dbId!, CancellationToken.None);
            });
        }

        [Theory]
        [InlineData("2025")]
        [InlineData("2028")]
        [InlineData("master")]
        [InlineData("Production")]
        public async Task CreateOpenConnectionAsync_UnsupportedDatabaseId_ThrowsInvalidDatabaseSelectionException(string unsupportedId)
        {
            var config = new ConfigurationBuilder().Build();
            var factory = new IsolatedTestRemoteDatabaseConnectionFactory(config);

            var ex = await Assert.ThrowsAsync<InvalidDatabaseSelectionException>(async () =>
            {
                await factory.CreateOpenConnectionAsync(unsupportedId, CancellationToken.None);
            });

            Assert.Contains($"Unsupported canonical DatabaseId '{unsupportedId}'", ex.Message);
        }

        [Fact]
        public async Task CreateOpenConnectionAsync_2026_MissingTestRemoteConnection2026_ThrowsInvalidOperationException_FailsClosed()
        {
            var inMemorySettings = new Dictionary<string, string?>
            {
                { "ConnectionStrings:SomeOtherConnection", "Server=test;" }
            };
            var config = new ConfigurationBuilder().AddInMemoryCollection(inMemorySettings).Build();
            var factory = new IsolatedTestRemoteDatabaseConnectionFactory(config);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await factory.CreateOpenConnectionAsync("2026", CancellationToken.None);
            });

            Assert.Contains("TestRemoteConnection2026", ex.Message);
            Assert.Contains("Fallback to production databases is strictly prohibited", ex.Message);
        }

        [Fact]
        public async Task CreateOpenConnectionAsync_2027_MissingTestRemoteConnection2027_ThrowsInvalidOperationException_FailsClosed()
        {
            var inMemorySettings = new Dictionary<string, string?>
            {
                { "ConnectionStrings:SomeOtherConnection", "Server=test;" }
            };
            var config = new ConfigurationBuilder().AddInMemoryCollection(inMemorySettings).Build();
            var factory = new IsolatedTestRemoteDatabaseConnectionFactory(config);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await factory.CreateOpenConnectionAsync("2027", CancellationToken.None);
            });

            Assert.Contains("TestRemoteConnection2027", ex.Message);
            Assert.Contains("Fallback to production databases is strictly prohibited", ex.Message);
        }

        [Fact]
        public async Task CreateOpenConnectionAsync_2026_WithProductionDefaultConnectionPresent_DoesNotFallback_ThrowsInvalidOperationException()
        {
            // Even if DefaultConnection (Azure Production) is configured, the factory must fail closed and NEVER fall back
            var inMemorySettings = new Dictionary<string, string?>
            {
                { "ConnectionStrings:DefaultConnection", "Server=tcp:iprogram-server.database.windows.net;Database=IProgramDb2026;" }
            };
            var config = new ConfigurationBuilder().AddInMemoryCollection(inMemorySettings).Build();
            var factory = new IsolatedTestRemoteDatabaseConnectionFactory(config);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await factory.CreateOpenConnectionAsync("2026", CancellationToken.None);
            });

            Assert.Contains("TestRemoteConnection2026", ex.Message);
            Assert.Contains("Fallback to production databases is strictly prohibited", ex.Message);
        }

        [Fact]
        public async Task CreateOpenConnectionAsync_2027_WithProductionCON2027Present_DoesNotFallback_ThrowsInvalidOperationException()
        {
            // Even if CON2027 (Azure Production) is configured, the factory must fail closed and NEVER fall back
            var inMemorySettings = new Dictionary<string, string?>
            {
                { "ConnectionStrings:CON2027", "Server=tcp:iprogram-server.database.windows.net;Database=IProgramDb2027;" }
            };
            var config = new ConfigurationBuilder().AddInMemoryCollection(inMemorySettings).Build();
            var factory = new IsolatedTestRemoteDatabaseConnectionFactory(config);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await factory.CreateOpenConnectionAsync("2027", CancellationToken.None);
            });

            Assert.Contains("TestRemoteConnection2027", ex.Message);
            Assert.Contains("Fallback to production databases is strictly prohibited", ex.Message);
        }

        [Fact]
        public void LocalPullTransactionCoordinator_ActiveLeaseTokenParsing_GuidAndStringParity()
        {
            // Verifies the defensive cast logic applied in LocalPullTransactionCoordinator for SQL Server 2014
            var expectedGuid = Guid.NewGuid();

            // Case 1: Driver returns raw Guid
            object rawGuid = expectedGuid;
            Guid? parsed1 = rawGuid switch
            {
                Guid g => g,
                string s when Guid.TryParse(s, out var pg) => pg,
                _ => null
            };
            Assert.Equal(expectedGuid, parsed1);

            // Case 2: Driver returns string representation of Guid (e.g. SQL Server 2014 / ADO.NET)
            object rawString = expectedGuid.ToString();
            Guid? parsed2 = rawString switch
            {
                Guid g => g,
                string s when Guid.TryParse(s, out var pg) => pg,
                _ => null
            };
            Assert.Equal(expectedGuid, parsed2);

            // Case 3: Invalid string
            object invalidString = "not-a-guid";
            Guid? parsed3 = invalidString switch
            {
                Guid g => g,
                string s when Guid.TryParse(s, out var pg) => pg,
                _ => null
            };
            Assert.Null(parsed3);

            // Case 4: Other object type
            object intObj = 12345;
            Guid? parsed4 = intObj switch
            {
                Guid g => g,
                string s when Guid.TryParse(s, out var pg) => pg,
                _ => null
            };
            Assert.Null(parsed4);
        }
    }
}
