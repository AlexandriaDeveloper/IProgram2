using System;

namespace Core.Interfaces
{
    public enum BootstrapReadinessStatus
    {
        Disabled,
        ManifestMissing,
        Unverified,
        VerifiedReady
    }

    public class BootstrapReadinessResult
    {
        public string DatabaseId { get; set; } = string.Empty;
        public BootstrapReadinessStatus Status { get; set; }
        public bool IsWriteAllowed { get; set; }
        public string Message { get; set; } = string.Empty;
        public DateTime? VerifiedAtUtc { get; set; }
    }

    public interface ILocalBootstrapWriteGate
    {
        BootstrapReadinessResult EvaluateReadiness(string databaseId);
        bool IsWriteAllowed(string databaseId);
        void EnsureWriteAllowed(string databaseId);
    }
}
