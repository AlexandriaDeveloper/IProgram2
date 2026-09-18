using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Application.Dtos;
using Application.Dtos.Requests;
using Application.Features;
using Application.Interfaces;
using Application.Services;
using Core.Interfaces;
using Core.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Auth.UnitTests
{
    public class NullSafetyAndNotFoundTests
    {
        [Fact]
        public async Task MarkFormDetailsAsReviewed_WhenDetailNotFound_ReturnsNotFound_WithoutNullReferenceException()
        {
            // Arrange
            var formRepoMock = new Mock<IFormRepository>();
            var formRefRepoMock = new Mock<IFormReferencesRepository>();
            var formDetailsRepoMock = new Mock<IFormDetailsRepository>();
            var dailyRepoMock = new Mock<IDailyRepository>();
            var uowMock = new Mock<IUnitOfWork>();
            var httpContextAccessorMock = new Mock<IHttpContextAccessor>();
            var userManagerMock = CreateMockUserManager();
            var loggerMock = new Mock<ILogger<FormDetailsService>>();
            var currentUserServiceMock = new Mock<ICurrentUserService>();
            var watchListRepoMock = new Mock<IEmployeeWatchListRepository>();
            var empRepoMock = new Mock<IEmployeeRepository>();
            var memoryCache = new MemoryCache(new MemoryCacheOptions());
            var keyFactoryMock = new Mock<IDbCacheKeyFactory>();

            var watchListService = new WatchListService(
                watchListRepoMock.Object,
                empRepoMock.Object,
                uowMock.Object,
                currentUserServiceMock.Object);

            var guard = new DailyClosureGuard(
                dailyRepoMock.Object,
                formRepoMock.Object,
                formDetailsRepoMock.Object,
                new Mock<IDailyReferencesRepository>().Object,
                formRefRepoMock.Object);

            // User is non-admin
            var claimsPrincipal = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "some-user")
            }, "TestAuth"));
            var httpContext = new DefaultHttpContext { User = claimsPrincipal };
            httpContextAccessorMock.Setup(h => h.HttpContext).Returns(httpContext);
            currentUserServiceMock.Setup(u => u.UserId).Returns("some-user");

            int nonExistentDetailId = 9999;
            formDetailsRepoMock.Setup(r => r.GetById(nonExistentDetailId))
                .ReturnsAsync((FormDetails?)null);

            var service = new FormDetailsService(
                formRepoMock.Object,
                formRefRepoMock.Object,
                formDetailsRepoMock.Object,
                dailyRepoMock.Object,
                uowMock.Object,
                httpContextAccessorMock.Object,
                memoryCache,
                keyFactoryMock.Object,
                userManagerMock.Object,
                loggerMock.Object,
                currentUserServiceMock.Object,
                watchListService,
                guard);

            // Act & Assert - must NOT throw NullReferenceException
            var result = await service.MarkFormDetailsAsReviewed(nonExistentDetailId, true);

            Assert.True(result.IsFailure);
            Assert.Equal("404", result.Error.Code);
        }

        [Fact]
        public async Task EditDaily_WhenDailyNotFound_ReturnsNotFound_WithoutNullReferenceException()
        {
            // Arrange
            var dailyRepoMock = new Mock<IDailyRepository>();
            var formRepoMock = new Mock<IFormRepository>();
            var uowMock = new Mock<IUnitOfWork>();
            var userManagerMock = CreateMockUserManager();
            var configMock = new Mock<IConfiguration>();
            var netPayRepoMock = new Mock<IEmployeeNetPayRepository>();
            var watchListRepoMock = new Mock<IEmployeeWatchListRepository>();
            var empRepoMock = new Mock<IEmployeeRepository>();
            var currentUserServiceMock = new Mock<ICurrentUserService>();

            var watchListService = new WatchListService(
                watchListRepoMock.Object,
                empRepoMock.Object,
                uowMock.Object,
                currentUserServiceMock.Object);

            var guard = new DailyClosureGuard(
                dailyRepoMock.Object,
                formRepoMock.Object,
                new Mock<IFormDetailsRepository>().Object,
                new Mock<IDailyReferencesRepository>().Object,
                new Mock<IFormReferencesRepository>().Object);

            var dailyService = new DailyService(
                dailyRepoMock.Object,
                formRepoMock.Object,
                null!,
                uowMock.Object,
                userManagerMock.Object,
                configMock.Object,
                netPayRepoMock.Object,
                watchListService,
                guard);

            int nonExistentDailyId = 9999;
            dailyRepoMock.Setup(r => r.GetById(nonExistentDailyId))
                .ReturnsAsync((Daily?)null);

            // Act & Assert - must NOT throw NullReferenceException
            var result = await dailyService.EditDaily(new DailyDto { Id = nonExistentDailyId, Name = "NonExistent" }, CancellationToken.None);

            Assert.True(result.IsFailure);
            Assert.Equal("404", result.Error.Code);
        }

        [Fact]
        public async Task AddEmployeeToFormDetails_WhenParentFormNotFound_ReturnsNotFound()
        {
            // Arrange
            var formRepoMock = new Mock<IFormRepository>();
            var formRefRepoMock = new Mock<IFormReferencesRepository>();
            var formDetailsRepoMock = new Mock<IFormDetailsRepository>();
            var dailyRepoMock = new Mock<IDailyRepository>();
            var uowMock = new Mock<IUnitOfWork>();
            var httpContextAccessorMock = new Mock<IHttpContextAccessor>();
            var userManagerMock = CreateMockUserManager();
            var loggerMock = new Mock<ILogger<FormDetailsService>>();
            var currentUserServiceMock = new Mock<ICurrentUserService>();
            var watchListRepoMock = new Mock<IEmployeeWatchListRepository>();
            var empRepoMock = new Mock<IEmployeeRepository>();
            var memoryCache = new MemoryCache(new MemoryCacheOptions());
            var keyFactoryMock = new Mock<IDbCacheKeyFactory>();

            var watchListService = new WatchListService(
                watchListRepoMock.Object,
                empRepoMock.Object,
                uowMock.Object,
                currentUserServiceMock.Object);

            var guard = new DailyClosureGuard(
                dailyRepoMock.Object,
                formRepoMock.Object,
                formDetailsRepoMock.Object,
                new Mock<IDailyReferencesRepository>().Object,
                formRefRepoMock.Object);

            int nonExistentFormId = 8888;
            formRepoMock.Setup(r => r.GetById(nonExistentFormId))
                .ReturnsAsync((Form?)null);

            var service = new FormDetailsService(
                formRepoMock.Object,
                formRefRepoMock.Object,
                formDetailsRepoMock.Object,
                dailyRepoMock.Object,
                uowMock.Object,
                httpContextAccessorMock.Object,
                memoryCache,
                keyFactoryMock.Object,
                userManagerMock.Object,
                loggerMock.Object,
                currentUserServiceMock.Object,
                watchListService,
                guard);

            // Act
            var result = await service.AddEmployeeToFormDetails(new FormDetailsRequest
            {
                FormId = nonExistentFormId,
                EmployeeId = "12345678901234",
                Amount = 50
            });

            // Assert
            Assert.True(result.IsFailure);
            Assert.Equal("404", result.Error.Code);
        }

        private static Mock<UserManager<ApplicationUser>> CreateMockUserManager()
        {
            var store = new Mock<IUserStore<ApplicationUser>>();
            return new Mock<UserManager<ApplicationUser>>(store.Object, null!, null!, null!, null!, null!, null!, null!, null!);
        }
    }
}
