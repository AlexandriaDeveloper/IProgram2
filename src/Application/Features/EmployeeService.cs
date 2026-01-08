using System.Data;
using Application.Dtos;
using Application.Dtos.Requests;
using Application.Helpers;
using Application.Services;
using Core.Interfaces;
using Core.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using NPOI.SS.UserModel;
using Persistence.Extensions;
using Persistence.Helpers;
using Persistence.Specifications;
namespace Application.Features
{
    public class EmployeeService
    {
        private readonly IDepartmentRepository _departmentRepository;
        private readonly IEmployeeRepository _employeeRepository;
        private readonly IUnitOfWork _uow;
        private readonly IFormDetailsRepository _formDetailsRepository;
        private readonly IConfiguration _config;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly ICurrentUserService _currentUserService;


        public EmployeeService(IEmployeeRepository employeeRepository,
         IFormDetailsRepository formDetailsRepository
        , IDepartmentRepository departmentRepository
        , IUnitOfWork uow, IConfiguration config,
        IHttpContextAccessor httpContextAccessor,
        IMemoryCache cache,
        ICurrentUserService currentUserService)
        {
            this._config = config;
            this._httpContextAccessor = httpContextAccessor;
            this._formDetailsRepository = formDetailsRepository;
            this._uow = uow;
            this._employeeRepository = employeeRepository;
            this._departmentRepository = departmentRepository;
            this._currentUserService = currentUserService;
        }
        public async Task<Result<PaginatedResult<EmployeeDto>>> getEmployees(EmployeeParam param)
        {
            var spec = new EmployeeSpecification(param);
            spec.PaginationEnabled = true;

            var employeesFromDb = await _employeeRepository.ListAllAsync(spec, trackChanges: false);
            var employeeToReturn = employeesFromDb.Select(x => new EmployeeDto
            {
                Collage = x.Collage,
                DepartmentId = x.DepartmentId,
                Id = x.Id,
                DepartmentName = x.Department != null ? x.Department.Name : "",
                Name = x.Name,
                TabCode = x.TabCode,
                TegaraCode = x.TegaraCode
            }).ToList();
            var count = await _employeeRepository.CountAsync(new EmployeeCountSpecification(param));
            var employees = PaginatedResult<EmployeeDto>.Create(employeeToReturn.ToList(), param.PageIndex, param.PageSize, count);

            return Result.Success(employees);
        }


        public async Task<Result<EmployeeDto>> getEmployee(EmployeeParam param)
        {
            var spec = new EmployeeSpecification(param);
            spec.PaginationEnabled = false;

            var employee = await _employeeRepository.GetBySpec(spec, trackChanges: false);
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
            var empExist = await _employeeRepository.CheckEmployeeByNationalId(employee.Id);
            if (empExist)
            {
                return Result.Failure<EmployeeDto>(new Error("500", "عفوا الرقم القومى مسجل من قبل"));
            }
            var employeeToDb = new Employee()
            {
                Id = employee.Id,
                Collage = employee.Collage,
                DepartmentId = employee.DepartmentId,
                IsActive = true,
                Name = employee.Name,
                TabCode = employee.TabCode,
                TegaraCode = employee.TegaraCode
            };
            await _employeeRepository.Insert(employeeToDb);

            var result = await _uow.SaveChangesAsync() > 0;

            if (!result)
            {
                return Result.Failure<EmployeeDto>(new Error("500", "حدث خطأ في تحديث الموظف"));
            }

            employee.Id = employeeToDb.Id;

            return Result.Success(employee);

        }

