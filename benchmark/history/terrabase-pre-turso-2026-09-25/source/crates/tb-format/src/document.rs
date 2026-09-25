//! Canonical document key namespace shared by local storage and replication.

#![allow(
    clippy::missing_panics_doc,
    reason = "fixed-width conversions follow complete length and checksum validation"
)]
#![allow(
    clippy::trivially_copy_pass_by_ref,
    reason = "record magics are static byte arrays borrowed for encode/decode comparison"
)]

use sha2::{Digest, Sha256};
use tb_core::{CollectionId, DatabaseId, DocumentId, OriginId, ReplicaId, ShardId, TransactionId};

use crate::FormatError;

const FORMAT: &str = "document key";
const CATALOG_TAG: u8 = 0xC0;
const ACTIVE_TAG: u8 = 0xC1;
const PRIMARY_TAG: u8 = 0xD0;
const CHANGE_TAG: u8 = 0xD1;
const MARKER_TAG: u8 = 0xD2;
const RETRY_TAG: u8 = 0xD3;
const HIGH_WATER_TAG: u8 = 0xD4;
const VERSION: u8 = 1;
const ACTIVE_MAGIC: &[u8; 8] = b"TBACT001";
const RETRY_MAGIC: &[u8; 8] = b"TBRMP001";
const HIGH_WATER_MAGIC: &[u8; 8] = b"TBSEQ001";
const DOCUMENT_MAGIC: &[u8; 8] = b"TBDOC001";
const SCHEMA_MAGIC: &[u8; 8] = b"TBSCH001";
const CATALOG_MAGIC: &[u8; 8] = b"TBCAT001";
const CHANGE_MAGIC: &[u8; 8] = b"TBCHG001";
const STATE_MAGIC: &[u8; 8] = b"TBSTA001";
const SCHEMA_HEADER_LEN: usize = 8 + 4 + 2;
const SCHEMA_ENTRY_FIXED_LEN: usize = 4 + 4 + 1 + 1 + 1 + 2;
const DOCUMENT_HEADER_LEN: usize = 8 + 4 + 16 + 16 + 8 + 32 + 2 + 4 + 4;
const DOCUMENT_ENTRY_LEN: usize = 13;
const DOCUMENT_MAX_DEPTH: usize = 16;
const DOCUMENT_MAX_NODES: usize = 4_096;
const DOCUMENT_MAX_CHILDREN: usize = 1_024;
const DOCUMENT_MAX_BYTES: usize = tb_core::MAX_VALUE_BYTES;
const CATALOG_HEADER_LEN: usize = 8 + 4 + 16 + 8 + 8 + 32 + 4;
const CHANGE_HEADER_LEN: usize = 8 + 4 + 64 + 16 + 16 + 8 + 32 + 1 + 4;
const STATE_HEADER_LEN: usize = 8 + 4 + 16 + 16 + 2 + 2;
const DOT_LEN: usize = 16 + 16 + 8;
const SIBLING_HEADER_LEN: usize = 64 + 8 + 32 + 1 + 4;

/// Encoded length of a collection catalog-definition key.
pub const CATALOG_KEY_LEN: usize = 2 + 16 + 8;
/// Encoded length of an active-generation catalog key.
pub const ACTIVE_KEY_LEN: usize = 2 + 16;
/// Encoded length of a materialized document-primary key.
pub const PRIMARY_KEY_LEN: usize = 2 + 16 + 16;
/// Encoded length of an authoritative source-change key.
pub const CHANGE_KEY_LEN: usize = 2 + 16 + 16 + 16 + 16 + 8 + 2;
/// Encoded length of an event-marker key.
pub const MARKER_KEY_LEN: usize = 2 + 16 + 16 + 8;
/// Encoded length of a transaction retry-map key.
pub const RETRY_KEY_LEN: usize = 2 + 24;
/// Encoded length of an origin high-water key.
pub const HIGH_WATER_KEY_LEN: usize = 2 + 16 + 16;
/// Fixed length of an active-generation value, including its CRC.
pub const ACTIVE_VALUE_LEN: usize = 8 + 4 + 8 + 32 + 4;
/// Fixed length of a retry-map value, including its CRC.
pub const RETRY_VALUE_LEN: usize = 8 + 4 + 64 + 4;
/// Fixed length of an origin high-water value, including its CRC.
pub const HIGH_WATER_VALUE_LEN: usize = 8 + 4 + 8 + 4;

/// Canonical event identity encoded in document values without depending on
/// the replication crate's policy types.
#[derive(Clone, Copy, Debug, Eq, Ord, PartialEq, PartialOrd)]
pub struct DocumentEventId {
    pub database: DatabaseId,
    pub shard: ShardId,
    pub origin: OriginId,
    pub sequence: u64,
}

impl DocumentEventId {
    /// Validate the nonzero origin sequence and incarnation invariant.
    ///
    /// # Errors
    /// Returns an error if the event cannot identify a committed origin slot.
    pub fn validate(self) -> Result<Self, FormatError> {
        validate_origin(self.origin, self.sequence)?;
        Ok(self)
    }
}

/// Active schema generation and digest in a local catalog pointer.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct ActiveSchema {
    pub generation: u64,
    pub digest: [u8; 32],
}

/// One v1 typed value tag. Missing is represented by an absent field, not a tag.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
#[repr(u8)]
pub enum FieldType {
    Null = 1,
    Bool = 2,
    I64 = 3,
    Utf8 = 4,
    Bytes = 5,
    Map = 6,
    Array = 7,
}

impl TryFrom<u8> for FieldType {
    type Error = FormatError;

    fn try_from(value: u8) -> Result<Self, Self::Error> {
        match value {
            1 => Ok(Self::Null),
            2 => Ok(Self::Bool),
            3 => Ok(Self::I64),
            4 => Ok(Self::Utf8),
            5 => Ok(Self::Bytes),
            6 => Ok(Self::Map),
            7 => Ok(Self::Array),
            _ => Err(invalid("unknown document value type")),
        }
    }
}

/// A field supplied to the bounded document writer. Nested Map/Array bytes
/// follow the canonical recursive grammar and are validated before encoding.
#[derive(Clone, Copy, Debug)]
pub struct DocumentFieldInput<'a> {
    pub field_id: u32,
    pub value_type: FieldType,
    pub value: &'a [u8],
}

/// Immutable binding embedded in one live document.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct DocumentBinding {
    pub collection: CollectionId,
    pub document: DocumentId,
    pub generation: u64,
    pub schema_digest: [u8; 32],
}

/// One field in a canonical schema definition.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct SchemaField<'a> {
    /// Immutable numeric schema field identity; zero denotes the root only.
    pub field_id: u32,
    /// Parent field identity, or zero for a top-level field.
    pub parent_field_id: u32,
    /// Declared field type.
    pub value_type: FieldType,
    /// Homogeneous array element type, present only for an array field.
    pub element_type: Option<FieldType>,
    /// Bits: required=1, nullable=2, array-element-nullable=4.
    pub flags: u8,
    /// Exact, nonempty UTF-8 field name within its parent.
    pub name: &'a str,
}

/// Fully validated schema whose field names borrow from the supplied bytes.
#[derive(Clone, Debug)]
pub struct SchemaDefinition<'a> {
    fields: Vec<SchemaField<'a>>,
}

/// Immutable catalog entry and its verified canonical schema bytes.
#[derive(Clone, Debug)]
pub struct CatalogDefinition<'a> {
    pub collection: CollectionId,
    pub generation: u64,
    pub previous_generation: u64,
    pub digest: [u8; 32],
    pub schema_bytes: &'a [u8],
    pub schema: SchemaDefinition<'a>,
}

/// Kind of a logical document mutation or materialized sibling.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
#[repr(u8)]
pub enum DocumentKind {
    Live = 0,
    Tombstone = 1,
}

impl TryFrom<u8> for DocumentKind {
    type Error = FormatError;

    fn try_from(value: u8) -> Result<Self, Self::Error> {
        match value {
            0 => Ok(Self::Live),
            1 => Ok(Self::Tombstone),
            _ => Err(invalid("unknown document mutation kind")),
        }
    }
}

/// A complete, verified authoritative logical change.
#[derive(Clone, Debug)]
pub struct DocumentChangeView<'a> {
    pub event: DocumentEventId,
    pub binding: DocumentBinding,
    pub kind: DocumentKind,
    pub document_bytes: &'a [u8],
}

/// One causal dot in a materialized state context.
#[derive(Clone, Copy, Debug, Eq, Ord, PartialEq, PartialOrd)]
pub struct DocumentDot {
    pub origin: OriginId,
    pub sequence: u64,
}

/// One encoded sibling supplied to a state writer or returned by its decoder.
#[derive(Clone, Copy, Debug)]
pub struct DocumentSibling<'a> {
    pub event: DocumentEventId,
    pub generation: u64,
    pub schema_digest: [u8; 32],
    pub kind: DocumentKind,
    pub document_bytes: &'a [u8],
}

/// Bounded materialized state with all visible siblings and causal context.
#[derive(Clone, Debug)]
pub struct DocumentStateView<'a> {
    pub collection: CollectionId,
    pub document: DocumentId,
    pub context: Vec<DocumentDot>,
    pub siblings: Vec<DocumentSibling<'a>>,
}

impl<'a> SchemaDefinition<'a> {
    /// Schema fields in ascending immutable field-ID order.
    #[must_use]
    pub fn fields(&self) -> &[SchemaField<'a>] {
        &self.fields
    }

    /// Find a field by its immutable schema identity.
    #[must_use]
    pub fn field(&self, id: u32) -> Option<SchemaField<'a>> {
        self.fields
            .binary_search_by_key(&id, |field| field.field_id)
            .ok()
            .map(|index| self.fields[index])
    }
}

/// Borrowed, syntax-validated live document. Its field offsets and nested
/// values were checked without expanding an owned document tree.
#[derive(Clone, Copy, Debug)]
pub struct DocumentView<'a> {
    binding: DocumentBinding,
    directory: &'a [u8],
    payload: &'a [u8],
    field_count: usize,
}

impl<'a> DocumentView<'a> {
    #[must_use]
    pub const fn binding(self) -> DocumentBinding {
        self.binding
    }

    #[must_use]
    pub const fn field_count(self) -> usize {
        self.field_count
    }

    /// Iterate over top-level fields in their canonical ascending ID order.
    ///
    /// The returned values borrow the same validated document bytes as this
    /// view. No field tree or payload is allocated while iterating.
    #[must_use]
    pub fn fields(self) -> impl ExactSizeIterator<Item = (u32, FieldValueView<'a>)> + 'a {
        self.directory
            .as_chunks::<DOCUMENT_ENTRY_LEN>()
            .0
            .iter()
            .map(move |entry| field_from_entry(entry, self.payload))
    }

    /// Find a field by schema field ID, retaining a borrow of the input bytes.
    #[must_use]
    pub fn field(self, field_id: u32) -> Option<FieldValueView<'a>> {
        for entry in self.directory.as_chunks::<DOCUMENT_ENTRY_LEN>().0 {
            let (id, value) = field_from_entry(entry, self.payload);
            if id == field_id {
                return Some(value);
            }
            if id > field_id {
                break;
            }
        }
        None
    }
}

