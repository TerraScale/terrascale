//! Public-path tests for the typed static index definition contract
//! (GAP-Q06, ticket TerraBase/TerraBaseV2#63).
//!
//! The typed definition language is the V2 replacement for V1's
//! code-authored static index definitions: maps, reduce, multi-map, field
//! options (indexing/storage/analysis), dynamic and compound fields, and
//! map-reduce output collections are typed, versioned, fail-closed values.
//! These tests reach the contract only through the public
//! `terrabase::index::definitions` surface, exactly as an external typed
//! operation would.

use std::collections::BTreeMap;

use terrabase::{
    index::definitions::{
        AggregationBinding, AggregationFunction, AnalyzerName, CompoundField,
        DefinitionDifferences, DynamicFieldRule, FieldIndexing, FieldStorage, GroupByArrayMode,
        IndexDefinitionError, IndexFieldOptions, IndexLanguageVersion, MapExpression,
        MapOutputField, MapSource, MapStage, OutputReferencePattern, ReduceKey, ReduceOutput,
        ReduceStage, StaticIndexDefinition, StaticIndexName,
    },
    types::CollectionName,
};

fn collection(name: &str) -> CollectionName {
    CollectionName::try_new(name).expect("valid collection identifier")
}

fn name(value: &str) -> StaticIndexName {
    StaticIndexName::try_new(value).expect("valid static index name")
}

fn output_field(name: &str, path: &str) -> MapOutputField {
    MapOutputField::try_new(name, MapExpression::try_field(path).expect("valid path"))
        .expect("valid output field")
}

fn users_stage() -> MapStage {
    MapStage::try_new(
        MapSource::Collection(collection("users")),
        vec![output_field("region", "address.region"), output_field("age", "age")],
        Vec::new(),
    )
    .expect("valid map stage")
}

fn companies_stage() -> MapStage {
    MapStage::try_new(
        MapSource::Collection(collection("companies")),
        vec![output_field("region", "headquarters.region"), output_field("age", "foundedYears")],
        Vec::new(),
    )
    .expect("valid map stage")
}

fn regional_reduce() -> ReduceStage {
    ReduceStage::try_new(
        vec![ReduceKey::try_new("region", GroupByArrayMode::NotApplicable).expect("valid key")],
        vec![
            AggregationBinding::try_new("age", AggregationFunction::Sum, Some("age"))
                .expect("valid sum"),
            AggregationBinding::try_new("count", AggregationFunction::Count, None)
                .expect("valid count"),
        ],
    )
    .expect("valid reduce stage")
}

#[test]
fn language_version_parses_and_fails_closed() {
    let current = IndexLanguageVersion::try_parse("idx/1.0").expect("current version parses");
    assert_eq!(current, IndexLanguageVersion::CURRENT);
    assert_eq!(current.as_str(), "idx/1.0");

    // The version is data: any well-formed `idx/MAJOR.MINOR` parses, and the
    // engine accepts only the version it speaks at definition construction.
    let stale = IndexLanguageVersion::try_parse("idx/2.0").expect("well-formed version parses");
    assert_eq!(stale.as_str(), "idx/2.0");
    assert_eq!(
        StaticIndexDefinition::try_map(stale, name("Users/ByRegion"), users_stage()),
        Err(IndexDefinitionError::UnknownVersion)
    );

    // Malformed version text never reaches a definition.
    for rejected in ["1.0", "idx/1", "idx/1.0.0", "", "IDX/1.0", "idx/01.0", "idx/x.y"] {
        assert_eq!(
            IndexLanguageVersion::try_parse(rejected),
            Err(IndexDefinitionError::UnknownVersion),
            "version text '{rejected}' must fail closed"
        );
    }
}

#[test]
fn static_names_reject_reserved_and_malformed_shapes() {
    for valid in ["Orders/ByCompany", "users-by-age", "a.b", "Regional_1"] {
        assert!(StaticIndexName::try_new(valid).is_ok(), "'{valid}' must be valid");
    }

    // The reserved auto namespace is refused for static definitions exactly
    // as V1 refuses it (case-insensitively).
    for reserved in ["Auto/Users/ByAge", "auto/Users/ByAge", "AUTO/Users/ByAge"] {
        assert_eq!(StaticIndexName::try_new(reserved), Err(IndexDefinitionError::ReservedAutoName));
    }

    for invalid in
        ["", " ", "a//b", ".leading", "trailing.", "double..dot", "space name", "tab\tname"]
    {
        assert_eq!(StaticIndexName::try_new(invalid), Err(IndexDefinitionError::InvalidName));
    }

    let long = "a".repeat(256);
    assert_eq!(StaticIndexName::try_new(&long), Err(IndexDefinitionError::InvalidName));
    let boundary = "a".repeat(255);
    assert!(StaticIndexName::try_new(&boundary).is_ok());

    let parsed = name("Orders/ByCompany");
    assert_eq!(parsed.as_str(), "Orders/ByCompany");
}

