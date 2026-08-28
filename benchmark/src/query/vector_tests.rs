//! In-crate vector search tests: the execution semantics (GAP-C4).
//!
//! These tests use `TenantScope::from_parts`, which mirrors Platform's
//! production role of creating a validated scope, because `TenantScope` has
//! no public constructor by design. They pin the exact cosine k-NN contract
//! the public surface promises: the similarity values, the filtered
//! candidate set, the deterministic score-tie ordering by document identity,
//! the schema-optional treatment of documents without the vector field, the
//! typed fail-closed classes for malformed stored vectors, and the result
//! metadata (ids, scores, accounting). The construction surface is pinned at
//! the public seam by `tests/query/vector_search.rs` and
//! `tests/query/vector_conformance_vectors.rs`.

use std::{
    collections::BTreeMap,
    path::PathBuf,
    sync::atomic::{AtomicU64, Ordering},
};

use super::{
    QueryCondition, QueryPredicate, QuerySource, VectorSearchError, VectorSearchMode,
    VectorSearchRequest, VectorSimilarity, vector::VectorMatch,
};
use crate::{
    engine::{CommitOutcome, Engine, EngineConfig},
    types::{
        Cancellation, CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion,
        LocalDurability, OperationId, TenantScope,
    },
};

fn location(tag: &str) -> PathBuf {
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    let index = COUNTER.fetch_add(1, Ordering::Relaxed);
    std::env::temp_dir().join(format!("tbv2-gapc4-vector-{tag}-{}-{index}", std::process::id()))
}

fn open_engine(tag: &str) -> (PathBuf, Engine) {
    let dir = location(tag);
    std::fs::remove_dir_all(&dir).ok();
    let engine =
        Engine::open(EngineConfig::durable(&dir).expect("durable location")).expect("engine opens");
    (dir, engine)
}

fn scope(engine: &Engine) -> TenantScope {
    TenantScope::from_parts(
        crate::types::TenantId::try_new("tenant-a").expect("valid tenant"),
        engine.identity(),
    )
}

fn number(value: f64) -> DocumentValue {
    DocumentValue::Number(value)
}

fn vector_value(components: &[f64]) -> DocumentValue {
    DocumentValue::Array(components.iter().map(|component| number(*component)).collect())
}

fn object(fields: &[(&str, DocumentValue)]) -> DocumentValue {
    DocumentValue::Object(
        fields
            .iter()
            .map(|(name, value)| ((*name).to_owned(), value.clone()))
            .collect::<BTreeMap<_, _>>(),
    )
}

fn vector_document(id: &str, embedding: &[f64], extra: &[(&str, DocumentValue)]) -> Document {
    let mut fields: Vec<(&str, DocumentValue)> = vec![("embedding", vector_value(embedding))];
    fields.extend_from_slice(extra);
    Document::new(
        DocumentId::try_new(id).expect("valid identifier"),
        CollectionName::try_new("docs").expect("valid identifier"),
        object(&fields),
    )
}

fn commit(engine: &Engine, tenant_scope: &TenantScope, tag: &str, documents: Vec<Document>) {
    let snapshot = engine.read_snapshot(tenant_scope).expect("snapshot");
    let operation = OperationId::try_new(tag).expect("valid identifier");
    let mut transaction = engine.begin(tenant_scope, snapshot, operation).expect("begins");
    for document in documents {
        transaction.put(document, ExpectedVersion::Any).expect("stages");
    }
    match transaction.commit(LocalDurability::Synced) {
        CommitOutcome::Committed(_) => {}
        other => panic!("commit did not succeed: {other:?}"),
    }
}

fn source() -> QuerySource {
    QuerySource::try_new(CollectionName::try_new("docs").expect("valid identifier"))
        .expect("user collection")
}

#[allow(clippy::cast_possible_truncation, reason = "test embeddings are f32-exact by design")]
fn request(vector: &[f64], limit: u32) -> VectorSearchRequest {
    VectorSearchRequest::try_new(
        source(),
        "embedding",
        vector.iter().map(|component| *component as f32).collect(),
        limit,
    )
    .expect("valid request")
}

