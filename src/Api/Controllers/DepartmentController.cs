using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Application.Dtos;
using Application.Features;
using Application.Helpers;

//using FluentResults;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Persistence.Helpers;
using Microsoft.Extensions.Caching.Memory;


namespace Api.Controllers
{
    public class DepartmentController : BaseApiController
    {
        private readonly DepartmentService _departmentService;
        public DepartmentController(DepartmentService departmentService)
        {
            this._departmentService = departmentService;

        }

        [HttpGet()]
        // [ResponseCache(CacheProfileName = "Short")] // 1 minute cache for paginated results
        public async Task<IActionResult> GetDepartments([FromQuery] DepartmentParam departmentParam)
        {
            return HandleResult<PaginatedResult<DepartmentDto>>(await _departmentService.getDepartments(departmentParam));
        }
        [HttpGet("GetAllDepartments")]
        //[ResponseCache(Duration = 1800)] // 30 minutes cache - departments change rarely
        public async Task<IActionResult> GetAllDepartments()
        {
            return HandleResult<List<DepartmentDto>>(await _departmentService.getAllDepartments());
        }
        [HttpPost]
        public async Task<IActionResult> AddDepartment(DepartmentDto departmentDto)
        {
            if (!ModelState.IsValid)
            {
                return HandleResult<DepartmentDto>(Result.ValidationErrors<DepartmentDto>(ModelState.SelectMany(x => x.Value.Errors)));
            }
            return HandleResult<DepartmentDto>(await _departmentService.AddDepartment(departmentDto));// await _departmentService.AddDepartment(departmentDto);
        }

        [HttpPost("upload-employees-department")]
        public async Task<IActionResult> UploadDepartment(EmployeesDepartmentFileUploadRequest departmentDto)
        {
            if (!ModelState.IsValid)
            {
                return HandleResult(Result.ValidationErrors<DepartmentDto>(ModelState.SelectMany(x => x.Value.Errors)));
            }
            var result = await _departmentService.UploadEmployeesDepartment(departmentDto);
            return HandleResult(result);
        }
        [HttpPut]
        public async Task<IActionResult> EditDepartment(DepartmentDto departmentDto)
        {
            if (!ModelState.IsValid)
            {
                return HandleResult(Result.ValidationErrors<DepartmentDto>(ModelState.SelectMany(x => x.Value.Errors)));
            }
            return HandleResult(await _departmentService.EditDepartment(departmentDto));// await _departmentService.EditDepartment(departmentDto);
        }
        [HttpPut("{id}/employees")]
        public async Task<IActionResult> AddEmployees(int id, [FromBody] EmployeesInDepartmentIdsRequest employeeIds)
        {
            return HandleResult(await _departmentService.UpdateEmployeesDepartment(id, employeeIds));// await _departmentService.UpdateEmployeesDepartment(id, employeeIds);
        }
        [HttpPut("removeEmployees")]
        public async Task<IActionResult> RemoveEmployees([FromBody] EmployeesInDepartmentIdsRequest employeeIds)
        {
            return HandleResult(await _departmentService.UpdateEmployeesDepartment(null, employeeIds));//  await  _departmentService.UpdateEmployeesDepartment(null, employeeIds);
        }
        [HttpPut("removeEmployeesByDepartment/{departmentId}")]
        public async Task<IActionResult> RemoveEmployees(int departmentId)
        {
            return HandleResult(await _departmentService.RemoveAllEmployeesFromDepartment(departmentId));//  await  _departmentService.UpdateEmployeesDepartment(null, employeeIds);
        }
        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteDepartment(int id)
        {
            return HandleResult(await _departmentService.DeleteDepartment(id));// await _departmentService.DeleteDepartment(id);
        }



    }
}
