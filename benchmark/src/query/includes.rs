//! The includes contract: same-request secondary materialization.
//!
//! An include names a path out of each result row and asks the engine to
//! materialize what that path points at, beside the rows, in the same
//! response. It is not a join, not a projection, and not a second round
//! trip. V1 carries the materialization beside its result array
//! (`QueryResultBase.Includes` and its counter and time-series siblings in
//! the pinned checkout), and this seam keeps that shape: one
//! [`IncludeSet`] on the [`QueryResult`](super::QueryResult), never merged
//! into the rows.
//!
//! Everything here is bounded and deterministic. Clause construction
//! refuses over-long paths, depths, and name lists before any data access;
//! materialization counts the documents it materializes and refuses past
//! [`MAX_INCLUDE_DOCUMENTS`]; and the one hazard chaining introduces — a
//! reference cycle — is detected over the finite include graph the
//! execution actually traversed and refused as
//! [`QueryError::IncludeCycle`]. There is
//! no recursion anywhere on this path: the expansion is a bounded level
//! walk and the cycle test is an explicit stack.
//!
//! Counter and time-series values are read through the family seam's own
//! typed readers and returned as the family seam's own values; this module
//! coordinates those reads and never re-derives family state. The contract
//! record is `docs/query/gapq07-includes-contract.md`.

use std::collections::{BTreeMap, BTreeSet};
use std::time::Duration;

use super::spec::validate_field_name;
use super::{QueryError, QueryRow};
use crate::engine::Engine;
use crate::families::{
    CounterEntry, CounterRead, FamilyError, FamilyReadContext, FamilyReadRequest, FamilyReadResult,
    TimeSeriesPage, TimeSeriesRange, TimeSeriesRead,
};
use crate::operations::RequestValidationError;
use crate::types::{Cancellation, Deadline, Document, DocumentId, DocumentValue, ReadSnapshot};

/// The maximum number of include clauses one query may carry, matching the
/// load-alias bound `MAX_LOADS`.
pub const MAX_INCLUDES: usize = 8;

/// The maximum number of dotted segments in one include path.
pub const MAX_INCLUDE_PATH_SEGMENTS: usize = 8;

/// The maximum chaining depth of one document include.
///
/// `1` is V1 parity: V1's `IncludeDocumentsCommand` gathers from the result
/// rows and loads the references, but never gathers from the loaded
/// documents, so its includes are exactly one level deep. Depths above `1`
/// chain the same path out of each included document and are a bounded V2
/// extension.
pub const MAX_INCLUDE_DEPTH: u8 = 4;

/// The maximum number of distinct documents one execution may materialize
/// as includes.
///
/// Depth bounds the number of levels, not the width: one array field can
/// carry an unbounded number of references. This bound closes that, so the
/// include set is never the one unbounded allocation in an otherwise
/// bounded execution.
pub const MAX_INCLUDE_DOCUMENTS: usize = 1024;

/// The maximum number of counter names one counter include may request,
/// matching the membership-list bound `MAX_IN_VALUES`.
pub const MAX_INCLUDE_COUNTER_NAMES: usize = 32;

/// The execution window a family read gets when the plan itself carries no
/// deadline.
///
/// The V1-compatible form normalizes without a deadline or a cancellation,
/// but the family seam requires both for every read. This window is the
/// include's own bound in that case, not a claim about how long a read
/// takes.
pub const INCLUDE_READ_WINDOW: Duration = Duration::from_mins(1);

// ---------------------------------------------------------------------------
// Clause values
// ---------------------------------------------------------------------------

/// A dotted include path: the walk one include takes out of a document.
///
/// The walk navigates `Object` fields by segment name and flattens `Array`
/// elements at every step, so `friends.name` reaches the name of every
/// element of `friends` — the flattened term space V1's include walker
/// applies. A leaf contributes a reference when it is a string or an
/// integer, exactly V1's two accepted token shapes; every other leaf
/// contributes nothing.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct IncludePath {
    segments: Vec<String>,
}

