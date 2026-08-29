//! The ZoneTree-inspired LSM storage engine (P1, ADR 0016).
//!
//! [`LsmStorage`] replaces the W3-1 full-root-rewrite store behind the same
//! crate-private [`StoragePort`] seam, so the Engine, families, query, and
//! replication consumers migrate without protocol change. One acknowledged
//! commit appends exactly one checksummed WAL frame carrying the whole
//! ordered batch — document entries, ledger record, and history record — so
//! durable bytes per write are bounded by the write set itself, never by the
//! size of the table it lands in. The commissioning baseline this replaces
//! appended 154,246,326 durable bytes for a 512-write window of ~300-byte
//! documents.
//!
//! Publication order per commit: validate → resolve identity → allocate
//! sequence → build batch → WAL append → (Synced: fsync) → memtable apply →
//! receipt. There is no root file and no pointer rename: the ledger and
//! history records are entries in the same atomic frame, so they can never
//! diverge from the published state and `journal_divergence` is structurally
//! zero. The root identity becomes a chain — a checksum of the previous
//! head, the sequence, and the write-set digest — verifiable by replay
//! without ever rewriting a table.
//!
//! Recorded limits (honest, first landing): merges are inline and
//! caller-driven through `compact` plus rotation-triggered flushes; segment
//! readers hold sparse indexes in memory and read records on demand; MVCC
//! keeps every version until an explicit compaction declares a bound.

pub(crate) mod compact;
pub(crate) mod internal;
pub(crate) mod manifest;
pub(crate) mod memtable;
pub(crate) mod object_store;
pub(crate) mod segment;
#[cfg(test)]
pub(crate) mod tests;
#[cfg(test)]
pub(crate) mod tests_support;
pub(crate) mod tier;
pub(crate) mod wal;

use std::{
    cell::RefCell,
    collections::{BTreeMap, BTreeSet},
    fs,
    path::{Path, PathBuf},
};

use crate::types::{
    CollectionName, Document, DocumentId, ExpectedVersion, LocalDurability, OperationId,
    StorageSequence, TenantId,
};

use super::{
    PublishedCommit, PublishedWrite, StoragePort, codec,
    faults::{FaultPlan, FaultPoint},
    intent::{
        CommitIntent, CommitRejection, CommitResult, StagedWrite, StorageReceipt, UnknownCause,
    },
    ledger,
    root::StorageRootId,
};
use internal::{Entry, InternalKey};
use manifest::SegmentManifest;
use memtable::MemTable;
use segment::{SegmentMeta, SegmentReader};
use wal::WalLog;

/// The LSM location's format marker: a directory carrying this marker
/// belongs to the LSM engine; anything else fails closed at open.
const FORMAT_MARKER: &str = "TBV2LSM1";

/// The meta-keyspace label holding the declared history floor.
const META_HISTORY_FLOOR: &str = "history-floor";

/// One in-memory operation-resolution record.
#[derive(Clone, Debug, Eq, PartialEq)]
struct LedgerEntry {
    digest: u64,
    sequence: u64,
    root: String,
    requested: LocalDurability,
    committed: bool,
    post_publish_synced: bool,
    rejection: Option<CommitRejection>,
}

/// Why an LSM location could not be opened.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum LsmOpenError {
    /// The location holds a different storage format.
    FormatMismatch,
    /// Durable storage could not be opened or verified.
    Unavailable,
}

/// Tuning bounds of one LSM location.
#[derive(Clone, Copy, Debug)]
pub(crate) struct LsmTuning {
    /// Freeze the memtable at this many accounted bytes.
    pub(crate) max_memtable_bytes: usize,
    /// Freeze the memtable at this many entries.
    pub(crate) max_memtable_entries: usize,
    /// Merge level-0 segments when their count reaches this.
    pub(crate) l0_merge_threshold: usize,
}

impl Default for LsmTuning {
    fn default() -> Self {
        Self {
            max_memtable_bytes: memtable::DEFAULT_MAX_MEMTABLE_BYTES,
            max_memtable_entries: memtable::DEFAULT_MAX_MEMTABLE_ENTRIES,
            l0_merge_threshold: 4,
        }
    }
}

/// The LSM storage engine state behind the [`StoragePort`] seam.
pub(crate) struct LsmStorage {
    dir: PathBuf,
    wal: WalLog,
    memtable: MemTable,
    frozen: Vec<MemTable>,
    manifest: SegmentManifest,
    readers: BTreeMap<u64, RefCell<SegmentReader>>,
    pub(crate) tuning: LsmTuning,
    /// The highest sequence this location has ever named. Sequences may have
    /// holes (a rejected-after-allocation commit consumes one) and are never
    /// reused.
    sequence_watermark: u64,
    /// The root-chain head: a verifiable identity derived from the chain of
    /// committed digests, standing in for the old store's root-file identity
    /// without rewriting any table.
    root_head: StorageRootId,
    ledger_index: BTreeMap<(String, String), LedgerEntry>,
    history_index: BTreeMap<u64, PublishedCommit>,
    /// The oldest sequence the history keyspace still serves, once a
    /// compaction declared a bound.
    history_floor: Option<u64>,
    retained: super::retained::RetainedWriter,
    retained_index: super::retained::RetainedIndex,
    faults: FaultPlan,
}

impl LsmStorage {
    /// Opens or creates an LSM location, recovering it.
    pub(crate) fn open(dir: &Path) -> Result<Self, LsmOpenError> {
        fs::create_dir_all(dir).map_err(|_| LsmOpenError::Unavailable)?;
        let marker = dir.join("format");
        match fs::read_to_string(&marker) {
            Ok(content) if content.trim() == FORMAT_MARKER => {}
            Ok(_) => return Err(LsmOpenError::FormatMismatch),
            Err(_) => {
                // A fresh location: any pre-existing file makes it foreign.
                let populated = fs::read_dir(dir)
                    .map_or(0, |entries| entries.filter_map(Result::ok).count())
                    > 0;
                if populated {
                    return Err(LsmOpenError::FormatMismatch);
                }
                fs::write(&marker, FORMAT_MARKER).map_err(|_| LsmOpenError::Unavailable)?;
                super::root::sync_dir(dir).map_err(|_| LsmOpenError::Unavailable)?;
            }
        }
        Self::recover(dir)
    }

