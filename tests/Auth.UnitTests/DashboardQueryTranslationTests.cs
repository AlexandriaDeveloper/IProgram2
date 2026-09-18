using System;
using System.Linq;
using Auth.Infrastructure;
using Core.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Auth.UnitTests
{
    public class DashboardQueryTranslationTests
    {
        private ApplicationContext CreateContext()
        {
            var options = new DbContextOptionsBuilder<ApplicationContext>()
                .UseSqlServer("Server=localhost;Database=DummyDb;Trusted_Connection=True;TrustServerCertificate=True;")
                .Options;

            return new ApplicationContext(options);
        }

        [Fact]
        public void Test_TopEmployees_Translation()
        {
            using var context = CreateContext();
            var startDate = new DateTime(2026, 1, 1);
            var endDate = new DateTime(2026, 1, 31);

            var query = context.Set<Form>()
                .Where(f => f.IsActive && f.CreatedAt >= startDate && f.CreatedAt <= endDate)
                .SelectMany(f => f.FormDetails)
                .Where(fd => fd.IsActive && fd.Employee != null)
                .GroupBy(fd => new
                {
                    fd.Employee.Id,
                    fd.Employee.Name,
                    DepartmentName = fd.Employee.Department != null ? fd.Employee.Department.Name : "غير محدد"
                })
                .Select(g => new
                {
                    Id = g.Key.Id,
                    Name = g.Key.Name,
                    Department = g.Key.DepartmentName,
                    FormCount = g.Count(),
                    TotalAmount = g.Sum(fd => fd.Amount)
                })
                .OrderByDescending(x => x.TotalAmount)
                .Take(5);

            var sql = query.ToQueryString();
            Assert.Contains("GROUP BY", sql);
            Assert.Contains("TOP(@__p_", sql);
            Assert.Contains("[f0].[IsActive] = CAST(1 AS bit)", sql);
        }

        [Fact]
        public void Test_DepartmentStats_Translation()
        {
            using var context = CreateContext();
            var startDate = new DateTime(2026, 1, 1);
            var endDate = new DateTime(2026, 1, 31);

            var query = context.Set<Form>()
                .Where(f => f.IsActive && f.CreatedAt >= startDate && f.CreatedAt <= endDate)
                .SelectMany(f => f.FormDetails)
                .Where(fd => fd.IsActive)
                .GroupBy(fd => fd.Employee != null && fd.Employee.Department != null ? fd.Employee.Department.Name : "غير محدد")
                .Select(g => new
                {
                    Label = g.Key,
                    Value = g.Sum(fd => fd.Amount)
                });

            var sql = query.ToQueryString();
            Assert.Contains("GROUP BY", sql);
            Assert.Contains("SUM", sql);
            Assert.Contains("[f0].[IsActive] = CAST(1 AS bit)", sql);
        }

        [Fact]
        public void Test_RecentForms_Translation()
        {
            using var context = CreateContext();
            var startDate = new DateTime(2026, 1, 1);
            var endDate = new DateTime(2026, 1, 31);

            var query = context.Set<Form>()
                .Where(f => f.IsActive && f.CreatedAt >= startDate && f.CreatedAt <= endDate)
                .OrderByDescending(f => f.CreatedAt)
                .Take(5)
                .Select(f => new
                {
                    Id = f.Id,
                    Description = f.Description,
                    Date = f.CreatedAt,
                    EmployeeCount = f.FormDetails.Count(fd => fd.IsActive),
                    TotalAmount = f.FormDetails.Where(fd => fd.IsActive).Sum(fd => (double?)fd.Amount) ?? 0.0
                });

            var sql = query.ToQueryString();
            Assert.Contains("TOP(@__p_", sql);
            Assert.Contains("[f0].[IsActive] = CAST(1 AS bit)", sql);
        }

        [Fact]
        public void Test_ChartData_Daily_Translation()
        {
            using var context = CreateContext();
            var startDate = new DateTime(2026, 1, 1);
            var endDate = new DateTime(2026, 1, 31);

            var query = context.Set<Form>()
                .Where(f => f.IsActive && f.CreatedAt >= startDate && f.CreatedAt <= endDate)
                .GroupBy(f => f.CreatedAt.Date)
                .Select(g => new
                {
                    Date = g.Key,
                    FormCount = g.Count(),
                    TotalAmount = g.SelectMany(f => f.FormDetails).Where(fd => fd.IsActive).Sum(fd => (double?)fd.Amount) ?? 0.0,
                    EmployeeCount = g.SelectMany(f => f.FormDetails).Where(fd => fd.IsActive).Select(fd => fd.EmployeeId).Distinct().Count()
                });

            var sql = query.ToQueryString();
            Assert.Contains("GROUP BY", sql);
            Assert.Contains("[f1].[IsActive] = CAST(1 AS bit)", sql);
        }

        [Fact]
        public void Test_ChartData_Monthly_Translation()
        {
            using var context = CreateContext();
            var startDate = new DateTime(2025, 1, 1);
            var endDate = new DateTime(2026, 1, 31);

            var query = context.Set<Form>()
                .Where(f => f.IsActive && f.CreatedAt >= startDate && f.CreatedAt <= endDate)
                .GroupBy(f => new { f.CreatedAt.Year, f.CreatedAt.Month })
                .Select(g => new
                {
                    g.Key.Year,
                    g.Key.Month,
                    FormCount = g.Count(),
                    TotalAmount = g.SelectMany(f => f.FormDetails).Where(fd => fd.IsActive).Sum(fd => (double?)fd.Amount) ?? 0.0,
                    EmployeeCount = g.SelectMany(f => f.FormDetails).Where(fd => fd.IsActive).Select(fd => fd.EmployeeId).Distinct().Count()
                });

            var sql = query.ToQueryString();
            Assert.Contains("GROUP BY", sql);
            Assert.Contains("[f1].[IsActive] = CAST(1 AS bit)", sql);
        }
    }
}
