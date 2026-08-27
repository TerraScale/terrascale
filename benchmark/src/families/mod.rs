//! The typed Data Family seam.
//!
//! A Data Family is a V1-named group of related capabilities exposed through
//! one closed registry of typed requests and typed results. The seam is
//! logical: family values live in documents of a reserved collection, family
//! writes ride an Engine [`Transaction`], and
//! family reads observe an immutable
//! [`ReadSnapshot`]. Families never commit
//! independently, never touch storage bytes, and never expose a page, frame,
//! codec buffer, or generic byte escape hatch.
//!
//! [`FamilyStore`] is acquired only from a durable
//! [`Engine`] through
//! [`Engine::families`](crate::engine::Engine::families); it has no
//! independent durable or in-memory production constructor. Reads validate
//! the tenant, snapshot, and Engine binding before any family data access,
//! and staged writes are checked against the borrowed transaction's Engine,
//! tenant, and operation identity before they stage anything.
//!
//! W3-F implements the initial typed family behavior. It makes no V1 parity,
//! recovery, replication, multi-region, or performance claim: the family
//! parity rows stay unresolved and later packets own streaming, query and
//! index projection, replication, backup and migration, and Platform
//! authorization.
//!
//! SM-02 adds the Document Schema capability on the accepted
//! [`SchemaValidation`](FamilyKind::SchemaValidation) family: typed, versioned
//! collection schemas enforced on every staged document put at the engine
//! seam. The model, evaluator, JSON Schema subset mapping, and write-path
//! hook live in the crate-private `schemas` module, whose public types
//! ([`SchemaDefinition`], [`SchemaVersion`], [`SchemaFormatError`]) surface
//! here. Schema-free collections keep their exact prior behavior; the schema
//! decision record is `docs/families/schemas.md`.
//!
//! GAP-F01 adds the attachment content plane under the public
//! [`attachments`] module: digest-addressed,
//! deduplicated byte storage for attachment content with bounded
//! segment-by-segment readers, V1-pinned range and disposal semantics, and
//! explicit size limits. The plane records the engine-side halves of the
//! PB-3007/PB-3045 backup and migration obligations; its decision record is
//! `docs/families/attachment-content-plane.md`.

pub mod attachments;
pub mod compare_exchange;
pub mod hilo;
pub mod lifecycle;
pub mod migrations;
mod payloads;
pub mod revisions;
pub(crate) mod schemas;
mod state;
mod store;
#[cfg(test)]
mod tests;

pub use payloads::{
    Aggregation, ArchivalCommand, ArchivalReadRequest, ArchivalReadResult, ArchivalState,
    ArchivalWrite, AttachmentMetadata, AttachmentMetadataResult, AttachmentMutation,
    AttachmentRead, AttachmentWrite, CompareExchangeCondition, CompareExchangeEntry,
    CompareExchangeRead, CompareExchangeValueResult, CompareExchangeWrite, CounterBatchWrite,
    CounterEntry, CounterGroupResult, CounterOperation, CounterRead, IdentityCommand,
    IdentityEntry, IdentityRangeResult, IdentityRead, IdentityWrite, LifecycleCommand,
    LifecycleRead, LifecycleResult, LifecycleWrite, RevisionCommand, RevisionEntry, RevisionPage,
    RevisionPaging, RevisionPolicy, RevisionRead, RevisionWrite, RollupPolicy, SchemaDefinition,
    SchemaFieldType, SchemaRule, SchemaVersion, SchemaViolation, TimeSeriesAppend,
    TimeSeriesBatchWrite, TimeSeriesDelete, TimeSeriesEntry, TimeSeriesPage, TimeSeriesPolicy,
    TimeSeriesRange, TimeSeriesRead, TombstoneCommand, TombstoneEntry, TombstoneRead,
    TombstoneResult, TombstoneWrite, ValidationRead, ValidationResult, ValidationWrite,
    ViolationKind,
};
pub use schemas::SchemaFormatError;

// The counter merge seam of the GAP-F07 decision record
// (`docs/families/counter-merge-encoding.md`): counter records replicate as
// merged state, so the replication apply path consumes the record
// predicate and the value merge at this crate-private surface instead of
// re-deriving the reserved layout.
pub(crate) use state::FamilyMergeError;
pub(crate) use state::{RESERVED_COLLECTION, is_counter_record, merge_counter_values};

