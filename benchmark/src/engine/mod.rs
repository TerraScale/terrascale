//! The database-owned Engine: documents, transactions, and commit outcomes.
//!
//! The Engine opens one durable location, hands out immutable read snapshots,
//! runs one atomic publication per transaction, and resolves uncertain commits
//! by operation identity from the durable operation ledger. It exposes no
//! in-memory durable constructor: [`Engine::open`] with an
//! [`EngineConfig::durable`] location is the only production path.

use rusqlite::OptionalExtension as _;
use std::{
    collections::{BTreeMap, BTreeSet},
    path::PathBuf,
    sync::{
        Arc, Mutex,
        atomic::{AtomicU64, Ordering},
    },
    time::{Instant, SystemTime, UNIX_EPOCH},
};

mod authorized;
mod batch_request;
mod branch_restore;
#[cfg(test)]
mod branch_restore_tests;
mod document_request;
mod export;
mod ingest;
pub mod lifecycle_state;
pub(crate) mod merge;
mod metadata;
mod origin_fencing;
mod patch;
mod physical_restore;
mod private_metadata;
mod recovery_statistics;
mod repair_summary;
mod replicated_publication;
mod replication_outbox;
mod retention;
pub use repair_summary::ReplicationRepairCut;
mod storage_usage;
mod usage_placement;
mod usage_budget;
#[cfg(test)]
mod usage_placement_tests;
pub use replicated_publication::{
    MAX_REPLICATION_PUBLICATION_BYTES, MAX_REPLICATION_PUBLICATION_RECORDS, ReplicatedPublication,
};
mod cross_file;
mod replication_dependencies;
mod replication_wire;
mod schema_indexes;
mod schema_metadata;
pub(crate) mod shard_catchup;
mod shard_split;
pub(crate) mod shard_supervisor;
mod webhook_metadata;

mod commit_timing_probe;
#[cfg(test)]
mod ingest_tests;

#[cfg(test)]
mod metadata_tests;

#[cfg(test)]
mod cross_file_tests;
#[cfg(test)]
mod patch_tests;
#[cfg(test)]
mod shard_split_tests;
#[cfg(test)]
mod shard_serving_correction_tests;

#[cfg(test)]
mod tests;
#[cfg(test)]
pub(crate) use tests::test_storage_keys;

pub use crate::storage::DatabaseOwner;
pub use crate::storage::database_file;
pub use crate::storage::{
    DocumentMergeState, DocumentRevisionEntry, MergeConflictEntry, MergeFieldKind,
    MergeKernelEntry, MergePolicy, MergeSchema, MergeSchemaError,
};
pub use authorized::{AuthorizedEngine, AuthorizedEngineError, AuthorizedSession};
pub use cross_file::{CrossFileReceiptPhase, LocalCrossFileReceipt};
pub use export::{ExportHistoryRow, ExportMetadataSnapshot};
pub use ingest::{
    BulkIngestRequest, IngestBatchReceipt, IngestConflictMode, IngestReceipt, IngestRefusal,
    IngestRefusalReason, IngestStream, IngestTermination, MAX_INGEST_BATCH_DOCUMENTS,
    MAX_INGEST_REFUSAL_DETAILS,
};
pub use metadata::{
    ConditionalLoadError, DocumentHead, DocumentVersionContract, DocumentVersionContractError,
    HeadCondition,
};
pub use patch::{
    AddOrPatch, DocumentPatch, MAX_PATCH_FIELD_BYTES, MAX_PATCH_OPERATIONS,
    MAX_PATCH_PATH_SEGMENTS, PatchError, PatchOperation, PatchOutcome, PatchPath, PatchPathSegment,
    apply_add_or_patch, apply_document_patch,
};
pub use recovery_statistics::RecoveryStatistics;
pub(crate) use recovery_statistics::{logical_document_digest, logical_state_hasher};

use crate::{
    families::FamilyStore,
    observability::{CommitResult as MetricCommitResult, MetricsRegistry, Outcome},
    operations::{EngineRequest, EngineResponse},
    query::QueryStore,
    storage::{
        self, CommitIntent, CommitPublicationEvent, CommitResult, DbUsageFact, PublishedCommit,
        StagedWrite, StoragePort, UsageFactPosition, UsageStorageError,
    },
    streaming::StreamStore,
    types::{
        CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion, IndexGenerationSet,
        LocalDurability, OperationId, ReadSnapshot, RegionId, ReplicationClockStamp,
        RetryDisposition, StorageSequence, TenantId, TenantScope,
    },
    usage::{
        MAX_USAGE_PAGE_SIZE, MeasurementWindow, MintingRule, UsageAdmissionContext,
        UsageCommandError, UsageContinuation, UsageFactId, UsagePage, UsagePageRequest,
        UsageRecordReceipt, UsageRecordRequest,
    },
};

#[cfg(test)]
use crate::storage::FaultPoint;

/// A sink receiving immutable publication events after locks release.
pub(crate) type CommitPublicationSink = Arc<dyn Fn(&CommitPublicationEvent) + Send + Sync>;

/// The maximum number of publication sinks one Engine registers at a time.
///
/// This is the publication path's subscriber bound (W4-CA-3). The emit path
/// calls every registered sink synchronously on the committing thread, so the
/// registry itself must stay small and bounded: a sink offered beyond this
/// bound is not registered, and the returned guard's
/// [`SubscriptionGuard::is_attached`] reports `false`.
pub(crate) const MAX_PUBLICATION_SINKS: usize = 64;

/// Maximum rows in one boundary-pinned final-state snapshot page.
pub(crate) const MAX_SNAPSHOT_RECORD_PAGE: u32 = 256;

/// The row budget of one governed index-backfill batch (D-31): each
/// [`Engine::maintain_index_backfills`] call covers at most this many rows
/// per building descriptor, so the host cadence — not the pass — owns how
/// much write-path time the backfill takes.
pub const INDEX_BACKFILL_BATCH: u64 = 4096;

const USAGE_CONTINUATION_MAGIC: &[u8; 8] = b"TBUCUR01";
const USAGE_CONTINUATION_DOMAIN: &[u8] = b"terrascale.usage.continuation.v1\0";

/// Decoded state carried by one opaque usage continuation.
struct UsageContinuationState {
    tenant: TenantId,
    snapshot: StorageSequence,
    window: Option<MeasurementWindow>,
    page_size: u32,
    after: UsageFactPosition,
}

/// A bounded reader for the private usage-continuation encoding.
struct UsageContinuationReader<'a> {
    bytes: &'a [u8],
    position: usize,
}

impl<'a> UsageContinuationReader<'a> {
    const fn new(bytes: &'a [u8]) -> Self {
        Self { bytes, position: 0 }
    }

    fn take(&mut self, count: usize) -> Option<&'a [u8]> {
        let end = self.position.checked_add(count)?;
        let value = self.bytes.get(self.position..end)?;
        self.position = end;
        Some(value)
    }

    fn u8(&mut self) -> Option<u8> {
        self.take(1).map(|bytes| bytes[0])
    }

    fn u16(&mut self) -> Option<u16> {
        Some(u16::from_le_bytes(self.take(2)?.try_into().ok()?))
    }

    fn u32(&mut self) -> Option<u32> {
        Some(u32::from_le_bytes(self.take(4)?.try_into().ok()?))
    }

    fn u64(&mut self) -> Option<u64> {
        Some(u64::from_le_bytes(self.take(8)?.try_into().ok()?))
    }

    fn string(&mut self) -> Option<String> {
        let length = usize::from(self.u16()?);
        String::from_utf8(self.take(length)?.to_vec()).ok()
    }

    const fn is_empty(&self) -> bool {
        self.position == self.bytes.len()
    }
}

fn usage_rule_tag(rule: MintingRule) -> u8 {
    match rule {
        MintingRule::AcceptedOriginCommit => 0,
        MintingRule::StableAdmittedRequest => 1,
        MintingRule::PublicResponseBytes => 2,
        MintingRule::ActiveRegionHour => 3,
        MintingRule::BackupByteHour => 4,
        MintingRule::ExplicitRestoreWork => 5,
    }
}

fn usage_rule_from_tag(tag: u8) -> Option<MintingRule> {
    Some(match tag {
        0 => MintingRule::AcceptedOriginCommit,
        1 => MintingRule::StableAdmittedRequest,
        2 => MintingRule::PublicResponseBytes,
        3 => MintingRule::ActiveRegionHour,
        4 => MintingRule::BackupByteHour,
        5 => MintingRule::ExplicitRestoreWork,
        _ => return None,
    })
}

fn push_usage_cursor_string(bytes: &mut Vec<u8>, value: &str) {
    let length = u16::try_from(value.len()).expect("validated usage identifiers fit u16");
    bytes.extend_from_slice(&length.to_le_bytes());
    bytes.extend_from_slice(value.as_bytes());
}

fn usage_continuation_checksum(body: &[u8]) -> [u8; 32] {
    let mut hasher = blake3::Hasher::new();
    hasher.update(USAGE_CONTINUATION_DOMAIN);
    hasher.update(body);
    *hasher.finalize().as_bytes()
}

fn encode_usage_continuation(
    tenant: &TenantId,
    snapshot: StorageSequence,
    request: &UsagePageRequest,
    after: &UsageFactPosition,
) -> UsageContinuation {
    let mut bytes = Vec::with_capacity(128);
    bytes.extend_from_slice(USAGE_CONTINUATION_MAGIC);
    bytes.extend_from_slice(&snapshot.get().to_le_bytes());
    bytes.extend_from_slice(&request.page_size().to_le_bytes());
    match request.window() {
        Some(window) => {
            bytes.push(1);
            bytes.extend_from_slice(&window.start_millis().to_le_bytes());
            bytes.extend_from_slice(&window.end_millis().to_le_bytes());
        }
        None => bytes.push(0),
    }
    push_usage_cursor_string(&mut bytes, tenant.as_str());
    bytes.extend_from_slice(&after.sequence.to_le_bytes());
    bytes.push(usage_rule_tag(after.rule));
    push_usage_cursor_string(&mut bytes, after.id.as_str());
    let checksum = usage_continuation_checksum(&bytes);
    bytes.extend_from_slice(&checksum);
    UsageContinuation::from_trusted_bytes(bytes)
}

fn decode_usage_continuation(
    continuation: &UsageContinuation,
) -> Result<UsageContinuationState, UsageCommandError> {
    let bytes = continuation.as_bytes();
    if bytes.len() < USAGE_CONTINUATION_MAGIC.len() + 32 {
        return Err(UsageCommandError::InvalidRequest);
    }
    let (body, checksum) = bytes.split_at(bytes.len() - 32);
    if usage_continuation_checksum(body).as_slice() != checksum {
        return Err(UsageCommandError::InvalidRequest);
    }
    let mut reader = UsageContinuationReader::new(body);
    if reader.take(USAGE_CONTINUATION_MAGIC.len()) != Some(USAGE_CONTINUATION_MAGIC) {
        return Err(UsageCommandError::InvalidRequest);
    }
    let snapshot = StorageSequence::new(reader.u64().ok_or(UsageCommandError::InvalidRequest)?);
    let page_size = reader.u32().ok_or(UsageCommandError::InvalidRequest)?;
    let window = match reader.u8().ok_or(UsageCommandError::InvalidRequest)? {
        0 => None,
        1 => Some(
            MeasurementWindow::interval(
                reader.u64().ok_or(UsageCommandError::InvalidRequest)?,
                reader.u64().ok_or(UsageCommandError::InvalidRequest)?,
            )
            .map_err(|_| UsageCommandError::InvalidRequest)?,
        ),
        _ => return Err(UsageCommandError::InvalidRequest),
    };
    let tenant = TenantId::try_new(&reader.string().ok_or(UsageCommandError::InvalidRequest)?)
        .map_err(|_| UsageCommandError::InvalidRequest)?;
    let sequence = reader.u64().ok_or(UsageCommandError::InvalidRequest)?;
    let rule = usage_rule_from_tag(reader.u8().ok_or(UsageCommandError::InvalidRequest)?)
        .ok_or(UsageCommandError::InvalidRequest)?;
    let id = UsageFactId::try_new(&reader.string().ok_or(UsageCommandError::InvalidRequest)?)
        .map_err(|_| UsageCommandError::InvalidRequest)?;
    if !reader.is_empty() || snapshot.get() == 0 || sequence == 0 || sequence > snapshot.get() {
        return Err(UsageCommandError::InvalidRequest);
    }
    Ok(UsageContinuationState {
        tenant,
        snapshot,
        window,
        page_size,
        after: UsageFactPosition { sequence, rule, id },
    })
}

const fn map_usage_storage_error(error: UsageStorageError) -> UsageCommandError {
    match error {
        UsageStorageError::Corrupt => UsageCommandError::CorruptStorage,
        UsageStorageError::IdentityConflict => UsageCommandError::IdentityConflict,
        UsageStorageError::Unavailable => UsageCommandError::StorageUnavailable,
    }
}

fn usage_bound_commit_digest(
    base: u64,
    context: Option<&UsageAdmissionContext>,
    allocation: Option<&crate::usage::VerifiedAllocation>,
) -> u64 {
    if context.is_none() && allocation.is_none() {
        return base;
    }
    let mut hasher = blake3::Hasher::new();
    hasher.update(b"terrascale.commit.usage-admission.v1\0");
    hasher.update(&base.to_le_bytes());
    if let Some(context) = context {
        hasher.update(&context.digest_bytes());
    }
    if let Some(allocation) = allocation {
        hasher.update(allocation.payload_digest());
    }
    let digest = hasher.finalize();
    u64::from_le_bytes(digest.as_bytes()[..8].try_into().expect("blake3 has eight bytes"))
}

/// The production-visible, multi-subscriber registry of publication-event
/// sinks (integrator action W4-CA-3).
///
/// The registry replaces the former single-slot, test-only sink. Its emit
/// path snapshots the registered sink handles under the registry lock — a
/// bounded clone of at most [`MAX_PUBLICATION_SINKS`] handles — releases the
/// lock, and only then calls each sink. Consequences the design relies on:
///
/// - No sink call executes while the registry lock is held, so a sink that
///   subscribes or unsubscribes during its own emit cannot deadlock the
///   committing thread, and the registry stays operable while a sink runs.
/// - Sinks are called synchronously on the committing thread, after storage
///   locks release. A sink must not block, must not take a storage lock, and
///   must not grow an unbounded queue: a slow or overflowing sink reports
///   through its own bounded channel or is dropped, and never queues events
///   inside this registry.
pub(crate) struct PublicationRegistry {
    inner: Mutex<RegistryInner>,
}

/// The registry's guarded state.
struct RegistryInner {
    next_id: u64,
    sinks: BTreeMap<u64, CommitPublicationSink>,
}

impl PublicationRegistry {
    /// Creates an empty registry.
    pub(crate) fn new() -> Self {
        Self { inner: Mutex::new(RegistryInner { next_id: 1, sinks: BTreeMap::new() }) }
    }

    /// Registers one sink and returns its detach-on-drop guard.
    ///
    /// When the registry already holds [`MAX_PUBLICATION_SINKS`] sinks, the
    /// offered sink is not registered and the returned guard reports
    /// `!is_attached()`.
    pub(crate) fn subscribe(self: &Arc<Self>, sink: CommitPublicationSink) -> SubscriptionGuard {
        let mut inner = self.inner.lock().expect("publication registry lock");
        let id = if inner.sinks.len() < MAX_PUBLICATION_SINKS {
            let id = inner.next_id;
            inner.next_id = inner.next_id.saturating_add(1);
            inner.sinks.insert(id, sink);
            Some(id)
        } else {
            None
        };
        SubscriptionGuard { registry: Arc::clone(self), id }
    }

    /// Removes one registered sink identity.
    ///
    /// The removed sink closure drops outside the registry lock: a closure
    /// may own the last handle to its Engine, and dropping that Engine runs
    /// this registry again (the Engine's own index subscription detaches), so
    /// running sink destructors under the lock would self-deadlock.
    fn detach(&self, id: u64) {
        let removed = {
            let mut inner = self.inner.lock().expect("publication registry lock");
            inner.sinks.remove(&id)
        };
        drop(removed);
    }

    /// Emits one immutable event to every registered sink.
    ///
    /// The sink snapshot is bounded by [`MAX_PUBLICATION_SINKS`] and the lock
    /// is released before any sink runs; the calls themselves are synchronous
    /// and never queued.
    fn emit(&self, event: &CommitPublicationEvent) {
        let sinks: Vec<CommitPublicationSink> = {
            let inner = self.inner.lock().expect("publication registry lock");
            inner.sinks.values().map(Arc::clone).collect()
        };
        for sink in sinks {
            sink(event);
        }
    }
}

/// A guard that detaches its publication sink when dropped (W4-CA-3).
///
/// The guard does not borrow its Engine: it holds the registry handle, so it
/// may outlive the Engine it subscribed through, and dropping it removes
/// exactly its own sink.
pub(crate) struct SubscriptionGuard {
    registry: Arc<PublicationRegistry>,
    id: Option<u64>,
}

/// The engine has no free publication notification slots.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct PublicationWatchFull;

/// Physical capture could not acquire the engine's storage barriers.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct WalCaptureError;

impl std::fmt::Display for WalCaptureError {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter.write_str("the physical WAL capture barrier is unavailable")
    }
}

impl std::error::Error for WalCaptureError {}

/// A bounded publication notification subscription, detached when dropped.
pub struct PublicationWatch {
    _guard: SubscriptionGuard,
}

fn replication_operation_id(record: &ReplicatedRecord) -> Result<OperationId, EngineError> {
    OperationId::try_new(&record.commit_ulid).map_err(|_| EngineError::InvalidDocument)?;
    let mut hash = blake3::Hasher::new();
    hash.update(b"terrabase-replication-record-v1\0");
    for component in [
        record.origin.as_str(),
        record.commit_ulid.as_str(),
        record.collection.as_str(),
        record.id.as_str(),
    ] {
        hash.update(
            &u64::try_from(component.len())
                .map_err(|_| EngineError::InvalidDocument)?
                .to_be_bytes(),
        );
        hash.update(component.as_bytes());
    }
    OperationId::try_new(&format!("repl-{}", hash.finalize().to_hex()))
        .map_err(|_| EngineError::InvalidDocument)
}

fn replication_fingerprint(record: &ReplicatedRecord) -> Result<[u8; 32], EngineError> {
    let document = Document::new(record.id.clone(), record.collection.clone(), record.body.clone());
    if !storage::document_within_limits(&document) {
        return Err(EngineError::InvalidDocument);
    }
    let mut hash = blake3::Hasher::new();
    hash.update(b"terrabase-replication-content-v1\0");
    hash.update(&storage::document_fingerprint(&document));
    hash.update(&[u8::from(record.deleted)]);
    hash.update(&record.hlc.to_be_bytes());
    hash.update(&record.origin_sequence.to_be_bytes());
    for bytes in [record.origin.as_bytes(), record.commit_ulid.as_bytes(), &record.context] {
        hash.update(
            &u64::try_from(bytes.len()).map_err(|_| EngineError::InvalidDocument)?.to_be_bytes(),
        );
        hash.update(bytes);
    }
    Ok(*hash.finalize().as_bytes())
}

impl Engine {
    /// Registers a notification after a committed publication releases storage locks.
    /// Callbacks must be brief and nonblocking; durable data is read separately.
    ///
    /// # Errors
    ///
    /// Returns `PublicationWatchFull` when the publication registry is full.
    pub fn watch_publications(
        &self,
        notify: impl Fn() + Send + Sync + 'static,
    ) -> Result<PublicationWatch, PublicationWatchFull> {
        let guard = self.subscriptions.subscribe(Arc::new(move |_| notify()));
        if !guard.is_attached() {
            return Err(PublicationWatchFull);
        }
        Ok(PublicationWatch { _guard: guard })
    }
}

impl SubscriptionGuard {
    /// Reports whether the guarded sink is registered.
    ///
    /// `false` means the registry was at its subscriber bound when the sink
    /// was offered, so the sink never receives events.
    #[allow(dead_code, reason = "observed by the W4-STREAM subscription path; exercised in tests")]
    pub(crate) fn is_attached(&self) -> bool {
        self.id.is_some()
    }
}

impl Drop for SubscriptionGuard {
    fn drop(&mut self) {
        if let Some(id) = self.id.take() {
            self.registry.detach(id);
        }
    }
}

/// Describes one durable `SQLite` Engine location.
///
/// ```compile_fail
/// // No ephemeral durable Engine constructor exists. This must not compile.
/// let engine = terrabase::Engine::new_in_memory();
/// ```
///
/// ```compile_fail
/// // Neither does a memory-backed configuration path. This must not compile.
/// let config = terrabase::EngineConfig::from_memory();
/// ```
#[derive(Clone, Debug)]
pub struct EngineConfig {
    location: PathBuf,
    lifecycle_thresholds: crate::query::optimizer::LifecycleThresholds,
    owner: Option<crate::storage::DatabaseOwner>,
    replication_origin: Option<RegionId>,
    replication_lane_origin: Option<crate::origin_lane::OriginLaneId>,
    replication_clock: Arc<dyn ReplicationClock>,
    storage_keys: Option<Arc<dyn crate::encryption::DatabaseKeySource>>,
    allow_create: bool,
}

impl EngineConfig {
    /// Describes a durable Engine location, creating the directory if it is
    /// absent.
    ///
    /// # Errors
    ///
    /// Returns [`EngineConfigError::EmptyLocation`] for an empty location and
    /// [`EngineConfigError::NonDurableLocation`] when the location cannot
    /// become a directory, for example because a file already occupies it.
    pub fn durable(location: impl Into<PathBuf>) -> Result<Self, EngineConfigError> {
        let location = location.into();
        if location.as_os_str().is_empty() {
            return Err(EngineConfigError::EmptyLocation);
        }
        match std::fs::create_dir_all(&location) {
            Ok(()) => {}
            Err(_) if location.is_dir() => {}
            Err(_) => return Err(EngineConfigError::NonDurableLocation),
        }
        if !location.is_dir() {
            return Err(EngineConfigError::NonDurableLocation);
        }
        Ok(Self {
            location,
            lifecycle_thresholds: crate::query::optimizer::LifecycleThresholds::DEFAULT,
            owner: None,
            replication_origin: None,
            replication_lane_origin: None,
            replication_clock: Arc::new(SystemReplicationClock),
            storage_keys: None,
            allow_create: true,
        })
    }

    /// Describes an existing durable Engine location without creating it.
    pub(crate) fn existing_durable(
        location: impl Into<PathBuf>,
    ) -> Result<Self, EngineConfigError> {
        let location = location.into();
        if location.as_os_str().is_empty() {
            return Err(EngineConfigError::EmptyLocation);
        }
        if !location.is_dir() {
            return Err(EngineConfigError::NonDurableLocation);
        }
        Ok(Self::durable_existing_unchecked(location))
    }

    fn durable_existing_unchecked(location: PathBuf) -> Self {
        Self {
            location,
            lifecycle_thresholds: crate::query::optimizer::LifecycleThresholds::DEFAULT,
            owner: None,
            replication_origin: None,
            replication_lane_origin: None,
            replication_clock: Arc::new(SystemReplicationClock),
            storage_keys: None,
            allow_create: false,
        }
    }

    /// Binds this location to the supplied production database owner tuple.
    ///
    /// A location may already carry the same owner and is reopened only when
    /// every field matches. A local-development owner, a corrupt record, or
    /// a different scope is refused by [`Engine::open`].
    #[must_use]
    pub fn with_owner(mut self, owner: crate::storage::DatabaseOwner) -> Self {
        self.owner = Some(owner);
        self
    }

    /// Opens `SQLite`, WAL and rollback journals through authenticated encrypted
    /// storage with renewable, owner-bound key leases. Production owners must
    /// supply this source; plaintext locations are never adopted or converted.
    #[must_use]
    pub fn with_storage_keys(
        mut self,
        keys: Arc<dyn crate::encryption::DatabaseKeySource>,
    ) -> Self {
        self.storage_keys = Some(keys);
        self
    }

    /// Pins the origin written into this database's durable replication journal.
    /// Reopening with a different origin is refused.
    #[must_use]
    pub fn with_replication_origin(mut self, origin: RegionId) -> Self {
        self.replication_origin = Some(origin);
        self
    }

    /// Pins the unique causal origin of one physical shard lane.
    #[must_use]
    pub fn with_replication_lane_origin(
        mut self,
        origin: crate::origin_lane::OriginLaneId,
    ) -> Self {
        self.replication_lane_origin = Some(origin);
        self
    }

    /// Supplies the physical clock used by the origin HLC. This permits
    /// deterministic fault qualification through the same commit path.
    #[must_use]
    pub fn with_replication_clock(mut self, clock: Arc<dyn ReplicationClock>) -> Self {
        self.replication_clock = clock;
        self
    }

    /// Selects the auto-index lifecycle thresholds the Engine's index catalog
    /// measures against (IX-03 rider). The defaults carry V1's values; a
    /// caller scheduling [`Engine::maintain_indexes`] on its own timer
    /// configures the gates here, not through any post-open path.
    #[must_use]
    pub const fn with_lifecycle_thresholds(
        mut self,
        thresholds: crate::query::optimizer::LifecycleThresholds,
    ) -> Self {
        self.lifecycle_thresholds = thresholds;
        self
    }

    pub(crate) fn at_location(mut self, location: impl Into<PathBuf>) -> Self {
        self.location = location.into();
        self
    }

    /// Returns the explicitly bound production owner, if one was supplied.
    #[must_use]
    pub fn owner(&self) -> Option<&crate::storage::DatabaseOwner> {
        self.owner.as_ref()
    }

