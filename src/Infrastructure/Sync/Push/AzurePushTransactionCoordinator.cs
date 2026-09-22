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

            if (!string.Equals(outboxItem.AggregateType, "Daily", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(outboxItem.AggregateType, "Form", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(outboxItem.AggregateType, "FormDetails", StringComparison.OrdinalIgnoreCase))
            {
                throw new SyncMetadataMismatchException(
                    $"Metadata mismatch: outbox AggregateType '{outboxItem.AggregateType}' is not supported for push.");
            }

            ParsedDailyPayload? parsedDaily = null;
            ParsedFormPayload? parsedForm = null;
            ParsedFormDetailsPayload? parsedFormDetails = null;

            Guid originDeviceId;
            string operationType;
            string expectedCommandName;

            if (string.Equals(outboxItem.AggregateType, "Daily", StringComparison.OrdinalIgnoreCase))
            {
                parsedDaily = ParseAndValidatePayload(outboxItem);
                originDeviceId = parsedDaily.DeviceId;
                operationType = parsedDaily.OperationType;
                expectedCommandName = operationType.ToUpperInvariant() switch
                {
                    "INSERT" => "Daily.Insert",
                    "UPDATE" => "Daily.Update",
                    "SOFT_DELETE" => "Daily.SoftDelete",
                    _ => throw new SyncPayloadValidationException($"Unsupported operation type '{operationType}'.")
                };
            }
            else if (string.Equals(outboxItem.AggregateType, "Form", StringComparison.OrdinalIgnoreCase))
            {
                parsedForm = ParseAndValidateFormPayload(outboxItem);
                originDeviceId = parsedForm.DeviceId;
                operationType = parsedForm.OperationType;
                expectedCommandName = operationType.ToUpperInvariant() switch
                {
                    "INSERT" => "Form.Insert",
                    "UPDATE" => "Form.Update",
                    "SOFT_DELETE" => "Form.SoftDelete",
                    _ => throw new SyncPayloadValidationException($"Unsupported operation type '{operationType}'.")
                };
            }
            else if (string.Equals(outboxItem.AggregateType, "FormDetails", StringComparison.OrdinalIgnoreCase))
            {
                parsedFormDetails = ParseAndValidateFormDetailsPayload(outboxItem);
                originDeviceId = parsedFormDetails.DeviceId;
                operationType = parsedFormDetails.OperationType;
                expectedCommandName = operationType.ToUpperInvariant() switch
                {
                    "INSERT" => "FormDetails.Insert",
                    "UPDATE" => "FormDetails.Update",
                    "SOFT_DELETE" => "FormDetails.SoftDelete",
                    _ => throw new SyncPayloadValidationException($"Unsupported operation type '{operationType}'.")
                };
            }
            else
            {
                throw new SyncMetadataMismatchException($"Unsupported AggregateType '{outboxItem.AggregateType}'.");
            }

            if (originDeviceId != expectedDeviceId)
            {
                throw new SyncMetadataMismatchException(
                    $"Metadata mismatch: payload deviceId '{originDeviceId}' does not match expected LocalState DeviceId '{expectedDeviceId}'.");
            }

            if (!string.Equals(outboxItem.CommandName, expectedCommandName, StringComparison.OrdinalIgnoreCase))
            {
                throw new SyncMetadataMismatchException(
                    $"Metadata mismatch: outbox CommandName '{outboxItem.CommandName}' does not match payload operation '{operationType}'.");
            }

            var payloadSyncId = parsedDaily?.SyncId ?? parsedForm?.SyncId ?? parsedFormDetails!.SyncId;
            if (outboxItem.EntitySyncId != payloadSyncId || payloadSyncId == Guid.Empty)
            {
                throw new SyncMetadataMismatchException(
                    $"Metadata mismatch: outbox EntitySyncId '{outboxItem.EntitySyncId}' does not match payload SyncId '{payloadSyncId}'.");
            }

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
                    if (currentServerVersion > expectedServerVersion)
                    {
                        _logger.LogWarning(
                            "Both changed conflict risk on DatabaseId {DatabaseId}: Local expected {ExpectedVersion}, Remote server is newer at {CurrentVersion}.",
                            databaseId, expectedServerVersion, currentServerVersion);

                        throw new SyncConflictRiskException(
                            expectedServerVersion,
                            currentServerVersion,
                            1,
                            $"Conflict risk detected (BOTH_CHANGED / CONFLICT_RISK) for DatabaseId '{databaseId}': Local pending operations exist at version {expectedServerVersion}, but remote server is at {currentServerVersion}. Both states are preserved without automatic overwrite.");
                    }

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
                if (parsedDaily != null)
                {
                    switch (parsedDaily.OperationType.ToUpperInvariant())
                    {
                        case "INSERT":
                            await ApplyInsertAsync(connection, transaction, parsedDaily, cancellationToken);
                            break;
                        case "UPDATE":
                            await ApplyUpdateAsync(connection, transaction, parsedDaily, cancellationToken);
                            break;
                        case "SOFT_DELETE":
                            await ApplySoftDeleteAsync(connection, transaction, parsedDaily, cancellationToken);
                            break;
                        default:
                            throw new SyncPayloadValidationException($"Unsupported operation type '{parsedDaily.OperationType}'.");
                    }
                }
                else if (parsedForm != null)
                {
                    switch (parsedForm.OperationType.ToUpperInvariant())
                    {
                        case "INSERT":
                            await ApplyFormInsertAsync(connection, transaction, parsedForm, cancellationToken);
                            break;
                        case "UPDATE":
                            await ApplyFormUpdateAsync(connection, transaction, parsedForm, cancellationToken);
                            break;
                        case "SOFT_DELETE":
                            await ApplyFormSoftDeleteAsync(connection, transaction, parsedForm, cancellationToken);
                            break;
                        default:
                            throw new SyncPayloadValidationException($"Unsupported operation type '{parsedForm.OperationType}'.");
                    }
                }
                else if (parsedFormDetails != null)
                {
                    switch (parsedFormDetails.OperationType.ToUpperInvariant())
                    {
                        case "INSERT":
                            await ApplyFormDetailsInsertAsync(connection, transaction, parsedFormDetails, cancellationToken);
                            break;
                        case "UPDATE":
                            await ApplyFormDetailsUpdateAsync(connection, transaction, parsedFormDetails, cancellationToken);
                            break;
                        case "SOFT_DELETE":
                            await ApplyFormDetailsSoftDeleteAsync(connection, transaction, parsedFormDetails, cancellationToken);
                            break;
                        default:
                            throw new SyncPayloadValidationException($"Unsupported operation type '{parsedFormDetails.OperationType}'.");
                    }
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
                        (@ServerVersion, @DatabaseId, @EntityType, @EntitySyncId, @OperationType, @OriginDeviceId, @TimestampUtc);";

                    AddParam(feedCmd, "@ServerVersion", newServerVersion);
                    AddParam(feedCmd, "@DatabaseId", databaseId);
                    AddParam(feedCmd, "@EntityType", outboxItem.AggregateType);
                    AddParam(feedCmd, "@EntitySyncId", outboxItem.EntitySyncId);
                    AddParam(feedCmd, "@OperationType", operationType);
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
                        (@DatabaseId, @ClientOperationId, @DeviceId, @CommandName, @RequestHash, @EntityType, @EntitySyncId, @ProcessedAtUtc, 'SUCCESS', @ResponseJson);";

                    AddParam(procCmd, "@DatabaseId", databaseId);
                    AddParam(procCmd, "@ClientOperationId", outboxItem.ClientOperationId);
                    AddParam(procCmd, "@DeviceId", originDeviceId);
                    AddParam(procCmd, "@CommandName", outboxItem.CommandName);
                    AddParam(procCmd, "@RequestHash", requestHash);
                    AddParam(procCmd, "@EntityType", outboxItem.AggregateType);
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

        private static async Task ApplyFormInsertAsync(
            DbConnection connection,
            DbTransaction transaction,
            ParsedFormPayload payload,
            CancellationToken ct)
        {
            await using (var existsCmd = connection.CreateCommand())
            {
                existsCmd.Transaction = transaction;
                existsCmd.CommandText = "SELECT COUNT(*) FROM [dbo].[Form] WHERE [SyncId] = @SyncId;";
                AddParam(existsCmd, "@SyncId", payload.SyncId);

                var count = Convert.ToInt32(await existsCmd.ExecuteScalarAsync(ct));
                if (count > 0)
                {
                    throw new SyncEntityAlreadyExistsException(
                        $"Entity integrity violation: Form with SyncId '{payload.SyncId}' already exists in remote database.");
                }
            }

            int? dailyId = null;
            if (payload.DailySyncId.HasValue)
            {
                await using var resolveCmd = connection.CreateCommand();
                resolveCmd.Transaction = transaction;
                resolveCmd.CommandText = "SELECT Id FROM [dbo].[Daily] WHERE SyncId = @DailySyncId;";
                AddParam(resolveCmd, "@DailySyncId", payload.DailySyncId.Value);
                var obj = await resolveCmd.ExecuteScalarAsync(ct);
                if (obj == null || obj == DBNull.Value)
                {
                    throw new SyncEntityNotFoundException($"Referenced Daily with SyncId '{payload.DailySyncId.Value}' not found.");
                }
                dailyId = Convert.ToInt32(obj);
            }

            await using (var insertCmd = connection.CreateCommand())
            {
                insertCmd.Transaction = transaction;
                insertCmd.CommandText = @"
                    INSERT INTO [dbo].[Form]
                    ([Name], [DailyId], [Index], [Description], [CreatedBy], [CreatedAt], [UpdatedBy], [UpdatedAt], [DeactivatedBy], [DeactivatedAt], [IsActive], [SyncId])
                    VALUES
                    (@Name, @DailyId, @Index, @Description, @CreatedBy, @CreatedAt, @UpdatedBy, @UpdatedAt, @DeactivatedBy, @DeactivatedAt, @IsActive, @SyncId);";

                AddParam(insertCmd, "@Name", payload.Name);
                AddParam(insertCmd, "@DailyId", (object?)dailyId ?? DBNull.Value);
                AddParam(insertCmd, "@Index", (object?)payload.Index ?? DBNull.Value);
                AddParam(insertCmd, "@Description", (object?)payload.Description ?? DBNull.Value);
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

        private static async Task ApplyFormUpdateAsync(
            DbConnection connection,
            DbTransaction transaction,
            ParsedFormPayload payload,
            CancellationToken ct)
        {
            await using (var checkCmd = connection.CreateCommand())
            {
                checkCmd.Transaction = transaction;
                checkCmd.CommandText = "SELECT IsActive FROM [dbo].[Form] WHERE [SyncId] = @SyncId;";
                AddParam(checkCmd, "@SyncId", payload.SyncId);

                var existingActive = await checkCmd.ExecuteScalarAsync(ct);
                if (existingActive == null || existingActive == DBNull.Value)
                {
                    throw new SyncEntityNotFoundException(
                        $"Entity not found: Form with SyncId '{payload.SyncId}' does not exist for UPDATE operation.");
                }

                var isCurrentActive = Convert.ToBoolean(existingActive);
                if (isCurrentActive && !payload.IsActive)
                {
                    throw new SyncPayloadValidationException(
                        "Invalid operation: Deactivating an entity must be performed via SOFT_DELETE operation.");
                }
            }

            int? dailyId = null;
            if (payload.DailySyncId.HasValue)
            {
                await using var resolveCmd = connection.CreateCommand();
                resolveCmd.Transaction = transaction;
                resolveCmd.CommandText = "SELECT Id FROM [dbo].[Daily] WHERE SyncId = @DailySyncId;";
                AddParam(resolveCmd, "@DailySyncId", payload.DailySyncId.Value);
                var obj = await resolveCmd.ExecuteScalarAsync(ct);
                if (obj == null || obj == DBNull.Value)
                {
                    throw new SyncEntityNotFoundException($"Referenced Daily with SyncId '{payload.DailySyncId.Value}' not found.");
                }
                dailyId = Convert.ToInt32(obj);
            }

            await using (var updateCmd = connection.CreateCommand())
            {
                updateCmd.Transaction = transaction;
                updateCmd.CommandText = @"
                    UPDATE [dbo].[Form]
                    SET [Name] = @Name,
                        [DailyId] = @DailyId,
                        [Index] = @Index,
                        [Description] = @Description,
                        [UpdatedBy] = @UpdatedBy,
                        [UpdatedAt] = @UpdatedAt
                    WHERE [SyncId] = @SyncId;";

                AddParam(updateCmd, "@Name", payload.Name);
                AddParam(updateCmd, "@DailyId", (object?)dailyId ?? DBNull.Value);
                AddParam(updateCmd, "@Index", (object?)payload.Index ?? DBNull.Value);
                AddParam(updateCmd, "@Description", (object?)payload.Description ?? DBNull.Value);
                AddParam(updateCmd, "@UpdatedBy", (object?)payload.UpdatedBy ?? DBNull.Value);
                AddParam(updateCmd, "@UpdatedAt", (object?)payload.UpdatedAt ?? DBNull.Value);
                AddParam(updateCmd, "@SyncId", payload.SyncId);

                await updateCmd.ExecuteNonQueryAsync(ct);
            }
        }

        private static async Task ApplyFormSoftDeleteAsync(
            DbConnection connection,
            DbTransaction transaction,
            ParsedFormPayload payload,
            CancellationToken ct)
        {
            await using (var checkCmd = connection.CreateCommand())
            {
                checkCmd.Transaction = transaction;
                checkCmd.CommandText = "SELECT COUNT(*) FROM [dbo].[Form] WHERE [SyncId] = @SyncId;";
                AddParam(checkCmd, "@SyncId", payload.SyncId);

                var count = Convert.ToInt32(await checkCmd.ExecuteScalarAsync(ct));
                if (count == 0)
                {
                    throw new SyncEntityNotFoundException(
                        $"Entity not found: Form with SyncId '{payload.SyncId}' does not exist for SOFT_DELETE operation.");
                }
            }

            await using (var deleteCmd = connection.CreateCommand())
            {
                deleteCmd.Transaction = transaction;
                deleteCmd.CommandText = @"
                    UPDATE [dbo].[Form]
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

        private static async Task ApplyFormDetailsInsertAsync(
            DbConnection connection,
            DbTransaction transaction,
            ParsedFormDetailsPayload payload,
            CancellationToken ct)
        {
            await using (var existsCmd = connection.CreateCommand())
            {
                existsCmd.Transaction = transaction;
                existsCmd.CommandText = "SELECT COUNT(*) FROM [dbo].[FormDetails] WHERE [SyncId] = @SyncId;";
                AddParam(existsCmd, "@SyncId", payload.SyncId);

                var count = Convert.ToInt32(await existsCmd.ExecuteScalarAsync(ct));
                if (count > 0)
                {
                    throw new SyncEntityAlreadyExistsException(
                        $"Entity integrity violation: FormDetails with SyncId '{payload.SyncId}' already exists in remote database.");
                }
            }

            int formId;
            await using (var resolveCmd = connection.CreateCommand())
            {
                resolveCmd.Transaction = transaction;
                resolveCmd.CommandText = "SELECT Id FROM [dbo].[Form] WHERE SyncId = @FormSyncId;";
                AddParam(resolveCmd, "@FormSyncId", payload.FormSyncId);
                var obj = await resolveCmd.ExecuteScalarAsync(ct);
                if (obj == null || obj == DBNull.Value)
                {
                    throw new SyncEntityNotFoundException($"Referenced Form with SyncId '{payload.FormSyncId}' not found.");
                }
                formId = Convert.ToInt32(obj);
            }

            if (!string.IsNullOrWhiteSpace(payload.EmployeeId))
            {
                await using var empCmd = connection.CreateCommand();
                empCmd.Transaction = transaction;
                empCmd.CommandText = "SELECT COUNT(*) FROM [dbo].[Employee] WHERE [Id] = @EmployeeId;";
                AddParam(empCmd, "@EmployeeId", payload.EmployeeId);
                var empCount = Convert.ToInt32(await empCmd.ExecuteScalarAsync(ct));
                if (empCount == 0)
                {
                    throw new SyncEntityNotFoundException($"Referenced Employee with Id '{payload.EmployeeId}' not found.");
                }
            }

            await using (var insertCmd = connection.CreateCommand())
            {
                insertCmd.Transaction = transaction;
                insertCmd.CommandText = @"
                    INSERT INTO [dbo].[FormDetails]
                    ([FormId], [EmployeeId], [Amount], [OrderNum], [IsReviewed], [IsReviewedBy], [ReviewedAt], [ReviewComments],
                     [IsSummaryReviewed], [IsSummaryReviewedBy], [SummaryReviewedAt], [SummaryComments], [SummaryReviewMethod],
                     [CreatedBy], [CreatedAt], [UpdatedBy], [UpdatedAt], [DeactivatedBy], [DeactivatedAt], [IsActive], [SyncId])
                    VALUES
                    (@FormId, @EmployeeId, @Amount, @OrderNum, @IsReviewed, @IsReviewedBy, @ReviewedAt, @ReviewComments,
                     @IsSummaryReviewed, @IsSummaryReviewedBy, @SummaryReviewedAt, @SummaryComments, @SummaryReviewMethod,
                     @CreatedBy, @CreatedAt, @UpdatedBy, @UpdatedAt, @DeactivatedBy, @DeactivatedAt, @IsActive, @SyncId);";

                AddParam(insertCmd, "@FormId", formId);
                AddParam(insertCmd, "@EmployeeId", (object?)payload.EmployeeId ?? DBNull.Value);
                AddParam(insertCmd, "@Amount", payload.Amount);
                AddParam(insertCmd, "@OrderNum", payload.OrderNum);
                AddParam(insertCmd, "@IsReviewed", payload.IsReviewed);
                AddParam(insertCmd, "@IsReviewedBy", (object?)payload.IsReviewedBy ?? DBNull.Value);
                AddParam(insertCmd, "@ReviewedAt", (object?)payload.ReviewedAt ?? DBNull.Value);
                AddParam(insertCmd, "@ReviewComments", (object?)payload.ReviewComments ?? DBNull.Value);
                AddParam(insertCmd, "@IsSummaryReviewed", payload.IsSummaryReviewed);
                AddParam(insertCmd, "@IsSummaryReviewedBy", (object?)payload.IsSummaryReviewedBy ?? DBNull.Value);
                AddParam(insertCmd, "@SummaryReviewedAt", (object?)payload.SummaryReviewedAt ?? DBNull.Value);
                AddParam(insertCmd, "@SummaryComments", (object?)payload.SummaryComments ?? DBNull.Value);
                AddParam(insertCmd, "@SummaryReviewMethod", (object?)payload.SummaryReviewMethod ?? DBNull.Value);
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

        private static async Task ApplyFormDetailsUpdateAsync(
            DbConnection connection,
            DbTransaction transaction,
            ParsedFormDetailsPayload payload,
            CancellationToken ct)
        {
            await using (var checkCmd = connection.CreateCommand())
            {
                checkCmd.Transaction = transaction;
                checkCmd.CommandText = "SELECT IsActive FROM [dbo].[FormDetails] WHERE [SyncId] = @SyncId;";
                AddParam(checkCmd, "@SyncId", payload.SyncId);

                var existingActive = await checkCmd.ExecuteScalarAsync(ct);
                if (existingActive == null || existingActive == DBNull.Value)
                {
                    throw new SyncEntityNotFoundException(
                        $"Entity not found: FormDetails with SyncId '{payload.SyncId}' does not exist for UPDATE operation.");
                }

                var isCurrentActive = Convert.ToBoolean(existingActive);
                if (isCurrentActive && !payload.IsActive)
                {
                    throw new SyncPayloadValidationException(
                        "Invalid operation: Deactivating an entity must be performed via SOFT_DELETE operation.");
                }
            }

            int formId;
            await using (var resolveCmd = connection.CreateCommand())
            {
                resolveCmd.Transaction = transaction;
                resolveCmd.CommandText = "SELECT Id FROM [dbo].[Form] WHERE SyncId = @FormSyncId;";
                AddParam(resolveCmd, "@FormSyncId", payload.FormSyncId);
                var obj = await resolveCmd.ExecuteScalarAsync(ct);
                if (obj == null || obj == DBNull.Value)
                {
                    throw new SyncEntityNotFoundException($"Referenced Form with SyncId '{payload.FormSyncId}' not found.");
                }
                formId = Convert.ToInt32(obj);
            }

            if (!string.IsNullOrWhiteSpace(payload.EmployeeId))
            {
                await using var empCmd = connection.CreateCommand();
                empCmd.Transaction = transaction;
                empCmd.CommandText = "SELECT COUNT(*) FROM [dbo].[Employee] WHERE [Id] = @EmployeeId;";
                AddParam(empCmd, "@EmployeeId", payload.EmployeeId);
                var empCount = Convert.ToInt32(await empCmd.ExecuteScalarAsync(ct));
                if (empCount == 0)
                {
                    throw new SyncEntityNotFoundException($"Referenced Employee with Id '{payload.EmployeeId}' not found.");
                }
            }

            await using (var updateCmd = connection.CreateCommand())
            {
                updateCmd.Transaction = transaction;
                updateCmd.CommandText = @"
                    UPDATE [dbo].[FormDetails]
                    SET [FormId] = @FormId,
                        [EmployeeId] = @EmployeeId,
                        [Amount] = @Amount,
                        [OrderNum] = @OrderNum,
                        [IsReviewed] = @IsReviewed,
                        [IsReviewedBy] = @IsReviewedBy,
                        [ReviewedAt] = @ReviewedAt,
                        [ReviewComments] = @ReviewComments,
                        [IsSummaryReviewed] = @IsSummaryReviewed,
                        [IsSummaryReviewedBy] = @IsSummaryReviewedBy,
                        [SummaryReviewedAt] = @SummaryReviewedAt,
                        [SummaryComments] = @SummaryComments,
                        [SummaryReviewMethod] = @SummaryReviewMethod,
                        [UpdatedBy] = @UpdatedBy,
                        [UpdatedAt] = @UpdatedAt
                    WHERE [SyncId] = @SyncId;";

                AddParam(updateCmd, "@FormId", formId);
                AddParam(updateCmd, "@EmployeeId", (object?)payload.EmployeeId ?? DBNull.Value);
                AddParam(updateCmd, "@Amount", payload.Amount);
                AddParam(updateCmd, "@OrderNum", payload.OrderNum);
                AddParam(updateCmd, "@IsReviewed", payload.IsReviewed);
                AddParam(updateCmd, "@IsReviewedBy", (object?)payload.IsReviewedBy ?? DBNull.Value);
                AddParam(updateCmd, "@ReviewedAt", (object?)payload.ReviewedAt ?? DBNull.Value);
                AddParam(updateCmd, "@ReviewComments", (object?)payload.ReviewComments ?? DBNull.Value);
                AddParam(updateCmd, "@IsSummaryReviewed", payload.IsSummaryReviewed);
                AddParam(updateCmd, "@IsSummaryReviewedBy", (object?)payload.IsSummaryReviewedBy ?? DBNull.Value);
                AddParam(updateCmd, "@SummaryReviewedAt", (object?)payload.SummaryReviewedAt ?? DBNull.Value);
                AddParam(updateCmd, "@SummaryComments", (object?)payload.SummaryComments ?? DBNull.Value);
                AddParam(updateCmd, "@SummaryReviewMethod", (object?)payload.SummaryReviewMethod ?? DBNull.Value);
                AddParam(updateCmd, "@UpdatedBy", (object?)payload.UpdatedBy ?? DBNull.Value);
                AddParam(updateCmd, "@UpdatedAt", (object?)payload.UpdatedAt ?? DBNull.Value);
                AddParam(updateCmd, "@SyncId", payload.SyncId);

                await updateCmd.ExecuteNonQueryAsync(ct);
            }
        }

        private static async Task ApplyFormDetailsSoftDeleteAsync(
            DbConnection connection,
            DbTransaction transaction,
            ParsedFormDetailsPayload payload,
            CancellationToken ct)
        {
            await using (var checkCmd = connection.CreateCommand())
            {
                checkCmd.Transaction = transaction;
                checkCmd.CommandText = "SELECT COUNT(*) FROM [dbo].[FormDetails] WHERE [SyncId] = @SyncId;";
                AddParam(checkCmd, "@SyncId", payload.SyncId);

                var count = Convert.ToInt32(await checkCmd.ExecuteScalarAsync(ct));
                if (count == 0)
                {
                    throw new SyncEntityNotFoundException(
                        $"Entity not found: FormDetails with SyncId '{payload.SyncId}' does not exist for SOFT_DELETE operation.");
                }
            }

            await using (var deleteCmd = connection.CreateCommand())
            {
                deleteCmd.Transaction = transaction;
                deleteCmd.CommandText = @"
                    UPDATE [dbo].[FormDetails]
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

        public static ParsedFormPayload ParseAndValidateFormPayload(LocalOutbox outboxItem)
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
                if (!string.Equals(entityType, "Form", StringComparison.OrdinalIgnoreCase))
                {
                    throw new SyncPayloadValidationException($"Unsupported entityType '{entityType}'. Expected 'Form'.");
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

                Guid? dailySyncId = null;
                if (entityData.TryGetProperty("DailySyncId", out var dsProp) && dsProp.ValueKind == JsonValueKind.String)
                {
                    var dsStr = dsProp.GetString();
                    if (!string.IsNullOrWhiteSpace(dsStr) && Guid.TryParse(dsStr, out var g))
                    {
                        dailySyncId = g;
                    }
                }

                int? index = null;
                if (entityData.TryGetProperty("Index", out var idxProp) && idxProp.ValueKind == JsonValueKind.Number)
                {
                    index = idxProp.GetInt32();
                }

                string name = string.Empty;
                if (!string.Equals(operationType, "SOFT_DELETE", StringComparison.OrdinalIgnoreCase))
                {
                    name = entityData.TryGetProperty("Name", out var nProp) ? nProp.GetString() ?? string.Empty : string.Empty;
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        throw new SyncPayloadValidationException("Form Name is required and cannot be empty.");
                    }
                }

                string? description = entityData.TryGetProperty("Description", out var descProp) && descProp.ValueKind == JsonValueKind.String
                    ? descProp.GetString()
                    : null;

                bool isActive;
                if (string.Equals(operationType, "INSERT", StringComparison.OrdinalIgnoreCase))
                {
                    if (!entityData.TryGetProperty("IsActive", out var iaProp) || iaProp.ValueKind != JsonValueKind.True)
                    {
                        throw new SyncPayloadValidationException("INSERT operation requires IsActive to be explicitly true.");
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

                DateTime? createdAt = string.Equals(operationType, "INSERT", StringComparison.OrdinalIgnoreCase)
                    ? ParseRequiredTimestamp(entityData, "CreatedAt")
                    : ParseOptionalTimestamp(entityData, "CreatedAt");

                string? createdBy = entityData.TryGetProperty("CreatedBy", out var cbProp) && cbProp.ValueKind == JsonValueKind.String ? cbProp.GetString() : null;
                string? updatedBy = entityData.TryGetProperty("UpdatedBy", out var ubProp) && ubProp.ValueKind == JsonValueKind.String ? ubProp.GetString() : null;

                DateTime? updatedAt = string.Equals(operationType, "UPDATE", StringComparison.OrdinalIgnoreCase)
                    ? ParseRequiredTimestamp(entityData, "UpdatedAt")
                    : ParseOptionalTimestamp(entityData, "UpdatedAt");

                string? deactivatedBy = entityData.TryGetProperty("DeactivatedBy", out var dbProp) && dbProp.ValueKind == JsonValueKind.String ? dbProp.GetString() : null;

                DateTime? deactivatedAt = string.Equals(operationType, "SOFT_DELETE", StringComparison.OrdinalIgnoreCase)
                    ? ParseRequiredTimestamp(entityData, "DeactivatedAt")
                    : ParseOptionalTimestamp(entityData, "DeactivatedAt");

                return new ParsedFormPayload
                {
                    SyncId = entitySyncId,
                    DeviceId = deviceId,
                    OperationType = operationType,
                    DailySyncId = dailySyncId,
                    Index = index,
                    Name = name,
                    Description = description,
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

        public static ParsedFormDetailsPayload ParseAndValidateFormDetailsPayload(LocalOutbox outboxItem)
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
                if (!string.Equals(entityType, "FormDetails", StringComparison.OrdinalIgnoreCase))
                {
                    throw new SyncPayloadValidationException($"Unsupported entityType '{entityType}'. Expected 'FormDetails'.");
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

                if (!entityData.TryGetProperty("FormSyncId", out var fsProp) || fsProp.ValueKind != JsonValueKind.String ||
                    !Guid.TryParse(fsProp.GetString(), out var formSyncId) || formSyncId == Guid.Empty)
                {
                    throw new SyncPayloadValidationException("Valid FormSyncId is required in FormDetails entityData.");
                }

                string? employeeId = entityData.TryGetProperty("EmployeeId", out var empProp) && empProp.ValueKind == JsonValueKind.String
                    ? empProp.GetString()
                    : null;

                double amount = 0;
                if (entityData.TryGetProperty("Amount", out var amtProp) && amtProp.ValueKind == JsonValueKind.Number)
                {
                    amount = amtProp.GetDouble();
                }

                int orderNum = 0;
                if (entityData.TryGetProperty("OrderNum", out var ordProp) && ordProp.ValueKind == JsonValueKind.Number)
                {
                    orderNum = ordProp.GetInt32();
                }

                bool isReviewed = entityData.TryGetProperty("IsReviewed", out var irProp) && irProp.ValueKind == JsonValueKind.True;
                string? isReviewedBy = entityData.TryGetProperty("IsReviewedBy", out var irbProp) && irbProp.ValueKind == JsonValueKind.String ? irbProp.GetString() : null;
                DateTime? reviewedAt = ParseOptionalTimestamp(entityData, "ReviewedAt");
                string? reviewComments = entityData.TryGetProperty("ReviewComments", out var rcProp) && rcProp.ValueKind == JsonValueKind.String ? rcProp.GetString() : null;

                bool isSummaryReviewed = entityData.TryGetProperty("IsSummaryReviewed", out var isrProp) && isrProp.ValueKind == JsonValueKind.True;
                string? isSummaryReviewedBy = entityData.TryGetProperty("IsSummaryReviewedBy", out var isrbProp) && isrbProp.ValueKind == JsonValueKind.String ? isrbProp.GetString() : null;
                DateTime? summaryReviewedAt = ParseOptionalTimestamp(entityData, "SummaryReviewedAt");
                string? summaryComments = entityData.TryGetProperty("SummaryComments", out var scProp) && scProp.ValueKind == JsonValueKind.String ? scProp.GetString() : null;
                string? summaryReviewMethod = entityData.TryGetProperty("SummaryReviewMethod", out var srmProp) && srmProp.ValueKind == JsonValueKind.String ? srmProp.GetString() : null;

                bool isActive;
                if (string.Equals(operationType, "INSERT", StringComparison.OrdinalIgnoreCase))
                {
                    if (!entityData.TryGetProperty("IsActive", out var iaProp) || iaProp.ValueKind != JsonValueKind.True)
                    {
                        throw new SyncPayloadValidationException("INSERT operation requires IsActive to be explicitly true.");
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

                DateTime? createdAt = string.Equals(operationType, "INSERT", StringComparison.OrdinalIgnoreCase)
                    ? ParseRequiredTimestamp(entityData, "CreatedAt")
                    : ParseOptionalTimestamp(entityData, "CreatedAt");

                string? createdBy = entityData.TryGetProperty("CreatedBy", out var cbProp) && cbProp.ValueKind == JsonValueKind.String ? cbProp.GetString() : null;
                string? updatedBy = entityData.TryGetProperty("UpdatedBy", out var ubProp) && ubProp.ValueKind == JsonValueKind.String ? ubProp.GetString() : null;

                DateTime? updatedAt = string.Equals(operationType, "UPDATE", StringComparison.OrdinalIgnoreCase)
                    ? ParseRequiredTimestamp(entityData, "UpdatedAt")
                    : ParseOptionalTimestamp(entityData, "UpdatedAt");

                string? deactivatedBy = entityData.TryGetProperty("DeactivatedBy", out var dbProp) && dbProp.ValueKind == JsonValueKind.String ? dbProp.GetString() : null;

                DateTime? deactivatedAt = string.Equals(operationType, "SOFT_DELETE", StringComparison.OrdinalIgnoreCase)
                    ? ParseRequiredTimestamp(entityData, "DeactivatedAt")
                    : ParseOptionalTimestamp(entityData, "DeactivatedAt");

                return new ParsedFormDetailsPayload
                {
                    SyncId = entitySyncId,
                    DeviceId = deviceId,
                    OperationType = operationType,
                    FormSyncId = formSyncId,
                    EmployeeId = employeeId,
                    Amount = amount,
                    OrderNum = orderNum,
                    IsReviewed = isReviewed,
                    IsReviewedBy = isReviewedBy,
                    ReviewedAt = reviewedAt,
                    ReviewComments = reviewComments,
                    IsSummaryReviewed = isSummaryReviewed,
                    IsSummaryReviewedBy = isSummaryReviewedBy,
                    SummaryReviewedAt = summaryReviewedAt,
                    SummaryComments = summaryComments,
                    SummaryReviewMethod = summaryReviewMethod,
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

        public sealed class ParsedFormPayload
        {
            public required Guid SyncId { get; init; }
            public required Guid DeviceId { get; init; }
            public required string OperationType { get; init; }
            public Guid? DailySyncId { get; init; }
            public int? Index { get; init; }
            public required string Name { get; init; }
            public string? Description { get; init; }
            public required bool IsActive { get; init; }
            public DateTime? CreatedAt { get; init; }
            public string? CreatedBy { get; init; }
            public DateTime? UpdatedAt { get; init; }
            public string? UpdatedBy { get; init; }
            public DateTime? DeactivatedAt { get; init; }
            public string? DeactivatedBy { get; init; }
        }

        public sealed class ParsedFormDetailsPayload
        {
            public required Guid SyncId { get; init; }
            public required Guid DeviceId { get; init; }
            public required string OperationType { get; init; }
            public required Guid FormSyncId { get; init; }
            public string? EmployeeId { get; init; }
            public double Amount { get; init; }
            public int OrderNum { get; init; }
            public bool IsReviewed { get; init; }
            public string? IsReviewedBy { get; init; }
            public DateTime? ReviewedAt { get; init; }
            public string? ReviewComments { get; init; }
            public bool IsSummaryReviewed { get; init; }
            public string? IsSummaryReviewedBy { get; init; }
            public DateTime? SummaryReviewedAt { get; init; }
            public string? SummaryComments { get; init; }
            public string? SummaryReviewMethod { get; init; }
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