impl IncludePath {
    /// Creates a validated dotted path.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::EmptyField`] for an empty segment
    /// and [`RequestValidationError::InvalidQuery`] for an invalid segment
    /// or a path of more than [`MAX_INCLUDE_PATH_SEGMENTS`] segments.
    pub fn try_new(path: &str) -> Result<Self, RequestValidationError> {
        let segments: Vec<&str> = path.split('.').collect();
        if segments.len() > MAX_INCLUDE_PATH_SEGMENTS {
            return Err(RequestValidationError::InvalidQuery);
        }
        for segment in &segments {
            validate_field_name(segment)?;
        }
        Ok(Self { segments: segments.into_iter().map(str::to_owned).collect() })
    }

    /// Creates a validated single-segment path.
    ///
    /// # Errors
    ///
    /// Returns the field-name validation error of the one segment.
    pub fn field(name: &str) -> Result<Self, RequestValidationError> {
        validate_field_name(name)?;
        Ok(Self { segments: vec![name.to_owned()] })
    }

    /// Returns the path segments in walk order.
    #[must_use]
    pub fn segments(&self) -> &[String] {
        &self.segments
    }
}

/// A document include: follow a path, materialize what it names.
///
/// V1's `Include(path)` and its collection-prefix spelling `path(prefix)`.
/// A reference that resolves to no document contributes nothing, and a
/// reference that resolves to a document already carried as a row of this
/// result is not re-materialized — V1's `AddToIgnore` rule.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct DocumentInclude {
    path: IncludePath,
    id_prefix: Option<String>,
    depth: u8,
}

impl DocumentInclude {
    /// Creates a document include.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidQuery`] for a depth of zero
    /// or above [`MAX_INCLUDE_DEPTH`].
    pub fn try_new(path: IncludePath, depth: u8) -> Result<Self, RequestValidationError> {
        if depth == 0 || depth > MAX_INCLUDE_DEPTH {
            return Err(RequestValidationError::InvalidQuery);
        }
        Ok(Self { path, id_prefix: None, depth })
    }

    /// Binds the collection-identifier prefix of V1's `path(prefix)` form.
    ///
    /// A prefixed reference is the prefix joined to the gathered reference,
    /// and the bare reference is gathered alongside it, because V1 loads
    /// both and a caller relying on either must not change answers.
    ///
    /// # Errors
    ///
    /// Returns the field-name validation error of the prefix.
    pub fn with_id_prefix(mut self, prefix: &str) -> Result<Self, RequestValidationError> {
        validate_field_name(prefix)?;
        self.id_prefix = Some(prefix.to_owned());
        Ok(self)
    }

    /// Returns the walked path.
    #[must_use]
    pub fn path(&self) -> &IncludePath {
        &self.path
    }

    /// Returns the collection-identifier prefix, when one is bound.
    #[must_use]
    pub fn id_prefix(&self) -> Option<&str> {
        self.id_prefix.as_deref()
    }

    /// Returns the chaining depth. `1` gathers from the result rows only.
    #[must_use]
    pub const fn depth(&self) -> u8 {
        self.depth
    }
}

/// A counter include: materialize counters of what a path names.
///
/// V1's `counters(sourcePath, 'name')` for each listed name and
/// `counters(sourcePath)` for the whole group. Counter includes gather from
/// the result rows only; V1 has no chaining form, and counters are not
/// documents, so there is nothing to chain through.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct CounterInclude {
    path: IncludePath,
    names: Vec<String>,
}

impl CounterInclude {
    /// Creates a counter include.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::EmptyField`] for an empty name,
    /// [`RequestValidationError::InvalidQuery`] for an invalid or duplicated
    /// name or a name list longer than [`MAX_INCLUDE_COUNTER_NAMES`].
    pub fn try_new(path: IncludePath, names: Vec<String>) -> Result<Self, RequestValidationError> {
        if names.len() > MAX_INCLUDE_COUNTER_NAMES {
            return Err(RequestValidationError::InvalidQuery);
        }
        for name in &names {
            validate_field_name(name)?;
        }
        let mut unique = names.clone();
        unique.sort_unstable();
        if unique.windows(2).any(|pair| pair[0] == pair[1]) {
            return Err(RequestValidationError::InvalidQuery);
        }
        Ok(Self { path, names })
    }

    /// Returns the walked path.
    #[must_use]
    pub fn path(&self) -> &IncludePath {
        &self.path
    }

    /// Returns the requested counter names, or an empty list for the whole
    /// group.
    #[must_use]
    pub fn names(&self) -> &[String] {
        &self.names
    }
}

