use crate::{FormatError, FormatLimits, checked_u32, limit};
use sha2::{Digest, Sha256};

const MAGIC: &[u8; 8] = b"TBRNG001";
const MAJOR: u16 = 1;
const HEADER_LEN: usize = 108;
const ENTRY_LEN: usize = 48;
const TRAILER_LEN: usize = 4;
const MIN_LEN: usize = HEADER_LEN + TRAILER_LEN;
const SEGMENT_HEADER_LEN: u64 = 36;
const SEGMENT_FOOTER_LEN: u64 = 24;
const FORMAT: &str = "remote range sidecar";

/// A block digest supplied by an immutable-run publisher.
///
/// The publisher computes SHA-256 over exact stored block bytes, including the
/// block header and CRC32C trailer. The sidecar itself is authenticated by the
/// manifest before its descriptors may be used for remote reads.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct RemoteBlockDigest {
    /// Zero-based block ordinal in the segment directory.
    pub ordinal: u32,
    /// Absolute segment byte offset of the block header.
    pub offset: u64,
    /// Exact stored block length, including header and CRC.
    pub len: u32,
    /// SHA-256 of the exact stored block bytes.
    pub sha256: [u8; 32],
}

/// Data required to encode one bounded remote range sidecar.
#[derive(Clone, Copy, Debug)]
pub struct RemoteDescriptorInput<'a> {
    /// Complete segment byte length, including its footer.
    pub segment_len: u64,
    /// SHA-256 of the complete immutable segment bytes.
    pub segment_sha256: [u8; 32],
    /// Absolute segment byte offset of the complete encoded directory.
    pub directory_offset: u64,
    /// Exact encoded directory length, including its CRC.
    pub directory_len: u64,
    /// SHA-256 of the complete encoded directory bytes.
    pub directory_sha256: [u8; 32],
    /// Ordered exact block hashes and ranges.
    pub blocks: &'a [RemoteBlockDigest],
}

/// A block range created only after parsing a sidecar against a trusted manifest.
///
/// Fields remain private so arbitrary offsets or CRC-only descriptors cannot be
/// passed to an object reader as authenticated range authority.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct AuthenticatedRemoteBlock {
    ordinal: u32,
    offset: u64,
    len: u32,
    sha256: [u8; 32],
}

impl AuthenticatedRemoteBlock {
    /// Zero-based block ordinal.
    #[must_use]
    pub const fn ordinal(self) -> u32 {
        self.ordinal
    }

    /// Absolute segment byte offset.
    #[must_use]
    pub const fn offset(self) -> u64 {
        self.offset
    }

    /// Exact range length.
    #[must_use]
    pub const fn len(self) -> u32 {
        self.len
    }

    /// Whether the authenticated stored range is empty.
    #[must_use]
    pub const fn is_empty(self) -> bool {
        self.len == 0
    }

    /// Trusted SHA-256 of exact stored range bytes.
    #[must_use]
    pub const fn sha256(self) -> [u8; 32] {
        self.sha256
    }

    /// Verify that fetched bytes exactly match this authenticated range.
    ///
    /// # Errors
    ///
    /// Returns an error if the byte length or SHA-256 digest differs from the
    /// authenticated descriptor.
    pub fn verify(self, bytes: &[u8]) -> Result<(), FormatError> {
        if bytes.len() != self.len as usize {
            return Err(invalid(
                "remote range length differs from authenticated descriptor",
            ));
        }
        if sha256(bytes) != self.sha256 {
            return Err(FormatError::ChecksumMismatch {
                format: "authenticated remote block",
            });
        }
        Ok(())
    }
}

/// Borrowed sidecar view whose digest and segment identity were checked against
/// values from a validated manifest.
#[derive(Clone, Debug)]
pub struct RemoteDescriptorSidecar<'a> {
    segment_len: u64,
    segment_sha256: [u8; 32],
    directory_offset: u64,
    directory_len: u64,
    directory_sha256: [u8; 32],
    blocks: &'a [u8],
    block_count: usize,
    sidecar_sha256: [u8; 32],
    limits: FormatLimits,
}

