
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

        public EmployeeRepository(ApplicationContext context, IHttpContextAccessor accessor) : base(context, accessor)
        {
            this._context = context;

        }
        public new async Task<Employee> GetById(string id)
        {
            return await this._context.Set<Employee>().FirstOrDefaultAsync(x => x.Id == id);
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