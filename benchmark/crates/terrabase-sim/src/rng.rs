//! Seeded RNG for the simulation harness.
//!
//! Every nondeterministic choice in a scenario — workload shape, fault
//! schedule, delivery jitter — derives from one `u64` seed, so a failing
//! nightly run reproduces exactly from its seed. Built on the audited
//! `ChaCha8` generator behind the `rand` facade (per the repo rule to prefer
//! tested crates over hand-rolled randomness).

use rand::SeedableRng as _;
use rand::seq::{IndexedRandom as _, SliceRandom as _};
use rand::{Rng as _, RngCore as _};
use rand_chacha::ChaCha8Rng;

/// Deterministic random source for one simulation scenario.
///
/// Wraps `ChaCha8` seeded from a single `u64`. `Clone` replays the same
/// stream, which is how the seed batch checks determinism: two runs from
/// the same seed must produce byte-identical event logs.
#[derive(Clone, Debug)]
pub struct SimRng {
    /// The seed this stream derives from, kept for failure reports.
    seed: u64,
    /// The underlying generator.
    inner: ChaCha8Rng,
}

impl SimRng {
    /// Creates the deterministic stream for `seed`.
    #[must_use]
    pub fn new(seed: u64) -> Self {
        Self { seed, inner: ChaCha8Rng::seed_from_u64(seed) }
    }

    /// Returns the seed this stream derives from.
    #[must_use]
    pub fn seed(&self) -> u64 {
        self.seed
    }

    /// Returns a uniform `u64`.
    pub fn next_u64(&mut self) -> u64 {
        self.inner.next_u64()
    }

    /// Returns a uniform value in `0..bound`; returns `0` when `bound` is `0`.
    pub fn below(&mut self, bound: u64) -> u64 {
        if bound == 0 {
            return 0;
        }
        self.inner.random_range(0..bound)
    }

    /// Returns a uniform value in `low..=high`; returns `low` when `low >= high`.
    pub fn between(&mut self, low: u64, high: u64) -> u64 {
        if low >= high {
            return low;
        }
        self.inner.random_range(low..=high)
    }

    /// Returns `true` with probability `numerator / denominator`.
    pub fn biased(&mut self, numerator: u64, denominator: u64) -> bool {
        if denominator == 0 || numerator == 0 {
            return false;
        }
        if numerator >= denominator {
            return true;
        }
        self.below(denominator) < numerator
    }

    /// Returns a reference to a uniform element of `slice`, or `None` when empty.
    pub fn pick<'slice, Item>(&mut self, slice: &'slice [Item]) -> Option<&'slice Item> {
        slice.choose(&mut self.inner)
    }

    /// Shuffles `items` in place with the `Fisher-Yates` algorithm.
    pub fn shuffle<Item>(&mut self, items: &mut [Item]) {
        items.shuffle(&mut self.inner);
    }
}

/// Expands a base seed into a batch of `count` independent scenario seeds.
///
/// Seed `index` of the batch is `base ^ (index * K)` with an odd constant `K`,
/// so batches never overlap and a single failing seed still reproduces alone
/// via [`SimRng::new`]. Used by the CI seed batch and the nightly sweep.
#[must_use]
pub fn seed_batch(base: u64, count: usize) -> Vec<u64> {
    const STRIDE: u64 = 0x9E37_79B9_7F4A_7C15;
    (0..count).map(|index| base ^ STRIDE.wrapping_mul(index as u64).wrapping_add(1)).collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn same_seed_replays_the_same_stream() {
        let mut first = SimRng::new(42);
        let mut second = SimRng::new(42);
        for _ in 0..64 {
            assert_eq!(first.next_u64(), second.next_u64());
        }
    }

    #[test]
    fn different_seeds_diverge() {
        let mut first = SimRng::new(1);
        let mut second = SimRng::new(2);
        let same = (0..16).all(|_| first.next_u64() == second.next_u64());
        assert!(!same, "independent seeds must diverge");
    }

    #[test]
    fn seed_batch_is_stable_and_unique() {
        let batch = seed_batch(99, 8);
        assert_eq!(batch.len(), 8);
        assert_eq!(batch, seed_batch(99, 8));
        let mut sorted = batch.clone();
        sorted.sort_unstable();
        sorted.dedup();
        assert_eq!(sorted.len(), 8, "batch seeds must not collide");
    }
}
