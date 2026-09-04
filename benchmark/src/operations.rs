//! Closed typed operation registries shared by the Engine and adapters.

#![allow(dead_code)]
#![allow(
    clippy::elidable_lifetime_names,
    clippy::missing_errors_doc,
    clippy::must_use_candidate,
    clippy::needless_pass_by_value
)]

use crate::{
    engine::CommitOutcome,
    families::{FamilyReadRequest, FamilyReadResult, FamilyWriteRequest},
    query::{QueryResult, QuerySpec},
    types::{
        Cancellation, CollectionName, Deadline, Document, DocumentId, DocumentValue,
        ExpectedVersion, OperationId, TenantId,
    },
};

/// Validation failures for public logical identifiers and requests.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum RequestValidationError {
    /// A required text field was empty.
    EmptyField,
    /// A logical identifier was invalid.
    InvalidIdentifier,
    /// An operation identity was invalid.
    InvalidOperationIdentity,
    /// A deadline was invalid or expired.
    InvalidDeadline,
    /// A cancellation signal was already cancelled or invalid.
    InvalidCancellation,
    /// A range was invalid.
    InvalidRange,
    /// A family payload was invalid.
    InvalidFamilyPayload,
    /// A typed query was invalid.
    InvalidQuery,
    /// A typed document patch was invalid.
    InvalidPatch,
}

/// Validation failures for V1-compatible query input.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum QueryInputError {
    /// The query text was empty.
    EmptyText,
    /// The text does not use the accepted V1 grammar.
    UnsupportedGrammar,
    /// The text contains an invalid logical identifier.
    InvalidIdentifier,
}

/// A typed document read request.
#[derive(Clone, Debug, PartialEq)]
pub struct DocumentReadRequest {
    tenant_claim: TenantId,
    collection: CollectionName,
    document_id: DocumentId,
    deadline: Deadline,
    cancellation: Cancellation,
}

impl DocumentReadRequest {
    /// Creates a request after checking its deadline and cancellation state.
    ///
    /// The tenant claim, collection, and document identifier arrive through
    /// their own validated constructors, so this constructor repeats only the
    /// bounded liveness checks: an already elapsed deadline or an already
    /// cancelled signal is refused here and never reaches an adapter.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidDeadline`] for an elapsed
    /// deadline and [`RequestValidationError::InvalidCancellation`] for an
    /// already cancelled signal.
    pub fn try_new(
        tenant_claim: TenantId,
        collection: CollectionName,
        document_id: DocumentId,
        deadline: Deadline,
        cancellation: Cancellation,
    ) -> Result<Self, RequestValidationError> {
        if deadline.is_expired() {
            return Err(RequestValidationError::InvalidDeadline);
        }
        if cancellation.is_cancelled() {
            return Err(RequestValidationError::InvalidCancellation);
        }
        Ok(Self { tenant_claim, collection, document_id, deadline, cancellation })
    }

    /// Returns the untrusted tenant claim.
    pub fn tenant_claim(&self) -> &TenantId {
        &self.tenant_claim
    }

    /// Returns the collection name.
    pub fn collection(&self) -> &CollectionName {
        &self.collection
    }

    /// Returns the document identifier.
    pub fn document_id(&self) -> &DocumentId {
        &self.document_id
    }

    /// Returns the request deadline.
    pub fn deadline(&self) -> Deadline {
        self.deadline
    }

    /// Returns a clone of the cancellation signal.
    pub fn cancellation(&self) -> Cancellation {
        self.cancellation.clone()
    }
}

/// A typed document write request.
#[derive(Clone, Debug, PartialEq)]
pub struct DocumentWriteRequest {
    tenant_claim: TenantId,
    document: Document,
    expected: ExpectedVersion,
    operation_id: OperationId,
    deadline: Deadline,
    cancellation: Cancellation,
}

