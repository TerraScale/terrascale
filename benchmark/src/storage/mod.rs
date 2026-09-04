//! Private storage: one `SQLite` file per database (D-27/D-42).
//!
//! This module owns the durable representation of accepted writes. It exposes
//! one crate-private port (`StoragePort`) that the Engine calls, one
//! implementation of that port (`SqliteStorage`), and one immutable
//! `CommitPublicationEvent` emitted after storage locks release. Raw bytes,
//! pages, and lock state never leave this module: the Engine receives typed
//! intents, receipts, and resolutions only. Documents, operation-ledger row,
//! and publication-history journal row commit in one `SQLite` transaction.

mod codec;
#[cfg(test)]
mod faults;
mod intent;
mod ownership;
mod sqlite;

#[cfg(test)]
pub(crate) use faults::FaultPoint;
pub(crate) use intent::{
    CommitIntent, CommitRejection, CommitResult, OperationResolution, StagedWrite, UnknownCause,
};
pub use ownership::{DatabaseOwner, EnvironmentName, OwnershipError};
pub(crate) use ownership::{OwnershipMode, load_or_create};
pub(crate) use sqlite::SqliteStorage;
pub use sqlite::database_file;

/// Failures opening one durable location.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum StorageOpenError {
    /// The location is not an owned SQLite database directory.
    FormatMismatch,
    /// The location cannot be opened or recovered.
    Unavailable,
}

/// The identity of one published storage state.
///
/// One opaque head string the implementation chains on publish; consumers
/// treat it as an opaque publication identity, never a path or a format.
#[derive(Clone, Debug, Eq, Ord, PartialEq, PartialOrd)]
pub(crate) struct StorageRootId(String);

impl StorageRootId {
    /// Wraps one recorded publication-identity string.
    pub(crate) fn from_recorded(value: String) -> Self {
        Self(value)
    }

    /// Returns the recorded identity text.
    #[cfg(test)]
    pub(crate) fn as_str(&self) -> &str {
        &self.0
    }
}

impl std::fmt::Display for StorageRootId {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter.write_str(&self.0)
    }
}

use crate::types::{
    CollectionName, Document, DocumentId, LocalDurability, OperationId, StorageSequence, TenantId,
};
use crate::usage::{DurableUsageFact, MeasurementWindow, MintingRule, UsageFact, UsageFactId};

/// Storage-side usage command failures, mapped to the public Engine contract.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum UsageStorageError {
    /// The selected storage backend predates durable usage facts.
    UnsupportedBackend,
    /// Persisted usage is malformed or contradicts its durable binding.
    Corrupt,
    /// One scoped fact identity is already bound to different content.
    IdentityConflict,
    /// Durable storage could not complete the command.
    Unavailable,
}

/// Failures while serving a bounded snapshot page.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum SnapshotReadError {
    /// A persisted document value or key failed semantic decoding.
    Integrity,
}

/// One pushable condition of a collection scan filter (D-42): a scalar
/// comparison against one field's extracted index column. The comparison
/// semantics are `tql/1.0`'s: strings compare by code point, numbers compare
/// numerically across int/float, and a condition whose value kind the
/// extracted columns do not carry is never pushed.
#[derive(Clone, Debug, PartialEq)]
pub struct ScanCondition {
    /// The dotted field path whose extracted column the condition names.
    pub(crate) field: String,
    /// The comparison.
    pub(crate) op: ScanOp,
    /// The comparison value; a string or a number by construction.
    pub(crate) value: crate::types::DocumentValue,
}

/// The pushable comparison family.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ScanOp {
    /// `=`
    Equals,
    /// `!=`
    NotEquals,
    /// `<`
    Less,
    /// `<=`
    LessOrEqual,
    /// `>`
    Greater,
    /// `>=`
    GreaterOrEqual,
}

/// The condition conjunction pushed onto one collection scan.
///
/// The set is bounded: a filter beyond [`MAX_SCAN_CONDITIONS`] conditions is
/// refused at construction rather than silently truncated.
#[derive(Clone, Debug, PartialEq)]
pub struct ScanFilter {
    conditions: Vec<ScanCondition>,
}

/// The maximum number of conditions one pushed scan filter carries.
pub const MAX_SCAN_CONDITIONS: usize = 16;

impl ScanFilter {
    /// Creates a filter from its conjuncts.
    ///
    /// # Errors
    ///
    /// Returns `None` when the condition list exceeds
    /// [`MAX_SCAN_CONDITIONS`].
    #[must_use]
    pub fn try_new(conditions: Vec<ScanCondition>) -> Option<Self> {
        (conditions.len() <= MAX_SCAN_CONDITIONS).then_some(Self { conditions })
    }

    /// Returns the conjuncts in declaration order.
    #[must_use]
    pub fn conditions(&self) -> &[ScanCondition] {
        &self.conditions
    }
}

/// The outcome of one index-descriptor registration.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum DescriptorRegistration {
    /// The descriptor was created and every existing row already carries its
    /// extracted values (inline backfill complete or nothing to backfill):
    /// conditions over the field push immediately.
    Active,
    /// The descriptor was created but pre-existing rows still lack their
    /// extracted values; the governed backfill pass (D-31) completes it.
    /// Conditions over the field do not push until then — a scan over
    /// half-extracted columns would silently miss rows.
    Building,
    /// An identical descriptor was already on record.
    Unchanged,
}

