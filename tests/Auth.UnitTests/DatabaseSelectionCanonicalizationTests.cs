using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Auth.Api.Middleware;
using Auth.Infrastructure.Services;
using Core.Exceptions;
using Core.Interfaces;
using Core.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Persistence.Services;
using Xunit;

namespace Auth.UnitTests
{
    public class DatabaseSelectionCanonicalizationTests
    {
        private readonly IConfiguration _standardConfig;

        public DatabaseSelectionCanonicalizationTests()
        {
            var inMemoryConfig = new Dictionary<string, string?>
            {
                { "ConnectionStrings:DefaultConnection", "Server=localhost;Database=IProgramDb2026;" },
                { "ConnectionStrings:CON2027", "Server=localhost;Database=IProgramDb2027;" },
                { "DatabaseSettings:Databases:0:Id", "2026" },
                { "DatabaseSettings:Databases:0:Name", "بيانات سنة 2026" },
                { "DatabaseSettings:Databases:0:ConnectionStringName", "DefaultConnection" },
                { "DatabaseSettings:Databases:1:Id", "2027" },
                { "DatabaseSettings:Databases:1:Name", "بيانات سنة 2027" },
                { "DatabaseSettings:Databases:1:ConnectionStringName", "CON2027" },
                { "Token:Key", "super_secret_jwt_signing_key_for_unit_tests_only_minimum_64_characters_long_for_hmac_sha512!" },
                { "Token:Issuer", "http://localhost" }
            };

            _standardConfig = new ConfigurationBuilder()
                .AddInMemoryCollection(inMemoryConfig)
                .Build();
        }

        private (DbConnectionProvider provider, DbCacheKeyFactory cacheKeyFactory, DefaultHttpContext httpContext) CreateSystem(
            IConfiguration? config = null,
            DefaultHttpContext? context = null)
        {
            var activeConfig = config ?? _standardConfig;
            var httpContext = context ?? new DefaultHttpContext();
            var httpContextAccessor = new HttpContextAccessor { HttpContext = httpContext };

            var provider = new DbConnectionProvider(httpContextAccessor, activeConfig);
            var cacheKeyFactory = new DbCacheKeyFactory(provider);

            return (provider, cacheKeyFactory, httpContext);
        }

        [Fact]
        public void ValidHeader_2027_ResolvesTo_CanonicalId_Connection_And_CacheKey()
        {
            // Arrange
            var context = new DefaultHttpContext();
            context.Request.Headers["X-Db-Selection"] = "2027";
            var (provider, cacheKeyFactory, _) = CreateSystem(context: context);

            // Act
            var selectedId = provider.GetSelectedDatabaseId();
            var connStr = provider.GetConnectionString();
            var cacheKey = cacheKeyFactory.GetFormDetailsKey(101);

            // Assert
            Assert.Equal("2027", selectedId);
            Assert.Equal("Server=localhost;Database=IProgramDb2027;", connStr);
            Assert.Equal("2027:FormDetails:101", cacheKey);
        }

        [Fact]
        public void Header_WithWhitespace_2027_ResolvesTo_CanonicalId_Connection_And_CacheKey()
        {
            // Arrange
            var context = new DefaultHttpContext();
            context.Request.Headers["X-Db-Selection"] = "  2027  ";
            var (provider, cacheKeyFactory, _) = CreateSystem(context: context);

            // Act
            var selectedId = provider.GetSelectedDatabaseId();
            var connStr = provider.GetConnectionString();
            var cacheKey = cacheKeyFactory.GetFormDetailsKey(101);

            // Assert
            Assert.Equal("2027", selectedId);
            Assert.Equal("Server=localhost;Database=IProgramDb2027;", connStr);
            Assert.Equal("2027:FormDetails:101", cacheKey);
        }

        [Fact]
        public void QueryParam_WithWhitespace_2027_ResolvesTo_CanonicalId()
        {
            // Arrange
            var context = new DefaultHttpContext();
            context.Request.QueryString = new QueryString("?dbId=%202027%20");
            var (provider, cacheKeyFactory, _) = CreateSystem(context: context);

            // Act
            var selectedId = provider.GetSelectedDatabaseId();
            var connStr = provider.GetConnectionString();

            // Assert
            Assert.Equal("2027", selectedId);
            Assert.Equal("Server=localhost;Database=IProgramDb2027;", connStr);
        }

        [Fact]
        public void AuthenticatedJwtClaim_WithWhitespace_ResolvesTo_CanonicalId()
        {
            // Arrange
            var context = new DefaultHttpContext();
            var identity = new ClaimsIdentity(new[] { new Claim("db", "  2027  ") }, "TestAuth");
            context.User = new ClaimsPrincipal(identity);
            var (provider, _, _) = CreateSystem(context: context);

            // Act
            var selectedId = provider.GetSelectedDatabaseId();
            var connStr = provider.GetConnectionString();

            // Assert
            Assert.Equal("2027", selectedId);
            Assert.Equal("Server=localhost;Database=IProgramDb2027;", connStr);
        }

