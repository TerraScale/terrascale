//! Vector search at the query seam (GAP-C4, TerraBaseV2#86).
//!
//! This module owns the typed vector vocabulary and the exact k-NN
//! execution path of the query seam: the declaration of a vector index
//! over one field of one ordinary collection
//! ([`VectorIndexDeclaration`], the PB-4051 anchor), the filtered k-NN
//! request and its typed error taxonomy ([`VectorSearchRequest`], the
//! PB-4024 anchor), the versioned vector predicate handoff
//! ([`VectorPredicate`], lowered directly into typed requests),
//! and [`QueryStore::vector_search`] — the execution against one
//! immutable snapshot. The decision record with the substrate decision,
//! the V1 anchors, and the WS-PARITY proposals is
//! `docs/query/vector-search.md`.
//!
//! ## What landed
//!
//! Exact (brute-force) cosine k-NN over stored document vectors: the
//! execution scans the snapshot view of the source collection, applies
//! the request's ordinary seam predicate as a pre-filter (CP-204's
//! better-than measure — vector search filterable by ordinary predicates
//! of the query seam, not actions-only), scores every surviving stored
//! vector exactly, and orders by score descending with ties broken by
//! document identity, so one snapshot always yields one order. The
//! declaration is vocabulary: schema-optional per PD-2026-08-26-04 D1
//! (documents without the vector field are non-candidates, never
//! errors), typed, MCP-exposable in shape, with the identity/drift model
//! the static index language established (GAP-Q06 D10).
//!
//! ## Explicit non-claims
//!
//! - **No ANN structure**: no HNSW-class graph is built or consulted; the
//!   declaration's candidate/edge counts are validated, identity-carried
//!   vocabulary for the ANN follow-up the decision record proposes. A
//!   request's [`mode`](VectorSearchMode) is always [`Exact`](VectorSearchMode::Exact).
//! - **No quantization**: Int8/Binary embeddings are refused typed at
//!   declaration ([`VectorOptionsError::QuantizationUnsupported`]); only
//!   f32 embeddings are scored. The PB-4051 quantization options are
//!   dispositioned as deferred in the decision record.
//! - **No auto-embedding**: a Text source is refused typed
//!   ([`VectorOptionsError::AutoEmbeddingUnsupported`]) until the
//!   embeddings-task integration lands; queries bind vectors by value.
//! - **No performance claim of any kind**: exact scoring is a correctness
//!   contract, not a speed statement; retrieval-workload receipts are a
//!   named follow-up owner in the decision record.
//!
//! V1 semantics pinned where adopted (the exact file-and-line citations
//! live in the decision record, at pin commit
//! `fa99b9257776dbe625e6a2388c74c9ce916637ee`): cosine similarity is the
//! reported score — V1 scores cosine *distance* internally and converts
//! with `DistanceToScoreCosine` (`1 - distance`, in `Hnsw.SimilarityMethods`);
//! the client seam carries `minimumSimilarity`, `numberOfCandidates`,
//! and `isExact` (`IAbstractDocumentQuery.VectorSearch`); and option
//! validation refuses a Text destination, non-positive dimensions, and
//! non-positive candidate/edge counts (`VectorOptions.Validate`).

use std::cmp::Ordering as StdOrdering;

use super::{
    QueryPredicate, QuerySource, QueryStore,
    execute::check_bounds,
    model,
    spec::{MAX_FIELD_BYTES, validate_field_name},
};
use crate::{
    index::definitions::{IndexDefinitionError, StaticIndexName},
    types::{
        Cancellation, CollectionName, Deadline, Document, DocumentId, DocumentValue, ReadSnapshot,
        TenantScope,
    },
};

/// The minimum dimensionality of one vector space (EV-C8: the competitive
/// bar starts at two dimensions).
pub const MIN_VECTOR_DIMENSIONS: usize = 2;

/// The maximum dimensionality of one vector space (EV-C8: the competitive
/// bar ends at 4096 dimensions).
pub const MAX_VECTOR_DIMENSIONS: usize = 4096;

/// The maximum k of one k-NN request (EV-C8: the competitive limit range
/// is 1..=256).
pub const MAX_VECTOR_SEARCH_LIMIT: u32 = 256;

/// The k a bare vector predicate — one written without an explicit
/// `limit` — lowers to.
///
/// An initial engine value, tunable with measured evidence; the SQL planner
/// (E-4) may revisit it with its paging model.
pub const DEFAULT_VECTOR_SEARCH_LIMIT: u32 = 10;

/// The version identity of the typed vector predicate surface.
///
/// The predicate shape (field, parameter, threshold, limit) is versioned:
/// additive changes bump within `vec/1.0`'s minor line by appending,
/// breaking changes open `vec/2.0`.
pub const VECTOR_PREDICATE_VERSION: &str = "vec/1.0";

/// The reserved family collection, spelled as `spec.rs` spells it: the
/// family module owns the canonical constant and does not re-export it.
const RESERVED_FAMILY_COLLECTION: &str = "terrabase.families";

/// How many scanned documents pass between two bounded execution
/// checkpoints; mirrors the interval of `execute.rs`.
const CHECKPOINT_INTERVAL: usize = 64;

/// The default execution window of a request that binds no deadline —
/// the include read-window pattern: one bounded window, not an unbounded
/// scan.
pub const VECTOR_SEARCH_WINDOW: std::time::Duration = std::time::Duration::from_mins(1);

// ---------------------------------------------------------------------------
// Options (PB-4051)
// ---------------------------------------------------------------------------

/// The embedding type of a vector field, V1's closed `VectorEmbeddingType`
/// set (file-and-line citation in the decision record).
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub enum VectorEmbeddingType {
    /// Single-precision floating-point components — the only type the
    /// first landing scores.
    Single,
    /// 8-bit signed integer quantized components (deferred).
    Int8,
    /// One-bit-per-dimension binary components (deferred).
    Binary,
    /// Text converted to an embedding by an embedding task (deferred: the
    /// auto-embedding integration).
    Text,
}

