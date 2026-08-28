//! The typed static index definition language (GAP-Q06).
//!
//! This module owns the versioned, typed contract through which a static
//! index is authored in V2: map stages (including multi-map), the optional
//! reduce stage that turns maps into static aggregation, per-field options
//! (indexing, storage, analysis), dynamic-field rules, compound-field
//! hints, and map-reduce output collections. The decision record — the
//! language choice, the V1 anchors, and the rejected alternatives — is
//! `docs/indexing/static-index-definitions.md`; the WS-PARITY proposals it
//! feeds are `docs/parity/gapq06-ws-parity-proposals.md`.
//!
//! The language replaces V1's code-authored index definitions with closed
//! typed values, per the charter's no-arbitrary-code rule for V2 languages
//! (PD-2026-08-26-08 D2): a map stage projects document field paths and the
//! document identity, never executable code; a reduce stage groups by
//! projected keys and aggregates with the typed count/sum vocabulary; field
//! options are enums, not string-composed flags. Every constructor fails
//! closed with a typed, content-safe error: an error names the violated
//! rule, never a document value.
//!
//! Versioning follows the TQL pattern: every definition carries the
//! `idx/MAJOR.MINOR` language version it was authored against, and the
//! engine accepts only the version it speaks — any other version fails
//! closed before any other validation runs. Additive grammar changes bump
//! the minor; breaking changes bump the major.
//!
//! Semantics pinned to V1 where adopted (see the decision record for the
//! per-row evidence): static names refuse the reserved auto namespace
//! case-insensitively; multi-map stages project one common output shape
//! over unique sources; the reduce output shape covers the map shape
//! exactly through grouping keys and aggregation inputs, so one output row
//! exists per distinct grouping-key value (the static `GROUP BY`/`DISTINCT`
//! semantic); array grouping modes carry V1's group-by array behavior; and
//! output collections refuse the loops and duplicate placeholders V1
//! refuses at index creation.

use std::collections::BTreeMap;

use crate::types::CollectionName;

/// The identity prefix of the index definition language version.
pub const INDEX_LANGUAGE_PREFIX: &str = "idx/";

/// The maximum number of map stages in one definition (multi-map bound).
///
/// An initial engine bound, tunable with measured evidence; V1 keeps a set
/// of maps with no engine bound, and the typed language keeps one so a
/// definition stays bounded engine state.
pub const MAX_MAP_STAGES: usize = 8;

/// The maximum number of output fields one map stage projects.
///
/// An initial engine bound aligned with the seam's clause bounds; the
/// catalog's own field bound stays the authority for registered shapes.
pub const MAX_MAP_OUTPUT_FIELDS: usize = 32;

/// The maximum number of grouping keys in one reduce stage.
pub const MAX_REDUCE_KEYS: usize = 8;

/// The maximum number of aggregate bindings in one reduce stage.
///
/// An initial engine bound aligned with the map output field bound:
/// aggregate outputs are the reduce stage's half of the output width, so
/// a reduce stage stays bounded engine state exactly as a map stage is.
pub const MAX_AGGREGATIONS: usize = MAX_MAP_OUTPUT_FIELDS;

/// The maximum number of dynamic-field rules one map stage declares.
pub const MAX_DYNAMIC_FIELD_RULES: usize = 4;

/// The maximum number of compound-field hints in one definition.
pub const MAX_COMPOUND_FIELDS: usize = 8;

/// The maximum number of members in one compound-field hint.
pub const MAX_COMPOUND_MEMBERS: usize = 8;

/// The byte bound of one static index name, shared with the seam's
/// identifier rule.
pub const MAX_NAME_BYTES: usize = 255;

/// The typed, content-safe error vocabulary of the definition language.
///
/// Every variant names the violated contract rule. No variant carries
/// document data, field values, or identifiers beyond what the rule needs,
/// so errors stay safe to observe and record.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum IndexDefinitionError {
    /// The version is not a well-formed `idx/MAJOR.MINOR` identity, or is
    /// not the language version this engine speaks.
    UnknownVersion,
    /// The static index name is empty, oversized, carries control
    /// characters, whitespace padding, a double slash, misplaced dots, or a
    /// character outside the name alphabet.
    InvalidName,
    /// The name squats the reserved engine-owned auto namespace, which
    /// static definitions may never occupy.
    ReservedAutoName,
    /// The definition carries no map stage.
    NoMaps,
    /// The definition carries more map stages than the language bound.
    TooManyMaps,
    /// Two map stages draw from the same source, which would double-count
    /// every document in that source.
    DuplicateMapSource,
    /// An all-documents map stage is combined with collection stages; the
    /// all-documents source already covers every collection.
    AllDocumentsWithCollections,
    /// Two map stages project different output shapes; a multi-map
    /// definition emits one common result shape.
    MapShapeMismatch,
    /// An output field name is empty, oversized, or carries control
    /// characters or whitespace padding.
    InvalidFieldName,
    /// A map stage projects one output field name twice.
    DuplicateOutputField,
    /// A map stage carries no output field.
    NoOutputFields,
    /// A map stage projects more output fields than the language bound.
    TooManyOutputFields,
    /// A document field path is empty, oversized, carries control
    /// characters, whitespace padding, or an empty dot segment.
    InvalidFieldPath,
    /// A map stage declares more dynamic-field rules than the bound.
    TooManyDynamicRules,
    /// A reduce stage declares no grouping key; a map-reduce index is a
    /// grouping by construction.
    NoReduceKeys,
    /// A reduce stage declares more grouping keys than the bound.
    TooManyReduceKeys,
    /// A reduce stage declares more aggregate bindings than the bound.
    TooManyAggregations,
    /// A grouping key names a field the map stages do not project.
    ReduceKeyNotProjected,
    /// A reduce stage groups twice by the same field.
    DuplicateReduceKey,
    /// The `count` aggregation was given an input; count aggregates the
    /// grouped rows themselves.
    CountHasInput,
    /// The `sum` aggregation was given no input; sum aggregates a named
    /// projected field.
    SumNeedsInput,
    /// An aggregation input names a field the map stages do not project.
    AggregationInputNotProjected,
    /// An aggregation input names a grouping key, which the grouping
    /// already fixes per output row.
    AggregationInputIsKey,
    /// An aggregation output reuses a grouping key or another output name.
    DuplicateAggregationOutput,
    /// The map shape is not covered exactly by grouping keys plus
    /// aggregation inputs: a projected field dangles, or the grouping
    /// needs data the maps never produce.
    ReduceShapeMismatch,
    /// An output collection was declared on a definition without a reduce
    /// stage; only map-reduce results are published to a collection.
    OutputWithoutReduce,
    /// The output collection is a collection this index maps, which would
    /// loop the index over its own output.
    OutputCollectionIsMappedSource,
    /// The definition maps all documents and declares an output collection;
    /// every collection is a mapped source, so any output loops.
    AllDocumentsOutputLoop,
    /// The references collection equals the output collection.
    OutputCollectionEqualsReferences,
    /// A field option names a field the definition does not output.
    FieldOptionUnknownField,
    /// Field options declare the same field twice.
    FieldOptionDuplicate,
    /// An analyzer name was attached to a field that is not analyzed
    /// (`FieldIndexing::Search`).
    AnalyzerWithoutSearch,
    /// An analyzer name is empty, oversized, or carries control characters
    /// or whitespace padding.
    InvalidAnalyzerName,
    /// A compound-field hint names fewer than two members.
    CompoundTooFewMembers,
    /// A compound-field hint names more members than the bound.
    CompoundTooManyMembers,
    /// A compound-field member names a field the definition does not
    /// output.
    CompoundMemberUnknown,
    /// A compound-field hint names the same member twice.
    CompoundMemberDuplicate,
    /// The definition declares the same compound-field hint twice.
    CompoundDuplicate,
    /// The definition declares more compound-field hints than the bound.
    TooManyCompounds,
    /// An output reference pattern carries no field placeholder.
    PatternNoPlaceholder,
    /// An output reference pattern repeats one field placeholder.
    PatternDuplicateField,
    /// An output reference placeholder carries a format suffix, which the
    /// typed language does not define.
    PatternFormatUnsupported,
    /// An output reference placeholder is malformed.
    PatternPlaceholderInvalid,
    /// An output reference pattern names a field the reduce stage does not
    /// output.
    PatternFieldNotProjected,
    /// A family map source carries an invalid name: empty, oversized, with
    /// control characters or whitespace padding, or — for a series source —
    /// carrying the reserved record separator.
    InvalidSourceName,
    /// Map stages mix source kinds: document sources with a family source,
    /// or the counters and time-series families in one definition. V1 keeps
    /// one definition type per source kind (`CountersIndexDefinition`,
    /// `TimeSeriesIndexDefinition`), and the typed language keeps the same
    /// one-kind rule.
    SourceKindMismatch,
    /// The all-entries family source is combined with a named source of the
    /// same kind: the all-entries source already covers every entry the
    /// named source maps, so every mapped entry would be double-counted.
    OverlappingFamilySources,
}

