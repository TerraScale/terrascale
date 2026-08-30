//! The private file-backed storage adapter: open, recovery, and publication.

use std::{
    collections::BTreeMap,
    fs,
    io::Write,
    path::{Path, PathBuf},
    sync::Arc,
};

use crate::types::{
    CollectionName, Document, DocumentId, ExpectedVersion, LocalDurability, OperationId,
    StorageSequence, TenantId,
};

use super::{PublishedCommit, PublishedWrite};
use super::{
    codec::{self, LocalDurabilityCode, StorageKey},
    compaction::{
        CompactionDriverFacts, CompactionError, CompactionReceipt, RetainedState, StorageCompaction,
    },
    faults::{FaultPlan, FaultPoint},
    frontier::{
        ActiveFormat, FRONTIER_FORMAT_MAJOR, FrontierError, LEGACY_FORMAT_MAJOR, LedgerFrontier,
        LedgerFrontierActivationError, LedgerFrontierActivationIdentity,
        LedgerFrontierActivationReceipt, LedgerFrontierActivationRequest, LedgerFrontierStatus,
        STORAGE_FRONTIER_FEATURE, StoreId, VerifiedAnchors,
    },
    intent::{
        CommitIntent, CommitRejection, CommitResult, OperationResolution, StagedWrite,
        StorageReceipt, UnknownCause,
    },
    ledger::{
        HistoryIndex, HistoryWriter, LedgerIndex, LedgerRecord, LedgerState, LedgerWriter,
        OperationEntry, Published, ReadyToPublish, RejectedRecord,
    },
    retained::{RetainedAppend, RetainedIndex, RetainedWriter},
    root::{self, RootTable, StorageRoot, StorageRootId},
    wal::{self, WalCommit, WalWriter},
};

/// Magic bytes identifying the V2 storage format.
const FORMAT_MAGIC: &[u8; 10] = b"TBV2FORMAT";

/// Domain separating the checksummed activated format record.
const FORMAT_CHECKSUM_DOMAIN: &[u8] = b"terrabase.root-format.v2\0";

const FORMAT_V2_RESERVED_BYTES: usize = 8;

enum FormatState {
    Legacy,
    Active(ActiveFormat),
}

struct FormatOpen {
    state: FormatState,
    created: bool,
}

/// Why a durable location could not be opened.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum StorageOpenError {
    /// The location does not hold the expected storage format.
    FormatMismatch,
    /// The location could not be opened.
    Unavailable,
}

/// The private file adapter implementing [`super::StoragePort`].
///
/// The fields are `pub(super)`-visible so the compaction pass
/// (`super::compaction`, GAP-S1) drives the same writers and indexes the
/// publication order uses — one implementation of the format, no duplicate
/// handles.
pub(crate) struct FileStorage {
    pub(super) dir: PathBuf,
    pub(super) wal: WalWriter,
    pub(super) ledger: LedgerWriter,
    pub(super) ledger_index: LedgerIndex,
    /// The format-1 shadow or format-2 mandatory independent ledger anchor.
    ledger_frontier: Option<LedgerFrontier>,
    format_major: u64,
    activation: Option<LedgerFrontierActivationIdentity>,
    pub(super) frontier_poisoned: bool,
    /// The durable publication-history journal and its sequence-ordered
    /// index (W4-CA-4), with the durable high-water mark (repair F2).
    pub(super) journal: HistoryWriter,
    pub(super) journal_index: HistoryIndex,
    pub(super) journal_high_water: Option<u64>,
    /// The durable retained-evidence store and its index (GAP-S1 D3): the
    /// deletion facts and bound declarations that keep the tombstone
    /// retention watermark honest after history is given up.
    pub(super) retained: RetainedWriter,
    pub(super) retained_index: RetainedIndex,
    pub(super) published: StorageRoot,
    pub(super) history: BTreeMap<u64, Arc<RootTable>>,
    /// The highest storage sequence ever named in this location, whether or
    /// not its commit published. New commits allocate past it, so a sequence
    /// is never reused after a mid-publication failure.
    pub(super) sequence_watermark: u64,
    /// A verified WAL commit exists but has not moved the published pointer
    /// in this process. A later commit must not branch from the older table.
    pub(super) publication_blocked: bool,
    pub(super) faults: FaultPlan,
}

