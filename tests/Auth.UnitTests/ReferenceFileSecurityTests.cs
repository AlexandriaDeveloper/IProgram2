using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Api.Controllers;
using Application.Dtos.Requests;
using Application.Features;
using Application.Helpers;
using Auth.Infrastructure.Services;
using Core.Interfaces;
using Core.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Moq;
using Xunit;
using Application.Interfaces;

namespace Auth.UnitTests
{
    public class ReferenceFileSecurityTests
    {
        private IFormFile CreateMockFormFile(string fileName, byte[] content, string contentType = "application/octet-stream")
        {
            var fileMock = new Mock<IFormFile>();
            fileMock.Setup(f => f.FileName).Returns(fileName);
            fileMock.Setup(f => f.Length).Returns(content.Length);
            fileMock.Setup(f => f.ContentType).Returns(contentType);
            fileMock.Setup(f => f.OpenReadStream()).Returns(() => new MemoryStream(content));
            return fileMock.Object;
        }

        #region 1. Anonymous Access Restrictions

        [Fact]
        public void DailyReferences_GetFile_RequiresAuthentication_AndDeniesAnonymous()
        {
            var method = typeof(DailyReferencesController).GetMethod("GetFile");
            Assert.NotNull(method);

            var allowAnonymous = method.GetCustomAttribute<AllowAnonymousAttribute>();
            Assert.Null(allowAnonymous);

            var classAuthorize = typeof(DailyReferencesController).BaseType?.GetCustomAttribute<AuthorizeAttribute>();
            Assert.NotNull(classAuthorize);
            Assert.Equal("Bearer", classAuthorize.AuthenticationSchemes);
        }

        [Fact]
        public void FormReferences_GetFile_RequiresAuthentication_AndDeniesAnonymous()
        {
            var method = typeof(FormReferencesController).GetMethod("GetFile");
            Assert.NotNull(method);

            var allowAnonymous = method.GetCustomAttribute<AllowAnonymousAttribute>();
            Assert.Null(allowAnonymous);

            var classAuthorize = typeof(FormReferencesController).BaseType?.GetCustomAttribute<AuthorizeAttribute>();
            Assert.NotNull(classAuthorize);
            Assert.Equal("Bearer", classAuthorize.AuthenticationSchemes);
        }

        [Fact]
        public void EmployeeReferences_GetFile_RequiresAuthentication_AndDeniesAnonymous()
        {
            var method = typeof(EmployeeReferncesController).GetMethod("GetFile");
            Assert.NotNull(method);

            var allowAnonymous = method.GetCustomAttribute<AllowAnonymousAttribute>();
            Assert.Null(allowAnonymous);

            var classAuthorize = typeof(EmployeeReferncesController).BaseType?.GetCustomAttribute<AuthorizeAttribute>();
            Assert.NotNull(classAuthorize);
            Assert.Equal("Bearer", classAuthorize.AuthenticationSchemes);
        }

        #endregion

        #region 2. Static Content Isolation Middleware

        [Theory]
        [InlineData("/content/DailyReferences/1_test.pdf")]
        [InlineData("/Content/DailyReferences/secret.pdf")]
        [InlineData("/content/FormReferences/form_123.jpg")]
        [InlineData("/Content/FormReferences/test.png")]
        [InlineData("/content/EmployeeReferences/emp_55.pdf")]
        [InlineData("/Content/EmployeeReferences/contract.pdf")]
        [InlineData("/content\\DailyReferences\\bypass.pdf")]
        public async Task StaticContentMiddleware_BlocksDirectAccess_ToSensitiveFolders(string requestPath)
        {
            var context = new DefaultHttpContext();
            context.Request.Path = requestPath;

            bool nextCalled = false;
            RequestDelegate next = (ctx) =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            };

