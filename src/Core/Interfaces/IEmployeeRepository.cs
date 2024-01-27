using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Core.Models;

namespace Core.Interfaces
{
    public interface IEmployeeRepository : IGenericRepository<Employee>
    {

        Task<bool> CheckEmployeeByNationalId(string nationalId);
        Task<Employee> GetEmployeeByNationalId(string nationalId);
        Task<bool> HasBank(int id);
        Task<bool> HasEmployeeReferences(int EmployeeId);
    }
}