/// Validation failures of one [`VectorOptions`] construction.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum VectorOptionsError {
    /// The dimensionality is below [`MIN_VECTOR_DIMENSIONS`]. V1 refuses
    /// non-positive dimensions ("Number of vector dimensions has to be
    /// positive", `VectorOptions.Validate`); V2's closed range starts at
    /// two per the competitive bar.
    DimensionsBelowMinimum,
    /// The dimensionality is above [`MAX_VECTOR_DIMENSIONS`].
    DimensionsAboveMaximum,
    /// The destination embedding type is Text. V1: "Destination embedding
    /// type cannot be Text." (`VectorOptions.Validate`).
    TextDestination,
    /// The source embedding type is Text, whose dimensions the embedder
    /// sets internally (V1: "Dimensions are set internally by the
    /// embedder."); V2 dimensions are required and typed, so a Text
    /// source is refused until auto-embedding lands.
    AutoEmbeddingUnsupported,
    /// A quantized embedding configuration (Int8/Binary on either side).
    /// V1 refuses re-quantizing an already quantized source; V2 refuses
    /// every quantized configuration while only f32 embeddings are
    /// scored — a typed deferral, never a silently degraded scoring.
    QuantizationUnsupported,
    /// The candidate count is zero. V1: "Number of candidate nodes has to
    /// be positive."
    InvalidCandidateCount,
    /// The edge count is zero. V1: "Number of edges has to be positive."
    InvalidEdgeCount,
}

/// The typed options of one vector index: the PB-4051 contract.
///
/// The served configuration is Single-to-Single with explicit dimensions;
/// the candidate and edge counts are the ANN graph-construction vocabulary
/// (V1 `NumberOfCandidatesForIndexing`/`NumberOfEdges`) — validated and
/// carried in the declaration's identity, but not consumed by the exact
/// execution path this landing ships.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct VectorOptions {
    dimensions: u16,
    source: VectorEmbeddingType,
    destination: VectorEmbeddingType,
    candidates: Option<u32>,
    edges: Option<u32>,
}

impl VectorOptions {
    /// Creates one option set over the closed dimension range.
    ///
    /// # Errors
    ///
    /// Returns [`VectorOptionsError::DimensionsBelowMinimum`] below two
    /// dimensions, [`VectorOptionsError::DimensionsAboveMaximum`] above
    /// 4096, [`VectorOptionsError::TextDestination`] for a Text
    /// destination, [`VectorOptionsError::AutoEmbeddingUnsupported`] for a
    /// Text source, and [`VectorOptionsError::QuantizationUnsupported`]
    /// for any Int8/Binary embedding type, mirroring the refusals of V1's
    /// `VectorOptions.Validate`.
    pub fn try_new(
        dimensions: u16,
        source: VectorEmbeddingType,
        destination: VectorEmbeddingType,
    ) -> Result<Self, VectorOptionsError> {
        if usize::from(dimensions) < MIN_VECTOR_DIMENSIONS {
            return Err(VectorOptionsError::DimensionsBelowMinimum);
        }
        if usize::from(dimensions) > MAX_VECTOR_DIMENSIONS {
            return Err(VectorOptionsError::DimensionsAboveMaximum);
        }
        if destination == VectorEmbeddingType::Text {
            return Err(VectorOptionsError::TextDestination);
        }
        if source == VectorEmbeddingType::Text {
            return Err(VectorOptionsError::AutoEmbeddingUnsupported);
        }
        if matches!(source, VectorEmbeddingType::Int8 | VectorEmbeddingType::Binary)
            || matches!(destination, VectorEmbeddingType::Int8 | VectorEmbeddingType::Binary)
        {
            return Err(VectorOptionsError::QuantizationUnsupported);
        }
        Ok(Self { dimensions, source, destination, candidates: None, edges: None })
    }

    /// Returns the declared dimensionality.
    #[must_use]
    pub const fn dimensions(&self) -> u16 {
        self.dimensions
    }

    /// Returns the source embedding type.
    #[must_use]
    pub const fn source(&self) -> VectorEmbeddingType {
        self.source
    }

    /// Returns the destination (stored) embedding type.
    #[must_use]
    pub const fn destination(&self) -> VectorEmbeddingType {
        self.destination
    }

    /// Returns the ANN graph candidate count, when set.
    #[must_use]
    pub const fn candidates(&self) -> Option<u32> {
        self.candidates
    }

    /// Returns the ANN graph edge count, when set.
    #[must_use]
    pub const fn edges(&self) -> Option<u32> {
        self.edges
    }

    /// Sets the graph-construction candidate count.
    ///
    /// # Errors
    ///
    /// Returns [`VectorOptionsError::InvalidCandidateCount`] for zero —
    /// V1: "Number of candidate nodes has to be positive."
    pub fn with_candidates(mut self, candidates: u32) -> Result<Self, VectorOptionsError> {
        if candidates == 0 {
            return Err(VectorOptionsError::InvalidCandidateCount);
        }
        self.candidates = Some(candidates);
        Ok(self)
    }

    /// Sets the graph-construction edge count.
    ///
    /// # Errors
    ///
    /// Returns [`VectorOptionsError::InvalidEdgeCount`] for zero — V1:
    /// "Number of edges has to be positive."
    pub fn with_edges(mut self, edges: u32) -> Result<Self, VectorOptionsError> {
        if edges == 0 {
            return Err(VectorOptionsError::InvalidEdgeCount);
        }
        self.edges = Some(edges);
        Ok(self)
    }

    /// Renders the deterministic content identity of the option set: two
    /// option sets with equal content render equal identities, and the
    /// construction counts participate, so changing them is a content
    /// change of the declaration.
    fn identity(&self) -> String {
        let candidates =
            self.candidates.map_or_else(|| "none".to_owned(), |count| count.to_string());
        let edges = self.edges.map_or_else(|| "none".to_owned(), |count| count.to_string());
        format!(
            "d={}/s={:?}/t={:?}/c={candidates}/e={edges}",
            self.dimensions, self.source, self.destination
        )
    }
}

// ---------------------------------------------------------------------------
// Declaration (PB-4051, the index side)
// ---------------------------------------------------------------------------

/// Validation failures of one [`VectorIndexDeclaration`] construction.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum VectorIndexError {
    /// The index name is empty, oversized, malformed, or otherwise
    /// outside the static-index name alphabet.
    InvalidName,
    /// The name occupies the reserved engine-owned auto namespace
    /// (`Auto/`, compared case-insensitively) — ADR 0010 AX-10, the same
    /// refusal the static index language and the catalog apply.
    AutoNamespaceReserved,
    /// The collection is the reserved family collection: vector indexes
    /// live on ordinary collections, exactly as query sources do.
    ReservedCollection,
    /// The vector field name is invalid.
    InvalidField,
}

/// The drift of one vector index declaration relative to another.
///
/// Identity is the (name, collection, field) triple; the options are
/// content. A same-identity declaration with different options is one
/// evolution of the same index; any identity change is a different
/// index, not a drift of this one.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum VectorIndexDrift {
    /// The declarations are identical.
    Unchanged,
    /// Same identity, different options: one evolution of the same index.
    OptionsChanged,
    /// Different identity (name, collection, or field): a different
    /// index, not a drift of this one.
    IdentityChanged,
}

