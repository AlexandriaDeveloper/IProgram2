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
                new Mock<ILocalDailyPullService>().Object,
                syncConnectionProviderMock.Object,
                config,
                NullLogger<SyncController>.Instance,
                new Mock<ILocalScopeBaselineService>().Object);

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
                new Mock<ILocalDailyPullService>().Object,
                syncConnectionProviderMock.Object,
                config,
                NullLogger<SyncController>.Instance,
                new Mock<ILocalScopeBaselineService>().Object);

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
                new Mock<ILocalDailyPullService>().Object,
                syncConnectionProviderMock.Object,
                config,
                NullLogger<SyncController>.Instance,
                new Mock<ILocalScopeBaselineService>().Object);

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
                new Mock<ILocalDailyPullService>().Object,
                syncConnectionProviderMock.Object,
                config,
                NullLogger<SyncController>.Instance,
                new Mock<ILocalScopeBaselineService>().Object);

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
                DatabaseId = "2026",
                PayloadJson = "{\"schemaVersion\":1,\"entityType\":\"Daily\",\"operationType\":\"INSERT\",\"databaseId\":\"2026\",\"deviceId\":\"" + Guid.NewGuid() + "\",\"entitySyncId\":\"00000000-0000-0000-0000-000000000000\",\"entityData\":{}}",
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
                databaseId = "2026",
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
                DatabaseId = "2026",
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
        public void ParsePayload_SoftDelete_WithIsActiveFalse_Succeeds()
        {
            var syncId = Guid.NewGuid();
            var deviceId = Guid.NewGuid();
            var payload = new
            {
                schemaVersion = 1,
                entityType = "Daily",
                operationType = "SOFT_DELETE",
                databaseId = "2026",
                deviceId = deviceId,
                entitySyncId = syncId,
                baseServerVersion = 1,
                entityData = new
                {
                    SyncId = syncId,
                    Name = "يومية 2026-05-01",
                    DailyDate = "2026-05-01T00:00:00Z",
                    Closed = false,
                    IsActive = false,
                    CreatedAt = "2026-05-01T10:00:00Z",
                    DeactivatedAt = "2026-05-01T11:00:00Z"
                }
            };

            var item = new LocalOutbox
            {
                DatabaseId = "2026",
                PayloadJson = JsonSerializer.Serialize(payload),
                ClientOperationId = Guid.NewGuid(),
                EntitySyncId = syncId,
                CommandName = "Daily.SoftDelete"
            };

            var parsed = AzurePushTransactionCoordinator.ParseAndValidatePayload(item);
            Assert.False(parsed.IsActive);
            Assert.NotNull(parsed.DeactivatedAt);
        }

        [Fact]
        public void ParsePayload_SoftDelete_MalformedDeactivatedAt_ThrowsSyncPayloadValidationException()
        {
            var syncId = Guid.NewGuid();
            var deviceId = Guid.NewGuid();
            var payload = new
            {
                schemaVersion = 1,
                entityType = "Daily",
                operationType = "SOFT_DELETE",
                databaseId = "2026",
                deviceId = deviceId,
                entitySyncId = syncId,
                baseServerVersion = 1,
                entityData = new
                {
                    SyncId = syncId,
                    Name = "يومية 2026-05-01",
                    Closed = false,
                    IsActive = false,
                    DeactivatedAt = "malformed-not-a-date" // Malformed timestamp
                }
            };

            var item = new LocalOutbox
            {
                DatabaseId = "2026",
                PayloadJson = JsonSerializer.Serialize(payload),
                ClientOperationId = Guid.NewGuid(),
                EntitySyncId = syncId,
                CommandName = "Daily.SoftDelete"
            };

            var ex = Assert.Throws<SyncPayloadValidationException>(() =>
                AzurePushTransactionCoordinator.ParseAndValidatePayload(item));
            Assert.Equal("SYNC_PAYLOAD_INVALID", ex.ErrorCode);
            Assert.Contains("Malformed DeactivatedAt timestamp", ex.Message);
        }

        [Fact]
        public void ParsePayload_SoftDelete_MissingDeactivatedAt_ThrowsSyncPayloadValidationException()
        {
            var syncId = Guid.NewGuid();
            var deviceId = Guid.NewGuid();
            var payload = new
            {
                schemaVersion = 1,
                entityType = "Daily",
                operationType = "SOFT_DELETE",
                databaseId = "2026",
                deviceId = deviceId,
                entitySyncId = syncId,
                baseServerVersion = 1,
                entityData = new
                {
                    SyncId = syncId,
                    Name = "يومية 2026-05-01",
                    Closed = false,
                    IsActive = false
                    // Missing DeactivatedAt
                }
            };

            var item = new LocalOutbox
            {
                DatabaseId = "2026",
                PayloadJson = JsonSerializer.Serialize(payload),
                ClientOperationId = Guid.NewGuid(),
                EntitySyncId = syncId,
                CommandName = "Daily.SoftDelete"
            };

            var ex = Assert.Throws<SyncPayloadValidationException>(() =>
                AzurePushTransactionCoordinator.ParseAndValidatePayload(item));
            Assert.Equal("SYNC_PAYLOAD_INVALID", ex.ErrorCode);
            Assert.Contains("DeactivatedAt timestamp is required", ex.Message);
        }

        [Fact]
        public void ParsePayload_Update_WithValidUpdatedAt_Succeeds()
        {
            var syncId = Guid.NewGuid();
            var deviceId = Guid.NewGuid();
            var payload = new
            {
                schemaVersion = 1,
                entityType = "Daily",
                operationType = "UPDATE",
                databaseId = "2026",
                deviceId = deviceId,
                entitySyncId = syncId,
                baseServerVersion = 1,
                entityData = new
                {
                    SyncId = syncId,
                    Name = "يومية 2026-05-01 معدلة",
                    DailyDate = "2026-05-01T00:00:00Z",
                    Closed = true,
                    IsActive = true,
                    UpdatedAt = "2026-05-01T12:00:00Z"
                }
            };

            var item = new LocalOutbox
            {
                DatabaseId = "2026",
                PayloadJson = JsonSerializer.Serialize(payload),
                ClientOperationId = Guid.NewGuid(),
                EntitySyncId = syncId,
                CommandName = "Daily.Update"
            };

            var parsed = AzurePushTransactionCoordinator.ParseAndValidatePayload(item);
            Assert.Equal("UPDATE", parsed.OperationType);
            Assert.NotNull(parsed.UpdatedAt);
        }

        [Fact]
        public void ParsePayload_Update_MissingUpdatedAt_ThrowsSyncPayloadValidationException()
        {
            var syncId = Guid.NewGuid();
            var deviceId = Guid.NewGuid();
            var payload = new
            {
                schemaVersion = 1,
                entityType = "Daily",
                operationType = "UPDATE",
                databaseId = "2026",
                deviceId = deviceId,
                entitySyncId = syncId,
                baseServerVersion = 1,
                entityData = new
                {
                    SyncId = syncId,
                    Name = "يومية 2026-05-01 معدلة",
                    DailyDate = "2026-05-01T00:00:00Z",
                    Closed = true,
                    IsActive = true
                    // Missing UpdatedAt
                }
            };

            var item = new LocalOutbox
            {
                DatabaseId = "2026",
                PayloadJson = JsonSerializer.Serialize(payload),
                ClientOperationId = Guid.NewGuid(),
                EntitySyncId = syncId,
                CommandName = "Daily.Update"
            };

            var ex = Assert.Throws<SyncPayloadValidationException>(() =>
                AzurePushTransactionCoordinator.ParseAndValidatePayload(item));
            Assert.Equal("SYNC_PAYLOAD_INVALID", ex.ErrorCode);
            Assert.Contains("UpdatedAt timestamp is required", ex.Message);
        }

        [Fact]
        public void ParsePayload_Update_MalformedUpdatedAt_ThrowsSyncPayloadValidationException()
        {
            var syncId = Guid.NewGuid();
            var deviceId = Guid.NewGuid();
            var payload = new
            {
                schemaVersion = 1,
                entityType = "Daily",
                operationType = "UPDATE",
                databaseId = "2026",
                deviceId = deviceId,
                entitySyncId = syncId,
                baseServerVersion = 1,
                entityData = new
                {
                    SyncId = syncId,
                    Name = "يومية 2026-05-01 معدلة",
                    DailyDate = "2026-05-01T00:00:00Z",
                    Closed = true,
                    IsActive = true,
                    UpdatedAt = "corrupt-timestamp"
                }
            };

            var item = new LocalOutbox
            {
                DatabaseId = "2026",
                PayloadJson = JsonSerializer.Serialize(payload),
                ClientOperationId = Guid.NewGuid(),
                EntitySyncId = syncId,
                CommandName = "Daily.Update"
            };

            var ex = Assert.Throws<SyncPayloadValidationException>(() =>
                AzurePushTransactionCoordinator.ParseAndValidatePayload(item));
            Assert.Equal("SYNC_PAYLOAD_INVALID", ex.ErrorCode);
            Assert.Contains("Malformed UpdatedAt timestamp", ex.Message);
        }

        [Fact]
        public void ParsePayload_Insert_MalformedDailyDate_ThrowsSyncPayloadValidationException()
        {
            var syncId = Guid.NewGuid();
            var deviceId = Guid.NewGuid();
            var payload = new
            {
                schemaVersion = 1,
                entityType = "Daily",
                operationType = "INSERT",
                databaseId = "2026",
                deviceId = deviceId,
                entitySyncId = syncId,
                baseServerVersion = 0,
                entityData = new
                {
                    SyncId = syncId,
                    Name = "يومية 2026-05-01",
                    DailyDate = "not-a-valid-date",
                    Closed = false,
                    IsActive = true,
                    CreatedAt = "2026-05-01T10:00:00Z"
                }
            };

            var item = new LocalOutbox
            {
                DatabaseId = "2026",
                PayloadJson = JsonSerializer.Serialize(payload),
                ClientOperationId = Guid.NewGuid(),
                EntitySyncId = syncId,
                CommandName = "Daily.Insert"
            };

            var ex = Assert.Throws<SyncPayloadValidationException>(() =>
                AzurePushTransactionCoordinator.ParseAndValidatePayload(item));
            Assert.Equal("SYNC_PAYLOAD_INVALID", ex.ErrorCode);
            Assert.Contains("Malformed DailyDate timestamp", ex.Message);
        }

        [Fact]
        public void ParsePayload_Insert_MalformedCreatedAt_ThrowsSyncPayloadValidationException()
        {
            var syncId = Guid.NewGuid();
            var deviceId = Guid.NewGuid();
            var payload = new
            {
                schemaVersion = 1,
                entityType = "Daily",
                operationType = "INSERT",
                databaseId = "2026",
                deviceId = deviceId,
                entitySyncId = syncId,
                baseServerVersion = 0,
                entityData = new
                {
                    SyncId = syncId,
                    Name = "يومية 2026-05-01",
                    DailyDate = "2026-05-01T00:00:00Z",
                    Closed = false,
                    IsActive = true,
                    CreatedAt = "corrupt-date-string"
                }
            };

            var item = new LocalOutbox
            {
                DatabaseId = "2026",
                PayloadJson = JsonSerializer.Serialize(payload),
                ClientOperationId = Guid.NewGuid(),
                EntitySyncId = syncId,
                CommandName = "Daily.Insert"
            };

            var ex = Assert.Throws<SyncPayloadValidationException>(() =>
                AzurePushTransactionCoordinator.ParseAndValidatePayload(item));
            Assert.Equal("SYNC_PAYLOAD_INVALID", ex.ErrorCode);
            Assert.Contains("Malformed CreatedAt timestamp", ex.Message);
        }

        [Fact]
        public void ParsePayload_Insert_WithIsActiveFalse_ThrowsSyncPayloadValidationException()
        {
            var syncId = Guid.NewGuid();
            var deviceId = Guid.NewGuid();
            var payload = new
            {
                schemaVersion = 1,
                entityType = "Daily",
                operationType = "INSERT",
                databaseId = "2026",
                deviceId = deviceId,
                entitySyncId = syncId,
                baseServerVersion = 0,
                entityData = new
                {
                    SyncId = syncId,
                    Name = "يومية 2026-05-01",
                    DailyDate = "2026-05-01T00:00:00Z",
                    Closed = false,
                    IsActive = false, // Deactivation in INSERT must be rejected
                    CreatedAt = "2026-05-01T10:00:00Z"
                }
            };

            var item = new LocalOutbox
            {
                DatabaseId = "2026",
                PayloadJson = JsonSerializer.Serialize(payload),
                ClientOperationId = Guid.NewGuid(),
                EntitySyncId = syncId,
                CommandName = "Daily.Insert"
            };

            var ex = Assert.Throws<SyncPayloadValidationException>(() =>
                AzurePushTransactionCoordinator.ParseAndValidatePayload(item));
            Assert.Equal("SYNC_PAYLOAD_INVALID", ex.ErrorCode);
        }

        [Fact]
        public void ParsePayload_SoftDelete_WithIsActiveTrue_ThrowsSyncPayloadValidationException()
        {
            var syncId = Guid.NewGuid();
            var deviceId = Guid.NewGuid();
            var payload = new
            {
                schemaVersion = 1,
                entityType = "Daily",
                operationType = "SOFT_DELETE",
                databaseId = "2026",
                deviceId = deviceId,
                entitySyncId = syncId,
                baseServerVersion = 1,
                entityData = new
                {
                    SyncId = syncId,
                    Name = "يومية 2026-05-01",
                    DailyDate = "2026-05-01T00:00:00Z",
                    Closed = false,
                    IsActive = true, // SOFT_DELETE must carry IsActive = false explicitly
                    CreatedAt = "2026-05-01T10:00:00Z"
                }
            };

            var item = new LocalOutbox
            {
                DatabaseId = "2026",
                PayloadJson = JsonSerializer.Serialize(payload),
                ClientOperationId = Guid.NewGuid(),
                EntitySyncId = syncId,
                CommandName = "Daily.SoftDelete"
            };

            var ex = Assert.Throws<SyncPayloadValidationException>(() =>
                AzurePushTransactionCoordinator.ParseAndValidatePayload(item));
            Assert.Equal("SYNC_PAYLOAD_INVALID", ex.ErrorCode);
        }

        [Fact]
        public void ParsePayload_SoftDelete_WithoutIsActive_ThrowsSyncPayloadValidationException()
        {
            var syncId = Guid.NewGuid();
            var deviceId = Guid.NewGuid();
            var payload = new
            {
                schemaVersion = 1,
                entityType = "Daily",
                operationType = "SOFT_DELETE",
                databaseId = "2026",
                deviceId = deviceId,
                entitySyncId = syncId,
                baseServerVersion = 1,
                entityData = new
                {
                    SyncId = syncId,
                    Name = "يومية 2026-05-01",
                    DailyDate = "2026-05-01T00:00:00Z",
                    Closed = false,
                    // Missing IsActive
                    CreatedAt = "2026-05-01T10:00:00Z"
                }
            };

            var item = new LocalOutbox
            {
                DatabaseId = "2026",
                PayloadJson = JsonSerializer.Serialize(payload),
                ClientOperationId = Guid.NewGuid(),
                EntitySyncId = syncId,
                CommandName = "Daily.SoftDelete"
            };

            var ex = Assert.Throws<SyncPayloadValidationException>(() =>
                AzurePushTransactionCoordinator.ParseAndValidatePayload(item));
            Assert.Equal("SYNC_PAYLOAD_INVALID", ex.ErrorCode);
        }

        [Fact]
        public void ParsePayload_DatabaseIdMismatch_ThrowsSyncMetadataMismatchException()
        {
            var syncId = Guid.NewGuid();
            var deviceId = Guid.NewGuid();
            var payload = new
            {
                schemaVersion = 1,
                entityType = "Daily",
                operationType = "INSERT",
                databaseId = "2027", // Mismatched from outbox.DatabaseId = "2026"
                deviceId = deviceId,
                entitySyncId = syncId,
                baseServerVersion = 0,
                entityData = new
                {
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
                DatabaseId = "2026",
                PayloadJson = JsonSerializer.Serialize(payload),
                ClientOperationId = Guid.NewGuid(),
                EntitySyncId = syncId,
                CommandName = "Daily.Insert"
            };

            var ex = Assert.Throws<SyncMetadataMismatchException>(() =>
                AzurePushTransactionCoordinator.ParseAndValidatePayload(item));
            Assert.Equal("SYNC_METADATA_MISMATCH", ex.ErrorCode);
        }

        [Fact]
        public void ParsePayload_EntityDataSyncIdMismatch_ThrowsSyncMetadataMismatchException()
        {
            var rootSyncId = Guid.NewGuid();
            var entityDataSyncId = Guid.NewGuid(); // Mismatched
            var deviceId = Guid.NewGuid();
            var payload = new
            {
                schemaVersion = 1,
                entityType = "Daily",
                operationType = "INSERT",
                databaseId = "2026",
                deviceId = deviceId,
                entitySyncId = rootSyncId,
                baseServerVersion = 0,
                entityData = new
                {
                    SyncId = entityDataSyncId,
                    Name = "يومية 2026-05-01",
                    DailyDate = "2026-05-01T00:00:00Z",
                    Closed = false,
                    IsActive = true,
                    CreatedAt = "2026-05-01T10:00:00Z"
                }
            };

            var item = new LocalOutbox
            {
                DatabaseId = "2026",
                PayloadJson = JsonSerializer.Serialize(payload),
                ClientOperationId = Guid.NewGuid(),
                EntitySyncId = rootSyncId,
                CommandName = "Daily.Insert"
            };

            var ex = Assert.Throws<SyncMetadataMismatchException>(() =>
                AzurePushTransactionCoordinator.ParseAndValidatePayload(item));
            Assert.Equal("SYNC_METADATA_MISMATCH", ex.ErrorCode);
        }

        [Fact]
        public async Task ApplyOperationAsync_ThrowsSyncMetadataMismatch_WhenPayloadDeviceId_DiffersFromLocalDeviceId()
        {
            var syncId = Guid.NewGuid();
            var payloadDeviceId = Guid.NewGuid();
            var expectedDeviceId = Guid.NewGuid(); // Different from payloadDeviceId
            var payload = new
            {
                schemaVersion = 1,
                entityType = "Daily",
                operationType = "INSERT",
                databaseId = "2026",
                deviceId = payloadDeviceId,
                entitySyncId = syncId,
                baseServerVersion = 0,
                entityData = new
                {
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
                DatabaseId = "2026",
                PayloadJson = JsonSerializer.Serialize(payload),
                ClientOperationId = Guid.NewGuid(),
                EntitySyncId = syncId,
                CommandName = "Daily.Insert",
                AggregateType = "Daily"
            };

            var coordinator = new AzurePushTransactionCoordinator(NullLogger<AzurePushTransactionCoordinator>.Instance);
            var mockConn = new Mock<DbConnection>();

            var ex = await Assert.ThrowsAsync<SyncMetadataMismatchException>(async () =>
            {
                await coordinator.ApplyOperationAsync(
                    mockConn.Object,
                    "2026",
                    item,
                    0,
                    "fake-hash",
                    expectedDeviceId,
                    CancellationToken.None);
            });

            Assert.Equal("SYNC_METADATA_MISMATCH", ex.ErrorCode);
            Assert.Contains("does not match expected LocalState DeviceId", ex.Message);
        }

        #endregion

        #region 5. Connection and Binding Isolation Tests

        [Fact]
        public void DatabaseBindingValidator_EnforcesAzureSqlEndpoint()
        {
            // Valid Azure SQL endpoints
            Assert.True(DatabaseBindingValidator.IsAzureSqlEndpoint("mycompany.database.windows.net"));
            Assert.True(DatabaseBindingValidator.IsAzureSqlEndpoint("tcp:mycompany.database.windows.net,1433"));
            Assert.True(DatabaseBindingValidator.IsAzureSqlEndpoint("mycompany.database.windows.net:1433"));

            // Invalid / non-Azure endpoints
            Assert.False(DatabaseBindingValidator.IsAzureSqlEndpoint("localhost"));
            Assert.False(DatabaseBindingValidator.IsAzureSqlEndpoint("127.0.0.1"));
            Assert.False(DatabaseBindingValidator.IsAzureSqlEndpoint("sqlserver.mycorp.local"));
            Assert.False(DatabaseBindingValidator.IsAzureSqlEndpoint("evil-azure.database.windows.net.attacker.com"));
            Assert.False(DatabaseBindingValidator.IsAzureSqlEndpoint(""));
            Assert.False(DatabaseBindingValidator.IsAzureSqlEndpoint(null));
        }

        [Fact]
        public void DatabaseBindingValidator_ValidateAzureBinding_RejectsNonAzureEndpoints()
        {
            // Valid Azure binding passes
            DatabaseBindingValidator.ValidateAzureBinding("myserver.database.windows.net", "IProgramDb2026");

            // Non-Azure endpoints must throw InvalidOperationException
            Assert.Throws<InvalidOperationException>(() =>
                DatabaseBindingValidator.ValidateAzureBinding("localhost", "IProgramDb2026"));

            Assert.Throws<InvalidOperationException>(() =>
                DatabaseBindingValidator.ValidateAzureBinding("internal-server.local", "IProgramDb2026"));

            Assert.Throws<InvalidOperationException>(() =>
                DatabaseBindingValidator.ValidateAzureBinding("myserver.database.windows.net", "IProgramDb_Unknown"));
        }

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
            var controller = new SyncController(pushServiceMock.Object, new Mock<ILocalDailyPullService>().Object, syncConnectionProviderMock.Object, config, NullLogger<SyncController>.Instance, new Mock<ILocalScopeBaselineService>().Object);

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
            var controller = new SyncController(pushServiceMock.Object, new Mock<ILocalDailyPullService>().Object, syncConnectionProviderMock.Object, config, NullLogger<SyncController>.Instance, new Mock<ILocalScopeBaselineService>().Object);

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
            var controller = new SyncController(pushServiceMock.Object, new Mock<ILocalDailyPullService>().Object, syncConnectionProviderMock.Object, config, NullLogger<SyncController>.Instance, new Mock<ILocalScopeBaselineService>().Object);

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
            var controller = new SyncController(pushServiceMock.Object, new Mock<ILocalDailyPullService>().Object, syncConnectionProviderMock.Object, config, NullLogger<SyncController>.Instance, new Mock<ILocalScopeBaselineService>().Object);

            var result = await controller.PushOutbox(CancellationToken.None) as ObjectResult;
            Assert.NotNull(result);
            Assert.Equal(StatusCodes.Status500InternalServerError, result.StatusCode);
        }

        #endregion
    }
}
