use crate::{FormatError, FormatLimits, checked_u32, limit};
use std::cmp::Ordering;
use std::ops::Range;

const HEADER_MAGIC: &[u8; 8] = b"TBRUN001";
const BLOCK_MAGIC: &[u8; 4] = b"TBB1";
const DIRECTORY_MAGIC: &[u8; 8] = b"TBDIR001";
const FOOTER_MAGIC: &[u8; 8] = b"TBEND001";
const MAJOR: u16 = 1;
const HEADER_LEN: usize = 36;
const BLOCK_HEADER_LEN: usize = 16;
const BLOCK_TRAILER_LEN: usize = 4;
const BLOCK_OVERHEAD: usize = BLOCK_HEADER_LEN + BLOCK_TRAILER_LEN;
const RECORD_HEADER_LEN: usize = 17;
const FOOTER_LEN: usize = 24;
const DIRECTORY_FIXED_LEN: usize = 16;
const HEADER_FORMAT: &str = "segment header";
const BLOCK_FORMAT: &str = "segment block";
const DIRECTORY_FORMAT: &str = "segment directory";
const FOOTER_FORMAT: &str = "segment footer";

/// Metadata at the start of an immutable sorted run.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct SegmentHeader {
    /// Number of independently checksummed data blocks.
    pub block_count: u32,
    /// Byte offset where the directory starts.
    pub directory_offset: u64,
    /// Directory byte length including its CRC.
    pub directory_len: u64,
}

/// Metadata for one independently verifiable sorted data block.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct BlockDescriptor<'a> {
    /// Lowest key stored in this block.
    pub min_key: &'a [u8],
    /// Highest key stored in this block.
    pub max_key: &'a [u8],
    /// Absolute byte offset of the block header.
    pub block_offset: u64,
    /// Total block length including its 16-byte header and 4-byte checksum.
    pub total_block_len: u32,
    /// CRC32C value stored at the end of the block.
    pub block_crc32c: u32,
}

/// One sorted segment record.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct SegmentRecord<'a> {
    /// Opaque, non-empty key bytes.
    pub key: &'a [u8],
    /// Shard-local commit sequence.
    pub sequence: u64,
    /// Put value or tombstone.
    pub value: SegmentValue<'a>,
}

/// A record returned by an owned block cursor, with its value's byte range in
/// the complete encoded block when the record is a put.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct CursorRecord<'a> {
    /// Parsed record fields borrowing from the cursor's owned block buffer.
    pub record: SegmentRecord<'a>,
    /// Byte range in the complete encoded block containing the put value.
    /// Tombstones have no value range.
    pub value_range: Option<SegmentValueRange>,
}

/// Opaque byte range for a put value in one checked segment block.
///
/// The range is bound to the descriptor and checksum that produced it, so it
/// can be retained while storage merges records and later checked against a
/// reloaded block.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct SegmentValueRange {
    identity: SegmentBlockIdentity,
    start: usize,
    end: usize,
}

impl SegmentValueRange {
    /// Byte range relative to the beginning of the complete encoded block.
    #[must_use]
    pub const fn range(self) -> Range<usize> {
        self.start..self.end
    }
}

/// Opaque resume point emitted by an [`OwnedSegmentBlockCursor`].
///
/// The token is process-local and is valid only with the same checked block
/// descriptor and bytes. Its fields are private so callers cannot construct a
/// cursor position that skips into the middle of a record.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct SegmentBlockPosition {
    block_offset: u64,
    total_block_len: u32,
    block_crc32c: u32,
    record_count: usize,
    next_record_offset: usize,
    remaining_records: usize,
}

/// Record value kind in an immutable segment.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum SegmentValue<'a> {
    /// Stored value bytes.
    Put(&'a [u8]),
    /// Deletion marker hiding older values.
    Tombstone,
}

/// A validated, borrowed view of one complete segment block.
#[derive(Clone, Debug)]
pub struct SegmentBlock<'a> {
    record_count: usize,
    records: &'a [u8],
    checksum: u32,
    first_key: &'a [u8],
    last_key: &'a [u8],
    limits: FormatLimits,
}

impl<'a> SegmentBlock<'a> {
    /// Number of records in this block.
    #[must_use]
    pub const fn record_count(&self) -> usize {
        self.record_count
    }

    /// CRC32C value stored at the end of the block.
    #[must_use]
    pub const fn checksum(&self) -> u32 {
        self.checksum
    }

    /// First key in key-ascending, sequence-descending order.
    #[must_use]
    pub const fn first_key(&self) -> &'a [u8] {
        self.first_key
    }

    /// Last key in key-ascending, sequence-descending order.
    #[must_use]
    pub const fn last_key(&self) -> &'a [u8] {
        self.last_key
    }

    /// Iterate over records without allocating.
    #[must_use]
    pub fn records(&self) -> SegmentRecordIter<'a> {
        SegmentRecordIter {
            bytes: self.records,
            position: 0,
            remaining: self.record_count,
            failed: false,
            limits: self.limits,
        }
    }
}

/// Owned checked cursor over records in one complete encoded segment block.
///
/// The cursor owns the encoded block so its record metadata and value ranges
/// remain valid while it advances. Construction validates the full block,
/// including its checksum, record ordering, and descriptor bounds.
#[derive(Debug)]
pub struct OwnedSegmentBlockCursor {
    bytes: Vec<u8>,
    position: usize,
    remaining: usize,
    descriptor: SegmentBlockIdentity,
    limits: FormatLimits,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
struct SegmentBlockIdentity {
    block_offset: u64,
    total_block_len: u32,
    block_crc32c: u32,
    record_count: usize,
}

impl OwnedSegmentBlockCursor {
    /// Validate and own a complete block bound to its verified directory
    /// descriptor.
    ///
    /// # Errors
    ///
    /// Returns an error if block decoding fails or its length, checksum, or
    /// key bounds disagree with the supplied descriptor.
    pub fn decode(
        bytes: Vec<u8>,
        descriptor: &BlockDescriptor<'_>,
        limits: &FormatLimits,
    ) -> Result<Self, FormatError> {
        let limits = limits.effective();
        let (record_count, block_crc32c) = {
            let block = decode_segment_block_checked(&bytes, descriptor, &limits)?;
            (block.record_count, block.checksum)
        };
        Ok(Self {
            bytes,
            position: 0,
            remaining: record_count,
            descriptor: SegmentBlockIdentity {
                block_offset: descriptor.block_offset,
                total_block_len: descriptor.total_block_len,
                block_crc32c,
                record_count,
            },
            limits,
        })
    }

