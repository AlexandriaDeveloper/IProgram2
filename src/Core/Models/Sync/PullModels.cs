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

    public class FormAuthoritativeSnapshot
    {
        public Guid SyncId { get; set; }
        public Guid? DailySyncId { get; set; }
        public int? Index { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public DateTime CreatedAt { get; set; }
        public string? CreatedBy { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public string? UpdatedBy { get; set; }
        public DateTime? DeactivatedAt { get; set; }
        public string? DeactivatedBy { get; set; }
        public bool IsActive { get; set; }
    }

    public class FormDetailsAuthoritativeSnapshot
    {
        public Guid SyncId { get; set; }
        public Guid FormSyncId { get; set; }
        public string? EmployeeId { get; set; }
        public double Amount { get; set; }
        public int OrderNum { get; set; }
        public bool IsReviewed { get; set; }
        public string? IsReviewedBy { get; set; }
        public DateTime? ReviewedAt { get; set; }
        public string? ReviewComments { get; set; }
        public bool IsSummaryReviewed { get; set; }
        public string? IsSummaryReviewedBy { get; set; }
        public DateTime? SummaryReviewedAt { get; set; }
        public string? SummaryComments { get; set; }
        public string? SummaryReviewMethod { get; set; }
        public DateTime CreatedAt { get; set; }
        public string? CreatedBy { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public string? UpdatedBy { get; set; }
        public DateTime? DeactivatedAt { get; set; }
        public string? DeactivatedBy { get; set; }
        public bool IsActive { get; set; }
    }

    public class FormRefernceAuthoritativeSnapshot
    {
        public Guid SyncId { get; set; }
        public Guid FormSyncId { get; set; }
        public string? ReferencePath { get; set; }
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
        public string EntityType { get; set; } = "Daily";
        public Guid EntitySyncId { get; set; }
        public long TerminalServerVersion { get; set; }
        public DailyAuthoritativeSnapshot? Snapshot { get; set; }
        public FormAuthoritativeSnapshot? FormSnapshot { get; set; }
        public FormDetailsAuthoritativeSnapshot? FormDetailsSnapshot { get; set; }
        public FormRefernceAuthoritativeSnapshot? FormRefernceSnapshot { get; set; }

        public static PullCommand CreateUpsert(DailyAuthoritativeSnapshot snapshot, long terminalVersion)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            return new PullCommand
            {
                CommandType = PullCommandType.Upsert,
                EntityType = "Daily",
                EntitySyncId = snapshot.SyncId,
                TerminalServerVersion = terminalVersion,
                Snapshot = snapshot
            };
        }

        public static PullCommand CreateFormUpsert(FormAuthoritativeSnapshot snapshot, long terminalVersion)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            return new PullCommand
            {
                CommandType = PullCommandType.Upsert,
                EntityType = "Form",
                EntitySyncId = snapshot.SyncId,
                TerminalServerVersion = terminalVersion,
                FormSnapshot = snapshot
            };
        }

        public static PullCommand CreateFormDetailsUpsert(FormDetailsAuthoritativeSnapshot snapshot, long terminalVersion)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            return new PullCommand
            {
                CommandType = PullCommandType.Upsert,
                EntityType = "FormDetails",
                EntitySyncId = snapshot.SyncId,
                TerminalServerVersion = terminalVersion,
                FormDetailsSnapshot = snapshot
            };
        }

        public static PullCommand CreateFormRefernceUpsert(FormRefernceAuthoritativeSnapshot snapshot, long terminalVersion)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            return new PullCommand
            {
                CommandType = PullCommandType.Upsert,
                EntityType = "FormRefernce",
                EntitySyncId = snapshot.SyncId,
                TerminalServerVersion = terminalVersion,
                FormRefernceSnapshot = snapshot
            };
        }

        public static PullCommand CreateDelete(Guid syncId, long terminalVersion)
        {
            return CreateDelete("Daily", syncId, terminalVersion);
        }

        public static PullCommand CreateDelete(string entityType, Guid syncId, long terminalVersion)
        {
            if (syncId == Guid.Empty) throw new ArgumentException("SyncId cannot be empty.", nameof(syncId));
            if (string.IsNullOrWhiteSpace(entityType)) throw new ArgumentException("EntityType cannot be empty.", nameof(entityType));
            return new PullCommand
            {
                CommandType = PullCommandType.Delete,
                EntityType = entityType,
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
