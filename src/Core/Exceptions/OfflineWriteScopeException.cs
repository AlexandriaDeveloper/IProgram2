#nullable enable
using System;

namespace Core.Exceptions
{
    /// <summary>
    /// Thrown when a write or mutation is attempted in OfflineReadWritePilot mode
    /// that falls outside the permitted Daily pilot scope or bypasses the approved transactional UnitOfWork coordinator.
    /// </summary>
    public class OfflineWriteScopeException : Exception
    {
        public OfflineWriteScopeException(string message) : base(message) { }

        public OfflineWriteScopeException(string message, Exception innerException) : base(message, innerException) { }
    }
}
