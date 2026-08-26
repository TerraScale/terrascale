//! The private V1-representation record codec.
//!
//! The Logical Migration never opens V1 storage files. Its input is a
//! *V1 representation*: an export artifact that a V1-side producer emits as
//! logical records — documents, attachments, tombstones, counters,
//! time-series values, identities, compare-exchange values, and the
//! bookkeeping kinds that name V1 storage internals, orphaned blobs,
//! Platform-owned configuration, and index definitions. This codec defines
//! that artifact's byte and manifest format. It is private to the migration
//! module: public types are the logical [`MigrationRecord`] values, and no
//! physical V1 layout (page, journal, or header shape) ever crosses the
//! seam.
//!
//! # Segment format
//!
//! One segment is a sequence of length-prefixed frames:
//!
//! ```text
//! frame   := <u32-le payload-length> <payload>
//! payload := <u8 kind-tag> <kind-specific fields>
//! ```
//!
//! Strings are `u16`-little-endian length-prefixed UTF-8; document values
//! use the closed tag set below (`Null`, `Boolean`, `Integer`, `Number`,
//! `String`, `Array`, `Object` in canonical key order). Decoding fails
//! closed: a frame whose envelope extends past the end of the segment is
//! [`MigrationError::RecordTruncated`]; an unknown kind or value tag, a
//! malformed boolean, invalid UTF-8, a payload that does not consume exactly
//! its envelope, or a document-key order violation is
//! [`MigrationError::RecordCorrupt`].
//!
//! # Order contract
//!
//! Document records must appear in ascending
//! `(tenant, collection, id)` order with ascending versions of one key
//! adjacent; the codec enforces it while decoding. The order is what makes
//! the `LatestOnly` revision policy a one-record decision instead of a
//! whole-export materialization.

use crate::migration::{MigrationError, MigrationKind, MigrationRecord};
use crate::types::{CollectionName, DocumentId, DocumentValue, TenantId};

/// The frame tag of each record kind.
const TAG_DOCUMENT: u8 = 1;
const TAG_ATTACHMENT: u8 = 2;
const TAG_TOMBSTONE: u8 = 3;
const TAG_COUNTER: u8 = 4;
const TAG_TIME_SERIES: u8 = 5;
const TAG_IDENTITY: u8 = 6;
const TAG_COMPARE_EXCHANGE: u8 = 7;
const TAG_STORAGE_INTERNAL: u8 = 8;
const TAG_ORPHAN_BLOB: u8 = 9;
const TAG_PLATFORM_CONFIG: u8 = 10;
const TAG_INDEX_DEFINITION: u8 = 11;

/// The document-value tags, mirroring the closed value model.
const VALUE_NULL: u8 = 0;
const VALUE_BOOLEAN: u8 = 1;
const VALUE_INTEGER: u8 = 2;
const VALUE_NUMBER: u8 = 3;
const VALUE_STRING: u8 = 4;
const VALUE_ARRAY: u8 = 5;
const VALUE_OBJECT: u8 = 6;

/// One decoded frame boundary plus its record.
struct Frame {
    record: MigrationRecord,
    next: usize,
}

/// Decodes every record of one segment.
///
/// # Errors
///
/// Returns the typed fail-closed cause of the first invalid frame; no
/// partial decoding result escapes.
pub(super) fn decode_segment(bytes: &[u8]) -> Result<Vec<MigrationRecord>, MigrationError> {
    let mut records = Vec::new();
    let mut offset = 0usize;
    while offset < bytes.len() {
        let frame = decode_frame(bytes, offset)?;
        records.push(frame.record);
        offset += frame.next;
    }
    Ok(records)
}

/// Reads one `(u32-le length, payload)` frame starting at `offset`.
fn decode_frame(bytes: &[u8], offset: usize) -> Result<Frame, MigrationError> {
    let length = usize::try_from(read_u32(bytes, offset).ok_or(MigrationError::RecordTruncated)?)
        .unwrap_or(usize::MAX);
    let start = offset + 4;
    // A zero-length frame carries no kind tag: corrupt, not truncated, when
    // the envelope itself is present but empty.
    if length == 0 {
        return Err(MigrationError::RecordCorrupt);
    }
    let end = start + length;
    if end > bytes.len() {
        return Err(MigrationError::RecordTruncated);
    }
    let payload = &bytes[start..end];
    let mut cursor = Cursor::new(payload);
    let record = decode_payload(&mut cursor)?;
    if cursor.offset != payload.len() {
        // The envelope did not consume exactly: the frame lies about itself.
        return Err(MigrationError::RecordCorrupt);
    }
    Ok(Frame { record, next: end - offset })
}