        [Fact]
        public void Precedence_AuthenticatedJwtClaim_Overrides_Header_And_Query()
        {
            // Arrange: JWT says 2026, Header says 2027, Query says 2027
            var context = new DefaultHttpContext();
            var identity = new ClaimsIdentity(new[] { new Claim("db", "2026") }, "TestAuth");
            context.User = new ClaimsPrincipal(identity);
            context.Request.Headers["X-Db-Selection"] = "2027";
            context.Request.QueryString = new QueryString("?dbId=2027");

            var (provider, cacheKeyFactory, _) = CreateSystem(context: context);

            // Act
            var selectedId = provider.GetSelectedDatabaseId();
            var connStr = provider.GetConnectionString();
            var cacheKey = cacheKeyFactory.GetFormDetailsKey(50);

            // Assert: JWT claim authority wins strictly
            Assert.Equal("2026", selectedId);
            Assert.Equal("Server=localhost;Database=IProgramDb2026;", connStr);
            Assert.Equal("2026:FormDetails:50", cacheKey);
        }

        [Fact]
        public void InvalidHeader_Abc_FailsClosed_ThrowsInvalidDatabaseSelectionException_NoFallback()
        {
            // Arrange
            var context = new DefaultHttpContext();
            context.Request.Headers["X-Db-Selection"] = "abc";
            var (provider, _, _) = CreateSystem(context: context);

            // Act & Assert: Must fail closed, never silently fall back to DefaultConnection
            Assert.Throws<InvalidDatabaseSelectionException>(() => provider.GetSelectedDatabaseId());
            Assert.Throws<InvalidDatabaseSelectionException>(() => provider.GetConnectionString());
        }

        [Fact]
        public void InvalidQuery_Xyz_FailsClosed_ThrowsInvalidDatabaseSelectionException_NoFallback()
        {
            // Arrange
            var context = new DefaultHttpContext();
            context.Request.QueryString = new QueryString("?dbId=xyz");
            var (provider, _, _) = CreateSystem(context: context);

            // Act & Assert: Fail closed
            Assert.Throws<InvalidDatabaseSelectionException>(() => provider.GetSelectedDatabaseId());
            Assert.Throws<InvalidDatabaseSelectionException>(() => provider.GetConnectionString());
        }

        [Fact]
        public void InvalidJwtClaim_Def_FailsClosed_NoFallbackToHeaderOrQueryOrDefault()
        {
            // Arrange
            var context = new DefaultHttpContext();
            var identity = new ClaimsIdentity(new[] { new Claim("db", "def") }, "TestAuth");
            context.User = new ClaimsPrincipal(identity);
            context.Request.Headers["X-Db-Selection"] = "2026"; // Header should not be used as fallback!

            var (provider, _, _) = CreateSystem(context: context);

            // Act & Assert: Fail closed, never fall back to header or default
            Assert.Throws<InvalidDatabaseSelectionException>(() => provider.GetSelectedDatabaseId());
            Assert.Throws<InvalidDatabaseSelectionException>(() => provider.GetConnectionString());
        }

        [Fact]
        public void NoSelector_ResolvesTo_CanonicalDefaultConfiguredDatabase()
        {
            // Arrange: Unauthenticated, no headers, no query
            var context = new DefaultHttpContext();
            var (provider, cacheKeyFactory, _) = CreateSystem(context: context);

            // Act
            var selectedId = provider.GetSelectedDatabaseId();
            var connStr = provider.GetConnectionString();
            var cacheKey = cacheKeyFactory.GetFormDetailsKey(1);

            // Assert: Resolves to first configured database (2026)
            Assert.Equal("2026", selectedId);
            Assert.Equal("Server=localhost;Database=IProgramDb2026;", connStr);
            Assert.Equal("2026:FormDetails:1", cacheKey);
        }

        [Fact]
        public void ConfiguredDatabase_WithMissingConnectionString_FailsClosed_ThrowsDatabaseConfigurationException()
        {
            // Arrange: DB 2028 is configured, but its connection string is missing from ConnectionStrings
            var brokenConfig = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    { "ConnectionStrings:DefaultConnection", "Server=localhost;Database=IProgramDb2026;" },
                    { "DatabaseSettings:Databases:0:Id", "2026" },
                    { "DatabaseSettings:Databases:0:ConnectionStringName", "DefaultConnection" },
                    { "DatabaseSettings:Databases:1:Id", "2028" },
                    { "DatabaseSettings:Databases:1:ConnectionStringName", "CON2028_NON_EXISTENT" }
                })
                .Build();

            var context = new DefaultHttpContext();
            context.Request.Headers["X-Db-Selection"] = "2028";
            var (provider, _, _) = CreateSystem(config: brokenConfig, context: context);

