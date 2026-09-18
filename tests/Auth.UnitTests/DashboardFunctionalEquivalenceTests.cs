using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Application.DTOs;
using Application.Features;
using Auth.Infrastructure;
using Core.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Moq;
using Persistence.Repository;
using Xunit;

namespace Auth.UnitTests
{
    public class DashboardFunctionalEquivalenceTests
    {
        private ApplicationContext CreateInMemoryContext(string dbName)
        {
            var options = new DbContextOptionsBuilder<ApplicationContext>()
                .UseInMemoryDatabase(databaseName: dbName)
                .Options;

            return new ApplicationContext(options);
        }

        private async Task SeedTestDataAsync(ApplicationContext context, DateTime currentStart, DateTime currentEnd, DateTime prevStart, DateTime prevEnd)
        {
            // 1. Departments
            var deptIT = new Department { Id = 1, Name = "تكنولوجيا المعلومات", IsActive = true, CreatedAt = DateTime.Now.AddMonths(-6) };
            var deptHR = new Department { Id = 2, Name = "الموارد البشرية", IsActive = true, CreatedAt = DateTime.Now.AddMonths(-6) };
            context.Departments.AddRange(deptIT, deptHR);

            // 2. Employees (6 active, 1 inactive)
            var emp1 = new Employee { Id = "29001011234561", Name = "أحمد علي", DepartmentId = 1, Department = deptIT, IsActive = true, CreatedAt = DateTime.Now.AddMonths(-5) };
            var emp2 = new Employee { Id = "29001011234562", Name = "محمد حسن", DepartmentId = 2, Department = deptHR, IsActive = true, CreatedAt = DateTime.Now.AddMonths(-5) };
            var emp3 = new Employee { Id = "29001011234563", Name = "محمود كمال", DepartmentId = null, Department = null, IsActive = true, CreatedAt = DateTime.Now.AddMonths(-5) }; // No department
            var emp4 = new Employee { Id = "29001011234564", Name = "سارة إبراهيم", DepartmentId = 1, Department = deptIT, IsActive = true, CreatedAt = DateTime.Now.AddMonths(-5) };
            var emp5 = new Employee { Id = "29001011234565", Name = "علياء سعيد", DepartmentId = 2, Department = deptHR, IsActive = true, CreatedAt = DateTime.Now.AddMonths(-5) };
            var emp6 = new Employee { Id = "29001011234566", Name = "خالد يوسف", DepartmentId = 1, Department = deptIT, IsActive = true, CreatedAt = DateTime.Now.AddMonths(-5) };
            var empInactive = new Employee { Id = "29001011234599", Name = "موظف ملغي", DepartmentId = 1, Department = deptIT, IsActive = false, CreatedAt = DateTime.Now.AddMonths(-5) };

            context.Employees.AddRange(emp1, emp2, emp3, emp4, emp5, emp6, empInactive);

            // 3. Current Period Forms
            // Form 1: currentStart + 1 day
            var form1Date = currentStart.AddDays(1);
            var form1 = new Form { Id = 101, Description = "استمارة يناير 1", CreatedAt = form1Date, IsActive = true };
            form1.FormDetails.Add(new FormDetails { FormId = 101, EmployeeId = emp1.Id, Employee = emp1, Amount = 1000.0, IsActive = true });
            form1.FormDetails.Add(new FormDetails { FormId = 101, EmployeeId = emp2.Id, Employee = emp2, Amount = 2000.0, IsActive = true });
            form1.FormDetails.Add(new FormDetails { FormId = 101, EmployeeId = emp3.Id, Employee = emp3, Amount = 500.0, IsActive = true }); // No dept

            // Form 2: currentStart + 3 days
            var form2Date = currentStart.AddDays(3);
            var form2 = new Form { Id = 102, Description = "استمارة يناير 2", CreatedAt = form2Date, IsActive = true };
            form2.FormDetails.Add(new FormDetails { FormId = 102, EmployeeId = emp1.Id, Employee = emp1, Amount = 1500.0, IsActive = true });
            form2.FormDetails.Add(new FormDetails { FormId = 102, EmployeeId = emp4.Id, Employee = emp4, Amount = 3000.0, IsActive = true });

            // Form 3: currentStart + 5 days
            var form3Date = currentStart.AddDays(5);
            var form3 = new Form { Id = 103, Description = "استمارة يناير 3", CreatedAt = form3Date, IsActive = true };
            form3.FormDetails.Add(new FormDetails { FormId = 103, EmployeeId = emp2.Id, Employee = emp2, Amount = 1000.0, IsActive = true });
            form3.FormDetails.Add(new FormDetails { FormId = 103, EmployeeId = emp3.Id, Employee = emp3, Amount = 1000.0, IsActive = true }); // No dept

            // Form 4: currentStart + 7 days (Empty form, no FormDetails)
            var form4Date = currentStart.AddDays(7);
            var form4 = new Form { Id = 104, Description = "استمارة فارغة", CreatedAt = form4Date, IsActive = true };

            // Form 5: Inactive form in current period (IsActive = false, should NOT be counted in activeForms)
            var form5 = new Form { Id = 105, Description = "استمارة محذوفة", CreatedAt = form1Date, IsActive = false };
            form5.FormDetails.Add(new FormDetails { FormId = 105, EmployeeId = emp6.Id, Employee = emp6, Amount = 9999.0, IsActive = true });

            // 4. Previous Period Forms
            var prevForm1Date = prevStart.AddDays(1);
            var prevForm1 = new Form { Id = 201, Description = "استمارة سابقة 1", CreatedAt = prevForm1Date, IsActive = true };
            prevForm1.FormDetails.Add(new FormDetails { FormId = 201, EmployeeId = emp1.Id, Employee = emp1, Amount = 1000.0, IsActive = true });
            prevForm1.FormDetails.Add(new FormDetails { FormId = 201, EmployeeId = emp2.Id, Employee = emp2, Amount = 1000.0, IsActive = true });

            var prevForm2Date = prevStart.AddDays(3);
            var prevForm2 = new Form { Id = 202, Description = "استمارة سابقة 2", CreatedAt = prevForm2Date, IsActive = true };
            prevForm2.FormDetails.Add(new FormDetails { FormId = 202, EmployeeId = emp5.Id, Employee = emp5, Amount = 2000.0, IsActive = true });

            context.Set<Form>().AddRange(form1, form2, form3, form4, form5, prevForm1, prevForm2);
            await context.SaveChangesAsync();
        }

