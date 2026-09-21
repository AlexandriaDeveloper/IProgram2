#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Core.Models.Sync;

namespace Core.Interfaces
{
    public interface IAzureFencedBatchReader
    {
        Task<FencedPullBatch> ReadFencedBatchAsync(string databaseId, long localLastServerVersion, CancellationToken cancellationToken);
    }
}
