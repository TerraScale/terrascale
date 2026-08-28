//! `TerraBaseV2` engine.
//!
//! The public seams, parity obligations, and evidence rules live in the
//! workspace charter and parity inventory. Behavior lands behind those seams
//! packet by packet: the storage/WAL/recovery and document/transaction
//! engine seam is implemented and focused-tested; later seams remain
//! declaration-only until their packets prove them.

#![deny(unsafe_code)]
#![deny(missing_docs)]
#![warn(clippy::all, clippy::pedantic)]

/// The crate version selected by Cargo for evidence and artifact identity.
pub const VERSION: &str = env!("CARGO_PKG_VERSION");

/// The current repository status. This is metadata, not a database feature.
pub const STATUS: &str = "w3-1-storage-engine";

pub mod adapters;
pub mod backup;
pub mod engine;
pub mod families;
pub mod index;
pub mod migration;
pub mod operations;
pub mod query;
pub mod replication;
pub mod session;
pub mod sharding;
pub mod storage;
pub mod streaming;
pub mod types;

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
    EngineClosed, EngineConfig, EngineError, EngineOpenError, EngineShutdownError,
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
pub use migration::{
    MigrationError, MigrationLossMatrix, MigrationManifest, MigrationOptions, MigrationRecord,
    MigrationReport,
};
pub use operations::{
    DocumentReadRequest, DocumentWriteRequest, FamilyOperation, MAX_QUERY_TEXT_BYTES,
    PublicOperation, PublicResponse, QueryInput, QueryInputError, QueryOperation,
    RequestValidationError, V1QueryText,
};
pub use query::{
    BulkAuditEntry, BulkAuditStage, BulkByQueryOptions, BulkByQueryReceipt, BulkDetail,
    BulkDetailOutcome, BulkOperationKind, BulkPageReceipt, BulkRefusal, BulkTermination,
    CounterInclude, DeleteByQueryRequest, DeletionConfirmation, DocumentInclude, IncludeClause,
    IncludePath, IncludeSet, IncludedCounters, IncludedTimeSeries, MAX_BULK_DETAILS,
    MAX_BULK_PAGE_DOCUMENTS, PatchByQueryRequest, QueryError, QueryOrdering, QueryPaging,
    QueryPlan, QueryPredicate, QueryProjection, QueryResult, QuerySource, QuerySpec, QueryStore,
    StalenessPolicy, TimeSeriesInclude,
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

#[cfg(test)]
mod tests {
    use super::{STATUS, VERSION};

    #[test]
    fn foundation_metadata_is_present() {
        assert_eq!(STATUS, "w3-1-storage-engine");
        assert!(!VERSION.is_empty());
    }
}
