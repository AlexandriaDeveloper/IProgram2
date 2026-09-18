using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using Application.Features;
using Application.Interfaces;
using Core.Interfaces;
using Core.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Moq;
using Persistence.Helpers;
using Persistence.Specifications;
using Xunit;

namespace Auth.UnitTests
{
    public class FormArchivedCountTests
    {
        [Fact]
        public async Task GetArchivedForms_NonAdminUser_PassesUserFilteredSpecToCountAsync()
        {
            // Arrange
            var formRepoMock = new Mock<IFormRepository>();
            var uowMock = new Mock<IUnitOfWork>();
            var httpContextAccessorMock = new Mock<IHttpContextAccessor>();
            var currentUserServiceMock = new Mock<ICurrentUserService>();
            var userManagerMock = CreateMockUserManager();
            var guardMock = new Mock<IDailyClosureGuard>();

            string currentUserId = "user-regular-123";
            currentUserServiceMock.Setup(s => s.UserId).Returns(currentUserId);

            // User is NOT Admin
            var claimsPrincipal = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, currentUserId)
            }, "TestAuth"));
            var httpContext = new DefaultHttpContext { User = claimsPrincipal };
            httpContextAccessorMock.Setup(h => h.HttpContext).Returns(httpContext);

            formRepoMock.Setup(r => r.ListAllAsync(It.IsAny<ISpecification<Form>>()))
                .ReturnsAsync(new List<Form>());

            userManagerMock.Setup(m => m.Users)
                .Returns(new List<ApplicationUser>().AsAsyncQueryable());

            ISpecification<Form>? capturedCountSpec = null;
            formRepoMock.Setup(r => r.CountAsync(It.IsAny<ISpecification<Form>>()))
                .Callback<ISpecification<Form>>(spec => capturedCountSpec = spec)
                .ReturnsAsync(0);

            var service = new FormArchivedService(
                formRepoMock.Object,
                uowMock.Object,
                httpContextAccessorMock.Object,
                userManagerMock.Object,
                currentUserServiceMock.Object,
                guardMock.Object);

            var param = new FormArchivedParam { PageIndex = 1, PageSize = 10 };

            // Act
            var result = await service.GetArchivedForms(param);

            // Assert
            Assert.True(result.IsSuccess);
            Assert.NotNull(capturedCountSpec);

            // Verify that capturedCountSpec contains the user criteria!
            Assert.NotEmpty(capturedCountSpec!.Criterias);
        }

        [Fact]
        public async Task GetArchivedForms_AdminUser_DoesNotAddUserFilterToCountAsync()
        {
            // Arrange
            var formRepoMock = new Mock<IFormRepository>();
            var uowMock = new Mock<IUnitOfWork>();
            var httpContextAccessorMock = new Mock<IHttpContextAccessor>();
            var currentUserServiceMock = new Mock<ICurrentUserService>();
            var userManagerMock = CreateMockUserManager();
            var guardMock = new Mock<IDailyClosureGuard>();

            string adminUserId = "admin-user-456";
            currentUserServiceMock.Setup(s => s.UserId).Returns(adminUserId);

            // User IS Admin
            var claimsPrincipal = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, adminUserId),
                new Claim(ClaimTypes.Role, "Admin")
            }, "TestAuth"));
            var httpContext = new DefaultHttpContext { User = claimsPrincipal };
            httpContextAccessorMock.Setup(h => h.HttpContext).Returns(httpContext);

            formRepoMock.Setup(r => r.ListAllAsync(It.IsAny<ISpecification<Form>>()))
                .ReturnsAsync(new List<Form>());

            userManagerMock.Setup(m => m.Users)
                .Returns(new List<ApplicationUser>().AsAsyncQueryable());

            ISpecification<Form>? capturedCountSpec = null;
            formRepoMock.Setup(r => r.CountAsync(It.IsAny<ISpecification<Form>>()))
                .Callback<ISpecification<Form>>(spec => capturedCountSpec = spec)
                .ReturnsAsync(0);

            var service = new FormArchivedService(
                formRepoMock.Object,
                uowMock.Object,
                httpContextAccessorMock.Object,
                userManagerMock.Object,
                currentUserServiceMock.Object,
                guardMock.Object);

            var param = new FormArchivedParam { PageIndex = 1, PageSize = 10 };

            // Act
            var result = await service.GetArchivedForms(param);

            // Assert
            Assert.True(result.IsSuccess);
            Assert.NotNull(capturedCountSpec);

            // Admin should not have user filter criteria added
            Assert.Empty(capturedCountSpec!.Criterias);
        }

        private static Mock<UserManager<ApplicationUser>> CreateMockUserManager()
        {
            var store = new Mock<IUserStore<ApplicationUser>>();
            return new Mock<UserManager<ApplicationUser>>(store.Object, null!, null!, null!, null!, null!, null!, null!, null!);
        }
    }
}
