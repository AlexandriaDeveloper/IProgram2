
using Application.Dtos;
using Application.Dtos.Requests;
using Application.Features;
using Application.Helpers;
using Application.Shared;
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

        public async Task<Result<PaginatedResult<EmployeeDto>>> GetEmployees([FromQuery] EmployeeParam employeeParam)
        {
            return await _employeeService.getEmployees(employeeParam);

        }
        [HttpGet("GetEmployee")]

        public async Task<Result> GetEmployeeBySpec([FromQuery] EmployeeParam employeeParam)
        {
            return await _employeeService.getEmployee(employeeParam); ;

        }

        [HttpPost("Add")]
        public async Task<ActionResult<Result<EmployeeDto>>> AddEmployee(EmployeeDto employee, CancellationToken cancellationToken)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState.FirstOrDefault().Value);
            }
            try
            {
                return await _employeeService.AddEmployee(employee, cancellationToken);
            }
            catch (Exception ex)
            {
                return Result.Failure<EmployeeDto>(new Error("500", ex.Message));
            }



        }


        [HttpPost("Upload")] // 10 MB

        public async Task<ActionResult<Result>> UploadEmployees([FromForm] UploadEmployeesRequest model)
        {

            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState.FirstOrDefault().Value);
            }
            try
            {
                foreach (var file in model.Files)
                {
                    await _employeeService.UploadEmployees(file);
                }
                return Result.Success("تم الرفع بنجاح");
            }
            catch (Exception ex)
            {
                return Result.Failure<EmployeeDto>(new Error("500", ex.Message));
            }

        }

        [HttpGet("Test")]
        [AllowAnonymous]
        public async Task<Result<string>> ConvetNumToStrin(decimal num)
        {

            return await _employeeService.ConvertNumber(num);

        }

    }
}