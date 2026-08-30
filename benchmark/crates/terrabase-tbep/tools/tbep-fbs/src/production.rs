//! Safe, owned production requests and response encoding over the generated
//! TBEP FlatBuffers bindings.
//!
//! Network input becomes an owned value only after the common structural
//! verifier, the envelope gates, the operation budget, and all body semantics
//! pass. The engine-facing crate therefore never dispatches generated table
//! accessors or borrows attacker-owned bytes.

use std::collections::HashSet;

use flatbuffers::{FlatBufferBuilder, UnionWIPOffset, WIPOffset};

use crate::tbep_generated::terrabase::tbep as fbs;
use crate::{DecodeError, SIZE_PREFIX_BYTES, decode_envelope};

/// Registry control-frame budget.
pub const CONTROL_FRAME_BYTES: usize = 256;
/// Registry ordinary-operation frame budget.
pub const OPERATION_FRAME_BYTES: usize = 1_024;
/// Registry large-operation frame budget.
pub const LARGE_OPERATION_FRAME_BYTES: usize = 65_536;
/// Maximum UTF-8 bytes in an error detail.
pub const MAX_DETAIL_BYTES: usize = 256;
/// Maximum UTF-8 bytes in a database, collection, or document identifier.
pub const MAX_IDENTIFIER_BYTES: usize = 64;
/// Maximum UTF-8 bytes in a workload principal or operation identity.
pub const MAX_IDENTITY_BYTES: usize = 128;
/// Maximum recursive JSON-family depth accepted by the semantic validator.
pub const MAX_VALUE_DEPTH: usize = 64;
/// Maximum value nodes accepted by one document frame.
pub const MAX_VALUE_NODES: usize = 10_000;
/// Maximum capability count accepted during negotiation.
pub const MAX_CAPABILITIES: usize = 16;
/// Maximum supported-version count accepted during negotiation.
pub const MAX_SUPPORTED_VERSIONS: usize = 8;
/// Maximum bytes in one capability name.
pub const MAX_CAPABILITY_BYTES: usize = 64;

/// One 16-byte TBEP correlation or causation identity.
pub type FrameId = [u8; 16];

/// A validated recursive document value.
#[derive(Clone, Debug, PartialEq)]
pub enum Value {
    /// JSON null.
    Null,
    /// JSON boolean.
    Boolean(bool),
    /// Signed integer.
    Integer(i64),
    /// Finite floating-point number.
    Number(f64),
    /// UTF-8 text.
    String(String),
    /// Ordered array.
    Array(Vec<Self>),
    /// Name-sorted object with unique keys.
    Object(Vec<(String, Self)>),
}

/// A validated expected-version precondition.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ExpectedVersion {
    /// The document must not exist.
    Absent,
    /// Any current version is admissible.
    Any,
    /// The current version must equal this nonzero value.
    Exact(u64),
}

/// A validated acknowledgement request.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Acknowledgement {
    /// Use the operation's default acknowledgement.
    Default,
    /// Regional quorum.
    RegionalQuorum,
    /// Global quorum.
    GlobalQuorum,
    /// Backup acknowledgement.
    BackupAck,
}

/// A validated read level.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ReadLevel {
    /// Use the operation's default level.
    Default,
    /// Local member read.
    Local,
    /// Regional quorum read.
    RegionalQuorum,
    /// Global quorum read.
    GlobalQuorum,
}

/// One validated document write body.
#[derive(Clone, Debug, PartialEq)]
pub struct DocumentPut {
    /// Document identifier.
    pub id: String,
    /// Collection identifier.
    pub collection: String,
    /// Document value.
    pub body: Value,
}

/// One validated production request.
#[derive(Clone, Debug, PartialEq)]
pub enum Request {
    /// Version and capability negotiation.
    Negotiation {
        /// Request correlation.
        correlation: FrameId,
        /// Supported protocol majors.
        supported_versions: Vec<u16>,
        /// Optional capabilities.
        capabilities: Vec<String>,
    },
    /// Bind this authenticated channel to one database scope.
    ChannelBind {
        /// Request correlation.
        correlation: FrameId,
        /// Authenticated workload principal assertion.
        principal: String,
        /// Database/tenant scope.
        tenant: String,
    },
    /// Read one document.
    DocumentRead {
        /// Request correlation.
        correlation: FrameId,
        /// Collection identifier.
        collection: String,
        /// Document identifier.
        document: String,
        /// Read level.
        level: ReadLevel,
    },
    /// Write one document.
    DocumentWrite {
        /// Request correlation.
        correlation: FrameId,
        /// Document body.
        document: DocumentPut,
        /// Version precondition.
        expected: ExpectedVersion,
        /// Public request ULID, also used as the durable idempotency identity.
        operation_id: String,
        /// Requested acknowledgement.
        acknowledgement: Acknowledgement,
    },
    /// Delete one document.
    DocumentDelete {
        /// Request correlation.
        correlation: FrameId,
        /// Collection identifier.
        collection: String,
        /// Document identifier.
        document: String,
        /// Version precondition.
        expected: ExpectedVersion,
        /// Public request ULID, also used as the durable idempotency identity.
        operation_id: String,
        /// Requested acknowledgement.
        acknowledgement: Acknowledgement,
    },
}

