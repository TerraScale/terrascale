//! The database-owned Engine: documents, transactions, and commit outcomes.
//!
//! The Engine opens one durable location, hands out immutable read snapshots,
//! runs one atomic publication per transaction, and resolves uncertain commits
//! by operation identity from the durable operation ledger. It exposes no
//! in-memory durable constructor: [`Engine::open`] with an
//! [`EngineConfig::durable`] location is the only production path.

use std::{
    collections::BTreeSet,
    path::PathBuf,
    sync::{
        Arc, Mutex,
        atomic::{AtomicU64, Ordering},
    },
    time::{SystemTime, UNIX_EPOCH},
};

#[cfg(test)]
mod tests;

use crate::{
    families::FamilyStore,
    operations::{EngineRequest, EngineResponse},
    query::QueryStore,
    storage::{self, CommitIntent, CommitPublicationEvent, CommitResult, StagedWrite, StoragePort},
    types::{
        Document, DocumentId, ExpectedVersion, IndexGenerationSet, LocalDurability, OperationId,
        ReadSnapshot, RetryDisposition, StorageSequence, TenantScope,
    },
};

#[cfg(test)]
use crate::storage::FaultPoint;

/// A sink receiving immutable publication events after locks release.
pub(crate) type CommitPublicationSink = Arc<dyn Fn(&CommitPublicationEvent) + Send + Sync>;

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
        Ok(Self { location })
    }

    /// Returns the configured durable location. Crate-only by design: the
    /// physical path never crosses the public seam.
    pub(crate) fn location(&self) -> &PathBuf {
        &self.location
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
    sink: Mutex<Option<CommitPublicationSink>>,
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
        Ok(Self {
            identity: EngineIdentity::next().0,
            storage: Arc::new(Mutex::new(StorageState { port: Box::new(storage) })),
            in_flight: Mutex::new(BTreeSet::new()),
            sink: Mutex::new(None),
        })
    }

    /// Returns the never-reused identity of this Engine instance. Tests and
    /// crate-internal callers read it to forge scopes and snapshots.
    #[cfg(test)]
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
    /// Query behavior belongs to the W3-2 packet; the borrowed store is wired
    /// but not implemented here.
    #[allow(clippy::missing_panics_doc)]
    pub fn queries(&self) -> QueryStore<'_> {
        let _store = QueryStore::from_engine(self);
        unimplemented!("query behavior belongs to W3-2")
    }

    /// Returns a family store borrowed from this Engine.
    ///
    /// Family behavior belongs to the W3-F packet; the borrowed store is wired
    /// but not implemented here.
    #[allow(clippy::missing_panics_doc)]
    pub fn families(&self) -> FamilyStore<'_> {
        let _store = FamilyStore::from_engine(self);
        unimplemented!("family behavior belongs to W3-F")
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

    fn lock_storage(&self) -> Result<std::sync::MutexGuard<'_, StorageState>, EngineError> {
        self.storage.lock().map_err(|_| EngineError::StorageFailure)
    }

    /// Installs a crate-private publication-event sink. Test-only.
    #[cfg(test)]
    pub(crate) fn set_publication_sink(&self, sink: CommitPublicationSink) {
        let mut slot = self.sink.lock().expect("publication sink lock");
        *slot = Some(sink);
    }

    /// Arms one storage fault inside the publication order. Test-only.
    #[cfg(test)]
    pub(crate) fn arm_storage_fault(&self, point: FaultPoint) {
        let mut storage = self.storage.lock().expect("storage lock");
        storage.port.arm_test_fault(point);
    }

    fn emit_publication_event(&self, event: &CommitPublicationEvent) {
        let sink = self.sink.lock().ok().and_then(|slot| slot.as_ref().map(Arc::clone));
        if let Some(sink) = sink {
            sink(event);
        }
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

    /// Stages one document mutation.
    ///
    /// # Errors
    ///
    /// Returns [`EngineError::InvalidDocument`] when the document exceeds the
    /// accepted structural limits.
    pub fn put(
        &mut self,
        document: Document,
        expected: ExpectedVersion,
    ) -> Result<(), EngineError> {
        if !storage::document_within_limits(&document) {
            return Err(EngineError::InvalidDocument);
        }
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
