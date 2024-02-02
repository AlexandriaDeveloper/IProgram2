using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Application.Dtos;
using Application.Features;
using Application.Helpers;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers
{

    public class EmployeeBankController : BaseApiController
    {
        private readonly EmployeeBankService _employeeBankService;
        public EmployeeBankController(EmployeeBankService employeeBankService)
        {
            this._employeeBankService = employeeBankService;

        }

        [HttpDelete("{id}")]
        public async Task<Result> DeleteEmployeeBank(int id)
        {
            if (!ModelState.IsValid)
            {
                return Result.Failure(new Error("400", "Bad request"));
            }
            try
            {
                return await this._employeeBankService.DeleteEmployeeBank(id);
            }
            catch (Exception ex)
            {
                return Result.Failure(new Error("400", ex.Message));
            }
        }
        [HttpPost]
        public async Task<Result> AddEmployeeBank(EmployeeBankDto employeeBank)
        {
            if (!ModelState.IsValid)
            {
                return Result.Failure(new Error("400", "Bad request"));
            }
            try
            {
                return await this._employeeBankService.AddEmployeeBank(employeeBank);
            }
            catch (Exception ex)
            {
                return Result.Failure(new Error("400", ex.Message));
            }
        }

        [HttpGet("{id}")]
        public async Task<Result> GetEmployeeBankById(int id)
        {
            if (!ModelState.IsValid)
            {
                return Result.Failure(new Error("400", "Bad request"));
            }
            try
            {
                return await this._employeeBankService.GetByEmployeeId(id);
            }
            catch (Exception ex)
            {
                return Result.Failure(new Error("400", ex.Message));
            }
        }

        [HttpGet("ByEmployeeId/{id}")]
        public async Task<Result> GetEmployeeBankByEmployeeId(int id)
        {
            if (!ModelState.IsValid)
            {
                return Result.Failure(new Error("400", "Bad request"));
            }
            try
            {
                return await this._employeeBankService.GetByEmployeeId(id);
            }
            catch (Exception ex)
            {
                return Result.Failure(new Error("400", ex.Message));
            }
        }


    }
}