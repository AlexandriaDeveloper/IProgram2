using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Application.Dtos;
using Application.Helpers;
using Application.Services;

using Core.Interfaces;
using Core.Models;
using Microsoft.EntityFrameworkCore;
using Persistence.Helpers;
using Persistence.Specifications;

namespace Application.Features
{
    public class DepartmentService
    {
        private readonly IDepartmentRepository _departmentRepository;
        private readonly IEmployeeRepository _employeeRepository;
        private readonly IUniteOfWork _unitOfWork;

        public DepartmentService(IDepartmentRepository departmentRepository, IEmployeeRepository employeeRepository, IUniteOfWork unitOfWork)
        {
            this._unitOfWork = unitOfWork;
            this._departmentRepository = departmentRepository;
            this._employeeRepository = employeeRepository;
        }

        public async Task<Result<PaginatedResult<DepartmentDto>>> getDepartments(DepartmentParam departmentParam)
        {
            var spec = new DepartmentSpecification(departmentParam);
            spec.Includes.Add(x => x.Employees);
            var departments = await _departmentRepository.ListAllAsync(spec);


            if (departments == null)
                return Result.Failure<PaginatedResult<DepartmentDto>>(new Error("404", "Not Found"));

            var specCount = new DepartmentCountSpecification(departmentParam);
            var count = await _departmentRepository.CountAsync(specCount);

            var departmentDtos = departments.Select(x => new DepartmentDto
            {
                Id = x.Id,
                Name = x.Name,
                EmployeesCount = x.Employees.Count


            }).ToList();

            var PaginatedResult = PaginatedResult<DepartmentDto>.Create(departmentDtos, departmentParam.PageIndex, departmentParam.PageSize, count);

            return Result.Success<PaginatedResult<DepartmentDto>>(PaginatedResult);
        }
        public async Task<Result<DepartmentDto>> getDepartment(int id)
        {
            var department = await _departmentRepository.GetById(id);
            if (department == null)
            {
                return Result.Failure<DepartmentDto>(new Error("404", "Not Found"));
            }
            var departmentDto = new DepartmentDto
            {
                Id = department.Id,
                Name = department.Name
            };
            return Result.Success<DepartmentDto>(departmentDto);
        }

        public async Task<Result<DepartmentDto>> AddDepartment(DepartmentDto departmentDto)
        {
            var department = new Department
            {
                Name = departmentDto.Name
            };
            await _departmentRepository.Insert(department);
            var result = await _unitOfWork.SaveChangesAsync() > 0;
            if (!result)
            {
                return Result.Failure<DepartmentDto>(new Error("500", "Internal Server Error"));
            }
            departmentDto.Id = department.Id;
            return Result.Success<DepartmentDto>(departmentDto);
        }
        public async Task<Result> UploadEmployeesDepartment(EmployeesDepartmentFileUploadRequest request)
        {
            if (request.File == null)
            {
                return Result.Failure<DepartmentDto>(new Error("500", "الملف غير موجود للرفع الرجاء التأكد من الملف"));
            }
            UploadFile upload = new UploadFile(request.File);
            var path = await upload.UploadFileToTempPath();
            NpoiServiceProvider npoi = new NpoiServiceProvider(path);

            //Check Header Row


            DataTable dt = npoi.ReadSheeBySheetName("Sheet1");
            foreach (DataRow row in dt.Rows)
            {
                Employee empExist = null;
                var errorCode = "لا يوجد كود";
                if (!string.IsNullOrEmpty(row.ItemArray[0].ToString()))
                {
                    empExist = await _employeeRepository.GetEmployeeByNationalId(row.ItemArray[0].ToString());
                    errorCode = row.ItemArray[0].ToString();
                }
                else if (!string.IsNullOrEmpty(row.ItemArray[1].ToString()))
                {
                    var result = int.TryParse(row.ItemArray[1].ToString(), out int id);
                    if (result)
                    {
                        empExist = _employeeRepository.GetQueryable().FirstOrDefault(x => x.TabCode == id);

                    }
                    errorCode = row.ItemArray[1].ToString();
                }
                else if (!string.IsNullOrEmpty(row.ItemArray[2].ToString()))
                {
                    var result = int.TryParse(row.ItemArray[2].ToString(), out int id);
                    if (result)
                    {
                        empExist = _employeeRepository.GetQueryable().FirstOrDefault(x => x.TegaraCode == id);
                    }
                    errorCode = row.ItemArray[2].ToString();
                }
                if (empExist == null)
                {

                    return Result.Failure<DepartmentDto>(new Error("500", " خطأ بالكود رقم " + errorCode + " لا يوجد موظف بهذا الكود"));
                }

                empExist.DepartmentId = request.DepartmentId;
                _employeeRepository.Update(empExist);

            }
            await _unitOfWork.SaveChangesAsync();
            return Result.Success("تم الرفع بنجاح");

        }

