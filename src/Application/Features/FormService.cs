using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Application.Dtos;
using Application.Dtos.Requests;
using Application.Helpers;
using Application.Shared;
using Core.Interfaces;
using Core.Models;
using Persistence.Helpers;
using Persistence.Specifications;

namespace Application.Features
{
    public class FormService
    {

        private readonly IFormRepository _formRepository;
        private readonly IUniteOfWork _unitOfWork;

        public FormService(IFormRepository formRepository, IUniteOfWork unitOfWork)
        {
            this._unitOfWork = unitOfWork;
            this._formRepository = formRepository;
        }

        public async Task<Result<PaginatedResult<FormDto>>> GetForms(int id, FormParam param)
        {
            var spec = new FormSpecification(id, param);
            spec.Includes.Add(x => x.FormDetails);
            var result = await _formRepository.ListAllAsync(spec);
            var count = await _formRepository.CountAsync(new FormCountSpecification(id, param));
            var resultToReturn = result.Select(x => new FormDto
            {
                //  Description = x.Description,
                Name = x.Name,
                Id = x.Id,
                DailyId = x.DailyId,
                Count = x.FormDetails.Count,
                TotalAmount = x.FormDetails.Sum(x => x.Amount)


            }).ToList();
            var pagedResult = PaginatedResult<FormDto>.Create(resultToReturn, param.PageIndex, param.PageSize, count);
            return Result.Success<PaginatedResult<FormDto>>(pagedResult);
        }

        public async Task<Result<FormDto>> AddForm(FormDto form)
        {
            var formToDb = new Form
            {
                Name = form.Name,
                DailyId = form.DailyId

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
            var form = await _formRepository.GetById(id);
            if (form == null)
                return Result.Failure(new Error("404", "Not Found"));
            form.Name = request.Name;
            form.DailyId = request.DailyId;
            _formRepository.Update(form);
            var result = await _unitOfWork.SaveChangesAsync() > 0;
            if (result)
                return Result.Success("تم التعديل بنجاح");
            return Result.Failure(new Error("500", "Internal Server Error"));
        }


        public async Task<Result> UpdateDescription(int id, UpdateFormDescriptonRequest request)
        {
            var form = await _formRepository.GetById(id);
            if (form == null)
                return Result.Failure(new Error("404", "Not Found"));
            form.Description = request.Description;
            _formRepository.Update(form);
            var result = await _unitOfWork.SaveChangesAsync() > 0;
            if (result)
                return Result.Success("تم التعديل بنجاح");
            return Result.Failure(new Error("500", "Internal Server Error"));
        }

        public async Task<Result> SoftDelete(int id)
        {
            var form = await _formRepository.GetById(id);
            if (form == null)
                return Result.Failure(new Error("404", "Not Found"));
            form.IsActive = false;
            _formRepository.Update(form);
            var result = await _unitOfWork.SaveChangesAsync() > 0;
            if (result)
                return Result.Success("تم الحذف بنجاح");
            return Result.Failure(new Error("500", "Internal Server Error"));
        }

    }
}