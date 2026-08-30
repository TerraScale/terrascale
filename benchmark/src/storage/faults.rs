//! Private fault-injection control surface of the storage adapter.
//!
//! The plan is compiled into the storage adapter so the publication order has
//! one implementation, but only `cfg(test)` code inside this crate can arm a
//! fault. Production callers never see this type: it is not re-exported from
//! the crate root, and an unarmed plan never fires.

/// One named point in the publication order where a test may inject a failure.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum FaultPoint {
    /// Write half of the next WAL frame and then fail the append.
    #[allow(dead_code)]
    WalAppend,
    /// Fail while applying the write set to the new private root and commit
    /// marker, before anything is published.
    CommitMarkerApplication,
    /// Fail the durable sync of the `ReadyToPublish` operation-ledger record.
    ReadyLedgerSync,
    /// Fail the atomic root-pointer publication itself.
    PointerPublish,
    /// Fail the post-publication sync of the published pointer file.
    PointerSync,
    /// Fail the post-publication sync of the containing directory.
    DirectorySync,
    /// Fail the durable sync of the `Published` operation-ledger record.
    PublishedLedgerSync,
    /// Fail after an append-only safety stream syncs but before the
    /// independent storage-frontier slot is written and synced.
    StorageFrontierSync,
    /// Fail the frontier sync for a Ready ledger transition.
    ReadyFrontierSync,
    /// Fail the frontier sync for a Published ledger transition.
    PublishedFrontierSync,
    /// Fail the frontier sync for a Rejected ledger transition.
    RejectedFrontierSync,
    /// Fail the frontier sync for a retained-evidence batch.
    RetainedFrontierSync,
    /// Fail after creating the active-manifest temporary file, before its
    /// bytes are written.
    ActivationManifestTempCreated,
    /// Fail after writing the active-manifest bytes, before syncing them.
    ActivationManifestWritten,
    /// Fail after syncing the active-manifest temporary file, before rename.
    ActivationManifestSynced,
    /// Fail after renaming the active manifest, before syncing the directory.
    ActivationManifestRenamed,
    /// Fail after the active manifest and its directory entry are durable,
    /// before the format-marker publication starts.
    ActivationManifestDirectorySynced,
    /// Fail after creating the active-format temporary file, before its
    /// bytes are written.
    ActivationFormatTempCreated,
    /// Fail after writing the active-format bytes, before syncing them.
    ActivationFormatWritten,
    /// Fail after syncing the active-format temporary file, before rename.
    ActivationFormatSynced,
    /// Fail after renaming the active format, before syncing the directory.
    ActivationFormatRenamed,
    /// Fail after the active format and its directory entry are durable,
    /// before activation returns its receipt.
    ActivationFormatDirectorySynced,
    /// Fail the next publication-history journal append. The commit still
    /// publishes: journaling is best-effort, so the injected failure leaves
    /// only the observable divergence between the journaled high-water mark
    /// and the published sequence (repair F2).
    JournalAppend,
    /// Fail the compaction checkpoint's manifest publication, after the
    /// checkpoint root file is written and synced (GAP-S1 D4).
    CompactionCheckpointPublish,
    /// Fail after the activated checkpoint manifest is published and
    /// directory-synced, before either next-epoch frontier slot is written.
    CompactionCheckpointPublished,
    /// Fail after the activated checkpoint writes and syncs the first empty
    /// slot for the next WAL epoch.
    CompactionWalFrontierFirstSlot,
    /// Fail after the activated checkpoint writes and syncs the second empty
    /// slot, before the old WAL is shortened.
    CompactionWalFrontierSecondSlot,
    /// Fail the compaction pass immediately before the write-ahead log is
    /// truncated to the checkpoint (GAP-S1 D4).
    CompactionWalTruncate,
    /// Fail after the WAL length changes but before that truncation is synced.
    CompactionWalSync,
    /// Fail the disposable legacy WAL-frontier reset after the checkpoint and
    /// WAL truncation complete.
    CompactionLegacyFrontierReset,
    /// Fail the compaction pass after the retained-evidence records are
    /// appended but before they are synced (GAP-S1 D4).
    CompactionEvidenceSync,
    /// Fail the compaction pass after the retained evidence and bound are
    /// durably synced but before the journal is rewritten (GAP-S1 D4).
    CompactionJournalRewrite,
    /// Fail the compaction pass immediately before superseded root files
    /// are reclaimed (GAP-S1 D4).
    CompactionRootReclaim,
}

/// The armed plan. `None` means no fault fires, which is the only state
/// production code can observe.
#[derive(Default)]
pub(crate) struct FaultPlan {
    armed: Option<FaultPoint>,
}

impl FaultPlan {
    /// Creates an unarmed plan.
    pub(crate) const fn empty() -> Self {
        Self { armed: None }
    }

    /// Fires a one-shot fault when `point` is armed.
    pub(crate) fn fire(&mut self, point: FaultPoint) -> Result<(), FaultPoint> {
        if self.armed == Some(point) {
            self.armed = None;
            return Err(point);
        }
        Ok(())
    }

    /// Arms one one-shot fault. Test-only by visibility.
    #[cfg(test)]
    pub(crate) fn arm(&mut self, point: FaultPoint) {
        self.armed = Some(point);
    }
}
