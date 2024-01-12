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
    public class FormDetailsController : BaseApiController
    {
        private readonly FormDetailsService _formDetailsService;

        public FormDetailsController(FormDetailsService formDetailsService)
        {
            this._formDetailsService = formDetailsService;
        }

        [HttpPost("AddEmployeeToFormDetails")]
        public async Task<Result> AddEmployeeToFormDetails(FormDetailsRequest form)
        {
            return await _formDetailsService.AddEmployeeToFormDetails(form);
        }
        [HttpPut("EditEmployeeToFormDetails")]
        public async Task<Result> EditEmployeeToFormDetails(FormDetailsRequest form)
        {
            return await _formDetailsService.EditEmployeeToFormDetails(form);
        }

        [HttpPut("reOrderRows/{id}")]
        public async Task<Result> ReOrderRows(int id, [FromBody] int[] rows)
        {
            return await _formDetailsService.ReOrderRows(id, rows);
        }
        [HttpDelete("{id}")]
        public async Task<Result> DeleteEmployeeFromFormDetails(int id)
        {
            return await _formDetailsService.DeleteEmployeeFromFormDetails(id);
        }


    }
}