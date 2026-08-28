//! Revisions completeness (GAP-F05, TerraBaseV2#74).
//!
//! W3-F gave revisions their typed family seam: a per-document revision log
//! in one reserved record, explicit `ForceCreate`/`Revert`/`Configure`
//! commands, and paged reads. This module is the completeness layer the
//! parity sweep asked for (PB-3005, PB-3025 remainder, PB-3026 remainder):
//! collection revision policies, policy enforcement including
//! purge-on-delete, the revisions bin and its cleaner, revert addressed by
//! GAP-D02's [`DocumentVersionContract`], and attachment snapshots that
//! compose with the GAP-F01 content plane by reference, never by bytes.
//!
//! ## Ownership adjudication (PROPOSED, `docs/families/revisions.md`)
//!
//! Revision *state* — the revision log — stays in the
//! [`Revisions`](super::FamilyKind::Revisions) family kind: reserved records
//! under `family:revisions/<document>`, exactly the W3-F D4 contract. The
//! *document contract* (GAP-D02's version triple) supplies each snapshot's
//! change identity; it does not store history. Revision *policies* are
//! collection-scoped family state under a distinct reserved kind
//! (`family:revision-configuration/...`), mirroring V1's database-record
//! `RevisionsConfiguration` (a default plus per-collection map, resolved
//! collection, then default, then disabled; `RevisionsStorage.cs:176-194`
//! at the pinned V1 commit). *Enforcement* splits by mechanism: policy
//! application and bin cleaning are the typed, caller-invoked operations
//! this module owns — the family seam owns no background worker, and V1's
//! `RevisionsBinCleaner` is a server timer whose schedule belongs to the
//! host — while write-time snapshot creation on every document put belongs
//! to the Engine write path through the same crate-private hook pattern
//! SM-02 set for schema validation, a PROPOSED collision action the parent
//! must ratify; until it lands, snapshot creation is this module's explicit
//! operation.
//!
//! ## What is here and what is not
//!
//! Landed: the typed operations below, focused in-crate tests, and the
//! decision record. Not claimed: automatic snapshot-on-put (the proposed
//! engine hook), conflict-revision configuration (GAP-F07), revision
//! includes and collection statistics, start-from-etag transfer semantics,
//! any replication or multi-region behavior, and content-plane reference
//! counting against disposed digests — a revert re-puts references V1 also
//! never verifies (`AttachmentsStorage.PutAttachmentRevert`).

use std::collections::BTreeMap;

use crate::engine::{
    DocumentHead, DocumentVersionContract, EngineError, HeadCondition, Transaction,
};
use crate::families::schemas::violations;
use crate::families::state::{
    AttachmentRecord, RESERVED_COLLECTION, REVISION_CONFIGURATION_KIND, REVISION_SNAPSHOT_KIND,
    Records, RevisionRecord, TombstoneRecord, ValidationRecord, record_id,
};
use crate::families::{FamilyError, FamilyReadContext};
use crate::operations::RequestValidationError;
use crate::replication::ReplicationClockStamp;
use crate::types::{
    Cancellation, CollectionName, Deadline, DocumentId, DocumentValue, ExpectedVersion,
    OperationId, TenantScope,
};

/// The default minimum age of revisions-bin entries: V1's 30 days
/// (`RevisionsBinConfiguration.MinimumEntriesAgeToKeepInMin` default
/// `30 * 24 * 60`, `RevisionsBinConfiguration.cs:63`).
pub const DEFAULT_BIN_MINIMUM_AGE_MS: i64 = 30 * 24 * 60 * 60 * 1000;

/// The bin cleaner's per-run document bound: V1's 64-bit batch size
/// (`RevisionsBinCleaner.cs:27`, `10 * 1024`).
pub const MAX_BIN_BATCH_DOCUMENTS: usize = 10 * 1024;

/// Rejects a deadline that has already elapsed.
fn validate_deadline(deadline: &Deadline) -> Result<(), RequestValidationError> {
    if deadline.is_expired() {
        return Err(RequestValidationError::InvalidDeadline);
    }
    Ok(())
}

/// Rejects a cancellation signal that is already cancelled.
fn validate_cancellation(signal: &Cancellation) -> Result<(), RequestValidationError> {
    if signal.is_cancelled() {
        return Err(RequestValidationError::InvalidCancellation);
    }
    Ok(())
}

// ---------------------------------------------------------------------------
// Configuration
// ---------------------------------------------------------------------------

/// The revision policy of one collection — V1's
/// `RevisionsCollectionConfiguration`
/// (`TerraBase.Client/Documents/Operations/Revisions/RevisionsCollectionConfiguration.cs:9-32`):
/// a minimum number of revisions to keep, a minimum age to keep, a cap on
/// deletions per document update, purge-on-delete, and the disable switch.
///
/// V1's fields are all optional; both limits unset means "no count or age
/// limit" (`GetRevisionsForCollectionOrDefault`, `RevisionsStorage.cs:768-772`).
/// The typed constructor refuses zero-valued limits (a zero minimum keeps
/// nothing; a zero cap deletes nothing) where V1's client type carries any
/// number — a fail-closed strengthening recorded in the decision record.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct CollectionRevisionsConfiguration {
    minimum_revisions_to_keep: Option<u64>,
    minimum_revision_age_to_keep_ms: Option<i64>,
    maximum_revisions_to_delete_upon_update: Option<u64>,
    purge_on_delete: bool,
    disabled: bool,
}

impl CollectionRevisionsConfiguration {
    /// Creates one validated collection policy.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidFamilyPayload`] for a zero
    /// minimum-keep count, a negative minimum age, or a zero per-update
    /// deletion cap.
    pub fn try_new(
        minimum_revisions_to_keep: Option<u64>,
        minimum_revision_age_to_keep_ms: Option<i64>,
        maximum_revisions_to_delete_upon_update: Option<u64>,
        purge_on_delete: bool,
        disabled: bool,
    ) -> Result<Self, RequestValidationError> {
        if minimum_revisions_to_keep.is_some_and(|value| value == 0) {
            return Err(RequestValidationError::InvalidFamilyPayload);
        }
        if minimum_revision_age_to_keep_ms.is_some_and(|value| value < 0) {
            return Err(RequestValidationError::InvalidFamilyPayload);
        }
        if maximum_revisions_to_delete_upon_update.is_some_and(|value| value == 0) {
            return Err(RequestValidationError::InvalidFamilyPayload);
        }
        Ok(Self {
            minimum_revisions_to_keep,
            minimum_revision_age_to_keep_ms,
            maximum_revisions_to_delete_upon_update,
            purge_on_delete,
            disabled,
        })
    }

    /// V1's empty configuration: disabled with no limits
    /// (`RevisionsStorage.cs:62`, returned at `:193`).
    #[must_use]
    pub fn disabled() -> Self {
        Self {
            minimum_revisions_to_keep: None,
            minimum_revision_age_to_keep_ms: None,
            maximum_revisions_to_delete_upon_update: None,
            purge_on_delete: false,
            disabled: true,
        }
    }

    /// Returns the minimum number of revisions to keep.
    #[must_use]
    pub const fn minimum_revisions_to_keep(&self) -> Option<u64> {
        self.minimum_revisions_to_keep
    }

    /// Returns the minimum age, in milliseconds, a revision must reach
    /// before any policy may delete it.
    #[must_use]
    pub const fn minimum_revision_age_to_keep_ms(&self) -> Option<i64> {
        self.minimum_revision_age_to_keep_ms
    }

    /// Returns the maximum number of revisions one update may delete.
    #[must_use]
    pub const fn maximum_revisions_to_delete_upon_update(&self) -> Option<u64> {
        self.maximum_revisions_to_delete_upon_update
    }

    /// Reports whether a deleted document's whole revision log is purged.
    #[must_use]
    pub const fn purge_on_delete(&self) -> bool {
        self.purge_on_delete
    }

    /// Reports whether snapshot creation is disabled for the collection.
    #[must_use]
    pub const fn is_disabled(&self) -> bool {
        self.disabled
    }
}

/// The whole revisions configuration of one tenant — V1's
/// `RevisionsConfiguration`: a default policy plus per-collection policies
/// (`RevisionsConfiguration.cs:23-32`) — with the bin cleaner's minimum
/// entry age (`RevisionsBinConfiguration.cs:63`).
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct RevisionsDatabaseConfiguration {
    default: Option<CollectionRevisionsConfiguration>,
    collections: BTreeMap<CollectionName, CollectionRevisionsConfiguration>,
    bin_minimum_age_ms: i64,
}

impl RevisionsDatabaseConfiguration {
    /// Creates one validated database configuration.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidFamilyPayload`] for a
    /// negative bin minimum age.
    pub fn try_new(
        default: Option<CollectionRevisionsConfiguration>,
        collections: Vec<(CollectionName, CollectionRevisionsConfiguration)>,
        bin_minimum_age_ms: i64,
    ) -> Result<Self, RequestValidationError> {
        if bin_minimum_age_ms < 0 {
            return Err(RequestValidationError::InvalidFamilyPayload);
        }
        Ok(Self { default, collections: collections.into_iter().collect(), bin_minimum_age_ms })
    }

    /// The configuration an absent tenant record reads as: no default, no
    /// per-collection policies, and V1's default bin minimum age.
    #[must_use]
    pub fn absent() -> Self {
        Self {
            default: None,
            collections: BTreeMap::new(),
            bin_minimum_age_ms: DEFAULT_BIN_MINIMUM_AGE_MS,
        }
    }

    /// Resolves one collection's effective policy: the collection's own
    /// entry, else the default, else `None` — V1's resolution order
    /// (`GetRevisionsConfiguration`, `RevisionsStorage.cs:176-194`), where
    /// an unresolved collection is the disabled empty configuration.
    #[must_use]
    pub fn effective(
        &self,
        collection: &CollectionName,
    ) -> Option<&CollectionRevisionsConfiguration> {
        self.collections
            .get(collection)
            .or(self.default.as_ref())
            .filter(|configuration| !configuration.is_disabled())
    }

    /// Returns the default policy.
    #[must_use]
    pub const fn default(&self) -> Option<&CollectionRevisionsConfiguration> {
        self.default.as_ref()
    }

    /// Returns the per-collection policies in canonical collection order.
    #[must_use]
    pub fn collections(&self) -> &BTreeMap<CollectionName, CollectionRevisionsConfiguration> {
        &self.collections
    }

    /// Returns the bin cleaner's minimum entry age in milliseconds. An
    /// unreachable age disables the cleaner, as in V1
    /// (`RevisionsBinCleaner.cs:86-87`).
    #[must_use]
    pub const fn bin_minimum_age_ms(&self) -> i64 {
        self.bin_minimum_age_ms
    }
}

// ---------------------------------------------------------------------------
// Errors
// ---------------------------------------------------------------------------

/// Revisions-completeness failures.
///
/// Every case is typed and carries no document value, storage detail, path,
/// credential, or private implementation type.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum RevisionsError {
    /// The write names another operation identity.
    TransactionIdentityMismatch,
    /// The transaction is not live on this store's Engine.
    TransactionEngineMismatch,
    /// The context or write names another tenant.
    TenantMismatch,
    /// The snapshot belongs to another Engine.
    SnapshotEngineMismatch,
    /// The Engine has closed.
    EngineClosed,
    /// The request deadline elapsed before the operation ran.
    DeadlineExceeded,
    /// The operation's cancellation signal was already cancelled.
    Cancelled,
    /// Durable storage returned a typed failure.
    StorageFailure,
    /// A reserved family record could not be read as family state.
    ReservedStateInvalid,
    /// The addressed document does not exist in this view.
    DocumentMissing,
    /// The observed document version contradicts the expected one.
    Conflict,
    /// A recorded family tombstone is newer than the writer's view.
    TombstoneConflict,
    /// The restored value violates the collection's schema policy.
    SchemaViolation,
    /// The collection's effective configuration is absent or disabled.
    RevisionsDisabled,
    /// The referenced revision or version contract does not exist.
    ReferenceInvalid,
    /// A bin-clean candidate is not a deleted document's aged revision log.
    BinEntryNotEligible,
}

impl From<FamilyError> for RevisionsError {
    #[allow(clippy::match_same_arms)] // every family case keeps its closed mapping
    fn from(error: FamilyError) -> Self {
        match error {
            FamilyError::EngineClosed => Self::EngineClosed,
            FamilyError::TenantMismatch => Self::TenantMismatch,
            FamilyError::SnapshotEngineMismatch => Self::SnapshotEngineMismatch,
            FamilyError::TransactionEngineMismatch => Self::TransactionEngineMismatch,
            FamilyError::TransactionIdentityMismatch => Self::TransactionIdentityMismatch,
            FamilyError::DeadlineExceeded => Self::DeadlineExceeded,
            FamilyError::Cancelled => Self::Cancelled,
            FamilyError::StorageFailure => Self::StorageFailure,
            FamilyError::ReservedStateInvalid => Self::ReservedStateInvalid,
            FamilyError::DocumentMissing => Self::DocumentMissing,
            FamilyError::Conflict => Self::Conflict,
            FamilyError::TombstoneConflict => Self::TombstoneConflict,
            FamilyError::SchemaViolation => Self::SchemaViolation,
            FamilyError::RevisionReferenceInvalid => Self::ReferenceInvalid,
            FamilyError::InvalidRequest
            | FamilyError::CounterOverflow
            | FamilyError::TimeSeriesArityInvalid
            | FamilyError::TimeSeriesRangeInvalid
            | FamilyError::TimeSeriesPolicyInvalid
            | FamilyError::ValidationPolicyMalformed
            | FamilyError::AttachmentNameDuplicate
            | FamilyError::LifecycleValueInvalid
            | FamilyError::CompareExchangeIndexStale
            | FamilyError::IdentitySeedInvalid
            | FamilyError::IdentityExhausted => Self::StorageFailure,
        }
    }
}

/// Maps Engine HEAD failures to the revisions vocabulary.
fn map_head_error(error: &EngineError) -> RevisionsError {
    match error {
        EngineError::Closed(_) => RevisionsError::EngineClosed,
        _ => RevisionsError::StorageFailure,
    }
}

// ---------------------------------------------------------------------------
// Request and result payloads
// ---------------------------------------------------------------------------

/// One whole-tenant revisions-configuration write.
#[derive(Clone, Debug, PartialEq)]
pub struct RevisionsConfigurationWrite {
    scope: TenantScope,
    configuration: RevisionsDatabaseConfiguration,
    operation_id: OperationId,
    deadline: Deadline,
    cancellation: Cancellation,
}

impl RevisionsConfigurationWrite {
    /// Creates one validated configuration write.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidDeadline`] for an elapsed
    /// deadline and [`RequestValidationError::InvalidCancellation`] for an
    /// already cancelled signal.
    pub fn try_new(
        scope: TenantScope,
        configuration: RevisionsDatabaseConfiguration,
        operation_id: OperationId,
        deadline: Deadline,
        cancellation: Cancellation,
    ) -> Result<Self, RequestValidationError> {
        validate_deadline(&deadline)?;
        validate_cancellation(&cancellation)?;
        Ok(Self { scope, configuration, operation_id, deadline, cancellation })
    }

    /// Returns the validated tenant scope.
    #[must_use]
    pub fn tenant_scope(&self) -> &TenantScope {
        &self.scope
    }

    /// Returns the configuration to record.
    #[must_use]
    pub fn configuration(&self) -> &RevisionsDatabaseConfiguration {
        &self.configuration
    }

    /// Returns the stable operation identity.
    #[must_use]
    pub fn operation_id(&self) -> &OperationId {
        &self.operation_id
    }

    /// Returns the request deadline.
    #[must_use]
    pub fn deadline(&self) -> Deadline {
        self.deadline
    }

    /// Returns a clone of the cancellation signal.
    #[must_use]
    pub fn cancellation(&self) -> Cancellation {
        self.cancellation.clone()
    }
}

/// One revisions-configuration read.
#[derive(Clone, Debug, PartialEq)]
pub struct RevisionsConfigurationRead {
    deadline: Deadline,
    cancellation: Cancellation,
}

impl RevisionsConfigurationRead {
    /// Creates one validated configuration read.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidDeadline`] for an elapsed
    /// deadline and [`RequestValidationError::InvalidCancellation`] for an
    /// already cancelled signal.
    pub fn try_new(
        deadline: Deadline,
        cancellation: Cancellation,
    ) -> Result<Self, RequestValidationError> {
        validate_deadline(&deadline)?;
        validate_cancellation(&cancellation)?;
        Ok(Self { deadline, cancellation })
    }

