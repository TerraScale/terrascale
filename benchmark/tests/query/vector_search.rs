//! Public-path vector search tests (GAP-C4, TerraBaseV2#86).
//!
//! `TenantScope` has no public constructor by design (Platform creates it
//! after authorization), so the execution path — the exact cosine k-NN over
//! one immutable snapshot — is covered by the in-crate tests of
//! `src/query/vector_tests.rs`. What this file pins at the public seam is
//! everything constructible without a scope: the typed vector index
//! declaration and its validation, the filtered k-NN request and its typed
//! error taxonomy (empty query vector, dimension bounds, invalid limit,
//! invalid threshold, non-finite components), the deterministic identity and
//! drift model of a declaration, and the versioned TQL predicate handoff
//! shape whose grammar integration rides TQL-03 (#34).

use std::time::Duration;

use terrabase_v2::{
    Cancellation, Deadline, QueryPredicate, QuerySource,
    query::{
        MAX_VECTOR_DIMENSIONS, MAX_VECTOR_SEARCH_LIMIT, MIN_VECTOR_DIMENSIONS, QueryCondition,
        TqlVectorPredicate, VECTOR_PREDICATE_VERSION, VectorEmbeddingType, VectorIndexDeclaration,
        VectorIndexDrift, VectorIndexError, VectorOptions, VectorOptionsError, VectorSearchError,
        VectorSearchMode, VectorSearchRequest, VectorSimilarity,
    },
    types::{CollectionName, DocumentValue},
};

fn collection(name: &str) -> terrabase_v2::types::CollectionName {
    CollectionName::try_new(name).expect("valid collection name")
}

fn source(name: &str) -> QuerySource {
    QuerySource::try_new(collection(name)).expect("user collection")
}

fn options(dimensions: u16) -> VectorOptions {
    VectorOptions::try_new(dimensions, VectorEmbeddingType::Single, VectorEmbeddingType::Single)
        .expect("single-to-single options are the served configuration")
}

fn declaration(name: &str, dimensions: u16) -> VectorIndexDeclaration {
    VectorIndexDeclaration::try_new(name, collection("docs"), "embedding", options(dimensions))
        .expect("valid declaration")
}

fn vector(dimensions: usize) -> Vec<f32> {
    (0..dimensions).map(|index| (index % 7) as f32 + 1.0).collect()
}

// ---------------------------------------------------------------------------
// Declaration: construction and validation (PB-4051 anchor)
// ---------------------------------------------------------------------------

#[test]
fn single_to_single_declaration_is_the_served_configuration() {
    let declaration = declaration("docs/embedding", 3);
    assert_eq!(declaration.name(), "docs/embedding");
    assert_eq!(declaration.collection().as_str(), "docs");
    assert_eq!(declaration.field(), "embedding");
    assert_eq!(declaration.options().dimensions(), 3);
    assert_eq!(declaration.options().source(), VectorEmbeddingType::Single);
    assert_eq!(declaration.options().destination(), VectorEmbeddingType::Single);
    assert_eq!(declaration.options().candidates(), None);
    assert_eq!(declaration.options().edges(), None);
}

#[test]
fn dimension_bounds_match_the_competitive_bar() {
    // EV-C8: Convex ships `vectorIndex` dimensions 2..4096. The seam accepts
    // the same closed range and refuses both ends' neighbors typed.
    assert_eq!(MIN_VECTOR_DIMENSIONS, 2);
    assert_eq!(MAX_VECTOR_DIMENSIONS, 4096);
    assert_eq!(
        options(u16::try_from(MIN_VECTOR_DIMENSIONS).expect("in range")).dimensions(),
        2,
        "the minimum dimensionality is itself a valid space"
    );
    assert!(
        VectorOptions::try_new(
            u16::try_from(MAX_VECTOR_DIMENSIONS).expect("in range"),
            VectorEmbeddingType::Single,
            VectorEmbeddingType::Single
        )
        .is_ok()
    );
    assert_eq!(
        VectorOptions::try_new(1, VectorEmbeddingType::Single, VectorEmbeddingType::Single)
            .unwrap_err(),
        VectorOptionsError::DimensionsBelowMinimum,
        "a one-dimensional space is refused: the competitive bar starts at two"
    );
    assert_eq!(
        VectorOptions::try_new(4097, VectorEmbeddingType::Single, VectorEmbeddingType::Single)
            .unwrap_err(),
        VectorOptionsError::DimensionsAboveMaximum
    );
}

