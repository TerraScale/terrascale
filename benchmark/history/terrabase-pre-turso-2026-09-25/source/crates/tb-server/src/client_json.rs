//! Strict local-v1 JSON encoding for the bounded document caller.
//!
//! This module owns the JSON wire grammar only. It does not authenticate a
//! caller, choose a shard, or authorize a collection. Request values are
//! converted once into canonical format payloads; the synchronous document
//! service remains responsible for schema and causal-history validation.

use std::fmt::{self, Display};
use std::io::{self, Write};
use std::marker::PhantomData;

use base64::Engine;
use base64::engine::general_purpose::URL_SAFE_NO_PAD;
use serde::de::{self, MapAccess, SeqAccess, Visitor};
use serde::ser::{SerializeMap, SerializeSeq};
use serde::{Deserialize, Deserializer, Serialize, Serializer};
use tb_core::{CollectionId, DatabaseId, DocumentId, OriginId, ReplicaId, TransactionId};
use tb_document::{DocumentPoint, DocumentRead, DocumentWrite, DocumentWriteValue, OwnedSibling};
use tb_format::{
    DocumentBinding, DocumentEventId, DocumentFieldInput, DocumentKind, FieldType, FieldValueView,
    FormatError, decode_document, encode_document_into,
};
use tb_replication::{CausalContext, ReplicationError};

use crate::LocalDurableCommit;

const MAX_JSON_REQUEST_BYTES: usize = 4 * 1024 * 1024;
const MAX_JSON_RESPONSE_BYTES: usize = 4 * 1024 * 1024;
const MAX_WRITES: usize = 1_024;
const MAX_FIELDS: usize = 1_024;
const MAX_CONTEXT_ENTRIES: usize = 1_024;
const MAX_DOCUMENT_NODES: usize = 4_096;
const MAX_DOCUMENT_DEPTH: usize = 16;

