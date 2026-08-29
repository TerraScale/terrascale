//! Storage compaction: the capability, its typed request, and the pass
//! (GAP-S1, ticket #81).
//!
//! The decision record is `docs/storage/gaps1-storage-compaction.md`; this
//! module implements it. One pass is an in-band, caller-driven maintenance
//! operation on an open store (decision D1): it checkpoints the published
//! root, truncates the write-ahead log the checkpoint replaces, optionally
//! gives up publication history below the journal's durable head after the
//! window's deletion evidence is durably held in the retained-evidence
//! store, and reclaims superseded root files. The operation ledger is never
//! rewritten (D6), and the kill-point ordering of D4 makes every crash
//! inside the sequence recoverable without loss.
//!
//! Reporting is bounded by construction (D2): the receipt carries fixed
//! facts and counts — never unbounded lists — and the optional progress sink
//! receives at most one start and one completion event per phase of the
//! pass's fixed phase set.

use std::{collections::BTreeMap, fs, io::Write, path::Path, sync::Arc};

use crate::types::{StorageSequence, TenantId};

use super::{
    FileStorage, PublishedCommit, PublishedWrite,
    faults::FaultPoint,
    ledger::{self, HistoryWriter},
    retained::RetainedFact,
    root,
};

/// Which durable artifacts one compaction pass may reclaim (decision D2).
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum CompactionPolicy {
    /// Checkpoint the published root, truncate the write-ahead log to the
    /// checkpoint, and reclaim superseded root files. The
    /// publication-history journal is untouched.
    Reclaim,
    /// Everything [`CompactionPolicy::Reclaim`] does, plus giving up
    /// publication history below the journal's durable head — but only
    /// after the window's deletion evidence is durably held in the
    /// retained-evidence store (decision D5's gates).
    ReclaimHistory,
}

/// A sink receiving one bounded progress event per pass phase transition.
pub type CompactionProgressSink = Arc<dyn Fn(&CompactionProgress) + Send + Sync>;

/// One typed compaction request.
///
/// The request carries a policy and an optional progress sink — never a
/// sequence: the public surface has no caller-minted sequences by design,
/// and the pass derives its bounds from the store's own durable facts.
#[derive(Clone)]
pub struct CompactionRequest {
    policy: CompactionPolicy,
    progress: Option<CompactionProgressSink>,
}

impl CompactionRequest {
    /// Creates one request for the given policy.
    #[must_use]
    pub const fn new(policy: CompactionPolicy) -> Self {
        Self { policy, progress: None }
    }

    /// Attaches one bounded progress sink to the request.
    #[must_use]
    pub fn with_progress(mut self, sink: CompactionProgressSink) -> Self {
        self.progress = Some(sink);
        self
    }

    /// Lowers the request to the storage-internal pass request, carrying the
    /// retention floor the watermark named (decision D5: a pass never
    /// discards what the watermark holds).
    pub(crate) fn lower(self, floor: Option<StorageSequence>) -> StorageCompaction {
        StorageCompaction { policy: self.policy, floor, progress: self.progress }
    }
}

/// One phase of the compaction pass, in execution order.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum CompactionPhase {
    /// Writing and syncing the checkpoint root, manifest, and directory.
    Checkpoint,
    /// Truncating the write-ahead log the checkpoint replaced.
    WalReclaim,
    /// Extracting the window's deletion evidence into the durable store.
    EvidenceExtraction,
    /// Rewriting the journal to its retained records.
    JournalRewrite,
    /// Reclaiming superseded root files.
    RootReclaim,
}

/// One bounded progress event: a phase started or completed, with the
/// unit counts that name the phase's work.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct CompactionProgress {
    phase: CompactionPhase,
    completed: bool,
    units_done: u64,
    units_total: u64,
}

impl CompactionProgress {
    /// Builds one progress event from an LSM-backend pass (P1).
    pub(crate) const fn from_parts(
        phase: CompactionPhase,
        completed: bool,
        units_done: u64,
        units_total: u64,
    ) -> Self {
        Self { phase, completed, units_done, units_total }
    }