/// Failures registering one extracted index-column descriptor.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum IndexDescriptorError {
    /// The database's extracted-column budget is exhausted.
    ColumnBudgetExceeded,
    /// Durable registration failed.
    Storage,
}

/// One durable index descriptor as the audit and the collections route see
/// it: its identity, its activity, and the rows its governed backfill still
/// owes.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct DescriptorListing {
    /// The descriptor's collection.
    pub collection: String,
    /// The descriptor's field path.
    pub field: String,
    /// Whether conditions over the field push.
    pub active: bool,
    /// The commit-sequence span the governed backfill has not covered yet
    /// (registration watermark minus durable cursor).
    pub backfill_remaining: u64,
}

/// One retained revision of a document: the history view of the folded
/// journal (D-42 — superseded rows stay reachable until the pruner passes
/// every consumer frontier and the 30-day floor).
///
/// D-36/D-48: every entry carries the conflict-viewer facts — the writing
/// dot (`commit_ulid`), the hybrid stamp, and the deterministic policy plus
/// its explanation — so the dashboard's `db.history(id)` can state why one
/// version won without a second lookup.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct DocumentRevisionEntry {
    /// The revision string (`seq-version` for live rows,
    /// `conflict-<commit_ulid>` for a recorded merge loser).
    pub revision: String,
    /// Whether this revision is the tombstone.
    pub deleted: bool,
    /// The writing dot: the row's `commit_ulid` (the source commit for a
    /// replicated row, the local operation for an origin row, the loser's
    /// commit for a conflict entry).
    pub commit_ulid: String,
    /// The hybrid logical stamp the deterministic merge compares.
    pub hlc: u64,
    /// The writing region's identity (empty for a local row in-process).
    pub origin: String,
    /// The deterministic policy that produced this revision
    /// ([`MergePolicy::as_str`]).
    pub policy: String,
    /// The deterministic, human-readable explanation of the policy
    /// outcome (field-level for merges, stable across regions).
    pub explanation: String,
}

/// One typed merge field kind a collection may declare (D-36, ADR 0022).
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub enum MergeFieldKind {
    /// A monotonic integer counter converging to the highest observed
    /// value (the idempotent full-state form; per-actor PN components
    /// follow when per-field dots land).
    PnCounter,
    /// An array set converging by union: a concurrent add on either side
    /// survives.
    AddWinsSet,
    /// An array set converging by intersection: a concurrent remove on
    /// either side wins.
    RemoveWinsSet,
    /// A register keeping every distinct concurrent value as a sorted
    /// array.
    MvRegister,
}

impl MergeFieldKind {
    /// Returns the stable schema-text name of this kind.
    #[must_use]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::PnCounter => "pn-counter",
            Self::AddWinsSet => "add-wins-set",
            Self::RemoveWinsSet => "remove-wins-set",
            Self::MvRegister => "mv-register",
        }
    }

    /// Parses one schema-text kind name.
    #[must_use]
    pub fn try_from_str(value: &str) -> Option<Self> {
        match value {
            "pn-counter" => Some(Self::PnCounter),
            "add-wins-set" => Some(Self::AddWinsSet),
            "remove-wins-set" => Some(Self::RemoveWinsSet),
            "mv-register" => Some(Self::MvRegister),
            _ => None,
        }
    }
}

/// Why one merge-schema declaration was refused.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum MergeSchemaError {
    /// The schema names no fields.
    Empty,
    /// One field name is invalid (empty, over 255 bytes, control
    /// characters, or leading/trailing whitespace).
    InvalidField,
    /// One field kind name is unknown.
    UnknownKind,
    /// The schema names more than [`MAX_MERGE_SCHEMA_FIELDS`] fields.
    TooManyFields,
    /// Durable registration failed.
    Storage,
}

/// The maximum number of typed fields one merge schema may declare.
pub const MAX_MERGE_SCHEMA_FIELDS: usize = 64;

/// One collection's declared merge schema (D-36): the typed CRDT fields
/// whose merges use per-field kernels. Collections without a schema merge
/// with the remove-wins map default plus the HLC tiebreak and store no
/// per-field kernels.
#[derive(Clone, Debug, Default, Eq, PartialEq)]
pub struct MergeSchema {
    fields: std::collections::BTreeMap<String, MergeFieldKind>,
}

impl MergeSchema {
    /// Creates one schema from its field map, validating every name.
    ///
    /// # Errors
    ///
    /// Returns [`MergeSchemaError`] when the map is empty, oversized, or
    /// names an invalid field.
    pub fn try_new(
        fields: std::collections::BTreeMap<String, MergeFieldKind>,
    ) -> Result<Self, MergeSchemaError> {
        if fields.is_empty() {
            return Err(MergeSchemaError::Empty);
        }
        if fields.len() > MAX_MERGE_SCHEMA_FIELDS {
            return Err(MergeSchemaError::TooManyFields);
        }
        for field in fields.keys() {
            if !is_valid_merge_field(field) {
                return Err(MergeSchemaError::InvalidField);
            }
        }
        Ok(Self { fields })
    }

