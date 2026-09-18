using Application.Dtos;
using Application.Dtos.Requests;
using Application.Helpers;
using Core.Interfaces;
using Core.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Persistence.Extensions;
using System.Diagnostics;

using Application.Interfaces;

namespace Application.Features
{
    public class FormDetailsService
    {

        private readonly IFormRepository _formRepository;
        private readonly IFormReferencesRepository _formReferencesRepository;
        private readonly IFormDetailsRepository _formDetailsRepository;
        private readonly IDailyRepository _dailyRepository;
        private readonly IUnitOfWork _unitOfWork;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IMemoryCache _cache;
        private readonly IDbCacheKeyFactory _cacheKeyFactory;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly ILogger<FormDetailsService> _logger;
        private readonly ICurrentUserService _currentUserService;
        private readonly WatchListService _watchListService;
        private readonly IDailyClosureGuard _dailyClosureGuard;

        public FormDetailsService(
            IFormRepository formRepository,
            IFormReferencesRepository formReferencesRepository,
         IFormDetailsRepository formDetailsRepository,
         IDailyRepository dailyRepository,
         IUnitOfWork unitOfWork,
          IHttpContextAccessor httpContextAccessor,
          IMemoryCache cache,
          IDbCacheKeyFactory cacheKeyFactory,
          UserManager<ApplicationUser> userManager,
          ILogger<FormDetailsService> logger,
          ICurrentUserService currentUserService,
          WatchListService watchListService,
          IDailyClosureGuard dailyClosureGuard)
        {
            this._logger = logger;
            this._unitOfWork = unitOfWork;
            this._httpContextAccessor = httpContextAccessor;
            this._formRepository = formRepository;
            this._formReferencesRepository = formReferencesRepository;
            this._formDetailsRepository = formDetailsRepository;
            this._dailyRepository = dailyRepository;
            this._userManager = userManager;
            this._cache = cache;
            this._cacheKeyFactory = cacheKeyFactory;
            this._currentUserService = currentUserService;
            this._watchListService = watchListService;
            this._dailyClosureGuard = dailyClosureGuard;
        }

        private void ClearFormDetailsCache(int formId)
        {
            var cacheKey = _cacheKeyFactory.GetFormDetailsKey(formId);
            _cache.Remove(cacheKey);
        }

        private void ClearFormDetailsCache()
        {
            // In a real scenario, you might want to clear related caches too
            // For now, individual form cache clearing is sufficient
        }

        public async Task<Result<FormDto>> GetFormDetails(int id)
        {
            // Single optimized query: fetch form with active form details and includes
            var cacheKey = _cacheKeyFactory.GetFormDetailsKey(id);
            if (!_cache.TryGetValue(cacheKey, out FormDto formDto))
            {
                var sw = Stopwatch.StartNew();
                _logger.LogInformation($"Starting GetFormDetails for ID {id}");

                formDto = await _formRepository.GetQueryable()
                   .AsNoTracking()
                   .Where(f => f.Id == id)
                   .Select(f => new FormDto
                   {
                       Description = f.Description,
                       Id = f.Id,
                       Index = f.Index,
                       Name = f.Name,
                       FormDetails = f.FormDetails
                           .Where(fd => fd.IsActive)
                           .OrderBy(fd => fd.OrderNum)
                           .Select(fd => new FormDetailsDto
                           {
                               Id = fd.Id,
                               FormId = fd.FormId,

                               Name = fd.Employee.Name,
                               TabCode = fd.Employee.TabCode,
                               TegaraCode = fd.Employee.TegaraCode,
                               Amount = fd.Amount,
                               EmployeeId = fd.EmployeeId,
                               Department = fd.Employee.Department.Name,
                               IsReviewed = fd.IsReviewed,
                               ReviewComments = fd.ReviewComments
                           }).ToList()
                   })
                   .AsSplitQuery()
                   .FirstOrDefaultAsync();

                if (formDto == null)
                {
                    return Result.Failure<FormDto>(new Error("404", "Form not found"));
                }

                var queryTime = sw.ElapsedMilliseconds;
                _logger.LogInformation($"GetFormDetails Query Time for ID {id}: {queryTime}ms. Details Count: {formDto.FormDetails?.Count ?? 0}");

                // Check references in separate async call (can't await inside Select query)
                var swRef = Stopwatch.StartNew();
                formDto.HasReferences = await _formReferencesRepository.CheckFomHaveReferences(id);
                swRef.Stop();
                _logger.LogInformation($"CheckFomHaveReferences Time for ID {id}: {swRef.ElapsedMilliseconds}ms");

                sw.Stop();
                _logger.LogInformation($"Total GetFormDetails Fetch Time for ID {id}: {sw.ElapsedMilliseconds}ms");

                // Cache the result for future requests
                var cacheEntryOptions = new MemoryCacheEntryOptions()
                .SetAbsoluteExpiration(TimeSpan.FromMinutes(10))
                .SetSlidingExpiration(TimeSpan.FromMinutes(5)); // Adjust cache duration as needed

                _cache.Set(cacheKey, formDto, cacheEntryOptions);


            }

            var employeeIds = formDto.FormDetails.Select(x => x.EmployeeId).Distinct().ToList();
            var alerts = await _watchListService.GetActiveAlerts(employeeIds);
            
            foreach(var detail in formDto.FormDetails)
            {
                if(alerts.TryGetValue(detail.EmployeeId, out var alert))
                {
                    detail.WatchListAlert = alert;
                }
            }

            return Result.Success<FormDto>(formDto);

        }