        [Fact]
        public async Task DashboardService_Returns_AccurateMetrics_AndMaintains_Equivalence()
        {
            // Arrange
            var dbName = Guid.NewGuid().ToString();
            using var context = CreateInMemoryContext(dbName);

            var currentStart = new DateTime(2026, 1, 1, 0, 0, 0);
            var currentEnd = new DateTime(2026, 1, 20, 23, 59, 59);
            // duration = 19.99 days <= 35 days -> triggers Daily Chart Data
            var duration = currentEnd - currentStart;
            var prevEnd = currentStart.AddDays(-1);
            var prevStart = prevEnd.AddDays(-duration.TotalDays);

            await SeedTestDataAsync(context, currentStart, currentEnd, prevStart, prevEnd);

            var httpContextAccessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
            var formRepo = new FormRepository(context, httpContextAccessor);
            var empRepo = new EmployeeRepository(context, httpContextAccessor);

            var dashboardService = new DashboardService(formRepo, empRepo);

            var request = new DashboardFilterRequest
            {
                StartDate = currentStart,
                EndDate = currentEnd
            };

            // Act
            var stats = await dashboardService.GetDashboardStatsAsync(request);

            // Assert
            // 1. TotalEmployees: Active employees in system (6 active, 1 inactive excluded)
            Assert.Equal(6, stats.TotalEmployees);

            // 2. TotalForms: All active forms across all time (4 current active + 2 prev active = 6)
            Assert.Equal(6, stats.TotalForms);

            // 3. ActiveForms in current period: forms 101, 102, 103, 104 (105 is inactive, excluded)
            Assert.Equal(4, stats.ActiveForms);

            // 4. TotalAmount in current period:
            // Form 101: 1000 + 2000 + 500 = 3500
            // Form 102: 1500 + 3000 = 4500
            // Form 103: 1000 + 1000 = 2000
            // Form 104: 0
            // Total = 10,000.0
            Assert.Equal(10000.0, stats.TotalAmount);

            // 5. Previous period metrics & Trends:
            // Prev Form 201: 1000 + 1000 = 2000
            // Prev Form 202: 2000
            // Prev TotalAmount = 4000.0
            // Prev ActiveForms = 2
            // Prev Distinct Employees = 3 (emp1, emp2, emp5)
            // Current Distinct Employees = 4 (emp1, emp2, emp3, emp4)
            // TotalAmountChange = ((10000 - 4000) / 4000) * 100 = 150.0%
            // FormCountChange = ((4 - 2) / 2) * 100 = 100.0%
            // EmployeeCountChange = ((4 - 3) / 3) * 100 = 33.333333333333336%
            Assert.Equal(150.0, stats.TotalAmountChange);
            Assert.Equal(100.0, stats.FormCountChange);
            Assert.True(Math.Abs(stats.EmployeeCountChange - 33.33333333) < 0.01);

            // 6. Top Employees (Top 5 by TotalAmount):
            // emp4: 3000 (1 form)
            // emp2: 2000 + 1000 = 3000 (2 forms)
            // emp1: 1000 + 1500 = 2500 (2 forms)
            // emp3 (No Dept): 500 + 1000 = 1500 (2 forms)
            Assert.NotNull(stats.TopEmployees);
            Assert.Equal(4, stats.TopEmployees.Count); // only 4 employees have records in current period

            var topEmpIds = stats.TopEmployees.Select(e => e.Id).ToList();
            Assert.Contains("29001011234564", topEmpIds); // emp4 (3000)
            Assert.Contains("29001011234562", topEmpIds); // emp2 (3000)
            Assert.Contains("29001011234561", topEmpIds); // emp1 (2500)
            Assert.Contains("29001011234563", topEmpIds); // emp3 (1500)

            // Ordering: top 2 have 3000, 3rd has 2500, 4th has 1500
            Assert.Equal(3000.0, stats.TopEmployees[0].TotalAmount);
            Assert.Equal(3000.0, stats.TopEmployees[1].TotalAmount);
            Assert.Equal(2500.0, stats.TopEmployees[2].TotalAmount);
            Assert.Equal(1500.0, stats.TopEmployees[3].TotalAmount);

            // Verify "غير محدد" fallback for employee without department
            var emp3Summary = stats.TopEmployees.First(e => e.Id == "29001011234563");
            Assert.Equal("غير محدد", emp3Summary.Department);
            Assert.Equal(2, emp3Summary.FormCount);
            Assert.Equal(1500.0, emp3Summary.TotalAmount);

            // 7. FormsByDepartment (Pie Chart):
            // IT: emp1 (1000 + 1500 = 2500) + emp4 (3000) = 5500.0
            // HR: emp2 (2000 + 1000) = 3000.0
            // غير محدد: emp3 (500 + 1000) = 1500.0
            Assert.NotNull(stats.FormsByDepartment);
            Assert.Equal(3, stats.FormsByDepartment.Count);

            var itDept = stats.FormsByDepartment.FirstOrDefault(d => d.Label == "تكنولوجيا المعلومات");
            Assert.NotNull(itDept);
            Assert.Equal(5500.0, itDept.Value);

            var hrDept = stats.FormsByDepartment.FirstOrDefault(d => d.Label == "الموارد البشرية");
            Assert.NotNull(hrDept);
            Assert.Equal(3000.0, hrDept.Value);

            var unassignedDept = stats.FormsByDepartment.FirstOrDefault(d => d.Label == "غير محدد");
            Assert.NotNull(unassignedDept);
            Assert.Equal(1500.0, unassignedDept.Value);

            // 8. Recent Forms (ordered by CreatedAt desc, max 5):
            Assert.NotNull(stats.RecentForms);
            Assert.Equal(4, stats.RecentForms.Count); // 4 active forms in period
            Assert.Equal(104, stats.RecentForms[0].Id); // newest (empty form)
            Assert.Equal(0, stats.RecentForms[0].EmployeeCount);
            Assert.Equal(0.0, stats.RecentForms[0].TotalAmount);

            Assert.Equal(103, stats.RecentForms[1].Id);
            Assert.Equal(2, stats.RecentForms[1].EmployeeCount);
            Assert.Equal(2000.0, stats.RecentForms[1].TotalAmount);

            Assert.Equal(102, stats.RecentForms[2].Id);
            Assert.Equal(2, stats.RecentForms[2].EmployeeCount);
            Assert.Equal(4500.0, stats.RecentForms[2].TotalAmount);

            Assert.Equal(101, stats.RecentForms[3].Id);
            Assert.Equal(3, stats.RecentForms[3].EmployeeCount);
            Assert.Equal(3500.0, stats.RecentForms[3].TotalAmount);

            // 9. Daily Chart Data (duration <= 35 days):
            Assert.NotNull(stats.ChartData);
            Assert.Equal(4, stats.ChartData.Count); // 4 distinct dates with forms
            foreach (var point in stats.ChartData)
            {
                Assert.Matches(@"^\d{2}/\d{2}$", point.Label);
            }
        }