#[test]
fn map_definition_projects_document_fields_in_a_versioned_shape() {
    let definition = StaticIndexDefinition::try_map(
        IndexLanguageVersion::CURRENT,
        name("Users/ByRegion"),
        users_stage(),
    )
    .expect("valid map definition");

    assert_eq!(definition.name().as_str(), "Users/ByRegion");
    assert_eq!(definition.version(), IndexLanguageVersion::CURRENT);
    assert!(!definition.is_map_reduce());
    assert_eq!(definition.map_stages().len(), 1);
    assert_eq!(definition.map_stages()[0].source().collection().map(|c| c.as_str()), Some("users"));
    let shape: Vec<&str> = definition.output_fields().collect();
    assert_eq!(shape, vec!["region", "age"]);
    // A map field may also project the document identity.
    let identity_field = MapOutputField::try_new("id", MapExpression::identity())
        .expect("identity projection is typed");
    let stage = MapStage::try_new(
        MapSource::Collection(collection("users")),
        vec![identity_field, output_field("region", "address.region")],
        Vec::new(),
    )
    .expect("valid stage");
    let definition = StaticIndexDefinition::try_map(
        IndexLanguageVersion::CURRENT,
        name("Users/ByIdentity"),
        stage,
    )
    .expect("valid map definition");
    assert_eq!(definition.map_stages()[0].fields()[0].expression(), &MapExpression::identity());
}

#[test]
fn multi_map_requires_one_common_shape_and_unique_sources() {
    let definition = StaticIndexDefinition::try_map(
        IndexLanguageVersion::CURRENT,
        name("Parties/ByRegion"),
        users_stage(),
    )
    .and_then(|first| first.with_additional_map_stage(companies_stage()))
    .expect("valid multi-map definition");

    assert_eq!(definition.map_stages().len(), 2);
    // Stages are stored in canonical source order regardless of input order.
    let sources: Vec<&str> = definition
        .map_stages()
        .iter()
        .filter_map(|stage| stage.source().collection().map(|c| c.as_str()))
        .collect();
    assert_eq!(sources, vec!["companies", "users"]);

    // Shape mismatch: a stage that projects different output names.
    let mismatched = MapStage::try_new(
        MapSource::Collection(collection("companies")),
        vec![output_field("area", "headquarters.region")],
        Vec::new(),
    )
    .expect("valid stage shape");
    let definition = StaticIndexDefinition::try_map(
        IndexLanguageVersion::CURRENT,
        name("Parties/ByRegion"),
        users_stage(),
    )
    .expect("base definition");
    assert_eq!(
        definition.clone().with_additional_map_stage(mismatched),
        Err(IndexDefinitionError::MapShapeMismatch)
    );

    // Duplicate source collection.
    assert_eq!(
        definition.clone().with_additional_map_stage(users_stage()),
        Err(IndexDefinitionError::DuplicateMapSource)
    );

    // The all-documents source is exclusive: it already covers every
    // collection, so mixing would double-count documents in the reduce.
    let all_documents = MapStage::try_new(
        MapSource::AllDocuments,
        vec![output_field("region", "address.region"), output_field("age", "age")],
        Vec::new(),
    )
    .expect("valid all-documents stage");
    assert_eq!(
        definition.with_additional_map_stage(all_documents),
        Err(IndexDefinitionError::AllDocumentsWithCollections)
    );

    // A definition with no map stage does not exist.
    assert_eq!(
        StaticIndexDefinition::try_new(
            IndexLanguageVersion::CURRENT,
            name("Empty"),
            Vec::new(),
            None,
            Vec::new(),
            Vec::new(),
            None,
        ),
        Err(IndexDefinitionError::NoMaps)
    );
}

#[test]
fn map_reduce_pins_the_static_group_by_shape() {
    let definition = StaticIndexDefinition::try_new(
        IndexLanguageVersion::CURRENT,
        name("Users/ByRegion"),
        vec![users_stage()],
        Some(regional_reduce()),
        Vec::new(),
        Vec::new(),
        None,
    )
    .expect("valid map-reduce definition");

    assert!(definition.is_map_reduce());
    let reduce = definition.reduce().expect("reduce stage present");
    assert_eq!(reduce.keys()[0].field(), "region");
    assert_eq!(reduce.keys()[0].array_mode(), GroupByArrayMode::NotApplicable);
    assert_eq!(reduce.aggregations().len(), 2);
    // The reduce output shape equals the grouping plus the aggregates and
    // covers the map output exactly: static GROUP BY on a map-reduce index.
    let shape: Vec<&str> = definition.output_fields().collect();
    assert_eq!(shape, vec!["region", "age", "count"]);

    // The aggregation vocabulary is V1's typed set: count and sum only.
    assert_eq!(AggregationFunction::Count.as_str(), "count");
    assert_eq!(AggregationFunction::Sum.as_str(), "sum");
}

