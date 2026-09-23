#nullable enable
using System;
using System.Collections.Generic;

namespace Core.Models.Sync
{
    public class LocalSyncStatusDto
    {
        public string DatabaseId { get; set; } = string.Empty;
        public int PendingCount { get; set; }
        public int InProgressCount { get; set; }
        public int FailedCount { get; set; }
        public int TotalCount { get; set; }
        public long LastServerVersion { get; set; }
        public DateTime? LastSuccessfulPushUtc { get; set; }
        public DateTime? LastSuccessfulPullUtc { get; set; }
        public DateTime? LastSyncAttemptUtc { get; set; }
        public string? LastSyncError { get; set; }
        public string RuntimeMode { get; set; } = string.Empty;
        public bool IsReadOnly { get; set; }
        public bool IsLocalFirst { get; set; }
    }

    public class ScopeSyncStatusDto
    {
        public string Scope { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty; // UP_TO_DATE, REMOTE_NEWER, BOTH_CHANGED, UNKNOWN, SYNC_STATE_ERROR, NOT_BASELINED
        public bool IsBaselined { get; set; }
        public long LocalVersion { get; set; }
        public long ServerVersion { get; set; }
        public int PendingCount { get; set; }
        public string? Message { get; set; }
    }

    public class OnlineSyncStatusDto
    {
        public string DatabaseId { get; set; } = string.Empty;
        public bool IsOnline { get; set; }
        public string OverallStatus { get; set; } = string.Empty; // UP_TO_DATE, REMOTE_NEWER, BOTH_CHANGED, UNKNOWN, SYNC_STATE_ERROR, NOT_BASELINED
        public long ServerVersion { get; set; }
        public long LocalVersion { get; set; }
        public int TotalPendingCount { get; set; }
        public List<ScopeSyncStatusDto> Scopes { get; set; } = new();
        public DateTime CheckedAtUtc { get; set; }
        public string? ErrorMessage { get; set; }
    }
}
