using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Application.Dtos;
using Application.Dtos.Requests;
using Application.Features;
using Application.Helpers;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers
{

    public class FormReferencesController : BaseApiController
    {
        private readonly FormReferenceService _formReferenceService;
        public FormReferencesController(FormReferenceService formReferenceService)
        {
            this._formReferenceService = formReferenceService;

        }

        [HttpGet("GetFormReferences/{formId}")] // GetFormReferences
        public async Task<IActionResult> GetFormReferences(int formId)
        {
            return HandleResult<List<FormReferenceDto>>(await _formReferenceService.GetFormReferences(formId));
        }


        [HttpGet("file/{id}")]
        public async Task<IActionResult> GetFile(int id)
        {
            var result = await _formReferenceService.GetReferenceFile(id);
            if (result.IsFailure)
            {
                return HandleResult(result);
            }

            var (stream, contentType, fileName) = result.Value;
            return File(stream, contentType, fileName, enableRangeProcessing: true);
        }

        [HttpDelete("DeleteFormReference/{id}")]
        public async Task<IActionResult> DeleteFormReference(int id)
        {
            return HandleResult(await _formReferenceService.DeleteFormReference(id));
        }

        [HttpPost("UploadFormRefernce")]
        [RequestSizeLimit(FileSecurityValidator.MaxDailyReferenceBytes)]
        public async Task<IActionResult> UploadRefernce([FromForm] FormRefernceFileUploadRequest request)
        {
            var validation = FileSecurityValidator.ValidateFile(
                request?.File,
                FileSecurityValidator.MaxDailyReferenceBytes,
                new[] { ".pdf", ".jpg", ".jpeg", ".png" });

            if (!validation.IsSuccess)
            {
                return HandleResult(validation);
            }

            return HandleResult(await _formReferenceService.UploadRefernce(request));
        }
    }
}