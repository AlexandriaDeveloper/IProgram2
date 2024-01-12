using Core.Models;

namespace Core.Interfaces
{
    public interface IFormRepository : IGenericRepository<Form>
    {
        Task<Form> GetFormWithDetailsByIdAsync(int id);
    }



}