//! Snapshot-bound stream sources: cursors, selectors, and batch reads.
//!
//! Every source reads through an existing engine seam — the W4-CA-7
//! windowed snapshot read for documents, the public query path for plans,
//! the family seam's typed readers for time-series and family values — and
//! mints one opaque [`StreamCursor`](super::StreamCursor) that binds the
//! engine identity, the source's structural token, and the snapshot the
//! stream was opened against. Streaming code never begins an Engine
//! transaction: the never-committed read rule.

use crate::{
    engine::{Engine, EngineError},
    families::{
        CompareExchangeRead, CounterRead, FamilyError, FamilyReadContext, FamilyReadRequest,
        IdentityRead, TimeSeriesRead,
    },
    operations::RequestValidationError,
    query::{QueryError, QueryPlan},
    types::{
        Cancellation, CollectionName, Deadline, Document, DocumentId, ReadSnapshot,
        StorageSequence, TenantScope,
    },
};

use super::{
    DocumentStreamSelector, FamilyStreamSelector, StreamCompletion, StreamCursor, StreamError,
    StreamItem, StreamSource, TimeSeriesStreamRange,
};

/// The kind of one stream source, the public observation a cursor exposes.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum StreamSourceKind {
    /// A document stream in canonical key order.
    Documents,
    /// A query stream in the compiled plan's order.
    Query,
    /// A time-series stream in timestamp order.
    TimeSeries,
    /// A family stream through the family seam's readers.
    Family,
}

/// The reserved family collection, mirrored from the W3 family contract.
///
/// The constant itself lives in the family-private state module; the query
/// seam mirrors it the same way (`src/query/spec.rs`). A document stream
/// may not select it: family values are reachable only through the family
/// stream source.
const RESERVED_FAMILY_COLLECTION: &str = "terrabase.families";

/// How many bounded window reads one document batch may spend before
/// returning a short batch.
///
/// Canonical key order clusters a collection's documents, so one window
/// usually crosses into the selected collection; the bound keeps a batch's
/// work bounded when it does not. A tracer-scale limit, not a contract one.
const MAX_WINDOW_READS_PER_BATCH: usize = 64;

/// The private, per-source position one cursor carries.
#[derive(Clone, Debug, Eq, PartialEq)]
pub(crate) enum CursorPosition {
    /// Resume after this document identity; `exhausted` is terminal.
    Documents { after: Option<DocumentId>, exhausted: bool },
    /// Resume at this row offset within the plan's result window.
    Query { offset: u64, exhausted: bool },
    /// Resume at this entry offset within the family page.
    TimeSeries { offset: u64, exhausted: bool },
    /// The single family read has been served.
    Family { exhausted: bool },
}

/// The binding every cursor of one live stream shares.
struct CursorBinding {
    engine: u128,
    snapshot: StorageSequence,
    kind: StreamSourceKind,
    token: String,
}

impl CursorBinding {
    /// Mints one cursor of this binding at the given position.
    fn cursor(&self, position: CursorPosition) -> StreamCursor {
        StreamCursor {
            engine: self.engine,
            snapshot: self.snapshot,
            kind: self.kind,
            token: self.token.clone(),
            position,
        }
    }
}

/// Validates one selector text: non-empty, bounded, no control characters.
pub(crate) fn validate_selector_text(value: &str) -> Result<(), RequestValidationError> {
    if value.is_empty() || value.len() > 255 || value.chars().any(char::is_control) {
        return Err(RequestValidationError::InvalidIdentifier);
    }
    Ok(())
}

/// Validates a time-series stream's series name under the family seam's
/// series-name rules.
pub(crate) fn validate_series_stream_name(value: &str) -> Result<(), RequestValidationError> {
    if value.is_empty()
        || value.len() > 255
        || value.chars().any(char::is_control)
        || value != value.trim()
        || value.contains('/')
    {
        return Err(RequestValidationError::InvalidFamilyPayload);
    }
    Ok(())
}

