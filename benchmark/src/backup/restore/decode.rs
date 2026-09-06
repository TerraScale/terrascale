//! The streaming archive-record decoder of the restore side (BK-02).
//!
//! [`super::super::records`] writes archive members as a deterministic,
//! length-prefixed sequence of logical record frames; this module is the
//! inverse reader. It decodes one member at a time through a bounded read
//! buffer — a member is never slurped into memory — and every length prefix
//! is checked against the member's manifest-bound byte length before any
//! allocation, so a forged frame cannot provoke an oversized read. A member
//! that verifies by digest but does not decode as canonical archive records
//! fails closed as [`crate::backup::BackupError::ChecksumMismatch`]: the
//! bytes do not reproduce the typed facts their manifest binds, and restore
//! never turns that into a success.
//!
//! The tag bytes below mirror the writer's constants. The canonical
//! definitions stay private to `src/backup/records.rs`, and this module
//! keeps its own spelled copies, following the repository's cross-module
//! constant convention (the reserved family collection name is spelled
//! separately by the query and streaming seams the same way). A decode test
//! pins the mirror: the encoder's own output is the decoder's test oracle.

use std::fs::File;
use std::io::{BufReader, Cursor, Read};

use crate::backup::BackupError;
use crate::backup::records::Record;
use crate::backup::sha256::{Sha256, to_hex};
use crate::types::{
    CollectionName, Document, DocumentId, DocumentValue, LocalDurability, TenantId,
};

/// Record tag: one tenant's document in its published form (mirrors
/// `records::TAG_DOCUMENT`).
const TAG_DOCUMENT: u8 = 1;
/// Record tag: one journal delete (mirrors `records::TAG_DELETE`).
const TAG_DELETE: u8 = 3;

/// Durability tag: the commit had achieved `Synced` local durability.
const TAG_DURABILITY_SYNCED: u8 = 0;
/// Durability tag: the commit had achieved `Written` local durability.
const TAG_DURABILITY_WRITTEN: u8 = 1;
/// Durability tag: the commit had achieved `Buffered` local durability.
const TAG_DURABILITY_BUFFERED: u8 = 2;

/// Value tag: a null value.
const VALUE_NULL: u8 = 0;
/// Value tag: a boolean value.
const VALUE_BOOLEAN: u8 = 1;
/// Value tag: a signed integer value.
const VALUE_INTEGER: u8 = 2;
/// Value tag: a floating-point value.
const VALUE_NUMBER: u8 = 3;
/// Value tag: a text value.
const VALUE_STRING: u8 = 4;
/// Value tag: an ordered sequence of structured values.
const VALUE_ARRAY: u8 = 5;
/// Value tag: a map of field names to structured values.
const VALUE_OBJECT: u8 = 6;

/// The bounded read-buffer size for one streaming member decode. A member's
/// bytes pass through this window; nothing larger is ever held in memory.
pub(crate) const READ_CHUNK_BYTES: usize = 64 * 1024;

/// The structural nesting bound a decoded value may carry. It mirrors the
/// writer-side structural limit (`MAX_VALUE_DEPTH` in the storage codec, 64):
/// a document deeper than the engine accepts can never be re-published, so
/// decode rejects it instead of staging a certain rejection later.
const MAX_VALUE_DEPTH: usize = 64;

/// Streams one member's record frames to `on_record`, in stored order.
///
/// `expected_len` is the member's manifest-bound byte length; decode consumes
/// exactly that many bytes and every length prefix is validated against the
/// remaining budget before it is read, so the frames decoded are exactly the
/// bytes the manifest's digest binds. `on_record` receives each decoded
/// record and its exact frame byte length, so a caller can size per-tenant
/// archive byte weights without re-reading anything.
///
/// # Errors
///
/// Returns [`BackupError::ChecksumMismatch`] when the bytes are not canonical
/// archive records or do not match `expected_digest`, and the callback's own
/// typed failure unchanged.
pub(crate) fn decode_member(
    file: File,
    expected_len: u64,
    expected_digest: &str,
    on_record: &mut dyn FnMut(&Record, u64) -> Result<(), BackupError>,
) -> Result<(), BackupError> {
    let digest_reader =
        DigestReader { reader: file, hasher: Sha256::new(), remaining: expected_len };
    decode_reader(digest_reader, expected_len, Some(expected_digest), on_record)
}

/// Decodes an authenticated member already held in the bounded encryption
/// foundation's plaintext window. The encrypted restore path uses this after
/// `ArtifactCipher::open`; the ciphertext itself is verified separately
/// against the manifest before decryption.
pub(crate) fn decode_member_bytes(
    bytes: Vec<u8>,
    on_record: &mut dyn FnMut(&Record, u64) -> Result<(), BackupError>,
) -> Result<(), BackupError> {
    let expected_len = u64::try_from(bytes.len()).map_err(|_| BackupError::ChecksumMismatch)?;
    let digest_reader =
        DigestReader { reader: Cursor::new(bytes), hasher: Sha256::new(), remaining: expected_len };
    decode_reader(digest_reader, expected_len, None, on_record)
}

