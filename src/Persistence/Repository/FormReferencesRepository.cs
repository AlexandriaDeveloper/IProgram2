using Auth.Infrastructure;
using Core.Interfaces;
using Core.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Persistence.Repository
{
    public class FormReferencesRepository : GenericRepository<FormRefernce>, IFormReferencesRepository
    {
        private readonly ApplicationContext context;

        public FormReferencesRepository(ApplicationContext context, IHttpContextAccessor accessor) : base(context, accessor)
        {
            this.context = context;
        }

        public async Task<bool> CheckFomHaveReferences(int id)
        {
            return await context.Set<FormRefernce>().AnyAsync(x => x.FormId == id && x.IsActive);
        }
    }
}