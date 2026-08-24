//! The streaming seam: bounded, ordered, cancellable streams and
//! subscriptions over immutable snapshots and published commit facts.
//!
//! This module implements the W4-STREAM contract
//! (`docs/design/w4-transfer-replication-contract.md`, "Streaming
//! contract"). One batch contract governs every source:
//! [`StreamStore::next_batch`] serves at most the caller's item limit, ends
//! with the next [`StreamCursor`], and reports a terminal
//! [`StreamCompletion`] exactly once at the end of the stream. Ordering is
//! per source and part of the public contract: documents in canonical key
//! order, queries in the compiled plan's order, time-series entries in
//! timestamp order, family reads through the family seam's typed readers,
//! and change subscriptions strictly by [`StorageSequence`].
//!
//! Contract facts this implementation keeps:
//!
//! - Streams read only immutable snapshot state and already-published commit
//!   facts. A live, uncommitted transaction is never observable through any
//!   stream; streaming code never begins an Engine transaction.
//! - Every buffer is bounded. A slow consumer is reported — never buffered
//!   without bound — and never blocks an Engine commit.
//! - Delivery is at-least-once with stable operation identity, so
//!   duplicates are identifiable by the consumer.
//! - No path, payload, transport detail, or storage handle crosses this
//!   seam.
//!
//! The design record with the delivery and overflow policy, per-row
//! coverage notes, non-claims, limits, and proposed collision actions is
//! `docs/streaming/w4-stream.md`.

mod sources;
mod subscription;
#[cfg(test)]
mod tests;

use crate::{
    engine::Engine,
    families::{FamilyReadResult, TimeSeriesEntry, TimeSeriesRange},
    query::{QueryPlan, QueryRow},
    types::{
        Cancellation, CollectionName, Deadline, Document, DocumentId, OperationId, ReadSnapshot,
        StorageSequence, TenantScope,
    },
};

pub use sources::StreamSourceKind;

/// The largest batch one [`StreamStore::next_batch`] call may serve.
///
/// A larger requested bound is a typed
/// [`StreamError::InvalidSource`] value, never a
/// silently truncated batch.
pub const MAX_STREAM_BATCH_ITEMS: u32 = 1024;

/// The largest subscription batch a [`SubscriptionSpec`] may request.
pub const MAX_SUBSCRIPTION_BATCH_ITEMS: u32 = 1024;

/// The largest exclusion list one [`DocumentStreamSelector`] may carry.
pub const MAX_EXCLUDED_IDS: usize = 64;

/// A streaming store borrowed from one durable `Engine`.
///
/// - The only production acquisition path is
///   [`Engine::streams()`](crate::engine::Engine::streams). No independent
///   durable or in-memory production constructor exists, mirroring
///   [`QueryStore`](crate::query::QueryStore) and
///   [`FamilyStore`](crate::families::FamilyStore).
/// - One batch contract governs every source: a batch is bounded by the
///   caller's item limit, ends with the next `StreamCursor`, and a terminal
///   `StreamCompletion` is reported exactly once at the end of the stream.
/// - Streams read only immutable snapshot state and already-published commit
///   facts. A live, uncommitted transaction is never observable through any
///   stream.
/// - Buffers are bounded: a slow consumer is reported as
///   [`StreamCompletion::SlowConsumer`] or
///   [`StreamError::SlowConsumer`], never buffered without bound, and never
///   blocks an engine commit.
pub struct StreamStore<'engine> {
    engine: &'engine Engine,
}

impl<'engine> StreamStore<'engine> {
    /// Binds one store to one `Engine`. Crate-only: the durable `Engine`
    /// through `Engine::streams()` is the only production acquisition path.
    pub(crate) fn from_engine(engine: &'engine Engine) -> Self {
        Self { engine }
    }

    /// Validates one source against one scope and snapshot and mints the
    /// start cursor of its stream.
    ///
    /// Selectors are validated before the first batch: an invalid selector,
    /// a query plan without a bounded page, an already elapsed deadline, an
    /// already cancelled signal, and an exact-id selector addressing a
    /// document absent from the snapshot view are all typed
    /// [`StreamError`] values here, never silently skipped items.
    ///
    /// # Errors
    ///
    /// Returns [`StreamError::SnapshotEngineMismatch`] for a scope or
    /// snapshot naming another `Engine`, [`StreamError::TenantMismatch`]
    /// when the scope and snapshot name different tenants, and the typed
    /// source-validation error otherwise.
    #[allow(clippy::needless_pass_by_value)] // opening hands the source over
    pub fn open(
        &self,
        scope: &TenantScope,
        snapshot: &ReadSnapshot,
        source: StreamSource,
    ) -> Result<StreamCursor, StreamError> {
        sources::open(self.engine, scope, snapshot, &source)
    }

