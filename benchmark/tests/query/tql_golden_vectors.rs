//! The TQL golden query vector corpus (ticket TQL-02).
//!
//! Every construct of the accepted `tql/1.0` grammar
//! (`docs/tql/spec.md`, Section 10) has at least one vector: 45 parse vectors
//! and 30 error vectors, pinned by the exact-count assertions below. The
//! registry-completeness test at the end of this file iterates every name of
//! the closed function registry and fails when no vector invokes it, so a
//! coverage gap of that class cannot recur silently. A parse vector pins the
//! typed AST through the canonical `TqlQuery::outline()` rendering; an error
//! vector pins the fail-closed class (and construct, where the class names
//! one). The corpus runs in CI with the rest of the integration tests.

use std::collections::BTreeSet;

use terrabase::query::tql::{
    self, Capability, Expression, ExpressionKind, GrammarVersion, ProjectionKind, TqlCapabilities,
    TqlErrorClass, TqlParameters, TqlQuery,
};
use terrabase::types::DocumentValue;

/// One golden vector: parse input triple plus the expected outcome.
struct Vector {
    name: &'static str,
    /// `None` submits no version to the artifact entry (the missing-version
    /// vector); `Some(text)` declares it and is also how unknown versions are
    /// probed.
    version: Option<&'static str>,
    text: &'static str,
    parameters: Vec<(&'static str, DocumentValue)>,
    capabilities: TqlCapabilities,
    expected: Expected,
}

enum Expected {
    /// The canonical outline of the parsed AST.
    Outline(&'static str),
    /// The fail-closed error class and, where applicable, the offending
    /// construct.
    Error(TqlErrorClass, Option<&'static str>),
}

const NONE: TqlCapabilities = TqlCapabilities::none();

fn string(value: &str) -> DocumentValue {
    DocumentValue::String(value.to_owned())
}

fn integer(value: i64) -> DocumentValue {
    DocumentValue::Integer(value)
}

fn vectors() -> Vec<Vector> {
    vec![
        // -------------------------------------------------------------------
        // Clause inventory
        // -------------------------------------------------------------------
        Vector {
            name: "quick-form",
            version: Some("tql/1.0"),
            text: "from orders limit 10",
            parameters: vec![],
            capabilities: NONE,
            expected: Expected::Outline(
                "(query tql/1.0 (select plain *) (from orders) (load) (where -) (group) (order) \
                 (limit 10) (offset -) (include))",
            ),
        },
        Vector {
            name: "select-star",
            version: Some("tql/1.0"),
            text: "select * from users",
            parameters: vec![],
            capabilities: NONE,
            expected: Expected::Outline(
                "(query tql/1.0 (select plain *) (from users) (load) (where -) (group) (order) \
                 (limit -) (offset -) (include))",
            ),
        },
        Vector {
            name: "select-distinct",
            version: Some("tql/1.0"),
            text: "select distinct name from users",
            parameters: vec![],
            capabilities: NONE,
            expected: Expected::Outline(
                "(query tql/1.0 (select distinct name) (from users) (load) (where -) (group) \
                 (order) (limit -) (offset -) (include))",
            ),
        },
        Vector {
            name: "select-items-with-alias",
            version: Some("tql/1.0"),
            text: "select name, address.city as city from customers",
            parameters: vec![],
            capabilities: NONE,
            expected: Expected::Outline(
                "(query tql/1.0 (select plain name;address.city as city) (from customers) (load) \
                 (where -) (group) (order) (limit -) (offset -) (include))",
            ),
        },
        Vector {
            name: "object-constructor-projection",
            version: Some("tql/1.0"),
            text: "select { id: id(), buyer: c.name, lines: { sku: lines[].sku }, \
                   stage: if(shipment = null, 'pending', 'shipped') } \
                   from orders as o load o.customerId as c",
            parameters: vec![],
            capabilities: NONE,
            expected: Expected::Outline(
                "(query tql/1.0 (select plain {id:id(),buyer:c.name,lines:{sku:lines[].sku},\
                 stage:if((= shipment null),str:'pending',str:'shipped')}) (from orders as o) \
                 (load o.customerId as c) (where -) (group) (order) (limit -) (offset -) \
                 (include))",
            ),
        },
        Vector {
            name: "from-alias",
            version: Some("tql/1.0"),
            text: "from Orders as o where o.active = true",
            parameters: vec![],
            capabilities: NONE,
            expected: Expected::Outline(
                "(query tql/1.0 (select plain *) (from Orders as o) (load) \
                 (where (= o.active true)) (group) (order) (limit -) (offset -) (include))",
            ),
        },
        Vector {
            name: "from-quoted-collection",
            version: Some("tql/1.0"),
            text: "from \"Orders/Archived\" where active = true",
            parameters: vec![],
            capabilities: NONE,
            expected: Expected::Outline(
                "(query tql/1.0 (select plain *) (from Orders/Archived) (load) \
                 (where (= active true)) (group) (order) (limit -) (offset -) (include))",
            ),
        },
        Vector {
            name: "from-all-docs-with-metadata",
            version: Some("tql/1.0"),
            text: "select @id, @collection from @all_docs \
                   where @collection = 'orders' and @id = $id",
            parameters: vec![("id", string("orders/1"))],
            capabilities: NONE,
            expected: Expected::Outline(
                "(query tql/1.0 (select plain @id;@collection) (from @all_docs) (load) \
                 (where (and (= @collection str:'orders') (= @id $id))) (group) (order) \
                 (limit -) (offset -) (include))",
            ),
        },
        Vector {
            name: "load-multiple-aliases",
            version: Some("tql/1.0"),
            text: "from orders as o load o.customerId as c, o.warehouseId as w \
                   where c.region = 'EMEA' and w.active = true",
            parameters: vec![],
            capabilities: NONE,
            expected: Expected::Outline(
                "(query tql/1.0 (select plain *) (from orders as o) \
                 (load o.customerId as c;o.warehouseId as w) \
                 (where (and (= c.region str:'EMEA') (= w.active true))) (group) (order) \
                 (limit -) (offset -) (include))",
            ),
        },
        // -------------------------------------------------------------------
        // Expression system
        // -------------------------------------------------------------------
        Vector {
            name: "comparison-operators",
            version: Some("tql/1.0"),
            text: "from t where a = 1 and b != 2 and c <> 3 and d < 4 and e <= 5 and f > 6 \
                   and g >= 7",
            parameters: vec![],
            capabilities: NONE,
            expected: Expected::Outline(
                "(query tql/1.0 (select plain *) (from t) (load) (where (and (= a int:1) \
                 (!= b int:2) (!= c int:3) (< d int:4) (<= e int:5) (> f int:6) (>= g int:7))) \
                 (group) (order) (limit -) (offset -) (include))",
            ),
        },
        Vector {
            name: "between",
            version: Some("tql/1.0"),
            text: "from t where a between 1 and 2",
            parameters: vec![],
            capabilities: NONE,
            expected: Expected::Outline(
                "(query tql/1.0 (select plain *) (from t) (load) (where (between a int:1 int:2)) \
                 (group) (order) (limit -) (offset -) (include))",
            ),
        },
        Vector {
            name: "between-binds-its-inner-and",
            version: Some("tql/1.0"),
            text: "from t where a between 1 and 2 and b = 3",
            parameters: vec![],
            capabilities: NONE,
            expected: Expected::Outline(
                "(query tql/1.0 (select plain *) (from t) (load) \
                 (where (and (between a int:1 int:2) (= b int:3))) (group) (order) (limit -) \
                 (offset -) (include))",
            ),
        },
        Vector {
            name: "in-any",
            version: Some("tql/1.0"),
            text: "from t where a in (1, 2, 3)",
            parameters: vec![],
            capabilities: NONE,
            expected: Expected::Outline(
                "(query tql/1.0 (select plain *) (from t) (load) (where (in-any a int:1 int:2 \
                 int:3)) (group) (order) (limit -) (offset -) (include))",
            ),
        },
        Vector {
            name: "in-empty-list",
            version: Some("tql/1.0"),
            text: "from t where a in ()",
            parameters: vec![],
            capabilities: NONE,
            expected: Expected::Outline(
                "(query tql/1.0 (select plain *) (from t) (load) (where (in-any a)) (group) \
                 (order) (limit -) (offset -) (include))",
            ),
        },
        Vector {
            name: "all-in",
            version: Some("tql/1.0"),
            text: "from t where tags all in ('a', 'b')",
            parameters: vec![],
            capabilities: NONE,
            expected: Expected::Outline(
                "(query tql/1.0 (select plain *) (from t) (load) \
                 (where (in-all tags str:'a' str:'b')) (group) (order) (limit -) (offset -) \
                 (include))",
            ),
        },
        Vector {
            name: "and-binds-tighter-than-or",
            version: Some("tql/1.0"),
            text: "from t where a = 1 or b = 2 and c = 3",
            parameters: vec![],
            capabilities: NONE,
            expected: Expected::Outline(
                "(query tql/1.0 (select plain *) (from t) (load) \
                 (where (or (= a int:1) (and (= b int:2) (= c int:3)))) (group) (order) \
                 (limit -) (offset -) (include))",
            ),
        },
        Vector {
            name: "not",
            version: Some("tql/1.0"),
            text: "from t where not a = 1",
            parameters: vec![],
            capabilities: NONE,
            expected: Expected::Outline(
                "(query tql/1.0 (select plain *) (from t) (load) (where (not (= a int:1))) \
                 (group) (order) (limit -) (offset -) (include))",
            ),
        },
        Vector {
            name: "arithmetic-precedence",
            version: Some("tql/1.0"),
            text: "from t where a + 1 * b - c / d % e = 2",
            parameters: vec![],
            capabilities: NONE,
            expected: Expected::Outline(
                "(query tql/1.0 (select plain *) (from t) (load) \
                 (where (= (- (+ a (* int:1 b)) (% (/ c d) e)) int:2)) (group) (order) \
                 (limit -) (offset -) (include))",
            ),
        },
        Vector {
            name: "unary-sign-is-an-operator",
            version: Some("tql/1.0"),
            text: "from t where a = -1.5e2 and b = +3",
            parameters: vec![],
            capabilities: NONE,
            expected: Expected::Outline(
                "(query tql/1.0 (select plain *) (from t) (load) \
                 (where (and (= a (- num:150)) (= b (+ int:3)))) (group) (order) (limit -) \
                 (offset -) (include))",
            ),
        },
        Vector {
            name: "parenthesized-primary",
            version: Some("tql/1.0"),
            text: "from t where (a or b) and c",
            parameters: vec![],
            capabilities: NONE,
            expected: Expected::Outline(
                "(query tql/1.0 (select plain *) (from t) (load) (where (and (or a b) c)) \
                 (group) (order) (limit -) (offset -) (include))",
            ),
        },
        Vector {
            name: "literal-forms",
            version: Some("tql/1.0"),
            text: "from t where a = 'it''s' and b = 42 and c = 3.14 and d = 1e3 and e = true \
                   and f = null",
            parameters: vec![],
            capabilities: NONE,
            expected: Expected::Outline(
                "(query tql/1.0 (select plain *) (from t) (load) \
                 (where (and (= a str:'it''s') (= b int:42) (= c num:3.14) (= d num:1000) \
                 (= e true) (= f null))) (group) (order) (limit -) (offset -) (include))",
            ),
        },
        Vector {
            name: "parameters-in-expressions-and-page-bounds",
            version: Some("tql/1.0"),
            text: "from t where a = $x and b in ($y, 2) limit $page offset $skip",
            parameters: vec![
                ("x", integer(1)),
                ("y", integer(1)),
                ("page", integer(5)),
                ("skip", integer(10)),
            ],
            capabilities: NONE,
            expected: Expected::Outline(
                "(query tql/1.0 (select plain *) (from t) (load) \
                 (where (and (= a $x) (in-any b $y int:2))) (group) (order) (limit $page) \
                 (offset $skip) (include))",
            ),
        },
        // -------------------------------------------------------------------
        // Document-native constructs
        // -------------------------------------------------------------------
        Vector {
            name: "nested-and-array-paths",
            version: Some("tql/1.0"),
            text: "select shipment.history[0].warehouse, items[].qty, items[2], a.b.c from t",
            parameters: vec![],
            capabilities: NONE,
            expected: Expected::Outline(
                "(query tql/1.0 (select plain shipment.history[0].warehouse;items[].qty;\
                 items[2];a.b.c) (from t) (load) (where -) (group) (order) (limit -) (offset -) \
                 (include))",
            ),
        },
        Vector {
            name: "quoted-dynamic-fields",
            version: Some("tql/1.0"),
            text: "select \"properties\".\"Screen Size\" as screen from devices \
                   where \"properties\".\"Screen Size\" between 6 and 7",
            parameters: vec![],
            capabilities: NONE,
            expected: Expected::Outline(
                "(query tql/1.0 (select plain properties.Screen Size as screen) \
                 (from devices) (load) \
                 (where (between properties.Screen Size int:6 int:7)) (group) (order) \
                 (limit -) (offset -) (include))",
            ),
        },
        Vector {
            name: "metadata-pseudo-fields",
            version: Some("tql/1.0"),
            text: "select @id, @collection, @last_modified, @change_vector, @metadata.flags \
                   from t order by @last_modified desc",
            parameters: vec![],
            capabilities: NONE,
            expected: Expected::Outline(
                "(query tql/1.0 (select plain \
                 @id;@collection;@last_modified;@change_vector;@metadata.flags) (from t) (load) \
                 (where -) (group) (order @last_modified desc) (limit -) (offset -) (include))",
            ),
        },
        // -------------------------------------------------------------------
        // Function registry
        // -------------------------------------------------------------------
        Vector {
            name: "string-functions",
            version: Some("tql/1.0"),
            text: "select len(name), lower(a), upper(b), concat(a, b, c) from t \
                   where contains(name, 'x') and starts_with(name, 'y') \
                   and ends_with(name, 'z')",
            parameters: vec![],
            capabilities: NONE,
            expected: Expected::Outline(
                "(query tql/1.0 (select plain len(name);lower(a);upper(b);concat(a,b,c)) (from t) \
                 (load) (where (and contains(name,str:'x') starts_with(name,str:'y') \
                 ends_with(name,str:'z'))) (group) (order) (limit -) (offset -) (include))",
            ),
        },
        Vector {
            name: "typed-shaping-functions",
            version: Some("tql/1.0"),
            text: "select { has: exists(f), gap: missing(g), pick: if(a = 1, b, c), \
                   contact: coalesce(c.email, c.phone) } from t",
            parameters: vec![],
            capabilities: NONE,
            expected: Expected::Outline(
                "(query tql/1.0 (select plain {has:exists(f),gap:missing(g),\
                 pick:if((= a int:1),b,c),contact:coalesce(c.email,c.phone)}) (from t) (load) \
                 (where -) (group) (order) (limit -) (offset -) (include))",
            ),
        },
        Vector {
            name: "id-function-forms",
            version: Some("tql/1.0"),
            text: "select id(), id(o) from orders as o where id() = $id",
            parameters: vec![("id", string("orders/1"))],
            capabilities: NONE,
            expected: Expected::Outline(
                "(query tql/1.0 (select plain id();id(o)) (from orders as o) (load) \
                 (where (= id() $id)) (group) (order) (limit -) (offset -) (include))",
            ),
        },
        Vector {
            name: "per-document-aggregates",
            version: Some("tql/1.0"),
            text: "select { volume: sum(lines[].qty), n: count(lines[].sku) } from orders \
                   where sum(lines[].qty) > 10",
            parameters: vec![],
            capabilities: NONE,
            expected: Expected::Outline(
                "(query tql/1.0 (select plain {volume:sum(lines[].qty),n:count(lines[].sku)}) \
                 (from orders) (load) (where (> sum(lines[].qty) int:10)) (group) (order) \
                 (limit -) (offset -) (include))",
            ),
        },
        Vector {
            name: "group-by-with-aggregates-and-alias-ordering",
            version: Some("tql/1.0"),
            text: "select c.region as region, count() as orders, sum(o.total) as revenue \
                   from orders as o load o.customerId as c group by c.region \
                   order by revenue desc limit 10",
            parameters: vec![],
            capabilities: NONE,
            expected: Expected::Outline(
                "(query tql/1.0 (select plain c.region as region;count() as orders;\
                 sum(o.total) as revenue) (from orders as o) (load o.customerId as c) (where -) \
                 (group c.region) (order revenue desc) (limit 10) (offset -) (include))",
            ),
        },
        Vector {
            name: "whole-query-aggregates",
            version: Some("tql/1.0"),
            text: "select count(), sum(total), avg(total), min(total), max(total) from orders",
            parameters: vec![],
            capabilities: NONE,
            expected: Expected::Outline(
                "(query tql/1.0 (select plain count();sum(total);avg(total);min(total);\
                 max(total)) (from orders) (load) (where -) (group) (order) (limit -) (offset -) \
                 (include))",
            ),
        },
        Vector {
            name: "group-by-multiple-keys",
            version: Some("tql/1.0"),
            text: "select region, warehouse, count() from orders group by region, warehouse",
            parameters: vec![],
            capabilities: NONE,
            expected: Expected::Outline(
                "(query tql/1.0 (select plain region;warehouse;count()) (from orders) (load) \
                 (where -) (group region;warehouse) (order) (limit -) (offset -) (include))",
            ),
        },
        Vector {
            name: "order-direction-and-nulls-spellings",
            version: Some("tql/1.0"),
            text: "from t order by a asc, b ascending, c desc, d descending, e, \
                   f nulls first, g desc nulls last",
            parameters: vec![],
            capabilities: NONE,
            expected: Expected::Outline(
                "(query tql/1.0 (select plain *) (from t) (load) (where -) (group) \
                 (order a asc;b asc;c desc;d desc;e;f nulls first;g desc nulls last) \
                 (limit -) (offset -) (include))",
            ),
        },
        Vector {
            name: "exact-predicate-modifier",
            version: Some("tql/1.0"),
            text: "from t where exact(name = 'Ada')",
            parameters: vec![],
            capabilities: NONE,
            expected: Expected::Outline(
                "(query tql/1.0 (select plain *) (from t) (load) \
                 (where exact((= name str:'Ada'))) (group) (order) (limit -) (offset -) \
                 (include))",
            ),
        },
        // -------------------------------------------------------------------
        // Lexical conventions
        // -------------------------------------------------------------------
        Vector {
            name: "comments-are-not-part-of-the-artifact",
            version: Some("tql/1.0"),
            text: "// lead\nfrom t /* mid\nblock */ where a = 1 // tail",
            parameters: vec![],
            capabilities: NONE,
            expected: Expected::Outline(
                "(query tql/1.0 (select plain *) (from t) (load) (where (= a int:1)) (group) \
                 (order) (limit -) (offset -) (include))",
            ),
        },
        Vector {
            name: "keywords-are-case-insensitive",
            version: Some("tql/1.0"),
            text: "SELECT DISTINCT name FROM users AS u WHERE u.a = 1 \
                   ORDER BY u.a DESC NULLS LAST LIMIT 2 OFFSET 1",
            parameters: vec![],
            capabilities: NONE,
            expected: Expected::Outline(
                "(query tql/1.0 (select distinct name) (from users as u) (load) \
                 (where (= u.a int:1)) (group) (order u.a desc nulls last) (limit 2) (offset 1) \
                 (include))",
            ),
        },
        Vector {
            name: "number-literal-domains",
            version: Some("tql/1.0"),
            text: "from t where a = 0.5 and b = 1e10 and c = 5E-3 \
                   and d = 123456789012345678 and e = 99999999999999999999",
            parameters: vec![],
            capabilities: NONE,
            expected: Expected::Outline(
                "(query tql/1.0 (select plain *) (from t) (load) \
                 (where (and (= a num:0.5) (= b num:10000000000) (= c num:0.005) \
                 (= d int:123456789012345678) \
                 (= e num:100000000000000000000))) (group) (order) (limit -) (offset -) \
                 (include))",
            ),
        },
        // -------------------------------------------------------------------
        // Capability-gated predicates with the capability reported
        // -------------------------------------------------------------------
        Vector {
            name: "search-predicates-with-capability",
            version: Some("tql/1.0"),
            text: "from p where boost(search(body, $terms, and), 3) or search(title, $terms) \
                   order by score() desc limit 10",
            parameters: vec![("terms", string("coffee"))],
            capabilities: TqlCapabilities::none().with(Capability::Search),
            expected: Expected::Outline(
                "(query tql/1.0 (select plain *) (from p) (load) \
                 (where (or boost(search(body,$terms,str:'and'),int:3) \
                 search(title,$terms))) (group) (order score() desc) (limit 10) (offset -) \
                 (include))",
            ),
        },
        Vector {
            name: "vector-search-with-capability",
            version: Some("tql/1.0"),
            text: "select @id, score() from products \
                   where vector.search(embedding, $queryVector, 0.62) \
                   order by score() desc limit 25",
            parameters: vec![(
                "queryVector",
                DocumentValue::Array(vec![DocumentValue::Number(0.1)]),
            )],
            capabilities: TqlCapabilities::none().with(Capability::Vector).with(Capability::Search),
            expected: Expected::Outline(
                "(query tql/1.0 (select plain @id;score()) (from products) (load) \
                 (where vector.search(embedding,$queryVector,num:0.62)) (group) \
                 (order score() desc) (limit 25) (offset -) (include))",
            ),
        },
        Vector {
            name: "vector-of-inline-constructor",
            version: Some("tql/1.0"),
            text: "from p where vector.search(embedding, vector.of(0.1, 0.2))",
            parameters: vec![],
            capabilities: TqlCapabilities::none().with(Capability::Vector),
            expected: Expected::Outline(
                "(query tql/1.0 (select plain *) (from p) (load) \
                 (where vector.search(embedding,vector.of(num:0.1,num:0.2))) (group) (order) \
                 (limit -) (offset -) (include))",
            ),
        },
        // -------------------------------------------------------------------
        // Fail-closed outcomes
        // -------------------------------------------------------------------
        Vector {
            name: "missing-grammar-version",
            version: None,
            text: "from t",
            parameters: vec![],
            capabilities: NONE,
            expected: Expected::Error(TqlErrorClass::MissingGrammarVersion, None),
        },
        Vector {
            name: "unsupported-future-major",
            version: Some("tql/2.0"),
            text: "from t",
            parameters: vec![],
            capabilities: NONE,
            expected: Expected::Error(TqlErrorClass::UnsupportedGrammarVersion, None),
        },
        Vector {
            name: "unsupported-newer-minor",
            version: Some("tql/1.1"),
            text: "from t",
            parameters: vec![],
            capabilities: NONE,
            expected: Expected::Error(TqlErrorClass::UnsupportedGrammarVersion, None),
        },
        Vector {
            name: "misplaced-from",
            version: Some("tql/1.0"),
            text: "select { a: 1 } where b = 2 from t",
            parameters: vec![],
            capabilities: NONE,
            expected: Expected::Error(TqlErrorClass::ParseError, None),
        },
        Vector {
            name: "repeated-clause",
            version: Some("tql/1.0"),
            text: "from t where a = 1 where b = 2",
            parameters: vec![],
            capabilities: NONE,
            expected: Expected::Error(TqlErrorClass::ParseError, None),
        },
        Vector {
            name: "clause-out-of-order",
            version: Some("tql/1.0"),
            text: "from t limit 1 offset 2 select a",
            parameters: vec![],
            capabilities: NONE,
            expected: Expected::Error(TqlErrorClass::ParseError, None),
        },
        Vector {
            name: "unterminated-string",
            version: Some("tql/1.0"),
            text: "from t where x = 'open",
            parameters: vec![],
            capabilities: NONE,
            expected: Expected::Error(TqlErrorClass::ParseError, None),
        },
        Vector {
            name: "unterminated-block-comment",
            version: Some("tql/1.0"),
            text: "from t /* open",
            parameters: vec![],
            capabilities: NONE,
            expected: Expected::Error(TqlErrorClass::ParseError, None),
        },
        Vector {
            name: "unknown-metadata-field",
            version: Some("tql/1.0"),
            text: "from t where @bogus = 1",
            parameters: vec![],
            capabilities: NONE,
            expected: Expected::Error(TqlErrorClass::ParseError, None),
        },
        Vector {
            name: "function-call-is-not-indexable",
            version: Some("tql/1.0"),
            text: "from t where f(x)[0] = 1",
            parameters: vec![],
            capabilities: NONE,
            expected: Expected::Error(TqlErrorClass::ParseError, None),
        },
        Vector {
            name: "unknown-function",
            version: Some("tql/1.0"),
            text: "from t where trim(x) = 'a'",
            parameters: vec![],
            capabilities: NONE,
            expected: Expected::Error(TqlErrorClass::UnknownFunction, Some("trim")),
        },
        Vector {
            name: "wrong-arity",
            version: Some("tql/1.0"),
            text: "from t where lower() = 'a'",
            parameters: vec![],
            capabilities: NONE,
            expected: Expected::Error(TqlErrorClass::InvalidArgument, Some("lower")),
        },
        Vector {
            name: "wrong-literal-domain",
            version: Some("tql/1.0"),
            text: "from t where lower(5) = 'a'",
            parameters: vec![],
            capabilities: NONE,
            expected: Expected::Error(TqlErrorClass::InvalidArgument, Some("lower")),
        },
        Vector {
            name: "wrong-arity-if",
            version: Some("tql/1.0"),
            text: "from t where if(1, 2) = 3",
            parameters: vec![],
            capabilities: NONE,
            expected: Expected::Error(TqlErrorClass::InvalidArgument, Some("if")),
        },
        Vector {
            name: "exact-requires-a-comparison",
            version: Some("tql/1.0"),
            text: "from t where exact(5)",
            parameters: vec![],
            capabilities: NONE,
            expected: Expected::Error(TqlErrorClass::InvalidArgument, Some("exact")),
        },
        Vector {
            name: "exists-requires-a-path",
            version: Some("tql/1.0"),
            text: "from t where exists(1)",
            parameters: vec![],
            capabilities: NONE,
            expected: Expected::Error(TqlErrorClass::InvalidArgument, Some("exists")),
        },
        Vector {
            name: "group-scope-aggregate-in-where",
            version: Some("tql/1.0"),
            text: "from t where sum(total) > 5",
            parameters: vec![],
            capabilities: NONE,
            expected: Expected::Error(TqlErrorClass::InvalidArgument, Some("sum")),
        },
        Vector {
            name: "unbound-parameter",
            version: Some("tql/1.0"),
            text: "from t where x = $nope",
            parameters: vec![],
            capabilities: NONE,
            expected: Expected::Error(TqlErrorClass::UnboundParameter, Some("nope")),
        },
        Vector {
            name: "unexpected-parameter",
            version: Some("tql/1.0"),
            text: "from t where x = 1",
            parameters: vec![("unused", integer(1))],
            capabilities: NONE,
            expected: Expected::Error(TqlErrorClass::UnexpectedParameter, Some("unused")),
        },
        Vector {
            name: "vector-capability-unavailable",
            version: Some("tql/1.0"),
            text: "from p where vector.search(embedding, $v)",
            parameters: vec![("v", DocumentValue::Array(vec![DocumentValue::Number(0.1)]))],
            capabilities: NONE,
            expected: Expected::Error(TqlErrorClass::CapabilityUnavailable, Some("vector.search")),
        },
        Vector {
            name: "search-capability-unavailable",
            version: Some("tql/1.0"),
            text: "from p where search(body, $terms)",
            parameters: vec![("terms", string("coffee"))],
            capabilities: NONE,
            expected: Expected::Error(TqlErrorClass::CapabilityUnavailable, Some("search")),
        },
        Vector {
            name: "limit-zero",
            version: Some("tql/1.0"),
            text: "from t limit 0",
            parameters: vec![],
            capabilities: NONE,
            expected: Expected::Error(TqlErrorClass::InvalidBound, None),
        },
        Vector {
            name: "limit-above-the-seam-maximum",
            version: Some("tql/1.0"),
            text: "from t limit 2000",
            parameters: vec![],
            capabilities: NONE,
            expected: Expected::Error(TqlErrorClass::InvalidBound, None),
        },
        Vector {
            name: "bound-parameter-zero",
            version: Some("tql/1.0"),
            text: "from t limit $page",
            parameters: vec![("page", integer(0))],
            capabilities: NONE,
            expected: Expected::Error(TqlErrorClass::InvalidBound, Some("page")),
        },
        Vector {
            name: "reserved-vector-as-collection",
            version: Some("tql/1.0"),
            text: "from vector",
            parameters: vec![],
            capabilities: NONE,
            expected: Expected::Error(TqlErrorClass::ReservedName, Some("vector")),
        },
        Vector {
            name: "reserved-spatial-as-alias",
            version: Some("tql/1.0"),
            text: "from t as spatial",
            parameters: vec![],
            capabilities: NONE,
            expected: Expected::Error(TqlErrorClass::ReservedName, Some("spatial")),
        },
        Vector {
            name: "reserved-family-collection",
            version: Some("tql/1.0"),
            text: "from \"terrabase.families\"",
            parameters: vec![],
            capabilities: NONE,
            expected: Expected::Error(TqlErrorClass::ReservedName, Some("terrabase.families")),
        },
        Vector {
            name: "oversized-identifier",
            version: Some("tql/1.0"),
            text: "from t as aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\
                   aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\
                   aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\
                   aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\
                   aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            parameters: vec![],
            capabilities: NONE,
            expected: Expected::Error(TqlErrorClass::InvalidIdentifier, None),
        },
        Vector {
            name: "empty-quoted-identifier",
            version: Some("tql/1.0"),
            text: "from t as \"\"",
            parameters: vec![],
            capabilities: NONE,
            expected: Expected::Error(TqlErrorClass::InvalidIdentifier, None),
        },
    ]
}

#[test]
fn golden_query_vectors_cover_every_grammar_construct() {
    let corpus = vectors();
    // The exact counts keep the packet's quantitative claims derived from
    // this artifact: 40 parse vectors and 29 error vectors, 69 in total
    // (M0-5 removed the spatial function vectors).
    let errors =
        corpus.iter().filter(|vector| matches!(vector.expected, Expected::Error(..))).count();
    assert_eq!(corpus.len(), 69, "the corpus must stay complete");
    assert_eq!(errors, 29, "the corpus keeps its error vectors");
    for vector in &corpus {
        let parameters =
            TqlParameters::try_new(&vector.parameters).expect("vector parameters are valid");
        let outcome = match vector.version {
            Some(declared) => {
                tql::parse_artifact(Some(declared), vector.text, &parameters, &vector.capabilities)
            }
            None => tql::parse_artifact(None, vector.text, &parameters, &vector.capabilities),
        };
        match &vector.expected {
            Expected::Outline(expected) => {
                let query = outcome.unwrap_or_else(|error| {
                    panic!("vector {} must parse: {} ({:?})", vector.name, vector.text, error)
                });
                assert_eq!(query.outline(), *expected, "vector {} outline drifted", vector.name);
                assert_eq!(
                    query.grammar_version,
                    GrammarVersion::CURRENT,
                    "vector {} carries the grammar version",
                    vector.name
                );
            }
            Expected::Error(class, construct) => {
                let Err(error) = outcome else {
                    panic!("vector {} must fail closed: {}", vector.name, vector.text);
                };
                assert_eq!(
                    error.class(),
                    *class,
                    "vector {} produced the wrong class: {:?}",
                    vector.name,
                    error
                );
                if let Some(expected_construct) = construct {
                    assert_eq!(
                        error.construct(),
                        Some(*expected_construct),
                        "vector {} names the offending construct",
                        vector.name
                    );
                }
                if *class != TqlErrorClass::MissingGrammarVersion
                    && *class != TqlErrorClass::UnsupportedGrammarVersion
                    && *class != TqlErrorClass::UnexpectedParameter
                {
                    assert!(
                        error.position().is_some(),
                        "vector {} carries a source position",
                        vector.name
                    );
                }
            }
        }
    }
}

/// Collects the registry function names one expression subtree invokes.
fn collect_invocations(expression: &Expression, invoked: &mut BTreeSet<String>) {
    match &expression.kind {
        ExpressionKind::Call(call) => {
            invoked.insert(call.name.clone());
            for argument in &call.args {
                collect_invocations(argument, invoked);
            }
        }
        ExpressionKind::Literal(_) | ExpressionKind::ParameterRef(_) | ExpressionKind::Path(_) => {}
        ExpressionKind::Unary { operand, .. } | ExpressionKind::Not { operand } => {
            collect_invocations(operand, invoked);
        }
        ExpressionKind::Binary { left, right, .. } => {
            collect_invocations(left, invoked);
            collect_invocations(right, invoked);
        }
        ExpressionKind::Between { expr, low, high } => {
            collect_invocations(expr, invoked);
            collect_invocations(low, invoked);
            collect_invocations(high, invoked);
        }
        ExpressionKind::In { expr, list, .. } => {
            collect_invocations(expr, invoked);
            for item in list {
                collect_invocations(item, invoked);
            }
        }
        ExpressionKind::Logical { operands, .. } => {
            for operand in operands {
                collect_invocations(operand, invoked);
            }
        }
        ExpressionKind::ObjectCtor { members } => {
            for member in members {
                collect_invocations(&member.value, invoked);
            }
        }
        ExpressionKind::ArrayCtor { elements } => {
            for element in elements {
                collect_invocations(element, invoked);
            }
        }
    }
}

/// Collects the registry function names one parsed query invokes across every
/// clause that carries expressions.
fn query_invocations(query: &TqlQuery) -> BTreeSet<String> {
    let mut invoked = BTreeSet::new();
    if let ProjectionKind::Items(items) = &query.projection.kind {
        for item in items {
            collect_invocations(&item.expr, &mut invoked);
        }
    }
    if let Some(filter) = &query.filter {
        collect_invocations(filter, &mut invoked);
    }
    for key in &query.grouping {
        collect_invocations(&key.expr, &mut invoked);
    }
    for key in &query.ordering {
        collect_invocations(&key.expr, &mut invoked);
    }
    invoked
}

/// Proves the corpus covers the whole closed registry: every descriptor name
/// the parser enforces is invoked by at least one vector that parses.
#[test]
fn the_corpus_invokes_every_function_of_the_closed_registry() {
    let mut invoked = BTreeSet::new();
    for vector in &vectors() {
        let parameters =
            TqlParameters::try_new(&vector.parameters).expect("vector parameters are valid");
        let Some(declared) = vector.version else { continue };
        if let Ok(query) =
            tql::parse_artifact(Some(declared), vector.text, &parameters, &vector.capabilities)
        {
            invoked.extend(query_invocations(&query));
        }
    }
    let registry: Vec<&str> = tql::registry_function_names().collect();
    // The closed `tql/1.0` registry holds exactly 23 functions; a registry
    // change is a grammar-version change and moves this count with it.
    assert_eq!(registry.len(), 23, "the closed registry enumeration: {registry:?}");
    for name in &registry {
        assert!(
            invoked.contains(*name),
            "no golden vector invokes the registry function {name}; add one"
        );
    }
}
