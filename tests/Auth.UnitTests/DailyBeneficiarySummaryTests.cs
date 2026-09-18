using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Application.Features;
using Application.Helpers;
using Application.Interfaces;
using Application.Services;
using Core.Interfaces;
using Core.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;

namespace Auth.UnitTests
{
    public class DailyBeneficiarySummaryTests
    {
        [Fact]
        public async Task GetBeneficiariesSummary_Excludes_Inactive_FormDetails_And_Inactive_Forms_And_Resolves_Department()
        {
            // Arrange
            int dailyId = 42;
            var deptEngineering = new Department { Id = 1, Name = "الهندسة" };
            var deptFinance = new Department { Id = 2, Name = "المالية" };

            var empActive = new Employee
            {
                Id = "11111111111111",
                Name = "أحمد محمد",
                Department = deptEngineering
            };

            var empInactiveOnly = new Employee
            {
                Id = "22222222222222",
                Name = "محمود علي",
                Department = deptFinance
            };

            var activeForm = new Form
            {
                Id = 10,
                DailyId = dailyId,
                Name = "استمارة نشطة",
                Index = 1,
                IsActive = true,
                FormDetails = new List<FormDetails>
                {
                    // Active detail -> SHOULD be included (100)
                    new FormDetails
                    {
                        Id = 101,
                        FormId = 10,
                        EmployeeId = empActive.Id,
                        Employee = empActive,
                        Amount = 100.0,
                        IsActive = true,
                        IsReviewed = true,
                        IsSummaryReviewed = true,
                        IsSummaryReviewedBy = "reviewer-1"
                    },
                    // Inactive detail -> MUST be excluded (500)
                    new FormDetails
                    {
                        Id = 102,
                        FormId = 10,
                        EmployeeId = empActive.Id,
                        Employee = empActive,
                        Amount = 500.0,
                        IsActive = false,
                        IsReviewed = false,
                        IsSummaryReviewed = false,
                        IsSummaryReviewedBy = "reviewer-inactive"
                    },
                    // Detail for empInactiveOnly that is inactive -> MUST be excluded
                    new FormDetails
                    {
                        Id = 103,
                        FormId = 10,
                        EmployeeId = empInactiveOnly.Id,
                        Employee = empInactiveOnly,
                        Amount = 300.0,
                        IsActive = false
                    }
                }
            };

            var inactiveForm = new Form
            {
                Id = 20,
                DailyId = dailyId,
                Name = "استمارة غير نشطة",
                Index = 2,
                IsActive = false,
                FormDetails = new List<FormDetails>
                {
                    // Detail in inactive form -> MUST be excluded even if detail.IsActive == true
                    new FormDetails
                    {
                        Id = 201,
                        FormId = 20,
                        EmployeeId = empActive.Id,
                        Employee = empActive,
                        Amount = 1000.0,
                        IsActive = true
                    }
                }
            };

            var daily = new Daily
            {
                Id = dailyId,
                Name = "يومية الاختبار",
                DailyDate = DateTime.Today,
                Closed = false,
                Forms = new List<Form> { activeForm, inactiveForm }
            };

            var dailyRepoMock = new Mock<IDailyRepository>();
            dailyRepoMock.Setup(r => r.GetQueryable(It.IsAny<bool?>()))
                .Returns(new List<Daily> { daily }.AsAsyncQueryable());
            dailyRepoMock.Setup(r => r.GetById(dailyId, It.IsAny<bool>()))
                .ReturnsAsync(daily);

            var netPayRepoMock = new Mock<IEmployeeNetPayRepository>();
            netPayRepoMock.Setup(r => r.GetQueryable(It.IsAny<bool?>()))
                .Returns(new List<EmployeeNetPay>().AsAsyncQueryable());

            var watchListRepoMock = new Mock<IEmployeeWatchListRepository>();
            watchListRepoMock.Setup(r => r.GetQueryable(It.IsAny<bool?>()))
                .Returns(new List<EmployeeWatchList>().AsAsyncQueryable());
            var empRepoMock = new Mock<IEmployeeRepository>();
            var uowMock = new Mock<IUnitOfWork>();
            var currentUserServiceMock = new Mock<ICurrentUserService>();
            var watchListService = new WatchListService(
                watchListRepoMock.Object,
                empRepoMock.Object,
                uowMock.Object,
                currentUserServiceMock.Object);

            var userManagerMock = CreateMockUserManager();
            userManagerMock.Setup(m => m.Users)
                .Returns(new List<ApplicationUser>
                {
                    new ApplicationUser { Id = "reviewer-1", DisplayName = "مراجع أول", UserName = "rev1" }
                }.AsAsyncQueryable());

            var formRepoMock = new Mock<IFormRepository>();
            var formDetailsRepoMock = new Mock<IFormDetailsRepository>();
            var dailyRefRepoMock = new Mock<IDailyReferencesRepository>();
            var formRefRepoMock = new Mock<IFormReferencesRepository>();
            var guard = new DailyClosureGuard(
                dailyRepoMock.Object,
                formRepoMock.Object,
                formDetailsRepoMock.Object,
                dailyRefRepoMock.Object,
                formRefRepoMock.Object);

            var configMock = new Mock<IConfiguration>();

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

            // Act
            var result = await dailyService.GetBeneficiariesSummary(dailyId);

            // Assert
            Assert.True(result.IsSuccess);
            var summary = result.Value;
            Assert.NotNull(summary);

            // 1. Total amount must ONLY be 100.0 (from the active detail in the active form)
            Assert.Equal(100.0, summary.TotalAmount);

            // 2. Only 1 beneficiary (empActive), empInactiveOnly must NOT appear
            Assert.Equal(1, summary.TotalBeneficiaries);
            var beneficiary = summary.Beneficiaries.Single();
            Assert.Equal(empActive.Id, beneficiary.EmployeeId);
            Assert.Equal(100.0, beneficiary.TotalAmount);

            // 3. Department must be correctly loaded from Employee.Department
            Assert.Equal("الهندسة", beneficiary.Department);

            // 4. Details list in beneficiary must only have 1 active detail
            Assert.Single(beneficiary.Details);
            Assert.Equal(101, beneficiary.Details[0].FormDetailId);
            Assert.Equal("مراجع أول", beneficiary.Details[0].IsSummaryReviewedBy);
        }

        private static Mock<UserManager<ApplicationUser>> CreateMockUserManager()
        {
            var store = new Mock<IUserStore<ApplicationUser>>();
            return new Mock<UserManager<ApplicationUser>>(store.Object, null!, null!, null!, null!, null!, null!, null!, null!);
        }
    }
}
