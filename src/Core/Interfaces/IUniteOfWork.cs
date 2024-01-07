using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Core.Interfaces
{
    public interface IUniteOfWork
    {
        Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
    }
}