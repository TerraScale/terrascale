//! The trusted in-process Engine execution behind the Platform adapter.
//!
//! This module implements the crate-private [`EnginePort`] for the durable
//! [`Engine`]: every [`EngineRequest`] the Platform adapter forwards is
//! executed through the same seams any in-process caller uses — one immutable
//! read snapshot per read or query, one Engine transaction per mutation — and
//! every failure crosses back as a typed [`EngineError`]. The port holds no
//! credentials, performs no authorization (PD-2026-08-26-11 keeps this
//! boundary local and trusted), and bypasses no public Engine validation.

#![allow(clippy::doc_markdown)] // TerraScale and AcknowledgementLevel are names

use std::sync::atomic::{AtomicU64, Ordering};

use crate::{
    engine::{CommitError, CommitOutcome, Engine, EngineClosed, EngineError, EnginePort},
    families::{
        FamilyError, FamilyReadContext, FamilyReadRequest, FamilyReadResult, FamilyWriteRequest,
    },
    operations::{
        DocumentReadRequest, DocumentWriteRequest, EngineRequest, EngineResponse, FamilyOperation,
        QueryOperation,
    },
    query::QueryError,
    types::{Cancellation, Deadline, LocalDurability, OperationId, TenantScope},
};

/// The local durability point every public mutation commits at.
///
/// The frozen public request vocabulary carries no per-request durability
/// choice, so the public path commits at the strongest local point;
/// AcknowledgementLevel facts stay with the replication seam.
const PUBLIC_WRITE_DURABILITY: LocalDurability = LocalDurability::Synced;

impl EnginePort for Engine {
    fn execute(&self, request: EngineRequest) -> Result<EngineResponse, EngineError> {
        match request {
            EngineRequest::DocumentRead { scope, request } => {
                execute_document_read(self, &scope, &request)
            }
            EngineRequest::DocumentWrite { scope, request } => {
                execute_document_write(self, &scope, &request)
            }
            EngineRequest::Query { scope, request } => execute_query(self, &scope, &request),
            EngineRequest::Family { scope, request: FamilyOperation::Read(request) } => {
                execute_family_read(self, &scope, request)
            }
            EngineRequest::Family { scope, request: FamilyOperation::Write(request) } => {
                execute_family_write(self, &scope, request)
            }
            EngineRequest::Schema { scope, request } => {
                execute_schema_operation(self, &scope, request)
            }
        }
    }
}

/// Executes one schema-lifecycle operation through the schema seams.
///
/// Every step runs under the authorized [`TenantScope`] the Platform adapter
/// derived: the claim was already checked to name the same tenant, and the
/// family and migration seams repeat the engine-side scope validation on
/// every snapshot and transaction they open. The declare step stages the
/// schema policy through the family write seam — the same path any other
/// family mutation takes — while the migration steps drive the migration
/// model's own step operations, which own their transactions.
fn execute_schema_operation(
    engine: &Engine,
    scope: &TenantScope,
    request: crate::operations::SchemaOperation,
) -> Result<EngineResponse, EngineError> {
    use crate::operations::SchemaOperation;

    let answer = match request {
        SchemaOperation::Declare(request) => {
            let write = crate::families::ValidationWrite::try_new(
                scope.clone(),
                request.collection().clone(),
                request.schema().cloned(),
                request.operation_id().clone(),
                request.deadline(),
                request.cancellation(),
            )
            .map_err(|_| EngineError::InvalidDocument)?;
            let snapshot = engine.read_snapshot(write.tenant_scope())?;
            let mut transaction =
                engine.begin(write.tenant_scope(), snapshot, write.operation_id().clone())?;
            let staged = FamilyWriteRequest::schema_validation_update(write);
            engine.families().stage(&mut transaction, staged).map_err(map_family_error)?;
            commit_response(transaction.commit(PUBLIC_WRITE_DURABILITY))?;
            let (version, rules) = recorded_policy(engine, scope, request.collection())?;
            crate::operations::SchemaResponse::Declared(crate::operations::SchemaDeclared::new(
                version, rules,
            ))
        }
        SchemaOperation::Status(request) => {
            let (version, rules) = recorded_policy(engine, scope, request.collection())?;
            crate::operations::SchemaResponse::Status(crate::operations::SchemaStatus::new(
                version, rules,
            ))
        }
        SchemaOperation::Validate(request) => {
            let result = validation_of(engine, scope, request.collection())?;
            let violations = result
                .policy()
                .map(|policy| crate::families::schemas::violations(policy, request.value()))
                .unwrap_or_default();
            crate::operations::SchemaResponse::Validation(crate::operations::SchemaValidation::new(
                result.enforced_version(),
                violations,
            ))
        }
        SchemaOperation::MigrationStatus(request) => {
            let status = crate::families::migrations::status(engine, scope, request.collection())
                .map_err(map_migration_error)?;
            crate::operations::SchemaResponse::MigrationStatus(status)
        }
        SchemaOperation::MigrationDryRun(request) => {
            let plan = crate::families::migrations::dry_run(engine, scope, request.contract())
                .map_err(map_migration_error)?;
            crate::operations::SchemaResponse::Plan(plan)
        }
        SchemaOperation::MigrationApply(request) => {
            let receipt = crate::families::migrations::apply(
                engine,
                scope,
                request.contract(),
                &dry_run(engine, scope, request.contract())?,
                request.options(),
            )
            .map_err(map_migration_error)?;
            crate::operations::SchemaResponse::Receipt(receipt)
        }
    };
    Ok(EngineResponse::Schema(answer))
}

