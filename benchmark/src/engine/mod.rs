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
    time::{SystemTime, UNIX_EPOCH},
};

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

pub use ingest::{
    BulkIngestRequest, IngestBatchReceipt, IngestConflictMode, IngestReceipt, IngestRefusal,
    IngestRefusalReason, IngestStream, IngestTermination, MAX_INGEST_BATCH_DOCUMENTS,
    MAX_INGEST_REFUSAL_DETAILS,
};
pub use metadata::{
    ConditionalLoadError, DocumentHead, DocumentVersionContract, DocumentVersionContractError,
    HeadCondition, MAX_V1_CHANGE_VECTOR_BYTES, MAX_V1_CHANGE_VECTOR_ENTRIES, V1ChangeVector,
    V1ChangeVectorEntry, V1ChangeVectorError, V1ChangeVectorMapping,
};
pub use patch::{
    AddOrPatch, DocumentPatch, MAX_PATCH_FIELD_BYTES, MAX_PATCH_OPERATIONS,
    MAX_PATCH_PATH_SEGMENTS, PatchError, PatchOperation, PatchOutcome, PatchPath, PatchPathSegment,
    apply_add_or_patch, apply_document_patch,
};

use crate::{
    families::FamilyStore,
    operations::{EngineRequest, EngineResponse},
    query::QueryStore,
    replication::{
        DeletionEvidence, ReplicationClockStamp, SharedTombstoneRetention, StampedCommit,
    },
    storage::{
        self, CommitIntent, CommitPublicationEvent, CommitResult, CompactionError,
        CompactionReceipt, CompactionRequest, PublishedCommit, StagedWrite, StoragePort,
    },
    streaming::StreamStore,
    types::{
        CollectionName, Document, DocumentId, ExpectedVersion, IndexGenerationSet, LocalDurability,
        OperationId, ReadSnapshot, RetryDisposition, StorageSequence, TenantId, TenantScope,
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

/// The clock stamp durable retained evidence carries (GAP-S1 D3).
///
/// D-3 stamps are process-local lane state; the retained-evidence store
/// does not persist them, so evidence re-derived from the store carries the
/// origin stamp — the same disclosed degradation post-restart history
/// serving already documents on `StampedCommit`.
const ORIGIN_STAMP: ReplicationClockStamp = ReplicationClockStamp::new(0, 0);

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
/// let engine = terrabase_v2::Engine::new_in_memory();
/// ```
///
/// ```compile_fail
/// // Neither does a memory-backed configuration path. This must not compile.
/// let config = terrabase_v2::EngineConfig::from_memory();
/// ```
#[derive(Clone, Debug)]
pub struct EngineConfig {
    location: PathBuf,
    lifecycle_thresholds: crate::query::optimizer::LifecycleThresholds,
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
        })
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
}

/// Errors encountered while shutting down an Engine.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum EngineShutdownError {
    /// Durable storage failed while closing.
    StorageFailure,
    /// The Engine was already closed.
    AlreadyClosed,
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

/// The locked storage state of one Engine.
struct StorageState {
    port: Box<dyn StoragePort>,
}

