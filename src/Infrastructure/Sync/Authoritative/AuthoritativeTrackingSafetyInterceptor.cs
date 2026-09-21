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
    /// during Online mode when Sync:AuthoritativeTrackingEnabled is true, and prevents un-tracked
    /// Daily writes when tracking is disabled after authoritative cutover has been committed.
    /// </summary>
    public class AuthoritativeTrackingSafetyInterceptor : SaveChangesInterceptor
    {
        private readonly ISyncConnectionProvider _syncConnectionProvider;
        private readonly IConfiguration _configuration;
        private readonly IAuthoritativeCutoverGuard _cutoverGuard;


        public AuthoritativeTrackingSafetyInterceptor(
            ISyncConnectionProvider syncConnectionProvider,
            IConfiguration configuration,
            IAuthoritativeCutoverGuard cutoverGuard)
        {
            _syncConnectionProvider = syncConnectionProvider ?? throw new ArgumentNullException(nameof(syncConnectionProvider));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _cutoverGuard = cutoverGuard ?? throw new ArgumentNullException(nameof(cutoverGuard));
        }

        public override InterceptionResult<int> SavingChanges(
            DbContextEventData eventData,
            InterceptionResult<int> result)
        {
            EnforceAuthoritativeTrackingSafety(eventData);
            return base.SavingChanges(eventData, result);
        }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            await EnforceAuthoritativeTrackingSafetyAsync(eventData, cancellationToken).ConfigureAwait(false);
            return await base.SavingChangesAsync(eventData, result, cancellationToken).ConfigureAwait(false);
        }

        private void EnforceAuthoritativeTrackingSafety(DbContextEventData eventData)
        {
            // 1. Only active in Online mode (not LocalFirst, not ReadOnlyMode)
            if (_syncConnectionProvider.IsLocalFirstEnabled || _syncConnectionProvider.IsReadOnlyMode)
            {
                return;
            }

            var context = eventData.Context;
            if (context == null || !context.ChangeTracker.HasChanges())
            {
                return;
            }

            // 2. Check first whether SaveChanges contains a Daily mutation (Added, Modified, Deleted)
            // If no Daily mutation, do NOT perform any cutover query or change behavior.
            var hasDailyMutation = context.ChangeTracker.Entries()
                .Any(e => e.Entity is Daily && e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);

            if (!hasDailyMutation)
            {
                return;
            }

            var isTrackingEnabled = _configuration.GetValue<bool>("Sync:AuthoritativeTrackingEnabled", false);

            // 3. If Tracking Enabled == true:
            if (isTrackingEnabled)
            {
                if (!AuthoritativeWriteScopeContext.IsActive)
                {
                    throw new AuthoritativeWriteScopeException(
                        "عمليات الحفظ المباشرة (Direct SaveChanges) لـ Daily محظورة في وضع Online مع تفعيل Authoritative Tracking. يجب أن تمر جميع تعديلات Daily عبر UnitOfWork Transaction Coordinator.");
                }
                return;
            }

            // 4. If Tracking Enabled == false AND hasDailyMutation == true:
            // Verify authoritative cutover state synchronously before allowing save
            var databaseId = _syncConnectionProvider.GetSelectedDatabaseId();
            _cutoverGuard.ValidateCutoverState(context, databaseId);
        }

        private async Task EnforceAuthoritativeTrackingSafetyAsync(
            DbContextEventData eventData,
            CancellationToken cancellationToken)
        {
            // 1. Only active in Online mode (not LocalFirst, not ReadOnlyMode)
            if (_syncConnectionProvider.IsLocalFirstEnabled || _syncConnectionProvider.IsReadOnlyMode)
            {
                return;
            }

            var context = eventData.Context;
            if (context == null || !context.ChangeTracker.HasChanges())
            {
                return;
            }

            // 2. Check first whether SaveChanges contains a Daily mutation (Added, Modified, Deleted)
            // If no Daily mutation, do NOT perform any cutover query or change behavior.
            var hasDailyMutation = context.ChangeTracker.Entries()
                .Any(e => e.Entity is Daily && e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);

            if (!hasDailyMutation)
            {
                return;
            }

            var isTrackingEnabled = _configuration.GetValue<bool>("Sync:AuthoritativeTrackingEnabled", false);

            // 3. If Tracking Enabled == true:
            if (isTrackingEnabled)
            {
                if (!AuthoritativeWriteScopeContext.IsActive)
                {
                    throw new AuthoritativeWriteScopeException(
                        "عمليات الحفظ المباشرة (Direct SaveChanges) لـ Daily محظورة في وضع Online مع تفعيل Authoritative Tracking. يجب أن تمر جميع تعديلات Daily عبر UnitOfWork Transaction Coordinator.");
                }
                return;
            }

            // 4. If Tracking Enabled == false AND hasDailyMutation == true:
            // Verify authoritative cutover state asynchronously before allowing save
            var databaseId = _syncConnectionProvider.GetSelectedDatabaseId();
            await _cutoverGuard.ValidateCutoverStateAsync(context, databaseId, cancellationToken).ConfigureAwait(false);
        }
    }
}
