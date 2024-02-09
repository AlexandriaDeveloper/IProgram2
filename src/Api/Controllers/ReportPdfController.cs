
using Application.Dtos;
using Application.Dtos.Requests;
using Application.Features;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers
{
    public class ReportPdfController : BaseApiController
    {
        private readonly ReportService _reportService;
        private readonly EmployeeService _employeeService;

        public ReportPdfController(ReportService reportService, EmployeeService employeeService)
        {
            this._reportService = reportService;
            this._employeeService = employeeService;
        }
        // [HttpPost("PrintFormPdf")]
        // [AllowAnonymous]
        // public async Task<ActionResult> PrintFormPdf([FromBody] ReportFormPdfRequest request)
        // {

        //     var pdf = await _reportService.PrintFormPdf(request);

        //     var path = Path.GetTempPath() + "test.pdf";


        //     var memory = new MemoryStream(pdf);
        //     await using (var stream = new FileStream(path, FileMode.Create))
        //     {
        //         await stream.CopyToAsync(memory);
        //     }
        //     memory.Position = 0;
        //     return File(memory, "application/pdf", "test.pdf");
        // }


        [HttpGet("PrintFormWithDetailsPdf/{formId}")]
        public async Task<ActionResult> PrintFormPdf(int formId)
        {

            var pdf = await _reportService.PrintFormWithDetailsPdf(formId);

            var path = Path.GetTempPath() + "test.pdf";


            var memory = new MemoryStream(pdf);
            await using (var stream = new FileStream(path, FileMode.Create))
            {
                await stream.CopyToAsync(memory);
            }
            memory.Position = 0;
            return File(memory, "application/pdf", "test.pdf");
        }

        [HttpGet("PrintEmployeeReportDetailsPdf")]
        public async Task<ActionResult> PrintEmployeeReportPdf([FromQuery] EmployeeReportRequest request)
        {

            var reportToPrint = await _employeeService.EmployeeReport(request);
            if (reportToPrint.IsFailure)
            {
                return BadRequest(reportToPrint.Error);
            }

            var pdf = await _reportService.PrintEmployeeReportDetailsPdf(reportToPrint.Value);

            var path = Path.GetTempPath() + "test.pdf";


            var memory = new MemoryStream(pdf);
            await using (var stream = new FileStream(path, FileMode.Create))
            {
                await stream.CopyToAsync(memory);
            }
            memory.Position = 0;
            return File(memory, "application/pdf", "test.pdf");
        }
    }
}