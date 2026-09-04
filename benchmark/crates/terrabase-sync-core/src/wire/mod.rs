//! The bounded MessagePack codec for `terrascale.sync.v2`.
//!
//! This module is deliberately independent from the older [`crate::protocol`]
//! frame format. The public wire is one indexed MessagePack envelope:
//! `[major, minor, features, tag, correlation, causation, body]`. Maps,
//! extensions, floats, unknown protocol values, and trailing bytes are not
//! part of this contract.

use std::fmt;

/// The protocol major version spoken by this codec.
pub const MAJOR_VERSION: u32 = 2;
/// The protocol minor version spoken by this codec.
pub const MINOR_VERSION: u32 = 1;
/// Feature bits understood by this codec.
pub const SUPPORTED_FEATURES: u32 = 0;
/// Maximum complete `MessagePack` message size.
pub const MAX_MESSAGE_BYTES: usize = 8_388_608;
/// Maximum nesting depth accepted by the structural scan.
pub const MAX_DEPTH: usize = 64;
/// Maximum string size accepted by the structural scan.
pub const MAX_STRING_BYTES: usize = 262_144;
/// Maximum binary size accepted by the structural scan.
pub const MAX_BINARY_BYTES: usize = 1_048_576;
/// Maximum number of elements in any array accepted by the structural scan.
pub const MAX_ARRAY_ITEMS: usize = 10_000;
/// Maximum map entries. Maps are forbidden by this protocol.
pub const MAX_MAP_ENTRIES: usize = 0;

/// Message tags fixed by the manifest. Tags are never reused.
pub mod tags {
    /// Subscribe.
    pub const SUBSCRIBE: u32 = 1;
    /// Unsubscribe.
    pub const UNSUBSCRIBE: u32 = 2;
    /// Snapshot.
    pub const SNAPSHOT: u32 = 3;
    /// Delta.
    pub const DELTA: u32 = 4;
    /// Reset.
    pub const RESET: u32 = 5;
    /// Heartbeat.
    pub const HEARTBEAT: u32 = 6;
    /// Checkpoint acknowledgement.
    pub const CHECKPOINT_ACK: u32 = 7;
    /// Authorization changed.
    pub const AUTHORIZATION_CHANGED: u32 = 8;
    /// Region route change.
    pub const REGION_ROUTE_CHANGE: u32 = 9;
    /// Error.
    pub const ERROR: u32 = 10;
    /// Backpressure.
    pub const BACKPRESSURE: u32 = 11;
}

/// The direction in which a message may travel.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Direction {
    /// Client to server.
    ClientToServer,
    /// Server to client.
    ServerToClient,
    /// Either direction.
    Bidirectional,
}

/// One projected field; `value` is opaque canonical JSON bytes.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct ProjectedField {
    /// Projected field path.
    pub path: String,
    /// Opaque canonical JSON bytes.
    pub value: Vec<u8>,
}

/// One authorized result item in canonical query order.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct ResultItem {
    /// Stable result identity.
    pub identity: String,
    /// Projected fields.
    pub fields: Vec<ProjectedField>,
}

/// A complete authorized result view at one opaque checkpoint.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Snapshot {
    /// View generation.
    pub view_generation: u64,
    /// Authorization generation.
    pub authorization_generation: u64,
    /// Result revision.
    pub revision: u64,
    /// Items in canonical query order.
    pub items: Vec<ResultItem>,
    /// Content digest text.
    pub digest: String,
    /// Opaque resume checkpoint.
    pub checkpoint: Vec<u8>,
}

/// One projected field replacement or removal.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct FieldChange {
    /// Projected field path.
    pub path: String,
    /// Replacement canonical JSON bytes, or `None` for removal.
    pub value: Option<Vec<u8>>,
}

/// One ordered mutation of an authorized result view.
#[allow(missing_docs)]
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum ResultOperation {
    /// Insert a result item.
    Insert { identity: String, position: u32, fields: Vec<ProjectedField> },
    /// Replace projected fields of an item.
    Update { identity: String, fields: Vec<ProjectedField> },
    /// Replace or remove selected fields.
    Patch { identity: String, changes: Vec<FieldChange> },
    /// Remove an item.
    Remove { identity: String },
    /// Move an item to a new position.
    Move { identity: String, position: u32 },
}

/// Why an incremental view was replaced with a snapshot.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ResetReason {
    /// Checkpoint expired.
    CheckpointExpired,
    /// Retention advanced.
    RetentionAdvanced,
    /// View generation changed.
    GenerationChanged,
    /// Checkpoint is not current.
    CheckpointNotCurrent,
    /// View diverged.
    ViewDiverged,
    /// Delivery was oversized.
    Oversized,
    /// Operations were invalid.
    InvalidOperations,
    /// Checkpoint was invalid.
    InvalidCheckpoint,
    /// Consumer backpressure required a reset.
    Backpressure,
}

/// Closed public reactive error vocabulary.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ErrorCode {
    /// Unsupported protocol version.
    UnsupportedVersion,
    /// Malformed frame.
    MalformedFrame,
    /// Authentication expired.
    AuthenticationExpired,
    /// Permission denied.
    PermissionDenied,
    /// Checkpoint refused.
    CheckpointRefused,
    /// Consumer is slow.
    SlowConsumer,
    /// Server overloaded.
    Overloaded,
    /// Service unavailable.
    Unavailable,
}

/// Server bounded-consumer instruction.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum BackpressureMode {
    /// Pause client traffic.
    Pause,
    /// Resume client traffic.
    Resume,
}

