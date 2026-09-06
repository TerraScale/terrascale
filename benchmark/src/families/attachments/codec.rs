//! Record encoding for the attachment content plane.
//!
//! Family values are structured [`DocumentValue`]s of the reserved
//! collection, and a `DocumentValue` carries text rather than raw bytes, so
//! attachment bytes are carried as padded RFC 4648 base64 inside one
//! `DocumentValue::String` per segment. The codec is local to this module:
//! no other family, and no caller, ever sees the segment encoding, and a
//! record that does not decode fails closed as
//! [`FamilyError::ReservedStateInvalid`](super::super::FamilyError::ReservedStateInvalid).

use crate::types::DocumentValue;

use std::collections::BTreeMap;

/// The base64 alphabet of RFC 4648 (standard, with padding).
const ALPHABET: &[u8; 64] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

/// The value each base64 alphabet byte decodes to; 255 marks an invalid byte.
const DECODE_TABLE: [u8; 256] = build_decode_table();

/// Builds the reverse lookup table at compile time.
const fn build_decode_table() -> [u8; 256] {
    let mut table = [255_u8; 256];
    let mut index: u8 = 0;
    while index < 64 {
        table[ALPHABET[index as usize] as usize] = index;
        index += 1;
    }
    table
}

/// Encodes bytes as padded RFC 4648 base64.
pub(super) fn base64_encode(bytes: &[u8]) -> String {
    let mut encoded = String::with_capacity(bytes.len().div_ceil(3) * 4);
    for chunk in bytes.chunks(3) {
        let b0 = chunk[0];
        let b1 = chunk.get(1).copied().unwrap_or(0);
        let b2 = chunk.get(2).copied().unwrap_or(0);
        let triple = (u32::from(b0) << 16) | (u32::from(b1) << 8) | u32::from(b2);
        encoded.push(ALPHABET[(triple >> 18) as usize & 0x3f] as char);
        encoded.push(ALPHABET[(triple >> 12) as usize & 0x3f] as char);
        encoded.push(if chunk.len() > 1 {
            ALPHABET[(triple >> 6) as usize & 0x3f] as char
        } else {
            '='
        });
        encoded.push(if chunk.len() > 2 { ALPHABET[triple as usize & 0x3f] as char } else { '=' });
    }
    encoded
}

/// Decodes padded RFC 4648 base64, rejecting any other shape.
///
/// # Errors
///
/// Returns `Err(())` when the input is not padded base64 of the standard
/// alphabet: wrong length, invalid characters, padding in the middle, or
/// non-zero trailing bits.
pub(crate) fn base64_decode(text: &[u8]) -> Result<Vec<u8>, ()> {
    if !text.len().is_multiple_of(4) {
        return Err(());
    }
    let padding = text.iter().rev().take_while(|byte| **byte == b'=').count();
    if padding > 2 {
        return Err(());
    }
    let groups = text.len() / 4;
    let mut decoded = Vec::with_capacity(text.len() / 4 * 3);
    for (index, group) in text.chunks(4).enumerate() {
        let last = index + 1 == groups;
        let group_padding = if last { padding } else { 0 };
        let mut sextets = [0_u32; 4];
        for (position, byte) in group.iter().enumerate() {
            let is_padding = *byte == b'=';
            let padding_starts = if padding == 0 { 4 } else { 4 - padding };
            if is_padding != (last && position >= padding_starts) {
                return Err(());
            }
            if is_padding {
                continue;
            }
            let value = DECODE_TABLE[*byte as usize];
            if value == 255 {
                return Err(());
            }
            sextets[position] = u32::from(value);
        }
        let bits = (sextets[0] << 18) | (sextets[1] << 12) | (sextets[2] << 6) | sextets[3];
        let data = [
            u8::try_from((bits >> 16) & 0xff).map_err(|_| ())?,
            u8::try_from((bits >> 8) & 0xff).map_err(|_| ())?,
            u8::try_from(bits & 0xff).map_err(|_| ())?,
        ];
        decoded.extend_from_slice(&data[..3 - group_padding]);
        // Canonical encodings leave the unused low bits of the final data
        // sextet zero; anything else is a second encoding of the same bytes
        // and fails closed. With `p` padding characters the last data sextet
        // sits at bit offset `6 * p` and carries `2 * p` unused low bits.
        let unused_bits_mask =
            ((1_u32 << (group_padding * 2)).wrapping_sub(1)) << (group_padding * 6);
        if bits & unused_bits_mask != 0 {
            return Err(());
        }
    }
    Ok(decoded)
}

