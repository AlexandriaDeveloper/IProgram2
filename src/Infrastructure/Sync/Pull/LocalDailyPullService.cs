#nullable enable
using System;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using System.Threading;
using System.Threading.Tasks;
using Core.Exceptions;
using Core.Interfaces;
using Core.Models.Sync;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Auth.Infrastructure.Sync.Pull
{
    public class LocalDailyPullService : ILocalDailyPullService
    {
        private readonly ISyncConnectionProvider _syncConnectionProvider;
        private readonly ILocalPullLeaseManager _leaseManager;
        private readonly IAzureFencedBatchReader _fencedBatchReader;
        private readonly ILocalPullTransactionCoordinator _transactionCoordinator;
        private readonly ILocalScopeBaselineService _scopeBaselineService;
        private readonly IConfiguration _configuration;
        private readonly ILogger<LocalDailyPullService> _logger;

        public LocalDailyPullService(
            ISyncConnectionProvider syncConnectionProvider,
            ILocalPullLeaseManager leaseManager,
            IAzureFencedBatchReader fencedBatchReader,
            ILocalPullTransactionCoordinator transactionCoordinator,
            IConfiguration configuration,
            ILogger<LocalDailyPullService> logger,
            ILocalScopeBaselineService scopeBaselineService)
        {
            _syncConnectionProvider = syncConnectionProvider ?? throw new ArgumentNullException(nameof(syncConnectionProvider));
            _leaseManager = leaseManager ?? throw new ArgumentNullException(nameof(leaseManager));
            _fencedBatchReader = fencedBatchReader ?? throw new ArgumentNullException(nameof(fencedBatchReader));
            _transactionCoordinator = transactionCoordinator ?? throw new ArgumentNullException(nameof(transactionCoordinator));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _scopeBaselineService = scopeBaselineService ?? throw new ArgumentNullException(nameof(scopeBaselineService));
        }

        public async Task<PullResultDto> PullDailyChangesAsync(CancellationToken cancellationToken, bool isExplicitManual = false)
        {
            // 1. Feature gate check: background/automatic pull requires Sync:PullEnabled = true
            if (!isExplicitManual)
            {
                var isPullEnabled = _configuration.GetValue<bool>("Sync:PullEnabled", false);
                if (!isPullEnabled)
                {
                    _logger.LogWarning("Pull attempt rejected: Sync:PullEnabled is false.");
                    throw new SyncPullDisabledException("ميزة مزامنة السحب (Pull) معطلة على هذا النظام (Sync:PullEnabled = false).");
                }
            }

            // 2. Runtime mode check: must be strictly in LocalFirst and not ReadOnly
            if (!_syncConnectionProvider.IsLocalFirstEnabled || _syncConnectionProvider.IsReadOnlyMode)
            {
                _logger.LogWarning("Pull attempt rejected: Invalid runtime mode. (LocalFirst: {LocalFirst}, ReadOnly: {ReadOnly})",
                    _syncConnectionProvider.IsLocalFirstEnabled, _syncConnectionProvider.IsReadOnlyMode);
                throw new InvalidOperationException("ميزة مزامنة السحب مصرح بها فقط في وضع LocalFirst مع تمكين الكتابة (!ReadOnly).");
            }

            // 3. DatabaseId selection: strictly derived from authenticated context
            var databaseId = _syncConnectionProvider.GetSelectedDatabaseId();
            if (string.IsNullOrWhiteSpace(databaseId) || (databaseId != "2026" && databaseId != "2027"))
            {
                throw new InvalidDatabaseSelectionException($"Invalid canonical database ID '{databaseId}'. Expected '2026' or '2027'.");
            }

            var normDbId = databaseId.Trim();

            // 4. Physical Binding Pre-flight Validation (Fail closed before network calls)
            ValidatePhysicalBindings(normDbId);

            // 5. Acquire atomic local lease (mutual exclusion with Push and concurrent Pull)
            var leaseDuration = TimeSpan.FromSeconds(60);
            var leaseToken = await _leaseManager.AcquireLeaseAsync(normDbId, leaseDuration, cancellationToken);

            try
            {
                var localConnStr = _syncConnectionProvider.GetLocalConnectionString(normDbId);

                // 6. Read local checkpoint L
                long localCheckpoint = await GetLocalLastServerVersionAsync(localConnStr, normDbId, cancellationToken);

                // 7. Pre-flight Outbox check: Block if any PENDING, IN_PROGRESS, or FAILED exist
                await ValidateLocalOutboxStateAsync(localConnStr, normDbId, cancellationToken);

                // 8. Materialize Fenced Azure Batch
                var batch = await _fencedBatchReader.ReadFencedBatchAsync(normDbId, localCheckpoint, cancellationToken);

                // 8b. If batch contains Forms entities, enforce persistent Forms scope baseline (fail-closed)
                if (batch.Commands.Any(i => i.EntityType == "Form" || i.EntityType == "FormDetails" || i.EntityType == "FormRefernce"))
                {
                    await using var baselineConn = new Microsoft.Data.SqlClient.SqlConnection(localConnStr);
                    await baselineConn.OpenAsync(cancellationToken);
                    await _scopeBaselineService.EnsureScopeBaselinedAsync(baselineConn, null, normDbId, "Forms", cancellationToken);
                }

                // 9. If No-Op (H == L)
                if (batch.IsNoOp)
                {
                    _logger.LogInformation("Pull for DatabaseId {DatabaseId} is a NO-OP. Local checkpoint {Checkpoint} matches server.",
                        normDbId, localCheckpoint);

                    // P0: Verify current pull still owns an unexpired lease before returning success
                    await _leaseManager.ValidateLeaseOwnershipAsync(normDbId, leaseToken, cancellationToken);

                    // P0: Atomically record successful sync attempt with strict lease fencing
                    await RecordSuccessfulNoOpPullAsync(localConnStr, normDbId, leaseToken, cancellationToken);

                    return new PullResultDto
                    {
                        DatabaseId = normDbId,
                        PreviousWatermark = localCheckpoint,
                        FinalServerVersion = localCheckpoint,
                        IsNoOp = true,
                        TotalProcessed = 0,
                        Succeeded = 0,
                        Failed = 0,
                        Message = "No changes to pull. Local checkpoint matches server version."
                    };
                }

                // 10. Apply Coalesced Batch Locally (atomic local transaction with lease fencing)
                var result = await _transactionCoordinator.ApplyPullBatchAsync(normDbId, batch, leaseToken, cancellationToken);
                return result;
            }
            finally
            {
                await _leaseManager.ReleaseLeaseAsync(normDbId, leaseToken, CancellationToken.None);
            }
        }

        private void ValidatePhysicalBindings(string databaseId)
        {
            // Local physical binding validation
            var localConnStr = _syncConnectionProvider.GetLocalConnectionString(databaseId);
            var localBuilder = new SqlConnectionStringBuilder(localConnStr);
            DatabaseBindingValidator.ValidateLocalBinding(localBuilder.DataSource, localBuilder.InitialCatalog);
            DatabaseBindingValidator.ValidateTargetDatabase(databaseId, localBuilder.InitialCatalog, isLocalTarget: true);
        }

        private static async Task<long> GetLocalLastServerVersionAsync(string localConnStr, string databaseId, CancellationToken cancellationToken)
        {
            await using var conn = new SqlConnection(localConnStr);
            await conn.OpenAsync(cancellationToken);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT LastServerVersion FROM [sync].[LocalState] WHERE DatabaseId = @DatabaseId;";
            cmd.Parameters.AddWithValue("@DatabaseId", databaseId);

            var scalar = await cmd.ExecuteScalarAsync(cancellationToken);
            if (scalar == null || scalar == DBNull.Value)
            {
                throw new SyncLocalStateMissingException($"LocalState record does not exist for DatabaseId '{databaseId}'.");
            }

            return Convert.ToInt64(scalar);
        }

        private static async Task ValidateLocalOutboxStateAsync(string localConnStr, string databaseId, CancellationToken cancellationToken)
        {
            await using var conn = new SqlConnection(localConnStr);
            await conn.OpenAsync(cancellationToken);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT COUNT(*)
                FROM [sync].[LocalOutbox]
                WHERE DatabaseId = @DatabaseId
                  AND Status IN ('PENDING', 'IN_PROGRESS', 'FAILED');";
            cmd.Parameters.AddWithValue("@DatabaseId", databaseId);

            var pendingCount = Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken));
            if (pendingCount > 0)
            {
                throw new SyncPullBlockedLocalChangesPendingException(
                    $"Pull blocked: {pendingCount} unmerged local outbox changes found (PENDING/IN_PROGRESS/FAILED) for DatabaseId '{databaseId}'.");
            }
        }

        private static async Task RecordSuccessfulNoOpPullAsync(string localConnStr, string databaseId, Guid leaseToken, CancellationToken cancellationToken)
        {
            await using var conn = new SqlConnection(localConnStr);
            await conn.OpenAsync(cancellationToken);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                UPDATE [sync].[LocalState]
                SET LastSuccessfulPullUtc = SYSUTCDATETIME(),
                    LastSyncAttemptUtc = SYSUTCDATETIME(),
                    LastSyncError = NULL
                WHERE DatabaseId = @DatabaseId
                  AND ActiveLeaseToken = @LeaseToken
                  AND LeaseExpiresAtUtc >= SYSUTCDATETIME();";
            cmd.Parameters.AddWithValue("@DatabaseId", databaseId);
            cmd.Parameters.AddWithValue("@LeaseToken", leaseToken);

            var rows = await cmd.ExecuteNonQueryAsync(cancellationToken);
            if (rows == 0)
            {
                throw new SyncLeaseExpiredException($"Failed to record NO-OP sync checkpoint. Pull lease expired or stolen for DatabaseId '{databaseId}'.");
            }
        }
    }
}