    /// Revalidate and own a block, then continue at a position previously
    /// emitted by this cursor for the same descriptor and block.
    ///
    /// The full block is checked again to detect changed or corrupt input;
    /// records before `position` are not returned or iterated again.
    ///
    /// # Errors
    ///
    /// Returns an error if block decoding fails or the resume token does not
    /// identify a valid position in this descriptor and block.
    pub fn resume(
        bytes: Vec<u8>,
        descriptor: &BlockDescriptor<'_>,
        limits: &FormatLimits,
        position: SegmentBlockPosition,
    ) -> Result<Self, FormatError> {
        let mut cursor = Self::decode(bytes, descriptor, limits)?;
        let payload_len = cursor.bytes.len() - BLOCK_OVERHEAD;
        if position.block_offset != cursor.descriptor.block_offset
            || position.total_block_len != cursor.descriptor.total_block_len
            || position.block_crc32c != cursor.descriptor.block_crc32c
            || position.record_count != cursor.descriptor.record_count
            || position.next_record_offset > payload_len
            || position.remaining_records > cursor.descriptor.record_count
            || (position.remaining_records == 0) != (position.next_record_offset == payload_len)
        {
            return Err(invalid(
                BLOCK_FORMAT,
                "resume position does not match the checked block",
            ));
        }
        cursor.position = position.next_record_offset;
        cursor.remaining = position.remaining_records;
        Ok(cursor)
    }

    /// Opaque position immediately after the last record returned.
    #[must_use]
    pub const fn position(&self) -> SegmentBlockPosition {
        SegmentBlockPosition {
            block_offset: self.descriptor.block_offset,
            total_block_len: self.descriptor.total_block_len,
            block_crc32c: self.descriptor.block_crc32c,
            record_count: self.descriptor.record_count,
            next_record_offset: self.position,
            remaining_records: self.remaining,
        }
    }

    /// Borrow the put value identified by a cursor-produced range token.
    ///
    /// Returns `None` when the token belongs to another descriptor/block or
    /// its byte range is outside this cursor's owned encoding.
    #[must_use]
    pub fn value_bytes(&self, range: SegmentValueRange) -> Option<&[u8]> {
        if range.identity != self.descriptor
            || range.start > range.end
            || range.end > self.bytes.len()
        {
            return None;
        }
        self.bytes.get(range.start..range.end)
    }

    /// Parse and return the next record, or `None` when the cursor is
    /// exhausted.
    ///
    /// The optional value range is relative to the start of the complete
    /// encoded block and can be used to materialize a winning value after the
    /// block is read again.
    ///
    /// # Errors
    ///
    /// Returns an error if an unexpected parsing failure occurs after the
    /// block was validated.
    pub fn next_record(&mut self) -> Result<Option<CursorRecord<'_>>, FormatError> {
        if self.remaining == 0 {
            return Ok(None);
        }
        let payload = &self.bytes[BLOCK_HEADER_LEN..self.bytes.len() - BLOCK_TRAILER_LEN];
        let current_position = self.position;
        let (record, next_position) = parse_record(payload, current_position, &self.limits)?;
        let value_range = match record.value {
            SegmentValue::Put(value) => {
                let value_start = current_position
                    .checked_add(RECORD_HEADER_LEN)
                    .and_then(|offset| offset.checked_add(record.key.len()))
                    .and_then(|offset| offset.checked_add(BLOCK_HEADER_LEN))
                    .ok_or_else(|| invalid(BLOCK_FORMAT, "value range overflows"))?;
                let value_end = value_start
                    .checked_add(value.len())
                    .ok_or_else(|| invalid(BLOCK_FORMAT, "value range overflows"))?;
                Some(SegmentValueRange {
                    identity: self.descriptor,
                    start: value_start,
                    end: value_end,
                })
            }
            SegmentValue::Tombstone => None,
        };
        self.position = next_position;
        self.remaining -= 1;
        Ok(Some(CursorRecord {
            record,
            value_range,
        }))
    }
}

/// Borrowed, validated directory containing block descriptors.
#[derive(Clone, Debug)]
pub struct SegmentDirectory<'a> {
    descriptor_count: usize,
    descriptors: &'a [u8],
    checksum: u32,
    limits: FormatLimits,
}

impl SegmentDirectory<'_> {
    /// Number of block descriptors.
    #[must_use]
    pub const fn descriptor_count(&self) -> usize {
        self.descriptor_count
    }

    /// CRC32C stored at the end of the directory.
    #[must_use]
    pub const fn checksum(&self) -> u32 {
        self.checksum
    }

    /// Iterate over descriptors without allocating.
    #[must_use]
    pub fn descriptors(&self) -> SegmentDescriptorIter<'_> {
        SegmentDescriptorIter {
            bytes: self.descriptors,
            position: 0,
            remaining: self.descriptor_count,
            limits: self.limits,
            failed: false,
        }
    }
}

/// Borrowing iterator over checked directory entries.
#[derive(Clone, Debug)]
pub struct SegmentDescriptorIter<'a> {
    bytes: &'a [u8],
    position: usize,
    remaining: usize,
    limits: FormatLimits,
    failed: bool,
}

impl<'a> Iterator for SegmentDescriptorIter<'a> {
    type Item = Result<BlockDescriptor<'a>, FormatError>;

    fn next(&mut self) -> Option<Self::Item> {
        if self.remaining == 0 || self.failed {
            return None;
        }
        match parse_descriptor(self.bytes, self.position, &self.limits) {
            Ok((descriptor, next_position)) => {
                self.position = next_position;
                self.remaining -= 1;
                Some(Ok(descriptor))
            }
            Err(error) => {
                self.failed = true;
                Some(Err(error))
            }
        }
    }

    fn size_hint(&self) -> (usize, Option<usize>) {
        (self.remaining, Some(self.remaining))
    }
}

impl ExactSizeIterator for SegmentDescriptorIter<'_> {}

/// Footer fields authenticated by the footer CRC.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct SegmentFooter {
    /// Total file length, including this footer.
    pub run_bytes: u64,
    /// CRC value stored at the end of the directory.
    pub directory_crc32c: u32,
}

/// Encode a finalized 36-byte run header into a caller-owned buffer.
///
/// # Errors
///
/// Returns an error if the header violates v1 limits or invariants, or the
/// output buffer cannot reserve its capacity.
pub fn encode_segment_header_into(
    output: &mut Vec<u8>,
    header: SegmentHeader,
    limits: &FormatLimits,
) -> Result<(), FormatError> {
    let limits = limits.effective();
    validate_header(header, &limits)?;
    output.clear();
    output
        .try_reserve_exact(HEADER_LEN)
        .map_err(|_| FormatError::Allocation)?;
    output.extend_from_slice(HEADER_MAGIC);
    push_u16(output, MAJOR);
    push_u16(output, 0);
    push_u32(output, header.block_count);
    push_u64(output, header.directory_offset);
    push_u64(output, header.directory_len);
    let checksum = crc32c::crc32c(output);
    push_u32(output, checksum);
    Ok(())
}

