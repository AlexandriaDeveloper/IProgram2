using System;
using System.IO;
using System.Threading.Tasks;
using Application.Dtos.Requests;
using Application.Helpers;
using Core.Interfaces;
using Core.Models;
using Microsoft.AspNetCore.Hosting;

namespace Application.Features
{
    public class DailyReferenceService
    {
        private readonly IDailyReferencesRepository _dailyReferencesRepository;
        private readonly IUniteOfWork _uow;
        private readonly IWebHostEnvironment _hostEnvironment;

        public DailyReferenceService(IDailyReferencesRepository dailyReferencesRepository, IUniteOfWork uow, IWebHostEnvironment hostEnvironment)
        {
            _dailyReferencesRepository = dailyReferencesRepository;
            _uow = uow;
            _hostEnvironment = hostEnvironment;
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

            var directoryPath = Path.Combine(_hostEnvironment.ContentRootPath, "Content", "DailyReferences");
            var filePath = Path.Combine(directoryPath, dailyReference.ReferencePath);
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
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

            var dailyReference = new DailyReference
            {
                DailyId = request.DailyId,
                ReferencePath = fileName,
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
    }
}