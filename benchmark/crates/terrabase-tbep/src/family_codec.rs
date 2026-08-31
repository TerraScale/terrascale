//! Canonical protobuf payloads for the advertised data-family operations.
//!
//! The gRPC envelope deliberately keeps `request` and `response` opaque.  A
//! family payload is nevertheless a closed, versioned protobuf oneof so a
//! receiver can reject unknown operations, malformed bytes, and an outer
//! family/tag mismatch before dispatching to a host.

use prost::Message;

use crate::{proto::terrabase::v1 as pb, registry};

/// The v1 request payload codec identifier.
pub const REQUEST_CODEC: &str = "terrabase.family-request/1";
/// The v1 response payload codec identifier.
pub const RESPONSE_CODEC: &str = "terrabase.family-response/1";
/// The maximum encoded family payload accepted by this codec.
pub const MAX_PAYLOAD_BYTES: usize = registry::MAX_FRAME_BYTES;

/// A malformed or semantically unsupported family payload.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum FamilyCodecError {
    /// The encoded payload exceeds [`MAX_PAYLOAD_BYTES`].
    PayloadTooLarge {
        /// Encoded byte count presented by the caller.
        actual: usize,
        /// Maximum encoded byte count accepted by the codec.
        bound: usize,
    },
    /// Prost could not decode the protobuf bytes.
    Malformed {
        /// Bounded decoder diagnostic.
        detail: String,
    },
    /// The message contained no operation/result oneof arm.
    MissingVariant {
        /// Static protobuf payload type name.
        payload: &'static str,
    },
    /// Decoding and canonical re-encoding did not preserve the bytes.
    NonCanonical,
    /// The payload's oneof arm belongs to a different advertised family.
    FamilyMismatch {
        /// Family advertised by the outer request.
        expected: String,
        /// Family encoded by the payload variant.
        actual: String,
    },
    /// The outer codec identifier does not match this payload family.
    CodecMismatch {
        /// Static codec identifier required by this operation.
        expected: &'static str,
        /// Codec identifier presented by the outer request.
        actual: String,
    },
    /// A DTO field violates a family operation's semantic bounds.
    InvalidField {
        /// Field name that failed validation.
        field: &'static str,
    },
}

impl std::fmt::Display for FamilyCodecError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::PayloadTooLarge { actual, bound } => {
                write!(f, "family payload is {actual} bytes; maximum is {bound}")
            }
            Self::Malformed { detail } => write!(f, "malformed family protobuf: {detail}"),
            Self::MissingVariant { payload } => write!(f, "{payload} has no oneof variant"),
            Self::NonCanonical => f.write_str("family protobuf is not canonical"),
            Self::FamilyMismatch { expected, actual } => {
                write!(f, "family payload is for {actual}, outer family is {expected}")
            }
            Self::CodecMismatch { expected, actual } => {
                write!(f, "family codec is {actual}, expected {expected}")
            }
            Self::InvalidField { field } => write!(f, "invalid family payload field {field}"),
        }
    }
}

impl std::error::Error for FamilyCodecError {}

fn bounded_encode<M: Message>(
    message: &M,
    payload: &'static str,
) -> Result<Vec<u8>, FamilyCodecError> {
    if message.encoded_len() > MAX_PAYLOAD_BYTES {
        return Err(FamilyCodecError::PayloadTooLarge {
            actual: message.encoded_len(),
            bound: MAX_PAYLOAD_BYTES,
        });
    }
    if message.encoded_len() == 0 {
        return Err(FamilyCodecError::MissingVariant { payload });
    }
    Ok(message.encode_to_vec())
}

fn bounded_decode<M: Message + Default>(
    bytes: &[u8],
    payload: &'static str,
) -> Result<M, FamilyCodecError> {
    if bytes.len() > MAX_PAYLOAD_BYTES {
        return Err(FamilyCodecError::PayloadTooLarge {
            actual: bytes.len(),
            bound: MAX_PAYLOAD_BYTES,
        });
    }
    let message = M::decode(bytes)
        .map_err(|error| FamilyCodecError::Malformed { detail: error.to_string() })?;
    if message.encoded_len() == 0 {
        return Err(FamilyCodecError::MissingVariant { payload });
    }
    if message.encode_to_vec() != bytes {
        return Err(FamilyCodecError::NonCanonical);
    }
    Ok(message)
}