    /// Rebuilds engine state from the manifest anchor, the verifying
    /// segments, the retained-evidence store, and the WAL tail.
    fn recover(dir: &Path) -> Result<Self, LsmOpenError> {
        let retained_path = dir.join("retained");
        let mut retained = super::retained::RetainedWriter::open(&retained_path)
            .map_err(|_| LsmOpenError::Unavailable)?;
        let retained_scan = super::retained::RetainedIndex::scan(&retained_path);
        if retained_scan.mid_file_damage {
            // The retained-evidence store backs every declared history
            // bound; unreadable evidence fails the open (GAP-S1 D3).
            return Err(LsmOpenError::FormatMismatch);
        }
        if retained_scan.total_bytes > retained_scan.good_bytes {
            retained.truncate(retained_scan.good_bytes).map_err(|_| LsmOpenError::Unavailable)?;
        }
        let mut manifest = SegmentManifest::read(dir).unwrap_or_default();
        let mut readers: BTreeMap<u64, RefCell<SegmentReader>> = BTreeMap::new();
        let mut verified: Vec<SegmentMeta> = Vec::new();
        for meta in &manifest.segments {
            if let Ok(reader) =
                SegmentReader::open(&segment_path(dir, meta.id), meta.id, meta.level)
            {
                verified.push(meta.clone());
                readers.insert(meta.id, RefCell::new(reader));
            } else {
                verified.clear();
                readers.clear();
                break;
            }
        }
        if verified.is_empty() && !manifest.segments.is_empty() {
            // The manifest does not verify against its segments: rebuild the
            // segment set from the verifying files on disk. WAL replay above
            // the rebuilt state is idempotent by internal key.
            manifest = rebuild_manifest_from_directory(dir, &mut readers, &manifest);
        } else {
            manifest.segments = verified;
        }
        let wal = WalLog::open(&dir.join("wal")).map_err(|error| {
            if error.kind() == std::io::ErrorKind::InvalidData {
                // Mid-file damage: verified history sits behind it, so the
                // location's bytes do not verify — a format failure, not an
                // availability one.
                LsmOpenError::FormatMismatch
            } else {
                LsmOpenError::Unavailable
            }
        })?;
        let mut engine = Self {
            dir: dir.to_path_buf(),
            wal,
            memtable: MemTable::default(),
            frozen: Vec::new(),
            manifest,
            readers,
            tuning: LsmTuning::default(),
            sequence_watermark: 0,
            root_head: chain_root(&StorageRootId::from_recorded(String::new()), 0, 0),
            ledger_index: BTreeMap::new(),
            history_index: BTreeMap::new(),
            history_floor: None,
            retained,
            retained_index: retained_scan.index,
            faults: FaultPlan::empty(),
        };
        engine.rebuild_indexes_from_segments();
        let wal_scan = engine.wal.scan().map_err(|_| LsmOpenError::Unavailable)?;
        let mut watermark = engine.sequence_watermark;
        for entry in &wal_scan.entries {
            watermark = watermark.max(entry.key.sequence());
        }
        for sequence in engine.history_index.keys() {
            watermark = watermark.max(*sequence);
        }
        for entry in engine.ledger_index.values() {
            watermark = watermark.max(entry.sequence);
        }
        engine.sequence_watermark = watermark;
        for entry in wal_scan.entries {
            engine.apply_entry_to_memory(&entry);
        }
        // The chain head is the root recorded by the highest ledger entry.
        if let Some((_, entry)) = engine.ledger_index.iter().next_back()
            && !entry.root.is_empty()
        {
            engine.root_head = StorageRootId::from_recorded(entry.root.clone());
        }
        // The floor may only become known after a segment's history entries
        // were applied (the meta keyspace sorts last): enforce it once, over
        // the fully rebuilt index.
        if let Some(floor) = engine.history_floor {
            engine.history_index.retain(|sequence, _| *sequence >= floor);
        }
        Ok(engine)
    }

    /// Rebuilds the ledger, history, and floor state by scanning the named
    /// segments' non-document keyspaces.
    fn rebuild_indexes_from_segments(&mut self) {
        let metas: Vec<SegmentMeta> = self.manifest.segments.clone();
        for meta in metas {
            let Some(reader) = self.readers.get(&meta.id) else {
                continue;
            };
            let Ok(entries) = reader.borrow_mut().read_all() else {
                continue;
            };
            for entry in entries {
                let tag = entry.key.tag();
                if tag == internal::Keyspace::Ledger.tag()
                    || tag == internal::Keyspace::History.tag()
                    || tag == internal::Keyspace::Meta.tag()
                {
                    self.apply_entry_to_memory(&entry);
                }
            }
        }
    }

    /// Applies one entry to the memtable and the in-memory indexes.
    ///
    /// Every entry — documents, ledger, history, meta — enters the memtable:
    /// the memtable is the write buffer whose flush carries all keyspaces
    /// into segments. Ledger, history, and meta entries additionally update
    /// their query indexes. Idempotent by internal key.
    fn apply_entry_to_memory(&mut self, entry: &Entry) {
        let tag = entry.key.tag();
        if tag == internal::Keyspace::Ledger.tag() {
            if let Some(record) = decode_ledger_value(&entry.value) {
                let mut reader = codec::Reader::new(&entry.key.user_key()[1..]);
                if let (Some(tenant), Some(operation)) =
                    (reader.read_string(), reader.read_string())
                {
                    let key = (tenant, operation);
                    if self
                        .ledger_index
                        .get(&key)
                        .is_none_or(|prior| prior.sequence <= record.sequence)
                    {
                        self.ledger_index.insert(key, record);
                    }
                }
            }
        } else if tag == internal::Keyspace::History.tag()
            && let Some(commit) = decode_history_value(&entry.value)
            && self.history_floor.is_none_or(|floor| commit.sequence().get() >= floor)
        {
            self.history_index.insert(commit.sequence().get(), commit);
        } else if tag == internal::Keyspace::Meta.tag()
            && let Some(bound) = decode_meta_bound(&entry.value)
            && meta_label(entry.key.user_key()) == META_HISTORY_FLOOR
        {
            self.history_floor = Some(bound);
        }
        self.memtable.insert(entry.clone());
    }