        public async Task<Result<DepartmentDto>> EditDepartment(DepartmentDto departmentDto)
        {
            var department = await _departmentRepository.GetById(departmentDto.Id);
            department.Name = departmentDto.Name;
            _departmentRepository.Update(department);
            var result = await _unitOfWork.SaveChangesAsync() > 0;
            if (!result)
            {
                return Result.Failure<DepartmentDto>(new Error("500", "Internal Server Error"));
            }
            departmentDto.Id = department.Id;
            return Result.Success<DepartmentDto>(departmentDto);
        }

        public async Task<Result> UpdateEmployeesDepartment(int? id, EmployeesInDepartmentIdsRequest employees)
        {
            if (id.HasValue)
            {
                var department = await _departmentRepository.GetById(id.Value);
                if (department == null)
                {
                    return Result.Failure<DepartmentDto>(new Error("404", "Not Found"));
                }
            }
            foreach (var employee in employees.Ids)
            {
                var emp = await _employeeRepository.GetById(employee);
                if (emp == null)
                {
                    return Result.Failure<DepartmentDto>(new Error("404", "Not Found"));
                }
                emp.DepartmentId = id;
                _employeeRepository.Update(emp);
            }

            var result = await _unitOfWork.SaveChangesAsync() > 0;
            if (!result)
            {
                return Result.Failure<DepartmentDto>(new Error("500", "Internal Server Error"));
            }
            return Result.Success("تم الحفظ بنجاح");
        }

        public async Task<Result> RemoveAllEmployeesFromDepartment(int departmentId)
        {

            var department = await _departmentRepository.GetQueryable().Include(x => x.Employees).FirstOrDefaultAsync(x => x.Id == departmentId);
            if (department == null)
            {
                return Result.Failure<DepartmentDto>(new Error("404", "Not Found"));
            }

            foreach (var employee in department.Employees)
            {
                employee.DepartmentId = null;
                _employeeRepository.Update(employee);
            }

            var result = await _unitOfWork.SaveChangesAsync() > 0;
            if (!result)
            {
                return Result.Failure<DepartmentDto>(new Error("500", "Internal Server Error"));
            }
            return Result.Success("تم الحفظ بنجاح");
        }


        public async Task<Result> DeleteDepartment(int id)
        {
            var department = await _departmentRepository.GetById(id);
            if (department == null)
            {
                return Result.Failure<DepartmentDto>(new Error("404", "Not Found"));
            }
            await _departmentRepository.Delete(id);
            var result = await _unitOfWork.SaveChangesAsync() > 0;
            if (!result)
            {
                return Result.Failure<DepartmentDto>(new Error("500", "Internal Server Error"));
            }
            return Result.Success("تم الحذف بنجاح");
        }

        public async Task<Result<List<DepartmentDto>>> getAllDepartments()
        {
            return Result.Success(await _departmentRepository.GetQueryable().Where(x => x.IsActive).Select(x => new DepartmentDto { Id = x.Id, Name = x.Name }).ToListAsync());
        }
    }
}