        public async Task<Result> AddEmployeeToFormDetails(FormDetailsRequest form)
        {
            var guard = await _dailyClosureGuard.EnsureFormDailyOpenAsync(form.FormId);
            if (guard.IsFailure)
            {
                return guard;
            }

            //chceck employee exist
            var exist = await _formDetailsRepository.CheckEmployeeFormDetailsExist(form.EmployeeId, form.FormId);
            if (exist)
            {
                return Result.Failure(new Error("400", "عفوا الموظف مسجل بالفعل فى الملف"));
            }

            var formFromDb = await _formDetailsRepository.AddEmployeeToFormDetails(new Core.Models.FormDetails()
            {
                Amount = form.Amount,
                EmployeeId = form.EmployeeId,
                FormId = form.FormId,
                OrderNum = _formDetailsRepository.GetMaxOrderNum(form.FormId) + 1,
                IsActive = true,
                ReviewComments = form.ReviewComments ?? string.Empty

            });
            await _unitOfWork.SaveChangesAsync();

            // Clear cache after adding employee to form
            ClearFormDetailsCache(form.FormId);

            return Result.Success("تم الحفظ بنجاح");
        }

        public async Task<Result> CopyFormToArchive(int id)
        {
            var form = await _formRepository.GetFormWithDetailsByIdAsync(id);
            if (form == null)
            {
                return Result.Failure(new Error("400", "عفوا الملف غير موجود"));
            }
            var copy = new Form
            {
                Name = form.Name,
                Description = form.Description,
                DailyId = null,
                FormDetails = form.FormDetails.Select(x => new FormDetails
                {
                    Amount = x.Amount,
                    EmployeeId = x.EmployeeId,
                    FormId = x.FormId,
                    IsActive = x.IsActive,
                    ReviewComments = x.ReviewComments,
                    IsReviewed = x.IsReviewed,
                    ReviewedAt = x.ReviewedAt,
                    IsReviewedBy = x.IsReviewedBy,
                    OrderNum = x.OrderNum,
                    CreatedAt = DateTime.Now,
                    CreatedBy = _currentUserService.UserId,
                }).ToList()

            };

            await _formRepository.Insert(copy);
            var result = await _unitOfWork.SaveChangesAsync() > 0;
            if (!result)
            {
                return Result.Failure(new Error("500", "Internal Server Error"));
            }
            return Result.Success("تم نسخ الملف بنجاح");
        }
        public async Task<Result> EditEmployeeToFormDetails(FormDetailsRequest form)
        {
            var formDetailsFromDb = await _formDetailsRepository.GetById(form.Id);

            if (formDetailsFromDb == null)
            {
                return Result.Failure(new Error("404", "عفوا التفاصيل غير موجودة"));
            }

            var guard = await _dailyClosureGuard.EnsureFormDailyOpenAsync(formDetailsFromDb.FormId);
            if (guard.IsFailure)
            {
                return guard;
            }

            formDetailsFromDb.Amount = form.Amount;
            if (formDetailsFromDb.EmployeeId != form.EmployeeId)
            {
                var exits = await _formDetailsRepository.CheckEmployeeFormDetailsExist(form.EmployeeId, form.FormId);
                if (exits)
                {
                    return Result.Failure(new Error("400", "عفوا الموظف مسجل بالفعل فى الملف"));
                }
                formDetailsFromDb.EmployeeId = form.EmployeeId;
            }

            formDetailsFromDb.IsReviewed = false;
            formDetailsFromDb.ReviewedAt = null;
            formDetailsFromDb.IsReviewedBy = null;
            if (formDetailsFromDb.ReviewComments != form.ReviewComments)
            {
                if (string.IsNullOrEmpty(form.ReviewComments))
                {
                    formDetailsFromDb.ReviewComments = null;
                }
                else
                {
                    formDetailsFromDb.ReviewComments = form.ReviewComments;
                }

            }

            _formDetailsRepository.Update(formDetailsFromDb);
            var result = await _unitOfWork.SaveChangesAsync() > 0;
            if (!result)
            {
                return Result.Failure(new Error("500", "Internal Server Error"));
            }

            // Clear cache after editing employee in form
            ClearFormDetailsCache(formDetailsFromDb.FormId);

            return Result.Success("تم التعديل بنجاح");
        }

