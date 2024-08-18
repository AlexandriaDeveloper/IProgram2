using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Org.BouncyCastle.Crypto;

namespace Application.Dtos
{
    public class DepartmentDto
    {
        public int Id { get; set; }
        [Required]
        [MaxLength(200)]
        public string Name { get; set; }
        public int EmployeesCount { get; set; }
    }

    public class EmployeesDepartmentFileUploadRequest
    {
        public int DepartmentId { get; set; }
        public IFormFile File { get; set; }
    }

    public class EmployeesInDepartmentIdsRequest
    {
        public string[] Ids { get; set; }
    }
}