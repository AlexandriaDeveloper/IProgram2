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
            Guid expectedDeviceId,
            CancellationToken cancellationToken)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (outboxItem == null) throw new ArgumentNullException(nameof(outboxItem));
            if (string.IsNullOrWhiteSpace(databaseId)) throw new ArgumentException("DatabaseId is required.", nameof(databaseId));
            if (string.IsNullOrWhiteSpace(requestHash)) throw new ArgumentException("RequestHash is required.", nameof(requestHash));
            if (expectedDeviceId == Guid.Empty) throw new ArgumentException("ExpectedDeviceId is required.", nameof(expectedDeviceId));

            // =========================================================================
            // STEP 0: Validate Metadata Consistency & Parse Envelope V1 Upfront
            // =========================================================================
            if (!string.Equals(outboxItem.DatabaseId, databaseId, StringComparison.OrdinalIgnoreCase))
            {
                throw new SyncMetadataMismatchException(
                    $"Metadata mismatch: outbox DatabaseId '{outboxItem.DatabaseId}' does not match target database '{databaseId}'.");
            }

            if (!string.Equals(outboxItem.AggregateType, "Daily", StringComparison.OrdinalIgnoreCase))
            {
                throw new SyncMetadataMismatchException(
                    $"Metadata mismatch: outbox AggregateType '{outboxItem.AggregateType}' is not 'Daily'.");
            }

            var parsedPayload = ParseAndValidatePayload(outboxItem);

            if (parsedPayload.DeviceId != expectedDeviceId)
            {
                throw new SyncMetadataMismatchException(
                    $"Metadata mismatch: payload deviceId '{parsedPayload.DeviceId}' does not match expected LocalState DeviceId '{expectedDeviceId}'.");
            }

            var expectedCommandName = parsedPayload.OperationType.ToUpperInvariant() switch
            {
                "INSERT" => "Daily.Insert",
                "UPDATE" => "Daily.Update",
                "SOFT_DELETE" => "Daily.SoftDelete",
                _ => throw new SyncPayloadValidationException($"Unsupported operation type '{parsedPayload.OperationType}'.")
            };

            if (!string.Equals(outboxItem.CommandName, expectedCommandName, StringComparison.OrdinalIgnoreCase))
            {
                throw new SyncMetadataMismatchException(
                    $"Metadata mismatch: outbox CommandName '{outboxItem.CommandName}' does not match payload operation '{parsedPayload.OperationType}'.");
            }

            if (outboxItem.EntitySyncId != parsedPayload.SyncId || parsedPayload.SyncId == Guid.Empty)
            {
                throw new SyncMetadataMismatchException(
                    $"Metadata mismatch: outbox EntitySyncId '{outboxItem.EntitySyncId}' does not match payload SyncId '{parsedPayload.SyncId}'.");
            }

            Guid originDeviceId = parsedPayload.DeviceId;

            if (connection.State != ConnectionState.Open)
            {
                await connection.OpenAsync(cancellationToken);
            }

            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            try
            {
                // =========================================================================
                // STEP 1: Check Idempotency Ledger (ProcessedOperations) FIRST
                // Using UPDLOCK, HOLDLOCK to protect against concurrent idempotency races
                // =========================================================================
                await using (var checkCmd = connection.CreateCommand())
                {
                    checkCmd.Transaction = transaction;
                    checkCmd.CommandText = @"
                        SELECT RequestHash, ResultStatus, ResponseJson
                        FROM [sync].[ProcessedOperations] WITH (UPDLOCK, HOLDLOCK)
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
                        if (string.Equals(resultStatus, "SUCCESS", StringComparison.OrdinalIgnoreCase))
                        {
                            if (string.IsNullOrWhiteSpace(existingResponseJson))
                            {
                                throw new SyncCorruptResponseJsonException(
                                    $"Corrupt ProcessedOperation: ResponseJson is empty for ClientOperationId '{outboxItem.ClientOperationId}'.");
                            }

                            _logger.LogInformation(
                                "Idempotent replay detected for ClientOperationId {ClientOperationId}. Returning stored server response.",
                                outboxItem.ClientOperationId);

                            long storedVersion = 0;
                            try
                            {
                                using var respDoc = JsonDocument.Parse(existingResponseJson);
                                var rootResp = respDoc.RootElement;

                                if (!rootResp.TryGetProperty("clientOperationId", out var opProp) ||
                                    !Guid.TryParse(opProp.GetString(), out var storedOpId) ||
                                    storedOpId != outboxItem.ClientOperationId)
                                {
                                    throw new SyncCorruptResponseJsonException(
                                        $"Stored ResponseJson for ClientOperationId '{outboxItem.ClientOperationId}' has mismatched or missing 'clientOperationId'.");
                                }

                                if (!rootResp.TryGetProperty("entitySyncId", out var esProp) ||
                                    !Guid.TryParse(esProp.GetString(), out var storedSyncId) ||
                                    storedSyncId != outboxItem.EntitySyncId)
                                {
                                    throw new SyncCorruptResponseJsonException(
                                        $"Stored ResponseJson for ClientOperationId '{outboxItem.ClientOperationId}' has mismatched or missing 'entitySyncId'.");
                                }

                                if (!rootResp.TryGetProperty("result", out var resProp) ||
                                    !string.Equals(resProp.GetString(), "SUCCESS", StringComparison.OrdinalIgnoreCase))
                                {
                                    throw new SyncCorruptResponseJsonException(
                                        $"Stored ResponseJson for ClientOperationId '{outboxItem.ClientOperationId}' has invalid result status.");
                                }

                                if (!rootResp.TryGetProperty("serverVersion", out var svProp) ||
                                    !svProp.TryGetInt64(out var sv) ||
                                    sv <= 0)
                                {
                                    throw new SyncCorruptResponseJsonException(
                                        $"Stored ResponseJson for ClientOperationId '{outboxItem.ClientOperationId}' is missing valid positive 'serverVersion'.");
                                }
                                storedVersion = sv;
                            }
                            catch (Exception ex) when (ex is not SyncCorruptResponseJsonException)
                            {
                                _logger.LogError("Corrupt responseJson detected: DatabaseId {DatabaseId}, ClientOperationId {ClientOperationId}, ErrorType {ErrorType}.",
                                    databaseId, outboxItem.ClientOperationId, ex.GetType().Name);
                                throw new SyncCorruptResponseJsonException(
                                    $"Stored ResponseJson for ClientOperationId '{outboxItem.ClientOperationId}' is corrupt.", ex);
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
                // STEP 3: Execute Business Mutation (INSERT / UPDATE / SOFT_DELETE)
                // =========================================================================
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
                AddParam(deleteCmd, "@DeactivatedAt", (object?)payload.DeactivatedAt ?? DBNull.Value);
                AddParam(deleteCmd, "@UpdatedBy", (object?)payload.UpdatedBy ?? DBNull.Value);
                AddParam(deleteCmd, "@UpdatedAt", (object?)payload.UpdatedAt ?? DBNull.Value);
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

                var databaseIdStr = root.TryGetProperty("databaseId", out var dbIdProp) ? dbIdProp.GetString() : null;
                if (string.IsNullOrWhiteSpace(databaseIdStr) || !string.Equals(databaseIdStr, outboxItem.DatabaseId, StringComparison.OrdinalIgnoreCase))
                {
                    throw new SyncMetadataMismatchException(
                        $"Metadata mismatch: payload databaseId '{databaseIdStr}' does not match outbox DatabaseId '{outboxItem.DatabaseId}'.");
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

                if (entityData.TryGetProperty("SyncId", out var edSyncIdProp) || entityData.TryGetProperty("syncId", out edSyncIdProp))
                {
                    if (edSyncIdProp.ValueKind != JsonValueKind.String ||
                        !Guid.TryParse(edSyncIdProp.GetString(), out var edSyncId) ||
                        edSyncId != entitySyncId ||
                        edSyncId != outboxItem.EntitySyncId)
                    {
                        throw new SyncMetadataMismatchException("Metadata mismatch: entityData SyncId does not match envelope entitySyncId or outbox EntitySyncId.");
                    }
                }

                string name = string.Empty;
                DateTime? dailyDate;

                if (!string.Equals(operationType, "SOFT_DELETE", StringComparison.OrdinalIgnoreCase))
                {
                    name = entityData.TryGetProperty("Name", out var nProp) ? nProp.GetString() ?? string.Empty : string.Empty;
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        throw new SyncPayloadValidationException("Daily Name is required and cannot be empty.");
                    }

                    dailyDate = ParseRequiredTimestamp(entityData, "DailyDate");
                }
                else
                {
                    dailyDate = ParseOptionalTimestamp(entityData, "DailyDate");
                }

                bool closed = false;
                if (!string.Equals(operationType, "SOFT_DELETE", StringComparison.OrdinalIgnoreCase))
                {
                    if (!entityData.TryGetProperty("Closed", out var cProp) || (cProp.ValueKind != JsonValueKind.True && cProp.ValueKind != JsonValueKind.False))
                    {
                        throw new SyncPayloadValidationException("Boolean Closed property is required in entityData.");
                    }
                    closed = cProp.GetBoolean();
                }

                bool isActive;
                if (string.Equals(operationType, "INSERT", StringComparison.OrdinalIgnoreCase))
                {
                    if (!entityData.TryGetProperty("IsActive", out var iaProp) || iaProp.ValueKind != JsonValueKind.True)
                    {
                        throw new SyncPayloadValidationException("INSERT operation requires IsActive to be explicitly true. Deactivation must be performed via SOFT_DELETE.");
                    }
                    isActive = true;
                }
                else if (string.Equals(operationType, "SOFT_DELETE", StringComparison.OrdinalIgnoreCase))
                {
                    if (!entityData.TryGetProperty("IsActive", out var iaProp) || iaProp.ValueKind != JsonValueKind.False)
                    {
                        throw new SyncPayloadValidationException("SOFT_DELETE operation requires entityData.IsActive to be explicitly false.");
                    }
                    isActive = false;
                }
                else
                {
                    if (!entityData.TryGetProperty("IsActive", out var iaProp) || (iaProp.ValueKind != JsonValueKind.True && iaProp.ValueKind != JsonValueKind.False))
                    {
                        throw new SyncPayloadValidationException("Boolean IsActive property is required in entityData.");
                    }
                    isActive = iaProp.GetBoolean();
                }

                DateTime? createdAt;
                if (string.Equals(operationType, "INSERT", StringComparison.OrdinalIgnoreCase))
                {
                    createdAt = ParseRequiredTimestamp(entityData, "CreatedAt");
                }
                else
                {
                    createdAt = ParseOptionalTimestamp(entityData, "CreatedAt");
                }

                string? createdBy = entityData.TryGetProperty("CreatedBy", out var cbProp) && cbProp.ValueKind == JsonValueKind.String ? cbProp.GetString() : null;
                string? updatedBy = entityData.TryGetProperty("UpdatedBy", out var ubProp) && ubProp.ValueKind == JsonValueKind.String ? ubProp.GetString() : null;

                DateTime? updatedAt;
                if (string.Equals(operationType, "UPDATE", StringComparison.OrdinalIgnoreCase))
                {
                    updatedAt = ParseRequiredTimestamp(entityData, "UpdatedAt");
                }
                else
                {
                    updatedAt = ParseOptionalTimestamp(entityData, "UpdatedAt");
                }

                string? deactivatedBy = entityData.TryGetProperty("DeactivatedBy", out var dbProp) && dbProp.ValueKind == JsonValueKind.String ? dbProp.GetString() : null;

                DateTime? deactivatedAt;
                if (string.Equals(operationType, "SOFT_DELETE", StringComparison.OrdinalIgnoreCase))
                {
                    deactivatedAt = ParseRequiredTimestamp(entityData, "DeactivatedAt");
                }
                else
                {
                    deactivatedAt = ParseOptionalTimestamp(entityData, "DeactivatedAt");
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
            catch (Exception ex) when (ex is not SyncDomainException)
            {
                throw new SyncPayloadValidationException($"Payload validation error: {ex.Message}", ex);
            }
        }

        private static DateTime? ParseOptionalTimestamp(JsonElement element, string propName)
        {
            if (!element.TryGetProperty(propName, out var prop) || prop.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            if (prop.ValueKind != JsonValueKind.String)
            {
                throw new SyncPayloadValidationException($"Malformed {propName} timestamp in entityData.");
            }

            if (prop.TryGetDateTime(out var dt) || DateTime.TryParse(prop.GetString(), out dt))
            {
                return dt;
            }

            throw new SyncPayloadValidationException($"Malformed {propName} timestamp in entityData.");
        }

        private static DateTime ParseRequiredTimestamp(JsonElement element, string propName)
        {
            if (!element.TryGetProperty(propName, out var prop) || prop.ValueKind == JsonValueKind.Null)
            {
                throw new SyncPayloadValidationException($"Valid {propName} timestamp is required in entityData.");
            }

            if (prop.ValueKind != JsonValueKind.String)
            {
                throw new SyncPayloadValidationException($"Malformed {propName} timestamp in entityData.");
            }

            if (prop.TryGetDateTime(out var dt) || DateTime.TryParse(prop.GetString(), out dt))
            {
                return dt;
            }

            throw new SyncPayloadValidationException($"Malformed {propName} timestamp in entityData.");
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
            public DateTime? DailyDate { get; init; }
            public required bool Closed { get; init; }
            public required bool IsActive { get; init; }
            public DateTime? CreatedAt { get; init; }
            public string? CreatedBy { get; init; }
            public DateTime? UpdatedAt { get; init; }
            public string? UpdatedBy { get; init; }
            public DateTime? DeactivatedAt { get; init; }
            public string? DeactivatedBy { get; init; }
        }
    }
}
