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
        Cancellation, CollectionName, Deadline, Document, DocumentId, ExpectedVersion, OperationId,
        TenantId,
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

/// A validated V1-compatible query text.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct V1QueryText(String);

/// The maximum accepted V1-compatible query text length.
///
/// The bound keeps parsing cost proportional to a bounded input before the
/// text reaches the public constructor.
pub const MAX_QUERY_TEXT_BYTES: usize = 16 * 1024;

impl V1QueryText {
    /// Creates a validated V1-compatible query text.
    ///
    /// The text must be non-empty, at most [`MAX_QUERY_TEXT_BYTES`] bytes,
    /// and use only the supported V1-compatible grammar; anything else is a
    /// typed [`QueryInputError`] and never reaches the compile seam.
    ///
    /// # Errors
    ///
    /// Returns [`QueryInputError::EmptyText`] for empty text,
    /// [`QueryInputError::UnsupportedGrammar`] for text that is too long or
    /// does not use the supported grammar, and
    /// [`QueryInputError::InvalidIdentifier`] for invalid identifiers.
    pub fn try_new(text: &str) -> Result<Self, QueryInputError> {
        if text.is_empty() {
            return Err(QueryInputError::EmptyText);
        }
        if text.len() > MAX_QUERY_TEXT_BYTES {
            return Err(QueryInputError::UnsupportedGrammar);
        }
        crate::query::validate_v1_text(text)?;
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
    /// A typed query specification.
    Typed(QuerySpec),
    /// V1-compatible query text.
    V1Compatible(V1QueryText),
}

impl QueryInput {
    /// Wraps a typed query specification.
    pub fn typed(spec: QuerySpec) -> Self {
        Self::Typed(spec)
    }

    /// Wraps validated V1-compatible query text.
    pub fn v1_compatible(text: V1QueryText) -> Self {
        Self::V1Compatible(text)
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
    /// the V1-compatible text through [`V1QueryText::try_new`] — so this
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
}
