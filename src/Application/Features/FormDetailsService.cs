using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Application.Dtos;
using Application.Helpers;
using Application.Shared;
using Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Application.Features
{
    public class FormDetailsService
    {

        private readonly IFormRepository _formRepository;

        private readonly IFormDetailsRepository _formDetailsRepository;
        private readonly IUniteOfWork _unitOfWork;

        public FormDetailsService(IFormRepository formRepository, IFormDetailsRepository formDetailsRepository, IUniteOfWork unitOfWork)
        {
            this._unitOfWork = unitOfWork;
            this._formRepository = formRepository;
            this._formDetailsRepository = formDetailsRepository;
        }

        public async Task<Result<FormDto>> GetFormDetails(int id)
        {
            var result = await _formRepository.GetFormWithDetailsByIdAsync(id);
            if (result == null)
            {
                return Result.Failure<FormDto>(new Error("404", "Not Found"));
            }
            result.FormDetails = await _formDetailsRepository.GetQueryable().Include(X => X.Employee).Where(x => x.FormId == id).ToListAsync();

            var resultToReturn = new FormDto()
            {
                Description = result.Description,
                Id = result.Id,
                Name = result.Name,
                FormDetails = result.FormDetails.OrderBy(x => x.OrderNum).Select(x => new FormDetailsDto()
                {
                    Id = x.Id,
                    FormId = x.FormId,
                    Name = x.Employee.Name,
                    TabCode = x.Employee.TabCode,
                    TegaraCode = x.Employee.TegaraCode,
                    NationalId = x.Employee.NationalId,
                    Amount = x.Amount,
                    EmployeeId = x.EmployeeId


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