using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Auth.Api.Middleware;
using Auth.Infrastructure.Configuration;
using Core.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Auth.UnitTests
{
    public class AzureSqlStabilityTests
    {
        [Fact]
        public void SqlServerOptions_FromConfiguration_ReturnsDefaults_WhenConfigurationMissingOrEmpty()
        {
            // Arrange
            IConfiguration? nullConfig = null;
            var emptyConfig = new ConfigurationBuilder().Build();

            // Act
            var optionsFromNull = SqlServerOptions.FromConfiguration(nullConfig);
            var optionsFromEmpty = SqlServerOptions.FromConfiguration(emptyConfig);

            // Assert
            Assert.Equal(5, optionsFromNull.MaxRetryCount);
            Assert.Equal(10, optionsFromNull.MaxRetryDelaySeconds);
            Assert.Equal(30, optionsFromNull.CommandTimeoutSeconds);

            Assert.Equal(5, optionsFromEmpty.MaxRetryCount);
            Assert.Equal(10, optionsFromEmpty.MaxRetryDelaySeconds);
            Assert.Equal(30, optionsFromEmpty.CommandTimeoutSeconds);
        }

        [Fact]
        public void SqlServerOptions_FromConfiguration_BindsCustomValues_AndClampsWithinSafeBounds()
        {
            // Arrange: custom valid settings
            var inMemorySettings = new Dictionary<string, string?>
            {
                { "SqlServer:MaxRetryCount", "7" },
                { "SqlServer:MaxRetryDelaySeconds", "25" },
                { "SqlServer:CommandTimeoutSeconds", "45" }
            };
            var config = new ConfigurationBuilder().AddInMemoryCollection(inMemorySettings).Build();

            // Act
            var options = SqlServerOptions.FromConfiguration(config);

            // Assert
            Assert.Equal(7, options.MaxRetryCount);
            Assert.Equal(25, options.MaxRetryDelaySeconds);
            Assert.Equal(45, options.CommandTimeoutSeconds);

            // Arrange: extreme settings that must be clamped
            var extremeSettings = new Dictionary<string, string?>
            {
                { "SqlServer:MaxRetryCount", "999" }, // max is 10
                { "SqlServer:MaxRetryDelaySeconds", "500" }, // max is 60
                { "SqlServer:CommandTimeoutSeconds", "5000" } // max is 120
            };
            var extremeConfig = new ConfigurationBuilder().AddInMemoryCollection(extremeSettings).Build();

            // Act
            var clampedOptions = SqlServerOptions.FromConfiguration(extremeConfig);

            // Assert
            Assert.Equal(10, clampedOptions.MaxRetryCount);
            Assert.Equal(60, clampedOptions.MaxRetryDelaySeconds);
            Assert.Equal(120, clampedOptions.CommandTimeoutSeconds);

            // Arrange: negative / zero settings that must be clamped to safe minimums
            var zeroSettings = new Dictionary<string, string?>
            {
                { "SqlServer:MaxRetryCount", "0" },
                { "SqlServer:MaxRetryDelaySeconds", "-5" },
                { "SqlServer:CommandTimeoutSeconds", "1" }
            };
            var zeroConfig = new ConfigurationBuilder().AddInMemoryCollection(zeroSettings).Build();

            // Act
            var minClamped = SqlServerOptions.FromConfiguration(zeroConfig);

            // Assert
            // 0 falls back to default 5 for count and 10 for delay; command timeout clamps to min 5
            Assert.Equal(5, minClamped.MaxRetryCount);
            Assert.Equal(10, minClamped.MaxRetryDelaySeconds);
            Assert.Equal(5, minClamped.CommandTimeoutSeconds);
        }

        [Fact]
        public async Task GlobalExceptionHandler_HidesInternalExceptionMessage_AndStackTrace()
        {
            // Arrange
            var loggerMock = new Mock<ILogger<GlobalExceptionHandler>>();
            var handler = new GlobalExceptionHandler(loggerMock.Object);

            var context = new DefaultHttpContext();
            context.TraceIdentifier = "test-trace-id-12345";
            context.Response.Body = new MemoryStream();

            var sensitiveEx = new InvalidOperationException(
                "FATAL INTERNAL SQL CRASH: table [dbo].[Secrets] with secret_key='SUPER_SECRET_12345' failed.\n   at Auth.Internal.SecretMethod() in F:\\Secret\\Path.cs:line 99");

            // Act
            var handled = await handler.TryHandleAsync(context, sensitiveEx, CancellationToken.None);

            // Assert
            Assert.True(handled);
            Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);

            context.Response.Body.Seek(0, SeekOrigin.Begin);
            using var reader = new StreamReader(context.Response.Body);
            var responseBody = await reader.ReadToEndAsync();

            var responseJson = JsonSerializer.Deserialize<ErrorResponseDto>(responseBody, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            Assert.NotNull(responseJson);
            Assert.Equal(500, responseJson.StatusCode);
            Assert.Equal("حدث خطأ غير متوقع أثناء تنفيذ الطلب.", responseJson.Message);
            Assert.Equal("test-trace-id-12345", responseJson.TraceId);

            // Verify sensitive information is completely absent from the client response
            Assert.DoesNotContain("SUPER_SECRET_12345", responseBody);
            Assert.DoesNotContain("Secrets", responseBody);
            Assert.DoesNotContain("SecretMethod", responseBody);
            Assert.DoesNotContain("Path.cs", responseBody);
            Assert.DoesNotContain("StackTrace", responseBody);
            Assert.DoesNotContain("InvalidOperationException", responseBody);
        }

        [Fact]
        public async Task GlobalExceptionHandler_IncludesTraceId_InResponse()
        {
            // Arrange
            var loggerMock = new Mock<ILogger<GlobalExceptionHandler>>();
            var handler = new GlobalExceptionHandler(loggerMock.Object);

            var context = new DefaultHttpContext();
            string expectedTraceId = "trace-corr-id-998877";
            context.TraceIdentifier = expectedTraceId;
            context.Response.Body = new MemoryStream();

            var ex = new Exception("General unhandled fault");

            // Act
            await handler.TryHandleAsync(context, ex, CancellationToken.None);

            // Assert
            context.Response.Body.Seek(0, SeekOrigin.Begin);
            using var reader = new StreamReader(context.Response.Body);
            var responseBody = await reader.ReadToEndAsync();

            var responseJson = JsonSerializer.Deserialize<ErrorResponseDto>(responseBody, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            Assert.NotNull(responseJson);
            Assert.Equal(expectedTraceId, responseJson.TraceId);
        }

        [Fact]
        public async Task GlobalExceptionHandler_Returns503_OnTransientDatabaseFailure()
        {
            // Arrange
            var loggerMock = new Mock<ILogger<GlobalExceptionHandler>>();
            var handler = new GlobalExceptionHandler(loggerMock.Object);

            var context = new DefaultHttpContext();
            context.TraceIdentifier = "transient-trace-503";
            context.Response.Body = new MemoryStream();

            // Wrapped TimeoutException simulating command timeout after transient retry exhaustion
            var transientEx = new Exception("EF Core execution strategy failed", new TimeoutException("The command execution timeout expired."));

            // Act
            var handled = await handler.TryHandleAsync(context, transientEx, CancellationToken.None);

            // Assert
            Assert.True(handled);
            Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);

            context.Response.Body.Seek(0, SeekOrigin.Begin);
            using var reader = new StreamReader(context.Response.Body);
            var responseBody = await reader.ReadToEndAsync();

            var responseJson = JsonSerializer.Deserialize<ErrorResponseDto>(responseBody, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            Assert.NotNull(responseJson);
            Assert.Equal(503, responseJson.StatusCode);
            Assert.Contains("الخدمة غير متوفرة مؤقتًا بسبب انقطاع الاتصال بقاعدة البيانات", responseJson.Message);
            Assert.DoesNotContain("TimeoutException", responseBody);
        }

        [Fact]
        public async Task GlobalExceptionHandler_LogsSelectedDatabaseId_WithoutLoggingConnectionString()
        {
            // Arrange
            string loggedMessage = string.Empty;
            var loggerMock = new Mock<ILogger<GlobalExceptionHandler>>();
            loggerMock.Setup(l => l.Log(
                It.IsAny<LogLevel>(),
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
                .Callback(new InvocationAction(invocation =>
                {
                    var formatter = invocation.Arguments[4];
                    var state = invocation.Arguments[2];
                    var ex = invocation.Arguments[3] as Exception;
                    var formatMethod = formatter.GetType().GetMethod("Invoke");
                    loggedMessage = formatMethod?.Invoke(formatter, new[] { state, ex })?.ToString() ?? state.ToString() ?? "";
                }));

            var handler = new GlobalExceptionHandler(loggerMock.Object);

            string secretConnStr = "Server=tcp:azure-sql.database.windows.net;User Id=dbadmin;Password=P@ssw0rd9988!;Database=IProgramDb2027;";
            string databaseId = "2027";

            var dbProviderMock = new Mock<IDbConnectionProvider>();
            dbProviderMock.Setup(p => p.GetSelectedDatabaseId()).Returns(databaseId);
            dbProviderMock.Setup(p => p.GetConnectionString()).Returns(secretConnStr);

            var services = new ServiceCollection();
            services.AddSingleton(dbProviderMock.Object);
            var serviceProvider = services.BuildServiceProvider();

            var context = new DefaultHttpContext();
            context.RequestServices = serviceProvider;
            context.Request.Path = "/api/daily/5";
            context.TraceIdentifier = "trace-db-diag-1";
            context.Response.Body = new MemoryStream();

            var dbEx = new TimeoutException("Database operation timed out");

            // Act
            await handler.TryHandleAsync(context, dbEx, CancellationToken.None);

            // Assert
            // Logged message must include DatabaseId "2027"
            Assert.Contains("2027", loggedMessage);
            Assert.Contains("/api/daily/5", loggedMessage);

            // Logged message MUST NOT contain connection string or credentials
            Assert.DoesNotContain("Server=tcp:azure-sql", loggedMessage);
            Assert.DoesNotContain("dbadmin", loggedMessage);
            Assert.DoesNotContain("P@ssw0rd9988!", loggedMessage);
            Assert.DoesNotContain("IProgramDb2027", loggedMessage);
        }

        [Fact]
        public async Task GlobalExceptionHandler_NoSecretsOrConnectionString_InResponsePayload()
        {
            // Arrange
            var loggerMock = new Mock<ILogger<GlobalExceptionHandler>>();
            var handler = new GlobalExceptionHandler(loggerMock.Object);

            var context = new DefaultHttpContext();
            context.Response.Body = new MemoryStream();

            // Simulate raw exception containing connection string and credentials
            var leakedDbEx = new Exception("Cannot open database requested by login. Connection: Server=tcp:mycloud.database.windows.net;User Id=sa;Password=SuperSecretPassWord1!;");

            // Act
            await handler.TryHandleAsync(context, leakedDbEx, CancellationToken.None);

            // Assert
            context.Response.Body.Seek(0, SeekOrigin.Begin);
            using var reader = new StreamReader(context.Response.Body);
            var responseBody = await reader.ReadToEndAsync();

            Assert.DoesNotContain("mycloud.database.windows.net", responseBody);
            Assert.DoesNotContain("SuperSecretPassWord1!", responseBody);
            Assert.DoesNotContain("User Id=sa", responseBody);
            Assert.DoesNotContain("Connection:", responseBody);
        }

        [Fact]
        public void BusinessResultFailures_DoNotTriggerGlobal500()
        {
            // Arrange
            var failureResult = Application.Helpers.Result.Failure(new Application.Helpers.Error("400", "البيانات المدخلة غير صحيحة"));

            // Assert: Business Result failures are standard domain responses, not unhandled exceptions
            Assert.True(failureResult.IsFailure);
            Assert.False(failureResult.IsSuccess);
            Assert.Equal("400", failureResult.Error.Code);
            Assert.Equal("البيانات المدخلة غير صحيحة", failureResult.Error.Message);
        }

        [Fact]
        public void AllManualTransactions_UseExecutionStrategyCompatiblePattern()
        {
            // Locate src directory
            var currentDir = new DirectoryInfo(AppContext.BaseDirectory);
            while (currentDir != null && !Directory.Exists(Path.Combine(currentDir.FullName, "src")))
            {
                currentDir = currentDir.Parent;
            }

            Assert.NotNull(currentDir);
            var srcDir = Path.Combine(currentDir.FullName, "src");
            var csFiles = Directory.GetFiles(srcDir, "*.cs", SearchOption.AllDirectories);

            var filesWithTransaction = new List<string>();
            foreach (var file in csFiles)
            {
                var content = File.ReadAllText(file);
                if (content.Contains("BeginTransactionAsync") || content.Contains("BeginTransaction("))
                {
                    filesWithTransaction.Add(Path.GetFileName(file));
                    // Verify that the file uses EF Core execution strategy to wrap the transaction
                    Assert.Contains("CreateExecutionStrategy", content);
                    Assert.Contains("ExecuteAsync", content);
                }
            }

            // Only EmployeeService.cs contains a manual transaction, and it is strictly wrapped in CreateExecutionStrategy
            Assert.Single(filesWithTransaction);
            Assert.Equal("EmployeeService.cs", filesWithTransaction[0]);
        }
    }
}
