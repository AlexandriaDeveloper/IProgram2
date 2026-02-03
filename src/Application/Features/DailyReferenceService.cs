using System;
using System.IO;
using System.Threading.Tasks;
using Application.Dtos.Requests;
using Application.Helpers;
using Core.Interfaces;
using Core.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace Application.Features
{
    public class DailyReferenceService
    {
        private readonly IDailyReferencesRepository _dailyReferencesRepository;
        private readonly IUnitOfWork _uow;
        private readonly IWebHostEnvironment _hostEnvironment;
        private readonly IFileStorageService _fileStorageService;
        private readonly Microsoft.Extensions.Logging.ILogger<DailyReferenceService> _logger;

        public DailyReferenceService(IDailyReferencesRepository dailyReferencesRepository, IUnitOfWork uow, IWebHostEnvironment hostEnvironment, IFileStorageService fileStorageService, Microsoft.Extensions.Logging.ILogger<DailyReferenceService> logger)
        {
            _dailyReferencesRepository = dailyReferencesRepository;
            _uow = uow;
            _hostEnvironment = hostEnvironment;
            _fileStorageService = fileStorageService;
            _logger = logger;
        }

        public async Task<Result<object>> DeleteReference(int id)
        {



            var dailyReference = await _dailyReferencesRepository.GetById(id);
            if (dailyReference == null)
            {
                return Result.Failure(new Error("404", "المرجع غير موجود."));
            }

            await _dailyReferencesRepository.Delete(dailyReference.Id);
            var result = await _uow.SaveChangesAsync() > 0;
            if (!result)
            {
                return Result.Failure(new Error("500", "فشلت عملية حذف المرجع."));
            }

            Console.WriteLine($"[DEBUG] Deleting Reference. Path: '{dailyReference.ReferencePath}'");

            if (!string.IsNullOrEmpty(dailyReference.ReferencePath) && dailyReference.ReferencePath.Contains("cloudinary", StringComparison.OrdinalIgnoreCase))
            {
                 Console.WriteLine("[DEBUG] Detected Cloudinary path. Invoking DeleteFileAsync.");
                 // Cloudinary File
            var delResult=    await _fileStorageService.DeleteFileAsync(dailyReference.ReferencePath, "DailyReferences");
            if (!delResult)
            {
                return Result.Failure(new Error("500", "فشلت عملية حذف المرجع."));
            }
            }
            else
            {
                // Local File (Legacy)
                var directoryPath = Path.Combine(_hostEnvironment.ContentRootPath, "Content", "DailyReferences");
                var filePath = Path.Combine(directoryPath, dailyReference.ReferencePath);
                
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }


            return Result.Success("تم حذف المرجع بنجاح.");
        }

        public async Task<Result> UploadReference(DailyReferenceFileUploadRequest request)
        {
            var fileName = $"{request.DailyId}_{DateTime.Now:yyyyMMddHHmmssfff}{Path.GetExtension(request.File.FileName.ToLower())}";
            var directoryPath = Path.Combine(_hostEnvironment.ContentRootPath, "Content", "DailyReferences");

            if (!Directory.Exists(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }

            var path = Path.Combine(directoryPath, fileName);

            using (var fileStream = new FileStream(path, FileMode.Create))
            {
                await request.File.CopyToAsync(fileStream);
            }

            string savedPath = fileName;
            try 
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read))
                {
                    savedPath = await _fileStorageService.UploadFileAsync(stream, fileName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cloud Storage Upload Failed");
            }

            var dailyReference = new DailyReference
            {
                DailyId = request.DailyId,
                ReferencePath = savedPath,
                Description = request.Description,
                //Name = request.File.FileName // This is a [NotMapped] property for display
            };

            await _dailyReferencesRepository.Insert(dailyReference);

            var result = await _uow.SaveChangesAsync() > 0;
            if (!result)
            {
                if (File.Exists(path)) File.Delete(path);
                return Result.Failure(new Error("500", "فشلت عملية حفظ المرجع فى قاعدة البيانات."));
            }

            return Result.Success("تم رفع الملف بنجاح.");
        }
        public async Task<string> TestCloudinaryConnection()
        {
            try
            {
                using (var memoryStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("Hello Cloudinary Test")))
                {
                    string testFileName = $"test_conn_{DateTime.Now.Ticks}.txt";
                    return await _fileStorageService.UploadFileAsync(memoryStream, testFileName, "TestFolder");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Test Connection Failed");
                return $"FAILED: {ex.Message}";
            }
        }
    }
}