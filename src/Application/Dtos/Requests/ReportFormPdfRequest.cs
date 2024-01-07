using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Application.Dtos.Requests
{
    public class ReportFormPdfRequest
    {
        public string Description { get; set; }

        // public List<FormDetailsDto> FormDetails { get; set; }
        // public class FormDetailsDto
        // {
        //     public int EmployeeId { get; set; }
        //     public decimal Amount { get; set; }
        // }
    }
}