fn search(
    engine: &Engine,
    request: &VectorSearchRequest,
) -> Result<super::vector::VectorSearchResult, VectorSearchError> {
    let tenant_scope = scope(engine);
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    engine.queries().vector_search(request, &snapshot)
}

fn ids(matches: &[VectorMatch]) -> Vec<&str> {
    matches.iter().map(|matched| matched.id().as_str()).collect()
}

fn scores(matches: &[VectorMatch]) -> Vec<f64> {
    matches.iter().map(VectorMatch::score).collect()
}

/// Asserts two finite scores are equal within a deterministic window.
fn assert_close(left: f64, right: f64) {
    assert!((left - right).abs() < 1e-12, "score {left} is not the pinned value {right}");
}

#[test]
fn cosine_similarity_matches_the_pinned_values() {
    let (_dir, engine) = open_engine("cosine");
    commit(
        &engine,
        &scope(&engine),
        "cosine",
        vec![
            vector_document("same", &[1.0, 0.0], &[]),
            vector_document("orthogonal", &[0.0, 1.0], &[]),
            vector_document("opposite", &[-1.0, 0.0], &[]),
            vector_document("obtuse", &[-1.0, 1.0], &[]),
        ],
    );
    let result = search(&engine, &request(&[1.0, 0.0], 4)).expect("scores");
    let by_id: BTreeMap<&str, f64> =
        result.matches().iter().map(|matched| (matched.id().as_str(), matched.score())).collect();
    assert_close(by_id["same"], 1.0);
    assert_close(by_id["orthogonal"], 0.0);
    assert_close(by_id["opposite"], -1.0);
    assert_close(by_id["obtuse"], -std::f64::consts::FRAC_1_SQRT_2);
    // V1 scores cosine similarity (distance is 1 - similarity) and reports
    // similarity as the score: `Hnsw.SimilarityMethods` and
    // `DistanceToScoreCosine`.
    assert_eq!(result.similarity(), VectorSimilarity::Cosine);
    assert_eq!(result.mode(), VectorSearchMode::Exact);
    assert!(result.exact());
}

#[test]
fn a_zero_magnitude_stored_vector_scores_zero_by_rule() {
    let (_dir, engine) = open_engine("zero-vector");
    commit(&engine, &scope(&engine), "zero", vec![vector_document("zero", &[0.0, 0.0], &[])]);
    let result = search(&engine, &request(&[1.0, 0.0], 1)).expect("the zero rule is defined");
    assert_eq!(ids(result.matches()), ["zero"]);
    assert_close(result.matches()[0].score(), 0.0);
}

#[test]
fn results_order_by_score_descending() {
    let (_dir, engine) = open_engine("ordering");
    commit(
        &engine,
        &scope(&engine),
        "order",
        vec![
            vector_document("middle", &[1.0, 1.0], &[]),
            vector_document("nearest", &[1.0, 0.0], &[]),
            vector_document("furthest", &[-1.0, 0.0], &[]),
        ],
    );
    let result = search(&engine, &request(&[1.0, 0.0], 3)).expect("orders");
    assert_eq!(ids(result.matches()), ["nearest", "middle", "furthest"]);
    let mut descending = scores(result.matches());
    descending.sort_by(|left, right| right.partial_cmp(left).expect("finite scores"));
    assert_eq!(scores(result.matches()), descending);
}

