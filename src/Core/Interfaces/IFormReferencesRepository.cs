using Core.Models;

namespace Core.Interfaces
{
    public interface IFormReferencesRepository : IGenericRepository<FormRefernce>
    {
        Task<bool> CheckFomHaveReferences(int id);
    }



}