#[test]
fn map_reduce_validation_fails_closed() {
    fn build(reduce: ReduceStage) -> Result<StaticIndexDefinition, IndexDefinitionError> {
        StaticIndexDefinition::try_new(
            IndexLanguageVersion::CURRENT,
            name("Users/ByRegion"),
            vec![users_stage()],
            Some(reduce),
            Vec::new(),
            Vec::new(),
            None,
        )
    }

    // A key that the maps do not project cannot group anything.
    let unknown_key = ReduceStage::try_new(
        vec![ReduceKey::try_new("planet", GroupByArrayMode::NotApplicable).expect("valid key")],
        vec![
            AggregationBinding::try_new("age", AggregationFunction::Sum, Some("age"))
                .expect("valid sum"),
        ],
    )
    .expect("stage validates alone");
    assert_eq!(build(unknown_key), Err(IndexDefinitionError::ReduceKeyNotProjected));

    // No keys is not a grouping.
    let no_keys = ReduceStage::try_new(
        Vec::new(),
        vec![
            AggregationBinding::try_new("age", AggregationFunction::Sum, Some("age"))
                .expect("valid sum"),
        ],
    );
    assert_eq!(no_keys, Err(IndexDefinitionError::NoReduceKeys));

    // Duplicate keys collapse groups silently; the language refuses them.
    let duplicate_keys = ReduceStage::try_new(
        vec![
            ReduceKey::try_new("region", GroupByArrayMode::NotApplicable).expect("valid key"),
            ReduceKey::try_new("region", GroupByArrayMode::ByContent).expect("valid key"),
        ],
        vec![
            AggregationBinding::try_new("age", AggregationFunction::Sum, Some("age"))
                .expect("valid sum"),
        ],
    );
    assert_eq!(duplicate_keys, Err(IndexDefinitionError::DuplicateReduceKey));

    // count() takes no input; sum() requires one, and the input must not be
    // a grouping key.
    let count_with_input =
        AggregationBinding::try_new("count", AggregationFunction::Count, Some("age"));
    assert_eq!(count_with_input, Err(IndexDefinitionError::CountHasInput));

    let sum_without_input = AggregationBinding::try_new("age", AggregationFunction::Sum, None);
    assert_eq!(sum_without_input, Err(IndexDefinitionError::SumNeedsInput));

    let sum_of_key = ReduceStage::try_new(
        vec![ReduceKey::try_new("region", GroupByArrayMode::NotApplicable).expect("valid key")],
        vec![
            AggregationBinding::try_new("age", AggregationFunction::Sum, Some("region"))
                .expect("valid binding shape"),
        ],
    )
    .expect("stage validates alone");
    assert_eq!(build(sum_of_key), Err(IndexDefinitionError::AggregationInputIsKey));

    // Every projected field is either a grouping key or an aggregate output;
    // anything else would drop data from the reduce result silently.
    let partial = ReduceStage::try_new(
        vec![ReduceKey::try_new("region", GroupByArrayMode::NotApplicable).expect("valid key")],
        vec![
            AggregationBinding::try_new("count", AggregationFunction::Count, None)
                .expect("valid count"),
        ],
    )
    .expect("stage validates alone");
    assert_eq!(build(partial), Err(IndexDefinitionError::ReduceShapeMismatch));

    // An aggregate output that reuses a projected name twice is a duplicate.
    let duplicate_output = ReduceStage::try_new(
        vec![ReduceKey::try_new("region", GroupByArrayMode::NotApplicable).expect("valid key")],
        vec![
            AggregationBinding::try_new("count", AggregationFunction::Count, None)
                .expect("valid count"),
            AggregationBinding::try_new("count", AggregationFunction::Sum, Some("age"))
                .expect("valid sum shape"),
        ],
    );
    assert_eq!(duplicate_output, Err(IndexDefinitionError::DuplicateAggregationOutput));
}