impl DocumentWriteRequest {
    /// Creates a request after checking its deadline and cancellation state.
    ///
    /// The tenant claim, the document, the expected version, and the stable
    /// operation identity arrive through their own validated constructors, so
    /// this constructor repeats only the bounded liveness checks: an already
    /// elapsed deadline or an already cancelled signal is refused here and
    /// never reaches an adapter.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidDeadline`] for an elapsed
    /// deadline and [`RequestValidationError::InvalidCancellation`] for an
    /// already cancelled signal.
    pub fn try_new(
        tenant_claim: TenantId,
        document: Document,
        expected: ExpectedVersion,
        operation_id: OperationId,
        deadline: Deadline,
        cancellation: Cancellation,
    ) -> Result<Self, RequestValidationError> {
        if deadline.is_expired() {
            return Err(RequestValidationError::InvalidDeadline);
        }
        if cancellation.is_cancelled() {
            return Err(RequestValidationError::InvalidCancellation);
        }
        Ok(Self { tenant_claim, document, expected, operation_id, deadline, cancellation })
    }

    /// Returns the untrusted tenant claim.
    pub fn tenant_claim(&self) -> &TenantId {
        &self.tenant_claim
    }

    /// Returns the logical document.
    pub fn document(&self) -> &Document {
        &self.document
    }

    /// Returns the expected document version.
    pub fn expected_version(&self) -> ExpectedVersion {
        self.expected
    }

    /// Returns the stable operation identity.
    pub fn operation_id(&self) -> &OperationId {
        &self.operation_id
    }

    /// Returns the request deadline.
    pub fn deadline(&self) -> Deadline {
        self.deadline
    }

    /// Returns a clone of the cancellation signal.
    pub fn cancellation(&self) -> Cancellation {
        self.cancellation.clone()
    }
}

/// Validated bounded read-only SQL query text (D-78/D-54).
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct SqlQueryText(String);

/// The maximum accepted SQL query text length.
///
/// The bound keeps validation cost proportional to a bounded input before the
/// text reaches the public constructor.
pub const MAX_SQL_TEXT_BYTES: usize = 16 * 1024;

/// The maximum accepted query text length: the SQL bound under its
/// canonical name.
pub const MAX_QUERY_TEXT_BYTES: usize = MAX_SQL_TEXT_BYTES;

impl SqlQueryText {
    /// Creates validated read-only SQL query text.
    ///
    /// The text must be non-empty, at most [`MAX_SQL_TEXT_BYTES`] bytes,
    /// and shaped as read-only SQL (`SELECT`, `WITH`, or `EXPLAIN` first
    /// keyword; no DDL, DML, pragma, attachment, or multi-statement text).
    /// Anything else is a typed [`QueryInputError`] and never reaches the
    /// compile seam. Full planning belongs to track item E-4.
    ///
    /// # Errors
    ///
    /// Returns [`QueryInputError::EmptyText`] for empty text,
    /// [`QueryInputError::UnsupportedGrammar`] for text that is too long or
    /// not read-only SQL, and
    /// [`QueryInputError::InvalidIdentifier`] for invalid identifiers.
    pub fn try_new(text: &str) -> Result<Self, QueryInputError> {
        if text.is_empty() {
            return Err(QueryInputError::EmptyText);
        }
        if text.len() > MAX_SQL_TEXT_BYTES {
            return Err(QueryInputError::UnsupportedGrammar);
        }
        crate::query::validate_sql_text(text)?;
        Ok(Self(text.to_owned()))
    }

    /// Returns the validated query text.
    pub fn as_str(&self) -> &str {
        &self.0
    }
}

/// One of the two accepted query input forms.
#[derive(Clone, Debug, PartialEq)]
pub enum QueryInput {
    /// A typed query specification (the SQL AST surface).
    Typed(QuerySpec),
    /// Read-only SQL text (planned by E-4).
    Sql(SqlQueryText),
    /// Read-only SQL text with a caller-supplied deadline and
    /// cancellation signal: the serving seam binds control to the
    /// otherwise unbound text (P4's cancellation surface).
    SqlBounded {
        /// The validated text.
        text: SqlQueryText,
        /// The execution deadline.
        deadline: Deadline,
        /// The cancellation signal.
        cancellation: Cancellation,
    },
}

impl QueryInput {
    /// Wraps a typed query specification.
    pub fn typed(spec: QuerySpec) -> Self {
        Self::Typed(spec)
    }

    /// Wraps validated read-only SQL text.
    pub fn sql(text: SqlQueryText) -> Self {
        Self::Sql(text)
    }

    /// Wraps validated SQL text with execution control.
    #[must_use]
    pub fn sql_bounded(text: SqlQueryText, deadline: Deadline, cancellation: Cancellation) -> Self {
        Self::SqlBounded { text, deadline, cancellation }
    }
}