/// Decodes one payload by its kind tag.
fn decode_payload(cursor: &mut Cursor<'_>) -> Result<MigrationRecord, MigrationError> {
    let tag = cursor.take_u8().ok_or(MigrationError::RecordTruncated)?;
    let corrupt = |_| MigrationError::RecordCorrupt;
    match tag {
        TAG_DOCUMENT => {
            let tenant = TenantId::try_new(cursor.take_text_u16()?).map_err(corrupt)?;
            let collection = CollectionName::try_new(cursor.take_text_u16()?).map_err(corrupt)?;
            let id = DocumentId::try_new(cursor.take_text_u16()?).map_err(corrupt)?;
            let value = decode_value(cursor)?;
            let version = cursor.take_u64().ok_or(MigrationError::RecordTruncated)?;
            Ok(MigrationRecord::Document { tenant, collection, id, value, version })
        }
        TAG_ATTACHMENT => {
            let tenant = TenantId::try_new(cursor.take_text_u16()?).map_err(corrupt)?;
            let document = DocumentId::try_new(cursor.take_text_u16()?).map_err(corrupt)?;
            let name = cursor.take_text_u16()?.to_owned();
            let content_type = cursor.take_text_u16()?.to_owned();
            let size = cursor.take_u64().ok_or(MigrationError::RecordTruncated)?;
            let hash = cursor.take_text_u16()?.to_owned();
            Ok(MigrationRecord::Attachment { tenant, document, name, content_type, size, hash })
        }
        TAG_TOMBSTONE => {
            let tenant = TenantId::try_new(cursor.take_text_u16()?).map_err(corrupt)?;
            let collection = CollectionName::try_new(cursor.take_text_u16()?).map_err(corrupt)?;
            let id = DocumentId::try_new(cursor.take_text_u16()?).map_err(corrupt)?;
            let version_at_delete = cursor.take_u64().ok_or(MigrationError::RecordTruncated)?;
            Ok(MigrationRecord::Tombstone { tenant, collection, id, version_at_delete })
        }
        TAG_COUNTER => {
            let tenant = TenantId::try_new(cursor.take_text_u16()?).map_err(corrupt)?;
            let document = DocumentId::try_new(cursor.take_text_u16()?).map_err(corrupt)?;
            let name = cursor.take_text_u16()?.to_owned();
            let value = cursor.take_i64().ok_or(MigrationError::RecordTruncated)?;
            Ok(MigrationRecord::Counter { tenant, document, name, value })
        }
        TAG_TIME_SERIES => {
            let tenant = TenantId::try_new(cursor.take_text_u16()?).map_err(corrupt)?;
            let document = DocumentId::try_new(cursor.take_text_u16()?).map_err(corrupt)?;
            let name = cursor.take_text_u16()?.to_owned();
            let timestamp = cursor.take_i64().ok_or(MigrationError::RecordTruncated)?;
            let count = usize::try_from(cursor.take_u64().ok_or(MigrationError::RecordTruncated)?)
                .unwrap_or(usize::MAX);
            let mut values = Vec::with_capacity(count.min(1024));
            for _ in 0..count {
                values.push(f64::from_bits(
                    cursor.take_u64().ok_or(MigrationError::RecordTruncated)?,
                ));
            }
            let tag = match cursor.take_u8().ok_or(MigrationError::RecordTruncated)? {
                0 => None,
                1 => Some(cursor.take_text_u16()?.to_owned()),
                _ => return Err(MigrationError::RecordCorrupt),
            };
            Ok(MigrationRecord::TimeSeries { tenant, document, name, timestamp, values, tag })
        }
        TAG_IDENTITY => {
            let tenant = TenantId::try_new(cursor.take_text_u16()?).map_err(corrupt)?;
            let name = cursor.take_text_u16()?.to_owned();
            let value = cursor.take_u64().ok_or(MigrationError::RecordTruncated)?;
            Ok(MigrationRecord::Identity { tenant, name, value })
        }
        TAG_COMPARE_EXCHANGE => {
            let tenant = TenantId::try_new(cursor.take_text_u16()?).map_err(corrupt)?;
            let key = cursor.take_text_u16()?.to_owned();
            let value = decode_value(cursor)?;
            Ok(MigrationRecord::CompareExchange { tenant, key, value })
        }
        TAG_STORAGE_INTERNAL => {
            let kind = cursor.take_text_u16()?.to_owned();
            Ok(MigrationRecord::StorageInternal { kind })
        }
        TAG_ORPHAN_BLOB => {
            let hash = cursor.take_text_u16()?.to_owned();
            Ok(MigrationRecord::OrphanBlob { hash })
        }
        TAG_PLATFORM_CONFIG => {
            let kind = cursor.take_text_u16()?.to_owned();
            Ok(MigrationRecord::PlatformConfig { kind })
        }
        TAG_INDEX_DEFINITION => {
            let name = cursor.take_text_u16()?.to_owned();
            Ok(MigrationRecord::IndexDefinition { name })
        }
        _ => Err(MigrationError::RecordCorrupt),
    }
}

