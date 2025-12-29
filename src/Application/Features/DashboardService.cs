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

            // 2. Fetch Raw Data for BOTH periods to do in-memory aggregation
            // Fetch for Current Period
            var currentForms = await _formRepository.GetQueryable()
                .Include(f => f.FormDetails).ThenInclude(fd => fd.Employee).ThenInclude(e => e.Department)
                .Where(f => f.CreatedAt >= startDate && f.CreatedAt <= endDate)
                .ToListAsync();

            // Fetch for Previous Period (Optimized: only need aggregates)
            var prevForms = await _formRepository.GetQueryable()
                 .Include(f => f.FormDetails)
                 .Where(f => f.CreatedAt >= prevStartDate && f.CreatedAt <= prevEndDate)
                 .Select(f => new { Details = f.FormDetails.Select(fd => new { fd.Amount, fd.EmployeeId }).ToList() })
                 .ToListAsync();

            // 3. Calculate Key Metrics
            var totalEmployees = await _employeeRepository.GetQueryable().CountAsync(); // Total distinct employees in system
            var activeForms = currentForms.Count;
            var totalAmount = currentForms.Sum(f => f.FormDetails.Sum(fd => fd.Amount));

            // Previous Metrics for Trend Calculation
            var prevActiveForms = prevForms.Count;
            var prevTotalAmount = prevForms.Sum(f => f.Details.Sum(d => d.Amount));
            // Distinct employees paid in period
            var currentDistinctEmp = currentForms.SelectMany(f => f.FormDetails.Select(fd => fd.EmployeeId)).Distinct().Count();
            var prevDistinctEmp = prevForms.SelectMany(f => f.Details.Select(d => d.EmployeeId)).Distinct().Count();

            // Trends
            double CalcTrend(double current, double prev) => prev == 0 ? 0 : ((current - prev) / prev) * 100;
            
            // 4. Top Employees (by Amount)
            var topEmployees = currentForms
                .SelectMany(f => f.FormDetails)
                .Where(fd => fd.Employee != null)
                .GroupBy(fd => new { fd.Employee.Id, fd.Employee.Name, DepartmentName = fd.Employee.Department != null ? fd.Employee.Department.Name : "غير محدد" }) 
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
                .ToList();

            // 5. Department Stats (Pie Chart)
            var formsByDept = currentForms
                .SelectMany(f => f.FormDetails)
                .GroupBy(fd => fd.Employee?.Department?.Name ?? "غير محدد")
                .Select(g => new PieChartDto
                {
                    Label = g.Key,
                    Value = g.Sum(fd => fd.Amount) 
                })
                .ToList();

            // 6. Recent Forms
            var recentForms = currentForms.OrderByDescending(f => f.CreatedAt).Take(5)
                .Select(f => new FormSummaryDto
                {
                    Id = f.Id,
                    Description = f.Description,
                    Date = f.CreatedAt,
                    EmployeeCount = f.FormDetails.Count,
                    TotalAmount = f.FormDetails.Sum(fd => fd.Amount)
                }).ToList();

            // 7. Chart Data
            // If duration <= 35 days, Group by Day. Else by Month.
            List<ChartDataDto> chartDataDtos;
            if (duration.TotalDays <= 35)
            {
                chartDataDtos = currentForms
                    .GroupBy(f => f.CreatedAt.Date)
                    .Select(g => new ChartDataDto
                    {
                        Label = g.Key.ToString("dd/MM"),
                        FormCount = g.Count(),
                        EmployeeCount = g.SelectMany(f => f.FormDetails.Select(fd => fd.EmployeeId)).Distinct().Count(),
                        TotalAmount = g.Sum(f => f.FormDetails.Sum(fd => fd.Amount))
                    })
                    .OrderBy(x => x.Label)
                    .ToList();
            }
            else
            {
                chartDataDtos = currentForms
                    .GroupBy(f => new { f.CreatedAt.Year, f.CreatedAt.Month })
                    .Select(g => new ChartDataDto
                    {
                        Label = $"{g.Key.Year}-{g.Key.Month}",
                        FormCount = g.Count(),
                        EmployeeCount = g.SelectMany(f => f.FormDetails.Select(fd => fd.EmployeeId)).Distinct().Count(),
                        TotalAmount = g.Sum(f => f.FormDetails.Sum(fd => fd.Amount))
                    })
                    .OrderBy(x => x.Label)
                    .ToList();
            }

            return new DashboardDto
            {
                TotalEmployees = totalEmployees,
                TotalForms = await _formRepository.GetQueryable().CountAsync(), // All time count
                ActiveForms = activeForms, // Count in selected period
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
