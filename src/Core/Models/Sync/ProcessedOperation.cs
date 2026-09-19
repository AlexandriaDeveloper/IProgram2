#nullable enable
using System;

namespace Core.Models.Sync
{
    public class ProcessedOperation
    {
        public string DatabaseId { get; set; } = string.Empty; // '2026' or '2027'
        public Guid ClientOperationId { get; set; }
        public Guid DeviceId { get; set; }
        public string EntityType { get; set; } = string.Empty;
        public Guid EntitySyncId { get; set; }
        public DateTime ProcessedAtUtc { get; set; } = DateTime.UtcNow;
        public string ResultStatus { get; set; } = "SUCCESS"; // 'SUCCESS', 'REJECTED'
        public string? ResponseJson { get; set; }
    }
}