    /// Serves one bounded batch of a snapshot-bound stream.
    ///
    /// The batch carries at most `limit` items, the cursor that resumes
    /// exactly after them, and — exactly once, at the end of the stream —
    /// the terminal [`StreamCompletion`]. Re-polling a finished stream
    /// idempotently returns an empty batch with the same terminal
    /// observation.
    ///
    /// The cursor must bind to this `Engine`, this source, and the snapshot
    /// the stream was opened against; anything else is
    /// [`StreamError::InvalidCursor`]. The caller's `Cancellation` signal
    /// is checked before any work — a cancelled stream closes promptly and
    /// reports [`StreamError::Cancelled`] — because the shared
    /// [`Cancellation`] value severs on clone,
    /// so only a per-call reference observes a live cancel.
    ///
    /// # Errors
    ///
    /// Returns [`StreamError::Cancelled`] for a cancelled signal,
    /// [`StreamError::InvalidSource`] for a zero or over-limit `limit`,
    /// [`StreamError::InvalidCursor`] for a cursor from another source,
    /// `Engine`, or snapshot, [`StreamError::Deadline`] for an elapsed
    /// deadline the source carries, and the typed read error of the source
    /// otherwise.
    #[allow(clippy::needless_pass_by_value)] // the frozen contract passes the cursor by value
    pub fn next_batch(
        &self,
        scope: &TenantScope,
        snapshot: &ReadSnapshot,
        source: &StreamSource,
        cursor: StreamCursor,
        limit: u32,
        cancellation: &Cancellation,
    ) -> Result<StreamBatch, StreamError> {
        sources::next_batch(self.engine, scope, snapshot, source, cursor, limit, cancellation)
    }

    /// Observes one cursor publicly: the storage sequence it is bound to
    /// and its source kind, without internal offsets.
    #[must_use]
    #[allow(clippy::unused_self, reason = "one store-bound observation seam")]
    pub fn position(&self, cursor: &StreamCursor) -> StreamPosition {
        sources::position(cursor)
    }

    /// Opens one named subscription under this scope and returns its
    /// negotiated status.
    ///
    /// The subscription is bound to this `Engine` and the scope's tenant.
    /// Opening an already open subscription is a typed error — exactly one
    /// active consumer owns a subscription at a time — while opening a
    /// reconnecting one reattaches to its preserved checkpoint and resend
    /// state. Delivery starts from the journal's durable high-water mark
    /// unless the spec asks to start from the beginning.
    ///
    /// # Errors
    ///
    /// Returns [`StreamError::SnapshotEngineMismatch`] for a scope naming
    /// another `Engine`, [`StreamError::InvalidSource`] for an invalid spec
    /// or an already open subscription, and
    /// [`StreamError::EngineClosed`] when the publication registry cannot
    /// accept the notification sink.
    pub fn subscribe(
        &self,
        scope: &TenantScope,
        spec: &SubscriptionSpec,
    ) -> Result<SubscriptionStatus, StreamError> {
        subscription::subscribe(self.engine, scope, spec)
    }

    /// Serves one bounded subscription batch and records its boundary.
    ///
    /// The batch is bounded by the spec's max items, carries its
    /// [`SubscriptionCheckpoint`], and reports filtered drops and dropped
    /// notifications as progress facts. The boundary is recorded before the
    /// call returns; the durable checkpoint advances only when the caller
    /// acknowledges it. While a batch is unacknowledged, the same recorded
    /// batch is redelivered — delivery is at-least-once.
    ///
    /// # Errors
    ///
    /// Returns [`StreamError::InvalidSource`] when no such subscription is
    /// open under this scope or the subscription is reconnecting,
    /// [`StreamError::Cancelled`] after the consumer's signal is cancelled,
    /// and the typed journal-read error otherwise.
    pub fn next_subscription_batch(
        &self,
        scope: &TenantScope,
        name: &str,
        cancellation: &Cancellation,
    ) -> Result<SubscriptionBatch, StreamError> {
        subscription::next_batch(self.engine, scope, name, cancellation)
    }

