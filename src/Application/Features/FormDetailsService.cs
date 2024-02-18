using Application.Dtos;
using Application.Dtos.Requests;
using Application.Helpers;
using Core.Interfaces;
using Core.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
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

        public FormDetailsService(
            IFormRepository formRepository,
            IFormReferencesRepository formReferencesRepository,
         IFormDetailsRepository formDetailsRepository,
         IUniteOfWork unitOfWork,
          IHttpContextAccessor httpContextAccessor)
        {
            this._unitOfWork = unitOfWork;
            this._httpContextAccessor = httpContextAccessor;
            this._formRepository = formRepository;
            this._formReferencesRepository = formReferencesRepository;
            this._formDetailsRepository = formDetailsRepository;
        }

        public async Task<Result<FormDto>> GetFormDetails(int id)
        {
            var result = await _formRepository.GetFormWithDetailsByIdAsync(id);
            if (result == null)
            {
                return Result.Failure<FormDto>(new Error("404", "Not Found"));
            }
            result.FormDetails = await _formDetailsRepository.GetQueryable().Include(X => X.Employee).ThenInclude(x => x.Department).Where(x => x.FormId == id).ToListAsync();

            var resultToReturn = new FormDto()
            {
                Description = result.Description,
                Id = result.Id,
                Name = result.Name,
                HasReferences = await _formReferencesRepository.CheckFomHaveReferences(result.Id),
                FormDetails = result.FormDetails.OrderBy(x => x.OrderNum).Select(x => new FormDetailsDto()
                {
                    Id = x.Id,
                    FormId = x.FormId,
                    Name = x.Employee.Name,
                    TabCode = x.Employee.TabCode,
                    TegaraCode = x.Employee.TegaraCode,
                    NationalId = x.Employee.NationalId,
                    Amount = x.Amount,
                    EmployeeId = x.EmployeeId,
                    Department = x.Employee?.Department == null ? null : x.Employee?.Department.Name



                }).ToList()
            };

            return Result.Success<FormDto>(resultToReturn);
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
                IsActive = true

            });
            await _unitOfWork.SaveChangesAsync();

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



            _formDetailsRepository.Update(formDetailsFromDb);
            var result = await _unitOfWork.SaveChangesAsync() > 0;
            if (!result)
            {
                return Result.Failure(new Error("500", "Internal Server Error"));
            }

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
            return Result.Success("تم الحذف بنجاح");
        }

        public async Task<Result> ReOrderRows(int formId, int[] formOrderDetailsIds)
        {
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
            return Result.Success("تم التعديل بنجاح");
        }


    }
}