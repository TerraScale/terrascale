//! Typed query results.
//!
//! A result carries the ordered rows, the number of matched rows before
//! paging, the ordering actually applied, the per-namespace staleness
//! observations, the index generation used, the include materialization,
//! and the typed result statistics. It never carries a storage detail,
//! lock, or raw byte.

use super::includes::IncludeSet;
use super::spec::{IndexNamespace, QueryOrdering};
use super::statistics::QueryStatistics;
use crate::types::{Document, DocumentValue};

/// One typed result row: the matched document and its projected fields.
#[derive(Clone, Debug, PartialEq)]
pub struct QueryRow {
    document: Document,
    projected: Vec<(String, DocumentValue)>,
}

impl QueryRow {
    /// Creates one row from the matched document and its projected fields.
    pub(crate) fn new(document: Document, projected: Vec<(String, DocumentValue)>) -> Self {
        Self { document, projected }
    }

    /// Returns the whole matched document, independent of the projection.
    #[must_use]
    pub fn document(&self) -> &Document {
        &self.document
    }

    /// Returns the projected fields in projection order.
    ///
    /// A whole-document projection lists every top-level field of an object
    /// document. A field projection lists the selected fields that are
    /// present; an absent field is omitted, which stays observable and
    /// distinct from a present null.
    #[must_use]
    pub fn projected(&self) -> &[(String, DocumentValue)] {
        &self.projected
    }
}

/// One namespace/epoch/generation triple as observed or used by a query.
///
/// This is the W3-2 view of a generation. The shared
/// [`IndexGeneration`](crate::types::IndexGeneration) value that read
/// snapshots carry is not yet readable or constructible at this seam; until
/// that integrator action lands, queries report generations through this
/// typed triple and never compare generations across namespaces or epochs.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct ObservedGeneration {
    epoch: u64,
    generation: u64,
}

impl ObservedGeneration {
    /// Creates one observation from its epoch and generation.
    pub(crate) const fn new(epoch: u64, generation: u64) -> Self {
        Self { epoch, generation }
    }

    /// Returns the persisted epoch of the namespace.
    #[must_use]
    pub const fn epoch(self) -> u64 {
        self.epoch
    }

    /// Returns the generation within that epoch.
    #[must_use]
    pub const fn generation(self) -> u64 {
        self.generation
    }
}

/// The staleness observation of one required index namespace.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct StaleIndexObservation {
    namespace: IndexNamespace,
    observed: Option<ObservedGeneration>,
    used: Option<ObservedGeneration>,
    stale: bool,
}

impl StaleIndexObservation {
    /// Creates one observation.
    pub(crate) const fn new(
        namespace: IndexNamespace,
        observed: Option<ObservedGeneration>,
        used: Option<ObservedGeneration>,
        stale: bool,
    ) -> Self {
        Self { namespace, observed, used, stale }
    }

    /// Returns the observed namespace.
    #[must_use]
    pub const fn namespace(&self) -> &IndexNamespace {
        &self.namespace
    }

    /// Returns the generation the snapshot observed for the namespace, when
    /// one was observable.
    #[must_use]
    pub const fn observed(&self) -> Option<ObservedGeneration> {
        self.observed
    }

    /// Returns the generation execution accepted for the namespace.
    ///
    /// This is the generation the staleness gate let through, not a claim
    /// that index data served the query: W3-2 execution is index-less, and no
    /// segment content is consulted. Under an allow-stale policy the accepted
    /// generation may be older than, absent from, or from a different epoch
    /// than the snapshot's observation.
    #[must_use]
    pub const fn used(&self) -> Option<ObservedGeneration> {
        self.used
    }

    /// Reports whether the used generation is older than, from a different
    /// epoch than, or absent from the snapshot's observation.
    #[must_use]
    pub const fn stale(&self) -> bool {
        self.stale
    }
}

/// The per-namespace staleness report of one executed query.
#[derive(Clone, Debug, Default, Eq, PartialEq)]
pub struct StalenessReport {
    observations: Vec<StaleIndexObservation>,
}

impl StalenessReport {
    /// Creates a report from its observations.
    pub(crate) fn new(observations: Vec<StaleIndexObservation>) -> Self {
        Self { observations }
    }