fn invalid(field: &'static str) -> FamilyCodecError {
    FamilyCodecError::InvalidField { field }
}

fn identifier(value: &str, field: &'static str) -> Result<(), FamilyCodecError> {
    if value.is_empty()
        || value.len() > 255
        || value != value.trim()
        || value.chars().any(char::is_control)
    {
        return Err(invalid(field));
    }
    Ok(())
}

fn bounded_list<T>(value: &[T], field: &'static str) -> Result<(), FamilyCodecError> {
    if value.len() > registry::MAX_LIST_ITEMS { Err(invalid(field)) } else { Ok(()) }
}

fn digest(value: &str, field: &'static str) -> Result<(), FamilyCodecError> {
    if value.len() != 64
        || !value.bytes().all(|byte| byte.is_ascii_hexdigit() && !byte.is_ascii_uppercase())
    {
        return Err(invalid(field));
    }
    Ok(())
}

fn unique_names(values: &[String], field: &'static str) -> Result<(), FamilyCodecError> {
    let mut seen = values.to_vec();
    seen.sort_unstable();
    if seen.windows(2).any(|pair| pair[0] == pair[1]) { Err(invalid(field)) } else { Ok(()) }
}

fn validate_expected(value: Option<&pb::ExpectedVersion>) -> Result<(), FamilyCodecError> {
    if value.is_some_and(|value| value.discipline.is_none()) {
        Err(invalid("expected"))
    } else {
        Ok(())
    }
}

fn validate_value(
    value: Option<&pb::Value>,
    depth: usize,
    nodes: &mut usize,
) -> Result<(), FamilyCodecError> {
    let Some(value) = value else {
        return Err(invalid("value"));
    };
    *nodes = nodes.checked_add(1).ok_or_else(|| invalid("value.nodes"))?;
    if depth > 64 || *nodes > 4096 {
        return Err(invalid("value.depth"));
    }
    match value.kind.as_ref() {
        Some(pb::value::Kind::Null(value)) if *value != 1 => Err(invalid("value.null")),
        Some(pb::value::Kind::Number(number)) if !number.is_finite() => {
            Err(invalid("value.number"))
        }
        Some(pb::value::Kind::Text(text)) if text.len() > 1_048_576 => Err(invalid("value.text")),
        Some(pb::value::Kind::List(items)) => {
            bounded_list(&items.items, "value.list")?;
            for item in &items.items {
                validate_value(Some(item), depth + 1, nodes)?;
            }
            Ok(())
        }
        Some(pb::value::Kind::Map(map)) => {
            bounded_list(&map.entries, "value.map")?;
            let mut previous = None;
            for entry in &map.entries {
                identifier(&entry.name, "value.map.name")?;
                if previous.is_some_and(|previous: &str| previous >= entry.name.as_str()) {
                    return Err(invalid("value.map.order"));
                }
                previous = Some(entry.name.as_str());
                validate_value(entry.value.as_ref(), depth + 1, nodes)?;
            }
            Ok(())
        }
        Some(_) => Ok(()),
        None => Err(invalid("value")),
    }
}