/// One tagged `MessagePack` message body.
#[allow(missing_docs)]
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Message {
    /// Open a bounded live query result view.
    Subscribe {
        subscription: String,
        query: String,
        parameters: Vec<u8>,
        checkpoint: Option<Vec<u8>>,
    },
    /// Close a result view.
    Unsubscribe { subscription: String },
    /// Install a complete result view.
    Snapshot { subscription: String, snapshot: Snapshot },
    /// Apply one complete result revision.
    Delta {
        subscription: String,
        view_generation: u64,
        from_revision: u64,
        to_revision: u64,
        operations: Vec<ResultOperation>,
        digest: String,
        checkpoint: Vec<u8>,
    },
    /// Replace a view with a snapshot.
    Reset { subscription: String, reason: ResetReason, snapshot: Snapshot },
    /// Content-free liveness frame.
    Heartbeat,
    /// Acknowledge a durable checkpoint.
    CheckpointAck { subscription: String, checkpoint: Vec<u8> },
    /// Remove forbidden rows and install the new authorized view.
    AuthorizationChanged {
        subscription: String,
        authorization_generation: u64,
        removed: Vec<String>,
        snapshot: Snapshot,
    },
    /// Tell the transport to re-resolve its endpoint.
    RegionRouteChange { region: String },
    /// Typed public reactive failure.
    Error { code: ErrorCode, detail: Option<String> },
    /// Pause or resume client traffic.
    Backpressure { mode: BackpressureMode },
}

/// One complete sealed protocol envelope.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Envelope {
    /// Feature bits (currently zero).
    pub features: u32,
    /// Correlation identity.
    pub correlation: u64,
    /// Causation identity.
    pub causation: u64,
    /// Tagged message body.
    pub message: Message,
}

/// A fail-closed wire error.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum WireError {
    /// Input ended inside a value.
    Truncated,
    /// A `MessagePack` type is not valid at this position.
    InvalidType,
    /// An indexed array has the wrong shape.
    InvalidShape,
    /// Text is not valid UTF-8.
    InvalidUtf8,
    /// The whole message exceeds its bound.
    MessageLimit,
    /// A string exceeds its declared bound.
    StringLimit,
    /// A binary value exceeds its declared bound.
    BinaryLimit,
    /// An array exceeds its declared bound.
    ArrayLimit,
    /// Maps are not part of this protocol.
    MapForbidden,
    /// Extension values are not part of this protocol.
    ExtensionForbidden,
    /// Float32 is not part of this protocol.
    Float32Forbidden,
    /// Bytes remain after the complete value.
    TrailingBytes,
    /// Protocol major version is unknown.
    UnsupportedMajor(u32),
    /// Protocol minor version is unknown.
    UnsupportedMinor(u32),
    /// Feature bits are unknown.
    UnknownFeatures(u32),
    /// Message tag is unknown.
    UnknownTag(u32),
    /// Enum value is unknown.
    UnknownEnum(&'static str, u32),
    /// Union discriminant is unknown.
    UnknownVariant(u32),
}

impl fmt::Display for WireError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Truncated => f.write_str("truncated MessagePack value"),
            Self::InvalidType => f.write_str("invalid MessagePack type"),
            Self::InvalidShape => f.write_str("invalid indexed MessagePack shape"),
            Self::InvalidUtf8 => f.write_str("invalid UTF-8 text"),
            Self::MessageLimit => f.write_str("message exceeds its byte bound"),
            Self::StringLimit => f.write_str("string exceeds its byte bound"),
            Self::BinaryLimit => f.write_str("binary value exceeds its byte bound"),
            Self::ArrayLimit => f.write_str("array exceeds its item bound"),
            Self::MapForbidden => f.write_str("maps are forbidden"),
            Self::ExtensionForbidden => f.write_str("extensions are forbidden"),
            Self::Float32Forbidden => f.write_str("float32 is forbidden"),
            Self::TrailingBytes => f.write_str("trailing bytes after complete value"),
            Self::UnsupportedMajor(value) => write!(f, "unsupported protocol major {value}"),
            Self::UnsupportedMinor(value) => write!(f, "unsupported protocol minor {value}"),
            Self::UnknownFeatures(value) => write!(f, "unknown feature bits {value:#x}"),
            Self::UnknownTag(value) => write!(f, "unknown message tag {value}"),
            Self::UnknownEnum(name, value) => write!(f, "unknown {name} value {value}"),
            Self::UnknownVariant(value) => write!(f, "unknown result operation variant {value}"),
        }
    }
}

impl std::error::Error for WireError {}

/// Encodes one message body without an envelope. The tag is supplied to
/// [`decode_message`] because the body itself is intentionally untagged.
///
/// # Errors
///
/// Returns [`WireError`] when a value violates a manifest bound.
pub fn encode_message(message: &Message) -> Result<Vec<u8>, WireError> {
    validate_message(message)?;
    let mut writer = Writer::default();
    encode_body(&mut writer, message)?;
    writer.finish()
}

/// Decodes one untagged message body using its manifest tag.
///
/// # Errors
///
/// Returns [`WireError`] for malformed, unsupported, or bounded input.
pub fn decode_message(bytes: &[u8], tag: u32) -> Result<Message, WireError> {
    scan(bytes)?;
    let mut reader = Reader::new(bytes);
    let message = decode_body(&mut reader, tag)?;
    reader.end()?;
    Ok(message)
}

/// Encodes one complete indexed envelope using canonical `MessagePack`.
///
/// # Errors
///
/// Returns [`WireError`] when a value violates a manifest bound.
pub fn encode_envelope(envelope: &Envelope) -> Result<Vec<u8>, WireError> {
    validate_envelope(envelope)?;
    let mut writer = Writer::default();
    writer.array(7)?;
    writer.u32(MAJOR_VERSION);
    writer.u32(MINOR_VERSION);
    writer.u32(envelope.features);
    writer.u32(message_tag(&envelope.message));
    writer.u64(envelope.correlation);
    writer.u64(envelope.causation);
    encode_body(&mut writer, &envelope.message)?;
    writer.finish()
}

