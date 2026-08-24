//! Declaration-only typed Data Family seam.

#![allow(dead_code)]
#![allow(clippy::missing_errors_doc, clippy::must_use_candidate, clippy::unimplemented)]

use std::marker::PhantomData;

use crate::{
    engine::{Engine, Transaction},
    types::{ReadSnapshot, TenantScope},
};

macro_rules! family_value {
    ($name:ident, $description:literal) => {
        #[doc = $description]
        #[derive(Clone, Debug, Eq, PartialEq)]
        pub struct $name {
            marker: PhantomData<()>,
        }
    };
}

/// The closed set of V1-named Data Families.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
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

/// Tenant and snapshot context for a family read.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct FamilyReadContext {
    tenant: TenantScope,
    snapshot: ReadSnapshot,
}

impl FamilyReadContext {
    /// Creates a structural context without performing Engine I/O.
    pub fn new(tenant: TenantScope, snapshot: ReadSnapshot) -> Self {
        let _ = (tenant, snapshot);
        unimplemented!("family context construction belongs to a later behavior packet")
    }

    /// Creates a context after checking tenant and snapshot identity.
    pub fn try_new(tenant: TenantScope, snapshot: ReadSnapshot) -> Result<Self, FamilyError> {
        let _ = (tenant, snapshot);
        unimplemented!("family context validation belongs to a later behavior packet")
    }
}

family_value!(AttachmentRead, "A typed attachment read request.");
family_value!(AttachmentWrite, "A typed attachment mutation request.");
family_value!(AttachmentMetadataResult, "A typed attachment metadata result.");
family_value!(CounterRead, "A typed counter-group read request.");
family_value!(CounterBatchWrite, "A typed counter batch mutation request.");
family_value!(CounterGroupResult, "A typed counter-group result.");
family_value!(TimeSeriesRead, "A typed time-series range read request.");
family_value!(TimeSeriesBatchWrite, "A typed time-series batch mutation request.");
family_value!(TimeSeriesPage, "A typed time-series page result.");
family_value!(RevisionRead, "A typed revision-page read request.");
family_value!(RevisionWrite, "A typed revision command request.");
family_value!(RevisionPage, "A typed revision page result.");
family_value!(CompareExchangeRead, "A typed compare-exchange read request.");
family_value!(CompareExchangeWrite, "A typed compare-exchange condition.");
family_value!(CompareExchangeValueResult, "A typed compare-exchange result.");
family_value!(IdentityRead, "A typed identity-range read request.");
family_value!(IdentityWrite, "A typed identity allocation request.");
family_value!(IdentityRangeResult, "A typed identity-range result.");
family_value!(LifecycleRead, "A typed lifecycle read request.");
family_value!(LifecycleWrite, "A typed lifecycle update request.");
family_value!(LifecycleResult, "A typed lifecycle result.");
family_value!(ArchivalReadRequest, "A typed archival read request.");
family_value!(ArchivalWrite, "A typed archival update request.");
family_value!(ArchivalReadResult, "A typed archival result.");
family_value!(ValidationRead, "A typed validation read request.");
family_value!(ValidationWrite, "A typed validation update request.");
family_value!(ValidationResult, "A typed validation result.");
family_value!(TombstoneRead, "A typed tombstone read request.");
family_value!(TombstoneWrite, "A typed tombstone operation request.");
family_value!(TombstoneResult, "A typed tombstone result.");

