using Core.Models;

namespace Core.Interfaces
{
    public interface IEmployeeBankRepository : IGenericRepository<EmployeeBank>
    {
        Task<EmployeeBank> GetByEmployeeId(string employeeId);

        Task Delete(string employeeId);

    }
}