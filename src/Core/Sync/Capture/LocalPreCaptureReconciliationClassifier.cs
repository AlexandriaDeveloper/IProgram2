#nullable enable
using System;
using Core.Interfaces;
using Core.Models;

namespace Core.Sync.Capture
{
    public class LocalPreCaptureReconciliationClassifier : ILocalPreCaptureReconciliationClassifier
    {
        public static LocalPreCaptureReconciliationClassifier Default { get; } = new LocalPreCaptureReconciliationClassifier();

        public EntityCaptureStatus ClassifyEntity(
            ISyncableEntity entity,
            DateTime lastAzureBaselineTimestampUtc,
            DateTime captureActivationTimestampUtc,
            bool hasOutboxRecord)
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));

            // Rule 1: If an outbox record exists for this entity, it is captured post-activation
            if (hasOutboxRecord)
            {
                return EntityCaptureStatus.CapturedPostActivation;
            }

            // Inspect effective mutation timestamp (UpdatedAt if present, otherwise CreatedAt)
            DateTime effectiveTimestamp;
            if (entity is Entity baseEntity)
            {
                effectiveTimestamp = baseEntity.UpdatedAt ?? baseEntity.CreatedAt;
            }
            else
            {
                // Fallback to reflection if not directly inheriting from Entity
                var propUpdated = entity.GetType().GetProperty("UpdatedAt")?.GetValue(entity) as DateTime?;
                var propCreated = entity.GetType().GetProperty("CreatedAt")?.GetValue(entity) as DateTime?;
                effectiveTimestamp = propUpdated ?? propCreated ?? DateTime.MinValue;
            }

            // Normalization: Ensure comparison in UTC
            if (effectiveTimestamp.Kind == DateTimeKind.Local)
            {
                effectiveTimestamp = effectiveTimestamp.ToUniversalTime();
            }

            if (effectiveTimestamp > captureActivationTimestampUtc)
            {
                // Modified after activation, but hasOutboxRecord is false => Diverged
                return EntityCaptureStatus.UntrackedOrDiverged;
            }

            if (effectiveTimestamp > lastAzureBaselineTimestampUtc)
            {
                // Modified between baseline and activation => Pre-capture local modification
                return EntityCaptureStatus.LocallyChangedPreCapture;
            }

            // Created/modified on or before baseline => Unchanged from baseline
            return EntityCaptureStatus.UnchangedFromBaseline;
        }
    }
}
