#nullable enable
using System;
using System.Threading;

namespace Core.Models.Sync
{
    /// <summary>
    /// Thread-safe AsyncLocal context that tracks whether the current execution flow is operating
    /// inside an approved authoritative online mutation tracking coordinator (UnitOfWork).
    /// </summary>
    public static class AuthoritativeWriteScopeContext
    {
        private static readonly AsyncLocal<bool> _isScopeActive = new AsyncLocal<bool>();

        public static bool IsActive => _isScopeActive.Value;

        public static IDisposable BeginScope()
        {
            _isScopeActive.Value = true;
            return new ScopeReleaser();
        }

        private sealed class ScopeReleaser : IDisposable
        {
            public void Dispose()
            {
                _isScopeActive.Value = false;
            }
        }
    }
}
