#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Core.Models.Sync;

namespace Core.Interfaces
{
    public interface ISyncStatusService
    {
        Task<LocalSyncStatusDto> GetLocalStatusAsync(string databaseId, CancellationToken cancellationToken = default);
        Task<OnlineSyncStatusDto> CheckOnlineStatusAsync(string databaseId, CancellationToken cancellationToken = default);
    }
}