/// Decodes one complete indexed envelope, rejecting all trailing bytes.
///
/// # Errors
///
/// Returns [`WireError`] for malformed, unsupported, or bounded input.
pub fn decode_envelope(bytes: &[u8]) -> Result<Envelope, WireError> {
    scan(bytes)?;
    let mut reader = Reader::new(bytes);
    reader.array_header(7)?;
    let major = reader.u32()?;
    if major != MAJOR_VERSION {
        return Err(WireError::UnsupportedMajor(major));
    }
    let minor = reader.u32()?;
    if minor != MINOR_VERSION {
        return Err(WireError::UnsupportedMinor(minor));
    }
    let features = reader.u32()?;
    if features & !SUPPORTED_FEATURES != 0 {
        return Err(WireError::UnknownFeatures(features));
    }
    let tag = reader.u32()?;
    let correlation = reader.u64()?;
    let causation = reader.u64()?;
    let message = decode_body(&mut reader, tag)?;
    reader.end()?;
    Ok(Envelope { features, correlation, causation, message })
}

/// Returns the stable manifest tag for a message.
#[must_use]
pub fn message_tag(message: &Message) -> u32 {
    match message {
        Message::Subscribe { .. } => tags::SUBSCRIBE,
        Message::Unsubscribe { .. } => tags::UNSUBSCRIBE,
        Message::Snapshot { .. } => tags::SNAPSHOT,
        Message::Delta { .. } => tags::DELTA,
        Message::Reset { .. } => tags::RESET,
        Message::Heartbeat => tags::HEARTBEAT,
        Message::CheckpointAck { .. } => tags::CHECKPOINT_ACK,
        Message::AuthorizationChanged { .. } => tags::AUTHORIZATION_CHANGED,
        Message::RegionRouteChange { .. } => tags::REGION_ROUTE_CHANGE,
        Message::Error { .. } => tags::ERROR,
        Message::Backpressure { .. } => tags::BACKPRESSURE,
    }
}

/// Returns the manifest-recorded travel direction for a message.
#[must_use]
pub fn message_direction(message: &Message) -> Direction {
    match message {
        Message::Subscribe { .. } | Message::Unsubscribe { .. } | Message::CheckpointAck { .. } => {
            Direction::ClientToServer
        }
        Message::Heartbeat => Direction::Bidirectional,
        _ => Direction::ServerToClient,
    }
}

fn validate_envelope(envelope: &Envelope) -> Result<(), WireError> {
    if envelope.features & !SUPPORTED_FEATURES != 0 {
        return Err(WireError::UnknownFeatures(envelope.features));
    }
    validate_message(&envelope.message)
}

fn check_text(value: &str, min: usize, max: usize) -> Result<(), WireError> {
    let length = value.len();
    if length < min || length > max { Err(WireError::StringLimit) } else { Ok(()) }
}

fn check_binary(value: &[u8], min: usize, max: usize) -> Result<(), WireError> {
    if value.len() < min || value.len() > max { Err(WireError::BinaryLimit) } else { Ok(()) }
}

fn check_fields(fields: &[ProjectedField]) -> Result<(), WireError> {
    if fields.len() > 1024 {
        return Err(WireError::ArrayLimit);
    }
    let mut aggregate = 0usize;
    for field in fields {
        check_text(&field.path, 1, 256)?;
        check_binary(&field.value, 0, MAX_BINARY_BYTES)?;
        aggregate = aggregate.checked_add(field.value.len()).ok_or(WireError::BinaryLimit)?;
    }
    if aggregate > MAX_BINARY_BYTES {
        return Err(WireError::BinaryLimit);
    }
    Ok(())
}

fn check_item(item: &ResultItem) -> Result<(), WireError> {
    check_text(&item.identity, 0, 256)?;
    check_fields(&item.fields)
}

fn check_snapshot(snapshot: &Snapshot) -> Result<(), WireError> {
    if snapshot.items.len() > MAX_ARRAY_ITEMS {
        return Err(WireError::ArrayLimit);
    }
    for item in &snapshot.items {
        check_item(item)?;
    }
    check_text(&snapshot.digest, 0, 64)?;
    check_binary(&snapshot.checkpoint, 1, 4096)
}

fn check_operation(operation: &ResultOperation) -> Result<(), WireError> {
    match operation {
        ResultOperation::Insert { identity, fields, .. }
        | ResultOperation::Update { identity, fields } => {
            check_text(identity, 0, 256)?;
            check_fields(fields)
        }
        ResultOperation::Patch { identity, changes } => {
            check_text(identity, 0, 256)?;
            if changes.len() > 1024 {
                return Err(WireError::ArrayLimit);
            }
            let mut aggregate = 0usize;
            for change in changes {
                check_text(&change.path, 1, 256)?;
                if let Some(value) = &change.value {
                    check_binary(value, 0, MAX_BINARY_BYTES)?;
                    aggregate = aggregate.checked_add(value.len()).ok_or(WireError::BinaryLimit)?;
                }
            }
            if aggregate > MAX_BINARY_BYTES {
                return Err(WireError::BinaryLimit);
            }
            Ok(())
        }
        ResultOperation::Remove { identity } | ResultOperation::Move { identity, .. } => {
            check_text(identity, 0, 256)
        }
    }
}

