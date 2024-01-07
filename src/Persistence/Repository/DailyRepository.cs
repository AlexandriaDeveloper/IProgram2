using Auth.Infrastructure;
using Core.Interfaces;
using Core.Models;
using Microsoft.AspNetCore.Http;

namespace Persistence.Repository
{
    public class DailyRepository : GenericRepository<Daily>, IDailyRepository
    {
        public DailyRepository(ApplicationContext context, IHttpContextAccessor accessor) : base(context, accessor)
        {
        }
    }
}