    /// Returns the request deadline.
    #[must_use]
    pub fn deadline(&self) -> Deadline {
        self.deadline
    }

    /// Returns a clone of the cancellation signal.
    #[must_use]
    pub fn cancellation(&self) -> Cancellation {
        self.cancellation.clone()
    }
}

/// One revision-snapshot write: record the document's published state —
/// value, GAP-D02 version contract, and attachment references — as one new
/// revision under the collection's effective policy.
#[derive(Clone, Debug, PartialEq)]
pub struct RevisionSnapshotWrite {
    scope: TenantScope,
    document: DocumentId,
    expected: ExpectedVersion,
    now_ms: i64,
    operation_id: OperationId,
    deadline: Deadline,
    cancellation: Cancellation,
}

impl RevisionSnapshotWrite {
    /// Creates one validated snapshot write. The caller supplies the logical
    /// clock (`now_ms`): the family owns logical times, the host owns the
    /// schedule that drives snapshotting.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidDeadline`] for an elapsed
    /// deadline, [`RequestValidationError::InvalidCancellation`] for an
    /// already cancelled signal, and
    /// [`RequestValidationError::InvalidFamilyPayload`] for a negative clock.
    #[allow(clippy::too_many_arguments)] // the frozen payload shape
    pub fn try_new(
        scope: TenantScope,
        document: DocumentId,
        expected: ExpectedVersion,
        now_ms: i64,
        operation_id: OperationId,
        deadline: Deadline,
        cancellation: Cancellation,
    ) -> Result<Self, RequestValidationError> {
        validate_deadline(&deadline)?;
        validate_cancellation(&cancellation)?;
        if now_ms < 0 {
            return Err(RequestValidationError::InvalidFamilyPayload);
        }
        Ok(Self { scope, document, expected, now_ms, operation_id, deadline, cancellation })
    }

    /// Returns the validated tenant scope.
    #[must_use]
    pub fn tenant_scope(&self) -> &TenantScope {
        &self.scope
    }

    /// Returns the document identity.
    #[must_use]
    pub fn document(&self) -> &DocumentId {
        &self.document
    }

    /// Returns the expected owner-document version.
    #[must_use]
    pub const fn expected(&self) -> ExpectedVersion {
        self.expected
    }

    /// Returns the caller's logical clock.
    #[must_use]
    pub const fn now_ms(&self) -> i64 {
        self.now_ms
    }

    /// Returns the stable operation identity.
    #[must_use]
    pub fn operation_id(&self) -> &OperationId {
        &self.operation_id
    }

    /// Returns the request deadline.
    #[must_use]
    pub const fn deadline(&self) -> Deadline {
        self.deadline
    }

    /// Returns a clone of the cancellation signal.
    #[must_use]
    pub fn cancellation(&self) -> Cancellation {
        self.cancellation.clone()
    }
}

/// One historical revision read joining the revision log with the GAP-F05
/// snapshot side car: each entry carries the value, the GAP-D02 version
/// contract captured at snapshot time, and the attachment references.
#[derive(Clone, Debug, PartialEq)]
pub struct RevisionSnapshotsRead {
    document: DocumentId,
    at_revision: Option<u64>,
    paging: crate::families::RevisionPaging,
    deadline: Deadline,
    cancellation: Cancellation,
}

impl RevisionSnapshotsRead {
    /// Creates one validated snapshots read. `at_revision` selects one
    /// exact revision (V1's single-revision get); `None` pages the log.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidDeadline`] for an elapsed
    /// deadline and [`RequestValidationError::InvalidCancellation`] for an
    /// already cancelled signal.
    pub fn try_new(
        document: DocumentId,
        at_revision: Option<u64>,
        paging: crate::families::RevisionPaging,
        deadline: Deadline,
        cancellation: Cancellation,
    ) -> Result<Self, RequestValidationError> {
        validate_deadline(&deadline)?;
        validate_cancellation(&cancellation)?;
        Ok(Self { document, at_revision, paging, deadline, cancellation })
    }

    /// Returns the document identity.
    #[must_use]
    pub fn document(&self) -> &DocumentId {
        &self.document
    }

    /// Returns the exact-revision selector.
    #[must_use]
    pub const fn at_revision(&self) -> Option<u64> {
        self.at_revision
    }

    /// Returns the requested paging.
    #[must_use]
    pub const fn paging(&self) -> crate::families::RevisionPaging {
        self.paging
    }

    /// Returns the request deadline.
    #[must_use]
    pub const fn deadline(&self) -> Deadline {
        self.deadline
    }

    /// Returns a clone of the cancellation signal.
    #[must_use]
    pub fn cancellation(&self) -> Cancellation {
        self.cancellation.clone()
    }
}

/// One revert write addressed by the GAP-D02 version contract — V1's
/// revert-by-change-vector (`RevertRevisionsByIdOperation`,
/// `TerraBase.Client/Documents/Operations/Revisions/RevertRevisionsByIdOperation.cs:19-33`).
#[derive(Clone, Debug, PartialEq)]
pub struct RevertToContractWrite {
    scope: TenantScope,
    document: DocumentId,
    contract: DocumentVersionContract,
    expected: ExpectedVersion,
    now_ms: i64,
    operation_id: OperationId,
    deadline: Deadline,
    cancellation: Cancellation,
}

impl RevertToContractWrite {
    /// Creates one validated revert write.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidDeadline`] for an elapsed
    /// deadline, [`RequestValidationError::InvalidCancellation`] for an
    /// already cancelled signal, and
    /// [`RequestValidationError::InvalidFamilyPayload`] for a negative clock.
    #[allow(clippy::too_many_arguments)] // the frozen payload shape
    pub fn try_new(
        scope: TenantScope,
        document: DocumentId,
        contract: DocumentVersionContract,
        expected: ExpectedVersion,
        now_ms: i64,
        operation_id: OperationId,
        deadline: Deadline,
        cancellation: Cancellation,
    ) -> Result<Self, RequestValidationError> {
        validate_deadline(&deadline)?;
        validate_cancellation(&cancellation)?;
        if now_ms < 0 {
            return Err(RequestValidationError::InvalidFamilyPayload);
        }
        Ok(Self {
            scope,
            document,
            contract,
            expected,
            now_ms,
            operation_id,
            deadline,
            cancellation,
        })
    }

    /// Returns the validated tenant scope.
    #[must_use]
    pub fn tenant_scope(&self) -> &TenantScope {
        &self.scope
    }

    /// Returns the document identity.
    #[must_use]
    pub fn document(&self) -> &DocumentId {
        &self.document
    }

    /// Returns the version contract of the revision to restore.
    #[must_use]
    pub const fn contract(&self) -> &DocumentVersionContract {
        &self.contract
    }

    /// Returns the expected owner-document version.
    #[must_use]
    pub const fn expected(&self) -> ExpectedVersion {
        self.expected
    }

    /// Returns the caller's logical clock.
    #[must_use]
    pub const fn now_ms(&self) -> i64 {
        self.now_ms
    }

    /// Returns the stable operation identity.
    #[must_use]
    pub fn operation_id(&self) -> &OperationId {
        &self.operation_id
    }

    /// Returns the request deadline.
    #[must_use]
    pub const fn deadline(&self) -> Deadline {
        self.deadline
    }

    /// Returns a clone of the cancellation signal.
    #[must_use]
    pub fn cancellation(&self) -> Cancellation {
        self.cancellation.clone()
    }
}

/// One policy-enforcement write: apply the collection's effective policy to
/// one document's revision log — V1's
/// `EnforceRevisionsConfigurationOperation` narrowed to one document.
#[derive(Clone, Debug, PartialEq)]
pub struct EnforceRevisionsPolicyWrite {
    scope: TenantScope,
    document: DocumentId,
    now_ms: i64,
    operation_id: OperationId,
    deadline: Deadline,
    cancellation: Cancellation,
}

impl EnforceRevisionsPolicyWrite {
    /// Creates one validated enforcement write.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidDeadline`] for an elapsed
    /// deadline, [`RequestValidationError::InvalidCancellation`] for an
    /// already cancelled signal, and
    /// [`RequestValidationError::InvalidFamilyPayload`] for a negative clock.
    pub fn try_new(
        scope: TenantScope,
        document: DocumentId,
        now_ms: i64,
        operation_id: OperationId,
        deadline: Deadline,
        cancellation: Cancellation,
    ) -> Result<Self, RequestValidationError> {
        validate_deadline(&deadline)?;
        validate_cancellation(&cancellation)?;
        if now_ms < 0 {
            return Err(RequestValidationError::InvalidFamilyPayload);
        }
        Ok(Self { scope, document, now_ms, operation_id, deadline, cancellation })
    }

    /// Returns the validated tenant scope.
    #[must_use]
    pub fn tenant_scope(&self) -> &TenantScope {
        &self.scope
    }

    /// Returns the document identity.
    #[must_use]
    pub fn document(&self) -> &DocumentId {
        &self.document
    }

    /// Returns the caller's logical clock.
    #[must_use]
    pub const fn now_ms(&self) -> i64 {
        self.now_ms
    }

    /// Returns the stable operation identity.
    #[must_use]
    pub fn operation_id(&self) -> &OperationId {
        &self.operation_id
    }

    /// Returns the request deadline.
    #[must_use]
    pub const fn deadline(&self) -> Deadline {
        self.deadline
    }

    /// Returns a clone of the cancellation signal.
    #[must_use]
    pub fn cancellation(&self) -> Cancellation {
        self.cancellation.clone()
    }
}

/// One revisions-bin page read: the deleted documents whose revision logs
/// are older than the given cutoff — V1's `GetDeletedRevisionsIds` before a
/// batched clean (`RevisionsBinCleaner.cs:97-109`).
#[derive(Clone, Debug, PartialEq)]
pub struct RevisionsBinRead {
    before_ms: i64,
    limit: u32,
    deadline: Deadline,
    cancellation: Cancellation,
}

impl RevisionsBinRead {
    /// Creates one validated bin read.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidRange`] for a zero limit or
    /// a limit past [`MAX_BIN_BATCH_DOCUMENTS`],
    /// [`RequestValidationError::InvalidFamilyPayload`] for a negative
    /// cutoff, [`RequestValidationError::InvalidDeadline`] for an elapsed
    /// deadline, and [`RequestValidationError::InvalidCancellation`] for an
    /// already cancelled signal.
    pub fn try_new(
        before_ms: i64,
        limit: u32,
        deadline: Deadline,
        cancellation: Cancellation,
    ) -> Result<Self, RequestValidationError> {
        if limit == 0 || usize::try_from(limit).is_ok_and(|value| value > MAX_BIN_BATCH_DOCUMENTS) {
            return Err(RequestValidationError::InvalidRange);
        }
        validate_deadline(&deadline)?;
        validate_cancellation(&cancellation)?;
        if before_ms < 0 {
            return Err(RequestValidationError::InvalidFamilyPayload);
        }
        Ok(Self { before_ms, limit, deadline, cancellation })
    }

    /// Returns the age cutoff: only entries whose newest revision was
    /// recorded strictly before this time are eligible.
    #[must_use]
    pub const fn before_ms(&self) -> i64 {
        self.before_ms
    }

    /// Returns the page limit.
    #[must_use]
    pub const fn limit(&self) -> u32 {
        self.limit
    }

    /// Returns the request deadline.
    #[must_use]
    pub const fn deadline(&self) -> Deadline {
        self.deadline
    }

    /// Returns a clone of the cancellation signal.
    #[must_use]
    pub fn cancellation(&self) -> Cancellation {
        self.cancellation.clone()
    }
}

/// One revisions-bin clean write: purge the listed documents' revision logs.
/// Eligibility — the document is deleted and its newest revision is strictly
/// older than the cutoff — is re-checked before anything is staged, and one
/// ineligible candidate refuses the whole write, leaving the caller's
/// transaction unchanged.
#[derive(Clone, Debug, PartialEq)]
pub struct RevisionsBinCleanWrite {
    scope: TenantScope,
    documents: Vec<DocumentId>,
    before_ms: i64,
    operation_id: OperationId,
    deadline: Deadline,
    cancellation: Cancellation,
}

impl RevisionsBinCleanWrite {
    /// Creates one validated bin clean.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidFamilyPayload`] for an empty
    /// candidate list or a negative cutoff,
    /// [`RequestValidationError::InvalidRange`] for a candidate list past
    /// [`MAX_BIN_BATCH_DOCUMENTS`], [`RequestValidationError::InvalidDeadline`]
    /// for an elapsed deadline, and
    /// [`RequestValidationError::InvalidCancellation`] for an already
    /// cancelled signal.
    pub fn try_new(
        scope: TenantScope,
        documents: Vec<DocumentId>,
        before_ms: i64,
        operation_id: OperationId,
        deadline: Deadline,
        cancellation: Cancellation,
    ) -> Result<Self, RequestValidationError> {
        if documents.is_empty() {
            return Err(RequestValidationError::InvalidFamilyPayload);
        }
        if documents.len() > MAX_BIN_BATCH_DOCUMENTS {
            return Err(RequestValidationError::InvalidRange);
        }
        validate_deadline(&deadline)?;
        validate_cancellation(&cancellation)?;
        if before_ms < 0 {
            return Err(RequestValidationError::InvalidFamilyPayload);
        }
        Ok(Self { scope, documents, before_ms, operation_id, deadline, cancellation })
    }

    /// Returns the validated tenant scope.
    #[must_use]
    pub fn tenant_scope(&self) -> &TenantScope {
        &self.scope
    }

    /// Returns the candidate documents in the given order.
    #[must_use]
    pub fn documents(&self) -> &[DocumentId] {
        &self.documents
    }

    /// Returns the age cutoff.
    #[must_use]
    pub const fn before_ms(&self) -> i64 {
        self.before_ms
    }

    /// Returns the stable operation identity.
    #[must_use]
    pub fn operation_id(&self) -> &OperationId {
        &self.operation_id
    }

    /// Returns the request deadline.
    #[must_use]
    pub const fn deadline(&self) -> Deadline {
        self.deadline
    }

    /// Returns a clone of the cancellation signal.
    #[must_use]
    pub fn cancellation(&self) -> Cancellation {
        self.cancellation.clone()
    }
}

/// One recorded revision snapshot.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct RevisionSnapshotRecorded {
    revision: u64,
    contract: DocumentVersionContract,
    attachment_references: usize,
    removed_by_policy: u64,
    has_more: bool,
}

impl RevisionSnapshotRecorded {
    /// Assembles one recorded snapshot. Crate-only.
    pub(crate) const fn new(
        revision: u64,
        contract: DocumentVersionContract,
        attachment_references: usize,
        removed_by_policy: u64,
        has_more: bool,
    ) -> Self {
        Self { revision, contract, attachment_references, removed_by_policy, has_more }
    }

    /// Returns the recorded revision's number.
    #[must_use]
    pub const fn revision(&self) -> u64 {
        self.revision
    }

    /// Returns the GAP-D02 version contract captured with the snapshot.
    #[must_use]
    pub const fn contract(&self) -> &DocumentVersionContract {
        &self.contract
    }

    /// Returns the number of attachment references the snapshot captured.
    #[must_use]
    pub const fn attachment_references(&self) -> usize {
        self.attachment_references
    }

    /// Returns how many revisions the collection policy removed with this
    /// update.
    #[must_use]
    pub const fn removed_by_policy(&self) -> u64 {
        self.removed_by_policy
    }

    /// Reports whether the per-update deletion cap stopped the policy
    /// before the configured minimum was reached — V1's `HasMore`
    /// (`RevisionsStorage.cs:833`).
    #[must_use]
    pub const fn has_more(&self) -> bool {
        self.has_more
    }
}

/// One content-addressed attachment reference inside a revision snapshot —
/// V1's revision attachment metadata: `PutAttachmentRevert` re-puts by name,
/// content type, hash, and size (`AttachmentsStorage.cs:611-652`), and the
/// bytes stay in the hash-addressed store. References never carry bytes.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct RevisionAttachmentRef {
    name: String,
    content_type: String,
    size: u64,
    digest: String,
}

impl RevisionAttachmentRef {
    /// Assembles one reference. Crate-only: references originate from the
    /// attachments family's records, never from caller text.
    pub(crate) fn new(name: String, content_type: String, size: u64, digest: String) -> Self {
        Self { name, content_type, size, digest }
    }

    /// Returns the attachment name.
    #[must_use]
    pub fn name(&self) -> &str {
        &self.name
    }