    /// Returns the phase the event names.
    #[must_use]
    pub const fn phase(&self) -> CompactionPhase {
        self.phase
    }

    /// Reports whether the phase completed (`false` marks its start).
    #[must_use]
    pub const fn completed(&self) -> bool {
        self.completed
    }

    /// Returns the units of work performed when the event fired.
    #[must_use]
    pub const fn units_done(&self) -> u64 {
        self.units_done
    }

    /// Returns the units of work the phase covers.
    #[must_use]
    pub const fn units_total(&self) -> u64 {
        self.units_total
    }
}

/// The outcome of the history-reclaim part of one pass (decision D5).
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum HistoryReclaimOutcome {
    /// The pass's policy left the journal untouched.
    NotRequested,
    /// History below the bound was given up with its deletion evidence
    /// durably held.
    Reclaimed {
        /// The bound the pass declared and durably recorded.
        bound: StorageSequence,
    },
    /// The journal serves no verified window, so there is no readable
    /// window to give up: declaring one would treat an unreadable window
    /// as an empty one.
    RefusedUnservableWindow,
    /// An operation the ledger resolves as committed below the candidate
    /// bound was never journaled, so its deletion evidence cannot be
    /// extracted; history below the bound is not given up.
    RefusedDivergence {
        /// How many below-bound commits have no journal record.
        diverged: u64,
    },
}

/// Why a compaction pass could not complete.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum CompactionError {
    /// Durable storage failed inside the pass. The store is left in the
    /// kill-point state the decision record's D4 table names: reopening
    /// loses nothing, and the pass may be retried.
    Storage,
}

/// The bounded facts one completed pass reports.
///
/// Every fact is a count, a byte length, or a sequence the pass observed —
/// never an unbounded list, and never a speed statement.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct CompactionReceipt {
    checkpoint_sequence: Option<StorageSequence>,
    wal_bytes_before: u64,
    wal_bytes_after: u64,
    journal_bytes_before: u64,
    journal_bytes_after: u64,
    journal_records_before: u64,
    journal_records_after: u64,
    roots_before: u64,
    roots_removed: u64,
    roots_unremovable: u64,
    servable_start_before: Option<StorageSequence>,
    servable_start_after: Option<StorageSequence>,
    journaled_head: Option<StorageSequence>,
    window_commits: u64,
    evidence_extracted: u64,
    evidence_retained: u64,
    evidence_below_floor: u64,
    history_reclaim: HistoryReclaimOutcome,
    phases: Vec<CompactionPhase>,
}

impl CompactionReceipt {
    /// Builds a receipt from an LSM-backend pass (P1).
    ///
    /// Field mapping for the LSM engine, recorded honestly: `wal_*` counts
    /// the LSM write-ahead log; `journal_*` counts the history keyspace the
    /// atomic commit batch writes; `roots_*` count the immutable segment
    /// artifacts the pass reclaimed (a merged-away segment is the LSM's
    /// superseded root).
    #[allow(clippy::too_many_arguments, clippy::missing_panics_doc)]
    pub(crate) fn from_parts(
        checkpoint_sequence: Option<StorageSequence>,
        wal_bytes_before: u64,
        wal_bytes_after: u64,
        journal_bytes_before: u64,
        journal_bytes_after: u64,
        journal_records_before: u64,
        journal_records_after: u64,
        roots_before: u64,
        roots_removed: u64,
        roots_unremovable: u64,
        servable_start_before: Option<StorageSequence>,
        servable_start_after: Option<StorageSequence>,
        journaled_head: Option<StorageSequence>,
        window_commits: u64,
        evidence_extracted: u64,
        evidence_retained: u64,
        evidence_below_floor: u64,
        history_reclaim: HistoryReclaimOutcome,
        phases: Vec<CompactionPhase>,
    ) -> Self {
        Self {
            checkpoint_sequence,
            wal_bytes_before,
            wal_bytes_after,
            journal_bytes_before,
            journal_bytes_after,
            journal_records_before,
            journal_records_after,
            roots_before,
            roots_removed,
            roots_unremovable,
            servable_start_before,
            servable_start_after,
            journaled_head,
            window_commits,
            evidence_extracted,
            evidence_retained,
            evidence_below_floor,
            history_reclaim,
            phases,
        }
    }

