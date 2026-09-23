#nullable enable
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
    /// EF Core SaveChangesInterceptor that enforces fail-closed offline write safety guards
    /// during OfflineReadWritePilot mode:
    /// 1. Ensures all mutations occur strictly within an active, approved LocalWriteScopeContext (UnitOfWork).
    /// 2. Ensures only Daily entities can be mutated (all other entities are blocked fail-closed).
    /// 3. Ensures physical hard deletes (EntityState.Deleted) are strictly blocked.
    /// </summary>
    public class LocalWriteSafetyInterceptor : SaveChangesInterceptor
    {
        private readonly ISyncConnectionProvider _syncConnectionProvider;

        public LocalWriteSafetyInterceptor(ISyncConnectionProvider syncConnectionProvider)
        {
            _syncConnectionProvider = syncConnectionProvider;
        }

        public override InterceptionResult<int> SavingChanges(
            DbContextEventData eventData,
            InterceptionResult<int> result)
        {
            EnforceLocalWriteSafety(eventData);
            return base.SavingChanges(eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            EnforceLocalWriteSafety(eventData);
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        private void EnforceLocalWriteSafety(DbContextEventData eventData)
        {
            // Only active in OfflineReadWritePilot (LocalFirst:Enabled == true && ReadOnlyMode == false && LocalOnlyProduction == false)
            if (_syncConnectionProvider == null ||
                !_syncConnectionProvider.IsLocalFirstEnabled ||
                _syncConnectionProvider.IsReadOnlyMode ||
                _syncConnectionProvider.IsLocalOnlyProduction)
            {
                return;
            }

            var context = eventData.Context;
            if (context == null || !context.ChangeTracker.HasChanges())
            {
                return;
            }

            // 1. Guard against direct SaveChanges bypass outside the approved transactional UnitOfWork coordinator
            if (!LocalWriteScopeContext.IsActive)
            {
                throw new OfflineWriteScopeException(
                    "عمليات الحفظ المباشرة (Direct SaveChanges) محظورة في وضع Offline Read-Write Pilot. يجب أن تمر جميع التعديلات عبر UnitOfWork Transaction Coordinator.");
            }

            // 2. Validate all mutated entities
            foreach (var entry in context.ChangeTracker.Entries())
            {
                if (entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                {
                    if (entry.Entity is not Daily)
                    {
                        throw new OfflineWriteScopeException(
                            $"الكيان من نوع '{entry.Metadata.ClrType.Name}' غير مصرح بتعديله في وضع Offline Read-Write Pilot. العمليات المصرح بها محصورة في Daily فقط.");
                    }

                    if (entry.State == EntityState.Deleted)
                    {
                        throw new OfflineWriteScopeException(
                            "الحذف الفعلي (Hard Delete) غير مسموح به في وضع Offline Read-Write Pilot. يجب استخدام الحذف المنطقي (Soft Delete) فقط.");
                    }
                }
            }
        }
    }
}