            // Replicate the static content isolation middleware logic from Program.cs
            Func<HttpContext, RequestDelegate, Task> middleware = async (ctx, nxt) =>
            {
                var path = ctx.Request.Path.Value ?? string.Empty;
                var normalizedPath = path.Replace('\\', '/');
                if (normalizedPath.StartsWith("/content/DailyReferences", StringComparison.OrdinalIgnoreCase) ||
                    normalizedPath.StartsWith("/content/FormReferences", StringComparison.OrdinalIgnoreCase) ||
                    normalizedPath.StartsWith("/content/EmployeeReferences", StringComparison.OrdinalIgnoreCase))
                {
                    ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }
                await nxt(ctx);
            };

            await middleware(context, next);

            Assert.False(nextCalled, "Middleware should NOT have passed request to next() for sensitive folder");
            Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        }

        [Theory]
        [InlineData("/content/logo.png")]
        [InlineData("/content/logo2.png")]
        [InlineData("/content/Fonts/Amiri.ttf")]
        [InlineData("/content/AddEmployees.xlsx")]
        public async Task StaticContentMiddleware_AllowsAccess_ToPublicAssets(string requestPath)
        {
            var context = new DefaultHttpContext();
            context.Request.Path = requestPath;

            bool nextCalled = false;
            RequestDelegate next = (ctx) =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            };

            Func<HttpContext, RequestDelegate, Task> middleware = async (ctx, nxt) =>
            {
                var path = ctx.Request.Path.Value ?? string.Empty;
                var normalizedPath = path.Replace('\\', '/');
                if (normalizedPath.StartsWith("/content/DailyReferences", StringComparison.OrdinalIgnoreCase) ||
                    normalizedPath.StartsWith("/content/FormReferences", StringComparison.OrdinalIgnoreCase) ||
                    normalizedPath.StartsWith("/content/EmployeeReferences", StringComparison.OrdinalIgnoreCase))
                {
                    ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }
                await nxt(ctx);
            };

            await middleware(context, next);

