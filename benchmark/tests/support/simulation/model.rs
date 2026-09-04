//! Small reference state used to exercise invariant logic.

use std::collections::{BTreeMap, BTreeSet};

/// Region identity in the reference world.
pub type Region = u8;

/// One causal dot.
#[derive(Clone, Copy, Debug, Eq, Ord, PartialEq, PartialOrd)]
pub struct Dot {
    /// Origin region.
    pub origin: Region,
    /// Origin sequence.
    pub sequence: u64,
}

/// One acknowledged publication-journal row.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct JournalRow {
    /// Stable causal identity.
    pub dot: Dot,
    /// Causal dependencies.
    pub context: BTreeSet<Dot>,
    /// Document key.
    pub key: u64,
    /// Value, or tombstone.
    pub value: Option<u64>,
    /// Deterministic HLC value.
    pub hlc: i64,
}

/// Durable reference state for one simulation run.
#[derive(Clone, Debug, Default)]
pub struct ReferenceWorld {
    durable: BTreeMap<Dot, JournalRow>,
    applied: BTreeMap<Region, BTreeMap<u64, JournalRow>>,
    applied_dots: BTreeMap<Region, BTreeSet<Dot>>,
}

impl ReferenceWorld {
    /// Creates an empty world for `regions`.
    #[must_use]
    pub fn new(regions: &[Region]) -> Self {
        let applied = regions.iter().copied().map(|region| (region, BTreeMap::new())).collect();
        let applied_dots =
            regions.iter().copied().map(|region| (region, BTreeSet::new())).collect();
        Self { durable: BTreeMap::new(), applied, applied_dots }
    }

    /// Records an acknowledged row in the durable origin journal.
    pub fn acknowledge(&mut self, row: JournalRow) {
        self.durable.insert(row.dot, row);
    }

    /// Applies a row idempotently using a deterministic winner rule.
    pub fn apply(&mut self, region: Region, row: &JournalRow) {
        let Some(state) = self.applied.get_mut(&region) else { return };
        if let Some(applied) = self.applied_dots.get_mut(&region) {
            applied.insert(row.dot);
        }
        let replace = state
            .get(&row.key)
            .is_none_or(|current| (row.hlc, row.dot) >= (current.hlc, current.dot));
        if replace {
            state.insert(row.key, row.clone());
        }
    }

    /// Returns all acknowledged rows.
    pub fn durable_rows(&self) -> impl Iterator<Item = &JournalRow> {
        self.durable.values()
    }

    /// Returns whether every durable row is applied at `region`.
    #[must_use]
    pub fn has_all_rows(&self, region: Region) -> bool {
        let Some(applied) = self.applied_dots.get(&region) else { return false };
        self.durable.keys().all(|dot| applied.contains(dot))
    }

    /// Returns whether one region has applied one causal dot.
    #[must_use]
    pub fn has_applied(&self, region: Region, dot: Dot) -> bool {
        self.applied_dots.get(&region).is_some_and(|applied| applied.contains(&dot))
    }

    /// Returns a stable digest of one region's visible state.
    #[must_use]
    pub fn digest(&self, region: Region) -> u64 {
        let Some(state) = self.applied.get(&region) else { return 0 };
        state.values().fold(0xcbf2_9ce4_8422_2325, |hash, row| {
            let mut result = hash ^ row.key;
            result = result.wrapping_mul(0x0000_0100_0000_01b3) ^ row.hlc as u64;
            result = result.wrapping_mul(0x0000_0100_0000_01b3) ^ row.dot.origin as u64;
            result.wrapping_mul(0x0000_0100_0000_01b3) ^ row.dot.sequence
        })
    }
}
