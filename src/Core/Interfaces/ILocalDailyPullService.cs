#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Core.Models.Sync;

namespace Core.Interfaces
{
    public interface ILocalDailyPullService
    {
        Task<PullResultDto> PullDailyChangesAsync(CancellationToken cancellationToken);
    }
}
