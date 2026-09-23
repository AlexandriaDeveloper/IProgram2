#nullable enable
using System;

namespace Core.Exceptions
{
    public abstract class SyncDomainException : Exception
    {
        public abstract string ErrorCode { get; }

        protected SyncDomainException(string message) : base(message) { }
        protected SyncDomainException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class SyncPushDisabledException : SyncDomainException
    {
        public override string ErrorCode => "SYNC_PUSH_DISABLED";
        public SyncPushDisabledException(string message = "مزامنة الرفع (Push) معطلة حالياً.") : base(message) { }
    }

    public class SyncPushAlreadyRunningException : SyncDomainException
    {
        public override string ErrorCode => "SYNC_PUSH_ALREADY_RUNNING";
        public SyncPushAlreadyRunningException(string message = "توجد جلسة مزامنة رفع قيد التشغيل بالفعل لنفس قاعدة البيانات.") : base(message) { }
    }

    public class SyncOperationIdReuseException : SyncDomainException
    {
        public override string ErrorCode => "SYNC_OPERATION_ID_REUSE";
        public SyncOperationIdReuseException(string message = "تم رفض العملية: محاولة إعادة استخدام ClientOperationId بمحتوى مختلف.") : base(message) { }
    }

    public class SyncVersionConflictException : SyncDomainException
    {
        public override string ErrorCode => "SYNC_VERSION_CONFLICT";
        public long ExpectedVersion { get; }
        public long CurrentServerVersion { get; }

        public SyncVersionConflictException(long expectedVersion, long currentServerVersion, string message = "تعارض في إصدار الخادم: لم يتطابق الإصدار المتوقع مع إصدار الخادم الحالي.")
            : base(message)
        {
            ExpectedVersion = expectedVersion;
            CurrentServerVersion = currentServerVersion;
        }
    }

    public class SyncEntityAlreadyExistsException : SyncDomainException
    {
        public override string ErrorCode => "SYNC_ENTITY_ALREADY_EXISTS";
        public SyncEntityAlreadyExistsException(string message = "الكيان موجود بالفعل في الخادم بنفس SyncId.") : base(message) { }
    }

    public class SyncEntityNotFoundException : SyncDomainException
    {
        public override string ErrorCode => "SYNC_ENTITY_NOT_FOUND";
        public SyncEntityNotFoundException(string message = "الكيان المطلوب تعديله أو حذفه غير موجود في الخادم.") : base(message) { }
    }

    public class SyncPayloadValidationException : SyncDomainException
    {
        public override string ErrorCode => "SYNC_PAYLOAD_INVALID";
        public SyncPayloadValidationException(string message) : base(message) { }
        public SyncPayloadValidationException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class SyncMetadataMismatchException : SyncDomainException
    {
        public override string ErrorCode => "SYNC_METADATA_MISMATCH";
        public SyncMetadataMismatchException(string message) : base(message) { }
    }

    public class SyncLocalStateMissingException : SyncDomainException
    {
        public override string ErrorCode => "SYNC_LOCAL_STATE_MISSING";
        public SyncLocalStateMissingException(string message) : base(message) { }
    }

    public class SyncLeaseExpiredException : SyncDomainException
    {
        public override string ErrorCode => "SYNC_LEASE_EXPIRED";
        public SyncLeaseExpiredException(string message = "انتهت صلاحية الـ Lease أو تم الاستحواذ عليها من جلسة أخرى.") : base(message) { }
    }

    public class SyncCorruptResponseJsonException : SyncDomainException
    {
        public override string ErrorCode => "SYNC_CORRUPT_RESPONSE_JSON";
        public SyncCorruptResponseJsonException(string message) : base(message) { }
        public SyncCorruptResponseJsonException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class SyncPullDisabledException : SyncDomainException
    {
        public override string ErrorCode => "SYNC_PULL_DISABLED";
        public SyncPullDisabledException(string message = "ميزة مزامنة السحب معطلة حالياً (Sync:PullEnabled = false).") : base(message) { }
    }

    public class SyncPullAlreadyRunningException : SyncDomainException
    {
        public override string ErrorCode => "SYNC_PULL_ALREADY_RUNNING";
        public SyncPullAlreadyRunningException(string message = "توجد جلسة مزامنة سحب أو رفع قيد التشغيل بالفعل لنفس قاعدة البيانات.") : base(message) { }
    }

    public class SyncPullBlockedLocalChangesPendingException : SyncDomainException
    {
        public override string ErrorCode => "PULL_BLOCKED_LOCAL_CHANGES_PENDING";
        public SyncPullBlockedLocalChangesPendingException(string message = "تم حظر المزامنة لوجود تعديلات محلية قيد الانتظار أو المعالجة أو الفشل في الـ Outbox.") : base(message) { }
    }

    public class SyncPullCheckpointAheadOfServerException : SyncDomainException
    {
        public override string ErrorCode => "PULL_CHECKPOINT_AHEAD_OF_SERVER";
        public long LocalVersion { get; }
        public long ServerVersion { get; }

