//! RQL-parity execution conformance (ticket TQL-04).
//!
//! The public corpus (`tests/query/tql_rql_parity_vectors.rs`) proves every
//! mapping row parses and lowers, or fails closed exactly as recorded. This
//! module proves the executable subset executes equivalently: each vector
//! commits one shared corpus, runs the TQL text through the one compile and
//! execute seam, and asserts two things — the result equals the result of
//! the equivalent hand-built typed specification (the W3-2 execution-parity
//! rule this seam already uses), and the result is exactly the expected rows
//! the vector pins over the shared corpus. The V1 RQL text travels with each
//! vector as pinned evidence; V1 is not executed in this repository.
//!
//! Two findings are pinned here rather than hidden: the implicit any-element
//! match over an array-topped field (row R26) lowers and executes
//! structurally today, which does not yet realize the language's any-match
//! rule, and that behavior is asserted as the current observable contract.

use std::{
    collections::BTreeMap,
    path::PathBuf,
    sync::atomic::{AtomicU64, Ordering},
    time::Duration,
};

use super::{GrammarVersion, TqlParameters, compile, parse};
use crate::{
    engine::{CommitOutcome, Engine, EngineConfig},
    operations::QueryInput,
    query::{
        NullsPlacement, OrderDirection, OrderingKey, QueryCondition, QueryOrdering, QueryPaging,
        QueryPredicate, QueryProjection, QuerySource, QuerySpec, StalenessMode, StalenessPolicy,
    },
    types::{
        Cancellation, CollectionName, Deadline, Document, DocumentId, DocumentValue,
        ExpectedVersion, LocalDurability, OperationId, ReadSnapshot, TenantId, TenantScope,
    },
};

fn location(tag: &str) -> PathBuf {
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    let index = COUNTER.fetch_add(1, Ordering::Relaxed);
    std::env::temp_dir().join(format!("tbv2-tql04-{tag}-{}-{index}", std::process::id()))
}

fn open_engine(tag: &str) -> (PathBuf, Engine) {
    let dir = location(tag);
    std::fs::remove_dir_all(&dir).ok();
    let engine =
        Engine::open(EngineConfig::durable(&dir).expect("durable location")).expect("engine opens");
    (dir, engine)
}

fn tenant() -> TenantId {
    TenantId::try_new("tenant-a").expect("valid tenant")
}

fn scope(engine: &Engine) -> TenantScope {
    TenantScope::from_parts(tenant(), engine.identity())
}

fn deadline() -> Deadline {
    Deadline::after(Duration::from_secs(30)).expect("valid deadline")
}

fn object(fields: &[(&str, DocumentValue)]) -> DocumentValue {
    DocumentValue::Object(
        fields
            .iter()
            .map(|(name, value)| ((*name).to_owned(), value.clone()))
            .collect::<BTreeMap<_, _>>(),
    )
}

fn commit_documents(engine: &Engine, tag: &str, documents: Vec<Document>) -> ReadSnapshot {
    let tenant_scope = scope(engine);
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let operation = OperationId::try_new(tag).expect("valid identifier");
    let mut transaction = engine.begin(&tenant_scope, snapshot, operation).expect("begins");
    for document in documents {
        transaction.put(document, ExpectedVersion::Any).expect("stages");
    }
    match transaction.commit(LocalDurability::Synced) {
        CommitOutcome::Committed(_) => {}
        other => panic!("commit did not succeed: {other:?}"),
    }
    engine.read_snapshot(&tenant_scope).expect("snapshot")
}

/// One user document of the shared parity corpus, built from its fields.
fn user(id: &str, fields: &[(&str, DocumentValue)]) -> Document {
    Document::new(
        DocumentId::try_new(id).expect("valid identifier"),
        CollectionName::try_new("users").expect("valid identifier"),
        object(fields),
    )
}

/// A string-array field value.
fn strings(items: &[&str]) -> DocumentValue {
    DocumentValue::Array(
        items.iter().map(|item| DocumentValue::String((*item).to_owned())).collect(),
    )
}

/// A boolean field value.
fn boolean(value: bool) -> DocumentValue {
    DocumentValue::Boolean(value)
}

