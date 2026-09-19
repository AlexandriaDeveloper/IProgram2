#nullable enable
using System;

namespace Core.Models.Sync
{
    public class LocalOutbox
    {
        public Guid ClientOperationId { get; set; }
        public string DatabaseId { get; set; } = string.Empty; // Canonical string e.g. '2026' or '2027'
        public string AggregateType { get; set; } = string.Empty; // e.g. 'Daily', 'Form', 'FormDetails', 'Employee'
        public string CommandName { get; set; } = string.Empty; // e.g. 'CreateFormCommand'
        public Guid EntitySyncId { get; set; }
        public string PayloadJson { get; set; } = string.Empty;
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public string Status { get; set; } = "PENDING"; // PENDING, IN_PROGRESS, COMPLETED, FAILED
        public int RetryCount { get; set; } = 0;
        public string? LastError { get; set; }
        public DateTime? CompletedAtUtc { get; set; }
        public DateTime? LockedUntilUtc { get; set; }
        public Guid? LockToken { get; set; }
    }
}
