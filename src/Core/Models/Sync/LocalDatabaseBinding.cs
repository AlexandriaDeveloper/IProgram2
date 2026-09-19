using System;
using Core.Exceptions;

namespace Core.Models.Sync
{
    /// <summary>
    /// Represents a verified, immutable binding between a canonical database identifier and an expected local physical database name.
    /// </summary>
    public sealed class LocalDatabaseBinding
    {
        public const string LocalDb2026 = "IProgramLocalDb2026";
        public const string LocalDb2027 = "IProgramLocalDb2027";

        public string CanonicalDatabaseId { get; }
        public string ExpectedDatabaseName { get; }

        public LocalDatabaseBinding(string canonicalDatabaseId, string expectedDatabaseName)
        {
            if (string.IsNullOrWhiteSpace(canonicalDatabaseId))
                throw new ArgumentException("CanonicalDatabaseId is required.", nameof(canonicalDatabaseId));
            if (string.IsNullOrWhiteSpace(expectedDatabaseName))
                throw new ArgumentException("ExpectedDatabaseName is required.", nameof(expectedDatabaseName));

            var normalizedId = canonicalDatabaseId.Trim();
            var normalizedExpectedName = expectedDatabaseName.Trim();

            // Strictly reject unsupported years (fail-closed)
            if (normalizedId != "2026" && normalizedId != "2027")
            {
                throw new InvalidDatabaseSelectionException(
                    $"Unsupported canonical DatabaseId '{normalizedId}'. Expected '2026' or '2027'.");
            }

            // Strictly reject local/remote confusion: local binding cannot point to remote Azure DB names
            if (normalizedExpectedName.Equals("IProgramDb2026", StringComparison.OrdinalIgnoreCase) ||
                normalizedExpectedName.Equals("IProgramDb2027", StringComparison.OrdinalIgnoreCase))
            {
                throw new PhysicalDatabaseMismatchException(
                    $"Local/Remote mismatch: LocalDatabaseBinding cannot be bound to Azure remote database '{normalizedExpectedName}'.");
            }

            // Strictly enforce canonical local database names
            if (normalizedId == "2026" && !normalizedExpectedName.Equals(LocalDb2026, StringComparison.OrdinalIgnoreCase))
            {
                throw new PhysicalDatabaseMismatchException(
                    $"Configuration mismatch: Canonical DatabaseId '2026' cannot be bound to local database '{normalizedExpectedName}'. Expected '{LocalDb2026}'.");
            }

            if (normalizedId == "2027" && !normalizedExpectedName.Equals(LocalDb2027, StringComparison.OrdinalIgnoreCase))
            {
                throw new PhysicalDatabaseMismatchException(
                    $"Configuration mismatch: Canonical DatabaseId '2027' cannot be bound to local database '{normalizedExpectedName}'. Expected '{LocalDb2027}'.");
            }

            CanonicalDatabaseId = normalizedId;
            ExpectedDatabaseName = normalizedExpectedName;
        }

        public static LocalDatabaseBinding For2026() => new LocalDatabaseBinding("2026", LocalDb2026);
        public static LocalDatabaseBinding For2027() => new LocalDatabaseBinding("2027", LocalDb2027);

        public static LocalDatabaseBinding For(string canonicalDatabaseId)
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