    /// Reports whether this target carries both an owner and storage keys.
    #[must_use]
    pub fn has_encrypted_owner(&self) -> bool {
        self.owner.is_some() && self.storage_keys.is_some()
    }

    /// Returns the configured durable location. Crate-only by design: the
    /// physical path never crosses the public seam.
    pub(crate) fn location(&self) -> &PathBuf {
        &self.location
    }

    /// Returns the configured lifecycle thresholds. Crate-only: production
    /// callers configure them through [`EngineConfig::durable`] plus
    /// [`EngineConfig::with_lifecycle_thresholds`], and the Engine applies
    /// them at open.
    pub(crate) fn lifecycle_thresholds(&self) -> crate::query::optimizer::LifecycleThresholds {
        self.lifecycle_thresholds
    }
}

/// Physical clock used to assign origin-write HLC stamps.
/// Implementations supply milliseconds since the Unix epoch; the engine
/// supplies monotonic logical advancement and witnesses remote stamps.
pub trait ReplicationClock: std::fmt::Debug + Send + Sync {
    /// Returns the current physical milliseconds.
    fn now_millis(&self) -> u64;
}

#[derive(Debug)]
struct SystemReplicationClock;

impl ReplicationClock for SystemReplicationClock {
    fn now_millis(&self) -> u64 {
        Engine::now_millis()
    }
}

/// Configuration validation failures that expose no physical paths.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum EngineConfigError {
    /// No durable location was supplied.
    EmptyLocation,
    /// The supplied location cannot provide durable storage.
    NonDurableLocation,
}

/// Errors encountered while opening a configured Engine.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum EngineOpenError {
    /// The configuration was invalid.
    InvalidConfig,
    /// The location is not an owned `SQLite` database directory.
    FormatMismatch,
    /// Durable storage could not be opened.
    StorageUnavailable,
}

/// A typed signal that an Engine has closed.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct EngineClosed;

/// Operational Engine failures.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum EngineError {
    /// The Engine has closed.
    Closed(EngineClosed),
    /// A snapshot belongs to another Engine.
    SnapshotEngineMismatch,
    /// A snapshot belongs to another tenant.
    SnapshotTenantMismatch,
    /// A scope does not match this Engine's identity or durable owner.
    ScopeMismatch,
    /// An operation identity has already been used for another request.
    OperationIdReuse {
        /// The reused operation identity.
        operation_id: OperationId,
    },
    /// The write set conflicts with the current view.
    Conflict,
    /// A document failed logical validation.
    InvalidDocument,
    /// The request deadline expired.
    Deadline,
    /// The request was cancelled.
    Cancelled,
    /// Durable storage returned a typed failure.
    StorageFailure,
    /// The replay cursor predates journal history removed by retention.
    HistoryExpired,
    /// A snapshot page requested more rows than the bounded seam permits.
    SnapshotPageLimitExceeded,
    /// The requested snapshot boundary is newer than the published view or
    /// older than the durable history still available to prove its winners.
    SnapshotBoundaryUnavailable,
}

/// Errors encountered while shutting down an Engine.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum EngineShutdownError {
    /// Durable storage failed while closing.
    StorageFailure,
    /// The Engine was already closed.
    AlreadyClosed,
}

/// Failures registering one extracted index column descriptor (D-31/D-42).
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum IndexRegistrationError {
    /// The database's extracted-column budget is exhausted.
    BudgetExceeded,
    /// Durable registration failed.
    Storage,
    /// The Engine has closed.
    Closed,
}

pub use crate::storage::DescriptorRegistration;

/// How one observed query field is covered by a durable extracted-column
/// descriptor (D-31): the auto-index audit's per-shape verdict.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum DescriptorCoverage {
    /// An active descriptor covers the field; conditions over it push.
    Active,
    /// The descriptor is building; its governed backfill still owes rows.
    Building {
        /// The commit-sequence span the governed backfill has not covered
        /// yet (registration watermark minus durable cursor).
        backfill_remaining: u64,
    },
    /// No descriptor covers the field; the first query registers one
    /// (inline when the collection is inside the cost cap, building
    /// otherwise).
    Absent,
}

/// One auto-index audit entry: an observed `(collection, field)` query shape
/// with its demand count and its durable-descriptor coverage (D-31).
///
/// The audit joins the optimizer's selection records (observed demand, this
/// tenant) with the durable descriptor table (coverage, whole database), so
/// one read serves both directions: uncovered shapes worth registering, and
/// registered descriptors with no observed demand worth retiring. The S-03
/// collections route serves this as the index listing/recommendation read.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct AutoIndexRecommendation {
    /// The queried collection.
    pub collection: String,
    /// The queried field path.
    pub field: String,
    /// How many recorded selections demanded this shape.
    pub observed_queries: u64,
    /// The shape's durable-descriptor coverage.
    pub coverage: DescriptorCoverage,
}

/// A local receipt for a commit that reached a known durability point.
///
/// The receipt carries only the operation identity, the storage sequence, and
/// the achieved [`LocalDurability`]. It never carries an acknowledgement
/// level, convergence, member, or region fact: those belong to the later
/// replication seam.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct CommitReceipt {
    operation_id: OperationId,
    storage_sequence: StorageSequence,
    durability: LocalDurability,
}

/// One final-state document and the durability achieved by its winning write.
/// Export consumers retain the winning write durability alongside its document.
#[derive(Clone, Debug, PartialEq)]
pub struct SnapshotRecord {
    document: Document,
    durability: LocalDurability,
}

impl SnapshotRecord {
    /// Returns the final-state document.
    #[must_use]
    pub fn document(&self) -> &Document {
        &self.document
    }

    /// Returns the durability of the write that won at the snapshot boundary.
    #[must_use]
    pub fn durability(&self) -> LocalDurability {
        self.durability
    }
}

impl CommitReceipt {
    /// Creates one receipt from the published local facts.
    pub(crate) fn new(
        operation_id: OperationId,
        storage_sequence: StorageSequence,
        durability: LocalDurability,
    ) -> Self {
        Self { operation_id, storage_sequence, durability }
    }

    /// Returns the stable operation identity.
    #[must_use]
    pub fn operation_id(&self) -> &OperationId {
        &self.operation_id
    }

    /// Returns the local storage sequence.
    #[must_use]
    pub fn sequence(&self) -> StorageSequence {
        self.storage_sequence
    }

    /// Returns the local durability point.
    #[must_use]
    pub fn durability(&self) -> LocalDurability {
        self.durability
    }
}

/// One journal row shipped for cross-region replication (D-35/D-36): the
/// published mutation — canonical body bytes for a put, a tombstone for a
/// delete — plus the causal metadata the receiving region applies it with.
///
/// The record is the in-process form of what the journal tailer puts on the
/// wire (slice 2a); the QUIC lanes (slice 2b) carry the same fields.
#[derive(Clone, Debug, PartialEq)]
pub struct ReplicatedRecord {
    /// The changed collection.
    pub collection: CollectionName,
    /// The changed document identity.
    pub id: DocumentId,
    /// The published document value (ignored for a tombstone).
    pub body: DocumentValue,
    /// Whether this record is a tombstone.
    pub deleted: bool,
    /// The source region's commit identity: the receiving row's
    /// `commit_ulid` and the idempotency binding for re-apply.
    pub commit_ulid: String,
    /// The source region's hybrid logical clock stamp.
    pub hlc: u64,
    /// The source region's identity.
    pub origin: String,
    /// The source region's durable per-origin journal sequence.
    pub origin_sequence: u64,
    /// Opaque versioned dotted causal context from the source journal row.
    ///
    /// The engine validates and joins this context, but never interprets
    /// document body bytes on the clean-apply path.
    pub context: Vec<u8>,
}

/// One retained publication-journal row for anti-entropy. The canonical
/// bytes are storage-owned and include tombstones; callers derive the
/// reconciliation hash without replacing the row identity with the global
/// storage sequence.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct JournalRow {
    /// Global publication sequence used only to page the journal.
    pub commit_sequence: u64,
    /// Persisted writing origin.
    pub origin: String,
    /// Persisted per-origin sequence.
    pub origin_sequence: u64,
    /// Canonical bytes covering the complete durable row.
    pub canonical: Vec<u8>,
}

/// The outcome of applying one [`ReplicatedRecord`]:
/// [`Engine::apply_replicated`]'s receipt plus whether this call published
/// or replayed an already-applied record.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct AppliedReplication {
    receipt: Option<CommitReceipt>,
    replayed: bool,
    superseded: bool,
}

impl AppliedReplication {
    /// Assembles one apply outcome. Crate-only.
    pub(crate) fn new(receipt: CommitReceipt, replayed: bool) -> Self {
        Self { receipt: Some(receipt), replayed, superseded: false }
    }

    /// The superseded outcome: the deterministic merge kept the current
    /// row and the incoming record published nothing. Crate-only.
    pub(crate) fn lost_merge() -> Self {
        Self { receipt: None, replayed: false, superseded: true }
    }

    /// Returns the local receipt of the apply (or of the original apply a
    /// replay resolved to). `None` when the record was superseded.
    #[must_use]
    pub const fn receipt(&self) -> Option<&CommitReceipt> {
        self.receipt.as_ref()
    }

    /// Reports whether the record had already been applied: the
    /// deterministic operation identity replayed a recorded publication
    /// or superseded decision.
    #[must_use]
    pub const fn replayed(&self) -> bool {
        self.replayed
    }

    /// Reports whether the deterministic merge superseded the record:
    /// the current row's `(hlc, origin)` won, nothing published, and
    /// every region reaches the same verdict.
    #[must_use]
    pub const fn superseded(&self) -> bool {
        self.superseded
    }
}

/// A rejected local commit.
///
/// The variants are the frozen contract set: no deadline, cancellation, or
/// uncertainty appears here. An uncertain outcome is
/// [`CommitOutcome::Unknown`] and is resolved by operation identity.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum CommitError {
    /// The write set failed logical validation.
    Validation,
    /// The write set conflicted with the published view.
    Conflict,
    /// The operation identity is bound to a different write set.
    OperationIdReuse,
    /// The Engine closed before the commit reached storage.
    EngineClosed,
    /// An authoritative usage-bearing mutation requested weaker durability.
    UsageRequiresSynced,
    /// Durable storage failed before publication.
    StorageFailure,
}

impl CommitError {
    /// Returns the closed, stable retry policy for this rejection.
    ///
    /// The mapping is exhaustive and stable: validation and conflict want a
    /// new identity, identity reuse may never be retried, closure wants the
    /// same identity after reopen, and a storage failure requires recovery
    /// before any new identity is issued.
    #[must_use]
    #[allow(clippy::match_same_arms)]
    pub fn retry_disposition(&self) -> RetryDisposition {
        match self {
            Self::Validation => RetryDisposition::NewOperation,
            Self::Conflict => RetryDisposition::NewOperation,
            Self::OperationIdReuse => RetryDisposition::Never,
            Self::EngineClosed => RetryDisposition::SameOperation,
            Self::UsageRequiresSynced => RetryDisposition::SameOperation,
            Self::StorageFailure => RetryDisposition::AfterRecovery,
        }
    }
}

/// The known or uncertain result of one local commit.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum CommitOutcome {
    /// The commit reached the requested local durability point.
    Committed(CommitReceipt),
    /// The commit was rejected before publication.
    Rejected(CommitError),
    /// The response is uncertain and must be resolved by operation identity.
    Unknown(UnknownCommit),
}

/// An uncertain commit retaining its original operation identity.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct UnknownCommit {
    operation_id: OperationId,
    reason: UnknownReason,
}

impl UnknownCommit {
    /// Creates one uncertain outcome.
    pub(crate) fn new(operation_id: OperationId, reason: UnknownReason) -> Self {
        Self { operation_id, reason }
    }

    /// Returns the original operation identity.
    #[must_use]
    pub fn operation_id(&self) -> &OperationId {
        &self.operation_id
    }

    /// Returns the typed uncertainty reason.
    #[must_use]
    pub fn reason(&self) -> UnknownReason {
        self.reason
    }
}

/// The reason a commit outcome remains uncertain.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum UnknownReason {
    /// The request deadline expired before the outcome was known.
    Deadline,
    /// The request was cancelled before the outcome was known.
    Cancellation,
    /// The response was interrupted after submission.
    ResponseInterrupted,
    /// SQLite committed but could not restore its post-commit synchronous mode.
    PostCommitSync,
    /// The post-publication pointer sync failed.
    PostPublicationPointerSync,
    /// The post-publication containing-directory sync failed.
    PostPublicationDirectorySync,
    /// The post-publication published-ledger sync failed.
    PublishedLedgerSync,
}

/// A durable lookup result for one operation identity.
///
/// There is deliberately no uncertain variant: an unknown response resolves to
/// one of these three durable answers, and a not-yet-decided publication stays
/// [`DurableResolution::Pending`] instead of being reported as a rejection.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum DurableResolution {
    /// The operation committed locally and reached the named durability.
    Committed(CommitReceipt),
    /// A durable rejection record exists for the operation.
    Rejected(CommitError),
    /// The operation is recorded but its publication is not decided yet.
    Pending,
}

/// Internal identity of one open Engine instance.
///
/// The identity is unique per open Engine and is never reused, so a snapshot,
/// scope, or transaction cannot leak across Engine instances. It is separate
/// from the durable owner identity recorded on disk, which detects a location
/// mismatch before `SQLite` opens.
pub(crate) struct EngineIdentity(u128);

impl EngineIdentity {
    /// Mints the next never-reused Engine identity by combining the process,
    /// a monotonic clock reading, and a per-process counter.
    fn next() -> Self {
        static COUNTER: AtomicU64 = AtomicU64::new(0);
        let count = COUNTER.fetch_add(1, Ordering::Relaxed);
        let nanos = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map(|elapsed| u64::try_from(elapsed.as_nanos()).unwrap_or(0));
        let stamp = nanos.unwrap_or(0);
        Self((u128::from(stamp) << 64) | (u128::from(std::process::id()) << 32) | u128::from(count))
    }
}

/// Folds one hybrid stamp into the comparable u64 the provenance column
/// carries: physical millis in the high bits, the logical counter low.
fn fold_stamp(ticks: u64, counter: u64) -> u64 {
    (ticks << 16) | (counter & 0xFFFF)
}

/// Unfolds a stored stamp into its physical and logical parts.
fn unfold_stamp(hlc: u64) -> (u64, u64) {
    (hlc >> 16, hlc & 0xFFFF)
}

/// The locked storage state of one Engine.
pub(crate) struct StorageState {
    pub(crate) port: Box<dyn StoragePort>,
}

type AutoIndexShadowSamples = BTreeMap<
    (String, String, [u8; 32]),
    (u64, Vec<crate::auto_index::LogicalCost>, Vec<crate::auto_index::LogicalCost>),
>;

/// The database-owned Engine.
///
/// ```compile_fail
/// // There is no in-memory durable constructor. This must not compile.
/// let engine = terrabase::engine::Engine::ephemeral();
/// ```
pub struct Engine {
    /// Durable directory retained so related encrypted shard files use the
    /// same database root after a restart.
    pub(crate) location: PathBuf,
    storage_keys: Option<Arc<dyn crate::encryption::DatabaseKeySource>>,
    identity: u128,
    durable_owner: crate::storage::DatabaseOwner,
    /// Whether this location was opened with an explicit production owner.
    /// Local-development opens retain the existing multi-tenant compatibility
    /// path; production locations are strictly database-per-tenant (D-51).
    owner_scope_enforced: bool,
    /// Canonical causal origin for this physical file, retained across reopen.
    replication_lane_origin: crate::origin_lane::OriginLaneId,
    /// The engine's replication clock (D-36): one hybrid stamp per
    /// database. Origin commits tick it; replicated applies witness it.
    /// The deterministic merge compares `(hlc, origin)` totals.
    replication_clock: Mutex<crate::types::ReplicationClockStamp>,
    physical_replication_clock: Arc<dyn ReplicationClock>,
    /// The host-fed clock safety input (D-69). The Engine cannot measure
    /// chrony/NTP itself; `None` is the explicit unknown or stale state.
    clock_offset_seconds: Mutex<Option<f64>>,
    pub(crate) storage: Arc<Mutex<StorageState>>,
    /// One bounded registry shared by the engine, storage, TBEP, probes, and
    /// Prometheus adapter for this process-local engine instance.
    metrics: Arc<MetricsRegistry>,
    /// Live operation reservations, keyed by (tenant, operation). Keying by
    /// tenant keeps the reservation scoped: two tenants may run the same
    /// operation identity concurrently.
    in_flight: Mutex<BTreeSet<(String, String)>>,
    /// The multi-subscriber publication-event registry (W4-CA-3).
    subscriptions: Arc<PublicationRegistry>,
    /// The Engine-owned production index catalog (W4-CA-3): its publication
    /// sink is registered at open, so index maintenance is driven by real
    /// commits without any test-only wiring.
    indexes: Arc<crate::index::IndexCatalog>,
    /// Drop-only: keeps the index catalog's sink registered for this
    /// Engine's lifetime. The leading underscore marks a guard whose `Drop`
    /// is its only effect.
    _index_subscription: SubscriptionGuard,
    /// The bounded in-process audit journal of bulk-by-query runs (GAP-Q10):
    /// one content-free event pair per run, engine-owned, with durable
    /// audit shipping belonging to the Platform side.
    bulk_audit: Arc<crate::query::bulk::BulkAuditJournal>,
    /// Durable D-31 lifecycle journal and control state.  This metadata is
    /// intentionally separate from the planner catalog: after a restart a
    /// lifecycle record never makes an index visible without a fresh `SQLite`
    /// segment at the matching publication boundary.
    pub(crate) auto_index_lifecycle: Mutex<crate::auto_index::LifecycleStore>,
    /// Bounded literal-free execution observations used to rank D-31
    /// candidates. Values are logical pages/rows only; document bodies never
    /// enter this sketch.
    pub(crate) auto_index_observations: Mutex<crate::auto_index::WorkloadSketch>,
    pub(crate) auto_index_shadow_samples: Mutex<AutoIndexShadowSamples>,
    /// The Storage Tier Mover's control state (P4): the configured
    /// policy and the pause gate. The handle is [`Engine::tiers`].
    /// The group-commit mailbox: waiting intents and the leader flag.
    /// Shared through the [`Arc`] so cloned engines form one group.
    origin_commit_barrier: std::sync::RwLock<()>,
    /// One resident handle per admitted shard, bounded by the route catalog limit.
    shard_participants: Mutex<BTreeMap<u64, Arc<Engine>>>,
    /// Target commits share the source database's live authority, without a cycle.
    shard_authority: Mutex<Option<std::sync::Weak<Engine>>>,
    pub(crate) commit_mailbox: Arc<Mutex<CommitMailbox>>,
    /// The arrival signal paired with [`Engine::commit_mailbox`]: every
    /// enqueue notifies it, so a coalescing leader wakes per arrival
    /// instead of polling on a sleep slice. A plain field, not mailbox
    /// state, because the leader hands its guard to the wait and the
    /// condvar must outlive that move.
    commit_arrivals: std::sync::Condvar,
}

/// The group-commit mailbox state.
#[derive(Default)]
pub(crate) struct CommitMailbox {
    /// Waiting intents, in arrival order, each with its answer channel.
    queue: Vec<(storage::CommitIntent, std::sync::mpsc::SyncSender<storage::CommitResult>)>,
    /// When a batch of two or more intents last committed: the engine's
    /// evidence that it is serving a group and the next narrow batch is a
    /// between-waves leader, not a serial caller. A batch of one may take
    /// the pipelined shape only inside this window.
    /// Whether a leader is currently driving batches.
    leader_active: bool,
}

/// Clears the mailbox's leader flag when dropped, so a panicked batch
/// driver cannot strand future committers behind a leader that no longer
/// exists.
struct LeaderGuard(Arc<Mutex<CommitMailbox>>);

impl Drop for LeaderGuard {
    fn drop(&mut self) {
        if let Ok(mut mailbox) = self.0.lock() {
            mailbox.leader_active = false;
        }
    }
}

impl EngineConfig {
    fn validate_storage_keys(&self) -> Result<(), EngineOpenError> {
        match (&self.owner, &self.storage_keys) {
            (Some(owner), Some(keys)) => {
                let lease =
                    keys.current_lease().map_err(|_| EngineOpenError::StorageUnavailable)?;
                let scope = lease.metadata().scope();
                if lease.provider() != crate::encryption::KeyProviderIdentity::OciVault
                    || scope.purpose() != crate::encryption::KeyPurpose::Data
                    || scope.environment() != owner.environment().as_str()
                    || scope.tenant() != owner.tenant()
                    || scope.project() != Some(owner.project())
                    || scope.database() != owner.database()
                {
                    return Err(EngineOpenError::InvalidConfig);
                }
            }
            (Some(_), None) | (None, Some(_)) => return Err(EngineOpenError::InvalidConfig),
            (None, None) => {}
        }
        Ok(())
    }

    fn open_owner(&self) -> Result<crate::storage::DatabaseOwner, EngineOpenError> {
        let location = self.location();
        if location.as_os_str().is_empty() {
            return Err(EngineOpenError::InvalidConfig);
        }
        self.validate_storage_keys()?;
        if self.allow_create {
            crate::storage::load_or_create(
                location,
                self.owner.as_ref(),
                if self.owner.is_some() {
                    crate::storage::OwnershipMode::Production
                } else {
                    crate::storage::OwnershipMode::LocalDevelopment
                },
            )
        } else {
            crate::storage::load_existing(
                location,
                self.owner.as_ref(),
                if self.owner.is_some() {
                    crate::storage::OwnershipMode::Production
                } else {
                    crate::storage::OwnershipMode::LocalDevelopment
                },
            )
        }
        .map_err(|error| match error {
            crate::storage::OwnershipError::Unavailable => EngineOpenError::StorageUnavailable,
            crate::storage::OwnershipError::InvalidEnvironment
            | crate::storage::OwnershipError::InvalidLocation
            | crate::storage::OwnershipError::ForeignLocation
            | crate::storage::OwnershipError::Corrupt
            | crate::storage::OwnershipError::Mismatch
            | crate::storage::OwnershipError::ModeMismatch => EngineOpenError::FormatMismatch,
        })
    }
}

impl Engine {
    /// Opens a durable `SQLite` Engine.
    ///
    /// # Errors
    ///
    /// Returns [`EngineOpenError::InvalidConfig`] for an unusable
    /// configuration, [`EngineOpenError::FormatMismatch`] when the location
    /// is not an owned `SQLite` database directory, and
    /// [`EngineOpenError::StorageUnavailable`] when the location cannot be
    /// opened.
    #[allow(clippy::needless_pass_by_value)]
    pub fn open(config: EngineConfig) -> Result<Self, EngineOpenError> {
        let location = config.location().clone();
        let owner = config.open_owner()?;
        let metrics = Arc::new(MetricsRegistry::new());
        let configured_lane = config.replication_lane_origin.clone().or_else(|| {
            let owner = config.owner.as_ref()?;
            let region = RegionId::try_new(owner.region().as_str()).ok()?;
            crate::origin_lane::OriginLaneId::try_new(
                region,
                u128::from_be_bytes(owner.database().as_bytes()),
            )
            .ok()
        });
        let lane_origin_text = configured_lane
            .as_ref()
            .map(crate::origin_lane::OriginLaneId::as_str);
        let replication_origin = lane_origin_text
            .as_deref()
            .or_else(|| config.replication_origin.as_ref().map(RegionId::as_str));
        // Production owners use the authenticated VFS for every connection.
        let open_storage = if config.allow_create {
            storage::SqliteStorage::open_with_keys(
                &location,
                replication_origin,
                config.storage_keys.clone(),
            )
        } else {
            storage::SqliteStorage::open_existing_with_keys(
                &location,
                replication_origin,
                config.storage_keys.clone(),
            )
        };
        let mut storage: Box<dyn storage::StoragePort> = open_storage
            .map(|engine| Box::new(engine) as Box<dyn storage::StoragePort>)
            .map_err(|_| EngineOpenError::StorageUnavailable)?;
        let replication_lane_origin = configured_lane
            .or_else(|| {
                crate::origin_lane::OriginLaneId::parse(storage.port.local_replication_origin()).ok()
            })
            .or_else(|| {
                let region = RegionId::try_new(owner.region().as_str()).ok()?;
                crate::origin_lane::OriginLaneId::try_new(
                    region,
                    u128::from_be_bytes(owner.database().as_bytes()),
                )
                .ok()
            })
            .ok_or(EngineOpenError::FormatMismatch)?;
        // Sync subscriptions are process-owned. Their in-memory checkpoint
        // handles cannot survive this Engine admission, so retire an old
        // sync lease before serving. The required replication, metering, and
        // backup frontiers were installed by SQLite admission first and keep
        // the retained journal floor safe for reconnect/reset.
        storage
            .retire_consumer_frontier("sync")
            .map_err(|_| EngineOpenError::StorageUnavailable)?;
        if config.owner.is_some()
            && storage.origin_epoch().map_err(|()| EngineOpenError::StorageUnavailable)?.0 == 0
        {
            storage.set_origin_epoch(0, true).map_err(|()| EngineOpenError::StorageUnavailable)?;
        }
        let mut initial_clock =
            ReplicationClockStamp::new(config.replication_clock.now_millis(), 0);
        for tenant in
            storage.committed_tenants().map_err(|()| EngineOpenError::StorageUnavailable)?
        {
            for entry in storage
                .live_origin_prefixes(&tenant)
                .map_err(|()| EngineOpenError::StorageUnavailable)?
            {
                let (ticks, counter) = unfold_stamp(entry.hlc);
                if (ticks, counter) > (initial_clock.ticks(), initial_clock.counter()) {
                    initial_clock = ReplicationClockStamp::new(ticks, counter);
                }
            }
        }
        // Retired sidecar state is outside the SQLite replication/restore
        // contract. Refuse it rather than silently opening an empty catalog.
        if location
            .join("auto-index.lifecycle")
            .try_exists()
            .map_err(|_| EngineOpenError::StorageUnavailable)?
        {
            return Err(EngineOpenError::FormatMismatch);
        }
        let auto_index_lifecycle = crate::auto_index::LifecycleStore::open(storage.as_ref())
            .map_err(|()| EngineOpenError::FormatMismatch)?;
        let subscriptions = Arc::new(PublicationRegistry::new());
        // W4-CA-3: register the Engine-owned index catalog's sink through the
        // production registry so index maintenance observes every publication.
        let indexes = Arc::new(crate::index::IndexCatalog::new());
        // IX-03 rider: the catalog measures against the configured lifecycle
        // thresholds from open, never a post-open default.
        indexes.set_lifecycle_thresholds(config.lifecycle_thresholds());
        let index_subscription = subscriptions.subscribe(indexes.sink());
        Ok(Self {
            location,
            storage_keys: config.storage_keys.clone(),
            identity: EngineIdentity::next().0,
            durable_owner: owner,
            owner_scope_enforced: config.owner.is_some(),
            replication_lane_origin,
            replication_clock: Mutex::new(initial_clock),
            physical_replication_clock: Arc::clone(&config.replication_clock),
            // Keep existing local/development callers usable until the host
            // starts feeding observations. Production wiring must publish a
            // real sample promptly; an explicit unknown/stale sample is
            // fail-closed through `clear_clock_offset_seconds()`.
            clock_offset_seconds: Mutex::new(Some(0.0)),
            storage: Arc::new(Mutex::new(StorageState { port: storage })),
            metrics,
            in_flight: Mutex::new(BTreeSet::new()),
            subscriptions,
            indexes,
            _index_subscription: index_subscription,
            bulk_audit: Arc::new(crate::query::bulk::BulkAuditJournal::new()),
            auto_index_lifecycle: Mutex::new(auto_index_lifecycle),
            auto_index_observations: Mutex::new(crate::auto_index::WorkloadSketch::default()),
            auto_index_shadow_samples: Mutex::new(BTreeMap::new()),
            origin_commit_barrier: std::sync::RwLock::new(()),
            shard_participants: Mutex::new(BTreeMap::new()),
            shard_authority: Mutex::new(None),
            commit_mailbox: Arc::new(Mutex::new(CommitMailbox::default())),
            commit_arrivals: std::sync::Condvar::new(),
        })
    }