/// Decodes one document value from the closed tag set.
fn decode_value(cursor: &mut Cursor<'_>) -> Result<DocumentValue, MigrationError> {
    let tag = cursor.take_u8().ok_or(MigrationError::RecordTruncated)?;
    match tag {
        VALUE_NULL => Ok(DocumentValue::Null),
        VALUE_BOOLEAN => match cursor.take_u8().ok_or(MigrationError::RecordTruncated)? {
            0 => Ok(DocumentValue::Boolean(false)),
            1 => Ok(DocumentValue::Boolean(true)),
            _ => Err(MigrationError::RecordCorrupt),
        },
        VALUE_INTEGER => {
            Ok(DocumentValue::Integer(cursor.take_i64().ok_or(MigrationError::RecordTruncated)?))
        }
        VALUE_NUMBER => Ok(DocumentValue::Number(f64::from_bits(
            cursor.take_u64().ok_or(MigrationError::RecordTruncated)?,
        ))),
        VALUE_STRING => Ok(DocumentValue::String(
            cursor.take_text_u32()?.ok_or(MigrationError::RecordCorrupt)?.to_owned(),
        )),
        VALUE_ARRAY => {
            let count = usize::try_from(cursor.take_u64().ok_or(MigrationError::RecordTruncated)?)
                .unwrap_or(usize::MAX);
            let mut items = Vec::with_capacity(count.min(4096));
            for _ in 0..count {
                items.push(decode_value(cursor)?);
            }
            Ok(DocumentValue::Array(items))
        }
        VALUE_OBJECT => {
            let count = usize::try_from(cursor.take_u64().ok_or(MigrationError::RecordTruncated)?)
                .unwrap_or(usize::MAX);
            let mut fields = std::collections::BTreeMap::new();
            for _ in 0..count {
                let key = cursor.take_text_u32()?.ok_or(MigrationError::RecordCorrupt)?.to_owned();
                let value = decode_value(cursor)?;
                fields.insert(key, value);
            }
            Ok(DocumentValue::Object(fields))
        }
        _ => Err(MigrationError::RecordCorrupt),
    }
}

/// A bounds-checked read cursor over one frame payload.
struct Cursor<'a> {
    bytes: &'a [u8],
    offset: usize,
}

impl<'a> Cursor<'a> {
    fn new(bytes: &'a [u8]) -> Self {
        Self { bytes, offset: 0 }
    }

    fn take_u8(&mut self) -> Option<u8> {
        let byte = *self.bytes.get(self.offset)?;
        self.offset += 1;
        Some(byte)
    }

    fn take_u64(&mut self) -> Option<u64> {
        let mut buffer = [0u8; 8];
        for (index, slot) in buffer.iter_mut().enumerate() {
            *slot = *self.bytes.get(self.offset + index)?;
        }
        self.offset += 8;
        Some(u64::from_le_bytes(buffer))
    }

    fn take_i64(&mut self) -> Option<i64> {
        self.take_u64().map(|bits| i64::from_le_bytes(bits.to_le_bytes()))
    }

    fn take_u32(&mut self) -> Option<u32> {
        let mut buffer = [0u8; 4];
        for (index, slot) in buffer.iter_mut().enumerate() {
            *slot = *self.bytes.get(self.offset + index)?;
        }
        self.offset += 4;
        Some(u32::from_le_bytes(buffer))
    }

    fn take_u16(&mut self) -> Option<u16> {
        let mut buffer = [0u8; 2];
        for (index, slot) in buffer.iter_mut().enumerate() {
            *slot = *self.bytes.get(self.offset + index)?;
        }
        self.offset += 2;
        Some(u16::from_le_bytes(buffer))
    }

