use crate::{FormatError, FormatLimits, checked_u32, limit};

const MAGIC: &[u8; 8] = b"TBLZ4E01";
const MAJOR: u16 = 1;
const HEADER_LEN: usize = 40;
const FORMAT: &str = "LZ4 envelope";
const CODEC_IDENTITY: u8 = 0;
const CODEC_LZ4: u8 = 1;

/// Payload codec stored in a versioned application envelope.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum EnvelopeCodec {
    /// Payload bytes are stored unchanged.
    Identity,
    /// Payload uses the bounded LZ4 block codec.
    Lz4,
}

/// A validated view of decoded application payload bytes.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Envelope<'a> {
    codec: EnvelopeCodec,
    plain_bytes: &'a [u8],
}

impl<'a> Envelope<'a> {
    /// Codec used by the encoded envelope.
    #[must_use]
    pub const fn codec(&self) -> EnvelopeCodec {
        self.codec
    }

    /// Validated uncompressed application payload.
    #[must_use]
    pub const fn plain_bytes(&self) -> &'a [u8] {
        self.plain_bytes
    }
}

/// Validated codec and declared plaintext size from an encoded envelope.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct EnvelopeInfo {
    codec: EnvelopeCodec,
    plain_len: usize,
}

impl EnvelopeInfo {
    /// Codec declared by the envelope.
    #[must_use]
    pub const fn codec(&self) -> EnvelopeCodec {
        self.codec
    }

    /// Plaintext size declared by the envelope.
    #[must_use]
    pub const fn plain_len(&self) -> usize {
        self.plain_len
    }
}

#[derive(Clone, Copy, Debug)]
struct ParsedEnvelope<'a> {
    codec: EnvelopeCodec,
    stored: &'a [u8],
    plain_len: usize,
    plain_crc: u32,
}

/// Encode a payload using LZ4 where it reduces size, otherwise identity.
///
/// The 40-byte header carries explicit encoded and uncompressed lengths, a
/// header CRC32C, a CRC32C of the stored payload, and a CRC32C of the decoded
/// payload. The caller must reserve the output buffer's capacity before this
/// call and retain its memory reservation for the buffer's full lifetime.
///
/// # Errors
///
/// Returns an error if the payload is empty, exceeds configured limits, cannot
/// fit in the v1 envelope, or the output buffer cannot reserve its capacity.
pub fn encode_lz4_envelope_into(
    output: &mut Vec<u8>,
    plain: &[u8],
    limits: &FormatLimits,
) -> Result<EnvelopeCodec, FormatError> {
    encode_lz4_envelope_with_mode(output, plain, false, limits)
}

/// Encode a payload using exactly the requested envelope codec.
///
/// Forced LZ4 encoding is retained even when its payload is larger than the
/// plaintext, provided the complete encoded envelope fits the configured cap.
/// Identity encoding stores the input bytes unchanged. The caller must reserve
/// and retain the output buffer's memory reservation for its full lifetime.
///
/// # Errors
///
/// Returns an error if the payload is empty, exceeds configured limits, cannot
/// fit in the v1 envelope, compression fails, or the output buffer cannot
/// reserve its capacity.
pub fn encode_envelope_with_codec_into(
    output: &mut Vec<u8>,
    plain: &[u8],
    codec: EnvelopeCodec,
    limits: &FormatLimits,
) -> Result<EnvelopeCodec, FormatError> {
    match codec {
        EnvelopeCodec::Identity => encode_identity_envelope_into(output, plain, limits),
        EnvelopeCodec::Lz4 => encode_lz4_envelope_with_mode(output, plain, true, limits),
    }
}

