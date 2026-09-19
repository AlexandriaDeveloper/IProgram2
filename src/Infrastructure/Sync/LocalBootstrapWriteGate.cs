using System;
using System.Linq;
using Core.Exceptions;
using Core.Interfaces;

namespace Auth.Infrastructure.Sync
{
    public class LocalBootstrapWriteGate : ILocalBootstrapWriteGate
    {
        private readonly LocalSyncContext _localSyncContext;

        public LocalBootstrapWriteGate(LocalSyncContext localSyncContext)
        {
            _localSyncContext = localSyncContext;
        }

        public BootstrapReadinessResult EvaluateReadiness(string databaseId)
        {
            if (string.IsNullOrWhiteSpace(databaseId))
            {
                throw new InvalidDatabaseSelectionException("Canonical database ID is required to evaluate bootstrap readiness.");
            }

            var normalizedId = databaseId.Trim();
            if (normalizedId != "2026" && normalizedId != "2027")
            {
                throw new InvalidDatabaseSelectionException(
                    $"Unsupported canonical DatabaseId '{normalizedId}'. Expected '2026' or '2027'.");
            }

            if (_localSyncContext == null)
            {
                return new BootstrapReadinessResult
                {
                    DatabaseId = normalizedId,
                    Status = BootstrapReadinessStatus.Disabled,
                    IsWriteAllowed = false,
                    Message = "LocalSyncContext is not configured."
                };
            }

            var manifest = _localSyncContext.BootstrapManifests
                .Where(m => m.DatabaseId == normalizedId)
                .OrderByDescending(m => m.BootstrapTimestampUtc)
                .FirstOrDefault();

            if (manifest == null)
            {
                return new BootstrapReadinessResult
                {
                    DatabaseId = normalizedId,
                    Status = BootstrapReadinessStatus.ManifestMissing,
                    IsWriteAllowed = false,
                    Message = $"Bootstrap manifest is missing for database '{normalizedId}'. Local writes are blocked until Slice 4.2B bootstrap completes."
                };
            }

            var isVerified = string.Equals(manifest.Status, "VERIFIED_READY", StringComparison.OrdinalIgnoreCase) && manifest.IsWriteAllowed;

            if (!isVerified)
            {
                return new BootstrapReadinessResult
                {
                    DatabaseId = normalizedId,
                    Status = BootstrapReadinessStatus.Unverified,
                    IsWriteAllowed = false,
                    Message = $"Bootstrap manifest for database '{normalizedId}' has status '{manifest.Status}' (IsWriteAllowed={manifest.IsWriteAllowed}). Local writes are blocked.",
                    VerifiedAtUtc = manifest.BootstrapTimestampUtc
                };
            }

            return new BootstrapReadinessResult
            {
                DatabaseId = normalizedId,
                Status = BootstrapReadinessStatus.VerifiedReady,
                IsWriteAllowed = true,
                Message = $"Bootstrap verified and ready for database '{normalizedId}'. Local writes are permitted.",
                VerifiedAtUtc = manifest.BootstrapTimestampUtc
            };
        }

        public bool IsWriteAllowed(string databaseId)
        {
            return EvaluateReadiness(databaseId).IsWriteAllowed;
        }

        public void EnsureWriteAllowed(string databaseId)
        {
            var result = EvaluateReadiness(databaseId);
            if (!result.IsWriteAllowed)
            {
                throw new BootstrapNotVerifiedException(databaseId, result.Message);
            }
        }
    }
}
