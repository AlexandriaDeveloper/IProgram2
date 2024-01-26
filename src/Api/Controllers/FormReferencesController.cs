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
        public async Task<Result<List<FormReferenceDto>>> GetFormReferences(int formId)
        {
            return await _formReferenceService.GetFormReferences(formId);
        }


        [HttpDelete("DeleteFormReference/{id}")]
        public async Task<Application.Helpers.Result> DeleteFormReference(int id)
        {
            return await _formReferenceService.DeleteFormReference(id);
        }
        [HttpPost("UploadFormRefernce")]
        public async Task<Application.Helpers.Result> UploadRefernce(FormRefernceFileUploadRequest request)
        {
            var random = Random.Shared.Next(1000, 5000);
            Task.Delay(random).Wait();
            return await _formReferenceService.UploadRefernce(request);
        }
    }
}