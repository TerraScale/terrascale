//! In-crate TQL tests: lowered-plan execution parity and bounded parsing.
//!
//! Execution parity needs `ReadSnapshot` acquisition, which mirrors Platform's
//! production role through `TenantScope::from_parts` because `TenantScope` has
//! no public constructor by design; these tests therefore live in the crate
//! next to the other W3-2 execution tests. The property suite runs the parser
//! over adversarial generated input under explicit size and time budgets and
//! asserts the fail-closed contract: no panic, a typed outcome, and the same
//! outcome for the same input.

use std::{
    collections::BTreeMap,
    path::PathBuf,
    sync::atomic::{AtomicU64, Ordering},
    time::{Duration, Instant},
};

use super::{
    GrammarVersion, TqlCapabilities, TqlError, TqlErrorClass, TqlParameters, compile,
    lower_to_spec, parse, parse_with,
};
use crate::{
    engine::{CommitOutcome, Engine, EngineConfig},
    operations::QueryInput,
    query::{
        OrderDirection, OrderingKey, QueryCondition, QueryOrdering, QueryPaging, QueryPredicate,
        QueryProjection, QuerySource, QuerySpec, QueryStore, StalenessMode, StalenessPolicy, tql,
    },
    types::{
        Cancellation, CollectionName, Deadline, Document, DocumentId, DocumentValue,
        ExpectedVersion, LocalDurability, OperationId, ReadSnapshot, TenantId, TenantScope,
    },
};

fn location(tag: &str) -> PathBuf {
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    let index = COUNTER.fetch_add(1, Ordering::Relaxed);
    std::env::temp_dir().join(format!("tbv2-tql02-{tag}-{}-{index}", std::process::id()))
}

fn open_engine(tag: &str) -> (PathBuf, Engine) {
    let dir = location(tag);
    std::fs::remove_dir_all(&dir).ok();
    let engine =
        Engine::open(EngineConfig::durable(&dir).expect("durable location")).expect("engine opens");
    (dir, engine)
}

fn tenant(name: &str) -> TenantId {
    TenantId::try_new(name).expect("valid tenant")
}

fn scope(engine: &Engine, name: &str) -> TenantScope {
    TenantScope::from_parts(tenant(name), engine.identity())
}

fn object(fields: &[(&str, DocumentValue)]) -> DocumentValue {
    DocumentValue::Object(
        fields
            .iter()
            .map(|(name, value)| ((*name).to_owned(), value.clone()))
            .collect::<BTreeMap<_, _>>(),
    )
}

fn commit_documents(engine: &Engine, scope: &TenantScope, tag: &str, documents: Vec<Document>) {
    let snapshot = engine.read_snapshot(scope).expect("snapshot");
    let operation = OperationId::try_new(tag).expect("valid identifier");
    let mut transaction = engine.begin(scope, snapshot, operation).expect("begins");
    for document in documents {
        transaction.put(document, ExpectedVersion::Any).expect("stages");
    }
    match transaction.commit(LocalDurability::Synced) {
        CommitOutcome::Committed(_) => {}
        other => panic!("commit did not succeed: {other:?}"),
    }
}

fn user(id: &str, name: &str, country: &str, age: i64, active: bool) -> Document {
    Document::new(
        DocumentId::try_new(id).expect("valid identifier"),
        CollectionName::try_new("users").expect("valid identifier"),
        object(&[
            ("name", DocumentValue::String(name.to_owned())),
            ("country", DocumentValue::String(country.to_owned())),
            ("age", DocumentValue::Integer(age)),
            ("active", DocumentValue::Boolean(active)),
        ]),
    )
}

fn deadline() -> Deadline {
    Deadline::after(Duration::from_secs(30)).expect("valid deadline")
}

