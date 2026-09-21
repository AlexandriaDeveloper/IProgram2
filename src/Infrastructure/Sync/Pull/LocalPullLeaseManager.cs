#nullable enable
using System;
using Microsoft.Data.SqlClient;
using System.Threading;
using System.Threading.Tasks;
using Core.Exceptions;
using Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Auth.Infrastructure.Sync.Pull
{
    public class LocalPullLeaseManager : ILocalPullLeaseManager
    {
        private readonly ISyncConnectionProvider _syncConnectionProvider;
        private readonly ILogger<LocalPullLeaseManager> _logger;

        public LocalPullLeaseManager(
            ISyncConnectionProvider syncConnectionProvider,
            ILogger<LocalPullLeaseManager> logger)
        {
            _syncConnectionProvider = syncConnectionProvider ?? throw new ArgumentNullException(nameof(syncConnectionProvider));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        private static string MaskToken(Guid token)
        {
            return token.ToString().Substring(0, 8) + "***";
        }

        public async Task<Guid> AcquireLeaseAsync(string databaseId, TimeSpan duration, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(databaseId))
            {
                throw new InvalidDatabaseSelectionException("Canonical database ID is required to acquire lease.");
            }

            var normDbId = databaseId.Trim();
            var leaseToken = Guid.NewGuid();
            var durationSeconds = (int)Math.Max(5, duration.TotalSeconds);
            var localConnStr = _syncConnectionProvider.GetLocalConnectionString(normDbId);

            await using var conn = new SqlConnection(localConnStr);
            await conn.OpenAsync(cancellationToken);

            // Fail-closed check: LocalState row must exist
            await using (var checkCmd = conn.CreateCommand())
            {
                checkCmd.CommandText = "SELECT COUNT(*) FROM [sync].[LocalState] WHERE DatabaseId = @DatabaseId;";
                checkCmd.Parameters.AddWithValue("@DatabaseId", normDbId);
                var existsCount = Convert.ToInt32(await checkCmd.ExecuteScalarAsync(cancellationToken));
                if (existsCount == 0)
                {
                    throw new SyncLocalStateMissingException($"LocalState record does not exist for DatabaseId '{normDbId}'.");
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
            cmd.Parameters.AddWithValue("@DatabaseId", normDbId);

            var rows = await cmd.ExecuteNonQueryAsync(cancellationToken);
            if (rows == 0)
            {
                _logger.LogWarning("Failed to acquire pull lease for DatabaseId {DatabaseId}: active lease already held by another session.", normDbId);
                throw new SyncPullAlreadyRunningException($"A sync push or pull session is already actively running for DatabaseId '{normDbId}'.");
            }

            _logger.LogInformation("Acquired pull lease for DatabaseId {DatabaseId} with masked token {MaskedToken} (Duration: {DurationSeconds}s).",
                normDbId, MaskToken(leaseToken), durationSeconds);

            return leaseToken;
        }

        public async Task RenewLeaseAsync(string databaseId, Guid leaseToken, TimeSpan duration, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(databaseId)) throw new ArgumentNullException(nameof(databaseId));

            var normDbId = databaseId.Trim();
            var durationSeconds = (int)Math.Max(5, duration.TotalSeconds);
            var localConnStr = _syncConnectionProvider.GetLocalConnectionString(normDbId);

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
            cmd.Parameters.AddWithValue("@DatabaseId", normDbId);
            cmd.Parameters.AddWithValue("@LeaseToken", leaseToken);

            var rows = await cmd.ExecuteNonQueryAsync(cancellationToken);
            if (rows == 0)
            {
                _logger.LogWarning("Pull lease renewal failed for DatabaseId {DatabaseId}. Lease is either expired or owned by another session.", normDbId);
                throw new SyncLeaseExpiredException($"Pull lease expired or stolen for DatabaseId '{normDbId}'.");
            }
        }

        public async Task ValidateLeaseOwnershipAsync(string databaseId, Guid leaseToken, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(databaseId)) throw new ArgumentNullException(nameof(databaseId));

            var normDbId = databaseId.Trim();
            var localConnStr = _syncConnectionProvider.GetLocalConnectionString(normDbId);

            await using var conn = new SqlConnection(localConnStr);
            await conn.OpenAsync(cancellationToken);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT COUNT(*)
                FROM [sync].[LocalState]
                WHERE DatabaseId = @DatabaseId
                  AND ActiveLeaseToken = @LeaseToken
                  AND LeaseExpiresAtUtc >= SYSUTCDATETIME();";

            cmd.Parameters.AddWithValue("@DatabaseId", normDbId);
            cmd.Parameters.AddWithValue("@LeaseToken", leaseToken);

            var validCount = Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken));
            if (validCount == 0)
            {
                _logger.LogWarning("Pull lease ownership validation failed for DatabaseId {DatabaseId}. Stale lease owner rejected.", normDbId);
                throw new SyncLeaseExpiredException($"Stale pull lease owner rejected for DatabaseId '{normDbId}'.");
            }
        }

        public async Task ReleaseLeaseAsync(string databaseId, Guid leaseToken, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(databaseId)) return;

            var normDbId = databaseId.Trim();
            try
            {
                var localConnStr = _syncConnectionProvider.GetLocalConnectionString(normDbId);
                await using var conn = new SqlConnection(localConnStr);
                await conn.OpenAsync(cancellationToken);

                await using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    UPDATE [sync].[LocalState]
                    SET ActiveLeaseToken = NULL,
                        LeaseExpiresAtUtc = NULL
                    WHERE DatabaseId = @DatabaseId AND ActiveLeaseToken = @LeaseToken;";

                cmd.Parameters.AddWithValue("@DatabaseId", normDbId);
                cmd.Parameters.AddWithValue("@LeaseToken", leaseToken);

                await cmd.ExecuteNonQueryAsync(cancellationToken);
                _logger.LogInformation("Released pull lease for DatabaseId {DatabaseId} with masked token {MaskedToken}.", normDbId, MaskToken(leaseToken));
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to gracefully release pull lease for DatabaseId {DatabaseId} (masked token: {MaskedToken}), ErrorType: {ErrorType}. Lease will expire automatically.",
                    normDbId, MaskToken(leaseToken), ex.GetType().Name);
            }
        }
    }
}