    /// Returns the published sequence the pass checkpointed.
    #[must_use]
    pub const fn checkpoint_sequence(&self) -> Option<StorageSequence> {
        self.checkpoint_sequence
    }

    /// Returns the write-ahead log's byte length before the pass.
    #[must_use]
    pub const fn wal_bytes_before(&self) -> u64 {
        self.wal_bytes_before
    }

    /// Returns the write-ahead log's byte length after the pass.
    #[must_use]
    pub const fn wal_bytes_after(&self) -> u64 {
        self.wal_bytes_after
    }

    /// Returns the journal's byte length before the pass.
    #[must_use]
    pub const fn journal_bytes_before(&self) -> u64 {
        self.journal_bytes_before
    }

    /// Returns the journal's byte length after the pass.
    #[must_use]
    pub const fn journal_bytes_after(&self) -> u64 {
        self.journal_bytes_after
    }

    /// Returns the journal's commit-record count before the pass.
    #[must_use]
    pub const fn journal_records_before(&self) -> u64 {
        self.journal_records_before
    }

    /// Returns the journal's commit-record count after the pass.
    #[must_use]
    pub const fn journal_records_after(&self) -> u64 {
        self.journal_records_after
    }

    /// Returns how many root files existed before the pass.
    #[must_use]
    pub const fn roots_before(&self) -> u64 {
        self.roots_before
    }

    /// Returns how many superseded root files the pass removed.
    #[must_use]
    pub const fn roots_removed(&self) -> u64 {
        self.roots_removed
    }

    /// Returns how many superseded root files could not be removed — a
    /// disclosed reclamation miss, never a silent one.
    #[must_use]
    pub const fn roots_unremovable(&self) -> u64 {
        self.roots_unremovable
    }

    /// Returns the servable window's start before the pass.
    #[must_use]
    pub const fn servable_start_before(&self) -> Option<StorageSequence> {
        self.servable_start_before
    }

    /// Returns the servable window's start after the pass.
    #[must_use]
    pub const fn servable_start_after(&self) -> Option<StorageSequence> {
        self.servable_start_after
    }

    /// Returns the journal's durable head as the pass observed it.
    #[must_use]
    pub const fn journaled_head(&self) -> Option<StorageSequence> {
        self.journaled_head
    }

    /// Returns how many commits the given-up window held.
    #[must_use]
    pub const fn window_commits(&self) -> u64 {
        self.window_commits
    }

    /// Returns how many deletion facts the window contributed.
    #[must_use]
    pub const fn evidence_extracted(&self) -> u64 {
        self.evidence_extracted
    }

    /// Returns how many deletion facts the pass wrote durably to the
    /// retained-evidence store (already-durable facts are not re-appended).
    #[must_use]
    pub const fn evidence_retained(&self) -> u64 {
        self.evidence_retained
    }

    /// Returns how many deletion facts fell below the retention floor and
    /// were therefore not retained — a disclosed count, never a silent
    /// drop.
    #[must_use]
    pub const fn evidence_below_floor(&self) -> u64 {
        self.evidence_below_floor
    }

    /// Returns the history-reclaim outcome of the pass.
    #[must_use]
    pub fn history_reclaim(&self) -> HistoryReclaimOutcome {
        self.history_reclaim.clone()
    }

    /// Returns the phases the pass completed, in execution order — bounded
    /// by the pass's fixed phase set.
    #[must_use]
    pub fn phases(&self) -> &[CompactionPhase] {
        &self.phases
    }
}

/// The storage-internal pass request lowered from a public one.
pub(crate) struct StorageCompaction {
    /// The policy naming which artifacts may be reclaimed.
    pub(crate) policy: CompactionPolicy,
    /// The retention floor a watermark-carrying pass must respect: facts
    /// below it are skipped with a disclosed count.
    pub(crate) floor: Option<StorageSequence>,
    /// The optional bounded progress sink.
    pub(crate) progress: Option<CompactionProgressSink>,
}

