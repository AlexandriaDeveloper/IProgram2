
using Application.Dtos;
using Application.Dtos.Requests;
using Application.Features;
using Application.Helpers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Persistence.Helpers;

namespace Api.Controllers
{
    public class EmployeeController : BaseApiController
    {
        private readonly EmployeeService _employeeService;
        public EmployeeController(EmployeeService employeeService)
        {
            this._employeeService = employeeService;
        }
        [HttpGet("GetEmployees")]
        [ResponseCache(CacheProfileName = "Short")]
        public async Task<IActionResult> GetEmployees([FromQuery] EmployeeParam employeeParam)
        {
            return HandleResult(await _employeeService.getEmployees(employeeParam));
        }
        [HttpGet("GetEmployee")]
        public async Task<IActionResult> GetEmployeeBySpec([FromQuery] EmployeeParam employeeParam)
        {
            return HandleResult<EmployeeDto>(await _employeeService.getEmployee(employeeParam));
        }
        [HttpPost("Add")]
        public async Task<IActionResult> AddEmployee(EmployeeDto employee, CancellationToken cancellationToken)
        {
            if (!ModelState.IsValid)
            {
                return HandleResult(Result.ValidationErrors<EmployeeDto>(ModelState.SelectMany(x => x.Value.Errors)));
            }

            return HandleResult<EmployeeDto>(await _employeeService.AddEmployee(employee, cancellationToken));
        }
        [HttpPut()]
        public async Task<IActionResult> PutEmployee(EmployeeDto employee)
        {
            if (!ModelState.IsValid)
            {
                return HandleResult(Result.ValidationErrors<EmployeeDto>(ModelState.SelectMany(x => x.Value.Errors)));
            }
            var result = await _employeeService.UpdateEmployee(employee);

            // Force browser to refetch employee data after update
            if (result.IsSuccess)
            {
                Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
                Response.Headers.Pragma = "no-cache";
                Response.Headers.Expires = "0";
            }

            return HandleResult<EmployeeDto>(result);
        }
        [HttpPost("Upload")]
        [RequestSizeLimit(10 * 1024 * 1024)] // 10 MB
        public async Task<IActionResult> UploadEmployees(EmployeeFileUploadRequest model)
        {
            if (!ModelState.IsValid)
            {
                return HandleResult(Result.ValidationErrors<EmployeeDto>(ModelState.SelectMany(x => x.Value.Errors)));
            }
            try
            {
                var result = await _employeeService.UploadTegaraFile(model);
                return HandleResult(result);
            }
            catch (Exception ex)
            {
                return HandleResult(Result.Failure<EmployeeDto>(new Error("500", ex.Message)));
            }
        }

        [HttpPost("UploadTegaraFile")]
        [RequestSizeLimit(10 * 1024 * 1024)] // 10 MB
        public async Task<IActionResult> UploadTegaraFile(EmployeeFileUploadRequest model)
        {
            if (!ModelState.IsValid)
            {
                return HandleResult(Result.ValidationErrors<EmployeeDto>(ModelState.SelectMany(x => x.Value.Errors)));
            }
            try
            {
                var result = await _employeeService.UploadTegaraFile(model);
                return HandleResult(result);
            }
            catch (Exception ex)
            {
                return HandleResult(Result.Failure<EmployeeDto>(new Error("500", ex.Message)));
            }
        }
        [HttpPost("EmployeeReport")]
        public async Task<IActionResult> EmployeeReport([FromBody] EmployeeReportRequest request)
        {
            return HandleResult<EmployeeReportDto>(await _employeeService.EmployeeReport(request));
        }
        [HttpGet("download-employees/")]
        public async Task<FileResult> DownloadFile([FromQuery] DownloadAllEmployeesParam param = null)
        {
            var ms = await _employeeService.DownloadAllEmployees(param);

            return File(ms, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "employees.xlsx");


        }
        [HttpGet("GetCollages")]
        [AllowAnonymous]
        [ResponseCache(Duration = 1800, Location = ResponseCacheLocation.Any, NoStore = false)] // 30 minutes cache for collages
        public async Task<IActionResult> GetCollages()
        {
            return HandleResult<List<string>>(await _employeeService.GetCollagesName());
        }
        [HttpGet("GetSections")]
        [ResponseCache(Duration = 1800, Location = ResponseCacheLocation.Any, NoStore = false)] // 30 minutes cache for sections
        public async Task<IActionResult> GetSections()
        {
            return HandleResult<List<string>>(await _employeeService.GetSectionsName());
        }

        [HttpDelete("SoftDelete/{id}")]
        public async Task<IActionResult> SoftDelete(string id)
        {
            var result = await _employeeService.SoftDelete(id);
            return HandleResult(result);// result;
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(string id)
        {
            var result = await _employeeService.Delete(id);
            return HandleResult(result);// result;
        }
    }

}