/// A query operation with optional retry identity.
#[derive(Clone, Debug, PartialEq)]
pub struct QueryOperation {
    input: QueryInput,
    operation_id: Option<OperationId>,
    deadline: Deadline,
    cancellation: Cancellation,
}

impl QueryOperation {
    /// Creates a query operation after checking its deadline and cancellation.
    ///
    /// Both input forms arrive pre-validated — the typed specification
    /// through [`QuerySpec::try_new`](crate::query::QuerySpec::try_new) and
    /// SQL text through [`SqlQueryText::try_new`], so this
    /// constructor repeats only the bounded liveness checks. The optional
    /// operation identity is a stable retry identity, not a liveness bound.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidDeadline`] for an elapsed
    /// deadline and [`RequestValidationError::InvalidCancellation`] for an
    /// already cancelled signal.
    pub fn try_new(
        input: QueryInput,
        operation_id: Option<OperationId>,
        deadline: Deadline,
        cancellation: Cancellation,
    ) -> Result<Self, RequestValidationError> {
        if deadline.is_expired() {
            return Err(RequestValidationError::InvalidDeadline);
        }
        if cancellation.is_cancelled() {
            return Err(RequestValidationError::InvalidCancellation);
        }
        Ok(Self { input, operation_id, deadline, cancellation })
    }

    /// Returns the typed query input.
    pub fn input(&self) -> &QueryInput {
        &self.input
    }

    /// Returns the optional stable operation identity.
    pub fn operation_id(&self) -> Option<&OperationId> {
        self.operation_id.as_ref()
    }

    /// Returns the request deadline.
    pub fn deadline(&self) -> Deadline {
        self.deadline
    }

    /// Returns a clone of the cancellation signal.
    pub fn cancellation(&self) -> Cancellation {
        self.cancellation.clone()
    }
}

/// A closed family operation registry.
#[derive(Clone, Debug, PartialEq)]
pub enum FamilyOperation {
    /// Read one family capability.
    Read(FamilyReadRequest),
    /// Stage one family mutation.
    Write(FamilyWriteRequest),
}

impl FamilyOperation {
    /// Wraps a family read request.
    pub fn read(value: FamilyReadRequest) -> Self {
        Self::Read(value)
    }

    /// Wraps a family write request.
    pub fn write(value: FamilyWriteRequest) -> Self {
        Self::Write(value)
    }
}

/// The only public operation registry accepted by typed adapters.
#[derive(Clone, Debug, PartialEq)]
pub enum PublicOperation {
    /// Read one document.
    DocumentRead(DocumentReadRequest),
    /// Write one document.
    DocumentWrite(DocumentWriteRequest),
    /// Execute one query.
    Query(QueryOperation),
    /// Execute one family operation.
    Family(FamilyOperation),
    /// Execute one schema-lifecycle operation.
    Schema(SchemaOperation),
}

impl PublicOperation {
    /// Wraps a document read request.
    pub fn document_read(value: DocumentReadRequest) -> Self {
        Self::DocumentRead(value)
    }

    /// Wraps a document write request.
    pub fn document_write(value: DocumentWriteRequest) -> Self {
        Self::DocumentWrite(value)
    }

    /// Wraps a query operation.
    pub fn query(value: QueryOperation) -> Self {
        Self::Query(value)
    }

    /// Wraps a family operation.
    pub fn family(value: FamilyOperation) -> Self {
        Self::Family(value)
    }

    /// Wraps a schema-lifecycle operation.
    pub fn schema(value: SchemaOperation) -> Self {
        Self::Schema(value)
    }
}

/// A typed request to declare, replace, or clear one collection's schema.
///
/// The schema arrives as a [`crate::families::SchemaDefinition`] — the typed, versioned,
/// bounded rule set of the schema model — never as bytes, text, or a raw
/// frame body, so the generic family byte path cannot express it and this
/// constructor cannot smuggle one. A `None` schema clears the collection's
/// policy, returning it to schema-free behavior.
#[derive(Clone, Debug, PartialEq)]
pub struct SchemaDeclareRequest {
    tenant_claim: TenantId,
    collection: CollectionName,
    schema: Option<crate::families::SchemaDefinition>,
    operation_id: OperationId,
    deadline: Deadline,
    cancellation: Cancellation,
}

