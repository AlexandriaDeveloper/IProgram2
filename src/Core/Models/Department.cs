

using System.ComponentModel.DataAnnotations;

namespace Core.Models
{
    public class Department : Entity
    {
        [Required]
        [MaxLength(200)]
        override public string Name { get; set; }
        public ICollection<Employee> Employees { get; set; }
    }

}