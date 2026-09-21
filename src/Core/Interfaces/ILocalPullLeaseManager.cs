#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Interfaces
{
    public interface ILocalPullLeaseManager
    {
        Task<Guid> AcquireLeaseAsync(string databaseId, TimeSpan duration, CancellationToken cancellationToken);
        Task RenewLeaseAsync(string databaseId, Guid leaseToken, TimeSpan duration, CancellationToken cancellationToken);
        Task ValidateLeaseOwnershipAsync(string databaseId, Guid leaseToken, CancellationToken cancellationToken);
        Task ReleaseLeaseAsync(string databaseId, Guid leaseToken, CancellationToken cancellationToken);
    }
}