/// Commits the shared parity corpus: five users.
///
/// The corpus is chosen so every executable mapping row has observable data:
/// `note` is null on one document, a string on another, and absent from the
/// rest; `tags` is an array on two documents; `cpu-count` and `2024batch`
/// exercise the V1 caller-chosen identifier alphabet.
fn parity_corpus(engine: &Engine, tag: &str) -> ReadSnapshot {
    commit_documents(
        engine,
        tag,
        vec![
            user(
                "users/1",
                &[
                    ("name", string("ada")),
                    ("country", string("NO")),
                    ("age", integer(36)),
                    ("active", boolean(true)),
                    ("tags", strings(&["red", "blue"])),
                    ("note", DocumentValue::Null),
                    ("cpu-count", integer(8)),
                ],
            ),
            user(
                "users/2",
                &[
                    ("name", string("bob")),
                    ("country", string("SE")),
                    ("age", integer(21)),
                    ("active", boolean(true)),
                    ("tags", strings(&["blue"])),
                    ("note", string("vip")),
                    ("2024batch", string("a")),
                ],
            ),
            user(
                "users/3",
                &[
                    ("name", string("carol")),
                    ("country", string("NO")),
                    ("age", integer(44)),
                    ("active", boolean(false)),
                ],
            ),
            user(
                "users/4",
                &[
                    ("name", string("dave")),
                    ("country", string("DK")),
                    ("age", integer(17)),
                    ("active", boolean(true)),
                ],
            ),
            user(
                "users/5",
                &[
                    ("name", string("erin")),
                    ("country", string("NO")),
                    ("age", integer(25)),
                    ("active", boolean(true)),
                ],
            ),
        ],
    )
}

/// The shape of one execution vector's equivalent typed specification.
struct Typed {
    conditions: Vec<QueryCondition>,
    ordering: Vec<TypedOrdering>,
    projection: Option<Vec<&'static str>>,
    offset: u64,
    limit: Option<u32>,
}