    /// Returns the declared content type.
    #[must_use]
    pub fn content_type(&self) -> &str {
        &self.content_type
    }

    /// Returns the declared size in bytes.
    #[must_use]
    pub const fn size(&self) -> u64 {
        self.size
    }

    /// Returns the GAP-F01 content digest the bytes are stored under.
    #[must_use]
    pub fn digest(&self) -> &str {
        &self.digest
    }
}

/// One historical revision with its captured identity and attachments.
#[derive(Clone, Debug, PartialEq)]
pub struct RevisionSnapshotEntry {
    revision: u64,
    at: i64,
    value: DocumentValue,
    contract: Option<DocumentVersionContract>,
    reverted_to: Option<u64>,
    attachments: Vec<RevisionAttachmentRef>,
}

impl RevisionSnapshotEntry {
    /// Assembles one entry. Crate-only.
    pub(crate) fn new(
        revision: u64,
        at: i64,
        value: DocumentValue,
        contract: Option<DocumentVersionContract>,
        reverted_to: Option<u64>,
        attachments: Vec<RevisionAttachmentRef>,
    ) -> Self {
        Self { revision, at, value, contract, reverted_to, attachments }
    }

    /// Returns the monotonic revision number.
    #[must_use]
    pub const fn revision(&self) -> u64 {
        self.revision
    }

    /// Returns the logical recording time.
    #[must_use]
    pub const fn at(&self) -> i64 {
        self.at
    }

    /// Returns the revisioned document value.
    #[must_use]
    pub fn value(&self) -> &DocumentValue {
        &self.value
    }

    /// Returns the GAP-D02 version contract captured at snapshot time, or
    /// `None` for revisions created without one (a revert's restore
    /// revision: its publication identity exists only after commit).
    #[must_use]
    pub const fn contract(&self) -> Option<&DocumentVersionContract> {
        self.contract.as_ref()
    }

    /// Returns the revision a revert restored, when this entry is one.
    #[must_use]
    pub const fn reverted_to(&self) -> Option<u64> {
        self.reverted_to
    }

    /// Returns the snapshot's attachment references.
    #[must_use]
    pub fn attachments(&self) -> &[RevisionAttachmentRef] {
        &self.attachments
    }
}

/// One page of historical revisions.
#[derive(Clone, Debug, PartialEq)]
pub struct RevisionSnapshotPage {
    entries: Vec<RevisionSnapshotEntry>,
    total: u64,
}

impl RevisionSnapshotPage {
    /// Assembles one page. Crate-only.
    pub(crate) fn new(entries: Vec<RevisionSnapshotEntry>, total: u64) -> Self {
        Self { entries, total }
    }

    /// Returns the page entries in revision order.
    #[must_use]
    pub fn entries(&self) -> &[RevisionSnapshotEntry] {
        &self.entries
    }

    /// Returns the total number of stored revisions.
    #[must_use]
    pub const fn total(&self) -> u64 {
        self.total
    }
}

/// The outcome of one revert.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct RevertOutcome {
    created_revision: u64,
    restored_attachments: usize,
}

impl RevertOutcome {
    /// Assembles one outcome. Crate-only.
    pub(crate) const fn new(created_revision: u64, restored_attachments: usize) -> Self {
        Self { created_revision, restored_attachments }
    }

    /// Returns the revision the restore created.
    #[must_use]
    pub const fn created_revision(&self) -> u64 {
        self.created_revision
    }

    /// Returns how many attachment references the restore re-put.
    #[must_use]
    pub const fn restored_attachments(&self) -> usize {
        self.restored_attachments
    }
}

/// The outcome of one policy enforcement.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct PolicyEnforcementOutcome {
    removed: u64,
    remaining: u64,
    purged_record: bool,
    has_more: bool,
}

impl PolicyEnforcementOutcome {
    /// Assembles one outcome. Crate-only.
    pub(crate) const fn new(
        removed: u64,
        remaining: u64,
        purged_record: bool,
        has_more: bool,
    ) -> Self {
        Self { removed, remaining, purged_record, has_more }
    }

    /// Returns how many revisions the policy removed.
    #[must_use]
    pub const fn removed(&self) -> u64 {
        self.removed
    }

    /// Returns how many revisions remain stored.
    #[must_use]
    pub const fn remaining(&self) -> u64 {
        self.remaining
    }

    /// Reports whether the whole revision log was purged (purge-on-delete
    /// of a deleted document).
    #[must_use]
    pub const fn purged_record(&self) -> bool {
        self.purged_record
    }

    /// Reports whether the per-update deletion cap stopped before the
    /// configured minimum.
    #[must_use]
    pub const fn has_more(&self) -> bool {
        self.has_more
    }
}

/// One revisions-bin entry: the revision log of one deleted document.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct RevisionsBinEntry {
    document: DocumentId,
    newest_revision: u64,
    newest_at: i64,
    total: u64,
}

impl RevisionsBinEntry {
    /// Assembles one entry. Crate-only.
    pub(crate) const fn new(
        document: DocumentId,
        newest_revision: u64,
        newest_at: i64,
        total: u64,
    ) -> Self {
        Self { document, newest_revision, newest_at, total }
    }

    /// Returns the deleted document's identity.
    #[must_use]
    pub const fn document(&self) -> &DocumentId {
        &self.document
    }

    /// Returns the newest stored revision's number.
    #[must_use]
    pub const fn newest_revision(&self) -> u64 {
        self.newest_revision
    }

    /// Returns the newest stored revision's recording time.
    #[must_use]
    pub const fn newest_at(&self) -> i64 {
        self.newest_at
    }

    /// Returns the number of stored revisions.
    #[must_use]
    pub const fn total(&self) -> u64 {
        self.total
    }
}

/// One page of revisions-bin entries.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct RevisionsBinPage {
    entries: Vec<RevisionsBinEntry>,
    has_more: bool,
}

impl RevisionsBinPage {
    /// Assembles one page. Crate-only.
    pub(crate) fn new(entries: Vec<RevisionsBinEntry>, has_more: bool) -> Self {
        Self { entries, has_more }
    }

    /// Returns the page entries in canonical document order.
    #[must_use]
    pub fn entries(&self) -> &[RevisionsBinEntry] {
        &self.entries
    }

    /// Reports whether more eligible entries exist past the page limit.
    #[must_use]
    pub const fn has_more(&self) -> bool {
        self.has_more
    }
}

/// The outcome of one bin clean.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct RevisionsBinCleanOutcome {
    purged: u64,
}

impl RevisionsBinCleanOutcome {
    /// Assembles one outcome. Crate-only.
    pub(crate) const fn new(purged: u64) -> Self {
        Self { purged }
    }

    /// Returns how many documents' revision logs were purged.
    #[must_use]
    pub const fn purged(&self) -> u64 {
        self.purged
    }
}

// ---------------------------------------------------------------------------
// Reserved-record encoding
// ---------------------------------------------------------------------------

/// The reserved configuration record identifier of one tenant.
fn configuration_id() -> Result<DocumentId, RevisionsError> {
    record_id(REVISION_CONFIGURATION_KIND, &["root"]).map_err(|_| RevisionsError::StorageFailure)
}

/// The reserved snapshot side-car identifier of one document.
fn snapshot_id(document: &DocumentId) -> Result<DocumentId, RevisionsError> {
    record_id(REVISION_SNAPSHOT_KIND, &[document.as_str()])
        .map_err(|_| RevisionsError::StorageFailure)
}

/// Writes one record, or clears it when it holds no state — the family
/// store's own convention (`src/families/store.rs:234-241`).
fn write_or_clear(
    records: &mut Records<'_, '_>,
    id: DocumentId,
    empty: bool,
    encode: DocumentValue,
) -> Result<(), RevisionsError> {
    if empty {
        records.clear(&id).map_err(RevisionsError::from)
    } else {
        records.write(id, encode).map_err(RevisionsError::from)
    }
}

/// Encodes one collection policy as one structured value.
fn encode_collection_configuration(
    configuration: &CollectionRevisionsConfiguration,
) -> DocumentValue {
    fn optional_u64(value: Option<u64>) -> DocumentValue {
        match value {
            Some(value) => DocumentValue::Integer(i64::try_from(value).unwrap_or(i64::MAX)),
            None => DocumentValue::Null,
        }
    }
    DocumentValue::Object(
        [
            ("minimum_revisions", optional_u64(configuration.minimum_revisions_to_keep())),
            (
                "minimum_age_ms",
                match configuration.minimum_revision_age_to_keep_ms() {
                    Some(value) => DocumentValue::Integer(value),
                    None => DocumentValue::Null,
                },
            ),
            (
                "maximum_deletions",
                optional_u64(configuration.maximum_revisions_to_delete_upon_update()),
            ),
            ("purge_on_delete", DocumentValue::Boolean(configuration.purge_on_delete())),
            ("disabled", DocumentValue::Boolean(configuration.is_disabled())),
        ]
        .into_iter()
        .map(|(key, value)| (key.to_owned(), value))
        .collect(),
    )
}

/// Decodes one collection policy, failing closed on any malformed field.
fn decode_collection_configuration(
    value: &DocumentValue,
) -> Result<CollectionRevisionsConfiguration, RevisionsError> {
    let DocumentValue::Object(fields) = value else {
        return Err(RevisionsError::ReservedStateInvalid);
    };
    let optional_count = |fields: &BTreeMap<String, DocumentValue>,
                          key: &str|
     -> Result<Option<u64>, RevisionsError> {
        match fields.get(key) {
            None | Some(DocumentValue::Null) => Ok(None),
            Some(DocumentValue::Integer(number)) if *number >= 0 => {
                Ok(Some(u64::try_from(*number).map_err(|_| RevisionsError::ReservedStateInvalid)?))
            }
            _ => Err(RevisionsError::ReservedStateInvalid),
        }
    };
    let optional_age = |fields: &BTreeMap<String, DocumentValue>,
                        key: &str|
     -> Result<Option<i64>, RevisionsError> {
        match fields.get(key) {
            None | Some(DocumentValue::Null) => Ok(None),
            Some(DocumentValue::Integer(number)) => Ok(Some(*number)),
            _ => Err(RevisionsError::ReservedStateInvalid),
        }
    };
    let required_bool =
        |fields: &BTreeMap<String, DocumentValue>, key: &str| -> Result<bool, RevisionsError> {
            match fields.get(key) {
                Some(DocumentValue::Boolean(flag)) => Ok(*flag),
                _ => Err(RevisionsError::ReservedStateInvalid),
            }
        };
    CollectionRevisionsConfiguration::try_new(
        optional_count(fields, "minimum_revisions")?,
        optional_age(fields, "minimum_age_ms")?,
        optional_count(fields, "maximum_deletions")?,
        required_bool(fields, "purge_on_delete")?,
        required_bool(fields, "disabled")?,
    )
    .map_err(|_| RevisionsError::ReservedStateInvalid)
}

/// Encodes the whole database configuration.
fn encode_configuration(configuration: &RevisionsDatabaseConfiguration) -> DocumentValue {
    let mut collections = BTreeMap::new();
    for (name, policy) in configuration.collections() {
        collections.insert(name.as_str().to_owned(), encode_collection_configuration(policy));
    }
    DocumentValue::Object(
        [
            (
                "default".to_owned(),
                match configuration.default() {
                    Some(policy) => encode_collection_configuration(policy),
                    None => DocumentValue::Null,
                },
            ),
            ("collections".to_owned(), DocumentValue::Object(collections)),
            (
                "bin_minimum_age_ms".to_owned(),
                DocumentValue::Integer(configuration.bin_minimum_age_ms()),
            ),
        ]
        .into_iter()
        .collect(),
    )
}

/// Decodes the whole database configuration, failing closed.
fn decode_configuration(
    value: &DocumentValue,
) -> Result<RevisionsDatabaseConfiguration, RevisionsError> {
    let DocumentValue::Object(fields) = value else {
        return Err(RevisionsError::ReservedStateInvalid);
    };
    let default = match fields.get("default") {
        None | Some(DocumentValue::Null) => None,
        Some(encoded) => Some(decode_collection_configuration(encoded)?),
    };
    let mut collections = Vec::new();
    match fields.get("collections") {
        None | Some(DocumentValue::Null) => {}
        Some(DocumentValue::Object(map)) => {
            for (name, encoded) in map {
                let name = CollectionName::try_new(name)
                    .map_err(|_| RevisionsError::ReservedStateInvalid)?;
                collections.push((name, decode_collection_configuration(encoded)?));
            }
        }
        _ => return Err(RevisionsError::ReservedStateInvalid),
    }
    let bin = match fields.get("bin_minimum_age_ms") {
        Some(DocumentValue::Integer(value)) => *value,
        _ => return Err(RevisionsError::ReservedStateInvalid),
    };
    RevisionsDatabaseConfiguration::try_new(default, collections, bin)
        .map_err(|_| RevisionsError::ReservedStateInvalid)
}

/// One side-car entry, joined to the revision log by revision number.
#[derive(Clone, Debug, PartialEq)]
struct SnapshotEntry {
    revision: u64,
    contract: Option<DocumentVersionContract>,
    reverted_to: Option<u64>,
    attachments: Vec<RevisionAttachmentRef>,
}

/// The GAP-F05 snapshot side car of one document: the collection its
/// revisions belong to plus, per revision, the captured GAP-D02 version
/// contract, revert provenance, and attachment references. The W3-F
/// revision log stays the canonical value log; the side car holds the facts
/// that log predates.
#[derive(Clone, Debug, Default, PartialEq)]
struct SnapshotRecord {
    collection: Option<CollectionName>,
    entries: Vec<SnapshotEntry>,
}

impl SnapshotRecord {
    /// Decodes one side-car value, failing closed.
    fn decode(value: &DocumentValue) -> Result<Self, RevisionsError> {
        let DocumentValue::Object(fields) = value else {
            return Err(RevisionsError::ReservedStateInvalid);
        };
        let collection = match fields.get("collection") {
            None | Some(DocumentValue::Null) => None,
            Some(DocumentValue::String(name)) => Some(
                CollectionName::try_new(name).map_err(|_| RevisionsError::ReservedStateInvalid)?,
            ),
            _ => return Err(RevisionsError::ReservedStateInvalid),
        };
        let mut entries = Vec::new();
        match fields.get("entries") {
            None | Some(DocumentValue::Null) => {}
            Some(DocumentValue::Array(items)) => {
                for item in items {
                    entries.push(SnapshotEntry::decode(item)?);
                }
            }
            _ => return Err(RevisionsError::ReservedStateInvalid),
        }
        entries.sort_by_key(|entry| entry.revision);
        Ok(Self { collection, entries })
    }

    /// Encodes one side-car value.
    fn encode(&self) -> DocumentValue {
        DocumentValue::Object(
            [
                (
                    "collection".to_owned(),
                    match &self.collection {
                        Some(name) => DocumentValue::String(name.as_str().to_owned()),
                        None => DocumentValue::Null,
                    },
                ),
                (
                    "entries".to_owned(),
                    DocumentValue::Array(self.entries.iter().map(SnapshotEntry::encode).collect()),
                ),
            ]
            .into_iter()
            .collect(),
        )
    }

    /// Returns the entry of one revision.
    fn entry(&self, revision: u64) -> Option<&SnapshotEntry> {
        self.entries.iter().find(|entry| entry.revision == revision)
    }

    /// Drops every entry whose revision is absent from the log.
    fn retain_log(&mut self, log: &RevisionRecord) {
        self.entries
            .retain(|entry| log.entries.iter().any(|(revision, _, _)| *revision == entry.revision));
    }

    /// Reports whether the side car holds nothing.
    fn is_empty(&self) -> bool {
        self.collection.is_none() && self.entries.is_empty()
    }
}