/// Decode exactly one finalized 36-byte run header.
///
/// # Errors
///
/// Returns an error if the header is truncated, malformed, unsupported,
/// checksum-invalid, or outside configured limits.
pub fn decode_segment_header(
    bytes: &[u8],
    limits: &FormatLimits,
) -> Result<SegmentHeader, FormatError> {
    if bytes.len() < HEADER_LEN {
        return Err(FormatError::Truncated {
            format: HEADER_FORMAT,
            expected: HEADER_LEN,
            actual: bytes.len(),
        });
    }
    if bytes.len() != HEADER_LEN {
        return Err(invalid(HEADER_FORMAT, "header input has trailing bytes"));
    }
    if &bytes[..8] != HEADER_MAGIC {
        return Err(invalid(HEADER_FORMAT, "bad magic"));
    }
    let major = read_u16(bytes, 8, HEADER_FORMAT)?;
    if major != MAJOR {
        return Err(FormatError::UnsupportedVersion {
            format: HEADER_FORMAT,
            major,
            minor: 0,
        });
    }
    if read_u16(bytes, 10, HEADER_FORMAT)? != 0 {
        return Err(invalid(HEADER_FORMAT, "unknown flags"));
    }
    let expected_crc = read_u32(bytes, 32, HEADER_FORMAT)?;
    if crc32c::crc32c(&bytes[..32]) != expected_crc {
        return Err(FormatError::ChecksumMismatch {
            format: HEADER_FORMAT,
        });
    }
    let header = SegmentHeader {
        block_count: read_u32(bytes, 12, HEADER_FORMAT)?,
        directory_offset: read_u64(bytes, 16, HEADER_FORMAT)?,
        directory_len: read_u64(bytes, 24, HEADER_FORMAT)?,
    };
    validate_header(header, &limits.effective())?;
    Ok(header)
}

/// Encode sorted records as a v1 plain segment block.
///
/// The returned checksum is the block CRC32C also written into its trailing
/// four bytes and directory descriptor. The output length includes the 16-byte
/// header and 4-byte trailer.
///
/// # Errors
///
/// Returns an error if records are invalid, unsorted, oversized, or the output
/// buffer cannot reserve its capacity.
pub fn encode_segment_block_into(
    output: &mut Vec<u8>,
    records: &[SegmentRecord<'_>],
    limits: &FormatLimits,
) -> Result<u32, FormatError> {
    let limits = limits.effective();
    if records.is_empty() {
        return Err(invalid(BLOCK_FORMAT, "empty blocks are not valid"));
    }
    limit(
        "segment record count",
        records.len(),
        limits.max_records_per_block,
    )?;
    let mut plain_len = 0usize;
    let mut previous: Option<SegmentRecord<'_>> = None;
    for record in records {
        validate_record(*record, &limits)?;
        if let Some(prior) = previous {
            match prior.key.cmp(record.key) {
                Ordering::Greater => {
                    return Err(invalid(BLOCK_FORMAT, "records are not key sorted"));
                }
                Ordering::Equal if prior.sequence <= record.sequence => {
                    return Err(invalid(
                        BLOCK_FORMAT,
                        "versions of one key must have strictly descending sequences",
                    ));
                }
                Ordering::Equal | Ordering::Less => {}
            }
        }
        let value_len = match record.value {
            SegmentValue::Put(value) => value.len(),
            SegmentValue::Tombstone => 0,
        };
        plain_len = plain_len
            .checked_add(RECORD_HEADER_LEN)
            .and_then(|length| length.checked_add(record.key.len()))
            .and_then(|length| length.checked_add(value_len))
            .ok_or_else(|| invalid(BLOCK_FORMAT, "record lengths overflow"))?;
        previous = Some(*record);
    }
    limit(
        "segment block plain bytes",
        plain_len,
        limits.max_block_plain_bytes,
    )?;
    let total_len = plain_len
        .checked_add(BLOCK_OVERHEAD)
        .ok_or_else(|| invalid(BLOCK_FORMAT, "block length overflows"))?;
    limit(
        "segment block stored bytes",
        total_len,
        limits.max_block_stored_bytes,
    )?;

    output.clear();
    output
        .try_reserve_exact(total_len)
        .map_err(|_| FormatError::Allocation)?;
    output.extend_from_slice(BLOCK_MAGIC);
    push_u32(output, checked_u32(records.len(), "segment record count")?);
    push_u32(output, checked_u32(plain_len, "segment block payload")?);
    push_u32(output, checked_u32(plain_len, "segment block payload")?);
    for record in records {
        match record.value {
            SegmentValue::Put(value) => {
                push_u32(output, checked_u32(record.key.len(), "key")?);
                push_u32(output, checked_u32(value.len(), "value")?);
                push_u64(output, record.sequence);
                output.push(0);
                output.extend_from_slice(record.key);
                output.extend_from_slice(value);
            }
            SegmentValue::Tombstone => {
                push_u32(output, checked_u32(record.key.len(), "key")?);
                push_u32(output, 0);
                push_u64(output, record.sequence);
                output.push(1);
                output.extend_from_slice(record.key);
            }
        }
    }
    let checksum = crc32c::crc32c(output);
    push_u32(output, checksum);
    debug_assert_eq!(output.len(), total_len);
    Ok(checksum)
}

/// Decode and validate one plain v1 block, including its internal CRC32C.
///
/// # Errors
///
/// Returns an error if the block is truncated, malformed, checksum-invalid, or
/// outside configured record and byte limits.
pub fn decode_segment_block<'a>(
    bytes: &'a [u8],
    limits: &FormatLimits,
) -> Result<SegmentBlock<'a>, FormatError> {
    let limits = limits.effective();
    if bytes.len() < BLOCK_OVERHEAD {
        return Err(FormatError::Truncated {
            format: BLOCK_FORMAT,
            expected: BLOCK_OVERHEAD,
            actual: bytes.len(),
        });
    }
    limit(
        "segment block stored bytes",
        bytes.len(),
        limits.max_block_stored_bytes,
    )?;
    if &bytes[..4] != BLOCK_MAGIC {
        return Err(invalid(BLOCK_FORMAT, "bad magic"));
    }
    let record_count = read_u32(bytes, 4, BLOCK_FORMAT)? as usize;
    if record_count == 0 {
        return Err(invalid(BLOCK_FORMAT, "empty blocks are not valid"));
    }
    limit(
        "segment record count",
        record_count,
        limits.max_records_per_block,
    )?;
    let stored_len = read_u32(bytes, 8, BLOCK_FORMAT)? as usize;
    let plain_len = read_u32(bytes, 12, BLOCK_FORMAT)? as usize;
    limit(
        "segment block plain bytes",
        plain_len,
        limits.max_block_plain_bytes,
    )?;
    if stored_len != plain_len {
        return Err(invalid(BLOCK_FORMAT, "v1 blocks must be uncompressed"));
    }
    let expected_len = BLOCK_OVERHEAD
        .checked_add(stored_len)
        .ok_or_else(|| invalid(BLOCK_FORMAT, "block length overflows"))?;
    if bytes.len() < expected_len {
        return Err(FormatError::Truncated {
            format: BLOCK_FORMAT,
            expected: expected_len,
            actual: bytes.len(),
        });
    }
    if bytes.len() != expected_len {
        return Err(invalid(BLOCK_FORMAT, "block input has trailing bytes"));
    }
    let expected_crc = read_u32(bytes, expected_len - 4, BLOCK_FORMAT)?;
    if crc32c::crc32c(&bytes[..expected_len - 4]) != expected_crc {
        return Err(FormatError::ChecksumMismatch {
            format: BLOCK_FORMAT,
        });
    }
    let records = &bytes[BLOCK_HEADER_LEN..expected_len - BLOCK_TRAILER_LEN];
    let (first_key, last_key) = validate_block_records(records, record_count, &limits)?;
    Ok(SegmentBlock {
        record_count,
        records,
        checksum: expected_crc,
        first_key,
        last_key,
        limits,
    })
}