/// A time-series include: materialize one series range of what a path names.
///
/// V1's `timeseries(sourcePath, 'name', from, to)`, with `None` for the
/// whole series. V1's `last(n)` and `last(time, unit)` forms need a rollup
/// decision against the family policy and stay with the families wave.
#[derive(Clone, Debug, PartialEq)]
pub struct TimeSeriesInclude {
    path: IncludePath,
    name: String,
    range: Option<TimeSeriesRange>,
}

impl TimeSeriesInclude {
    /// Creates a time-series include.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::EmptyField`] for an empty series
    /// name and [`RequestValidationError::InvalidQuery`] for an invalid one.
    pub fn try_new(
        path: IncludePath,
        name: &str,
        range: Option<TimeSeriesRange>,
    ) -> Result<Self, RequestValidationError> {
        validate_field_name(name)?;
        Ok(Self { path, name: name.to_owned(), range })
    }

    /// Returns the walked path.
    #[must_use]
    pub fn path(&self) -> &IncludePath {
        &self.path
    }

    /// Returns the series name.
    #[must_use]
    pub fn name(&self) -> &str {
        &self.name
    }

    /// Returns the requested inclusive range, or `None` for the whole
    /// series.
    #[must_use]
    pub const fn range(&self) -> Option<TimeSeriesRange> {
        self.range
    }
}

/// One include clause of a query.
///
/// The clause set is closed over the three sub-contracts this seam owns.
/// V1 folds revisions, compare-exchange values, highlighting, timings, and
/// explanation into the same `include` keyword; those are not defined here
/// and stay with their owning tickets.
#[derive(Clone, Debug, PartialEq)]
pub enum IncludeClause {
    /// Follow a document path and materialize the referenced documents.
    Documents(DocumentInclude),
    /// Materialize the counters of the document a path names.
    Counters(CounterInclude),
    /// Materialize one time-series range of the document a path names.
    TimeSeries(TimeSeriesInclude),
}

impl IncludeClause {
    /// Wraps a document include.
    #[must_use]
    pub fn documents(value: DocumentInclude) -> Self {
        Self::Documents(value)
    }

    /// Wraps a counter include.
    #[must_use]
    pub fn counters(value: CounterInclude) -> Self {
        Self::Counters(value)
    }

    /// Wraps a time-series include.
    #[must_use]
    pub fn time_series(value: TimeSeriesInclude) -> Self {
        Self::TimeSeries(value)
    }

    /// Returns the walked path of this clause.
    #[must_use]
    pub fn path(&self) -> &IncludePath {
        match self {
            Self::Documents(value) => value.path(),
            Self::Counters(value) => value.path(),
            Self::TimeSeries(value) => value.path(),
        }
    }

    /// Validates one clause list as the include set of a query.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidQuery`] when the list is
    /// longer than [`MAX_INCLUDES`]. Each clause is already validated by its
    /// own constructor.
    pub(crate) fn validate_list(clauses: &[Self]) -> Result<(), RequestValidationError> {
        if clauses.len() > MAX_INCLUDES {
            return Err(RequestValidationError::InvalidQuery);
        }
        Ok(())
    }
}

// ---------------------------------------------------------------------------
// Result values
// ---------------------------------------------------------------------------

/// The counters of one included document.
#[derive(Clone, Debug, PartialEq)]
pub struct IncludedCounters {
    document: DocumentId,
    entries: Vec<CounterEntry>,
}

impl IncludedCounters {
    /// Creates one included counter group. Crate-only.
    pub(crate) fn new(document: DocumentId, entries: Vec<CounterEntry>) -> Self {
        Self { document, entries }
    }

    /// Returns the document the counters belong to.
    #[must_use]
    pub const fn document(&self) -> &DocumentId {
        &self.document
    }

    /// Returns the counter entries, ordered by name.
    #[must_use]
    pub fn entries(&self) -> &[CounterEntry] {
        &self.entries
    }
}

/// One included time-series range.
#[derive(Clone, Debug, PartialEq)]
pub struct IncludedTimeSeries {
    document: DocumentId,
    name: String,
    range: Option<TimeSeriesRange>,
    page: TimeSeriesPage,
}