    /// Returns the durable database identity bound to this location.
    #[must_use]
    pub fn database_id(&self) -> crate::identity::DatabaseUlid {
        self.durable_owner.database()
    }

    /// Returns the canonical causal origin for this physical file.
    #[must_use]
    pub(crate) fn replication_lane_origin(&self) -> &crate::origin_lane::OriginLaneId {
        &self.replication_lane_origin
    }

    /// Whether logical tenant scopes are restricted to this durable production owner.
    #[must_use]
    pub const fn owner_scope_enforced(&self) -> bool {
        self.owner_scope_enforced
    }

    /// Returns the complete durable owner tuple recovered from this location.
    #[must_use]
    pub fn database_owner(&self) -> &crate::storage::DatabaseOwner {
        &self.durable_owner
    }

    /// Captures the physical database and WAL while commits and checkpoints pause.
    /// The callback receives the owned database directory. It must only read
    /// these files, must finish promptly, and must not call back into this engine.
    /// Keep network I/O outside this callback; capture bytes locally first.
    ///
    /// # Errors
    ///
    /// Returns [`WalCaptureError`] when storage locks are unavailable.
    pub fn with_quiescent_wal<T>(
        &self,
        capture: impl FnOnce(&std::path::Path) -> T,
    ) -> Result<T, WalCaptureError> {
        self.validate_storage_authorization().map_err(|_| WalCaptureError)?;
        let storage = self.storage.lock().map_err(|_| WalCaptureError)?;
        let mut capture = Some(capture);
        let mut result = None;
        storage
            .port
            .with_quiescent_wal(&mut |location| {
                if let Some(capture) = capture.take() {
                    result = Some(capture(location));
                }
            })
            .map_err(|()| WalCaptureError)?;
        result.ok_or(WalCaptureError)
    }

    /// Returns the bounded registry shared with this Engine's storage path.
    #[must_use]
    pub fn metrics(&self) -> Arc<MetricsRegistry> {
        Arc::clone(&self.metrics)
    }

    /// Supplies the host's latest measured offset from its chrony/NTP
    /// reference, in seconds (D-69).
    ///
    /// The Engine deliberately does not inspect chrony or measure the host
    /// clock. This records one finite signed observation. An invalid value is
    /// treated as unknown, just like [`Engine::clear_clock_offset_seconds`],
    /// and therefore refuses new origin writes until the host supplies
    /// another sample. Replicated records already carrying a source HLC are
    /// not blocked by this local admission gate.
    pub fn set_clock_offset_seconds(&self, offset_seconds: f64) {
        let offset_seconds = offset_seconds.is_finite().then_some(offset_seconds);
        self.set_clock_offset_observation(offset_seconds);
    }

    /// Marks the host clock observation unknown or stale (D-69).
    ///
    /// The host owns freshness: when its chrony sample expires or cannot be
    /// read, it must call this method. The Engine has no chrony access and
    /// intentionally cannot infer freshness from its own wall clock.
    pub fn clear_clock_offset_seconds(&self) {
        self.set_clock_offset_observation(None);
    }

    fn set_clock_offset_observation(&self, offset_seconds: Option<f64>) {
        if let Ok(mut observed) = self.clock_offset_seconds.lock() {
            *observed = offset_seconds;
        }
        match offset_seconds {
            Some(offset) => self.metrics.set_clock_offset_seconds(offset),
            None => self.metrics.clear_clock_offset_seconds(),
        }
    }

    /// Returns the last host-fed clock offset, or `None` when the host has
    /// marked its observation unknown/stale.
    #[must_use]
    pub fn clock_offset_seconds(&self) -> Option<f64> {
        self.clock_offset_seconds.lock().ok().and_then(|offset| *offset)
    }

    /// Admits an origin commit only while its host-fed clock observation is
    /// finite and inside the hard D-69 threshold. Refusals increment the
    /// existing counter exactly once at this single production call site.
    fn admit_origin_clock(&self) -> Result<(), ()> {
        let safe = self.clock_offset_seconds.lock().ok().and_then(|offset| *offset).is_some_and(
            |offset| {
                offset.abs() <= crate::observability::CLOCK_OFFSET_WRITE_REFUSAL_THRESHOLD_SECONDS
            },
        );
        if safe {
            Ok(())
        } else {
            self.metrics.record_clock_write_refusal();
            // Keep the frozen CommitError vocabulary: clock admission is a
            // pre-publication validation refusal, with no secret detail.
            Err(())
        }
    }

    /// Drains the group-commit mailbox until empty: one storage batch per
    /// drained group, one answer per waiter.
    ///
    /// Each storage batch completes its requested durability before its
    /// waiters receive their results. A group smaller than the wave width
    /// coalesces until the wave is wide or arrivals go quiet: the mailbox
    /// condvar wakes the leader per arrival, so a dense wave gathers to full
    /// width while sparse traffic commits after one quiet gap.
    ///
    /// Called only by the current leader (see [`Transaction::commit`]);
    /// the `LeaderGuard` the caller holds clears the flag when this
    /// returns or panics.
    fn drive_commit_batches(&self) {
        /// The floor of the wave width to gather toward: the minimum
        /// concurrency worth one coalesce window. The leader raises its
        /// target with every committed wave, so a saturated run gathers to
        /// full width instead of splitting at the floor.
        const GROUP_HARVEST_WIDTH: usize = 8;
        /// A gap of this length with no arrival ends coalescing: arrivals
        /// cluster within the writers' turnaround, and waiting past the gap
        /// would only add latency to the wave.
        const GROUP_QUIET_GAP: std::time::Duration = std::time::Duration::from_micros(500);
        /// The widest coalesce window: how long a narrow group keeps
        /// collecting before it commits anyway. One full writer turnaround:
        /// a wave's writers spread over their lock-contended snapshots, and
        /// cutting the window inside that spread splits the wave — two syncs
        /// where one would have answered everyone.
        const GROUP_HARVEST_WINDOW_MAX: std::time::Duration = std::time::Duration::from_millis(3);
        // The wave width to gather toward. It starts at the harvest width
        // and grows with every committed wave, so a saturated run's next
        // wave coalesces to full width instead of splitting at the floor —
        // the quiet gap still ends coalescing early whenever the traffic
        // is genuinely narrower.
        let mut wave_width = GROUP_HARVEST_WIDTH;
        loop {
            let mut batch = self.take_queued_intents();
            if batch.is_empty() {
                batch = self.take_queued_intents();
                if batch.is_empty() {
                    // Flush while still holding leadership — arrivals
                    // during the flush join the next loop's batch instead
                    // of racing a leaderless queue — then step down
                    // atomically: the flag clear and the empty check share
                    // one lock hold, so nothing strands.
                    if let Ok(mut storage) = self.lock_storage() {
                        storage.port.flush_pending_flips();
                    }
                    if std::env::var_os("TERRABASE_TRACE_BATCHES").is_some() {
                        eprintln!("terrabase leader retired quiet");
                    }
                    let Ok(mut mailbox) = self.commit_mailbox.lock() else {
                        return;
                    };
                    if mailbox.queue.is_empty() {
                        mailbox.leader_active = false;
                        return;
                    }
                    continue;
                }
            }
            batch =
                self.coalesce_wave(batch, wave_width, GROUP_QUIET_GAP, GROUP_HARVEST_WINDOW_MAX);
            let batch_len = batch.len();
            let (intents, senders): (Vec<storage::CommitIntent>, Vec<_>) =
                batch.into_iter().unzip();
            wave_width = wave_width.max(batch_len.saturating_add(1));
            let results = match self.lock_storage() {
                Ok(mut storage) => storage.port.commit_batch(intents, false),
                Err(_) => vec![
                    storage::CommitResult::Rejected(storage::CommitRejection::Storage);
                    batch_len
                ],
            };
            for (sender, result) in senders.into_iter().zip(results) {
                let _ = sender.send(result);
            }
        }
    }

    /// Drives the mailbox as the elected leader.
    ///
    /// The [`LeaderGuard`] clears the flag when the drive returns or panics,
    /// so a panicked driver cannot strand the flag; the waiters' senders
    /// drop with the panicked batch, turning their waits into the honest
    /// uncertain outcome.
    fn drive_as_leader(&self) {
        let _guard = LeaderGuard(Arc::clone(&self.commit_mailbox));
        self.drive_commit_batches();
    }

    /// Waits for this commit's answer, taking leadership if the mailbox is
    /// leaderless with queued work.
    ///
    /// The bounded wait lets a queued writer elect a replacement if the
    /// previous leader stopped before answering it.
    /// The election is one atomic mailbox transition, so two waiters cannot
    /// both lead, and a leader mid-drive holds the flag, making the check a
    /// no-op for every waiter it is about to answer.
    #[allow(clippy::needless_pass_by_value, reason = "the receiver is consumed by the wait loop")]
    fn await_commit_answer(
        &self,
        receiver: std::sync::mpsc::Receiver<storage::CommitResult>,
    ) -> storage::CommitResult {
        loop {
            match receiver.recv_timeout(LEADER_STEAL_AFTER) {
                Ok(result) => return result,
                Err(std::sync::mpsc::RecvTimeoutError::Disconnected) => {
                    return storage::CommitResult::Unknown(
                        storage::UnknownCause::ResponseInterrupted,
                    );
                }
                Err(std::sync::mpsc::RecvTimeoutError::Timeout) => {
                    let steal = match self.commit_mailbox.lock() {
                        Ok(mut mailbox) => {
                            if mailbox.leader_active || mailbox.queue.is_empty() {
                                false
                            } else {
                                mailbox.leader_active = true;
                                true
                            }
                        }
                        Err(_) => false,
                    };
                    if steal {
                        self.drive_as_leader();
                    }
                }
            }
        }
    }

    /// Takes every queued intent, leaving the mailbox's queue empty.
    fn take_queued_intents(
        &self,
    ) -> Vec<(storage::CommitIntent, std::sync::mpsc::SyncSender<storage::CommitResult>)> {
        match self.commit_mailbox.lock() {
            Ok(mut mailbox) => std::mem::take(&mut mailbox.queue),
            Err(_) => Vec::new(),
        }
    }

    /// Coalesces one narrow batch toward the wave width.
    ///
    /// Coalescing ends at the width, at the window's cap, or after one
    /// quiet gap with no arrival — whichever comes first. Every wait holds
    /// the mailbox lock through the condvar, so each arrival wakes the
    /// leader immediately and the lock is never re-acquired while held.
    fn coalesce_wave(
        &self,
        mut batch: Vec<(storage::CommitIntent, std::sync::mpsc::SyncSender<storage::CommitResult>)>,
        width: usize,
        quiet_gap: std::time::Duration,
        window_max: std::time::Duration,
    ) -> Vec<(storage::CommitIntent, std::sync::mpsc::SyncSender<storage::CommitResult>)> {
        let Ok(mut mailbox) = self.commit_mailbox.lock() else {
            return batch;
        };
        let deadline = std::time::Instant::now() + window_max;
        while batch.len() < width && std::time::Instant::now() < deadline {
            if !mailbox.queue.is_empty() {
                batch.append(&mut mailbox.queue);
                continue;
            }
            let gap = quiet_gap.min(deadline.saturating_duration_since(std::time::Instant::now()));
            let Ok(waited) = self.commit_arrivals.wait_timeout(mailbox, gap) else {
                return batch;
            };
            let (guard, timed_out) = waited;
            mailbox = guard;
            if timed_out.timed_out() && mailbox.queue.is_empty() {
                // One quiet gap: the wave that is going to form has formed.
                // Committing now beats idling for stragglers.
                break;
            }
        }
        batch
    }

    /// Returns the never-reused identity of this Engine instance. Tests
    /// and crate-internal callers — the index store's rebuild step among
    /// them — read it to forge scopes and snapshots.
    pub(crate) fn identity(&self) -> u128 {
        self.identity
    }

    /// Mints the tenant scope bound to this Engine.
    ///
    /// The scope names this Engine's own identity, so only a caller
    /// holding this Engine can mint a scope that validates against it.
    /// The replication members (P2) and the server sessions (P4) mint
    /// their scopes here for authenticated tenants; `TenantScope`
    /// itself stays opaque.
    #[must_use]
    pub fn scope_for(&self, tenant: &TenantId) -> TenantScope {
        TenantScope::from_parts(tenant.clone(), self.identity)
    }

    /// Returns the currently published storage sequence — the
    /// replication position a member reports (P5's cluster surface).
    ///
    /// # Errors
    ///
    /// Returns [`EngineError::StorageFailure`] when storage cannot lock.
    pub fn published_sequence(&self) -> Result<StorageSequence, EngineError> {
        let storage = self.lock_storage()?;
        Ok(storage.port.published_state().0)
    }

    /// Returns an immutable tenant-scoped read snapshot.
    ///
    /// # Errors
    ///
    /// Returns [`EngineError::ScopeMismatch`] when the scope names another
    /// Engine and [`EngineError::StorageFailure`] when storage cannot be
    /// reached.
    pub fn read_snapshot(&self, scope: &TenantScope) -> Result<ReadSnapshot, EngineError> {
        self.validate_scope(scope)?;
        let sequence = {
            let storage = self.lock_storage()?;
            storage.port.published_state().0
        };
        let snapshot = ReadSnapshot::new(
            self.identity,
            scope.tenant().clone(),
            sequence,
            IndexGenerationSet::default(),
        );
        Ok(snapshot)
    }

    /// Returns the incarnation bound to this durable database for session
    /// tokens. A restored database receives a new durable database identity,
    /// so tokens from the previous incarnation cannot be replayed against it.
    pub(crate) fn session_epoch(&self) -> crate::session::SessionEpoch {
        let bytes = self.database_id().as_bytes();
        let value = u64::from_be_bytes(bytes[..8].try_into().expect("ULID has 16 bytes"));
        crate::session::SessionEpoch::new(value.max(1)).expect("non-zero database epoch")
    }

    /// Returns the durable applied frontier used by session read guarantees.
    ///
    /// This is queried from the provenance history, rather than inferred from
    /// the global storage sequence: replicated writes can advance different
    /// origins independently, and a client token names those origins.
    pub(crate) fn session_frontier(
        &self,
        scope: &TenantScope,
    ) -> Result<crate::session::SessionFrontier, EngineError> {
        self.validate_scope(scope)?;
        let epoch = self.session_epoch();
        let mut frontier = crate::session::SessionFrontier::new(epoch);
        let storage = self.lock_storage()?;
        for entry in storage
            .port
            .live_origin_prefixes(scope.tenant())
            .map_err(|()| EngineError::StorageFailure)?
        {
            let origin = crate::origin_lane::OriginLaneId::parse(&entry.origin)
                .map_err(|_| EngineError::InvalidDocument)?;
            if entry.origin_sequence == 0 {
                continue;
            }
            frontier
                .advance(origin, entry.origin_sequence)
                .map_err(|_| EngineError::InvalidDocument)?;
            let (ticks, counter) = unfold_stamp(entry.hlc);
            frontier.observe_write(ReplicationClockStamp::new(ticks, counter));
        }
        Ok(frontier)
    }