impl SchemaDeclareRequest {
    /// Creates a request after checking its deadline and cancellation state.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidDeadline`] for an elapsed
    /// deadline and [`RequestValidationError::InvalidCancellation`] for an
    /// already cancelled signal.
    pub fn try_new(
        tenant_claim: TenantId,
        collection: CollectionName,
        schema: Option<crate::families::SchemaDefinition>,
        operation_id: OperationId,
        deadline: Deadline,
        cancellation: Cancellation,
    ) -> Result<Self, RequestValidationError> {
        if deadline.is_expired() {
            return Err(RequestValidationError::InvalidDeadline);
        }
        if cancellation.is_cancelled() {
            return Err(RequestValidationError::InvalidCancellation);
        }
        Ok(Self { tenant_claim, collection, schema, operation_id, deadline, cancellation })
    }

    /// Returns the untrusted tenant claim.
    pub fn tenant_claim(&self) -> &TenantId {
        &self.tenant_claim
    }

    /// Returns the collection name.
    pub fn collection(&self) -> &CollectionName {
        &self.collection
    }

    /// Returns the requested policy, or `None` to clear it.
    pub fn schema(&self) -> Option<&crate::families::SchemaDefinition> {
        self.schema.as_ref()
    }

    /// Returns the stable operation identity.
    pub fn operation_id(&self) -> &OperationId {
        &self.operation_id
    }

    /// Returns the request deadline.
    pub fn deadline(&self) -> Deadline {
        self.deadline
    }

    /// Returns a clone of the cancellation signal.
    pub fn cancellation(&self) -> Cancellation {
        self.cancellation.clone()
    }
}

/// A typed request for one collection's recorded schema state.
#[derive(Clone, Debug, PartialEq)]
pub struct SchemaStatusRequest {
    tenant_claim: TenantId,
    collection: CollectionName,
    deadline: Deadline,
    cancellation: Cancellation,
}

impl SchemaStatusRequest {
    /// Creates a request after checking its deadline and cancellation state.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidDeadline`] for an elapsed
    /// deadline and [`RequestValidationError::InvalidCancellation`] for an
    /// already cancelled signal.
    pub fn try_new(
        tenant_claim: TenantId,
        collection: CollectionName,
        deadline: Deadline,
        cancellation: Cancellation,
    ) -> Result<Self, RequestValidationError> {
        if deadline.is_expired() {
            return Err(RequestValidationError::InvalidDeadline);
        }
        if cancellation.is_cancelled() {
            return Err(RequestValidationError::InvalidCancellation);
        }
        Ok(Self { tenant_claim, collection, deadline, cancellation })
    }

    /// Returns the untrusted tenant claim.
    pub fn tenant_claim(&self) -> &TenantId {
        &self.tenant_claim
    }

    /// Returns the collection name.
    pub fn collection(&self) -> &CollectionName {
        &self.collection
    }

    /// Returns the request deadline.
    pub fn deadline(&self) -> Deadline {
        self.deadline
    }

    /// Returns a clone of the cancellation signal.
    pub fn cancellation(&self) -> Cancellation {
        self.cancellation.clone()
    }
}

/// A typed request to validate one document value against the collection's
/// recorded schema.
///
/// Validation is pure: nothing is staged and nothing is written, so the
/// operation is the on-demand check the studio and the CLI surface. The
/// value arrives as a [`DocumentValue`]; the answer's violations are the
/// schema model's field-and-kind vocabulary and never carry a field value.
#[derive(Clone, Debug, PartialEq)]
pub struct SchemaValidateRequest {
    tenant_claim: TenantId,
    collection: CollectionName,
    value: DocumentValue,
    deadline: Deadline,
    cancellation: Cancellation,
}

impl SchemaValidateRequest {
    /// Creates a request after checking its deadline and cancellation state.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidDeadline`] for an elapsed
    /// deadline and [`RequestValidationError::InvalidCancellation`] for an
    /// already cancelled signal.
    pub fn try_new(
        tenant_claim: TenantId,
        collection: CollectionName,
        value: DocumentValue,
        deadline: Deadline,
        cancellation: Cancellation,
    ) -> Result<Self, RequestValidationError> {
        if deadline.is_expired() {
            return Err(RequestValidationError::InvalidDeadline);
        }
        if cancellation.is_cancelled() {
            return Err(RequestValidationError::InvalidCancellation);
        }
        Ok(Self { tenant_claim, collection, value, deadline, cancellation })
    }