/// The `idx/MAJOR.MINOR` version identity carried by every definition.
///
/// The version is data: any well-formed identity parses, and the engine
/// accepts only [`IndexLanguageVersion::CURRENT`] when a definition is
/// constructed, so a definition authored against a version the engine does
/// not speak fails closed before any other validation runs.
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct IndexLanguageVersion {
    major: u32,
    minor: u32,
}

impl IndexLanguageVersion {
    /// The language version this engine speaks.
    pub const CURRENT: Self = Self { major: 1, minor: 0 };

    /// Parses one `idx/MAJOR.MINOR` version identity.
    ///
    /// # Errors
    ///
    /// Returns [`IndexDefinitionError::UnknownVersion`] unless the text is
    /// exactly the prefix, a major, a dot, and a minor, both numeric
    /// without leading zeros.
    pub fn try_parse(text: &str) -> Result<Self, IndexDefinitionError> {
        let rest =
            text.strip_prefix(INDEX_LANGUAGE_PREFIX).ok_or(IndexDefinitionError::UnknownVersion)?;
        let (major, minor) = rest.split_once('.').ok_or(IndexDefinitionError::UnknownVersion)?;
        if minor.contains('.') {
            return Err(IndexDefinitionError::UnknownVersion);
        }
        Ok(Self { major: parse_version_part(major)?, minor: parse_version_part(minor)? })
    }

    /// Returns the canonical `idx/MAJOR.MINOR` rendering.
    #[must_use]
    pub fn as_str(self) -> String {
        format!("{INDEX_LANGUAGE_PREFIX}{}.{}", self.major, self.minor)
    }

    /// Returns the major grammar version.
    #[must_use]
    pub const fn major(self) -> u32 {
        self.major
    }

    /// Returns the minor grammar version.
    #[must_use]
    pub const fn minor(self) -> u32 {
        self.minor
    }
}

/// Parses one numeric version part without leading zeros.
fn parse_version_part(part: &str) -> Result<u32, IndexDefinitionError> {
    if part.is_empty()
        || part.len() > 1 && part.starts_with('0')
        || !part.bytes().all(|b| b.is_ascii_digit())
    {
        return Err(IndexDefinitionError::UnknownVersion);
    }
    part.parse::<u32>().map_err(|_| IndexDefinitionError::UnknownVersion)
}

/// The reserved namespace prefix of engine-owned auto indexes, spelled as
/// the catalog spells it; static names refuse it case-insensitively.
const AUTO_NAMESPACE: &str = "auto/";

/// The unit separator used inside deterministic identity renderings.
const IDENTITY_SEPARATOR: char = '\u{1f}';

/// The record separator used inside deterministic identity renderings.
const IDENTITY_GROUP: char = '\u{1e}';

/// A validated static index name.
///
/// Names are non-empty, at most [`MAX_NAME_BYTES`] bytes, free of control
/// characters and whitespace padding, and use the alphabet V1 allows static
/// index names: letters, digits, underscore, hyphen, dot, and slash — with
/// no double slash, no misplaced dot, and no occupancy of the reserved
/// engine-owned auto namespace (compared case-insensitively, as V1 does).
#[derive(Clone, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct StaticIndexName(String);

impl StaticIndexName {
    /// Creates one validated static index name.
    ///
    /// # Errors
    ///
    /// Returns [`IndexDefinitionError::InvalidName`] for an empty,
    /// oversized, control-carrying, whitespace-padded, or malformed name,
    /// and [`IndexDefinitionError::ReservedAutoName`] when the name squats
    /// the reserved auto namespace.
    pub fn try_new(value: &str) -> Result<Self, IndexDefinitionError> {
        validate_name_text(value)?;
        if value.contains("//")
            || value.starts_with('.')
            || value.ends_with('.')
            || value.contains("..")
            || !value
                .chars()
                .all(|c| c.is_ascii_alphanumeric() || matches!(c, '_' | '-' | '.' | '/'))
        {
            return Err(IndexDefinitionError::InvalidName);
        }
        if value.to_ascii_lowercase().starts_with(AUTO_NAMESPACE) {
            return Err(IndexDefinitionError::ReservedAutoName);
        }
        Ok(Self(value.to_owned()))
    }

    /// Returns the canonical name text.
    #[must_use]
    pub fn as_str(&self) -> &str {
        &self.0
    }
}

/// Validates the shared text shape of names and field paths.
fn validate_name_text(value: &str) -> Result<(), IndexDefinitionError> {
    if value.is_empty()
        || value.len() > MAX_NAME_BYTES
        || value.chars().any(char::is_control)
        || value.trim() != value
    {
        return Err(IndexDefinitionError::InvalidName);
    }
    Ok(())
}

/// Validates one output field name.
fn validate_field_name_text(value: &str) -> Result<(), IndexDefinitionError> {
    validate_name_text(value).map_err(|_| IndexDefinitionError::InvalidFieldName)
}

/// Validates one document field path: the name rule plus non-empty dot
/// segments.
fn validate_field_path_text(value: &str) -> Result<(), IndexDefinitionError> {
    validate_name_text(value).map_err(|_| IndexDefinitionError::InvalidFieldPath)?;
    if value.starts_with('.') || value.ends_with('.') || value.contains("..") {
        return Err(IndexDefinitionError::InvalidFieldPath);
    }
    Ok(())
}

/// One source a map stage draws its input from: documents of one collection,
/// every document, or the entries of one Data Family (GAP-Q19).
///
/// The family forms are V1's map-source spellings — `counters` /
/// `counters.{name}` and `timeSeries` / `timeSeries.{name}` — where the
/// name-less form maps every entry of the family and the named form one
/// name across every document. The mapped input space is V1's per-family
/// entry shape: `(DocumentId, Name, Value)` for counters, the summarized
/// segment fields for time-series (decision record D2).
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum MapSource {
    /// Every document of one named collection.
    Collection(CollectionName),
    /// Every document of the tenant, regardless of collection.
    AllDocuments,
    /// Live counter entries of the tenant, or one counter name across every
    /// document when a name is carried.
    Counters {
        /// The restricted counter name, or `None` for every entry.
        name: Option<String>,
    },
    /// Retained time-series entries of the tenant, or one series name
    /// across every document when a name is carried.
    TimeSeries {
        /// The restricted series name, or `None` for every entry.
        name: Option<String>,
    },
}

