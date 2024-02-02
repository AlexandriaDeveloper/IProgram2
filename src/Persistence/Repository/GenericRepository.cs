
using System.Security.Claims;
using Auth.Infrastructure;
using Core.Interfaces;
using Core.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Persistence.Specifications;

namespace Persistence.Repository
{
    public class GenericRepository<T> : IGenericRepository<T> where T : Entity
    {
        private readonly ApplicationContext _context;
        private readonly IHttpContextAccessor _accessor;

        public GenericRepository(ApplicationContext context, IHttpContextAccessor accessor)
        {
            this._context = context;
            this._accessor = accessor;
        }

        public async Task<T> GetById(int id)
        {
            return await this._context.Set<T>().FirstOrDefaultAsync(x => x.Id == id);
        }

        public async Task Delete(int id)
        {
            var entity = await GetById(id);
            this._context.Set<T>().Remove(entity);
        }
        public void DeleteRange(IEnumerable<T> entities)
        {
            this._context.Set<T>().RemoveRange(entities);
        }


        public async Task InActive(int id)
        {
            var entity = await GetById(id);

            entity.IsActive = true;
            entity.DeactivatedAt = null;
            entity.DeactivatedBy = null;
            _context.Set<T>().Update(entity);

        }
        public async Task DeActive(int id)
        {
            var entity = await GetById(id);

            entity.IsActive = false;


            entity.DeactivatedAt = DateTime.Now;
            entity.DeactivatedBy = _accessor.HttpContext.User.Claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier).Value;
            _context.Set<T>().Update(entity);

        }

        public async Task Insert(T entity)
        {
            entity.IsActive = true;
            entity.CreatedAt = DateTime.Now;
            entity.CreatedBy = _accessor.HttpContext.User.Claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier).Value;
            await _context.Set<T>().AddAsync(entity);
        }

        public void Update(T entity)
        {
            entity.UpdatedAt = DateTime.Now;
            entity.UpdatedBy = _accessor.HttpContext.User.Claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier).Value;
            _context.Set<T>().Update(entity);
        }

        public Task<T> GetBySpec(ISpecification<T> spec)
        {

            return ApplyActiveSpecification(spec).FirstOrDefaultAsync();
        }

        public async Task<List<T>> ListAllAsync()
        {

            return await _context.Set<T>().Where(t => t.IsActive).ToListAsync();
        }

        public async Task<List<T>> ListAllAsync(ISpecification<T> spec)
        {

            return await ApplyActiveSpecification(spec).ToListAsync();
        }
        public async Task<int> CountAsync(ISpecification<T> spec)
        {

            return await ApplyActiveSpecification(spec).CountAsync();
        }
        private IQueryable<T> ApplyActiveSpecification(ISpecification<T> spec)
        {
            spec.Criterias.Add(c => c.IsActive == true);
            return SpecificationEvaluator<T>.GetQuery(_context.Set<T>().AsQueryable(), spec);
        }
        private IQueryable<T> ApplyInactiveSpecification(ISpecification<T> spec)
        {
            spec.Criterias.Add(c => c.IsActive == false);
            return SpecificationEvaluator<T>.GetQuery(_context.Set<T>().AsQueryable(), spec);
        }
        public async Task<T> GetInactiveById(int id)
        {
            return await this._context.Set<T>().FirstOrDefaultAsync(x => x.Id == id && x.IsActive == false);
        }

        public Task<T> GetInactiveBySpec(ISpecification<T> spec)
        {
            return ApplyInactiveSpecification(spec).FirstOrDefaultAsync();
        }

        public async Task<List<T>> ListAllInactiveAsync()
        {
            return await _context.Set<T>().Where(t => !t.IsActive).ToListAsync();
        }

        public async Task<List<T>> ListAllInacticeAsync(ISpecification<T> spec)
        {
            return await ApplyInactiveSpecification(spec).ToListAsync();
        }
        public IQueryable<T> GetQueryable(bool isActive = true)
        {
            return _context.Set<T>().Where(t => t.IsActive == isActive);
        }
    }
}