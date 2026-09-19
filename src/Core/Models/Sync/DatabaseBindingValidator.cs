#nullable enable
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

        public static bool IsLocalServerEndpoint(string? serverOrDataSource)
        {
            if (string.IsNullOrWhiteSpace(serverOrDataSource)) return false;

            var endpoint = serverOrDataSource.Trim();

            // Strip protocol prefixes (e.g. "tcp:", "np:", "lpc:")
            if (endpoint.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase))
                endpoint = endpoint.Substring(4).Trim();
            else if (endpoint.StartsWith("np:", StringComparison.OrdinalIgnoreCase))
                endpoint = endpoint.Substring(3).Trim();
            else if (endpoint.StartsWith("lpc:", StringComparison.OrdinalIgnoreCase))
                endpoint = endpoint.Substring(4).Trim();

            // Check exact IPv6 localhost forms before any delimiter stripping
            if (endpoint.Equals("::1", StringComparison.OrdinalIgnoreCase) ||
                endpoint.Equals("[::1]", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // If IPv6 bracket notation e.g. [::1],1433 or [::1]:1433
            if (endpoint.StartsWith("["))
            {
                var closeBracket = endpoint.IndexOf(']');
                if (closeBracket > 0)
                {
                    var bracketHost = endpoint.Substring(0, closeBracket + 1);
                    return bracketHost.Equals("[::1]", StringComparison.OrdinalIgnoreCase);
                }
            }

            // Strip port suffix via comma (standard SQL Server format e.g. "localhost,1433")
            var commaIdx = endpoint.IndexOf(',');
            if (commaIdx >= 0)
            {
                endpoint = endpoint.Substring(0, commaIdx).Trim();
            }

            // Strip port suffix via colon only if not IPv6 (i.e. at most one colon)
            var firstColon = endpoint.IndexOf(':');
            if (firstColon >= 0 && endpoint.IndexOf(':', firstColon + 1) < 0)
            {
                endpoint = endpoint.Substring(0, firstColon).Trim();
            }

            // Extract host part if named instance is used (e.g. "localhost\SQLEXPRESS" -> "localhost")
            var slashIdx = endpoint.IndexOf('\\');
            var hostPart = (slashIdx >= 0) ? endpoint.Substring(0, slashIdx).Trim() : endpoint;

            if (hostPart.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                hostPart.Equals(".", StringComparison.OrdinalIgnoreCase) ||
                hostPart.Equals("(local)", StringComparison.OrdinalIgnoreCase) ||
                hostPart.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                hostPart.Equals("::1", StringComparison.OrdinalIgnoreCase) ||
                hostPart.Equals("[::1]", StringComparison.OrdinalIgnoreCase) ||
                hostPart.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase) ||
                hostPart.Equals("(localdb)", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        public static bool IsRemoteServerEndpoint(string? serverOrDataSource)
        {
            if (string.IsNullOrWhiteSpace(serverOrDataSource)) return false;
            return !IsLocalServerEndpoint(serverOrDataSource);
        }

        public static void ValidateAzureBinding(string? serverOrDataSource, string? physicalDbName)
        {
            if (string.IsNullOrWhiteSpace(physicalDbName))
            {
                throw new InvalidOperationException("Security violation: AzureSyncContext requires a configured database name.");
            }

            if (IsLocalDatabaseName(physicalDbName))
            {
                throw new InvalidOperationException(
                    $"Security violation: AzureSyncContext cannot target local database '{physicalDbName}'. AzureSyncContext is strictly for remote Azure databases.");
            }

            if (!IsRemoteDatabaseName(physicalDbName))
            {
                throw new InvalidOperationException(
                    $"Security violation: AzureSyncContext requires an approved remote database name ('{RemoteDb2026}' or '{RemoteDb2027}'), but found '{physicalDbName}'.");
            }

            if (IsLocalServerEndpoint(serverOrDataSource))
            {
                throw new InvalidOperationException(
                    $"Security violation: AzureSyncContext cannot target local server endpoint '{serverOrDataSource}'. AzureSyncContext is strictly for remote Azure endpoints.");
            }

            if (string.IsNullOrWhiteSpace(serverOrDataSource) || !IsRemoteServerEndpoint(serverOrDataSource))
            {
                throw new InvalidOperationException(
                    $"Security violation: AzureSyncContext requires a valid remote server endpoint, but found '{serverOrDataSource}'.");
            }
        }

        public static void ValidateLocalBinding(string? serverOrDataSource, string? physicalDbName)
        {
            if (string.IsNullOrWhiteSpace(physicalDbName))
            {
                throw new InvalidOperationException("Security violation: LocalSyncContext requires a configured database name.");
            }

            if (IsRemoteDatabaseName(physicalDbName))
            {
                throw new InvalidOperationException(
                    $"Security violation: LocalSyncContext cannot target remote Azure production database '{physicalDbName}'. LocalSyncContext is strictly local-only.");
            }

            if (!IsLocalDatabaseName(physicalDbName))
            {
                throw new InvalidOperationException(
                    $"Security violation: LocalSyncContext requires an approved local database name ('{LocalDb2026}' or '{LocalDb2027}'), but found '{physicalDbName}'.");
            }

            if (!IsLocalServerEndpoint(serverOrDataSource))
            {
                throw new InvalidOperationException(
                    $"Security violation: LocalSyncContext requires a trusted local server endpoint, but found '{serverOrDataSource}'. LocalSyncContext is strictly local-only.");
            }
        }
    }
}
