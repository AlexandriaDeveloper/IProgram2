
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
        public async Task<IActionResult> DeleteEmployeeBank(int id)
        {
            if (!ModelState.IsValid)
            {
                return HandleResult(Result.ValidationErrors<EmployeeBankDto>(ModelState.SelectMany(x => x.Value.Errors)));
            }

            return HandleResult(await this._employeeBankService.DeleteEmployeeBank(id));


        }
        [HttpPost]
        public async Task<IActionResult> AddEmployeeBank(EmployeeBankDto employeeBank)
        {
            if (!ModelState.IsValid)
            {
                return HandleResult(Result.ValidationErrors<EmployeeBankDto>(ModelState.SelectMany(x => x.Value.Errors)));
            }


            return HandleResult(await this._employeeBankService.AddEmployeeBank(employeeBank));

        }

        [HttpGet("{id}")]
        public async Task<IActionResult> GetEmployeeBankById(int id)
        {
            if (!ModelState.IsValid)
            {
                return HandleResult(Result.ValidationErrors<EmployeeBankDto>(ModelState.SelectMany(x => x.Value.Errors)));
            }

            return HandleResult(await this._employeeBankService.GetByEmployeeId(id));

        }

        [HttpGet("ByEmployeeId/{id}")]
        public async Task<IActionResult> GetEmployeeBankByEmployeeId(int id)
        {
            if (!ModelState.IsValid)
            {
                return HandleResult(Result.ValidationErrors<EmployeeBankDto>(ModelState.SelectMany(x => x.Value.Errors)));
            }

            return HandleResult(await this._employeeBankService.GetByEmployeeId(id));

        }


    }
}