impl MapSource {
    /// Creates the every-entry counters source (V1 `counters`).
    #[must_use]
    pub fn counters() -> Self {
        Self::Counters { name: None }
    }

    /// Creates the named counters source (V1 `counters.{name}`).
    ///
    /// # Errors
    ///
    /// Returns [`IndexDefinitionError::InvalidSourceName`] for an invalid
    /// name.
    pub fn counters_named(name: &str) -> Result<Self, IndexDefinitionError> {
        validate_source_name(name, false)?;
        Ok(Self::Counters { name: Some(name.to_owned()) })
    }

    /// Creates the every-entry time-series source (V1 `timeSeries`).
    #[must_use]
    pub fn time_series() -> Self {
        Self::TimeSeries { name: None }
    }

    /// Creates the named time-series source (V1 `timeSeries.{name}`).
    ///
    /// # Errors
    ///
    /// Returns [`IndexDefinitionError::InvalidSourceName`] for an invalid
    /// name or one carrying the reserved record separator.
    pub fn time_series_named(name: &str) -> Result<Self, IndexDefinitionError> {
        validate_source_name(name, true)?;
        Ok(Self::TimeSeries { name: Some(name.to_owned()) })
    }

    /// Returns the source collection, or `None` for the all-documents and
    /// family sources.
    #[must_use]
    pub const fn collection(&self) -> Option<&CollectionName> {
        match self {
            Self::Collection(collection) => Some(collection),
            _ => None,
        }
    }

    /// Reports whether this is a family source (GAP-Q19).
    #[must_use]
    pub const fn is_family(&self) -> bool {
        matches!(self, Self::Counters { .. } | Self::TimeSeries { .. })
    }

    /// Returns the canonical identity of this source inside identity
    /// renderings: all documents first, then collections in name order,
    /// then the family sources in their V1 map-source spelling.
    fn identity(&self) -> String {
        match self {
            Self::Collection(collection) => collection.as_str().to_owned(),
            Self::AllDocuments => "\u{0}all-documents".to_owned(),
            Self::Counters { name } => match name {
                None => "counters".to_owned(),
                Some(name) => format!("counters.{name}"),
            },
            Self::TimeSeries { name } => match name {
                None => "timeSeries".to_owned(),
                Some(name) => format!("timeSeries.{name}"),
            },
        }
    }
}

/// Validates one family source name with the shared name rule; a series
/// name additionally rejects the reserved record separator, the same rule
/// the family seam's series names apply.
fn validate_source_name(name: &str, series: bool) -> Result<(), IndexDefinitionError> {
    validate_name_text(name).map_err(|_| IndexDefinitionError::InvalidSourceName)?;
    if series && name.contains('/') {
        return Err(IndexDefinitionError::InvalidSourceName);
    }
    Ok(())
}

/// One typed projection a map stage applies to every document it maps.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum MapExpression {
    /// Projects one document field path, dot paths included.
    Field {
        /// The validated document field path.
        path: String,
    },
    /// Projects the document identity.
    DocumentIdentity,
}

impl MapExpression {
    /// Creates one field-path projection.
    ///
    /// # Errors
    ///
    /// Returns [`IndexDefinitionError::InvalidFieldPath`] for a malformed
    /// path.
    pub fn try_field(path: &str) -> Result<Self, IndexDefinitionError> {
        validate_field_path_text(path)?;
        Ok(Self::Field { path: path.to_owned() })
    }

    /// Creates the document-identity projection.
    #[must_use]
    pub const fn identity() -> Self {
        Self::DocumentIdentity
    }
}

/// One output field of a map stage: the name the map result carries and
/// the typed projection producing it.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct MapOutputField {
    name: String,
    expression: MapExpression,
}

impl MapOutputField {
    /// Creates one output field binding.
    ///
    /// # Errors
    ///
    /// Returns [`IndexDefinitionError::InvalidFieldName`] for a malformed
    /// output name.
    pub fn try_new(name: &str, expression: MapExpression) -> Result<Self, IndexDefinitionError> {
        validate_field_name_text(name)?;
        Ok(Self { name: name.to_owned(), expression })
    }

    /// Returns the output field name.
    #[must_use]
    pub fn name(&self) -> &str {
        &self.name
    }

    /// Returns the typed projection.
    #[must_use]
    pub const fn expression(&self) -> &MapExpression {
        &self.expression
    }
}

/// One dynamic-field rule: the typed replacement for V1's runtime-named
/// index fields.
///
/// For each mapped document, the value at `value_path` is indexed under a
/// field name resolved at index time from `name_path`, with one options
/// value applied to every field the rule creates. The created names are
/// runtime data: nothing static — grouping keys, field options, compound
/// members — may name them.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct DynamicFieldRule {
    name_path: String,
    value_path: String,
    options: IndexFieldOptions,
}

impl DynamicFieldRule {
    /// Creates one dynamic-field rule: for each document, the value at
    /// `value_path` is indexed under a field name resolved at index time
    /// from `name_path`, with the rule's options.
    ///
    /// # Errors
    ///
    /// Returns [`IndexDefinitionError::InvalidFieldPath`] for a malformed
    /// path.
    pub fn try_new(
        name_path: &str,
        value_path: &str,
        options: IndexFieldOptions,
    ) -> Result<Self, IndexDefinitionError> {
        validate_field_path_text(name_path)?;
        validate_field_path_text(value_path)?;
        Ok(Self { name_path: name_path.to_owned(), value_path: value_path.to_owned(), options })
    }

    /// Returns the document path resolving the dynamic field name.
    #[must_use]
    pub fn name_path(&self) -> &str {
        &self.name_path
    }

    /// Returns the document path resolving the dynamic field value.
    #[must_use]
    pub fn value_path(&self) -> &str {
        &self.value_path
    }

    /// Returns the options applied to every field the rule creates.
    #[must_use]
    pub const fn options(&self) -> &IndexFieldOptions {
        &self.options
    }
}

/// One map stage: a source, the output shape it projects, and the
/// dynamic-field rules it declares.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct MapStage {
    source: MapSource,
    fields: Vec<MapOutputField>,
    dynamic: Vec<DynamicFieldRule>,
}

impl MapStage {
    /// Creates one map stage after validating its shape and bounds.
    ///
    /// # Errors
    ///
    /// Returns [`IndexDefinitionError::NoOutputFields`] or
    /// [`IndexDefinitionError::TooManyOutputFields`] for an out-of-bound
    /// output shape, [`IndexDefinitionError::DuplicateOutputField`] for a
    /// repeated output name, [`IndexDefinitionError::InvalidFieldName`] for
    /// a malformed name, and [`IndexDefinitionError::TooManyDynamicRules`]
    /// for an out-of-bound rule list.
    pub fn try_new(
        source: MapSource,
        fields: Vec<MapOutputField>,
        dynamic: Vec<DynamicFieldRule>,
    ) -> Result<Self, IndexDefinitionError> {
        if fields.is_empty() {
            return Err(IndexDefinitionError::NoOutputFields);
        }
        if fields.len() > MAX_MAP_OUTPUT_FIELDS {
            return Err(IndexDefinitionError::TooManyOutputFields);
        }
        if dynamic.len() > MAX_DYNAMIC_FIELD_RULES {
            return Err(IndexDefinitionError::TooManyDynamicRules);
        }
        for field in &fields {
            validate_field_name_text(field.name())?;
        }
        // Sorted comparison catches any repeated name regardless of
        // position.
        let mut names: Vec<&str> = fields.iter().map(MapOutputField::name).collect();
        names.sort_unstable();
        if names.windows(2).any(|pair| pair[0] == pair[1]) {
            return Err(IndexDefinitionError::DuplicateOutputField);
        }
        Ok(Self { source, fields, dynamic })
    }