/// Decode a ranged block and bind it to the descriptor from a verified directory.
///
/// # Errors
///
/// Returns an error if block decoding fails or its descriptor is malformed or
/// disagrees with the block length, checksum, or key bounds.
pub fn decode_segment_block_checked<'a>(
    bytes: &'a [u8],
    descriptor: &BlockDescriptor<'_>,
    limits: &FormatLimits,
) -> Result<SegmentBlock<'a>, FormatError> {
    let limits = limits.effective();
    validate_descriptor(descriptor, &limits)?;
    if bytes.len() != descriptor.total_block_len as usize {
        return Err(invalid(
            BLOCK_FORMAT,
            "ranged block length disagrees with its directory descriptor",
        ));
    }
    let block = decode_segment_block(bytes, &limits)?;
    if block.checksum != descriptor.block_crc32c {
        return Err(FormatError::ChecksumMismatch {
            format: "segment block descriptor",
        });
    }
    if block.first_key != descriptor.min_key || block.last_key != descriptor.max_key {
        return Err(invalid(
            BLOCK_FORMAT,
            "block key bounds disagree with its directory descriptor",
        ));
    }
    Ok(block)
}

/// Encode a complete checksummed block directory.
///
/// Directory length includes its 8-byte magic, 4-byte count, variable
/// descriptors, and trailing 4-byte CRC32C. Descriptor block lengths include
/// each block's header, payload, and CRC trailer.
///
/// # Errors
///
/// Returns an error if descriptors are invalid, unsorted, overlapping,
/// oversized, or the output buffer cannot reserve its capacity.
pub fn encode_segment_directory_into(
    output: &mut Vec<u8>,
    descriptors: &[BlockDescriptor<'_>],
    limits: &FormatLimits,
) -> Result<u32, FormatError> {
    let limits = limits.effective();
    if descriptors.is_empty() {
        return Err(invalid(DIRECTORY_FORMAT, "empty directories are not valid"));
    }
    limit(
        "segment block count",
        descriptors.len(),
        limits.max_segment_blocks,
    )?;
    let mut encoded_len = DIRECTORY_FIXED_LEN;
    let mut previous_end = None;
    let mut previous_max_key: Option<&[u8]> = None;
    for descriptor in descriptors {
        validate_descriptor(descriptor, &limits)?;
        if let Some(end) = previous_end
            && descriptor.block_offset < end
        {
            return Err(invalid(
                DIRECTORY_FORMAT,
                "block ranges overlap or are unsorted",
            ));
        }
        if let Some(max_key) = previous_max_key
            && max_key > descriptor.min_key
        {
            return Err(invalid(
                DIRECTORY_FORMAT,
                "descriptor key ranges are unsorted",
            ));
        }
        let entry_len = 24usize
            .checked_add(descriptor.min_key.len())
            .and_then(|length| length.checked_add(descriptor.max_key.len()))
            .ok_or_else(|| invalid(DIRECTORY_FORMAT, "descriptor length overflows"))?;
        encoded_len = encoded_len
            .checked_add(entry_len)
            .ok_or_else(|| invalid(DIRECTORY_FORMAT, "directory length overflows"))?;
        previous_end = Some(
            descriptor
                .block_offset
                .checked_add(u64::from(descriptor.total_block_len))
                .ok_or_else(|| invalid(DIRECTORY_FORMAT, "block offset overflows"))?,
        );
        previous_max_key = Some(descriptor.max_key);
    }
    limit(
        "segment directory bytes",
        encoded_len,
        limits.max_directory_bytes,
    )?;
    output.clear();
    output
        .try_reserve_exact(encoded_len)
        .map_err(|_| FormatError::Allocation)?;
    output.extend_from_slice(DIRECTORY_MAGIC);
    push_u32(
        output,
        checked_u32(descriptors.len(), "segment block count")?,
    );
    for descriptor in descriptors {
        push_u32(output, checked_u32(descriptor.min_key.len(), "key")?);
        push_u32(output, checked_u32(descriptor.max_key.len(), "key")?);
        output.extend_from_slice(descriptor.min_key);
        output.extend_from_slice(descriptor.max_key);
        push_u64(output, descriptor.block_offset);
        push_u32(output, descriptor.total_block_len);
        push_u32(output, descriptor.block_crc32c);
    }
    let checksum = crc32c::crc32c(output);
    push_u32(output, checksum);
    debug_assert_eq!(output.len(), encoded_len);
    Ok(checksum)
}

/// Decode and validate a complete bounded directory without allocating.
///
/// # Errors
///
/// Returns an error if the directory is truncated, malformed, checksum-invalid,
/// unsorted, overlapping, or outside configured limits.
pub fn decode_segment_directory<'a>(
    bytes: &'a [u8],
    limits: &FormatLimits,
) -> Result<SegmentDirectory<'a>, FormatError> {
    let limits = limits.effective();
    if bytes.len() < DIRECTORY_FIXED_LEN {
        return Err(FormatError::Truncated {
            format: DIRECTORY_FORMAT,
            expected: DIRECTORY_FIXED_LEN,
            actual: bytes.len(),
        });
    }
    limit(
        "segment directory bytes",
        bytes.len(),
        limits.max_directory_bytes,
    )?;
    if &bytes[..8] != DIRECTORY_MAGIC {
        return Err(invalid(DIRECTORY_FORMAT, "bad magic"));
    }
    let descriptor_count = read_u32(bytes, 8, DIRECTORY_FORMAT)? as usize;
    if descriptor_count == 0 {
        return Err(invalid(DIRECTORY_FORMAT, "empty directories are not valid"));
    }
    limit(
        "segment block count",
        descriptor_count,
        limits.max_segment_blocks,
    )?;
    let expected_crc = read_u32(bytes, bytes.len() - 4, DIRECTORY_FORMAT)?;
    if crc32c::crc32c(&bytes[..bytes.len() - 4]) != expected_crc {
        return Err(FormatError::ChecksumMismatch {
            format: DIRECTORY_FORMAT,
        });
    }
    let descriptors = &bytes[12..bytes.len() - 4];
    let mut position = 0usize;
    let mut previous_end = None;
    let mut previous_max_key: Option<&[u8]> = None;
    for _ in 0..descriptor_count {
        let (descriptor, next_position) = parse_descriptor(descriptors, position, &limits)?;
        if let Some(end) = previous_end
            && descriptor.block_offset < end
        {
            return Err(invalid(
                DIRECTORY_FORMAT,
                "block ranges overlap or are unsorted",
            ));
        }
        if let Some(max_key) = previous_max_key
            && max_key > descriptor.min_key
        {
            return Err(invalid(
                DIRECTORY_FORMAT,
                "descriptor key ranges are unsorted",
            ));
        }
        previous_end = Some(
            descriptor
                .block_offset
                .checked_add(u64::from(descriptor.total_block_len))
                .ok_or_else(|| invalid(DIRECTORY_FORMAT, "block offset overflows"))?,
        );
        previous_max_key = Some(descriptor.max_key);
        position = next_position;
    }
    if position != descriptors.len() {
        return Err(invalid(
            DIRECTORY_FORMAT,
            "descriptor count does not consume the complete directory",
        ));
    }
    Ok(SegmentDirectory {
        descriptor_count,
        descriptors,
        checksum: expected_crc,
        limits,
    })
}

