#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Auth.Api.Middleware;
using Auth.Infrastructure;
using Auth.Infrastructure.Services;
using Auth.Infrastructure.Sync;
using Core.Exceptions;
using Core.Interfaces;
using Core.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Persistence.Repository;
using Persistence.Services;
using Xunit;

namespace Auth.UnitTests
{
    public class OfflineWritePilotRuntimeTests
    {
        private IConfiguration CreateConfiguration(bool localFirstEnabled, bool readOnlyMode, bool includeCloudinary = true)
        {
            var inMemorySettings = new Dictionary<string, string?>
            {
                { "ConnectionStrings:DefaultConnection", "Server=test-azure.database.windows.net;Database=IProgramDb2026;User ID=u;Password=p;" },
                { "ConnectionStrings:CON2027", "Server=test-azure.database.windows.net;Database=IProgramDb2027;User ID=u;Password=p;" },
                { "ConnectionStrings:LocalConnection2026", "Server=localhost;Database=IProgramLocalDb2026;Trusted_Connection=True;TrustServerCertificate=True;" },
                { "ConnectionStrings:LocalConnection2027", "Server=localhost;Database=IProgramLocalDb2027;Trusted_Connection=True;TrustServerCertificate=True;" },
                { "DatabaseSettings:Databases:0:Id", "2026" },
                { "DatabaseSettings:Databases:0:Name", "بيانات 2026" },
                { "DatabaseSettings:Databases:0:ConnectionStringName", "DefaultConnection" },
                { "DatabaseSettings:Databases:1:Id", "2027" },
                { "DatabaseSettings:Databases:1:Name", "بيانات 2027" },
                { "DatabaseSettings:Databases:1:ConnectionStringName", "CON2027" },
                { "LocalFirst:Enabled", localFirstEnabled.ToString().ToLowerInvariant() },
                { "LocalFirst:ReadOnlyMode", readOnlyMode.ToString().ToLowerInvariant() },
                { "LocalFirst:SqlServerInstance", "localhost" },
                { "LocalFirst:Databases:0:Id", "2026" },
                { "LocalFirst:Databases:0:LocalDatabaseName", "IProgramLocalDb2026" },
                { "LocalFirst:Databases:0:LocalConnectionStringName", "LocalConnection2026" },
                { "LocalFirst:Databases:1:Id", "2027" },
                { "LocalFirst:Databases:1:LocalDatabaseName", "IProgramLocalDb2027" },
                { "LocalFirst:Databases:1:LocalConnectionStringName", "LocalConnection2027" }
            };

            if (includeCloudinary)
            {
                inMemorySettings["Cloudinary:CloudName"] = "test-cloud";
                inMemorySettings["Cloudinary:ApiKey"] = "test-key";
                inMemorySettings["Cloudinary:ApiSecret"] = "test-secret";
            }

            return new ConfigurationBuilder()
                .AddInMemoryCollection(inMemorySettings)
                .Build();
        }

        [Theory]
        [InlineData(false, false, "Online")]
        [InlineData(false, true, "OfflineReadOnly")]
        [InlineData(true, true, "OfflineReadOnly")]
        [InlineData(true, false, "OfflineReadWritePilot")]
        public void RuntimeStatus_ResolvesExpectedMode(bool localFirst, bool readOnly, string expectedMode)
        {
            var config = CreateConfiguration(localFirst, readOnly);
            var mockAccessor = new Mock<IHttpContextAccessor>();
            var provider = new DbConnectionProvider(mockAccessor.Object, config);

            string runtimeMode;
            if (provider.IsReadOnlyMode)
            {
                runtimeMode = "OfflineReadOnly";
            }
            else if (provider.IsLocalFirstEnabled)
            {
                runtimeMode = "OfflineReadWritePilot";
            }
            else
            {
                runtimeMode = "Online";
            }

            Assert.Equal(expectedMode, runtimeMode);
        }

