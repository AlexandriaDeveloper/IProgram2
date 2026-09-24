#nullable enable
using System;
using Core.Interfaces;

namespace Core.Sync.Capture
{
    public interface ILocalPreCaptureReconciliationClassifier
    {
        EntityCaptureStatus ClassifyEntity(
            ISyncableEntity entity,
            DateTime lastAzureBaselineTimestampUtc,
            DateTime captureActivationTimestampUtc,
            bool hasOutboxRecord);
    }
}