/// Borrowed typed value from a validated document.
#[derive(Clone, Copy, Debug)]
pub struct FieldValueView<'a> {
    value_type: FieldType,
    bytes: &'a [u8],
}

impl<'a> FieldValueView<'a> {
    #[must_use]
    pub const fn value_type(self) -> FieldType {
        self.value_type
    }

    #[must_use]
    pub const fn encoded_bytes(self) -> &'a [u8] {
        self.bytes
    }

    /// Return a scalar signed integer only when its type is `I64`.
    #[must_use]
    pub fn as_i64(self) -> Option<i64> {
        (self.value_type == FieldType::I64)
            .then(|| i64::from_le_bytes(self.bytes.try_into().expect("validated i64 width")))
    }

    /// Return UTF-8 only when its type is `Utf8`.
    #[must_use]
    pub fn as_str(self) -> Option<&'a str> {
        (self.value_type == FieldType::Utf8)
            .then(|| std::str::from_utf8(self.bytes).expect("validated UTF-8"))
    }

    /// Iterate over a map's fields in canonical ascending field-ID order.
    ///
    /// Returns `None` unless this value is a map. The complete document was
    /// validated before this view was created, so iteration only walks its
    /// borrowed encoded bytes.
    #[must_use]
    pub fn map_fields(
        self,
    ) -> Option<impl ExactSizeIterator<Item = (u32, FieldValueView<'a>)> + 'a> {
        (self.value_type == FieldType::Map).then(|| {
            ContainerValueIter::new(self.bytes, true).map(|(field_id, value)| {
                (field_id.expect("validated map child has a field ID"), value)
            })
        })
    }

    /// Iterate over an array's values in their stored order.
    ///
    /// Returns `None` unless this value is an array. The iterator does not
    /// allocate and each value borrows the original document bytes.
    #[must_use]
    pub fn array_items(self) -> Option<impl ExactSizeIterator<Item = FieldValueView<'a>> + 'a> {
        (self.value_type == FieldType::Array)
            .then(|| ContainerValueIter::new(self.bytes, false).map(|(_, value)| value))
    }
}

fn field_from_entry<'a>(
    entry: &[u8; DOCUMENT_ENTRY_LEN],
    payload: &'a [u8],
) -> (u32, FieldValueView<'a>) {
    let field_id = u32::from_le_bytes(entry[..4].try_into().unwrap());
    let offset = usize::try_from(u32::from_le_bytes(entry[5..9].try_into().unwrap()))
        .expect("validated document field offset fits usize");
    let length = usize::try_from(u32::from_le_bytes(entry[9..13].try_into().unwrap()))
        .expect("validated document field length fits usize");
    (
        field_id,
        FieldValueView {
            value_type: FieldType::try_from(entry[4]).expect("validated document tag"),
            bytes: &payload[offset..offset + length],
        },
    )
}

/// Allocation-free iterator over the children of one already validated map or
/// array value. A field ID is present only for a map child.
#[derive(Clone, Debug)]
struct ContainerValueIter<'a> {
    bytes: &'a [u8],
    offset: usize,
    remaining: usize,
    has_field_ids: bool,
}

impl<'a> ContainerValueIter<'a> {
    fn new(bytes: &'a [u8], has_field_ids: bool) -> Self {
        Self {
            bytes,
            offset: 2,
            remaining: usize::from(u16::from_le_bytes(
                bytes[..2].try_into().expect("validated container header"),
            )),
            has_field_ids,
        }
    }
}

impl<'a> Iterator for ContainerValueIter<'a> {
    type Item = (Option<u32>, FieldValueView<'a>);

    fn next(&mut self) -> Option<Self::Item> {
        if self.remaining == 0 {
            return None;
        }

        let field_id = if self.has_field_ids {
            let end = self.offset + 4;
            let field_id = u32::from_le_bytes(
                self.bytes[self.offset..end]
                    .try_into()
                    .expect("validated map field ID"),
            );
            self.offset = end;
            Some(field_id)
        } else {
            None
        };
        let value_type =
            FieldType::try_from(self.bytes[self.offset]).expect("validated nested document tag");
        self.offset += 1;
        let length_end = self.offset + 4;
        let value_len = usize::try_from(u32::from_le_bytes(
            self.bytes[self.offset..length_end]
                .try_into()
                .expect("validated nested document length"),
        ))
        .expect("validated nested value length fits usize");
        self.offset = length_end;
        let value_end = self.offset + value_len;
        let value = FieldValueView {
            value_type,
            bytes: &self.bytes[self.offset..value_end],
        };
        self.offset = value_end;
        self.remaining -= 1;
        Some((field_id, value))
    }

    fn size_hint(&self) -> (usize, Option<usize>) {
        (self.remaining, Some(self.remaining))
    }
}

impl ExactSizeIterator for ContainerValueIter<'_> {}

/// A validated key in the document-owned logical namespace.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum DocumentKey {
    /// Immutable catalog definition for one generation.
    Catalog {
        collection: CollectionId,
        generation: u64,
    },
    /// Active catalog generation for one collection.
    Active { collection: CollectionId },
    /// Materialized primary state of one document.
    Primary {
        collection: CollectionId,
        document: DocumentId,
    },
    /// Authoritative source mutation in an origin event.
    Change {
        collection: CollectionId,
        document: DocumentId,
        origin: OriginId,
        sequence: u64,
        ordinal: u16,
    },
    /// Complete metadata of one committed origin event.
    Marker { origin: OriginId, sequence: u64 },
    /// Durable retry reconciliation for one transaction ID.
    Retry { transaction_id: TransactionId },
    /// Last committed event sequence of one origin.
    HighWater { origin: OriginId },
}

fn invalid(reason: &'static str) -> FormatError {
    FormatError::Invalid {
        format: FORMAT,
        reason,
    }
}

fn validate_origin(origin: OriginId, sequence: u64) -> Result<(), FormatError> {
    if origin.incarnation == [0; 16] || sequence == 0 {
        return Err(invalid("origin incarnation and sequence must be nonzero"));
    }
    Ok(())
}

fn validate_incarnation(origin: OriginId) -> Result<(), FormatError> {
    if origin.incarnation == [0; 16] {
        return Err(invalid("origin incarnation must be nonzero"));
    }
    Ok(())
}

fn encode_fixed<const N: usize>(magic: &[u8; 8], body: &[u8]) -> [u8; N] {
    let mut bytes = [0_u8; N];
    bytes[..8].copy_from_slice(magic);
    bytes[8..12].copy_from_slice(
        &u32::try_from(N)
            .expect("fixed record length fits u32")
            .to_le_bytes(),
    );
    bytes[12..N - 4].copy_from_slice(body);
    let checksum = crc32c::crc32c(&bytes[..N - 4]);
    bytes[N - 4..].copy_from_slice(&checksum.to_le_bytes());
    bytes
}

fn decode_fixed<'a, const N: usize>(
    bytes: &'a [u8],
    magic: &[u8; 8],
    format: &'static str,
) -> Result<&'a [u8], FormatError> {
    if bytes.len() < N {
        return Err(FormatError::Truncated {
            format,
            expected: N,
            actual: bytes.len(),
        });
    }
    if bytes.len() != N
        || &bytes[..8] != magic
        || u32::from_le_bytes(bytes[8..12].try_into().unwrap())
            != u32::try_from(N).expect("fixed record length fits u32")
    {
        return Err(FormatError::Invalid {
            format,
            reason: "fixed record length, magic, or version",
        });
    }
    if crc32c::crc32c(&bytes[..N - 4]) != u32::from_le_bytes(bytes[N - 4..].try_into().unwrap()) {
        return Err(FormatError::ChecksumMismatch { format });
    }
    Ok(&bytes[12..N - 4])
}

fn encode_event_id(id: DocumentEventId) -> [u8; 64] {
    let mut bytes = [0_u8; 64];
    bytes[..16].copy_from_slice(&id.database.to_bytes());
    bytes[16..24].copy_from_slice(&id.shard.get().to_le_bytes());
    bytes[24..40].copy_from_slice(&id.origin.replica_id.to_bytes());
    bytes[40..56].copy_from_slice(&id.origin.incarnation);
    bytes[56..64].copy_from_slice(&id.sequence.to_le_bytes());
    bytes
}

fn decode_event_id(bytes: &[u8]) -> Result<DocumentEventId, FormatError> {
    let id = DocumentEventId {
        database: DatabaseId::from_bytes(bytes[..16].try_into().unwrap()),
        shard: ShardId::new(u64::from_le_bytes(bytes[16..24].try_into().unwrap())),
        origin: OriginId {
            replica_id: ReplicaId::from_bytes(bytes[24..40].try_into().unwrap()),
            incarnation: bytes[40..56].try_into().unwrap(),
        },
        sequence: u64::from_le_bytes(bytes[56..64].try_into().unwrap()),
    };
    id.validate()
}

fn read_u16(bytes: &[u8], offset: usize) -> Result<u16, FormatError> {
    let end = offset
        .checked_add(2)
        .ok_or_else(|| invalid("document offset overflow"))?;
    let value = bytes
        .get(offset..end)
        .ok_or_else(|| invalid("truncated document value"))?;
    Ok(u16::from_le_bytes(value.try_into().unwrap()))
}

fn read_u32(bytes: &[u8], offset: usize) -> Result<u32, FormatError> {
    let end = offset
        .checked_add(4)
        .ok_or_else(|| invalid("document offset overflow"))?;
    let value = bytes
        .get(offset..end)
        .ok_or_else(|| invalid("truncated document value"))?;
    Ok(u32::from_le_bytes(value.try_into().unwrap()))
}