    /// Returns the untrusted tenant claim.
    pub fn tenant_claim(&self) -> &TenantId {
        &self.tenant_claim
    }

    /// Returns the collection name.
    pub fn collection(&self) -> &CollectionName {
        &self.collection
    }

    /// Returns the document value to validate.
    pub fn value(&self) -> &DocumentValue {
        &self.value
    }

    /// Returns the request deadline.
    pub fn deadline(&self) -> Deadline {
        self.deadline
    }

    /// Returns a clone of the cancellation signal.
    pub fn cancellation(&self) -> Cancellation {
        self.cancellation.clone()
    }
}

/// A typed request for one collection's Schema Migration state.
#[derive(Clone, Debug, PartialEq)]
pub struct MigrationStatusRequest {
    tenant_claim: TenantId,
    collection: CollectionName,
    deadline: Deadline,
    cancellation: Cancellation,
}

impl MigrationStatusRequest {
    /// Creates a request after checking its deadline and cancellation state.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidDeadline`] for an elapsed
    /// deadline and [`RequestValidationError::InvalidCancellation`] for an
    /// already cancelled signal.
    pub fn try_new(
        tenant_claim: TenantId,
        collection: CollectionName,
        deadline: Deadline,
        cancellation: Cancellation,
    ) -> Result<Self, RequestValidationError> {
        if deadline.is_expired() {
            return Err(RequestValidationError::InvalidDeadline);
        }
        if cancellation.is_cancelled() {
            return Err(RequestValidationError::InvalidCancellation);
        }
        Ok(Self { tenant_claim, collection, deadline, cancellation })
    }

    /// Returns the untrusted tenant claim.
    pub fn tenant_claim(&self) -> &TenantId {
        &self.tenant_claim
    }

    /// Returns the collection name.
    pub fn collection(&self) -> &CollectionName {
        &self.collection
    }

    /// Returns the request deadline.
    pub fn deadline(&self) -> Deadline {
        self.deadline
    }

    /// Returns a clone of the cancellation signal.
    pub fn cancellation(&self) -> Cancellation {
        self.cancellation.clone()
    }
}

/// A typed request to compute one migration contract's dry run.
///
/// The contract is the typed [`crate::families::migrations::SchemaMigration`] of the migration model; the
/// answer is the plan with its evidence, and nothing is mutated.
#[derive(Clone, Debug, PartialEq)]
pub struct MigrationDryRunRequest {
    tenant_claim: TenantId,
    contract: crate::families::migrations::SchemaMigration,
    deadline: Deadline,
    cancellation: Cancellation,
}

impl MigrationDryRunRequest {
    /// Creates a request after checking its deadline and cancellation state.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidDeadline`] for an elapsed
    /// deadline and [`RequestValidationError::InvalidCancellation`] for an
    /// already cancelled signal.
    pub fn try_new(
        tenant_claim: TenantId,
        contract: crate::families::migrations::SchemaMigration,
        deadline: Deadline,
        cancellation: Cancellation,
    ) -> Result<Self, RequestValidationError> {
        if deadline.is_expired() {
            return Err(RequestValidationError::InvalidDeadline);
        }
        if cancellation.is_cancelled() {
            return Err(RequestValidationError::InvalidCancellation);
        }
        Ok(Self { tenant_claim, contract, deadline, cancellation })
    }

    /// Returns the untrusted tenant claim.
    pub fn tenant_claim(&self) -> &TenantId {
        &self.tenant_claim
    }

    /// Returns the migration contract.
    pub fn contract(&self) -> &crate::families::migrations::SchemaMigration {
        &self.contract
    }

    /// Returns the request deadline.
    pub fn deadline(&self) -> Deadline {
        self.deadline
    }

    /// Returns a clone of the cancellation signal.
    pub fn cancellation(&self) -> Cancellation {
        self.cancellation.clone()
    }
}

/// A typed request to apply one migration contract to completion.
///
/// Apply drives the migration model's step operations — begin, page,
/// finalize — under one stable operation identity, so a retried apply names
/// the same drive and the model's idempotency answers with the recorded
/// receipt instead of a second application.
#[derive(Clone, Debug, PartialEq)]
pub struct MigrationApplyRequest {
    tenant_claim: TenantId,
    contract: crate::families::migrations::SchemaMigration,
    options: crate::families::migrations::MigrationOptions,
    operation_id: OperationId,
    deadline: Deadline,
    cancellation: Cancellation,
}

