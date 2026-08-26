//! The RQL-to-TQL parity conformance corpus (ticket TQL-04).
//!
//! This corpus turns every row of `docs/tql/rql-mapping.md` into one
//! conformance vector, extending the golden-vector conventions of
//! `tql_golden_vectors.rs` from grammar coverage to parity mapping coverage.
//! A vector carries the V1 RQL text (evidence pinned to the V1 source; it is
//! never parsed by this repository's code), the mapped TQL text, and the
//! fail-closed outcome class the TQL side must produce today:
//!
//! - **Lowers** — the TQL text parses and lowers onto the W3-2 seam; the
//!   executable subset of these rows additionally proves execution
//!   equivalence with expected rows in `src/query/tql/parity_tests.rs`.
//! - **NotPlanned(construct)** — the mapping is accepted at the language
//!   level but the current seam cannot lower it exactly, so lowering fails
//!   closed with `UnsupportedForPlanning` naming the construct. This is the
//!   honest record of seam-extension work, not a grammar gap.
//! - **Gated(function)** — the predicate is in the grammar and gated on an
//!   engine capability: without the capability it fails closed with
//!   `CapabilityUnavailable`; with it, it lowers no further than the closed
//!   registry allows today.
//! - **Rejected(class)** — an accepted-difference surface: TQL fails closed
//!   by design (unknown registry function or grammar rejection).
//!
//! The corpus-completeness test at the end fails when any of the 85 mapping
//! rows has no vector, so a mapping row cannot appear without its
//! conformance vector, and the disposition counts (42 direct, 14 translated,
//! 29 accepted-difference) are re-derived from this table, matching the
//! mapping document's summary.

use std::collections::BTreeSet;
use std::time::Duration;

use terrabase_v2::query::tql::{
    self, Capability, GrammarVersion, TqlCapabilities, TqlErrorClass, TqlParameters,
};
use terrabase_v2::types::{Cancellation, Deadline, DocumentValue, TenantId};

/// One row of `docs/tql/rql-mapping.md`, in document order.
struct Row {
    id: &'static str,
    disposition: Disposition,
}

#[derive(Clone, Copy, PartialEq, Eq)]
enum Disposition {
    Direct,
    Translated,
    AcceptedDifference,
}

