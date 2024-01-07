using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Core.Interfaces
{
    public interface IGenericRepository<TValue>
    {
        Task<TValue> GetById(int id);
        Task<TValue> GetBySpec(ISpecification<TValue> spec);
        Task<List<TValue>> ListAllAsync();
        Task<List<TValue>> ListAllAsync(ISpecification<TValue> spec);



        Task<TValue> GetInactiveById(int id);
        Task<TValue> GetInactiveBySpec(ISpecification<TValue> spec);
        Task<List<TValue>> ListAllInactiveAsync();
        Task<List<TValue>> ListAllInacticeAsync(ISpecification<TValue> spec);


        Task Insert(TValue entity);
        void Update(TValue entity);
        Task Delete(int id);
        Task InActive(int id);
        Task DeActive(int id);
        IQueryable<TValue> GetQueryable();
        Task<int> CountAsync(ISpecification<TValue> spec);



    }
}