impl VectorIndexDrift {
    /// Reports whether the drift requires rebuilding indexed data.
    ///
    /// An options change alters what the index must contain, so it
    /// requires a rebuild; an identity change names a different index —
    /// it never obligates this one — and no drift does not.
    #[must_use]
    pub const fn requires_rebuild(self) -> bool {
        matches!(self, Self::OptionsChanged)
    }
}

/// A typed vector index declaration over one field of one ordinary
/// collection.
///
/// The declaration is the MCP-exposable authoring shape (PD-2026-08-26-07
/// D1: typed operations, no raw escape hatch): it names the index under
/// the static-index name rules, binds it to one user collection and one
/// document field, and carries the typed [`VectorOptions`]. Names follow
/// the GAP-Q06 D10 adaptation of V1's `IndexStore.IsValidIndexName`
/// rules, applied by the static index language's own validator
/// (`StaticIndexName::try_new`): the V1 name alphabet, no double slash,
/// no misplaced dot, at most
/// [`MAX_NAME_BYTES`](crate::index::definitions::MAX_NAME_BYTES) bytes,
/// and no occupancy of the reserved auto namespace.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct VectorIndexDeclaration {
    name: String,
    collection: CollectionName,
    field: String,
    options: VectorOptions,
}

impl VectorIndexDeclaration {
    /// Creates one declaration after validating name, collection, field,
    /// and options.
    ///
    /// # Errors
    ///
    /// Returns [`VectorIndexError::InvalidName`] for an invalid name,
    /// [`VectorIndexError::AutoNamespaceReserved`] for a name in the
    /// reserved auto namespace, [`VectorIndexError::ReservedCollection`]
    /// for the reserved family collection, and
    /// [`VectorIndexError::InvalidField`] for an invalid field name.
    pub fn try_new(
        name: &str,
        collection: CollectionName,
        field: &str,
        options: VectorOptions,
    ) -> Result<Self, VectorIndexError> {
        validate_index_name(name)?;
        if collection.as_str() == RESERVED_FAMILY_COLLECTION {
            return Err(VectorIndexError::ReservedCollection);
        }
        validate_field_name(field).map_err(|_| VectorIndexError::InvalidField)?;
        Ok(Self { name: name.to_owned(), collection, field: field.to_owned(), options })
    }

    /// Returns the index name.
    #[must_use]
    pub fn name(&self) -> &str {
        &self.name
    }

    /// Returns the indexed collection.
    #[must_use]
    pub fn collection(&self) -> &CollectionName {
        &self.collection
    }

    /// Returns the indexed document field.
    #[must_use]
    pub fn field(&self) -> &str {
        &self.field
    }

    /// Returns the typed options.
    #[must_use]
    pub fn options(&self) -> &VectorOptions {
        &self.options
    }

    /// Returns the deterministic content identity of the declaration.
    ///
    /// The identity renders the name, collection, field, and the full
    /// option content (including the construction counts) in canonical
    /// order — the GAP-Q06 `identity_key` pattern — so two declarations
    /// with equal content render equal identities regardless of
    /// construction order, and any content change is a different
    /// identity.
    #[must_use]
    pub fn identity_key(&self) -> String {
        format!(
            "vec\u{1f}{}\u{1f}{}\u{1f}{}\u{1f}{}",
            self.name,
            self.collection.as_str(),
            self.field,
            self.options.identity()
        )
    }

    /// Records the drift of this declaration relative to `other`.
    ///
    /// See [`VectorIndexDrift`] for the identity/content split.
    #[must_use]
    pub fn drift(&self, other: &Self) -> VectorIndexDrift {
        let same_identity = self.name == other.name
            && self.collection == other.collection
            && self.field == other.field;
        if !same_identity {
            return VectorIndexDrift::IdentityChanged;
        }
        if self.options == other.options {
            VectorIndexDrift::Unchanged
        } else {
            VectorIndexDrift::OptionsChanged
        }
    }
}

/// Validates one vector index name under the static-index name rules.
///
/// The validation IS the static index language's own —
/// [`StaticIndexName::try_new`] — rather than a re-statement of it, so
/// the vector name rule and the static index name rule cannot drift
/// apart. The behavior is unchanged by the delegation: the same V1 name
/// alphabet (D3), the same auto-namespace refusal.
fn validate_index_name(name: &str) -> Result<(), VectorIndexError> {
    match StaticIndexName::try_new(name) {
        Ok(_) => Ok(()),
        Err(IndexDefinitionError::ReservedAutoName) => Err(VectorIndexError::AutoNamespaceReserved),
        Err(_) => Err(VectorIndexError::InvalidName),
    }
}

// ---------------------------------------------------------------------------
// Request (PB-4024)
// ---------------------------------------------------------------------------

/// The similarity measure of one vector search.
///
/// The first landing serves cosine similarity only — V1's
/// `CosineSimilaritySingles` similarity method of
/// `Hnsw.SimilarityMethods`; the Int8 and Hamming measures ride the
/// deferred quantization work.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum VectorSimilarity {
    /// Cosine similarity in [-1, 1]; the score is the similarity itself,
    /// exactly what V1 reports after `DistanceToScoreCosine` converts its
    /// internal cosine distance (`1 - distance`). The range is enforced,
    /// not assumed: f64 rounding of an exact match can overshoot 1.0 by
    /// one ulp, and the scorer clamps that back into the mathematical
    /// range before the score is reported or ordered.
    Cosine,
}

/// The execution mode of one vector search.
///
/// V1 separates exact from approximate search on its client seam
/// (`isExact`, `IAbstractDocumentQuery.VectorSearch`); V2 keeps the two
/// as distinct typed modes. Only [`Exact`](Self::Exact) is served by the
/// execution path this landing ships; [`Approximate`](Self::Approximate)
/// is the typed mode the ANN follow-up will produce, and no request can
/// select it yet.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum VectorSearchMode {
    /// Exact scoring of every candidate vector.
    Exact,
    /// Approximate nearest-neighbor search over an ANN structure —
    /// reserved for the follow-up recorded in the decision record; no
    /// execution path produces it.
    Approximate,
}