/// Failure at the typed JSON boundary. The HTTP adapter maps all validation
/// causes to one public `400 invalid_request`, and response-capacity errors to
/// `503 capacity`.
#[derive(Debug, thiserror::Error)]
pub(crate) enum ClientWireError {
    /// The request is malformed or noncanonical. Keep the cause for internal
    /// diagnostics and focused tests; it is never serialized to the caller.
    #[error("client request validation failed")]
    Validation(#[source] ValidationCause),
    /// The bounded request or response cannot be admitted in this profile.
    #[error("client JSON capacity exceeded")]
    Capacity,
    /// A stored live sibling failed its format or binding checks.
    #[error("stored document failed validation")]
    InvalidStoredData(#[source] FormatError),
    /// Serialization failed for an internal value before any response escaped.
    #[error("client JSON serialization failed")]
    Serialization(#[source] serde_json::Error),
}

/// Typed causes retained behind the single public request-validation code.
#[derive(Debug, thiserror::Error)]
pub(crate) enum ValidationCause {
    #[error("malformed JSON: {0}")]
    Json(#[from] serde_json::Error),
    #[error("noncanonical {0}")]
    NonCanonical(&'static str),
    #[error("invalid document value: {0}")]
    Format(#[from] FormatError),
    #[error("invalid causal context: {0}")]
    CausalContext(#[from] ReplicationError),
}

/// Fully parsed request with all buffers and causal contexts owned by this
/// value. Temporary `DocumentWrite` descriptors exist only during the
/// synchronous callback passed to [`ValidatedCommit::with_writes`].
pub(crate) struct ValidatedCommit {
    transaction_id: TransactionId,
    collections: Vec<CollectionId>,
    writes: Vec<OwnedWrite>,
}

struct OwnedWrite {
    binding: DocumentBinding,
    observed: CausalContext,
    value: OwnedWriteValue,
}

enum OwnedWriteValue {
    Live(Vec<OwnedField>),
    Tombstone,
}

struct OwnedField {
    field_id: u32,
    value_type: FieldType,
    value: Vec<u8>,
}

impl ValidatedCommit {
    /// Stable caller-supplied identity used for exact retry reconciliation.
    #[must_use]
    pub(crate) const fn transaction_id(&self) -> TransactionId {
        self.transaction_id
    }

    /// Sorted, unique collections touched by the request for pre-worker grant
    /// checks.
    #[must_use]
    pub(crate) fn collections(&self) -> impl ExactSizeIterator<Item = CollectionId> + '_ {
        self.collections.iter().copied()
    }

    /// Build borrowed producer descriptors, invoke the synchronous service,
    /// then drop every temporary descriptor before returning.
    ///
    /// # Errors
    /// Returns capacity failure if descriptor allocation cannot be reserved.
    pub(crate) fn with_writes<R>(
        &self,
        apply: impl for<'write> FnOnce(&'write [DocumentWrite<'write>]) -> R,
    ) -> Result<R, ClientWireError> {
        let mut field_inputs = Vec::new();
        field_inputs
            .try_reserve_exact(self.writes.len())
            .map_err(|_| ClientWireError::Capacity)?;
        for write in &self.writes {
            let mut fields = Vec::new();
            if let OwnedWriteValue::Live(owned_fields) = &write.value {
                fields
                    .try_reserve_exact(owned_fields.len())
                    .map_err(|_| ClientWireError::Capacity)?;
                for field in owned_fields {
                    fields.push(DocumentFieldInput {
                        field_id: field.field_id,
                        value_type: field.value_type,
                        value: &field.value,
                    });
                }
            }
            field_inputs.push(fields);
        }

        let mut writes = Vec::new();
        writes
            .try_reserve_exact(self.writes.len())
            .map_err(|_| ClientWireError::Capacity)?;
        for (write, fields) in self.writes.iter().zip(&field_inputs) {
            let value = match &write.value {
                OwnedWriteValue::Live(_) => DocumentWriteValue::Live(fields),
                OwnedWriteValue::Tombstone => DocumentWriteValue::Tombstone,
            };
            writes.push(DocumentWrite {
                binding: write.binding,
                observed: &write.observed,
                value,
            });
        }
        Ok(apply(&writes))
    }
}

/// Decode the exact v1 commit object and own all value payloads before it can
/// enter a worker queue.
///
/// # Errors
/// Returns a typed validation cause for malformed/noncanonical input or a
/// capacity error when the JSON body exceeds the 4-MiB request ceiling.
pub(crate) fn decode_commit(bytes: &[u8]) -> Result<ValidatedCommit, ClientWireError> {
    if bytes.len() > MAX_JSON_REQUEST_BYTES {
        return Err(ClientWireError::Capacity);
    }
    let request: CommitRequest =
        serde_json::from_slice(bytes).map_err(|error| ClientWireError::Validation(error.into()))?;
    if request.version != 1 {
        return Err(validation("request version"));
    }
    if request.writes.0.is_empty() {
        return Err(validation("empty writes"));
    }

    let expiry = parse_u64(
        &request.transaction_id.expires_at_unix_ms,
        "transaction expiry",
    )?;
    let nonce = parse_lower_hex::<16>(&request.transaction_id.nonce, "transaction nonce")?;
    let transaction_id = TransactionId::new(expiry, nonce);

    let mut writes = Vec::new();
    writes
        .try_reserve_exact(request.writes.0.len())
        .map_err(|_| ClientWireError::Capacity)?;
    let mut collections = Vec::new();
    collections
        .try_reserve_exact(request.writes.0.len())
        .map_err(|_| ClientWireError::Capacity)?;
    let mut previous_binding = None;
    let mut validation_scratch = Vec::new();

    for input in request.writes.0 {
        let collection = CollectionId::parse_canonical(&input.collection)
            .map_err(|_| validation("collection ULID"))?;
        let document = DocumentId::parse_canonical(&input.document)
            .map_err(|_| validation("document ULID"))?;
        let generation = parse_u64(&input.schema_generation, "schema generation")?;
        if generation == 0 {
            return Err(validation("zero schema generation"));
        }
        let schema_digest = parse_lower_hex::<32>(&input.schema_digest, "schema digest")?;
        let binding = DocumentBinding {
            collection,
            document,
            generation,
            schema_digest,
        };
        let order_key = (collection, document);
        if previous_binding.is_some_and(|previous| previous >= order_key) {
            return Err(validation("write order"));
        }
        previous_binding = Some(order_key);
        if collections.last() != Some(&collection) {
            collections.push(collection);
        }

        let mut observed_entries = Vec::new();
        observed_entries
            .try_reserve_exact(input.observed.0.len())
            .map_err(|_| ClientWireError::Capacity)?;
        let mut previous_origin = None;
        for entry in input.observed.0 {
            let replica_id = ReplicaId::parse_canonical(&entry.replica)
                .map_err(|_| validation("observed replica ULID"))?;
            let incarnation = parse_lower_hex::<16>(&entry.incarnation, "incarnation")?;
            let sequence = parse_u64(&entry.sequence, "observed sequence")?;
            if sequence == 0 {
                return Err(validation("zero observed sequence"));
            }
            let origin = OriginId {
                replica_id,
                incarnation,
            };
            let origin_key = (replica_id.to_bytes(), incarnation);
            if previous_origin.is_some_and(|previous| previous >= origin_key) {
                return Err(validation("observed context order"));
            }
            previous_origin = Some(origin_key);
            observed_entries.push((origin, sequence));
        }
        let observed = CausalContext::from_entries(observed_entries, MAX_CONTEXT_ENTRIES)
            .map_err(|error| ClientWireError::Validation(ValidationCause::CausalContext(error)))?;

        let value = match input.value {
            WriteValueInput::Tombstone {} => OwnedWriteValue::Tombstone,
            WriteValueInput::Live { fields } => {
                let mut nodes = 0;
                let fields = encode_fields(fields, 1, &mut nodes)?;
                validate_live_fields(binding, &fields, &mut validation_scratch)?;
                OwnedWriteValue::Live(fields)
            }
        };
        writes.push(OwnedWrite {
            binding,
            observed,
            value,
        });
    }

    Ok(ValidatedCommit {
        transaction_id,
        collections,
        writes,
    })
}

/// Provisional admission estimate for parser-owned and temporary descriptor
/// memory in addition to the raw request body. The input cap and bounded typed
/// sequences bound the number of structs; the factor is intended to cover
/// simultaneous decoded strings, the canonical payload/context representation,
/// vector growth, and borrowed producer descriptors. The fixed allowance is
/// intended to cover the one-document format validation scratch buffer and
/// per-request container overhead. Allocator peak evidence is still required
/// before this estimate can be treated as a qualified upper bound.
#[must_use]
pub(crate) fn decode_memory_bound(input_len: usize) -> Option<usize> {
    if input_len > MAX_JSON_REQUEST_BYTES {
        return None;
    }
    input_len
        .checked_mul(8)?
        .checked_add(tb_core::MAX_VALUE_BYTES)
}

/// Encode one complete typed point while the `DocumentRead` and its memory
/// permit remain alive. The resulting vector is returned only after the
/// bounded writer completes successfully.
///
/// # Errors
/// Returns stored-data validation failure or capacity if JSON escaping expands
/// a valid point above the 4-MiB response ceiling.
pub(crate) fn encode_point(read: &DocumentRead) -> Result<Vec<u8>, ClientWireError> {
    let siblings = point_siblings(read.point());
    let mut documents = Vec::new();
    documents
        .try_reserve_exact(siblings.len())
        .map_err(|_| ClientWireError::Capacity)?;
    for sibling in siblings {
        match sibling.kind {
            DocumentKind::Tombstone => {
                if !sibling.document_bytes.is_empty() {
                    return Err(ClientWireError::InvalidStoredData(FormatError::Invalid {
                        format: "stored document sibling",
                        reason: "tombstone has document bytes",
                    }));
                }
                documents.push(None);
            }
            DocumentKind::Live => {
                let document = decode_document(&sibling.document_bytes)
                    .map_err(ClientWireError::InvalidStoredData)?;
                let binding = document.binding();
                if binding.generation != sibling.generation
                    || binding.schema_digest != sibling.schema_digest
                {
                    return Err(ClientWireError::InvalidStoredData(FormatError::Invalid {
                        format: "stored document sibling",
                        reason: "document binding disagrees with sibling metadata",
                    }));
                }
                documents.push(Some(document));
            }
        }
    }
    encode_bounded(&PointOutput {
        read,
        documents: &documents,
    })
}

/// Project only the local-durable result fields accepted by the first-caller
/// contract. Generic storage receipts and remote frontiers are never visited.
pub(crate) fn encode_local_commit(commit: &LocalDurableCommit) -> Result<Vec<u8>, ClientWireError> {
    encode_bounded(&LocalCommitOutput(commit))
}

fn validation(reason: &'static str) -> ClientWireError {
    ClientWireError::Validation(ValidationCause::NonCanonical(reason))
}

fn parse_u64(value: &str, name: &'static str) -> Result<u64, ClientWireError> {
    let bytes = value.as_bytes();
    if bytes.is_empty()
        || (bytes.len() > 1 && bytes[0] == b'0')
        || bytes.iter().any(|byte| !byte.is_ascii_digit())
    {
        return Err(validation(name));
    }
    value.parse().map_err(|_| validation(name))
}

fn parse_i64(value: &str) -> Result<i64, ClientWireError> {
    let bytes = value.as_bytes();
    if value == "0" {
        return Ok(0);
    }
    let digits = if bytes.first() == Some(&b'-') {
        &bytes[1..]
    } else {
        bytes
    };
    if digits.is_empty() || digits[0] == b'0' || digits.iter().any(|byte| !byte.is_ascii_digit()) {
        return Err(validation("signed integer"));
    }
    value.parse().map_err(|_| validation("signed integer"))
}

fn parse_lower_hex<const N: usize>(
    value: &str,
    name: &'static str,
) -> Result<[u8; N], ClientWireError> {
    let bytes = value.as_bytes();
    if bytes.len() != N * 2 {
        return Err(validation(name));
    }
    let mut decoded = [0; N];
    for (index, pair) in bytes.as_chunks::<2>().0.iter().enumerate() {
        decoded[index] = (hex_nibble(pair[0])? << 4) | hex_nibble(pair[1])?;
    }
    Ok(decoded)
}

fn hex_nibble(byte: u8) -> Result<u8, ClientWireError> {
    match byte {
        b'0'..=b'9' => Ok(byte - b'0'),
        b'a'..=b'f' => Ok(byte - b'a' + 10),
        _ => Err(validation("lowercase hex")),
    }
}

fn decode_base64url(value: &str) -> Result<Vec<u8>, ClientWireError> {
    let decoded = URL_SAFE_NO_PAD
        .decode(value.as_bytes())
        .map_err(|_| validation("unpadded base64url"))?;
    if URL_SAFE_NO_PAD.encode(&decoded) != value {
        return Err(validation("canonical base64url"));
    }
    Ok(decoded)
}

fn encode_fields(
    input: BoundedVec<FieldInput, MAX_FIELDS>,
    depth: usize,
    nodes: &mut usize,
) -> Result<Vec<OwnedField>, ClientWireError> {
    let mut fields = Vec::new();
    fields
        .try_reserve_exact(input.0.len())
        .map_err(|_| ClientWireError::Capacity)?;
    let mut previous_id = 0;
    for field in input.0 {
        if field.id == 0 || field.id <= previous_id {
            return Err(validation("field order"));
        }
        previous_id = field.id;
        let (value_type, value) = encode_value(field.value, depth, nodes)?;
        fields.push(OwnedField {
            field_id: field.id,
            value_type,
            value,
        });
    }
    Ok(fields)
}

fn encode_value(
    input: ValueInput,
    depth: usize,
    nodes: &mut usize,
) -> Result<(FieldType, Vec<u8>), ClientWireError> {
    *nodes = nodes
        .checked_add(1)
        .ok_or_else(|| validation("document node count"))?;
    if depth > MAX_DOCUMENT_DEPTH || *nodes > MAX_DOCUMENT_NODES {
        return Err(validation("document node or nesting limit"));
    }
    match input {
        ValueInput::Null {} => Ok((FieldType::Null, Vec::new())),
        ValueInput::Bool { value } => {
            let mut bytes = Vec::new();
            bytes
                .try_reserve_exact(1)
                .map_err(|_| ClientWireError::Capacity)?;
            bytes.push(u8::from(value));
            Ok((FieldType::Bool, bytes))
        }
        ValueInput::I64 { value } => {
            let mut bytes = Vec::new();
            bytes
                .try_reserve_exact(8)
                .map_err(|_| ClientWireError::Capacity)?;
            bytes.extend_from_slice(&parse_i64(&value)?.to_le_bytes());
            Ok((FieldType::I64, bytes))
        }
        ValueInput::Utf8 { value } => Ok((FieldType::Utf8, value.into_bytes())),
        ValueInput::Bytes { value } => Ok((FieldType::Bytes, decode_base64url(&value)?)),
        ValueInput::Map { fields } => {
            let fields = encode_fields(fields, depth + 1, nodes)?;
            Ok((FieldType::Map, encode_container(&fields, true)?))
        }
        ValueInput::Array { items } => {
            let mut values = Vec::new();
            values
                .try_reserve_exact(items.0.len())
                .map_err(|_| ClientWireError::Capacity)?;
            for item in items.0 {
                let (value_type, value) = encode_value(item, depth + 1, nodes)?;
                values.push(OwnedField {
                    field_id: 0,
                    value_type,
                    value,
                });
            }
            Ok((FieldType::Array, encode_container(&values, false)?))
        }
    }
}

fn encode_container(fields: &[OwnedField], is_map: bool) -> Result<Vec<u8>, ClientWireError> {
    let mut total_len = 2_usize;
    for field in fields {
        total_len = total_len
            .checked_add(if is_map { 9 } else { 5 })
            .and_then(|length| length.checked_add(field.value.len()))
            .ok_or_else(|| validation("container length"))?;
    }
    let count = u16::try_from(fields.len()).map_err(|_| validation("container child count"))?;
    let mut bytes = Vec::new();
    bytes
        .try_reserve_exact(total_len)
        .map_err(|_| ClientWireError::Capacity)?;
    bytes.extend_from_slice(&count.to_le_bytes());
    for field in fields {
        if is_map {
            bytes.extend_from_slice(&field.field_id.to_le_bytes());
        }
        bytes.push(field.value_type as u8);
        let value_len =
            u32::try_from(field.value.len()).map_err(|_| validation("container value length"))?;
        bytes.extend_from_slice(&value_len.to_le_bytes());
        bytes.extend_from_slice(&field.value);
    }
    Ok(bytes)
}

fn validate_live_fields(
    binding: DocumentBinding,
    fields: &[OwnedField],
    scratch: &mut Vec<u8>,
) -> Result<(), ClientWireError> {
    let mut inputs = Vec::new();
    inputs
        .try_reserve_exact(fields.len())
        .map_err(|_| ClientWireError::Capacity)?;
    for field in fields {
        inputs.push(DocumentFieldInput {
            field_id: field.field_id,
            value_type: field.value_type,
            value: &field.value,
        });
    }
    encode_document_into(scratch, binding, &inputs).map_err(|error| match error {
        FormatError::Allocation => ClientWireError::Capacity,
        other => ClientWireError::Validation(ValidationCause::Format(other)),
    })
}

fn point_siblings(point: &DocumentPoint) -> &[OwnedSibling] {
    match point {
        DocumentPoint::Absent => &[],
        DocumentPoint::Deleted { sibling, .. } | DocumentPoint::Value { sibling, .. } => {
            std::slice::from_ref(sibling)
        }
        DocumentPoint::Conflict { siblings, .. } => siblings,
    }
}

fn encode_bounded<T: Serialize>(value: &T) -> Result<Vec<u8>, ClientWireError> {
    let mut writer = BoundedWriter::new()?;
    match serde_json::to_writer(&mut writer, value) {
        Ok(()) => Ok(writer.bytes),
        Err(_error) if writer.capacity_failed => Err(ClientWireError::Capacity),
        Err(error) => Err(ClientWireError::Serialization(error)),
    }
}

struct BoundedWriter {
    bytes: Vec<u8>,
    capacity_failed: bool,
}

impl BoundedWriter {
    fn new() -> Result<Self, ClientWireError> {
        let mut bytes = Vec::new();
        bytes
            .try_reserve_exact(MAX_JSON_RESPONSE_BYTES)
            .map_err(|_| ClientWireError::Capacity)?;
        Ok(Self {
            bytes,
            capacity_failed: false,
        })
    }
}

impl Write for BoundedWriter {
    fn write(&mut self, bytes: &[u8]) -> io::Result<usize> {
        let Some(next_len) = self.bytes.len().checked_add(bytes.len()) else {
            self.capacity_failed = true;
            return Err(io::Error::new(
                io::ErrorKind::WriteZero,
                "response capacity",
            ));
        };
        if next_len > MAX_JSON_RESPONSE_BYTES {
            self.capacity_failed = true;
            return Err(io::Error::new(
                io::ErrorKind::WriteZero,
                "response capacity",
            ));
        }
        self.bytes.extend_from_slice(bytes);
        Ok(bytes.len())
    }

    fn flush(&mut self) -> io::Result<()> {
        Ok(())
    }
}

struct PointOutput<'read, 'documents> {
    read: &'read DocumentRead,
    documents: &'documents [Option<tb_format::DocumentView<'read>>],
}

impl Serialize for PointOutput<'_, '_> {
    fn serialize<S>(&self, serializer: S) -> Result<S::Ok, S::Error>
    where
        S: Serializer,
    {
        let (state, context, siblings) = match self.read.point() {
            DocumentPoint::Absent => ("absent", None, &[][..]),
            DocumentPoint::Deleted { context, sibling } => {
                ("deleted", Some(context), std::slice::from_ref(sibling))
            }
            DocumentPoint::Value { context, sibling } => {
                ("value", Some(context), std::slice::from_ref(sibling))
            }
            DocumentPoint::Conflict { context, siblings } => {
                ("conflict", Some(context), siblings.as_slice())
            }
        };
        let mut map = serializer.serialize_map(Some(4))?;
        map.serialize_entry("version", &1_u8)?;
        map.serialize_entry("state", state)?;
        map.serialize_entry("context", &ContextOutput(context))?;
        map.serialize_entry(
            "siblings",
            &SiblingListOutput {
                siblings,
                documents: self.documents,
            },
        )?;
        map.end()
    }
}

struct ContextOutput<'a>(Option<&'a CausalContext>);

impl Serialize for ContextOutput<'_> {
    fn serialize<S>(&self, serializer: S) -> Result<S::Ok, S::Error>
    where
        S: Serializer,
    {
        let Some(context) = self.0 else {
            return serializer.serialize_seq(Some(0))?.end();
        };
        let mut sequence = serializer.serialize_seq(Some(context.len()))?;
        for (origin, sequence_number) in context.entries() {
            sequence.serialize_element(&ObservedOutput {
                origin,
                sequence: sequence_number,
            })?;
        }
        sequence.end()
    }
}

struct ObservedOutput {
    origin: OriginId,
    sequence: u64,
}

impl Serialize for ObservedOutput {
    fn serialize<S>(&self, serializer: S) -> Result<S::Ok, S::Error>
    where
        S: Serializer,
    {
        let mut map = serializer.serialize_map(Some(3))?;
        map.serialize_entry("replica", &UlidOutput::Replica(self.origin.replica_id))?;
        map.serialize_entry("incarnation", &HexOutput(&self.origin.incarnation))?;
        map.serialize_entry("sequence", &DecimalOutput(self.sequence))?;
        map.end()
    }
}

struct SiblingListOutput<'read, 'documents> {
    siblings: &'read [OwnedSibling],
    documents: &'documents [Option<tb_format::DocumentView<'read>>],
}

impl Serialize for SiblingListOutput<'_, '_> {
    fn serialize<S>(&self, serializer: S) -> Result<S::Ok, S::Error>
    where
        S: Serializer,
    {
        if self.siblings.len() != self.documents.len() {
            return Err(serde::ser::Error::custom("sibling validation mismatch"));
        }
        let mut sequence = serializer.serialize_seq(Some(self.siblings.len()))?;
        for (sibling, document) in self.siblings.iter().zip(self.documents) {
            sequence.serialize_element(&SiblingOutput {
                sibling,
                document: *document,
            })?;
        }
        sequence.end()
    }
}

struct SiblingOutput<'a> {
    sibling: &'a OwnedSibling,
    document: Option<tb_format::DocumentView<'a>>,
}

impl Serialize for SiblingOutput<'_> {
    fn serialize<S>(&self, serializer: S) -> Result<S::Ok, S::Error>
    where
        S: Serializer,
    {
        let live = self.sibling.kind == DocumentKind::Live;
        if live != self.document.is_some() {
            return Err(serde::ser::Error::custom("sibling validation mismatch"));
        }
        let mut map = serializer.serialize_map(Some(if live { 5 } else { 4 }))?;
        map.serialize_entry("event", &EventOutput(self.sibling.event))?;
        map.serialize_entry("schema_generation", &DecimalOutput(self.sibling.generation))?;
        map.serialize_entry("schema_digest", &HexOutput(&self.sibling.schema_digest))?;
        map.serialize_entry("kind", if live { "live" } else { "tombstone" })?;
        if let Some(document) = self.document {
            map.serialize_entry("fields", &TopFieldsOutput(document))?;
        }
        map.end()
    }
}

struct TopFieldsOutput<'a>(tb_format::DocumentView<'a>);