#[allow(
    clippy::too_many_lines,
    reason = "the closed request oneof is validated in one exhaustive match"
)]
fn validate_request_payload(payload: &pb::FamilyRequestPayload) -> Result<(), FamilyCodecError> {
    use pb::family_attachment_write::Mutation;
    use pb::family_request_payload::Operation;
    let Some(operation) = payload.operation.as_ref() else {
        return Err(FamilyCodecError::MissingVariant { payload: "FamilyRequestPayload" });
    };
    match operation {
        Operation::AttachmentRead(value) => identifier(&value.document, "document")?,
        Operation::CounterRead(value) => {
            identifier(&value.document, "document")?;
            bounded_list(&value.names, "names")?;
            for name in &value.names {
                identifier(name, "names")?;
            }
            unique_names(&value.names, "names")?;
        }
        Operation::TimeSeriesRead(value) => {
            identifier(&value.document, "document")?;
            identifier(&value.name, "name")?;
            if value.range_start.zip(value.range_end).is_some_and(|(start, end)| start > end) {
                return Err(invalid("range"));
            }
            if value.range_start.is_some() != value.range_end.is_some() {
                return Err(invalid("range"));
            }
            if value.limit.is_some_and(|limit| {
                limit == 0
                    || usize::try_from(limit).unwrap_or(usize::MAX) > registry::MAX_LIST_ITEMS
            }) {
                return Err(invalid("limit"));
            }
        }
        Operation::RevisionRead(value) => {
            identifier(&value.document, "document")?;
            if value.limit == 0
                || usize::try_from(value.limit).unwrap_or(usize::MAX) > registry::MAX_LIST_ITEMS
            {
                return Err(invalid("limit"));
            }
        }
        Operation::CompareExchangeRead(value) => identifier(&value.key, "key")?,
        Operation::IdentityRead(value) => {
            if value.names.is_empty() {
                return Err(invalid("names"));
            }
            bounded_list(&value.names, "names")?;
            for name in &value.names {
                identifier(name, "names")?;
            }
            unique_names(&value.names, "names")?;
        }
        Operation::LifecycleRead(value) => identifier(&value.document, "document")?,
        Operation::ArchivalRead(value) => identifier(&value.document, "document")?,
        Operation::AttachmentWrite(value) => {
            identifier(&value.document, "document")?;
            validate_expected(value.expected.as_ref())?;
            let Some(mutation) = value.mutation.as_ref() else {
                return Err(invalid("mutation"));
            };
            match mutation {
                Mutation::Store(value) => {
                    identifier(&value.name, "name")?;
                    identifier(&value.content_type, "content_type")?;
                    digest(&value.hash, "hash")?;
                }
                Mutation::Delete(value) => identifier(&value.name, "name")?,
                Mutation::Rename(value) => {
                    identifier(&value.from, "from")?;
                    identifier(&value.to, "to")?;
                }
                Mutation::Copy(value) => {
                    identifier(&value.name, "name")?;
                    identifier(&value.to_document, "to_document")?;
                    identifier(&value.to_name, "to_name")?;
                }
                Mutation::Move(value) => {
                    identifier(&value.name, "name")?;
                    identifier(&value.to_document, "to_document")?;
                    identifier(&value.to_name, "to_name")?;
                }
            }
        }
        Operation::AttachmentContentRead(value) => {
            digest(&value.digest, "digest")?;
            if let Some(pb::family_attachment_content_read::Range::Bytes(range)) =
                value.range.as_ref()
                && range.to.is_some_and(|to| to < range.from)
            {
                return Err(invalid("range"));
            }
            if matches!(value.range, Some(pb::family_attachment_content_read::Range::Suffix(0))) {
                return Err(invalid("suffix"));
            }
        }
        Operation::AttachmentContentWrite(value) => {
            if let Some(expected_digest) = value.expected_digest.as_deref() {
                digest(expected_digest, "expected_digest")?;
            }
            bounded_list(&value.segments, "segments")?;
            if value.segments.len() > 128 {
                return Err(invalid("segments"));
            }
            let total = value.segments.iter().try_fold(0usize, |total, segment| {
                if segment.len() > 64 * 1024 {
                    return Err(invalid("segments"));
                }
                total.checked_add(segment.len()).ok_or_else(|| invalid("segments"))
            })?;
            if total > 8 * 1024 * 1024 {
                return Err(invalid("segments"));
            }
        }
        Operation::AttachmentContentDelete(value) => {
            digest(&value.digest, "digest")?;
        }
        Operation::CounterWrite(value) => {
            identifier(&value.document, "document")?;
            validate_expected(value.expected.as_ref())?;
            bounded_list(&value.operations, "operations")?;
            if value.operations.is_empty() {
                return Err(invalid("operations"));
            }
            for operation in &value.operations {
                let Some(operation) = operation.operation.as_ref() else {
                    return Err(invalid("operation"));
                };
                match operation {
                    pb::family_counter_operation::Operation::Increment(value) => {
                        identifier(&value.name, "name")?;
                    }
                    pb::family_counter_operation::Operation::Delete(value) => {
                        identifier(&value.name, "name")?;
                    }
                }
            }
        }
        Operation::TimeSeriesWrite(value) => {
            identifier(&value.document, "document")?;
            identifier(&value.name, "name")?;
            validate_expected(value.expected.as_ref())?;
            bounded_list(&value.appends, "appends")?;
            bounded_list(&value.deletes, "deletes")?;
            if value.appends.is_empty() && value.deletes.is_empty() && value.policy.is_none() {
                return Err(invalid("operations"));
            }
            for append in &value.appends {
                if append.values.is_empty()
                    || append.values.len() > registry::MAX_LIST_ITEMS
                    || append.values.iter().any(|value| !value.is_finite())
                {
                    return Err(invalid("values"));
                }
            }
            for delete in &value.deletes {
                let Some(deletion) = delete.deletion.as_ref() else {
                    return Err(invalid("deletion"));
                };
                if let pb::family_time_series_delete::Deletion::Range(range) = deletion
                    && range.start > range.end
                {
                    return Err(invalid("range"));
                }
            }
            if let Some(policy) = &value.policy {
                if policy.retention_ms.is_some_and(|retention| retention < 0) {
                    return Err(invalid("retention_ms"));
                }
                bounded_list(&policy.rollups, "rollups")?;
                for rollup in &policy.rollups {
                    identifier(&rollup.name, "rollup.name")?;
                    if !(1..=5).contains(&rollup.aggregation) || rollup.window_ms <= 0 {
                        return Err(invalid("rollup"));
                    }
                }
            }
        }
        Operation::RevisionWrite(value) => {
            identifier(&value.document, "document")?;
            let Some(command) = value.command.as_ref() else {
                return Err(invalid("command"));
            };
            if let pb::family_revision_write::Command::ForceCreate(value) = command {
                validate_expected(value.expected.as_ref())?;
            }
            if let pb::family_revision_write::Command::Revert(value) = command
                && value.to_revision == 0
            {
                return Err(invalid("to_revision"));
            }
            if let pb::family_revision_write::Command::Configure(value) = command
                && (value.minimum_revisions == 0
                    || value
                        .maximum_revisions
                        .is_some_and(|maximum| maximum < value.minimum_revisions))
            {
                return Err(invalid("revisions"));
            }
        }
        Operation::CompareExchangeWrite(value) => {
            identifier(&value.key, "key")?;
            let Some(condition) = value.condition.as_ref() else {
                return Err(invalid("condition"));
            };
            match condition {
                pb::family_compare_exchange_write::Condition::PutIfIndex(value) => {
                    if value.index == 0 {
                        return Err(invalid("index"));
                    }
                    validate_value(value.value.as_ref(), 0, &mut 0)?;
                    if value.expires_at.is_some_and(|expires_at| expires_at < 0) {
                        return Err(invalid("expires_at"));
                    }
                }
                pb::family_compare_exchange_write::Condition::PutIfMissing(value) => {
                    validate_value(value.value.as_ref(), 0, &mut 0)?;
                    if value.expires_at.is_some_and(|expires_at| expires_at < 0) {
                        return Err(invalid("expires_at"));
                    }
                }
                pb::family_compare_exchange_write::Condition::DeleteIfIndex(value)
                    if value.index == 0 =>
                {
                    return Err(invalid("index"));
                }
                pb::family_compare_exchange_write::Condition::DeleteIfIndex(_) => {}
            }
        }
        Operation::IdentityWrite(value) => {
            let Some(command) = value.command.as_ref() else {
                return Err(invalid("command"));
            };
            match command {
                pb::family_identity_write::Command::Allocate(value) => {
                    identifier(&value.name, "name")?;
                    if value.count == 0 {
                        return Err(invalid("count"));
                    }
                }
                pb::family_identity_write::Command::Seed(value) => identifier(&value.name, "name")?,
            }
        }
        Operation::LifecycleWrite(value) => {
            identifier(&value.document, "document")?;
            if value.command.is_none() {
                return Err(invalid("command"));
            }
        }
        Operation::ArchivalWrite(value) => {
            identifier(&value.document, "document")?;
            if value.command.is_none() {
                return Err(invalid("command"));
            }
        }
    }
    Ok(())
}

