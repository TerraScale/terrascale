//! Durable split-record contract.

use super::SplitPhase;

/// Version of the durable split record encoding.
pub const SPLIT_STATE_VERSION: u8 = 1;

/// A storage-neutral, restart-safe snapshot of a split lifecycle.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct DurableSplitState {
    split_key: Vec<u8>,
    source_high_water: u64,
    target_applied_through: u64,
    acked_through: u64,
    phase: SplitPhase,
}

impl DurableSplitState {
    /// Creates a state snapshot. The split key must be non-empty.
    #[must_use]
    pub fn new(
        split_key: Vec<u8>,
        source_high_water: u64,
        target_applied_through: u64,
        acked_through: u64,
        phase: SplitPhase,
    ) -> Self {
        Self { split_key, source_high_water, target_applied_through, acked_through, phase }
    }

    /// Returns the upper-exclusive moved-range bound.
    #[must_use]
    pub fn split_key(&self) -> &[u8] {
        &self.split_key
    }

    /// Returns the source journal high-water mark.
    #[must_use]
    pub const fn source_high_water(&self) -> u64 {
        self.source_high_water
    }

    /// Returns the target's durable applied-through mark.
    #[must_use]
    pub const fn target_applied_through(&self) -> u64 {
        self.target_applied_through
    }

    /// Returns the acknowledged-write frontier.
    #[must_use]
    pub const fn acked_through(&self) -> u64 {
        self.acked_through
    }

    /// Returns the persisted lifecycle phase.
    #[must_use]
    pub const fn phase(&self) -> SplitPhase {
        self.phase
    }

    /// Encodes a compact deterministic record for a SQLite/blob metadata row.
    #[must_use]
    pub fn encode(&self) -> Vec<u8> {
        let key_len = u32::try_from(self.split_key.len()).unwrap_or(u32::MAX);
        let mut bytes = Vec::with_capacity(1 + 4 + self.split_key.len() + 25);
        bytes.push(SPLIT_STATE_VERSION);
        bytes.extend_from_slice(&key_len.to_be_bytes());
        bytes.extend_from_slice(&self.split_key);
        bytes.extend_from_slice(&self.source_high_water.to_be_bytes());
        bytes.extend_from_slice(&self.target_applied_through.to_be_bytes());
        bytes.extend_from_slice(&self.acked_through.to_be_bytes());
        bytes.push(self.phase as u8);
        debug_assert_eq!(bytes.len(), 1 + 4 + self.split_key.len() + 8 + 8 + 8 + 1);
        bytes
    }

    /// Decodes and validates a durable record.
    ///
    /// # Errors
    ///
    /// Returns [`SplitStateError`] for an unsupported version, truncation,
    /// malformed bound, or unknown phase.
    ///
    /// # Panics
    ///
    /// Never panics in practice: every byte-slice conversion is guarded by
    /// the length checks above, so the fallible conversions are infallible
    /// for any input that reaches them.
    pub fn decode(bytes: &[u8]) -> Result<Self, SplitStateError> {
        if bytes.first().copied() != Some(SPLIT_STATE_VERSION) {
            return Err(SplitStateError::UnsupportedVersion(bytes.first().copied()));
        }
        if bytes.len() < 5 {
            return Err(SplitStateError::Truncated);
        }
        let key_len = u32::from_be_bytes(bytes[1..5].try_into().expect("length checked")) as usize;
        // Trailing fixed fields: source (8) + target (8) + acked (8) + phase (1) = 25.
        // The 4-byte key-len prefix lives in bytes[1..5], so it must not be
        // counted again here.
        let tail = 8 + 8 + 8 + 1;
        debug_assert_eq!(tail, 25);
        let end = 5usize.checked_add(key_len).ok_or(SplitStateError::Malformed)?;
        if bytes.len() != end.saturating_add(tail) {
            return Err(SplitStateError::Malformed);
        }
        if key_len == 0 {
            return Err(SplitStateError::EmptySplitKey);
        }
        let key = bytes[5..end].to_vec();
        let source = u64::from_be_bytes(bytes[end..end + 8].try_into().expect("length checked"));
        let target =
            u64::from_be_bytes(bytes[end + 8..end + 16].try_into().expect("length checked"));
        let acked =
            u64::from_be_bytes(bytes[end + 16..end + 24].try_into().expect("length checked"));
        let phase = match bytes[end + 24] {
            0 => SplitPhase::Planned,
            1 => SplitPhase::Backfilling,
            2 => SplitPhase::CatchingUp,
            3 => SplitPhase::CutOver,
            4 => SplitPhase::Complete,
            5 => SplitPhase::Aborted,
            other => return Err(SplitStateError::UnknownPhase(other)),
        };
        Ok(Self::new(key, source, target, acked, phase))
    }
}

/// Failure decoding a durable split record.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum SplitStateError {
    /// The bytes carry an unsupported version.
    UnsupportedVersion(Option<u8>),
    /// The bytes ended before the fixed record fields.
    Truncated,
    /// The record length does not match its key length.
    Malformed,
    /// The split bound is empty.
    EmptySplitKey,
    /// The phase tag is not part of the closed lifecycle vocabulary.
    UnknownPhase(u8),
}

/// Persistence seam for a database's one active split record.
pub trait SplitStateStore {
    /// Storage-specific failure type.
    type Error;

    /// Loads the record, if a split is in progress.
    ///
    /// # Errors
    ///
    /// Returns [`Self::Error`] when the record cannot be read.
    fn load(&self) -> Result<Option<DurableSplitState>, Self::Error>;
    /// Atomically replaces the record with the supplied state.
    ///
    /// # Errors
    ///
    /// Returns [`Self::Error`] when the record cannot be replaced durably.
    fn save(&mut self, state: &DurableSplitState) -> Result<(), Self::Error>;
    /// Removes a terminal record after retention policy permits it.
    ///
    /// # Errors
    ///
    /// Returns [`Self::Error`] when the record cannot be removed.
    fn remove(&mut self) -> Result<(), Self::Error>;
}