    /// Returns one field's declared kind, if any.
    #[must_use]
    pub fn get(&self, field: &str) -> Option<MergeFieldKind> {
        self.fields.get(field).copied()
    }

    /// Returns the declared fields in canonical order.
    #[must_use]
    pub fn fields(&self) -> &std::collections::BTreeMap<String, MergeFieldKind> {
        &self.fields
    }

    /// Reports whether the schema declares no fields.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.fields.is_empty()
    }

    /// Renders the canonical schema text (`field=kind;…`, sorted).
    #[must_use]
    pub fn to_canonical_string(&self) -> String {
        self.fields
            .iter()
            .map(|(field, kind)| format!("{field}={}", kind.as_str()))
            .collect::<Vec<_>>()
            .join(";")
    }

    /// Parses the canonical schema text.
    ///
    /// # Errors
    ///
    /// Returns [`MergeSchemaError`] when the text is empty, oversized,
    /// names an invalid field, or names an unknown kind.
    pub fn parse_canonical(text: &str) -> Result<Self, MergeSchemaError> {
        if text.is_empty() {
            return Err(MergeSchemaError::Empty);
        }
        let mut fields = std::collections::BTreeMap::new();
        for part in text.split(';') {
            let (field, kind) = part.split_once('=').ok_or(MergeSchemaError::UnknownKind)?;
            if !is_valid_merge_field(field) {
                return Err(MergeSchemaError::InvalidField);
            }
            let kind = MergeFieldKind::try_from_str(kind).ok_or(MergeSchemaError::UnknownKind)?;
            fields.insert(field.to_owned(), kind);
        }
        Self::try_new(fields)
    }
}

/// Validates one merge-schema field name: non-empty, at most 255 bytes,
/// no control characters, no leading or trailing whitespace.
fn is_valid_merge_field(value: &str) -> bool {
    if value.is_empty() || value.len() > 255 {
        return false;
    }
    if value.chars().any(char::is_control) {
        return false;
    }
    value == value.trim()
}

/// The deterministic policy that produced one history revision (D-36/D-48).
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub enum MergePolicy {
    /// A local origin write (no merge).
    Origin,
    /// A replicated apply with no prior row.
    CleanApply,
    /// Concurrent values ordered by the `(hlc, origin)` total order.
    HlcTiebreak,
    /// A concurrent removal beat a concurrent assignment.
    RemoveWins,
    /// A declared PN-counter field converged to the highest observed value.
    PnCounterMax,
    /// A declared add-wins set field converged by union.
    AddWinsUnion,
    /// A declared remove-wins set field converged by intersection.
    RemoveWinsIntersection,
    /// A declared MV-register field kept every distinct concurrent value.
    MvUnion,
    /// A field-level merge combining more than one per-field policy.
    Mixed,
}

impl MergePolicy {
    /// Returns the stable history-viewer name of this policy.
    #[must_use]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Origin => "origin",
            Self::CleanApply => "clean-apply",
            Self::HlcTiebreak => "hlc-tiebreak",
            Self::RemoveWins => "remove-wins",
            Self::PnCounterMax => "pn-counter-max",
            Self::AddWinsUnion => "add-wins-union",
            Self::RemoveWinsIntersection => "remove-wins-intersection",
            Self::MvUnion => "mv-union",
            Self::Mixed => "mixed",
        }
    }
}

/// One deterministic conflict record (D-36): the loser a merge superseded,
/// the winner it kept, and the policy plus its explanation. Both regions
/// reaching the same merge compute the same record, so history explanations
/// are stable across regions.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct MergeConflictEntry {
    /// The loser's writing dot.
    pub loser_commit_ulid: String,
    /// The loser's hybrid stamp.
    pub loser_hlc: u64,
    /// The loser's writing region.
    pub loser_origin: String,
    /// Whether the loser was a tombstone.
    pub loser_deleted: bool,
    /// The winner's writing dot.
    pub winner_commit_ulid: String,
    /// The winner's hybrid stamp.
    pub winner_hlc: u64,
    /// The winner's writing region.
    pub winner_origin: String,
    /// The deterministic policy ([`MergePolicy::as_str`]).
    pub policy: String,
    /// The deterministic explanation of the outcome.
    pub explanation: String,
}

/// One per-field causal kernel of a declared collection (D-36): the newest
/// stamp that contributed to the field. Only collections with a declared
/// merge schema store kernels; schema-free collections store none.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct MergeKernelEntry {
    /// The field the kernel covers.
    pub field: String,
    /// The newest contributing hybrid stamp.
    pub hlc: u64,
    /// The newest contributing region.
    pub origin: String,
}

/// One document's current merge input: the live body (if any) plus the
/// provenance the deterministic merge compares.
#[derive(Clone, Debug, PartialEq)]
pub struct DocumentMergeState {
    /// The live body's value, or `None` for a tombstone.
    pub body: Option<crate::types::DocumentValue>,
    /// Whether the current row is a tombstone.
    pub deleted: bool,
    /// The current row's hybrid stamp.
    pub hlc: u64,
    /// The current row's writing region.
    pub origin: String,
    /// The current row's writing dot.
    pub commit_ulid: String,
}

