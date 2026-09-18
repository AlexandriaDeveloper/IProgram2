using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Application.Dtos;
using Application.Features;
using Application.Helpers;
using Application.Interfaces;
using Application.Services;
using Auth.Infrastructure;
using Core.Interfaces;
using Core.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Moq;
using NPOI.XSSF.UserModel;
using Persistence.Repository;
using Xunit;

namespace Auth.UnitTests
{
    public class FormExcelImportBatchTests
    {
        private ApplicationContext CreateContext(string dbName)
        {
            var options = new DbContextOptionsBuilder<ApplicationContext>()
                .UseInMemoryDatabase(databaseName: dbName)
                .Options;
            return new ApplicationContext(options);
        }

        private IFormFile CreateExcelFile(params (string natId, string tabCode, string tegaraCode, string dept, string name, string amount)[] rows)
        {
            using var workbook = new XSSFWorkbook();
            var sheet = workbook.CreateSheet("Sheet1");

            // Row 0: Title row
            var titleRow = sheet.CreateRow(0);
            titleRow.CreateCell(0).SetCellValue("بيانات الاستمارة");

            // Row 1: Header row (ReadSheeByIndex(0, 1) uses startRowIndex = 1 as headerIndex)
            var headerRow = sheet.CreateRow(1);
            headerRow.CreateCell(0).SetCellValue("م");
            headerRow.CreateCell(1).SetCellValue("الرقم القومى");
            headerRow.CreateCell(2).SetCellValue("كود طب");
            headerRow.CreateCell(3).SetCellValue("كود تجارة");
            headerRow.CreateCell(4).SetCellValue("القسم");
            headerRow.CreateCell(5).SetCellValue("الاسم");
            headerRow.CreateCell(6).SetCellValue("المبلغ");
            headerRow.CreateCell(7).SetCellValue("كود الموظف");

            // Data rows start at row 2 (headerIndex + 1)
            int r = 2;
            int counter = 1;
            foreach (var row in rows)
            {
                var dataRow = sheet.CreateRow(r++);
                dataRow.CreateCell(0).SetCellValue(counter++);
                dataRow.CreateCell(1).SetCellValue(row.natId ?? "");
                dataRow.CreateCell(2).SetCellValue(row.tabCode ?? "");
                dataRow.CreateCell(3).SetCellValue(row.tegaraCode ?? "");
                dataRow.CreateCell(4).SetCellValue(row.dept ?? "");
                dataRow.CreateCell(5).SetCellValue(row.name ?? "");
                dataRow.CreateCell(6).SetCellValue(row.amount ?? "0");
                dataRow.CreateCell(7).SetCellValue("");
            }

            using var ms = new MemoryStream();
            workbook.Write(ms);
            var bytes = ms.ToArray();
            return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "employees.xlsx");
        }

        private IFormFile CreateExcelFile(IEnumerable<(string natId, string tabCode, string tegaraCode, string dept, string name, string amount)> rows)
        {
            return CreateExcelFile(rows.ToArray());
        }

