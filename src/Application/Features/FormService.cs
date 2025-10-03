using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Application.Dtos;
using Application.Dtos.Requests;
using Application.Helpers;
using Application.Services;

using Core.Interfaces;
using Core.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.FileProviders;
using Newtonsoft.Json;
using NPOI.Util;
using Persistence.Extensions;
using Persistence.Helpers;
using Persistence.Specifications;

namespace Application.Features
{
    public class FormService
    {

        private readonly IFormRepository _formRepository;
        private readonly IFormDetailsRepository _formDetailsRepository;
        private readonly IUniteOfWork _unitOfWork;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IEmployeeRepository _employeeRepository;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IDailyRepository _dailyRepository;
        private readonly IMemoryCache _cache;

        public FormService(IFormRepository formRepository, IFormDetailsRepository formDetailsRepository, IDailyRepository dailyRepository, IEmployeeRepository employeeRepository, IUniteOfWork unitOfWork, IHttpContextAccessor httpContextAccessor, UserManager<ApplicationUser> userManager, IMemoryCache cache)
        {
            this._dailyRepository = dailyRepository;
            this._userManager = userManager;
            this._employeeRepository = employeeRepository;
            this._unitOfWork = unitOfWork;
            this._httpContextAccessor = httpContextAccessor;
            this._formRepository = formRepository;
            this._formDetailsRepository = formDetailsRepository;
            this._cache = cache;
        }

        private void ClearFormCache()
        {
            // Basic cache clearing foundation for future form caching needs
            // Currently no service-level caching in FormService, but prepared for future use
        }

        public async Task<Result<PaginatedResult<FormDto>>> GetForms(int id, FormParam param)
        {


            // determine current user id; if current user is admin, leave user null to fetch all
            var user = _httpContextAccessor.HttpContext.User;
            bool isAdmin = user?.IsInRole("Admin") ?? false;
            string userId = isAdmin ? null : ClaimPrincipalExtensions.RetriveAuthUserIdFromPrincipal(user);

            var spec = new FormSpecification(id, param);
            var specCount = new FormCountSpecification(id, param);

            // If current user is not admin, restrict results to forms created by any admin OR by the current user
            if (!isAdmin && userId != null)
            {
                // get all users in Admin role and use their ids as allowed creators
                var adminUsers = await _userManager.GetUsersInRoleAsync("Admin");
                var adminIds = adminUsers?.Select(a => a.Id).ToList() ?? new List<string>();

                // add current user id to allowed creators
                adminIds.Add(userId);

                var predicate = PredicateBuilder.False<Form>();
                foreach (var adminId in adminIds)
                {
                    predicate = predicate.Or(p => p.CreatedBy == adminId);
                }
                spec.Criterias.Add(predicate);
                specCount.Criterias.Add(predicate);
            }


            var result = await _formRepository.ListAllAsync(spec, withInactive: true);
            var count = await _formRepository.CountAsync(specCount);


            var resultToReturn = result.Select(x => new FormDto
            {
                Name = x.Name,
                Id = x.Id,
                Index = x.Index,
                Description = x.Description,
                DailyId = x.DailyId,
                Count = x.FormDetails.Count,
                TotalAmount = Math.Round(x.FormDetails.Sum(x => x.Amount), 2),
                CreatedBy = _userManager.FindByIdAsync(x.CreatedBy).Result.DisplayName,
                isReviewed = x.FormDetails.All(d => d.IsReviewed),
                isActive = x.IsActive,
            }).ToList();
            var pagedResult = PaginatedResult<FormDto>.Create(resultToReturn, param.PageIndex, param.PageSize, count);
            return Result.Success<PaginatedResult<FormDto>>(pagedResult);
        }



        public async Task<Result<FormDto>> AddForm(FormDto form)
        {
            if (form.DailyId.HasValue && _dailyRepository.IsClosed(form.DailyId.Value))
            {
                return Result.Failure<FormDto>(new Error("500", "هذا اليوم مغلق"));
            }

            var formToDb = new Form
            {
                Index = form.Index,

                Name = form.Name,
                Description = form.Description,
                DailyId = form.DailyId.HasValue ? form.DailyId.Value : null

            };
            await _formRepository.Insert(formToDb);
            var result = await _unitOfWork.SaveChangesAsync() > 0;
            if (!result)
            {
                return Result.Failure<FormDto>(new Error("500", "Internal Server Error"));
            }
            return Result.Success<FormDto>(form);

        }

