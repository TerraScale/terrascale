//! Typed family payloads and their closed value set.
//!
//! Every payload is built through `try_new`, which validates the typed fields
//! before the value is observable, and is inspected only through read-only
//! accessors. No payload has a public unchecked-field constructor. The rule is
//! the W3 core contract's construction and observation table: read payloads
//! carry typed fields plus a deadline and cancellation; write payloads carry
//! one validated `TenantScope` plus an operation identity, a deadline, and a
//! cancellation.
//!
//! Structural problems (empty names, inverted paging, duplicate fields) are
//! rejected here with [`RequestValidationError`]. Rules that need stored
//! family state, such as series arity or a stale compare-exchange index,
//! belong to the store and surface as [`super::FamilyError`] cases.

use crate::{
    operations::RequestValidationError,
    types::{
        Cancellation, CollectionName, Deadline, DocumentId, DocumentValue, ExpectedVersion,
        OperationId, TenantScope,
    },
};

use super::FamilyKind;

/// The maximum byte length of a family-local name.
const MAX_NAME_BYTES: usize = 255;

/// Validates one family-local name: non-empty, bounded, free of control
/// characters, and without surrounding whitespace.
fn validate_name(value: &str) -> Result<(), RequestValidationError> {
    if value.is_empty()
        || value.len() > MAX_NAME_BYTES
        || value.chars().any(char::is_control)
        || value != value.trim()
    {
        return Err(RequestValidationError::InvalidFamilyPayload);
    }
    Ok(())
}

/// Validates one free-text metadata field, for example a content type.
fn validate_text(value: &str) -> Result<(), RequestValidationError> {
    if value.is_empty() || value.chars().any(char::is_control) {
        return Err(RequestValidationError::InvalidFamilyPayload);
    }
    Ok(())
}

/// Rejects a deadline that has already elapsed.
fn validate_deadline(deadline: &Deadline) -> Result<(), RequestValidationError> {
    if deadline.is_expired() {
        return Err(RequestValidationError::InvalidDeadline);
    }
    Ok(())
}

/// Rejects a cancellation signal that is already cancelled.
fn validate_cancellation(signal: &Cancellation) -> Result<(), RequestValidationError> {
    if signal.is_cancelled() {
        return Err(RequestValidationError::InvalidCancellation);
    }
    Ok(())
}

/// Rejects a timestamp that is not a non-negative epoch-millisecond value.
///
/// Structural negativity is left to the store on purpose: lifecycle and
/// expiry values are family semantics and report
/// [`super::FamilyError`] cases, while payload construction only rejects
/// structurally impossible fields.
fn validate_epoch_millis(value: i64) -> Result<(), RequestValidationError> {
    if value < 0 {
        return Err(RequestValidationError::InvalidFamilyPayload);
    }
    Ok(())
}

// ---------------------------------------------------------------------------
// Attachment payloads
// ---------------------------------------------------------------------------

/// A typed attachment read: the metadata of every attachment of one document.
#[derive(Clone, Debug, PartialEq)]
pub struct AttachmentRead {
    document: DocumentId,
    deadline: Deadline,
    cancellation: Cancellation,
}

impl AttachmentRead {
    /// Creates a validated attachment read.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidDeadline`] for an elapsed
    /// deadline and [`RequestValidationError::InvalidCancellation`] for an
    /// already cancelled signal.
    pub fn try_new(
        document: DocumentId,
        deadline: Deadline,
        cancellation: Cancellation,
    ) -> Result<Self, RequestValidationError> {
        validate_deadline(&deadline)?;
        validate_cancellation(&cancellation)?;
        Ok(Self { document, deadline, cancellation })
    }

    /// Returns the owning document identity.
    #[must_use]
    pub fn document(&self) -> &DocumentId {
        &self.document
    }

    /// Returns the request deadline.
    #[must_use]
    pub fn deadline(&self) -> Deadline {
        self.deadline
    }

    /// Returns a clone of the cancellation signal.
    #[must_use]
    pub fn cancellation(&self) -> Cancellation {
        self.cancellation.clone()
    }
}

/// One typed attachment mutation.
#[derive(Clone, Debug, PartialEq)]
pub enum AttachmentMutation {
    /// Record attachment metadata. A name that already exists is a duplicate
    /// unless `replace` is set.
    Store {
        /// The attachment name.
        name: String,
        /// The logical content type.
        content_type: String,
        /// The logical content size in bytes.
        size: u64,
        /// The logical content digest.
        hash: String,
        /// Whether an existing attachment of the same name may be replaced.
        replace: bool,
    },
    /// Delete one attachment by name.
    Delete {
        /// The attachment name.
        name: String,
    },
    /// Rename one attachment in place.
    Rename {
        /// The current attachment name.
        from: String,
        /// The requested attachment name.
        to: String,
    },
    /// Copy one attachment to another document.
    Copy {
        /// The current attachment name.
        name: String,
        /// The receiving document identity.
        to_document: DocumentId,
        /// The requested attachment name on the receiver.
        to_name: String,
    },
    /// Move one attachment to another document.
    Move {
        /// The current attachment name.
        name: String,
        /// The receiving document identity.
        to_document: DocumentId,
        /// The requested attachment name on the receiver.
        to_name: String,
    },
}

impl AttachmentMutation {
    /// Returns the attachment name this mutation reads from.
    #[must_use]
    pub fn source_name(&self) -> &str {
        match self {
            Self::Store { name, .. }
            | Self::Delete { name }
            | Self::Copy { name, .. }
            | Self::Move { name, .. } => name,
            Self::Rename { from, .. } => from,
        }
    }
}

/// A typed attachment write bound to one document.
#[derive(Clone, Debug, PartialEq)]
pub struct AttachmentWrite {
    scope: TenantScope,
    document: DocumentId,
    mutation: AttachmentMutation,
    expected: ExpectedVersion,
    operation_id: OperationId,
    deadline: Deadline,
    cancellation: Cancellation,
}

impl AttachmentWrite {
    /// Creates a validated attachment mutation.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidFamilyPayload`] for an
    /// invalid name, content type, or digest,
    /// [`RequestValidationError::InvalidDeadline`] for an elapsed deadline,
    /// and [`RequestValidationError::InvalidCancellation`] for an already
    /// cancelled signal.
    pub fn try_new(
        scope: TenantScope,
        document: DocumentId,
        mutation: AttachmentMutation,
        expected: ExpectedVersion,
        operation_id: OperationId,
        deadline: Deadline,
        cancellation: Cancellation,
    ) -> Result<Self, RequestValidationError> {
        validate_name(mutation.source_name())?;
        if let AttachmentMutation::Store { content_type, hash, .. } = &mutation {
            validate_text(content_type)?;
            validate_text(hash)?;
        }
        if let AttachmentMutation::Rename { to, .. }
        | AttachmentMutation::Copy { to_name: to, .. }
        | AttachmentMutation::Move { to_name: to, .. } = &mutation
        {
            validate_name(to)?;
        }
        validate_deadline(&deadline)?;
        validate_cancellation(&cancellation)?;
        Ok(Self { scope, document, mutation, expected, operation_id, deadline, cancellation })
    }

    /// Returns the validated tenant scope.
    #[must_use]
    pub fn tenant_scope(&self) -> &TenantScope {
        &self.scope
    }

    /// Returns the owning document identity.
    #[must_use]
    pub fn document(&self) -> &DocumentId {
        &self.document
    }

    /// Returns the typed mutation.
    #[must_use]
    pub fn mutation(&self) -> &AttachmentMutation {
        &self.mutation
    }

    /// Returns the expected owner-document version.
    #[must_use]
    pub fn expected(&self) -> ExpectedVersion {
        self.expected
    }

    /// Returns the stable operation identity.
    #[must_use]
    pub fn operation_id(&self) -> &OperationId {
        &self.operation_id
    }

    /// Returns the request deadline.
    #[must_use]
    pub fn deadline(&self) -> Deadline {
        self.deadline
    }

    /// Returns a clone of the cancellation signal.
    #[must_use]
    pub fn cancellation(&self) -> Cancellation {
        self.cancellation.clone()
    }
}

/// The metadata of one recorded attachment.
#[derive(Clone, Debug, PartialEq)]
pub struct AttachmentMetadata {
    name: String,
    content_type: String,
    size: u64,
    hash: String,
    document: DocumentId,
}

impl AttachmentMetadata {
    /// Creates one metadata record. Crate-only: results are produced by the
    /// family store, never constructed by callers.
    pub(crate) fn new(
        name: String,
        content_type: String,
        size: u64,
        hash: String,
        document: DocumentId,
    ) -> Self {
        Self { name, content_type, size, hash, document }
    }

