//! Schema Migrations: versioned schema evolution with recovery evidence
//! (SM-03).
//!
//! A [`SchemaMigration`] is the explicit, idempotent engine contract that
//! moves one collection between [`SchemaVersion`](crate::families::SchemaVersion)
//! identities (CONTEXT: Schema Migration): a forward-only version step, a
//! closed list of declarative field transforms, and the target schema the
//! collection validates under afterwards. It moves one collection inside this
//! engine only; `TerraBase` has no cross-engine data transfer contract.
//!
//! The style is declarative-first, and the programmatic escape is the
//! engine's own typed document seam, never an embedded interpreter: the
//! transform vocabulary is closed and auditable
//! ([`FieldTransform`] with explicit coercion contracts), so a dry run can
//! state the whole plan, checksum, and count evidence before anything is
//! applied. A caller that needs logic beyond the vocabulary performs it
//! through typed document writes — ordinary schema-validated puts — outside
//! the migration contract; the engine embeds no callbacks and no code
//! execution. The decision record, with the evidence and the rejected
//! alternatives, is `docs/families/migrations.md`.
//!
//! Drive shape: a migration runs as typed step operations the host drives —
//! [`dry_run`] (plan and validation outcome, mutating nothing), [`begin`]
//! (records the drive), [`apply_page`] (one bounded page of documents, each
//! page its own committed transaction), [`finalize`] (verifies the evidence
//! and seats the version advance), and [`rollback`] (restores a reversible
//! migration page by page). [`apply`] drives the steps to completion. Every
//! step is idempotent and resumable from the durable migration record: a
//! completed migration answers a re-run as a no-op with its recorded
//! receipt, and an interrupted drive resumes from the recorded cursor and
//! checksum chains — transforms are idempotent on their own results, so a
//! re-served page changes nothing.
//!
//! Evidence (recovery-lane vocabulary): the manifest digest identifies the
//! contract; SHA-256 checksum chains over the collection's documents in
//! canonical key order state the pre-state and result-state identities; the
//! counts state documents, changed documents, and per-kind transform
//! applications. Finalize re-derives the result chain from storage and
//! compares it against both the recorded chains and the dry run's stated
//! evidence — a mismatch fails closed with a typed error and nothing is
//! seated. Rollback limits are computed from the transform list, stated in
//! the contract and the receipt, and enforced: a migration containing a
//! destructive transform is refused without the explicit
//! [`MigrationOptions::acknowledge_not_rollbackable`] acknowledgment.
//!
//! While a drive is in progress, the collection's write-path enforcement
//! retargets to the migration's target schema (the evolution hook in
//! `families::schemas`): puts must conform to where the collection is
//! going, and a rollback drive restores under the still-recorded
//! from-version schema. This module makes no recovery, replication,
//! multi-region, or performance claim; the kill-point evidence recorded
//! here is focused in-process runs, and the exclusive recovery lane owns
//! the formal Recovery Receipts.

mod evidence;
pub(crate) mod record;
mod transform;

#[cfg(test)]
mod tests;

use std::collections::BTreeSet;
use std::sync::atomic::{AtomicU64, Ordering};

pub use evidence::{MigrationPlan, MigrationReceipt, PlanDetail, PlanOutcome, PlanProblem};
pub use record::{MigrationStatus, MigrationStopReason};
pub use transform::{
    Coercion, FieldOutcome, FieldTransform, MAX_MIGRATION_TRANSFORMS, RollbackBlockedCause,
    RollbackLimit, SchemaMigration, TransformCounts, TransformLiteral, TransformRejection,
};

use crate::engine::{CommitOutcome, DurableResolution, Engine, EngineError, Transaction};
use crate::families::payloads::SchemaDefinition;
use crate::families::schemas::violations;
use crate::families::state::{Records, ValidationRecord, record_id};
use crate::types::{
    CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion, LocalDurability,
    OperationId, ReadSnapshot, TenantScope,
};

use evidence::DocumentChain;
use record::{
    MigrationRecord, RecordState, ScanCursor, clear_record, read_record, status_of, write_record,
};

/// The bounded number of collection documents one apply or rollback page
/// touches.
///
/// One page is one committed transaction, so a migration's in-flight work is
/// a bounded window of the collection, mirroring the bounded batch rules of
/// the streaming and replication seams. The value follows the catch-up batch
/// bound of the replication plane.
pub const MAX_MIGRATION_PAGE: usize = 1024;

/// The host-facing migration coordinator contract.
///
/// A coordinator owns no schema bytes and has no alternate in-memory state:
/// all three methods below are backed by the collection's durable migration
/// record.  This keeps the `EnginePort`, the member host, and SDK/CLI adapters
/// on one status/dry-run/apply vocabulary while allowing a host to drive
/// pages through [`apply`] without knowing the record layout.
pub trait MigrationCoordinator {
    /// Return the durable state of one collection's migration.
    ///
    /// # Errors
    ///
    /// Returns [`MigrationError::StorageFailure`] when the engine, scope, or
    /// storage fails.
    fn migration_status(
        &self,
        scope: &TenantScope,
        collection: &CollectionName,
    ) -> Result<MigrationStatus, MigrationError>;

    /// Compute a non-mutating, evidence-bearing migration plan.
    ///
    /// # Errors
    ///
    /// Returns [`MigrationError::FromVersionMismatch`],
    /// [`MigrationError::DriveInProgress`],
    /// [`MigrationError::StorageFailure`], or the typed scan failures.
    fn migration_dry_run(
        &self,
        scope: &TenantScope,
        contract: &SchemaMigration,
    ) -> Result<MigrationPlan, MigrationError>;