        public async Task<Result> UpdateForm(int id, FormDto request)
        {
            if (_dailyRepository.IsClosed(request.DailyId.Value))
            {
                return Result.Failure<FormDto>(new Error("500", "هذا اليوم مغلق"));
            }

            var form = await _formRepository.GetById(id);
            if (form == null)
                return Result.Failure(new Error("404", "Not Found"));
            form.Name = request.Name;
            form.DailyId = request.DailyId;
            form.Index = request.Index;
            form.Description = request.Description;
            _formRepository.Update(form);
            var result = await _unitOfWork.SaveChangesAsync() > 0;
            if (result)
                return Result.Success("تم التعديل بنجاح");

            return Result.Failure(new Error("500", "Internal Server Error"));
        }


        public async Task<Result> UpdateDescription(int id, UpdateFormDescriptonRequest request)
        {

            var form = await _formRepository.GetById(id);
            if (_dailyRepository.IsClosed(form.DailyId.Value))
            {
                return Result.Failure<FormDto>(new Error("500", "هذا اليوم مغلق"));
            }
            if (form == null)
                return Result.Failure(new Error("404", "Not Found"));
            form.Description = request.Description;
            _formRepository.Update(form);
            var result = await _unitOfWork.SaveChangesAsync() > 0;
            if (result)
                return Result.Success("تم التعديل بنجاح");
            return Result.Failure(new Error("500", "Internal Server Error"));
        }

        public async Task<Result> MoveFormDailyToArchive(MoveFormRequest request)
        {

            var formFromDb = await _formRepository.GetById(request.FormId);
            // if (_dailyRepository.IsClosed(formFromDb.DailyId.Value))
            // {
            //     return Result.Failure<FormDto>(new Error("500", "هذا اليوم مغلق"));
            // }
            if (formFromDb == null)
                return Result.Failure(new Error("404", "Not Found"));
            formFromDb.DailyId = request.DailyId;
            _formRepository.Update(formFromDb);
            var result = await _unitOfWork.SaveChangesAsync() > 0;
            if (!result)
            {
                return Result.Failure(new Error("500", "Internal Server Error"));
            }
            ClearFormDetailsCache(request.FormId);
            return Result.Success("تم التعديل بنجاح");
        }


        public async Task<Result> SoftDelete(int id)
        {
            var form = await _formRepository.GetById(id);
            if (_dailyRepository.IsClosed(form.DailyId.Value))
            {
                return Result.Failure<FormDto>(new Error("500", "هذا اليوم مغلق"));
            }
            if (form == null)
                return Result.Failure(new Error("404", "Not Found"));

            await _formRepository.DeActive(id);
            var result = await _unitOfWork.SaveChangesAsync() > 0;
            if (result)
                return Result.Success("تم الحذف بنجاح");
            return Result.Failure(new Error("500", "Internal Server Error"));
        }

        public async Task<Result> Delete(int id)
        {
            var form = await _formRepository.GetById(id);
            if (_dailyRepository.IsClosed(form.DailyId.Value))
            {
                return Result.Failure<FormDto>(new Error("500", "هذا اليوم مغلق"));
            }
            if (form == null)
                return Result.Failure(new Error("404", "Not Found"));

            await _formRepository.Delete(id);
            var result = await _unitOfWork.SaveChangesAsync() > 0;
            if (result)
                return Result.Success("تم الحذف بنجاح");
            return Result.Failure(new Error("500", "Internal Server Error"));
        }



