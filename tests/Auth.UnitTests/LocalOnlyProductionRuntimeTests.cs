#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Api.Controllers;
using Auth.Api.Middleware;
using Auth.Infrastructure;
using Auth.Infrastructure.Services;
using Auth.Infrastructure.Sync;
using Auth.Infrastructure.Sync.Authoritative;
using Auth.Infrastructure.Sync.Push;
using Core.Exceptions;
using Core.Interfaces;
using Core.Models;
using Core.Models.Sync;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Persistence.Repository;
using Xunit;

namespace Auth.UnitTests
{
    public class LocalOnlyProductionRuntimeTests
    {
        private IConfiguration CreateConfiguration(bool localOnlyProduction = true, string mode = "LocalOnlyProduction")
        {
            var inMemorySettings = new Dictionary<string, string?>
            {
                { "ConnectionStrings:DefaultConnection", "Server=test-azure.database.windows.net;Database=IProgramDb2026;User ID=u;Password=p;" },
                { "ConnectionStrings:CON2027", "Server=test-azure.database.windows.net;Database=IProgramDb2027;User ID=u;Password=p;" },
                { "ConnectionStrings:LocalConnection2026", "Server=localhost;Database=IProgramLocalDb2026;Trusted_Connection=True;TrustServerCertificate=True;" },
                { "ConnectionStrings:LocalConnection2027", "Server=localhost;Database=IProgramLocalDb2027;Trusted_Connection=True;TrustServerCertificate=True;" },
                { "ConnectionStrings:ManualSyncRemote2026", "Server=test-azure.database.windows.net;Database=IProgramDb2026;User ID=u;Password=p;" },
                { "ConnectionStrings:ManualSyncRemote2027", "Server=test-azure.database.windows.net;Database=IProgramDb2027;User ID=u;Password=p;" },
                { "DatabaseSettings:Databases:0:Id", "2026" },
                { "DatabaseSettings:Databases:0:Name", "بيانات 2026" },
                { "DatabaseSettings:Databases:0:ConnectionStringName", "DefaultConnection" },
                { "DatabaseSettings:Databases:1:Id", "2027" },
                { "DatabaseSettings:Databases:1:Name", "بيانات 2027" },
                { "DatabaseSettings:Databases:1:ConnectionStringName", "CON2027" },
                { "LocalFirst:Enabled", "true" },
                { "LocalFirst:ReadOnlyMode", "false" },
                { "LocalFirst:LocalOnlyProduction", localOnlyProduction.ToString().ToLowerInvariant() },
                { "LocalFirst:Mode", mode },
                { "LocalFirst:SqlServerInstance", "localhost" },
                { "LocalFirst:Databases:0:Id", "2026" },
                { "LocalFirst:Databases:0:LocalDatabaseName", "IProgramLocalDb2026" },
                { "LocalFirst:Databases:0:LocalConnectionStringName", "LocalConnection2026" },
                { "LocalFirst:Databases:1:Id", "2027" },
                { "LocalFirst:Databases:1:LocalDatabaseName", "IProgramLocalDb2027" },
                { "LocalFirst:Databases:1:LocalConnectionStringName", "LocalConnection2027" },
                { "Sync:AuthoritativeTrackingEnabled", "false" },
                { "Sync:PullEnabled", "false" },
                { "Sync:PushEnabled", "false" }
            };

            return new ConfigurationBuilder()
                .AddInMemoryCollection(inMemorySettings)
                .Build();
        }

        // =========================================================================
        // 1. DbConnectionProvider Routing & Fail-Closed Guards
        // =========================================================================

        [Fact]
        public void DbConnectionProvider_IsLocalOnlyProduction_ReturnsTrueWhenConfigured()
        {
            var config = CreateConfiguration(localOnlyProduction: true);
            var mockAccessor = new Mock<IHttpContextAccessor>();
            var provider = new DbConnectionProvider(mockAccessor.Object, config);

            Assert.True(provider.IsLocalOnlyProduction);
        }

