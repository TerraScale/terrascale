//! In-process local-v1 document caller.
//!
//! This is deliberately transport-neutral. It opens and retains the one
//! `DocumentStore` owner for its shard; it does not share a raw `Storage`
//! handle, accept request-supplied tenant/database/shard identity, provision
//! schemas, or expose the signed distributed store as a local service.

use std::collections::BTreeSet;
use std::sync::Arc;
use std::time::Instant;

use tb_core::{
    CollectionId, CommitSeq, CoreError, DatabaseId, DocumentId, MemoryBudget, ShardId, TenantId,
    TransactionId,
};
use tb_document::{
    DistributedDocumentCommit, DocumentCommit, DocumentError, DocumentPoint, DocumentRead,
    DocumentStore, DocumentStoreConfig, DocumentWrite, LocalSchemaBinding,
};
use tb_format::DocumentEventId;
use tb_format::FormatError;
use tb_replication::MAX_EVENT_CHANGES;
use tb_storage::{LocalDocumentMode, StorageError};

use crate::distributed_runtime::DocumentMemberRuntime;

/// Fixed-category upper bound for the trusted collection set installed with a
/// local-v1 service. A request cannot expand this set.
const MAX_SCOPE_COLLECTIONS: usize = MAX_EVENT_CHANGES;

/// A server-configured scope for exactly one local-v1 document shard.
///
/// The constructor is crate-private and is intended for trusted server
/// configuration. Client adapters must not construct this value from unchecked
/// request claims; no client adapter exists in this slice.
#[derive(Clone)]
pub(crate) struct LocalV1Scope {
    tenant: TenantId,
    database: DatabaseId,
    shard: ShardId,
    collections: BTreeSet<CollectionId>,
}

impl LocalV1Scope {
    /// Configure the fixed scope before the document directory is opened.
    ///
    /// # Errors
    /// Rejects an empty or duplicate collection set and more than the bounded
    /// v1 event-change ceiling.
    pub(crate) fn configured(
        tenant: TenantId,
        database: DatabaseId,
        shard: ShardId,
        collections: impl IntoIterator<Item = CollectionId>,
    ) -> Result<Self, DocumentServiceError> {
        let mut permitted = BTreeSet::new();
        for collection in collections {
            if permitted.contains(&collection) {
                return Err(DocumentServiceError::DuplicateCollectionGrant);
            }
            if permitted.len() == MAX_SCOPE_COLLECTIONS {
                return Err(DocumentServiceError::TooManyCollectionGrants);
            }
            permitted.insert(collection);
        }
        if permitted.is_empty() {
            return Err(DocumentServiceError::EmptyCollectionScope);
        }

        Ok(Self {
            tenant,
            database,
            shard,
            collections: permitted,
        })
    }

    fn validate_store_config(
        &self,
        config: &DocumentStoreConfig,
    ) -> Result<(), DocumentServiceError> {
        if self.tenant != config.storage.tenant
            || self.database != config.database
            || self.shard != config.shard
        {
            return Err(DocumentServiceError::ScopeBindingMismatch);
        }
        Ok(())
    }

    fn require_collection(&self, collection: CollectionId) -> Result<(), DocumentServiceError> {
        if self.collections.contains(&collection) {
            Ok(())
        } else {
            Err(DocumentServiceError::CollectionOutOfScope)
        }
    }

    /// Whether a validated client grant stays within this exact shard scope.
    ///
    /// The auth registry uses this same immutable scope at initial load and
    /// reload, so a client allowlist cannot drift from the service's authority.
    pub(crate) fn allows_grant(
        &self,
        tenant: TenantId,
        database: DatabaseId,
        shard: ShardId,
        collections: &[CollectionId],
    ) -> bool {
        self.tenant == tenant
            && self.database == database
            && self.shard == shard
            && !collections.is_empty()
            && collections
                .iter()
                .all(|collection| self.collections.contains(collection))
    }

    fn require_writes(&self, writes: &[DocumentWrite<'_>]) -> Result<(), DocumentServiceError> {
        for write in writes {
            self.require_collection(write.binding.collection)?;
        }
        Ok(())
    }
}

/// Borrow the signed active set only; historical catalogs never become grants.
pub(crate) fn signed_active_scope(
    document: &DocumentMemberRuntime,
) -> Result<LocalV1Scope, DocumentServiceError> {
    let view = document.authority.view();
    let active = view.catalogs().filter_map(|(binding, _)| {
        view.checked_active_catalog(binding.collection)
            .ok()
            .filter(|(current, _)| *current == binding)
            .map(|_| binding.collection)
    });
    LocalV1Scope::configured(view.tenant(), view.database(), view.shard(), active)
}

/// The HTTPS backend shares the sole recovered distributed document owner.
pub(crate) struct DistributedDocumentService {
    scope: LocalV1Scope,
    document: Arc<DocumentMemberRuntime>,
}

impl DistributedDocumentService {
    pub(crate) fn new(scope: LocalV1Scope, document: Arc<DocumentMemberRuntime>) -> Self {
        Self { scope, document }
    }

