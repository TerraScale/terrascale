//! Host-wide read-only catchup evidence from the immutable admitted scope set.

use crate::proto::terrabase::v1::MaintenanceServiceCatchUpStatusResponse;

/// Production runtime adapter joining live engine, receipt and backup owners.
/// Implementations must include every database of one immutable assignment,
/// and must not report completeness for a pending or fenced assignment.
pub trait CatchUpSource: Send + Sync {
    /// Reads bounded evidence without initiating replication or backup writes.
    ///
    /// # Errors
    /// Refuses inaccessible source owners or inconsistent assignment evidence.
    fn observe(&self) -> Result<MaintenanceServiceCatchUpStatusResponse, String>;
}
