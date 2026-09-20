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
        private static readonly AsyncLocal<int> _depth = new AsyncLocal<int>();

        public static bool IsActive => _depth.Value > 0;

        public static IDisposable BeginScope()
        {
            _depth.Value = _depth.Value + 1;
            return new ScopeReleaser();
        }

        private sealed class ScopeReleaser : IDisposable
        {
            private bool _disposed;

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _depth.Value = Math.Max(0, _depth.Value - 1);
            }
        }
    }
}
