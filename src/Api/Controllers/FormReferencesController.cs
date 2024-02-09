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


        [HttpDelete("DeleteFormReference/{id}")]
        public async Task<IActionResult> DeleteFormReference(int id)
        {

            return HandleResult(await _formReferenceService.DeleteFormReference(id));
        }
        [HttpPost("UploadFormRefernce")]
        public async Task<IActionResult> UploadRefernce(FormRefernceFileUploadRequest request)
        {
            return HandleResult(await _formReferenceService.UploadRefernce(request));
        }
    }
}