#[test]
fn score_ties_break_by_document_identity_deterministically() {
    // THE determinism pin of the contract: two documents with identical
    // vectors produce identical scores, and the tie resolves by document
    // identity in one total order — never by scan order or chance. The ids
    // are committed in the reverse of the expected order on purpose.
    let (_dir, engine) = open_engine("ties");
    commit(
        &engine,
        &scope(&engine),
        "ties",
        vec![
            vector_document("doc-z", &[1.0, 0.0], &[]),
            vector_document("doc-m", &[1.0, 0.0], &[]),
            vector_document("doc-a", &[1.0, 0.0], &[]),
            vector_document("doc-b", &[1.0, 0.0], &[]),
        ],
    );
    let result = search(&engine, &request(&[1.0, 0.0], 4)).expect("orders");
    assert_eq!(ids(result.matches()), ["doc-a", "doc-b", "doc-m", "doc-z"]);
    // Re-execution against the same snapshot returns the identical order.
    let again = search(&engine, &request(&[1.0, 0.0], 4)).expect("orders");
    assert_eq!(ids(again.matches()), ["doc-a", "doc-b", "doc-m", "doc-z"]);
}

#[test]
fn limit_truncates_to_the_k_nearest() {
    let (_dir, engine) = open_engine("limit");
    commit(
        &engine,
        &scope(&engine),
        "limit",
        vec![
            vector_document("near", &[1.0, 0.0], &[]),
            vector_document("far", &[0.0, 1.0], &[]),
            vector_document("between", &[1.0, 1.0], &[]),
        ],
    );
    let result = search(&engine, &request(&[1.0, 0.0], 2)).expect("truncates");
    assert_eq!(ids(result.matches()), ["near", "between"]);
    assert_eq!(result.statistics().returned(), 2);
    assert_eq!(result.statistics().scored(), 3);
}

#[test]
fn the_filter_predicates_candidates_before_scoring() {
    let (_dir, engine) = open_engine("filter");
    commit(
        &engine,
        &scope(&engine),
        "filter",
        vec![
            vector_document(
                "manual-a",
                &[1.0, 0.0],
                &[("kind", DocumentValue::String("manual".to_owned()))],
            ),
            vector_document(
                "guide",
                &[1.0, 0.0],
                &[("kind", DocumentValue::String("guide".to_owned()))],
            ),
            vector_document(
                "manual-b",
                &[0.0, 1.0],
                &[("kind", DocumentValue::String("manual".to_owned()))],
            ),
        ],
    );
    let filter = QueryPredicate::all(vec![QueryCondition::Equals {
        field: "kind".to_owned(),
        value: DocumentValue::String("manual".to_owned()),
    }])
    .expect("valid conjunction");
    let filtered = request(&[1.0, 0.0], 5).with_filter(filter);
    let result = search(&engine, &filtered).expect("filters");
    assert_eq!(ids(result.matches()), ["manual-a", "manual-b"]);
    assert_eq!(result.statistics().scanned(), 3);
    assert_eq!(result.statistics().filter_skipped(), 1);
    assert_eq!(result.statistics().scored(), 2);
}

#[test]
fn documents_without_the_vector_field_are_skipped_not_refused() {
    // The schema-optional rule (PD-2026-08-26-04 D1): a collection carries
    // vector documents and plain documents together, and a plain document
    // is a non-candidate, never an error.
    let (_dir, engine) = open_engine("schema-optional");
    let plain = Document::new(
        DocumentId::try_new("plain").expect("valid identifier"),
        CollectionName::try_new("docs").expect("valid identifier"),
        object(&[("title", DocumentValue::String("no embedding here".to_owned()))]),
    );
    commit(
        &engine,
        &scope(&engine),
        "mixed",
        vec![plain, vector_document("embedded", &[1.0, 0.0], &[])],
    );
    let result = search(&engine, &request(&[1.0, 0.0], 5)).expect("mixed collections are legal");
    assert_eq!(ids(result.matches()), ["embedded"]);
    assert_eq!(result.statistics().missing_field(), 1);
}

#[test]
fn other_collections_are_never_candidates() {
    let (_dir, engine) = open_engine("isolation");
    let other = Document::new(
        DocumentId::try_new("other").expect("valid identifier"),
        CollectionName::try_new("otherdocs").expect("valid identifier"),
        object(&[("embedding", vector_value(&[1.0, 0.0]))]),
    );
    commit(
        &engine,
        &scope(&engine),
        "isolation",
        vec![other, vector_document("docs-doc", &[1.0, 0.0], &[])],
    );
    let result = search(&engine, &request(&[1.0, 0.0], 5)).expect("scopes to the source");
    assert_eq!(ids(result.matches()), ["docs-doc"]);
    assert_eq!(result.statistics().scanned(), 1);
}