fn encode_identity_envelope_into(
    output: &mut Vec<u8>,
    plain: &[u8],
    limits: &FormatLimits,
) -> Result<EnvelopeCodec, FormatError> {
    let limits = limits.effective();
    if plain.is_empty() {
        return Err(invalid("empty application payloads are not valid"));
    }
    limit(
        "envelope uncompressed bytes",
        plain.len(),
        limits.max_envelope_plain_bytes,
    )?;
    let total_len = HEADER_LEN
        .checked_add(plain.len())
        .ok_or_else(|| invalid("encoded envelope length overflows"))?;
    limit(
        "envelope encoded bytes",
        total_len,
        limits.max_envelope_encoded_bytes,
    )?;
    checked_u32(total_len, "envelope")?;

    output.clear();
    output
        .try_reserve_exact(total_len)
        .map_err(|_| FormatError::Allocation)?;
    output.resize(total_len, 0);
    output[HEADER_LEN..].copy_from_slice(plain);
    write_header(output, EnvelopeCodec::Identity, plain)?;
    Ok(EnvelopeCodec::Identity)
}

fn encode_lz4_envelope_with_mode(
    output: &mut Vec<u8>,
    plain: &[u8],
    force_lz4: bool,
    limits: &FormatLimits,
) -> Result<EnvelopeCodec, FormatError> {
    let limits = limits.effective();
    if plain.is_empty() {
        return Err(invalid("empty application payloads are not valid"));
    }
    limit(
        "envelope uncompressed bytes",
        plain.len(),
        limits.max_envelope_plain_bytes,
    )?;
    let payload_capacity = limits
        .max_envelope_encoded_bytes
        .checked_sub(HEADER_LEN)
        .ok_or(FormatError::LimitExceeded {
            what: "envelope encoded bytes",
            value: HEADER_LEN,
            limit: limits.max_envelope_encoded_bytes,
        })?;
    let maximum_lz4 = lz4_flex::block::get_maximum_output_size(plain.len());
    let codec_capacity = maximum_lz4.min(payload_capacity);
    let buffer_capacity = if plain.len() <= payload_capacity {
        codec_capacity.max(plain.len())
    } else {
        codec_capacity
    };
    let allocation_len = HEADER_LEN
        .checked_add(buffer_capacity)
        .ok_or_else(|| invalid("envelope allocation size overflows"))?;
    limit(
        "envelope encoded bytes",
        allocation_len,
        limits.max_envelope_encoded_bytes,
    )?;
    checked_u32(allocation_len, "envelope")?;

    output.clear();
    output
        .try_reserve_exact(allocation_len)
        .map_err(|_| FormatError::Allocation)?;
    output.resize(allocation_len, 0);

    let compressed = lz4_flex::block::compress_into(plain, &mut output[HEADER_LEN..]);
    let codec = match compressed {
        Ok(compressed_len) if force_lz4 => {
            output.truncate(HEADER_LEN + compressed_len);
            EnvelopeCodec::Lz4
        }
        Ok(compressed_len) if compressed_len < plain.len() => {
            output.truncate(HEADER_LEN + compressed_len);
            EnvelopeCodec::Lz4
        }
        Ok(compressed_len) if compressed_len <= payload_capacity => {
            if plain.len() <= payload_capacity && compressed_len >= plain.len() {
                output.truncate(HEADER_LEN + plain.len());
                output[HEADER_LEN..].copy_from_slice(plain);
                EnvelopeCodec::Identity
            } else {
                output.truncate(HEADER_LEN + compressed_len);
                EnvelopeCodec::Lz4
            }
        }
        Ok(compressed_len) => {
            if plain.len() > payload_capacity {
                output.clear();
                return Err(FormatError::LimitExceeded {
                    what: "compressed envelope bytes",
                    value: HEADER_LEN.saturating_add(compressed_len),
                    limit: limits.max_envelope_encoded_bytes,
                });
            }
            output.truncate(HEADER_LEN + plain.len());
            output[HEADER_LEN..].copy_from_slice(plain);
            EnvelopeCodec::Identity
        }
        Err(_) if !force_lz4 && plain.len() <= payload_capacity => {
            output.truncate(HEADER_LEN + plain.len());
            output[HEADER_LEN..].copy_from_slice(plain);
            EnvelopeCodec::Identity
        }
        Err(_) => {
            output.clear();
            return Err(FormatError::LimitExceeded {
                what: "compressed envelope bytes",
                value: HEADER_LEN.saturating_add(maximum_lz4),
                limit: limits.max_envelope_encoded_bytes,
            });
        }
    };
    write_header(output, codec, plain)?;
    Ok(codec)
}