        public async Task<MemoryStream> CreateExcelFile(int formId, string title)
        {

            //Get Form Details By FormId Included Data

            var formDetails = _formDetailsRepository.GetQueryable()
            .Include(x => x.Employee)
            .ThenInclude(d => d.Department)
            .Where(x => x.FormId == formId && x.IsActive)
            .OrderBy(x => x.OrderNum)
            .ToList();

            //Convert To DataTable And Add To Excel File
            DataTable dt = new DataTable();
            dt.Columns.Add("م", typeof(int));

            dt.Columns.Add("الرقم القومى", typeof(string));
            dt.Columns.Add("كود طب", typeof(string));
            dt.Columns.Add("كود تجارة", typeof(string));
            dt.Columns.Add("القسم", typeof(string));
            dt.Columns.Add("الاسم", typeof(string));
            dt.Columns.Add("المبلغ", typeof(double));
            dt.Columns.Add("المراجع");

            int counter = 1;
            foreach (var item in formDetails)
            {
                DataRow dr = dt.NewRow();
                dr.SetField("م", counter++);
                dr["الرقم القومى"] = item.Employee.Id;
                dr["كود طب"] = item.Employee.TabCode;
                dr["كود تجارة"] = item.Employee.TegaraCode;
                dr["القسم"] = item.Employee.Department == null ? "" : item.Employee.Department.Name;
                dr["الاسم"] = item.Employee.Name;
                dr.SetField("المبلغ", Math.Round((double)item.Amount, 2));
                //_userManager.FindByIdAsync(item.IsReviewedBy).Result.DisplayName + (item.IsReviewed ? " (تم المراجعة)" : " (لم يتم المراجعة)")
                dr["المراجع"] = item.IsReviewed ? _userManager.FindByIdAsync(item.IsReviewedBy).Result.DisplayName + " ( تم المراجعة بواسطة)" : " (لم يتم المراجعة)";
                dt.Rows.Add(dr);
            }



            var npoi = new NpoiServiceProvider();
            var workbook = await npoi.CreateExcelFile("Sheet1", new string[] { "م", "الرقم القومى", "كود طب", "كود تجارة", "القسم", "الاسم", "المبلغ", "المراجع" }, dt, title);


            string tempPath = Path.GetTempPath();
            string filePath = Path.Combine(tempPath, "Form.xlsx");
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

        public async Task<Result> UploadJSONForm(UploadJsonFormRequest request)
        {

            ///TODO FIX USER
            if (request.File == null)
            {
                return Result.Failure(new Error("500", "الملف غير موجود للرفع الرجاء التأكد من الملف"));
            }
            UploadFile upload = new UploadFile(request.File);
            var path = await upload.UploadFileToTempPath();
            var json = File.ReadAllText(path);

            var json2Obj = JsonConvert.DeserializeObject<JsonDataDto>(json);
            Daily daily = new Daily();
            daily.Name = json2Obj.Name;
            daily.DailyDate = json2Obj.DailyDate;
            daily.Forms = new List<Form>();
            daily.CreatedBy = _httpContextAccessor.HttpContext.User.Claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier).Value;
            daily.CreatedAt = DateTime.Now;

            foreach (var form in json2Obj.Forms)
            {
                var formToAdd = new Form()
                {
                    Description = form.Description,
                    Name = form.Name,
                    Index = form.Index,
                    CreatedAt = DateTime.Now,
                    CreatedBy = _httpContextAccessor.HttpContext.User.Claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier).Value,
                    IsActive = true,

                };
                formToAdd.FormDetails = new List<FormDetails>();

                foreach (var formDetail in form.FormDetails)
                {
                    var empExist = await _employeeRepository.CheckEmployeeByNationalId(formDetail.EmployeeId);
                    if (empExist == false)
                    {
                        return Result.Failure(new Error("500", $" الموظف صاحب الرقم القومى {formDetail.EmployeeId} غير مسجل بقاعدة البيانات "));
                    }

                    formToAdd.FormDetails.Add(new FormDetails()
                    {
                        Amount = formDetail.Amount,
                        EmployeeId = formDetail.EmployeeId,
                        OrderNum = formDetail.OrderNum,
                        CreatedAt = DateTime.Now,
                        CreatedBy = _httpContextAccessor.HttpContext.User.Claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier).Value,
                        IsActive = true
                    });
                }
                daily.Forms.Add(formToAdd);
            }

            await _dailyRepository.Insert(daily);