#[test]
fn a_stored_vector_of_another_dimensionality_fails_closed() {
    let (_dir, engine) = open_engine("dimension-mismatch");
    commit(
        &engine,
        &scope(&engine),
        "mismatch",
        vec![vector_document("three-d", &[1.0, 0.0, 1.0], &[])],
    );
    assert_eq!(
        search(&engine, &request(&[1.0, 0.0], 5)).unwrap_err(),
        VectorSearchError::DimensionMismatch { expected: 2, actual: 3 }
    );
}

#[test]
fn malformed_stored_vectors_fail_closed_typed() {
    let (_dir, engine) = open_engine("malformed");
    let string_field = Document::new(
        DocumentId::try_new("string").expect("valid identifier"),
        CollectionName::try_new("docs").expect("valid identifier"),
        object(&[("embedding", DocumentValue::String("1.0,0.0".to_owned()))]),
    );
    let non_numeric = Document::new(
        DocumentId::try_new("words").expect("valid identifier"),
        CollectionName::try_new("docs").expect("valid identifier"),
        object(&[("embedding", DocumentValue::Array(vec![DocumentValue::String("x".to_owned())]))]),
    );
    let non_finite = Document::new(
        DocumentId::try_new("infinite").expect("valid identifier"),
        CollectionName::try_new("docs").expect("valid identifier"),
        object(&[("embedding", vector_value(&[f64::INFINITY, 0.0]))]),
    );
    commit(&engine, &scope(&engine), "malformed", vec![string_field, non_numeric, non_finite]);
    // The three malformed shapes — a non-array field, a non-numeric
    // component, and a non-finite component — refuse with the one typed
    // error class; which document offended is not part of the refusal.
    assert_eq!(
        search(&engine, &request(&[1.0, 0.0], 5)).unwrap_err(),
        VectorSearchError::InvalidStoredVector
    );
}

#[test]
fn the_similarity_threshold_drops_below_threshold_matches() {
    let (_dir, engine) = open_engine("threshold");
    commit(
        &engine,
        &scope(&engine),
        "threshold",
        vec![vector_document("high", &[1.0, 0.0], &[]), vector_document("low", &[0.0, 1.0], &[])],
    );
    let thresholded =
        request(&[1.0, 0.0], 5).with_minimum_similarity(0.5).expect("threshold in range");
    let result = search(&engine, &thresholded).expect("thresholds");
    assert_eq!(ids(result.matches()), ["high"]);
    assert_eq!(result.statistics().threshold_dropped(), 1);
}

#[test]
fn a_cancelled_request_refuses_typed() {
    let (_dir, engine) = open_engine("cancelled");
    commit(&engine, &scope(&engine), "cancelled", vec![vector_document("a", &[1.0, 0.0], &[])]);
    let cancellation = Cancellation::new();
    cancellation.cancel();
    let cancelled = request(&[1.0, 0.0], 5).with_cancellation(cancellation);
    assert_eq!(search(&engine, &cancelled).unwrap_err(), VectorSearchError::Cancelled);
}

#[test]
fn a_snapshot_of_another_engine_refuses_typed() {
    let (_dir_one, engine_one) = open_engine("engine-one");
    let (_dir_two, engine_two) = open_engine("engine-two");
    let scope_two = TenantScope::from_parts(
        crate::types::TenantId::try_new("tenant-a").expect("valid tenant"),
        engine_two.identity(),
    );
    let foreign = engine_two.read_snapshot(&scope_two).expect("snapshot");
    assert_eq!(
        engine_one.queries().vector_search(&request(&[1.0, 0.0], 5), &foreign).unwrap_err(),
        VectorSearchError::SnapshotEngineMismatch
    );
}

