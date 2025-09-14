using Auth.Infrastructure;
using Core.Interfaces;
using Core.Models;
using Microsoft.AspNetCore.Http;

namespace Persistence.Repository
{
    public class DailyReferencesRepository : GenericRepository<DailyReference>, IDailyReferencesRepository
    {
        private readonly ApplicationContext _context;

        public DailyReferencesRepository(ApplicationContext context, IHttpContextAccessor accessor) : base(context, accessor)
        {
            this._context = context;
        }
    }
}