    /// Returns the stage's source.
    #[must_use]
    pub const fn source(&self) -> &MapSource {
        &self.source
    }

    /// Returns the projected output shape, in declaration order.
    #[must_use]
    pub fn fields(&self) -> &[MapOutputField] {
        &self.fields
    }

    /// Returns the stage's dynamic-field rules.
    #[must_use]
    pub fn dynamic_rules(&self) -> &[DynamicFieldRule] {
        &self.dynamic
    }
}

/// The array grouping behavior of one reduce key, pinned to V1's group-by
/// array behavior.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum GroupByArrayMode {
    /// The key is scalar, or the array mode does not apply.
    NotApplicable,
    /// One group per whole array value.
    ByContent,
    /// One group per individual array element.
    ByIndividualValues,
}

/// The typed aggregation vocabulary of the reduce stage, pinned to V1's
/// aggregation operations for index definitions.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum AggregationFunction {
    /// Counts the grouped rows.
    Count,
    /// Sums one projected field over the grouped rows.
    Sum,
}

impl AggregationFunction {
    /// Returns the canonical function name.
    #[must_use]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Count => "count",
            Self::Sum => "sum",
        }
    }
}

/// One grouping key of a reduce stage.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ReduceKey {
    field: String,
    array_mode: GroupByArrayMode,
}

impl ReduceKey {
    /// Creates one grouping key over a projected output field.
    ///
    /// # Errors
    ///
    /// Returns [`IndexDefinitionError::InvalidFieldName`] for a malformed
    /// field name.
    pub fn try_new(
        field: &str,
        array_mode: GroupByArrayMode,
    ) -> Result<Self, IndexDefinitionError> {
        validate_field_name_text(field)?;
        Ok(Self { field: field.to_owned(), array_mode })
    }

    /// Returns the grouped output field name.
    #[must_use]
    pub fn field(&self) -> &str {
        &self.field
    }

    /// Returns the key's array grouping mode.
    #[must_use]
    pub const fn array_mode(&self) -> GroupByArrayMode {
        self.array_mode
    }
}

/// One aggregate output of a reduce stage.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct AggregationBinding {
    output: String,
    function: AggregationFunction,
    input: Option<String>,
}

impl AggregationBinding {
    /// Creates one aggregate binding: `output` names the reduce result
    /// field, and the function may name one projected input field.
    ///
    /// # Errors
    ///
    /// Returns [`IndexDefinitionError::InvalidFieldName`] for a malformed
    /// output or input name, [`IndexDefinitionError::CountHasInput`] when
    /// the count aggregate names an input, and
    /// [`IndexDefinitionError::SumNeedsInput`] when the sum aggregate names
    /// none.
    pub fn try_new(
        output: &str,
        function: AggregationFunction,
        input: Option<&str>,
    ) -> Result<Self, IndexDefinitionError> {
        validate_field_name_text(output)?;
        let input = match (function, input) {
            (AggregationFunction::Count, Some(_)) => {
                return Err(IndexDefinitionError::CountHasInput);
            }
            (AggregationFunction::Sum, None) => {
                return Err(IndexDefinitionError::SumNeedsInput);
            }
            (_, Some(field)) => {
                validate_field_name_text(field)?;
                Some(field.to_owned())
            }
            (_, None) => None,
        };
        Ok(Self { output: output.to_owned(), function, input })
    }

    /// Returns the reduce result field name.
    #[must_use]
    pub fn output(&self) -> &str {
        &self.output
    }

    /// Returns the aggregate function.
    #[must_use]
    pub const fn function(&self) -> AggregationFunction {
        self.function
    }

    /// Returns the aggregated input field, when the function names one.
    #[must_use]
    pub fn input(&self) -> Option<&str> {
        self.input.as_deref()
    }
}

/// The reduce stage: grouping keys plus aggregates, turning the map output
/// into grouped rows — the static aggregation contract.
///
/// One output row exists per distinct grouping-key value: the reduce key
/// set is the `DISTINCT` identity of the index output, which is the static
/// `GROUP BY` semantic a query against the index adopts.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ReduceStage {
    keys: Vec<ReduceKey>,
    aggregations: Vec<AggregationBinding>,
}

impl ReduceStage {
    /// Creates one reduce stage after validating its internal shape.
    ///
    /// # Errors
    ///
    /// Returns [`IndexDefinitionError::NoReduceKeys`] or
    /// [`IndexDefinitionError::TooManyReduceKeys`] for an out-of-bound key
    /// list, [`IndexDefinitionError::TooManyAggregations`] for an
    /// out-of-bound aggregation list,
    /// [`IndexDefinitionError::DuplicateReduceKey`] for a repeated key, and
    /// [`IndexDefinitionError::DuplicateAggregationOutput`] for a repeated
    /// aggregate output.
    pub fn try_new(
        keys: Vec<ReduceKey>,
        aggregations: Vec<AggregationBinding>,
    ) -> Result<Self, IndexDefinitionError> {
        if keys.is_empty() {
            return Err(IndexDefinitionError::NoReduceKeys);
        }
        if keys.len() > MAX_REDUCE_KEYS {
            return Err(IndexDefinitionError::TooManyReduceKeys);
        }
        if aggregations.len() > MAX_AGGREGATIONS {
            return Err(IndexDefinitionError::TooManyAggregations);
        }
        for key in &keys {
            validate_field_name_text(key.field())?;
        }
        let mut key_names: Vec<&str> = keys.iter().map(ReduceKey::field).collect();
        key_names.sort_unstable();
        if key_names.windows(2).any(|pair| pair[0] == pair[1]) {
            return Err(IndexDefinitionError::DuplicateReduceKey);
        }
        let mut outputs: Vec<&str> = aggregations.iter().map(AggregationBinding::output).collect();
        outputs.sort_unstable();
        if outputs.windows(2).any(|pair| pair[0] == pair[1]) {
            return Err(IndexDefinitionError::DuplicateAggregationOutput);
        }
        Ok(Self { keys, aggregations })
    }

    /// Returns the grouping keys, in declaration order.
    #[must_use]
    pub fn keys(&self) -> &[ReduceKey] {
        &self.keys
    }

    /// Returns the aggregate bindings, in declaration order.
    #[must_use]
    pub fn aggregations(&self) -> &[AggregationBinding] {
        &self.aggregations
    }
}

/// How one output field is indexed.
///
/// Pinned to V1's field indexing options for the typed subset: the default
/// exact-normalizing mode, unanalyzed exact terms, no index terms at all,
/// and analyzed search terms.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum FieldIndexing {
    /// Index with the engine's default exact-normalizing analysis.
    Default,
    /// Index the raw value as one exact term, without normalization.
    Exact,
    /// Do not index the field; it can only be returned, never queried,
    /// and only when stored.
    No,
    /// Index analyzed terms for search predicates.
    Search,
}

