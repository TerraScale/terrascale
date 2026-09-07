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
mod invisible;
mod ownership;
pub(crate) mod participant;
mod physical_file;
mod shadow;
mod sqlite;
pub use physical_file::{
    EncryptedStorage, PhysicalFile, create_physical_file, open_encrypted_sqlite,
    open_physical_file, open_physical_sqlite,
};

pub use invisible::{InvisibleColumnKind, InvisibleIndexColumn, InvisibleIndexProgress};
pub use shadow::{
    MAX_SHADOW_ROWS, MAX_SHADOW_VM_STEPS, ShadowReadComparison, ShadowReadError,
    ShadowReadMeasurement, ShadowReadRequest,
};

#[cfg(test)]
pub(crate) use faults::FaultPoint;
pub(crate) use intent::{
    CommitIntent, CommitRejection, CommitResult, OperationResolution, StagedWrite,
    SupersededReplication, UnknownCause,
};
pub(crate) use ownership::validate_local_restore_source;
pub use ownership::{DatabaseOwner, EnvironmentName, OwnershipError};
pub(crate) use ownership::{OwnershipMode, load_existing, load_or_create};
pub(crate) use sqlite::branch_import::{
    copy_encrypted_snapshot, rebind_owner, transform_branch_schema,
};
pub(crate) use sqlite::branch_provenance::transform_provenance;
pub use sqlite::database_file;
pub(crate) use sqlite::import_backup_file;
pub(crate) use sqlite::{SqliteStorage, decode_document_body};

/// A retained journal page could not be served.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum JournalReadError {
    StorageFailure,
    HistoryExpired,
}

impl From<()> for JournalReadError {
    fn from((): ()) -> Self {
        Self::StorageFailure
    }
}

/// Failures opening one durable location.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum StorageOpenError {
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
/// semantics are SQL's: strings compare by code point, numbers compare
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

/// A descriptor could not be removed without violating durable index state.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum IndexDescriptorRemovalError {
    /// The requested field is not a valid extracted descriptor name.
    InvalidField,
    /// A hidden index build still depends on this descriptor.
    InUse,
    /// The durable operation failed; no partial deletion is committed.
    Storage,
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
    /// The current row's durable per-origin journal sequence.
    pub origin_sequence: u64,
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

/// One per-database metering fact (E-9, D-16/D-42): the billable rollup of
/// one database's folded journal rows. `db` is the tenant identity text
/// (D-51: tenant = database); `seq_hi` is the highest journal sequence the
/// rollup covers; `writes` counts billed write units under the D-16
/// `ceil(bytes / 4 KiB)` rule; `bytes` sums the stored canonical body
/// bytes (tombstones contribute zero).
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct DbUsageFact {
    db: String,
    seq_hi: u64,
    writes: u64,
    bytes: u64,
}

impl DbUsageFact {
    /// Creates one per-database metering fact.
    #[must_use]
    pub fn new(db: String, seq_hi: u64, writes: u64, bytes: u64) -> Self {
        Self { db, seq_hi, writes, bytes }
    }

    /// Returns the billed database identity.
    #[must_use]
    pub fn db(&self) -> &str {
        &self.db
    }

    /// Returns the highest journal sequence the rollup covers.
    #[must_use]
    pub const fn seq_hi(&self) -> u64 {
        self.seq_hi
    }

    /// Returns the billed write units.
    #[must_use]
    pub const fn writes(&self) -> u64 {
        self.writes
    }

    /// Returns the stored canonical body bytes.
    #[must_use]
    pub const fn bytes(&self) -> u64 {
        self.bytes
    }
}

/// The D-16 write unit for one document operation over `body_len` stored
/// canonical bytes: `ceil(body_len / 4 KiB)` with a one-unit floor, so a
/// tombstone or empty body still bills the single delete it performed.
#[must_use]
pub fn billed_writes_for_bytes(body_len: u64) -> u64 {
    body_len.div_ceil(4096).max(1)
}