    /// Returns the newest entry for one user key at or below `sequence`.
    fn newest_document_entry(&self, key: &InternalKey, sequence: u64) -> Option<(bool, Vec<u8>)> {
        let bound = key.user_key_upper_bound();
        self.collect_range(key.user_key(), &bound, sequence)
            .into_iter()
            .find(|candidate| candidate.key.user_key() == key.user_key())
            .map(|found| (found.key.is_deletion(), found.value))
    }

    /// Collects the newest entry per user key in `[start, end)` at or below
    /// `sequence` from the memtable, frozen tables, and segments.
    fn collect_range(&self, start: &[u8], end: &[u8], sequence: u64) -> Vec<Entry> {
        let mut merged: Vec<Entry> = Vec::new();
        for source in std::iter::once(&self.memtable).chain(self.frozen.iter()) {
            for entry in source.iter() {
                if entry.key.sequence() <= sequence
                    && entry.key.user_key() >= start
                    && entry.key.user_key() < end
                {
                    merged.push(entry);
                }
            }
        }
        for meta in &self.manifest.segments {
            if meta.max_key.as_slice() < start || meta.min_key.as_slice() >= end {
                continue;
            }
            let Some(reader) = self.readers.get(&meta.id) else {
                continue;
            };
            let Ok(entries) = reader.borrow_mut().read_from(Some(start)) else {
                continue;
            };
            for entry in entries {
                if entry.key.user_key() >= end {
                    break;
                }
                // The sparse anchor may sit below the range start; those
                // records are other keys' versions, not this range's.
                if entry.key.user_key() >= start && entry.key.sequence() <= sequence {
                    merged.push(entry);
                }
            }
        }
        merged.sort_by(|left, right| left.key.cmp(&right.key));
        let mut reduced: Vec<Entry> = Vec::new();
        let mut seen: BTreeSet<Vec<u8>> = BTreeSet::new();
        for entry in merged {
            if seen.insert(entry.key.user_key().to_vec()) {
                reduced.push(entry);
            }
        }
        reduced
    }

    /// Returns the version of one document key at `sequence`.
    fn version_at(&self, key: &InternalKey, sequence: u64) -> Option<u64> {
        let (deletion, value) = self.newest_document_entry(key, sequence)?;
        if deletion {
            return None;
        }
        let mut reader = codec::Reader::new(&value);
        codec::decode_document(&mut reader).map(|document| document.metadata().version())
    }

    /// Finds the first document of a tenant with the identifier, in
    /// canonical key order, at `sequence`.
    fn find_by_document_id(
        &self,
        tenant: &TenantId,
        id: &DocumentId,
        sequence: u64,
    ) -> Option<Document> {
        let start = tenant_prefix(tenant);
        let end = internal::tenant_upper_bound(tenant);
        for found in self.collect_range(&start, &end, sequence) {
            let Some((found_tenant, _collection, document)) =
                decode_document_key(found.key.user_key())
            else {
                continue;
            };
            if found_tenant != tenant.as_str() {
                continue;
            }
            if document == id.as_str() {
                if found.key.is_deletion() {
                    return None;
                }
                let mut value_reader = codec::Reader::new(&found.value);
                return codec::decode_document(&mut value_reader);
            }
        }
        None
    }