use crate::{
    engine::{Engine, EngineError, Transaction},
    types::{Cancellation, Deadline, ReadSnapshot, TenantScope},
};

/// The closed set of V1-named Data Families.
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub enum FamilyKind {
    /// Attachment metadata and mutations.
    Attachments,
    /// Counter groups and batches.
    Counters,
    /// Time-series ranges and batches.
    TimeSeries,
    /// Revisions pages and commands.
    Revisions,
    /// Compare-and-exchange values and conditions.
    CompareExchange,
    /// Identity ranges and allocations.
    Identities,
    /// Expiration-refresh state and updates.
    ExpirationRefresh,
    /// Archival state and updates.
    Archival,
    /// Schema-validation rules and updates.
    SchemaValidation,
    /// Conflict-tombstone state and operations.
    ConflictTombstones,
}

impl FamilyKind {
    /// Returns the reserved record key of this family.
    #[must_use]
    pub fn record_key(self) -> &'static str {
        state::family_key(self)
    }
}

/// Tenant and snapshot context for one family read.
///
/// The tenant must be the tenant bound to the snapshot. [`Self::try_new`]
/// rejects a mismatched pair and [`FamilyStore::read`] repeats the same check
/// before any family data access, including reserved-record lookup.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct FamilyReadContext {
    tenant: TenantScope,
    snapshot: ReadSnapshot,
}

impl FamilyReadContext {
    /// Creates a structural context without performing Engine I/O.
    ///
    /// The context is not validated here; [`FamilyStore::read`] validates the
    /// tenant, snapshot, and Engine binding before any family data access.
    #[must_use]
    pub fn new(tenant: TenantScope, snapshot: ReadSnapshot) -> Self {
        Self { tenant, snapshot }
    }

    /// Creates a context after checking tenant and snapshot identity.
    ///
    /// # Errors
    ///
    /// Returns [`FamilyError::TenantMismatch`] when the tenant scope and the
    /// snapshot name different tenants or different Engines.
    pub fn try_new(tenant: TenantScope, snapshot: ReadSnapshot) -> Result<Self, FamilyError> {
        Self::validate(&tenant, &snapshot)?;
        Ok(Self { tenant, snapshot })
    }

    /// Returns the validated tenant scope.
    #[must_use]
    pub fn tenant(&self) -> &TenantScope {
        &self.tenant
    }

    /// Returns the immutable snapshot this context reads.
    #[must_use]
    pub fn snapshot(&self) -> &ReadSnapshot {
        &self.snapshot
    }

    /// Checks the tenant-scope and snapshot binding.
    fn validate(tenant: &TenantScope, snapshot: &ReadSnapshot) -> Result<(), FamilyError> {
        if tenant.tenant() != snapshot.tenant() {
            return Err(FamilyError::TenantMismatch);
        }
        if tenant.engine_identity() != snapshot.engine_identity() {
            return Err(FamilyError::SnapshotEngineMismatch);
        }
        Ok(())
    }
}

/// The closed set of family read requests.
#[derive(Clone, Debug, PartialEq)]
pub enum FamilyReadRequest {
    /// Read attachment metadata.
    AttachmentMetadata(AttachmentRead),
    /// Read a counter group.
    CounterGroup(CounterRead),
    /// Read a time-series range.
    TimeSeriesRange(TimeSeriesRead),
    /// Read a revision page.
    RevisionPage(RevisionRead),
    /// Read a compare-exchange value.
    CompareExchangeValue(CompareExchangeRead),
    /// Read an identity range.
    IdentityRange(IdentityRead),
    /// Read lifecycle state.
    Lifecycle(LifecycleRead),
    /// Read archival state.
    Archival(ArchivalReadRequest),
    /// Read validation state.
    Validation(ValidationRead),
    /// Read tombstones.
    Tombstones(TombstoneRead),
}

impl FamilyReadRequest {
    /// Wraps an attachment read.
    #[must_use]
    pub fn attachment_metadata(value: AttachmentRead) -> Self {
        Self::AttachmentMetadata(value)
    }

    /// Wraps a counter read.
    #[must_use]
    pub fn counter_group(value: CounterRead) -> Self {
        Self::CounterGroup(value)
    }

    /// Wraps a time-series read.
    #[must_use]
    pub fn time_series_range(value: TimeSeriesRead) -> Self {
        Self::TimeSeriesRange(value)
    }