impl FileStorage {
    /// Opens or creates the store at `dir` and recovers it.
    #[allow(clippy::too_many_lines, reason = "recovery ordering is one fail-closed transaction")]
    pub(crate) fn open(dir: &Path) -> Result<Self, StorageOpenError> {
        fs::create_dir_all(dir).map_err(|_| StorageOpenError::Unavailable)?;
        let format = verify_or_create_format(dir)?;
        if format.created {
            root::sync_dir(dir).map_err(|_| StorageOpenError::Unavailable)?;
        }
        let wal_path = dir.join("wal");
        let ledger_path = dir.join("ledger");
        let journal_path = dir.join("history");
        let retained_path = dir.join("retained");
        let ledger_exists = ledger_path.try_exists().map_err(|_| StorageOpenError::Unavailable)?;
        let retained_exists =
            retained_path.try_exists().map_err(|_| StorageOpenError::Unavailable)?;
        if !format.created && !ledger_exists {
            // The operation ledger is part of every existing root-format
            // location, including an empty one. Recreating it would discard
            // idempotency and rejection bindings while leaving published
            // state in place.
            return Err(StorageOpenError::FormatMismatch);
        }
        if matches!(&format.state, FormatState::Active(_)) && !retained_exists {
            return Err(StorageOpenError::FormatMismatch);
        }

        // Validate the complete WAL before truncating or recovering any other
        // artifact. Hostile but checksummed commit groups must not cause root,
        // manifest, ledger, or retained-store side effects.
        let report = wal::scan(&wal_path).map_err(|_| StorageOpenError::FormatMismatch)?;
        let mut wal_writer =
            WalWriter::open(&wal_path).map_err(|_| StorageOpenError::Unavailable)?;
        let mut ledger_writer =
            LedgerWriter::open(&ledger_path).map_err(|_| StorageOpenError::Unavailable)?;
        let mut journal_writer =
            HistoryWriter::open(&journal_path).map_err(|_| StorageOpenError::Unavailable)?;
        let mut retained_writer =
            RetainedWriter::open(&retained_path).map_err(|_| StorageOpenError::Unavailable)?;
        if wal_writer.is_fresh()
            || ledger_writer.is_fresh()
            || journal_writer.is_fresh()
            || retained_writer.is_fresh()
        {
            root::sync_dir(dir).map_err(|_| StorageOpenError::Unavailable)?;
        }
        let ledger_scan =
            LedgerIndex::scan(&ledger_path).map_err(|_| StorageOpenError::Unavailable)?;
        if ledger_scan.mid_file_damage {
            // Losing a later operation binding could make recovery execute
            // already accepted work under the same identity. Mid-file
            // damage therefore refuses the location instead of accepting a
            // partial index.
            return Err(StorageOpenError::FormatMismatch);
        }
        let journal_scan = HistoryIndex::scan(&journal_path);
        let retained_scan = RetainedIndex::scan(&retained_path);
        if retained_scan.mid_file_damage {
            // GAP-S1 D3: the retained-evidence store backs every declared
            // below-bound history bound. Mid-file damage makes that
            // evidence unreadable, so the open fails closed rather than
            // silently serving a prefix — the same discipline the WAL
            // applies to damage with bytes behind it.
            return Err(StorageOpenError::FormatMismatch);
        }
        let (format_major, activation, mut ledger_frontier) = match &format.state {
            FormatState::Legacy => (LEGACY_FORMAT_MAJOR, None, None),
            FormatState::Active(active) => {
                let verified = VerifiedAnchors::from_scans(
                    &ledger_path,
                    &ledger_scan,
                    &retained_path,
                    &retained_scan,
                    active.store_id,
                )
                .map_err(|_| StorageOpenError::Unavailable)?;
                let frontier = LedgerFrontier::open_active(dir, active.store_id, &verified)
                    .map_err(map_frontier_open_error)?;
                (FRONTIER_FORMAT_MAJOR, Some(active.activation.clone()), Some(frontier))
            }
        };

        if ledger_scan.total_bytes > ledger_scan.good_bytes {
            ledger_writer
                .truncate(ledger_scan.good_bytes)
                .map_err(|_| StorageOpenError::Unavailable)?;
        }
        if retained_scan.total_bytes > retained_scan.good_bytes {
            retained_writer
                .truncate(retained_scan.good_bytes)
                .map_err(|_| StorageOpenError::Unavailable)?;
        }
        if report.torn_tail {
            wal_writer.truncate(report.good_bytes).map_err(|_| StorageOpenError::Unavailable)?;
        }
        if journal_scan.total_bytes > journal_scan.good_bytes {
            journal_writer
                .truncate(journal_scan.good_bytes)
                .map_err(|_| StorageOpenError::Unavailable)?;
        }

        if matches!(&format.state, FormatState::Legacy) {
            let store_id = StoreId::generate(dir);
            let verified = VerifiedAnchors::from_scans(
                &ledger_path,
                &ledger_scan,
                &retained_path,
                &retained_scan,
                store_id,
            )
            .map_err(|_| StorageOpenError::Unavailable)?;
            ledger_frontier = Some(
                LedgerFrontier::rebuild_shadow(dir, store_id, verified.final_anchor())
                    .map_err(map_frontier_open_error)?,
            );
        }

        let mut storage = Self {
            dir: dir.to_path_buf(),
            wal: wal_writer,
            ledger: ledger_writer,
            ledger_index: ledger_scan.index,
            ledger_frontier,
            format_major,
            activation,
            frontier_poisoned: false,
            journal: journal_writer,
            journal_index: journal_scan.index,
            journal_high_water: journal_scan.high_water,
            retained: retained_writer,
            retained_index: retained_scan.index,
            published: StorageRoot::empty(),
            history: BTreeMap::new(),
            sequence_watermark: 0,
            publication_blocked: false,
            faults: FaultPlan::empty(),
        };
        storage.recover(report.commits)?;
        if storage.ledger_index.entries().any(|entry| {
            entry.state == LedgerState::Ready
                && entry.sequence > storage.published.sequence
                && entry.requested.to_level() != LocalDurability::Buffered
        }) {
            // Written and Synced Ready records claim durable root/WAL work.
            // If neither recovery source can reach that sequence, accepting a
            // pending prefix would hide acknowledged durable loss. Buffered
            // Ready records have no WAL group and may remain pending.
            return Err(StorageOpenError::FormatMismatch);
        }
        if storage.published.sequence != 0
            && !storage
                .ledger_index
                .binds_published_root(storage.published.sequence, storage.published.id.as_str())
        {
            // The manifest/root head is durable publication evidence even
            // when the best-effort history append was skipped. Its exact
            // Ready or Published binding must survive once WAL recovery has
            // had the chance to reconstruct it.
            return Err(StorageOpenError::FormatMismatch);
        }
        if !storage.journal_index.all_commits_bound_in(&storage.ledger_index) {
            // Recovery first gets a chance to reconstruct a missing binding
            // from a verified WAL commit. Anything still journaled without
            // its `Ready` or `Published` entry proves exact frame-boundary
            // ledger loss, which a prefix checksum scan alone cannot see.
            return Err(StorageOpenError::FormatMismatch);
        }
        Ok(storage)
    }

    /// Rebuilds the published root and rolls every verified write-ahead commit
    /// forward.
    ///
    /// A manifest whose checksum or root identity does not verify is ignored,
    /// and the published state is rebuilt from the verified commits instead.
    /// Prepare groups without a valid commit marker were already discarded by
    /// the scan, so they can never publish a root here.
    fn recover(&mut self, commits: Vec<WalCommit>) -> Result<(), StorageOpenError> {
        let mut table = RootTable::empty();
        let mut sequence = 0_u64;
        let mut id = StorageRoot::empty().id;
        let mut manifest_verified = false;
        if let Some((manifest_sequence, manifest_id)) = root::read_manifest(&self.dir) {
            let path = root::root_file_path(&self.dir, &manifest_id);
            if let Ok(Some((root_sequence, root_table, confirmed_id))) = root::load_root_file(&path)
                && root_sequence == manifest_sequence
                && confirmed_id == manifest_id
            {
                table = root_table;
                sequence = manifest_sequence;
                id = manifest_id;
                manifest_verified = true;
            }
        }
        if !manifest_verified {
            // GAP-S1 D7: the manifest is absent or does not verify and the
            // write-ahead log may no longer carry the frames to rebuild
            // from (a compacted location truncates them at its checkpoint).
            // Base the rebuild on the highest-sequence root file that
            // verifies instead. Compaction never deletes the manifest's own
            // root, so at least one verifying candidate exists whenever
            // anything was ever published.
            match root::scan_newest_verifying_root(&self.dir) {
                Ok(Some((root_sequence, root_table, confirmed_id))) => {
                    table = root_table;
                    sequence = root_sequence;
                    id = confirmed_id;
                }
                Ok(None) => {}
                Err(()) => return Err(StorageOpenError::Unavailable),
            }
        }
        // The watermark starts past every sequence any durable record names,
        // so a fresh commit never reuses the sequence of a failed or
        // half-published group.
        self.sequence_watermark = sequence;
        for entry in self.ledger_index.entries() {
            self.sequence_watermark = self.sequence_watermark.max(entry.sequence);
        }
        for commit in &commits {
            self.sequence_watermark = self.sequence_watermark.max(commit.sequence);
        }
        for commit in commits {
            if commit.sequence <= sequence {
                self.reconcile_covered_commit(&commit, sequence, &id)?;
                continue;
            }
            let tenant =
                TenantId::try_new(&commit.tenant).map_err(|_| StorageOpenError::FormatMismatch)?;
            let applied = table.apply(&tenant, &commit.writes);
            let expected_id = root::root_identity_of(commit.sequence, &applied);
            if expected_id.as_str() != commit.root {
                // WAL commits form one publication chain. A later commit
                // created from an older table is a sibling branch, not a
                // successor that recovery may silently rewrite.
                return Err(StorageOpenError::FormatMismatch);
            }
            // W4-CA-4: recovery re-derives the reconstructable write set from
            // the tables the roll-forward moved and appends the missing
            // journal record, so a crash between pointer publication and the
            // journal append still leaves the entry durable.
            //
            // F4: a roll-forward entry is annotated `Written`: this path
            // wrote and verified the root bytes but never observed the
            // commit's own post-publication syncs, so it cannot prove more.
            if !self.journal_index.contains(commit.tenant.as_str(), commit.sequence) {
                let record = PublishedCommit::new(
                    tenant.clone(),
                    OperationId::try_new(&commit.operation)
                        .map_err(|_| StorageOpenError::FormatMismatch)?,
                    StorageSequence::new(commit.sequence),
                    LocalDurability::Written,
                    published_writes(&table, &applied, &tenant, &commit.writes),
                );
                if self.journal.append(&record).and_then(|()| self.journal.sync()).is_ok() {
                    self.journal_index.upsert(record);
                    self.advance_journal_high_water(commit.sequence);
                }
            }
            table = applied;
            sequence = commit.sequence;
            let published_id = root::write_root_file(&self.dir, sequence, &table, false)
                .map_err(|_| StorageOpenError::Unavailable)?;
            root::write_manifest_temp(&self.dir, sequence, &published_id, false)
                .map_err(|_| StorageOpenError::Unavailable)?;
            root::publish_manifest(&self.dir).map_err(|_| StorageOpenError::Unavailable)?;
            self.history.insert(sequence, Arc::new(table.clone()));
            self.record_publication(
                &commit.tenant,
                &commit.operation,
                commit.digest,
                &published_id,
                sequence,
                false,
            )?;
            id = published_id;
        }
        let shared = Arc::new(table);
        self.history.insert(sequence, Arc::clone(&shared));
        self.published = StorageRoot { id, sequence, table: shared };
        // The manifest names only the current root. Rehydrate every verified
        // retained root so an earlier snapshot boundary remains readable
        // after restart; compaction removes roots below its retained floor.
        if let Ok(roots) = root::scan_verifying_roots(&self.dir) {
            for (root_sequence, root_table, _) in roots {
                if root_sequence <= sequence {
                    self.history.entry(root_sequence).or_insert_with(|| Arc::new(root_table));
                }
            }
        }
        Ok(())
    }