/// Validate envelope framing and checksums without decompressing its payload.
///
/// The returned plaintext length is bounded by `limits`. For an identity
/// envelope, the plaintext checksum is verified because stored bytes are the
/// plaintext. For LZ4, the header and stored-payload checksums are verified;
/// the plaintext checksum can only be verified during `decode_envelope`.
///
/// # Errors
///
/// Returns an error if the envelope is truncated, malformed, unsupported,
/// oversized, or fails a checksum that can be validated without decompression.
pub fn inspect_envelope(bytes: &[u8], limits: &FormatLimits) -> Result<EnvelopeInfo, FormatError> {
    let limits = limits.effective();
    let parsed = parse_envelope(bytes, &limits)?;
    if parsed.codec == EnvelopeCodec::Identity {
        validate_identity_payload(&parsed)?;
    }
    Ok(EnvelopeInfo {
        codec: parsed.codec,
        plain_len: parsed.plain_len,
    })
}

/// Validate and decode a complete envelope into a caller-owned bounded buffer.
///
/// Identity payloads borrow directly from the input. LZ4 payloads are expanded
/// into `plain_output` after checking encoded size, declared plain size, header
/// CRC, and stored-payload CRC. The caller must reserve and retain a memory
/// permit for the output capacity before calling.
///
/// # Errors
///
/// Returns an error if the envelope is truncated, malformed, unsupported,
/// oversized, checksum-invalid, or cannot be decompressed into the output
/// buffer.
pub fn decode_envelope<'a>(
    bytes: &'a [u8],
    plain_output: &'a mut Vec<u8>,
    limits: &FormatLimits,
) -> Result<Envelope<'a>, FormatError> {
    let limits = limits.effective();
    let parsed = parse_envelope(bytes, &limits)?;

    match parsed.codec {
        EnvelopeCodec::Identity => {
            validate_identity_payload(&parsed)?;
            plain_output.clear();
            Ok(Envelope {
                codec: parsed.codec,
                plain_bytes: parsed.stored,
            })
        }
        EnvelopeCodec::Lz4 => {
            plain_output.clear();
            plain_output
                .try_reserve_exact(parsed.plain_len)
                .map_err(|_| FormatError::Allocation)?;
            plain_output.resize(parsed.plain_len, 0);
            let Ok(actual) = lz4_flex::block::decompress_into(parsed.stored, plain_output) else {
                plain_output.clear();
                return Err(FormatError::Compression);
            };
            if actual != parsed.plain_len {
                plain_output.clear();
                return Err(invalid("decompressed length differs from its declaration"));
            }
            if crc32c::crc32c(plain_output) != parsed.plain_crc {
                plain_output.clear();
                return Err(FormatError::ChecksumMismatch {
                    format: "LZ4 envelope plaintext",
                });
            }
            Ok(Envelope {
                codec: parsed.codec,
                plain_bytes: plain_output,
            })
        }
    }
}

fn validate_identity_payload(parsed: &ParsedEnvelope<'_>) -> Result<(), FormatError> {
    if parsed.stored.len() != parsed.plain_len {
        return Err(invalid("identity payload lengths must match"));
    }
    if crc32c::crc32c(parsed.stored) != parsed.plain_crc {
        return Err(FormatError::ChecksumMismatch {
            format: "LZ4 envelope plaintext",
        });
    }
    Ok(())
}