/// Calculate the encoded length of one descriptor entry from its two key
/// lengths.
///
/// This checks the key and entry-size limits before a caller reads or reserves
/// memory for the variable-length portion of an entry. It does not verify
/// directory membership or authentication.
///
/// # Errors
///
/// Returns an error if either key length is zero, exceeds configured limits,
/// or the calculated entry length exceeds its bound.
pub fn segment_descriptor_entry_len(
    key_lengths: &[u8; 8],
    limits: &FormatLimits,
) -> Result<usize, FormatError> {
    descriptor_entry_len(
        read_u32(key_lengths, 0, DIRECTORY_FORMAT)? as usize,
        read_u32(key_lengths, 4, DIRECTORY_FORMAT)? as usize,
        &limits.effective(),
    )
}

/// Decode exactly one locally valid descriptor entry.
///
/// The entry contains no independent authentication. Callers that need to
/// trust directory membership must first validate the complete directory
/// checksum, ordering, and structure with [`decode_segment_directory`].
///
/// # Errors
///
/// Returns an error if the entry is truncated, has trailing bytes, is
/// malformed, or exceeds configured limits.
pub fn decode_segment_descriptor_entry<'a>(
    bytes: &'a [u8],
    limits: &FormatLimits,
) -> Result<BlockDescriptor<'a>, FormatError> {
    let prefix = bytes.get(..8).ok_or(FormatError::Truncated {
        format: DIRECTORY_FORMAT,
        expected: 8,
        actual: bytes.len(),
    })?;
    let key_lengths: &[u8; 8] = prefix.try_into().map_err(|_| FormatError::Truncated {
        format: DIRECTORY_FORMAT,
        expected: 8,
        actual: prefix.len(),
    })?;
    let expected_len = segment_descriptor_entry_len(key_lengths, limits)?;
    if bytes.len() < expected_len {
        return Err(FormatError::Truncated {
            format: DIRECTORY_FORMAT,
            expected: expected_len,
            actual: bytes.len(),
        });
    }
    if bytes.len() > expected_len {
        return Err(invalid(
            DIRECTORY_FORMAT,
            "descriptor entry has trailing bytes",
        ));
    }
    let (descriptor, next_position) = parse_descriptor(bytes, 0, &limits.effective())?;
    if next_position != bytes.len() {
        return Err(invalid(
            DIRECTORY_FORMAT,
            "descriptor entry does not consume its input",
        ));
    }
    Ok(descriptor)
}

/// Encode the fixed 24-byte segment footer.
///
/// # Errors
///
/// Returns an error if the declared run length is too small or the output
/// buffer cannot reserve its capacity.
pub fn encode_segment_footer_into(
    output: &mut Vec<u8>,
    footer: SegmentFooter,
) -> Result<(), FormatError> {
    if footer.run_bytes < (HEADER_LEN + FOOTER_LEN) as u64 {
        return Err(invalid(FOOTER_FORMAT, "run byte length is too small"));
    }
    output.clear();
    output
        .try_reserve_exact(FOOTER_LEN)
        .map_err(|_| FormatError::Allocation)?;
    output.extend_from_slice(FOOTER_MAGIC);
    push_u64(output, footer.run_bytes);
    push_u32(output, footer.directory_crc32c);
    let checksum = crc32c::crc32c(output);
    push_u32(output, checksum);
    Ok(())
}

/// Decode exactly one 24-byte segment footer.
///
/// # Errors
///
/// Returns an error if the footer is truncated, malformed, too small, or
/// checksum-invalid.
pub fn decode_segment_footer(bytes: &[u8]) -> Result<SegmentFooter, FormatError> {
    if bytes.len() < FOOTER_LEN {
        return Err(FormatError::Truncated {
            format: FOOTER_FORMAT,
            expected: FOOTER_LEN,
            actual: bytes.len(),
        });
    }
    if bytes.len() != FOOTER_LEN {
        return Err(invalid(FOOTER_FORMAT, "footer input has trailing bytes"));
    }
    if &bytes[..8] != FOOTER_MAGIC {
        return Err(invalid(FOOTER_FORMAT, "bad magic"));
    }
    let expected_crc = read_u32(bytes, 20, FOOTER_FORMAT)?;
    if crc32c::crc32c(&bytes[..20]) != expected_crc {
        return Err(FormatError::ChecksumMismatch {
            format: FOOTER_FORMAT,
        });
    }
    let footer = SegmentFooter {
        run_bytes: read_u64(bytes, 8, FOOTER_FORMAT)?,
        directory_crc32c: read_u32(bytes, 16, FOOTER_FORMAT)?,
    };
    if footer.run_bytes < (HEADER_LEN + FOOTER_LEN) as u64 {
        return Err(invalid(FOOTER_FORMAT, "run byte length is too small"));
    }
    Ok(footer)
}

/// Parse one variable-length record into a block payload.
///
/// # Errors
///
/// Returns an error if the record is truncated, malformed, or outside
/// configured key and value limits.
pub fn parse_segment_record<'a>(
    bytes: &'a [u8],
    position: usize,
    limits: &FormatLimits,
) -> Result<(SegmentRecord<'a>, usize), FormatError> {
    parse_record(bytes, position, &limits.effective())
}

/// Returns the complete encoded size of a record for bounded block admission.
///
/// # Errors
///
/// Returns an error if the record is invalid, exceeds configured limits, or
/// its encoded length overflows `usize`.
pub fn encoded_segment_record_len(
    record: SegmentRecord<'_>,
    limits: &FormatLimits,
) -> Result<usize, FormatError> {
    validate_record(record, &limits.effective())?;
    let value_len = match record.value {
        SegmentValue::Put(value) => value.len(),
        SegmentValue::Tombstone => 0,
    };
    RECORD_HEADER_LEN
        .checked_add(record.key.len())
        .and_then(|length| length.checked_add(value_len))
        .ok_or_else(|| invalid(BLOCK_FORMAT, "record length overflows"))
}

/// Borrowing iterator over records in one already validated block.
#[derive(Clone, Debug)]
pub struct SegmentRecordIter<'a> {
    bytes: &'a [u8],
    position: usize,
    remaining: usize,
    failed: bool,
    limits: FormatLimits,
}

impl<'a> Iterator for SegmentRecordIter<'a> {
    type Item = Result<SegmentRecord<'a>, FormatError>;

    fn next(&mut self) -> Option<Self::Item> {
        if self.remaining == 0 || self.failed {
            return None;
        }
        match parse_record(self.bytes, self.position, &self.limits) {
            Ok((record, next_position)) => {
                self.position = next_position;
                self.remaining -= 1;
                Some(Ok(record))
            }
            Err(error) => {
                self.failed = true;
                Some(Err(error))
            }
        }
    }

    fn size_hint(&self) -> (usize, Option<usize>) {
        (self.remaining, Some(self.remaining))
    }
}

impl ExactSizeIterator for SegmentRecordIter<'_> {}