/// The typed failures of the vector search surface: construction,
/// lowering, and execution.
///
/// Every case is typed and carries no document value, no document
/// identity, and no storage detail — the fail-closed, content-safe rule
/// of the seam's error taxonomy.
#[derive(Clone, Debug)]
pub enum VectorSearchError {
    /// The k-NN limit is zero or above
    /// [`MAX_VECTOR_SEARCH_LIMIT`].
    InvalidLimit {
        /// The refused limit.
        requested: u32,
    },
    /// The query vector is empty. V1 refuses a `VectorSearch` without an
    /// input ("Cannot use `VectorSearch` without text(s), document id or
    /// embedding(s).",
    /// `AbstractDocumentQuery.VectorSearch`).
    EmptyQueryVector,
    /// The query vector's dimensionality is below
    /// [`MIN_VECTOR_DIMENSIONS`].
    DimensionsBelowMinimum {
        /// The refused dimensionality.
        dimensions: usize,
    },
    /// The query vector's dimensionality is above
    /// [`MAX_VECTOR_DIMENSIONS`].
    DimensionsAboveMaximum {
        /// The refused dimensionality.
        dimensions: usize,
    },
    /// A query-vector component is not finite.
    NonFiniteQueryVector,
    /// The similarity threshold is outside the inclusive cosine range
    /// [-1, 1], or not a number.
    InvalidSimilarityThreshold {
        /// The refused threshold.
        requested: f64,
    },
    /// A bound parameter lowered into a query vector is not a numeric
    /// array.
    InvalidQueryVector,
    /// The vector field name is invalid.
    InvalidField,
    /// The query vector's dimensionality does not match the bound
    /// context — a declaration's declared dimensions, or a stored
    /// vector's dimensionality at execution.
    DimensionMismatch {
        /// The expected dimensionality.
        expected: usize,
        /// The encountered dimensionality.
        actual: usize,
    },
    /// A stored vector field is malformed: a non-array field, a
    /// non-numeric component, a non-finite component, or a magnitude
    /// that overflows the score. Execution fails closed rather than
    /// scoring a malformed vector as anything.
    InvalidStoredVector,
    /// The request deadline expired at a bounded checkpoint.
    Deadline,
    /// The request was cancelled at a bounded checkpoint.
    Cancelled,
    /// The snapshot belongs to another Engine.
    SnapshotEngineMismatch,
    /// The Engine has closed.
    EngineClosed,
    /// The snapshot view returned a typed storage failure.
    StorageFailure,
}

impl PartialEq for VectorSearchError {
    /// Structural equality with one totalizing rule: the threshold
    /// payload compares NaN-equal, because a NaN refusal *is* the NaN —
    /// two `InvalidSimilarityThreshold` refusals of a not-a-number
    /// threshold are one refusal, and a derived `PartialEq` (under which
    /// NaN never equals NaN) would make the class untestable against
    /// itself. Every other payload compares structurally.
    fn eq(&self, other: &Self) -> bool {
        // Different variants are never equal; the check also makes the
        // merged dimension-bound arms below unreachable for cross-variant
        // pairs, so the merge cannot blur the two classes.
        if std::mem::discriminant(self) != std::mem::discriminant(other) {
            return false;
        }
        match (self, other) {
            (Self::InvalidLimit { requested: left }, Self::InvalidLimit { requested: right }) => {
                left == right
            }
            (
                Self::DimensionsBelowMinimum { dimensions: left }
                | Self::DimensionsAboveMaximum { dimensions: left },
                Self::DimensionsBelowMinimum { dimensions: right }
                | Self::DimensionsAboveMaximum { dimensions: right },
            ) => left == right,
            (
                Self::InvalidSimilarityThreshold { requested: left },
                Self::InvalidSimilarityThreshold { requested: right },
            ) => (left.is_nan() && right.is_nan()) || left == right,
            (
                Self::DimensionMismatch { expected: expected_left, actual: actual_left },
                Self::DimensionMismatch { expected: expected_right, actual: actual_right },
            ) => expected_left == expected_right && actual_left == actual_right,
            // The fieldless variants equal by the discriminant check above.
            _ => true,
        }
    }
}

/// A typed filtered k-NN request against one field of one collection.
///
/// The request is the CP-204 surface: vector search callable from the
/// query seam with ordinary seam predicates as its filter — the
/// better-than measure against the competitor's actions-only restriction
/// (EV-C8) — with the dimension bounds (2..4096) and the limit bounds
/// (1..256) of the competitive bar typed at construction.
#[derive(Clone, Debug, PartialEq)]
pub struct VectorSearchRequest {
    source: QuerySource,
    field: String,
    vector: Vec<f32>,
    limit: u32,
    minimum_similarity: Option<f64>,
    filter: Option<QueryPredicate>,
    deadline: Option<Deadline>,
    cancellation: Option<Cancellation>,
}

impl VectorSearchRequest {
    /// Creates one request over a by-value query vector.
    ///
    /// # Errors
    ///
    /// Returns [`VectorSearchError::InvalidLimit`] for a limit outside
    /// 1..=[`MAX_VECTOR_SEARCH_LIMIT`], [`VectorSearchError::InvalidField`]
    /// for an invalid field name,
    /// [`VectorSearchError::EmptyQueryVector`] for an empty vector,
    /// [`VectorSearchError::DimensionsBelowMinimum`] or
    /// [`VectorSearchError::DimensionsAboveMaximum`] for a
    /// dimensionality outside the closed range, and
    /// [`VectorSearchError::NonFiniteQueryVector`] for a non-finite
    /// component.
    #[allow(clippy::needless_pass_by_value)] // the vector is stored, not borrowed
    pub fn try_new(
        source: QuerySource,
        field: &str,
        vector: Vec<f32>,
        limit: u32,
    ) -> Result<Self, VectorSearchError> {
        validate_field_name(field).map_err(|_| VectorSearchError::InvalidField)?;
        if limit == 0 || limit > MAX_VECTOR_SEARCH_LIMIT {
            return Err(VectorSearchError::InvalidLimit { requested: limit });
        }
        validate_query_vector(&vector)?;
        Ok(Self {
            source,
            field: field.to_owned(),
            vector,
            limit,
            minimum_similarity: None,
            filter: None,
            deadline: None,
            cancellation: None,
        })
    }

    /// Creates one request bound to a declaration's collection, field,
    /// and declared dimensionality.
    ///
    /// # Errors
    ///
    /// Returns [`VectorSearchError::DimensionMismatch`] when the query
    /// vector's dimensionality differs from the declared one, then every
    /// refusal of [`VectorSearchRequest::try_new`].
    #[allow(clippy::needless_pass_by_value)] // the vector is stored, not borrowed
    pub fn for_declaration(
        declaration: &VectorIndexDeclaration,
        vector: Vec<f32>,
        limit: u32,
    ) -> Result<Self, VectorSearchError> {
        let expected = usize::from(declaration.options().dimensions());
        let actual = vector.len();
        if actual != expected {
            return Err(VectorSearchError::DimensionMismatch { expected, actual });
        }
        let source = QuerySource::try_new(declaration.collection().clone())
            .map_err(|_| VectorSearchError::InvalidField)?;
        Self::try_new(source, declaration.field(), vector, limit)
    }

