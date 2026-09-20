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
        private readonly IConfiguration _configuration;
        private readonly ILogger<LocalOutboxPushService> _logger;

        public LocalOutboxPushService(
            ISyncConnectionProvider syncConnectionProvider,
            IRemoteDatabaseConnectionFactory remoteConnectionFactory,
            IAzurePushTransactionCoordinator transactionCoordinator,
            ILocalPushLeaseManager leaseManager,
            IConfiguration configuration,
            ILogger<LocalOutboxPushService> logger)
        {
            _syncConnectionProvider = syncConnectionProvider ?? throw new ArgumentNullException(nameof(syncConnectionProvider));
            _remoteConnectionFactory = remoteConnectionFactory ?? throw new ArgumentNullException(nameof(remoteConnectionFactory));
            _transactionCoordinator = transactionCoordinator ?? throw new ArgumentNullException(nameof(transactionCoordinator));
            _leaseManager = leaseManager ?? throw new ArgumentNullException(nameof(leaseManager));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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

                // 7. Open dedicated remote connection
                await using var remoteConnection = await _remoteConnectionFactory.CreateOpenConnectionAsync(databaseId, cancellationToken);

                // 8. Process operations one by one
                foreach (var outboxItem in pendingOperations)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Step 8a: Mark outbox IN_PROGRESS locally
                    await MarkOutboxInProgressAsync(localConnStr, outboxItem.ClientOperationId, leaseToken, cancellationToken);

                    // Step 8b: Compute deterministic RequestHash (SHA-256)
                    var requestHash = ComputeRequestHash(
                        databaseId,
                        localDeviceId,
                        outboxItem.CommandName,
                        outboxItem.AggregateType,
                        outboxItem.EntitySyncId,
                        outboxItem.PayloadJson);

                    try
                    {
                        // Step 8c: Apply operation atomically on remote database
                        var applyResult = await _transactionCoordinator.ApplyOperationAsync(
                            remoteConnection,
                            databaseId,
                            outboxItem,
                            expectedServerVersion,
                            requestHash,
                            cancellationToken);

                        var returnedServerVersion = applyResult.ServerVersion;

                        // Step 8d: Mark outbox COMPLETED locally and update LocalState
                        await MarkOutboxCompletedAsync(localConnStr, outboxItem.ClientOperationId, cancellationToken);
                        await UpdateLocalStateServerVersionAsync(localConnStr, databaseId, returnedServerVersion, cancellationToken);

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
                            "Push operation {ClientOperationId} succeeded ({Status}). Advanced ServerVersion to {ServerVersion}.",
                            outboxItem.ClientOperationId, applyResult.IsReplay ? "REPLAY" : "SUCCESS", returnedServerVersion);
                    }
                    catch (Exception ex)
                    {
                        var sanitizedError = SanitizeErrorMessage(ex);
                        _logger.LogError(ex, "Push failed for ClientOperationId {ClientOperationId}: {Error}",
                            outboxItem.ClientOperationId, sanitizedError);

                        // Mark outbox failure and LocalState error
                        await MarkOutboxFailedAsync(localConnStr, outboxItem.ClientOperationId, sanitizedError, cancellationToken);
                        await UpdateLocalStateErrorAsync(localConnStr, databaseId, sanitizedError, cancellationToken);

                        batchResult.TotalProcessed++;
                        batchResult.Failed++;
                        batchResult.Operations.Add(new OperationPushResult
                        {
                            ClientOperationId = outboxItem.ClientOperationId,
                            EntitySyncId = outboxItem.EntitySyncId,
                            OperationType = outboxItem.CommandName,
                            Status = "FAILED",
                            ServerVersion = expectedServerVersion,
                            ErrorCode = ex.GetType().Name,
                            ErrorMessage = sanitizedError
                        });

                        // CRITICAL: Stop processing subsequent operations to enforce strict queue ordering
                        _logger.LogWarning("Halting push queue for DatabaseId {DatabaseId} to preserve strict FIFO ordering.", databaseId);
                        break;
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
            if (val == null || val == DBNull.Value) return 0L;
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
            if (val == null || val == DBNull.Value) return Guid.Empty;
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
                WHERE DatabaseId = @DatabaseId AND Status = 'PENDING'
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

        private static async Task MarkOutboxInProgressAsync(string localConnStr, Guid clientOperationId, Guid leaseToken, CancellationToken ct)
        {
            await using var conn = new SqlConnection(localConnStr);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                UPDATE [sync].[LocalOutbox]
                SET Status = 'IN_PROGRESS',
                    LockToken = @LockToken,
                    LockedUntilUtc = DATEADD(SECOND, 60, SYSUTCDATETIME())
                WHERE ClientOperationId = @ClientOperationId AND Status = 'PENDING';";
            cmd.Parameters.AddWithValue("@LockToken", leaseToken);
            cmd.Parameters.AddWithValue("@ClientOperationId", clientOperationId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        private static async Task MarkOutboxCompletedAsync(string localConnStr, Guid clientOperationId, CancellationToken ct)
        {
            await using var conn = new SqlConnection(localConnStr);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                UPDATE [sync].[LocalOutbox]
                SET Status = 'COMPLETED',
                    CompletedAtUtc = SYSUTCDATETIME(),
                    LockToken = NULL,
                    LockedUntilUtc = NULL,
                    LastError = NULL
                WHERE ClientOperationId = @ClientOperationId;";
            cmd.Parameters.AddWithValue("@ClientOperationId", clientOperationId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        private static async Task MarkOutboxFailedAsync(string localConnStr, Guid clientOperationId, string lastError, CancellationToken ct)
        {
            await using var conn = new SqlConnection(localConnStr);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                UPDATE [sync].[LocalOutbox]
                SET Status = 'PENDING',
                    RetryCount = RetryCount + 1,
                    LastError = @LastError,
                    LockToken = NULL,
                    LockedUntilUtc = NULL
                WHERE ClientOperationId = @ClientOperationId;";
            cmd.Parameters.AddWithValue("@LastError", lastError);
            cmd.Parameters.AddWithValue("@ClientOperationId", clientOperationId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        private static async Task UpdateLocalStateServerVersionAsync(string localConnStr, string databaseId, long newVersion, CancellationToken ct)
        {
            await using var conn = new SqlConnection(localConnStr);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                UPDATE [sync].[LocalState]
                SET LastServerVersion = @NewVersion,
                    LastSuccessfulPushUtc = SYSUTCDATETIME(),
                    LastSyncAttemptUtc = SYSUTCDATETIME(),
                    LastSyncError = NULL
                WHERE DatabaseId = @DatabaseId;";
            cmd.Parameters.AddWithValue("@NewVersion", newVersion);
            cmd.Parameters.AddWithValue("@DatabaseId", databaseId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        private static async Task UpdateLocalStateErrorAsync(string localConnStr, string databaseId, string error, CancellationToken ct)
        {
            await using var conn = new SqlConnection(localConnStr);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                UPDATE [sync].[LocalState]
                SET LastSyncError = @Error,
                    LastSyncAttemptUtc = SYSUTCDATETIME()
                WHERE DatabaseId = @DatabaseId;";
            cmd.Parameters.AddWithValue("@Error", error);
            cmd.Parameters.AddWithValue("@DatabaseId", databaseId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        private static string SanitizeErrorMessage(Exception ex)
        {
            if (ex is SyncVersionConflictException or SyncOperationIdReuseException or SyncEntityAlreadyExistsException or SyncEntityNotFoundException or SyncPayloadValidationException or SyncPushAlreadyRunningException or SyncPushDisabledException)
            {
                return ex.Message;
            }
            return $"{ex.GetType().Name}: An error occurred during remote push transaction.";
        }
    }
}
