using Application.Dtos;
using Application.Dtos.Requests;
using Application.Helpers;
using Core.Interfaces;
using Core.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Persistence.Extensions;

namespace Application.Features
{
    public class FormDetailsService
    {

        private readonly IFormRepository _formRepository;
        private readonly IFormReferencesRepository _formReferencesRepository;
        private readonly IFormDetailsRepository _formDetailsRepository;
        private readonly IUniteOfWork _unitOfWork;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IMemoryCache _cache;
        private readonly UserManager<ApplicationUser> _userManager;

        public FormDetailsService(
            IFormRepository formRepository,
            IFormReferencesRepository formReferencesRepository,
         IFormDetailsRepository formDetailsRepository,
         IDailyRepository dailyRepository,
         IUniteOfWork unitOfWork,
          IHttpContextAccessor httpContextAccessor,
          IMemoryCache cache,
          UserManager<ApplicationUser> userManager)
        {
            this._unitOfWork = unitOfWork;
            this._httpContextAccessor = httpContextAccessor;
            this._formRepository = formRepository;
            this._formReferencesRepository = formReferencesRepository;
            this._formDetailsRepository = formDetailsRepository;
            this._userManager = userManager;
            this._cache = cache;
        }

        private void ClearFormDetailsCache(int formId)
        {
            var cacheKey = $"FormDetails_{formId}";
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
            var cacheKey = $"FormDetails_{id}";
            if (!_cache.TryGetValue(cacheKey, out FormDto formDto))
            {
                formDto = await _formRepository.GetQueryable()
                   .Where(f => f.Id == id)
                   .Select(f => new FormDto
                   {
                       Description = f.Description,
                       Id = f.Id,
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
                   .FirstOrDefaultAsync();

                if (formDto == null)
                {
                    return Result.Failure<FormDto>(new Error("404", "Form not found"));
                }

                // Check references in separate async call (can't await inside Select query)
                formDto.HasReferences = await _formReferencesRepository.CheckFomHaveReferences(id);
                // Cache the result for future requests
                var cacheEntryOptions = new MemoryCacheEntryOptions()
                .SetAbsoluteExpiration(TimeSpan.FromMinutes(10))
                .SetSlidingExpiration(TimeSpan.FromMinutes(5)); // Adjust cache duration as needed

                _cache.Set(cacheKey, formDto, cacheEntryOptions);


            }
            return Result.Success<FormDto>(formDto);

        }

        public async Task<Result> AddEmployeeToFormDetails(FormDetailsRequest form)
        {
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
                    CreatedBy = ClaimPrincipalExtensions.RetriveAuthUserIdFromPrincipal(_httpContextAccessor.HttpContext.User), //_httpContextAccessor.HttpContext.User.Claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier).Value
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
                return Result.Failure(new Error("400", "عفوا الموظف مسجل بالفعل فى الملف"));
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
            // var form = _formRepository.GetQueryable().Include(x => x.Daily).FirstOrDefault(x => x.Id == formId);
            // if (form.Daily.Closed)
            // {
            //     return Result.Failure(new Error("500", "اليوميه مغلقه"));
            // }

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

            // Get User Id 
            var user = _httpContextAccessor.HttpContext.User;
            var userId = ClaimPrincipalExtensions.RetriveAuthUserIdFromPrincipal(user);
            //check if current user not same id reviewdby and current user not admin return not authorizerd to check out
            if (!user.IsInRole("Admin") && formDetails.CreatedBy != userId)
            {
                return Result.Failure(new Error("403", "عفوا لا يمكنك التحقق من هذا الملف"));
            }
            if (formDetails == null)
            {
                return Result.Failure(new Error("404", "عفوا التفاصيل غير موجودة"));
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


    }
}