#[allow(
    clippy::too_many_lines,
    reason = "the closed response oneof is validated in one exhaustive match"
)]
fn validate_response_payload(payload: &pb::FamilyResponsePayload) -> Result<(), FamilyCodecError> {
    use pb::family_response_payload::Result;
    let Some(result) = payload.result.as_ref() else {
        return Err(FamilyCodecError::MissingVariant { payload: "FamilyResponsePayload" });
    };
    match result {
        Result::Attachment(value) => {
            bounded_list(&value.entries, "entries")?;
            let mut names: Vec<&str> = Vec::with_capacity(value.entries.len());
            for entry in &value.entries {
                identifier(&entry.name, "name")?;
                identifier(&entry.content_type, "content_type")?;
                identifier(&entry.document, "document")?;
                digest(&entry.hash, "hash")?;
                names.push(&entry.name);
            }
            names.sort_unstable();
            if names.windows(2).any(|pair| pair[0] == pair[1]) {
                return Err(invalid("entries"));
            }
        }
        Result::Counter(value) => {
            bounded_list(&value.entries, "entries")?;
            let mut names: Vec<&str> = Vec::with_capacity(value.entries.len());
            for entry in &value.entries {
                identifier(&entry.name, "name")?;
                names.push(&entry.name);
            }
            names.sort_unstable();
            if names.windows(2).any(|pair| pair[0] == pair[1]) {
                return Err(invalid("entries"));
            }
        }
        Result::TimeSeries(value) => {
            bounded_list(&value.entries, "entries")?;
            for entry in &value.entries {
                if entry.values.iter().any(|value| !value.is_finite()) {
                    return Err(invalid("values"));
                }
            }
        }
        Result::Revisions(value) => {
            bounded_list(&value.entries, "entries")?;
            for entry in &value.entries {
                validate_value(entry.value.as_ref(), 0, &mut 0)?;
            }
        }
        Result::CompareExchange(value) => {
            identifier(&value.key, "key")?;
            if let Some(entry) = &value.entry {
                validate_value(entry.value.as_ref(), 0, &mut 0)?;
            }
        }
        Result::Identities(value) => {
            bounded_list(&value.entries, "entries")?;
            let mut names: Vec<&str> = Vec::with_capacity(value.entries.len());
            for entry in &value.entries {
                identifier(&entry.name, "name")?;
                names.push(&entry.name);
            }
            names.sort_unstable();
            if names.windows(2).any(|pair| pair[0] == pair[1]) {
                return Err(invalid("entries"));
            }
        }
        Result::ExpirationRefresh(_) | Result::Archival(_) => {}
        Result::Write(value) => {
            if value.sequence == 0 {
                return Err(invalid("sequence"));
            }
            identifier(&value.operation_id, "operation_id")?;
        }
        Result::AttachmentContent(value) => {
            if !value.digest.is_empty() {
                digest(&value.digest, "digest")?;
            }
            if value.digest.len() != 64 && !value.digest.is_empty() {
                return Err(invalid("digest"));
            }
            bounded_list(&value.chunks, "chunks")?;
            let mut total = 0usize;
            let mut previous_offset = None;
            let mut saw_last = false;
            for (index, chunk) in value.chunks.iter().enumerate() {
                if previous_offset.is_some_and(|offset| chunk.offset < offset)
                    || (chunk.last && index + 1 != value.chunks.len())
                    || (saw_last && !chunk.bytes.is_empty())
                {
                    return Err(invalid("chunks.order"));
                }
                if chunk.bytes.len() > 64 * 1024 || chunk.bytes.len().checked_add(total).is_none() {
                    return Err(invalid("chunks"));
                }
                total += chunk.bytes.len();
                previous_offset = Some(chunk.offset);
                saw_last |= chunk.last;
            }
            if total > 8 * 1024 * 1024 {
                return Err(invalid("chunks"));
            }
            if !value.chunks.is_empty() && value.size != total as u64 {
                return Err(invalid("size"));
            }
        }
    }
    Ok(())
}

