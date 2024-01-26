using System.ComponentModel.DataAnnotations.Schema;

namespace Core.Models
{
    public class FormDetails : Entity
    {
        [NotMapped]
        public override string Name { get; set; }
        public int FormId { get; set; }
        public int EmployeeId { get; set; }
        public double Amount { get; set; }
        public int OrderNum { get; set; }
        public Form Form { get; set; }
        public Employee Employee { get; set; }
    }
}