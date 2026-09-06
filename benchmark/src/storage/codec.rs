//! Private codec for storage keys, logical values, checksums, and digests.
//!
//! Everything in this module is private to the storage implementation. Raw
//! bytes exist only here and in the WAL, ledger, root, and manifest writers
//! that consume this codec. No public type ever carries these bytes.
//!
//! Length prefixes are `u32`: the codec caps one document at
//! [`MAX_DOCUMENT_BYTES`] and one write set far below any count that could
//! overflow a `u32` length on supported targets, so the narrowing casts below
//! are bounded by construction rather than by a runtime check.
#![allow(clippy::cast_possible_truncation)]
#![allow(dead_code, reason = "codec decode helpers are reserved for restore paths")]

use crate::types::{
    CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion, LocalDurability, TenantId,
};

use super::intent::CommitRejection;
use super::intent::StagedWrite;

/// Maximum nesting depth accepted for one document value.
pub(super) const MAX_VALUE_DEPTH: usize = 64;

/// Maximum encoded byte length accepted for one document.
pub(super) const MAX_DOCUMENT_BYTES: usize = 1 << 20;

/// Private 64-bit FNV-1a offset basis.
const FNV_OFFSET_BASIS: u64 = 0xcbf2_9ce4_8422_2325;

/// Private 64-bit FNV-1a prime.
const FNV_PRIME: u64 = 0x0000_0100_0000_01b3;

/// Computes the private 64-bit FNV-1a checksum of `bytes`.
///
/// This is a corruption-detection checksum, not a cryptographic digest. It is
/// private to the storage format and never crosses a public seam.
pub(super) fn fnv1a(bytes: &[u8]) -> u64 {
    let mut hash = FNV_OFFSET_BASIS;
    for byte in bytes {
        hash ^= u64::from(*byte);
        hash = hash.wrapping_mul(FNV_PRIME);
    }
    hash
}

/// Appends a single byte in little-endian order.
pub(super) fn push_u8(out: &mut Vec<u8>, value: u8) {
    out.push(value);
}

/// Appends a `u32` in little-endian order.
pub(super) fn push_u32(out: &mut Vec<u8>, value: u32) {
    out.extend_from_slice(&value.to_le_bytes());
}

/// Appends a `u64` in little-endian order.
pub(super) fn push_u64(out: &mut Vec<u8>, value: u64) {
    out.extend_from_slice(&value.to_le_bytes());
}

/// Appends a length-prefixed UTF-8 string.
pub(super) fn push_str(out: &mut Vec<u8>, value: &str) {
    push_u32(out, value.len() as u32);
    out.extend_from_slice(value.as_bytes());
}

/// A forward-only reader over private encoded bytes.
pub(super) struct Reader<'a> {
    bytes: &'a [u8],
    position: usize,
}

impl<'a> Reader<'a> {
    /// Creates a reader positioned at the start of `bytes`.
    pub(super) const fn new(bytes: &'a [u8]) -> Self {
        Self { bytes, position: 0 }
    }

    /// Reports how many bytes remain.
    pub(super) fn remaining(&self) -> usize {
        self.bytes.len() - self.position
    }

    /// Reports whether the payload region is structurally complete.
    pub(super) fn take(&mut self, length: usize) -> Option<&'a [u8]> {
        if self.remaining() < length {
            return None;
        }
        let start = self.position;
        self.position += length;
        Some(&self.bytes[start..start + length])
    }

    /// Reads one byte.
    pub(super) fn read_u8(&mut self) -> Option<u8> {
        self.take(1).map(|slice| slice[0])
    }

    /// Reads one `u32` in little-endian order.
    pub(super) fn read_u32(&mut self) -> Option<u32> {
        self.take(4).map(|slice| u32::from_le_bytes(slice.try_into().expect("four bytes")))
    }

    /// Reads one `u64` in little-endian order.
    pub(super) fn read_u64(&mut self) -> Option<u64> {
        self.take(8).map(|slice| u64::from_le_bytes(slice.try_into().expect("eight bytes")))
    }

    /// Reads one length-prefixed UTF-8 string.
    pub(super) fn read_string(&mut self) -> Option<String> {
        let length = self.read_u32()? as usize;
        let bytes = self.take(length)?;
        String::from_utf8(bytes.to_owned()).ok()
    }
}

/// Appends the encoded expected-version clause.
pub(super) fn encode_expected(out: &mut Vec<u8>, expected: ExpectedVersion) {
    match expected {
        ExpectedVersion::Any => push_u8(out, 0),
        ExpectedVersion::New => push_u8(out, 1),
        ExpectedVersion::Exact(version) => {
            push_u8(out, 2);
            push_u64(out, version);
        }
    }
}