#[test]
fn text_destination_is_refused_as_v1_refuses_it() {
    // V1 VectorOptions.Validate: "Destination embedding type cannot be Text."
    assert_eq!(
        VectorOptions::try_new(3, VectorEmbeddingType::Single, VectorEmbeddingType::Text)
            .unwrap_err(),
        VectorOptionsError::TextDestination
    );
}

#[test]
fn text_source_is_refused_while_auto_embedding_is_deferred() {
    // V1 refuses a Text source with explicit dimensions ("Dimensions are set
    // internally by the embedder"); V2 dimensions are required and typed, so
    // a Text source is refused outright until the embeddings-task
    // integration lands — a typed deferral, never a silent no-op.
    assert_eq!(
        VectorOptions::try_new(3, VectorEmbeddingType::Text, VectorEmbeddingType::Single)
            .unwrap_err(),
        VectorOptionsError::AutoEmbeddingUnsupported
    );
}

#[test]
fn quantization_is_refused_typed_while_deferred() {
    // V1 refuses re-quantizing an already quantized source; the first V2
    // landing scores f32 vectors only, so every quantized configuration is
    // refused with the deferral error rather than scored incorrectly.
    assert_eq!(
        VectorOptions::try_new(3, VectorEmbeddingType::Int8, VectorEmbeddingType::Int8)
            .unwrap_err(),
        VectorOptionsError::QuantizationUnsupported
    );
    assert_eq!(
        VectorOptions::try_new(3, VectorEmbeddingType::Binary, VectorEmbeddingType::Binary)
            .unwrap_err(),
        VectorOptionsError::QuantizationUnsupported
    );
    assert_eq!(
        VectorOptions::try_new(3, VectorEmbeddingType::Int8, VectorEmbeddingType::Single)
            .unwrap_err(),
        VectorOptionsError::QuantizationUnsupported
    );
}

#[test]
fn graph_construction_counts_are_validated_positive_when_set() {
    // V1: "Number of edges has to be positive." / "Number of candidate nodes
    // has to be positive." The counts are declaration vocabulary for the ANN
    // follow-up; this version validates and identity-carries them.
    let with_both = options(3)
        .with_candidates(16)
        .expect("positive candidates")
        .with_edges(8)
        .expect("positive edges");
    assert_eq!(with_both.candidates(), Some(16));
    assert_eq!(with_both.edges(), Some(8));
    assert_eq!(
        options(3).with_candidates(0).unwrap_err(),
        VectorOptionsError::InvalidCandidateCount
    );
    assert_eq!(options(3).with_edges(0).unwrap_err(), VectorOptionsError::InvalidEdgeCount);
}

#[test]
fn declaration_names_follow_the_seam_identifier_rules_and_refuse_the_auto_namespace() {
    // The `Auto/` prefix is the optimizer's reserved namespace (ADR 0010
    // AX-10); a vector declaration refuses it so the later catalog wiring
    // never meets a name collision.
    assert_eq!(
        VectorIndexDeclaration::try_new("", collection("docs"), "embedding", options(3))
            .unwrap_err(),
        VectorIndexError::InvalidName
    );
    assert_eq!(
        VectorIndexDeclaration::try_new(
            "Auto/docs/ByEmbedding",
            collection("docs"),
            "embedding",
            options(3)
        )
        .unwrap_err(),
        VectorIndexError::AutoNamespaceReserved
    );
}

