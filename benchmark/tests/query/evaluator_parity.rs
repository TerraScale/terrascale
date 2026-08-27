//! GAP-EVAL (#62): public-surface evaluator parity contract tests.
//!
//! These tests reach only what an external caller can construct: the typed
//! clause values the evaluator seam gained for the folded GAP-Q1..Q5 set —
//! membership, range, and pattern conditions, explicit nulls placement,
//! seeded random ordering, and reference-resolution loads — plus the TQL
//! lowering of the same constructs onto that seam. Execution behavior is
//! covered by the in-crate tests (`ReadSnapshot` has no public construction
//! path by design). V1 evidence is the pinned source identity
//! `fa99b9257776dbe625e6a2388c74c9ce916637ee` under
//! `.worktrees/terrabase-v1-fa99b925-readonly`, cited per test and collected
//! in `docs/query/gap-eval-evaluator-parity.md`.

use std::time::Duration;

use terrabase_v2::RequestValidationError;
use terrabase_v2::query::tql::{self, GrammarVersion, TqlErrorClass, TqlParameters};
use terrabase_v2::query::{
    MAX_IN_VALUES, NullsPlacement, OrderDirection, OrderingKey, QueryCondition, QueryLoad,
    QueryOrdering, QueryPredicate, QueryProjection, QuerySource, QuerySpec, StalenessMode,
    StalenessPolicy,
};
use terrabase_v2::types::{Cancellation, Deadline, DocumentValue, TenantId};
use terrabase_v2::{Engine, EngineConfig};

fn location(tag: &str) -> std::path::PathBuf {
    std::env::temp_dir().join(format!("tbv2-gapeval-{tag}-{}", std::process::id()))
}

fn open_engine(tag: &str) -> (std::path::PathBuf, Engine) {
    let dir = location(tag);
    std::fs::remove_dir_all(&dir).ok();
    let engine =
        Engine::open(EngineConfig::durable(&dir).expect("durable location")).expect("engine opens");
    (dir, engine)
}

fn deadline() -> Deadline {
    Deadline::after(Duration::from_secs(30)).expect("valid deadline")
}

fn tenant() -> TenantId {
    TenantId::try_new("tenant-a").expect("valid tenant")
}

fn source() -> QuerySource {
    QuerySource::try_new(terrabase_v2::types::CollectionName::try_new("users").expect("name"))
        .expect("user collection")
}

fn base_spec() -> QuerySpec {
    QuerySpec::try_new(
        tenant(),
        source(),
        QueryPredicate::all(Vec::new()).expect("valid conjunction"),
        QueryOrdering::default(),
        QueryProjection::whole_document(),
        terrabase_v2::query::QueryPaging::try_new(0, None).expect("valid paging"),
        StalenessPolicy::try_new(StalenessMode::Strict, Vec::new()).expect("valid policy"),
        deadline(),
        Cancellation::new(),
    )
    .expect("valid specification")
}

fn strings(values: &[&str]) -> Vec<DocumentValue> {
    values.iter().map(|value| DocumentValue::String((*value).to_owned())).collect()
}

// ---------------------------------------------------------------------------
// Q2: BETWEEN, membership, and pattern conditions (GAP-Q2/Q3)
// ---------------------------------------------------------------------------

/// Q3 (PB-4005): `in` is V1's `WhereIn`/`ContainsAny` operator — the field
/// matches when any listed value equals it (client
/// AbstractDocumentQuery.cs:746 and :1499-1510 both lower to the In
/// operator), and `all in` is `ContainsAll` (all listed values must be
/// carried; an empty ContainsAll lowers to TrueToken and matches every
/// document, AbstractDocumentQuery.cs:1516-1537).
#[test]
fn membership_conditions_construct_with_bounded_value_lists() {
    let values = strings(&["red", "green"]);
    let condition = QueryCondition::In { field: "tags".to_owned(), values };
    assert_eq!(condition.field(), "tags");
    let all = QueryCondition::AllIn { field: "tags".to_owned(), values: strings(&["red"]) };
    assert_eq!(all.field(), "tags");

    assert_eq!(
        QueryPredicate::all(vec![QueryCondition::In { field: String::new(), values: Vec::new() }])
            .unwrap_err(),
        RequestValidationError::EmptyField
    );
    // The value list is bounded: one closed limit keeps every condition a
    // bounded value, in the seam's MAX_CONDITIONS discipline.
    let too_many = strings(&["a"; MAX_IN_VALUES + 1]);
    assert_eq!(
        QueryPredicate::all(vec![QueryCondition::In {
            field: "tags".to_owned(),
            values: too_many
        }])
        .unwrap_err(),
        RequestValidationError::InvalidQuery
    );
}

