using Application.Dtos.Requests;
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
        public async Task<IActionResult> AddEmployeeToFormDetails(FormDetailsRequest form)
        {
            if (!ModelState.IsValid)
            {
                return HandleResult(Result.ValidationErrors<FormDetailsRequest>(ModelState.SelectMany(x => x.Value.Errors)));
            }
            return HandleResult(await _formDetailsService.AddEmployeeToFormDetails(form));
        }
        [HttpPut("EditEmployeeToFormDetails")]
        public async Task<IActionResult> EditEmployeeToFormDetails(FormDetailsRequest form)
        {
            if (!ModelState.IsValid)
            {
                return HandleResult(Result.ValidationErrors<FormDetailsRequest>(ModelState.SelectMany(x => x.Value.Errors)));
            }
            return HandleResult(await _formDetailsService.EditEmployeeToFormDetails(form));
        }

        [HttpPut("reOrderRows/{id}")]
        public async Task<IActionResult> ReOrderRows(int id, [FromBody] int[] rows)
        {
            if (!ModelState.IsValid)
            {
                return HandleResult(Result.ValidationErrors<FormDetailsRequest>(ModelState.SelectMany(x => x.Value.Errors)));
            }
            return HandleResult(await _formDetailsService.ReOrderRows(id, rows));
        }
        [HttpPut("MarkAsReviewed/{id}")]
        public async Task<IActionResult> MarkAsReviewed(int id, [FromBody] bool isReviewed)
        {
            // if (!ModelState.IsValid)
            // {
            //     return HandleResult(Result.ValidationErrors<FormDetailsRequest>(ModelState.SelectMany(x => x.Value.Errors)));
            // }
            return HandleResult(await _formDetailsService.MarkFormDetailsAsReviewed(id, isReviewed));
        }
        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteEmployeeFromFormDetails(int id)
        {
            return HandleResult(await _formDetailsService.DeleteEmployeeFromFormDetails(id));// await _formDetailsService.DeleteEmployeeFromFormDetails(id);
        }


    }
}