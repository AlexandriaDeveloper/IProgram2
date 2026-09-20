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
                _logger.LogWarning(ex, "Failed to gracefully release push lease for DatabaseId {DatabaseId} (token: {LeaseToken}). Lease will expire automatically.",
                    databaseId, leaseToken);
            }
        }
    }
}
