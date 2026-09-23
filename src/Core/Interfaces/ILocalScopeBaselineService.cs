#nullable enable
using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Interfaces
{
    public enum SyncScopeBaselineStatus
    {
        NotBaselined = 0,
        Baselined = 1,
        Pending = 2
    }

    public interface ILocalScopeBaselineService
    {
        Task<SyncScopeBaselineStatus> GetScopeStatusAsync(
            DbConnection connection,
            DbTransaction? transaction,
            string databaseId,
            string scope,
            CancellationToken cancellationToken);

        Task EnsureScopeBaselinedAsync(
            DbConnection connection,
            DbTransaction? transaction,
            string databaseId,
            string scope,
            CancellationToken cancellationToken);

        Task SetScopeStatusAsync(
            DbConnection connection,
            DbTransaction? transaction,
            string databaseId,
            string scope,
            SyncScopeBaselineStatus status,
            long? baselineVersion,
            string? notes,
            CancellationToken cancellationToken);
    }
}