/// Commits one known user corpus and returns a live snapshot over it.
fn corpus(engine: &Engine, tag: &str) -> ReadSnapshot {
    let scope = scope(engine, "tenant-a");
    commit_documents(
        engine,
        &scope,
        tag,
        vec![
            user("users/1", "ada", "NO", 36, true),
            user("users/2", "bob", "SE", 21, true),
            user("users/3", "carol", "NO", 44, false),
            user("users/4", "dave", "DK", 17, true),
            user("users/5", "erin", "NO", 25, true),
        ],
    );
    engine.read_snapshot(&scope).expect("snapshot")
}

// ---------------------------------------------------------------------------
// Lowered plans execute through the query seam with result parity
// ---------------------------------------------------------------------------

/// Builds the typed specification that is equivalent to one TQL text and
/// returns both execution results for comparison.
fn parity_pair(
    store: &QueryStore<'_>,
    snapshot: &ReadSnapshot,
    text: &str,
    parameters: &TqlParameters,
    typed: QuerySpec,
) -> (crate::query::QueryResult, crate::query::QueryResult) {
    let query = parse(GrammarVersion::CURRENT, text, parameters)
        .unwrap_or_else(|error| panic!("{text} parses: {error:?}"));
    let plan =
        compile(store, &query, parameters, tenant("tenant-a"), deadline(), Cancellation::new())
            .unwrap_or_else(|error| panic!("{text} lowers: {error:?}"));
    let tql_result = store.execute(&plan, snapshot).expect("the TQL plan executes");
    let typed_plan =
        store.compile(QueryInput::typed(typed)).expect("the typed specification compiles");
    let typed_result = store.execute(&typed_plan, snapshot).expect("the typed plan executes");
    (tql_result, typed_result)
}

fn typed_spec(
    predicate: QueryPredicate,
    ordering: QueryOrdering,
    projection: QueryProjection,
    paging: QueryPaging,
) -> QuerySpec {
    QuerySpec::try_new(
        tenant("tenant-a"),
        QuerySource::try_new(CollectionName::try_new("users").expect("valid identifier"))
            .expect("user collection"),
        predicate,
        ordering,
        projection,
        paging,
        StalenessPolicy::try_new(StalenessMode::Strict, Vec::new()).expect("valid policy"),
        deadline(),
        Cancellation::new(),
    )
    .expect("valid specification")
}