        [Fact]
        public async Task DashboardService_Generates_MonthlyChartData_WhenDurationExceeds35Days()
        {
            // Arrange
            var dbName = Guid.NewGuid().ToString();
            using var context = CreateInMemoryContext(dbName);

            var start = new DateTime(2025, 1, 1);
            var end = new DateTime(2025, 3, 31); // 90 days > 35 days -> triggers Monthly Chart Data

            var formJan = new Form { Id = 301, Description = "يناير", CreatedAt = new DateTime(2025, 1, 15), IsActive = true };
            formJan.FormDetails.Add(new FormDetails { FormId = 301, EmployeeId = "111", Amount = 500, IsActive = true });

            var formFeb = new Form { Id = 302, Description = "فبراير", CreatedAt = new DateTime(2025, 2, 20), IsActive = true };
            formFeb.FormDetails.Add(new FormDetails { FormId = 302, EmployeeId = "111", Amount = 700, IsActive = true });
            formFeb.FormDetails.Add(new FormDetails { FormId = 302, EmployeeId = "222", Amount = 300, IsActive = true });

            context.Set<Form>().AddRange(formJan, formFeb);
            await context.SaveChangesAsync();

            var httpContextAccessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
            var formRepo = new FormRepository(context, httpContextAccessor);
            var empRepo = new EmployeeRepository(context, httpContextAccessor);

            var service = new DashboardService(formRepo, empRepo);

            // Act
            var stats = await service.GetDashboardStatsAsync(new DashboardFilterRequest { StartDate = start, EndDate = end });

            // Assert
            Assert.NotNull(stats.ChartData);
            Assert.Equal(2, stats.ChartData.Count);
            Assert.Equal("2025-1", stats.ChartData[0].Label);
            Assert.Equal(1, stats.ChartData[0].FormCount);
            Assert.Equal(1, stats.ChartData[0].EmployeeCount);
            Assert.Equal(500.0, stats.ChartData[0].TotalAmount);

            Assert.Equal("2025-2", stats.ChartData[1].Label);
            Assert.Equal(1, stats.ChartData[1].FormCount);
            Assert.Equal(2, stats.ChartData[1].EmployeeCount);
            Assert.Equal(1000.0, stats.ChartData[1].TotalAmount);
        }