    /// Durably records one trusted, tenant-bound usage event.
    ///
    /// The command accepts exactly one fact and always reaches synced local
    /// durability before returning. Replaying an identical scoped fact
    /// identity returns its original sequence with `already_recorded = true`;
    /// reusing the identity for different content fails closed.
    ///
    /// # Errors
    ///
    /// Returns [`UsageCommandError::ScopeMismatch`] for a scope from another
    /// Engine, the typed storage availability and integrity errors otherwise.
    #[allow(
        clippy::needless_pass_by_value,
        reason = "public command API preserves owned request semantics"
    )]
    pub fn record_usage(
        &self,
        scope: &TenantScope,
        request: UsageRecordRequest,
    ) -> Result<UsageRecordReceipt, UsageCommandError> {
        self.validate_scope(scope).map_err(|_| UsageCommandError::ScopeMismatch)?;
        if !self.usage_owner_matches(request.project_id().as_str(), request.database_id().as_str())
        {
            return Err(UsageCommandError::InvalidRequest);
        }
        let fact = request
            .fact_for(scope.tenant().clone())
            .map_err(|_| UsageCommandError::InvalidRequest)?;
        let (durable, already_recorded) = {
            let mut storage =
                self.storage.lock().map_err(|_| UsageCommandError::StorageUnavailable)?;
            storage.port.record_usage(scope.tenant(), fact).map_err(map_usage_storage_error)?
        };
        Ok(UsageRecordReceipt::new(
            durable.fact().identity().id().clone(),
            StorageSequence::new(durable.sequence()),
            durable.replica_epoch(),
            already_recorded,
        ))
    }

    /// Returns one stable-snapshot page of authoritative usage facts.
    ///
    /// The first page fixes the location's current durable high-water.
    /// Continuations bind tenant, filter, page size, snapshot, and the last
    /// emitted `(sequence, minting rule, fact id)` position. Facts appended
    /// after that snapshot never enter later pages.
    ///
    /// # Errors
    ///
    /// Returns [`UsageCommandError::InvalidRequest`] for a damaged,
    /// cross-tenant, or filter-mismatched continuation and typed storage
    /// availability or integrity failures otherwise.
    #[allow(
        clippy::needless_pass_by_value,
        reason = "public command API preserves owned request semantics"
    )]
    pub fn usage_page(
        &self,
        scope: &TenantScope,
        request: UsagePageRequest,
    ) -> Result<UsagePage, UsageCommandError> {
        // Scope refusal is an authorization failure, not an export/storage
        // failure. Return before the wrapper's failure counter so a forged
        // tenant cannot alter observable metrics.
        self.validate_scope(scope).map_err(|_| UsageCommandError::ScopeMismatch)?;
        let result = self.usage_page_unobserved(scope, &request);
        match &result {
            Ok(page) => {
                let _ = self.metrics.advance_usage_export_frontier(page.snapshot_sequence().get());
            }
            Err(_) => self.metrics.record_usage_export_failure(),
        }
        result
    }

    fn usage_page_unobserved(
        &self,
        scope: &TenantScope,
        request: &UsagePageRequest,
    ) -> Result<UsagePage, UsageCommandError> {
        self.validate_scope(scope).map_err(|_| UsageCommandError::ScopeMismatch)?;
        let decoded = request.continuation().map(decode_usage_continuation).transpose()?;
        if let Some(state) = &decoded
            && (&state.tenant != scope.tenant()
                || state.window != request.window()
                || state.page_size != request.page_size())
        {
            return Err(UsageCommandError::InvalidRequest);
        }
        let storage = self.storage.lock().map_err(|_| UsageCommandError::StorageUnavailable)?;
        let current = storage.port.published_state().0;
        let snapshot = decoded.as_ref().map_or(current, |state| state.snapshot);
        if snapshot > current {
            return Err(UsageCommandError::InvalidRequest);
        }
        let after = decoded.as_ref().map(|state| &state.after);
        let fetch = request.page_size().saturating_add(1);
        let mut facts = storage
            .port
            .usage_facts_page(scope.tenant(), snapshot, after, request.window(), fetch)
            .map_err(map_usage_storage_error)?;
        drop(storage);
        let has_more = facts.len() > request.page_size() as usize;
        if has_more {
            facts.truncate(request.page_size() as usize);
        }
        let continuation = if has_more {
            let last = facts.last().ok_or(UsageCommandError::CorruptStorage)?;
            Some(encode_usage_continuation(
                scope.tenant(),
                snapshot,
                request,
                &UsageFactPosition::of(last),
            ))
        } else {
            None
        };
        Ok(UsagePage::new(facts, snapshot, continuation))
    }

    /// Returns one database's metering fact (E-9, D-16/D-42): `(db, seq_hi,
    /// writes, bytes)` flushed once per group-commit wave and reconciled
    /// from the journal on restart. Tenant-isolated: `db` must name the
    /// caller's own tenant (D-51: tenant = database); anything else answers
    /// `None`. This is the read seam the usage-export surface pages through.
    #[must_use]
    pub fn metering_fact(&self, scope: &TenantScope, db: &str) -> Option<DbUsageFact> {
        if db != scope.tenant().as_str() {
            return None;
        }
        self.validate_scope(scope).ok()?;
        self.storage.lock().ok()?.port.metering_fact(db)
    }

    /// Returns one cursor page of the caller's metering facts in `db` order
    /// (E-9): the tenant-isolated view of the shared storage cursor, so a
    /// database exports exactly its own fact. The walk follows the storage
    /// cursor until the caller's own database, which appears at most once;
    /// everything after it belongs to other databases, so the page is
    /// complete with no next cursor. The cursor itself stays opaque.
    #[must_use]
    pub fn metering_facts_page(
        &self,
        scope: &TenantScope,
        after: Option<&str>,
        limit: u32,
    ) -> (Vec<DbUsageFact>, Option<String>) {
        if limit == 0 {
            return (Vec::new(), None);
        }
        if self.validate_scope(scope).is_err() {
            return (Vec::new(), None);
        }
        let Ok(storage) = self.storage.lock() else {
            return (Vec::new(), None);
        };
        let db = scope.tenant().as_str();
        let mut cursor = after.map(str::to_owned);
        loop {
            let (facts, next) =
                storage.port.metering_facts_page(cursor.as_deref(), MAX_USAGE_PAGE_SIZE);
            for fact in facts {
                if fact.db() == db {
                    return (vec![fact], None);
                }
            }
            let Some(next) = next else {
                return (Vec::new(), None);
            };
            cursor = Some(next);
        }
    }

    /// Returns the caller's live logical bytes for the D-16 storage
    /// dimension: newest version per key, tombstones excluded.
    #[must_use]
    pub fn metering_live_bytes(&self, scope: &TenantScope) -> u64 {
        if self.validate_scope(scope).is_err() {
            return 0;
        }
        self.storage
            .lock()
            .ok()
            .map_or(0, |storage| storage.port.metering_live_bytes(scope.tenant().as_str()))
    }

    /// Returns the caller's served-read count for abuse guards and cost
    /// attribution (E-9, D-16/D-44: reads are metered, never billed).
    #[must_use]
    pub fn metering_read_count(&self, scope: &TenantScope) -> u64 {
        if self.validate_scope(scope).is_err() {
            return 0;
        }
        self.storage
            .lock()
            .ok()
            .map_or(0, |storage| storage.port.read_count(scope.tenant().as_str()))
    }

    /// Begins an Engine-borrowing transaction.
    ///
    /// The snapshot's Engine identity, the requested scope, the snapshot
    /// tenant against that scope, and the operation identity are validated
    /// before a transaction exists, and none of these checks touches storage
    /// publication state.
    ///
    /// # Errors
    ///
    /// Returns [`EngineError::SnapshotEngineMismatch`] for a snapshot from
    /// another Engine, [`EngineError::ScopeMismatch`] for a scope from another
    /// Engine, [`EngineError::SnapshotTenantMismatch`] when the snapshot and
    /// scope name different tenants, and [`EngineError::OperationIdReuse`]
    /// when the operation identity is already in use by another live
    /// transaction of this Engine.
    pub fn begin<'engine>(
        &'engine self,
        scope: &TenantScope,
        snapshot: ReadSnapshot,
        operation: OperationId,
    ) -> Result<Transaction<'engine>, EngineError> {
        if snapshot.engine_identity() != self.identity {
            return Err(EngineError::SnapshotEngineMismatch);
        }
        self.validate_scope(scope)?;
        if snapshot.tenant() != scope.tenant() {
            return Err(EngineError::SnapshotTenantMismatch);
        }
        let origin_epoch =
            self.lock_storage()?.port.origin_epoch().map_err(|()| EngineError::StorageFailure)?.0;
        let source_authority = self
            .shard_authority
            .lock()
            .map_err(|_| EngineError::StorageFailure)?
            .as_ref()
            .map(|source| {
                let source = source.upgrade().ok_or(EngineError::StorageFailure)?;
                let epoch = source.origin_epoch()?.0;
                Ok::<_, EngineError>((source, epoch))
            })
            .transpose()?;
        let reserved = self
            .in_flight
            .lock()
            .map_err(|_| EngineError::StorageFailure)?
            .insert(reservation_key(scope, &operation));
        if !reserved {
            return Err(EngineError::OperationIdReuse { operation_id: operation });
        }
        let snapshot_sequence = snapshot.sequence();
        Ok(Transaction {
            engine: self,
            scope: scope.clone(),
            snapshot,
            snapshot_sequence,
            operation,
            usage_admission: None,
            budget_allocation: None,
            internal_maintenance: false,
            shard_copy: false,
            expected_origin_epoch: origin_epoch,
            source_authority,
            expected_uniform_context: None,
            merge_conflict: None,
            replication_fingerprint: None,
            replication_publication: None,
            shard_admission: None,
            writes: Vec::new(),
            metadata: Vec::new(),
            schema_envelopes: Vec::new(),
        })
    }

    /// Returns a query store borrowed from this Engine.
    ///
    /// The store is bound to this Engine's lifetime; query behavior is
    /// implemented in the query module.
    pub fn queries(&self) -> QueryStore<'_> {
        QueryStore::from_engine(self)
    }

    /// Returns an index store borrowed from this Engine (GAP-Q18).
    ///
    /// The store is bound to this Engine's lifetime and its engine-owned
    /// index catalog; it is the typed surface of the index lifecycle
    /// operations, per-state staleness, the bounded wait-for-non-stale,
    /// and the engine-owned statistics. This acquisition path is the only
    /// production way to obtain an
    /// [`IndexStore`](crate::index::operations::IndexStore), mirroring
    /// [`Engine::queries`] and [`Engine::families`].
    pub fn indexes(&self) -> crate::index::operations::IndexStore<'_> {
        crate::index::operations::IndexStore::from_engine(self)
    }

    /// Returns a family store borrowed from this Engine.
    ///
    /// The store is bound to this Engine's lifetime; family behavior is
    /// implemented in the families module.
    pub fn families(&self) -> FamilyStore<'_> {
        FamilyStore::from_engine(self)
    }

    /// Returns a stream store borrowed from this Engine.
    ///
    /// The store is bound to this Engine's lifetime; streaming behavior is
    /// implemented in the streaming module. This acquisition path is the only
    /// production way to obtain a [`StreamStore`],
    /// mirroring [`Engine::queries`] and [`Engine::families`].
    pub fn streams(&self) -> StreamStore<'_> {
        StreamStore::from_engine(self)
    }

    /// Opens one bulk-insert ingestion stream bound to this Engine and
    /// scope (GAP-D10).
    ///
    /// The scope is validated before the stream exists, exactly like
    /// [`Engine::begin`]. The stream consumes any document source one
    /// document at a time, publishes each bounded batch as one atomic
    /// transaction, refuses per-document validation failures without
    /// aborting, and returns a receipt whose counts reconcile exactly — a
    /// mid-stream stop never claims a document it did not commit. The
    /// decision record is `docs/engine/gapd10-bulk-ingest.md`.
    ///
    /// # Errors
    ///
    /// Returns [`EngineError::ScopeMismatch`] when the scope names another
    /// Engine and [`EngineError::StorageFailure`] when storage cannot be
    /// reached to validate the scope.
    pub fn ingest_stream(
        &self,
        scope: &TenantScope,
        request: crate::engine::ingest::BulkIngestRequest,
    ) -> Result<crate::engine::ingest::IngestStream<'_>, EngineError> {
        self.read_snapshot(scope)?;
        Ok(crate::engine::ingest::IngestStream::new(self, scope.clone(), request))
    }

    /// Resolves an uncertain operation using its durable identity.
    ///
    /// # Errors
    ///
    /// Returns [`EngineError::ScopeMismatch`] for a scope from another Engine
    /// and [`EngineError::StorageFailure`] when storage cannot be reached.
    pub fn resolve_commit(
        &self,
        scope: &TenantScope,
        operation: OperationId,
    ) -> Result<DurableResolution, EngineError> {
        self.validate_scope(scope)?;
        let resolution = {
            let storage = self.lock_storage()?;
            storage.port.resolve_operation(scope.tenant(), &operation)
        };
        Ok(match resolution {
            Some(storage::OperationResolution::Committed { sequence, durability }) => {
                DurableResolution::Committed(CommitReceipt::new(operation, sequence, durability))
            }
            Some(storage::OperationResolution::Rejected(reason)) => {
                DurableResolution::Rejected(reason.to_commit_error())
            }
            None => DurableResolution::Pending,
        })
    }

    /// Applies one replicated record from a peer region (D-35/D-36): an
    /// unconditional put or tombstone stamped `kind='replicated'` with its
    /// causal metadata, committed at [`LocalDurability::Written`] and bound
    /// to an operation identity derived from the source origin, commit,
    /// collection, and document.
    ///
    /// The identity binding is the idempotency: a re-applied record — a
    /// re-pull after a crash, a duplicate delivery — replays the recorded
    /// publication's receipt instead of double-writing, reported through
    /// [`AppliedReplication::replayed`]. Replicated writes carry no
    /// expected-version clause and no collection-schema enforcement: the
    /// origin region validated the write, and the tailer owns
    /// last-writer ordering.
    ///
    /// # Errors
    ///
    /// Returns [`EngineError::InvalidDocument`] when the record's body is
    /// outside the structural limits, and the transaction path's typed
    /// failures otherwise.
    /// The wall-clock milliseconds of the replication clock's epoch.
    pub(crate) fn now_millis() -> u64 {
        SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map_or(0, |elapsed| u64::try_from(elapsed.as_millis()).unwrap_or(0))
    }

    /// Ticks the replication clock for one origin commit: physical
    /// millis advance, ties increment the logical counter.
    fn tick_replication_clock(&self) -> u64 {
        let mut clock = self.replication_clock.lock().expect("the clock is not poisoned");
        let ticks = self.physical_replication_clock.now_millis();
        let stamp = if ticks > clock.ticks() {
            *clock = ReplicationClockStamp::new(ticks, 0);
            ticks
        } else {
            let counter = clock.counter().saturating_add(1);
            *clock = ReplicationClockStamp::new(clock.ticks(), counter);
            clock.ticks()
        };
        let _ = stamp;
        fold_stamp(clock.ticks(), clock.counter())
    }

    /// Witnesses a remote stamp: the clock advances to cover it without
    /// ever moving backwards.
    fn witness_replication_clock(&self, hlc: u64) {
        let (ticks, counter) = unfold_stamp(hlc);
        let mut clock = self.replication_clock.lock().expect("the clock is not poisoned");
        if ticks > clock.ticks() || (ticks == clock.ticks() && counter > clock.counter()) {
            *clock = ReplicationClockStamp::new(ticks, counter);
        }
    }

    /// Declares one collection's typed merge schema (D-36): the fields
    /// whose merges use per-field kernels. Schema-free collections keep
    /// the remove-wins map default and store no kernels.
    ///
    /// # Errors
    ///
    /// Returns [`EngineError::InvalidDocument`] when the schema is invalid,
    /// [`EngineError::ScopeMismatch`] for a foreign scope, and
    /// [`EngineError::StorageFailure`] when durable registration fails.
    pub fn declare_merge_schema(
        &self,
        scope: &TenantScope,
        collection: &CollectionName,
        schema: &crate::storage::MergeSchema,
    ) -> Result<(), EngineError> {
        self.validate_scope(scope)?;
        let mut storage = self.lock_storage()?;
        storage.port.declare_merge_schema(collection, schema).map_err(|error| match error {
            crate::storage::MergeSchemaError::Storage => EngineError::StorageFailure,
            _ => EngineError::InvalidDocument,
        })
    }

    /// Returns one collection's declared merge schema, if any.
    ///
    /// # Errors
    ///
    /// Returns [`EngineError::ScopeMismatch`] for a foreign scope.
    pub fn merge_schema(
        &self,
        scope: &TenantScope,
        collection: &CollectionName,
    ) -> Result<Option<crate::storage::MergeSchema>, EngineError> {
        self.validate_scope(scope)?;
        let storage = self.lock_storage().map_err(|_| EngineError::StorageFailure)?;
        Ok(storage.port.merge_schema(collection))
    }

    /// Returns one document's recorded conflict losers, newest first
    /// (D-36): the deterministic history the conflict viewer reads.
    ///
    /// # Errors
    ///
    /// Returns [`EngineError::ScopeMismatch`] for a foreign scope.
    pub fn merge_conflicts_for(
        &self,
        scope: &TenantScope,
        collection: &CollectionName,
        id: &DocumentId,
        limit: u32,
    ) -> Result<Vec<crate::storage::MergeConflictEntry>, EngineError> {
        self.validate_scope(scope)?;
        let storage = self.lock_storage().map_err(|_| EngineError::StorageFailure)?;
        Ok(storage.port.merge_conflicts_for(scope.tenant(), collection, id, limit))
    }

    /// Returns one document's stored per-field kernels in field order
    /// (D-36). Schema-free collections serve none.
    ///
    /// # Errors
    ///
    /// Returns [`EngineError::ScopeMismatch`] for a foreign scope.
    pub fn merge_kernels_for(
        &self,
        scope: &TenantScope,
        collection: &CollectionName,
        id: &DocumentId,
    ) -> Result<Vec<crate::storage::MergeKernelEntry>, EngineError> {
        self.validate_scope(scope)?;
        let storage = self.lock_storage().map_err(|_| EngineError::StorageFailure)?;
        Ok(storage.port.merge_kernels_for(scope.tenant(), collection, id))
    }

    /// Records one deterministic conflict loser. Per-field metadata belongs
    /// to the document transaction and must not be overwritten here.
    #[allow(clippy::too_many_arguments, reason = "the conflict record's fixed dimensions")]
    fn record_merge_conflict(
        &self,
        scope: &TenantScope,
        record: &ReplicatedRecord,
        snapshot: &ReadSnapshot,
        winner_hlc: u64,
        winner_origin: &str,
        loser_commit: &str,
        loser_hlc: u64,
        loser_origin: &str,
        loser_deleted: bool,
        winner_commit: &str,
        policy: crate::storage::MergePolicy,
        explanation: &str,
    ) -> Result<AppliedReplication, EngineError> {
        let conflict = crate::storage::MergeConflictEntry {
            loser_commit_ulid: loser_commit.to_owned(),
            loser_hlc,
            loser_origin: loser_origin.to_owned(),
            loser_deleted,
            winner_commit_ulid: winner_commit.to_owned(),
            winner_hlc,
            winner_origin: winner_origin.to_owned(),
            policy: policy.as_str().to_owned(),
            explanation: explanation.to_owned(),
        };
        self.record_superseded_replication(scope, record, snapshot, Some(conflict))
    }

    fn record_superseded_replication(
        &self,
        scope: &TenantScope,
        record: &ReplicatedRecord,
        snapshot: &ReadSnapshot,
        conflict: Option<storage::MergeConflictEntry>,
    ) -> Result<AppliedReplication, EngineError> {
        let operation = replication_operation_id(record)?;
        let intent = storage::SupersededReplication {
            tenant: scope.tenant().clone(),
            operation: operation.clone(),
            fingerprint: replication_fingerprint(record)?,
            snapshot_sequence: snapshot.sequence(),
            collection: record.collection.clone(),
            id: record.id.clone(),
            conflict,
        };
        let replayed =
            self.lock_storage()?.port.record_superseded_replication(intent).map_err(|reason| {
                match reason {
                    storage::CommitRejection::Conflict => EngineError::Conflict,
                    storage::CommitRejection::OperationIdReuse => {
                        EngineError::OperationIdReuse { operation_id: operation }
                    }
                    _ => EngineError::StorageFailure,
                }
            })?;
        let mut applied = AppliedReplication::lost_merge();
        applied.replayed = replayed;
        Ok(applied)
    }

    /// Applies one replicated record with the deterministic merge
    /// (D-36): a clean apply writes bytes; a true concurrent conflict of
    /// two live objects merges per field — the remove-wins map default
    /// plus the `(hlc, origin)` tiebreak, or the collection's declared
    /// typed kernels — and records the loser with its deterministic
    /// explanation. Tombstones fall back to the document-level
    /// `(hlc, origin)` order with remove-wins on ties. The clock witnesses
    /// the remote stamp either way. A superseded record (the current row
    /// wins) publishes nothing; every region reaches the same verdict, so
    /// convergence needs no coordination.
    ///
    /// # Errors
    ///
    /// Returns the engine's typed failure when the apply cannot run.
    #[allow(clippy::too_many_lines, reason = "the merge's read/compute/publish/record stages")]
    pub fn apply_replicated(
        &self,
        scope: &TenantScope,
        record: &ReplicatedRecord,
    ) -> Result<AppliedReplication, EngineError> {
        // Validate before witnessing the remote HLC or taking any storage
        // lock: a foreign tenant must not mutate clock, bytes, sequence, or
        // replication idempotency state.
        self.validate_scope(scope)?;
        if record.origin.is_empty()
            || record.origin_sequence == 0
            || record.origin_sequence > i64::MAX as u64
        {
            return Err(EngineError::InvalidDocument);
        }
        let incoming_context =
            merge::decode_context(&record.context).ok_or(EngineError::InvalidDocument)?;
        let operation = replication_operation_id(record)?;
        let fingerprint = replication_fingerprint(record)?;
        let incoming_version = merge::DocumentVersion::new(
            incoming_context,
            merge::DocumentDot::new(record.origin.clone(), record.origin_sequence),
        );
        if !incoming_version.is_well_formed() {
            return Err(EngineError::InvalidDocument);
        }
        // Source transactions can contain multiple document mutations. Replay
        // binds the origin, source commit, collection, and document together;
        // the source commit alone would discard every later document.
        {
            let storage = self.lock_storage().map_err(|_| EngineError::StorageFailure)?;
            let bound = storage
                .port
                .replication_fingerprint(scope.tenant(), &operation)
                .map_err(|()| EngineError::StorageFailure)?;
            if bound.is_some_and(|expected| expected != fingerprint) {
                return Err(EngineError::OperationIdReuse { operation_id: operation });
            }
            if storage
                .port
                .replication_was_superseded(scope.tenant(), &operation)
                .map_err(|()| EngineError::StorageFailure)?
            {
                if bound.is_none() {
                    return Err(EngineError::StorageFailure);
                }
                let mut applied = AppliedReplication::lost_merge();
                applied.replayed = true;
                return Ok(applied);
            }
            if let Some(resolution) = storage.port.resolve_operation(scope.tenant(), &operation) {
                if bound.is_none() {
                    return Err(EngineError::StorageFailure);
                }
                match resolution {
                    storage::OperationResolution::Committed { sequence, durability } => {
                        let receipt = CommitReceipt::new(operation, sequence, durability);
                        return Ok(AppliedReplication::new(receipt, true));
                    }
                    storage::OperationResolution::Rejected(reason) => {
                        return Err(match reason.to_commit_error() {
                            CommitError::Conflict => EngineError::Conflict,
                            CommitError::OperationIdReuse => {
                                EngineError::OperationIdReuse { operation_id: operation }
                            }
                            _ => EngineError::StorageFailure,
                        });
                    }
                }
            }
        }
        self.witness_replication_clock(record.hlc);
        // The current merge input, read under one short lock.
        let (current_state, schema, current_context, merge_snapshot) = {
            let storage = self.lock_storage().map_err(|_| EngineError::StorageFailure)?;
            let state =
                storage.port.document_merge_state(scope.tenant(), &record.collection, &record.id);
            let schema = storage.port.merge_schema(&record.collection);
            let context =
                storage.port.document_context(scope.tenant(), &record.collection, &record.id);
            let snapshot = ReadSnapshot::new(
                self.identity,
                scope.tenant().clone(),
                storage.port.published_state().0,
                IndexGenerationSet::default(),
            );
            (state, schema, context, snapshot)
        };
        let Some(current) = current_state else {
            // A clean apply writes bytes (D-36): no conflict, no loser.
            return self.apply_replicated_clean(scope, record, &operation, merge_snapshot);
        };
        let current_context = merge::decode_context(current_context.as_deref().unwrap_or_default())
            .ok_or(EngineError::StorageFailure)?;
        let current_version = merge::DocumentVersion::new(
            current_context.clone(),
            merge::DocumentDot::new(current.origin.clone(), current.origin_sequence),
        );
        if !current_version.is_well_formed() {
            return Err(EngineError::StorageFailure);
        }
        // Causal precedence is decided from durable contexts before HLC
        // conflict ordering. A later event may cleanly overwrite an older
        // body without parsing either side; a dominated delivery is an
        // idempotent no-op.
        if incoming_version.context().contains(current_version.dot()) {
            return self.apply_replicated_clean(scope, record, &operation, merge_snapshot);
        }
        if current_version.context().contains(incoming_version.dot()) {
            return self.record_superseded_replication(scope, record, &merge_snapshot, None);
        }
        // Tombstones merge at document level (D-36): remove-wins on ties.
        if record.deleted || current.deleted {
            return self.apply_replicated_tombstone(
                scope,
                record,
                &operation,
                &current,
                merge_snapshot,
            );
        }
        // Both live: field-level merge when both bodies are objects.
        let (Some(DocumentValue::Object(current_fields)), DocumentValue::Object(incoming_fields)) =
            (current.body.as_ref(), &record.body)
        else {
            // Non-object bodies fall back to the document-level order.
            return self.apply_replicated_tombstone(
                scope,
                record,
                &operation,
                &current,
                merge_snapshot,
            );
        };
        // Identical bytes need no new row, unless this is a duplicate
        // delivery of the same dot: then the operation ledger must replay
        // the original receipt so the tailer counts it as a skip.
        if current.body.as_ref() == Some(&record.body) {
            if current.commit_ulid == record.commit_ulid {
                return self.apply_replicated_clean(scope, record, &operation, merge_snapshot);
            }
            return self.record_superseded_replication(scope, record, &merge_snapshot, None);
        }
        let (merged, policy, explanation) = merge::merge_object_fields(
            current_fields,
            current.hlc,
            &current.origin,
            &current.commit_ulid,
            incoming_fields,
            record.hlc,
            &record.origin,
            &record.commit_ulid,
            schema.as_ref(),
        );
        let merged_value = DocumentValue::Object(merged.clone());
        let current_value = DocumentValue::Object(current_fields.clone());
        if merged_value == current_value {
            // The current row wins every field: publish nothing, but the
            // loser joins history with its deterministic explanation.
            let (winner_hlc, winner_origin) =
                if merge::compare_stamps(current.hlc, &current.origin, record.hlc, &record.origin)
                    == std::cmp::Ordering::Greater
                {
                    (current.hlc, current.origin.clone())
                } else {
                    (record.hlc, record.origin.clone())
                };
            // Record the loser without changing the surviving document metadata.
            return self.record_merge_conflict(
                scope,
                record,
                &merge_snapshot,
                winner_hlc,
                &winner_origin,
                &record.commit_ulid,
                record.hlc,
                &record.origin,
                record.deleted,
                &current.commit_ulid,
                policy,
                &explanation,
            );
        }
        // The merge produced new bytes: publish them under the incoming
        // identity at the covering stamp, so both regions compute the same
        // row without shipping merged bytes (replicated rows never re-ship).
        let (cover_hlc, cover_origin, cover_origin_sequence) =
            if merge::compare_stamps(current.hlc, &current.origin, record.hlc, &record.origin)
                == std::cmp::Ordering::Greater
            {
                (current.hlc, current.origin.clone(), current.origin_sequence)
            } else {
                (record.hlc, record.origin.clone(), record.origin_sequence)
            };
        let merged_record = ReplicatedRecord {
            collection: record.collection.clone(),
            id: record.id.clone(),
            body: merged_value,
            deleted: false,
            commit_ulid: record.commit_ulid.clone(),
            hlc: cover_hlc,
            origin: cover_origin.clone(),
            origin_sequence: cover_origin_sequence,
            context: merge::encode_context(&current_version.joined_context(&incoming_version)),
        };
        let conflict = storage::MergeConflictEntry {
            loser_commit_ulid: current.commit_ulid.clone(),
            loser_hlc: current.hlc,
            loser_origin: current.origin.clone(),
            loser_deleted: current.deleted,
            winner_commit_ulid: record.commit_ulid.clone(),
            winner_hlc: cover_hlc,
            winner_origin: cover_origin,
            policy: policy.as_str().to_owned(),
            explanation,
        };
        self.apply_replicated_with_conflict(
            scope,
            &merged_record,
            fingerprint,
            &operation,
            Some(conflict),
            merge_snapshot,
        )
    }

    /// Applies one replicated record with no prior row, or one merged row
    /// whose bytes must publish, checked against the merge snapshot and
    /// bound to the per-record replay identity.
    fn apply_replicated_clean(
        &self,
        scope: &TenantScope,
        record: &ReplicatedRecord,
        operation: &OperationId,
        merge_snapshot: ReadSnapshot,
    ) -> Result<AppliedReplication, EngineError> {
        self.apply_replicated_with_conflict(
            scope,
            record,
            replication_fingerprint(record)?,
            operation,
            None,
            merge_snapshot,
        )
    }

    fn apply_replicated_with_conflict(
        &self,
        scope: &TenantScope,
        record: &ReplicatedRecord,
        fingerprint: [u8; 32],
        operation: &OperationId,
        conflict: Option<storage::MergeConflictEntry>,
        snapshot: ReadSnapshot,
    ) -> Result<AppliedReplication, EngineError> {
        let snapshot_sequence = snapshot.sequence();
        let mut transaction = self.begin(scope, snapshot, operation.clone())?;
        transaction.stage_replicated(record)?;
        transaction.replication_fingerprint = Some(fingerprint);
        transaction.merge_conflict =
            conflict.map(|entry| (record.collection.clone(), record.id.clone(), entry));
        match transaction.commit(LocalDurability::Written) {
            CommitOutcome::Committed(receipt) => {
                let replayed = receipt.sequence() <= snapshot_sequence;
                Ok(AppliedReplication::new(receipt, replayed))
            }
            CommitOutcome::Rejected(error) => Err(match error {
                CommitError::Conflict => EngineError::Conflict,
                CommitError::OperationIdReuse => {
                    EngineError::OperationIdReuse { operation_id: operation.clone() }
                }
                _ => EngineError::StorageFailure,
            }),
            CommitOutcome::Unknown(_) => Err(EngineError::StorageFailure),
        }
    }

    /// Applies one replicated record against a tombstone, or any
    /// non-object bodies, with the document-level `(hlc, origin)` order
    /// and remove-wins on ties. Losers join history with a deterministic
    /// explanation.
    fn apply_replicated_tombstone(
        &self,
        scope: &TenantScope,
        record: &ReplicatedRecord,
        operation: &OperationId,
        current: &crate::storage::DocumentMergeState,
        merge_snapshot: ReadSnapshot,
    ) -> Result<AppliedReplication, EngineError> {
        let incoming = (record.hlc, record.origin.as_str());
        let current_stamp = (current.hlc, current.origin.as_str());
        let current_wins = match incoming.cmp(&current_stamp) {
            std::cmp::Ordering::Greater => false,
            std::cmp::Ordering::Less => true,
            std::cmp::Ordering::Equal => {
                // Remove-wins on ties: a tombstone beats a live value.
                !record.deleted && current.deleted
            }
        };
        if current_wins {
            let policy = if incoming == current_stamp {
                crate::storage::MergePolicy::RemoveWins
            } else {
                crate::storage::MergePolicy::HlcTiebreak
            };
            let explanation = format!(
                "merge {} (hlc {}, origin '{}') vs {} (hlc {}, origin '{}'): policy {}; winner (hlc {}, origin '{}')",
                current.commit_ulid,
                current.hlc,
                current.origin,
                record.commit_ulid,
                record.hlc,
                record.origin,
                policy.as_str(),
                current.hlc,
                current.origin,
            );
            return self.record_merge_conflict(
                scope,
                record,
                &merge_snapshot,
                current.hlc,
                &current.origin.clone(),
                &record.commit_ulid,
                record.hlc,
                &record.origin,
                record.deleted,
                &current.commit_ulid.clone(),
                policy,
                &explanation,
            );
        }
        let policy = if incoming == current_stamp {
            crate::storage::MergePolicy::RemoveWins
        } else {
            crate::storage::MergePolicy::HlcTiebreak
        };
        let explanation = format!(
            "merge {} (hlc {}, origin '{}') vs {} (hlc {}, origin '{}'): policy {}; winner (hlc {}, origin '{}')",
            current.commit_ulid,
            current.hlc,
            current.origin,
            record.commit_ulid,
            record.hlc,
            record.origin,
            policy.as_str(),
            record.hlc,
            record.origin,
        );
        let conflict = storage::MergeConflictEntry {
            loser_commit_ulid: current.commit_ulid.clone(),
            loser_hlc: current.hlc,
            loser_origin: current.origin.clone(),
            loser_deleted: current.deleted,
            winner_commit_ulid: record.commit_ulid.clone(),
            winner_hlc: record.hlc,
            winner_origin: record.origin.clone(),
            policy: policy.as_str().to_owned(),
            explanation,
        };
        self.apply_replicated_with_conflict(
            scope,
            record,
            replication_fingerprint(record)?,
            operation,
            Some(conflict),
            merge_snapshot,
        )
    }

    /// Returns one document's current replication stamp: the newest row's
    /// `hlc`, `origin`, and tombstone flag — the deterministic merge's
    /// comparison input. `None` when the key has no row.
    ///
    /// This is the read-only stamp seam consumed by TBEP's ADR 0044
    /// range-based reconciliation path. It exposes the same `(hlc, origin,
    /// deleted)` inputs that [`Engine::apply_replicated`] compares, so the
    /// row fingerprints and merge order cover the same causal state.
    /// Read-only; the S-02 query/storage lane is otherwise untouched.
    ///
    /// # Errors
    ///
    /// Returns [`EngineError::ScopeMismatch`] when the scope names another
    /// Engine and [`EngineError::StorageFailure`] when storage cannot be
    /// reached.
    pub fn document_replication_stamp(
        &self,
        scope: &TenantScope,
        collection: &CollectionName,
        id: &DocumentId,
    ) -> Result<Option<(u64, String, bool)>, EngineError> {
        self.validate_scope(scope)?;
        let storage = self.lock_storage()?;
        Ok(storage.port.document_provenance_at(scope.tenant(), collection, id))
    }

    /// Advances only the independently verified native backup journal floor.
    pub(crate) fn advance_backup_frontier(
        &self,
        sequence: StorageSequence,
    ) -> Result<(), EngineError> {
        let mut storage = self.storage.lock().map_err(|_| EngineError::StorageFailure)?;
        storage
            .port
            .advance_backup_frontier(sequence.get())
            .map_err(|()| EngineError::StorageFailure)
    }

    /// Advances one required retention consumer after its durable
    /// acknowledgement. Unknown consumers are rejected so a caller cannot
    /// accidentally create an unaccounted pruning floor.
    pub fn advance_retention_frontier(&self, name: &str, sequence: u64) -> Result<(), EngineError> {
        let mut storage = self.storage.lock().map_err(|_| EngineError::StorageFailure)?;
        storage
            .port
            .advance_consumer_frontier(name, sequence)
            .map_err(|()| EngineError::StorageFailure)
    }

    /// Registers an optional live retention consumer at its durable start
    /// boundary. The sync stream uses this while at least one reader exists.
    pub(crate) fn register_retention_consumer(
        &self,
        name: &str,
        sequence: StorageSequence,
    ) -> Result<(), EngineError> {
        let mut storage = self.storage.lock().map_err(|_| EngineError::StorageFailure)?;
        storage
            .port
            .register_consumer_frontier(name, sequence.get())
            .map_err(|()| EngineError::StorageFailure)
    }

    /// Retires an optional consumer when its owning subsystem has no live
    /// readers, so an absent consumer does not pin history forever.
    pub(crate) fn retire_retention_consumer(&self, name: &str) -> Result<(), EngineError> {
        let mut storage = self.storage.lock().map_err(|_| EngineError::StorageFailure)?;
        storage.port.retire_consumer_frontier(name).map_err(|()| EngineError::StorageFailure)
    }

    /// Runs one storage retention sweep: prune publication history every
    /// consumer frontier has passed, then fold the WAL back into the
    /// database file. Expired TTL keys sweep first (bounded), so one
    /// maintenance cadence reclaims both history and expiries.
    /// Crate-private: the maintenance loop owns the cadence.
    ///
    /// # Errors
    ///
    /// Returns [`EngineError::StorageFailure`] when storage fails the sweep.
    pub(crate) fn run_retention_sweep(&self) -> Result<(), EngineError> {
        self.sweep_expired_ttl(1024)?;
        let mut storage = self.lock_storage()?;
        storage.port.run_retention_sweep().map_err(|()| EngineError::StorageFailure)
    }

    /// Closes the Engine and its durable storage.
    ///
    /// # Errors
    ///
    /// Returns [`EngineShutdownError::StorageFailure`] when the final flush
    /// of durable storage fails. [`EngineShutdownError::AlreadyClosed`] is
    /// unreachable through this consuming path and exists for the frozen
    /// contract only.
    pub fn shutdown(self) -> Result<(), EngineShutdownError> {
        // The shutdown lifecycle: prune publication history below every
        // consumer frontier (a no-op while no consumer is registered), fold
        // the WAL back into the database file, then flush.
        let _ = self.run_retention_sweep();
        let mut storage = self.storage.lock().map_err(|_| EngineShutdownError::StorageFailure)?;
        storage.port.flush().map_err(|()| EngineShutdownError::StorageFailure)
    }

    /// Returns the admitted renewable artifact key source for this database.
    /// The source exposes scoped leases, never raw key material.
    ///
    /// # Errors
    /// Returns a scope mismatch or storage failure when the admitted lease
    /// cannot be validated. Unencrypted local engines have no artifact source.
    pub fn artifact_key_source(
        &self,
        scope: &TenantScope,
    ) -> Result<Arc<dyn crate::encryption::DatabaseKeySource>, EngineError> {
        self.validate_scope(scope)?;
        self.storage_keys.clone().ok_or(EngineError::StorageFailure)
    }

    /// Revalidates authorization even when `SQLite` satisfies a read from cache.
    ///
    /// # Errors
    /// Returns storage failure after lease revocation or failed renewal.
    pub fn validate_storage_authorization(&self) -> Result<(), EngineError> {
        if let Some(keys) = &self.storage_keys {
            let lease = keys.current_lease().map_err(|_| EngineError::StorageFailure)?;
            if lease.is_expired() {
                return Err(EngineError::StorageFailure);
            }
        }
        Ok(())
    }

    fn validate_scope(&self, scope: &TenantScope) -> Result<(), EngineError> {
        self.validate_storage_authorization()?;
        if scope.engine_identity() != self.identity {
            return Err(EngineError::ScopeMismatch);
        }
        self.validate_tenant(scope.tenant())
    }

    /// Validates the logical tenant against this location's durable owner.
    ///
    /// Production opens are one database per tenant (D-51), so a scope that
    /// merely forges this Engine's process identity is still foreign. The
    /// existing local-development ownership mode intentionally remains the
    /// compatibility path for in-process multi-tenant fixtures and
    /// control-plane tests; it is never used by `Member::open_production`.
    fn validate_tenant(&self, tenant: &TenantId) -> Result<(), EngineError> {
        if self.owner_scope_enforced && tenant.as_str() != self.durable_owner.tenant().to_text() {
            return Err(EngineError::ScopeMismatch);
        }
        Ok(())
    }

    /// Checks the non-tenant usage dimensions against the durable owner.
    ///
    /// The serving boundary already authenticates these fields, but the
    /// Engine repeats the database binding so a direct trusted caller cannot
    /// mint a fact attributed to another Project or database.
    fn usage_owner_matches(&self, project: &str, database: &str) -> bool {
        !self.owner_scope_enforced
            || (project == self.durable_owner.project().to_text()
                && database == self.durable_owner.database().to_text())
    }

    /// Returns every document of one tenant as the supplied read snapshot
    /// observed them, in canonical key order.
    ///
    /// This is the crate-private snapshot view the query module executes
    /// against. It performs the same validation as [`Engine::begin`] — the
    /// snapshot's Engine identity, the scope's Engine identity, and the
    /// snapshot tenant against the scope — before touching storage.
    ///
    /// # Errors
    ///
    /// Returns [`EngineError::SnapshotEngineMismatch`] for a snapshot from
    /// another Engine, [`EngineError::ScopeMismatch`] for a scope from another
    /// Engine, [`EngineError::SnapshotTenantMismatch`] when the snapshot and
    /// scope name different tenants, and
    /// [`EngineError::StorageFailure`] when storage cannot be reached.
    #[allow(dead_code, reason = "consumed by the query module in W3-2")]
    pub fn snapshot_documents(
        &self,
        scope: &TenantScope,
        snapshot: &ReadSnapshot,
    ) -> Result<Vec<Document>, EngineError> {
        if snapshot.engine_identity() != self.identity {
            return Err(EngineError::SnapshotEngineMismatch);
        }
        self.validate_scope(scope)?;
        if snapshot.tenant() != scope.tenant() {
            return Err(EngineError::SnapshotTenantMismatch);
        }
        let documents = {
            let storage = self.lock_storage()?;
            storage.port.documents_at(snapshot.sequence(), scope.tenant())
        };
        Ok(documents)
    }

    /// Returns the exact canonical body blob for one document at a pinned
    /// snapshot. The bytes are opaque to the Engine; callers that already
    /// speak the canonical response format can splice them directly without
    /// constructing a Document or decoding a JSON value.
    ///
    /// # Errors
    ///
    /// Returns `ScopeMismatch` for a foreign scope or `StorageFailure` when the
    /// raw read cannot be served.
    pub fn raw_document_body_in_collection_at(
        &self,
        scope: &TenantScope,
        snapshot: StorageSequence,
        collection: &CollectionName,
        id: &DocumentId,
    ) -> Result<Option<Vec<u8>>, EngineError> {
        self.validate_scope(scope)?;
        let storage = self.lock_storage()?;
        let connection =
            storage.port.open_sqlite_read_connection().map_err(|()| EngineError::StorageFailure)?;
        storage.port.record_read(scope.tenant().as_str());
        let now = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map_or(i64::MAX, |elapsed| i64::try_from(elapsed.as_millis()).unwrap_or(i64::MAX));
        let row = connection
            .query_row(
                "SELECT deleted, expires_at, body FROM documents
                 WHERE tenant = ?1 AND collection = ?2 AND id = ?3 AND commit_seq <= ?4
                 ORDER BY commit_seq DESC LIMIT 1",
                rusqlite::params![
                    scope.tenant().as_str(),
                    collection.as_str(),
                    id.as_str(),
                    snapshot.get().cast_signed()
                ],
                |row| {
                    Ok((
                        row.get::<_, i64>(0)?,
                        row.get::<_, Option<i64>>(1)?,
                        row.get::<_, Vec<u8>>(2)?,
                    ))
                },
            )
            .optional()
            .map_err(|_| EngineError::StorageFailure)?;
        Ok(row.and_then(|(deleted, expires_at, body)| {
            let family = collection.as_str() == "terrabase.families";
            (deleted == 0 && (family || expires_at.is_none_or(|at| at > now))).then_some(body)
        }))
    }

    /// Lists the distinct collection names a tenant's documents occupy at
    /// the read snapshot, in canonical order.
    ///
    /// # Errors
    ///
    /// Returns the validation and storage failures of
    /// [`Engine::snapshot_documents`].
    pub fn collections_at(
        &self,
        scope: &TenantScope,
        snapshot: &ReadSnapshot,
    ) -> Result<Vec<CollectionName>, EngineError> {
        let documents = self.snapshot_documents(scope, snapshot)?;
        let mut names: Vec<CollectionName> = Vec::new();
        for document in &documents {
            let collection = document.collection().clone();
            if !names.contains(&collection) {
                names.push(collection);
            }
        }
        names.sort();
        names.dedup();
        Ok(names)
    }

    /// Returns every document of one tenant's collection as the supplied read
    /// snapshot observed them, in canonical key order.
    ///
    /// The collection-scoped form of [`Engine::snapshot_documents`], with the
    /// same validation before any storage access: a query whose source is one
    /// collection reads no other collection's documents, so the storage seam
    /// stops at the collection's key range instead of materializing the rest
    /// of the tenant. Crate-only: the query executor is the consumer.
    ///
    /// # Errors
    ///
    /// Returns the validation and storage failures of
    /// [`Engine::snapshot_documents`].
    #[allow(dead_code, reason = "compatibility collection-wide read seam")]
    pub fn snapshot_documents_in_collection(
        &self,
        scope: &TenantScope,
        snapshot: &ReadSnapshot,
        collection: &CollectionName,
    ) -> Result<Vec<Document>, EngineError> {
        if snapshot.engine_identity() != self.identity {
            return Err(EngineError::SnapshotEngineMismatch);
        }
        self.validate_scope(scope)?;
        if snapshot.tenant() != scope.tenant() {
            return Err(EngineError::SnapshotTenantMismatch);
        }
        let documents = {
            let storage = self.lock_storage()?;
            storage.port.documents_in_collection_at(snapshot.sequence(), scope.tenant(), collection)
        };
        Ok(documents)
    }

    /// Returns the documents the supplied read snapshot observed at the named
    /// exact keys of one tenant's collection, in canonical key order of the
    /// requested identifiers.
    ///
    /// The batched keyed-candidate fetch of the index-narrowed query path:
    /// each identifier resolves by its exact `(tenant, collection,
    /// identifier)` key — the same resolution
    /// [`Transaction::read_in_collection`] applies — so an identifier
    /// reused in another collection is never misread here, and an identifier
    /// absent at the snapshot contributes nothing. Validation matches
    /// [`Engine::snapshot_documents`] before any storage access. Crate-only:
    /// the query executor is the consumer.
    ///
    /// # Errors
    ///
    /// Returns the validation and storage failures of
    /// [`Engine::snapshot_documents`].
    pub(crate) fn snapshot_documents_by_ids(
        &self,
        scope: &TenantScope,
        snapshot: &ReadSnapshot,
        collection: &CollectionName,
        ids: &[DocumentId],
    ) -> Result<Vec<Document>, EngineError> {
        if snapshot.engine_identity() != self.identity {
            return Err(EngineError::SnapshotEngineMismatch);
        }
        self.validate_scope(scope)?;
        if snapshot.tenant() != scope.tenant() {
            return Err(EngineError::SnapshotTenantMismatch);
        }
        let documents = {
            let storage = self.lock_storage()?;
            storage.port.read_documents_in_collection_at(
                snapshot.sequence(),
                scope.tenant(),
                collection,
                ids,
            )
        };
        Ok(documents)
    }

    /// Returns one bounded window of the tenant's documents as the supplied
    /// read snapshot observed them, in canonical key order resuming at the
    /// boundary named by `after` (W4-CA-7).
    ///
    /// This is the bounded-memory read path for streaming: one call serves
    /// at most `limit` documents, so a snapshot-bound stream source never
    /// materializes a whole tenant per batch. The validation matches
    /// [`Engine::snapshot_documents`] before any storage access; the resume
    /// rule and its at-least-once caveat are documented there on the storage
    /// port.
    ///
    /// # Errors
    ///
    /// Returns [`EngineError::SnapshotEngineMismatch`] for a snapshot from
    /// another Engine, [`EngineError::ScopeMismatch`] for a scope from another
    /// Engine, [`EngineError::SnapshotTenantMismatch`] when the snapshot and
    /// scope name different tenants, and
    /// [`EngineError::StorageFailure`] when storage cannot be reached.
    #[allow(dead_code, reason = "consumed by the W4-STREAM packet")]
    #[allow(clippy::needless_pass_by_value)] // the frozen contract passes the cursor by value
    pub(crate) fn snapshot_documents_from(
        &self,
        scope: &TenantScope,
        snapshot: &ReadSnapshot,
        after: Option<DocumentId>,
        limit: u32,
    ) -> Result<Vec<Document>, EngineError> {
        if snapshot.engine_identity() != self.identity {
            return Err(EngineError::SnapshotEngineMismatch);
        }
        self.validate_scope(scope)?;
        if snapshot.tenant() != scope.tenant() {
            return Err(EngineError::SnapshotTenantMismatch);
        }
        let documents = {
            let storage = self.lock_storage()?;
            storage.port.documents_at_from(
                snapshot.sequence(),
                scope.tenant(),
                after.as_ref(),
                limit,
            )
        };
        Ok(documents)
    }

    /// Returns one bounded window of a collection at the supplied read
    /// snapshot, resuming strictly after the optional identifier cursor.
    /// Collection-local paging keeps query memory proportional to one page,
    /// even when the tenant owns many other collections.
    ///
    /// # Errors
    ///
    /// Returns [`EngineError::SnapshotPageLimitExceeded`] above the bounded
    /// page ceiling, the same snapshot validation errors as
    /// [`Engine::snapshot_documents`], or [`EngineError::StorageFailure`]
    /// when a persisted candidate fails integrity checks.
    #[allow(dead_code, reason = "consumed by collection query execution")]
    #[allow(clippy::needless_pass_by_value)]
    pub(crate) fn snapshot_documents_in_collection_from(
        &self,
        scope: &TenantScope,
        snapshot: &ReadSnapshot,
        collection: &CollectionName,
        after: Option<DocumentId>,
        limit: u32,
    ) -> Result<Vec<Document>, EngineError> {
        if limit > MAX_SNAPSHOT_RECORD_PAGE {
            return Err(EngineError::SnapshotPageLimitExceeded);
        }
        if snapshot.engine_identity() != self.identity {
            return Err(EngineError::SnapshotEngineMismatch);
        }
        self.validate_scope(scope)?;
        if snapshot.tenant() != scope.tenant() {
            return Err(EngineError::SnapshotTenantMismatch);
        }
        let documents = {
            let storage = self.lock_storage()?;
            storage.port.documents_in_collection_at_from(
                snapshot.sequence(),
                scope.tenant(),
                collection,
                after.as_ref(),
                limit,
            )
        }
        .map_err(|_| EngineError::StorageFailure)?;
        Ok(documents)
    }

    /// Returns one bounded window of a collection at the supplied read
    /// snapshot with the condition conjunction pushed onto the extracted
    /// index columns (D-42), or `Ok(None)` when the storage cannot push the
    /// filter and the caller must take the unfiltered page path.
    ///
    /// The pushed filter is a sound narrowing, never an exact one: the
    /// caller still applies its full predicate over the returned page. The
    /// validation matches [`Engine::snapshot_documents`]; crate-only, the
    /// query executor is the consumer.
    ///
    /// # Errors
    ///
    /// Returns [`EngineError::SnapshotPageLimitExceeded`] above the bounded
    /// page ceiling, the same snapshot validation errors as
    /// [`Engine::snapshot_documents`], or [`EngineError::StorageFailure`]
    /// when a persisted candidate fails integrity checks.
    #[allow(clippy::needless_pass_by_value)]
    pub(crate) fn snapshot_documents_in_collection_filtered(
        &self,
        scope: &TenantScope,
        snapshot: &ReadSnapshot,
        collection: &CollectionName,
        after: Option<DocumentId>,
        limit: u32,
        filter: &crate::storage::ScanFilter,
    ) -> Result<Option<Vec<Document>>, EngineError> {
        if limit > MAX_SNAPSHOT_RECORD_PAGE {
            return Err(EngineError::SnapshotPageLimitExceeded);
        }
        if snapshot.engine_identity() != self.identity {
            return Err(EngineError::SnapshotEngineMismatch);
        }
        self.validate_scope(scope)?;
        if snapshot.tenant() != scope.tenant() {
            return Err(EngineError::SnapshotTenantMismatch);
        }
        let documents = {
            let storage = self.lock_storage()?;
            storage.port.collection_scan_filtered(
                snapshot.sequence(),
                scope.tenant(),
                collection,
                after.as_ref(),
                limit,
                filter,
            )
        };
        match documents {
            Some(Ok(documents)) => Ok(Some(documents)),
            Some(Err(_)) => Err(EngineError::StorageFailure),
            None => Ok(None),
        }
    }

    /// Registers one extracted index column descriptor for a collection
    /// (D-31/D-42): the field gains stored columns written by the Rust
    /// write path from the next commit on, existing rows are backfilled
    /// inline while the collection is inside the cost cap, and ordinary
    /// indexes cover the columns. Idempotent for an identical descriptor.
    ///
    /// This is the durable half of index creation; the query path's
    /// first-query policy, the auto-index audit, and the S-03 collections
    /// route drive it.
    ///
    /// # Errors
    ///
    /// Returns [`IndexRegistrationError::BudgetExceeded`] past the
    /// database's extracted-column budget, and
    /// [`IndexRegistrationError::Storage`] when durable registration fails.
    pub fn register_index_field(
        &self,
        collection: &CollectionName,
        field: &str,
    ) -> Result<crate::storage::DescriptorRegistration, IndexRegistrationError> {
        let mut storage = self.lock_storage().map_err(|_| IndexRegistrationError::Storage)?;
        storage.port.register_index_descriptor(collection, field).map_err(|error| match error {
            crate::storage::IndexDescriptorError::ColumnBudgetExceeded => {
                IndexRegistrationError::BudgetExceeded
            }
            crate::storage::IndexDescriptorError::Storage => IndexRegistrationError::Storage,
        })
    }

    /// Removes one collection's extracted index descriptor. Shared columns and
    /// indexes remain while another collection uses the field. Removing the
    /// last descriptor releases its physical columns and database column budget.
    /// A later query can register the field again through normal auto-indexing.
    ///
    /// # Errors
    ///
    /// Refuses invalid fields, hidden-index dependencies, and durable failures.
    pub fn remove_index_field(
        &self,
        collection: &CollectionName,
        field: &str,
    ) -> Result<bool, crate::storage::IndexDescriptorRemovalError> {
        let mut storage = self
            .lock_storage()
            .map_err(|_| crate::storage::IndexDescriptorRemovalError::Storage)?;
        storage.port.remove_index_descriptor(collection, field)
    }

    /// Lists the database's durable index descriptors with their activity
    /// and remaining governed-backfill work (D-31): the collections route's
    /// index listing and the audit's coverage input.
    ///
    /// # Errors
    ///
    /// Returns [`EngineError::StorageFailure`] when the durable listing
    /// fails.
    pub fn index_descriptors(&self) -> Result<Vec<crate::storage::DescriptorListing>, EngineError> {
        let storage = self.lock_storage()?;
        storage.port.index_descriptors().map_err(|()| EngineError::StorageFailure)
    }

    /// Reports one collection field's durable descriptor state without
    /// registering anything: `Some(true)` pushes, `Some(false)` is building,
    /// `None` is absent. Crate-only: the query path's first-query policy is
    /// the only reader.
    pub(crate) fn pushdown_field_state(
        &self,
        collection: &CollectionName,
        field: &str,
    ) -> Option<bool> {
        let storage = self.lock_storage().ok()?;
        storage.port.index_descriptor_state(collection, field)
    }

    /// Registers one full-text descriptor for a collection (S-02 slice D,
    /// D-39): the field gains FTS5 rows written by the Rust write path
    /// from the next commit on, backfilled inline inside the cost cap and
    /// governed beyond it. Idempotent for an identical descriptor.
    ///
    /// This is the durable half of text-index creation; the query path's
    /// first-query policy and the S-03 collections route drive it.
    ///
    /// # Errors
    ///
    /// Returns [`IndexRegistrationError::Storage`] when durable
    /// registration fails. Text has no column budget.
    pub fn register_fts_field(
        &self,
        collection: &CollectionName,
        field: &str,
    ) -> Result<crate::storage::DescriptorRegistration, IndexRegistrationError> {
        let mut storage = self.lock_storage().map_err(|_| IndexRegistrationError::Storage)?;
        storage.port.register_fts_descriptor(collection, field).map_err(|error| match error {
            crate::storage::IndexDescriptorError::ColumnBudgetExceeded => {
                IndexRegistrationError::BudgetExceeded
            }
            crate::storage::IndexDescriptorError::Storage => IndexRegistrationError::Storage,
        })
    }

    /// Reports one collection field's text-descriptor state without
    /// registering anything: `Some(true)` narrows, `Some(false)` is
    /// building, `None` is absent. Crate-only: the query path's
    /// first-query policy is the only reader.
    pub(crate) fn fts_field_state(&self, collection: &CollectionName, field: &str) -> Option<bool> {
        let storage = self.lock_storage().ok()?;
        storage.port.fts_descriptor_state(collection, field)
    }

    /// Returns the FTS5 candidate set of one search — the matching
    /// document ids with relevance scores, higher first — or `Ok(None)`
    /// when the index cannot narrow the read and the caller must take the
    /// exact scan. Crate-only: the query executor is the consumer.
    ///
    /// # Errors
    ///
    /// Returns [`EngineError::SnapshotPageLimitExceeded`] above the bounded
    /// page ceiling, the same snapshot validation errors as
    /// [`Engine::snapshot_documents`], or [`EngineError::StorageFailure`]
    /// when a persisted candidate fails integrity checks.
    pub(crate) fn snapshot_fts_match(
        &self,
        scope: &TenantScope,
        snapshot: &ReadSnapshot,
        collection: &CollectionName,
        field: &str,
        query: &str,
        match_any: bool,
    ) -> Result<Option<Vec<(String, f64)>>, EngineError> {
        if snapshot.engine_identity() != self.identity {
            return Err(EngineError::SnapshotEngineMismatch);
        }
        self.validate_scope(scope)?;
        if snapshot.tenant() != scope.tenant() {
            return Err(EngineError::SnapshotTenantMismatch);
        }
        let storage = self.lock_storage()?;
        match storage.port.fts_match(
            snapshot.sequence(),
            scope.tenant(),
            collection,
            field,
            query,
            match_any,
        ) {
            Some(Ok(candidates)) => Ok(Some(candidates)),
            Some(Err(_)) => Err(EngineError::StorageFailure),
            None => Ok(None),
        }
    }

    /// Sweeps at most `batch` expired TTL keys into origin tombstones
    /// (S-02 slice D): one transaction per tenant, committed
    /// [`LocalDurability::Written`], returning the tombstones written.
    /// Read-time filtering already hides expired keys, so the sweep is
    /// reclamation and FTS hygiene, never a correctness gate — a lost wave
    /// is simply rewritten by the next sweep.
    ///
    /// # Errors
    ///
    /// Returns [`EngineError::StorageFailure`] when storage cannot be
    /// reached or a sweep commit is refused for a durable reason.
    pub fn sweep_expired_ttl(&self, batch: u32) -> Result<u64, EngineError> {
        let now_millis = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map_or(i64::MAX, |since| i64::try_from(since.as_millis()).unwrap_or(i64::MAX));
        let keys = {
            let storage = self.lock_storage()?;
            storage
                .port
                .expired_ttl_keys(now_millis, batch.max(1))
                .map_err(|()| EngineError::StorageFailure)?
        };
        if keys.is_empty() {
            return Ok(0);
        }
        let mut by_tenant: std::collections::BTreeMap<String, Vec<crate::storage::ExpiredTtlKey>> =
            std::collections::BTreeMap::new();
        for key in keys {
            by_tenant.entry(key.tenant.as_str().to_owned()).or_default().push(key);
        }
        let mut tombstones = 0_u64;
        for (_, group) in by_tenant {
            let tenant = group[0].tenant.clone();
            let scope = self.scope_for(&tenant);
            let snapshot = self.read_snapshot(&scope)?;
            let operation = OperationId::try_new(&crate::identity::generate().to_string())
                .map_err(|_| EngineError::StorageFailure)?;
            let mut transaction = self.begin(&scope, snapshot, operation)?;
            transaction.internal_maintenance = true;
            for key in &group {
                transaction.delete_expired_ttl(
                    &key.collection,
                    key.id.clone(),
                    ExpectedVersion::Exact(key.version),
                    crate::storage::TtlGuard {
                        revision: key.revision.clone(),
                        expires_at: key.expires_at,
                    },
                );
            }
            match transaction.commit(LocalDurability::Written) {
                CommitOutcome::Committed(_) => {
                    tombstones = tombstones.saturating_add(group.len() as u64);
                }
                CommitOutcome::Rejected(_) | CommitOutcome::Unknown(_) => {
                    return Err(EngineError::StorageFailure);
                }
            }
        }
        Ok(tombstones)
    }

    /// Audits observed query shapes against durable descriptor coverage
    /// (D-31): every `(collection, field)` shape the optimizer's selection
    /// records observed for this tenant, joined with the descriptor table,
    /// plus every registered descriptor even when nothing observed it (an
    /// idle descriptor is a retirement candidate). Entries sort by
    /// `(collection, field)`.
    ///
    /// This is the durable half's read for the S-03 collections route: the
    /// route registers missing shapes through
    /// [`Engine::register_index_field`] and pumps large ones through
    /// [`Engine::maintain_index_backfills`].
    ///
    /// # Errors
    ///
    /// Returns [`EngineError::StorageFailure`] when the durable listing
    /// fails.
    pub fn auto_index_audit(
        &self,
        tenant: &TenantId,
    ) -> Result<Vec<AutoIndexRecommendation>, EngineError> {
        self.validate_tenant(tenant)?;
        let listings = self.index_descriptors()?;
        let mut observed: BTreeMap<(String, String), u64> = BTreeMap::new();
        for record in self.indexes.selection_records(tenant) {
            for field in record.query_fields() {
                *observed
                    .entry((record.collection().to_owned(), field.name().to_owned()))
                    .or_default() += 1;
            }
        }
        for listing in &listings {
            observed.entry((listing.collection.clone(), listing.field.clone())).or_default();
        }
        let mut entries = Vec::with_capacity(observed.len());
        for ((collection, field), count) in observed {
            let coverage = listings
                .iter()
                .find(|listing| listing.collection == collection && listing.field == field)
                .map_or(DescriptorCoverage::Absent, |listing| {
                    if listing.active {
                        DescriptorCoverage::Active
                    } else {
                        DescriptorCoverage::Building {
                            backfill_remaining: listing.backfill_remaining,
                        }
                    }
                });
            entries.push(AutoIndexRecommendation {
                collection,
                field,
                observed_queries: count,
                coverage,
            });
        }
        Ok(entries)
    }

    /// Returns the durable D-31 lifecycle generation for one tenant.
    ///
    /// # Errors
    ///
    /// Returns [`EngineError::ScopeMismatch`] when the tenant is foreign to
    /// this Engine's durable owner, and [`EngineError::StorageFailure`] when
    /// the lifecycle state cannot be locked.
    pub fn auto_index_lifecycle_generation(&self, tenant: &TenantId) -> Result<u64, EngineError> {
        self.validate_tenant(tenant)?;
        self.auto_index_lifecycle
            .lock()
            .map(|state| state.generation(tenant.as_str()))
            .map_err(|_| EngineError::StorageFailure)
    }

    /// Returns a bounded snapshot of committed lifecycle records only.
    ///
    /// # Errors
    /// Returns [`EngineError::ScopeMismatch`] for a foreign tenant and
    /// [`EngineError::StorageFailure`] when the lifecycle state cannot be read.
    pub fn auto_index_lifecycle_snapshot(
        &self,
        tenant: &TenantId,
        collection: Option<&str>,
        limit: usize,
    ) -> Result<crate::auto_index::LifecycleSnapshot, EngineError> {
        self.validate_tenant(tenant)?;
        self.auto_index_lifecycle
            .lock()
            .map(|state| state.snapshot(tenant.as_str(), collection, limit))
            .map_err(|_| EngineError::StorageFailure)
    }

    /// Computes bounded recommendations from real query selection records and
    /// measured execution observations.  Candidate identities are derived
    /// from the canonical field order, so retries and Regions agree without
    /// carrying query literals.
    ///
    /// # Errors
    ///
    /// Returns [`EngineError::ScopeMismatch`] when the tenant is foreign to
    /// this Engine's durable owner, and [`EngineError::StorageFailure`] when
    /// the lifecycle state, the observation sketch, or the collection names
    /// cannot be read.
    #[allow(
        clippy::cast_precision_loss,
        reason = "ranking scores only; bounded counts and collection sizes stay far below 2^53"
    )]
    pub fn auto_index_recommendations(
        &self,
        tenant: &TenantId,
        collection: Option<&str>,
        limit: usize,
    ) -> Result<crate::auto_index::RecommendationSnapshot, EngineError> {
        self.validate_tenant(tenant)?;
        let forbidden =
            self.auto_index_lifecycle.lock().map_err(|_| EngineError::StorageFailure)?;
        let generation = forbidden.generation(tenant.as_str());
        let mut grouped: BTreeMap<(String, Vec<String>), (u64, f64)> = BTreeMap::new();
        if let Ok(observations) = self.auto_index_observations.lock() {
            for observation in observations.observations() {
                if observation.tenant != tenant.as_str()
                    || collection.is_some_and(|name| observation.template.collection != name)
                    || (observation.template.predicates.is_empty()
                        && observation.template.ordering.is_empty())
                {
                    continue;
                }
                let mut fields = observation
                    .template
                    .predicates
                    .iter()
                    .map(|predicate| predicate.field.clone())
                    .chain(observation.template.ordering.iter().cloned())
                    .collect::<Vec<_>>();
                let mut seen = std::collections::BTreeSet::new();
                fields.retain(|field| seen.insert(field.clone()));
                let row =
                    grouped.entry((observation.template.collection.clone(), fields)).or_default();
                row.0 = row.0.saturating_add(1);
                row.1 += observation.cost.total() as f64;
            }
        }
        for record in self.indexes.selection_records(tenant) {
            if collection.is_some_and(|name| record.collection() != name) {
                continue;
            }
            let fields: Vec<String> =
                record.query_fields().iter().map(|field| field.name().to_owned()).collect();
            if fields.is_empty() {
                continue;
            }
            let score = record.candidates().len() as f64 + 1.0;
            let row = grouped.entry((record.collection().to_owned(), fields)).or_default();
            row.0 = row.0.saturating_add(1);
            row.1 += score;
        }
        let mut recommendations = Vec::new();
        for ((collection_name, fields), (frequency, score)) in grouped {
            let definition_fields = fields
                .iter()
                .filter_map(|field| {
                    crate::query::optimizer::IndexField::try_new(
                        field,
                        crate::query::optimizer::IndexCapabilities::FULL,
                    )
                    .ok()
                })
                .collect();
            let Ok(definition) = crate::query::optimizer::IndexDefinition::try_new(
                crate::types::CollectionName::try_new(&collection_name)
                    .map_err(|_| EngineError::StorageFailure)?,
                definition_fields,
                crate::query::optimizer::IndexKind::Auto,
            ) else {
                continue;
            };
            let candidate_id = crate::query::optimizer::auto_index_name(&definition);
            if forbidden.is_forbidden(tenant.as_str(), &candidate_id) {
                continue;
            }
            let collection_key = crate::types::CollectionName::try_new(&collection_name)
                .map_err(|_| EngineError::StorageFailure)?;
            let needs_descriptor = fields
                .iter()
                .any(|field| self.pushdown_field_state(&collection_key, field).is_none());
            recommendations.push(crate::auto_index::LifecycleRecommendation {
                candidate_id,
                tenant: tenant.as_str().to_owned(),
                collection: collection_name,
                maintenance_cost: fields.len() as u64,
                fields,
                score: score + frequency as f64,
                generation,
                needs_descriptor,
            });
        }
        recommendations.sort_by(|left, right| {
            right
                .score
                .total_cmp(&left.score)
                .then_with(|| left.candidate_id.cmp(&right.candidate_id))
        });
        let truncated = recommendations.len() > limit;
        recommendations.truncate(limit);
        Ok(crate::auto_index::RecommendationSnapshot { generation, truncated, recommendations })
    }

    /// Durably selects one recommendation after an optimistic generation check.
    /// The build worker must establish coverage and shadow evidence before
    /// activation. Selection creates no descriptors or planner visibility.
    ///
    /// # Errors
    ///
    /// Returns [`EngineError::Conflict`] when the generation is stale, the
    /// candidate is unknown, forbidden, or already selected; and [`EngineError::StorageFailure`]
    /// when durable state cannot be read or written.
    pub fn auto_index_apply(
        &self,
        tenant: &TenantId,
        candidate_id: &str,
        expected_generation: u64,
        at_millis: u64,
    ) -> Result<crate::auto_index::LifecycleIndexView, EngineError> {
        let current = self.auto_index_lifecycle_generation(tenant)?;
        if current != expected_generation {
            return Err(EngineError::Conflict);
        }
        let recommendation = self
            .auto_index_recommendations(tenant, None, 256)?
            .recommendations
            .into_iter()
            .find(|item| item.candidate_id == candidate_id)
            .ok_or(EngineError::Conflict)?;
        let mut lifecycle =
            self.auto_index_lifecycle.lock().map_err(|_| EngineError::StorageFailure)?;
        if lifecycle.generation(tenant.as_str()) != expected_generation {
            return Err(EngineError::Conflict);
        }
        if lifecycle.index(tenant.as_str(), candidate_id).is_some() {
            return Err(EngineError::Conflict);
        }
        let mut view = crate::auto_index::LifecycleIndexView {
            id: recommendation.candidate_id.clone(),
            tenant: tenant.as_str().to_owned(),
            collection: recommendation.collection,
            fields: recommendation.fields,
            phase: crate::auto_index::LifecyclePhase::Selected,
            generation: current,
            covered_watermark: 0,
            target_watermark: None,
            visible: false,
            pinned: false,
            forbidden: false,
            maintenance_cost: recommendation.maintenance_cost,
            last_used_millis: None,
            zero_stale_reads: false,
            regression_free: false,
        };
        let digest = *blake3::hash(view.id.as_bytes()).as_bytes();
        lifecycle
            .install(self.lock_storage()?.port.as_mut(), view.clone(), at_millis, digest)
            .map_err(|()| EngineError::StorageFailure)?;
        view.generation = lifecycle.generation(tenant.as_str());
        Ok(view)
    }

    /// Pins one exact normalized template to an active candidate.
    ///
    /// # Errors
    ///
    /// Returns [`EngineError::Conflict`] when the generation is stale or the
    /// index is unknown or not active, and [`EngineError::StorageFailure`]
    /// when durable state cannot be read or written.
    pub fn auto_index_pin_template(
        &self,
        tenant: &TenantId,
        index_id: &str,
        template: &str,
        expected_generation: u64,
    ) -> Result<crate::auto_index::LifecycleIndexView, EngineError> {
        self.validate_tenant(tenant)?;
        let mut lifecycle =
            self.auto_index_lifecycle.lock().map_err(|_| EngineError::StorageFailure)?;
        if lifecycle.generation(tenant.as_str()) != expected_generation {
            return Err(EngineError::Conflict);
        }
        let mut view =
            lifecycle.index(tenant.as_str(), index_id).cloned().ok_or(EngineError::Conflict)?;
        if view.phase != crate::auto_index::LifecyclePhase::Active {
            return Err(EngineError::Conflict);
        }
        lifecycle
            .pin_template(
                self.lock_storage()?.port.as_mut(),
                (tenant.as_str(), index_id),
                template,
                Self::now_millis(),
            )
            .map_err(|()| EngineError::StorageFailure)?;
        view.generation = lifecycle.generation(tenant.as_str());
        Ok(view)
    }

    /// Forbids one exact candidate shape and invalidates all optimistic
    /// generations.  It never disables automatic indexing for a collection.
    ///
    /// # Errors
    ///
    /// Returns [`EngineError::Conflict`] when the generation is stale,
    /// [`EngineError::InvalidDocument`] when the candidate or shape is empty,
    /// and [`EngineError::StorageFailure`] when durable state cannot be read
    /// or written.
    pub fn auto_index_forbid_shape(
        &self,
        tenant: &TenantId,
        candidate_id: &str,
        shape: &str,
        expected_generation: u64,
    ) -> Result<u64, EngineError> {
        self.validate_tenant(tenant)?;
        if candidate_id.is_empty() || shape.is_empty() {
            return Err(EngineError::InvalidDocument);
        }
        let mut lifecycle =
            self.auto_index_lifecycle.lock().map_err(|_| EngineError::StorageFailure)?;
        if lifecycle.generation(tenant.as_str()) != expected_generation {
            return Err(EngineError::Conflict);
        }
        lifecycle
            .control(
                self.lock_storage()?.port.as_mut(),
                (tenant.as_str(), candidate_id),
                true,
                Self::now_millis(),
                *blake3::hash(shape.as_bytes()).as_bytes(),
            )
            .map_err(|()| EngineError::StorageFailure)?;
        Ok(lifecycle.generation(tenant.as_str()))
    }

    /// Retires one candidate after its lifecycle has made it invisible.
    ///
    /// # Errors
    ///
    /// Returns [`EngineError::Conflict`] when the generation is stale or the
    /// index is unknown, pinned, or not invisible, and [`EngineError::StorageFailure`] when durable
    /// state cannot be read or written.
    pub fn auto_index_retire(
        &self,
        tenant: &TenantId,
        index_id: &str,
        expected_generation: u64,
        at_millis: u64,
    ) -> Result<crate::auto_index::LifecycleIndexView, EngineError> {
        self.validate_tenant(tenant)?;
        let mut lifecycle =
            self.auto_index_lifecycle.lock().map_err(|_| EngineError::StorageFailure)?;
        if lifecycle.generation(tenant.as_str()) != expected_generation {
            return Err(EngineError::Conflict);
        }
        let view =
            lifecycle.index(tenant.as_str(), index_id).cloned().ok_or(EngineError::Conflict)?;
        if view.pinned || view.phase != crate::auto_index::LifecyclePhase::Invisible {
            return Err(EngineError::Conflict);
        }
        lifecycle
            .retire_index(
                self.lock_storage()?.port.as_mut(),
                (tenant.as_str(), index_id),
                at_millis,
                *blake3::hash(index_id.as_bytes()).as_bytes(),
            )
            .map_err(|()| EngineError::StorageFailure)?;
        let mut retired = view;
        retired.phase = crate::auto_index::LifecyclePhase::Abandoned;
        retired.visible = false;
        retired.generation = lifecycle.generation(tenant.as_str());
        Ok(retired)
    }

    /// Returns bounded durable lifecycle audit entries for the tenant and
    /// optional collection. Events without a collection appear only in the
    /// database-wide audit. Scope filtering precedes the page limit.
    ///
    /// # Errors
    ///
    /// Returns [`EngineError::ScopeMismatch`] when the tenant is foreign to
    /// this Engine's durable owner, and [`EngineError::StorageFailure`] when
    /// the lifecycle state cannot be locked.
    pub fn auto_index_lifecycle_audit(
        &self,
        tenant: &TenantId,
        collection: Option<&str>,
        index_id: Option<&str>,
        limit: usize,
    ) -> Result<Vec<crate::auto_index::LifecycleAuditEntry>, EngineError> {
        self.validate_tenant(tenant)?;
        self.auto_index_lifecycle
            .lock()
            .map(|state| state.audit(tenant.as_str(), collection, index_id, limit))
            .map_err(|_| EngineError::StorageFailure)
    }

    /// Starts an invisible physical build for a durably selected candidate.
    /// Existing descriptors must be fully extracted. A failed lifecycle append
    /// leaves the hidden build resumable by retrying this operation.
    ///
    /// # Errors
    /// Returns [`EngineError::Conflict`] for a stale generation or mismatched
    /// candidate, and [`EngineError::StorageFailure`] for a refused build or journal write.
    pub fn begin_index_build(
        &self,
        tenant: &TenantId,
        index_id: &str,
        columns: &[crate::storage::InvisibleIndexColumn],
        expected_generation: u64,
        at_millis: u64,
    ) -> Result<crate::auto_index::LifecycleIndexView, EngineError> {
        self.validate_tenant(tenant)?;
        let mut lifecycle =
            self.auto_index_lifecycle.lock().map_err(|_| EngineError::StorageFailure)?;
        let mut view =
            lifecycle.index(tenant.as_str(), index_id).cloned().ok_or(EngineError::Conflict)?;
        if lifecycle.generation(tenant.as_str()) != expected_generation
            || view.phase != crate::auto_index::LifecyclePhase::Selected
            || view.forbidden
            || !view
                .fields
                .iter()
                .map(String::as_str)
                .eq(columns.iter().map(|column| column.field.as_str()))
        {
            return Err(EngineError::Conflict);
        }
        let collection =
            CollectionName::try_new(&view.collection).map_err(|_| EngineError::InvalidDocument)?;
        let mut storage = self.lock_storage()?;
        let progress = storage
            .port
            .begin_invisible_index(tenant, &collection, index_id, columns)
            .map_err(|()| EngineError::StorageFailure)?;
        view.target_watermark = Some(progress.target_sequence);
        view.covered_watermark = if progress.complete { progress.target_sequence } else { 0 };
        view.phase = if progress.complete {
            crate::auto_index::LifecyclePhase::Shadowing
        } else {
            crate::auto_index::LifecyclePhase::Building
        };
        lifecycle
            .install(storage.port.as_mut(), view.clone(), at_millis, [0; 32])
            .map_err(|()| EngineError::StorageFailure)?;
        view.generation = lifecycle.generation(tenant.as_str());
        Ok(view)
    }

    /// Advances a hidden build by a bounded batch. Completion enters shadow
    /// verification and never changes planner visibility.
    ///
    /// # Errors
    /// Returns [`EngineError::Conflict`] for stale or ineligible state and
    /// [`EngineError::StorageFailure`] when backfill or durable publication fails.
    pub fn advance_index_build(
        &self,
        tenant: &TenantId,
        index_id: &str,
        batch: u32,
        expected_generation: u64,
        at_millis: u64,
    ) -> Result<crate::auto_index::LifecycleIndexView, EngineError> {
        self.validate_tenant(tenant)?;
        let mut lifecycle =
            self.auto_index_lifecycle.lock().map_err(|_| EngineError::StorageFailure)?;
        let mut view =
            lifecycle.index(tenant.as_str(), index_id).cloned().ok_or(EngineError::Conflict)?;
        if lifecycle.generation(tenant.as_str()) != expected_generation
            || view.phase != crate::auto_index::LifecyclePhase::Building
            || view.forbidden
        {
            return Err(EngineError::Conflict);
        }
        let mut storage = self.lock_storage()?;
        let progress = storage
            .port
            .advance_invisible_index(tenant, index_id, batch)
            .map_err(|()| EngineError::StorageFailure)?;
        if view.target_watermark != Some(progress.target_sequence) {
            return Err(EngineError::StorageFailure);
        }
        if progress.complete {
            view.phase = crate::auto_index::LifecyclePhase::Shadowing;
            view.covered_watermark = progress.target_sequence;
        }
        lifecycle
            .install(storage.port.as_mut(), view.clone(), at_millis, [0; 32])
            .map_err(|()| EngineError::StorageFailure)?;
        view.generation = lifecycle.generation(tenant.as_str());
        Ok(view)
    }

    /// Publishes a completed, verified hidden index to the planner. The
    /// optimistic generation and retained shadow evidence are checked while
    /// the lifecycle and storage locks are held, so a stale verifier cannot
    /// expose a candidate after another lifecycle transition.
    ///
    /// # Errors
    /// Returns [`EngineError`] when the tenant, generation, lifecycle phase,
    /// shadow evidence, or durable installation is invalid.
    pub fn auto_index_activate(
        &self,
        tenant: &TenantId,
        index_id: &str,
        expected_generation: u64,
        at_millis: u64,
    ) -> Result<crate::auto_index::LifecycleIndexView, EngineError> {
        self.validate_tenant(tenant)?;
        let mut lifecycle =
            self.auto_index_lifecycle.lock().map_err(|_| EngineError::StorageFailure)?;
        if lifecycle.generation(tenant.as_str()) != expected_generation {
            return Err(EngineError::Conflict);
        }
        let mut view =
            lifecycle.index(tenant.as_str(), index_id).cloned().ok_or(EngineError::Conflict)?;
        let Some(evidence_digest) = lifecycle.evidence_digest(tenant.as_str(), index_id) else {
            return Err(EngineError::Conflict);
        };
        if view.phase != crate::auto_index::LifecyclePhase::Shadowing
            || view.visible
            || view.target_watermark != Some(view.covered_watermark)
            || lifecycle.evidence_outcome(tenant.as_str(), index_id, evidence_digest)
                != Some(crate::auto_index::ShadowOutcome::Activate)
        {
            return Err(EngineError::Conflict);
        }
        view.phase = crate::auto_index::LifecyclePhase::Active;
        view.visible = true;
        view.zero_stale_reads = true;
        view.regression_free = true;
        lifecycle
            .install(self.lock_storage()?.port.as_mut(), view.clone(), at_millis, [0; 32])
            .map_err(|()| EngineError::StorageFailure)?;
        view.generation = lifecycle.generation(tenant.as_str());
        Ok(view)
    }

    /// Abandons a shadow candidate rejected by the measured gate.
    pub fn auto_index_abandon_shadow(
        &self,
        tenant: &TenantId,
        index_id: &str,
        expected_generation: u64,
        at_millis: u64,
    ) -> Result<crate::auto_index::LifecycleIndexView, EngineError> {
        self.validate_tenant(tenant)?;
        let mut lifecycle =
            self.auto_index_lifecycle.lock().map_err(|_| EngineError::StorageFailure)?;
        if lifecycle.generation(tenant.as_str()) != expected_generation {
            return Err(EngineError::Conflict);
        }
        let view =
            lifecycle.index(tenant.as_str(), index_id).cloned().ok_or(EngineError::Conflict)?;
        lifecycle
            .abandon_shadow(
                self.lock_storage()?.port.as_mut(),
                (tenant.as_str(), index_id),
                at_millis,
                *blake3::hash(index_id.as_bytes()).as_bytes(),
            )
            .map_err(|_| EngineError::Conflict)?;
        let mut abandoned = view;
        abandoned.phase = crate::auto_index::LifecyclePhase::Abandoned;
        abandoned.visible = false;
        abandoned.generation = lifecycle.generation(tenant.as_str());
        Ok(abandoned)
    }

    /// Hides an active index immediately after a verified regression. The
    /// physical hidden table remains available for delayed cleanup.
    ///
    /// # Errors
    /// Returns [`EngineError`] when the tenant, generation, lifecycle phase,
    /// or durable installation is invalid.
    pub fn auto_index_revert(
        &self,
        tenant: &TenantId,
        index_id: &str,
        expected_generation: u64,
        at_millis: u64,
    ) -> Result<crate::auto_index::LifecycleIndexView, EngineError> {
        self.validate_tenant(tenant)?;
        let mut lifecycle =
            self.auto_index_lifecycle.lock().map_err(|_| EngineError::StorageFailure)?;
        if lifecycle.generation(tenant.as_str()) != expected_generation {
            return Err(EngineError::Conflict);
        }
        let mut view =
            lifecycle.index(tenant.as_str(), index_id).cloned().ok_or(EngineError::Conflict)?;
        if view.phase != crate::auto_index::LifecyclePhase::Active || !view.visible {
            return Err(EngineError::Conflict);
        }
        view.phase = crate::auto_index::LifecyclePhase::Invisible;
        view.visible = false;
        view.regression_free = false;
        lifecycle
            .install(self.lock_storage()?.port.as_mut(), view.clone(), at_millis, [0; 32])
            .map_err(|_| EngineError::StorageFailure)?;
        view.generation = lifecycle.generation(tenant.as_str());
        Ok(view)
    }

    /// Measures ordinary and forced-index collection reads at one snapshot.
    /// The counters are diagnostic VM/full-scan measurements and cannot by
    /// themselves authorize activation or become page/row evidence.
    ///
    /// # Errors
    /// Returns [`crate::storage::ShadowReadError`] for a foreign scope,
    /// unavailable snapshot/index, invalid request, or exceeded work budget.
    pub fn measure_index_shadow(
        &self,
        scope: &TenantScope,
        snapshot: &ReadSnapshot,
        request: &crate::storage::ShadowReadRequest<'_>,
    ) -> Result<crate::storage::ShadowReadComparison, crate::storage::ShadowReadError> {
        use crate::storage::ShadowReadError;
        if snapshot.engine_identity() != self.identity || snapshot.tenant() != scope.tenant() {
            return Err(ShadowReadError::ScopeMismatch);
        }
        self.validate_scope(scope).map_err(|_| ShadowReadError::ScopeMismatch)?;
        self.lock_storage().map_err(|_| ShadowReadError::Unavailable)?.port.shadow_filtered_page(
            snapshot.sequence(),
            scope.tenant(),
            request,
        )
    }

    /// Returns bounded durable shadow evidence, or `None` when no retained
    /// measurement exists. Recording evidence does not promote a candidate.
    ///
    /// # Errors
    ///
    /// Returns [`EngineError::ScopeMismatch`] when the tenant is foreign to
    /// this Engine's durable owner, and [`EngineError::StorageFailure`] when
    /// the lifecycle state cannot be locked.
    pub fn auto_index_shadow_evidence(
        &self,
        tenant: &TenantId,
        index_id: &str,
        template_digest: [u8; 32],
        limit: usize,
    ) -> Result<Option<crate::auto_index::ShadowEvidence>, EngineError> {
        self.validate_tenant(tenant)?;
        Ok(self.auto_index_lifecycle.lock().map_err(|_| EngineError::StorageFailure)?.evidence(
            tenant.as_str(),
            index_id,
            template_digest,
            limit,
        ))
    }

    /// Returns the deterministic template digest for the first retained
    /// evidence record of an index. The digest is required to retrieve the
    /// exact paired sample set without exposing a template through the
    /// lifecycle API.
    ///
    /// # Errors
    /// Returns [`EngineError`] when the tenant is foreign or lifecycle state
    /// cannot be locked.
    pub fn auto_index_shadow_evidence_digest(
        &self,
        tenant: &TenantId,
        index_id: &str,
    ) -> Result<Option<[u8; 32]>, EngineError> {
        self.validate_tenant(tenant)?;
        Ok(self
            .auto_index_lifecycle
            .lock()
            .map_err(|_| EngineError::StorageFailure)?
            .evidence_digest(tenant.as_str(), index_id))
    }

    /// Records bounded paired shadow samples in the durable lifecycle journal.
    /// The lifecycle generation is checked and advanced atomically with the
    /// evidence event; recording evidence never changes planner visibility.
    ///
    /// # Errors
    /// Returns [`EngineError`] when the tenant, evidence, lifecycle state, or
    /// durable evidence record is invalid.
    pub fn auto_index_record_shadow_evidence(
        &self,
        tenant: &TenantId,
        evidence: crate::auto_index::ShadowEvidence,
        at_millis: u64,
    ) -> Result<crate::auto_index::ShadowEvidence, EngineError> {
        self.validate_tenant(tenant)?;
        if evidence.index_id.is_empty() || evidence.index_id.len() > 1024 {
            return Err(EngineError::InvalidDocument);
        }
        let mut lifecycle =
            self.auto_index_lifecycle.lock().map_err(|_| EngineError::StorageFailure)?;
        lifecycle
            .record_evidence(
                self.lock_storage()?.port.as_mut(),
                tenant.as_str(),
                evidence,
                at_millis,
            )
            .map_err(|()| EngineError::Conflict)
    }

    /// Measures a paired ordinary/hidden execution against one immutable
    /// snapshot and commits the resulting samples. Every value comes from
    /// SQLite statement counters; no timing or inferred estimate is admitted.
    ///
    /// # Errors
    /// Returns [`EngineError`] when the scope, sample bounds, snapshot, or
    /// shadow measurement is invalid.
    pub fn auto_index_measure_shadow(
        &self,
        scope: &TenantScope,
        request: &crate::storage::ShadowReadRequest<'_>,
        template_digest: [u8; 32],
        expected_generation: u64,
        samples: u16,
        at_millis: u64,
    ) -> Result<crate::auto_index::ShadowEvidence, EngineError> {
        let max_samples = u16::try_from(crate::auto_index::MAX_SHADOW_SAMPLES)
            .map_err(|_| EngineError::InvalidDocument)?;
        if !(20..=max_samples).contains(&samples) || template_digest == [0; 32] {
            return Err(EngineError::InvalidDocument);
        }
        self.validate_scope(scope)?;
        let tenant = scope.tenant().clone();
        let snapshot = self.read_snapshot(scope)?;
        let mut baseline = Vec::with_capacity(samples as usize);
        let mut candidate = Vec::with_capacity(samples as usize);
        for _ in 0..samples {
            let comparison = self
                .measure_index_shadow(scope, &snapshot, request)
                .map_err(|_| EngineError::StorageFailure)?;
            baseline.push(crate::auto_index::LogicalCost::new(
                u64::from(comparison.ordinary.full_scan_steps),
                u64::from(comparison.ordinary.rows),
            ));
            candidate.push(crate::auto_index::LogicalCost::new(
                u64::from(comparison.candidate.full_scan_steps),
                u64::from(comparison.candidate.rows),
            ));
        }
        self.auto_index_record_shadow_evidence(
            &tenant,
            crate::auto_index::ShadowEvidence {
                index_id: request.hidden_candidate.ok_or(EngineError::InvalidDocument)?.to_owned(),
                generation: expected_generation,
                template_digest,
                baseline,
                candidate,
                improvement: 0.0,
                regression: false,
                outcome: crate::auto_index::ShadowOutcome::Abandon,
                digest: [0; 32],
            },
            at_millis,
        )
    }

    /// Admits at most one live shadow sample per template/candidate each
    /// interval and records its measured counters against the caller's
    /// immutable snapshot.
    #[expect(
        clippy::too_many_arguments,
        reason = "query observer callback carries one measured sample"
    )]
    pub(crate) fn sample_auto_index_shadow(
        &self,
        scope: &TenantScope,
        snapshot: &ReadSnapshot,
        collection: &str,
        field: &str,
        template_digest: [u8; 32],
        candidate_fields: &[String],
        filter: &crate::storage::ScanFilter,
        generation: u64,
    ) {
        let tenant = scope.tenant().as_str().to_owned();
        let Some(candidate) = self.auto_index_lifecycle.lock().ok().and_then(|state| {
            state.shadow_candidate(&tenant, collection, template_digest, candidate_fields)
        }) else {
            return;
        };
        let key = (tenant.clone(), candidate.clone(), template_digest);
        let now = Self::now_millis();
        let Ok(mut samples) = self.auto_index_shadow_samples.lock() else { return };
        if samples.get(&key).is_some_and(|(last, _, _)| now.saturating_sub(*last) < 60_000) {
            return;
        }
        let Ok(collection) = crate::types::CollectionName::try_new(collection) else { return };
        let request = crate::storage::ShadowReadRequest {
            collection: &collection,
            filter,
            candidate_field: field,
            hidden_candidate: Some(&candidate),
            after: None,
            limit: 100,
            max_vm_steps: crate::storage::MAX_SHADOW_VM_STEPS,
        };
        let Ok(comparison) = self.measure_index_shadow(scope, snapshot, &request) else { return };
        let entry = samples.entry(key.clone()).or_insert_with(|| (0, Vec::new(), Vec::new()));
        entry.0 = now;
        entry.1.push(crate::auto_index::LogicalCost::new(
            u64::from(comparison.ordinary.full_scan_steps),
            u64::from(comparison.ordinary.rows),
        ));
        entry.2.push(crate::auto_index::LogicalCost::new(
            u64::from(comparison.candidate.full_scan_steps),
            u64::from(comparison.candidate.rows),
        ));
        let sample_count = entry.1.len();
        let _ = entry;
        if samples.len() > 256
            && let Some(oldest) =
                samples.iter().min_by_key(|(_, (last, _, _))| *last).map(|(key, _)| key.clone())
        {
            samples.remove(&oldest);
        }
        if sample_count < 20 {
            return;
        }
        let (_, baseline, candidate_samples) = samples.remove(&key).expect("shadow sample entry");
        drop(samples);
        let _ = self.auto_index_record_shadow_evidence(
            &scope.tenant().clone(),
            crate::auto_index::ShadowEvidence {
                index_id: candidate,
                generation,
                template_digest,
                baseline,
                candidate: candidate_samples,
                improvement: 0.0,
                regression: false,
                outcome: crate::auto_index::ShadowOutcome::Abandon,
                digest: [0; 32],
            },
            now,
        );
    }

    /// Records one literal-free execution observation for the D-31 bounded
    /// workload sketch. Query execution supplies this after it has computed
    /// the result statistics, so the observation is never synthetic.
    pub(crate) fn record_auto_index_observation(
        &self,
        observation: crate::auto_index::Observation,
    ) {
        if let Ok(mut observations) = self.auto_index_observations.lock() {
            observations.observe(observation);
        }
    }

    /// Returns one document's retained revision history, newest first
    /// (D-42: history is journal retention — the pruner's consumer
    /// frontiers and the 30-day floor bound what is still servable).
    ///
    /// # Errors
    ///
    /// Returns [`EngineError::StorageFailure`] when the durable read
    /// fails.
    pub fn document_history_in_collection(
        &self,
        scope: &TenantScope,
        collection: &CollectionName,
        id: &DocumentId,
        limit: u32,
    ) -> Result<Vec<crate::storage::DocumentRevisionEntry>, EngineError> {
        self.validate_scope(scope)?;
        let storage = self.lock_storage()?;
        Ok(storage.port.document_history_in_collection(scope.tenant(), collection, id, limit))
    }

    /// Advances every building descriptor's governed backfill by one
    /// bounded batch of rows (D-31), completing a descriptor — backfill
    /// done, ordinary indexes created, conditions pushing — when its cursor
    /// reaches its registration watermark. A host schedules this on its own
    /// timer, the same cadence as [`Engine::maintain_indexes`]; the pass is
    /// idempotent and resumable through its durable cursor.
    ///
    /// # Errors
    ///
    /// Returns [`EngineError::StorageFailure`] when the durable pass fails.
    pub fn maintain_index_backfills(
        &self,
    ) -> Result<Vec<crate::storage::BackfillProgress>, EngineError> {
        let mut storage = self.lock_storage()?;
        storage
            .port
            .pump_index_backfills(INDEX_BACKFILL_BATCH)
            .map_err(|()| EngineError::StorageFailure)
    }

    /// Returns one bounded page of final-state documents at an explicit
    /// durable boundary, in canonical `(collection, identifier)` order.
    /// Unlike the legacy identifier cursor, the full-key cursor is lossless
    /// when an identifier is reused across collections. Each returned record
    /// carries the durability of the write that won at `boundary`.
    ///
    /// The page is served from the immutable boundary view, so commits that
    /// arrive while callers request later pages cannot enter the result. The
    /// history scan resolves durability only for this page, keeping working
    /// memory proportional to the page rather than the tenant.
    ///
    /// # Errors
    ///
    /// Returns [`EngineError::SnapshotPageLimitExceeded`] when `limit` is
    /// above [`MAX_SNAPSHOT_RECORD_PAGE`],
    /// [`EngineError::SnapshotBoundaryUnavailable`] when the boundary cannot
    /// be served or proved by retained history, and
    /// [`EngineError::StorageFailure`] when storage cannot be reached.
    #[allow(clippy::needless_pass_by_value)]
    pub(crate) fn snapshot_records_from(
        &self,
        tenant: TenantId,
        boundary: StorageSequence,
        after: Option<(CollectionName, DocumentId)>,
        limit: u32,
    ) -> Result<Vec<SnapshotRecord>, EngineError> {
        self.validate_tenant(&tenant)?;
        if limit > MAX_SNAPSHOT_RECORD_PAGE {
            return Err(EngineError::SnapshotPageLimitExceeded);
        }
        let storage = self.lock_storage()?;
        let current = storage.port.published_state().0;
        if boundary > current {
            return Err(EngineError::SnapshotBoundaryUnavailable);
        }
        if !storage.port.snapshot_boundary_available(boundary) {
            return Err(EngineError::SnapshotBoundaryUnavailable);
        }
        if storage
            .port
            .servable_history_start()
            .map_err(|()| EngineError::StorageFailure)?
            .is_some_and(|start| boundary < start)
        {
            return Err(EngineError::SnapshotBoundaryUnavailable);
        }
        if limit == 0 {
            return Ok(Vec::new());
        }
        let documents = storage
            .port
            .documents_at_from_key(
                boundary,
                &tenant,
                after.as_ref().map(|(collection, identifier)| (collection, identifier)),
                limit,
            )
            .map_err(|_| EngineError::StorageFailure)?;
        if documents.is_empty() {
            return Ok(Vec::new());
        }
        let keys: Vec<_> = documents
            .iter()
            .map(|document| (document.collection().as_str(), document.id().as_str()))
            .collect();
        let winners = storage
            .port
            .winning_durabilities_at(&tenant, boundary, &keys)
            .ok_or(EngineError::SnapshotBoundaryUnavailable)?;
        if winners.len() != documents.len() {
            return Err(EngineError::SnapshotBoundaryUnavailable);
        }
        Ok(documents
            .into_iter()
            .zip(winners)
            .map(|(document, durability)| SnapshotRecord { document, durability })
            .collect())
    }

    /// Returns one tenant's published commits after `after`, in global
    /// storage-sequence order, up to `limit` entries (W4-CA-4).
    ///
    /// This is the durable publication-history read replication catch-up and
    /// backup boundaries consume: the write set travels reconstructed — full
    /// documents for puts, the exact deleted key for deletes — never as a
    /// digest.
    ///
    /// # Errors
    ///
    /// Returns [`EngineError::StorageFailure`] when storage cannot be
    /// reached. The mirrors deliberately fail closed on an unreachable
    /// storage lock instead of collapsing to an empty answer: an empty
    /// result here would read as "nothing to catch up" (repair F5).
    #[allow(dead_code, reason = "consumed by the W4 replication and backup packets")]
    pub(crate) fn tenant_commits(
        &self,
        tenant: &TenantId,
        after: StorageSequence,
        limit: u32,
    ) -> Result<Vec<PublishedCommit>, EngineError> {
        self.validate_tenant(tenant)?;
        let storage = self.lock_storage()?;
        storage.port.tenant_commits(tenant, after, limit).map_err(|error| match error {
            storage::JournalReadError::StorageFailure => EngineError::StorageFailure,
            storage::JournalReadError::HistoryExpired => EngineError::HistoryExpired,
        })
    }

    pub(crate) fn journal_before_image(
        &self,
        tenant: &TenantId,
        collection: &crate::types::CollectionName,
        id: &crate::types::DocumentId,
        sequence: StorageSequence,
    ) -> Result<storage::JournalBeforeImage, EngineError> {
        self.validate_tenant(tenant)?;
        self.lock_storage()?
            .port
            .journal_before_image(tenant, collection, id, sequence)
            .map_err(|()| EngineError::StorageFailure)
    }

    /// Returns one bounded page of retained publication-journal rows for
    /// anti-entropy. The global sequence is only a paging cursor; each row's
    /// reconciliation identity is its persisted origin and origin sequence.
    ///
    /// # Errors
    ///
    /// Returns [`EngineError::ScopeMismatch`] for another engine's scope and
    /// [`EngineError::StorageFailure`] when storage cannot be reached.
    pub fn journal_rows(
        &self,
        scope: &TenantScope,
        after: u64,
        limit: u32,
    ) -> Result<Vec<JournalRow>, EngineError> {
        self.validate_scope(scope)?;
        if limit == 0 {
            return Ok(Vec::new());
        }
        let storage = self.lock_storage()?;
        Ok(storage
            .port
            .journal_rows(scope.tenant(), StorageSequence::new(after), limit)
            .into_iter()
            .map(|row| JournalRow {
                commit_sequence: row.commit_sequence,
                origin: row.origin,
                origin_sequence: row.origin_sequence,
                canonical: row.canonical,
            })
            .collect())
    }

    /// Recovers contiguous origin prefixes from durable provenance and progress
    /// saved atomically before pruning. Missing sequences cannot establish a prefix.
    ///
    /// # Errors
    ///
    /// Returns a scope error for a foreign tenant scope or a storage failure
    /// if the durable provenance cannot be read or validated.
    pub fn durable_origin_prefixes(
        &self,
        scope: &TenantScope,
    ) -> Result<std::collections::BTreeMap<String, u64>, EngineError> {
        self.validate_scope(scope)?;
        let storage = self.lock_storage()?;
        let entries = storage
            .port
            .live_origin_prefixes(scope.tenant())
            .map_err(|()| EngineError::StorageFailure)?;
        Ok(entries.into_iter().map(|entry| (entry.origin, entry.origin_sequence)).collect())
    }

    /// Returns every tenant that has at least one recorded published commit,
    /// in canonical tenant order (W4-CA-4).
    ///
    /// # Errors
    ///
    /// Returns [`EngineError::StorageFailure`] when storage cannot be
    /// reached; the enumeration fails closed rather than collapsing to an
    /// empty list (repair F5).
    #[allow(dead_code, reason = "consumed by the W4 replication and backup packets")]
    pub(crate) fn committed_tenants(&self) -> Result<Vec<TenantId>, EngineError> {
        let storage = self.lock_storage()?;
        storage.port.committed_tenants().map_err(|()| EngineError::StorageFailure)
    }

    /// Returns the tenant's latest recorded sequence at or below `at`
    /// (W4-CA-4): the mapping entry that turns one global storage-sequence
    /// cursor into this tenant's own progress.
    ///
    /// # Errors
    ///
    /// Returns [`EngineError::StorageFailure`] when storage cannot be
    /// reached (repair F5).
    #[allow(dead_code, reason = "consumed by the W4 replication and backup packets")]
    pub(crate) fn tenant_sequence(
        &self,
        tenant: &TenantId,
        at: StorageSequence,
    ) -> Result<Option<StorageSequence>, EngineError> {
        self.validate_tenant(tenant)?;
        let storage = self.lock_storage()?;
        storage.port.tenant_sequence(tenant, at).map_err(|()| EngineError::StorageFailure)
    }

    /// Returns the oldest sequence the durable publication history can
    /// serve, or `None` when no history is servable (W4-CA-4).
    ///
    /// A cursor older than this bound cannot be served incrementally: the
    /// caller must fall back to a full catch-up or backup rather than
    /// silently skip the gap.
    ///
    /// # Errors
    ///
    /// Returns [`EngineError::StorageFailure`] when storage cannot be
    /// reached (repair F5).
    #[allow(dead_code, reason = "consumed by the W4 replication and backup packets")]
    pub(crate) fn servable_history_start(&self) -> Result<Option<StorageSequence>, EngineError> {
        let storage = self.lock_storage()?;
        storage.port.servable_history_start().map_err(|()| EngineError::StorageFailure)
    }

    /// Reads one bounded journal page's durable clocks using one indexed query.
    pub(crate) fn backup_commit_clocks(
        &self,
        sequences: &[StorageSequence],
    ) -> Result<Vec<u64>, EngineError> {
        if sequences.is_empty() {
            return Ok(Vec::new());
        }
        if sequences.len() > 1024 {
            return Err(EngineError::StorageFailure);
        }
        let parameters: Vec<i64> = sequences
            .iter()
            .map(|sequence| i64::try_from(sequence.get()).map_err(|_| EngineError::StorageFailure))
            .collect::<Result<_, _>>()?;
        let placeholders = std::iter::repeat_n("?", sequences.len()).collect::<Vec<_>>().join(",");
        let connection = self.open_sqlite_read_connection()?;
        let mut statement = connection.prepare(&format!(
            "SELECT local_seq, committed_at FROM replication_publications WHERE local_seq IN ({placeholders})"
        )).map_err(|_| EngineError::StorageFailure)?;
        let rows = statement
            .query_map(rusqlite::params_from_iter(parameters.iter()), |row| {
                Ok((row.get::<_, i64>(0)?, row.get::<_, i64>(1)?))
            })
            .map_err(|_| EngineError::StorageFailure)?;
        let clocks = rows
            .collect::<Result<std::collections::BTreeMap<_, _>, _>>()
            .map_err(|_| EngineError::StorageFailure)?;
        parameters
            .iter()
            .map(|sequence| {
                clocks
                    .get(sequence)
                    .copied()
                    .and_then(|clock| u64::try_from(clock).ok())
                    .ok_or(EngineError::StorageFailure)
            })
            .collect()
    }

    /// Reads the durable monotonic clock of an exact source publication.
    /// Sequence zero names the empty database baseline at the Unix epoch.
    pub(crate) fn backup_boundary_unix_millis(
        &self,
        sequence: StorageSequence,
    ) -> Result<u64, EngineError> {
        if sequence.get() == 0 {
            return Ok(0);
        }
        let connection = self.open_sqlite_read_connection()?;
        let timestamp: i64 = connection
            .query_row(
                "SELECT committed_at FROM replication_publications WHERE local_seq = ?1",
                [i64::try_from(sequence.get()).map_err(|_| EngineError::StorageFailure)?],
                |row| row.get(0),
            )
            .map_err(|_| EngineError::StorageFailure)?;
        u64::try_from(timestamp).map_err(|_| EngineError::StorageFailure)
    }

    /// Returns the last global storage sequence durably recorded in the
    /// publication-history journal (W4-CA-4 repair F2).
    ///
    /// `journaled_high_water() < read_snapshot(...).sequence()` is the
    /// observable signal that at least one published commit is not servable
    /// from the journal: sequences have legitimate holes, so no consumer may
    /// infer completeness from discontinuity.
    ///
    /// # Errors
    ///
    /// Returns [`EngineError::StorageFailure`] when storage cannot be
    /// reached (repair F5).
    #[allow(dead_code, reason = "consumed by the W4 replication and backup packets")]
    pub(crate) fn journaled_high_water(&self) -> Result<Option<StorageSequence>, EngineError> {
        let storage = self.lock_storage()?;
        storage.port.journaled_high_water().map_err(|()| EngineError::StorageFailure)
    }

    /// Returns the number of published commits the publication-history
    /// journal does not serve (W4-CA-4 repair F2's divergence, as a
    /// count).
    ///
    fn lock_storage(&self) -> Result<std::sync::MutexGuard<'_, StorageState>, EngineError> {
        self.validate_storage_authorization()?;
        self.storage.lock().map_err(|_| EngineError::StorageFailure)
    }

    /// Opens the engine's bounded `SQLite` read handle for SQL execution.
    ///
    /// The physical location remains Engine-owned; this capability is
    /// deliberately crate-private so callers cannot reopen an arbitrary path
    /// or bypass ownership validation.
    pub(crate) fn open_sqlite_read_connection(&self) -> Result<rusqlite::Connection, EngineError> {
        let storage = self.lock_storage()?;
        storage.port.open_sqlite_read_connection().map_err(|()| EngineError::StorageFailure)
    }

    /// Registers one publication-event sink and returns its guard.
    ///
    /// The guard detaches the sink when dropped. Sinks are called
    /// synchronously on the committing thread after storage locks release,
    /// so a sink must not block, must not take a storage lock, and must not
    /// grow an unbounded queue; the registry bounds its subscriber count at
    /// [`MAX_PUBLICATION_SINKS`] and never queues events itself.
    #[allow(dead_code, reason = "the W4-STREAM subscription path acquires through this seam")]
    pub(crate) fn subscribe_publications(&self, sink: CommitPublicationSink) -> SubscriptionGuard {
        self.subscriptions.subscribe(sink)
    }

    /// Returns the Engine-owned production index catalog (W4-CA-3).
    ///
    /// The catalog's publication sink is registered for the Engine's
    /// lifetime, so its derived index state is maintained by real commits.
    #[allow(dead_code, reason = "consumed by the query-side catalog binding and the index tests")]
    pub(crate) fn index_catalog(&self) -> Arc<crate::index::IndexCatalog> {
        Arc::clone(&self.indexes)
    }

    /// Returns the retained bulk-by-query audit events in recorded order
    /// (GAP-Q10).
    ///
    /// The journal is bounded and in-process: the newest
    /// [`MAX_BULK_AUDIT_ENTRIES`](crate::query::bulk::MAX_BULK_AUDIT_ENTRIES)
    /// events of the engine are retained, each carrying only structural
    /// identity and counts. Durable audit shipping belongs to the Platform
    /// side.
    #[must_use]
    pub fn bulk_operations_audit(&self) -> Vec<crate::query::bulk::BulkAuditEntry> {
        self.bulk_audit.entries()
    }

    /// Records one bulk-by-query audit event. Crate-only: the query-to-write
    /// bridge (`crate::query::bulk`) is the only writer.
    pub(crate) fn record_bulk_audit(
        &self,
        stage: crate::query::bulk::BulkAuditStage,
        kind: crate::query::bulk::BulkOperationKind,
        tenant: &TenantId,
        operation: Option<&OperationId>,
        selected: u64,
        processed: u64,
    ) {
        if self.validate_tenant(tenant).is_err() {
            return;
        }
        self.bulk_audit.record(stage, kind, tenant, operation, selected, processed);
    }

    /// Runs one auto-index lifecycle maintenance pass (IX-03 rider).
    ///
    /// This is the engine-owned body a host schedules on its own timer
    /// (PD-2026-08-26-01 D1: cadence belongs to the host side, the pass to
    /// the Engine). The dynamic query path stays the live driver — every
    /// dynamic query's optimizer pass runs the same maintenance at its head —
    /// so a host without a timer loses no correctness, only idle-time
    /// retirement latency. The pass is bounded, holds no lock across an
    /// Engine call, and never touches an explicit index.
    pub fn maintain_indexes(&self) {
        crate::query::optimizer::lifecycle::maintain(self, &self.indexes, self.identity);
        self.maintain_d31_indexes();
    }

    fn maintain_d31_indexes(&self) {
        let Ok(tenant) = TenantId::try_new(self.database_owner().tenant().to_string().as_str())
        else {
            return;
        };
        let Ok(snapshot) = self.auto_index_lifecycle_snapshot(&tenant, None, 64) else { return };
        for index in snapshot.indexes {
            let generation = index.generation;
            match index.phase {
                crate::auto_index::LifecyclePhase::Selected => {
                    let columns = index
                        .fields
                        .iter()
                        .map(|field| crate::storage::InvisibleIndexColumn {
                            field: field.clone(),
                            kind: crate::storage::InvisibleColumnKind::Text,
                        })
                        .collect::<Vec<_>>();
                    let started = Instant::now();
                    let result = self.begin_index_build(
                        &tenant,
                        &index.id,
                        &columns,
                        generation,
                        Self::now_millis(),
                    );
                    self.metrics.record_index(
                        if result.is_ok() { Outcome::Success } else { Outcome::Error },
                        started.elapsed(),
                    );
                }
                crate::auto_index::LifecyclePhase::Building => {
                    let started = Instant::now();
                    let result = self.advance_index_build(
                        &tenant,
                        &index.id,
                        32,
                        generation,
                        Self::now_millis(),
                    );
                    self.metrics.record_index(
                        if result.is_ok() { Outcome::Success } else { Outcome::Error },
                        started.elapsed(),
                    );
                }
                crate::auto_index::LifecyclePhase::Shadowing => {
                    if let Some(digest) =
                        self.auto_index_shadow_evidence_digest(&tenant, &index.id).ok().flatten()
                        && let Ok(Some(evidence)) =
                            self.auto_index_shadow_evidence(&tenant, &index.id, digest, 64)
                    {
                        match evidence.outcome {
                            crate::auto_index::ShadowOutcome::Abandon => {
                                let started = Instant::now();
                                let result = self.auto_index_abandon_shadow(
                                    &tenant,
                                    &index.id,
                                    generation,
                                    Self::now_millis(),
                                );
                                self.metrics.record_index(
                                    if result.is_ok() { Outcome::Success } else { Outcome::Error },
                                    started.elapsed(),
                                );
                            }
                            crate::auto_index::ShadowOutcome::Activate => {
                                let started = Instant::now();
                                let result = self.auto_index_activate(
                                    &tenant,
                                    &index.id,
                                    generation,
                                    Self::now_millis(),
                                );
                                self.metrics.record_index(
                                    if result.is_ok() { Outcome::Success } else { Outcome::Error },
                                    started.elapsed(),
                                );
                            }
                            crate::auto_index::ShadowOutcome::ActivateWithPins => {}
                        }
                    }
                }
                _ => {}
            }
        }
    }

    /// Arms one storage fault inside the publication order. Test-only.
    #[cfg(test)]
    pub(crate) fn arm_storage_fault(&self, point: FaultPoint) {
        let mut storage = self.storage.lock().expect("storage lock");
        storage.port.arm_test_fault(point);
    }

    fn emit_publication_event(&self, event: &CommitPublicationEvent) {
        self.subscriptions.emit(event);
    }
}

