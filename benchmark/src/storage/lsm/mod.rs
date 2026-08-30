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
#[cfg(test)]
mod tier_matrix_tests;
pub(crate) mod wal;
pub(crate) mod wal_frontier;

use std::{
    cell::RefCell,
    collections::{BTreeMap, BTreeSet},
    fs,
    io::Write,
    path::{Path, PathBuf},
};

use crate::types::{
    CollectionName, Document, DocumentId, ExpectedVersion, LocalDurability, OperationId,
    StorageSequence, TenantId,
};
use crate::usage::{
    DurableUsageFact, MeasurementWindow, MeterCatalog, MeterKind, MintingIdentity, UsageFact,
    UsageFactId, UsageFactLedger, UsageScope,
};

use super::frontier::{
    ActiveFormat, FRONTIER_FORMAT_MAJOR, LEGACY_FORMAT_MAJOR, LedgerFrontierActivationError,
    LedgerFrontierActivationIdentity, LedgerFrontierActivationReceipt,
    LedgerFrontierActivationRequest, LedgerFrontierObservation, LedgerFrontierStatus,
    STORAGE_FRONTIER_FEATURE, StoreId,
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
use manifest::{ActiveWalManifest, SegmentManifest, WalCheckpoint};
use memtable::MemTable;
use segment::{SegmentMeta, SegmentReader};
use wal::WalLog;
use wal_frontier::{FrontierError, RetainedFrontier, VerifiedStream, WalFrontier};

/// The LSM location's format marker: a directory carrying this marker
/// belongs to the LSM engine; anything else fails closed at open.
const FORMAT_MARKER: &str = "TBV2LSM1";
const ACTIVE_FORMAT_MAGIC: &[u8; 8] = b"TBLSMACT";
const ACTIVE_FORMAT_DOMAIN: &[u8] = b"terrabase.lsm-format.v2\0";
const ACTIVE_FORMAT_RESERVED: usize = 8;
const INITIAL_WAL_EPOCH: u64 = 1;

enum FormatState {
    Legacy,
    Active(ActiveFormat),
}

struct FormatOpen {
    state: FormatState,
    created: bool,
}

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

/// One intent's fully prepared commit: everything [`LsmStorage::
/// commit_batch`] needs to publish it after the batch's shared durable
/// append. Built by [`LsmStorage::prepare_commit`] without touching
/// durable state.
struct PreparedCommit {
    /// The intent's position in the batch's results vector, set by the
    /// batch driver.
    index: usize,
    /// Every entry of the commit, applied to memory on publication.
    entries: Vec<Entry>,
    /// The entries this intent appends durably (document bytes excluded
    /// for `Buffered`, the history record excluded under the journal-append
    /// fault).
    durable_batch: Vec<Entry>,
    /// The operation-resolution record.
    ledger_entry: LedgerEntry,
    /// The post-sync flip record that upgrades a `Synced` resolution from
    /// `Written`.
    flip: Entry,
    /// Whether the intent requested `Synced` durability.
    synced: bool,
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
    wal_frontier: Option<WalFrontier>,
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
    /// The identifier index: every `(tenant, document identifier)` pair the
    /// documents keyspace has ever carried a record for, with the set of
    /// collections that carried it. Point reads by identifier resolve their
    /// exact keys here instead of scanning the tenant's whole key range;
    /// entries whose records compaction has fully reclaimed are stale but
    /// harmless — their probe finds nothing and the scan they would replace
    /// would have found nothing either.
    identifier_index: BTreeMap<(String, String), BTreeSet<String>>,
    history_index: BTreeMap<u64, PublishedCommit>,
    /// The oldest sequence the history keyspace still serves, once a
    /// compaction declared a bound.
    history_floor: Option<u64>,
    retained: super::retained::RetainedWriter,
    retained_index: super::retained::RetainedIndex,
    retained_frontier: Option<RetainedFrontier>,
    format_major: u64,
    activation: Option<LedgerFrontierActivationIdentity>,
    store_id: StoreId,
    frontier_poisoned: bool,
    usage_index: UsageFactLedger,
    /// A malformed or conflicting persisted fact makes recovery fail closed.
    usage_corrupt: bool,
    faults: FaultPlan,
}

impl LsmStorage {
    /// Opens or creates an LSM location, recovering it.
    pub(crate) fn open(dir: &Path) -> Result<Self, LsmOpenError> {
        fs::create_dir_all(dir).map_err(|_| LsmOpenError::Unavailable)?;
        let format = verify_or_create_format(dir)?;
        if format.created {
            super::root::sync_dir(dir).map_err(|_| LsmOpenError::Unavailable)?;
        }
        Self::recover(dir, format.state)
    }

    /// Rebuilds engine state from the manifest anchor, the verifying
    /// segments, the retained-evidence store, and the WAL tail.
    #[allow(
        clippy::needless_pass_by_value,
        clippy::single_match_else,
        clippy::too_many_lines,
        reason = "recovery consumes one format decision and preserves ordered fail-closed stages"
    )]
    fn recover(dir: &Path, format: FormatState) -> Result<Self, LsmOpenError> {
        let retained_path = dir.join("retained");
        let wal_path = dir.join("wal");
        let active_format = match &format {
            FormatState::Legacy => None,
            FormatState::Active(active) => Some(active),
        };
        let retained_exists = retained_path.try_exists().map_err(|_| LsmOpenError::Unavailable)?;
        let wal_exists = wal_path.try_exists().map_err(|_| LsmOpenError::Unavailable)?;
        if active_format.is_some() && (!retained_exists || !wal_exists) {
            return Err(LsmOpenError::FormatMismatch);
        }
        let mut retained = super::retained::RetainedWriter::open(&retained_path)
            .map_err(|_| LsmOpenError::Unavailable)?;
        let retained_scan = super::retained::RetainedIndex::scan(&retained_path);
        if retained_scan.mid_file_damage {
            // The retained-evidence store backs every declared history
            // bound; unreadable evidence fails the open (GAP-S1 D3).
            return Err(LsmOpenError::FormatMismatch);
        }
        let read_manifest = if active_format.is_some() {
            SegmentManifest::read_active(dir)
        } else {
            SegmentManifest::read(dir)
        }
        .map_err(|error| {
            if error.kind() == std::io::ErrorKind::InvalidData {
                LsmOpenError::FormatMismatch
            } else {
                LsmOpenError::Unavailable
            }
        })?;
        let segment_artifacts = has_segment_artifacts(dir)?;
        let manifest = match (active_format, read_manifest) {
            (Some(_), Some(manifest)) if manifest.active_wal.is_some() => manifest,
            (None, Some(manifest)) if manifest.active_wal.is_none() => manifest,
            (Some(_), _) | (None, Some(_)) => return Err(LsmOpenError::FormatMismatch),
            (None, None) if segment_artifacts => return Err(LsmOpenError::FormatMismatch),
            (None, None) => SegmentManifest::default(),
        };
        let mut readers: BTreeMap<u64, RefCell<SegmentReader>> = BTreeMap::new();
        let mut segment_documents: Vec<Entry> = Vec::new();
        for meta in &manifest.segments {
            let mut reader = SegmentReader::open(&segment_path(dir, meta.id), meta.id, meta.level)
                .map_err(|_| LsmOpenError::FormatMismatch)?;
            let verified_entries = if reader.meta() != meta {
                None
            } else {
                reader.read_all().ok()
            };
            let entries = verified_entries.ok_or(LsmOpenError::FormatMismatch)?;
            segment_documents.extend(
                entries
                    .into_iter()
                    .filter(|entry| entry.key.tag() == internal::Keyspace::Documents.tag()),
            );
            readers.insert(meta.id, RefCell::new(reader));
        }
        let mut wal = WalLog::open_unrepaired(&wal_path).map_err(|error| {
            if error.kind() == std::io::ErrorKind::InvalidData {
                // Mid-file damage: verified history sits behind it, so the
                // location's bytes do not verify — a format failure, not an
                // availability one.
                LsmOpenError::FormatMismatch
            } else {
                LsmOpenError::Unavailable
            }
        })?;
        let wal_scan = wal.scan().map_err(|_| LsmOpenError::Unavailable)?;
        let store_id =
            active_format.map_or_else(|| StoreId::generate(dir), |active| active.store_id);
        let (format_major, activation, wal_frontier, retained_frontier, replay_wal) =
            match active_format {
                Some(active) => {
                    let active_wal = manifest.active_wal.ok_or(LsmOpenError::FormatMismatch)?;
                    let (wal_frontier, replay_wal) = recover_active_wal(
                        dir,
                        &wal_path,
                        &mut wal,
                        &wal_scan,
                        active.store_id,
                        active_wal,
                    )?;
                    let retained_frontier = recover_active_retained(
                        dir,
                        &retained_path,
                        &mut retained,
                        &retained_scan,
                        active.store_id,
                    )?;
                    (
                        FRONTIER_FORMAT_MAJOR,
                        Some(active.activation.clone()),
                        Some(wal_frontier),
                        Some(retained_frontier),
                        replay_wal,
                    )
                }
                None => {
                    if wal_scan.mid_file_damage {
                        return Err(LsmOpenError::FormatMismatch);
                    }
                    if wal_scan.total_bytes > wal_scan.good_bytes {
                        wal.truncate(wal_scan.good_bytes).map_err(|_| LsmOpenError::Unavailable)?;
                    }
                    if retained_scan.total_bytes > retained_scan.good_bytes {
                        retained
                            .truncate(retained_scan.good_bytes)
                            .map_err(|_| LsmOpenError::Unavailable)?;
                    }
                    let verified_wal =
                        VerifiedStream::for_wal(&wal_path, &wal_scan, store_id, INITIAL_WAL_EPOCH)
                            .map_err(|_| LsmOpenError::Unavailable)?;
                    let verified_retained =
                        VerifiedStream::for_retained(&retained_path, &retained_scan, store_id)
                            .map_err(|_| LsmOpenError::Unavailable)?;
                    let wal_frontier = WalFrontier::rebuild_shadow(
                        dir,
                        store_id,
                        INITIAL_WAL_EPOCH,
                        verified_wal.final_anchor(),
                    )
                    .map_err(map_frontier_open_error)?;
                    let retained_frontier = RetainedFrontier::rebuild_shadow(
                        dir,
                        store_id,
                        verified_retained.final_anchor(),
                    )
                    .map_err(map_frontier_open_error)?;
                    (LEGACY_FORMAT_MAJOR, None, Some(wal_frontier), Some(retained_frontier), true)
                }
            };
        let mut engine = Self {
            dir: dir.to_path_buf(),
            wal,
            wal_frontier,
            memtable: MemTable::default(),
            frozen: Vec::new(),
            manifest,
            readers,
            tuning: LsmTuning::default(),
            sequence_watermark: 0,
            root_head: chain_root(&StorageRootId::from_recorded(String::new()), 0, 0),
            ledger_index: BTreeMap::new(),
            identifier_index: BTreeMap::new(),
            history_index: BTreeMap::new(),
            history_floor: None,
            retained,
            retained_index: retained_scan.index,
            retained_frontier,
            format_major,
            activation,
            store_id,
            frontier_poisoned: false,
            usage_index: UsageFactLedger::default(),
            usage_corrupt: false,
            faults: FaultPlan::empty(),
        };
        engine.rebuild_indexes_from_segments();
        engine.rebuild_indexes_from_cold_tier();
        let mut watermark = engine.sequence_watermark;
        if replay_wal {
            for entry in &wal_scan.entries {
                watermark = watermark.max(entry.key.sequence());
            }
        }
        for sequence in engine.history_index.keys() {
            watermark = watermark.max(*sequence);
        }
        for entry in engine.ledger_index.values() {
            watermark = watermark.max(entry.sequence);
        }
        engine.sequence_watermark = watermark;
        if replay_wal {
            for entry in wal_scan.entries {
                engine.apply_entry_to_memory(&entry);
            }
        }
        if engine.usage_corrupt {
            return Err(LsmOpenError::FormatMismatch);
        }
        // The chain head is the root recorded by the highest committed
        // sequence. The ledger is keyed by (tenant, operation), whose
        // lexical order is unrelated to publication order.
        if let Some(entry) = engine
            .ledger_index
            .values()
            .filter(|entry| entry.committed && entry.rejection.is_none() && !entry.root.is_empty())
            .max_by_key(|entry| entry.sequence)
        {
            engine.root_head = StorageRootId::from_recorded(entry.root.clone());
        }
        // The floor may only become known after a segment's history entries
        // were applied (the meta keyspace sorts last): enforce it once, over
        // the fully rebuilt index.
        if let Some(floor) = engine.history_floor {
            engine.history_index.retain(|sequence, _| *sequence >= floor);
        }
        for entry in &segment_documents {
            engine.index_identifier_entry(entry);
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

    /// Rebuilds indexes and the publication watermark from verified cold
    /// segments as well as `NVMe` segments. Demotion intentionally removes the
    /// latter from the active segment manifest, so omitting this pass would
    /// make a reopen advertise an older sequence and hide the demoted range.
    fn rebuild_indexes_from_cold_tier(&mut self) {
        let Some(tier_manifest) = tier::TierManifest::read(&self.dir) else {
            return;
        };
        let active_ids: BTreeSet<u64> = self.manifest.segments.iter().map(|meta| meta.id).collect();
        for object in tier_manifest
            .objects
            .iter()
            .filter(|object| object.verified && !active_ids.contains(&object.segment_id))
        {
            let Ok(entries) = tier::read_cold_segment_entries(self, object.segment_id) else {
                continue;
            };
            for entry in entries {
                self.sequence_watermark = self.sequence_watermark.max(entry.key.sequence());
                let tag = entry.key.tag();
                if tag == internal::Keyspace::Ledger.tag()
                    || tag == internal::Keyspace::History.tag()
                    || tag == internal::Keyspace::Meta.tag()
                {
                    self.apply_entry_to_memory(&entry);
                } else if tag == internal::Keyspace::Documents.tag() {
                    self.index_identifier_entry(&entry);
                }
            }
        }
    }

    /// Records one documents-keyspace entry in the identifier index.
    ///
    /// Deliberately separate from [`Self::apply_entry_to_memory`]: segment
    /// rebuilds must index identifiers without re-loading segment records
    /// into the memtable.
    fn index_identifier_entry(&mut self, entry: &Entry) {
        if entry.key.tag() != internal::Keyspace::Documents.tag() {
            return;
        }
        if let Some((tenant, collection, document)) = decode_document_key(entry.key.user_key()) {
            self.identifier_index
                .entry((tenant, document))
                .or_default()
                .insert(collection);
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
        } else if tag == internal::Keyspace::Meta.tag()
            && let Some(fact) = decode_usage_entry(entry)
        {
            let operation = fact.fact().identity().id().as_str();
            let tenant = fact.fact().scope().tenant_id().as_str();
            let bound = self.ledger_index.get(&(tenant.to_owned(), operation.to_owned()));
            if !matches!(
                bound,
                Some(record) if record.committed
                    && record.rejection.is_none()
                    && record.sequence == fact.sequence()
            ) || self.usage_index.insert(fact).is_err()
            {
                self.usage_corrupt = true;
            }
        } else if tag == internal::Keyspace::Meta.tag()
            && meta_label(entry.key.user_key()).starts_with("usage/")
        {
            self.usage_corrupt = true;
        } else if tag == internal::Keyspace::Meta.tag() {
            // Meta is a closed keyspace. Unknown or malformed markers must
            // not be silently skipped during recovery.
            self.usage_corrupt = true;
        } else if tag == internal::Keyspace::Documents.tag() {
            self.index_identifier_entry(entry);
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
            for entry in source.range_between(start, end) {
                if entry.key.sequence() <= sequence {
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
            let Ok(entries) = reader.borrow_mut().read_between(Some(start), end) else {
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
        // Demotion removes a segment from the NVMe manifest and reader map;
        // its immutable object remains named by the tier manifest. Read all
        // such cold segments through the verified fallback before reducing
        // keys. If any named cold segment is unavailable/corrupt, fail closed
        // instead of returning a partial range that silently omits data.
        let active_ids: BTreeSet<u64> = self.manifest.segments.iter().map(|meta| meta.id).collect();
        if let Some(tier_manifest) = tier::TierManifest::read(&self.dir) {
            for object in tier_manifest
                .objects
                .iter()
                .filter(|object| object.verified && !active_ids.contains(&object.segment_id))
            {
                let Ok(entries) = tier::read_cold_segment_entries(self, object.segment_id) else {
                    return Vec::new();
                };
                for entry in entries {
                    if entry.key.user_key() >= start
                        && entry.key.user_key() < end
                        && entry.key.sequence() <= sequence
                    {
                        merged.push(entry);
                    }
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
    ///
    /// The identifier index names every collection that ever carried the
    /// identifier; probing those collections in canonical order and letting
    /// the first key with a record at or below `sequence` decide (a
    /// tombstone answers absent, exactly as the tenant scan it replaces
    /// did) reproduces the scan's semantics without materializing the
    /// tenant's whole key range.
    fn find_by_document_id(
        &self,
        tenant: &TenantId,
        id: &DocumentId,
        sequence: u64,
    ) -> Option<Document> {
        let collections = self
            .identifier_index
            .get(&(tenant.as_str().to_owned(), id.as_str().to_owned()))?;
        for collection in collections {
            let Some(collection) = CollectionName::try_new(collection).ok() else {
                continue;
            };
            // The lookup key's own sequence is irrelevant to the range the
            // point read computes; zero is a valid stand-in.
            let key = internal::InternalKey::document(tenant, &collection, id, 0);
            let Some((deletion, value)) = self.newest_document_entry(&key, sequence) else {
                continue;
            };
            if deletion {
                return None;
            }
            let mut value_reader = codec::Reader::new(&value);
            return codec::decode_document(&mut value_reader);
        }
        None
    }

    /// Encodes, appends, and applies one commit batch.
    pub(crate) fn commit_intent(&mut self, intent: &CommitIntent) -> CommitResult {
        self.commit_batch(vec![intent.clone()])
            .pop()
            .expect("a one-intent batch returns one result")
    }

    /// Commits a batch of intents under one durable append, one WAL sync,
    /// and one frontier advance: the group-commit shape.
    ///
    /// The batch is exactly equivalent to committing each intent alone in
    /// order — every step below runs per intent in sequence order except
    /// the file syncs, which run once for the whole batch. A waiter's
    /// acknowledgement still implies its records are durable (the batch's
    /// single sync covers them), and a crash mid-batch leaves the same
    /// states a crash between serial commits would.
    #[allow(clippy::too_many_lines)]
    pub(crate) fn commit_batch(&mut self, intents: Vec<CommitIntent>) -> Vec<CommitResult> {
        let mut results: Vec<Option<CommitResult>> = intents.iter().map(|_| None).collect();
        let mut prepared: Vec<PreparedCommit> = Vec::with_capacity(intents.len());
        for (index, intent) in intents.iter().enumerate() {
            match self.prepare_commit(intent) {
                Ok(mut item) => {
                    item.index = index;
                    prepared.push(item);
                }
                Err(result) => results[index] = Some(result),
            }
        }
        if prepared.is_empty() {
            return results.into_iter().map(Option::unwrap).collect();
        }

        // The pre-append fault gates now guard one durable append; a serial
        // caller fires them exactly as before (a batch of one fires once).
        if self.faults.fire(FaultPoint::CommitMarkerApplication).is_err()
            || self.faults.fire(FaultPoint::WalAppend).is_err()
            || self.faults.fire(FaultPoint::PointerPublish).is_err()
        {
            for item in &prepared {
                results[item.index] =
                    Some(CommitResult::Rejected(CommitRejection::Storage));
            }
            return results.into_iter().map(Option::unwrap).collect();
        }
        let any_synced = prepared.iter().any(|item| item.synced);
        let mut durable: Vec<Entry> = Vec::with_capacity(
            prepared.iter().map(|item| item.durable_batch.len()).sum(),
        );
        for item in &prepared {
            durable.extend(item.durable_batch.iter().cloned());
        }
        let Ok(append) = self.append_wal_batch(&durable) else {
            for item in &prepared {
                results[item.index] = Some(CommitResult::Rejected(CommitRejection::Storage));
            }
            return results.into_iter().map(Option::unwrap).collect();
        };
        if any_synced && self.faults.fire(FaultPoint::ReadyLedgerSync).is_err() {
            if self.activation.is_some() {
                self.frontier_poisoned = true;
            }
            for item in &prepared {
                results[item.index] = Some(self.finish_publication(
                    &item.entries,
                    &item.ledger_entry,
                    Some(UnknownCause::ResponseInterrupted),
                    false,
                ));
            }
            return results.into_iter().map(Option::unwrap).collect();
        }
        if self
            .witness_wal_append(&append, any_synced, FaultPoint::ReadyFrontierSync)
            .is_err()
        {
            for item in &prepared {
                results[item.index] = Some(self.finish_publication(
                    &item.entries,
                    &item.ledger_entry,
                    Some(UnknownCause::ResponseInterrupted),
                    false,
                ));
            }
            return results.into_iter().map(Option::unwrap).collect();
        }
        if any_synced
            && (self.faults.fire(FaultPoint::PointerSync).is_err()
                || self.faults.fire(FaultPoint::DirectorySync).is_err())
        {
            for item in &prepared {
                let cause = if item.synced {
                    Some(UnknownCause::PostPublicationPointerSync)
                } else {
                    None
                };
                results[item.index] = Some(self.finish_publication(
                    &item.entries,
                    &item.ledger_entry,
                    cause,
                    false,
                ));
            }
            return results.into_iter().map(Option::unwrap).collect();
        }

        // The flip frame upgrades every Synced item's proven durability in
        // one durable append; its absence after a crash leaves an honest
        // `Written` resolution, exactly as the per-commit flip did.
        let synced_items: Vec<&PreparedCommit> =
            prepared.iter().filter(|item| item.synced).collect();
        let flip_witnessed = if synced_items.is_empty() {
            true
        } else if self.faults.fire(FaultPoint::PublishedLedgerSync).is_err() {
            false
        } else {
            let mut flips: Vec<Entry> = Vec::with_capacity(synced_items.len());
            for item in &synced_items {
                flips.push(item.flip.clone());
            }
            match self.append_wal_batch(&flips) {
                Ok(flip_append) => {
                    self.witness_wal_append(&flip_append, true, FaultPoint::PublishedFrontierSync)
                        .is_ok()
                }
                Err(()) => false,
            }
        };
        for item in &prepared {
            let cause = if item.synced && !flip_witnessed {
                Some(UnknownCause::PublishedLedgerSync)
            } else {
                None
            };
            results[item.index] = Some(self.finish_publication(
                &item.entries,
                &item.ledger_entry,
                cause,
                item.synced && flip_witnessed,
            ));
        }
        results.into_iter().map(Option::unwrap).collect()
    }

    /// Validates, conflict-checks, and encodes one intent without touching
    /// durable state: the prepare phase of [`Self::commit_batch`].
    ///
    /// # Errors
    ///
    /// Returns the intent's definite result (a rejection, or the replay of
    /// an already-durable identity) without recording anything new.
    #[allow(
        clippy::too_many_lines,
        reason = "one publication order, mirrored from the root store's proven sequence"
    )]
    fn prepare_commit(&mut self, intent: &CommitIntent) -> Result<PreparedCommit, CommitResult> {
        // Step 1: validate the request without allocating anything. A
        // validation failure never binds the identity.
        if intent.writes.is_empty() || has_duplicate_targets(&intent.writes) {
            return Err(CommitResult::Rejected(CommitRejection::Validation));
        }
        // Step 2: resolve a retried identity durably.
        let identity = (intent.tenant.as_str().to_owned(), intent.operation.as_str().to_owned());
        if let Some(entry) = self.ledger_index.get(&identity) {
            if entry.digest != intent.digest {
                return Err(CommitResult::Rejected(CommitRejection::OperationIdReuse));
            }
            return Err(replay(entry));
        }
        if self.frontier_poisoned && self.activation.is_some() {
            return Err(CommitResult::Rejected(CommitRejection::Storage));
        }
        // Step 3: conflict-check every write against the snapshot and the
        // published view.
        let snapshot_sequence = intent.snapshot_sequence.get();
        let published_sequence = self.sequence_watermark;
        for write in &intent.writes {
            if let StagedWrite::Put { document, .. } = write
                && !codec::document_within_limits(document)
            {
                return Err(CommitResult::Rejected(CommitRejection::Validation));
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
                return Err(self.record_rejection(intent, CommitRejection::Conflict));
            }
        }
        // Step 4: allocate the sequence past everything this location has
        // ever named; a later failure can never let another commit reuse it.
        let Some(sequence) = self.sequence_watermark.checked_add(1) else {
            return Err(CommitResult::Rejected(CommitRejection::Storage));
        };
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
        // Usage is part of the same WAL batch as the accepted mutation. The
        // fact is minted only after validation and identity resolution, and
        // its sequence and tenant are bound in the checksummed envelope.
        let Ok(usage_fact) =
            accepted_mutation_usage(&intent.tenant, &intent.operation, sequence, &entries)
        else {
            return Err(CommitResult::Rejected(CommitRejection::Storage));
        };
        entries.push(Entry {
            key: InternalKey::meta(&usage_label(&usage_fact), sequence),
            value: usage_fact.encoded(),
        });
        self.sequence_watermark = sequence;
        // The per-intent durable filter: `Buffered` writes no document
        // bytes, and the journal-append fault skips the history record.
        // The fault fires per intent, so a serial caller fires it exactly
        // as before (a batch of one fires once).
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
        let mut synced_entry = ledger_entry.clone();
        synced_entry.post_publish_synced = true;
        let flip = Entry {
            key: InternalKey::ledger(&intent.tenant, intent.operation.as_str(), sequence),
            value: encode_ledger_value(&synced_entry),
        };
        Ok(PreparedCommit {
            index: usize::MAX,
            entries,
            durable_batch,
            ledger_entry,
            flip,
            synced: intent.durability == LocalDurability::Synced,
        })
    }

    /// Appends a complete WAL frame, rolling back an interrupted `write_all`
    /// so an append error remains a definite pre-publication failure.
    fn append_wal_batch(&mut self, entries: &[Entry]) -> Result<wal::WalAppend, ()> {
        if self.frontier_poisoned && self.activation.is_some() {
            return Err(());
        }
        let start = self.wal.len();
        if let Ok(append) = self.wal.append(entries) {
            Ok(append)
        } else {
            if self.wal.truncate(start).is_err() {
                self.frontier_poisoned = true;
            }
            Err(())
        }
    }

    /// Syncs an append as required and advances the independent frontier
    /// before any in-memory state can expose the batch.
    fn witness_wal_append(
        &mut self,
        append: &wal::WalAppend,
        legacy_sync_required: bool,
        specific_fault: FaultPoint,
    ) -> Result<(), ()> {
        let active = self.activation.is_some();
        if (active || legacy_sync_required) && self.wal.sync().is_err() {
            if active {
                self.frontier_poisoned = true;
            }
            return Err(());
        }
        if active {
            if self.faults.fire(FaultPoint::StorageFrontierSync).is_err()
                || self.faults.fire(specific_fault).is_err()
            {
                self.frontier_poisoned = true;
                return Err(());
            }
            let Some(frontier) = self.wal_frontier.as_mut() else {
                self.frontier_poisoned = true;
                return Err(());
            };
            if frontier.advance(append).is_err() {
                self.frontier_poisoned = true;
                return Err(());
            }
        } else if self
            .wal_frontier
            .as_mut()
            .is_some_and(|frontier| frontier.advance(append).is_err())
        {
            // The legacy shadow is disposable and must not change the old
            // writer contract. It only blocks activation until a reopen
            // rebuilds it from the streams.
            self.frontier_poisoned = true;
        }
        Ok(())
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
            // Persist the post-fsync proof in the memtable as well as the
            // operation index. Compaction may truncate the WAL immediately
            // after this call, so the flip must be present in the segment it
            // leaves behind or recovery would downgrade Synced to Written.
            let key = entries
                .iter()
                .find(|entry| entry.key.tag() == internal::Keyspace::Ledger.tag())
                .map(|entry| entry.key.clone())
                .expect("a published batch carries its ledger entry");
            let flip = Entry { key, value: encode_ledger_value(&synced_entry) };
            self.apply_entry_to_memory(&flip);
        }
        self.root_head = StorageRootId::from_recorded(ledger_entry.root.clone());
        if !self.frontier_poisoned {
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
        }
        CommitResult::Committed(StorageReceipt {
            sequence: StorageSequence::new(ledger_entry.sequence),
            root: StorageRootId::from_recorded(ledger_entry.root.clone()),
            durability: ledger_entry.requested,
        })
    }

    /// Records one conflict rejection durably, then reports it.
    fn record_rejection(&mut self, intent: &CommitIntent, reason: CommitRejection) -> CommitResult {
        if self.frontier_poisoned {
            return CommitResult::Rejected(CommitRejection::Storage);
        }
        let Some(sequence) = self.sequence_watermark.checked_add(1) else {
            return CommitResult::Rejected(CommitRejection::Storage);
        };
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
        let persisted = Entry {
            key: InternalKey::ledger(&intent.tenant, intent.operation.as_str(), sequence),
            value: encode_ledger_value(&entry),
        };
        let batch = vec![persisted.clone()];
        let Ok(append) = self.append_wal_batch(&batch) else {
            return CommitResult::Rejected(CommitRejection::Storage);
        };
        if self.witness_wal_append(&append, true, FaultPoint::RejectedFrontierSync).is_err() {
            return CommitResult::Rejected(CommitRejection::Storage);
        }
        self.apply_entry_to_memory(&persisted);
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

/// Validates the logical meaning of a structurally decoded WAL or segment
/// entry. Checksums prove byte consistency, not that hostile bytes describe a
/// legal `TerraBase` key/value pair.
fn persisted_entry_is_semantically_valid(entry: &Entry) -> bool {
    let tag = entry.key.tag();
    if tag == internal::Keyspace::Documents.tag() {
        if entry.key.is_deletion() {
            return entry.value.is_empty();
        }
        let Some((_tenant, collection, document)) = decode_document_key(entry.key.user_key())
        else {
            return false;
        };
        let mut reader = codec::Reader::new(&entry.value);
        let Some(decoded) = codec::decode_document(&mut reader) else {
            return false;
        };
        reader.remaining() == 0
            && decoded.collection().as_str() == collection
            && decoded.id().as_str() == document
    } else if tag == internal::Keyspace::Ledger.tag() {
        let Some(record) = decode_ledger_value(&entry.value) else {
            return false;
        };
        record.sequence == entry.key.sequence()
            && if record.committed {
                record.rejection.is_none()
                    && record.root.len() == 32
                    && record.root.bytes().all(|byte| byte.is_ascii_hexdigit())
            } else {
                record.rejection.is_some() && record.root.is_empty() && !record.post_publish_synced
            }
    } else if tag == internal::Keyspace::History.tag() {
        decode_history_value(&entry.value)
            .is_some_and(|commit| commit.sequence().get() == entry.key.sequence())
    } else if tag == internal::Keyspace::Meta.tag() {
        let label = meta_label(entry.key.user_key());
        if label == META_HISTORY_FLOOR {
            decode_meta_bound(&entry.value).is_some()
        } else if label.starts_with(USAGE_LABEL_PREFIX) {
            decode_usage_entry(entry).is_some()
        } else {
            false
        }
    } else {
        false
    }
}

/// Returns the meta label of one meta-keyspace user key.
fn meta_label(user_key: &[u8]) -> String {
    let mut reader = codec::Reader::new(user_key);
    reader.read_u8();
    reader.read_string().unwrap_or_default()
}

const USAGE_LABEL_PREFIX: &str = "usage/";

fn usage_label(fact: &DurableUsageFact) -> String {
    format!("{USAGE_LABEL_PREFIX}{:020}:{}", fact.sequence(), fact.fact().identity().id())
}

/// Builds the origin usage fact for one accepted mutation.
///
/// The current engine seam carries tenant and operation identity, but not the
/// public Project/Database/Partition placement context. Until that context is
/// admitted by the higher API layer, those three scope components are bound to
/// the validated tenant. This is conservative: facts cannot cross tenants or
/// accidentally claim another database's usage.
fn accepted_mutation_usage(
    tenant: &TenantId,
    operation: &OperationId,
    sequence: u64,
    entries: &[Entry],
) -> Result<DurableUsageFact, ()> {
    let region = crate::replication::RegionId::try_new("local").map_err(|_| ())?;
    let scope = UsageScope::try_new(
        tenant.clone(),
        tenant.as_str(),
        tenant.as_str(),
        tenant.as_str(),
        region,
    )
    .map_err(|_| ())?;
    let mut bytes = 0_usize;
    for entry in entries {
        if entry.key.tag() == internal::Keyspace::Documents.tag() {
            bytes = bytes.checked_add(entry.value.len()).ok_or(())?;
        }
    }
    // A delete carries no value bytes but is still one accepted origin event.
    let delta = i128::try_from(bytes.max(1)).map_err(|_| ())?;
    let end = sequence.checked_add(1).ok_or(())?;
    let definition =
        MeterCatalog::initial().get(MeterKind::StoredByteHours, 1).copied().ok_or(())?;
    let id = UsageFactId::try_new(operation.as_str()).map_err(|_| ())?;
    let fact = UsageFact::try_new(
        definition,
        scope,
        MintingIdentity::AcceptedOriginCommit(id),
        MeasurementWindow::interval(sequence, end).map_err(|_| ())?,
        delta,
    )
    .map_err(|_| ())?;
    DurableUsageFact::try_new(fact, sequence, 1).map_err(|_| ())
}

fn decode_usage_entry(entry: &Entry) -> Option<DurableUsageFact> {
    let label = meta_label(entry.key.user_key());
    let suffix = label.strip_prefix(USAGE_LABEL_PREFIX)?;
    let (sequence, identity) = suffix.split_once(':')?;
    let sequence = sequence.parse::<u64>().ok()?;
    if sequence != entry.key.sequence() {
        return None;
    }
    let durable = DurableUsageFact::decode(&entry.value).ok()?;
    if durable.sequence() != sequence
        || durable.fact().identity().id().as_str() != identity
        || durable.fact().identity().rule() != crate::usage::MintingRule::AcceptedOriginCommit
    {
        return None;
    }
    Some(durable)
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
    let committed = match reader.read_u8()? {
        0 => false,
        1 => true,
        _ => return None,
    };
    let post_publish_synced = match reader.read_u8()? {
        0 => false,
        1 => true,
        _ => return None,
    };
    let rejection = match reader.read_u8()? {
        0 => None,
        code => Some(decode_rejection(code)?),
    };
    if reader.remaining() != 0 {
        return None;
    }
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
    if codec::fnv1a(payload) != checksum || reader.remaining() != 0 {
        return None;
    }
    ledger::decode_commit_payload(payload)
}

/// Decodes one meta bound value.
fn decode_meta_bound(bytes: &[u8]) -> Option<u64> {
    let mut reader = codec::Reader::new(bytes);
    let bound = reader.read_u64()?;
    (reader.remaining() == 0).then_some(bound)
}

/// Maps a durability level to its one-byte code.
fn durability_code(level: LocalDurability) -> u8 {
    match level {
        LocalDurability::Buffered => 0,
        LocalDurability::Written => 1,
        LocalDurability::Synced => 2,
    }
}

fn verify_or_create_format(dir: &Path) -> Result<FormatOpen, LsmOpenError> {
    let path = dir.join("format");
    let bytes = match fs::read(&path) {
        Ok(bytes) => bytes,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
            let mut entries = fs::read_dir(dir).map_err(|_| LsmOpenError::Unavailable)?;
            if entries.next().transpose().map_err(|_| LsmOpenError::Unavailable)?.is_some() {
                return Err(LsmOpenError::FormatMismatch);
            }
            let mut file = fs::File::create(&path).map_err(|_| LsmOpenError::Unavailable)?;
            file.write_all(FORMAT_MARKER.as_bytes()).map_err(|_| LsmOpenError::Unavailable)?;
            file.sync_all().map_err(|_| LsmOpenError::Unavailable)?;
            return Ok(FormatOpen { state: FormatState::Legacy, created: true });
        }
        Err(_) => return Err(LsmOpenError::Unavailable),
    };
    if std::str::from_utf8(&bytes).is_ok_and(|text| text.trim() == FORMAT_MARKER) {
        return Ok(FormatOpen { state: FormatState::Legacy, created: false });
    }
    decode_active_format(&bytes)
        .map(|active| FormatOpen { state: FormatState::Active(active), created: false })
}

fn encode_active_format(active: &ActiveFormat) -> Vec<u8> {
    let mut bytes = Vec::new();
    bytes.extend_from_slice(FORMAT_MARKER.as_bytes());
    bytes.extend_from_slice(ACTIVE_FORMAT_MAGIC);
    codec::push_u64(&mut bytes, FRONTIER_FORMAT_MAJOR);
    codec::push_u64(&mut bytes, STORAGE_FRONTIER_FEATURE);
    bytes.extend_from_slice(&active.store_id.as_bytes());
    let identity = active.activation.activation_id.as_str().as_bytes();
    let identity_len =
        u16::try_from(identity.len()).expect("a validated operation identity fits u16");
    codec::push_u16(&mut bytes, identity_len);
    bytes.extend_from_slice(identity);
    codec::push_u64(&mut bytes, active.activation.external_fence_generation);
    bytes.extend_from_slice(&active.activation.external_record_digest);
    bytes.extend_from_slice(&[0_u8; ACTIVE_FORMAT_RESERVED]);
    let checksum = active_format_checksum(&bytes);
    bytes.extend_from_slice(&checksum);
    bytes
}

fn decode_active_format(bytes: &[u8]) -> Result<ActiveFormat, LsmOpenError> {
    const FIXED_WITHOUT_ID: usize = 9 + 8 + 8 + 8 + 16 + 2 + 8 + 32 + 8 + 32;
    if bytes.len() < FIXED_WITHOUT_ID + 1
        || !bytes.starts_with(FORMAT_MARKER.as_bytes())
        || bytes.get(FORMAT_MARKER.len()..FORMAT_MARKER.len() + 8) != Some(ACTIVE_FORMAT_MAGIC)
    {
        return Err(LsmOpenError::FormatMismatch);
    }
    let checksum_start = bytes.len() - 32;
    if bytes[checksum_start..] != active_format_checksum(&bytes[..checksum_start]) {
        return Err(LsmOpenError::FormatMismatch);
    }
    let body_start = FORMAT_MARKER.len() + ACTIVE_FORMAT_MAGIC.len();
    let mut reader = codec::Reader::new(&bytes[body_start..checksum_start]);
    if reader.read_u64() != Some(FRONTIER_FORMAT_MAJOR)
        || reader.read_u64() != Some(STORAGE_FRONTIER_FEATURE)
    {
        return Err(LsmOpenError::FormatMismatch);
    }
    let mut store_id = [0_u8; 16];
    store_id.copy_from_slice(reader.take(16).ok_or(LsmOpenError::FormatMismatch)?);
    let identity_len = reader.read_u16().ok_or(LsmOpenError::FormatMismatch)? as usize;
    let identity_text =
        std::str::from_utf8(reader.take(identity_len).ok_or(LsmOpenError::FormatMismatch)?)
            .map_err(|_| LsmOpenError::FormatMismatch)?;
    let activation_id =
        OperationId::try_new(identity_text).map_err(|_| LsmOpenError::FormatMismatch)?;
    let external_fence_generation = reader.read_u64().ok_or(LsmOpenError::FormatMismatch)?;
    if external_fence_generation == 0 {
        return Err(LsmOpenError::FormatMismatch);
    }
    let mut external_record_digest = [0_u8; 32];
    external_record_digest.copy_from_slice(reader.take(32).ok_or(LsmOpenError::FormatMismatch)?);
    if reader.take(ACTIVE_FORMAT_RESERVED) != Some(&[0_u8; ACTIVE_FORMAT_RESERVED])
        || reader.remaining() != 0
    {
        return Err(LsmOpenError::FormatMismatch);
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

fn write_active_format(
    dir: &Path,
    active: &ActiveFormat,
    faults: &mut FaultPlan,
) -> Result<(), LsmOpenError> {
    let temp = dir.join("format.tmp");
    let live = dir.join("format");
    let mut file = fs::OpenOptions::new()
        .create(true)
        .truncate(true)
        .write(true)
        .open(&temp)
        .map_err(|_| LsmOpenError::Unavailable)?;
    faults.fire(FaultPoint::ActivationFormatTempCreated).map_err(|_| LsmOpenError::Unavailable)?;
    file.write_all(&encode_active_format(active)).map_err(|_| LsmOpenError::Unavailable)?;
    faults.fire(FaultPoint::ActivationFormatWritten).map_err(|_| LsmOpenError::Unavailable)?;
    file.sync_all().map_err(|_| LsmOpenError::Unavailable)?;
    faults.fire(FaultPoint::ActivationFormatSynced).map_err(|_| LsmOpenError::Unavailable)?;
    fs::rename(&temp, &live).map_err(|_| LsmOpenError::Unavailable)?;
    faults.fire(FaultPoint::ActivationFormatRenamed).map_err(|_| LsmOpenError::Unavailable)?;
    super::root::sync_dir(dir).map_err(|_| LsmOpenError::Unavailable)?;
    faults.fire(FaultPoint::ActivationFormatDirectorySynced).map_err(|_| LsmOpenError::Unavailable)
}

fn active_format_checksum(bytes: &[u8]) -> [u8; 32] {
    let mut hasher = blake3::Hasher::new();
    hasher.update(ACTIVE_FORMAT_DOMAIN);
    hasher.update(bytes);
    *hasher.finalize().as_bytes()
}

fn has_segment_artifacts(dir: &Path) -> Result<bool, LsmOpenError> {
    let entries = fs::read_dir(dir).map_err(|_| LsmOpenError::Unavailable)?;
    Ok(entries.filter_map(Result::ok).any(|entry| {
        entry.file_name().to_str().is_some_and(|name| {
            name.starts_with("seg-")
                && Path::new(name).extension().is_some_and(|extension| extension == "lsm")
        })
    }))
}

fn recover_active_retained(
    dir: &Path,
    retained_path: &Path,
    retained: &mut super::retained::RetainedWriter,
    scan: &super::retained::RetainedScan,
    store_id: StoreId,
) -> Result<RetainedFrontier, LsmOpenError> {
    let verified = VerifiedStream::for_retained(retained_path, scan, store_id)
        .map_err(|_| LsmOpenError::Unavailable)?;
    let mut frontier =
        RetainedFrontier::open_active(dir, store_id).map_err(map_frontier_open_error)?;
    frontier.repair_or_advance(&verified).map_err(map_frontier_open_error)?;
    if scan.total_bytes > scan.good_bytes {
        retained.truncate(scan.good_bytes).map_err(|_| LsmOpenError::Unavailable)?;
    }
    Ok(frontier)
}

fn recover_active_wal(
    dir: &Path,
    wal_path: &Path,
    wal: &mut WalLog,
    scan: &wal::WalScan,
    store_id: StoreId,
    manifest: ActiveWalManifest,
) -> Result<(WalFrontier, bool), LsmOpenError> {
    let mut frontier = WalFrontier::open_active(dir, store_id).map_err(map_frontier_open_error)?;
    if manifest.checkpoint.is_some() && frontier.epoch_established(manifest.live_epoch) {
        if scan.total_bytes != 0 {
            wal.truncate(0).map_err(|_| LsmOpenError::Unavailable)?;
        }
        return Ok((frontier, false));
    }
    if scan.mid_file_damage {
        return Err(LsmOpenError::FormatMismatch);
    }
    let selected = frontier.anchor();
    if selected.epoch == manifest.live_epoch {
        let live = VerifiedStream::for_wal(wal_path, scan, store_id, manifest.live_epoch)
            .map_err(|_| LsmOpenError::Unavailable)?;
        if selected == live.empty_anchor()
            && let Some(checkpoint) = manifest.checkpoint
        {
            let old = VerifiedStream::for_wal(wal_path, scan, store_id, checkpoint.anchor.epoch)
                .map_err(|_| LsmOpenError::Unavailable)?;
            if old.final_anchor() == checkpoint.anchor && scan.good_bytes == checkpoint.anchor.bytes
            {
                for slot in frontier.slots().iter().flatten() {
                    let valid = if slot.anchor.epoch == manifest.live_epoch {
                        slot.anchor == live.empty_anchor()
                    } else if slot.anchor.epoch == checkpoint.anchor.epoch {
                        old.contains(slot.anchor)
                    } else {
                        false
                    };
                    if !valid {
                        return Err(LsmOpenError::FormatMismatch);
                    }
                }
                frontier
                    .ensure_epoch_established(manifest.live_epoch)
                    .map_err(map_frontier_open_error)?;
                wal.truncate(0).map_err(|_| LsmOpenError::Unavailable)?;
                return Ok((frontier, false));
            }
        }
        frontier.repair_or_advance(&live).map_err(map_frontier_open_error)?;
        if scan.total_bytes > scan.good_bytes {
            wal.truncate(scan.good_bytes).map_err(|_| LsmOpenError::Unavailable)?;
        }
        return Ok((frontier, true));
    }

    let checkpoint = manifest.checkpoint.ok_or(LsmOpenError::FormatMismatch)?;
    if selected != checkpoint.anchor {
        return Err(LsmOpenError::FormatMismatch);
    }
    let old = VerifiedStream::for_wal(wal_path, scan, store_id, checkpoint.anchor.epoch)
        .map_err(|_| LsmOpenError::Unavailable)?;
    if old.final_anchor() != checkpoint.anchor || scan.good_bytes != checkpoint.anchor.bytes {
        return Err(LsmOpenError::FormatMismatch);
    }
    for slot in frontier.slots().iter().flatten() {
        if slot.anchor.epoch != checkpoint.anchor.epoch || !old.contains(slot.anchor) {
            return Err(LsmOpenError::FormatMismatch);
        }
    }
    frontier
        .write_epoch_zero(checkpoint.next_epoch)
        .and_then(|()| frontier.write_epoch_zero(checkpoint.next_epoch))
        .map_err(map_frontier_open_error)?;
    wal.truncate(0).map_err(|_| LsmOpenError::Unavailable)?;
    Ok((frontier, false))
}

fn map_frontier_open_error(error: FrontierError) -> LsmOpenError {
    match error {
        FrontierError::Integrity => LsmOpenError::FormatMismatch,
        FrontierError::Unavailable => LsmOpenError::Unavailable,
    }
}

fn map_activation_frontier_error(error: FrontierError) -> LedgerFrontierActivationError {
    match error {
        FrontierError::Integrity => LedgerFrontierActivationError::IntegrityFailure,
        FrontierError::Unavailable => LedgerFrontierActivationError::StorageUnavailable,
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

    fn commit_batch(&mut self, intents: Vec<CommitIntent>) -> Vec<CommitResult> {
        LsmStorage::commit_batch(self, intents)
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

    fn ledger_frontier_status(&self) -> LedgerFrontierStatus {
        if self.frontier_poisoned {
            return LedgerFrontierStatus::Unavailable;
        }
        let Some(observation) = self.frontier_observation() else {
            return LedgerFrontierStatus::Unavailable;
        };
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
        if self.frontier_poisoned {
            return Err(LedgerFrontierActivationError::StorageUnavailable);
        }
        if let Some(active) = &self.activation {
            if active != &request.identity {
                return Err(LedgerFrontierActivationError::AlreadyActivatedByDifferentRecord);
            }
            let observation = self
                .frontier_observation()
                .ok_or(LedgerFrontierActivationError::IntegrityFailure)?;
            return Ok(LedgerFrontierActivationReceipt { identity: active.clone(), observation });
        }
        if request.identity.external_fence_generation == 0 {
            return Err(LedgerFrontierActivationError::IntegrityFailure);
        }
        if self.frontier_observation().as_ref() != Some(&request.expected) {
            return Err(LedgerFrontierActivationError::StaleObservation);
        }
        let wal_path = self.dir.join("wal");
        let retained_path = self.dir.join("retained");
        let wal_scan =
            self.wal.scan().map_err(|_| LedgerFrontierActivationError::StorageUnavailable)?;
        let retained_scan = super::retained::RetainedIndex::scan(&retained_path);
        if wal_scan.mid_file_damage
            || wal_scan.good_bytes != wal_scan.total_bytes
            || retained_scan.mid_file_damage
            || retained_scan.good_bytes != retained_scan.total_bytes
        {
            return Err(LedgerFrontierActivationError::IntegrityFailure);
        }
        let wal_epoch = self
            .wal_frontier
            .as_ref()
            .ok_or(LedgerFrontierActivationError::IntegrityFailure)?
            .anchor()
            .epoch;
        let verified_wal = VerifiedStream::for_wal(&wal_path, &wal_scan, self.store_id, wal_epoch)
            .map_err(|_| LedgerFrontierActivationError::StorageUnavailable)?;
        let verified_retained =
            VerifiedStream::for_retained(&retained_path, &retained_scan, self.store_id)
                .map_err(|_| LedgerFrontierActivationError::StorageUnavailable)?;
        if self.wal_frontier.as_ref().map(WalFrontier::anchor) != Some(verified_wal.final_anchor())
            || self.retained_frontier.as_ref().map(RetainedFrontier::anchor)
                != Some(verified_retained.final_anchor())
        {
            return Err(LedgerFrontierActivationError::IntegrityFailure);
        }
        self.wal
            .sync()
            .and_then(|()| self.retained.sync())
            .map_err(|_| LedgerFrontierActivationError::StorageUnavailable)?;
        let wal_frontier = WalFrontier::rebuild_shadow(
            &self.dir,
            self.store_id,
            wal_epoch,
            verified_wal.final_anchor(),
        )
        .map_err(map_activation_frontier_error)?;
        let retained_frontier = RetainedFrontier::rebuild_shadow(
            &self.dir,
            self.store_id,
            verified_retained.final_anchor(),
        )
        .map_err(map_activation_frontier_error)?;
        self.wal_frontier = Some(wal_frontier);
        self.retained_frontier = Some(retained_frontier);

        let mut activated_manifest = self.manifest.clone();
        activated_manifest.epoch = activated_manifest.epoch.saturating_add(1);
        activated_manifest.active_wal =
            Some(ActiveWalManifest { live_epoch: wal_epoch, checkpoint: None });
        if activated_manifest.publish_activation(&self.dir, &mut self.faults).is_err() {
            self.frontier_poisoned = true;
            return Err(LedgerFrontierActivationError::StorageUnavailable);
        }
        self.manifest = activated_manifest;
        let active = ActiveFormat { store_id: self.store_id, activation: request.identity.clone() };
        if write_active_format(&self.dir, &active, &mut self.faults).is_err() {
            self.frontier_poisoned = true;
            self.activation = Some(request.identity.clone());
            return Err(LedgerFrontierActivationError::StorageUnavailable);
        }
        self.format_major = FRONTIER_FORMAT_MAJOR;
        self.activation = Some(request.identity.clone());
        let observation =
            self.frontier_observation().expect("activation retains both frontier handles");
        Ok(LedgerFrontierActivationReceipt { identity: request.identity.clone(), observation })
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

    fn mover_pass(
        &mut self,
        policy: &tier::TierMovePolicy,
        home_region: &str,
    ) -> Result<tier::MoverOutcome, tier::TierUnsupported> {
        if self.frontier_poisoned && self.activation.is_some() {
            return Err(tier::TierUnsupported);
        }
        // The engine-owned local Tier-2 store: content-addressed objects
        // beside the segments. Remote object stores arrive with the
        // cold-tier landing; the seam stays typed.
        let mut store = object_store::LocalObjectStore::open(&self.dir.join("tier-objects"))
            .map_err(|_| tier::TierUnsupported)?;
        Ok(tier::run_mover(self, &mut store, policy, home_region))
    }

    fn tier_snapshot(&self) -> Option<tier::TierSnapshot> {
        let manifest = tier::TierManifest::read(&self.dir);
        Some(tier::TierSnapshot {
            manifest_epoch: manifest.as_ref().map_or(0, |manifest| manifest.epoch),
            object_count: manifest.map_or(0, |manifest| manifest.objects.len()),
            nvme_bytes: self.manifest.segments.iter().map(|meta| meta.byte_len).sum(),
        })
    }

    fn flush(&mut self) -> Result<(), ()> {
        if self.frontier_poisoned && self.activation.is_some() {
            return Err(());
        }
        self.wal.sync().map_err(|_| ())
    }

    #[cfg(test)]
    fn arm_test_fault(&mut self, point: FaultPoint) {
        self.faults.arm(point);
    }
}

/// Crate-private accessors the compact module and tests use.
impl LsmStorage {
    fn frontier_observation(&self) -> Option<LedgerFrontierObservation> {
        let wal = self.wal_frontier.as_ref()?;
        let retained = self.retained_frontier.as_ref()?;
        let wal_anchor = wal.anchor();
        let retained_anchor = retained.anchor();
        Some(LedgerFrontierObservation {
            format_major: self.format_major,
            frontier_generation: wal.generation(),
            ledger_bytes: wal_anchor.bytes,
            record_count: wal_anchor.frame_count,
            chain_head: wal_anchor.chain_head,
            retained_bytes: retained_anchor.bytes,
            retained_record_count: retained_anchor.frame_count,
            retained_chain_head: retained_anchor.chain_head,
        })
    }

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

    /// Returns the durable usage facts in stable identity order.
    #[cfg(test)]
    pub(super) fn usage_facts_impl(&self) -> Vec<DurableUsageFact> {
        self.usage_index.facts().cloned().collect()
    }

    /// Publishes a manifest bound to the exact old WAL anchor, establishes
    /// both slots of the next epoch, then truncates and syncs the old WAL.
    pub(super) fn publish_wal_checkpoint_impl(&mut self) -> Result<(), ()> {
        if !self.frozen.is_empty() {
            return Err(());
        }
        let active = self.activation.is_some();
        let mut candidate = self.manifest.clone();
        candidate.epoch = candidate.epoch.checked_add(1).ok_or(())?;
        candidate.wal_checkpoint = self.sequence_watermark;
        let current_anchor = self.wal_frontier.as_ref().ok_or(())?.anchor();
        let frontier_next_epoch = current_anchor.epoch.checked_add(1).ok_or(())?;
        let next_epoch = if active {
            let anchor = current_anchor;
            let next_epoch = frontier_next_epoch;
            candidate.active_wal = Some(ActiveWalManifest {
                live_epoch: next_epoch,
                checkpoint: Some(WalCheckpoint { anchor, next_epoch }),
            });
            Some(next_epoch)
        } else {
            candidate.active_wal = None;
            None
        };
        if self.faults.fire(FaultPoint::CompactionCheckpointPublish).is_err() {
            return Err(());
        }
        if candidate.publish(&self.dir).is_err() {
            self.frontier_poisoned = true;
            return Err(());
        }
        self.manifest = candidate;
        if self.faults.fire(FaultPoint::CompactionCheckpointPublished).is_err() {
            if active {
                self.frontier_poisoned = true;
            }
            return Err(());
        }
        if let Some(next_epoch) = next_epoch {
            let Some(frontier) = self.wal_frontier.as_mut() else {
                self.frontier_poisoned = true;
                return Err(());
            };
            if frontier.write_epoch_zero(next_epoch).is_err() {
                self.frontier_poisoned = true;
                return Err(());
            }
            if self.faults.fire(FaultPoint::CompactionWalFrontierFirstSlot).is_err() {
                self.frontier_poisoned = true;
                return Err(());
            }
            if frontier.write_epoch_zero(next_epoch).is_err() {
                self.frontier_poisoned = true;
                return Err(());
            }
            if self.faults.fire(FaultPoint::CompactionWalFrontierSecondSlot).is_err() {
                self.frontier_poisoned = true;
                return Err(());
            }
        }
        if self.faults.fire(FaultPoint::CompactionWalTruncate).is_err() {
            if active {
                self.frontier_poisoned = true;
            }
            return Err(());
        }
        if self.wal.set_len_unsynced(0).is_err() {
            if active {
                self.frontier_poisoned = true;
            }
            return Err(());
        }
        let wal_sync_failed = self.faults.fire(FaultPoint::CompactionWalSync).is_err()
            || self.wal.sync_all().is_err();
        if wal_sync_failed {
            if active {
                self.frontier_poisoned = true;
            } else {
                let _ = self.reset_legacy_wal_shadow(frontier_next_epoch);
            }
            return Err(());
        }
        if !active {
            self.reset_legacy_wal_shadow(frontier_next_epoch)?;
        }
        Ok(())
    }

    fn reset_legacy_wal_shadow(&mut self, next_epoch: u64) -> Result<(), ()> {
        if self.faults.fire(FaultPoint::CompactionLegacyFrontierReset).is_err() {
            self.frontier_poisoned = true;
            return Err(());
        }
        let Some(frontier) = self.wal_frontier.as_mut() else {
            self.frontier_poisoned = true;
            return Err(());
        };
        if frontier.write_epoch_zero(next_epoch).is_err()
            || frontier.write_epoch_zero(next_epoch).is_err()
        {
            // A legacy shadow never changes the old writer contract. Reopen
            // rebuilds it from the stream if this best-effort reset fails.
            self.frontier_poisoned = true;
            return Err(());
        }
        Ok(())
    }

    /// Appends the window's retained facts and declares the bound durably.
    pub(super) fn append_retained_facts_impl(
        &mut self,
        facts: &[super::retained::RetainedFact],
        bound: u64,
        head: u64,
        non_deletion: &[(TenantId, Option<StorageSequence>)],
    ) -> Result<(), ()> {
        if self.frontier_poisoned && self.activation.is_some() {
            return Err(());
        }
        let start = self.retained.len();
        let mut appended = Vec::new();
        let mut durable_appends = Vec::new();
        for fact in facts {
            // Check the index before writing. A retry can revisit the same
            // evidence after the first append was synced; appending first
            // would grow the durable stream with an indistinguishable
            // duplicate even though the in-memory index correctly dedups.
            if !self.retained_index.contains_fact(fact) {
                if let Ok(append) = self.retained.append_fact(fact) {
                    durable_appends.push(append);
                } else {
                    if self.retained.truncate(start).is_err() {
                        self.frontier_poisoned = true;
                    }
                    return Err(());
                }
                appended.push(fact.clone());
            }
        }
        if let Ok(append) = self.retained.append_bound(bound, head, non_deletion) {
            durable_appends.push(append);
        } else {
            if self.retained.truncate(start).is_err() {
                self.frontier_poisoned = true;
            }
            return Err(());
        }
        if self.faults.fire(FaultPoint::CompactionEvidenceSync).is_err()
            || self.retained.sync().is_err()
        {
            if self.activation.is_some() {
                self.frontier_poisoned = true;
            }
            return Err(());
        }
        if self.activation.is_some() {
            if self.faults.fire(FaultPoint::RetainedFrontierSync).is_err() {
                self.frontier_poisoned = true;
                return Err(());
            }
            let Some(frontier) = self.retained_frontier.as_mut() else {
                self.frontier_poisoned = true;
                return Err(());
            };
            if frontier.advance(&durable_appends).is_err() {
                self.frontier_poisoned = true;
                return Err(());
            }
        } else if self
            .retained_frontier
            .as_mut()
            .is_some_and(|frontier| frontier.advance(&durable_appends).is_err())
        {
            // The format-1 frontier is a non-authoritative shadow. Its
            // failure must not poison durable application state; activation
            // rescans both streams and refuses a stale shadow until reopen
            // repairs it. Format 2 takes the fail-closed branch above.
        }
        for fact in appended {
            self.retained_index.upsert_fact(fact);
        }
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
        let append = self.append_wal_batch(&batch)?;
        self.witness_wal_append(&append, true, FaultPoint::ReadyFrontierSync)?;
        self.sequence_watermark += 1;
        self.apply_entry_to_memory(&batch[0]);
        self.history_index.retain(|sequence, _| *sequence >= bound);
        Ok(())
    }
}
