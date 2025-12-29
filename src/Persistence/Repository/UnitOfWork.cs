
using Auth.Infrastructure;
using Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Persistence.Repository
{
    public class UnitOfWork : IUnitOfWork
    {
        private readonly ApplicationContext context;

        /*************  ✨ Windsurf Command ⭐  *************/
        /// <summary>
        public UnitOfWork(ApplicationContext context)
        {
            this.context = context;
        }
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            return context.SaveChangesAsync(cancellationToken);
        }
    }
}