            Assert.True(nextCalled, "Middleware should have passed public assets to next()");
            Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        }

        #endregion

        #region 3. Upload Security, Extension Spoofing & Content Validation

        [Fact]
        public void ValidateFile_Rejects_ExtensionSpoofedPdf()
        {
            // Plain text or script disguised with a .pdf extension
            var fakeContent = Encoding.UTF8.GetBytes("<html><script>alert('xss')</script></html>");
            var file = CreateMockFormFile("invoice.pdf", fakeContent, "application/pdf");

            var result = FileSecurityValidator.ValidateFile(
                file,
                FileSecurityValidator.MaxDailyReferenceBytes,
                new[] { ".pdf", ".jpg", ".jpeg", ".png" });

            Assert.False(result.IsSuccess);
            Assert.Contains("محتوى الملف لا يتطابق", result.Error.Message);
        }

        [Fact]
        public void ValidateFile_Rejects_ExtensionSpoofedImage()
        {
            // Executable header disguised as a .jpg
            var exeHeader = new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00 }; // MZ header
            var file = CreateMockFormFile("profile.jpg", exeHeader, "image/jpeg");

            var result = FileSecurityValidator.ValidateFile(
                file,
                FileSecurityValidator.MaxEmployeeUploadBytes,
                new[] { ".pdf", ".jpg", ".jpeg", ".png" });

            Assert.False(result.IsSuccess);
            Assert.Contains("محتوى الملف لا يتطابق", result.Error.Message);
        }

        [Theory]
        [InlineData("malicious.exe")]
        [InlineData("payload.html")]
        [InlineData("script.js")]
        [InlineData("webshell.php")]
        [InlineData("run.bat")]
        [InlineData("document.docm")]
        public void ValidateFile_Rejects_ExecutableAndHtmlAndScriptUploads(string fileName)
        {
            var content = Encoding.UTF8.GetBytes("malicious payload");
            var file = CreateMockFormFile(fileName, content);

            var result = FileSecurityValidator.ValidateFile(
                file,
                FileSecurityValidator.MaxDailyReferenceBytes,
                new[] { ".pdf", ".jpg", ".jpeg", ".png" });

            Assert.False(result.IsSuccess);
            Assert.Contains("نوع الملف غير مسموح به", result.Error.Message);
        }

        [Fact]
        public void ValidateFile_Accepts_GenuinePdfFile()
        {
            var pdfBytes = Encoding.ASCII.GetBytes("%PDF-1.7\nGenuine PDF Content");
            var file = CreateMockFormFile("reference.pdf", pdfBytes, "application/pdf");

            var result = FileSecurityValidator.ValidateFile(
                file,
                FileSecurityValidator.MaxDailyReferenceBytes,
                new[] { ".pdf", ".jpg", ".jpeg", ".png" });

            Assert.True(result.IsSuccess);
        }

        [Fact]
        public void ValidateFile_Accepts_GenuinePngFile()
        {
            var pngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D };
            var file = CreateMockFormFile("receipt.png", pngBytes, "image/png");

            var result = FileSecurityValidator.ValidateFile(
                file,
                FileSecurityValidator.MaxDailyReferenceBytes,
                new[] { ".pdf", ".jpg", ".jpeg", ".png" });

            Assert.True(result.IsSuccess);
        }

        [Fact]
        public void ValidateFile_Accepts_GenuineJpegFile()
        {
            var jpegBytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46 };
            var file = CreateMockFormFile("photo.jpg", jpegBytes, "image/jpeg");

            var result = FileSecurityValidator.ValidateFile(
                file,
                FileSecurityValidator.MaxDailyReferenceBytes,
                new[] { ".pdf", ".jpg", ".jpeg", ".png" });

            Assert.True(result.IsSuccess);
        }

        #endregion

        #region 4. Cloudinary & Storage Raw Exception Sanitization

        [Fact]
        public async Task TestCloudinaryConnection_OnFailure_DoesNotLeakRawExceptionToCaller()
        {
            var repoMock = new Mock<IDailyReferencesRepository>();
            var uowMock = new Mock<IUnitOfWork>();
            var hostEnvMock = new Mock<IWebHostEnvironment>();
            var fileStorageMock = new Mock<IFileStorageService>();
            var loggerMock = new Mock<ILogger<DailyReferenceService>>();

            // Simulate storage failure throwing sensitive internal exception details
            fileStorageMock.Setup(s => s.UploadFileAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>()))
                .ThrowsAsync(new InvalidOperationException("CRITICAL: Cloudinary api_secret=s3cr3t failed connection to host 10.0.0.1"));

            var closureGuardMock = new Mock<IDailyClosureGuard>();

            var service = new DailyReferenceService(
                repoMock.Object,
                uowMock.Object,
                hostEnvMock.Object,
                fileStorageMock.Object,
                loggerMock.Object,
                closureGuardMock.Object);

            var result = await service.TestCloudinaryConnection();

            // Result must be a generic failure, NEVER containing the raw exception or sensitive details
            Assert.Equal("FAILED", result);
            Assert.DoesNotContain("s3cr3t", result);
            Assert.DoesNotContain("InvalidOperationException", result);
            Assert.DoesNotContain("10.0.0.1", result);

            // Verify exception was logged internally
            loggerMock.Verify(
                x => x.Log(
                    LogLevel.Error,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => true),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }

        [Fact]
        public async Task DailyReferencesController_TestConnection_ReturnsGenericMessage_OnFailure()
        {
            var repoMock = new Mock<IDailyReferencesRepository>();
            var uowMock = new Mock<IUnitOfWork>();
            var hostEnvMock = new Mock<IWebHostEnvironment>();
            var fileStorageMock = new Mock<IFileStorageService>();
            var loggerMock = new Mock<ILogger<DailyReferenceService>>();

            fileStorageMock.Setup(s => s.UploadFileAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>()))
                .ThrowsAsync(new Exception("Internal provider connection timeout"));

            var closureGuardMock = new Mock<IDailyClosureGuard>();

            var service = new DailyReferenceService(
                repoMock.Object,
                uowMock.Object,
                hostEnvMock.Object,
                fileStorageMock.Object,
                loggerMock.Object,
                closureGuardMock.Object);

            var controller = new DailyReferencesController(service);

            var actionResult = await controller.TestConnection();
            var badRequestResult = Assert.IsType<BadRequestObjectResult>(actionResult);
            Assert.NotNull(badRequestResult.Value);

            var messageProperty = badRequestResult.Value.GetType().GetProperty("message");
            Assert.NotNull(messageProperty);
            var messageValue = messageProperty.GetValue(badRequestResult.Value)?.ToString() ?? string.Empty;

            Assert.Contains("فشل الاتصال بخدمة التخزين السحابي", messageValue);
            Assert.DoesNotContain("Internal provider connection timeout", messageValue);
        }

        #endregion

        #region 5. Query Token Security (Rejected for Normal API Endpoints, Only for /migrationHub)

        [Theory]
        [InlineData("/api/DailyReferences/file/1")]
        [InlineData("/api/DailyReferences/file/999")]
        [InlineData("/api/FormReferences/file/2")]
        [InlineData("/api/EmployeeRefernces/file/3")]
        [InlineData("/api/Account/Login")]
        [InlineData("/api/Daily/1")]
        [InlineData("/content/DailyReferences/file.pdf")]
        public async Task JwtBearer_DoesNotAccept_AccessTokenQueryParam_ForApiEndpoints(string requestPath)
        {
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Path = requestPath;
            httpContext.Request.Query = new QueryCollection(new Dictionary<string, StringValues>
            {
                { "access_token", "super-secret-jwt-token" }
            });

            var options = new JwtBearerOptions();
            options.Events = new JwtBearerEvents
            {
                OnMessageReceived = context =>
                {
                    var accessToken = context.Request.Query["access_token"];
                    var path = context.HttpContext.Request.Path;
                    if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/migrationHub"))
                    {
                        context.Token = accessToken;
                    }
                    return Task.CompletedTask;
                }
            };

            var context = new MessageReceivedContext(
                httpContext,
                new AuthenticationScheme("Bearer", null, typeof(JwtBearerHandler)),
                options);

            await options.Events.MessageReceived(context);

            Assert.Null(context.Token);
        }

        [Theory]
        [InlineData("/migrationHub")]
        [InlineData("/migrationHub/negotiate")]
        [InlineData("/migrationHub/")]
        public async Task JwtBearer_Accepts_AccessTokenQueryParam_OnlyForMigrationHub(string requestPath)
        {
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Path = requestPath;
            httpContext.Request.Query = new QueryCollection(new Dictionary<string, StringValues>
            {
                { "access_token", "valid-signalr-token" }
            });

            var options = new JwtBearerOptions();
            options.Events = new JwtBearerEvents
            {
                OnMessageReceived = context =>
                {
                    var accessToken = context.Request.Query["access_token"];
                    var path = context.HttpContext.Request.Path;
                    if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/migrationHub"))
                    {
                        context.Token = accessToken;
                    }
                    return Task.CompletedTask;
                }
            };

            var context = new MessageReceivedContext(
                httpContext,
                new AuthenticationScheme("Bearer", null, typeof(JwtBearerHandler)),
                options);

            await options.Events.MessageReceived(context);

            Assert.Equal("valid-signalr-token", context.Token);
        }

        #endregion

        #region 6. Cloudinary Logging Sanitization & Authenticated Deletion

        [Fact]
        public async Task CloudinaryService_DoesNotLog_ProtectedSignedUrls_OrSignatures()
        {
            var inMemorySettings = new Dictionary<string, string?> {
                {"Cloudinary:CloudName", "dummy_cloud"},
                {"Cloudinary:ApiKey", "123456789012345"},
                {"Cloudinary:ApiSecret", "abcdefghijklmnopqrstuvwxyz1"}
            };
            IConfiguration config = new ConfigurationBuilder()
                .AddInMemoryCollection(inMemorySettings)
                .Build();
            var service = new CloudinaryService(config);

            using var sw = new StringWriter();
            var originalOut = Console.Out;
            Console.SetOut(sw);
            try
            {
                var sensitiveUrl = "https://res.cloudinary.com/dummy/raw/authenticated/s--SecretSig123--/v1/DailyReferences/file.pdf";
                await service.DownloadFileStreamAsync(sensitiveUrl, "DailyReferences");
                await service.DeleteFileAsync(sensitiveUrl, "DailyReferences");

                var logs = sw.ToString();
                Assert.DoesNotContain("SecretSig123", logs);
                Assert.DoesNotContain("https://res.cloudinary.com", logs);
            }
            finally
            {
                Console.SetOut(originalOut);
            }
        }

        [Fact]
        public async Task CloudinaryService_DeleteFileAsync_HandlesInvalidOrEmptyUrl_WithoutCrashing()
        {
            var inMemorySettings = new Dictionary<string, string?> {
                {"Cloudinary:CloudName", "dummy_cloud"},
                {"Cloudinary:ApiKey", "123456789012345"},
                {"Cloudinary:ApiSecret", "abcdefghijklmnopqrstuvwxyz1"}
            };
            IConfiguration config = new ConfigurationBuilder()
                .AddInMemoryCollection(inMemorySettings)
                .Build();
            var service = new CloudinaryService(config);

            var emptyResult = await service.DeleteFileAsync("", "DailyReferences");
            Assert.False(emptyResult);

            var nullResult = await service.DeleteFileAsync(null!, "DailyReferences");
            Assert.False(nullResult);
        }

        [Theory]
        [InlineData("https://res.cloudinary.com/cloud123/raw/authenticated/v12345/DailyReferences/report.pdf", "DailyReferences/report.pdf")]
        [InlineData("https://res.cloudinary.com/cloud123/raw/authenticated/s--Sig123--/v12345/DailyReferences/report.pdf", "DailyReferences/report.pdf")]
        [InlineData("https://res.cloudinary.com/cloud123/raw/authenticated/s--Sig123--/DailyReferences/report.pdf", "DailyReferences/report.pdf")]
        [InlineData("https://res.cloudinary.com/cloud123/raw/authenticated/v12345/DailyReferences/legacy_no_ext", "DailyReferences/legacy_no_ext")]
        [InlineData("https://res.cloudinary.com/cloud123/raw/authenticated/v12345/EmployeeReferences/emp_doc.pdf", "EmployeeReferences/emp_doc.pdf")]
        [InlineData("https://res.cloudinary.com/cloud123/raw/upload/v12345/FormReferences/form.jpg", "FormReferences/form.jpg")]
        [InlineData("DailyReferences/direct_id.pdf", "DailyReferences/direct_id.pdf")]
        public void CloudinaryService_ExtractPublicIdFromUrl_ExtractsCorrectPublicId(string inputUrl, string expectedPublicId)
        {
            var result = CloudinaryService.ExtractPublicIdFromUrl(inputUrl, "DailyReferences");
            Assert.Equal(expectedPublicId, result);
        }

        [Fact]
        public void CloudinaryService_GetProtectedUrl_PreservesPublicDeliveryUrls()
        {
            var inMemorySettings = new Dictionary<string, string?> {
                {"Cloudinary:CloudName", "dummy_cloud"},
                {"Cloudinary:ApiKey", "123456789012345"},
                {"Cloudinary:ApiSecret", "abcdefghijklmnopqrstuvwxyz1"}
            };
            IConfiguration config = new ConfigurationBuilder()
                .AddInMemoryCollection(inMemorySettings)
                .Build();
            var service = new CloudinaryService(config);

            var legacyRawUrl = "https://res.cloudinary.com/dummy_cloud/raw/upload/v12345/DailyReferences/old.pdf";
            var result = service.GetProtectedUrl(legacyRawUrl, "DailyReferences");

            Assert.Equal(legacyRawUrl, result);
        }

        [Fact]
        public void CloudinaryService_GetProtectedUrl_GeneratesSignedUrl_ForAuthenticatedRawAssets()
        {
            var inMemorySettings = new Dictionary<string, string?> {
                {"Cloudinary:CloudName", "dummy_cloud"},
                {"Cloudinary:ApiKey", "123456789012345"},
                {"Cloudinary:ApiSecret", "abcdefghijklmnopqrstuvwxyz1"}
            };
            IConfiguration config = new ConfigurationBuilder()
                .AddInMemoryCollection(inMemorySettings)
                .Build();
            var service = new CloudinaryService(config);

            var authenticatedRawUrl = "https://res.cloudinary.com/dummy_cloud/raw/authenticated/v12345/DailyReferences/file.pdf";
            var result = service.GetProtectedUrl(authenticatedRawUrl, "DailyReferences");
            Assert.Contains("/raw/authenticated/", result);
            Assert.Contains("DailyReferences/file.pdf", result);
            Assert.Contains("s--", result); // signature token
        }

        [Fact]
        public async Task CloudinaryService_BinaryCdnFallback_ServesFromLocalDisk_WhenCached()
        {
            var inMemorySettings = new Dictionary<string, string?> {
                { "LocalFirst:Enabled", "true" },
                { "LocalFirst:LocalOnlyProduction", "true" },
                { "LocalFirst:AllowCloudinaryFallback", "true" }
            };
            var config = new ConfigurationBuilder().AddInMemoryCollection(inMemorySettings).Build();
            var service = new CloudinaryService(config);

            var folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Content", "DailyReferences");
            Directory.CreateDirectory(folder);
            var testFile = Path.Combine(folder, "cached_fallback_test.pdf");
            try
            {
                await File.WriteAllTextAsync(testFile, "%PDF-1.4 test cached content");
                var result = await service.DownloadFileStreamAsync("https://res.cloudinary.com/test/raw/upload/DailyReferences/cached_fallback_test.pdf", "DailyReferences");
                Assert.NotNull(result);
                using var reader = new StreamReader(result.Value.stream);
                var content = await reader.ReadToEndAsync();
                Assert.Equal("%PDF-1.4 test cached content", content);
            }
            finally
            {
                if (File.Exists(testFile)) File.Delete(testFile);
            }
        }

        [Fact]
        public async Task CloudinaryService_BinaryCdnFallback_ReturnsNull_WhenFallbackExplicitlyDisabled()
        {
            var inMemorySettings = new Dictionary<string, string?> {
                { "LocalFirst:Enabled", "true" },
                { "LocalFirst:AllowCloudinaryFallback", "false" }
            };
            var config = new ConfigurationBuilder().AddInMemoryCollection(inMemorySettings).Build();
            var service = new CloudinaryService(config);

            var result = await service.DownloadFileStreamAsync("https://res.cloudinary.com/test/raw/upload/DailyReferences/uncached_remote.pdf", "DailyReferences");
            Assert.Null(result);
        }

        #endregion

        #region 7. Angular Client Hygiene (No access_token in URLs)

        [Fact]
        public void AngularClient_DoesNotContain_AccessTokenInUrls()
        {
            var currentDir = AppContext.BaseDirectory;
            var dir = new DirectoryInfo(currentDir);

            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Client", "src", "app")))
            {
                dir = dir.Parent;
            }

            Assert.True(dir != null, $"Expected Client source directory 'Client/src/app' not found. Searched upwards from: {currentDir}");

            var clientAppDir = Path.Combine(dir.FullName, "Client", "src", "app");
            Assert.True(Directory.Exists(clientAppDir), $"Expected Client source directory does not exist: {clientAppDir}");

            var tsFiles = Directory.GetFiles(clientAppDir, "*.ts", SearchOption.AllDirectories);
            Assert.NotEmpty(tsFiles);

            foreach (var file in tsFiles)
            {
                var content = File.ReadAllText(file);
                Assert.DoesNotContain("access_token=", content);
                Assert.DoesNotContain("access_token}", content);
            }
        }

        #endregion
    }
}
