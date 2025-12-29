namespace Application.DTOs
{
    public class DashboardDto
    {
        public int TotalEmployees { get; set; }
        public int TotalForms { get; set; }
        public int ActiveForms { get; set; }
        public double TotalAmount { get; set; } // Total amount for the period

        // Trends (Percentage Change vs Previous Period)
        public double TotalAmountChange { get; set; }
        public double EmployeeCountChange { get; set; }
        public double FormCountChange { get; set; }

        public List<FormSummaryDto> RecentForms { get; set; }
        public List<ChartDataDto> ChartData { get; set; }
        public List<EmployeeSummaryDto> TopEmployees { get; set; }
        public List<PieChartDto> FormsByDepartment { get; set; }
    }

    public class EmployeeSummaryDto
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Department { get; set; }
        public int FormCount { get; set; }
        public double TotalAmount { get; set; }
    }

    public class PieChartDto
    {
        public string Label { get; set; }
        public double Value { get; set; }
    }

    public class FormSummaryDto
    {
        public int Id { get; set; }
        public string Description { get; set; }
        public DateTime Date { get; set; }
        public int EmployeeCount { get; set; }
        public double TotalAmount { get; set; }
    }

    public class ChartDataDto
    {
        public string Label { get; set; } // e.g., "Jan 2024"
        public int FormCount { get; set; }
        public int EmployeeCount { get; set; }
        public double TotalAmount { get; set; }
    }
}