/// Encodes a canonical v1 family request payload.
///
/// # Errors
///
/// Returns an error if the request is invalid or exceeds the payload bound.
pub fn encode_request(payload: &pb::FamilyRequestPayload) -> Result<Vec<u8>, FamilyCodecError> {
    validate_request_payload(payload)?;
    bounded_encode(payload, "FamilyRequestPayload")
}

/// Decodes a canonical v1 family request payload.
///
/// # Errors
///
/// Returns an error if the bytes are malformed, non-canonical, invalid, or
/// exceed the payload bound.
pub fn decode_request(bytes: &[u8]) -> Result<pb::FamilyRequestPayload, FamilyCodecError> {
    let payload = bounded_decode::<pb::FamilyRequestPayload>(bytes, "FamilyRequestPayload")?;
    validate_request_payload(&payload)?;
    Ok(payload)
}

/// Encodes a canonical v1 family response payload.
///
/// # Errors
///
/// Returns an error if the response is invalid or exceeds the payload bound.
pub fn encode_response(payload: &pb::FamilyResponsePayload) -> Result<Vec<u8>, FamilyCodecError> {
    validate_response_payload(payload)?;
    bounded_encode(payload, "FamilyResponsePayload")
}

/// Decodes a canonical v1 family response payload.
///
/// # Errors
///
/// Returns an error if the bytes are malformed, non-canonical, invalid, or
/// exceed the payload bound.
pub fn decode_response(bytes: &[u8]) -> Result<pb::FamilyResponsePayload, FamilyCodecError> {
    let payload = bounded_decode::<pb::FamilyResponsePayload>(bytes, "FamilyResponsePayload")?;
    validate_response_payload(&payload)?;
    Ok(payload)
}