impl Request {
    /// Returns the caller correlation used for response causation.
    #[must_use]
    pub const fn correlation(&self) -> FrameId {
        match self {
            Self::Negotiation { correlation, .. }
            | Self::ChannelBind { correlation, .. }
            | Self::DocumentRead { correlation, .. }
            | Self::DocumentWrite { correlation, .. }
            | Self::DocumentDelete { correlation, .. } => *correlation,
        }
    }
}

/// Why a structurally verified request was refused before dispatch.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum SemanticError {
    /// The request exceeded its registry operation budget.
    OperationBudget {
        /// Delivered FlatBuffers bytes, excluding the size prefix.
        actual: usize,
        /// Registry budget for this operation.
        maximum: usize,
    },
    /// A client request used the zero correlation sentinel.
    ZeroCorrelation,
    /// A client request named a causal parent.
    NonzeroCausation,
    /// The frame is registered but is not a supported client request.
    WrongDirectionOrUnsupported,
    /// A required body field was absent.
    Missing(&'static str),
    /// A string or collection violated its semantic bound.
    Invalid(&'static str),
    /// A closed enum or union carried an unsupported value.
    Unknown(&'static str),
}

impl core::fmt::Display for SemanticError {
    fn fmt(&self, formatter: &mut core::fmt::Formatter<'_>) -> core::fmt::Result {
        match self {
            Self::OperationBudget { actual, maximum } => {
                write!(formatter, "operation frame has {actual} bytes; maximum is {maximum}")
            }
            Self::ZeroCorrelation => write!(formatter, "request correlation is zero"),
            Self::NonzeroCausation => write!(formatter, "request causation is not zero"),
            Self::WrongDirectionOrUnsupported => {
                write!(formatter, "operation is not a supported client request")
            }
            Self::Missing(field) => write!(formatter, "required field {field} is absent"),
            Self::Invalid(field) => write!(formatter, "field {field} is invalid"),
            Self::Unknown(field) => write!(formatter, "field {field} carries an unknown value"),
        }
    }
}

impl core::error::Error for SemanticError {}

/// A production request decode failure, with a correlation when the verified
/// envelope exposed one safely.
#[derive(Debug)]
pub struct RequestDecodeError {
    /// Envelope or structural failure, when decoding did not reach semantics.
    pub envelope: Option<DecodeError>,
    /// Semantic failure, when the envelope passed structural verification.
    pub semantic: Option<SemanticError>,
    /// Verified request correlation, usable as typed-error causation.
    pub correlation: Option<FrameId>,
}

impl core::fmt::Display for RequestDecodeError {
    fn fmt(&self, formatter: &mut core::fmt::Formatter<'_>) -> core::fmt::Result {
        if let Some(error) = &self.envelope {
            error.fmt(formatter)
        } else if let Some(error) = &self.semantic {
            error.fmt(formatter)
        } else {
            write!(formatter, "unknown production request decode failure")
        }
    }
}

impl core::error::Error for RequestDecodeError {}

/// Decode and fully validate one supported production request.
pub fn decode_request(bytes: &[u8]) -> Result<Request, RequestDecodeError> {
    let decoded = decode_envelope(bytes).map_err(|error| RequestDecodeError {
        envelope: Some(error),
        semantic: None,
        correlation: None,
    })?;
    let envelope = decoded.envelope;
    let correlation = envelope.correlation().expect("decode_envelope requires correlation").0;
    let causation = envelope.causation().expect("decode_envelope requires causation").0;
    let fail = |semantic| RequestDecodeError {
        envelope: None,
        semantic: Some(semantic),
        correlation: Some(correlation),
    };

    if correlation == [0; 16] {
        return Err(fail(SemanticError::ZeroCorrelation));
    }
    if causation != [0; 16] {
        return Err(fail(SemanticError::NonzeroCausation));
    }

    let operation = envelope.operation();
    let budget = operation_budget(operation)
        .ok_or_else(|| fail(SemanticError::WrongDirectionOrUnsupported))?;
    let actual = bytes.len().saturating_sub(SIZE_PREFIX_BYTES);
    if actual > budget {
        return Err(fail(SemanticError::OperationBudget { actual, maximum: budget }));
    }

    let request = if operation == fbs::TbepOperation::NEGOTIATION_REQUEST {
        let body = envelope
            .body_as_negotiation_request_frame()
            .ok_or_else(|| fail(SemanticError::Missing("negotiation body")))?;
        let versions = body
            .supported_versions()
            .ok_or_else(|| fail(SemanticError::Missing("supported_versions")))?;
        if versions.is_empty() || versions.len() > MAX_SUPPORTED_VERSIONS {
            return Err(fail(SemanticError::Invalid("supported_versions")));
        }
        let capabilities =
            body.capabilities().ok_or_else(|| fail(SemanticError::Missing("capabilities")))?;
        if capabilities.len() > MAX_CAPABILITIES {
            return Err(fail(SemanticError::Invalid("capabilities")));
        }
        let mut owned_capabilities = Vec::with_capacity(capabilities.len());
        let mut unique = HashSet::with_capacity(capabilities.len());
        for capability in capabilities {
            if !valid_text(capability, MAX_CAPABILITY_BYTES) || !unique.insert(capability) {
                return Err(fail(SemanticError::Invalid("capabilities")));
            }
            owned_capabilities.push(capability.to_owned());
        }
        Request::Negotiation {
            correlation,
            supported_versions: versions.iter().collect(),
            capabilities: owned_capabilities,
        }
    } else if operation == fbs::TbepOperation::CHANNEL_BIND {
        let body = envelope
            .body_as_channel_bind_request()
            .ok_or_else(|| fail(SemanticError::Missing("channel_bind body")))?;
        let principal =
            required_text(body.principal(), MAX_IDENTITY_BYTES, "principal").map_err(&fail)?;
        let tenant = required_identifier(body.tenant(), "tenant").map_err(&fail)?;
        Request::ChannelBind { correlation, principal, tenant }
    } else if operation == fbs::TbepOperation::DOCUMENT_READ {
        let body = envelope
            .body_as_document_read_request()
            .ok_or_else(|| fail(SemanticError::Missing("document_read body")))?;
        let collection = required_identifier(body.collection(), "collection").map_err(&fail)?;
        let document = required_identifier(body.document(), "document").map_err(&fail)?;
        let level = read_level(body.level()).map_err(&fail)?;
        Request::DocumentRead { correlation, collection, document, level }
    } else if operation == fbs::TbepOperation::DOCUMENT_WRITE {
        let body = envelope
            .body_as_document_write_request()
            .ok_or_else(|| fail(SemanticError::Missing("document_write body")))?;
        let document = body.document().ok_or_else(|| fail(SemanticError::Missing("document")))?;
        let id = required_identifier(document.id(), "document.id").map_err(&fail)?;
        let collection =
            required_identifier(document.collection(), "document.collection").map_err(&fail)?;
        let mut state = ValueState::default();
        let body_value = decode_document_value(document, &mut state, 1).map_err(&fail)?;
        let expected = expected_version(
            body.expected_type(),
            body.expected_as_expected_version_exact().map(|value| value.version()),
            body.expected().is_some(),
        )
        .map_err(&fail)?;
        let operation_id = required_text(body.operation_id(), MAX_IDENTITY_BYTES, "operation_id")
            .map_err(&fail)?;
        if !canonical_ulid(&operation_id) {
            return Err(fail(SemanticError::Invalid("operation_id")));
        }
        let acknowledgement = acknowledgement(body.ack()).map_err(&fail)?;
        Request::DocumentWrite {
            correlation,
            document: DocumentPut { id, collection, body: body_value },
            expected,
            operation_id,
            acknowledgement,
        }
    } else if operation == fbs::TbepOperation::DOCUMENT_DELETE {
        let body = envelope
            .body_as_document_delete_request()
            .ok_or_else(|| fail(SemanticError::Missing("document_delete body")))?;
        let collection = required_identifier(body.collection(), "collection").map_err(&fail)?;
        let document = required_identifier(body.document(), "document").map_err(&fail)?;
        let expected = expected_version(
            body.expected_type(),
            body.expected_as_expected_version_exact().map(|value| value.version()),
            body.expected().is_some(),
        )
        .map_err(&fail)?;
        let operation_id = required_text(body.operation_id(), MAX_IDENTITY_BYTES, "operation_id")
            .map_err(&fail)?;
        if !canonical_ulid(&operation_id) {
            return Err(fail(SemanticError::Invalid("operation_id")));
        }
        let acknowledgement = acknowledgement(body.ack()).map_err(&fail)?;
        Request::DocumentDelete {
            correlation,
            collection,
            document,
            expected,
            operation_id,
            acknowledgement,
        }
    } else {
        return Err(fail(SemanticError::WrongDirectionOrUnsupported));
    };
    Ok(request)
}

fn operation_budget(operation: fbs::TbepOperation) -> Option<usize> {
    match operation {
        fbs::TbepOperation::NEGOTIATION_REQUEST | fbs::TbepOperation::CHANNEL_BIND => {
            Some(CONTROL_FRAME_BYTES)
        }
        fbs::TbepOperation::DOCUMENT_READ | fbs::TbepOperation::DOCUMENT_DELETE => {
            Some(OPERATION_FRAME_BYTES)
        }
        fbs::TbepOperation::DOCUMENT_WRITE => Some(LARGE_OPERATION_FRAME_BYTES),
        _ => None,
    }
}

fn required_identifier(value: Option<&str>, field: &'static str) -> Result<String, SemanticError> {
    required_text(value, MAX_IDENTIFIER_BYTES, field)
}

fn required_text(
    value: Option<&str>,
    maximum: usize,
    field: &'static str,
) -> Result<String, SemanticError> {
    let value = value.ok_or(SemanticError::Missing(field))?;
    if !valid_text(value, maximum) {
        return Err(SemanticError::Invalid(field));
    }
    Ok(value.to_owned())
}

fn valid_text(value: &str, maximum: usize) -> bool {
    !value.is_empty() && value.len() <= maximum && !value.chars().any(char::is_control)
}

fn canonical_ulid(value: &str) -> bool {
    value.len() == 26
        && matches!(value.as_bytes().first(), Some(b'0'..=b'7'))
        && value.bytes().all(|byte| {
            matches!(
                byte,
                b'0'..=b'9'
                    | b'A'..=b'H'
                    | b'J'
                    | b'K'
                    | b'M'
                    | b'N'
                    | b'P'..=b'T'
                    | b'V'..=b'Z'
            )
        })
}

fn read_level(value: fbs::ReadLevel) -> Result<ReadLevel, SemanticError> {
    match value {
        fbs::ReadLevel::NONE => Ok(ReadLevel::Default),
        fbs::ReadLevel::LOCAL => Ok(ReadLevel::Local),
        fbs::ReadLevel::REGIONAL_QUORUM_READ => Ok(ReadLevel::RegionalQuorum),
        fbs::ReadLevel::GLOBAL_QUORUM_READ => Ok(ReadLevel::GlobalQuorum),
        _ => Err(SemanticError::Unknown("read_level")),
    }
}

fn acknowledgement(value: fbs::Acknowledgement) -> Result<Acknowledgement, SemanticError> {
    match value {
        fbs::Acknowledgement::NONE => Ok(Acknowledgement::Default),
        fbs::Acknowledgement::REGIONAL_QUORUM => Ok(Acknowledgement::RegionalQuorum),
        fbs::Acknowledgement::GLOBAL_QUORUM => Ok(Acknowledgement::GlobalQuorum),
        fbs::Acknowledgement::BACKUP_ACK => Ok(Acknowledgement::BackupAck),
        _ => Err(SemanticError::Unknown("acknowledgement")),
    }
}

fn expected_version(
    kind: fbs::ExpectedVersion,
    exact: Option<u64>,
    value_present: bool,
) -> Result<ExpectedVersion, SemanticError> {
    match kind {
        fbs::ExpectedVersion::ExpectedVersionAbsent if value_present => Ok(ExpectedVersion::Absent),
        fbs::ExpectedVersion::ExpectedVersionAny if value_present => Ok(ExpectedVersion::Any),
        fbs::ExpectedVersion::ExpectedVersionExact => match exact {
            Some(version) if version != 0 => Ok(ExpectedVersion::Exact(version)),
            _ => Err(SemanticError::Invalid("expected.version")),
        },
        fbs::ExpectedVersion::NONE => Err(SemanticError::Missing("expected")),
        fbs::ExpectedVersion::ExpectedVersionAbsent | fbs::ExpectedVersion::ExpectedVersionAny => {
            Err(SemanticError::Missing("expected.value"))
        }
        _ => Err(SemanticError::Unknown("expected")),
    }
}

#[derive(Default)]
struct ValueState {
    nodes: usize,
}

impl ValueState {
    fn enter(&mut self, depth: usize) -> Result<(), SemanticError> {
        if depth > MAX_VALUE_DEPTH {
            return Err(SemanticError::Invalid("document.value.depth"));
        }
        self.nodes =
            self.nodes.checked_add(1).ok_or(SemanticError::Invalid("document.value.nodes"))?;
        if self.nodes > MAX_VALUE_NODES {
            return Err(SemanticError::Invalid("document.value.nodes"));
        }
        Ok(())
    }
}

enum ValueView<'a> {
    Null,
    Boolean(fbs::ValueBoolean<'a>),
    Integer(fbs::ValueInteger<'a>),
    Number(fbs::ValueNumber<'a>),
    String(fbs::ValueString<'a>),
    Array(fbs::ValueArray<'a>),
    Object(fbs::ValueObject<'a>),
}

fn decode_document_value(
    document: fbs::DocumentPut<'_>,
    state: &mut ValueState,
    depth: usize,
) -> Result<Value, SemanticError> {
    let view = match document.body_type() {
        fbs::TbepValue::ValueNull => document.body_as_value_null().map(|_| ValueView::Null),
        fbs::TbepValue::ValueBoolean => document.body_as_value_boolean().map(ValueView::Boolean),
        fbs::TbepValue::ValueInteger => document.body_as_value_integer().map(ValueView::Integer),
        fbs::TbepValue::ValueNumber => document.body_as_value_number().map(ValueView::Number),
        fbs::TbepValue::ValueString => document.body_as_value_string().map(ValueView::String),
        fbs::TbepValue::ValueArray => document.body_as_value_array().map(ValueView::Array),
        fbs::TbepValue::ValueObject => document.body_as_value_object().map(ValueView::Object),
        fbs::TbepValue::NONE => return Err(SemanticError::Missing("document.body")),
        _ => return Err(SemanticError::Unknown("document.body_type")),
    }
    .ok_or(SemanticError::Missing("document.body.value"))?;
    decode_value(view, state, depth)
}

fn decode_cell_value(
    cell: fbs::ValueCell<'_>,
    state: &mut ValueState,
    depth: usize,
) -> Result<Value, SemanticError> {
    let view = match cell.value_type() {
        fbs::TbepValue::ValueNull => cell.value_as_value_null().map(|_| ValueView::Null),
        fbs::TbepValue::ValueBoolean => cell.value_as_value_boolean().map(ValueView::Boolean),
        fbs::TbepValue::ValueInteger => cell.value_as_value_integer().map(ValueView::Integer),
        fbs::TbepValue::ValueNumber => cell.value_as_value_number().map(ValueView::Number),
        fbs::TbepValue::ValueString => cell.value_as_value_string().map(ValueView::String),
        fbs::TbepValue::ValueArray => cell.value_as_value_array().map(ValueView::Array),
        fbs::TbepValue::ValueObject => cell.value_as_value_object().map(ValueView::Object),
        fbs::TbepValue::NONE => return Err(SemanticError::Missing("document.value")),
        _ => return Err(SemanticError::Unknown("document.value_type")),
    }
    .ok_or(SemanticError::Missing("document.value.payload"))?;
    decode_value(view, state, depth)
}

fn decode_entry_value(
    entry: fbs::ValueEntry<'_>,
    state: &mut ValueState,
    depth: usize,
) -> Result<Value, SemanticError> {
    let view = match entry.value_type() {
        fbs::TbepValue::ValueNull => entry.value_as_value_null().map(|_| ValueView::Null),
        fbs::TbepValue::ValueBoolean => entry.value_as_value_boolean().map(ValueView::Boolean),
        fbs::TbepValue::ValueInteger => entry.value_as_value_integer().map(ValueView::Integer),
        fbs::TbepValue::ValueNumber => entry.value_as_value_number().map(ValueView::Number),
        fbs::TbepValue::ValueString => entry.value_as_value_string().map(ValueView::String),
        fbs::TbepValue::ValueArray => entry.value_as_value_array().map(ValueView::Array),
        fbs::TbepValue::ValueObject => entry.value_as_value_object().map(ValueView::Object),
        fbs::TbepValue::NONE => return Err(SemanticError::Missing("document.object.value")),
        _ => return Err(SemanticError::Unknown("document.object.value_type")),
    }
    .ok_or(SemanticError::Missing("document.object.payload"))?;
    decode_value(view, state, depth)
}

fn decode_value(
    view: ValueView<'_>,
    state: &mut ValueState,
    depth: usize,
) -> Result<Value, SemanticError> {
    state.enter(depth)?;
    match view {
        ValueView::Null => Ok(Value::Null),
        ValueView::Boolean(value) => Ok(Value::Boolean(value.value())),
        ValueView::Integer(value) => Ok(Value::Integer(value.value())),
        ValueView::Number(value) if value.value().is_finite() => Ok(Value::Number(value.value())),
        ValueView::Number(_) => Err(SemanticError::Invalid("document.number")),
        ValueView::String(value) => {
            let value = value.value().ok_or(SemanticError::Missing("document.string"))?;
            if value.len() > LARGE_OPERATION_FRAME_BYTES {
                return Err(SemanticError::Invalid("document.string"));
            }
            Ok(Value::String(value.to_owned()))
        }
        ValueView::Array(value) => {
            let items = value.items().ok_or(SemanticError::Missing("document.array"))?;
            if items.len() > MAX_VALUE_NODES {
                return Err(SemanticError::Invalid("document.array"));
            }
            let items = items
                .iter()
                .map(|item| decode_cell_value(item, state, depth + 1))
                .collect::<Result<Vec<_>, _>>()?;
            Ok(Value::Array(items))
        }
        ValueView::Object(value) => {
            let entries = value.entries().ok_or(SemanticError::Missing("document.object"))?;
            if entries.len() > MAX_VALUE_NODES {
                return Err(SemanticError::Invalid("document.object"));
            }
            let mut result = Vec::with_capacity(entries.len());
            let mut previous: Option<String> = None;
            for entry in entries {
                let key = required_text(entry.key(), LARGE_OPERATION_FRAME_BYTES, "object.key")?;
                if previous.as_deref().is_some_and(|prior| prior >= key.as_str()) {
                    return Err(SemanticError::Invalid("object.key_order"));
                }
                let item = decode_entry_value(entry, state, depth + 1)?;
                previous = Some(key.clone());
                result.push((key, item));
            }
            Ok(Value::Object(result))
        }
    }
}

/// One document returned by the engine.
#[derive(Clone, Debug, PartialEq)]
pub struct Document {
    /// Document identifier.
    pub id: String,
    /// Collection identifier.
    pub collection: String,
    /// Published version.
    pub version: u64,
    /// Document body.
    pub body: Value,
}

/// A committed write receipt.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct CommitReceipt {
    /// Storage sequence.
    pub sequence: u64,
    /// Published document version.
    pub version: u64,
    /// Echoed public request ULID.
    pub operation_id: String,
    /// Granted acknowledgement, if reported.
    pub acknowledgement: Option<Acknowledgement>,
    /// Backup outcome wire code, if reported: 1 no-backup, 2 full, 3 partial.
    pub backup: Option<u8>,
}

/// A document write outcome.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum WriteOutcome {
    /// Committed with a receipt.
    Committed(CommitReceipt),
    /// Version conflict.
    Conflict,
    /// Rejected with a `WriteReason` wire code.
    Rejected(u8),
}

/// One production response body.
#[derive(Clone, Debug, PartialEq)]
pub enum Response {
    /// Negotiated protocol version and capabilities.
    Negotiation {
        /// Selected version.
        selected_version: u16,
        /// Accepted capabilities.
        accepted_capabilities: Vec<String>,
    },
    /// Successful channel binding.
    ChannelBind {
        /// Bound database/tenant.
        tenant: String,
        /// Engine serving instance.
        serving_instance: u64,
    },
    /// Document read result.
    DocumentRead {
        /// Returned document, or none when absent.
        document: Option<Document>,
        /// Engine read position.
        position: u64,
    },
    /// Document write result.
    DocumentWrite(WriteOutcome),
    /// Document delete result.
    DocumentDelete(WriteOutcome),
    /// Typed protocol or engine refusal.
    Error {
        /// Registered error-code wire value.
        code: u16,
        /// Safe bounded detail.
        detail: String,
    },
}

/// A response could not be encoded within the protocol contract.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum EncodeError {
    /// A response semantic was invalid.
    Invalid(&'static str),
    /// The encoded response exceeded its operation budget.
    Oversized {
        /// Encoded FlatBuffers bytes excluding the size prefix.
        actual: usize,
        /// Registry budget.
        maximum: usize,
    },
}

impl core::fmt::Display for EncodeError {
    fn fmt(&self, formatter: &mut core::fmt::Formatter<'_>) -> core::fmt::Result {
        match self {
            Self::Invalid(field) => write!(formatter, "response field {field} is invalid"),
            Self::Oversized { actual, maximum } => {
                write!(formatter, "response has {actual} bytes; maximum is {maximum}")
            }
        }
    }
}

impl core::error::Error for EncodeError {}

/// Encode one response with a fresh nonzero correlation and request-bound
/// causation.
pub fn encode_response(
    response_correlation: FrameId,
    request_correlation: FrameId,
    response: &Response,
) -> Result<Vec<u8>, EncodeError> {
    if response_correlation == [0; 16] {
        return Err(EncodeError::Invalid("correlation"));
    }
    if request_correlation == [0; 16] && !matches!(response, Response::Error { .. }) {
        return Err(EncodeError::Invalid("causation"));
    }

    let mut builder = FlatBufferBuilder::new();
    let (operation, body_type, body, budget) = match response {
        Response::Negotiation { selected_version, accepted_capabilities } => {
            if *selected_version == 0 || accepted_capabilities.len() > MAX_CAPABILITIES {
                return Err(EncodeError::Invalid("negotiation"));
            }
            let capabilities: Vec<_> =
                accepted_capabilities.iter().map(|value| builder.create_string(value)).collect();
            let capabilities = builder.create_vector(&capabilities);
            let body = fbs::NegotiationResponseFrame::create(
                &mut builder,
                &fbs::NegotiationResponseFrameArgs {
                    selected_version: *selected_version,
                    accepted_capabilities: Some(capabilities),
                },
            );
            (
                fbs::TbepOperation::NEGOTIATION_RESPONSE,
                fbs::TbepFrame::NegotiationResponseFrame,
                body.as_union_value(),
                CONTROL_FRAME_BYTES,
            )
        }
        Response::ChannelBind { tenant, serving_instance } => {
            if !valid_text(tenant, MAX_IDENTIFIER_BYTES) {
                return Err(EncodeError::Invalid("tenant"));
            }
            let tenant = builder.create_string(tenant);
            let body = fbs::ChannelBindResponse::create(
                &mut builder,
                &fbs::ChannelBindResponseArgs {
                    tenant: Some(tenant),
                    serving_instance: *serving_instance,
                },
            );
            (
                fbs::TbepOperation::CHANNEL_BIND_RESPONSE,
                fbs::TbepFrame::ChannelBindResponse,
                body.as_union_value(),
                CONTROL_FRAME_BYTES,
            )
        }
        Response::DocumentRead { document, position } => {
            let document =
                document.as_ref().map(|value| encode_document(&mut builder, value)).transpose()?;
            let body = fbs::DocumentReadResponse::create(
                &mut builder,
                &fbs::DocumentReadResponseArgs {
                    found: document.is_some(),
                    document,
                    position: *position,
                },
            );
            (
                fbs::TbepOperation::DOCUMENT_READ_RESPONSE,
                fbs::TbepFrame::DocumentReadResponse,
                body.as_union_value(),
                OPERATION_FRAME_BYTES,
            )
        }
        Response::DocumentWrite(outcome) => {
            let (outcome_type, outcome) = encode_write_outcome(&mut builder, outcome)?;
            let body = fbs::DocumentWriteResponse::create(
                &mut builder,
                &fbs::DocumentWriteResponseArgs { outcome_type, outcome: Some(outcome) },
            );
            (
                fbs::TbepOperation::DOCUMENT_WRITE_RESPONSE,
                fbs::TbepFrame::DocumentWriteResponse,
                body.as_union_value(),
                LARGE_OPERATION_FRAME_BYTES,
            )
        }
        Response::DocumentDelete(outcome) => {
            let (outcome_type, outcome) = encode_write_outcome(&mut builder, outcome)?;
            let body = fbs::DocumentDeleteResponse::create(
                &mut builder,
                &fbs::DocumentDeleteResponseArgs { outcome_type, outcome: Some(outcome) },
            );
            (
                fbs::TbepOperation::DOCUMENT_DELETE_RESPONSE,
                fbs::TbepFrame::DocumentDeleteResponse,
                body.as_union_value(),
                OPERATION_FRAME_BYTES,
            )
        }
        Response::Error { code, detail } => {
            let code = fbs::ErrorCode(*code);
            if code == fbs::ErrorCode::NONE || code.variant_name().is_none() {
                return Err(EncodeError::Invalid("error.code"));
            }
            let detail = truncate_utf8(detail, MAX_DETAIL_BYTES);
            let detail = builder.create_string(detail);
            let body = fbs::ErrorFrame::create(
                &mut builder,
                &fbs::ErrorFrameArgs { code, detail: Some(detail) },
            );
            (
                fbs::TbepOperation::ERROR,
                fbs::TbepFrame::ErrorFrame,
                body.as_union_value(),
                CONTROL_FRAME_BYTES,
            )
        }
    };

    let envelope = fbs::TbepEnvelope::create(
        &mut builder,
        &fbs::TbepEnvelopeArgs {
            protocol_major: 1,
            protocol_minor: 0,
            required_features: fbs::FeatureBits::empty(),
            operation,
            correlation: Some(&fbs::Ulid(response_correlation)),
            causation: Some(&fbs::Ulid(request_correlation)),
            body_type,
            body: Some(body),
        },
    );
    fbs::finish_size_prefixed_tbep_envelope_buffer(&mut builder, envelope);
    let bytes = builder.finished_data().to_vec();
    let actual = bytes.len().saturating_sub(SIZE_PREFIX_BYTES);
    if actual > budget {
        return Err(EncodeError::Oversized { actual, maximum: budget });
    }
    Ok(bytes)
}

fn encode_document<'a>(
    builder: &mut FlatBufferBuilder<'a>,
    document: &Document,
) -> Result<WIPOffset<fbs::DocumentBody<'a>>, EncodeError> {
    if !valid_text(&document.id, MAX_IDENTIFIER_BYTES)
        || !valid_text(&document.collection, MAX_IDENTIFIER_BYTES)
        || document.version == 0
    {
        return Err(EncodeError::Invalid("document"));
    }
    let id = builder.create_string(&document.id);
    let collection = builder.create_string(&document.collection);
    let (body_type, body) = encode_value(builder, &document.body)?;
    Ok(fbs::DocumentBody::create(
        builder,
        &fbs::DocumentBodyArgs {
            id: Some(id),
            collection: Some(collection),
            version: document.version,
            body_type,
            body: Some(body),
        },
    ))
}

fn encode_write_outcome<'a>(
    builder: &mut FlatBufferBuilder<'a>,
    outcome: &WriteOutcome,
) -> Result<(fbs::WriteOutcome, WIPOffset<UnionWIPOffset>), EncodeError> {
    match outcome {
        WriteOutcome::Committed(receipt) => {
            if receipt.sequence == 0
                || receipt.version == 0
                || !canonical_ulid(&receipt.operation_id)
            {
                return Err(EncodeError::Invalid("receipt"));
            }
            let operation_id = builder.create_string(&receipt.operation_id);
            let acknowledgement = match receipt.acknowledgement {
                None | Some(Acknowledgement::Default) => fbs::Acknowledgement::NONE,
                Some(Acknowledgement::RegionalQuorum) => fbs::Acknowledgement::REGIONAL_QUORUM,
                Some(Acknowledgement::GlobalQuorum) => fbs::Acknowledgement::GLOBAL_QUORUM,
                Some(Acknowledgement::BackupAck) => fbs::Acknowledgement::BACKUP_ACK,
            };
            let backup = match receipt.backup {
                None => fbs::BackupOutcome::NONE,
                Some(1) => fbs::BackupOutcome::NO_BACKUP,
                Some(2) => fbs::BackupOutcome::FULL,
                Some(3) => fbs::BackupOutcome::PARTIAL,
                Some(_) => return Err(EncodeError::Invalid("receipt.backup")),
            };
            let receipt = fbs::CommitReceipt::create(
                builder,
                &fbs::CommitReceiptArgs {
                    sequence: receipt.sequence,
                    version: receipt.version,
                    operation_id: Some(operation_id),
                    acknowledgement,
                    backup,
                },
            );
            let committed = fbs::WriteCommitted::create(
                builder,
                &fbs::WriteCommittedArgs { receipt: Some(receipt) },
            );
            Ok((fbs::WriteOutcome::WriteCommitted, committed.as_union_value()))
        }
        WriteOutcome::Conflict => {
            let conflict = fbs::WriteConflict::create(builder, &fbs::WriteConflictArgs {});
            Ok((fbs::WriteOutcome::WriteConflict, conflict.as_union_value()))
        }
        WriteOutcome::Rejected(reason) => {
            let reason = match *reason {
                1 => fbs::WriteReason::INVALID_DOCUMENT,
                2 => fbs::WriteReason::SCHEMA_REFUSED,
                3 => fbs::WriteReason::NOT_FOUND,
                _ => return Err(EncodeError::Invalid("write.reason")),
            };
            let rejected = fbs::WriteRejected::create(builder, &fbs::WriteRejectedArgs { reason });
            Ok((fbs::WriteOutcome::WriteRejected, rejected.as_union_value()))
        }
    }
}

fn encode_value<'a>(
    builder: &mut FlatBufferBuilder<'a>,
    value: &Value,
) -> Result<(fbs::TbepValue, WIPOffset<UnionWIPOffset>), EncodeError> {
    match value {
        Value::Null => {
            let value = fbs::ValueNull::create(builder, &fbs::ValueNullArgs {});
            Ok((fbs::TbepValue::ValueNull, value.as_union_value()))
        }
        Value::Boolean(flag) => {
            let value = fbs::ValueBoolean::create(builder, &fbs::ValueBooleanArgs { value: *flag });
            Ok((fbs::TbepValue::ValueBoolean, value.as_union_value()))
        }
        Value::Integer(number) => {
            let value =
                fbs::ValueInteger::create(builder, &fbs::ValueIntegerArgs { value: *number });
            Ok((fbs::TbepValue::ValueInteger, value.as_union_value()))
        }
        Value::Number(number) if number.is_finite() => {
            let value = fbs::ValueNumber::create(builder, &fbs::ValueNumberArgs { value: *number });
            Ok((fbs::TbepValue::ValueNumber, value.as_union_value()))
        }
        Value::Number(_) => Err(EncodeError::Invalid("document.number")),
        Value::String(text) => {
            let text = builder.create_string(text);
            let value =
                fbs::ValueString::create(builder, &fbs::ValueStringArgs { value: Some(text) });
            Ok((fbs::TbepValue::ValueString, value.as_union_value()))
        }
        Value::Array(items) => {
            let mut cells = Vec::with_capacity(items.len());
            for item in items {
                let (value_type, value) = encode_value(builder, item)?;
                cells.push(fbs::ValueCell::create(
                    builder,
                    &fbs::ValueCellArgs { value_type, value: Some(value) },
                ));
            }
            let cells = builder.create_vector(&cells);
            let value =
                fbs::ValueArray::create(builder, &fbs::ValueArrayArgs { items: Some(cells) });
            Ok((fbs::TbepValue::ValueArray, value.as_union_value()))
        }
        Value::Object(entries) => {
            let mut previous: Option<&str> = None;
            let mut encoded = Vec::with_capacity(entries.len());
            for (key, item) in entries {
                if !valid_text(key, LARGE_OPERATION_FRAME_BYTES)
                    || previous.is_some_and(|prior| prior >= key.as_str())
                {
                    return Err(EncodeError::Invalid("object.keys"));
                }
                let key_offset = builder.create_string(key);
                let (value_type, value) = encode_value(builder, item)?;
                encoded.push(fbs::ValueEntry::create(
                    builder,
                    &fbs::ValueEntryArgs { key: Some(key_offset), value_type, value: Some(value) },
                ));
                previous = Some(key);
            }
            let entries = builder.create_vector(&encoded);
            let value =
                fbs::ValueObject::create(builder, &fbs::ValueObjectArgs { entries: Some(entries) });
            Ok((fbs::TbepValue::ValueObject, value.as_union_value()))
        }
    }
}

fn truncate_utf8(value: &str, maximum: usize) -> &str {
    if value.len() <= maximum {
        return value;
    }
    let mut end = maximum;
    while !value.is_char_boundary(end) {
        end -= 1;
    }
    &value[..end]
}

#[cfg(test)]
mod tests {
    use super::canonical_ulid;

    #[test]
    fn canonical_ulid_rejects_text_that_overflows_128_bits() {
        assert!(canonical_ulid("01ARZ3NDEKTSV4RRFFQ69G5FAV"));
        assert!(canonical_ulid("7ZZZZZZZZZZZZZZZZZZZZZZZZZ"));
        assert!(!canonical_ulid("81ARZ3NDEKTSV4RRFFQ69G5FAV"));
        assert!(!canonical_ulid("ZZZZZZZZZZZZZZZZZZZZZZZZZZ"));
    }
}
