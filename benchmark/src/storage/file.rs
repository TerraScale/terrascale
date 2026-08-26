//! The private file-backed storage adapter: open, recovery, and publication.

use std::{
    collections::BTreeMap,
    fs,
    io::Write,
    path::{Path, PathBuf},
    sync::Arc,
};

use crate::types::{
    Document, DocumentId, ExpectedVersion, LocalDurability, OperationId, StorageSequence, TenantId,
};

use super::{PublishedCommit, PublishedWrite};
use super::{
    codec::{self, LocalDurabilityCode, StorageKey},
    faults::{FaultPlan, FaultPoint},
    intent::{
        CommitIntent, CommitRejection, CommitResult, OperationResolution, StagedWrite,
        StorageReceipt, UnknownCause,
    },
    ledger::{
        HistoryIndex, HistoryWriter, LedgerIndex, LedgerRecord, LedgerState, LedgerWriter,
        OperationEntry, Published, ReadyToPublish, RejectedRecord,
    },
    root::{self, RootTable, StorageRoot, StorageRootId},
    wal::{self, WalCommit, WalWriter},
};

/// Magic bytes identifying the V2 storage format.
const FORMAT_MAGIC: &[u8; 10] = b"TBV2FORMAT";

/// The storage-format version this implementation writes and accepts.
const FORMAT_VERSION: u64 = 1;

/// Why a durable location could not be opened.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum StorageOpenError {
    /// The location does not hold the expected storage format.
    FormatMismatch,
    /// The location could not be opened.
    Unavailable,
}

/// The private file adapter implementing [`super::StoragePort`].
pub(crate) struct FileStorage {
    dir: PathBuf,
    wal: WalWriter,
    ledger: LedgerWriter,
    ledger_index: LedgerIndex,
    /// The durable publication-history journal and its sequence-ordered
    /// index (W4-CA-4), with the durable high-water mark (repair F2).
    journal: HistoryWriter,
    journal_index: HistoryIndex,
    journal_high_water: Option<u64>,
    published: StorageRoot,
    history: BTreeMap<u64, Arc<RootTable>>,
    /// The highest storage sequence ever named in this location, whether or
    /// not its commit published. New commits allocate past it, so a sequence
    /// is never reused after a mid-publication failure.
    sequence_watermark: u64,
    faults: FaultPlan,
}

