
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

namespace Application.Features
{
    public class FormArchivedService
    {

        private readonly IFormRepository _formRepository;
        private readonly IUniteOfWork _unitOfWork;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly UserManager<ApplicationUser> _usermanager;

        public FormArchivedService(IFormRepository formRepository, IUniteOfWork unitOfWork, IHttpContextAccessor httpContextAccessor, UserManager<ApplicationUser> usermanager)
        {
            this._usermanager = usermanager;
            this._unitOfWork = unitOfWork;
            this._httpContextAccessor = httpContextAccessor;
            this._formRepository = formRepository;
        }
        public async Task<Result<PaginatedResult<FormArchivedDto>>> GetArchivedForms(FormArchivedParam param)
        {

            var user = _httpContextAccessor.HttpContext.User.IsInRole("Admin") ? null :
           ClaimPrincipalExtensions.RetriveAuthUserIdFromPrincipal(_httpContextAccessor.HttpContext.User);
            var spec = new ArchivedFormsSpecification(param);
            var specCount = new ArchivedFormsCountSpecification(param);
            if (user != null)
            {
                spec.Criterias.Add(x => x.CreatedBy == user);
                specCount.Criterias.Add(x => x.CreatedBy == user);
            }

            var result = await _formRepository.ListAllAsync(spec);
            var count = await _formRepository.CountAsync(new ArchivedFormsCountSpecification(param));
            var resultToReturn = result.Select(x => new FormArchivedDto
            {
                Name = x.Name,
                Id = x.Id,
                DailyId = x.DailyId,
                Count = x.FormDetails.Count,
                TotalAmount = Math.Round(x.FormDetails.Sum(x => x.Amount), 2),
                CreatedBy = _usermanager.FindByIdAsync(x.CreatedBy).Result.DisplayName,
            }).ToList();
            var pagedResult = PaginatedResult<FormArchivedDto>.Create(resultToReturn, param.PageIndex, param.PageSize, count);
            return Result.Success<PaginatedResult<FormArchivedDto>>(pagedResult);
        }

        public async Task<Result> MoveFormArchiveToDaily(MoveFromArchiveToDaily request)
        {

            foreach (var formId in request.FormIds)
            {
                var form = await _formRepository.GetById(formId);
                if (form == null)
                {
                    return Result.Failure(new Error("404", "Not Found"));
                }
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

            await _formRepository.DeActive(id);
            var result = await _unitOfWork.SaveChangesAsync() > 0;
            if (result)
                return Result.Success("تم الحذف بنجاح");
            return Result.Failure(new Error("500", "Internal Server Error"));
        }

        public async Task<Result> SoftDeleteMultiForms(int[] ids)
        {
            foreach (var id in ids)
            {

                await SoftDelete(id);
            }
            return Result.Success("تم الحذف بنجاح");
        }
    }
}