    pub(crate) fn scope(&self) -> &LocalV1Scope {
        &self.scope
    }

    pub(crate) fn point_read(
        &self,
        collection: CollectionId,
        document: DocumentId,
    ) -> Result<DocumentRead, DocumentServiceError> {
        self.scope.require_collection(collection)?;
        self.document
            .authority
            .view()
            .checked_active_catalog(collection)
            .map_err(|_| DocumentServiceError::CollectionOutOfScope)?;
        self.document
            .store
            .point_read(collection, document)
            .map_err(DocumentServiceError::from)
    }

    pub(crate) fn commit_documents(
        &self,
        transaction_id: TransactionId,
        writes: &[DocumentWrite<'_>],
    ) -> Result<LocalDurableCommit, DocumentServiceError> {
        self.scope.require_writes(writes)?;
        let view = self.document.authority.view();
        for write in writes {
            let (active, _) = view
                .checked_active_catalog(write.binding.collection)
                .map_err(|_| DocumentServiceError::CollectionOutOfScope)?;
            if active.generation != write.binding.generation
                || active.schema_digest != write.binding.schema_digest
            {
                return Err(DocumentServiceError::SchemaChanged);
            }
        }
        self.document
            .store
            .commit_documents(transaction_id, writes)
            .map(LocalDurableCommit::from)
            .map_err(DocumentServiceError::from_commit)
    }
}

/// A synchronous service owner for one local-v1 document shard.
///
/// Construct one service on a bounded blocking worker and retain it for the
/// service lifetime. Its exclusive `DocumentStore` owns the shard directory;
/// a second raw, local-v1, or signed-distributed owner for that directory will
/// be rejected by storage's exclusive lock. These synchronous calls contain no
/// await point or queued work. A caller that drops a task after starting a
/// commit must retry the same transaction ID; cancellation does not roll back
/// an admitted durable commit.
pub(crate) struct LocalV1DocumentService {
    scope: LocalV1Scope,
    store: DocumentStore,
}

impl LocalV1DocumentService {
    /// Open the one local-v1 owner after validating the fixed scope.
    ///
    /// `DocumentStoreConfig` carries local event identity and bounded schema,
    /// event, storage, and memory limits. Its epoch/configuration are local
    /// event metadata only; this service has no certified remote authority.
    /// Schema installation remains a separately authorized operator action.
    ///
    /// # Errors
    /// Rejects a scope/configuration mismatch or any document-store recovery
    /// failure before exposing the service to a caller.
    pub(crate) fn open(
        config: DocumentStoreConfig,
        budget: MemoryBudget,
        scope: LocalV1Scope,
        mode: LocalDocumentMode,
        expected: &[LocalSchemaBinding],
    ) -> Result<Self, DocumentServiceError> {
        scope.validate_store_config(&config)?;
        let store = DocumentStore::open_local_v1(config, budget, mode, expected)?;
        Ok(Self { scope, store })
    }

    pub(crate) fn scope(&self) -> &LocalV1Scope {
        &self.scope
    }

    /// Read one document at one newly captured visibility cut.
    ///
    /// The returned `DocumentRead` retains the document engine's output-memory
    /// permit until the caller finishes using or serializing the typed point.
    /// It preserves `Absent`, `Deleted`, `Value`, and every canonical sibling
    /// in `Conflict`; this service never selects a winner.
    ///
    /// # Errors
    /// Rejects collections outside the configured scope and propagates bounded
    /// document-store admission, validation, and recovery errors.
    pub(crate) fn point_read(
        &self,
        collection: CollectionId,
        document: DocumentId,
    ) -> Result<DocumentRead, DocumentServiceError> {
        let _in_flight = InFlightOperation::begin("point_read");
        let started = Instant::now();
        let result = self.scope.require_collection(collection).and_then(|()| {
            let snapshot = self.store.snapshot();
            self.store
                .read(snapshot, collection, document)
                .map_err(DocumentServiceError::from)
        });
        let output_payload_bytes = result
            .as_ref()
            .map_or(0, |read| point_payload_bytes(read.point()));
        let outcome = result
            .as_ref()
            .map_or_else(DocumentServiceError::metric_class, |_| "success");
        record_operation("point_read", started, outcome, output_payload_bytes);
        if let Ok(read) = &result
            && matches!(read.point(), DocumentPoint::Conflict { .. })
        {
            metrics::counter!("terrabase_document_service_conflicts_total").increment(1);
        }
        result
    }

