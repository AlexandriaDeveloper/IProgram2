#nullable enable
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Core.Exceptions;
using Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Auth.Infrastructure.Sync.Authoritative
{
    /// <summary>
    /// Service that coordinates authoritative sync metadata mutations (ServerState, ServerChangeFeed, Tombstones)
    /// within the exact same database connection and transaction used for the business mutation on Azure SQL.
    /// </summary>
    public class AuthoritativeDailyMutationTracker : IAuthoritativeDailyMutationTracker
    {
        private readonly ILogger<AuthoritativeDailyMutationTracker> _logger;

        /// <summary>
        /// Protocol constant representing authoritative online server writes (not from client device push).
        /// </summary>
        public static readonly Guid ServerOriginDeviceId = Guid.Empty;

        public AuthoritativeDailyMutationTracker(ILogger<AuthoritativeDailyMutationTracker> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<AuthoritativeTrackingReservation> PrepareAuthoritativeBatchAsync(
            DbConnection connection,
            DbTransaction transaction,
            string databaseId,
            IReadOnlyList<CapturedAuthoritativeDailyMutation> mutations,
            CancellationToken cancellationToken)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (transaction == null) throw new ArgumentNullException(nameof(transaction));

            if (string.IsNullOrWhiteSpace(databaseId))
            {
                throw new AuthoritativeTrackingException("Canonical DatabaseId is required for authoritative sync tracking.");
            }

            var normDbId = databaseId.Trim();
            if (normDbId != "2026" && normDbId != "2027")
            {
                throw new AuthoritativeTrackingException($"Unsupported canonical DatabaseId '{databaseId}'. Expected '2026' or '2027'.");
            }

            if (mutations == null || mutations.Count == 0)
            {
                return new AuthoritativeTrackingReservation
                {
                    StartingServerVersion = 0,
                    TransactionTimestampUtc = DateTime.UtcNow,
                    OrderedMutations = Array.Empty<CapturedAuthoritativeDailyMutation>()
                };
            }

            foreach (var mutation in mutations)
            {
                if (mutation.EntitySyncId == Guid.Empty)
                {
                    throw new AuthoritativeTrackingException(
                        $"Daily mutation has empty SyncId for operation '{mutation.OperationType}'. Empty SyncId is strictly forbidden.");
                }
            }

            var transactionTimestampUtc = DateTime.UtcNow;

            // Phase 1: Lock and read current ServerState with (UPDLOCK, HOLDLOCK) BEFORE business Daily DML
            long startingServerVersion;
            await using (var lockCmd = connection.CreateCommand())
            {
                lockCmd.Transaction = transaction;
                lockCmd.CommandText = @"
                    SELECT CurrentVersion
                    FROM [sync].[ServerState] WITH (UPDLOCK, HOLDLOCK)
                    WHERE DatabaseId = @DatabaseId;";

                AddParam(lockCmd, "@DatabaseId", normDbId);

                var scalar = await lockCmd.ExecuteScalarAsync(cancellationToken);
                if (scalar == null || scalar == DBNull.Value)
                {
                    throw new AuthoritativeTrackingException(
                        $"ServerState record not found for DatabaseId '{normDbId}'. Write transaction aborted fail-closed.");
                }

                startingServerVersion = Convert.ToInt64(scalar);
            }

            // Deterministically order mutations by EntitySyncId ASC
            var orderedMutations = mutations.OrderBy(m => m.EntitySyncId).ToList();

            // Verify tombstone resurrection safety on INSERT mutations
            foreach (var mutation in orderedMutations.Where(m => string.Equals(m.OperationType, "INSERT", StringComparison.OrdinalIgnoreCase)))
            {
                await using var checkTombstoneCmd = connection.CreateCommand();
                checkTombstoneCmd.Transaction = transaction;
                checkTombstoneCmd.CommandText = @"
                    SELECT COUNT(1)
                    FROM [sync].[Tombstones] WITH (UPDLOCK, HOLDLOCK)
                    WHERE DatabaseId = @DatabaseId
                      AND EntityType = 'Daily'
                      AND EntitySyncId = @EntitySyncId;";

                AddParam(checkTombstoneCmd, "@DatabaseId", normDbId);
                AddParam(checkTombstoneCmd, "@EntitySyncId", mutation.EntitySyncId);

                var count = Convert.ToInt32(await checkTombstoneCmd.ExecuteScalarAsync(cancellationToken));
                if (count > 0)
                {
                    throw new AuthoritativeTrackingException(
                        $"Cannot insert Daily with SyncId '{mutation.EntitySyncId}': a tombstone already exists for this entity in DatabaseId '{normDbId}'. Entity resurrection is disallowed.");
                }
            }

            return new AuthoritativeTrackingReservation
            {
                StartingServerVersion = startingServerVersion,
                TransactionTimestampUtc = transactionTimestampUtc,
                OrderedMutations = orderedMutations
            };
        }

        public async Task<long> CompleteAuthoritativeBatchAsync(
            DbConnection connection,
            DbTransaction transaction,
            string databaseId,
            AuthoritativeTrackingReservation reservation,
            CancellationToken cancellationToken)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (transaction == null) throw new ArgumentNullException(nameof(transaction));
            if (reservation == null) throw new ArgumentNullException(nameof(reservation));

            var normDbId = databaseId?.Trim() ?? string.Empty;
            if (normDbId != "2026" && normDbId != "2027")
            {
                throw new AuthoritativeTrackingException($"Unsupported canonical DatabaseId '{databaseId}'. Expected '2026' or '2027'.");
            }

            if (reservation.OrderedMutations.Count == 0)
            {
                return reservation.StartingServerVersion;
            }

            var currentServerVersion = reservation.StartingServerVersion;
            var transactionTimestampUtc = reservation.TransactionTimestampUtc;

            // Phase 2: Apply mutations sequentially, incrementing version for each
            foreach (var mutation in reservation.OrderedMutations)
            {
                currentServerVersion++;

                // If HARD_DELETE: record Tombstone
                if (string.Equals(mutation.OperationType, "HARD_DELETE", StringComparison.OrdinalIgnoreCase))
                {
                    await using var tombstoneCmd = connection.CreateCommand();
                    tombstoneCmd.Transaction = transaction;
                    tombstoneCmd.CommandText = @"
                        IF EXISTS (
                            SELECT 1 FROM [sync].[Tombstones]
                            WHERE DatabaseId = @DatabaseId AND EntityType = 'Daily' AND EntitySyncId = @EntitySyncId
                        )
                        BEGIN
                            UPDATE [sync].[Tombstones]
                            SET ServerVersion = @ServerVersion,
                                DeletedAtUtc = @DeletedAtUtc,
                                NaturalKey = NULL
                            WHERE DatabaseId = @DatabaseId AND EntityType = 'Daily' AND EntitySyncId = @EntitySyncId;
                        END
                        ELSE
                        BEGIN
                            INSERT INTO [sync].[Tombstones]
                            ([DatabaseId], [EntityType], [EntitySyncId], [NaturalKey], [ServerVersion], [DeletedAtUtc])
                            VALUES
                            (@DatabaseId, 'Daily', @EntitySyncId, NULL, @ServerVersion, @DeletedAtUtc);
                        END";

                    AddParam(tombstoneCmd, "@DatabaseId", normDbId);
                    AddParam(tombstoneCmd, "@EntitySyncId", mutation.EntitySyncId);
                    AddParam(tombstoneCmd, "@ServerVersion", currentServerVersion);
                    AddParam(tombstoneCmd, "@DeletedAtUtc", transactionTimestampUtc);

                    await tombstoneCmd.ExecuteNonQueryAsync(cancellationToken);
                }

                // Insert into ServerChangeFeed
                await using var feedCmd = connection.CreateCommand();
                feedCmd.Transaction = transaction;
                feedCmd.CommandText = @"
                    INSERT INTO [sync].[ServerChangeFeed]
                    ([ServerVersion], [DatabaseId], [EntityType], [EntitySyncId], [OperationType], [OriginDeviceId], [TimestampUtc])
                    VALUES
                    (@ServerVersion, @DatabaseId, 'Daily', @EntitySyncId, @OperationType, @OriginDeviceId, @TimestampUtc);";

                AddParam(feedCmd, "@ServerVersion", currentServerVersion);
                AddParam(feedCmd, "@DatabaseId", normDbId);
                AddParam(feedCmd, "@EntitySyncId", mutation.EntitySyncId);
                AddParam(feedCmd, "@OperationType", mutation.OperationType.ToUpperInvariant());
                AddParam(feedCmd, "@OriginDeviceId", ServerOriginDeviceId);
                AddParam(feedCmd, "@TimestampUtc", transactionTimestampUtc);

                await feedCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            // Update ServerState with final ServerVersion and transaction timestamp
            await using (var updateStateCmd = connection.CreateCommand())
            {
                updateStateCmd.Transaction = transaction;
                updateStateCmd.CommandText = @"
                    UPDATE [sync].[ServerState]
                    SET CurrentVersion = @FinalVersion,
                        LastUpdatedUtc = @LastUpdatedUtc
                    WHERE DatabaseId = @DatabaseId;";

                AddParam(updateStateCmd, "@FinalVersion", currentServerVersion);
                AddParam(updateStateCmd, "@LastUpdatedUtc", transactionTimestampUtc);
                AddParam(updateStateCmd, "@DatabaseId", normDbId);

                var rows = await updateStateCmd.ExecuteNonQueryAsync(cancellationToken);
                if (rows != 1)
                {
                    throw new AuthoritativeTrackingException(
                        $"Failed to update ServerState for DatabaseId '{normDbId}': affected rows = {rows}.");
                }
            }

            _logger.LogInformation(
                "Authoritative Daily mutations tracked successfully: DatabaseId={DatabaseId}, Count={Count}, FinalServerVersion={FinalVersion}.",
                normDbId, reservation.OrderedMutations.Count, currentServerVersion);

            return currentServerVersion;
        }

        public async Task TrackDailyMutationsAsync(
            DbConnection connection,
            DbTransaction transaction,
            string databaseId,
            IReadOnlyList<CapturedAuthoritativeDailyMutation> mutations,
            CancellationToken cancellationToken)
        {
            var reservation = await PrepareAuthoritativeBatchAsync(
                connection, transaction, databaseId, mutations, cancellationToken);

            await CompleteAuthoritativeBatchAsync(
                connection, transaction, databaseId, reservation, cancellationToken);
        }

        private static void AddParam(DbCommand cmd, string name, object? value)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }
    }
}
