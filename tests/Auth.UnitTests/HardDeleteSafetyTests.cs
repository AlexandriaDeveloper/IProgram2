using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Api.Controllers;
using Application.Dtos;
using Application.Dtos.Requests;
using Application.Features;
using Application.Helpers;
using Application.Interfaces;
using Application.Services;
using Auth.Infrastructure;
using Core.Interfaces;
using Core.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Persistence.Helpers;
using Persistence.Repository;
using Xunit;

namespace Auth.UnitTests
{
    public class HardDeleteSafetyTests
    {
        private readonly Mock<IDailyRepository> _mockDailyRepo = new();
        private readonly Mock<IFormRepository> _mockFormRepo = new();
        private readonly Mock<IFormDetailsRepository> _mockFormDetailsRepo = new();
        private readonly Mock<IDepartmentRepository> _mockDeptRepo = new();
        private readonly Mock<IDailyReferencesRepository> _mockDailyRefRepo = new();
        private readonly Mock<IFormReferencesRepository> _mockFormRefRepo = new();
        private readonly Mock<IEmployeeNetPayRepository> _mockNetPayRepo = new();
        private readonly Mock<IEmployeeRepository> _mockEmpRepo = new();
        private readonly Mock<IEmployeeWatchListRepository> _mockWatchListRepo = new();
        private readonly Mock<IUnitOfWork> _mockUow = new();
        private readonly Mock<IHttpContextAccessor> _mockHttpAccessor = new();
        private readonly Mock<UserManager<ApplicationUser>> _mockUserManager;
        private readonly Mock<ICurrentUserService> _mockCurrentUserService = new();
        private readonly Mock<IConfiguration> _mockConfig = new();
        private readonly Mock<IMemoryCache> _mockCache = new();
        private readonly Mock<IDbCacheKeyFactory> _mockCacheKeyFactory = new();
        private readonly Mock<ILogger<FormDetailsService>> _mockFormDetailsLogger = new();
        private readonly Mock<ILogger<FormService>> _mockFormLogger = new();
        private readonly Mock<IWebHostEnvironment> _mockHostEnvironment = new();
        private readonly Mock<IFileStorageService> _mockFileStorage = new();
        private readonly Mock<IEmployeeRefernceRepository> _mockEmpRefRepo = new();
        private readonly Mock<ILogger<DailyReferenceService>> _mockDailyRefLogger = new();
        private readonly Mock<ILogger<FormReferenceService>> _mockFormRefLogger = new();
        private readonly Mock<ILogger<EmployeeController>> _mockEmpControllerLogger = new();
        private readonly DailyClosureGuard _closureGuard;

        public HardDeleteSafetyTests()
        {
            var userStoreMock = new Mock<IUserStore<ApplicationUser>>();
            _mockUserManager = new Mock<UserManager<ApplicationUser>>(
                userStoreMock.Object, null!, null!, null!, null!, null!, null!, null!, null!);

            _closureGuard = new DailyClosureGuard(
                _mockDailyRepo.Object,
                _mockFormRepo.Object,
                _mockFormDetailsRepo.Object,
                _mockDailyRefRepo.Object,
                _mockFormRefRepo.Object);

            _mockCacheKeyFactory.Setup(f => f.CreateKey(It.IsAny<string>()))
                .Returns<string>(k => $"test_{k}");
            _mockCacheKeyFactory.Setup(f => f.GetFormDetailsKey(It.IsAny<int>()))
                .Returns<int>(id => $"test_formdetails_{id}");

            _mockHttpAccessor.Setup(h => h.HttpContext).Returns(new DefaultHttpContext());
            _mockUserManager.Setup(m => m.Users).Returns(new List<ApplicationUser>().AsAsyncQueryable());
            _mockHostEnvironment.Setup(h => h.ContentRootPath).Returns(AppDomain.CurrentDomain.BaseDirectory);
            _mockCurrentUserService.Setup(u => u.UserId).Returns("test-user-id");
        }

