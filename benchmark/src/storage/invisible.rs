//! Physical hidden-index build contracts.

/// Scalar representation already maintained by descriptor extraction.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum InvisibleColumnKind {
    /// Stored text scalar.
    Text,
    /// Stored integer or floating-point scalar.
    Numeric,
}

/// One ordered key in a physical candidate.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct InvisibleIndexColumn {
    /// Existing extracted field.
    pub field: String,
    /// Scalar representation used by this candidate.
    pub kind: InvisibleColumnKind,
}

/// Durable build progress. Completion never implies planner visibility.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct InvisibleIndexProgress {
    /// Publication boundary captured when the hidden build started.
    pub target_sequence: u64,
    /// Whether all rows present at the build boundary have been copied.
    pub complete: bool,
}
