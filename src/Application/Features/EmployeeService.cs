using System.Data;
using Application.Dtos;
using Application.Dtos.Requests;
using Application.Helpers;
using Application.Services;
using Application.Shared;
using Application.Shared.ErrorResult;
using Core.Interfaces;
using Core.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Persistence.Helpers;
using Persistence.Specifications;
namespace Application.Features
{
    public class EmployeeService
    {
        private readonly IEmployeeRepository _employeeRepository;
        private readonly IUniteOfWork _uow;
        private readonly IFormDetailsRepository _formDetailsRepository;
        public EmployeeService(IEmployeeRepository employeeRepository, IFormDetailsRepository formDetailsRepository
        , IUniteOfWork uow)
        {
            this._formDetailsRepository = formDetailsRepository;
            this._uow = uow;

            this._employeeRepository = employeeRepository;

        }
        public async Task<Result<PaginatedResult<EmployeeDto>>> getEmployees(EmployeeParam param)
        {
            var spec = new EmployeeSpecification(param);
            spec.PaginationEnabled = true;

            var employees = await _employeeRepository.ListAllAsync(spec);
            var employeeToReturn = employees.Select(x => new EmployeeDto
            {
                Collage = x.Collage,
                DepartmentId = x.DepartmentId,
                Id = x.Id,

                Name = x.Name,
                NationalId = x.NationalId,
                TabCode = x.TabCode,
                TegaraCode = x.TegaraCode
            }).ToList();
            var count = await _employeeRepository.CountAsync(new EmployeeCountSpecification(param));
            var result = PaginatedResult<EmployeeDto>.Create(employeeToReturn.ToList(), param.PageIndex, param.PageSize, count);
            return Result.Success(result);

            //return Result.Success<PA<EmployeeDto>>(employeeToReturn);

        }
        public async Task<Result<string>> ConvertNumber(decimal num)
        {
            var result = NumericToLiteral.Convert(num, false, "جنيه", "جنيهات");


            return Result.Success<string>(result + " فقط  لا غير ");
        }

        public async Task<Result> getEmployee(EmployeeParam param)
        {
            var spec = new EmployeeSpecification(param);
            spec.PaginationEnabled = false;

            var employee = await _employeeRepository.GetBySpec(spec);
            if (employee == null)
            {
                return Result.Failure(new Error("404", "عفوا الموظف غير موجود"));
            }
            var employeeToReturn = new EmployeeDto
            {
                Collage = employee.Collage,
                DepartmentId = employee.DepartmentId,
                Id = employee.Id,

                Name = employee.Name,
                NationalId = employee.NationalId,
                TabCode = employee.TabCode,
                TegaraCode = employee.TegaraCode
            };
            return Result.Success(employeeToReturn);

            //return Result.Success<PA<EmployeeDto>>(employeeToReturn);

        }
        public async Task<Result<EmployeeDto>> AddEmployee(EmployeeDto employee, CancellationToken cancellationToken)
        {
            var empExist = await _employeeRepository.CheckEmployeeByNationalId(employee.NationalId);
            if (empExist)
            {
                return Result.Failure<EmployeeDto>(new Error("500", "الموظف موجود بالفعل في النظام"));
            }
            var employeeToDb = new Employee()
            {
                Collage = employee.Collage,
                DepartmentId = employee.DepartmentId,
                IsActive = true,
                Name = employee.Name,
                NationalId = employee.NationalId,
                TabCode = employee.TabCode,
                TegaraCode = employee.TegaraCode
            };
            await _employeeRepository.Insert(employeeToDb);

            await _uow.SaveChangesAsync(cancellationToken);

            employee.Id = employeeToDb.Id;

            return Result.Success(employee);

        }