    /// Returns the attachment name.
    #[must_use]
    pub fn name(&self) -> &str {
        &self.name
    }

    /// Returns the logical content type.
    #[must_use]
    pub fn content_type(&self) -> &str {
        &self.content_type
    }

    /// Returns the logical content size in bytes.
    #[must_use]
    pub fn size(&self) -> u64 {
        self.size
    }

    /// Returns the logical content digest.
    #[must_use]
    pub fn hash(&self) -> &str {
        &self.hash
    }

    /// Returns the owning document identity.
    #[must_use]
    pub fn document(&self) -> &DocumentId {
        &self.document
    }
}

/// The metadata of every attachment of one document.
#[derive(Clone, Debug, Default, PartialEq)]
pub struct AttachmentMetadataResult {
    entries: Vec<AttachmentMetadata>,
}

impl AttachmentMetadataResult {
    /// Creates one metadata result. Crate-only.
    pub(crate) fn new(entries: Vec<AttachmentMetadata>) -> Self {
        Self { entries }
    }

    /// Returns the recorded metadata, ordered by attachment name.
    #[must_use]
    pub fn entries(&self) -> &[AttachmentMetadata] {
        &self.entries
    }

    /// Returns the metadata of one attachment name, if recorded.
    #[must_use]
    pub fn find(&self, name: &str) -> Option<&AttachmentMetadata> {
        self.entries.iter().find(|entry| entry.name() == name)
    }
}

// ---------------------------------------------------------------------------
// Counter payloads
// ---------------------------------------------------------------------------

/// A typed counter-group read: the named counters of one document, or every
/// counter of the document when no name is requested.
#[derive(Clone, Debug, PartialEq)]
pub struct CounterRead {
    document: DocumentId,
    names: Vec<String>,
    deadline: Deadline,
    cancellation: Cancellation,
}

impl CounterRead {
    /// Creates a validated counter read. An empty name list reads the whole
    /// counter group.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidFamilyPayload`] for an
    /// invalid or duplicated name,
    /// [`RequestValidationError::InvalidDeadline`] for an elapsed deadline,
    /// and [`RequestValidationError::InvalidCancellation`] for an already
    /// cancelled signal.
    pub fn try_new(
        document: DocumentId,
        names: Vec<String>,
        deadline: Deadline,
        cancellation: Cancellation,
    ) -> Result<Self, RequestValidationError> {
        for name in &names {
            validate_name(name)?;
        }
        let mut unique = names.clone();
        unique.sort_unstable();
        if unique.windows(2).any(|pair| pair[0] == pair[1]) {
            return Err(RequestValidationError::InvalidFamilyPayload);
        }
        validate_deadline(&deadline)?;
        validate_cancellation(&cancellation)?;
        Ok(Self { document, names, deadline, cancellation })
    }

    /// Returns the owning document identity.
    #[must_use]
    pub fn document(&self) -> &DocumentId {
        &self.document
    }

    /// Returns the requested names, or an empty list for the whole group.
    #[must_use]
    pub fn names(&self) -> &[String] {
        &self.names
    }

    /// Returns the request deadline.
    #[must_use]
    pub fn deadline(&self) -> Deadline {
        self.deadline
    }

    /// Returns a clone of the cancellation signal.
    #[must_use]
    pub fn cancellation(&self) -> Cancellation {
        self.cancellation.clone()
    }
}

/// One typed counter operation inside a batch.
#[derive(Clone, Debug, PartialEq)]
pub enum CounterOperation {
    /// Add a signed delta to a counter, creating it from zero.
    Increment {
        /// The counter name.
        name: String,
        /// The signed delta.
        delta: i64,
    },
    /// Delete a counter by name.
    Delete {
        /// The counter name.
        name: String,
    },
}

impl CounterOperation {
    /// Returns the counter name this operation touches.
    #[must_use]
    pub fn name(&self) -> &str {
        match self {
            Self::Increment { name, .. } | Self::Delete { name } => name,
        }
    }
}

/// A typed counter batch: one atomic set of counter operations on a document.
#[derive(Clone, Debug, PartialEq)]
pub struct CounterBatchWrite {
    scope: TenantScope,
    document: DocumentId,
    operations: Vec<CounterOperation>,
    expected: ExpectedVersion,
    operation_id: OperationId,
    deadline: Deadline,
    cancellation: Cancellation,
}

impl CounterBatchWrite {
    /// Creates a validated counter batch from named operations.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidFamilyPayload`] for an empty
    /// batch or an invalid name,
    /// [`RequestValidationError::InvalidDeadline`] for an elapsed deadline,
    /// and [`RequestValidationError::InvalidCancellation`] for an already
    /// cancelled signal.
    pub fn try_new(
        scope: TenantScope,
        document: DocumentId,
        operations: Vec<CounterOperation>,
        expected: ExpectedVersion,
        operation_id: OperationId,
        deadline: Deadline,
        cancellation: Cancellation,
    ) -> Result<Self, RequestValidationError> {
        if operations.is_empty() {
            return Err(RequestValidationError::InvalidFamilyPayload);
        }
        for operation in &operations {
            validate_name(operation.name())?;
        }
        validate_deadline(&deadline)?;
        validate_cancellation(&cancellation)?;
        Ok(Self { scope, document, operations, expected, operation_id, deadline, cancellation })
    }

    /// Returns the validated tenant scope.
    #[must_use]
    pub fn tenant_scope(&self) -> &TenantScope {
        &self.scope
    }

    /// Returns the owning document identity.
    #[must_use]
    pub fn document(&self) -> &DocumentId {
        &self.document
    }

    /// Returns the batch operations in request order.
    #[must_use]
    pub fn operations(&self) -> &[CounterOperation] {
        &self.operations
    }

    /// Returns the expected owner-document version.
    #[must_use]
    pub fn expected(&self) -> ExpectedVersion {
        self.expected
    }

    /// Returns the stable operation identity.
    #[must_use]
    pub fn operation_id(&self) -> &OperationId {
        &self.operation_id
    }

    /// Returns the request deadline.
    #[must_use]
    pub fn deadline(&self) -> Deadline {
        self.deadline
    }

    /// Returns a clone of the cancellation signal.
    #[must_use]
    pub fn cancellation(&self) -> Cancellation {
        self.cancellation.clone()
    }
}

/// One counter name with its value, or its absence.
#[derive(Clone, Debug, PartialEq)]
pub struct CounterEntry {
    name: String,
    value: Option<i64>,
}

impl CounterEntry {
    /// Creates one counter entry. Crate-only.
    pub(crate) fn new(name: String, value: Option<i64>) -> Self {
        Self { name, value }
    }

    /// Returns the counter name.
    #[must_use]
    pub fn name(&self) -> &str {
        &self.name
    }

    /// Returns the signed value, or `None` when the counter does not exist.
    #[must_use]
    pub fn value(&self) -> Option<i64> {
        self.value
    }
}

/// The counters of one document.
#[derive(Clone, Debug, Default, PartialEq)]
pub struct CounterGroupResult {
    entries: Vec<CounterEntry>,
}

impl CounterGroupResult {
    /// Creates one counter-group result. Crate-only.
    pub(crate) fn new(entries: Vec<CounterEntry>) -> Self {
        Self { entries }
    }

    /// Returns the counter entries.
    #[must_use]
    pub fn entries(&self) -> &[CounterEntry] {
        &self.entries
    }

    /// Returns one counter value by name.
    #[must_use]
    pub fn find(&self, name: &str) -> Option<Option<i64>> {
        self.entries.iter().find(|entry| entry.name() == name).map(CounterEntry::value)
    }
}

// ---------------------------------------------------------------------------
// Time-series payloads
// ---------------------------------------------------------------------------

/// An inclusive timestamp range in epoch milliseconds.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct TimeSeriesRange {
    start: i64,
    end: i64,
}

impl TimeSeriesRange {
    /// Creates a validated range.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidRange`] when the start is
    /// later than the end.
    pub fn try_new(start: i64, end: i64) -> Result<Self, RequestValidationError> {
        if start > end {
            return Err(RequestValidationError::InvalidRange);
        }
        Ok(Self { start, end })
    }

    /// Returns the inclusive start.
    #[must_use]
    pub fn start(&self) -> i64 {
        self.start
    }

    /// Returns the inclusive end.
    #[must_use]
    pub fn end(&self) -> i64 {
        self.end
    }
}

