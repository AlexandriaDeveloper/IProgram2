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

            // Phase 2: Validate each mutation under row-level lock BEFORE business SaveChanges
            foreach (var mutation in orderedMutations)
            {
                if (string.Equals(mutation.OperationType, "INSERT", StringComparison.OrdinalIgnoreCase))
                {
                    // 1. Verify tombstone resurrection safety on INSERT
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

                    var tombCount = Convert.ToInt32(await checkTombstoneCmd.ExecuteScalarAsync(cancellationToken));
                    if (tombCount > 0)
                    {
                        throw new AuthoritativeTrackingException(
                            $"Cannot insert Daily with SyncId '{mutation.EntitySyncId}': a tombstone already exists for this entity in DatabaseId '{normDbId}'. Entity resurrection is disallowed.");
                    }

                    // 2. Verify Daily does not already exist with this SyncId
                    await using var checkDailyCmd = connection.CreateCommand();
                    checkDailyCmd.Transaction = transaction;
                    checkDailyCmd.CommandText = @"
                        SELECT COUNT(1)
                        FROM [dbo].[Daily] WITH (UPDLOCK, HOLDLOCK)
                        WHERE SyncId = @SyncId;";

                    AddParam(checkDailyCmd, "@SyncId", mutation.EntitySyncId);

                    var dailyCount = Convert.ToInt32(await checkDailyCmd.ExecuteScalarAsync(cancellationToken));
                    if (dailyCount > 0)
                    {
                        throw new AuthoritativeConcurrencyConflictException(
                            $"Authoritative concurrency conflict on Daily with SyncId '{mutation.EntitySyncId}'. An entity with this SyncId already exists in DatabaseId '{normDbId}'. Write transaction aborted fail-closed.");
                    }
                }
                else // UPDATE, SOFT_DELETE, HARD_DELETE
                {
                    if (mutation.OriginalSnapshot == null)
                    {
                        throw new AuthoritativeTrackingException(
                            $"OriginalSnapshot is required for operation '{mutation.OperationType}' on Daily with SyncId '{mutation.EntitySyncId}'. Write transaction aborted fail-closed.");
                    }

                    // Query current authoritative row and hold UPDLOCK, HOLDLOCK until transaction ends
                    await using var readDailyCmd = connection.CreateCommand();
                    readDailyCmd.Transaction = transaction;
                    readDailyCmd.CommandText = @"
                        SELECT [Name], [DailyDate], [Closed], [CreatedAt], [CreatedBy], [UpdatedAt], [UpdatedBy], [DeactivatedAt], [DeactivatedBy], [IsActive]
                        FROM [dbo].[Daily] WITH (UPDLOCK, HOLDLOCK)
                        WHERE [SyncId] = @SyncId;";

                    AddParam(readDailyCmd, "@SyncId", mutation.EntitySyncId);

                    bool rowFound = false;
                    string? dbName = null;
                    DateTime dbDailyDate = default;
                    bool dbClosed = false;
                    DateTime dbCreatedAt = default;
                    string? dbCreatedBy = null;
                    DateTime? dbUpdatedAt = null;
                    string? dbUpdatedBy = null;
                    DateTime? dbDeactivatedAt = null;
                    string? dbDeactivatedBy = null;
                    bool dbIsActive = false;

                    await using (var reader = await readDailyCmd.ExecuteReaderAsync(cancellationToken))
                    {
                        if (await reader.ReadAsync(cancellationToken))
                        {
                            rowFound = true;
                            dbName = reader.GetString(0);
                            dbDailyDate = reader.GetDateTime(1);
                            dbClosed = reader.GetBoolean(2);
                            dbCreatedAt = reader.GetDateTime(3);
                            dbCreatedBy = reader.IsDBNull(4) ? null : reader.GetString(4);
                            dbUpdatedAt = reader.IsDBNull(5) ? null : reader.GetDateTime(5);
                            dbUpdatedBy = reader.IsDBNull(6) ? null : reader.GetString(6);
                            dbDeactivatedAt = reader.IsDBNull(7) ? null : reader.GetDateTime(7);
                            dbDeactivatedBy = reader.IsDBNull(8) ? null : reader.GetString(8);
                            dbIsActive = reader.GetBoolean(9);
                        }
                    }

                    if (!rowFound)
                    {
                        throw new AuthoritativeConcurrencyConflictException(
                            $"Authoritative concurrency conflict on Daily with SyncId '{mutation.EntitySyncId}'. The target entity no longer exists in DatabaseId '{normDbId}'. Write transaction aborted fail-closed.");
                    }

                    var snap = mutation.OriginalSnapshot;
                    string? conflictField = null;

                    if (!StringsMatch(snap.Name, dbName))
                    {
                        conflictField = "Name";
                    }
                    else if (!DateTimesMatch(snap.DailyDate, dbDailyDate))
                    {
                        conflictField = "DailyDate";
                    }
                    else if (snap.Closed != dbClosed)
                    {
                        conflictField = "Closed";
                    }
                    else if (!DateTimesMatch(snap.CreatedAt, dbCreatedAt))
                    {
                        conflictField = "CreatedAt";
                    }
                    else if (!StringsMatch(snap.CreatedBy, dbCreatedBy))
                    {
                        conflictField = "CreatedBy";
                    }
                    else if (!NullableDateTimesMatch(snap.UpdatedAt, dbUpdatedAt))
                    {
                        conflictField = "UpdatedAt";
                    }
                    else if (!StringsMatch(snap.UpdatedBy, dbUpdatedBy))
                    {
                        conflictField = "UpdatedBy";
                    }
                    else if (!NullableDateTimesMatch(snap.DeactivatedAt, dbDeactivatedAt))
                    {
                        conflictField = "DeactivatedAt";
                    }
                    else if (!StringsMatch(snap.DeactivatedBy, dbDeactivatedBy))
                    {
                        conflictField = "DeactivatedBy";
                    }
                    else if (snap.IsActive != dbIsActive)
                    {
                        conflictField = "IsActive";
                    }

                    if (conflictField != null)
                    {
                        throw new AuthoritativeConcurrencyConflictException(
                            $"Authoritative concurrency conflict on Daily with SyncId '{mutation.EntitySyncId}'. Database current values do not match original snapshot (Field '{conflictField}' differed). Write transaction aborted fail-closed.");
                    }
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

        private static bool DateTimesMatch(DateTime dt1, DateTime dt2)
        {
            return dt1.Ticks == dt2.Ticks;
        }

        private static bool NullableDateTimesMatch(DateTime? dt1, DateTime? dt2)
        {
            if (!dt1.HasValue && !dt2.HasValue)
            {
                return true;
            }
            if (!dt1.HasValue || !dt2.HasValue)
            {
                return false;
            }
            return DateTimesMatch(dt1.Value, dt2.Value);
        }

        private static bool StringsMatch(string? s1, string? s2)
        {
            return string.Equals(s1, s2, StringComparison.Ordinal);
        }
    }
}