fn validate_message(message: &Message) -> Result<(), WireError> {
    match message {
        Message::Subscribe { subscription, query, parameters, checkpoint } => {
            check_text(subscription, 0, 128)?;
            check_text(query, 0, 262_144)?;
            check_binary(parameters, 0, MAX_BINARY_BYTES)?;
            if let Some(checkpoint) = checkpoint {
                check_binary(checkpoint, 1, 4096)?;
            }
        }
        Message::Unsubscribe { subscription }
        | Message::Snapshot { subscription, .. }
        | Message::Reset { subscription, .. }
        | Message::CheckpointAck { subscription, .. }
        | Message::AuthorizationChanged { subscription, .. } => {
            check_text(subscription, 0, 128)?;
            match message {
                Message::Snapshot { snapshot, .. }
                | Message::Reset { snapshot, .. }
                | Message::AuthorizationChanged { snapshot, .. } => check_snapshot(snapshot)?,
                Message::CheckpointAck { checkpoint, .. } => check_binary(checkpoint, 1, 4096)?,
                _ => {}
            }
            if let Message::AuthorizationChanged { removed, .. } = message {
                if removed.len() > MAX_ARRAY_ITEMS {
                    return Err(WireError::ArrayLimit);
                }
                for identity in removed {
                    check_text(identity, 0, 256)?;
                }
            }
        }
        Message::Delta { subscription, operations, digest, checkpoint, .. } => {
            check_text(subscription, 0, 128)?;
            if operations.len() > 1024 {
                return Err(WireError::ArrayLimit);
            }
            for operation in operations {
                check_operation(operation)?;
            }
            check_text(digest, 0, 64)?;
            check_binary(checkpoint, 1, 4096)?;
        }
        Message::Heartbeat | Message::Backpressure { .. } => {}
        Message::RegionRouteChange { region } => check_text(region, 0, 64)?,
        Message::Error { detail, .. } => {
            if let Some(detail) = detail {
                check_text(detail, 0, 256)?;
            }
        }
    }
    Ok(())
}

fn reset_reason_code(reason: ResetReason) -> u32 {
    match reason {
        ResetReason::CheckpointExpired => 1,
        ResetReason::RetentionAdvanced => 2,
        ResetReason::GenerationChanged => 3,
        ResetReason::CheckpointNotCurrent => 4,
        ResetReason::ViewDiverged => 5,
        ResetReason::Oversized => 6,
        ResetReason::InvalidOperations => 7,
        ResetReason::InvalidCheckpoint => 8,
        ResetReason::Backpressure => 9,
    }
}

fn error_code_code(code: ErrorCode) -> u32 {
    match code {
        ErrorCode::UnsupportedVersion => 1,
        ErrorCode::MalformedFrame => 2,
        ErrorCode::AuthenticationExpired => 3,
        ErrorCode::PermissionDenied => 4,
        ErrorCode::CheckpointRefused => 5,
        ErrorCode::SlowConsumer => 6,
        ErrorCode::Overloaded => 7,
        ErrorCode::Unavailable => 8,
    }
}

fn backpressure_code(mode: BackpressureMode) -> u32 {
    match mode {
        BackpressureMode::Pause => 1,
        BackpressureMode::Resume => 2,
    }
}

#[derive(Default)]
struct Writer {
    bytes: Vec<u8>,
}

impl Writer {
    fn finish(self) -> Result<Vec<u8>, WireError> {
        if self.bytes.len() > MAX_MESSAGE_BYTES {
            Err(WireError::MessageLimit)
        } else {
            Ok(self.bytes)
        }
    }

    fn array(&mut self, count: usize) -> Result<(), WireError> {
        let count = u32::try_from(count).map_err(|_| WireError::ArrayLimit)?;
        if count <= 15 {
            let count = u8::try_from(count).expect("fixarray count is bounded");
            self.bytes.push(0x90 | count);
        } else if let Ok(count) = u16::try_from(count) {
            self.bytes.push(0xdc);
            self.bytes.extend_from_slice(&count.to_be_bytes());
        } else {
            self.bytes.push(0xdd);
            self.bytes.extend_from_slice(&count.to_be_bytes());
        }
        Ok(())
    }

    fn nil(&mut self) {
        self.bytes.push(0xc0);
    }

    fn u32(&mut self, value: u32) {
        self.uint(u64::from(value));
    }

    fn u64(&mut self, value: u64) {
        self.uint(value);
    }

    fn uint(&mut self, value: u64) {
        if value <= 0x7f {
            let value = u8::try_from(value).expect("positive fixint is bounded");
            self.bytes.push(value);
        } else if let Ok(value) = u8::try_from(value) {
            self.bytes.extend_from_slice(&[0xcc, value]);
        } else if let Ok(value) = u16::try_from(value) {
            self.bytes.push(0xcd);
            self.bytes.extend_from_slice(&value.to_be_bytes());
        } else if let Ok(value) = u32::try_from(value) {
            self.bytes.push(0xce);
            self.bytes.extend_from_slice(&value.to_be_bytes());
        } else {
            self.bytes.push(0xcf);
            self.bytes.extend_from_slice(&value.to_be_bytes());
        }
    }

    fn text(&mut self, value: &str) {
        let length = value.len();
        if let Ok(length) = u8::try_from(length) {
            if length <= 31 {
                self.bytes.push(0xa0 | length);
            } else {
                self.bytes.extend_from_slice(&[0xd9, length]);
            }
        } else if let Ok(length) = u16::try_from(length) {
            self.bytes.push(0xda);
            self.bytes.extend_from_slice(&length.to_be_bytes());
        } else {
            self.bytes.push(0xdb);
            self.bytes.extend_from_slice(
                &u32::try_from(length).expect("bounded text length").to_be_bytes(),
            );
        }
        self.bytes.extend_from_slice(value.as_bytes());
    }

