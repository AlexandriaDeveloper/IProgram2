using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Core.Models
{
    public class EmployeeNetPay : Entity
    {
        public int DailyId { get; set; }
        public Daily Daily { get; set; }

        [StringLength(14)]
        public string EmployeeId { get; set; }
        public Employee Employee { get; set; }

        public double NetPay { get; set; }

        public int? DailyReferenceId { get; set; }
        public DailyReference DailyReference { get; set; }
    }
}