/// Q2 (PB-4006): `between` is V1's `BetweenExpression`. V1 treats a null
/// bound as unbounded and includes explicit nulls when the upper bound is
/// unbounded (CoraxBooleanItem.BuildBetween,
/// CoraxBooleanItem.cs:105-160), so null bounds construct here and the
/// evaluation pins are in-crate.
#[test]
fn between_condition_constructs_including_null_bounds() {
    let condition = QueryCondition::Between {
        field: "age".to_owned(),
        low: DocumentValue::Integer(1),
        high: DocumentValue::Integer(100),
    };
    assert_eq!(condition.field(), "age");
    let unbounded = QueryCondition::Between {
        field: "age".to_owned(),
        low: DocumentValue::Null,
        high: DocumentValue::Null,
    };
    assert_eq!(unbounded.field(), "age");
}

/// Q3 (PB-4007): prefix, suffix, and substring conditions construct over the
/// string domain; V1 lowers `startsWith`/`endsWith` through its pattern
/// methods (LuceneQueryBuilder.cs:423-426) and `contains` as the substring
/// wildcard form.
#[test]
fn pattern_conditions_construct_over_the_string_domain() {
    assert_eq!(
        QueryCondition::StartsWith { field: "name".to_owned(), value: "ad".to_owned() }.field(),
        "name"
    );
    assert_eq!(
        QueryCondition::EndsWith { field: "name".to_owned(), value: "da".to_owned() }.field(),
        "name"
    );
    assert_eq!(
        QueryCondition::Contains { field: "name".to_owned(), value: "da".to_owned() }.field(),
        "name"
    );
    assert_eq!(
        QueryPredicate::all(vec![QueryCondition::StartsWith {
            field: "name".to_owned(),
            value: String::new()
        }])
        .unwrap_err(),
        RequestValidationError::InvalidQuery
    );
}

// ---------------------------------------------------------------------------
// Q4: explicit nulls placement and seeded random ordering (GAP-Q4)
// ---------------------------------------------------------------------------

/// Q4 (PB-4013): V1's `NullsOrdering.First`/`Last` place nulls first or last
/// regardless of the sort direction, and only the Corax engine honors them
/// (src/TerraBase.Client/Documents/Session/Ordering.cs:15-43; the Corax
/// mapping at CoraxQueryBuilder.cs:1334-1339). The default construction
/// stays `Implicit`, which keeps the seam's own total document order.
#[test]
fn explicit_nulls_placement_constructs_and_defaults_to_implicit() {
    let implicit = OrderingKey::try_new("age", OrderDirection::Ascending).expect("valid key");
    assert_eq!(implicit.nulls_placement(), NullsPlacement::Implicit);
    let explicit =
        OrderingKey::try_new_with_nulls("age", OrderDirection::Descending, NullsPlacement::First)
            .expect("valid key");
    assert_eq!(explicit.nulls_placement(), NullsPlacement::First);
    assert_eq!(explicit.direction(), OrderDirection::Descending);
    assert_eq!(
        OrderingKey::try_new_with_nulls("", OrderDirection::Ascending, NullsPlacement::Last)
            .unwrap_err(),
        RequestValidationError::EmptyField
    );
}

