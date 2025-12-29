using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Core.Interfaces
{
    public interface IGenericRepository<TValue>
    {
        Task<TValue> GetById(int id, bool trackChanges = true);
        Task<TValue> GetBySpec(ISpecification<TValue> spec, bool trackChanges = true);
        Task<List<TValue>> ListAllAsync(bool trackChanges = true);
        Task<List<TValue>> ListAllAsync(ISpecification<TValue> spec, bool? withInactive = false, bool trackChanges = true);



        Task<TValue> GetInactiveById(int id);
        Task<TValue> GetInactiveBySpec(ISpecification<TValue> spec);
        Task<List<TValue>> ListAllInactiveAsync();
        Task<List<TValue>> ListAllInacticeAsync(ISpecification<TValue> spec);


        Task Insert(TValue entity);
        void Update(TValue entity);
        Task Delete(int id);
        void DeleteRange(IEnumerable<TValue> entities);
        Task InActive(int id);
        Task DeActive(int id);
        IQueryable<TValue> GetQueryable(bool? isActive = true);
        Task<int> CountAsync(ISpecification<TValue> spec);



    }
}