    /// Reconciles a WAL commit at or below the recovered root without
    /// assuming that a higher manifest proves every earlier branch.
    fn reconcile_covered_commit(
        &mut self,
        commit: &WalCommit,
        published_sequence: u64,
        published_id: &StorageRootId,
    ) -> Result<(), StorageOpenError> {
        let exact_head =
            commit.sequence == published_sequence && commit.root == published_id.as_str();
        let ledger_proves_publication = self.ledger_index.binds_published_commit(
            &commit.tenant,
            &commit.operation,
            commit.digest,
            commit.sequence,
            &commit.root,
        );
        let journal_proves_publication = self.journal_index.binds_publication(
            &commit.tenant,
            &commit.operation,
            commit.sequence,
        );
        if !exact_head && !ledger_proves_publication && !journal_proves_publication {
            // A higher manifest may be a sibling root created from the table
            // before this commit. Claiming the commit would then report data
            // that the published root does not contain.
            return Err(StorageOpenError::FormatMismatch);
        }
        let commit_root =
            StorageRootId::from_encoded(&commit.root).ok_or(StorageOpenError::FormatMismatch)?;
        // The journal is deliberately not reconstructed here. Without the
        // pre-commit table, inventing a write set would poison retained
        // evidence. Missing history remains visible to the divergence gate.
        self.record_publication(
            &commit.tenant,
            &commit.operation,
            commit.digest,
            &commit_root,
            commit.sequence,
            false,
        )?;
        Ok(())
    }

    /// Persists one semantically valid ledger transition and advances the
    /// independent frontier before any in-memory binding becomes visible.
    fn persist_ledger_record(&mut self, record: &LedgerRecord) -> Result<(), FrontierError> {
        if self.frontier_poisoned || !self.ledger_index.can_apply(record) {
            return Err(FrontierError::Integrity);
        }
        let Ok(append) = self.ledger.append(record) else {
            self.frontier_poisoned = true;
            self.publication_blocked = true;
            return Err(FrontierError::Unavailable);
        };
        if self.ledger.sync().is_err() {
            self.frontier_poisoned = true;
            self.publication_blocked = true;
            return Err(FrontierError::Unavailable);
        }
        let record_fault = match record {
            LedgerRecord::Ready(_) => FaultPoint::ReadyFrontierSync,
            LedgerRecord::Published(_) => FaultPoint::PublishedFrontierSync,
            LedgerRecord::Rejected(_) => FaultPoint::RejectedFrontierSync,
        };
        if self.faults.fire(FaultPoint::StorageFrontierSync).is_err()
            || self.faults.fire(record_fault).is_err()
        {
            self.frontier_poisoned = true;
            self.publication_blocked = true;
            return Err(FrontierError::Unavailable);
        }
        let frontier_result = self
            .ledger_frontier
            .as_mut()
            .ok_or(FrontierError::Integrity)
            .and_then(|frontier| frontier.advance_ledger(&append));
        if let Err(error) = frontier_result {
            self.frontier_poisoned = true;
            self.publication_blocked = true;
            return Err(error);
        }
        Ok(())
    }

    /// Syncs a completed retained-evidence batch, then binds every appended
    /// frame in one frontier generation before compaction may drop history.
    pub(super) fn persist_retained_appends(
        &mut self,
        appends: &[RetainedAppend],
    ) -> Result<(), FrontierError> {
        if self.frontier_poisoned {
            return Err(FrontierError::Integrity);
        }
        if self.retained.sync().is_err() {
            self.frontier_poisoned = true;
            self.publication_blocked = true;
            return Err(FrontierError::Unavailable);
        }
        if self.faults.fire(FaultPoint::StorageFrontierSync).is_err()
            || self.faults.fire(FaultPoint::RetainedFrontierSync).is_err()
        {
            self.frontier_poisoned = true;
            self.publication_blocked = true;
            return Err(FrontierError::Unavailable);
        }
        let result = self
            .ledger_frontier
            .as_mut()
            .ok_or(FrontierError::Integrity)
            .and_then(|frontier| frontier.advance_retained(appends));
        if let Err(error) = result {
            self.frontier_poisoned = true;
            self.publication_blocked = true;
            return Err(error);
        }
        Ok(())
    }

    pub(super) fn poison_storage_frontier(&mut self) {
        self.frontier_poisoned = true;
        self.publication_blocked = true;
    }

    /// Appends a `Published` ledger record during recovery, without claiming
    /// the post-publication syncs.
    fn record_publication(
        &mut self,
        tenant: &str,
        operation: &str,
        digest: u64,
        id: &StorageRootId,
        sequence: u64,
        post_publish_synced: bool,
    ) -> Result<(), StorageOpenError> {
        if self.ledger_index.binds_published_commit(
            tenant,
            operation,
            digest,
            sequence,
            id.as_str(),
        ) {
            return Ok(());
        }
        if self.ledger_index.entry_by_text(tenant, operation).is_some_and(|entry| {
            let exact_ready = entry.state == LedgerState::Ready
                && entry.digest == digest
                && entry.sequence == sequence
                && entry.root == id.as_str();
            let exact_legacy_publication = matches!(entry.state, LedgerState::Published { .. })
                && !entry.digest_known
                && entry.sequence == sequence
                && entry.root == id.as_str();
            !exact_ready && !exact_legacy_publication
        }) {
            return Err(StorageOpenError::FormatMismatch);
        }
        let record = LedgerRecord::Published(Published {
            tenant: tenant.to_owned(),
            operation: operation.to_owned(),
            digest,
            digest_known: true,
            root: id.as_str().to_owned(),
            sequence,
            post_publish_synced,
        });
        self.persist_ledger_record(&record).map_err(map_frontier_open_error)?;
        if !self.ledger_index.apply(record) {
            return Err(StorageOpenError::FormatMismatch);
        }
        Ok(())
    }