    /// Returns every observation in plan order.
    #[must_use]
    pub fn observations(&self) -> &[StaleIndexObservation] {
        &self.observations
    }

    /// Returns the observation of one namespace, when required by the plan.
    #[must_use]
    pub fn observation_for(&self, namespace: &IndexNamespace) -> Option<&StaleIndexObservation> {
        self.observations.iter().find(|observation| observation.namespace() == namespace)
    }

    /// Reports whether any required namespace was answered from a stale
    /// generation.
    ///
    /// This is the aggregate form of V1's `IsStale` flag. An index-less
    /// query — one whose plan required no namespace — has an empty report,
    /// has nothing to be stale about, and reports `false`, exactly as V1's
    /// collection queries reading live storage do.
    #[must_use]
    pub fn is_stale(&self) -> bool {
        self.observations.iter().any(StaleIndexObservation::stale)
    }
}

/// The namespace/epoch/generation of the index a query used.
///
/// This is the primary generation of the query: the generation the staleness
/// gate accepted for the first namespace the plan required. It records which
/// generation the gate allowed through, not which segment served the query —
/// W3-2 execution is index-less and consults no index data. An index-less
/// query reports `None`.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct IndexGenerationRef {
    namespace: IndexNamespace,
    observed: ObservedGeneration,
}

impl IndexGenerationRef {
    /// Creates one reference.
    pub(crate) const fn new(namespace: IndexNamespace, observed: ObservedGeneration) -> Self {
        Self { namespace, observed }
    }

    /// Returns the namespace of the index the gate selected.
    #[must_use]
    pub const fn namespace(&self) -> &IndexNamespace {
        &self.namespace
    }

    /// Returns the epoch and generation the gate accepted.
    #[must_use]
    pub const fn generation(&self) -> ObservedGeneration {
        self.observed
    }
}

/// A typed query result.
#[derive(Clone, Debug, PartialEq)]
pub struct QueryResult {
    rows: Vec<QueryRow>,
    matched: u64,
    ordering: QueryOrdering,
    staleness: StalenessReport,
    index_generation: Option<IndexGenerationRef>,
    includes: IncludeSet,
    statistics: QueryStatistics,
}

impl QueryResult {
    /// Creates one result from its typed parts.
    pub(crate) fn new(
        rows: Vec<QueryRow>,
        matched: u64,
        ordering: QueryOrdering,
        staleness: StalenessReport,
        index_generation: Option<IndexGenerationRef>,
        includes: IncludeSet,
        statistics: QueryStatistics,
    ) -> Self {
        Self { rows, matched, ordering, staleness, index_generation, includes, statistics }
    }

    /// Returns the result rows in result order.
    #[must_use]
    pub fn rows(&self) -> &[QueryRow] {
        &self.rows
    }

    /// Returns the number of rows the predicate matched before paging.
    #[must_use]
    pub const fn matched(&self) -> u64 {
        self.matched
    }

    /// Returns the ordering the result is in.
    #[must_use]
    pub fn ordering(&self) -> &QueryOrdering {
        &self.ordering
    }

    /// Returns the per-namespace staleness report.
    #[must_use]
    pub const fn staleness(&self) -> &StalenessReport {
        &self.staleness
    }

    /// Returns the generation the staleness gate accepted for this query, or
    /// `None` for an index-less execution. The value names the accepted
    /// generation; it does not claim index data served the query.
    #[must_use]
    pub const fn index_generation(&self) -> Option<&IndexGenerationRef> {
        self.index_generation.as_ref()
    }

    /// Returns the include materialization of this result.
    ///
    /// The set is materialized from exactly the rows this result carries,
    /// so paging an include-bearing query pages its includes: the second
    /// page materializes the includes of the second page's rows, not of the
    /// whole matched set. A query without include clauses carries an empty
    /// set.
    #[must_use]
    pub const fn includes(&self) -> &IncludeSet {
        &self.includes
    }

    /// Returns the typed result-metadata of this execution: the scan
    /// accounting, the capped matched count, the state token, and the
    /// diagnostic duration.
    ///
    /// The counts are exact and carry no marker values, and the duration is
    /// diagnostic-only; see [`QueryStatistics`] for what each field is and
    /// is not.
    #[must_use]
    pub const fn statistics(&self) -> &QueryStatistics {
        &self.statistics
    }
}