        public async Task<Result<EmployeeReportDto>> EmployeeReport(EmployeeReportRequest request)
        {

            var employee = _formDetailsRepository.GetQueryable()
            .Where(x => x.EmployeeId == request.Id &&
             x.Form.IsActive && x.IsActive)
            .Include(x => x.Form)
            .ThenInclude(x => x.Daily).AsQueryable();

            if (request.StartDate.HasValue)
            {
                employee = employee.Where(x => x.Form.Daily.DailyDate >= request.StartDate);
            }
            else
            {
                employee = employee.Where(x => x.Form.Daily.DailyDate >= DateTime.Now.AddYears(-5));
            }
            if (request.EndDate.HasValue)
            {
                employee = employee.Where(x => x.Form.Daily.DailyDate <= request.EndDate);
            }
            else
            {
                employee = employee.Where(x => x.Form.Daily.DailyDate <= DateTime.Now.AddYears(5));
            }


            var EmployeeReportDto = new EmployeeReportDto();
            var emp = await _employeeRepository.GetById(request.Id);
            if (emp == null)
            {
                return Result.Failure<EmployeeReportDto>(new Error("404", "الموظف غير موجود"));
            }
            EmployeeReportDto.TabCode = emp.TabCode;
            EmployeeReportDto.TegaraCode = emp.TegaraCode;
            EmployeeReportDto.NationalId = emp.NationalId;
            EmployeeReportDto.Name = emp.Name;


            EmployeeReportDto.Dailies = employee
           .GroupBy(g => g.Form.Daily)
            .Select(x => new EmployeeDailyDto()
            {

                DailyDate = x.Key.DailyDate,
                DailyId = x.Key.Id,
                DailyName = x.Key.Name,
                State = x.Key.Closed ? "مغلق" : "مفتوح",
                Forms = x.Select(x2 => new EmployeeFormDto()
                {
                    Amount = x2.Amount,
                    FormId = x2.Id,
                    FormName = x2.Form.Name

                }).ToList()
            }).ToList();
            return Result.Success(EmployeeReportDto);


        }

        public async Task<Result> UploadEmployees(IFormFile file)
        {

            if (file == null)
            {
                return Result.Failure(new Error("500", "الملف غير موجود للرفع الرجاء التأكد من الملف"));
            }
            UploadFile upload = new UploadFile(file);
            var path = await upload.UploadFileToTempPath();
            NpoiServiceProvider npoi = new NpoiServiceProvider(path);

            //Check Header Row
            var header = npoi.GetHeaders("Sheet1");
            if (!CheckHeaderRow(header))
            {
                return Result.Failure<EmployeeDto>(new Error("500", "الملف غير صالح للرفع الرجاء التأكد من الملف"));
            }
            DataTable dt = npoi.ReadSheetData("Sheet1");
            foreach (DataRow row in dt.Rows)
            {

                var empExist = await _employeeRepository.GetEmployeeByNationalId(row.ItemArray[0].ToString());
                if (empExist == null)
                {
                    var employee = new Employee()
                    {
                        Collage = row.ItemArray[2].ToString(),
                        DepartmentId = null,
                        IsActive = true,
                        Name = row.ItemArray[5].ToString(),
                        NationalId = row.ItemArray[0].ToString(),
                        TabCode = Convert.ToInt32(row.ItemArray[4]),

                    };
                    await _employeeRepository.Insert(employee);
                }
                else
                {
                    empExist.Collage = row.ItemArray[2].ToString();
                    empExist.DepartmentId = null;
                    empExist.IsActive = true;
                    empExist.Name = row.ItemArray[5].ToString();
                    empExist.NationalId = row.ItemArray[0].ToString();
                    empExist.TabCode = Convert.ToInt32(row.ItemArray[4]);
                    _employeeRepository.Update(empExist);
                }
            }
            await _uow.SaveChangesAsync();
            return Result.Success("تم الرفع بنجاح");

        }



        private bool CheckHeaderRow(List<string> header)
        {
            if (header.Contains("الرقم القومى") && header.Contains("القطاع") && header.Contains("الإدارة") && header.Contains("رقم الموظف بجهته الأصلية") && header.Contains("الاسم"))
            {
                return true;
            }
            return false;
        }


    }
}