    /// Acknowledges one recorded batch boundary and advances the durable
    /// checkpoint.
    ///
    /// Only the acknowledgement seam advances the checkpoint. An
    /// acknowledgement of the currently recorded boundary is idempotent; a
    /// stale or unknown checkpoint is
    /// [`StreamError::InvalidCursor`] and never silently advances anything.
    ///
    /// # Errors
    ///
    /// Returns [`StreamError::InvalidSource`] when no subscription owns the
    /// acknowledged checkpoint under this scope,
    /// [`StreamError::InvalidCursor`] for a stale or unknown checkpoint.
    #[allow(clippy::needless_pass_by_value)] // acknowledging hands the boundary over
    pub fn acknowledge(
        &self,
        scope: &TenantScope,
        acknowledgement: SubscriptionAcknowledgement,
    ) -> Result<SubscriptionStatus, StreamError> {
        subscription::acknowledge(self.engine, scope, &acknowledgement)
    }

    /// Reports the negotiated state of one named subscription.
    ///
    /// # Errors
    ///
    /// Returns [`StreamError::InvalidSource`] when no such subscription
    /// exists under this scope.
    pub fn subscription_status(
        &self,
        scope: &TenantScope,
        name: &str,
    ) -> Result<SubscriptionStatus, StreamError> {
        subscription::status(self.engine, scope, name)
    }

    /// Marks one subscription reconnecting: its consumer went away.
    ///
    /// The core, its durable checkpoint, and any unacknowledged recorded
    /// batch are retained, so a later [`StreamStore::subscribe`] reattaches
    /// and resends exactly the unacknowledged items.
    ///
    /// # Errors
    ///
    /// Returns [`StreamError::InvalidSource`] when no such subscription
    /// exists under this scope.
    pub fn disconnect_subscription(
        &self,
        scope: &TenantScope,
        name: &str,
    ) -> Result<SubscriptionStatus, StreamError> {
        subscription::disconnect(self.engine, scope, name)
    }

    /// Closes one subscription with an explicit terminal status and
    /// releases its state and buffers.
    ///
    /// The close is terminal: a later [`StreamStore::subscribe`] with the
    /// same name opens a fresh subscription with no preserved checkpoint.
    ///
    /// # Errors
    ///
    /// Returns [`StreamError::InvalidSource`] when no such subscription
    /// exists under this scope.
    pub fn close_subscription(
        &self,
        scope: &TenantScope,
        name: &str,
    ) -> Result<SubscriptionStatus, StreamError> {
        subscription::close(self.engine, scope, name)
    }
}

/// The closed set of stream sources.
///
/// Every variant carries typed selectors only: no raw query text, route, or
/// generic bytes crosses this seam. Ordering is per source and part of the
/// public contract (PB-6007..PB-6009, PB-3002, PB-3014).
#[derive(Clone, Debug, PartialEq)]
pub enum StreamSource {
    /// Document stream in canonical key order within the selected collection.
    Documents(DocumentStreamSelector),
    /// Query stream in the compiled plan's ordering, reporting the index
    /// generation it used and honoring the plan's staleness policy.
    Query(QueryPlan),
    /// Time-series stream in timestamp order within the requested range.
    TimeSeries(TimeSeriesStreamRange),
    /// Family stream wrapping the family seam's typed bounded readers so the
    /// same cursor and backpressure rules apply.
    Family(FamilyStreamSelector),
}

/// Typed document-stream selectors: id, prefix, match, and exclusion
/// (PB-6007).
///
/// Selectors are validated before the first batch; an invalid selector is a
/// typed [`StreamError::InvalidSource`] value,
/// never a silently skipped item. The `match` selector is the typed W4 form
/// of V1's match: substring containment on the document identifier. A
/// regex form needs a script or engine decision and is bound by decision
/// D-5.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct DocumentStreamSelector {
    collection: CollectionName,
    id: Option<DocumentId>,
    prefix: Option<String>,
    matching: Option<String>,
    excluded: Vec<DocumentId>,
    deadline: Deadline,
    cancellation: Cancellation,
}

impl DocumentStreamSelector {
    /// Creates a selector over every document of one collection.
    ///
    /// # Errors
    ///
    /// Returns
    /// [`RequestValidationError::InvalidFamilyPayload`](crate::operations::RequestValidationError::InvalidFamilyPayload)
    /// for the reserved family collection,
    /// [`RequestValidationError::InvalidDeadline`](crate::operations::RequestValidationError::InvalidDeadline)
    /// for an elapsed deadline, and
    /// [`RequestValidationError::InvalidCancellation`](crate::operations::RequestValidationError::InvalidCancellation)
    /// for an already cancelled signal.
    pub fn try_new(
        collection: CollectionName,
        deadline: Deadline,
        cancellation: Cancellation,
    ) -> Result<Self, crate::operations::RequestValidationError> {
        sources::validate_document_selector_parts(&collection, &deadline, &cancellation)?;
        Ok(Self {
            collection,
            id: None,
            prefix: None,
            matching: None,
            excluded: Vec::new(),
            deadline,
            cancellation,
        })
    }

