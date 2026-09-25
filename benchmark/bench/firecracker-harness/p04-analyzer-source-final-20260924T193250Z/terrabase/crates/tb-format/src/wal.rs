use crate::{FormatError, FormatLimits, checked_u32, limit};
use tb_core::{MAX_WAL_OPERATIONS, TransactionId};

const MAGIC: &[u8; 8] = b"TBWAL001";
const COMMIT_MARKER: &[u8; 4] = b"TBOK";
const MAJOR: u16 = 1;
const HEADER_LEN: usize = 52;
const TRAILER_LEN: usize = 8;
const MIN_FRAME_LEN: usize = HEADER_LEN + TRAILER_LEN;
const FORMAT: &str = "WAL frame";

/// One operation in an atomic WAL batch.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum WalOperation<'a> {
    /// Set a key to the supplied opaque value.
    Put { key: &'a [u8], value: &'a [u8] },
    /// Delete a key.
    Delete { key: &'a [u8] },
}

impl<'a> WalOperation<'a> {
    fn key(&self) -> &'a [u8] {
        match self {
            Self::Put { key, .. } | Self::Delete { key } => key,
        }
    }
}

/// A validated view of one complete WAL frame.
///
/// The frame's operations remain borrowed from the input. The full operation
/// list, checksum, marker, and duplicate-key rule are checked before this view
/// is returned, so callers can apply the frame atomically after validation.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct WalFrame<'a> {
    sequence: u64,
    transaction_id: TransactionId,
    operation_count: usize,
    operations: &'a [u8],
}

impl<'a> WalFrame<'a> {
    /// Shard-local commit sequence stored in the frame.
    #[must_use]
    pub const fn sequence(&self) -> u64 {
        self.sequence
    }

    /// Transaction identity stored in the frame.
    #[must_use]
    pub const fn transaction_id(&self) -> TransactionId {
        self.transaction_id
    }

    /// Number of operations in the atomic batch.
    #[must_use]
    pub const fn operation_count(&self) -> usize {
        self.operation_count
    }

    /// Iterate over the already validated operations without allocating.
    #[must_use]
    pub fn operations(&self) -> WalOperationIter<'a> {
        WalOperationIter {
            bytes: self.operations,
            position: 0,
            remaining: self.operation_count,
            failed: false,
        }
    }
}

/// Borrowing iterator over operations in a validated WAL batch.
#[derive(Clone, Debug)]
pub struct WalOperationIter<'a> {
    bytes: &'a [u8],
    position: usize,
    remaining: usize,
    failed: bool,
}

impl<'a> Iterator for WalOperationIter<'a> {
    type Item = Result<WalOperation<'a>, FormatError>;