    /// Runs the storage publication order for one validated intent.
    #[allow(clippy::too_many_lines)]
    fn commit_intent(&mut self, intent: &CommitIntent) -> CommitResult {
        // Step 1: validate request, scope, operation identity, and conflicts
        // without changing the published root. A validation failure is a
        // definite synchronous answer and never binds the identity durably;
        // only a conflict, which follows validated write work, is recorded as
        // a durable rejection.
        if intent.writes.is_empty() || codec::staged_writes_have_duplicate_targets(&intent.writes) {
            return CommitResult::Rejected(CommitRejection::Validation);
        }
        if let Some(entry) = self.ledger_index.entry(&intent.tenant, &intent.operation) {
            if !entry.digest_known || entry.digest != intent.digest {
                return CommitResult::Rejected(CommitRejection::OperationIdReuse);
            }
            return self.replay(entry);
        }
        if self.frontier_poisoned {
            return CommitResult::Rejected(CommitRejection::Storage);
        }
        if self.publication_blocked {
            // A verified earlier commit still needs recovery. Publishing a
            // different operation from the current table would create a
            // sibling root and make one of the two write sets disappear.
            return CommitResult::Rejected(CommitRejection::Storage);
        }
        let snapshot_table = self.table_at(intent.snapshot_sequence);
        let published_table = Arc::clone(&self.published.table);
        for write in &intent.writes {
            if let StagedWrite::Put { document, .. } = write
                && !codec::document_within_limits(document)
            {
                return CommitResult::Rejected(CommitRejection::Validation);
            }
            let mismatch = match write {
                StagedWrite::Put { document, expected } => {
                    let key = StorageKey::new(&intent.tenant, document.collection(), document.id());
                    Self::conflict_between(&snapshot_table, &published_table, &key, *expected)
                }
                StagedWrite::Delete { collection: Some(collection), id, expected } => {
                    let key = StorageKey::new(&intent.tenant, collection, id);
                    Self::conflict_between(&snapshot_table, &published_table, &key, *expected)
                }
                StagedWrite::Delete { collection: None, id, expected } => {
                    Self::conflict_for_absent_document(
                        &snapshot_table,
                        &published_table,
                        &intent.tenant,
                        id,
                        *expected,
                    )
                }
            };
            if mismatch {
                return self.record_rejection(intent, CommitRejection::Conflict);
            }
        }

        // Step 2: encode the write set into private frames. Raw bytes exist
        // only from here through the private writers below. The sequence is
        // allocated past every sequence this location has ever named and the
        // watermark advances immediately, so a later failure in this commit
        // can never let another commit reuse this sequence.
        let Some(sequence) = self.sequence_watermark.checked_add(1) else {
            return CommitResult::Rejected(CommitRejection::Storage);
        };
        self.sequence_watermark = sequence;
        let durable_frames = intent.durability != LocalDurability::Buffered;
        let new_table = self.published.table.apply(&intent.tenant, &intent.writes);
        let root_id = root::root_identity_of(sequence, &new_table);
        let commit_marker = format!("{}:{sequence}", intent.operation.as_str());

        // Step 3: append the prepare record and the write frames.
        if durable_frames {
            let mut prepare = Vec::new();
            codec::push_str(&mut prepare, intent.tenant.as_str());
            codec::push_str(&mut prepare, intent.operation.as_str());
            codec::push_u64(&mut prepare, intent.digest);
            codec::push_u64(&mut prepare, sequence);
            #[allow(clippy::cast_possible_truncation)]
            let write_count = intent.writes.len() as u32;
            codec::push_u32(&mut prepare, write_count);
            if self.wal.append(wal::FRAME_PREPARE, &prepare).is_err() {
                return CommitResult::Rejected(CommitRejection::Storage);
            }
            for write in &intent.writes {
                let mut payload = Vec::new();
                codec::encode_staged_write(&mut payload, write);
                if self.wal.append(wal::FRAME_DATA, &payload).is_err() {
                    return CommitResult::Rejected(CommitRejection::Storage);
                }
            }
        }

        // Step 4: apply the write set to a new private root and append the
        // commit marker. The published root stays readable throughout.
        if self.faults.fire(FaultPoint::CommitMarkerApplication).is_err() {
            return CommitResult::Rejected(CommitRejection::Storage);
        }
        if durable_frames {
            let marker = wal::encode_commit_marker(
                intent.tenant.as_str(),
                intent.operation.as_str(),
                intent.digest,
                sequence,
                root_id.as_str(),
            );
            if self.wal.append(wal::FRAME_COMMIT_MARKER, &marker).is_err() {
                return CommitResult::Rejected(CommitRejection::Storage);
            }
            self.publication_blocked = true;
        }

        // Step 5: the requested local durability action.
        let write_ok = match intent.durability {
            LocalDurability::Buffered => Ok(()),
            LocalDurability::Written => {
                root::write_root_file(&self.dir, sequence, &new_table, false)
                    .map(|_| ())
                    .and_then(|()| root::write_manifest_temp(&self.dir, sequence, &root_id, false))
            }
            LocalDurability::Synced => self
                .wal
                .sync()
                .and_then(|()| root::write_root_file(&self.dir, sequence, &new_table, true))
                .map(|_| ())
                .and_then(|()| root::write_manifest_temp(&self.dir, sequence, &root_id, true)),
        };
        if write_ok.is_err() {
            return if durable_frames {
                CommitResult::Unknown(UnknownCause::ResponseInterrupted)
            } else {
                CommitResult::Rejected(CommitRejection::Storage)
            };
        }

        // Step 6: append and durably sync the ReadyToPublish record. This
        // sync persists resolution metadata for every durability level.
        if self.faults.fire(FaultPoint::ReadyLedgerSync).is_err() {
            return if durable_frames {
                CommitResult::Unknown(UnknownCause::ResponseInterrupted)
            } else {
                CommitResult::Rejected(CommitRejection::Storage)
            };
        }
        let ready = LedgerRecord::Ready(ReadyToPublish {
            tenant: intent.tenant.as_str().to_owned(),
            operation: intent.operation.as_str().to_owned(),
            digest: intent.digest,
            sequence,
            commit_marker,
            root: root_id.as_str().to_owned(),
            requested: LocalDurabilityCode::of(intent.durability),
        });
        if self.persist_ledger_record(&ready).is_err() {
            return if durable_frames {
                CommitResult::Unknown(UnknownCause::ResponseInterrupted)
            } else {
                CommitResult::Rejected(CommitRejection::Storage)
            };
        }
        if !self.ledger_index.apply(ready) {
            self.publication_blocked = true;
            return if durable_frames {
                CommitResult::Unknown(UnknownCause::ResponseInterrupted)
            } else {
                CommitResult::Rejected(CommitRejection::Storage)
            };
        }
        self.publication_blocked = true;

        // Step 7: atomically publish the root pointer.
        if self.faults.fire(FaultPoint::PointerPublish).is_err() {
            return if durable_frames {
                CommitResult::Unknown(UnknownCause::ResponseInterrupted)
            } else {
                CommitResult::Rejected(CommitRejection::Storage)
            };
        }
        if durable_frames && root::publish_manifest(&self.dir).is_err() {
            return CommitResult::Unknown(UnknownCause::ResponseInterrupted);
        }
        let shared = Arc::new(new_table);
        self.history.insert(sequence, Arc::clone(&shared));
        self.published = StorageRoot { id: root_id.clone(), sequence, table: shared };

        // For Synced, the published pointer and its containing directory are
        // synced after publication, before any Synced claim.
        if intent.durability == LocalDurability::Synced {
            if self.faults.fire(FaultPoint::PointerSync).is_err() {
                return CommitResult::Unknown(UnknownCause::PostPublicationPointerSync);
            }
            if root::sync_file(&self.dir.join("manifest")).is_err() {
                return CommitResult::Unknown(UnknownCause::PostPublicationPointerSync);
            }
            if self.faults.fire(FaultPoint::DirectorySync).is_err() {
                return CommitResult::Unknown(UnknownCause::PostPublicationDirectorySync);
            }
            if root::sync_dir(&self.dir).is_err() {
                return CommitResult::Unknown(UnknownCause::PostPublicationDirectorySync);
            }
        }

        // W4-CA-4: durably record the reconstructable write set in the
        // publication-history journal. The commit has already published, so
        // the entry can only ever describe published state; the append runs
        // before the `Published` ledger record so the uncertain path of a
        // failed published-ledger sync still leaves the entry recorded.
        //
        // Repair F2: the append is best-effort and injectably fallible. A
        // failed append must never delay or fail the published commit; it
        // leaves the divergence observable instead — the durable high-water
        // mark stays at the last successful append while the published
        // sequence advances, and no consumer can mistake that for a legal
        // sequence hole.
        let journal_record = PublishedCommit::new(
            intent.tenant.clone(),
            intent.operation.clone(),
            StorageSequence::new(sequence),
            intent.durability,
            published_writes(
                &published_table,
                &self.published.table,
                &intent.tenant,
                &intent.writes,
            ),
        );
        if self.faults.fire(FaultPoint::JournalAppend).is_err() {
            // Injected append failure (test-only): skip journaling entirely
            // and let the high-water mark report the skipped sequence.
        } else if self.journal.append(&journal_record).and_then(|()| self.journal.sync()).is_ok() {
            self.journal_index.upsert(journal_record);
            self.advance_journal_high_water(sequence);
        }

        // Step 8: append and durably sync the Published record.
        if self.faults.fire(FaultPoint::PublishedLedgerSync).is_err() {
            return CommitResult::Unknown(UnknownCause::PublishedLedgerSync);
        }
        let published = LedgerRecord::Published(Published {
            tenant: intent.tenant.as_str().to_owned(),
            operation: intent.operation.as_str().to_owned(),
            digest: intent.digest,
            digest_known: true,
            root: root_id.as_str().to_owned(),
            sequence,
            post_publish_synced: intent.durability == LocalDurability::Synced,
        });
        if self.persist_ledger_record(&published).is_err() {
            return CommitResult::Unknown(UnknownCause::PublishedLedgerSync);
        }
        if !self.ledger_index.apply(published) {
            self.publication_blocked = true;
            return CommitResult::Unknown(UnknownCause::PublishedLedgerSync);
        }
        self.publication_blocked = false;

        // Step 9: return only the guarantees of the requested level.
        CommitResult::Committed(StorageReceipt {
            sequence: StorageSequence::new(sequence),
            root: root_id,
            durability: intent.durability,
        })
    }

