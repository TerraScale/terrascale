//! Durable split-record contract.

use super::SplitPhase;

/// Version of the durable split record encoding.
pub const SPLIT_STATE_VERSION: u8 = 2;
const LEGACY_SPLIT_STATE_VERSION: u8 = 1;

/// Identity and placement facts that must travel with a split.
///
/// A route cutover is only safe for the database incarnation and placement
/// epoch it was planned against.  Keeping these facts in the split record
/// prevents a restarted worker from applying an old split to a recreated
/// database or to a placement that has since moved.  The frontier is the
/// caller's session/read-your-writes frontier at planning time; adapters may
/// require a newer value before installing the route.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct SplitContext {
    split_id: u128,
    database_incarnation: u128,
    source_shard: u64,
    target_shard: u64,
    placement_epoch: u64,
    session_frontier: u64,
}

impl SplitContext {
    /// Creates context for one split operation.
    #[must_use]
    pub const fn new(
        split_id: u128,
        database_incarnation: u128,
        source_shard: u64,
        target_shard: u64,
        placement_epoch: u64,
        session_frontier: u64,
    ) -> Self {
        Self {
            split_id,
            database_incarnation,
            source_shard,
            target_shard,
            placement_epoch,
            session_frontier,
        }
    }

    /// Stable operation identity used for idempotent retries.
    #[must_use]
    pub const fn split_id(self) -> u128 {
        self.split_id
    }

    /// Database incarnation the split was planned against.
    #[must_use]
    pub const fn database_incarnation(self) -> u128 {
        self.database_incarnation
    }

    /// Source shard identifier.
    #[must_use]
    pub const fn source_shard(self) -> u64 {
        self.source_shard
    }

    /// Target shard identifier.
    #[must_use]
    pub const fn target_shard(self) -> u64 {
        self.target_shard
    }

    /// Placement epoch observed while planning.
    #[must_use]
    pub const fn placement_epoch(self) -> u64 {
        self.placement_epoch
    }

    /// Session/read-your-writes frontier observed while planning.
    #[must_use]
    pub const fn session_frontier(self) -> u64 {
        self.session_frontier
    }
}

/// A storage-neutral, restart-safe snapshot of a split lifecycle.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct DurableSplitState {
    split_key: Vec<u8>,
    source_high_water: u64,
    target_applied_through: u64,
    acked_through: u64,
    phase: SplitPhase,
    context: Option<SplitContext>,
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
        Self {
            split_key,
            source_high_water,
            target_applied_through,
            acked_through,
            phase,
            context: None,
        }
    }

    /// Attaches identity, frontier, and placement facts to this state.
    #[must_use]
    pub const fn with_context(mut self, context: SplitContext) -> Self {
        self.context = Some(context);
        self
    }

    /// Returns the operation context, when this is a v2 record.
    #[must_use]
    pub const fn context(&self) -> Option<SplitContext> {
        self.context
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
        let context_len = self.context.map_or(0, |_| 1 + 16 + 16 + 8 + 8 + 8 + 8);
        let version =
            if self.context.is_some() { SPLIT_STATE_VERSION } else { LEGACY_SPLIT_STATE_VERSION };
        let mut bytes = Vec::with_capacity(1 + 4 + self.split_key.len() + 25 + context_len);
        bytes.push(version);
        bytes.extend_from_slice(&key_len.to_be_bytes());
        bytes.extend_from_slice(&self.split_key);
        bytes.extend_from_slice(&self.source_high_water.to_be_bytes());
        bytes.extend_from_slice(&self.target_applied_through.to_be_bytes());
        bytes.extend_from_slice(&self.acked_through.to_be_bytes());
        bytes.push(self.phase as u8);
        if let Some(context) = self.context {
            bytes.push(1);
            bytes.extend_from_slice(&context.split_id.to_be_bytes());
            bytes.extend_from_slice(&context.database_incarnation.to_be_bytes());
            bytes.extend_from_slice(&context.source_shard.to_be_bytes());
            bytes.extend_from_slice(&context.target_shard.to_be_bytes());
            bytes.extend_from_slice(&context.placement_epoch.to_be_bytes());
            bytes.extend_from_slice(&context.session_frontier.to_be_bytes());
        }
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
        let version = bytes.first().copied();
        if !matches!(version, Some(value) if value == LEGACY_SPLIT_STATE_VERSION || value == SPLIT_STATE_VERSION)
        {
            return Err(SplitStateError::UnsupportedVersion(version));
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
        let expected_len =
            end.saturating_add(tail).saturating_add(if version == Some(SPLIT_STATE_VERSION) {
                1 + 16 + 16 + 8 + 8 + 8 + 8
            } else {
                0
            });
        if bytes.len() != expected_len {
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
        let mut state = Self::new(key, source, target, acked, phase);
        if version == Some(SPLIT_STATE_VERSION) {
            let context_start = end + tail;
            let marker = *bytes.get(context_start).ok_or(SplitStateError::Truncated)?;
            if marker != 1 {
                return Err(SplitStateError::Malformed);
            }
            let context_bytes = bytes.get(context_start + 1..).ok_or(SplitStateError::Truncated)?;
            if context_bytes.len() != 16 + 16 + 8 + 8 + 8 + 8 {
                return Err(SplitStateError::Malformed);
            }
            let u128_at = |offset: usize| {
                u128::from_be_bytes(
                    context_bytes[offset..offset + 16].try_into().expect("length checked"),
                )
            };
            let u64_at = |offset: usize| {
                u64::from_be_bytes(
                    context_bytes[offset..offset + 8].try_into().expect("length checked"),
                )
            };
            state.context = Some(SplitContext::new(
                u128_at(0),
                u128_at(16),
                u64_at(32),
                u64_at(40),
                u64_at(48),
                u64_at(56),
            ));
        }
        Ok(state)
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
