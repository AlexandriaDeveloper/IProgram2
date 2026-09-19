using System;

namespace Core.Models.Sync
{
    public class ServerChangeFeed
    {
        public long FeedId { get; set; }
        public long ServerVersion { get; set; }
        public string DatabaseId { get; set; } = string.Empty; // '2026' or '2027'
        public string EntityType { get; set; } = string.Empty;
        public Guid EntitySyncId { get; set; }
        public string OperationType { get; set; } = string.Empty; // INSERT, UPDATE, SOFT_DELETE, HARD_DELETE
        public Guid OriginDeviceId { get; set; }
        public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    }
}
