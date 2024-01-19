using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Application.Dtos;
using Application.Helpers;
using Application.Shared;
using Core.Interfaces;
using Core.Models;
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

        public async Task<Result> UpdateEmployeesDepartment(int? id, int[] employees)
        {
            if (id.HasValue)
            {
                var department = await _departmentRepository.GetById(id.Value);
                if (department == null)
                {
                    return Result.Failure(new Error("404", "Not Found"));
                }
            }
            foreach (var employee in employees)
            {
                var emp = await _employeeRepository.GetById(employee);
                if (emp == null)
                {
                    return Result.Failure(new Error("404", "Not Found"));
                }
                emp.DepartmentId = id;
                _employeeRepository.Update(emp);
            }

            var result = await _unitOfWork.SaveChangesAsync() > 0;
            if (!result)
            {
                return Result.Failure(new Error("500", "Internal Server Error"));
            }
            return Result.Success("تم الحفظ بنجاح");
        }

        public async Task<Result> DeleteDepartment(int id)
        {
            var department = await _departmentRepository.GetById(id);
            if (department == null)
            {
                return Result.Failure(new Error("404", "Not Found"));
            }
            await _departmentRepository.DeActive(id);
            var result = await _unitOfWork.SaveChangesAsync() > 0;
            if (!result)
            {
                return Result.Failure(new Error("500", "Internal Server Error"));
            }
            return Result.Success("تم الحذف بنجاح");
        }
    }
}