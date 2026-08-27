//! The engine-side bulk-insert ingestion stream (GAP-D10, ticket #61).
//!
//! SCAFFOLD: this is the red-test vehicle — it materializes the whole
//! source before committing once, which the bounded-memory proof
//! (`bulk_ingest_never_materializes_the_whole_batch`) must observe RED
//! before the streaming implementation replaces `run`.

use std::{collections::BTreeMap, fmt};

use crate::engine::{CommitError, CommitOutcome, Engine, EngineError, UnknownReason};
use crate::operations::RequestValidationError;
use crate::types::{
    Cancellation, CollectionName, Deadline, Document, DocumentId, ExpectedVersion,
    LocalDurability, OperationId, StorageSequence, TenantScope,
};

/// The maximum number of documents one forming batch holds.
pub const MAX_INGEST_BATCH_DOCUMENTS: usize = 8192;

/// The maximum number of per-document refusal details the receipt retains.
pub const MAX_INGEST_REFUSAL_DETAILS: usize = 64;

/// How the stream treats a document whose key already exists.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum IngestConflictMode {
    /// Overwrite existing documents (V1 bulk insert: `Put` with a null
    /// change vector). The destructive variant: it is an explicit typed
    /// selection at this seam, and the host/TBEP surface confirms it.
    Overwrite,
    /// Refuse to overwrite: every staged put carries
    /// [`ExpectedVersion::New`], so an existing document rejects the batch
    /// as a typed conflict.
    CreateOnly,
}

/// A validated bulk-ingest request (the engine-boundary operation shape).
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct BulkIngestRequest {
    conflict: IngestConflictMode,
    durability: LocalDurability,
    batch_documents: usize,
    deadline: Deadline,
    cancellation: Cancellation,
}

impl BulkIngestRequest {
    /// Creates a request after checking its liveness bounds.
    ///
    /// The conflict mode is a required argument: the destructive overwrite
    /// variant can never be reached by default.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidDeadline`] for an elapsed
    /// deadline and [`RequestValidationError::InvalidCancellation`] for an
    /// already-cancelled signal.
    pub fn try_new(
        conflict: IngestConflictMode,
        durability: LocalDurability,
        deadline: Deadline,
        cancellation: Cancellation,
    ) -> Result<Self, RequestValidationError> {
        if deadline.is_expired() {
            return Err(RequestValidationError::InvalidDeadline);
        }
        if cancellation.is_cancelled() {
            return Err(RequestValidationError::InvalidCancellation);
        }
        Ok(Self {
            conflict,
            durability,
            batch_documents: MAX_INGEST_BATCH_DOCUMENTS,
            deadline,
            cancellation,
        })
    }

    /// Selects the batch bound: how many documents one forming batch holds
    /// before it publishes.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidRange`] for a zero bound or
    /// one over [`MAX_INGEST_BATCH_DOCUMENTS`].
    pub fn try_with_batch_documents(
        mut self,
        batch_documents: usize,
    ) -> Result<Self, RequestValidationError> {
        if batch_documents == 0 || batch_documents > MAX_INGEST_BATCH_DOCUMENTS {
            return Err(RequestValidationError::InvalidRange);
        }
        self.batch_documents = batch_documents;
        Ok(self)
    }

    /// Returns the conflict mode.
    #[must_use]
    pub const fn conflict_mode(&self) -> IngestConflictMode {
        self.conflict
    }

    /// Returns the local durability point each batch commits to.
    #[must_use]
    pub const fn durability(&self) -> LocalDurability {
        self.durability
    }

    /// Returns the batch bound.
    #[must_use]
    pub const fn batch_documents(&self) -> usize {
        self.batch_documents
    }

    /// Returns the deadline.
    #[must_use]
    pub const fn deadline(&self) -> Deadline {
        self.deadline
    }

    /// Returns the cancellation signal.
    #[must_use]
    pub const fn cancellation(&self) -> &Cancellation {
        &self.cancellation
    }
}

