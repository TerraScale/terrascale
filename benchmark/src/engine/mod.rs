//! Declaration-only Engine and local commit outcome seam.

#![allow(dead_code)]
#![allow(
    clippy::elidable_lifetime_names,
    clippy::missing_errors_doc,
    clippy::must_use_candidate,
    clippy::needless_pass_by_value,
    clippy::unimplemented
)]

use std::{marker::PhantomData, path::PathBuf};

use crate::{
    families::FamilyStore,
    operations::{EngineRequest, EngineResponse},
    query::QueryStore,
    types::{
        Document, DocumentId, ExpectedVersion, LocalDurability, OperationId, ReadSnapshot,
        StorageSequence, TenantScope,
    },
};

/// An Engine configuration selecting the V2 Storage Format.
#[derive(Clone, Debug)]
pub struct EngineConfig {
    location: PathBuf,
}

impl EngineConfig {
    /// Describes a durable Engine location.
    pub fn durable(location: impl Into<PathBuf>) -> Result<Self, EngineConfigError> {
        let _ = location.into();
        unimplemented!("durable Engine opening belongs to W3-1")
    }
}

/// Configuration validation failures that do not expose physical paths.
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
    /// The location contains another Storage Format.
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
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct CommitReceipt {
    operation_id: OperationId,
    storage_sequence: StorageSequence,
    durability: LocalDurability,
}

impl CommitReceipt {
    /// Returns the stable operation identity.
    pub fn operation_id(&self) -> &OperationId {
        &self.operation_id
    }

    /// Returns the local storage sequence.
    pub fn sequence(&self) -> StorageSequence {
        self.storage_sequence
    }

    /// Returns the local durability point.
    pub fn durability(&self) -> LocalDurability {
        self.durability
    }
}

/// A rejected local commit.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum CommitError {
    /// The write set conflicted with the current view.
    Conflict,
    /// The request was invalid.
    InvalidRequest,
    /// Durable storage failed.
    StorageFailure,
    /// The deadline expired before publication.
    Deadline,
    /// Cancellation was observed before publication.
    Cancelled,
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

/// The reason a commit outcome remains uncertain.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum UnknownReason {
    /// The post-publication synchronization result was lost.
    PostPublicationSync,
    /// The local storage result could not be observed.
    StorageUncertain,
    /// The caller lost the response after submission.
    ResponseLost,
}

/// An uncertain commit retaining its original operation identity.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct UnknownCommit {
    operation_id: OperationId,
    reason: UnknownReason,
}

impl UnknownCommit {
    /// Returns the original operation identity.
    pub fn operation_id(&self) -> &OperationId {
        &self.operation_id
    }

    /// Returns the typed uncertainty reason.
    pub fn reason(&self) -> UnknownReason {
        self.reason
    }
}

/// A durable lookup result for an uncertain operation.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum DurableResolution {
    /// The operation is not yet resolved.
    Pending,
    /// The operation committed locally.
    Committed(CommitReceipt),
    /// The operation was rejected.
    Rejected(CommitError),
    /// The durable ledger retains an uncertain result.
    Unknown(UnknownCommit),
}

/// The database-owned Engine.
pub struct Engine {
    identity: u128,
}

impl Engine {
    /// Opens a durable Engine from a V2 configuration.
    pub fn open(config: EngineConfig) -> Result<Self, EngineOpenError> {
        let _ = config;
        unimplemented!("Engine opening belongs to W3-1")
    }

    /// Returns an immutable tenant-scoped read snapshot.
    pub fn read_snapshot(&self, scope: &TenantScope) -> Result<ReadSnapshot, EngineError> {
        let _ = (self, scope);
        unimplemented!("snapshot behavior belongs to W3-1")
    }

    /// Begins an Engine-borrowing transaction.
    pub fn begin<'engine>(
        &'engine self,
        scope: &TenantScope,
        snapshot: ReadSnapshot,
        operation: OperationId,
    ) -> Result<Transaction<'engine>, EngineError> {
        let _ = (self, scope, snapshot, operation);
        unimplemented!("transaction behavior belongs to W3-1")
    }

    /// Returns a query store borrowed from this Engine.
    pub fn queries<'engine>(&'engine self) -> QueryStore<'engine> {
        let _ = self;
        unimplemented!("query behavior belongs to W3-2")
    }

    /// Returns a family store borrowed from this Engine.
    pub fn families<'engine>(&'engine self) -> FamilyStore<'engine> {
        let _ = self;
        unimplemented!("family behavior belongs to W3-F")
    }

    /// Resolves an uncertain operation using its durable identity.
    pub fn resolve_commit(
        &self,
        scope: &TenantScope,
        operation: OperationId,
    ) -> Result<DurableResolution, EngineError> {
        let _ = (self, scope, operation);
        unimplemented!("commit resolution belongs to W3-1")
    }

    /// Closes the Engine and its durable storage.
    pub fn shutdown(self) -> Result<(), EngineShutdownError> {
        let _ = self;
        unimplemented!("shutdown behavior belongs to W3-1")
    }
}

/// An Engine-borrowing transaction write set.
pub struct Transaction<'engine> {
    engine: PhantomData<&'engine Engine>,
    operation_id: OperationId,
}

impl<'engine> Transaction<'engine> {
    /// Reads one document from the transaction snapshot.
    pub fn read(&self, id: &DocumentId) -> Result<Option<Document>, EngineError> {
        let _ = (self, id);
        unimplemented!("transaction behavior belongs to W3-1")
    }

    /// Stages one document mutation.
    pub fn put(
        &mut self,
        document: Document,
        expected: ExpectedVersion,
    ) -> Result<(), EngineError> {
        let _ = (self, document, expected);
        unimplemented!("transaction behavior belongs to W3-1")
    }

    /// Stages one document deletion.
    pub fn delete(&mut self, id: DocumentId, expected: ExpectedVersion) -> Result<(), EngineError> {
        let _ = (self, id, expected);
        unimplemented!("transaction behavior belongs to W3-1")
    }

    /// Publishes this transaction at the requested local durability point.
    pub fn commit(self, durability: LocalDurability) -> CommitOutcome {
        let _ = (self, durability);
        unimplemented!("commit behavior belongs to W3-1")
    }

    /// Returns the stable operation identity.
    pub fn operation_id(&self) -> &OperationId {
        &self.operation_id
    }
}

/// Internal identity used to reject cross-Engine snapshots.
pub(crate) struct EngineIdentity(pub(crate) u128);

/// The trusted in-process Engine seam used by the Platform Adapter.
pub(crate) trait EnginePort {
    fn execute(&self, request: EngineRequest) -> Result<EngineResponse, EngineError>;
}

#[allow(dead_code)]
fn _keep_internal_identity(_: EngineIdentity) {}