/// Whether one output field's value is stored in the index.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum FieldStorage {
    /// The field value is not stored; results re-read the source document.
    NotStored,
    /// The field value is stored in the index and projectable from it.
    Stored,
}

/// A validated analyzer name naming one analysis strategy.
#[derive(Clone, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct AnalyzerName(String);

impl AnalyzerName {
    /// Creates one validated analyzer name.
    ///
    /// # Errors
    ///
    /// Returns [`IndexDefinitionError::InvalidAnalyzerName`] for an empty,
    /// oversized, control-carrying, or whitespace-padded name.
    pub fn try_new(value: &str) -> Result<Self, IndexDefinitionError> {
        validate_name_text(value).map_err(|_| IndexDefinitionError::InvalidAnalyzerName)?;
        Ok(Self(value.to_owned()))
    }

    /// Returns the canonical analyzer name text.
    #[must_use]
    pub fn as_str(&self) -> &str {
        &self.0
    }
}

/// The options of one output field: indexing mode, storage mode, and the
/// analysis strategy an analyzed field uses.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct IndexFieldOptions {
    indexing: FieldIndexing,
    storage: FieldStorage,
    analyzer: Option<AnalyzerName>,
}

impl IndexFieldOptions {
    /// Creates one field-options value.
    ///
    /// # Errors
    ///
    /// Returns [`IndexDefinitionError::AnalyzerWithoutSearch`] when an
    /// analyzer is attached to a field that is not analyzed
    /// (`FieldIndexing::Search`).
    pub fn try_new(
        indexing: FieldIndexing,
        storage: FieldStorage,
        analyzer: Option<AnalyzerName>,
    ) -> Result<Self, IndexDefinitionError> {
        if analyzer.is_some() && indexing != FieldIndexing::Search {
            return Err(IndexDefinitionError::AnalyzerWithoutSearch);
        }
        Ok(Self { indexing, storage, analyzer })
    }

    /// Returns the indexing mode.
    #[must_use]
    pub const fn indexing(&self) -> FieldIndexing {
        self.indexing
    }

    /// Returns the storage mode.
    #[must_use]
    pub const fn storage(&self) -> FieldStorage {
        self.storage
    }

    /// Returns the analysis strategy of an analyzed field.
    #[must_use]
    pub fn analyzer(&self) -> Option<&AnalyzerName> {
        self.analyzer.as_ref()
    }
}

/// One compound-field hint: an ordered group of output fields the storage
/// engine may treat as one unit.
///
/// A hint is an optimization input only: it never changes the definition's
/// output shape or visible results, and a compound-only definition change
/// does not require a reindex.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct CompoundField {
    members: Vec<String>,
}

impl CompoundField {
    /// Creates one compound-field hint after validating its members.
    ///
    /// # Errors
    ///
    /// Returns [`IndexDefinitionError::CompoundTooFewMembers`] or
    /// [`IndexDefinitionError::CompoundTooManyMembers`] for an out-of-bound
    /// member list, [`IndexDefinitionError::InvalidFieldName`] for a
    /// malformed member, and
    /// [`IndexDefinitionError::CompoundMemberDuplicate`] for a repeated
    /// member.
    pub fn try_new(members: Vec<String>) -> Result<Self, IndexDefinitionError> {
        if members.len() < 2 {
            return Err(IndexDefinitionError::CompoundTooFewMembers);
        }
        if members.len() > MAX_COMPOUND_MEMBERS {
            return Err(IndexDefinitionError::CompoundTooManyMembers);
        }
        for member in &members {
            validate_field_name_text(member)?;
        }
        let mut sorted = members.clone();
        sorted.sort();
        if sorted.windows(2).any(|pair| pair[0] == pair[1]) {
            return Err(IndexDefinitionError::CompoundMemberDuplicate);
        }
        Ok(Self { members })
    }

    /// Returns the compound members, in declaration order.
    #[must_use]
    pub fn members(&self) -> &[String] {
        &self.members
    }
}

/// One output reference pattern: the identity shape of the reference
/// documents an output collection's pattern creates.
///
/// A pattern carries at least one `{field}` placeholder, every placeholder
/// names one reduce output field, and no field appears twice. V1's format
/// suffixes are not in the typed language and fail closed.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct OutputReferencePattern {
    text: String,
    fields: Vec<String>,
}

impl OutputReferencePattern {
    /// Creates one validated output reference pattern.
    ///
    /// # Errors
    ///
    /// Returns [`IndexDefinitionError::PatternNoPlaceholder`] when the
    /// pattern carries no field placeholder,
    /// [`IndexDefinitionError::PatternPlaceholderInvalid`] for a malformed
    /// placeholder, [`IndexDefinitionError::PatternDuplicateField`] for a
    /// repeated field, and
    /// [`IndexDefinitionError::PatternFormatUnsupported`] for a placeholder
    /// with a format suffix.
    pub fn try_new(text: &str) -> Result<Self, IndexDefinitionError> {
        validate_name_text(text).map_err(|_| IndexDefinitionError::PatternPlaceholderInvalid)?;
        let mut fields = Vec::new();
        let mut remainder = text;
        while let Some(start) = remainder.find('{') {
            let after = &remainder[start + 1..];
            let end = after.find('}').ok_or(IndexDefinitionError::PatternPlaceholderInvalid)?;
            let placeholder = &after[..end];
            if placeholder.contains('{') {
                return Err(IndexDefinitionError::PatternPlaceholderInvalid);
            }
            let name = placeholder
                .split_once(':')
                .map_or(Ok(placeholder), |_| Err(IndexDefinitionError::PatternFormatUnsupported))?;
            validate_field_name_text(name)
                .map_err(|_| IndexDefinitionError::PatternPlaceholderInvalid)?;
            if fields.contains(&name.to_owned()) {
                return Err(IndexDefinitionError::PatternDuplicateField);
            }
            fields.push(name.to_owned());
            remainder = &after[end + 1..];
        }
        if remainder.contains('}') {
            return Err(IndexDefinitionError::PatternPlaceholderInvalid);
        }
        if fields.is_empty() {
            return Err(IndexDefinitionError::PatternNoPlaceholder);
        }
        Ok(Self { text: text.to_owned(), fields })
    }

    /// Returns the pattern text as declared.
    #[must_use]
    pub fn as_str(&self) -> &str {
        &self.text
    }

    /// Returns the pattern's field placeholders, in declaration order.
    #[must_use]
    pub fn fields(&self) -> &[String] {
        &self.fields
    }
}

/// The map-reduce output publication contract: the collection reduce
/// results are written to, an optional reference-document identity pattern,
/// and the optional collection the reference documents live in.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ReduceOutput {
    collection: CollectionName,
    reference_pattern: Option<OutputReferencePattern>,
    references_collection: Option<CollectionName>,
}

impl ReduceOutput {
    /// Creates one output declaration.
    ///
    /// # Errors
    ///
    /// Returns
    /// [`IndexDefinitionError::OutputCollectionEqualsReferences`] when the
    /// references collection equals the output collection, and the pattern
    /// validation errors of [`OutputReferencePattern::try_new`].
    pub fn try_new(
        collection: CollectionName,
        reference_pattern: Option<OutputReferencePattern>,
        references_collection: Option<CollectionName>,
    ) -> Result<Self, IndexDefinitionError> {
        if references_collection.as_ref() == Some(&collection) {
            return Err(IndexDefinitionError::OutputCollectionEqualsReferences);
        }
        Ok(Self { collection, reference_pattern, references_collection })
    }