fn encode_value(out: &mut Vec<u8>, value: &DocumentValue, depth: usize) {
    if depth > MAX_VALUE_DEPTH {
        return;
    }
    match value {
        DocumentValue::Null => push_u8(out, 0),
        DocumentValue::Boolean(boolean) => {
            push_u8(out, 1);
            push_u8(out, u8::from(*boolean));
        }
        DocumentValue::Integer(integer) => {
            push_u8(out, 2);
            push_u64(out, u64::from_le_bytes(integer.to_le_bytes()));
        }
        DocumentValue::Number(number) => {
            push_u8(out, 3);
            push_u64(out, number.to_bits());
        }
        DocumentValue::String(text) => {
            push_u8(out, 4);
            push_str(out, text);
        }
        DocumentValue::Array(items) => {
            push_u8(out, 5);
            push_u32(out, items.len() as u32);
            for item in items {
                encode_value(out, item, depth + 1);
            }
        }
        DocumentValue::Object(fields) => {
            push_u8(out, 6);
            push_u32(out, fields.len() as u32);
            for (name, item) in fields {
                push_str(out, name);
                encode_value(out, item, depth + 1);
            }
        }
    }
}

fn decode_value(reader: &mut Reader<'_>, depth: usize) -> Option<DocumentValue> {
    if depth > MAX_VALUE_DEPTH {
        return None;
    }
    match reader.read_u8()? {
        0 => Some(DocumentValue::Null),
        1 => match reader.read_u8()? {
            0 => Some(DocumentValue::Boolean(false)),
            1 => Some(DocumentValue::Boolean(true)),
            _ => None,
        },
        2 => Some(DocumentValue::Integer(i64::from_le_bytes(reader.read_u64()?.to_le_bytes()))),
        3 => Some(DocumentValue::Number(f64::from_bits(reader.read_u64()?))),
        4 => Some(DocumentValue::String(reader.read_string()?)),
        5 => {
            let length = reader.read_u32()?;
            let mut items = Vec::new();
            for _ in 0..length {
                items.push(decode_value(reader, depth + 1)?);
            }
            Some(DocumentValue::Array(items))
        }
        6 => {
            let length = reader.read_u32()?;
            let mut fields = std::collections::BTreeMap::new();
            for _ in 0..length {
                let name = reader.read_string()?;
                let item = decode_value(reader, depth + 1)?;
                fields.insert(name, item);
            }
            Some(DocumentValue::Object(fields))
        }
        _ => None,
    }
}

/// Appends the encoded logical document.
pub(super) fn encode_document(out: &mut Vec<u8>, document: &Document) {
    push_str(out, document.collection().as_str());
    push_str(out, document.id().as_str());
    push_u64(out, document.metadata().version());
    encode_value(out, document.value(), 0);
}

/// Reads one encoded logical document.
pub(super) fn decode_document(reader: &mut Reader<'_>) -> Option<Document> {
    let collection = CollectionName::try_new(&reader.read_string()?).ok()?;
    let id = DocumentId::try_new(&reader.read_string()?).ok()?;
    let version = reader.read_u64()?;
    let value = decode_value(reader, 0)?;
    let mut document = Document::new(id, collection, value);
    document.set_version(version);
    Some(document)
}

/// Reports whether two writes can address the same logical document.
///
/// A collection-less delete asserts that the identifier is absent from every
/// collection, so it conflicts with any other write carrying that identifier.
pub(crate) fn staged_writes_have_duplicate_targets(writes: &[StagedWrite]) -> bool {
    let mut seen: Vec<(Option<&str>, &str)> = Vec::new();
    for write in writes {
        let target = match write {
            StagedWrite::Put { document, .. } | StagedWrite::ReplicatedPut { document, .. } => {
                (Some(document.collection().as_str()), document.id().as_str())
            }
            StagedWrite::Delete { collection, id, .. } => {
                (collection.as_ref().map(CollectionName::as_str), id.as_str())
            }
            StagedWrite::ReplicatedDelete { collection, id, .. } => {
                (Some(collection.as_str()), id.as_str())
            }
        };
        if seen.iter().any(|(collection, id)| {
            *id == target.1
                && (collection.is_none() || target.0.is_none() || *collection == target.0)
        }) {
            return true;
        }
        seen.push(target);
    }
    false
}

