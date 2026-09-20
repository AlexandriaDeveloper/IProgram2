#nullable enable
using System;
using Core.Exceptions;
using Core.Interfaces;
using Core.Models.Sync;

namespace Auth.Infrastructure.Sync.Authoritative
{
    /// <summary>
    /// Production implementation of IAuthoritativeDatabaseBindingGuard enforcing
    /// physical Azure SQL (*.database.windows.net) connectivity and database mapping.
    /// </summary>
    public class AuthoritativeDatabaseBindingGuard : IAuthoritativeDatabaseBindingGuard
    {
        public void ValidateAuthoritativeAzureBinding(string canonicalDatabaseId, string? serverOrDataSource, string? physicalDbName)
        {
            if (string.IsNullOrWhiteSpace(canonicalDatabaseId))
            {
                throw new AuthoritativeBindingException("Canonical database ID is required for authoritative Azure binding.");
            }

            if (string.IsNullOrWhiteSpace(physicalDbName))
            {
                throw new AuthoritativeBindingException("Physical database name is required for authoritative Azure binding.");
            }

            try
            {
                // 1. Validate canonical ID vs physical remote database name (IProgramDb2026 or IProgramDb2027)
                DatabaseBindingValidator.ValidateTargetDatabase(canonicalDatabaseId, physicalDbName, isLocalTarget: false);

                // 2. Validate remote server endpoint (*.database.windows.net, non-local)
                DatabaseBindingValidator.ValidateAzureBinding(serverOrDataSource, physicalDbName);
            }
            catch (Exception ex) when (ex is not AuthoritativeBindingException)
            {
                throw new AuthoritativeBindingException(
                    $"Authoritative Azure binding security violation for DatabaseId '{canonicalDatabaseId}': {ex.Message}", ex);
            }
        }
    }
}