/// Validates the non-selector parts of a document selector.
pub(crate) fn validate_document_selector_parts(
    collection: &CollectionName,
    deadline: &Deadline,
    cancellation: &Cancellation,
) -> Result<(), RequestValidationError> {
    if collection.as_str() == RESERVED_FAMILY_COLLECTION {
        // The reserved family collection is reachable only through the
        // family stream source.
        return Err(RequestValidationError::InvalidFamilyPayload);
    }
    if deadline.is_expired() {
        return Err(RequestValidationError::InvalidDeadline);
    }
    if cancellation.is_cancelled() {
        return Err(RequestValidationError::InvalidCancellation);
    }
    Ok(())
}

/// Maps an Engine scope/snapshot failure onto the typed stream errors.
#[allow(clippy::match_same_arms, reason = "one frozen mapping table names every engine case")]
pub(crate) fn map_engine_error(error: &EngineError) -> StreamError {
    match error {
        EngineError::SnapshotEngineMismatch | EngineError::ScopeMismatch => {
            StreamError::SnapshotEngineMismatch
        }
        EngineError::SnapshotTenantMismatch => StreamError::TenantMismatch,
        EngineError::Closed(_) => StreamError::EngineClosed,
        // The frozen stream error set has no storage-failure case; an
        // unusable storage lock means the engine cannot serve the stream.
        EngineError::StorageFailure
        | EngineError::SnapshotPageLimitExceeded
        | EngineError::SnapshotBoundaryUnavailable
        | EngineError::Conflict
        | EngineError::Deadline
        | EngineError::Cancelled
        | EngineError::InvalidDocument
        | EngineError::OperationIdReuse { .. } => StreamError::EngineClosed,
    }
}

/// Maps a query failure onto the typed stream errors.
#[allow(clippy::match_same_arms, reason = "one frozen mapping table names every query case")]
fn map_query_error(error: QueryError) -> StreamError {
    match error {
        QueryError::EngineClosed => StreamError::EngineClosed,
        QueryError::SnapshotEngineMismatch => StreamError::SnapshotEngineMismatch,
        QueryError::SnapshotTenantMismatch | QueryError::TenantMismatch => {
            StreamError::TenantMismatch
        }
        // A rejected spec or an unmet staleness policy cannot serve this
        // source at this snapshot.
        QueryError::InvalidSpec | QueryError::IndexNotReady => StreamError::InvalidSource,
        QueryError::Deadline => StreamError::Deadline,
        QueryError::Cancelled => StreamError::Cancelled,
        // Both include refusals are typed rejections of the plan's own
        // include set: a reference cycle reachable within the declared
        // depth, or a set that would pass the include document bound. The
        // stream reports them the way it reports any other source the plan
        // names and the seam refuses.
        QueryError::IncludeCycle | QueryError::IncludeBoundExceeded => StreamError::InvalidSource,
        QueryError::StorageFailure => StreamError::EngineClosed,
    }
}

/// Maps a family failure onto the typed stream errors.
fn map_family_error(error: FamilyError) -> StreamError {
    match error {
        FamilyError::EngineClosed => StreamError::EngineClosed,
        FamilyError::TenantMismatch => StreamError::TenantMismatch,
        FamilyError::SnapshotEngineMismatch | FamilyError::TransactionEngineMismatch => {
            StreamError::SnapshotEngineMismatch
        }
        FamilyError::DeadlineExceeded => StreamError::Deadline,
        FamilyError::Cancelled => StreamError::Cancelled,
        // A missing document or a rejected family rule means the addressed
        // source cannot be served; the frozen set has no closer case.
        FamilyError::DocumentMissing
        | FamilyError::InvalidRequest
        | FamilyError::Conflict
        | FamilyError::CounterOverflow
        | FamilyError::TimeSeriesArityInvalid
        | FamilyError::TimeSeriesRangeInvalid
        | FamilyError::TimeSeriesPolicyInvalid
        | FamilyError::RevisionReferenceInvalid
        | FamilyError::ValidationPolicyMalformed
        | FamilyError::SchemaViolation
        | FamilyError::AttachmentNameDuplicate
        | FamilyError::LifecycleValueInvalid
        | FamilyError::CompareExchangeIndexStale
        | FamilyError::IdentitySeedInvalid
        | FamilyError::IdentityExhausted
        | FamilyError::TombstoneConflict
        | FamilyError::TransactionIdentityMismatch => StreamError::InvalidSource,
        FamilyError::StorageFailure | FamilyError::ReservedStateInvalid => {
            StreamError::EngineClosed
        }
    }
}