fn parse_envelope<'a>(
    bytes: &'a [u8],
    limits: &FormatLimits,
) -> Result<ParsedEnvelope<'a>, FormatError> {
    if bytes.len() < HEADER_LEN {
        return Err(FormatError::Truncated {
            format: FORMAT,
            expected: HEADER_LEN,
            actual: bytes.len(),
        });
    }
    if &bytes[..8] != MAGIC {
        return Err(invalid("bad magic"));
    }
    let major = read_u16(bytes, 8)?;
    if major != MAJOR {
        return Err(FormatError::UnsupportedVersion {
            format: FORMAT,
            major,
            minor: 0,
        });
    }
    if read_u16(bytes, 10)? != 0 || bytes[13..16] != [0, 0, 0] {
        return Err(invalid("unknown flags or nonzero reserved bytes"));
    }
    let header_crc = read_u32(bytes, 28)?;
    if crc32c::crc32c(&bytes[..28]) != header_crc {
        return Err(FormatError::ChecksumMismatch {
            format: "LZ4 envelope header",
        });
    }

    let codec = match bytes[12] {
        CODEC_IDENTITY => EnvelopeCodec::Identity,
        CODEC_LZ4 => EnvelopeCodec::Lz4,
        _ => return Err(invalid("unknown codec")),
    };
    let total_len = read_u32(bytes, 16)? as usize;
    let stored_len = read_u32(bytes, 20)? as usize;
    let plain_len = read_u32(bytes, 24)? as usize;
    limit(
        "envelope encoded bytes",
        total_len,
        limits.max_envelope_encoded_bytes,
    )?;
    limit(
        "envelope uncompressed bytes",
        plain_len,
        limits.max_envelope_plain_bytes,
    )?;
    let expected_total = HEADER_LEN
        .checked_add(stored_len)
        .ok_or_else(|| invalid("encoded envelope length overflows"))?;
    if expected_total != total_len {
        return Err(invalid("declared encoded length is inconsistent"));
    }
    if bytes.len() < total_len {
        return Err(FormatError::Truncated {
            format: FORMAT,
            expected: total_len,
            actual: bytes.len(),
        });
    }
    if bytes.len() != total_len {
        return Err(invalid("envelope input has trailing bytes"));
    }
    let stored = &bytes[HEADER_LEN..];
    let stored_crc = read_u32(bytes, 32)?;
    if crc32c::crc32c(stored) != stored_crc {
        return Err(FormatError::ChecksumMismatch {
            format: "LZ4 envelope payload",
        });
    }
    if plain_len == 0 {
        return Err(invalid("empty application payloads are not valid"));
    }
    let plain_crc = read_u32(bytes, 36)?;
    Ok(ParsedEnvelope {
        codec,
        stored,
        plain_len,
        plain_crc,
    })
}

fn write_header(output: &mut [u8], codec: EnvelopeCodec, plain: &[u8]) -> Result<(), FormatError> {
    let stored_len = output
        .len()
        .checked_sub(HEADER_LEN)
        .ok_or_else(|| invalid("output is shorter than its header"))?;
    let total_len = checked_u32(output.len(), "envelope")?;
    let stored_len = checked_u32(stored_len, "envelope payload")?;
    let plain_len = checked_u32(plain.len(), "envelope plaintext")?;
    output[..8].copy_from_slice(MAGIC);
    output[8..10].copy_from_slice(&MAJOR.to_le_bytes());
    output[10..12].copy_from_slice(&0u16.to_le_bytes());
    output[12] = match codec {
        EnvelopeCodec::Identity => CODEC_IDENTITY,
        EnvelopeCodec::Lz4 => CODEC_LZ4,
    };
    output[13..16].fill(0);
    output[16..20].copy_from_slice(&total_len.to_le_bytes());
    output[20..24].copy_from_slice(&stored_len.to_le_bytes());
    output[24..28].copy_from_slice(&plain_len.to_le_bytes());
    let header_crc = crc32c::crc32c(&output[..28]);
    output[28..32].copy_from_slice(&header_crc.to_le_bytes());
    let stored_crc = crc32c::crc32c(&output[HEADER_LEN..]);
    output[32..36].copy_from_slice(&stored_crc.to_le_bytes());
    output[36..40].copy_from_slice(&crc32c::crc32c(plain).to_le_bytes());
    Ok(())
}

fn invalid(reason: &'static str) -> FormatError {
    FormatError::Invalid {
        format: FORMAT,
        reason,
    }
}