#[test]
fn array_grouping_modes_are_pinned_to_v1() {
    // V1's GroupByArrayBehavior: ByContent groups by the whole array value,
    // ByIndividualValues groups per element, NotApplicable is the scalar key.
    let by_content = ReduceKey::try_new("region", GroupByArrayMode::ByContent).expect("valid key");
    let by_values =
        ReduceKey::try_new("region", GroupByArrayMode::ByIndividualValues).expect("valid key");
    assert_ne!(by_content, by_values);

    let keys =
        vec![ReduceKey::try_new("tags", GroupByArrayMode::ByIndividualValues).expect("valid key")];
    let tags_stage = MapStage::try_new(
        MapSource::Collection(collection("posts")),
        vec![output_field("tags", "tags"), output_field("likes", "likes")],
        Vec::new(),
    )
    .expect("valid stage");
    let reduce = ReduceStage::try_new(
        keys,
        vec![
            AggregationBinding::try_new("likes", AggregationFunction::Sum, Some("likes"))
                .expect("valid sum"),
        ],
    )
    .expect("valid reduce");
    let definition = StaticIndexDefinition::try_new(
        IndexLanguageVersion::CURRENT,
        name("Posts/ByTag"),
        vec![tags_stage],
        Some(reduce),
        Vec::new(),
        Vec::new(),
        None,
    )
    .expect("valid map-reduce definition");
    assert_eq!(
        definition.reduce().expect("reduce present").keys()[0].array_mode(),
        GroupByArrayMode::ByIndividualValues
    );
    // The grouping mode is part of the definition's identity: changing it is
    // a different index, not a silent reroute.
    let other = StaticIndexDefinition::try_new(
        IndexLanguageVersion::CURRENT,
        name("Posts/ByTag"),
        vec![
            MapStage::try_new(
                MapSource::Collection(collection("posts")),
                vec![output_field("tags", "tags"), output_field("likes", "likes")],
                Vec::new(),
            )
            .expect("valid stage"),
        ],
        Some(
            ReduceStage::try_new(
                vec![ReduceKey::try_new("tags", GroupByArrayMode::ByContent).expect("valid key")],
                vec![
                    AggregationBinding::try_new("likes", AggregationFunction::Sum, Some("likes"))
                        .expect("valid sum"),
                ],
            )
            .expect("valid reduce"),
        ),
        Vec::new(),
        Vec::new(),
        None,
    )
    .expect("valid map-reduce definition");
    assert_ne!(definition.identity_key(), other.identity_key());
    assert_eq!(definition.compare(&other), DefinitionDifferences::REDUCE);
}

#[test]
fn field_options_carry_indexing_storage_and_analysis() {
    let analyzed = IndexFieldOptions::try_new(
        FieldIndexing::Search,
        FieldStorage::Stored,
        Some(AnalyzerName::try_new("standard-language-analyzer").expect("valid analyzer name")),
    )
    .expect("search field with analyzer");
    assert_eq!(analyzed.indexing(), FieldIndexing::Search);
    assert_eq!(analyzed.storage(), FieldStorage::Stored);
    assert_eq!(
        analyzed.analyzer().map(|analyzer| analyzer.as_str()),
        Some("standard-language-analyzer")
    );

    // An analyzer names an analysis strategy for an analyzed (search) field;
    // attaching one elsewhere is refused rather than ignored.
    assert_eq!(
        IndexFieldOptions::try_new(
            FieldIndexing::Default,
            FieldStorage::NotStored,
            Some(AnalyzerName::try_new("x-analyzer").expect("valid analyzer name"))
        ),
        Err(IndexDefinitionError::AnalyzerWithoutSearch)
    );
    assert_eq!(AnalyzerName::try_new(" no-trim "), Err(IndexDefinitionError::InvalidAnalyzerName));

    let options = vec![("region".to_owned(), analyzed)];
    let definition = StaticIndexDefinition::try_new(
        IndexLanguageVersion::CURRENT,
        name("Users/ByRegionText"),
        vec![users_stage()],
        None,
        options,
        Vec::new(),
        None,
    )
    .expect("field options apply to projected fields");
    assert_eq!(definition.field_options().len(), 1);

    // Options must name fields the definition actually produces.
    let unknown = StaticIndexDefinition::try_new(
        IndexLanguageVersion::CURRENT,
        name("Users/ByRegionText"),
        vec![users_stage()],
        None,
        vec![(
            "nickname".to_owned(),
            IndexFieldOptions::try_new(FieldIndexing::Exact, FieldStorage::NotStored, None)
                .expect("valid options"),
        )],
        Vec::new(),
        None,
    );
    assert_eq!(unknown, Err(IndexDefinitionError::FieldOptionUnknownField));

    // On a map-reduce definition the options apply to the reduce output
    // shape, not the map-only shape.
    let reduce_options = vec![(
        "count".to_owned(),
        IndexFieldOptions::try_new(FieldIndexing::Exact, FieldStorage::Stored, None)
            .expect("valid options"),
    )];
    let definition = StaticIndexDefinition::try_new(
        IndexLanguageVersion::CURRENT,
        name("Users/ByRegion"),
        vec![users_stage()],
        Some(regional_reduce()),
        reduce_options,
        Vec::new(),
        None,
    )
    .expect("options apply to reduce outputs");
    let keys: Vec<&str> = definition.field_options().keys().map(String::as_str).collect();
    assert_eq!(keys, vec!["count"]);
}