/// Q4 (PB-4013): seeded random ordering. V1 derives a deterministic order
/// from the seed argument (CoraxQueryBuilder.cs:1343-1347 hashes the seed;
/// Lucene's RandomFieldComparator.cs:20-24 seeds Random with the field
/// hash), and V1's two engines already produce different sequences for the
/// same seed, so the parity contract is determinism per seed, not one
/// canonical sequence.
#[test]
fn seeded_random_ordering_constructs_and_reports_its_seed() {
    let ordering = QueryOrdering::random_with_seed(42);
    assert_eq!(ordering.random_seed(), Some(42));
    assert!(ordering.keys().is_empty());
    // The field ordering construction stays untouched and implicit.
    let keys = QueryOrdering::try_new(vec![
        OrderingKey::try_new("age", OrderDirection::Ascending).expect("valid key"),
    ])
    .expect("valid ordering");
    assert_eq!(keys.random_seed(), None);
    assert_eq!(keys.keys().len(), 1);
}

// ---------------------------------------------------------------------------
// Q5: reference-resolution loads (GAP-Q5)
// ---------------------------------------------------------------------------

/// Q5 (PB-4015): `load` binds a document id read at a source field to an
/// alias (V1 QueryMetadata.cs:1082-1136 binds each alias once and rejects
/// duplicates); the loaded document is the referenced source document —
/// the stored-field serving path is gated on GAP-Q6 and not part of this
/// seam.
#[test]
fn loads_bind_aliases_and_validate_on_the_typed_spec() {
    let load = QueryLoad::try_new("customerId", "c").expect("valid load");
    assert_eq!(load.field(), "customerId");
    assert_eq!(load.alias(), "c");
    assert_eq!(QueryLoad::try_new("", "c").unwrap_err(), RequestValidationError::EmptyField);

    let spec = base_spec().with_loads(vec![load]).expect("loads bind");
    assert_eq!(spec.loads().len(), 1);
    assert_eq!(spec.loads()[0].alias(), "c");

    // Duplicate aliases are rejected, exactly as V1 rejects them.
    let duplicated = vec![
        QueryLoad::try_new("customerId", "c").expect("valid load"),
        QueryLoad::try_new("warehouseId", "c").expect("valid load"),
    ];
    assert_eq!(
        base_spec().with_loads(duplicated).unwrap_err(),
        RequestValidationError::InvalidQuery
    );
}

// ---------------------------------------------------------------------------
// TQL lowering of the expanded predicates onto the seam
// ---------------------------------------------------------------------------

fn parse_and_lower(
    text: &str,
    parameters: &TqlParameters,
) -> Result<QuerySpec, terrabase_v2::query::tql::TqlError> {
    let query = tql::parse(GrammarVersion::CURRENT, text, parameters).expect("parses");
    tql::lower_to_spec(&query, parameters, tenant(), deadline(), Cancellation::new())
}

/// Q2/Q3: `between`, `in`, `all in`, and the pattern functions lower onto
/// the seam's expanded conditions instead of failing closed.
#[test]
fn tql_lowers_between_membership_and_pattern_predicates() {
    let parameters = TqlParameters::new();
    let spec =
        parse_and_lower("from users where age between 20 and 30", &parameters).expect("lowers");
    assert_eq!(
        spec.predicate().conditions(),
        [QueryCondition::Between {
            field: "age".to_owned(),
            low: DocumentValue::Integer(20),
            high: DocumentValue::Integer(30),
        }]
    );

    let spec = parse_and_lower("from users where tag in ('a', 'b')", &parameters).expect("lowers");
    assert_eq!(
        spec.predicate().conditions(),
        [QueryCondition::In { field: "tag".to_owned(), values: strings(&["a", "b"]) }]
    );

    let spec =
        parse_and_lower("from users where tag all in ('a', 'b')", &parameters).expect("lowers");
    assert_eq!(
        spec.predicate().conditions(),
        [QueryCondition::AllIn { field: "tag".to_owned(), values: strings(&["a", "b"]) }]
    );

    let spec =
        parse_and_lower("from users where starts_with(name, 'ad')", &parameters).expect("lowers");
    assert_eq!(
        spec.predicate().conditions(),
        [QueryCondition::StartsWith { field: "name".to_owned(), value: "ad".to_owned() }]
    );

    let spec =
        parse_and_lower("from users where ends_with(name, 'da')", &parameters).expect("lowers");
    assert_eq!(
        spec.predicate().conditions(),
        [QueryCondition::EndsWith { field: "name".to_owned(), value: "da".to_owned() }]
    );

    let spec =
        parse_and_lower("from users where contains(name, 'da')", &parameters).expect("lowers");
    assert_eq!(
        spec.predicate().conditions(),
        [QueryCondition::Contains { field: "name".to_owned(), value: "da".to_owned() }]
    );
}

