//! The private smuggler manifest codec and record decoder.
//!
//! The artifact's members use the backup family's private record encoding
//! (`super::records`): the export side reuses the backup member writer, and
//! this module carries the import side's fail-closed decoder plus the
//! manifest's strict text codec. Decoding never trusts a length or tag: a
//! frame that ends inside its member is
//! [`SmugglerError::RecordTruncated`], a structurally impossible frame is
//! [`SmugglerError::RecordCorrupt`], and nothing is skipped.

use std::collections::{BTreeMap, BTreeSet};
use std::fmt::Write as _;

use super::super::records::Record;
use super::{
    ArtifactManifest, ExportFamily, ExportSelection, HEADER, SmugglerError, SmugglerMember,
};
use crate::backup::sha256::{Sha256, to_hex};
use crate::types::{Document, DocumentId, DocumentValue, LocalDurability, TenantId};

/// The record tag of a document frame.
const TAG_DOCUMENT: u8 = 1;
/// The record tag of a delete frame.
const TAG_DELETE: u8 = 3;
/// The durability tags, mirroring the backup record codec.
const TAG_DURABILITY_SYNCED: u8 = 0;
const TAG_DURABILITY_WRITTEN: u8 = 1;
const TAG_DURABILITY_BUFFERED: u8 = 2;
/// The document-value tags, mirroring the backup record codec.
const VALUE_NULL: u8 = 0;
const VALUE_BOOLEAN: u8 = 1;
const VALUE_INTEGER: u8 = 2;
const VALUE_NUMBER: u8 = 3;
const VALUE_STRING: u8 = 4;
const VALUE_ARRAY: u8 = 5;
const VALUE_OBJECT: u8 = 6;

/// Renders the manifest body: every line except the identity line, in
/// strict block order.
pub(super) fn render_body(
    version: u32,
    selection: &ExportSelection,
    counts: &BTreeMap<ExportFamily, u64>,
    tenants: &[(TenantId, u64, u64, u64)],
    members: &[super::super::records::SealedMember],
) -> String {
    let mut body = String::new();
    let _ = writeln!(body, "{HEADER} {version}");
    let _ = writeln!(body, "selection {}", selection.tokens());
    for family in selection.families() {
        let _ = writeln!(
            body,
            "count {} {}",
            family.token(),
            counts.get(&family).copied().unwrap_or(0)
        );
    }
    for (tenant, documents, family_records, tombstones) in tenants {
        let name = tenant.as_str();
        let _ = writeln!(
            body,
            "tenant {}:{} {documents} {family_records} {tombstones}",
            name.len(),
            name
        );
    }
    for member in members {
        let _ = writeln!(body, "member {} {} {}", member.name(), member.length(), member.digest());
    }
    body
}

/// Computes the content-derived identity of a manifest body.
pub(super) fn identity_of_body(body: &str) -> String {
    let mut hasher = Sha256::new();
    hasher.update(body.as_bytes());
    to_hex(&hasher.finish())
}

/// Inserts the identity line after the header of a rendered body.
pub(super) fn with_identity(body: &str, identity: &str) -> String {
    let mut lines = body.split_inclusive('\n');
    let header = lines.next().unwrap_or_default();
    let mut manifest = String::from(header);
    let _ = writeln!(manifest, "identity {identity}");
    manifest.push_str(&lines.collect::<String>());
    manifest
}

/// Strictly parses one artifact manifest text.
///
/// # Errors
///
/// Returns [`SmugglerError::UnsupportedVersion`] for a future format
/// version and [`SmugglerError::ManifestInvalid`] for every malformed,
/// unordered, unknown, or unbound manifest.
/// The manifest block order.
#[derive(Clone, Copy, Eq, PartialEq)]
enum Phase {
    Counts,
    Tenants,
    Members,
}