/// One live key whose TTL expiry has passed (S-02 slice D): the sweep's
/// unit of work. The key is tenant-scoped; the sweep deletes it through
/// the ordinary origin-tombstone path, so expiry replicates and merges
/// like any other delete.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ExpiredTtlKey {
    /// The owning tenant.
    pub tenant: TenantId,
    /// The collection of the expired key.
    pub collection: CollectionName,
    /// The expired document's identifier.
    pub id: DocumentId,
}

/// The durable progress of one governed backfill batch.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct BackfillProgress {
    /// The descriptor's collection.
    pub collection: String,
    /// The descriptor's field path.
    pub field: String,
    /// The rows still owed after this batch.
    pub remaining: u64,
    /// Whether the descriptor completed with this batch.
    pub active: bool,
}

/// The total order used by usage export and its continuation cursor.
#[derive(Clone, Debug, Eq, Ord, PartialEq, PartialOrd)]
pub(crate) struct UsageFactPosition {
    pub(crate) sequence: u64,
    pub(crate) rule: MintingRule,
    pub(crate) id: UsageFactId,
}

impl UsageFactPosition {
    pub(crate) fn of(fact: &DurableUsageFact) -> Self {
        Self {
            sequence: fact.sequence(),
            rule: fact.fact().identity().rule(),
            id: fact.fact().identity().id().clone(),
        }
    }
}

/// The private storage adapter seam the Engine depends on.
///
/// The production implementation is [`SqliteStorage`]. The trait exists so the
/// storage seam stays substitutable behind one typed contract instead of
/// leaking file, codec, or page details into the Engine.
pub(crate) trait StoragePort: Send {
    /// Runs the publication order for one validated commit intent.
    fn commit(&mut self, intent: CommitIntent) -> CommitResult;

    /// Runs the publication order for a batch of validated commit intents
    /// under one shared durable append where the implementation supports
    /// it (group commit). The default commits each intent alone, which is
    /// exactly the serial behavior.
    fn commit_batch(&mut self, intents: Vec<CommitIntent>, defer_flips: bool) -> Vec<CommitResult> {
        let _ = defer_flips;
        intents.into_iter().map(|intent| self.commit(intent)).collect()
    }

    /// Durable-side flush of any records a deferred batch left unsynced.
    /// The default has nothing to flush.
    fn flush_pending_flips(&mut self) {}

    /// Returns the durable resolution of one operation identity, if recorded.
    fn resolve_operation(
        &self,
        tenant: &TenantId,
        operation: &OperationId,
    ) -> Option<OperationResolution>;

    /// Appends one standalone authoritative usage fact durably and
    /// idempotently. The default rejects legacy backends.
    fn record_usage(
        &mut self,
        _tenant: &TenantId,
        _fact: UsageFact,
    ) -> Result<(DurableUsageFact, bool), UsageStorageError> {
        Err(UsageStorageError::UnsupportedBackend)
    }

    /// Returns at most `limit` tenant facts at or below the fixed snapshot,
    /// ordered strictly after the optional continuation position. The Engine
    /// asks for one extra row to decide whether another page exists.
    fn usage_facts_page(
        &self,
        _tenant: &TenantId,
        _snapshot: StorageSequence,
        _after: Option<&UsageFactPosition>,
        _window: Option<MeasurementWindow>,
        _limit: u32,
    ) -> Result<Vec<DurableUsageFact>, UsageStorageError> {
        Err(UsageStorageError::UnsupportedBackend)
    }

    /// Returns one document as the given read snapshot observed it.
    fn read_document_at(
        &self,
        sequence: StorageSequence,
        tenant: &TenantId,
        id: &DocumentId,
    ) -> Option<Document>;

    /// Returns one document as the given read snapshot observed it, resolved
    /// by the full storage key: tenant, collection, and identifier.
    ///
    /// Documents are keyed by all three parts and nothing forbids reusing one
    /// identifier across a tenant's collections, so a read that names a
    /// collection must resolve by the exact key. An identifier-only lookup
    /// answers whichever copy canonical key order happens to put first, which
    /// is a different document, not an absent one.
    fn read_document_in_collection_at(
        &self,
        sequence: StorageSequence,
        tenant: &TenantId,
        collection: &CollectionName,
        id: &DocumentId,
    ) -> Option<Document>;

    /// Returns one document's current replication stamp: the newest
    /// row's `hlc`, `origin`, and tombstone flag — the deterministic
    /// merge's comparison input. `None` when the key has no row.
    fn document_provenance_at(
        &self,
        _tenant: &TenantId,
        _collection: &CollectionName,
        _id: &DocumentId,
    ) -> Option<(u64, String, bool)> {
        None
    }

    /// Returns one document's retained revision history, newest first:
    /// one row per committed mutation (put or tombstone) that retention
    /// has not pruned yet (D-42: history is journal retention). The
    /// default serves no history; the `SQLite` storage overrides it.
    fn document_history_in_collection(
        &self,
        _tenant: &TenantId,
        _collection: &CollectionName,
        _id: &DocumentId,
        _limit: u32,
    ) -> Vec<DocumentRevisionEntry> {
        Vec::new()
    }