/// A typed time-series read over one named series of a document.
#[derive(Clone, Debug, PartialEq)]
pub struct TimeSeriesRead {
    document: DocumentId,
    name: String,
    range: Option<TimeSeriesRange>,
    deadline: Deadline,
    cancellation: Cancellation,
}

impl TimeSeriesRead {
    /// Creates a validated time-series read.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidFamilyPayload`] for an
    /// invalid series name, [`RequestValidationError::InvalidDeadline`] for
    /// an elapsed deadline, and
    /// [`RequestValidationError::InvalidCancellation`] for an already
    /// cancelled signal.
    pub fn try_new(
        document: DocumentId,
        name: String,
        range: Option<TimeSeriesRange>,
        deadline: Deadline,
        cancellation: Cancellation,
    ) -> Result<Self, RequestValidationError> {
        validate_name(&name)?;
        validate_deadline(&deadline)?;
        validate_cancellation(&cancellation)?;
        Ok(Self { document, name, range, deadline, cancellation })
    }

    /// Returns the owning document identity.
    #[must_use]
    pub fn document(&self) -> &DocumentId {
        &self.document
    }

    /// Returns the series name.
    #[must_use]
    pub fn name(&self) -> &str {
        &self.name
    }

    /// Returns the requested inclusive range, or `None` for the whole series.
    #[must_use]
    pub fn range(&self) -> Option<TimeSeriesRange> {
        self.range
    }

    /// Returns the request deadline.
    #[must_use]
    pub fn deadline(&self) -> Deadline {
        self.deadline
    }

    /// Returns a clone of the cancellation signal.
    #[must_use]
    pub fn cancellation(&self) -> Cancellation {
        self.cancellation.clone()
    }
}

/// One appended time-series point.
#[derive(Clone, Debug, PartialEq)]
pub struct TimeSeriesAppend {
    timestamp: i64,
    values: Vec<f64>,
    tag: Option<String>,
}

impl TimeSeriesAppend {
    /// Creates one append. Crate-only: callers validate appends through the
    /// batch constructor.
    pub(crate) fn new(timestamp: i64, values: Vec<f64>, tag: Option<String>) -> Self {
        Self { timestamp, values, tag }
    }

    /// Returns the append timestamp.
    #[must_use]
    pub fn timestamp(&self) -> i64 {
        self.timestamp
    }

    /// Returns the appended values.
    #[must_use]
    pub fn values(&self) -> &[f64] {
        &self.values
    }

    /// Returns the optional tag.
    #[must_use]
    pub fn tag(&self) -> Option<&str> {
        self.tag.as_deref()
    }
}

/// One typed time-series deletion.
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum TimeSeriesDelete {
    /// Delete one timestamp.
    At {
        /// The timestamp to delete.
        timestamp: i64,
    },
    /// Delete an inclusive range. The store validates the bounds.
    Range {
        /// The inclusive start.
        start: i64,
        /// The inclusive end.
        end: i64,
    },
}

/// One named rollup policy of a series configuration.
#[derive(Clone, Debug, PartialEq)]
pub struct RollupPolicy {
    name: String,
    aggregation: Aggregation,
    window_ms: i64,
}

impl RollupPolicy {
    /// Creates one rollup policy. Crate-only: callers validate rollups
    /// through the series-policy constructor.
    pub(crate) fn new(name: String, aggregation: Aggregation, window_ms: i64) -> Self {
        Self { name, aggregation, window_ms }
    }

    /// Returns the rollup name.
    #[must_use]
    pub fn name(&self) -> &str {
        &self.name
    }

    /// Returns the aggregation.
    #[must_use]
    pub fn aggregation(&self) -> Aggregation {
        self.aggregation
    }

    /// Returns the aggregation window.
    #[must_use]
    pub fn window_ms(&self) -> i64 {
        self.window_ms
    }
}

/// The aggregation of one rollup policy.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Aggregation {
    /// Smallest value in the window.
    Min,
    /// Largest value in the window.
    Max,
    /// Sum of the window values.
    Sum,
    /// Mean of the window values.
    Mean,
    /// Count of the window points.
    Count,
}

/// The logical retention and rollup configuration of one series.
///
/// W3-F stores, validates, and reports this configuration. Rollup
/// generation and retention cleanup execution belong to the streaming
/// qualification packet.
#[derive(Clone, Debug, Default, PartialEq)]
pub struct TimeSeriesPolicy {
    retention_ms: Option<i64>,
    rollups: Vec<RollupPolicy>,
}

impl TimeSeriesPolicy {
    /// Creates a series configuration.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidFamilyPayload`] for an
    /// invalid rollup name.
    pub fn try_new(
        retention_ms: Option<i64>,
        rollups: Vec<RollupPolicy>,
    ) -> Result<Self, RequestValidationError> {
        for rollup in &rollups {
            validate_name(&rollup.name)?;
        }
        Ok(Self { retention_ms, rollups })
    }

    /// Returns the retention window.
    #[must_use]
    pub fn retention_ms(&self) -> Option<i64> {
        self.retention_ms
    }

    /// Returns the named rollup policies.
    #[must_use]
    pub fn rollups(&self) -> &[RollupPolicy] {
        &self.rollups
    }
}

/// A typed time-series batch: appends, deletions, and an optional policy
/// update for one named series of a document.
#[derive(Clone, Debug, PartialEq)]
pub struct TimeSeriesBatchWrite {
    scope: TenantScope,
    document: DocumentId,
    name: String,
    appends: Vec<TimeSeriesAppend>,
    deletes: Vec<TimeSeriesDelete>,
    policy: Option<TimeSeriesPolicy>,
    expected: ExpectedVersion,
    operation_id: OperationId,
    deadline: Deadline,
    cancellation: Cancellation,
}

impl TimeSeriesBatchWrite {
    /// Creates a validated time-series batch.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidFamilyPayload`] for an empty
    /// batch or an invalid series name,
    /// [`RequestValidationError::InvalidDeadline`] for an elapsed deadline,
    /// and [`RequestValidationError::InvalidCancellation`] for an already
    /// cancelled signal.
    #[allow(clippy::too_many_arguments)]
    pub fn try_new(
        scope: TenantScope,
        document: DocumentId,
        name: String,
        appends: Vec<TimeSeriesAppend>,
        deletes: Vec<TimeSeriesDelete>,
        policy: Option<TimeSeriesPolicy>,
        expected: ExpectedVersion,
        operation_id: OperationId,
        deadline: Deadline,
        cancellation: Cancellation,
    ) -> Result<Self, RequestValidationError> {
        validate_name(&name)?;
        if appends.is_empty() && deletes.is_empty() && policy.is_none() {
            return Err(RequestValidationError::InvalidFamilyPayload);
        }
        validate_deadline(&deadline)?;
        validate_cancellation(&cancellation)?;
        Ok(Self {
            scope,
            document,
            name,
            appends,
            deletes,
            policy,
            expected,
            operation_id,
            deadline,
            cancellation,
        })
    }

    /// Returns the validated tenant scope.
    #[must_use]
    pub fn tenant_scope(&self) -> &TenantScope {
        &self.scope
    }

    /// Returns the owning document identity.
    #[must_use]
    pub fn document(&self) -> &DocumentId {
        &self.document
    }

    /// Returns the series name.
    #[must_use]
    pub fn name(&self) -> &str {
        &self.name
    }

    /// Returns the appends in request order.
    #[must_use]
    pub fn appends(&self) -> &[TimeSeriesAppend] {
        &self.appends
    }

    /// Returns the deletions in request order.
    #[must_use]
    pub fn deletes(&self) -> &[TimeSeriesDelete] {
        &self.deletes
    }

    /// Returns the requested policy update.
    #[must_use]
    pub fn policy(&self) -> Option<&TimeSeriesPolicy> {
        self.policy.as_ref()
    }

    /// Returns the expected owner-document version.
    #[must_use]
    pub fn expected(&self) -> ExpectedVersion {
        self.expected
    }

    /// Returns the stable operation identity.
    #[must_use]
    pub fn operation_id(&self) -> &OperationId {
        &self.operation_id
    }

    /// Returns the request deadline.
    #[must_use]
    pub fn deadline(&self) -> Deadline {
        self.deadline
    }

    /// Returns a clone of the cancellation signal.
    #[must_use]
    pub fn cancellation(&self) -> Cancellation {
        self.cancellation.clone()
    }
}

/// One time-series point.
#[derive(Clone, Debug, PartialEq)]
pub struct TimeSeriesEntry {
    timestamp: i64,
    values: Vec<f64>,
    tag: Option<String>,
}

