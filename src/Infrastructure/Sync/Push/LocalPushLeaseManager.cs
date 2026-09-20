#nullable enable
using System;
using Microsoft.Data.SqlClient;
using System.Threading;
using System.Threading.Tasks;
using Core.Exceptions;
using Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Auth.Infrastructure.Sync.Push
{
    public class LocalPushLeaseManager : ILocalPushLeaseManager
    {
        private readonly ISyncConnectionProvider _syncConnectionProvider;
        private readonly ILogger<LocalPushLeaseManager> _logger;

        public LocalPushLeaseManager(
            ISyncConnectionProvider syncConnectionProvider,
            ILogger<LocalPushLeaseManager> logger)
        {
            _syncConnectionProvider = syncConnectionProvider ?? throw new ArgumentNullException(nameof(syncConnectionProvider));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<Guid> AcquireLeaseAsync(string databaseId, TimeSpan duration, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(databaseId))
            {
                throw new InvalidDatabaseSelectionException("Canonical database ID is required to acquire lease.");
            }

            var leaseToken = Guid.NewGuid();
            var durationSeconds = (int)Math.Max(5, duration.TotalSeconds);
            var localConnStr = _syncConnectionProvider.GetLocalConnectionString(databaseId);

            await using var conn = new SqlConnection(localConnStr);
            await conn.OpenAsync(cancellationToken);

            // Fail-closed check: LocalState row must exist
            await using (var checkCmd = conn.CreateCommand())
            {
                checkCmd.CommandText = "SELECT COUNT(*) FROM [sync].[LocalState] WHERE DatabaseId = @DatabaseId;";
                checkCmd.Parameters.AddWithValue("@DatabaseId", databaseId.Trim());
                var existsCount = Convert.ToInt32(await checkCmd.ExecuteScalarAsync(cancellationToken));
                if (existsCount == 0)
                {
                    throw new SyncLocalStateMissingException($"LocalState record does not exist for DatabaseId '{databaseId}'.");
                }
            }

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                UPDATE [sync].[LocalState]
                SET ActiveLeaseToken = @LeaseToken,
                    LeaseExpiresAtUtc = DATEADD(SECOND, @DurationSec, SYSUTCDATETIME()),
                    LastSyncAttemptUtc = SYSUTCDATETIME()
                WHERE DatabaseId = @DatabaseId
                  AND (ActiveLeaseToken IS NULL OR LeaseExpiresAtUtc < SYSUTCDATETIME() OR ActiveLeaseToken = @LeaseToken);";

            cmd.Parameters.AddWithValue("@LeaseToken", leaseToken);
            cmd.Parameters.AddWithValue("@DurationSec", durationSeconds);
            cmd.Parameters.AddWithValue("@DatabaseId", databaseId.Trim());

            var rows = await cmd.ExecuteNonQueryAsync(cancellationToken);
            if (rows == 0)
            {
                _logger.LogWarning("Failed to acquire push lease for DatabaseId {DatabaseId}: active lease already held.", databaseId);
                throw new SyncPushAlreadyRunningException($"A push session is already actively running for DatabaseId '{databaseId}'.");
            }

            _logger.LogInformation("Acquired push lease for DatabaseId {DatabaseId} with token {LeaseToken} (Duration: {DurationSeconds}s).",
                databaseId, leaseToken, durationSeconds);

            return leaseToken;
        }

        public async Task RenewLeaseAsync(string databaseId, Guid leaseToken, TimeSpan duration, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(databaseId)) throw new ArgumentNullException(nameof(databaseId));

            var durationSeconds = (int)Math.Max(5, duration.TotalSeconds);
            var localConnStr = _syncConnectionProvider.GetLocalConnectionString(databaseId);

            await using var conn = new SqlConnection(localConnStr);
            await conn.OpenAsync(cancellationToken);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                UPDATE [sync].[LocalState]
                SET LeaseExpiresAtUtc = DATEADD(SECOND, @DurationSec, SYSUTCDATETIME())
                WHERE DatabaseId = @DatabaseId
                  AND ActiveLeaseToken = @LeaseToken
                  AND LeaseExpiresAtUtc >= SYSUTCDATETIME();";

            cmd.Parameters.AddWithValue("@DurationSec", durationSeconds);
            cmd.Parameters.AddWithValue("@DatabaseId", databaseId.Trim());
            cmd.Parameters.AddWithValue("@LeaseToken", leaseToken);

            var rows = await cmd.ExecuteNonQueryAsync(cancellationToken);
            if (rows == 0)
            {
                _logger.LogWarning("Push lease renewal failed for DatabaseId {DatabaseId}. Lease is either expired or owned by another session.", databaseId);
                throw new SyncLeaseExpiredException($"Push lease expired or stolen for DatabaseId '{databaseId}'.");
            }
        }

        public async Task ValidateLeaseOwnershipAsync(string databaseId, Guid leaseToken, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(databaseId)) throw new ArgumentNullException(nameof(databaseId));

            var localConnStr = _syncConnectionProvider.GetLocalConnectionString(databaseId);

            await using var conn = new SqlConnection(localConnStr);
            await conn.OpenAsync(cancellationToken);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT COUNT(*)
                FROM [sync].[LocalState]
                WHERE DatabaseId = @DatabaseId
                  AND ActiveLeaseToken = @LeaseToken
                  AND LeaseExpiresAtUtc >= SYSUTCDATETIME();";

            cmd.Parameters.AddWithValue("@DatabaseId", databaseId.Trim());
            cmd.Parameters.AddWithValue("@LeaseToken", leaseToken);

            var validCount = Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken));
            if (validCount == 0)
            {
                _logger.LogWarning("Push lease ownership validation failed for DatabaseId {DatabaseId}. Stale lease owner rejected.", databaseId);
                throw new SyncLeaseExpiredException($"Stale push lease owner rejected for DatabaseId '{databaseId}'.");
            }
        }

        public async Task ReleaseLeaseAsync(string databaseId, Guid leaseToken, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(databaseId)) return;

            try
            {
                var localConnStr = _syncConnectionProvider.GetLocalConnectionString(databaseId);
                await using var conn = new SqlConnection(localConnStr);
                await conn.OpenAsync(cancellationToken);

                await using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    UPDATE [sync].[LocalState]
                    SET ActiveLeaseToken = NULL,
                        LeaseExpiresAtUtc = NULL
                    WHERE DatabaseId = @DatabaseId AND ActiveLeaseToken = @LeaseToken;";

                cmd.Parameters.AddWithValue("@DatabaseId", databaseId.Trim());
                cmd.Parameters.AddWithValue("@LeaseToken", leaseToken);

                await cmd.ExecuteNonQueryAsync(cancellationToken);
                _logger.LogInformation("Released push lease for DatabaseId {DatabaseId} with token {LeaseToken}.", databaseId, leaseToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to gracefully release push lease for DatabaseId {DatabaseId} (token: {LeaseToken}), ErrorType: {ErrorType}. Lease will expire automatically.",
                    databaseId, leaseToken, ex.GetType().Name);
            }
        }
    }
}