/// One origin's applied causal progress for a tenant.
#[derive(Clone, Debug, Eq, PartialEq)]
pub(crate) struct AppliedFrontierEntry {
    /// The logical origin that produced the rows.
    pub(crate) origin: String,
    /// The greatest contiguous origin sequence proven for this tenant.
    pub(crate) origin_sequence: u64,
    /// The greatest HLC stamp observed for this origin.
    pub(crate) hlc: u64,
}

pub(crate) use participant::{ParticipantIdentity, ParticipantPrepare, ParticipantPrepareReceipt};

/// The private storage adapter seam the Engine depends on.
///
/// The production implementation is [`SqliteStorage`]. The trait exists so the
/// storage seam stays substitutable behind one typed contract instead of
/// leaking file, codec, or page details into the Engine.
pub(crate) trait StoragePort: Send {
    fn prepare_participant(
        &mut self,
        _prepare: &ParticipantPrepare,
    ) -> Result<ParticipantPrepareReceipt, ()> {
        Err(())
    }
    fn resolve_participant(
        &mut self,
        _identity: &ParticipantIdentity,
    ) -> Result<Option<ParticipantPrepareReceipt>, ()> {
        Ok(None)
    }
    fn lifecycle_update(
        &mut self,
        _tenant: &TenantId,
        _command: crate::engine::lifecycle_state::LifecycleCommand,
    ) -> Result<
        crate::engine::lifecycle_state::LifecycleState,
        crate::engine::lifecycle_state::LifecycleError,
    > {
        Err(crate::engine::lifecycle_state::LifecycleError::Unavailable)
    }

    fn lifecycle_operation(
        &mut self,
        _tenant: &TenantId,
        _command: crate::engine::lifecycle_state::LifecycleCommand,
    ) -> Result<
        crate::engine::lifecycle_state::LifecycleOperationOutcome,
        crate::engine::lifecycle_state::LifecycleError,
    > {
        Err(crate::engine::lifecycle_state::LifecycleError::Unavailable)
    }

    fn configure_usage_placement(
        &mut self,
        _placement: &crate::usage::UsagePlacement,
    ) -> Result<(), ()> {
        Err(())
    }
    fn usage_placement(
        &self,
        _tenant: &TenantId,
    ) -> Result<Option<crate::usage::UsagePlacement>, ()> {
        Ok(None)
    }

    fn local_replication_origin(&self) -> &str;

    fn replication_repair_cut(
        &self,
        tenant: &TenantId,
        cut: u64,
    ) -> Result<(std::collections::BTreeMap<String, u64>, [u8; 32]), ()>;
    fn replication_range_summary(
        &self,
        tenant: &TenantId,
        origin: &str,
        cut: u64,
        first: u64,
        last: u64,
    ) -> Result<terrabase_replication_core::anti_entropy::RangeFingerprint, ()>;
    fn replication_range_publications(
        &self,
        tenant: &TenantId,
        origin: &str,
        cut: u64,
        first: u64,
        last: u64,
    ) -> Result<Vec<crate::engine::ReplicatedPublication>, ()>;

    /// Reads complete retained source groups, including their original private controls.
    fn replication_publications(
        &self,
        tenant: &TenantId,
        after: u64,
        limit: u32,
    ) -> Result<Vec<(u64, crate::engine::ReplicatedPublication)>, ()>;
    /// Reads private control state at the transaction's immutable snapshot.
    fn private_metadata_at(
        &self,
        tenant: &TenantId,
        key: &str,
        sequence: StorageSequence,
    ) -> Result<Option<crate::types::DocumentValue>, ()>;

    /// Loads one private webhook metadata record.
    fn webhook_metadata_load(
        &self,
        tenant: &TenantId,
        namespace: &str,
        key: &str,
    ) -> Result<Option<Vec<u8>>, ()>;

    /// Lists a complete bounded private webhook namespace, or fails.
    fn webhook_metadata_list(
        &self,
        tenant: &TenantId,
        namespace: &str,
    ) -> Result<Vec<(String, Vec<u8>)>, ()>;