    /// Apply a ready plan, resuming from the durable cursor after restart.
    ///
    /// # Errors
    ///
    /// Returns every typed step refusal unchanged.
    fn migration_apply(
        &self,
        scope: &TenantScope,
        contract: &SchemaMigration,
        plan: &MigrationPlan,
        options: MigrationOptions,
    ) -> Result<MigrationReceipt, MigrationError>;
}

impl MigrationCoordinator for Engine {
    fn migration_status(
        &self,
        scope: &TenantScope,
        collection: &CollectionName,
    ) -> Result<MigrationStatus, MigrationError> {
        status(self, scope, collection)
    }

    fn migration_dry_run(
        &self,
        scope: &TenantScope,
        contract: &SchemaMigration,
    ) -> Result<MigrationPlan, MigrationError> {
        dry_run(self, scope, contract)
    }

    fn migration_apply(
        &self,
        scope: &TenantScope,
        contract: &SchemaMigration,
        plan: &MigrationPlan,
        options: MigrationOptions,
    ) -> Result<MigrationReceipt, MigrationError> {
        apply(self, scope, contract, plan, options)
    }
}

/// The bounded storage window one scan fetch reads.
const SCAN_WINDOW: u32 = 1024;

/// Options a migration is applied under.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct MigrationOptions {
    /// Acknowledges that a not-rollbackable migration cannot be reversed.
    ///
    /// A contract whose rollback limit is
    /// [`RollbackLimit::NotReversible`] is refused at [`begin`] without
    /// this acknowledgment: the caller states it accepts the documented
    /// rollback limits before anything is applied.
    pub acknowledge_not_rollbackable: bool,
}

/// Schema Migration failures.
///
/// Every case is typed, fail-closed, and content-safe: no case carries a
/// document value, field value, path, credential, or private implementation
/// type.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum MigrationError {
    /// The contract is structurally invalid: a reserved collection, a
    /// non-forward version step, a mismatched target version, too many
    /// transforms, an invalid field name, or a self-rename.
    InvalidContract,
    /// The target schema fails the whole-definition semantics of the schema
    /// model.
    TargetSchemaMalformed,
    /// The request does not hold at this step — a blocked plan passed to
    /// `begin`, a plan addressing another collection or version, or a
    /// scope that names another Engine or tenant than the snapshot.
    InvalidRequest,
    /// The plan was computed for a different contract (manifest mismatch).
    PlanMismatch,
    /// A live migration drive already owns the collection.
    DriveInProgress,
    /// The collection is schema-free or recorded at a version the contract
    /// does not name as its from-version.
    FromVersionMismatch,
    /// A recorded drive exists for a different contract (manifest mismatch).
    ManifestMismatch,
    /// `finalize` was called before the scan completed.
    ScanIncomplete,
    /// A not-rollbackable migration was submitted without the explicit
    /// acknowledgment.
    AcknowledgmentRequired,
    /// `rollback` was called on a not-rollbackable contract.
    NotRollbackable,
    /// No live drive is in the state this operation addresses.
    NotInProgress,
    /// A transform's coercion contract rejects a document value.
    TransformRejected,
    /// The migration drive was durably stopped after a transform or target
    /// validation failure. The source schema remains active until an
    /// operator submits a new valid drive.
    MigrationStopped,
    /// A transformed document violates the target schema.
    TargetViolation,
    /// The recomputed checksum chain differs from the recorded or planned
    /// evidence.
    ChecksumMismatch,
    /// The recomputed document or transform count differs from the recorded
    /// or planned evidence.
    CountMismatch,
    /// The bounded scan could not advance its cursor — an address-space
    /// guard against pathological identifier reuse across collections.
    PageProgressStalled,
    /// A reserved migration or validation record could not be read as its
    /// recorded state.
    ReservedStateInvalid,
    /// Durable storage returned a typed failure.
    StorageFailure,
}

/// One step's observable outcome in the migration drive.
#[derive(Clone, Debug, PartialEq)]
pub enum MigrationStep {
    /// `begin` seated the drive (or found the same drive already seated and
    /// changed nothing).
    Begun,
    /// `apply_page` applied one bounded page.
    PageApplied {
        /// Documents of the collection the page scanned.
        documents: u64,
        /// Documents whose value the page changed.
        transformed: u64,
    },
    /// The scan completed; `finalize` is the next step.
    ScanComplete,
    /// The migration already completed; the recorded receipt is returned
    /// and nothing was staged.
    AlreadyComplete(MigrationReceipt),
}

/// One rollback step's observable outcome.
#[derive(Clone, Debug, PartialEq)]
pub enum RollbackStep {
    /// One bounded rollback page was restored; further pages remain.
    PageRestored {
        /// Documents whose value the page restored.
        restored: u64,
    },
    /// The rollback completed; the migration record is cleared and the
    /// collection enforces its from-version schema again.
    Complete {
        /// Documents the whole rollback restored.
        restored: u64,
    },
}

// ---------------------------------------------------------------------------
// Read operations
// ---------------------------------------------------------------------------

/// Reports one collection's migration state from a never-committed read
/// transaction.
///
/// # Errors
///
/// Returns [`MigrationError::StorageFailure`] when the Engine, scope, or
/// storage fails. An unreadable migration record also fails closed as
/// [`MigrationError::StorageFailure`]: the engine vocabulary has no
/// reserved-state case, a recorded mapping limit.
pub fn status(
    engine: &Engine,
    scope: &TenantScope,
    collection: &CollectionName,
) -> Result<MigrationStatus, MigrationError> {
    let (_snapshot, transaction) = read_transaction(engine, scope, "family-migration-status")?;
    match read_record(&transaction, collection) {
        Ok(None) => Ok(MigrationStatus::None),
        Ok(Some(record)) => Ok(status_of(&record)),
        Err(_) => Err(MigrationError::StorageFailure),
    }
}

