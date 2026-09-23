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
                          AND EntityType = @EntityType
                          AND EntitySyncId = @EntitySyncId;";

                    AddParam(checkTombstoneCmd, "@DatabaseId", normDbId);
                    AddParam(checkTombstoneCmd, "@EntityType", mutation.EntityType);
                    AddParam(checkTombstoneCmd, "@EntitySyncId", mutation.EntitySyncId);

                    var tombCount = Convert.ToInt32(await checkTombstoneCmd.ExecuteScalarAsync(cancellationToken));
                    if (tombCount > 0)
                    {
                        throw new AuthoritativeTrackingException(
                            $"Cannot insert {mutation.EntityType} with SyncId '{mutation.EntitySyncId}': a tombstone already exists for this entity in DatabaseId '{normDbId}'. Entity resurrection is disallowed.");
                    }

                    // 2. Verify Entity does not already exist with this SyncId
                    string targetTable = mutation.EntityType switch
                    {
                        "Form" => "[dbo].[Form]",
                        "FormDetails" => "[dbo].[FormDetails]",
                        "FormRefernce" => "[dbo].[FormRefernce]",
                        _ => "[dbo].[Daily]"
                    };

                    await using var checkExistCmd = connection.CreateCommand();
                    checkExistCmd.Transaction = transaction;
                    checkExistCmd.CommandText = $@"
                        SELECT COUNT(1)
                        FROM {targetTable} WITH (UPDLOCK, HOLDLOCK)
                        WHERE SyncId = @SyncId;";

                    AddParam(checkExistCmd, "@SyncId", mutation.EntitySyncId);

                    var existCount = Convert.ToInt32(await checkExistCmd.ExecuteScalarAsync(cancellationToken));
                    if (existCount > 0)
                    {
                        throw new AuthoritativeConcurrencyConflictException(
                            $"Authoritative concurrency conflict on {mutation.EntityType} with SyncId '{mutation.EntitySyncId}'. An entity with this SyncId already exists in DatabaseId '{normDbId}'. Write transaction aborted fail-closed.");
                    }
                }
                else // UPDATE, SOFT_DELETE, HARD_DELETE
                {
                    if (mutation.EntityType == "Daily")
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
                    else if (mutation.EntityType == "Form")
                    {
                        if (mutation.FormOriginalSnapshot == null)
                        {
                            throw new AuthoritativeTrackingException(
                                $"FormOriginalSnapshot is required for operation '{mutation.OperationType}' on Form with SyncId '{mutation.EntitySyncId}'. Write transaction aborted fail-closed.");
                        }

                        await using var readFormCmd = connection.CreateCommand();
                        readFormCmd.Transaction = transaction;
                        readFormCmd.CommandText = @"
                            SELECT [Name], [DailyId], [Index], [Description], [CreatedAt], [CreatedBy], [UpdatedAt], [UpdatedBy], [DeactivatedAt], [DeactivatedBy], [IsActive]
                            FROM [dbo].[Form] WITH (UPDLOCK, HOLDLOCK)
                            WHERE [SyncId] = @SyncId;";

                        AddParam(readFormCmd, "@SyncId", mutation.EntitySyncId);

                        bool rowFound = false;
                        string? dbName = null;
                        int? dbDailyId = null;
                        int? dbIndex = null;
                        string? dbDescription = null;
                        DateTime dbCreatedAt = default;
                        string? dbCreatedBy = null;
                        DateTime? dbUpdatedAt = null;
                        string? dbUpdatedBy = null;
                        DateTime? dbDeactivatedAt = null;
                        string? dbDeactivatedBy = null;
                        bool dbIsActive = false;

                        await using (var reader = await readFormCmd.ExecuteReaderAsync(cancellationToken))
                        {
                            if (await reader.ReadAsync(cancellationToken))
                            {
                                rowFound = true;
                                dbName = reader.IsDBNull(0) ? null : reader.GetString(0);
                                dbDailyId = reader.IsDBNull(1) ? null : reader.GetInt32(1);
                                dbIndex = reader.IsDBNull(2) ? null : reader.GetInt32(2);
                                dbDescription = reader.IsDBNull(3) ? null : reader.GetString(3);
                                dbCreatedAt = reader.GetDateTime(4);
                                dbCreatedBy = reader.IsDBNull(5) ? null : reader.GetString(5);
                                dbUpdatedAt = reader.IsDBNull(6) ? null : reader.GetDateTime(6);
                                dbUpdatedBy = reader.IsDBNull(7) ? null : reader.GetString(7);
                                dbDeactivatedAt = reader.IsDBNull(8) ? null : reader.GetDateTime(8);
                                dbDeactivatedBy = reader.IsDBNull(9) ? null : reader.GetString(9);
                                dbIsActive = reader.GetBoolean(10);
                            }
                        }

                        if (!rowFound)
                        {
                            throw new AuthoritativeConcurrencyConflictException(
                                $"Authoritative concurrency conflict on Form with SyncId '{mutation.EntitySyncId}'. The target entity no longer exists in DatabaseId '{normDbId}'. Write transaction aborted fail-closed.");
                        }

                        var snap = mutation.FormOriginalSnapshot;
                        string? conflictField = null;

                        if (!StringsMatch(snap.Name, dbName))
                        {
                            conflictField = "Name";
                        }
                        else if (snap.DailyId != dbDailyId)
                        {
                            conflictField = "DailyId";
                        }
                        else if (snap.Index != dbIndex)
                        {
                            conflictField = "Index";
                        }
                        else if (!StringsMatch(snap.Description, dbDescription))
                        {
                            conflictField = "Description";
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
                                $"Authoritative concurrency conflict on Form with SyncId '{mutation.EntitySyncId}'. Database current values do not match original snapshot (Field '{conflictField}' differed). Write transaction aborted fail-closed.");
                        }
                    }
                    else if (mutation.EntityType == "FormDetails")
                    {
                        if (mutation.FormDetailsOriginalSnapshot == null)
                        {
                            throw new AuthoritativeTrackingException(
                                $"FormDetailsOriginalSnapshot is required for operation '{mutation.OperationType}' on FormDetails with SyncId '{mutation.EntitySyncId}'. Write transaction aborted fail-closed.");
                        }

                        await using var readFdCmd = connection.CreateCommand();
                        readFdCmd.Transaction = transaction;
                        readFdCmd.CommandText = @"
                            SELECT [FormId], [EmployeeId], [Amount], [OrderNum], [IsReviewed], [IsReviewedBy], [ReviewedAt], [ReviewComments],
                                   [IsSummaryReviewed], [IsSummaryReviewedBy], [SummaryReviewedAt], [SummaryComments], [SummaryReviewMethod],
                                   [CreatedAt], [CreatedBy], [UpdatedAt], [UpdatedBy], [DeactivatedAt], [DeactivatedBy], [IsActive]
                            FROM [dbo].[FormDetails] WITH (UPDLOCK, HOLDLOCK)
                            WHERE [SyncId] = @SyncId;";

                        AddParam(readFdCmd, "@SyncId", mutation.EntitySyncId);

                        bool rowFound = false;
                        int dbFormId = 0;
                        string dbEmployeeId = string.Empty;
                        double dbAmount = 0.0;
                        int dbOrderNum = 0;
                        bool dbIsReviewed = false;
                        string? dbIsReviewedBy = null;
                        DateTime? dbReviewedAt = null;
                        string? dbReviewComments = null;
                        bool dbIsSummaryReviewed = false;
                        string? dbIsSummaryReviewedBy = null;
                        DateTime? dbSummaryReviewedAt = null;
                        string? dbSummaryComments = null;
                        string? dbSummaryReviewMethod = null;
                        DateTime dbCreatedAt = default;
                        string? dbCreatedBy = null;
                        DateTime? dbUpdatedAt = null;
                        string? dbUpdatedBy = null;
                        DateTime? dbDeactivatedAt = null;
                        string? dbDeactivatedBy = null;
                        bool dbIsActive = false;

                        await using (var reader = await readFdCmd.ExecuteReaderAsync(cancellationToken))
                        {
                            if (await reader.ReadAsync(cancellationToken))
                            {
                                rowFound = true;
                                dbFormId = reader.GetInt32(0);
                                dbEmployeeId = reader.GetString(1);
                                dbAmount = Convert.ToDouble(reader.GetValue(2));
                                dbOrderNum = reader.GetInt32(3);
                                dbIsReviewed = reader.GetBoolean(4);
                                dbIsReviewedBy = reader.IsDBNull(5) ? null : reader.GetString(5);
                                dbReviewedAt = reader.IsDBNull(6) ? null : reader.GetDateTime(6);
                                dbReviewComments = reader.IsDBNull(7) ? null : reader.GetString(7);
                                dbIsSummaryReviewed = reader.GetBoolean(8);
                                dbIsSummaryReviewedBy = reader.IsDBNull(9) ? null : reader.GetString(9);
                                dbSummaryReviewedAt = reader.IsDBNull(10) ? null : reader.GetDateTime(10);
                                dbSummaryComments = reader.IsDBNull(11) ? null : reader.GetString(11);
                                dbSummaryReviewMethod = reader.IsDBNull(12) ? null : reader.GetString(12);
                                dbCreatedAt = reader.GetDateTime(13);
                                dbCreatedBy = reader.IsDBNull(14) ? null : reader.GetString(14);
                                dbUpdatedAt = reader.IsDBNull(15) ? null : reader.GetDateTime(15);
                                dbUpdatedBy = reader.IsDBNull(16) ? null : reader.GetString(16);
                                dbDeactivatedAt = reader.IsDBNull(17) ? null : reader.GetDateTime(17);
                                dbDeactivatedBy = reader.IsDBNull(18) ? null : reader.GetString(18);
                                dbIsActive = reader.GetBoolean(19);
                            }
                        }

                        if (!rowFound)
                        {
                            throw new AuthoritativeConcurrencyConflictException(
                                $"Authoritative concurrency conflict on FormDetails with SyncId '{mutation.EntitySyncId}'. The target entity no longer exists in DatabaseId '{normDbId}'. Write transaction aborted fail-closed.");
                        }

                        var snap = mutation.FormDetailsOriginalSnapshot;
                        string? conflictField = null;

                        if (snap.FormId != dbFormId)
                        {
                            conflictField = "FormId";
                        }
                        else if (!StringsMatch(snap.EmployeeId, dbEmployeeId))
                        {
                            conflictField = "EmployeeId";
                        }
                        else if (Math.Abs(snap.Amount - dbAmount) > 0.0001)
                        {
                            conflictField = "Amount";
                        }
                        else if (snap.OrderNum != dbOrderNum)
                        {
                            conflictField = "OrderNum";
                        }
                        else if (snap.IsReviewed != dbIsReviewed)
                        {
                            conflictField = "IsReviewed";
                        }
                        else if (!StringsMatch(snap.IsReviewedBy, dbIsReviewedBy))
                        {
                            conflictField = "IsReviewedBy";
                        }
                        else if (!NullableDateTimesMatch(snap.ReviewedAt, dbReviewedAt))
                        {
                            conflictField = "ReviewedAt";
                        }
                        else if (!StringsMatch(snap.ReviewComments, dbReviewComments))
                        {
                            conflictField = "ReviewComments";
                        }
                        else if (snap.IsSummaryReviewed != dbIsSummaryReviewed)
                        {
                            conflictField = "IsSummaryReviewed";
                        }
                        else if (!StringsMatch(snap.IsSummaryReviewedBy, dbIsSummaryReviewedBy))
                        {
                            conflictField = "IsSummaryReviewedBy";
                        }
                        else if (!NullableDateTimesMatch(snap.SummaryReviewedAt, dbSummaryReviewedAt))
                        {
                            conflictField = "SummaryReviewedAt";
                        }
                        else if (!StringsMatch(snap.SummaryComments, dbSummaryComments))
                        {
                            conflictField = "SummaryComments";
                        }
                        else if (!StringsMatch(snap.SummaryReviewMethod, dbSummaryReviewMethod))
                        {
                            conflictField = "SummaryReviewMethod";
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
                                $"Authoritative concurrency conflict on FormDetails with SyncId '{mutation.EntitySyncId}'. Database current values do not match original snapshot (Field '{conflictField}' differed). Write transaction aborted fail-closed.");
                        }
                    }
                    else if (mutation.EntityType == "FormRefernce")
                    {
                        if (mutation.FormRefernceOriginalSnapshot == null)
                        {
                            throw new AuthoritativeTrackingException(
                                $"FormRefernceOriginalSnapshot is required for operation '{mutation.OperationType}' on FormRefernce with SyncId '{mutation.EntitySyncId}'. Write transaction aborted fail-closed.");
                        }

                        await using var readRefCmd = connection.CreateCommand();
                        readRefCmd.Transaction = transaction;
                        readRefCmd.CommandText = @"
                            SELECT [FormId], [ReferencePath], [CreatedAt], [CreatedBy], [UpdatedAt], [UpdatedBy], [DeactivatedAt], [DeactivatedBy], [IsActive]
                            FROM [dbo].[FormRefernce] WITH (UPDLOCK, HOLDLOCK)
                            WHERE [SyncId] = @SyncId;";

                        AddParam(readRefCmd, "@SyncId", mutation.EntitySyncId);

                        bool rowFound = false;
                        int dbFormId = 0;
                        string? dbReferencePath = null;
                        DateTime dbCreatedAt = default;
                        string? dbCreatedBy = null;
                        DateTime? dbUpdatedAt = null;
                        string? dbUpdatedBy = null;
                        DateTime? dbDeactivatedAt = null;
                        string? dbDeactivatedBy = null;
                        bool dbIsActive = false;

                        await using (var reader = await readRefCmd.ExecuteReaderAsync(cancellationToken))
                        {
                            if (await reader.ReadAsync(cancellationToken))
                            {
                                rowFound = true;
                                dbFormId = reader.GetInt32(0);
                                dbReferencePath = reader.IsDBNull(1) ? null : reader.GetString(1);
                                dbCreatedAt = reader.GetDateTime(2);
                                dbCreatedBy = reader.IsDBNull(3) ? null : reader.GetString(3);
                                dbUpdatedAt = reader.IsDBNull(4) ? null : reader.GetDateTime(4);
                                dbUpdatedBy = reader.IsDBNull(5) ? null : reader.GetString(5);
                                dbDeactivatedAt = reader.IsDBNull(6) ? null : reader.GetDateTime(6);
                                dbDeactivatedBy = reader.IsDBNull(7) ? null : reader.GetString(7);
                                dbIsActive = reader.GetBoolean(8);
                            }
                        }

                        if (!rowFound)
                        {
                            throw new AuthoritativeConcurrencyConflictException(
                                $"Authoritative concurrency conflict on FormRefernce with SyncId '{mutation.EntitySyncId}'. The target entity no longer exists in DatabaseId '{normDbId}'. Write transaction aborted fail-closed.");
                        }

                        var snap = mutation.FormRefernceOriginalSnapshot;
                        string? conflictField = null;

                        if (snap.FormId != dbFormId)
                        {
                            conflictField = "FormId";
                        }
                        else if (!StringsMatch(snap.ReferencePath, dbReferencePath))
                        {
                            conflictField = "ReferencePath";
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
                                $"Authoritative concurrency conflict on FormRefernce with SyncId '{mutation.EntitySyncId}'. Database current values do not match original snapshot (Field '{conflictField}' differed). Write transaction aborted fail-closed.");
                        }
                    }
                    else
                    {
                        throw new AuthoritativeTrackingException($"Unsupported entity type '{mutation.EntityType}' for authoritative tracking.");
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
                            WHERE DatabaseId = @DatabaseId AND EntityType = @EntityType AND EntitySyncId = @EntitySyncId
                        )
                        BEGIN
                            UPDATE [sync].[Tombstones]
                            SET ServerVersion = @ServerVersion,
                                DeletedAtUtc = @DeletedAtUtc,
                                NaturalKey = NULL
                            WHERE DatabaseId = @DatabaseId AND EntityType = @EntityType AND EntitySyncId = @EntitySyncId;
                        END
                        ELSE
                        BEGIN
                            INSERT INTO [sync].[Tombstones]
                            ([DatabaseId], [EntityType], [EntitySyncId], [NaturalKey], [ServerVersion], [DeletedAtUtc])
                            VALUES
                            (@DatabaseId, @EntityType, @EntitySyncId, NULL, @ServerVersion, @DeletedAtUtc);
                        END";

                    AddParam(tombstoneCmd, "@DatabaseId", normDbId);
                    AddParam(tombstoneCmd, "@EntityType", mutation.EntityType);
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
                    (@ServerVersion, @DatabaseId, @EntityType, @EntitySyncId, @OperationType, @OriginDeviceId, @TimestampUtc);";

                AddParam(feedCmd, "@ServerVersion", currentServerVersion);
                AddParam(feedCmd, "@DatabaseId", normDbId);
                AddParam(feedCmd, "@EntityType", mutation.EntityType);
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