#[test]
fn lowered_field_projection_matches_the_equivalent_typed_query() {
    let (dir, engine) = open_engine("parity-projection");
    let store = engine.queries();
    let snapshot = corpus(&engine, "projection");
    let (tql_result, typed_result) = parity_pair(
        &store,
        &snapshot,
        "select name from users where country = $country and age > 25 order by age desc limit 2 \
         offset 1",
        &TqlParameters::try_new(&[("country", DocumentValue::String("NO".to_owned()))])
            .expect("valid parameters"),
        typed_spec(
            QueryPredicate::all(vec![
                QueryCondition::Equals {
                    field: "country".to_owned(),
                    value: DocumentValue::String("NO".to_owned()),
                },
                QueryCondition::GreaterThan {
                    field: "age".to_owned(),
                    value: DocumentValue::Integer(25),
                },
            ])
            .expect("valid conjunction"),
            QueryOrdering::try_new(vec![
                OrderingKey::try_new("age", OrderDirection::Descending).expect("valid key"),
            ])
            .expect("valid ordering"),
            QueryProjection::fields(&["name"]).expect("valid projection"),
            QueryPaging::try_new(1, Some(2)).expect("valid paging"),
        ),
    );
    assert_eq!(tql_result, typed_result);
    // The shared corpus makes the expected page observable: users over 25 in
    // NO are ada (36) and carol (44) and erin (25 is not > 25); ordered
    // desc, offset 1 of limit 2 leaves ada's whole row, projected to `name`.
    assert_eq!(tql_result.matched(), 2);
    assert_eq!(tql_result.rows().len(), 1);
    assert_eq!(tql_result.rows()[0].document().id().as_str(), "users/1");
    assert_eq!(
        tql_result.rows()[0].projected(),
        &[("name".to_owned(), DocumentValue::String("ada".to_owned()))][..]
    );
    drop(store);
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn lowered_whole_document_queries_match_the_equivalent_typed_query() {
    let (dir, engine) = open_engine("parity-whole");
    let store = engine.queries();
    let snapshot = corpus(&engine, "whole");
    let (tql_result, typed_result) = parity_pair(
        &store,
        &snapshot,
        "from users where active = true",
        &TqlParameters::new(),
        typed_spec(
            QueryPredicate::all(vec![QueryCondition::Equals {
                field: "active".to_owned(),
                value: DocumentValue::Boolean(true),
            }])
            .expect("valid conjunction"),
            QueryOrdering::default(),
            QueryProjection::whole_document(),
            QueryPaging::try_new(0, None).expect("valid paging"),
        ),
    );
    assert_eq!(tql_result, typed_result);
    assert_eq!(tql_result.matched(), 4);
    drop(store);
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn lowered_operator_and_presence_predicates_match_typed_conditions() {
    let (dir, engine) = open_engine("parity-operators");
    let store = engine.queries();
    let snapshot = corpus(&engine, "operators");
    for (text, condition, expected_matched) in [
        (
            "from users where age != 21",
            QueryCondition::NotEquals {
                field: "age".to_owned(),
                value: DocumentValue::Integer(21),
            },
            4_u64,
        ),
        (
            "from users where age < 30",
            QueryCondition::LessThan { field: "age".to_owned(), value: DocumentValue::Integer(30) },
            3,
        ),
        (
            "from users where age <= 25",
            QueryCondition::LessOrEqual {
                field: "age".to_owned(),
                value: DocumentValue::Integer(25),
            },
            3,
        ),
        (
            "from users where age >= 36",
            QueryCondition::GreaterOrEqual {
                field: "age".to_owned(),
                value: DocumentValue::Integer(36),
            },
            2,
        ),
        // No corpus document carries `note`: presence tests distinguish the
        // empty field from the absent one.
        ("from users where exists(note)", QueryCondition::Exists { field: "note".to_owned() }, 0),
        ("from users where missing(note)", QueryCondition::Missing { field: "note".to_owned() }, 5),
    ] {
        let (tql_result, typed_result) = parity_pair(
            &store,
            &snapshot,
            text,
            &TqlParameters::new(),
            typed_spec(
                QueryPredicate::all(vec![condition]).expect("valid conjunction"),
                QueryOrdering::default(),
                QueryProjection::whole_document(),
                QueryPaging::try_new(0, None).expect("valid paging"),
            ),
        );
        assert_eq!(tql_result, typed_result, "{text}");
        assert_eq!(tql_result.matched(), expected_matched, "{text}");
    }
    drop(store);
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn lowered_aliased_sources_strip_the_qualifier() {
    let (dir, engine) = open_engine("parity-alias");
    let store = engine.queries();
    let snapshot = corpus(&engine, "alias");
    let (tql_result, typed_result) = parity_pair(
        &store,
        &snapshot,
        "from users as u where u.country = 'SE' order by u.age",
        &TqlParameters::new(),
        typed_spec(
            QueryPredicate::all(vec![QueryCondition::Equals {
                field: "country".to_owned(),
                value: DocumentValue::String("SE".to_owned()),
            }])
            .expect("valid conjunction"),
            QueryOrdering::try_new(vec![
                OrderingKey::try_new("age", OrderDirection::Ascending).expect("valid key"),
            ])
            .expect("valid ordering"),
            QueryProjection::whole_document(),
            QueryPaging::try_new(0, None).expect("valid paging"),
        ),
    );
    assert_eq!(tql_result, typed_result);
    assert_eq!(tql_result.matched(), 1);
    drop(store);
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn lowered_plans_stay_inside_the_snapshot_tenant_contract() {
    let (dir, engine) = open_engine("parity-tenant");
    let store = engine.queries();
    let snapshot = corpus(&engine, "tenant");
    let query =
        parse(GrammarVersion::CURRENT, "from users where country = 'NO'", &TqlParameters::new())
            .expect("parses");
    // A lowered plan is an ordinary seam plan: it compiles with any claim and
    // the seam itself rejects the snapshot mismatch before any data access,
    // exactly as for a typed specification.
    let plan = compile(
        &store,
        &query,
        &TqlParameters::new(),
        tenant("tenant-b"),
        deadline(),
        Cancellation::new(),
    )
    .expect("compiling a claim is not a data access");
    assert_eq!(
        store.execute(&plan, &snapshot).unwrap_err(),
        crate::query::QueryError::SnapshotTenantMismatch
    );
    drop(store);
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

// ---------------------------------------------------------------------------
// Bounded, deterministic parsing on adversarial input
// ---------------------------------------------------------------------------

/// A deterministic splitmix64 generator: the property suite never depends on
/// an external fuzzer or on wall-clock seeding, so a failing iteration is
/// reproducible from its seed.
struct SplitMix64(u64);

impl SplitMix64 {
    fn next(&mut self) -> u64 {
        self.0 = self.0.wrapping_add(0x9e37_79b9_7f4a_7c15);
        let mut mixed = self.0;
        mixed = (mixed ^ (mixed >> 30)).wrapping_mul(0xbf58_476d_1ce4_e5b9);
        mixed = (mixed ^ (mixed >> 27)).wrapping_mul(0x94d0_49bb_1331_11eb);
        mixed ^ (mixed >> 31)
    }

    fn below(&mut self, bound: usize) -> usize {
        usize::try_from(self.next() % (bound as u64)).expect("a bounded remainder fits usize")
    }
}

/// The adversarial alphabet: every punctuation class, both comment openers,
/// both quote forms, both escape forms, whitespace, keywords, and digits.
const ADVERSARIAL_ALPHABET: &[&str] = &[
    "(",
    ")",
    "[",
    "]",
    "{",
    "}",
    ",",
    ":",
    ".",
    "$",
    "@",
    "=",
    "!=",
    "<>",
    "<=",
    ">=",
    "<",
    ">",
    "+",
    "-",
    "*",
    "/",
    "%",
    "'",
    "\"",
    "''",
    "\"\"",
    "//",
    "/*",
    "*/",
    " ",
    "\n",
    "\t",
    "all",
    "in",
    "and",
    "or",
    "not",
    "between",
    "select",
    "from",
    "where",
    "order",
    "by",
    "limit",
    "offset",
    "include",
    "load",
    "group",
    "as",
    "distinct",
    "null",
    "true",
    "false",
    "nulls",
    "first",
    "last",
    "asc",
    "desc",
    "@id",
    "@collection",
    "@all_docs",
    "$p",
    "x",
    "9",
    "0.5",
    "\u{b}",
];

fn generate(seed: u64, rounds: usize) -> String {
    let mut generator = SplitMix64(seed);
    let mut text = String::new();
    for _ in 0..rounds {
        text.push_str(ADVERSARIAL_ALPHABET[generator.below(ADVERSARIAL_ALPHABET.len())]);
    }
    text
}

#[test]
fn adversarial_input_never_panics_and_always_yields_a_typed_outcome() {
    const ROUNDS: u64 = 4096;
    const MAX_ROUNDS_PER_INPUT: usize = 96;
    const TIME_BUDGET: Duration = Duration::from_secs(30);
    let started = Instant::now();
    let parameters = TqlParameters::new();
    for seed in 0..ROUNDS {
        let text = generate(
            seed,
            1 + usize::try_from(seed % (MAX_ROUNDS_PER_INPUT as u64))
                .expect("a bounded remainder fits usize"),
        );
        let outcome = parse(GrammarVersion::CURRENT, &text, &parameters);
        match &outcome {
            Ok(query) => {
                assert!(!query.outline().is_empty());
                assert_eq!(query.span.end().byte(), text.len());
            }
            Err(error) => {
                assert_ne!(error.class(), TqlErrorClass::MissingGrammarVersion);
                assert_ne!(error.class(), TqlErrorClass::UnsupportedGrammarVersion);
            }
        }
        // Determinism: the same input triple yields the identical outcome.
        let replay = parse(GrammarVersion::CURRENT, &text, &parameters);
        assert_eq!(outcome.is_ok(), replay.is_ok());
        if let (Ok(first), Ok(second)) = (&outcome, &replay) {
            assert_eq!(first, second);
        }
        assert!(
            started.elapsed() <= TIME_BUDGET,
            "the property suite exceeded its {TIME_BUDGET:?} budget at seed {seed}"
        );
    }
}

#[test]
fn deep_nesting_is_bounded_by_a_typed_error_not_the_stack() {
    const FULL_TEXT_BYTES: usize = crate::operations::MAX_QUERY_TEXT_BYTES;
    let parameters = TqlParameters::new();
    let nested = format!("from t where {}", "(".repeat(FULL_TEXT_BYTES));
    let error = parse(GrammarVersion::CURRENT, &nested, &parameters)
        .expect_err("unbounded nesting fails closed");
    assert!(matches!(error.class(), TqlErrorClass::ParseError | TqlErrorClass::InvalidArgument));
    let negations = format!("from t where {}a = 1", "not ".repeat(FULL_TEXT_BYTES / 4));
    assert!(parse(GrammarVersion::CURRENT, &negations, &parameters).is_err());
    let objects =
        format!("select {}1{}", "{a:".repeat(FULL_TEXT_BYTES / 4), "}".repeat(FULL_TEXT_BYTES / 4));
    assert!(parse(GrammarVersion::CURRENT, &objects, &parameters).is_err());
    let arrays =
        format!("select {}1{}", "[".repeat(FULL_TEXT_BYTES / 2), "]".repeat(FULL_TEXT_BYTES / 2));
    assert!(parse(GrammarVersion::CURRENT, &arrays, &parameters).is_err());
    let calls = format!("from t where {}1)", "len(".repeat(FULL_TEXT_BYTES / 4));
    assert!(parse(GrammarVersion::CURRENT, &calls, &parameters).is_err());
    // Path segment chains are bounded by the same rule.
    let path = format!("from t where {}", "a.".repeat(FULL_TEXT_BYTES / 2));
    assert!(parse(GrammarVersion::CURRENT, &path, &parameters).is_err());
}

#[test]
fn worst_case_input_parses_within_the_time_budget() {
    const TIME_BUDGET: Duration = Duration::from_secs(5);
    let parameters = TqlParameters::new();
    let unit_bytes = crate::operations::MAX_QUERY_TEXT_BYTES;
    // Long conjunctions, long token runs, and long comment bodies each cover
    // one scan-heavy shape at the full input bound.
    let conjunction = format!("from t where {}a = 1", "a = 1 and ".repeat(unit_bytes / 8));
    let identifier = format!("from t where {} = 1", "a".repeat(unit_bytes / 2));
    let comment = format!("from t where /*{}*/ a = 1", "x".repeat(unit_bytes / 2));
    let started = Instant::now();
    for text in [&conjunction, &identifier, &comment] {
        let outcome = parse(GrammarVersion::CURRENT, text, &parameters);
        assert!(outcome.is_ok() || outcome.is_err());
    }
    let elapsed = started.elapsed();
    assert!(
        elapsed < TIME_BUDGET,
        "three full-bound inputs parsed in {elapsed:?}, over the {TIME_BUDGET:?} budget"
    );
}

#[test]
fn the_same_artifact_parses_identically_across_capabilities_only_when_ungated() {
    let parameters = TqlParameters::new();
    let text = "from t where len(a) > 1";
    let ungated = parse(GrammarVersion::CURRENT, text, &parameters).expect("parses");
    let with_all = parse_with(GrammarVersion::CURRENT, text, &parameters, &TqlCapabilities::none())
        .expect("parses identically without gated calls");
    assert_eq!(ungated, with_all);
}

#[test]
fn error_positions_report_line_and_column_from_the_source_text() {
    let error = parse(GrammarVersion::CURRENT, "from t\nwhere x =", &TqlParameters::new())
        .expect_err("a truncated comparison fails closed");
    let position = error.position().expect("positioned");
    assert_eq!(position.line(), 2);
    assert_eq!(position.column(), u32::try_from("where x =".len()).expect("short line") + 1);
    assert_eq!(position.byte(), "from t\nwhere x =".len());
}

// ---------------------------------------------------------------------------
// Lowering entry preconditions
// ---------------------------------------------------------------------------

#[test]
fn lowering_validates_identifier_discipline_at_the_seam() {
    // A quoted collection name with leading whitespace fails the shared
    // identifier discipline; the parse checker rejects it before lowering,
    // with the same class the seam would report.
    let padded = format!("from \"{}\" limit 1", " ".repeat(8));
    let error = parse(GrammarVersion::CURRENT, &padded, &TqlParameters::new())
        .expect_err("the shared identifier discipline fails the collection");
    assert_eq!(error.class(), TqlErrorClass::InvalidIdentifier);
    assert_eq!(error.grammar_version(), Some(GrammarVersion::CURRENT));
    // Identifiers that pass the shared discipline lower.
    let fine =
        parse(GrammarVersion::CURRENT, "from \"Orders/Archived\" limit 1", &TqlParameters::new())
            .expect("parses");
    assert!(
        lower_to_spec(
            &fine,
            &TqlParameters::new(),
            tenant("tenant-a"),
            deadline(),
            Cancellation::new()
        )
        .is_ok()
    );
}

#[test]
fn lowered_conjunction_beyond_the_seam_condition_bound_fails_closed() {
    let conditions: Vec<String> = (0..40).map(|index| format!("f{index} = 1")).collect();
    let text = format!("from t where {}", conditions.join(" and "));
    let query = parse(GrammarVersion::CURRENT, &text, &TqlParameters::new())
        .expect("the grammar has no condition-count rule");
    let error = lower_to_spec(
        &query,
        &TqlParameters::new(),
        tenant("tenant-a"),
        deadline(),
        Cancellation::new(),
    )
    .expect_err("the seam's condition bound fails the lowering");
    assert_eq!(error.class(), TqlErrorClass::UnsupportedForPlanning);
}

// Keep the crate-linked index catalog seam observable for future planner
// work; this test pins that a TQL-lowered plan is an ordinary seam plan.
#[test]
fn a_lowered_plan_is_an_ordinary_query_plan_value() {
    let (dir, engine) = open_engine("plan-value");
    let store = engine.queries();
    let query = parse(GrammarVersion::CURRENT, "from users limit 1", &TqlParameters::new())
        .expect("parses");
    let plan = tql::compile(
        &store,
        &query,
        &TqlParameters::new(),
        tenant("tenant-a"),
        deadline(),
        Cancellation::new(),
    )
    .expect("compiles");
    let copy = plan.clone();
    assert_eq!(copy.source(), plan.source());
    assert_eq!(copy.tenant_claim(), plan.tenant_claim());
    drop(store);
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

/// The error type stays cloneable and comparable so surfaces can deduplicate
/// repeated failures; this also pins the public shape.
#[test]
fn errors_are_values() {
    let first: TqlError = parse(GrammarVersion::CURRENT, "from", &TqlParameters::new())
        .expect_err("truncated text fails");
    let second = first.clone();
    assert_eq!(first, second);
}
