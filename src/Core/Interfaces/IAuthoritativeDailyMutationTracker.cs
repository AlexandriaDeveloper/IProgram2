#nullable enable
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Core.Models;

namespace Core.Interfaces
{
    public sealed class CapturedAuthoritativeDailyMutation
    {
        public Daily? Daily { get; init; }
        public required string OperationType { get; init; } // INSERT, UPDATE, SOFT_DELETE, HARD_DELETE
        public required Guid EntitySyncId { get; init; }
    }

    /// <summary>
    /// Service responsible for recording authoritative sync metadata (ServerState, ServerChangeFeed, Tombstones)
    /// within the exact same database connection and transaction used for the business mutation.
    /// </summary>
    public interface IAuthoritativeDailyMutationTracker
    {
        Task TrackDailyMutationsAsync(
            DbConnection connection,
            DbTransaction transaction,
            string databaseId,
            IReadOnlyList<CapturedAuthoritativeDailyMutation> mutations,
            CancellationToken cancellationToken);
    }
}