/// Builds the structural token one source binds its cursors to.
///
/// The token covers every structural field of the source and deliberately
/// excludes deadlines and cancellation signals, whose clone and identity
/// semantics must not break cursor resume. It is a binding, not a digest
/// claim.
fn source_token(source: &StreamSource) -> String {
    match source {
        StreamSource::Documents(selector) => format!(
            "documents|{}|{:?}|{:?}|{:?}|{:?}",
            selector.collection().as_str(),
            selector.id(),
            selector.prefix(),
            selector.matching(),
            selector.excluded(),
        ),
        StreamSource::Query(plan) => {
            let model = plan.model();
            format!(
                "query|{:?}|{:?}|{:?}|{:?}|{:?}|{:?}|{:?}",
                model.source(),
                model.predicate(),
                model.ordering(),
                model.projection(),
                model.paging(),
                model.staleness(),
                model.tenant_claim(),
            )
        }
        StreamSource::TimeSeries(range) => format!(
            "time-series|{}|{}|{:?}",
            range.document().as_str(),
            range.name(),
            range.range(),
        ),
        StreamSource::Family(selector) => {
            format!("family|{}", family_request_token(selector.request()))
        }
    }
}

/// Summarizes one family read request structurally, excluding its deadline
/// and cancellation signal.
pub(crate) fn family_request_token(request: &FamilyReadRequest) -> String {
    match request {
        FamilyReadRequest::AttachmentMetadata(read) => {
            format!("attachment-metadata|{}", read.document().as_str())
        }
        FamilyReadRequest::CounterGroup(read) => {
            format!("counter-group|{}|{:?}", read.document().as_str(), read_counter_names(read))
        }
        FamilyReadRequest::TimeSeriesRange(read) => format!(
            "time-series-range|{}|{}|{:?}",
            read.document().as_str(),
            read.name(),
            read.range(),
        ),
        FamilyReadRequest::RevisionPage(read) => format!(
            "revision-page|{}|{}|{}",
            read.document().as_str(),
            read.paging().offset(),
            read.paging().limit(),
        ),
        FamilyReadRequest::CompareExchangeValue(read) => {
            format!("compare-exchange|{}", read_compare_exchange_key(read))
        }
        FamilyReadRequest::IdentityRange(read) => {
            format!("identity|{:?}", read_identity_names(read))
        }
        FamilyReadRequest::Lifecycle(read) => format!("lifecycle|{}", read.document().as_str()),
        FamilyReadRequest::Archival(read) => format!("archival|{}", read.document().as_str()),
        FamilyReadRequest::Validation(read) => format!(
            "validation|{}|{:?}",
            read.collection().as_str(),
            read.document().map(DocumentId::as_str),
        ),
        FamilyReadRequest::Tombstones(read) => format!("tombstones|{}", read.document().as_str()),
    }
}

/// Reads one counter request's names through its public accessor.
fn read_counter_names(read: &CounterRead) -> Vec<String> {
    read.names().to_vec()
}

/// Reads one compare-exchange request's key through its public accessor.
fn read_compare_exchange_key(read: &CompareExchangeRead) -> &str {
    read.key()
}