/// Appends one encoded reconstructable publication write (W4-CA-4).
///
/// The put form is the published document — the same encoding the root table
/// stores. The delete form is its exact key — the collection (an explicit
/// flag, absent exactly for the provable no-op delete), the identifier, and
/// the version that existed at that key.
/// Reads one encoded reconstructable publication write.
/// Orders staged writes canonically so a digest is independent of staging order.
fn sort_key(write: &StagedWrite) -> (u8, String, String) {
    match write {
        StagedWrite::Put { document, .. } => {
            (1, document.collection().as_str().to_owned(), document.id().as_str().to_owned())
        }
        StagedWrite::Delete { id, .. } => (2, String::new(), id.as_str().to_owned()),
        StagedWrite::ReplicatedPut { document, .. } => {
            (3, document.collection().as_str().to_owned(), document.id().as_str().to_owned())
        }
        StagedWrite::ReplicatedDelete { collection, id, .. } => {
            (4, collection.as_str().to_owned(), id.as_str().to_owned())
        }
    }
}

/// Appends one write in the digest's canonical form.
///
/// The digest form deliberately excludes the snapshot-resolved collection of a
/// delete: the same logical delete stages as `Some(collection)` when the
/// snapshot contains the document and as `None` when it does not, and a retry
/// after an uncertain outcome may run against either view. The collection is
/// kept for conflict checking and WAL replay, but it is not part of the
/// identity binding.
fn encode_digest_write(out: &mut Vec<u8>, write: &StagedWrite) {
    match write {
        StagedWrite::Put { document, expected } => {
            push_u8(out, 1);
            push_str(out, document.collection().as_str());
            push_str(out, document.id().as_str());
            encode_expected(out, *expected);
            encode_document(out, document);
        }
        StagedWrite::Delete { id, expected, .. } => {
            push_u8(out, 2);
            push_str(out, id.as_str());
            encode_expected(out, *expected);
        }
        StagedWrite::ReplicatedPut {
            document,
            commit_ulid,
            hlc,
            origin,
            origin_sequence,
            context,
        } => {
            push_u8(out, 3);
            push_str(out, document.collection().as_str());
            push_str(out, document.id().as_str());
            push_str(out, commit_ulid);
            push_u64(out, *hlc);
            push_str(out, origin);
            push_u64(out, *origin_sequence);
            push_u32(out, context.len() as u32);
            out.extend_from_slice(context);
            encode_document(out, document);
        }
        StagedWrite::ReplicatedDelete {
            collection,
            id,
            commit_ulid,
            hlc,
            origin,
            origin_sequence,
            context,
        } => {
            push_u8(out, 4);
            push_str(out, collection.as_str());
            push_str(out, id.as_str());
            push_str(out, commit_ulid);
            push_u64(out, *hlc);
            push_str(out, origin);
            push_u64(out, *origin_sequence);
            push_u32(out, context.len() as u32);
            out.extend_from_slice(context);
        }
    }
}

/// Computes the logical digest that binds one operation identity to its write
/// set.
///
/// The digest covers the tenant, every staged write, and each expected-version
/// clause, in canonical write order. It does not cover the read snapshot, so a
/// retry of the same logical write set after a lost response keeps its
/// identity. A delete contributes only its identifier and expected version:
/// the collection a delete happens to resolve against is snapshot state, not
/// part of the logical request.
pub(crate) fn write_set_digest(tenant: &TenantId, writes: &[StagedWrite]) -> u64 {
    let mut ordered: Vec<&StagedWrite> = writes.iter().collect();
    ordered.sort_by_key(|write| sort_key(write));
    let mut bytes = Vec::new();
    push_str(&mut bytes, tenant.as_str());
    push_u32(&mut bytes, ordered.len() as u32);
    for write in ordered {
        encode_digest_write(&mut bytes, write);
    }
    fnv1a(&bytes)
}

/// Reports whether one document is within the structural limits the codec
/// accepts.
pub(crate) fn document_within_limits(document: &Document) -> bool {
    let mut encoded = Vec::new();
    encode_document(&mut encoded, document);
    encoded.len() <= MAX_DOCUMENT_BYTES && value_depth(document.value(), 0) <= MAX_VALUE_DEPTH
}

fn value_depth(value: &DocumentValue, depth: usize) -> usize {
    match value {
        DocumentValue::Array(items) => {
            items.iter().map(|item| value_depth(item, depth + 1)).max().unwrap_or(depth)
        }
        DocumentValue::Object(fields) => {
            fields.values().map(|item| value_depth(item, depth + 1)).max().unwrap_or(depth)
        }
        _ => depth,
    }
}

// ---------------------------------------------------------------------------
// Shared durability / expectation / ledger-frame helpers
//
// These spellings predate the storage consolidation; the
// `SQLite` engine persists and reads the same bytes, so they live here now.
// ---------------------------------------------------------------------------