    /// Wraps a revision read.
    #[must_use]
    pub fn revision_page(value: RevisionRead) -> Self {
        Self::RevisionPage(value)
    }

    /// Wraps a compare-exchange read.
    #[must_use]
    pub fn compare_exchange_value(value: CompareExchangeRead) -> Self {
        Self::CompareExchangeValue(value)
    }

    /// Wraps an identity read.
    #[must_use]
    pub fn identity_range(value: IdentityRead) -> Self {
        Self::IdentityRange(value)
    }

    /// Wraps a lifecycle read.
    #[must_use]
    pub fn lifecycle(value: LifecycleRead) -> Self {
        Self::Lifecycle(value)
    }

    /// Wraps an archival read.
    #[must_use]
    pub fn archival(value: ArchivalReadRequest) -> Self {
        Self::Archival(value)
    }

    /// Wraps a validation read.
    #[must_use]
    pub fn validation(value: ValidationRead) -> Self {
        Self::Validation(value)
    }

    /// Wraps a tombstone read.
    #[must_use]
    pub fn tombstones(value: TombstoneRead) -> Self {
        Self::Tombstones(value)
    }

    /// Returns the request deadline.
    #[must_use]
    pub fn deadline(&self) -> Deadline {
        match self {
            Self::AttachmentMetadata(value) => value.deadline(),
            Self::CounterGroup(value) => value.deadline(),
            Self::TimeSeriesRange(value) => value.deadline(),
            Self::RevisionPage(value) => value.deadline(),
            Self::CompareExchangeValue(value) => value.deadline(),
            Self::IdentityRange(value) => value.deadline(),
            Self::Lifecycle(value) => value.deadline(),
            Self::Archival(value) => value.deadline(),
            Self::Validation(value) => value.deadline(),
            Self::Tombstones(value) => value.deadline(),
        }
    }

    /// Returns a clone of the request cancellation signal.
    #[must_use]
    pub fn cancellation(&self) -> Cancellation {
        match self {
            Self::AttachmentMetadata(value) => value.cancellation(),
            Self::CounterGroup(value) => value.cancellation(),
            Self::TimeSeriesRange(value) => value.cancellation(),
            Self::RevisionPage(value) => value.cancellation(),
            Self::CompareExchangeValue(value) => value.cancellation(),
            Self::IdentityRange(value) => value.cancellation(),
            Self::Lifecycle(value) => value.cancellation(),
            Self::Archival(value) => value.cancellation(),
            Self::Validation(value) => value.cancellation(),
            Self::Tombstones(value) => value.cancellation(),
        }
    }
}

/// The closed set of family write requests.
#[derive(Clone, Debug, PartialEq)]
pub enum FamilyWriteRequest {
    /// Stage an attachment mutation.
    AttachmentMutation(AttachmentWrite),
    /// Stage a counter batch.
    CounterBatch(CounterBatchWrite),
    /// Stage a time-series batch.
    TimeSeriesBatch(TimeSeriesBatchWrite),
    /// Stage a revision command.
    RevisionCommand(RevisionWrite),
    /// Stage a compare-exchange condition.
    CompareExchangeCondition(CompareExchangeWrite),
    /// Stage an identity allocation.
    IdentityAllocation(IdentityWrite),
    /// Stage a lifecycle update.
    LifecycleUpdate(LifecycleWrite),
    /// Stage an archival update.
    ArchivalUpdate(ArchivalWrite),
    /// Stage a schema validation update.
    SchemaValidationUpdate(ValidationWrite),
    /// Stage a tombstone operation.
    TombstoneOperation(TombstoneWrite),
}

impl FamilyWriteRequest {
    /// Wraps an attachment mutation.
    #[must_use]
    pub fn attachment_mutation(value: AttachmentWrite) -> Self {
        Self::AttachmentMutation(value)
    }

    /// Wraps a counter batch.
    #[must_use]
    pub fn counter_batch(value: CounterBatchWrite) -> Self {
        Self::CounterBatch(value)
    }

    /// Wraps a time-series batch.
    #[must_use]
    pub fn time_series_batch(value: TimeSeriesBatchWrite) -> Self {
        Self::TimeSeriesBatch(value)
    }

    /// Wraps a revision command.
    #[must_use]
    pub fn revision_command(value: RevisionWrite) -> Self {
        Self::RevisionCommand(value)
    }

