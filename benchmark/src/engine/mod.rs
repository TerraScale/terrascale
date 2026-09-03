//! The database-owned Engine: documents, transactions, and commit outcomes.
//!
//! The Engine opens one durable location, hands out immutable read snapshots,
//! runs one atomic publication per transaction, and resolves uncertain commits
//! by operation identity from the durable operation ledger. It exposes no
//! in-memory durable constructor: [`Engine::open`] with an
//! [`EngineConfig::durable`] location is the only production path.

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
mod ingest;
mod metadata;
mod patch;

#[cfg(test)]
mod ingest_tests;

#[cfg(test)]
mod metadata_tests;

#[cfg(test)]
mod patch_tests;

#[cfg(test)]
mod tests;

pub use crate::storage::DatabaseOwner;
pub use crate::storage::database_file;
pub use authorized::{AuthorizedEngine, AuthorizedEngineError, AuthorizedSession};
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

use crate::{
    families::FamilyStore,
    observability::{CommitResult as MetricCommitResult, MetricsRegistry},
    operations::{EngineRequest, EngineResponse},
    query::QueryStore,
    storage::{
        self, CommitIntent, CommitPublicationEvent, CommitResult, PublishedCommit, StagedWrite,
        StoragePort, UsageFactPosition, UsageStorageError,
    },
    streaming::StreamStore,
    types::{
        CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion, IndexGenerationSet,
        LocalDurability, OperationId, ReadSnapshot, ReplicationClockStamp, RetryDisposition,
        StorageSequence, TenantId, TenantScope,
    },
    usage::{
        MeasurementWindow, MintingRule, UsageAdmissionContext, UsageCommandError,
        UsageContinuation, UsageFactId, UsagePage, UsagePageRequest, UsageRecordReceipt,
        UsageRecordRequest,
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
        UsageStorageError::UnsupportedBackend => UsageCommandError::UnsupportedBackend,
        UsageStorageError::Corrupt => UsageCommandError::CorruptStorage,
        UsageStorageError::IdentityConflict => UsageCommandError::IdentityConflict,
        UsageStorageError::Unavailable => UsageCommandError::StorageUnavailable,
    }
}

fn usage_bound_commit_digest(base: u64, context: Option<&UsageAdmissionContext>) -> u64 {
    let Some(context) = context else { return base };
    let mut hasher = blake3::Hasher::new();
    hasher.update(b"terrascale.commit.usage-admission.v1\0");
    hasher.update(&base.to_le_bytes());
    hasher.update(&context.digest_bytes());
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

/// Describes a durable Engine location selecting the V2 Storage Format.
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
        })
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
    /// The location holds another storage format, or its recorded bytes do
    /// not verify.
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
    /// A requested scope does not match the snapshot scope.
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
/// This remains crate-private so archive and streaming consumers share the
/// typed fact without exposing storage internals publicly.
#[derive(Clone, Debug, PartialEq)]
pub(crate) struct SnapshotRecord {
    document: Document,
    durability: LocalDurability,
}

impl SnapshotRecord {
    /// Returns the final-state document.
    pub(crate) fn document(&self) -> &Document {
        &self.document
    }

