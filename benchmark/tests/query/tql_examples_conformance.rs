//! Conformance of the TQL surface against `docs/tql/examples.md` (S-02).
//!
//! Every query shape in the reference is one case with its pinned outcome:
//! executed document ids where the engine serves the shape today, and the
//! exact fail-closed class where it does not. A case moves from a failure
//! pin to an execution pin only by implementing the shape — the corpus
//! fails loudly on drift either way.
//!
//! Coverage notes, not gaps in the corpus:
//!
//! - Section 11 (live queries) has no TQL text of its own — the text is
//!   identical, only the verb changes — so there is no text case to pin;
//!   the reactive seam is covered by its own suites.
//! - Section 12 (`explain`) is a request option, not a keyword; it maps to
//!   [`terrabase::query::QueryStore::explain`], covered by the in-crate
//!   explain tests.
//! - The `vector.near` / `vector.distance` spellings of section 8 are not
//!   in the `tql/1.0` registry (which carries `vector.search` /
//!   `vector.of`, gated on the unreported Vector capability): vector search
//!   through TQL is post-launch per workspace goal D-40, and the cases pin
//!   the fail-closed spelling mismatch.

use std::collections::BTreeMap;
use std::time::Duration;

use terrabase::{
    engine::{Engine, EngineConfig},
    query::tql::{self, TqlErrorClass, TqlParameters},
    types::{
        Cancellation, CollectionName, Deadline, Document, DocumentId, DocumentValue,
        ExpectedVersion, LocalDurability, TenantId,
    },
};

/// The Search-only capability set the production engine reports.
fn capabilities() -> tql::TqlCapabilities {
    tql::engine_capabilities()
}

fn string(value: &str) -> DocumentValue {
    DocumentValue::String(value.to_owned())
}

fn integer(value: i64) -> DocumentValue {
    DocumentValue::Integer(value)
}

fn parameters(entries: &[(&str, DocumentValue)]) -> TqlParameters {
    TqlParameters::try_new(entries).expect("vector parameters are valid")
}

/// One examples case: reference text plus the pinned outcome.
struct Case {
    name: &'static str,
    text: &'static str,
    parameters: TqlParameters,
    expected: Expected,
}

