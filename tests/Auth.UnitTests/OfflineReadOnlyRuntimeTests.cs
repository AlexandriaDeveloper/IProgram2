using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Auth.Api.Middleware;
using Auth.Infrastructure;
using Auth.Infrastructure.Services;
using Auth.Infrastructure.Sync;
using Core.Configuration;
using Core.Exceptions;
using Core.Interfaces;
using Core.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Auth.UnitTests
{
    public class OfflineReadOnlyRuntimeTests
    {
        private IConfiguration CreateConfiguration(bool readOnlyMode, bool localFirstEnabled = false)
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
                { "LocalFirst:Databases:1:LocalConnectionStringName", "LocalConnection2027" },
                { "Cloudinary:CloudName", "test-cloud" },
                { "Cloudinary:ApiKey", "test-key" },
                { "Cloudinary:ApiSecret", "test-secret" }
            };

            return new ConfigurationBuilder()
                .AddInMemoryCollection(inMemorySettings)
                .Build();
        }

        #region Connection Routing & Fail-Closed Tests

        [Fact]
        public void DbConnectionProvider_RoutesToLocal_When_ReadOnlyMode_IsTrue_And_LocalFirst_IsFalse()
        {
            var config = CreateConfiguration(readOnlyMode: true, localFirstEnabled: false);
            var httpContextAccessor = new Mock<IHttpContextAccessor>();
            var context = new DefaultHttpContext();
            context.Request.Headers["X-Db-Selection"] = "2027";
            httpContextAccessor.Setup(h => h.HttpContext).Returns(context);

            var provider = new DbConnectionProvider(httpContextAccessor.Object, config);

            Assert.True(provider.IsReadOnlyMode);
            Assert.False(provider.IsLocalFirstEnabled);

            var connStr = provider.GetConnectionString();
            Assert.Contains("Database=IProgramLocalDb2027", connStr);
            Assert.Contains("Server=localhost", connStr);
            Assert.DoesNotContain("database.windows.net", connStr);
        }

        [Fact]
        public void DbConnectionProvider_Preserves_AzureRouting_When_BothFlags_AreFalse()
        {
            var config = CreateConfiguration(readOnlyMode: false, localFirstEnabled: false);
            var httpContextAccessor = new Mock<IHttpContextAccessor>();
            var context = new DefaultHttpContext();
            context.Request.Headers["X-Db-Selection"] = "2026";
            httpContextAccessor.Setup(h => h.HttpContext).Returns(context);

            var provider = new DbConnectionProvider(httpContextAccessor.Object, config);

            Assert.False(provider.IsReadOnlyMode);
            Assert.False(provider.IsLocalFirstEnabled);

            var connStr = provider.GetConnectionString();
            Assert.Contains("Database=IProgramDb2026", connStr);
            Assert.Contains("test-azure.database.windows.net", connStr);
        }

        [Fact]
        public void DbConnectionProvider_Enforces_YearIsolation_In_ReadOnlyMode()
        {
            var config = CreateConfiguration(readOnlyMode: true, localFirstEnabled: false);
            var httpContextAccessor = new Mock<IHttpContextAccessor>();
            
            // 2026 Request
            var context2026 = new DefaultHttpContext();
            context2026.Request.Headers["X-Db-Selection"] = "2026";
            httpContextAccessor.Setup(h => h.HttpContext).Returns(context2026);
            var provider2026 = new DbConnectionProvider(httpContextAccessor.Object, config);
            Assert.Equal("2026", provider2026.GetSelectedDatabaseId());
            Assert.Contains("IProgramLocalDb2026", provider2026.GetConnectionString());

            // 2027 Request
            var context2027 = new DefaultHttpContext();
            context2027.Request.Headers["X-Db-Selection"] = "2027";
            httpContextAccessor.Setup(h => h.HttpContext).Returns(context2027);
            var provider2027 = new DbConnectionProvider(httpContextAccessor.Object, config);
            Assert.Equal("2027", provider2027.GetSelectedDatabaseId());
            Assert.Contains("IProgramLocalDb2027", provider2027.GetConnectionString());
        }

        #endregion

        #region HTTP Middleware Write-Rejection Tests

        [Theory]
        [InlineData("POST", "/api/daily")]
        [InlineData("PUT", "/api/form/123")]
        [InlineData("DELETE", "/api/employee/456")]
        [InlineData("PATCH", "/api/department/1")]
        [InlineData("POST", "/api/account/register")]
        [InlineData("PUT", "/api/account/ChangePassword")]
        [InlineData("POST", "/api/DailyReferences/upload")]
        public async Task ReadOnlyModeMiddleware_RejectsMutatingRequests_With403_When_ReadOnlyMode_IsTrue(string method, string path)
        {
            var config = CreateConfiguration(readOnlyMode: true);
            bool nextCalled = false;
            RequestDelegate next = (ctx) =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            };

            var middleware = new ReadOnlyModeMiddleware(next, config, NullLogger<ReadOnlyModeMiddleware>.Instance);
            var context = new DefaultHttpContext();
            context.Request.Method = method;
            context.Request.Path = path;
            context.Response.Body = new MemoryStream();

            await middleware.InvokeAsync(context);

            Assert.False(nextCalled, "Mutating request must NOT reach controller");
            Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);

            context.Response.Body.Seek(0, SeekOrigin.Begin);
            var responseBody = await new StreamReader(context.Response.Body).ReadToEndAsync();
            Assert.Contains("READ_ONLY_MODE_BLOCKED", responseBody);
            Assert.Contains("وضع القراءة المحلية فقط", responseBody);
        }

        [Fact]
        public async Task ReadOnlyModeMiddleware_Allows_LoginEndpoint_When_ReadOnlyMode_IsTrue()
        {
            var config = CreateConfiguration(readOnlyMode: true);
            bool nextCalled = false;
            RequestDelegate next = (ctx) =>
            {
                nextCalled = true;
                ctx.Response.StatusCode = StatusCodes.Status200OK;
                return Task.CompletedTask;
            };

            var middleware = new ReadOnlyModeMiddleware(next, config, NullLogger<ReadOnlyModeMiddleware>.Instance);
            var context = new DefaultHttpContext();
            context.Request.Method = "POST";
            context.Request.Path = "/api/account/login";

            await middleware.InvokeAsync(context);

            Assert.True(nextCalled, "POST /api/account/login must be allowed to authenticate against local Identity");
            Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        }

        [Theory]
        [InlineData("GET", "/api/daily")]
        [InlineData("GET", "/api/form")]
        [InlineData("GET", "/api/employee/123")]
        [InlineData("GET", "/api/account/databases")]
        [InlineData("GET", "/api/account/runtime-status")]
        [InlineData("HEAD", "/api/daily")]
        [InlineData("OPTIONS", "/api/form")]
        public async Task ReadOnlyModeMiddleware_Allows_SafeVerbs_When_ReadOnlyMode_IsTrue(string method, string path)
        {
            var config = CreateConfiguration(readOnlyMode: true);
            bool nextCalled = false;
            RequestDelegate next = (ctx) =>
            {
                nextCalled = true;
                ctx.Response.StatusCode = StatusCodes.Status200OK;
                return Task.CompletedTask;
            };

            var middleware = new ReadOnlyModeMiddleware(next, config, NullLogger<ReadOnlyModeMiddleware>.Instance);
            var context = new DefaultHttpContext();
            context.Request.Method = method;
            context.Request.Path = path;

            await middleware.InvokeAsync(context);

            Assert.True(nextCalled, $"{method} {path} must be allowed in read-only mode");
            Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        }

        [Fact]
        public async Task ReadOnlyModeMiddleware_PassesAllRequests_When_ReadOnlyMode_IsFalse()
        {
            var config = CreateConfiguration(readOnlyMode: false);
            bool nextCalled = false;
            RequestDelegate next = (ctx) =>
            {
                nextCalled = true;
                ctx.Response.StatusCode = StatusCodes.Status200OK;
                return Task.CompletedTask;
            };

            var middleware = new ReadOnlyModeMiddleware(next, config, NullLogger<ReadOnlyModeMiddleware>.Instance);
            var context = new DefaultHttpContext();
            context.Request.Method = "POST";
            context.Request.Path = "/api/daily";

            await middleware.InvokeAsync(context);

            Assert.True(nextCalled, "All requests pass when ReadOnlyMode is false");
            Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        }

        #endregion

        #region SaveChanges Interceptor Tests

        [Fact]
        public void LocalBootstrapWriteGateInterceptor_ThrowsReadOnlyModeException_OnSaveChanges_WhenChangesExist()
        {
            var mockSyncProvider = new Mock<ISyncConnectionProvider>();
            mockSyncProvider.Setup(p => p.IsReadOnlyMode).Returns(true);
            mockSyncProvider.Setup(p => p.GetSelectedDatabaseId()).Returns("2027");

            var mockWriteGate = new Mock<ILocalBootstrapWriteGate>();

            var interceptor = new LocalBootstrapWriteGateInterceptor(mockSyncProvider.Object, mockWriteGate.Object);

            var options = new DbContextOptionsBuilder<ApplicationContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .AddInterceptors(interceptor)
                .Options;

            using var context = new ApplicationContext(options);
            context.Departments.Add(new Department { Id = 999, Name = "Test Dep" });

            var ex = Assert.Throws<ReadOnlyModeException>(() => context.SaveChanges());
            Assert.Contains("وضع القراءة المحلية فقط", ex.Message);
        }

        [Fact]
        public void LocalBootstrapWriteGateInterceptor_AllowsSaveChanges_WhenNoChangesExist()
        {
            var mockSyncProvider = new Mock<ISyncConnectionProvider>();
            mockSyncProvider.Setup(p => p.IsReadOnlyMode).Returns(true);
            mockSyncProvider.Setup(p => p.GetSelectedDatabaseId()).Returns("2026");

            var mockWriteGate = new Mock<ILocalBootstrapWriteGate>();

            var interceptor = new LocalBootstrapWriteGateInterceptor(mockSyncProvider.Object, mockWriteGate.Object);

            var options = new DbContextOptionsBuilder<ApplicationContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .AddInterceptors(interceptor)
                .Options;

            using var context = new ApplicationContext(options);
            // No changes tracked
            var result = context.SaveChanges();
            Assert.Equal(0, result);
        }

        #endregion

        #region Raw SQL DbCommand Interceptor Tests

        [Theory]
        [InlineData("INSERT INTO Employees (Id, Name) VALUES ('1', 'test')")]
        [InlineData("UPDATE FormDetails SET Amount = 100 WHERE Id = 1")]
        [InlineData("DELETE FROM EmployeeBank WHERE EmployeeId = '1'")]
        [InlineData("ALTER TABLE FormDetails NOCHECK CONSTRAINT ALL")]
        [InlineData("DROP TABLE SomeTable")]
        [InlineData("TRUNCATE TABLE SomeTable")]
        [InlineData("MERGE INTO TargetTable USING SourceTable ON 1=1")]
        public async Task ReadOnlyDbCommandInterceptor_ThrowsReadOnlyModeException_OnMutatingSql(string mutatingSql)
        {
            var mockSyncProvider = new Mock<ISyncConnectionProvider>();
            mockSyncProvider.Setup(p => p.IsReadOnlyMode).Returns(true);

            var interceptor = new ReadOnlyDbCommandInterceptor(mockSyncProvider.Object);

            var mockCommand = new Mock<DbCommand>();
            mockCommand.SetupGet(c => c.CommandText).Returns(mutatingSql);

            var ex = Assert.Throws<ReadOnlyModeException>(() =>
                interceptor.NonQueryExecuting(mockCommand.Object, null!, default));
            Assert.Contains("وضع القراءة المحلية فقط", ex.Message);

            await Assert.ThrowsAsync<ReadOnlyModeException>(async () =>
                await interceptor.NonQueryExecutingAsync(mockCommand.Object, null!, default));
        }

        [Theory]
        [InlineData("SET NOCOUNT ON")]
        [InlineData("SET TRANSACTION ISOLATION LEVEL READ COMMITTED")]
        [InlineData("SELECT COUNT(*) FROM Employees")]
        public void ReadOnlyDbCommandInterceptor_AllowsSafeCommands_InReadOnlyMode(string safeSql)
        {
            var mockSyncProvider = new Mock<ISyncConnectionProvider>();
            mockSyncProvider.Setup(p => p.IsReadOnlyMode).Returns(true);

            var interceptor = new ReadOnlyDbCommandInterceptor(mockSyncProvider.Object);

            var mockCommand = new Mock<DbCommand>();
            mockCommand.SetupGet(c => c.CommandText).Returns(safeSql);

            // Should not throw
            var result = interceptor.NonQueryExecuting(mockCommand.Object, null!, default);
            Assert.False(result.HasResult);
        }

        #endregion

        #region File Storage Write-Rejection Tests

        [Fact]
        public async Task CloudinaryService_ThrowsReadOnlyModeException_OnUploadAndDownload_WhenReadOnly()
        {
            var config = CreateConfiguration(readOnlyMode: true);
            var service = new CloudinaryService(config);

            using var stream = new MemoryStream(Encoding.UTF8.GetBytes("test content"));
            
            var uploadEx = await Assert.ThrowsAsync<ReadOnlyModeException>(() =>
                service.UploadFileAsync(stream, "test.pdf", "DailyReferences"));
            Assert.Contains("وضع القراءة المحلية فقط", uploadEx.Message);

            var deleteEx = await Assert.ThrowsAsync<ReadOnlyModeException>(() =>
                service.DeleteFileAsync("https://res.cloudinary.com/test-cloud/raw/upload/DailyReferences/test.pdf", "DailyReferences"));
            Assert.Contains("وضع القراءة المحلية فقط", deleteEx.Message);
        }

        #endregion
    }
}