/// Computes one migration contract's dry run: the transform plan and the
/// validation outcome, without mutating anything.
///
/// The dry run reads the collection through a never-committed transaction,
/// applies the transforms purely, and validates every result against the
/// target schema. A ready plan carries the evidence a later apply must
/// reproduce — the document count, the source and result checksum chains,
/// and the per-kind transform counts — and a blocked plan carries the typed
/// problems, document by document, in canonical key order.
///
/// # Errors
///
/// Returns [`MigrationError::FromVersionMismatch`] when the collection is
/// schema-free or recorded at a version other than the contract's
/// from-version, [`MigrationError::DriveInProgress`] when a live drive owns
/// the collection, [`MigrationError::StorageFailure`] for engine and
/// storage failures, and the typed scan failures.
pub fn dry_run(
    engine: &Engine,
    scope: &TenantScope,
    contract: &SchemaMigration,
) -> Result<MigrationPlan, MigrationError> {
    let (snapshot, transaction) = read_transaction(engine, scope, "family-migration-dry-run")?;
    if let Some(record) = read_record(&transaction, contract.collection())
        .map_err(|_| MigrationError::StorageFailure)?
        && matches!(record.state, RecordState::InProgress { .. } | RecordState::RollingBack { .. })
    {
        return Err(MigrationError::DriveInProgress);
    }
    let recorded = recorded_schema(&transaction, contract.collection())?;
    match recorded {
        None => return Err(MigrationError::FromVersionMismatch),
        Some(schema) if schema.version() != contract.from_version() => {
            return Err(MigrationError::FromVersionMismatch);
        }
        Some(_) => {}
    }

    let mut source_chain = DocumentChain::new();
    let mut result_chain = DocumentChain::new();
    let mut counts = TransformCounts::default();
    let mut problems: Vec<PlanProblem> = Vec::new();
    scan_collection(
        engine,
        scope,
        &snapshot,
        contract.collection(),
        &ScanPosition::start(),
        usize::MAX,
        |document| {
            source_chain.link_source(document.id(), document.value());
            match apply_with_counts(contract, document.value(), &mut counts) {
                Err((field, reason)) => {
                    problems.push(PlanProblem::new(
                        document.id().clone(),
                        field,
                        PlanDetail::TransformRejected(reason),
                    ));
                }
                Ok((changed, value)) => {
                    result_chain.link_result(document.id(), &value);
                    let found = violations(contract.target(), &value);
                    if !found.is_empty() {
                        let field = found
                            .first()
                            .map_or(String::new(), |violation| violation.field().to_owned());
                        problems.push(PlanProblem::new(
                            document.id().clone(),
                            field,
                            PlanDetail::TargetViolations(found),
                        ));
                    }
                    let _ = changed;
                }
            }
            Ok(())
        },
    )?;

    let manifest = evidence::manifest_digest(contract);
    let outcome = if problems.is_empty() {
        PlanOutcome::Ready {
            documents: source_chain.links(),
            source_digest: hex::encode(source_chain.running()),
            result_digest: hex::encode(result_chain.running()),
            transform_counts: counts,
        }
    } else {
        PlanOutcome::Blocked { problems }
    };
    Ok(MigrationPlan::new(
        manifest,
        contract.collection().clone(),
        contract.from_version(),
        contract.to_version(),
        outcome,
    ))
}

// ---------------------------------------------------------------------------
// Drive operations
// ---------------------------------------------------------------------------

/// Seats one migration drive: records the contract's manifest and target as
/// the collection's in-progress migration.
///
/// Idempotent: a completed drive of the same manifest answers with its
/// recorded receipt and stages nothing, a seated drive of the same manifest
/// changes nothing, and any other recorded drive refuses with a typed
/// error. The plan must be ready and address this contract.
///
/// # Errors
///
/// Returns the typed refusal for a blocked or mismatched plan, a version
/// mismatch, a recorded different manifest, a missing acknowledgment for a
/// not-rollbackable contract, or engine and storage failures.
pub fn begin(
    engine: &Engine,
    scope: &TenantScope,
    contract: &SchemaMigration,
    plan: &MigrationPlan,
    options: MigrationOptions,
) -> Result<MigrationStep, MigrationError> {
    let manifest = evidence::manifest_digest(contract);
    check_plan(contract, plan, &manifest)?;

    let snapshot = fresh_snapshot(engine, scope)?;
    let operation = unique_operation("family-migration-begin");
    let mut transaction = engine
        .begin(scope, snapshot.clone(), operation.clone())
        .map_err(|error| map_engine_error(&error))?;
    if let Some(record) = read_record(&transaction, contract.collection())
        .map_err(|_| MigrationError::StorageFailure)?
    {
        match &record.state {
            RecordState::Complete { receipt } => {
                if receipt.manifest() == manifest {
                    return Ok(MigrationStep::AlreadyComplete(receipt.clone()));
                }
                // A completed drive of a different manifest may be
                // superseded: the version checks below decide whether this
                // contract is the collection's next sanctioned step.
            }
            RecordState::InProgress { .. } | RecordState::RollingBack { .. } => {
                return if record.manifest == manifest {
                    Ok(MigrationStep::Begun)
                } else {
                    Err(MigrationError::ManifestMismatch)
                };
            }
            // A stopped drive is terminal for page/finalize calls, but it is
            // not ownership of the collection. A fresh dry-run and begin may
            // retry the source version after the caller changes the contract
            // or repairs the offending data.
            RecordState::Stopped { .. } => {}
        }
    }
    let recorded = recorded_schema(&transaction, contract.collection())?;
    let Some(recorded) = recorded else {
        return Err(MigrationError::FromVersionMismatch);
    };
    if recorded.version() == contract.to_version() && recorded == *contract.target() {
        // The collection already sits at the contract's target: the
        // migration is an idempotent no-op. The receipt states the observed
        // evidence honestly and stages nothing.
        return at_target_receipt(engine, scope, &snapshot, contract, options);
    }
    if recorded.version() != contract.from_version() {
        return Err(MigrationError::FromVersionMismatch);
    }
    if contract.rollback_limit() != RollbackLimit::Reversible
        && !options.acknowledge_not_rollbackable
    {
        return Err(MigrationError::AcknowledgmentRequired);
    }
    let record = MigrationRecord {
        manifest,
        from: contract.from_version(),
        to: contract.to_version(),
        state: RecordState::InProgress {
            cursor: None,
            exhausted: false,
            target: contract.target().clone(),
        },
        documents: 0,
        transformed: 0,
        counts: TransformCounts::default(),
        source_chain: DocumentChain::new(),
        result_chain: DocumentChain::new(),
        pages: 0,
        acknowledged: options.acknowledge_not_rollbackable,
    };
    write_record(&mut transaction, contract.collection(), &record)
        .map_err(|_| MigrationError::StorageFailure)?;
    commit_step(engine, scope, transaction, operation)?;
    Ok(MigrationStep::Begun)
}