/// The ordering shape of one typed equivalent: field, direction, and an
/// optional explicit nulls placement.
type TypedOrdering = (&'static str, OrderDirection, Option<NullsPlacement>);

/// One execution conformance vector.
struct Vector {
    name: &'static str,
    rows: &'static [&'static str],
    rql: &'static str,
    tql: &'static str,
    parameters: Vec<(&'static str, DocumentValue)>,
    /// The document identities of the expected rows, in expected order.
    expected_ids: &'static [&'static str],
    /// The expected projected pairs of the first expected row, when the
    /// vector carries a field projection.
    expected_projection: Option<Vec<(&'static str, DocumentValue)>>,
    typed: Typed,
}

fn spec_of(typed: &Typed) -> QuerySpec {
    QuerySpec::try_new(
        tenant(),
        QuerySource::try_new(CollectionName::try_new("users").expect("valid identifier"))
            .expect("user collection"),
        QueryPredicate::all(typed.conditions.clone()).expect("valid conjunction"),
        QueryOrdering::try_new(
            typed
                .ordering
                .iter()
                .map(|(field, direction, nulls)| {
                    OrderingKey::try_new_with_nulls(
                        field,
                        *direction,
                        nulls.unwrap_or(NullsPlacement::Implicit),
                    )
                    .expect("valid key")
                })
                .collect(),
        )
        .expect("valid ordering"),
        match &typed.projection {
            None => QueryProjection::whole_document(),
            Some(names) => QueryProjection::fields(names).expect("valid projection"),
        },
        QueryPaging::try_new(typed.offset, typed.limit).expect("valid paging"),
        StalenessPolicy::try_new(StalenessMode::Strict, Vec::new()).expect("valid policy"),
        deadline(),
        Cancellation::new(),
    )
    .expect("valid specification")
}

fn equals(field: &str, value: DocumentValue) -> QueryCondition {
    QueryCondition::Equals { field: field.to_owned(), value }
}

fn integer(value: i64) -> DocumentValue {
    DocumentValue::Integer(value)
}

fn string(value: &str) -> DocumentValue {
    DocumentValue::String(value.to_owned())
}

fn vectors() -> Vec<Vector> {
    let mut corpus = source_and_operator_vectors();
    corpus.extend(value_and_identifier_vectors());
    corpus.extend(array_semantics_vectors());
    corpus.extend(projection_ordering_paging_vectors());
    corpus
}

/// The source, alias, and operator vectors (mapping sections 1 and 2).
fn source_and_operator_vectors() -> Vec<Vector> {
    vec![
        // R01/R28: the bare `from` and the star projection.
        Vector {
            name: "whole-document",
            rows: &["R01", "R28"],
            rql: "from Users",
            tql: "from users",
            parameters: vec![],
            expected_ids: &["users/1", "users/2", "users/3", "users/4", "users/5"],
            expected_projection: None,
            typed: Typed {
                conditions: vec![],
                ordering: vec![],
                projection: None,
                offset: 0,
                limit: None,
            },
        },
        // R02/R03/R07: the aliased source, the bare-alias translation, and
        // the double-equals translation.
        Vector {
            name: "aliased-source-and-double-equals",
            rows: &["R02", "R03", "R07"],
            rql: "from Users u where u.Age == $age",
            tql: "from users as u where u.age = $age",
            parameters: vec![("age", integer(36))],
            expected_ids: &["users/1"],
            expected_projection: None,
            typed: Typed {
                conditions: vec![equals("age", integer(36))],
                ordering: vec![],
                projection: None,
                offset: 0,
                limit: None,
            },
        },
        // R06: the operator set over one query.
        Vector {
            name: "operator-set",
            rows: &["R06"],
            rql: "from Users where Age != 21 and Age < 44",
            tql: "from users where age != 21 and age < 44",
            parameters: vec![],
            expected_ids: &["users/1", "users/4", "users/5"],
            expected_projection: None,
            typed: Typed {
                conditions: vec![
                    QueryCondition::NotEquals { field: "age".to_owned(), value: integer(21) },
                    QueryCondition::LessThan { field: "age".to_owned(), value: integer(44) },
                ],
                ordering: vec![],
                projection: None,
                offset: 0,
                limit: None,
            },
        },
    ]
}

/// The value, parameter, comment, and identifier vectors (mapping
/// section 3).
fn value_and_identifier_vectors() -> Vec<Vector> {
    vec![
        // R15/R17/R18: literals, and one bound parameter.
        Vector {
            name: "literals-and-parameters",
            rows: &["R15", "R17", "R18"],
            rql: "from Users where Active = true and Age >= 21 and Country = $country",
            tql: "from users where active = true and age >= 21 and country = $country",
            parameters: vec![("country", string("NO"))],
            expected_ids: &["users/1", "users/5"],
            expected_projection: None,
            typed: Typed {
                conditions: vec![
                    equals("active", boolean(true)),
                    QueryCondition::GreaterOrEqual { field: "age".to_owned(), value: integer(21) },
                    equals("country", string("NO")),
                ],
                ordering: vec![],
                projection: None,
                offset: 0,
                limit: None,
            },
        },
        // R21: comments are not part of the parsed artifact.
        Vector {
            name: "comments-strip",
            rows: &["R21"],
            rql: "// Norwegians\nfrom Users where Country = 'NO' limit 1",
            tql: "// Norwegians\nfrom users where country = 'NO' limit 1",
            parameters: vec![],
            expected_ids: &["users/1"],
            expected_projection: None,
            typed: Typed {
                conditions: vec![equals("country", string("NO"))],
                ordering: vec![],
                projection: None,
                offset: 0,
                limit: Some(1),
            },
        },
        // R22: the V1 identifier alphabet — hyphenated field names lower and
        // execute; the numeric-leading spelling is finding TQL-04-F1.
        Vector {
            name: "identifier-alphabet-hyphen",
            rows: &["R22"],
            rql: "from Users where cpu-count = 8",
            tql: "from users where cpu-count = 8",
            parameters: vec![],
            expected_ids: &["users/1"],
            expected_projection: None,
            typed: Typed {
                conditions: vec![equals("cpu-count", integer(8))],
                ordering: vec![],
                projection: None,
                offset: 0,
                limit: None,
            },
        },
        Vector {
            name: "identifier-alphabet-numeric-leading-quoted",
            rows: &["R22"],
            rql: "from Users where 2024batch = 'a'",
            tql: "from users where \"2024batch\" = 'a'",
            parameters: vec![],
            expected_ids: &["users/2"],
            expected_projection: None,
            typed: Typed {
                conditions: vec![equals("2024batch", string("a"))],
                ordering: vec![],
                projection: None,
                offset: 0,
                limit: None,
            },
        },
    ]
}

/// The array-semantics vectors (mapping section 4), pinned separately
/// because finding TQL-04-F2 hangs on them.
fn array_semantics_vectors() -> Vec<Vector> {
    vec![
        // R26, finding TQL-04-F2: the implicit any-element rule is a language
        // rule the current seam does not realize — the lowered comparison
        // evaluates structurally, so a scalar literal never matches an array
        // field. The vector pins the current observable behavior.
        Vector {
            name: "implicit-array-match-today-structural",
            rows: &["R26"],
            rql: "from Users where Tags = 'red'",
            tql: "from users where tags = 'red'",
            parameters: vec![],
            expected_ids: &[],
            expected_projection: None,
            typed: Typed {
                conditions: vec![equals("tags", string("red"))],
                ordering: vec![],
                projection: None,
                offset: 0,
                limit: None,
            },
        },
        // R26 again: an array-valued parameter matches by structural
        // equality, which the seam does realize today.
        Vector {
            name: "array-parameter-structural-match",
            rows: &["R26"],
            rql: "from Users where Tags = $tags",
            tql: "from users where tags = $tags",
            parameters: vec![(
                "tags",
                DocumentValue::Array(vec![DocumentValue::String("blue".to_owned())]),
            )],
            expected_ids: &["users/2"],
            expected_projection: None,
            typed: Typed {
                conditions: vec![equals(
                    "tags",
                    DocumentValue::Array(vec![DocumentValue::String("blue".to_owned())]),
                )],
                ordering: vec![],
                projection: None,
                offset: 0,
                limit: None,
            },
        },
    ]
}

/// The projection, ordering, and paging vectors (mapping sections 5, 8, 9).
fn projection_ordering_paging_vectors() -> Vec<Vector> {
    vec![
        // R29/R36: a field projection without renaming, over a presence test.
        Vector {
            name: "projection-and-presence",
            rows: &["R29", "R36"],
            rql: "from Users where exists(Note) select Name",
            tql: "select name from users where exists(note)",
            parameters: vec![],
            expected_ids: &["users/1", "users/2"],
            expected_projection: Some(vec![("name", string("ada"))]),
            typed: Typed {
                conditions: vec![QueryCondition::Exists { field: "note".to_owned() }],
                ordering: vec![],
                projection: Some(vec!["name"]),
                offset: 0,
                limit: None,
            },
        },
        // R68: every direction spelling lowers to the same two directions.
        Vector {
            name: "order-direction-spellings",
            rows: &["R68"],
            rql: "from Users where Country = 'NO' order by Age ascending",
            tql: "from users where country = 'NO' order by age ascending",
            parameters: vec![],
            expected_ids: &["users/5", "users/1", "users/3"],
            expected_projection: None,
            typed: Typed {
                conditions: vec![equals("country", string("NO"))],
                ordering: vec![("age", OrderDirection::Ascending, None)],
                projection: None,
                offset: 0,
                limit: None,
            },
        },
        // R69: explicit null placement — `note` is a string on bob, null
        // on ada, and absent elsewhere, so `desc nulls last` puts the string
        // first and holds the whole missing-and-null group last; inside the
        // block the seam's missing-before-null order keeps ada after the
        // missing three. The typed equivalent carries the same explicit
        // placement (GAP-EVAL).
        Vector {
            name: "nulls-placement",
            rows: &["R69"],
            rql: "from Users order by Note desc nulls last, Age as long",
            tql: "from users order by note desc nulls last, age",
            parameters: vec![],
            expected_ids: &["users/2", "users/4", "users/5", "users/3", "users/1"],
            expected_projection: None,
            typed: Typed {
                conditions: vec![],
                ordering: vec![
                    ("note", OrderDirection::Descending, Some(NullsPlacement::Last)),
                    ("age", OrderDirection::Ascending, None),
                ],
                projection: None,
                offset: 0,
                limit: None,
            },
        },
        // R75/R76/R77: the paging translation in one page.
        Vector {
            name: "paging-comma-form-translated",
            rows: &["R75", "R76", "R77"],
            rql: "from Users order by Age as long desc limit 1, 2",
            tql: "from users order by age desc limit 2 offset 1",
            parameters: vec![],
            expected_ids: &["users/1", "users/5"],
            expected_projection: None,
            typed: Typed {
                conditions: vec![],
                ordering: vec![("age", OrderDirection::Descending, None)],
                projection: None,
                offset: 1,
                limit: Some(2),
            },
        },
    ]
}

/// Runs every execution vector: TQL result equals the equivalent typed
/// specification's result, the expected row identities match in order, and
/// the pinned projection of the first row is observable.
#[test]
fn rql_parity_execution_vectors_execute_equivalently() {
    let corpus = vectors();
    assert_eq!(corpus.len(), 13, "the execution corpus keeps its exact size");
    let (dir, engine) = open_engine("parity-execution");
    let store = engine.queries();
    let snapshot = parity_corpus(&engine, "execution");
    for vector in &corpus {
        let parameters =
            TqlParameters::try_new(&vector.parameters).expect("vector parameters are valid");
        let query =
            parse(GrammarVersion::CURRENT, vector.tql, &parameters).unwrap_or_else(|error| {
                panic!("vector {} parses: {} ({:?})", vector.name, vector.tql, error)
            });
        let plan = compile(&store, &query, &parameters, tenant(), deadline(), Cancellation::new())
            .unwrap_or_else(|error| {
                panic!("vector {} compiles: {} ({:?})", vector.name, vector.tql, error)
            });
        let tql_result = store.execute(&plan, &snapshot).expect("the TQL plan executes");
        let typed_spec = spec_of(&vector.typed);
        let typed_plan =
            store.compile(QueryInput::typed(typed_spec)).expect("the typed plan compiles");
        let typed_result = store.execute(&typed_plan, &snapshot).expect("the typed plan executes");
        assert_eq!(tql_result, typed_result, "vector {} matches its typed equivalent", vector.name);
        let ids: Vec<&str> =
            tql_result.rows().iter().map(|row| row.document().id().as_str()).collect();
        assert_eq!(ids, vector.expected_ids, "vector {} row identities", vector.name);
        if let Some(expected) = &vector.expected_projection {
            let row = tql_result.rows().first().expect("a projected row exists");
            let projected: Vec<(String, DocumentValue)> =
                row.projected().iter().map(|(name, value)| (name.clone(), value.clone())).collect();
            let pinned: Vec<(String, DocumentValue)> =
                expected.iter().map(|(name, value)| ((*name).to_owned(), value.clone())).collect();
            assert_eq!(projected, pinned, "vector {} projected pairs", vector.name);
        }
    }
    drop(store);
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

/// Every execution vector pins its mapping rows and its V1 evidence text,
/// and names are unique across the corpus.
#[test]
fn execution_vectors_pin_their_mapping_rows_and_v1_text() {
    use std::collections::BTreeSet;
    let mut names = BTreeSet::new();
    for vector in vectors() {
        assert!(
            vector.rows.iter().all(|row| row.starts_with('R') && row.len() == 3),
            "vector {} names mapping rows in the R## inventory",
            vector.name
        );
        assert!(!vector.rql.is_empty(), "vector {} records its V1 evidence text", vector.name);
        assert_ne!(
            vector.rql, vector.tql,
            "vector {} keeps the V1 spelling distinct from the TQL spelling",
            vector.name
        );
        assert!(names.insert(vector.name), "vector names are unique");
    }
}

/// The shared corpus keeps every observable distinction the vectors rely on:
/// one present null, one present string, three absent note fields, two
/// array-topped tag fields, and both identifier-alphabet field names.
#[test]
fn the_shared_parity_corpus_carries_its_pinned_distinctions() {
    let (dir, engine) = open_engine("parity-corpus-shape");
    let snapshot = parity_corpus(&engine, "corpus-shape");
    let documents =
        engine.snapshot_documents(&scope(&engine), &snapshot).expect("the corpus is readable");
    assert_eq!(documents.len(), 5);
    let field = |id: &str, name: &str| {
        documents.iter().find(|document| document.id().as_str() == id).and_then(|document| {
            match document.value() {
                DocumentValue::Object(fields) => fields.get(name).cloned(),
                _ => None,
            }
        })
    };
    // Present null, present string, absent on the rest: three states of one
    // field, the distinction the exists/missing and nulls-placement vectors
    // observe.
    assert_eq!(field("users/1", "note"), Some(DocumentValue::Null));
    assert_eq!(field("users/2", "note"), Some(string("vip")));
    assert_eq!(field("users/3", "note"), None);
    assert_eq!(field("users/4", "note"), None);
    assert_eq!(field("users/5", "note"), None);
    // Two array-topped tag fields, one of which contains 'red'.
    assert_eq!(
        field("users/1", "tags"),
        Some(DocumentValue::Array(vec![string("red"), string("blue")]))
    );
    assert_eq!(field("users/2", "tags"), Some(DocumentValue::Array(vec![string("blue")])));
    // The identifier-alphabet field names the R22 vectors address.
    assert_eq!(field("users/1", "cpu-count"), Some(integer(8)));
    assert_eq!(field("users/2", "2024batch"), Some(string("a")));
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}