    /// Returns one document's public revision string (`seq-version`) at
    /// the given read snapshot without reading its body — the
    /// `If-None-Match` answer (`S-03`: a 304 must not decode the body
    /// blob). `None` when no live version of the key exists at the
    /// snapshot.
    fn document_revision_in_collection_at(
        &self,
        _sequence: StorageSequence,
        _tenant: &TenantId,
        _collection: &CollectionName,
        _id: &DocumentId,
    ) -> Option<String> {
        None
    }

    /// Returns every document of one tenant as the given read snapshot
    /// observed them, in canonical key order.
    fn documents_at(&self, sequence: StorageSequence, tenant: &TenantId) -> Vec<Document>;

    /// Returns every document of one tenant's collection as the given read
    /// snapshot observed them, in canonical key order.
    ///
    /// A query whose source is one collection needs no other collection's
    /// documents, and this read stops at the collection's key range instead
    /// of materializing the rest of the tenant. Canonical order is the
    /// order of [`StoragePort::documents_at`] restricted to the collection.
    #[allow(dead_code, reason = "compatibility collection-wide read seam")]
    fn documents_in_collection_at(
        &self,
        sequence: StorageSequence,
        tenant: &TenantId,
        collection: &CollectionName,
    ) -> Vec<Document>;

    /// Returns one bounded window of a collection at a fixed snapshot,
    /// resuming strictly after the optional identifier cursor.
    ///
    /// The result is ordered by the collection's canonical identifier order
    /// and never contains documents from another collection.  Implementations
    /// must bound the returned page by `limit`; callers enforce the public
    /// page ceiling before reaching this seam.
    fn documents_in_collection_at_from(
        &self,
        sequence: StorageSequence,
        tenant: &TenantId,
        collection: &CollectionName,
        after: Option<&DocumentId>,
        limit: u32,
    ) -> Result<Vec<Document>, SnapshotReadError>;

    /// Returns one bounded window of a collection at a fixed snapshot with
    /// the filter pushed onto the extracted index columns (D-42): the same
    /// page shape as [`StoragePort::documents_in_collection_at_from`] with
    /// the condition conjunction applied in SQL over the newest version of
    /// each key, so non-matching rows are never decoded or shipped.
    ///
    /// The pushed filter is a sound narrowing, never an exact one: a backend
    /// may drop conditions it cannot express exactly (fields without an
    /// active descriptor, value kinds the columns do not carry), and the
    /// caller still applies its full predicate over the returned page.
    /// `None` reports that this backend cannot push the filter at all and
    /// the caller falls back to [`StoragePort::documents_in_collection_at_from`].
    fn collection_scan_filtered(
        &self,
        _sequence: StorageSequence,
        _tenant: &TenantId,
        _collection: &CollectionName,
        _after: Option<&DocumentId>,
        _limit: u32,
        _filter: &ScanFilter,
    ) -> Option<Result<Vec<Document>, SnapshotReadError>> {
        None
    }

    /// Registers one extracted index column descriptor (D-31/D-42): the
    /// named field of the named collection gains stored columns written by
    /// the Rust write path from the next commit on, and existing rows are
    /// backfilled inline while the collection is inside the cost cap
    /// (registering marks the descriptor active only when every existing
    /// row carries its extracted values; a larger backfill is the governed
    /// background pass's work and the descriptor stays building, its
    /// conditions unpushed, until that pass completes).
    ///
    /// Registration is idempotent: an identical descriptor already on record
    /// is a no-op.
    ///
    /// # Errors
    ///
    /// Returns [`IndexDescriptorError::ColumnBudgetExceeded`] past the
    /// database's extracted-column budget, and
    /// [`IndexDescriptorError::Storage`] when durable registration fails.
    fn register_index_descriptor(
        &mut self,
        _collection: &CollectionName,
        _field: &str,
    ) -> Result<DescriptorRegistration, IndexDescriptorError> {
        Err(IndexDescriptorError::Storage)
    }

    /// Lists the durable index descriptors with their activity and
    /// remaining governed-backfill work. The default reports none.
    fn index_descriptors(&self) -> Result<Vec<DescriptorListing>, ()> {
        Err(())
    }

    /// Reports one field's durable descriptor state without registering
    /// anything: `Some(true)` is an active descriptor whose conditions push,
    /// `Some(false)` is a building descriptor whose backfill still owes rows,
    /// and `None` is no descriptor (or a backend that does not report).
    ///
    /// The read is lock-light (in-memory state, no transaction): the query
    /// path calls it per pushable field to decide between pushing,
    /// registering, and refusing with `IndexNotReady`.
    fn index_descriptor_state(&self, _collection: &CollectionName, _field: &str) -> Option<bool> {
        None
    }

    /// Advances every building descriptor's governed backfill by one
    /// bounded batch of rows (D-31), completing a descriptor when its
    /// cursor reaches its registration watermark. The default owes nothing.
    ///
    /// # Errors
    ///
    /// Returns `Err(())` when the durable pass fails.
    fn pump_index_backfills(&mut self, _batch: u64) -> Result<Vec<BackfillProgress>, ()> {
        Ok(Vec::new())
    }

