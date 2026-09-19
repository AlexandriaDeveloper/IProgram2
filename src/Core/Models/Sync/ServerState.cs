using System;

namespace Core.Models.Sync
{
    public class ServerState
    {
        public string DatabaseId { get; set; } = string.Empty; // Canonical string e.g. '2026' or '2027'
        public long CurrentVersion { get; set; } = 0;
        public DateTime LastUpdatedUtc { get; set; } = DateTime.UtcNow;
    }
}