impl IncludedTimeSeries {
    /// Creates one included series. Crate-only.
    pub(crate) fn new(
        document: DocumentId,
        name: String,
        range: Option<TimeSeriesRange>,
        page: TimeSeriesPage,
    ) -> Self {
        Self { document, name, range, page }
    }

    /// Returns the document the series belongs to.
    #[must_use]
    pub const fn document(&self) -> &DocumentId {
        &self.document
    }

    /// Returns the series name.
    #[must_use]
    pub fn name(&self) -> &str {
        &self.name
    }

    /// Returns the inclusive range that was read, or `None` for the whole
    /// series.
    #[must_use]
    pub const fn range(&self) -> Option<TimeSeriesRange> {
        self.range
    }

    /// Returns the read page, its entries in timestamp order.
    #[must_use]
    pub fn page(&self) -> &TimeSeriesPage {
        &self.page
    }
}

/// The include materialization of one executed query.
///
/// Documents are ordered by identifier, counters by document then name, and
/// time-series by document then name. Every set the materializer keeps is
/// an ordered map or set over the reference string, so the same plan, rows,
/// and snapshot always produce the same include set in the same order.
#[derive(Clone, Debug, Default, PartialEq)]
pub struct IncludeSet {
    documents: Vec<Document>,
    counters: Vec<IncludedCounters>,
    time_series: Vec<IncludedTimeSeries>,
}

impl IncludeSet {
    /// Creates one include set from its typed parts. Crate-only.
    pub(crate) fn new(
        documents: Vec<Document>,
        counters: Vec<IncludedCounters>,
        time_series: Vec<IncludedTimeSeries>,
    ) -> Self {
        Self { documents, counters, time_series }
    }

    /// Returns the included documents in ascending identifier order.
    #[must_use]
    pub fn documents(&self) -> &[Document] {
        &self.documents
    }

    /// Returns the included counter groups in ascending document order.
    #[must_use]
    pub fn counters(&self) -> &[IncludedCounters] {
        &self.counters
    }

    /// Returns the included time-series ranges in ascending document and
    /// name order.
    #[must_use]
    pub fn time_series(&self) -> &[IncludedTimeSeries] {
        &self.time_series
    }

    /// Reports whether nothing was materialized.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.documents.is_empty() && self.counters.is_empty() && self.time_series.is_empty()
    }

    /// Returns the included document one reference resolved to.
    #[must_use]
    pub fn find_document(&self, id: &str) -> Option<&Document> {
        self.documents.iter().find(|document| document.id().as_str() == id)
    }

    /// Returns the included counter group of one document.
    #[must_use]
    pub fn counters_of(&self, document: &DocumentId) -> Option<&IncludedCounters> {
        self.counters.iter().find(|group| group.document() == document)
    }

    /// Returns the first included series of one document and name.
    #[must_use]
    pub fn time_series_of(&self, document: &DocumentId, name: &str) -> Option<&IncludedTimeSeries> {
        self.time_series
            .iter()
            .find(|series| series.document() == document && series.name() == name)
    }
}

// ---------------------------------------------------------------------------
// Materialization
// ---------------------------------------------------------------------------

/// Walks one include path out of one document and returns the gathered
/// references.
///
/// The walk navigates object fields by segment and flattens array elements
/// at every step. A leaf is a reference when it is a string or an integer,
/// V1's two accepted token shapes; an integer leaf formats as its invariant
/// decimal string. A bound prefix joins onto the reference and the bare
/// reference is gathered alongside it only when the leaf was a string, the
/// rule V1's `IncludesUtil.ExecuteInternal` applies.
fn gather(path: &IncludePath, prefix: Option<&str>, document: &Document) -> BTreeSet<String> {
    let mut leaves: Vec<&DocumentValue> = Vec::new();
    walk(document.value(), path.segments(), &mut leaves);
    let mut gathered = BTreeSet::new();
    for leaf in leaves {
        let (reference, was_string) = match leaf {
            DocumentValue::String(value) => (value.clone(), true),
            DocumentValue::Integer(value) => (value.to_string(), false),
            _ => continue,
        };
        match prefix {
            None => {
                gathered.insert(reference);
            }
            Some(bound) => {
                gathered.insert(format!("{bound}{reference}"));
                if was_string {
                    gathered.insert(reference);
                }
            }
        }
    }
    gathered
}