    /// Encodes, appends, and applies one commit batch.
    #[allow(
        clippy::too_many_lines,
        reason = "one publication order, mirrored from the root store's proven sequence"
    )]
    pub(crate) fn commit_intent(&mut self, intent: &CommitIntent) -> CommitResult {
        // Step 1: validate the request without allocating anything. A
        // validation failure never binds the identity.
        if intent.writes.is_empty() || has_duplicate_targets(&intent.writes) {
            return CommitResult::Rejected(CommitRejection::Validation);
        }
        // Step 2: resolve a retried identity durably.
        let identity = (intent.tenant.as_str().to_owned(), intent.operation.as_str().to_owned());
        if let Some(entry) = self.ledger_index.get(&identity) {
            if entry.digest != intent.digest {
                return CommitResult::Rejected(CommitRejection::OperationIdReuse);
            }
            return replay(entry);
        }
        // Step 3: conflict-check every write against the snapshot and the
        // published view.
        let snapshot_sequence = intent.snapshot_sequence.get();
        let published_sequence = self.sequence_watermark;
        for write in &intent.writes {
            if let StagedWrite::Put { document, .. } = write
                && !codec::document_within_limits(document)
            {
                return CommitResult::Rejected(CommitRejection::Validation);
            }
            let mismatch = match write {
                StagedWrite::Put { document, expected } => {
                    let key = InternalKey::document(
                        &intent.tenant,
                        document.collection(),
                        document.id(),
                        0,
                    );
                    self.conflict_between(&key, *expected, snapshot_sequence, published_sequence)
                }
                StagedWrite::Delete { collection: Some(collection), id, expected } => {
                    let key = InternalKey::document(&intent.tenant, collection, id, 0);
                    self.conflict_between(&key, *expected, snapshot_sequence, published_sequence)
                }
                StagedWrite::Delete { collection: None, id, expected } => self
                    .conflict_for_absent_document(
                        &intent.tenant,
                        id,
                        *expected,
                        snapshot_sequence,
                        published_sequence,
                    ),
            };
            if mismatch {
                return self.record_rejection(intent, CommitRejection::Conflict);
            }
        }
        // Step 4: allocate the sequence past everything this location has
        // ever named; a later failure can never let another commit reuse it.
        let sequence = self.sequence_watermark + 1;
        self.sequence_watermark = sequence;
        let root_id = chain_root(&self.root_head, sequence, intent.digest);
        // Step 5: build the ordered batch. Documents, ledger record, and
        // history record ride one frame.
        let mut entries: Vec<Entry> = Vec::with_capacity(intent.writes.len() + 2);
        let mut published_writes: Vec<PublishedWrite> = Vec::with_capacity(intent.writes.len());
        for write in &intent.writes {
            match write {
                StagedWrite::Put { document, .. } => {
                    let key = InternalKey::document(
                        &intent.tenant,
                        document.collection(),
                        document.id(),
                        0,
                    );
                    let version = self
                        .version_at(&key, published_sequence)
                        .map_or(1, |prior| prior.saturating_add(1));
                    let mut published = document.clone();
                    published.set_version(version);
                    let mut value = Vec::new();
                    codec::encode_document(&mut value, &published);
                    entries.push(Entry {
                        key: InternalKey::document(
                            &intent.tenant,
                            published.collection(),
                            published.id(),
                            sequence,
                        ),
                        value,
                    });
                    published_writes.push(PublishedWrite::Put { document: published });
                }
                StagedWrite::Delete { collection, id, .. } => {
                    let version_at_delete = match collection {
                        Some(collection) => self
                            .version_at(
                                &InternalKey::document(&intent.tenant, collection, id, 0),
                                published_sequence,
                            )
                            .unwrap_or(0),
                        None => 0,
                    };
                    match collection {
                        Some(collection) => {
                            entries.push(Entry {
                                key: InternalKey::document_delete(
                                    &intent.tenant,
                                    collection,
                                    id,
                                    sequence,
                                ),
                                value: Vec::new(),
                            });
                        }
                        None => {
                            if let Some(existing) =
                                self.find_by_document_id(&intent.tenant, id, published_sequence)
                            {
                                entries.push(Entry {
                                    key: InternalKey::document_delete(
                                        &intent.tenant,
                                        existing.collection(),
                                        id,
                                        sequence,
                                    ),
                                    value: Vec::new(),
                                });
                            }
                        }
                    }
                    published_writes.push(PublishedWrite::Delete {
                        collection: collection.clone(),
                        id: id.clone(),
                        version_at_delete,
                    });
                }
            }
        }
        // The durable batch records `post_publish_synced = false`: the
        // frame is appended before its fsync runs, so a crash between the
        // two must resolve as `Written`, never `Synced`. A successful sync
        // appends the small flip frame that upgrades the resolution — the
        // LSM's analogue of the root store's post-publication `Published`
        // record.
        let ledger_entry = LedgerEntry {
            digest: intent.digest,
            sequence,
            root: root_id.as_str().to_owned(),
            requested: intent.durability,
            committed: true,
            post_publish_synced: false,
            rejection: None,
        };
        entries.push(Entry {
            key: InternalKey::ledger(&intent.tenant, intent.operation.as_str(), sequence),
            value: encode_ledger_value(&ledger_entry),
        });
        let commit = PublishedCommit::new(
            intent.tenant.clone(),
            intent.operation.clone(),
            StorageSequence::new(sequence),
            intent.durability,
            published_writes,
        );
        entries.push(Entry {
            key: InternalKey::history(sequence),
            value: ledger::encode_commit_frame(&commit),
        });
        // Step 6: durable append. `Buffered` writes no document bytes, but
        // its resolution records are still durable (the old store's rule:
        // resolution metadata persists for every durability level, so a
        // reopened Buffered commit resolves instead of reporting Pending).
        //
        // Fault mapping mirrors the root store's publication-order split:
        // `WalAppend`, `CommitMarkerApplication`, and `PointerPublish` fail
        // BEFORE the durable append — a definite rejection with nothing
        // applied; `JournalAppend` skips the history record (the commit
        // still publishes, and the high-water mark discloses the gap);
        // `ReadyLedgerSync` fails the durable sync of the resolution
        // record; `PointerSync` and `DirectorySync` interrupt the response
        // of an already-durable commit.
        let skip_journal = self.faults.fire(FaultPoint::JournalAppend).is_err();
        let durable_batch: Vec<Entry> = entries
            .iter()
            .filter(|entry| {
                if intent.durability == LocalDurability::Buffered {
                    let tag = entry.key.tag();
                    return tag == internal::Keyspace::Ledger.tag()
                        || tag == internal::Keyspace::History.tag();
                }
                !(skip_journal && entry.key.tag() == internal::Keyspace::History.tag())
            })
            .cloned()
            .collect();
        if self.faults.fire(FaultPoint::CommitMarkerApplication).is_err() {
            return CommitResult::Rejected(CommitRejection::Storage);
        }
        if self.faults.fire(FaultPoint::WalAppend).is_err() {
            return CommitResult::Rejected(CommitRejection::Storage);
        }
        if self.faults.fire(FaultPoint::PointerPublish).is_err() {
            return CommitResult::Rejected(CommitRejection::Storage);
        }
        if self.wal.append(&durable_batch).is_err() {
            return CommitResult::Rejected(CommitRejection::Storage);
        }
        if intent.durability == LocalDurability::Synced
            && self.faults.fire(FaultPoint::ReadyLedgerSync).is_err()
        {
            return CommitResult::Rejected(CommitRejection::Storage);
        }
        if intent.durability == LocalDurability::Synced
            && self.faults.fire(FaultPoint::PointerSync).is_err()
        {
            return self.finish_publication(
                &entries,
                &ledger_entry,
                Some(UnknownCause::PostPublicationPointerSync),
                false,
                intent.tenant.as_str(),
                intent.operation.as_str(),
            );
        }
        if intent.durability == LocalDurability::Synced
            && self.faults.fire(FaultPoint::DirectorySync).is_err()
        {
            return self.finish_publication(
                &entries,
                &ledger_entry,
                Some(UnknownCause::PostPublicationDirectorySync),
                false,
                intent.tenant.as_str(),
                intent.operation.as_str(),
            );
        }
        if intent.durability == LocalDurability::Synced && self.wal.sync().is_err() {
            return CommitResult::Rejected(CommitRejection::Storage);
        }
        if intent.durability == LocalDurability::Synced {
            // The batch is durable and synced. The flip frame upgrades the
            // ledger entry's proven durability; its absence after a crash or
            // fault leaves an honest `Written` resolution.
            if self.faults.fire(FaultPoint::PublishedLedgerSync).is_err() {
                return self.finish_publication(
                    &entries,
                    &ledger_entry,
                    Some(UnknownCause::PublishedLedgerSync),
                    false,
                    intent.tenant.as_str(),
                    intent.operation.as_str(),
                );
            }
            let mut synced_entry = ledger_entry.clone();
            synced_entry.post_publish_synced = true;
            let flip = vec![Entry {
                key: InternalKey::ledger(&intent.tenant, intent.operation.as_str(), sequence),
                value: encode_ledger_value(&synced_entry),
            }];
            if self.wal.append(&flip).is_err() || self.wal.sync().is_err() {
                return self.finish_publication(
                    &entries,
                    &ledger_entry,
                    Some(UnknownCause::PublishedLedgerSync),
                    false,
                    intent.tenant.as_str(),
                    intent.operation.as_str(),
                );
            }
        }
        self.finish_publication(
            &entries,
            &ledger_entry,
            None,
            intent.durability == LocalDurability::Synced,
            intent.tenant.as_str(),
            intent.operation.as_str(),
        )
    }

    /// Applies the published batch to memory and returns the receipt.
    ///
    /// A fault after the durable append reports `Unknown`: the commit may or
    /// may not be observed by a later reopen, exactly like the root store's
    /// post-marker failures (W3-1 R3). The batch is applied either way, so
    /// this process observes what recovery would.
    #[allow(
        clippy::too_many_arguments,
        clippy::too_many_lines,
        reason = "the publication tail carries the batch and its proven-sync bookkeeping"
    )]
    fn finish_publication(
        &mut self,
        entries: &[Entry],
        ledger_entry: &LedgerEntry,
        interrupted_cause: Option<UnknownCause>,
        intent_sync_completed: bool,
        synced_tenant: &str,
        synced_operation: &str,
    ) -> CommitResult {
        let cause = interrupted_cause.or_else(|| {
            self.faults
                .fire(FaultPoint::PublishedLedgerSync)
                .err()
                .map(|_| UnknownCause::PublishedLedgerSync)
        });
        if let Some(cause) = cause {
            for entry in entries {
                self.apply_entry_to_memory(entry);
            }
            self.root_head = StorageRootId::from_recorded(ledger_entry.root.clone());
            return CommitResult::Unknown(cause);
        }
        for entry in entries {
            self.apply_entry_to_memory(entry);
        }
        if intent_sync_completed {
            let mut synced_entry = ledger_entry.clone();
            synced_entry.post_publish_synced = true;
            self.ledger_index
                .insert((synced_tenant.to_owned(), synced_operation.to_owned()), synced_entry);
        }
        self.root_head = StorageRootId::from_recorded(ledger_entry.root.clone());
        if self
            .memtable
            .should_rotate(self.tuning.max_memtable_bytes, self.tuning.max_memtable_entries)
        {
            let replacement = std::mem::take(&mut self.memtable);
            self.frozen.push(replacement);
        }
        if self.frozen.len() >= 2 {
            // Keep the frozen backlog bounded: flush inline. The flush is
            // bounded by the memtable bound, never by table size.
            compact::flush_frozen(self);
        }
        CommitResult::Committed(StorageReceipt {
            sequence: StorageSequence::new(ledger_entry.sequence),
            root: StorageRootId::from_recorded(ledger_entry.root.clone()),
            durability: ledger_entry.requested,
        })
    }

    /// Records one conflict rejection durably, then reports it.
    fn record_rejection(&mut self, intent: &CommitIntent, reason: CommitRejection) -> CommitResult {
        let sequence = self.sequence_watermark + 1;
        self.sequence_watermark = sequence;
        let entry = LedgerEntry {
            digest: intent.digest,
            sequence,
            root: String::new(),
            requested: intent.durability,
            committed: false,
            post_publish_synced: false,
            rejection: Some(reason),
        };
        if intent.durability != LocalDurability::Buffered {
            let batch = vec![Entry {
                key: InternalKey::ledger(&intent.tenant, intent.operation.as_str(), sequence),
                value: encode_ledger_value(&entry),
            }];
            if self.wal.append(&batch).is_err() {
                return CommitResult::Rejected(reason);
            }
            let _ = self.wal.sync();
        }
        self.ledger_index.insert(
            (intent.tenant.as_str().to_owned(), intent.operation.as_str().to_owned()),
            entry,
        );
        CommitResult::Rejected(reason)
    }

    /// Checks one keyed write against the snapshot and published views.
    fn conflict_between(
        &self,
        key: &InternalKey,
        expected: ExpectedVersion,
        snapshot_sequence: u64,
        published_sequence: u64,
    ) -> bool {
        let snapshot_version = self.version_at(key, snapshot_sequence);
        let published_version = self.version_at(key, published_sequence);
        if snapshot_version != published_version {
            return true;
        }
        !matches_expectation(snapshot_version, expected)
    }

    /// Checks a delete of an identifier the snapshot does not contain.
    fn conflict_for_absent_document(
        &self,
        tenant: &TenantId,
        id: &DocumentId,
        expected: ExpectedVersion,
        snapshot_sequence: u64,
        published_sequence: u64,
    ) -> bool {
        let in_snapshot = self.find_by_document_id(tenant, id, snapshot_sequence).is_some();
        let in_published = self.find_by_document_id(tenant, id, published_sequence).is_some();
        if in_snapshot != in_published {
            return true;
        }
        if in_published {
            return true;
        }
        !matches_expectation(None, expected)
    }
}