    fn next(&mut self) -> Option<Self::Item> {
        if self.remaining == 0 || self.failed {
            return None;
        }
        match parse_operation(self.bytes, self.position) {
            Ok((operation, next_position)) => {
                self.position = next_position;
                self.remaining -= 1;
                Some(Ok(operation))
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

impl ExactSizeIterator for WalOperationIter<'_> {}

/// Calculate a frame's declared length from a complete fixed header.
///
/// This lets streaming WAL recovery distinguish an incomplete tail from a
/// complete invalid frame before it allocates a frame buffer. The returned
/// size includes the fixed header, payload, marker, and checksum.
///
/// # Errors
///
/// Returns an error if the header is truncated, malformed, unsupported,
/// declares an invalid operation count, or exceeds configured frame limits.
pub fn wal_frame_len(header: &[u8], limits: &FormatLimits) -> Result<usize, FormatError> {
    let limits = limits.effective();
    if header.len() < HEADER_LEN {
        return Err(FormatError::Truncated {
            format: FORMAT,
            expected: HEADER_LEN,
            actual: header.len(),
        });
    }
    if &header[..8] != MAGIC {
        return Err(invalid("bad magic"));
    }
    let major = read_u16(header, 8)?;
    if major != MAJOR {
        return Err(FormatError::UnsupportedVersion {
            format: FORMAT,
            major,
            minor: 0,
        });
    }
    if read_u16(header, 10)? != 0 {
        return Err(invalid("unknown flags"));
    }
    let declared = read_u32(header, 12)? as usize;
    if declared < MIN_FRAME_LEN {
        return Err(invalid("declared frame is shorter than its fixed fields"));
    }
    limit("WAL frame", declared, limits.max_frame_bytes)?;
    let operation_count = read_u32(header, 48)? as usize;
    if operation_count == 0 {
        return Err(invalid("empty batches are not valid"));
    }
    limit(
        "WAL operation count",
        operation_count,
        limits.max_operations,
    )?;
    Ok(declared)
}

/// Decode and fully validate one complete WAL frame without allocating.
///
/// Input must contain exactly one frame. A shorter input returns a truncated
/// error, while extra bytes are rejected so a caller cannot accidentally parse
/// an ambiguous prefix.
///
/// # Errors
///
/// Returns an error if the frame is truncated, malformed, unsupported,
/// checksum-invalid, contains duplicate or invalid operations, or exceeds
/// configured limits.
pub fn decode_wal_frame<'a>(
    bytes: &'a [u8],
    limits: &FormatLimits,
) -> Result<WalFrame<'a>, FormatError> {
    if bytes.len() < HEADER_LEN {
        return Err(FormatError::Truncated {
            format: FORMAT,
            expected: HEADER_LEN,
            actual: bytes.len(),
        });
    }
    let declared_len = wal_frame_len(bytes, limits)?;
    if bytes.len() < declared_len {
        return Err(FormatError::Truncated {
            format: FORMAT,
            expected: declared_len,
            actual: bytes.len(),
        });
    }
    if bytes.len() != declared_len {
        return Err(invalid("input contains bytes after the declared frame"));
    }
    if bytes[declared_len - TRAILER_LEN..declared_len - 4] != COMMIT_MARKER[..] {
        return Err(FormatError::InvalidCommitMarker);
    }
    let expected_crc = read_u32(bytes, declared_len - 4)?;
    if crc32c::crc32c(&bytes[..declared_len - 4]) != expected_crc {
        return Err(FormatError::ChecksumMismatch { format: FORMAT });
    }

    let operation_count = read_u32(bytes, 48)? as usize;
    let sequence = read_u64(bytes, 16)?;
    let nonce = bytes[32..48]
        .try_into()
        .map_err(|_| invalid("transaction nonce has an invalid width"))?;
    let transaction_id = TransactionId::new(read_u64(bytes, 24)?, nonce);
    let operations = &bytes[HEADER_LEN..declared_len - TRAILER_LEN];
    validate_operations(operations, operation_count, &limits.effective())?;

    Ok(WalFrame {
        sequence,
        transaction_id,
        operation_count,
        operations,
    })
}

/// Encode one atomic batch into a caller-owned buffer.
///
/// The exact frame size and every operation are validated before the buffer is
/// changed. The caller must reserve its memory budget before calling and keep
/// that reservation for the returned bytes' full lifetime.
///
/// # Errors
///
/// Returns an error if operations are invalid, duplicate keys occur, a frame
/// or operation exceeds configured limits, a wire length overflows, or the
/// output buffer cannot reserve its capacity.
pub fn encode_wal_frame_into(
    output: &mut Vec<u8>,
    sequence: u64,
    transaction_id: TransactionId,
    operations: &[WalOperation<'_>],
    limits: &FormatLimits,
) -> Result<(), FormatError> {
    let limits = limits.effective();
    if operations.is_empty() {
        return Err(invalid("empty batches are not valid"));
    }
    limit(
        "WAL operation count",
        operations.len(),
        limits.max_operations,
    )?;

    let mut operations_len = 0usize;
    let mut keys: [&[u8]; MAX_WAL_OPERATIONS] = [&[]; MAX_WAL_OPERATIONS];
    for (index, operation) in operations.iter().enumerate() {
        let key = operation.key();
        if key.is_empty() {
            return Err(invalid("keys must not be empty"));
        }
        limit("key", key.len(), limits.max_key_bytes)?;
        let value_len = match operation {
            WalOperation::Put { value, .. } => {
                limit("value", value.len(), limits.max_value_bytes)?;
                value.len()
            }
            WalOperation::Delete { .. } => 0,
        };
        keys[index] = key;
        operations_len = operations_len
            .checked_add(9)
            .and_then(|length| length.checked_add(key.len()))
            .and_then(|length| length.checked_add(value_len))
            .ok_or_else(|| invalid("operation lengths overflow"))?;
    }
    keys[..operations.len()].sort_unstable();
    if keys[..operations.len()]
        .windows(2)
        .any(|pair| pair[0] == pair[1])
    {
        return Err(invalid("a batch cannot mutate the same key more than once"));
    }
    let total_len = HEADER_LEN
        .checked_add(operations_len)
        .and_then(|length| length.checked_add(TRAILER_LEN))
        .ok_or_else(|| invalid("frame length overflows"))?;
    limit("WAL frame", total_len, limits.max_frame_bytes)?;
    let total_len_u32 = checked_u32(total_len, "WAL frame")?;

    output.clear();
    output
        .try_reserve_exact(total_len)
        .map_err(|_| FormatError::Allocation)?;
    output.extend_from_slice(MAGIC);
    push_u16(output, MAJOR);
    push_u16(output, 0);
    push_u32(output, total_len_u32);
    push_u64(output, sequence);
    push_u64(output, transaction_id.expires_at_unix_ms());
    output.extend_from_slice(&transaction_id.nonce());
    push_u32(
        output,
        checked_u32(operations.len(), "WAL operation count")?,
    );
    for operation in operations {
        match operation {
            WalOperation::Put { key, value } => {
                output.push(0);
                push_u32(output, checked_u32(key.len(), "key")?);
                push_u32(output, checked_u32(value.len(), "value")?);
                output.extend_from_slice(key);
                output.extend_from_slice(value);
            }
            WalOperation::Delete { key } => {
                output.push(1);
                push_u32(output, checked_u32(key.len(), "key")?);
                push_u32(output, 0);
                output.extend_from_slice(key);
            }
        }
    }
    output.extend_from_slice(COMMIT_MARKER);
    let checksum = crc32c::crc32c(output);
    push_u32(output, checksum);
    debug_assert_eq!(output.len(), total_len);
    Ok(())
}

fn validate_operations(
    bytes: &[u8],
    count: usize,
    limits: &FormatLimits,
) -> Result<(), FormatError> {
    let limits = limits.effective();
    let mut keys: [&[u8]; MAX_WAL_OPERATIONS] = [&[]; MAX_WAL_OPERATIONS];
    let mut position = 0usize;
    for key_slot in keys.iter_mut().take(count) {
        let (operation, next) = parse_operation(bytes, position)?;
        limit("key", operation.key().len(), limits.max_key_bytes)?;
        if operation.key().is_empty() {
            return Err(invalid("keys must not be empty"));
        }
        if let WalOperation::Put { value, .. } = operation {
            limit("value", value.len(), limits.max_value_bytes)?;
        }
        *key_slot = operation.key();
        position = next;
    }
    if position != bytes.len() {
        return Err(invalid(
            "operation count does not consume the complete payload",
        ));
    }
    keys[..count].sort_unstable();
    if keys[..count].windows(2).any(|pair| pair[0] == pair[1]) {
        return Err(invalid("a batch mutates the same key more than once"));
    }
    Ok(())
}

fn parse_operation(
    bytes: &[u8],
    position: usize,
) -> Result<(WalOperation<'_>, usize), FormatError> {
    let fixed_end = position
        .checked_add(9)
        .ok_or_else(|| invalid("operation offset overflows"))?;
    if fixed_end > bytes.len() {
        return Err(FormatError::Truncated {
            format: FORMAT,
            expected: fixed_end,
            actual: bytes.len(),
        });
    }
    let tag = bytes[position];
    let key_len = read_u32(bytes, position + 1)? as usize;
    let value_len = read_u32(bytes, position + 5)? as usize;
    if key_len == 0 {
        return Err(invalid("keys must not be empty"));
    }
    if tag > 1 || (tag == 1 && value_len != 0) {
        return Err(invalid(
            "unknown operation tag or invalid delete value length",
        ));
    }
    let payload_len = key_len
        .checked_add(value_len)
        .ok_or_else(|| invalid("operation payload length overflows"))?;
    let end = fixed_end
        .checked_add(payload_len)
        .ok_or_else(|| invalid("operation end offset overflows"))?;
    if end > bytes.len() {
        return Err(FormatError::Truncated {
            format: FORMAT,
            expected: end,
            actual: bytes.len(),
        });
    }
    let key = &bytes[fixed_end..fixed_end + key_len];
    let operation = if tag == 0 {
        let value = &bytes[fixed_end + key_len..end];
        WalOperation::Put { key, value }
    } else {
        WalOperation::Delete { key }
    };
    Ok((operation, end))
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

#[cfg(test)]
mod tests {
    use super::{
        FORMAT, HEADER_LEN, MAGIC, MAJOR, WalOperation, decode_wal_frame, encode_wal_frame_into,
        wal_frame_len,
    };
    use crate::{FormatError, FormatLimits, TransactionId};

    #[test]
    fn wal_round_trip_keeps_atomic_operations_and_transaction_identity() {
        let transaction_id = TransactionId::new(1_800_000_000_123, [0x5a; 16]);
        let operations = [
            WalOperation::Put {
                key: b"alpha",
                value: b"first",
            },
            WalOperation::Delete { key: b"beta" },
        ];
        let mut encoded = Vec::new();
        encode_wal_frame_into(
            &mut encoded,
            42,
            transaction_id,
            &operations,
            &FormatLimits::default(),
        )
        .expect("valid bounded batch encodes");

        assert_eq!(
            wal_frame_len(&encoded, &FormatLimits::default()),
            Ok(encoded.len())
        );
        let frame =
            decode_wal_frame(&encoded, &FormatLimits::default()).expect("encoded frame decodes");
        assert_eq!(frame.sequence(), 42);
        assert_eq!(frame.transaction_id(), transaction_id);
        assert_eq!(frame.operation_count(), 2);
        let decoded = frame
            .operations()
            .collect::<Result<Vec<_>, _>>()
            .expect("validated operations iterate");
        assert_eq!(decoded, operations);
    }

    #[test]
    fn wal_rejects_truncation_marker_crc_duplicates_and_unknown_versions() {
        let operation = [WalOperation::Put {
            key: b"k",
            value: b"v",
        }];
        let mut encoded = Vec::new();
        encode_wal_frame_into(
            &mut encoded,
            1,
            TransactionId::new(100, [7; 16]),
            &operation,
            &FormatLimits::default(),
        )
        .expect("valid batch encodes");

        assert!(matches!(
            decode_wal_frame(&encoded[..encoded.len() - 1], &FormatLimits::default()),
            Err(FormatError::Truncated { .. })
        ));
        let mut bad_marker = encoded.clone();
        let marker_offset = bad_marker.len() - 8;
        bad_marker[marker_offset] ^= 1;
        assert_eq!(
            decode_wal_frame(&bad_marker, &FormatLimits::default()),
            Err(FormatError::InvalidCommitMarker)
        );
        let mut bad_payload = encoded.clone();
        bad_payload[HEADER_LEN + 9] ^= 1;
        assert_eq!(
            decode_wal_frame(&bad_payload, &FormatLimits::default()),
            Err(FormatError::ChecksumMismatch { format: FORMAT })
        );
        encoded[8..10].copy_from_slice(&2u16.to_le_bytes());
        assert!(matches!(
            decode_wal_frame(&encoded, &FormatLimits::default()),
            Err(FormatError::UnsupportedVersion { .. })
        ));

        let duplicate = [
            WalOperation::Delete { key: b"same" },
            WalOperation::Put {
                key: b"same",
                value: b"x",
            },
        ];
        assert!(matches!(
            encode_wal_frame_into(
                &mut Vec::new(),
                2,
                TransactionId::new(100, [8; 16]),
                &duplicate,
                &FormatLimits::default()
            ),
            Err(FormatError::Invalid { .. })
        ));
    }

    #[test]
    fn caller_raised_limits_cannot_raise_v1_hard_caps() {
        let limits = FormatLimits {
            max_frame_bytes: usize::MAX,
            max_operations: usize::MAX,
            max_key_bytes: usize::MAX,
            ..FormatLimits::default()
        };
        let oversized_key = vec![b'x'; 4 * 1024 + 1];
        let operation = [WalOperation::Delete {
            key: &oversized_key,
        }];
        assert!(matches!(
            encode_wal_frame_into(
                &mut Vec::new(),
                1,
                TransactionId::new(100, [1; 16]),
                &operation,
                &limits
            ),
            Err(FormatError::LimitExceeded { what: "key", .. })
        ));

        let mut header = [0u8; HEADER_LEN];
        header[..8].copy_from_slice(MAGIC);
        header[8..10].copy_from_slice(&MAJOR.to_le_bytes());
        header[12..16].copy_from_slice(&((2 * 1024 * 1024 + 1) as u32).to_le_bytes());
        header[48..52].copy_from_slice(&1u32.to_le_bytes());
        assert!(matches!(
            wal_frame_len(&header, &limits),
            Err(FormatError::LimitExceeded {
                what: "WAL frame",
                ..
            })
        ));
    }
}