/// Reads one identity request's names through its public accessor.
fn read_identity_names(read: &IdentityRead) -> &[String] {
    read.names()
}

/// Validates the scope and snapshot before a cursor is minted or served.
fn validate_context(
    engine: &Engine,
    scope: &TenantScope,
    snapshot: &ReadSnapshot,
) -> Result<(), StreamError> {
    // The probe validates the scope against this Engine without touching
    // publication state.
    engine.read_snapshot(scope).map_err(|error| map_engine_error(&error))?;
    if snapshot.engine_identity() != scope.engine_identity() {
        return Err(StreamError::SnapshotEngineMismatch);
    }
    if snapshot.tenant() != scope.tenant() {
        return Err(StreamError::TenantMismatch);
    }
    Ok(())
}

/// Opens one stream: validates the source and mints the start cursor.
pub(crate) fn open(
    engine: &Engine,
    scope: &TenantScope,
    snapshot: &ReadSnapshot,
    source: &StreamSource,
) -> Result<StreamCursor, StreamError> {
    validate_context(engine, scope, snapshot)?;
    let binding = CursorBinding {
        engine: scope.engine_identity(),
        snapshot: snapshot.sequence(),
        kind: kind_of(source),
        token: source_token(source),
    };
    let position = match &source {
        StreamSource::Documents(selector) => {
            check_document_source(engine, scope, snapshot, selector)?;
            CursorPosition::Documents { after: None, exhausted: false }
        }
        StreamSource::Query(plan) => {
            check_query_source(plan)?;
            CursorPosition::Query { offset: 0, exhausted: false }
        }
        StreamSource::TimeSeries(range) => {
            check_signal(&range.deadline(), &range.cancellation())?;
            CursorPosition::TimeSeries { offset: 0, exhausted: false }
        }
        StreamSource::Family(selector) => {
            check_signal(&selector.request().deadline(), &selector.request().cancellation())?;
            CursorPosition::Family { exhausted: false }
        }
    };
    Ok(binding.cursor(position))
}

/// Returns the source kind of one stream source.
fn kind_of(source: &StreamSource) -> StreamSourceKind {
    match source {
        StreamSource::Documents(_) => StreamSourceKind::Documents,
        StreamSource::Query(_) => StreamSourceKind::Query,
        StreamSource::TimeSeries(_) => StreamSourceKind::TimeSeries,
        StreamSource::Family(_) => StreamSourceKind::Family,
    }
}

/// Checks one source-carried deadline and cancellation signal.
fn check_signal(deadline: &Deadline, cancellation: &Cancellation) -> Result<(), StreamError> {
    if deadline.is_expired() {
        return Err(StreamError::Deadline);
    }
    if cancellation.is_cancelled() {
        return Err(StreamError::Cancelled);
    }
    Ok(())
}

/// Validates one document selector against the snapshot view.
fn check_document_source(
    engine: &Engine,
    scope: &TenantScope,
    snapshot: &ReadSnapshot,
    selector: &DocumentStreamSelector,
) -> Result<(), StreamError> {
    check_signal(&selector.deadline(), &selector.cancellation())?;
    // An exact-identity selector is validated before the first batch: the
    // addressed document must exist in this snapshot view.
    if let Some(id) = selector.id()
        && !document_exists(engine, scope, snapshot, selector.collection(), id)?
    {
        return Err(StreamError::InvalidSource);
    }
    Ok(())
}

/// Reports whether one exact document identity exists in the snapshot view.
///
/// Exact selectors use the keyed snapshot seam so validation remains bounded
/// even when many documents precede the target in canonical key order. The
/// lookup is collection-scoped and snapshot-bound, so it cannot accidentally
/// accept an identifier from another collection or a later snapshot.
fn document_exists(
    engine: &Engine,
    scope: &TenantScope,
    snapshot: &ReadSnapshot,
    collection: &CollectionName,
    id: &DocumentId,
) -> Result<bool, StreamError> {
    let documents = engine
        .snapshot_documents_by_ids(scope, snapshot, collection, std::slice::from_ref(id))
        .map_err(|error| map_engine_error(&error))?;
    Ok(!documents.is_empty())
}

