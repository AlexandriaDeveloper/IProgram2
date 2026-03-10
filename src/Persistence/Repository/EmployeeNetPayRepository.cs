using Auth.Infrastructure;
using Core.Interfaces;
using Core.Models;

using Microsoft.AspNetCore.Http;

namespace Persistence.Repository
{
    public class EmployeeNetPayRepository : GenericRepository<EmployeeNetPay>, IEmployeeNetPayRepository
    {
        public EmployeeNetPayRepository(ApplicationContext context, IHttpContextAccessor accessor) : base(context, accessor)
        {
        }
    }
}
