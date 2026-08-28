//! The vector conformance vector corpus (GAP-C4, TerraBaseV2#86).
//!
//! Following the query golden-vector pattern of
//! `tests/query/tql_golden_vectors.rs`: every accept and refusal class of
//! the vector vocabulary's construction surface has at least one named
//! vector, pinned by the exact-count assertions below, and the
//! coverage-completeness checks at the end of this file fail when a
//! refusal variant of the typed error taxonomy is produced by no vector,
//! so a coverage gap of that class cannot recur silently.
//!
//! The corpus runs in CI with the rest of the integration tests. It pins
//! the CONSTRUCTION surface (declaration options, k-NN requests, TQL
//! predicate forms); the execution semantics — cosine scoring, filtered
//! k-NN, deterministic tie ordering — are pinned by the in-crate corpus of
//! `src/query/vector_tests.rs`, because execution requires a `TenantScope`
//! and the public surface has no scope constructor by design.

use terrabase_v2::{
    QuerySource,
    query::{
        MAX_VECTOR_DIMENSIONS, MIN_VECTOR_DIMENSIONS, TqlVectorPredicate, VectorEmbeddingType,
        VectorIndexDeclaration, VectorIndexError, VectorOptions, VectorOptionsError,
        VectorSearchError, VectorSearchRequest,
    },
    types::{CollectionName, DocumentValue},
};

/// One conformance vector: a named construction scenario plus the outcome
/// the seam must produce.
struct Vector {
    name: &'static str,
    scenario: Scenario,
    expected: Expected,
}