/// The facts the engine-level compaction driver needs to declare the new
/// bound to the tombstone retention watermark (decision D5).
pub(crate) struct CompactionDriverFacts {
    /// The window's commits, in global sequence order — the deletion
    /// evidence and per-tenant non-deletion facts the watermark extracts on
    /// declaration.
    pub(crate) window: Vec<PublishedCommit>,
    /// The bound this pass declared, when it reclaimed history.
    pub(crate) bound: Option<StorageSequence>,
    /// The journal's durable head at pass time.
    pub(crate) head: Option<StorageSequence>,
}

impl CompactionDriverFacts {
    /// Builds the facts from an LSM-backend pass (P1): the same three
    /// durable facts the root-store driver derives.
    pub(crate) fn from_parts(
        window: Vec<PublishedCommit>,
        bound: Option<StorageSequence>,
        head: Option<StorageSequence>,
    ) -> Self {
        Self { window, bound, head }
    }
}

/// The durable retained-evidence state, read for watermark re-derivation.
pub(crate) struct RetainedState {
    /// The durably declared history bound, when one exists.
    pub(crate) bound: Option<StorageSequence>,
    /// Every retained deletion fact the store holds.
    pub(crate) facts: Vec<RetainedFact>,
    /// The merged per-tenant non-deletion facts the bound records carry.
    pub(crate) non_deletion: Vec<(TenantId, Option<StorageSequence>)>,
}

impl FileStorage {
    /// Runs one compaction pass (decision record D2 and D4).
    pub(crate) fn run_compaction(
        &mut self,
        request: StorageCompaction,
    ) -> Result<(CompactionReceipt, CompactionDriverFacts), CompactionError> {
        let StorageCompaction { policy, floor, progress } = request;
        let progress = progress.as_ref();
        let mut phases: Vec<CompactionPhase> = Vec::new();
        let wal_path = self.dir.join("wal");
        let journal_path = self.dir.join("history");
        let wal_bytes_before = file_len(&wal_path);
        let journal_bytes_before = file_len(&journal_path);
        let journal_records_before = self.journal_index.record_count();
        let servable_start_before = self.journal_index.servable_start();
        let roots_before = count_root_files(&self.dir);
        let head = self.journal_high_water;

        // Phase: checkpoint. The published root becomes durable independent
        // of the write-ahead log before anything is shortened (D4 ordering
        // 1).
        emit(progress, CompactionPhase::Checkpoint, false, 0, 1);
        let checkpoint_sequence = self.compact_checkpoint()?;
        phases.push(CompactionPhase::Checkpoint);
        emit(progress, CompactionPhase::Checkpoint, true, 1, 1);

        // Phase: write-ahead log reclaim. Only the checkpoint's durability
        // makes this safe.
        emit(progress, CompactionPhase::WalReclaim, false, 0, wal_bytes_before);
        if self.faults.fire(FaultPoint::CompactionWalTruncate).is_err() {
            return Err(CompactionError::Storage);
        }
        self.wal.truncate(0).map_err(|_| CompactionError::Storage)?;
        self.wal.sync().map_err(|_| CompactionError::Storage)?;
        phases.push(CompactionPhase::WalReclaim);
        emit(progress, CompactionPhase::WalReclaim, true, 0, wal_bytes_before);

        // History reclaim under the decision record's D5 gates.
        let mut history_reclaim = HistoryReclaimOutcome::NotRequested;
        let mut window: Vec<PublishedCommit> = Vec::new();
        let mut declared: Option<StorageSequence> = None;
        let mut evidence_extracted = 0_u64;
        let mut evidence_retained = 0_u64;
        let mut evidence_below_floor = 0_u64;
        if policy == CompactionPolicy::ReclaimHistory {
            match self.history_reclaim_gate(head) {
                Ok(bound) => {
                    let window_len = self.journal_index.commits_below(bound).len();
                    emit(
                        progress,
                        CompactionPhase::EvidenceExtraction,
                        false,
                        0,
                        window_len_as_u64(window_len),
                    );
                    let extraction = self.extract_and_retain_evidence(bound, head, floor)?;
                    window = extraction.window;
                    evidence_extracted = extraction.extracted;
                    evidence_retained = extraction.retained;
                    evidence_below_floor = extraction.below_floor;
                    phases.push(CompactionPhase::EvidenceExtraction);
                    emit(
                        progress,
                        CompactionPhase::EvidenceExtraction,
                        true,
                        evidence_extracted,
                        window_len_as_u64(window_len),
                    );

                    emit(progress, CompactionPhase::JournalRewrite, false, 0, 1);
                    if self.faults.fire(FaultPoint::CompactionJournalRewrite).is_err() {
                        return Err(CompactionError::Storage);
                    }
                    self.rewrite_journal(bound, head)?;
                    phases.push(CompactionPhase::JournalRewrite);
                    emit(progress, CompactionPhase::JournalRewrite, true, 1, 1);
                    history_reclaim =
                        HistoryReclaimOutcome::Reclaimed { bound: StorageSequence::new(bound) };
                    declared = Some(StorageSequence::new(bound));
                }
                Err(refusal) => history_reclaim = refusal,
            }
        }

        // Phase: root reclaim — last, and never the manifest's own root.
        emit(progress, CompactionPhase::RootReclaim, false, 0, roots_before);
        let (roots_removed, roots_unremovable) = self.reclaim_roots()?;
        phases.push(CompactionPhase::RootReclaim);
        emit(progress, CompactionPhase::RootReclaim, true, roots_removed, roots_before);

        let receipt = CompactionReceipt {
            checkpoint_sequence,
            wal_bytes_before,
            wal_bytes_after: file_len(&wal_path),
            journal_bytes_before,
            journal_bytes_after: file_len(&journal_path),
            journal_records_before,
            journal_records_after: self.journal_index.record_count(),
            roots_before,
            roots_removed,
            roots_unremovable,
            servable_start_before,
            servable_start_after: self.journal_index.servable_start(),
            journaled_head: head.map(StorageSequence::new),
            window_commits: u64::try_from(window.len()).unwrap_or(u64::MAX),
            evidence_extracted,
            evidence_retained,
            evidence_below_floor,
            history_reclaim,
            phases,
        };
        let facts =
            CompactionDriverFacts { window, bound: declared, head: head.map(StorageSequence::new) };
        Ok((receipt, facts))
    }

