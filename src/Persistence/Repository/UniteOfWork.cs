
using Auth.Infrastructure;
using Core.Interfaces;

namespace Persistence.Repository
{
    public class UniteOfWork : IUniteOfWork
    {
        private readonly ApplicationContext context;

        /*************  ✨ Windsurf Command ⭐  *************/
        /// <summary>
        /// Initializes a new instance of the <see cref="UniteOfWork"/> class.
        /// </summary>
        /// <param name="context">The context.</param>
        /*******  ed2684a2-cb0c-48bf-8e59-ff5bb26a2bbb  *******/
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