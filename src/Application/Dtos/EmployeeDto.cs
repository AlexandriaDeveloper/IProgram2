using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;

namespace Application.Dtos
{
    public class EmployeeDto
    {
        public string Id { get; set; }
        public int? TegaraCode { get; set; }
        public int? TabCode { get; set; }
        public string Name { get; set; }
        public string NationalId => Id;
        public string Collage { get; set; }
        [MaxLength(25)]
        public string Section { get; set; }
        [MaxLength(250)]
        [EmailAddress()]

        public string Email { get; set; }
        public int? DepartmentId { get; set; }
        public string DepartmentName { get; set; }
        public bool HasReferences { get; set; }
        public EmployeeBankDto BankInfo { get; set; } = new EmployeeBankDto();

        public ICollection<EmployeeRefernceDto> EmployeeRefernces { get; set; }

    }

    public class EmployeeBankDto
    {
        public string BankName { get; set; }
        public string BranchName { get; set; }
        public string AccountNumber { get; set; }
        public string? EmployeeId { get; set; }


    }

    public class EmployeeRefernceDto
    {
        public int? Id { get; set; }
        public string EmployeeId { get; set; }
        public string ReferencePath { get; set; }

    }
}