    /// Registers one full-text descriptor (S-02 slice D, D-39): the named
    /// field of the named collection gains FTS5 rows written by the Rust
    /// write path from the next commit on, backfilled inline inside the
    /// cost cap and governed beyond it — the same lifecycle as
    /// [`StoragePort::register_index_descriptor`], over its own table, so
    /// a field may carry both a value and a text descriptor.
    ///
    /// Registration is idempotent: an identical descriptor already on
    /// record is a no-op.
    ///
    /// # Errors
    ///
    /// Returns [`IndexDescriptorError::Storage`] when durable registration
    /// fails. There is no column budget for text: the FTS5 table is the
    /// index.
    fn register_fts_descriptor(
        &mut self,
        _collection: &CollectionName,
        _field: &str,
    ) -> Result<DescriptorRegistration, IndexDescriptorError> {
        Err(IndexDescriptorError::Storage)
    }

    /// Reports one field's text-descriptor state without registering
    /// anything: `Some(true)` narrows, `Some(false)` is building, and
    /// `None` is no descriptor (or a backend that does not report).
    fn fts_descriptor_state(&self, _collection: &CollectionName, _field: &str) -> Option<bool> {
        None
    }

    /// Returns the FTS5 candidate set of one search — the matching
    /// document ids with relevance scores, higher first — or `None` when
    /// this backend cannot narrow the read and the caller must take the
    /// exact scan. The candidates are a sound narrowing, never an exact
    /// filter: the caller still applies its full predicate.
    fn fts_match(
        &self,
        _sequence: StorageSequence,
        _tenant: &TenantId,
        _collection: &CollectionName,
        _field: &str,
        _query: &str,
        _match_any: bool,
    ) -> Option<Result<Vec<(String, f64)>, SnapshotReadError>> {
        None
    }

    /// Returns the live keys whose TTL expiry has passed, bounded by
    /// `batch`: the sweep's work list (S-02 slice D). The default owes no
    /// keys.
    ///
    /// # Errors
    ///
    /// Returns `Err(())` when durable storage failed.
    fn expired_ttl_keys(&self, _now_millis: i64, _batch: u32) -> Result<Vec<ExpiredTtlKey>, ()> {
        Ok(Vec::new())
    }

    /// Returns the documents the given read snapshot observed at the named
    /// exact keys of one tenant's collection, in canonical key order of the
    /// requested identifiers.
    ///
    /// This is the batched form of [`StoragePort::read_document_in_collection_at`]:
    /// each named key resolves by its exact `(tenant, collection, identifier)`
    /// triple, a key absent at the snapshot (never written, or deleted at or
    /// below it) contributes nothing, and one identifier is never resolved in
    /// another collection. The result is the keyed-candidate fetch of the
    /// index-narrowed query path: a bounded identifier set in, the surviving
    /// documents out, in the same canonical order a scan of the collection
    /// would have produced.
    fn read_documents_in_collection_at(
        &self,
        sequence: StorageSequence,
        tenant: &TenantId,
        collection: &CollectionName,
        ids: &[DocumentId],
    ) -> Vec<Document>;

    /// Returns one bounded window of a tenant's documents as the given read
    /// snapshot observed them, in canonical key order resuming at the
    /// boundary named by `after` (W4-CA-7).
    fn documents_at_from(
        &self,
        sequence: StorageSequence,
        tenant: &TenantId,
        after: Option<&DocumentId>,
        limit: u32,
    ) -> Vec<Document>;

    /// Returns one bounded window of a tenant's documents in canonical full
    /// key order, resuming strictly after `(collection, identifier)`.
    /// Unlike the identifier-only compatibility seam above, this cursor is
    /// lossless when an identifier is reused across collections.
    fn documents_at_from_key(
        &self,
        sequence: StorageSequence,
        tenant: &TenantId,
        after: Option<(&CollectionName, &DocumentId)>,
        limit: u32,
    ) -> Result<Vec<Document>, SnapshotReadError>;

    /// Reports whether the immutable state needed for a historical boundary
    /// is still retained. A false result must fail closed instead of letting
    /// a backend fall back to its current published state.
    fn snapshot_boundary_available(&self, sequence: StorageSequence) -> bool;

    /// Resolves the durability of each requested final-state key without
    /// cloning complete publication write sets. `None` means at least one
    /// winner is no longer provable from retained history.
    fn winning_durabilities_at(
        &self,
        tenant: &TenantId,
        sequence: StorageSequence,
        keys: &[(&str, &str)],
    ) -> Option<Vec<LocalDurability>>;

    /// Returns one tenant's published commits after `after`, in global
    /// storage-sequence order, up to `limit` entries (W4-CA-4).
    fn tenant_commits(
        &self,
        tenant: &TenantId,
        after: StorageSequence,
        limit: u32,
    ) -> Vec<PublishedCommit>;

    /// Returns every tenant that has at least one recorded published commit,
    /// in canonical tenant order (W4-CA-4).
    fn committed_tenants(&self) -> Vec<TenantId>;

    /// Returns the tenant's latest recorded sequence at or below `at`
    /// (W4-CA-4): the mapping entry that turns one global
    /// storage-sequence cursor into this tenant's own progress.
    fn tenant_sequence(&self, tenant: &TenantId, at: StorageSequence) -> Option<StorageSequence>;