    /// Returns the output collection.
    #[must_use]
    pub const fn collection(&self) -> &CollectionName {
        &self.collection
    }

    /// Returns the reference-document identity pattern, when one is
    /// declared.
    #[must_use]
    pub fn reference_pattern(&self) -> Option<&OutputReferencePattern> {
        self.reference_pattern.as_ref()
    }

    /// Returns the references collection, when one is declared.
    #[must_use]
    pub const fn references_collection(&self) -> Option<&CollectionName> {
        self.references_collection.as_ref()
    }
}

/// The recorded differences between two definitions of one name.
///
/// A difference report is drift evidence: which parts of a definition
/// changed, and whether the change requires the index to be rebuilt. The
/// name itself is never compared — drift is the question asked of two
/// definitions that share a name.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct DefinitionDifferences(u8);

impl DefinitionDifferences {
    /// No difference: identical content.
    pub const NONE: Self = Self(0);
    /// The map stages differ.
    pub const MAPS: Self = Self(1 << 0);
    /// The reduce stage differs.
    pub const REDUCE: Self = Self(1 << 1);
    /// The field options differ.
    pub const FIELDS: Self = Self(1 << 2);
    /// The compound-field hints differ.
    pub const COMPOUND_FIELDS: Self = Self(1 << 3);
    /// The output publication differs.
    pub const OUTPUT: Self = Self(1 << 4);

    /// Reports whether the map stages differ.
    #[must_use]
    pub const fn maps(self) -> bool {
        self.0 & Self::MAPS.0 != 0
    }

    /// Reports whether the reduce stage differs.
    #[must_use]
    pub const fn reduce(self) -> bool {
        self.0 & Self::REDUCE.0 != 0
    }

    /// Reports whether the field options differ.
    #[must_use]
    pub const fn fields(self) -> bool {
        self.0 & Self::FIELDS.0 != 0
    }

    /// Reports whether the compound-field hints differ.
    #[must_use]
    pub const fn compound_fields(self) -> bool {
        self.0 & Self::COMPOUND_FIELDS.0 != 0
    }

    /// Reports whether the output publication differs.
    #[must_use]
    pub const fn output(self) -> bool {
        self.0 & Self::OUTPUT.0 != 0
    }

    /// Reports whether no part differs.
    #[must_use]
    pub const fn is_empty(self) -> bool {
        self.0 == 0
    }

    /// Reports whether the drift requires a rebuild of indexed data.
    ///
    /// Maps, reduce, field options, and output publication change what the
    /// index contains; compound-field hints do not — they only advise the
    /// storage engine, exactly as V1's reindex mask excludes them.
    #[must_use]
    pub const fn reindex_required(self) -> bool {
        self.0 & (Self::MAPS.0 | Self::REDUCE.0 | Self::FIELDS.0 | Self::OUTPUT.0) != 0
    }

    /// Combines two difference reports.
    #[must_use]
    pub const fn union(self, other: Self) -> Self {
        Self(self.0 | other.0)
    }
}

/// A validated static index definition: the typed authoring unit of V2
/// static indexes.
///
/// One definition names an index, carries the language version it was
/// authored against, declares its map stages, and — when it aggregates —
/// its reduce stage, field options, compound-field hints, and output
/// publication. Construction validates the whole contract fail-closed, so
/// a value of this type is a definition the engine accepts by shape.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct StaticIndexDefinition {
    version: IndexLanguageVersion,
    name: StaticIndexName,
    maps: Vec<MapStage>,
    reduce: Option<ReduceStage>,
    field_options: BTreeMap<String, IndexFieldOptions>,
    compounds: Vec<CompoundField>,
    output: Option<ReduceOutput>,
}

impl StaticIndexDefinition {
    /// Creates one definition after validating the full contract.
    ///
    /// Map stages are stored in canonical source order (all-documents
    /// first, then collections by name) so map order is never part of a
    /// definition's identity, exactly as V1 keeps maps as a set.
    ///
    /// # Errors
    ///
    /// Returns [`IndexDefinitionError::UnknownVersion`] unless the version
    /// is the one this engine speaks, and the map, reduce, option,
    /// compound, and output rule errors of the language for any violated
    /// contract rule.
    pub fn try_new(
        version: IndexLanguageVersion,
        name: StaticIndexName,
        maps: Vec<MapStage>,
        reduce: Option<ReduceStage>,
        field_options: Vec<(String, IndexFieldOptions)>,
        compounds: Vec<CompoundField>,
        output: Option<ReduceOutput>,
    ) -> Result<Self, IndexDefinitionError> {
        if version != IndexLanguageVersion::CURRENT {
            return Err(IndexDefinitionError::UnknownVersion);
        }
        let maps = validate_maps(maps)?;
        let shape = map_shape(&maps);
        if let Some(reduce) = &reduce {
            validate_reduce_shape(reduce, &shape)?;
        }
        let output_shape = reduce.as_ref().map_or_else(|| shape.clone(), reduce_output_shape);
        validate_output(output.as_ref(), reduce.as_ref(), &maps)?;
        let field_options = validate_field_options(field_options, &output_shape)?;
        validate_compounds(&compounds, &output_shape)?;
        Ok(Self { version, name, maps, reduce, field_options, compounds, output })
    }

    /// Creates one map-only definition with a single map stage.
    ///
    /// # Errors
    ///
    /// Returns the errors of [`StaticIndexDefinition::try_new`].
    pub fn try_map(
        version: IndexLanguageVersion,
        name: StaticIndexName,
        stage: MapStage,
    ) -> Result<Self, IndexDefinitionError> {
        Self::try_new(version, name, vec![stage], None, Vec::new(), Vec::new(), None)
    }

    /// Returns a copy of this definition with one more map stage.
    ///
    /// # Errors
    ///
    /// Returns the map rule errors of [`StaticIndexDefinition::try_new`].
    pub fn with_additional_map_stage(self, stage: MapStage) -> Result<Self, IndexDefinitionError> {
        let mut maps = self.maps.clone();
        maps.push(stage);
        Self::try_new(
            self.version,
            self.name,
            maps,
            self.reduce,
            self.field_options.into_iter().collect(),
            self.compounds,
            self.output,
        )
    }

    /// Returns a copy of this definition with the given field options.
    ///
    /// # Errors
    ///
    /// Returns the field-option rule errors of
    /// [`StaticIndexDefinition::try_new`].
    pub fn with_field_options(
        self,
        options: Vec<(String, IndexFieldOptions)>,
    ) -> Result<Self, IndexDefinitionError> {
        Self::try_new(
            self.version,
            self.name,
            self.maps,
            self.reduce,
            options,
            self.compounds,
            self.output,
        )
    }

    /// Returns the definition's name.
    #[must_use]
    pub const fn name(&self) -> &StaticIndexName {
        &self.name
    }

    /// Returns the language version this definition was authored against.
    #[must_use]
    pub const fn version(&self) -> IndexLanguageVersion {
        self.version
    }

    /// Returns the map stages in canonical source order.
    #[must_use]
    pub fn map_stages(&self) -> &[MapStage] {
        &self.maps
    }

    /// Returns the reduce stage, when the definition aggregates.
    #[must_use]
    pub fn reduce(&self) -> Option<&ReduceStage> {
        self.reduce.as_ref()
    }

