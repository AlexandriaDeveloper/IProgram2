#nullable enable
using System;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Core.Exceptions;
using Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Auth.Infrastructure.Sync
{
    public class LocalScopeBaselineService : ILocalScopeBaselineService
    {
        private readonly ILogger<LocalScopeBaselineService> _logger;

        public LocalScopeBaselineService(ILogger<LocalScopeBaselineService> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<SyncScopeBaselineStatus> GetScopeStatusAsync(
            DbConnection connection,
            DbTransaction? transaction,
            string databaseId,
            string scope,
            CancellationToken cancellationToken)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (string.IsNullOrWhiteSpace(databaseId)) throw new ArgumentException("DatabaseId cannot be empty.", nameof(databaseId));
            if (string.IsNullOrWhiteSpace(scope)) throw new ArgumentException("Scope cannot be empty.", nameof(scope));

            var normDbId = databaseId.Trim();
            var normScope = scope.Trim();

            await using var cmd = connection.CreateCommand();
            if (transaction != null) cmd.Transaction = transaction;

            cmd.CommandText = @"
                IF OBJECT_ID(N'[sync].[ScopeBaseline]', N'U') IS NOT NULL
                BEGIN
                    SELECT [Status]
                    FROM [sync].[ScopeBaseline]
                    WHERE [DatabaseId] = @DatabaseId AND [Scope] = @Scope;
                END
                ELSE
                BEGIN
                    SELECT NULL;
                END";

            AddParam(cmd, "@DatabaseId", normDbId);
            AddParam(cmd, "@Scope", normScope);

            var scalar = await cmd.ExecuteScalarAsync(cancellationToken);
            if (scalar != null && scalar != DBNull.Value)
            {
                var statusStr = scalar.ToString();
                if (string.Equals(statusStr, "BASELINED", StringComparison.OrdinalIgnoreCase))
                {
                    return SyncScopeBaselineStatus.Baselined;
                }
                if (string.Equals(statusStr, "PENDING", StringComparison.OrdinalIgnoreCase))
                {
                    return SyncScopeBaselineStatus.Pending;
                }
                return SyncScopeBaselineStatus.NotBaselined;
            }

            // Fallback for Daily: if unrecorded in ScopeBaseline, Daily defaults to Baselined
            // as established in initial clone/rehearsal if LocalState exists.
            if (string.Equals(normScope, "Daily", StringComparison.OrdinalIgnoreCase))
            {
                await using var checkStateCmd = connection.CreateCommand();
                if (transaction != null) checkStateCmd.Transaction = transaction;
                checkStateCmd.CommandText = @"
                    IF OBJECT_ID(N'[sync].[LocalState]', N'U') IS NOT NULL
                    BEGIN
                        SELECT COUNT(1) FROM [sync].[LocalState] WHERE [DatabaseId] = @DatabaseId;
                    END
                    ELSE
                    BEGIN
                        SELECT 0;
                    END";
                AddParam(checkStateCmd, "@DatabaseId", normDbId);
                var stateCount = Convert.ToInt32(await checkStateCmd.ExecuteScalarAsync(cancellationToken));
                if (stateCount > 0)
                {
                    return SyncScopeBaselineStatus.Baselined;
                }
            }

            // All other scopes (including Forms) strictly default to NotBaselined (fail closed)
            return SyncScopeBaselineStatus.NotBaselined;
        }

        public async Task EnsureScopeBaselinedAsync(
            DbConnection connection,
            DbTransaction? transaction,
            string databaseId,
            string scope,
            CancellationToken cancellationToken)
        {
            var status = await GetScopeStatusAsync(connection, transaction, databaseId, scope, cancellationToken);
            if (status != SyncScopeBaselineStatus.Baselined)
            {
                _logger.LogWarning(
                    "Scope baseline check failed for DatabaseId='{DatabaseId}', Scope='{Scope}'. Current status: {Status}",
                    databaseId, scope, status);

                if (string.Equals(scope, "Forms", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(scope, "Form", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(scope, "FormDetails", StringComparison.OrdinalIgnoreCase))
                {
                    throw new FormsScopeNotBaselinedException(
                        $"نطاق النماذج ({scope}) غير مؤصل محلياً لقاعدة البيانات '{databaseId}' (الحالة: {status}). العمليات المحلية على النماذج معطلة لحين إتمام التأصيل المعتمد.");
                }

                throw new SyncScopeNotBaselinedException(
                    $"نطاق المزامنة ({scope}) غير مؤصل لقاعدة البيانات '{databaseId}' (الحالة: {status}).");
            }
        }

        public async Task SetScopeStatusAsync(
            DbConnection connection,
            DbTransaction? transaction,
            string databaseId,
            string scope,
            SyncScopeBaselineStatus status,
            long? baselineVersion,
            string? notes,
            CancellationToken cancellationToken)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (string.IsNullOrWhiteSpace(databaseId)) throw new ArgumentException("DatabaseId cannot be empty.", nameof(databaseId));
            if (string.IsNullOrWhiteSpace(scope)) throw new ArgumentException("Scope cannot be empty.", nameof(scope));

            var normDbId = databaseId.Trim();
            var normScope = scope.Trim();
            var statusStr = status switch
            {
                SyncScopeBaselineStatus.Baselined => "BASELINED",
                SyncScopeBaselineStatus.Pending => "PENDING",
                _ => "NOT_BASELINED"
            };

            await using var cmd = connection.CreateCommand();
            if (transaction != null) cmd.Transaction = transaction;

            cmd.CommandText = @"
                IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'sync')
                    EXEC('CREATE SCHEMA [sync]');

                IF OBJECT_ID(N'[sync].[ScopeBaseline]', N'U') IS NULL
                BEGIN
                    CREATE TABLE [sync].[ScopeBaseline] (
                        [DatabaseId] VARCHAR(50) NOT NULL,
                        [Scope] VARCHAR(50) NOT NULL,
                        [Status] VARCHAR(50) NOT NULL,
                        [BaselinedAtUtc] DATETIMEOFFSET NULL,
                        [BaselineVersion] BIGINT NULL,
                        [Notes] NVARCHAR(255) NULL,
                        CONSTRAINT [PK_ScopeBaseline] PRIMARY KEY CLUSTERED ([DatabaseId], [Scope])
                    );
                END;

                IF EXISTS (SELECT 1 FROM [sync].[ScopeBaseline] WHERE [DatabaseId] = @DatabaseId AND [Scope] = @Scope)
                BEGIN
                    UPDATE [sync].[ScopeBaseline]
                    SET [Status] = @Status,
                        [BaselinedAtUtc] = @BaselinedAtUtc,
                        [BaselineVersion] = @BaselineVersion,
                        [Notes] = @Notes
                    WHERE [DatabaseId] = @DatabaseId AND [Scope] = @Scope;
                END
                ELSE
                BEGIN
                    INSERT INTO [sync].[ScopeBaseline] ([DatabaseId], [Scope], [Status], [BaselinedAtUtc], [BaselineVersion], [Notes])
                    VALUES (@DatabaseId, @Scope, @Status, @BaselinedAtUtc, @BaselineVersion, @Notes);
                END;";

            AddParam(cmd, "@DatabaseId", normDbId);
            AddParam(cmd, "@Scope", normScope);
            AddParam(cmd, "@Status", statusStr);
            AddParam(cmd, "@BaselinedAtUtc", status == SyncScopeBaselineStatus.Baselined ? DateTimeOffset.UtcNow : (object?)null);
            AddParam(cmd, "@BaselineVersion", baselineVersion);
            AddParam(cmd, "@Notes", notes);

            await cmd.ExecuteNonQueryAsync(cancellationToken);

            _logger.LogInformation(
                "Updated ScopeBaseline: DatabaseId='{DatabaseId}', Scope='{Scope}', Status='{Status}', Version='{Version}'.",
                normDbId, normScope, statusStr, baselineVersion);
        }

        private static void AddParam(DbCommand cmd, string name, object? value)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }
    }
}