    fn binary(&mut self, value: &[u8]) {
        let length = value.len();
        if let Ok(length) = u8::try_from(length) {
            self.bytes.extend_from_slice(&[0xc4, length]);
        } else if let Ok(length) = u16::try_from(length) {
            self.bytes.push(0xc5);
            self.bytes.extend_from_slice(&length.to_be_bytes());
        } else {
            self.bytes.push(0xc6);
            self.bytes.extend_from_slice(
                &u32::try_from(length).expect("bounded binary length").to_be_bytes(),
            );
        }
        self.bytes.extend_from_slice(value);
    }
}

fn encode_projected_field(writer: &mut Writer, field: &ProjectedField) -> Result<(), WireError> {
    writer.array(2)?;
    writer.text(&field.path);
    writer.binary(&field.value);
    Ok(())
}

fn encode_fields(writer: &mut Writer, fields: &[ProjectedField]) -> Result<(), WireError> {
    writer.array(fields.len())?;
    for field in fields {
        encode_projected_field(writer, field)?;
    }
    Ok(())
}

fn encode_snapshot(writer: &mut Writer, snapshot: &Snapshot) -> Result<(), WireError> {
    writer.array(6)?;
    writer.u64(snapshot.view_generation);
    writer.u64(snapshot.authorization_generation);
    writer.u64(snapshot.revision);
    writer.array(snapshot.items.len())?;
    for item in &snapshot.items {
        writer.array(2)?;
        writer.text(&item.identity);
        encode_fields(writer, &item.fields)?;
    }
    writer.text(&snapshot.digest);
    writer.binary(&snapshot.checkpoint);
    Ok(())
}

fn encode_operation(writer: &mut Writer, operation: &ResultOperation) -> Result<(), WireError> {
    match operation {
        ResultOperation::Insert { identity, position, fields } => {
            writer.array(4)?;
            writer.u32(1);
            writer.text(identity);
            writer.u32(*position);
            encode_fields(writer, fields)?;
        }
        ResultOperation::Update { identity, fields } => {
            writer.array(3)?;
            writer.u32(2);
            writer.text(identity);
            encode_fields(writer, fields)?;
        }
        ResultOperation::Patch { identity, changes } => {
            writer.array(3)?;
            writer.u32(3);
            writer.text(identity);
            writer.array(changes.len())?;
            for change in changes {
                writer.array(2)?;
                writer.text(&change.path);
                match &change.value {
                    Some(value) => writer.binary(value),
                    None => writer.nil(),
                }
            }
        }
        ResultOperation::Remove { identity } => {
            writer.array(2)?;
            writer.u32(4);
            writer.text(identity);
        }
        ResultOperation::Move { identity, position } => {
            writer.array(3)?;
            writer.u32(5);
            writer.text(identity);
            writer.u32(*position);
        }
    }
    Ok(())
}

fn encode_body(writer: &mut Writer, message: &Message) -> Result<(), WireError> {
    match message {
        Message::Subscribe { subscription, query, parameters, checkpoint } => {
            writer.array(4)?;
            writer.text(subscription);
            writer.text(query);
            writer.binary(parameters);
            match checkpoint {
                Some(value) => writer.binary(value),
                None => writer.nil(),
            }
        }
        Message::Unsubscribe { subscription } => {
            writer.array(1)?;
            writer.text(subscription);
        }
        Message::Snapshot { subscription, snapshot } => {
            writer.array(2)?;
            writer.text(subscription);
            encode_snapshot(writer, snapshot)?;
        }
        Message::Delta {
            subscription,
            view_generation,
            from_revision,
            to_revision,
            operations,
            digest,
            checkpoint,
        } => {
            writer.array(7)?;
            writer.text(subscription);
            writer.u64(*view_generation);
            writer.u64(*from_revision);
            writer.u64(*to_revision);
            writer.array(operations.len())?;
            for operation in operations {
                encode_operation(writer, operation)?;
            }
            writer.text(digest);
            writer.binary(checkpoint);
        }
        Message::Reset { subscription, reason, snapshot } => {
            writer.array(3)?;
            writer.text(subscription);
            writer.u32(reset_reason_code(*reason));
            encode_snapshot(writer, snapshot)?;
        }
        Message::Heartbeat => writer.array(0)?,
        Message::CheckpointAck { subscription, checkpoint } => {
            writer.array(2)?;
            writer.text(subscription);
            writer.binary(checkpoint);
        }
        Message::AuthorizationChanged {
            subscription,
            authorization_generation,
            removed,
            snapshot,
        } => {
            writer.array(4)?;
            writer.text(subscription);
            writer.u64(*authorization_generation);
            writer.array(removed.len())?;
            for identity in removed {
                writer.text(identity);
            }
            encode_snapshot(writer, snapshot)?;
        }
        Message::RegionRouteChange { region } => {
            writer.array(1)?;
            writer.text(region);
        }
        Message::Error { code, detail } => {
            writer.array(2)?;
            writer.u32(error_code_code(*code));
            match detail {
                Some(value) => writer.text(value),
                None => writer.nil(),
            }
        }
        Message::Backpressure { mode } => {
            writer.array(1)?;
            writer.u32(backpressure_code(*mode));
        }
    }
    Ok(())
}

struct Reader<'a> {
    bytes: &'a [u8],
    cursor: usize,
}

impl<'a> Reader<'a> {
    const fn new(bytes: &'a [u8]) -> Self {
        Self { bytes, cursor: 0 }
    }