    /// Returns the field options keyed by output field name.
    #[must_use]
    pub fn field_options(&self) -> &BTreeMap<String, IndexFieldOptions> {
        &self.field_options
    }

    /// Returns the compound-field hints.
    #[must_use]
    pub fn compound_fields(&self) -> &[CompoundField] {
        &self.compounds
    }

    /// Returns the output publication, when one is declared.
    #[must_use]
    pub fn output(&self) -> Option<&ReduceOutput> {
        self.output.as_ref()
    }

    /// Reports whether this definition aggregates: it carries a reduce
    /// stage and its output is grouped rows.
    #[must_use]
    pub const fn is_map_reduce(&self) -> bool {
        self.reduce.is_some()
    }

    /// Returns the definition's output field names in canonical order:
    /// declaration order for map-only definitions; grouping keys, then
    /// aggregate outputs, for map-reduce definitions.
    pub fn output_fields(&self) -> impl Iterator<Item = &str> + '_ {
        let fields: Vec<&str> = match &self.reduce {
            None => self
                .maps
                .first()
                .map(|stage| stage.fields.iter().map(MapOutputField::name).collect())
                .unwrap_or_default(),
            Some(reduce) => reduce
                .keys
                .iter()
                .map(ReduceKey::field)
                .chain(reduce.aggregations.iter().map(AggregationBinding::output))
                .collect(),
        };
        fields.into_iter()
    }

    /// Compares this definition's content with another definition of the
    /// same name and records which parts differ.
    ///
    /// The name and language version are identity, not content: they are
    /// never reported. V1 folds output-collection changes into its reduce
    /// difference; V2 reports them separately as output, with the same
    /// rebuild consequence through
    /// [`DefinitionDifferences::reindex_required`].
    #[must_use]
    pub fn compare(&self, other: &Self) -> DefinitionDifferences {
        let maps = self.maps != other.maps;
        let reduce = self.reduce != other.reduce;
        let fields = self.field_options != other.field_options;
        let compound = self.compounds != other.compounds;
        let output = self.output != other.output;
        let mut differences = DefinitionDifferences::NONE;
        if maps {
            differences = differences.union(DefinitionDifferences::MAPS);
        }
        if reduce {
            differences = differences.union(DefinitionDifferences::REDUCE);
        }
        if fields {
            differences = differences.union(DefinitionDifferences::FIELDS);
        }
        if compound {
            differences = differences.union(DefinitionDifferences::COMPOUND_FIELDS);
        }
        if output {
            differences = differences.union(DefinitionDifferences::OUTPUT);
        }
        differences
    }

    /// Returns the deterministic content identity of this definition.
    ///
    /// The identity renders the version, name, sources, projections
    /// (including dynamic-field rules with their options), reduce stage,
    /// field options, compound hints, and output publication in canonical
    /// order: two definitions of one name with equal content render equal
    /// identities regardless of map order.
    #[must_use]
    pub fn identity_key(&self) -> String {
        let mut identity =
            format!("{}{IDENTITY_SEPARATOR}{}", self.version.as_str(), self.name.as_str());
        for stage in &self.maps {
            identity.push(IDENTITY_GROUP);
            identity.push_str(&stage.source.identity());
            for field in &stage.fields {
                identity.push(IDENTITY_SEPARATOR);
                identity.push_str(field.name());
                match &field.expression {
                    MapExpression::Field { path } => {
                        identity.push(':');
                        identity.push_str(path);
                    }
                    MapExpression::DocumentIdentity => identity.push_str(":id()"),
                }
            }
            for rule in &stage.dynamic {
                identity.push(IDENTITY_SEPARATOR);
                identity.push_str("dynamic:");
                identity.push_str(rule.name_path());
                identity.push('=');
                identity.push_str(rule.value_path());
                push_options_identity(&mut identity, rule.options());
            }
        }
        if let Some(reduce) = &self.reduce {
            identity.push(IDENTITY_GROUP);
            identity.push_str("reduce");
            for key in &reduce.keys {
                identity.push(IDENTITY_SEPARATOR);
                identity.push_str(key.field());
                identity.push_str(match key.array_mode {
                    GroupByArrayMode::NotApplicable => "|scalar",
                    GroupByArrayMode::ByContent => "|by-content",
                    GroupByArrayMode::ByIndividualValues => "|by-values",
                });
            }
            for aggregation in &reduce.aggregations {
                identity.push(IDENTITY_SEPARATOR);
                identity.push_str(aggregation.output());
                identity.push('=');
                identity.push_str(aggregation.function().as_str());
                if let Some(input) = aggregation.input() {
                    identity.push('(');
                    identity.push_str(input);
                    identity.push(')');
                }
            }
        }
        for (field, options) in &self.field_options {
            identity.push(IDENTITY_GROUP);
            identity.push_str("options:");
            identity.push_str(field);
            push_options_identity(&mut identity, options);
        }
        for compound in &self.compounds {
            identity.push(IDENTITY_GROUP);
            identity.push_str("compound:");
            identity.push_str(&compound.members.join("+"));
        }
        if let Some(output) = &self.output {
            identity.push(IDENTITY_GROUP);
            identity.push_str("output:");
            identity.push_str(output.collection().as_str());
            if let Some(references) = output.references_collection() {
                identity.push_str("|refs:");
                identity.push_str(references.as_str());
            }
            if let Some(pattern) = output.reference_pattern() {
                identity.push_str("|pattern:");
                identity.push_str(pattern.as_str());
            }
        }
        identity
    }

    /// Reports whether another definition claims the same output
    /// collection as this one.
    ///
    /// One output collection belongs to one index: two differently named
    /// map-reduce definitions publishing into the same collection is a
    /// conflict the engine refuses, exactly as V1 refuses it at index
    /// creation. Definitions without output collections never conflict,
    /// and one definition never conflicts with itself.
    #[must_use]
    pub fn output_collection_conflicts_with(&self, other: &Self) -> bool {
        self.name != other.name
            && matches!((&self.output, &other.output), (Some(mine), Some(theirs))
                if mine.collection() == theirs.collection())
    }
}

/// Appends one options value to an identity rendering with the shared
/// options grammar: indexing mode, storage mode, then the analyzer of an
/// analyzed field. Static field options and dynamic-field rules render
/// through this one helper, so the two can never drift apart.
fn push_options_identity(identity: &mut String, options: &IndexFieldOptions) {
    identity.push_str(match options.indexing() {
        FieldIndexing::Default => "|default",
        FieldIndexing::Exact => "|exact",
        FieldIndexing::No => "|no",
        FieldIndexing::Search => "|search",
    });
    identity.push_str(match options.storage() {
        FieldStorage::NotStored => "|not-stored",
        FieldStorage::Stored => "|stored",
    });
    if let Some(analyzer) = options.analyzer() {
        identity.push('|');
        identity.push_str(analyzer.as_str());
    }
}