fn validate_value(
    value_type: FieldType,
    bytes: &[u8],
    depth: usize,
    inside_array: bool,
    nodes: &mut usize,
) -> Result<(), FormatError> {
    *nodes = nodes
        .checked_add(1)
        .ok_or_else(|| invalid("document node count overflow"))?;
    if *nodes > DOCUMENT_MAX_NODES || depth > DOCUMENT_MAX_DEPTH {
        return Err(invalid("document node or nesting limit"));
    }
    match value_type {
        FieldType::Null if bytes.is_empty() => Ok(()),
        FieldType::Null => Err(invalid("null must have no payload")),
        FieldType::Bool if bytes == [0] || bytes == [1] => Ok(()),
        FieldType::Bool => Err(invalid("boolean must be one byte 0 or 1")),
        FieldType::I64 if bytes.len() == 8 => Ok(()),
        FieldType::I64 => Err(invalid("signed integer must be eight bytes")),
        FieldType::Utf8 => std::str::from_utf8(bytes)
            .map(|_| ())
            .map_err(|_| invalid("malformed UTF-8 field")),
        FieldType::Bytes => Ok(()),
        FieldType::Map | FieldType::Array => {
            if value_type == FieldType::Array && inside_array {
                return Err(invalid("nested arrays are unsupported"));
            }
            let count = usize::from(read_u16(bytes, 0)?);
            if count > DOCUMENT_MAX_CHILDREN {
                return Err(invalid("document container child limit"));
            }
            let mut offset = 2_usize;
            let mut previous_field_id = 0_u32;
            for _ in 0..count {
                if value_type == FieldType::Map {
                    let id = read_u32(bytes, offset)?;
                    if id == 0 || id <= previous_field_id {
                        return Err(invalid("map field IDs must increase"));
                    }
                    previous_field_id = id;
                    offset += 4;
                }
                let tag = *bytes
                    .get(offset)
                    .ok_or_else(|| invalid("truncated field tag"))?;
                let child_type = FieldType::try_from(tag)?;
                offset += 1;
                let length = usize::try_from(read_u32(bytes, offset)?)
                    .map_err(|_| invalid("field length exceeds platform"))?;
                offset += 4;
                let end = offset
                    .checked_add(length)
                    .ok_or_else(|| invalid("field length overflow"))?;
                let child = bytes
                    .get(offset..end)
                    .ok_or_else(|| invalid("field exceeds container"))?;
                validate_value(
                    child_type,
                    child,
                    depth + 1,
                    inside_array || value_type == FieldType::Array,
                    nodes,
                )?;
                offset = end;
            }
            if offset != bytes.len() {
                return Err(invalid("container has trailing bytes"));
            }
            Ok(())
        }
    }
}

/// Validate and borrow a complete live document without materializing its fields.
///
/// # Errors
/// Returns an error for a malformed, over-limit, noncanonical, or corrupt value.
pub fn decode_document(bytes: &[u8]) -> Result<DocumentView<'_>, FormatError> {
    if bytes.len() < DOCUMENT_HEADER_LEN + 4 {
        return Err(FormatError::Truncated {
            format: "document value",
            expected: DOCUMENT_HEADER_LEN + 4,
            actual: bytes.len(),
        });
    }
    if bytes.len() > DOCUMENT_MAX_BYTES {
        return Err(FormatError::LimitExceeded {
            what: "document value bytes",
            value: bytes.len(),
            limit: DOCUMENT_MAX_BYTES,
        });
    }
    if &bytes[..8] != DOCUMENT_MAGIC
        || usize::try_from(read_u32(bytes, 8)?)
            .map_err(|_| invalid("document length exceeds platform"))?
            != bytes.len()
    {
        return Err(invalid("document magic, version, or total length"));
    }
    if crc32c::crc32c(&bytes[..bytes.len() - 4])
        != u32::from_le_bytes(bytes[bytes.len() - 4..].try_into().unwrap())
    {
        return Err(FormatError::ChecksumMismatch {
            format: "document value",
        });
    }
    let generation = u64::from_le_bytes(bytes[44..52].try_into().unwrap());
    if generation == 0 {
        return Err(invalid("document generation must be nonzero"));
    }
    let field_count = usize::from(read_u16(bytes, 84)?);
    if field_count > DOCUMENT_MAX_CHILDREN {
        return Err(invalid("document field count"));
    }
    let directory_len = usize::try_from(read_u32(bytes, 86)?)
        .map_err(|_| invalid("directory length exceeds platform"))?;
    let payload_len = usize::try_from(read_u32(bytes, 90)?)
        .map_err(|_| invalid("payload length exceeds platform"))?;
    if directory_len != field_count * DOCUMENT_ENTRY_LEN
        || DOCUMENT_HEADER_LEN
            .checked_add(directory_len)
            .and_then(|len| len.checked_add(payload_len))
            .and_then(|len| len.checked_add(4))
            != Some(bytes.len())
    {
        return Err(invalid("document section lengths"));
    }
    let directory = &bytes[DOCUMENT_HEADER_LEN..DOCUMENT_HEADER_LEN + directory_len];
    let payload = &bytes[DOCUMENT_HEADER_LEN + directory_len..bytes.len() - 4];
    let mut previous_field_id = 0_u32;
    let mut previous_end = 0_usize;
    let mut nodes = 0_usize;
    for entry in directory.as_chunks::<DOCUMENT_ENTRY_LEN>().0 {
        let id = u32::from_le_bytes(entry[..4].try_into().unwrap());
        if id == 0 || id <= previous_field_id {
            return Err(invalid("document field IDs must increase"));
        }
        previous_field_id = id;
        let value_type = FieldType::try_from(entry[4])?;
        let offset = usize::try_from(u32::from_le_bytes(entry[5..9].try_into().unwrap()))
            .map_err(|_| invalid("field offset exceeds platform"))?;
        let length = usize::try_from(u32::from_le_bytes(entry[9..13].try_into().unwrap()))
            .map_err(|_| invalid("field length exceeds platform"))?;
        if offset != previous_end {
            return Err(invalid("document fields have a gap or overlap"));
        }
        previous_end = offset
            .checked_add(length)
            .ok_or_else(|| invalid("field end overflow"))?;
        let field_bytes = payload
            .get(offset..previous_end)
            .ok_or_else(|| invalid("field exceeds document payload"))?;
        validate_value(value_type, field_bytes, 1, false, &mut nodes)?;
    }
    if previous_end != payload.len() {
        return Err(invalid("document payload has trailing bytes"));
    }
    Ok(DocumentView {
        binding: DocumentBinding {
            collection: CollectionId::from_bytes(bytes[12..28].try_into().unwrap()),
            document: DocumentId::from_bytes(bytes[28..44].try_into().unwrap()),
            generation,
            schema_digest: bytes[52..84].try_into().unwrap(),
        },
        directory,
        payload,
        field_count,
    })
}