impl TimeSeriesEntry {
    /// Creates one entry. Crate-only.
    pub(crate) fn new(timestamp: i64, values: Vec<f64>, tag: Option<String>) -> Self {
        Self { timestamp, values, tag }
    }

    /// Returns the entry timestamp.
    #[must_use]
    pub fn timestamp(&self) -> i64 {
        self.timestamp
    }

    /// Returns the entry values.
    #[must_use]
    pub fn values(&self) -> &[f64] {
        &self.values
    }

    /// Returns the optional tag.
    #[must_use]
    pub fn tag(&self) -> Option<&str> {
        self.tag.as_deref()
    }
}

/// One bounded page of a time-series read.
#[derive(Clone, Debug, PartialEq)]
pub struct TimeSeriesPage {
    entries: Vec<TimeSeriesEntry>,
    policy: Option<TimeSeriesPolicy>,
}

impl TimeSeriesPage {
    /// Creates one page. Crate-only.
    pub(crate) fn new(entries: Vec<TimeSeriesEntry>, policy: Option<TimeSeriesPolicy>) -> Self {
        Self { entries, policy }
    }

    /// Returns the entries in timestamp order.
    #[must_use]
    pub fn entries(&self) -> &[TimeSeriesEntry] {
        &self.entries
    }

    /// Returns the series configuration.
    #[must_use]
    pub fn policy(&self) -> Option<&TimeSeriesPolicy> {
        self.policy.as_ref()
    }
}

// ---------------------------------------------------------------------------
// Revision payloads
// ---------------------------------------------------------------------------

/// The paging of one revision-page read.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct RevisionPaging {
    offset: u64,
    limit: u32,
}

impl RevisionPaging {
    /// Creates validated paging.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidRange`] for a zero limit.
    pub fn try_new(offset: u64, limit: u32) -> Result<Self, RequestValidationError> {
        if limit == 0 {
            return Err(RequestValidationError::InvalidRange);
        }
        Ok(Self { offset, limit })
    }

    /// Returns the page offset.
    #[must_use]
    pub fn offset(&self) -> u64 {
        self.offset
    }

    /// Returns the page size.
    #[must_use]
    pub fn limit(&self) -> u32 {
        self.limit
    }
}

/// A typed revision-page read for one document.
#[derive(Clone, Debug, PartialEq)]
pub struct RevisionRead {
    document: DocumentId,
    paging: RevisionPaging,
    deadline: Deadline,
    cancellation: Cancellation,
}

impl RevisionRead {
    /// Creates a validated revision read.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidDeadline`] for an elapsed
    /// deadline and [`RequestValidationError::InvalidCancellation`] for an
    /// already cancelled signal.
    pub fn try_new(
        document: DocumentId,
        paging: RevisionPaging,
        deadline: Deadline,
        cancellation: Cancellation,
    ) -> Result<Self, RequestValidationError> {
        validate_deadline(&deadline)?;
        validate_cancellation(&cancellation)?;
        Ok(Self { document, paging, deadline, cancellation })
    }

    /// Returns the document identity.
    #[must_use]
    pub fn document(&self) -> &DocumentId {
        &self.document
    }

    /// Returns the requested paging.
    #[must_use]
    pub fn paging(&self) -> RevisionPaging {
        self.paging
    }

    /// Returns the request deadline.
    #[must_use]
    pub fn deadline(&self) -> Deadline {
        self.deadline
    }

    /// Returns a clone of the cancellation signal.
    #[must_use]
    pub fn cancellation(&self) -> Cancellation {
        self.cancellation.clone()
    }
}

/// The retention limits of one document's revision log.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct RevisionPolicy {
    minimum_revisions: u32,
    maximum_revisions: Option<u32>,
}

impl RevisionPolicy {
    /// Creates a validated revision policy.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidFamilyPayload`] for a zero
    /// minimum or a maximum below the minimum.
    pub fn try_new(
        minimum_revisions: u32,
        maximum_revisions: Option<u32>,
    ) -> Result<Self, RequestValidationError> {
        if minimum_revisions == 0 {
            return Err(RequestValidationError::InvalidFamilyPayload);
        }
        if maximum_revisions.is_some_and(|maximum| maximum < minimum_revisions) {
            return Err(RequestValidationError::InvalidFamilyPayload);
        }
        Ok(Self { minimum_revisions, maximum_revisions })
    }

    /// Returns the minimum number of revisions to keep.
    #[must_use]
    pub fn minimum_revisions(&self) -> u32 {
        self.minimum_revisions
    }

    /// Returns the maximum number of revisions to keep.
    #[must_use]
    pub fn maximum_revisions(&self) -> Option<u32> {
        self.maximum_revisions
    }
}

/// One typed revision command.
#[derive(Clone, Debug, PartialEq)]
pub enum RevisionCommand {
    /// Snapshot the document's current value as one new revision.
    ForceCreate {
        /// The expected owner-document version.
        expected: ExpectedVersion,
    },
    /// Restore an existing revision's value as a new revision.
    Revert {
        /// The revision to restore.
        to_revision: u64,
    },
    /// Configure the document's revision retention policy.
    Configure {
        /// The retention policy.
        policy: RevisionPolicy,
    },
}

/// A typed revision write bound to one document.
#[derive(Clone, Debug, PartialEq)]
pub struct RevisionWrite {
    scope: TenantScope,
    document: DocumentId,
    command: RevisionCommand,
    operation_id: OperationId,
    deadline: Deadline,
    cancellation: Cancellation,
}

impl RevisionWrite {
    /// Creates a validated revision command.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidDeadline`] for an elapsed
    /// deadline and [`RequestValidationError::InvalidCancellation`] for an
    /// already cancelled signal.
    pub fn try_new(
        scope: TenantScope,
        document: DocumentId,
        command: RevisionCommand,
        operation_id: OperationId,
        deadline: Deadline,
        cancellation: Cancellation,
    ) -> Result<Self, RequestValidationError> {
        validate_deadline(&deadline)?;
        validate_cancellation(&cancellation)?;
        Ok(Self { scope, document, command, operation_id, deadline, cancellation })
    }

    /// Returns the validated tenant scope.
    #[must_use]
    pub fn tenant_scope(&self) -> &TenantScope {
        &self.scope
    }

    /// Returns the document identity.
    #[must_use]
    pub fn document(&self) -> &DocumentId {
        &self.document
    }

    /// Returns the typed command.
    #[must_use]
    pub fn command(&self) -> &RevisionCommand {
        &self.command
    }

    /// Returns the stable operation identity.
    #[must_use]
    pub fn operation_id(&self) -> &OperationId {
        &self.operation_id
    }

    /// Returns the request deadline.
    #[must_use]
    pub fn deadline(&self) -> Deadline {
        self.deadline
    }

    /// Returns a clone of the cancellation signal.
    #[must_use]
    pub fn cancellation(&self) -> Cancellation {
        self.cancellation.clone()
    }
}

/// One stored revision.
#[derive(Clone, Debug, PartialEq)]
pub struct RevisionEntry {
    revision: u64,
    at: i64,
    value: DocumentValue,
}

impl RevisionEntry {
    /// Creates one revision entry. Crate-only.
    pub(crate) fn new(revision: u64, at: i64, value: DocumentValue) -> Self {
        Self { revision, at, value }
    }

    /// Returns the monotonic revision number.
    #[must_use]
    pub fn revision(&self) -> u64 {
        self.revision
    }

    /// Returns the logical recording time.
    #[must_use]
    pub fn at(&self) -> i64 {
        self.at
    }

    /// Returns the revisioned document value.
    #[must_use]
    pub fn value(&self) -> &DocumentValue {
        &self.value
    }
}

/// One page of a document's revision log.
#[derive(Clone, Debug, PartialEq)]
pub struct RevisionPage {
    entries: Vec<RevisionEntry>,
    total: u64,
    policy: Option<RevisionPolicy>,
}

impl RevisionPage {
    /// Creates one revision page. Crate-only.
    pub(crate) fn new(
        entries: Vec<RevisionEntry>,
        total: u64,
        policy: Option<RevisionPolicy>,
    ) -> Self {
        Self { entries, total, policy }
    }

    /// Returns the page entries in revision order.
    #[must_use]
    pub fn entries(&self) -> &[RevisionEntry] {
        &self.entries
    }

    /// Returns the total number of stored revisions.
    #[must_use]
    pub fn total(&self) -> u64 {
        self.total
    }

    /// Returns the document's retention policy.
    #[must_use]
    pub fn policy(&self) -> Option<RevisionPolicy> {
        self.policy
    }
}

// ---------------------------------------------------------------------------
// Compare-exchange payloads
// ---------------------------------------------------------------------------

