//! The query optimizer at the query/index seam (IX-02, ADR 0010).
//!
//! This module owns index selection for every **dynamic** query: a plan whose
//! staleness policy names no index. A plan that names an index bypasses the
//! optimizer entirely (V1 AX-26 parity), and a pure collection query — no
//! predicate and no ordering — stays an index-free scan exactly as V1's
//! collection-query runner (AX-1). Every other execution selects the best
//! available index, prefers an explicit index that equivalently serves the
//! query over any auto index (ADR 0010 D-3), and creates a deterministic
//! `Auto/` index when nothing serves the query (D-2). SQL text plans
//! through the same compile seam once E-4 lands, so planned SQL selects
//! through this one path.
//!
//! # Recorded selections (D-1)
//!
//! Every selection produces an [`IndexSelectionRecord`]: the query identity
//! (tenant, collection, demanded fields and capabilities), every candidate
//! considered with its verdict, the tie-break rules that decided, the chosen
//! index or the creation event, whether the plan came from the bounded
//! selection cache, and the staleness basis of the answer. The charter's
//! selection-quality rule is mechanical: a query served by a worse-than-
//! available index always carries the reason in its record, because every
//! unchosen complete candidate records why it lost. Records are bounded
//! engine state ([`MAX_SELECTION_RECORDS`]) and readable through the typed
//! `QueryStore` reads — they are engine state, not a log
//! line and not a debug flag.
//!
//! # Automatic creation (D-2)
//!
//! The auto-index definition derives from the query shape: where-clause
//! fields in declaration order, then order-by fields, one canonical ordinal
//! order. The name is deterministic — `Auto/{Collection}/By{Field1}And…`,
//! truncated with an ellipsis and an FNV-1a 64 digest when it would exceed
//! the namespace name bound — so the same shape always maps to the same
//! identity, on this engine and on any other. Auto fields carry the full
//! W3-2 capability set (equality and ordering), which makes the capability
//! component of the identity constant for auto indexes; explicit definitions
//! may restrict capabilities per field. Creation is fail-closed: the
//! reserved `Auto/` namespace refuses explicit registration, an existing
//! namespace that squats a deterministic auto name fails closed instead of
//! being reused or overwritten, and the engine kill switch (creation
//! disabled) makes an unmatched dynamic query fail with a typed
//! [`QueryError::IndexNotReady`] — never a
//! silent scan — while a query an existing index serves is unaffected.
//!
//! # Bounded first-query behavior
//!
//! The query that triggers a creation runs to its full correct answer through
//! the bounded snapshot scan while the index is created: the catalog
//! registers the definition and performs one initial build from the latest
//! observed publication (deferred when none was observed). The record marks
//! the outcome as a creation, so the first-query basis is observable. V1's
//! bounded non-stale wait for a fresh auto index (AX-23) has no observable
//! effect on this execution path: results never depend on index data, so
//! there is nothing to wait for; the wait default is engine configuration
//! recorded when an index-dependent execution path exists, not a constant
//! introduced speculatively here.
//!
//! Only the pass whose registration created the namespace runs the initial
//! build. Concurrent queries racing one cold shape lose the registration
//! race, select the existing namespace as their serving index, and build
//! nothing — one cold shape costs one build however many queries arrive
//! together. That leaves one deliberate gap: a namespace whose creator has
//! not published yet — still building, or dead before it could — is
//! registered but carries no segment, so those concurrent selections serve
//! through the bounded scan (correct, only not accelerated). The gap is
//! closed by the next lifecycle pass, whose coverage catch-up rebuilds every
//! reported namespace that trails the latest publication; see
//! `lifecycle`.
//!
//! # Selection order (AX-13, adapted)
//!
//! Among complete candidates: an equivalent explicit index takes precedence
//! over an auto index (D-3), then the most up-to-date candidate (highest
//! covered storage sequence of its newest published segment), then the
//! non-idle one at equal recency, then the widest (most fields); every
//! applied rule is recorded. An idle candidate stays selectable with a
//! complete-but-idle verdict, demoted only at equal recency, and choosing
//! one wakes it synchronously (AX-22). The bounded selection cache (AX-2,
//! adapted) remembers the chosen index per query shape and is invalidated
//! whenever catalog truth changes, and a cache hit still counts as a
//! selection with its origin recorded.
//!
//! # The automatic lifecycle (IX-03, ADR 0010 D-4)
//!
//! One maintenance pass (`lifecycle::maintain`, the crate-private entry in
//! this module's `lifecycle` submodule) runs at the head of every
//! dynamic query's optimizer pass, so selection decides against
//! post-maintenance truth and one public-seam query drives the whole state
//! machine: a namespace whose published segment trails the latest
//! publication is caught up first, one rebuild per namespace per pass, so
//! one write never strands an Active index at stale coverage; a partial auto
//! match widens the mapping into the covering union
//! identity and supersedes the covered partials (AX-16); a background
//! surpass pass retires auto indexes another auto index of the same
//! collection completely serves and extends partially served ones into the
//! union, one extension per pass (AX-18); an auto index unqueried past the
//! idle threshold is demoted but stays selectable, and one still unqueried
//! past the deletion threshold is deleted (AX-21). Every deletion is
//! drain-waited behind replacement coverage parity (AX-17), no transition
//! issues while the usage report is missing, every transition records its
//! measured value and threshold, explicit indexes are never touched, and the
//! typed index view reports states, transitions, and a bounded window of
//! retirements. The per-tenant auto registry is bounded
//! ([`MAX_AUTO_INDEXES_PER_TENANT`]) and creation past the bound fails
//! closed with a recorded reason. Selection itself is bounded-retried: an
//! index replaced underneath the pass re-matches once instead of
//! interrupting the query (AX-9).
//!
//! # Not claimed
//!
//! Execution remains a bounded snapshot materialization: safely supported
//! scalar equality predicates may narrow candidates through the selected
//! segment, while all other predicates use the canonical scan. This packet
//! makes no performance claim of any kind (D-6; receipts belong to IX-04).

use std::sync::Arc;

use super::{GenerationView, NormalizedQuery, QueryError, QueryPlan, spec::IndexNamespace};
use crate::{
    engine::Engine,
    index::{IndexBuilder, IndexCatalog, RebuildOutcome},
    types::{ReadSnapshot, TenantId},
};

pub mod auto_index;
pub(crate) mod lifecycle;