impl MigrationApplyRequest {
    /// Creates a request after checking its deadline and cancellation state.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidDeadline`] for an elapsed
    /// deadline and [`RequestValidationError::InvalidCancellation`] for an
    /// already cancelled signal.
    pub fn try_new(
        tenant_claim: TenantId,
        contract: crate::families::migrations::SchemaMigration,
        options: crate::families::migrations::MigrationOptions,
        operation_id: OperationId,
        deadline: Deadline,
        cancellation: Cancellation,
    ) -> Result<Self, RequestValidationError> {
        if deadline.is_expired() {
            return Err(RequestValidationError::InvalidDeadline);
        }
        if cancellation.is_cancelled() {
            return Err(RequestValidationError::InvalidCancellation);
        }
        Ok(Self { tenant_claim, contract, options, operation_id, deadline, cancellation })
    }

    /// Returns the untrusted tenant claim.
    pub fn tenant_claim(&self) -> &TenantId {
        &self.tenant_claim
    }

    /// Returns the migration contract.
    pub fn contract(&self) -> &crate::families::migrations::SchemaMigration {
        &self.contract
    }

    /// Returns the apply options, including the not-rollbackable
    /// acknowledgment.
    pub fn options(&self) -> crate::families::migrations::MigrationOptions {
        self.options
    }

    /// Returns the stable operation identity.
    pub fn operation_id(&self) -> &OperationId {
        &self.operation_id
    }

    /// Returns the request deadline.
    pub fn deadline(&self) -> Deadline {
        self.deadline
    }

    /// Returns a clone of the cancellation signal.
    pub fn cancellation(&self) -> Cancellation {
        self.cancellation.clone()
    }
}

/// The closed schema-lifecycle operation set.
///
/// Six variants, one per lifecycle step, and no more: the set is the
/// vertical exposure slice the schema wave names (declare, status, validate
/// document, migration status, dry run, apply). There is deliberately no
/// byte, text, or frame-body form — a schema crosses this seam only as the
/// typed [`SchemaDefinition`](crate::families::SchemaDefinition), and a
/// migration only as the typed
/// [`SchemaMigration`](crate::families::migrations::SchemaMigration).
#[derive(Clone, Debug, PartialEq)]
pub enum SchemaOperation {
    /// Declare, replace, or clear one collection's schema.
    Declare(SchemaDeclareRequest),
    /// Read one collection's recorded schema state.
    Status(SchemaStatusRequest),
    /// Validate one document value against the recorded schema.
    Validate(SchemaValidateRequest),
    /// Read one collection's Schema Migration state.
    MigrationStatus(MigrationStatusRequest),
    /// Compute one migration contract's dry run.
    MigrationDryRun(MigrationDryRunRequest),
    /// Apply one migration contract to completion.
    MigrationApply(MigrationApplyRequest),
}

impl SchemaOperation {
    /// Wraps a schema declaration.
    pub fn declare(value: SchemaDeclareRequest) -> Self {
        Self::Declare(value)
    }

    /// Wraps a schema status read.
    pub fn status(value: SchemaStatusRequest) -> Self {
        Self::Status(value)
    }

    /// Wraps a document validation.
    pub fn validate(value: SchemaValidateRequest) -> Self {
        Self::Validate(value)
    }

    /// Wraps a migration status read.
    pub fn migration_status(value: MigrationStatusRequest) -> Self {
        Self::MigrationStatus(value)
    }

    /// Wraps a migration dry run.
    pub fn migration_dry_run(value: MigrationDryRunRequest) -> Self {
        Self::MigrationDryRun(value)
    }

    /// Wraps a migration apply.
    pub fn migration_apply(value: MigrationApplyRequest) -> Self {
        Self::MigrationApply(value)
    }
}

/// The answer of one declared schema: the version the collection now
/// enforces and the recorded rule count.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct SchemaDeclared {
    enforced_version: Option<crate::families::SchemaVersion>,
    rule_count: usize,
}

impl SchemaDeclared {
    /// Creates one declared answer. Crate-only.
    pub(crate) fn new(
        enforced_version: Option<crate::families::SchemaVersion>,
        rule_count: usize,
    ) -> Self {
        Self { enforced_version, rule_count }
    }