    /// Restricts the stream to one exact document identity.
    ///
    /// The addressed document must exist in the snapshot view when the
    /// stream opens; a missing document is a typed
    /// [`StreamError::InvalidSource`] value.
    #[must_use]
    pub fn with_id(mut self, id: DocumentId) -> Self {
        self.id = Some(id);
        self
    }

    /// Restricts the stream to identifiers starting with one prefix.
    ///
    /// # Errors
    ///
    /// Returns
    /// [`RequestValidationError::InvalidIdentifier`](crate::operations::RequestValidationError::InvalidIdentifier)
    /// for an empty prefix, a prefix over 255 bytes, or one containing
    /// control characters.
    pub fn with_prefix(
        self,
        prefix: &str,
    ) -> Result<Self, crate::operations::RequestValidationError> {
        sources::validate_selector_text(prefix)?;
        Ok(Self { prefix: Some(prefix.to_owned()), ..self })
    }

    /// Restricts the stream to identifiers containing one substring.
    ///
    /// # Errors
    ///
    /// Returns
    /// [`RequestValidationError::InvalidIdentifier`](crate::operations::RequestValidationError::InvalidIdentifier)
    /// for an empty pattern, a pattern over 255 bytes, or one containing
    /// control characters.
    pub fn with_match(
        self,
        pattern: &str,
    ) -> Result<Self, crate::operations::RequestValidationError> {
        sources::validate_selector_text(pattern)?;
        Ok(Self { matching: Some(pattern.to_owned()), ..self })
    }

    /// Excludes up to [`MAX_EXCLUDED_IDS`] identities from the stream.
    ///
    /// # Errors
    ///
    /// Returns
    /// [`RequestValidationError::InvalidRange`](crate::operations::RequestValidationError::InvalidRange)
    /// for an empty or over-limit exclusion list.
    pub fn excluding(
        self,
        ids: Vec<DocumentId>,
    ) -> Result<Self, crate::operations::RequestValidationError> {
        if ids.is_empty() || ids.len() > MAX_EXCLUDED_IDS {
            return Err(crate::operations::RequestValidationError::InvalidRange);
        }
        Ok(Self { excluded: ids, ..self })
    }

    /// Returns the selected collection.
    #[must_use]
    pub const fn collection(&self) -> &CollectionName {
        &self.collection
    }

    /// Returns the exact identity selector, when one is set.
    #[must_use]
    pub fn id(&self) -> Option<&DocumentId> {
        self.id.as_ref()
    }

    /// Returns the identifier prefix selector, when one is set.
    #[must_use]
    pub fn prefix(&self) -> Option<&str> {
        self.prefix.as_deref()
    }

    /// Returns the substring match selector, when one is set.
    #[must_use]
    pub fn matching(&self) -> Option<&str> {
        self.matching.as_deref()
    }

    /// Returns the excluded identities.
    #[must_use]
    pub fn excluded(&self) -> &[DocumentId] {
        &self.excluded
    }

    /// Returns the stream deadline.
    #[must_use]
    pub const fn deadline(&self) -> Deadline {
        self.deadline
    }

    /// Returns a clone of the stream cancellation signal.
    #[must_use]
    pub fn cancellation(&self) -> Cancellation {
        self.cancellation.clone()
    }
}

/// The requested time-series range (PB-6009).
///
/// Entries stream in timestamp order within the range, preserving tag and
/// value shape. Rollups, deleted, and empty ranges keep the family seam's
/// observable behavior; W4-STREAM streams the raw entry view the family
/// reader serves.
#[derive(Clone, Debug, PartialEq)]
pub struct TimeSeriesStreamRange {
    document: DocumentId,
    name: String,
    range: Option<TimeSeriesRange>,
    deadline: Deadline,
    cancellation: Cancellation,
}

impl TimeSeriesStreamRange {
    /// Creates a validated time-series stream source.
    ///
    /// # Errors
    ///
    /// Returns
    /// [`RequestValidationError::InvalidFamilyPayload`](crate::operations::RequestValidationError::InvalidFamilyPayload)
    /// for an invalid series name,
    /// [`RequestValidationError::InvalidDeadline`](crate::operations::RequestValidationError::InvalidDeadline)
    /// for an elapsed deadline, and
    /// [`RequestValidationError::InvalidCancellation`](crate::operations::RequestValidationError::InvalidCancellation)
    /// for an already cancelled signal.
    pub fn try_new(
        document: DocumentId,
        name: String,
        range: Option<TimeSeriesRange>,
        deadline: Deadline,
        cancellation: Cancellation,
    ) -> Result<Self, crate::operations::RequestValidationError> {
        sources::validate_series_stream_name(&name)?;
        if deadline.is_expired() {
            return Err(crate::operations::RequestValidationError::InvalidDeadline);
        }
        if cancellation.is_cancelled() {
            return Err(crate::operations::RequestValidationError::InvalidCancellation);
        }
        Ok(Self { document, name, range, deadline, cancellation })
    }