    /// Wraps a compare-exchange condition.
    #[must_use]
    pub fn compare_exchange_condition(value: CompareExchangeWrite) -> Self {
        Self::CompareExchangeCondition(value)
    }

    /// Wraps an identity allocation.
    #[must_use]
    pub fn identity_allocation(value: IdentityWrite) -> Self {
        Self::IdentityAllocation(value)
    }

    /// Wraps a lifecycle update.
    #[must_use]
    pub fn lifecycle_update(value: LifecycleWrite) -> Self {
        Self::LifecycleUpdate(value)
    }

    /// Wraps an archival update.
    #[must_use]
    pub fn archival_update(value: ArchivalWrite) -> Self {
        Self::ArchivalUpdate(value)
    }

    /// Wraps a schema validation update.
    #[must_use]
    pub fn schema_validation_update(value: ValidationWrite) -> Self {
        Self::SchemaValidationUpdate(value)
    }

    /// Wraps a tombstone operation.
    #[must_use]
    pub fn tombstone_operation(value: TombstoneWrite) -> Self {
        Self::TombstoneOperation(value)
    }

    /// Returns the write's validated tenant scope.
    #[must_use]
    pub fn tenant_scope(&self) -> &TenantScope {
        match self {
            Self::AttachmentMutation(value) => value.tenant_scope(),
            Self::CounterBatch(value) => value.tenant_scope(),
            Self::TimeSeriesBatch(value) => value.tenant_scope(),
            Self::RevisionCommand(value) => value.tenant_scope(),
            Self::CompareExchangeCondition(value) => value.tenant_scope(),
            Self::IdentityAllocation(value) => value.tenant_scope(),
            Self::LifecycleUpdate(value) => value.tenant_scope(),
            Self::ArchivalUpdate(value) => value.tenant_scope(),
            Self::SchemaValidationUpdate(value) => value.tenant_scope(),
            Self::TombstoneOperation(value) => value.tenant_scope(),
        }
    }

    /// Returns the write's stable operation identity.
    #[must_use]
    pub fn operation_id(&self) -> &crate::types::OperationId {
        match self {
            Self::AttachmentMutation(value) => value.operation_id(),
            Self::CounterBatch(value) => value.operation_id(),
            Self::TimeSeriesBatch(value) => value.operation_id(),
            Self::RevisionCommand(value) => value.operation_id(),
            Self::CompareExchangeCondition(value) => value.operation_id(),
            Self::IdentityAllocation(value) => value.operation_id(),
            Self::LifecycleUpdate(value) => value.operation_id(),
            Self::ArchivalUpdate(value) => value.operation_id(),
            Self::SchemaValidationUpdate(value) => value.operation_id(),
            Self::TombstoneOperation(value) => value.operation_id(),
        }
    }

    /// Replaces the write's operation identity. Crate-internal: the session
    /// seam (GAP-D01) rebinds deferred commands to the save transaction's
    /// identity before staging, which the family stage contract requires.
    pub(crate) fn rebase_operation(&mut self, operation: crate::types::OperationId) {
        match self {
            Self::AttachmentMutation(value) => value.rebase_operation(operation),
            Self::CounterBatch(value) => value.rebase_operation(operation),
            Self::TimeSeriesBatch(value) => value.rebase_operation(operation),
            Self::RevisionCommand(value) => value.rebase_operation(operation),
            Self::CompareExchangeCondition(value) => value.rebase_operation(operation),
            Self::IdentityAllocation(value) => value.rebase_operation(operation),
            Self::LifecycleUpdate(value) => value.rebase_operation(operation),
            Self::ArchivalUpdate(value) => value.rebase_operation(operation),
            Self::SchemaValidationUpdate(value) => value.rebase_operation(operation),
            Self::TombstoneOperation(value) => value.rebase_operation(operation),
        }
    }

    /// Returns the write's deadline.
    #[must_use]
    pub fn deadline(&self) -> Deadline {
        match self {
            Self::AttachmentMutation(value) => value.deadline(),
            Self::CounterBatch(value) => value.deadline(),
            Self::TimeSeriesBatch(value) => value.deadline(),
            Self::RevisionCommand(value) => value.deadline(),
            Self::CompareExchangeCondition(value) => value.deadline(),
            Self::IdentityAllocation(value) => value.deadline(),
            Self::LifecycleUpdate(value) => value.deadline(),
            Self::ArchivalUpdate(value) => value.deadline(),
            Self::SchemaValidationUpdate(value) => value.deadline(),
            Self::TombstoneOperation(value) => value.deadline(),
        }
    }

