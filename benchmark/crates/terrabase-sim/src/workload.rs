//! Deterministic client-operation generator for the harness.
//!
//! A [`Workload`] is the client side of a scenario: a fixed sequence of
//! writes, deletes, and reads spread over the regions. Derived entirely
//! from the scenario seed, so replays are exact.

use crate::model::RegionId;
use crate::rng::SimRng;

/// One client operation in a scenario.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Op {
    /// A document write at `region`.
    Write {
        /// The region receiving the write.
        region: RegionId,
        /// The document key.
        key: u64,
        /// The document value.
        value: u64,
    },
    /// A document delete at `region`.
    Delete {
        /// The region receiving the delete.
        region: RegionId,
        /// The document key.
        key: u64,
    },
    /// A point read at `region` (checks session guarantees, invariant 5).
    Read {
        /// The region serving the read.
        region: RegionId,
        /// The document key.
        key: u64,
    },
}

/// A fixed, seeded sequence of client operations.
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct Workload {
    /// The operations in execution order.
    ops: Vec<Op>,
}

impl Workload {
    /// Creates an empty workload.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Generates `len` operations over `key_space` keys across `regions`.
    ///
    /// The mix is write-heavy (6 writes : 2 deletes : 2 reads), matching a
    /// replication-qualification profile: reads exist to exercise session
    /// tokens, not to dominate the run.
    #[must_use]
    pub fn generate(rng: &mut SimRng, regions: &[RegionId], len: usize, key_space: u64) -> Self {
        let mut ops = Vec::with_capacity(len);
        let keys = key_space.max(1);
        for _ in 0..len {
            let Some(region) = rng.pick(regions).copied() else {
                break;
            };
            let key = rng.below(keys);
            let choice = rng.below(10);
            if choice < 6 {
                ops.push(Op::Write { region, key, value: rng.next_u64() });
            } else if choice < 8 {
                ops.push(Op::Delete { region, key });
            } else {
                ops.push(Op::Read { region, key });
            }
        }
        Self { ops }
    }

    /// Returns the operations in execution order.
    #[must_use]
    pub fn ops(&self) -> &[Op] {
        &self.ops
    }

    /// Returns the number of operations.
    #[must_use]
    pub fn len(&self) -> usize {
        self.ops.len()
    }

    /// Returns `true` when the workload holds no operations.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.ops.is_empty()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn generation_is_deterministic() {
        let regions = [RegionId::new(0), RegionId::new(1)];
        let mut first = SimRng::new(11);
        let mut second = SimRng::new(11);
        assert_eq!(
            Workload::generate(&mut first, &regions, 50, 8),
            Workload::generate(&mut second, &regions, 50, 8)
        );
    }

    #[test]
    fn keys_stay_inside_the_key_space() {
        let regions = [RegionId::new(0)];
        let mut rng = SimRng::new(3);
        let workload = Workload::generate(&mut rng, &regions, 200, 8);
        assert_eq!(workload.len(), 200);
        for op in workload.ops() {
            let key = match op {
                Op::Write { key, .. } | Op::Delete { key, .. } | Op::Read { key, .. } => *key,
            };
            assert!(key < 8, "key {key} escapes the key space");
        }
    }
}
