using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Application.Dtos;
using Application.Dtos.Requests;
using Application.Features;
using Application.Helpers;
using Application.Interfaces;
using Application.Services;
using Core.Interfaces;
using Core.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Auth.UnitTests
{
    public class DailyClosureGuardTests
    {
        private readonly Mock<IDailyRepository> _mockDailyRepo = new();
        private readonly Mock<IFormRepository> _mockFormRepo = new();
        private readonly Mock<IFormDetailsRepository> _mockFormDetailsRepo = new();
        private readonly Mock<IDailyReferencesRepository> _mockDailyRefRepo = new();
        private readonly Mock<IFormReferencesRepository> _mockFormRefRepo = new();
        private readonly DailyClosureGuard _guard;

        public DailyClosureGuardTests()
        {
            _guard = new DailyClosureGuard(
                _mockDailyRepo.Object,
                _mockFormRepo.Object,
                _mockFormDetailsRepo.Object,
                _mockDailyRefRepo.Object,
                _mockFormRefRepo.Object);
        }

        [Fact]
        public async Task EnsureDailyOpenAsync_WhenDailyClosed_ReturnsBadRequestWithArabicMessage()
        {
            // Arrange
            int dailyId = 1;
            _mockDailyRepo.Setup(r => r.GetById(dailyId))
                .ReturnsAsync(new Daily { Id = dailyId, Closed = true });

            // Act
            var result = await _guard.EnsureDailyOpenAsync(dailyId);

            // Assert
            Assert.True(result.IsFailure);
            Assert.Equal("400", result.Error.Code);
            Assert.Equal("لا يمكن تعديل بيانات يومية مغلقة. قم بإعادة فتح اليومية أولًا.", result.Error.Message);
        }

        [Fact]
        public async Task EnsureDailyOpenAsync_WhenDailyOpen_ReturnsSuccess()
        {
            // Arrange
            int dailyId = 2;
            _mockDailyRepo.Setup(r => r.GetById(dailyId))
                .ReturnsAsync(new Daily { Id = dailyId, Closed = false });

            // Act
            var result = await _guard.EnsureDailyOpenAsync(dailyId);

            // Assert
            Assert.True(result.IsSuccess);
        }

        [Fact]
        public async Task EnsureDailyOpenAsync_WhenDailyNotFound_ReturnsNotFound()
        {
            // Arrange
            int dailyId = 999;
            _mockDailyRepo.Setup(r => r.GetById(dailyId))
                .ReturnsAsync((Daily?)null);

            // Act
            var result = await _guard.EnsureDailyOpenAsync(dailyId);

            // Assert
            Assert.True(result.IsFailure);
            Assert.Equal("404", result.Error.Code);
        }

        [Fact]
        public async Task EnsureFormDailyOpenAsync_WhenFormInClosedDaily_ReturnsBadRequest()
        {
            // Arrange
            int formId = 10;
            int dailyId = 1;
            _mockFormRepo.Setup(r => r.GetById(formId))
                .ReturnsAsync(new Form { Id = formId, DailyId = dailyId });
            _mockDailyRepo.Setup(r => r.GetById(dailyId))
                .ReturnsAsync(new Daily { Id = dailyId, Closed = true });

            // Act
            var result = await _guard.EnsureFormDailyOpenAsync(formId);

            // Assert
            Assert.True(result.IsFailure);
            Assert.Equal("400", result.Error.Code);
            Assert.Equal("لا يمكن تعديل بيانات يومية مغلقة. قم بإعادة فتح اليومية أولًا.", result.Error.Message);
        }

        [Fact]
        public async Task EnsureFormDailyOpenAsync_WhenFormInOpenDaily_ReturnsSuccess()
        {
            // Arrange
            int formId = 11;
            int dailyId = 2;
            _mockFormRepo.Setup(r => r.GetById(formId))
                .ReturnsAsync(new Form { Id = formId, DailyId = dailyId });
            _mockDailyRepo.Setup(r => r.GetById(dailyId))
                .ReturnsAsync(new Daily { Id = dailyId, Closed = false });

            // Act
            var result = await _guard.EnsureFormDailyOpenAsync(formId);

            // Assert
            Assert.True(result.IsSuccess);
        }

        [Fact]
        public async Task EnsureFormDailyOpenAsync_WhenFormHasNoDaily_ReturnsSuccess()
        {
            // Arrange
            int formId = 12;
            _mockFormRepo.Setup(r => r.GetById(formId))
                .ReturnsAsync(new Form { Id = formId, DailyId = null });

            // Act
            var result = await _guard.EnsureFormDailyOpenAsync(formId);

            // Assert
            Assert.True(result.IsSuccess);
        }

        [Fact]
        public async Task EnsureFormDetailsDailyOpenAsync_WhenDetailInClosedDaily_ReturnsBadRequest()
        {
            // Arrange
            int detailId = 50;
            int formId = 10;
            int dailyId = 1;
            _mockFormDetailsRepo.Setup(r => r.GetById(detailId))
                .ReturnsAsync(new FormDetails { Id = detailId, FormId = formId });
            _mockFormRepo.Setup(r => r.GetById(formId))
                .ReturnsAsync(new Form { Id = formId, DailyId = dailyId });
            _mockDailyRepo.Setup(r => r.GetById(dailyId))
                .ReturnsAsync(new Daily { Id = dailyId, Closed = true });

            // Act
            var result = await _guard.EnsureFormDetailsDailyOpenAsync(detailId);

            // Assert
            Assert.True(result.IsFailure);
            Assert.Equal("400", result.Error.Code);
            Assert.Equal("لا يمكن تعديل بيانات يومية مغلقة. قم بإعادة فتح اليومية أولًا.", result.Error.Message);
        }

        [Fact]
        public async Task EnsureDailyReferenceDailyOpenAsync_WhenReferenceInClosedDaily_ReturnsBadRequest()
        {
            // Arrange
            int refId = 100;
            int dailyId = 1;
            _mockDailyRefRepo.Setup(r => r.GetById(refId))
                .ReturnsAsync(new DailyReference { Id = refId, DailyId = dailyId });
            _mockDailyRepo.Setup(r => r.GetById(dailyId))
                .ReturnsAsync(new Daily { Id = dailyId, Closed = true });

            // Act
            var result = await _guard.EnsureDailyReferenceDailyOpenAsync(refId);

            // Assert
            Assert.True(result.IsFailure);
            Assert.Equal("400", result.Error.Code);
        }

        [Fact]
        public async Task EnsureFormReferenceDailyOpenAsync_WhenFormReferenceInClosedDaily_ReturnsBadRequest()
        {
            // Arrange
            int refId = 200;
            int formId = 10;
            int dailyId = 1;
            _mockFormRefRepo.Setup(r => r.GetById(refId))
                .ReturnsAsync(new FormRefernce { Id = refId, FormId = formId });
            _mockFormRepo.Setup(r => r.GetById(formId))
                .ReturnsAsync(new Form { Id = formId, DailyId = dailyId });
            _mockDailyRepo.Setup(r => r.GetById(dailyId))
                .ReturnsAsync(new Daily { Id = dailyId, Closed = true });

            // Act
            var result = await _guard.EnsureFormReferenceDailyOpenAsync(refId);

            // Assert
            Assert.True(result.IsFailure);
            Assert.Equal("400", result.Error.Code);
        }

        [Fact]
        public async Task DailyService_EditDaily_WhenClosed_Fails()
        {
            // Arrange
            var dailyRepoMock = new Mock<IDailyRepository>();
            var uowMock = new Mock<IUnitOfWork>();
            var formRepoMock = new Mock<IFormRepository>();
            var userManagerMock = CreateMockUserManager();
            var netPayRepoMock = new Mock<IEmployeeNetPayRepository>();
            var watchListRepoMock = new Mock<IEmployeeWatchListRepository>();
            var empRepoMock = new Mock<IEmployeeRepository>();
            var currentUserServiceMock = new Mock<ICurrentUserService>();
            var configMock = new Mock<IConfiguration>();

            var watchListService = new WatchListService(
                watchListRepoMock.Object,
                empRepoMock.Object,
                uowMock.Object,
                currentUserServiceMock.Object);

            var dailyService = new DailyService(
                dailyRepoMock.Object,
                formRepoMock.Object,
                null!,
                uowMock.Object,
                userManagerMock.Object,
                configMock.Object,
                netPayRepoMock.Object,
                watchListService,
                _guard);

            int dailyId = 5;
            dailyRepoMock.Setup(r => r.GetById(dailyId))
                .ReturnsAsync(new Daily { Id = dailyId, Closed = true });

            // Act
            var result = await dailyService.EditDaily(new DailyDto { Id = dailyId, Name = "تعديل" }, CancellationToken.None);

            // Assert
            Assert.True(result.IsFailure);
            Assert.Equal("400", result.Error.Code);
            Assert.Equal("لا يمكن تعديل بيانات يومية مغلقة. قم بإعادة فتح اليومية أولًا.", result.Error.Message);
        }

        [Fact]
        public async Task DailyService_EditDaily_WhenOpen_Succeeds()
        {
            // Arrange
            var dailyRepoMock = new Mock<IDailyRepository>();
            var uowMock = new Mock<IUnitOfWork>();
            var formRepoMock = new Mock<IFormRepository>();
            var userManagerMock = CreateMockUserManager();
            var netPayRepoMock = new Mock<IEmployeeNetPayRepository>();
            var watchListRepoMock = new Mock<IEmployeeWatchListRepository>();
            var empRepoMock = new Mock<IEmployeeRepository>();
            var currentUserServiceMock = new Mock<ICurrentUserService>();
            var configMock = new Mock<IConfiguration>();

            uowMock.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

            var watchListService = new WatchListService(
                watchListRepoMock.Object,
                empRepoMock.Object,
                uowMock.Object,
                currentUserServiceMock.Object);

            var dailyService = new DailyService(
                dailyRepoMock.Object,
                formRepoMock.Object,
                null!,
                uowMock.Object,
                userManagerMock.Object,
                configMock.Object,
                netPayRepoMock.Object,
                watchListService,
                _guard);

            int dailyId = 6;
            dailyRepoMock.Setup(r => r.GetById(dailyId))
                .ReturnsAsync(new Daily { Id = dailyId, Closed = false });

            // Act
            var result = await dailyService.EditDaily(new DailyDto { Id = dailyId, Name = "اسم جديد" }, CancellationToken.None);

            // Assert
            Assert.True(result.IsSuccess);
        }

        [Fact]
        public async Task DailyService_UpdateBeneficiaryComment_WhenClosed_Fails()
        {
            // Arrange
            var dailyRepoMock = new Mock<IDailyRepository>();
            var uowMock = new Mock<IUnitOfWork>();
            var userManagerMock = CreateMockUserManager();
            var netPayRepoMock = new Mock<IEmployeeNetPayRepository>();
            var watchListRepoMock = new Mock<IEmployeeWatchListRepository>();
            var empRepoMock = new Mock<IEmployeeRepository>();
            var currentUserServiceMock = new Mock<ICurrentUserService>();
            var configMock = new Mock<IConfiguration>();

            var watchListService = new WatchListService(
                watchListRepoMock.Object,
                empRepoMock.Object,
                uowMock.Object,
                currentUserServiceMock.Object);

            var dailyService = new DailyService(
                dailyRepoMock.Object,
                new Mock<IFormRepository>().Object,
                null!,
                uowMock.Object,
                userManagerMock.Object,
                configMock.Object,
                netPayRepoMock.Object,
                watchListService,
                _guard);

            int dailyId = 7;
            var closedDaily = new Daily { Id = dailyId, Closed = true, Forms = new List<Form>() };
            dailyRepoMock.Setup(r => r.GetQueryable(It.IsAny<bool?>()))
                .Returns(new List<Daily> { closedDaily }.AsAsyncQueryable());

            // Act
            var result = await dailyService.UpdateBeneficiaryComment(dailyId, new UpdateBeneficiaryCommentRequest
            {
                EmployeeId = "123",
                Comment = "ملاحظة"
            });

            // Assert
            Assert.True(result.IsFailure);
            Assert.Equal("400", result.Error.Code);
            Assert.Equal("لا يمكن تعديل بيانات يومية مغلقة. قم بإعادة فتح اليومية أولًا.", result.Error.Message);
        }

        [Fact]
        public async Task DailyService_UpdateBeneficiaryNetPay_WhenClosed_Fails()
        {
            // Arrange
            var dailyRepoMock = new Mock<IDailyRepository>();
            var uowMock = new Mock<IUnitOfWork>();
            var userManagerMock = CreateMockUserManager();
            var netPayRepoMock = new Mock<IEmployeeNetPayRepository>();
            var watchListRepoMock = new Mock<IEmployeeWatchListRepository>();
            var empRepoMock = new Mock<IEmployeeRepository>();
            var currentUserServiceMock = new Mock<ICurrentUserService>();
            var configMock = new Mock<IConfiguration>();

            var watchListService = new WatchListService(
                watchListRepoMock.Object,
                empRepoMock.Object,
                uowMock.Object,
                currentUserServiceMock.Object);

            var dailyService = new DailyService(
                dailyRepoMock.Object,
                new Mock<IFormRepository>().Object,
                null!,
                uowMock.Object,
                userManagerMock.Object,
                configMock.Object,
                netPayRepoMock.Object,
                watchListService,
                _guard);

            int dailyId = 8;
            dailyRepoMock.Setup(r => r.GetById(dailyId))
                .ReturnsAsync(new Daily { Id = dailyId, Closed = true });

            // Act
            var result = await dailyService.UpdateBeneficiaryNetPay(dailyId, new UpdateBeneficiaryNetPayRequest
            {
                EmployeeId = "123",
                NetPay = 1500
            });

            // Assert
            Assert.True(result.IsFailure);
            Assert.Equal("400", result.Error.Code);
            Assert.Equal("لا يمكن تعديل بيانات يومية مغلقة. قم بإعادة فتح اليومية أولًا.", result.Error.Message);
        }

        [Fact]
        public async Task DailyService_ResetDailyReviews_WhenClosed_Fails()
        {
            // Arrange
            var dailyRepoMock = new Mock<IDailyRepository>();
            var uowMock = new Mock<IUnitOfWork>();
            var userManagerMock = CreateMockUserManager();
            var netPayRepoMock = new Mock<IEmployeeNetPayRepository>();
            var watchListRepoMock = new Mock<IEmployeeWatchListRepository>();
            var empRepoMock = new Mock<IEmployeeRepository>();
            var currentUserServiceMock = new Mock<ICurrentUserService>();
            var configMock = new Mock<IConfiguration>();

            var watchListService = new WatchListService(
                watchListRepoMock.Object,
                empRepoMock.Object,
                uowMock.Object,
                currentUserServiceMock.Object);

            var dailyService = new DailyService(
                dailyRepoMock.Object,
                new Mock<IFormRepository>().Object,
                null!,
                uowMock.Object,
                userManagerMock.Object,
                configMock.Object,
                netPayRepoMock.Object,
                watchListService,
                _guard);

            int dailyId = 9;
            var closedDaily = new Daily { Id = dailyId, Closed = true, Forms = new List<Form>() };
            dailyRepoMock.Setup(r => r.GetQueryable(It.IsAny<bool?>()))
                .Returns(new List<Daily> { closedDaily }.AsAsyncQueryable());

            // Act
            var result = await dailyService.ResetDailyReviews(dailyId);

            // Assert
            Assert.True(result.IsFailure);
            Assert.Equal("400", result.Error.Code);
            Assert.Equal("لا يمكن تعديل بيانات يومية مغلقة. قم بإعادة فتح اليومية أولًا.", result.Error.Message);
        }

        [Fact]
        public async Task FormService_AddForm_WhenDailyClosed_Fails()
        {
            // Arrange
            var formRepoMock = new Mock<IFormRepository>();
            var formDetailsRepoMock = new Mock<IFormDetailsRepository>();
            var dailyRepoMock = new Mock<IDailyRepository>();
            var empRepoMock = new Mock<IEmployeeRepository>();
            var uowMock = new Mock<IUnitOfWork>();
            var httpContextAccessorMock = new Mock<IHttpContextAccessor>();
            var userManagerMock = CreateMockUserManager();
            var memoryCache = new MemoryCache(new MemoryCacheOptions());
            var keyFactoryMock = new Mock<IDbCacheKeyFactory>();
            var currentUserServiceMock = new Mock<ICurrentUserService>();

            int dailyId = 15;
            dailyRepoMock.Setup(r => r.GetById(dailyId))
                .ReturnsAsync(new Daily { Id = dailyId, Closed = true });

            var guard = new DailyClosureGuard(
                dailyRepoMock.Object,
                formRepoMock.Object,
                formDetailsRepoMock.Object,
                new Mock<IDailyReferencesRepository>().Object,
                new Mock<IFormReferencesRepository>().Object);

            var formService = new FormService(
                formRepoMock.Object,
                formDetailsRepoMock.Object,
                dailyRepoMock.Object,
                empRepoMock.Object,
                uowMock.Object,
                httpContextAccessorMock.Object,
                userManagerMock.Object,
                memoryCache,
                keyFactoryMock.Object,
                currentUserServiceMock.Object,
                guard);

            // Act
            var result = await formService.AddForm(new FormDto
            {
                Name = "استمارة جديدة",
                DailyId = dailyId
            });

            // Assert
            Assert.True(result.IsFailure);
            Assert.Equal("400", result.Error.Code);
            Assert.Equal("لا يمكن تعديل بيانات يومية مغلقة. قم بإعادة فتح اليومية أولًا.", result.Error.Message);
        }

        [Fact]
        public async Task FormDetailsService_AddEmployeeToFormDetails_WhenDailyClosed_Fails()
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

            int formId = 100;
            int dailyId = 25;
            formRepoMock.Setup(r => r.GetById(formId))
                .ReturnsAsync(new Form { Id = formId, DailyId = dailyId });
            dailyRepoMock.Setup(r => r.GetById(dailyId))
                .ReturnsAsync(new Daily { Id = dailyId, Closed = true });

            var guard = new DailyClosureGuard(
                dailyRepoMock.Object,
                formRepoMock.Object,
                formDetailsRepoMock.Object,
                new Mock<IDailyReferencesRepository>().Object,
                formRefRepoMock.Object);

            var formDetailsService = new FormDetailsService(
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
            var result = await formDetailsService.AddEmployeeToFormDetails(new FormDetailsRequest
            {
                FormId = formId,
                EmployeeId = "12345678901234",
                Amount = 250
            });

            // Assert
            Assert.True(result.IsFailure);
            Assert.Equal("400", result.Error.Code);
            Assert.Equal("لا يمكن تعديل بيانات يومية مغلقة. قم بإعادة فتح اليومية أولًا.", result.Error.Message);
        }

        [Fact]
        public async Task FormDetailsService_MarkFormDetailsAsReviewed_WhenDailyClosed_Fails()
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

            int detailId = 300;
            int formId = 100;
            int dailyId = 25;
            formDetailsRepoMock.Setup(r => r.GetById(detailId))
                .ReturnsAsync(new FormDetails { Id = detailId, FormId = formId, CreatedBy = "user1" });
            formRepoMock.Setup(r => r.GetById(formId))
                .ReturnsAsync(new Form { Id = formId, DailyId = dailyId });
            dailyRepoMock.Setup(r => r.GetById(dailyId))
                .ReturnsAsync(new Daily { Id = dailyId, Closed = true });

            var guard = new DailyClosureGuard(
                dailyRepoMock.Object,
                formRepoMock.Object,
                formDetailsRepoMock.Object,
                new Mock<IDailyReferencesRepository>().Object,
                formRefRepoMock.Object);

            var claimsPrincipal = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "user1")
            }, "TestAuth"));
            var httpContext = new DefaultHttpContext { User = claimsPrincipal };
            httpContextAccessorMock.Setup(h => h.HttpContext).Returns(httpContext);
            currentUserServiceMock.Setup(u => u.UserId).Returns("user1");

            var formDetailsService = new FormDetailsService(
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
            var result = await formDetailsService.MarkFormDetailsAsReviewed(detailId, true);

            // Assert
            Assert.True(result.IsFailure);
            Assert.Equal("400", result.Error.Code);
            Assert.Equal("لا يمكن تعديل بيانات يومية مغلقة. قم بإعادة فتح اليومية أولًا.", result.Error.Message);
        }

        private static Mock<UserManager<ApplicationUser>> CreateMockUserManager()
        {
            var store = new Mock<IUserStore<ApplicationUser>>();
            return new Mock<UserManager<ApplicationUser>>(store.Object, null!, null!, null!, null!, null!, null!, null!, null!);
        }
    }
}