/// A typed compare-exchange read of one key.
#[derive(Clone, Debug, PartialEq)]
pub struct CompareExchangeRead {
    key: String,
    deadline: Deadline,
    cancellation: Cancellation,
}

impl CompareExchangeRead {
    /// Creates a validated compare-exchange read.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidFamilyPayload`] for an
    /// invalid key, [`RequestValidationError::InvalidDeadline`] for an
    /// elapsed deadline, and
    /// [`RequestValidationError::InvalidCancellation`] for an already
    /// cancelled signal.
    pub fn try_new(
        key: String,
        deadline: Deadline,
        cancellation: Cancellation,
    ) -> Result<Self, RequestValidationError> {
        validate_name(&key)?;
        validate_deadline(&deadline)?;
        validate_cancellation(&cancellation)?;
        Ok(Self { key, deadline, cancellation })
    }

    /// Returns the compare-exchange key.
    #[must_use]
    pub fn key(&self) -> &str {
        &self.key
    }

    /// Returns the request deadline.
    #[must_use]
    pub fn deadline(&self) -> Deadline {
        self.deadline
    }

    /// Returns a clone of the cancellation signal.
    #[must_use]
    pub fn cancellation(&self) -> Cancellation {
        self.cancellation.clone()
    }
}

/// One typed compare-exchange condition.
#[derive(Clone, Debug, PartialEq)]
pub enum CompareExchangeCondition {
    /// Store a value only when the key's current index matches.
    PutIfIndex {
        /// The required current index.
        index: u64,
        /// The value to store.
        value: DocumentValue,
        /// The optional expiry of the stored value.
        expires_at: Option<i64>,
    },
    /// Store a value only when the key is missing or expired.
    PutIfMissing {
        /// The value to store.
        value: DocumentValue,
        /// The optional expiry of the stored value.
        expires_at: Option<i64>,
    },
    /// Delete the value only when the key's current index matches.
    DeleteIfIndex {
        /// The required current index.
        index: u64,
    },
}

/// A typed compare-exchange write for one key.
#[derive(Clone, Debug, PartialEq)]
pub struct CompareExchangeWrite {
    scope: TenantScope,
    key: String,
    condition: CompareExchangeCondition,
    operation_id: OperationId,
    deadline: Deadline,
    cancellation: Cancellation,
}

impl CompareExchangeWrite {
    /// Creates a validated compare-exchange condition.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidFamilyPayload`] for an
    /// invalid key or a negative expiry,
    /// [`RequestValidationError::InvalidDeadline`] for an elapsed deadline,
    /// and [`RequestValidationError::InvalidCancellation`] for an already
    /// cancelled signal.
    pub fn try_new(
        scope: TenantScope,
        key: String,
        condition: CompareExchangeCondition,
        operation_id: OperationId,
        deadline: Deadline,
        cancellation: Cancellation,
    ) -> Result<Self, RequestValidationError> {
        validate_name(&key)?;
        match &condition {
            CompareExchangeCondition::PutIfIndex { expires_at, .. }
            | CompareExchangeCondition::PutIfMissing { expires_at, .. } => {
                if let Some(at) = expires_at {
                    validate_epoch_millis(*at)?;
                }
            }
            CompareExchangeCondition::DeleteIfIndex { .. } => {}
        }
        validate_deadline(&deadline)?;
        validate_cancellation(&cancellation)?;
        Ok(Self { scope, key, condition, operation_id, deadline, cancellation })
    }

    /// Returns the validated tenant scope.
    #[must_use]
    pub fn tenant_scope(&self) -> &TenantScope {
        &self.scope
    }

    /// Returns the compare-exchange key.
    #[must_use]
    pub fn key(&self) -> &str {
        &self.key
    }

    /// Returns the typed condition.
    #[must_use]
    pub fn condition(&self) -> &CompareExchangeCondition {
        &self.condition
    }

    /// Returns the stable operation identity.
    #[must_use]
    pub fn operation_id(&self) -> &OperationId {
        &self.operation_id
    }

    /// Returns the request deadline.
    #[must_use]
    pub fn deadline(&self) -> Deadline {
        self.deadline
    }

    /// Returns a clone of the cancellation signal.
    #[must_use]
    pub fn cancellation(&self) -> Cancellation {
        self.cancellation.clone()
    }
}

/// One compare-exchange value with its index.
#[derive(Clone, Debug, PartialEq)]
pub struct CompareExchangeEntry {
    value: DocumentValue,
    index: u64,
    expires_at: Option<i64>,
}

impl CompareExchangeEntry {
    /// Creates one entry. Crate-only.
    pub(crate) fn new(value: DocumentValue, index: u64, expires_at: Option<i64>) -> Self {
        Self { value, index, expires_at }
    }

    /// Returns the stored value.
    #[must_use]
    pub fn value(&self) -> &DocumentValue {
        &self.value
    }

    /// Returns the value's monotonic index.
    #[must_use]
    pub fn index(&self) -> u64 {
        self.index
    }

    /// Returns the optional expiry.
    #[must_use]
    pub fn expires_at(&self) -> Option<i64> {
        self.expires_at
    }
}

/// The read result of one compare-exchange key.
#[derive(Clone, Debug, PartialEq)]
pub struct CompareExchangeValueResult {
    key: String,
    entry: Option<CompareExchangeEntry>,
}

impl CompareExchangeValueResult {
    /// Creates one read result. Crate-only.
    pub(crate) fn new(key: String, entry: Option<CompareExchangeEntry>) -> Self {
        Self { key, entry }
    }

    /// Returns the compare-exchange key.
    #[must_use]
    pub fn key(&self) -> &str {
        &self.key
    }

    /// Returns the live entry, or `None` when the key is missing, deleted,
    /// or expired.
    #[must_use]
    pub fn entry(&self) -> Option<&CompareExchangeEntry> {
        self.entry.as_ref()
    }
}

// ---------------------------------------------------------------------------
// Identity payloads
// ---------------------------------------------------------------------------

/// A typed identity-range read.
#[derive(Clone, Debug, PartialEq)]
pub struct IdentityRead {
    names: Vec<String>,
    deadline: Deadline,
    cancellation: Cancellation,
}

impl IdentityRead {
    /// Creates a validated identity read.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidFamilyPayload`] for an empty
    /// request, an invalid name, or a duplicated name,
    /// [`RequestValidationError::InvalidDeadline`] for an elapsed deadline,
    /// and [`RequestValidationError::InvalidCancellation`] for an already
    /// cancelled signal.
    pub fn try_new(
        names: Vec<String>,
        deadline: Deadline,
        cancellation: Cancellation,
    ) -> Result<Self, RequestValidationError> {
        if names.is_empty() {
            return Err(RequestValidationError::InvalidFamilyPayload);
        }
        for name in &names {
            validate_name(name)?;
        }
        let mut unique = names.clone();
        unique.sort_unstable();
        if unique.windows(2).any(|pair| pair[0] == pair[1]) {
            return Err(RequestValidationError::InvalidFamilyPayload);
        }
        validate_deadline(&deadline)?;
        validate_cancellation(&cancellation)?;
        Ok(Self { names, deadline, cancellation })
    }

    /// Returns the requested identity names.
    #[must_use]
    pub fn names(&self) -> &[String] {
        &self.names
    }

    /// Returns the request deadline.
    #[must_use]
    pub fn deadline(&self) -> Deadline {
        self.deadline
    }

    /// Returns a clone of the cancellation signal.
    #[must_use]
    pub fn cancellation(&self) -> Cancellation {
        self.cancellation.clone()
    }
}

/// One typed identity command.
#[derive(Clone, Debug, PartialEq)]
pub enum IdentityCommand {
    /// Advance one identity by a positive count. The allocated range becomes
    /// readable after the containing transaction commits; staging returns no
    /// value by contract.
    Allocate {
        /// The identity name.
        name: String,
        /// The number of values to allocate.
        count: u64,
    },
    /// Move one identity to a target value. Seeding backwards requires
    /// `force`.
    Seed {
        /// The identity name.
        name: String,
        /// The target next value.
        value: u64,
        /// Whether a backwards move is allowed.
        force: bool,
    },
}

impl IdentityCommand {
    /// Returns the identity name this command touches.
    #[must_use]
    pub fn name(&self) -> &str {
        match self {
            Self::Allocate { name, .. } | Self::Seed { name, .. } => name,
        }
    }
}

/// A typed identity allocation or seeding request.
#[derive(Clone, Debug, PartialEq)]
pub struct IdentityWrite {
    scope: TenantScope,
    name: String,
    command: IdentityCommand,
    operation_id: OperationId,
    deadline: Deadline,
    cancellation: Cancellation,
}