/// Applies one bounded page of the seated migration.
///
/// Each page is one committed transaction: it reads the drive's recorded
/// cursor and chains, scans forward to the next bounded set of collection
/// documents, applies the transforms, validates every result against the
/// target schema before staging anything, links the checksum chains, and
/// records the advanced cursor. An interruption between pages loses nothing:
/// the next call resumes from the recorded step, and transforms are
/// idempotent on their own results, so a re-served document is unchanged.
///
/// # Errors
///
/// Returns [`MigrationError::NotInProgress`] without a seated drive,
/// [`MigrationError::ManifestMismatch`] against another contract's drive,
/// the typed transform and target refusals, and engine and storage
/// failures.
pub fn apply_page(
    engine: &Engine,
    scope: &TenantScope,
    contract: &SchemaMigration,
    plan: &MigrationPlan,
) -> Result<MigrationStep, MigrationError> {
    let manifest = evidence::manifest_digest(contract);
    check_plan(contract, plan, &manifest)?;

    let snapshot = fresh_snapshot(engine, scope)?;
    let operation = unique_operation("family-migration-page");
    let mut transaction = engine
        .begin(scope, snapshot.clone(), operation.clone())
        .map_err(|error| map_engine_error(&error))?;
    let Some(mut record) = read_record(&transaction, contract.collection())
        .map_err(|_| MigrationError::StorageFailure)?
    else {
        return Err(MigrationError::NotInProgress);
    };
    if record.manifest != manifest {
        return Err(MigrationError::ManifestMismatch);
    }
    let (cursor, exhausted, target) = match &record.state {
        RecordState::Complete { receipt } => {
            return Ok(MigrationStep::AlreadyComplete(receipt.clone()));
        }
        RecordState::RollingBack { .. } => return Err(MigrationError::NotInProgress),
        RecordState::Stopped { .. } => return Err(MigrationError::MigrationStopped),
        RecordState::InProgress { cursor, exhausted, target } => {
            (cursor.clone(), *exhausted, target.clone())
        }
    };
    if exhausted {
        return Ok(MigrationStep::ScanComplete);
    }

    // Keep the last committed evidence separate from the speculative page.
    // If scanning stops on one document, no partial page facts are durable.
    let committed_record = record.clone();
    let mut staged: Vec<(DocumentId, DocumentValue)> = Vec::new();
    let mut page_documents: u64 = 0;
    let mut page_transformed: u64 = 0;
    let next = match scan_collection(
        engine,
        scope,
        &snapshot,
        contract.collection(),
        &ScanPosition { cursor, exhausted: false },
        MAX_MIGRATION_PAGE,
        |document| {
            record.source_chain.link_source(document.id(), document.value());
            match apply_with_counts(contract, document.value(), &mut record.counts) {
                Err(_) => return Err(MigrationError::TransformRejected),
                Ok((changed, value)) => {
                    record.result_chain.link_result(document.id(), &value);
                    if !violations(&target, &value).is_empty() {
                        return Err(MigrationError::TargetViolation);
                    }
                    if changed {
                        page_transformed += 1;
                        staged.push((document.id().clone(), value));
                    }
                }
            }
            page_documents += 1;
            record.documents += 1;
            Ok(())
        },
    ) {
        Ok(next) => next,
        Err(error @ (MigrationError::TargetViolation | MigrationError::TransformRejected)) => {
            let reason = if error == MigrationError::TargetViolation {
                MigrationStopReason::TargetViolation
            } else {
                MigrationStopReason::TransformRejected
            };
            let mut stopped = committed_record;
            stopped.state = RecordState::Stopped { reason };
            write_record(&mut transaction, contract.collection(), &stopped)
                .map_err(|_| MigrationError::StorageFailure)?;
            commit_step(engine, scope, transaction, operation)?;
            return Err(error);
        }
        Err(error) => return Err(error),
    };

    // Stage the page's transformed documents first: while the record still
    // reads in-progress inside this transaction, the write-path hook
    // enforces the migration's target on every put — the fence is the
    // validation, not a bypass of it.
    let collection = contract.collection().clone();
    for (id, value) in staged {
        let document = Document::new(id, collection.clone(), value);
        transaction.put(document, ExpectedVersion::Any).map_err(|error| map_put_error(&error))?;
    }
    record.transformed += page_transformed;
    record.pages += 1;
    record.state =
        RecordState::InProgress { cursor: next.cursor, exhausted: next.exhausted, target };
    write_record(&mut transaction, contract.collection(), &record)
        .map_err(|_| MigrationError::StorageFailure)?;
    commit_step(engine, scope, transaction, operation)?;
    if next.exhausted {
        Ok(MigrationStep::ScanComplete)
    } else {
        Ok(MigrationStep::PageApplied { documents: page_documents, transformed: page_transformed })
    }
}