        [Theory]
        [InlineData("POST", "/api/Daily", true)]
        [InlineData("POST", "/api/daily", true)]
        [InlineData("PUT", "/api/Daily", true)]
        [InlineData("PUT", "/api/daily", true)]
        [InlineData("PUT", "/api/Daily/CloseDaily/1", true)]
        [InlineData("PUT", "/api/daily/closedaily/99", true)]
        [InlineData("PUT", "/api/Daily/UncloseDaily/1", true)]
        [InlineData("PUT", "/api/daily/unclosedaily/55", true)]
        [InlineData("DELETE", "/api/Daily/12", true)]
        [InlineData("DELETE", "/api/daily/12", true)]
        [InlineData("DELETE", "/api/Daily/softdelete/12", true)]
        [InlineData("DELETE", "/api/daily/softdelete/12", true)]
        // Permitted Forms write routes in Phase 3
        [InlineData("POST", "/api/Form", true)]
        [InlineData("POST", "/api/form", true)]
        [InlineData("PUT", "/api/Form/1", true)]
        [InlineData("PUT", "/api/form/1", true)]
        [InlineData("PUT", "/api/form/MoveFormDailyArchives", true)]
        [InlineData("PUT", "/api/form/hide-form/1", true)]
        [InlineData("PUT", "/api/form/restore-form/1", true)]
        [InlineData("PUT", "/api/form/UpdateDescription/1", true)]
        [InlineData("DELETE", "/api/form/SoftDelete/1", true)]
        [InlineData("DELETE", "/api/form/1", true)]
        [InlineData("POST", "/api/form/upload-excel-form", true)]
        [InlineData("POST", "/api/form/upload-json-form", true)]
        [InlineData("GET", "/api/form/CopyFormToArchive/1", true)]
        [InlineData("POST", "/api/formdetails/AddEmployeeToFormDetails", true)]
        [InlineData("PUT", "/api/formdetails/EditEmployeeToFormDetails", true)]
        [InlineData("PUT", "/api/formdetails/reOrderRows/1", true)]
        [InlineData("PUT", "/api/formdetails/MarkAsReviewed/1", true)]
        [InlineData("PUT", "/api/formdetails/MarkAsSummaryReviewed/1", true)]
        [InlineData("DELETE", "/api/formdetails/1", true)]
        [InlineData("PUT", "/api/formarchived/MoveFormArchiveToDaily", true)]
        [InlineData("DELETE", "/api/formarchived/1", true)]
        [InlineData("POST", "/api/formarchived/deleteMultiForms", true)]
        // Disallowed mutating routes in OfflineReadWritePilot
        [InlineData("POST", "/api/Daily/copy/1", false)]
        [InlineData("PUT", "/api/Daily/1/beneficiary-comment", false)]
        [InlineData("PUT", "/api/Daily/1/beneficiary-netpay", false)]
        [InlineData("POST", "/api/Daily/1/verify-pdf", false)]
        [InlineData("POST", "/api/Daily/1/reset-reviews", false)]
        [InlineData("POST", "/api/Employee", false)]
        [InlineData("PUT", "/api/Employee", false)]
        [InlineData("DELETE", "/api/Employee/1", false)]
        [InlineData("PUT", "/api/Form", false)]
        [InlineData("POST", "/api/FormReferences", false)]
        [InlineData("DELETE", "/api/FormReferences/1", false)]
        [InlineData("PUT", "/api/account/ChangePassword", false)]
        [InlineData("POST", "/api/account/register", false)]
        public void OfflineWritePilotRouteAllowlist_MatchesExpectedPermission(string method, string path, bool shouldAllow)
        {
            var isAllowed = ReadOnlyModeMiddleware.IsPermittedOfflineWritePilotRoute(method, path);
            Assert.Equal(shouldAllow, isAllowed);
        }

        [Fact]
        public async Task ReadOnlyModeMiddleware_OfflineWritePilot_BlocksDisallowedMutationsWithErrorCode()
        {
            var config = CreateConfiguration(localFirstEnabled: true, readOnlyMode: false);
            var middleware = new ReadOnlyModeMiddleware(
                next: (innerHttpContext) => Task.CompletedTask,
                configuration: config,
                logger: NullLogger<ReadOnlyModeMiddleware>.Instance);

            var context = new DefaultHttpContext();
            context.Request.Method = "POST";
            context.Request.Path = "/api/Daily/copy/42";
            context.Response.Body = new MemoryStream();

            await middleware.InvokeAsync(context);

            Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
            context.Response.Body.Seek(0, SeekOrigin.Begin);
            using var reader = new StreamReader(context.Response.Body);
            var responseJson = await reader.ReadToEndAsync();
            Assert.Contains("OFFLINE_WRITE_SCOPE_BLOCKED", responseJson);
        }

