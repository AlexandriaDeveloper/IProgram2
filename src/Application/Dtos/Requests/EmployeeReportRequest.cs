namespace Application.Dtos.Requests
{
    public class EmployeeReportRequest
    {
        public int Id { get; set; }
        public DateTime? StartDate { get; set; } = DateTime.Now.AddYears(-5);
        public DateTime? EndDate { get; set; } = DateTime.Now;
    }

}