/// Validates one query plan as a stream source.
fn check_query_source(plan: &QueryPlan) -> Result<(), StreamError> {
    // A limit-free plan materializes every match per execution, which would
    // make a stream's memory unbounded; streaming requires a bounded page.
    if plan.model().paging().limit().is_none() {
        return Err(StreamError::InvalidSource);
    }
    Ok(())
}

/// Serves one bounded batch of a snapshot-bound stream.
#[allow(clippy::needless_pass_by_value)] // a continuation cursor is one-shot by contract
pub(crate) fn next_batch(
    engine: &Engine,
    scope: &TenantScope,
    snapshot: &ReadSnapshot,
    source: &StreamSource,
    cursor: StreamCursor,
    limit: u32,
    cancellation: &Cancellation,
) -> Result<super::StreamBatch, StreamError> {
    // The caller's live signal is checked before any work: a cancelled
    // stream closes promptly without taking a lock or a buffer.
    if cancellation.is_cancelled() {
        return Err(StreamError::Cancelled);
    }
    if limit == 0 || limit > super::MAX_STREAM_BATCH_ITEMS {
        return Err(StreamError::InvalidSource);
    }
    validate_context(engine, scope, snapshot)?;
    let binding = CursorBinding {
        engine: scope.engine_identity(),
        snapshot: snapshot.sequence(),
        kind: kind_of(source),
        token: source_token(source),
    };
    if cursor.engine != binding.engine
        || cursor.token != binding.token
        || cursor.snapshot != binding.snapshot
        || cursor.kind != binding.kind
    {
        return Err(StreamError::InvalidCursor);
    }
    match (source, &cursor.position) {
        (StreamSource::Documents(selector), CursorPosition::Documents { after, exhausted }) => {
            document_batch(
                engine,
                scope,
                snapshot,
                selector,
                &binding,
                after.clone(),
                *exhausted,
                limit,
            )
        }
        (StreamSource::Query(plan), CursorPosition::Query { offset, exhausted }) => {
            query_batch(engine, snapshot, plan, &binding, *offset, *exhausted, limit)
        }
        (StreamSource::TimeSeries(range), CursorPosition::TimeSeries { offset, exhausted }) => {
            time_series_batch(engine, scope, snapshot, range, &binding, *offset, *exhausted, limit)
        }
        (StreamSource::Family(selector), CursorPosition::Family { exhausted }) => {
            family_batch(engine, scope, snapshot, selector, &binding, *exhausted)
        }
        // The token check above already rejects a position of another kind.
        _ => Err(StreamError::InvalidCursor),
    }
}

/// Observes one cursor publicly.
pub(crate) fn position(cursor: &StreamCursor) -> super::StreamPosition {
    super::StreamPosition { kind: cursor.kind, sequence: cursor.snapshot }
}

