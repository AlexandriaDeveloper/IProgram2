
using System.Runtime.CompilerServices;
using Auth.Infrastructure;
using Core.Interfaces;
using Core.Models;
using Microsoft.AspNetCore.Http;

namespace Persistence.Repository
{
    public class EmployeeBanktRepository : GenericRepository<EmployeeBank>, IEmployeeBankRepository
    {
        private readonly ApplicationContext _context;
        public EmployeeBanktRepository(ApplicationContext context, IHttpContextAccessor accessor) : base(context, accessor)
        {
            _context = context;
        }

        public new async Task Delete(string employeeId)
        {
            var entity = _context.Set<EmployeeBank>().FirstOrDefault(x => x.EmployeeId == employeeId);
            _context.Set<EmployeeBank>().Remove(entity);
        }
    }
}