    /// Returns the oldest sequence the durable publication history can
    /// serve, or `None` when no history is servable (W4-CA-4).
    ///
    /// A cursor older than this bound cannot be served incrementally: the
    /// caller must fall back to a full catch-up or backup instead of
    /// silently skipping the gap.
    fn servable_history_start(&self) -> Option<StorageSequence>;

    /// Returns the last global storage sequence durably recorded in the
    /// publication-history journal (W4-CA-4 repair F2).
    ///
    /// Sequences have legitimate holes, so a consumer cannot detect a
    /// skipped journal append by discontinuity; this durable high-water
    /// mark is the real signal. `journaled_high_water() <
    /// published_state().0` means at least one published commit is not
    /// servable from the journal, and the consumer must fall back instead
    /// of assuming completeness.
    fn journaled_high_water(&self) -> Option<StorageSequence>;

    /// Returns the currently published sequence and root identity.
    fn published_state(&self) -> (StorageSequence, StorageRootId);

    /// Declares one collection's typed merge schema (D-36): the fields
    /// whose merges use per-field kernels. The default stores no schema.
    fn declare_merge_schema(
        &mut self,
        _collection: &CollectionName,
        _schema: &MergeSchema,
    ) -> Result<(), MergeSchemaError> {
        Err(MergeSchemaError::Storage)
    }

    /// Returns one collection's declared merge schema, if any.
    fn merge_schema(&self, _collection: &CollectionName) -> Option<MergeSchema> {
        None
    }

    /// Returns one document's current merge input: the live body (if any)
    /// plus the provenance and dot the deterministic merge compares.
    /// `None` when the key has no row at all.
    fn document_merge_state(
        &self,
        _tenant: &TenantId,
        _collection: &CollectionName,
        _id: &DocumentId,
    ) -> Option<DocumentMergeState> {
        None
    }

    /// Records one deterministic conflict loser (D-36): the side a merge
    /// superseded, with the winner, policy, and explanation. The default
    /// keeps no conflicts.
    fn record_merge_conflict(
        &mut self,
        _tenant: &TenantId,
        _collection: &CollectionName,
        _id: &DocumentId,
        _conflict: &MergeConflictEntry,
    ) -> Result<(), ()> {
        Ok(())
    }

    /// Returns one document's recorded conflict losers, newest first,
    /// bounded by `limit`. The default serves none.
    fn merge_conflicts_for(
        &self,
        _tenant: &TenantId,
        _collection: &CollectionName,
        _id: &DocumentId,
        _limit: u32,
    ) -> Vec<MergeConflictEntry> {
        Vec::new()
    }

    /// Stores one per-field causal kernel of a declared collection
    /// (D-36). Schema-free collections never call this seam.
    fn upsert_merge_kernel(
        &mut self,
        _tenant: &TenantId,
        _collection: &CollectionName,
        _id: &DocumentId,
        _field: &str,
        _hlc: u64,
        _origin: &str,
    ) -> Result<(), ()> {
        Ok(())
    }

    /// Returns one document's stored per-field kernels in field order.
    /// Schema-free collections serve none.
    fn merge_kernels_for(
        &self,
        _tenant: &TenantId,
        _collection: &CollectionName,
        _id: &DocumentId,
    ) -> Vec<MergeKernelEntry> {
        Vec::new()
    }

    /// Runs one retention sweep: prune publication history below every
    /// consumer frontier, then checkpoint the WAL.
    fn run_retention_sweep(&mut self) -> Result<(), ()>;

    /// Flushes appended durable bytes on shutdown.
    fn flush(&mut self) -> Result<(), ()>;

    /// Arms one test fault inside the publication order. Test-only.
    #[cfg(test)]
    fn arm_test_fault(&mut self, point: FaultPoint);
}

/// Whether one journal row was written by this region or replicated in
/// from a peer (D-35): the cross-region tailer ships `Origin` rows only,
/// so replicated rows never re-ship and the tail cannot loop.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum JournalKind {
    /// Written by this region's own writer.
    Origin,
    /// Applied from a peer region's journal by the replication tailer.
    Replicated,
}

/// One journal row's replication provenance (D-35/D-36): where the write
/// came from and the causal stamp it carries. An origin row reports
/// `Origin`, an empty origin, and `hlc = 0` — this slice lands the
/// cross-region apply path; the HLC clock itself is the QUIC lane's work.
///
/// The type is crate-public in spirit; it is `pub` only so
/// [`SyncJournalRecord`](crate::streaming::SyncJournalRecord) can expose it
/// to the in-process tailer seam beside the journal types.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct JournalProvenance {
    kind: JournalKind,
    origin: String,
    hlc: u64,
}

impl JournalProvenance {
    /// Assembles one provenance from its facts. Crate-only: provenance is
    /// recorded by the storage write path, never minted by a consumer.
    pub(crate) fn new(kind: JournalKind, origin: String, hlc: u64) -> Self {
        Self { kind, origin, hlc }
    }

    /// Returns whether the row was written by this region.
    #[must_use]
    pub const fn kind(&self) -> JournalKind {
        self.kind
    }

    /// Returns the writing region's identity (empty for a local row).
    #[must_use]
    pub fn origin(&self) -> &str {
        &self.origin
    }