/// The database-owned Engine.
///
/// ```compile_fail
/// // There is no in-memory durable constructor. This must not compile.
/// let engine = terrabase_v2::engine::Engine::ephemeral();
/// ```
pub struct Engine {
    identity: u128,
    storage: Arc<Mutex<StorageState>>,
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
        let storage = storage::FileStorage::open(&location).map_err(|error| match error {
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
            storage: Arc::new(Mutex::new(StorageState { port: Box::new(storage) })),
            in_flight: Mutex::new(BTreeSet::new()),
            subscriptions,
            indexes,
            _index_subscription: index_subscription,
            bulk_audit: Arc::new(crate::query::bulk::BulkAuditJournal::new()),
        })
    }

    /// Returns the never-reused identity of this Engine instance. Tests
    /// and crate-internal callers — the index store's rebuild step among
    /// them — read it to forge scopes and snapshots.
    pub(crate) fn identity(&self) -> u128 {
        self.identity
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
    /// production way to obtain a [`StreamStore`](crate::streaming::StreamStore),
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
            Some(storage::OperationResolution::Pending) | None => DurableResolution::Pending,
        })
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
    pub(crate) fn snapshot_documents(
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
    /// The count is derived from the durable operation ledger against the
    /// journal, so it survives reopens and keeps naming every skipped
    /// journal append even after a later successful append heals the
    /// high-water mark. A nonzero count means the journal cannot prove
    /// completeness: a journal-only completeness proof (the SH-03 bucket
    /// migration's confirmation and cutover) must fail closed on it.
    ///
    /// # Errors
    ///
    /// Returns [`EngineError::StorageFailure`] when storage cannot be
    /// reached (repair F5).
    pub(crate) fn journal_divergence(&self) -> Result<u64, EngineError> {
        let storage = self.lock_storage()?;
        Ok(storage.port.journal_divergence())
    }

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
    /// identity and counts. V1 writes one audit line per operation at
    /// scheduling time carrying the query text
    /// (`AbstractDatabaseOperationQueriesHandlerProcessor.cs:55-66`, pinned
    /// V1 tree); the engine journal's content-free shape is the
    /// accepted-difference the decision record names, and durable audit
    /// shipping belongs to the Platform side.
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

    /// Runs one storage compaction pass (GAP-S1, decision record
    /// `docs/storage/gaps1-storage-compaction.md` D1 and D2).
    ///
    /// The pass is an in-band, caller-driven maintenance operation on this
    /// open Engine, exactly like [`Engine::maintain_indexes`]: the host owns
    /// the cadence, the Engine owns the body. It checkpoints the published
    /// root, truncates the write-ahead log the checkpoint replaces, and
    /// reclaims superseded root files; under
    /// [`CompactionPolicy::ReclaimHistory`](crate::storage::CompactionPolicy::ReclaimHistory)
    /// it also gives up publication history below the journal's durable
    /// head after the window's deletion evidence is durably held — or
    /// refuses and discloses, per the decision record's gates. A failed
    /// pass leaves the kill-point state the record's D4 table names:
    /// reopening loses nothing and the pass may be retried.
    ///
    /// # Errors
    ///
    /// Returns [`CompactionError::Storage`] when durable storage failed
    /// inside the pass.
    pub fn compact(
        &self,
        request: CompactionRequest,
    ) -> Result<CompactionReceipt, CompactionError> {
        let mut storage = self.lock_storage().map_err(|_| CompactionError::Storage)?;
        storage.port.compact(request.lower(None)).map(|(receipt, _)| receipt)
    }

    /// Runs one storage compaction pass driving the tombstone retention
    /// watermark (GAP-S1 D5 — the production history-bound declaration
    /// GAP-R02 left un-driven).
    ///
    /// The pass first re-derives the handed watermark from the durable
    /// retained-evidence store — holding every durable deletion fact and
    /// restoring the declared bound with its per-tenant non-deletion facts —
    /// so a restarted process resumes from durable state. When it reclaims
    /// history, it then declares the new bound from the window it just
    /// extracted, with `durably_held = true` backed by the synced store.
    /// The watermark's own state is the observation point for the
    /// declaration (its bound, refusals log, and retained set); the
    /// retained-evidence facts carry the origin clock stamp, the documented
    /// degradation of durable evidence (stamps are process-local).
    ///
    /// # Errors
    ///
    /// Returns [`CompactionError::Storage`] when durable storage failed
    /// inside the pass.
    pub fn compact_with_retention(
        &self,
        request: CompactionRequest,
        retention: &SharedTombstoneRetention,
    ) -> Result<CompactionReceipt, CompactionError> {
        let floor = retention.with_lock(|watermark| watermark.retention_floor());
        let mut storage = self.lock_storage().map_err(|_| CompactionError::Storage)?;
        // Prologue: re-derive the watermark from the durable store before
        // the pass touches anything, so the declaration rests on durable
        // facts even after a restart.
        let durable = storage.port.retained_state();
        if !durable.facts.is_empty() || durable.bound.is_some() {
            let facts: Vec<DeletionEvidence> = durable
                .facts
                .iter()
                .map(|fact| {
                    DeletionEvidence::new(
                        fact.tenant.clone(),
                        fact.collection.clone(),
                        fact.id.clone(),
                        fact.version_at_delete,
                        fact.sequence,
                        fact.operation.clone(),
                        ORIGIN_STAMP,
                        fact.durability,
                    )
                })
                .collect();
            retention.with_lock(|watermark| watermark.hold(&facts));
            if let Some(bound) = durable.bound {
                let head = storage.port.journaled_high_water().unwrap_or_default();
                // A refused restoration (a bound the damaged journal can no
                // longer cover) leaves the watermark's bound unmoved and is
                // recorded in its bounded refusal log — the observation
                // point for declarations — so the engine does not re-report
                // it here.
                let _ = retention.with_lock(|watermark| {
                    watermark.declare_history_bound_retained(
                        bound,
                        head,
                        &durable.non_deletion,
                        true,
                    )
                });
            }
        }
        let (receipt, driver) = storage.port.compact(request.lower(Some(floor)))?;
        // Declaration: the evidence is durable now, so the assertion is
        // honest. A watermark refusal (a regressing bound, a head the
        // damaged journal can no longer cover) is recorded in the
        // watermark's bounded refusal log — the observation point above.
        if let Some(bound) = driver.bound {
            let head = driver.head.unwrap_or_default();
            let window: Vec<StampedCommit> = driver
                .window
                .iter()
                .map(|commit| StampedCommit::new(ORIGIN_STAMP, commit.clone()))
                .collect();
            // A refused declaration (a regressing bound) leaves the
            // watermark's bound unmoved and is recorded in its bounded
            // refusal log — the observation point for declarations — so the
            // engine does not re-report it here.
            let _ = retention.with_lock(|watermark| {
                watermark.declare_history_bound_from_commits(bound, head, &window, true)
            });
        }
        Ok(receipt)
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
    writes: Vec<StagedWrite>,
}

impl Drop for Transaction<'_> {
    fn drop(&mut self) {
        self.engine.release_operation(&self.scope, &self.operation);
    }
}

