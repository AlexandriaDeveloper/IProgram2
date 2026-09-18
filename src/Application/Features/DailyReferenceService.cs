using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Application.Dtos.Requests;
using Application.Helpers;
using Core.Interfaces;
using Core.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;

namespace Application.Features
{
    public class DailyReferenceService
    {
        private readonly IDailyReferencesRepository _dailyReferencesRepository;
        private readonly IUnitOfWork _uow;
        private readonly IWebHostEnvironment _hostEnvironment;
        private readonly IFileStorageService _fileStorageService;
        private readonly ILogger<DailyReferenceService> _logger;

        public DailyReferenceService(
            IDailyReferencesRepository dailyReferencesRepository,
            IUnitOfWork uow,
            IWebHostEnvironment hostEnvironment,
            IFileStorageService fileStorageService,
            ILogger<DailyReferenceService> logger)
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
                return Result.Failure(new Error("500", "فشلت عملية حذف المرجع من قاعدة البيانات."));
            }

            Console.WriteLine($"[DEBUG] Deleting Reference. Path: '{dailyReference.ReferencePath}'");

            if (!string.IsNullOrEmpty(dailyReference.ReferencePath) && dailyReference.ReferencePath.Contains("cloudinary", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("[DEBUG] Detected Cloudinary path. Invoking DeleteFileAsync.");
                var delResult = await _fileStorageService.DeleteFileAsync(dailyReference.ReferencePath, "DailyReferences");
                if (!delResult)
                {
                    _logger.LogWarning("Failed to delete file from Cloudinary: {Path}", dailyReference.ReferencePath);
                }
            }
            else if (!string.IsNullOrEmpty(dailyReference.ReferencePath))
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
                Console.WriteLine($"[DEBUG-CLOUDINARY-ERR] Attempt 1 Failed for {fileName}: {ex}");
                _logger.LogError(ex, $"Cloud Storage Upload Attempt 1 Failed for {fileName}. Retrying...");
                try
                {
                    await Task.Delay(1500);
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read))
                    {
                        savedPath = await _fileStorageService.UploadFileAsync(stream, fileName);
                    }
                }
                catch (Exception retryEx)
                {
                    Console.WriteLine($"[DEBUG-CLOUDINARY-ERR] Retry Failed for {fileName}: {retryEx}");
                    _logger.LogError(retryEx, $"Cloud Storage Upload Retry Failed for {fileName}. Falling back to local storage.");
                }
            }


            var dailyReference = new DailyReference
            {
                DailyId = request.DailyId,
                ReferencePath = savedPath,
                Description = request.Description
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

        public async Task<Result<(Stream stream, string contentType, string fileName)>> GetReferenceFile(int id)
        {
            var dailyReference = await _dailyReferencesRepository.GetById(id);
            if (dailyReference == null || !dailyReference.IsActive)
            {
                return Result.Failure<(Stream, string, string)>(new Error("404", "المرجع غير موجود."));
            }

            if (string.IsNullOrEmpty(dailyReference.ReferencePath))
            {
                return Result.Failure<(Stream, string, string)>(new Error("404", "مسار المرجع غير صالح."));
            }

            // Cloudinary or External storage
            if (dailyReference.ReferencePath.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
                dailyReference.ReferencePath.Contains("cloudinary", StringComparison.OrdinalIgnoreCase))
            {
                var downloaded = await _fileStorageService.DownloadFileStreamAsync(dailyReference.ReferencePath, "DailyReferences");
                if (downloaded == null)
                {
                    return Result.Failure<(Stream, string, string)>(new Error("404", "تعذر جلب الملف من التخزين السحابي."));
                }
                return Result.Success(downloaded.Value);
            }

            // Local file (Legacy)
            var safeFileName = Path.GetFileName(dailyReference.ReferencePath);
            var directoryPath = Path.Combine(_hostEnvironment.ContentRootPath, "Content", "DailyReferences");
            var filePath = Path.Combine(directoryPath, safeFileName);

            if (!File.Exists(filePath))
            {
                return Result.Failure<(Stream, string, string)>(new Error("404", "الملف غير موجود على الخادم."));
            }

            var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var contentType = GetContentType(safeFileName);
            return Result.Success((fileStream as Stream, contentType, safeFileName));
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
                _logger.LogError(ex, "Test Connection to Cloudinary Failed");
                return "FAILED";
            }
        }


        public async Task<List<object>> SyncLocalReferencesToCloudinary(int? specificDailyId = null)
        {
            var results = new List<object>();
            var allReferences = await _dailyReferencesRepository.ListAllAsync();
            var localRefs = allReferences
                .Where(r => (!specificDailyId.HasValue || r.DailyId == specificDailyId.Value) &&
                            !string.IsNullOrEmpty(r.ReferencePath) &&
                            !r.ReferencePath.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var directoryPath = Path.Combine(_hostEnvironment.ContentRootPath, "Content", "DailyReferences");

            bool anyUpdated = false;
            foreach (var r in localRefs)
            {
                var fileName = Path.GetFileName(r.ReferencePath);
                var fullLocalPath = Path.Combine(directoryPath, fileName);

                if (!File.Exists(fullLocalPath))
                {
                    results.Add(new { Id = r.Id, DailyId = r.DailyId, FileName = fileName, Status = "Local file not found" });
                    continue;
                }

                try
                {
                    string cloudinaryUrl;
                    using (var stream = new FileStream(fullLocalPath, FileMode.Open, FileAccess.Read))
                    {
                        cloudinaryUrl = await _fileStorageService.UploadFileAsync(stream, fileName);
                    }

                    r.ReferencePath = cloudinaryUrl;
                    _dailyReferencesRepository.Update(r);
                    anyUpdated = true;
                    results.Add(new { Id = r.Id, DailyId = r.DailyId, FileName = fileName, Status = "Synced", CloudinaryUrl = cloudinaryUrl });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Failed to sync reference {r.Id} ({fileName}) to Cloudinary");
                    results.Add(new { Id = r.Id, DailyId = r.DailyId, FileName = fileName, Status = "Failed", Error = "فشلت عملية المزامنة السحابية للملف." });
                }
            }

            if (anyUpdated)
            {
                await _uow.SaveChangesAsync();
            }

            return results;
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
                ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                ".xls" => "application/vnd.ms-excel",
                ".json" => "application/json",
                _ => "application/octet-stream"
            };
        }
    }
}