    /// Returns the owning document identity.
    #[must_use]
    pub const fn document(&self) -> &DocumentId {
        &self.document
    }

    /// Returns the series name.
    #[must_use]
    pub fn name(&self) -> &str {
        &self.name
    }

    /// Returns the requested inclusive range, or `None` for the whole series.
    #[must_use]
    pub const fn range(&self) -> Option<TimeSeriesRange> {
        self.range
    }

    /// Returns the stream deadline.
    #[must_use]
    pub const fn deadline(&self) -> Deadline {
        self.deadline
    }

    /// Returns a clone of the stream cancellation signal.
    #[must_use]
    pub fn cancellation(&self) -> Cancellation {
        self.cancellation.clone()
    }
}

/// The time-series range is two ordered integers, so its stream source is
/// exactly equality-comparable even though the family payload does not
/// derive `Eq`.
impl Eq for TimeSeriesStreamRange {}

/// Family-stream selection over the family seam's typed bounded readers
/// (PB-3002, PB-3014).
///
/// The selector wraps one closed
/// [`FamilyReadRequest`](crate::families::FamilyReadRequest); the stream
/// reads it once through
/// [`FamilyStore::read`](crate::families::FamilyStore::read) and applies
/// the same cursor and completion rules as every other source.
#[derive(Clone, Debug)]
pub struct FamilyStreamSelector {
    request: crate::families::FamilyReadRequest,
}

impl FamilyStreamSelector {
    /// Wraps one validated family read as a stream source.
    ///
    /// # Errors
    ///
    /// Returns
    /// [`RequestValidationError::InvalidDeadline`](crate::operations::RequestValidationError::InvalidDeadline)
    /// for an elapsed deadline and
    /// [`RequestValidationError::InvalidCancellation`](crate::operations::RequestValidationError::InvalidCancellation)
    /// for an already cancelled signal.
    pub fn try_new(
        request: crate::families::FamilyReadRequest,
    ) -> Result<Self, crate::operations::RequestValidationError> {
        if request.deadline().is_expired() {
            return Err(crate::operations::RequestValidationError::InvalidDeadline);
        }
        if request.cancellation().is_cancelled() {
            return Err(crate::operations::RequestValidationError::InvalidCancellation);
        }
        Ok(Self { request })
    }

    /// Returns the wrapped family read request.
    #[must_use]
    pub const fn request(&self) -> &crate::families::FamilyReadRequest {
        &self.request
    }
}

/// Two family selectors are equal exactly when they select the same read
/// structurally. Deadlines, cancellation signals, and floating-point entry
/// values do not take part, so cloning a selector — which re-instantiates
/// its signal — still compares equal to itself and cursor binding stays
/// stable.
impl PartialEq for FamilyStreamSelector {
    fn eq(&self, other: &Self) -> bool {
        sources::family_request_token(&self.request)
            == sources::family_request_token(&other.request)
    }
}

/// Structural equality over integer, boolean, and text fields only.
impl Eq for FamilyStreamSelector {}

/// An opaque, resumable stream position bound to one stream source and one
/// `Engine` identity.
///
/// A cursor resumes exactly where the previous batch ended. A cursor from
/// another source, `Engine`, or snapshot is a typed
/// [`StreamError::InvalidCursor`] value. Cursors are comparable for
/// equality only; no ordering is defined.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct StreamCursor {
    engine: u128,
    snapshot: StorageSequence,
    kind: StreamSourceKind,
    token: String,
    position: sources::CursorPosition,
}

/// One bounded batch of streamed items plus the next cursor.
///
/// The batch carries its items, the cursor that resumes exactly after them,
/// and — exactly once at the end of the stream — the terminal
/// [`StreamCompletion`]. The batch limit is part of the request, not the
/// batch. Query streams additionally report the index generation their
/// execution used, faithfully `None` for an index-less execution.
#[derive(Clone, Debug, PartialEq)]
pub struct StreamBatch {
    items: Vec<StreamItem>,
    next_cursor: StreamCursor,
    completion: Option<StreamCompletion>,
    index_generation: Option<crate::query::IndexGenerationRef>,
}