    /// Returns the durability of the write that won at the snapshot boundary.
    pub(crate) fn durability(&self) -> LocalDurability {
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
    /// deterministic operation identity replayed the recorded publication
    /// instead of double-writing.
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

/// The deterministic merge's verdict (D-36): which side of a replicated
/// apply the `(hlc, origin)` total order keeps.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum MergeVerdict {
    /// The incoming record wins and publishes.
    IncomingWins,
    /// The current row wins; the incoming record publishes nothing.
    CurrentWins,
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
/// from the persistent storage-format identity recorded on disk, which detects
/// [`EngineOpenError::FormatMismatch`].
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

/// The database-owned Engine.
///
/// ```compile_fail
/// // There is no in-memory durable constructor. This must not compile.
/// let engine = terrabase::engine::Engine::ephemeral();
/// ```
pub struct Engine {
    identity: u128,
    durable_owner: crate::storage::DatabaseOwner,
    /// The engine's replication clock (D-36): one hybrid stamp per
    /// database. Origin commits tick it; replicated applies witness it.
    /// The deterministic merge compares `(hlc, origin)` totals.
    replication_clock: Mutex<crate::types::ReplicationClockStamp>,
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
    /// The Storage Tier Mover's control state (P4): the configured
    /// policy and the pause gate. The handle is [`Engine::tiers`].
    /// The group-commit mailbox: waiting intents and the leader flag.
    /// Shared through the [`Arc`] so cloned engines form one group.
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

impl Engine {
    /// Opens a durable Engine from a V2 configuration.
    ///
    /// # Errors
    ///
    /// Returns [`EngineOpenError::InvalidConfig`] for an unusable
    /// configuration, [`EngineOpenError::FormatMismatch`] when the location
    /// holds a different storage format or bytes that do not verify, and
    /// [`EngineOpenError::StorageUnavailable`] when the location cannot be
    /// opened.
    #[allow(clippy::needless_pass_by_value)]
    pub fn open(config: EngineConfig) -> Result<Self, EngineOpenError> {
        let location = config.location().clone();
        if location.as_os_str().is_empty() {
            return Err(EngineOpenError::InvalidConfig);
        }
        let owner = crate::storage::load_or_create(
            &location,
            config.owner.as_ref(),
            if config.owner.is_some() {
                crate::storage::OwnershipMode::Production
            } else {
                crate::storage::OwnershipMode::LocalDevelopment
            },
        )
        .map_err(|error| match error {
            crate::storage::OwnershipError::Unavailable => EngineOpenError::StorageUnavailable,
            crate::storage::OwnershipError::InvalidEnvironment
            | crate::storage::OwnershipError::InvalidLocation
            | crate::storage::OwnershipError::ForeignLocation
            | crate::storage::OwnershipError::Corrupt
            | crate::storage::OwnershipError::Mismatch
            | crate::storage::OwnershipError::ModeMismatch => EngineOpenError::FormatMismatch,
        })?;
        let metrics = Arc::new(MetricsRegistry::new());
        // The storage is `SQLite` and only `SQLite` (D-27/D-42): one
        // WAL-mode database file per location. Owner-scoped (production)
        // locations open with the ownership tuple enforced but without
        // encryption at rest until M2-1 lands the per-database key on this
        // substrate.
        let storage: Box<dyn storage::StoragePort> = storage::SqliteStorage::open(&location)
            .map(|engine| Box::new(engine) as Box<dyn storage::StoragePort>)
            .map_err(|error| match error {
                storage::StorageOpenError::FormatMismatch => EngineOpenError::FormatMismatch,
                storage::StorageOpenError::Unavailable => EngineOpenError::StorageUnavailable,
            })?;
        let subscriptions = Arc::new(PublicationRegistry::new());
        // W4-CA-3: register the Engine-owned index catalog's sink through the
        // production registry so index maintenance observes every publication.
        let indexes = Arc::new(crate::index::IndexCatalog::new());
        // IX-03 rider: the catalog measures against the configured lifecycle
        // thresholds from open, never a post-open default.
        indexes.set_lifecycle_thresholds(config.lifecycle_thresholds());
        let index_subscription = subscriptions.subscribe(indexes.sink());
        Ok(Self {
            identity: EngineIdentity::next().0,
            durable_owner: owner,
            replication_clock: Mutex::new(ReplicationClockStamp::new(Self::now_millis(), 0)),
            storage: Arc::new(Mutex::new(StorageState { port: storage })),
            metrics,
            in_flight: Mutex::new(BTreeSet::new()),
            subscriptions,
            indexes,
            _index_subscription: index_subscription,
            bulk_audit: Arc::new(crate::query::bulk::BulkAuditJournal::new()),
            commit_mailbox: Arc::new(Mutex::new(CommitMailbox::default())),
            commit_arrivals: std::sync::Condvar::new(),
        })
    }

    /// Returns the durable database identity bound to this location.
    #[must_use]
    pub fn database_id(&self) -> crate::identity::DatabaseUlid {
        self.durable_owner.database()
    }

    /// Returns the complete durable owner tuple recovered from this location.
    #[must_use]
    pub fn database_owner(&self) -> &crate::storage::DatabaseOwner {
        &self.durable_owner
    }

    /// Returns the bounded registry shared with this Engine's storage path.
    #[must_use]
    pub fn metrics(&self) -> Arc<MetricsRegistry> {
        Arc::clone(&self.metrics)
    }

    /// Drains the group-commit mailbox until empty: one storage batch per
    /// drained group, one answer per waiter.
    ///
    /// The pipelined shape (PG-shaped: appends overlap syncs): a wave is
    /// prepared, appended, and published under the storage lock —
    /// lock-held CPU work plus one `write()`, tens of microseconds — and
    /// the leader then releases the lock and performs the batch's single
    /// `fdatasync` itself, through the Engine's WAL sync handle, before it
    /// answers the wave's waiters. The expensive sync therefore never
    /// holds the storage mutex: the answered writers take it for their
    /// snapshots and staged reads the moment the answers land, instead of
    /// queueing behind a six-millisecond lock hold, so the next wave
    /// re-forms while the leader is still between syncs. `fdatasync` on
    /// the same inode covers every byte appended before it began, which
    /// is the whole ordering argument: a waiter is answered only after a
    /// sync that started after its batch's append returned, so its
    /// receipt still implies its frame is durable.
    ///
    /// A group smaller than the wave width coalesces until the wave is
    /// wide or arrivals go quiet: the mailbox condvar wakes the leader per
    /// arrival, so a dense wave gathers to full width at once while sparse
    /// traffic commits after one quiet gap instead of idling. A serial
    /// caller — a batch of one on a quiescent engine — never asks for the
    /// pipelined shape and keeps the byte-identical synchronous order:
    /// append, sync, flip append, flip sync, then the answer.
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
        /// How long an idle leader lingers for the next wave before it
        /// flushes any deferred flip bytes and steps down. One full writer
        /// turnaround: a saturated run sees its next arrival inside it (the
        /// arrivals carry the deferred flips in the next batch's append),
        /// while a quiet engine pays at most one flush.
        const GROUP_LINGER_WINDOW: std::time::Duration = std::time::Duration::from_millis(2);
        // The wave width to gather toward. It starts at the harvest width
        // and grows with every committed wave, so a saturated run's next
        // wave coalesces to full width instead of splitting at the floor —
        // the quiet gap still ends coalescing early whenever the traffic
        // is genuinely narrower.
        let mut wave_width = GROUP_HARVEST_WIDTH;
        // Whether the last batch may have left deferred flip bytes in
        // storage. Only a pipelined batch does; a leader with nothing to
        // carry has no reason to linger before stepping down.
        let mut carry_pending = false;
        loop {
            let mut batch = self.take_queued_intents();
            if batch.is_empty() {
                if carry_pending && self.wait_for_arrival(GROUP_LINGER_WINDOW) {
                    if std::env::var_os("TERRABASE_TRACE_BATCHES").is_some() {
                        eprintln!("terrabase leader handoff");
                    }
                    // The next wave's first arrival landed. Hand off now,
                    // while it is queued: the next arrival — or the
                    // waiter-side leadership check — takes the whole forming
                    // wave, and this leader returns to commit its own next
                    // intent inside that wave like every other writer
                    // instead of draining it alone at the tail.
                    if let Ok(mut mailbox) = self.commit_mailbox.lock() {
                        mailbox.leader_active = false;
                    }
                    return;
                }
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
            let intents: Vec<storage::CommitIntent> =
                batch.iter().map(|(intent, _)| intent.clone()).collect();
            // Two or more concurrent committers is the pipelined signal:
            // the batch appends and publishes under the lock, and the
            // leader witnesses it outside the lock before answering. A
            // solo batch qualifies only while a group was served recently
            // — a between-waves leader, not a serial caller. A serial
            // caller on a quiescent engine keeps the synchronous order,
            // byte-identical to a direct storage commit.
            carry_pending = false;
            wave_width = wave_width.max(batch.len().saturating_add(1));
            let results = match self.lock_storage() {
                Ok(mut storage) => storage.port.commit_batch(intents, false),
                Err(_) => vec![
                    storage::CommitResult::Rejected(storage::CommitRejection::Storage);
                    batch.len()
                ],
            };
            for ((_, sender), result) in batch.into_iter().zip(results) {
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
    /// The wait is bounded: a handed-off wave's first arrival sits briefly
    /// leaderless by design (the retiring leader stepped down while it was
    /// queued), and this check — or the next arrival — picks the wave up.
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

    /// Waits until an intent arrives or `window` passes.
    ///
    /// Returns whether an arrival landed. A poisoned mailbox lock returns
    /// immediately: the caller treats it as a quiet window and steps down,
    /// which is the honest outcome for a commit path that can no longer
    /// coordinate.
    fn wait_for_arrival(&self, window: std::time::Duration) -> bool {
        let Ok(mailbox) = self.commit_mailbox.lock() else {
            return false;
        };
        if !mailbox.queue.is_empty() {
            return true;
        }
        match self.commit_arrivals.wait_timeout(mailbox, window) {
            Ok((guard, _)) => !guard.queue.is_empty(),
            Err(_) => false,
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
            writes: Vec::new(),
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
    /// to the deterministic operation identity `repl-{commit_ulid}`.
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
    fn now_millis() -> u64 {
        SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map_or(0, |elapsed| u64::try_from(elapsed.as_millis()).unwrap_or(0))
    }

    /// Ticks the replication clock for one origin commit: physical
    /// millis advance, ties increment the logical counter.
    fn tick_replication_clock(&self) -> u64 {
        let mut clock = self.replication_clock.lock().expect("the clock is not poisoned");
        let ticks = Self::now_millis();
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

    /// The deterministic merge's verdict for one incoming record.
    fn merge_verdict(&self, scope: &TenantScope, record: &ReplicatedRecord) -> MergeVerdict {
        let storage = self.lock_storage().expect("the storage lock is live");
        let Some((current_hlc, current_origin, current_deleted)) =
            storage.port.document_provenance_at(scope.tenant(), &record.collection, &record.id)
        else {
            return MergeVerdict::IncomingWins;
        };
        let incoming = (record.hlc, record.origin.as_str());
        let current = (current_hlc, current_origin.as_str());
        match incoming.cmp(&current) {
            std::cmp::Ordering::Greater => MergeVerdict::IncomingWins,
            std::cmp::Ordering::Less => MergeVerdict::CurrentWins,
            // An exact tie resolves remove-wins (D-36): a tombstone
            // beats a live value, and two tombstones agree.
            std::cmp::Ordering::Equal => {
                if record.deleted || !current_deleted {
                    MergeVerdict::IncomingWins
                } else {
                    MergeVerdict::CurrentWins
                }
            }
        }
    }

    /// Applies one replicated record with the deterministic merge
    /// (D-36): the incoming `(hlc, origin)` total order decides against
    /// the current row's stamp — remove-wins on ties — and the clock
    /// witnesses the remote stamp either way. A superseded record (the
    /// current row wins) publishes nothing; the source region's history
    /// keeps the loser and every region picks the same winner.
    ///
    /// # Errors
    ///
    /// Returns the engine's typed failure when the apply cannot run.
    pub fn apply_replicated(
        &self,
        scope: &TenantScope,
        record: &ReplicatedRecord,
    ) -> Result<AppliedReplication, EngineError> {
        let operation = OperationId::try_new(&format!("repl-{}", record.commit_ulid))
            .map_err(|_| EngineError::InvalidDocument)?;
        // The deterministic merge (D-36): the clock always witnesses the
        // remote stamp, then the `(hlc, origin)` total order decides —
        // remove-wins on equal stamps. A superseded record publishes
        // nothing: every region reaches the same verdict, so convergence
        // needs no coordination.
        self.witness_replication_clock(record.hlc);
        if self.merge_verdict(scope, record) == MergeVerdict::CurrentWins {
            return Ok(AppliedReplication::lost_merge());
        }
        let snapshot = self.read_snapshot(scope)?;
        let snapshot_sequence = snapshot.sequence();
        let mut transaction = self.begin(scope, snapshot, operation.clone())?;
        transaction.stage_replicated(record)?;
        match transaction.commit(LocalDurability::Written) {
            CommitOutcome::Committed(receipt) => {
                // A replayed identity resolves to the original publication's
                // sequence — at or below the snapshot taken this call —
                // while a fresh apply allocates one past it.
                let replayed = receipt.sequence() <= snapshot_sequence;
                Ok(AppliedReplication::new(receipt, replayed))
            }
            CommitOutcome::Rejected(error) => Err(match error {
                CommitError::Conflict => EngineError::Conflict,
                CommitError::OperationIdReuse => {
                    EngineError::OperationIdReuse { operation_id: operation }
                }
                _ => EngineError::StorageFailure,
            }),
            // `synchronous=NORMAL` under WAL cannot report an uncertain
            // outcome on this path; treating one as a storage failure
            // keeps the tailer retrying the same deterministic identity.
            CommitOutcome::Unknown(_) => Err(EngineError::StorageFailure),
        }
    }

    /// Runs one storage retention sweep: prune publication history every
    /// consumer frontier has passed, then fold the WAL back into the
    /// database file. Crate-private: the maintenance loop owns the cadence.
    ///
    /// # Errors
    ///
    /// Returns [`EngineError::StorageFailure`] when storage fails the sweep.
    pub(crate) fn run_retention_sweep(&self) -> Result<(), EngineError> {
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

    fn validate_scope(&self, scope: &TenantScope) -> Result<(), EngineError> {
        if scope.engine_identity() != self.identity {
            return Err(EngineError::ScopeMismatch);
        }
        Ok(())
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
    /// This is the durable half of index creation; the auto-index audit and
    /// the S-03 collections route drive it.
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
        if storage.port.servable_history_start().is_some_and(|start| boundary < start) {
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
        let storage = self.lock_storage()?;
        Ok(storage.port.tenant_commits(tenant, after, limit))
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
        Ok(storage.port.committed_tenants())
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
        let storage = self.lock_storage()?;
        Ok(storage.port.tenant_sequence(tenant, at))
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
        Ok(storage.port.servable_history_start())
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
        Ok(storage.port.journaled_high_water())
    }

    /// Returns the number of published commits the publication-history
    /// journal does not serve (W4-CA-4 repair F2's divergence, as a
    /// count).
    ///
    fn lock_storage(&self) -> Result<std::sync::MutexGuard<'_, StorageState>, EngineError> {
        self.storage.lock().map_err(|_| EngineError::StorageFailure)
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
    writes: Vec<StagedWrite>,
}

impl Drop for Transaction<'_> {
    fn drop(&mut self) {
        self.engine.release_operation(&self.scope, &self.operation);
    }
}

impl Transaction<'_> {
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
        if self.usage_admission.is_some() {
            return Err(UsageCommandError::InvalidRequest);
        }
        self.usage_admission = Some(context);
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
        self.writes.push(StagedWrite::Delete { collection, id, expected });
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
        let started = Instant::now();
        let metrics = Arc::clone(&self.engine.metrics);
        let outcome = self.commit_unobserved(durability);
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
        let writes = std::mem::take(&mut self.writes);
        let usage_admission = self.usage_admission.take();
        if usage_admission.is_some() && durability != LocalDurability::Synced {
            return CommitOutcome::Rejected(CommitError::UsageRequiresSynced);
        }
        let write_digest = storage::write_set_digest(self.scope.tenant(), &writes);
        // An origin commit stamps the replication clock (the hybrid
        // logical clock the deterministic merge compares); a replicated
        // intent carries its source stamp on the staged variant.
        let replicated =
            writes.iter().any(|write| matches!(write, storage::StagedWrite::ReplicatedPut { .. }));
        let hlc = if replicated { 0 } else { self.engine.tick_replication_clock() };
        let intent = CommitIntent {
            tenant: self.scope.tenant().clone(),
            operation: self.operation.clone(),
            digest: usage_bound_commit_digest(write_digest, usage_admission.as_ref()),
            snapshot_sequence: self.snapshot_sequence,
            durability,
            usage_admission,
            hlc,
            writes,
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
