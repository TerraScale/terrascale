//! Declaration-only streaming seam: bounded, ordered, cancellable streams.
//!
//! This module declares the streaming vocabulary of the W4 design contract
//! (`docs/design/w4-transfer-replication-contract.md`, "Public type
//! ownership"). No streaming behavior exists here: batching, ordering, cursor
//! resume, subscriptions, backpressure, and cancellation are implemented by
//! the W4-STREAM packet, and the production acquisition path
//! `Engine::streams()` is added by integrator action W4-CA-2.
//!
//! Contract facts the declarations state: streams read only immutable
//! snapshot state and already-published commit facts, never a live
//! transaction; every buffer is bounded and a slow consumer is reported
//! instead of buffered without bound; delivery is at-least-once with stable
//! operation identity; and no path, payload, transport detail, or storage
//! handle crosses this seam.

#![allow(dead_code)]

use std::marker::PhantomData;

use crate::{
    engine::Engine,
    families::{FamilyReadResult, TimeSeriesEntry},
    query::{QueryPlan, QueryRow},
    types::{CollectionName, Document, DocumentId, OperationId, StorageSequence, TenantScope},
};

/// A streaming store borrowed from one durable `Engine`.
///
/// Contract facts; the behavior belongs to W4-STREAM:
///
/// - The only production acquisition path is `Engine::streams()`, added by
///   integrator action W4-CA-2. No independent durable or in-memory production
///   constructor exists, mirroring `QueryStore` and `FamilyStore`.
/// - One batch contract governs every source: a batch is bounded by the
///   caller's item limit, ends with the next `StreamCursor`, and a terminal
///   `StreamCompletion` is reported exactly once at the end of the stream.
/// - Streams read only immutable snapshot state and already-published commit
///   facts. A live, uncommitted transaction is never observable through any
///   stream.
/// - Buffers are bounded: a slow consumer is reported as
///   `StreamCompletion::SlowConsumer` or `StreamError::SlowConsumer`, never
///   buffered without bound, and never blocks an engine commit.
pub struct StreamStore<'engine> {
    engine: &'engine Engine,
}

impl<'engine> StreamStore<'engine> {
    /// Binds one store to one `Engine`. Crate-only: the durable `Engine`
    /// through `Engine::streams()` is the only production acquisition path.
    ///
    /// # Panics
    ///
    /// Always: streaming behavior belongs to W4-STREAM.
    pub(crate) fn from_engine(engine: &'engine Engine) -> Self {
        let _ = engine;
        todo!("w4 implementation packet")
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
/// typed `StreamError::InvalidSource` value, never a silently skipped item.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct DocumentStreamSelector {
    selectors: PhantomData<()>,
}

/// The requested time-series range (PB-6009).
///
/// Entries stream in timestamp order within the range, preserving tag and
/// value shape, rollups, and deleted and empty ranges.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct TimeSeriesStreamRange {
    range: PhantomData<()>,
}

/// Family-stream selection over the family seam's typed bounded readers
/// (PB-3002, PB-3014).
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct FamilyStreamSelector {
    selection: PhantomData<()>,
}

/// An opaque, resumable stream position bound to one stream source and one
/// `Engine` identity.
///
/// A cursor resumes exactly where the previous batch ended. A cursor from
/// another source or `Engine` is a typed `StreamError::InvalidCursor` value.
/// Cursors are comparable for equality only; no ordering is defined.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct StreamCursor {
    position: PhantomData<()>,
}

/// One bounded batch of streamed items plus the next cursor.
///
/// The batch carries its items, the cursor that resumes exactly after them,
/// and — exactly once at the end of the stream — the terminal
/// `StreamCompletion`. The batch limit is part of the request, not the batch.
#[derive(Clone, Debug, PartialEq)]
pub struct StreamBatch {
    items: Vec<StreamItem>,
    next_cursor: StreamCursor,
    completion: Option<StreamCompletion>,
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
    observation: PhantomData<()>,
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
/// W4 implements typed filters only; no untrusted script executes in this seam
/// (decision D-5). A filter drop is reported in the batch's progress facts
/// and never silently advances a checkpoint.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ChangeFilter {
    filters: PhantomData<()>,
}

/// A named subscription request (PB-6003).
///
/// Carries the subscription identity, its source and filters, the batch bound,
/// and the acknowledgement policy. A subscription opens once, then delivers
/// bounded batches that are acknowledged at the checkpoint seam.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct SubscriptionSpec {
    spec: PhantomData<()>,
}

/// One bounded subscription batch with its `SubscriptionCheckpoint`
/// (PB-6003, PB-6004).
///
/// The durable subscription checkpoint advances only when the caller
/// acknowledges the batch; unacknowledged items enter resend state and a
/// reconnect neither skips them nor advances the checkpoint without the
/// acknowledgement.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct SubscriptionBatch {
    batch: PhantomData<()>,
}

/// An opaque durable subscription position.
///
/// The checkpoint advances only at the acknowledgement seam; it survives
/// reconnect and cross-member failover, and exactly one active consumer owns
/// a subscription at a time.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct SubscriptionCheckpoint {
    checkpoint: PhantomData<()>,
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