pub(super) fn parse_manifest(text: &str) -> Result<ArtifactManifest, SmugglerError> {
    let mut lines = text.lines();
    let header = lines.next().ok_or(SmugglerError::ManifestInvalid)?;
    let mut header_parts = header.split(' ');
    if header_parts.next() != Some(HEADER) {
        return Err(SmugglerError::ManifestInvalid);
    }
    let version = header_parts
        .next()
        .and_then(|value| value.parse::<u32>().ok())
        .ok_or(SmugglerError::ManifestInvalid)?;
    if header_parts.next().is_some() {
        return Err(SmugglerError::ManifestInvalid);
    }
    if version != 1 {
        return Err(SmugglerError::UnsupportedVersion);
    }
    let identity_line = lines.next().ok_or(SmugglerError::ManifestInvalid)?;
    let identity = identity_line.strip_prefix("identity ").ok_or(SmugglerError::ManifestInvalid)?;
    validate_hex(identity)?;
    let selection_line = lines.next().ok_or(SmugglerError::ManifestInvalid)?;
    let selection_text =
        selection_line.strip_prefix("selection ").ok_or(SmugglerError::ManifestInvalid)?;
    let mut families = BTreeSet::new();
    for token in selection_text.split(',') {
        let family = ExportFamily::of_token(token).ok_or(SmugglerError::ManifestInvalid)?;
        if !families.insert(family) {
            return Err(SmugglerError::ManifestInvalid);
        }
    }
    if families.is_empty() {
        return Err(SmugglerError::ManifestInvalid);
    }
    let selection = ExportSelection { families };

    let mut counts = BTreeMap::new();
    let mut seen_count_families = BTreeSet::new();
    let mut tenants: BTreeMap<TenantId, (u64, u64, u64)> = BTreeMap::new();
    let mut members = Vec::new();
    let mut phase = Phase::Counts;
    for line in lines {
        if line.is_empty() {
            return Err(SmugglerError::ManifestInvalid);
        }
        if line.starts_with("count ") {
            if phase != Phase::Counts {
                return Err(SmugglerError::ManifestInvalid);
            }
            let rest = line.strip_prefix("count ").ok_or(SmugglerError::ManifestInvalid)?;
            let (token, count) = rest.split_once(' ').ok_or(SmugglerError::ManifestInvalid)?;
            let family = ExportFamily::of_token(token).ok_or(SmugglerError::ManifestInvalid)?;
            if !selection.contains(family) || !seen_count_families.insert(family) {
                return Err(SmugglerError::ManifestInvalid);
            }
            let count = count.parse::<u64>().map_err(|_| SmugglerError::ManifestInvalid)?;
            counts.insert(family, count);
            continue;
        }
        if line.starts_with("tenant ") {
            if phase == Phase::Members {
                return Err(SmugglerError::ManifestInvalid);
            }
            phase = Phase::Tenants;
            let (tenant, tenant_counts) = parse_tenant_line(line)?;
            if tenants.insert(tenant, tenant_counts).is_some() {
                return Err(SmugglerError::ManifestInvalid);
            }
            continue;
        }
        if line.starts_with("member ") {
            phase = Phase::Members;
            members.push(parse_member_line(line)?);
            continue;
        }
        return Err(SmugglerError::ManifestInvalid);
    }
    if members.is_empty() && counts.values().any(|count| *count > 0) {
        return Err(SmugglerError::ManifestInvalid);
    }
    let mut body = String::new();
    for line in text.lines().filter(|line| !line.starts_with("identity ")) {
        let _ = writeln!(body, "{line}");
    }
    if identity_of_body(&body) != identity {
        return Err(SmugglerError::ManifestInvalid);
    }
    Ok(ArtifactManifest { identity: identity.to_owned(), selection, counts, tenants, members })
}

/// Parses one `tenant <len>:<name> <documents> <family_records>
/// <tombstones>` line.
fn parse_tenant_line(line: &str) -> Result<(TenantId, (u64, u64, u64)), SmugglerError> {
    let rest = line.strip_prefix("tenant ").ok_or(SmugglerError::ManifestInvalid)?;
    let (length, rest) = rest.split_once(':').ok_or(SmugglerError::ManifestInvalid)?;
    let length = length.parse::<usize>().map_err(|_| SmugglerError::ManifestInvalid)?;
    let Some(name) = rest.get(..length) else {
        return Err(SmugglerError::ManifestInvalid);
    };
    let counts_text = &rest[length..];
    if !counts_text.starts_with(' ') {
        return Err(SmugglerError::ManifestInvalid);
    }
    let mut parts = counts_text[1..].split(' ');
    let documents = parts
        .next()
        .and_then(|value| value.parse::<u64>().ok())
        .ok_or(SmugglerError::ManifestInvalid)?;
    let family_records = parts
        .next()
        .and_then(|value| value.parse::<u64>().ok())
        .ok_or(SmugglerError::ManifestInvalid)?;
    let tombstones = parts
        .next()
        .and_then(|value| value.parse::<u64>().ok())
        .ok_or(SmugglerError::ManifestInvalid)?;
    if parts.next().is_some() {
        return Err(SmugglerError::ManifestInvalid);
    }
    let tenant = TenantId::try_new(name).map_err(|_| SmugglerError::ManifestInvalid)?;
    Ok((tenant, (documents, family_records, tombstones)))
}

