using System;

namespace Application.Dtos
{
    public class WatchListDto
    {
        public int Id { get; set; }
        public string EmployeeId { get; set; }
        public string EmployeeName { get; set; }
        public string Reason { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public string CreatedBy { get; set; }
        public DateTime CreatedAt { get; set; }
        public bool IsActive { get; set; }
        public int? TabCode { get; set; }
        public int? TegaraCode { get; set; }
    }

    public class WatchListAlertDto
    {
        public string Reason { get; set; }
    }

    public class AddToWatchListRequest
    {
        public string NationalId { get; set; }
        public int? TabCode { get; set; }
        public int? TegaraCode { get; set; }
        public string SearchType { get; set; } // "nationalId", "tabCode", "tegaraCode"
        public string Reason { get; set; }
        public DateTime? ExpiresAt { get; set; }
    }
}
