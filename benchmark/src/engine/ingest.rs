//! The engine-side bulk-insert ingestion stream (GAP-D10, ticket #61).
//!
//! One [`IngestStream`] consumes any document source **one document at a
//! time**, stages into a forming batch bounded at the request's batch size
//! — mirroring V1's server-side batch bound — and publishes each full
//! batch as one engine transaction: one [`OperationId`], one atomic commit,
//! all or nothing, exactly V1's per-batch transaction shape.
//!
//! The memory bound is structural: nothing in the stream is proportional to
//! the stream length. The forming batch holds at most
//! [`MAX_INGEST_BATCH_DOCUMENTS`] documents, the source iterator is pulled
//! incrementally, and the receipt holds counters plus O(1) detail (the last
//! committed batch, the terminal failure, a bounded refusal window). The
//! focused test `bulk_ingest_never_materializes_the_whole_batch` proves the
//! bound behaviorally: while the source is consumed, every batch behind the
//! cursor must already be published.
//!
//! Error classes map V1 precisely (the decision record is
//! `docs/engine/gapd10-bulk-ingest.md`): a document that fails the engine's
//! structural limits or its collection's schema is **refused typed and the
//! stream continues** — V1's continue class (its client-side protocol
//! checks throw before anything reaches the stream, leaving the operation
//! usable) — while a rejected or unknown **batch commit aborts the
//! stream** — V1's server-side stop — with earlier committed batches left
//! committed and the failed batch's documents accounted aborted, never
//! claimed. The receipt's counts reconcile exactly:
//! `attempted == committed + refused + aborted` on every path.
//!
//! The destructive variant is explicit at this seam: bulk insert's V1
//! semantics overwrite existing documents, so
//! [`IngestConflictMode::Overwrite`] is a required typed selection, never a
//! default, and [`IngestConflictMode::CreateOnly`] stages creates that
//! reject on conflict. Interactive confirmation of the destructive mode
//! belongs to the typed-operation surface (charter `PD-2026-08-26-07`),
//! whose bulk path is proposed in the decision record's D7.

use std::{collections::BTreeMap, fmt};

use crate::engine::{CommitError, CommitOutcome, Engine, EngineError, UnknownReason};
use crate::operations::RequestValidationError;
use crate::types::{
    CollectionName, Deadline, Document, DocumentId, ExpectedVersion, LocalDurability, OperationId,
    StorageSequence, TenantScope,
};

/// The maximum number of documents one forming batch holds.
///
/// The bound mirrors V1's server-side batch bound: the bulk-insert handler
/// flushes at 8192 accumulated operations
/// (`AbstractBulkInsertHandlerProcessor.cs:119-121`, pinned V1 tree). The
/// engine's per-document limits already bound bytes
/// (`document_within_limits`), so no second byte bound exists.
pub const MAX_INGEST_BATCH_DOCUMENTS: usize = 8192;

/// The maximum number of per-document refusal details the receipt retains.
///
/// Refusals are always counted exactly; the details ride a bounded
/// drop-oldest window with the overflow counted and disclosed — the same
/// bounded-disclosure idiom the streaming subscription seam uses.
pub const MAX_INGEST_REFUSAL_DETAILS: usize = 64;

/// How the stream treats a document whose key already exists.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum IngestConflictMode {
    /// Overwrite existing documents (V1 bulk insert: `Put` with a null
    /// change vector). The destructive variant: it is an explicit typed
    /// selection at this seam, and the host/TBEP surface confirms it.
    Overwrite,
    /// Refuse to overwrite: every staged put carries
    /// [`ExpectedVersion::New`], so a batch containing an existing document
    /// is rejected as a typed conflict and the stream aborts.
    CreateOnly,
}

/// A validated bulk-ingest request (the engine-boundary operation shape the
/// future host/TBEP registry entry wraps; decision record D7).
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct BulkIngestRequest {
    conflict: IngestConflictMode,
    durability: LocalDurability,
    batch_documents: usize,
    deadline: Deadline,
}

