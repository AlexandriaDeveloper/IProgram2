#nullable enable
using System;

namespace Core.Exceptions
{
    public class SyncPushDisabledException : Exception
    {
        public SyncPushDisabledException(string message = "مزامنة الرفع (Push) معطلة حالياً.") : base(message) { }
    }

    public class SyncPushAlreadyRunningException : Exception
    {
        public SyncPushAlreadyRunningException(string message = "توجد جلسة مزامنة رفع قيد التشغيل بالفعل لنفس قاعدة البيانات.") : base(message) { }
    }

    public class SyncOperationIdReuseException : Exception
    {
        public SyncOperationIdReuseException(string message = "تم رفض العملية: محاولة إعادة استخدام ClientOperationId بمحتوى مختلف.") : base(message) { }
    }

    public class SyncVersionConflictException : Exception
    {
        public long ExpectedVersion { get; }
        public long CurrentServerVersion { get; }

        public SyncVersionConflictException(long expectedVersion, long currentServerVersion, string message = "تعارض في إصدار الخادم: لم يتطابق الإصدار المتوقع مع إصدار الخادم الحالي.")
            : base(message)
        {
            ExpectedVersion = expectedVersion;
            CurrentServerVersion = currentServerVersion;
        }
    }

    public class SyncEntityAlreadyExistsException : Exception
    {
        public SyncEntityAlreadyExistsException(string message = "الكيان موجود بالفعل في الخادم بنفس SyncId.") : base(message) { }
    }

    public class SyncEntityNotFoundException : Exception
    {
        public SyncEntityNotFoundException(string message = "الكيان المطلوب تعديله أو حذفه غير موجود في الخادم.") : base(message) { }
    }

    public class SyncPayloadValidationException : Exception
    {
        public SyncPayloadValidationException(string message) : base(message) { }
        public SyncPayloadValidationException(string message, Exception innerException) : base(message, innerException) { }
    }
}
