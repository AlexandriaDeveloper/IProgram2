#nullable enable
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using System.Threading;
using System.Threading.Tasks;
using Core.Exceptions;
using Core.Interfaces;
using Core.Models.Sync;
using Microsoft.Extensions.Logging;

namespace Auth.Infrastructure.Sync.Pull
{
    public class LocalPullTransactionCoordinator : ILocalPullTransactionCoordinator
    {
        private readonly ISyncConnectionProvider _syncConnectionProvider;
        private readonly ILogger<LocalPullTransactionCoordinator> _logger;

        public LocalPullTransactionCoordinator(
            ISyncConnectionProvider syncConnectionProvider,
            ILogger<LocalPullTransactionCoordinator> logger)
        {
            _syncConnectionProvider = syncConnectionProvider ?? throw new ArgumentNullException(nameof(syncConnectionProvider));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<PullResultDto> ApplyPullBatchAsync(
            string databaseId,
            FencedPullBatch batch,
            Guid leaseToken,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(databaseId))
            {
                throw new InvalidDatabaseSelectionException("Canonical database ID is required.");
            }
            if (batch == null) throw new ArgumentNullException(nameof(batch));
            if (leaseToken == Guid.Empty) throw new ArgumentException("Lease token cannot be empty.", nameof(leaseToken));

            var normDbId = databaseId.Trim();
            if (normDbId != "2026" && normDbId != "2027")
            {
                throw new InvalidDatabaseSelectionException($"Unsupported canonical DatabaseId '{databaseId}'. Expected '2026' or '2027'.");
            }

            // P0: Exact batch database match validation
            if (!string.Equals(batch.DatabaseId, normDbId, StringComparison.Ordinal))
            {
                throw new SyncPullBatchDatabaseMismatchException(
                    $"Batch DatabaseId '{batch.DatabaseId}' does not match target database '{normDbId}'.");
            }

            // P0: Pre-flight watermark and invariant validation
            if (batch.LowWatermark < 0)
            {
                throw new SyncPullBatchMalformedException($"Invalid batch LowWatermark {batch.LowWatermark}. Must be >= 0.");
            }

            if (batch.HighWatermark < batch.LowWatermark)
            {
                throw new SyncPullBatchMalformedException(
                    $"Invalid batch watermarks: HighWatermark ({batch.HighWatermark}) is less than LowWatermark ({batch.LowWatermark}).");
            }

            if (batch.IsNoOp)
            {
                if (batch.HighWatermark != batch.LowWatermark)
                {
                    throw new SyncPullBatchMalformedException(
                        $"NoOp batch must have HighWatermark ({batch.HighWatermark}) == LowWatermark ({batch.LowWatermark}).");
                }
                if (batch.Commands != null && batch.Commands.Count > 0)
                {
                    throw new SyncPullBatchMalformedException(
                        $"NoOp batch must have empty commands list, but found {batch.Commands.Count} commands.");
                }
            }
            else
            {
                if (batch.HighWatermark <= batch.LowWatermark)
                {
                    throw new SyncPullBatchMalformedException(
                        $"Non-NoOp batch must have HighWatermark ({batch.HighWatermark}) > LowWatermark ({batch.LowWatermark}).");
                }
                if (batch.Commands == null)
                {
                    throw new SyncPullBatchMalformedException("Non-NoOp batch commands list cannot be null.");
                }
            }

            if (batch.Commands != null)
            {
                foreach (var cmd in batch.Commands)
                {
                    if (cmd.EntitySyncId == Guid.Empty)
                    {
                        throw new SyncPullBatchMalformedException("Batch command EntitySyncId cannot be empty Guid.");
                    }

                    if (cmd.TerminalServerVersion <= batch.LowWatermark || cmd.TerminalServerVersion > batch.HighWatermark)
                    {
                        throw new SyncPullBatchMalformedException(
                            $"Command TerminalServerVersion {cmd.TerminalServerVersion} for entity '{cmd.EntitySyncId}' is outside batch range ({batch.LowWatermark}, {batch.HighWatermark}].");
                    }

                    if (cmd.CommandType == PullCommandType.Upsert)
                    {
                        if (cmd.Snapshot == null)
                        {
                            throw new SyncPullBatchMalformedException($"Upsert command for entity '{cmd.EntitySyncId}' has null Snapshot.");
                        }
                        if (cmd.Snapshot.SyncId != cmd.EntitySyncId)
                        {
                            throw new SyncPullBatchMalformedException(
                                $"Upsert command EntitySyncId '{cmd.EntitySyncId}' does not match Snapshot.SyncId '{cmd.Snapshot.SyncId}'.");
                        }
                    }
                    else if (cmd.CommandType == PullCommandType.Delete)
                    {
                        if (cmd.Snapshot != null)
                        {
                            throw new SyncPullBatchMalformedException($"Delete command for entity '{cmd.EntitySyncId}' must have null Snapshot.");
                        }
                    }
                    else
                    {
                        throw new SyncPullBatchMalformedException($"Unsupported batch command type '{cmd.CommandType}'.");
                    }
                }
            }

            var localConnStr = _syncConnectionProvider.GetLocalConnectionString(normDbId);

            var builder = new SqlConnectionStringBuilder(localConnStr);
            DatabaseBindingValidator.ValidateLocalBinding(builder.DataSource, builder.InitialCatalog);
            DatabaseBindingValidator.ValidateTargetDatabase(normDbId, builder.InitialCatalog, isLocalTarget: true);

            var result = new PullResultDto
            {
                DatabaseId = normDbId,
                PreviousWatermark = batch.LowWatermark,
                FinalServerVersion = batch.HighWatermark,
                IsNoOp = batch.IsNoOp,
                Operations = new List<PullOperationResult>()
            };

            if (batch.IsNoOp)
            {
                result.TotalProcessed = 0;
                result.Succeeded = 0;
                result.Failed = 0;
                result.Message = "No changes to pull. Local checkpoint matches server version.";
                return result;
            }

            await using var conn = new SqlConnection(localConnStr);
            await conn.OpenAsync(cancellationToken);
            await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

            try
            {
                // Step 1: Verify LocalState with (UPDLOCK, HOLDLOCK)
                long localCheckpoint;
                Guid? activeToken;
                DateTime? leaseExpiresAt;

                await using (var stateCmd = conn.CreateCommand())
                {
                    stateCmd.Transaction = tx;
                    stateCmd.CommandText = @"
                        SELECT LastServerVersion, ActiveLeaseToken, LeaseExpiresAtUtc
                        FROM [sync].[LocalState] WITH (UPDLOCK, HOLDLOCK)
                        WHERE DatabaseId = @DatabaseId;";

                    AddParam(stateCmd, "@DatabaseId", normDbId);

                    await using var reader = await stateCmd.ExecuteReaderAsync(cancellationToken);
                    if (!await reader.ReadAsync(cancellationToken))
                    {
                        throw new SyncLocalStateMissingException($"LocalState record does not exist for DatabaseId '{normDbId}'.");
                    }

                    localCheckpoint = reader.GetInt64(0);
                    if (reader.IsDBNull(1))
                    {
                        activeToken = null;
                    }
                    else
                    {
                        var rawToken = reader.GetValue(1);
                        activeToken = rawToken switch
                        {
                            Guid g => g,
                            string s when Guid.TryParse(s, out var pg) => pg,
                            _ => null
                        };
                    }
                    leaseExpiresAt = reader.IsDBNull(2) ? null : reader.GetDateTime(2);
                }

                if (localCheckpoint != batch.LowWatermark)
                {
                    throw new SyncPullLocalCheckpointChangedException(
                        $"Local checkpoint changed mid-pull for DatabaseId '{normDbId}'. Expected {batch.LowWatermark}, found {localCheckpoint}.");
                }

                if (!activeToken.HasValue || activeToken.Value != leaseToken ||
                    !leaseExpiresAt.HasValue || leaseExpiresAt.Value < DateTime.UtcNow)
                {
                    throw new SyncLeaseExpiredException(
                        $"Pull lease expired, stolen, or invalid during atomic apply for DatabaseId '{normDbId}'.");
                }

                // Step 2: Check Local Outbox - Block if PENDING, IN_PROGRESS, or FAILED
                await using (var outboxCheckCmd = conn.CreateCommand())
                {
                    outboxCheckCmd.Transaction = tx;
                    outboxCheckCmd.CommandText = @"
                        SELECT COUNT(*)
                        FROM [sync].[LocalOutbox]
                        WHERE DatabaseId = @DatabaseId
                          AND Status IN ('PENDING', 'IN_PROGRESS', 'FAILED');";

                    AddParam(outboxCheckCmd, "@DatabaseId", normDbId);

                    var pendingCount = Convert.ToInt32(await outboxCheckCmd.ExecuteScalarAsync(cancellationToken));
                    if (pendingCount > 0)
                    {
                        throw new SyncPullBlockedLocalChangesPendingException(
                            $"Pull blocked: {pendingCount} unmerged local outbox changes found (PENDING/IN_PROGRESS/FAILED) for DatabaseId '{normDbId}'.");
                    }
                }

                // Step 3: Apply Coalesced Commands by SyncId Only
                if (batch.Commands != null)
                {
                    foreach (var cmd in batch.Commands)
                    {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (cmd.CommandType == PullCommandType.Delete)
                    {
                        await using var delCmd = conn.CreateCommand();
                        delCmd.Transaction = tx;
                        delCmd.CommandText = "DELETE FROM [dbo].[Daily] WHERE SyncId = @SyncId;";
                        AddParam(delCmd, "@SyncId", cmd.EntitySyncId);

                        var affected = await delCmd.ExecuteNonQueryAsync(cancellationToken);
                        result.Operations.Add(new PullOperationResult
                        {
                            EntitySyncId = cmd.EntitySyncId,
                            OperationType = "HARD_DELETE",
                            Status = affected > 0 ? "SUCCESS" : "NO_OP",
                            TerminalServerVersion = cmd.TerminalServerVersion
                        });
                        result.TotalProcessed++;
                        result.Succeeded++;
                    }
                    else if (cmd.CommandType == PullCommandType.Upsert && cmd.Snapshot != null)
                    {
                        var snap = cmd.Snapshot;

                        // Check existence by SyncId
                        bool existsLocally;
                        await using (var checkCmd = conn.CreateCommand())
                        {
                            checkCmd.Transaction = tx;
                            checkCmd.CommandText = "SELECT COUNT(*) FROM [dbo].[Daily] WHERE SyncId = @SyncId;";
                            AddParam(checkCmd, "@SyncId", snap.SyncId);
                            existsLocally = Convert.ToInt32(await checkCmd.ExecuteScalarAsync(cancellationToken)) > 0;
                        }

                        if (existsLocally)
                        {
                            // UPDATE existing row without modifying integer Id
                            await using var updateCmd = conn.CreateCommand();
                            updateCmd.Transaction = tx;
                            updateCmd.CommandText = @"
                                UPDATE [dbo].[Daily]
                                SET Name = @Name,
                                    DailyDate = @DailyDate,
                                    Closed = @Closed,
                                    CreatedAt = @CreatedAt,
                                    CreatedBy = @CreatedBy,
                                    UpdatedAt = @UpdatedAt,
                                    UpdatedBy = @UpdatedBy,
                                    DeactivatedAt = @DeactivatedAt,
                                    DeactivatedBy = @DeactivatedBy,
                                    IsActive = @IsActive
                                WHERE SyncId = @SyncId;";

                            AddParam(updateCmd, "@SyncId", snap.SyncId);
                            AddParam(updateCmd, "@Name", snap.Name);
                            AddParam(updateCmd, "@DailyDate", snap.DailyDate);
                            AddParam(updateCmd, "@Closed", snap.Closed);
                            AddParam(updateCmd, "@CreatedAt", snap.CreatedAt);
                            AddParam(updateCmd, "@CreatedBy", snap.CreatedBy);
                            AddParam(updateCmd, "@UpdatedAt", snap.UpdatedAt);
                            AddParam(updateCmd, "@UpdatedBy", snap.UpdatedBy);
                            AddParam(updateCmd, "@DeactivatedAt", snap.DeactivatedAt);
                            AddParam(updateCmd, "@DeactivatedBy", snap.DeactivatedBy);
                            AddParam(updateCmd, "@IsActive", snap.IsActive);

                            await updateCmd.ExecuteNonQueryAsync(cancellationToken);
                            result.Operations.Add(new PullOperationResult
                            {
                                EntitySyncId = snap.SyncId,
                                OperationType = "UPDATE",
                                Status = "SUCCESS",
                                TerminalServerVersion = cmd.TerminalServerVersion
                            });
                        }
                        else
                        {
                            // INSERT new row letting Local SQL identity generate integer Id
                            await using var insertCmd = conn.CreateCommand();
                            insertCmd.Transaction = tx;
                            insertCmd.CommandText = @"
                                INSERT INTO [dbo].[Daily]
                                (SyncId, Name, DailyDate, Closed, CreatedAt, CreatedBy, UpdatedAt, UpdatedBy, DeactivatedAt, DeactivatedBy, IsActive)
                                VALUES
                                (@SyncId, @Name, @DailyDate, @Closed, @CreatedAt, @CreatedBy, @UpdatedAt, @UpdatedBy, @DeactivatedAt, @DeactivatedBy, @IsActive);";

                            AddParam(insertCmd, "@SyncId", snap.SyncId);
                            AddParam(insertCmd, "@Name", snap.Name);
                            AddParam(insertCmd, "@DailyDate", snap.DailyDate);
                            AddParam(insertCmd, "@Closed", snap.Closed);
                            AddParam(insertCmd, "@CreatedAt", snap.CreatedAt);
                            AddParam(insertCmd, "@CreatedBy", snap.CreatedBy);
                            AddParam(insertCmd, "@UpdatedAt", snap.UpdatedAt);
                            AddParam(insertCmd, "@UpdatedBy", snap.UpdatedBy);
                            AddParam(insertCmd, "@DeactivatedAt", snap.DeactivatedAt);
                            AddParam(insertCmd, "@DeactivatedBy", snap.DeactivatedBy);
                            AddParam(insertCmd, "@IsActive", snap.IsActive);

                            await insertCmd.ExecuteNonQueryAsync(cancellationToken);
                            result.Operations.Add(new PullOperationResult
                            {
                                EntitySyncId = snap.SyncId,
                                OperationType = "INSERT",
                                Status = "SUCCESS",
                                TerminalServerVersion = cmd.TerminalServerVersion
                            });
                        }

                        result.TotalProcessed++;
                        result.Succeeded++;
                    }
                }
            }

                // Step 4: Atomic Checkpoint Update with Lease Fencing
                await using (var checkpointCmd = conn.CreateCommand())
                {
                    checkpointCmd.Transaction = tx;
                    checkpointCmd.CommandText = @"
                        UPDATE [sync].[LocalState]
                        SET LastServerVersion = @HighWatermark,
                            LastSuccessfulPullUtc = SYSUTCDATETIME(),
                            LastSyncAttemptUtc = SYSUTCDATETIME(),
                            LastSyncError = NULL
                        WHERE DatabaseId = @DatabaseId
                          AND ActiveLeaseToken = @LeaseToken
                          AND LeaseExpiresAtUtc >= SYSUTCDATETIME();";

                    AddParam(checkpointCmd, "@HighWatermark", batch.HighWatermark);
                    AddParam(checkpointCmd, "@DatabaseId", normDbId);
                    AddParam(checkpointCmd, "@LeaseToken", leaseToken);

                    var rows = await checkpointCmd.ExecuteNonQueryAsync(cancellationToken);
                    if (rows == 0)
                    {
                        throw new SyncLeaseExpiredException(
                            $"Failed to update LocalState checkpoint. Lease expired or stolen for DatabaseId '{normDbId}'.");
                    }
                }

                // Commit single local atomic transaction
                await tx.CommitAsync(cancellationToken);

                _logger.LogInformation("Successfully applied pull batch locally for DatabaseId {DatabaseId}: Checkpoint advanced {LowWatermark} -> {HighWatermark}.",
                    normDbId, batch.LowWatermark, batch.HighWatermark);

                return result;
            }
            catch
            {
                try
                {
                    await tx.RollbackAsync(CancellationToken.None);
                }
                catch
                {
                    // Ignore rollback errors on faulted transaction
                }
                throw;
            }
        }

        private static void AddParam(DbCommand cmd, string name, object? value)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            if (value is DateTime)
            {
                p.DbType = System.Data.DbType.DateTime2;
            }
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }
    }
}
