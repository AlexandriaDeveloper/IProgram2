
using Application.Dtos;
using Application.Dtos.Requests;
using Application.Helpers;
using Core.Interfaces;
using Core.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Persistence.Extensions;
using Persistence.Helpers;
using Persistence.Specifications;
using Microsoft.EntityFrameworkCore;
using Application.Interfaces;

namespace Application.Features
{
    public class FormArchivedService
    {

        private readonly IFormRepository _formRepository;
        private readonly IUnitOfWork _unitOfWork;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly UserManager<ApplicationUser> _usermanager;
        private readonly ICurrentUserService _currentUserService;
        private readonly IDailyClosureGuard _dailyClosureGuard;

        public FormArchivedService(IFormRepository formRepository, IUnitOfWork unitOfWork, IHttpContextAccessor httpContextAccessor, UserManager<ApplicationUser> usermanager, ICurrentUserService currentUserService, IDailyClosureGuard dailyClosureGuard)
        {
            this._usermanager = usermanager;
            this._unitOfWork = unitOfWork;
            this._httpContextAccessor = httpContextAccessor;
            this._formRepository = formRepository;
            this._currentUserService = currentUserService;
            this._dailyClosureGuard = dailyClosureGuard;
        }
        public async Task<Result<PaginatedResult<FormArchivedDto>>> GetArchivedForms(FormArchivedParam param)
        {

            var user = _httpContextAccessor.HttpContext.User.IsInRole("Admin") ? null :
           _currentUserService.UserId;
            var spec = new ArchivedFormsSpecification(param);
            spec.Includes.Add(x => x.FormDetails);
            var specCount = new ArchivedFormsCountSpecification(param);
            if (user != null)
            {
                spec.Criterias.Add(x => x.CreatedBy == user);
                specCount.Criterias.Add(x => x.CreatedBy == user);
            }

            var result = await _formRepository.ListAllAsync(spec);
            var count = await _formRepository.CountAsync(specCount);

            var creatorIds = result.Select(x => x.CreatedBy).Where(x => !string.IsNullOrEmpty(x)).Distinct().ToList();
            var creatorMap = await _usermanager.Users
                .Where(u => creatorIds.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => u.DisplayName);

            var resultToReturn = result.Select(x => new FormArchivedDto
            {
                Name = x.Name,
                Id = x.Id,
                DailyId = x.DailyId,
                Count = x.FormDetails.Count,
                TotalAmount = Math.Round(x.FormDetails.Sum(x => x.Amount), 2),
                CreatedBy = x.CreatedBy != null && creatorMap.TryGetValue(x.CreatedBy, out var dName) ? dName : null,
            }).ToList();
            var pagedResult = PaginatedResult<FormArchivedDto>.Create(resultToReturn, param.PageIndex, param.PageSize, count);
            return Result.Success<PaginatedResult<FormArchivedDto>>(pagedResult);
        }

        public async Task<Result> MoveFormArchiveToDaily(MoveFromArchiveToDaily request)
        {
            var targetGuard = await _dailyClosureGuard.EnsureDailyOpenAsync(request.DailyId);
            if (targetGuard.IsFailure)
            {
                return targetGuard;
            }

            var forms = new List<Form>();
            foreach (var formId in request.FormIds)
            {
                var form = await _formRepository.GetById(formId);
                if (form == null)
                {
                    return Result.Failure(new Error("404", "Not Found"));
                }
                var guard = await _dailyClosureGuard.ValidateFormDailyOpenAsync(form);
                if (guard.IsFailure)
                {
                    return guard;
                }
                forms.Add(form);
            }

            foreach (var form in forms)
            {
                form.DailyId = request.DailyId;
                _formRepository.Update(form);
            }

            var result = await _unitOfWork.SaveChangesAsync() > 0;
            if (!result)
            {
                return Result.Failure(new Error("500", "Internal Server Error"));
            }
            return Result.Success("تم الحفظ بنجاح");
        }

        public async Task<Result> SoftDelete(int id)
        {
            var form = await _formRepository.GetById(id);
            if (form == null)
                return Result.Failure(new Error("404", "Not Found"));

            var guard = await _dailyClosureGuard.ValidateFormDailyOpenAsync(form);
            if (guard.IsFailure)
            {
                return guard;
            }

            await _formRepository.DeActive(id);
            var result = await _unitOfWork.SaveChangesAsync() > 0;
            if (result)
                return Result.Success("تم الحذف بنجاح");
            return Result.Failure(new Error("500", "Internal Server Error"));
        }

        public async Task<Result> SoftDeleteMultiForms(int[] ids)
        {
            if (ids == null || ids.Length == 0)
            {
                return Result.Failure(new Error("400", "لم يتم تحديد أي استمارات للحذف."));
            }

            var distinctIds = ids.Distinct().ToList();
            var forms = new List<Form>();

            // Phase 1: All-or-nothing business validation before mutating anything
            foreach (var id in distinctIds)
            {
                var form = await _formRepository.GetById(id);
                if (form == null)
                {
                    return Result.Failure(new Error("404", $"الاستمارة رقم {id} غير موجودة."));
                }

                var guard = await _dailyClosureGuard.ValidateFormDailyOpenAsync(form);
                if (guard.IsFailure)
                {
                    return guard;
                }

                forms.Add(form);
            }

            // Phase 2: Execute soft delete on all forms
            foreach (var form in forms)
            {
                await _formRepository.DeActive(form.Id);
            }

            // Phase 3: Single commit
            var result = await _unitOfWork.SaveChangesAsync() > 0;
            if (!result && forms.Count > 0)
            {
                return Result.Failure(new Error("500", "فشلت عملية حفظ الحذف في قاعدة البيانات."));
            }

            return Result.Success("تم الحذف بنجاح");
        }
    }
}