#[cfg(test)]
mod lifecycle_tests;

#[cfg(test)]
mod tests;

pub use lifecycle::{IndexLifecycleState, IndexTransition, LifecycleThresholds};

/// The reserved namespace prefix of engine-owned auto indexes (AX-10).
pub const AUTO_NAMESPACE_PREFIX: &str = "Auto/";

/// The maximum number of index selection records retained as engine state
/// per tenant: each tenant's selections are that tenant's alone to observe,
/// so each tenant owns a separately bounded store.
pub const MAX_SELECTION_RECORDS: usize = 128;

/// The maximum number of live auto indexes one tenant's registry retains.
///
/// The registry is bounded engine state: past the bound, a new auto identity
/// is refused fail-closed with a recorded reason until the retirement sweep
/// of the same optimizer pass frees a slot. An initial value, tunable and
/// subject to measurement (IX-04 owns the receipts).
pub const MAX_AUTO_INDEXES_PER_TENANT: usize = 64;

/// The maximum number of selection attempts one pass runs when its subject
/// is replaced underneath it: the bounded plan retry (AX-9) — a vanished
/// index re-matches once instead of interrupting the query.
const MAX_SELECTION_ATTEMPTS: usize = 2;

/// The maximum number of cached query-shape selections.
pub const MAX_CACHED_SELECTIONS: usize = 64;

/// The maximum number of fields in one index definition. The demand of one
/// query is bounded by the seam's own clause bounds
/// ([`MAX_CONDITIONS`](super::MAX_CONDITIONS) +
/// [`MAX_ORDERING_KEYS`](super::MAX_ORDERING_KEYS)).
pub const MAX_DEFINITION_FIELDS: usize = 40;

/// The byte bound of one namespace name, shared with the seam's field-name
/// rule; auto names truncate to fit it.
const MAX_NAME_BYTES: usize = super::MAX_FIELD_BYTES;

/// The truncation marker of an over-long auto name.
const ELLIPSIS: &str = "...";

/// The hex-digit length of the digest tail of a truncated auto name.
const DIGEST_BYTES: usize = 16;

/// One capability an index field can offer a query.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum IndexCapability {
    /// The field serves equality and presence conditions.
    Equality,
    /// The field serves range conditions and ordering keys.
    Ordering,
}

impl IndexCapability {
    /// Returns the canonical short tag of this capability, used only where a
    /// capability set must render inside an identity.
    const fn as_str(self) -> &'static str {
        match self {
            Self::Equality => "eq",
            Self::Ordering => "ord",
        }
    }
}

/// The capability set of one index field.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct IndexCapabilities {
    equality: bool,
    ordering: bool,
}

impl IndexCapabilities {
    /// Equality only.
    pub const EQUALITY: Self = Self { equality: true, ordering: false };
    /// Ordering only.
    pub const ORDERING: Self = Self { equality: false, ordering: true };
    /// The full W3-2 capability set, which every auto-index field carries.
    pub const FULL: Self = Self { equality: true, ordering: true };

    /// Reports whether the set carries one capability.
    #[must_use]
    pub const fn has(self, capability: IndexCapability) -> bool {
        match capability {
            IndexCapability::Equality => self.equality,
            IndexCapability::Ordering => self.ordering,
        }
    }

    /// Returns the union of two sets.
    #[must_use]
    pub const fn union(self, other: Self) -> Self {
        Self {
            equality: self.equality || other.equality,
            ordering: self.ordering || other.ordering,
        }
    }

    /// Reports whether the set is empty.
    #[must_use]
    pub const fn is_empty(self) -> bool {
        !self.equality && !self.ordering
    }

    /// Returns the canonical tag rendering: `eq`, `ord`, or `eq+ord`.
    #[must_use]
    pub fn as_tag(self) -> String {
        let mut tags: Vec<&str> = Vec::new();
        if self.equality {
            tags.push(IndexCapability::Equality.as_str());
        }
        if self.ordering {
            tags.push(IndexCapability::Ordering.as_str());
        }
        tags.join("+")
    }
}

/// One indexed field of an index definition: a name and its capabilities.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct IndexField {
    name: String,
    capabilities: IndexCapabilities,
}

impl IndexField {
    /// Creates one index field after validating the name and requiring at
    /// least one capability.
    ///
    /// # Errors
    ///
    /// Returns the field-name validation errors of
    /// [`RequestValidationError`](crate::operations::RequestValidationError)
    /// and [`RequestValidationError::InvalidQuery`](crate::operations::RequestValidationError::InvalidQuery)
    /// for an empty capability set.
    pub fn try_new(
        name: &str,
        capabilities: IndexCapabilities,
    ) -> Result<Self, crate::operations::RequestValidationError> {
        super::spec::validate_field_name(name)?;
        if capabilities.is_empty() {
            return Err(crate::operations::RequestValidationError::InvalidQuery);
        }
        Ok(Self { name: name.to_owned(), capabilities })
    }

    /// Returns the indexed field name.
    #[must_use]
    pub fn name(&self) -> &str {
        &self.name
    }

    /// Returns the field's capability set.
    #[must_use]
    pub const fn capabilities(&self) -> IndexCapabilities {
        self.capabilities
    }
}

/// Whether an index was created explicitly or by the optimizer.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum IndexKind {
    /// Created by a user operation through a typed registration.
    Explicit,
    /// Created by the query optimizer under the reserved `Auto/` namespace.
    Auto,
}

/// A validated index definition: one collection and an ordered field set.
///
/// The field order is the canonical ordinal order of the shape that produced
/// the definition, and the ordered field list is part of the definition's
/// identity.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct IndexDefinition {
    collection: crate::types::CollectionName,
    fields: Vec<IndexField>,
    kind: IndexKind,
}

impl IndexDefinition {
    /// Creates one definition after validating its field list.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidQuery`](crate::operations::RequestValidationError::InvalidQuery)
    /// for a field list that is empty or longer than
    /// [`MAX_DEFINITION_FIELDS`], or that names one field twice; per-field
    /// validation errors come from [`IndexField::try_new`].
    pub fn try_new(
        collection: crate::types::CollectionName,
        fields: Vec<IndexField>,
        kind: IndexKind,
    ) -> Result<Self, crate::operations::RequestValidationError> {
        if fields.is_empty() || fields.len() > MAX_DEFINITION_FIELDS {
            return Err(crate::operations::RequestValidationError::InvalidQuery);
        }
        let mut names: Vec<&str> = fields.iter().map(IndexField::name).collect();
        names.sort_unstable();
        if names.windows(2).any(|pair| pair[0] == pair[1]) {
            return Err(crate::operations::RequestValidationError::InvalidQuery);
        }
        Ok(Self { collection, fields, kind })
    }