/// Parses one `member <name> <length> <digest>` line.
fn parse_member_line(line: &str) -> Result<SmugglerMember, SmugglerError> {
    let rest = line.strip_prefix("member ").ok_or(SmugglerError::ManifestInvalid)?;
    let mut parts = rest.split(' ');
    let name = parts.next().ok_or(SmugglerError::ManifestInvalid)?;
    let length = parts
        .next()
        .and_then(|value| value.parse::<u64>().ok())
        .ok_or(SmugglerError::ManifestInvalid)?;
    let digest = parts.next().ok_or(SmugglerError::ManifestInvalid)?;
    if parts.next().is_some() {
        return Err(SmugglerError::ManifestInvalid);
    }
    validate_member_name(name)?;
    validate_hex(digest)?;
    Ok(SmugglerMember::new(name.to_owned(), length, digest.to_owned()))
}

/// Validates one 64-character lowercase hex value.
fn validate_hex(value: &str) -> Result<(), SmugglerError> {
    if value.len() == 64
        && value.bytes().all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
    {
        return Ok(());
    }
    Err(SmugglerError::ManifestInvalid)
}

/// Validates one canonical member name.
fn validate_member_name(name: &str) -> Result<(), SmugglerError> {
    let digits = name.strip_prefix("member-").ok_or(SmugglerError::ManifestInvalid)?;
    if digits.len() == 6 && digits.bytes().all(|byte| byte.is_ascii_digit()) {
        return Ok(());
    }
    Err(SmugglerError::ManifestInvalid)
}

// ---------------------------------------------------------------------------
// Record decoding
// ---------------------------------------------------------------------------

/// Decodes one record frame at `offset`, returning it and its byte length.
///
/// # Errors
///
/// Returns [`SmugglerError::RecordTruncated`] when the bytes end inside the
/// frame and [`SmugglerError::RecordCorrupt`] for an unknown tag, an
/// invalid identifier, or a value that does not consume exactly its
/// encoding.
pub(super) fn decode_record(bytes: &[u8], offset: usize) -> Result<(Record, usize), SmugglerError> {
    let mut cursor = Cursor::new(bytes, offset);
    let record = decode_frame(&mut cursor)?;
    Ok((record, cursor.offset - offset))
}

/// Decodes one full record frame.
fn decode_frame(cursor: &mut Cursor<'_>) -> Result<Record, SmugglerError> {
    let tag = cursor.take_u8().ok_or(SmugglerError::RecordTruncated)?;
    match tag {
        TAG_DOCUMENT => {
            let tenant = TenantId::try_new(cursor.take_text_u16()?)
                .map_err(|_| SmugglerError::RecordCorrupt)?;
            let durability = decode_durability(cursor)?;
            let id = DocumentId::try_new(cursor.take_text_u16()?)
                .map_err(|_| SmugglerError::RecordCorrupt)?;
            let collection = crate::types::CollectionName::try_new(cursor.take_text_u16()?)
                .map_err(|_| SmugglerError::RecordCorrupt)?;
            let value = decode_value(cursor)?;
            let version = cursor.take_u64().ok_or(SmugglerError::RecordTruncated)?;
            let mut document = Document::new(id, collection, value);
            document.set_version(version);
            Ok(Record::Document { tenant, document, durability })
        }
        TAG_DELETE => {
            let tenant = TenantId::try_new(cursor.take_text_u16()?)
                .map_err(|_| SmugglerError::RecordCorrupt)?;
            let durability = decode_durability(cursor)?;
            let collection = match cursor.take_u8().ok_or(SmugglerError::RecordTruncated)? {
                0 => None,
                1 => Some(cursor.take_text_u16()?.to_owned()),
                _ => return Err(SmugglerError::RecordCorrupt),
            };
            let id = cursor.take_text_u16()?.to_owned();
            let version_at_delete = cursor.take_u64().ok_or(SmugglerError::RecordTruncated)?;
            Ok(Record::Delete { tenant, collection, id, version_at_delete, durability })
        }
        _ => Err(SmugglerError::RecordCorrupt),
    }
}