/// Encode one syntax-valid live document into a caller-owned output buffer.
/// The caller must separately validate its fields against the pinned schema.
///
/// # Errors
/// Returns an error for invalid identity, field order/types, nested limits,
/// allocation failure, or a record exceeding the 1-MiB value cap.
pub fn encode_document_into(
    output: &mut Vec<u8>,
    binding: DocumentBinding,
    fields: &[DocumentFieldInput<'_>],
) -> Result<(), FormatError> {
    if binding.generation == 0 || fields.len() > DOCUMENT_MAX_CHILDREN {
        return Err(invalid("document generation or field count"));
    }
    let mut previous_field_id = 0_u32;
    let mut payload_len = 0_usize;
    let mut nodes = 0_usize;
    for field in fields {
        if field.field_id == 0 || field.field_id <= previous_field_id {
            return Err(invalid("document field IDs must increase"));
        }
        previous_field_id = field.field_id;
        validate_value(field.value_type, field.value, 1, false, &mut nodes)?;
        payload_len = payload_len
            .checked_add(field.value.len())
            .ok_or_else(|| invalid("document payload length overflow"))?;
    }
    let directory_len = fields
        .len()
        .checked_mul(DOCUMENT_ENTRY_LEN)
        .ok_or_else(|| invalid("document directory length overflow"))?;
    let total_len = DOCUMENT_HEADER_LEN
        .checked_add(directory_len)
        .and_then(|len| len.checked_add(payload_len))
        .and_then(|len| len.checked_add(4))
        .ok_or_else(|| invalid("document total length overflow"))?;
    if total_len > DOCUMENT_MAX_BYTES {
        return Err(FormatError::LimitExceeded {
            what: "document value bytes",
            value: total_len,
            limit: DOCUMENT_MAX_BYTES,
        });
    }
    let total =
        u32::try_from(total_len).map_err(|_| invalid("document total length exceeds u32"))?;
    let directory = u32::try_from(directory_len)
        .map_err(|_| invalid("document directory length exceeds u32"))?;
    let payload =
        u32::try_from(payload_len).map_err(|_| invalid("document payload length exceeds u32"))?;
    output.clear();
    output
        .try_reserve_exact(total_len)
        .map_err(|_| FormatError::Allocation)?;
    output.extend_from_slice(DOCUMENT_MAGIC);
    output.extend_from_slice(&total.to_le_bytes());
    output.extend_from_slice(&binding.collection.to_bytes());
    output.extend_from_slice(&binding.document.to_bytes());
    output.extend_from_slice(&binding.generation.to_le_bytes());
    output.extend_from_slice(&binding.schema_digest);
    output.extend_from_slice(
        &u16::try_from(fields.len())
            .map_err(|_| invalid("document field count exceeds u16"))?
            .to_le_bytes(),
    );
    output.extend_from_slice(&directory.to_le_bytes());
    output.extend_from_slice(&payload.to_le_bytes());
    let mut offset = 0_u32;
    for field in fields {
        let length =
            u32::try_from(field.value.len()).map_err(|_| invalid("field length exceeds u32"))?;
        output.extend_from_slice(&field.field_id.to_le_bytes());
        output.push(field.value_type as u8);
        output.extend_from_slice(&offset.to_le_bytes());
        output.extend_from_slice(&length.to_le_bytes());
        offset = offset
            .checked_add(length)
            .ok_or_else(|| invalid("field offset overflow"))?;
    }
    for field in fields {
        output.extend_from_slice(field.value);
    }
    let checksum = crc32c::crc32c(output);
    output.extend_from_slice(&checksum.to_le_bytes());
    debug_assert_eq!(output.len(), total_len);
    Ok(())
}

fn validate_schema_field(
    field: SchemaField<'_>,
    earlier: &[SchemaField<'_>],
) -> Result<(), FormatError> {
    if field.field_id == 0
        || earlier
            .last()
            .is_some_and(|previous| previous.field_id >= field.field_id)
        || field.name.is_empty()
        || field.name.len() > 128
        || field.flags & !0b111 != 0
    {
        return Err(invalid("schema field identity, name, flags, or order"));
    }
    if field.value_type == FieldType::Array {
        if field
            .element_type
            .is_none_or(|element| element == FieldType::Array)
        {
            return Err(invalid("array requires one non-array element type"));
        }
    } else if field.element_type.is_some() || field.flags & 0b100 != 0 {
        return Err(invalid(
            "non-array has element type or element-nullable flag",
        ));
    }
    let mut parent_id = field.parent_field_id;
    let mut depth = 1_usize;
    let mut inside_array = false;
    while parent_id != 0 {
        let parent = earlier
            .iter()
            .find(|previous| previous.field_id == parent_id)
            .ok_or_else(|| invalid("schema parent must precede child"))?;
        match parent.value_type {
            FieldType::Map => depth += 1,
            FieldType::Array if parent.element_type == Some(FieldType::Map) => {
                depth += 2;
                inside_array = true;
            }
            _ => return Err(invalid("schema parent is not a map")),
        }
        parent_id = parent.parent_field_id;
    }
    if depth > DOCUMENT_MAX_DEPTH || (inside_array && field.value_type == FieldType::Array) {
        return Err(invalid("schema nesting limit or nested array"));
    }
    if earlier.iter().any(|previous| {
        previous.parent_field_id == field.parent_field_id && previous.name == field.name
    }) {
        return Err(invalid("duplicate schema sibling name"));
    }
    Ok(())
}

/// Encode an ordered canonical schema definition for catalog publication.
///
/// # Errors
/// Returns an error for duplicate/invalid fields, unsupported nesting, over-cap
/// encoded bytes, or a fallible output allocation.
pub fn encode_schema_into(
    output: &mut Vec<u8>,
    fields: &[SchemaField<'_>],
) -> Result<(), FormatError> {
    if fields.len() > DOCUMENT_MAX_CHILDREN {
        return Err(invalid("schema field count"));
    }
    let mut total_len = SCHEMA_HEADER_LEN + 4;
    for (index, field) in fields.iter().copied().enumerate() {
        validate_schema_field(field, &fields[..index])?;
        total_len = total_len
            .checked_add(SCHEMA_ENTRY_FIXED_LEN)
            .and_then(|len| len.checked_add(field.name.len()))
            .ok_or_else(|| invalid("schema length overflow"))?;
    }
    if total_len > DOCUMENT_MAX_BYTES {
        return Err(FormatError::LimitExceeded {
            what: "schema definition bytes",
            value: total_len,
            limit: DOCUMENT_MAX_BYTES,
        });
    }
    let total = u32::try_from(total_len).map_err(|_| invalid("schema length exceeds u32"))?;
    output.clear();
    output
        .try_reserve_exact(total_len)
        .map_err(|_| FormatError::Allocation)?;
    output.extend_from_slice(SCHEMA_MAGIC);
    output.extend_from_slice(&total.to_le_bytes());
    output.extend_from_slice(
        &u16::try_from(fields.len())
            .map_err(|_| invalid("schema field count exceeds u16"))?
            .to_le_bytes(),
    );
    for field in fields {
        output.extend_from_slice(&field.field_id.to_le_bytes());
        output.extend_from_slice(&field.parent_field_id.to_le_bytes());
        output.push(field.value_type as u8);
        output.push(field.element_type.map_or(0, |kind| kind as u8));
        output.push(field.flags);
        output.extend_from_slice(
            &u16::try_from(field.name.len())
                .map_err(|_| invalid("schema field name exceeds u16"))?
                .to_le_bytes(),
        );
        output.extend_from_slice(field.name.as_bytes());
    }
    let checksum = crc32c::crc32c(output);
    output.extend_from_slice(&checksum.to_le_bytes());
    debug_assert_eq!(output.len(), total_len);
    Ok(())
}

/// Decode and validate a canonical schema definition.
///
/// The caller must reserve space for up to 1,024 small field descriptors before
/// calling; the returned view keeps those descriptors and borrows field names.
///
/// # Errors
/// Returns an error for malformed, over-cap, unsorted, or corrupt schema bytes.
pub fn decode_schema(bytes: &[u8]) -> Result<SchemaDefinition<'_>, FormatError> {
    if bytes.len() < SCHEMA_HEADER_LEN + 4 {
        return Err(FormatError::Truncated {
            format: "schema definition",
            expected: SCHEMA_HEADER_LEN + 4,
            actual: bytes.len(),
        });
    }
    if bytes.len() > DOCUMENT_MAX_BYTES {
        return Err(FormatError::LimitExceeded {
            what: "schema definition bytes",
            value: bytes.len(),
            limit: DOCUMENT_MAX_BYTES,
        });
    }
    if &bytes[..8] != SCHEMA_MAGIC
        || usize::try_from(read_u32(bytes, 8)?)
            .map_err(|_| invalid("schema length exceeds platform"))?
            != bytes.len()
    {
        return Err(invalid("schema magic, version, or total length"));
    }
    if crc32c::crc32c(&bytes[..bytes.len() - 4])
        != u32::from_le_bytes(bytes[bytes.len() - 4..].try_into().unwrap())
    {
        return Err(FormatError::ChecksumMismatch {
            format: "schema definition",
        });
    }
    let count = usize::from(read_u16(bytes, 12)?);
    if count > DOCUMENT_MAX_CHILDREN {
        return Err(invalid("schema field count"));
    }
    if count
        .checked_mul(SCHEMA_ENTRY_FIXED_LEN)
        .is_none_or(|minimum| minimum > bytes.len() - SCHEMA_HEADER_LEN - 4)
    {
        return Err(invalid("schema field count exceeds available bytes"));
    }
    let mut fields = Vec::new();
    fields
        .try_reserve_exact(count)
        .map_err(|_| FormatError::Allocation)?;
    let mut position = SCHEMA_HEADER_LEN;
    let payload_end = bytes.len() - 4;
    for _ in 0..count {
        if position
            .checked_add(SCHEMA_ENTRY_FIXED_LEN)
            .is_none_or(|end| end > payload_end)
        {
            return Err(invalid("truncated schema field"));
        }
        let field_id = read_u32(bytes, position)?;
        let parent_field_id = read_u32(bytes, position + 4)?;
        let value_type = FieldType::try_from(bytes[position + 8])?;
        let element_type = match bytes[position + 9] {
            0 => None,
            tag => Some(FieldType::try_from(tag)?),
        };
        let flags = bytes[position + 10];
        let name_len = usize::from(read_u16(bytes, position + 11)?);
        position += SCHEMA_ENTRY_FIXED_LEN;
        let end = position
            .checked_add(name_len)
            .ok_or_else(|| invalid("schema name length overflow"))?;
        let name_bytes = bytes
            .get(position..end)
            .filter(|_| end <= payload_end)
            .ok_or_else(|| invalid("schema name exceeds record"))?;
        let name =
            std::str::from_utf8(name_bytes).map_err(|_| invalid("invalid schema UTF-8 name"))?;
        let field = SchemaField {
            field_id,
            parent_field_id,
            value_type,
            element_type,
            flags,
            name,
        };
        validate_schema_field(field, &fields)?;
        fields.push(field);
        position = end;
    }
    if position != payload_end {
        return Err(invalid("schema has trailing bytes"));
    }
    Ok(SchemaDefinition { fields })
}

fn validate_schema_value(
    schema: &SchemaDefinition<'_>,
    field: SchemaField<'_>,
    actual: FieldType,
    bytes: &[u8],
    depth: usize,
) -> Result<(), FormatError> {
    if actual == FieldType::Null {
        if field.value_type == FieldType::Null || field.flags & 0b010 != 0 {
            return Ok(());
        }
        return Err(invalid("nonnullable field is null"));
    }
    if actual != field.value_type {
        return Err(invalid("document field type differs from schema"));
    }
    match actual {
        FieldType::Map => validate_schema_map(schema, field.field_id, bytes, depth + 1),
        FieldType::Array => {
            let count = usize::from(read_u16(bytes, 0)?);
            let mut position = 2;
            for _ in 0..count {
                let actual = FieldType::try_from(bytes[position])?;
                position += 1;
                let length = usize::try_from(read_u32(bytes, position)?)
                    .map_err(|_| invalid("array element length exceeds platform"))?;
                position += 4;
                let end = position
                    .checked_add(length)
                    .ok_or_else(|| invalid("array element length overflow"))?;
                let value = bytes
                    .get(position..end)
                    .ok_or_else(|| invalid("array element exceeds container"))?;
                if actual == FieldType::Null {
                    if field.element_type != Some(FieldType::Null) && field.flags & 0b100 == 0 {
                        return Err(invalid("nonnullable array element is null"));
                    }
                } else if field.element_type != Some(actual) {
                    return Err(invalid("array element type differs from schema"));
                } else if actual == FieldType::Map {
                    validate_schema_map(schema, field.field_id, value, depth + 2)?;
                }
                position = end;
            }
            Ok(())
        }
        _ => Ok(()),
    }
}

fn validate_required(
    schema: &SchemaDefinition<'_>,
    parent_id: u32,
    seen: &[u32],
) -> Result<(), FormatError> {
    for field in schema.fields() {
        if field.parent_field_id == parent_id
            && field.flags & 1 != 0
            && seen.binary_search(&field.field_id).is_err()
        {
            return Err(invalid("required schema field is missing"));
        }
    }
    Ok(())
}

fn validate_schema_map(
    schema: &SchemaDefinition<'_>,
    parent_id: u32,
    bytes: &[u8],
    depth: usize,
) -> Result<(), FormatError> {
    if depth > DOCUMENT_MAX_DEPTH {
        return Err(invalid("schema-bound document nesting limit"));
    }
    let count = usize::from(read_u16(bytes, 0)?);
    let mut seen = Vec::new();
    seen.try_reserve_exact(count)
        .map_err(|_| FormatError::Allocation)?;
    let mut position = 2;
    for _ in 0..count {
        let id = read_u32(bytes, position)?;
        position += 4;
        let actual = FieldType::try_from(bytes[position])?;
        position += 1;
        let length = usize::try_from(read_u32(bytes, position)?)
            .map_err(|_| invalid("map field length exceeds platform"))?;
        position += 4;
        let end = position
            .checked_add(length)
            .ok_or_else(|| invalid("map field length overflow"))?;
        let value = bytes
            .get(position..end)
            .ok_or_else(|| invalid("map field exceeds container"))?;
        let field = schema
            .field(id)
            .filter(|field| field.parent_field_id == parent_id)
            .ok_or_else(|| invalid("unknown map field or wrong schema parent"))?;
        validate_schema_value(schema, field, actual, value, depth)?;
        seen.push(id);
        position = end;
    }
    validate_required(schema, parent_id, &seen)
}

/// Validate every field, array element, nested map and required path against
/// the immutable schema bound by a syntactically valid document.
///
/// # Errors
/// Returns an error for an unknown field, type mismatch, illegal null or
/// missing required field. The caller checks generation and digest binding.
pub fn validate_document_schema(
    document: DocumentView<'_>,
    schema: &SchemaDefinition<'_>,
) -> Result<(), FormatError> {
    let mut seen = Vec::new();
    seen.try_reserve_exact(document.field_count)
        .map_err(|_| FormatError::Allocation)?;
    for entry in document.directory.as_chunks::<DOCUMENT_ENTRY_LEN>().0 {
        let id = u32::from_le_bytes(entry[..4].try_into().unwrap());
        let actual = FieldType::try_from(entry[4])?;
        let offset = usize::try_from(u32::from_le_bytes(entry[5..9].try_into().unwrap()))
            .map_err(|_| invalid("field offset exceeds platform"))?;
        let length = usize::try_from(u32::from_le_bytes(entry[9..13].try_into().unwrap()))
            .map_err(|_| invalid("field length exceeds platform"))?;
        let field = schema
            .field(id)
            .filter(|field| field.parent_field_id == 0)
            .ok_or_else(|| invalid("unknown top-level field"))?;
        validate_schema_value(
            schema,
            field,
            actual,
            &document.payload[offset..offset + length],
            1,
        )?;
        seen.push(id);
    }
    validate_required(schema, 0, &seen)
}

fn decode_variable_record<'a>(
    bytes: &'a [u8],
    magic: &[u8; 8],
    format: &'static str,
    minimum: usize,
) -> Result<&'a [u8], FormatError> {
    if bytes.len() < minimum {
        return Err(FormatError::Truncated {
            format,
            expected: minimum,
            actual: bytes.len(),
        });
    }
    if bytes.len() > DOCUMENT_MAX_BYTES {
        return Err(FormatError::LimitExceeded {
            what: format,
            value: bytes.len(),
            limit: DOCUMENT_MAX_BYTES,
        });
    }
    if &bytes[..8] != magic || usize::try_from(read_u32(bytes, 8)?).ok() != Some(bytes.len()) {
        return Err(FormatError::Invalid {
            format,
            reason: "magic, version, or total length",
        });
    }
    if crc32c::crc32c(&bytes[..bytes.len() - 4])
        != u32::from_le_bytes(bytes[bytes.len() - 4..].try_into().unwrap())
    {
        return Err(FormatError::ChecksumMismatch { format });
    }
    Ok(&bytes[12..bytes.len() - 4])
}

