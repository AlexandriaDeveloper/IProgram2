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
        private readonly Microsoft.Extensions.Logging.ILogger<DailyReferenceService> _logger;
        private readonly PayrollPdfParserService _payrollPdfParserService;
        private readonly IEmployeeRepository _employeeRepository;
        private readonly IEmployeeNetPayRepository _employeeNetPayRepository;

        public DailyReferenceService(
            IDailyReferencesRepository dailyReferencesRepository, 
            IUnitOfWork uow, 
            IWebHostEnvironment hostEnvironment, 
            IFileStorageService fileStorageService, 
            Microsoft.Extensions.Logging.ILogger<DailyReferenceService> logger,
            PayrollPdfParserService payrollPdfParserService,
            IEmployeeRepository employeeRepository,
            IEmployeeNetPayRepository employeeNetPayRepository)
        {
            _dailyReferencesRepository = dailyReferencesRepository;
            _uow = uow;
            _hostEnvironment = hostEnvironment;
            _fileStorageService = fileStorageService;
            _logger = logger;
            _payrollPdfParserService = payrollPdfParserService;
            _employeeRepository = employeeRepository;
            _employeeNetPayRepository = employeeNetPayRepository;
        }

        public async Task<Result<object>> DeleteReference(int id)
        {



            var dailyReference = await _dailyReferencesRepository.GetById(id);
            if (dailyReference == null)
            {
                return Result.Failure(new Error("404", "المرجع غير موجود."));
            }

            var relatedNetPays = await _employeeNetPayRepository.GetQueryable().Where(n => n.DailyReferenceId == id).ToListAsync();
            foreach (var netPay in relatedNetPays)
            {
                await _employeeNetPayRepository.Delete(netPay.Id);
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

            // Check if file is PDF, then parse it for NetPay
            if (path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read))
                    {
                        var netPayDict = _payrollPdfParserService.ParseNetPayFromPdf(stream);
                        if (netPayDict.Any())
                        {
                            var nationalIds = netPayDict.Keys.ToList();
                            var existingEmployees = await _employeeRepository.GetQueryable().Where(e => nationalIds.Contains(e.Id)).ToListAsync();
                            var validEmployeeIds = existingEmployees.Select(e => e.Id).ToHashSet();

                            foreach (var kvp in netPayDict)
                            {
                                if (validEmployeeIds.Contains(kvp.Key))
                                {
                                    var employeeNetPay = new EmployeeNetPay
                                    {
                                        DailyId = request.DailyId,
                                        EmployeeId = kvp.Key,
                                        NetPay = kvp.Value,
                                        DailyReferenceId = dailyReference.Id
                                    };
                                    await _employeeNetPayRepository.Insert(employeeNetPay);
                                }
                            }
                            
                            await _uow.SaveChangesAsync();
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to parse PDF and save NetPay records.");
                }
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