fn decode_reader<R: Read>(
    digest_reader: DigestReader<R>,
    expected_len: u64,
    expected_digest: Option<&str>,
    on_record: &mut dyn FnMut(&Record, u64) -> Result<(), BackupError>,
) -> Result<(), BackupError> {
    let mut reader = BufReader::with_capacity(READ_CHUNK_BYTES, digest_reader);
    {
        let mut cursor = FrameCursor { reader: &mut reader, remaining: expected_len };
        while cursor.remaining > 0 {
            let before = cursor.remaining;
            let record = cursor.decode_record()?;
            let frame_bytes = before - cursor.remaining;
            on_record(&record, frame_bytes)?;
        }
    }
    let digest = reader.into_inner().finish();
    if expected_digest.is_some_and(|expected| digest != expected) {
        return Err(BackupError::ChecksumMismatch);
    }
    Ok(())
}

/// A bounded reader that hashes exactly the manifest-sized prefix consumed by
/// the decoder. Limiting the underlying reads prevents `BufReader` from
/// absorbing bytes beyond the member length into the digest.
struct DigestReader<R> {
    reader: R,
    hasher: Sha256,
    remaining: u64,
}

impl<R> DigestReader<R> {
    fn finish(self) -> String {
        to_hex(&self.hasher.finish())
    }
}

impl<R: Read> Read for DigestReader<R> {
    fn read(&mut self, buffer: &mut [u8]) -> std::io::Result<usize> {
        if self.remaining == 0 || buffer.is_empty() {
            return Ok(0);
        }
        let limit = usize::try_from(self.remaining).unwrap_or(usize::MAX);
        let read_limit = buffer.len().min(limit);
        let read = self.reader.read(&mut buffer[..read_limit])?;
        if read > 0 {
            self.hasher.update(&buffer[..read]);
            self.remaining -= read as u64;
        }
        Ok(read)
    }
}

/// A bounded cursor over one member's digest-bound bytes.
struct FrameCursor<'a, R: Read> {
    reader: &'a mut BufReader<R>,
    remaining: u64,
}

/// The internal decode failure: the bytes are not canonical archive records.
type FrameResult<T> = Result<T, BackupError>;

