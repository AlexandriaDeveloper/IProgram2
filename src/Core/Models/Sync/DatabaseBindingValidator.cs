using System;
using Core.Exceptions;

namespace Core.Models.Sync
{
    public static class DatabaseBindingValidator
    {
        public const string RemoteDb2026 = "IProgramDb2026";
        public const string RemoteDb2027 = "IProgramDb2027";
        public const string LocalDb2026 = "IProgramLocalDb2026";
        public const string LocalDb2027 = "IProgramLocalDb2027";

        public static string GetExpectedRemoteDatabaseName(string canonicalDatabaseId)
        {
            if (string.IsNullOrWhiteSpace(canonicalDatabaseId))
                throw new InvalidDatabaseSelectionException("Canonical database ID is required.");

            return canonicalDatabaseId.Trim() switch
            {
                "2026" => RemoteDb2026,
                "2027" => RemoteDb2027,
                _ => throw new InvalidDatabaseSelectionException($"Unsupported canonical DatabaseId '{canonicalDatabaseId}'. Expected '2026' or '2027'.")
            };
        }

        public static string GetExpectedLocalDatabaseName(string canonicalDatabaseId)
        {
            if (string.IsNullOrWhiteSpace(canonicalDatabaseId))
                throw new InvalidDatabaseSelectionException("Canonical database ID is required.");

            return canonicalDatabaseId.Trim() switch
            {
                "2026" => LocalDb2026,
                "2027" => LocalDb2027,
                _ => throw new InvalidDatabaseSelectionException($"Unsupported canonical DatabaseId '{canonicalDatabaseId}'. Expected '2026' or '2027'.")
            };
        }

        public static bool IsRemoteDatabaseName(string physicalDbName)
        {
            if (string.IsNullOrWhiteSpace(physicalDbName)) return false;
            return physicalDbName.Equals(RemoteDb2026, StringComparison.OrdinalIgnoreCase) ||
                   physicalDbName.Equals(RemoteDb2027, StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsLocalDatabaseName(string physicalDbName)
        {
            if (string.IsNullOrWhiteSpace(physicalDbName)) return false;
            return physicalDbName.Equals(LocalDb2026, StringComparison.OrdinalIgnoreCase) ||
                   physicalDbName.Equals(LocalDb2027, StringComparison.OrdinalIgnoreCase);
        }

        public static void ValidateTargetDatabase(string canonicalDatabaseId, string physicalDbName, bool isLocalTarget)
        {
            if (string.IsNullOrWhiteSpace(canonicalDatabaseId))
                throw new InvalidDatabaseSelectionException("Canonical database ID is required.");
            if (string.IsNullOrWhiteSpace(physicalDbName))
                throw new ArgumentException("Physical database name is required.", nameof(physicalDbName));

            var normId = canonicalDatabaseId.Trim();
            var normName = physicalDbName.Trim();

            if (normId != "2026" && normId != "2027")
            {
                throw new InvalidDatabaseSelectionException($"Unsupported canonical DatabaseId '{normId}'. Expected '2026' or '2027'.");
            }

            if (isLocalTarget)
            {
                // Local target must NOT be an Azure remote DB
                if (IsRemoteDatabaseName(normName))
                {
                    throw new PhysicalDatabaseMismatchException(
                        $"Security violation: Local target cannot use remote Azure database '{normName}'.");
                }

                var expectedLocal = GetExpectedLocalDatabaseName(normId);
                if (!normName.Equals(expectedLocal, StringComparison.OrdinalIgnoreCase))
                {
                    throw new PhysicalDatabaseMismatchException(
                        $"Physical database mismatch: Local target for '{normId}' must be '{expectedLocal}', but found '{normName}'.");
                }
            }
            else
            {
                // Remote target must NOT be a local DB
                if (IsLocalDatabaseName(normName))
                {
                    throw new PhysicalDatabaseMismatchException(
                        $"Security violation: Remote Azure target cannot use local database '{normName}'.");
                }

                var expectedRemote = GetExpectedRemoteDatabaseName(normId);
                if (!normName.Equals(expectedRemote, StringComparison.OrdinalIgnoreCase))
                {
                    throw new PhysicalDatabaseMismatchException(
                        $"Physical database mismatch: Remote Azure target for '{normId}' must be '{expectedRemote}', but found '{normName}'.");
                }
            }
        }
    }
}