impl BulkIngestRequest {
    /// Creates a request after checking its liveness bound.
    ///
    /// The conflict mode is a required argument: the destructive overwrite
    /// variant can never be reached by default.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidDeadline`] for an elapsed
    /// deadline.
    pub fn try_new(
        conflict: IngestConflictMode,
        durability: LocalDurability,
        deadline: Deadline,
    ) -> Result<Self, RequestValidationError> {
        if deadline.is_expired() {
            return Err(RequestValidationError::InvalidDeadline);
        }
        Ok(Self { conflict, durability, batch_documents: MAX_INGEST_BATCH_DOCUMENTS, deadline })
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

    /// Returns the batch transaction's operation identity: the handle
    /// [`Engine::resolve_commit`](crate::engine::Engine::resolve_commit)
    /// answers every document in this batch by.
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
    /// An explicit [`IngestStream::abort`] before any pull reports the same
    /// termination with zero counts.
    Cancelled,
    /// The deadline expired before the next pull or batch commit.
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
        /// The uncertain batch's operation identity.
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
/// did not commit. Detail is bounded by design: the last committed batch,
/// the terminal failure, and a bounded drop-oldest window of refusal
/// details — nothing proportional to the stream length.
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
/// Opened through [`Engine::ingest_stream`], which validates the scope.
/// [`IngestStream::run`] consumes the source; [`IngestStream::abort`] stops
/// before consuming anything. Both consume the stream, so use after an
/// abort is a compile error — V1's post-abort store refusal
/// (`KilledTooEarly`) has no runtime shape to reach here.
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

/// The forming batch's keying: storage keys a document by tenant,
/// collection, and identifier, and the engine's commit seam refuses
/// duplicate targets in one write set — so a re-put of the same key
/// replaces, mapping V1's sequential in-batch `Put` (last write wins).
type FormingKey = (CollectionName, DocumentId);

impl<'engine> IngestStream<'engine> {
    pub(crate) fn new(
        engine: &'engine Engine,
        scope: TenantScope,
        request: BulkIngestRequest,
    ) -> Self {
        Self { engine, scope, request, identity: next_stream_identity(), next_batch: 0 }
    }

    /// Consumes the source one document at a time and returns the
    /// reconciling receipt.
    ///
    /// Each document is structurally validated at pull time, staged into
    /// the forming batch, and published with its batch when the batch
    /// reaches the request's bound (or at source exhaustion). The
    /// cancellation signal gates between pulls and before the final partial
    /// batch publishes — a shareable by-reference signal, like the
    /// streaming seam's, because a cloned [`Cancellation`](crate::types::Cancellation) is an
    /// independent snapshot.
    ///
    /// # Panics
    ///
    /// Never: the derived batch operation identities are constructed from
    /// validated parts.
    #[must_use]
    pub fn run<I>(self, documents: I, cancellation: &crate::types::Cancellation) -> IngestReceipt
    where
        I: IntoIterator<Item = Document>,
    {
        let Self { engine, scope, request, identity, mut next_batch } = self;
        let expected = match request.conflict_mode() {
            IngestConflictMode::Overwrite => ExpectedVersion::Any,
            IngestConflictMode::CreateOnly => ExpectedVersion::New,
        };
        let mut receipt = IngestReceipt::cancelled();
        let mut forming: BTreeMap<FormingKey, Document> = BTreeMap::new();
        // Pulls accepted into the forming batch: duplicates each count,
        // because both were accepted and the later one won, exactly like
        // V1's sequential in-batch puts.
        let mut forming_pulls: u64 = 0;

        let mut source = documents.into_iter();
        loop {
            // Liveness gates between documents — the position of V1's
            // per-store token check — so every pulled document is staged or
            // refused before the next gate runs.
            if cancellation.is_cancelled() {
                receipt.termination = IngestTermination::Cancelled;
                receipt.aborted += forming_pulls;
                return receipt;
            }
            if request.deadline().is_expired() {
                receipt.termination = IngestTermination::Deadline;
                receipt.aborted += forming_pulls;
                return receipt;
            }
            let Some(document) = source.next() else { break };
            receipt.attempted += 1;
            // The pull-time structural check refuses a document without it
            // ever entering the forming batch; the collection-schema check
            // re-runs at staging time inside the batch flush.
            if !crate::storage::document_within_limits(&document) {
                record_refusal(&mut receipt, &document);
                continue;
            }
            forming_pulls += 1;
            let key = (document.collection().clone(), document.id().clone());
            forming.insert(key, document);
            if forming.len() < request.batch_documents() {
                continue;
            }
            match flush_batch(
                engine,
                &scope,
                &request,
                expected,
                identity,
                &mut next_batch,
                &mut forming,
                &mut forming_pulls,
                &mut receipt,
            ) {
                FlushOutcome::Published => {}
                FlushOutcome::Stopped => return receipt,
            }
        }
        // Source exhausted: publish the final partial batch. Both liveness
        // gates run once more so a stop between the last pull and this
        // commit claims nothing for the forming documents.
        if !forming.is_empty() {
            if cancellation.is_cancelled() {
                receipt.termination = IngestTermination::Cancelled;
                receipt.aborted += forming_pulls;
                return receipt;
            }
            if request.deadline().is_expired() {
                receipt.termination = IngestTermination::Deadline;
                receipt.aborted += forming_pulls;
                return receipt;
            }
            match flush_batch(
                engine,
                &scope,
                &request,
                expected,
                identity,
                &mut next_batch,
                &mut forming,
                &mut forming_pulls,
                &mut receipt,
            ) {
                FlushOutcome::Published => {}
                FlushOutcome::Stopped => return receipt,
            }
        }
        receipt.termination = IngestTermination::Completed;
        receipt
    }

    /// Stops without consuming the source — V1's `AbortAsync` when nothing
    /// was stored (`BulkInsertOperation.cs:600-604`): a receipt with zero
    /// counts and no claim.
    #[must_use]
    pub fn abort(self) -> IngestReceipt {
        IngestReceipt::cancelled()
    }
}

/// The result of one batch flush.
enum FlushOutcome {
    /// The batch published; the stream continues.
    Published,
    /// The stream stops: the receipt already carries its termination and
    /// the aborted count for everything that was forming.
    Stopped,
}

/// Publishes the forming batch as one atomic transaction and folds the
/// outcome into the receipt.
///
/// Every pull in the forming batch is accounted exactly once: a per-document
/// staging refusal moves its pull to `refused`, a committed batch moves the
/// rest to `committed`, and a rejected, unknown, or engine-refused batch
/// moves them to `aborted` — never to `committed`.
#[allow(clippy::too_many_arguments, reason = "one flush of the forming batch's full state")]
fn flush_batch(
    engine: &Engine,
    scope: &TenantScope,
    request: &BulkIngestRequest,
    expected: ExpectedVersion,
    identity: u64,
    next_batch: &mut u64,
    forming: &mut BTreeMap<FormingKey, Document>,
    forming_pulls: &mut u64,
    receipt: &mut IngestReceipt,
) -> FlushOutcome {
    let operation = batch_operation_id(identity, next_batch);
    let snapshot = match engine.read_snapshot(scope) {
        Ok(snapshot) => snapshot,
        Err(error) => return engine_stop(receipt, *forming_pulls, error),
    };
    let mut transaction = match engine.begin(scope, snapshot, operation.clone()) {
        Ok(transaction) => transaction,
        Err(error) => return engine_stop(receipt, *forming_pulls, error),
    };
    for ((_, _), document) in forming.iter() {
        match transaction.put(document.clone(), expected) {
            Ok(()) => {}
            // A schema-level per-document refusal aborts nothing: the
            // document stages nowhere and the stream continues. Its pull
            // leaves the batch so the committed count never claims it.
            Err(EngineError::InvalidDocument) => {
                record_refusal(receipt, document);
                *forming_pulls -= 1;
            }
            Err(error) => return engine_stop(receipt, *forming_pulls, error),
        }
    }
    match transaction.commit(request.durability()) {
        CommitOutcome::Committed(commit) => {
            receipt.committed += *forming_pulls;
            receipt.committed_batches += 1;
            receipt.last_committed = Some(IngestBatchReceipt::new(
                operation,
                *forming_pulls,
                commit.sequence(),
                commit.durability(),
            ));
            forming.clear();
            *forming_pulls = 0;
            FlushOutcome::Published
        }
        CommitOutcome::Rejected(error) => {
            receipt.aborted += *forming_pulls;
            receipt.termination = IngestTermination::Rejected { operation, error };
            FlushOutcome::Stopped
        }
        CommitOutcome::Unknown(unknown) => {
            receipt.aborted += *forming_pulls;
            receipt.termination =
                IngestTermination::Unknown { operation, reason: unknown.reason() };
            FlushOutcome::Stopped
        }
    }
}

/// Folds an engine-level failure into the receipt and reports the stop.
fn engine_stop(
    receipt: &mut IngestReceipt,
    forming_pulls: u64,
    error: EngineError,
) -> FlushOutcome {
    receipt.aborted += forming_pulls;
    receipt.termination = IngestTermination::Engine(error);
    FlushOutcome::Stopped
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

/// Derives the next batch's operation identity: one identity per batch
/// transaction, the engine's `OperationId` discipline (decision record D3).
fn batch_operation_id(identity: u64, next_batch: &mut u64) -> OperationId {
    let ordinal = *next_batch;
    *next_batch = next_batch.saturating_add(1);
    OperationId::try_new(&format!("terrabase-ingest-{identity}-b{ordinal}"))
        .expect("derived ingest identities are valid")
}