        public SyncPullCheckpointAheadOfServerException(long localVersion, long serverVersion, string message = "نقطة تفتيش المحطة المحلية متقدمة عن إصدار الخادم الحالي.")
            : base(message)
        {
            LocalVersion = localVersion;
            ServerVersion = serverVersion;
        }
    }

    public class SyncPullFeedGapException : SyncDomainException
    {
        public override string ErrorCode => "PULL_FEED_GAP";
        public SyncPullFeedGapException(string message) : base(message) { }
    }

    public class SyncPullDuplicateVersionException : SyncDomainException
    {
        public override string ErrorCode => "PULL_DUPLICATE_VERSION";
        public SyncPullDuplicateVersionException(string message) : base(message) { }
    }

    public class SyncPullUnsupportedEntityTypeException : SyncDomainException
    {
        public override string ErrorCode => "PULL_UNSUPPORTED_ENTITY_TYPE";
        public SyncPullUnsupportedEntityTypeException(string message) : base(message) { }
    }

    public class SyncPullUnsupportedOperationTypeException : SyncDomainException
    {
        public override string ErrorCode => "PULL_UNSUPPORTED_OPERATION_TYPE";
        public SyncPullUnsupportedOperationTypeException(string message) : base(message) { }
    }

    public class SyncPullTombstoneValidationException : SyncDomainException
    {
        public override string ErrorCode => "PULL_TOMBSTONE_VALIDATION_FAILED";
        public SyncPullTombstoneValidationException(string message) : base(message) { }
    }

    public class SyncPullAuthoritativeRowMissingException : SyncDomainException
    {
        public override string ErrorCode => "PULL_AUTHORITATIVE_ROW_MISSING";
        public SyncPullAuthoritativeRowMissingException(string message) : base(message) { }
    }

    public class SyncPullTombstoneEntityStillActiveException : SyncDomainException
    {
        public override string ErrorCode => "PULL_TOMBSTONE_ENTITY_STILL_ACTIVE";
        public SyncPullTombstoneEntityStillActiveException(string message) : base(message) { }
    }

    public class SyncPullLocalCheckpointChangedException : SyncDomainException
    {
        public override string ErrorCode => "PULL_LOCAL_CHECKPOINT_CHANGED";
        public SyncPullLocalCheckpointChangedException(string message = "تغيرت نقطة التفتيش المحلية أثناء تنفيذ عملية السحب.") : base(message) { }
    }

    public class SyncPullBatchDatabaseMismatchException : SyncDomainException
    {
        public override string ErrorCode => "PULL_BATCH_DATABASE_MISMATCH";
        public SyncPullBatchDatabaseMismatchException(string message) : base(message) { }
    }

    public class SyncPullBatchMalformedException : SyncDomainException
    {
        public override string ErrorCode => "PULL_BATCH_MALFORMED";
        public SyncPullBatchMalformedException(string message) : base(message) { }
    }

    public class SyncPullAuthoritativeStateMismatchException : SyncDomainException
    {
        public override string ErrorCode => "PULL_AUTHORITATIVE_STATE_MISMATCH";
        public SyncPullAuthoritativeStateMismatchException(string message) : base(message) { }
    }

    public class SyncPullFeedMalformedException : SyncDomainException
    {
        public override string ErrorCode => "PULL_FEED_MALFORMED";
        public SyncPullFeedMalformedException(string message) : base(message) { }
    }

    public class SyncLocalWriteBlockedActiveSyncException : SyncDomainException
    {
        public override string ErrorCode => "LOCAL_WRITE_BLOCKED_ACTIVE_SYNC";
        public SyncLocalWriteBlockedActiveSyncException(string message = "عملية الكتابة المحلية متوقفة لوجود عملية مزامنة نشطة قيد التنفيذ تحت قيد الـ Lease.") : base(message) { }
    }

    public class SyncPullForeignKeyResolutionException : SyncDomainException
    {
        public override string ErrorCode => "PULL_FOREIGN_KEY_RESOLUTION_FAILED";
        public SyncPullForeignKeyResolutionException(string message) : base(message) { }
    }

    public class SyncConflictRiskException : SyncDomainException
    {
        public override string ErrorCode => "BOTH_CHANGED_CONFLICT_RISK";
        public long LocalVersion { get; }
        public long ServerVersion { get; }
        public int PendingOutboxCount { get; }

        public SyncConflictRiskException(long localVersion, long serverVersion, int pendingOutboxCount, string message = "تعارض محتمل (BOTH_CHANGED / CONFLICT_RISK): توجد تعديلات محلية معلقة في الـ Outbox بينما الخادم يحتوي على تعديلات جديدة. تم حظر المزامنة للحفاظ على البيانات دون الكتابة فوقها.")
            : base(message)
        {
            LocalVersion = localVersion;
            ServerVersion = serverVersion;
            PendingOutboxCount = pendingOutboxCount;
        }
    }
}