enum Scenario {
    /// `VectorOptions::try_new` over one embedding-type pair.
    Options { dimensions: u16, source: VectorEmbeddingType, destination: VectorEmbeddingType },
    /// `VectorOptions` construction counts.
    Counts { candidates: u32, edges: u32 },
    /// `VectorIndexDeclaration::try_new` over one name/collection/field.
    Declaration {
        name: &'static str,
        collection: &'static str,
        field: &'static str,
        dimensions: u16,
    },
    /// `VectorSearchRequest::try_new` over one synthesized query vector.
    Request { dimensions: usize, limit: u32 },
    /// `VectorSearchRequest::try_new` over one field name (the field rule).
    RequestField { field: &'static str },
    /// `with_minimum_similarity` over one threshold.
    Threshold { value: f64 },
    /// `TqlVectorPredicate::try_new` over one name pair (the identifier rule).
    PredicateName { field: &'static str, parameter: &'static str },
    /// `TqlVectorPredicate` rendering over one predicate shape.
    Render {
        field: &'static str,
        parameter: &'static str,
        threshold: Option<f64>,
        limit: Option<u32>,
    },
    /// `TqlVectorPredicate::lower` over one bound parameter value.
    Lower { parameter: DocumentValue },
}

/// The expected outcome of one conformance vector.
#[derive(Debug)]
enum Expected {
    Accepted,
    RefusedOptions(VectorOptionsError),
    RefusedDeclaration(VectorIndexError),
    RefusedRequest(VectorSearchError),
    /// The rendered TQL predicate text, pinned to the grammar's shape.
    Rendered(&'static str),
}

fn collection(name: &str) -> CollectionName {
    CollectionName::try_new(name).expect("valid collection name")
}

fn source() -> QuerySource {
    QuerySource::try_new(collection("docs")).expect("user collection")
}

fn options(dimensions: u16) -> VectorOptions {
    VectorOptions::try_new(dimensions, VectorEmbeddingType::Single, VectorEmbeddingType::Single)
        .expect("served configuration")
}

fn synthesized(dimensions: usize) -> Vec<f32> {
    (0..dimensions).map(|index| (index % 5) as f32 + 1.0).collect()
}

fn vectors() -> Vec<Vector> {
    let dims_min = u16::try_from(MIN_VECTOR_DIMENSIONS).expect("in u16");
    let dims_max = u16::try_from(MAX_VECTOR_DIMENSIONS).expect("in u16");
    vec![
        // ---------------------------------------------------------------
        // Options: dimensions and embedding types (PB-4051)
        // ---------------------------------------------------------------
        Vector {
            name: "options/dimensions-minimum",
            scenario: Scenario::Options {
                dimensions: dims_min,
                source: VectorEmbeddingType::Single,
                destination: VectorEmbeddingType::Single,
            },
            expected: Expected::Accepted,
        },
        Vector {
            name: "options/dimensions-maximum",
            scenario: Scenario::Options {
                dimensions: dims_max,
                source: VectorEmbeddingType::Single,
                destination: VectorEmbeddingType::Single,
            },
            expected: Expected::Accepted,
        },
        Vector {
            name: "options/dimensions-below-minimum",
            scenario: Scenario::Options {
                dimensions: 1,
                source: VectorEmbeddingType::Single,
                destination: VectorEmbeddingType::Single,
            },
            expected: Expected::RefusedOptions(VectorOptionsError::DimensionsBelowMinimum),
        },
        Vector {
            name: "options/dimensions-above-maximum",
            scenario: Scenario::Options {
                dimensions: 4097,
                source: VectorEmbeddingType::Single,
                destination: VectorEmbeddingType::Single,
            },
            expected: Expected::RefusedOptions(VectorOptionsError::DimensionsAboveMaximum),
        },
        Vector {
            name: "options/text-destination",
            scenario: Scenario::Options {
                dimensions: 4,
                source: VectorEmbeddingType::Single,
                destination: VectorEmbeddingType::Text,
            },
            expected: Expected::RefusedOptions(VectorOptionsError::TextDestination),
        },
        Vector {
            name: "options/text-source-auto-embedding-deferred",
            scenario: Scenario::Options {
                dimensions: 4,
                source: VectorEmbeddingType::Text,
                destination: VectorEmbeddingType::Single,
            },
            expected: Expected::RefusedOptions(VectorOptionsError::AutoEmbeddingUnsupported),
        },
        Vector {
            name: "options/int8-quantization-deferred",
            scenario: Scenario::Options {
                dimensions: 4,
                source: VectorEmbeddingType::Int8,
                destination: VectorEmbeddingType::Int8,
            },
            expected: Expected::RefusedOptions(VectorOptionsError::QuantizationUnsupported),
        },
        Vector {
            name: "options/binary-quantization-deferred",
            scenario: Scenario::Options {
                dimensions: 4,
                source: VectorEmbeddingType::Binary,
                destination: VectorEmbeddingType::Binary,
            },
            expected: Expected::RefusedOptions(VectorOptionsError::QuantizationUnsupported),
        },
        Vector {
            name: "options/counts-valid",
            scenario: Scenario::Counts { candidates: 16, edges: 8 },
            expected: Expected::Accepted,
        },
        Vector {
            name: "options/candidate-count-zero",
            scenario: Scenario::Counts { candidates: 0, edges: 8 },
            expected: Expected::RefusedOptions(VectorOptionsError::InvalidCandidateCount),
        },
        Vector {
            name: "options/edge-count-zero",
            scenario: Scenario::Counts { candidates: 16, edges: 0 },
            expected: Expected::RefusedOptions(VectorOptionsError::InvalidEdgeCount),
        },
        // ---------------------------------------------------------------
        // Declaration: names, collections, fields
        // ---------------------------------------------------------------
        Vector {
            name: "declaration/ordinary-collection",
            scenario: Scenario::Declaration {
                name: "docs/embedding",
                collection: "docs",
                field: "embedding",
                dimensions: 4,
            },
            expected: Expected::Accepted,
        },
        Vector {
            name: "declaration/name-empty",
            scenario: Scenario::Declaration {
                name: "",
                collection: "docs",
                field: "embedding",
                dimensions: 4,
            },
            expected: Expected::RefusedDeclaration(VectorIndexError::InvalidName),
        },
        Vector {
            name: "declaration/auto-namespace-reserved",
            scenario: Scenario::Declaration {
                name: "Auto/docs/ByEmbedding",
                collection: "docs",
                field: "embedding",
                dimensions: 4,
            },
            expected: Expected::RefusedDeclaration(VectorIndexError::AutoNamespaceReserved),
        },
        Vector {
            name: "declaration/family-collection-reserved",
            scenario: Scenario::Declaration {
                name: "families/embedding",
                collection: "terrabase.families",
                field: "embedding",
                dimensions: 4,
            },
            expected: Expected::RefusedDeclaration(VectorIndexError::ReservedCollection),
        },
        Vector {
            name: "declaration/field-empty",
            scenario: Scenario::Declaration {
                name: "docs/embedding",
                collection: "docs",
                field: "",
                dimensions: 4,
            },
            expected: Expected::RefusedDeclaration(VectorIndexError::InvalidField),
        },
        // ---------------------------------------------------------------
        // Request: the k-NN bounds and query-vector shape
        // ---------------------------------------------------------------
        Vector {
            name: "request/limit-one",
            scenario: Scenario::Request { dimensions: 4, limit: 1 },
            expected: Expected::Accepted,
        },
        Vector {
            name: "request/limit-maximum",
            scenario: Scenario::Request { dimensions: 4, limit: 256 },
            expected: Expected::Accepted,
        },
        Vector {
            name: "request/limit-zero",
            scenario: Scenario::Request { dimensions: 4, limit: 0 },
            expected: Expected::RefusedRequest(VectorSearchError::InvalidLimit { requested: 0 }),
        },
        Vector {
            name: "request/limit-above-maximum",
            scenario: Scenario::Request { dimensions: 4, limit: 257 },
            expected: Expected::RefusedRequest(VectorSearchError::InvalidLimit { requested: 257 }),
        },
        Vector {
            name: "request/vector-empty",
            scenario: Scenario::Request { dimensions: 0, limit: 4 },
            expected: Expected::RefusedRequest(VectorSearchError::EmptyQueryVector),
        },
        Vector {
            name: "request/vector-below-minimum",
            scenario: Scenario::Request { dimensions: 1, limit: 4 },
            expected: Expected::RefusedRequest(VectorSearchError::DimensionsBelowMinimum {
                dimensions: 1,
            }),
        },
        Vector {
            name: "request/vector-above-maximum",
            scenario: Scenario::Request { dimensions: 4097, limit: 4 },
            expected: Expected::RefusedRequest(VectorSearchError::DimensionsAboveMaximum {
                dimensions: 4097,
            }),
        },
        Vector {
            name: "request/vector-non-finite",
            scenario: Scenario::Request { dimensions: usize::MAX, limit: 4 },
            expected: Expected::RefusedRequest(VectorSearchError::NonFiniteQueryVector),
        },
        Vector {
            name: "request/field-empty",
            scenario: Scenario::RequestField { field: "" },
            expected: Expected::RefusedRequest(VectorSearchError::InvalidField),
        },
        Vector {
            name: "request/field-whitespace-padding",
            scenario: Scenario::RequestField { field: " embedding" },
            expected: Expected::RefusedRequest(VectorSearchError::InvalidField),
        },
        Vector {
            name: "request/threshold-inside-range",
            scenario: Scenario::Threshold { value: 0.62 },
            expected: Expected::Accepted,
        },
        Vector {
            name: "request/threshold-above-range",
            scenario: Scenario::Threshold { value: 1.5 },
            expected: Expected::RefusedRequest(VectorSearchError::InvalidSimilarityThreshold {
                requested: 1.5,
            }),
        },
        // ---------------------------------------------------------------
        // TQL predicate: render and lower (the TQL-03 seam handoff)
        // ---------------------------------------------------------------
        Vector {
            name: "tql/render-full-form",
            scenario: Scenario::Render {
                field: "embedding",
                parameter: "queryVector",
                threshold: Some(0.62),
                limit: Some(25),
            },
            expected: Expected::Rendered(
                "where vector.search(embedding, $queryVector, 0.62)\nlimit 25",
            ),
        },
        Vector {
            name: "tql/render-bare-form",
            scenario: Scenario::Render {
                field: "embedding",
                parameter: "queryVector",
                threshold: None,
                limit: None,
            },
            expected: Expected::Rendered("where vector.search(embedding, $queryVector)"),
        },
        Vector {
            name: "tql/render-threshold-only",
            scenario: Scenario::Render {
                field: "embedding",
                parameter: "queryVector",
                threshold: Some(-0.5),
                limit: None,
            },
            expected: Expected::Rendered("where vector.search(embedding, $queryVector, -0.5)"),
        },
        Vector {
            name: "tql/name-field-metacharacters",
            scenario: Scenario::PredicateName { field: "a, 0.9) or true(", parameter: "p" },
            expected: Expected::RefusedRequest(VectorSearchError::InvalidField),
        },
        Vector {
            name: "tql/name-parameter-metacharacters",
            scenario: Scenario::PredicateName {
                field: "embedding",
                parameter: "p, 0.5) limit 999",
            },
            expected: Expected::RefusedRequest(VectorSearchError::InvalidField),
        },
        Vector {
            name: "tql/lower-bound-array",
            scenario: Scenario::Lower {
                parameter: DocumentValue::Array(vec![
                    DocumentValue::Integer(1),
                    DocumentValue::Number(0.0),
                ]),
            },
            expected: Expected::Accepted,
        },
        Vector {
            name: "tql/lower-non-vector-parameter",
            scenario: Scenario::Lower { parameter: DocumentValue::String("0.1,0.2".to_owned()) },
            expected: Expected::RefusedRequest(VectorSearchError::InvalidQueryVector),
        },
        Vector {
            name: "tql/lower-below-minimum",
            scenario: Scenario::Lower {
                parameter: DocumentValue::Array(vec![DocumentValue::Number(0.5)]),
            },
            expected: Expected::RefusedRequest(VectorSearchError::DimensionsBelowMinimum {
                dimensions: 1,
            }),
        },
    ]
}

/// Runs one vector against the public construction surface.
fn run(vector: &Vector) {
    let outcome: Result<(), Outcomes> = match &vector.scenario {
        Scenario::Options { dimensions, source, destination } => {
            VectorOptions::try_new(*dimensions, *source, *destination)
                .map(|_| ())
                .map_err(Outcomes::Options)
        }
        Scenario::Counts { candidates, edges } => options(4)
            .with_candidates(*candidates)
            .and_then(|configured| configured.with_edges(*edges))
            .map(|_| ())
            .map_err(Outcomes::Options),
        Scenario::Declaration { name, collection: collection_name, field, dimensions } => {
            VectorIndexDeclaration::try_new(
                name,
                collection(collection_name),
                field,
                options(*dimensions),
            )
            .map(|_| ())
            .map_err(Outcomes::Declaration)
        }
        Scenario::Request { dimensions, limit } => match *dimensions {
            usize::MAX => {
                VectorSearchRequest::try_new(source(), "embedding", vec![1.0, f32::NAN], *limit)
                    .map(|_| ())
                    .map_err(Outcomes::Request)
            }
            size => VectorSearchRequest::try_new(source(), "embedding", synthesized(size), *limit)
                .map(|_| ())
                .map_err(Outcomes::Request),
        },
        Scenario::Threshold { value } => {
            VectorSearchRequest::try_new(source(), "embedding", synthesized(4), 4)
                .and_then(|request| request.with_minimum_similarity(*value).map(|_| ()))
                .map_err(Outcomes::Request)
        }
        Scenario::RequestField { field } => VectorSearchRequest::try_new(
            source(),
            field,
            synthesized(4),
            4,
        )
        .map(|_| ())
        .map_err(Outcomes::Request),
        Scenario::PredicateName { field, parameter } => {
            TqlVectorPredicate::try_new(field, parameter, None, None)
                .map(|_| ())
                .map_err(Outcomes::Request)
        }
        Scenario::Render { field, parameter, threshold, limit } => {
            let predicate = TqlVectorPredicate::try_new(field, parameter, *threshold, *limit)
                .expect("the render vectors are well-formed predicates");
            assert_eq!(
                predicate.render(),
                vector.expected.expected_text(),
                "vector {}",
                vector.name
            );
            return;
        }
        Scenario::Lower { parameter } => {
            TqlVectorPredicate::try_new("embedding", "queryVector", None, Some(4))
                .expect("the lower vectors are well-formed predicates")
                .lower(source(), parameter)
                .map(|_| ())
                .map_err(Outcomes::Request)
        }
    };
    match (&vector.expected, outcome) {
        (Expected::Accepted, Ok(())) => {}
        (Expected::RefusedOptions(expected), Err(Outcomes::Options(actual))) => {
            assert_eq!(&actual, expected, "vector {}", vector.name);
        }
        (Expected::RefusedDeclaration(expected), Err(Outcomes::Declaration(actual))) => {
            assert_eq!(&actual, expected, "vector {}", vector.name);
        }
        (Expected::RefusedRequest(expected), Err(Outcomes::Request(actual))) => {
            assert_eq!(&actual, expected, "vector {}", vector.name);
        }
        (expected, outcome) => {
            panic!("vector {} produced {outcome:?} for {expected:?}", vector.name);
        }
    }
}

/// The refusal outcomes one construction path can produce.
#[derive(Debug)]
enum Outcomes {
    Options(VectorOptionsError),
    Declaration(VectorIndexError),
    Request(VectorSearchError),
}

impl Expected {
    /// Returns the rendered text this vector expects, or panics for
    /// non-render vectors.
    fn expected_text(&self) -> &str {
        match self {
            Self::Rendered(text) => text,
            _ => panic!("not a render vector"),
        }
    }
}

#[test]
fn vector_conformance_corpus_is_complete_and_exact() {
    let corpus = vectors();
    // Exact counts per family: adding or removing a vector is a deliberate
    // contract change this assertion makes audible.
    assert_eq!(corpus.iter().filter(|v| matches!(v.scenario, Scenario::Options { .. })).count(), 8);
    assert_eq!(corpus.iter().filter(|v| matches!(v.scenario, Scenario::Counts { .. })).count(), 3);
    assert_eq!(
        corpus.iter().filter(|v| matches!(v.scenario, Scenario::Declaration { .. })).count(),
        5
    );
    assert_eq!(corpus.iter().filter(|v| matches!(v.scenario, Scenario::Request { .. })).count(), 8);
    assert_eq!(
        corpus.iter().filter(|v| matches!(v.scenario, Scenario::RequestField { .. })).count(),
        2
    );
    assert_eq!(
        corpus.iter().filter(|v| matches!(v.scenario, Scenario::Threshold { .. })).count(),
        2
    );
    assert_eq!(
        corpus.iter().filter(|v| matches!(v.scenario, Scenario::PredicateName { .. })).count(),
        2
    );
    assert_eq!(corpus.iter().filter(|v| matches!(v.scenario, Scenario::Render { .. })).count(), 3);
    assert_eq!(corpus.iter().filter(|v| matches!(v.scenario, Scenario::Lower { .. })).count(), 3);
    assert_eq!(corpus.len(), 36, "the corpus carries exactly thirty-six named vectors");
    for vector in &corpus {
        run(vector);
    }
}

#[test]
fn every_options_refusal_variant_is_reached_by_the_corpus() {
    let produced: Vec<VectorOptionsError> = vectors()
        .into_iter()
        .filter_map(|vector| match (vector.scenario, vector.expected) {
            (
                Scenario::Options { .. } | Scenario::Counts { .. },
                Expected::RefusedOptions(error),
            ) => Some(error),
            _ => None,
        })
        .collect();
    for variant in [
        VectorOptionsError::DimensionsBelowMinimum,
        VectorOptionsError::DimensionsAboveMaximum,
        VectorOptionsError::TextDestination,
        VectorOptionsError::AutoEmbeddingUnsupported,
        VectorOptionsError::QuantizationUnsupported,
        VectorOptionsError::InvalidCandidateCount,
        VectorOptionsError::InvalidEdgeCount,
    ] {
        assert!(
            produced.contains(&variant),
            "no conformance vector produces {variant:?}: a refusal class cannot silently lose coverage"
        );
    }
}

#[test]
fn every_construction_refusal_variant_of_the_request_is_reached() {
    let produced: Vec<VectorSearchError> = vectors()
        .into_iter()
        .filter_map(|vector| match (vector.scenario, vector.expected) {
            (
                Scenario::Request { .. }
                | Scenario::RequestField { .. }
                | Scenario::Threshold { .. }
                | Scenario::PredicateName { .. }
                | Scenario::Lower { .. },
                Expected::RefusedRequest(error),
            ) => Some(error),
            _ => None,
        })
        .collect();
    for variant in [
        VectorSearchError::InvalidLimit { requested: 0 },
        VectorSearchError::EmptyQueryVector,
        VectorSearchError::DimensionsBelowMinimum { dimensions: 1 },
        VectorSearchError::DimensionsAboveMaximum { dimensions: 4097 },
        VectorSearchError::NonFiniteQueryVector,
        VectorSearchError::InvalidSimilarityThreshold { requested: 1.5 },
        VectorSearchError::InvalidQueryVector,
        VectorSearchError::InvalidField,
    ] {
        assert!(
            produced.contains(&variant),
            "no conformance vector produces {variant:?}: a refusal class cannot silently lose coverage"
        );
    }
}
