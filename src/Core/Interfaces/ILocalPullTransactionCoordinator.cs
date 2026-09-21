#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Core.Models.Sync;

namespace Core.Interfaces
{
    public interface ILocalPullTransactionCoordinator
    {
        Task<PullResultDto> ApplyPullBatchAsync(string databaseId, FencedPullBatch batch, Guid leaseToken, CancellationToken cancellationToken);
    }
}