fn begin_variable_record(
    output: &mut Vec<u8>,
    magic: &[u8; 8],
    total_len: usize,
) -> Result<(), FormatError> {
    if total_len > DOCUMENT_MAX_BYTES {
        return Err(FormatError::LimitExceeded {
            what: "document-owned value bytes",
            value: total_len,
            limit: DOCUMENT_MAX_BYTES,
        });
    }
    let total = u32::try_from(total_len).map_err(|_| invalid("record length exceeds u32"))?;
    output.clear();
    output
        .try_reserve_exact(total_len)
        .map_err(|_| FormatError::Allocation)?;
    output.extend_from_slice(magic);
    output.extend_from_slice(&total.to_le_bytes());
    Ok(())
}

fn finish_variable_record(output: &mut Vec<u8>, total_len: usize) {
    let checksum = crc32c::crc32c(output);
    output.extend_from_slice(&checksum.to_le_bytes());
    debug_assert_eq!(output.len(), total_len);
}

/// Encode an immutable catalog definition with its SHA-256 schema digest.
///
/// # Errors
/// Returns an error for an invalid schema/generation or oversized value.
pub fn encode_catalog_into(
    output: &mut Vec<u8>,
    collection: CollectionId,
    generation: u64,
    previous_generation: u64,
    schema_bytes: &[u8],
) -> Result<[u8; 32], FormatError> {
    if generation == 0 || previous_generation >= generation {
        return Err(invalid("catalog generation chain"));
    }
    decode_schema(schema_bytes)?;
    let total_len = CATALOG_HEADER_LEN
        .checked_add(schema_bytes.len())
        .and_then(|length| length.checked_add(4))
        .ok_or_else(|| invalid("catalog length overflow"))?;
    let digest: [u8; 32] = Sha256::digest(schema_bytes).into();
    begin_variable_record(output, CATALOG_MAGIC, total_len)?;
    output.extend_from_slice(&collection.to_bytes());
    output.extend_from_slice(&generation.to_le_bytes());
    output.extend_from_slice(&previous_generation.to_le_bytes());
    output.extend_from_slice(&digest);
    output.extend_from_slice(
        &u32::try_from(schema_bytes.len())
            .map_err(|_| invalid("schema length exceeds u32"))?
            .to_le_bytes(),
    );
    output.extend_from_slice(schema_bytes);
    finish_variable_record(output, total_len);
    Ok(digest)
}

/// Decode a complete catalog definition, including its nested schema and digest.
///
/// # Errors
/// Returns an error for a malformed schema, digest mismatch, or bad generation.
pub fn decode_catalog(bytes: &[u8]) -> Result<CatalogDefinition<'_>, FormatError> {
    decode_variable_record(
        bytes,
        CATALOG_MAGIC,
        "catalog definition",
        CATALOG_HEADER_LEN + 4,
    )?;
    let generation = u64::from_le_bytes(bytes[28..36].try_into().unwrap());
    let previous_generation = u64::from_le_bytes(bytes[36..44].try_into().unwrap());
    if generation == 0 || previous_generation >= generation {
        return Err(invalid("catalog generation chain"));
    }
    let schema_len = usize::try_from(read_u32(bytes, 76)?)
        .map_err(|_| invalid("schema length exceeds platform"))?;
    if CATALOG_HEADER_LEN
        .checked_add(schema_len)
        .and_then(|length| length.checked_add(4))
        != Some(bytes.len())
    {
        return Err(invalid("catalog schema length"));
    }
    let schema_bytes = &bytes[CATALOG_HEADER_LEN..bytes.len() - 4];
    let digest: [u8; 32] = bytes[44..76].try_into().unwrap();
    if Sha256::digest(schema_bytes).as_slice() != digest {
        return Err(invalid("catalog schema digest mismatch"));
    }
    let schema = decode_schema(schema_bytes)?;
    Ok(CatalogDefinition {
        collection: CollectionId::from_bytes(bytes[12..28].try_into().unwrap()),
        generation,
        previous_generation,
        digest,
        schema_bytes,
        schema,
    })
}

fn validate_embedded_document(
    binding: DocumentBinding,
    kind: DocumentKind,
    bytes: &[u8],
) -> Result<(), FormatError> {
    match kind {
        DocumentKind::Live => {
            let document = decode_document(bytes)?;
            if document.binding() != binding {
                return Err(invalid("embedded document binding mismatch"));
            }
        }
        DocumentKind::Tombstone if !bytes.is_empty() => {
            return Err(invalid("tombstone carries document bytes"));
        }
        DocumentKind::Tombstone => {}
    }
    Ok(())
}

/// Encode one source-owned logical change. Tombstones have no document bytes.
///
/// # Errors
/// Returns an error for an invalid event, mismatched document binding, or cap.
pub fn encode_change_into(
    output: &mut Vec<u8>,
    event: DocumentEventId,
    binding: DocumentBinding,
    kind: DocumentKind,
    document_bytes: &[u8],
) -> Result<(), FormatError> {
    event.validate()?;
    if binding.generation == 0 {
        return Err(invalid("change schema generation must be nonzero"));
    }
    validate_embedded_document(binding, kind, document_bytes)?;
    let total_len = CHANGE_HEADER_LEN
        .checked_add(document_bytes.len())
        .and_then(|length| length.checked_add(4))
        .ok_or_else(|| invalid("change length overflow"))?;
    begin_variable_record(output, CHANGE_MAGIC, total_len)?;
    output.extend_from_slice(&encode_event_id(event));
    output.extend_from_slice(&binding.collection.to_bytes());
    output.extend_from_slice(&binding.document.to_bytes());
    output.extend_from_slice(&binding.generation.to_le_bytes());
    output.extend_from_slice(&binding.schema_digest);
    output.push(kind as u8);
    output.extend_from_slice(
        &u32::try_from(document_bytes.len())
            .map_err(|_| invalid("change document length exceeds u32"))?
            .to_le_bytes(),
    );
    output.extend_from_slice(document_bytes);
    finish_variable_record(output, total_len);
    Ok(())
}

/// Decode one authoritative change and verify its embedded document binding.
///
/// # Errors
/// Returns an error for malformed, noncanonical, corrupt, or oversized bytes.
pub fn decode_change(bytes: &[u8]) -> Result<DocumentChangeView<'_>, FormatError> {
    decode_variable_record(
        bytes,
        CHANGE_MAGIC,
        "document change",
        CHANGE_HEADER_LEN + 4,
    )?;
    let document_len = usize::try_from(read_u32(bytes, 149)?)
        .map_err(|_| invalid("change document length exceeds platform"))?;
    if CHANGE_HEADER_LEN
        .checked_add(document_len)
        .and_then(|length| length.checked_add(4))
        != Some(bytes.len())
    {
        return Err(invalid("change document length"));
    }
    let binding = DocumentBinding {
        collection: CollectionId::from_bytes(bytes[76..92].try_into().unwrap()),
        document: DocumentId::from_bytes(bytes[92..108].try_into().unwrap()),
        generation: u64::from_le_bytes(bytes[108..116].try_into().unwrap()),
        schema_digest: bytes[116..148].try_into().unwrap(),
    };
    if binding.generation == 0 {
        return Err(invalid("change schema generation must be nonzero"));
    }
    let kind = DocumentKind::try_from(bytes[148])?;
    let document_bytes = &bytes[CHANGE_HEADER_LEN..bytes.len() - 4];
    validate_embedded_document(binding, kind, document_bytes)?;
    Ok(DocumentChangeView {
        event: decode_event_id(&bytes[12..76])?,
        binding,
        kind,
        document_bytes,
    })
}

fn validate_dot(dot: DocumentDot) -> Result<(), FormatError> {
    validate_origin(dot.origin, dot.sequence)
}

fn validate_sibling(
    collection: CollectionId,
    document: DocumentId,
    sibling: DocumentSibling<'_>,
) -> Result<(), FormatError> {
    sibling.event.validate()?;
    if sibling.generation == 0 {
        return Err(invalid("state sibling generation must be nonzero"));
    }
    validate_embedded_document(
        DocumentBinding {
            collection,
            document,
            generation: sibling.generation,
            schema_digest: sibling.schema_digest,
        },
        sibling.kind,
        sibling.document_bytes,
    )
}

