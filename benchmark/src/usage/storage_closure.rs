//! Frozen storage observations minted from the engine's durable byte timeline.

use super::{MeasurementWindow, UsageScope};

/// Immutable proof of one completed storage measurement, including zero usage.
/// Byte-seconds measure this Region once. Aggregate every admitted Region.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct StorageUsageClosure {
    pub(crate) scope: UsageScope,
    pub(crate) window: MeasurementWindow,
    pub(crate) sequence: u64,
    pub(crate) selected_regions: u32,
    pub(crate) byte_seconds: u128,
    pub(crate) write_units: i128,
    pub(crate) fact_count: u64,
    pub(crate) checksum: [u8; 32],
}

impl StorageUsageClosure {
    /// Authoritative database, partition, and Region identity.
    #[must_use]
    pub const fn scope(&self) -> &UsageScope {
        &self.scope
    }
    /// Exclusive accounting interval.
    #[must_use]
    pub const fn window(&self) -> MeasurementWindow {
        self.window
    }
    /// Frozen durable usage high-water, including the emitted storage fact.
    #[must_use]
    pub const fn sequence(&self) -> u64 {
        self.sequence
    }
    /// Immutable admitted Region count; storage totals are measured locally.
    #[must_use]
    pub const fn selected_regions(&self) -> u32 {
        self.selected_regions
    }
    /// Whole byte-seconds, with fractional byte-milliseconds carried forward.
    #[must_use]
    pub const fn byte_seconds(&self) -> u128 {
        self.byte_seconds
    }
    /// Frozen write units in the same complete interval.
    #[must_use]
    pub const fn write_units(&self) -> i128 {
        self.write_units
    }
    /// Number of canonical ledger facts bound by the checksum.
    #[must_use]
    pub const fn fact_count(&self) -> u64 {
        self.fact_count
    }
    /// BLAKE3 digest binding the scope, interval, sequence and measured total.
    #[must_use]
    pub const fn checksum(&self) -> &[u8; 32] {
        &self.checksum
    }
}
