using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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

        public async Task<bool> CheckEmployeeByNationalId(string nationalId)
        {
            return await _context.Employees.AnyAsync(x => x.NationalId == nationalId);
        }

        public async Task<Employee> GetEmployeeByNationalId(string nationalId)
        {
            var emp = await _context.Employees.FirstOrDefaultAsync(x => x.NationalId == nationalId);
            return emp;
        }

        public Task<bool> HasEmployeeReferences(int EmployeeId)
        {
            return _context.Set<EmployeeRefernce>().AnyAsync(x => x.EmployeeId == EmployeeId && x.IsActive);
        }
    }

}