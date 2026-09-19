using System;
using Core.Exceptions;
using Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Auth.Infrastructure.Sync
{
    public interface ILocalSyncContextFactory
    {
        LocalSyncContext Create(string databaseId);
    }

    public class LocalSyncContextFactory : ILocalSyncContextFactory
    {
        private readonly ISyncConnectionProvider _syncConnectionProvider;

        public LocalSyncContextFactory(ISyncConnectionProvider syncConnectionProvider)
        {
            _syncConnectionProvider = syncConnectionProvider ?? throw new ArgumentNullException(nameof(syncConnectionProvider));
        }

        public LocalSyncContext Create(string databaseId)
        {
            if (string.IsNullOrWhiteSpace(databaseId))
            {
                throw new InvalidDatabaseSelectionException("Canonical database ID is required to create LocalSyncContext.");
            }

            var normalizedId = databaseId.Trim();
            if (normalizedId != "2026" && normalizedId != "2027")
            {
                throw new InvalidDatabaseSelectionException(
                    $"Unsupported canonical DatabaseId '{normalizedId}'. Expected '2026' or '2027'.");
            }

            // Explicit year resolution independent of any ambient HttpContext
            var localConnStr = _syncConnectionProvider.GetLocalConnectionString(normalizedId);

            var optionsBuilder = new DbContextOptionsBuilder<LocalSyncContext>();
            optionsBuilder.UseSqlServer(localConnStr, o =>
            {
                o.UseCompatibilityLevel(120);
                o.MigrationsHistoryTable(
                    LocalSyncContext.MigrationsHistoryTableName,
                    LocalSyncContext.MigrationsHistoryTableSchema);
            });

            return new LocalSyncContext(optionsBuilder.Options);
        }
    }
}