impl StreamBatch {
    /// Returns the batch items, at most the requested limit.
    #[must_use]
    pub fn items(&self) -> &[StreamItem] {
        &self.items
    }

    /// Returns the cursor that resumes exactly after this batch.
    #[must_use]
    pub const fn next_cursor(&self) -> &StreamCursor {
        &self.next_cursor
    }

    /// Returns the terminal observation, present exactly once at the end of
    /// the stream.
    #[must_use]
    pub const fn completion(&self) -> Option<StreamCompletion> {
        self.completion
    }

    /// Returns the index generation the query execution used, or `None` for
    /// an index-less execution or a non-query source.
    ///
    /// The value names the generation the staleness gate accepted; it does
    /// not claim index data served the query (the W3-2 execution path is
    /// index-less).
    #[must_use]
    pub fn index_generation(&self) -> Option<&crate::query::IndexGenerationRef> {
        self.index_generation.as_ref()
    }
}

/// One streamed item.
///
/// Closed enum over documents, query rows, time-series entries, and family
/// values; no storage handle crosses this seam.
#[derive(Clone, Debug, PartialEq)]
pub enum StreamItem {
    /// One document from a document stream.
    Document(Document),
    /// One typed result row from a query stream.
    QueryRow(QueryRow),
    /// One time-series entry from a time-series stream.
    TimeSeries(TimeSeriesEntry),
    /// One typed family value from a family stream.
    Family(FamilyReadResult),
}

/// The terminal observation of a stream, reported exactly once at the end.
///
/// `EndOfStream`, `Cancelled`, `Failed`, and `SlowConsumer` are distinct
/// terminal observations; none is silently collapsed into another.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum StreamCompletion {
    /// The source is exhausted.
    EndOfStream,
    /// The stream was cancelled; buffers are released promptly.
    Cancelled,
    /// The stream failed with a typed error.
    Failed(StreamError),
    /// The consumer was slower than production and a buffer bound was
    /// reached; the stream reports it instead of growing without bound.
    SlowConsumer,
}

/// Typed stream failure categories.
///
/// No path, payload, or transport detail crosses this seam; missing
/// documents, invalid selectors, and malformed continuation cursors are typed
/// values, never silently skipped items.
///
/// The frozen variant set has no storage-failure case: an unreachable or
/// poisoned Engine storage lock is reported as [`StreamError::EngineClosed`],
/// and a family or query rejection is reported as
/// [`StreamError::InvalidSource`]. Both mappings are recorded as limits in
/// the design record.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum StreamError {
    /// The `Engine` has closed.
    EngineClosed,
    /// The snapshot belongs to another `Engine`.
    SnapshotEngineMismatch,
    /// The source and snapshot or tenant do not match.
    TenantMismatch,
    /// The stream source or its selectors are invalid.
    InvalidSource,
    /// The continuation cursor does not bind to this source and `Engine`.
    InvalidCursor,
    /// A blocking call could not proceed within its buffer bound because the
    /// consumer is slow.
    SlowConsumer,
    /// The request deadline elapsed before the operation ran.
    Deadline,
    /// The operation was cancelled.
    Cancelled,
}

/// The public observation of a cursor: storage sequence and source kind,
/// without internal offsets.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct StreamPosition {
    kind: StreamSourceKind,
    sequence: StorageSequence,
}

impl StreamPosition {
    /// Returns the kind of the observed cursor's source.
    #[must_use]
    pub const fn kind(&self) -> StreamSourceKind {
        self.kind
    }

    /// Returns the storage sequence the stream is bound to — its snapshot's
    /// publication boundary.
    #[must_use]
    pub const fn sequence(&self) -> StorageSequence {
        self.sequence
    }
}

/// One committed change observed by a change or subscription stream
/// (PB-6002).
///
/// The event carries the change kind, tenant scope, collection and document
/// identity, the stable `OperationId`, and the `StorageSequence`. It stays
/// small: content-bearing payloads are fetched separately, and the event is
/// observable only after its commit is published.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ChangeEvent {
    kind: ChangeKind,
    scope: TenantScope,
    collection: CollectionName,
    document: DocumentId,
    operation_id: OperationId,
    sequence: StorageSequence,
}

impl ChangeEvent {
    /// Creates one event from published facts. Crate-only: change events
    /// originate from published commits alone.
    pub(crate) fn new(
        kind: ChangeKind,
        scope: TenantScope,
        collection: CollectionName,
        document: DocumentId,
        operation_id: OperationId,
        sequence: StorageSequence,
    ) -> Self {
        Self { kind, scope, collection, document, operation_id, sequence }
    }