/// One typed per-document refusal recorded in the receipt.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct IngestRefusal {
    collection: CollectionName,
    document: DocumentId,
    reason: IngestRefusalReason,
}

impl IngestRefusal {
    /// Creates one refusal from its facts.
    #[must_use]
    pub const fn new(
        collection: CollectionName,
        document: DocumentId,
        reason: IngestRefusalReason,
    ) -> Self {
        Self { collection, document, reason }
    }

    /// Returns the owning collection.
    #[must_use]
    pub const fn collection(&self) -> &CollectionName {
        &self.collection
    }

    /// Returns the refused document identity.
    #[must_use]
    pub const fn document(&self) -> &DocumentId {
        &self.document
    }

    /// Returns the typed refusal reason.
    #[must_use]
    pub const fn reason(&self) -> IngestRefusalReason {
        self.reason
    }
}

/// The typed reason one document was refused without aborting the stream.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum IngestRefusalReason {
    /// The document failed the engine's structural limits or its
    /// collection's schema: nothing was staged for it.
    InvalidDocument,
}

/// The receipt of one committed batch.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct IngestBatchReceipt {
    operation_id: OperationId,
    documents: u64,
    sequence: StorageSequence,
    durability: LocalDurability,
}

impl IngestBatchReceipt {
    /// Creates one batch receipt from the published facts.
    #[must_use]
    pub const fn new(
        operation_id: OperationId,
        documents: u64,
        sequence: StorageSequence,
        durability: LocalDurability,
    ) -> Self {
        Self { operation_id, documents, sequence, durability }
    }

    /// Returns the batch transaction's operation identity.
    #[must_use]
    pub const fn operation_id(&self) -> &OperationId {
        &self.operation_id
    }

    /// Returns the accepted pulls this batch published.
    #[must_use]
    pub const fn documents(&self) -> u64 {
        self.documents
    }

    /// Returns the storage sequence the batch committed at.
    #[must_use]
    pub const fn sequence(&self) -> StorageSequence {
        self.sequence
    }

    /// Returns the durability point the batch reached.
    #[must_use]
    pub const fn durability(&self) -> LocalDurability {
        self.durability
    }
}

/// How one ingestion run ended.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum IngestTermination {
    /// The source was exhausted; every pulled document is accounted.
    Completed,
    /// The caller's cancellation was observed: the forming batch's
    /// documents are counted aborted, and committed-so-far stays counted.
    Cancelled,
    /// The deadline expired before the next pull.
    Deadline,
    /// A batch commit was rejected: the stream stopped, earlier batches stay
    /// committed, and the failed batch's documents are aborted-counted.
    Rejected {
        /// The failed batch's operation identity.
        operation: OperationId,
        /// The typed rejection.
        error: CommitError,
    },
    /// A batch commit's outcome is unknown: resolve by the named operation
    /// identity. The receipt claims nothing for that batch.
    Unknown {
        /// the uncertain batch's operation identity.
        operation: OperationId,
        /// The typed uncertainty reason.
        reason: UnknownReason,
    },
    /// The engine refused a batch transaction or an engine-level stage.
    Engine(EngineError),
}

/// The terminal accounting of one ingestion run.
///
/// The counts reconcile exactly: `attempted == committed + refused +
/// aborted` on every path, so an aborted run never reports a document it
/// did not commit. Detail is bounded: the last committed batch, the
/// terminal failure, and a bounded drop-oldest window of refusal details.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct IngestReceipt {
    attempted: u64,
    committed: u64,
    refused: u64,
    aborted: u64,
    committed_batches: u64,
    last_committed: Option<IngestBatchReceipt>,
    refusal_details: Vec<IngestRefusal>,
    dropped_refusal_details: u64,
    termination: IngestTermination,
}

impl IngestReceipt {
    /// Creates the empty receipt of a run that pulled nothing.
    #[must_use]
    pub fn cancelled() -> Self {
        Self {
            attempted: 0,
            committed: 0,
            refused: 0,
            aborted: 0,
            committed_batches: 0,
            last_committed: None,
            refusal_details: Vec::new(),
            dropped_refusal_details: 0,
            termination: IngestTermination::Cancelled,
        }
    }