/// Returns the advertised family selected by a request payload.
///
/// # Errors
///
/// Returns an error if the request has no operation variant.
pub fn request_family(
    payload: &pb::FamilyRequestPayload,
) -> Result<&'static str, FamilyCodecError> {
    use pb::family_request_payload::Operation;
    match payload.operation.as_ref() {
        Some(
            Operation::AttachmentRead(_)
            | Operation::AttachmentWrite(_)
            | Operation::AttachmentContentRead(_)
            | Operation::AttachmentContentWrite(_)
            | Operation::AttachmentContentDelete(_),
        ) => Ok("attachment"),
        Some(Operation::CounterRead(_) | Operation::CounterWrite(_)) => Ok("counter"),
        Some(Operation::TimeSeriesRead(_) | Operation::TimeSeriesWrite(_)) => Ok("time_series"),
        Some(Operation::RevisionRead(_) | Operation::RevisionWrite(_)) => Ok("revisions"),
        Some(Operation::CompareExchangeRead(_) | Operation::CompareExchangeWrite(_)) => {
            Ok("compare_exchange")
        }
        Some(Operation::IdentityRead(_) | Operation::IdentityWrite(_)) => Ok("identities"),
        Some(Operation::LifecycleRead(_) | Operation::LifecycleWrite(_)) => {
            Ok("expiration_refresh")
        }
        Some(Operation::ArchivalRead(_) | Operation::ArchivalWrite(_)) => Ok("archival"),
        None => Err(FamilyCodecError::MissingVariant { payload: "FamilyRequestPayload" }),
    }
}

/// Returns whether a request payload selects a write operation.
///
/// # Errors
///
/// Returns an error if the request has no operation variant.
pub fn request_is_write(payload: &pb::FamilyRequestPayload) -> Result<bool, FamilyCodecError> {
    use pb::family_request_payload::Operation;
    Ok(match payload.operation.as_ref() {
        Some(
            Operation::AttachmentWrite(_)
            | Operation::CounterWrite(_)
            | Operation::TimeSeriesWrite(_)
            | Operation::RevisionWrite(_)
            | Operation::CompareExchangeWrite(_)
            | Operation::IdentityWrite(_)
            | Operation::LifecycleWrite(_)
            | Operation::ArchivalWrite(_)
            | Operation::AttachmentContentWrite(_)
            | Operation::AttachmentContentDelete(_),
        ) => true,
        Some(_) => false,
        None => return Err(FamilyCodecError::MissingVariant { payload: "FamilyRequestPayload" }),
    })
}

/// Returns the family selected by a response payload. Write receipts have no
/// family tag of their own and therefore return `None`.
///
/// # Errors
///
/// Returns an error if the response has no result variant.
pub fn response_family(
    payload: &pb::FamilyResponsePayload,
) -> Result<Option<&'static str>, FamilyCodecError> {
    use pb::family_response_payload::Result;
    match payload.result.as_ref() {
        Some(Result::Attachment(_) | Result::AttachmentContent(_)) => Ok(Some("attachment")),
        Some(Result::Counter(_)) => Ok(Some("counter")),
        Some(Result::TimeSeries(_)) => Ok(Some("time_series")),
        Some(Result::Revisions(_)) => Ok(Some("revisions")),
        Some(Result::CompareExchange(_)) => Ok(Some("compare_exchange")),
        Some(Result::Identities(_)) => Ok(Some("identities")),
        Some(Result::ExpirationRefresh(_)) => Ok(Some("expiration_refresh")),
        Some(Result::Archival(_)) => Ok(Some("archival")),
        Some(Result::Write(_)) => Ok(None),
        None => Err(FamilyCodecError::MissingVariant { payload: "FamilyResponsePayload" }),
    }
}