#[test]
fn family_collection_and_invalid_fields_refuse_typed() {
    // Vector indexes live on ORDINARY collections: the reserved family
    // collection is not a vector source, exactly as it is not a query source.
    assert_eq!(
        VectorIndexDeclaration::try_new(
            "families/embedding",
            collection("terrabase.families"),
            "embedding",
            options(3)
        )
        .unwrap_err(),
        VectorIndexError::ReservedCollection
    );
    assert_eq!(
        VectorIndexDeclaration::try_new("docs/embedding", collection("docs"), "", options(3))
            .unwrap_err(),
        VectorIndexError::InvalidField
    );
}

#[test]
fn declaration_identity_key_is_deterministic_and_content_addressed() {
    let left = declaration("docs/embedding", 3);
    let right = declaration("docs/embedding", 3);
    let wider = declaration("docs/embedding", 4);
    assert_eq!(left.identity_key(), right.identity_key());
    assert_ne!(left.identity_key(), wider.identity_key());
    let counted = VectorIndexDeclaration::try_new(
        "docs/embedding",
        collection("docs"),
        "embedding",
        options(3).with_candidates(16).expect("positive"),
    )
    .expect("valid");
    assert_ne!(
        left.identity_key(),
        counted.identity_key(),
        "the construction counts participate in the declaration identity"
    );
}

#[test]
fn declaration_drift_separates_option_evolution_from_identity_change() {
    let original = declaration("docs/embedding", 3);
    // Same identity, changed options: one evolution of the same index.
    let evolved = VectorIndexDeclaration::try_new(
        "docs/embedding",
        collection("docs"),
        "embedding",
        options(4),
    )
    .expect("valid");
    // Same options, different name or field: a different index.
    let renamed =
        VectorIndexDeclaration::try_new("docs/other", collection("docs"), "embedding", options(3))
            .expect("valid");
    assert_eq!(original.drift(&original), VectorIndexDrift::Unchanged);
    assert_eq!(original.drift(&evolved), VectorIndexDrift::OptionsChanged);
    assert_eq!(original.drift(&renamed), VectorIndexDrift::IdentityChanged);
    assert!(VectorIndexDrift::OptionsChanged.requires_rebuild());
    assert!(!VectorIndexDrift::Unchanged.requires_rebuild());
}

// ---------------------------------------------------------------------------
// Request: construction and the typed error taxonomy (PB-4024 anchor)
// ---------------------------------------------------------------------------

#[test]
fn request_construction_captures_the_filtered_knn_shape() {
    let request = VectorSearchRequest::try_new(source("docs"), "embedding", vector(3), 10)
        .expect("valid request");
    assert_eq!(request.dimensions(), 3);
    assert_eq!(request.limit(), 10);
    assert_eq!(request.field(), "embedding");
    assert_eq!(request.minimum_similarity(), None);
    assert_eq!(request.filter(), None);
    assert_eq!(request.mode(), VectorSearchMode::Exact);
    assert_eq!(request.similarity(), VectorSimilarity::Cosine);
}

#[test]
fn limit_bounds_are_one_through_two_hundred_fifty_six() {
    // EV-C8 names the competitor's limit range; the seam pins the same
    // closed range with typed refusals on both sides.
    assert_eq!(MAX_VECTOR_SEARCH_LIMIT, 256);
    assert!(
        VectorSearchRequest::try_new(source("docs"), "embedding", vector(3), 1).is_ok(),
        "k of one is the exact-match lookup"
    );
    assert!(
        VectorSearchRequest::try_new(
            source("docs"),
            "embedding",
            vector(3),
            MAX_VECTOR_SEARCH_LIMIT
        )
        .is_ok()
    );
    assert_eq!(
        VectorSearchRequest::try_new(source("docs"), "embedding", vector(3), 0).unwrap_err(),
        VectorSearchError::InvalidLimit { requested: 0 }
    );
    assert_eq!(
        VectorSearchRequest::try_new(source("docs"), "embedding", vector(3), 257).unwrap_err(),
        VectorSearchError::InvalidLimit { requested: 257 }
    );
}