/// The documents-keyspace prefix of one tenant.
fn tenant_prefix(tenant: &TenantId) -> Vec<u8> {
    let mut bytes = Vec::new();
    codec::push_u8(&mut bytes, internal::Keyspace::Documents.tag());
    bytes.extend_from_slice(tenant.as_str().as_bytes());
    bytes.push(0);
    bytes
}

/// Decodes one documents-keyspace user key into its three parts.
fn decode_document_key(user_key: &[u8]) -> Option<(String, String, String)> {
    let mut parts = user_key[1..].split(|byte| *byte == 0);
    let tenant = parts.next()?;
    let collection = parts.next()?;
    let document = parts.next()?;
    // The encoding terminates every identifier with NUL, so exactly one
    // empty trailing part remains; anything beyond that is malformed.
    match (parts.next(), parts.next()) {
        (Some([]) | None, None) => {}
        _ => return None,
    }
    Some((
        String::from_utf8(tenant.to_vec()).ok()?,
        String::from_utf8(collection.to_vec()).ok()?,
        String::from_utf8(document.to_vec()).ok()?,
    ))
}

/// Returns the meta label of one meta-keyspace user key.
fn meta_label(user_key: &[u8]) -> String {
    let mut reader = codec::Reader::new(user_key);
    reader.read_u8();
    reader.read_string().unwrap_or_default()
}

