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
}