/// Serves one document batch from the W4-CA-7 windowed read.
///
/// One call reads at most `MAX_WINDOW_READS_PER_BATCH` windows of at most
/// `limit + 1` documents, keeps only matching documents up to `limit`, and
/// never materializes the tenant: this is the bounded-memory mechanism the
/// contract names for `stream_memory_stays_bounded_when_the_consumer_never_drains`.
#[allow(clippy::too_many_arguments)] // one frozen batch contract over one binding
fn document_batch(
    engine: &Engine,
    scope: &TenantScope,
    snapshot: &ReadSnapshot,
    selector: &DocumentStreamSelector,
    binding: &CursorBinding,
    after: Option<DocumentId>,
    exhausted: bool,
    limit: u32,
) -> Result<super::StreamBatch, StreamError> {
    check_signal(&selector.deadline(), &selector.cancellation())?;
    if exhausted {
        return Ok(terminal_batch(binding, CursorPosition::Documents { after, exhausted: true }));
    }
    // An exact selector has at most one result. Serve it through the keyed
    // snapshot seam instead of scanning the tenant prefix to find it; this
    // keeps both source validation and delivery bounded for deep keys.
    if let Some(id) = selector.id() {
        let documents = engine
            .snapshot_documents_by_ids(
                scope,
                snapshot,
                selector.collection(),
                std::slice::from_ref(id),
            )
            .map_err(|error| map_engine_error(&error))?;
        let items = documents
            .into_iter()
            .filter(|document| selector_matches(selector, document))
            .map(StreamItem::Document)
            .collect();
        return Ok(super::StreamBatch {
            items,
            next_cursor: binding
                .cursor(CursorPosition::Documents { after: Some(id.clone()), exhausted: true }),
            completion: Some(StreamCompletion::EndOfStream),
            index_generation: None,
            includes: crate::query::IncludeSet::default(),
        });
    }
    let limit_index = usize::try_from(limit).unwrap_or(usize::MAX);
    let mut items: Vec<StreamItem> = Vec::new();
    let mut last_delivered: Option<DocumentId> = None;
    let mut resume = after;
    let mut complete = false;
    for _ in 0..MAX_WINDOW_READS_PER_BATCH {
        let window = engine
            .snapshot_documents_from(scope, snapshot, resume.clone(), limit.saturating_add(1))
            .map_err(|error| map_engine_error(&error))?;
        // A window that did not fill its lookahead reached the end of the
        // tenant view at this snapshot.
        let short_window = window.len() <= limit_index;
        let mut last_raw: Option<DocumentId> = None;
        let mut batch_full = false;
        for document in &window {
            last_raw = Some(document.id().clone());
            if !selector_matches(selector, document) {
                continue;
            }
            if items.len() >= limit_index {
                batch_full = true;
                break;
            }
            last_delivered = Some(document.id().clone());
            items.push(StreamItem::Document(document.clone()));
        }
        if batch_full {
            // Resume exactly after the last delivered document so the
            // lookahead document is never skipped. Documents the selector
            // filtered between matches are re-scanned, never lost. This
            // takes priority over a short window: a batch that filled
            // still holding a matching item is not a complete stream —
            // the window's shortness says the view ends, not that every
            // match in it was delivered.
            resume = last_delivered.or(resume);
            break;
        }
        if short_window {
            // The snapshot view is exhausted and no match is pending in
            // the window: every match has been delivered.
            complete = true;
            resume = last_raw.or(resume);
            break;
        }
        resume = last_raw;
    }
    Ok(super::StreamBatch {
        items,
        next_cursor: binding
            .cursor(CursorPosition::Documents { after: resume, exhausted: complete }),
        completion: complete.then_some(StreamCompletion::EndOfStream),
        index_generation: None,
        includes: crate::query::IncludeSet::default(),
    })
}

/// Builds one terminal, empty batch for an already-finished stream.
fn terminal_batch(binding: &CursorBinding, position: CursorPosition) -> super::StreamBatch {
    super::StreamBatch {
        items: Vec::new(),
        next_cursor: binding.cursor(position),
        completion: Some(StreamCompletion::EndOfStream),
        index_generation: None,
        includes: crate::query::IncludeSet::default(),
    }
}

/// Reports whether one document matches the typed selectors.
fn selector_matches(selector: &DocumentStreamSelector, document: &Document) -> bool {
    if document.collection().as_str() != selector.collection().as_str() {
        return false;
    }
    let id = document.id().as_str();
    if let Some(exact) = selector.id()
        && id != exact.as_str()
    {
        return false;
    }
    if let Some(prefix) = selector.prefix()
        && !id.starts_with(prefix)
    {
        return false;
    }
    if let Some(pattern) = selector.matching()
        && !id.contains(pattern)
    {
        return false;
    }
    if selector.excluded().iter().any(|excluded| excluded.as_str() == id) {
        return false;
    }
    true
}