    /// Checkpoints the published root: writes and syncs the root file,
    /// writes, publishes, and syncs the manifest, and syncs the directory.
    fn compact_checkpoint(&mut self) -> Result<Option<StorageSequence>, CompactionError> {
        let sequence = self.published.sequence;
        let table = Arc::clone(&self.published.table);
        let id = root::root_identity_of(sequence, &table);
        root::write_root_file(&self.dir, sequence, &table, true)
            .map_err(|_| CompactionError::Storage)?;
        root::write_manifest_temp(&self.dir, sequence, &id, true)
            .map_err(|_| CompactionError::Storage)?;
        if self.faults.fire(FaultPoint::CompactionCheckpointPublish).is_err() {
            return Err(CompactionError::Storage);
        }
        root::publish_manifest(&self.dir).map_err(|_| CompactionError::Storage)?;
        root::sync_file(&self.dir.join("manifest")).map_err(|_| CompactionError::Storage)?;
        root::sync_dir(&self.dir).map_err(|_| CompactionError::Storage)?;
        Ok(Some(StorageSequence::new(sequence)))
    }

    /// Applies the D5 gates and returns the candidate bound (the journal's
    /// durable head) or the typed refusal.
    fn history_reclaim_gate(&self, head: Option<u64>) -> Result<u64, HistoryReclaimOutcome> {
        let Some(head) = head else {
            return Err(HistoryReclaimOutcome::RefusedUnservableWindow);
        };
        if self.journal_index.servable_start().is_none() {
            return Err(HistoryReclaimOutcome::RefusedUnservableWindow);
        }
        // The divergence gate: a below-bound commit the ledger resolves as
        // committed but the journal never recorded has deletion evidence no
        // extraction can recover, so the window is not given up. Sequences
        // below the store's durably declared bound are exempt: a prior pass
        // already extracted their evidence (that is what the bound records),
        // so their absence from the journal is compaction, not divergence —
        // without the exemption a retried pass after a kill between the
        // journal rewrite and completion could never converge.
        let durable_bound = self.retained_index.declared_bound().unwrap_or(0);
        let mut diverged = 0_u64;
        for entry in self.ledger_index.entries() {
            let committed = matches!(
                self.resolution_for(entry),
                super::intent::OperationResolution::Committed { .. }
            );
            if committed
                && entry.sequence < head
                && entry.sequence >= durable_bound
                && !self.journal_index.contains(&entry.tenant, entry.sequence)
            {
                diverged += 1;
            }
        }
        if diverged > 0 {
            return Err(HistoryReclaimOutcome::RefusedDivergence { diverged });
        }
        Ok(head)
    }