    /// Returns a clone of the write's cancellation signal.
    #[must_use]
    pub fn cancellation(&self) -> Cancellation {
        match self {
            Self::AttachmentMutation(value) => value.cancellation(),
            Self::CounterBatch(value) => value.cancellation(),
            Self::TimeSeriesBatch(value) => value.cancellation(),
            Self::RevisionCommand(value) => value.cancellation(),
            Self::CompareExchangeCondition(value) => value.cancellation(),
            Self::IdentityAllocation(value) => value.cancellation(),
            Self::LifecycleUpdate(value) => value.cancellation(),
            Self::ArchivalUpdate(value) => value.cancellation(),
            Self::SchemaValidationUpdate(value) => value.cancellation(),
            Self::TombstoneOperation(value) => value.cancellation(),
        }
    }
}

/// The closed set of family read results.
#[derive(Clone, Debug, PartialEq)]
pub enum FamilyReadResult {
    /// Attachment metadata result.
    AttachmentMetadata(AttachmentMetadataResult),
    /// Counter group result.
    CounterGroup(CounterGroupResult),
    /// Time-series page result.
    TimeSeriesPage(TimeSeriesPage),
    /// Revision page result.
    RevisionPage(RevisionPage),
    /// Compare-exchange result.
    CompareExchangeValue(CompareExchangeValueResult),
    /// Identity range result.
    IdentityRange(IdentityRangeResult),
    /// Lifecycle result.
    Lifecycle(LifecycleResult),
    /// Archival result.
    Archival(ArchivalReadResult),
    /// Validation result.
    Validation(ValidationResult),
    /// Tombstone result.
    Tombstones(TombstoneResult),
}

/// Family seam failures.
///
/// Every case is typed and carries no document value, storage detail, path,
/// credential, or private implementation type. The family-specific rules stay
/// distinct: counter overflow, time-series arity, range, and policy, revision
/// references, validation policy and violations, duplicate attachment names,
/// lifecycle values, compare-exchange indexes, and identity seeding never
/// collapse into one generic case.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum FamilyError {
    /// The Engine has closed.
    EngineClosed,
    /// The context and request use different tenants, or the transaction is
    /// not bound to the request's tenant scope.
    TenantMismatch,
    /// The snapshot belongs to another Engine.
    SnapshotEngineMismatch,
    /// The transaction belongs to another Engine.
    TransactionEngineMismatch,
    /// The write identity does not match the transaction.
    TransactionIdentityMismatch,
    /// The family request is invalid.
    InvalidRequest,
    /// The family operation conflicted with the observed document version.
    Conflict,
    /// The family operation was cancelled.
    Cancelled,
    /// The request deadline elapsed before the family operation ran.
    DeadlineExceeded,
    /// Durable storage returned a typed failure.
    StorageFailure,
    /// The addressed document does not exist in this view.
    DocumentMissing,
    /// A counter increment would overflow the signed 64-bit range.
    CounterOverflow,
    /// A time-series append does not match the series' value arity.
    TimeSeriesArityInvalid,
    /// A time-series range is not valid for the series' policy or request.
    TimeSeriesRangeInvalid,
    /// A time-series retention or rollup policy is malformed.
    TimeSeriesPolicyInvalid,
    /// The referenced revision does not exist.
    RevisionReferenceInvalid,
    /// A schema-validation policy definition is malformed.
    ValidationPolicyMalformed,
    /// The addressed document violates its collection's schema policy.
    SchemaViolation,
    /// An attachment name already exists on the document.
    AttachmentNameDuplicate,
    /// A lifecycle expiry or refresh value is invalid.
    LifecycleValueInvalid,
    /// The compare-exchange index does not match the stored index.
    CompareExchangeIndexStale,
    /// Seeding an identity backwards requires an explicit force.
    IdentitySeedInvalid,
    /// One identity has consumed the representable identity range.
    IdentityExhausted,
    /// A recorded family tombstone is newer than the writer's view.
    TombstoneConflict,
    /// A reserved family record could not be read as family state.
    ReservedStateInvalid,
}