fn validate_header(header: SegmentHeader, limits: &FormatLimits) -> Result<(), FormatError> {
    if header.block_count == 0 {
        return Err(invalid(HEADER_FORMAT, "empty runs are not valid"));
    }
    limit(
        "segment block count",
        header.block_count as usize,
        limits.max_segment_blocks,
    )?;
    limit(
        "segment directory bytes",
        usize::try_from(header.directory_len).unwrap_or(usize::MAX),
        limits.max_directory_bytes,
    )?;
    if header.directory_len < DIRECTORY_FIXED_LEN as u64 {
        return Err(invalid(HEADER_FORMAT, "directory length is too small"));
    }
    if header.directory_offset < HEADER_LEN as u64 {
        return Err(invalid(HEADER_FORMAT, "directory overlaps the run header"));
    }
    header
        .directory_offset
        .checked_add(header.directory_len)
        .ok_or_else(|| invalid(HEADER_FORMAT, "directory offset overflows"))?;
    Ok(())
}

fn validate_record(record: SegmentRecord<'_>, limits: &FormatLimits) -> Result<(), FormatError> {
    if record.key.is_empty() {
        return Err(invalid(BLOCK_FORMAT, "keys must not be empty"));
    }
    limit("key", record.key.len(), limits.max_key_bytes)?;
    match record.value {
        SegmentValue::Put(value) => limit("value", value.len(), limits.max_value_bytes),
        SegmentValue::Tombstone => Ok(()),
    }
}

fn validate_block_records<'a>(
    bytes: &'a [u8],
    count: usize,
    limits: &FormatLimits,
) -> Result<(&'a [u8], &'a [u8]), FormatError> {
    let mut position = 0usize;
    let mut previous: Option<SegmentRecord<'a>> = None;
    let mut first_key = None;
    let mut last_key = None;
    for _ in 0..count {
        let (record, next) = parse_record(bytes, position, limits)?;
        if let Some(prior) = previous {
            match prior.key.cmp(record.key) {
                Ordering::Greater => {
                    return Err(invalid(BLOCK_FORMAT, "records are not key sorted"));
                }
                Ordering::Equal if prior.sequence <= record.sequence => {
                    return Err(invalid(
                        BLOCK_FORMAT,
                        "versions of one key must have strictly descending sequences",
                    ));
                }
                Ordering::Equal | Ordering::Less => {}
            }
        } else {
            first_key = Some(record.key);
        }
        previous = Some(record);
        last_key = Some(record.key);
        position = next;
    }
    if position != bytes.len() {
        return Err(invalid(
            BLOCK_FORMAT,
            "record count does not consume the complete payload",
        ));
    }
    match (first_key, last_key) {
        (Some(first), Some(last)) => Ok((first, last)),
        _ => Err(invalid(BLOCK_FORMAT, "empty blocks are not valid")),
    }
}

fn parse_record<'a>(
    bytes: &'a [u8],
    position: usize,
    limits: &FormatLimits,
) -> Result<(SegmentRecord<'a>, usize), FormatError> {
    let fixed_end = position
        .checked_add(RECORD_HEADER_LEN)
        .ok_or_else(|| invalid(BLOCK_FORMAT, "record offset overflows"))?;
    if fixed_end > bytes.len() {
        return Err(FormatError::Truncated {
            format: BLOCK_FORMAT,
            expected: fixed_end,
            actual: bytes.len(),
        });
    }
    let key_len = read_u32(bytes, position, BLOCK_FORMAT)? as usize;
    let value_len = read_u32(bytes, position + 4, BLOCK_FORMAT)? as usize;
    let sequence = read_u64(bytes, position + 8, BLOCK_FORMAT)?;
    let kind = bytes[position + 16];
    if key_len == 0 {
        return Err(invalid(BLOCK_FORMAT, "keys must not be empty"));
    }
    limit("key", key_len, limits.max_key_bytes)?;
    limit("value", value_len, limits.max_value_bytes)?;
    if kind > 1 || (kind == 1 && value_len != 0) {
        return Err(invalid(
            BLOCK_FORMAT,
            "unknown record kind or invalid tombstone value",
        ));
    }
    let payload_len = key_len
        .checked_add(value_len)
        .ok_or_else(|| invalid(BLOCK_FORMAT, "record payload length overflows"))?;
    let end = fixed_end
        .checked_add(payload_len)
        .ok_or_else(|| invalid(BLOCK_FORMAT, "record end offset overflows"))?;
    if end > bytes.len() {
        return Err(FormatError::Truncated {
            format: BLOCK_FORMAT,
            expected: end,
            actual: bytes.len(),
        });
    }
    let key_end = fixed_end + key_len;
    let key = &bytes[fixed_end..key_end];
    let value = if kind == 0 {
        SegmentValue::Put(&bytes[key_end..end])
    } else {
        SegmentValue::Tombstone
    };
    Ok((
        SegmentRecord {
            key,
            sequence,
            value,
        },
        end,
    ))
}

fn validate_descriptor(
    descriptor: &BlockDescriptor<'_>,
    limits: &FormatLimits,
) -> Result<(), FormatError> {
    if descriptor.min_key.is_empty() || descriptor.max_key.is_empty() {
        return Err(invalid(
            DIRECTORY_FORMAT,
            "descriptor keys must not be empty",
        ));
    }
    limit("key", descriptor.min_key.len(), limits.max_key_bytes)?;
    limit("key", descriptor.max_key.len(), limits.max_key_bytes)?;
    if descriptor.min_key > descriptor.max_key {
        return Err(invalid(
            DIRECTORY_FORMAT,
            "descriptor minimum exceeds maximum",
        ));
    }
    let block_len = descriptor.total_block_len as usize;
    if block_len < BLOCK_OVERHEAD {
        return Err(invalid(
            DIRECTORY_FORMAT,
            "descriptor block length is too small",
        ));
    }
    limit(
        "segment block stored bytes",
        block_len,
        limits.max_block_stored_bytes,
    )?;
    descriptor
        .block_offset
        .checked_add(u64::from(descriptor.total_block_len))
        .ok_or_else(|| invalid(DIRECTORY_FORMAT, "block offset overflows"))?;
    Ok(())
}

fn parse_descriptor<'a>(
    bytes: &'a [u8],
    position: usize,
    limits: &FormatLimits,
) -> Result<(BlockDescriptor<'a>, usize), FormatError> {
    let key_lens_end = position
        .checked_add(8)
        .ok_or_else(|| invalid(DIRECTORY_FORMAT, "descriptor offset overflows"))?;
    if key_lens_end > bytes.len() {
        return Err(FormatError::Truncated {
            format: DIRECTORY_FORMAT,
            expected: key_lens_end,
            actual: bytes.len(),
        });
    }
    let min_len = read_u32(bytes, position, DIRECTORY_FORMAT)? as usize;
    let max_len = read_u32(bytes, position + 4, DIRECTORY_FORMAT)? as usize;
    let entry_len = descriptor_entry_len(min_len, max_len, limits)?;
    let total_len = position
        .checked_add(entry_len)
        .ok_or_else(|| invalid(DIRECTORY_FORMAT, "descriptor length overflows"))?;
    if total_len > bytes.len() {
        return Err(FormatError::Truncated {
            format: DIRECTORY_FORMAT,
            expected: total_len,
            actual: bytes.len(),
        });
    }
    let min_start = key_lens_end;
    let max_start = min_start + min_len;
    let suffix_start = max_start + max_len;
    let descriptor = BlockDescriptor {
        min_key: &bytes[min_start..max_start],
        max_key: &bytes[max_start..suffix_start],
        block_offset: read_u64(bytes, suffix_start, DIRECTORY_FORMAT)?,
        total_block_len: read_u32(bytes, suffix_start + 8, DIRECTORY_FORMAT)?,
        block_crc32c: read_u32(bytes, suffix_start + 12, DIRECTORY_FORMAT)?,
    };
    validate_descriptor(&descriptor, limits)?;
    let next_position = position
        .checked_add(total_len - position)
        .ok_or_else(|| invalid(DIRECTORY_FORMAT, "descriptor end offset overflows"))?;
    Ok((descriptor, next_position))
}