/// Encode the bounded materialized state of one document.
///
/// Context and siblings must each be strictly sorted, with no duplicate dots
/// or event IDs. The absolute cap is 1,024 each; callers may impose lower
/// replication limits before calling.
///
/// # Errors
/// Returns an error for invalid ordering, sibling binding, or value size.
pub fn encode_state_into(
    output: &mut Vec<u8>,
    collection: CollectionId,
    document: DocumentId,
    context: &[DocumentDot],
    siblings: &[DocumentSibling<'_>],
) -> Result<(), FormatError> {
    if context.len() > DOCUMENT_MAX_CHILDREN
        || siblings.is_empty()
        || siblings.len() > DOCUMENT_MAX_CHILDREN
    {
        return Err(invalid("state causal or sibling count"));
    }
    let mut previous_dot = None;
    for &dot in context {
        validate_dot(dot)?;
        if previous_dot.is_some_and(|previous| previous >= dot) {
            return Err(invalid("state context is not strictly sorted"));
        }
        previous_dot = Some(dot);
    }
    let mut previous_event = None;
    let mut total_len = STATE_HEADER_LEN
        .checked_add(context.len() * DOT_LEN)
        .and_then(|length| length.checked_add(4))
        .ok_or_else(|| invalid("state length overflow"))?;
    for &sibling in siblings {
        validate_sibling(collection, document, sibling)?;
        if context
            .binary_search_by_key(&sibling.event.origin, |dot| dot.origin)
            .ok()
            .is_none_or(|index| context[index].sequence < sibling.event.sequence)
        {
            return Err(invalid("state sibling dot is not covered by context"));
        }
        if previous_event.is_some_and(|previous| previous >= sibling.event) {
            return Err(invalid("state siblings are not strictly sorted"));
        }
        previous_event = Some(sibling.event);
        total_len = total_len
            .checked_add(SIBLING_HEADER_LEN)
            .and_then(|length| length.checked_add(sibling.document_bytes.len()))
            .ok_or_else(|| invalid("state sibling length overflow"))?;
    }
    begin_variable_record(output, STATE_MAGIC, total_len)?;
    output.extend_from_slice(&collection.to_bytes());
    output.extend_from_slice(&document.to_bytes());
    output.extend_from_slice(
        &u16::try_from(context.len())
            .map_err(|_| invalid("state context count exceeds u16"))?
            .to_le_bytes(),
    );
    output.extend_from_slice(
        &u16::try_from(siblings.len())
            .map_err(|_| invalid("state sibling count exceeds u16"))?
            .to_le_bytes(),
    );
    for &dot in context {
        output.extend_from_slice(&dot.origin.replica_id.to_bytes());
        output.extend_from_slice(&dot.origin.incarnation);
        output.extend_from_slice(&dot.sequence.to_le_bytes());
    }
    for &sibling in siblings {
        output.extend_from_slice(&encode_event_id(sibling.event));
        output.extend_from_slice(&sibling.generation.to_le_bytes());
        output.extend_from_slice(&sibling.schema_digest);
        output.push(sibling.kind as u8);
        output.extend_from_slice(
            &u32::try_from(sibling.document_bytes.len())
                .map_err(|_| invalid("state document length exceeds u32"))?
                .to_le_bytes(),
        );
        output.extend_from_slice(sibling.document_bytes);
    }
    finish_variable_record(output, total_len);
    Ok(())
}

/// Decode a materialized state using caller-configured context/sibling caps.
///
/// # Errors
/// Returns an error for malformed, over-limit, noncanonical, or corrupt state.
#[allow(
    clippy::too_many_lines,
    reason = "validates one complete bounded state record before returning borrowed sibling bytes"
)]
pub fn decode_state_with_limits(
    bytes: &[u8],
    max_context: usize,
    max_siblings: usize,
) -> Result<DocumentStateView<'_>, FormatError> {
    decode_variable_record(bytes, STATE_MAGIC, "document state", STATE_HEADER_LEN + 4)?;
    let collection = CollectionId::from_bytes(bytes[12..28].try_into().unwrap());
    let document = DocumentId::from_bytes(bytes[28..44].try_into().unwrap());
    let context_count = usize::from(read_u16(bytes, 44)?);
    let sibling_count = usize::from(read_u16(bytes, 46)?);
    if context_count > max_context.min(DOCUMENT_MAX_CHILDREN)
        || sibling_count == 0
        || sibling_count > max_siblings.min(DOCUMENT_MAX_CHILDREN)
    {
        return Err(invalid("state causal or sibling count limit"));
    }
    let minimum_body = context_count
        .checked_mul(DOT_LEN)
        .and_then(|bytes| {
            sibling_count
                .checked_mul(SIBLING_HEADER_LEN)
                .and_then(|siblings| bytes.checked_add(siblings))
        })
        .ok_or_else(|| invalid("state count length overflow"))?;
    if minimum_body > bytes.len() - STATE_HEADER_LEN - 4 {
        return Err(invalid("state count exceeds available bytes"));
    }
    let mut context = Vec::new();
    context
        .try_reserve_exact(context_count)
        .map_err(|_| FormatError::Allocation)?;
    let mut siblings = Vec::new();
    siblings
        .try_reserve_exact(sibling_count)
        .map_err(|_| FormatError::Allocation)?;
    let mut position = STATE_HEADER_LEN;
    let payload_end = bytes.len() - 4;
    let mut previous_dot = None;
    for _ in 0..context_count {
        let end = position
            .checked_add(DOT_LEN)
            .filter(|&end| end <= payload_end)
            .ok_or_else(|| invalid("truncated state context"))?;
        let dot = DocumentDot {
            origin: OriginId {
                replica_id: ReplicaId::from_bytes(
                    bytes[position..position + 16].try_into().unwrap(),
                ),
                incarnation: bytes[position + 16..position + 32].try_into().unwrap(),
            },
            sequence: u64::from_le_bytes(bytes[position + 32..end].try_into().unwrap()),
        };
        validate_dot(dot)?;
        if previous_dot.is_some_and(|previous| previous >= dot) {
            return Err(invalid("state context is not strictly sorted"));
        }
        context.push(dot);
        previous_dot = Some(dot);
        position = end;
    }
    let mut previous_event = None;
    for _ in 0..sibling_count {
        let header_end = position
            .checked_add(SIBLING_HEADER_LEN)
            .filter(|&end| end <= payload_end)
            .ok_or_else(|| invalid("truncated state sibling"))?;
        let event = decode_event_id(&bytes[position..position + 64])?;
        let generation =
            u64::from_le_bytes(bytes[position + 64..position + 72].try_into().unwrap());
        let schema_digest = bytes[position + 72..position + 104].try_into().unwrap();
        let kind = DocumentKind::try_from(bytes[position + 104])?;
        let document_len = usize::try_from(read_u32(bytes, position + 105)?)
            .map_err(|_| invalid("state document length exceeds platform"))?;
        let end = header_end
            .checked_add(document_len)
            .filter(|&end| end <= payload_end)
            .ok_or_else(|| invalid("state document exceeds record"))?;
        let sibling = DocumentSibling {
            event,
            generation,
            schema_digest,
            kind,
            document_bytes: &bytes[header_end..end],
        };
        validate_sibling(collection, document, sibling)?;
        if context
            .binary_search_by_key(&event.origin, |dot| dot.origin)
            .ok()
            .is_none_or(|index| context[index].sequence < event.sequence)
        {
            return Err(invalid("state sibling dot is not covered by context"));
        }
        if previous_event.is_some_and(|previous| previous >= event) {
            return Err(invalid("state siblings are not strictly sorted"));
        }
        siblings.push(sibling);
        previous_event = Some(event);
        position = end;
    }
    if position != payload_end {
        return Err(invalid("state has trailing bytes"));
    }
    Ok(DocumentStateView {
        collection,
        document,
        context,
        siblings,
    })
}

/// Decode a materialized state with the absolute v1 hard caps.
///
/// # Errors
/// Returns an error for malformed, noncanonical, or corrupt state bytes.
pub fn decode_state(bytes: &[u8]) -> Result<DocumentStateView<'_>, FormatError> {
    decode_state_with_limits(bytes, DOCUMENT_MAX_CHILDREN, DOCUMENT_MAX_CHILDREN)
}

/// Encode a local active-generation pointer value.
///
/// # Errors
/// Returns an error for generation zero.
pub fn encode_active_value(schema: ActiveSchema) -> Result<[u8; ACTIVE_VALUE_LEN], FormatError> {
    if schema.generation == 0 {
        return Err(invalid("active schema generation must be nonzero"));
    }
    let mut body = [0_u8; 40];
    body[..8].copy_from_slice(&schema.generation.to_le_bytes());
    body[8..].copy_from_slice(&schema.digest);
    Ok(encode_fixed(ACTIVE_MAGIC, &body))
}

/// Decode a complete checksummed active-generation pointer value.
///
/// # Errors
/// Returns an error for malformed bytes, checksum failure, or generation zero.
pub fn decode_active_value(bytes: &[u8]) -> Result<ActiveSchema, FormatError> {
    let body = decode_fixed::<ACTIVE_VALUE_LEN>(bytes, ACTIVE_MAGIC, "active schema value")?;
    let schema = ActiveSchema {
        generation: u64::from_le_bytes(body[..8].try_into().unwrap()),
        digest: body[8..40].try_into().unwrap(),
    };
    if schema.generation == 0 {
        return Err(invalid("active schema generation must be nonzero"));
    }
    Ok(schema)
}

/// Encode a transaction retry mapping to its committed distributed event.
///
/// # Errors
/// Returns an error for a zero event sequence or origin incarnation.
pub fn encode_retry_value(id: DocumentEventId) -> Result<[u8; RETRY_VALUE_LEN], FormatError> {
    Ok(encode_fixed(RETRY_MAGIC, &encode_event_id(id.validate()?)))
}

/// Decode a complete checksummed transaction retry mapping.
///
/// # Errors
/// Returns an error for malformed bytes, checksum failure, or invalid event identity.
pub fn decode_retry_value(bytes: &[u8]) -> Result<DocumentEventId, FormatError> {
    let body = decode_fixed::<RETRY_VALUE_LEN>(bytes, RETRY_MAGIC, "retry map value")?;
    decode_event_id(body)
}

/// Encode the last committed per-origin event sequence.
///
/// # Errors
/// Returns an error for sequence zero.
pub fn encode_high_water_value(sequence: u64) -> Result<[u8; HIGH_WATER_VALUE_LEN], FormatError> {
    if sequence == 0 {
        return Err(invalid("origin high-water must be nonzero"));
    }
    Ok(encode_fixed(HIGH_WATER_MAGIC, &sequence.to_le_bytes()))
}

/// Decode a complete checksummed per-origin high-water value.
///
/// # Errors
/// Returns an error for malformed bytes, checksum failure, or sequence zero.
pub fn decode_high_water_value(bytes: &[u8]) -> Result<u64, FormatError> {
    let body = decode_fixed::<HIGH_WATER_VALUE_LEN>(bytes, HIGH_WATER_MAGIC, "high-water value")?;
    let sequence = u64::from_le_bytes(body.try_into().unwrap());
    if sequence == 0 {
        return Err(invalid("origin high-water must be nonzero"));
    }
    Ok(sequence)
}

/// Whether the first byte belongs to the exclusive document/catalog/index key range.
#[must_use]
pub fn is_document_reserved_key(key: &[u8]) -> bool {
    key.first().is_some_and(|tag| (0xC0..=0xEF).contains(tag))
}

/// Encode an immutable catalog-definition key.
///
/// # Errors
/// Returns an error for generation zero.
pub fn encode_catalog_key(
    collection: CollectionId,
    generation: u64,
) -> Result<[u8; CATALOG_KEY_LEN], FormatError> {
    if generation == 0 {
        return Err(invalid("catalog generation must be nonzero"));
    }
    let mut key = [0_u8; CATALOG_KEY_LEN];
    key[..2].copy_from_slice(&[CATALOG_TAG, VERSION]);
    key[2..18].copy_from_slice(&collection.to_bytes());
    key[18..].copy_from_slice(&generation.to_be_bytes());
    Ok(key)
}

/// Encode an active-generation catalog key.
#[must_use]
pub fn encode_active_key(collection: CollectionId) -> [u8; ACTIVE_KEY_LEN] {
    let mut key = [0_u8; ACTIVE_KEY_LEN];
    key[..2].copy_from_slice(&[ACTIVE_TAG, VERSION]);
    key[2..].copy_from_slice(&collection.to_bytes());
    key
}