    /// Returns the closed change kind.
    #[must_use]
    pub const fn kind(&self) -> ChangeKind {
        self.kind
    }

    /// Returns the owning tenant scope.
    #[must_use]
    pub const fn scope(&self) -> &TenantScope {
        &self.scope
    }

    /// Returns the collection of the changed key.
    #[must_use]
    pub const fn collection(&self) -> &CollectionName {
        &self.collection
    }

    /// Returns the changed document identity.
    #[must_use]
    pub const fn document(&self) -> &DocumentId {
        &self.document
    }

    /// Returns the stable operation identity of the commit.
    #[must_use]
    pub const fn operation_id(&self) -> &OperationId {
        &self.operation_id
    }

    /// Returns the storage sequence of the publication.
    #[must_use]
    pub const fn sequence(&self) -> StorageSequence {
        self.sequence
    }
}

/// The closed V1 event-shape set for change streams (PB-6002).
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ChangeKind {
    /// A document change.
    Document,
    /// An index change.
    Index,
    /// A counter change.
    Counter,
    /// A time-series change.
    TimeSeries,
    /// An operation-level change.
    Operation,
    /// A database-level change.
    Database,
}

/// Typed document, index, and family filters for change streams (PB-6006).
///
/// W4 implements typed filters only; no untrusted script executes in this
/// seam (decision D-5). A filter drop is reported in the batch's progress
/// facts and never silently advances a checkpoint.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ChangeFilter {
    kinds: Option<Vec<ChangeKind>>,
    collections: Option<Vec<CollectionName>>,
}

impl ChangeFilter {
    /// Creates a filter from optional include lists.
    ///
    /// `None` accepts every kind or collection. `Some` with an empty list is
    /// rejected: a filter that accepts nothing is an invalid request, not a
    /// match-nothing subscription. Duplicate entries are accepted and
    /// idempotent.
    ///
    /// # Errors
    ///
    /// Returns
    /// [`RequestValidationError::EmptyField`](crate::operations::RequestValidationError::EmptyField)
    /// for an empty include list.
    pub fn try_new(
        kinds: Option<Vec<ChangeKind>>,
        collections: Option<Vec<CollectionName>>,
    ) -> Result<Self, crate::operations::RequestValidationError> {
        if kinds.as_ref().is_some_and(Vec::is_empty) {
            return Err(crate::operations::RequestValidationError::EmptyField);
        }
        if collections.as_ref().is_some_and(Vec::is_empty) {
            return Err(crate::operations::RequestValidationError::EmptyField);
        }
        Ok(Self { kinds, collections })
    }

    /// Returns the included kinds, or `None` for every kind.
    #[must_use]
    pub fn kinds(&self) -> Option<&[ChangeKind]> {
        self.kinds.as_deref()
    }

    /// Returns the included collections, or `None` for every collection.
    #[must_use]
    pub fn collections(&self) -> Option<&[CollectionName]> {
        self.collections.as_deref()
    }

    /// Reports whether one event passes this filter.
    #[must_use]
    pub fn accepts(&self, kind: ChangeKind, collection: &CollectionName) -> bool {
        self.kinds.as_ref().is_none_or(|kinds| kinds.contains(&kind))
            && self.collections.as_ref().is_none_or(|collections| collections.contains(collection))
    }
}

/// A named subscription request (PB-6003).
///
/// Carries the subscription identity, its filter, and the batch bound. A
/// subscription opens once, then delivers bounded batches that are
/// acknowledged at the checkpoint seam. By default delivery starts from the
/// journal's durable high-water mark; [`SubscriptionSpec::from_beginning`]
/// starts from the oldest servable publication history instead.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct SubscriptionSpec {
    name: String,
    filter: ChangeFilter,
    max_events: u32,
    from_beginning: bool,
}

impl SubscriptionSpec {
    /// Creates a validated subscription request.
    ///
    /// # Errors
    ///
    /// Returns
    /// [`RequestValidationError::EmptyField`](crate::operations::RequestValidationError::EmptyField)
    /// for an empty name,
    /// [`RequestValidationError::InvalidIdentifier`](crate::operations::RequestValidationError::InvalidIdentifier)
    /// for an invalid name, and
    /// [`RequestValidationError::InvalidRange`](crate::operations::RequestValidationError::InvalidRange)
    /// for a zero or over-limit batch bound.
    pub fn try_new(
        name: &str,
        filter: ChangeFilter,
        max_events: u32,
    ) -> Result<Self, crate::operations::RequestValidationError> {
        subscription::validate_spec_name(name)?;
        if max_events == 0 || max_events > MAX_SUBSCRIPTION_BATCH_ITEMS {
            return Err(crate::operations::RequestValidationError::InvalidRange);
        }
        Ok(Self { name: name.to_owned(), filter, max_events, from_beginning: false })
    }

