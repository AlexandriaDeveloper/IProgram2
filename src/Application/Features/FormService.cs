using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Security.Claims;
using System.Text;
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
        private readonly IUnitOfWork _unitOfWork;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IEmployeeRepository _employeeRepository;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IDailyRepository _dailyRepository;
        private readonly IMemoryCache _cache;
        private readonly ICurrentUserService _currentUserService;

        public FormService(
            IFormRepository formRepository,
            IFormDetailsRepository formDetailsRepository,
            IDailyRepository dailyRepository,
            IEmployeeRepository employeeRepository,
            IUnitOfWork unitOfWork,
            IHttpContextAccessor httpContextAccessor,
            UserManager<ApplicationUser> userManager,
            IMemoryCache cache,
            ICurrentUserService currentUserService)
        {
            this._dailyRepository = dailyRepository;
            this._userManager = userManager;
            this._employeeRepository = employeeRepository;
            this._unitOfWork = unitOfWork;
            this._httpContextAccessor = httpContextAccessor;
            this._formRepository = formRepository;
            this._formDetailsRepository = formDetailsRepository;
            this._cache = cache;
            this._currentUserService = currentUserService;
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

            string userId = isAdmin ? null : _currentUserService.UserId;

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



            var result = await _formRepository.ListAllAsync(spec, withInactive: true, trackChanges: false);
            var count = await _formRepository.CountAsync(specCount);

            // Batch-load FormDetails aggregates (count, sum, all-reviewed) in a single grouped query
            var formIds = result.Select(x => x.Id).ToList();
            var rawDetails = await _formDetailsRepository.GetQueryable()
                .Where(fd => formIds.Contains(fd.FormId))
                .Select(fd => new { fd.FormId, fd.Amount, fd.IsReviewed })
                .ToListAsync();

            var aggregates = rawDetails
                .GroupBy(fd => fd.FormId)
                .ToDictionary(
                    g => g.Key,
                    g => new
                    {
                        Count = g.Count(),
                        TotalAmount = g.Sum(fd => fd.Amount),
                        AllReviewed = g.All(fd => fd.IsReviewed)
                    });

            // Batch-load user display names to avoid N+1 blocking calls
            var userIds = result.Select(x => x.CreatedBy).Where(x => x != null).Distinct().ToList();
            var userDisplayNames = new Dictionary<string, string>();
            foreach (var uid in userIds)
            {
                var u = await _userManager.FindByIdAsync(uid);
                if (u != null) userDisplayNames[uid] = u.DisplayName;
            }

            var resultToReturn = result.Select(x =>
            {
                aggregates.TryGetValue(x.Id, out var agg);
                return new FormDto
                {
                    Name = x.Name,
                    Id = x.Id,
                    Index = x.Index,
                    Description = x.Description,
                    DailyId = x.DailyId,
                    Count = agg?.Count ?? 0,
                    TotalAmount = Math.Round(agg?.TotalAmount ?? 0, 2),
                    CreatedBy = x.CreatedBy != null && userDisplayNames.TryGetValue(x.CreatedBy, out var displayName) ? displayName : null,
                    isReviewed = agg?.AllReviewed ?? true,
                    isActive = x.IsActive,
                };
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
                DailyId = form.DailyId.HasValue ? form.DailyId.Value : null,
                CreatedBy = _currentUserService.UserId,
                CreatedAt = DateTime.Now,
                IsActive = true

            };
            await _formRepository.Insert(formToDb);
            var result = await _unitOfWork.SaveChangesAsync() > 0;
            if (!result)
            {
                return Result.Failure<FormDto>(new Error("500", "Internal Server Error"));
            }
            form.Id = formToDb.Id;
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
            .AsNoTracking()
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
            daily.CreatedBy = _currentUserService.UserId;
            daily.CreatedAt = DateTime.Now;

            foreach (var form in json2Obj.Forms)
            {
                var formToAdd = new Form()
                {
                    Description = form.Description,
                    Name = form.Name,
                    Index = form.Index,
                    CreatedAt = DateTime.Now,
                    CreatedBy = _currentUserService.UserId,
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
                        CreatedBy = _currentUserService.UserId,
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

                // Add optional name validation logic
                if (request.ValidateName)
                {
                    string dbNameStr = empExist.Name ?? "";
                    string excelNameStr = row.ItemArray[5]?.ToString() ?? "";

                    if (!IsAdvancedNameMatch(dbNameStr, excelNameStr))
                    {
                        messages.Add($@"يوجد اختلاف في الاسم بالسطر رقم {counter}: مسجل لدينا ({dbNameStr}) وفي الملف ({excelNameStr}) للموظف ({message})");
                        counter++; // Need to increment counter here as well if we are skipping/recording error
                        continue;
                    }
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

        private bool IsAdvancedNameMatch(string dbName, string excelName)
        {
            if (string.IsNullOrWhiteSpace(dbName) || string.IsNullOrWhiteSpace(excelName))
                return false;

            // 1. Normalize
            dbName = NormalizeArabicText(dbName);
            excelName = NormalizeArabicText(excelName);

            // Exact match after normalization
            if (dbName == excelName)
                return true;

            // 2. Split into words
            var dbWords = dbName.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            var excelWords = excelName.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

            // If excel only provided one word, and it doesn't match the first word exactly, 
            // it's too risky to accept just any single word match (e.g. "محمد").
            if (excelWords.Count == 1 && dbWords.Count > 0)
            {
                return IsWordMatch(dbWords[0], excelWords[0]);
            }

            // Determine which list is shorter to use as the baseline
            var shorterList = dbWords.Count <= excelWords.Count ? dbWords : excelWords;
            var longerList = dbWords.Count <= excelWords.Count ? excelWords : dbWords;

            // 3. Count matching words (allowing out-of-order matches)
            int matches = 0;
            bool[] matchedIndices = new bool[longerList.Count];

            foreach (var shortWord in shorterList)
            {
                for (int i = 0; i < longerList.Count; i++)
                {
                    if (!matchedIndices[i] && IsWordMatch(longerList[i], shortWord))
                    {
                        matches++;
                        matchedIndices[i] = true;
                        break; // Move to the next word in the shorter list
                    }
                }
            }

            // 4. Threshold Logic:
            if (shorterList.Count <= 2)
            {
                // For 1 or 2 words, we must match all of them
                return matches == shorterList.Count;
            }
            else
            {
                // For 3+ words, we allow missing 1 word (e.g. matched 3 out of 4, or 2 out of 3)
                // This covers cases where one name has an extra arbitrary 4th name, or is missing a name.
                return matches >= (shorterList.Count - 1);
            }
        }

        private bool IsWordMatch(string dbWord, string excelWord)
        {
            if (dbWord == excelWord) return true;

            // "عبد" handling
            if (dbWord == "عبد" && excelWord.StartsWith("عبد")) return true;
            if (excelWord == "عبد" && dbWord.StartsWith("عبد")) return true;

            if (dbWord == "ابو" && excelWord.StartsWith("ابو")) return true;
            if (excelWord == "ابو" && dbWord.StartsWith("ابو")) return true;

            // Prefix match for abbreviations
            if (dbWord.Length >= 3 && excelWord.Length >= 3)
            {
                if (dbWord.StartsWith(excelWord) || excelWord.StartsWith(dbWord)) return true;

                // Levenshtein Distance for typos (allow 1 mistake for words >= 4 chars)
                if (dbWord.Length >= 4 && excelWord.Length >= 4)
                {
                    int distance = GetLevenshteinDistance(dbWord, excelWord);
                    if (distance <= 1) return true;
                }
            }

            return false;
        }

        private int GetLevenshteinDistance(string s, string t)
        {
            if (string.IsNullOrEmpty(s)) return string.IsNullOrEmpty(t) ? 0 : t.Length;
            if (string.IsNullOrEmpty(t)) return s.Length;

            int n = s.Length;
            int m = t.Length;
            int[,] d = new int[n + 1, m + 1];

            for (int i = 0; i <= n; d[i, 0] = i++) { }
            for (int j = 0; j <= m; d[0, j] = j++) { }

            for (int i = 1; i <= n; i++)
            {
                for (int j = 1; j <= m; j++)
                {
                    int cost = (t[j - 1] == s[i - 1]) ? 0 : 1;
                    d[i, j] = Math.Min(
                        Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                        d[i - 1, j - 1] + cost);
                }
            }
            return d[n, m];
        }

        private string NormalizeArabicText(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return string.Empty;

            // Custom dictionary to map Arabic Presentation Forms (isolated, medial, final, initial) to base characters.
            // This is necessary because some PDF extractors return these presentation forms instead of standard letters.
            var ArabicPresentationFormsMap = new Dictionary<char, char>
            {
                // Alef Forms
                { 'ﺂ', 'ا' }, { 'ﺎ', 'ا' }, { 'ﺄ', 'ا' }, { 'ﺃ', 'ا' },
                { 'ﺈ', 'ا' }, { 'ﺇ', 'ا' }, { 'ﺁ', 'ا' }, { 'ﺍ', 'ا' },

                // Baa Forms
                { 'ﺐ', 'ب' }, { 'ﺒ', 'ب' }, { 'ﺑ', 'ب' }, { 'ﺏ', 'ب' },

                // Taa Forms
                { 'ﺖ', 'ت' }, { 'ﺘ', 'ت' }, { 'ﺗ', 'ت' }, { 'ﺕ', 'ت' },

                // Thaa Forms
                { 'ﺚ', 'ث' }, { 'ﺜ', 'ث' }, { 'ﺛ', 'ث' }, { 'ﺙ', 'ث' },

                // Jeem Forms
                { 'ﺞ', 'ج' }, { 'ﺠ', 'ج' }, { 'ﺟ', 'ج' }, { 'ﺝ', 'ج' },

                // Haa Forms
                { 'ﺢ', 'ح' }, { 'ﺤ', 'ح' }, { 'ﺣ', 'ح' }, { 'ﺡ', 'ح' },

                // Khaa Forms
                { 'ﺦ', 'خ' }, { 'ﺨ', 'خ' }, { 'ﺧ', 'خ' }, { 'ﺥ', 'خ' },

                // Dal Forms
                { 'ﺪ', 'د' }, { 'ﺩ', 'د' },

                // Thal Forms
                { 'ﺬ', 'ذ' }, { 'ﺫ', 'ذ' },

                // Raa Forms
                { 'ﺮ', 'ر' }, { 'ﺭ', 'ر' },

                // Zay Forms
                { 'ﺰ', 'ز' }, { 'ﺯ', 'ز' },

                // Seen Forms
                { 'ﺲ', 'س' }, { 'ﺴ', 'س' }, { 'ﺳ', 'س' }, { 'ﺱ', 'س' },

                // Sheen Forms
                { 'ﺶ', 'ش' }, { 'ﺸ', 'ش' }, { 'ﺷ', 'ش' }, { 'ﺵ', 'ش' },

                // Saad Forms
                { 'ﺺ', 'ص' }, { 'ﺼ', 'ص' }, { 'ﺻ', 'ص' }, { 'ﺹ', 'ص' },

                // Daad Forms
                { 'ﺾ', 'ض' }, { 'ﻀ', 'ض' }, { 'ﺿ', 'ض' }, { 'ﺽ', 'ض' },

                // Taa Forms
                { 'ﻂ', 'ط' }, { 'ﻄ', 'ط' }, { 'ﻃ', 'ط' }, { 'ﻁ', 'ط' },

                // Zhaa Forms
                { 'ﻆ', 'ظ' }, { 'ﻈ', 'ظ' }, { 'ﻇ', 'ظ' }, { 'ﻅ', 'ظ' },

                // Ayn Forms
                { 'ﻊ', 'ع' }, { 'ﻌ', 'ع' }, { 'ﻋ', 'ع' }, { 'ﻉ', 'ع' },

                // Ghayn Forms
                { 'ﻎ', 'غ' }, { 'ﻐ', 'غ' }, { 'ﻏ', 'غ' }, { 'ﻍ', 'غ' },

                // Faa Forms
                { 'ﻒ', 'ف' }, { 'ﻔ', 'ف' }, { 'ﻓ', 'ف' }, { 'ﻑ', 'ف' },

                // Qaaf Forms
                { 'ﻖ', 'ق' }, { 'ﻘ', 'ق' }, { 'ﻗ', 'ق' }, { 'ﻕ', 'ق' },

                // Kaaf Forms
                { 'ﻚ', 'ك' }, { 'ﻜ', 'ك' }, { 'ﻛ', 'ك' }, { 'ﻙ', 'ك' },

                // Laam Forms
                { 'ﻞ', 'ل' }, { 'ﻠ', 'ل' }, { 'ﻟ', 'ل' }, { 'ﻝ', 'ل' },

                // Meem Forms
                { 'ﻢ', 'م' }, { 'ﻤ', 'م' }, { 'ﻣ', 'م' }, { 'ﻡ', 'م' },

                // Noon Forms
                { 'ﻦ', 'ن' }, { 'ﻨ', 'ن' }, { 'ﻧ', 'ن' }, { 'ﻥ', 'ن' },

                // Haa Forms
                { 'ﻪ', 'ه' }, { 'ﻬ', 'ه' }, { 'ﻫ', 'ه' }, { 'ﻩ', 'ه' },

                // Waw Forms
                { 'ﻮ', 'و' }, { 'ﻭ', 'و' },

                // Yaa Forms
                { 'ﻲ', 'ي' }, { 'ﻴ', 'ي' }, { 'ﻳ', 'ي' }, { 'ﻱ', 'ي' },
                { 'ﻰ', 'ي' }, { 'ﯨ', 'ي' }, { 'ﯩ', 'ي' },

                // Hamza Forms
                { 'ﺆ', 'و' }, { 'ﺅ', 'و' }, // Waw with hamza
                { 'ﺊ', 'ي' }, { 'ﺌ', 'ي' }, { 'ﺋ', 'ي' }, { 'ﺉ', 'ي' }, // Yeh with hamza
                { 'ﺀ', 'ا' }, // Lone hamza -> map to alef for simple comparison
            };

            var sb = new StringBuilder(input.Length);
            foreach (char c in input)
            {
                if (ArabicPresentationFormsMap.TryGetValue(c, out char baseChar))
                {
                    sb.Append(baseChar);
                }
                else
                {
                    sb.Append(c);
                }
            }

            input = sb.ToString();

            // Normalize Arabic letters based on User request: أ-ا-إ-ي-ى-ه-ة-ل-ا-أ
            return input
                .Replace("أ", "ا")
                .Replace("إ", "ا")
                .Replace("آ", "ا")
                .Replace("ٱ", "ا")
                .Replace("ى", "ي")  // Replace Alef Maksura with Yeh
                .Replace("ة", "ه")  // Replace Teh Marbuta with Heh
                .Replace("ؤ", "و")
                .Replace("ئ", "ي")
                .Replace("لا", "لا")
                .Replace("ﻷ", "لا")
                .Replace("ﻹ", "لا")
                .Replace("ﻵ", "لا");
        }
    }
}