    /// Returns the indexed collection.
    #[must_use]
    pub fn collection(&self) -> &crate::types::CollectionName {
        &self.collection
    }

    /// Returns the canonical ordered field list.
    #[must_use]
    pub fn fields(&self) -> &[IndexField] {
        &self.fields
    }

    /// Returns the definition's kind.
    #[must_use]
    pub const fn kind(&self) -> IndexKind {
        self.kind
    }
}

/// Returns the deterministic auto-index name of one definition.
///
/// The name is `Auto/{Collection}/By{Field1}And{Field2}…` over the
/// definition's canonical field order. A name that would exceed the
/// namespace byte bound is truncated on a character boundary and suffixed
/// with `...` plus the sixteen hex digits of an FNV-1a 64 digest of the full
/// name, so the mapping from (collection, field set) to identity stays
/// injective and deterministic. The digest is format-private, like the
/// storage checksum; it is an identity fragment, not a security claim.
#[must_use]
pub fn auto_index_name(definition: &IndexDefinition) -> String {
    let fields = definition
        .fields()
        .iter()
        .map(|field| capitalize_first(field.name()))
        .collect::<Vec<_>>()
        .join("And");
    let full = format!("{AUTO_NAMESPACE_PREFIX}{}/By{fields}", definition.collection().as_str());
    if full.len() <= MAX_NAME_BYTES {
        return full;
    }
    let keep = MAX_NAME_BYTES - ELLIPSIS.len() - DIGEST_BYTES;
    let boundary = floor_char_boundary(&full, keep);
    format!("{}{ELLIPSIS}{:016x}", &full[..boundary], fnv1a64(full.as_bytes()))
}

/// Returns `limit` if it is a character boundary of `text`, or the largest
/// smaller boundary. `str::floor_char_boundary` is not stable on the pinned
/// toolchain, so this is the same walk spelled out.
fn floor_char_boundary(text: &str, limit: usize) -> usize {
    let mut boundary = limit.min(text.len());
    while boundary > 0 && !text.is_char_boundary(boundary) {
        boundary -= 1;
    }
    boundary
}

/// Capitalizes the first character of a field name, V1's `By{Field}` shape.
fn capitalize_first(name: &str) -> String {
    let mut characters = name.chars();
    match characters.next() {
        Some(first) => first.to_uppercase().collect::<String>() + characters.as_str(),
        None => String::new(),
    }
}

/// The FNV-1a 64-bit digest of the name, hex-encoded to sixteen digits by
/// the caller. Non-cryptographic and format-private by design.
const FNV_OFFSET: u64 = 0xcbf2_9ce4_8422_2325;
const FNV_PRIME: u64 = 0x0000_0100_0000_01b3;
fn fnv1a64(bytes: &[u8]) -> u64 {
    let mut hash = FNV_OFFSET;
    for byte in bytes {
        hash ^= u64::from(*byte);
        hash = hash.wrapping_mul(FNV_PRIME);
    }
    hash
}

/// The per-candidate verdict of one selection (AX-12, adapted).
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum CandidateVerdict {
    /// Every demanded field is covered with sufficient capabilities.
    Complete,
    /// Every demanded field is covered with sufficient capabilities, but the
    /// index is idle: demoted in preference (AX-13 rule 3), still
    /// selectable, and woken synchronously when chosen (AX-22).
    CompleteButIdle,
    /// The candidate covers the collection but cannot serve the whole demand.
    Partial(PartialReason),
    /// The candidate cannot serve this query at all.
    Failure(FailureReason),
}

/// Why a candidate is a partial match.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum PartialReason {
    /// A demanded field is not indexed by the candidate.
    FieldNotIndexed(String),
    /// A demanded field is indexed without the required capability.
    CapabilityInsufficient {
        /// The demanded field.
        field: String,
        /// The capability the query demands.
        required: IndexCapability,
        /// The capability set the candidate carries.
        present: IndexCapabilities,
    },
}

/// Why a candidate is not a match.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum FailureReason {
    /// The candidate indexes another collection.
    AnotherCollection,
}

/// The recorded note on one unchosen candidate (AX-14, adapted).
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum CandidateNote {
    /// The candidate lost only because another candidate existed; a full tie
    /// was broken deterministically by catalog order.
    BetterMatchAvailable,
    /// An equivalent explicit index took precedence (D-3).
    ExplicitPreferredOverAuto,
    /// The chosen candidate was more up to date.
    NotMostUpToDate,
    /// The candidate was idle while the chosen candidate was not, at equal
    /// recency (AX-13 rule 3).
    IdleDemoted,
    /// The chosen candidate was wider at equal recency.
    NotWidest,
}

/// One tie-break rule the selection applied.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum TieBreak {
    /// An equivalent explicit index took precedence over an auto index.
    ExplicitPreferred,
    /// The more up-to-date candidate won.
    MostUpToDate,
    /// The non-idle candidate won at equal recency (AX-13 rule 3).
    NonIdlePreferred,
    /// The wider candidate won at equal recency.
    Widest,
}

/// The outcome of one automatic creation attempt's initial build.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum FirstBuildOutcome {
    /// The initial build published at this generation and coverage.
    Published {
        /// The epoch the generation belongs to.
        epoch: u64,
        /// The generation published.
        generation: u64,
        /// The storage sequence the segment covers.
        covered_sequence: u64,
    },
    /// No publication had been observed; the first build is deferred.
    NoPublicationObserved,
    /// The build did not advance coverage and was rejected as stale.
    RejectedAsStale,
    /// The build could not read the snapshot view.
    StorageFailure,
}

/// Why an automatic creation was refused.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum CreationRefusal {
    /// The engine's kill switch has auto-index creation disabled.
    Disabled,
    /// A namespace already squats the deterministic auto name with different
    /// content; creation fails closed instead of reusing it.
    IdentityConflict,
    /// The tenant's auto-index registry is at
    /// [`MAX_AUTO_INDEXES_PER_TENANT`] and no slot was freed by the
    /// retirement sweep of this pass; creation fails closed with the bound
    /// as its recorded reason.
    RegistryFull,
}