/// Decodes one durability tag.
fn decode_durability(cursor: &mut Cursor<'_>) -> Result<LocalDurability, SmugglerError> {
    match cursor.take_u8().ok_or(SmugglerError::RecordTruncated)? {
        TAG_DURABILITY_SYNCED => Ok(LocalDurability::Synced),
        TAG_DURABILITY_WRITTEN => Ok(LocalDurability::Written),
        TAG_DURABILITY_BUFFERED => Ok(LocalDurability::Buffered),
        _ => Err(SmugglerError::RecordCorrupt),
    }
}

/// Decodes one document value.
fn decode_value(cursor: &mut Cursor<'_>) -> Result<DocumentValue, SmugglerError> {
    let tag = cursor.take_u8().ok_or(SmugglerError::RecordTruncated)?;
    match tag {
        VALUE_NULL => Ok(DocumentValue::Null),
        VALUE_BOOLEAN => match cursor.take_u8().ok_or(SmugglerError::RecordTruncated)? {
            0 => Ok(DocumentValue::Boolean(false)),
            1 => Ok(DocumentValue::Boolean(true)),
            _ => Err(SmugglerError::RecordCorrupt),
        },
        VALUE_INTEGER => {
            Ok(DocumentValue::Integer(cursor.take_i64().ok_or(SmugglerError::RecordTruncated)?))
        }
        VALUE_NUMBER => Ok(DocumentValue::Number(f64::from_bits(
            cursor.take_u64().ok_or(SmugglerError::RecordTruncated)?,
        ))),
        VALUE_STRING => Ok(DocumentValue::String(cursor.take_text_u32()?.to_owned())),
        VALUE_ARRAY => {
            let count = usize::try_from(cursor.take_u64().ok_or(SmugglerError::RecordTruncated)?)
                .unwrap_or(usize::MAX);
            let mut items = Vec::with_capacity(count.min(4096));
            for _ in 0..count {
                items.push(decode_value(cursor)?);
            }
            Ok(DocumentValue::Array(items))
        }
        VALUE_OBJECT => {
            let count = usize::try_from(cursor.take_u64().ok_or(SmugglerError::RecordTruncated)?)
                .unwrap_or(usize::MAX);
            let mut fields = std::collections::BTreeMap::new();
            for _ in 0..count {
                let key = cursor.take_text_u32()?.to_owned();
                let value = decode_value(cursor)?;
                fields.insert(key, value);
            }
            Ok(DocumentValue::Object(fields))
        }
        _ => Err(SmugglerError::RecordCorrupt),
    }
}

/// A bounds-checked read cursor over a member's bytes.
struct Cursor<'a> {
    bytes: &'a [u8],
    offset: usize,
}

impl<'a> Cursor<'a> {
    fn new(bytes: &'a [u8], offset: usize) -> Self {
        Self { bytes, offset }
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
    fn take_text_u16(&mut self) -> Result<&str, SmugglerError> {
        let length = self.take_u16().ok_or(SmugglerError::RecordTruncated)? as usize;
        self.take_text(length)
    }

    /// Reads one `u32`-length-prefixed UTF-8 string.
    fn take_text_u32(&mut self) -> Result<&str, SmugglerError> {
        let length = self.take_u32().ok_or(SmugglerError::RecordTruncated)? as usize;
        self.take_text(length)
    }

    fn take_text(&mut self, length: usize) -> Result<&str, SmugglerError> {
        let end = self.offset.checked_add(length).ok_or(SmugglerError::RecordCorrupt)?;
        let slice = self.bytes.get(self.offset..end).ok_or(SmugglerError::RecordTruncated)?;
        self.offset = end;
        std::str::from_utf8(slice).map_err(|_| SmugglerError::RecordCorrupt)
    }
}
