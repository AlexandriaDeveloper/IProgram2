

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Core.Models
{
    public class Employee : Entity
    {

        [MaxLength(14, ErrorMessage = "الرقم القومى لا يمكن ان يزيد عن 14 رقم")]
        [MinLength(14, ErrorMessage = "الرقم القومى لا يمكن ان يقل عن 14 رقم")]
        [StringLength(14)]
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.None)]
        public new string Id { get; set; }
        public int? TegaraCode { get; set; }
        public int? TabCode { get; set; }
        [Required]
        public new string Name { get; set; }

        // [MaxLength(14, ErrorMessage = "الرقم القومى لا يمكن ان يزيد عن 14 رقم")]
        // [MinLength(14, ErrorMessage = "الرقم القومى لا يمكن ان يقل عن 14 رقم")]
        // [Required]
        // public string NationalId { get; set; }
        public string Collage { get; set; }
        [MaxLength(25)]
        public string Section { get; set; }
        [MaxLength(250)]
        [EmailAddress]
        public string Email { get; set; }
        public int? DepartmentId { get; set; }

        public Department Department { get; set; }
        public EmployeeBank? EmployeeBank { get; set; } = null;
        public ICollection<FormDetails> FormDetails { get; set; }
        public ICollection<EmployeeRefernce> EmployeeRefernces { get; set; }

    }


}