    /// Atomically replaces private webhook metadata on exact previous bytes.
    fn webhook_metadata_compare_exchange(
        &mut self,
        tenant: &TenantId,
        namespace: &str,
        key: &str,
        expected: Option<&[u8]>,
        replacement: &[u8],
    ) -> Result<bool, ()>;

    /// Loads an opaque public schema envelope from internal metadata.
    fn schema_metadata_load(
        &self,
        database: &str,
        branch: &str,
        collection: &str,
    ) -> Result<Option<Vec<u8>>, ()>;

    /// Runs a file capture with commits and background checkpoints excluded.
    fn with_quiescent_wal(&self, capture: &mut dyn FnMut(&std::path::Path)) -> Result<(), ()>;
    /// Opens a read-only `SQLite` handle for one bounded SQL execution.
    ///
    /// This narrow capability keeps physical storage access behind the Engine;
    /// query execution never reconstructs a database from an unverified path.
    fn open_sqlite_read_connection(&self) -> Result<rusqlite::Connection, ()>;

    /// Reads persisted origin authority and its closed state.
    fn origin_epoch(&self) -> Result<(u64, bool), ()>;

    /// Monotonically changes origin authority under the `SQLite` writer transaction.
    fn set_origin_epoch(&mut self, epoch: u64, closed: bool) -> Result<(), ()>;

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

    /// Returns the original source fingerprint bound to a replicated publication.
    fn replication_fingerprint(
        &self,
        tenant: &TenantId,
        operation: &OperationId,
    ) -> Result<Option<[u8; 32]>, ()>;

    /// Appends one standalone authoritative usage fact durably and
    /// idempotently.
    fn record_usage(
        &mut self,
        tenant: &TenantId,
        fact: UsageFact,
    ) -> Result<(DurableUsageFact, bool), UsageStorageError>;

    /// Freezes a trusted completed storage interval from durable logical bytes.
    fn close_storage_usage(
        &mut self,
        _tenant: &TenantId,
        _window: MeasurementWindow,
    ) -> Result<crate::usage::StorageUsageClosure, UsageStorageError> {
        Err(UsageStorageError::Unavailable)
    }

    /// Durable accepted-deletion fence.
    fn storage_usage_stopped_at(
        &self,
        _tenant: &TenantId,
    ) -> Result<Option<u64>, UsageStorageError> {
        Err(UsageStorageError::Unavailable)
    }

    /// Last complete immutable storage cut, absent before the first closure.
    fn storage_usage_closed_through(
        &self,
        _tenant: &TenantId,
    ) -> Result<Option<u64>, UsageStorageError> {
        Err(UsageStorageError::Unavailable)
    }

    /// Stops all future customer storage accrual at accepted database deletion.
    fn stop_storage_usage(
        &mut self,
        _tenant: &TenantId,
        _at_millis: u64,
    ) -> Result<(), UsageStorageError> {
        Err(UsageStorageError::Unavailable)
    }

    /// Returns at most `limit` tenant facts at or below the fixed snapshot,
    /// ordered strictly after the optional continuation position. The Engine
    /// asks for one extra row to decide whether another page exists.
    fn usage_facts_page(
        &self,
        tenant: &TenantId,
        snapshot: StorageSequence,
        after: Option<&UsageFactPosition>,
        window: Option<MeasurementWindow>,
        limit: u32,
    ) -> Result<Vec<DurableUsageFact>, UsageStorageError>;

    /// Returns one database's metering fact, when it has billed rows.
    fn metering_fact(&self, _db: &str) -> Option<DbUsageFact> {
        None
    }

    /// Returns one page of per-database metering facts in `db` order with
    /// an opaque cursor resuming strictly after `after`. The cursor is the
    /// last fact's `db`; callers treat it as opaque bytes, never a key.
    fn metering_facts_page(
        &self,
        _after: Option<&str>,
        _limit: u32,
    ) -> (Vec<DbUsageFact>, Option<String>) {
        (Vec::new(), None)
    }

    /// Returns one database's live logical bytes: newest version per key,
    /// tombstones excluded (the D-16 storage dimension's snapshot input).
    fn metering_live_bytes(&self, _db: &str) -> u64 {
        0
    }

