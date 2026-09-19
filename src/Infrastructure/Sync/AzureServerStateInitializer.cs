using System;
using Core.Models.Sync;
using Microsoft.EntityFrameworkCore;

namespace Auth.Infrastructure.Sync
{
    using AzureDatabaseBinding = Core.Models.Sync.AzureDatabaseBinding;

    /// <summary>
    /// Service responsible for safely initializing the canonical sync.ServerState row,
    /// strictly bound and physically verified against the target database.
    /// </summary>
    public static class AzureServerStateInitializer
    {
        public static void Initialize(AzureSyncContext context, AzureDatabaseBinding binding)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (binding == null) throw new ArgumentNullException(nameof(binding));

            // If running against a relational database, verify the actual physical database connection
            if (context.Database.IsRelational())
            {
                var connection = context.Database.GetDbConnection();
                var physicalDbName = connection?.Database;

                if (!string.IsNullOrEmpty(physicalDbName))
                {
                    if (!physicalDbName.Equals(binding.ExpectedDatabaseName, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            $"Physical database connection mismatch: target connection database is '{physicalDbName}', " +
                            $"which does not match the expected physical database '{binding.ExpectedDatabaseName}' for canonical DatabaseId '{binding.CanonicalDatabaseId}'.");
                    }
                }
            }

            var existing = context.ServerStates.Find(binding.CanonicalDatabaseId);
            if (existing == null)
            {
                context.ServerStates.Add(new ServerState
                {
                    DatabaseId = binding.CanonicalDatabaseId,
                    CurrentVersion = 0,
                    LastUpdatedUtc = DateTime.UtcNow
                });
                context.SaveChanges();
            }
        }
    }
}
