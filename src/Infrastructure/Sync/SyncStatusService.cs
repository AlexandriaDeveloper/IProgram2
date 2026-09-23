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
            Guid? activeLeaseToken = null;
            DateTime? leaseExpiresAtUtc = null;
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT TOP 1 LastServerVersion, LastSuccessfulPushUtc, LastSuccessfulPullUtc, LastSyncAttemptUtc, LastSyncError,
                                 ActiveLeaseToken, LeaseExpiresAtUtc
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

                    if (reader.FieldCount > 5 && !reader.IsDBNull(5))
                    {
                        activeLeaseToken = reader.GetGuid(5);
                    }
                    if (reader.FieldCount > 6 && !reader.IsDBNull(6))
                    {
                        leaseExpiresAtUtc = reader.GetDateTime(6);
                    }
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

            if (dto.InProgressCount > 0)
            {
                bool hasValidLease = activeLeaseToken.HasValue && leaseExpiresAtUtc.HasValue && leaseExpiresAtUtc.Value >= DateTime.UtcNow;
                if (!hasValidLease)
                {
                    dto.HasOrphanInProgress = true;
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
            var changedEntityTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool isServerStateMissing = false;

            try
            {
                await using var remoteConn = await _remoteConnectionFactory.CreateOpenConnectionAsync(databaseId, cancellationToken);
                await using (var cmd = remoteConn.CreateCommand())
                {
                    cmd.CommandText = "SELECT CurrentVersion FROM [sync].[ServerState] WHERE DatabaseId = @DatabaseId;";
                    var p = cmd.CreateParameter();
                    p.ParameterName = "@DatabaseId";
                    p.Value = databaseId;
                    cmd.Parameters.Add(p);

                    var obj = await cmd.ExecuteScalarAsync(cancellationToken);
                    if (obj == null || obj == DBNull.Value)
                    {
                        isServerStateMissing = true;
                    }
                    else
                    {
                        serverVersion = Convert.ToInt64(obj);
                    }
                }

                // If remote version > local version, query ServerChangeFeed for changed entity types in (W, V]
                if (!isServerStateMissing && serverVersion > localStatus.LastServerVersion)
                {
                    await using var feedCmd = remoteConn.CreateCommand();
                    feedCmd.CommandText = @"
                        SELECT DISTINCT EntityType
                        FROM [sync].[ServerChangeFeed]
                        WHERE DatabaseId = @DatabaseId
                          AND ServerVersion > @LocalVersion
                          AND ServerVersion <= @ServerVersion;";

                    var pDb = feedCmd.CreateParameter();
                    pDb.ParameterName = "@DatabaseId";
                    pDb.Value = databaseId;
                    feedCmd.Parameters.Add(pDb);

                    var pLocalVer = feedCmd.CreateParameter();
                    pLocalVer.ParameterName = "@LocalVersion";
                    pLocalVer.Value = localStatus.LastServerVersion;
                    feedCmd.Parameters.Add(pLocalVer);

                    var pServerVer = feedCmd.CreateParameter();
                    pServerVer.ParameterName = "@ServerVersion";
                    pServerVer.Value = serverVersion;
                    feedCmd.Parameters.Add(pServerVer);

                    await using var reader = await feedCmd.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        if (!reader.IsDBNull(0))
                        {
                            changedEntityTypes.Add(reader.GetString(0));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Remote connection check failed for database '{DatabaseId}'.", databaseId);
                dto.IsOnline = false;
                dto.OverallStatus = "UNKNOWN";
                dto.ErrorMessage = (ex.Message == "MANUAL_SYNC_REMOTE_NOT_CONFIGURED")
                    ? "الاتصال بالسحابة غير مهيأ لهذا الجهاز (MANUAL_SYNC_REMOTE_NOT_CONFIGURED)"
                    : "تعذر الاتصال بالخادم السحابي";

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

            // Invariant & Health Checks:
            // 1. Missing/malformed ServerState
            // 2. W > V (Local checkpoint ahead of server)
            // 3. Local FAILED outbox > 0
            // 4. Stale/orphan IN_PROGRESS state
            // 5. Inconsistent/gapped ServerChangeFeed when V > W
            bool isFeedInconsistent = !isServerStateMissing &&
                                      serverVersion > localStatus.LastServerVersion &&
                                      changedEntityTypes.Count == 0;

            bool hasSyncStateError = isServerStateMissing ||
                                     (localStatus.LastServerVersion > serverVersion) ||
                                     (localStatus.FailedCount > 0) ||
                                     localStatus.HasOrphanInProgress ||
                                     isFeedInconsistent;

            string dailyStatus;
            string dailyMessage;
            string formsStatus;
            string formsMessage;

            if (hasSyncStateError)
            {
                dailyStatus = (dailyBaselineStatus == SyncScopeBaselineStatus.Baselined) ? "SYNC_STATE_ERROR" : "NOT_BASELINED";
                dailyMessage = "خطأ في حالة المزامنة (بيانات السحابة مفقودة أو عمليات فاشلة أو عدم تطابق في الإصدارات)";

                formsStatus = (formsBaselineStatus == SyncScopeBaselineStatus.Baselined) ? "SYNC_STATE_ERROR" : "NOT_BASELINED";
                formsMessage = (formsBaselineStatus == SyncScopeBaselineStatus.Baselined)
                    ? "خطأ في حالة المزامنة (بيانات السحابة مفقودة أو عمليات فاشلة أو عدم تطابق في الإصدارات)"
                    : "نطاق النماذج بانتظار إجراء مطابقة الأساس المعتمدة (NOT_BASELINED)";
            }
            else
            {
                bool hasDailyRemoteChanges = changedEntityTypes.Contains("Daily");
                bool hasFormsRemoteChanges = changedEntityTypes.Contains("Form") ||
                                             changedEntityTypes.Contains("FormDetails") ||
                                             changedEntityTypes.Contains("FormRefernce");

                // Evaluate Daily Scope
                if (dailyBaselineStatus != SyncScopeBaselineStatus.Baselined)
                {
                    dailyStatus = "NOT_BASELINED";
                    dailyMessage = "نطاق اليوميات غير مطابق الأساس بعد";
                }
                else
                {
                    if (serverVersion > localStatus.LastServerVersion && hasDailyRemoteChanges)
                    {
                        dailyStatus = (pendingDaily > 0) ? "BOTH_CHANGED" : "REMOTE_NEWER";
                        dailyMessage = (pendingDaily > 0)
                            ? "توجد تعديلات محلية معلقة وتحديثات جديدة على السحابة"
                            : $"توجد تحديثات جديدة على السحابة (الإصدار {serverVersion} مقابل {localStatus.LastServerVersion})";
                    }
                    else if (serverVersion > localStatus.LastServerVersion && !hasDailyRemoteChanges)
                    {
                        dailyStatus = (pendingDaily > 0) ? "LOCAL_PENDING" : "UP_TO_DATE";
                        dailyMessage = (pendingDaily > 0)
                            ? $"توجد {pendingDaily} تعديلات معلقة للرفع"
                            : "البيانات متطابقة مع السحابة";
                    }
                    else // V == W
                    {
                        dailyStatus = (pendingDaily > 0) ? "LOCAL_PENDING" : "UP_TO_DATE";
                        dailyMessage = (pendingDaily > 0)
                            ? $"البيانات محدثة محلياً مع وجود {pendingDaily} تعديلات معلقة للرفع"
                            : "البيانات متطابقة مع السحابة";
                    }
                }

                // Evaluate Forms Scope
                // Forms must display NOT_BASELINED until a separately authorized baseline/re-baseline occurs
                if (formsBaselineStatus != SyncScopeBaselineStatus.Baselined)
                {
                    formsStatus = "NOT_BASELINED";
                    formsMessage = "نطاق النماذج بانتظار إجراء مطابقة الأساس المعتمدة (NOT_BASELINED)";
                }
                else
                {
                    if (serverVersion > localStatus.LastServerVersion && hasFormsRemoteChanges)
                    {
                        formsStatus = (pendingForms > 0) ? "BOTH_CHANGED" : "REMOTE_NEWER";
                        formsMessage = (pendingForms > 0)
                            ? "توجد تعديلات على النماذج محلياً وسحابياً"
                            : "توجد نماذج أحدث على السحابة";
                    }
                    else if (serverVersion > localStatus.LastServerVersion && !hasFormsRemoteChanges)
                    {
                        formsStatus = (pendingForms > 0) ? "LOCAL_PENDING" : "UP_TO_DATE";
                        formsMessage = (pendingForms > 0)
                            ? $"النماذج محدثة مع وجود {pendingForms} تعديلات معلقة"
                            : "النماذج متطابقة مع السحابة";
                    }
                    else // V == W
                    {
                        formsStatus = (pendingForms > 0) ? "LOCAL_PENDING" : "UP_TO_DATE";
                        formsMessage = (pendingForms > 0)
                            ? $"النماذج محدثة مع وجود {pendingForms} تعديلات معلقة"
                            : "النماذج متطابقة مع السحابة";
                    }
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

            // Derive Overall Status according to canonical precedence:
            // SYNC_STATE_ERROR > BOTH_CHANGED > REMOTE_NEWER > LOCAL_PENDING > NOT_BASELINED > UP_TO_DATE > UNKNOWN
            var allStatuses = new[] { dailyStatus, formsStatus };
            if (hasSyncStateError || Array.IndexOf(allStatuses, "SYNC_STATE_ERROR") >= 0)
            {
                dto.OverallStatus = "SYNC_STATE_ERROR";
            }
            else if (Array.IndexOf(allStatuses, "BOTH_CHANGED") >= 0)
            {
                dto.OverallStatus = "BOTH_CHANGED";
            }
            else if (Array.IndexOf(allStatuses, "REMOTE_NEWER") >= 0)
            {
                dto.OverallStatus = "REMOTE_NEWER";
            }
            else if (Array.IndexOf(allStatuses, "LOCAL_PENDING") >= 0)
            {
                dto.OverallStatus = "LOCAL_PENDING";
            }
            else if (Array.IndexOf(allStatuses, "NOT_BASELINED") >= 0)
            {
                dto.OverallStatus = "NOT_BASELINED";
            }
            else if (Array.TrueForAll(allStatuses, s => s == "UP_TO_DATE"))
            {
                dto.OverallStatus = "UP_TO_DATE";
            }
            else
            {
                dto.OverallStatus = "UNKNOWN";
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