impl RemoteDescriptorSidecar<'_> {
    /// Complete segment length bound by the sidecar.
    #[must_use]
    pub const fn segment_len(&self) -> u64 {
        self.segment_len
    }

    /// Complete segment SHA-256 bound by the sidecar.
    #[must_use]
    pub const fn segment_sha256(&self) -> [u8; 32] {
        self.segment_sha256
    }

    /// Segment directory offset.
    #[must_use]
    pub const fn directory_offset(&self) -> u64 {
        self.directory_offset
    }

    /// Segment directory exact byte length.
    #[must_use]
    pub const fn directory_len(&self) -> u64 {
        self.directory_len
    }

    /// SHA-256 of exact encoded directory bytes.
    #[must_use]
    pub const fn directory_sha256(&self) -> [u8; 32] {
        self.directory_sha256
    }

    /// Number of block range digests in the sidecar.
    #[must_use]
    pub const fn block_count(&self) -> usize {
        self.block_count
    }

    /// SHA-256 that a trusted manifest must bind to the sidecar object.
    #[must_use]
    pub const fn sidecar_sha256(&self) -> [u8; 32] {
        self.sidecar_sha256
    }

    /// Iterate over authenticated block ranges without allocating.
    #[must_use]
    pub fn blocks(&self) -> RemoteBlockIter<'_> {
        RemoteBlockIter {
            bytes: self.blocks,
            position: 0,
            remaining: self.block_count,
            failed: false,
        }
    }

    /// Get an authenticated block range by its zero-based ordinal in O(1).
    ///
    /// The sidecar decoder validates that entries are contiguous, ordered, and
    /// exactly sized before constructing this view, so the ordinal is also the
    /// entry's array index. Private fields prevent callers from fabricating a
    /// range that bypasses that validation.
    #[must_use]
    pub fn block(&self, ordinal: u32) -> Option<AuthenticatedRemoteBlock> {
        let index = usize::try_from(ordinal).ok()?;
        if index >= self.block_count {
            return None;
        }
        let start = index.checked_mul(ENTRY_LEN)?;
        let end = start.checked_add(ENTRY_LEN)?;
        let entry = self.blocks.get(start..end)?;
        let ordinal = read_u32(entry, 0).ok()?;
        let offset = read_u64(entry, 4).ok()?;
        let len = read_u32(entry, 12).ok()?;
        let mut digest = [0_u8; 32];
        digest.copy_from_slice(entry.get(16..48)?);
        Some(AuthenticatedRemoteBlock {
            ordinal,
            offset,
            len,
            sha256: digest,
        })
    }

    /// Verify exact directory bytes before trusting its key bounds or offsets.
    ///
    /// # Errors
    ///
    /// Returns an error if the offset, length, or SHA-256 digest differs from
    /// the authenticated directory descriptor.
    pub fn verify_directory(&self, offset: u64, bytes: &[u8]) -> Result<(), FormatError> {
        if offset != self.directory_offset || bytes.len() as u64 != self.directory_len {
            return Err(invalid(
                "directory range differs from authenticated sidecar",
            ));
        }
        if sha256(bytes) != self.directory_sha256 {
            return Err(FormatError::ChecksumMismatch {
                format: "authenticated remote directory",
            });
        }
        Ok(())
    }

    /// Limits applied while validating this sidecar.
    #[must_use]
    pub const fn limits(&self) -> FormatLimits {
        self.limits
    }
}

/// Iterator that yields only block ranges covered by an authenticated sidecar.
#[derive(Clone, Debug)]
pub struct RemoteBlockIter<'a> {
    bytes: &'a [u8],
    position: usize,
    remaining: usize,
    failed: bool,
}

impl Iterator for RemoteBlockIter<'_> {
    type Item = Result<AuthenticatedRemoteBlock, FormatError>;

    fn next(&mut self) -> Option<Self::Item> {
        if self.remaining == 0 || self.failed {
            return None;
        }
        let end = match self.position.checked_add(ENTRY_LEN) {
            Some(end) if end <= self.bytes.len() => end,
            _ => {
                self.failed = true;
                return Some(Err(invalid("sidecar entry is truncated")));
            }
        };
        let entry = &self.bytes[self.position..end];
        let ordinal = u32::from_le_bytes([entry[0], entry[1], entry[2], entry[3]]);
        let offset = u64::from_le_bytes([
            entry[4], entry[5], entry[6], entry[7], entry[8], entry[9], entry[10], entry[11],
        ]);
        let len = u32::from_le_bytes([entry[12], entry[13], entry[14], entry[15]]);
        let mut digest = [0_u8; 32];
        digest.copy_from_slice(&entry[16..48]);
        self.position = end;
        self.remaining -= 1;
        Some(Ok(AuthenticatedRemoteBlock {
            ordinal,
            offset,
            len,
            sha256: digest,
        }))
    }

    fn size_hint(&self) -> (usize, Option<usize>) {
        (self.remaining, Some(self.remaining))
    }
}

