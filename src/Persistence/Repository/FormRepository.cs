using Auth.Infrastructure;
using Core.Interfaces;
using Core.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Persistence.Repository
{
    public class FormRepository : GenericRepository<Form>, IFormRepository
    {
        private readonly ApplicationContext context;

        public FormRepository(ApplicationContext context, IHttpContextAccessor accessor) : base(context, accessor)
        {
            this.context = context;
        }

        public async Task<Form> GetFormWithDetailsByIdAsync(int id)
        {
            return await context.Set<Form>()
            .Include(x => x.FormDetails.OrderBy(x => x.OrderNum))
            .FirstOrDefaultAsync(x => x.Id == id && x.IsActive == true);
        }
    }
}