/// An Engine-borrowing transaction write set.
///
/// The transaction is opaque: it stages typed document operations against one
/// immutable snapshot, one tenant scope, one Engine, and one operation
/// identity, and publishes all staged writes atomically or not at all. The
/// tenant scope cannot change during the transaction; there is no such method.
///
/// While a transaction is live, its operation identity is reserved: a second
/// transaction of the same Engine cannot use it. Committing or dropping the
/// transaction releases the reservation; the durable operation ledger remains
/// the binding for any committed identity.
pub struct Transaction<'engine> {
    engine: &'engine Engine,
    scope: TenantScope,
    /// The immutable snapshot this transaction is tied to. It is retained for
    /// contract fidelity; reads use `snapshot_sequence` against storage
    /// history so the snapshot value itself is never mutated.
    #[allow(dead_code)]
    snapshot: ReadSnapshot,
    snapshot_sequence: StorageSequence,
    operation: OperationId,
    usage_admission: Option<UsageAdmissionContext>,
    budget_allocation: Option<crate::usage::VerifiedAllocation>,
    internal_maintenance: bool,
    shard_copy: bool,
    source_authority: Option<(Arc<Engine>, u64)>,
    expected_origin_epoch: u64,
    expected_uniform_context: Option<Vec<u8>>,
    merge_conflict: Option<(CollectionName, DocumentId, storage::MergeConflictEntry)>,
    replication_fingerprint: Option<[u8; 32]>,
    replication_publication: Option<storage::ReplicationPublicationIntent>,
    shard_admission: Option<crate::shard_split::ShardAdmissionToken>,
    writes: Vec<StagedWrite>,
    metadata: Vec<storage::PrivateMetadataWrite>,
    schema_envelopes: Vec<storage::SchemaEnvelopeWrite>,
}