/// Walks one value's remaining segments onto a leaf list.
///
/// The walk is an explicit stack, not a recursive call: it is bounded by
/// the path's segment count and the value's own shape, and stays that way
/// in the code as well as in the argument.
///
/// Flattening runs before the leaf test, so an array is never itself a
/// leaf: a segment that resolves to an array contributes each element at
/// the same position, and only a non-array value at the end of the segment
/// list is a leaf. That is V1's rule — `IncludesUtil.ExecuteInternal`
/// recurses into `BlittableJsonReaderArray` before it tests the token
/// shape — and it is what makes `tags` an array of references and
/// `friends.name` reach every friend's name.
fn walk<'value>(
    value: &'value DocumentValue,
    segments: &[String],
    leaves: &mut Vec<&'value DocumentValue>,
) {
    let mut pending: Vec<(&'value DocumentValue, usize)> = vec![(value, 0)];
    while let Some((current, index)) = pending.pop() {
        if let DocumentValue::Array(items) = current {
            for item in items {
                pending.push((item, index));
            }
            continue;
        }
        if index == segments.len() {
            leaves.push(current);
            continue;
        }
        if let DocumentValue::Object(fields) = current
            && let Some(next) = fields.get(segments[index].as_str())
        {
            pending.push((next, index + 1));
        }
    }
}

/// Reports whether the finite include graph carries a directed cycle.
///
/// The graph is exactly the edges the level walk traversed, so a cycle here
/// is a reference cycle reachable within the declared depth. The test is a
/// three-color marking over an explicit stack: a gray successor is a back
/// edge, and a back edge is a cycle.
fn has_cycle(edges: &BTreeMap<String, BTreeSet<String>>) -> bool {
    let successors: BTreeMap<&str, Vec<&str>> = edges
        .iter()
        .map(|(from, to)| (from.as_str(), to.iter().map(String::as_str).collect()))
        .collect();
    let mut color: BTreeMap<&str, u8> = BTreeMap::new();
    for root in successors.keys() {
        if color.get(root).is_some_and(|mark| *mark != 0) {
            continue;
        }
        color.insert(root, 1);
        let mut stack: Vec<(&str, usize)> = vec![(root, 0)];
        while let Some(last) = stack.len().checked_sub(1) {
            let (node, index) = stack[last];
            let node_successors =
                successors.get(node).map_or([].as_slice(), |list| list.as_slice());
            if index >= node_successors.len() {
                color.insert(node, 2);
                stack.pop();
                continue;
            }
            stack[last].1 += 1;
            let next = node_successors[index];
            match color.get(next).copied().unwrap_or(0) {
                // Gray: on the current path, so this edge closes it.
                1 => return true,
                // White: unseen, descend.
                0 => {
                    color.insert(next, 1);
                    stack.push((next, 0));
                }
                _ => {}
            }
        }
    }
    false
}

/// One distinct series request key: the gathered target reference, the
/// series name, and the inclusive bounds with `None` for the whole series.
type SeriesRequest = (String, String, Option<(i64, i64)>);

/// Maps one family-read failure onto the query error vocabulary.
///
/// A target document that does not exist is not a failure — V1's counter and
/// time-series includes simply have nothing to materialize for a reference
/// that resolves to no document — so the callers skip that case before it
/// can reach this mapping.
///
/// Crate-only: the family-source execution path (GAP-Q19) maps the same
/// failures through the same vocabulary, so an include and a family-source
/// query never report one family read differently.
pub(crate) fn map_family_error(error: FamilyError) -> QueryError {
    match error {
        FamilyError::EngineClosed => QueryError::EngineClosed,
        FamilyError::TenantMismatch => QueryError::TenantMismatch,
        FamilyError::SnapshotEngineMismatch => QueryError::SnapshotEngineMismatch,
        FamilyError::DeadlineExceeded => QueryError::Deadline,
        FamilyError::Cancelled => QueryError::Cancelled,
        // A family record the reader could not decode, an out-of-range
        // counter total, or any other family-specific rejection has no
        // query-side vocabulary; it reaches the caller as the same typed
        // storage failure the engine's own snapshot view reports, the same
        // mapping limit `map_engine_error` already records.
        FamilyError::StorageFailure
        | FamilyError::DocumentMissing
        | FamilyError::ReservedStateInvalid
        | FamilyError::CounterOverflow
        | FamilyError::InvalidRequest
        | FamilyError::Conflict
        | FamilyError::TransactionEngineMismatch
        | FamilyError::TransactionIdentityMismatch
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
        | FamilyError::TombstoneConflict => QueryError::StorageFailure,
    }
}

/// One include materialization over the rows a result or a batch carries.
///
/// The caller supplies the tenant's materialized snapshot view, so reference
/// resolution uses the same first-match-in-canonical-key-order map the loads
/// clause builds: a load, a point read, and an include never disagree about
/// which document one bare identifier names.
///
/// # Errors
///
/// Returns [`QueryError::IncludeCycle`] when the traversed include graph
/// carries a reference cycle, [`QueryError::IncludeBoundExceeded`] when the
/// include set would pass [`MAX_INCLUDE_DOCUMENTS`], the bounded-checkpoint
/// errors, and the mapped family-read failure of a counter or time-series
/// target.
pub(crate) fn materialize(
    engine: &Engine,
    snapshot: &ReadSnapshot,
    view_documents: &[Document],
    rows: &[QueryRow],
    clauses: &[IncludeClause],
    deadline: Option<Deadline>,
    cancellation: Option<&Cancellation>,
) -> Result<IncludeSet, QueryError> {
    if clauses.is_empty() || rows.is_empty() {
        return Ok(IncludeSet::default());
    }
    super::execute::check_bounds(deadline, cancellation)?;

    // The same first-match resolution map the loads clause applies, built
    // from the same materialized snapshot view.
    let mut by_id: BTreeMap<&str, &Document> = BTreeMap::new();
    for document in view_documents {
        by_id.entry(document.id().as_str()).or_insert(document);
    }
    // A row's own document is never materialized as an include of itself:
    // V1's AddToIgnore rule for a non-projection result.
    let row_ids: BTreeSet<&str> = rows.iter().map(|row| row.document().id().as_str()).collect();

    let documents = materialize_documents(&by_id, &row_ids, rows, clauses, deadline, cancellation)?;
    let counters = materialize_counters(
        engine,
        snapshot,
        &by_id,
        &row_ids,
        rows,
        clauses,
        deadline,
        cancellation,
    )?;
    let time_series = materialize_time_series(
        engine,
        snapshot,
        &by_id,
        &row_ids,
        rows,
        clauses,
        deadline,
        cancellation,
    )?;
    Ok(IncludeSet::new(documents, counters, time_series))
}

/// Materializes the document includes of one row set.
///
/// The expansion is a bounded level walk: level `0` is the rows, and each
/// step gathers the references of the level's documents for every clause
/// whose declared depth still reaches, resolves them, and passes the newly
/// materialized documents to the next step. Every gathered set is an ordered
/// set over the reference string, so the walk is deterministic and the
/// materialized documents come out in ascending identifier order.
fn materialize_documents(
    by_id: &BTreeMap<&str, &Document>,
    row_ids: &BTreeSet<&str>,
    rows: &[QueryRow],
    clauses: &[IncludeClause],
    deadline: Option<Deadline>,
    cancellation: Option<&Cancellation>,
) -> Result<Vec<Document>, QueryError> {
    let document_clauses: Vec<&DocumentInclude> = clauses
        .iter()
        .filter_map(|clause| match clause {
            IncludeClause::Documents(value) => Some(value),
            _ => None,
        })
        .collect();
    if document_clauses.is_empty() {
        return Ok(Vec::new());
    }
    let max_depth = document_clauses.iter().map(|clause| clause.depth()).max().unwrap_or(0);
    let mut materialized: BTreeMap<String, Document> = BTreeMap::new();
    let mut edges: BTreeMap<String, BTreeSet<String>> = BTreeMap::new();
    let mut level: Vec<&Document> = rows.iter().map(QueryRow::document).collect();
    for step in 0..u32::from(max_depth) {
        if level.is_empty() {
            break;
        }
        super::execute::check_bounds(deadline, cancellation)?;
        let mut pairs: BTreeSet<(String, String)> = BTreeSet::new();
        for document in &level {
            let source = document.id().as_str().to_owned();
            for clause in &document_clauses {
                if step >= u32::from(clause.depth()) {
                    continue;
                }
                for reference in gather(clause.path(), clause.id_prefix(), document) {
                    if !reference.is_empty() {
                        pairs.insert((source.clone(), reference));
                    }
                }
            }
        }
        // Record every traversed edge between resolved documents first, so
        // the cycle test sees the graph the walk actually walked whether or
        // not the target is new at this step.
        for (source, reference) in &pairs {
            if row_ids.contains(reference.as_str()) || !by_id.contains_key(reference.as_str()) {
                continue;
            }
            edges.entry(source.clone()).or_default().insert(reference.clone());
            edges.entry(reference.clone()).or_default();
        }
        let mut next: Vec<&Document> = Vec::new();
        for (_, reference) in &pairs {
            if row_ids.contains(reference.as_str()) {
                continue;
            }
            let Some(document) = by_id.get(reference.as_str()) else {
                continue;
            };
            if !materialized.contains_key(reference) {
                if materialized.len() >= MAX_INCLUDE_DOCUMENTS {
                    return Err(QueryError::IncludeBoundExceeded);
                }
                materialized.insert(reference.clone(), (*document).clone());
                next.push(*document);
            }
        }
        level = next;
    }
    if has_cycle(&edges) {
        return Err(QueryError::IncludeCycle);
    }
    Ok(materialized.into_values().collect())
}

/// Materializes the counter includes of one row set.
///
/// One included group per distinct target document: every clause that
/// reaches a document contributes its names, which is V1's
/// keyed-by-document result shape. Entries are sorted by name so the
/// include set's order never depends on the family reader's internal name
/// order.
#[allow(clippy::too_many_arguments)] // one shared materialization context over one row set
fn materialize_counters(
    engine: &Engine,
    snapshot: &ReadSnapshot,
    by_id: &BTreeMap<&str, &Document>,
    row_ids: &BTreeSet<&str>,
    rows: &[QueryRow],
    clauses: &[IncludeClause],
    deadline: Option<Deadline>,
    cancellation: Option<&Cancellation>,
) -> Result<Vec<IncludedCounters>, QueryError> {
    let counter_clauses: Vec<&CounterInclude> = clauses
        .iter()
        .filter_map(|clause| match clause {
            IncludeClause::Counters(value) => Some(value),
            _ => None,
        })
        .collect();
    if counter_clauses.is_empty() {
        return Ok(Vec::new());
    }
    let mut requested: BTreeMap<String, BTreeSet<String>> = BTreeMap::new();
    for row in rows {
        for clause in &counter_clauses {
            for reference in gather(clause.path(), None, row.document()) {
                if resolved_target(&reference, by_id, row_ids).is_some() {
                    requested.entry(reference).or_default().extend(clause.names().iter().cloned());
                }
            }
        }
    }
    let mut groups = Vec::new();
    for (reference, names) in requested {
        super::execute::check_bounds(deadline, cancellation)?;
        let target = by_id.get(reference.as_str()).copied();
        let Some(target) = target else {
            continue;
        };
        let read = CounterRead::try_new(
            target.id().clone(),
            names.into_iter().collect(),
            read_deadline(deadline),
            read_cancellation(cancellation),
        )
        .map_err(|_| QueryError::StorageFailure)?;
        let result = read_family(engine, snapshot, FamilyReadRequest::counter_group(read));
        let group = match result {
            Ok(FamilyReadResult::CounterGroup(group)) => group,
            // A reference that resolves to no document has no counters to
            // materialize; V1 omits them rather than failing the query.
            Err(FamilyError::DocumentMissing) => continue,
            Err(error) => return Err(map_family_error(error)),
            Ok(_) => return Err(QueryError::StorageFailure),
        };
        let mut entries = group.entries().to_vec();
        entries.sort_by(|left, right| left.name().cmp(right.name()));
        groups.push(IncludedCounters::new(target.id().clone(), entries));
    }
    Ok(groups)
}

/// Materializes the time-series includes of one row set.
///
/// One included series per distinct target, series name, and range request,
/// so two clauses naming the same series with different ranges stay two
/// materializations instead of silently merging.
#[allow(clippy::too_many_arguments)] // one shared materialization context over one row set
fn materialize_time_series(
    engine: &Engine,
    snapshot: &ReadSnapshot,
    by_id: &BTreeMap<&str, &Document>,
    row_ids: &BTreeSet<&str>,
    rows: &[QueryRow],
    clauses: &[IncludeClause],
    deadline: Option<Deadline>,
    cancellation: Option<&Cancellation>,
) -> Result<Vec<IncludedTimeSeries>, QueryError> {
    let series_clauses: Vec<&TimeSeriesInclude> = clauses
        .iter()
        .filter_map(|clause| match clause {
            IncludeClause::TimeSeries(value) => Some(value),
            _ => None,
        })
        .collect();
    if series_clauses.is_empty() {
        return Ok(Vec::new());
    }
    let mut requested: BTreeSet<SeriesRequest> = BTreeSet::new();
    for row in rows {
        for clause in &series_clauses {
            for reference in gather(clause.path(), None, row.document()) {
                if resolved_target(&reference, by_id, row_ids).is_some() {
                    let bounds = clause.range().map(|range| (range.start(), range.end()));
                    requested.insert((reference, clause.name().to_owned(), bounds));
                }
            }
        }
    }
    let mut series = Vec::new();
    for (reference, name, bounds) in requested {
        super::execute::check_bounds(deadline, cancellation)?;
        let Some(target) = by_id.get(reference.as_str()) else {
            continue;
        };
        // The bounds came from an already validated range, so this
        // reconstruction cannot fail; the mapping keeps that a typed fact
        // rather than an unwrap.
        let range = bounds
            .map(|(start, end)| TimeSeriesRange::try_new(start, end))
            .transpose()
            .map_err(|_| QueryError::StorageFailure)?;
        let read = TimeSeriesRead::try_new(
            target.id().clone(),
            name.clone(),
            range,
            read_deadline(deadline),
            read_cancellation(cancellation),
        )
        .map_err(|_| QueryError::StorageFailure)?;
        let result = read_family(engine, snapshot, FamilyReadRequest::time_series_range(read));
        let page = match result {
            Ok(FamilyReadResult::TimeSeriesPage(page)) => page,
            Err(FamilyError::DocumentMissing) => continue,
            Err(error) => return Err(map_family_error(error)),
            Ok(_) => return Err(QueryError::StorageFailure),
        };
        series.push(IncludedTimeSeries::new(target.id().clone(), name, range, page));
    }
    Ok(series)
}

/// Resolves one gathered reference to the document of the snapshot view,
/// applying the include skip rules.
///
/// `None` for an empty reference, a reference naming a document this result
/// already carries as a row, or a reference no document of the view answers.
/// The resolution is the loads clause's first-match rule over the same map,
/// so the two never disagree.
fn resolved_target<'view>(
    reference: &str,
    by_id: &BTreeMap<&str, &'view Document>,
    row_ids: &BTreeSet<&str>,
) -> Option<&'view Document> {
    if reference.is_empty() || row_ids.contains(reference) {
        return None;
    }
    by_id.get(reference).copied()
}

