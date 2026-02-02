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
using Microsoft.Extensions.Logging;

namespace Application.Features
{
    public class FormReferenceService
    {
        private readonly IFormReferencesRepository _formReferencesRepository;
        private readonly IUnitOfWork _uow;
        private readonly IConfiguration _config;
        private readonly IWebHostEnvironment _hostEnvironment;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly ICurrentUserService _currentUserService;

        private readonly IFileStorageService _fileStorageService;
        private readonly Microsoft.Extensions.Logging.ILogger<FormReferenceService> _logger;

        public FormReferenceService(IFormReferencesRepository formReferencesRepository, IUnitOfWork uow, IHttpContextAccessor httpContextAccessor, IConfiguration config, IWebHostEnvironment hostEnvironment, ICurrentUserService currentUserService, IFileStorageService fileStorageService, Microsoft.Extensions.Logging.ILogger<FormReferenceService> logger)
        {
            this._httpContextAccessor = httpContextAccessor;
            this._hostEnvironment = hostEnvironment;
            this._config = config;
            this._formReferencesRepository = formReferencesRepository;
            this._uow = uow;
            this._currentUserService = currentUserService;
            this._fileStorageService = fileStorageService;
            this._logger = logger;
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
                ReferencePath = x.ReferencePath.StartsWith("http") ? x.ReferencePath : _config["ApiImageContent"] + "FormReferences/" + x.ReferencePath
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
            formRefernce.DeactivatedBy = _currentUserService.UserId;
            _formReferencesRepository.Update(formRefernce);
            var result = await _uow.SaveChangesAsync() > 0;
            if (!result)
            {
                return Result.Failure(new Error("500", "Internal Server Error"));
            }
            if (!string.IsNullOrEmpty(formRefernce.ReferencePath) && formRefernce.ReferencePath.Contains("cloudinary"))
            {
                 // Cloudinary File
                 await _fileStorageService.DeleteFileAsync(formRefernce.ReferencePath, "FormReferences");
            }
            else
            {
                // Local File (Legacy or previous implementation)
                 // Note: Logic for local deletion was missing in original code snippet above, 
                 // but typically we should clean up if possible. 
                 // The original code only did soft delete (IsActive=false). 
                 // If we want physical delete:
                 var fileName = Path.GetFileName(formRefernce.ReferencePath);
                 var path = Path.Combine(_hostEnvironment.ContentRootPath, "Content", "FormReferences", fileName);
                 if (File.Exists(path))
                 {
                     File.Delete(path);
                 }
            }
            
            return Result.Success("تم الحذف بنجاح");

        }

        public async Task<Result> UploadRefernce(FormRefernceFileUploadRequest request)
        {
            var fileName = request.FormId.ToString() + "_" + DateTime.Now.ToString("yyyyMMddHHmmssfff") + Path.GetExtension(request.File.FileName);
            //check directory exist
            if (!Directory.Exists(Path.Combine(_hostEnvironment.ContentRootPath, "Content", "FormReferences")))
            {
                Directory.CreateDirectory(Path.Combine(_hostEnvironment.ContentRootPath, "Content", "FormReferences"));
            }

            var path = Path.Combine(_hostEnvironment.ContentRootPath, "Content", "FormReferences", fileName);


            using (var fileStream = new FileStream(path, FileMode.Create))
            {
                await request.File.CopyToAsync(fileStream);
            }

            string savedPath = fileName;
            try
            {
               using (var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read))
               {
                   savedPath = await _fileStorageService.UploadFileAsync(fileStream, fileName, "FormReferences");
               }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cloud Storage Upload Failed for FormReference");
            }
            await _formReferencesRepository.Insert(new Core.Models.FormRefernce()
            {
                FormId = request.FormId,
                ReferencePath = savedPath
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