impl Drop for Transaction<'_> {
    fn drop(&mut self) {
        self.engine.release_operation(&self.scope, &self.operation);
    }
}

impl Transaction<'_> {
    /// Requires this write's causal context to remain exactly the frontier
    /// already proven uniform across its selected Regions. Storage compares it
    /// atomically before any new mutation; committed idempotent retries still
    /// replay their durable receipt. A changed frontier returns a conflict so
    /// the caller can obtain a new uniform cut and retry safely.
    ///
    /// # Errors
    ///
    /// Returns `InvalidDocument` for malformed or unbounded frontier entries.
    pub fn require_uniform_context(
        &mut self,
        frontier: std::collections::BTreeMap<String, u64>,
    ) -> Result<(), EngineError> {
        if frontier.len() > 1024
            || frontier.iter().any(|(origin, sequence)| {
                origin.is_empty()
                    || origin.len() > 1024
                    || origin.chars().any(char::is_control)
                    || *sequence == 0
                    || *sequence > i64::MAX as u64
            })
        {
            return Err(EngineError::InvalidDocument);
        }
        self.expected_uniform_context =
            Some(merge::encode_context(&merge::DocumentContext::from_frontier(frontier)));
        Ok(())
    }

    /// Attaches trusted admitted-request accounting to this origin mutation.
    ///
    /// The stable request identity must equal the transaction operation
    /// identity. This binds idempotent commit replay and the durable usage
    /// fact to one identity. Replication, repair, and internal transactions
    /// do not call this method and therefore never mint origin usage.
    ///
    /// # Errors
    ///
    /// Returns [`UsageCommandError::IdentityMismatch`] when the identities
    /// differ and [`UsageCommandError::InvalidRequest`] when a context was
    /// already attached.
    pub fn attach_usage_admission(
        &mut self,
        context: UsageAdmissionContext,
    ) -> Result<(), UsageCommandError> {
        if context.request_id().as_str() != self.operation.as_str() {
            return Err(UsageCommandError::IdentityMismatch);
        }
        if !self
            .engine
            .usage_owner_matches(context.project_id().as_str(), context.database_id().as_str())
        {
            return Err(UsageCommandError::InvalidRequest);
        }
        if self.usage_admission.is_some() {
            return Err(UsageCommandError::InvalidRequest);
        }
        self.usage_admission = Some(context);
        Ok(())
    }

    /// Binds the serving router's owner and generation to this origin write.
    ///
    /// # Errors
    /// Returns [`EngineError`] when the token is invalid or an admission is
    /// already bound.
    pub fn bind_shard_admission(
        &mut self,
        token: crate::shard_split::ShardAdmissionToken,
    ) -> Result<(), EngineError> {
        if self.shard_admission.is_some() || token.shard_id == 0 {
            return Err(EngineError::InvalidDocument);
        }
        self.shard_admission = Some(token);
        Ok(())
    }

    /// Attaches a verified regional allocation. The allocation is consumed in
    /// the same SQLite transaction as the document and usage fact.
    ///
    /// # Errors
    /// Returns [`UsageCommandError`] when the allocation does not match this
    /// operation or an allocation is already attached.
    pub fn attach_usage_budget(
        &mut self,
        allocation: crate::usage::VerifiedAllocation,
    ) -> Result<(), UsageCommandError> {
        if allocation.grant().pool_id.is_empty() || allocation.grant().database_id.is_empty() {
            return Err(UsageCommandError::IdentityMismatch);
        }
        if self.budget_allocation.is_some() {
            return Err(UsageCommandError::InvalidRequest);
        }
        self.budget_allocation = Some(allocation);
        Ok(())
    }

    /// Reads one document as this transaction's snapshot observed it.
    ///
    /// A staged write is visible to later reads inside the same transaction;
    /// the snapshot itself is never mutated.
    ///
    /// # Errors
    ///
    /// Returns [`EngineError::StorageFailure`] when storage cannot be reached.
    pub fn read(&self, id: &DocumentId) -> Result<Option<Document>, EngineError> {
        self.engine.validate_storage_authorization()?;
        for write in self.writes.iter().rev() {
            if write.document_id() == id {
                return Ok(match write {
                    StagedWrite::Put { document, .. }
                    | StagedWrite::ReplicatedPut { document, .. } => Some(document.clone()),
                    StagedWrite::Delete { .. } | StagedWrite::ReplicatedDelete { .. } => None,
                });
            }
        }
        let storage = self.engine.lock_storage()?;
        Ok(storage.port.read_document_at(self.snapshot_sequence, self.scope.tenant(), id))
    }

    /// Reads one document of one collection as this transaction's snapshot
    /// observed it.
    ///
    /// Storage keys a document by tenant, collection, and identifier, and
    /// nothing forbids reusing one identifier across a tenant's collections,
    /// so this read resolves by the exact key: it answers the copy stored in
    /// the named collection, never whichever copy an identifier-only lookup
    /// reaches first. A staged write is visible to later reads inside the same
    /// transaction; the snapshot itself is never mutated.
    ///
    /// # Errors
    ///
    /// Returns [`EngineError::StorageFailure`] when storage cannot be reached.
    pub fn read_in_collection(
        &self,
        collection: &CollectionName,
        id: &DocumentId,
    ) -> Result<Option<Document>, EngineError> {
        self.engine.validate_storage_authorization()?;
        for write in self.writes.iter().rev() {
            if write.document_id() != id {
                continue;
            }
            // A staged write naming another collection touched a different
            // key, so it cannot answer this read and the scan continues. A
            // delete that named no collection removed an identifier the
            // snapshot did not contain anywhere, so it matches every
            // collection and reads as absent from each of them.
            let touches_this_collection = match write {
                StagedWrite::Put { document, .. } | StagedWrite::ReplicatedPut { document, .. } => {
                    document.collection() == collection
                }
                StagedWrite::Delete { collection: None, .. } => true,
                StagedWrite::Delete { collection: Some(staged), .. }
                | StagedWrite::ReplicatedDelete { collection: staged, .. } => staged == collection,
            };
            if touches_this_collection {
                return Ok(match write {
                    StagedWrite::Put { document, .. }
                    | StagedWrite::ReplicatedPut { document, .. } => Some(document.clone()),
                    StagedWrite::Delete { .. } | StagedWrite::ReplicatedDelete { .. } => None,
                });
            }
        }
        let storage = self.engine.lock_storage()?;
        Ok(storage.port.read_document_in_collection_at(
            self.snapshot_sequence,
            self.scope.tenant(),
            collection,
            id,
        ))
    }

    /// Returns one document's public revision (`<seq>-<version>`) at the
    /// read snapshot without reading its body — the `If-None-Match`
    /// answer. The body blob is never decoded on this path (`S-03`: a 304
    /// costs one revision-column probe). A staged write inside this
    /// transaction answers with the staged document's would-be revision.
    ///
    /// # Errors
    ///
    /// Returns [`EngineError::StorageFailure`] when storage cannot be
    /// reached.
    pub fn revision_in_collection(
        &self,
        collection: &CollectionName,
        id: &DocumentId,
    ) -> Result<Option<String>, EngineError> {
        for write in self.writes.iter().rev() {
            if write.document_id() != id {
                continue;
            }
            match write {
                StagedWrite::Put { document, .. } | StagedWrite::ReplicatedPut { document, .. }
                    if document.collection() == collection =>
                {
                    let version = document.metadata().version().max(1);
                    let sequence = self.snapshot_sequence.get().saturating_add(1);
                    return Ok(Some(format!("{sequence}-{version}")));
                }
                StagedWrite::Delete { collection: Some(staged), .. }
                | StagedWrite::ReplicatedDelete { collection: staged, .. }
                    if staged == collection =>
                {
                    return Ok(None);
                }
                _ => {}
            }
        }
        let storage = self.engine.lock_storage()?;
        Ok(storage.port.document_revision_in_collection_at(
            self.snapshot_sequence,
            self.scope.tenant(),
            collection,
            id,
        ))
    }

    /// Stages one document mutation.
    ///
    /// # Errors
    ///
    /// Returns [`EngineError::InvalidDocument`] when the document exceeds the
    /// accepted structural limits or violates its collection's declared
    /// schema, and [`EngineError::StorageFailure`] when the collection's
    /// schema state cannot be read. A rejected document stages nothing, so
    /// the transaction keeps its prior write set and no partial write
    /// occurs; the typed field-and-kind violation report with the enforced
    /// schema version is available through the family validation read.
    pub fn put(
        &mut self,
        document: Document,
        expected: ExpectedVersion,
    ) -> Result<(), EngineError> {
        if !storage::document_within_limits(&document) {
            return Err(EngineError::InvalidDocument);
        }
        crate::families::schemas::enforce_put(self, &document)?;
        self.writes.push(StagedWrite::Put { document, expected });
        Ok(())
    }

    /// Stages one document deletion.
    ///
    /// # Errors
    ///
    /// Returns [`EngineError::StorageFailure`] when the snapshot view cannot
    /// be read to resolve the document's collection.
    pub fn delete(&mut self, id: DocumentId, expected: ExpectedVersion) -> Result<(), EngineError> {
        let collection = self.read(&id)?.map(|document| document.collection().clone());
        self.writes.push(StagedWrite::Delete { collection, id, expected, ttl_guard: None });
        Ok(())
    }

    /// Stages one collection-scoped document deletion (GAP-Q10).
    ///
    /// Storage keys a document by tenant, collection, and identifier, and
    /// nothing forbids reusing one identifier across a tenant's collections,
    /// so the identifier-only [`Transaction::delete`] resolves a reused
    /// identifier to whichever collection storage reaches first. A
    /// bulk-by-query selection targets one collection by construction, so
    /// its deletions resolve by the exact key: this variant stages the
    /// collection-named delete the storage seam already defines, with no
    /// lookup. The caller owns existence — the bulk bridge reads the target
    /// first and refuses a vanished one rather than staging a phantom
    /// tombstone.
    pub fn delete_in_collection(
        &mut self,
        collection: &CollectionName,
        id: DocumentId,
        expected: ExpectedVersion,
    ) {
        self.writes.push(StagedWrite::Delete {
            collection: Some(collection.clone()),
            id,
            expected,
            ttl_guard: None,
        });
    }

    /// Stages a TTL delete guarded by the exact discovered row identity.
    pub(crate) fn delete_expired_ttl(
        &mut self,
        collection: &CollectionName,
        id: DocumentId,
        expected: ExpectedVersion,
        guard: crate::storage::TtlGuard,
    ) {
        self.writes.push(StagedWrite::Delete {
            collection: Some(collection.clone()),
            id,
            expected,
            ttl_guard: Some(guard),
        });
    }

    /// Stages one replicated record's mutation (D-35): the unconditional
    /// put or tombstone the cross-region tailer applies. Unlike
    /// [`Transaction::put`] this enforces no collection schema — the origin
    /// region validated the write, and a divergent local schema must not
    /// block convergence — but the structural limits still hold: a body
    /// the codec cannot store is never staged.
    ///
    /// # Errors
    ///
    /// Returns [`EngineError::InvalidDocument`] when a put's body is
    /// outside the accepted structural limits.
    pub(crate) fn stage_replicated(
        &mut self,
        record: &ReplicatedRecord,
    ) -> Result<(), EngineError> {
        let write = if record.deleted {
            StagedWrite::ReplicatedDelete {
                collection: record.collection.clone(),
                id: record.id.clone(),
                commit_ulid: record.commit_ulid.clone(),
                hlc: record.hlc,
                origin: record.origin.clone(),
                origin_sequence: record.origin_sequence,
                context: record.context.clone(),
            }
        } else {
            let document =
                Document::new(record.id.clone(), record.collection.clone(), record.body.clone());
            if !storage::document_within_limits(&document) {
                return Err(EngineError::InvalidDocument);
            }
            StagedWrite::ReplicatedPut {
                document,
                commit_ulid: record.commit_ulid.clone(),
                hlc: record.hlc,
                origin: record.origin.clone(),
                origin_sequence: record.origin_sequence,
                context: record.context.clone(),
            }
        };
        self.writes.push(write);
        Ok(())
    }

    /// Publishes this transaction at the requested local durability point.
    ///
    /// One call publishes all staged writes atomically, or reports a typed
    /// rejection with no partial writes, or reports an uncertain outcome that
    /// keeps this transaction's operation identity for durable resolution.
    ///
    /// Concurrent publishers form one group: whichever caller becomes the
    /// batch leader drains every waiting intent into one storage batch, so
    /// the batch's durable append and witness are shared — and the witness
    /// runs outside the storage lock, keeping the location readable while
    /// a wave's sync is in flight. A solo caller is a batch of one and
    /// behaves exactly as a serial commit.
    #[must_use]
    pub fn commit(self, durability: LocalDurability) -> CommitOutcome {
        if self.engine.validate_storage_authorization().is_err() {
            return CommitOutcome::Rejected(CommitError::StorageFailure);
        }
        let engine = self.engine;
        if engine.lifecycle_is_paused(&self.scope).unwrap_or(true) {
            return CommitOutcome::Rejected(CommitError::StorageFailure);
        }
        let started = Instant::now();
        let metrics = Arc::clone(&self.engine.metrics);
        // Keep source authority locked through target publication and sync.
        // Pause/revocation/fencing take its exclusive barrier and therefore
        // cannot race an already staged target commit past their boundary.
        let replicated = self.replication_publication.is_some()
            || self.writes.iter().any(|write| {
                matches!(
                    write,
                    storage::StagedWrite::ReplicatedPut { .. }
                        | storage::StagedWrite::ReplicatedDelete { .. }
                )
            });
        let source_authority = self.source_authority.clone();
        let needs_gate = !self.internal_maintenance || !self.writes.is_empty() || replicated;
        let _source_barrier = match source_authority.as_ref().filter(|_| needs_gate) {
            Some((source, epoch)) => {
                let Ok(guard) = source.origin_commit_barrier.read() else {
                    return CommitOutcome::Rejected(CommitError::StorageFailure);
                };
                let source_scope = source.scope_for(self.scope.tenant());
                let active = source
                    .lifecycle_state(&source_scope)
                    .is_ok_and(|state| !state.paused && !state.access_revoked());
                let placement_matches = match (
                    source.usage_placement(&source_scope),
                    engine.usage_placement(&self.scope),
                ) {
                    (Ok(source), Ok(target)) => source == target,
                    _ => false,
                };
                if (!replicated
                    && (!active
                        || source.origin_epoch() != Ok((*epoch, false))
                        || !placement_matches))
                    || source.validate_storage_authorization().is_err()
                {
                    return CommitOutcome::Rejected(CommitError::Conflict);
                }
                Some(guard)
            }
            None => None,
        };
        let _barrier = if needs_gate {
            match engine.origin_commit_barrier.read() {
                Ok(guard) => Some(guard),
                Err(_) => return CommitOutcome::Rejected(CommitError::StorageFailure),
            }
        } else {
            None
        };
        let outcome = if self.internal_maintenance {
            self.commit_unobserved(durability)
        } else {
            if let Some(token) = self.shard_admission.as_ref() {
                if engine.validate_shard_admission(&self.scope, token, &self.writes).is_err() {
                    return CommitOutcome::Rejected(CommitError::Conflict);
                }
            } else if engine.shard_catalog_exists(&self.scope).unwrap_or(true) {
                return CommitOutcome::Rejected(CommitError::Conflict);
            }
            self.commit_unobserved(durability)
        };
        let metric_result = match &outcome {
            CommitOutcome::Committed(_) => MetricCommitResult::Committed,
            CommitOutcome::Rejected(CommitError::StorageFailure) => MetricCommitResult::Failed,
            CommitOutcome::Rejected(_) => MetricCommitResult::Refused,
            CommitOutcome::Unknown(_) => MetricCommitResult::Unknown,
        };
        metrics.record_commit(metric_result, started.elapsed());
        outcome
    }

    fn commit_unobserved(mut self, durability: LocalDurability) -> CommitOutcome {
        if !self.valid_document_request_binding() || !self.valid_batch_request_binding() {
            return CommitOutcome::Rejected(CommitError::Validation);
        }
        let writes = std::mem::take(&mut self.writes);
        let usage_admission = self.usage_admission.take();
        let budget_allocation = self.budget_allocation.take();
        if usage_admission.is_some() && durability != LocalDurability::Synced {
            return CommitOutcome::Rejected(CommitError::UsageRequiresSynced);
        }
        if !replicated && !writes.is_empty() {
            if self.ensure_primary_file_shard_identity().is_err() {
                return CommitOutcome::Rejected(CommitError::StorageFailure);
            }
        }
        let metadata = std::mem::take(&mut self.metadata);
        let schema_envelopes = std::mem::take(&mut self.schema_envelopes);
        let write_digest =
            storage::transaction_digest(self.scope.tenant(), &writes, &metadata, &schema_envelopes);
        // An origin commit stamps the replication clock (the hybrid
        // logical clock the deterministic merge compares); a replicated
        // intent carries its source stamp on the staged variant.
        let replicated = self.replication_publication.is_some()
            || writes.iter().any(|write| {
                matches!(
                    write,
                    storage::StagedWrite::ReplicatedPut { .. }
                        | storage::StagedWrite::ReplicatedDelete { .. }
                )
            });
        if !replicated
            && (!writes.is_empty() || !metadata.is_empty() || !schema_envelopes.is_empty())
            && self.engine.admit_origin_clock().is_err()
        {
            return CommitOutcome::Rejected(CommitError::Validation);
        }
        let hlc = if replicated { 0 } else { self.engine.tick_replication_clock() };
        let intent = CommitIntent {
            tenant: self.scope.tenant().clone(),
            operation: self.operation.clone(),
            digest: usage_bound_commit_digest(
                write_digest,
                usage_admission.as_ref(),
                budget_allocation.as_ref(),
            ),
            snapshot_sequence: self.snapshot_sequence,
            durability,
            usage_admission,
            budget_allocation,
            internal_maintenance: self.internal_maintenance,
            shard_copy: self.shard_copy,
            expected_origin_epoch: if replicated { None } else { Some(self.expected_origin_epoch) },
            expected_uniform_context: self.expected_uniform_context.take(),
            merge_conflict: self.merge_conflict.clone(),
            replication_fingerprint: self.replication_fingerprint,
            replication_publication: self.replication_publication.take(),
            hlc,
            writes,
            metadata,
            schema_envelopes,
        };
        let engine = self.engine;
        let operation = self.operation.clone();
        let (sender, receiver) = std::sync::mpsc::sync_channel(1);
        let become_leader = {
            let Ok(mut mailbox) = engine.commit_mailbox.lock() else {
                return CommitOutcome::Rejected(CommitError::StorageFailure);
            };
            mailbox.queue.push((intent, sender));
            if mailbox.leader_active {
                false
            } else {
                mailbox.leader_active = true;
                true
            }
        };
        // Wake a coalescing leader at once: waves gather on arrival
        // notifications, not on sleep-slice polls.
        engine.commit_arrivals.notify_all();
        if become_leader {
            engine.drive_as_leader();
        }
        let result = engine.await_commit_answer(receiver);
        if let CommitResult::Committed(receipt) = &result {
            let event = CommitPublicationEvent::new(
                operation.clone(),
                receipt.root.clone(),
                receipt.sequence,
            );
            engine.emit_publication_event(&event);
        }

        match result {
            CommitResult::Committed(receipt) => CommitOutcome::Committed(CommitReceipt::new(
                operation.clone(),
                receipt.sequence,
                receipt.durability,
            )),
            CommitResult::Rejected(reason) => CommitOutcome::Rejected(reason.to_commit_error()),
            CommitResult::Unknown(cause) => {
                CommitOutcome::Unknown(UnknownCommit::new(operation, cause.to_unknown_reason()))
            }
        }
    }

    /// Returns the stable operation identity.
    #[must_use]
    pub fn operation_id(&self) -> &OperationId {
        &self.operation
    }

    /// Returns the staged writes. Test-only, used to compare logical digests.
    #[cfg(test)]
    pub(crate) fn staged_writes(&self) -> Vec<StagedWrite> {
        self.writes.clone()
    }
}

impl Engine {
    fn release_operation(&self, scope: &TenantScope, operation: &OperationId) {
        if let Ok(mut in_flight) = self.in_flight.lock() {
            in_flight.remove(&reservation_key(scope, operation));
        }
    }
}

/// Builds the tenant-scoped reservation key of one operation identity.
fn reservation_key(scope: &TenantScope, operation: &OperationId) -> (String, String) {
    (scope.tenant().as_str().to_owned(), operation.as_str().to_owned())
}

/// How long a waiter waits for its answer before re-checking that a leader
/// exists.
///
/// A handed-off wave's first arrival is briefly leaderless by design (the
/// retiring leader cleared the flag while it was queued); the next arrival —
/// or this check — takes the wave over. A leader mid-drive holds the flag,
/// so the check costs one mailbox peek and no more.
const LEADER_STEAL_AFTER: std::time::Duration = std::time::Duration::from_millis(2);

/// The trusted in-process Engine seam used by the Platform Adapter.
#[allow(dead_code)]
pub(crate) trait EnginePort {
    /// Executes one trusted in-process request.
    fn execute(&self, request: EngineRequest) -> Result<EngineResponse, EngineError>;
}