#[test]
fn dynamic_fields_are_typed_rules_not_runtime_names() {
    let rule = DynamicFieldRule::try_new(
        "attributes.name",
        "attributes.value",
        IndexFieldOptions::try_new(FieldIndexing::Exact, FieldStorage::NotStored, None)
            .expect("valid options"),
    )
    .expect("valid dynamic rule");
    let stage = MapStage::try_new(
        MapSource::Collection(collection("products")),
        vec![output_field("category", "category")],
        vec![rule],
    )
    .expect("stage with dynamic rule");
    let definition = StaticIndexDefinition::try_map(
        IndexLanguageVersion::CURRENT,
        name("Products/ByDynamicAttribute"),
        stage,
    )
    .expect("dynamic rules are part of the contract");
    assert_eq!(definition.map_stages()[0].dynamic_rules().len(), 1);
    assert_eq!(definition.map_stages()[0].dynamic_rules()[0].name_path(), "attributes.name");

    // Dynamic field names are runtime data: they are not part of the static
    // output shape, so nothing static may name them.
    let bad_key = ReduceStage::try_new(
        vec![
            ReduceKey::try_new("attributes.name", GroupByArrayMode::NotApplicable)
                .expect("valid key"),
        ],
        vec![
            AggregationBinding::try_new("category", AggregationFunction::Count, None)
                .expect("valid count"),
        ],
    )
    .expect("stage validates alone");
    let result = StaticIndexDefinition::try_new(
        IndexLanguageVersion::CURRENT,
        name("Products/ByDynamicAttribute"),
        vec![
            MapStage::try_new(
                MapSource::Collection(collection("products")),
                vec![output_field("category", "category")],
                vec![
                    DynamicFieldRule::try_new(
                        "attributes.name",
                        "attributes.value",
                        IndexFieldOptions::try_new(
                            FieldIndexing::Exact,
                            FieldStorage::NotStored,
                            None,
                        )
                        .expect("valid options"),
                    )
                    .expect("valid rule"),
                ],
            )
            .expect("valid stage"),
        ],
        Some(bad_key),
        Vec::new(),
        Vec::new(),
        None,
    );
    assert_eq!(result, Err(IndexDefinitionError::ReduceKeyNotProjected));

    // A dynamic rule with a malformed path is refused at construction.
    assert_eq!(
        DynamicFieldRule::try_new(
            "attributes..name",
            "attributes.value",
            IndexFieldOptions::try_new(FieldIndexing::Exact, FieldStorage::NotStored, None)
                .expect("valid options"),
        ),
        Err(IndexDefinitionError::InvalidFieldPath)
    );
}

#[test]
fn compound_fields_are_a_hint_only_extension() {
    let compound = CompoundField::try_new(vec!["region".to_owned(), "age".to_owned()])
        .expect("valid compound");
    let definition = StaticIndexDefinition::try_new(
        IndexLanguageVersion::CURRENT,
        name("Users/ByRegion"),
        vec![users_stage()],
        None,
        Vec::new(),
        vec![compound],
        None,
    )
    .expect("compound fields compose with maps");
    assert_eq!(definition.compound_fields()[0].members(), ["region", "age"]);

    // Members must exist in the output shape, be distinct, and not repeat as
    // an identical compound.
    assert_eq!(
        CompoundField::try_new(vec!["region".to_owned()]),
        Err(IndexDefinitionError::CompoundTooFewMembers)
    );
    assert_eq!(
        CompoundField::try_new(vec!["region".to_owned(), "region".to_owned()]),
        Err(IndexDefinitionError::CompoundMemberDuplicate)
    );
    // A member the definition does not output is refused at the definition
    // seam, where the output shape is known.
    let unknown_member = StaticIndexDefinition::try_new(
        IndexLanguageVersion::CURRENT,
        name("Users/ByRegion"),
        vec![users_stage()],
        None,
        Vec::new(),
        vec![
            CompoundField::try_new(vec!["region".to_owned(), "nickname".to_owned()])
                .expect("valid shape"),
        ],
        None,
    );
    assert_eq!(unknown_member, Err(IndexDefinitionError::CompoundMemberUnknown));

    let base = StaticIndexDefinition::try_new(
        IndexLanguageVersion::CURRENT,
        name("Users/ByRegion"),
        vec![users_stage()],
        None,
        Vec::new(),
        Vec::new(),
        None,
    )
    .expect("base definition");
    let duplicated = StaticIndexDefinition::try_new(
        IndexLanguageVersion::CURRENT,
        name("Users/ByRegion"),
        vec![users_stage()],
        None,
        Vec::new(),
        vec![
            CompoundField::try_new(vec!["region".to_owned(), "age".to_owned()])
                .expect("valid compound"),
            CompoundField::try_new(vec!["region".to_owned(), "age".to_owned()])
                .expect("valid compound"),
        ],
        None,
    );
    assert_eq!(duplicated, Err(IndexDefinitionError::CompoundDuplicate));
    // The hint is additive: it never changes the definition's output shape.
    assert_eq!(base.output_fields().count(), 2);
}