        [Fact]
        public void ArchitecturalEvidence_DashboardService_DoesNotBulkLoad_EntityGraphs()
        {
            // Verify source code architectural invariants
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && dir.GetFiles("*.sln").Length == 0)
            {
                dir = dir.Parent;
            }
            var root = dir?.FullName ?? AppContext.BaseDirectory;
            var serviceFilePath = Path.Combine(root, "src", "Application", "Features", "DashboardService.cs");

            Assert.True(File.Exists(serviceFilePath), $"Could not find DashboardService.cs at {serviceFilePath}");

            var sourceCode = File.ReadAllText(serviceFilePath);

            // 1. MUST NOT have bulk loading .Include(f => f.FormDetails)
            Assert.DoesNotContain(".Include(f => f.FormDetails)", sourceCode);

            // 2. MUST NOT have .ThenInclude
            Assert.DoesNotContain(".ThenInclude", sourceCode);

            // 3. MUST NOT have in-memory entity graph SelectMany over loaded forms
            Assert.DoesNotContain("currentForms.SelectMany", sourceCode);
            Assert.DoesNotContain("prevForms.SelectMany", sourceCode);

            // 4. MUST NOT execute Task.WhenAll on EF queries (thread safety)
            Assert.DoesNotContain("Task.WhenAll", sourceCode);
        }
    }
}
