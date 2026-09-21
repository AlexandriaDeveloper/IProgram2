#nullable enable
using System;

namespace Core.Exceptions
{
    public class AuthoritativeWriteScopeException : Exception
    {
        public string ErrorCode { get; } = "AUTHORITATIVE_WRITE_SCOPE_BLOCKED";

        public AuthoritativeWriteScopeException(string message) : base(message)
        {
        }

        public AuthoritativeWriteScopeException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }

    public class AuthoritativeBindingException : Exception
    {
        public string ErrorCode { get; } = "AUTHORITATIVE_BINDING_MISMATCH";

        public AuthoritativeBindingException(string message) : base(message)
        {
        }

        public AuthoritativeBindingException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }

    public class AuthoritativeTrackingException : Exception
    {
        public string ErrorCode { get; } = "AUTHORITATIVE_TRACKING_FAILED";

        public AuthoritativeTrackingException(string message) : base(message)
        {
        }

        public AuthoritativeTrackingException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }

    public class AuthoritativeTrackingConfigurationException : Exception
    {
        public string ErrorCode { get; } = "AUTHORITATIVE_TRACKING_CONFIGURATION_INVALID";

        public AuthoritativeTrackingConfigurationException(string message) : base(message)
        {
        }

        public AuthoritativeTrackingConfigurationException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }

    public class AuthoritativeConcurrencyConflictException : Exception
    {
        public string ErrorCode { get; } = "AUTHORITATIVE_CONCURRENCY_CONFLICT";

        public AuthoritativeConcurrencyConflictException(string message) : base(message)
        {
        }

        public AuthoritativeConcurrencyConflictException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }

    public static class AuthoritativeCutoverErrorCodes
    {
        public const string CutoverCommittedTrackingDisabled = "CUTOVER_COMMITTED_TRACKING_DISABLED";
        public const string AuthoritativeCutoverStateUnverifiable = "AUTHORITATIVE_CUTOVER_STATE_UNVERIFIABLE";
    }

    public class AuthoritativeCutoverGuardException : Exception
    {
        public string ErrorCode { get; }

        public AuthoritativeCutoverGuardException(
            string message, 
            string errorCode = AuthoritativeCutoverErrorCodes.CutoverCommittedTrackingDisabled) 
            : base(message)
        {
            ErrorCode = errorCode;
        }

        public AuthoritativeCutoverGuardException(
            string message, 
            Exception innerException, 
            string errorCode = AuthoritativeCutoverErrorCodes.CutoverCommittedTrackingDisabled) 
            : base(message, innerException)
        {
            ErrorCode = errorCode;
        }
    }
}