    /// Returns the documents pulled from the source.
    #[must_use]
    pub const fn attempted(&self) -> u64 {
        self.attempted
    }

    /// Returns the pulls whose batch committed.
    #[must_use]
    pub const fn committed(&self) -> u64 {
        self.committed
    }

    /// Returns the pulls refused per-document without aborting.
    #[must_use]
    pub const fn refused(&self) -> u64 {
        self.refused
    }

    /// Returns the pulls neither committed nor refused: a forming batch the
    /// run never published, or a batch whose commit rejected or stayed
    /// unknown.
    #[must_use]
    pub const fn aborted(&self) -> u64 {
        self.aborted
    }

    /// Returns the number of committed batches.
    #[must_use]
    pub const fn committed_batches(&self) -> u64 {
        self.committed_batches
    }

    /// Returns the last committed batch's receipt.
    #[must_use]
    pub const fn last_committed_batch(&self) -> Option<&IngestBatchReceipt> {
        self.last_committed.as_ref()
    }

    /// Returns the bounded refusal-detail window, oldest first.
    #[must_use]
    pub fn refusal_details(&self) -> &[IngestRefusal] {
        &self.refusal_details
    }

    /// Returns how many refusal details were dropped from the bounded
    /// window.
    #[must_use]
    pub const fn dropped_refusal_details(&self) -> u64 {
        self.dropped_refusal_details
    }

    /// Returns the typed termination.
    #[must_use]
    pub const fn termination(&self) -> &IngestTermination {
        &self.termination
    }

    /// Reports whether the source was exhausted: the only outcome that may
    /// be read as a completed run.
    #[must_use]
    pub const fn is_complete(&self) -> bool {
        matches!(self.termination, IngestTermination::Completed)
    }

    /// Reports whether the counts reconcile.
    #[must_use]
    pub const fn reconciles(&self) -> bool {
        self.attempted == self.committed + self.refused + self.aborted
    }
}

/// The engine-side bulk-insert ingestion stream.
///
/// The stream consumes any document source one document at a time, stages
/// into a forming batch bounded at the request's batch size, and publishes
/// each full batch as one engine transaction — one operation identity, one
/// atomic commit. Per-document validation refusals abort nothing; a
/// rejected or unknown batch commit stops the stream with exact accounting.
pub struct IngestStream<'engine> {
    engine: &'engine Engine,
    scope: TenantScope,
    request: BulkIngestRequest,
    identity: u64,
    next_batch: u64,
}

impl fmt::Debug for IngestStream<'_> {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter
            .debug_struct("IngestStream")
            .field("request", &self.request)
            .field("identity", &self.identity)
            .field("next_batch", &self.next_batch)
            .finish_non_exhaustive()
    }
}

impl<'engine> IngestStream<'engine> {
    pub(crate) fn new(
        engine: &'engine Engine,
        scope: TenantScope,
        request: BulkIngestRequest,
    ) -> Self {
        Self { engine, scope, request, identity: next_stream_identity(), next_batch: 0 }
    }