        private DailyService CreateDailyService(IDailyRepository? dailyRepo = null)
        {
            var watchListService = new WatchListService(
                _mockWatchListRepo.Object,
                _mockEmpRepo.Object,
                _mockUow.Object,
                _mockCurrentUserService.Object);

            return new DailyService(
                dailyRepo ?? _mockDailyRepo.Object,
                _mockFormRepo.Object,
                null!,
                _mockUow.Object,
                _mockUserManager.Object,
                _mockConfig.Object,
                _mockNetPayRepo.Object,
                watchListService,
                _closureGuard);
        }

        private EmployeeService CreateEmployeeService(ApplicationContext? context = null)
        {
            var dbContext = context ?? new ApplicationContext(new DbContextOptionsBuilder<ApplicationContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

            return new EmployeeService(
                _mockEmpRepo.Object,
                _mockFormDetailsRepo.Object,
                _mockDeptRepo.Object,
                _mockUow.Object,
                _mockConfig.Object,
                _mockHttpAccessor.Object,
                _mockCache.Object,
                _mockCurrentUserService.Object,
                dbContext);
        }

        private DailyReferenceService CreateDailyReferenceService()
        {
            return new DailyReferenceService(
                _mockDailyRefRepo.Object,
                _mockUow.Object,
                _mockHostEnvironment.Object,
                _mockFileStorage.Object,
                _mockDailyRefLogger.Object,
                _closureGuard);
        }

        private FormReferenceService CreateFormReferenceService()
        {
            return new FormReferenceService(
                _mockFormRefRepo.Object,
                _mockUow.Object,
                _mockHttpAccessor.Object,
                _mockConfig.Object,
                _mockHostEnvironment.Object,
                _mockCurrentUserService.Object,
                _mockFileStorage.Object,
                _mockFormRefLogger.Object,
                _closureGuard);
        }

        private EmployeeRefernceService CreateEmployeeRefernceService()
        {
            return new EmployeeRefernceService(
                _mockHttpAccessor.Object,
                _mockEmpRefRepo.Object,
                _mockUow.Object,
                _mockConfig.Object,
                _mockHostEnvironment.Object,
                _mockCurrentUserService.Object);
        }

        private FormService CreateFormService()
        {
            return new FormService(
                _mockFormRepo.Object,
                _mockFormDetailsRepo.Object,
                _mockDailyRepo.Object,
                _mockEmpRepo.Object,
                _mockUow.Object,
                _mockHttpAccessor.Object,
                _mockUserManager.Object,
                _mockCache.Object,
                _mockCacheKeyFactory.Object,
                _mockCurrentUserService.Object,
                _closureGuard);
        }

        private FormDetailsService CreateFormDetailsService()
        {
            var watchListService = new WatchListService(
                _mockWatchListRepo.Object,
                _mockEmpRepo.Object,
                _mockUow.Object,
                _mockCurrentUserService.Object);

            return new FormDetailsService(
                _mockFormRepo.Object,
                _mockFormRefRepo.Object,
                _mockFormDetailsRepo.Object,
                _mockDailyRepo.Object,
                _mockUow.Object,
                _mockHttpAccessor.Object,
                _mockCache.Object,
                _mockCacheKeyFactory.Object,
                _mockUserManager.Object,
                _mockFormDetailsLogger.Object,
                _mockCurrentUserService.Object,
                watchListService,
                _closureGuard);
        }

        private DepartmentService CreateDepartmentService()
        {
            return new DepartmentService(
                _mockDeptRepo.Object,
                _mockEmpRepo.Object,
                _mockUow.Object,
                _mockCache.Object,
                _mockCacheKeyFactory.Object);
        }

        // ==========================================
        // 1. Daily Deletion Protection Tests
        // ==========================================

        [Fact]
        public async Task DailyController_Delete_RoutesToSoftDelete_AndDoesNotPhysicallyDelete()
        {
            // Arrange
            var dailyService = CreateDailyService();
            var controller = new DailyController(dailyService, null!);
            int dailyId = 10;
            var daily = new Daily { Id = dailyId, Closed = false, IsActive = true };

            _mockDailyRepo.Setup(r => r.GetById(dailyId)).ReturnsAsync(daily);
            _mockDailyRepo.Setup(r => r.DeActive(dailyId)).Returns(Task.CompletedTask);
            _mockUow.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

            // Act: Normal standard DELETE route /api/daily/{id}
            var response = await controller.Delete(dailyId, CancellationToken.None);

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(response);
            // Verify Soft Delete (DeActive) was called
            _mockDailyRepo.Verify(r => r.DeActive(dailyId), Times.Once);
            // Verify Hard Delete (Delete) was NEVER called!
            _mockDailyRepo.Verify(r => r.Delete(It.IsAny<int>()), Times.Never);
            _mockUow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task DailyController_Delete_WhenDailyIsClosed_RejectsDeletionWithoutMutating()
        {
            // Arrange
            var dailyService = CreateDailyService();
            var controller = new DailyController(dailyService, null!);
            int dailyId = 11;
            var closedDaily = new Daily { Id = dailyId, Closed = true, IsActive = true };

            _mockDailyRepo.Setup(r => r.GetById(dailyId)).ReturnsAsync(closedDaily);

            // Act
            var response = await controller.Delete(dailyId, CancellationToken.None);

            // Assert
            var badRequest = Assert.IsType<BadRequestObjectResult>(response);
            _mockDailyRepo.Verify(r => r.DeActive(It.IsAny<int>()), Times.Never);
            _mockDailyRepo.Verify(r => r.Delete(It.IsAny<int>()), Times.Never);
            _mockUow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        }

        // ==========================================
        // 2. Form Deletion Protection Tests
        // ==========================================

        [Fact]
        public async Task FormController_Delete_RoutesToSoftDelete_AndDoesNotPhysicallyDelete()
        {
            // Arrange
            var formService = CreateFormService();
            var controller = new FormController(formService, null!);
            int formId = 25;
            int dailyId = 5;
            var daily = new Daily { Id = dailyId, Closed = false };
            var form = new Form { Id = formId, DailyId = dailyId, IsActive = true };

            _mockFormRepo.Setup(r => r.GetById(formId)).ReturnsAsync(form);
            _mockDailyRepo.Setup(r => r.GetById(dailyId)).ReturnsAsync(daily);
            _mockFormRepo.Setup(r => r.DeActive(formId)).Returns(Task.CompletedTask);
            _mockUow.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

            // Act: Normal standard DELETE route /api/form/{id}
            var response = await controller.Delete(formId);

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(response);
            // Verify Soft Delete (DeActive) was called
            _mockFormRepo.Verify(r => r.DeActive(formId), Times.Once);
            // Verify Hard Delete (Delete) was NEVER called!
            _mockFormRepo.Verify(r => r.Delete(It.IsAny<int>()), Times.Never);
            _mockUow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task FormController_Delete_WhenFormInClosedDaily_RejectsDeletionWithoutMutating()
        {
            // Arrange
            var formService = CreateFormService();
            var controller = new FormController(formService, null!);
            int formId = 26;
            int dailyId = 6;
            var closedDaily = new Daily { Id = dailyId, Closed = true };
            var form = new Form { Id = formId, DailyId = dailyId, IsActive = true };

            _mockFormRepo.Setup(r => r.GetById(formId)).ReturnsAsync(form);
            _mockDailyRepo.Setup(r => r.GetById(dailyId)).ReturnsAsync(closedDaily);

            // Act
            var response = await controller.Delete(formId);

            // Assert
            var badRequest = Assert.IsType<BadRequestObjectResult>(response);
            _mockFormRepo.Verify(r => r.DeActive(It.IsAny<int>()), Times.Never);
            _mockFormRepo.Verify(r => r.Delete(It.IsAny<int>()), Times.Never);
            _mockUow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        }

        // ==========================================
        // 3. FormDetails & Department Deletion Protection Tests
        // ==========================================

        [Fact]
        public async Task FormDetailsService_DeleteEmployeeFromFormDetails_CallsDeActive_DoesNotPhysicallyDelete()
        {
            // Arrange
            var formDetailsService = CreateFormDetailsService();
            int formDetailsId = 50;
            int formId = 20;
            int dailyId = 10;
            var formDetails = new FormDetails { Id = formDetailsId, FormId = formId, IsActive = true };
            var form = new Form { Id = formId, DailyId = dailyId, IsActive = true };
            var daily = new Daily { Id = dailyId, Closed = false };

            _mockFormDetailsRepo.Setup(r => r.GetById(formDetailsId)).ReturnsAsync(formDetails);
            _mockFormRepo.Setup(r => r.GetById(formId)).ReturnsAsync(form);
            _mockDailyRepo.Setup(r => r.GetById(dailyId)).ReturnsAsync(daily);
            _mockFormDetailsRepo.Setup(r => r.DeActive(formDetailsId)).Returns(Task.CompletedTask);
            _mockUow.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

            // Act
            var result = await formDetailsService.DeleteEmployeeFromFormDetails(formDetailsId);

            // Assert
            Assert.True(result.IsSuccess);
            _mockFormDetailsRepo.Verify(r => r.DeActive(formDetailsId), Times.Once);
            _mockFormDetailsRepo.Verify(r => r.Delete(It.IsAny<int>()), Times.Never);
            _mockUow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task DepartmentService_DeleteDepartment_CallsDeActive_DoesNotPhysicallyDelete()
        {
            // Arrange
            var departmentService = CreateDepartmentService();
            int departmentId = 3;
            var department = new Department { Id = departmentId, Name = "قسم الحسابات", IsActive = true };

            _mockDeptRepo.Setup(r => r.GetById(departmentId)).ReturnsAsync(department);
            _mockDeptRepo.Setup(r => r.DeActive(departmentId)).Returns(Task.CompletedTask);
            _mockUow.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

            // Act
            var result = await departmentService.DeleteDepartment(departmentId);

            // Assert
            Assert.True(result.IsSuccess);
            _mockDeptRepo.Verify(r => r.DeActive(departmentId), Times.Once);
            _mockDeptRepo.Verify(r => r.Delete(It.IsAny<int>()), Times.Never);
            _mockUow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        }

        // ==========================================
        // 4. Active List Queries Protection Test
        // ==========================================

        [Fact]
        public async Task FormService_GetForms_CallsListAllAsync_WithInactiveFalse()
        {
            // Arrange
            var formService = CreateFormService();
            int dailyId = 1;
            var param = new FormParam { PageIndex = 1, PageSize = 10 };

            _mockFormRepo.Setup(r => r.ListAllAsync(It.IsAny<ISpecification<Form>>(), false, false))
                .ReturnsAsync(new List<Form>());
            _mockFormRepo.Setup(r => r.CountAsync(It.IsAny<ISpecification<Form>>()))
                .ReturnsAsync(0);
            _mockFormDetailsRepo.Setup(r => r.GetQueryable(It.IsAny<bool?>()))
                .Returns(new List<FormDetails>().AsAsyncQueryable());

            // Act
            var result = await formService.GetForms(dailyId, param);

            // Assert
            Assert.True(result.IsSuccess);
            // Verify withInactive was passed as FALSE so soft-deleted forms are NOT loaded
            _mockFormRepo.Verify(r => r.ListAllAsync(It.IsAny<ISpecification<Form>>(), false, false), Times.Once);
        }

        // ==========================================
        // 5. GenericRepository & EmployeeRepository Safety Tests
        // ==========================================

        [Fact]
        public async Task GenericRepository_Delete_WhenEntityNotFound_DoesNotThrowArgumentNullException()
        {
            // Arrange
            var options = new DbContextOptionsBuilder<ApplicationContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            using var context = new ApplicationContext(options);
            var repo = new GenericRepository<Daily>(context, _mockHttpAccessor.Object);

            // Act & Assert: Calling Delete on a non-existent ID (999) must not throw ArgumentNullException
            var exception = await Record.ExceptionAsync(() => repo.Delete(999));
            Assert.Null(exception);
        }

        [Fact]
        public async Task GenericRepository_DeActive_WhenEntityNotFound_DoesNotThrowNullReferenceException()
        {
            // Arrange
            var options = new DbContextOptionsBuilder<ApplicationContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            using var context = new ApplicationContext(options);
            var repo = new GenericRepository<Daily>(context, _mockHttpAccessor.Object);

            // Act & Assert: Calling DeActive on a non-existent ID (999) must not throw NullReferenceException
            var exception = await Record.ExceptionAsync(() => repo.DeActive(999));
            Assert.Null(exception);
        }

        [Fact]
        public async Task EmployeeRepository_Delete_WhenEntityNotFound_DoesNotThrowArgumentNullException()
        {
            // Arrange
            var options = new DbContextOptionsBuilder<ApplicationContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            using var context = new ApplicationContext(options);
            var repo = new EmployeeRepository(context, _mockHttpAccessor.Object);

            // Act & Assert: Calling Delete on a non-existent National ID must not throw ArgumentNullException
            var exception = await Record.ExceptionAsync(() => repo.Delete("00000000000000"));
            Assert.Null(exception);
        }

        [Fact]
        public async Task EmployeeRepository_DeActive_WhenEntityNotFound_DoesNotThrowNullReferenceException()
        {
            // Arrange
            var options = new DbContextOptionsBuilder<ApplicationContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            using var context = new ApplicationContext(options);
            var repo = new EmployeeRepository(context, _mockHttpAccessor.Object);

            // Act & Assert: Calling DeActive on a non-existent National ID must not throw NullReferenceException
            var exception = await Record.ExceptionAsync(() => repo.DeActive("00000000000000"));
            Assert.Null(exception);
        }

        // ==========================================
        // 6. Sprint 2B Extended Safety Tests (Employee, DailyReference, FormReference, EmployeeReference)
        // ==========================================

        [Fact]
        public async Task EmployeeController_Delete_RoutesToSoftDelete_AndCallsDeActiveOnly()
        {
            // Arrange
            var empId = "12345678901234";
            var employee = new Employee { Id = empId, Name = "Test Emp", IsActive = true };
            _mockEmpRepo.Setup(r => r.GetById(empId, false)).ReturnsAsync(employee);
            _mockEmpRepo.Setup(r => r.DeActive(empId)).Returns(Task.CompletedTask);
            _mockUow.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

            var employeeService = CreateEmployeeService();
            var controller = new EmployeeController(employeeService, _mockEmpControllerLogger.Object);

            // Act: Normal DELETE /api/employee/{id}
            var result = await controller.Delete(empId);

            // Assert
            Assert.IsType<OkObjectResult>(result);
            _mockEmpRepo.Verify(r => r.DeActive(empId), Times.Once);
            _mockEmpRepo.Verify(r => r.Delete(It.IsAny<string>()), Times.Never);
            _mockUow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task EmployeeService_SoftDelete_NeverCallsRepositoryHardDelete()
        {
            // Arrange
            var empId = "12345678901234";
            var employee = new Employee { Id = empId, Name = "Test Emp", IsActive = true };
            _mockEmpRepo.Setup(r => r.GetById(empId, false)).ReturnsAsync(employee);
            _mockEmpRepo.Setup(r => r.DeActive(empId)).Returns(Task.CompletedTask);
            _mockUow.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

            var employeeService = CreateEmployeeService();

            // Act
            var result = await employeeService.SoftDelete(empId);

            // Assert
            Assert.True(result.IsSuccess);
            _mockEmpRepo.Verify(r => r.Delete(It.IsAny<string>()), Times.Never);
            _mockEmpRepo.Verify(r => r.DeActive(empId), Times.Once);
        }

        [Fact]
        public async Task DailyReferenceService_DeleteReference_PerformsSoftDelete_AndNeverCallsHardDelete()
        {
            // Arrange
            var refId = 10;
            var dailyId = 1;
            var dailyRef = new DailyReference { Id = refId, DailyId = dailyId, IsActive = true, ReferencePath = "https://res.cloudinary.com/test/ref.pdf" };
            var daily = new Daily { Id = dailyId, Closed = false };

            _mockDailyRefRepo.Setup(r => r.GetById(refId)).ReturnsAsync(dailyRef);
            _mockDailyRepo.Setup(r => r.GetById(dailyId, It.IsAny<bool>())).ReturnsAsync(daily);
            _mockDailyRefRepo.Setup(r => r.DeActive(refId)).Returns(Task.CompletedTask);
            _mockUow.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

            var service = CreateDailyReferenceService();

            // Act
            var result = await service.DeleteReference(refId);

            // Assert
            Assert.True(result.IsSuccess);
            _mockDailyRefRepo.Verify(r => r.DeActive(refId), Times.Once);
            _mockDailyRefRepo.Verify(r => r.Delete(It.IsAny<int>()), Times.Never);
            _mockUow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task DailyReferenceService_DeleteReference_DoesNotDeletePhysicalFileFromStorage()
        {
            // Arrange
            var refId = 10;
            var dailyId = 1;
            var dailyRef = new DailyReference { Id = refId, DailyId = dailyId, IsActive = true, ReferencePath = "https://res.cloudinary.com/test/ref.pdf" };
            var daily = new Daily { Id = dailyId, Closed = false };

            _mockDailyRefRepo.Setup(r => r.GetById(refId)).ReturnsAsync(dailyRef);
            _mockDailyRepo.Setup(r => r.GetById(dailyId, It.IsAny<bool>())).ReturnsAsync(daily);
            _mockDailyRefRepo.Setup(r => r.DeActive(refId)).Returns(Task.CompletedTask);
            _mockUow.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

            var service = CreateDailyReferenceService();

            // Act
            var result = await service.DeleteReference(refId);

            // Assert: Storage service must NEVER be called to delete file
            Assert.True(result.IsSuccess);
            _mockFileStorage.Verify(f => f.DeleteFileAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task FormReferenceService_DeleteFormReference_DoesNotDeletePhysicalFileFromStorage()
        {
            // Arrange
            var refId = 20;
            var formId = 2;
            var dailyId = 1;
            var formRef = new FormRefernce { Id = refId, FormId = formId, IsActive = true, ReferencePath = "https://res.cloudinary.com/test/formref.pdf" };
            var form = new Form { Id = formId, DailyId = dailyId };
            var daily = new Daily { Id = dailyId, Closed = false };

            _mockFormRefRepo.Setup(r => r.GetById(refId)).ReturnsAsync(formRef);
            _mockFormRepo.Setup(r => r.GetById(formId, It.IsAny<bool>())).ReturnsAsync(form);
            _mockDailyRepo.Setup(r => r.GetById(dailyId, It.IsAny<bool>())).ReturnsAsync(daily);
            _mockUow.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

            var service = CreateFormReferenceService();

            // Act
            var result = await service.DeleteFormReference(refId);

            // Assert: Soft-deleted in DB, but physical file deletion must NOT be invoked
            Assert.True(result.IsSuccess);
            Assert.False(formRef.IsActive);
            _mockFormRefRepo.Verify(r => r.Update(It.Is<FormRefernce>(f => f.Id == refId && !f.IsActive)), Times.Once);
            _mockFileStorage.Verify(f => f.DeleteFileAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task EmployeeReferenceService_DeleteEmployeeReference_PreservesSoftDelete_AndDoesNotDeleteFile()
        {
            // Arrange
            var refId = 30;
            var empRef = new EmployeeRefernce { Id = refId, EmployeeId = "123", IsActive = true, ReferencePath = "emp_ref.pdf" };

            _mockEmpRefRepo.Setup(r => r.GetById(refId)).ReturnsAsync(empRef);
            _mockUow.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

            var service = CreateEmployeeRefernceService();

            // Act
            var result = await service.DeleteEmployeeReference(refId);

            // Assert
            Assert.True(result.IsSuccess);
            Assert.False(empRef.IsActive);
            _mockEmpRefRepo.Verify(r => r.Update(It.Is<EmployeeRefernce>(e => e.Id == refId && !e.IsActive)), Times.Once);
            _mockEmpRefRepo.Verify(r => r.Delete(It.IsAny<int>()), Times.Never);
        }

        [Fact]
        public async Task DailyService_GetDaily_ExcludesInactiveDailyReferences()
        {
            // Arrange
            var options = new DbContextOptionsBuilder<ApplicationContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            using var context = new ApplicationContext(options);
            var daily = new Daily { Id = 100, Name = "Test Daily References", DailyDate = DateTime.Today, Closed = false };
            context.Set<Daily>().Add(daily);
            context.Set<DailyReference>().AddRange(
                new DailyReference { Id = 1001, DailyId = 100, ReferencePath = "active.pdf", IsActive = true },
                new DailyReference { Id = 1002, DailyId = 100, ReferencePath = "inactive.pdf", IsActive = false }
            );
            await context.SaveChangesAsync();

            var dailyRepo = new DailyRepository(context, _mockHttpAccessor.Object);
            var dailyService = CreateDailyService(dailyRepo);

            // Act
            var result = await dailyService.GetDaily(100, CancellationToken.None);

            // Assert
            Assert.True(result.IsSuccess);
            Assert.NotNull(result.Value.DailyReferences);
            Assert.Single(result.Value.DailyReferences);
            Assert.Equal(1001, result.Value.DailyReferences[0].Id);
        }

        [Fact]
        public async Task FormReferenceService_GetFormReferences_ExcludesInactiveFormReferences()
        {
            // Arrange
            int formId = 55;
            var references = new List<FormRefernce>
            {
                new() { Id = 501, FormId = formId, ReferencePath = "active_form.pdf", IsActive = true },
                new() { Id = 502, FormId = formId, ReferencePath = "inactive_form.pdf", IsActive = false }
            };

            _mockFormRefRepo.Setup(r => r.GetQueryable())
                .Returns(references.AsAsyncQueryable());

            var service = CreateFormReferenceService();

            // Act
            var result = await service.GetFormReferences(formId);

            // Assert
            Assert.True(result.IsSuccess);
            Assert.Single(result.Value);
            Assert.Equal(501, result.Value[0].Id);
        }

        [Fact]
        public async Task EmployeeReferenceService_GetEmployeeRefernces_ExcludesInactiveEmployeeReferences()
        {
            // Arrange
            var empId = "98765432101234";
            var references = new List<EmployeeRefernce>
            {
                new() { Id = 601, EmployeeId = empId, ReferencePath = "active_emp.pdf", IsActive = true },
                new() { Id = 602, EmployeeId = empId, ReferencePath = "inactive_emp.pdf", IsActive = false }
            };

            _mockEmpRefRepo.Setup(r => r.GetQueryable())
                .Returns(references.AsAsyncQueryable());

            var service = CreateEmployeeRefernceService();

            // Act
            var result = await service.GetEmployeeRefernces(empId);

            // Assert
            Assert.True(result.IsSuccess);
            Assert.Single(result.Value);
            Assert.Equal(601, result.Value[0].Id);
        }
    }
}
