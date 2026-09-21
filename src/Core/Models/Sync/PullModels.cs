#nullable enable
using System;
using System.Collections.Generic;

namespace Core.Models.Sync
{
    public class DailyAuthoritativeSnapshot
    {
        public Guid SyncId { get; set; }
        public string Name { get; set; } = string.Empty;
        public DateTime DailyDate { get; set; }
        public bool Closed { get; set; }
        public DateTime CreatedAt { get; set; }
        public string? CreatedBy { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public string? UpdatedBy { get; set; }
        public DateTime? DeactivatedAt { get; set; }
        public string? DeactivatedBy { get; set; }
        public bool IsActive { get; set; }
    }

    public enum PullCommandType
    {
        Upsert,
        Delete
    }

    public class PullCommand
    {
        public PullCommandType CommandType { get; set; }
        public Guid EntitySyncId { get; set; }
        public long TerminalServerVersion { get; set; }
        public DailyAuthoritativeSnapshot? Snapshot { get; set; }

        public static PullCommand CreateUpsert(DailyAuthoritativeSnapshot snapshot, long terminalVersion)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            return new PullCommand
            {
                CommandType = PullCommandType.Upsert,
                EntitySyncId = snapshot.SyncId,
                TerminalServerVersion = terminalVersion,
                Snapshot = snapshot
            };
        }

        public static PullCommand CreateDelete(Guid syncId, long terminalVersion)
        {
            if (syncId == Guid.Empty) throw new ArgumentException("SyncId cannot be empty.", nameof(syncId));
            return new PullCommand
            {
                CommandType = PullCommandType.Delete,
                EntitySyncId = syncId,
                TerminalServerVersion = terminalVersion,
                Snapshot = null
            };
        }
    }

    public class FencedPullBatch
    {
        public string DatabaseId { get; set; } = string.Empty;
        public long LowWatermark { get; set; }
        public long HighWatermark { get; set; }
        public bool IsNoOp { get; set; }
        public IReadOnlyList<PullCommand> Commands { get; set; } = Array.Empty<PullCommand>();

        public static FencedPullBatch CreateNoOp(string databaseId, long watermark)
        {
            return new FencedPullBatch
            {
                DatabaseId = databaseId,
                LowWatermark = watermark,
                HighWatermark = watermark,
                IsNoOp = true,
                Commands = Array.Empty<PullCommand>()
            };
        }
    }

    public class PullOperationResult
    {
        public Guid EntitySyncId { get; set; }
        public string OperationType { get; set; } = string.Empty;
        public string Status { get; set; } = "SUCCESS";
        public long TerminalServerVersion { get; set; }
    }

    public class PullResultDto
    {
        public string DatabaseId { get; set; } = string.Empty;
        public long PreviousWatermark { get; set; }
        public long FinalServerVersion { get; set; }
        public bool IsNoOp { get; set; }
        public int TotalProcessed { get; set; }
        public int Succeeded { get; set; }
        public int Failed { get; set; }
        public List<PullOperationResult> Operations { get; set; } = new List<PullOperationResult>();
        public string? Message { get; set; }
    }
}