/// The outcome of one selection.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum SelectionOutcome {
    /// The query is a pure collection scan: no index was considered or
    /// created (AX-1).
    CollectionScan,
    /// An existing index serves the query.
    ChosenIndex {
        /// The catalog name of the chosen index.
        name: String,
        /// The chosen index's kind.
        kind: IndexKind,
    },
    /// The query created an auto index. The triggering query executed
    /// The initial build outcome recorded for the triggering query.
    CreatedAutoIndex {
        /// The deterministic name the shape maps to.
        name: String,
        /// The outcome of the initial build attempted at creation.
        build: FirstBuildOutcome,
    },
    /// Creation was refused; the query fails closed with a typed error.
    CreationRefused {
        /// Why creation was refused.
        reason: CreationRefusal,
    },
}

/// One candidate an optimizer selection considered.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct IndexCandidate {
    name: String,
    kind: IndexKind,
    verdict: CandidateVerdict,
    note: Option<CandidateNote>,
}

impl IndexCandidate {
    /// Creates one candidate entry. Crate-only: candidates are produced by
    /// selection.
    pub(crate) const fn new(
        name: String,
        kind: IndexKind,
        verdict: CandidateVerdict,
        note: Option<CandidateNote>,
    ) -> Self {
        Self { name, kind, verdict, note }
    }

    /// Returns the candidate's catalog name.
    #[must_use]
    pub fn name(&self) -> &str {
        &self.name
    }

    /// Returns the candidate's kind.
    #[must_use]
    pub const fn kind(&self) -> IndexKind {
        self.kind
    }

    /// Returns the candidate's verdict against the query's demand.
    #[must_use]
    pub const fn verdict(&self) -> &CandidateVerdict {
        &self.verdict
    }

    /// Returns why an unchosen complete candidate lost, when one was recorded.
    #[must_use]
    pub const fn note(&self) -> Option<CandidateNote> {
        self.note
    }
}

/// The staleness basis of one selection's answer: the generation the
/// snapshot observed for the serving index and the generation the catalog
/// can currently supply.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct StalenessBasis {
    observed: Option<super::ObservedGeneration>,
    available: Option<super::ObservedGeneration>,
}

impl StalenessBasis {
    /// Creates one basis. Crate-only: selection produces bases.
    pub(crate) const fn new(
        observed: Option<super::ObservedGeneration>,
        available: Option<super::ObservedGeneration>,
    ) -> Self {
        Self { observed, available }
    }

    /// Returns the generation the snapshot observed for the serving index.
    #[must_use]
    pub const fn observed(&self) -> Option<super::ObservedGeneration> {
        self.observed
    }

    /// Returns the generation the catalog can currently supply.
    #[must_use]
    pub const fn available(&self) -> Option<super::ObservedGeneration> {
        self.available
    }
}

/// The recorded outcome of one index selection (the glossary's Index
/// Selection Record).
///
/// Records are bounded engine state produced by every dynamic query and
/// read through [`QueryStore::recent_index_selections`](super::QueryStore::recent_index_selections).
/// They carry no timestamp, path, or document value, so they are
/// deterministic for the same selection inputs.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct IndexSelectionRecord {
    tenant: TenantId,
    collection: String,
    query_fields: Vec<IndexField>,
    candidates: Vec<IndexCandidate>,
    tie_breaks: Vec<TieBreak>,
    outcome: SelectionOutcome,
    cache_hit: bool,
    staleness_basis: Option<StalenessBasis>,
}

impl IndexSelectionRecord {
    /// Creates one record. Crate-only: selection is the only producer.
    #[allow(clippy::too_many_arguments)] // one frozen record shape
    pub(crate) const fn new(
        tenant: TenantId,
        collection: String,
        query_fields: Vec<IndexField>,
        candidates: Vec<IndexCandidate>,
        tie_breaks: Vec<TieBreak>,
        outcome: SelectionOutcome,
        cache_hit: bool,
        staleness_basis: Option<StalenessBasis>,
    ) -> Self {
        Self {
            tenant,
            collection,
            query_fields,
            candidates,
            tie_breaks,
            outcome,
            cache_hit,
            staleness_basis,
        }
    }

    /// Returns the tenant the selection ran for.
    #[must_use]
    pub fn tenant(&self) -> &TenantId {
        &self.tenant
    }

    /// Returns the queried collection.
    #[must_use]
    pub fn collection(&self) -> &str {
        &self.collection
    }

    /// Returns the demanded fields with their demanded capabilities, in the
    /// query's canonical ordinal order.
    #[must_use]
    pub fn query_fields(&self) -> &[IndexField] {
        &self.query_fields
    }

    /// Returns every considered candidate in catalog order.
    #[must_use]
    pub fn candidates(&self) -> &[IndexCandidate] {
        &self.candidates
    }

    /// Returns the tie-break rules this selection applied, in rule order.
    #[must_use]
    pub fn tie_breaks(&self) -> &[TieBreak] {
        &self.tie_breaks
    }

    /// Returns the selection's outcome.
    #[must_use]
    pub const fn outcome(&self) -> &SelectionOutcome {
        &self.outcome
    }

    /// Reports whether the selection was served from the bounded selection
    /// cache.
    #[must_use]
    pub const fn cache_hit(&self) -> bool {
        self.cache_hit
    }

    /// Returns the staleness basis of the answer, when an index was chosen
    /// or created.
    #[must_use]
    pub const fn staleness_basis(&self) -> Option<StalenessBasis> {
        self.staleness_basis
    }
}

/// One registered index as the typed reads report it.
///
/// A view carries the index's lifecycle state and its bounded transition
/// history, and a bounded window of retired indexes stays listed with the
/// [`IndexLifecycleState::Retired`] state so lifecycle transitions —
/// including retirements — stay observable through this one typed read.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct RegisteredIndex {
    tenant: TenantId,
    name: String,
    kind: IndexKind,
    collection: String,
    fields: Vec<IndexField>,
    published: Option<super::ObservedGeneration>,
    queried: bool,
    state: IndexLifecycleState,
    transitions: Vec<IndexTransition>,
}

