using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Application.Dtos;
using Application.Features;
using Application.Helpers;
using Application.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Persistence.Helpers;

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
        public async Task<Result<PaginatedResult<DepartmentDto>>> GetDepartments([FromQuery] DepartmentParam departmentParam)
        {
            return await _departmentService.getDepartments(departmentParam);
        }

        [HttpGet("{id}")]
        public async Task<Result<DepartmentDto>> GetDepartment(int id)
        {
            return await _departmentService.getDepartment(id);
        }
        [HttpPost]
        public async Task<Result<DepartmentDto>> AddDepartment(DepartmentDto departmentDto)
        {
            if (!ModelState.IsValid)
            {
                return Result.Failure<DepartmentDto>(new Error("500", "Validation Error"));
            }
            return await _departmentService.AddDepartment(departmentDto);
        }
        [HttpPut]
        public async Task<Result<DepartmentDto>> EditDepartment(DepartmentDto departmentDto)
        {
            if (!ModelState.IsValid)
            {
                return Result.Failure<DepartmentDto>(new Error("500", "Validation Error"));
            }
            return await _departmentService.EditDepartment(departmentDto);
        }
        [HttpPut("{id}/employees")]
        public async Task<Result> AddEmployees(int id, [FromBody] int[] employeeIds)
        {
            return await _departmentService.UpdateEmployeesDepartment(id, employeeIds);
        }
        [HttpPut("removeEmployees")]
        public async Task<Result> RemoveEmployees([FromBody] int[] employeeIds)
        {
            return await _departmentService.UpdateEmployeesDepartment(null, employeeIds);
        }
        [HttpDelete("{id}")]
        public async Task<Result> DeleteDepartment(int id)
        {
            return await _departmentService.DeleteDepartment(id);
        }

    }
}