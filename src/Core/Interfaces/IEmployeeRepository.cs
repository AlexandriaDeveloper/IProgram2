using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Core.Models;

namespace Core.Interfaces
{
    public interface IEmployeeRepository : IGenericRepository<Employee>
    {
        Task<Employee> GetById(string employeeId, bool noTracking = false);
        Task<bool> CheckEmployeeByNationalId(string nationalId);
        Task<Employee> GetEmployeeByNationalId(string nationalId);
        Task<bool> HasBank(string employeeId);
        Task<bool> HasEmployeeReferences(string EmployeeId);
        Task Delete(string id);
        Task DeActive(string id);
    }
}