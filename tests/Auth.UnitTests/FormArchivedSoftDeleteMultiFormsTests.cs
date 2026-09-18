using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Application.Dtos.Requests;
using Application.Features;
using Application.Helpers;
using Application.Interfaces;
using Application.Services;
using Core.Interfaces;
using Core.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Moq;
using Xunit;

namespace Auth.UnitTests
{
    public class FormArchivedSoftDeleteMultiFormsTests
    {
        private readonly Mock<IFormRepository> _mockFormRepo = new();
        private readonly Mock<IUnitOfWork> _mockUow = new();
        private readonly Mock<IHttpContextAccessor> _mockHttpContextAccessor = new();
        private readonly Mock<UserManager<ApplicationUser>> _mockUserManager;
        private readonly Mock<ICurrentUserService> _mockCurrentUserService = new();
        private readonly Mock<IDailyClosureGuard> _mockClosureGuard = new();

        public FormArchivedSoftDeleteMultiFormsTests()
        {
            var userStoreMock = new Mock<IUserStore<ApplicationUser>>();
            _mockUserManager = new Mock<UserManager<ApplicationUser>>(
                userStoreMock.Object, null!, null!, null!, null!, null!, null!, null!, null!);
        }

        private FormArchivedService CreateService(IDailyClosureGuard? guard = null)
        {
            return new FormArchivedService(
                _mockFormRepo.Object,
                _mockUow.Object,
                _mockHttpContextAccessor.Object,
                _mockUserManager.Object,
                _mockCurrentUserService.Object,
                guard ?? _mockClosureGuard.Object);
        }