impl IdentityWrite {
    /// Creates a validated identity request.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidFamilyPayload`] for an
    /// invalid name or a zero allocation,
    /// [`RequestValidationError::InvalidDeadline`] for an elapsed deadline,
    /// and [`RequestValidationError::InvalidCancellation`] for an already
    /// cancelled signal.
    pub fn try_new(
        scope: TenantScope,
        name: String,
        command: IdentityCommand,
        operation_id: OperationId,
        deadline: Deadline,
        cancellation: Cancellation,
    ) -> Result<Self, RequestValidationError> {
        validate_name(&name)?;
        if name != command.name() {
            return Err(RequestValidationError::InvalidFamilyPayload);
        }
        if let IdentityCommand::Allocate { count, .. } = command
            && count == 0
        {
            return Err(RequestValidationError::InvalidFamilyPayload);
        }
        validate_deadline(&deadline)?;
        validate_cancellation(&cancellation)?;
        Ok(Self { scope, name, command, operation_id, deadline, cancellation })
    }

    /// Returns the validated tenant scope.
    #[must_use]
    pub fn tenant_scope(&self) -> &TenantScope {
        &self.scope
    }

    /// Returns the identity name.
    #[must_use]
    pub fn name(&self) -> &str {
        &self.name
    }

    /// Returns the typed command.
    #[must_use]
    pub fn command(&self) -> &IdentityCommand {
        &self.command
    }

    /// Returns the stable operation identity.
    #[must_use]
    pub fn operation_id(&self) -> &OperationId {
        &self.operation_id
    }

    /// Returns the request deadline.
    #[must_use]
    pub fn deadline(&self) -> Deadline {
        self.deadline
    }

    /// Returns a clone of the cancellation signal.
    #[must_use]
    pub fn cancellation(&self) -> Cancellation {
        self.cancellation.clone()
    }
}

/// One identity with its next value.
#[derive(Clone, Debug, PartialEq)]
pub struct IdentityEntry {
    name: String,
    next: u64,
}

impl IdentityEntry {
    /// Creates one identity entry. Crate-only.
    pub(crate) fn new(name: String, next: u64) -> Self {
        Self { name, next }
    }

    /// Returns the identity name.
    #[must_use]
    pub fn name(&self) -> &str {
        &self.name
    }

    /// Returns the next unallocated value.
    #[must_use]
    pub fn next(&self) -> u64 {
        self.next
    }
}

/// The read result of one identity range.
#[derive(Clone, Debug, Default, PartialEq)]
pub struct IdentityRangeResult {
    entries: Vec<IdentityEntry>,
}

impl IdentityRangeResult {
    /// Creates one identity-range result. Crate-only.
    pub(crate) fn new(entries: Vec<IdentityEntry>) -> Self {
        Self { entries }
    }

    /// Returns the identity entries.
    #[must_use]
    pub fn entries(&self) -> &[IdentityEntry] {
        &self.entries
    }
}

// ---------------------------------------------------------------------------
// Lifecycle payloads
// ---------------------------------------------------------------------------

/// A typed lifecycle read of one document's expiry and refresh state.
#[derive(Clone, Debug, PartialEq)]
pub struct LifecycleRead {
    document: DocumentId,
    deadline: Deadline,
    cancellation: Cancellation,
}

impl LifecycleRead {
    /// Creates a validated lifecycle read.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidDeadline`] for an elapsed
    /// deadline and [`RequestValidationError::InvalidCancellation`] for an
    /// already cancelled signal.
    pub fn try_new(
        document: DocumentId,
        deadline: Deadline,
        cancellation: Cancellation,
    ) -> Result<Self, RequestValidationError> {
        validate_deadline(&deadline)?;
        validate_cancellation(&cancellation)?;
        Ok(Self { document, deadline, cancellation })
    }

    /// Returns the document identity.
    #[must_use]
    pub fn document(&self) -> &DocumentId {
        &self.document
    }

    /// Returns the request deadline.
    #[must_use]
    pub fn deadline(&self) -> Deadline {
        self.deadline
    }

    /// Returns a clone of the cancellation signal.
    #[must_use]
    pub fn cancellation(&self) -> Cancellation {
        self.cancellation.clone()
    }
}

/// One typed lifecycle update.
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum LifecycleCommand {
    /// Configure or clear the document's expiry time.
    SetExpiration {
        /// The expiry time in epoch milliseconds, or `None` to clear.
        at: Option<i64>,
    },
    /// Configure or clear the document's refresh time.
    SetRefresh {
        /// The refresh time in epoch milliseconds, or `None` to clear.
        at: Option<i64>,
    },
    /// Clear both configured times.
    Clear,
}

/// A typed lifecycle write bound to one document.
#[derive(Clone, Debug, PartialEq)]
pub struct LifecycleWrite {
    scope: TenantScope,
    document: DocumentId,
    command: LifecycleCommand,
    expected: ExpectedVersion,
    operation_id: OperationId,
    deadline: Deadline,
    cancellation: Cancellation,
}

impl LifecycleWrite {
    /// Creates a validated lifecycle update.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidDeadline`] for an elapsed
    /// deadline and [`RequestValidationError::InvalidCancellation`] for an
    /// already cancelled signal.
    pub fn try_new(
        scope: TenantScope,
        document: DocumentId,
        command: LifecycleCommand,
        expected: ExpectedVersion,
        operation_id: OperationId,
        deadline: Deadline,
        cancellation: Cancellation,
    ) -> Result<Self, RequestValidationError> {
        validate_deadline(&deadline)?;
        validate_cancellation(&cancellation)?;
        Ok(Self { scope, document, command, expected, operation_id, deadline, cancellation })
    }

    /// Returns the validated tenant scope.
    #[must_use]
    pub fn tenant_scope(&self) -> &TenantScope {
        &self.scope
    }

    /// Returns the document identity.
    #[must_use]
    pub fn document(&self) -> &DocumentId {
        &self.document
    }

    /// Returns the typed command.
    #[must_use]
    pub fn command(&self) -> LifecycleCommand {
        self.command
    }

    /// Returns the expected owner-document version.
    #[must_use]
    pub fn expected(&self) -> ExpectedVersion {
        self.expected
    }

    /// Returns the stable operation identity.
    #[must_use]
    pub fn operation_id(&self) -> &OperationId {
        &self.operation_id
    }

    /// Returns the request deadline.
    #[must_use]
    pub fn deadline(&self) -> Deadline {
        self.deadline
    }

    /// Returns a clone of the cancellation signal.
    #[must_use]
    pub fn cancellation(&self) -> Cancellation {
        self.cancellation.clone()
    }
}

/// The lifecycle state of one document.
#[derive(Clone, Debug, PartialEq)]
pub struct LifecycleResult {
    document: DocumentId,
    present: bool,
    expires_at: Option<i64>,
    refresh_at: Option<i64>,
    expired: bool,
}

impl LifecycleResult {
    /// Creates one lifecycle result. Crate-only.
    pub(crate) fn new(
        document: DocumentId,
        present: bool,
        expires_at: Option<i64>,
        refresh_at: Option<i64>,
        expired: bool,
    ) -> Self {
        Self { document, present, expires_at, refresh_at, expired }
    }

    /// Returns the document identity.
    #[must_use]
    pub fn document(&self) -> &DocumentId {
        &self.document
    }

    /// Returns whether the document exists in this snapshot.
    #[must_use]
    pub fn present(&self) -> bool {
        self.present
    }

    /// Returns the configured expiry time.
    #[must_use]
    pub fn expires_at(&self) -> Option<i64> {
        self.expires_at
    }

    /// Returns the configured refresh time.
    #[must_use]
    pub fn refresh_at(&self) -> Option<i64> {
        self.refresh_at
    }

    /// Returns whether the configured expiry time has elapsed.
    #[must_use]
    pub fn expired(&self) -> bool {
        self.expired
    }
}

// ---------------------------------------------------------------------------
// Archival payloads
// ---------------------------------------------------------------------------

/// A typed archival read of one document's archival state.
///
/// Archival transfer and restore qualification belong to the later
/// backup and migration packets; W3-F records the typed state only.
#[derive(Clone, Debug, PartialEq)]
pub struct ArchivalReadRequest {
    document: DocumentId,
    deadline: Deadline,
    cancellation: Cancellation,
}

