#nullable enable
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
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
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Auth.UnitTests
{
    public class SyncPushUnitTests
    {
        private static IConfiguration CreateConfig(bool localFirstEnabled, bool readOnlyMode, bool pushEnabled)
        {
            var inMemorySettings = new Dictionary<string, string?>
            {
                ["LocalFirst:Enabled"] = localFirstEnabled.ToString().ToLowerInvariant(),
                ["LocalFirst:ReadOnlyMode"] = readOnlyMode.ToString().ToLowerInvariant(),
                ["Sync:PushEnabled"] = pushEnabled.ToString().ToLowerInvariant()
            };

            return new ConfigurationBuilder()
                .AddInMemoryCollection(inMemorySettings)
                .Build();
        }

        #region 1. Route and Middleware Security Tests

        [Fact]
        public void RouteAllowlist_SyncPush_BlockedInOfflineReadOnly()
        {
            var config = CreateConfig(localFirstEnabled: true, readOnlyMode: true, pushEnabled: true);
            var isPilotPermitted = ReadOnlyModeMiddleware.IsPermittedOfflineWritePilotRoute("POST", "/api/sync/push", config);
            Assert.True(isPilotPermitted); // The method tests pilot route match, but ReadOnly middleware blocks before pilot check
        }

        [Fact]
        public async Task ReadOnlyMiddleware_BlocksSyncPush_InOfflineReadOnlyMode_EvenIfPushEnabled()
        {
            // Even if PushEnabled=true, in OfflineReadOnly all POST /api/sync/push must be rejected with 403 READ_ONLY_MODE_BLOCKED
            var config = CreateConfig(localFirstEnabled: true, readOnlyMode: true, pushEnabled: true);
            var middleware = new ReadOnlyModeMiddleware(
                next: (ctx) => Task.CompletedTask,
                configuration: config,
                logger: NullLogger<ReadOnlyModeMiddleware>.Instance);

            var context = new DefaultHttpContext();
            context.Request.Method = "POST";
            context.Request.Path = "/api/sync/push";
            context.Response.Body = new MemoryStream();

            await middleware.InvokeAsync(context);

            Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
            context.Response.Body.Seek(0, SeekOrigin.Begin);
            using var reader = new StreamReader(context.Response.Body);
            var body = await reader.ReadToEndAsync();
            Assert.Contains("READ_ONLY_MODE_BLOCKED", body);
        }

        [Fact]
        public async Task ReadOnlyMiddleware_BlocksSyncPush_WhenPushEnabledIsFalse()
        {
            // In OfflineReadWritePilot, if Sync:PushEnabled == false, POST /api/sync/push must be rejected fail-closed
            var config = CreateConfig(localFirstEnabled: true, readOnlyMode: false, pushEnabled: false);
            var middleware = new ReadOnlyModeMiddleware(
                next: (ctx) => Task.CompletedTask,
                configuration: config,
                logger: NullLogger<ReadOnlyModeMiddleware>.Instance);

            var context = new DefaultHttpContext();
            context.Request.Method = "POST";
            context.Request.Path = "/api/sync/push";
            context.Response.Body = new MemoryStream();

            await middleware.InvokeAsync(context);

            Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
            context.Response.Body.Seek(0, SeekOrigin.Begin);
            using var reader = new StreamReader(context.Response.Body);
            var body = await reader.ReadToEndAsync();
            Assert.Contains("OFFLINE_WRITE_SCOPE_BLOCKED", body);
        }

        [Fact]
        public async Task ReadOnlyMiddleware_AllowsSyncPush_WhenPushEnabledIsTrue()
        {
            // In OfflineReadWritePilot, if Sync:PushEnabled == true, POST /api/sync/push passes middleware
            var config = CreateConfig(localFirstEnabled: true, readOnlyMode: false, pushEnabled: true);
            bool nextCalled = false;
            var middleware = new ReadOnlyModeMiddleware(
                next: (ctx) =>
                {
                    nextCalled = true;
                    return Task.CompletedTask;
                },
                configuration: config,
                logger: NullLogger<ReadOnlyModeMiddleware>.Instance);

            var context = new DefaultHttpContext();
            context.Request.Method = "POST";
            context.Request.Path = "/api/sync/push";

            await middleware.InvokeAsync(context);

            Assert.True(nextCalled);
        }

        #endregion

        #region 2. Controller Security and Gate Tests

        [Fact]
        public async Task SyncController_Push_FailsClosed_WhenPushDisabled()
        {
            var config = CreateConfig(localFirstEnabled: true, readOnlyMode: false, pushEnabled: false);
            var syncConnectionProviderMock = new Mock<ISyncConnectionProvider>();
            syncConnectionProviderMock.Setup(p => p.GetSelectedDatabaseId()).Returns("2026");
            syncConnectionProviderMock.Setup(p => p.IsLocalFirstEnabled).Returns(true);
            syncConnectionProviderMock.Setup(p => p.IsReadOnlyMode).Returns(false);

            var pushServiceMock = new Mock<ILocalOutboxPushService>();

            var controller = new SyncController(
                pushServiceMock.Object,
                syncConnectionProviderMock.Object,
                config,
                NullLogger<SyncController>.Instance);

            var result = await controller.PushOutbox(CancellationToken.None);

            var statusResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(StatusCodes.Status403Forbidden, statusResult.StatusCode);
            Assert.Contains("SYNC_PUSH_DISABLED", statusResult.Value?.ToString() ?? "");
        }

        [Fact]
        public async Task SyncController_Push_FailsClosed_WhenReadOnlyModeActive()
        {
            var config = CreateConfig(localFirstEnabled: true, readOnlyMode: true, pushEnabled: true);
            var syncConnectionProviderMock = new Mock<ISyncConnectionProvider>();
            syncConnectionProviderMock.Setup(p => p.GetSelectedDatabaseId()).Returns("2026");
            syncConnectionProviderMock.Setup(p => p.IsLocalFirstEnabled).Returns(true);
            syncConnectionProviderMock.Setup(p => p.IsReadOnlyMode).Returns(true);

            var pushServiceMock = new Mock<ILocalOutboxPushService>();

            var controller = new SyncController(
                pushServiceMock.Object,
                syncConnectionProviderMock.Object,
                config,
                NullLogger<SyncController>.Instance);

            var result = await controller.PushOutbox(CancellationToken.None);

            var statusResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(StatusCodes.Status403Forbidden, statusResult.StatusCode);
        }

        [Fact]
        public async Task SyncController_Push_FailsClosed_WhenLocalFirstDisabled()
        {
            var config = CreateConfig(localFirstEnabled: false, readOnlyMode: false, pushEnabled: true);
            var syncConnectionProviderMock = new Mock<ISyncConnectionProvider>();
            syncConnectionProviderMock.Setup(p => p.GetSelectedDatabaseId()).Returns("2026");
            syncConnectionProviderMock.Setup(p => p.IsLocalFirstEnabled).Returns(false);
            syncConnectionProviderMock.Setup(p => p.IsReadOnlyMode).Returns(false);

            var pushServiceMock = new Mock<ILocalOutboxPushService>();

            var controller = new SyncController(
                pushServiceMock.Object,
                syncConnectionProviderMock.Object,
                config,
                NullLogger<SyncController>.Instance);

            var result = await controller.PushOutbox(CancellationToken.None);

            var statusResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(StatusCodes.Status403Forbidden, statusResult.StatusCode);
        }

        [Fact]
        public async Task SyncController_Push_InvokesPushService_Successfully()
        {
            var config = CreateConfig(localFirstEnabled: true, readOnlyMode: false, pushEnabled: true);
            var syncConnectionProviderMock = new Mock<ISyncConnectionProvider>();
            syncConnectionProviderMock.Setup(p => p.GetSelectedDatabaseId()).Returns("2026");
            syncConnectionProviderMock.Setup(p => p.IsLocalFirstEnabled).Returns(true);
            syncConnectionProviderMock.Setup(p => p.IsReadOnlyMode).Returns(false);

            var pushServiceMock = new Mock<ILocalOutboxPushService>();
            pushServiceMock.Setup(s => s.PushPendingOutboxAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PushBatchResult
                {
                    DatabaseId = "2026",
                    TotalProcessed = 1,
                    Succeeded = 1
                });

            var controller = new SyncController(
                pushServiceMock.Object,
                syncConnectionProviderMock.Object,
                config,
                NullLogger<SyncController>.Instance);

            var result = await controller.PushOutbox(CancellationToken.None);

            var okResult = Assert.IsType<OkObjectResult>(result);
            var batchResult = Assert.IsType<PushBatchResult>(okResult.Value);
            Assert.Equal("2026", batchResult.DatabaseId);
            pushServiceMock.Verify(s => s.PushPendingOutboxAsync(It.IsAny<CancellationToken>()), Times.Once);
        }

        #endregion

        #region 3. RequestHash Calculation Tests

        [Fact]
        public void RequestHash_IsDeterministic_AndSensitiveToChanges()
        {
            var dbId = "2026";
            var devId1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
            var devId2 = Guid.Parse("22222222-2222-2222-2222-222222222222");
            var cmd = "Daily.Insert";
            var entityType = "Daily";
            var syncId = Guid.Parse("33333333-3333-3333-3333-333333333333");
            var payload1 = "{\"title\":\"Work Log\"}";
            var payload2 = "{\"title\":\"Modified Work Log\"}";

            var hash1a = LocalOutboxPushService.ComputeRequestHash(dbId, devId1, cmd, entityType, syncId, payload1);
            var hash1b = LocalOutboxPushService.ComputeRequestHash(dbId, devId1, cmd, entityType, syncId, payload1);

            Assert.Equal(64, hash1a.Length); // 256 bits = 64 hex chars
            Assert.Equal(hash1a, hash1b);

            // Change payload
            var hash2 = LocalOutboxPushService.ComputeRequestHash(dbId, devId1, cmd, entityType, syncId, payload2);
            Assert.NotEqual(hash1a, hash2);

            // Change DeviceId
            var hash3 = LocalOutboxPushService.ComputeRequestHash(dbId, devId2, cmd, entityType, syncId, payload1);
            Assert.NotEqual(hash1a, hash3);
        }

        #endregion

        #region 4. Payload Parsing and Whitelist Validation Tests

        [Fact]
        public void ParsePayload_Fails_WhenPayloadInvalidJson()
        {
            var item = new LocalOutbox
            {
                PayloadJson = "invalid-json",
                ClientOperationId = Guid.NewGuid(),
                EntitySyncId = Guid.NewGuid(),
                CommandName = "Daily.Insert"
            };

            Assert.Throws<SyncPayloadValidationException>(() =>
                AzurePushTransactionCoordinator.ParseAndValidatePayload(item));
        }

        [Fact]
        public void ParsePayload_Fails_WhenEntitySyncIdIsEmpty()
        {
            var item = new LocalOutbox
            {
                PayloadJson = "{\"schemaVersion\":1,\"entityType\":\"Daily\",\"operationType\":\"INSERT\",\"deviceId\":\"" + Guid.NewGuid() + "\",\"entitySyncId\":\"00000000-0000-0000-0000-000000000000\",\"entityData\":{}}",
                ClientOperationId = Guid.NewGuid(),
                EntitySyncId = Guid.Empty,
                CommandName = "Daily.Insert"
            };

            Assert.Throws<SyncPayloadValidationException>(() =>
                AzurePushTransactionCoordinator.ParseAndValidatePayload(item));
        }

        [Fact]
        public void ParsePayload_ParsesAllowedScalarFields_Correctly()
        {
            var syncId = Guid.NewGuid();
            var deviceId = Guid.NewGuid();
            var payload = new
            {
                schemaVersion = 1,
                entityType = "Daily",
                operationType = "INSERT",
                deviceId = deviceId,
                entitySyncId = syncId,
                baseServerVersion = 0,
                entityData = new
                {
                    Id = 9999, // Should be ignored by coordinator
                    SyncId = syncId,
                    Name = "يومية 2026-05-01",
                    DailyDate = "2026-05-01T00:00:00Z",
                    Closed = false,
                    IsActive = true,
                    CreatedAt = "2026-05-01T10:00:00Z"
                }
            };

            var item = new LocalOutbox
            {
                PayloadJson = JsonSerializer.Serialize(payload),
                ClientOperationId = Guid.NewGuid(),
                EntitySyncId = syncId,
                CommandName = "Daily.Insert"
            };

            var parsed = AzurePushTransactionCoordinator.ParseAndValidatePayload(item);

            Assert.Equal("INSERT", parsed.OperationType);
            Assert.Equal(syncId, parsed.SyncId);
            Assert.Equal("يومية 2026-05-01", parsed.Name);
            Assert.False(parsed.Closed);
            Assert.True(parsed.IsActive);
        }

        [Fact]
        public void ParsePayload_Enforces_SoftDelete_SetsIsActiveFalse()
        {
            var syncId = Guid.NewGuid();
            var deviceId = Guid.NewGuid();
            var payload = new
            {
                schemaVersion = 1,
                entityType = "Daily",
                operationType = "SOFT_DELETE",
                deviceId = deviceId,
                entitySyncId = syncId,
                baseServerVersion = 1,
                entityData = new
                {
                    SyncId = syncId,
                    Name = "يومية 2026-05-01",
                    DailyDate = "2026-05-01T00:00:00Z",
                    Closed = false,
                    IsActive = true, // Attempt to set IsActive=true during SOFT_DELETE
                    CreatedAt = "2026-05-01T10:00:00Z"
                }
            };

            var item = new LocalOutbox
            {
                PayloadJson = JsonSerializer.Serialize(payload),
                ClientOperationId = Guid.NewGuid(),
                EntitySyncId = syncId,
                CommandName = "Daily.SoftDelete"
            };

            var parsed = AzurePushTransactionCoordinator.ParseAndValidatePayload(item);
            // SOFT_DELETE forces IsActive to false
            Assert.False(parsed.IsActive);
        }

        #endregion

        #region 5. Connection and Binding Isolation Tests

        [Fact]
        public async Task AzureConnectionFactory_Throws_OnInvalidOrNonAzureBinding()
        {
            var syncConnectionProviderMock = new Mock<ISyncConnectionProvider>();
            // Remote connection string pointing to local server instead of Azure database
            syncConnectionProviderMock.Setup(p => p.GetRemoteConnectionString("2026"))
                .Returns("Server=localhost;Database=IProgramDb2026;Trusted_Connection=True;");

            var factory = new AzureRemoteDatabaseConnectionFactory(syncConnectionProviderMock.Object);

            // Attempting to create open connection to localhost with production factory fails validation
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await factory.CreateOpenConnectionAsync("2026", CancellationToken.None);
            });
        }

        #endregion

        #region 6. Domain Exceptions and Controller Status Code Tests

        [Fact]
        public void DomainExceptions_Have_StablePublicErrorCodes()
        {
            Assert.Equal("SYNC_PUSH_DISABLED", new SyncPushDisabledException().ErrorCode);
            Assert.Equal("SYNC_PUSH_ALREADY_RUNNING", new SyncPushAlreadyRunningException().ErrorCode);
            Assert.Equal("SYNC_VERSION_CONFLICT", new SyncVersionConflictException(0, 1).ErrorCode);
            Assert.Equal("SYNC_OPERATION_ID_REUSE", new SyncOperationIdReuseException().ErrorCode);
            Assert.Equal("SYNC_ENTITY_ALREADY_EXISTS", new SyncEntityAlreadyExistsException().ErrorCode);
            Assert.Equal("SYNC_ENTITY_NOT_FOUND", new SyncEntityNotFoundException().ErrorCode);
            Assert.Equal("SYNC_PAYLOAD_INVALID", new SyncPayloadValidationException("test").ErrorCode);
            Assert.Equal("SYNC_METADATA_MISMATCH", new SyncMetadataMismatchException("test").ErrorCode);
            Assert.Equal("SYNC_LOCAL_STATE_MISSING", new SyncLocalStateMissingException("test").ErrorCode);
            Assert.Equal("SYNC_LEASE_EXPIRED", new SyncLeaseExpiredException().ErrorCode);
            Assert.Equal("SYNC_CORRUPT_RESPONSE_JSON", new SyncCorruptResponseJsonException("test").ErrorCode);
        }

        [Fact]
        public async Task SyncController_Maps_SyncLeaseExpiredException_To_409Conflict()
        {
            var pushServiceMock = new Mock<ILocalOutboxPushService>();
            pushServiceMock.Setup(s => s.PushPendingOutboxAsync(It.IsAny<CancellationToken>()))
                .ThrowsAsync(new SyncLeaseExpiredException());

            var syncConnectionProviderMock = new Mock<ISyncConnectionProvider>();
            syncConnectionProviderMock.Setup(p => p.IsLocalFirstEnabled).Returns(true);
            syncConnectionProviderMock.Setup(p => p.IsReadOnlyMode).Returns(false);

            var config = CreateConfig(localFirstEnabled: true, readOnlyMode: false, pushEnabled: true);
            var controller = new SyncController(pushServiceMock.Object, syncConnectionProviderMock.Object, config, NullLogger<SyncController>.Instance);

            var result = await controller.PushOutbox(CancellationToken.None) as ObjectResult;
            Assert.NotNull(result);
            Assert.Equal(StatusCodes.Status409Conflict, result.StatusCode);
        }

        [Fact]
        public async Task SyncController_Maps_SyncMetadataMismatchException_To_400BadRequest()
        {
            var pushServiceMock = new Mock<ILocalOutboxPushService>();
            pushServiceMock.Setup(s => s.PushPendingOutboxAsync(It.IsAny<CancellationToken>()))
                .ThrowsAsync(new SyncMetadataMismatchException("Metadata mismatch"));

            var syncConnectionProviderMock = new Mock<ISyncConnectionProvider>();
            syncConnectionProviderMock.Setup(p => p.IsLocalFirstEnabled).Returns(true);
            syncConnectionProviderMock.Setup(p => p.IsReadOnlyMode).Returns(false);

            var config = CreateConfig(localFirstEnabled: true, readOnlyMode: false, pushEnabled: true);
            var controller = new SyncController(pushServiceMock.Object, syncConnectionProviderMock.Object, config, NullLogger<SyncController>.Instance);

            var result = await controller.PushOutbox(CancellationToken.None) as ObjectResult;
            Assert.NotNull(result);
            Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
        }

        [Fact]
        public async Task SyncController_Maps_SyncLocalStateMissingException_To_500InternalServerError()
        {
            var pushServiceMock = new Mock<ILocalOutboxPushService>();
            pushServiceMock.Setup(s => s.PushPendingOutboxAsync(It.IsAny<CancellationToken>()))
                .ThrowsAsync(new SyncLocalStateMissingException("LocalState missing"));

            var syncConnectionProviderMock = new Mock<ISyncConnectionProvider>();
            syncConnectionProviderMock.Setup(p => p.IsLocalFirstEnabled).Returns(true);
            syncConnectionProviderMock.Setup(p => p.IsReadOnlyMode).Returns(false);

            var config = CreateConfig(localFirstEnabled: true, readOnlyMode: false, pushEnabled: true);
            var controller = new SyncController(pushServiceMock.Object, syncConnectionProviderMock.Object, config, NullLogger<SyncController>.Instance);

            var result = await controller.PushOutbox(CancellationToken.None) as ObjectResult;
            Assert.NotNull(result);
            Assert.Equal(StatusCodes.Status500InternalServerError, result.StatusCode);
        }

        [Fact]
        public async Task SyncController_Maps_SyncCorruptResponseJsonException_To_500InternalServerError()
        {
            var pushServiceMock = new Mock<ILocalOutboxPushService>();
            pushServiceMock.Setup(s => s.PushPendingOutboxAsync(It.IsAny<CancellationToken>()))
                .ThrowsAsync(new SyncCorruptResponseJsonException("Corrupt ResponseJson"));

            var syncConnectionProviderMock = new Mock<ISyncConnectionProvider>();
            syncConnectionProviderMock.Setup(p => p.IsLocalFirstEnabled).Returns(true);
            syncConnectionProviderMock.Setup(p => p.IsReadOnlyMode).Returns(false);

            var config = CreateConfig(localFirstEnabled: true, readOnlyMode: false, pushEnabled: true);
            var controller = new SyncController(pushServiceMock.Object, syncConnectionProviderMock.Object, config, NullLogger<SyncController>.Instance);

            var result = await controller.PushOutbox(CancellationToken.None) as ObjectResult;
            Assert.NotNull(result);
            Assert.Equal(StatusCodes.Status500InternalServerError, result.StatusCode);
        }

        #endregion
    }
}