impl SnapshotEntry {
    /// Decodes one entry, failing closed.
    fn decode(value: &DocumentValue) -> Result<Self, RevisionsError> {
        let DocumentValue::Object(fields) = value else {
            return Err(RevisionsError::ReservedStateInvalid);
        };
        let positive = |fields: &BTreeMap<String, DocumentValue>, key: &str| -> Option<u64> {
            match fields.get(key) {
                Some(DocumentValue::Integer(number)) if *number > 0 => u64::try_from(*number).ok(),
                _ => None,
            }
        };
        let Some(revision) = positive(fields, "revision") else {
            return Err(RevisionsError::ReservedStateInvalid);
        };
        let contract = match (fields.get("version"), fields.get("sequence")) {
            (Some(DocumentValue::Integer(version)), Some(DocumentValue::Integer(sequence)))
                if *version > 0 && *sequence > 0 =>
            {
                let non_negative = |key: &str| -> Option<u64> {
                    match fields.get(key) {
                        Some(DocumentValue::Integer(value)) if *value >= 0 => {
                            u64::try_from(*value).ok()
                        }
                        _ => None,
                    }
                };
                let (ticks, counter) = (
                    non_negative("stamp_ticks").ok_or(RevisionsError::ReservedStateInvalid)?,
                    non_negative("stamp_counter").ok_or(RevisionsError::ReservedStateInvalid)?,
                );
                Some(
                    DocumentVersionContract::try_new(
                        u64::try_from(*version)
                            .map_err(|_| RevisionsError::ReservedStateInvalid)?,
                        u64::try_from(*sequence)
                            .map_err(|_| RevisionsError::ReservedStateInvalid)?,
                        ReplicationClockStamp::new(ticks, counter),
                    )
                    .map_err(|_| RevisionsError::ReservedStateInvalid)?,
                )
            }
            (Some(DocumentValue::Null), Some(DocumentValue::Null)) | (None, None) => None,
            _ => return Err(RevisionsError::ReservedStateInvalid),
        };
        let reverted_to = match fields.get("reverted_to") {
            None | Some(DocumentValue::Null) => None,
            Some(DocumentValue::Integer(number)) if *number > 0 => {
                Some(u64::try_from(*number).map_err(|_| RevisionsError::ReservedStateInvalid)?)
            }
            _ => return Err(RevisionsError::ReservedStateInvalid),
        };
        let mut attachments = Vec::new();
        match fields.get("attachments") {
            None | Some(DocumentValue::Null) => {}
            Some(DocumentValue::Array(items)) => {
                for item in items {
                    let DocumentValue::Object(fields) = item else {
                        return Err(RevisionsError::ReservedStateInvalid);
                    };
                    let text = |key: &str| -> Option<String> {
                        match fields.get(key) {
                            Some(DocumentValue::String(value)) => Some(value.clone()),
                            _ => None,
                        }
                    };
                    let size = match fields.get("size") {
                        Some(DocumentValue::Integer(size)) if *size >= 0 => {
                            u64::try_from(*size).ok()
                        }
                        _ => None,
                    };
                    let (Some(name), Some(content_type), Some(size), Some(digest)) =
                        (text("name"), text("content_type"), size, text("digest"))
                    else {
                        return Err(RevisionsError::ReservedStateInvalid);
                    };
                    attachments.push(RevisionAttachmentRef::new(name, content_type, size, digest));
                }
            }
            _ => return Err(RevisionsError::ReservedStateInvalid),
        }
        Ok(Self { revision, contract, reverted_to, attachments })
    }

    /// Encodes one entry.
    fn encode(&self) -> DocumentValue {
        fn optional_u64(value: Option<u64>) -> DocumentValue {
            match value {
                Some(value) => DocumentValue::Integer(i64::try_from(value).unwrap_or(i64::MAX)),
                None => DocumentValue::Null,
            }
        }
        let (version, sequence, ticks, counter) = match self.contract {
            Some(contract) => (
                DocumentValue::Integer(i64::try_from(contract.version()).unwrap_or(i64::MAX)),
                DocumentValue::Integer(
                    i64::try_from(contract.sequence().get()).unwrap_or(i64::MAX),
                ),
                DocumentValue::Integer(i64::try_from(contract.stamp().ticks()).unwrap_or(i64::MAX)),
                DocumentValue::Integer(
                    i64::try_from(contract.stamp().counter()).unwrap_or(i64::MAX),
                ),
            ),
            None => (
                DocumentValue::Null,
                DocumentValue::Null,
                DocumentValue::Integer(0),
                DocumentValue::Integer(0),
            ),
        };
        DocumentValue::Object(
            [
                ("revision", optional_u64(Some(self.revision))),
                ("version", version),
                ("sequence", sequence),
                ("stamp_ticks", ticks),
                ("stamp_counter", counter),
                ("reverted_to", optional_u64(self.reverted_to)),
                (
                    "attachments",
                    DocumentValue::Array(
                        self.attachments
                            .iter()
                            .map(|reference| {
                                DocumentValue::Object(
                                    [
                                        (
                                            "name",
                                            DocumentValue::String(reference.name().to_owned()),
                                        ),
                                        (
                                            "content_type",
                                            DocumentValue::String(
                                                reference.content_type().to_owned(),
                                            ),
                                        ),
                                        (
                                            "size",
                                            DocumentValue::Integer(
                                                i64::try_from(reference.size()).unwrap_or(0),
                                            ),
                                        ),
                                        (
                                            "digest",
                                            DocumentValue::String(reference.digest().to_owned()),
                                        ),
                                    ]
                                    .into_iter()
                                    .map(|(key, value)| (key.to_owned(), value))
                                    .collect(),
                                )
                            })
                            .collect(),
                    ),
                ),
            ]
            .into_iter()
            .map(|(key, value)| (key.to_owned(), value))
            .collect(),
        )
    }
}

// ---------------------------------------------------------------------------
// Policy arithmetic
// ---------------------------------------------------------------------------

/// Applies one collection policy to a document's revision log and side car.
///
/// This is V1's `GetRevisionsForCollectionOrDefault`
/// (`RevisionsStorage.cs:754-835`): with a minimum count set, the deletion
/// budget is the count above that minimum, capped by the per-update maximum;
/// with only a minimum age set, every revision past the age is deletable up
/// to the cap. Deletion proceeds oldest-first and stops at the first
/// revision younger than the age floor — in a revision-ordered log every
/// later revision is younger still, so the stop covers V1's per-update
/// behavior and its enforce-mode skip alike. The returned flag is V1's
/// `HasMore`: the cap, not the policy, stopped the deletion
/// (`RevisionsStorage.cs:833`).
fn apply_policy(
    log: &mut RevisionRecord,
    side: &mut SnapshotRecord,
    configuration: &CollectionRevisionsConfiguration,
    now_ms: i64,
) -> (u64, bool) {
    if configuration.is_disabled() {
        return (0, false);
    }
    let count = log.entries.len();
    let minimum = configuration.minimum_revisions_to_keep();
    let age = configuration.minimum_revision_age_to_keep_ms();
    if minimum.is_none() && age.is_none() {
        // No count or age limit exists (`RevisionsStorage.cs:768-772`).
        return (0, false);
    }
    let mut budget = match minimum {
        Some(minimum) => count.saturating_sub(usize::try_from(minimum).unwrap_or(usize::MAX)),
        None => count,
    };
    let mut capped = false;
    if budget > 0
        && let Some(cap) = configuration.maximum_revisions_to_delete_upon_update()
        && let Ok(cap) = usize::try_from(cap)
        && cap < budget
    {
        budget = cap;
        capped = true;
    }
    if budget == 0 {
        return (0, false);
    }
    let cutoff = age.map(|age| now_ms.saturating_sub(age));
    let mut drop = 0_usize;
    for (_, at, _) in &log.entries {
        if drop >= budget {
            break;
        }
        // A revision becomes deletable once its age exceeds the floor
        // (`RevisionsStorage.cs:806-818`: `now - LastModified <= age` keeps).
        if let Some(cutoff) = cutoff
            && *at >= cutoff
        {
            break;
        }
        drop += 1;
    }
    if drop == 0 {
        return (0, false);
    }
    log.entries.drain(..drop);
    side.retain_log(log);
    // The per-document W3-F policy may still bound the log; keep its trim
    // and reflect it in the side car.
    log.trim_to_policy();
    side.retain_log(log);
    let removed = u64::try_from(drop).unwrap_or(u64::MAX);
    (removed, capped && usize::try_from(removed).is_ok_and(|removed| removed >= budget))
}

/// Reads the tenant's recorded configuration through one transaction.
fn load_configuration(
    records: &mut Records<'_, '_>,
) -> Result<RevisionsDatabaseConfiguration, RevisionsError> {
    match records.read(&configuration_id()?)? {
        None => Ok(RevisionsDatabaseConfiguration::absent()),
        Some(value) => decode_configuration(&value),
    }
}

/// Reads one document's revision log, defaulting to the empty log.
fn load_log(
    records: &mut Records<'_, '_>,
    document: &DocumentId,
) -> Result<RevisionRecord, RevisionsError> {
    let id =
        record_id("revisions", &[document.as_str()]).map_err(|_| RevisionsError::StorageFailure)?;
    match records.read(&id)? {
        None => Ok(RevisionRecord::default()),
        Some(value) => RevisionRecord::decode(&value).map_err(RevisionsError::from),
    }
}

/// Reads one document's snapshot side car, defaulting to empty.
fn load_side(
    records: &mut Records<'_, '_>,
    document: &DocumentId,
) -> Result<SnapshotRecord, RevisionsError> {
    match records.read(&snapshot_id(document)?)? {
        None => Ok(SnapshotRecord::default()),
        Some(value) => SnapshotRecord::decode(&value),
    }
}

/// Applies the shared document-bound guard in the family's fixed order:
/// tombstone, presence, expected version — the order of `guard_document`
/// (`src/families/store.rs:184-219`) minus the schema check, which a
/// value-copying snapshot has no user write to gate (a revert restores user
/// state and applies the schema step separately).
fn guard_document(
    records: &mut Records<'_, '_>,
    document: &DocumentId,
    expected: ExpectedVersion,
) -> Result<(CollectionName, u64, DocumentValue), RevisionsError> {
    let observed = records.user_document(document)?;
    let observed_version = observed.as_ref().map_or(0, |(_, version, _)| *version);
    let tombstone_id = record_id("tombstones", &[document.as_str()])
        .map_err(|_| RevisionsError::StorageFailure)?;
    let blocked = match records.read(&tombstone_id)? {
        None => false,
        Some(value) => TombstoneRecord::decode(&value)
            .map_err(RevisionsError::from)?
            .deletion_version(super::FamilyKind::Revisions)
            .is_some_and(|version| version > observed_version),
    };
    if blocked {
        return Err(RevisionsError::TombstoneConflict);
    }
    let (collection, version, value) = observed.ok_or(RevisionsError::DocumentMissing)?;
    match expected {
        ExpectedVersion::Any => {}
        ExpectedVersion::Exact(wanted) if wanted == version => {}
        ExpectedVersion::New | ExpectedVersion::Exact(_) => return Err(RevisionsError::Conflict),
    }
    Ok((collection, version, value))
}

/// Validates one value against its collection's schema policy, mirroring
/// the family guard's schema step (`src/families/store.rs:204-210`).
fn check_schema(
    records: &mut Records<'_, '_>,
    collection: &CollectionName,
    value: &DocumentValue,
) -> Result<(), RevisionsError> {
    let policy_id = record_id("validation", &[collection.as_str()])
        .map_err(|_| RevisionsError::StorageFailure)?;
    if let Some(value_record) = records.read(&policy_id)? {
        let schema = ValidationRecord::decode(&value_record).map_err(RevisionsError::from)?.schema;
        if !violations(&schema, value).is_empty() {
            return Err(RevisionsError::SchemaViolation);
        }
    }
    Ok(())
}

/// Reads the document's current attachment references — the GAP-F01
/// content-addressed plane's metadata family — never bytes.
fn attachment_references(
    records: &mut Records<'_, '_>,
    document: &DocumentId,
) -> Result<Vec<RevisionAttachmentRef>, RevisionsError> {
    let id = record_id("attachments", &[document.as_str()])
        .map_err(|_| RevisionsError::StorageFailure)?;
    let record = match records.read(&id)? {
        None => AttachmentRecord::default(),
        Some(value) => AttachmentRecord::decode(&value).map_err(RevisionsError::from)?,
    };
    Ok(record
        .entries
        .iter()
        .map(|(name, entry)| {
            RevisionAttachmentRef::new(
                name.clone(),
                entry.content_type.clone(),
                entry.size,
                entry.hash.clone(),
            )
        })
        .collect())
}

/// Resolves the published version contract of one document through the
/// transaction's Engine, failing closed when the publication identity
/// cannot be proven — GAP-D02's HEAD semantics (`src/engine/metadata.rs`).
fn published_contract(
    transaction: &Transaction<'_>,
    collection: &CollectionName,
    document: &DocumentId,
    observed_version: u64,
) -> Result<DocumentVersionContract, RevisionsError> {
    match transaction
        .head(collection, document, HeadCondition::Any)
        .map_err(|error| map_head_error(&error))?
    {
        DocumentHead::Missing => Err(RevisionsError::StorageFailure),
        DocumentHead::Current(contract) | DocumentHead::Unchanged(contract) => {
            if contract.version() != observed_version {
                // The transaction's view and the published history disagree:
                // a staged write's publication identity does not exist yet,
                // and the contract is a publication fact, never a guess.
                return Err(RevisionsError::StorageFailure);
            }
            Ok(contract)
        }
    }
}

// ---------------------------------------------------------------------------
// The public operations
// ---------------------------------------------------------------------------

impl<'engine> super::FamilyStore<'engine> {
    /// Stages one whole-tenant revisions configuration — V1's database
    /// record `RevisionsConfiguration` (a default plus per-collection map).
    ///
    /// The write replaces the recorded configuration atomically; the
    /// revisions bin's minimum entry age rides the same record.
    ///
    /// # Errors
    ///
    /// Returns [`RevisionsError::TransactionIdentityMismatch`] when the
    /// write names another operation identity, the binding cases for a
    /// transaction not live under the write's scope, `DeadlineExceeded` or
    /// `Cancelled` before staging, and the reserved-state cases for an
    /// unreadable prior record.
    #[allow(clippy::needless_pass_by_value)] // the seam contract passes payloads by value
    pub fn stage_revisions_configuration(
        &self,
        transaction: &mut Transaction<'engine>,
        write: RevisionsConfigurationWrite,
    ) -> Result<(), RevisionsError> {
        if transaction.operation_id() != write.operation_id() {
            return Err(RevisionsError::TransactionIdentityMismatch);
        }
        if write.deadline().is_expired() {
            return Err(RevisionsError::DeadlineExceeded);
        }
        if write.cancellation().is_cancelled() {
            return Err(RevisionsError::Cancelled);
        }
        self.bind_transaction(transaction, write.tenant_scope())?;
        let id = configuration_id()?;
        let mut records = Records::new(transaction);
        // A foreign record occupying the identifier is corruption, never a
        // configuration to merge or overwrite.
        if let Some(existing) = records.read(&id)?
            && decode_configuration(&existing).is_err()
        {
            return Err(RevisionsError::ReservedStateInvalid);
        }
        records.write(id, encode_configuration(write.configuration())).map_err(RevisionsError::from)
    }

    /// Reads the tenant's revisions configuration from one snapshot context.
    ///
    /// An absent record reads as the absent configuration: no default, no
    /// per-collection policies, and V1's default bin minimum age.
    ///
    /// # Errors
    ///
    /// Returns the context-binding cases for an invalid context,
    /// `DeadlineExceeded` or `Cancelled` before the read, and the
    /// reserved-state or storage cases for unreadable records.
    #[allow(clippy::needless_pass_by_value)] // the seam contract passes the context by value
    pub fn read_revisions_configuration(
        &self,
        context: FamilyReadContext,
        read: RevisionsConfigurationRead,
    ) -> Result<RevisionsDatabaseConfiguration, RevisionsError> {
        FamilyReadContext::validate(context.tenant(), context.snapshot())?;
        if read.deadline().is_expired() {
            return Err(RevisionsError::DeadlineExceeded);
        }
        if read.cancellation().is_cancelled() {
            return Err(RevisionsError::Cancelled);
        }
        let operation = super::store::internal_read_operation()?;
        let mut transaction = self
            .engine
            .begin(context.tenant(), context.snapshot().clone(), operation)
            .map_err(|error| RevisionsError::from(super::map_begin_error(&error)))?;
        load_configuration(&mut Records::new(&mut transaction))
    }