    /// Consumes the source to its end, to the caller's cancellation, or to
    /// the first aborting batch outcome, and returns the reconciling
    /// receipt.
    #[must_use]
    pub fn run<I>(self, documents: I) -> IngestReceipt
    where
        I: IntoIterator<Item = Document>,
    {
        let Self { engine, scope, request, identity, mut next_batch } = self;
        let expected = match request.conflict_mode() {
            IngestConflictMode::Overwrite => ExpectedVersion::Any,
            IngestConflictMode::CreateOnly => ExpectedVersion::New,
        };
        let mut receipt = IngestReceipt::cancelled();
        // SCAFFOLD: the whole source is materialized before one commit.
        let mut forming: BTreeMap<(CollectionName, DocumentId), Document> = BTreeMap::new();
        let mut forming_pulls: u64 = 0;
        for document in documents {
            if request.cancellation().is_cancelled() || request.deadline().is_expired() {
                receipt.termination = if request.deadline().is_expired() {
                    IngestTermination::Deadline
                } else {
                    IngestTermination::Cancelled
                };
                receipt.aborted += forming_pulls;
                return receipt;
            }
            receipt.attempted += 1;
            if !crate::storage::document_within_limits(&document) {
                record_refusal(&mut receipt, &document);
                continue;
            }
            forming_pulls += 1;
            let key = (document.collection().clone(), document.id().clone());
            forming.insert(key, document);
        }
        if forming.is_empty() {
            receipt.termination = IngestTermination::Completed;
            return receipt;
        }
        let operation = batch_operation_id(identity, &mut next_batch);
        let snapshot = match engine.read_snapshot(&scope) {
            Ok(snapshot) => snapshot,
            Err(error) => {
                receipt.aborted += forming_pulls;
                receipt.termination = IngestTermination::Engine(error);
                return receipt;
            }
        };
        let mut transaction = match engine.begin(&scope, snapshot, operation.clone()) {
            Ok(transaction) => transaction,
            Err(error) => {
                receipt.aborted += forming_pulls;
                receipt.termination = IngestTermination::Engine(error);
                return receipt;
            }
        };
        for ((_, _), document) in &forming {
            match transaction.put(document.clone(), expected) {
                Ok(()) => {}
                Err(EngineError::InvalidDocument) => {
                    record_refusal(&mut receipt, document);
                    forming_pulls -= 1;
                }
                Err(error) => {
                    receipt.aborted += forming_pulls;
                    receipt.termination = IngestTermination::Engine(error);
                    return receipt;
                }
            }
        }
        match transaction.commit(request.durability()) {
            CommitOutcome::Committed(commit) => {
                receipt.committed += forming_pulls;
                receipt.committed_batches += 1;
                receipt.last_committed = Some(IngestBatchReceipt::new(
                    operation,
                    forming_pulls,
                    commit.sequence(),
                    commit.durability(),
                ));
                receipt.termination = IngestTermination::Completed;
            }
            CommitOutcome::Rejected(error) => {
                receipt.aborted += forming_pulls;
                receipt.termination = IngestTermination::Rejected { operation, error };
            }
            CommitOutcome::Unknown(unknown) => {
                receipt.aborted += forming_pulls;
                receipt.termination = IngestTermination::Unknown {
                    operation,
                    reason: unknown.reason(),
                };
            }
        }
        receipt
    }

    /// Stops without consuming the source — V1's `AbortAsync` when nothing
    /// was stored: a receipt with zero counts and no claim.
    #[must_use]
    pub fn abort(self) -> IngestReceipt {
        IngestReceipt::cancelled()
    }
}

/// Records one per-document refusal and its bounded detail.
fn record_refusal(receipt: &mut IngestReceipt, document: &Document) {
    receipt.refused += 1;
    if receipt.refusal_details.len() >= MAX_INGEST_REFUSAL_DETAILS {
        receipt.refusal_details.remove(0);
        receipt.dropped_refusal_details += 1;
    }
    receipt.refusal_details.push(IngestRefusal::new(
        document.collection().clone(),
        document.id().clone(),
        IngestRefusalReason::InvalidDocument,
    ));
}

/// Mints never-reused stream identities for operation-id derivation.
fn next_stream_identity() -> u64 {
    static COUNTER: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(0);
    COUNTER.fetch_add(1, std::sync::atomic::Ordering::Relaxed)
}

/// Derives the next batch's operation identity.
fn batch_operation_id(identity: u64, next_batch: &mut u64) -> OperationId {
    let ordinal = *next_batch;
    *next_batch = next_batch.saturating_add(1);
    OperationId::try_new(&format!("tbv2-ingest-{identity}-b{ordinal}"))
        .expect("derived ingest identities are valid")
}