/// Validates a decoded request against the outer family and request codec.
///
/// # Errors
///
/// Returns an error if the payload is invalid or its family or codec does not
/// match the outer envelope.
pub fn validate_request_family(
    family: &str,
    request_codec: &str,
    payload: &pb::FamilyRequestPayload,
) -> Result<(), FamilyCodecError> {
    if request_codec != REQUEST_CODEC {
        return Err(FamilyCodecError::CodecMismatch {
            expected: REQUEST_CODEC,
            actual: request_codec.to_owned(),
        });
    }
    validate_request_payload(payload)?;
    let actual = request_family(payload)?;
    if actual != family {
        return Err(FamilyCodecError::FamilyMismatch {
            expected: family.to_owned(),
            actual: actual.to_owned(),
        });
    }
    Ok(())
}

/// Validates a decoded response against the outer family and response codec.
///
/// # Errors
///
/// Returns an error if the payload is invalid or its family or codec does not
/// match the outer envelope.
pub fn validate_response_family(
    family: &str,
    response_codec: &str,
    payload: &pb::FamilyResponsePayload,
) -> Result<(), FamilyCodecError> {
    if response_codec != RESPONSE_CODEC {
        return Err(FamilyCodecError::CodecMismatch {
            expected: RESPONSE_CODEC,
            actual: response_codec.to_owned(),
        });
    }
    validate_response_payload(payload)?;
    if let Some(actual) = response_family(payload)?
        && actual != family
    {
        return Err(FamilyCodecError::FamilyMismatch {
            expected: family.to_owned(),
            actual: actual.to_owned(),
        });
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn minimal_request_golden_and_round_trip() {
        let payload = pb::FamilyRequestPayload {
            operation: Some(pb::family_request_payload::Operation::AttachmentRead(
                pb::FamilyAttachmentRead { document: "doc".into() },
            )),
        };
        let bytes = encode_request(&payload).expect("encode");
        assert_eq!(bytes, vec![0x0a, 0x05, 0x0a, 0x03, b'd', b'o', b'c']);
        assert_eq!(decode_request(&bytes).expect("decode"), payload);
        assert_eq!(request_family(&payload), Ok("attachment"));
    }

    #[test]
    fn malformed_unknown_and_trailing_bytes_fail_closed() {
        assert!(matches!(decode_request(&[0x0a]), Err(FamilyCodecError::Malformed { .. })));
        assert!(decode_request(&[0x78, 0x01]).is_err());
        let payload = pb::FamilyRequestPayload {
            operation: Some(pb::family_request_payload::Operation::CounterRead(
                pb::FamilyCounterRead { document: "d".into(), names: Vec::new() },
            )),
        };
        let mut bytes = encode_request(&payload).expect("encode");
        bytes.push(0);
        assert!(matches!(decode_request(&bytes), Err(FamilyCodecError::Malformed { .. })));
        assert!(matches!(
            encode_request(&pb::FamilyRequestPayload { operation: None }),
            Err(FamilyCodecError::MissingVariant { .. })
        ));
    }

    #[test]
    fn family_and_codec_validation_is_closed() {
        let payload = pb::FamilyRequestPayload {
            operation: Some(pb::family_request_payload::Operation::ArchivalRead(
                pb::FamilyArchivalRead { document: "d".into() },
            )),
        };
        assert!(validate_request_family("archival", REQUEST_CODEC, &payload).is_ok());
        assert!(matches!(
            validate_request_family("counter", REQUEST_CODEC, &payload),
            Err(FamilyCodecError::FamilyMismatch { .. })
        ));
        assert!(matches!(
            validate_request_family("archival", "other", &payload),
            Err(FamilyCodecError::CodecMismatch { .. })
        ));
        assert!(matches!(
            validate_request_family(
                "archival",
                REQUEST_CODEC,
                &pb::FamilyRequestPayload {
                    operation: Some(pb::family_request_payload::Operation::ArchivalRead(
                        pb::FamilyArchivalRead { document: "d".into() },
                    )),
                }
            ),
            Ok(())
        ));
    }

    #[test]
    #[allow(
        clippy::too_many_lines,
        reason = "one table test covers every advertised request and response family"
    )]
    fn every_advertised_family_has_request_and_response_tags() {
        let requests = [
            "attachment",
            "counter",
            "time_series",
            "revisions",
            "compare_exchange",
            "identities",
            "expiration_refresh",
            "archival",
        ];
        for family in requests {
            let payload = match family {
                "attachment" => pb::FamilyRequestPayload {
                    operation: Some(pb::family_request_payload::Operation::AttachmentRead(
                        pb::FamilyAttachmentRead { document: "d".into() },
                    )),
                },
                "counter" => pb::FamilyRequestPayload {
                    operation: Some(pb::family_request_payload::Operation::CounterRead(
                        pb::FamilyCounterRead { document: "d".into(), names: Vec::new() },
                    )),
                },
                "time_series" => pb::FamilyRequestPayload {
                    operation: Some(pb::family_request_payload::Operation::TimeSeriesRead(
                        pb::FamilyTimeSeriesRead {
                            document: "d".into(),
                            name: "s".into(),
                            ..Default::default()
                        },
                    )),
                },
                "revisions" => pb::FamilyRequestPayload {
                    operation: Some(pb::family_request_payload::Operation::RevisionRead(
                        pb::FamilyRevisionRead { document: "d".into(), offset: 0, limit: 1 },
                    )),
                },
                "compare_exchange" => pb::FamilyRequestPayload {
                    operation: Some(pb::family_request_payload::Operation::CompareExchangeRead(
                        pb::FamilyCompareExchangeRead { key: "k".into() },
                    )),
                },
                "identities" => pb::FamilyRequestPayload {
                    operation: Some(pb::family_request_payload::Operation::IdentityRead(
                        pb::FamilyIdentityRead { names: Vec::new() },
                    )),
                },
                "expiration_refresh" => pb::FamilyRequestPayload {
                    operation: Some(pb::family_request_payload::Operation::LifecycleRead(
                        pb::FamilyLifecycleRead { document: "d".into() },
                    )),
                },
                "archival" => pb::FamilyRequestPayload {
                    operation: Some(pb::family_request_payload::Operation::ArchivalRead(
                        pb::FamilyArchivalRead { document: "d".into() },
                    )),
                },
                _ => unreachable!(),
            };
            assert_eq!(request_family(&payload), Ok(family));
        }

        let responses = [
            (
                "attachment",
                pb::family_response_payload::Result::Attachment(
                    pb::FamilyAttachmentResponse::default(),
                ),
            ),
            (
                "counter",
                pb::family_response_payload::Result::Counter(pb::FamilyCounterResponse::default()),
            ),
            (
                "time_series",
                pb::family_response_payload::Result::TimeSeries(
                    pb::FamilyTimeSeriesResponse::default(),
                ),
            ),
            (
                "revisions",
                pb::family_response_payload::Result::Revisions(
                    pb::FamilyRevisionResponse::default(),
                ),
            ),
            (
                "compare_exchange",
                pb::family_response_payload::Result::CompareExchange(
                    pb::FamilyCompareExchangeResponse { key: "k".into(), entry: None },
                ),
            ),
            (
                "identities",
                pb::family_response_payload::Result::Identities(
                    pb::FamilyIdentityResponse::default(),
                ),
            ),
            (
                "expiration_refresh",
                pb::family_response_payload::Result::ExpirationRefresh(
                    pb::FamilyLifecycleResponse::default(),
                ),
            ),
            (
                "archival",
                pb::family_response_payload::Result::Archival(pb::FamilyArchivalResponse::default()),
            ),
        ];
        for (family, result) in responses {
            assert_eq!(
                response_family(&pb::FamilyResponsePayload { result: Some(result) }),
                Ok(Some(family))
            );
        }
    }

    #[test]
    fn semantic_and_size_bounds_fail_closed() {
        let invalid = pb::FamilyRequestPayload {
            operation: Some(pb::family_request_payload::Operation::TimeSeriesRead(
                pb::FamilyTimeSeriesRead {
                    document: "d".into(),
                    name: "series".into(),
                    range_start: Some(2),
                    range_end: Some(1),
                    ..Default::default()
                },
            )),
        };
        assert!(matches!(encode_request(&invalid), Err(FamilyCodecError::InvalidField { .. })));
        assert!(matches!(
            decode_request(&vec![0u8; MAX_PAYLOAD_BYTES + 1]),
            Err(FamilyCodecError::PayloadTooLarge { .. })
        ));
    }
}