/// The pinned outcome of one case.
enum Expected {
    /// The query executes; the listed ids return in result order.
    Executes(Vec<&'static str>),
    /// The text parses, but lowering fails closed with the class and
    /// construct.
    LowerFails(TqlErrorClass, Option<&'static str>),
    /// Parsing fails closed with the class and construct.
    ParseFails(TqlErrorClass, Option<&'static str>),
}

fn cases() -> Vec<Case> {
    let empty = || parameters(&[]);
    vec![
        // Section 2: basics.
        Case {
            name: "section-2-bare-collection",
            text: "from todos",
            parameters: empty(),
            expected: Expected::Executes(vec!["t1", "t2"]),
        },
        Case {
            name: "section-2-limit",
            text: "from todos limit 20",
            parameters: empty(),
            expected: Expected::Executes(vec!["t1", "t2"]),
        },
        Case {
            name: "section-2-projection",
            text: "select title, done from todos",
            parameters: empty(),
            expected: Expected::Executes(vec!["t1", "t2"]),
        },
        Case {
            name: "section-2-distinct",
            text: "select distinct status from orders",
            parameters: empty(),
            expected: Expected::LowerFails(TqlErrorClass::UnsupportedForPlanning, Some("distinct")),
        },
        Case {
            name: "section-2-get-by-id",
            text: "from orders where @id = $id",
            parameters: parameters(&[("id", string("o1"))]),
            expected: Expected::LowerFails(TqlErrorClass::UnsupportedForPlanning, Some("metadata")),
        },
        // Section 3: filters.
        Case {
            name: "section-3-equality",
            text: "from orders where status = 'paid'",
            parameters: empty(),
            expected: Expected::Executes(vec!["o1", "o2"]),
        },
        Case {
            name: "section-3-conjunction",
            text: "from orders where status = $status and total >= $min",
            parameters: parameters(&[("status", string("paid")), ("min", integer(100))]),
            expected: Expected::Executes(vec!["o1"]),
        },
        Case {
            name: "section-3-in",
            text: "from orders where status in ('paid', 'shipped')",
            parameters: empty(),
            expected: Expected::Executes(vec!["o1", "o2", "o3"]),
        },
        Case {
            name: "section-3-between",
            text: "from orders where total between $lo and $hi",
            parameters: parameters(&[("lo", integer(50)), ("hi", integer(150))]),
            expected: Expected::Executes(vec!["o1"]),
        },
        Case {
            name: "section-3-negated-disjunction",
            text: "from orders where not (status = 'draft' or total = 0)",
            parameters: empty(),
            expected: Expected::LowerFails(TqlErrorClass::UnsupportedForPlanning, Some("not")),
        },
        Case {
            name: "section-3-nested-path",
            text: "from orders where customer.address.country = 'BR'",
            parameters: empty(),
            expected: Expected::LowerFails(TqlErrorClass::UnsupportedForPlanning, Some("path")),
        },
        Case {
            name: "section-3-array-membership",
            text: "from products where 'blue' in tags",
            parameters: empty(),
            // The grammar only accepts parenthesized `in (...)` lists, so
            // the reference's bare `tags` membership form fails at parse
            // time; the parenthesized array-superset form below parses.
            expected: Expected::ParseFails(TqlErrorClass::ParseError, None),
        },
        Case {
            name: "section-3-array-superset",
            text: "from products where tags all in ($required)",
            parameters: parameters(&[("required", string("blue"))]),
            expected: Expected::Executes(vec!["p2", "p3"]),
        },
        Case {
            name: "section-3-presence",
            text: "from users where exists(phone) and missing(deleted_at)",
            parameters: empty(),
            expected: Expected::Executes(vec!["u1"]),
        },
        Case {
            name: "section-3-composed-pattern",
            text: "from users where starts_with(lower(email), $prefix)",
            parameters: parameters(&[("prefix", string("a"))]),
            expected: Expected::LowerFails(TqlErrorClass::UnsupportedForPlanning, Some("call")),
        },
        // Section 4: ordering and paging.
        Case {
            name: "section-4-order-desc",
            text: "from orders order by created_at desc",
            parameters: empty(),
            expected: Expected::Executes(vec!["o4", "o3", "o1", "o2"]),
        },
        Case {
            name: "section-4-order-multi-limit",
            text: "from orders order by total desc, created_at asc limit 50",
            parameters: empty(),
            expected: Expected::Executes(vec!["o3", "o1", "o2", "o4"]),
        },
        Case {
            name: "section-4-order-nulls-last",
            text: "from orders order by shipped_at desc nulls last limit 20",
            parameters: empty(),
            expected: Expected::Executes(vec!["o1", "o2", "o3", "o4"]),
        },
        Case {
            name: "section-4-offset-past-the-end",
            text: "from orders order by created_at desc limit 20 offset 40",
            parameters: empty(),
            expected: Expected::Executes(vec![]),
        },
        // Section 5: projections.
        Case {
            name: "section-5-renaming-projection",
            text: "select title, customer.name as customer from orders",
            parameters: empty(),
            expected: Expected::LowerFails(TqlErrorClass::UnsupportedForPlanning, Some("alias")),
        },
        Case {
            name: "section-5-object-projection",
            text: "select { id: @id, total, paid: status = 'paid' } from orders",
            parameters: empty(),
            // The grammar requires `key: value` for every object member,
            // so the reference's bare-`total` shorthand fails at parse
            // time; keyed members parse (see the golden object vector).
            expected: Expected::ParseFails(TqlErrorClass::ParseError, None),
        },
        Case {
            name: "section-5-computed-projection",
            text: "select coalesce(nickname, name) as display from users",
            parameters: empty(),
            expected: Expected::LowerFails(TqlErrorClass::UnsupportedForPlanning, Some("alias")),
        },
        Case {
            name: "section-5-metadata-projection",
            text: "select @id, @revision, @last_modified from orders where @id = $id",
            parameters: parameters(&[("id", string("o1"))]),
            // The grammar carries `@change_vector`, not `@revision`: the
            // reference and the grammar disagree here, and the grammar wins
            // by failing closed at parse time.
            expected: Expected::ParseFails(TqlErrorClass::ParseError, None),
        },
        // Section 6: aggregates.
        Case {
            name: "section-6-count",
            text: "select count() from orders where status = 'paid'",
            parameters: empty(),
            expected: Expected::LowerFails(TqlErrorClass::UnsupportedForPlanning, Some("call")),
        },
        Case {
            name: "section-6-grouped-aggregation",
            text: "select status, count() as n, sum(total) as revenue from orders group by status order by revenue desc",
            parameters: empty(),
            expected: Expected::LowerFails(TqlErrorClass::UnsupportedForPlanning, Some("group by")),
        },
        Case {
            name: "section-6-grouped-averages",
            text: "select customer.country as country, avg(total) as avg_total, max(total) as biggest from orders where created_at >= $since group by customer.country",
            parameters: parameters(&[("since", string("2026-01-01"))]),
            expected: Expected::LowerFails(TqlErrorClass::UnsupportedForPlanning, Some("group by")),
        },
        // Section 7: full-text search.
        Case {
            name: "section-7-search",
            text: "from products where search(description, $q)",
            parameters: parameters(&[("q", string("bicycle"))]),
            expected: Expected::Executes(vec!["p1"]),
        },
        Case {
            name: "section-7-search-filter-rank",
            text: "from products where search(description, $q) and price < $max order by score() desc limit 10",
            parameters: parameters(&[("q", string("red")), ("max", integer(400))]),
            expected: Expected::Executes(vec!["p3", "p1"]),
        },
        Case {
            name: "section-7-search-disjunction",
            text: "from articles where search(body, $q) or search(title, $q) order by score() desc",
            parameters: parameters(&[("q", string("quick"))]),
            expected: Expected::LowerFails(TqlErrorClass::UnsupportedForPlanning, Some("or")),
        },
        // Section 8: vector search (post-launch per workspace goal D-40).
        Case {
            name: "section-8-vector-near",
            text: "from products where vector.near(embedding, $v, 10)",
            parameters: parameters(&[(
                "v",
                DocumentValue::Array(vec![DocumentValue::Number(0.5), DocumentValue::Number(0.25)]),
            )]),
            expected: Expected::ParseFails(TqlErrorClass::UnknownFunction, Some("vector.near")),
        },
        Case {
            name: "section-8-vector-filtered",
            text: "from products where vector.near(embedding, $v, 20) and category = $cat order by vector.distance(embedding, $v) limit 5",
            parameters: parameters(&[
                (
                    "v",
                    DocumentValue::Array(vec![
                        DocumentValue::Number(0.5),
                        DocumentValue::Number(0.25),
                    ]),
                ),
                ("cat", string("books")),
            ]),
            expected: Expected::ParseFails(TqlErrorClass::UnknownFunction, Some("vector.near")),
        },
        Case {
            name: "section-8-vector-projection",
            text: "select @id, name, vector.distance(embedding, $v) as distance from products where vector.near(embedding, $v, 10)",
            parameters: parameters(&[(
                "v",
                DocumentValue::Array(vec![DocumentValue::Number(0.5), DocumentValue::Number(0.25)]),
            )]),
            expected: Expected::ParseFails(TqlErrorClass::UnknownFunction, Some("vector.near")),
        },
        // Section 9: time travel (post-launch per workspace goal D-40).
        Case {
            name: "section-9-point-read",
            text: "from orders as of $ts where @id = $id",
            parameters: parameters(&[("ts", string("2026-01-01")), ("id", string("o1"))]),
            expected: Expected::ParseFails(TqlErrorClass::ParseError, None),
        },
        Case {
            name: "section-9-count-as-of",
            text: "select count() from orders as of $ts where status = 'paid'",
            parameters: parameters(&[("ts", string("2026-01-01"))]),
            expected: Expected::ParseFails(TqlErrorClass::ParseError, None),
        },
        // Section 10: relationships.
        Case {
            name: "section-10-include",
            text: "from orders include customer",
            parameters: empty(),
            expected: Expected::LowerFails(TqlErrorClass::UnsupportedForPlanning, Some("include")),
        },
        Case {
            name: "section-10-include-multi",
            text: "from orders include customer, lines.product where status = 'paid'",
            parameters: empty(),
            // The grammar takes `include` after paging, not before
            // `where`, so the reference's order fails at parse time; the
            // trailing-`include` form above parses and fails in lowering.
            expected: Expected::ParseFails(TqlErrorClass::ParseError, None),
        },
        Case {
            name: "section-10-reverse-filter",
            text: "from customers where exists(orders where status = 'open')",
            parameters: empty(),
            expected: Expected::ParseFails(TqlErrorClass::ParseError, None),
        },
        // Section 13: errors.
        Case {
            name: "section-13-truncated-predicate",
            text: "from orders where status =",
            parameters: empty(),
            expected: Expected::ParseFails(TqlErrorClass::ParseError, None),
        },
        Case {
            name: "section-13-unknown-function",
            text: "from orders where foo(status)",
            parameters: empty(),
            expected: Expected::ParseFails(TqlErrorClass::UnknownFunction, Some("foo")),
        },
        Case {
            name: "section-13-no-clock-function",
            text: "from orders where created_at > now()",
            parameters: empty(),
            expected: Expected::ParseFails(TqlErrorClass::UnknownFunction, Some("now")),
        },
    ]
}

fn object(fields: &[(&str, DocumentValue)]) -> DocumentValue {
    DocumentValue::Object(
        fields
            .iter()
            .map(|(name, value)| ((*name).to_owned(), value.clone()))
            .collect::<BTreeMap<_, _>>(),
    )
}

fn document(id: &str, collection: &str, fields: &[(&str, DocumentValue)]) -> Document {
    Document::new(
        DocumentId::try_new(id).expect("valid identifier"),
        CollectionName::try_new(collection).expect("valid identifier"),
        object(fields),
    )
}

fn seed(engine: &Engine) {
    let tenant = TenantId::try_new("tenant-a").expect("valid tenant");
    let scope = engine.scope_for(&tenant);
    let snapshot = engine.read_snapshot(&scope).expect("snapshot");
    let operation =
        terrabase::types::OperationId::try_new("seed-examples").expect("valid operation");
    let mut transaction = engine.begin(&scope, snapshot, operation).expect("begins");
    for document in [
        document(
            "t1",
            "todos",
            &[("title", string("Buy milk")), ("done", DocumentValue::Boolean(false))],
        ),
        document(
            "t2",
            "todos",
            &[("title", string("Write report")), ("done", DocumentValue::Boolean(true))],
        ),
        document(
            "o1",
            "orders",
            &[
                ("status", string("paid")),
                ("total", integer(120)),
                ("created_at", string("2026-01-02")),
                ("shipped_at", string("2026-01-05")),
                ("customer", object(&[("address", object(&[("country", string("BR"))]))])),
            ],
        ),
        document(
            "o2",
            "orders",
            &[
                ("status", string("paid")),
                ("total", integer(40)),
                ("created_at", string("2026-01-01")),
            ],
        ),
        document(
            "o3",
            "orders",
            &[
                ("status", string("shipped")),
                ("total", integer(200)),
                ("created_at", string("2026-01-03")),
            ],
        ),
        document(
            "o4",
            "orders",
            &[
                ("status", string("draft")),
                ("total", integer(0)),
                ("created_at", string("2026-01-04")),
            ],
        ),
        document(
            "p1",
            "products",
            &[
                ("description", string("a red bicycle for city rides")),
                ("price", integer(300)),
                ("tags", DocumentValue::Array(vec![string("red"), string("city")])),
            ],
        ),
        document(
            "p2",
            "products",
            &[
                ("description", string("a blue kayak for calm lakes")),
                ("price", integer(500)),
                ("tags", DocumentValue::Array(vec![string("blue")])),
            ],
        ),
        document(
            "p3",
            "products",
            &[
                ("description", string("a red helmet")),
                ("price", integer(50)),
                ("tags", DocumentValue::Array(vec![string("red"), string("blue")])),
            ],
        ),
        document(
            "u1",
            "users",
            &[
                ("phone", string("123")),
                ("email", string("Ada@Example.com")),
                ("name", string("Ada")),
            ],
        ),
        document(
            "u2",
            "users",
            &[
                ("email", string("bob@example.com")),
                ("name", string("Bob")),
                ("nickname", string("Bobby")),
                ("deleted_at", string("2026-01-01")),
            ],
        ),
        document(
            "a1",
            "articles",
            &[("body", string("the quick brown fox")), ("title", string("fox"))],
        ),
        document(
            "a2",
            "articles",
            &[("body", string("a slow turtle")), ("title", string("quick meals"))],
        ),
    ] {
        transaction.put(document, ExpectedVersion::Any).expect("stages");
    }
    match transaction.commit(LocalDurability::Synced) {
        terrabase::engine::CommitOutcome::Committed(_) => {}
        other => panic!("the examples seed did not commit: {other:?}"),
    }
}

fn deadline() -> Deadline {
    Deadline::after(Duration::from_secs(30)).expect("valid deadline")
}

fn tenant() -> TenantId {
    TenantId::try_new("tenant-a").expect("valid tenant")
}

#[test]
fn every_examples_case_parses_and_lowers_as_pinned() {
    let tenant = tenant();
    for case in cases() {
        let parsed =
            tql::parse_artifact(Some("tql/1.0"), case.text, &case.parameters, &capabilities());
        match &case.expected {
            Expected::ParseFails(class, construct) => {
                let error =
                    parsed.err().unwrap_or_else(|| panic!("case {} must fail parsing", case.name));
                assert_eq!(error.class(), *class, "case {} class drifted", case.name);
                assert_eq!(
                    error.construct(),
                    construct.as_deref(),
                    "case {} construct drifted",
                    case.name
                );
            }
            Expected::Executes(_) | Expected::LowerFails(_, _) => {
                let parsed = parsed.unwrap_or_else(|_| panic!("case {} must parse", case.name));
                let lowered = tql::lower_to_spec(
                    &parsed,
                    &case.parameters,
                    tenant.clone(),
                    deadline(),
                    Cancellation::new(),
                );
                match &case.expected {
                    Expected::LowerFails(class, construct) => {
                        let error = lowered
                            .err()
                            .unwrap_or_else(|| panic!("case {} must fail lowering", case.name));
                        assert_eq!(error.class(), *class, "case {} class drifted", case.name);
                        assert_eq!(
                            error.construct(),
                            construct.as_deref(),
                            "case {} construct drifted",
                            case.name
                        );
                    }
                    Expected::Executes(_) => {
                        // Ranked queries lower through the plan compile, not
                        // the specification entry; execution pins them below.
                        if case.text.contains("score()") {
                            let error = lowered.err().unwrap_or_else(|| {
                                panic!("case {} keeps the specification/rank split", case.name)
                            });
                            assert_eq!(
                                (error.class(), error.construct().map(str::to_owned)),
                                (TqlErrorClass::UnsupportedForPlanning, Some("score".to_owned())),
                                "case {} keeps the specification/rank split",
                                case.name
                            );
                        } else {
                            lowered.unwrap_or_else(|_| panic!("case {} must lower", case.name));
                        }
                    }
                    Expected::ParseFails(_, _) => unreachable!("handled above"),
                }
            }
        }
    }
}

#[test]
fn every_executable_examples_case_returns_its_pinned_ids() {
    let dir =
        std::env::temp_dir().join(format!("terrabase-examples-conformance-{}", std::process::id()));
    std::fs::remove_dir_all(&dir).ok();
    let engine =
        Engine::open(EngineConfig::durable(&dir).expect("durable location")).expect("engine opens");
    seed(&engine);
    let tenant = tenant();
    let scope = engine.scope_for(&tenant);
    let mut executable = 0_usize;
    for case in cases() {
        let Expected::Executes(ids) = &case.expected else { continue };
        executable += 1;
        let store = engine.queries();
        let snapshot = engine.read_snapshot(&scope).expect("snapshot");
        let parsed =
            tql::parse_artifact(Some("tql/1.0"), case.text, &case.parameters, &capabilities())
                .unwrap_or_else(|_| panic!("case {} must parse", case.name));
        let plan = tql::compile(
            &store,
            &parsed,
            &case.parameters,
            tenant.clone(),
            deadline(),
            Cancellation::new(),
        )
        .unwrap_or_else(|_| panic!("case {} must compile", case.name));
        let result = store
            .execute(&plan, &snapshot)
            .unwrap_or_else(|_| panic!("case {} must execute", case.name));
        let actual = result
            .rows()
            .iter()
            .map(|row| row.document().id().as_str().to_owned())
            .collect::<Vec<_>>();
        let expected = ids.iter().map(|id| (*id).to_owned()).collect::<Vec<_>>();
        assert_eq!(actual, expected, "case {} result drifted", case.name);
    }
    assert!(executable >= 15, "the corpus must keep its executable cases, found {executable}");
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}
