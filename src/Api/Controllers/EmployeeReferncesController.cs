using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Application.Dtos;
using Application.Dtos.Requests;
using Application.Features;
using Application.Helpers;
using Application.Shared;
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
        public async Task<Result<List<EmployeeRefernceDto>>> GetEmployeeRefernces(int employeeId)
        {
            return await _employeeRefernceService.GetEmployeeRefernces(employeeId);
        }
        [HttpDelete("DeleteEmployeeReference/{id}")]
        public async Task<Application.Helpers.Result> DeleteEmployeeReference(int id)
        {
            return await _employeeRefernceService.DeleteEmployeeReference(id);
        }
        [HttpPost("UploadRefernce")]
        public async Task<Application.Helpers.Result> UploadRefernce(EmployeeRefernceFileUploadRequest request)
        {
            var random = Random.Shared.Next(1000, 5000);
            Task.Delay(random).Wait();
            return await _employeeRefernceService.UploadRefernce(request);
        }

    }
}