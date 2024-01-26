
namespace Application.Dtos
{
    public class EmployeeReportDto
    {

        public int? TabCode { get; set; }
        public int? TegaraCode { get; set; }
        public string NationalId { get; set; }
        public string Name { get; set; }
        public List<EmployeeDailyDto> Dailies { get; set; } = new List<EmployeeDailyDto>();
        public double GrandTotal => Dailies.Sum(x => x.TotalAmount);
    }
    public class EmployeeFormDto
    {
        public int FormId { get; set; }
        public string FormName { get; set; }
        public double Amount { get; set; }
    }

    public class EmployeeDailyDto
    {
        public List<EmployeeFormDto> Forms { get; set; }
        public int DailyId { get; set; }
        public string DailyName { get; set; }
        public string State { get; set; }
        public DateTime DailyDate { get; set; }
        public double TotalAmount => Forms.Sum(x => x.Amount);
    }
}