using System.Data;
using Application.Dtos;
using Application.Dtos.Requests;
using Application.Helpers;
using Application.Services;
using Application.Shared;
using Application.Shared.ErrorResult;
using Core.Interfaces;
using Core.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Persistence.Extensions;
using Persistence.Helpers;
using Persistence.Specifications;
namespace Application.Features
{
    public class EmployeeService
    {
        private readonly IEmployeeRepository _employeeRepository;
        private readonly IUniteOfWork _uow;
        private readonly IFormDetailsRepository _formDetailsRepository;
        private readonly IConfiguration _config;
        private readonly IHttpContextAccessor _httpContextAccessor;

        public EmployeeService(IEmployeeRepository employeeRepository, IFormDetailsRepository formDetailsRepository

        , IUniteOfWork uow, IConfiguration config, IHttpContextAccessor httpContextAccessor)
        {
            this._config = config;
            this._httpContextAccessor = httpContextAccessor;
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
        public async Task<Result<string>> ConvertNumber(double num)
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
                DepartmentName = employee.Department != null ? employee.Department.Name : "",
                Name = employee.Name,
                NationalId = employee.NationalId,
                TabCode = employee.TabCode,
                TegaraCode = employee.TegaraCode,
                Email = employee.Email,
                Section = employee.Section,
                HasReferences = await _employeeRepository.HasEmployeeReferences(employee.Id),

            };
            if (await _employeeRepository.HasBank(employee.Id))
            {
                employeeToReturn.BankInfo = new EmployeeBankDto()
                {
                    BankName = employee.EmployeeBank != null ? employee.EmployeeBank.BankName : "",
                    BranchName = employee.EmployeeBank != null ? employee.EmployeeBank.BranchName : "",
                    AccountNumber = employee.EmployeeBank != null ? employee.EmployeeBank.AccountNumber : ""
                };


            }
            else
            {
                employeeToReturn.BankInfo = null;
            }

            return Result.Success(employeeToReturn);
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

        public async Task<Result<EmployeeDto>> UpdateEmployee(EmployeeDto employee)
        {
            var employeeFromDb = await _employeeRepository.GetById(employee.Id);
            if (employeeFromDb == null)
            {
                return Result.Failure<EmployeeDto>(new Error("500", "الموظف غير موجود"));
            }

            if (!employeeFromDb.Collage.Equals(employee.Collage))
                employeeFromDb.Collage = employee.Collage;

            if (!employeeFromDb.DepartmentId.Equals(employee.DepartmentId))
                employeeFromDb.DepartmentId = employee.DepartmentId;

            if (!employeeFromDb.TabCode.Equals(employee.TabCode))
                employeeFromDb.TabCode = employee.TabCode;

            if (!employeeFromDb.TegaraCode.Equals(employee.TegaraCode))
                employeeFromDb.TegaraCode = employee.TegaraCode;

            if (!employeeFromDb.Name.Equals(employee.Name))
                employeeFromDb.Name = employee.Name;

            if (!employeeFromDb.NationalId.Equals(employee.NationalId))
                employeeFromDb.NationalId = employee.NationalId;

            _employeeRepository.Update(employeeFromDb);

            var result = await _uow.SaveChangesAsync() > 0;

            if (!result)
            {
                return Result.Failure<EmployeeDto>(new Error("500", "حدث خطأ في تحديث الموظف"));
            }

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
                    FormId = x2.Form.Id,
                    FormName = x2.Form.Name

                }).ToList()
            }).ToList();
            return Result.Success(EmployeeReportDto);


        }

        public async Task<Result> UploadTegaraFile(EmployeeFileUploadRequest file)
        {

            if (file == null)
            {
                return Result.Failure(new Error("500", "الملف غير موجود للرفع الرجاء التأكد من الملف"));
            }
            UploadFile upload = new UploadFile(file.File);
            var path = await upload.UploadFileToTempPath();
            NpoiServiceProvider npoi = new NpoiServiceProvider(path);

            //Check Header Row
            var header = npoi.GetHeaders("Sheet1");
            // if (!CheckHeaderRow(header))
            // {
            //     return Result.Failure<EmployeeDto>(new Error("500", "الملف غير صالح للرفع الرجاء التأكد من الملف"));
            // }
            DataTable dt = npoi.ReadSheetData("Sheet1");
            foreach (DataRow row in dt.Rows)
            {

                var empExist = _employeeRepository.GetQueryable().Include(x => x.EmployeeBank).FirstOrDefault(x => x.NationalId == row.ItemArray[3].ToString());

                if (empExist == null)
                {
                    continue;
                }
                try
                {
                    if (!string.IsNullOrEmpty(row.ItemArray[1].ToString()) && !row.ItemArray[1].ToString().Equals(empExist.TegaraCode))
                    {
                        empExist.TegaraCode = int.Parse(row.ItemArray[1].ToString());
                    }
                    if (!string.IsNullOrEmpty(row.ItemArray[4].ToString()) && !row.ItemArray[4].ToString().Equals(empExist.Section))
                    {
                        empExist.Section = row.ItemArray[4].ToString();
                    }
                    if (!string.IsNullOrEmpty(row.ItemArray[6].ToString()) && row.ItemArray[6].ToString() != empExist.Email)
                    {
                        empExist.Email = row.ItemArray[6].ToString();
                    }
                    if (empExist.EmployeeBank == null && !string.IsNullOrEmpty(row.ItemArray[9].ToString()))
                    {
                        empExist.EmployeeBank = new EmployeeBank();
                        empExist.EmployeeBank.CreatedAt = DateTime.Now;
                        empExist.EmployeeBank.CreatedBy = ClaimPrincipalExtensions.RetriveAuthUserFromPrincipal(_httpContextAccessor.HttpContext.User);
                        empExist.EmployeeBank.IsActive = true;
                    }

                    if (!string.IsNullOrEmpty(row.ItemArray[9].ToString()) && row.ItemArray[9].ToString() != empExist.EmployeeBank.BankName)
                    {
                        empExist.EmployeeBank.BankName = row.ItemArray[9].ToString();
                    }
                    if (!string.IsNullOrEmpty(row.ItemArray[10].ToString()) && row.ItemArray[10].ToString() != empExist.EmployeeBank.BranchName)
                    {
                        empExist.EmployeeBank.BranchName = row.ItemArray[10].ToString();
                    }
                    if (!string.IsNullOrEmpty(row.ItemArray[11].ToString()) && row.ItemArray[11].ToString() != empExist.EmployeeBank.AccountNumber)
                    {
                        empExist.EmployeeBank.AccountNumber = row.ItemArray[11].ToString();
                    }
                    _employeeRepository.Update(empExist);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);

                }

            }
            try
            {
                await _uow.SaveChangesAsync();

            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
            return Result.Success("تم الرفع بنجاح");

        }



        public async Task<Result> UploadTabFile(IFormFile file)
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