    /// Starts delivery from the oldest servable publication history instead
    /// of the journal's durable high-water mark.
    #[must_use]
    pub fn from_beginning(mut self) -> Self {
        self.from_beginning = true;
        self
    }

    /// Returns the subscription name.
    #[must_use]
    pub fn name(&self) -> &str {
        &self.name
    }

    /// Returns the typed change filter.
    #[must_use]
    pub const fn filter(&self) -> &ChangeFilter {
        &self.filter
    }

    /// Returns the subscription batch bound.
    #[must_use]
    pub const fn max_events(&self) -> u32 {
        self.max_events
    }

    /// Reports whether delivery starts from the servable beginning.
    pub(crate) const fn starts_from_beginning(&self) -> bool {
        self.from_beginning
    }
}

/// One bounded subscription batch with its `SubscriptionCheckpoint`
/// (PB-6003, PB-6004).
///
/// The durable subscription checkpoint advances only when the caller
/// acknowledges the batch; unacknowledged items enter resend state and a
/// reconnect neither skips them nor advances the checkpoint without the
/// acknowledgement. Filtered drops and dropped notifications are progress
/// facts, not deliveries.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct SubscriptionBatch {
    events: Vec<ChangeEvent>,
    checkpoint: SubscriptionCheckpoint,
    filtered_dropped: u64,
    dropped_notifications: u64,
}

impl SubscriptionBatch {
    /// Assembles one batch from its typed parts. Crate-only.
    pub(crate) fn new(
        events: Vec<ChangeEvent>,
        checkpoint: SubscriptionCheckpoint,
        filtered_dropped: u64,
        dropped_notifications: u64,
    ) -> Self {
        Self { events, checkpoint, filtered_dropped, dropped_notifications }
    }

    /// Returns the delivered events, at most the spec's batch bound.
    #[must_use]
    pub fn events(&self) -> &[ChangeEvent] {
        &self.events
    }

    /// Returns the recorded boundary this batch asks the caller to
    /// acknowledge.
    #[must_use]
    pub fn checkpoint(&self) -> SubscriptionCheckpoint {
        self.checkpoint.clone()
    }

    /// Returns the number of events the filter dropped while forming this
    /// batch at or below its recorded boundary.
    #[must_use]
    pub const fn filtered_dropped(&self) -> u64 {
        self.filtered_dropped
    }

    /// Returns the cumulative number of publication notifications the
    /// bounded notification buffer dropped for this subscription.
    #[must_use]
    pub const fn dropped_notifications(&self) -> u64 {
        self.dropped_notifications
    }
}

/// An opaque durable subscription position.
///
/// The checkpoint advances only at the acknowledgement seam; it survives
/// reconnect, and exactly one active consumer owns a subscription at a time.
/// It stays opaque: equality is its only public operation.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct SubscriptionCheckpoint {
    pub(crate) inner: subscription::CheckpointInner,
}

/// The caller's acknowledgement of one subscription batch.
///
/// Carries the `SubscriptionCheckpoint` it confirms; acknowledge failures,
/// changed subscription state, and concurrent updates are typed errors that
/// never silently advance the checkpoint.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct SubscriptionAcknowledgement {
    confirmed: SubscriptionCheckpoint,
}

impl SubscriptionAcknowledgement {
    /// Acknowledges exactly the recorded boundary one batch carried.
    #[must_use]
    pub const fn confirming(checkpoint: SubscriptionCheckpoint) -> Self {
        Self { confirmed: checkpoint }
    }

    /// Returns the checkpoint this acknowledgement confirms.
    #[must_use]
    pub const fn confirmed(&self) -> &SubscriptionCheckpoint {
        &self.confirmed
    }
}

/// The negotiated state of one subscription (PB-6005).
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum SubscriptionStatus {
    /// The subscription is being opened.
    Opening,
    /// The subscription is delivering batches.
    Active,
    /// A batch is delivered and the durable checkpoint waits for the caller's
    /// acknowledgement.
    AwaitingAcknowledgement,
    /// The subscription is reconnecting; unacknowledged items enter resend
    /// state.
    Reconnecting,
    /// The subscription closed with this explicit terminal reason.
    Closed(StreamCompletion),
}