/// Serves one query batch by executing the plan and slicing its window.
///
/// Each batch executes the immutable plan once — the W3-2 execution path —
/// and slices the plan's bounded result window at the cursor offset, so the
/// memory a query stream holds is bounded by the plan's page limit, never
/// by the matched set. The batch reports the index generation that
/// execution used, faithfully `None` for an index-less execution, and
/// materializes the plan's includes over exactly the rows it sliced into
/// this batch — the same materialization the paged execution runs over its
/// own page, so the union of every batch's include set equals the paged
/// execution's.
fn query_batch(
    engine: &Engine,
    snapshot: &ReadSnapshot,
    plan: &QueryPlan,
    binding: &CursorBinding,
    offset: u64,
    exhausted: bool,
    limit: u32,
) -> Result<super::StreamBatch, StreamError> {
    if exhausted {
        return Ok(terminal_batch(binding, CursorPosition::Query { offset, exhausted: true }));
    }
    let store = engine.queries();
    let result = store.execute(plan, snapshot).map_err(map_query_error)?;
    let rows = result.rows();
    let offset_index = usize::try_from(offset).unwrap_or(usize::MAX);
    if offset_index >= rows.len() {
        let batch = super::StreamBatch {
            items: Vec::new(),
            next_cursor: binding.cursor(CursorPosition::Query { offset, exhausted: true }),
            completion: Some(StreamCompletion::EndOfStream),
            index_generation: result.index_generation().cloned(),
            includes: crate::query::IncludeSet::default(),
        };
        return Ok(batch);
    }
    let limit_index = usize::try_from(limit).unwrap_or(usize::MAX);
    let end = offset_index.saturating_add(limit_index).min(rows.len());
    let items: Vec<StreamItem> =
        rows[offset_index..end].iter().cloned().map(StreamItem::QueryRow).collect();
    let includes = batch_includes(engine, snapshot, plan, &rows[offset_index..end])?;
    let consumed = u64::try_from(end - offset_index).unwrap_or(u64::MAX);
    let next_offset = offset.saturating_add(consumed);
    let complete = next_offset >= u64::try_from(rows.len()).unwrap_or(u64::MAX);
    Ok(super::StreamBatch {
        items,
        next_cursor: binding
            .cursor(CursorPosition::Query { offset: next_offset, exhausted: complete }),
        completion: complete.then_some(StreamCompletion::EndOfStream),
        index_generation: result.index_generation().cloned(),
        includes,
    })
}

/// Materializes one batch's includes over exactly the rows it carries.
///
/// The snapshot view is re-read here rather than borrowed from the
/// execution, because the execution's view is private to it; the snapshot
/// is immutable, so the two reads observe the same documents and reference
/// resolution applies the same first-match rule the loads clause applies.
/// A plan without include clauses materializes nothing and reads nothing.
fn batch_includes(
    engine: &Engine,
    snapshot: &ReadSnapshot,
    plan: &QueryPlan,
    rows: &[crate::query::QueryRow],
) -> Result<crate::query::IncludeSet, StreamError> {
    if plan.includes().is_empty() || rows.is_empty() {
        return Ok(crate::query::IncludeSet::default());
    }
    let scope = TenantScope::from_parts(snapshot.tenant().clone(), snapshot.engine_identity());
    let documents =
        engine.snapshot_documents(&scope, snapshot).map_err(|_| StreamError::EngineClosed)?;
    crate::query::includes::materialize(
        engine,
        snapshot,
        &documents,
        rows,
        plan.includes(),
        plan.model().deadline(),
        plan.model().cancellation_signal(),
    )
    .map_err(map_query_error)
}