        public async Task<Result> DeleteEmployeeFromFormDetails(int id)
        {
            var formDetailsFromDb = await _formDetailsRepository.GetById(id);
            if (formDetailsFromDb == null)
            {
                return Result.Failure(new Error("404", "عفوا الموظف غير موجود حتى يتم حذفة"));
            }

            var guard = await _dailyClosureGuard.EnsureFormDailyOpenAsync(formDetailsFromDb.FormId);
            if (guard.IsFailure)
            {
                return guard;
            }

            await _formDetailsRepository.Delete(id);
            var result = await _unitOfWork.SaveChangesAsync() > 0;
            if (!result)
            {
                return Result.Failure(new Error("500", "حدث خطأ أثناء الحذف الرجاء المحاوله لاحقا "));
            }

            // Clear cache after deleting employee from form
            ClearFormDetailsCache(formDetailsFromDb.FormId);

            return Result.Success("تم الحذف بنجاح");
        }

        public async Task<Result> ReOrderRows(int formId, int[] formOrderDetailsIds)
        {
            var guard = await _dailyClosureGuard.EnsureFormDailyOpenAsync(formId);
            if (guard.IsFailure)
            {
                return guard;
            }

            var orderDetails = _formDetailsRepository.GetQueryable().Where(x => x.FormId == formId).ToList();
            for (int i = 0; i < formOrderDetailsIds.Length; i++)
            {
                var orderToReOrder = orderDetails.Where(x => x.Id == formOrderDetailsIds[i]).FirstOrDefault();
                if (orderToReOrder != null)
                {
                    orderToReOrder.OrderNum = i;
                    _formDetailsRepository.Update(orderToReOrder);
                }

            }
            await _unitOfWork.SaveChangesAsync();

            // Clear cache after reordering rows
            ClearFormDetailsCache(formId);

            return Result.Success("تم التعديل بنجاح");
        }
        public async Task<Result> MarkFormDetailsAsReviewed(int formDetailsId, bool isReviewed)
        {
            var formDetails = await _formDetailsRepository.GetById(formDetailsId);
            if (formDetails == null)
            {
                return Result.Failure(new Error("404", "عفوا التفاصيل غير موجودة"));
            }

            var guard = await _dailyClosureGuard.EnsureFormDailyOpenAsync(formDetails.FormId);
            if (guard.IsFailure)
            {
                return guard;
            }

            // Get User Id 
            var user = _httpContextAccessor.HttpContext.User;
            var userId = _currentUserService.UserId;
            //check if current user not same id reviewdby and current user not admin return not authorizerd to check out
            if (!user.IsInRole("Admin") && formDetails.CreatedBy != userId)
            {
                return Result.Failure(new Error("403", "عفوا لا يمكنك التحقق من هذا الملف"));
            }

            formDetails.IsReviewed = isReviewed;
            if (isReviewed)
            {
                formDetails.ReviewedAt = DateTime.Now;
                formDetails.IsReviewedBy = userId;
            }
            else
            {
                formDetails.ReviewedAt = null;
                formDetails.IsReviewedBy = null;
            }

            _formDetailsRepository.Update(formDetails);
            var result = await _unitOfWork.SaveChangesAsync() > 0;
            if (!result)
            {
                return Result.Failure(new Error("500", "Internal Server Error"));
            }

            // Clear cache after marking form details as reviewed
            ClearFormDetailsCache(formDetails.FormId);

            return Result.Success("تم وضع علامة المراجعة بنجاح");
        }

        public async Task<Result> MarkFormDetailsAsSummaryReviewed(int formDetailsId, bool isReviewed)
        {
            var formDetails = await _formDetailsRepository.GetById(formDetailsId);
            if (formDetails == null)
            {
                return Result.Failure(new Error("404", "عفوا التفاصيل غير موجودة"));
            }

            var guard = await _dailyClosureGuard.EnsureFormDailyOpenAsync(formDetails.FormId);
            if (guard.IsFailure)
            {
                return guard;
            }

            var user = _httpContextAccessor.HttpContext.User;
            var userId = _currentUserService.UserId;

            if (!user.IsInRole("Admin") && formDetails.CreatedBy != userId)
            {
                return Result.Failure(new Error("403", "عفوا لا يمكنك التحقق من هذا الملف"));
            }

            formDetails.IsSummaryReviewed = isReviewed;
            if (isReviewed)
            {
                formDetails.SummaryReviewedAt = DateTime.Now;
                formDetails.IsSummaryReviewedBy = userId;
                formDetails.SummaryReviewMethod = "Manual";
            }
            else
            {
                formDetails.SummaryReviewedAt = null;
                formDetails.IsSummaryReviewedBy = null;
                formDetails.SummaryReviewMethod = null;
            }

            _formDetailsRepository.Update(formDetails);
            var result = await _unitOfWork.SaveChangesAsync() > 0;
            if (!result)
            {
                return Result.Failure(new Error("500", "Internal Server Error"));
            }

            ClearFormDetailsCache(formDetails.FormId);

            return Result.Success("تم وضع علامة المراجعة بنجاح");
        }


    }
}
