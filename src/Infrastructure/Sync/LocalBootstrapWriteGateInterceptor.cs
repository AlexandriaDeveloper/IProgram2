using System;
using System.Threading;
using System.Threading.Tasks;
using Core.Exceptions;
using Core.Interfaces;
using Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Auth.Infrastructure.Sync
{
    /// <summary>
    /// EF Core SaveChangesInterceptor that automatically enforces the server-side Bootstrap Write Gate
    /// on ApplicationContext writes whenever LocalFirst is enabled.
    /// </summary>
    public class LocalBootstrapWriteGateInterceptor : SaveChangesInterceptor
    {
        private readonly ISyncConnectionProvider _syncConnectionProvider;
        private readonly ILocalBootstrapWriteGate _writeGate;

        public LocalBootstrapWriteGateInterceptor(
            ISyncConnectionProvider syncConnectionProvider,
            ILocalBootstrapWriteGate writeGate)
        {
            _syncConnectionProvider = syncConnectionProvider;
            _writeGate = writeGate;
        }

        public override InterceptionResult<int> SavingChanges(
            DbContextEventData eventData,
            InterceptionResult<int> result)
        {
            EnforceWriteGate(eventData);
            return base.SavingChanges(eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            EnforceWriteGate(eventData);
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        private void EnforceWriteGate(DbContextEventData eventData)
        {
            // 0. If ReadOnlyMode is enabled and modifications exist in change tracker: unconditionally block
            if (_syncConnectionProvider != null && _syncConnectionProvider.IsReadOnlyMode)
            {
                var ctx = eventData.Context;
                if (ctx != null && ctx.ChangeTracker.HasChanges())
                {
                    var dbId = _syncConnectionProvider.GetSelectedDatabaseId();
                    throw new ReadOnlyModeException($"النظام يعمل حالياً في وضع القراءة المحلية فقط للعام {dbId}. جميع عمليات الإضافة والتعديل والحذف معطلة.");
                }
                return;
            }

            // 1. If LocalFirst is not enabled, do not gate writes (Azure production path proceeds unaffected)
            if (_syncConnectionProvider == null || !_syncConnectionProvider.IsLocalFirstEnabled)
            {
                return;
            }

            // 2. If no entity modifications exist in change tracker, no-op
            var context = eventData.Context;
            if (context == null || !context.ChangeTracker.HasChanges())
            {
                return;
            }

            // 3. Resolve the canonical database ID for the operational context
            var databaseId = _syncConnectionProvider.GetSelectedDatabaseId();
            if (string.IsNullOrWhiteSpace(databaseId))
            {
                throw new InvalidDatabaseSelectionException("Cannot enforce bootstrap write-gate: canonical database ID is missing.");
            }

            // 4. Server-side write-gate enforcement: throws BootstrapNotVerifiedException if not VERIFIED_READY
            if (_writeGate == null)
            {
                throw new BootstrapNotVerifiedException(databaseId, "Bootstrap write-gate service is not available.");
            }

            _writeGate.EnsureWriteAllowed(databaseId);
        }
    }
}
