
using Auth.Infrastructure;
using Core.Interfaces;
using Core.Models;
using Microsoft.AspNetCore.Http;

namespace Persistence.Repository
{
    public class DepartmentRepository : GenericRepository<Department>, IDepartmentRepository
    {
        public DepartmentRepository(ApplicationContext context, IHttpContextAccessor accessor) : base(context, accessor)
        {
        }
    }
}