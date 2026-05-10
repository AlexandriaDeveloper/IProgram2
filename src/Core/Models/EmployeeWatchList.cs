using System.ComponentModel.DataAnnotations;

namespace Core.Models
{
    public class EmployeeWatchList : Entity
    {
        [StringLength(14)]
        public string EmployeeId { get; set; }
        
        [MaxLength(500)]
        public string Reason { get; set; }     // سبب الانتباه التفصيلي (نص حر)
        
        public DateTime? ExpiresAt { get; set; } // تاريخ انتهاء التنبيه (اختياري)
        
        public Employee Employee { get; set; }
    }
}
