//! In-crate focused tests for the typed static index definition contract.
//!
//! The public-path contract tests live in `tests/index/`; these tests pin
//! the crate-internal invariants: the reserved auto namespace rule agrees
//! with the query optimizer's catalog rule, and every language bound fails
//! closed instead of truncating.

use super::definitions::{
    AggregationBinding, AggregationFunction, CompoundField, DynamicFieldRule, FieldIndexing,
    FieldStorage, GroupByArrayMode, IndexDefinitionError, IndexFieldOptions, IndexLanguageVersion,
    MAX_COMPOUND_FIELDS, MAX_COMPOUND_MEMBERS, MAX_DYNAMIC_FIELD_RULES, MAX_MAP_OUTPUT_FIELDS,
    MAX_MAP_STAGES, MAX_REDUCE_KEYS, MapExpression, MapOutputField, MapSource, MapStage, ReduceKey,
    ReduceStage, StaticIndexDefinition, StaticIndexName,
};
use crate::query::optimizer::AUTO_NAMESPACE_PREFIX;
use crate::types::CollectionName;

fn collection(name: &str) -> CollectionName {
    CollectionName::try_new(name).expect("valid collection identifier")
}

fn index_name(value: &str) -> StaticIndexName {
    StaticIndexName::try_new(value).expect("valid static index name")
}

fn field(name: &str, path: &str) -> MapOutputField {
    MapOutputField::try_new(name, MapExpression::try_field(path).expect("valid path"))
        .expect("valid output field")
}

fn stage(collection_text: &str, fields: Vec<MapOutputField>) -> MapStage {
    MapStage::try_new(MapSource::Collection(collection(collection_text)), fields, Vec::new())
        .expect("valid map stage")
}

fn shape(count: usize) -> Vec<MapOutputField> {
    (0..count).map(|index| field(&format!("field{index}"), &format!("path{index}"))).collect()
}

fn reduce_over(first: &str) -> ReduceStage {
    ReduceStage::try_new(
        vec![ReduceKey::try_new(first, GroupByArrayMode::NotApplicable).expect("valid key")],
        vec![
            AggregationBinding::try_new("field1", AggregationFunction::Sum, Some("field1"))
                .expect("valid sum"),
        ],
    )
    .expect("valid reduce stage")
}

#[test]
fn reserved_prefix_agrees_with_the_catalog_rule() {
    // The catalog refuses explicit registration under the optimizer's
    // reserved auto namespace; the definition language must refuse the same
    // names before they ever reach it, case-insensitively as V1 does.
    assert_eq!(AUTO_NAMESPACE_PREFIX, "Auto/");
    for casing in ["AUTO/", "Auto/", "auto/", "aUtO/"] {
        let squatted = format!("{casing}Users/ByAge");
        assert_eq!(
            StaticIndexName::try_new(&squatted),
            Err(IndexDefinitionError::ReservedAutoName),
            "name under '{casing}' must be refused"
        );
    }
    // A static name may still contain a slash outside the reserved prefix.
    assert!(StaticIndexName::try_new("Orders/ByCompany").is_ok());
}

#[test]
fn stage_bound_fails_closed() {
    // One more distinct-source stage than the bound allows.
    let stages: Vec<MapStage> =
        (0..=MAX_MAP_STAGES).map(|index| stage(&format!("collection{index}"), shape(2))).collect();
    assert_eq!(
        StaticIndexDefinition::try_new(
            IndexLanguageVersion::CURRENT,
            index_name("Bounded/Stages"),
            stages,
            None,
            Vec::new(),
            Vec::new(),
            None,
        )
        .err(),
        Some(IndexDefinitionError::TooManyMaps)
    );

    // The bound itself is reachable: exactly the bound passes.
    let stages: Vec<MapStage> =
        (0..MAX_MAP_STAGES).map(|index| stage(&format!("collection{index}"), shape(2))).collect();
    assert!(
        StaticIndexDefinition::try_new(
            IndexLanguageVersion::CURRENT,
            index_name("Bounded/Stages"),
            stages,
            None,
            Vec::new(),
            Vec::new(),
            None,
        )
        .is_ok()
    );
}

