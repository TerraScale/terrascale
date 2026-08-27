//! TQL public-surface integration tests (ticket TQL-02).
//!
//! These tests reach only what an external caller can construct: the typed
//! grammar-version and parameter values, the public parse entries, the typed
//! TQL AST, the typed error vocabulary, and the lowering onto the one compile
//! seam. Execution parity against equivalent typed query operations is covered
//! by the in-crate tests, because `ReadSnapshot` acquisition has no public
//! construction path by design.

use std::time::Duration;

use terrabase_v2::QueryInput;
use terrabase_v2::query::tql::{
    self, Capability, Direction, ExpressionKind, GrammarVersion, Literal, MetadataField, PageBound,
    PageBoundKind, Paging, PathRoot, PathSegment, ProjectionKind, Quantifier, SourceKind,
    TqlCapabilities, TqlErrorClass, TqlParameters,
};
use terrabase_v2::types::{Cancellation, Deadline, DocumentValue, TenantId};
use terrabase_v2::{Engine, EngineConfig};

fn location(tag: &str) -> std::path::PathBuf {
    std::env::temp_dir().join(format!("tbv2-tql02-public-{tag}-{}", std::process::id()))
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

fn params(entries: &[(&str, DocumentValue)]) -> TqlParameters {
    TqlParameters::try_new(entries).expect("valid parameters")
}

// ---------------------------------------------------------------------------
// Grammar version identity
// ---------------------------------------------------------------------------

#[test]
fn grammar_version_identity_round_trips_the_current_version() {
    assert_eq!(GrammarVersion::CURRENT, GrammarVersion::new(1, 0));
    assert_eq!(GrammarVersion::CURRENT.as_str(), "tql/1.0");
    assert_eq!(GrammarVersion::parse("tql/1.0"), Some(GrammarVersion::CURRENT));
    assert_eq!(GrammarVersion::parse("tql/2.0"), Some(GrammarVersion::new(2, 0)));
    assert_eq!(GrammarVersion::parse("not-a-version"), None);
    assert_eq!(GrammarVersion::parse("tql/1"), None);
    assert_eq!(GrammarVersion::parse("tql/1.0.0"), None);
}

#[test]
fn parse_requires_the_declared_version_and_never_guesses() {
    let parameters = TqlParameters::new();
    let query = tql::parse(GrammarVersion::CURRENT, "from users", &parameters)
        .expect("the current version parses");
    assert_eq!(query.grammar_version, GrammarVersion::CURRENT);
    // A newer minor of the same major is rejected: the additive rule makes the
    // parser the judge of what its own minor accepts.
    assert_eq!(
        tql::parse(GrammarVersion::new(1, 1), "from users", &parameters).unwrap_err().class(),
        TqlErrorClass::UnsupportedGrammarVersion
    );
    assert_eq!(
        tql::parse(GrammarVersion::new(2, 0), "from users", &parameters).unwrap_err().class(),
        TqlErrorClass::UnsupportedGrammarVersion
    );
    // The artifact entry rejects a missing version with its own class.
    assert_eq!(
        tql::parse_artifact(None, "from users", &parameters, &TqlCapabilities::none())
            .unwrap_err()
            .class(),
        TqlErrorClass::MissingGrammarVersion
    );
    assert_eq!(
        tql::parse_artifact(Some("tql/9.9"), "from users", &parameters, &TqlCapabilities::none())
            .unwrap_err()
            .class(),
        TqlErrorClass::UnsupportedGrammarVersion
    );
    assert_eq!(
        tql::parse_artifact(Some("garbage"), "from users", &parameters, &TqlCapabilities::none())
            .unwrap_err()
            .class(),
        TqlErrorClass::UnsupportedGrammarVersion
    );
    let artifact =
        tql::parse_artifact(Some("tql/1.0"), "from users", &parameters, &TqlCapabilities::none())
            .expect("a declared current version parses");
    assert_eq!(artifact.grammar_version, GrammarVersion::CURRENT);
}

#[test]
fn every_error_carries_version_and_position_where_applicable() {
    let parameters = TqlParameters::new();
    let error = tql::parse(GrammarVersion::CURRENT, "from users where", &parameters)
        .expect_err("an incomplete where fails closed");
    assert_eq!(error.class(), TqlErrorClass::ParseError);
    assert_eq!(error.grammar_version(), Some(GrammarVersion::CURRENT));
    let position = error.position().expect("a parse error is positioned");
    assert_eq!(position.byte(), "from users where".len());
    assert_eq!(position.line(), 1);
    assert_eq!(position.column(), ("from users where".len() + 1) as u32);
    // A version error names the offending version and no source position.
    let version_error = tql::parse(GrammarVersion::new(3, 0), "from users", &parameters)
        .expect_err("an unknown version fails closed before the text is read");
    assert_eq!(version_error.class(), TqlErrorClass::UnsupportedGrammarVersion);
    assert_eq!(version_error.grammar_version(), Some(GrammarVersion::new(3, 0)));
    assert_eq!(version_error.position(), None);
}

#[test]
fn text_beyond_the_shared_query_text_bound_fails_closed() {
    let oversized = format!("from users where {}", "a = 1 and ".repeat(4096));
    assert!(oversized.len() > terrabase_v2::MAX_QUERY_TEXT_BYTES);
    assert_eq!(
        tql::parse(GrammarVersion::CURRENT, &oversized, &TqlParameters::new()).unwrap_err().class(),
        TqlErrorClass::ParseError
    );
    assert_eq!(
        tql::parse(GrammarVersion::CURRENT, "", &TqlParameters::new()).unwrap_err().class(),
        TqlErrorClass::ParseError
    );
    assert_eq!(
        tql::parse(GrammarVersion::CURRENT, "   \n ", &TqlParameters::new()).unwrap_err().class(),
        TqlErrorClass::ParseError
    );
}

// ---------------------------------------------------------------------------
// Parse results: clause inventory over the public AST
// ---------------------------------------------------------------------------

#[test]
fn omitted_select_parses_as_the_whole_document_projection() {
    let query = tql::parse(GrammarVersion::CURRENT, "from orders limit 10", &TqlParameters::new())
        .expect("the quick RQL-style form stays valid");
    assert!(matches!(query.projection.kind, ProjectionKind::WholeDocument));
    assert!(!query.projection.distinct);
    assert_eq!(query.source.kind, SourceKind::Collection("orders".to_owned()));
    assert_eq!(query.source.alias, None);
    assert_eq!(
        query.paging,
        Paging {
            span: query.paging.span,
            limit: Some(PageBound { span: query.paging.span, kind: PageBoundKind::Literal(10) }),
            offset: None,
        }
    );
}

#[test]
fn a_full_clause_inventory_parses_into_its_typed_nodes() {
    let text = "select name, address.city as city \
                from customers as c \
                load c.region as r \
                where address.country in ('NO', 'SE') and active = true \
                group by name, address.city \
                order by name desc nulls last, city \
                limit 20 offset 40 \
                include c.reports";
    let query = tql::parse(GrammarVersion::CURRENT, text, &TqlParameters::new()).expect("parses");
    assert_eq!(query.source.alias.as_deref(), Some("c"));
    assert_eq!(query.loads.len(), 1);
    assert_eq!(query.loads[0].alias, "r");
    let Some(filter) = query.filter.as_ref() else {
        panic!("the where clause parses");
    };
    assert!(matches!(filter.kind, ExpressionKind::Logical { .. }));
    assert_eq!(query.grouping.len(), 2);
    assert_eq!(query.grouping[0].alias, None);
    assert_eq!(query.ordering.len(), 2);
    assert_eq!(query.ordering[0].direction, Some(Direction::Descending));
    assert_eq!(query.ordering[0].nulls, Some(terrabase_v2::query::tql::NullsOrder::Last));
    assert_eq!(query.ordering[1].direction, None);
    assert_eq!(
        query.paging.limit.as_ref().map(|bound| match &bound.kind {
            PageBoundKind::Literal(value) => *value,
            PageBoundKind::Parameter(_) => unreachable!("the vector uses a literal"),
        }),
        Some(20)
    );
    assert_eq!(query.includes.len(), 1);
    match &query.projection.kind {
        ProjectionKind::Items(items) => {
            assert_eq!(items.len(), 2);
            assert_eq!(items[0].alias, None);
            assert_eq!(items[1].alias.as_deref(), Some("city"));
        }
        other => panic!("an item list parses as items, not {other:?}"),
    }
}

#[test]
fn metadata_paths_and_array_navigation_parse_to_typed_segments() {
    let query = tql::parse(
        GrammarVersion::CURRENT,
        "select @id, shipment.history[0].warehouse, items[].qty, @metadata.flags \
         from @all_docs \
         where @collection = 'orders'",
        &TqlParameters::new(),
    )
    .expect("metadata and array forms parse");
    assert_eq!(query.source.kind, SourceKind::AllDocs);
    let ProjectionKind::Items(items) = &query.projection.kind else {
        panic!("the projection is an item list");
    };
    let root = |item: &terrabase_v2::query::tql::ProjectionItem| {
        let ExpressionKind::Path(path) = &item.expr.kind else {
            panic!("every projection item in this vector is a path");
        };
        path.clone()
    };
    assert_eq!(root(&items[0]).root, PathRoot::Metadata(MetadataField::Id));
    assert_eq!(root(&items[0]).segments, Vec::new());
    assert_eq!(root(&items[1]).root, PathRoot::Member("shipment".to_owned()));
    assert_eq!(
        root(&items[1]).segments,
        vec![
            PathSegment::Member("history".to_owned()),
            PathSegment::ArrayIndex(0),
            PathSegment::Member("warehouse".to_owned()),
        ]
    );
    assert_eq!(root(&items[2]).root, PathRoot::Member("items".to_owned()));
    assert_eq!(
        root(&items[2]).segments,
        vec![PathSegment::AnyElement, PathSegment::Member("qty".to_owned())]
    );
    assert_eq!(root(&items[3]).root, PathRoot::Metadata(MetadataField::MetadataRoot));
    assert_eq!(root(&items[3]).segments, vec![PathSegment::Member("flags".to_owned())]);
}

#[test]
fn expressions_carry_the_typed_literal_and_operator_nodes() {
    let query = tql::parse(
        GrammarVersion::CURRENT,
        "select -1.5e2 + 3 * count() from t \
         where not (a <> 'it''s' or b between 1 and 2) and tags all in ('a', 'b')",
        &TqlParameters::new(),
    )
    .expect("operator and literal forms parse");
    let filter = query.filter.as_ref().expect("the where clause parses");
    let ExpressionKind::Logical { op, operands } = &filter.kind else {
        panic!("the top-level filter is a conjunction");
    };
    assert!(*op == terrabase_v2::query::tql::LogicalOperator::And);
    assert_eq!(operands.len(), 2);
    let ExpressionKind::Not { operand } = &operands[0].kind else {
        panic!("the conjunction negates the disjunction");
    };
    let ExpressionKind::Logical { op: inner, .. } = &operand.kind else {
        panic!("the not operand is the disjunction");
    };
    assert!(*inner == terrabase_v2::query::tql::LogicalOperator::Or);
    let ExpressionKind::In { quantifier, list, .. } = &operands[1].kind else {
        panic!("the all-in form parses as an In node");
    };
    assert_eq!(*quantifier, Quantifier::All);
    assert_eq!(list.len(), 2);
    let ExpressionKind::Literal(Literal::String(text)) = &list[0].kind else {
        panic!("string literals keep their unescaped value");
    };
    // The escape check lives in its own vector; this one pins the operator set.
    assert_eq!(text, "a");
    let ProjectionKind::Items(items) = &query.projection.kind else {
        panic!("the projection is an item list");
    };
    let ExpressionKind::Binary { .. } = &items[0].expr.kind else {
        panic!("arithmetic parses as a binary node");
    };
}

#[test]
fn string_and_quoted_identifier_escapes_and_comments_parse() {
    let query = tql::parse(
        GrammarVersion::CURRENT,
        "// leading comment\nselect props.\"Color (front)\" as color, \"quoted key\" \
         /* block\ncomment */ from devices \
         where note = 'it''s fine' // trailing comment",
        &TqlParameters::new(),
    )
    .expect("escapes and comments parse");
    let ProjectionKind::Items(items) = &query.projection.kind else {
        panic!("the projection is an item list");
    };
    let ExpressionKind::Path(path) = &items[0].expr.kind else {
        panic!("the first item is a path");
    };
    assert_eq!(path.root, PathRoot::Member("props".to_owned()));
    assert_eq!(path.segments, vec![PathSegment::Member("Color (front)".to_owned())]);
    assert_eq!(items[0].alias.as_deref(), Some("color"));
    let filter = query.filter.as_ref().expect("the where clause parses");
    let ExpressionKind::Binary { right, .. } = &filter.kind else {
        panic!("the filter is a comparison");
    };
    let ExpressionKind::Literal(Literal::String(text)) = &right.kind else {
        panic!("the compared value is a string literal");
    };
    assert_eq!(text, "it's fine");
}

// ---------------------------------------------------------------------------
// Fail-closed static checks
// ---------------------------------------------------------------------------

#[test]
fn function_registry_is_closed_for_the_declared_version() {
    let parameters = TqlParameters::new();
    let unknown = tql::parse(GrammarVersion::CURRENT, "from t where trim(x) = 'a'", &parameters)
        .expect_err("an unregistered name fails closed");
    assert_eq!(unknown.class(), TqlErrorClass::UnknownFunction);
    assert_eq!(unknown.construct(), Some("trim"));
    let arity = tql::parse(GrammarVersion::CURRENT, "from t where lower() = 'a'", &parameters)
        .expect_err("a wrong arity fails closed");
    assert_eq!(arity.class(), TqlErrorClass::InvalidArgument);
    let domain = tql::parse(GrammarVersion::CURRENT, "from t where lower(5) = 'a'", &parameters)
        .expect_err("a literal argument outside the function domain fails closed");
    assert_eq!(domain.class(), TqlErrorClass::InvalidArgument);
    let aggregate = tql::parse(GrammarVersion::CURRENT, "from t where sum(total) > 5", &parameters)
        .expect_err("a group-scope aggregate in where fails closed");
    assert_eq!(aggregate.class(), TqlErrorClass::InvalidArgument);
    // A per-document aggregate over any-element navigation is legal in where.
    assert!(
        tql::parse(GrammarVersion::CURRENT, "from t where sum(lines[].qty) > 5", &parameters)
            .is_ok()
    );
}

#[test]
fn capability_gated_predicates_fail_closed_until_the_capability_ships() {
    let vector_parameters =
        params(&[("queryVector", DocumentValue::Array(vec![DocumentValue::Number(0.1)]))]);
    let term_parameters = params(&[("terms", DocumentValue::String("coffee".to_owned()))]);
    let empty_parameters = TqlParameters::new();
    let gated: &[(&str, &TqlParameters)] = &[
        ("from p where vector.search(embedding, $queryVector, 0.62)", &vector_parameters),
        ("from p where search(body, $terms)", &term_parameters),
        ("from p where boost(search(title, $terms), 3)", &term_parameters),
        ("from p order by score()", &empty_parameters),
        (
            "from p where spatial.within(loc, spatial.circle(10, 59.9, 10.7, 'km'))",
            &empty_parameters,
        ),
        ("from p where spatial.intersects(area, spatial.wkt($terms))", &term_parameters),
        (
            "from p where spatial.contains(area, spatial.circle(10, 59.9, 10.7, 'km'))",
            &empty_parameters,
        ),
        ("from p where spatial.disjoint(area, spatial.point(59.9, 10.7))", &empty_parameters),
        ("from p order by spatial.distance(loc, spatial.point(59.9, 10.7))", &empty_parameters),
    ];
    for (text, parameters) in gated {
        let error = tql::parse(GrammarVersion::CURRENT, text, parameters)
            .expect_err("a gated predicate fails closed without the capability");
        assert_eq!(error.class(), TqlErrorClass::CapabilityUnavailable, "{text}");
    }
    // The same text parses once the engine reports the capability: the gate is
    // a capability probe, never a grammar rejection.
    let vector_ready = TqlCapabilities::none().with(Capability::Vector);
    assert!(
        tql::parse_with(
            GrammarVersion::CURRENT,
            "from p where vector.search(embedding, $queryVector, 0.62)",
            &vector_parameters,
            &vector_ready
        )
        .is_ok()
    );
    let search_ready = TqlCapabilities::none().with(Capability::Search);
    assert!(
        tql::parse_with(
            GrammarVersion::CURRENT,
            "from p order by score() limit 2",
            &TqlParameters::new(),
            &search_ready
        )
        .is_ok()
    );
    let spatial_ready = TqlCapabilities::none().with(Capability::Spatial);
    assert!(
        tql::parse_with(
            GrammarVersion::CURRENT,
            "from p where spatial.within(loc, spatial.point(59.9, 10.7))",
            &empty_parameters,
            &spatial_ready
        )
        .is_ok()
    );
}

#[test]
fn parameter_pairing_is_strict_in_both_directions() {
    let empty = TqlParameters::new();
    let unbound = tql::parse(GrammarVersion::CURRENT, "from t where x = $nope", &empty)
        .expect_err("an unreferenced binding slot fails closed");
    assert_eq!(unbound.class(), TqlErrorClass::UnboundParameter);
    assert_eq!(unbound.construct(), Some("nope"));
    let unexpected = tql::parse(
        GrammarVersion::CURRENT,
        "from t where x = 1",
        &params(&[("unused", DocumentValue::Integer(1))]),
    )
    .expect_err("an unreferenced binding fails closed");
    assert_eq!(unexpected.class(), TqlErrorClass::UnexpectedParameter);
    assert_eq!(unexpected.construct(), Some("unused"));
    assert!(
        tql::parse(
            GrammarVersion::CURRENT,
            "from t where x = $bound limit $page",
            &params(&[("bound", DocumentValue::Integer(1)), ("page", DocumentValue::Integer(5)),]),
        )
        .is_ok()
    );
}

#[test]
fn paging_bounds_fail_closed_for_zero_and_out_of_range_bounds() {
    let parameters = TqlParameters::new();
    for text in ["from t limit 0", "from t limit 0 offset 0", "from t limit 2000"] {
        let error = tql::parse(GrammarVersion::CURRENT, text, &parameters)
            .expect_err("an invalid page bound fails closed");
        assert_eq!(error.class(), TqlErrorClass::InvalidBound, "{text}");
    }
    let bound_parameter = tql::parse(
        GrammarVersion::CURRENT,
        "from t limit $page",
        &params(&[("page", DocumentValue::Integer(0))]),
    )
    .expect_err("a zero-valued bound parameter fails at binding time");
    assert_eq!(bound_parameter.class(), TqlErrorClass::InvalidBound);
    // A non-numeric binding is not a page bound at all.
    let text_parameter = tql::parse(
        GrammarVersion::CURRENT,
        "from t limit $page",
        &params(&[("page", DocumentValue::String("ten".to_owned()))]),
    )
    .expect_err("a non-numeric bound parameter fails closed");
    assert_eq!(text_parameter.class(), TqlErrorClass::InvalidBound);
    assert!(tql::parse(GrammarVersion::CURRENT, "from t limit 1 offset 0", &parameters).is_ok());
}

#[test]
fn reserved_names_and_invalid_identifiers_fail_closed() {
    let parameters = TqlParameters::new();
    for text in ["from vector", "from spatial as s", "from t as vector", "from t as spatial"] {
        let error = tql::parse(GrammarVersion::CURRENT, text, &parameters)
            .expect_err("a reserved name is not an alias or collection");
        assert_eq!(error.class(), TqlErrorClass::ReservedName, "{text}");
    }
    let family = tql::parse(GrammarVersion::CURRENT, "from \"terrabase.families\"", &parameters)
        .expect_err("the reserved family collection is not a TQL source");
    assert_eq!(family.class(), TqlErrorClass::ReservedName);
    // The dotted bare spelling has no collection-name production and is a
    // positioned parse error instead.
    let dotted = tql::parse(GrammarVersion::CURRENT, "from terrabase.families", &parameters)
        .expect_err("a dotted bare collection name is not grammar");
    assert_eq!(dotted.class(), TqlErrorClass::ParseError);
    let oversized = format!("from t as {}", "a".repeat(300));
    let invalid = tql::parse(GrammarVersion::CURRENT, &oversized, &parameters)
        .expect_err("an oversized identifier fails the shared identifier discipline");
    assert_eq!(invalid.class(), TqlErrorClass::InvalidIdentifier);
    let empty_quoted = tql::parse(GrammarVersion::CURRENT, "from t as \"\"", &parameters)
        .expect_err("an empty quoted identifier is not an identifier");
    assert_eq!(empty_quoted.class(), TqlErrorClass::InvalidIdentifier);
}

#[test]
fn malformed_text_fails_closed_with_positioned_parse_errors() {
    let parameters = TqlParameters::new();
    let misplaced =
        tql::parse(GrammarVersion::CURRENT, "select { a: 1 } where b = 2 from t", &parameters)
            .expect_err("a misplaced from fails closed");
    assert_eq!(misplaced.class(), TqlErrorClass::ParseError);
    let repeated =
        tql::parse(GrammarVersion::CURRENT, "from t where a = 1 where b = 2", &parameters)
            .expect_err("a repeated clause fails closed");
    assert_eq!(repeated.class(), TqlErrorClass::ParseError);
    let trailing = tql::parse(GrammarVersion::CURRENT, "from t select", &parameters)
        .expect_err("a truncated clause fails closed");
    assert_eq!(trailing.class(), TqlErrorClass::ParseError);
    let unterminated = tql::parse(GrammarVersion::CURRENT, "from t where x = 'open", &parameters)
        .expect_err("an unterminated string fails closed");
    assert_eq!(unterminated.class(), TqlErrorClass::ParseError);
    let unknown_at = tql::parse(GrammarVersion::CURRENT, "from t where @bogus = 1", &parameters)
        .expect_err("an unknown metadata pseudo-field fails closed");
    assert_eq!(unknown_at.class(), TqlErrorClass::ParseError);
    let keyword_alias = tql::parse(GrammarVersion::CURRENT, "from t as select", &parameters)
        .expect_err("a reserved word is not a bare alias");
    assert_eq!(keyword_alias.class(), TqlErrorClass::ParseError);
}

#[test]
fn group_by_requires_select_and_order_keys_derived_from_the_group_keys() {
    let parameters = TqlParameters::new();
    assert!(
        tql::parse(
            GrammarVersion::CURRENT,
            "select c.region as region, count() as orders from orders as o \
             load o.customerId as c group by c.region order by region desc limit 10",
            &parameters
        )
        .is_ok(),
        "group keys, aggregates, and their aliases are derived forms"
    );
    let undirected = tql::parse(
        GrammarVersion::CURRENT,
        "select o.total from orders as o group by o.region",
        &parameters,
    )
    .expect_err("a non-derived projection under group by fails closed");
    assert_eq!(undirected.class(), TqlErrorClass::InvalidArgument);
    let whole_document =
        tql::parse(GrammarVersion::CURRENT, "from orders group by o.region", &parameters)
            .expect_err("the whole-document projection is not group-derived");
    assert_eq!(whole_document.class(), TqlErrorClass::InvalidArgument);
}

// ---------------------------------------------------------------------------
// Lowering onto the one compile seam
// ---------------------------------------------------------------------------

#[test]
fn lowering_produces_a_typed_spec_that_compiles_through_the_seam() {
    let (dir, engine) = open_engine("compile");
    let store = engine.queries();
    let query = tql::parse(
        GrammarVersion::CURRENT,
        "select name from users where country = $country and age > 25 \
         order by age desc limit 2 offset 1",
        &params(&[("country", DocumentValue::String("NO".to_owned()))]),
    )
    .expect("parses");
    let spec = tql::lower_to_spec(
        &query,
        &params(&[("country", DocumentValue::String("NO".to_owned()))]),
        tenant(),
        deadline(),
        Cancellation::new(),
    )
    .expect("the seam-expressible form lowers");
    assert_eq!(spec.source().collection().as_str(), "users");
    assert_eq!(spec.predicate().conditions().len(), 2);
    assert_eq!(spec.ordering().keys().len(), 1);
    assert_eq!(spec.projection().selected(), Some(&["name".to_owned()][..]));
    assert_eq!(spec.paging().offset(), 1);
    assert_eq!(spec.paging().limit(), Some(2));
    let plan = store.compile(QueryInput::typed(spec)).expect("the lowered spec compiles");
    assert_eq!(plan.source().collection().as_str(), "users");
    // The seam-glue entry produces the same plan from the TQL artifact.
    let direct = tql::compile(
        &store,
        &query,
        &params(&[("country", DocumentValue::String("NO".to_owned()))]),
        tenant(),
        deadline(),
        Cancellation::new(),
    )
    .expect("the seam glue compiles");
    assert_eq!(direct.source(), plan.source());
    assert_eq!(direct.ordering().keys(), plan.ordering().keys());
    drop(store);
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn lowering_fails_closed_for_constructs_the_seam_cannot_express() {
    let parameters = TqlParameters::new();
    for text in [
        "from orders where a = 1 or b = 2",
        "from orders where address.city = 'Oslo'",
        "select distinct name from orders",
        "select { id: id() } from orders",
        "select name as n from orders",
        "from orders as o load o.customerId as c where c.region = 'EMEA'",
        "from orders include customerId",
        "select region, count() from orders group by region",
        "from @all_docs",
        "from orders where len(name) > 3",
    ] {
        let query = tql::parse(GrammarVersion::CURRENT, text, &parameters)
            .unwrap_or_else(|error| panic!("{text} parses: {error:?}"));
        let error =
            tql::lower_to_spec(&query, &parameters, tenant(), deadline(), Cancellation::new())
                .expect_err("a construct beyond the seam lowers to a typed error");
        assert_eq!(error.class(), TqlErrorClass::UnsupportedForPlanning, "{text}");
        // The error is positioned inside the query text and names the version.
        assert!(error.position().is_some(), "{text}");
        assert_eq!(error.grammar_version(), Some(GrammarVersion::CURRENT));
    }
}

#[test]
fn lowering_rejects_invalid_request_context() {
    let query = tql::parse(GrammarVersion::CURRENT, "from users limit 2", &TqlParameters::new())
        .expect("parses");
    let expired = Deadline::after(Duration::from_nanos(1)).expect("positive duration");
    while !expired.is_expired() {
        std::hint::spin_loop();
    }
    let error =
        tql::lower_to_spec(&query, &TqlParameters::new(), tenant(), expired, Cancellation::new())
            .expect_err("an expired request context fails closed");
    assert_eq!(error.class(), TqlErrorClass::InvalidRequest);
    let cancelled = Cancellation::new();
    cancelled.cancel();
    let error = tql::lower_to_spec(&query, &TqlParameters::new(), tenant(), deadline(), cancelled)
        .expect_err("a cancelled request context fails closed");
    assert_eq!(error.class(), TqlErrorClass::InvalidRequest);
}

#[test]
fn parse_is_deterministic_and_spans_stay_inside_the_text() {
    let text = "select a, b.b from t where c >= -1.5 and d = 'x' order by a desc limit 5";
    let parameters = TqlParameters::new();
    let first = tql::parse(GrammarVersion::CURRENT, text, &parameters).expect("parses");
    let second = tql::parse(GrammarVersion::CURRENT, text, &parameters).expect("parses again");
    assert_eq!(first, second, "same version, text, and parameters, same AST");
    assert_eq!(first.span.start().byte(), 0);
    assert_eq!(first.span.end().byte(), text.len());
    assert_eq!(first.span.start().line(), 1);
    let outline = first.outline();
    assert_eq!(outline, second.outline());
    assert!(outline.contains("from t"));
}
