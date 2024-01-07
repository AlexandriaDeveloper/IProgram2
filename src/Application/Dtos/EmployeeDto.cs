using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Application.Dtos
{
    public class EmployeeDto
    {
        public int Id { get; set; }
        public int? TegaraCode { get; set; }
        public int? TabCode { get; set; }
        public string Name { get; set; }
        public string NationalId { get; set; }
        public string Collage { get; set; }
        public int? DepartmentId { get; set; }

    }
}