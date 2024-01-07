
using System.Web;
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

    public class FormController : BaseApiController
    {
        private readonly FormService _formService;
        private readonly FormDetailsService _formDetailsService;

        public FormController(FormService formService, FormDetailsService formDetailsService)
        {
            this._formService = formService;
            this._formDetailsService = formDetailsService;
        }

        [HttpPost()]

        public async Task<Result<FormDto>> AddForm([FromBody] FormDto form)
        {
            var result = await _formService.AddForm(form);
            return result;
        }
        [HttpPut("{id}")]
        public async Task<Result> UpdateForm(int id, [FromBody] FormDto form)
        {
            var result = await _formService.UpdateForm(id, form);
            return result;
        }

        [HttpGet("{dailyId}")]

        public async Task<Result<PaginatedResult<FormDto>>> GetForms(int dailyId, [FromQuery] FormParam param)
        {
            var result = await _formService.GetForms(dailyId, param);
            return result;
        }

        [HttpGet("formDetails/{id}")]

        public async Task<Result<FormDto>> GetFormByIdWithDetails(int id)
        {
            var result = await _formDetailsService.GetFormDetails(id);
            return result;
        }





        [HttpPut("UpdateDescription/{id}")]
        public async Task<Result> UpdateDescription(int id, [FromBody] UpdateFormDescriptonRequest request)
        {

            string decodedString = HttpUtility.HtmlDecode(request.Description);
            // Clean HTML
            // string sanitizedHtmlText = HtmlUtility.SanitizeHtml(decodedString);

            // string encoded = HttpUtility.HtmlEncode(sanitizedHtmlText);

            request.Description = decodedString;

            var result = await _formService.UpdateDescription(id, request);
            return result;
        }
        [HttpDelete("{id}")]
        public async Task<Result> SoftDelete(int id)
        {
            var result = await _formService.SoftDelete(id);
            return result;
        }

    }
}