/// Encode a materialized document-primary key.
#[must_use]
pub fn encode_primary_key(collection: CollectionId, document: DocumentId) -> [u8; PRIMARY_KEY_LEN] {
    let mut key = [0_u8; PRIMARY_KEY_LEN];
    key[..2].copy_from_slice(&[PRIMARY_TAG, VERSION]);
    key[2..18].copy_from_slice(&collection.to_bytes());
    key[18..].copy_from_slice(&document.to_bytes());
    key
}

/// Encode an authoritative source-change key.
///
/// # Errors
/// Returns an error for a zero sequence or origin incarnation.
pub fn encode_change_key(
    collection: CollectionId,
    document: DocumentId,
    origin: OriginId,
    sequence: u64,
    ordinal: u16,
) -> Result<[u8; CHANGE_KEY_LEN], FormatError> {
    validate_origin(origin, sequence)?;
    let mut key = [0_u8; CHANGE_KEY_LEN];
    key[..2].copy_from_slice(&[CHANGE_TAG, VERSION]);
    key[2..18].copy_from_slice(&collection.to_bytes());
    key[18..34].copy_from_slice(&document.to_bytes());
    key[34..50].copy_from_slice(&origin.replica_id.to_bytes());
    key[50..66].copy_from_slice(&origin.incarnation);
    key[66..74].copy_from_slice(&sequence.to_be_bytes());
    key[74..76].copy_from_slice(&ordinal.to_be_bytes());
    Ok(key)
}

/// Encode a committed-event marker key.
///
/// # Errors
/// Returns an error for a zero sequence or origin incarnation.
pub fn encode_marker_key(
    origin: OriginId,
    sequence: u64,
) -> Result<[u8; MARKER_KEY_LEN], FormatError> {
    validate_origin(origin, sequence)?;
    let mut key = [0_u8; MARKER_KEY_LEN];
    key[..2].copy_from_slice(&[MARKER_TAG, VERSION]);
    key[2..18].copy_from_slice(&origin.replica_id.to_bytes());
    key[18..34].copy_from_slice(&origin.incarnation);
    key[34..42].copy_from_slice(&sequence.to_be_bytes());
    Ok(key)
}

/// Encode a transaction retry-map key.
#[must_use]
pub fn encode_retry_key(transaction_id: TransactionId) -> [u8; RETRY_KEY_LEN] {
    let mut key = [0_u8; RETRY_KEY_LEN];
    key[..2].copy_from_slice(&[RETRY_TAG, VERSION]);
    key[2..].copy_from_slice(&transaction_id.to_bytes());
    key
}

/// Encode a per-origin durable high-water key.
///
/// # Errors
/// Returns an error for a zero origin incarnation.
pub fn encode_high_water_key(origin: OriginId) -> Result<[u8; HIGH_WATER_KEY_LEN], FormatError> {
    validate_incarnation(origin)?;
    let mut key = [0_u8; HIGH_WATER_KEY_LEN];
    key[..2].copy_from_slice(&[HIGH_WATER_TAG, VERSION]);
    key[2..18].copy_from_slice(&origin.replica_id.to_bytes());
    key[18..].copy_from_slice(&origin.incarnation);
    Ok(key)
}