    /// Commit one sorted, bounded group to this shard and return only its local
    /// durable result.
    ///
    /// The store validates immutable collection/schema bindings, causal
    /// contexts, sorted unique documents, request bytes/items, and exact token
    /// retries before or during its atomic WAL commit. The service never
    /// reports a received/applied or remotely durable acknowledgment.
    ///
    /// # Errors
    /// Rejects any collection outside this fixed scope and propagates typed
    /// document-store schema, retry, capacity, and uncertain-recovery errors.
    pub(crate) fn commit_documents(
        &mut self,
        transaction_id: TransactionId,
        writes: &[DocumentWrite<'_>],
    ) -> Result<LocalDurableCommit, DocumentServiceError> {
        let _in_flight = InFlightOperation::begin("atomic_commit");
        let started = Instant::now();
        let mut encoded_wal_bytes = 0;
        let mut encoded_metadata_bytes = 0;
        let result = self.scope.require_writes(writes).and_then(|()| {
            self.store
                .commit_documents(transaction_id, writes)
                .map(|commit| {
                    encoded_wal_bytes = commit.wal_frame().len();
                    encoded_metadata_bytes = commit.metadata().len();
                    LocalDurableCommit::from(commit)
                })
                .map_err(DocumentServiceError::from_commit)
        });
        let input_payload_bytes = writes_payload_bytes(writes);
        let outcome = result
            .as_ref()
            .map_or_else(DocumentServiceError::metric_class, |_| "success");
        record_operation("atomic_commit", started, outcome, input_payload_bytes);
        if encoded_wal_bytes > 0 {
            metrics::counter!(
                "terrabase_document_service_encoded_bytes_total",
                "encoding" => "wal_frame",
            )
            .increment(u64::try_from(encoded_wal_bytes).unwrap_or(u64::MAX));
            metrics::counter!(
                "terrabase_document_service_encoded_bytes_total",
                "encoding" => "event_metadata",
            )
            .increment(u64::try_from(encoded_metadata_bytes).unwrap_or(u64::MAX));
            metrics::counter!(
                "terrabase_document_service_commit_stage_total",
                "stage" => "local_durable",
            )
            .increment(1);
        }
        result
    }
}

/// Result of a local-v1 commit or exact retry. Its type makes the durability
/// claim local; it contains no member-received or applied acknowledgment.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) struct LocalDurableCommit {
    transaction_id: TransactionId,
    commit_seq: CommitSeq,
    event: DocumentEventId,
}

impl LocalDurableCommit {
    /// Stable caller-supplied identity used for retry reconciliation.
    #[must_use]
    pub(crate) const fn transaction_id(self) -> TransactionId {
        self.transaction_id
    }

    /// Sequence assigned to this locally durable transaction.
    #[must_use]
    #[cfg(test)]
    pub(crate) const fn commit_seq(self) -> CommitSeq {
        self.commit_seq
    }

    /// Local durable frontier reached by this successful commit.
    ///
    /// This local-only service cannot report remote received or applied
    /// frontiers. A successful `DocumentStore::commit_documents` call crosses
    /// the local durable barrier, so its commit sequence is the local durable
    /// frontier for this result.
    #[must_use]
    pub(crate) const fn local_durable_frontier(self) -> CommitSeq {
        self.commit_seq
    }