fn descriptor_entry_len(
    min_len: usize,
    max_len: usize,
    limits: &FormatLimits,
) -> Result<usize, FormatError> {
    if min_len == 0 || max_len == 0 {
        return Err(invalid(
            DIRECTORY_FORMAT,
            "descriptor keys must not be empty",
        ));
    }
    limit("key", min_len, limits.max_key_bytes)?;
    limit("key", max_len, limits.max_key_bytes)?;
    let entry_len = 24usize
        .checked_add(min_len)
        .and_then(|length| length.checked_add(max_len))
        .ok_or_else(|| invalid(DIRECTORY_FORMAT, "descriptor length overflows"))?;
    limit(
        "segment descriptor entry bytes",
        entry_len,
        limits.max_directory_bytes,
    )?;
    Ok(entry_len)
}

fn invalid(format: &'static str, reason: &'static str) -> FormatError {
    FormatError::Invalid { format, reason }
}

fn read_u16(bytes: &[u8], offset: usize, format: &'static str) -> Result<u16, FormatError> {
    let end = offset.saturating_add(2);
    let slice = bytes.get(offset..end).ok_or(FormatError::Truncated {
        format,
        expected: end,
        actual: bytes.len(),
    })?;
    Ok(u16::from_le_bytes([slice[0], slice[1]]))
}

fn read_u32(bytes: &[u8], offset: usize, format: &'static str) -> Result<u32, FormatError> {
    let end = offset.saturating_add(4);
    let slice = bytes.get(offset..end).ok_or(FormatError::Truncated {
        format,
        expected: end,
        actual: bytes.len(),
    })?;
    Ok(u32::from_le_bytes([slice[0], slice[1], slice[2], slice[3]]))
}