fn read_u16(bytes: &[u8], offset: usize) -> Result<u16, FormatError> {
    let end = offset.saturating_add(2);
    let slice = bytes.get(offset..end).ok_or(FormatError::Truncated {
        format: FORMAT,
        expected: end,
        actual: bytes.len(),
    })?;
    Ok(u16::from_le_bytes([slice[0], slice[1]]))
}

fn read_u32(bytes: &[u8], offset: usize) -> Result<u32, FormatError> {
    let end = offset.saturating_add(4);
    let slice = bytes.get(offset..end).ok_or(FormatError::Truncated {
        format: FORMAT,
        expected: end,
        actual: bytes.len(),
    })?;
    Ok(u32::from_le_bytes([slice[0], slice[1], slice[2], slice[3]]))
}

#[cfg(test)]
mod tests {
    use super::{
        EnvelopeCodec, HEADER_LEN, decode_envelope, encode_envelope_with_codec_into,
        encode_lz4_envelope_into, inspect_envelope,
    };
    use crate::{FormatError, FormatLimits};

    #[test]
    fn lz4_and_identity_envelopes_round_trip_with_bounded_decode() {
        let limits = FormatLimits::default();
        let repetitive = vec![b'x'; 16 * 1024];
        let mut encoded = Vec::new();
        let codec = encode_lz4_envelope_into(&mut encoded, &repetitive, &limits)
            .expect("repetitive payload compresses");
        assert_eq!(codec, EnvelopeCodec::Lz4);
        let mut plain_output = Vec::new();
        let decoded = decode_envelope(&encoded, &mut plain_output, &limits)
            .expect("compressed envelope decodes");
        assert_eq!(decoded.codec(), EnvelopeCodec::Lz4);
        assert_eq!(decoded.plain_bytes(), repetitive);

        let short = b"x";
        let codec = encode_lz4_envelope_into(&mut encoded, short, &limits)
            .expect("small payload uses bounded identity encoding");
        assert_eq!(codec, EnvelopeCodec::Identity);
        let decoded =
            decode_envelope(&encoded, &mut plain_output, &limits).expect("identity decodes");
        assert_eq!(decoded.codec(), EnvelopeCodec::Identity);
        assert_eq!(decoded.plain_bytes(), short);
    }

    #[test]
    fn envelope_checksums_truncation_and_limits_fail_before_expansion() {
        let limits = FormatLimits::default();
        let plain = vec![b'z'; 4096];
        let mut encoded = Vec::new();
        encode_lz4_envelope_into(&mut encoded, &plain, &limits).expect("payload encodes");
        let mut corrupt = encoded.clone();
        let last = corrupt.len() - 1;
        corrupt[last] ^= 1;
        assert!(matches!(
            decode_envelope(&corrupt, &mut Vec::new(), &limits),
            Err(FormatError::ChecksumMismatch { .. })
        ));
        assert!(matches!(
            decode_envelope(&encoded[..encoded.len() - 1], &mut Vec::new(), &limits),
            Err(FormatError::Truncated { .. })
        ));

        let raised_limits = FormatLimits {
            max_envelope_encoded_bytes: usize::MAX,
            max_envelope_plain_bytes: usize::MAX,
            ..limits
        };
        let oversized = vec![0u8; 4 * 1024 * 1024 + 1];
        assert!(matches!(
            encode_lz4_envelope_into(&mut Vec::new(), &oversized, &raised_limits),
            Err(FormatError::LimitExceeded {
                what: "envelope uncompressed bytes",
                ..
            })
        ));
    }