/// Verifies the migration's evidence and seats the version advance.
///
/// Finalize re-derives the collection's result checksum chain from storage
/// and compares it, the document count, and the per-kind transform counts
/// against both the recorded drive evidence and the dry run's stated plan.
/// Any mismatch fails closed with a typed error and nothing is seated. On
/// agreement, one transaction swaps the collection's recorded schema to the
/// target — the sanctioned forward-only version advance — and records the
/// completed drive with its receipt.
///
/// Idempotent: a completed drive of the same manifest returns its recorded
/// receipt and stages nothing.
///
/// # Errors
///
/// Returns the typed evidence mismatches, [`MigrationError::ScanIncomplete`]
/// before the scan completed, the step-state refusals, and engine and
/// storage failures.
pub fn finalize(
    engine: &Engine,
    scope: &TenantScope,
    contract: &SchemaMigration,
    plan: &MigrationPlan,
) -> Result<MigrationReceipt, MigrationError> {
    let manifest = evidence::manifest_digest(contract);
    check_plan(contract, plan, &manifest)?;
    let PlanOutcome::Ready {
        documents: planned_documents,
        source_digest: planned_source,
        result_digest: planned_result,
        transform_counts: planned_counts,
    } = plan.outcome()
    else {
        return Err(MigrationError::InvalidRequest);
    };

    let snapshot = fresh_snapshot(engine, scope)?;
    let operation = unique_operation("family-migration-finalize");
    let mut transaction = engine
        .begin(scope, snapshot.clone(), operation.clone())
        .map_err(|error| map_engine_error(&error))?;
    let Some(mut record) = read_record(&transaction, contract.collection())
        .map_err(|_| MigrationError::StorageFailure)?
    else {
        return Err(MigrationError::NotInProgress);
    };
    if record.manifest != manifest {
        return Err(MigrationError::ManifestMismatch);
    }
    match &record.state {
        RecordState::Complete { receipt } => return Ok(receipt.clone()),
        RecordState::RollingBack { .. } => return Err(MigrationError::NotInProgress),
        RecordState::Stopped { .. } => return Err(MigrationError::MigrationStopped),
        RecordState::InProgress { exhausted: false, .. } => {
            return Err(MigrationError::ScanIncomplete);
        }
        RecordState::InProgress { .. } => {}
    }

    // Evidence pass: re-derive the result chain from storage. The scan runs
    // at this transaction's snapshot, so it observes every committed page.
    let mut verification = DocumentChain::new();
    scan_collection(
        engine,
        scope,
        &snapshot,
        contract.collection(),
        &ScanPosition::start(),
        usize::MAX,
        |document| {
            verification.link_result(document.id(), document.value());
            Ok(())
        },
    )?;
    if verification.links() != record.documents || record.documents != *planned_documents {
        return Err(MigrationError::CountMismatch);
    }
    let verification_digest = hex::encode(verification.running());
    if verification_digest != hex::encode(record.result_chain.running())
        || verification_digest != *planned_result
        || hex::encode(record.source_chain.running()) != *planned_source
    {
        return Err(MigrationError::ChecksumMismatch);
    }
    if record.counts != *planned_counts {
        return Err(MigrationError::CountMismatch);
    }

    // The recorded schema must still sit at the from-version: a concurrent
    // direct declaration is caught here, never silently overwritten.
    let recorded = recorded_schema(&transaction, contract.collection())?;
    match recorded {
        Some(schema) if schema.version() == contract.from_version() => {}
        _ => return Err(MigrationError::FromVersionMismatch),
    }
    let policy_id = record_id("validation", &[contract.collection().as_str()])
        .map_err(|_| MigrationError::StorageFailure)?;
    let mut records = Records::new(&mut transaction);
    records
        .write(policy_id, ValidationRecord { schema: contract.target().clone() }.encode())
        .map_err(|_| MigrationError::StorageFailure)?;

    let receipt = MigrationReceipt::new(
        contract.collection().clone(),
        manifest,
        contract.from_version(),
        contract.to_version(),
        hex::encode(record.source_chain.running()),
        hex::encode(record.result_chain.running()),
        record.documents,
        record.transformed,
        record.counts,
        record.pages,
        contract.rollback_limit(),
        record.acknowledged,
        false,
    );
    record.state = RecordState::Complete { receipt: receipt.clone() };
    write_record(&mut transaction, contract.collection(), &record)
        .map_err(|_| MigrationError::StorageFailure)?;
    commit_step(engine, scope, transaction, operation)?;
    Ok(receipt)
}

