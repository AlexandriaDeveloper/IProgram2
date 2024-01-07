
using Auth.Infrastructure;
using Core.Interfaces;

namespace Persistence.Repository
{
    public class UniteOfWork : IUniteOfWork
    {
        private readonly ApplicationContext context;

        public UniteOfWork(ApplicationContext context)
        {
            this.context = context;
        }
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                return context.SaveChangesAsync(cancellationToken);
            }
            catch (System.Exception ex)
            {

                throw new System.Exception(ex.Message);
            }

        }
    }
}