    fn take(&mut self, length: usize) -> Result<&'a [u8], WireError> {
        let end = self.cursor.checked_add(length).ok_or(WireError::Truncated)?;
        if end > self.bytes.len() {
            return Err(WireError::Truncated);
        }
        let result = &self.bytes[self.cursor..end];
        self.cursor = end;
        Ok(result)
    }

    fn tag(&mut self) -> Result<u8, WireError> {
        Ok(self.take(1)?[0])
    }

    fn length(&mut self, width: usize) -> Result<usize, WireError> {
        let bytes = self.take(width)?;
        let mut value = 0usize;
        for byte in bytes {
            value = value
                .checked_mul(256)
                .and_then(|value| value.checked_add(usize::from(*byte)))
                .ok_or(WireError::ArrayLimit)?;
        }
        Ok(value)
    }

    fn integer(&mut self) -> Result<Integer, WireError> {
        let tag = self.tag()?;
        match tag {
            0x00..=0x7f => Ok(Integer::Unsigned(u64::from(tag))),
            0xe0..=0xff => Ok(Integer::Signed(i64::from(i8::from_ne_bytes([tag])))),
            0xcc => Ok(Integer::Unsigned(self.length(1)? as u64)),
            0xcd => Ok(Integer::Unsigned(self.length(2)? as u64)),
            0xce => Ok(Integer::Unsigned(self.length(4)? as u64)),
            0xcf => Ok(Integer::Unsigned(u64::from_be_bytes(
                self.take(8)?.try_into().map_err(|_| WireError::Truncated)?,
            ))),
            0xd0 => Ok(Integer::Signed(i64::from(i8::from_ne_bytes([self.take(1)?[0]])))),
            0xd1 => Ok(Integer::Signed(i64::from(i16::from_be_bytes(
                self.take(2)?.try_into().map_err(|_| WireError::Truncated)?,
            )))),
            0xd2 => Ok(Integer::Signed(i64::from(i32::from_be_bytes(
                self.take(4)?.try_into().map_err(|_| WireError::Truncated)?,
            )))),
            0xd3 => Ok(Integer::Signed(i64::from_be_bytes(
                self.take(8)?.try_into().map_err(|_| WireError::Truncated)?,
            ))),
            0xca => Err(WireError::Float32Forbidden),
            _ => Err(WireError::InvalidType),
        }
    }

    fn u32(&mut self) -> Result<u32, WireError> {
        match self.integer()? {
            Integer::Unsigned(value) => u32::try_from(value).map_err(|_| WireError::InvalidType),
            Integer::Signed(value) => u32::try_from(value).map_err(|_| WireError::InvalidType),
        }
    }

    fn u64(&mut self) -> Result<u64, WireError> {
        match self.integer()? {
            Integer::Unsigned(value) => Ok(value),
            Integer::Signed(value) => u64::try_from(value).map_err(|_| WireError::InvalidType),
        }
    }

    fn array_header(&mut self, expected: usize) -> Result<(), WireError> {
        if self.array_count()? != expected {
            return Err(WireError::InvalidShape);
        }
        Ok(())
    }

    fn array_count(&mut self) -> Result<usize, WireError> {
        let tag = self.tag()?;
        let count = match tag {
            0x90..=0x9f => usize::from(tag - 0x90),
            0xdc => self.length(2)?,
            0xdd => self.length(4)?,
            _ => return Err(WireError::InvalidShape),
        };
        if count > MAX_ARRAY_ITEMS {
            return Err(WireError::ArrayLimit);
        }
        Ok(count)
    }

    fn text(&mut self, min: usize, max: usize) -> Result<String, WireError> {
        let tag = self.tag()?;
        let length = match tag {
            0xa0..=0xbf => usize::from(tag - 0xa0),
            0xd9 => self.length(1)?,
            0xda => self.length(2)?,
            0xdb => self.length(4)?,
            _ => return Err(WireError::InvalidType),
        };
        if length < min || length > max {
            return Err(WireError::StringLimit);
        }
        String::from_utf8(self.take(length)?.to_vec()).map_err(|_| WireError::InvalidUtf8)
    }

    fn binary(&mut self, min: usize, max: usize) -> Result<Vec<u8>, WireError> {
        let tag = self.tag()?;
        let length = match tag {
            0xc4 => self.length(1)?,
            0xc5 => self.length(2)?,
            0xc6 => self.length(4)?,
            _ => return Err(WireError::InvalidType),
        };
        if length < min || length > max {
            return Err(WireError::BinaryLimit);
        }
        Ok(self.take(length)?.to_vec())
    }

    fn optional_nil(&mut self) -> bool {
        if self.cursor < self.bytes.len() && self.bytes[self.cursor] == 0xc0 {
            self.cursor += 1;
            true
        } else {
            false
        }
    }

    fn end(&self) -> Result<(), WireError> {
        if self.cursor == self.bytes.len() { Ok(()) } else { Err(WireError::TrailingBytes) }
    }
}

enum Integer {
    Unsigned(u64),
    Signed(i64),
}

fn decode_projected_field(reader: &mut Reader<'_>) -> Result<ProjectedField, WireError> {
    reader.array_header(2)?;
    let path = reader.text(1, 256)?;
    let value = reader.binary(0, MAX_BINARY_BYTES)?;
    Ok(ProjectedField { path, value })
}

fn decode_fields(reader: &mut Reader<'_>) -> Result<Vec<ProjectedField>, WireError> {
    let count = reader.array_count()?;
    if count > 1024 {
        return Err(WireError::ArrayLimit);
    }
    let mut fields = Vec::with_capacity(count);
    let mut aggregate = 0usize;
    for _ in 0..count {
        let field = decode_projected_field(reader)?;
        aggregate = aggregate.checked_add(field.value.len()).ok_or(WireError::BinaryLimit)?;
        fields.push(field);
    }
    if aggregate > MAX_BINARY_BYTES {
        return Err(WireError::BinaryLimit);
    }
    Ok(fields)
}