/// Stages rollback ownership alongside the caller's durable recovery intent.
///
/// This changes no documents. The caller must commit the transaction before
/// driving [`rollback`]. A repeated call retains the last durable rollback
/// cursor. Forward page operations refuse this marker, so an API envelope and
/// its direction change can become visible atomically.
///
/// # Errors
///
/// Refuses an absent or completed drive, a different manifest, irreversible
/// transforms, or unavailable durable state.
pub fn stage_rollback(
    transaction: &mut Transaction<'_>,
    contract: &SchemaMigration,
) -> Result<(), MigrationError> {
    if contract.inverse_transforms().is_none() {
        return Err(MigrationError::NotRollbackable);
    }
    let Some(mut record) = read_record(transaction, contract.collection())
        .map_err(|_| MigrationError::StorageFailure)?
    else {
        return Err(MigrationError::NotInProgress);
    };
    if record.manifest != evidence::manifest_digest(contract) {
        return Err(MigrationError::ManifestMismatch);
    }
    match record.state {
        RecordState::Complete { .. } => return Err(MigrationError::NotInProgress),
        RecordState::RollingBack { .. } => return Ok(()),
        RecordState::InProgress { .. } | RecordState::Stopped { .. } => {}
    }
    record.state = RecordState::RollingBack { cursor: None, exhausted: false };
    write_record(transaction, contract.collection(), &record)
        .map_err(|_| MigrationError::StorageFailure)
}

/// Restores one bounded page of a seated, reversible migration.
///
/// Rollback applies the contract's exact inverse transform list — the
/// reversed transforms with every direction swapped — under the collection's
/// still-recorded from-version schema, one bounded page per call. When the
/// scan completes, the migration record is cleared: the collection returns
/// to its pre-migration state, enforcing the from-version schema, with no
/// completed-migration receipt (a rollback leaves no completed drive).
/// Rollback is refused after finalize: the recorded schema has advanced, and
/// version identity never moves backward.
///
/// # Errors
///
/// Returns [`MigrationError::NotInProgress`] without a seated drive,
/// [`MigrationError::NotRollbackable`] for a contract whose rollback limit
/// is not reversible, [`MigrationError::ManifestMismatch`] against another
/// contract's drive, and the typed transform refusals.
pub fn rollback(
    engine: &Engine,
    scope: &TenantScope,
    contract: &SchemaMigration,
) -> Result<RollbackStep, MigrationError> {
    let Some(inverse) = contract.inverse_transforms() else {
        return Err(MigrationError::NotRollbackable);
    };
    let manifest = evidence::manifest_digest(contract);

    let snapshot = fresh_snapshot(engine, scope)?;
    let operation = unique_operation("family-migration-rollback");
    let mut transaction = engine
        .begin(scope, snapshot.clone(), operation.clone())
        .map_err(|error| map_engine_error(&error))?;
    let Some(mut record) = read_record(&transaction, contract.collection())
        .map_err(|_| MigrationError::StorageFailure)?
    else {
        return Err(MigrationError::NotInProgress);
    };
    if record.manifest != manifest {
        return Err(MigrationError::ManifestMismatch);
    }
    let (cursor, exhausted) = match &record.state {
        RecordState::Complete { .. } => return Err(MigrationError::NotInProgress),
        // A forward drive entering rollback restarts the scan from the
        // beginning: every document of the collection is a rollback
        // candidate, and the inverse transforms skip restored documents
        // idempotently.
        RecordState::InProgress { .. } | RecordState::Stopped { .. } => (None, false),
        RecordState::RollingBack { cursor, exhausted } => (cursor.clone(), *exhausted),
    };
    let mut staged: Vec<(DocumentId, DocumentValue)> = Vec::new();
    let mut restored: u64 = 0;
    let next = scan_collection(
        engine,
        scope,
        &snapshot,
        contract.collection(),
        &ScanPosition { cursor, exhausted },
        MAX_MIGRATION_PAGE,
        |document| {
            let mut value = document.value().clone();
            let mut changed = false;
            for transform in &inverse {
                match transform.apply(&value) {
                    FieldOutcome::Unchanged => {}
                    FieldOutcome::Changed(next_value) => {
                        value = next_value;
                        changed = true;
                    }
                    // An inverse rejection names a genuine ambiguity — for
                    // example a rename whose destination was reoccupied by
                    // a concurrent write. Restoring past it could lose data,
                    // so the rollback fails closed and leaves the drive
                    // seated for the caller to resolve.
                    FieldOutcome::Rejected(_) => return Err(MigrationError::TransformRejected),
                }
            }
            if changed {
                restored += 1;
                staged.push((document.id().clone(), value));
            }
            Ok(())
        },
    )?;

    // Stage the drive-record change once, before the document puts: the
    // write-path hook reads the record through this transaction, so while
    // the record reads rolling-back — or absent on the completing page,
    // where the clear is staged ahead of the puts — the hook falls through
    // to the still-recorded from-version schema, the contract restoring
    // documents must satisfy, never the target. One transaction never
    // writes the same record twice: a page either updates the record or
    // clears it, because staging both would target the record twice. The
    // non-final page's record carries the page's final cursor state.
    let collection = contract.collection().clone();
    if next.exhausted {
        // Final rollback page: the collection is back to its from-version
        // state; the restoring puts and the record's clear share one
        // commit, so an interrupted final page either lands whole or is
        // re-served whole.
        clear_record(&mut transaction, contract.collection())
            .map_err(|_| MigrationError::StorageFailure)?;
    } else {
        // A later page continues; the record keeps the drive seated and
        // carries the page's final cursor state.
        record.state = RecordState::RollingBack { cursor: next.cursor, exhausted: next.exhausted };
        record.pages += 1;
        write_record(&mut transaction, contract.collection(), &record)
            .map_err(|_| MigrationError::StorageFailure)?;
    }
    for (id, value) in staged {
        let document = Document::new(id, collection.clone(), value);
        transaction.put(document, ExpectedVersion::Any).map_err(|error| map_put_error(&error))?;
    }
    commit_step(engine, scope, transaction, operation)?;
    if next.exhausted {
        Ok(RollbackStep::Complete { restored })
    } else {
        Ok(RollbackStep::PageRestored { restored })
    }
}