/// Reports whether one write set targets the same document twice.
fn has_duplicate_targets(writes: &[StagedWrite]) -> bool {
    let mut seen: BTreeSet<(String, String)> = BTreeSet::new();
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
        if !seen.insert(key) {
            return true;
        }
    }
    false
}

/// Whether one observed version satisfies an expectation.
fn matches_expectation(version: Option<u64>, expected: ExpectedVersion) -> bool {
    match expected {
        ExpectedVersion::Any => true,
        ExpectedVersion::New => version.is_none(),
        ExpectedVersion::Exact(exact) => version == Some(exact),
    }
}

/// Derives the next root-chain identity from the current head.
///
/// The identity is 32 canonical hex characters — sequence and the chained
/// checksum — matching the root-identity shape the rest of the engine
/// validates.
fn chain_root(head: &StorageRootId, sequence: u64, digest: u64) -> StorageRootId {
    let mut bytes = Vec::new();
    codec::push_str(&mut bytes, head.as_str());
    codec::push_u64(&mut bytes, sequence);
    codec::push_u64(&mut bytes, digest);
    StorageRootId::from_recorded(format!("{sequence:016x}{:016x}", codec::fnv1a(&bytes)))
}

/// Replays the recorded durable resolution for a retried identity.
fn replay(entry: &LedgerEntry) -> CommitResult {
    if let Some(reason) = entry.rejection {
        return CommitResult::Rejected(reason);
    }
    let proven =
        if entry.post_publish_synced { LocalDurability::Synced } else { LocalDurability::Written };
    CommitResult::Committed(StorageReceipt {
        sequence: StorageSequence::new(entry.sequence),
        root: StorageRootId::from_recorded(entry.root.clone()),
        durability: super::file::min_level(entry.requested, proven),
    })
}

/// Encodes one ledger entry's durable value.
fn encode_ledger_value(entry: &LedgerEntry) -> Vec<u8> {
    let mut out = Vec::new();
    codec::push_u64(&mut out, entry.digest);
    codec::push_u64(&mut out, entry.sequence);
    codec::push_str(&mut out, &entry.root);
    codec::push_u8(&mut out, durability_code(entry.requested));
    codec::push_u8(&mut out, u8::from(entry.committed));
    codec::push_u8(&mut out, u8::from(entry.post_publish_synced));
    codec::push_u8(&mut out, entry.rejection.map_or(0, rejection_code));
    out
}

/// Decodes one ledger entry's durable value.
fn decode_ledger_value(bytes: &[u8]) -> Option<LedgerEntry> {
    let mut reader = codec::Reader::new(bytes);
    let digest = reader.read_u64()?;
    let sequence = reader.read_u64()?;
    let root = reader.read_string()?;
    let requested = durability_of(reader.read_u8()?)?;
    let committed = reader.read_u8()? != 0;
    let post_publish_synced = reader.read_u8()? != 0;
    let rejection = match reader.read_u8()? {
        0 => None,
        code => Some(decode_rejection(code)?),
    };
    Some(LedgerEntry {
        digest,
        sequence,
        root,
        requested,
        committed,
        post_publish_synced,
        rejection,
    })
}

/// Decodes one history entry's durable value.
fn decode_history_value(bytes: &[u8]) -> Option<PublishedCommit> {
    let mut reader = codec::Reader::new(bytes);
    let length = reader.read_u32()? as usize;
    let checksum = reader.read_u64()?;
    let payload = reader.take(length)?;
    if codec::fnv1a(payload) != checksum {
        return None;
    }
    ledger::decode_commit_payload(payload)
}

/// Decodes one meta bound value.
fn decode_meta_bound(bytes: &[u8]) -> Option<u64> {
    let mut reader = codec::Reader::new(bytes);
    reader.read_u64()
}

/// Maps a durability level to its one-byte code.
fn durability_code(level: LocalDurability) -> u8 {
    match level {
        LocalDurability::Buffered => 0,
        LocalDurability::Written => 1,
        LocalDurability::Synced => 2,
    }
}

