using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Application.Dtos;
using Application.Helpers;

using Core.Interfaces;
using Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Application.Features
{
    public class EmployeeBankService
    {
        private readonly IUniteOfWork _uow;
        private readonly IEmployeeBankRepository _employeeBankRepository;
        public EmployeeBankService(IEmployeeBankRepository employeeBankRepository, IUniteOfWork uow)
        {
            this._employeeBankRepository = employeeBankRepository;
            this._uow = uow;

        }

        public async Task<Result> AddEmployeeBank(EmployeeBankDto employeeBank)
        {
            var employeeBankToSave = new EmployeeBank()
            {
                BankName = employeeBank.BankName,
                BranchName = employeeBank.BranchName,
                EmployeeId = employeeBank.EmployeeId,
                AccountNumber = employeeBank.AccountNumber
            };
            await _employeeBankRepository.Insert(employeeBankToSave);
            var result = await _uow.SaveChangesAsync() > 0;
            if (!result)
            {
                return Result.Failure(new Error("500", "حدث خطأ في عملية الحفظ"));
            }
            return Result.Success("تمت عملية الحفظ بنجاح");
        }


        public async Task<Result> DeleteEmployeeBank(string employeeId)
        {
            var employeeBank = await _employeeBankRepository.GetQueryable().Where(x => x.EmployeeId == employeeId).FirstOrDefaultAsync();
            if (employeeBank == null)
            {
                return Result.Failure(new Error("500", "حدث خطأ في عملية الحذف"));
            }
            await _employeeBankRepository.Delete(employeeId);
            var result = await _uow.SaveChangesAsync() > 0;
            if (!result)
            {
                return Result.Failure(new Error("500", "حدث خطأ في عملية الحذف"));
            }
            return Result.Success("تمت عملية الحذف بنجاح");
        }
        public async Task<Result> GetById(int id)
        {
            var employeeBank = await _employeeBankRepository.GetById(id);
            if (employeeBank == null)
            {
                return Result.Failure(new Error("500", "حدث خطأ في عملية الحذف"));
            }
            var employeeBankToReturn = new EmployeeBankDto()
            {
                BankName = employeeBank.BankName,
                BranchName = employeeBank.BranchName,
                AccountNumber = employeeBank.AccountNumber,

                EmployeeId = employeeBank.EmployeeId
            };

            return Result.Success(employeeBankToReturn);
        }

        public async Task<Result> GetByEmployeeId(string employeeId)
        {
            var employeeBank = await _employeeBankRepository.GetQueryable().Where(x => x.EmployeeId == employeeId).FirstOrDefaultAsync();
            if (employeeBank == null)
            {
                return Result.Failure(new Error("500", "حدث خطأ في عملية الحذف"));
            }
            var employeeBankToReturn = new EmployeeBankDto()
            {
                BankName = employeeBank.BankName,
                BranchName = employeeBank.BranchName,
                AccountNumber = employeeBank.AccountNumber,

                EmployeeId = employeeBank.EmployeeId
            };

            return Result.Success(employeeBankToReturn);
        }
    }
}