    /// Stages one revision snapshot: the document's published value, its
    /// GAP-D02 version contract, and its attachment references, recorded as
    /// one new revision under the collection's effective policy.
    ///
    /// The collection's effective configuration must exist and be enabled;
    /// an unconfigured or disabled collection refuses the snapshot — V1's
    /// resolution order (`RevisionsStorage.cs:176-194`, empty configuration
    /// at `:62`/`:193`). The policy then applies to the whole log exactly
    /// as V1 applies `DeleteOldRevisions` on every document update.
    ///
    /// # Errors
    ///
    /// Returns [`RevisionsError::RevisionsDisabled`] for an unconfigured or
    /// disabled collection, [`RevisionsError::DocumentMissing`] for an
    /// absent document, [`RevisionsError::Conflict`] for a contradicted
    /// expected version, the tombstone case for a newer recorded family
    /// tombstone, the identity and binding cases of the seam, and the
    /// storage cases when the publication identity cannot be proven.
    #[allow(clippy::needless_pass_by_value)] // the seam contract passes payloads by value
    pub fn stage_revision_snapshot(
        &self,
        transaction: &mut Transaction<'engine>,
        write: RevisionSnapshotWrite,
    ) -> Result<RevisionSnapshotRecorded, RevisionsError> {
        if transaction.operation_id() != write.operation_id() {
            return Err(RevisionsError::TransactionIdentityMismatch);
        }
        if write.deadline().is_expired() {
            return Err(RevisionsError::DeadlineExceeded);
        }
        if write.cancellation().is_cancelled() {
            return Err(RevisionsError::Cancelled);
        }
        self.bind_transaction(transaction, write.tenant_scope())?;
        let document = write.document().clone();
        let now_ms = write.now_ms();
        let (collection, version, value) = {
            let mut records = Records::new(transaction);
            guard_document(&mut records, &document, write.expected())?
        };
        // The contract read borrows the transaction immutably, so it runs
        // between the two record-access scopes.
        let contract = published_contract(transaction, &collection, &document, version)?;
        let mut records = Records::new(transaction);
        let configuration = load_configuration(&mut records)?;
        let Some(policy) = configuration.effective(&collection).copied() else {
            return Err(RevisionsError::RevisionsDisabled);
        };
        let references = attachment_references(&mut records, &document)?;

        let mut log = load_log(&mut records, &document)?;
        let mut side = load_side(&mut records, &document)?;
        let revision = log.push(now_ms, value.clone());
        side.collection = Some(collection.clone());
        side.entries.push(SnapshotEntry {
            revision,
            contract: Some(contract),
            reverted_to: None,
            attachments: references.clone(),
        });
        let (removed_by_policy, has_more) = apply_policy(&mut log, &mut side, &policy, now_ms);

        // Everything validated; stage the two records now.
        let log_id = record_id("revisions", &[document.as_str()])
            .map_err(|_| RevisionsError::StorageFailure)?;
        let log_empty = log.entries.is_empty() && log.policy.is_none();
        write_or_clear(&mut records, log_id, log_empty, log.encode())?;
        write_or_clear(&mut records, snapshot_id(&document)?, side.is_empty(), side.encode())?;
        Ok(RevisionSnapshotRecorded::new(
            revision,
            contract,
            references.len(),
            removed_by_policy,
            has_more,
        ))
    }

    /// Reads one document's historical revisions joined with the GAP-F05
    /// side car: values from the W3-F log; contracts, revert provenance,
    /// and attachment references where they were captured.
    ///
    /// `at_revision` selects one exact revision and fails with
    /// [`RevisionsError::ReferenceInvalid`] when that revision does not
    /// exist — V1's single-revision get; `None` pages the whole log.
    ///
    /// # Errors
    ///
    /// Returns the context-binding cases for an invalid context,
    /// `DeadlineExceeded` or `Cancelled` before the read,
    /// [`RevisionsError::ReferenceInvalid`] for an unknown exact revision,
    /// and the reserved-state or storage cases for unreadable records.
    #[allow(clippy::needless_pass_by_value)] // the seam contract passes the context by value
    pub fn read_revision_snapshots(
        &self,
        context: FamilyReadContext,
        read: RevisionSnapshotsRead,
    ) -> Result<RevisionSnapshotPage, RevisionsError> {
        FamilyReadContext::validate(context.tenant(), context.snapshot())?;
        if read.deadline().is_expired() {
            return Err(RevisionsError::DeadlineExceeded);
        }
        if read.cancellation().is_cancelled() {
            return Err(RevisionsError::Cancelled);
        }
        let operation = super::store::internal_read_operation()?;
        let mut transaction = self
            .engine
            .begin(context.tenant(), context.snapshot().clone(), operation)
            .map_err(|error| RevisionsError::from(super::map_begin_error(&error)))?;
        let mut records = Records::new(&mut transaction);
        let log = load_log(&mut records, read.document())?;
        let side = load_side(&mut records, read.document())?;
        if let Some(wanted) = read.at_revision()
            && !log.entries.iter().any(|(revision, _, _)| *revision == wanted)
        {
            return Err(RevisionsError::ReferenceInvalid);
        }
        let total = u64::try_from(log.entries.len()).unwrap_or(u64::MAX);
        let start = usize::try_from(read.paging().offset()).unwrap_or(usize::MAX);
        let limit = usize::try_from(read.paging().limit()).unwrap_or(usize::MAX);
        let entries = log
            .entries
            .iter()
            .filter(|(revision, _, _)| read.at_revision().is_none_or(|wanted| *revision == wanted))
            .skip(start)
            .take(limit)
            .map(|(revision, at, value)| {
                let entry = side.entry(*revision);
                RevisionSnapshotEntry::new(
                    *revision,
                    *at,
                    value.clone(),
                    entry.and_then(|entry| entry.contract),
                    entry.and_then(|entry| entry.reverted_to),
                    entry.map(|entry| entry.attachments.clone()).unwrap_or_default(),
                )
            })
            .collect();
        Ok(RevisionSnapshotPage::new(entries, total))
    }

    /// Stages one revert addressed by the GAP-D02 version contract — V1's
    /// `RevertDocumentsCommand` semantics (`RevisionsStorage.cs:2571-2620`):
    /// verify the revision exists and belongs to the document
    /// (`VerifyAndGetRevision`, `:2623-2643`), restore its value as a new
    /// revision, rewrite the user document, and re-put the snapshot's
    /// attachment references by digest (`DeleteAttachmentBeforeRevert` then
    /// `PutAttachmentRevert`, `AttachmentsStorage.cs:590`, `:611-652`) —
    /// never bytes.
    ///
    /// # Errors
    ///
    /// Returns [`RevisionsError::ReferenceInvalid`] when no revision holds
    /// the contract, [`RevisionsError::DocumentMissing`] for an absent
    /// document, [`RevisionsError::Conflict`] for a contradicted expected
    /// version, [`RevisionsError::SchemaViolation`] when the restored value
    /// violates the collection's schema policy, and the seam's identity,
    /// binding, deadline, and cancellation cases.
    #[allow(clippy::needless_pass_by_value)] // the seam contract passes payloads by value
    pub fn stage_revert_to_contract(
        &self,
        transaction: &mut Transaction<'engine>,
        write: RevertToContractWrite,
    ) -> Result<RevertOutcome, RevisionsError> {
        if transaction.operation_id() != write.operation_id() {
            return Err(RevisionsError::TransactionIdentityMismatch);
        }
        if write.deadline().is_expired() {
            return Err(RevisionsError::DeadlineExceeded);
        }
        if write.cancellation().is_cancelled() {
            return Err(RevisionsError::Cancelled);
        }
        self.bind_transaction(transaction, write.tenant_scope())?;
        let document = write.document().clone();
        let contract = *write.contract();
        let now_ms = write.now_ms();
        let mut records = Records::new(transaction);
        let (collection, _, _) = guard_document(&mut records, &document, write.expected())?;
        let log = load_log(&mut records, &document)?;
        let side = load_side(&mut records, &document)?;
        let Some((revision, _, restored)) = log.entries.iter().find(|(revision, _, _)| {
            side.entry(*revision).is_some_and(|entry| entry.contract == Some(contract))
        }) else {
            return Err(RevisionsError::ReferenceInvalid);
        };
        let (revision, restored) = (*revision, restored.clone());
        let restored_references =
            side.entry(revision).map(|entry| entry.attachments.clone()).unwrap_or_default();
        // The restore rewrites user state, so the family guard's schema step
        // applies to the restored value (store.rs:204-210 order).
        check_schema(&mut records, &collection, &restored)?;

        let mut log = log;
        let mut side = side;
        let created = log.push(now_ms, restored.clone());
        side.collection = Some(collection.clone());
        side.entries.push(SnapshotEntry {
            revision: created,
            contract: None,
            reverted_to: Some(revision),
            attachments: restored_references.clone(),
        });
        let configuration = load_configuration(&mut records)?;
        if let Some(policy) = configuration.effective(&collection).copied() {
            let _ = apply_policy(&mut log, &mut side, &policy, now_ms);
        }
        side.retain_log(&log);

        // Everything validated; stage the user document, the attachment
        // metadata replacement, and both revision records.
        records
            .write_user_document(document.clone(), collection.clone(), restored)
            .map_err(RevisionsError::from)?;
        let attachment_id = record_id("attachments", &[document.as_str()])
            .map_err(|_| RevisionsError::StorageFailure)?;
        let mut attachment_record = AttachmentRecord::default();
        for reference in &restored_references {
            attachment_record.entries.insert(
                reference.name().to_owned(),
                super::state::AttachmentEntry {
                    content_type: reference.content_type().to_owned(),
                    size: reference.size(),
                    hash: reference.digest().to_owned(),
                },
            );
        }
        let attachment_empty = attachment_record.entries.is_empty();
        write_or_clear(&mut records, attachment_id, attachment_empty, attachment_record.encode())?;
        let log_id = record_id("revisions", &[document.as_str()])
            .map_err(|_| RevisionsError::StorageFailure)?;
        let log_empty = log.entries.is_empty() && log.policy.is_none();
        write_or_clear(&mut records, log_id, log_empty, log.encode())?;
        write_or_clear(&mut records, snapshot_id(&document)?, side.is_empty(), side.encode())?;
        Ok(RevertOutcome::new(created, restored_references.len()))
    }

    /// Stages one policy enforcement for one document — V1's
    /// `EnforceRevisionsConfigurationOperation` narrowed to a single
    /// document: apply the effective collection policy to the revision log
    /// and, when the document is deleted and the policy says
    /// purge-on-delete, remove the whole log (`DeleteOldRevisions`' deleted
    /// branch, `RevisionsStorage.cs:627-631`).
    ///
    /// A deleted document's collection is the one its snapshots recorded;
    /// a log without GAP-F05 snapshots carries no collection, so no policy
    /// applies and the bin cleaner owns it.
    ///
    /// # Errors
    ///
    /// Returns the seam's identity, binding, deadline, and cancellation
    /// cases, and the reserved-state or storage cases for unreadable
    /// records.
    #[allow(clippy::needless_pass_by_value)] // the seam contract passes payloads by value
    pub fn stage_enforce_revision_policy(
        &self,
        transaction: &mut Transaction<'engine>,
        write: EnforceRevisionsPolicyWrite,
    ) -> Result<PolicyEnforcementOutcome, RevisionsError> {
        if transaction.operation_id() != write.operation_id() {
            return Err(RevisionsError::TransactionIdentityMismatch);
        }
        if write.deadline().is_expired() {
            return Err(RevisionsError::DeadlineExceeded);
        }
        if write.cancellation().is_cancelled() {
            return Err(RevisionsError::Cancelled);
        }
        self.bind_transaction(transaction, write.tenant_scope())?;
        let document = write.document().clone();
        let now_ms = write.now_ms();
        let mut records = Records::new(transaction);
        let mut log = load_log(&mut records, &document)?;
        let mut side = load_side(&mut records, &document)?;
        let present = records.user_document(&document)?.is_some();
        let configuration = load_configuration(&mut records)?;

        // Resolve the collection whose policy governs the log: the live
        // document's collection, else the collection its snapshots recorded
        // (the document is gone; V1 reads it from the revisions table,
        // `RevisionsStorage.cs:670`).
        let collection = if present {
            records.user_document(&document)?.map(|(collection, _, _)| collection)
        } else {
            side.collection.clone()
        };
        let policy = collection.as_ref().and_then(|collection| configuration.effective(collection));

        let mut outcome = PolicyEnforcementOutcome::new(
            0,
            u64::try_from(log.entries.len()).unwrap_or(u64::MAX),
            false,
            false,
        );
        if let Some(policy) = policy {
            if !present && policy.purge_on_delete() {
                let removed = outcome.remaining;
                log.entries.clear();
                side.entries.clear();
                side.collection = None;
                outcome = PolicyEnforcementOutcome::new(removed, 0, true, false);
            } else {
                let (removed, has_more) = apply_policy(&mut log, &mut side, policy, now_ms);
                side.retain_log(&log);
                outcome = PolicyEnforcementOutcome::new(
                    removed,
                    u64::try_from(log.entries.len()).unwrap_or(u64::MAX),
                    false,
                    has_more,
                );
            }
        }

        let log_id = record_id("revisions", &[document.as_str()])
            .map_err(|_| RevisionsError::StorageFailure)?;
        let log_empty = log.entries.is_empty() && log.policy.is_none();
        write_or_clear(&mut records, log_id, log_empty, log.encode())?;
        write_or_clear(&mut records, snapshot_id(&document)?, side.is_empty(), side.encode())?;
        Ok(outcome)
    }

    /// Reads one page of the revisions bin: the deleted documents whose
    /// revision log's newest revision was recorded strictly before the
    /// cutoff — V1's `GetDeletedRevisionsIds(before, batchSize, ...)`
    /// (`RevisionsBinCleaner.cs:97-109`).
    ///
    /// # Errors
    ///
    /// Returns the context-binding cases for an invalid context,
    /// `DeadlineExceeded` or `Cancelled` before the read, and the
    /// reserved-state or storage cases for unreadable records.
    #[allow(clippy::needless_pass_by_value)] // the seam contract passes the context by value
    pub fn read_revisions_bin(
        &self,
        context: FamilyReadContext,
        read: RevisionsBinRead,
    ) -> Result<RevisionsBinPage, RevisionsError> {
        FamilyReadContext::validate(context.tenant(), context.snapshot())?;
        if read.deadline().is_expired() {
            return Err(RevisionsError::DeadlineExceeded);
        }
        if read.cancellation().is_cancelled() {
            return Err(RevisionsError::Cancelled);
        }
        let operation = super::store::internal_read_operation()?;
        let mut transaction = self
            .engine
            .begin(context.tenant(), context.snapshot().clone(), operation)
            .map_err(|error| RevisionsError::from(super::map_begin_error(&error)))?;
        let documents = self
            .engine
            .snapshot_documents(context.tenant(), context.snapshot())
            .map_err(|_| RevisionsError::StorageFailure)?;
        let prefix = "family:revisions/";
        let mut candidates = Vec::new();
        for document in &documents {
            if document.collection().as_str() != RESERVED_COLLECTION {
                continue;
            }
            let Some(owner) = document.id().as_str().strip_prefix(prefix) else {
                continue;
            };
            if owner.is_empty() || owner.contains('/') {
                continue;
            }
            let owner =
                DocumentId::try_new(owner).map_err(|_| RevisionsError::ReservedStateInvalid)?;
            let log = RevisionRecord::decode(document.value()).map_err(RevisionsError::from)?;
            let Some(newest) = log.entries.last() else {
                continue;
            };
            if newest.1 >= read.before_ms() {
                continue;
            }
            // A live document — deleted and re-created — is never a bin
            // entry, whatever its revision log's age.
            let mut records = Records::new(&mut transaction);
            if records.user_document(&owner)?.is_some() {
                continue;
            }
            candidates.push(RevisionsBinEntry::new(
                owner,
                newest.0,
                newest.1,
                u64::try_from(log.entries.len()).unwrap_or(u64::MAX),
            ));
        }
        candidates.sort_by(|left, right| left.document().cmp(right.document()));
        let limit = usize::try_from(read.limit()).unwrap_or(usize::MAX);
        let has_more = candidates.len() > limit;
        candidates.truncate(limit);
        Ok(RevisionsBinPage::new(candidates, has_more))
    }

