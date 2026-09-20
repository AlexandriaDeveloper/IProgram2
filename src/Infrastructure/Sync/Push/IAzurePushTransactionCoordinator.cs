#nullable enable
using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Core.Models.Sync;

namespace Auth.Infrastructure.Sync.Push
{
    public class RemoteApplyResult
    {
        public bool IsReplay { get; init; }
        public long ServerVersion { get; init; }
        public string ResponseJson { get; init; } = string.Empty;
    }

    public interface IAzurePushTransactionCoordinator
    {
        Task<RemoteApplyResult> ApplyOperationAsync(
            DbConnection connection,
            string databaseId,
            LocalOutbox outboxItem,
            long expectedServerVersion,
            string requestHash,
            CancellationToken cancellationToken);
    }
}