impl Serialize for TopFieldsOutput<'_> {
    fn serialize<S>(&self, serializer: S) -> Result<S::Ok, S::Error>
    where
        S: Serializer,
    {
        let mut sequence = serializer.serialize_seq(Some(self.0.field_count()))?;
        for (field_id, value) in self.0.fields() {
            sequence.serialize_element(&FieldOutput { field_id, value })?;
        }
        sequence.end()
    }
}

struct FieldOutput<'a> {
    field_id: u32,
    value: FieldValueView<'a>,
}

impl Serialize for FieldOutput<'_> {
    fn serialize<S>(&self, serializer: S) -> Result<S::Ok, S::Error>
    where
        S: Serializer,
    {
        let mut map = serializer.serialize_map(Some(2))?;
        map.serialize_entry("id", &self.field_id)?;
        map.serialize_entry("value", &ValueOutput(self.value))?;
        map.end()
    }
}

struct ValueOutput<'a>(FieldValueView<'a>);

impl Serialize for ValueOutput<'_> {
    fn serialize<S>(&self, serializer: S) -> Result<S::Ok, S::Error>
    where
        S: Serializer,
    {
        let (tag, map_len) = match self.0.value_type() {
            FieldType::Null => ("null", 1),
            FieldType::Bool => ("bool", 2),
            FieldType::I64 => ("i64", 2),
            FieldType::Utf8 => ("utf8", 2),
            FieldType::Bytes => ("bytes", 2),
            FieldType::Map => ("map", 2),
            FieldType::Array => ("array", 2),
        };
        let mut map = serializer.serialize_map(Some(map_len))?;
        map.serialize_entry("type", tag)?;
        match self.0.value_type() {
            FieldType::Null => {}
            FieldType::Bool => map.serialize_entry("value", &(self.0.encoded_bytes() == [1]))?,
            FieldType::I64 => {
                let value = self.0.as_i64().expect("validated signed integer");
                map.serialize_entry("value", &value.to_string())?;
            }
            FieldType::Utf8 => {
                map.serialize_entry("value", self.0.as_str().expect("validated UTF-8"))?;
            }
            FieldType::Bytes => {
                map.serialize_entry("value", &Base64Output(self.0.encoded_bytes()))?;
            }
            FieldType::Map => map.serialize_entry("fields", &MapFieldsOutput(self.0))?,
            FieldType::Array => map.serialize_entry("items", &ArrayItemsOutput(self.0))?,
        }
        map.end()
    }
}