    /// Returns the typed source.
    #[must_use]
    pub fn source(&self) -> &QuerySource {
        &self.source
    }

    /// Returns the searched field name.
    #[must_use]
    pub fn field(&self) -> &str {
        &self.field
    }

    /// Returns the query vector.
    #[must_use]
    pub fn vector(&self) -> &[f32] {
        &self.vector
    }

    /// Returns the query vector's dimensionality.
    #[must_use]
    pub fn dimensions(&self) -> usize {
        self.vector.len()
    }

    /// Returns the k-NN limit.
    #[must_use]
    pub const fn limit(&self) -> u32 {
        self.limit
    }

    /// Returns the minimum similarity threshold, when set.
    #[must_use]
    pub const fn minimum_similarity(&self) -> Option<f64> {
        self.minimum_similarity
    }

    /// Returns the filter predicate, when set.
    #[must_use]
    pub const fn filter(&self) -> Option<&QueryPredicate> {
        self.filter.as_ref()
    }

    /// Returns the request's deadline: the bound one, or a fresh
    /// [`VECTOR_SEARCH_WINDOW`] window when none is bound — the include
    /// read-window rule, so no execution is ever unbounded by default.
    ///
    /// # Panics
    ///
    /// Panics never: the default window is a positive constant, so the
    /// fallback construction cannot fail — the expectation documents the
    /// invariant, it is not a reachable failure.
    #[must_use]
    pub fn deadline(&self) -> Deadline {
        self.deadline
            .unwrap_or_else(|| Deadline::after(VECTOR_SEARCH_WINDOW).expect("positive window"))
    }

    /// Returns the request's cancellation signal: the bound one, or a
    /// fresh uncancelled signal when none is bound.
    #[must_use]
    pub fn cancellation(&self) -> Cancellation {
        self.cancellation.clone().unwrap_or_default()
    }

    /// Returns the execution mode this request serves: always
    /// [`Exact`](VectorSearchMode::Exact) — the approximation vocabulary
    /// arrives with the ANN follow-up.
    #[must_use]
    pub const fn mode(&self) -> VectorSearchMode {
        VectorSearchMode::Exact
    }

    /// Returns the similarity measure: always
    /// [`Cosine`](VectorSimilarity::Cosine).
    #[must_use]
    pub const fn similarity(&self) -> VectorSimilarity {
        VectorSimilarity::Cosine
    }

    /// Sets the minimum similarity threshold over the inclusive cosine
    /// range [-1, 1] — V1's `minimumSimilarity`.
    ///
    /// # Errors
    ///
    /// Returns [`VectorSearchError::InvalidSimilarityThreshold`] for a
    /// threshold outside the range or not a number.
    pub fn with_minimum_similarity(
        mut self,
        minimum_similarity: f64,
    ) -> Result<Self, VectorSearchError> {
        if !minimum_similarity.is_finite() || !(-1.0..=1.0).contains(&minimum_similarity) {
            return Err(VectorSearchError::InvalidSimilarityThreshold {
                requested: minimum_similarity,
            });
        }
        self.minimum_similarity = Some(minimum_similarity);
        Ok(self)
    }

    /// Sets the pre-scoring filter predicate. The predicate is
    /// pre-validated by its own constructor; filtering happens before
    /// scoring, so no vector is scored for a document the filter
    /// excludes.
    #[must_use]
    pub fn with_filter(mut self, filter: QueryPredicate) -> Self {
        self.filter = Some(filter);
        self
    }

    /// Binds a request deadline, checked at bounded execution
    /// checkpoints.
    #[must_use]
    pub fn with_deadline(mut self, deadline: Deadline) -> Self {
        self.deadline = Some(deadline);
        self
    }

    /// Binds a cancellation signal, checked at bounded execution
    /// checkpoints. A signal that is already cancelled is accepted here
    /// and refuses at execution.
    #[must_use]
    pub fn with_cancellation(mut self, cancellation: Cancellation) -> Self {
        self.cancellation = Some(cancellation);
        self
    }
}

/// Validates one query vector: non-empty, inside the closed dimension
/// range, and entirely finite.
fn validate_query_vector(vector: &[f32]) -> Result<(), VectorSearchError> {
    if vector.is_empty() {
        return Err(VectorSearchError::EmptyQueryVector);
    }
    if vector.len() < MIN_VECTOR_DIMENSIONS {
        return Err(VectorSearchError::DimensionsBelowMinimum { dimensions: vector.len() });
    }
    if vector.len() > MAX_VECTOR_DIMENSIONS {
        return Err(VectorSearchError::DimensionsAboveMaximum { dimensions: vector.len() });
    }
    if vector.iter().any(|component| !component.is_finite()) {
        return Err(VectorSearchError::NonFiniteQueryVector);
    }
    Ok(())
}

// ---------------------------------------------------------------------------
// The typed vector predicate (lowered directly into typed requests)
// ---------------------------------------------------------------------------

/// The typed, versioned vector predicate: a field, a bound parameter name,
/// and the threshold/limit options.
///
/// The predicate lowers — together with the bound parameter value — into
/// the engine's typed [`VectorSearchRequest`]. The vector operand stays a
/// `$parameter`: vectors are data, never SQL text.
#[derive(Clone, Debug, PartialEq)]
pub struct VectorPredicate {
    field: String,
    parameter: String,
    threshold: Option<f64>,
    limit: Option<u32>,
}

impl VectorPredicate {
    /// Creates one predicate after validating its parts.
    ///
    /// The field and parameter names use the bare-identifier alphabet
    /// (ASCII letter or underscore first, then ASCII letters, digits, or
    /// underscores): a name carrying SQL metacharacters refuses typed.
    ///
    /// # Errors
    ///
    /// Returns [`VectorSearchError::InvalidField`] for a field or
    /// parameter name outside the bare-identifier alphabet,
    /// [`VectorSearchError::InvalidLimit`] for a limit outside
    /// 1..=[`MAX_VECTOR_SEARCH_LIMIT`], and
    /// [`VectorSearchError::InvalidSimilarityThreshold`] for a threshold
    /// outside the inclusive cosine range or not a number.
    pub fn try_new(
        field: &str,
        parameter: &str,
        threshold: Option<f64>,
        limit: Option<u32>,
    ) -> Result<Self, VectorSearchError> {
        if !validate_sql_identifier(field) || !validate_sql_identifier(parameter) {
            return Err(VectorSearchError::InvalidField);
        }
        if let Some(limit) = limit
            && (limit == 0 || limit > MAX_VECTOR_SEARCH_LIMIT)
        {
            return Err(VectorSearchError::InvalidLimit { requested: limit });
        }
        if let Some(threshold) = threshold
            && (!threshold.is_finite() || !(-1.0..=1.0).contains(&threshold))
        {
            return Err(VectorSearchError::InvalidSimilarityThreshold { requested: threshold });
        }
        Ok(Self { field: field.to_owned(), parameter: parameter.to_owned(), threshold, limit })
    }

