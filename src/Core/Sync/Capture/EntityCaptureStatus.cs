#nullable enable

namespace Core.Sync.Capture
{
    public enum EntityCaptureStatus
    {
        /// <summary>
        /// Row was created/updated before or at the last verified Azure baseline and has no local outbox record.
        /// </summary>
        UnchangedFromBaseline = 0,

        /// <summary>
        /// Row was created or modified locally after the baseline but BEFORE change-capture activation.
        /// Needs reconciliation before backfill/push.
        /// </summary>
        LocallyChangedPreCapture = 1,

        /// <summary>
        /// Row was modified after change-capture activation and is represented in [sync].[LocalOutbox].
        /// </summary>
        CapturedPostActivation = 2,

        /// <summary>
        /// Row was modified after activation timestamp but lacks a corresponding outbox record (divergence).
        /// </summary>
        UntrackedOrDiverged = 3
    }
}