            // Act & Assert: Must fail closed with DatabaseConfigurationException, no silent fallback to 2026
            Assert.Throws<DatabaseConfigurationException>(() => provider.GetConnectionString());
        }

        [Fact]
        public void DuplicateOrAmbiguousConfiguredIds_AreDetectedAndRejected()
        {
            // Arrange: Config contains duplicate IDs after normalization ("2027" and " 2027 ")
            var duplicateConfig = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    { "ConnectionStrings:CON1", "Server=localhost;Database=DB1;" },
                    { "ConnectionStrings:CON2", "Server=localhost;Database=DB2;" },
                    { "DatabaseSettings:Databases:0:Id", "2027" },
                    { "DatabaseSettings:Databases:0:ConnectionStringName", "CON1" },
                    { "DatabaseSettings:Databases:1:Id", " 2027 " },
                    { "DatabaseSettings:Databases:1:ConnectionStringName", "CON2" }
                })
                .Build();

            var (provider, _, _) = CreateSystem(config: duplicateConfig);

            // Act & Assert: Must throw DatabaseConfigurationException due to ambiguous duplicate IDs
            Assert.Throws<DatabaseConfigurationException>(() => provider.GetSelectedDatabaseId());
        }

        [Fact]
        public async Task TokenService_GeneratesCanonicalDbClaim_FromNormalizedInput()
        {
            // Arrange: Login header contains whitespace " 2027 "
            var context = new DefaultHttpContext();
            context.Request.Headers["X-Db-Selection"] = "  2027  ";
            var (provider, _, _) = CreateSystem(context: context);

            var userStoreMock = new Mock<IUserStore<ApplicationUser>>();
            var userManagerMock = new Mock<UserManager<ApplicationUser>>(userStoreMock.Object, null!, null!, null!, null!, null!, null!, null!, null!);
            userManagerMock.Setup(m => m.GetRolesAsync(It.IsAny<ApplicationUser>()))
                .ReturnsAsync(new List<string> { "User" });

            var tokenService = new TokenService(_standardConfig, userManagerMock.Object, provider);
            var user = new ApplicationUser { Id = "user-1", Email = "test@example.com", DisplayName = "مستخدم" };

            // Act
            var tokenString = await tokenService.CreateToken(user);

            // Assert: Token must contain canonical claim "db" = "2027"
            var handler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
            var jwt = handler.ReadJwtToken(tokenString);
            var dbClaim = jwt.Claims.FirstOrDefault(c => c.Type == "db")?.Value;

            Assert.Equal("2027", dbClaim);
        }

        [Theory]
        [InlineData("2026", "Server=localhost;Database=IProgramDb2026;")]
        [InlineData("2027", "Server=localhost;Database=IProgramDb2027;")]
        [InlineData(" 2027 ", "Server=localhost;Database=IProgramDb2027;")]
        public void InvariantTest_ConnectionDbIdentity_Equals_SelectedDbIdentity_Equals_CacheDbIdentity(string inputSelector, string expectedConn)
        {
            // Arrange
            var context = new DefaultHttpContext();
            context.Request.Headers["X-Db-Selection"] = inputSelector;
            var (provider, cacheKeyFactory, _) = CreateSystem(context: context);

            // Act
            var selectedDbId = provider.GetSelectedDatabaseId();
            var connStr = provider.GetConnectionString();
            var cacheDbId = cacheKeyFactory.GetCurrentDatabaseId();

            // Assert the core invariant:
            // Connection DB Identity == Request Selected DB Identity == Cache DB Identity
            Assert.Equal(cacheDbId, selectedDbId);
            Assert.Equal(expectedConn, connStr);

            var cacheKey = cacheKeyFactory.CreateKey("TestEntity", 999);
            Assert.StartsWith($"{selectedDbId}:TestEntity:999", cacheKey);
        }

        [Fact]
        public async Task GlobalExceptionHandler_Maps_InvalidDatabaseSelectionException_To_Http400_WithGenericMessage()
        {
            // Arrange
            var loggerMock = new Mock<ILogger<GlobalExceptionHandler>>();
            var handler = new GlobalExceptionHandler(loggerMock.Object);

            var context = new DefaultHttpContext();
            context.TraceIdentifier = "trace-invalid-db-400";
            context.Response.Body = new MemoryStream();

            var ex = new InvalidDatabaseSelectionException("Invalid database selection 'xyz' from source 'Header'.");

            // Act
            var handled = await handler.TryHandleAsync(context, ex, CancellationToken.None);

            // Assert
            Assert.True(handled);
            Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);

            context.Response.Body.Seek(0, SeekOrigin.Begin);
            using var reader = new StreamReader(context.Response.Body);
            var responseBody = await reader.ReadToEndAsync();

            var responseJson = JsonSerializer.Deserialize<ErrorResponseDto>(responseBody, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            Assert.NotNull(responseJson);
            Assert.Equal(400, responseJson.StatusCode);
            Assert.Equal("قاعدة البيانات المحددة غير صالحة.", responseJson.Message);
            Assert.Equal("trace-invalid-db-400", responseJson.TraceId);

            // Assert raw exception details are not leaked
            Assert.DoesNotContain("xyz", responseBody);
            Assert.DoesNotContain("InvalidDatabaseSelectionException", responseBody);
        }
    }
}
