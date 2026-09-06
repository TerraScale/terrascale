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
    pub(crate) storage_intervals: Vec<StorageUsageInterval>,
}

/// One exact local logical-byte level in a frozen half-open interval.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct StorageUsageInterval {
    pub(crate) start_millis: u64,
    pub(crate) end_millis: u64,
    pub(crate) logical_bytes: u128,
}
impl StorageUsageInterval {
    /// Inclusive start.
    #[must_use]
    pub const fn start_millis(&self) -> u64 {
        self.start_millis
    }
    /// Exclusive end.
    #[must_use]
    pub const fn end_millis(&self) -> u64 {
        self.end_millis
    }
    /// Actual local logical bytes; no Region multiplier.
    #[must_use]
    pub const fn logical_bytes(&self) -> u128 {
        self.logical_bytes
    }
}

impl StorageUsageClosure {
    /// Complete chronological storage levels for organization allowance rating.
    #[must_use]
    pub fn storage_intervals(&self) -> &[StorageUsageInterval] {
        &self.storage_intervals
    }

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