/// Decode exactly one canonical document/catalog key.
///
/// # Errors
/// Returns an error for an unknown tag/version, wrong length, or invalid identity.
pub fn decode_document_key(key: &[u8]) -> Result<DocumentKey, FormatError> {
    if key.len() < 2 {
        return Err(invalid("key is shorter than tag and version"));
    }
    if key[1] != VERSION {
        return Err(invalid("unknown document key version"));
    }
    let expected = match key[0] {
        CATALOG_TAG => CATALOG_KEY_LEN,
        ACTIVE_TAG => ACTIVE_KEY_LEN,
        PRIMARY_TAG => PRIMARY_KEY_LEN,
        CHANGE_TAG => CHANGE_KEY_LEN,
        MARKER_TAG => MARKER_KEY_LEN,
        RETRY_TAG => RETRY_KEY_LEN,
        HIGH_WATER_TAG => HIGH_WATER_KEY_LEN,
        _ => return Err(invalid("unknown document key tag")),
    };
    if key.len() != expected {
        return Err(invalid("document key length"));
    }
    match key[0] {
        CATALOG_TAG => {
            let generation = u64::from_be_bytes(key[18..26].try_into().unwrap());
            if generation == 0 {
                return Err(invalid("catalog generation must be nonzero"));
            }
            Ok(DocumentKey::Catalog {
                collection: CollectionId::from_bytes(key[2..18].try_into().unwrap()),
                generation,
            })
        }
        ACTIVE_TAG => Ok(DocumentKey::Active {
            collection: CollectionId::from_bytes(key[2..18].try_into().unwrap()),
        }),
        PRIMARY_TAG => Ok(DocumentKey::Primary {
            collection: CollectionId::from_bytes(key[2..18].try_into().unwrap()),
            document: DocumentId::from_bytes(key[18..34].try_into().unwrap()),
        }),
        CHANGE_TAG => {
            let origin = OriginId {
                replica_id: ReplicaId::from_bytes(key[34..50].try_into().unwrap()),
                incarnation: key[50..66].try_into().unwrap(),
            };
            let sequence = u64::from_be_bytes(key[66..74].try_into().unwrap());
            validate_origin(origin, sequence)?;
            Ok(DocumentKey::Change {
                collection: CollectionId::from_bytes(key[2..18].try_into().unwrap()),
                document: DocumentId::from_bytes(key[18..34].try_into().unwrap()),
                origin,
                sequence,
                ordinal: u16::from_be_bytes(key[74..76].try_into().unwrap()),
            })
        }
        MARKER_TAG => {
            let origin = OriginId {
                replica_id: ReplicaId::from_bytes(key[2..18].try_into().unwrap()),
                incarnation: key[18..34].try_into().unwrap(),
            };
            let sequence = u64::from_be_bytes(key[34..42].try_into().unwrap());
            validate_origin(origin, sequence)?;
            Ok(DocumentKey::Marker { origin, sequence })
        }
        RETRY_TAG => Ok(DocumentKey::Retry {
            transaction_id: TransactionId::from_bytes(key[2..26].try_into().unwrap()),
        }),
        HIGH_WATER_TAG => {
            let origin = OriginId {
                replica_id: ReplicaId::from_bytes(key[2..18].try_into().unwrap()),
                incarnation: key[18..34].try_into().unwrap(),
            };
            validate_incarnation(origin)?;
            Ok(DocumentKey::HighWater { origin })
        }
        _ => unreachable!("tag was validated above"),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn encoded_container(map: bool, children: &[(Option<u32>, FieldType, &[u8])]) -> Vec<u8> {
        let mut bytes = Vec::new();
        bytes.extend_from_slice(&u16::try_from(children.len()).unwrap().to_le_bytes());
        for (field_id, value_type, value) in children {
            if map {
                bytes.extend_from_slice(&field_id.unwrap().to_le_bytes());
            }
            bytes.push(*value_type as u8);
            bytes.extend_from_slice(&u32::try_from(value.len()).unwrap().to_le_bytes());
            bytes.extend_from_slice(value);
        }
        bytes
    }

    fn ids() -> (CollectionId, DocumentId, OriginId) {
        (
            CollectionId::from_bytes([1; 16]),
            DocumentId::from_bytes([2; 16]),
            OriginId {
                replica_id: ReplicaId::from_bytes([3; 16]),
                incarnation: [4; 16],
            },
        )
    }

    #[test]
    fn fixed_keys_roundtrip_and_preserve_sequence_order() {
        let (collection, document, origin) = ids();
        let catalog = encode_catalog_key(collection, 7).unwrap();
        let active = encode_active_key(collection);
        let primary = encode_primary_key(collection, document);
        let change = encode_change_key(collection, document, origin, 7, 2).unwrap();
        let marker = encode_marker_key(origin, 7).unwrap();
        let retry = encode_retry_key(TransactionId::new(99, [5; 16]));
        let high_water = encode_high_water_key(origin).unwrap();
        for key in [
            catalog.as_slice(),
            active.as_slice(),
            primary.as_slice(),
            change.as_slice(),
            marker.as_slice(),
            retry.as_slice(),
            high_water.as_slice(),
        ] {
            assert!(is_document_reserved_key(key));
            assert!(decode_document_key(key).is_ok());
        }
        assert!(
            encode_change_key(collection, document, origin, 7, 2).unwrap()
                < encode_change_key(collection, document, origin, 8, 0).unwrap()
        );
        assert!(encode_marker_key(origin, 7).unwrap() < encode_marker_key(origin, 8).unwrap());
        assert!(
            encode_catalog_key(collection, 7).unwrap() < encode_catalog_key(collection, 8).unwrap()
        );
    }

    #[test]
    fn fixed_keys_reject_noncanonical_identity_and_length() {
        let (collection, document, origin) = ids();
        assert!(encode_catalog_key(collection, 0).is_err());
        assert!(encode_marker_key(origin, 0).is_err());
        let mut short = encode_primary_key(collection, document).to_vec();
        short.pop();
        assert!(decode_document_key(&short).is_err());
        let mut unknown = encode_primary_key(collection, document);
        unknown[1] = 2;
        assert!(decode_document_key(&unknown).is_err());
        assert!(!is_document_reserved_key(b"raw"));
    }

    #[test]
    fn fixed_values_roundtrip_and_reject_corruption() {
        let (_, _, origin) = ids();
        let schema = ActiveSchema {
            generation: 7,
            digest: [9; 32],
        };
        assert_eq!(
            decode_active_value(&encode_active_value(schema).unwrap()).unwrap(),
            schema
        );
        let event = DocumentEventId {
            database: DatabaseId::from_bytes([6; 16]),
            shard: ShardId::new(3),
            origin,
            sequence: 7,
        };
        let retry = encode_retry_value(event).unwrap();
        assert_eq!(decode_retry_value(&retry).unwrap(), event);
        assert_eq!(
            decode_high_water_value(&encode_high_water_value(7).unwrap()).unwrap(),
            7
        );
        let mut corrupt = retry;
        corrupt[24] ^= 1;
        assert!(matches!(
            decode_retry_value(&corrupt),
            Err(FormatError::ChecksumMismatch { .. })
        ));
        assert!(decode_retry_value(&retry[..retry.len() - 1]).is_err());
        assert!(
            encode_retry_value(DocumentEventId {
                sequence: 0,
                ..event
            })
            .is_err()
        );
    }

    #[test]
    fn catalog_and_schema_bound_document_roundtrip() {
        let (collection, document, _) = ids();
        let fields = [
            SchemaField {
                field_id: 1,
                parent_field_id: 0,
                value_type: FieldType::Utf8,
                element_type: None,
                flags: 1,
                name: "name",
            },
            SchemaField {
                field_id: 2,
                parent_field_id: 0,
                value_type: FieldType::Map,
                element_type: None,
                flags: 0,
                name: "address",
            },
            SchemaField {
                field_id: 3,
                parent_field_id: 2,
                value_type: FieldType::I64,
                element_type: None,
                flags: 1,
                name: "number",
            },
        ];
        let mut schema_bytes = Vec::new();
        encode_schema_into(&mut schema_bytes, &fields).unwrap();
        let mut catalog_bytes = Vec::new();
        let digest =
            encode_catalog_into(&mut catalog_bytes, collection, 7, 6, &schema_bytes).unwrap();
        let catalog = decode_catalog(&catalog_bytes).unwrap();
        assert_eq!(catalog.digest, digest);
        assert_eq!(catalog.schema.fields(), fields);
        let binding = DocumentBinding {
            collection,
            document,
            generation: 7,
            schema_digest: digest,
        };
        let mut nested = Vec::new();
        nested.extend_from_slice(&1_u16.to_le_bytes());
        nested.extend_from_slice(&3_u32.to_le_bytes());
        nested.push(FieldType::I64 as u8);
        nested.extend_from_slice(&8_u32.to_le_bytes());
        nested.extend_from_slice(&42_i64.to_le_bytes());
        let mut document_bytes = Vec::new();
        encode_document_into(
            &mut document_bytes,
            binding,
            &[
                DocumentFieldInput {
                    field_id: 1,
                    value_type: FieldType::Utf8,
                    value: b"Ada",
                },
                DocumentFieldInput {
                    field_id: 2,
                    value_type: FieldType::Map,
                    value: &nested,
                },
            ],
        )
        .unwrap();
        let view = decode_document(&document_bytes).unwrap();
        validate_document_schema(view, &catalog.schema).unwrap();
        assert_eq!(view.field(1).unwrap().as_str(), Some("Ada"));
        let mut bad_catalog = catalog_bytes.clone();
        bad_catalog[44] ^= 1;
        let end = bad_catalog.len();
        let checksum = crc32c::crc32c(&bad_catalog[..end - 4]);
        bad_catalog[end - 4..].copy_from_slice(&checksum.to_le_bytes());
        assert!(decode_catalog(&bad_catalog).is_err());
        let empty_map = 0_u16.to_le_bytes();
        encode_document_into(
            &mut document_bytes,
            binding,
            &[
                DocumentFieldInput {
                    field_id: 1,
                    value_type: FieldType::Utf8,
                    value: b"Ada",
                },
                DocumentFieldInput {
                    field_id: 2,
                    value_type: FieldType::Map,
                    value: &empty_map,
                },
            ],
        )
        .unwrap();
        assert!(
            validate_document_schema(decode_document(&document_bytes).unwrap(), &catalog.schema)
                .is_err()
        );
    }

    #[test]
    fn document_and_nested_views_iterate_in_canonical_order_without_allocating() {
        let (collection, document, _) = ids();
        let binding = DocumentBinding {
            collection,
            document,
            generation: 7,
            schema_digest: [8; 32],
        };
        let negative = (-42_i64).to_le_bytes();
        let map = encoded_container(
            true,
            &[
                (Some(2), FieldType::I64, &negative),
                (Some(9), FieldType::Utf8, b"nested"),
            ],
        );
        let array = encoded_container(
            false,
            &[
                (None, FieldType::Bool, &[1]),
                (None, FieldType::Utf8, b"second"),
            ],
        );
        let top_value = (123_i64).to_le_bytes();
        let mut document_bytes = Vec::new();
        encode_document_into(
            &mut document_bytes,
            binding,
            &[
                DocumentFieldInput {
                    field_id: 1,
                    value_type: FieldType::Map,
                    value: &map,
                },
                DocumentFieldInput {
                    field_id: 7,
                    value_type: FieldType::Array,
                    value: &array,
                },
                DocumentFieldInput {
                    field_id: 12,
                    value_type: FieldType::I64,
                    value: &top_value,
                },
            ],
        )
        .unwrap();

        let view = decode_document(&document_bytes).unwrap();
        let mut fields = view.fields();
        assert_eq!(fields.len(), 3);

        let (field_id, map_value) = fields.next().unwrap();
        assert_eq!(field_id, 1);
        assert_eq!(map_value.value_type(), FieldType::Map);
        assert!(map_value.array_items().is_none());
        let mut map_fields = map_value.map_fields().unwrap();
        assert_eq!(map_fields.len(), 2);
        let (nested_id, nested_value) = map_fields.next().unwrap();
        assert_eq!(nested_id, 2);
        assert_eq!(nested_value.as_i64(), Some(-42));
        let (nested_id, nested_value) = map_fields.next().unwrap();
        assert_eq!(nested_id, 9);
        assert_eq!(nested_value.as_str(), Some("nested"));
        assert_eq!(map_fields.len(), 0);
        assert!(map_fields.next().is_none());

        let (field_id, array_value) = fields.next().unwrap();
        assert_eq!(field_id, 7);
        assert!(array_value.map_fields().is_none());
        let mut array_items = array_value.array_items().unwrap();
        assert_eq!(array_items.len(), 2);
        let first = array_items.next().unwrap();
        assert_eq!(first.value_type(), FieldType::Bool);
        assert_eq!(first.encoded_bytes(), &[1]);
        let second = array_items.next().unwrap();
        assert_eq!(second.as_str(), Some("second"));
        assert_eq!(array_items.len(), 0);
        assert!(array_items.next().is_none());

        let (field_id, top_value) = fields.next().unwrap();
        assert_eq!(field_id, 12);
        assert_eq!(top_value.as_i64(), Some(123));
        assert_eq!(fields.len(), 0);
        assert!(fields.next().is_none());
    }

    #[test]
    fn document_iteration_is_unavailable_until_complete_validation_succeeds() {
        let (collection, document, _) = ids();
        let binding = DocumentBinding {
            collection,
            document,
            generation: 7,
            schema_digest: [8; 32],
        };
        let map = encoded_container(
            true,
            &[
                (Some(1), FieldType::Null, &[]),
                (Some(3), FieldType::Null, &[]),
            ],
        );
        let mut document_bytes = Vec::new();
        encode_document_into(
            &mut document_bytes,
            binding,
            &[DocumentFieldInput {
                field_id: 1,
                value_type: FieldType::Map,
                value: &map,
            }],
        )
        .unwrap();

        let second_map_field_id = DOCUMENT_HEADER_LEN + DOCUMENT_ENTRY_LEN + 2 + 9;
        document_bytes[second_map_field_id..second_map_field_id + 4]
            .copy_from_slice(&1_u32.to_le_bytes());
        let checksum_offset = document_bytes.len() - 4;
        let checksum = crc32c::crc32c(&document_bytes[..checksum_offset]);
        document_bytes[checksum_offset..].copy_from_slice(&checksum.to_le_bytes());
        assert!(decode_document(&document_bytes).is_err());

        let over_limit = vec![0; DOCUMENT_MAX_BYTES + 1];
        assert!(matches!(
            decode_document(&over_limit),
            Err(FormatError::LimitExceeded {
                what: "document value bytes",
                ..
            })
        ));
    }

    #[test]
    fn change_and_state_preserve_live_and_tombstone_siblings() {
        let (collection, document, origin) = ids();
        let event = DocumentEventId {
            database: DatabaseId::from_bytes([6; 16]),
            shard: ShardId::new(9),
            origin,
            sequence: 1,
        };
        let binding = DocumentBinding {
            collection,
            document,
            generation: 7,
            schema_digest: [8; 32],
        };
        let mut document_bytes = Vec::new();
        encode_document_into(&mut document_bytes, binding, &[]).unwrap();
        let mut change = Vec::new();
        encode_change_into(
            &mut change,
            event,
            binding,
            DocumentKind::Live,
            &document_bytes,
        )
        .unwrap();
        assert_eq!(
            decode_change(&change).unwrap().document_bytes,
            document_bytes
        );
        let context = [DocumentDot {
            origin,
            sequence: 2,
        }];
        let siblings = [
            DocumentSibling {
                event,
                generation: 7,
                schema_digest: [8; 32],
                kind: DocumentKind::Live,
                document_bytes: &document_bytes,
            },
            DocumentSibling {
                event: DocumentEventId {
                    sequence: 2,
                    ..event
                },
                generation: 7,
                schema_digest: [8; 32],
                kind: DocumentKind::Tombstone,
                document_bytes: &[],
            },
        ];
        let mut state = Vec::new();
        encode_state_into(&mut state, collection, document, &context, &siblings).unwrap();
        let decoded = decode_state_with_limits(&state, 1, 2).unwrap();
        assert_eq!(decoded.context, context);
        assert_eq!(decoded.siblings.len(), 2);
        assert_eq!(decoded.siblings[1].kind, DocumentKind::Tombstone);
        assert!(decode_state_with_limits(&state, 1, 1).is_err());
        assert!(encode_state_into(&mut state, collection, document, &[], &siblings).is_err());
        assert!(encode_state_into(&mut state, collection, document, &context, &[]).is_err());
        assert!(
            encode_state_into(
                &mut state,
                collection,
                document,
                &[],
                &[siblings[1], siblings[0]]
            )
            .is_err()
        );
    }

    #[test]
    fn state_decoder_rejects_uncovered_sibling_and_impossible_counts() {
        let (collection, document, origin) = ids();
        let event = DocumentEventId {
            database: DatabaseId::from_bytes([6; 16]),
            shard: ShardId::new(9),
            origin,
            sequence: 2,
        };
        let sibling = DocumentSibling {
            event,
            generation: 7,
            schema_digest: [8; 32],
            kind: DocumentKind::Tombstone,
            document_bytes: &[],
        };
        let mut state = Vec::new();
        encode_state_into(
            &mut state,
            collection,
            document,
            &[DocumentDot {
                origin,
                sequence: 2,
            }],
            &[sibling],
        )
        .unwrap();
        state[80..88].copy_from_slice(&1_u64.to_le_bytes());
        let end = state.len();
        let checksum = crc32c::crc32c(&state[..end - 4]);
        state[end - 4..].copy_from_slice(&checksum.to_le_bytes());
        assert!(decode_state(&state).is_err());
        state[44..46].copy_from_slice(&1_024_u16.to_le_bytes());
        let checksum = crc32c::crc32c(&state[..end - 4]);
        state[end - 4..].copy_from_slice(&checksum.to_le_bytes());
        assert!(decode_state(&state).is_err());
    }

    #[test]
    fn schema_decoder_rejects_impossible_declared_field_count() {
        let mut schema = Vec::new();
        encode_schema_into(&mut schema, &[]).unwrap();
        schema[12..14].copy_from_slice(&1_024_u16.to_le_bytes());
        let end = schema.len();
        let checksum = crc32c::crc32c(&schema[..end - 4]);
        schema[end - 4..].copy_from_slice(&checksum.to_le_bytes());
        assert!(decode_schema(&schema).is_err());
    }
}
