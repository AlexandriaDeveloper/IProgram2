using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Application.Dtos;
using Application.Dtos.Requests;
using Application.Helpers;
using Core.Interfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Persistence.Extensions;

namespace Application.Features
{
    public class FormReferenceService
    {
        private readonly IFormReferencesRepository _formReferencesRepository;
        private readonly IUniteOfWork _uow;
        private readonly IConfiguration _config;
        private readonly IWebHostEnvironment _hostEnvironment;
        private readonly IHttpContextAccessor _httpContextAccessor;

        public FormReferenceService(IFormReferencesRepository formReferencesRepository, IUniteOfWork uow, IHttpContextAccessor httpContextAccessor, IConfiguration config, IWebHostEnvironment hostEnvironment)
        {
            this._httpContextAccessor = httpContextAccessor;
            this._hostEnvironment = hostEnvironment;
            this._config = config;
            this._formReferencesRepository = formReferencesRepository;
            this._uow = uow;
        }

        public async Task<Result<List<FormReferenceDto>>> GetFormReferences(int formId)
        {
            var result = await _formReferencesRepository.GetQueryable().Where(x => x.FormId == formId).ToListAsync();
            if (result == null)
            {
                return Result.Failure<List<FormReferenceDto>>(new Error("404", "Not Found"));
            }
            var referencesDto = result.Select(x => new FormReferenceDto()
            {
                FormId = x.FormId,
                Id = x.Id,
                ReferencePath = _config["ApiContent"] + "FormReferences/" + x.ReferencePath
            }).ToList();
            return Result.Success<List<FormReferenceDto>>(referencesDto);
        }

        public async Task<Result> DeleteFormReference(int id)
        {
            var formRefernce = await _formReferencesRepository.GetById(id);
            if (formRefernce == null)
            {
                return Result.Failure(new Error("404", "Not Found"));
            }
            formRefernce.IsActive = false;
            formRefernce.DeactivatedAt = DateTime.Now;
            formRefernce.DeactivatedBy = ClaimPrincipalExtensions.RetriveAuthUserFromPrincipal(_httpContextAccessor.HttpContext.User);
            _formReferencesRepository.Update(formRefernce);
            var result = await _uow.SaveChangesAsync() > 0;
            if (!result)
            {
                return Result.Failure(new Error("500", "Internal Server Error"));
            }
            return Result.Success("تم الحذف بنجاح");

        }

        public async Task<Result> UploadRefernce(FormRefernceFileUploadRequest request)
        {
            var fileName = request.FormId.ToString() + "_" + DateTime.Now.ToString("yyyyMMddHHmmssfff") + Path.GetExtension(request.File.FileName);
            //check directory exist
            if (!Directory.Exists(Path.Combine(_hostEnvironment.ContentRootPath, "wwwroot", "Content", "FormReferences")))
            {
                Directory.CreateDirectory(Path.Combine(_hostEnvironment.ContentRootPath, "wwwroot", "Content", "FormReferences"));
            }

            var path = Path.Combine(_hostEnvironment.ContentRootPath, "wwwroot", "Content", "FormReferences", fileName);


            using (var fileStream = new FileStream(path, FileMode.Create))
            {
                await request.File.CopyToAsync(fileStream);
            }
            await _formReferencesRepository.Insert(new Core.Models.FormRefernce()
            {
                FormId = request.FormId,
                ReferencePath = fileName
            });
            var result = await _uow.SaveChangesAsync() > 0;
            if (!result)
            {
                return Result.Failure(new Error("500", "Internal Server Error"));
            }

            return Result.Success("تم الحذف بنجاح");
        }



    }
}