/// Reads one collection's recorded policy as a schema-surface answer shape.
fn recorded_policy(
    engine: &Engine,
    scope: &TenantScope,
    collection: &crate::types::CollectionName,
) -> Result<(Option<crate::families::SchemaVersion>, usize), EngineError> {
    let snapshot = engine.read_snapshot(scope)?;
    let context = FamilyReadContext::new(scope.clone(), snapshot);
    let request = crate::families::ValidationRead::try_new(
        collection.clone(),
        None,
        Deadline::after(std::time::Duration::from_secs(30))
            .map_err(|_| EngineError::InvalidDocument)?,
        Cancellation::new(),
    )
    .map_err(|_| EngineError::InvalidDocument)?;
    let read = FamilyReadRequest::validation(request);
    let FamilyReadResult::Validation(result) =
        engine.families().read(context, read).map_err(map_family_error)?
    else {
        return Err(EngineError::StorageFailure);
    };
    let (version, rules) = match result.policy() {
        None => (None, 0),
        Some(policy) => (Some(policy.version()), policy.rules().len()),
    };
    Ok((version, rules))
}

/// Reads one collection's full validation result, policy included.
fn validation_of(
    engine: &Engine,
    scope: &TenantScope,
    collection: &crate::types::CollectionName,
) -> Result<crate::families::ValidationResult, EngineError> {
    let snapshot = engine.read_snapshot(scope)?;
    let context = FamilyReadContext::new(scope.clone(), snapshot);
    let request = crate::families::ValidationRead::try_new(
        collection.clone(),
        None,
        Deadline::after(std::time::Duration::from_secs(30))
            .map_err(|_| EngineError::InvalidDocument)?,
        Cancellation::new(),
    )
    .map_err(|_| EngineError::InvalidDocument)?;
    let read = FamilyReadRequest::validation(request);
    match engine.families().read(context, read).map_err(map_family_error)? {
        FamilyReadResult::Validation(result) => Ok(result),
        _ => Err(EngineError::StorageFailure),
    }
}

/// Recomputes the plan an apply must reproduce.
///
/// The migration model's apply requires the plan the caller derived from the
/// same contract; the operation surface derives it here under the same
/// authorized scope, so a caller cannot hand in a plan addressed to another
/// collection or version.
fn dry_run(
    engine: &Engine,
    scope: &TenantScope,
    contract: &crate::families::migrations::SchemaMigration,
) -> Result<crate::families::migrations::MigrationPlan, EngineError> {
    crate::families::migrations::dry_run(engine, scope, contract).map_err(map_migration_error)
}

/// Maps one typed Schema Migration failure onto the Engine error vocabulary.
///
/// The migration model's own typed detail stays at its seam, where its tests
/// prove it; here it collapses to the engine categories the Platform adapter
/// maps. The state refusals — a drive already in progress, a from-version
/// the collection does not record, another contract's manifest — cross as
/// [`EngineError::Conflict`]: they are refusals against the observed state,
/// not malformed requests.
#[allow(clippy::match_same_arms)] // the typed rows stay readable per refusal
fn map_migration_error(error: crate::families::migrations::MigrationError) -> EngineError {
    use crate::families::migrations::MigrationError;
    match error {
        MigrationError::DriveInProgress
        | MigrationError::FromVersionMismatch
        | MigrationError::ManifestMismatch
        | MigrationError::PlanMismatch
        | MigrationError::ScanIncomplete
        | MigrationError::NotInProgress
        | MigrationError::ChecksumMismatch
        | MigrationError::CountMismatch => EngineError::Conflict,
        MigrationError::NotRollbackable => EngineError::Conflict,
        MigrationError::InvalidContract
        | MigrationError::TargetSchemaMalformed
        | MigrationError::InvalidRequest
        | MigrationError::AcknowledgmentRequired
        | MigrationError::TransformRejected
        | MigrationError::TargetViolation
        | MigrationError::MigrationStopped => EngineError::InvalidDocument,
        MigrationError::PageProgressStalled
        | MigrationError::ReservedStateInvalid
        | MigrationError::StorageFailure => EngineError::StorageFailure,
    }
}

