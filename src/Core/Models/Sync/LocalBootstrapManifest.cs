using System;

namespace Core.Models.Sync
{
    public class LocalBootstrapManifest
    {
        public int Id { get; set; }
        public string DatabaseId { get; set; } = string.Empty;
        public DateTime BootstrapTimestampUtc { get; set; } = DateTime.UtcNow;
        public string AzureServerSource { get; set; } = string.Empty;
        public string TargetLocalEngine { get; set; } = string.Empty;
        public string MigrationHistoryHash { get; set; } = string.Empty;
        public string TableCheckJson { get; set; } = string.Empty;
        public string IdentityCheckJson { get; set; } = string.Empty;
        public string Status { get; set; } = "VERIFIED_READY"; // VERIFIED_READY, FAILED_MISMATCH
        public bool IsWriteAllowed { get; set; } = false;
    }
}
