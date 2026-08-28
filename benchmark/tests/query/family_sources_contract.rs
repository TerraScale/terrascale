//! Public-path tests for the family query/index source vocabulary
//! (GAP-Q19, ticket #70), per `docs/query/gapq19-family-sources.md`.
//!
//! These tests use only public construction and the public compile seam.
//! `TenantScope` has no public constructor by design (Platform creates it
//! after authorization), so the execution proofs — row semantics, F04
//! paging, read parity — run in the in-crate query tests instead
//! (`src/query/family_sources_tests.rs`).

use std::path::PathBuf;
use std::time::Duration;

use terrabase_v2::engine::{Engine, EngineConfig};
use terrabase_v2::families::{TimeSeriesPaging, TimeSeriesRange};
use terrabase_v2::index::definitions::{
    IndexDefinitionError, IndexLanguageVersion, MapExpression, MapOutputField, MapSource, MapStage,
    StaticIndexDefinition, StaticIndexName,
};
use terrabase_v2::operations::{QueryInput, RequestValidationError};
use terrabase_v2::query::{
    OrderDirection, OrderingKey, QueryError, QueryOrdering, QueryPaging, QueryPredicate,
    QueryProjection, QuerySource, QuerySourceKind, QuerySpec, StalenessMode, StalenessPolicy,
};
use terrabase_v2::types::{Cancellation, CollectionName, Deadline, TenantId};

fn location(tag: &str) -> PathBuf {
    let index = std::process::id();
    std::env::temp_dir().join(format!("tbv2-q19-public-{tag}-{index}"))
}

fn open_engine(tag: &str) -> (PathBuf, Engine) {
    let dir = location(tag);
    std::fs::remove_dir_all(&dir).ok();
    let engine =
        Engine::open(EngineConfig::durable(&dir).expect("durable location")).expect("engine opens");
    (dir, engine)
}

fn deadline() -> Deadline {
    Deadline::after(Duration::from_secs(30)).expect("valid deadline")
}

fn family_spec(source: QuerySource) -> QuerySpec {
    QuerySpec::try_new(
        TenantId::try_new("tenant-a").expect("valid tenant"),
        source,
        QueryPredicate::all(Vec::new()).expect("valid conjunction"),
        QueryOrdering::try_new(vec![
            OrderingKey::try_new("name", OrderDirection::Ascending).expect("valid key"),
        ])
        .expect("valid ordering"),
        QueryProjection::whole_document(),
        QueryPaging::try_new(0, None).expect("valid paging"),
        StalenessPolicy::try_new(StalenessMode::Strict, Vec::new()).expect("valid policy"),
        deadline(),
        Cancellation::new(),
    )
    .expect("valid specification")
}

fn entry_shape() -> Vec<MapOutputField> {
    vec![
        MapOutputField::try_new("name", MapExpression::try_field("name").expect("valid path"))
            .expect("valid field"),
        MapOutputField::try_new("value", MapExpression::try_field("value").expect("valid path"))
            .expect("valid field"),
    ]
}

fn index_name(text: &str) -> StaticIndexName {
    StaticIndexName::try_new(text).expect("valid index name")
}

fn collection(text: &str) -> CollectionName {
    CollectionName::try_new(text).expect("valid identifier")
}

#[test]
fn family_query_source_constructors_are_public_vocabulary() {
    assert_eq!(QuerySource::counters().kind(), QuerySourceKind::Counters);
    assert_eq!(QuerySource::time_series().kind(), QuerySourceKind::TimeSeries);
    assert_eq!(
        QuerySource::try_new(collection("users")).expect("user collection").kind(),
        QuerySourceKind::Collection
    );

    // The name-restricted spellings of V1's map-source grammar.
    let named = QuerySource::counters_named("likes").expect("valid name");
    assert_eq!(named.counter_name(), Some("likes"));
    assert_eq!(named.series_name(), None);
    let series = QuerySource::time_series_named("HeartRate").expect("valid name");
    assert_eq!(series.series_name(), Some("HeartRate"));
    assert_eq!(series.counter_name(), None);
    assert!(QuerySource::counters().counter_name().is_none());
    assert!(QuerySource::time_series().series_name().is_none());

    // Validation fails closed through the same typed vocabulary.
    assert_eq!(
        QuerySource::counters_named(" padded ").err(),
        Some(RequestValidationError::InvalidQuery)
    );
    assert_eq!(
        QuerySource::time_series_named("slash/name").err(),
        Some(RequestValidationError::InvalidQuery)
    );
    // The reserved family collection stays refused for document sources.
    assert_eq!(
        QuerySource::try_new(collection("terrabase.families")).err(),
        Some(RequestValidationError::InvalidQuery)
    );
}

