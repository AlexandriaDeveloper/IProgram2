#nullable enable
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Core.Models;

namespace Core.Interfaces
{
    public sealed class AuthoritativeDailyOriginalSnapshot
    {
        public required Guid SyncId { get; init; }
        public required string Name { get; init; }
        public required DateTime DailyDate { get; init; }
        public required bool Closed { get; init; }
        public required DateTime CreatedAt { get; init; }
        public string? CreatedBy { get; init; }
        public DateTime? UpdatedAt { get; init; }
        public string? UpdatedBy { get; init; }
        public DateTime? DeactivatedAt { get; init; }
        public string? DeactivatedBy { get; init; }
        public required bool IsActive { get; init; }
    }

    public sealed class CapturedAuthoritativeDailyMutation
    {
        public string EntityType { get; init; } = "Daily";
        public Daily? Daily { get; init; }
        public object? Entity { get; init; }
        public required string OperationType { get; init; } // INSERT, UPDATE, SOFT_DELETE, HARD_DELETE
        public required Guid EntitySyncId { get; init; }
        public AuthoritativeDailyOriginalSnapshot? OriginalSnapshot { get; init; }
    }

    public sealed class AuthoritativeTrackingReservation
    {
        public required long StartingServerVersion { get; init; }
        public required DateTime TransactionTimestampUtc { get; init; }
        public required IReadOnlyList<CapturedAuthoritativeDailyMutation> OrderedMutations { get; init; }
    }

    /// <summary>
    /// Service responsible for recording authoritative sync metadata (ServerState, ServerChangeFeed, Tombstones)
    /// within the exact same database connection and transaction used for the business mutation.
    /// Supports a two-phase protocol so ServerState row lock is acquired BEFORE business Daily DML,
    /// avoiding lock order inversion with offline push.
    /// </summary>
    public interface IAuthoritativeDailyMutationTracker
    {
        /// <summary>
        /// Phase 1: Takes UPDLOCK, HOLDLOCK on sync.ServerState, validates tombstone resurrection,
        /// and records the starting ServerVersion without advancing it yet.
        /// Must be called BEFORE business SaveChanges on Daily.
        /// </summary>
        Task<AuthoritativeTrackingReservation> PrepareAuthoritativeBatchAsync(
            DbConnection connection,
            DbTransaction transaction,
            string databaseId,
            IReadOnlyList<CapturedAuthoritativeDailyMutation> mutations,
            CancellationToken cancellationToken);

        /// <summary>
        /// Phase 2: Writes Tombstones, ServerChangeFeed, and advances ServerState.CurrentVersion.
        /// Must be called AFTER successful business SaveChanges on Daily, prior to transaction commit.
        /// </summary>
        Task<long> CompleteAuthoritativeBatchAsync(
            DbConnection connection,
            DbTransaction transaction,
            string databaseId,
            AuthoritativeTrackingReservation reservation,
            CancellationToken cancellationToken);

        /// <summary>
        /// Convenience method combining Phase 1 and Phase 2.
        /// </summary>
        Task TrackDailyMutationsAsync(
            DbConnection connection,
            DbTransaction transaction,
            string databaseId,
            IReadOnlyList<CapturedAuthoritativeDailyMutation> mutations,
            CancellationToken cancellationToken);
    }
}