#[test]
fn empty_and_out_of_bounds_query_vectors_refuse_typed() {
    assert_eq!(
        VectorSearchRequest::try_new(source("docs"), "embedding", Vec::new(), 5).unwrap_err(),
        VectorSearchError::EmptyQueryVector
    );
    assert_eq!(
        VectorSearchRequest::try_new(source("docs"), "embedding", vec![1.0], 5).unwrap_err(),
        VectorSearchError::DimensionsBelowMinimum { dimensions: 1 }
    );
    assert_eq!(
        VectorSearchRequest::try_new(source("docs"), "embedding", vector(4097), 5).unwrap_err(),
        VectorSearchError::DimensionsAboveMaximum { dimensions: 4097 }
    );
}

#[test]
fn non_finite_query_components_refuse_typed() {
    assert_eq!(
        VectorSearchRequest::try_new(source("docs"), "embedding", vec![1.0, f32::NAN], 5)
            .unwrap_err(),
        VectorSearchError::NonFiniteQueryVector
    );
    assert_eq!(
        VectorSearchRequest::try_new(source("docs"), "embedding", vec![f32::INFINITY, 1.0], 5)
            .unwrap_err(),
        VectorSearchError::NonFiniteQueryVector
    );
}

#[test]
fn similarity_threshold_is_validated_over_the_cosine_range() {
    let request = VectorSearchRequest::try_new(source("docs"), "embedding", vector(3), 5)
        .expect("valid")
        .with_minimum_similarity(0.62)
        .expect("threshold inside the cosine range");
    assert_eq!(request.minimum_similarity(), Some(0.62));
    assert_eq!(
        VectorSearchRequest::try_new(source("docs"), "embedding", vector(3), 5)
            .expect("valid")
            .with_minimum_similarity(-1.0)
            .expect("the cosine range is inclusive at -1")
            .minimum_similarity(),
        Some(-1.0)
    );
    assert_eq!(
        VectorSearchRequest::try_new(source("docs"), "embedding", vector(3), 5)
            .expect("valid")
            .with_minimum_similarity(1.0)
            .expect("the cosine range is inclusive at 1")
            .minimum_similarity(),
        Some(1.0)
    );
    assert_eq!(
        VectorSearchRequest::try_new(source("docs"), "embedding", vector(3), 5)
            .expect("valid")
            .with_minimum_similarity(1.01)
            .unwrap_err(),
        VectorSearchError::InvalidSimilarityThreshold { requested: 1.01 }
    );
    assert_eq!(
        VectorSearchRequest::try_new(source("docs"), "embedding", vector(3), 5)
            .expect("valid")
            .with_minimum_similarity(f64::NAN)
            .unwrap_err(),
        VectorSearchError::InvalidSimilarityThreshold { requested: f64::NAN }
    );
}

#[test]
fn filter_composes_the_seam_predicate_for_filter_fields() {
    // CP-204's better-than measure: vector search filterable by ORDINARY
    // predicates of the query seam — not actions-only as Convex restricts
    // it (EV-C8).
    let filter = QueryPredicate::all(vec![QueryCondition::Equals {
        field: "kind".to_owned(),
        value: DocumentValue::String("manual".to_owned()),
    }])
    .expect("valid conjunction");
    let request = VectorSearchRequest::try_new(source("docs"), "embedding", vector(3), 5)
        .expect("valid")
        .with_filter(filter.clone());
    assert_eq!(request.filter(), Some(&filter));
}

#[test]
fn request_bound_against_a_declaration_enforces_the_declared_dimensions() {
    let declaration = declaration("docs/embedding", 4);
    assert!(
        VectorSearchRequest::for_declaration(&declaration, vector(4), 5).is_ok(),
        "a query vector of the declared dimensionality is accepted"
    );
    assert_eq!(
        VectorSearchRequest::for_declaration(&declaration, vector(3), 5).unwrap_err(),
        VectorSearchError::DimensionMismatch { expected: 4, actual: 3 },
        "a query vector of another dimensionality refuses against the declaration"
    );
}

