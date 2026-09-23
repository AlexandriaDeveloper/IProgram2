#nullable enable
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Auth.Infrastructure.Sync.Push;
using Core.Exceptions;
using Core.Interfaces;
using Core.Models.Sync;
using Microsoft.Extensions.Logging;

namespace Auth.Infrastructure.Sync.Pull
{
    public class AzureFencedBatchReader : IAzureFencedBatchReader
    {
        private readonly IRemoteDatabaseConnectionFactory _remoteConnectionFactory;
        private readonly ILogger<AzureFencedBatchReader> _logger;

        private static readonly HashSet<string> AllowedEntityTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "Daily", "Form", "FormDetails", "FormRefernce"
        };

        private static readonly HashSet<string> AllowedOperationTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "INSERT", "UPDATE", "SOFT_DELETE", "HARD_DELETE"
        };

        public AzureFencedBatchReader(
            IRemoteDatabaseConnectionFactory remoteConnectionFactory,
            ILogger<AzureFencedBatchReader> logger)
        {
            _remoteConnectionFactory = remoteConnectionFactory ?? throw new ArgumentNullException(nameof(remoteConnectionFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<FencedPullBatch> ReadFencedBatchAsync(
            string databaseId,
            long localLastServerVersion,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(databaseId))
            {
                throw new InvalidDatabaseSelectionException("Canonical database ID is required.");
            }

            var normDbId = databaseId.Trim();
            if (normDbId != "2026" && normDbId != "2027")
            {
                throw new InvalidDatabaseSelectionException($"Unsupported canonical DatabaseId '{databaseId}'. Expected '2026' or '2027'.");
            }

            await using var connection = await _remoteConnectionFactory.CreateOpenConnectionAsync(normDbId, cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

            try
            {
                // Phase 1: High-Watermark Fence: Lock and read ServerState with (UPDLOCK, HOLDLOCK)
                long highWatermark;
                await using (var stateCmd = connection.CreateCommand())
                {
                    stateCmd.Transaction = transaction;
                    stateCmd.CommandText = @"
                        SELECT CurrentVersion
                        FROM [sync].[ServerState] WITH (UPDLOCK, HOLDLOCK)
                        WHERE DatabaseId = @DatabaseId;";

                    AddParam(stateCmd, "@DatabaseId", normDbId);

                    var scalar = await stateCmd.ExecuteScalarAsync(cancellationToken);
                    if (scalar == null || scalar == DBNull.Value)
                    {
                        throw new SyncLocalStateMissingException($"ServerState record does not exist for DatabaseId '{normDbId}'.");
                    }
                    highWatermark = Convert.ToInt64(scalar);
                }

                _logger.LogInformation("Azure High-Watermark read: H={HighWatermark}, Local L={LowWatermark} for DatabaseId {DatabaseId}.",
                    highWatermark, localLastServerVersion, normDbId);

                // Checkpoint comparison
                if (highWatermark == localLastServerVersion)
                {
                    await transaction.CommitAsync(cancellationToken);
                    return FencedPullBatch.CreateNoOp(normDbId, localLastServerVersion);
                }

                if (highWatermark < localLastServerVersion)
                {
                    throw new SyncPullCheckpointAheadOfServerException(
                        localLastServerVersion,
                        highWatermark,
                        $"Local checkpoint ({localLastServerVersion}) is ahead of Azure server version ({highWatermark}) for DatabaseId '{normDbId}'.");
                }

                // Phase 2: Feed Window Validation
                var rawFeedEvents = new List<ServerChangeFeed>();
                await using (var feedCmd = connection.CreateCommand())
                {
                    feedCmd.Transaction = transaction;
                    feedCmd.CommandText = @"
                        SELECT FeedId, ServerVersion, DatabaseId, EntityType, EntitySyncId, OperationType, OriginDeviceId, TimestampUtc
                        FROM [sync].[ServerChangeFeed]
                        WHERE DatabaseId = @DatabaseId
                          AND ServerVersion > @LowWatermark
                          AND ServerVersion <= @HighWatermark
                        ORDER BY ServerVersion ASC;";

                    AddParam(feedCmd, "@DatabaseId", normDbId);
                    AddParam(feedCmd, "@LowWatermark", localLastServerVersion);
                    AddParam(feedCmd, "@HighWatermark", highWatermark);

                    await using var reader = await feedCmd.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        rawFeedEvents.Add(new ServerChangeFeed
                        {
                            FeedId = reader.GetInt64(0),
                            ServerVersion = reader.GetInt64(1),
                            DatabaseId = reader.GetString(2),
                            EntityType = reader.GetString(3),
                            EntitySyncId = reader.GetGuid(4),
                            OperationType = reader.GetString(5),
                            OriginDeviceId = reader.GetGuid(6),
                            TimestampUtc = reader.GetDateTime(7)
                        });
                    }
                }

                // Validate strict contiguous sequence L+1 ... H without gaps or duplicates
                var expectedCount = (int)(highWatermark - localLastServerVersion);
                if (rawFeedEvents.Count != expectedCount)
                {
                    _logger.LogWarning("Feed count mismatch for DatabaseId {DatabaseId}. Expected {ExpectedCount}, actual {ActualCount}.",
                        normDbId, expectedCount, rawFeedEvents.Count);
                }

                long expectedNext = localLastServerVersion + 1;
                long? lastSeenVersion = null;

                foreach (var evt in rawFeedEvents)
                {
                    if (evt.EntitySyncId == Guid.Empty)
                    {
                        throw new SyncPullFeedMalformedException(
                            $"Feed event at ServerVersion {evt.ServerVersion} has empty EntitySyncId for DatabaseId '{normDbId}'.");
                    }

                    if (lastSeenVersion.HasValue && evt.ServerVersion == lastSeenVersion.Value)
                    {
                        throw new SyncPullDuplicateVersionException(
                            $"Duplicate ServerVersion '{evt.ServerVersion}' detected in ServerChangeFeed for DatabaseId '{normDbId}'.");
                    }

                    if (evt.ServerVersion != expectedNext)
                    {
                        throw new SyncPullFeedGapException(
                            $"Feed gap detected in ServerChangeFeed for DatabaseId '{normDbId}'. Expected version {expectedNext}, found {evt.ServerVersion}.");
                    }

                    if (!string.Equals(evt.DatabaseId, normDbId, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new SyncMetadataMismatchException(
                            $"ServerChangeFeed event DatabaseId '{evt.DatabaseId}' does not match expected '{normDbId}'.");
                    }

                    if (!AllowedEntityTypes.Contains(evt.EntityType))
                    {
                        throw new SyncPullUnsupportedEntityTypeException(
                            $"Unsupported EntityType '{evt.EntityType}' detected at ServerVersion {evt.ServerVersion}. Only 'Daily', 'Form', 'FormDetails', 'FormRefernce' are supported.");
                    }

                    if (!AllowedOperationTypes.Contains(evt.OperationType))
                    {
                        throw new SyncPullUnsupportedOperationTypeException(
                            $"Unsupported OperationType '{evt.OperationType}' detected at ServerVersion {evt.ServerVersion}.");
                    }

                    lastSeenVersion = evt.ServerVersion;
                    expectedNext++;
                }

                if (expectedNext - 1 != highWatermark)
                {
                    throw new SyncPullFeedGapException(
                        $"ServerChangeFeed does not reach HighWatermark {highWatermark}. Last version seen was {expectedNext - 1}.");
                }

                // Phase 3: Coalescing Algorithm
                // Group feed events in window (L, H] by (EntityType, EntitySyncId) and select the terminal event with highest ServerVersion
                var groupedEvents = rawFeedEvents
                    .GroupBy(e => (EntityType: CanonicalizeEntityType(e.EntityType), e.EntitySyncId))
                    .Select(g => g.OrderByDescending(e => e.ServerVersion).First())
                    .OrderBy(e => e.ServerVersion)
                    .ToList();

                // Phase 4: Materialize terminal state under fence
                var commands = new List<PullCommand>();
                foreach (var terminalEvent in groupedEvents)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var entityType = CanonicalizeEntityType(terminalEvent.EntityType);
                    var syncId = terminalEvent.EntitySyncId;
                    var terminalVersion = terminalEvent.ServerVersion;
                    var tableName = GetTableNameForEntityType(entityType);

                    if (string.Equals(terminalEvent.OperationType, "HARD_DELETE", StringComparison.OrdinalIgnoreCase))
                    {
                        // Requirement 10: Verify Tombstones has exactly one matching record
                        await using (var tombstoneCmd = connection.CreateCommand())
                        {
                            tombstoneCmd.Transaction = transaction;
                            tombstoneCmd.CommandText = @"
                                SELECT COUNT(*)
                                FROM [sync].[Tombstones]
                                WHERE DatabaseId = @DatabaseId
                                  AND EntityType = @EntityType
                                  AND EntitySyncId = @EntitySyncId
                                  AND ServerVersion = @ServerVersion;";

                            AddParam(tombstoneCmd, "@DatabaseId", normDbId);
                            AddParam(tombstoneCmd, "@EntityType", entityType);
                            AddParam(tombstoneCmd, "@EntitySyncId", syncId);
                            AddParam(tombstoneCmd, "@ServerVersion", terminalVersion);

                            var tombstoneCount = Convert.ToInt32(await tombstoneCmd.ExecuteScalarAsync(cancellationToken));
                            if (tombstoneCount != 1)
                            {
                                throw new SyncPullTombstoneValidationException(
                                    $"Terminal HARD_DELETE for {entityType} SyncId '{syncId}' requires exactly one matching Tombstone at version {terminalVersion}, found {tombstoneCount}.");
                            }
                        }

                        // Verify authoritative table does NOT contain this row
                        await using (var existsCmd = connection.CreateCommand())
                        {
                            existsCmd.Transaction = transaction;
                            existsCmd.CommandText = $"SELECT COUNT(*) FROM {tableName} WHERE SyncId = @SyncId;";

                            AddParam(existsCmd, "@SyncId", syncId);

                            var rowCount = Convert.ToInt32(await existsCmd.ExecuteScalarAsync(cancellationToken));
                            if (rowCount > 0)
                            {
                                throw new SyncPullTombstoneEntityStillActiveException(
                                    $"Terminal HARD_DELETE {entityType} SyncId '{syncId}' still exists in authoritative {tableName}.");
                            }
                        }

                        commands.Add(PullCommand.CreateDelete(entityType, syncId, terminalVersion));
                    }
                    else
                    {
                        // Terminal event is INSERT, UPDATE, or SOFT_DELETE
                        // Verify no tombstone exists for this EntityType + SyncId
                        await using (var tombstoneCheckCmd = connection.CreateCommand())
                        {
                            tombstoneCheckCmd.Transaction = transaction;
                            tombstoneCheckCmd.CommandText = @"
                                SELECT COUNT(*)
                                FROM [sync].[Tombstones]
                                WHERE DatabaseId = @DatabaseId
                                  AND EntityType = @EntityType
                                  AND EntitySyncId = @EntitySyncId;";

                            AddParam(tombstoneCheckCmd, "@DatabaseId", normDbId);
                            AddParam(tombstoneCheckCmd, "@EntityType", entityType);
                            AddParam(tombstoneCheckCmd, "@EntitySyncId", syncId);

                            var tombstoneCount = Convert.ToInt32(await tombstoneCheckCmd.ExecuteScalarAsync(cancellationToken));
                            if (tombstoneCount > 0)
                            {
                                throw new SyncPullTombstoneEntityStillActiveException(
                                    $"Terminal mutation '{terminalEvent.OperationType}' for {entityType} SyncId '{syncId}' conflicts with existing Tombstone.");
                            }
                        }

                        switch (entityType)
                        {
                            case "Daily":
                            {
                                DailyAuthoritativeSnapshot? snapshot = null;
                                await using (var snapshotCmd = connection.CreateCommand())
                                {
                                    snapshotCmd.Transaction = transaction;
                                    snapshotCmd.CommandText = @"
                                        SELECT SyncId, Name, DailyDate, Closed, CreatedAt, CreatedBy, UpdatedAt, UpdatedBy, DeactivatedAt, DeactivatedBy, IsActive
                                        FROM [dbo].[Daily]
                                        WHERE SyncId = @SyncId;";

                                    AddParam(snapshotCmd, "@SyncId", syncId);

                                    await using var snapReader = await snapshotCmd.ExecuteReaderAsync(cancellationToken);
                                    if (await snapReader.ReadAsync(cancellationToken))
                                    {
                                        snapshot = new DailyAuthoritativeSnapshot
                                        {
                                            SyncId = snapReader.GetGuid(0),
                                            Name = snapReader.GetString(1),
                                            DailyDate = snapReader.GetDateTime(2),
                                            Closed = snapReader.GetBoolean(3),
                                            CreatedAt = snapReader.GetDateTime(4),
                                            CreatedBy = snapReader.IsDBNull(5) ? null : snapReader.GetString(5),
                                            UpdatedAt = snapReader.IsDBNull(6) ? null : snapReader.GetDateTime(6),
                                            UpdatedBy = snapReader.IsDBNull(7) ? null : snapReader.GetString(7),
                                            DeactivatedAt = snapReader.IsDBNull(8) ? null : snapReader.GetDateTime(8),
                                            DeactivatedBy = snapReader.IsDBNull(9) ? null : snapReader.GetString(9),
                                            IsActive = snapReader.GetBoolean(10)
                                        };
                                    }
                                }

                                if (snapshot == null)
                                {
                                    throw new SyncPullAuthoritativeRowMissingException(
                                        $"Terminal mutation '{terminalEvent.OperationType}' for Daily SyncId '{syncId}' not found in authoritative dbo.Daily table.");
                                }

                                if (string.Equals(terminalEvent.OperationType, "SOFT_DELETE", StringComparison.OrdinalIgnoreCase) && snapshot.IsActive)
                                {
                                    throw new SyncPullAuthoritativeStateMismatchException(
                                        $"Terminal SOFT_DELETE mutation for Daily SyncId '{syncId}' at version {terminalVersion} has IsActive=true in authoritative dbo.Daily.");
                                }

                                commands.Add(PullCommand.CreateUpsert(snapshot, terminalVersion));
                                break;
                            }
                            case "Form":
                            {
                                FormAuthoritativeSnapshot? snapshot = null;
                                await using (var snapshotCmd = connection.CreateCommand())
                                {
                                    snapshotCmd.Transaction = transaction;
                                    snapshotCmd.CommandText = @"
                                        SELECT f.SyncId, d.SyncId AS DailySyncId, f.[Index], f.Name, f.Description,
                                               f.CreatedAt, f.CreatedBy, f.UpdatedAt, f.UpdatedBy, f.DeactivatedAt, f.DeactivatedBy, f.IsActive
                                        FROM [dbo].[Form] f
                                        LEFT JOIN [dbo].[Daily] d ON f.DailyId = d.Id
                                        WHERE f.SyncId = @SyncId;";

                                    AddParam(snapshotCmd, "@SyncId", syncId);

                                    await using var snapReader = await snapshotCmd.ExecuteReaderAsync(cancellationToken);
                                    if (await snapReader.ReadAsync(cancellationToken))
                                    {
                                        snapshot = new FormAuthoritativeSnapshot
                                        {
                                            SyncId = snapReader.GetGuid(0),
                                            DailySyncId = snapReader.IsDBNull(1) ? null : snapReader.GetGuid(1),
                                            Index = snapReader.IsDBNull(2) ? null : snapReader.GetInt32(2),
                                            Name = snapReader.GetString(3),
                                            Description = snapReader.IsDBNull(4) ? null : snapReader.GetString(4),
                                            CreatedAt = snapReader.GetDateTime(5),
                                            CreatedBy = snapReader.IsDBNull(6) ? null : snapReader.GetString(6),
                                            UpdatedAt = snapReader.IsDBNull(7) ? null : snapReader.GetDateTime(7),
                                            UpdatedBy = snapReader.IsDBNull(8) ? null : snapReader.GetString(8),
                                            DeactivatedAt = snapReader.IsDBNull(9) ? null : snapReader.GetDateTime(9),
                                            DeactivatedBy = snapReader.IsDBNull(10) ? null : snapReader.GetString(10),
                                            IsActive = snapReader.GetBoolean(11)
                                        };
                                    }
                                }

                                if (snapshot == null)
                                {
                                    throw new SyncPullAuthoritativeRowMissingException(
                                        $"Terminal mutation '{terminalEvent.OperationType}' for Form SyncId '{syncId}' not found in authoritative dbo.Form table.");
                                }

                                if (string.Equals(terminalEvent.OperationType, "SOFT_DELETE", StringComparison.OrdinalIgnoreCase) && snapshot.IsActive)
                                {
                                    throw new SyncPullAuthoritativeStateMismatchException(
                                        $"Terminal SOFT_DELETE mutation for Form SyncId '{syncId}' at version {terminalVersion} has IsActive=true in authoritative dbo.Form.");
                                }

                                commands.Add(PullCommand.CreateFormUpsert(snapshot, terminalVersion));
                                break;
                            }
                            case "FormDetails":
                            {
                                FormDetailsAuthoritativeSnapshot? snapshot = null;
                                await using (var snapshotCmd = connection.CreateCommand())
                                {
                                    snapshotCmd.Transaction = transaction;
                                    snapshotCmd.CommandText = @"
                                        SELECT fd.SyncId, f.SyncId AS FormSyncId, fd.EmployeeId, fd.Amount, fd.OrderNum,
                                               fd.IsReviewed, fd.IsReviewedBy, fd.ReviewedAt, fd.ReviewComments,
                                               fd.IsSummaryReviewed, fd.IsSummaryReviewedBy, fd.SummaryReviewedAt, fd.SummaryComments, fd.SummaryReviewMethod,
                                               fd.CreatedAt, fd.CreatedBy, fd.UpdatedAt, fd.UpdatedBy, fd.DeactivatedAt, fd.DeactivatedBy, fd.IsActive
                                        FROM [dbo].[FormDetails] fd
                                        INNER JOIN [dbo].[Form] f ON fd.FormId = f.Id
                                        WHERE fd.SyncId = @SyncId;";

                                    AddParam(snapshotCmd, "@SyncId", syncId);

                                    await using var snapReader = await snapshotCmd.ExecuteReaderAsync(cancellationToken);
                                    if (await snapReader.ReadAsync(cancellationToken))
                                    {
                                        snapshot = new FormDetailsAuthoritativeSnapshot
                                        {
                                            SyncId = snapReader.GetGuid(0),
                                            FormSyncId = snapReader.GetGuid(1),
                                            EmployeeId = snapReader.IsDBNull(2) ? null : snapReader.GetString(2),
                                            Amount = snapReader.GetDouble(3),
                                            OrderNum = snapReader.GetInt32(4),
                                            IsReviewed = snapReader.GetBoolean(5),
                                            IsReviewedBy = snapReader.IsDBNull(6) ? null : snapReader.GetString(6),
                                            ReviewedAt = snapReader.IsDBNull(7) ? null : snapReader.GetDateTime(7),
                                            ReviewComments = snapReader.IsDBNull(8) ? null : snapReader.GetString(8),
                                            IsSummaryReviewed = snapReader.GetBoolean(9),
                                            IsSummaryReviewedBy = snapReader.IsDBNull(10) ? null : snapReader.GetString(10),
                                            SummaryReviewedAt = snapReader.IsDBNull(11) ? null : snapReader.GetDateTime(11),
                                            SummaryComments = snapReader.IsDBNull(12) ? null : snapReader.GetString(12),
                                            SummaryReviewMethod = snapReader.IsDBNull(13) ? null : snapReader.GetString(13),
                                            CreatedAt = snapReader.GetDateTime(14),
                                            CreatedBy = snapReader.IsDBNull(15) ? null : snapReader.GetString(15),
                                            UpdatedAt = snapReader.IsDBNull(16) ? null : snapReader.GetDateTime(16),
                                            UpdatedBy = snapReader.IsDBNull(17) ? null : snapReader.GetString(17),
                                            DeactivatedAt = snapReader.IsDBNull(18) ? null : snapReader.GetDateTime(18),
                                            DeactivatedBy = snapReader.IsDBNull(19) ? null : snapReader.GetString(19),
                                            IsActive = snapReader.GetBoolean(20)
                                        };
                                    }
                                }

                                if (snapshot == null)
                                {
                                    throw new SyncPullAuthoritativeRowMissingException(
                                        $"Terminal mutation '{terminalEvent.OperationType}' for FormDetails SyncId '{syncId}' not found in authoritative dbo.FormDetails table.");
                                }

                                if (string.Equals(terminalEvent.OperationType, "SOFT_DELETE", StringComparison.OrdinalIgnoreCase) && snapshot.IsActive)
                                {
                                    throw new SyncPullAuthoritativeStateMismatchException(
                                        $"Terminal SOFT_DELETE mutation for FormDetails SyncId '{syncId}' at version {terminalVersion} has IsActive=true in authoritative dbo.FormDetails.");
                                }

                                commands.Add(PullCommand.CreateFormDetailsUpsert(snapshot, terminalVersion));
                                break;
                            }
                            case "FormRefernce":
                            {
                                FormRefernceAuthoritativeSnapshot? snapshot = null;
                                await using (var snapshotCmd = connection.CreateCommand())
                                {
                                    snapshotCmd.Transaction = transaction;
                                    snapshotCmd.CommandText = @"
                                        SELECT fr.SyncId, f.SyncId AS FormSyncId, fr.ReferencePath,
                                               fr.CreatedAt, fr.CreatedBy, fr.UpdatedAt, fr.UpdatedBy, fr.DeactivatedAt, fr.DeactivatedBy, fr.IsActive
                                        FROM [dbo].[FormRefernce] fr
                                        INNER JOIN [dbo].[Form] f ON fr.FormId = f.Id
                                        WHERE fr.SyncId = @SyncId;";

                                    AddParam(snapshotCmd, "@SyncId", syncId);

                                    await using var snapReader = await snapshotCmd.ExecuteReaderAsync(cancellationToken);
                                    if (await snapReader.ReadAsync(cancellationToken))
                                    {
                                        snapshot = new FormRefernceAuthoritativeSnapshot
                                        {
                                            SyncId = snapReader.GetGuid(0),
                                            FormSyncId = snapReader.GetGuid(1),
                                            ReferencePath = snapReader.IsDBNull(2) ? null : snapReader.GetString(2),
                                            CreatedAt = snapReader.GetDateTime(3),
                                            CreatedBy = snapReader.IsDBNull(4) ? null : snapReader.GetString(4),
                                            UpdatedAt = snapReader.IsDBNull(5) ? null : snapReader.GetDateTime(5),
                                            UpdatedBy = snapReader.IsDBNull(6) ? null : snapReader.GetString(6),
                                            DeactivatedAt = snapReader.IsDBNull(7) ? null : snapReader.GetDateTime(7),
                                            DeactivatedBy = snapReader.IsDBNull(8) ? null : snapReader.GetString(8),
                                            IsActive = snapReader.GetBoolean(9)
                                        };
                                    }
                                }

                                if (snapshot == null)
                                {
                                    throw new SyncPullAuthoritativeRowMissingException(
                                        $"Terminal mutation '{terminalEvent.OperationType}' for FormRefernce SyncId '{syncId}' not found in authoritative dbo.FormRefernce table.");
                                }

                                if (string.Equals(terminalEvent.OperationType, "SOFT_DELETE", StringComparison.OrdinalIgnoreCase) && snapshot.IsActive)
                                {
                                    throw new SyncPullAuthoritativeStateMismatchException(
                                        $"Terminal SOFT_DELETE mutation for FormRefernce SyncId '{syncId}' at version {terminalVersion} has IsActive=true in authoritative dbo.FormRefernce.");
                                }

                                commands.Add(PullCommand.CreateFormRefernceUpsert(snapshot, terminalVersion));
                                break;
                            }
                            default:
                                throw new SyncPullUnsupportedEntityTypeException($"Unsupported EntityType '{entityType}'.");
                        }
                    }
                }

                // Phase 5: Commit and release Azure fence
                await transaction.CommitAsync(cancellationToken);

                _logger.LogInformation("Fenced Azure pull batch materialized successfully for DatabaseId {DatabaseId}: {CommandCount} coalesced commands in range ({LowWatermark}, {HighWatermark}].",
                    normDbId, commands.Count, localLastServerVersion, highWatermark);

                return new FencedPullBatch
                {
                    DatabaseId = normDbId,
                    LowWatermark = localLastServerVersion,
                    HighWatermark = highWatermark,
                    IsNoOp = false,
                    Commands = commands
                };
            }
            catch
            {
                try
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                }
                catch
                {
                    // Ignore rollback errors on faulted transaction
                }
                throw;
            }
        }

        private static void AddParam(DbCommand cmd, string name, object value)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }

        private static string CanonicalizeEntityType(string entityType)
        {
            if (string.Equals(entityType, "Daily", StringComparison.OrdinalIgnoreCase)) return "Daily";
            if (string.Equals(entityType, "Form", StringComparison.OrdinalIgnoreCase)) return "Form";
            if (string.Equals(entityType, "FormDetails", StringComparison.OrdinalIgnoreCase)) return "FormDetails";
            if (string.Equals(entityType, "FormRefernce", StringComparison.OrdinalIgnoreCase)) return "FormRefernce";
            throw new SyncPullUnsupportedEntityTypeException($"Unsupported EntityType '{entityType}'.");
        }

        private static string GetTableNameForEntityType(string entityType) => entityType switch
        {
            "Daily" => "[dbo].[Daily]",
            "Form" => "[dbo].[Form]",
            "FormDetails" => "[dbo].[FormDetails]",
            "FormRefernce" => "[dbo].[FormRefernce]",
            _ => throw new SyncPullUnsupportedEntityTypeException($"Unsupported EntityType '{entityType}'.")
        };
    }
}