    /// Checks one keyed write against the snapshot and the published view.
    fn conflict_between(
        snapshot_table: &RootTable,
        published_table: &RootTable,
        key: &StorageKey,
        expected: ExpectedVersion,
    ) -> bool {
        let snapshot_version = snapshot_table.version(key);
        let published_version = published_table.version(key);
        if snapshot_version != published_version {
            return true;
        }
        !matches_expectation(snapshot_version, expected)
    }

    /// Checks a delete of an identifier the snapshot does not contain.
    fn conflict_for_absent_document(
        snapshot_table: &RootTable,
        published_table: &RootTable,
        tenant: &TenantId,
        id: &DocumentId,
        expected: ExpectedVersion,
    ) -> bool {
        let in_snapshot = snapshot_table.find_by_document_id(tenant, id).is_some();
        let in_published = published_table.find_by_document_id(tenant, id).is_some();
        if in_snapshot != in_published {
            return true;
        }
        if in_published {
            return true;
        }
        !matches_expectation(None, expected)
    }

    /// Replays the recorded durable resolution for a retried identity.
    fn replay(&self, entry: &OperationEntry) -> CommitResult {
        match self.resolution_for(entry) {
            OperationResolution::Committed { sequence, durability } => {
                CommitResult::Committed(StorageReceipt {
                    sequence,
                    root: StorageRootId::from_recorded(entry.root.clone()),
                    durability,
                })
            }
            OperationResolution::Rejected(reason) => CommitResult::Rejected(reason),
            OperationResolution::Pending => {
                CommitResult::Unknown(UnknownCause::ResponseInterrupted)
            }
        }
    }

    /// Computes the durable resolution of one operation identity.
    pub(super) fn resolution_for(&self, entry: &OperationEntry) -> OperationResolution {
        let requested = entry.requested.to_level();
        match entry.state {
            LedgerState::Published { post_publish_synced } => {
                let proven = if post_publish_synced {
                    LocalDurability::Synced
                } else {
                    LocalDurability::Written
                };
                OperationResolution::Committed {
                    sequence: StorageSequence::new(entry.sequence),
                    durability: min_level(requested, proven),
                }
            }
            LedgerState::Ready => {
                if !entry.root.is_empty() && self.published.id.as_str() == entry.root {
                    OperationResolution::Committed {
                        sequence: StorageSequence::new(entry.sequence),
                        durability: min_level(requested, LocalDurability::Written),
                    }
                } else {
                    OperationResolution::Pending
                }
            }
            LedgerState::Rejected(reason) => OperationResolution::Rejected(reason),
        }
    }

    /// Records one conflict rejection durably, then reports it.
    ///
    /// Only conflict rejections are recorded: they follow validated write work
    /// and a later resolution must not misread them as pending. Validation and
    /// storage failures are definite synchronous answers or may still be
    /// rolled forward from the write-ahead log, so they never bind the
    /// identity to a rejection.
    fn record_rejection(&mut self, intent: &CommitIntent, reason: CommitRejection) -> CommitResult {
        let record = LedgerRecord::Rejected(RejectedRecord {
            tenant: intent.tenant.as_str().to_owned(),
            operation: intent.operation.as_str().to_owned(),
            digest: intent.digest,
            reason,
        });
        if self.persist_ledger_record(&record).is_err() {
            return CommitResult::Rejected(CommitRejection::Storage);
        }
        if !self.ledger_index.apply(record) {
            return CommitResult::Rejected(CommitRejection::Storage);
        }
        CommitResult::Rejected(reason)
    }