impl<R: Read> FrameCursor<'_, R> {
    /// Decodes one complete record frame.
    fn decode_record(&mut self) -> FrameResult<Record> {
        let tag = self.read_u8()?;
        let tenant = self.read_tenant()?;
        let durability = self.read_durability()?;
        let sequence = self.read_u64()?;
        let committed_unix_millis = self.read_u64()?;
        match tag {
            TAG_DOCUMENT => {
                let id = self.read_identifier()?;
                let collection = self.read_identifier()?;
                let value = self.read_value(0)?;
                let version = self.read_u64()?;
                let id = DocumentId::try_new(&id).map_err(|_| BackupError::ChecksumMismatch)?;
                let collection = CollectionName::try_new(&collection)
                    .map_err(|_| BackupError::ChecksumMismatch)?;
                let mut document = Document::new(id, collection, value);
                document.set_version(version);
                Ok(Record::Document { tenant, document, durability, sequence, committed_unix_millis })
            }
            TAG_DELETE => {
                let collection = match self.read_u8()? {
                    0 => None,
                    1 => Some(self.read_identifier()?),
                    _ => return Err(BackupError::ChecksumMismatch),
                };
                let id = self.read_identifier()?;
                let version_at_delete = self.read_u64()?;
                Ok(Record::Delete { tenant, collection, id, version_at_delete, durability, sequence, committed_unix_millis })
            }
            _ => Err(BackupError::ChecksumMismatch),
        }
    }

    /// Reads the length-prefixed tenant name of one frame.
    fn read_tenant(&mut self) -> FrameResult<TenantId> {
        let text = self.read_text_u16()?;
        TenantId::try_new(&text).map_err(|_| BackupError::ChecksumMismatch)
    }

    /// Reads one length-prefixed bounded identifier.
    fn read_identifier(&mut self) -> FrameResult<String> {
        self.read_text_u16()
    }

    /// Reads the durability tag one frame carries.
    fn read_durability(&mut self) -> FrameResult<LocalDurability> {
        match self.read_u8()? {
            TAG_DURABILITY_SYNCED => Ok(LocalDurability::Synced),
            TAG_DURABILITY_WRITTEN => Ok(LocalDurability::Written),
            TAG_DURABILITY_BUFFERED => Ok(LocalDurability::Buffered),
            _ => Err(BackupError::ChecksumMismatch),
        }
    }

    /// Reads one structured value, enforcing the writer's nesting bound.
    fn read_value(&mut self, depth: usize) -> FrameResult<DocumentValue> {
        if depth > MAX_VALUE_DEPTH {
            return Err(BackupError::ChecksumMismatch);
        }
        match self.read_u8()? {
            VALUE_NULL => Ok(DocumentValue::Null),
            VALUE_BOOLEAN => Ok(DocumentValue::Boolean(match self.read_u8()? {
                0 => false,
                1 => true,
                _ => return Err(BackupError::ChecksumMismatch),
            })),
            VALUE_INTEGER => Ok(DocumentValue::Integer(i64::from_le_bytes(
                self.read_array(8)?.try_into().expect("eight bytes"),
            ))),
            VALUE_NUMBER => Ok(DocumentValue::Number(f64::from_le_bytes(
                self.read_array(8)?.try_into().expect("eight bytes"),
            ))),
            VALUE_STRING => {
                let bytes = self.read_length_prefixed(4)?;
                String::from_utf8(bytes)
                    .map(DocumentValue::String)
                    .map_err(|_| BackupError::ChecksumMismatch)
            }
            VALUE_ARRAY => {
                let length = self.read_u64()?;
                if length > self.remaining {
                    return Err(BackupError::ChecksumMismatch);
                }
                let mut items = Vec::new();
                for _ in 0..length {
                    items.push(self.read_value(depth + 1)?);
                }
                Ok(DocumentValue::Array(items))
            }
            VALUE_OBJECT => {
                let length = self.read_u64()?;
                if length > self.remaining / 5 {
                    return Err(BackupError::ChecksumMismatch);
                }
                let mut fields = std::collections::BTreeMap::new();
                for _ in 0..length {
                    let key_bytes = self.read_length_prefixed(4)?;
                    let key =
                        String::from_utf8(key_bytes).map_err(|_| BackupError::ChecksumMismatch)?;
                    let value = self.read_value(depth + 1)?;
                    if fields.insert(key, value).is_some() {
                        // The writer serializes a BTreeMap, whose keys are
                        // unique. Reject duplicate fields rather than
                        // silently changing the value by last-write-wins.
                        return Err(BackupError::ChecksumMismatch);
                    }
                }
                Ok(DocumentValue::Object(fields))
            }
            _ => Err(BackupError::ChecksumMismatch),
        }
    }

    /// Reads one little-endian `u8`.
    fn read_u8(&mut self) -> FrameResult<u8> {
        let bytes = self.read_array(1)?;
        Ok(bytes[0])
    }

    /// Reads one little-endian `u64`.
    fn read_u64(&mut self) -> FrameResult<u64> {
        let bytes = self.read_array(8)?;
        Ok(u64::from_le_bytes(bytes.try_into().expect("eight bytes")))
    }

    /// Reads one `u16`-length-prefixed UTF-8 text.
    fn read_text_u16(&mut self) -> FrameResult<String> {
        let bytes = self.read_length_prefixed(2)?;
        String::from_utf8(bytes).map_err(|_| BackupError::ChecksumMismatch)
    }

    /// Reads `n` exact bytes after validating the remaining budget.
    fn read_array(&mut self, n: usize) -> FrameResult<Vec<u8>> {
        let bytes = self.read_exact(n)?;
        Ok(bytes)
    }

    /// Reads one length prefix of `prefix_bytes` width, validates it against
    /// the remaining budget, and reads exactly that many bytes.
    fn read_length_prefixed(&mut self, prefix_bytes: usize) -> FrameResult<Vec<u8>> {
        let length = match prefix_bytes {
            2 => {
                let bytes = self.read_exact(2)?;
                u64::from(u16::from_le_bytes(bytes.try_into().expect("two bytes")))
            }
            4 => {
                let bytes = self.read_exact(4)?;
                u64::from(u32::from_le_bytes(bytes.try_into().expect("four bytes")))
            }
            _ => unreachable!("only u16 and u32 length prefixes exist in the codec"),
        };
        self.read_exact_usize(length)
    }

    /// Reads exactly `n` bytes, never allocating past the remaining budget.
    fn read_exact(&mut self, n: usize) -> FrameResult<Vec<u8>> {
        self.read_exact_usize(n as u64)
    }

    /// Reads exactly `n` bytes, never allocating past the remaining budget.
    fn read_exact_usize(&mut self, n: u64) -> FrameResult<Vec<u8>> {
        if n > self.remaining {
            return Err(BackupError::ChecksumMismatch);
        }
        let length = usize::try_from(n).map_err(|_| BackupError::ChecksumMismatch)?;
        let mut buffer = vec![0_u8; length];
        self.reader.read_exact(&mut buffer).map_err(|_| BackupError::ChecksumMismatch)?;
        self.remaining -= n;
        Ok(buffer)
    }
}