    /// Extracts the window's deletion evidence and non-deletion facts,
    /// appends them with the bound record to the retained-evidence store,
    /// and syncs the store and its directory — everything that must be
    /// durable before one journal record may be dropped (D4 ordering 2).
    fn extract_and_retain_evidence(
        &mut self,
        bound: u64,
        head: Option<u64>,
        floor: Option<StorageSequence>,
    ) -> Result<EvidenceExtraction, CompactionError> {
        let window = self.journal_index.commits_below(bound);
        let mut non_deletion: BTreeMap<TenantId, Option<StorageSequence>> = BTreeMap::new();
        let mut extracted = 0_u64;
        let mut below_floor = 0_u64;
        let mut facts: Vec<RetainedFact> = Vec::new();
        for commit in &window {
            let tenant = commit.tenant().clone();
            if commit.writes().iter().any(|write| matches!(write, PublishedWrite::Put { .. })) {
                let entry = non_deletion.entry(tenant.clone()).or_default();
                *entry = (*entry).max(Some(commit.sequence()));
            }
            for write in commit.writes() {
                let PublishedWrite::Delete { collection, id, version_at_delete } = write else {
                    continue;
                };
                extracted += 1;
                let fact = RetainedFact {
                    tenant: tenant.clone(),
                    collection: collection.clone(),
                    id: id.clone(),
                    version_at_delete: *version_at_delete,
                    sequence: commit.sequence(),
                    operation: commit.operation_id().clone(),
                    durability: commit.durability(),
                };
                if floor.is_some_and(|floor| fact.sequence.get() < floor.get()) {
                    below_floor += 1;
                    continue;
                }
                if !self.retained_index.contains_fact(&fact) {
                    facts.push(fact);
                }
            }
        }
        for fact in &facts {
            self.retained.append_fact(fact).map_err(|_| CompactionError::Storage)?;
        }
        let non_deletion = non_deletion.into_iter().collect::<Vec<_>>();
        self.retained
            .append_bound(bound, head.unwrap_or(bound), &non_deletion)
            .map_err(|_| CompactionError::Storage)?;
        if self.faults.fire(FaultPoint::CompactionEvidenceSync).is_err() {
            return Err(CompactionError::Storage);
        }
        self.retained.sync().map_err(|_| CompactionError::Storage)?;
        root::sync_dir(&self.dir).map_err(|_| CompactionError::Storage)?;
        for fact in &facts {
            self.retained_index.upsert_fact(fact.clone());
        }
        let retained = u64::try_from(facts.len()).unwrap_or(u64::MAX);
        Ok(EvidenceExtraction { window, extracted, retained, below_floor })
    }

