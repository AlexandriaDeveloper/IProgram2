
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

        [HttpGet("file/{id}")]
        public async Task<IActionResult> GetFile(int id)
        {
            var result = await _employeeRefernceService.GetReferenceFile(id);
            if (result.IsFailure)
            {
                return HandleResult(result);
            }

            var (stream, contentType, fileName) = result.Value;
            return File(stream, contentType, fileName, enableRangeProcessing: true);
        }

        [HttpDelete("DeleteEmployeeReference/{id}")]
        public async Task<IActionResult> DeleteEmployeeReference(int id)
        {
            return HandleResult(await _employeeRefernceService.DeleteEmployeeReference(id));
        }

        [HttpPost("UploadRefernce")]
        [RequestSizeLimit(FileSecurityValidator.MaxEmployeeUploadBytes)]
        public async Task<IActionResult> UploadRefernce([FromForm] EmployeeRefernceFileUploadRequest request)
        {
            if (!ModelState.IsValid)
            {
                return HandleResult(Result.ValidationErrors<EmployeeRefernceFileUploadRequest>(ModelState.SelectMany(x => x.Value.Errors)));
            }

            var validation = FileSecurityValidator.ValidateFile(
                request?.File,
                FileSecurityValidator.MaxEmployeeUploadBytes,
                new[] { ".pdf", ".jpg", ".jpeg", ".png" });

            if (!validation.IsSuccess)
            {
                return HandleResult(validation);
            }

            return HandleResult(await _employeeRefernceService.UploadRefernce(request));
        }

    }
}