/// Q4: explicit nulls placement lowers in both directions — the placement is
/// direction-independent in V1 (CoraxQueryBuilder.cs:1334-1339), so
/// `desc nulls first` and `asc nulls last` must lower too, not just the two
/// spellings that agree with a wholesale reverse.
#[test]
fn tql_lowers_explicit_nulls_placement_in_both_directions() {
    let parameters = TqlParameters::new();
    let spec = parse_and_lower("from users order by age desc nulls first", &parameters)
        .expect("desc nulls first lowers");
    let key = &spec.ordering().keys()[0];
    assert_eq!(key.field(), "age");
    assert_eq!(key.direction(), OrderDirection::Descending);
    assert_eq!(key.nulls_placement(), NullsPlacement::First);

    let spec = parse_and_lower("from users order by age asc nulls last", &parameters)
        .expect("asc nulls last lowers");
    let key = &spec.ordering().keys()[0];
    assert_eq!(key.direction(), OrderDirection::Ascending);
    assert_eq!(key.nulls_placement(), NullsPlacement::Last);
}

/// Q5: `load x as alias` lowers onto the loads clause and alias-qualified
/// projection items lower onto qualified projection names (V1 binds the
/// alias and resolves the referenced source document,
/// QueryMetadata.cs:1082-1136).
#[test]
fn tql_lowers_load_items_and_qualified_projections() {
    let parameters = TqlParameters::new();
    let spec =
        parse_and_lower("select total, c.name from orders load customerId as c", &parameters)
            .expect("load lowers");
    assert_eq!(spec.loads().len(), 1);
    assert_eq!(spec.loads()[0].field(), "customerId");
    assert_eq!(spec.loads()[0].alias(), "c");
    assert_eq!(
        spec.projection().selected().map(|names| names.to_vec()),
        Some(vec!["total".to_owned(), "c.name".to_owned()])
    );
}

/// The still-unlowerable constructs keep failing closed with the planner
/// class: `or` and `not` (boolean predicates beyond a conjunction),
/// arithmetic, and every function outside the lowerable set — V1 regex rides
/// the engines' term-level regex machines and TQL's registry has no regex
/// function, so the engine keeps no regex of its own (deferred with
/// evidence in the close-out record).
#[test]
fn tql_keeps_failing_closed_for_boolean_arithmetic_and_regex() {
    let parameters = TqlParameters::new();
    assert_eq!(
        parse_and_lower("from users where a = 1 or b = 2", &parameters).unwrap_err().class(),
        TqlErrorClass::UnsupportedForPlanning
    );
    assert_eq!(
        parse_and_lower("from users where not (a = 1)", &parameters).unwrap_err().class(),
        TqlErrorClass::UnsupportedForPlanning
    );
    assert_eq!(
        parse_and_lower("from users where a + 1 = 2", &parameters).unwrap_err().class(),
        TqlErrorClass::UnsupportedForPlanning
    );
    // No regex spelling exists anywhere in tql/1.0 — the grammar has no
    // regex production and the registry no regex function — so the text is
    // rejected at parse and never reaches a degraded pattern.
    assert_eq!(
        tql::parse(GrammarVersion::CURRENT, "from users where name regex '^ad'", &parameters)
            .unwrap_err()
            .class(),
        TqlErrorClass::ParseError
    );
}

// The engine handle exists only to prove the public acquisition path still
// resolves with the expanded seam; no execution happens here.
#[test]
fn the_expanded_seam_still_acquires_through_the_engine() {
    let (dir, engine) = open_engine("acquire");
    let store = engine.queries();
    let input = terrabase_v2::QueryInput::typed(base_spec());
    let plan = store.compile(input).expect("compiles");
    drop(plan);
    drop(store);
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}
