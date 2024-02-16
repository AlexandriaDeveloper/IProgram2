
using Application.Dtos;
using Application.Dtos.Requests;
using Application.Features;
using Application.Helpers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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

            return HandleResult<EmployeeDto>(await _employeeService.UpdateEmployee(employee));




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
                var result = await _employeeService.UploadTabFile(model.File);

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
            // return null;
        }

        [HttpDelete("SoftDelete/{id}")]
        public async Task<IActionResult> SoftDelete(int id)
        {
            var result = await _employeeService.SoftDelete(id);
            return HandleResult(result);// result;
        }

    }

}
