using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Application.Dtos;
using Application.Features;
using Auth.Infrastructure.Services;
using Core.Interfaces;
using Core.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Auth.UnitTests
{
    public class ServiceCacheIntegrationTests
    {
        [Fact]
        public async Task FormDetailsService_Uses_Scoped_CacheKey_For_Clearing_Cache()
        {
            // Arrange
            var mockDbProvider = new Mock<IDbConnectionProvider>();
            mockDbProvider.Setup(p => p.GetSelectedDatabaseId()).Returns("2026");
            var keyFactory = new DbCacheKeyFactory(mockDbProvider.Object);

            var memoryCache = new MemoryCache(new MemoryCacheOptions());
            var formKey2026 = keyFactory.GetFormDetailsKey(100);
            var formKey2027 = "2027:FormDetails:100";

            memoryCache.Set(formKey2026, new FormDto { Id = 100, Name = "Form 2026" });
            memoryCache.Set(formKey2027, new FormDto { Id = 100, Name = "Form 2027" });

            // Create FormDetailsService with mocked dependencies
            var formRepoMock = new Mock<IFormRepository>();
            var formRefRepoMock = new Mock<IFormReferencesRepository>();
            var formDetailsRepoMock = new Mock<IFormDetailsRepository>();
            var dailyRepoMock = new Mock<IDailyRepository>();
            var uowMock = new Mock<IUnitOfWork>();
            var httpContextAccessorMock = new Mock<IHttpContextAccessor>();
            var userManagerMock = CreateMockUserManager();
            var loggerMock = new Mock<ILogger<FormDetailsService>>();
            var currentUserServiceMock = new Mock<ICurrentUserService>();
            var empRepoMock = new Mock<IEmployeeRepository>();
            var watchListRepoMock = new Mock<IEmployeeWatchListRepository>();
            var watchListService = new WatchListService(
                watchListRepoMock.Object, 
                empRepoMock.Object, 
                uowMock.Object, 
                currentUserServiceMock.Object);

            var service = new FormDetailsService(
                formRepoMock.Object,
                formRefRepoMock.Object,
                formDetailsRepoMock.Object,
                dailyRepoMock.Object,
                uowMock.Object,
                httpContextAccessorMock.Object,
                memoryCache,
                keyFactory,
                userManagerMock.Object,
                loggerMock.Object,
                currentUserServiceMock.Object,
                watchListService);

            // Mock DB lookup and update for ReOrderRows (which calls ClearFormDetailsCache)
            formRepoMock.Setup(r => r.GetById(100)).ReturnsAsync(new Form { Id = 100, DailyId = null });
            formDetailsRepoMock.Setup(r => r.GetQueryable()).Returns(new List<FormDetails>().AsQueryable());
            uowMock.Setup(u => u.SaveChangesAsync()).ReturnsAsync(1);

            // Act
            await service.ReOrderRows(100, Array.Empty<int>());

            // Assert: 2026 key was cleared, but 2027 key remains intact
            Assert.False(memoryCache.TryGetValue(formKey2026, out _));
            Assert.True(memoryCache.TryGetValue(formKey2027, out FormDto? remaining));
            Assert.NotNull(remaining);
            Assert.Equal("Form 2027", remaining.Name);
        }

        [Fact]
        public async Task DepartmentService_Uses_Scoped_CacheKey_For_Clearing_Cache()
        {
            // Arrange
            var mockDbProvider = new Mock<IDbConnectionProvider>();
            mockDbProvider.Setup(p => p.GetSelectedDatabaseId()).Returns("2026");
            var keyFactory = new DbCacheKeyFactory(mockDbProvider.Object);

            var memoryCache = new MemoryCache(new MemoryCacheOptions());
            var deptKey2026 = keyFactory.CreateKey("departments_all");
            var deptKey2027 = "2027:departments_all";

            memoryCache.Set(deptKey2026, "Dept2026");
            memoryCache.Set(deptKey2027, "Dept2027");

            var deptRepoMock = new Mock<IDepartmentRepository>();
            var empRepoMock = new Mock<IEmployeeRepository>();
            var uowMock = new Mock<IUnitOfWork>();

            deptRepoMock.Setup(r => r.Insert(It.IsAny<Department>())).Returns(Task.CompletedTask);
            uowMock.Setup(u => u.SaveChangesAsync()).ReturnsAsync(1);

            var service = new DepartmentService(
                deptRepoMock.Object,
                empRepoMock.Object,
                uowMock.Object,
                memoryCache,
                keyFactory);

            // Act: AddDepartment calls ClearDepartmentCache()
            await service.AddDepartment(new DepartmentDto { Name = "New Dept" });

            // Assert: 2026 cache removed, 2027 cache preserved
            Assert.False(memoryCache.TryGetValue(deptKey2026, out _));
            Assert.True(memoryCache.TryGetValue(deptKey2027, out string? val));
            Assert.Equal("Dept2027", val);
        }

        private static Mock<UserManager<ApplicationUser>> CreateMockUserManager()
        {
            var store = new Mock<IUserStore<ApplicationUser>>();
            return new Mock<UserManager<ApplicationUser>>(
                store.Object, null!, null!, null!, null!, null!, null!, null!, null!);
        }
    }
}