    /// Returns the writing region's hybrid logical clock stamp.
    #[must_use]
    pub const fn hlc(&self) -> u64 {
        self.hlc
    }
}

/// One published commit reconstructed from the durable publication history
/// (W4-CA-4).
///
/// The commit carries the logical write set itself — full documents for puts
/// and identifier-plus-version for deletes — never a digest: a digest is not
/// invertible, and a follower must be able to replay both puts and deletes
/// idempotently by operation identity. Each entry is annotated with the
/// commit's achieved [`LocalDurability`] (repair F4): a `Buffered` or
/// `Written` entry may describe data the source lost after a restart, and a
/// consumer must reconcile that disclosure rather than assume the entry's
/// bytes are recoverable.
#[derive(Clone, Debug, PartialEq)]
pub(crate) struct PublishedCommit {
    tenant: TenantId,
    operation_id: OperationId,
    sequence: StorageSequence,
    durability: LocalDurability,
    provenance: JournalProvenance,
    writes: Vec<PublishedWrite>,
}

impl PublishedCommit {
    /// Creates one commit record from the published facts.
    pub(crate) fn new(
        tenant: TenantId,
        operation_id: OperationId,
        sequence: StorageSequence,
        durability: LocalDurability,
        provenance: JournalProvenance,
        writes: Vec<PublishedWrite>,
    ) -> Self {
        Self { tenant, operation_id, sequence, durability, provenance, writes }
    }

    /// Returns the tenant that owns the commit.
    pub(crate) fn tenant(&self) -> &TenantId {
        &self.tenant
    }

    /// Returns the stable operation identity of the commit.
    pub(crate) fn operation_id(&self) -> &OperationId {
        &self.operation_id
    }

    /// Returns the global storage sequence of the publication.
    pub(crate) fn sequence(&self) -> StorageSequence {
        self.sequence
    }

    /// Returns the local durability the commit had achieved when the entry
    /// was journaled (repair F4).
    pub(crate) fn durability(&self) -> LocalDurability {
        self.durability
    }

    /// Returns the commit's replication provenance (D-35).
    pub(crate) fn provenance(&self) -> &JournalProvenance {
        &self.provenance
    }

    /// Returns the reconstructable write set of the commit.
    pub(crate) fn writes(&self) -> &[PublishedWrite] {
        &self.writes
    }

    /// Appends one write to the reconstructable write set.
    pub(crate) fn writes_mut(&mut self) -> &mut Vec<PublishedWrite> {
        &mut self.writes
    }
}

/// One reconstructable write of a published commit (W4-CA-4).
///
/// A put carries the published document — the form the commit assigned,
/// version included. A delete carries the exact key it removed — the
/// collection, the document identifier, and the version that existed at that
/// key (`0` when nothing existed there) — because a snapshot read cannot
/// distinguish a deleted identifier from one that never existed, and an
/// identifier alone is ambiguous when one tenant reuses it across
/// collections. The `None` collection case is exactly the provable no-op
/// delete: nothing existed at any key for that identifier, so
/// `version_at_delete` is `0` and a replay removes nothing.
#[derive(Clone, Debug, PartialEq)]
pub(crate) enum PublishedWrite {
    /// Insert or replace one document in its published form.
    Put {
        /// The document as the publication stored it, version included.
        document: Document,
    },
    /// Delete one document by its exact key.
    Delete {
        /// The collection of the deleted key, or `None` exactly when the
        /// identifier was absent from every collection of the tenant.
        collection: Option<CollectionName>,
        /// The deleted document's identifier.
        id: DocumentId,
        /// The version that existed at the deleted key; `0` when nothing
        /// existed there.
        version_at_delete: u64,
    },
}

/// An immutable event emitted after storage locks release for one published
/// commit.
///
/// The event carries the committed root and its storage sequence. Index
/// publication is a separate concern that may lag; it never blocks storage
/// lock release.
#[derive(Clone, Debug, Eq, PartialEq)]
pub(crate) struct CommitPublicationEvent {
    operation: OperationId,
    root: StorageRootId,
    sequence: StorageSequence,
}

impl CommitPublicationEvent {
    /// Creates one event from the published facts of a commit.
    pub(crate) fn new(
        operation: OperationId,
        root: StorageRootId,
        sequence: StorageSequence,
    ) -> Self {
        Self { operation, root, sequence }
    }

    // Read accessors are consumed by the W3-2 index-publication seam; W3-1
    // proves the emission point and ordering only.
    /// Returns the published operation identity.
    #[allow(dead_code)]
    pub(crate) fn operation_id(&self) -> &OperationId {
        &self.operation
    }

    /// Returns the immutable root that became visible.
    #[allow(dead_code)]
    pub(crate) fn root(&self) -> &StorageRootId {
        &self.root
    }

    /// Returns the storage sequence of the publication.
    #[allow(dead_code)]
    pub(crate) fn sequence(&self) -> StorageSequence {
        self.sequence
    }
}

/// Computes the logical digest binding an operation identity to its write set.
pub(crate) use codec::write_set_digest;

/// Reports whether one document is inside the codec's structural limits.
pub(crate) use codec::document_within_limits;
