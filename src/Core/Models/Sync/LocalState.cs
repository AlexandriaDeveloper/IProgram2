#nullable enable
using System;

namespace Core.Models.Sync
{
    public class LocalState
    {
        public string DatabaseId { get; set; } = string.Empty; // Canonical string e.g. '2026' or '2027'
        public Guid DeviceId { get; set; }
        public string DeviceName { get; set; } = string.Empty; // Human-friendly name e.g. 'Home-PC'
        public DateTime? LastSuccessfulPushUtc { get; set; }
        public DateTime? LastSuccessfulPullUtc { get; set; }
        public long LastServerVersion { get; set; } = 0;
        public Guid? ActiveLeaseToken { get; set; }
        public DateTime? LeaseExpiresAtUtc { get; set; }
        public string? LastSyncError { get; set; }
        public DateTime? LastSyncAttemptUtc { get; set; }
    }
}