#[test]
fn output_field_and_key_bounds_fail_closed() {
    assert_eq!(
        MapStage::try_new(
            MapSource::Collection(collection("users")),
            shape(MAX_MAP_OUTPUT_FIELDS + 1),
            Vec::new(),
        )
        .err(),
        Some(IndexDefinitionError::TooManyOutputFields)
    );
    assert!(
        MapStage::try_new(
            MapSource::Collection(collection("users")),
            shape(MAX_MAP_OUTPUT_FIELDS),
            Vec::new(),
        )
        .is_ok()
    );

    let keys: Vec<ReduceKey> = (0..=MAX_REDUCE_KEYS)
        .map(|index| {
            ReduceKey::try_new(&format!("field{index}"), GroupByArrayMode::NotApplicable)
                .expect("valid key")
        })
        .collect();
    assert_eq!(
        ReduceStage::try_new(keys, Vec::new()).err(),
        Some(IndexDefinitionError::TooManyReduceKeys)
    );
}

#[test]
fn dynamic_rule_bound_fails_closed() {
    let options = IndexFieldOptions::try_new(FieldIndexing::Exact, FieldStorage::NotStored, None)
        .expect("valid options");
    let rules: Vec<DynamicFieldRule> = (0..=MAX_DYNAMIC_FIELD_RULES)
        .map(|index| {
            DynamicFieldRule::try_new(
                &format!("attrs.{index}.name"),
                &format!("attrs.{index}.value"),
                options.clone(),
            )
            .expect("valid rule")
        })
        .collect();
    assert_eq!(
        MapStage::try_new(MapSource::Collection(collection("users")), shape(1), rules,).err(),
        Some(IndexDefinitionError::TooManyDynamicRules)
    );
}

#[test]
fn compound_bounds_fail_closed() {
    // Distinct compounds beyond the bound.
    let compounds: Vec<CompoundField> = (0..=MAX_COMPOUND_FIELDS)
        .map(|index| {
            CompoundField::try_new(vec![
                format!("field{index}"),
                format!("field{}", (index + 1) % (MAX_COMPOUND_FIELDS + 1)),
            ])
            .expect("valid compound")
        })
        .collect();
    assert_eq!(
        StaticIndexDefinition::try_new(
            IndexLanguageVersion::CURRENT,
            index_name("Bounded/Compounds"),
            vec![stage("users", shape(9))],
            None,
            Vec::new(),
            compounds,
            None,
        )
        .err(),
        Some(IndexDefinitionError::TooManyCompounds)
    );

    // An identical compound twice is a duplicate, not a second hint.
    assert_eq!(
        StaticIndexDefinition::try_new(
            IndexLanguageVersion::CURRENT,
            index_name("Bounded/Compounds"),
            vec![stage("users", shape(2))],
            None,
            Vec::new(),
            vec![
                CompoundField::try_new(vec!["field0".to_owned(), "field1".to_owned()])
                    .expect("valid compound"),
                CompoundField::try_new(vec!["field0".to_owned(), "field1".to_owned()])
                    .expect("valid compound"),
            ],
            None,
        )
        .err(),
        Some(IndexDefinitionError::CompoundDuplicate)
    );

    assert_eq!(
        CompoundField::try_new(
            (0..=MAX_COMPOUND_MEMBERS).map(|index| format!("field{index}")).collect()
        )
        .err(),
        Some(IndexDefinitionError::CompoundTooManyMembers)
    );
}

#[test]
fn version_constant_is_wired_to_the_language_identity() {
    assert_eq!(IndexLanguageVersion::CURRENT.as_str(), "idx/1.0");
    assert_eq!(IndexLanguageVersion::CURRENT.major(), 1);
    assert_eq!(IndexLanguageVersion::CURRENT.minor(), 0);
}

#[test]
fn reduce_shape_covers_keys_and_inputs_exactly() {
    // The map shape must equal the grouping keys plus the aggregation
    // inputs: nothing projected dangles and nothing groups over data the
    // maps never produce.
    let two = shape(2);
    let definition = StaticIndexDefinition::try_new(
        IndexLanguageVersion::CURRENT,
        index_name("Users/ByShape"),
        vec![stage("users", two)],
        Some(reduce_over("field0")),
        Vec::new(),
        Vec::new(),
        None,
    )
    .expect("shape covers keys and inputs");
    let shape: Vec<&str> = definition.output_fields().collect();
    assert_eq!(shape, vec!["field0", "field1"]);
}
