//! The SQL conformance vector corpus (E-4, D-54/D-78).
//!
//! Following the vector golden-vector pattern of
//! `vector_conformance_vectors.rs`: every accept and refusal class of the
//! SQL front end — statement kinds, the live-subset tag, parameter binding,
//! and every [`SqlErrorClass`] refusal — has at least one named vector,
//! pinned by the exact assertions below, and the coverage-completeness
//! checks at the end of this file fail when a refusal variant of the typed
//! error taxonomy is produced by no vector, so a coverage gap of that class
//! cannot recur silently.
//!
//! The corpus runs in CI with the rest of the integration tests and is the
//! stub the SDK conformance suite (D-13) pins: every SDK proves parity by
//! replaying these named vectors against its own typed schema builder,
//! parameterized SQL surface, and subscribe/execute split. It pins the
//! CONSTRUCTION surface (parsing, classification, binding); execution
//! semantics — predicate evaluation, FTS narrowing, ordering, paging — are
//! pinned by the in-crate corpus of `src/query/tests.rs`, because execution
//! requires a `TenantScope` and the public surface has no scope constructor
//! by design.

use terrabase::{
    MAX_SQL_TEXT_BYTES,
    query::{
        LiveSubset,
        sql::{
            SqlErrorClass, SqlParameters, SqlStatementKind, classify_sql_text, parse,
            validate_parameters,
        },
    },
    types::DocumentValue,
};

/// One conformance vector: a named front-end scenario plus the outcome the
/// seam must produce.
struct Vector {
    name: &'static str,
    scenario: Scenario,
    expected: Expected,
}