        public async Task<Result<EmployeeDto>> UpdateEmployee(EmployeeDto employee)
        {
            var employeeFromDb = await _employeeRepository.GetById(employee.EmployeeId);
            if (employeeFromDb == null)
            {
                return Result.Failure<EmployeeDto>(new Error("500", "الموظف غير موجود"));
            }


            if (!string.IsNullOrEmpty(employee.Collage) && employeeFromDb.Collage != employee.Collage)
                employeeFromDb.Collage = employee.Collage;

            if (!employeeFromDb.DepartmentId.Equals(employee.DepartmentId))
                employeeFromDb.DepartmentId = employee.DepartmentId;

            if (!employeeFromDb.TabCode.Equals(employee.TabCode))
                employeeFromDb.TabCode = employee.TabCode;

            if (!employeeFromDb.TegaraCode.Equals(employee.TegaraCode))
                employeeFromDb.TegaraCode = employee.TegaraCode;

            if (!string.IsNullOrEmpty(employee.Name) && employeeFromDb.Name != employee.Name)
                employeeFromDb.Name = employee.Name;

            if (!employeeFromDb.Id.Equals(employee.EmployeeId))
                employeeFromDb.Id = employee.EmployeeId;

            if (!string.IsNullOrEmpty(employee.Email) && employeeFromDb.Email != employee.Email)
                employeeFromDb.Email = employee.Email;

            if (!string.IsNullOrEmpty(employee.Section) && employeeFromDb.Section != employee.Section)
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
            .Where(x => x.EmployeeId == request.EmployeeId &&
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
            var emp = await _employeeRepository.GetById(request.EmployeeId);
            if (emp == null)
            {
                return Result.Failure<EmployeeReportDto>(new Error("404", "الموظف غير موجود"));
            }
            EmployeeReportDto.TabCode = emp.TabCode;
            EmployeeReportDto.TegaraCode = emp.TegaraCode;
            EmployeeReportDto.Name = emp.Name;
            EmployeeReportDto.NationalId = emp.Id;


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
                    Amount = Math.Round(x2.Amount, 2),
                    FormId = x2.Form.Id,
                    FormIndex = x2.Form.Index,
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
            int colIndex = -1;
            int tegaraIndex = -1;
            // int tabIndex = -1;
            if (dt.Columns.Contains("الرقم القومى"))
            {
                colIndex = dt.Columns.IndexOf("الرقم القومى");

            }

            else if (dt.Columns.Contains("الرقم القومي"))
            {
                colIndex = dt.Columns.IndexOf("الرقم القومي");

            }

            if (dt.Columns.Contains("كود تجارة"))
            {
                tegaraIndex = dt.Columns.IndexOf("كود تجارة");

            }
            // else if (dt.Columns.Contains("رقم الموظف بجهته الأصلية"))
            // {
            //     colIndex = dt.Columns.IndexOf("رقم الموظف بجهته الأصلية");
            // }
            else if (colIndex == -1)
            {
                return Result.Failure(new Error("500", "الملف غير صالح للرفع الرجاء التأكد من الملف"));
            }
            //  colIndex = 0;
            foreach (DataRow row in dt.Rows)
            {

                if (CheckNullRow(row))
                {
                    continue;
                }
                Employee empExist = null;
                EmployeeBank employeeBankExist = null;
                if (row.ItemArray[colIndex] != null && !string.IsNullOrEmpty(row.ItemArray[colIndex].ToString()))
                    empExist = await _employeeRepository.GetById(row.ItemArray[colIndex].ToString(), true);
                else if (tegaraIndex > -1 && !string.IsNullOrEmpty(row.ItemArray[tegaraIndex].ToString()))
                {
                    bool success = int.TryParse(row.ItemArray[tegaraIndex].ToString(), out int result);
                    if (success)
                    {
                        empExist = await _employeeRepository.GetQueryable().Include(x => x.EmployeeBank).FirstOrDefaultAsync(x => x.TegaraCode == result);
                    }
                }
                if (empExist != null && !string.IsNullOrEmpty(row.ItemArray[colIndex].ToString()) && colIndex > -1)
                {
                    empExist = _employeeRepository.GetQueryable().Include(x => x.EmployeeBank).FirstOrDefault(x => x.Id == row.ItemArray[colIndex].ToString());
                }
                // if (empExist == null && !string.IsNullOrEmpty(row.ItemArray[tegaraIndex].ToString()) && tegaraIndex > -1)
                // {
                //     bool success = int.TryParse(row.ItemArray[tegaraIndex].ToString(), out int result);
                //     if (success)
                //     {
                //         empExist = _employeeRepository.GetQueryable(null).Include(x => x.EmployeeBank).FirstOrDefault(x => x.TegaraCode == result);
                //     }
                // }
                // if (empExist == null && !string.IsNullOrEmpty(row.ItemArray[tabIndex].ToString()) && tabIndex > -1)
                // {
                //     bool success = int.TryParse(row.ItemArray[tabIndex].ToString(), out int result);
                //     if (success)
                //     {
                //         empExist = _employeeRepository.GetQueryable(null).Include(x => x.EmployeeBank).FirstOrDefault(x => x.TabCode == result);
                //     }
                // }
                if (empExist != null && empExist.IsActive == false)
                {
                    return Result.Failure(new Error("400", "هذا الموظف مسجل و  موقوف من قبل"));
                }

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
                return Result.Success("تم الرفع بنجاح");
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return Result.Success("تم الرفع بنجاح");
            }

        }



        //27007091802437
        //2531092501257
        //2531092501257
        private Employee AddEmployee(DataColumnCollection columns, DataRow row)
        {
            var employee = new Employee();

            if (columns.Contains("الرقم القومى") && row["الرقم القومى"] != null)
            {
                employee.Id = row["الرقم القومى"].ToString();
            }
            if (columns.Contains("الرقم القومي") && row["الرقم القومي"] != null)
            {
                employee.Id = row["الرقم القومي"].ToString();
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

            if (columns.Contains("أسم القسم") && row["أسم القسم"] != null)
            {
                var department = _departmentRepository.GetQueryable().FirstOrDefault(x => x.Name == row["أسم القسم"].ToString().Trim());
                if (department != null)
                {
                    employee.DepartmentId = department.Id;
                }

            }
            if (columns.Contains("أسم القسم") && row["أسم القسم"] != null)
            {
                var department = _departmentRepository.GetQueryable().FirstOrDefault(x => x.Name == row["أسم القسم"].ToString().Trim());
                if (department != null)
                {
                    employee.DepartmentId = department.Id;
                }
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
            if (!string.IsNullOrEmpty(row["البنك"].ToString()))
            {
                if (columns.Contains("البنك"))
                {
                    if (employee.EmployeeBank == null)
                        employee.EmployeeBank = new EmployeeBank();

                    employee.EmployeeBank.BankName = row["البنك"].ToString();
                    employee.EmployeeBank.CreatedBy = _currentUserService.UserId;
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
            }

            return employee;

        }
        private Employee UpdateEmployee(DataColumnCollection columns, DataRow row, Employee empExist, out bool hasUpdat)
        {
            hasUpdat = false;


            if (columns.Contains("القطاع") && row["القطاع"] != null && !string.IsNullOrWhiteSpace(row["القطاع"].ToString()))
            {
                if (row["القطاع"].ToString() != empExist.Collage)
                {
                    empExist.Collage = row["القطاع"].ToString();
                    hasUpdat = true;
                }
            }


            if (columns.Contains("كود تجارة") && row["كود تجارة"].ToString() != empExist.TegaraCode.ToString() && !string.IsNullOrEmpty(row["كود تجارة"].ToString()))
            {
                empExist.TegaraCode = int.TryParse(row["كود تجارة"].ToString(), out int code) ? code : null;
                hasUpdat = true;
            }

            if (columns.Contains("كود القسم") && row["كود القسم"] != null && !string.IsNullOrWhiteSpace(row["كود القسم"].ToString()))
            {

                //Get Department Id By Name 


                if (row["كود القسم"].ToString() != empExist.DepartmentId.ToString())
                {
                    var departmentId = GetDepartmentIdByName(row["كود القسم"].ToString());
                    empExist.DepartmentId = int.TryParse(row["كود القسم"].ToString(), out int code) ? code : null;
                    hasUpdat = true;
                }
            }

            if (columns.Contains("أسم القسم") && row["أسم القسم"] != null && !string.IsNullOrWhiteSpace(row["أسم القسم"].ToString()))
            {
                var department = _departmentRepository.GetQueryable().FirstOrDefault(x => x.Name == row["أسم القسم"].ToString().Trim());
                if (department != null)
                {

                    empExist.DepartmentId = department.Id;
                    hasUpdat = true;

                }

            }


            if (columns.Contains("الإدارة") && row["الإدارة"] != null && !string.IsNullOrWhiteSpace(row["الإدارة"].ToString()))
            {
                if (row["الإدارة"].ToString() != empExist.Section)
                {
                    empExist.Section = row["الإدارة"].ToString();
                    hasUpdat = true;
                }
            }




            if (columns.Contains("الايميل") && row["الايميل"] != null && !string.IsNullOrWhiteSpace(row["الايميل"].ToString()))
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
                    empExist.EmployeeBank.EmployeeId = empExist.Id;
                    empExist.EmployeeBank.CreatedAt = DateTime.Now;
                    empExist.EmployeeBank.CreatedBy = _currentUserService.UserId;
                    empExist.EmployeeBank.IsActive = true;
                    empExist.EmployeeBank.BankName = row["البنك"].ToString();
                    if (!string.IsNullOrWhiteSpace(row["الفرع"].ToString()))
                    {
                        empExist.EmployeeBank.BranchName = row["الفرع"].ToString();
                        hasUpdat = true;
                    }
                    if (!string.IsNullOrWhiteSpace(row["رقم الحساب"].ToString()))
                    {
                        empExist.EmployeeBank.AccountNumber = row["رقم الحساب"].ToString();
                        hasUpdat = true;
                    }


                    hasUpdat = true;
                }
                else if (empExist.EmployeeBank != null)
                {
                    if (!string.IsNullOrWhiteSpace(row["البنك"].ToString()) && row["البنك"].ToString() != empExist.EmployeeBank.BankName)
                    {
                        empExist.EmployeeBank.BankName = row["البنك"].ToString();
                        hasUpdat = true;
                    }
                    if (!string.IsNullOrWhiteSpace(row["الفرع"].ToString()) && row["الفرع"].ToString() != empExist.EmployeeBank.BranchName)
                    {
                        empExist.EmployeeBank.BranchName = row["الفرع"].ToString();
                        hasUpdat = true;
                    }
                    if (!string.IsNullOrWhiteSpace(row["رقم الحساب"].ToString()) && row["رقم الحساب"].ToString() != empExist.EmployeeBank.AccountNumber)
                    {
                        empExist.EmployeeBank.AccountNumber = row["رقم الحساب"].ToString();
                        hasUpdat = true;
                    }
                }


            return empExist;



        }


        private bool CheckNullRow(DataRow row)
        {

            foreach (var cell in row.ItemArray)
            {
                if (!string.IsNullOrWhiteSpace(cell.ToString()))
                {
                    return false;
                }

            }
            return true;
        }



        private bool CheckHeaderRow(List<string> header)
        {
            string[] allowerColumns = ["رقم الموظف بجهته الأصلية", "كود تجارة","الاسم","المرتب","نوع المدفوعه","الرقم القومي","بطاقات","بنكية","تاريخ التعديل",
            "الرقم القومى","الإدارة","القطاع","الايميل","البنك","الفرع","رقم الحساب","كود القسم","اسم القسم","أسم القسم"];


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

        public async Task<Result> SoftDelete(string id)
        {
            var employee = await _employeeRepository.GetById(id);
            if (employee == null)
            {
                return Result.Failure(new Error("404", "الموظف غير موجود"));
            }
            employee.IsActive = false;
            employee.DeactivatedAt = DateTime.Now;
            employee.DeactivatedBy = _currentUserService.UserId;
            _employeeRepository.Update(employee);
            var result = await _uow.SaveChangesAsync() > 0;
            if (!result)
            {
                return Result.Failure(new Error("500", "حدث خطأ في عملية الحذف"));
            }
            return Result.Success("تم حذف الموظف بنجاح");
        }

        public async Task<Result> Delete(string id)
        {
            var employee = await _employeeRepository.GetById(id);
            if (employee == null)
            {
                return Result.Failure(new Error("404", "الموظف غير موجود"));
            }

            await _employeeRepository.Delete(id);
            var result = await _uow.SaveChangesAsync() > 0;

            if (!result)
            {
                return Result.Failure(new Error("500", "حدث خطأ في عملية الحذف"));
            }
            return Result.Success("تم حذف الموظف بنجاح");
        }


        public async Task<MemoryStream> DownloadAllEmployees(DownloadAllEmployeesParam employeeParam)
        {
            var spec = new DownloadAllEmployeeSpecification(employeeParam);
            spec.PaginationEnabled = false;
            var employees = await _employeeRepository.ListAllAsync(spec, trackChanges: false);
            // var departments = await _departmentRepository.ListAllAsync();
            var npoi = new NpoiServiceProvider();
            IWorkbook workbook = null;
            int i = 1;




            DataTable dt2 = new DataTable();
            dt2.Columns.Add("رقم الموظف بجهته الأصلية", typeof(string));
            dt2.Columns.Add("كود تجارة", typeof(string));
            dt2.Columns.Add("الاسم", typeof(string));
            dt2.Columns.Add("الرقم القومى", typeof(string));
            dt2.Columns.Add("الإدارة", typeof(string));
            dt2.Columns.Add("القطاع", typeof(string));
            dt2.Columns.Add("أسم القسم", typeof(string));
            dt2.Columns.Add("الايميل", typeof(string));
            dt2.Columns.Add("البنك", typeof(string));
            dt2.Columns.Add("الفرع", typeof(string));
            dt2.Columns.Add("رقم الحساب", typeof(string));

            foreach (var employee in employees)
            {
                DataRow dr = dt2.NewRow();
                dr["رقم الموظف بجهته الأصلية"] = employee.TabCode.HasValue ? employee.TabCode.Value.ToString() : string.Empty;
                dr["كود تجارة"] = employee.TegaraCode.HasValue ? employee.TegaraCode.Value.ToString() : string.Empty;
                dr["الاسم"] = employee.Name;
                dr["الرقم القومى"] = employee.Id.ToString();
                dr["الإدارة"] = employee.Section;
                dr["القطاع"] = employee.Collage;
                dr["أسم القسم"] = employee.DepartmentId.HasValue ? employee.Department.Name : string.Empty;
                dr["الايميل"] = employee.Email;
                if (employee.EmployeeBank != null)
                {
                    dr["البنك"] = employee.EmployeeBank.BankName;
                    dr["الفرع"] = employee.EmployeeBank.BranchName;
                    dr["رقم الحساب"] = employee.EmployeeBank.AccountNumber.ToString();
                }
                else
                {
                    dr["البنك"] = string.Empty;
                    dr["الفرع"] = string.Empty;
                    dr["رقم الحساب"] = string.Empty;
                }
                dt2.Rows.Add(dr);

            }

            workbook = await npoi.CreateExcelFile("Sheet1", new string[] { "رقم الموظف بجهته الأصلية", "كود تجارة", "الاسم", "الرقم القومى", "الإدارة", "القطاع", "أسم القسم", "الايميل", "البنك", "الفرع", "رقم الحساب" }, dt2);



            if (workbook == null)
            {
                return null;
            }
            string tempPath = Path.GetTempPath();
            string filePath = Path.Combine(tempPath, Path.GetTempFileName() + ".xlsx");
            var memory = new MemoryStream();

            FileStream fs;
            using (var ms = new MemoryStream())
            {
                fs = new FileStream(filePath, System.IO.FileMode.Create);
                workbook.Write(fs);
            }

            await using (var stream = new FileStream(filePath, FileMode.Open))
            {
                await stream.CopyToAsync(memory);
            }
            memory.Position = 0;

            return memory;

        }

        public async Task<List<string>> GetSectionsName()
        {
            var sections = await _employeeRepository.GetQueryable()
                .Where(x => !string.IsNullOrEmpty(x.Section))
                .Select(x => x.Section)
                .Distinct()
                .ToListAsync();

            return sections;
        }

        public async Task<List<string>> GetCollagesName()
        {
            var collages = await _employeeRepository.GetQueryable()
                .Where(x => !string.IsNullOrEmpty(x.Collage))
                .Select(x => x.Collage)
                .Distinct()
                .ToListAsync();

            return collages;
        }



        private int? GetDepartmentIdByName(string name)
        {
            var department = _departmentRepository.GetQueryable().FirstOrDefault(x => x.Name == name);
            if (department != null)
                return department.Id;
            return null;



        }




    }
}