#[test]
fn matches_carry_the_document_besides_id_and_score() {
    let (_dir, engine) = open_engine("payload");
    commit(
        &engine,
        &scope(&engine),
        "payload",
        vec![vector_document(
            "doc",
            &[1.0, 0.0],
            &[("kind", DocumentValue::String("manual".to_owned()))],
        )],
    );
    let result = search(&engine, &request(&[1.0, 0.0], 1)).expect("carries");
    assert_eq!(result.matches().len(), 1);
    let matched = &result.matches()[0];
    assert_eq!(matched.id().as_str(), "doc");
    assert_eq!(matched.document().collection().as_str(), "docs");
    assert_eq!(
        matched.document().value(),
        &object(&[
            ("embedding", vector_value(&[1.0, 0.0])),
            ("kind", DocumentValue::String("manual".to_owned()))
        ])
    );
}

// ---------------------------------------------------------------------------
// The semantic conformance corpus: named vectors with pinned outcomes
// ---------------------------------------------------------------------------

/// One semantic conformance vector: named documents with fixed embeddings,
/// a query, and the pinned result order and scores.
struct SemanticVector {
    name: &'static str,
    documents: &'static [(&'static str, &'static [f64])],
    query: &'static [f64],
    limit: u32,
    expected_ids: &'static [&'static str],
    expected_scores: &'static [f64],
}

const SEMANTIC_VECTORS: &[SemanticVector] = &[
    SemanticVector {
        name: "semantic/axis-aligned",
        documents: &[("a", &[1.0, 0.0]), ("b", &[0.0, 1.0])],
        query: &[1.0, 0.0],
        limit: 2,
        expected_ids: &["a", "b"],
        expected_scores: &[1.0, 0.0],
    },
    SemanticVector {
        name: "semantic/diagonal-scale-invariance",
        // Cosine ignores magnitude: [1,1] and [100,100] are the same
        // direction and tie by identity.
        documents: &[("small", &[1.0, 1.0]), ("large", &[100.0, 100.0])],
        query: &[1.0, 1.0],
        limit: 2,
        expected_ids: &["large", "small"],
        expected_scores: &[1.0, 1.0],
    },
    SemanticVector {
        name: "semantic/opposite-direction-scores-minus-one",
        documents: &[("against", &[-1.0, -1.0])],
        query: &[1.0, 1.0],
        limit: 1,
        expected_ids: &["against"],
        expected_scores: &[-1.0],
    },
    SemanticVector {
        name: "semantic/k-truncation-at-one",
        documents: &[("best", &[1.0, 0.0]), ("worse", &[0.9, 0.1])],
        query: &[1.0, 0.0],
        limit: 1,
        expected_ids: &["best"],
        expected_scores: &[1.0],
    },
    SemanticVector {
        name: "semantic/three-dim-space",
        documents: &[("x", &[1.0, 0.0, 0.0]), ("y", &[0.0, 1.0, 0.0]), ("xy", &[1.0, 1.0, 0.0])],
        query: &[1.0, 0.0, 0.0],
        limit: 3,
        expected_ids: &["x", "xy", "y"],
        expected_scores: &[1.0, std::f64::consts::FRAC_1_SQRT_2, 0.0],
    },
];

#[test]
fn semantic_conformance_corpus_is_complete_and_exact() {
    assert_eq!(SEMANTIC_VECTORS.len(), 5, "the semantic corpus carries exactly five vectors");
    for vector in SEMANTIC_VECTORS {
        let (_dir, engine) = open_engine("corpus");
        let documents = vector
            .documents
            .iter()
            .map(|(id, embedding)| vector_document(id, embedding, &[]))
            .collect();
        commit(&engine, &scope(&engine), "corpus", documents);
        let result = search(&engine, &request(vector.query, vector.limit))
            .unwrap_or_else(|error| panic!("vector {} refused: {error:?}", vector.name));
        assert_eq!(
            ids(result.matches()),
            vector.expected_ids,
            "vector {} returned the wrong order",
            vector.name
        );
        for (matched, expected) in result.matches().iter().zip(vector.expected_scores) {
            assert_close(matched.score(), *expected);
        }
    }
}
