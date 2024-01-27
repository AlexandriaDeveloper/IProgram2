

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Core.Models
{
    public class EmployeeBank : Entity
    {
        [NotMapped]
        override public int Id { get; set; }
        [NotMapped]
        override public string Name { get; set; }
        [Key, ForeignKey("Employee")]
        public int EmployeeId { get; set; }
        public string BankName { get; set; }
        public string BranchName { get; set; }
        public string AccountNumber { get; set; }
        public Employee Employee { get; set; }
    }


}