#[test]
fn family_source_specs_compile_through_the_public_seam() {
    let (_dir, engine) = open_engine("compile");
    let store = engine.queries();
    for source in [QuerySource::counters(), QuerySource::time_series()] {
        let plan = store.compile(QueryInput::Typed(family_spec(source.clone())));
        assert!(plan.is_ok(), "the family source compiles: {:?}", source.kind());
    }

    // The fail-closed rule: family sources are index-less.
    let namespace = terrabase_v2::query::IndexNamespace::try_new(
        TenantId::try_new("tenant-a").expect("valid tenant"),
        "Auto/ByValue",
    )
    .expect("valid namespace");
    let spec = QuerySpec::try_new(
        TenantId::try_new("tenant-a").expect("valid tenant"),
        QuerySource::counters(),
        QueryPredicate::all(Vec::new()).expect("valid conjunction"),
        QueryOrdering::default(),
        QueryProjection::whole_document(),
        QueryPaging::try_new(0, None).expect("valid paging"),
        StalenessPolicy::try_new(StalenessMode::AllowStale, vec![namespace]).expect("valid policy"),
        deadline(),
        Cancellation::new(),
    )
    .expect("valid specification");
    assert_eq!(
        store.compile(QueryInput::Typed(spec)).err(),
        Some(QueryError::InvalidSpec),
        "an index-naming staleness policy on a family source fails closed"
    );

    // The time-series window binds only to the time-series source.
    let windowed = family_spec(QuerySource::time_series())
        .with_time_series_window(
            Some(TimeSeriesRange::try_new(0, 100).expect("valid range")),
            Some(TimeSeriesPaging::try_new(None, Some(10)).expect("valid paging")),
        )
        .expect("valid window");
    assert!(store.compile(QueryInput::Typed(windowed)).is_ok());
    let misplaced = family_spec(QuerySource::counters()).with_time_series_window(
        Some(TimeSeriesRange::try_new(0, 100).expect("valid range")),
        None,
    );
    assert!(misplaced.is_err(), "the window is refused on a counter source");
}

#[test]
fn index_definitions_accept_family_map_sources() {
    for (source, identity) in [
        (MapSource::Counters { name: None }, "counters"),
        (MapSource::Counters { name: Some("likes".to_owned()) }, "counters.likes"),
        (MapSource::TimeSeries { name: None }, "timeSeries"),
        (MapSource::TimeSeries { name: Some("HeartRate".to_owned()) }, "timeSeries.HeartRate"),
    ] {
        let stage = MapStage::try_new(source.clone(), entry_shape(), Vec::new())
            .expect("valid family map stage");
        let definition = StaticIndexDefinition::try_map(
            IndexLanguageVersion::CURRENT,
            index_name(&format!("family/{identity}")),
            stage,
        )
        .expect("the definition accepts the family source");
        let rendered = definition.identity_key();
        assert!(
            rendered.contains(identity),
            "the identity renders the V1 map-source spelling {identity}: {rendered}"
        );
    }
}

#[test]
fn index_definitions_refuse_mixed_and_overlapping_family_sources() {
    // One shared one-field output shape, so the multi-stage rule under test
    // is the source rule and not the common-shape rule.
    let one_field_shape = || {
        vec![
            MapOutputField::try_new(
                "value",
                MapExpression::try_field("value").expect("valid path"),
            )
            .expect("valid field"),
        ]
    };
    let counters_stage = |name: Option<&str>| {
        MapStage::try_new(
            MapSource::Counters { name: name.map(str::to_owned) },
            one_field_shape(),
            Vec::new(),
        )
        .expect("valid stage")
    };

    // A counters source may not mix with a document collection.
    let mixed = MapStage::try_new(
        MapSource::Collection(collection("users")),
        one_field_shape(),
        Vec::new(),
    )
    .expect("valid stage");
    let err = StaticIndexDefinition::try_new(
        IndexLanguageVersion::CURRENT,
        index_name("family/mixed"),
        vec![counters_stage(None), mixed],
        None,
        Vec::new(),
        Vec::new(),
        None,
    )
    .err();
    assert_eq!(err, Some(IndexDefinitionError::SourceKindMismatch));

    // Nor with the time-series kind.
    let series_stage =
        MapStage::try_new(MapSource::TimeSeries { name: None }, one_field_shape(), Vec::new())
            .expect("valid stage");
    let err = StaticIndexDefinition::try_new(
        IndexLanguageVersion::CURRENT,
        index_name("family/mixed-kinds"),
        vec![counters_stage(None), series_stage],
        None,
        Vec::new(),
        Vec::new(),
        None,
    )
    .err();
    assert_eq!(err, Some(IndexDefinitionError::SourceKindMismatch));

    // The all-entries form overlaps any named form of the same kind.
    let err = StaticIndexDefinition::try_new(
        IndexLanguageVersion::CURRENT,
        index_name("family/overlap"),
        vec![counters_stage(None), counters_stage(Some("likes"))],
        None,
        Vec::new(),
        Vec::new(),
        None,
    )
    .err();
    assert_eq!(err, Some(IndexDefinitionError::OverlappingFamilySources));

    // Two distinct named sources of one kind are the V1 multi-map shape.
    let both = StaticIndexDefinition::try_new(
        IndexLanguageVersion::CURRENT,
        index_name("family/two-names"),
        vec![counters_stage(Some("likes")), counters_stage(Some("views"))],
        None,
        Vec::new(),
        Vec::new(),
        None,
    );
    assert!(both.is_ok(), "disjoint named family sources may multi-map");

    // The same named source twice is the existing duplicate rule.
    let err = StaticIndexDefinition::try_new(
        IndexLanguageVersion::CURRENT,
        index_name("family/duplicate"),
        vec![counters_stage(Some("likes")), counters_stage(Some("likes"))],
        None,
        Vec::new(),
        Vec::new(),
        None,
    )
    .err();
    assert_eq!(err, Some(IndexDefinitionError::DuplicateMapSource));
}