/// The front-end construction scenarios the corpus covers.
#[derive(Debug)]
enum Scenario {
    /// `parse` over one text: the admitted statement kind or the
    /// front-end refusal.
    Parse {
        /// The SQL text under test.
        text: &'static str,
    },
    /// `classify_sql_text` over one text: the live-subset tag or the
    /// front-end refusal.
    Classify {
        /// The SQL text under test.
        text: &'static str,
    },
    /// `validate_parameters` over one text with out-of-band bindings: the
    /// lowering accepts the binding set or refuses it with its typed class.
    Bind {
        /// The SQL text under test.
        text: &'static str,
        /// The name-value bindings, in declaration order.
        params: Vec<(&'static str, DocumentValue)>,
    },
    /// `SqlParameters::try_new` over one binding name: the identifier
    /// discipline accepts it or refuses it.
    BindingName {
        /// The binding name under test.
        name: &'static str,
    },
}

/// The expected outcome of one conformance vector.
#[derive(Debug)]
enum Expected {
    /// The text parses as the named statement kind.
    Kind(SqlStatementKind),
    /// The text classifies inside the live subset.
    Live,
    /// The text classifies execute-only with the named operator.
    ExecuteOnly(&'static str),
    /// The binding set validates.
    Accepted,
    /// The front end refuses with the named class.
    Refused(SqlErrorClass),
}

fn integer(value: i64) -> DocumentValue {
    DocumentValue::Integer(value)
}

fn text(value: &str) -> DocumentValue {
    DocumentValue::String(value.to_owned())
}

fn vectors() -> Vec<Vector> {
    vec![
        // ---------------------------------------------------------------
        // Parse: the admitted statement kinds (D-54 allow-list)
        // ---------------------------------------------------------------
        Vector {
            name: "parse/select-basic",
            scenario: Scenario::Parse { text: "SELECT * FROM orders" },
            expected: Expected::Kind(SqlStatementKind::Select),
        },
        Vector {
            name: "parse/select-json-placeholders",
            scenario: Scenario::Parse {
                text: "SELECT _id, body ->> '$.customer' AS customer FROM orders \
                       WHERE total >= ?1 ORDER BY _updated DESC LIMIT 25",
            },
            expected: Expected::Kind(SqlStatementKind::Select),
        },
        Vector {
            name: "parse/with-select",
            scenario: Scenario::Parse {
                text: "WITH recent AS (SELECT * FROM orders WHERE _updated > :cursor) \
                       SELECT _id FROM recent ORDER BY _id",
            },
            expected: Expected::Kind(SqlStatementKind::With),
        },
        Vector {
            name: "parse/explain-query-plan",
            scenario: Scenario::Parse {
                text: "EXPLAIN QUERY PLAN SELECT * FROM orders WHERE total > ?",
            },
            expected: Expected::Kind(SqlStatementKind::Explain),
        },
        // ---------------------------------------------------------------
        // Parse: every front-end refusal class
        // ---------------------------------------------------------------
        Vector {
            name: "parse/refuses-invalid-syntax",
            scenario: Scenario::Parse { text: "SELECT FROM" },
            expected: Expected::Refused(SqlErrorClass::ParseError),
        },
        Vector {
            name: "parse/refuses-dml-statement",
            scenario: Scenario::Parse { text: "DELETE FROM orders" },
            expected: Expected::Refused(SqlErrorClass::UnsupportedStatement),
        },
        Vector {
            name: "parse/refuses-select-into",
            scenario: Scenario::Parse { text: "SELECT * INTO stolen FROM orders" },
            expected: Expected::Refused(SqlErrorClass::ForbiddenKeyword),
        },
        Vector {
            name: "parse/refuses-volatile-function",
            scenario: Scenario::Parse { text: "SELECT random() FROM orders" },
            expected: Expected::Refused(SqlErrorClass::ForbiddenFunction),
        },
        Vector {
            name: "parse/refuses-internal-relation",
            scenario: Scenario::Parse { text: "SELECT * FROM sqlite_master" },
            expected: Expected::Refused(SqlErrorClass::ForbiddenRelation),
        },
        Vector {
            name: "parse/refuses-table-valued-function",
            scenario: Scenario::Parse { text: "SELECT * FROM json_each(body)" },
            expected: Expected::Refused(SqlErrorClass::ForbiddenTableFunction),
        },
        // ---------------------------------------------------------------
        // Classify: the launch live subset (D-78, ADR 0044 §3.2)
        // ---------------------------------------------------------------
        Vector {
            name: "classify/live-filter-order-limit",
            scenario: Scenario::Classify {
                text: "SELECT _id, total FROM orders WHERE status = 'paid' \
                       ORDER BY total DESC LIMIT 25",
            },
            expected: Expected::Live,
        },
        Vector {
            name: "classify/live-match-parameter",
            scenario: Scenario::Classify { text: "SELECT * FROM articles WHERE body MATCH :query" },
            expected: Expected::Live,
        },
        Vector {
            name: "classify/live-explains-through",
            scenario: Scenario::Classify { text: "EXPLAIN SELECT * FROM orders WHERE total > 10" },
            expected: Expected::Live,
        },
        Vector {
            name: "classify/execute-only-join",
            scenario: Scenario::Classify {
                text: "SELECT * FROM orders JOIN archive ON orders._id = archive._id",
            },
            expected: Expected::ExecuteOnly("join"),
        },
        Vector {
            name: "classify/execute-only-aggregate-function",
            scenario: Scenario::Classify { text: "SELECT count(*) FROM orders" },
            expected: Expected::ExecuteOnly("aggregate"),
        },
        Vector {
            name: "classify/execute-only-aggregate-group",
            scenario: Scenario::Classify {
                text: "SELECT status, count(*) FROM orders GROUP BY status HAVING count(*) > 1",
            },
            expected: Expected::ExecuteOnly("aggregate"),
        },
        Vector {
            name: "classify/execute-only-window",
            scenario: Scenario::Classify {
                text: "SELECT row_number() OVER (ORDER BY _id) FROM orders",
            },
            expected: Expected::ExecuteOnly("window"),
        },
        Vector {
            name: "classify/execute-only-subquery-predicate",
            scenario: Scenario::Classify {
                text: "SELECT * FROM orders WHERE _id IN (SELECT _id FROM archive)",
            },
            expected: Expected::ExecuteOnly("subquery"),
        },
        Vector {
            name: "classify/execute-only-subquery-cte",
            scenario: Scenario::Classify {
                text: "WITH recent AS (SELECT * FROM orders) SELECT * FROM recent",
            },
            expected: Expected::ExecuteOnly("subquery"),
        },
        Vector {
            name: "classify/execute-only-subquery-union",
            scenario: Scenario::Classify {
                text: "SELECT * FROM orders UNION ALL SELECT * FROM archive",
            },
            expected: Expected::ExecuteOnly("subquery"),
        },
        Vector {
            name: "classify/priority-join-over-aggregate",
            scenario: Scenario::Classify {
                text: "SELECT count(*) FROM orders JOIN archive ON orders._id = archive._id",
            },
            expected: Expected::ExecuteOnly("join"),
        },
        // ---------------------------------------------------------------
        // Bind: named, typed, out-of-band parameters (values are data)
        // ---------------------------------------------------------------
        Vector {
            name: "bind/accepts-used-parameter",
            scenario: Scenario::Bind {
                text: "SELECT * FROM users WHERE age >= :min_age",
                params: vec![("min_age", integer(30))],
            },
            expected: Expected::Accepted,
        },
        Vector {
            name: "bind/refuses-unbound-parameter",
            scenario: Scenario::Bind {
                text: "SELECT * FROM users WHERE age >= :min_age",
                params: vec![],
            },
            expected: Expected::Refused(SqlErrorClass::UnboundParameter),
        },
        Vector {
            name: "bind/refuses-unused-parameter",
            scenario: Scenario::Bind {
                text: "SELECT * FROM users",
                params: vec![("min_age", integer(30))],
            },
            expected: Expected::Refused(SqlErrorClass::UnexpectedParameter),
        },
        Vector {
            name: "bind/refuses-zero-limit",
            scenario: Scenario::Bind { text: "SELECT * FROM users LIMIT 0", params: vec![] },
            expected: Expected::Refused(SqlErrorClass::InvalidBound),
        },
        Vector {
            name: "bind/refuses-oversize-limit",
            scenario: Scenario::Bind { text: "SELECT * FROM users LIMIT 2048", params: vec![] },
            expected: Expected::Refused(SqlErrorClass::InvalidBound),
        },
        // ---------------------------------------------------------------
        // Binding names: the shared identifier discipline
        // ---------------------------------------------------------------
        Vector {
            name: "binding-name/accepts-plain-identifier",
            scenario: Scenario::BindingName { name: "cursor" },
            expected: Expected::Accepted,
        },
        Vector {
            name: "binding-name/refuses-control-character",
            scenario: Scenario::BindingName { name: "bad\u{7}name" },
            expected: Expected::Refused(SqlErrorClass::InvalidIdentifier),
        },
        // A bound string value rides as data: it can never inject a clause,
        // so even SQL-shaped text inside a value validates.
        Vector {
            name: "bind/value-cannot-inject",
            scenario: Scenario::Bind {
                text: "SELECT * FROM users WHERE name = :name",
                params: vec![("name", text("x' OR '1'='1"))],
            },
            expected: Expected::Accepted,
        },
        // A live shape the typed planner cannot express (a disjunction) is
        // not a binding problem: with no bindings to refuse, validation
        // passes and planning rides the SQL runtime path.
        Vector {
            name: "bind/live-unlowerable-defers-to-runtime",
            scenario: Scenario::Bind {
                text: "SELECT * FROM users WHERE age = 1 OR age = 2",
                params: vec![],
            },
            expected: Expected::Accepted,
        },
    ]
}

fn run(vector: &Vector) {
    match (&vector.scenario, &vector.expected) {
        (Scenario::Parse { text }, Expected::Kind(kind)) => {
            let parsed = parse(text, MAX_SQL_TEXT_BYTES)
                .unwrap_or_else(|error| panic!("vector {} refused: {error:?}", vector.name));
            assert_eq!(&parsed.kind(), kind, "vector {}", vector.name);
        }
        (Scenario::Parse { text }, Expected::Refused(class)) => {
            let error = parse(text, MAX_SQL_TEXT_BYTES).unwrap_err();
            assert_eq!(&error.class(), class, "vector {}", vector.name);
        }
        (Scenario::Classify { text }, Expected::Live) => {
            let tag = classify_sql_text(text, MAX_SQL_TEXT_BYTES)
                .unwrap_or_else(|error| panic!("vector {} refused: {error:?}", vector.name));
            assert_eq!(tag, LiveSubset::Live, "vector {}", vector.name);
        }
        (Scenario::Classify { text }, Expected::ExecuteOnly(operator)) => {
            let tag = classify_sql_text(text, MAX_SQL_TEXT_BYTES)
                .unwrap_or_else(|error| panic!("vector {} refused: {error:?}", vector.name));
            assert_eq!(tag, LiveSubset::ExecuteOnly { operator }, "vector {}", vector.name);
        }
        (Scenario::Classify { text }, Expected::Refused(class)) => {
            let error = classify_sql_text(text, MAX_SQL_TEXT_BYTES).unwrap_err();
            assert_eq!(&error.class(), class, "vector {}", vector.name);
        }
        (Scenario::Bind { text, params }, Expected::Accepted) => {
            let entries: Vec<(&str, DocumentValue)> =
                params.iter().map(|(name, value)| (*name, value.clone())).collect();
            let parameters = SqlParameters::try_new(&entries)
                .unwrap_or_else(|error| panic!("vector {} refused: {error:?}", vector.name));
            validate_parameters(text, &parameters)
                .unwrap_or_else(|error| panic!("vector {} refused: {error:?}", vector.name));
        }
        (Scenario::Bind { text, params }, Expected::Refused(class)) => {
            let entries: Vec<(&str, DocumentValue)> =
                params.iter().map(|(name, value)| (*name, value.clone())).collect();
            let parameters = SqlParameters::try_new(&entries)
                .unwrap_or_else(|error| panic!("vector {} refused: {error:?}", vector.name));
            let error = validate_parameters(text, &parameters).unwrap_err();
            assert_eq!(&error.class(), class, "vector {}", vector.name);
        }
        (Scenario::BindingName { name }, Expected::Accepted) => {
            SqlParameters::try_new(&[(name, DocumentValue::Null)])
                .unwrap_or_else(|error| panic!("vector {} refused: {error:?}", vector.name));
        }
        (Scenario::BindingName { name }, Expected::Refused(class)) => {
            let error = SqlParameters::try_new(&[(name, DocumentValue::Null)]).unwrap_err();
            assert_eq!(&error.class(), class, "vector {}", vector.name);
        }
        (scenario, expected) => {
            panic!("vector {} pairs {scenario:?} with {expected:?}", vector.name);
        }
    }
}

#[test]
fn sql_conformance_corpus_is_complete_and_exact() {
    let corpus = vectors();
    // Exact counts per family: adding or removing a vector is a deliberate
    // contract change this assertion makes audible.
    assert_eq!(corpus.iter().filter(|v| matches!(v.scenario, Scenario::Parse { .. })).count(), 10);
    assert_eq!(
        corpus.iter().filter(|v| matches!(v.scenario, Scenario::Classify { .. })).count(),
        11
    );
    assert_eq!(corpus.iter().filter(|v| matches!(v.scenario, Scenario::Bind { .. })).count(), 7);
    assert_eq!(
        corpus.iter().filter(|v| matches!(v.scenario, Scenario::BindingName { .. })).count(),
        2
    );
    assert_eq!(corpus.len(), 30, "the corpus carries exactly thirty named vectors");
    for vector in &corpus {
        run(vector);
    }
}

#[test]
fn every_sql_refusal_class_is_reached_by_the_corpus() {
    let produced: Vec<SqlErrorClass> = vectors()
        .into_iter()
        .filter_map(|vector| match (vector.scenario, vector.expected) {
            (
                Scenario::Parse { .. }
                | Scenario::Classify { .. }
                | Scenario::Bind { .. }
                | Scenario::BindingName { .. },
                Expected::Refused(class),
            ) => Some(class),
            _ => None,
        })
        .collect();
    for variant in [
        SqlErrorClass::ParseError,
        SqlErrorClass::UnsupportedStatement,
        SqlErrorClass::ForbiddenKeyword,
        SqlErrorClass::ForbiddenFunction,
        SqlErrorClass::ForbiddenRelation,
        SqlErrorClass::ForbiddenTableFunction,
        SqlErrorClass::UnboundParameter,
        SqlErrorClass::UnexpectedParameter,
        SqlErrorClass::InvalidBound,
        SqlErrorClass::InvalidIdentifier,
    ] {
        assert!(
            produced.contains(&variant),
            "no conformance vector produces {variant:?}: a refusal class cannot silently lose coverage"
        );
    }
}

#[test]
fn every_execute_only_operator_is_named_by_the_corpus() {
    let produced: Vec<&str> = vectors()
        .into_iter()
        .filter_map(|vector| match (vector.scenario, vector.expected) {
            (Scenario::Classify { .. }, Expected::ExecuteOnly(operator)) => Some(operator),
            _ => None,
        })
        .collect();
    for operator in ["join", "aggregate", "window", "subquery"] {
        assert!(
            produced.contains(&operator),
            "no conformance vector names the {operator} operator: the NotSubscribable taxonomy cannot silently lose coverage"
        );
    }
}
