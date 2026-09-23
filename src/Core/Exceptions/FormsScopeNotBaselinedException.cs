#nullable enable
using System;

namespace Core.Exceptions
{
    public class FormsScopeNotBaselinedException : SyncDomainException
    {
        public override string ErrorCode => "FORMS_SCOPE_NOT_BASELINED";

        public FormsScopeNotBaselinedException(string message = "نطاق النماذج (Forms) غير مؤصل محلياً حتى الآن (NOT_BASELINED). العمليات المحلية على النماذج معطلة لحين إتمام التأصيل المعتمد.")
            : base(message)
        {
        }

        public FormsScopeNotBaselinedException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    public class SyncScopeNotBaselinedException : SyncDomainException
    {
        public override string ErrorCode => "SCOPE_NOT_BASELINED";

        public SyncScopeNotBaselinedException(string message)
            : base(message)
        {
        }

        public SyncScopeNotBaselinedException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