/// A family store borrowed from one Engine.
///
/// The store is bound to its Engine's lifetime and durable open state. It can
/// neither outlive the Engine, open a second storage location, nor switch to
/// an in-memory implementation, and
/// [`Engine::families`](crate::engine::Engine::families) is its only
/// production acquisition path.
///
/// ```compile_fail
/// // No store may outlive its Engine. This must not compile.
/// let engine = terrabase_v2::engine::Engine::open(config)?;
/// let store = engine.families();
/// engine.shutdown()?;
/// let _ = store;
/// ```
///
/// ```compile_fail
/// // There is no independent durable or in-memory store constructor. This
/// // must not compile.
/// let store = terrabase_v2::families::FamilyStore::open("/tmp/other");
/// ```
pub struct FamilyStore<'engine> {
    engine: &'engine Engine,
}

impl<'engine> FamilyStore<'engine> {
    /// Binds one store to one Engine. Crate-only: the durable Engine is the
    /// only production acquisition path.
    pub(crate) fn from_engine(engine: &'engine Engine) -> Self {
        Self { engine }
    }

    /// Enumerates the tenant's family records from the reserved collection
    /// as one immutable snapshot observed them (integrator action W4-CA-5).
    ///
    /// The enumeration feeds backup and migration: it produces the
    /// crate-private [`FamilyRecordInput`](crate::backup::FamilyRecordInput)
    /// — identity, family, value, and published version — so a consumer can
    /// restore family state through the Engine transaction seam without any
    /// knowledge of the reserved collection layout, which stays
    /// family-private. Records arrive in canonical key order; a user
    /// document outside the reserved collection is not a family record and
    /// never appears.
    ///
    /// # Errors
    ///
    /// Returns [`FamilyError::SnapshotEngineMismatch`] when the scope or the
    /// snapshot names another Engine, [`FamilyError::TenantMismatch`] when
    /// the scope and snapshot name different tenants,
    /// [`FamilyError::StorageFailure`] when the snapshot view cannot be read,
    /// and [`FamilyError::ReservedStateInvalid`] when a document occupies the
    /// reserved collection with an identifier that does not parse as a family
    /// record.
    #[allow(dead_code, reason = "consumed by the W4-BACKUP packet; exercised in tests")]
    pub(crate) fn snapshot_family_records(
        &self,
        scope: &TenantScope,
        snapshot: &ReadSnapshot,
    ) -> Result<Vec<crate::backup::FamilyRecordInput>, FamilyError> {
        let documents =
            self.engine.snapshot_documents(scope, snapshot).map_err(|error| match error {
                crate::engine::EngineError::SnapshotEngineMismatch
                | crate::engine::EngineError::ScopeMismatch => FamilyError::SnapshotEngineMismatch,
                crate::engine::EngineError::SnapshotTenantMismatch => FamilyError::TenantMismatch,
                _ => FamilyError::StorageFailure,
            })?;
        let mut records = Vec::new();
        for document in documents {
            if document.collection().as_str() != state::RESERVED_COLLECTION {
                continue;
            }
            let kind =
                state::kind_of_record(document.id()).ok_or(FamilyError::ReservedStateInvalid)?;
            records.push(crate::backup::FamilyRecordInput::new(
                document.id().clone(),
                kind,
                document.value().clone(),
                document.metadata().version(),
            ));
        }
        Ok(records)
    }

    /// Reads one typed family capability from a snapshot context.
    ///
    /// The context's tenant, snapshot, and Engine binding are validated
    /// before any family data access, the read observes the context's
    /// immutable snapshot, and the read transaction is never committed.
    ///
    /// # Errors
    ///
    /// Returns [`FamilyError::TenantMismatch`] when the context's tenant and
    /// snapshot disagree, [`FamilyError::SnapshotEngineMismatch`] when the
    /// snapshot belongs to another Engine, [`FamilyError::DeadlineExceeded`]
    /// or [`FamilyError::Cancelled`] for an elapsed deadline or cancelled
    /// signal, and the family-specific case for a rejected request.
    #[allow(clippy::needless_pass_by_value)] // the frozen contract passes the context by value
    pub fn read(
        &self,
        context: FamilyReadContext,
        request: FamilyReadRequest,
    ) -> Result<FamilyReadResult, FamilyError> {
        FamilyReadContext::validate(context.tenant(), context.snapshot())?;
        if request.deadline().is_expired() {
            return Err(FamilyError::DeadlineExceeded);
        }
        if request.cancellation().is_cancelled() {
            return Err(FamilyError::Cancelled);
        }
        let operation = store::internal_read_operation()?;
        let mut transaction = self
            .engine
            .begin(context.tenant(), context.snapshot().clone(), operation)
            .map_err(|error| map_begin_error(&error))?;
        store::read(&mut transaction, request)
    }