        [Fact]
        public void DbConnectionProvider_IsLocalOnlyProduction_ReturnsTrueWhenModeIsLocalOnlyProduction()
        {
            var config = CreateConfiguration(localOnlyProduction: false, mode: "LocalOnlyProduction");
            var mockAccessor = new Mock<IHttpContextAccessor>();
            var provider = new DbConnectionProvider(mockAccessor.Object, config);

            Assert.True(provider.IsLocalOnlyProduction);
        }

        [Theory]
        [InlineData("2026", "IProgramLocalDb2026")]
        [InlineData("2027", "IProgramLocalDb2027")]
        public void DbConnectionProvider_GetConnectionString_RoutesToLocalInLocalOnlyProduction(string dbId, string expectedCatalog)
        {
            var config = CreateConfiguration();
            var mockAccessor = new Mock<IHttpContextAccessor>();
            var context = new DefaultHttpContext();
            context.Request.Headers["X-Db-Selection"] = dbId;
            mockAccessor.Setup(a => a.HttpContext).Returns(context);

            var provider = new DbConnectionProvider(mockAccessor.Object, config);
            var connStr = provider.GetConnectionString();

            Assert.Contains(expectedCatalog, connStr);
            Assert.Contains("localhost", connStr, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("database.windows.net", connStr, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void DbConnectionProvider_GetRemoteConnectionString_ThrowsImmediatelyInLocalOnlyProduction()
        {
            var config = CreateConfiguration();
            var mockAccessor = new Mock<IHttpContextAccessor>();
            var provider = new DbConnectionProvider(mockAccessor.Object, config);

            var ex = Assert.Throws<InvalidOperationException>(() => provider.GetRemoteConnectionString("2026"));
            Assert.Contains("AZURE_REMOTE_DISABLED_IN_LOCAL_ONLY_PRODUCTION", ex.Message);
        }

        [Fact]
        public void DbConnectionProvider_GetManualSyncRemoteConnectionString_ThrowsImmediatelyInLocalOnlyProduction()
        {
            var config = CreateConfiguration();
            var mockAccessor = new Mock<IHttpContextAccessor>();
            var provider = new DbConnectionProvider(mockAccessor.Object, config);

            var ex = Assert.Throws<InvalidOperationException>(() => provider.GetManualSyncRemoteConnectionString("2026"));
            Assert.Contains("MANUAL_SYNC_REMOTE_DISABLED_IN_LOCAL_ONLY_PRODUCTION", ex.Message);
        }

        // =========================================================================
        // 2. ReadOnlyModeMiddleware Permitted Operations & Blocked Sync Routes
        // =========================================================================

        [Theory]
        [InlineData("POST", "/api/employees")]
        [InlineData("PUT", "/api/employees/1")]
        [InlineData("DELETE", "/api/employees/1")]
        [InlineData("POST", "/api/daily")]
        [InlineData("PUT", "/api/daily")]
        [InlineData("POST", "/api/form")]
        [InlineData("PUT", "/api/form/1")]
        [InlineData("POST", "/api/formdetails/AddEmployeeToFormDetails")]
        [InlineData("POST", "/api/department")]
        public async Task ReadOnlyModeMiddleware_PermitsBusinessWrites_InLocalOnlyProduction(string method, string path)
        {
            var config = CreateConfiguration();
            var nextInvoked = false;
            RequestDelegate next = (ctx) =>
            {
                nextInvoked = true;
                ctx.Response.StatusCode = 200;
                return Task.CompletedTask;
            };

            var middleware = new ReadOnlyModeMiddleware(next, config, NullLogger<ReadOnlyModeMiddleware>.Instance);
            var context = new DefaultHttpContext();
            context.Request.Method = method;
            context.Request.Path = path;

            await middleware.InvokeAsync(context);

            Assert.True(nextInvoked, $"Expected next to be invoked for {method} {path} in LocalOnlyProduction mode.");
            Assert.Equal(200, context.Response.StatusCode);
        }

        [Theory]
        [InlineData("POST", "/api/sync/pull")]
        [InlineData("POST", "/api/sync/push")]
        [InlineData("POST", "/api/sync/status/check-online")]
        [InlineData("POST", "/api/migration/sync")]
        [InlineData("POST", "/api/migration/pull")]
        public async Task ReadOnlyModeMiddleware_BlocksSyncEndpoints_InLocalOnlyProduction(string method, string path)
        {
            var config = CreateConfiguration();
            var nextInvoked = false;
            RequestDelegate next = (ctx) =>
            {
                nextInvoked = true;
                return Task.CompletedTask;
            };

            var middleware = new ReadOnlyModeMiddleware(next, config, NullLogger<ReadOnlyModeMiddleware>.Instance);
            var context = new DefaultHttpContext();
            context.Response.Body = new MemoryStream();
            context.Request.Method = method;
            context.Request.Path = path;

            await middleware.InvokeAsync(context);

            Assert.False(nextInvoked, $"Sync route {method} {path} should NOT pass through to next in LocalOnlyProduction.");
            Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);

            context.Response.Body.Seek(0, SeekOrigin.Begin);
            using var reader = new StreamReader(context.Response.Body);
            var body = await reader.ReadToEndAsync();
            var json = JsonDocument.Parse(body);
            Assert.Equal("SYNC_DISABLED_IN_LOCAL_ONLY_PRODUCTION", json.RootElement.GetProperty("code").GetString());
        }

        // =========================================================================
        // 3. LocalWriteSafetyInterceptor & AuthoritativeTrackingSafetyInterceptor
        // =========================================================================

        [Fact]
        public void LocalWriteSafetyInterceptor_DoesNotBlockWrites_InLocalOnlyProduction()
        {
            var mockProvider = new Mock<ISyncConnectionProvider>();
            mockProvider.Setup(p => p.IsLocalFirstEnabled).Returns(true);
            mockProvider.Setup(p => p.IsReadOnlyMode).Returns(false);
            mockProvider.Setup(p => p.IsLocalOnlyProduction).Returns(true);

            var interceptor = new LocalWriteSafetyInterceptor(mockProvider.Object);

            var options = new DbContextOptionsBuilder<ApplicationContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .AddInterceptors(interceptor)
                .Options;

            using var context = new ApplicationContext(options);
            // Adding an Employee (which would be blocked in OfflineReadWritePilot if not Daily)
            context.Employees.Add(new Employee
            {
                Id = "12345678901234",
                Name = "Local Employee",
                Collage = "Engineering",
                Section = "IT"
            });

            // SaveChanges should succeed without throwing OfflineWriteScopeException
            var count = context.SaveChanges();
            Assert.Equal(1, count);
        }

        [Fact]
        public async Task UnitOfWork_DirectLocalPersistence_ZeroOutboxInLocalOnlyProduction()
        {
            var mockProvider = new Mock<ISyncConnectionProvider>();
            mockProvider.Setup(p => p.IsLocalFirstEnabled).Returns(true);
            mockProvider.Setup(p => p.IsReadOnlyMode).Returns(false);
            mockProvider.Setup(p => p.IsLocalOnlyProduction).Returns(true);

            var options = new DbContextOptionsBuilder<ApplicationContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .Options;

            await using var context = new ApplicationContext(options);

            var config = CreateConfiguration();
            var uow = new UnitOfWork(context, mockProvider.Object, configuration: config);

            // Add an employee
            context.Employees.Add(new Employee
            {
                Id = "12345678901235",
                Name = "Local Production Worker",
                Collage = "Operations",
                Section = "Maintenance"
            });

            var saved = await uow.SaveChangesAsync();
            Assert.Equal(1, saved);

            // In contrast, in standard OfflineReadWritePilot (where IsLocalOnlyProduction = false),
            // mutating an Employee is blocked by OfflineWriteScopeException
            var pilotProvider = new Mock<ISyncConnectionProvider>();
            pilotProvider.Setup(p => p.IsLocalFirstEnabled).Returns(true);
            pilotProvider.Setup(p => p.IsReadOnlyMode).Returns(false);
            pilotProvider.Setup(p => p.IsLocalOnlyProduction).Returns(false);

            var pilotUow = new UnitOfWork(context, pilotProvider.Object, configuration: config);
            context.Employees.Add(new Employee
            {
                Id = "99999999999999",
                Name = "Blocked Pilot Worker",
                Collage = "Operations",
                Section = "Maintenance"
            });

            await Assert.ThrowsAsync<OfflineWriteScopeException>(() => pilotUow.SaveChangesAsync());
        }

        // =========================================================================
        // 4. SyncController Fail-Closed Guards in LocalOnlyProduction
        // =========================================================================

        [Fact]
        public async Task SyncController_PushOutbox_Returns403InLocalOnlyProduction()
        {
            var mockPush = new Mock<ILocalOutboxPushService>();
            var mockPull = new Mock<ILocalDailyPullService>();
            var mockProvider = new Mock<ISyncConnectionProvider>();
            mockProvider.Setup(p => p.IsLocalOnlyProduction).Returns(true);
            var mockBaseline = new Mock<ILocalScopeBaselineService>();
            var mockStatus = new Mock<ISyncStatusService>();
            var config = CreateConfiguration();

            var controller = new SyncController(
                mockPush.Object, mockPull.Object, mockProvider.Object,
                config, NullLogger<SyncController>.Instance,
                mockBaseline.Object, mockStatus.Object);

            var result = await controller.PushOutbox(CancellationToken.None);

            var statusResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(StatusCodes.Status403Forbidden, statusResult.StatusCode);
            mockPush.Verify(p => p.PushPendingOutboxAsync(It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Never);
        }

        [Fact]
        public async Task SyncController_PullDaily_Returns403InLocalOnlyProduction()
        {
            var mockPush = new Mock<ILocalOutboxPushService>();
            var mockPull = new Mock<ILocalDailyPullService>();
            var mockProvider = new Mock<ISyncConnectionProvider>();
            mockProvider.Setup(p => p.IsLocalOnlyProduction).Returns(true);
            var mockBaseline = new Mock<ILocalScopeBaselineService>();
            var mockStatus = new Mock<ISyncStatusService>();
            var config = CreateConfiguration();

            var controller = new SyncController(
                mockPush.Object, mockPull.Object, mockProvider.Object,
                config, NullLogger<SyncController>.Instance,
                mockBaseline.Object, mockStatus.Object);

            var result = await controller.PullDaily(CancellationToken.None);

            var statusResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(StatusCodes.Status403Forbidden, statusResult.StatusCode);
            mockPull.Verify(p => p.PullDailyChangesAsync(It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Never);
        }

        [Fact]
        public async Task SyncController_CheckOnlineStatus_Returns403InLocalOnlyProduction()
        {
            var mockPush = new Mock<ILocalOutboxPushService>();
            var mockPull = new Mock<ILocalDailyPullService>();
            var mockProvider = new Mock<ISyncConnectionProvider>();
            mockProvider.Setup(p => p.IsLocalOnlyProduction).Returns(true);
            var mockBaseline = new Mock<ILocalScopeBaselineService>();
            var mockStatus = new Mock<ISyncStatusService>();
            var config = CreateConfiguration();

            var controller = new SyncController(
                mockPush.Object, mockPull.Object, mockProvider.Object,
                config, NullLogger<SyncController>.Instance,
                mockBaseline.Object, mockStatus.Object);

            var result = await controller.CheckOnlineStatus(CancellationToken.None);

            var statusResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(StatusCodes.Status403Forbidden, statusResult.StatusCode);
            mockStatus.Verify(s => s.CheckOnlineStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        // =========================================================================
        // 5. AccountController Runtime Status
        // =========================================================================

        [Fact]
        public void AccountController_GetRuntimeStatus_ReturnsLocalOnlyProductionMode()
        {
            var mockProvider = new Mock<ISyncConnectionProvider>();
            mockProvider.Setup(p => p.IsLocalOnlyProduction).Returns(true);
            mockProvider.Setup(p => p.IsReadOnlyMode).Returns(false);
            mockProvider.Setup(p => p.IsLocalFirstEnabled).Returns(true);
            mockProvider.Setup(p => p.GetSelectedDatabaseId()).Returns("2026");

            var controller = new AccountController(null!, null!, null!, null!, null!, mockProvider.Object);

            var result = controller.GetRuntimeStatus();

            var okResult = Assert.IsType<OkObjectResult>(result);
            var json = JsonSerializer.Serialize(okResult.Value);
            var parsed = JsonDocument.Parse(json);

            Assert.Equal("LocalOnlyProduction", parsed.RootElement.GetProperty("runtimeMode").GetString());
            Assert.True(parsed.RootElement.GetProperty("isLocalOnlyProduction").GetBoolean());
            Assert.False(parsed.RootElement.GetProperty("isReadOnly").GetBoolean());
            Assert.True(parsed.RootElement.GetProperty("isLocalFirst").GetBoolean());
            Assert.Equal("2026", parsed.RootElement.GetProperty("selectedDatabase").GetString());
        }

        // =========================================================================
        // 6. SyncStatusService CheckOnlineStatusAsync Fail-Closed
        // =========================================================================

        [Fact]
        public async Task SyncStatusService_CheckOnlineStatusAsync_ThrowsInLocalOnlyProduction()
        {
            var mockProvider = new Mock<ISyncConnectionProvider>();
            mockProvider.Setup(p => p.IsLocalOnlyProduction).Returns(true);
            var mockRemoteConnFactory = new Mock<IRemoteDatabaseConnectionFactory>();
            var mockBaselineService = new Mock<ILocalScopeBaselineService>();

            var service = new SyncStatusService(
                mockProvider.Object,
                mockRemoteConnFactory.Object,
                mockBaselineService.Object,
                NullLogger<SyncStatusService>.Instance);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.CheckOnlineStatusAsync("2026"));

            Assert.Contains("ONLINE_CHECK_DISABLED_IN_LOCAL_ONLY_PRODUCTION", ex.Message);
        }

        // =========================================================================
        // 7. Non-DB Outbound Network Zero-Remote Acceptance (File Storage / CDN)
        // =========================================================================

        [Fact]
        public async Task LocalOnlyProduction_CloudinaryDownload_Uncached_ReturnsNull_ZeroOutboundHttp()
        {
            var config = CreateConfiguration(localOnlyProduction: true);
            var service = new CloudinaryService(config);

            // An uncached remote reference in LocalOnlyProduction must immediately return null without making any outbound HTTP/CDN call
            var result = await service.DownloadFileStreamAsync("https://res.cloudinary.com/dummy/raw/authenticated/DailyReferences/uncached_remote.pdf", "DailyReferences");

            Assert.Null(result);
        }

        [Fact]
        public async Task LocalOnlyProduction_CloudinaryUpload_ThrowsOfflineWriteScopeException_ZeroOutboundHttp()
        {
            var config = CreateConfiguration(localOnlyProduction: true);
            var service = new CloudinaryService(config);

            using var ms = new MemoryStream(new byte[] { 1, 2, 3 });
            var ex = await Assert.ThrowsAsync<OfflineWriteScopeException>(
                () => service.UploadFileAsync(ms, "sample.pdf", "DailyReferences"));

            Assert.Contains("رفع المرفقات إلى التخزين السحابي معطل في الوضع المحلي", ex.Message);
        }

        [Fact]
        public async Task LocalOnlyProduction_CloudinaryDelete_ThrowsOfflineWriteScopeException_ZeroOutboundHttp()
        {
            var config = CreateConfiguration(localOnlyProduction: true);
            var service = new CloudinaryService(config);

            var ex = await Assert.ThrowsAsync<OfflineWriteScopeException>(
                () => service.DeleteFileAsync("https://res.cloudinary.com/dummy/raw/authenticated/DailyReferences/sample.pdf", "DailyReferences"));

            Assert.Contains("حذف المرفقات من التخزين السحابي معطل في الوضع المحلي", ex.Message);
        }
    }
}