        [Fact]
        public async Task ReadOnlyModeMiddleware_OfflineWritePilot_AllowsPermittedDailyOperation()
        {
            var config = CreateConfiguration(localFirstEnabled: true, readOnlyMode: false);
            bool nextInvoked = false;
            var middleware = new ReadOnlyModeMiddleware(
                next: (innerHttpContext) =>
                {
                    nextInvoked = true;
                    return Task.CompletedTask;
                },
                configuration: config,
                logger: NullLogger<ReadOnlyModeMiddleware>.Instance);

            var context = new DefaultHttpContext();
            context.Request.Method = "POST";
            context.Request.Path = "/api/Daily";

            await middleware.InvokeAsync(context);

            Assert.True(nextInvoked);
            Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        }

        [Fact]
        public void LocalWriteSafetyInterceptor_DirectSaveChanges_ThrowsOfflineWriteScopeException()
        {
            var mockSyncProvider = new Mock<ISyncConnectionProvider>();
            mockSyncProvider.Setup(p => p.IsLocalFirstEnabled).Returns(true);
            mockSyncProvider.Setup(p => p.IsReadOnlyMode).Returns(false);
            mockSyncProvider.Setup(p => p.GetSelectedDatabaseId()).Returns("2026");

            var interceptor = new LocalWriteSafetyInterceptor(mockSyncProvider.Object);
            var options = new DbContextOptionsBuilder<ApplicationContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .AddInterceptors(interceptor)
                .Options;

            using var context = new ApplicationContext(options);
            context.Set<Daily>().Add(new Daily
            {
                Name = "Daily Direct Test",
                DailyDate = DateTime.UtcNow
            });

            var ex = Assert.Throws<OfflineWriteScopeException>(() => context.SaveChanges());
            Assert.Contains("Direct SaveChanges", ex.Message);
        }

        [Fact]
        public void LocalWriteSafetyInterceptor_NonDailyMutation_ThrowsOfflineWriteScopeException()
        {
            var mockSyncProvider = new Mock<ISyncConnectionProvider>();
            mockSyncProvider.Setup(p => p.IsLocalFirstEnabled).Returns(true);
            mockSyncProvider.Setup(p => p.IsReadOnlyMode).Returns(false);
            mockSyncProvider.Setup(p => p.GetSelectedDatabaseId()).Returns("2026");

            var interceptor = new LocalWriteSafetyInterceptor(mockSyncProvider.Object);
            var options = new DbContextOptionsBuilder<ApplicationContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .AddInterceptors(interceptor)
                .Options;

            using var context = new ApplicationContext(options);
            using var scope = LocalWriteScopeContext.BeginScope();

            context.Departments.Add(new Department { Name = "Blocked Department", SyncId = Guid.NewGuid() });

            var ex = Assert.Throws<OfflineWriteScopeException>(() => context.SaveChanges());
            Assert.Contains("Daily فقط", ex.Message);
        }

        [Fact]
        public void LocalWriteSafetyInterceptor_HardDelete_ThrowsOfflineWriteScopeException()
        {
            var mockSyncProvider = new Mock<ISyncConnectionProvider>();
            mockSyncProvider.Setup(p => p.IsLocalFirstEnabled).Returns(true);
            mockSyncProvider.Setup(p => p.IsReadOnlyMode).Returns(false);
            mockSyncProvider.Setup(p => p.GetSelectedDatabaseId()).Returns("2026");

            var interceptor = new LocalWriteSafetyInterceptor(mockSyncProvider.Object);
            var options = new DbContextOptionsBuilder<ApplicationContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .AddInterceptors(interceptor)
                .Options;

            using var context = new ApplicationContext(options);
            var daily = new Daily { Id = 1, Name = "Daily Hard Delete", DailyDate = DateTime.UtcNow };

            using (var scope = LocalWriteScopeContext.BeginScope())
            {
                context.Set<Daily>().Attach(daily);
                context.Set<Daily>().Remove(daily);

                var ex = Assert.Throws<OfflineWriteScopeException>(() => context.SaveChanges());
                Assert.Contains("Hard Delete", ex.Message);
            }
        }