/// Every mapping row, in document order. The counts per disposition are
/// pinned by the corpus test against the mapping document's summary
/// (Section 12): 42 direct, 14 translated, 29 accepted-difference.
const ROWS: &[Row] = &[
    // Section 1 — query shape (`from`)
    Row { id: "R01", disposition: Disposition::Direct }, // from <collection>
    Row { id: "R02", disposition: Disposition::Direct }, // from <collection> as alias
    Row { id: "R03", disposition: Disposition::Translated }, // bare alias -> `as`
    Row { id: "R04", disposition: Disposition::AcceptedDifference }, // from index
    Row { id: "R05", disposition: Disposition::Direct }, // from @all_docs
    // Section 2 — where operators
    Row { id: "R06", disposition: Disposition::Direct }, // =,!=,<>,<,<=,>,>=
    Row { id: "R07", disposition: Disposition::Translated }, // == -> =
    Row { id: "R08", disposition: Disposition::Direct }, // and, or
    Row { id: "R09", disposition: Disposition::Direct }, // not
    Row { id: "R10", disposition: Disposition::Translated }, // and not / or not
    Row { id: "R11", disposition: Disposition::Direct }, // parentheses
    Row { id: "R12", disposition: Disposition::Direct }, // between
    Row { id: "R13", disposition: Disposition::Direct }, // in
    Row { id: "R14", disposition: Disposition::Direct }, // all in
    Row { id: "R15", disposition: Disposition::Direct }, // true/false/null
    // Section 3 — values, parameters, lexical rules
    Row { id: "R16", disposition: Disposition::Direct }, // single-quoted strings
    Row { id: "R17", disposition: Disposition::Direct }, // long/double literals
    Row { id: "R18", disposition: Disposition::Direct }, // $name parameters
    Row { id: "R19", disposition: Disposition::Translated }, // studio param prefix
    Row { id: "R20", disposition: Disposition::Translated }, // JSON parameter body
    Row { id: "R21", disposition: Disposition::Direct }, // comments
    Row { id: "R22", disposition: Disposition::Direct }, // identifier alphabet
    Row { id: "R23", disposition: Disposition::Translated }, // @-metadata paths
    // Section 4 — paths and documents
    Row { id: "R24", disposition: Disposition::Direct }, // dot paths
    Row { id: "R25", disposition: Disposition::Direct }, // array positional
    Row { id: "R26", disposition: Disposition::Direct }, // implicit any-match
    Row { id: "R27", disposition: Disposition::Direct }, // id()
    // Section 5 — projection and select
    Row { id: "R28", disposition: Disposition::Direct }, // select *
    Row { id: "R29", disposition: Disposition::Direct }, // select field as alias
    Row { id: "R30", disposition: Disposition::Translated }, // bare alias -> as
    Row { id: "R31", disposition: Disposition::Direct }, // distinct
    Row { id: "R32", disposition: Disposition::Translated }, // JS projection -> ctors
    Row { id: "R33", disposition: Disposition::AcceptedDifference }, // declare function
    Row { id: "R34", disposition: Disposition::Translated }, // load-alias projection
    // Section 6 — function registry
    Row { id: "R35", disposition: Disposition::Direct }, // id
    Row { id: "R36", disposition: Disposition::Direct }, // exists
    Row { id: "R37", disposition: Disposition::Translated }, // startsWith
    Row { id: "R38", disposition: Disposition::Translated }, // endsWith
    Row { id: "R39", disposition: Disposition::Direct }, // exact
    Row { id: "R40", disposition: Disposition::Direct }, // search (gated)
    Row { id: "R41", disposition: Disposition::Direct }, // boost (gated)
    Row { id: "R42", disposition: Disposition::AcceptedDifference }, // regex
    Row { id: "R43", disposition: Disposition::AcceptedDifference }, // lucene
    Row { id: "R44", disposition: Disposition::AcceptedDifference }, // fuzzy
    Row { id: "R45", disposition: Disposition::AcceptedDifference }, // proximity
    Row { id: "R46", disposition: Disposition::AcceptedDifference }, // moreLikeThis
    Row { id: "R47", disposition: Disposition::AcceptedDifference }, // intersect
    Row { id: "R48", disposition: Disposition::AcceptedDifference }, // cmpxchg
    Row { id: "R49", disposition: Disposition::AcceptedDifference }, // counters
    Row { id: "R50", disposition: Disposition::AcceptedDifference }, // revisions
    Row { id: "R51", disposition: Disposition::AcceptedDifference }, // array
    Row { id: "R52", disposition: Disposition::AcceptedDifference }, // highlight
    Row { id: "R53", disposition: Disposition::AcceptedDifference }, // explanations
    Row { id: "R54", disposition: Disposition::AcceptedDifference }, // timings
    Row { id: "R55", disposition: Disposition::AcceptedDifference }, // now
    Row { id: "R56", disposition: Disposition::AcceptedDifference }, // today
    Row { id: "R57", disposition: Disposition::Translated }, // when -> if
    Row { id: "R58", disposition: Disposition::AcceptedDifference }, // timeseries
    Row { id: "R59", disposition: Disposition::AcceptedDifference }, // last
    Row { id: "R60", disposition: Disposition::Direct }, // count,sum,avg,min,max
    Row { id: "R61", disposition: Disposition::Direct }, // vector.search (gated)
    Row { id: "R62", disposition: Disposition::Translated }, // embedding.* -> params
    Row { id: "R63", disposition: Disposition::Direct }, // spatial predicates (gated)
    Row { id: "R64", disposition: Disposition::Direct }, // spatial shapes (gated)
    Row { id: "R65", disposition: Disposition::Direct }, // spatial.distance (gated)
    // Section 7 — group by
    Row { id: "R66", disposition: Disposition::Direct }, // group by key
    Row { id: "R67", disposition: Disposition::AcceptedDifference }, // with <method>
    // Section 8 — order by
    Row { id: "R68", disposition: Disposition::Direct }, // asc/desc spellings
    Row { id: "R69", disposition: Disposition::Direct }, // nulls first/last
    Row { id: "R70", disposition: Disposition::AcceptedDifference }, // as string|long|...
    Row { id: "R71", disposition: Disposition::AcceptedDifference }, // random()
    Row { id: "R72", disposition: Disposition::Direct }, // score() (gated)
    Row { id: "R73", disposition: Disposition::Direct }, // spatial.distance (gated)
    Row { id: "R74", disposition: Disposition::AcceptedDifference }, // custom sorter
    // Section 9 — paging
    Row { id: "R75", disposition: Disposition::Direct }, // limit n / $p
    Row { id: "R76", disposition: Disposition::Translated }, // limit offset,size
    Row { id: "R77", disposition: Disposition::Direct }, // offset keyword
    Row { id: "R78", disposition: Disposition::AcceptedDifference }, // filter
    Row { id: "R79", disposition: Disposition::AcceptedDifference }, // filter_limit
    // Section 10 — load and include
    Row { id: "R80", disposition: Disposition::Direct }, // load path as alias
    Row { id: "R81", disposition: Disposition::Direct }, // include path
    Row { id: "R82", disposition: Disposition::AcceptedDifference }, // include counters/ts
    // Section 11 — non-read clauses
    Row { id: "R83", disposition: Disposition::AcceptedDifference }, // update
    Row { id: "R84", disposition: Disposition::AcceptedDifference }, // declare function (dup)
    Row { id: "R85", disposition: Disposition::AcceptedDifference }, // time-series sub-queries
];

/// One parity conformance vector: the V1 text (evidence), the mapped TQL
/// text under test, and the outcome class the TQL side must produce today.
struct Vector {
    name: &'static str,
    /// The mapping rows this vector proves.
    rows: &'static [&'static str],
    /// The V1 RQL spelling, as evidence. Never parsed here: V1 execution is
    /// pinned to the V1 source identity, not re-run in this CI.
    rql: &'static str,
    /// The TQL spelling under test.
    tql: &'static str,
    parameters: Vec<(&'static str, DocumentValue)>,
    expected: Expected,
}

