using System.Data;
using Application.Dtos;
using Application.Dtos.Requests;
using Application.Helpers;
using Application.Services;
using Core.Interfaces;
using Core.Models;
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
                DepartmentName = x.Department != null ? x.Department.Name : "",

                Name = x.Name,
                NationalId = x.NationalId,
                TabCode = x.TabCode,
                TegaraCode = x.TegaraCode
            }).ToList();
            var count = await _employeeRepository.CountAsync(new EmployeeCountSpecification(param));
            var result = PaginatedResult<EmployeeDto>.Create(employeeToReturn.ToList(), param.PageIndex, param.PageSize, count);
            return Result.Success(result);
        }


        public async Task<Result<EmployeeDto>> getEmployee(EmployeeParam param)
        {
            var spec = new EmployeeSpecification(param);
            spec.PaginationEnabled = false;

            var employee = await _employeeRepository.GetBySpec(spec);
            if (employee == null)
            {
                return Result.Failure<EmployeeDto>(new Error("404", "عفوا الموظف غير موجود"));
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
                    AccountNumber = employee.EmployeeBank != null ? employee.EmployeeBank.AccountNumber : "",
                    EmployeeId = employee.Id,

                };


            }
            else
            {
                employeeToReturn.BankInfo = null;
            }

            return Result.Success<EmployeeDto>(employeeToReturn);
        }
        public async Task<Result<EmployeeDto>> AddEmployee(EmployeeDto employee, CancellationToken cancellationToken)
        {
            var empExist = await _employeeRepository.CheckEmployeeByNationalId(employee.NationalId);
            if (empExist)
            {
                return Result.Failure<EmployeeDto>(new Error("500", "عفوا الرقم القومى مسجل من قبل"));
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

            if (!string.IsNullOrEmpty(employee.Email) && employeeFromDb.Email != employee.Email)
                employeeFromDb.Email = employee.Email;

            if (!employeeFromDb.Section.Equals(employee.Section))
                employeeFromDb.Section = employee.Section;

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
            var header = npoi.GetHeadersByIndex(0);
            if (!CheckHeaderRow(header))
            {
                return Result.Failure<EmployeeDto>(new Error("500", "الملف غير صالح للرفع الرجاء التأكد من الملف"));
            }
            DataTable dt = npoi.ReadSheeByIndex(0);
            int colIndex = 0;

            if (dt.Columns.Contains("الرقم القومى"))
            {
                colIndex = dt.Columns.IndexOf("الرقم القومى");
            }
            else if (dt.Columns.Contains("الرقم القومي"))
            {
                colIndex = dt.Columns.IndexOf("الرقم القومي");
            }
            if (colIndex == 0)
            {
                return Result.Failure(new Error("500", "الملف غير صالح للرفع الرجاء التأكد من الملف"));
            }

            foreach (DataRow row in dt.Rows)
            {

                var empExist = _employeeRepository.GetQueryable().Include(x => x.EmployeeBank).FirstOrDefault(x => x.NationalId == row.ItemArray[colIndex].ToString());

                if (empExist == null)
                {
                    // continue;
                    await _employeeRepository.Insert(AddEmployee(dt.Columns, row));
                }
                else
                {
                    bool hasUpdat = false;
                    UpdateEmployee(dt.Columns, row, empExist, out hasUpdat);
                    if (hasUpdat)
                        _employeeRepository.Update(empExist);
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





        private Employee AddEmployee(DataColumnCollection columns, DataRow row)
        {
            var employee = new Employee();

            if (columns.Contains("الرقم القومى") && row["الرقم القومى"] != null)
            {
                employee.NationalId = row["الرقم القومى"].ToString();
            }
            if (columns.Contains("الرقم القومي") && row["الرقم القومي"] != null)
            {
                employee.NationalId = row["الرقم القومي"].ToString();
            }

            if (columns.Contains("الاسم") && row["الاسم"] != null)
            {
                employee.Name = row["الاسم"].ToString();
            }
            if (columns.Contains("الايميل") && row["الايميل"] != null)
            {
                employee.Email = row["الايميل"].ToString();
            }
            if (columns.Contains("كود تجارة") && row["كود تجارة"] != null)
            {
                employee.TegaraCode = int.TryParse(row["كود تجارة"].ToString(), out int code) ? code : null;
            }
            if (columns.Contains("كود القسم") && row["كود القسم"] != null)
            {
                employee.DepartmentId = int.TryParse(row["كود القسم"].ToString(), out int code) ? code : null;
            }

            if (columns.Contains("رقم الموظف بجهته الأصلية") && row["رقم الموظف بجهته الأصلية"] != null)
            {
                employee.TabCode = int.TryParse(row["رقم الموظف بجهته الأصلية"].ToString(), out int code) ? code : null;
            }
            if (columns.Contains("القطاع") && row["القطاع"] != null)
            {
                employee.Collage = row["القطاع"].ToString();
            }
            if (columns.Contains("الإدارة") && row["الإدارة"] != null)
            {
                employee.Section = row["الإدارة"].ToString();
            }
            if (columns.Contains("البنك") && row["البنك"] != null)
            {
                if (employee.EmployeeBank == null)
                    employee.EmployeeBank = new EmployeeBank();

                employee.EmployeeBank.BankName = row["البنك"].ToString();
                employee.EmployeeBank.CreatedBy = ClaimPrincipalExtensions.RetriveAuthUserIdFromPrincipal(_httpContextAccessor.HttpContext.User);
                employee.EmployeeBank.CreatedAt = DateTime.Now;
            }
            if (columns.Contains("الفرع") && row["الفرع"] != null)
            {
                if (employee.EmployeeBank == null)
                    employee.EmployeeBank = new EmployeeBank();

                employee.EmployeeBank.BranchName = row["الفرع"].ToString();
            }
            if (columns.Contains("رقم الحساب") && row["رقم الحساب"] != null)
            {
                if (employee.EmployeeBank == null)
                    employee.EmployeeBank = new EmployeeBank();

                employee.EmployeeBank.AccountNumber = row["رقم الحساب"].ToString();

            }

            return employee;

        }
        private Employee UpdateEmployee(DataColumnCollection columns, DataRow row, Employee empExist, out bool hasUpdat)
        {
            hasUpdat = false;
            if (columns.Contains("القطاع") && row["القطاع"] != null)
            {
                if (row["القطاع"].ToString() != empExist.Collage)
                {
                    empExist.Collage = row["القطاع"].ToString();
                    hasUpdat = true;
                }
            }
            if (columns.Contains("كود تجارة") && row["كود تجارة"] != null)
            {
                if (row["كود تجارة"].ToString() != empExist.TegaraCode.ToString())
                {
                    empExist.TegaraCode = int.TryParse(row["كود تجارة"].ToString(), out int code) ? code : null;
                    hasUpdat = true;
                }
            }
            if (columns.Contains("كود القسم") && row["كود القسم"] != null)
            {
                if (row["كود القسم"].ToString() != empExist.DepartmentId.ToString())
                {
                    empExist.DepartmentId = int.TryParse(row["كود القسم"].ToString(), out int code) ? code : null;
                    hasUpdat = true;
                }
            }

            if (columns.Contains("الإدارة") && row["الإدارة"] != null)
            {
                if (row["الإدارة"].ToString() != empExist.Section)
                {
                    empExist.Section = row["الإدارة"].ToString();
                    hasUpdat = true;
                }
            }

            if (columns.Contains("الايميل") && row["الايميل"] != null)
            {
                if (row["الايميل"].ToString() != empExist.Email)
                {
                    empExist.Email = row["الايميل"].ToString();
                    hasUpdat = true;
                }
            }
            if (columns.Contains("البنك") && row["البنك"] != null)

                if (empExist.EmployeeBank == null && !string.IsNullOrEmpty(row["البنك"].ToString()))
                {
                    empExist.EmployeeBank = new EmployeeBank();
                    empExist.EmployeeBank.CreatedAt = DateTime.Now;
                    empExist.EmployeeBank.CreatedBy = ClaimPrincipalExtensions.RetriveAuthUserIdFromPrincipal(_httpContextAccessor.HttpContext.User);
                    empExist.EmployeeBank.IsActive = true;
                    empExist.EmployeeBank.BankName = row["البنك"].ToString();
                    if (!string.IsNullOrEmpty(row["الفرع"].ToString()))
                    {
                        empExist.EmployeeBank.BranchName = row["الفرع"].ToString();
                        hasUpdat = true;
                    }
                    if (!string.IsNullOrEmpty(row["رقم الحساب"].ToString()))
                    {
                        empExist.EmployeeBank.AccountNumber = row["رقم الحساب"].ToString();
                        hasUpdat = true;
                    }


                    hasUpdat = true;
                }
                else if (empExist.EmployeeBank != null)
                {
                    if (!string.IsNullOrEmpty(row["البنك"].ToString()) && row["البنك"].ToString() != empExist.EmployeeBank.BankName)
                    {
                        empExist.EmployeeBank.BankName = row["البنك"].ToString();
                        hasUpdat = true;
                    }
                    if (!string.IsNullOrEmpty(row["الفرع"].ToString()) && row["الفرع"].ToString() != empExist.EmployeeBank.BranchName)
                    {
                        empExist.EmployeeBank.BranchName = row["الفرع"].ToString();
                        hasUpdat = true;
                    }
                    if (!string.IsNullOrEmpty(row["رقم الحساب"].ToString()) && row["رقم الحساب"].ToString() != empExist.EmployeeBank.AccountNumber)
                    {
                        empExist.EmployeeBank.AccountNumber = row["رقم الحساب"].ToString();
                        hasUpdat = true;
                    }
                }


            return empExist;



        }




        private bool CheckHeaderRow(List<string> header)
        {
            string[] allowerColumns = ["رقم الموظف بجهته الأصلية", "كود تجارة","الاسم","المرتب","نوع المدفوعه","الرقم القومي","بطاقات","بنكية","تاريخ التعديل",
            "الرقم القومى","الإدارة","القطاع","الايميل","البنك","الفرع","رقم الحساب","كود القسم"];


            var fileAccepted = true;
            foreach (string col in header)
            {
                if (!allowerColumns.Contains(col))
                {
                    fileAccepted = false;
                }
            }
            return fileAccepted;
        }

        public async Task<Result> SoftDelete(int id)
        {
            var employee = await _employeeRepository.GetById(id);
            if (employee == null)
            {
                return Result.Failure(new Error("404", "الموظف غير موجود"));
            }
            employee.IsActive = false;
            employee.DeactivatedAt = DateTime.Now;
            employee.DeactivatedBy = ClaimPrincipalExtensions.RetriveAuthUserIdFromPrincipal(_httpContextAccessor.HttpContext.User);
            _employeeRepository.Update(employee);
            var result = await _uow.SaveChangesAsync() > 0;
            if (!result)
            {
                return Result.Failure(new Error("500", "حدث خطأ في عملية الحذف"));
            }
            return Result.Success("تم حذف الموظف بنجاح");
        }
    }
}