impl RegisteredIndex {
    /// Creates one view. Crate-only: the catalog produces these.
    #[allow(clippy::too_many_arguments)] // one frozen view shape
    pub(crate) fn new(
        tenant: TenantId,
        name: String,
        kind: IndexKind,
        collection: String,
        fields: Vec<IndexField>,
        published: Option<super::ObservedGeneration>,
        queried: bool,
        state: IndexLifecycleState,
        transitions: Vec<IndexTransition>,
    ) -> Self {
        Self { tenant, name, kind, collection, fields, published, queried, state, transitions }
    }

    /// Returns the owning tenant.
    #[must_use]
    pub fn tenant(&self) -> &TenantId {
        &self.tenant
    }

    /// Returns the catalog name.
    #[must_use]
    pub fn name(&self) -> &str {
        &self.name
    }

    /// Returns the index kind.
    #[must_use]
    pub const fn kind(&self) -> IndexKind {
        self.kind
    }

    /// Returns the indexed collection.
    #[must_use]
    pub fn collection(&self) -> &str {
        &self.collection
    }

    /// Returns the canonical field list.
    #[must_use]
    pub fn fields(&self) -> &[IndexField] {
        &self.fields
    }

    /// Returns the newest published generation, when one exists.
    #[must_use]
    pub const fn published(&self) -> Option<super::ObservedGeneration> {
        self.published
    }

    /// Reports whether any query has marked this index queried (AX-20) —
    /// for an auto index, whether its usage report has arrived at all: the
    /// fail-closed reporting rule of the lifecycle (AX-21) reads exactly
    /// this fact.
    #[must_use]
    pub const fn queried(&self) -> bool {
        self.queried
    }

    /// Returns the lifecycle state. Explicit indexes are always active and
    /// never transition; retired entries are bounded tombstones of deleted
    /// auto indexes.
    #[must_use]
    pub const fn state(&self) -> IndexLifecycleState {
        self.state
    }

    /// Returns the bounded lifecycle transition history, oldest first, with
    /// each transition's measured value and threshold.
    #[must_use]
    pub fn transitions(&self) -> &[IndexTransition] {
        &self.transitions
    }
}

// ---------------------------------------------------------------------------
// The selection core
// ---------------------------------------------------------------------------

/// The demand one dynamic query places on an index.
#[derive(Clone, Debug, Eq, PartialEq)]
pub(crate) struct QueryDemand {
    collection: String,
    fields: Vec<IndexField>,
}

impl QueryDemand {
    /// Creates one demand from its parts. Crate-only: the demand of a query
    /// derives through [`demand_of`], and the lifecycle's index-vs-index
    /// comparison derives one from an existing definition.
    pub(crate) fn new(collection: String, fields: Vec<IndexField>) -> Self {
        Self { collection, fields }
    }

    /// Returns the demanded collection.
    pub(crate) fn collection(&self) -> &str {
        &self.collection
    }

    /// Returns the demanded fields in canonical ordinal order.
    pub(crate) fn fields(&self) -> &[IndexField] {
        &self.fields
    }

    /// Returns the canonical shape key of this demand for one tenant.
    pub(crate) fn shape_key(&self, tenant: &TenantId) -> String {
        let fields = self
            .fields
            .iter()
            .map(|field| field.name().to_owned())
            .collect::<Vec<_>>()
            .join("\u{1f}");
        format!("{}\u{1f}{}\u{1f}{}", tenant.as_str(), self.collection, fields)
    }
}

/// Derives the index demand of one normalized query.
///
/// Where-clause fields come first in declaration order, then order-by fields
/// that are not already present — V1's ordinal field order. A condition or
/// ordering key that maps to no capability would contribute nothing; every
/// condition of the closed set maps to at least one, so a query with any
/// condition or ordering key always demands its fields. The expanded
/// evaluator conditions classify the way V1 serves them: membership
/// (`in`, `all in`) and the pattern matches are term-set lookups over the
/// indexed field, and `between` is a range.
pub(crate) fn demand_of(query: &NormalizedQuery) -> QueryDemand {
    let mut fields: Vec<IndexField> = Vec::new();
    for condition in query.predicate().conditions() {
        let capability = match condition {
            super::QueryCondition::Equals { .. }
            | super::QueryCondition::NotEquals { .. }
            | super::QueryCondition::Exists { .. }
            | super::QueryCondition::Missing { .. }
            | super::QueryCondition::In { .. }
            | super::QueryCondition::AllIn { .. }
            | super::QueryCondition::StartsWith { .. }
            | super::QueryCondition::EndsWith { .. }
            | super::QueryCondition::Contains { .. } => IndexCapability::Equality,
            super::QueryCondition::GreaterThan { .. }
            | super::QueryCondition::GreaterOrEqual { .. }
            | super::QueryCondition::LessThan { .. }
            | super::QueryCondition::LessOrEqual { .. }
            | super::QueryCondition::Between { .. } => IndexCapability::Ordering,
            // A full-text predicate is served by its own text auto-index
            // (S-02 slice D), never by a value btree: it contributes no
            // value demand, and a pure-search query keeps the pure
            // collection-scan record.
            super::QueryCondition::Search { .. } => continue,
        };
        demand_field(&mut fields, condition.field(), capability);
    }
    for key in query.ordering().keys() {
        demand_field(&mut fields, key.field(), IndexCapability::Ordering);
    }
    QueryDemand {
        // A family source never reaches the optimizer pass (its execution
        // bypasses it), so its demand is recorded without a collection.
        collection: query
            .source()
            .collection()
            .map_or_else(|| "\u{0}family".to_owned(), |collection| collection.as_str().to_owned()),
        fields,
    }
}

/// Unions one demanded capability into the ordinal field list.
fn demand_field(fields: &mut Vec<IndexField>, name: &str, capability: IndexCapability) {
    let single = match capability {
        IndexCapability::Equality => IndexCapabilities::EQUALITY,
        IndexCapability::Ordering => IndexCapabilities::ORDERING,
    };
    if let Some(existing) = fields.iter_mut().find(|field| field.name() == name) {
        if let Ok(merged) = IndexField::try_new(name, existing.capabilities().union(single)) {
            *existing = merged;
        }
        return;
    }
    if let Ok(field) = IndexField::try_new(name, single) {
        fields.push(field);
    }
}