            var result = await _unitOfWork.SaveChangesAsync() > 0;
            if (result)
                return Result.Success("تم الحفظ بنجاح");
            return Result.Failure(new Error("500", "Internal Server Error"));

        }
        public async Task<Result> UploadExcelEmployeesToForm(UploadEmployeesToFormRequest request)
        {
            if (request.File == null)
            {
                return Result.Failure(new Error("500", "الملف غير موجود للرفع الرجاء التأكد من الملف"));
            }
            UploadFile upload = new UploadFile(request.File);
            var path = await upload.UploadFileToTempPath();
            NpoiServiceProvider npoi = new NpoiServiceProvider(path);
            // Read Excel Sheet and convert it to DataTable
            DataTable dt = npoi.ReadSheeByIndex(0, 1);
            DataTable dt2 = new DataTable();
            dt2.Columns.Add("م", typeof(int));
            dt2.Columns.Add("الرقم القومى", typeof(string));
            dt2.Columns.Add("كود طب", typeof(string));
            dt2.Columns.Add("كود تجارة", typeof(string));
            dt2.Columns.Add("القسم", typeof(string));
            dt2.Columns.Add("الاسم", typeof(string));
            dt2.Columns.Add("المبلغ", typeof(double));
            dt2.Columns.Add("كود الموظف", typeof(string));
            int counter = 1;
            List<string> messages = new List<string>();
            foreach (DataRow row in dt.Rows)
            {
                var message = "";

                Employee empExist = null;
                if (!string.IsNullOrEmpty(row.ItemArray[1].ToString()))
                {
                    empExist = await _employeeRepository.GetQueryable().FirstOrDefaultAsync(x => x.Id == row.ItemArray[1].ToString());
                    message = "الرقم القومى" + row.ItemArray[1].ToString();
                }
                else if (!string.IsNullOrEmpty(row.ItemArray[2].ToString()))
                {
                    var result = int.TryParse(row.ItemArray[2].ToString(), out int id);
                    if (result)
                    {
                        empExist = await _employeeRepository.GetQueryable().FirstOrDefaultAsync(x => x.TabCode == id);
                        message = "كود طب رقم  " + row.ItemArray[2].ToString();
                    }
                }
                else if (!string.IsNullOrEmpty(row.ItemArray[3].ToString()))
                {
                    var result = int.TryParse(row.ItemArray[3].ToString(), out int id);
                    if (result)
                    {
                        empExist = await _employeeRepository.GetQueryable().FirstOrDefaultAsync(x => x.TegaraCode == id);
                        message = "كود تجارة رقم  " + row.ItemArray[3].ToString();
                    }
                }
                if (empExist == null)
                {
                    messages.Add(@"يوجد مشكلة بالبيانات الاتيه   بالسطر رقم " + counter++ + " " + message);
                    continue;
                }
                DataRow dr = dt2.NewRow();
                dr.SetField("م", counter++);
                dr["الرقم القومى"] = empExist.Id;
                dr["كود طب"] = empExist.TabCode;
                dr["كود تجارة"] = empExist.TegaraCode;
                dr["القسم"] = empExist.Department == null ? "" : empExist.Department.Name;
                dr["الاسم"] = empExist.Name;
                dr.SetField("المبلغ", Math.Round(double.Parse(row.ItemArray[6].ToString()), 2));
                dr.SetField("كود الموظف", empExist.Id);
                dt2.Rows.Add(dr);
            }
            if (messages.Count > 0)
            {
                //   return Result.Failure(new Error("1500", " يوجد مشكلة بالبيانات الاتيه  " + string.Join(" |||", messages)));
                return Result.Failure(new Error("1500", System.Text.Json.JsonSerializer.Serialize(messages)));
            }
            var deleteEntity = _formDetailsRepository.GetQueryable().Where(x => x.FormId == request.FormId);
            _formDetailsRepository.DeleteRange(deleteEntity);
            await _unitOfWork.SaveChangesAsync();
            foreach (DataRow row in dt2.Rows)
            {
                var empDetails = new FormDetails();
                empDetails.OrderNum = int.Parse(row.ItemArray[0].ToString());
                empDetails.Amount = Math.Round(double.Parse(row.ItemArray[6].ToString()), 2);
                empDetails.EmployeeId = row.ItemArray[7].ToString();
                empDetails.FormId = request.FormId;
                await _formDetailsRepository.Insert(empDetails);
            }
            ClearFormDetailsCache(request.FormId);
            await _unitOfWork.SaveChangesAsync();

            return Result.Success("تم الرفع بنجاح");
        }
        public async Task<Result<object>> HideForm(int id)
        {
            var form = await _formRepository.GetQueryable().FirstOrDefaultAsync(x => x.Id == id);
            if (form == null) return Result.Failure<object>(new Error("404", "Form not found"));

            form.IsActive = false;
            _formRepository.Update(form);
            await _unitOfWork.SaveChangesAsync();

            return Result.Success("تم اخفاء النموذج بنجاح");
        }

        public async Task<Result<object>> RestoreForm(int id)
        {
            var form = await _formRepository.GetQueryable(null).FirstOrDefaultAsync(x => x.Id == id);
            if (form == null) return Result.Failure<object>(new Error("404", "Form not found"));

            form.IsActive = true;
            _formRepository.Update(form);
            await _unitOfWork.SaveChangesAsync();

            return Result.Success("تم استرجاع النموذج بنجاح");
        }
        private void ClearFormDetailsCache(int formId)
        {
            var cacheKey = $"FormDetails_{formId}";
            _cache.Remove(cacheKey);
        }
    }
}
/*

messages


messages
*/