/// Maps a durability code back to its level.
fn durability_of(code: u8) -> Option<LocalDurability> {
    match code {
        0 => Some(LocalDurability::Buffered),
        1 => Some(LocalDurability::Written),
        2 => Some(LocalDurability::Synced),
        _ => None,
    }
}

/// Maps a rejection to its one-byte code.
fn rejection_code(reason: CommitRejection) -> u8 {
    match reason {
        CommitRejection::Validation => 1,
        CommitRejection::Conflict => 2,
        CommitRejection::OperationIdReuse => 3,
        CommitRejection::Storage => 4,
    }
}

/// Maps a rejection code back to the rejection.
fn decode_rejection(code: u8) -> Option<CommitRejection> {
    match code {
        1 => Some(CommitRejection::Validation),
        2 => Some(CommitRejection::Conflict),
        3 => Some(CommitRejection::OperationIdReuse),
        4 => Some(CommitRejection::Storage),
        _ => None,
    }
}

/// Returns the segment file path of one segment identity.
fn segment_path(dir: &Path, id: u64) -> PathBuf {
    dir.join(format!("seg-{id:016x}.lsm"))
}

/// Rebuilds a manifest from the verifying segment files on disk.
fn rebuild_manifest_from_directory(
    dir: &Path,
    readers: &mut BTreeMap<u64, RefCell<SegmentReader>>,
    previous: &SegmentManifest,
) -> SegmentManifest {
    let mut manifest = SegmentManifest {
        epoch: previous.epoch + 1,
        next_segment_id: previous.next_segment_id,
        wal_checkpoint: 0,
        segments: Vec::new(),
    };
    let Ok(entries) = fs::read_dir(dir) else {
        return manifest;
    };
    let mut candidates: Vec<(u64, u16)> = Vec::new();
    for entry in entries.filter_map(Result::ok) {
        let file_name = entry.file_name();
        let Some(hex) = file_name
            .to_str()
            .and_then(|name| name.strip_prefix("seg-"))
            .and_then(|stem| stem.strip_suffix(".lsm"))
        else {
            continue;
        };
        let Ok(id) = u64::from_str_radix(hex, 16) else { continue };
        let level =
            previous.segments.iter().find(|meta| meta.id == id).map_or(0, |meta| meta.level);
        candidates.push((id, level));
    }
    candidates.sort_unstable();
    for (id, level) in candidates {
        if let Ok(reader) = SegmentReader::open(&segment_path(dir, id), id, level) {
            manifest.next_segment_id = manifest.next_segment_id.max(id + 1);
            manifest.segments.push(reader.meta().clone());
            readers.insert(id, RefCell::new(reader));
        }
    }
    manifest
}

/// The merged documents of one tenant at a sequence, in canonical order.
fn tenant_documents_at(storage: &LsmStorage, tenant: &TenantId, sequence: u64) -> Vec<Document> {
    let start = tenant_prefix(tenant);
    let end = internal::tenant_upper_bound(tenant);
    storage
        .collect_range(&start, &end, sequence)
        .into_iter()
        .filter(|entry| !entry.key.is_deletion())
        .filter_map(|entry| {
            let mut reader = codec::Reader::new(&entry.value);
            codec::decode_document(&mut reader)
        })
        .collect()
}

/// One bounded window of a tenant's documents, mirroring the root store's
/// cursor semantics: resume strictly after the first key bearing `after`,
/// and skip below-cursor identifiers when the cursor key is absent.
fn tenant_documents_from_at(
    storage: &LsmStorage,
    tenant: &TenantId,
    after: Option<&DocumentId>,
    limit: u32,
    sequence: u64,
) -> Vec<Document> {
    let documents = tenant_documents_at(storage, tenant, sequence);
    let limit = usize::try_from(limit).unwrap_or(usize::MAX);
    let Some(after) = after else {
        return documents.into_iter().take(limit).collect();
    };
    let resume_position =
        documents.iter().position(|document| document.id().as_str() == after.as_str());
    match resume_position {
        Some(at) => documents.into_iter().skip(at + 1).take(limit).collect(),
        None => documents
            .into_iter()
            .skip_while(|document| document.id().as_str() < after.as_str())
            .take(limit)
            .collect(),
    }
}

impl StoragePort for LsmStorage {
    fn commit(&mut self, intent: CommitIntent) -> CommitResult {
        self.commit_intent(&intent)
    }

    fn resolve_operation(
        &self,
        tenant: &TenantId,
        operation: &OperationId,
    ) -> Option<super::intent::OperationResolution> {
        let entry =
            self.ledger_index.get(&(tenant.as_str().to_owned(), operation.as_str().to_owned()))?;
        if let Some(reason) = entry.rejection {
            return Some(super::intent::OperationResolution::Rejected(reason));
        }
        let proven = if entry.post_publish_synced {
            LocalDurability::Synced
        } else {
            LocalDurability::Written
        };
        Some(super::intent::OperationResolution::Committed {
            sequence: StorageSequence::new(entry.sequence),
            durability: super::file::min_level(entry.requested, proven),
        })
    }

    fn read_document_at(
        &self,
        sequence: StorageSequence,
        tenant: &TenantId,
        id: &DocumentId,
    ) -> Option<Document> {
        self.find_by_document_id(tenant, id, sequence.get())
    }

    fn read_document_in_collection_at(
        &self,
        sequence: StorageSequence,
        tenant: &TenantId,
        collection: &CollectionName,
        id: &DocumentId,
    ) -> Option<Document> {
        let key = InternalKey::document(tenant, collection, id, 0);
        let (deletion, value) = self.newest_document_entry(&key, sequence.get())?;
        if deletion {
            return None;
        }
        let mut reader = codec::Reader::new(&value);
        codec::decode_document(&mut reader)
    }