    /// Records one served read for abuse-guard and attribution counters
    /// (D-16/D-44: reads are metered, never billed).
    fn record_read(&self, _db: &str) {}

    /// Returns one database's served-read count.
    fn read_count(&self, _db: &str) -> u64 {
        0
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

    /// Resolves the raw version before a journal publication. An unavailable
    /// predecessor must remain unknown, never be mistaken for an insert.
    fn journal_before_image(
        &self,
        _tenant: &TenantId,
        _collection: &CollectionName,
        _id: &DocumentId,
        _sequence: StorageSequence,
    ) -> Result<JournalBeforeImage, ()> {
        Ok(JournalBeforeImage::Unknown)
    }

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
    /// The pushed filter is a sound narrowing, never an exact one: the
    /// implementation may drop conditions it cannot express exactly (fields without an
    /// active descriptor, value kinds the columns do not carry), and the
    /// caller still applies its full predicate over the returned page.
    /// `None` reports that this implementation cannot push the filter at all and
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

    /// Measures ordinary and forced-index generated reads in one read transaction.
    fn shadow_filtered_page(
        &self,
        _sequence: StorageSequence,
        _tenant: &TenantId,
        _request: &ShadowReadRequest<'_>,
    ) -> Result<ShadowReadComparison, ShadowReadError> {
        Err(ShadowReadError::Unavailable)
    }

    /// Creates or resumes a hidden physical candidate with durable write maintenance.
    fn begin_invisible_index(
        &mut self,
        _tenant: &TenantId,
        _collection: &CollectionName,
        _id: &str,
        _columns: &[InvisibleIndexColumn],
    ) -> Result<InvisibleIndexProgress, ()> {
        Err(())
    }

    /// Advances a hidden candidate by at most the admitted number of rows.
    fn advance_invisible_index(
        &mut self,
        _tenant: &TenantId,
        _id: &str,
        _batch: u32,
    ) -> Result<InvisibleIndexProgress, ()> {
        Err(())
    }

    /// Installs one canonical compound index; false means backfill is pending.
    fn register_schema_index(
        &mut self,
        _tenant: &TenantId,
        _collection: &CollectionName,
        _name: &str,
        _fields: &[String],
        _unique: bool,
    ) -> Result<bool, ()> {
        Err(())
    }

    /// Removes a canonical index and its Region uniqueness constraint.
    fn remove_schema_index(
        &mut self,
        _tenant: &TenantId,
        _collection: &CollectionName,
        _name: &str,
    ) -> Result<(), ()> {
        Err(())
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

    /// Removes an extracted descriptor and releases unused physical resources.
    /// Returns false when the descriptor is already absent.
    ///
    /// # Errors
    ///
    /// Refuses invalid fields, descriptors used by hidden builds, or storage failures.
    fn remove_index_descriptor(
        &mut self,
        _collection: &CollectionName,
        _field: &str,
    ) -> Result<bool, IndexDescriptorRemovalError> {
        Err(IndexDescriptorRemovalError::Storage)
    }

    /// Lists the durable index descriptors with their activity and
    /// remaining governed-backfill work. The default reports none.
    fn index_descriptors(&self) -> Result<Vec<DescriptorListing>, ()> {
        Err(())
    }

    /// Reports one field's durable descriptor state without registering
    /// anything: `Some(true)` is an active descriptor whose conditions push,
    /// `Some(false)` is a building descriptor whose backfill still owes rows,
    /// and `None` is no descriptor (or an unavailable descriptor state).
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

    /// Applies a collection TTL descriptor and advances its durable bounded backfill.
    /// Returns true only once all retained rows have the declared expiry metadata.
    ///
    /// # Errors
    /// Returns a durable storage failure or invalid batch refusal.
    fn advance_schema_ttl(
        &mut self,
        _collection: &CollectionName,
        _field: Option<&str>,
        _batch: u32,
    ) -> Result<bool, ()> {
        Err(())
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

    /// Retires a full-text descriptor and its maintained term rows.
    ///
    /// # Errors
    /// Returns a durable storage failure.
    fn remove_schema_fts(&mut self, _collection: &CollectionName, _field: &str) -> Result<(), ()> {
        Err(())
    }

    /// Reports one field's text-descriptor state without registering
    /// anything: `Some(true)` narrows, `Some(false)` is building, and
    /// `None` is no descriptor (or an unavailable descriptor state).
    fn fts_descriptor_state(&self, _collection: &CollectionName, _field: &str) -> Option<bool> {
        None
    }

    /// Returns the FTS5 candidate set of one search — the matching
    /// document ids with relevance scores, higher first — or `None` when
    /// this implementation cannot narrow the read and the caller must take the
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
    /// an implementation fall back to its current published state.
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
    /// Storage failures or invalid rows reject the entire page; an empty
    /// successful page means no retained commits matched the cursor.
    fn tenant_commits(
        &self,
        tenant: &TenantId,
        after: StorageSequence,
        limit: u32,
    ) -> Result<Vec<PublishedCommit>, JournalReadError>;

    /// Looks up an exact retained commit for publication proof. Unlike replay,
    /// this does not promise that intervening journal history is complete.
    fn retained_commit(
        &self,
        tenant: &TenantId,
        sequence: StorageSequence,
    ) -> Result<Option<PublishedCommit>, ()>;

    /// Returns one bounded page of retained journal rows. `after` is only a
    /// paging cursor in global storage order; reconciliation identities are
    /// always the row's persisted `(origin, origin_sequence)` pair.
    fn journal_rows(
        &self,
        _tenant: &TenantId,
        _after: StorageSequence,
        _limit: u32,
    ) -> Vec<JournalRow> {
        Vec::new()
    }

    /// Recovers contiguous origin prefixes proven by durable storage.
    /// Missing history never proves a prefix; storage errors remain errors.
    fn durable_origin_prefixes(&self, _tenant: &TenantId) -> Result<Vec<AppliedFrontierEntry>, ()> {
        Err(())
    }

    /// Returns every tenant that has at least one recorded published commit,
    /// in canonical tenant order (W4-CA-4).
    fn committed_tenants(&self) -> Result<Vec<TenantId>, ()>;

    /// Returns the tenant's latest recorded sequence at or below `at`
    /// (W4-CA-4): the mapping entry that turns one global
    /// storage-sequence cursor into this tenant's own progress.
    fn tenant_sequence(
        &self,
        tenant: &TenantId,
        at: StorageSequence,
    ) -> Result<Option<StorageSequence>, ()>;

    /// Returns the oldest sequence the durable publication history can
    /// serve, or `None` when no history is servable (W4-CA-4).
    ///
    /// A cursor older than this bound cannot be served incrementally: the
    /// caller must fall back to a full catch-up or backup instead of
    /// silently skipping the gap.
    fn servable_history_start(&self) -> Result<Option<StorageSequence>, ()>;

    /// Returns the last global storage sequence durably recorded in the
    /// publication-history journal (W4-CA-4 repair F2).
    ///
    /// Sequences have legitimate holes, so a consumer cannot detect a
    /// skipped journal append by discontinuity; this durable high-water
    /// mark is the real signal. `journaled_high_water() <
    /// published_state().0` means at least one published commit is not
    /// servable from the journal, and the consumer must fall back instead
    /// of assuming completeness.
    fn journaled_high_water(&self) -> Result<Option<StorageSequence>, ()>;

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

    /// Reads a bounded page of database-owned index lifecycle events.
    fn index_lifecycle_events(&self, from: u64, limit: usize) -> Result<Vec<Vec<u8>>, ()>;

    /// Appends exactly the next lifecycle event with synced `SQLite` durability.
    fn append_index_lifecycle_event(&mut self, sequence: u64, payload: &[u8]) -> Result<(), ()>;

    /// Returns the opaque, versioned causal context persisted with the
    /// document's newest row. Storage keeps this as bytes so merge and
    /// replication can carry it without a JSON/document decode on reads.
    fn document_context(
        &self,
        _tenant: &TenantId,
        _collection: &CollectionName,
        _id: &DocumentId,
    ) -> Option<Vec<u8>> {
        None
    }

    /// Atomically records a superseded source and optional conflict history.
    /// Returns true when the same decision was already recorded.
    fn record_superseded_replication(
        &mut self,
        intent: SupersededReplication,
    ) -> Result<bool, CommitRejection>;

    /// Reports whether a source operation has a durable superseded decision.
    fn replication_was_superseded(
        &self,
        tenant: &TenantId,
        operation: &OperationId,
    ) -> Result<bool, ()>;

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

    /// Durably advances the independent native backup retention floor.
    fn advance_backup_frontier(&mut self, sequence: u64) -> Result<(), ()>;

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
/// `Origin`, this location's stable origin identity, and its HLC stamp;
/// replicated rows retain the source origin and stamp from the incoming dot.
///
/// The type is crate-public in spirit; it is `pub` only so
/// [`SyncJournalRecord`](crate::streaming::SyncJournalRecord) can expose it
/// to the in-process tailer seam beside the journal types.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct JournalProvenance {
    kind: JournalKind,
    origin: String,
    origin_sequence: u64,
    hlc: u64,
    /// The opaque, versioned dotted causal context persisted with the row.
    ///
    /// Storage and replication adapters pass this through without
    /// interpreting its bytes. An empty value is retained for journal rows
    /// written before causal-context persistence was introduced.
    context: Vec<u8>,
}

/// The predecessor of a durable document publication, for result-view routing.
#[derive(Clone, Debug, PartialEq)]
pub enum JournalBeforeImage {
    /// No live row preceded the publication.
    Absent,
    /// The previous published document, including its version.
    Document(Box<crate::types::Document>),
    /// Retention removed the evidence, or this replay did not request it.
    /// Dependency dispatch must conservatively include the collection.
    Unknown,
}

impl JournalProvenance {
    /// Assembles one provenance from its facts. Crate-only: provenance is
    /// recorded by the storage write path, never minted by a consumer.
    pub(crate) fn new(kind: JournalKind, origin: String, origin_sequence: u64, hlc: u64) -> Self {
        Self { kind, origin, origin_sequence, hlc, context: Vec::new() }
    }

    /// Attaches the exact durable causal context carried by the journal row.
    /// Crate-only because storage is the sole authority that constructs
    /// journal provenance.
    pub(crate) fn with_context(mut self, context: Vec<u8>) -> Self {
        self.context = context;
        self
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

    /// Returns the writing region's durable per-origin journal sequence.
    #[must_use]
    pub const fn origin_sequence(&self) -> u64 {
        self.origin_sequence
    }

    /// Returns the writing region's hybrid logical clock stamp.
    #[must_use]
    pub const fn hlc(&self) -> u64 {
        self.hlc
    }

    /// Returns the opaque, versioned dotted causal context persisted with the
    /// journal row.
    #[must_use]
    pub fn context(&self) -> &[u8] {
        &self.context
    }
}

/// One retained publication-journal row exposed to the engine's
/// anti-entropy adapter. Storage owns the durable field ordering and
/// contents; the replication boundary hashes the canonical bytes.
#[derive(Clone, Debug, Eq, PartialEq)]
pub(crate) struct JournalRow {
    /// This location's global publication sequence, used only for paging.
    pub(crate) commit_sequence: u64,
    /// The persisted writing origin.
    pub(crate) origin: String,
    /// The persisted per-origin sequence.
    pub(crate) origin_sequence: u64,
    /// Canonical bytes covering the complete journal row.
    pub(crate) canonical: Vec<u8>,
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
pub(crate) use codec::{document_fingerprint, write_set_digest};

/// Reports whether one document is inside the codec's structural limits.
pub(crate) use codec::document_within_limits;

pub(crate) use codec::{
    decode_private_changes, decode_private_value, encode_private_value, transaction_digest,
};
pub(crate) use intent::{PrivateMetadataWrite, SchemaEnvelopeWrite};

pub(crate) use intent::ReplicationPublicationIntent;