/// Drives one migration to completion: begin, pages, finalize.
///
/// Convenience wrapper over the step operations for a host that does not
/// need to interrupt the drive; the steps stay public and equivalent.
///
/// # Errors
///
/// Returns every typed step refusal unchanged.
pub fn apply(
    engine: &Engine,
    scope: &TenantScope,
    contract: &SchemaMigration,
    plan: &MigrationPlan,
    options: MigrationOptions,
) -> Result<MigrationReceipt, MigrationError> {
    if let MigrationStep::AlreadyComplete(receipt) = begin(engine, scope, contract, plan, options)?
    {
        return Ok(receipt.as_already_applied());
    }
    loop {
        match apply_page(engine, scope, contract, plan)? {
            MigrationStep::ScanComplete => return finalize(engine, scope, contract, plan),
            MigrationStep::AlreadyComplete(receipt) => return Ok(receipt.as_already_applied()),
            MigrationStep::PageApplied { .. } | MigrationStep::Begun => {}
        }
    }
}

// ---------------------------------------------------------------------------
// Shared internals
// ---------------------------------------------------------------------------

/// The position of one bounded scan.
struct ScanPosition {
    cursor: Option<ScanCursor>,
    exhausted: bool,
}

impl ScanPosition {
    /// The position a fresh scan starts from.
    fn start() -> Self {
        Self { cursor: None, exhausted: false }
    }
}

/// Scans forward over one collection's documents in canonical key order,
/// visiting at most `budget` documents, and returns the next position.
///
/// The scan fetches bounded windows of the tenant's documents through the
/// engine's bounded snapshot seam and filters to the collection with a
/// composite (collection, identifier) cursor, so paged progress is exact
/// even when identifiers repeat across collections: the storage seam's
/// identifier-only resume may re-serve keys, and re-served keys are
/// duplicates only — the composite cursor strictly advances and a repeated
/// cursor position fails closed with
/// [`MigrationError::PageProgressStalled`] instead of looping.
fn scan_collection(
    engine: &Engine,
    scope: &TenantScope,
    snapshot: &ReadSnapshot,
    collection: &CollectionName,
    position: &ScanPosition,
    budget: usize,
    mut visit: impl FnMut(&Document) -> Result<(), MigrationError>,
) -> Result<ScanPosition, MigrationError> {
    let window_limit = usize::try_from(SCAN_WINDOW).unwrap_or(usize::MAX);
    let mut cursor = position.cursor.clone();
    let mut remaining = budget;
    let mut seen: BTreeSet<(String, String)> = BTreeSet::new();
    while remaining > 0 {
        let after = match cursor.as_ref().map(|cursor| cursor.document.as_str()) {
            Some(document) => {
                Some(DocumentId::try_new(document).map_err(|_| MigrationError::StorageFailure)?)
            }
            None => None,
        };
        let window = engine
            .snapshot_documents_from(scope, snapshot, after, SCAN_WINDOW)
            .map_err(|error| map_engine_error(&error))?;
        if window.is_empty() {
            return Ok(ScanPosition { cursor, exhausted: true });
        }
        let mut last_visited: Option<ScanCursor> = None;
        for document in &window {
            if !past_cursor(document, collection, cursor.as_ref()) {
                continue;
            }
            if remaining == 0 {
                break;
            }
            visit(document)?;
            remaining -= 1;
            last_visited = Some(ScanCursor {
                collection: document.collection().as_str().to_owned(),
                document: document.id().as_str().to_owned(),
            });
        }
        if window.len() < window_limit {
            // A short window ends the tenant scan: every key after the
            // resume point was served. The scan is exhausted only when the
            // budget consumed nothing of the tail; otherwise the page ends
            // mid-tail and resumes exactly after the last visited key.
            if remaining == 0 && last_visited.is_some() {
                return Ok(ScanPosition { cursor: last_visited, exhausted: false });
            }
            return Ok(ScanPosition { cursor: last_visited.or(cursor), exhausted: true });
        }
        if let Some(visited) = last_visited {
            // The budget ran out inside this window: resume exactly after
            // the last visited key, never past an unvisited one.
            cursor = Some(visited);
        } else {
            // The window held no key of this collection past the cursor:
            // advance past the window's last key, whatever its collection.
            let last = window.last().expect("the window is non-empty");
            cursor = Some(ScanCursor {
                collection: last.collection().as_str().to_owned(),
                document: last.id().as_str().to_owned(),
            });
        }
        let key =
            cursor.as_ref().map(|cursor| (cursor.collection.clone(), cursor.document.clone()));
        if let Some(key) = key
            && !seen.insert(key)
        {
            return Err(MigrationError::PageProgressStalled);
        }
    }
    Ok(ScanPosition { cursor, exhausted: false })
}

/// Reports whether one document belongs to the scanned collection and sorts
/// strictly past the cursor.
fn past_cursor(
    document: &Document,
    collection: &CollectionName,
    cursor: Option<&ScanCursor>,
) -> bool {
    if document.collection() != collection {
        return false;
    }
    match cursor {
        None => true,
        Some(cursor) => {
            (document.collection().as_str(), document.id().as_str())
                > (cursor.collection.as_str(), cursor.document.as_str())
        }
    }
}