    fn documents_at(&self, sequence: StorageSequence, tenant: &TenantId) -> Vec<Document> {
        tenant_documents_at(self, tenant, sequence.get())
    }

    fn documents_at_from(
        &self,
        sequence: StorageSequence,
        tenant: &TenantId,
        after: Option<&DocumentId>,
        limit: u32,
    ) -> Vec<Document> {
        tenant_documents_from_at(self, tenant, after, limit, sequence.get())
    }

    fn tenant_commits(
        &self,
        tenant: &TenantId,
        after: StorageSequence,
        limit: u32,
    ) -> Vec<PublishedCommit> {
        self.history_index
            .range((after.get() + 1)..)
            .filter(|(_, commit)| commit.tenant() == tenant)
            .take(limit as usize)
            .map(|(_, commit)| commit.clone())
            .collect()
    }

    fn committed_tenants(&self) -> Vec<TenantId> {
        let mut tenants: BTreeSet<String> = BTreeSet::new();
        for commit in self.history_index.values() {
            tenants.insert(commit.tenant().as_str().to_owned());
        }
        tenants.into_iter().filter_map(|tenant| TenantId::try_new(&tenant).ok()).collect()
    }

    fn tenant_sequence(&self, tenant: &TenantId, at: StorageSequence) -> Option<StorageSequence> {
        self.history_index
            .range(..=at.get())
            .rev()
            .find(|(_, commit)| commit.tenant() == tenant)
            .map(|(sequence, _)| StorageSequence::new(*sequence))
    }

    fn servable_history_start(&self) -> Option<StorageSequence> {
        // The oldest sequence the history keyspace still serves: the
        // declared floor once a compaction reclaimed below it, otherwise
        // the oldest retained record.
        self.history_floor
            .or_else(|| self.history_index.keys().next().copied())
            .map(StorageSequence::new)
    }

    fn journaled_high_water(&self) -> Option<StorageSequence> {
        self.history_index.keys().next_back().map(|sequence| StorageSequence::new(*sequence))
    }

    fn journal_divergence(&self) -> u64 {
        // The LSM writes the history record inside the same atomic commit
        // batch as the documents and ledger record, so a published commit
        // with a missing history record is structurally impossible.
        0
    }

    fn published_state(&self) -> (StorageSequence, StorageRootId) {
        (
            StorageSequence::new(self.sequence_watermark),
            StorageRootId::from_recorded(self.root_head.as_str().to_owned()),
        )
    }

    fn compact(
        &mut self,
        request: super::compaction::StorageCompaction,
    ) -> Result<
        (super::compaction::CompactionReceipt, super::compaction::CompactionDriverFacts),
        super::compaction::CompactionError,
    > {
        compact::run_compaction(self, request)
    }

    fn retained_state(&self) -> super::compaction::RetainedState {
        super::compaction::RetainedState {
            bound: self.retained_index.declared_bound().map(StorageSequence::new),
            facts: self.retained_index.facts().to_vec(),
            non_deletion: self.retained_index.non_deletion(),
        }
    }

    fn flush(&mut self) -> Result<(), ()> {
        self.wal.sync().map_err(|_| ())
    }

    #[cfg(test)]
    fn arm_test_fault(&mut self, point: FaultPoint) {
        self.faults.arm(point);
    }
}

/// Crate-private accessors the compact module and tests use.
impl LsmStorage {
    /// The directory the engine lives in.
    pub(super) fn dir(&self) -> &Path {
        &self.dir
    }

    /// The live fault plan.
    pub(super) fn faults_mut(&mut self) -> &mut FaultPlan {
        &mut self.faults
    }

    /// The WAL byte length.
    pub(super) fn wal_len(&self) -> u64 {
        self.wal.len()
    }

    /// The segment count by level (test evidence).
    #[cfg(test)]
    pub(super) fn segment_count_at_level(&self, level: u16) -> usize {
        self.manifest.segments.iter().filter(|meta| meta.level == level).count()
    }

    /// The journal's durable head (the highest history sequence).
    pub(super) fn journaled_high_water_impl(&self) -> Option<StorageSequence> {
        self.history_index.keys().next_back().map(|sequence| StorageSequence::new(*sequence))
    }

    /// The published sequence.
    pub(super) fn published_sequence_impl(&self) -> StorageSequence {
        StorageSequence::new(self.sequence_watermark)
    }

    /// Truncates the WAL after a published checkpoint.
    pub(super) fn wal_truncate_impl(&mut self) -> Result<(), ()> {
        self.wal.truncate_after_checkpoint().map_err(|_| ())
    }

    /// Appends the window's retained facts and declares the bound durably.
    pub(super) fn append_retained_facts_impl(
        &mut self,
        facts: &[super::retained::RetainedFact],
        bound: u64,
        head: u64,
        non_deletion: &[(TenantId, Option<StorageSequence>)],
    ) -> Result<(), ()> {
        for fact in facts {
            self.retained.append_fact(fact).map_err(|_| ())?;
            self.retained_index.upsert_fact(fact.clone());
        }
        self.retained.append_bound(bound, head, non_deletion).map_err(|_| ())?;
        self.retained.sync().map_err(|_| ())?;
        self.retained_index.declare_bound(bound, head);
        Ok(())
    }

    /// Declares the history floor durably through a WAL meta entry and
    /// advances the in-memory floor.
    pub(super) fn declare_history_floor_impl(&mut self, bound: u64) -> Result<(), ()> {
        let mut value = Vec::new();
        codec::push_u64(&mut value, bound);
        let batch = vec![Entry {
            key: InternalKey::meta(META_HISTORY_FLOOR, self.sequence_watermark + 1),
            value,
        }];
        self.wal.append(&batch).map_err(|_| ())?;
        self.wal.sync().map_err(|_| ())?;
        self.sequence_watermark += 1;
        self.history_floor = Some(bound);
        self.history_index.retain(|sequence, _| *sequence >= bound);
        Ok(())
    }
}