impl ArchivalReadRequest {
    /// Creates a validated archival read.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidDeadline`] for an elapsed
    /// deadline and [`RequestValidationError::InvalidCancellation`] for an
    /// already cancelled signal.
    pub fn try_new(
        document: DocumentId,
        deadline: Deadline,
        cancellation: Cancellation,
    ) -> Result<Self, RequestValidationError> {
        validate_deadline(&deadline)?;
        validate_cancellation(&cancellation)?;
        Ok(Self { document, deadline, cancellation })
    }

    /// Returns the document identity.
    #[must_use]
    pub fn document(&self) -> &DocumentId {
        &self.document
    }

    /// Returns the request deadline.
    #[must_use]
    pub fn deadline(&self) -> Deadline {
        self.deadline
    }

    /// Returns a clone of the cancellation signal.
    #[must_use]
    pub fn cancellation(&self) -> Cancellation {
        self.cancellation.clone()
    }
}

/// One typed archival update.
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum ArchivalCommand {
    /// Configure the archival delay of the document.
    SetPolicy {
        /// The delay before archival, in milliseconds.
        after_ms: i64,
    },
    /// Mark the document archived at a logical time.
    Archive {
        /// The archival time in epoch milliseconds.
        at: i64,
    },
    /// Mark the document not archived.
    Restore,
}

/// A typed archival write bound to one document.
#[derive(Clone, Debug, PartialEq)]
pub struct ArchivalWrite {
    scope: TenantScope,
    document: DocumentId,
    command: ArchivalCommand,
    operation_id: OperationId,
    deadline: Deadline,
    cancellation: Cancellation,
}

impl ArchivalWrite {
    /// Creates a validated archival update.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidDeadline`] for an elapsed
    /// deadline and [`RequestValidationError::InvalidCancellation`] for an
    /// already cancelled signal.
    pub fn try_new(
        scope: TenantScope,
        document: DocumentId,
        command: ArchivalCommand,
        operation_id: OperationId,
        deadline: Deadline,
        cancellation: Cancellation,
    ) -> Result<Self, RequestValidationError> {
        validate_deadline(&deadline)?;
        validate_cancellation(&cancellation)?;
        Ok(Self { scope, document, command, operation_id, deadline, cancellation })
    }

    /// Returns the validated tenant scope.
    #[must_use]
    pub fn tenant_scope(&self) -> &TenantScope {
        &self.scope
    }

    /// Returns the document identity.
    #[must_use]
    pub fn document(&self) -> &DocumentId {
        &self.document
    }

    /// Returns the typed command.
    #[must_use]
    pub fn command(&self) -> ArchivalCommand {
        self.command
    }

    /// Returns the stable operation identity.
    #[must_use]
    pub fn operation_id(&self) -> &OperationId {
        &self.operation_id
    }

    /// Returns the request deadline.
    #[must_use]
    pub fn deadline(&self) -> Deadline {
        self.deadline
    }

    /// Returns a clone of the cancellation signal.
    #[must_use]
    pub fn cancellation(&self) -> Cancellation {
        self.cancellation.clone()
    }
}

/// The archival state of one document.
#[derive(Clone, Debug, PartialEq)]
pub struct ArchivalState {
    archived: bool,
    at: Option<i64>,
    after_ms: Option<i64>,
}

impl ArchivalState {
    /// Creates one archival state. Crate-only.
    pub(crate) fn new(archived: bool, at: Option<i64>, after_ms: Option<i64>) -> Self {
        Self { archived, at, after_ms }
    }

    /// Returns whether the document is marked archived.
    #[must_use]
    pub fn archived(&self) -> bool {
        self.archived
    }

    /// Returns the archival time.
    #[must_use]
    pub fn at(&self) -> Option<i64> {
        self.at
    }

    /// Returns the configured archival delay.
    #[must_use]
    pub fn after_ms(&self) -> Option<i64> {
        self.after_ms
    }
}

/// The read result of one archival state.
#[derive(Clone, Debug, PartialEq)]
pub struct ArchivalReadResult {
    document: DocumentId,
    state: Option<ArchivalState>,
}

impl ArchivalReadResult {
    /// Creates one archival read result. Crate-only.
    pub(crate) fn new(document: DocumentId, state: Option<ArchivalState>) -> Self {
        Self { document, state }
    }

    /// Returns the document identity.
    #[must_use]
    pub fn document(&self) -> &DocumentId {
        &self.document
    }

    /// Returns the archival state, or `None` when none is configured.
    #[must_use]
    pub fn state(&self) -> Option<&ArchivalState> {
        self.state.as_ref()
    }
}

// ---------------------------------------------------------------------------
// Schema-validation payloads
// ---------------------------------------------------------------------------

/// The logical type of one document field.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum SchemaFieldType {
    /// A boolean value.
    Boolean,
    /// A signed integer value.
    Integer,
    /// A floating-point number.
    Number,
    /// A text value.
    Text,
    /// An object value.
    Object,
    /// An array value.
    Array,
}

/// One closed schema rule of a collection's validation policy.
#[derive(Clone, Debug, PartialEq)]
pub enum SchemaRule {
    /// The field must be present.
    Required {
        /// The field name.
        field: String,
    },
    /// The field must carry the named logical type.
    FieldType {
        /// The field name.
        field: String,
        /// The required logical type.
        kind: SchemaFieldType,
    },
    /// The field must be one of the listed text values.
    EnumValues {
        /// The field name.
        field: String,
        /// The accepted text values.
        values: Vec<String>,
    },
    /// The field must be an integer inside the bounds.
    NumberRange {
        /// The field name.
        field: String,
        /// The inclusive lower bound.
        min: Option<i64>,
        /// The inclusive upper bound.
        max: Option<i64>,
    },
    /// The field must be a text of bounded length.
    TextLength {
        /// The field name.
        field: String,
        /// The inclusive minimum length.
        min: Option<u64>,
        /// The inclusive maximum length.
        max: Option<u64>,
    },
}

impl SchemaRule {
    /// Returns the ruled field name.
    #[must_use]
    pub fn field(&self) -> &str {
        match self {
            Self::Required { field }
            | Self::FieldType { field, .. }
            | Self::EnumValues { field, .. }
            | Self::NumberRange { field, .. }
            | Self::TextLength { field, .. } => field,
        }
    }
}

/// A collection's schema-validation policy.
#[derive(Clone, Debug, Default, PartialEq)]
pub struct SchemaDefinition {
    rules: Vec<SchemaRule>,
}

impl SchemaDefinition {
    /// Creates one policy from closed rules.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidFamilyPayload`] for an empty
    /// field name.
    pub fn try_new(rules: Vec<SchemaRule>) -> Result<Self, RequestValidationError> {
        for rule in &rules {
            validate_name(rule.field())?;
        }
        Ok(Self { rules })
    }

    /// Returns the policy rules.
    #[must_use]
    pub fn rules(&self) -> &[SchemaRule] {
        &self.rules
    }
}

/// A typed schema-validation read of one collection's policy, optionally
/// validating one stored document on demand.
#[derive(Clone, Debug, PartialEq)]
pub struct ValidationRead {
    collection: CollectionName,
    document: Option<DocumentId>,
    deadline: Deadline,
    cancellation: Cancellation,
}

impl ValidationRead {
    /// Creates a validated schema-validation read.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidDeadline`] for an elapsed
    /// deadline and [`RequestValidationError::InvalidCancellation`] for an
    /// already cancelled signal.
    pub fn try_new(
        collection: CollectionName,
        document: Option<DocumentId>,
        deadline: Deadline,
        cancellation: Cancellation,
    ) -> Result<Self, RequestValidationError> {
        validate_deadline(&deadline)?;
        validate_cancellation(&cancellation)?;
        Ok(Self { collection, document, deadline, cancellation })
    }

    /// Returns the collection name.
    #[must_use]
    pub fn collection(&self) -> &CollectionName {
        &self.collection
    }

    /// Returns the document validated on demand, if requested.
    #[must_use]
    pub fn document(&self) -> Option<&DocumentId> {
        self.document.as_ref()
    }

    /// Returns the request deadline.
    #[must_use]
    pub fn deadline(&self) -> Deadline {
        self.deadline
    }

    /// Returns a clone of the cancellation signal.
    #[must_use]
    pub fn cancellation(&self) -> Cancellation {
        self.cancellation.clone()
    }
}

/// A typed schema-validation policy update for one collection.
#[derive(Clone, Debug, PartialEq)]
pub struct ValidationWrite {
    scope: TenantScope,
    collection: CollectionName,
    schema: Option<SchemaDefinition>,
    operation_id: OperationId,
    deadline: Deadline,
    cancellation: Cancellation,
}

