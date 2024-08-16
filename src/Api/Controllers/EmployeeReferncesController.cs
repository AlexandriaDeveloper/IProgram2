
using Application.Dtos;
using Application.Dtos.Requests;
using Application.Features;
using Application.Helpers;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers
{
    public class EmployeeReferncesController : BaseApiController
    {
        private readonly EmployeeRefernceService _employeeRefernceService;

        public EmployeeReferncesController(EmployeeRefernceService employeeRefernceService)
        {
            this._employeeRefernceService = employeeRefernceService;

        }

        [HttpGet("GetEmployeeRefernces/{employeeId}")]
        public async Task<IActionResult> GetEmployeeRefernces(string employeeId)
        {
            return HandleResult<List<EmployeeRefernceDto>>(await _employeeRefernceService.GetEmployeeRefernces(employeeId));
        }
        [HttpDelete("DeleteEmployeeReference/{id}")]
        public async Task<IActionResult> DeleteEmployeeReference(int id)
        {

            return HandleResult(await _employeeRefernceService.DeleteEmployeeReference(id));
        }
        [HttpPost("UploadRefernce")]
        public async Task<IActionResult> UploadRefernce(EmployeeRefernceFileUploadRequest request)
        {
            if (!ModelState.IsValid)
            {
                return HandleResult(Result.ValidationErrors<EmployeeRefernceFileUploadRequest>(ModelState.SelectMany(x => x.Value.Errors)));
            }

            return HandleResult(await _employeeRefernceService.UploadRefernce(request));
        }

    }
}