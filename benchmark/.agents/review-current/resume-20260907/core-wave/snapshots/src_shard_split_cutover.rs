//! Writer-drain fence and cutover permit contracts.

use std::sync::atomic::{AtomicU64, Ordering};

// A permit is intentionally tied to one fence instance.  This prevents a
// permit copied from an unrelated split from passing `WriterFence::permits`.
// The value is an opaque capability; it is not a routing generation or a
// persisted sequence number.
static NEXT_FENCE_ID: AtomicU64 = AtomicU64::new(1);

/// State of the source writer fence.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum WriterFenceState {
    /// New writes are accepted.
    Open,
    /// New writes drain while the target catches up.
    Draining,
    /// The source is fenced and routing may be swapped.
    Fenced,
}

/// Why a writer fence operation failed.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum FenceError {
    /// A second drain request was made while one is active.
    AlreadyDraining,
    /// The target or acknowledgements trail the requested sequence.
    NotDrained {
        /// Target sequence applied at the fence attempt.
        target_applied: u64,
        /// Required sequence.
        required: u64,
    },
    /// New writes are not accepted after fencing.
    Fenced,
    /// The operation requires a draining fence.
    WrongState(WriterFenceState),
}

/// An unforgeable-in-practice permit returned only after a successful fence.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct CutoverPermit {
    required: u64,
    fence_id: u64,
}

impl CutoverPermit {
    /// Returns the acknowledged sequence covered by this permit.
    #[must_use]
    pub const fn required(&self) -> u64 {
        self.required
    }

    /// Returns whether this value was issued by a writer fence.
    #[must_use]
    pub(crate) const fn is_valid(self) -> bool {
        self.fence_id != 0
    }
}

/// Monotonic source-writer drain state.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct WriterFence {
    state: WriterFenceState,
    required: u64,
    acked_through: u64,
    fence_id: u64,
}

impl WriterFence {
    /// Creates an open writer fence.
    #[must_use]
    pub fn new() -> Self {
        // Zero is reserved for an invalid permit.  The wrapping fallback is
        // practically unreachable, but keeps construction total even after
        // an exceptionally long-lived process exhausts the counter.
        let id = NEXT_FENCE_ID.fetch_add(1, Ordering::Relaxed);
        Self {
            state: WriterFenceState::Open,
            required: 0,
            acked_through: 0,
            fence_id: if id == 0 { 1 } else { id },
        }
    }

    /// Returns the current fence state.
    #[must_use]
    pub const fn state(&self) -> WriterFenceState {
        self.state
    }

    /// Returns the highest acknowledged source sequence observed.
    #[must_use]
    pub const fn acked_through(&self) -> u64 {
        self.acked_through
    }

    /// Begins draining writes through `required` before cutover.
    pub fn begin_drain(&mut self, required: u64) -> Result<(), FenceError> {
        match self.state {
            WriterFenceState::Open => {
                self.required = self.required.max(required);
                self.state = WriterFenceState::Draining;
                Ok(())
            }
            state => Err(if state == WriterFenceState::Draining {
                FenceError::AlreadyDraining
            } else {
                FenceError::WrongState(state)
            }),
        }
    }

    /// Records an acknowledged write while the source drains.
    pub fn note_acked(&mut self, sequence: u64) -> Result<(), FenceError> {
        if self.state == WriterFenceState::Fenced {
            return Err(FenceError::Fenced);
        }
        self.acked_through = self.acked_through.max(sequence);
        Ok(())
    }

    /// Fences the source only when the target has applied every required ack.
    pub fn try_fence(&mut self, target_applied: u64) -> Result<CutoverPermit, FenceError> {
        if self.state != WriterFenceState::Draining {
            return Err(FenceError::WrongState(self.state));
        }
        let required = self.required.max(self.acked_through);
        if target_applied < required {
            return Err(FenceError::NotDrained { target_applied, required });
        }
        self.state = WriterFenceState::Fenced;
        Ok(CutoverPermit { required, fence_id: self.fence_id })
    }

    /// Checks that a cutover permit still matches this fence.
    #[must_use]
    pub fn permits(&self, permit: CutoverPermit) -> bool {
        self.state == WriterFenceState::Fenced
            && permit.fence_id == self.fence_id
            && permit.required <= self.acked_through.max(self.required)
    }
}

impl Default for WriterFence {
    fn default() -> Self {
        Self::new()
    }
}