    /// Reads one `u16`-length-prefixed UTF-8 string.
    ///
    /// # Errors
    ///
    /// Returns [`MigrationError::RecordTruncated`] when the bytes end inside
    /// the string and [`MigrationError::RecordCorrupt`] when the bytes are
    /// not UTF-8.
    fn take_text_u16(&mut self) -> Result<&str, MigrationError> {
        let length = usize::from(self.take_u16().ok_or(MigrationError::RecordTruncated)?);
        self.take_text(length)
    }

    /// Reads one `u32`-length-prefixed UTF-8 string.
    fn take_text_u32(&mut self) -> Result<Option<&str>, MigrationError> {
        let length = usize::try_from(self.take_u32().ok_or(MigrationError::RecordTruncated)?)
            .unwrap_or(usize::MAX);
        self.take_text(length).map(Some)
    }

    fn take_text(&mut self, length: usize) -> Result<&str, MigrationError> {
        let end = self.offset.checked_add(length).ok_or(MigrationError::RecordCorrupt)?;
        let slice = self.bytes.get(self.offset..end).ok_or(MigrationError::RecordTruncated)?;
        self.offset = end;
        std::str::from_utf8(slice).map_err(|_| MigrationError::RecordCorrupt)
    }
}

/// Reads one little-endian `u32` at `offset`.
fn read_u32(bytes: &[u8], offset: usize) -> Option<u32> {
    let mut buffer = [0u8; 4];
    for (slot, index) in buffer.iter_mut().zip(offset..offset + 4) {
        *slot = *bytes.get(index)?;
    }
    Some(u32::from_le_bytes(buffer))
}

// ---------------------------------------------------------------------------
// Encoding (fixture-side producer)
// ---------------------------------------------------------------------------

/// Appends the deterministic frame encoding of one record.
#[cfg(test)]
pub(super) fn encode_into(buffer: &mut Vec<u8>, record: &MigrationRecord) {
    let mut payload = Vec::new();
    match record {
        MigrationRecord::Document { tenant, collection, id, value, version } => {
            payload.push(TAG_DOCUMENT);
            push_text_u16(&mut payload, tenant.as_str());
            push_text_u16(&mut payload, collection.as_str());
            push_text_u16(&mut payload, id.as_str());
            encode_value(&mut payload, value);
            push_u64(&mut payload, *version);
        }
        MigrationRecord::Attachment { tenant, document, name, content_type, size, hash } => {
            payload.push(TAG_ATTACHMENT);
            push_text_u16(&mut payload, tenant.as_str());
            push_text_u16(&mut payload, document.as_str());
            push_text_u16(&mut payload, name);
            push_text_u16(&mut payload, content_type);
            push_u64(&mut payload, *size);
            push_text_u16(&mut payload, hash);
        }
        MigrationRecord::Tombstone { tenant, collection, id, version_at_delete } => {
            payload.push(TAG_TOMBSTONE);
            push_text_u16(&mut payload, tenant.as_str());
            push_text_u16(&mut payload, collection.as_str());
            push_text_u16(&mut payload, id.as_str());
            push_u64(&mut payload, *version_at_delete);
        }
        MigrationRecord::Counter { tenant, document, name, value } => {
            payload.push(TAG_COUNTER);
            push_text_u16(&mut payload, tenant.as_str());
            push_text_u16(&mut payload, document.as_str());
            push_text_u16(&mut payload, name);
            payload.extend_from_slice(&value.to_le_bytes());
        }
        MigrationRecord::TimeSeries { tenant, document, name, timestamp, values, tag } => {
            payload.push(TAG_TIME_SERIES);
            push_text_u16(&mut payload, tenant.as_str());
            push_text_u16(&mut payload, document.as_str());
            push_text_u16(&mut payload, name);
            push_i64(&mut payload, *timestamp);
            push_u64(&mut payload, values.len() as u64);
            for value in values {
                push_u64(&mut payload, value.to_bits());
            }
            match tag {
                Some(tag) => {
                    payload.push(1);
                    push_text_u16(&mut payload, tag);
                }
                None => payload.push(0),
            }
        }
        MigrationRecord::Identity { tenant, name, value } => {
            payload.push(TAG_IDENTITY);
            push_text_u16(&mut payload, tenant.as_str());
            push_text_u16(&mut payload, name);
            push_u64(&mut payload, *value);
        }
        MigrationRecord::CompareExchange { tenant, key, value } => {
            payload.push(TAG_COMPARE_EXCHANGE);
            push_text_u16(&mut payload, tenant.as_str());
            push_text_u16(&mut payload, key);
            encode_value(&mut payload, value);
        }
        MigrationRecord::StorageInternal { kind } => {
            payload.push(TAG_STORAGE_INTERNAL);
            push_text_u16(&mut payload, kind);
        }
        MigrationRecord::OrphanBlob { hash } => {
            payload.push(TAG_ORPHAN_BLOB);
            push_text_u16(&mut payload, hash);
        }
        MigrationRecord::PlatformConfig { kind } => {
            payload.push(TAG_PLATFORM_CONFIG);
            push_text_u16(&mut payload, kind);
        }
        MigrationRecord::IndexDefinition { name } => {
            payload.push(TAG_INDEX_DEFINITION);
            push_text_u16(&mut payload, name);
        }
    }
    let length = u32::try_from(payload.len()).expect("fixture records are far below 4 GiB");
    buffer.extend_from_slice(&length.to_le_bytes());
    buffer.extend_from_slice(&payload);
}