/// Reads one document as the scope's freshest snapshot observed it.
///
/// The read rides a never-committed snapshot transaction, mirroring the
/// family read path. The typed request names one collection, and storage
/// keys documents by tenant, collection, and identifier, so the read resolves
/// by that full key: it answers the copy in the requested collection even
/// when another collection of the same tenant reuses the identifier, and
/// answers `None` when the requested collection holds no such document.
fn execute_document_read(
    engine: &Engine,
    scope: &TenantScope,
    request: &DocumentReadRequest,
) -> Result<EngineResponse, EngineError> {
    let snapshot = engine.read_snapshot(scope)?;
    let operation = internal_read_operation()?;
    let transaction = engine.begin(scope, snapshot, operation)?;
    let document = transaction.read_in_collection(request.collection(), request.document_id())?;
    Ok(EngineResponse::Document(document))
}

/// Writes one document through one Engine transaction.
fn execute_document_write(
    engine: &Engine,
    scope: &TenantScope,
    request: &DocumentWriteRequest,
) -> Result<EngineResponse, EngineError> {
    let snapshot = engine.read_snapshot(scope)?;
    let mut transaction = engine.begin(scope, snapshot, request.operation_id().clone())?;
    transaction.put(request.document().clone(), request.expected_version())?;
    commit_response(transaction.commit(PUBLIC_WRITE_DURABILITY))
}

/// Compiles and executes one query against the scope's freshest snapshot.
fn execute_query(
    engine: &Engine,
    scope: &TenantScope,
    request: &QueryOperation,
) -> Result<EngineResponse, EngineError> {
    let snapshot = engine.read_snapshot(scope)?;
    let store = engine.queries();
    let plan = store.compile(request.input().clone()).map_err(map_query_error)?;
    let result = store.execute(&plan, &snapshot).map_err(map_query_error)?;
    Ok(EngineResponse::Query(result))
}

/// Reads one family capability through the family read seam.
fn execute_family_read(
    engine: &Engine,
    scope: &TenantScope,
    request: FamilyReadRequest,
) -> Result<EngineResponse, EngineError> {
    let snapshot = engine.read_snapshot(scope)?;
    let context = FamilyReadContext::new(scope.clone(), snapshot);
    let result = engine.families().read(context, request).map_err(map_family_error)?;
    Ok(EngineResponse::Family(result))
}

/// Stages and commits one family mutation through one Engine transaction.
fn execute_family_write(
    engine: &Engine,
    scope: &TenantScope,
    write: FamilyWriteRequest,
) -> Result<EngineResponse, EngineError> {
    // Defense in depth: the Platform adapter already refused a tenant
    // mismatch before dispatch; the Engine repeats the check before any
    // data access so the port never crosses tenants on its own.
    if write.tenant_scope().tenant() != scope.tenant() {
        return Err(EngineError::SnapshotTenantMismatch);
    }
    // The write carries its own validated scope and the transaction binds
    // to it, so a scope naming another Engine instance is refused here by
    // the Engine's own scope validation rather than by the derived scope.
    let snapshot = engine.read_snapshot(write.tenant_scope())?;
    let mut transaction =
        engine.begin(write.tenant_scope(), snapshot, write.operation_id().clone())?;
    engine.families().stage(&mut transaction, write).map_err(map_family_error)?;
    commit_response(transaction.commit(PUBLIC_WRITE_DURABILITY))
}

/// Publishes one transaction and maps its typed commit outcome.
///
/// A committed outcome and an uncertain outcome both cross as a typed
/// response: an uncertain commit is a durable state the caller resolves by
/// operation identity, not a failure. A typed rejection crosses as the
/// mapped [`EngineError`].
fn commit_response(outcome: CommitOutcome) -> Result<EngineResponse, EngineError> {
    match outcome {
        CommitOutcome::Committed(_) | CommitOutcome::Unknown(_) => {
            Ok(EngineResponse::Commit(outcome))
        }
        CommitOutcome::Rejected(error) => Err(map_commit_error(error)),
    }
}