    /// Rewrites the journal to a leading high-water mark plus its retained
    /// records, atomically, and reopens the writer on the rewritten file.
    fn rewrite_journal(&mut self, bound: u64, head: Option<u64>) -> Result<(), CompactionError> {
        let journal_path = self.dir.join("history");
        let temp_path = self.dir.join("history.tmp");
        let head = head.unwrap_or(bound);
        let mut kept = self.journal_index.commits_at_or_above(bound);
        kept.sort_by_key(|commit| commit.sequence().get());
        let mut temp = fs::File::create(&temp_path).map_err(|_| CompactionError::Storage)?;
        // The leading mark preserves the pre-pass durable head, so the
        // rewritten journal never regresses `journaled_high_water`.
        temp.write_all(&ledger::encode_high_water_frame(head))
            .map_err(|_| CompactionError::Storage)?;
        for commit in &kept {
            temp.write_all(&ledger::encode_commit_frame(commit))
                .map_err(|_| CompactionError::Storage)?;
        }
        temp.sync_data().map_err(|_| CompactionError::Storage)?;
        fs::rename(&temp_path, &journal_path).map_err(|_| CompactionError::Storage)?;
        root::sync_dir(&self.dir).map_err(|_| CompactionError::Storage)?;
        self.journal = HistoryWriter::open(&journal_path).map_err(|_| CompactionError::Storage)?;
        self.journal_index.retain_from(bound);
        self.journal_high_water = Some(self.journal_high_water.map_or(head, |mark| mark.max(head)));
        Ok(())
    }

    /// Reclaims superseded root files: everything with a sequence strictly
    /// below the manifest's, keeping the manifest's own root and any
    /// roll-forward partner at or above it (decision D2).
    fn reclaim_roots(&mut self) -> Result<(u64, u64), CompactionError> {
        if self.faults.fire(FaultPoint::CompactionRootReclaim).is_err() {
            return Err(CompactionError::Storage);
        }
        let manifest_sequence = self.published.sequence;
        let manifest_id = self.published.id.as_str().to_owned();
        let mut removed = 0_u64;
        let mut unremovable = 0_u64;
        if let Ok(entries) = fs::read_dir(self.dir.join("roots")) {
            for entry in entries.flatten() {
                let path = entry.path();
                if path.extension().is_none_or(|extension| extension != "root") {
                    continue;
                }
                let Some(stem) = path.file_stem().and_then(|stem| stem.to_str()) else {
                    continue;
                };
                let Some(id) = root::StorageRootId::from_encoded(stem) else {
                    continue;
                };
                if id.as_str() == manifest_id {
                    continue;
                }
                let Some(sequence) =
                    stem.get(..16).and_then(|prefix| u64::from_str_radix(prefix, 16).ok())
                else {
                    continue;
                };
                if sequence >= manifest_sequence {
                    continue;
                }
                match fs::remove_file(&path) {
                    Ok(()) => removed += 1,
                    Err(_) => unremovable += 1,
                }
            }
        }
        // Stale temporaries from a killed pass are not state.
        let _ = fs::remove_file(self.dir.join("history.tmp"));
        let _ = fs::remove_file(self.dir.join("manifest.tmp"));
        Ok((removed, unremovable))
    }
}

/// The extraction outcome the pass threads into its receipt and driver
/// facts. The window's non-deletion facts are written to the bound record
/// inside the extraction itself (`append_bound`), so they need no threading.
struct EvidenceExtraction {
    window: Vec<PublishedCommit>,
    extracted: u64,
    retained: u64,
    below_floor: u64,
}

/// Emits one bounded progress event when a sink is attached.
fn emit(
    sink: Option<&CompactionProgressSink>,
    phase: CompactionPhase,
    completed: bool,
    units_done: u64,
    units_total: u64,
) {
    if let Some(sink) = sink {
        sink(&CompactionProgress { phase, completed, units_done, units_total });
    }
}

fn window_len_as_u64(window_len: usize) -> u64 {
    u64::try_from(window_len).unwrap_or(u64::MAX)
}

fn file_len(path: &Path) -> u64 {
    fs::metadata(path).map_or(0, |metadata| metadata.len())
}

fn count_root_files(dir: &Path) -> u64 {
    let Ok(entries) = fs::read_dir(dir.join("roots")) else {
        return 0;
    };
    let count = entries
        .flatten()
        .filter(|entry| entry.path().extension().is_some_and(|extension| extension == "root"))
        .count();
    u64::try_from(count).unwrap_or(u64::MAX)
}