struct MapFieldsOutput<'a>(FieldValueView<'a>);

impl Serialize for MapFieldsOutput<'_> {
    fn serialize<S>(&self, serializer: S) -> Result<S::Ok, S::Error>
    where
        S: Serializer,
    {
        let fields = self
            .0
            .map_fields()
            .expect("validated map value has map fields");
        let mut sequence = serializer.serialize_seq(Some(fields.len()))?;
        for (field_id, value) in fields {
            sequence.serialize_element(&FieldOutput { field_id, value })?;
        }
        sequence.end()
    }
}

struct ArrayItemsOutput<'a>(FieldValueView<'a>);

impl Serialize for ArrayItemsOutput<'_> {
    fn serialize<S>(&self, serializer: S) -> Result<S::Ok, S::Error>
    where
        S: Serializer,
    {
        let items = self
            .0
            .array_items()
            .expect("validated array value has array items");
        let mut sequence = serializer.serialize_seq(Some(items.len()))?;
        for value in items {
            sequence.serialize_element(&ValueOutput(value))?;
        }
        sequence.end()
    }
}

struct EventOutput(DocumentEventId);

impl Serialize for EventOutput {
    fn serialize<S>(&self, serializer: S) -> Result<S::Ok, S::Error>
    where
        S: Serializer,
    {
        let mut map = serializer.serialize_map(Some(5))?;
        map.serialize_entry("database", &UlidOutput::Database(self.0.database))?;
        map.serialize_entry("shard", &DecimalOutput(self.0.shard.get()))?;
        map.serialize_entry("replica", &UlidOutput::Replica(self.0.origin.replica_id))?;
        map.serialize_entry("incarnation", &HexOutput(&self.0.origin.incarnation))?;
        map.serialize_entry("sequence", &DecimalOutput(self.0.sequence))?;
        map.end()
    }
}

struct LocalCommitOutput<'a>(&'a LocalDurableCommit);

impl Serialize for LocalCommitOutput<'_> {
    fn serialize<S>(&self, serializer: S) -> Result<S::Ok, S::Error>
    where
        S: Serializer,
    {
        let transaction_id = self.0.transaction_id();
        let mut map = serializer.serialize_map(Some(5))?;
        map.serialize_entry("version", &1_u8)?;
        map.serialize_entry("durability", "local_durable")?;
        map.serialize_entry("transaction_id", &TransactionOutput(transaction_id))?;
        map.serialize_entry("event", &EventOutput(self.0.event()))?;
        map.serialize_entry(
            "commit_sequence",
            &DecimalOutput(self.0.local_durable_frontier().get()),
        )?;
        map.end()
    }
}

struct TransactionOutput(TransactionId);

impl Serialize for TransactionOutput {
    fn serialize<S>(&self, serializer: S) -> Result<S::Ok, S::Error>
    where
        S: Serializer,
    {
        let mut map = serializer.serialize_map(Some(2))?;
        map.serialize_entry(
            "expires_at_unix_ms",
            &DecimalOutput(self.0.expires_at_unix_ms()),
        )?;
        map.serialize_entry("nonce", &HexOutput(&self.0.nonce()))?;
        map.end()
    }
}

struct DecimalOutput(u64);

impl Serialize for DecimalOutput {
    fn serialize<S>(&self, serializer: S) -> Result<S::Ok, S::Error>
    where
        S: Serializer,
    {
        serializer.serialize_str(&self.0.to_string())
    }
}

struct Base64Output<'a>(&'a [u8]);

impl Serialize for Base64Output<'_> {
    fn serialize<S>(&self, serializer: S) -> Result<S::Ok, S::Error>
    where
        S: Serializer,
    {
        serializer.collect_str(&Base64Display(self.0))
    }
}

/// Chunked unpadded-base64 display so JSON serialization never holds a full
/// expanded copy of a stored byte field beside the bounded response buffer.
struct Base64Display<'a>(&'a [u8]);

impl Display for Base64Display<'_> {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        const INPUT_CHUNK_BYTES: usize = 768;
        const OUTPUT_CHUNK_BYTES: usize = 1_024;

        let mut remaining = self.0;
        let mut encoded = [0_u8; OUTPUT_CHUNK_BYTES];
        while !remaining.is_empty() {
            let input_len = remaining.len().min(INPUT_CHUNK_BYTES);
            let chunk = &remaining[..input_len];
            let encoded_len = URL_SAFE_NO_PAD
                .encode_slice(chunk, &mut encoded)
                .map_err(|_| fmt::Error)?;
            let encoded_text =
                std::str::from_utf8(&encoded[..encoded_len]).map_err(|_| fmt::Error)?;
            formatter.write_str(encoded_text)?;
            remaining = &remaining[input_len..];
        }
        Ok(())
    }
}

struct HexOutput<'a>(&'a [u8]);

impl Serialize for HexOutput<'_> {
    fn serialize<S>(&self, serializer: S) -> Result<S::Ok, S::Error>
    where
        S: Serializer,
    {
        let mut encoded = String::new();
        encoded.reserve(self.0.len() * 2);
        for byte in self.0 {
            use std::fmt::Write as _;
            write!(encoded, "{byte:02x}").expect("writing to a String cannot fail");
        }
        serializer.serialize_str(&encoded)
    }
}

enum UlidOutput {
    Database(DatabaseId),
    Replica(ReplicaId),
}