#[test]
fn output_collections_follow_the_v1_loop_and_pattern_rules() {
    let output = ReduceOutput::try_new(
        collection("regions-summary"),
        Some(
            OutputReferencePattern::try_new("regions/{region}")
                .expect("pattern names a reduce output field"),
        ),
        Some(collection("region-references")),
    )
    .expect("valid output");

    let definition = StaticIndexDefinition::try_new(
        IndexLanguageVersion::CURRENT,
        name("Users/ByRegion"),
        vec![users_stage()],
        Some(regional_reduce()),
        Vec::new(),
        Vec::new(),
        Some(output),
    )
    .expect("valid definition with output collection");
    let output = definition.output().expect("output present");
    assert_eq!(output.collection().as_str(), "regions-summary");
    assert_eq!(output.reference_pattern().expect("pattern present").as_str(), "regions/{region}");
    assert_eq!(output.references_collection().map(|c| c.as_str()), Some("region-references"));

    // Output collections exist only for map-reduce definitions.
    let map_only = StaticIndexDefinition::try_new(
        IndexLanguageVersion::CURRENT,
        name("Users/ByRegion"),
        vec![users_stage()],
        None,
        Vec::new(),
        Vec::new(),
        Some(
            ReduceOutput::try_new(collection("regions-summary"), None, None)
                .expect("output validates alone"),
        ),
    );
    assert_eq!(map_only, Err(IndexDefinitionError::OutputWithoutReduce));

    // Writing reduce outputs into a collection the same index maps loops
    // forever; V1 refuses this at creation.
    let looped = ReduceOutput::try_new(collection("users"), None, None).expect("valid alone");
    let looping = StaticIndexDefinition::try_new(
        IndexLanguageVersion::CURRENT,
        name("Users/ByRegion"),
        vec![users_stage()],
        Some(regional_reduce()),
        Vec::new(),
        Vec::new(),
        Some(looped),
    );
    assert_eq!(looping, Err(IndexDefinitionError::OutputCollectionIsMappedSource));

    // An all-documents map writes into every collection: any output loops.
    let all_documents_stage = MapStage::try_new(
        MapSource::AllDocuments,
        vec![output_field("region", "address.region"), output_field("age", "age")],
        Vec::new(),
    )
    .expect("valid stage");
    let all_documents = StaticIndexDefinition::try_new(
        IndexLanguageVersion::CURRENT,
        name("All/ByRegion"),
        vec![all_documents_stage],
        Some(regional_reduce()),
        Vec::new(),
        Vec::new(),
        Some(
            ReduceOutput::try_new(collection("regions-summary"), None, None)
                .expect("output validates alone"),
        ),
    );
    assert_eq!(all_documents, Err(IndexDefinitionError::AllDocumentsOutputLoop));

    // The references collection must differ from the output collection.
    let same = ReduceOutput::try_new(
        collection("regions-summary"),
        None,
        Some(collection("regions-summary")),
    );
    assert_eq!(same, Err(IndexDefinitionError::OutputCollectionEqualsReferences));

    // Patterns need at least one unique placeholder naming a reduce output
    // field; V1's format suffixes are not in the typed language yet.
    assert_eq!(
        OutputReferencePattern::try_new("regions/no-placeholder"),
        Err(IndexDefinitionError::PatternNoPlaceholder)
    );
    assert_eq!(
        OutputReferencePattern::try_new("regions/{region}/{region}"),
        Err(IndexDefinitionError::PatternDuplicateField)
    );
    assert_eq!(
        OutputReferencePattern::try_new("regions/{region:yyyy-MM-dd}"),
        Err(IndexDefinitionError::PatternFormatUnsupported)
    );

    let unprojected = StaticIndexDefinition::try_new(
        IndexLanguageVersion::CURRENT,
        name("Users/ByRegion"),
        vec![users_stage()],
        Some(regional_reduce()),
        Vec::new(),
        Vec::new(),
        Some(
            ReduceOutput::try_new(
                collection("regions-summary"),
                Some(OutputReferencePattern::try_new("regions/{nickname}").expect("shape valid")),
                None,
            )
            .expect("valid alone"),
        ),
    );
    assert_eq!(unprojected, Err(IndexDefinitionError::PatternFieldNotProjected));

    // One output collection belongs to one index: a second map-reduce
    // definition writing the same collection is a conflict (V1 rule).
    let first = StaticIndexDefinition::try_new(
        IndexLanguageVersion::CURRENT,
        name("Users/ByRegion"),
        vec![users_stage()],
        Some(regional_reduce()),
        Vec::new(),
        Vec::new(),
        Some(
            ReduceOutput::try_new(collection("regions-summary"), None, None).expect("valid output"),
        ),
    )
    .expect("first definition");
    let second = StaticIndexDefinition::try_new(
        IndexLanguageVersion::CURRENT,
        name("Companies/ByRegion"),
        vec![companies_stage()],
        Some(regional_reduce()),
        Vec::new(),
        Vec::new(),
        Some(
            ReduceOutput::try_new(collection("regions-summary"), None, None).expect("valid output"),
        ),
    )
    .expect("second definition");
    assert!(first.output_collection_conflicts_with(&second));
    let elsewhere =
        ReduceOutput::try_new(collection("company-regions"), None, None).expect("valid");
    let second = StaticIndexDefinition::try_new(
        IndexLanguageVersion::CURRENT,
        name("Companies/ByRegion"),
        vec![companies_stage()],
        Some(regional_reduce()),
        Vec::new(),
        Vec::new(),
        Some(elsewhere),
    )
    .expect("valid second definition");
    assert!(!first.output_collection_conflicts_with(&second));
}