        [Fact]
        public void DeterministicPayloadEnvelope_ProducesConsistentOrderedJson()
        {
            var deviceId = Guid.Parse("11111111-2222-3333-4444-555555555555");
            var syncId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
            var timestamp = new DateTime(2026, 9, 20, 18, 0, 0, DateTimeKind.Utc);

            var daily = new Daily
            {
                Id = 42,
                Name = "يومية اختبارية",
                DailyDate = new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc),
                Closed = false,
                SyncId = syncId,
                CreatedBy = "Admin",
                CreatedAt = timestamp,
                IsActive = true
            };

            var json1 = UnitOfWork.BuildDeterministicPayloadJson("INSERT", "2026", deviceId, 5, daily, timestamp);
            var json2 = UnitOfWork.BuildDeterministicPayloadJson("INSERT", "2026", deviceId, 5, daily, timestamp);

            Assert.Equal(json1, json2);

            using var doc = JsonDocument.Parse(json1);
            var root = doc.RootElement;
            Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("INSERT", root.GetProperty("operationType").GetString());
            Assert.Equal("2026", root.GetProperty("databaseId").GetString());
            Assert.Equal(deviceId.ToString(), root.GetProperty("deviceId").GetString());
            Assert.Equal(5, root.GetProperty("baseServerVersion").GetInt64());
            Assert.Equal("Daily", root.GetProperty("entityType").GetString());
            Assert.Equal(syncId.ToString(), root.GetProperty("entitySyncId").GetString());

            var entityData = root.GetProperty("entityData");
            Assert.Equal(42, entityData.GetProperty("Id").GetInt32());
            Assert.Equal("يومية اختبارية", entityData.GetProperty("Name").GetString());
            Assert.True(entityData.GetProperty("IsActive").GetBoolean());
            Assert.False(entityData.TryGetProperty("Forms", out _));
            Assert.False(entityData.TryGetProperty("DailyReferences", out _));
        }

        [Fact]
        public async Task ReadOnlyDbConnectionInterceptor_OfflineWritePilot_EnforcesLocalOnly()
        {
            var mockSyncProvider = new Mock<ISyncConnectionProvider>();
            mockSyncProvider.Setup(p => p.IsLocalFirstEnabled).Returns(true);
            mockSyncProvider.Setup(p => p.IsReadOnlyMode).Returns(false);

            var interceptor = new ReadOnlyDbConnectionInterceptor(mockSyncProvider.Object);

            var options = new DbContextOptionsBuilder<ApplicationContext>()
                .UseSqlServer("Server=test-remote.database.windows.net;Database=IProgramLocalDb2026;User ID=u;Password=p;")
                .AddInterceptors(interceptor)
                .Options;

            using var context = new ApplicationContext(options);
            await Assert.ThrowsAsync<ReadOnlyModeException>(async () =>
            {
                await context.Database.OpenConnectionAsync();
            });
        }

        [Fact]
        public async Task CloudinaryService_OfflineWritePilot_BlocksOutboundOperations()
        {
            var config = CreateConfiguration(localFirstEnabled: true, readOnlyMode: false);
            var service = new CloudinaryService(config);

            using var ms = new MemoryStream(new byte[] { 1, 2, 3 });
            await Assert.ThrowsAsync<OfflineWriteScopeException>(async () =>
            {
                await service.UploadFileAsync(ms, "test.pdf");
            });

            await Assert.ThrowsAsync<OfflineWriteScopeException>(async () =>
            {
                await service.DeleteFileAsync("https://res.cloudinary.com/test/raw/upload/test.pdf", "DailyReferences");
            });

            var downloadResult = await service.DownloadFileStreamAsync("https://res.cloudinary.com/test/raw/upload/nonexistent.pdf", "DailyReferences");
            Assert.Null(downloadResult);
        }

        [Fact]
        public async Task SeedData_LocalFirstEnabled_SkipsRoleCreation()
        {
            var mockRoleStore = new Mock<IRoleStore<IdentityRole>>();
            var mockRoleMgr = new Mock<RoleManager<IdentityRole>>(
                mockRoleStore.Object, null!, null!, null!, null!);

            mockRoleMgr.Setup(r => r.RoleExistsAsync(It.IsAny<string>()))
                .ReturnsAsync(false);

            await SeedData.EnsureSeedData(mockRoleMgr.Object, skipRoleCreation: true);

            mockRoleMgr.Verify(r => r.CreateAsync(It.IsAny<IdentityRole>()), Times.Never);
        }
    }
}
