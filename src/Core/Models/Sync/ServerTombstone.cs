#nullable enable
using System;

namespace Core.Models.Sync
{
    public class ServerTombstone
    {
        public string DatabaseId { get; set; } = string.Empty; // '2026' or '2027'
        public string EntityType { get; set; } = string.Empty; // e.g. 'EmployeeNetPays', 'EmployeeWatchLists', 'EmployeeBank'
        public Guid EntitySyncId { get; set; }
        public string? NaturalKey { get; set; } // e.g. EmployeeId string
        public long ServerVersion { get; set; }
        public DateTime DeletedAtUtc { get; set; } = DateTime.UtcNow;
    }
}