    /// Returns the immutable table a read snapshot observes.
    fn table_at(&self, sequence: StorageSequence) -> Arc<RootTable> {
        self.history
            .range(..=sequence.get())
            .next_back()
            .map_or_else(|| Arc::clone(&self.published.table), |(_, table)| Arc::clone(table))
    }

    /// Durably records and advances the journal high-water mark (repair F2).
    ///
    /// The mark record is best-effort: a failed mark append leaves the
    /// in-memory mark advanced — the commit record itself is already durable
    /// — and the next open re-derives the same value from the verified
    /// commit records, so the mark never over-reports completeness.
    fn advance_journal_high_water(&mut self, sequence: u64) {
        self.journal_high_water =
            Some(self.journal_high_water.map_or(sequence, |mark| mark.max(sequence)));
        let _ = self.journal.append_high_water(sequence).and_then(|()| self.journal.sync());
    }
}

fn matches_expectation(version: Option<u64>, expected: ExpectedVersion) -> bool {
    match expected {
        ExpectedVersion::Any => true,
        ExpectedVersion::New => version.is_none(),
        ExpectedVersion::Exact(exact) => version == Some(exact),
    }
}

/// Derives the reconstructable published write set of one commit from the
/// tables that commit moved (W4-CA-4).
///
/// A put carries the published document — the after-table's form, with the
/// version the commit assigned — and a delete carries the identifier with
/// the version the before-table held (`0` when the identifier was absent).
/// Both the commit path and the recovery roll-forward call this, so the
/// journal records one shape regardless of which path wrote it.
fn published_writes(
    before: &RootTable,
    after: &RootTable,
    tenant: &TenantId,
    writes: &[StagedWrite],
) -> Vec<PublishedWrite> {
    writes
        .iter()
        .map(|write| match write {
            StagedWrite::Put { document, .. } => {
                let key = StorageKey::new(tenant, document.collection(), document.id());
                let published = after.document(&key).unwrap_or_else(|| document.clone());
                PublishedWrite::Put { document: published }
            }
            // Repair F3: the version comes from the exact key the delete
            // removes, never from the first canonical occurrence of the
            // identifier. A collection-carrying delete names its key
            // directly; a collection-less delete can only commit when the
            // identifier is absent everywhere, so its version is zero and a
            // replay removes nothing.
            StagedWrite::Delete { collection, id, .. } => {
                let version_at_delete = match collection {
                    Some(collection) => before
                        .document(&StorageKey::new(tenant, collection, id))
                        .map_or(0, |deleted| deleted.metadata().version()),
                    None => 0,
                };
                PublishedWrite::Delete {
                    collection: collection.clone(),
                    id: id.clone(),
                    version_at_delete,
                }
            }
        })
        .collect()
}

/// Returns the weaker of two local durability levels.
pub(crate) fn min_level(first: LocalDurability, second: LocalDurability) -> LocalDurability {
    use LocalDurability::{Buffered, Synced, Written};
    match (first, second) {
        (Buffered, _) | (_, Buffered) => Buffered,
        (Written, _) | (_, Written) => Written,
        (Synced, Synced) => Synced,
    }
}

fn verify_or_create_format(dir: &Path) -> Result<FormatOpen, StorageOpenError> {
    let path = dir.join("format");
    let bytes = match fs::read(&path) {
        Ok(bytes) => bytes,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
            let entries = fs::read_dir(dir).map_err(|_| StorageOpenError::Unavailable)?;
            let mut foreign = false;
            for entry in entries {
                let entry = entry.map_err(|_| StorageOpenError::Unavailable)?;
                let name = entry.file_name();
                if name != "owner.tbv1"
                    && name != "manifest.tmp"
                    && !name.to_str().is_some_and(|name| name.starts_with("owner.tbv1.tmp-"))
                {
                    foreign = true;
                    break;
                }
            }
            if foreign {
                // A missing marker beside any durable artifact is loss, not a
                // fresh location. Recreating it could select the wrong format
                // and erase acknowledged state.
                return Err(StorageOpenError::FormatMismatch);
            }
            let mut file = fs::File::create(&path).map_err(|_| StorageOpenError::Unavailable)?;
            let mut head = Vec::new();
            head.extend_from_slice(FORMAT_MAGIC);
            codec::push_u64(&mut head, LEGACY_FORMAT_MAJOR);
            file.write_all(&head).map_err(|_| StorageOpenError::Unavailable)?;
            file.sync_data().map_err(|_| StorageOpenError::Unavailable)?;
            return Ok(FormatOpen { state: FormatState::Legacy, created: true });
        }
        Err(_) => return Err(StorageOpenError::Unavailable),
    };
    if bytes.len() < FORMAT_MAGIC.len() + 8 || bytes[..FORMAT_MAGIC.len()] != *FORMAT_MAGIC {
        return Err(StorageOpenError::FormatMismatch);
    }
    let major = u64::from_le_bytes(
        bytes[FORMAT_MAGIC.len()..FORMAT_MAGIC.len() + 8]
            .try_into()
            .expect("the format major is eight bytes"),
    );
    let state = match major {
        LEGACY_FORMAT_MAJOR if bytes.len() == FORMAT_MAGIC.len() + 8 => FormatState::Legacy,
        FRONTIER_FORMAT_MAJOR => FormatState::Active(decode_active_format(&bytes)?),
        _ => return Err(StorageOpenError::FormatMismatch),
    };
    Ok(FormatOpen { state, created: false })
}

fn encode_active_format(active: &ActiveFormat) -> Vec<u8> {
    let mut bytes = Vec::new();
    bytes.extend_from_slice(FORMAT_MAGIC);
    codec::push_u64(&mut bytes, FRONTIER_FORMAT_MAJOR);
    codec::push_u64(&mut bytes, STORAGE_FRONTIER_FEATURE);
    bytes.extend_from_slice(&active.store_id.as_bytes());
    let activation = active.activation.activation_id.as_str().as_bytes();
    let activation_len =
        u16::try_from(activation.len()).expect("a validated operation identity fits u16");
    codec::push_u16(&mut bytes, activation_len);
    bytes.extend_from_slice(activation);
    codec::push_u64(&mut bytes, active.activation.external_fence_generation);
    bytes.extend_from_slice(&active.activation.external_record_digest);
    bytes.extend_from_slice(&[0_u8; FORMAT_V2_RESERVED_BYTES]);
    let checksum = format_checksum(&bytes);
    bytes.extend_from_slice(&checksum);
    bytes
}