        [Fact]
        public async Task SoftDeleteMultiForms_AllValidAndAllowed_DeactivatesAllFormsAndCommitsOnce()
        {
            // Arrange
            var service = CreateService();
            var form1 = new Form { Id = 10, DailyId = 1, IsActive = true };
            var form2 = new Form { Id = 20, DailyId = 1, IsActive = true };

            _mockFormRepo.Setup(r => r.GetById(10)).ReturnsAsync(form1);
            _mockFormRepo.Setup(r => r.GetById(20)).ReturnsAsync(form2);

            _mockClosureGuard.Setup(g => g.ValidateFormDailyOpenAsync(form1))
                .ReturnsAsync(Result.Success());
            _mockClosureGuard.Setup(g => g.ValidateFormDailyOpenAsync(form2))
                .ReturnsAsync(Result.Success());

            _mockUow.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(2);

            // Act
            var result = await service.SoftDeleteMultiForms(new[] { 10, 20 });

            // Assert
            Assert.True(result.IsSuccess);
            _mockFormRepo.Verify(r => r.DeActive(10), Times.Once);
            _mockFormRepo.Verify(r => r.DeActive(20), Times.Once);
            _mockUow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task SoftDeleteMultiForms_NonExistentIdInGroup_FailsAndMutatesNothing()
        {
            // Arrange
            var service = CreateService();
            var form1 = new Form { Id = 10, DailyId = 1, IsActive = true };

            _mockFormRepo.Setup(r => r.GetById(10)).ReturnsAsync(form1);
            _mockFormRepo.Setup(r => r.GetById(99)).ReturnsAsync((Form?)null);

            _mockClosureGuard.Setup(g => g.ValidateFormDailyOpenAsync(form1))
                .ReturnsAsync(Result.Success());

            // Act
            var result = await service.SoftDeleteMultiForms(new[] { 10, 99 });

            // Assert
            Assert.True(result.IsFailure);
            Assert.Equal("404", result.Error.Code);
            Assert.Contains("99", result.Error.Message);

            // Verify ALL-OR-NOTHING: Not even form 10 was deactivated or saved!
            _mockFormRepo.Verify(r => r.DeActive(It.IsAny<int>()), Times.Never);
            _mockUow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task SoftDeleteMultiForms_ClosedDailyInGroup_FailsAndMutatesNothing()
        {
            // Arrange
            var service = CreateService();
            var form1 = new Form { Id = 10, DailyId = 1, IsActive = true };
            var form2 = new Form { Id = 20, DailyId = 2, IsActive = true };

            _mockFormRepo.Setup(r => r.GetById(10)).ReturnsAsync(form1);
            _mockFormRepo.Setup(r => r.GetById(20)).ReturnsAsync(form2);

            _mockClosureGuard.Setup(g => g.ValidateFormDailyOpenAsync(form1))
                .ReturnsAsync(Result.Success());
            _mockClosureGuard.Setup(g => g.ValidateFormDailyOpenAsync(form2))
                .ReturnsAsync(Result.Failure(new Error("400", "لا يمكن تعديل أو حذف بيانات مرتبطة بيومية مغلقة.")));

            // Act
            var result = await service.SoftDeleteMultiForms(new[] { 10, 20 });

            // Assert
            Assert.True(result.IsFailure);
            Assert.Equal("400", result.Error.Code);

            // Verify ALL-OR-NOTHING: No form is modified when one belongs to a closed daily
            _mockFormRepo.Verify(r => r.DeActive(It.IsAny<int>()), Times.Never);
            _mockUow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task SoftDeleteMultiForms_MixedOpenAndClosedDailyForms_NeitherFormIsDeleted()
        {
            // Specifically testing:
            // "لو المجموعة تحتوي:
            //  * Form في Daily مفتوحة
            //  * Form في Daily مغلقة
            //  يجب ألا يتم حذف أي منهما."

            // Arrange with real DailyClosureGuard
            var mockDailyRepo = new Mock<IDailyRepository>();
            var mockFormDetailsRepo = new Mock<IFormDetailsRepository>();
            var mockDailyRefRepo = new Mock<IDailyReferencesRepository>();
            var mockFormRefRepo = new Mock<IFormReferencesRepository>();

            var realGuard = new DailyClosureGuard(
                mockDailyRepo.Object,
                _mockFormRepo.Object,
                mockFormDetailsRepo.Object,
                mockDailyRefRepo.Object,
                mockFormRefRepo.Object);

            var service = CreateService(realGuard);

            var openDaily = new Daily { Id = 100, Closed = false };
            var closedDaily = new Daily { Id = 200, Closed = true };

            var openForm = new Form { Id = 1, DailyId = openDaily.Id, IsActive = true };
            var closedForm = new Form { Id = 2, DailyId = closedDaily.Id, IsActive = true };

            _mockFormRepo.Setup(r => r.GetById(1)).ReturnsAsync(openForm);
            _mockFormRepo.Setup(r => r.GetById(2)).ReturnsAsync(closedForm);

            mockDailyRepo.Setup(r => r.GetById(openDaily.Id)).ReturnsAsync(openDaily);
            mockDailyRepo.Setup(r => r.GetById(closedDaily.Id)).ReturnsAsync(closedDaily);

            // Act
            var result = await service.SoftDeleteMultiForms(new[] { 1, 2 });

            // Assert
            Assert.True(result.IsFailure);
            Assert.Equal("400", result.Error.Code);
            Assert.Equal(DailyClosureGuard.DailyClosedMessage, result.Error.Message);

            // Crucial assertion: NEITHER form 1 nor form 2 was deactivated!
            _mockFormRepo.Verify(r => r.DeActive(1), Times.Never);
            _mockFormRepo.Verify(r => r.DeActive(2), Times.Never);
            _mockFormRepo.Verify(r => r.DeActive(It.IsAny<int>()), Times.Never);

            // SaveChangesAsync was NEVER called!
            _mockUow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task SoftDeleteMultiForms_EmptyOrNullIds_ReturnsFailureWithoutMutating()
        {
            // Arrange
            var service = CreateService();

            // Act
            var resultNull = await service.SoftDeleteMultiForms(null!);
            var resultEmpty = await service.SoftDeleteMultiForms(Array.Empty<int>());

            // Assert
            Assert.True(resultNull.IsFailure);
            Assert.True(resultEmpty.IsFailure);
            Assert.Equal("400", resultNull.Error.Code);
            Assert.Equal("400", resultEmpty.Error.Code);

            _mockFormRepo.Verify(r => r.DeActive(It.IsAny<int>()), Times.Never);
            _mockUow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task SoftDeleteMultiForms_WhenSaveChangesReturnsZero_ReturnsInternalServerError()
        {
            // Arrange
            var service = CreateService();
            var form1 = new Form { Id = 10, DailyId = 1, IsActive = true };

            _mockFormRepo.Setup(r => r.GetById(10)).ReturnsAsync(form1);
            _mockClosureGuard.Setup(g => g.ValidateFormDailyOpenAsync(form1))
                .ReturnsAsync(Result.Success());

            _mockUow.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(0);

            // Act
            var result = await service.SoftDeleteMultiForms(new[] { 10 });

            // Assert
            Assert.True(result.IsFailure);
            Assert.Equal("500", result.Error.Code);
            _mockFormRepo.Verify(r => r.DeActive(10), Times.Once);
            _mockUow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        }
    }
}