/// Reads one family capability against the snapshot the includes run on.
///
/// The read context is derived from the snapshot itself, exactly as
/// execution derives the tenant scope, so the family read observes the same
/// immutable view the rows came from.
///
/// Crate-only: the family-source execution path (GAP-Q19) reads through the
/// same helper, which is the read-parity rule itself — a query row, an
/// include entry, and a direct family read observe one seam.
pub(crate) fn read_family(
    engine: &Engine,
    snapshot: &ReadSnapshot,
    request: FamilyReadRequest,
) -> Result<FamilyReadResult, FamilyError> {
    let scope = crate::types::TenantScope::from_parts(
        snapshot.tenant().clone(),
        snapshot.engine_identity(),
    );
    let context = FamilyReadContext::new(scope, snapshot.clone());
    engine.families().read(context, request)
}

/// The include read window for a plan that carries no deadline.
pub(crate) fn read_deadline(deadline: Option<Deadline>) -> Deadline {
    deadline.unwrap_or_else(|| Deadline::after(INCLUDE_READ_WINDOW).expect("positive window"))
}

/// The include read signal for a plan that carries no cancellation.
pub(crate) fn read_cancellation(cancellation: Option<&Cancellation>) -> Cancellation {
    cancellation.cloned().unwrap_or_default()
}