fn decode_snapshot(reader: &mut Reader<'_>) -> Result<Snapshot, WireError> {
    reader.array_header(6)?;
    let view_generation = reader.u64()?;
    let authorization_generation = reader.u64()?;
    let revision = reader.u64()?;
    let count = reader.array_count()?;
    let mut items = Vec::with_capacity(count);
    for _ in 0..count {
        reader.array_header(2)?;
        let identity = reader.text(0, 256)?;
        let fields = decode_fields(reader)?;
        items.push(ResultItem { identity, fields });
    }
    let digest = reader.text(0, 64)?;
    let checkpoint = reader.binary(1, 4096)?;
    Ok(Snapshot { view_generation, authorization_generation, revision, items, digest, checkpoint })
}

fn decode_operation(reader: &mut Reader<'_>) -> Result<ResultOperation, WireError> {
    let count = reader.array_count()?;
    if count > 4 {
        return Err(WireError::ArrayLimit);
    }
    let discriminant = reader.u32()?;
    match discriminant {
        1 => {
            if count != 4 {
                return Err(WireError::InvalidShape);
            }
            let identity = reader.text(0, 256)?;
            let position = reader.u32()?;
            let fields = decode_fields(reader)?;
            Ok(ResultOperation::Insert { identity, position, fields })
        }
        2 => {
            if count != 3 {
                return Err(WireError::InvalidShape);
            }
            let identity = reader.text(0, 256)?;
            let fields = decode_fields(reader)?;
            Ok(ResultOperation::Update { identity, fields })
        }
        3 => {
            if count != 3 {
                return Err(WireError::InvalidShape);
            }
            let identity = reader.text(0, 256)?;
            let changes_count = reader.array_count()?;
            if changes_count > 1024 {
                return Err(WireError::ArrayLimit);
            }
            let mut changes = Vec::with_capacity(changes_count);
            let mut aggregate = 0usize;
            for _ in 0..changes_count {
                reader.array_header(2)?;
                let path = reader.text(1, 256)?;
                let value = if reader.optional_nil() {
                    None
                } else {
                    Some(reader.binary(0, MAX_BINARY_BYTES)?)
                };
                if let Some(value) = &value {
                    aggregate = aggregate.checked_add(value.len()).ok_or(WireError::BinaryLimit)?;
                }
                changes.push(FieldChange { path, value });
            }
            if aggregate > MAX_BINARY_BYTES {
                return Err(WireError::BinaryLimit);
            }
            Ok(ResultOperation::Patch { identity, changes })
        }
        4 => {
            if count != 2 {
                return Err(WireError::InvalidShape);
            }
            Ok(ResultOperation::Remove { identity: reader.text(0, 256)? })
        }
        5 => {
            if count != 3 {
                return Err(WireError::InvalidShape);
            }
            let identity = reader.text(0, 256)?;
            let position = reader.u32()?;
            Ok(ResultOperation::Move { identity, position })
        }
        value => Err(WireError::UnknownVariant(value)),
    }
}

fn decode_enum(reader: &mut Reader<'_>, name: &'static str, max: u32) -> Result<u32, WireError> {
    let value = reader.u32()?;
    if value == 0 || value > max { Err(WireError::UnknownEnum(name, value)) } else { Ok(value) }
}

fn decode_reset_reason(reader: &mut Reader<'_>) -> Result<ResetReason, WireError> {
    Ok(match decode_enum(reader, "reset_reason", 9)? {
        1 => ResetReason::CheckpointExpired,
        2 => ResetReason::RetentionAdvanced,
        3 => ResetReason::GenerationChanged,
        4 => ResetReason::CheckpointNotCurrent,
        5 => ResetReason::ViewDiverged,
        6 => ResetReason::Oversized,
        7 => ResetReason::InvalidOperations,
        8 => ResetReason::InvalidCheckpoint,
        9 => ResetReason::Backpressure,
        _ => unreachable!(),
    })
}

fn decode_error_code(reader: &mut Reader<'_>) -> Result<ErrorCode, WireError> {
    Ok(match decode_enum(reader, "error_code", 8)? {
        1 => ErrorCode::UnsupportedVersion,
        2 => ErrorCode::MalformedFrame,
        3 => ErrorCode::AuthenticationExpired,
        4 => ErrorCode::PermissionDenied,
        5 => ErrorCode::CheckpointRefused,
        6 => ErrorCode::SlowConsumer,
        7 => ErrorCode::Overloaded,
        8 => ErrorCode::Unavailable,
        _ => unreachable!(),
    })
}

fn decode_backpressure(reader: &mut Reader<'_>) -> Result<BackpressureMode, WireError> {
    Ok(match decode_enum(reader, "backpressure_mode", 2)? {
        1 => BackpressureMode::Pause,
        2 => BackpressureMode::Resume,
        _ => unreachable!(),
    })
}

