
using System.Security.Claims;
using Auth.Infrastructure;
using Core.Interfaces;
using Core.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Persistence.Repository
{
    public class EmployeeRepository : GenericRepository<Employee>, IEmployeeRepository
    {
        private readonly ApplicationContext _context;


        // private readonly ApplicationContext _context;
        private readonly IHttpContextAccessor _accessor;

        public EmployeeRepository(ApplicationContext context, IHttpContextAccessor accessor) : base(context, accessor)
        {
            this._accessor = accessor;
            this._context = context;

        }
        public new async Task<Employee> GetById(string id, bool noTracking = false)
        {
            if (noTracking)
                return await this._context.Set<Employee>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            return await this._context.Set<Employee>().FirstOrDefaultAsync(x => x.Id == id);
        }
        public new async Task Delete(string id)
        {
            var entity = await GetById(id);
            this._context.Set<Employee>().Remove(entity);

        }
        public new async Task DeActive(string id)
        {
            var entity = await GetById(id);
            entity.IsActive = false;
            entity.DeactivatedAt = DateTime.Now;
            entity.DeactivatedBy = _accessor.HttpContext.User.Claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier).Value;
            _context.Set<Employee>().Update(entity);

        }


        public async Task<bool> CheckEmployeeByNationalId(string nationalId)
        {
            return await _context.Employees.AnyAsync(x => x.Id == nationalId);
        }

        public async Task<Employee> GetEmployeeByNationalId(string nationalId)
        {
            var emp = await _context.Employees.FirstOrDefaultAsync(x => x.Id == nationalId);
            return emp;
        }

        public Task<bool> HasBank(string employeeId)
        {
            return _context.Set<EmployeeBank>().AnyAsync(x => x.EmployeeId == employeeId && x.IsActive);
        }

        public Task<bool> HasEmployeeReferences(string EmployeeId)
        {
            return _context.Set<EmployeeRefernce>().AnyAsync(x => x.EmployeeId == EmployeeId && x.IsActive);
        }
    }

}