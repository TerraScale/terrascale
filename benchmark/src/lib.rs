//! `TerraBase` engine.
//!
//! The workspace charter defines the native product boundary and qualification
//! rules. Accepted architecture is not an implementation or performance claim.
//!
//! The identity layer ([`identity`]) implements the ADR 0027 ULID object
//! identities. The engine's persisted-format migration to ULID keys remains
//! open under ADR 0031's activation rules, so no existing storage format
//! changed in that lane.

#![deny(unsafe_code)]
#![deny(missing_docs)]
#![warn(clippy::all, clippy::pedantic)]

/// The crate version selected by Cargo for evidence and artifact identity.
pub const VERSION: &str = env!("CARGO_PKG_VERSION");

/// The current repository status. This is metadata, not a database feature.
pub const STATUS: &str = "w3-1-storage-engine";

pub mod product_identity;

pub mod adapters;
pub mod authorization;
pub mod backup;
pub mod deletion;
pub mod encryption;
pub mod engine;
pub mod families;
pub mod identity;
pub mod index;
pub mod member;
pub mod observability;
pub mod operations;
pub mod query;
pub mod regions;
pub mod relationships;
pub mod replication;
pub mod session;
pub mod sharding;
pub mod storage;
pub mod streaming;
pub mod tiers;
pub mod types;
pub mod usage;

#[cfg(test)]
mod benchmarks;

pub use adapters::platform::{PlatformAdapter, PlatformError};
pub use adapters::protocol::{
    CallerContext, CallerContextError, CallerEvidence, CredentialInput, CredentialKind, PrincipalId,
};
pub use adapters::protocol::{ProtocolAdapter, ProtocolError};
pub use backup::{
    BackupArchive, BackupError, BackupKind, BackupManifest, BackupPlan, BackupProgress,
    BackupResult, RestorePlan, RestoreResult,
};
pub use engine::{
    BulkIngestRequest, CommitError, CommitOutcome, CommitReceipt, DurableResolution, Engine,
    EngineClosed, EngineConfig, EngineConfigError, EngineError, EngineOpenError, EngineShutdownError,
    IngestBatchReceipt, IngestConflictMode, IngestReceipt, IngestRefusal, IngestRefusalReason,
    IngestStream, IngestTermination, MAX_INGEST_BATCH_DOCUMENTS, MAX_INGEST_REFUSAL_DETAILS,
    Transaction, UnknownCommit, UnknownReason,
};
pub use families::{
    FamilyError, FamilyKind, FamilyReadContext, FamilyReadRequest, FamilyReadResult, FamilyStore,
    FamilyWriteRequest,
};
pub use index::operations::{
    INDEX_ERROR_STATE_THRESHOLD, IndexCreationOutcome, IndexDeployment, IndexErrorAction,
    IndexErrorCategory, IndexErrorListing, IndexErrorRecord, IndexLockMode, IndexOperationError,
    IndexOperationalState, IndexPriority, IndexResetMode, IndexStatistics, IndexStore,
    LifecycleTransition, MAX_INDEX_ERRORS_PER_INDEX, NonStaleWaitOutcome, RebuildStepOutcome,
    StalenessReason, TenantIndexStatistics, WaitForNonStale,
};
pub use operations::{
    DocumentReadRequest, DocumentWriteRequest, FamilyOperation, MAX_QUERY_TEXT_BYTES,
    PublicOperation, PublicResponse, QueryInput, QueryInputError, QueryOperation,
    RequestValidationError, TqlQueryText,
};
pub use query::{
    BulkAuditEntry, BulkAuditStage, BulkByQueryOptions, BulkByQueryReceipt, BulkDetail,
    BulkDetailOutcome, BulkOperationKind, BulkPageReceipt, BulkRefusal, BulkTermination,
    CounterInclude, DeleteByQueryRequest, DeletionConfirmation, DocumentInclude, IncludeClause,
    IncludePath, IncludeSet, IncludedCounters, IncludedTimeSeries, MAX_BULK_AUDIT_ENTRIES,
    MAX_BULK_DETAILS, MAX_BULK_PAGE_DOCUMENTS, PatchByQueryRequest, QueryError, QueryOrdering,
    QueryPaging, QueryPlan, QueryPredicate, QueryProjection, QueryResult, QuerySource, QuerySpec,
    QueryStore, StalenessPolicy, TimeSeriesInclude,
};
pub use regions::{
    REGION_CEILING, RegionAdmissionError, RegionCatalog, RegionCode, RegionCodeError,
};
pub use replication::{
    AcknowledgementLevel, AcknowledgementReport, ConflictRecord, Convergence, MemberId, RegionId,
    ReplicationConfig, ReplicationCursor, ReplicationError, ReplicationOutcome, ReplicationReceipt,
    ReplicationStatus, TopologyView,
};
pub use session::{
    AfterSaveEvent, BeforeDeleteEvent, BeforeDeleteHook, BeforeStoreEvent, BeforeStoreHook,
    DocumentChanges, FieldChange, FieldChangeKind, Session, SessionCommand, SessionError,
    SessionHookVeto, SessionOptions, SessionStream, SessionTracking, SessionTransactionMode,
};
pub use storage::{
    CompactionError, CompactionPhase, CompactionPolicy, CompactionProgress, CompactionProgressSink,
    CompactionReceipt, CompactionRequest, HistoryReclaimOutcome,
};
pub use streaming::{
    ChangeEvent, ChangeFilter, ChangeKind, DocumentStreamSelector, StreamBatch, StreamCompletion,
    StreamCursor, StreamError, StreamItem, StreamPosition, StreamSource, StreamStore,
    SubscriptionAcknowledgement, SubscriptionBatch, SubscriptionCheckpoint, SubscriptionSpec,
    SubscriptionStatus,
};
pub use types::{
    Cancellation, Deadline, Document, DocumentId, DocumentMetadata, DocumentValue, ExpectedVersion,
    IndexGeneration, IndexGenerationSet, LocalDurability, OperationId, ReadSnapshot,
    RetryDisposition, StorageSequence, TenantId, TenantScope,
};
pub use usage::{
    ChargeScope, CorrectionProcedure, MeasurementCadence, MeasurementWindow, MeterCatalog,
    MeterDefinition, MeterKind, MeterUnit, MintingIdentity, MintingRule, SegmentId,
    SegmentInsertOutcome, SequenceInterval, USAGE_FORMAT_VERSION, UsageError, UsageFact,
    UsageFactId, UsageScope, UsageScopeId, UsageSegment, UsageSegmentLedger,
};

#[cfg(test)]
mod tests {
    use super::{STATUS, VERSION};

    #[test]
    fn foundation_metadata_is_present() {
        assert_eq!(STATUS, "w3-1-storage-engine");
        assert!(!VERSION.is_empty());
    }
}