/// Runs one optimizer pass for a dynamic plan and records its selection.
///
/// The pass runs before data execution after the caller's binding checks, and
/// re-checks the execution bounds at its own bounded checkpoint before it
/// creates anything. A fail-closed refusal prevents a silent unbounded scan;
/// the selected segment is still subject to the executor's exact-key and
/// snapshot-coverage checks. The pass never changes the query's result.
///
/// One lifecycle maintenance pass runs at the head (IX-03): selection
/// decides against post-maintenance truth, and one public-seam query drives
/// the whole state machine. The selection itself is bounded-retried: when
/// the index it chose or created is replaced underneath it, the pass
/// re-matches once instead of interrupting the query (AX-9).
pub(crate) fn run(
    engine: &Engine,
    catalog: &IndexCatalog,
    plan: &QueryPlan,
    snapshot: &ReadSnapshot,
) -> Result<Option<IndexNamespace>, QueryError> {
    let query = plan.model();
    super::execute::check_bounds(query.deadline(), query.cancellation_signal())?;
    lifecycle::maintain(engine, catalog, snapshot.engine_identity());
    super::execute::check_bounds(query.deadline(), query.cancellation_signal())?;
    let tenant = snapshot.tenant().clone();
    let demand = demand_of(query);

    // The pure collection scan stays index-free (AX-1).
    if demand.fields.is_empty() {
        let record = IndexSelectionRecord::new(
            tenant,
            demand.collection,
            demand.fields,
            Vec::new(),
            Vec::new(),
            SelectionOutcome::CollectionScan,
            false,
            None,
        );
        catalog.record_selection(record);
        return Ok(None);
    }

    let shape = demand.shape_key(&tenant);
    for attempt in 0..MAX_SELECTION_ATTEMPTS {
        let final_attempt = attempt + 1 == MAX_SELECTION_ATTEMPTS;
        if let Some(record) =
            select_once(engine, catalog, &tenant, &demand, &shape, snapshot, final_attempt)?
        {
            let serving = serving_namespace(&record);
            catalog.record_selection(record);
            return Ok(serving);
        }
    }
    Ok(None) // unreachable in practice: the final attempt always records
}

/// Extracts the selected namespace from one recorded outcome. A created auto
/// index is immediately usable when its bounded initial build published a
/// segment; callers still verify coverage and predicate support before use.
fn serving_namespace(record: &IndexSelectionRecord) -> Option<IndexNamespace> {
    match record.outcome() {
        SelectionOutcome::ChosenIndex { name, .. }
        | SelectionOutcome::CreatedAutoIndex { name, .. } => {
            IndexNamespace::try_new(record.tenant().clone(), name).ok()
        }
        SelectionOutcome::CollectionScan | SelectionOutcome::CreationRefused { .. } => None,
    }
}

/// Runs one selection attempt and returns its record, or `None` when the
/// attempt's subject was replaced underneath it and the bounded retry
/// should re-match. The final attempt records its outcome regardless — every
/// dynamic query produces exactly one selection record.
#[allow(clippy::too_many_arguments)] // one pass context frozen by the seam's shape
fn select_once(
    engine: &Engine,
    catalog: &IndexCatalog,
    tenant: &TenantId,
    demand: &QueryDemand,
    shape: &str,
    snapshot: &ReadSnapshot,
    final_attempt: bool,
) -> Result<Option<IndexSelectionRecord>, QueryError> {
    // The bounded selection cache (AX-2, adapted): a hit still counts as a
    // selection, and a vanished index falls back to the full match.
    if let Some(record) = select_from_cache(catalog, tenant, demand, shape, snapshot) {
        return Ok(Some(record));
    }

    // Full match: every definition of this tenant is a candidate with a
    // verdict, whatever collection it indexes.
    let entries = catalog.definitions(tenant);
    let mut candidates: Vec<IndexCandidate> = Vec::new();
    let mut complete: Vec<(usize, u64)> = Vec::new(); // (index, covered)
    for (index, entry) in entries.iter().enumerate() {
        let verdict = verdict_of(entry, demand);
        if matches!(verdict, CandidateVerdict::Complete | CandidateVerdict::CompleteButIdle) {
            complete.push((index, entry.covered_sequence().unwrap_or(0)));
        }
        candidates.push(IndexCandidate::new(
            entry.namespace().name().to_owned(),
            entry.definition().kind(),
            verdict,
            None,
        ));
    }

    if !complete.is_empty() {
        let record =
            select_by_ranking(catalog, &entries, demand, shape, candidates, &complete, snapshot);
        return Ok(subject_or_retry(catalog, record, final_attempt));
    }
    create_or_refuse(engine, catalog, tenant, demand, shape, &entries, candidates, snapshot)
        .map(|record| subject_or_retry(catalog, record, final_attempt))
}

/// Returns the record when its serving index still exists, `None` when it
/// was replaced underneath the attempt and the bounded retry should
/// re-match — and the record unchanged on the final attempt, which always
/// records.
fn subject_or_retry(
    catalog: &IndexCatalog,
    record: IndexSelectionRecord,
    final_attempt: bool,
) -> Option<IndexSelectionRecord> {
    if final_attempt {
        return Some(record);
    }
    let serving = match record.outcome() {
        SelectionOutcome::ChosenIndex { name, .. }
        | SelectionOutcome::CreatedAutoIndex { name, .. } => Some(name.clone()),
        SelectionOutcome::CollectionScan | SelectionOutcome::CreationRefused { .. } => None,
    };
    let live = serving.is_none_or(|name| {
        IndexNamespace::try_new(record.tenant().clone(), &name)
            .is_ok_and(|namespace| catalog.definition_exists(&namespace))
    });
    live.then_some(record)
}

/// Serves one demand from the bounded selection cache when the cached choice
/// is still valid and still completely serves the demand.
fn select_from_cache(
    catalog: &IndexCatalog,
    tenant: &TenantId,
    demand: &QueryDemand,
    shape: &str,
    snapshot: &ReadSnapshot,
) -> Option<IndexSelectionRecord> {
    let name = catalog.cached_selection(shape)?;
    let entry = catalog
        .definitions(tenant)
        .into_iter()
        .find(|entry| entry.namespace().name() == name && serves(entry, demand))?;
    catalog.mark_queried(entry.namespace());
    let basis = basis_of(catalog, entry.namespace(), snapshot);
    Some(IndexSelectionRecord::new(
        tenant.clone(),
        demand.collection.clone(),
        demand.fields.clone(),
        Vec::new(),
        Vec::new(),
        SelectionOutcome::ChosenIndex {
            name: entry.namespace().name().to_owned(),
            kind: entry.definition().kind(),
        },
        true,
        Some(basis),
    ))
}

