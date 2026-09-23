#nullable enable
using System;
using System.Collections.Generic;
using Microsoft.Data.SqlClient;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Core.Exceptions;
using Core.Interfaces;
using Core.Models.Sync;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Auth.Infrastructure.Sync.Push
{
    public class LocalOutboxPushService : ILocalOutboxPushService
    {
        private readonly ISyncConnectionProvider _syncConnectionProvider;
        private readonly IRemoteDatabaseConnectionFactory _remoteConnectionFactory;
        private readonly IAzurePushTransactionCoordinator _transactionCoordinator;
        private readonly ILocalPushLeaseManager _leaseManager;
        private readonly ILocalScopeBaselineService? _scopeBaselineService;
        private readonly IConfiguration _configuration;
        private readonly ILogger<LocalOutboxPushService> _logger;

        public LocalOutboxPushService(
            ISyncConnectionProvider syncConnectionProvider,
            IRemoteDatabaseConnectionFactory remoteConnectionFactory,
            IAzurePushTransactionCoordinator transactionCoordinator,
            ILocalPushLeaseManager leaseManager,
            IConfiguration configuration,
            ILogger<LocalOutboxPushService> logger,
            ILocalScopeBaselineService? scopeBaselineService = null)
        {
            _syncConnectionProvider = syncConnectionProvider ?? throw new ArgumentNullException(nameof(syncConnectionProvider));
            _remoteConnectionFactory = remoteConnectionFactory ?? throw new ArgumentNullException(nameof(remoteConnectionFactory));
            _transactionCoordinator = transactionCoordinator ?? throw new ArgumentNullException(nameof(transactionCoordinator));
            _leaseManager = leaseManager ?? throw new ArgumentNullException(nameof(leaseManager));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _scopeBaselineService = scopeBaselineService;
        }

        public async Task<PushBatchResult> PushPendingOutboxAsync(CancellationToken cancellationToken)
        {
            // 1. Feature gate check: must be explicitly enabled
            var isPushEnabled = _configuration.GetValue<bool>("Sync:PushEnabled", false);
            if (!isPushEnabled)
            {
                _logger.LogWarning("Push attempt rejected: Sync:PushEnabled is false.");
                throw new SyncPushDisabledException("مزامنة الرفع (Push) معطلة على هذا النظام (Sync:PushEnabled = false).");
            }

            // 2. Runtime mode check: must be strictly in OfflineReadWritePilot
            if (!_syncConnectionProvider.IsLocalFirstEnabled || _syncConnectionProvider.IsReadOnlyMode)
            {
                _logger.LogWarning("Push attempt rejected: Invalid runtime mode. (LocalFirst: {LocalFirst}, ReadOnly: {ReadOnly})",
                    _syncConnectionProvider.IsLocalFirstEnabled, _syncConnectionProvider.IsReadOnlyMode);
                throw new InvalidOperationException("مزامنة الرفع مصرح بها فقط في وضع OfflineReadWritePilot.");
            }

            // 3. DatabaseId selection: strictly derived from authenticated context / JWT claim
            var databaseId = _syncConnectionProvider.GetSelectedDatabaseId();
            if (string.IsNullOrWhiteSpace(databaseId) || (databaseId != "2026" && databaseId != "2027"))
            {
                throw new InvalidDatabaseSelectionException($"Invalid canonical database ID '{databaseId}'. Expected '2026' or '2027'.");
            }

            // 4. Acquire atomic local lease (prevents concurrent push for same databaseId)
            var leaseDuration = TimeSpan.FromSeconds(60);
            var leaseToken = await _leaseManager.AcquireLeaseAsync(databaseId, leaseDuration, cancellationToken);

            var batchResult = new PushBatchResult
            {
                DatabaseId = databaseId,
                LeaseToken = leaseToken
            };

            try
            {
                var localConnStr = _syncConnectionProvider.GetLocalConnectionString(databaseId);

                // 5. Read local LastServerVersion (dynamic ExpectedServerVersion baseline)
                long expectedServerVersion = await GetLastServerVersionAsync(localConnStr, databaseId, cancellationToken);
                Guid localDeviceId = await GetLocalDeviceIdAsync(localConnStr, databaseId, cancellationToken);

                // 6. Query pending outbox operations strictly ordered FIFO
                var pendingOperations = await GetPendingOutboxOperationsAsync(localConnStr, databaseId, cancellationToken);
                _logger.LogInformation("Found {Count} pending outbox operations for DatabaseId {DatabaseId} (ExpectedServerVersion: {Version}).",
                    pendingOperations.Count, databaseId, expectedServerVersion);

                if (pendingOperations.Count == 0)
                {
                    batchResult.FinalServerVersion = expectedServerVersion;
                    return batchResult;
                }

                // If any pending outbox operation is Form or FormDetails, enforce Forms scope baseline (fail-closed)
                if (pendingOperations.Any(o => o.AggregateType == "Form" || o.AggregateType == "FormDetails"))
                {
                    if (_scopeBaselineService != null)
                    {
                        await using var baselineConn = new Microsoft.Data.SqlClient.SqlConnection(localConnStr);
                        await baselineConn.OpenAsync(cancellationToken);
                        await _scopeBaselineService.EnsureScopeBaselinedAsync(baselineConn, null, databaseId, "Forms", cancellationToken);
                    }
                }

                // 7. Open dedicated remote connection
                await using var remoteConnection = await _remoteConnectionFactory.CreateOpenConnectionAsync(databaseId, cancellationToken);

                // 8. Process operations one by one
                foreach (var outboxItem in pendingOperations)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Step 8a: Renew lease & validate ownership before attempting to process/claim
                    await _leaseManager.RenewLeaseAsync(databaseId, leaseToken, leaseDuration, cancellationToken);
                    await _leaseManager.ValidateLeaseOwnershipAsync(databaseId, leaseToken, cancellationToken);

                    // Step 8b: Claim outbox row atomically with lease fencing (handles both PENDING and expired IN_PROGRESS)
                    var claimed = await TryClaimOutboxInProgressAsync(localConnStr, databaseId, outboxItem.ClientOperationId, leaseToken, cancellationToken);
                    if (!claimed)
                    {
                        _logger.LogWarning(
                            "Outbox queue head operation {ClientOperationId} could not be claimed with active lease {LeaseToken}. Halting queue processing to preserve strict FIFO.",
                            outboxItem.ClientOperationId, leaseToken);
                        throw new SyncLeaseExpiredException(
                            $"Failed to claim queue head outbox operation '{outboxItem.ClientOperationId}' with active lease '{leaseToken}'. Queue processing halted.");
                    }

                    // Step 8c: Compute deterministic RequestHash (SHA-256)
                    var requestHash = ComputeRequestHash(
                        databaseId,
                        localDeviceId,
                        outboxItem.CommandName,
                        outboxItem.AggregateType,
                        outboxItem.EntitySyncId,
                        outboxItem.PayloadJson);

                    try
                    {
                        // Step 8d: Apply operation atomically on remote database (passing validated localDeviceId)
                        var applyResult = await _transactionCoordinator.ApplyOperationAsync(
                            remoteConnection,
                            databaseId,
                            outboxItem,
                            expectedServerVersion,
                            requestHash,
                            localDeviceId,
                            cancellationToken);

                        var returnedServerVersion = applyResult.ServerVersion;

                        // Step 8e: Validate lease ownership again before acknowledging locally
                        await _leaseManager.ValidateLeaseOwnershipAsync(databaseId, leaseToken, cancellationToken);

                        // Step 8f: Atomically mark outbox COMPLETED locally and advance LocalState with lease fencing
                        await AcknowledgeSuccessLocallyAsync(
                            localConnStr,
                            databaseId,
                            outboxItem.ClientOperationId,
                            returnedServerVersion,
                            leaseToken,
                            cancellationToken);

                        // Advance dynamic expected server version for subsequent operation
                        expectedServerVersion = returnedServerVersion;

                        batchResult.TotalProcessed++;
                        batchResult.Succeeded++;
                        batchResult.Operations.Add(new OperationPushResult
                        {
                            ClientOperationId = outboxItem.ClientOperationId,
                            EntitySyncId = outboxItem.EntitySyncId,
                            OperationType = outboxItem.CommandName,
                            Status = applyResult.IsReplay ? "REPLAY" : "SUCCESS",
                            ServerVersion = returnedServerVersion
                        });

                        _logger.LogInformation(
                            "Push operation {ClientOperationId} succeeded ({Status}). ServerVersion: {ServerVersion}.",
                            outboxItem.ClientOperationId, applyResult.IsReplay ? "REPLAY" : "SUCCESS", returnedServerVersion);
                    }
                    catch (Exception ex)
                    {
                        var sanitizedError = SanitizeErrorMessage(ex);
                        var errorCode = (ex as SyncDomainException)?.ErrorCode ?? ex.GetType().Name;

                        if (ex is SyncLeaseExpiredException)
                        {
                            _logger.LogWarning(
                                "Lease expired or ownership lost for ClientOperationId {ClientOperationId}, ErrorCode: {ErrorCode}. Skipping local failure record to prevent modifying state belonging to a newer lease owner.",
                                outboxItem.ClientOperationId, errorCode);
                            throw;
                        }

                        _logger.LogError(
                            "Push failed for ClientOperationId {ClientOperationId}, ErrorCode: {ErrorCode}. Message: {Error}",
                            outboxItem.ClientOperationId, errorCode, sanitizedError);

                        // Atomically mark outbox failure and update LocalState error with lease fencing
                        await RecordFailureLocallyAsync(localConnStr, databaseId, outboxItem.ClientOperationId, leaseToken, sanitizedError, cancellationToken);

                        batchResult.TotalProcessed++;
                        batchResult.Failed++;
                        batchResult.Operations.Add(new OperationPushResult
                        {
                            ClientOperationId = outboxItem.ClientOperationId,
                            EntitySyncId = outboxItem.EntitySyncId,
                            OperationType = outboxItem.CommandName,
                            Status = "FAILED",
                            ServerVersion = expectedServerVersion,
                            ErrorCode = errorCode,
                            ErrorMessage = sanitizedError
                        });

                        // Re-throw terminal error so queue halts strictly and controller emits appropriate HTTP status
                        throw;
                    }
                }

                batchResult.FinalServerVersion = expectedServerVersion;
                return batchResult;
            }
            finally
            {
                // Always release the local lease in finally
                await _leaseManager.ReleaseLeaseAsync(databaseId, leaseToken, cancellationToken);
            }
        }

        public static string ComputeRequestHash(
            string databaseId,
            Guid deviceId,
            string commandName,
            string entityType,
            Guid entitySyncId,
            string payloadJson)
        {
            var raw = $"{databaseId}:{deviceId}:{commandName}:{entityType}:{entitySyncId}:{payloadJson}";
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        private static async Task<long> GetLastServerVersionAsync(string localConnStr, string databaseId, CancellationToken ct)
        {
            await using var conn = new SqlConnection(localConnStr);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT LastServerVersion FROM [sync].[LocalState] WHERE DatabaseId = @DatabaseId;";
            cmd.Parameters.AddWithValue("@DatabaseId", databaseId);

            var val = await cmd.ExecuteScalarAsync(ct);
            if (val == null || val == DBNull.Value)
            {
                throw new SyncLocalStateMissingException($"LocalState record is missing for DatabaseId '{databaseId}'.");
            }
            return Convert.ToInt64(val);
        }

        private static async Task<Guid> GetLocalDeviceIdAsync(string localConnStr, string databaseId, CancellationToken ct)
        {
            await using var conn = new SqlConnection(localConnStr);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT DeviceId FROM [sync].[LocalState] WHERE DatabaseId = @DatabaseId;";
            cmd.Parameters.AddWithValue("@DatabaseId", databaseId);

            var val = await cmd.ExecuteScalarAsync(ct);
            if (val == null || val == DBNull.Value || (Guid)val == Guid.Empty)
            {
                throw new SyncLocalStateMissingException($"LocalState DeviceId is missing or empty for DatabaseId '{databaseId}'.");
            }
            return (Guid)val;
        }

        private static async Task<List<LocalOutbox>> GetPendingOutboxOperationsAsync(string localConnStr, string databaseId, CancellationToken ct)
        {
            var list = new List<LocalOutbox>();
            await using var conn = new SqlConnection(localConnStr);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT ClientOperationId, DatabaseId, AggregateType, CommandName, EntitySyncId, PayloadJson, CreatedAtUtc, Status, RetryCount
                FROM [sync].[LocalOutbox]
                WHERE DatabaseId = @DatabaseId
                  AND Status IN ('PENDING', 'IN_PROGRESS')
                ORDER BY CreatedAtUtc ASC, ClientOperationId ASC;";
            cmd.Parameters.AddWithValue("@DatabaseId", databaseId);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                list.Add(new LocalOutbox
                {
                    ClientOperationId = reader.GetGuid(0),
                    DatabaseId = reader.GetString(1),
                    AggregateType = reader.GetString(2),
                    CommandName = reader.GetString(3),
                    EntitySyncId = reader.GetGuid(4),
                    PayloadJson = reader.GetString(5),
                    CreatedAtUtc = reader.GetDateTime(6),
                    Status = reader.GetString(7),
                    RetryCount = reader.GetInt32(8)
                });
            }
            return list;
        }

        public static async Task<bool> TryClaimOutboxInProgressAsync(string localConnStr, string databaseId, Guid clientOperationId, Guid leaseToken, CancellationToken ct)
        {
            await using var conn = new SqlConnection(localConnStr);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                UPDATE o
                SET o.Status = 'IN_PROGRESS',
                    o.LockToken = @LockToken,
                    o.LockedUntilUtc = DATEADD(SECOND, 60, SYSUTCDATETIME())
                FROM [sync].[LocalOutbox] o
                INNER JOIN [sync].[LocalState] s ON s.DatabaseId = o.DatabaseId
                WHERE o.DatabaseId = @DatabaseId
                  AND o.ClientOperationId = @ClientOperationId
                  AND s.ActiveLeaseToken = @LockToken
                  AND s.LeaseExpiresAtUtc >= SYSUTCDATETIME()
                  AND (o.Status = 'PENDING' OR (o.Status = 'IN_PROGRESS' AND (o.LockedUntilUtc IS NULL OR o.LockedUntilUtc < SYSUTCDATETIME())));";
            cmd.Parameters.AddWithValue("@LockToken", leaseToken);
            cmd.Parameters.AddWithValue("@DatabaseId", databaseId);
            cmd.Parameters.AddWithValue("@ClientOperationId", clientOperationId);
            var rows = await cmd.ExecuteNonQueryAsync(ct);
            return rows > 0;
        }

        public static async Task AcknowledgeSuccessLocallyAsync(
            string localConnStr,
            string databaseId,
            Guid clientOperationId,
            long newServerVersion,
            Guid leaseToken,
            CancellationToken ct)
        {
            await using var conn = new SqlConnection(localConnStr);
            await conn.OpenAsync(ct);
            await using var transaction = (SqlTransaction)await conn.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
            try
            {
                // 1. Verify lease ownership fencing and advance LocalState version in same local transaction
                await using (var stateCmd = conn.CreateCommand())
                {
                    stateCmd.Transaction = transaction;
                    stateCmd.CommandText = @"
                        UPDATE [sync].[LocalState]
                        SET LastServerVersion = @NewVersion,
                            LastSuccessfulPushUtc = SYSUTCDATETIME(),
                            LastSyncAttemptUtc = SYSUTCDATETIME(),
                            LastSyncError = NULL,
                            LeaseExpiresAtUtc = DATEADD(SECOND, 60, SYSUTCDATETIME())
                        WHERE DatabaseId = @DatabaseId
                          AND ActiveLeaseToken = @LeaseToken
                          AND LeaseExpiresAtUtc >= SYSUTCDATETIME();";

                    stateCmd.Parameters.AddWithValue("@NewVersion", newServerVersion);
                    stateCmd.Parameters.AddWithValue("@DatabaseId", databaseId);
                    stateCmd.Parameters.AddWithValue("@LeaseToken", leaseToken);

                    var rows = await stateCmd.ExecuteNonQueryAsync(ct);
                    if (rows == 0)
                    {
                        throw new SyncLeaseExpiredException(
                            $"Lease expired or ownership lost for DatabaseId '{databaseId}'. Local acknowledgement aborted.");
                    }
                }

                // 2. Mark Outbox COMPLETED in same local transaction with strict fencing
                await using (var outboxCmd = conn.CreateCommand())
                {
                    outboxCmd.Transaction = transaction;
                    outboxCmd.CommandText = @"
                        UPDATE [sync].[LocalOutbox]
                        SET Status = 'COMPLETED',
                            CompletedAtUtc = SYSUTCDATETIME(),
                            LockToken = NULL,
                            LockedUntilUtc = NULL,
                            LastError = NULL
                        WHERE DatabaseId = @DatabaseId
                          AND ClientOperationId = @ClientOperationId
                          AND Status = 'IN_PROGRESS'
                          AND LockToken = @LeaseToken;";

                    outboxCmd.Parameters.AddWithValue("@DatabaseId", databaseId);
                    outboxCmd.Parameters.AddWithValue("@ClientOperationId", clientOperationId);
                    outboxCmd.Parameters.AddWithValue("@LeaseToken", leaseToken);

                    var outboxRows = await outboxCmd.ExecuteNonQueryAsync(ct);
                    if (outboxRows != 1)
                    {
                        throw new SyncLeaseExpiredException(
                            $"Outbox ownership lost or row not in IN_PROGRESS state for ClientOperationId '{clientOperationId}' and LeaseToken '{leaseToken}'. Affected rows: {outboxRows}. Transaction rolled back.");
                    }
                }

                await transaction.CommitAsync(ct);
            }
            catch
            {
                try { await transaction.RollbackAsync(ct); } catch { }
                throw;
            }
        }

        public static async Task RecordFailureLocallyAsync(
            string localConnStr,
            string databaseId,
            Guid clientOperationId,
            Guid leaseToken,
            string sanitizedError,
            CancellationToken ct)
        {
            try
            {
                await using var conn = new SqlConnection(localConnStr);
                await conn.OpenAsync(ct);
                await using var transaction = (SqlTransaction)await conn.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
                try
                {
                    await using (var outboxCmd = conn.CreateCommand())
                    {
                        outboxCmd.Transaction = transaction;
                        outboxCmd.CommandText = @"
                            UPDATE [sync].[LocalOutbox]
                            SET Status = 'PENDING',
                                RetryCount = RetryCount + 1,
                                LastError = @LastError,
                                LockToken = NULL,
                                LockedUntilUtc = NULL
                            WHERE DatabaseId = @DatabaseId
                              AND ClientOperationId = @ClientOperationId
                              AND Status = 'IN_PROGRESS'
                              AND LockToken = @LeaseToken;";
                        outboxCmd.Parameters.AddWithValue("@LastError", sanitizedError);
                        outboxCmd.Parameters.AddWithValue("@DatabaseId", databaseId);
                        outboxCmd.Parameters.AddWithValue("@ClientOperationId", clientOperationId);
                        outboxCmd.Parameters.AddWithValue("@LeaseToken", leaseToken);
                        await outboxCmd.ExecuteNonQueryAsync(ct);
                    }

                    await using (var stateCmd = conn.CreateCommand())
                    {
                        stateCmd.Transaction = transaction;
                        stateCmd.CommandText = @"
                            UPDATE [sync].[LocalState]
                            SET LastSyncError = @LastError,
                                LastSyncAttemptUtc = SYSUTCDATETIME()
                            WHERE DatabaseId = @DatabaseId
                              AND ActiveLeaseToken = @LeaseToken;";
                        stateCmd.Parameters.AddWithValue("@LastError", sanitizedError);
                        stateCmd.Parameters.AddWithValue("@DatabaseId", databaseId);
                        stateCmd.Parameters.AddWithValue("@LeaseToken", leaseToken);
                        await stateCmd.ExecuteNonQueryAsync(ct);
                    }

                    await transaction.CommitAsync(ct);
                }
                catch
                {
                    try { await transaction.RollbackAsync(ct); } catch { }
                    throw;
                }
            }
            catch
            {
                // Silently swallow secondary failure to record failure state so original exception bubbles up
            }
        }

        private static string SanitizeErrorMessage(Exception ex)
        {
            if (ex is SyncDomainException)
            {
                return ex.Message;
            }
            return $"{ex.GetType().Name}: An error occurred during remote push transaction.";
        }
    }
}