fn decode_active_format(bytes: &[u8]) -> Result<ActiveFormat, StorageOpenError> {
    const FIXED_WITHOUT_ID: usize = 10 + 8 + 8 + 16 + 2 + 8 + 32 + 8 + 32;
    if bytes.len() < FIXED_WITHOUT_ID + 1 {
        return Err(StorageOpenError::FormatMismatch);
    }
    let checksum_start = bytes.len() - 32;
    if bytes[checksum_start..] != format_checksum(&bytes[..checksum_start]) {
        return Err(StorageOpenError::FormatMismatch);
    }
    let mut reader = codec::Reader::new(&bytes[FORMAT_MAGIC.len() + 8..checksum_start]);
    if reader.read_u64() != Some(STORAGE_FRONTIER_FEATURE) {
        return Err(StorageOpenError::FormatMismatch);
    }
    let mut store_id = [0_u8; 16];
    store_id.copy_from_slice(reader.take(16).ok_or(StorageOpenError::FormatMismatch)?);
    let activation_len = reader.read_u16().ok_or(StorageOpenError::FormatMismatch)? as usize;
    let activation_text =
        std::str::from_utf8(reader.take(activation_len).ok_or(StorageOpenError::FormatMismatch)?)
            .map_err(|_| StorageOpenError::FormatMismatch)?;
    let activation_id =
        OperationId::try_new(activation_text).map_err(|_| StorageOpenError::FormatMismatch)?;
    let external_fence_generation = reader.read_u64().ok_or(StorageOpenError::FormatMismatch)?;
    if external_fence_generation == 0 {
        return Err(StorageOpenError::FormatMismatch);
    }
    let mut external_record_digest = [0_u8; 32];
    external_record_digest
        .copy_from_slice(reader.take(32).ok_or(StorageOpenError::FormatMismatch)?);
    if reader.take(FORMAT_V2_RESERVED_BYTES) != Some(&[0_u8; FORMAT_V2_RESERVED_BYTES])
        || reader.remaining() != 0
    {
        return Err(StorageOpenError::FormatMismatch);
    }
    Ok(ActiveFormat {
        store_id: StoreId::from_bytes(store_id),
        activation: LedgerFrontierActivationIdentity {
            activation_id,
            external_fence_generation,
            external_record_digest,
        },
    })
}

fn write_active_format(dir: &Path, active: &ActiveFormat) -> Result<(), StorageOpenError> {
    let temp = dir.join("format.tmp");
    let path = dir.join("format");
    let mut file = fs::OpenOptions::new()
        .create(true)
        .truncate(true)
        .write(true)
        .open(&temp)
        .map_err(|_| StorageOpenError::Unavailable)?;
    file.write_all(&encode_active_format(active)).map_err(|_| StorageOpenError::Unavailable)?;
    file.sync_all().map_err(|_| StorageOpenError::Unavailable)?;
    fs::rename(&temp, &path).map_err(|_| StorageOpenError::Unavailable)?;
    root::sync_dir(dir).map_err(|_| StorageOpenError::Unavailable)
}

fn format_checksum(bytes: &[u8]) -> [u8; 32] {
    let mut hasher = blake3::Hasher::new();
    hasher.update(FORMAT_CHECKSUM_DOMAIN);
    hasher.update(bytes);
    *hasher.finalize().as_bytes()
}

fn map_frontier_open_error(error: FrontierError) -> StorageOpenError {
    match error {
        FrontierError::Integrity => StorageOpenError::FormatMismatch,
        FrontierError::Unavailable => StorageOpenError::Unavailable,
    }
}

// Helper constructions for identities that already round-tripped through the
// validated public types are unnecessary: the ledger index accepts canonical
// text directly.

impl super::StoragePort for FileStorage {
    fn commit(&mut self, intent: CommitIntent) -> CommitResult {
        if intent.usage_admission.is_some() {
            return CommitResult::Rejected(CommitRejection::Storage);
        }
        self.commit_intent(&intent)
    }

    fn resolve_operation(
        &self,
        tenant: &TenantId,
        operation: &OperationId,
    ) -> Option<OperationResolution> {
        self.ledger_index.entry(tenant, operation).map(|entry| self.resolution_for(entry))
    }

    fn read_document_at(
        &self,
        sequence: StorageSequence,
        tenant: &TenantId,
        id: &DocumentId,
    ) -> Option<Document> {
        let table = self.table_at(sequence);
        table.find_by_document_id(tenant, id).map(|(_, document)| document)
    }

    fn read_document_in_collection_at(
        &self,
        sequence: StorageSequence,
        tenant: &TenantId,
        collection: &CollectionName,
        id: &DocumentId,
    ) -> Option<Document> {
        self.table_at(sequence).document(&StorageKey::new(tenant, collection, id))
    }

    fn documents_at(&self, sequence: StorageSequence, tenant: &TenantId) -> Vec<Document> {
        self.table_at(sequence).tenant_documents(tenant)
    }

    fn documents_in_collection_at(
        &self,
        sequence: StorageSequence,
        tenant: &TenantId,
        collection: &CollectionName,
    ) -> Vec<Document> {
        // The root table is an in-memory decoded map, so the collection bound
        // saves only the clones of other collections' documents — the decode
        // saving belongs to the LSM backend, whose records live encoded.
        self.table_at(sequence)
            .tenant_documents(tenant)
            .into_iter()
            .filter(|document| document.collection() == collection)
            .collect()
    }

    fn read_documents_in_collection_at(
        &self,
        sequence: StorageSequence,
        tenant: &TenantId,
        collection: &CollectionName,
        ids: &[DocumentId],
    ) -> Vec<Document> {
        let table = self.table_at(sequence);
        let mut documents: Vec<Document> = ids
            .iter()
            .filter_map(|id| table.document(&StorageKey::new(tenant, collection, id)))
            .collect();
        // Canonical key order of one collection is identifier order; the
        // caller's identifier list arrives in posting-list order, so the
        // surviving documents are ordered here once, by their own keys.
        documents.sort_by(|left, right| left.id().cmp(right.id()));
        documents
    }

    fn documents_at_from(
        &self,
        sequence: StorageSequence,
        tenant: &TenantId,
        after: Option<&DocumentId>,
        limit: u32,
    ) -> Vec<Document> {
        self.table_at(sequence).tenant_documents_from(tenant, after, limit)
    }

    fn documents_at_from_key(
        &self,
        sequence: StorageSequence,
        tenant: &TenantId,
        after: Option<(&CollectionName, &DocumentId)>,
        limit: u32,
    ) -> Result<Vec<Document>, super::SnapshotReadError> {
        Ok(self.table_at(sequence).tenant_documents_from_key(
            tenant,
            after.map(|(collection, identifier)| (collection.as_str(), identifier.as_str())),
            limit,
        ))
    }

    fn snapshot_boundary_available(&self, sequence: StorageSequence) -> bool {
        self.published.sequence == sequence.get()
            || self.history.range(..=sequence.get()).next_back().is_some()
    }

    fn winning_durabilities_at(
        &self,
        tenant: &TenantId,
        sequence: StorageSequence,
        keys: &[(&str, &str)],
    ) -> Option<Vec<LocalDurability>> {
        self.journal_index.winning_durabilities_at(tenant, sequence, keys)
    }

    fn tenant_commits(
        &self,
        tenant: &TenantId,
        after: StorageSequence,
        limit: u32,
    ) -> Vec<PublishedCommit> {
        self.journal_index.tenant_commits(tenant, after, limit)
    }

    fn committed_tenants(&self) -> Vec<TenantId> {
        self.journal_index.committed_tenants()
    }