    /// Stages one revisions-bin clean: purge the listed documents' revision
    /// logs and snapshot side cars — V1's `RevisionsBinCleanMergedCommand`
    /// under the cleaner's age rule (`RevisionsBinCleaner.cs:97`, `:118-119`).
    ///
    /// Eligibility is re-checked for every candidate — the document must be
    /// deleted and its newest revision strictly older than the cutoff — and
    /// one ineligible candidate refuses the whole write before anything is
    /// staged, leaving the caller's transaction unchanged. A candidate with
    /// no stored revisions is skipped, like V1's "no revisions found" info
    /// path (`RevisionsStorage.cs:671-675`).
    ///
    /// # Errors
    ///
    /// Returns [`RevisionsError::BinEntryNotEligible`] for a live document
    /// or a too-young log, and the seam's identity, binding, deadline, and
    /// cancellation cases.
    #[allow(clippy::needless_pass_by_value)] // the seam contract passes payloads by value
    pub fn stage_revisions_bin_clean(
        &self,
        transaction: &mut Transaction<'engine>,
        write: RevisionsBinCleanWrite,
    ) -> Result<RevisionsBinCleanOutcome, RevisionsError> {
        if transaction.operation_id() != write.operation_id() {
            return Err(RevisionsError::TransactionIdentityMismatch);
        }
        if write.deadline().is_expired() {
            return Err(RevisionsError::DeadlineExceeded);
        }
        if write.cancellation().is_cancelled() {
            return Err(RevisionsError::Cancelled);
        }
        self.bind_transaction(transaction, write.tenant_scope())?;
        let mut records = Records::new(transaction);
        // Validate every candidate before staging anything: a rejected
        // clean leaves the caller's transaction clean.
        let mut eligible = Vec::new();
        for document in write.documents() {
            let log = load_log(&mut records, document)?;
            if log.entries.is_empty() {
                continue;
            }
            if records.user_document(document)?.is_some() {
                return Err(RevisionsError::BinEntryNotEligible);
            }
            let Some(newest) = log.entries.last() else { continue };
            if newest.1 >= write.before_ms() {
                return Err(RevisionsError::BinEntryNotEligible);
            }
            eligible.push(document.clone());
        }
        for document in &eligible {
            let log_id = record_id("revisions", &[document.as_str()])
                .map_err(|_| RevisionsError::StorageFailure)?;
            records.clear(&log_id).map_err(RevisionsError::from)?;
            records.clear(&snapshot_id(document)?).map_err(RevisionsError::from)?;
        }
        Ok(RevisionsBinCleanOutcome::new(u64::try_from(eligible.len()).unwrap_or(u64::MAX)))
    }
}

#[cfg(test)]
mod tests {
    use std::{
        path::PathBuf,
        sync::atomic::{AtomicU64, Ordering},
        time::Duration,
    };

    use super::{
        CollectionRevisionsConfiguration, DEFAULT_BIN_MINIMUM_AGE_MS, EnforceRevisionsPolicyWrite,
        MAX_BIN_BATCH_DOCUMENTS, PolicyEnforcementOutcome, RevertOutcome, RevertToContractWrite,
        RevisionSnapshotPage, RevisionSnapshotWrite, RevisionSnapshotsRead,
        RevisionsBinCleanOutcome, RevisionsBinCleanWrite, RevisionsBinPage, RevisionsBinRead,
        RevisionsConfigurationRead, RevisionsConfigurationWrite, RevisionsDatabaseConfiguration,
        RevisionsError,
    };
    use crate::engine::{CommitOutcome, Engine, EngineConfig, HeadCondition, Transaction};
    use crate::families::{
        AttachmentMutation, AttachmentRead, AttachmentWrite, FamilyError, FamilyReadContext,
        FamilyReadRequest, FamilyReadResult, FamilyWriteRequest, RevisionCommand, RevisionPaging,
        RevisionPolicy, RevisionRead, RevisionWrite,
    };
    use crate::operations::RequestValidationError;
    use crate::types::{
        Cancellation, CollectionName, Deadline, Document, DocumentId, DocumentValue,
        ExpectedVersion, LocalDurability, OperationId, TenantId, TenantScope,
    };

    // -----------------------------------------------------------------------
    // Fixtures (the W3-F in-crate convention)
    // -----------------------------------------------------------------------

    fn location(tag: &str) -> PathBuf {
        static COUNTER: AtomicU64 = AtomicU64::new(0);
        let index = COUNTER.fetch_add(1, Ordering::Relaxed);
        std::env::temp_dir().join(format!("tbv2-gapf05-{tag}-{}-{index}", std::process::id()))
    }

    fn open_engine(tag: &str) -> (PathBuf, Engine) {
        let dir = location(tag);
        let engine = Engine::open(EngineConfig::durable(&dir).expect("durable location"))
            .expect("engine opens");
        (dir, engine)
    }

    fn tenant(name: &str) -> TenantId {
        TenantId::try_new(name).expect("valid tenant")
    }

    fn document_id(name: &str) -> DocumentId {
        DocumentId::try_new(name).expect("valid identifier")
    }

    fn collection(name: &str) -> CollectionName {
        CollectionName::try_new(name).expect("valid identifier")
    }

    fn operation(name: &str) -> OperationId {
        OperationId::try_new(name).expect("valid identifier")
    }

    /// Mints a never-reused operation identity for repeated helper commits:
    /// the Engine's operation ledger refuses a reused identity.
    fn unique_operation(prefix: &str) -> OperationId {
        static COUNTER: AtomicU64 = AtomicU64::new(0);
        let index = COUNTER.fetch_add(1, Ordering::Relaxed);
        OperationId::try_new(&format!("{prefix}-{index}")).expect("valid identifier")
    }

    fn scope(engine: &Engine, tenant_name: &str) -> TenantScope {
        TenantScope::from_parts(tenant(tenant_name), engine.identity())
    }

    fn deadline() -> Deadline {
        Deadline::after(Duration::from_secs(45)).expect("valid deadline")
    }

    fn cancellation() -> Cancellation {
        Cancellation::new()
    }

    fn expect_committed(outcome: &CommitOutcome) {
        assert!(matches!(outcome, CommitOutcome::Committed(_)), "the transaction committed");
    }

    fn user_value(label: &str) -> DocumentValue {
        DocumentValue::Object(
            [("label".to_owned(), DocumentValue::String(label.to_owned()))].into_iter().collect(),
        )
    }