impl ExactSizeIterator for RemoteBlockIter<'_> {}

/// Calculate the exact sidecar length and validate publication metadata.
///
/// Call before reserving sidecar memory. This validates metadata shape and
/// ordering; the publisher computes the supplied SHA-256 values from the
/// immutable segment, directory, and blocks.
///
/// # Errors
///
/// Returns an error if metadata is inconsistent, any configured limit is
/// exceeded, or an encoded length overflows its wire representation.
pub fn remote_descriptor_sidecar_encoded_len(
    input: &RemoteDescriptorInput<'_>,
    limits: &FormatLimits,
) -> Result<usize, FormatError> {
    let limits = limits.effective();
    validate_segment_ranges(
        input.segment_len,
        input.directory_offset,
        input.directory_len,
        input.blocks,
        &limits,
    )?;
    limit(
        "remote range block count",
        input.blocks.len(),
        limits.max_segment_blocks,
    )?;
    let entries_len = input
        .blocks
        .len()
        .checked_mul(ENTRY_LEN)
        .ok_or_else(|| invalid("remote sidecar entry count overflows"))?;
    let total_len = HEADER_LEN
        .checked_add(entries_len)
        .and_then(|length| length.checked_add(TRAILER_LEN))
        .ok_or_else(|| invalid("remote sidecar length overflows"))?;
    limit(
        "remote sidecar bytes",
        total_len,
        limits.max_remote_sidecar_bytes,
    )?;
    checked_u32(total_len, "remote sidecar")?;
    Ok(total_len)
}

/// Encode a v1 sidecar and return its SHA-256 for the immutable manifest entry.
///
/// The caller reserves the exact size reported by
/// `remote_descriptor_sidecar_encoded_len` and retains that permit until the
/// sidecar buffer is released.
///
/// # Errors
///
/// Returns an error if metadata is inconsistent, a configured limit is
/// exceeded, a wire length overflows, or the output buffer cannot reserve its
/// capacity.
pub fn encode_remote_descriptor_sidecar_into(
    output: &mut Vec<u8>,
    input: &RemoteDescriptorInput<'_>,
    limits: &FormatLimits,
) -> Result<[u8; 32], FormatError> {
    let total_len = remote_descriptor_sidecar_encoded_len(input, limits)?;
    output.clear();
    output
        .try_reserve_exact(total_len)
        .map_err(|_| FormatError::Allocation)?;
    output.extend_from_slice(MAGIC);
    push_u16(output, MAJOR);
    push_u16(output, 0);
    push_u32(output, checked_u32(total_len, "remote sidecar")?);
    push_u64(output, input.segment_len);
    output.extend_from_slice(&input.segment_sha256);
    push_u64(output, input.directory_offset);
    push_u64(output, input.directory_len);
    output.extend_from_slice(&input.directory_sha256);
    push_u32(
        output,
        checked_u32(input.blocks.len(), "remote range block count")?,
    );
    for block in input.blocks {
        push_u32(output, block.ordinal);
        push_u64(output, block.offset);
        push_u32(output, block.len);
        output.extend_from_slice(&block.sha256);
    }
    let checksum = crc32c::crc32c(output);
    push_u32(output, checksum);
    debug_assert_eq!(output.len(), total_len);
    Ok(sha256(output))
}