/// Validates the map stage list and returns it in canonical source order.
fn validate_maps(maps: Vec<MapStage>) -> Result<Vec<MapStage>, IndexDefinitionError> {
    if maps.is_empty() {
        return Err(IndexDefinitionError::NoMaps);
    }
    if maps.len() > MAX_MAP_STAGES {
        return Err(IndexDefinitionError::TooManyMaps);
    }
    let shape = map_shape(&maps);
    for stage in &maps {
        let stage_shape: Vec<&str> = stage.fields.iter().map(MapOutputField::name).collect();
        let declared: Vec<&str> = shape.iter().map(String::as_str).collect();
        if stage_shape != declared {
            return Err(IndexDefinitionError::MapShapeMismatch);
        }
    }
    let mut seen_sources: Vec<&MapSource> = Vec::new();
    let mut all_documents = false;
    let mut collections = 0;
    let mut family_kind: Option<MapSourceFamily> = None;
    let mut family_sources = 0usize;
    let mut family_all_entries = false;
    for stage in &maps {
        let source = stage.source();
        // The one-kind rule (GAP-Q19): document sources and family sources
        // never mix, and the two family kinds never mix — V1 keeps one
        // definition type per source kind, and the typed language keeps
        // the same boundary.
        if source.is_family() {
            let kind = match source {
                MapSource::Counters { .. } => MapSourceFamily::Counters,
                _ => MapSourceFamily::TimeSeries,
            };
            if collections > 0 || all_documents {
                return Err(IndexDefinitionError::SourceKindMismatch);
            }
            if family_kind.is_some_and(|seen| seen != kind) {
                return Err(IndexDefinitionError::SourceKindMismatch);
            }
            if matches!(
                source,
                MapSource::Counters { name: None } | MapSource::TimeSeries { name: None }
            ) {
                family_all_entries = true;
            }
            family_sources += 1;
            family_kind = Some(kind);
        } else {
            match source {
                MapSource::AllDocuments => all_documents = true,
                MapSource::Collection(_) => collections += 1,
                _ => unreachable!("the family branch returns before the document branch"),
            }
            if family_kind.is_some() {
                return Err(IndexDefinitionError::SourceKindMismatch);
            }
        }
        if seen_sources.iter().any(|source| **source == *stage.source()) {
            return Err(IndexDefinitionError::DuplicateMapSource);
        }
        seen_sources.push(&stage.source);
    }
    if all_documents && collections > 0 {
        return Err(IndexDefinitionError::AllDocumentsWithCollections);
    }
    // The all-entries source covers every entry of the family, so any
    // second family source of the same kind overlaps it and would
    // double-count every mapped entry.
    if family_all_entries && family_sources > 1 {
        return Err(IndexDefinitionError::OverlappingFamilySources);
    }
    let mut canonical = maps;
    canonical.sort_by_key(|stage| stage.source.identity());
    Ok(canonical)
}

/// Which family a family map source draws from, for the one-kind rule.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum MapSourceFamily {
    /// The counters family.
    Counters,
    /// The time-series family.
    TimeSeries,
}

/// Returns the ordered output shape shared by every stage.
fn map_shape(maps: &[MapStage]) -> Vec<String> {
    maps.first()
        .map(|stage| stage.fields.iter().map(|field| field.name().to_owned()).collect())
        .unwrap_or_default()
}

/// Validates the reduce stage against the map shape.
fn validate_reduce_shape(
    reduce: &ReduceStage,
    shape: &[String],
) -> Result<(), IndexDefinitionError> {
    for key in reduce.keys() {
        if !shape.contains(&key.field().to_owned()) {
            return Err(IndexDefinitionError::ReduceKeyNotProjected);
        }
    }
    for aggregation in reduce.aggregations() {
        if let Some(input) = aggregation.input() {
            if reduce.keys().iter().any(|key| key.field() == input) {
                return Err(IndexDefinitionError::AggregationInputIsKey);
            }
            if !shape.contains(&input.to_owned()) {
                return Err(IndexDefinitionError::AggregationInputNotProjected);
            }
        }
        if reduce.keys().iter().any(|key| key.field() == aggregation.output()) {
            return Err(IndexDefinitionError::DuplicateAggregationOutput);
        }
    }
    // The map shape is covered exactly by grouping keys plus aggregation
    // inputs: every projected field feeds the grouping, nothing dangles,
    // and the grouping needs nothing the maps do not produce.
    let covered: std::collections::BTreeSet<&str> = reduce
        .keys()
        .iter()
        .map(ReduceKey::field)
        .chain(reduce.aggregations().iter().filter_map(AggregationBinding::input))
        .collect();
    let projected: std::collections::BTreeSet<&str> = shape.iter().map(String::as_str).collect();
    if covered != projected {
        return Err(IndexDefinitionError::ReduceShapeMismatch);
    }
    Ok(())
}

/// Returns the reduce output shape: grouping keys, then aggregate outputs.
fn reduce_output_shape(reduce: &ReduceStage) -> Vec<String> {
    reduce
        .keys()
        .iter()
        .map(|key| key.field().to_owned())
        .chain(reduce.aggregations().iter().map(|binding| binding.output().to_owned()))
        .collect()
}

/// Validates the output publication against the definition's parts.
fn validate_output(
    output: Option<&ReduceOutput>,
    reduce: Option<&ReduceStage>,
    maps: &[MapStage],
) -> Result<(), IndexDefinitionError> {
    let Some(output) = output else { return Ok(()) };
    let Some(reduce) = reduce else {
        return Err(IndexDefinitionError::OutputWithoutReduce);
    };
    let mapped: Vec<&CollectionName> =
        maps.iter().filter_map(|stage| stage.source().collection()).collect();
    // A family map source maps entries of the family, not documents of a
    // collection (GAP-Q19), so it can neither loop an output collection
    // nor collide with one directly; the loop rules below stay the
    // document-source rules they were.
    let all_family = maps.iter().all(|stage| stage.source().is_family());
    if mapped.is_empty() && !all_family {
        // The all-documents source maps every collection: any output loops.
        return Err(IndexDefinitionError::AllDocumentsOutputLoop);
    }
    if mapped.contains(&output.collection()) {
        return Err(IndexDefinitionError::OutputCollectionIsMappedSource);
    }
    if let Some(pattern) = output.reference_pattern() {
        let outputs: Vec<&str> = reduce
            .keys()
            .iter()
            .map(ReduceKey::field)
            .chain(reduce.aggregations().iter().map(AggregationBinding::output))
            .collect();
        for field in pattern.fields() {
            if !outputs.contains(&field.as_str()) {
                return Err(IndexDefinitionError::PatternFieldNotProjected);
            }
        }
    }
    Ok(())
}

/// Validates the field options against the definition's output shape and
/// returns them in canonical key order.
fn validate_field_options(
    options: Vec<(String, IndexFieldOptions)>,
    output_shape: &[String],
) -> Result<BTreeMap<String, IndexFieldOptions>, IndexDefinitionError> {
    let mut map = BTreeMap::new();
    for (field, options) in options {
        if map.contains_key(&field) {
            return Err(IndexDefinitionError::FieldOptionDuplicate);
        }
        if !output_shape.contains(&field) {
            return Err(IndexDefinitionError::FieldOptionUnknownField);
        }
        map.insert(field, options);
    }
    Ok(map)
}

/// Validates the compound-field hints against the output shape.
fn validate_compounds(
    compounds: &[CompoundField],
    output_shape: &[String],
) -> Result<(), IndexDefinitionError> {
    if compounds.len() > MAX_COMPOUND_FIELDS {
        return Err(IndexDefinitionError::TooManyCompounds);
    }
    let mut seen: Vec<&[String]> = Vec::new();
    for compound in compounds {
        for member in compound.members() {
            if !output_shape.contains(member) {
                return Err(IndexDefinitionError::CompoundMemberUnknown);
            }
        }
        if seen.iter().any(|members| *members == compound.members()) {
            return Err(IndexDefinitionError::CompoundDuplicate);
        }
        seen.push(compound.members());
    }
    Ok(())
}
