

using System.ComponentModel.DataAnnotations;

namespace Core.Models
{
    public class Employee : Entity
    {
        // public int Id { get; set; }
        public int? TegaraCode { get; set; }
        public int? TabCode { get; set; }
        [Required]
        public new string Name { get; set; }

        [MaxLength(14, ErrorMessage = "الرقم القومى لا يمكن ان يزيد عن 14 رقم")]
        [MinLength(14, ErrorMessage = "الرقم القومى لا يمكن ان يقل عن 14 رقم")]
        [Required]
        public string NationalId { get; set; }
        public string Collage { get; set; }
        public int? DepartmentId { get; set; }

        public Department Department { get; set; }
        public EmployeeBank EmployeeBank { get; set; }
        public ICollection<FormDetails> FormDetails { get; set; }

    }


}