enum Expected {
    /// Parses and lowers onto the W3-2 seam today.
    Lowers,
    /// Parses; lowering fails closed naming the construct.
    NotPlanned(&'static str),
    /// Grammar-gated predicate: `CapabilityUnavailable` without the
    /// capability, `UnsupportedForPlanning` with it.
    Gated(&'static str, Capability),
    /// Accepted-difference surface: fails closed with this class.
    Rejected(TqlErrorClass, Option<&'static str>),
}

const NONE: TqlCapabilities = TqlCapabilities::none();

fn string(value: &str) -> DocumentValue {
    DocumentValue::String(value.to_owned())
}

fn integer(value: i64) -> DocumentValue {
    DocumentValue::Integer(value)
}

fn vector(number: Vec<f64>) -> DocumentValue {
    DocumentValue::Array(number.into_iter().map(DocumentValue::Number).collect())
}

fn vectors() -> Vec<Vector> {
    vec![
        // -------------------------------------------------------------------
        // Section 1 — query shape (`from`)
        // -------------------------------------------------------------------
        Vector {
            name: "from-collection",
            rows: &["R01"],
            rql: "from orders",
            tql: "from orders",
            parameters: vec![],
            expected: Expected::Lowers,
        },
        Vector {
            name: "from-alias-as",
            rows: &["R02"],
            rql: "from Orders as o where o.Freight = 5",
            tql: "from orders as o where o.freight = 5",
            parameters: vec![],
            expected: Expected::Lowers,
        },
        Vector {
            name: "from-bare-alias-translated",
            rows: &["R03"],
            rql: "from Orders o where o.Freight = 5",
            tql: "from orders as o where o.freight = 5",
            parameters: vec![],
            expected: Expected::Lowers,
        },
        Vector {
            name: "from-index-rejected",
            rows: &["R04"],
            rql: "from index 'Orders/ByCustomer'",
            tql: "from index 'Orders/ByCustomer'",
            parameters: vec![],
            expected: Expected::Rejected(TqlErrorClass::ParseError, None),
        },
        Vector {
            name: "from-all-docs-not-planned",
            rows: &["R05"],
            rql: "from @all_docs",
            tql: "from @all_docs",
            parameters: vec![],
            expected: Expected::NotPlanned("@all_docs"),
        },
        // -------------------------------------------------------------------
        // Section 2 — where operators
        // -------------------------------------------------------------------
        Vector {
            name: "operator-set",
            rows: &["R06"],
            rql: "from orders where Freight = 5 and Freight != 6 and ShipVia <> 1 \
                  and Freight < 10 and Freight <= 10 and Freight > 1 and Freight >= 1",
            tql: "from orders where freight = 5 and freight != 6 and shipVia <> 1 \
                  and freight < 10 and freight <= 10 and freight > 1 and freight >= 1",
            parameters: vec![],
            expected: Expected::Lowers,
        },
        Vector {
            name: "double-equals-translated",
            rows: &["R07"],
            rql: "from orders where ShipVia == 1",
            tql: "from orders where shipVia = 1",
            parameters: vec![],
            expected: Expected::Lowers,
        },
        Vector {
            name: "and-composition",
            rows: &["R08"],
            rql: "from orders where ShipVia = 1 and Freight > 5",
            tql: "from orders where shipVia = 1 and freight > 5",
            parameters: vec![],
            expected: Expected::Lowers,
        },
        Vector {
            name: "or-disjunction-not-planned",
            rows: &["R08"],
            rql: "from orders where ShipVia = 1 or ShipVia = 2",
            tql: "from orders where shipVia = 1 or shipVia = 2",
            parameters: vec![],
            expected: Expected::NotPlanned("or"),
        },
        Vector {
            name: "not-negation-not-planned",
            rows: &["R09"],
            rql: "from orders where not ShipVia = 1",
            tql: "from orders where not shipVia = 1",
            parameters: vec![],
            expected: Expected::NotPlanned("not"),
        },
        Vector {
            name: "combined-and-not-not-planned",
            rows: &["R10"],
            rql: "from orders where Freight > 5 and not ShipVia = 1",
            tql: "from orders where freight > 5 and not shipVia = 1",
            parameters: vec![],
            expected: Expected::NotPlanned("not"),
        },
        Vector {
            name: "parenthesized-grouping",
            rows: &["R11"],
            rql: "from orders where (ShipVia = 1 and Freight > 5)",
            tql: "from orders where (shipVia = 1 and freight > 5)",
            parameters: vec![],
            expected: Expected::Lowers,
        },
        Vector {
            name: "between-not-planned",
            rows: &["R12"],
            rql: "from orders where Freight between 10 and 20",
            tql: "from orders where freight between 10 and 20",
            parameters: vec![],
            expected: Expected::NotPlanned("between"),
        },
        Vector {
            name: "in-membership-not-planned",
            rows: &["R13"],
            rql: "from orders where ShipVia in (1, 2, 3)",
            tql: "from orders where shipVia in (1, 2, 3)",
            parameters: vec![],
            expected: Expected::NotPlanned("in"),
        },
        Vector {
            name: "all-in-not-planned",
            rows: &["R14"],
            rql: "from orders where Tags all in ('red', 'blue')",
            tql: "from orders where tags all in ('red', 'blue')",
            parameters: vec![],
            expected: Expected::NotPlanned("in"),
        },
        Vector {
            name: "static-value-literals",
            rows: &["R15"],
            rql: "from orders where ShippedAt = null and Discontinued = false",
            tql: "from orders where shippedAt = null and discontinued = false",
            parameters: vec![],
            expected: Expected::Lowers,
        },
        // -------------------------------------------------------------------
        // Section 3 — values, parameters, lexical rules
        // -------------------------------------------------------------------
        Vector {
            name: "string-escape",
            rows: &["R16"],
            rql: "from employees where Name = 'O''Brien'",
            tql: "from employees where name = 'O''Brien'",
            parameters: vec![],
            expected: Expected::Lowers,
        },
        Vector {
            name: "numeric-literal-domains",
            rows: &["R17"],
            rql: "from employees where Age = 42 and Score = 3.5",
            tql: "from employees where age = 42 and score = 3.5",
            parameters: vec![],
            expected: Expected::Lowers,
        },
        Vector {
            name: "named-parameters",
            rows: &["R18"],
            rql: "from employees where Country = $country",
            tql: "from employees where country = $country",
            parameters: vec![("country", string("Norway"))],
            expected: Expected::Lowers,
        },
        Vector {
            name: "studio-parameter-prefix-translated",
            rows: &["R19"],
            rql: "country = Norway\nfrom employees where Country = $country",
            tql: "from employees where country = $country",
            parameters: vec![("country", string("Norway"))],
            expected: Expected::Lowers,
        },
        Vector {
            name: "json-parameter-body-translated",
            rows: &["R20"],
            rql: "from employees where Country = $country\n{\"country\":\"Norway\"}",
            tql: "from employees where country = $country",
            parameters: vec![("country", string("Norway"))],
            expected: Expected::Lowers,
        },
        Vector {
            name: "line-and-block-comments",
            rows: &["R21"],
            rql: "// active staff\nfrom employees /* full-time\nonly */ where Active = true",
            tql: "// active staff\nfrom employees /* full-time\nonly */ where active = true",
            parameters: vec![],
            expected: Expected::Lowers,
        },
        Vector {
            name: "identifier-alphabet-hyphen",
            rows: &["R22"],
            rql: "from devices where cpu-count = 8",
            tql: "from devices where cpu-count = 8",
            parameters: vec![],
            expected: Expected::Lowers,
        },
        Vector {
            name: "identifier-alphabet-numeric-leading",
            rows: &["R22"],
            // Finding TQL-04-F1 (docs/tql/rql-conformance.md): the bare
            // numeric-leading spelling that spec Section 3.2 promises fails
            // to lex today — a leading digit always starts a number, so
            // `2024batch` lexes as `2024` then `batch` and the text fails
            // closed with ParseError. The quoted spelling is the working
            // address; the bare form is pinned in the next vector until the
            // parser owner reconciles the lexer with the specification.
            rql: "from devices where 2024batch = 'a'",
            tql: "from devices where \"2024batch\" = 'a'",
            parameters: vec![],
            expected: Expected::Lowers,
        },
        Vector {
            name: "identifier-alphabet-numeric-leading-bare-fails-today",
            rows: &["R22"],
            rql: "from devices where 2024batch = 'a'",
            tql: "from devices where 2024batch = 'a'",
            parameters: vec![],
            expected: Expected::Rejected(TqlErrorClass::ParseError, None),
        },
        Vector {
            name: "metadata-pseudo-fields-not-planned",
            rows: &["R23"],
            rql: "from @all_docs where @metadata.@collection = 'orders' \
                  order by @last_modified as long desc",
            tql: "select @id, @last_modified from orders order by @last_modified desc",
            parameters: vec![],
            expected: Expected::NotPlanned("metadata"),
        },
        // -------------------------------------------------------------------
        // Section 4 — paths and documents
        // -------------------------------------------------------------------
        Vector {
            name: "dot-paths-not-planned",
            rows: &["R24"],
            rql: "from customers where Address.City = 'Oslo'",
            tql: "from customers where address.city = 'Oslo'",
            parameters: vec![],
            expected: Expected::NotPlanned("path"),
        },
        Vector {
            name: "array-positional-not-planned",
            rows: &["R25"],
            rql: "from orders where Lines[0].Quantity > 5",
            tql: "from orders where lines[0].quantity > 5",
            parameters: vec![],
            expected: Expected::NotPlanned("path"),
        },
        Vector {
            name: "implicit-array-match-lowers",
            rows: &["R26"],
            // The language rule is any-element matching (spec Section 6.2);
            // the lowered top-level comparison executes structurally today.
            // `src/query/tql/parity_tests.rs` pins the current behavior.
            rql: "from orders where Tags = 'red'",
            tql: "from orders where tags = 'red'",
            parameters: vec![],
            expected: Expected::Lowers,
        },
        Vector {
            name: "id-function-not-planned",
            rows: &["R27", "R35"],
            rql: "from orders where id() = 'orders/1'",
            tql: "from orders where id() = 'orders/1'",
            parameters: vec![],
            expected: Expected::NotPlanned("call"),
        },
        // -------------------------------------------------------------------
        // Section 5 — projection and select
        // -------------------------------------------------------------------
        Vector {
            name: "whole-document-projection",
            rows: &["R28"],
            // V1's whole-document form is the bare `from`; the star spelling
            // appears in the studio grammar as `select distinct *`.
            rql: "from orders",
            tql: "select * from orders",
            parameters: vec![],
            expected: Expected::Lowers,
        },
        Vector {
            name: "select-field-alias-not-planned",
            rows: &["R29"],
            rql: "from orders select ShipTo as dest",
            tql: "select shipTo as dest from orders",
            parameters: vec![],
            expected: Expected::NotPlanned("alias"),
        },
        Vector {
            name: "select-bare-alias-translated",
            rows: &["R30"],
            rql: "from orders select ShipTo dest",
            tql: "select shipTo as dest from orders",
            parameters: vec![],
            expected: Expected::NotPlanned("alias"),
        },
        Vector {
            name: "select-distinct-not-planned",
            rows: &["R31"],
            rql: "from orders select distinct ShipVia",
            tql: "select distinct shipVia from orders",
            parameters: vec![],
            expected: Expected::NotPlanned("distinct"),
        },
        Vector {
            name: "js-projection-typed-constructor-not-planned",
            rows: &["R32"],
            rql: "from orders select { OrderKey: id(), Buyer: this.Customer, \
                  Total: this.Quantity * 1.25 }",
            tql: "select { orderKey: id(), buyer: customer, total: quantity * 1.25 } from orders",
            parameters: vec![],
            expected: Expected::NotPlanned("object constructor"),
        },
        Vector {
            name: "declare-function-rejected",
            rows: &["R33", "R84"],
            rql: "declare function quarter(d) { return d.getMonth() / 3; }\n\
                  from orders select { q: quarter(this.ShippedAt) }",
            tql: "declare function quarter(d) { return d.getMonth() / 3; }\n\
                  from orders select { q: quarter(shippedAt) }",
            parameters: vec![],
            expected: Expected::Rejected(TqlErrorClass::ParseError, None),
        },
        Vector {
            name: "js-callback-rejected",
            rows: &["R33"],
            rql: "from orders select { Names: this.Lines.map(x => x.Name) }",
            tql: "select { names: lines.map(x => x.name) } from orders",
            parameters: vec![],
            expected: Expected::Rejected(TqlErrorClass::ParseError, None),
        },
        Vector {
            name: "load-alias-projection-not-planned",
            rows: &["R34"],
            rql: "from Orders as o load o.Customer as c select { buyer: c.Name }",
            tql: "select { buyer: c.name } from orders as o load o.customerId as c",
            parameters: vec![],
            expected: Expected::NotPlanned("load"),
        },
        // -------------------------------------------------------------------
        // Section 6 — function registry
        // -------------------------------------------------------------------
        Vector {
            name: "exists-presence",
            rows: &["R36"],
            rql: "from orders where exists(Note)",
            tql: "from orders where exists(note)",
            parameters: vec![],
            expected: Expected::Lowers,
        },
        Vector {
            name: "starts-with-translated-not-planned",
            rows: &["R37"],
            rql: "from employees where startsWith(Name, 'ad')",
            tql: "from employees where starts_with(name, 'ad')",
            parameters: vec![],
            expected: Expected::NotPlanned("call"),
        },
        Vector {
            name: "ends-with-translated-not-planned",
            rows: &["R38"],
            rql: "from employees where endsWith(Name, 'da')",
            tql: "from employees where ends_with(name, 'da')",
            parameters: vec![],
            expected: Expected::NotPlanned("call"),
        },
        Vector {
            name: "exact-modifier-not-planned",
            rows: &["R39"],
            rql: "from employees where exact(Name = 'Ada')",
            tql: "from employees where exact(name = 'Ada')",
            parameters: vec![],
            expected: Expected::NotPlanned("call"),
        },
        Vector {
            name: "search-gated",
            rows: &["R40"],
            rql: "from articles where search(Body, $terms)",
            tql: "from articles where search(body, $terms)",
            parameters: vec![("terms", string("coffee"))],
            expected: Expected::Gated("search", Capability::Search),
        },
        Vector {
            name: "boost-gated",
            rows: &["R41"],
            rql: "from articles where boost(search(Title, $terms), 3)",
            tql: "from articles where boost(search(title, $terms), 3)",
            parameters: vec![("terms", string("coffee"))],
            expected: Expected::Gated("boost", Capability::Search),
        },
        Vector {
            name: "score-order-gated",
            rows: &["R72"],
            rql: "from articles where search(Body, $terms) order by score()",
            tql: "from articles where search(body, $terms) order by score()",
            parameters: vec![("terms", string("coffee"))],
            expected: Expected::Gated("search", Capability::Search),
        },
        Vector {
            name: "regex-rejected",
            rows: &["R42"],
            rql: "from employees where regex(Name, '^ad.*')",
            tql: "from employees where regex(name, '^ad.*')",
            parameters: vec![],
            expected: Expected::Rejected(TqlErrorClass::UnknownFunction, Some("regex")),
        },
        Vector {
            name: "lucene-rejected",
            rows: &["R43"],
            rql: "from employees where lucene(Name:('ada OR bob'))",
            tql: "from employees where lucene(name, 'ada OR bob')",
            parameters: vec![],
            expected: Expected::Rejected(TqlErrorClass::UnknownFunction, Some("lucene")),
        },
        Vector {
            name: "fuzzy-rejected",
            rows: &["R44"],
            rql: "from employees where fuzzy(Name, 0.8)",
            tql: "from employees where fuzzy(name, 0.8)",
            parameters: vec![],
            expected: Expected::Rejected(TqlErrorClass::UnknownFunction, Some("fuzzy")),
        },
        Vector {
            name: "proximity-rejected",
            rows: &["R45"],
            rql: "from employees where proximity(Name, 2)",
            tql: "from employees where proximity(name, 2)",
            parameters: vec![],
            expected: Expected::Rejected(TqlErrorClass::UnknownFunction, Some("proximity")),
        },
        Vector {
            name: "more-like-this-rejected",
            rows: &["R46"],
            rql: "from articles where moreLikeThis($seed)",
            tql: "from articles where moreLikeThis($seed)",
            parameters: vec![("seed", string("roasted beans"))],
            expected: Expected::Rejected(TqlErrorClass::UnknownFunction, Some("moreLikeThis")),
        },
        Vector {
            name: "intersect-rejected",
            rows: &["R47"],
            rql: "from employees where intersect(LastName = 'a') or intersect(Age = 1)",
            tql: "from employees where intersect(lastName = 'a') or intersect(age = 1)",
            parameters: vec![],
            expected: Expected::Rejected(TqlErrorClass::UnknownFunction, Some("intersect")),
        },
        Vector {
            name: "cmpxchg-rejected",
            rows: &["R48"],
            rql: "from employees where Name = cmpxchg('key')",
            tql: "from employees where name = cmpxchg('key')",
            parameters: vec![],
            expected: Expected::Rejected(TqlErrorClass::UnknownFunction, Some("cmpxchg")),
        },
        Vector {
            name: "counters-rejected",
            rows: &["R49"],
            rql: "from employees select counters('logins')",
            tql: "select counters('logins') from employees",
            parameters: vec![],
            expected: Expected::Rejected(TqlErrorClass::UnknownFunction, Some("counters")),
        },
        Vector {
            name: "revisions-rejected",
            rows: &["R50"],
            rql: "from employees select revisions('all')",
            tql: "select revisions('all') from employees",
            parameters: vec![],
            expected: Expected::Rejected(TqlErrorClass::UnknownFunction, Some("revisions")),
        },
        Vector {
            name: "array-group-rejected",
            rows: &["R51"],
            rql: "from orders select array(Lines.Product)",
            tql: "select array(lines[].product) from orders",
            parameters: vec![],
            expected: Expected::Rejected(TqlErrorClass::UnknownFunction, Some("array")),
        },
        Vector {
            name: "highlight-rejected",
            rows: &["R52"],
            rql: "from articles select highlight(Body, 128, 2)",
            tql: "select highlight(body, 128, 2) from articles",
            parameters: vec![],
            expected: Expected::Rejected(TqlErrorClass::UnknownFunction, Some("highlight")),
        },
        Vector {
            name: "explanations-rejected",
            rows: &["R53"],
            rql: "from employees where explanations()",
            tql: "from employees where explanations()",
            parameters: vec![],
            expected: Expected::Rejected(TqlErrorClass::UnknownFunction, Some("explanations")),
        },
        Vector {
            name: "timings-rejected",
            rows: &["R54"],
            rql: "from employees where timings()",
            tql: "from employees where timings()",
            parameters: vec![],
            expected: Expected::Rejected(TqlErrorClass::UnknownFunction, Some("timings")),
        },
        Vector {
            name: "now-rejected",
            rows: &["R55"],
            rql: "from events where startsWith(OccurredAt, now())",
            tql: "from events where starts_with(occurredAt, now())",
            parameters: vec![],
            expected: Expected::Rejected(TqlErrorClass::UnknownFunction, Some("now")),
        },
        Vector {
            name: "today-rejected",
            rows: &["R56"],
            rql: "from events where OccurredAt = today()",
            tql: "from events where occurredAt = today()",
            parameters: vec![],
            expected: Expected::Rejected(TqlErrorClass::UnknownFunction, Some("today")),
        },
        Vector {
            name: "when-if-translated-not-planned",
            rows: &["R57"],
            rql: "from orders select { stage: when(this.ShippedAt = null, \
                  'pending', 'shipped') }",
            tql: "select { stage: if(shippedAt = null, 'pending', 'shipped') } from orders",
            parameters: vec![],
            expected: Expected::NotPlanned("object constructor"),
        },
        Vector {
            name: "timeseries-rejected",
            rows: &["R58", "R85"],
            rql: "from Employees select timeseries(HeartRate from HR \
                  between '2020-01-01' and '2020-02-01' group by 1h select min(), max())",
            tql: "select timeseries(heartRate) from employees",
            parameters: vec![],
            expected: Expected::Rejected(TqlErrorClass::UnknownFunction, Some("timeseries")),
        },
        Vector {
            name: "last-rejected",
            rows: &["R59"],
            // `last` is a reserved word (the `nulls last` spelling), so the
            // time-series range helper cannot even lex as a call name: the
            // rejection is the reserved-word parse error, not UnknownFunction.
            rql: "from Employees select timeseries(HR from HeartRate last(1 month) select avg())",
            tql: "select last(1) from employees",
            parameters: vec![],
            expected: Expected::Rejected(TqlErrorClass::ParseError, None),
        },
        Vector {
            name: "aggregates-not-planned",
            rows: &["R60"],
            rql: "from orders group by ShipVia as via select via, count() as n, \
                  sum(Freight) as s, avg(Freight) as a, min(Freight) as mn, \
                  max(Freight) as mx",
            tql: "select shipVia as via, count() as n, sum(freight) as s, \
                  avg(freight) as a, min(freight) as mn, max(freight) as mx \
                  from orders group by shipVia as via",
            parameters: vec![],
            expected: Expected::NotPlanned("group by"),
        },
        Vector {
            name: "vector-search-gated",
            rows: &["R61"],
            rql: "from products where vector.search(Embedding, $queryVector, 0.62)",
            tql: "from products where vector.search(embedding, $queryVector, 0.62)",
            parameters: vec![("queryVector", vector(vec![0.1, 0.2]))],
            expected: Expected::Gated("vector.search", Capability::Vector),
        },
        Vector {
            name: "embedding-bind-translated-gated",
            rows: &["R62"],
            rql: "from products where vector.search(Embedding, embedding.text($queryText))",
            tql: "from products where vector.search(embedding, $queryVector)",
            parameters: vec![("queryVector", vector(vec![0.1, 0.2]))],
            expected: Expected::Gated("vector.search", Capability::Vector),
        },
        Vector {
            name: "spatial-predicates-gated",
            rows: &["R63"],
            rql: "from venues where spatial.within(Location, spatial.circle(10, 59.9, 10.7, 'km'))",
            tql: "from venues where spatial.within(location, spatial.circle(10, 59.9, 10.7, 'km'))",
            parameters: vec![],
            expected: Expected::Gated("spatial.within", Capability::Spatial),
        },
        Vector {
            name: "spatial-shapes-gated",
            rows: &["R64"],
            rql: "from venues where spatial.intersects(Area, spatial.wkt($shape))",
            tql: "from venues where spatial.intersects(area, spatial.wkt($shape))",
            parameters: vec![("shape", string("POLYGON((10.6 59.9,10.8 60.0,10.6 59.9))"))],
            expected: Expected::Gated("spatial.intersects", Capability::Spatial),
        },
        Vector {
            name: "spatial-distance-gated",
            rows: &["R65", "R73"],
            rql: "from venues order by spatial.distance(Location, spatial.point(59.9, 10.7))",
            tql: "from venues order by spatial.distance(location, spatial.point(59.9, 10.7))",
            parameters: vec![],
            expected: Expected::Gated("spatial.distance", Capability::Spatial),
        },
        // -------------------------------------------------------------------
        // Section 7 — group by
        // -------------------------------------------------------------------
        Vector {
            name: "group-by-keys-not-planned",
            rows: &["R66"],
            rql: "from orders group by ShipVia as via select via, count() as n",
            tql: "select shipVia as via, count() as n from orders group by shipVia as via",
            parameters: vec![],
            expected: Expected::NotPlanned("group by"),
        },
        Vector {
            name: "group-by-with-rejected",
            rows: &["R67"],
            rql: "from orders group by ShipTo with customRegion select ShipTo, count()",
            tql: "select shipTo, count() from orders group by shipTo with customRegion",
            parameters: vec![],
            expected: Expected::Rejected(TqlErrorClass::ParseError, None),
        },
        // -------------------------------------------------------------------
        // Section 8 — order by
        // -------------------------------------------------------------------
        Vector {
            name: "order-direction-spellings",
            rows: &["R68"],
            rql: "from orders order by Freight asc, EmployeeId descending, ShipVia",
            tql: "from orders order by freight asc, employeeId descending, shipVia",
            parameters: vec![],
            expected: Expected::Lowers,
        },
        Vector {
            name: "nulls-placement",
            rows: &["R69"],
            rql: "from orders order by ShippedAt desc nulls last, Note asc nulls first",
            tql: "from orders order by shippedAt desc nulls last, note asc nulls first",
            parameters: vec![],
            expected: Expected::Lowers,
        },
        Vector {
            name: "order-type-hints-rejected",
            rows: &["R70"],
            rql: "from orders order by Company as string",
            tql: "from orders order by company as string",
            parameters: vec![],
            expected: Expected::Rejected(TqlErrorClass::ParseError, None),
        },
        Vector {
            name: "random-order-rejected",
            rows: &["R71"],
            rql: "from orders order by random()",
            tql: "from orders order by random()",
            parameters: vec![],
            expected: Expected::Rejected(TqlErrorClass::UnknownFunction, Some("random")),
        },
        Vector {
            name: "custom-sorter-rejected",
            rows: &["R74"],
            rql: "from orders order by Company as CustomSorterName",
            tql: "from orders order by company as CustomSorterName",
            parameters: vec![],
            expected: Expected::Rejected(TqlErrorClass::ParseError, None),
        },
        // -------------------------------------------------------------------
        // Section 9 — paging
        // -------------------------------------------------------------------
        Vector {
            name: "limit-bound-parameter",
            rows: &["R75"],
            rql: "from orders limit $pageSize",
            tql: "from orders limit $pageSize",
            parameters: vec![("pageSize", integer(20))],
            expected: Expected::Lowers,
        },
        Vector {
            name: "limit-comma-form-translated",
            rows: &["R76"],
            rql: "from orders order by Freight limit 40, 20",
            tql: "from orders order by freight limit 20 offset 40",
            parameters: vec![],
            expected: Expected::Lowers,
        },
        Vector {
            name: "offset-keyword",
            rows: &["R77"],
            rql: "from orders order by Freight limit 20 offset 40",
            tql: "from orders order by freight limit 20 offset 40",
            parameters: vec![],
            expected: Expected::Lowers,
        },
        Vector {
            name: "filter-clause-rejected",
            rows: &["R78"],
            rql: "from orders where Freight > 5 filter Note != null",
            tql: "from orders where freight > 5 filter note != null",
            parameters: vec![],
            expected: Expected::Rejected(TqlErrorClass::ParseError, None),
        },
        Vector {
            name: "filter-limit-rejected",
            rows: &["R79"],
            rql: "from orders where Freight > 5 filter_limit 300",
            tql: "from orders where freight > 5 filter_limit 300",
            parameters: vec![],
            expected: Expected::Rejected(TqlErrorClass::ParseError, None),
        },
        // -------------------------------------------------------------------
        // Section 10 — load and include
        // -------------------------------------------------------------------
        Vector {
            name: "load-aliases-not-planned",
            rows: &["R80"],
            rql: "from Orders as o load o.Customer as c where c.Region = 'EMEA'",
            tql: "from orders as o load o.customerId as c where c.region = 'EMEA'",
            parameters: vec![],
            expected: Expected::NotPlanned("load"),
        },
        Vector {
            name: "include-paths-not-planned",
            rows: &["R81"],
            rql: "from orders include Customer, Warehouse",
            tql: "from orders include customer, warehouse",
            parameters: vec![],
            expected: Expected::NotPlanned("include"),
        },
        Vector {
            name: "include-family-rejected",
            rows: &["R82"],
            rql: "from employees include counters('logins')",
            tql: "from employees include counters('logins')",
            parameters: vec![],
            expected: Expected::Rejected(TqlErrorClass::ParseError, None),
        },
        // -------------------------------------------------------------------
        // Section 11 — non-read clauses
        // -------------------------------------------------------------------
        Vector {
            name: "update-clause-rejected",
            rows: &["R83"],
            rql: "from orders where Freight > 100 update { this.Archived = true }",
            tql: "from orders where freight > 100 update { archived = true }",
            parameters: vec![],
            expected: Expected::Rejected(TqlErrorClass::ParseError, None),
        },
    ]
}

fn tenant() -> TenantId {
    TenantId::try_new("tenant-a").expect("valid tenant")
}

fn deadline() -> Deadline {
    Deadline::after(Duration::from_secs(30)).expect("valid deadline")
}

/// Runs every vector against the public parse and lowering entries and
/// asserts the fail-closed outcome class the corpus records.
#[test]
fn rql_parity_vectors_hold_their_recorded_outcomes() {
    let corpus = vectors();
    assert_eq!(corpus.len(), 85, "the parity corpus keeps its exact size");
    for vector in &corpus {
        assert!(!vector.rql.is_empty(), "vector {} records its V1 evidence text", vector.name);
        // A translated mapping must spell the capability differently in TQL;
        // a direct mapping may keep the identical spelling.
        let translated = vector.rows.iter().any(|row| {
            ROWS.iter()
                .find(|known| known.id == *row)
                .is_some_and(|known| known.disposition == Disposition::Translated)
        });
        if translated {
            assert_ne!(
                vector.rql, vector.tql,
                "vector {} proves a translated surface, so the texts differ",
                vector.name
            );
        }
        let parameters =
            TqlParameters::try_new(&vector.parameters).expect("vector parameters are valid");
        let parse = |capabilities: &TqlCapabilities| {
            tql::parse_with(GrammarVersion::CURRENT, vector.tql, &parameters, capabilities)
        };
        let lower = |query: &terrabase_v2::query::tql::TqlQuery| {
            tql::lower_to_spec(query, &parameters, tenant(), deadline(), Cancellation::new())
        };
        match vector.expected {
            Expected::Lowers => {
                let query = parse(&NONE).unwrap_or_else(|error| {
                    panic!("vector {} must parse: {} ({:?})", vector.name, vector.tql, error)
                });
                lower(&query).unwrap_or_else(|error| {
                    panic!("vector {} must lower: {} ({:?})", vector.name, vector.tql, error)
                });
            }
            Expected::NotPlanned(construct) => {
                let query = parse(&NONE)
                    .unwrap_or_else(|error| panic!("vector {} parses: {error:?}", vector.name));
                let error = lower(&query)
                    .expect_err("the construct must fail closed at the seam, not degrade");
                assert_eq!(
                    error.class(),
                    TqlErrorClass::UnsupportedForPlanning,
                    "vector {} lowering class",
                    vector.name
                );
                assert_eq!(error.construct(), Some(construct), "vector {} construct", vector.name);
            }
            Expected::Gated(function, capability) => {
                let error = parse(&NONE)
                    .expect_err("a gated predicate fails closed without the capability");
                assert_eq!(
                    error.class(),
                    TqlErrorClass::CapabilityUnavailable,
                    "vector {} ungated class",
                    vector.name
                );
                assert_eq!(error.construct(), Some(function), "vector {} gated name", vector.name);
                let ready = TqlCapabilities::none().with(capability);
                let query = parse(&ready).unwrap_or_else(|error| {
                    panic!("vector {} parses with the capability: {error:?}", vector.name)
                });
                let lowered = lower(&query);
                assert_eq!(
                    lowered.expect_err("the gated predicate stays beyond the seam").class(),
                    TqlErrorClass::UnsupportedForPlanning,
                    "vector {} with-capability lowering",
                    vector.name
                );
            }
            Expected::Rejected(class, construct) => {
                let error = parse(&NONE).expect_err("the accepted-difference surface fails closed");
                assert_eq!(error.class(), class, "vector {} rejection class", vector.name);
                if let Some(expected) = construct {
                    assert_eq!(
                        error.construct(),
                        Some(expected),
                        "vector {} construct",
                        vector.name
                    );
                }
            }
        }
    }
}

/// Proves corpus completeness: every row of the mapping document has at
/// least one vector, and the disposition counts stay those the mapping
/// document's summary records.
#[test]
fn every_rql_mapping_row_has_a_conformance_vector() {
    let mut covered: BTreeSet<&str> = BTreeSet::new();
    for vector in vectors() {
        for row in vector.rows {
            covered.insert(*row);
        }
    }
    let known: BTreeSet<&str> = ROWS.iter().map(|row| row.id).collect();
    let missing: Vec<&str> = known.difference(&covered).copied().collect();
    assert!(missing.is_empty(), "mapping rows without a conformance vector: {missing:?}");
    let unknown: Vec<&str> = covered.difference(&known).copied().collect();
    assert!(unknown.is_empty(), "vectors name unknown mapping rows: {unknown:?}");
    // The counts re-derive the mapping document's Section 12 summary from
    // this committed artifact: 42 direct, 14 translated, 29 accepted
    // difference, 85 rows in total.
    let direct = ROWS.iter().filter(|row| row.disposition == Disposition::Direct).count();
    let translated = ROWS.iter().filter(|row| row.disposition == Disposition::Translated).count();
    let accepted =
        ROWS.iter().filter(|row| row.disposition == Disposition::AcceptedDifference).count();
    assert_eq!(ROWS.len(), 85, "the mapping row inventory stays complete");
    assert_eq!(direct, 42, "direct rows");
    assert_eq!(translated, 14, "translated rows");
    assert_eq!(accepted, 29, "accepted-difference rows");
}
