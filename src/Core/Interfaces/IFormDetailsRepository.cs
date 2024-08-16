using Core.Models;

namespace Core.Interfaces
{
    public interface IFormDetailsRepository : IGenericRepository<FormDetails>
    {

        Task<FormDetails> AddEmployeeToFormDetails(FormDetails form);

        int GetMaxOrderNum(int formId);
        Task<bool> CheckEmployeeFormDetailsExist(string employeeId, int formId);

    }
}