    /// Local event identity assigned by the one local document owner.
    #[must_use]
    pub(crate) const fn event(self) -> DocumentEventId {
        self.event
    }
}

impl From<DocumentCommit> for LocalDurableCommit {
    fn from(commit: DocumentCommit) -> Self {
        Self {
            transaction_id: commit.receipt.transaction_id,
            commit_seq: commit.receipt.commit_seq,
            event: commit.event,
        }
    }
}

impl From<DistributedDocumentCommit> for LocalDurableCommit {
    fn from(commit: DistributedDocumentCommit) -> Self {
        Self {
            transaction_id: commit.receipt.transaction_id,
            commit_seq: commit.receipt.commit_seq,
            event: commit.event,
        }
    }
}

/// Typed local service boundary failures.
#[derive(Debug, thiserror::Error)]
pub(crate) enum DocumentServiceError {
    /// The configured server-side scope omitted every collection.
    #[error("local-v1 scope must allow at least one collection")]
    EmptyCollectionScope,
    /// More collection grants were supplied than the bounded v1 ceiling.
    #[error("local-v1 scope exceeds the collection grant limit")]
    TooManyCollectionGrants,
    /// The same collection was supplied more than once in the scope.
    #[error("local-v1 scope contains a duplicate collection grant")]
    DuplicateCollectionGrant,
    /// The fixed scope does not identify the document shard being opened.
    #[error("local-v1 scope identity does not match the configured document shard")]
    ScopeBindingMismatch,
    /// The requested collection is outside the fixed server-side scope.
    #[error("collection is outside the configured local-v1 scope")]
    CollectionOutOfScope,
    /// An immutable collection schema binding changed after the caller's read.
    #[error("active collection schema changed")]
    SchemaChanged,
    /// The producer rejected a malformed write group or causal token before WAL.
    #[error("document request failed local-v1 validation")]
    Validation,
    /// The operation or its encoded output exceeded a configured resource cap.
    #[error("document operation exceeds configured capacity")]
    Capacity,
    /// The caller reused a transaction ID with different canonical mutations.
    #[error("transaction ID conflicts with a prior commit")]
    RetryConflict,
    /// The transaction ID is outside the durable retry window.
    #[error("transaction ID is outside the durable retry window")]
    RetryExpired,
    /// The caller must reconcile the same transaction after uncertain commit state.
    #[error("document commit requires recovery or retry reconciliation")]
    UncertainCommit(#[source] Box<DocumentError>),
    /// The persisted document shard failed an integrity or recovery check.
    #[error("document shard integrity check failed")]
    Corrupt,
    /// The local document owner rejected the operation.
    #[error("document operation failed")]
    Document(#[source] DocumentError),
}

impl DocumentServiceError {
    fn from_commit(error: DocumentError) -> Self {
        match error {
            DocumentError::SchemaChanged => Self::SchemaChanged,
            DocumentError::InvalidWriteOrder
            | DocumentError::InvalidCausalContext(_)
            | DocumentError::InvalidDocumentFields(_)
            | DocumentError::Core(CoreError::InvalidTransactionExpiry)
            | DocumentError::Storage(StorageError::Core(CoreError::InvalidTransactionExpiry)) => {
                Self::Validation
            }
            DocumentError::Format(FormatError::Allocation)
            | DocumentError::Capacity(_)
            | DocumentError::Admission(_) => Self::Capacity,
            DocumentError::Format(_) => Self::Corrupt,
            DocumentError::NeedsRecovery => {
                Self::UncertainCommit(Box::new(DocumentError::NeedsRecovery))
            }
            DocumentError::CommitUncertain(source) => Self::UncertainCommit(source),
            DocumentError::Core(error @ CoreError::Durability { .. }) => {
                Self::UncertainCommit(Box::new(DocumentError::Core(error)))
            }
            DocumentError::Storage(
                error @ (StorageError::Io(_)
                | StorageError::Poisoned
                | StorageError::Core(CoreError::Durability { .. })),
            ) => Self::UncertainCommit(Box::new(DocumentError::Storage(error))),
            other => Self::from_document(other),
        }
    }

    fn from_document(error: DocumentError) -> Self {
        match error {
            DocumentError::SchemaChanged => Self::SchemaChanged,
            DocumentError::InvalidWriteOrder
            | DocumentError::InvalidCausalContext(_)
            | DocumentError::InvalidDocumentFields(_)
            | DocumentError::Core(CoreError::InvalidTransactionExpiry)
            | DocumentError::Storage(StorageError::Core(CoreError::InvalidTransactionExpiry)) => {
                Self::Validation
            }
            DocumentError::Format(FormatError::Allocation)
            | DocumentError::Capacity(_)
            | DocumentError::Admission(_)
            | DocumentError::Core(CoreError::CapacityExceeded { .. })
            | DocumentError::Storage(StorageError::Capacity(_) | StorageError::Admission(_)) => {
                Self::Capacity
            }
            DocumentError::NeedsRecovery => {
                Self::UncertainCommit(Box::new(DocumentError::NeedsRecovery))
            }
            DocumentError::CommitUncertain(source) => Self::UncertainCommit(source),
            DocumentError::Corrupt(_)
            | DocumentError::Format(_)
            | DocumentError::Storage(StorageError::Corrupt(_)) => Self::Corrupt,
            DocumentError::Core(CoreError::IdempotencyConflict)
            | DocumentError::Storage(
                StorageError::IdempotencyConflict
                | StorageError::Core(CoreError::IdempotencyConflict),
            ) => Self::RetryConflict,
            DocumentError::Core(CoreError::IdempotencyExpired)
            | DocumentError::Storage(
                StorageError::IdempotencyExpired
                | StorageError::Core(CoreError::IdempotencyExpired),
            ) => Self::RetryExpired,
            other => Self::Document(other),
        }
    }

    const fn metric_class(&self) -> &'static str {
        match self {
            Self::EmptyCollectionScope
            | Self::TooManyCollectionGrants
            | Self::DuplicateCollectionGrant
            | Self::ScopeBindingMismatch
            | Self::CollectionOutOfScope => "scope_rejected",
            Self::SchemaChanged => "schema_conflict",
            Self::Validation => "validation_rejected",
            Self::Capacity => "capacity_rejected",
            Self::RetryConflict | Self::RetryExpired => "retry_rejected",
            Self::UncertainCommit(_) => "uncertain_commit",
            Self::Corrupt => "corrupt_state",
            Self::Document(_) => "document_rejected",
        }
    }
}

impl From<DocumentError> for DocumentServiceError {
    fn from(error: DocumentError) -> Self {
        Self::from_document(error)
    }
}

fn point_payload_bytes(point: &DocumentPoint) -> usize {
    fn sibling_bytes(sibling: &tb_document::OwnedSibling) -> usize {
        sibling.document_bytes.len()
    }

    match point {
        DocumentPoint::Absent => 0,
        DocumentPoint::Deleted { sibling, .. } | DocumentPoint::Value { sibling, .. } => {
            sibling_bytes(sibling)
        }
        DocumentPoint::Conflict { siblings, .. } => siblings.iter().map(sibling_bytes).sum(),
    }
}

fn writes_payload_bytes(writes: &[DocumentWrite<'_>]) -> usize {
    writes.iter().fold(0_usize, |total, write| {
        let bytes = match write.value {
            tb_document::DocumentWriteValue::Live(fields) => fields
                .iter()
                .fold(0_usize, |sum, field| sum.saturating_add(field.value.len())),
            tb_document::DocumentWriteValue::Tombstone => 0,
        };
        total.saturating_add(bytes)
    })
}

fn record_operation(
    operation: &'static str,
    started: Instant,
    outcome: &'static str,
    payload_bytes: usize,
) {
    metrics::counter!(
        "terrabase_document_service_operations_total",
        "operation" => operation,
        "outcome" => outcome,
    )
    .increment(1);
    metrics::histogram!(
        "terrabase_document_service_operation_seconds",
        "operation" => operation,
    )
    .record(started.elapsed().as_secs_f64());
    metrics::counter!(
        "terrabase_document_service_payload_bytes_total",
        "operation" => operation,
        "outcome" => outcome,
    )
    .increment(u64::try_from(payload_bytes).unwrap_or(u64::MAX));
}

struct InFlightOperation(&'static str);

impl InFlightOperation {
    fn begin(operation: &'static str) -> Self {
        metrics::gauge!("terrabase_document_service_in_flight", "operation" => operation)
            .increment(1.0);
        Self(operation)
    }
}

impl Drop for InFlightOperation {
    fn drop(&mut self) {
        metrics::gauge!("terrabase_document_service_in_flight", "operation" => self.0)
            .decrement(1.0);
    }
}

#[cfg(test)]
mod tests {
    use std::fs;
    use std::path::{Path, PathBuf};
    use std::sync::Arc;
    use std::sync::atomic::{AtomicU64, Ordering};
    use std::time::{SystemTime, UNIX_EPOCH};

    use tb_core::{BudgetConfig, DocumentId, MemoryBudget, OriginId, ReplicaId, WorkerId};
    use tb_document::{
        DocumentPoint, DocumentStore, DocumentStoreConfig, DocumentWriteValue, LocalSchemaBinding,
    };
    use tb_format::{DocumentBinding, DocumentKind, encode_catalog_into, encode_schema_into};
    use tb_replication::{CausalContext, ConfigurationId, ReplicationLimits};
    use tb_storage::{LocalDocumentMode, Storage, StorageConfig, StorageError};

    use super::*;

    static NEXT_TEMP_DIRECTORY: AtomicU64 = AtomicU64::new(0);

    struct TempShard(PathBuf);

    impl TempShard {
        fn new() -> Self {
            let nanos = SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .expect("system clock is after the Unix epoch")
                .as_nanos();
            let ordinal = NEXT_TEMP_DIRECTORY.fetch_add(1, Ordering::Relaxed);
            Self(std::env::temp_dir().join(format!(
                "terrabase-server-document-{}-{nanos}-{ordinal}",
                std::process::id(),
            )))
        }

        fn path(&self) -> &Path {
            &self.0
        }
    }

    impl Drop for TempShard {
        fn drop(&mut self) {
            if self.0.exists() {
                fs::remove_dir_all(&self.0).expect("remove temporary document shard");
            }
        }
    }

    struct ServiceFixture {
        _directory: TempShard,
        config: DocumentStoreConfig,
        memory: MemoryBudget,
        collections: [CollectionId; 2],
        digests: [[u8; 32]; 2],
        schemas: [LocalSchemaBinding; 2],
    }

    impl ServiceFixture {
        fn new() -> Self {
            let directory = TempShard::new();
            let config = DocumentStoreConfig {
                storage: StorageConfig::local(
                    directory.path(),
                    TenantId::from_bytes([40; 16]),
                    WorkerId(9),
                )
                .with_clock(Arc::new(|| 1_000)),
                database: DatabaseId::from_bytes([41; 16]),
                shard: ShardId::new(12),
                origin: OriginId {
                    replica_id: ReplicaId::from_bytes([42; 16]),
                    incarnation: [43; 16],
                },
                epoch: tb_core::MembershipEpoch::new(1),
                configuration: ConfigurationId::from_bytes([44; 32])
                    .expect("nonzero test configuration"),
                replication: ReplicationLimits {
                    max_origins: 16,
                    max_changes: 32,
                    max_siblings: 16,
                    max_value_bytes: tb_core::MAX_VALUE_BYTES,
                    max_payload_bytes: tb_core::MAX_WAL_GROUP_BYTES,
                },
            };
            let memory = MemoryBudget::new(
                BudgetConfig::new(64 * 1024 * 1024, 0).expect("valid test budget"),
            );
            let collections = [collection(1), collection(2)];
            let mode = local_mode(&config);
            let mut store = DocumentStore::bootstrap_local_v1(config.clone(), memory.clone(), mode)
                .expect("explicitly bootstrap test shard");
            let digests = [
                provision_empty_schema(&mut store, 1, collections[0]),
                provision_empty_schema(&mut store, 2, collections[1]),
            ];
            drop(store);
            let schemas = [
                LocalSchemaBinding {
                    collection: collections[0],
                    generation: 1,
                    schema_digest: digests[0],
                },
                LocalSchemaBinding {
                    collection: collections[1],
                    generation: 1,
                    schema_digest: digests[1],
                },
            ];

            Self {
                _directory: directory,
                config,
                memory,
                collections,
                digests,
                schemas,
            }
        }

        fn mode(&self) -> LocalDocumentMode {
            local_mode(&self.config)
        }

        fn scope(&self) -> LocalV1Scope {
            LocalV1Scope::configured(
                self.config.storage.tenant,
                self.config.database,
                self.config.shard,
                self.collections,
            )
            .expect("valid configured service scope")
        }

        fn open(&self) -> LocalV1DocumentService {
            LocalV1DocumentService::open(
                self.config.clone(),
                self.memory.clone(),
                self.scope(),
                self.mode(),
                &self.schemas,
            )
            .expect("open local-v1 service")
        }
    }

    fn local_mode(config: &DocumentStoreConfig) -> LocalDocumentMode {
        LocalDocumentMode::new(
            config.storage.tenant,
            config.database,
            config.shard,
            config.origin,
            config.epoch,
            config.configuration.to_bytes(),
        )
    }

    fn collection(byte: u8) -> CollectionId {
        CollectionId::from_bytes([byte; 16])
    }

    fn scope(collections: impl IntoIterator<Item = CollectionId>) -> LocalV1Scope {
        LocalV1Scope::configured(
            TenantId::from_bytes([1; 16]),
            DatabaseId::from_bytes([2; 16]),
            ShardId::new(3),
            collections,
        )
        .expect("valid fixed scope")
    }

    fn provision_empty_schema(
        store: &mut DocumentStore,
        nonce: u8,
        collection: CollectionId,
    ) -> [u8; 32] {
        let mut schema = Vec::new();
        encode_schema_into(&mut schema, &[]).expect("encode empty schema");
        let digest = encode_catalog_into(&mut Vec::new(), collection, 1, 0, &schema)
            .expect("encode catalog fixture");
        assert!(
            store
                .provision_schema(transaction(nonce), collection, 1, 0, &schema)
                .expect("provision test schema")
                .is_some()
        );
        digest
    }

    fn transaction(nonce: u8) -> TransactionId {
        TransactionId::new(50_000, [nonce; 16])
    }

    fn empty_live_write<'a>(
        fixture: &ServiceFixture,
        collection_index: usize,
        document: DocumentId,
        observed: &'a CausalContext,
    ) -> DocumentWrite<'a> {
        DocumentWrite {
            binding: DocumentBinding {
                collection: fixture.collections[collection_index],
                document,
                generation: 1,
                schema_digest: fixture.digests[collection_index],
            },
            observed,
            value: DocumentWriteValue::Live(&[]),
        }
    }

    #[test]
    fn local_scope_is_nonempty_unique_and_bounded() {
        assert!(matches!(
            LocalV1Scope::configured(
                TenantId::from_bytes([1; 16]),
                DatabaseId::from_bytes([2; 16]),
                ShardId::new(3),
                [],
            ),
            Err(DocumentServiceError::EmptyCollectionScope)
        ));
        assert!(matches!(
            LocalV1Scope::configured(
                TenantId::from_bytes([1; 16]),
                DatabaseId::from_bytes([2; 16]),
                ShardId::new(3),
                [collection(4), collection(4)],
            ),
            Err(DocumentServiceError::DuplicateCollectionGrant)
        ));
        assert!(matches!(
            LocalV1Scope::configured(
                TenantId::from_bytes([1; 16]),
                DatabaseId::from_bytes([2; 16]),
                ShardId::new(3),
                (0..=MAX_SCOPE_COLLECTIONS).map(|index| {
                    CollectionId::from_bytes(
                        u128::try_from(index).expect("small index").to_be_bytes(),
                    )
                }),
            ),
            Err(DocumentServiceError::TooManyCollectionGrants)
        ));
    }

    #[test]
    fn fixed_scope_admits_configured_collections_and_rejects_foreign_writes() {
        let scope = scope([collection(1), collection(2)]);
        let context = CausalContext::new();
        let allowed = [
            DocumentWrite {
                binding: DocumentBinding {
                    collection: collection(1),
                    document: DocumentId::from_bytes([10; 16]),
                    generation: 1,
                    schema_digest: [11; 32],
                },
                observed: &context,
                value: DocumentWriteValue::Tombstone,
            },
            DocumentWrite {
                binding: DocumentBinding {
                    collection: collection(2),
                    document: DocumentId::from_bytes([12; 16]),
                    generation: 1,
                    schema_digest: [13; 32],
                },
                observed: &context,
                value: DocumentWriteValue::Tombstone,
            },
        ];
        assert!(scope.require_writes(&allowed).is_ok());
        assert!(matches!(
            scope.require_collection(collection(3)),
            Err(DocumentServiceError::CollectionOutOfScope)
        ));

        let foreign = [DocumentWrite {
            binding: DocumentBinding {
                collection: collection(3),
                document: DocumentId::from_bytes([14; 16]),
                generation: 1,
                schema_digest: [15; 32],
            },
            observed: &context,
            value: DocumentWriteValue::Tombstone,
        }];
        assert!(matches!(
            scope.require_writes(&foreign),
            Err(DocumentServiceError::CollectionOutOfScope)
        ));
    }

    #[test]
    fn cloned_scope_validates_exact_client_grant_identity_and_collection_subset() {
        let scope = scope([collection(1), collection(2)]);
        let clone = scope.clone();
        let tenant = TenantId::from_bytes([1; 16]);
        let database = DatabaseId::from_bytes([2; 16]);
        let shard = ShardId::new(3);

        assert!(scope.allows_grant(tenant, database, shard, &[collection(1)]));
        assert!(clone.allows_grant(tenant, database, shard, &[collection(2)]));
        assert!(scope.allows_grant(tenant, database, shard, &[collection(1), collection(2)]));
        assert!(!scope.allows_grant(tenant, database, shard, &[]));
        assert!(!scope.allows_grant(tenant, database, shard, &[collection(1), collection(3)]));
        assert!(!scope.allows_grant(
            TenantId::from_bytes([9; 16]),
            database,
            shard,
            &[collection(1)]
        ));
        assert!(!scope.allows_grant(
            tenant,
            DatabaseId::from_bytes([9; 16]),
            shard,
            &[collection(1)]
        ));
        assert!(!scope.allows_grant(tenant, database, ShardId::new(9), &[collection(1)]));
    }

    #[test]
    fn service_owns_one_store_and_commits_reads_tombstones_and_exact_retry() {
        let fixture = ServiceFixture::new();
        let document = DocumentId::from_bytes([70; 16]);
        let empty = CausalContext::new();
        let writes = [
            empty_live_write(&fixture, 0, document, &empty),
            empty_live_write(&fixture, 1, document, &empty),
        ];
        let transaction_id = transaction(3);
        let mut service = fixture.open();

        assert!(matches!(
            Storage::open(fixture.config.storage.clone(), fixture.memory.clone()),
            Err(StorageError::InvalidDirectory {
                reason: "another process owns the shard",
                ..
            })
        ));

        let commit = service
            .commit_documents(transaction_id, &writes)
            .expect("commit both collections in one document group");
        assert_eq!(commit.transaction_id(), transaction_id);
        assert_eq!(commit.local_durable_frontier(), commit.commit_seq());
        assert_eq!(commit.event().database, fixture.config.database);
        assert_eq!(commit.event().shard, fixture.config.shard);
        assert_eq!(commit.event().sequence, 1);

        for (index, collection) in fixture.collections.into_iter().enumerate() {
            let baseline = fixture.memory.snapshot().reserved_bytes;
            let read = service
                .point_read(collection, document)
                .expect("read committed typed value");
            assert!(fixture.memory.snapshot().reserved_bytes > baseline);
            match read.point() {
                DocumentPoint::Value { sibling, .. } => {
                    assert_eq!(sibling.event, commit.event());
                    assert_eq!(sibling.kind, DocumentKind::Live);
                    assert_eq!(sibling.generation, 1);
                    assert_eq!(sibling.schema_digest, fixture.digests[index]);
                }
                other => panic!("expected typed live value, got {other:?}"),
            }
            drop(read);
            assert_eq!(fixture.memory.snapshot().reserved_bytes, baseline);
        }

        let causal = match service
            .point_read(fixture.collections[0], document)
            .expect("read causal context")
            .point()
        {
            DocumentPoint::Value { context, .. } => context.clone(),
            other => panic!("expected typed value, got {other:?}"),
        };
        let tombstone = [DocumentWrite {
            binding: writes[0].binding,
            observed: &causal,
            value: DocumentWriteValue::Tombstone,
        }];
        let deleted = service
            .commit_documents(transaction(4), &tombstone)
            .expect("commit causal tombstone");
        let read = service
            .point_read(fixture.collections[0], document)
            .expect("read typed tombstone");
        assert!(matches!(
            read.point(),
            DocumentPoint::Deleted { sibling, .. }
                if sibling.kind == DocumentKind::Tombstone && sibling.event == deleted.event()
        ));
        drop(read);

        drop(service);
        let mut reopened = fixture.open();
        let retried = reopened
            .commit_documents(transaction_id, &writes)
            .expect("resolve exact retry after service reopen");
        assert_eq!(retried, commit);
        assert!(matches!(
            reopened
                .point_read(fixture.collections[0], document)
                .expect("visible tombstone survives reopen")
                .point(),
            DocumentPoint::Deleted { sibling, .. }
                if sibling.event == deleted.event()
        ));
    }

    #[test]
    fn service_classifies_scope_validation_retry_and_uncertain_errors() {
        let fixture = ServiceFixture::new();
        let document = DocumentId::from_bytes([71; 16]);
        let empty = CausalContext::new();
        let writes = [
            empty_live_write(&fixture, 1, document, &empty),
            empty_live_write(&fixture, 0, document, &empty),
        ];
        let mut service = fixture.open();
        assert!(matches!(
            service.commit_documents(transaction(10), &writes),
            Err(DocumentServiceError::Validation)
        ));

        let future_context = CausalContext::from_entries(
            [(
                OriginId {
                    replica_id: ReplicaId::from_bytes([72; 16]),
                    incarnation: [73; 16],
                },
                1,
            )],
            16,
        )
        .expect("well-formed but unobserved causal context");
        let future_write = [empty_live_write(&fixture, 0, document, &future_context)];
        assert!(matches!(
            service.commit_documents(transaction(11), &future_write),
            Err(DocumentServiceError::Validation)
        ));

        let valid_write = [empty_live_write(&fixture, 0, document, &empty)];
        service
            .commit_documents(transaction(12), &valid_write)
            .expect("commit valid baseline");
        let changed_retry = [DocumentWrite {
            binding: valid_write[0].binding,
            observed: &empty,
            value: DocumentWriteValue::Tombstone,
        }];
        assert!(matches!(
            service.commit_documents(transaction(12), &changed_retry),
            Err(DocumentServiceError::RetryConflict)
        ));
        assert!(matches!(
            service.commit_documents(TransactionId::new(999, [13; 16]), &valid_write),
            Err(DocumentServiceError::RetryExpired)
        ));
        assert!(matches!(
            DocumentServiceError::from_commit(DocumentError::Corrupt("bad state")),
            DocumentServiceError::Corrupt
        ));
        assert!(matches!(
            DocumentServiceError::from_document(DocumentError::Format(FormatError::Invalid {
                format: "document state",
                reason: "bad state",
            })),
            DocumentServiceError::Corrupt
        ));
        assert!(matches!(
            DocumentServiceError::from_document(DocumentError::Format(FormatError::Allocation)),
            DocumentServiceError::Capacity
        ));
        assert!(matches!(
            DocumentServiceError::from_commit(DocumentError::InvalidDocumentFields(
                FormatError::Invalid {
                    format: "document",
                    reason: "schema field missing",
                }
            )),
            DocumentServiceError::Validation
        ));
        assert!(matches!(
            DocumentServiceError::from_commit(DocumentError::Storage(StorageError::Core(
                CoreError::InvalidTransactionExpiry
            ))),
            DocumentServiceError::Validation
        ));
        let uncertain = DocumentServiceError::from_commit(DocumentError::CommitUncertain(
            Box::new(DocumentError::Corrupt("verified frame mismatch")),
        ));
        assert!(matches!(
            uncertain,
            DocumentServiceError::UncertainCommit(_)
        ));
        assert_eq!(
            DocumentServiceError::UncertainCommit(Box::new(DocumentError::NeedsRecovery))
                .to_string(),
            "document commit requires recovery or retry reconciliation"
        );
    }
}
