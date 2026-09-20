#nullable enable
using System;
using System.Data;
using System.Data.Common;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Core.Exceptions;
using Core.Models.Sync;
using Microsoft.Extensions.Logging;

namespace Auth.Infrastructure.Sync.Push
{
    public class AzurePushTransactionCoordinator : IAzurePushTransactionCoordinator
    {
        private readonly ILogger<AzurePushTransactionCoordinator> _logger;

        public AzurePushTransactionCoordinator(ILogger<AzurePushTransactionCoordinator> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<RemoteApplyResult> ApplyOperationAsync(
            DbConnection connection,
            string databaseId,
            LocalOutbox outboxItem,
            long expectedServerVersion,
            string requestHash,
            CancellationToken cancellationToken)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (outboxItem == null) throw new ArgumentNullException(nameof(outboxItem));
            if (string.IsNullOrWhiteSpace(databaseId)) throw new ArgumentException("DatabaseId is required.", nameof(databaseId));
            if (string.IsNullOrWhiteSpace(requestHash)) throw new ArgumentException("RequestHash is required.", nameof(requestHash));

            if (connection.State != ConnectionState.Open)
            {
                await connection.OpenAsync(cancellationToken);
            }

            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            try
            {
                // =========================================================================
                // STEP 1: Check Idempotency Ledger (ProcessedOperations) FIRST
                // =========================================================================
                await using (var checkCmd = connection.CreateCommand())
                {
                    checkCmd.Transaction = transaction;
                    checkCmd.CommandText = @"
                        SELECT RequestHash, ResultStatus, ResponseJson
                        FROM [sync].[ProcessedOperations]
                        WHERE DatabaseId = @DatabaseId AND ClientOperationId = @ClientOperationId;";

                    AddParam(checkCmd, "@DatabaseId", databaseId);
                    AddParam(checkCmd, "@ClientOperationId", outboxItem.ClientOperationId);

                    string? storedHash = null;
                    string? resultStatus = null;
                    string? existingResponseJson = null;
                    bool recordFound = false;

                    await using (var reader = await checkCmd.ExecuteReaderAsync(cancellationToken))
                    {
                        if (await reader.ReadAsync(cancellationToken))
                        {
                            storedHash = reader.GetString(0);
                            resultStatus = reader.GetString(1);
                            existingResponseJson = reader.IsDBNull(2) ? null : reader.GetString(2);
                            recordFound = true;
                        }
                    }

                    if (recordFound)
                    {
                        // Check for Operation ID Reuse attack / corruption
                        if (!string.Equals(storedHash, requestHash, StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogWarning(
                                "Operation ID reuse detected: DatabaseId {DatabaseId}, ClientOperationId {ClientOperationId}. Existing hash mismatch.",
                                databaseId, outboxItem.ClientOperationId);
                            throw new SyncOperationIdReuseException(
                                $"Security violation: ClientOperationId '{outboxItem.ClientOperationId}' was already processed with a different request hash.");
                        }

                        // Idempotent replay of previously successful operation
                        if (string.Equals(resultStatus, "SUCCESS", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(existingResponseJson))
                        {
                            _logger.LogInformation(
                                "Idempotent replay detected for ClientOperationId {ClientOperationId}. Returning stored server response.",
                                outboxItem.ClientOperationId);

                            long storedVersion = 0;
                            try
                            {
                                using var respDoc = JsonDocument.Parse(existingResponseJson);
                                if (respDoc.RootElement.TryGetProperty("serverVersion", out var svProp) && svProp.TryGetInt64(out var sv))
                                {
                                    storedVersion = sv;
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to parse stored responseJson for ClientOperationId {ClientOperationId}.", outboxItem.ClientOperationId);
                            }

                            await transaction.CommitAsync(cancellationToken);

                            return new RemoteApplyResult
                            {
                                IsReplay = true,
                                ServerVersion = storedVersion,
                                ResponseJson = existingResponseJson
                            };
                        }
                    }
                }

                // =========================================================================
                // STEP 2: Lock and Verify ServerState (ExpectedServerVersion Check)
                // =========================================================================
                long currentServerVersion = 0;
                await using (var versionCmd = connection.CreateCommand())
                {
                    versionCmd.Transaction = transaction;
                    versionCmd.CommandText = @"
                        SELECT CurrentVersion
                        FROM [sync].[ServerState] WITH (UPDLOCK, HOLDLOCK)
                        WHERE DatabaseId = @DatabaseId;";

                    AddParam(versionCmd, "@DatabaseId", databaseId);

                    var scalar = await versionCmd.ExecuteScalarAsync(cancellationToken);
                    if (scalar == null || scalar == DBNull.Value)
                    {
                        throw new InvalidOperationException($"ServerState record not found for DatabaseId '{databaseId}'.");
                    }
                    currentServerVersion = Convert.ToInt64(scalar);
                }

                if (currentServerVersion != expectedServerVersion)
                {
                    _logger.LogWarning(
                        "Version conflict on DatabaseId {DatabaseId}: Expected {ExpectedVersion}, Actual {CurrentVersion}.",
                        databaseId, expectedServerVersion, currentServerVersion);

                    throw new SyncVersionConflictException(
                        expectedServerVersion,
                        currentServerVersion,
                        $"Version conflict detected for DatabaseId '{databaseId}': Expected {expectedServerVersion}, but remote server is at {currentServerVersion}.");
                }

                // =========================================================================
                // STEP 3: Parse Envelope V1 & Apply Daily Mutation by SyncId (Whitelist Only)
                // =========================================================================
                var parsedPayload = ParseAndValidatePayload(outboxItem);
                Guid originDeviceId = parsedPayload.DeviceId;

                switch (parsedPayload.OperationType.ToUpperInvariant())
                {
                    case "INSERT":
                        await ApplyInsertAsync(connection, transaction, parsedPayload, cancellationToken);
                        break;
                    case "UPDATE":
                        await ApplyUpdateAsync(connection, transaction, parsedPayload, cancellationToken);
                        break;
                    case "SOFT_DELETE":
                        await ApplySoftDeleteAsync(connection, transaction, parsedPayload, cancellationToken);
                        break;
                    default:
                        throw new SyncPayloadValidationException($"Unsupported operation type '{parsedPayload.OperationType}'.");
                }

                // =========================================================================
                // STEP 4: Increment ServerState.CurrentVersion
                // =========================================================================
                long newServerVersion = currentServerVersion + 1;
                var nowUtc = DateTime.UtcNow;

                await using (var updateVersionCmd = connection.CreateCommand())
                {
                    updateVersionCmd.Transaction = transaction;
                    updateVersionCmd.CommandText = @"
                        UPDATE [sync].[ServerState]
                        SET CurrentVersion = @NewVersion, LastUpdatedUtc = @NowUtc
                        WHERE DatabaseId = @DatabaseId;";

                    AddParam(updateVersionCmd, "@NewVersion", newServerVersion);
                    AddParam(updateVersionCmd, "@NowUtc", nowUtc);
                    AddParam(updateVersionCmd, "@DatabaseId", databaseId);

                    await updateVersionCmd.ExecuteNonQueryAsync(cancellationToken);
                }

                // =========================================================================
                // STEP 5: Insert ServerChangeFeed Record
                // =========================================================================
                await using (var feedCmd = connection.CreateCommand())
                {
                    feedCmd.Transaction = transaction;
                    feedCmd.CommandText = @"
                        INSERT INTO [sync].[ServerChangeFeed]
                        ([ServerVersion], [DatabaseId], [EntityType], [EntitySyncId], [OperationType], [OriginDeviceId], [TimestampUtc])
                        VALUES
                        (@ServerVersion, @DatabaseId, 'Daily', @EntitySyncId, @OperationType, @OriginDeviceId, @TimestampUtc);";

                    AddParam(feedCmd, "@ServerVersion", newServerVersion);
                    AddParam(feedCmd, "@DatabaseId", databaseId);
                    AddParam(feedCmd, "@EntitySyncId", outboxItem.EntitySyncId);
                    AddParam(feedCmd, "@OperationType", parsedPayload.OperationType);
                    AddParam(feedCmd, "@OriginDeviceId", originDeviceId);
                    AddParam(feedCmd, "@TimestampUtc", nowUtc);

                    await feedCmd.ExecuteNonQueryAsync(cancellationToken);
                }

                // =========================================================================
                // STEP 6: Insert ProcessedOperations Record
                // =========================================================================
                var responseObj = new
                {
                    clientOperationId = outboxItem.ClientOperationId,
                    entitySyncId = outboxItem.EntitySyncId,
                    serverVersion = newServerVersion,
                    result = "SUCCESS",
                    timestampUtc = nowUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ")
                };
                var responseJson = JsonSerializer.Serialize(responseObj);

                await using (var procCmd = connection.CreateCommand())
                {
                    procCmd.Transaction = transaction;
                    procCmd.CommandText = @"
                        INSERT INTO [sync].[ProcessedOperations]
                        ([DatabaseId], [ClientOperationId], [DeviceId], [CommandName], [RequestHash], [EntityType], [EntitySyncId], [ProcessedAtUtc], [ResultStatus], [ResponseJson])
                        VALUES
                        (@DatabaseId, @ClientOperationId, @DeviceId, @CommandName, @RequestHash, 'Daily', @EntitySyncId, @ProcessedAtUtc, 'SUCCESS', @ResponseJson);";

                    AddParam(procCmd, "@DatabaseId", databaseId);
                    AddParam(procCmd, "@ClientOperationId", outboxItem.ClientOperationId);
                    AddParam(procCmd, "@DeviceId", originDeviceId);
                    AddParam(procCmd, "@CommandName", outboxItem.CommandName);
                    AddParam(procCmd, "@RequestHash", requestHash);
                    AddParam(procCmd, "@EntitySyncId", outboxItem.EntitySyncId);
                    AddParam(procCmd, "@ProcessedAtUtc", nowUtc);
                    AddParam(procCmd, "@ResponseJson", responseJson);

                    await procCmd.ExecuteNonQueryAsync(cancellationToken);
                }

                // =========================================================================
                // STEP 7: Commit Transaction
                // =========================================================================
                await transaction.CommitAsync(cancellationToken);

                return new RemoteApplyResult
                {
                    IsReplay = false,
                    ServerVersion = newServerVersion,
                    ResponseJson = responseJson
                };
            }
            catch
            {
                try
                {
                    await transaction.RollbackAsync(cancellationToken);
                }
                catch
                {
                    // Suppress rollback errors if connection was lost
                }
                throw;
            }
        }

        private static async Task ApplyInsertAsync(
            DbConnection connection,
            DbTransaction transaction,
            ParsedDailyPayload payload,
            CancellationToken ct)
        {
            await using (var existsCmd = connection.CreateCommand())
            {
                existsCmd.Transaction = transaction;
                existsCmd.CommandText = "SELECT COUNT(*) FROM [dbo].[Daily] WHERE [SyncId] = @SyncId;";
                AddParam(existsCmd, "@SyncId", payload.SyncId);

                var count = Convert.ToInt32(await existsCmd.ExecuteScalarAsync(ct));
                if (count > 0)
                {
                    throw new SyncEntityAlreadyExistsException(
                        $"Entity integrity violation: Daily with SyncId '{payload.SyncId}' already exists in remote database.");
                }
            }

            await using (var insertCmd = connection.CreateCommand())
            {
                insertCmd.Transaction = transaction;
                insertCmd.CommandText = @"
                    INSERT INTO [dbo].[Daily]
                    ([Name], [DailyDate], [Closed], [CreatedBy], [CreatedAt], [UpdatedBy], [UpdatedAt], [DeactivatedBy], [DeactivatedAt], [IsActive], [SyncId])
                    VALUES
                    (@Name, @DailyDate, @Closed, @CreatedBy, @CreatedAt, @UpdatedBy, @UpdatedAt, @DeactivatedBy, @DeactivatedAt, @IsActive, @SyncId);";

                AddParam(insertCmd, "@Name", payload.Name);
                AddParam(insertCmd, "@DailyDate", payload.DailyDate);
                AddParam(insertCmd, "@Closed", payload.Closed);
                AddParam(insertCmd, "@CreatedBy", (object?)payload.CreatedBy ?? DBNull.Value);
                AddParam(insertCmd, "@CreatedAt", payload.CreatedAt);
                AddParam(insertCmd, "@UpdatedBy", (object?)payload.UpdatedBy ?? DBNull.Value);
                AddParam(insertCmd, "@UpdatedAt", (object?)payload.UpdatedAt ?? DBNull.Value);
                AddParam(insertCmd, "@DeactivatedBy", (object?)payload.DeactivatedBy ?? DBNull.Value);
                AddParam(insertCmd, "@DeactivatedAt", (object?)payload.DeactivatedAt ?? DBNull.Value);
                AddParam(insertCmd, "@IsActive", payload.IsActive);
                AddParam(insertCmd, "@SyncId", payload.SyncId);

                await insertCmd.ExecuteNonQueryAsync(ct);
            }
        }

        private static async Task ApplyUpdateAsync(
            DbConnection connection,
            DbTransaction transaction,
            ParsedDailyPayload payload,
            CancellationToken ct)
        {
            await using (var checkCmd = connection.CreateCommand())
            {
                checkCmd.Transaction = transaction;
                checkCmd.CommandText = "SELECT IsActive FROM [dbo].[Daily] WHERE [SyncId] = @SyncId;";
                AddParam(checkCmd, "@SyncId", payload.SyncId);

                var existingActive = await checkCmd.ExecuteScalarAsync(ct);
                if (existingActive == null || existingActive == DBNull.Value)
                {
                    throw new SyncEntityNotFoundException(
                        $"Entity not found: Daily with SyncId '{payload.SyncId}' does not exist for UPDATE operation.");
                }

                var isCurrentActive = Convert.ToBoolean(existingActive);
                if (isCurrentActive && !payload.IsActive)
                {
                    throw new SyncPayloadValidationException(
                        "Invalid operation: Deactivating an entity must be performed via SOFT_DELETE operation.");
                }
            }

            await using (var updateCmd = connection.CreateCommand())
            {
                updateCmd.Transaction = transaction;
                updateCmd.CommandText = @"
                    UPDATE [dbo].[Daily]
                    SET [Name] = @Name,
                        [DailyDate] = @DailyDate,
                        [Closed] = @Closed,
                        [UpdatedBy] = @UpdatedBy,
                        [UpdatedAt] = @UpdatedAt
                    WHERE [SyncId] = @SyncId;";

                AddParam(updateCmd, "@Name", payload.Name);
                AddParam(updateCmd, "@DailyDate", payload.DailyDate);
                AddParam(updateCmd, "@Closed", payload.Closed);
                AddParam(updateCmd, "@UpdatedBy", (object?)payload.UpdatedBy ?? DBNull.Value);
                AddParam(updateCmd, "@UpdatedAt", (object?)payload.UpdatedAt ?? DBNull.Value);
                AddParam(updateCmd, "@SyncId", payload.SyncId);

                await updateCmd.ExecuteNonQueryAsync(ct);
            }
        }

        private static async Task ApplySoftDeleteAsync(
            DbConnection connection,
            DbTransaction transaction,
            ParsedDailyPayload payload,
            CancellationToken ct)
        {
            await using (var checkCmd = connection.CreateCommand())
            {
                checkCmd.Transaction = transaction;
                checkCmd.CommandText = "SELECT COUNT(*) FROM [dbo].[Daily] WHERE [SyncId] = @SyncId;";
                AddParam(checkCmd, "@SyncId", payload.SyncId);

                var count = Convert.ToInt32(await checkCmd.ExecuteScalarAsync(ct));
                if (count == 0)
                {
                    throw new SyncEntityNotFoundException(
                        $"Entity not found: Daily with SyncId '{payload.SyncId}' does not exist for SOFT_DELETE operation.");
                }
            }

            await using (var deleteCmd = connection.CreateCommand())
            {
                deleteCmd.Transaction = transaction;
                deleteCmd.CommandText = @"
                    UPDATE [dbo].[Daily]
                    SET [IsActive] = 0,
                        [DeactivatedBy] = @DeactivatedBy,
                        [DeactivatedAt] = @DeactivatedAt,
                        [UpdatedBy] = @UpdatedBy,
                        [UpdatedAt] = @UpdatedAt
                    WHERE [SyncId] = @SyncId;";

                AddParam(deleteCmd, "@DeactivatedBy", (object?)payload.DeactivatedBy ?? DBNull.Value);
                AddParam(deleteCmd, "@DeactivatedAt", (object?)payload.DeactivatedAt ?? DateTime.UtcNow);
                AddParam(deleteCmd, "@UpdatedBy", (object?)payload.UpdatedBy ?? DBNull.Value);
                AddParam(deleteCmd, "@UpdatedAt", (object?)payload.UpdatedAt ?? DateTime.UtcNow);
                AddParam(deleteCmd, "@SyncId", payload.SyncId);

                await deleteCmd.ExecuteNonQueryAsync(ct);
            }
        }

        public static ParsedDailyPayload ParseAndValidatePayload(LocalOutbox outboxItem)
        {
            try
            {
                using var doc = JsonDocument.Parse(outboxItem.PayloadJson);
                var root = doc.RootElement;

                var schemaVersion = root.GetProperty("schemaVersion").GetInt32();
                if (schemaVersion != 1)
                {
                    throw new SyncPayloadValidationException($"Unsupported schemaVersion {schemaVersion}. Expected 1.");
                }

                var entityType = root.GetProperty("entityType").GetString();
                if (!string.Equals(entityType, "Daily", StringComparison.OrdinalIgnoreCase))
                {
                    throw new SyncPayloadValidationException($"Unsupported entityType '{entityType}'. Expected 'Daily'.");
                }

                var entitySyncIdStr = root.GetProperty("entitySyncId").GetString();
                if (!Guid.TryParse(entitySyncIdStr, out var entitySyncId) || entitySyncId == Guid.Empty || entitySyncId != outboxItem.EntitySyncId)
                {
                    throw new SyncPayloadValidationException("Payload entitySyncId is missing, empty, or does not match outbox EntitySyncId.");
                }

                var operationType = root.GetProperty("operationType").GetString() ?? string.Empty;
                var deviceIdStr = root.GetProperty("deviceId").GetString();
                if (!Guid.TryParse(deviceIdStr, out var deviceId) || deviceId == Guid.Empty)
                {
                    throw new SyncPayloadValidationException("Payload deviceId is missing or invalid GUID.");
                }

                var entityData = root.GetProperty("entityData");
                string name = string.Empty;
                DateTime dailyDate = DateTime.UtcNow.Date;

                if (!string.Equals(operationType, "SOFT_DELETE", StringComparison.OrdinalIgnoreCase))
                {
                    name = entityData.TryGetProperty("Name", out var nProp) ? nProp.GetString() ?? string.Empty : string.Empty;
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        throw new SyncPayloadValidationException("Daily Name is required.");
                    }

                    if (entityData.TryGetProperty("DailyDate", out var ddProp))
                    {
                        dailyDate = ddProp.GetDateTime();
                    }
                }

                var closed = entityData.TryGetProperty("Closed", out var cProp) && cProp.GetBoolean();
                var isActive = !string.Equals(operationType, "SOFT_DELETE", StringComparison.OrdinalIgnoreCase) &&
                               (!entityData.TryGetProperty("IsActive", out var iaProp) || iaProp.GetBoolean());

                DateTime createdAt = DateTime.UtcNow;
                if (entityData.TryGetProperty("CreatedAt", out var caProp) && caProp.ValueKind == JsonValueKind.String)
                {
                    _ = DateTime.TryParse(caProp.GetString(), out createdAt);
                }

                string? createdBy = entityData.TryGetProperty("CreatedBy", out var cbProp) && cbProp.ValueKind == JsonValueKind.String ? cbProp.GetString() : null;
                string? updatedBy = entityData.TryGetProperty("UpdatedBy", out var ubProp) && ubProp.ValueKind == JsonValueKind.String ? ubProp.GetString() : null;

                DateTime? updatedAt = null;
                if (entityData.TryGetProperty("UpdatedAt", out var uaProp) && uaProp.ValueKind == JsonValueKind.String && DateTime.TryParse(uaProp.GetString(), out var parsedUa))
                {
                    updatedAt = parsedUa;
                }

                string? deactivatedBy = entityData.TryGetProperty("DeactivatedBy", out var dbProp) && dbProp.ValueKind == JsonValueKind.String ? dbProp.GetString() : null;
                DateTime? deactivatedAt = null;
                if (entityData.TryGetProperty("DeactivatedAt", out var daProp) && daProp.ValueKind == JsonValueKind.String && DateTime.TryParse(daProp.GetString(), out var parsedDa))
                {
                    deactivatedAt = parsedDa;
                }

                return new ParsedDailyPayload
                {
                    SyncId = entitySyncId,
                    DeviceId = deviceId,
                    OperationType = operationType,
                    Name = name,
                    DailyDate = dailyDate,
                    Closed = closed,
                    IsActive = isActive,
                    CreatedAt = createdAt,
                    CreatedBy = createdBy,
                    UpdatedAt = updatedAt,
                    UpdatedBy = updatedBy,
                    DeactivatedAt = deactivatedAt,
                    DeactivatedBy = deactivatedBy
                };
            }
            catch (Exception ex) when (ex is not SyncPayloadValidationException)
            {
                throw new SyncPayloadValidationException($"Payload validation error: {ex.Message}", ex);
            }
        }

        private static void AddParam(DbCommand cmd, string name, object? value)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }

        public sealed class ParsedDailyPayload
        {
            public required Guid SyncId { get; init; }
            public required Guid DeviceId { get; init; }
            public required string OperationType { get; init; }
            public required string Name { get; init; }
            public required DateTime DailyDate { get; init; }
            public required bool Closed { get; init; }
            public required bool IsActive { get; init; }
            public required DateTime CreatedAt { get; init; }
            public string? CreatedBy { get; init; }
            public DateTime? UpdatedAt { get; init; }
            public string? UpdatedBy { get; init; }
            public DateTime? DeactivatedAt { get; init; }
            public string? DeactivatedBy { get; init; }
        }
    }
}
