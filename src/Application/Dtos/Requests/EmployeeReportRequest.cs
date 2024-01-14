namespace Application.Dtos.Requests
{
    public class EmployeeReportRequest
    {
        public int Id { get; set; }
        public DateTime? StartDate { get; set; }
        public DateTime? EndDate { get; set; }
    }

}