/// Serves one time-series batch by slicing the family seam's page.
///
/// One call reads one series record through the family seam's typed
/// reader and slices it at the cursor offset; the record is
/// retention-bounded by the family policy. The batch preserves the entry
/// timestamp, tag, and value shape exactly as the family reader served
/// them.
#[allow(clippy::too_many_arguments)] // one frozen batch contract over one binding
fn time_series_batch(
    engine: &Engine,
    scope: &TenantScope,
    snapshot: &ReadSnapshot,
    range: &TimeSeriesStreamRange,
    binding: &CursorBinding,
    offset: u64,
    exhausted: bool,
    limit: u32,
) -> Result<super::StreamBatch, StreamError> {
    check_signal(&range.deadline(), &range.cancellation())?;
    if exhausted {
        return Ok(terminal_batch(binding, CursorPosition::TimeSeries { offset, exhausted: true }));
    }
    let read = TimeSeriesRead::try_new(
        range.document().clone(),
        range.name().to_owned(),
        range.range(),
        range.deadline(),
        range.cancellation(),
    )
    .map_err(|_| StreamError::InvalidSource)?;
    let context = FamilyReadContext::try_new(scope.clone(), snapshot.clone())
        .map_err(|_| StreamError::TenantMismatch)?;
    let crate::families::FamilyReadResult::TimeSeriesPage(page) = engine
        .families()
        .read(context, FamilyReadRequest::TimeSeriesRange(read))
        .map_err(map_family_error)?
    else {
        return Err(StreamError::InvalidSource);
    };
    let entries = page.entries();
    let offset_index = usize::try_from(offset).unwrap_or(usize::MAX);
    if offset_index >= entries.len() {
        return Ok(terminal_batch(binding, CursorPosition::TimeSeries { offset, exhausted: true }));
    }
    let limit_index = usize::try_from(limit).unwrap_or(usize::MAX);
    let end = offset_index.saturating_add(limit_index).min(entries.len());
    let items: Vec<StreamItem> =
        entries[offset_index..end].iter().cloned().map(StreamItem::TimeSeries).collect();
    let consumed = u64::try_from(end - offset_index).unwrap_or(u64::MAX);
    let next_offset = offset.saturating_add(consumed);
    let complete = next_offset >= u64::try_from(entries.len()).unwrap_or(u64::MAX);
    Ok(super::StreamBatch {
        items,
        next_cursor: binding
            .cursor(CursorPosition::TimeSeries { offset: next_offset, exhausted: complete }),
        completion: complete.then_some(StreamCompletion::EndOfStream),
        index_generation: None,
        includes: crate::query::IncludeSet::default(),
    })
}

/// Serves the single family read of one family stream.
///
/// The family seam's typed readers are whole-record: one family stream
/// serves one [`FamilyReadResult`](crate::families::FamilyReadResult) item
/// and then completes, under the same cursor and completion rules as every
/// other source. Streaming a family record set page by page is a later
/// family-seam capability, recorded as a limit in the design record.
fn family_batch(
    engine: &Engine,
    scope: &TenantScope,
    snapshot: &ReadSnapshot,
    selector: &FamilyStreamSelector,
    binding: &CursorBinding,
    exhausted: bool,
) -> Result<super::StreamBatch, StreamError> {
    check_signal(&selector.request().deadline(), &selector.request().cancellation())?;
    if exhausted {
        return Ok(terminal_batch(binding, CursorPosition::Family { exhausted: true }));
    }
    let context = FamilyReadContext::try_new(scope.clone(), snapshot.clone())
        .map_err(|_| StreamError::TenantMismatch)?;
    let result =
        engine.families().read(context, selector.request().clone()).map_err(map_family_error)?;
    Ok(super::StreamBatch {
        items: vec![StreamItem::Family(result)],
        next_cursor: binding.cursor(CursorPosition::Family { exhausted: true }),
        completion: Some(StreamCompletion::EndOfStream),
        index_generation: None,
        includes: crate::query::IncludeSet::default(),
    })
}