/// One content manifest: the durable description of staged attachment bytes.
///
/// `lengths` carries one entry per segment record, in order. The sum of the
/// lengths is the content size, so a manifest whose lengths disagree with its
/// own `size` field is corrupt and fails closed at decode.
#[derive(Clone, Debug, PartialEq)]
pub(super) struct ContentManifest {
    /// The hex SHA-256 digest of the whole content.
    pub(super) digest: String,
    /// The whole-content size in bytes.
    pub(super) size: u64,
    /// The byte length of each segment record, in order.
    pub(super) lengths: Vec<u64>,
}

/// Encodes one manifest record value.
pub(super) fn encode_manifest(manifest: &ContentManifest) -> DocumentValue {
    DocumentValue::Object(
        [
            ("digest".to_owned(), DocumentValue::String(manifest.digest.clone())),
            ("size".to_owned(), DocumentValue::Integer(i64::try_from(manifest.size).unwrap_or(0))),
            (
                "lengths".to_owned(),
                DocumentValue::Array(
                    manifest
                        .lengths
                        .iter()
                        .map(|length| DocumentValue::Integer(i64::try_from(*length).unwrap_or(0)))
                        .collect(),
                ),
            ),
        ]
        .into_iter()
        .collect::<BTreeMap<_, _>>(),
    )
}

/// Decodes one manifest record value.
///
/// # Errors
///
/// Returns `Err(())` when the value is not a manifest record with a text
/// digest, a non-negative size, and segment lengths whose sum is the size.
pub(super) fn decode_manifest(value: &DocumentValue) -> Result<ContentManifest, ()> {
    let DocumentValue::Object(fields) = value else { return Err(()) };
    let digest = match fields.get("digest") {
        Some(DocumentValue::String(digest)) => digest.clone(),
        _ => return Err(()),
    };
    let size = match fields.get("size") {
        Some(DocumentValue::Integer(size)) if *size >= 0 => u64::try_from(*size).map_err(|_| ())?,
        _ => return Err(()),
    };
    let lengths = match fields.get("lengths") {
        Some(DocumentValue::Array(items)) => items
            .iter()
            .map(|item| match item {
                DocumentValue::Integer(length) if *length >= 0 => {
                    u64::try_from(*length).map_err(|_| ())
                }
                _ => Err(()),
            })
            .collect::<Result<Vec<_>, _>>()?,
        _ => return Err(()),
    };
    let summed: u64 =
        lengths.iter().try_fold(0_u64, |total, length| total.checked_add(*length)).ok_or(())?;
    if summed != size || (size == 0) != lengths.is_empty() {
        return Err(());
    }
    Ok(ContentManifest { digest, size, lengths })
}

/// Encodes one segment record value.
pub(super) fn encode_segment(bytes: &[u8]) -> DocumentValue {
    DocumentValue::String(base64_encode(bytes))
}

/// Decodes one segment record value.
///
/// # Errors
///
/// Returns `Err(())` when the value is not a base64 text segment.
pub(super) fn decode_segment(value: &DocumentValue) -> Result<Vec<u8>, ()> {
    match value {
        DocumentValue::String(text) => base64_decode(text.as_bytes()),
        _ => Err(()),
    }
}