/// Reports whether a stored version satisfies one expected-version rule.
pub(crate) fn matches_expectation(version: Option<u64>, expected: ExpectedVersion) -> bool {
    match expected {
        ExpectedVersion::Any => true,
        ExpectedVersion::New => version.is_none(),
        ExpectedVersion::Exact(exact) => version == Some(exact),
    }
}

/// The weaker of two durability levels (what a receipt may honestly claim).
/// Maps a durability level to its one-byte code.
pub(crate) fn durability_code(level: LocalDurability) -> u8 {
    match level {
        LocalDurability::Buffered => 0,
        LocalDurability::Written => 1,
        LocalDurability::Synced => 2,
    }
}

/// Maps a durability code back to the level.
pub(crate) fn durability_of(code: u8) -> Option<LocalDurability> {
    match code {
        0 => Some(LocalDurability::Buffered),
        1 => Some(LocalDurability::Written),
        2 => Some(LocalDurability::Synced),
        _ => None,
    }
}

/// Maps a rejection to its one-byte code.
pub(crate) fn rejection_code(reason: CommitRejection) -> u8 {
    match reason {
        CommitRejection::Validation => 1,
        CommitRejection::Conflict => 2,
        CommitRejection::OperationIdReuse => 3,
        CommitRejection::Storage => 4,
    }
}

/// Maps a rejection code back to the rejection.
pub(crate) fn decode_rejection(code: u8) -> Option<CommitRejection> {
    match code {
        1 => Some(CommitRejection::Validation),
        2 => Some(CommitRejection::Conflict),
        3 => Some(CommitRejection::OperationIdReuse),
        4 => Some(CommitRejection::Storage),
        _ => None,
    }
}

/// Fingerprints a structurally valid document with the storage's canonical encoding.
pub(crate) fn document_fingerprint(document: &Document) -> [u8; 32] {
    let mut bytes = Vec::new();
    encode_document(&mut bytes, document);
    *blake3::hash(&bytes).as_bytes()
}

/// Encodes a bounded private control value in the existing storage value codec.
pub(crate) fn encode_private_value(value: &DocumentValue) -> Option<Vec<u8>> {
    if value_depth(value, 0) > MAX_VALUE_DEPTH {
        return None;
    }
    let mut bytes = Vec::new();
    encode_value(&mut bytes, value, 0);
    (bytes.len() <= MAX_DOCUMENT_BYTES).then_some(bytes)
}

/// Decodes a complete private control value, rejecting trailing bytes.
pub(crate) fn decode_private_value(bytes: &[u8]) -> Option<DocumentValue> {
    let mut reader = Reader::new(bytes);
    let value = decode_value(&mut reader, 0)?;
    (reader.remaining() == 0).then_some(value)
}

/// Binds private metadata to the same operation identity as document writes.
pub(crate) fn transaction_digest(
    tenant: &TenantId,
    writes: &[StagedWrite],
    metadata: &[super::intent::PrivateMetadataWrite],
    schema_envelopes: &[super::intent::SchemaEnvelopeWrite],
) -> u64 {
    let document_digest = write_set_digest(tenant, writes);
    if metadata.is_empty() && schema_envelopes.is_empty() {
        return document_digest;
    }
    let mut bytes = b"terrabase.private-transaction.v1".to_vec();
    push_u64(&mut bytes, document_digest);
    let mut ordered: Vec<_> = metadata.iter().collect();
    ordered.sort_by(|a, b| a.key.cmp(&b.key));
    for record in ordered {
        push_str(&mut bytes, &record.key);
        push_u8(&mut bytes, u8::from(record.value.is_some()));
        if let Some(value) = &record.value {
            encode_value(&mut bytes, value, 0);
        }
    }
    let mut envelopes: Vec<_> = schema_envelopes.iter().collect();
    envelopes.sort_by(|a, b| {
        (&a.database, &a.branch, &a.collection).cmp(&(&b.database, &b.branch, &b.collection))
    });
    push_u32(&mut bytes, envelopes.len() as u32);
    for envelope in envelopes {
        push_str(&mut bytes, &envelope.database);
        push_str(&mut bytes, &envelope.branch);
        push_str(&mut bytes, &envelope.collection);
        push_u8(&mut bytes, u8::from(envelope.expected.is_some()));
        if let Some(expected) = &envelope.expected {
            push_u32(&mut bytes, expected.len() as u32);
            bytes.extend_from_slice(expected);
        }
        push_u32(&mut bytes, envelope.replacement.len() as u32);
        bytes.extend_from_slice(&envelope.replacement);
    }
    fnv1a(&bytes)
}