#[test]
fn definition_comparison_records_drift_and_reindex_scope() {
    let base = StaticIndexDefinition::try_new(
        IndexLanguageVersion::CURRENT,
        name("Users/ByRegion"),
        vec![users_stage()],
        Some(regional_reduce()),
        Vec::new(),
        Vec::new(),
        None,
    )
    .expect("base definition");

    // Identical content compares as no difference.
    let same = StaticIndexDefinition::try_new(
        IndexLanguageVersion::CURRENT,
        name("Users/ByRegion"),
        vec![users_stage()],
        Some(regional_reduce()),
        Vec::new(),
        Vec::new(),
        None,
    )
    .expect("same definition");
    assert_eq!(base.compare(&same), DefinitionDifferences::NONE);
    assert!(!base.compare(&same).reindex_required());

    // Changing the reduce stage is reindex-requiring drift.
    let different_reduce = StaticIndexDefinition::try_new(
        IndexLanguageVersion::CURRENT,
        name("Users/ByRegion"),
        vec![users_stage()],
        Some(
            ReduceStage::try_new(
                vec![
                    ReduceKey::try_new("region", GroupByArrayMode::NotApplicable)
                        .expect("valid key"),
                ],
                vec![
                    AggregationBinding::try_new("age", AggregationFunction::Sum, Some("age"))
                        .expect("valid sum"),
                ],
            )
            .expect("valid reduce"),
        ),
        Vec::new(),
        Vec::new(),
        None,
    )
    .expect("drifted definition");
    let drift = base.compare(&different_reduce);
    assert!(drift.reduce());
    assert!(drift.reindex_required());

    // Changing field options is reindex-requiring drift on the fields.
    let with_options = StaticIndexDefinition::try_new(
        IndexLanguageVersion::CURRENT,
        name("Users/ByRegion"),
        vec![users_stage()],
        Some(regional_reduce()),
        vec![(
            "region".to_owned(),
            IndexFieldOptions::try_new(FieldIndexing::Exact, FieldStorage::NotStored, None)
                .expect("valid options"),
        )],
        Vec::new(),
        None,
    )
    .expect("definition with options");
    let drift = base.compare(&with_options);
    assert!(drift.fields());
    assert!(drift.reindex_required());

    // A compound-field change is a hint change: no reindex, exactly as V1's
    // reindex mask excludes compound fields.
    let with_compound = StaticIndexDefinition::try_new(
        IndexLanguageVersion::CURRENT,
        name("Users/ByRegion"),
        vec![users_stage()],
        Some(regional_reduce()),
        Vec::new(),
        vec![
            CompoundField::try_new(vec!["region".to_owned(), "age".to_owned()])
                .expect("valid compound"),
        ],
        None,
    )
    .expect("definition with compound");
    let drift = base.compare(&with_compound);
    assert!(drift.compound_fields());
    assert!(!drift.reindex_required());

    // Changing the map stages is reindex-requiring drift.
    let wider_stage = MapStage::try_new(
        MapSource::Collection(collection("users")),
        vec![
            output_field("region", "address.region"),
            output_field("age", "age"),
            output_field("tenure", "tenure"),
        ],
        Vec::new(),
    )
    .expect("valid stage");
    let wider = StaticIndexDefinition::try_new(
        IndexLanguageVersion::CURRENT,
        name("Users/ByRegion"),
        vec![wider_stage],
        Some(
            ReduceStage::try_new(
                vec![
                    ReduceKey::try_new("region", GroupByArrayMode::NotApplicable)
                        .expect("valid key"),
                ],
                vec![
                    AggregationBinding::try_new("age", AggregationFunction::Sum, Some("age"))
                        .expect("valid sum"),
                    AggregationBinding::try_new("tenure", AggregationFunction::Sum, Some("tenure"))
                        .expect("valid sum"),
                ],
            )
            .expect("valid reduce"),
        ),
        Vec::new(),
        Vec::new(),
        None,
    )
    .expect("wider definition");
    assert!(base.compare(&wider).maps());
    assert!(base.compare(&wider).reindex_required());
}

