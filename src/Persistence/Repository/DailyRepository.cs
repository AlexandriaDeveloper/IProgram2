using Auth.Infrastructure;
using Core.Interfaces;
using Core.Models;
using Microsoft.AspNetCore.Http;

namespace Persistence.Repository
{
    public class DailyRepository : GenericRepository<Daily>, IDailyRepository
    {
        private readonly ApplicationContext _context;

        public DailyRepository(ApplicationContext context, IHttpContextAccessor accessor) : base(context, accessor)
        {
            this._context = context;
        }

        public bool IsClosed(int id)
        {
            return _context.Set<Daily>().Where(x => x.Id == id).Select(x => x.Closed).FirstOrDefault();
        }



    }
}