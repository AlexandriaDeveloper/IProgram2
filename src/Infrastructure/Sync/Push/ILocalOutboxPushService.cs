#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Core.Models.Sync;

namespace Auth.Infrastructure.Sync.Push
{
    public interface ILocalOutboxPushService
    {
        Task<PushBatchResult> PushPendingOutboxAsync(CancellationToken cancellationToken, bool isExplicitManual = false);
    }
}