/// Mints a never-reused internal operation identity for one public read.
fn internal_read_operation() -> Result<OperationId, EngineError> {
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    let index = COUNTER.fetch_add(1, Ordering::Relaxed);
    OperationId::try_new(&format!("platform-read-{}-{index}", std::process::id()))
        .map_err(|_| EngineError::StorageFailure)
}

/// Maps one typed commit rejection onto the Engine error vocabulary.
#[allow(clippy::match_same_arms)] // the conflict rows stay readable per variant
fn map_commit_error(error: CommitError) -> EngineError {
    match error {
        CommitError::Validation => EngineError::InvalidDocument,
        CommitError::Conflict => EngineError::Conflict,
        // The durable ledger already binds this identity to another write
        // set; surfacing the reuse as a conflict keeps the never-retry
        // disposition the commit contract gives it.
        CommitError::OperationIdReuse => EngineError::Conflict,
        CommitError::EngineClosed => EngineError::Closed(EngineClosed),
        CommitError::StorageFailure => EngineError::StorageFailure,
    }
}

/// Maps one typed query failure onto the Engine error vocabulary.
#[allow(clippy::match_same_arms)]
fn map_query_error(error: QueryError) -> EngineError {
    match error {
        QueryError::EngineClosed => EngineError::Closed(EngineClosed),
        QueryError::SnapshotEngineMismatch => EngineError::SnapshotEngineMismatch,
        // Both tenant cases are the engine-enforced tenant isolation: a
        // snapshot or a required index namespace naming another tenant
        // refuses the query before any data access.
        QueryError::SnapshotTenantMismatch => EngineError::SnapshotTenantMismatch,
        QueryError::TenantMismatch => EngineError::SnapshotTenantMismatch,
        // Unreachable through the adapter: both input forms were validated
        // by their own constructors before the operation was built.
        QueryError::InvalidSpec => EngineError::InvalidDocument,
        // A strict staleness policy the index catalog cannot meet is a
        // typed engine failure, never a silently stale answer.
        QueryError::IndexNotReady => EngineError::StorageFailure,
        QueryError::Deadline => EngineError::Deadline,
        QueryError::Cancelled => EngineError::Cancelled,
        // Both include refusals are typed rejections of the query's own
        // include set, not a storage condition: the malformed-request
        // category the adapter maps rejected query rules to.
        QueryError::IncludeCycle | QueryError::IncludeBoundExceeded => EngineError::InvalidDocument,
        QueryError::StorageFailure => EngineError::StorageFailure,
    }
}

/// Maps one typed family failure onto the Engine error vocabulary.
///
/// The family-specific typed detail stays at the family seam, where its own
/// tests prove it; at this port it collapses to the engine categories the
/// Platform adapter maps: tenant isolation, engine binding, conflict,
/// bounds, storage, and the malformed-request category for rejected family
/// rules.
#[allow(clippy::match_same_arms)]
fn map_family_error(error: FamilyError) -> EngineError {
    match error {
        FamilyError::EngineClosed => EngineError::Closed(EngineClosed),
        FamilyError::TenantMismatch => EngineError::SnapshotTenantMismatch,
        FamilyError::SnapshotEngineMismatch | FamilyError::TransactionEngineMismatch => {
            EngineError::SnapshotEngineMismatch
        }
        // Unreachable through the port: every family write transaction
        // begins under the write's own operation identity.
        FamilyError::TransactionIdentityMismatch => EngineError::InvalidDocument,
        FamilyError::InvalidRequest => EngineError::InvalidDocument,
        FamilyError::Conflict => EngineError::Conflict,
        // A stale compare-exchange index and a newer recorded tombstone are
        // write conflicts against the observed view, not malformed requests.
        FamilyError::CompareExchangeIndexStale => EngineError::Conflict,
        FamilyError::TombstoneConflict => EngineError::Conflict,
        FamilyError::Cancelled => EngineError::Cancelled,
        FamilyError::DeadlineExceeded => EngineError::Deadline,
        FamilyError::StorageFailure | FamilyError::ReservedStateInvalid => {
            EngineError::StorageFailure
        }
        FamilyError::DocumentMissing
        | FamilyError::CounterOverflow
        | FamilyError::TimeSeriesArityInvalid
        | FamilyError::TimeSeriesRangeInvalid
        | FamilyError::TimeSeriesPolicyInvalid
        | FamilyError::RevisionReferenceInvalid
        | FamilyError::ValidationPolicyMalformed
        | FamilyError::SchemaViolation
        | FamilyError::AttachmentNameDuplicate
        | FamilyError::LifecycleValueInvalid
        | FamilyError::IdentitySeedInvalid
        | FamilyError::IdentityExhausted => EngineError::InvalidDocument,
    }
}
