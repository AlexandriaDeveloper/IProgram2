using Core.Models;

namespace Core.Interfaces
{
    public interface IEmployeeBankRepository : IGenericRepository<EmployeeBank>
    {
        new Task Delete(int id);

    }
}