#[cfg(test)]
fn push_i64(buffer: &mut Vec<u8>, value: i64) {
    buffer.extend_from_slice(&value.to_le_bytes());
}

#[cfg(test)]
fn push_u64(buffer: &mut Vec<u8>, value: u64) {
    buffer.extend_from_slice(&value.to_le_bytes());
}

#[cfg(test)]
fn push_text_u16(buffer: &mut Vec<u8>, text: &str) {
    let bytes = text.as_bytes();
    let length = u16::try_from(bytes.len()).expect("fixture identifiers are at most 255 bytes");
    buffer.extend_from_slice(&length.to_le_bytes());
    buffer.extend_from_slice(bytes);
}

#[cfg(test)]
fn push_text_u32(buffer: &mut Vec<u8>, text: &str) {
    let bytes = text.as_bytes();
    let length = u32::try_from(bytes.len()).expect("field text length fits u32");
    buffer.extend_from_slice(&length.to_le_bytes());
    buffer.extend_from_slice(bytes);
}

#[cfg(test)]
fn encode_value(buffer: &mut Vec<u8>, value: &DocumentValue) {
    match value {
        DocumentValue::Null => buffer.push(VALUE_NULL),
        DocumentValue::Boolean(inner) => {
            buffer.push(VALUE_BOOLEAN);
            buffer.push(u8::from(*inner));
        }
        DocumentValue::Integer(inner) => {
            buffer.push(VALUE_INTEGER);
            buffer.extend_from_slice(&inner.to_le_bytes());
        }
        DocumentValue::Number(inner) => {
            buffer.push(VALUE_NUMBER);
            push_u64(buffer, inner.to_bits());
        }
        DocumentValue::String(inner) => {
            buffer.push(VALUE_STRING);
            push_text_u32(buffer, inner);
        }
        DocumentValue::Array(items) => {
            buffer.push(VALUE_ARRAY);
            push_u64(buffer, items.len() as u64);
            for item in items {
                encode_value(buffer, item);
            }
        }
        DocumentValue::Object(fields) => {
            buffer.push(VALUE_OBJECT);
            push_u64(buffer, fields.len() as u64);
            for (key, item) in fields {
                push_text_u32(buffer, key);
                encode_value(buffer, item);
            }
        }
    }
}

/// The manifest-line token of one kind.
#[cfg(test)]
pub(super) fn kind_token(kind: MigrationKind) -> &'static str {
    match kind {
        MigrationKind::Document => "document",
        MigrationKind::Attachment => "attachment",
        MigrationKind::Tombstone => "tombstone",
        MigrationKind::Counter => "counter",
        MigrationKind::TimeSeries => "time-series",
        MigrationKind::Identity => "identity",
        MigrationKind::CompareExchange => "compare-exchange",
        MigrationKind::StorageInternal => "storage-internal",
        MigrationKind::OrphanBlob => "orphan-blob",
        MigrationKind::PlatformConfig => "platform-config",
        MigrationKind::IndexDefinition => "index-definition",
    }
}

/// Parses a manifest-line kind token.
pub(super) fn kind_of_token(token: &str) -> Option<MigrationKind> {
    let kind = match token {
        "document" => MigrationKind::Document,
        "attachment" => MigrationKind::Attachment,
        "tombstone" => MigrationKind::Tombstone,
        "counter" => MigrationKind::Counter,
        "time-series" => MigrationKind::TimeSeries,
        "identity" => MigrationKind::Identity,
        "compare-exchange" => MigrationKind::CompareExchange,
        "storage-internal" => MigrationKind::StorageInternal,
        "orphan-blob" => MigrationKind::OrphanBlob,
        "platform-config" => MigrationKind::PlatformConfig,
        "index-definition" => MigrationKind::IndexDefinition,
        _ => return None,
    };
    Some(kind)
}