impl Serialize for UlidOutput {
    fn serialize<S>(&self, serializer: S) -> Result<S::Ok, S::Error>
    where
        S: Serializer,
    {
        match self {
            Self::Database(value) => serializer.collect_str(value),
            Self::Replica(value) => serializer.collect_str(value),
        }
    }
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct CommitRequest {
    version: u64,
    transaction_id: TransactionIdInput,
    writes: BoundedVec<WriteInput, MAX_WRITES>,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct TransactionIdInput {
    expires_at_unix_ms: String,
    nonce: String,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct WriteInput {
    collection: String,
    document: String,
    schema_generation: String,
    schema_digest: String,
    observed: BoundedVec<ObservedInput, MAX_CONTEXT_ENTRIES>,
    value: WriteValueInput,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct ObservedInput {
    replica: String,
    incarnation: String,
    sequence: String,
}

enum WriteValueInput {
    Tombstone {},
    Live {
        fields: BoundedVec<FieldInput, MAX_FIELDS>,
    },
}

impl<'de> Deserialize<'de> for WriteValueInput {
    fn deserialize<D>(deserializer: D) -> Result<Self, D::Error>
    where
        D: Deserializer<'de>,
    {
        struct WriteValueVisitor;

        impl<'de> Visitor<'de> for WriteValueVisitor {
            type Value = WriteValueInput;

            fn expecting(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
                formatter.write_str("a tagged live or tombstone value object")
            }

            fn visit_map<A>(self, mut map: A) -> Result<Self::Value, A::Error>
            where
                A: MapAccess<'de>,
            {
                let mut kind = None;
                let mut fields = None;
                while let Some(key) = map.next_key::<String>()? {
                    match key.as_str() {
                        "kind" => {
                            if kind.is_some() {
                                return Err(de::Error::duplicate_field("kind"));
                            }
                            kind = Some(map.next_value::<String>()?);
                        }
                        "fields" => {
                            if fields.is_some() {
                                return Err(de::Error::duplicate_field("fields"));
                            }
                            if kind.as_deref() == Some("tombstone") {
                                return Err(de::Error::unknown_field("fields", &["kind"]));
                            }
                            fields = Some(map.next_value::<BoundedVec<FieldInput, MAX_FIELDS>>()?);
                        }
                        _ => return Err(de::Error::unknown_field(&key, &["kind", "fields"])),
                    }
                }

                let kind = kind.ok_or_else(|| de::Error::missing_field("kind"))?;
                match kind.as_str() {
                    "tombstone" => {
                        if fields.is_some() {
                            Err(de::Error::unknown_field("fields", &["kind"]))
                        } else {
                            Ok(WriteValueInput::Tombstone {})
                        }
                    }
                    "live" => fields
                        .map(|fields| WriteValueInput::Live { fields })
                        .ok_or_else(|| de::Error::missing_field("fields")),
                    _ => Err(de::Error::unknown_variant(&kind, &["tombstone", "live"])),
                }
            }
        }

        deserializer.deserialize_map(WriteValueVisitor)
    }
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct FieldInput {
    id: u32,
    value: ValueInput,
}

enum ValueInput {
    Null {},
    Bool {
        value: bool,
    },
    I64 {
        value: String,
    },
    Utf8 {
        value: String,
    },
    Bytes {
        value: String,
    },
    Map {
        fields: BoundedVec<FieldInput, MAX_FIELDS>,
    },
    Array {
        items: BoundedVec<ValueInput, MAX_FIELDS>,
    },
}

enum ScalarInput {
    Bool(bool),
    String(String),
}

impl<'de> Deserialize<'de> for ScalarInput {
    fn deserialize<D>(deserializer: D) -> Result<Self, D::Error>
    where
        D: Deserializer<'de>,
    {
        struct ScalarVisitor;

        impl<'de> Visitor<'de> for ScalarVisitor {
            type Value = ScalarInput;

            fn expecting(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
                formatter.write_str("a boolean or string scalar")
            }

            fn visit_bool<E>(self, value: bool) -> Result<Self::Value, E>
            where
                E: de::Error,
            {
                Ok(ScalarInput::Bool(value))
            }

            fn visit_str<E>(self, value: &str) -> Result<Self::Value, E>
            where
                E: de::Error,
            {
                Ok(ScalarInput::String(value.to_owned()))
            }

            fn visit_borrowed_str<E>(self, value: &'de str) -> Result<Self::Value, E>
            where
                E: de::Error,
            {
                self.visit_str(value)
            }

            fn visit_string<E>(self, value: String) -> Result<Self::Value, E>
            where
                E: de::Error,
            {
                Ok(ScalarInput::String(value))
            }
        }

        deserializer.deserialize_any(ScalarVisitor)
    }
}

struct ValueVisitor;

impl<'de> Visitor<'de> for ValueVisitor {
    type Value = ValueInput;

    fn expecting(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter.write_str("a tagged typed document value object")
    }

    fn visit_map<A>(self, mut map: A) -> Result<Self::Value, A::Error>
    where
        A: MapAccess<'de>,
    {
        let mut value_type = None;
        let mut value = None;
        let mut fields = None;
        let mut items = None;
        while let Some(key) = map.next_key::<String>()? {
            match key.as_str() {
                "type" => {
                    if value_type.is_some() {
                        return Err(de::Error::duplicate_field("type"));
                    }
                    value_type = Some(map.next_value::<String>()?);
                }
                "value" => {
                    if value.is_some() {
                        return Err(de::Error::duplicate_field("value"));
                    }
                    if let Some(value_type) = value_type.as_deref()
                        && !matches!(value_type, "bool" | "i64" | "utf8" | "bytes")
                    {
                        return Err(de::Error::unknown_field(
                            "value",
                            value_input_expected_fields(Some(value_type)),
                        ));
                    }
                    value = Some(map.next_value::<ScalarInput>()?);
                }
                "fields" => {
                    if fields.is_some() {
                        return Err(de::Error::duplicate_field("fields"));
                    }
                    if let Some(value_type) = value_type.as_deref()
                        && value_type != "map"
                    {
                        return Err(de::Error::unknown_field(
                            "fields",
                            value_input_expected_fields(Some(value_type)),
                        ));
                    }
                    fields = Some(map.next_value::<BoundedVec<FieldInput, MAX_FIELDS>>()?);
                }
                "items" => {
                    if items.is_some() {
                        return Err(de::Error::duplicate_field("items"));
                    }
                    if let Some(value_type) = value_type.as_deref()
                        && value_type != "array"
                    {
                        return Err(de::Error::unknown_field(
                            "items",
                            value_input_expected_fields(Some(value_type)),
                        ));
                    }
                    items = Some(map.next_value::<BoundedVec<ValueInput, MAX_FIELDS>>()?);
                }
                _ => {
                    return Err(de::Error::unknown_field(
                        &key,
                        value_input_expected_fields(value_type.as_deref()),
                    ));
                }
            }
        }

        let value_type = value_type.ok_or_else(|| de::Error::missing_field("type"))?;
        finish_value_input(&value_type, value, fields, items)
    }
}

fn finish_value_input<E: de::Error>(
    value_type: &str,
    value: Option<ScalarInput>,
    fields: Option<BoundedVec<FieldInput, MAX_FIELDS>>,
    items: Option<BoundedVec<ValueInput, MAX_FIELDS>>,
) -> Result<ValueInput, E> {
    match value_type {
        "null" => {
            if value.is_some() || fields.is_some() || items.is_some() {
                Err(de::Error::custom("null value cannot contain a payload"))
            } else {
                Ok(ValueInput::Null {})
            }
        }
        "bool" => {
            if fields.is_some() || items.is_some() {
                return Err(de::Error::custom("boolean value has unexpected fields"));
            }
            match value.ok_or_else(|| de::Error::missing_field("value"))? {
                ScalarInput::Bool(value) => Ok(ValueInput::Bool { value }),
                ScalarInput::String(_) => {
                    Err(de::Error::custom("boolean value must be a JSON boolean"))
                }
            }
        }
        "i64" | "utf8" | "bytes" => {
            if fields.is_some() || items.is_some() {
                return Err(de::Error::custom("scalar value has unexpected fields"));
            }
            let ScalarInput::String(value) =
                value.ok_or_else(|| de::Error::missing_field("value"))?
            else {
                return Err(de::Error::custom("scalar value must be a JSON string"));
            };
            match value_type {
                "i64" => Ok(ValueInput::I64 { value }),
                "utf8" => Ok(ValueInput::Utf8 { value }),
                "bytes" => Ok(ValueInput::Bytes { value }),
                _ => Err(de::Error::custom("unknown scalar value type")),
            }
        }
        "map" => {
            if value.is_some() || items.is_some() {
                return Err(de::Error::custom("map value has unexpected fields"));
            }
            fields
                .map(|fields| ValueInput::Map { fields })
                .ok_or_else(|| de::Error::missing_field("fields"))
        }
        "array" => {
            if value.is_some() || fields.is_some() {
                return Err(de::Error::custom("array value has unexpected fields"));
            }
            items
                .map(|items| ValueInput::Array { items })
                .ok_or_else(|| de::Error::missing_field("items"))
        }
        _ => Err(de::Error::unknown_variant(
            value_type,
            &["null", "bool", "i64", "utf8", "bytes", "map", "array"],
        )),
    }
}

impl<'de> Deserialize<'de> for ValueInput {
    fn deserialize<D>(deserializer: D) -> Result<Self, D::Error>
    where
        D: Deserializer<'de>,
    {
        deserializer.deserialize_map(ValueVisitor)
    }
}

fn value_input_expected_fields(value_type: Option<&str>) -> &'static [&'static str] {
    match value_type {
        Some("null") => &["type"],
        Some("bool" | "i64" | "utf8" | "bytes") => &["type", "value"],
        Some("map") => &["type", "fields"],
        Some("array") => &["type", "items"],
        _ => &["type", "value", "fields", "items"],
    }
}

struct BoundedVec<T, const LIMIT: usize>(Vec<T>);

impl<'de, T, const LIMIT: usize> Deserialize<'de> for BoundedVec<T, LIMIT>
where
    T: Deserialize<'de>,
{
    fn deserialize<D>(deserializer: D) -> Result<Self, D::Error>
    where
        D: Deserializer<'de>,
    {
        struct BoundedVecVisitor<T, const LIMIT: usize>(PhantomData<fn() -> T>);

        impl<'de, T, const LIMIT: usize> Visitor<'de> for BoundedVecVisitor<T, LIMIT>
        where
            T: Deserialize<'de>,
        {
            type Value = BoundedVec<T, LIMIT>;

            fn expecting(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
                write!(formatter, "an array with at most {LIMIT} entries")
            }

            fn visit_seq<A>(self, mut sequence: A) -> Result<Self::Value, A::Error>
            where
                A: SeqAccess<'de>,
            {
                let mut values = Vec::new();
                while values.len() < LIMIT {
                    let Some(value) = sequence.next_element()? else {
                        return Ok(BoundedVec(values));
                    };
                    values.try_reserve(1).map_err(de::Error::custom)?;
                    values.push(value);
                }

                if sequence.next_element::<de::IgnoredAny>()?.is_some() {
                    return Err(de::Error::invalid_length(LIMIT + 1, &self));
                }
                Ok(BoundedVec(values))
            }
        }

        deserializer.deserialize_seq(BoundedVecVisitor::<T, LIMIT>(PhantomData))
    }
}

#[cfg(test)]
mod tests {
    use std::fs;
    use std::path::{Path, PathBuf};
    use std::sync::Arc;
    use std::sync::atomic::{AtomicU64, AtomicUsize, Ordering};
    use std::time::{SystemTime, UNIX_EPOCH};

    use super::*;
    use tb_core::{BudgetConfig, MembershipEpoch, MemoryBudget, WorkerId};
    use tb_document::{DocumentStore, DocumentStoreConfig, LocalSchemaBinding};
    use tb_format::{SchemaField, encode_schema_into};
    use tb_replication::{ConfigurationId, ReplicationLimits};
    use tb_storage::{LocalDocumentMode, StorageConfig};

    const COLLECTION: &str = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
    const DOCUMENT: &str = "01ARZ3NDEKTSV4RRFFQ69G5FAW";
    const NONCE: &str = "00000000000000000000000000000000";
    const DIGEST: &str = "0000000000000000000000000000000000000000000000000000000000000000";

    static NEXT_TEMP_DIRECTORY: AtomicU64 = AtomicU64::new(0);

    struct TempShard(PathBuf);

    impl TempShard {
        fn new() -> Self {
            let nanos = SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .expect("system clock is after the Unix epoch")
                .as_nanos();
            let ordinal = NEXT_TEMP_DIRECTORY.fetch_add(1, Ordering::Relaxed);
            Self(std::env::temp_dir().join(format!(
                "terrabase-client-json-{}-{nanos}-{ordinal}",
                std::process::id(),
            )))
        }

        fn path(&self) -> &Path {
            &self.0
        }
    }

    impl Drop for TempShard {
        fn drop(&mut self) {
            if self.0.exists() {
                fs::remove_dir_all(&self.0).expect("remove temporary client JSON shard");
            }
        }
    }

    struct ServiceFixture {
        service: crate::LocalV1DocumentService,
        binding: DocumentBinding,
        _directory: TempShard,
    }

    impl ServiceFixture {
        fn new() -> Self {
            let directory = TempShard::new();
            let tenant = tb_core::TenantId::from_bytes([40; 16]);
            let database = DatabaseId::from_bytes([41; 16]);
            let collection = CollectionId::from_bytes([42; 16]);
            let document = DocumentId::from_bytes([43; 16]);
            let shard = tb_core::ShardId::new(12);
            let origin = OriginId {
                replica_id: ReplicaId::from_bytes([44; 16]),
                incarnation: [45; 16],
            };
            let config = DocumentStoreConfig {
                storage: StorageConfig::local(directory.path(), tenant, WorkerId(9))
                    .with_clock(Arc::new(|| 1_000)),
                database,
                shard,
                origin,
                epoch: MembershipEpoch::new(1),
                configuration: ConfigurationId::from_bytes([46; 32])
                    .expect("nonzero fixture configuration"),
                replication: ReplicationLimits {
                    max_origins: 16,
                    max_changes: 32,
                    max_siblings: 16,
                    max_value_bytes: tb_core::MAX_VALUE_BYTES,
                    max_payload_bytes: tb_core::MAX_WAL_GROUP_BYTES,
                },
            };
            let memory = MemoryBudget::new(
                BudgetConfig::new(64 * 1024 * 1024, 0).expect("valid fixture memory budget"),
            );
            let schema_fields = [
                SchemaField {
                    field_id: 1,
                    parent_field_id: 0,
                    value_type: FieldType::Utf8,
                    element_type: None,
                    flags: 1,
                    name: "text",
                },
                SchemaField {
                    field_id: 2,
                    parent_field_id: 0,
                    value_type: FieldType::Bytes,
                    element_type: None,
                    flags: 0,
                    name: "binary",
                },
            ];
            let mut schema = Vec::new();
            encode_schema_into(&mut schema, &schema_fields).expect("encode fixture schema");
            let digest = tb_format::encode_catalog_into(&mut Vec::new(), collection, 1, 0, &schema)
                .expect("calculate fixture schema digest");
            let mode = LocalDocumentMode::new(
                tenant,
                database,
                shard,
                origin,
                config.epoch,
                config.configuration.to_bytes(),
            );
            let mut store = DocumentStore::bootstrap_local_v1(config.clone(), memory.clone(), mode)
                .expect("bootstrap fixture local-v1 document store");
            assert!(
                store
                    .provision_schema(
                        TransactionId::new(50_000, [1; 16]),
                        collection,
                        1,
                        0,
                        &schema,
                    )
                    .expect("provision fixture schema")
                    .is_some()
            );
            drop(store);

            let scope = crate::LocalV1Scope::configured(tenant, database, shard, [collection])
                .expect("valid fixture scope");
            let expected = [LocalSchemaBinding {
                collection,
                generation: 1,
                schema_digest: digest,
            }];
            let service =
                crate::LocalV1DocumentService::open(config, memory, scope, mode, &expected)
                    .expect("open fixture local-v1 service");
            Self {
                service,
                binding: DocumentBinding {
                    collection,
                    document,
                    generation: 1,
                    schema_digest: digest,
                },
                _directory: directory,
            }
        }

        fn write_live(
            &mut self,
            document: DocumentId,
            observed: &CausalContext,
            fields: &[DocumentFieldInput<'_>],
            nonce: u8,
        ) -> crate::LocalDurableCommit {
            let write = DocumentWrite {
                binding: DocumentBinding {
                    document,
                    ..self.binding
                },
                observed,
                value: DocumentWriteValue::Live(fields),
            };
            self.service
                .commit_documents(TransactionId::new(50_000, [nonce; 16]), &[write])
                .expect("commit fixture document")
        }

        fn delete(
            &mut self,
            document: DocumentId,
            observed: &CausalContext,
            nonce: u8,
        ) -> crate::LocalDurableCommit {
            let write = DocumentWrite {
                binding: DocumentBinding {
                    document,
                    ..self.binding
                },
                observed,
                value: DocumentWriteValue::Tombstone,
            };
            self.service
                .commit_documents(TransactionId::new(50_000, [nonce; 16]), &[write])
                .expect("delete fixture document")
        }
    }

    fn mixed_point_response(
        fixture: &mut ServiceFixture,
        document: DocumentId,
        utf8_bytes: usize,
        binary_bytes: usize,
        nonce: u8,
    ) -> Result<Vec<u8>, ClientWireError> {
        let utf8 = vec![0_u8; utf8_bytes];
        let binary = vec![0xa5_u8; binary_bytes];
        let fields = [
            DocumentFieldInput {
                field_id: 1,
                value_type: FieldType::Utf8,
                value: &utf8,
            },
            DocumentFieldInput {
                field_id: 2,
                value_type: FieldType::Bytes,
                value: &binary,
            },
        ];
        fixture.write_live(document, &CausalContext::new(), &fields, nonce);
        let read = fixture
            .service
            .point_read(fixture.binding.collection, document)
            .expect("read mixed response fixture");
        encode_point(&read)
    }

    fn tombstone_request() -> String {
        format!(
            r#"{{"version":1,"transaction_id":{{"expires_at_unix_ms":"10000","nonce":"{NONCE}"}},"writes":[{{"collection":"{COLLECTION}","document":"{DOCUMENT}","schema_generation":"1","schema_digest":"{DIGEST}","observed":[],"value":{{"kind":"tombstone"}}}}]}}"#
        )
    }

    #[test]
    fn decode_commit_owns_canonical_sorted_writes_and_borrowed_inputs() {
        let request = format!(
            r#"{{"version":1,"transaction_id":{{"expires_at_unix_ms":"10000","nonce":"{NONCE}"}},"writes":[{{"collection":"{COLLECTION}","document":"{DOCUMENT}","schema_generation":"1","schema_digest":"{DIGEST}","observed":[{{"replica":"{COLLECTION}","incarnation":"01010101010101010101010101010101","sequence":"3"}}],"value":{{"kind":"live","fields":[{{"id":1,"value":{{"type":"null"}}}},{{"id":2,"value":{{"type":"bytes","value":"AQ"}}}},{{"id":3,"value":{{"type":"map","fields":[{{"id":9,"value":{{"type":"i64","value":"-42"}}}}]}}}},{{"id":4,"value":{{"type":"array","items":[{{"type":"bool","value":true}},{{"type":"utf8","value":"second"}}]}}}}]}}}}]}}"#
        );
        let commit = decode_commit(request.as_bytes()).unwrap();
        assert_eq!(commit.transaction_id().expires_at_unix_ms(), 10_000);
        assert_eq!(
            commit.collections().collect::<Vec<_>>(),
            vec![CollectionId::parse_canonical(COLLECTION).unwrap()]
        );

        commit
            .with_writes(|writes| {
                assert_eq!(writes.len(), 1);
                assert_eq!(
                    writes[0].observed.get(OriginId {
                        replica_id: ReplicaId::parse_canonical(COLLECTION).unwrap(),
                        incarnation: [1; 16],
                    }),
                    3
                );
                let DocumentWriteValue::Live(fields) = writes[0].value else {
                    panic!("live request must produce live fields");
                };
                assert_eq!(fields.len(), 4);
                assert_eq!(fields[0].value_type, FieldType::Null);
                assert_eq!(fields[1].value, &[1]);
                assert_eq!(fields[2].value_type, FieldType::Map);
                assert_eq!(fields[3].value_type, FieldType::Array);
                let nested = decode_container_for_test(fields[2].value, true);
                assert_eq!(nested[0].0, Some(9));
                assert_eq!(nested[0].1, FieldType::I64);
                assert_eq!(i64::from_le_bytes(nested[0].2.try_into().unwrap()), -42);
                let items = decode_container_for_test(fields[3].value, false);
                assert_eq!(items[0].1, FieldType::Bool);
                assert_eq!(items[0].2, [1]);
                assert_eq!(items[1].1, FieldType::Utf8);
                assert_eq!(items[1].2, b"second");
            })
            .unwrap();
    }

    fn decode_container_for_test(
        bytes: &[u8],
        is_map: bool,
    ) -> Vec<(Option<u32>, FieldType, &[u8])> {
        let count = usize::from(u16::from_le_bytes(bytes[..2].try_into().unwrap()));
        let mut offset = 2;
        let mut values = Vec::new();
        for _ in 0..count {
            let field_id = if is_map {
                let id = u32::from_le_bytes(bytes[offset..offset + 4].try_into().unwrap());
                offset += 4;
                Some(id)
            } else {
                None
            };
            let value_type = FieldType::try_from(bytes[offset]).unwrap();
            offset += 1;
            let length = usize::try_from(u32::from_le_bytes(
                bytes[offset..offset + 4].try_into().unwrap(),
            ))
            .unwrap();
            offset += 4;
            let end = offset + length;
            values.push((field_id, value_type, &bytes[offset..end]));
            offset = end;
        }
        assert_eq!(offset, bytes.len());
        values
    }

    #[test]
    fn decode_commit_rejects_unknown_duplicate_and_noncanonical_members() {
        let request = tombstone_request();
        let duplicate = request.replacen("\"version\":1", "\"version\":1,\"version\":1", 1);
        let unknown = request.replacen("\"version\":1", "\"version\":1,\"extra\":0", 1);
        let duplicate_variant = request.replace(
            "\"kind\":\"tombstone\"",
            "\"kind\":\"tombstone\",\"kind\":\"tombstone\"",
        );
        for malformed in [duplicate, unknown, duplicate_variant] {
            assert!(matches!(
                decode_commit(malformed.as_bytes()),
                Err(ClientWireError::Validation(_))
            ));
        }

        let noncanonical_i64 = request.replace(
            "\"kind\":\"tombstone\"",
            "\"kind\":\"live\",\"fields\":[{\"id\":1,\"value\":{\"type\":\"i64\",\"value\":\"-0\"}}]",
        );
        let padded_base64 = request.replace(
            "\"kind\":\"tombstone\"",
            "\"kind\":\"live\",\"fields\":[{\"id\":1,\"value\":{\"type\":\"bytes\",\"value\":\"AQ==\"}}]",
        );
        let lowercase_ulid = request.replace(COLLECTION, "01arz3ndektsv4rrffq69g5fav");
        for malformed in [noncanonical_i64, padded_base64, lowercase_ulid] {
            assert!(matches!(
                decode_commit(malformed.as_bytes()),
                Err(ClientWireError::Validation(_))
            ));
        }
    }

    #[test]
    fn tagged_value_visitors_preserve_member_order_and_escaped_known_keys() {
        let base = tombstone_request();
        let reversed = base.replace(
            "\"kind\":\"tombstone\"",
            "\"fields\":[{\"id\":1,\"value\":{\"value\":\"text\",\"type\":\"utf8\"}}],\"kind\":\"live\"",
        );
        assert!(decode_commit(reversed.as_bytes()).is_ok());

        let escaped_key = base.replace(
            "\"kind\":\"tombstone\"",
            "\"kind\":\"live\",\"fields\":[{\"id\":1,\"value\":{\"ty\\u0070e\":\"null\"}}]",
        );
        assert!(decode_commit(escaped_key.as_bytes()).is_ok());

        let duplicate_tag = base.replace(
            "\"kind\":\"tombstone\"",
            "\"kind\":\"live\",\"fields\":[{\"id\":1,\"value\":{\"type\":\"null\",\"type\":\"null\"}}]",
        );
        let duplicate_payload = base.replace(
            "\"kind\":\"tombstone\"",
            "\"kind\":\"live\",\"fields\":[{\"id\":1,\"value\":{\"type\":\"bool\",\"value\":true,\"value\":false}}]",
        );
        for malformed in [duplicate_tag, duplicate_payload] {
            assert!(matches!(
                decode_commit(malformed.as_bytes()),
                Err(ClientWireError::Validation(_))
            ));
        }
    }

    #[test]
    fn tagged_value_visitors_reject_large_unknown_values_before_materializing_them() {
        let base = tombstone_request();
        let large_array = format!("{}]", "0,".repeat(1_650_000));
        let large_array = large_array.trim_end_matches(",]");
        let large_array = format!("[{large_array}]");
        assert!(large_array.len() < MAX_JSON_REQUEST_BYTES - base.len());

        let unknown_tombstone = base.replace(
            "\"kind\":\"tombstone\"",
            &format!("\"kind\":\"tombstone\",\"x\":{large_array}"),
        );
        assert!(unknown_tombstone.len() <= MAX_JSON_REQUEST_BYTES);
        assert!(matches!(
            decode_commit(unknown_tombstone.as_bytes()),
            Err(ClientWireError::Validation(_))
        ));

        let unknown_value = base.replace(
            "\"kind\":\"tombstone\"",
            &format!(
                "\"kind\":\"live\",\"fields\":[{{\"id\":1,\"value\":{{\"type\":\"null\",\"x\":{large_array}}}}}]"
            ),
        );
        assert!(unknown_value.len() <= MAX_JSON_REQUEST_BYTES);
        assert!(matches!(
            decode_commit(unknown_value.as_bytes()),
            Err(ClientWireError::Validation(_))
        ));
    }

    #[test]
    fn bounded_value_arrays_skip_the_first_oversized_item_without_typed_decode() {
        let base = tombstone_request();
        let items = format!(
            "{}{{\"type\":\"array\",\"items\":[{{\"type\":\"null\"}}]}}",
            "{\"type\":\"null\"},".repeat(MAX_FIELDS)
        );
        let oversized = base.replace(
            "\"kind\":\"tombstone\"",
            &format!(
                "\"kind\":\"live\",\"fields\":[{{\"id\":1,\"value\":{{\"type\":\"array\",\"items\":[{items}]}}}}]"
            ),
        );
        assert!(matches!(
            decode_commit(oversized.as_bytes()),
            Err(ClientWireError::Validation(_))
        ));
    }

    #[test]
    fn bounded_vec_deserializes_only_limit_entries_before_ignoring_overflow() {
        static TYPED_ITEMS_VISITED: AtomicUsize = AtomicUsize::new(0);

        struct CountedItem;

        impl<'de> Deserialize<'de> for CountedItem {
            fn deserialize<D>(deserializer: D) -> Result<Self, D::Error>
            where
                D: Deserializer<'de>,
            {
                TYPED_ITEMS_VISITED.fetch_add(1, Ordering::Relaxed);
                <()>::deserialize(deserializer)?;
                Ok(Self)
            }
        }

        let ignored_numbers = "0,".repeat(100_000);
        let ignored_numbers = ignored_numbers.trim_end_matches(',');
        let overflow = format!("[null,{{\"ignored\":[{ignored_numbers}]}}]");
        TYPED_ITEMS_VISITED.store(0, Ordering::Relaxed);
        assert!(serde_json::from_str::<BoundedVec<CountedItem, 1>>(&overflow).is_err());
        assert_eq!(TYPED_ITEMS_VISITED.load(Ordering::Relaxed), 1);
    }

    #[test]
    fn decode_commit_rejects_unsorted_writes_fields_and_contexts() {
        let base = tombstone_request();
        let unsorted_fields = base.replace(
            "\"kind\":\"tombstone\"",
            "\"kind\":\"live\",\"fields\":[{\"id\":2,\"value\":{\"type\":\"null\"}},{\"id\":1,\"value\":{\"type\":\"null\"}}]",
        );
        assert!(matches!(
            decode_commit(unsorted_fields.as_bytes()),
            Err(ClientWireError::Validation(_))
        ));

        let context_entry = format!(
            r#"{{"replica":"{COLLECTION}","incarnation":"02020202020202020202020202020202","sequence":"1"}}"#
        );
        let lower_context_entry = format!(
            r#"{{"replica":"{COLLECTION}","incarnation":"01010101010101010101010101010101","sequence":"1"}}"#
        );
        let unsorted_context = base.replace(
            "\"observed\":[]",
            &format!("\"observed\":[{context_entry},{lower_context_entry}]"),
        );
        assert!(matches!(
            decode_commit(unsorted_context.as_bytes()),
            Err(ClientWireError::Validation(_))
        ));

        let write = base.split_once("\"writes\":[").unwrap().1;
        let write = write.strip_suffix("]}").unwrap();
        let duplicate_write = format!(
            "{}\"writes\":[{write},{write}]}}",
            &base[..base.find("\"writes\":[").unwrap()]
        );
        assert!(matches!(
            decode_commit(duplicate_write.as_bytes()),
            Err(ClientWireError::Validation(_))
        ));
    }

    #[test]
    fn decode_commit_enforces_body_and_memory_bounds() {
        let request = tombstone_request();
        assert!(decode_commit(request.as_bytes()).is_ok());
        let too_large = vec![b' '; MAX_JSON_REQUEST_BYTES + 1];
        assert!(matches!(
            decode_commit(&too_large),
            Err(ClientWireError::Capacity)
        ));
        assert_eq!(decode_memory_bound(0), Some(tb_core::MAX_VALUE_BYTES));
        assert!(decode_memory_bound(MAX_JSON_REQUEST_BYTES).is_some());
        assert_eq!(decode_memory_bound(MAX_JSON_REQUEST_BYTES + 1), None);
    }

    #[test]
    fn typed_value_json_uses_borrowed_nested_format_iterators() {
        let binding = DocumentBinding {
            collection: CollectionId::from_bytes([50; 16]),
            document: DocumentId::from_bytes([51; 16]),
            generation: 1,
            schema_digest: [52; 32],
        };
        let negative = (-42_i64).to_le_bytes();
        let map = encode_container(
            &[OwnedField {
                field_id: 9,
                value_type: FieldType::Null,
                value: Vec::new(),
            }],
            true,
        )
        .unwrap();
        let array = encode_container(
            &[
                OwnedField {
                    field_id: 0,
                    value_type: FieldType::Bool,
                    value: vec![1],
                },
                OwnedField {
                    field_id: 0,
                    value_type: FieldType::Utf8,
                    value: b"tail".to_vec(),
                },
            ],
            false,
        )
        .unwrap();
        let values = [
            OwnedField {
                field_id: 1,
                value_type: FieldType::Null,
                value: Vec::new(),
            },
            OwnedField {
                field_id: 2,
                value_type: FieldType::Bool,
                value: vec![1],
            },
            OwnedField {
                field_id: 3,
                value_type: FieldType::I64,
                value: negative.to_vec(),
            },
            OwnedField {
                field_id: 4,
                value_type: FieldType::Utf8,
                value: b"line\nfeed".to_vec(),
            },
            OwnedField {
                field_id: 5,
                value_type: FieldType::Bytes,
                value: vec![0xff, 0x00],
            },
            OwnedField {
                field_id: 6,
                value_type: FieldType::Map,
                value: map,
            },
            OwnedField {
                field_id: 7,
                value_type: FieldType::Array,
                value: array,
            },
        ];
        let mut inputs = Vec::new();
        for field in &values {
            inputs.push(DocumentFieldInput {
                field_id: field.field_id,
                value_type: field.value_type,
                value: &field.value,
            });
        }
        let mut bytes = Vec::new();
        encode_document_into(&mut bytes, binding, &inputs).unwrap();
        let view = decode_document(&bytes).unwrap();
        let encoded = encode_bounded(&TopFieldsOutput(view)).unwrap();
        let json: serde_json::Value = serde_json::from_slice(&encoded).unwrap();
        assert_eq!(json[0]["value"]["type"], "null");
        assert_eq!(json[1]["value"]["value"], true);
        assert_eq!(json[2]["value"]["value"], "-42");
        assert_eq!(json[3]["value"]["value"], "line\nfeed");
        assert_eq!(json[4]["value"]["value"], "_wA");
        assert_eq!(json[5]["value"]["fields"][0]["id"], 9);
        assert_eq!(json[6]["value"]["items"][0]["value"], true);
        assert_eq!(json[6]["value"]["items"][1]["value"], "tail");
    }

    #[test]
    fn point_and_commit_json_preserve_all_states_and_only_local_receipt_fields() {
        let mut fixture = ServiceFixture::new();
        let document = fixture.binding.document;
        let absent = fixture
            .service
            .point_read(fixture.binding.collection, document)
            .expect("read absent fixture document");
        let absent_json: serde_json::Value =
            serde_json::from_slice(&encode_point(&absent).unwrap()).unwrap();
        assert_eq!(absent_json["state"], "absent");
        assert_eq!(absent_json["context"], serde_json::json!([]));
        assert_eq!(absent_json["siblings"], serde_json::json!([]));
        drop(absent);

        let text = b"first";
        let fields = [DocumentFieldInput {
            field_id: 1,
            value_type: FieldType::Utf8,
            value: text,
        }];
        let empty = CausalContext::new();
        let first_commit = fixture.write_live(document, &empty, &fields, 2);
        let commit_json: serde_json::Value =
            serde_json::from_slice(&encode_local_commit(&first_commit).unwrap()).unwrap();
        assert_eq!(commit_json["durability"], "local_durable");
        let commit_sequence = first_commit.commit_seq().to_string();
        assert_eq!(
            commit_json["commit_sequence"].as_str(),
            Some(commit_sequence.as_str())
        );
        assert_eq!(commit_json.as_object().unwrap().len(), 5);
        assert!(commit_json.get("received_frontier").is_none());
        assert!(commit_json.get("applied_frontier").is_none());
        assert!(commit_json.get("local_durable_frontier").is_none());

        let value = fixture
            .service
            .point_read(fixture.binding.collection, document)
            .expect("read fixture value");
        let value_json: serde_json::Value =
            serde_json::from_slice(&encode_point(&value).unwrap()).unwrap();
        assert_eq!(value_json["state"], "value");
        assert_eq!(value_json["siblings"].as_array().unwrap().len(), 1);
        assert_eq!(
            value_json["siblings"][0]["fields"][0]["value"]["value"],
            "first"
        );
        drop(value);

        let second_text = b"second";
        let second_fields = [DocumentFieldInput {
            field_id: 1,
            value_type: FieldType::Utf8,
            value: second_text,
        }];
        fixture.write_live(document, &empty, &second_fields, 3);
        let conflict = fixture
            .service
            .point_read(fixture.binding.collection, document)
            .expect("read fixture conflict");
        let conflict_context = match conflict.point() {
            DocumentPoint::Conflict { context, .. } => context.clone(),
            other => panic!("expected conflict, got {other:?}"),
        };
        let conflict_json: serde_json::Value =
            serde_json::from_slice(&encode_point(&conflict).unwrap()).unwrap();
        assert_eq!(conflict_json["state"], "conflict");
        assert_eq!(conflict_json["siblings"].as_array().unwrap().len(), 2);
        assert_eq!(conflict_json["context"].as_array().unwrap().len(), 1);
        assert_eq!(conflict_json["siblings"][0]["event"]["sequence"], "1");
        assert_eq!(conflict_json["siblings"][1]["event"]["sequence"], "2");
        drop(conflict);

        fixture.delete(document, &conflict_context, 4);
        let deleted = fixture
            .service
            .point_read(fixture.binding.collection, document)
            .expect("read fixture deletion");
        let deleted_json: serde_json::Value =
            serde_json::from_slice(&encode_point(&deleted).unwrap()).unwrap();
        assert_eq!(deleted_json["state"], "deleted");
        assert_eq!(deleted_json["siblings"].as_array().unwrap().len(), 1);
        assert_eq!(deleted_json["siblings"][0]["kind"], "tombstone");
        assert!(deleted_json["siblings"][0].get("fields").is_none());
    }

    #[test]
    fn response_writer_streams_mixed_escaped_utf8_and_bytes_near_the_cap() {
        let mut fixture = ServiceFixture::new();
        let encoded = mixed_point_response(
            &mut fixture,
            DocumentId::from_bytes([47; 16]),
            560_000,
            488_000,
            5,
        )
        .expect("encode mixed response below the cap");
        assert!(encoded.capacity() >= MAX_JSON_RESPONSE_BYTES);
        assert!(encoded.len() >= MAX_JSON_RESPONSE_BYTES - 256 * 1024);
        assert!(encoded.len() < MAX_JSON_RESPONSE_BYTES);
        assert!(encoded.windows(6).any(|window| window == b"\\u0000"));

        let parsed: serde_json::Value =
            serde_json::from_slice(&encoded).expect("parse complete mixed response");
        let utf8_value = parsed["siblings"][0]["fields"][0]["value"]["value"]
            .as_str()
            .expect("UTF-8 value string");
        assert_eq!(utf8_value.len(), 560_000);
        assert!(utf8_value.bytes().all(|byte| byte == 0));
        let binary_value = parsed["siblings"][0]["fields"][1]["value"]["value"]
            .as_str()
            .expect("base64url byte value");
        assert_eq!(
            URL_SAFE_NO_PAD.decode(binary_value).unwrap(),
            vec![0xa5_u8; 488_000]
        );
    }

    #[test]
    fn base64_output_matches_canonical_encoding_at_chunk_boundaries() {
        for length in [767, 768, 769] {
            let bytes = (0..length)
                .map(|index| u8::try_from(index % 251).expect("byte fixture value"))
                .collect::<Vec<_>>();
            let expected = serde_json::to_vec(&URL_SAFE_NO_PAD.encode(&bytes))
                .expect("encode expected base64url JSON string");
            assert_eq!(
                encode_bounded(&Base64Output(&bytes)).expect("stream base64url JSON string"),
                expected,
                "base64url encoding at input length {length}"
            );
        }
    }

    #[test]
    fn response_writer_returns_capacity_without_a_partial_over_cap_body() {
        let mut fixture = ServiceFixture::new();
        assert!(matches!(
            mixed_point_response(
                &mut fixture,
                DocumentId::from_bytes([48; 16]),
                610_000,
                430_000,
                6,
            ),
            Err(ClientWireError::Capacity)
        ));
    }
}
