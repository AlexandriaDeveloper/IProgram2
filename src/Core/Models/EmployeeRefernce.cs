using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Threading.Tasks;

namespace Core.Models
{
    public class EmployeeRefernce : Entity
    {
        [NotMapped]
        public override string Name { get => base.Name; set => base.Name = value; }
        public string EmployeeId { get; set; }
        public string ReferencePath { get; set; }

        public Employee Employee { get; set; }
    }
}