/// The closed set of family read requests.
#[derive(Clone, Debug, Eq, PartialEq)]
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
    pub fn attachment_metadata(value: AttachmentRead) -> Self {
        Self::AttachmentMetadata(value)
    }

    /// Wraps a counter read.
    pub fn counter_group(value: CounterRead) -> Self {
        Self::CounterGroup(value)
    }

    /// Wraps a time-series read.
    pub fn time_series_range(value: TimeSeriesRead) -> Self {
        Self::TimeSeriesRange(value)
    }

    /// Wraps a revision read.
    pub fn revision_page(value: RevisionRead) -> Self {
        Self::RevisionPage(value)
    }

    /// Wraps a compare-exchange read.
    pub fn compare_exchange_value(value: CompareExchangeRead) -> Self {
        Self::CompareExchangeValue(value)
    }

    /// Wraps an identity read.
    pub fn identity_range(value: IdentityRead) -> Self {
        Self::IdentityRange(value)
    }

    /// Wraps a lifecycle read.
    pub fn lifecycle(value: LifecycleRead) -> Self {
        Self::Lifecycle(value)
    }

    /// Wraps an archival read.
    pub fn archival(value: ArchivalReadRequest) -> Self {
        Self::Archival(value)
    }

    /// Wraps a validation read.
    pub fn validation(value: ValidationRead) -> Self {
        Self::Validation(value)
    }

    /// Wraps a tombstone read.
    pub fn tombstones(value: TombstoneRead) -> Self {
        Self::Tombstones(value)
    }
}

/// The closed set of family write requests.
#[derive(Clone, Debug, Eq, PartialEq)]
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
    pub fn attachment_mutation(value: AttachmentWrite) -> Self {
        Self::AttachmentMutation(value)
    }

    /// Wraps a counter batch.
    pub fn counter_batch(value: CounterBatchWrite) -> Self {
        Self::CounterBatch(value)
    }

    /// Wraps a time-series batch.
    pub fn time_series_batch(value: TimeSeriesBatchWrite) -> Self {
        Self::TimeSeriesBatch(value)
    }

    /// Wraps a revision command.
    pub fn revision_command(value: RevisionWrite) -> Self {
        Self::RevisionCommand(value)
    }

    /// Wraps a compare-exchange condition.
    pub fn compare_exchange_condition(value: CompareExchangeWrite) -> Self {
        Self::CompareExchangeCondition(value)
    }

    /// Wraps an identity allocation.
    pub fn identity_allocation(value: IdentityWrite) -> Self {
        Self::IdentityAllocation(value)
    }

    /// Wraps a lifecycle update.
    pub fn lifecycle_update(value: LifecycleWrite) -> Self {
        Self::LifecycleUpdate(value)
    }

    /// Wraps an archival update.
    pub fn archival_update(value: ArchivalWrite) -> Self {
        Self::ArchivalUpdate(value)
    }

    /// Wraps a validation update.
    pub fn schema_validation_update(value: ValidationWrite) -> Self {
        Self::SchemaValidationUpdate(value)
    }

    /// Wraps a tombstone operation.
    pub fn tombstone_operation(value: TombstoneWrite) -> Self {
        Self::TombstoneOperation(value)
    }
}

/// The closed set of family read results.
#[derive(Clone, Debug, Eq, PartialEq)]
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
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum FamilyError {
    /// The Engine has closed.
    EngineClosed,
    /// The context and request use different tenants.
    TenantMismatch,
    /// The snapshot belongs to another Engine.
    SnapshotEngineMismatch,
    /// The transaction belongs to another Engine.
    TransactionEngineMismatch,
    /// The write identity does not match the transaction.
    TransactionIdentityMismatch,
    /// The family request is invalid.
    InvalidRequest,
    /// The family operation conflicted.
    Conflict,
    /// The family operation was cancelled.
    Cancelled,
}

/// A family store borrowed from one Engine.
pub struct FamilyStore<'engine> {
    engine: PhantomData<&'engine Engine>,
}

impl<'engine> FamilyStore<'engine> {
    pub(crate) fn from_engine(_: &'engine Engine) -> Self {
        Self { engine: PhantomData }
    }

    /// Reads one typed family capability from a snapshot context.
    pub fn read(
        &self,
        context: FamilyReadContext,
        request: FamilyReadRequest,
    ) -> Result<FamilyReadResult, FamilyError> {
        let _ = (self, context, request);
        unimplemented!("family behavior belongs to W3-F")
    }

    /// Stages one typed family mutation in an Engine transaction.
    pub fn stage(
        &self,
        transaction: &mut Transaction<'engine>,
        request: FamilyWriteRequest,
    ) -> Result<(), FamilyError> {
        let _ = (self, transaction, request);
        unimplemented!("family behavior belongs to W3-F")
    }
}