/// Ranks the complete candidates, records why each loser lost, and returns
/// the winning record.
///
/// Ranking: an equivalent explicit index takes precedence over an auto index
/// (D-3), then the most up-to-date candidate, then the non-idle one at equal
/// recency (AX-13 rule 3), then the widest; catalog order breaks full ties
/// deterministically. Choosing an idle winner wakes it (AX-22), which
/// [`IndexCatalog::mark_queried`](crate::index::IndexCatalog) performs and
/// records.
fn select_by_ranking(
    catalog: &IndexCatalog,
    entries: &[crate::index::DefinitionEntry],
    demand: &QueryDemand,
    shape: &str,
    mut candidates: Vec<IndexCandidate>,
    complete: &[(usize, u64)],
    snapshot: &ReadSnapshot,
) -> IndexSelectionRecord {
    let (winner_index, winner_covered) = complete
        .iter()
        .copied()
        .max_by(|left, right| {
            rank_key(&entries[left.0], left.1).cmp(&rank_key(&entries[right.0], right.1))
        })
        .expect("a complete candidate exists whenever the vector is non-empty");
    let winner_entry = &entries[winner_index];
    let winner_idle = winner_entry.state() == IndexLifecycleState::Idle;
    let mut applied = [false; 4];
    for (index, covered) in complete {
        if *index == winner_index {
            continue;
        }
        let entry = &entries[*index];
        let (note, rule) = if winner_entry.definition().kind() == IndexKind::Explicit
            && entry.definition().kind() == IndexKind::Auto
        {
            (CandidateNote::ExplicitPreferredOverAuto, 0)
        } else if covered < &winner_covered {
            (CandidateNote::NotMostUpToDate, 1)
        } else if covered == &winner_covered
            && entry.state() == IndexLifecycleState::Idle
            && !winner_idle
        {
            (CandidateNote::IdleDemoted, 2)
        } else if covered == &winner_covered
            && entry.definition().fields().len() < winner_entry.definition().fields().len()
        {
            (CandidateNote::NotWidest, 3)
        } else {
            (CandidateNote::BetterMatchAvailable, 4)
        };
        if rule < applied.len() {
            applied[rule] = true;
        }
        candidates[*index].note = Some(note);
    }
    let mut tie_breaks = Vec::new();
    if applied[0] {
        tie_breaks.push(TieBreak::ExplicitPreferred);
    }
    if applied[1] {
        tie_breaks.push(TieBreak::MostUpToDate);
    }
    if applied[2] {
        tie_breaks.push(TieBreak::NonIdlePreferred);
    }
    if applied[3] {
        tie_breaks.push(TieBreak::Widest);
    }
    catalog.mark_queried(winner_entry.namespace());
    catalog.remember_selection(shape, winner_entry.namespace().name());
    let basis = basis_of(catalog, winner_entry.namespace(), snapshot);
    IndexSelectionRecord::new(
        winner_entry.namespace().tenant().clone(),
        demand.collection.clone(),
        demand.fields.clone(),
        candidates,
        tie_breaks,
        SelectionOutcome::ChosenIndex {
            name: winner_entry.namespace().name().to_owned(),
            kind: winner_entry.definition().kind(),
        },
        false,
        Some(basis),
    )
}

/// Creates the auto index the demand demands — widening the mapping from
/// the partially matching auto indexes first (AX-16) and refusing
/// fail-closed when a guard fires — and returns the recorded outcome of the
/// triggering query.
#[allow(clippy::too_many_arguments)] // one creation context frozen by the seam's shape
fn create_or_refuse(
    engine: &Engine,
    catalog: &IndexCatalog,
    tenant: &TenantId,
    demand: &QueryDemand,
    shape: &str,
    entries: &[crate::index::DefinitionEntry],
    candidates: Vec<IndexCandidate>,
    snapshot: &ReadSnapshot,
) -> Result<IndexSelectionRecord, QueryError> {
    // The kill switch: an unmatched dynamic query fails closed, never a
    // silent scan.
    if !catalog.auto_index_creation_enabled() {
        return Err(refused(catalog, tenant, demand, candidates, CreationRefusal::Disabled));
    }
    // The partial-match widening (AX-16): the union of the demand and every
    // partially matching auto index becomes the covering identity, and the
    // covered partials are scheduled for supersede. Explicit partials never
    // contribute — a dynamic query never disturbs an explicit index.
    let (widened, superseded) = lifecycle::widen_from_partials(demand, entries);
    let definition = auto_definition_of(&widened);
    // The fail-closed guards: a namespace that squats the deterministic name
    // with different content, or a registry at its bound, refuse creation
    // rather than reusing, overwriting, or growing unbounded.
    let registration = match catalog.register_auto(tenant, &definition) {
        Ok(registration) => registration,
        Err(crate::index::IndexError::AutoRegistryFull) => {
            return Err(refused(
                catalog,
                tenant,
                demand,
                candidates,
                CreationRefusal::RegistryFull,
            ));
        }
        Err(_) => {
            return Err(refused(
                catalog,
                tenant,
                demand,
                candidates,
                CreationRefusal::IdentityConflict,
            ));
        }
    };
    let namespace = registration.namespace().clone();
    // Only the pass that created the namespace runs its initial build. A
    // concurrent query that loses the registration race selects the existing
    // namespace below, exactly as it would have selected a complete
    // candidate, and does no build work: N threads racing one cold shape
    // cost one build, not N (the concurrent cold-start shape), and the
    // loser's compare-and-publish would only reject the build as stale.
    let build =
        registration.created().then(|| initial_build(engine, catalog, &namespace, snapshot));
    for target in &superseded {
        catalog.schedule_supersede(&namespace, target.namespace());
    }
    catalog.mark_queried(&namespace);
    catalog.remember_selection(shape, namespace.name());
    let basis = basis_of(catalog, &namespace, snapshot);
    let outcome = match (registration.created(), build) {
        (true, Some(build)) => {
            SelectionOutcome::CreatedAutoIndex { name: namespace.name().to_owned(), build }
        }
        // Reached when another pass already registered the same deterministic
        // identity — normally the concurrent cold-start race, where that
        // other pass created the namespace and owns the initial build, and
        // occasionally a registered definition this demand widened into
        // without the union becoming a complete candidate. The namespace is
        // selected as-is; when its creator has not published yet (or died
        // before it could), the selection stays correct through the bounded
        // scan and the next lifecycle pass catches the segment up.
        _ => SelectionOutcome::ChosenIndex {
            name: namespace.name().to_owned(),
            kind: IndexKind::Auto,
        },
    };
    Ok(IndexSelectionRecord::new(
        tenant.clone(),
        demand.collection.clone(),
        demand.fields.clone(),
        candidates,
        Vec::new(),
        outcome,
        false,
        Some(basis),
    ))
}

