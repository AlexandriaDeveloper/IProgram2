using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Core.Models
{
    public class FormDetails : Entity
    {
        [NotMapped]
        public override string Name { get; set; }
        public int FormId { get; set; }
        [StringLength(14)]
        public string EmployeeId { get; set; }
        public double Amount { get; set; }
        public int OrderNum { get; set; }
        public bool IsReviewed { get; set; }
        [MaxLength(100)]
        public string IsReviewedBy { get; set; }
        public DateTime? ReviewedAt { get; set; }
        public string ReviewComments { get; set; }



        public Form Form { get; set; }
        public Employee Employee { get; set; }
    }
}