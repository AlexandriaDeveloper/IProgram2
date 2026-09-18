using Application.Interfaces;
using Application.DTOs;
using Core.Interfaces;
using Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Application.Features
{
    public class DashboardService : IDashboardService
    {
        private readonly IFormRepository _formRepository;
        private readonly IEmployeeRepository _employeeRepository;

        public DashboardService(IFormRepository formRepository, IEmployeeRepository employeeRepository)
        {
            _formRepository = formRepository;
            _employeeRepository = employeeRepository;
        }

        public async Task<DashboardDto> GetDashboardStatsAsync(DashboardFilterRequest request)
        {
            // 1. Determine Date Range (Current vs Previous)
            var endDate = request.EndDate ?? DateTime.Now;
            var startDate = request.StartDate ?? new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1); // Default to start of current month

            var duration = endDate - startDate;
            var prevEndDate = startDate.AddDays(-1);
            var prevStartDate = prevEndDate.AddDays(-duration.TotalDays);

            // 2. Base IQueryables (AsNoTracking, Server-Side Filtering)
            // _formRepository.GetQueryable() filters to active forms by default (f.IsActive == true)
            var currentFormsQuery = _formRepository.GetQueryable()
                .AsNoTracking()
                .Where(f => f.CreatedAt >= startDate && f.CreatedAt <= endDate);

            var prevFormsQuery = _formRepository.GetQueryable()
                .AsNoTracking()
                .Where(f => f.CreatedAt >= prevStartDate && f.CreatedAt <= prevEndDate);

            // 3. All-time Totals (Sequentially executed on DbContext)
            var totalEmployees = await _employeeRepository.GetQueryable().AsNoTracking().CountAsync();
            var totalForms = await _formRepository.GetQueryable().AsNoTracking().CountAsync();

            // 4. Current Period Metrics (Direct SQL Aggregations on Active FormDetails only)
            var activeForms = await currentFormsQuery.CountAsync();
            var totalAmount = await currentFormsQuery
                .SelectMany(f => f.FormDetails)
                .Where(fd => fd.IsActive)
                .SumAsync(fd => (double?)fd.Amount) ?? 0.0;
            var currentDistinctEmp = await currentFormsQuery
                .SelectMany(f => f.FormDetails)
                .Where(fd => fd.IsActive)
                .Select(fd => fd.EmployeeId)
                .Distinct()
                .CountAsync();

            // 5. Previous Period Metrics (Direct SQL Aggregations on Active FormDetails only)
            var prevActiveForms = await prevFormsQuery.CountAsync();
            var prevTotalAmount = await prevFormsQuery
                .SelectMany(f => f.FormDetails)
                .Where(fd => fd.IsActive)
                .SumAsync(fd => (double?)fd.Amount) ?? 0.0;
            var prevDistinctEmp = await prevFormsQuery
                .SelectMany(f => f.FormDetails)
                .Where(fd => fd.IsActive)
                .Select(fd => fd.EmployeeId)
                .Distinct()
                .CountAsync();

            // Trends
            double CalcTrend(double current, double prev) => prev == 0 ? 0 : ((current - prev) / prev) * 100;

            // 6. Top Employees (SQL GroupBy + Aggregates on Active FormDetails + OrderByDescending + Take 5)
            var topEmployees = await currentFormsQuery
                .SelectMany(f => f.FormDetails)
                .Where(fd => fd.IsActive && fd.Employee != null)
                .GroupBy(fd => new
                {
                    fd.Employee.Id,
                    fd.Employee.Name,
                    DepartmentName = fd.Employee.Department != null ? fd.Employee.Department.Name : "غير محدد"
                })
                .Select(g => new EmployeeSummaryDto
                {
                    Id = g.Key.Id,
                    Name = g.Key.Name,
                    Department = g.Key.DepartmentName,
                    FormCount = g.Count(),
                    TotalAmount = g.Sum(fd => fd.Amount)
                })
                .OrderByDescending(x => x.TotalAmount)
                .Take(5)
                .ToListAsync();

            // 7. Department Stats (SQL GroupBy + Sum on Active FormDetails)
            var formsByDept = await currentFormsQuery
                .SelectMany(f => f.FormDetails)
                .Where(fd => fd.IsActive)
                .GroupBy(fd => fd.Employee != null && fd.Employee.Department != null ? fd.Employee.Department.Name : "غير محدد")
                .Select(g => new PieChartDto
                {
                    Label = g.Key,
                    Value = g.Sum(fd => fd.Amount)
                })
                .ToListAsync();

            // 8. Recent Forms (SQL Top 5 Projected with Active FormDetails counts and sums)
            var recentForms = await currentFormsQuery
                .OrderByDescending(f => f.CreatedAt)
                .Take(5)
                .Select(f => new FormSummaryDto
                {
                    Id = f.Id,
                    Description = f.Description,
                    Date = f.CreatedAt,
                    EmployeeCount = f.FormDetails.Count(fd => fd.IsActive),
                    TotalAmount = f.FormDetails.Where(fd => fd.IsActive).Sum(fd => (double?)fd.Amount) ?? 0.0
                })
                .ToListAsync();

            // 9. Chart Data (SQL GroupBy on Active FormDetails + In-memory Label Formatting on Small Aggregated Result)
            List<ChartDataDto> chartDataDtos;
            if (duration.TotalDays <= 35)
            {
                var dailyAggregates = await currentFormsQuery
                    .GroupBy(f => f.CreatedAt.Date)
                    .Select(g => new
                    {
                        Date = g.Key,
                        FormCount = g.Count(),
                        EmployeeCount = g.SelectMany(f => f.FormDetails).Where(fd => fd.IsActive).Select(fd => fd.EmployeeId).Distinct().Count(),
                        TotalAmount = g.SelectMany(f => f.FormDetails).Where(fd => fd.IsActive).Sum(fd => (double?)fd.Amount) ?? 0.0
                    })
                    .ToListAsync();

                chartDataDtos = dailyAggregates
                    .Select(x => new ChartDataDto
                    {
                        Label = x.Date.ToString("dd/MM"),
                        FormCount = x.FormCount,
                        EmployeeCount = x.EmployeeCount,
                        TotalAmount = x.TotalAmount
                    })
                    .OrderBy(x => x.Label)
                    .ToList();
            }
            else
            {
                var monthlyAggregates = await currentFormsQuery
                    .GroupBy(f => new { f.CreatedAt.Year, f.CreatedAt.Month })
                    .Select(g => new
                    {
                        g.Key.Year,
                        g.Key.Month,
                        FormCount = g.Count(),
                        EmployeeCount = g.SelectMany(f => f.FormDetails).Where(fd => fd.IsActive).Select(fd => fd.EmployeeId).Distinct().Count(),
                        TotalAmount = g.SelectMany(f => f.FormDetails).Where(fd => fd.IsActive).Sum(fd => (double?)fd.Amount) ?? 0.0
                    })
                    .ToListAsync();

                chartDataDtos = monthlyAggregates
                    .Select(x => new ChartDataDto
                    {
                        Label = $"{x.Year}-{x.Month}",
                        FormCount = x.FormCount,
                        EmployeeCount = x.EmployeeCount,
                        TotalAmount = x.TotalAmount
                    })
                    .OrderBy(x => x.Label)
                    .ToList();
            }

            return new DashboardDto
            {
                TotalEmployees = totalEmployees,
                TotalForms = totalForms,
                ActiveForms = activeForms,
                TotalAmount = totalAmount,

                // Trends
                TotalAmountChange = CalcTrend(totalAmount, prevTotalAmount),
                FormCountChange = CalcTrend(activeForms, (double)prevActiveForms),
                EmployeeCountChange = CalcTrend(currentDistinctEmp, (double)prevDistinctEmp),

                RecentForms = recentForms,
                TopEmployees = topEmployees,
                FormsByDepartment = formsByDept,
                ChartData = chartDataDtos
            };
        }
    }
}