/// Records one fail-closed creation refusal and returns the typed query
/// error that discards the served answer.
fn refused(
    catalog: &IndexCatalog,
    tenant: &TenantId,
    demand: &QueryDemand,
    candidates: Vec<IndexCandidate>,
    reason: CreationRefusal,
) -> QueryError {
    catalog.record_selection(IndexSelectionRecord::new(
        tenant.clone(),
        demand.collection.clone(),
        demand.fields.clone(),
        candidates,
        Vec::new(),
        SelectionOutcome::CreationRefused { reason },
        false,
        None,
    ));
    QueryError::IndexNotReady
}

/// The ranking key of one complete candidate: explicit first, then recency,
/// then non-idle, then width. Catalog order breaks full ties upstream.
fn rank_key(entry: &crate::index::DefinitionEntry, covered: u64) -> (bool, u64, bool, usize) {
    (
        entry.definition().kind() == IndexKind::Explicit,
        covered,
        entry.state() != IndexLifecycleState::Idle,
        entry.definition().fields().len(),
    )
}

/// Reports whether one catalog entry completely serves the demand.
fn serves(entry: &crate::index::DefinitionEntry, demand: &QueryDemand) -> bool {
    matches!(
        verdict_of(entry, demand),
        CandidateVerdict::Complete | CandidateVerdict::CompleteButIdle
    )
}

/// Returns the verdict of one candidate against the demand (AX-12).
fn verdict_of(entry: &crate::index::DefinitionEntry, demand: &QueryDemand) -> CandidateVerdict {
    let definition = entry.definition();
    if definition.collection().as_str() != demand.collection {
        return CandidateVerdict::Failure(FailureReason::AnotherCollection);
    }
    for field in demand.fields() {
        let Some(present) =
            definition.fields().iter().find(|indexed| indexed.name() == field.name())
        else {
            return CandidateVerdict::Partial(PartialReason::FieldNotIndexed(
                field.name().to_owned(),
            ));
        };
        for capability in [IndexCapability::Equality, IndexCapability::Ordering] {
            if field.capabilities().has(capability) && !present.capabilities().has(capability) {
                return CandidateVerdict::Partial(PartialReason::CapabilityInsufficient {
                    field: field.name().to_owned(),
                    required: capability,
                    present: present.capabilities(),
                });
            }
        }
    }
    if entry.state() == IndexLifecycleState::Idle {
        return CandidateVerdict::CompleteButIdle;
    }
    CandidateVerdict::Complete
}

/// Builds the auto definition of one demand: the demanded field names in
/// canonical order, each carrying the full capability set.
fn auto_definition_of(demand: &QueryDemand) -> IndexDefinition {
    let collection = crate::types::CollectionName::try_new(demand.collection())
        .expect("the demand collection comes from a validated query source");
    let fields = demand
        .fields()
        .iter()
        .map(|field| IndexField::try_new(field.name(), IndexCapabilities::FULL))
        .collect::<Result<Vec<_>, _>>()
        .expect("demanded field names are validated query field names");
    IndexDefinition::try_new(collection, fields, IndexKind::Auto)
        .expect("a non-empty demand with unique fields is a valid definition")
}

/// Performs the one bounded initial build of a freshly created auto index.
///
/// The builder validates the Engine identity inside the snapshot view; the
/// pass is only reached on a snapshot execution already validated against
/// this Engine, so the snapshot's identity is the build's identity.
fn initial_build(
    engine: &Engine,
    catalog: &IndexCatalog,
    namespace: &IndexNamespace,
    snapshot: &ReadSnapshot,
) -> FirstBuildOutcome {
    let Some(delta) = catalog.delta() else {
        return FirstBuildOutcome::NoPublicationObserved;
    };
    match IndexBuilder::new(catalog, engine).rebuild_from(
        namespace,
        &delta,
        snapshot.engine_identity(),
    ) {
        Ok(RebuildOutcome::Published { epoch, generation, covered_sequence }) => {
            FirstBuildOutcome::Published { epoch, generation, covered_sequence }
        }
        Ok(RebuildOutcome::RejectedAsStale { .. }) => FirstBuildOutcome::RejectedAsStale,
        Err(_) => FirstBuildOutcome::StorageFailure,
    }
}

/// Reads the staleness basis of one serving index at one snapshot.
fn basis_of(
    catalog: &IndexCatalog,
    namespace: &IndexNamespace,
    snapshot: &ReadSnapshot,
) -> StalenessBasis {
    StalenessBasis::new(
        GenerationView::observed_generation(catalog, namespace, snapshot.sequence()),
        GenerationView::available_generation(catalog, namespace),
    )
}

/// Reads the registered indexes of one tenant as the typed view: every live
/// definition with its lifecycle state and transition history, then the
/// bounded window of retired auto indexes so retirements stay observable
/// after the namespace is gone.
pub(crate) fn registered_indexes_of(
    catalog: &Arc<IndexCatalog>,
    tenant: &TenantId,
) -> Vec<RegisteredIndex> {
    let mut views: Vec<RegisteredIndex> = catalog
        .definitions(tenant)
        .into_iter()
        .map(|entry| {
            RegisteredIndex::new(
                tenant.clone(),
                entry.namespace().name().to_owned(),
                entry.definition().kind(),
                entry.definition().collection().as_str().to_owned(),
                entry.definition().fields().to_vec(),
                entry.published(),
                entry.queried(),
                entry.state(),
                entry.transitions().to_vec(),
            )
        })
        .collect();
    for retired in catalog.retired_records(tenant) {
        views.push(RegisteredIndex::new(
            retired.tenant().clone(),
            retired.name().to_owned(),
            retired.kind(),
            retired.collection().to_owned(),
            retired.fields().to_vec(),
            None,
            false,
            IndexLifecycleState::Retired,
            retired.transitions().to_vec(),
        ));
    }
    views
}