        private (FormService service,
                 ApplicationContext context,
                 Func<int> getQueryCount,
                 Mock<IDailyClosureGuard> guardMock,
                 Mock<IMemoryCache> cacheMock,
                 Mock<IUnitOfWork> uowMock)
            CreateFormServiceWithSpy(string dbName)
        {
            var context = CreateContext(dbName);

            var httpAccessorMock = new Mock<IHttpContextAccessor>();
            var claimsPrincipal = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "user-test-1")
            }));
            var httpContext = new DefaultHttpContext { User = claimsPrincipal };
            httpAccessorMock.Setup(a => a.HttpContext).Returns(httpContext);

            int count = 0;
            var realEmpRepo = new EmployeeRepository(context, httpAccessorMock.Object);
            var empRepoMock = new Mock<IEmployeeRepository>();
            empRepoMock.Setup(r => r.GetQueryable(It.IsAny<bool?>()))
                .Returns((bool? active) =>
                {
                    Interlocked.Increment(ref count);
                    return realEmpRepo.GetQueryable(active);
                });

            var formRepo = new FormRepository(context, httpAccessorMock.Object);
            var formDetailsRepo = new FormDetailsRepository(context, httpAccessorMock.Object);
            var dailyRepo = new DailyRepository(context, httpAccessorMock.Object);

            var realUow = new UnitOfWork(context);
            var uowMock = new Mock<IUnitOfWork>();
            uowMock.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()))
                .Returns<CancellationToken>(ct => realUow.SaveChangesAsync(ct));

            var userStoreMock = new Mock<IUserStore<ApplicationUser>>();
            var userManager = new UserManager<ApplicationUser>(userStoreMock.Object, null!, null!, null!, null!, null!, null!, null!, null!);

            var cacheMock = new Mock<IMemoryCache>();
            var cacheKeyFactoryMock = new Mock<IDbCacheKeyFactory>();
            cacheKeyFactoryMock.Setup(k => k.GetFormDetailsKey(It.IsAny<int>())).Returns((int id) => $"form_details_{id}");

            var currentUserServiceMock = new Mock<ICurrentUserService>();
            currentUserServiceMock.Setup(c => c.UserId).Returns("user-test-1");

            var dailyClosureGuardMock = new Mock<IDailyClosureGuard>();
            dailyClosureGuardMock.Setup(g => g.EnsureFormDailyOpenAsync(It.IsAny<int>())).ReturnsAsync(Result.Success());

            var service = new FormService(
                formRepo,
                formDetailsRepo,
                dailyRepo,
                empRepoMock.Object,
                uowMock.Object,
                httpAccessorMock.Object,
                userManager,
                cacheMock.Object,
                cacheKeyFactoryMock.Object,
                currentUserServiceMock.Object,
                dailyClosureGuardMock.Object);

            return (service, context, () => count, dailyClosureGuardMock, cacheMock, uowMock);
        }

        [Fact]
        public async Task UploadExcelEmployeesToForm_BatchesEmployeeQueries_WithoutPerRowNPlusOne()
        {
            var dbName = Guid.NewGuid().ToString();
            var (service, context, getQueryCount, guardMock, cacheMock, uowMock) = CreateFormServiceWithSpy(dbName);

            // Seed 5 employees in DB
            context.Employees.AddRange(
                new Employee { Id = "29001011111111", Name = "موظف قومي 1", TabCode = 101, TegaraCode = 201, IsActive = true },
                new Employee { Id = "29001011111112", Name = "موظف قومي 2", TabCode = 102, TegaraCode = 202, IsActive = true },
                new Employee { Id = "29001011111113", Name = "موظف طب 1", TabCode = 103, TegaraCode = 203, IsActive = true },
                new Employee { Id = "29001011111114", Name = "موظف طب 2", TabCode = 104, TegaraCode = 204, IsActive = true },
                new Employee { Id = "29001011111115", Name = "موظف تجارة 1", TabCode = 105, TegaraCode = 205, IsActive = true }
            );
            context.Set<Form>().Add(new Form { Id = 1, Name = "استمارة 1", IsActive = true });
            await context.SaveChangesAsync();

            // Create 5 rows: 2 by NatId, 2 by TabCode, 1 by TegaraCode
            var excelFile = CreateExcelFile(
                ("29001011111111", "", "", "قسم", "موظف قومي 1", "100.5"),
                ("29001011111112", "", "", "قسم", "موظف قومي 2", "200.75"),
                ("", "103", "", "قسم", "موظف طب 1", "300.25"),
                ("", "104", "", "قسم", "موظف طب 2", "400.0"),
                ("", "", "205", "قسم", "موظف تجارة 1", "500.5")
            );

            // Act
            var result = await service.UploadExcelEmployeesToForm(new UploadEmployeesToFormRequest
            {
                FormId = 1,
                File = excelFile,
                ValidateName = true
            });

            // Assert
            Assert.True(result.IsSuccess);
            // In old N+1 code, this was 5 calls. In batching, it is at most 3 calls (1 for NatIds, 1 for TabCodes, 1 for TegaraCodes).
            int sqlCalls = getQueryCount();
            Assert.InRange(sqlCalls, 1, 3);

            var savedDetails = await context.Set<FormDetails>().Where(f => f.FormId == 1).OrderBy(f => f.OrderNum).ToListAsync();
            Assert.Equal(5, savedDetails.Count);
            Assert.Equal("29001011111111", savedDetails[0].EmployeeId);
            Assert.Equal(1, savedDetails[0].OrderNum);
            Assert.Equal(100.5, savedDetails[0].Amount);

            Assert.Equal("29001011111115", savedDetails[4].EmployeeId);
            Assert.Equal(5, savedDetails[4].OrderNum);
            Assert.Equal(500.5, savedDetails[4].Amount);

            // Guard invoked twice in successful flow (start + before persistence)
            guardMock.Verify(g => g.EnsureFormDailyOpenAsync(1), Times.Exactly(2));
            uowMock.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once());
            cacheMock.Verify(c => c.Remove("form_details_1"), Times.Once());
        }

        [Fact]
        public async Task UploadExcelEmployeesToForm_DailyClosesDuringProcessing_SecondGuardFails_NoDbMutationAndNoCacheEviction()
        {
            var dbName = Guid.NewGuid().ToString();
            var (service, context, _, guardMock, cacheMock, uowMock) = CreateFormServiceWithSpy(dbName);

            // Seed form and old details
            var empOld = new Employee { Id = "29001019999991", Name = "موظف قديم", IsActive = true };
            var empNew = new Employee { Id = "29001019999992", Name = "موظف جديد", IsActive = true };
            context.Employees.AddRange(empOld, empNew);

            var form = new Form { Id = 10, Name = "استمارة 10", IsActive = true };
            context.Set<Form>().Add(form);

            context.Set<FormDetails>().AddRange(
                new FormDetails { FormId = 10, EmployeeId = empOld.Id, OrderNum = 1, Amount = 50.0, IsActive = true },
                new FormDetails { FormId = 10, EmployeeId = empOld.Id, OrderNum = 2, Amount = 75.0, IsActive = true }
            );
            await context.SaveChangesAsync();

            // Setup guard: 1st call -> Success; 2nd call -> Failure (Daily closed during processing)
            guardMock.Reset();
            guardMock.SetupSequence(g => g.EnsureFormDailyOpenAsync(10))
                .ReturnsAsync(Result.Success())
                .ReturnsAsync(Result.Failure(new Error("400", "اليومية مغلقة")));

            var excelFile = CreateExcelFile(
                ("29001019999992", "", "", "قسم", "موظف جديد", "100.0")
            );

            // Act
            var result = await service.UploadExcelEmployeesToForm(new UploadEmployeesToFormRequest
            {
                FormId = 10,
                File = excelFile,
                ValidateName = false
            });

            // Assert
            Assert.True(result.IsFailure);
            Assert.Equal("400", result.Error.Code);
            Assert.Equal("اليومية مغلقة", result.Error.Message);

            // Guard called exactly twice
            guardMock.Verify(g => g.EnsureFormDailyOpenAsync(10), Times.Exactly(2));

            // SaveChanges must NOT be called for replacement
            uowMock.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never());

            // Old details MUST remain untouched in DB
            var existingDetails = await context.Set<FormDetails>().Where(f => f.FormId == 10).ToListAsync();
            Assert.Equal(2, existingDetails.Count);
            Assert.All(existingDetails, d => Assert.Equal(empOld.Id, d.EmployeeId));

            // Cache MUST NOT be evicted on guard failure
            cacheMock.Verify(c => c.Remove(It.IsAny<object>()), Times.Never());
        }

        [Fact]
        public async Task UploadExcelEmployeesToForm_PreservesMatchingPrecedence_AndNoFallbackWhenNatIdPresent()
        {
            var dbName = Guid.NewGuid().ToString();
            var (service, context, _, guardMock, cacheMock, uowMock) = CreateFormServiceWithSpy(dbName);

            // Employee in DB with TabCode 202
            context.Employees.Add(new Employee { Id = "29001012222222", Name = "موظف ب", TabCode = 202, IsActive = true });
            context.Set<Form>().Add(new Form { Id = 2, Name = "استمارة 2", IsActive = true });
            await context.SaveChangesAsync();

            // Row has non-existent NatId "99999999999999", but valid TabCode 202
            var excelFile = CreateExcelFile(
                ("99999999999999", "202", "", "قسم", "موظف", "150.0")
            );

            // Act
            var result = await service.UploadExcelEmployeesToForm(new UploadEmployeesToFormRequest
            {
                FormId = 2,
                File = excelFile,
                ValidateName = false
            });

            // Assert
            Assert.True(result.IsFailure);
            Assert.Equal("1500", result.Error.Code);
            var messages = System.Text.Json.JsonSerializer.Deserialize<List<string>>(result.Error.Message);
            Assert.NotNull(messages);
            Assert.NotEmpty(messages);
            Assert.Contains("99999999999999", messages[0]);
            Assert.Contains("كود طب 202", messages[0]);

            // Fail-closed: No FormDetails inserted
            var detailsCount = await context.Set<FormDetails>().CountAsync(f => f.FormId == 2);
            Assert.Equal(0, detailsCount);

            // Second guard never reached due to validation failure
            guardMock.Verify(g => g.EnsureFormDailyOpenAsync(2), Times.Once());
            uowMock.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never());
            cacheMock.Verify(c => c.Remove(It.IsAny<object>()), Times.Never());
        }

        [Fact]
        public async Task UploadExcelEmployeesToForm_MatchesTabCode_WhenNationalIdEmpty()
        {
            var dbName = Guid.NewGuid().ToString();
            var (service, context, _, guardMock, cacheMock, uowMock) = CreateFormServiceWithSpy(dbName);

            context.Employees.Add(new Employee { Id = "29001013333333", Name = "موظف طب", TabCode = 555, TegaraCode = 999, IsActive = true });
            context.Set<Form>().Add(new Form { Id = 3, Name = "استمارة 3", IsActive = true });
            await context.SaveChangesAsync();

            // National ID is empty, TabCode is 555
            var excelFile = CreateExcelFile(
                ("", "555", "999", "قسم", "موظف طب", "250.0")
            );

            var result = await service.UploadExcelEmployeesToForm(new UploadEmployeesToFormRequest
            {
                FormId = 3,
                File = excelFile,
                ValidateName = false
            });

            Assert.True(result.IsSuccess);
            var detail = await context.Set<FormDetails>().FirstOrDefaultAsync(f => f.FormId == 3);
            Assert.NotNull(detail);
            Assert.Equal("29001013333333", detail.EmployeeId);
            Assert.Equal(250.0, detail.Amount);

            guardMock.Verify(g => g.EnsureFormDailyOpenAsync(3), Times.Exactly(2));
            cacheMock.Verify(c => c.Remove("form_details_3"), Times.Once());
        }

        [Fact]
        public async Task UploadExcelEmployeesToForm_MatchesTegaraCode_WhenNationalIdAndTabCodeEmpty()
        {
            var dbName = Guid.NewGuid().ToString();
            var (service, context, _, guardMock, cacheMock, uowMock) = CreateFormServiceWithSpy(dbName);

            context.Employees.Add(new Employee { Id = "29001014444444", Name = "موظف تجارة", TabCode = null, TegaraCode = 777, IsActive = true });
            context.Set<Form>().Add(new Form { Id = 4, Name = "استمارة 4", IsActive = true });
            await context.SaveChangesAsync();

            // National ID empty, TabCode empty, TegaraCode is 777
            var excelFile = CreateExcelFile(
                ("", "", "777", "قسم", "موظف تجارة", "350.0")
            );

            var result = await service.UploadExcelEmployeesToForm(new UploadEmployeesToFormRequest
            {
                FormId = 4,
                File = excelFile,
                ValidateName = false
            });

            Assert.True(result.IsSuccess);
            var detail = await context.Set<FormDetails>().FirstOrDefaultAsync(f => f.FormId == 4);
            Assert.NotNull(detail);
            Assert.Equal("29001014444444", detail.EmployeeId);
            Assert.Equal(350.0, detail.Amount);

            guardMock.Verify(g => g.EnsureFormDailyOpenAsync(4), Times.Exactly(2));
            cacheMock.Verify(c => c.Remove("form_details_4"), Times.Once());
        }

        [Fact]
        public async Task UploadExcelEmployeesToForm_ValidatesName_WhenValidateNameTrue_FailsOnMismatch()
        {
            var dbName = Guid.NewGuid().ToString();
            var (service, context, _, guardMock, cacheMock, uowMock) = CreateFormServiceWithSpy(dbName);

            context.Employees.Add(new Employee { Id = "29001015555555", Name = "أحمد محمد محمود", IsActive = true });
            context.Set<Form>().Add(new Form { Id = 5, Name = "استمارة 5", IsActive = true });
            await context.SaveChangesAsync();

            // Name in file is completely different
            var excelFile = CreateExcelFile(
                ("29001015555555", "", "", "قسم", "سعيد علي كمال", "100.0")
            );

            var result = await service.UploadExcelEmployeesToForm(new UploadEmployeesToFormRequest
            {
                FormId = 5,
                File = excelFile,
                ValidateName = true
            });

            Assert.True(result.IsFailure);
            Assert.Equal("1500", result.Error.Code);
            var messages = System.Text.Json.JsonSerializer.Deserialize<List<string>>(result.Error.Message);
            Assert.NotNull(messages);
            Assert.NotEmpty(messages);
            Assert.Contains("يوجد اختلاف في الاسم بالسطر رقم 1", messages[0]);
            Assert.Contains("مسجل لدينا (أحمد محمد محمود)", messages[0]);
            Assert.Contains("وفي الملف (سعيد علي كمال)", messages[0]);

            // Fail-closed: No details inserted
            var detailsCount = await context.Set<FormDetails>().CountAsync(f => f.FormId == 5);
            Assert.Equal(0, detailsCount);

            // Cache never evicted
            cacheMock.Verify(c => c.Remove(It.IsAny<object>()), Times.Never());
        }

        [Fact]
        public async Task UploadExcelEmployeesToForm_ValidatesName_WhenValidateNameTrue_SucceedsOnArabicMatch()
        {
            var dbName = Guid.NewGuid().ToString();
            var (service, context, _, guardMock, cacheMock, uowMock) = CreateFormServiceWithSpy(dbName);

            context.Employees.Add(new Employee { Id = "29001015555556", Name = "أحمد محمد محمود", IsActive = true });
            context.Set<Form>().Add(new Form { Id = 6, Name = "استمارة 6", IsActive = true });
            await context.SaveChangesAsync();

            // Name differs only in alef normalization (أ vs ا)
            var excelFile = CreateExcelFile(
                ("29001015555556", "", "", "قسم", "احمد محمد محمود", "120.0")
            );

            var result = await service.UploadExcelEmployeesToForm(new UploadEmployeesToFormRequest
            {
                FormId = 6,
                File = excelFile,
                ValidateName = true
            });

            Assert.True(result.IsSuccess);
            var detail = await context.Set<FormDetails>().FirstOrDefaultAsync(f => f.FormId == 6);
            Assert.NotNull(detail);
            Assert.Equal("29001015555556", detail.EmployeeId);

            guardMock.Verify(g => g.EnsureFormDailyOpenAsync(6), Times.Exactly(2));
            cacheMock.Verify(c => c.Remove("form_details_6"), Times.Once());
        }

        [Fact]
        public async Task UploadExcelEmployeesToForm_SafeAmountValidation_WhenAmountMalformed()
        {
            var dbName = Guid.NewGuid().ToString();
            var (service, context, _, guardMock, cacheMock, uowMock) = CreateFormServiceWithSpy(dbName);

            context.Employees.Add(new Employee { Id = "29001016666666", Name = "موظف مبلغ خاطئ", IsActive = true });
            context.Set<Form>().Add(new Form { Id = 7, Name = "استمارة 7", IsActive = true });
            await context.SaveChangesAsync();

            // Amount is not a valid number
            var excelFile = CreateExcelFile(
                ("29001016666666", "", "", "قسم", "موظف مبلغ خاطئ", "xyz_not_amount")
            );

            var result = await service.UploadExcelEmployeesToForm(new UploadEmployeesToFormRequest
            {
                FormId = 7,
                File = excelFile,
                ValidateName = false
            });

            Assert.True(result.IsFailure);
            Assert.Equal("1500", result.Error.Code);
            var messages = System.Text.Json.JsonSerializer.Deserialize<List<string>>(result.Error.Message);
            Assert.NotNull(messages);
            Assert.NotEmpty(messages);
            Assert.Contains("يوجد مشكلة في قيمة المبلغ بالسطر رقم 1", messages[0]);
            Assert.Contains("xyz_not_amount", messages[0]);

            // Fail-closed
            var detailsCount = await context.Set<FormDetails>().CountAsync(f => f.FormId == 7);
            Assert.Equal(0, detailsCount);

            // Cache never evicted
            cacheMock.Verify(c => c.Remove(It.IsAny<object>()), Times.Never());
        }

        [Fact]
        public async Task UploadExcelEmployeesToForm_AtomicReplacement_SingleSaveChanges_ReplacesOldDetails()
        {
            var dbName = Guid.NewGuid().ToString();
            var (service, context, _, guardMock, cacheMock, uowMock) = CreateFormServiceWithSpy(dbName);

            var emp1 = new Employee { Id = "29001017777771", Name = "موظف قديم 1", IsActive = true };
            var emp2 = new Employee { Id = "29001017777772", Name = "موظف جديد 2", IsActive = true };
            context.Employees.AddRange(emp1, emp2);

            var form = new Form { Id = 8, Name = "استمارة 8", IsActive = true };
            context.Set<Form>().Add(form);

            // Add 2 existing details
            context.Set<FormDetails>().AddRange(
                new FormDetails { FormId = 8, EmployeeId = emp1.Id, OrderNum = 1, Amount = 10.0, IsActive = true },
                new FormDetails { FormId = 8, EmployeeId = emp1.Id, OrderNum = 2, Amount = 20.0, IsActive = true }
            );
            await context.SaveChangesAsync();

            Assert.Equal(2, await context.Set<FormDetails>().CountAsync(f => f.FormId == 8));

            // Upload 1 new row for emp2
            var excelFile = CreateExcelFile(
                ("29001017777772", "", "", "قسم", "موظف جديد 2", "99.0")
            );

            var result = await service.UploadExcelEmployeesToForm(new UploadEmployeesToFormRequest
            {
                FormId = 8,
                File = excelFile,
                ValidateName = false
            });

            Assert.True(result.IsSuccess);

            // Only the new detail exists now
            var details = await context.Set<FormDetails>().Where(f => f.FormId == 8).ToListAsync();
            Assert.Single(details);
            Assert.Equal(emp2.Id, details[0].EmployeeId);
            Assert.Equal(99.0, details[0].Amount);
            Assert.Equal(1, details[0].OrderNum);

            guardMock.Verify(g => g.EnsureFormDailyOpenAsync(8), Times.Exactly(2));
            uowMock.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once());
            cacheMock.Verify(c => c.Remove("form_details_8"), Times.Once());
        }

        [Fact]
        public async Task UploadExcelEmployeesToForm_1000UniqueNationalIds_ExecutesExactlyOneBatchLookup()
        {
            var dbName = Guid.NewGuid().ToString();
            var (service, context, getQueryCount, _, _, _) = CreateFormServiceWithSpy(dbName);

            var employees = Enumerable.Range(1, 1000)
                .Select(i => new Employee
                {
                    Id = $"2900000000{i:D4}",
                    Name = $"موظف {i}",
                    IsActive = true
                })
                .ToList();
            context.Employees.AddRange(employees);
            context.Set<Form>().Add(new Form { Id = 100, Name = "استمارة 1000", IsActive = true });
            await context.SaveChangesAsync();

            var rows = Enumerable.Range(1, 1000)
                .Select(i => ($"2900000000{i:D4}", "", "", "قسم", $"موظف {i}", "50.0"));
            var excelFile = CreateExcelFile(rows);

            // Act
            var result = await service.UploadExcelEmployeesToForm(new UploadEmployeesToFormRequest
            {
                FormId = 100,
                File = excelFile,
                ValidateName = false
            });

            // Assert
            Assert.True(result.IsSuccess);
            int queryCount = getQueryCount();
            Assert.Equal(1, queryCount); // Exactly 1 batch lookup query, NOT 1000

            var detailsCount = await context.Set<FormDetails>().CountAsync(f => f.FormId == 100);
            Assert.Equal(1000, detailsCount);
        }

        [Fact]
        public async Task UploadExcelEmployeesToForm_1001UniqueNationalIds_ExecutesExactlyTwoBatchLookups()
        {
            var dbName = Guid.NewGuid().ToString();
            var (service, context, getQueryCount, _, _, _) = CreateFormServiceWithSpy(dbName);

            var employees = Enumerable.Range(1, 1001)
                .Select(i => new Employee
                {
                    Id = $"2900000000{i:D4}",
                    Name = $"موظف {i}",
                    IsActive = true
                })
                .ToList();
            context.Employees.AddRange(employees);
            context.Set<Form>().Add(new Form { Id = 101, Name = "استمارة 1001", IsActive = true });
            await context.SaveChangesAsync();

            var rows = Enumerable.Range(1, 1001)
                .Select(i => ($"2900000000{i:D4}", "", "", "قسم", $"موظف {i}", "50.0"));
            var excelFile = CreateExcelFile(rows);

            // Act
            var result = await service.UploadExcelEmployeesToForm(new UploadEmployeesToFormRequest
            {
                FormId = 101,
                File = excelFile,
                ValidateName = false
            });

            // Assert
            Assert.True(result.IsSuccess);
            int queryCount = getQueryCount();
            Assert.Equal(2, queryCount); // Exactly 2 batch lookup queries (1000 + 1), NOT 1001

            var detailsCount = await context.Set<FormDetails>().CountAsync(f => f.FormId == 101);
            Assert.Equal(1001, detailsCount);
        }

        [Fact]
        public async Task UploadExcelEmployeesToForm_SaveChangesFails_DoesNotEvictCache()
        {
            var dbName = Guid.NewGuid().ToString();
            var (service, context, _, guardMock, cacheMock, uowMock) = CreateFormServiceWithSpy(dbName);

            var emp = new Employee { Id = "29001018888888", Name = "موظف اختبار حفظ", IsActive = true };
            context.Employees.Add(emp);
            context.Set<Form>().Add(new Form { Id = 200, Name = "استمارة 200", IsActive = true });
            await context.SaveChangesAsync();

            // Configure SaveChanges to throw an exception
            uowMock.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()))
                .ThrowsAsync(new DbUpdateException("Simulated database failure during SaveChanges", new Exception()));

            var excelFile = CreateExcelFile(
                ("29001018888888", "", "", "قسم", "موظف اختبار حفظ", "100.0")
            );

            // Act & Assert: Exception propagates naturally
            await Assert.ThrowsAsync<DbUpdateException>(() => service.UploadExcelEmployeesToForm(new UploadEmployeesToFormRequest
            {
                FormId = 200,
                File = excelFile,
                ValidateName = false
            }));

            // Cache MUST NOT be evicted when SaveChanges fails
            cacheMock.Verify(c => c.Remove(It.IsAny<object>()), Times.Never());

            // Both guards were checked before DB write attempt
            guardMock.Verify(g => g.EnsureFormDailyOpenAsync(200), Times.Exactly(2));
        }
    }
}
