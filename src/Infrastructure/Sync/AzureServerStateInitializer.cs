using System;
using Core.Models.Sync;
using Microsoft.EntityFrameworkCore;

namespace Auth.Infrastructure.Sync
{
    /// <summary>
    /// Represents a verified, immutable binding between a canonical database identifier and an expected physical database name.
    /// </summary>
    public sealed class AzureDatabaseBinding
    {
        public string CanonicalDatabaseId { get; }
        public string ExpectedDatabaseName { get; }

        public AzureDatabaseBinding(string canonicalDatabaseId, string expectedDatabaseName)
        {
            if (string.IsNullOrWhiteSpace(canonicalDatabaseId))
                throw new ArgumentException("CanonicalDatabaseId is required.", nameof(canonicalDatabaseId));
            if (string.IsNullOrWhiteSpace(expectedDatabaseName))
                throw new ArgumentException("ExpectedDatabaseName is required.", nameof(expectedDatabaseName));

            // Strictly enforce valid canonical combinations
            if (canonicalDatabaseId == "2026" && !expectedDatabaseName.Equals("IProgramDb2026", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Configuration mismatch: Canonical DatabaseId '2026' cannot be bound to database '{expectedDatabaseName}'. Expected 'IProgramDb2026'.");
            }

            if (canonicalDatabaseId == "2027" && !expectedDatabaseName.Equals("IProgramDb2027", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Configuration mismatch: Canonical DatabaseId '2027' cannot be bound to database '{expectedDatabaseName}'. Expected 'IProgramDb2027'.");
            }

            if (canonicalDatabaseId != "2026" && canonicalDatabaseId != "2027")
            {
                throw new ArgumentException(
                    $"Unsupported canonical DatabaseId '{canonicalDatabaseId}'. Expected '2026' or '2027'.", nameof(canonicalDatabaseId));
            }

            CanonicalDatabaseId = canonicalDatabaseId;
            ExpectedDatabaseName = expectedDatabaseName;
        }

        public static AzureDatabaseBinding For2026() => new AzureDatabaseBinding("2026", "IProgramDb2026");
        public static AzureDatabaseBinding For2027() => new AzureDatabaseBinding("2027", "IProgramDb2027");
    }

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
