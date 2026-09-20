using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Core.Exceptions;
using Core.Interfaces;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Auth.Infrastructure.Sync
{
    public class ConnectionAuditRecord
    {
        public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
        public string Database { get; set; }
        public string DataSource { get; set; }
        public bool IsLocal { get; set; }
        public bool Allowed { get; set; }
    }

    public static class ConnectionAuditTracker
    {
        private static readonly ConcurrentBag<ConnectionAuditRecord> Records = new ConcurrentBag<ConnectionAuditRecord>();

        public static void RecordConnection(string database, string dataSource, bool isLocal, bool allowed)
        {
            Records.Add(new ConnectionAuditRecord
            {
                Database = database,
                DataSource = dataSource,
                IsLocal = isLocal,
                Allowed = allowed
            });
        }

        public static IReadOnlyCollection<ConnectionAuditRecord> GetRecords()
        {
            return Records.ToArray();
        }

        public static void Clear()
        {
            while (Records.TryTake(out _)) { }
        }
    }

    /// <summary>
    /// EF Core DbConnectionInterceptor that guarantees zero outbound or non-local database connections
    /// can be opened when ReadOnlyMode is active, and provides in-memory connection auditing.
    /// </summary>
    public class ReadOnlyDbConnectionInterceptor : DbConnectionInterceptor
    {
        private readonly ISyncConnectionProvider _syncConnectionProvider;

        public ReadOnlyDbConnectionInterceptor(ISyncConnectionProvider syncConnectionProvider)
        {
            _syncConnectionProvider = syncConnectionProvider;
        }

        public static bool IsLocalServer(string dataSource)
        {
            if (string.IsNullOrWhiteSpace(dataSource)) return false;
            var ds = dataSource.Trim().ToLowerInvariant();

            return ds == "localhost" || ds == "127.0.0.1" || ds == "(local)" || ds == "." ||
                   ds.StartsWith("localhost\\") || ds.StartsWith("127.0.0.1\\") ||
                   ds.StartsWith("(local)\\") || ds.StartsWith(".\\") ||
                   ds.StartsWith("localhost,") || ds.StartsWith("127.0.0.1,");
        }

        public override InterceptionResult ConnectionOpening(
            DbConnection connection,
            ConnectionEventData eventData,
            InterceptionResult result)
        {
            EnforceLocalConnection(connection);
            return base.ConnectionOpening(connection, eventData, result);
        }

        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(
            DbConnection connection,
            ConnectionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            EnforceLocalConnection(connection);
            return base.ConnectionOpeningAsync(connection, eventData, result, cancellationToken);
        }

        private void EnforceLocalConnection(DbConnection connection)
        {
            if (_syncConnectionProvider == null || !_syncConnectionProvider.IsReadOnlyMode)
            {
                return;
            }

            if (connection == null)
            {
                return;
            }

            var dataSource = connection.DataSource ?? string.Empty;
            var database = connection.Database ?? string.Empty;
            bool isLocal = IsLocalServer(dataSource);

            // Record connection attempt in thread-safe audit tracker
            ConnectionAuditTracker.RecordConnection(database, dataSource, isLocal, allowed: isLocal);

            if (!isLocal)
            {
                throw new ReadOnlyModeException(
                    "الاتصال بقواعد بيانات خارجية معطل في وضع القراءة المحلية فقط. تم منع محاولة الاتصال.");
            }
        }
    }
}
