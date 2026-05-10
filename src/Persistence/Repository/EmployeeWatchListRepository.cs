using Auth.Infrastructure;
using Core.Interfaces;
using Core.Models;
using Microsoft.AspNetCore.Http;

namespace Persistence.Repository
{
    public class EmployeeWatchListRepository : GenericRepository<EmployeeWatchList>, IEmployeeWatchListRepository
    {
        public EmployeeWatchListRepository(ApplicationContext context, IHttpContextAccessor accessor) : base(context, accessor)
        {
        }
    }
}