    /// Returns the searched field name.
    #[must_use]
    pub fn field(&self) -> &str {
        &self.field
    }

    /// Returns the bound parameter's name.
    #[must_use]
    pub fn parameter(&self) -> &str {
        &self.parameter
    }

    /// Returns the minimum similarity threshold, when set.
    #[must_use]
    pub const fn threshold(&self) -> Option<f64> {
        self.threshold
    }

    /// Returns the limit, when set.
    #[must_use]
    pub const fn limit(&self) -> Option<u32> {
        self.limit
    }

    /// Returns the predicate's version identity.
    #[must_use]
    pub const fn version(&self) -> &'static str {
        VECTOR_PREDICATE_VERSION
    }

    /// Lowers the predicate and one bound parameter value into the
    /// engine's typed request.
    ///
    /// A predicate without an explicit limit lowers to
    /// [`DEFAULT_VECTOR_SEARCH_LIMIT`].
    ///
    /// # Errors
    ///
    /// Returns [`VectorSearchError::InvalidQueryVector`] when the bound
    /// value is not a numeric array, then every refusal of
    /// [`VectorSearchRequest::try_new`] and
    /// [`VectorSearchRequest::with_minimum_similarity`].
    #[allow(
        clippy::cast_precision_loss,
        clippy::cast_possible_truncation,
        reason = "the integer and number document encodings lower to the engine's f32 embeddings; \
                  a wide integer narrows exactly as an embedding model's output would"
    )]
    pub fn lower(
        &self,
        source: QuerySource,
        bound: &DocumentValue,
    ) -> Result<VectorSearchRequest, VectorSearchError> {
        let DocumentValue::Array(components) = bound else {
            return Err(VectorSearchError::InvalidQueryVector);
        };
        let mut vector = Vec::with_capacity(components.len());
        for component in components {
            let number = match component {
                DocumentValue::Number(number) => *number,
                DocumentValue::Integer(integer) => *integer as f64,
                _ => return Err(VectorSearchError::InvalidQueryVector),
            };
            vector.push(number as f32);
        }
        let request = VectorSearchRequest::try_new(
            source,
            self.field.as_str(),
            vector,
            self.limit.unwrap_or(DEFAULT_VECTOR_SEARCH_LIMIT),
        )?;
        match self.threshold {
            Some(threshold) => request.with_minimum_similarity(threshold),
            None => Ok(request),
        }
    }
}

/// Validates one bare identifier — a field or parameter name: non-empty, at
/// most [`MAX_FIELD_BYTES`](super::spec::MAX_FIELD_BYTES) bytes, ASCII
/// letter or underscore first, then ASCII letters, digits, or underscores.
///
/// The bare-identifier alphabet carries none of SQL's metacharacters
/// (comma, parenthesis, space, `$`). The field-name rule of `spec.rs` is
/// looser (it must admit the seam's dotted field paths); binding names ask
/// the stricter question.
fn validate_sql_identifier(name: &str) -> bool {
    if name.is_empty() || name.len() > MAX_FIELD_BYTES {
        return false;
    }
    let mut characters = name.chars();
    let starts_like_an_identifier =
        matches!(characters.next(), Some(first) if first.is_ascii_alphabetic() || first == '_');
    starts_like_an_identifier
        && characters.all(|character| character.is_ascii_alphanumeric() || character == '_')
}

// ---------------------------------------------------------------------------
// Execution: result, statistics, and the query-seam entry point
// ---------------------------------------------------------------------------

/// One scored match of a vector search: the document identity, the
/// similarity score, and the matched document.
#[derive(Clone, Debug, PartialEq)]
pub struct VectorMatch {
    id: DocumentId,
    score: f64,
    document: Document,
}

impl VectorMatch {
    /// Returns the matched document's identity.
    #[must_use]
    pub fn id(&self) -> &DocumentId {
        &self.id
    }

    /// Returns the cosine similarity score, clamped to [-1, 1].
    ///
    /// The clamp is the enforcement of the documented range: the raw
    /// quotient of an exact match can exceed 1.0 by one ulp of f64
    /// rounding (`3 / (sqrt(3) * sqrt(3))`), and a reported score must
    /// not.
    #[must_use]
    pub const fn score(&self) -> f64 {
        self.score
    }

    /// Returns the matched document.
    #[must_use]
    pub fn document(&self) -> &Document {
        &self.document
    }
}

/// The accounting of one vector search execution: the FILTER-style
/// counts of the query seam's statistics contract, in the k-NN pipeline's
/// own stages.
///
/// [`scanned`](Self::scanned) counts the candidate documents of the
/// source collection; [`filter_skipped`](Self::filter_skipped),
/// [`missing_field`](Self::missing_field), and
/// [`threshold_dropped`](Self::threshold_dropped) count the candidates
/// the pipeline dropped at each stage; [`scored`](Self::scored) counts
/// the vectors scored; and [`returned`](Self::returned) counts the
/// matches the k-NN window kept. Every count is an exact, saturating
/// `u64`.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct VectorSearchStatistics {
    scanned: u64,
    filter_skipped: u64,
    missing_field: u64,
    scored: u64,
    threshold_dropped: u64,
    returned: u64,
}

impl VectorSearchStatistics {
    /// Assembles the statistics of one execution. Crate-only: execution
    /// is the only producer.
    pub(crate) const fn new(
        scanned: u64,
        filter_skipped: u64,
        missing_field: u64,
        scored: u64,
        threshold_dropped: u64,
        returned: u64,
    ) -> Self {
        Self { scanned, filter_skipped, missing_field, scored, threshold_dropped, returned }
    }

    /// Returns the number of candidate documents of the source
    /// collection the pipeline considered.
    #[must_use]
    pub const fn scanned(&self) -> u64 {
        self.scanned
    }

    /// Returns the number of candidates the filter predicate excluded
    /// before scoring.
    #[must_use]
    pub const fn filter_skipped(&self) -> u64 {
        self.filter_skipped
    }

    /// Returns the number of candidates without the vector field — the
    /// schema-optional rule: plain documents are non-candidates, never
    /// errors.
    #[must_use]
    pub const fn missing_field(&self) -> u64 {
        self.missing_field
    }