impl ValidationWrite {
    /// Creates a validated policy update. A `None` schema clears the
    /// collection's policy.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidDeadline`] for an elapsed
    /// deadline and [`RequestValidationError::InvalidCancellation`] for an
    /// already cancelled signal.
    pub fn try_new(
        scope: TenantScope,
        collection: CollectionName,
        schema: Option<SchemaDefinition>,
        operation_id: OperationId,
        deadline: Deadline,
        cancellation: Cancellation,
    ) -> Result<Self, RequestValidationError> {
        validate_deadline(&deadline)?;
        validate_cancellation(&cancellation)?;
        Ok(Self { scope, collection, schema, operation_id, deadline, cancellation })
    }

    /// Returns the validated tenant scope.
    #[must_use]
    pub fn tenant_scope(&self) -> &TenantScope {
        &self.scope
    }

    /// Returns the collection name.
    #[must_use]
    pub fn collection(&self) -> &CollectionName {
        &self.collection
    }

    /// Returns the requested policy, or `None` to clear it.
    #[must_use]
    pub fn schema(&self) -> Option<&SchemaDefinition> {
        self.schema.as_ref()
    }

    /// Returns the stable operation identity.
    #[must_use]
    pub fn operation_id(&self) -> &OperationId {
        &self.operation_id
    }

    /// Returns the request deadline.
    #[must_use]
    pub fn deadline(&self) -> Deadline {
        self.deadline
    }

    /// Returns a clone of the cancellation signal.
    #[must_use]
    pub fn cancellation(&self) -> Cancellation {
        self.cancellation.clone()
    }
}

/// Why one document field violates a policy rule.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ViolationKind {
    /// A required field is missing.
    MissingRequiredField,
    /// The field carries a different logical type.
    TypeMismatch,
    /// The field value is not accepted.
    ValueNotAllowed,
    /// The field value is outside the numeric bounds.
    NumberOutOfRange,
    /// The field value length is outside the bounds.
    LengthOutOfRange,
}

/// One policy violation of one document field.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct SchemaViolation {
    field: String,
    kind: ViolationKind,
}

impl SchemaViolation {
    /// Creates one violation. Crate-only.
    pub(crate) fn new(field: String, kind: ViolationKind) -> Self {
        Self { field, kind }
    }

    /// Returns the violated field name.
    #[must_use]
    pub fn field(&self) -> &str {
        &self.field
    }

    /// Returns the violation kind.
    #[must_use]
    pub fn kind(&self) -> ViolationKind {
        self.kind
    }
}

/// The read result of one schema-validation request.
#[derive(Clone, Debug, PartialEq)]
pub struct ValidationResult {
    collection: CollectionName,
    policy: Option<SchemaDefinition>,
    violations: Vec<SchemaViolation>,
}

impl ValidationResult {
    /// Creates one validation result. Crate-only.
    pub(crate) fn new(
        collection: CollectionName,
        policy: Option<SchemaDefinition>,
        violations: Vec<SchemaViolation>,
    ) -> Self {
        Self { collection, policy, violations }
    }

    /// Returns the collection name.
    #[must_use]
    pub fn collection(&self) -> &CollectionName {
        &self.collection
    }

    /// Returns the configured policy.
    #[must_use]
    pub fn policy(&self) -> Option<&SchemaDefinition> {
        self.policy.as_ref()
    }

    /// Returns the on-demand violations of the requested document.
    #[must_use]
    pub fn violations(&self) -> &[SchemaViolation] {
        &self.violations
    }
}

// ---------------------------------------------------------------------------
// Tombstone payloads
// ---------------------------------------------------------------------------

/// A typed tombstone read of one document's family tombstones.
#[derive(Clone, Debug, PartialEq)]
pub struct TombstoneRead {
    document: DocumentId,
    deadline: Deadline,
    cancellation: Cancellation,
}

impl TombstoneRead {
    /// Creates a validated tombstone read.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidDeadline`] for an elapsed
    /// deadline and [`RequestValidationError::InvalidCancellation`] for an
    /// already cancelled signal.
    pub fn try_new(
        document: DocumentId,
        deadline: Deadline,
        cancellation: Cancellation,
    ) -> Result<Self, RequestValidationError> {
        validate_deadline(&deadline)?;
        validate_cancellation(&cancellation)?;
        Ok(Self { document, deadline, cancellation })
    }

    /// Returns the document identity.
    #[must_use]
    pub fn document(&self) -> &DocumentId {
        &self.document
    }

    /// Returns the request deadline.
    #[must_use]
    pub fn deadline(&self) -> Deadline {
        self.deadline
    }

    /// Returns a clone of the cancellation signal.
    #[must_use]
    pub fn cancellation(&self) -> Cancellation {
        self.cancellation.clone()
    }
}

/// One typed tombstone operation.
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum TombstoneCommand {
    /// Record that one family's state for the document was deleted at a
    /// version.
    Record {
        /// The family whose state was deleted.
        family: FamilyKind,
        /// The document version at deletion.
        deletion_version: u64,
    },
    /// Clear one family's tombstone for the document.
    Clear {
        /// The family whose tombstone is cleared.
        family: FamilyKind,
    },
}

/// A typed tombstone write bound to one document.
#[derive(Clone, Debug, PartialEq)]
pub struct TombstoneWrite {
    scope: TenantScope,
    document: DocumentId,
    command: TombstoneCommand,
    operation_id: OperationId,
    deadline: Deadline,
    cancellation: Cancellation,
}

impl TombstoneWrite {
    /// Creates a validated tombstone operation.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidDeadline`] for an elapsed
    /// deadline and [`RequestValidationError::InvalidCancellation`] for an
    /// already cancelled signal.
    pub fn try_new(
        scope: TenantScope,
        document: DocumentId,
        command: TombstoneCommand,
        operation_id: OperationId,
        deadline: Deadline,
        cancellation: Cancellation,
    ) -> Result<Self, RequestValidationError> {
        validate_deadline(&deadline)?;
        validate_cancellation(&cancellation)?;
        Ok(Self { scope, document, command, operation_id, deadline, cancellation })
    }

    /// Returns the validated tenant scope.
    #[must_use]
    pub fn tenant_scope(&self) -> &TenantScope {
        &self.scope
    }

    /// Returns the document identity.
    #[must_use]
    pub fn document(&self) -> &DocumentId {
        &self.document
    }

    /// Returns the typed command.
    #[must_use]
    pub fn command(&self) -> TombstoneCommand {
        self.command
    }

    /// Returns the stable operation identity.
    #[must_use]
    pub fn operation_id(&self) -> &OperationId {
        &self.operation_id
    }

    /// Returns the request deadline.
    #[must_use]
    pub fn deadline(&self) -> Deadline {
        self.deadline
    }

    /// Returns a clone of the cancellation signal.
    #[must_use]
    pub fn cancellation(&self) -> Cancellation {
        self.cancellation.clone()
    }
}

/// One recorded family tombstone.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct TombstoneEntry {
    family: FamilyKind,
    deletion_version: u64,
    recorded_at: i64,
}

impl TombstoneEntry {
    /// Creates one tombstone entry. Crate-only.
    pub(crate) fn new(family: FamilyKind, deletion_version: u64, recorded_at: i64) -> Self {
        Self { family, deletion_version, recorded_at }
    }

    /// Returns the deleted family.
    #[must_use]
    pub fn family(&self) -> FamilyKind {
        self.family
    }

    /// Returns the document version at deletion.
    #[must_use]
    pub fn deletion_version(&self) -> u64 {
        self.deletion_version
    }

    /// Returns the logical recording time.
    #[must_use]
    pub fn recorded_at(&self) -> i64 {
        self.recorded_at
    }
}

/// The tombstones recorded for one document.
#[derive(Clone, Debug, PartialEq)]
pub struct TombstoneResult {
    document: DocumentId,
    entries: Vec<TombstoneEntry>,
}

impl TombstoneResult {
    /// Creates one tombstone result. Crate-only.
    pub(crate) fn new(document: DocumentId, entries: Vec<TombstoneEntry>) -> Self {
        Self { document, entries }
    }

    /// Returns the document identity.
    #[must_use]
    pub fn document(&self) -> &DocumentId {
        &self.document
    }

    /// Returns the recorded tombstones.
    #[must_use]
    pub fn entries(&self) -> &[TombstoneEntry] {
        &self.entries
    }

    /// Returns the tombstone of one family, if recorded.
    #[must_use]
    pub fn find(&self, family: FamilyKind) -> Option<&TombstoneEntry> {
        self.entries.iter().find(|entry| entry.family() == family)
    }
}
