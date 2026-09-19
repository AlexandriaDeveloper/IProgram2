using System;
using Core.Exceptions;

namespace Core.Models.Sync
{
    /// <summary>
    /// Represents a verified, immutable binding between a canonical database identifier and an expected Azure physical database name.
    /// </summary>
    public sealed class AzureDatabaseBinding
    {
        public const string RemoteDb2026 = "IProgramDb2026";
        public const string RemoteDb2027 = "IProgramDb2027";

        public string CanonicalDatabaseId { get; }
        public string ExpectedDatabaseName { get; }

        public AzureDatabaseBinding(string canonicalDatabaseId, string expectedDatabaseName)
        {
            if (string.IsNullOrWhiteSpace(canonicalDatabaseId))
                throw new ArgumentException("CanonicalDatabaseId is required.", nameof(canonicalDatabaseId));
            if (string.IsNullOrWhiteSpace(expectedDatabaseName))
                throw new ArgumentException("ExpectedDatabaseName is required.", nameof(expectedDatabaseName));

            var normalizedId = canonicalDatabaseId.Trim();
            var normalizedExpectedName = expectedDatabaseName.Trim();

            // Strictly reject unsupported years
            if (normalizedId != "2026" && normalizedId != "2027")
            {
                throw new ArgumentException(
                    $"Unsupported canonical DatabaseId '{normalizedId}'. Expected '2026' or '2027'.", nameof(canonicalDatabaseId));
            }

            // Strictly reject local/remote confusion: remote Azure binding cannot point to local DB names
            if (normalizedExpectedName.Equals("IProgramLocalDb2026", StringComparison.OrdinalIgnoreCase) ||
                normalizedExpectedName.Equals("IProgramLocalDb2027", StringComparison.OrdinalIgnoreCase))
            {
                throw new PhysicalDatabaseMismatchException(
                    $"Local/Remote mismatch: AzureDatabaseBinding cannot be bound to local database '{normalizedExpectedName}'.");
            }

            // Strictly enforce valid canonical combinations
            if (normalizedId == "2026" && !normalizedExpectedName.Equals(RemoteDb2026, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Configuration mismatch: Canonical DatabaseId '2026' cannot be bound to database '{normalizedExpectedName}'. Expected '{RemoteDb2026}'.");
            }

            if (normalizedId == "2027" && !normalizedExpectedName.Equals(RemoteDb2027, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Configuration mismatch: Canonical DatabaseId '2027' cannot be bound to database '{normalizedExpectedName}'. Expected '{RemoteDb2027}'.");
            }

            CanonicalDatabaseId = normalizedId;
            ExpectedDatabaseName = normalizedExpectedName;
        }

        public static AzureDatabaseBinding For2026() => new AzureDatabaseBinding("2026", RemoteDb2026);
        public static AzureDatabaseBinding For2027() => new AzureDatabaseBinding("2027", RemoteDb2027);

        public static AzureDatabaseBinding For(string canonicalDatabaseId)
        {
            if (string.IsNullOrWhiteSpace(canonicalDatabaseId))
                throw new InvalidDatabaseSelectionException("Canonical database ID is required.");

            return canonicalDatabaseId.Trim() switch
            {
                "2026" => For2026(),
                "2027" => For2027(),
                _ => throw new InvalidDatabaseSelectionException($"Unsupported canonical DatabaseId '{canonicalDatabaseId}'. Expected '2026' or '2027'.")
            };
        }
    }
}