    /// Returns the version now enforced, or `None` when the policy cleared.
    pub fn enforced_version(&self) -> Option<crate::families::SchemaVersion> {
        self.enforced_version
    }

    /// Returns the recorded rule count.
    pub fn rule_count(&self) -> usize {
        self.rule_count
    }
}

/// The answer of one schema status read.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct SchemaStatus {
    enforced_version: Option<crate::families::SchemaVersion>,
    rule_count: usize,
}

impl SchemaStatus {
    /// Creates one status answer. Crate-only.
    pub(crate) fn new(
        enforced_version: Option<crate::families::SchemaVersion>,
        rule_count: usize,
    ) -> Self {
        Self { enforced_version, rule_count }
    }

    /// Returns the version enforced, or `None` for a schema-free collection.
    pub fn enforced_version(&self) -> Option<crate::families::SchemaVersion> {
        self.enforced_version
    }

    /// Returns the recorded rule count.
    pub fn rule_count(&self) -> usize {
        self.rule_count
    }
}

/// The answer of one document validation.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct SchemaValidation {
    enforced_version: Option<crate::families::SchemaVersion>,
    violations: Vec<crate::families::SchemaViolation>,
}

impl SchemaValidation {
    /// Creates one validation answer. Crate-only.
    pub(crate) fn new(
        enforced_version: Option<crate::families::SchemaVersion>,
        violations: Vec<crate::families::SchemaViolation>,
    ) -> Self {
        Self { enforced_version, violations }
    }

    /// Returns the version the validation enforced.
    pub fn enforced_version(&self) -> Option<crate::families::SchemaVersion> {
        self.enforced_version
    }

    /// Reports whether the value satisfies the recorded schema.
    pub fn valid(&self) -> bool {
        self.violations.is_empty()
    }

    /// Returns the typed violations in the schema model's canonical order.
    pub fn violations(&self) -> &[crate::families::SchemaViolation] {
        &self.violations
    }
}

/// A typed response of the schema-lifecycle operation set.
///
/// Every variant reuses the migration model's own public result types where
/// one exists — the plan, the receipt, and the migration status — so the
/// surface adds vocabulary without duplicating evidence shapes.
#[derive(Clone, Debug, PartialEq)]
pub enum SchemaResponse {
    /// The answer of one schema declaration.
    Declared(SchemaDeclared),
    /// The answer of one schema status read.
    Status(SchemaStatus),
    /// The answer of one document validation.
    Validation(SchemaValidation),
    /// The answer of one migration status read.
    MigrationStatus(crate::families::migrations::MigrationStatus),
    /// The plan of one dry run.
    Plan(crate::families::migrations::MigrationPlan),
    /// The receipt of one applied migration.
    Receipt(crate::families::migrations::MigrationReceipt),
}

/// A typed response returned by the public operation registry.
#[derive(Clone, Debug, PartialEq)]
pub enum PublicResponse {
    /// A document read response.
    Document(Option<Document>),
    /// A query response.
    Query(QueryResult),
    /// A family read response.
    Family(FamilyReadResult),
    /// A local commit response.
    Commit(CommitOutcome),
    /// A schema-lifecycle response.
    Schema(SchemaResponse),
}

/// A trusted in-process request passed from Platform to Engine.
#[derive(Debug)]
pub(crate) enum EngineRequest {
    /// Read one document in an authorized scope.
    DocumentRead { scope: crate::types::TenantScope, request: DocumentReadRequest },
    /// Write one document in an authorized scope.
    DocumentWrite { scope: crate::types::TenantScope, request: DocumentWriteRequest },
    /// Execute one query in an authorized scope.
    Query { scope: crate::types::TenantScope, request: QueryOperation },
    /// Execute one family operation in an authorized scope.
    Family { scope: crate::types::TenantScope, request: FamilyOperation },
    /// Execute one schema-lifecycle operation in an authorized scope.
    Schema { scope: crate::types::TenantScope, request: SchemaOperation },
}

/// A trusted in-process response returned by Engine.
#[derive(Debug)]
pub(crate) enum EngineResponse {
    /// A document read response.
    Document(Option<Document>),
    /// A query response.
    Query(QueryResult),
    /// A family read response.
    Family(FamilyReadResult),
    /// A local commit response.
    Commit(CommitOutcome),
    /// A schema-lifecycle response.
    Schema(SchemaResponse),
}