    fn commit<'engine>(
        engine: &'engine Engine,
        tenant_name: &str,
        operation_name: &str,
        stage: impl FnOnce(
            &crate::families::FamilyStore<'engine>,
            &mut Transaction<'engine>,
            &TenantScope,
            &OperationId,
        ),
    ) -> CommitOutcome {
        let tenant_scope = scope(engine, tenant_name);
        let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
        let identity = operation(operation_name);
        let store = engine.families();
        let mut transaction =
            engine.begin(&tenant_scope, snapshot, identity.clone()).expect("begins");
        stage(&store, &mut transaction, &tenant_scope, &identity);
        transaction.commit(LocalDurability::Synced)
    }

    fn put_user_document(
        engine: &Engine,
        tenant_name: &str,
        operation_name: &str,
        id: &str,
        value: DocumentValue,
    ) {
        expect_committed(&commit(
            engine,
            tenant_name,
            operation_name,
            |_store, transaction, _, _| {
                transaction
                    .put(
                        Document::new(document_id(id), collection("users"), value),
                        ExpectedVersion::Any,
                    )
                    .expect("stages document");
            },
        ));
    }

    fn delete_user_document(engine: &Engine, tenant_name: &str, operation_name: &str, id: &str) {
        expect_committed(&commit(
            engine,
            tenant_name,
            operation_name,
            |_store, transaction, _, _| {
                transaction.delete(document_id(id), ExpectedVersion::Any).expect("stages delete");
            },
        ));
    }

    fn snapshot_op(
        engine: &Engine,
        tenant_name: &str,
        document: &str,
        expected: ExpectedVersion,
    ) -> Result<super::RevisionSnapshotRecorded, RevisionsError> {
        snapshot_at(engine, tenant_name, document, expected, 1_000)
    }

    fn snapshot_at(
        engine: &Engine,
        tenant_name: &str,
        document: &str,
        expected: ExpectedVersion,
        now_ms: i64,
    ) -> Result<super::RevisionSnapshotRecorded, RevisionsError> {
        let tenant_scope = scope(engine, tenant_name);
        let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
        let identity = unique_operation("gapf05-snapshot");
        let store = engine.families();
        let mut transaction =
            engine.begin(&tenant_scope, snapshot, identity.clone()).expect("begins");
        let write = RevisionSnapshotWrite::try_new(
            tenant_scope.clone(),
            document_id(document),
            expected,
            now_ms,
            identity,
            deadline(),
            cancellation(),
        )
        .expect("valid snapshot write");
        let recorded = store.stage_revision_snapshot(&mut transaction, write)?;
        expect_committed(&transaction.commit(LocalDurability::Synced));
        Ok(recorded)
    }

    fn read_snapshots(
        engine: &Engine,
        tenant_name: &str,
        document: &str,
        at_revision: Option<u64>,
    ) -> Result<RevisionSnapshotPage, RevisionsError> {
        let read_scope = scope(engine, tenant_name);
        let snapshot = engine.read_snapshot(&read_scope).expect("snapshot");
        let context = FamilyReadContext::new(read_scope, snapshot);
        let read = RevisionSnapshotsRead::try_new(
            document_id(document),
            at_revision,
            RevisionPaging::try_new(0, 10).expect("valid paging"),
            deadline(),
            cancellation(),
        )
        .expect("valid snapshots read");
        engine.families().read_revision_snapshots(context, read)
    }

    fn revision_page_total(engine: &Engine, tenant_name: &str, document: &str) -> u64 {
        let read_scope = scope(engine, tenant_name);
        let snapshot = engine.read_snapshot(&read_scope).expect("snapshot");
        let context = FamilyReadContext::new(read_scope, snapshot);
        let read = RevisionRead::try_new(
            document_id(document),
            RevisionPaging::try_new(0, 10).expect("valid paging"),
            deadline(),
            cancellation(),
        )
        .expect("valid revision read");
        match engine
            .families()
            .read(context, FamilyReadRequest::revision_page(read))
            .expect("pages the revisions")
        {
            FamilyReadResult::RevisionPage(page) => page.total(),
            other => panic!("expected a revision page, got {other:?}"),
        }
    }

    fn enable(engine: &Engine, tenant_name: &str, configuration: CollectionRevisionsConfiguration) {
        enable_map(engine, tenant_name, Some(configuration), vec![]);
    }

    fn enable_for_collection(
        engine: &Engine,
        tenant_name: &str,
        name: &CollectionName,
        configuration: CollectionRevisionsConfiguration,
    ) {
        enable_map(engine, tenant_name, None, vec![(name.clone(), configuration)]);
    }

    fn enable_map(
        engine: &Engine,
        tenant_name: &str,
        default: Option<CollectionRevisionsConfiguration>,
        collections: Vec<(CollectionName, CollectionRevisionsConfiguration)>,
    ) {
        let database = RevisionsDatabaseConfiguration::try_new(
            default,
            collections,
            DEFAULT_BIN_MINIMUM_AGE_MS,
        )
        .expect("valid configuration");
        expect_committed(&commit(
            engine,
            tenant_name,
            unique_operation("configure-enabled").as_str(),
            |store, transaction, scope, identity| {
                let write = RevisionsConfigurationWrite::try_new(
                    scope.clone(),
                    database.clone(),
                    identity.clone(),
                    deadline(),
                    cancellation(),
                )
                .expect("valid configuration write");
                store.stage_revisions_configuration(transaction, write).expect("stages");
            },
        ));
    }

    /// Stages attachment metadata through the public attachments family.
    fn put_attachment_metadata(
        engine: &Engine,
        tenant_name: &str,
        document: &str,
        name: &str,
        digest: &str,
        size: u64,
    ) {
        expect_committed(&commit(
            engine,
            tenant_name,
            unique_operation("attachment-put").as_str(),
            |store, transaction, scope, identity| {
                let write = AttachmentWrite::try_new(
                    scope.clone(),
                    document_id(document),
                    AttachmentMutation::Store {
                        name: name.to_owned(),
                        content_type: "text/plain".to_owned(),
                        size,
                        hash: digest.to_owned(),
                        replace: true,
                    },
                    ExpectedVersion::Any,
                    identity.clone(),
                    deadline(),
                    cancellation(),
                )
                .expect("valid attachment write");
                store
                    .stage(transaction, FamilyWriteRequest::attachment_mutation(write))
                    .expect("stages attachment metadata");
            },
        ));
    }

    // -----------------------------------------------------------------------
    // Configuration
    // -----------------------------------------------------------------------

    /// Reads the snapshot side car's entry count straight from the reserved
    /// record: the lockstep invariant the public reads cannot show, because
    /// their totals come from the log. Repair-loop 1's warden finding.
    fn side_car_entries(engine: &Engine, tenant_name: &str, document: &str) -> usize {
        let tenant_scope = scope(engine, tenant_name);
        let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
        let identity = unique_operation("gapf05-side-read");
        let mut transaction = engine.begin(&tenant_scope, snapshot, identity).expect("begins");
        let mut records = crate::families::state::Records::new(&mut transaction);
        let id = super::snapshot_id(&document_id(document)).expect("side-car id");
        match records.read(&id) {
            Ok(None) => 0,
            Ok(Some(value)) => {
                super::SnapshotRecord::decode(&value).expect("decodes the side car").entries.len()
            }
            Err(error) => panic!("the side-car read failed: {error:?}"),
        }
    }

    /// Configures the W3-F per-document retention policy through the public
    /// revision command — the trimming agent the collection policy does not
    /// control.
    fn configure_w3f_policy(
        engine: &Engine,
        tenant_name: &str,
        document: &str,
        policy: RevisionPolicy,
    ) {
        expect_committed(&commit(
            engine,
            tenant_name,
            unique_operation("w3f-configure").as_str(),
            |store, transaction, scope, identity| {
                let write = RevisionWrite::try_new(
                    scope.clone(),
                    document_id(document),
                    RevisionCommand::Configure { policy },
                    identity.clone(),
                    deadline(),
                    cancellation(),
                )
                .expect("valid revision command");
                store
                    .stage(transaction, FamilyWriteRequest::revision_command(write))
                    .expect("stages the configure");
            },
        ));
    }

    #[test]
    fn configuration_limits_round_trip_exactly_and_refuse_the_codec_ceiling() {
        let (_dir, engine) = open_engine("config-ceiling");
        // A count limit past the reserved record's i64 integer ceiling
        // cannot round-trip: staged u64::MAX must not read back as
        // i64::MAX. The typed constructor refuses such limits instead of
        // silently clamping them.
        assert_eq!(
            CollectionRevisionsConfiguration::try_new(Some(u64::MAX), None, None, false, false)
                .map(|_| ()),
            Err(RequestValidationError::InvalidFamilyPayload),
            "a minimum-keep count past the codec ceiling is refused"
        );
        assert_eq!(
            CollectionRevisionsConfiguration::try_new(Some(1), None, Some(u64::MAX), false, false)
                .map(|_| ()),
            Err(RequestValidationError::InvalidFamilyPayload),
            "a per-update deletion cap past the codec ceiling is refused"
        );

        // The ceiling itself is the largest representable policy and
        // round-trips exactly through the reserved record.
        let ceiling = CollectionRevisionsConfiguration::try_new(
            Some(i64::MAX as u64),
            None,
            Some(i64::MAX as u64),
            false,
            false,
        )
        .expect("valid configuration");
        let database = RevisionsDatabaseConfiguration::try_new(
            Some(ceiling),
            vec![],
            DEFAULT_BIN_MINIMUM_AGE_MS,
        )
        .expect("valid database configuration");
        expect_committed(&commit(
            &engine,
            "t1",
            "configure-ceiling",
            |store, transaction, scope, identity| {
                let write = RevisionsConfigurationWrite::try_new(
                    scope.clone(),
                    database.clone(),
                    identity.clone(),
                    deadline(),
                    cancellation(),
                )
                .expect("valid configuration write");
                store
                    .stage_revisions_configuration(transaction, write)
                    .expect("stages the configuration");
            },
        ));
        let read_scope = scope(&engine, "t1");
        let snapshot = engine.read_snapshot(&read_scope).expect("snapshot");
        let context = FamilyReadContext::new(read_scope, snapshot);
        let read =
            RevisionsConfigurationRead::try_new(deadline(), cancellation()).expect("valid read");
        let stored = engine
            .families()
            .read_revisions_configuration(context, read)
            .expect("reads the configuration");
        assert_eq!(stored, database, "the codec ceiling round-trips exactly");
    }

    #[test]
    fn collection_configuration_validates_v1_field_shapes() {
        assert_eq!(
            CollectionRevisionsConfiguration::try_new(Some(0), None, None, false, false)
                .map(|_| ()),
            Err(RequestValidationError::InvalidFamilyPayload),
            "a zero minimum-keep count keeps nothing and is refused"
        );
        assert_eq!(
            CollectionRevisionsConfiguration::try_new(Some(5), Some(-1), None, false, false)
                .map(|_| ()),
            Err(RequestValidationError::InvalidFamilyPayload),
            "a negative minimum age is refused"
        );
        assert_eq!(
            CollectionRevisionsConfiguration::try_new(Some(5), None, Some(0), false, false)
                .map(|_| ()),
            Err(RequestValidationError::InvalidFamilyPayload),
            "a zero per-update deletion cap is refused"
        );

        let configured =
            CollectionRevisionsConfiguration::try_new(Some(5), Some(60_000), Some(64), true, false)
                .expect("valid configuration");
        assert_eq!(configured.minimum_revisions_to_keep(), Some(5));
        assert_eq!(configured.minimum_revision_age_to_keep_ms(), Some(60_000));
        assert_eq!(configured.maximum_revisions_to_delete_upon_update(), Some(64));
        assert!(configured.purge_on_delete());
        assert!(!configured.is_disabled());

        let disabled = CollectionRevisionsConfiguration::disabled();
        assert!(disabled.is_disabled());
        assert_eq!(disabled.minimum_revisions_to_keep(), None);

        // Both limits unset is V1's "no count or age limit" shape and stays
        // constructible.
        assert!(
            CollectionRevisionsConfiguration::try_new(None, None, None, true, false).is_ok(),
            "purge-only configurations are legal"
        );
    }

    #[test]
    fn database_configuration_resolves_collection_default_then_disabled() {
        let per_collection =
            CollectionRevisionsConfiguration::try_new(Some(3), None, None, false, false)
                .expect("valid per-collection configuration");
        let default = CollectionRevisionsConfiguration::try_new(Some(9), None, None, false, false)
            .expect("valid default configuration");
        let configuration = RevisionsDatabaseConfiguration::try_new(
            Some(default),
            vec![(collection("users"), per_collection)],
            DEFAULT_BIN_MINIMUM_AGE_MS,
        )
        .expect("valid database configuration");

        let resolved = configuration.effective(&collection("users")).expect("resolves");
        assert_eq!(resolved.minimum_revisions_to_keep(), Some(3));
        let resolved = configuration.effective(&collection("orders")).expect("resolves default");
        assert_eq!(resolved.minimum_revisions_to_keep(), Some(9));
        assert!(
            RevisionsDatabaseConfiguration::try_new(None, vec![], 0)
                .expect("an empty configuration is legal")
                .effective(&collection("users"))
                .is_none(),
            "an unconfigured collection has no effective configuration"
        );
        // A disabled default is no configuration at all.
        let disabled_default = RevisionsDatabaseConfiguration::try_new(
            Some(CollectionRevisionsConfiguration::disabled()),
            vec![],
            0,
        )
        .expect("valid configuration");
        assert!(disabled_default.effective(&collection("users")).is_none());
        assert_eq!(configuration.bin_minimum_age_ms(), DEFAULT_BIN_MINIMUM_AGE_MS);
        assert_eq!(
            RevisionsDatabaseConfiguration::try_new(None, vec![], -1).map(|_| ()),
            Err(RequestValidationError::InvalidFamilyPayload),
            "a negative bin age is refused"
        );
    }

    #[test]
    fn bin_defaults_mirror_v1_constants() {
        assert_eq!(DEFAULT_BIN_MINIMUM_AGE_MS, 30 * 24 * 60 * 60 * 1000);
        assert_eq!(MAX_BIN_BATCH_DOCUMENTS, 10 * 1024);
    }

    #[test]
    fn stage_revisions_configuration_persists_and_reads_the_whole_record() {
        let (_dir, engine) = open_engine("config");

        let first = RevisionsDatabaseConfiguration::try_new(
            Some(
                CollectionRevisionsConfiguration::try_new(Some(9), None, None, false, false)
                    .expect("valid default"),
            ),
            vec![],
            DEFAULT_BIN_MINIMUM_AGE_MS,
        )
        .expect("valid configuration");

        expect_committed(&commit(
            &engine,
            "t1",
            "configure-1",
            |store, transaction, scope, identity| {
                let write = RevisionsConfigurationWrite::try_new(
                    scope.clone(),
                    first.clone(),
                    identity.clone(),
                    deadline(),
                    cancellation(),
                )
                .expect("valid configuration write");
                store
                    .stage_revisions_configuration(transaction, write)
                    .expect("stages the configuration");
            },
        ));

        let read_scope = scope(&engine, "t1");
        let snapshot = engine.read_snapshot(&read_scope).expect("snapshot");
        let context = FamilyReadContext::new(read_scope, snapshot);
        let read =
            RevisionsConfigurationRead::try_new(deadline(), cancellation()).expect("valid read");
        let stored = engine
            .families()
            .read_revisions_configuration(context, read)
            .expect("reads the configuration");
        assert_eq!(stored, first);

        // A second configuration replaces the whole record atomically.
        let second = RevisionsDatabaseConfiguration::try_new(
            None,
            vec![(
                collection("users"),
                CollectionRevisionsConfiguration::try_new(Some(2), None, None, true, false)
                    .expect("valid per-collection configuration"),
            )],
            60_000,
        )
        .expect("valid replacement");
        expect_committed(&commit(
            &engine,
            "t1",
            "configure-2",
            |store, transaction, scope, identity| {
                let write = RevisionsConfigurationWrite::try_new(
                    scope.clone(),
                    second.clone(),
                    identity.clone(),
                    deadline(),
                    cancellation(),
                )
                .expect("valid configuration write");
                store
                    .stage_revisions_configuration(transaction, write)
                    .expect("stages the replacement");
            },
        ));

        let read_scope = scope(&engine, "t1");
        let snapshot = engine.read_snapshot(&read_scope).expect("snapshot");
        let context = FamilyReadContext::new(read_scope, snapshot);
        let read =
            RevisionsConfigurationRead::try_new(deadline(), cancellation()).expect("valid read");
        let stored = engine
            .families()
            .read_revisions_configuration(context, read)
            .expect("reads the replacement");
        assert_eq!(stored, second);
    }

    // -----------------------------------------------------------------------
    // Snapshots: enablement, contract capture, attachment references
    // -----------------------------------------------------------------------

    #[test]
    fn revision_snapshot_requires_an_enabled_collection_configuration() {
        let (_dir, engine) = open_engine("snapshot-disabled");
        put_user_document(&engine, "t1", "put-1", "doc-1", user_value("one"));

        // No configuration at all: the collection's effective configuration
        // is disabled (V1's empty configuration).
        assert_eq!(
            snapshot_op(&engine, "t1", "doc-1", ExpectedVersion::Any).map(|_| ()),
            Err(RevisionsError::RevisionsDisabled)
        );

        enable(&engine, "t1", CollectionRevisionsConfiguration::disabled());
        assert_eq!(
            snapshot_op(&engine, "t1", "doc-1", ExpectedVersion::Any).map(|_| ()),
            Err(RevisionsError::RevisionsDisabled),
            "an explicitly disabled configuration refuses snapshots too"
        );
    }

    #[test]
    fn revision_snapshot_records_contract_and_attachment_references() {
        let (_dir, engine) = open_engine("snapshot-record");
        enable(
            &engine,
            "t1",
            CollectionRevisionsConfiguration::try_new(Some(10), None, None, false, false)
                .expect("valid configuration"),
        );
        put_user_document(&engine, "t1", "put-1", "doc-1", user_value("one"));
        put_attachment_metadata(
            &engine,
            "t1",
            "doc-1",
            "notes",
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            12,
        );

        // The published contract of the document at snapshot time.
        let tenant_scope = scope(&engine, "t1");
        let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
        let identity = operation("head-1");
        let transaction = engine.begin(&tenant_scope, snapshot, identity).expect("begins");
        let head = transaction
            .head(&collection("users"), &document_id("doc-1"), HeadCondition::Any)
            .expect("serves the head");
        drop(transaction);
        let expected_contract = *head.contract().expect("an existing document carries a contract");

        let recorded = snapshot_op(&engine, "t1", "doc-1", ExpectedVersion::Any).expect("records");
        assert_eq!(recorded.revision(), 1);
        assert_eq!(recorded.contract(), &expected_contract);
        assert_eq!(recorded.attachment_references(), 1);

        let page = read_snapshots(&engine, "t1", "doc-1", None).expect("reads");
        assert_eq!(page.total(), 1);
        let entry = &page.entries()[0];
        assert_eq!(entry.revision(), 1);
        assert_eq!(entry.value(), &user_value("one"));
        assert_eq!(entry.contract(), Some(&expected_contract));
        assert_eq!(entry.attachments().len(), 1);
        let reference = &entry.attachments()[0];
        assert_eq!(reference.name(), "notes");
        assert_eq!(reference.content_type(), "text/plain");
        assert_eq!(reference.size(), 12);
        assert_eq!(
            reference.digest(),
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
        );

        // The W3-F revision log and the GAP-F05 side car stay in lockstep.
        assert_eq!(revision_page_total(&engine, "t1", "doc-1"), 1);

        // A snapshot write against a stale expected version is refused.
        assert_eq!(
            snapshot_op(&engine, "t1", "doc-1", ExpectedVersion::Exact(999)).map(|_| ()),
            Err(RevisionsError::Conflict)
        );
    }

    #[test]
    fn snapshot_side_car_follows_the_w3f_policy_trim() {
        let (_dir, engine) = open_engine("w3f-lockstep");
        // The collection policy keeps a wide 100 revisions, so it never
        // trims and the W3-F per-document policy (maximum 2) is the only
        // trimming agent.
        enable(
            &engine,
            "t1",
            CollectionRevisionsConfiguration::try_new(Some(100), None, None, false, false)
                .expect("valid configuration"),
        );
        put_user_document(&engine, "t1", "put-1", "doc-1", user_value("one"));
        snapshot_op(&engine, "t1", "doc-1", ExpectedVersion::Any).expect("records");
        configure_w3f_policy(
            &engine,
            "t1",
            "doc-1",
            RevisionPolicy::try_new(1, Some(2)).expect("valid policy"),
        );

        for label in ["two", "three", "four", "five", "six", "seven"] {
            put_user_document(&engine, "t1", &format!("put-{label}"), "doc-1", user_value(label));
            snapshot_op(&engine, "t1", "doc-1", ExpectedVersion::Any).expect("records");
        }

        // The W3-F policy holds the log at two entries, and the side car is
        // reaped in lockstep: one entry per surviving revision, never one
        // per historical write.
        assert_eq!(revision_page_total(&engine, "t1", "doc-1"), 2);
        assert_eq!(
            side_car_entries(&engine, "t1", "doc-1"),
            2,
            "the side car holds one entry per surviving log revision"
        );
        // Every surviving snapshot still reads with its captured contract.
        let page = read_snapshots(&engine, "t1", "doc-1", None).expect("reads");
        assert_eq!(page.total(), 2);
        assert!(page.entries().iter().all(|entry| entry.contract().is_some()));
    }

    // -----------------------------------------------------------------------
    // Revert by change vector (the GAP-D02 version contract)
    // -----------------------------------------------------------------------

    #[test]
    fn revert_to_contract_restores_value_and_attachment_references() {
        let (_dir, engine) = open_engine("revert-contract");
        enable(
            &engine,
            "t1",
            CollectionRevisionsConfiguration::try_new(Some(10), None, None, false, false)
                .expect("valid configuration"),
        );
        put_user_document(&engine, "t1", "put-1", "doc-1", user_value("one"));
        put_attachment_metadata(
            &engine,
            "t1",
            "doc-1",
            "notes",
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            12,
        );
        let first = snapshot_op(&engine, "t1", "doc-1", ExpectedVersion::Any).expect("records");

        // Mutate the document and its attachments, then snapshot again.
        put_user_document(&engine, "t1", "put-2", "doc-1", user_value("two"));
        expect_committed(&commit(
            &engine,
            "t1",
            "attachment-delete",
            |store, transaction, scope, identity| {
                let write = AttachmentWrite::try_new(
                    scope.clone(),
                    document_id("doc-1"),
                    AttachmentMutation::Delete { name: "notes".to_owned() },
                    ExpectedVersion::Any,
                    identity.clone(),
                    deadline(),
                    cancellation(),
                )
                .expect("valid attachment write");
                store
                    .stage(transaction, FamilyWriteRequest::attachment_mutation(write))
                    .expect("stages the delete");
            },
        ));
        snapshot_op(&engine, "t1", "doc-1", ExpectedVersion::Any).expect("records");

        // Revert to the first snapshot's contract.
        let tenant_scope = scope(&engine, "t1");
        let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
        let identity = operation("revert-1");
        let store = engine.families();
        let mut transaction =
            engine.begin(&tenant_scope, snapshot, identity.clone()).expect("begins");
        let write = RevertToContractWrite::try_new(
            tenant_scope.clone(),
            document_id("doc-1"),
            *first.contract(),
            ExpectedVersion::Any,
            2_000,
            identity,
            deadline(),
            cancellation(),
        )
        .expect("valid revert write");
        let outcome = store.stage_revert_to_contract(&mut transaction, write).expect("reverts");
        expect_committed(&transaction.commit(LocalDurability::Synced));

        let RevertOutcome { created_revision, restored_attachments } = outcome;
        assert_eq!(created_revision, 3, "the restore is itself the newest revision");
        assert_eq!(restored_attachments, 1, "the notes reference is back");

        // The user document's attachment metadata holds the restored
        // reference, re-put by digest exactly as V1's revert does.
        let read_scope = scope(&engine, "t1");
        let snapshot = engine.read_snapshot(&read_scope).expect("snapshot");
        let context = FamilyReadContext::new(read_scope, snapshot);
        let read = AttachmentRead::try_new(document_id("doc-1"), deadline(), cancellation())
            .expect("read");
        match engine
            .families()
            .read(context, FamilyReadRequest::attachment_metadata(read))
            .expect("reads attachment metadata")
        {
            FamilyReadResult::AttachmentMetadata(result) => {
                assert_eq!(result.entries().len(), 1);
                assert_eq!(result.entries()[0].name(), "notes");
            }
            other => panic!("expected attachment metadata, got {other:?}"),
        }

        // The restored revision is on the log with the restored references
        // and the revert provenance, and no contract of its own.
        let page = read_snapshots(&engine, "t1", "doc-1", Some(created_revision)).expect("reads");
        assert_eq!(page.entries().len(), 1);
        let entry = &page.entries()[0];
        assert_eq!(entry.value(), &user_value("one"));
        assert_eq!(entry.contract(), None);
        assert_eq!(entry.reverted_to(), Some(1));
        assert_eq!(entry.attachments().len(), 1);
        assert_eq!(entry.attachments()[0].name(), "notes");
    }

    #[test]
    fn revert_to_unknown_contract_is_a_typed_reference_error() {
        let (_dir, engine) = open_engine("revert-unknown");
        enable(
            &engine,
            "t1",
            CollectionRevisionsConfiguration::try_new(Some(10), None, None, false, false)
                .expect("valid configuration"),
        );
        put_user_document(&engine, "t1", "put-1", "doc-1", user_value("one"));
        snapshot_op(&engine, "t1", "doc-1", ExpectedVersion::Any).expect("records");

        let unknown = crate::engine::DocumentVersionContract::try_new(
            7,
            7,
            crate::replication::ReplicationClockStamp::new(0, 0),
        )
        .expect("a well-formed but unknown contract");
        let tenant_scope = scope(&engine, "t1");
        let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
        let identity = operation("revert-unknown");
        let store = engine.families();
        let mut transaction =
            engine.begin(&tenant_scope, snapshot, identity.clone()).expect("begins");
        let write = RevertToContractWrite::try_new(
            tenant_scope.clone(),
            document_id("doc-1"),
            unknown,
            ExpectedVersion::Any,
            2_000,
            identity,
            deadline(),
            cancellation(),
        )
        .expect("valid revert write");
        assert_eq!(
            store.stage_revert_to_contract(&mut transaction, write).map(|_| ()),
            Err(RevisionsError::ReferenceInvalid)
        );
        assert_eq!(revision_page_total(&engine, "t1", "doc-1"), 1, "nothing was staged");
    }

    // -----------------------------------------------------------------------
    // Policy enforcement and the revisions bin cleaner
    // -----------------------------------------------------------------------

    fn enforce(
        engine: &Engine,
        tenant_name: &str,
        document: &str,
        now_ms: i64,
    ) -> Result<PolicyEnforcementOutcome, RevisionsError> {
        let tenant_scope = scope(engine, tenant_name);
        let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
        let identity = unique_operation("gapf05-enforce");
        let store = engine.families();
        let mut transaction =
            engine.begin(&tenant_scope, snapshot, identity.clone()).expect("begins");
        let write = EnforceRevisionsPolicyWrite::try_new(
            tenant_scope.clone(),
            document_id(document),
            now_ms,
            identity,
            deadline(),
            cancellation(),
        )
        .expect("valid enforce write");
        let outcome = store.stage_enforce_revision_policy(&mut transaction, write)?;
        expect_committed(&transaction.commit(LocalDurability::Synced));
        Ok(outcome)
    }

    #[test]
    fn apply_policy_counts_every_removal_including_the_w3f_trim() {
        // A hand-built log that outgrew its W3-F maximum (the defensive
        // post-drain trim's reason to exist) exercises the accounting: the
        // collection drain removes one revision, the W3-F trim two more,
        // and every removal is reported and reaped from the side car.
        let mut log = crate::families::state::RevisionRecord {
            next: 6,
            entries: (1..=5).map(|revision| (revision, 1_000, user_value("held"))).collect(),
            policy: Some(RevisionPolicy::try_new(1, Some(2)).expect("valid policy")),
        };
        let mut side = super::SnapshotRecord {
            collection: Some(collection("users")),
            entries: (1..=5)
                .map(|revision| super::SnapshotEntry {
                    revision,
                    contract: None,
                    reverted_to: None,
                    attachments: vec![],
                })
                .collect(),
        };
        let configuration = CollectionRevisionsConfiguration::try_new(Some(4), None, None, false, false)
            .expect("valid configuration");
        let (removed, has_more) = super::apply_policy(&mut log, &mut side, &configuration, 2_000);
        assert_eq!(removed, 3, "the drain removes one and the W3-F trim two more");
        assert!(!has_more, "no cap stopped the collection policy");
        assert_eq!(log.entries.len(), 2);
        assert_eq!(side.entries.len(), 2, "the side car follows both removals");
    }

    #[test]
    fn snapshots_apply_the_collection_policy_on_every_update() {
        let (_dir, engine) = open_engine("policy-on-update");
        enable(
            &engine,
            "t1",
            CollectionRevisionsConfiguration::try_new(Some(2), None, None, false, false)
                .expect("valid configuration"),
        );
        put_user_document(&engine, "t1", "put-1", "doc-1", user_value("one"));
        snapshot_op(&engine, "t1", "doc-1", ExpectedVersion::Any).expect("records");
        put_user_document(&engine, "t1", "put-2", "doc-1", user_value("two"));
        snapshot_op(&engine, "t1", "doc-1", ExpectedVersion::Any).expect("records");
        put_user_document(&engine, "t1", "put-3", "doc-1", user_value("three"));
        let recorded = snapshot_op(&engine, "t1", "doc-1", ExpectedVersion::Any).expect("records");

        assert_eq!(recorded.removed_by_policy(), 1, "the third snapshot trims to the minimum");
        assert!(!recorded.has_more());
        assert_eq!(revision_page_total(&engine, "t1", "doc-1"), 2);
        let page = read_snapshots(&engine, "t1", "doc-1", None).expect("reads");
        assert_eq!(page.total(), 2);
        assert_eq!(page.entries()[0].value(), &user_value("two"));
    }

    #[test]
    fn snapshots_honor_the_per_update_deletion_cap() {
        let (_dir, engine) = open_engine("policy-cap");
        // First policy keeps everything, so a backlog of revisions exists.
        enable(
            &engine,
            "t1",
            CollectionRevisionsConfiguration::try_new(Some(10), None, None, false, false)
                .expect("valid configuration"),
        );
        for label in ["one", "two", "three", "four"] {
            put_user_document(&engine, "t1", &format!("put-{label}"), "doc-1", user_value(label));
            snapshot_op(&engine, "t1", "doc-1", ExpectedVersion::Any).expect("records");
        }
        assert_eq!(revision_page_total(&engine, "t1", "doc-1"), 4);

        // Retighten to a minimum of one with a cap of one deletion per
        // update: the backlog exists, but one update may remove only one.
        enable(
            &engine,
            "t1",
            CollectionRevisionsConfiguration::try_new(Some(1), None, Some(1), false, false)
                .expect("valid configuration"),
        );
        put_user_document(&engine, "t1", "put-5", "doc-1", user_value("five"));
        let recorded = snapshot_op(&engine, "t1", "doc-1", ExpectedVersion::Any).expect("records");
        assert_eq!(recorded.removed_by_policy(), 1, "the cap removes one revision, not four");
        assert_eq!(revision_page_total(&engine, "t1", "doc-1"), 4, "the backlog survives");
        assert!(recorded.has_more(), "the cap stopped before the minimum");
    }

    #[test]
    fn snapshots_honor_the_minimum_age_floor() {
        let (_dir, engine) = open_engine("policy-age");
        enable(
            &engine,
            "t1",
            CollectionRevisionsConfiguration::try_new(None, Some(10_000), None, false, false)
                .expect("valid configuration"),
        );
        put_user_document(&engine, "t1", "put-1", "doc-1", user_value("one"));
        snapshot_at(&engine, "t1", "doc-1", ExpectedVersion::Any, 1_000).expect("records");
        put_user_document(&engine, "t1", "put-2", "doc-1", user_value("two"));
        snapshot_at(&engine, "t1", "doc-1", ExpectedVersion::Any, 2_000).expect("records");
        put_user_document(&engine, "t1", "put-3", "doc-1", user_value("three"));
        let recorded =
            snapshot_at(&engine, "t1", "doc-1", ExpectedVersion::Any, 3_000).expect("records");
        assert_eq!(recorded.removed_by_policy(), 0, "nothing is old enough yet");
        put_user_document(&engine, "t1", "put-4", "doc-1", user_value("four"));
        let recorded =
            snapshot_at(&engine, "t1", "doc-1", ExpectedVersion::Any, 15_000).expect("records");
        // At t=15_000 the floor sits at t=5_000: the revisions recorded at
        // 1_000, 2_000, and 3_000 are past it; the new one is not.
        assert_eq!(recorded.removed_by_policy(), 3, "every revision past the age floor is removed");
        assert_eq!(revision_page_total(&engine, "t1", "doc-1"), 1);
    }

    #[test]
    fn enforce_policy_purges_revisions_of_deleted_documents_when_configured() {
        let (_dir, engine) = open_engine("purge-on-delete");
        enable(
            &engine,
            "t1",
            CollectionRevisionsConfiguration::try_new(Some(10), None, None, true, false)
                .expect("valid configuration"),
        );
        put_user_document(&engine, "t1", "put-1", "doc-1", user_value("one"));
        snapshot_op(&engine, "t1", "doc-1", ExpectedVersion::Any).expect("records");
        put_user_document(&engine, "t1", "put-2", "doc-1", user_value("two"));
        snapshot_op(&engine, "t1", "doc-1", ExpectedVersion::Any).expect("records");
        delete_user_document(&engine, "t1", "delete-1", "doc-1");

        let outcome = enforce(&engine, "t1", "doc-1", 3_000).expect("enforces");
        assert!(outcome.purged_record(), "purge-on-delete removes the whole log");
        assert_eq!(outcome.removed(), 2);
        assert_eq!(revision_page_total(&engine, "t1", "doc-1"), 0);
        let page = read_snapshots(&engine, "t1", "doc-1", None).expect("reads");
        assert_eq!(page.total(), 0);
    }

    #[test]
    fn enforce_policy_without_purge_keeps_the_bin_entry() {
        let (_dir, engine) = open_engine("no-purge");
        enable(
            &engine,
            "t1",
            CollectionRevisionsConfiguration::try_new(Some(10), None, None, false, false)
                .expect("valid configuration"),
        );
        put_user_document(&engine, "t1", "put-1", "doc-1", user_value("one"));
        snapshot_op(&engine, "t1", "doc-1", ExpectedVersion::Any).expect("records");
        delete_user_document(&engine, "t1", "delete-1", "doc-1");

        let outcome = enforce(&engine, "t1", "doc-1", 3_000).expect("enforces");
        assert!(!outcome.purged_record());
        assert_eq!(outcome.removed(), 0);
        assert_eq!(revision_page_total(&engine, "t1", "doc-1"), 1, "the bin entry remains");
    }

    fn bin_page(
        engine: &Engine,
        tenant_name: &str,
        before_ms: i64,
        limit: u32,
    ) -> Result<RevisionsBinPage, RevisionsError> {
        let read_scope = scope(engine, tenant_name);
        let snapshot = engine.read_snapshot(&read_scope).expect("snapshot");
        let context = FamilyReadContext::new(read_scope, snapshot);
        let read =
            RevisionsBinRead::try_new(before_ms, limit, deadline(), cancellation()).expect("read");
        engine.families().read_revisions_bin(context, read)
    }

    #[test]
    fn bin_page_lists_only_deleted_documents_past_the_age() {
        let (_dir, engine) = open_engine("bin-page");
        enable(
            &engine,
            "t1",
            CollectionRevisionsConfiguration::try_new(Some(10), None, None, false, false)
                .expect("valid configuration"),
        );
        put_user_document(&engine, "t1", "put-1", "doc-1", user_value("one"));
        snapshot_at(&engine, "t1", "doc-1", ExpectedVersion::Any, 1_000).expect("records");
        delete_user_document(&engine, "t1", "delete-1", "doc-1");

        // doc-2 is snapshotted, deleted, and re-created: a live document is
        // never a bin entry.
        put_user_document(&engine, "t1", "put-2", "doc-2", user_value("two"));
        snapshot_at(&engine, "t1", "doc-2", ExpectedVersion::Any, 2_000).expect("records");
        delete_user_document(&engine, "t1", "delete-2", "doc-2");
        put_user_document(&engine, "t1", "put-3", "doc-2", user_value("three"));
        // doc-3 stays live throughout.
        put_user_document(&engine, "t1", "put-4", "doc-3", user_value("live"));
        snapshot_at(&engine, "t1", "doc-3", ExpectedVersion::Any, 3_000).expect("records");

        let page = bin_page(&engine, "t1", 500, 10).expect("reads");
        assert_eq!(page.entries().len(), 0, "nothing is old enough at t=500");

        let page = bin_page(&engine, "t1", 2_000, 10).expect("reads");
        assert_eq!(page.entries().len(), 1, "only the deleted document's log is a bin entry");
        assert_eq!(page.entries()[0].document(), &document_id("doc-1"));
        assert_eq!(page.entries()[0].total(), 1);
        assert_eq!(page.entries()[0].newest_at(), 1_000);
        assert!(!page.has_more());

        assert_eq!(
            RevisionsBinRead::try_new(2_000, 0, deadline(), cancellation()).map(|_| ()),
            Err(RequestValidationError::InvalidRange),
            "a zero page limit is refused"
        );
        assert_eq!(
            RevisionsBinRead::try_new(
                2_000,
                u32::try_from(MAX_BIN_BATCH_DOCUMENTS + 1).expect("fits"),
                deadline(),
                cancellation()
            )
            .map(|_| ()),
            Err(RequestValidationError::InvalidRange),
            "a page limit past the bin batch bound is refused"
        );
        assert_eq!(
            RevisionsBinRead::try_new(-1, 10, deadline(), cancellation()).map(|_| ()),
            Err(RequestValidationError::InvalidFamilyPayload),
            "a negative cutoff is refused"
        );
    }

    #[test]
    fn bin_clean_refuses_duplicate_candidates() {
        let (_dir, engine) = open_engine("bin-clean-duplicates");
        // A repeated candidate would stage the same record deleted twice —
        // a transaction that can never commit — and inflate the purged
        // count; the typed constructor refuses it.
        assert_eq!(
            RevisionsBinCleanWrite::try_new(
                scope(&engine, "t1"),
                vec![document_id("doc-1"), document_id("doc-1"), document_id("doc-1")],
                5_000,
                operation("bin-clean-duplicates"),
                deadline(),
                cancellation(),
            )
            .map(|_| ()),
            Err(RequestValidationError::InvalidFamilyPayload),
            "a repeated candidate document is refused"
        );
    }

    #[test]
    fn bin_clean_purges_eligible_entries_and_refuses_live_documents_atomically() {
        let (_dir, engine) = open_engine("bin-clean");
        enable(
            &engine,
            "t1",
            CollectionRevisionsConfiguration::try_new(Some(10), None, None, false, false)
                .expect("valid configuration"),
        );
        put_user_document(&engine, "t1", "put-1", "doc-1", user_value("one"));
        snapshot_at(&engine, "t1", "doc-1", ExpectedVersion::Any, 1_000).expect("records");
        delete_user_document(&engine, "t1", "delete-1", "doc-1");
        put_user_document(&engine, "t1", "put-2", "doc-live", user_value("live"));
        snapshot_at(&engine, "t1", "doc-live", ExpectedVersion::Any, 2_000).expect("records");

        // A clean naming a live document is refused and stages nothing: the
        // transaction is discarded, so the eligible entry survives too.
        let tenant_scope = scope(&engine, "t1");
        let identity = operation("bin-clean-refused");
        let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
        {
            let store = engine.families();
            let mut transaction =
                engine.begin(&tenant_scope, snapshot, identity.clone()).expect("begins");
            let write = RevisionsBinCleanWrite::try_new(
                tenant_scope.clone(),
                vec![document_id("doc-1"), document_id("doc-live")],
                5_000,
                identity,
                deadline(),
                cancellation(),
            )
            .expect("valid bin clean write");
            assert_eq!(
                store.stage_revisions_bin_clean(&mut transaction, write).map(|_| ()),
                Err(RevisionsError::BinEntryNotEligible)
            );
        }
        assert_eq!(revision_page_total(&engine, "t1", "doc-1"), 1, "nothing was purged");

        // The eligible-only clean purges the bin entry and both its records.
        let identity = operation("bin-clean-ok");
        let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
        let store = engine.families();
        let mut transaction =
            engine.begin(&tenant_scope, snapshot, identity.clone()).expect("begins");
        let write = RevisionsBinCleanWrite::try_new(
            tenant_scope.clone(),
            vec![document_id("doc-1")],
            5_000,
            identity,
            deadline(),
            cancellation(),
        )
        .expect("valid bin clean write");
        let outcome = store.stage_revisions_bin_clean(&mut transaction, write).expect("purges");
        expect_committed(&transaction.commit(LocalDurability::Synced));
        let RevisionsBinCleanOutcome { purged } = outcome;
        assert_eq!(purged, 1);
        assert_eq!(revision_page_total(&engine, "t1", "doc-1"), 0);
        assert_eq!(revision_page_total(&engine, "t1", "doc-live"), 1, "live logs are untouched");
    }

    // -----------------------------------------------------------------------
    // Composition with the existing W3-F revision path
    // -----------------------------------------------------------------------

    #[test]
    fn per_collection_configuration_governs_only_its_collection() {
        let (_dir, engine) = open_engine("collection-scope");
        enable_for_collection(
            &engine,
            "t1",
            &collection("users"),
            CollectionRevisionsConfiguration::try_new(Some(10), None, None, false, false)
                .expect("valid configuration"),
        );
        put_user_document(&engine, "t1", "put-1", "doc-1", user_value("one"));
        snapshot_op(&engine, "t1", "doc-1", ExpectedVersion::Any).expect("records");

        // A document of another collection has no effective configuration.
        expect_committed(&commit(&engine, "t1", "put-orders", |_store, transaction, _, _| {
            transaction
                .put(
                    Document::new(document_id("order-1"), collection("orders"), user_value("one")),
                    ExpectedVersion::Any,
                )
                .expect("stages document");
        }));
        assert_eq!(
            snapshot_op(&engine, "t1", "order-1", ExpectedVersion::Any).map(|_| ()),
            Err(RevisionsError::RevisionsDisabled)
        );
    }

    #[test]
    fn exact_revision_selection_of_an_unknown_revision_is_a_reference_error() {
        let (_dir, engine) = open_engine("exact-selection");
        enable(
            &engine,
            "t1",
            CollectionRevisionsConfiguration::try_new(Some(10), None, None, false, false)
                .expect("valid configuration"),
        );
        put_user_document(&engine, "t1", "put-1", "doc-1", user_value("one"));
        snapshot_op(&engine, "t1", "doc-1", ExpectedVersion::Any).expect("records");

        assert_eq!(
            read_snapshots(&engine, "t1", "doc-1", Some(42)).map(|_| ()),
            Err(RevisionsError::ReferenceInvalid)
        );
    }

    #[test]
    fn family_error_maps_into_the_revisions_error_vocabulary() {
        assert_eq!(
            RevisionsError::from(FamilyError::DocumentMissing),
            RevisionsError::DocumentMissing
        );
        assert_eq!(
            RevisionsError::from(FamilyError::RevisionReferenceInvalid),
            RevisionsError::ReferenceInvalid
        );
    }
}
