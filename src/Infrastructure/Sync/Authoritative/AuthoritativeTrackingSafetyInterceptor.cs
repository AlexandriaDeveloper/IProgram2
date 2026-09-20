#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Core.Exceptions;
using Core.Interfaces;
using Core.Models;
using Core.Models.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;

namespace Auth.Infrastructure.Sync.Authoritative
{
    /// <summary>
    /// EF Core SaveChangesInterceptor that prevents direct SaveChanges bypass for Daily mutations
    /// during Online mode when Sync:AuthoritativeTrackingEnabled is true.
    /// </summary>
    public class AuthoritativeTrackingSafetyInterceptor : SaveChangesInterceptor
    {
        private readonly ISyncConnectionProvider _syncConnectionProvider;
        private readonly IConfiguration _configuration;

        public AuthoritativeTrackingSafetyInterceptor(
            ISyncConnectionProvider syncConnectionProvider,
            IConfiguration configuration)
        {
            _syncConnectionProvider = syncConnectionProvider ?? throw new ArgumentNullException(nameof(syncConnectionProvider));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        }

        public override InterceptionResult<int> SavingChanges(
            DbContextEventData eventData,
            InterceptionResult<int> result)
        {
            EnforceAuthoritativeTrackingSafety(eventData);
            return base.SavingChanges(eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            EnforceAuthoritativeTrackingSafety(eventData);
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        private void EnforceAuthoritativeTrackingSafety(DbContextEventData eventData)
        {
            // Only active in Online mode (not LocalFirst, not ReadOnlyMode)
            if (_syncConnectionProvider.IsLocalFirstEnabled || _syncConnectionProvider.IsReadOnlyMode)
            {
                return;
            }

            // Only active when gate is enabled
            var isTrackingEnabled = _configuration.GetValue<bool>("Sync:AuthoritativeTrackingEnabled", false);
            if (!isTrackingEnabled)
            {
                return;
            }

            var context = eventData.Context;
            if (context == null || !context.ChangeTracker.HasChanges())
            {
                return;
            }

            // Check if any Daily mutation is part of this SaveChanges call
            var hasDailyMutation = context.ChangeTracker.Entries()
                .Any(e => e.Entity is Daily && e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);

            if (hasDailyMutation && !AuthoritativeWriteScopeContext.IsActive)
            {
                throw new AuthoritativeWriteScopeException(
                    "عمليات الحفظ المباشرة (Direct SaveChanges) لـ Daily محظورة في وضع Online مع تفعيل Authoritative Tracking. يجب أن تمر جميع تعديلات Daily عبر UnitOfWork Transaction Coordinator.");
            }
        }
    }
}