impl FileStorage {
    /// Opens or creates the store at `dir` and recovers it.
    pub(crate) fn open(dir: &Path) -> Result<Self, StorageOpenError> {
        fs::create_dir_all(dir).map_err(|_| StorageOpenError::Unavailable)?;
        let created_format = verify_or_create_format(dir)?;
        if created_format {
            root::sync_dir(dir).map_err(|_| StorageOpenError::Unavailable)?;
        }
        let wal_path = dir.join("wal");
        let ledger_path = dir.join("ledger");
        let journal_path = dir.join("history");
        let wal_writer = WalWriter::open(&wal_path).map_err(|_| StorageOpenError::Unavailable)?;
        let ledger_writer =
            LedgerWriter::open(&ledger_path).map_err(|_| StorageOpenError::Unavailable)?;
        let journal_writer =
            HistoryWriter::open(&journal_path).map_err(|_| StorageOpenError::Unavailable)?;
        if wal_writer.is_fresh() || ledger_writer.is_fresh() || journal_writer.is_fresh() {
            root::sync_dir(dir).map_err(|_| StorageOpenError::Unavailable)?;
        }
        let ledger_index = LedgerIndex::scan(&ledger_path);
        let journal_scan = HistoryIndex::scan(&journal_path);
        let report = wal::scan(&wal_path).map_err(|_| StorageOpenError::FormatMismatch)?;
        let mut storage = Self {
            dir: dir.to_path_buf(),
            wal: wal_writer,
            ledger: ledger_writer,
            ledger_index,
            journal: journal_writer,
            journal_index: journal_scan.index,
            journal_high_water: journal_scan.high_water,
            published: StorageRoot::empty(),
            history: BTreeMap::new(),
            sequence_watermark: 0,
            faults: FaultPlan::empty(),
        };
        if report.torn_tail {
            // The trailing frame was an interrupted append: drop it so the
            // store opens on its verified prefix. A truncation that cannot
            // complete fails the open instead of masking the damage.
            storage.wal.truncate(report.good_bytes).map_err(|_| StorageOpenError::Unavailable)?;
        }
        if journal_scan.total_bytes > journal_scan.good_bytes {
            // Repair F1: the journal's trailing record is torn or does not
            // verify. Truncate to the verified prefix exactly as the WAL is
            // truncated, so later appends land where a scan can see them
            // instead of freezing the servable window forever. The window
            // bound and the journaled high-water mark keep reporting the
            // divergence. A truncation that cannot complete fails the open.
            storage
                .journal
                .truncate(journal_scan.good_bytes)
                .map_err(|_| StorageOpenError::Unavailable)?;
        }
        storage.recover(report.commits)?;
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
        if let Some((manifest_sequence, manifest_id)) = root::read_manifest(&self.dir) {
            let path = root::root_file_path(&self.dir, &manifest_id);
            if let Ok(Some((root_sequence, root_table, confirmed_id))) = root::load_root_file(&path)
                && root_sequence == manifest_sequence
                && confirmed_id == manifest_id
            {
                table = root_table;
                sequence = manifest_sequence;
                id = manifest_id;
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
                continue;
            }
            let tenant =
                TenantId::try_new(&commit.tenant).map_err(|_| StorageOpenError::FormatMismatch)?;
            let applied = table.apply(&tenant, &commit.writes);
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
            );
            id = published_id;
        }
        let shared = Arc::new(table);
        self.history.insert(sequence, Arc::clone(&shared));
        self.published = StorageRoot { id, sequence, table: shared };
        Ok(())
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
    ) {
        if self
            .ledger_index
            .entry_by_text(tenant, operation)
            .is_some_and(|entry| matches!(entry.state, LedgerState::Published { .. }))
        {
            return;
        }
        let record = LedgerRecord::Published(Published {
            tenant: tenant.to_owned(),
            operation: operation.to_owned(),
            digest,
            root: id.as_str().to_owned(),
            sequence,
            post_publish_synced,
        });
        if self.ledger.append(&record).and_then(|()| self.ledger.sync()).is_ok() {
            self.ledger_index.apply(record);
        }
    }

    /// Runs the storage publication order for one validated intent.
    #[allow(clippy::too_many_lines)]
    fn commit_intent(&mut self, intent: &CommitIntent) -> CommitResult {
        // Step 1: validate request, scope, operation identity, and conflicts
        // without changing the published root. A validation failure is a
        // definite synchronous answer and never binds the identity durably;
        // only a conflict, which follows validated write work, is recorded as
        // a durable rejection.
        if intent.writes.is_empty() || Self::has_duplicate_targets(&intent.writes) {
            return CommitResult::Rejected(CommitRejection::Validation);
        }
        if let Some(entry) = self.ledger_index.entry(&intent.tenant, &intent.operation) {
            if entry.digest != intent.digest {
                return CommitResult::Rejected(CommitRejection::OperationIdReuse);
            }
            return self.replay(entry);
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
        let sequence = self.sequence_watermark + 1;
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
            let mut marker = Vec::new();
            codec::push_str(&mut marker, intent.tenant.as_str());
            codec::push_str(&mut marker, intent.operation.as_str());
            codec::push_u64(&mut marker, intent.digest);
            codec::push_u64(&mut marker, sequence);
            codec::push_str(&mut marker, root_id.as_str());
            if self.wal.append(wal::FRAME_COMMIT_MARKER, &marker).is_err() {
                return CommitResult::Rejected(CommitRejection::Storage);
            }
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
            return CommitResult::Rejected(CommitRejection::Storage);
        }

        // Step 6: append and durably sync the ReadyToPublish record. This
        // sync persists resolution metadata for every durability level.
        if self.faults.fire(FaultPoint::ReadyLedgerSync).is_err() {
            return CommitResult::Rejected(CommitRejection::Storage);
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
        if self.ledger.append(&ready).and_then(|()| self.ledger.sync()).is_err() {
            return CommitResult::Rejected(CommitRejection::Storage);
        }
        self.ledger_index.apply(ready);

        // Step 7: atomically publish the root pointer.
        if self.faults.fire(FaultPoint::PointerPublish).is_err() {
            return CommitResult::Rejected(CommitRejection::Storage);
        }
        if durable_frames && root::publish_manifest(&self.dir).is_err() {
            return CommitResult::Rejected(CommitRejection::Storage);
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
            root: root_id.as_str().to_owned(),
            sequence,
            post_publish_synced: intent.durability == LocalDurability::Synced,
        });
        if self.ledger.append(&published).and_then(|()| self.ledger.sync()).is_err() {
            return CommitResult::Unknown(UnknownCause::PublishedLedgerSync);
        }
        self.ledger_index.apply(published);

        // Step 9: return only the guarantees of the requested level.
        CommitResult::Committed(StorageReceipt {
            sequence: StorageSequence::new(sequence),
            root: root_id,
            durability: intent.durability,
        })
    }

    /// Reports whether one write set targets the same document twice.
    fn has_duplicate_targets(writes: &[StagedWrite]) -> bool {
        let mut seen: Vec<(String, String)> = Vec::new();
        for write in writes {
            let key = match write {
                StagedWrite::Put { document, .. } => {
                    (document.collection().as_str().to_owned(), document.id().as_str().to_owned())
                }
                StagedWrite::Delete { collection: Some(collection), id, .. } => {
                    (collection.as_str().to_owned(), id.as_str().to_owned())
                }
                StagedWrite::Delete { collection: None, id, .. } => {
                    (String::new(), id.as_str().to_owned())
                }
            };
            if seen.contains(&key) {
                return true;
            }
            seen.push(key);
        }
        false
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
    fn resolution_for(&self, entry: &OperationEntry) -> OperationResolution {
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
        if self.ledger.append(&record).and_then(|()| self.ledger.sync()).is_err() {
            return CommitResult::Rejected(reason);
        }
        self.ledger_index.apply(record);
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

fn verify_or_create_format(dir: &Path) -> Result<bool, StorageOpenError> {
    let path = dir.join("format");
    let bytes = match fs::read(&path) {
        Ok(bytes) => bytes,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
            let mut file = fs::File::create(&path).map_err(|_| StorageOpenError::Unavailable)?;
            let mut head = Vec::new();
            head.extend_from_slice(FORMAT_MAGIC);
            codec::push_u64(&mut head, FORMAT_VERSION);
            file.write_all(&head).map_err(|_| StorageOpenError::Unavailable)?;
            file.sync_data().map_err(|_| StorageOpenError::Unavailable)?;
            return Ok(true);
        }
        Err(_) => return Err(StorageOpenError::Unavailable),
    };
    if bytes.len() != FORMAT_MAGIC.len() + 8 || bytes[..FORMAT_MAGIC.len()] != *FORMAT_MAGIC {
        return Err(StorageOpenError::FormatMismatch);
    }
    let version = u64::from_le_bytes(
        bytes[FORMAT_MAGIC.len()..].try_into().map_err(|_| StorageOpenError::FormatMismatch)?,
    );
    if version != FORMAT_VERSION {
        return Err(StorageOpenError::FormatMismatch);
    }
    Ok(false)
}

// Helper constructions for identities that already round-tripped through the
// validated public types are unnecessary: the ledger index accepts canonical
// text directly.

impl super::StoragePort for FileStorage {
    fn commit(&mut self, intent: CommitIntent) -> CommitResult {
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

    fn documents_at(&self, sequence: StorageSequence, tenant: &TenantId) -> Vec<Document> {
        self.table_at(sequence).tenant_documents(tenant)
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

    fn flush(&mut self) -> Result<(), ()> {
        self.wal.sync().map_err(|_| ())?;
        self.ledger.sync().map_err(|_| ())?;
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
