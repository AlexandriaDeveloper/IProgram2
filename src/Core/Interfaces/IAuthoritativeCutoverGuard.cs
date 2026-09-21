#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Core.Interfaces
{
    /// <summary>
    /// Permanent post-cutover guard that verifies Azure authoritative cutover state
    /// before allowing Online Daily mutations when AuthoritativeTracking is disabled.
    /// </summary>
    public interface IAuthoritativeCutoverGuard
    {
        void ValidateCutoverState(DbContext context, string canonicalDatabaseId);
        Task ValidateCutoverStateAsync(DbContext context, string canonicalDatabaseId, CancellationToken cancellationToken = default);
    }
}
