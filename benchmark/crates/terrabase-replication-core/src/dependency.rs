//! Bounded causal dependency queue for the cross-region journal tailer.
//!
//! ADR 0044 §2.2 (GOAL D-37): a receiver applies a remote row only when
//! every dot in its causal context is already applied locally (the COPS
//! dependency check). Rows that arrive before their dependencies wait in a
//! bounded per-lane queue; the queue overflowing triggers anti-entropy
//! against the missing origin, never a skip.
//!
//! This module is the transport- and storage-independent kernel of that
//! queue: FIFO identities with duplicate coalescing and a hard capacity
//! bound. Readiness itself stays with the caller — it owns the applied
//! frontier and decides when a queued identity's context is covered — so
//! this type never stores row bytes, only the stable
//! [`JournalRowId`](super::anti_entropy::JournalRowId) identities waiting
//! for their dependencies. A push beyond capacity fails with the existing
//! [`RepairError::QueueFull`](super::anti_entropy::RepairError), which is
//! the caller's signal to schedule reconciliation rather than drop or skip
//! the row.

use std::collections::{BTreeSet, VecDeque};

use super::anti_entropy::{JournalRowId, RepairError};

/// The default bound on rows waiting for causal dependencies in one lane.
pub const DEFAULT_MAX_DEPENDENCY_QUEUE: usize = 1024;

/// FIFO identities waiting on causal dependencies, bounded and coalescing.
///
/// A repeated identity replays idempotently: pushing an identity that is
/// already queued reports `false` and keeps the original position, so
/// duplicate delivery never grows the queue and never reorders it.
#[derive(Clone, Debug)]
pub struct DependencyQueue {
    pending: VecDeque<JournalRowId>,
    index: BTreeSet<JournalRowId>,
    capacity: usize,
}

impl DependencyQueue {
    /// Creates an empty queue with an item bound.
    ///
    /// # Errors
    ///
    /// Returns [`RepairError::InvalidBounds`] for zero capacity.
    pub fn new(capacity: usize) -> Result<Self, RepairError> {
        if capacity == 0 {
            return Err(RepairError::InvalidBounds);
        }
        Ok(Self { pending: VecDeque::new(), index: BTreeSet::new(), capacity })
    }

    /// Creates an empty queue with [`DEFAULT_MAX_DEPENDENCY_QUEUE`].
    #[must_use]
    pub fn with_default_capacity() -> Self {
        Self {
            pending: VecDeque::new(),
            index: BTreeSet::new(),
            capacity: DEFAULT_MAX_DEPENDENCY_QUEUE,
        }
    }

    /// Queues one identity that is waiting on its causal dependencies.
    ///
    /// Returns `true` when the identity was newly queued and `false` when
    /// it was already waiting (a duplicate delivery, coalesced in place).
    ///
    /// # Errors
    ///
    /// Returns [`RepairError::QueueFull`] when a new identity would exceed
    /// capacity. The caller treats this as the reconciliation signal: run
    /// range-based set reconciliation against the missing origin, never
    /// skip the row.
    pub fn push(&mut self, id: JournalRowId) -> Result<bool, RepairError> {
        if self.index.contains(&id) {
            return Ok(false);
        }
        if self.pending.len() >= self.capacity {
            return Err(RepairError::QueueFull);
        }
        self.index.insert(id.clone());
        self.pending.push_back(id);
        Ok(true)
    }

    /// Pops the oldest waiting identity.
    pub fn pop(&mut self) -> Option<JournalRowId> {
        let id = self.pending.pop_front()?;
        self.index.remove(&id);
        Some(id)
    }

    /// Reports whether the identity is currently waiting.
    #[must_use]
    pub fn contains(&self, id: &JournalRowId) -> bool {
        self.index.contains(id)
    }

    /// Returns the number of waiting identities.
    #[must_use]
    pub fn len(&self) -> usize {
        self.pending.len()
    }

    /// Reports whether no identity is waiting.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.pending.is_empty()
    }

    /// Returns the configured capacity bound.
    #[must_use]
    pub const fn capacity(&self) -> usize {
        self.capacity
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn id(origin: &str, sequence: u64) -> JournalRowId {
        JournalRowId::new(origin, sequence).expect("valid test identity")
    }

    #[test]
    fn zero_capacity_is_rejected() {
        assert_eq!(DependencyQueue::new(0).unwrap_err(), RepairError::InvalidBounds);
    }

    #[test]
    fn duplicates_coalesce_without_growing_the_queue() {
        let mut queue = DependencyQueue::new(2).expect("valid bound");
        assert_eq!(queue.push(id("gru", 1)), Ok(true));
        assert_eq!(queue.push(id("gru", 1)), Ok(false));
        assert_eq!(queue.len(), 1);
        assert_eq!(queue.pop(), Some(id("gru", 1)));
        assert!(queue.is_empty());
    }

    #[test]
    fn overflow_signals_reconciliation_instead_of_a_skip() {
        let mut queue = DependencyQueue::new(1).expect("valid bound");
        assert_eq!(queue.push(id("gru", 1)), Ok(true));
        assert_eq!(queue.push(id("gru", 2)), Err(RepairError::QueueFull));
        assert!(queue.contains(&id("gru", 1)));
        assert_eq!(queue.len(), 1);
    }
}