impl Transaction<'_> {
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
                    StagedWrite::Put { document, .. } => Some(document.clone()),
                    StagedWrite::Delete { .. } => None,
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
                StagedWrite::Put { document, .. } => document.collection() == collection,
                StagedWrite::Delete { collection: None, .. } => true,
                StagedWrite::Delete { collection: Some(staged), .. } => staged == collection,
            };
            if touches_this_collection {
                return Ok(match write {
                    StagedWrite::Put { document, .. } => Some(document.clone()),
                    StagedWrite::Delete { .. } => None,
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

    /// Publishes this transaction at the requested local durability point.
    ///
    /// One call publishes all staged writes atomically, or reports a typed
    /// rejection with no partial writes, or reports an uncertain outcome that
    /// keeps this transaction's operation identity for durable resolution.
    #[must_use]
    pub fn commit(mut self, durability: LocalDurability) -> CommitOutcome {
        let writes = std::mem::take(&mut self.writes);
        let intent = CommitIntent {
            tenant: self.scope.tenant().clone(),
            operation: self.operation.clone(),
            digest: storage::write_set_digest(self.scope.tenant(), &writes),
            snapshot_sequence: self.snapshot_sequence,
            durability,
            writes,
        };
        let engine = self.engine;
        let operation = self.operation.clone();
        let result = {
            let Ok(mut storage) = engine.lock_storage() else {
                return CommitOutcome::Rejected(CommitError::StorageFailure);
            };
            storage.port.commit(intent)
        };
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

/// The trusted in-process Engine seam used by the Platform Adapter.
#[allow(dead_code)]
pub(crate) trait EnginePort {
    /// Executes one trusted in-process request.
    fn execute(&self, request: EngineRequest) -> Result<EngineResponse, EngineError>;
}
