#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Api.Controllers;
using Auth.Api.Middleware;
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
    public class SyncPullUnitTests
    {
        private static IConfiguration CreateConfig(bool localFirstEnabled, bool readOnlyMode, bool pullEnabled)
        {
            var inMemorySettings = new Dictionary<string, string?>
            {
                ["LocalFirst:Enabled"] = localFirstEnabled.ToString().ToLowerInvariant(),
                ["LocalFirst:ReadOnlyMode"] = readOnlyMode.ToString().ToLowerInvariant(),
                ["Sync:PullEnabled"] = pullEnabled.ToString().ToLowerInvariant()
            };

            return new ConfigurationBuilder()
                .AddInMemoryCollection(inMemorySettings)
                .Build();
        }

        #region 1. Route and Middleware Security Tests

        [Fact]
        public void RouteAllowlist_SyncPull_PermittedInPilot_WhenPullEnabled()
        {
            var config = CreateConfig(localFirstEnabled: true, readOnlyMode: false, pullEnabled: true);
            var isPilotPermitted = ReadOnlyModeMiddleware.IsPermittedOfflineWritePilotRoute("POST", "/api/sync/pull", config);
            Assert.True(isPilotPermitted);
        }

        [Fact]
        public void RouteAllowlist_SyncPull_BlockedInPilot_WhenPullDisabled()
        {
            var config = CreateConfig(localFirstEnabled: true, readOnlyMode: false, pullEnabled: false);
            var isPilotPermitted = ReadOnlyModeMiddleware.IsPermittedOfflineWritePilotRoute("POST", "/api/sync/pull", config);
            Assert.False(isPilotPermitted);
        }

        [Fact]
        public async Task ReadOnlyMiddleware_BlocksSyncPull_InOfflineReadOnlyMode_EvenIfPullEnabled()
        {
            var config = CreateConfig(localFirstEnabled: true, readOnlyMode: true, pullEnabled: true);
            var middleware = new ReadOnlyModeMiddleware(
                next: (ctx) => Task.CompletedTask,
                configuration: config,
                logger: NullLogger<ReadOnlyModeMiddleware>.Instance);

            var context = new DefaultHttpContext();
            context.Request.Method = "POST";
            context.Request.Path = "/api/sync/pull";
            context.Response.Body = new MemoryStream();

            await middleware.InvokeAsync(context);

            Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
            context.Response.Body.Seek(0, SeekOrigin.Begin);
            using var reader = new StreamReader(context.Response.Body);
            var body = await reader.ReadToEndAsync();
            Assert.Contains("READ_ONLY_MODE_BLOCKED", body);
        }

        [Fact]
        public async Task ReadOnlyMiddleware_BlocksSyncPull_WhenPullEnabledIsFalse()
        {
            var config = CreateConfig(localFirstEnabled: true, readOnlyMode: false, pullEnabled: false);
            var middleware = new ReadOnlyModeMiddleware(
                next: (ctx) => Task.CompletedTask,
                configuration: config,
                logger: NullLogger<ReadOnlyModeMiddleware>.Instance);

            var context = new DefaultHttpContext();
            context.Request.Method = "POST";
            context.Request.Path = "/api/sync/pull";
            context.Response.Body = new MemoryStream();

            await middleware.InvokeAsync(context);

            Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
            context.Response.Body.Seek(0, SeekOrigin.Begin);
            using var reader = new StreamReader(context.Response.Body);
            var body = await reader.ReadToEndAsync();
            Assert.Contains("OFFLINE_WRITE_SCOPE_BLOCKED", body);
        }

        #endregion

        #region 2. Controller Gating & Validation Tests

        [Fact]
        public async Task SyncController_Pull_FailsClosed_WhenPullDisabled()
        {
            var config = CreateConfig(localFirstEnabled: false, readOnlyMode: false, pullEnabled: false);
            var syncConnectionProviderMock = new Mock<ISyncConnectionProvider>();
            syncConnectionProviderMock.Setup(p => p.GetSelectedDatabaseId()).Returns("2026");
            syncConnectionProviderMock.Setup(p => p.IsReadOnlyMode).Returns(false);

            var controller = new SyncController(
                new Mock<ILocalOutboxPushService>().Object,
                new Mock<ILocalDailyPullService>().Object,
                syncConnectionProviderMock.Object,
                config,
                NullLogger<SyncController>.Instance);

            var result = await controller.PullDaily(CancellationToken.None);

            var statusResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(StatusCodes.Status403Forbidden, statusResult.StatusCode);
            Assert.Contains("SYNC_PULL_DISABLED", statusResult.Value?.ToString() ?? "");
        }

        [Fact]
        public async Task SyncController_Pull_FailsClosed_WhenReadOnlyModeActive()
        {
            var config = CreateConfig(localFirstEnabled: false, readOnlyMode: true, pullEnabled: true);
            var syncConnectionProviderMock = new Mock<ISyncConnectionProvider>();
            syncConnectionProviderMock.Setup(p => p.GetSelectedDatabaseId()).Returns("2026");
            syncConnectionProviderMock.Setup(p => p.IsReadOnlyMode).Returns(true);

            var controller = new SyncController(
                new Mock<ILocalOutboxPushService>().Object,
                new Mock<ILocalDailyPullService>().Object,
                syncConnectionProviderMock.Object,
                config,
                NullLogger<SyncController>.Instance);

            var result = await controller.PullDaily(CancellationToken.None);

            var statusResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(StatusCodes.Status403Forbidden, statusResult.StatusCode);
            Assert.Contains("READ_ONLY_MODE_BLOCKED", statusResult.Value?.ToString() ?? "");
        }

        [Fact]
        public async Task SyncController_Pull_FailsClosed_WhenDatabaseIdIsInvalid()
        {
            var config = CreateConfig(localFirstEnabled: false, readOnlyMode: false, pullEnabled: true);
            var syncConnectionProviderMock = new Mock<ISyncConnectionProvider>();
            syncConnectionProviderMock.Setup(p => p.GetSelectedDatabaseId()).Returns("2025");
            syncConnectionProviderMock.Setup(p => p.IsReadOnlyMode).Returns(false);

            var controller = new SyncController(
                new Mock<ILocalOutboxPushService>().Object,
                new Mock<ILocalDailyPullService>().Object,
                syncConnectionProviderMock.Object,
                config,
                NullLogger<SyncController>.Instance);

            var result = await controller.PullDaily(CancellationToken.None);

            var statusResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(StatusCodes.Status400BadRequest, statusResult.StatusCode);
            Assert.Contains("INVALID_DATABASE_SELECTION", statusResult.Value?.ToString() ?? "");
        }

        [Fact]
        public async Task SyncController_Pull_InvokesPullService_Successfully()
        {
            var config = CreateConfig(localFirstEnabled: false, readOnlyMode: false, pullEnabled: true);
            var syncConnectionProviderMock = new Mock<ISyncConnectionProvider>();
            syncConnectionProviderMock.Setup(p => p.GetSelectedDatabaseId()).Returns("2026");
            syncConnectionProviderMock.Setup(p => p.IsReadOnlyMode).Returns(false);

            var pullServiceMock = new Mock<ILocalDailyPullService>();
            pullServiceMock.Setup(s => s.PullDailyChangesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PullResultDto
                {
                    DatabaseId = "2026",
                    PreviousWatermark = 0,
                    FinalServerVersion = 2,
                    TotalProcessed = 1,
                    Succeeded = 1
                });

            var controller = new SyncController(
                new Mock<ILocalOutboxPushService>().Object,
                pullServiceMock.Object,
                syncConnectionProviderMock.Object,
                config,
                NullLogger<SyncController>.Instance);

            var result = await controller.PullDaily(CancellationToken.None);

            var okResult = Assert.IsType<OkObjectResult>(result);
            Assert.Equal(StatusCodes.Status200OK, okResult.StatusCode);
            var dto = Assert.IsType<PullResultDto>(okResult.Value);
            Assert.Equal(2, dto.FinalServerVersion);
        }

        #endregion

        #region 3. Exception Mapping Tests

        [Fact]
        public async Task SyncController_Maps_SyncPullAlreadyRunningException_To_409Conflict()
        {
            var pullServiceMock = new Mock<ILocalDailyPullService>();
            pullServiceMock.Setup(s => s.PullDailyChangesAsync(It.IsAny<CancellationToken>()))
                .ThrowsAsync(new SyncPullAlreadyRunningException());

            var syncConnectionProviderMock = new Mock<ISyncConnectionProvider>();
            syncConnectionProviderMock.Setup(p => p.GetSelectedDatabaseId()).Returns("2026");
            syncConnectionProviderMock.Setup(p => p.IsReadOnlyMode).Returns(false);

            var config = CreateConfig(localFirstEnabled: false, readOnlyMode: false, pullEnabled: true);
            var controller = new SyncController(new Mock<ILocalOutboxPushService>().Object, pullServiceMock.Object, syncConnectionProviderMock.Object, config, NullLogger<SyncController>.Instance);

            var result = await controller.PullDaily(CancellationToken.None) as ObjectResult;
            Assert.NotNull(result);
            Assert.Equal(StatusCodes.Status409Conflict, result.StatusCode);
            Assert.Contains("SYNC_PULL_ALREADY_RUNNING", result.Value?.ToString() ?? "");
        }

        [Fact]
        public async Task SyncController_Maps_SyncPullBlockedLocalChangesPendingException_To_409Conflict()
        {
            var pullServiceMock = new Mock<ILocalDailyPullService>();
            pullServiceMock.Setup(s => s.PullDailyChangesAsync(It.IsAny<CancellationToken>()))
                .ThrowsAsync(new SyncPullBlockedLocalChangesPendingException());

            var syncConnectionProviderMock = new Mock<ISyncConnectionProvider>();
            syncConnectionProviderMock.Setup(p => p.GetSelectedDatabaseId()).Returns("2026");
            syncConnectionProviderMock.Setup(p => p.IsReadOnlyMode).Returns(false);

            var config = CreateConfig(localFirstEnabled: false, readOnlyMode: false, pullEnabled: true);
            var controller = new SyncController(new Mock<ILocalOutboxPushService>().Object, pullServiceMock.Object, syncConnectionProviderMock.Object, config, NullLogger<SyncController>.Instance);

            var result = await controller.PullDaily(CancellationToken.None) as ObjectResult;
            Assert.NotNull(result);
            Assert.Equal(StatusCodes.Status409Conflict, result.StatusCode);
            Assert.Contains("PULL_BLOCKED_LOCAL_CHANGES_PENDING", result.Value?.ToString() ?? "");
        }

        [Fact]
        public async Task SyncController_Maps_SyncPullCheckpointAheadOfServerException_To_409Conflict()
        {
            var pullServiceMock = new Mock<ILocalDailyPullService>();
            pullServiceMock.Setup(s => s.PullDailyChangesAsync(It.IsAny<CancellationToken>()))
                .ThrowsAsync(new SyncPullCheckpointAheadOfServerException(5, 2));

            var syncConnectionProviderMock = new Mock<ISyncConnectionProvider>();
            syncConnectionProviderMock.Setup(p => p.GetSelectedDatabaseId()).Returns("2026");
            syncConnectionProviderMock.Setup(p => p.IsReadOnlyMode).Returns(false);

            var config = CreateConfig(localFirstEnabled: false, readOnlyMode: false, pullEnabled: true);
            var controller = new SyncController(new Mock<ILocalOutboxPushService>().Object, pullServiceMock.Object, syncConnectionProviderMock.Object, config, NullLogger<SyncController>.Instance);

            var result = await controller.PullDaily(CancellationToken.None) as ObjectResult;
            Assert.NotNull(result);
            Assert.Equal(StatusCodes.Status409Conflict, result.StatusCode);
            Assert.Contains("PULL_CHECKPOINT_AHEAD_OF_SERVER", result.Value?.ToString() ?? "");
        }

        [Fact]
        public async Task SyncController_Maps_SyncPullFeedGapException_To_409Conflict()
        {
            var pullServiceMock = new Mock<ILocalDailyPullService>();
            pullServiceMock.Setup(s => s.PullDailyChangesAsync(It.IsAny<CancellationToken>()))
                .ThrowsAsync(new SyncPullFeedGapException("Feed gap"));

            var syncConnectionProviderMock = new Mock<ISyncConnectionProvider>();
            syncConnectionProviderMock.Setup(p => p.GetSelectedDatabaseId()).Returns("2026");
            syncConnectionProviderMock.Setup(p => p.IsReadOnlyMode).Returns(false);

            var config = CreateConfig(localFirstEnabled: false, readOnlyMode: false, pullEnabled: true);
            var controller = new SyncController(new Mock<ILocalOutboxPushService>().Object, pullServiceMock.Object, syncConnectionProviderMock.Object, config, NullLogger<SyncController>.Instance);

            var result = await controller.PullDaily(CancellationToken.None) as ObjectResult;
            Assert.NotNull(result);
            Assert.Equal(StatusCodes.Status409Conflict, result.StatusCode);
            Assert.Contains("PULL_FEED_GAP", result.Value?.ToString() ?? "");
        }

        [Fact]
        public async Task SyncController_Maps_SyncPullTombstoneValidationException_To_500InternalServerError()
        {
            var pullServiceMock = new Mock<ILocalDailyPullService>();
            pullServiceMock.Setup(s => s.PullDailyChangesAsync(It.IsAny<CancellationToken>()))
                .ThrowsAsync(new SyncPullTombstoneValidationException("Tombstone missing"));

            var syncConnectionProviderMock = new Mock<ISyncConnectionProvider>();
            syncConnectionProviderMock.Setup(p => p.GetSelectedDatabaseId()).Returns("2026");
            syncConnectionProviderMock.Setup(p => p.IsReadOnlyMode).Returns(false);

            var config = CreateConfig(localFirstEnabled: false, readOnlyMode: false, pullEnabled: true);
            var controller = new SyncController(new Mock<ILocalOutboxPushService>().Object, pullServiceMock.Object, syncConnectionProviderMock.Object, config, NullLogger<SyncController>.Instance);

            var result = await controller.PullDaily(CancellationToken.None) as ObjectResult;
            Assert.NotNull(result);
            Assert.Equal(StatusCodes.Status500InternalServerError, result.StatusCode);
            Assert.Contains("PULL_TOMBSTONE_VALIDATION_FAILED", result.Value?.ToString() ?? "");
        }

        [Fact]
        public async Task SyncController_Maps_SyncPullUnsupportedEntityTypeException_To_400BadRequest()
        {
            var pullServiceMock = new Mock<ILocalDailyPullService>();
            pullServiceMock.Setup(s => s.PullDailyChangesAsync(It.IsAny<CancellationToken>()))
                .ThrowsAsync(new SyncPullUnsupportedEntityTypeException("Unsupported EntityType"));

            var syncConnectionProviderMock = new Mock<ISyncConnectionProvider>();
            syncConnectionProviderMock.Setup(p => p.GetSelectedDatabaseId()).Returns("2026");
            syncConnectionProviderMock.Setup(p => p.IsReadOnlyMode).Returns(false);

            var config = CreateConfig(localFirstEnabled: false, readOnlyMode: false, pullEnabled: true);
            var controller = new SyncController(new Mock<ILocalOutboxPushService>().Object, pullServiceMock.Object, syncConnectionProviderMock.Object, config, NullLogger<SyncController>.Instance);

            var result = await controller.PullDaily(CancellationToken.None) as ObjectResult;
            Assert.NotNull(result);
            Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
            Assert.Contains("PULL_UNSUPPORTED_ENTITY_TYPE", result.Value?.ToString() ?? "");
        }

        #endregion
    }
}
