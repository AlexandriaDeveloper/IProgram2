#nullable enable
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Auth.Infrastructure.Sync.Push;
using Core.Interfaces;
using Core.Models.Sync;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Auth.Infrastructure.Sync
{
    public class SyncStatusService : ISyncStatusService
    {
        private readonly ISyncConnectionProvider _syncConnectionProvider;
        private readonly IRemoteDatabaseConnectionFactory _remoteConnectionFactory;
        private readonly ILocalScopeBaselineService _scopeBaselineService;
        private readonly ILogger<SyncStatusService> _logger;

        public SyncStatusService(
            ISyncConnectionProvider syncConnectionProvider,
            IRemoteDatabaseConnectionFactory remoteConnectionFactory,
            ILocalScopeBaselineService scopeBaselineService,
            ILogger<SyncStatusService> logger)
        {
            _syncConnectionProvider = syncConnectionProvider ?? throw new ArgumentNullException(nameof(syncConnectionProvider));
            _remoteConnectionFactory = remoteConnectionFactory ?? throw new ArgumentNullException(nameof(remoteConnectionFactory));
            _scopeBaselineService = scopeBaselineService ?? throw new ArgumentNullException(nameof(scopeBaselineService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        protected virtual DbConnection CreateLocalConnection(string connectionString)
        {
            return new SqlConnection(connectionString);
        }

        public virtual async Task<LocalSyncStatusDto> GetLocalStatusAsync(string databaseId, CancellationToken cancellationToken = default)
        {
            ValidateDatabaseId(databaseId);

            var isReadOnly = _syncConnectionProvider.IsReadOnlyMode;
            var isLocalFirst = _syncConnectionProvider.IsLocalFirstEnabled;
            string runtimeMode = isReadOnly ? "OfflineReadOnly" : (isLocalFirst ? "OfflineReadWritePilot" : "Online");

            var dto = new LocalSyncStatusDto
            {
                DatabaseId = databaseId,
                RuntimeMode = runtimeMode,
                IsReadOnly = isReadOnly,
                IsLocalFirst = isLocalFirst
            };

            var localConnStr = _syncConnectionProvider.GetLocalConnectionString(databaseId);
            await using var conn = CreateLocalConnection(localConnStr);
            await conn.OpenAsync(cancellationToken);

            // Read [sync].[LocalState]
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT TOP 1 LastServerVersion, LastSuccessfulPushUtc, LastSuccessfulPullUtc, LastSyncAttemptUtc, LastSyncError
                    FROM [sync].[LocalState]
                    WHERE DatabaseId = @DatabaseId;";
                var p = cmd.CreateParameter();
                p.ParameterName = "@DatabaseId";
                p.Value = databaseId;
                cmd.Parameters.Add(p);

                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    dto.LastServerVersion = reader.GetInt64(0);
                    dto.LastSuccessfulPushUtc = reader.IsDBNull(1) ? null : reader.GetDateTime(1);
                    dto.LastSuccessfulPullUtc = reader.IsDBNull(2) ? null : reader.GetDateTime(2);
                    dto.LastSyncAttemptUtc = reader.IsDBNull(3) ? null : reader.GetDateTime(3);
                    dto.LastSyncError = reader.IsDBNull(4) ? null : reader.GetString(4);
                }
            }

            // Read [sync].[LocalOutbox] counts
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT Status, COUNT(*)
                    FROM [sync].[LocalOutbox]
                    WHERE DatabaseId = @DatabaseId
                    GROUP BY Status;";
                var p = cmd.CreateParameter();
                p.ParameterName = "@DatabaseId";
                p.Value = databaseId;
                cmd.Parameters.Add(p);

                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var status = reader.GetString(0);
                    var count = reader.GetInt32(1);
                    if (string.Equals(status, "PENDING", StringComparison.OrdinalIgnoreCase))
                    {
                        dto.PendingCount = count;
                    }
                    else if (string.Equals(status, "IN_PROGRESS", StringComparison.OrdinalIgnoreCase))
                    {
                        dto.InProgressCount = count;
                    }
                    else if (string.Equals(status, "FAILED", StringComparison.OrdinalIgnoreCase))
                    {
                        dto.FailedCount = count;
                    }
                    dto.TotalCount += count;
                }
            }

            return dto;
        }

        protected virtual async Task<(int PendingDaily, int PendingForms)> GetPendingCountsByScopeAsync(
            DbConnection connection,
            string databaseId,
            CancellationToken cancellationToken)
        {
            int pendingDaily = 0;
            int pendingForms = 0;

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                SELECT AggregateType, COUNT(*)
                FROM [sync].[LocalOutbox]
                WHERE DatabaseId = @DatabaseId AND Status IN ('PENDING', 'IN_PROGRESS')
                GROUP BY AggregateType;";
            var p = cmd.CreateParameter();
            p.ParameterName = "@DatabaseId";
            p.Value = databaseId;
            cmd.Parameters.Add(p);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var aggType = reader.GetString(0);
                var cnt = reader.GetInt32(1);
                if (string.Equals(aggType, "Daily", StringComparison.OrdinalIgnoreCase))
                {
                    pendingDaily += cnt;
                }
                else if (string.Equals(aggType, "Form", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(aggType, "FormDetails", StringComparison.OrdinalIgnoreCase))
                {
                    pendingForms += cnt;
                }
            }

            return (pendingDaily, pendingForms);
        }

        public async Task<OnlineSyncStatusDto> CheckOnlineStatusAsync(string databaseId, CancellationToken cancellationToken = default)
        {
            ValidateDatabaseId(databaseId);

            var localStatus = await GetLocalStatusAsync(databaseId, cancellationToken);

            var dto = new OnlineSyncStatusDto
            {
                DatabaseId = databaseId,
                LocalVersion = localStatus.LastServerVersion,
                TotalPendingCount = localStatus.PendingCount + localStatus.InProgressCount,
                CheckedAtUtc = DateTime.UtcNow
            };

            // Read Scope Baselines from local DB
            var localConnStr = _syncConnectionProvider.GetLocalConnectionString(databaseId);
            SyncScopeBaselineStatus dailyBaselineStatus;
            SyncScopeBaselineStatus formsBaselineStatus;

            int pendingDaily = 0;
            int pendingForms = 0;

            await using (var localConn = CreateLocalConnection(localConnStr))
            {
                await localConn.OpenAsync(cancellationToken);

                dailyBaselineStatus = await _scopeBaselineService.GetScopeStatusAsync(localConn, null, databaseId, "Daily", cancellationToken);
                formsBaselineStatus = await _scopeBaselineService.GetScopeStatusAsync(localConn, null, databaseId, "Forms", cancellationToken);

                (pendingDaily, pendingForms) = await GetPendingCountsByScopeAsync(localConn, databaseId, cancellationToken);
            }

            // Attempt read-only remote check
            long serverVersion = 0;
            try
            {
                await using var remoteConn = await _remoteConnectionFactory.CreateOpenConnectionAsync(databaseId, cancellationToken);
                await using var cmd = remoteConn.CreateCommand();
                cmd.CommandText = "SELECT CurrentVersion FROM [sync].[ServerState] WHERE DatabaseId = @DatabaseId;";
                var p = cmd.CreateParameter();
                p.ParameterName = "@DatabaseId";
                p.Value = databaseId;
                cmd.Parameters.Add(p);

                var obj = await cmd.ExecuteScalarAsync(cancellationToken);
                if (obj != null && obj != DBNull.Value)
                {
                    serverVersion = Convert.ToInt64(obj);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Remote connection check failed for database '{DatabaseId}'.", databaseId);
                dto.IsOnline = false;
                dto.OverallStatus = "UNKNOWN";
                dto.ErrorMessage = "تعذر الاتصال بالسحابة: " + ex.Message;

                dto.Scopes.Add(new ScopeSyncStatusDto
                {
                    Scope = "Daily",
                    Status = "UNKNOWN",
                    IsBaselined = dailyBaselineStatus == SyncScopeBaselineStatus.Baselined,
                    LocalVersion = localStatus.LastServerVersion,
                    ServerVersion = 0,
                    PendingCount = pendingDaily,
                    Message = "تعذر الاتصال بالخادم السحابي"
                });

                dto.Scopes.Add(new ScopeSyncStatusDto
                {
                    Scope = "Forms",
                    Status = formsBaselineStatus == SyncScopeBaselineStatus.Baselined ? "UNKNOWN" : "NOT_BASELINED",
                    IsBaselined = formsBaselineStatus == SyncScopeBaselineStatus.Baselined,
                    LocalVersion = localStatus.LastServerVersion,
                    ServerVersion = 0,
                    PendingCount = pendingForms,
                    Message = formsBaselineStatus == SyncScopeBaselineStatus.Baselined
                        ? "تعذر الاتصال بالخادم السحابي"
                        : "نطاق النماذج غير مطابق الأساس بعد (Not Baselined)"
                });

                return dto;
            }

            dto.IsOnline = true;
            dto.ServerVersion = serverVersion;

            // Evaluate Daily Scope
            string dailyStatus;
            string dailyMessage;
            if (dailyBaselineStatus != SyncScopeBaselineStatus.Baselined)
            {
                dailyStatus = "NOT_BASELINED";
                dailyMessage = "نطاق اليوميات غير مطابق الأساس بعد";
            }
            else
            {
                if (localStatus.LastServerVersion == serverVersion && pendingDaily == 0)
                {
                    dailyStatus = "UP_TO_DATE";
                    dailyMessage = "البيانات متطابقة مع السحابة";
                }
                else if (localStatus.LastServerVersion < serverVersion && pendingDaily == 0)
                {
                    dailyStatus = "REMOTE_NEWER";
                    dailyMessage = $"توجد تحديثات جديدة على السحابة (الإصدار {serverVersion} مقابل {localStatus.LastServerVersion})";
                }
                else if (localStatus.LastServerVersion < serverVersion && pendingDaily > 0)
                {
                    dailyStatus = "BOTH_CHANGED";
                    dailyMessage = "توجد تعديلات محلية معلقة وتحديثات جديدة على السحابة (تتطلب مراجعة)";
                }
                else // localStatus.LastServerVersion >= serverVersion && pendingDaily > 0
                {
                    dailyStatus = "UP_TO_DATE";
                    dailyMessage = $"البيانات محدثة محلياً مع وجود {pendingDaily} تعديلات معلقة للرفع";
                }
            }

            dto.Scopes.Add(new ScopeSyncStatusDto
            {
                Scope = "Daily",
                Status = dailyStatus,
                IsBaselined = dailyBaselineStatus == SyncScopeBaselineStatus.Baselined,
                LocalVersion = localStatus.LastServerVersion,
                ServerVersion = serverVersion,
                PendingCount = pendingDaily,
                Message = dailyMessage
            });

            // Evaluate Forms Scope
            // Explicit rule: Forms must display NOT_BASELINED until a separately authorized baseline/re-baseline occurs
            string formsStatus;
            string formsMessage;
            if (formsBaselineStatus != SyncScopeBaselineStatus.Baselined)
            {
                formsStatus = "NOT_BASELINED";
                formsMessage = "نطاق النماذج بانتظار إجراء مطابقة الأساس المعتمدة (NOT_BASELINED)";
            }
            else
            {
                if (localStatus.LastServerVersion == serverVersion && pendingForms == 0)
                {
                    formsStatus = "UP_TO_DATE";
                    formsMessage = "النماذج متطابقة مع السحابة";
                }
                else if (localStatus.LastServerVersion < serverVersion && pendingForms == 0)
                {
                    formsStatus = "REMOTE_NEWER";
                    formsMessage = "توجد نماذج أحدث على السحابة";
                }
                else if (localStatus.LastServerVersion < serverVersion && pendingForms > 0)
                {
                    formsStatus = "BOTH_CHANGED";
                    formsMessage = "توجد تعديلات على النماذج محلياً وسحابياً";
                }
                else
                {
                    formsStatus = "UP_TO_DATE";
                    formsMessage = $"النماذج محدثة مع وجود {pendingForms} تعديلات معلقة";
                }
            }

            dto.Scopes.Add(new ScopeSyncStatusDto
            {
                Scope = "Forms",
                Status = formsStatus,
                IsBaselined = formsBaselineStatus == SyncScopeBaselineStatus.Baselined,
                LocalVersion = localStatus.LastServerVersion,
                ServerVersion = serverVersion,
                PendingCount = pendingForms,
                Message = formsMessage
            });

            // Derive Overall Status
            if (dailyStatus == "BOTH_CHANGED" || formsStatus == "BOTH_CHANGED")
            {
                dto.OverallStatus = "BOTH_CHANGED";
            }
            else if (dailyStatus == "REMOTE_NEWER" || formsStatus == "REMOTE_NEWER")
            {
                dto.OverallStatus = "REMOTE_NEWER";
            }
            else
            {
                dto.OverallStatus = dailyStatus;
            }

            return dto;
        }

        private static void ValidateDatabaseId(string databaseId)
        {
            if (string.IsNullOrWhiteSpace(databaseId) || (databaseId != "2026" && databaseId != "2027"))
            {
                throw new ArgumentException($"Invalid canonical database ID '{databaseId}'. Expected '2026' or '2027'.", nameof(databaseId));
            }
        }
    }
}