/// Applies one contract's transforms to one value, counting per-kind
/// applications.
fn apply_with_counts(
    contract: &SchemaMigration,
    value: &DocumentValue,
    counts: &mut TransformCounts,
) -> Result<(bool, DocumentValue), (String, TransformRejection)> {
    let mut current = value.clone();
    let mut changed = false;
    for transform in contract.transforms() {
        match transform.apply(&current) {
            FieldOutcome::Unchanged => {}
            FieldOutcome::Changed(next) => {
                current = next;
                changed = true;
                match transform {
                    FieldTransform::AddField { .. } => counts.added += 1,
                    FieldTransform::RemoveField { .. } => counts.removed += 1,
                    FieldTransform::RenameField { .. } => counts.renamed += 1,
                    FieldTransform::RetypeField { .. } => counts.retyped += 1,
                }
            }
            FieldOutcome::Rejected(reason) => return Err((transform.field().to_owned(), reason)),
        }
    }
    Ok((changed, current))
}

/// Reads one collection's recorded validation schema through a transaction.
fn recorded_schema(
    transaction: &Transaction<'_>,
    collection: &CollectionName,
) -> Result<Option<SchemaDefinition>, MigrationError> {
    let id = record_id("validation", &[collection.as_str()])
        .map_err(|_| MigrationError::StorageFailure)?;
    let value = transaction
        .read_private_metadata(id.as_str())
        .map_err(|_| MigrationError::StorageFailure)?;
    value
        .as_ref()
        .map(|value| {
            ValidationRecord::decode(value)
                .map(|record| record.schema)
                .map_err(|_| MigrationError::ReservedStateInvalid)
        })
        .transpose()
}

/// Checks that one plan addresses one contract and is ready.
fn check_plan(
    contract: &SchemaMigration,
    plan: &MigrationPlan,
    manifest: &str,
) -> Result<(), MigrationError> {
    if plan.manifest() != manifest {
        return Err(MigrationError::PlanMismatch);
    }
    if plan.collection() != contract.collection()
        || plan.from_version() != contract.from_version()
        || plan.to_version() != contract.to_version()
    {
        return Err(MigrationError::InvalidRequest);
    }
    if !plan.is_ready() {
        return Err(MigrationError::InvalidRequest);
    }
    Ok(())
}

/// Computes the honest no-op receipt for a collection already at the
/// contract's target: the observed evidence, nothing staged.
fn at_target_receipt(
    engine: &Engine,
    scope: &TenantScope,
    snapshot: &ReadSnapshot,
    contract: &SchemaMigration,
    options: MigrationOptions,
) -> Result<MigrationStep, MigrationError> {
    let mut chain = DocumentChain::new();
    scan_collection(
        engine,
        scope,
        snapshot,
        contract.collection(),
        &ScanPosition::start(),
        usize::MAX,
        |document| {
            chain.link_result(document.id(), document.value());
            Ok(())
        },
    )?;
    let digest = hex::encode(chain.running());
    let receipt = MigrationReceipt::new(
        contract.collection().clone(),
        evidence::manifest_digest(contract),
        contract.from_version(),
        contract.to_version(),
        digest.clone(),
        digest,
        chain.links(),
        0,
        TransformCounts::default(),
        0,
        contract.rollback_limit(),
        options.acknowledge_not_rollbackable,
        true,
    );
    Ok(MigrationStep::AlreadyComplete(receipt))
}

/// Opens one never-committed read transaction with its fresh snapshot.
fn read_transaction<'engine>(
    engine: &'engine Engine,
    scope: &TenantScope,
    prefix: &str,
) -> Result<(ReadSnapshot, Transaction<'engine>), MigrationError> {
    let snapshot = fresh_snapshot(engine, scope)?;
    let operation = unique_operation(prefix);
    let transaction = engine
        .begin(scope, snapshot.clone(), operation)
        .map_err(|error| map_engine_error(&error))?;
    Ok((snapshot, transaction))
}

/// Returns one fresh validated read snapshot.
fn fresh_snapshot(engine: &Engine, scope: &TenantScope) -> Result<ReadSnapshot, MigrationError> {
    engine.read_snapshot(scope).map_err(|error| map_engine_error(&error))
}

/// Commits one step transaction at the strongest local durability point,
/// resolving an uncertain outcome through the durable operation ledger.
///
/// Every step is idempotent — a resolved-as-committed step advances the
/// recorded cursor, and an unresolved step is retried against the same
/// recorded state — so an uncertain commit never double-applies.
fn commit_step(
    engine: &Engine,
    scope: &TenantScope,
    transaction: Transaction<'_>,
    operation: OperationId,
) -> Result<(), MigrationError> {
    match transaction.commit(LocalDurability::Synced) {
        CommitOutcome::Committed(_) => Ok(()),
        CommitOutcome::Rejected(_) => Err(MigrationError::StorageFailure),
        CommitOutcome::Unknown(_) => match engine.resolve_commit(scope, operation) {
            Ok(DurableResolution::Committed(_)) => Ok(()),
            Ok(_) | Err(_) => Err(MigrationError::StorageFailure),
        },
    }
}

/// Maps an engine failure into the migration vocabulary.
fn map_engine_error(error: &EngineError) -> MigrationError {
    match error {
        EngineError::SnapshotEngineMismatch
        | EngineError::ScopeMismatch
        | EngineError::SnapshotTenantMismatch => MigrationError::InvalidRequest,
        _ => MigrationError::StorageFailure,
    }
}

/// Maps one staged document put's failure: the write-path hook rejects a
/// document that violates the enforced schema, so an invalid document is
/// the typed target refusal and everything else is a storage failure.
fn map_put_error(error: &EngineError) -> MigrationError {
    match error {
        EngineError::InvalidDocument => MigrationError::TargetViolation,
        _ => MigrationError::StorageFailure,
    }
}

/// Mints a never-reused operation identity for one step transaction.
fn unique_operation(prefix: &str) -> OperationId {
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    let index = COUNTER.fetch_add(1, Ordering::Relaxed);
    OperationId::try_new(&format!("{prefix}-{index}-{}", std::process::id()))
        .expect("unique operation identity")
}