    /// Returns the number of stored vectors scored.
    #[must_use]
    pub const fn scored(&self) -> u64 {
        self.scored
    }

    /// Returns the number of scored matches the similarity threshold
    /// dropped.
    #[must_use]
    pub const fn threshold_dropped(&self) -> u64 {
        self.threshold_dropped
    }

    /// Returns the number of matches the k-NN window returned.
    #[must_use]
    pub const fn returned(&self) -> u64 {
        self.returned
    }
}

/// The result of one vector search execution.
#[derive(Clone, Debug, PartialEq)]
pub struct VectorSearchResult {
    matches: Vec<VectorMatch>,
    similarity: VectorSimilarity,
    mode: VectorSearchMode,
    statistics: VectorSearchStatistics,
}

impl VectorSearchResult {
    /// Assembles one result. Crate-only: execution is the only producer.
    pub(crate) const fn new(
        matches: Vec<VectorMatch>,
        similarity: VectorSimilarity,
        mode: VectorSearchMode,
        statistics: VectorSearchStatistics,
    ) -> Self {
        Self { matches, similarity, mode, statistics }
    }

    /// Returns the matches in result order: score descending, ties by
    /// document identity.
    #[must_use]
    pub fn matches(&self) -> &[VectorMatch] {
        &self.matches
    }

    /// Returns the similarity measure of the execution.
    #[must_use]
    pub const fn similarity(&self) -> VectorSimilarity {
        self.similarity
    }

    /// Returns the execution mode.
    #[must_use]
    pub const fn mode(&self) -> VectorSearchMode {
        self.mode
    }

    /// Reports whether the execution scored every candidate exactly.
    #[must_use]
    pub const fn exact(&self) -> bool {
        matches!(self.mode, VectorSearchMode::Exact)
    }

    /// Returns the execution's accounting.
    #[must_use]
    pub const fn statistics(&self) -> &VectorSearchStatistics {
        &self.statistics
    }
}

impl QueryStore<'_> {
    /// Executes one filtered k-NN request against one immutable
    /// snapshot: the CP-204 surface — vector search callable from the
    /// query seam, filterable by ordinary seam predicates.
    ///
    /// The pipeline validates the snapshot's Engine identity before any
    /// data access, scans the snapshot view of the source collection,
    /// applies the request's filter predicate before scoring, scores
    /// every surviving stored vector exactly by cosine similarity,
    /// drops below-threshold matches, orders by score descending with
    /// ties broken by document identity, and truncates to the request's
    /// limit. One snapshot always yields one order; one snapshot and one
    /// request always yield one result.
    ///
    /// # Errors
    ///
    /// Returns [`VectorSearchError::SnapshotEngineMismatch`] for a
    /// snapshot from another Engine, [`VectorSearchError::Deadline`] or
    /// [`VectorSearchError::Cancelled`] at a bounded checkpoint,
    /// [`VectorSearchError::DimensionMismatch`] when a stored vector's
    /// dimensionality differs from the query vector's,
    /// [`VectorSearchError::InvalidStoredVector`] for a malformed stored
    /// vector, [`VectorSearchError::EngineClosed`] when the Engine has
    /// closed, and [`VectorSearchError::StorageFailure`] when the
    /// snapshot view fails.
    pub fn vector_search(
        &self,
        request: &VectorSearchRequest,
        snapshot: &ReadSnapshot,
    ) -> Result<VectorSearchResult, VectorSearchError> {
        self.vector_search_within(request, snapshot, VECTOR_SEARCH_WINDOW)
    }

    /// Crate-only execution seam: [`vector_search`](Self::vector_search)
    /// with an explicit default execution window.
    ///
    /// The window is the D6 bound a request that binds no deadline of its
    /// own executes inside; taking it as a parameter keeps the public
    /// constant the one source of truth while the bound itself stays
    /// provable in tests — a const one-minute window cannot be expired on
    /// a wall clock inside a test run.
    pub(crate) fn vector_search_within(
        &self,
        request: &VectorSearchRequest,
        snapshot: &ReadSnapshot,
        default_window: std::time::Duration,
    ) -> Result<VectorSearchResult, VectorSearchError> {
        // D6: the execution bounds are materialized once, at entry — the
        // include read-window pattern (`includes.rs`'s `read_deadline`).
        // A request without a bound deadline executes inside the default
        // window and one without a bound signal carries a fresh
        // uncancelled signal, so no execution is unbounded by default and
        // every checkpoint below enforces the same materialized bound.
        let deadline = request
            .deadline
            .unwrap_or_else(|| Deadline::after(default_window).expect("positive default window"));
        let cancellation = request.cancellation.clone().unwrap_or_default();
        // The entry checkpoint fires before the snapshot view is
        // materialized: an already-dead or already-cancelled request
        // refuses before any data access. (The view materialization
        // itself takes no deadline — the same index-less base path
        // `execute.rs` records; the checkpoints bound everything around
        // and after it.)
        check_bounds(Some(deadline), Some(&cancellation)).map_err(map_bound_error)?;
        // The snapshot supplies the tenant binding and its own Engine
        // identity; the Engine validates both inside the view, before
        // any data access reaches storage.
        let scope = TenantScope::from_parts(snapshot.tenant().clone(), snapshot.engine_identity());
        let documents = self
            .engine
            .snapshot_documents(&scope, snapshot)
            .map_err(|error| map_engine_error(&error))?;
        check_bounds(Some(deadline), Some(&cancellation)).map_err(map_bound_error)?;

        let dimensions = request.dimensions();
        let mut candidates: Vec<(f64, &Document)> = Vec::new();
        let mut scanned: u64 = 0;
        let mut filter_skipped: u64 = 0;
        let mut missing_field: u64 = 0;
        let mut scored: u64 = 0;
        let mut threshold_dropped: u64 = 0;
        for (position, document) in documents.iter().enumerate() {
            if position % CHECKPOINT_INTERVAL == 0 {
                check_bounds(Some(deadline), Some(&cancellation)).map_err(map_bound_error)?;
            }
            // GAP-Q19 collision action: the query source's collection is
            // optional now (family and all-documents sources name none);
            // a source without a collection does not filter by
            // collection. Family sources route around this executor
            // path, so the None arm is unreachable defense.
            if request
                .source()
                .collection()
                .is_none_or(|collection| document.collection() != collection)
            {
                continue;
            }
            scanned = scanned.saturating_add(1);
            // The filter predicates candidates before scoring: no vector
            // is scored for a document the filter excludes.
            if let Some(filter) = request.filter.as_ref()
                && !model::matches(filter, document)
            {
                filter_skipped = filter_skipped.saturating_add(1);
                continue;
            }
            // Schema-optional (PD-2026-08-26-04 D1): a document without
            // the vector field is a non-candidate, never an error.
            let Some(stored_field) = model::field_of(document, request.field.as_str()) else {
                missing_field = missing_field.saturating_add(1);
                continue;
            };
            let vector =
                stored_vector(stored_field).ok_or(VectorSearchError::InvalidStoredVector)?;
            if vector.len() != dimensions {
                return Err(VectorSearchError::DimensionMismatch {
                    expected: dimensions,
                    actual: vector.len(),
                });
            }
            let similarity = cosine_similarity(&request.vector, &vector)
                .ok_or(VectorSearchError::InvalidStoredVector)?;
            scored = scored.saturating_add(1);
            if request.minimum_similarity.is_some_and(|threshold| similarity < threshold) {
                threshold_dropped = threshold_dropped.saturating_add(1);
                continue;
            }
            candidates.push((similarity, document));
        }
        check_bounds(Some(deadline), Some(&cancellation)).map_err(map_bound_error)?;

        candidates.sort_by(compare_candidates);
        let limit = usize::try_from(request.limit).unwrap_or(usize::MAX);
        candidates.truncate(limit);
        let returned = u64::try_from(candidates.len()).unwrap_or(u64::MAX);
        let matches = candidates
            .into_iter()
            .map(|(similarity, document)| VectorMatch {
                id: document.id().clone(),
                score: similarity,
                document: document.clone(),
            })
            .collect();
        Ok(VectorSearchResult::new(
            matches,
            request.similarity(),
            request.mode(),
            VectorSearchStatistics::new(
                scanned,
                filter_skipped,
                missing_field,
                scored,
                threshold_dropped,
                returned,
            ),
        ))
    }
}

