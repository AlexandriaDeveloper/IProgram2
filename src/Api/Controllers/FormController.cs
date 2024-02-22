
using System.Web;
using Application.Dtos;
using Application.Dtos.Requests;
using Application.Features;
using Application.Helpers;

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

        public async Task<IActionResult> AddForm([FromBody] FormDto form)
        {
            if (!ModelState.IsValid)
            {
                return HandleResult(Result.ValidationErrors<FormDto>(ModelState.SelectMany(x => x.Value.Errors)));
            }
            var result = await _formService.AddForm(form);
            return HandleResult(result);
        }
        [HttpPut("{id}")]
        public async Task<IActionResult> UpdateForm(int id, [FromBody] FormDto form)
        {
            if (!ModelState.IsValid)
            {
                return HandleResult(Result.ValidationErrors<FormDto>(ModelState.SelectMany(x => x.Value.Errors)));
            }
            var result = await _formService.UpdateForm(id, form);
            return HandleResult(result);
        }
        [HttpPut("MoveFormDailyArchives")]
        public async Task<IActionResult
        > MoveFormDailyToArchives([FromBody] MoveFormRequest request)
        {
            return HandleResult(await _formService.MoveFormDailyToArchive(request));// await _formService.MoveFormDailyToArchive(request);
        }

        [HttpGet("{dailyId}")]

        public async Task<IActionResult> GetForms(int dailyId, [FromQuery] FormParam param)
        {
            var result = await _formService.GetForms(dailyId, param);
            return HandleResult<PaginatedResult<FormDto>>(result);// result;
        }
        // [HttpGet("getArchivedForms")]

        // public async Task<Result<PaginatedResult<FormDto>>> getArchivedForms([FromQuery] FormParam param)
        // {
        //     var result = await _formService.GetArchivedForms(param);
        //     return result;
        // }


        [HttpGet("formDetails/{id}")]

        public async Task<IActionResult> GetFormByIdWithDetails(int id)
        {
            var result = await _formDetailsService.GetFormDetails(id);
            return HandleResult<FormDto>(result);// result;
        }


        [HttpGet("CopyFormToArchive/{id}")]

        public async Task<IActionResult> CopyFormToArchive(int id)
        {
            var result = await _formDetailsService.CopyFormToArchive(id);
            return HandleResult(result);// result;
        }



        [HttpPut("UpdateDescription/{id}")]
        public async Task<IActionResult> UpdateDescription(int id, [FromBody] UpdateFormDescriptonRequest request)
        {

            if (!ModelState.IsValid)
            {
                return HandleResult(Result.ValidationErrors<UpdateFormDescriptonRequest>(ModelState.SelectMany(x => x.Value.Errors)));
            }

            string decodedString = HttpUtility.HtmlDecode(request.Description);
            // Clean HTML
            // string sanitizedHtmlText = HtmlUtility.SanitizeHtml(decodedString);

            // string encoded = HttpUtility.HtmlEncode(sanitizedHtmlText);

            request.Description = decodedString;

            var result = await _formService.UpdateDescription(id, request);
            return HandleResult(result);
        }
        [HttpDelete("SoftDelete/{id}")]
        public async Task<IActionResult> SoftDelete(int id)
        {
            var result = await _formService.SoftDelete(id);
            return HandleResult(result);// result;
        }
        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(int id)
        {
            var result = await _formService.Delete(id);
            return HandleResult(result);// result;
        }

        [HttpPost("download-form")]

        public async Task<FileResult> DownloadFile([FromBody] DownloadFormRequest formRequest)
        {
            var ms = await _formService.CreateExcelFile(formRequest.FormId, formRequest.FormTitle);

            return File(ms, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", formRequest.FormTitle + ".xlsx");


        }



        [HttpPost("upload-excel-form")]
        public async Task<IActionResult> UploadDepartment(UploadEmployeesToFormRequest model)
        {
            Result result = null;
            // if (!ModelState.IsValid)
            // {
            //     result = Result.Failure<DepartmentDto>(new Error("500", "Validation Error"));
            // }
            result = await _formService.UploadExcelEmployeesToForm(model);
            return HandleResult(result);
        }

    }
}