    #[test]
    fn forced_identity_preserves_exact_bytes_and_obeys_encoded_cap() {
        let limits = FormatLimits::default();
        let plain = b"identity bytes stay exact";
        let mut encoded = Vec::new();
        assert_eq!(
            encode_envelope_with_codec_into(&mut encoded, plain, EnvelopeCodec::Identity, &limits,)
                .expect("identity envelope fits"),
            EnvelopeCodec::Identity
        );
        assert_eq!(encoded[12], 0);
        assert_eq!(&encoded[HEADER_LEN..], plain);
        let info = inspect_envelope(&encoded, &limits).expect("identity header inspects");
        assert_eq!(info.codec(), EnvelopeCodec::Identity);
        assert_eq!(info.plain_len(), plain.len());
        let mut plain_output = Vec::new();
        let decoded = decode_envelope(&encoded, &mut plain_output, &limits)
            .expect("forced identity envelope decodes");
        assert_eq!(decoded.codec(), EnvelopeCodec::Identity);
        assert_eq!(decoded.plain_bytes(), plain);

        let capped_limits = FormatLimits {
            max_envelope_encoded_bytes: HEADER_LEN + plain.len() - 1,
            ..limits
        };
        encoded.clear();
        assert!(matches!(
            encode_envelope_with_codec_into(
                &mut encoded,
                plain,
                EnvelopeCodec::Identity,
                &capped_limits,
            ),
            Err(FormatError::LimitExceeded {
                what: "envelope encoded bytes",
                ..
            })
        ));
        assert!(encoded.is_empty());
    }

    #[test]
    fn forced_lz4_keeps_expansion_when_it_fits_and_rejects_over_cap() {
        let limits = FormatLimits::default();
        let mut state = 0x9e37_79b9_u32;
        let plain: Vec<u8> = (0..512)
            .map(|_| {
                state ^= state << 13;
                state ^= state >> 17;
                state ^= state << 5;
                state.to_le_bytes()[0]
            })
            .collect();
        let compressed = lz4_flex::block::compress(&plain);
        assert!(compressed.len() > plain.len());

        let mut encoded = Vec::new();
        assert_eq!(
            encode_envelope_with_codec_into(&mut encoded, &plain, EnvelopeCodec::Lz4, &limits,)
                .expect("expanded LZ4 envelope fits the configured cap"),
            EnvelopeCodec::Lz4
        );
        assert_eq!(encoded[12], 1);
        let stored_len = usize::try_from(u32::from_le_bytes([
            encoded[20],
            encoded[21],
            encoded[22],
            encoded[23],
        ]))
        .expect("stored length fits in usize");
        assert_eq!(stored_len, compressed.len());
        assert!(stored_len > plain.len());
        let info = inspect_envelope(&encoded, &limits).expect("LZ4 header inspects");
        assert_eq!(info.codec(), EnvelopeCodec::Lz4);
        assert_eq!(info.plain_len(), plain.len());
        let mut plain_output = Vec::new();
        let decoded = decode_envelope(&encoded, &mut plain_output, &limits)
            .expect("expanded LZ4 envelope decodes");
        assert_eq!(decoded.codec(), EnvelopeCodec::Lz4);
        assert_eq!(decoded.plain_bytes(), plain);

        let capped_limits = FormatLimits {
            max_envelope_encoded_bytes: HEADER_LEN + plain.len(),
            ..limits
        };
        encoded.clear();
        assert!(matches!(
            encode_envelope_with_codec_into(
                &mut encoded,
                &plain,
                EnvelopeCodec::Lz4,
                &capped_limits,
            ),
            Err(FormatError::LimitExceeded {
                what: "compressed envelope bytes",
                ..
            })
        ));
        assert!(encoded.is_empty());

        encode_envelope_with_codec_into(&mut encoded, &plain, EnvelopeCodec::Lz4, &limits)
            .expect("payload fits default encoded cap");
        let mut malformed_header = encoded.clone();
        malformed_header[0] ^= 1;
        assert!(matches!(
            inspect_envelope(&malformed_header, &limits),
            Err(FormatError::Invalid { .. })
        ));

        let mut forged_plain_len = encoded.clone();
        let oversized_plain_len = u32::try_from(limits.max_envelope_plain_bytes + 1)
            .expect("test plain limit fits in u32");
        forged_plain_len[24..28].copy_from_slice(&oversized_plain_len.to_le_bytes());
        let header_crc = crc32c::crc32c(&forged_plain_len[..28]);
        forged_plain_len[28..32].copy_from_slice(&header_crc.to_le_bytes());
        assert!(matches!(
            inspect_envelope(&forged_plain_len, &limits),
            Err(FormatError::LimitExceeded {
                what: "envelope uncompressed bytes",
                ..
            })
        ));
    }
}