/// THE determinism rule's comparator: score descending, and a score tie
/// resolves by document identity — one total order that neither scan
/// order nor chance can change. Scores are finite by the fail-closed
/// scoring rule, so the comparison is total.
///
/// Visible to the in-crate tests because the tie-break is load-bearing
/// exactly when storage iteration is not the canonical identity order:
/// the execution corpus cannot discriminate it (the snapshot view
/// already iterates documents in canonical key order), so the comparator
/// is pinned directly.
pub(super) fn compare_candidates(left: &(f64, &Document), right: &(f64, &Document)) -> StdOrdering {
    right
        .0
        .partial_cmp(&left.0)
        .unwrap_or(StdOrdering::Equal)
        .then_with(|| compare_identity(left.1, right.1))
}

/// Compares two documents by identity: collection, then identifier — a
/// total order over the tenant's documents.
fn compare_identity(left: &Document, right: &Document) -> StdOrdering {
    left.collection()
        .as_str()
        .cmp(right.collection().as_str())
        .then_with(|| left.id().as_str().cmp(right.id().as_str()))
}

/// Maps an Engine error of the snapshot view onto the typed vector
/// errors.
fn map_engine_error(error: &crate::engine::EngineError) -> VectorSearchError {
    match error {
        // A scope mismatch can only mean the snapshot claims another
        // Engine.
        crate::engine::EngineError::SnapshotEngineMismatch
        | crate::engine::EngineError::ScopeMismatch => VectorSearchError::SnapshotEngineMismatch,
        crate::engine::EngineError::Deadline => VectorSearchError::Deadline,
        crate::engine::EngineError::Cancelled => VectorSearchError::Cancelled,
        crate::engine::EngineError::Closed(_) => VectorSearchError::EngineClosed,
        // The scope is built from the snapshot's own tenant, so a tenant
        // mismatch cannot arise on this path; it and every other failure
        // keep the storage-failure class.
        crate::engine::EngineError::SnapshotTenantMismatch
        | crate::engine::EngineError::StorageFailure
        | crate::engine::EngineError::SnapshotPageLimitExceeded
        | crate::engine::EngineError::SnapshotBoundaryUnavailable
        | crate::engine::EngineError::Conflict
        | crate::engine::EngineError::InvalidDocument
        | crate::engine::EngineError::OperationIdReuse { .. } => VectorSearchError::StorageFailure,
    }
}

/// Maps a bounded-checkpoint refusal onto the typed vector errors.
fn map_bound_error(error: super::QueryError) -> VectorSearchError {
    match error {
        super::QueryError::Deadline => VectorSearchError::Deadline,
        super::QueryError::Cancelled => VectorSearchError::Cancelled,
        // check_bounds produces no other class.
        _ => VectorSearchError::StorageFailure,
    }
}

/// Reads one stored vector field: a numeric array of finite components.
///
/// `None` is the malformed class — a non-array field, a non-numeric
/// component, or a non-finite component — which execution fails closed
/// on rather than scoring.
#[allow(
    clippy::cast_precision_loss,
    reason = "an integer component of a stored vector reads as its nearest f64; \
              the engine scores f64 magnitudes either way"
)]
fn stored_vector(value: &DocumentValue) -> Option<Vec<f64>> {
    let DocumentValue::Array(components) = value else {
        return None;
    };
    let mut vector = Vec::with_capacity(components.len());
    for component in components {
        let number = match component {
            DocumentValue::Number(number) => *number,
            DocumentValue::Integer(integer) => *integer as f64,
            _ => return None,
        };
        if !number.is_finite() {
            return None;
        }
        vector.push(number);
    }
    Some(vector)
}

/// Computes the cosine similarity of one finite query vector and one
/// finite stored vector, in f64.
///
/// The zero-magnitude rule: a zero query vector or a zero stored vector
/// has no direction, and scores exactly `0.0` — a defined answer, never
/// a NaN. `None` marks a score that is not finite (a magnitude
/// overflow), which the caller fails closed on; a finite score is
/// clamped into [-1, 1], because sqrt-then-multiply rounding can push
/// the exact match of a stored vector one ulp past 1.0 — the
/// mathematical range is enforced, not assumed.
fn cosine_similarity(query: &[f32], stored: &[f64]) -> Option<f64> {
    let mut dot = 0.0;
    let mut query_magnitude = 0.0;
    let mut stored_magnitude = 0.0;
    for (query_component, stored_component) in query.iter().zip(stored.iter()) {
        let query_component = f64::from(*query_component);
        dot += query_component * stored_component;
        query_magnitude += query_component * query_component;
        stored_magnitude += stored_component * stored_component;
    }
    let denominator = query_magnitude.sqrt() * stored_magnitude.sqrt();
    if denominator == 0.0 {
        return Some(0.0);
    }
    let score = dot / denominator;
    if !score.is_finite() {
        return None;
    }
    Some(score.clamp(-1.0, 1.0))
}