fn read_u64(bytes: &[u8], offset: usize, format: &'static str) -> Result<u64, FormatError> {
    let end = offset.saturating_add(8);
    let slice = bytes.get(offset..end).ok_or(FormatError::Truncated {
        format,
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

#[cfg(test)]
mod tests {
    use super::{
        BlockDescriptor, OwnedSegmentBlockCursor, SegmentFooter, SegmentHeader, SegmentRecord,
        SegmentValue, decode_segment_block, decode_segment_block_checked,
        decode_segment_descriptor_entry, decode_segment_directory, decode_segment_footer,
        decode_segment_header, encode_segment_block_into, encode_segment_directory_into,
        encode_segment_footer_into, encode_segment_header_into, segment_descriptor_entry_len,
    };
    use crate::{FormatError, FormatLimits};

    #[test]
    fn sorted_block_directory_header_and_footer_round_trip() {
        let limits = FormatLimits::default();
        let records = [
            SegmentRecord {
                key: b"alpha",
                sequence: 9,
                value: SegmentValue::Put(b"new"),
            },
            SegmentRecord {
                key: b"alpha",
                sequence: 4,
                value: SegmentValue::Tombstone,
            },
            SegmentRecord {
                key: b"beta",
                sequence: 7,
                value: SegmentValue::Put(b"value"),
            },
        ];
        let mut block_bytes = Vec::new();
        let block_crc = encode_segment_block_into(&mut block_bytes, &records, &limits)
            .expect("sorted records encode");
        let block_end = 36 + block_bytes.len() as u64;
        let descriptor = BlockDescriptor {
            min_key: b"alpha",
            max_key: b"beta",
            block_offset: 36,
            total_block_len: u32::try_from(block_bytes.len())
                .expect("fixture block length fits in u32"),
            block_crc32c: block_crc,
        };
        let mut directory_bytes = Vec::new();
        let directory_crc =
            encode_segment_directory_into(&mut directory_bytes, &[descriptor], &limits)
                .expect("valid descriptor encodes");
        let mut header_bytes = Vec::new();
        encode_segment_header_into(
            &mut header_bytes,
            SegmentHeader {
                block_count: 1,
                directory_offset: block_end,
                directory_len: directory_bytes.len() as u64,
            },
            &limits,
        )
        .expect("valid header encodes");
        let run_bytes = block_end + directory_bytes.len() as u64 + 24;
        let mut footer_bytes = Vec::new();
        encode_segment_footer_into(
            &mut footer_bytes,
            SegmentFooter {
                run_bytes,
                directory_crc32c: directory_crc,
            },
        )
        .expect("valid footer encodes");

        let header = decode_segment_header(&header_bytes, &limits).expect("header decodes");
        let directory =
            decode_segment_directory(&directory_bytes, &limits).expect("directory decodes");
        let footer = decode_segment_footer(&footer_bytes).expect("footer decodes");
        assert_eq!(
            header.block_count,
            u32::try_from(directory.descriptor_count())
                .expect("fixture descriptor count fits in u32")
        );
        assert_eq!(footer.run_bytes, run_bytes);
        assert_eq!(footer.directory_crc32c, directory.checksum());
        let decoded_descriptor = directory
            .descriptors()
            .next()
            .expect("one descriptor")
            .expect("descriptor is valid");
        let block = decode_segment_block_checked(&block_bytes, &decoded_descriptor, &limits)
            .expect("block matches trusted local descriptor");
        let decoded_records = block
            .records()
            .collect::<Result<Vec<_>, _>>()
            .expect("records iterate");
        assert_eq!(decoded_records, records);
    }

    #[test]
    fn block_checksums_order_and_record_boundaries_reject_corruption() {
        let limits = FormatLimits::default();
        let records = [SegmentRecord {
            key: b"key",
            sequence: 3,
            value: SegmentValue::Put(b"value"),
        }];
        let mut bytes = Vec::new();
        encode_segment_block_into(&mut bytes, &records, &limits).expect("record encodes");
        let mut corrupted = bytes.clone();
        corrupted[17] ^= 1;
        assert!(matches!(
            decode_segment_block(&corrupted, &limits),
            Err(FormatError::ChecksumMismatch { .. })
        ));

        let unsorted = [
            SegmentRecord {
                key: b"z",
                sequence: 1,
                value: SegmentValue::Tombstone,
            },
            SegmentRecord {
                key: b"a",
                sequence: 1,
                value: SegmentValue::Tombstone,
            },
        ];
        assert!(matches!(
            encode_segment_block_into(&mut Vec::new(), &unsorted, &limits),
            Err(FormatError::Invalid { .. })
        ));

        let mut impossible_count = [0u8; 20];
        impossible_count[..4].copy_from_slice(super::BLOCK_MAGIC);
        impossible_count[4..8].copy_from_slice(&1u32.to_le_bytes());
        impossible_count[8..12].copy_from_slice(&u32::MAX.to_le_bytes());
        impossible_count[12..16].copy_from_slice(&u32::MAX.to_le_bytes());
        assert!(matches!(
            decode_segment_block(&impossible_count, &limits),
            Err(FormatError::LimitExceeded { .. })
        ));
    }

    #[test]
    fn caller_raised_limits_cannot_raise_v1_record_caps() {
        let limits = FormatLimits {
            max_key_bytes: usize::MAX,
            max_value_bytes: usize::MAX,
            max_block_plain_bytes: usize::MAX,
            max_block_stored_bytes: usize::MAX,
            max_records_per_block: usize::MAX,
            ..FormatLimits::default()
        };
        let oversized_key = vec![b'k'; 4 * 1024 + 1];
        let record = [SegmentRecord {
            key: &oversized_key,
            sequence: 1,
            value: SegmentValue::Tombstone,
        }];
        assert!(matches!(
            encode_segment_block_into(&mut Vec::new(), &record, &limits),
            Err(FormatError::LimitExceeded { what: "key", .. })
        ));
    }

    #[test]
    fn descriptor_entry_size_and_local_decode_are_bounded_and_exact() {
        let limits = FormatLimits::default();
        let descriptor = BlockDescriptor {
            min_key: b"a",
            max_key: b"b",
            block_offset: 36,
            total_block_len: 20,
            block_crc32c: 7,
        };
        let mut directory = Vec::new();
        encode_segment_directory_into(&mut directory, &[descriptor], &limits)
            .expect("descriptor encodes");
        let entry = &directory[12..directory.len() - 4];
        let key_lengths: &[u8; 8] = entry[..8].try_into().expect("length prefix is eight bytes");
        assert_eq!(
            segment_descriptor_entry_len(key_lengths, &limits),
            Ok(entry.len())
        );
        assert_eq!(
            decode_segment_descriptor_entry(entry, &limits),
            Ok(descriptor)
        );
        assert!(matches!(
            decode_segment_descriptor_entry(&entry[..entry.len() - 1], &limits),
            Err(FormatError::Truncated { .. })
        ));
        let mut trailing = entry.to_vec();
        trailing.push(0);
        assert!(matches!(
            decode_segment_descriptor_entry(&trailing, &limits),
            Err(FormatError::Invalid { .. })
        ));

        let small_directory_limit = FormatLimits {
            max_directory_bytes: entry.len() - 1,
            ..limits
        };
        assert!(matches!(
            segment_descriptor_entry_len(key_lengths, &small_directory_limit),
            Err(FormatError::LimitExceeded {
                what: "segment descriptor entry bytes",
                ..
            })
        ));
    }

    #[test]
    fn owned_block_cursor_resumes_at_record_and_reports_put_value_ranges() {
        let limits = FormatLimits::default();
        let records = [
            SegmentRecord {
                key: b"alpha",
                sequence: 9,
                value: SegmentValue::Put(b"new"),
            },
            SegmentRecord {
                key: b"alpha",
                sequence: 4,
                value: SegmentValue::Tombstone,
            },
            SegmentRecord {
                key: b"beta",
                sequence: 3,
                value: SegmentValue::Put(b""),
            },
        ];
        let mut bytes = Vec::new();
        let checksum =
            encode_segment_block_into(&mut bytes, &records, &limits).expect("records encode");
        let descriptor = BlockDescriptor {
            min_key: b"alpha",
            max_key: b"beta",
            block_offset: 36,
            total_block_len: u32::try_from(bytes.len()).expect("block length fits"),
            block_crc32c: checksum,
        };

        let position = {
            let mut cursor = OwnedSegmentBlockCursor::decode(bytes.clone(), &descriptor, &limits)
                .expect("block validates");
            let first = cursor
                .next_record()
                .expect("first record parses")
                .expect("first record exists");
            assert_eq!(first.record.key, b"alpha");
            let value_range = first.value_range.expect("put has value range");
            assert_eq!(value_range.range(), 38..41);
            assert_eq!(cursor.value_bytes(value_range), Some(&b"new"[..]));
            cursor.position()
        };

        let mut resumed =
            OwnedSegmentBlockCursor::resume(bytes.clone(), &descriptor, &limits, position)
                .expect("position resumes in its block");
        let second = resumed
            .next_record()
            .expect("second record parses")
            .expect("second record exists");
        assert_eq!(second.record.sequence, 4);
        assert_eq!(second.record.value, SegmentValue::Tombstone);
        assert_eq!(second.value_range, None);
        let third = resumed
            .next_record()
            .expect("third record parses")
            .expect("third record exists");
        assert_eq!(third.record.key, b"beta");
        assert_eq!(
            third.value_range.map(super::SegmentValueRange::range),
            Some(84..84)
        );
        let empty_value_range = third.value_range.expect("put has value range");
        assert_eq!(resumed.value_bytes(empty_value_range), Some(&b""[..]));
        assert!(
            resumed
                .next_record()
                .expect("exhausted cursor is valid")
                .is_none()
        );

        let wrong_descriptor = BlockDescriptor {
            block_offset: descriptor.block_offset + 1,
            ..descriptor
        };
        assert!(matches!(
            OwnedSegmentBlockCursor::resume(bytes, &wrong_descriptor, &limits, position),
            Err(FormatError::Invalid { .. })
        ));
    }

    #[test]
    fn value_range_cannot_be_applied_to_another_block_or_outside_encoding() {
        let limits = FormatLimits::default();
        let records = [SegmentRecord {
            key: b"key",
            sequence: 1,
            value: SegmentValue::Put(b"value"),
        }];
        let mut bytes = Vec::new();
        let checksum =
            encode_segment_block_into(&mut bytes, &records, &limits).expect("record encodes");
        let descriptor = BlockDescriptor {
            min_key: b"key",
            max_key: b"key",
            block_offset: 36,
            total_block_len: u32::try_from(bytes.len()).expect("block length fits"),
            block_crc32c: checksum,
        };
        let mut cursor = OwnedSegmentBlockCursor::decode(bytes.clone(), &descriptor, &limits)
            .expect("block validates");
        let range = cursor
            .next_record()
            .expect("record parses")
            .expect("record exists")
            .value_range
            .expect("put has value range");
        assert_eq!(cursor.value_bytes(range), Some(&b"value"[..]));

        let other_descriptor = BlockDescriptor {
            block_offset: descriptor.block_offset + 1,
            ..descriptor
        };
        let other_cursor = OwnedSegmentBlockCursor::decode(bytes, &other_descriptor, &limits)
            .expect("same block has a different run offset");
        assert_eq!(other_cursor.value_bytes(range), None);

        let out_of_bounds = super::SegmentValueRange {
            identity: cursor.descriptor,
            start: usize::MAX,
            end: usize::MAX,
        };
        assert_eq!(cursor.value_bytes(out_of_bounds), None);
    }
}
