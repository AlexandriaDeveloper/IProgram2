
using Application.Dtos;
using Application.Dtos.Requests;
using Application.Helpers;
using Core.Interfaces;
using Core.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Persistence.Extensions;

namespace Application.Features
{
    public class EmployeeRefernceService
    {
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IEmployeeRefernceRepository _employeeRefernceRepository;
        private readonly IConfiguration _config;
        private readonly IUnitOfWork _uow;
        private readonly IWebHostEnvironment _hostEnvironment;
        private readonly ICurrentUserService _currentUserService;

        public EmployeeRefernceService(
            IHttpContextAccessor httpContextAccessor,
            IEmployeeRefernceRepository employeeRefernceRepository,
            IUnitOfWork uow,
             IConfiguration config,
             IWebHostEnvironment hostEnvironment,
             ICurrentUserService currentUserService)
        {
            this._hostEnvironment = hostEnvironment;
            this._uow = uow;
            this._config = config;
            this._httpContextAccessor = httpContextAccessor;
            this._employeeRefernceRepository = employeeRefernceRepository;
            this._currentUserService = currentUserService;

        }

        public async Task<Result<List<EmployeeRefernceDto>>> GetEmployeeRefernces(string employeeId)
        {
            var employeeRefernces = await _employeeRefernceRepository.GetQueryable().Where(x => x.EmployeeId == employeeId && x.IsActive).ToListAsync();
            var employeeReferncesToReturn = employeeRefernces.Select(x => new EmployeeRefernceDto()
            {
                EmployeeId = x.EmployeeId,
                ReferencePath = _config["ApiContent"] + "EmployeeReferences/" + x.ReferencePath,
                Id = x.Id
            }).ToList();

            return Result.Success<List<EmployeeRefernceDto>>(employeeReferncesToReturn);
        }

        public async Task<Result> DeleteEmployeeReference(int id)
        {
            var employeeRefernce = await _employeeRefernceRepository.GetById(id);
            if (employeeRefernce == null)
            {
                return Result.Failure(new Error("404", "Not Found"));
            }
            employeeRefernce.IsActive = false;
            employeeRefernce.DeactivatedAt = DateTime.Now;
            employeeRefernce.DeactivatedBy = _currentUserService.UserId;
            _employeeRefernceRepository.Update(employeeRefernce);
            var result = await _uow.SaveChangesAsync() > 0;
            if (!result)
            {
                return Result.Failure(new Error("500", "Internal Server Error"));
            }
            return Result.Success("تم الحذف بنجاح");

        }

        public async Task<Result> UploadRefernce(EmployeeRefernceFileUploadRequest request)
        {
            var fileName = request.EmployeeId.ToString() + "_" + DateTime.Now.ToString("yyyyMMddHHmmssfff") + Path.GetExtension(request.File.FileName);
            //check directory exist
            // if (!Directory.Exists(Path.Combine(_hostEnvironment.ContentRootPath, "wwwroot", "Content", "EmployeeReferences")))
            // {
            //     Directory.CreateDirectory(Path.Combine(_hostEnvironment.ContentRootPath, "wwwroot", "Content", "EmployeeReferences"));
            // }

            // var path = Path.Combine(_hostEnvironment.ContentRootPath, "wwwroot", "Content", "EmployeeReferences", fileName);

            var path = Path.Combine(_hostEnvironment.ContentRootPath, "Content", "EmployeeReferences", fileName);
            //check directory exist
            if (!Directory.Exists(Path.Combine(_hostEnvironment.ContentRootPath, "Content", "EmployeeReferences")))
            {
                Directory.CreateDirectory(Path.Combine(_hostEnvironment.ContentRootPath, "Content", "EmployeeReferences"));
            }
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            //save file



            using (var fileStream = new FileStream(path, FileMode.Create))
            {
                await request.File.CopyToAsync(fileStream);
            }
            await _employeeRefernceRepository.Insert(new EmployeeRefernce
            {
                EmployeeId = request.EmployeeId,
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