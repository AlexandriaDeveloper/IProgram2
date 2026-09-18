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
                ReferencePath = $"api/FormReferences/file/{x.Id}"
            }).ToList();
            return Result.Success<List<FormReferenceDto>>(referencesDto);
        }

        public async Task<Result<(Stream stream, string contentType, string fileName)>> GetReferenceFile(int id)
        {
            var formReference = await _formReferencesRepository.GetById(id);
            if (formReference == null || !formReference.IsActive)
            {
                return Result.Failure<(Stream, string, string)>(new Error("404", "المرجع غير موجود."));
            }

            if (string.IsNullOrEmpty(formReference.ReferencePath))
            {
                return Result.Failure<(Stream, string, string)>(new Error("404", "مسار المرجع غير صالح."));
            }

            // Cloudinary or External storage
            if (formReference.ReferencePath.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
                formReference.ReferencePath.Contains("cloudinary", StringComparison.OrdinalIgnoreCase))
            {
                var downloaded = await _fileStorageService.DownloadFileStreamAsync(formReference.ReferencePath, "FormReferences");
                if (downloaded == null)
                {
                    return Result.Failure<(Stream, string, string)>(new Error("404", "تعذر جلب الملف من التخزين السحابي."));
                }
                return Result.Success(downloaded.Value);
            }

            // Local file (Legacy)
            var safeFileName = Path.GetFileName(formReference.ReferencePath);
            var directoryPath = Path.Combine(_hostEnvironment.ContentRootPath, "Content", "FormReferences");
            var filePath = Path.Combine(directoryPath, safeFileName);

            if (!File.Exists(filePath))
            {
                return Result.Failure<(Stream, string, string)>(new Error("404", "الملف غير موجود على الخادم."));
            }

            var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var contentType = GetContentType(safeFileName);
            return Result.Success((fileStream as Stream, contentType, safeFileName));
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

            return Result.Success("تم رفع الملف بنجاح.");
        }

        private static string GetContentType(string fileName)
        {
            var ext = Path.GetExtension(fileName).ToLowerInvariant();
            return ext switch
            {
                ".pdf" => "application/pdf",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".gif" => "image/gif",
                _ => "application/octet-stream"
            };
        }



    }
}