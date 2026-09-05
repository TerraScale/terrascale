//! Bounded diagnostic measurements for a generated collection query.

use super::ScanFilter;
use crate::types::{CollectionName, DocumentId};

/// Largest admitted instruction budget for either shadow execution.
pub const MAX_SHADOW_VM_STEPS: u32 = 1_000_000;
/// Largest result page admitted for a shadow comparison.
pub const MAX_SHADOW_ROWS: u32 = 1000;

/// A generated filtered page and one existing extracted-column index to measure.
pub struct ShadowReadRequest<'a> {
    /// Collection owned by the supplied read scope.
    pub collection: &'a CollectionName,
    /// Bounded conjunction of field predicates.
    pub filter: &'a ScanFilter,
    /// Field whose physical text or numeric index is forced for the candidate.
    pub candidate_field: &'a str,
    /// Completed hidden build to measure. `None` measures the field's ordinary index.
    pub hidden_candidate: Option<&'a str>,
    /// Exclusive pagination boundary.
    pub after: Option<&'a DocumentId>,
    /// Maximum number of result rows.
    pub limit: u32,
    /// Absolute VM instruction ceiling, including the ordinary execution.
    pub max_vm_steps: u32,
}

/// Actual `SQLite` statement counters, without inferred page or row costs.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ShadowReadMeasurement {
    /// Progress-handler ticks at an interval of one VM instruction. `SQLite`'s
    /// callback accounting can differ from the statement counter; work limits
    /// compare these ticks consistently on both sides.
    pub progress_steps: u32,
    /// Virtual-machine instructions executed.
    pub vm_steps: u32,
    /// Full table-scan steps, excluding visits through indexes.
    pub full_scan_steps: u32,
    /// Rows in the bounded result page.
    pub rows: u32,
    /// Digest of the ordered result bytes, including identifiers and metadata.
    pub result_digest: [u8; 32],
}

/// An ordinary plan and forced candidate measured at the same `SQLite` snapshot.
/// This diagnostic alone does not authorize index activation.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ShadowReadComparison {
    /// Measurement of the serving SQL without a candidate hint.
    pub ordinary: ShadowReadMeasurement,
    /// Measurement of the same SQL with the candidate index forced.
    pub candidate: ShadowReadMeasurement,
}

/// Why a shadow comparison could not produce admissible measurements.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ShadowReadError {
    /// A scope or snapshot belongs to another engine or tenant.
    ScopeMismatch,
    /// A limit, field, or filter cannot be admitted.
    InvalidRequest,
    /// The instruction or result-byte ceiling was reached.
    BudgetExceeded,
    /// The two plans produced different result bytes.
    ResultMismatch,
    /// `SQLite`, the snapshot, or the requested physical index is unavailable.
    Unavailable,
}
