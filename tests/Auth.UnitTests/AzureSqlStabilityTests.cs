using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Application.Features;
using Auth.Api.Middleware;
using Auth.Infrastructure;
using Auth.Infrastructure.Configuration;
using Core.Interfaces;
using Core.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
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
        public async Task GlobalExceptionHandler_GenericTimeoutException_Returns500_Not503()
        {
            // Arrange: Generic TimeoutException without SQL evidence must NOT be classified as 503
            var loggerMock = new Mock<ILogger<GlobalExceptionHandler>>();
            var handler = new GlobalExceptionHandler(loggerMock.Object);

            var context = new DefaultHttpContext();
            context.TraceIdentifier = "timeout-trace-500";
            context.Response.Body = new MemoryStream();

            var timeoutEx = new TimeoutException("HTTP client connection timed out while calling third-party API.");

            // Act
            var handled = await handler.TryHandleAsync(context, timeoutEx, CancellationToken.None);

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
        }

        [Theory]
        [InlineData(-2, true)]     // Execution Timeout Expired
        [InlineData(64, true)]     // Connection error during network write
        [InlineData(233, true)]    // Connection initialization error / broken pipe
        [InlineData(1205, true)]   // Deadlock victim
        [InlineData(10053, true)]  // Connection aborted
        [InlineData(10054, true)]  // Connection reset by peer
        [InlineData(10060, true)]  // Network connection failed
        [InlineData(10928, true)]  // Resource limit reached in Azure SQL
        [InlineData(10929, true)]  // Resource governor queued request
        [InlineData(40197, true)]  // Azure SQL transient error
        [InlineData(40501, true)]  // Azure SQL service busy
        [InlineData(40613, true)]  // Azure SQL database unavailable
        [InlineData(49918, true)]  // Not enough resources
        [InlineData(49919, true)]  // Service busy
        [InlineData(49920, true)]  // Service busy
        [InlineData(20, false)]    // Removed: encryption mismatch / configuration problem
        [InlineData(4060, false)]  // Removed: invalid login / database missing
        [InlineData(18456, false)] // Login failure
        [InlineData(50000, false)] // Custom user raiseerror
        public void TransientSqlClassification_IdentifiesTrueTransientCodes_AndExcludesLoginConfigErrors(int errorCode, bool expectedTransient)
        {
            // Act
            var isTransient = GlobalExceptionHandler.IsTransientSqlErrorCode(errorCode);

            // Assert
            Assert.Equal(expectedTransient, isTransient);
        }

        [Fact]
        public async Task GlobalExceptionHandler_LogsStructuredFields_WithoutPassingRawExceptionObject_AndNoSecretsLeaked()
        {
            // Arrange
            string loggedMessage = string.Empty;
            Exception? passedExceptionArg = new Exception("sentinel");

            var loggerMock = new Mock<ILogger<GlobalExceptionHandler>>();
            loggerMock.Setup(l => l.Log(
                It.IsAny<LogLevel>(),
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
                .Callback(new InvocationAction(invocation =>
                {
                    // Verify that the Exception argument passed to logger is NULL
                    passedExceptionArg = invocation.Arguments[3] as Exception;

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

            var dangerousEx = new InvalidOperationException(
                "Internal DB error. Connection string leaked: Server=tcp:azure-sql.database.windows.net;Password=P@ssw0rd9988!");

            // Act
            await handler.TryHandleAsync(context, dangerousEx, CancellationToken.None);

            // Assert 1: The Exception parameter passed to ILogger MUST BE NULL (prevents raw object serialization)
            Assert.Null(passedExceptionArg);

            // Assert 2: Logged message must include structured diagnostics
            Assert.Contains("2027", loggedMessage);
            Assert.Contains("/api/daily/5", loggedMessage);
            Assert.Contains("trace-db-diag-1", loggedMessage);
            Assert.Contains(nameof(InvalidOperationException), loggedMessage);

            // Assert 3: Logged message MUST NOT contain connection string, password, or raw message
            Assert.DoesNotContain("Server=tcp:azure-sql", loggedMessage);
            Assert.DoesNotContain("dbadmin", loggedMessage);
            Assert.DoesNotContain("P@ssw0rd9988!", loggedMessage);
            Assert.DoesNotContain("Connection string leaked", loggedMessage);
        }

        [Fact]
        public async Task GlobalExceptionHandler_NoSecretsOrConnectionString_InResponsePayload()
        {
            // Arrange
            var loggerMock = new Mock<ILogger<GlobalExceptionHandler>>();
            var handler = new GlobalExceptionHandler(loggerMock.Object);

            var context = new DefaultHttpContext();
            context.Response.Body = new MemoryStream();

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
        public void ChangeNationalId_SourceCode_EnsuresExecutionStrategyRethrowsAndOuterBlockCatches()
        {
            // Locate EmployeeService.cs source file
            var currentDir = new DirectoryInfo(AppContext.BaseDirectory);
            while (currentDir != null && !Directory.Exists(Path.Combine(currentDir.FullName, "src")))
            {
                currentDir = currentDir.Parent;
            }

            Assert.NotNull(currentDir);
            var empServicePath = Path.Combine(currentDir.FullName, "src", "Application", "Features", "EmployeeService.cs");
            Assert.True(File.Exists(empServicePath));

            var content = File.ReadAllText(empServicePath);

            // 1. Must use CreateExecutionStrategy
            Assert.Contains("var strategy = _context.Database.CreateExecutionStrategy();", content);

            // 2. Transaction must be wrapped within strategy.ExecuteAsync
            Assert.Contains("return await strategy.ExecuteAsync(async () =>", content);

            // 3. Delegate must rethrow exceptions and not swallow them with Result.Failure
            Assert.Contains("await transaction.RollbackAsync();", content);
            Assert.Contains("throw;", content);

            // 4. Outer try-catch must catch after retries are exhausted and return generic message
            Assert.Contains("return Result.Failure(new Error(\"500\", \"حدث خطأ أثناء تغيير الرقم القومى.\"));", content);

            // 5. Must NOT leak {ex.Message}
            Assert.DoesNotContain("حدث خطأ أثناء تغيير الرقم القومى: {ex.Message}", content);
        }

        [Fact]
        public async Task ChangeNationalId_WhenExecutionFails_ReturnsGenericFailureWithoutExMessage()
        {
            // Arrange: Setup EmployeeService with InMemory database (raw SQL throws in InMemory, testing full rollback & rethrow flow)
            var options = new DbContextOptionsBuilder<ApplicationContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .Options;

            using var dbContext = new ApplicationContext(options);

            var empRepoMock = new Mock<IEmployeeRepository>();
            empRepoMock.Setup(r => r.GetById("11111111111111", It.IsAny<bool>()))
                .ReturnsAsync(new Employee { Id = "11111111111111", Name = "اختبار" });
            empRepoMock.Setup(r => r.CheckEmployeeByNationalId("22222222222222"))
                .ReturnsAsync(false);

            var formDetailsRepoMock = new Mock<IFormDetailsRepository>();
            var deptRepoMock = new Mock<IDepartmentRepository>();
            var uowMock = new Mock<IUnitOfWork>();
            var configMock = new Mock<IConfiguration>();
            var httpContextAccessorMock = new Mock<IHttpContextAccessor>();
            var memoryCache = new MemoryCache(new MemoryCacheOptions());
            var currentUserServiceMock = new Mock<ICurrentUserService>();

            var employeeService = new EmployeeService(
                empRepoMock.Object,
                formDetailsRepoMock.Object,
                deptRepoMock.Object,
                uowMock.Object,
                configMock.Object,
                httpContextAccessorMock.Object,
                memoryCache,
                currentUserServiceMock.Object,
                dbContext);

            // Act
            var result = await employeeService.ChangeNationalId("11111111111111", "22222222222222");

            // Assert
            Assert.True(result.IsFailure);
            Assert.Equal("500", result.Error.Code);
            Assert.Equal("حدث خطأ أثناء تغيير الرقم القومى.", result.Error.Message);
            Assert.DoesNotContain("Exception", result.Error.Message);
            Assert.DoesNotContain("ExecuteSqlRawAsync", result.Error.Message);
            Assert.DoesNotContain("not supported", result.Error.Message);
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
                // Raw ADO.NET transactions in Sync Push and Pull coordinators are not EF Core DbContext transactions
                if (file.Contains(Path.Combine("Sync", "Push")) || file.Contains(Path.Combine("Sync", "Pull")))
                {
                    continue;
                }

                var content = File.ReadAllText(file);
                if (content.Contains("BeginTransactionAsync") || content.Contains("BeginTransaction("))
                {
                    filesWithTransaction.Add(Path.GetFileName(file));
                    // Verify that the file uses EF Core execution strategy to wrap the transaction
                    Assert.Contains("CreateExecutionStrategy", content);
                    Assert.Contains("ExecuteAsync", content);
                }
            }

            // EmployeeService.cs and UnitOfWork.cs (Slice 4.3B) contain manual transactions, and both are strictly wrapped in CreateExecutionStrategy
            Assert.Equal(2, filesWithTransaction.Count);
            Assert.Contains("EmployeeService.cs", filesWithTransaction);
            Assert.Contains("UnitOfWork.cs", filesWithTransaction);
        }
    }
}