    fn tenant_sequence(&self, tenant: &TenantId, at: StorageSequence) -> Option<StorageSequence> {
        self.journal_index.tenant_sequence(tenant, at)
    }

    fn servable_history_start(&self) -> Option<StorageSequence> {
        self.journal_index.servable_start()
    }

    fn journaled_high_water(&self) -> Option<StorageSequence> {
        self.journal_high_water.map(StorageSequence::new)
    }

    fn journal_divergence(&self) -> u64 {
        // Derived from the two durable indexes, never stored: every
        // operation the ledger resolves as committed owes exactly one
        // journal record, so an owed record the journal does not hold is
        // one published-but-unjournaled commit (repair F2's divergence).
        // Counting by the ledger's own committed-resolution rule (which
        // includes a `Ready` entry whose root is the published root — a
        // commit that published between the pointer publish and the
        // journal append) keeps the count live in-process and identical
        // after a reopen, with no new write-path or format surface.
        let diverged = self
            .ledger_index
            .entries()
            .filter(|entry| {
                matches!(self.resolution_for(entry), OperationResolution::Committed { .. })
                    && !self.journal_index.contains(&entry.tenant, entry.sequence)
            })
            .count();
        u64::try_from(diverged).unwrap_or(u64::MAX)
    }

    fn published_state(&self) -> (StorageSequence, StorageRootId) {
        (StorageSequence::new(self.published.sequence), self.published.id.clone())
    }

    fn ledger_frontier_status(&self) -> LedgerFrontierStatus {
        if self.frontier_poisoned {
            return LedgerFrontierStatus::Unavailable;
        }
        let Some(frontier) = self.ledger_frontier.as_ref() else {
            return LedgerFrontierStatus::UnsupportedBackend;
        };
        let observation = frontier.observation(self.format_major);
        match &self.activation {
            Some(activation) => {
                LedgerFrontierStatus::Active { observation, activation: activation.clone() }
            }
            None => LedgerFrontierStatus::Inactive(observation),
        }
    }

    fn activate_ledger_frontier(
        &mut self,
        request: &LedgerFrontierActivationRequest,
    ) -> Result<LedgerFrontierActivationReceipt, LedgerFrontierActivationError> {
        if let Some(active) = &self.activation {
            if active != &request.identity {
                return Err(LedgerFrontierActivationError::AlreadyActivatedByDifferentRecord);
            }
            let observation = self
                .ledger_frontier
                .as_ref()
                .ok_or(LedgerFrontierActivationError::IntegrityFailure)?
                .observation(self.format_major);
            return Ok(LedgerFrontierActivationReceipt { identity: active.clone(), observation });
        }
        if request.identity.external_fence_generation == 0 {
            return Err(LedgerFrontierActivationError::IntegrityFailure);
        }
        if self.frontier_poisoned {
            return Err(LedgerFrontierActivationError::StorageUnavailable);
        }
        if self.publication_blocked
            || self
                .ledger_index
                .entries()
                .any(|entry| self.resolution_for(entry) == OperationResolution::Pending)
        {
            return Err(LedgerFrontierActivationError::NotQuiescent);
        }
        let frontier =
            self.ledger_frontier.as_ref().ok_or(LedgerFrontierActivationError::IntegrityFailure)?;
        if frontier.observation(self.format_major) != request.expected {
            return Err(LedgerFrontierActivationError::StaleObservation);
        }

        let ledger_path = self.dir.join("ledger");
        let retained_path = self.dir.join("retained");
        let ledger_scan = LedgerIndex::scan(&ledger_path)
            .map_err(|_| LedgerFrontierActivationError::StorageUnavailable)?;
        let retained_scan = RetainedIndex::scan(&retained_path);
        if ledger_scan.mid_file_damage
            || ledger_scan.good_bytes != ledger_scan.total_bytes
            || retained_scan.mid_file_damage
            || retained_scan.good_bytes != retained_scan.total_bytes
        {
            return Err(LedgerFrontierActivationError::IntegrityFailure);
        }
        let store_id = frontier.store_id();
        let verified = VerifiedAnchors::from_scans(
            &ledger_path,
            &ledger_scan,
            &retained_path,
            &retained_scan,
            store_id,
        )
        .map_err(|_| LedgerFrontierActivationError::StorageUnavailable)?;
        if verified.final_anchor() != frontier.anchor() {
            return Err(LedgerFrontierActivationError::IntegrityFailure);
        }
        self.ledger
            .sync()
            .and_then(|()| self.retained.sync())
            .map_err(|_| LedgerFrontierActivationError::StorageUnavailable)?;
        let rebuilt = LedgerFrontier::rebuild_shadow(&self.dir, store_id, verified.final_anchor())
            .map_err(|error| match error {
                FrontierError::Integrity => LedgerFrontierActivationError::IntegrityFailure,
                FrontierError::Unavailable => LedgerFrontierActivationError::StorageUnavailable,
            })?;
        self.ledger_frontier = Some(rebuilt);
        let active = ActiveFormat { store_id, activation: request.identity.clone() };
        if let Err(error) = write_active_format(&self.dir, &active) {
            self.frontier_poisoned = true;
            return Err(match error {
                StorageOpenError::FormatMismatch => LedgerFrontierActivationError::IntegrityFailure,
                StorageOpenError::Unavailable => LedgerFrontierActivationError::StorageUnavailable,
            });
        }
        self.format_major = FRONTIER_FORMAT_MAJOR;
        self.activation = Some(request.identity.clone());
        let observation = self
            .ledger_frontier
            .as_ref()
            .expect("activation keeps the rebuilt frontier")
            .observation(self.format_major);
        Ok(LedgerFrontierActivationReceipt { identity: request.identity.clone(), observation })
    }

    fn compact(
        &mut self,
        request: StorageCompaction,
    ) -> Result<(CompactionReceipt, CompactionDriverFacts), CompactionError> {
        self.run_compaction(request)
    }

    fn retained_state(&self) -> RetainedState {
        RetainedState {
            bound: self.retained_index.declared_bound().map(StorageSequence::new),
            facts: self.retained_index.facts().to_vec(),
            non_deletion: self.retained_index.non_deletion(),
        }
    }

    // The root-file engine carries no tiers: P4's tier administration is
    // the LSM engine's surface, and this backend answers the typed
    // refusal.
    fn mover_pass(
        &mut self,
        _policy: &super::lsm::tier::TierMovePolicy,
        _home_region: &str,
    ) -> Result<super::lsm::tier::MoverOutcome, super::lsm::tier::TierUnsupported> {
        Err(super::lsm::tier::TierUnsupported)
    }

    fn tier_snapshot(&self) -> Option<super::lsm::tier::TierSnapshot> {
        None
    }

    fn flush(&mut self) -> Result<(), ()> {
        self.wal.sync().map_err(|_| ())?;
        self.ledger.sync().map_err(|_| ())?;
        self.retained.sync().map_err(|_| ())?;
        self.ledger_frontier.as_mut().ok_or(())?.sync().map_err(|_| ())?;
        self.journal.sync().map_err(|_| ())
    }

    #[cfg(test)]
    fn arm_test_fault(&mut self, point: FaultPoint) {
        if point == FaultPoint::WalAppend {
            self.wal.fail_next_append_partially();
        }
        self.faults.arm(point);
    }
}
