#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Auth.Infrastructure.Sync.Push
{
    public interface ILocalPushLeaseManager
    {
        Task<Guid> AcquireLeaseAsync(string databaseId, TimeSpan duration, CancellationToken cancellationToken);
        Task ReleaseLeaseAsync(string databaseId, Guid leaseToken, CancellationToken cancellationToken);
    }
}