fn decode_body(reader: &mut Reader<'_>, tag: u32) -> Result<Message, WireError> {
    match tag {
        tags::SUBSCRIBE => {
            reader.array_header(4)?;
            let subscription = reader.text(0, 128)?;
            let query = reader.text(0, 262_144)?;
            let parameters = reader.binary(0, MAX_BINARY_BYTES)?;
            let checkpoint =
                if reader.optional_nil() { None } else { Some(reader.binary(1, 4096)?) };
            Ok(Message::Subscribe { subscription, query, parameters, checkpoint })
        }
        tags::UNSUBSCRIBE => {
            reader.array_header(1)?;
            Ok(Message::Unsubscribe { subscription: reader.text(0, 128)? })
        }
        tags::SNAPSHOT => {
            reader.array_header(2)?;
            let subscription = reader.text(0, 128)?;
            Ok(Message::Snapshot { subscription, snapshot: decode_snapshot(reader)? })
        }
        tags::DELTA => {
            reader.array_header(7)?;
            let subscription = reader.text(0, 128)?;
            let view_generation = reader.u64()?;
            let from_revision = reader.u64()?;
            let to_revision = reader.u64()?;
            let count = reader.array_count()?;
            if count > 1024 {
                return Err(WireError::ArrayLimit);
            }
            let mut operations = Vec::with_capacity(count);
            for _ in 0..count {
                operations.push(decode_operation(reader)?);
            }
            let digest = reader.text(0, 64)?;
            let checkpoint = reader.binary(1, 4096)?;
            Ok(Message::Delta {
                subscription,
                view_generation,
                from_revision,
                to_revision,
                operations,
                digest,
                checkpoint,
            })
        }
        tags::RESET => {
            reader.array_header(3)?;
            let subscription = reader.text(0, 128)?;
            let reason = decode_reset_reason(reader)?;
            let snapshot = decode_snapshot(reader)?;
            Ok(Message::Reset { subscription, reason, snapshot })
        }
        tags::HEARTBEAT => {
            reader.array_header(0)?;
            Ok(Message::Heartbeat)
        }
        tags::CHECKPOINT_ACK => {
            reader.array_header(2)?;
            let subscription = reader.text(0, 128)?;
            let checkpoint = reader.binary(1, 4096)?;
            Ok(Message::CheckpointAck { subscription, checkpoint })
        }
        tags::AUTHORIZATION_CHANGED => {
            reader.array_header(4)?;
            let subscription = reader.text(0, 128)?;
            let authorization_generation = reader.u64()?;
            let count = reader.array_count()?;
            let mut removed = Vec::with_capacity(count);
            for _ in 0..count {
                removed.push(reader.text(0, 256)?);
            }
            let snapshot = decode_snapshot(reader)?;
            Ok(Message::AuthorizationChanged {
                subscription,
                authorization_generation,
                removed,
                snapshot,
            })
        }
        tags::REGION_ROUTE_CHANGE => {
            reader.array_header(1)?;
            Ok(Message::RegionRouteChange { region: reader.text(0, 64)? })
        }
        tags::ERROR => {
            reader.array_header(2)?;
            let code = decode_error_code(reader)?;
            let detail = if reader.optional_nil() { None } else { Some(reader.text(0, 256)?) };
            Ok(Message::Error { code, detail })
        }
        tags::BACKPRESSURE => {
            reader.array_header(1)?;
            Ok(Message::Backpressure { mode: decode_backpressure(reader)? })
        }
        value => Err(WireError::UnknownTag(value)),
    }
}

fn scan(bytes: &[u8]) -> Result<(), WireError> {
    if bytes.len() > MAX_MESSAGE_BYTES {
        return Err(WireError::MessageLimit);
    }
    let mut scanner = Scanner { bytes, cursor: 0, depth: 0 };
    scanner.value()?;
    if scanner.cursor == bytes.len() { Ok(()) } else { Err(WireError::TrailingBytes) }
}

struct Scanner<'a> {
    bytes: &'a [u8],
    cursor: usize,
    depth: usize,
}

impl Scanner<'_> {
    fn take(&mut self, count: usize) -> Result<(), WireError> {
        let end = self.cursor.checked_add(count).ok_or(WireError::Truncated)?;
        if end > self.bytes.len() {
            return Err(WireError::Truncated);
        }
        self.cursor = end;
        Ok(())
    }

    fn length(&mut self, width: usize) -> Result<usize, WireError> {
        let start = self.cursor;
        self.take(width)?;
        let mut value = 0usize;
        for byte in &self.bytes[start..self.cursor] {
            value = value
                .checked_mul(256)
                .and_then(|value| value.checked_add(usize::from(*byte)))
                .ok_or(WireError::ArrayLimit)?;
        }
        Ok(value)
    }

    fn value(&mut self) -> Result<(), WireError> {
        let tag = self.bytes.get(self.cursor).copied().ok_or(WireError::Truncated)?;
        self.cursor += 1;
        match tag {
            0x00..=0x7f | 0xe0..=0xff | 0xc0 | 0xc2 | 0xc3 => Ok(()),
            0xc1 => Err(WireError::InvalidType),
            0xca => Err(WireError::Float32Forbidden),
            0xcb | 0xcf | 0xd3 => self.take(8),
            0xcc | 0xd0 => self.take(1),
            0xcd | 0xd1 => self.take(2),
            0xce | 0xd2 => self.take(4),
            0xc4..=0xc6 => {
                let width = usize::from(tag - 0xc4) + 1;
                let length = self.length(width)?;
                if length > MAX_BINARY_BYTES {
                    return Err(WireError::BinaryLimit);
                }
                self.take(length)
            }
            0xc7..=0xc9 | 0xd4..=0xd8 => Err(WireError::ExtensionForbidden),
            0xa0..=0xbf => {
                let length = usize::from(tag - 0xa0);
                if length > MAX_STRING_BYTES {
                    return Err(WireError::StringLimit);
                }
                self.take(length)
            }
            0xd9..=0xdb => {
                let width = usize::from(tag - 0xd9) + 1;
                let length = self.length(width)?;
                if length > MAX_STRING_BYTES {
                    return Err(WireError::StringLimit);
                }
                self.take(length)
            }
            0x90..=0x9f => self.array(usize::from(tag - 0x90)),
            0xdc => {
                let count = self.length(2)?;
                self.array(count)
            }
            0xdd => {
                let count = self.length(4)?;
                self.array(count)
            }
            0x80..=0x8f | 0xde | 0xdf => Err(WireError::MapForbidden),
        }
    }

    fn array(&mut self, count: usize) -> Result<(), WireError> {
        if count > MAX_ARRAY_ITEMS {
            return Err(WireError::ArrayLimit);
        }
        self.depth += 1;
        if self.depth > MAX_DEPTH {
            return Err(WireError::ArrayLimit);
        }
        for _ in 0..count {
            self.value()?;
        }
        self.depth -= 1;
        Ok(())
    }
}