    /// Stages one typed family mutation in an Engine transaction.
    ///
    /// The transaction supplies the single operation identity, snapshot,
    /// conflict check, and commit outcome. The write's operation identity,
    /// the transaction's Engine and tenant binding, and every family rule are
    /// validated before anything is staged, so a rejected request leaves the
    /// caller's transaction unchanged and an abort hides every staged family
    /// change.
    ///
    /// # Errors
    ///
    /// Returns [`FamilyError::TransactionIdentityMismatch`] when the write
    /// names another operation identity, [`FamilyError::TransactionEngineMismatch`]
    /// when the request's tenant scope belongs to another Engine,
    /// [`FamilyError::TenantMismatch`] when the transaction is not live under
    /// the request's tenant scope, [`FamilyError::DeadlineExceeded`] or
    /// [`FamilyError::Cancelled`] for an elapsed deadline or cancelled
    /// signal, and the family-specific case for a rejected write.
    pub fn stage(
        &self,
        transaction: &mut Transaction<'engine>,
        request: FamilyWriteRequest,
    ) -> Result<(), FamilyError> {
        if transaction.operation_id() != request.operation_id() {
            return Err(FamilyError::TransactionIdentityMismatch);
        }
        if request.deadline().is_expired() {
            return Err(FamilyError::DeadlineExceeded);
        }
        if request.cancellation().is_cancelled() {
            return Err(FamilyError::Cancelled);
        }
        self.bind_transaction(transaction, request.tenant_scope())?;
        store::stage(transaction, request)
    }

    /// Proves that the borrowed transaction is live on this store's Engine
    /// under the request's tenant scope.
    ///
    /// The Engine exposes no transaction-identity accessor, so the check uses
    /// the Engine's own operation-reservation oracle: a scope that is not
    /// bound to this Engine fails [`Engine::read_snapshot`], and a live
    /// transaction on this Engine under this scope holds the operation
    /// reservation, so [`Engine::begin`] with the same scope and operation
    /// identity reports [`EngineError::OperationIdReuse`]. The probe begins
    /// no write, commits nothing, and releases its reservation immediately.
    fn bind_transaction(
        &self,
        transaction: &Transaction<'engine>,
        scope: &TenantScope,
    ) -> Result<(), FamilyError> {
        let snapshot = self.engine.read_snapshot(scope).map_err(|error| match error {
            EngineError::ScopeMismatch => FamilyError::TransactionEngineMismatch,
            EngineError::Closed(_) => FamilyError::EngineClosed,
            _ => FamilyError::StorageFailure,
        })?;
        let operation = transaction.operation_id().clone();
        match self.engine.begin(scope, snapshot, operation) {
            Err(EngineError::OperationIdReuse { .. }) => Ok(()),
            Ok(_) => Err(FamilyError::TenantMismatch),
            Err(EngineError::Closed(_)) => Err(FamilyError::EngineClosed),
            Err(_) => Err(FamilyError::StorageFailure),
        }
    }
}

/// Maps Engine transaction-begin failures to their family cases.
#[allow(clippy::match_same_arms)]
fn map_begin_error(error: &EngineError) -> FamilyError {
    match error {
        EngineError::SnapshotEngineMismatch | EngineError::ScopeMismatch => {
            FamilyError::SnapshotEngineMismatch
        }
        EngineError::SnapshotTenantMismatch => FamilyError::TenantMismatch,
        EngineError::Closed(_) => FamilyError::EngineClosed,
        // A read-only probe mints a fresh operation identity for every read,
        // so identity reuse and storage failures both surface as a typed
        // storage failure rather than a family rule.
        EngineError::OperationIdReuse { .. }
        | EngineError::Conflict
        | EngineError::Deadline
        | EngineError::Cancelled
        | EngineError::StorageFailure
        | EngineError::InvalidDocument => FamilyError::StorageFailure,
    }
}
