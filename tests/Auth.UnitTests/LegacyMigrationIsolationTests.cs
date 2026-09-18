using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Api.Controllers;
using Auth.Infrastructure.Hubs;
using Auth.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Auth.UnitTests
{
    public class LegacyMigrationIsolationTests
    {
        private readonly Mock<IHubContext<MigrationHub>> _mockHubContext;
        private readonly Mock<ILogger<MigrationController>> _mockControllerLogger;

        public LegacyMigrationIsolationTests()
        {
            _mockHubContext = new Mock<IHubContext<MigrationHub>>();
            _mockControllerLogger = new Mock<ILogger<MigrationController>>();
        }

        [Fact]
        public async Task FullSync_ReturnsNotFound_WhenLegacyMigrationIsDisabled()
        {
            var inMemoryConfig = new Dictionary<string, string?>
            {
                { "LegacyMigration:Enabled", "false" }
            };
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(inMemoryConfig).Build();
            var service = new DataMigrationService(configuration, _mockHubContext.Object);
            var controller = new MigrationController(service, configuration, _mockControllerLogger.Object);

            var result = await controller.FullSync();

            var notFoundResult = Assert.IsType<NotFoundObjectResult>(result);
            Assert.Equal(404, notFoundResult.StatusCode);
        }

        [Fact]
        public async Task Migrate_ReturnsNotFound_WhenLegacyMigrationIsDisabled()
        {
            var inMemoryConfig = new Dictionary<string, string?>
            {
                { "LegacyMigration:Enabled", "false" }
            };
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(inMemoryConfig).Build();
            var service = new DataMigrationService(configuration, _mockHubContext.Object);
            var controller = new MigrationController(service, configuration, _mockControllerLogger.Object);

            var result = await controller.Migrate();

            var notFoundResult = Assert.IsType<NotFoundObjectResult>(result);
            Assert.Equal(404, notFoundResult.StatusCode);
        }

        [Fact]
        public async Task PullFromCloud_ReturnsNotFound_WhenLegacyMigrationIsDisabled()
        {
            var inMemoryConfig = new Dictionary<string, string?>
            {
                { "LegacyMigration:Enabled", "false" }
            };
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(inMemoryConfig).Build();
            var service = new DataMigrationService(configuration, _mockHubContext.Object);
            var controller = new MigrationController(service, configuration, _mockControllerLogger.Object);

            var result = await controller.PullFromCloud();

            var notFoundResult = Assert.IsType<NotFoundObjectResult>(result);
            Assert.Equal(404, notFoundResult.StatusCode);
        }

        [Fact]
        public async Task Endpoints_ReturnNotFound_WhenLegacyMigrationSettingIsOmitted()
        {
            // Empty configuration defaults to false
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
            var service = new DataMigrationService(configuration, _mockHubContext.Object);
            var controller = new MigrationController(service, configuration, _mockControllerLogger.Object);

            var syncResult = await controller.FullSync();
            var migrateResult = await controller.Migrate();
            var pullResult = await controller.PullFromCloud();

            Assert.IsType<NotFoundObjectResult>(syncResult);
            Assert.IsType<NotFoundObjectResult>(migrateResult);
            Assert.IsType<NotFoundObjectResult>(pullResult);
        }

        [Fact]
        public async Task DataMigrationService_ThrowsInvalidOperationException_WhenDisabled()
        {
            var inMemoryConfig = new Dictionary<string, string?>
            {
                { "LegacyMigration:Enabled", "false" }
            };
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(inMemoryConfig).Build();
            var service = new DataMigrationService(configuration, _mockHubContext.Object);

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.FullSyncToSupabaseAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.PullFromSupabaseAsync());
        }
    }
}