// ---------------------------------------------------------------------------
// TQL predicate handoff (TQL-03 seam)
// ---------------------------------------------------------------------------

#[test]
fn tql_predicate_renders_the_specified_grammar_text() {
    // The tql/1.0 grammar fixes the predicate text (docs/tql/spec.md 8.2);
    // this render is what TQL-03's lowering must produce and consume.
    let predicate = TqlVectorPredicate::try_new("embedding", "queryVector", Some(0.62), Some(25))
        .expect("valid predicate");
    assert_eq!(predicate.render(), "where vector.search(embedding, $queryVector, 0.62)\nlimit 25");
    let bare = TqlVectorPredicate::try_new("embedding", "queryVector", None, None)
        .expect("valid predicate");
    assert_eq!(bare.render(), "where vector.search(embedding, $queryVector)");
}

#[test]
fn tql_predicate_is_versioned_with_the_vector_vocabulary() {
    assert_eq!(VECTOR_PREDICATE_VERSION, "vec/1.0");
    let predicate = TqlVectorPredicate::try_new("embedding", "queryVector", None, None)
        .expect("valid predicate");
    assert_eq!(predicate.version(), VECTOR_PREDICATE_VERSION);
}

#[test]
fn tql_predicate_lowering_binds_the_parameter_value() {
    // The lowering path TQL-03 wires: the predicate plus the bound parameter
    // value produce the engine's typed request.
    let predicate = TqlVectorPredicate::try_new("embedding", "queryVector", Some(0.62), Some(25))
        .expect("valid predicate");
    let bound = DocumentValue::Array(vec![
        DocumentValue::Number(1.0),
        DocumentValue::Number(0.0),
        DocumentValue::Number(1.0),
    ]);
    let request = predicate.lower(source("docs"), &bound).expect("lowers");
    assert_eq!(request.dimensions(), 3);
    assert_eq!(request.limit(), 25);
    assert_eq!(request.minimum_similarity(), Some(0.62));
    assert_eq!(
        request.vector(),
        &[1.0, 0.0, 1.0],
        "integer and number encodings lower to the same f32 embedding"
    );
    // Fail-closed classes of the lowering: a non-vector parameter and a
    // non-finite component never become a request.
    assert_eq!(
        predicate.lower(source("docs"), &DocumentValue::String("1.0,0.0".to_owned())).unwrap_err(),
        VectorSearchError::InvalidQueryVector
    );
    assert_eq!(
        predicate
            .lower(
                source("docs"),
                &DocumentValue::Array(vec![
                    DocumentValue::Number(1.0),
                    DocumentValue::Number(f64::NAN)
                ])
            )
            .unwrap_err(),
        VectorSearchError::NonFiniteQueryVector
    );
}

#[test]
fn tql_predicate_validates_limit_and_threshold_like_the_request() {
    assert_eq!(
        TqlVectorPredicate::try_new("embedding", "queryVector", None, Some(0)).unwrap_err(),
        VectorSearchError::InvalidLimit { requested: 0 }
    );
    assert_eq!(
        TqlVectorPredicate::try_new("embedding", "queryVector", Some(2.5), None).unwrap_err(),
        VectorSearchError::InvalidSimilarityThreshold { requested: 2.5 }
    );
    assert_eq!(
        TqlVectorPredicate::try_new("", "queryVector", None, None).unwrap_err(),
        VectorSearchError::InvalidField
    );
}

#[test]
fn deadline_and_cancellation_can_be_bound_to_a_request() {
    let deadline = Deadline::after(Duration::from_secs(30)).expect("valid deadline");
    let cancellation = Cancellation::new();
    let request = VectorSearchRequest::try_new(source("docs"), "embedding", vector(3), 5)
        .expect("valid")
        .with_deadline(deadline)
        .with_cancellation(cancellation.clone());
    assert_eq!(request.deadline(), deadline);
    assert!(!request.cancellation().is_cancelled());
}