/// Decode a sidecar only after binding it to manifest-authenticated identity.
///
/// The trusted sidecar digest, segment length, and segment digest must come
/// from an already validated manifest. Returned block descriptors are private
/// values that cannot be constructed from untrusted offsets by callers.
///
/// # Errors
///
/// Returns an error if the bytes fail manifest binding, structural validation,
/// checksums, or configured sidecar, directory, and block limits.
pub fn decode_remote_descriptor_sidecar<'a>(
    bytes: &'a [u8],
    trusted_sidecar_sha256: [u8; 32],
    expected_segment_len: u64,
    expected_segment_sha256: [u8; 32],
    limits: &FormatLimits,
) -> Result<RemoteDescriptorSidecar<'a>, FormatError> {
    let limits = limits.effective();
    limit(
        "remote sidecar bytes",
        bytes.len(),
        limits.max_remote_sidecar_bytes,
    )?;
    if bytes.len() < MIN_LEN {
        return Err(FormatError::Truncated {
            format: FORMAT,
            expected: MIN_LEN,
            actual: bytes.len(),
        });
    }
    let actual_sidecar_sha = sha256(bytes);
    if actual_sidecar_sha != trusted_sidecar_sha256 {
        return Err(FormatError::ChecksumMismatch {
            format: "manifest-bound remote sidecar",
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
    if read_u16(bytes, 10)? != 0 {
        return Err(invalid("unknown flags"));
    }
    let declared_len = read_u32(bytes, 12)? as usize;
    if declared_len != bytes.len() {
        return Err(invalid("declared sidecar length differs from input"));
    }
    let expected_crc = read_u32(bytes, bytes.len() - TRAILER_LEN)?;
    if crc32c::crc32c(&bytes[..bytes.len() - TRAILER_LEN]) != expected_crc {
        return Err(FormatError::ChecksumMismatch { format: FORMAT });
    }

    let segment_len = read_u64(bytes, 16)?;
    let mut segment_sha256 = [0_u8; 32];
    segment_sha256.copy_from_slice(&bytes[24..56]);
    if segment_len != expected_segment_len || segment_sha256 != expected_segment_sha256 {
        return Err(invalid(
            "segment length or digest differs from authenticated manifest entry",
        ));
    }
    let directory_offset = read_u64(bytes, 56)?;
    let directory_len = read_u64(bytes, 64)?;
    let mut directory_sha256 = [0_u8; 32];
    directory_sha256.copy_from_slice(&bytes[72..104]);
    let block_count = read_u32(bytes, 104)? as usize;
    limit(
        "remote range block count",
        block_count,
        limits.max_segment_blocks,
    )?;
    if block_count == 0 {
        return Err(invalid("remote sidecar has no block ranges"));
    }
    let entries_len = block_count
        .checked_mul(ENTRY_LEN)
        .ok_or_else(|| invalid("remote sidecar entry count overflows"))?;
    let expected_len = HEADER_LEN
        .checked_add(entries_len)
        .and_then(|length| length.checked_add(TRAILER_LEN))
        .ok_or_else(|| invalid("remote sidecar length overflows"))?;
    if expected_len != bytes.len() {
        return Err(invalid(
            "sidecar entry count does not consume its exact length",
        ));
    }
    let blocks = &bytes[HEADER_LEN..bytes.len() - TRAILER_LEN];
    validate_decoded_ranges(
        segment_len,
        directory_offset,
        directory_len,
        blocks,
        block_count,
        &limits,
    )?;
    Ok(RemoteDescriptorSidecar {
        segment_len,
        segment_sha256,
        directory_offset,
        directory_len,
        directory_sha256,
        blocks,
        block_count,
        sidecar_sha256: actual_sidecar_sha,
        limits,
    })
}

fn validate_segment_ranges(
    segment_len: u64,
    directory_offset: u64,
    directory_len: u64,
    blocks: &[RemoteBlockDigest],
    limits: &FormatLimits,
) -> Result<(), FormatError> {
    if blocks.is_empty() {
        return Err(invalid("remote sidecar has no block ranges"));
    }
    limit(
        "remote range block count",
        blocks.len(),
        limits.max_segment_blocks,
    )?;
    let directory_len_usize =
        usize::try_from(directory_len).map_err(|_| invalid("directory length overflows"))?;
    limit(
        "segment directory bytes",
        directory_len_usize,
        limits.max_directory_bytes,
    )?;
    validate_directory_range(segment_len, directory_offset, directory_len)?;
    let mut expected_offset = SEGMENT_HEADER_LEN;
    for (index, block) in blocks.iter().enumerate() {
        let expected_ordinal =
            u32::try_from(index).map_err(|_| invalid("block ordinal overflows"))?;
        if block.ordinal != expected_ordinal {
            return Err(invalid("block ordinals must be contiguous and zero-based"));
        }
        if block.offset != expected_offset {
            return Err(invalid("block ranges must be contiguous and ordered"));
        }
        let block_len = usize::try_from(block.len).unwrap_or(usize::MAX);
        if block_len < 20 {
            return Err(invalid("block range is shorter than its fixed overhead"));
        }
        limit(
            "segment block stored bytes",
            block_len,
            limits.max_block_stored_bytes,
        )?;
        expected_offset = block
            .offset
            .checked_add(u64::from(block.len))
            .ok_or_else(|| invalid("block range overflows"))?;
        if expected_offset > directory_offset {
            return Err(invalid("block range overlaps the directory"));
        }
    }
    if expected_offset != directory_offset {
        return Err(invalid("block ranges do not fill the segment data region"));
    }
    Ok(())
}

fn validate_decoded_ranges(
    segment_len: u64,
    directory_offset: u64,
    directory_len: u64,
    blocks: &[u8],
    block_count: usize,
    limits: &FormatLimits,
) -> Result<(), FormatError> {
    let directory_len_usize =
        usize::try_from(directory_len).map_err(|_| invalid("directory length overflows"))?;
    limit(
        "segment directory bytes",
        directory_len_usize,
        limits.max_directory_bytes,
    )?;
    validate_directory_range(segment_len, directory_offset, directory_len)?;
    let mut expected_offset = SEGMENT_HEADER_LEN;
    for index in 0..block_count {
        let entry_offset = index
            .checked_mul(ENTRY_LEN)
            .ok_or_else(|| invalid("sidecar entry offset overflows"))?;
        let entry_end = entry_offset
            .checked_add(ENTRY_LEN)
            .ok_or_else(|| invalid("sidecar entry end overflows"))?;
        let entry = blocks
            .get(entry_offset..entry_end)
            .ok_or_else(|| invalid("sidecar entry is truncated"))?;
        let ordinal = read_u32(entry, 0)?;
        let offset = read_u64(entry, 4)?;
        let len = read_u32(entry, 12)?;
        let expected_ordinal =
            u32::try_from(index).map_err(|_| invalid("block ordinal overflows"))?;
        if ordinal != expected_ordinal {
            return Err(invalid("block ordinals must be contiguous and zero-based"));
        }
        if offset != expected_offset {
            return Err(invalid("block ranges must be contiguous and ordered"));
        }
        let block_len = usize::try_from(len).unwrap_or(usize::MAX);
        if block_len < 20 {
            return Err(invalid("block range is shorter than its fixed overhead"));
        }
        limit(
            "segment block stored bytes",
            block_len,
            limits.max_block_stored_bytes,
        )?;
        expected_offset = offset
            .checked_add(u64::from(len))
            .ok_or_else(|| invalid("block range overflows"))?;
        if expected_offset > directory_offset {
            return Err(invalid("block range overlaps the directory"));
        }
    }
    if expected_offset != directory_offset {
        return Err(invalid("block ranges do not fill the segment data region"));
    }
    Ok(())
}

fn validate_directory_range(
    segment_len: u64,
    directory_offset: u64,
    directory_len: u64,
) -> Result<(), FormatError> {
    if directory_offset < SEGMENT_HEADER_LEN || directory_len == 0 {
        return Err(invalid(
            "directory range is empty or overlaps the segment header",
        ));
    }
    let directory_end = directory_offset
        .checked_add(directory_len)
        .ok_or_else(|| invalid("directory range overflows"))?;
    if directory_end
        .checked_add(SEGMENT_FOOTER_LEN)
        .is_none_or(|end| end != segment_len)
    {
        return Err(invalid("directory and footer do not match segment length"));
    }
    Ok(())
}

fn sha256(bytes: &[u8]) -> [u8; 32] {
    let digest = Sha256::digest(bytes);
    let mut bytes_out = [0_u8; 32];
    bytes_out.copy_from_slice(&digest);
    bytes_out
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

fn read_u64(bytes: &[u8], offset: usize) -> Result<u64, FormatError> {
    let end = offset.saturating_add(8);
    let slice = bytes.get(offset..end).ok_or(FormatError::Truncated {
        format: FORMAT,
        expected: end,
        actual: bytes.len(),
    })?;
    Ok(u64::from_le_bytes([
        slice[0], slice[1], slice[2], slice[3], slice[4], slice[5], slice[6], slice[7],
    ]))
}

fn push_u16(bytes: &mut Vec<u8>, value: u16) {
    bytes.extend_from_slice(&value.to_le_bytes());
}

fn push_u32(bytes: &mut Vec<u8>, value: u32) {
    bytes.extend_from_slice(&value.to_le_bytes());
}

fn push_u64(bytes: &mut Vec<u8>, value: u64) {
    bytes.extend_from_slice(&value.to_le_bytes());
}
