#nullable enable
using System;
using System.Collections.Generic;

namespace Core.Models.Sync
{
    public class PushBatchResult
    {
        public string DatabaseId { get; set; } = string.Empty;
        public Guid LeaseToken { get; set; }
        public int TotalProcessed { get; set; }
        public int Succeeded { get; set; }
        public int Failed { get; set; }
        public long FinalServerVersion { get; set; }
        public List<OperationPushResult> Operations { get; set; } = new();
    }

    public class OperationPushResult
    {
        public Guid ClientOperationId { get; set; }
        public Guid EntitySyncId { get; set; }
        public string OperationType { get; set; } = string.Empty;
        public string Status { get; set; } = "SUCCESS"; // SUCCESS, FAILED, REPLAY
        public long ServerVersion { get; set; }
        public string? ErrorCode { get; set; }
        public string? ErrorMessage { get; set; }
    }
}