#[test]
fn identity_key_is_deterministic_and_content_addressed() {
    let build = |stage_order_flipped: bool| {
        let stages = if stage_order_flipped {
            vec![companies_stage(), users_stage()]
        } else {
            vec![users_stage(), companies_stage()]
        };
        StaticIndexDefinition::try_new(
            IndexLanguageVersion::CURRENT,
            name("Parties/ByRegion"),
            stages,
            Some(regional_reduce()),
            Vec::new(),
            Vec::new(),
            None,
        )
        .expect("valid definition")
    };

    // Multi-map order is not part of the identity: V1 maps are a set.
    let ordered = build(false);
    let flipped = build(true);
    assert_eq!(ordered, flipped);
    assert_eq!(ordered.identity_key(), flipped.identity_key());

    // Different content, same name: different identity keys.
    let altered = StaticIndexDefinition::try_new(
        IndexLanguageVersion::CURRENT,
        name("Parties/ByRegion"),
        vec![users_stage(), companies_stage()],
        None,
        Vec::new(),
        Vec::new(),
        None,
    )
    .expect("map-only variant");
    assert_ne!(ordered.identity_key(), altered.identity_key());

    // The identity is inspectable without secrets: it renders the version,
    // name, and shape only.
    assert!(ordered.identity_key().contains("idx/1.0"));
    assert!(ordered.identity_key().contains("Parties/ByRegion"));
}

#[test]
fn option_maps_are_canonicalized_into_stable_order() {
    let options = vec![
        (
            "age".to_owned(),
            IndexFieldOptions::try_new(FieldIndexing::Exact, FieldStorage::NotStored, None)
                .expect("valid options"),
        ),
        (
            "region".to_owned(),
            IndexFieldOptions::try_new(FieldIndexing::Default, FieldStorage::Stored, None)
                .expect("valid options"),
        ),
    ];
    let definition = StaticIndexDefinition::try_map(
        IndexLanguageVersion::CURRENT,
        name("Users/ByRegion"),
        users_stage(),
    )
    .and_then(|definition| definition.with_field_options(options))
    .expect("options apply");
    let map: BTreeMap<&str, FieldIndexing> = definition
        .field_options()
        .iter()
        .map(|(field, options)| (field.as_str(), options.indexing()))
        .collect();
    assert_eq!(map.get("region"), Some(&FieldIndexing::Default));
    assert_eq!(map.get("age"), Some(&FieldIndexing::Exact));

    // Duplicate option entries for one field are refused, not merged.
    let duplicated = vec![
        (
            "age".to_owned(),
            IndexFieldOptions::try_new(FieldIndexing::Exact, FieldStorage::NotStored, None)
                .expect("valid options"),
        ),
        (
            "age".to_owned(),
            IndexFieldOptions::try_new(FieldIndexing::Search, FieldStorage::NotStored, None)
                .expect("valid options"),
        ),
    ];
    let base = StaticIndexDefinition::try_map(
        IndexLanguageVersion::CURRENT,
        name("Users/ByRegion"),
        users_stage(),
    )
    .expect("base definition");
    assert_eq!(
        base.clone().with_field_options(duplicated),
        Err(IndexDefinitionError::FieldOptionDuplicate)
    );
}
