//! TQL, the `TerraBase` query language: parser and planner integration.
//!
//! This module implements the `tql/1.0` grammar of `docs/tql/spec.md`
//! (ticket TQL-02, charter PD-2026-08-26-08, ADR 0009) as a zero-dependency,
//! deterministic, bounded pipeline: one linear tokenize, one no-backtracking
//! recursive-descent parse bounded in depth, and one fail-closed static check
//! over the closed function registry, parameter pairing, page bounds, the
//! shared identifier discipline, reserved names, and the group-derivation
//! rule. The typed AST types are re-exported from this module; every node
//! carries a source span and the root carries the grammar version.
//!
//! The parse input is the typed `(grammar_version, text, parameters)` triple;
//! the seam never guesses a version. A missing version is
//! [`TqlErrorClass::MissingGrammarVersion`] and any version this parser does
//! not implement — unknown major, unknown minor, malformed — is
//! [`TqlErrorClass::UnsupportedGrammarVersion`]. Text is bounded by the
//! shared `MAX_QUERY_TEXT_BYTES` bound, which the V1-compatible surface
//! also uses.
//!
//! Lowering ([`lower_to_spec`] and [`compile`]) maps the AST onto the
//! existing W3-2 typed compile seam (`QueryStore::compile` through
//! `QueryInput::typed`). Lowering is total over the grammar but exact: a
//! construct the current seam cannot express without changing results —
//! `or`, `not`, nested paths, `group by`, `include`, `distinct`, aliases
//! that rename output keys, object constructors, where clauses over a
//! loaded alias, and the gated predicate functions — fails closed with
//! [`TqlErrorClass::UnsupportedForPlanning`] naming the construct, never a
//! degraded plan. The GAP-EVAL packet extended the seam with `between`,
//! `in`/`all in`, the string pattern functions, explicit nulls placement,
//! and `load` bindings with alias-qualified projections; extending the seam
//! for the remaining constructs is recorded work for the seam owner.
//!
//! The search, vector, and spatial predicates are in the grammar now and
//! gated on [`TqlCapabilities`]: against an engine that does not report the
//! capability they fail closed with
//! [`TqlErrorClass::CapabilityUnavailable`] and never degrade to a different
//! predicate.

mod ast;
mod lexer;
mod lower;
mod parser;
mod registry;

#[cfg(test)]
mod parity_tests;
#[cfg(test)]
mod tests;

pub use ast::{
    BinaryOperator, Direction, Expression, ExpressionKind, FunctionCall, GroupKey, Literal,
    LoadItem, LogicalOperator, MetadataField, NullsOrder, ObjectMember, OrderKey, PageBound,
    PageBoundKind, Paging, PathRoot, PathSegment, Position, Projection, ProjectionItem,
    ProjectionKind, Quantifier, SourceKind, Span, TqlPath, TqlQuery, TqlSource, UnaryOperator,
};
pub use lower::{compile, lower_to_spec};

use crate::operations::MAX_QUERY_TEXT_BYTES;
use crate::query::spec::MAX_FIELD_BYTES;
use crate::types::DocumentValue;

/// The `tql/MAJOR.MINOR` grammar version identity.
///
/// The pair identifies the grammar and the closed function registry together.
/// Evolution follows Section 5.3 of the specification: additive changes bump
/// the minor; removals, retokenizations, and semantic changes bump the major.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct GrammarVersion {
    major: u32,
    minor: u32,
}

impl GrammarVersion {
    /// The grammar version this parser implements.
    pub const CURRENT: Self = Self::new(1, 0);

    /// Creates one version identity from its parts.
    #[must_use]
    pub const fn new(major: u32, minor: u32) -> Self {
        Self { major, minor }
    }

    /// Returns the major version.
    #[must_use]
    pub const fn major(self) -> u32 {
        self.major
    }

    /// Returns the minor version.
    #[must_use]
    pub const fn minor(self) -> u32 {
        self.minor
    }

    /// Returns the `tql/MAJOR.MINOR` spelling.
    #[must_use]
    pub fn as_str(self) -> String {
        format!("tql/{}.{}", self.major, self.minor)
    }

    /// Parses the `tql/MAJOR.MINOR` spelling of one artifact's version field.
    ///
    /// Returns `None` for anything that is not exactly the version identity
    /// spelling; the artifact entry maps that onto
    /// [`TqlErrorClass::UnsupportedGrammarVersion`].
    #[must_use]
    pub fn parse(text: &str) -> Option<Self> {
        let rest = text.strip_prefix("tql/")?;
        let (major, minor) = rest.split_once('.')?;
        if minor.contains('.') {
            return None;
        }
        Some(Self::new(major.parse().ok()?, minor.parse().ok()?))
    }

    /// Reports whether this parser implements the version: the same major
    /// and a minor no newer than its own.
    #[allow(
        clippy::absurd_extreme_comparisons,
        reason = "the current minor is zero, so the bound is exact until an additive change bumps it"
    )]
    pub(super) const fn is_implemented(self) -> bool {
        self.major == Self::CURRENT.major && self.minor <= Self::CURRENT.minor
    }
}

/// One capability a gated predicate function requires.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Capability {
    /// Full-text search: `search`, `boost`, `score`.
    Search,
    /// Vector search: `vector.search`, `vector.of`.
    Vector,
    /// Spatial predicates and shapes: the `spatial.*` family.
    Spatial,
}

/// The capabilities the engine reports to the TQL parser.
///
/// No capability is reported until the engine ships it; a gated call against
/// an unreported capability fails closed. The none-constructor is the default
/// at every call site that has not been told otherwise.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct TqlCapabilities {
    search: bool,
    vector: bool,
    spatial: bool,
}

impl TqlCapabilities {
    /// Reports no capability: every gated predicate fails closed.
    #[must_use]
    pub const fn none() -> Self {
        Self { search: false, vector: false, spatial: false }
    }

    /// Returns the set with one more capability available.
    #[must_use]
    pub const fn with(self, capability: Capability) -> Self {
        match capability {
            Capability::Search => Self { search: true, ..self },
            Capability::Vector => Self { vector: true, ..self },
            Capability::Spatial => Self { spatial: true, ..self },
        }
    }

    /// Reports whether one capability is available.
    #[must_use]
    pub const fn is_available(&self, capability: Capability) -> bool {
        match capability {
            Capability::Search => self.search,
            Capability::Vector => self.vector,
            Capability::Spatial => self.spatial,
        }
    }
}

/// The named, typed, out-of-band parameter bindings of one TQL artifact.
///
/// Parameter values are data, never parsed text: a binding can never inject a
/// clause or change the parse tree. Names follow the shared identifier
/// discipline and are validated at construction.
#[derive(Clone, Debug, Default, PartialEq)]
pub struct TqlParameters {
    values: std::collections::BTreeMap<String, DocumentValue>,
}

impl TqlParameters {
    /// Creates an empty binding set.
    #[must_use]
    pub const fn new() -> Self {
        Self { values: std::collections::BTreeMap::new() }
    }

    /// Creates a binding set from name-value pairs.
    ///
    /// # Errors
    ///
    /// Returns [`TqlErrorClass::InvalidIdentifier`] for one invalid name.
    pub fn try_new(entries: &[(&str, DocumentValue)]) -> Result<Self, TqlError> {
        let mut parameters = Self::new();
        for (name, value) in entries {
            parameters.insert(name, value.clone())?;
        }
        Ok(parameters)
    }

    /// Adds or replaces one binding.
    ///
    /// # Errors
    ///
    /// Returns [`TqlErrorClass::InvalidIdentifier`] for an invalid name.
    pub fn insert(&mut self, name: &str, value: DocumentValue) -> Result<(), TqlError> {
        if !valid_identifier(name) {
            return Err(TqlError::binding(TqlErrorClass::InvalidIdentifier, name));
        }
        self.values.insert(name.to_owned(), value);
        Ok(())
    }

    /// Returns one binding, when present.
    #[must_use]
    pub fn get(&self, name: &str) -> Option<&DocumentValue> {
        self.values.get(name)
    }

    /// Reports whether one binding is present.
    #[must_use]
    pub fn contains(&self, name: &str) -> bool {
        self.values.contains_key(name)
    }

    /// Returns the bound names in sorted order.
    pub fn names(&self) -> impl Iterator<Item = &str> {
        self.values.keys().map(String::as_str)
    }

    /// Returns the number of bindings.
    #[must_use]
    pub fn len(&self) -> usize {
        self.values.len()
    }

    /// Reports whether the set is empty.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.values.is_empty()
    }
}

/// The fail-closed error classes of the TQL pipeline.
///
/// The first eleven classes are fixed by Section 5.4 of the specification;
/// `UnsupportedForPlanning` and `InvalidRequest` are the planner classes this
/// packet owns: a construct the current seam cannot lower exactly, and a
/// request context (tenant claim, deadline, cancellation) the seam rejects.
/// Every error carries the grammar version and, where a source position
/// applies, the position and the offending construct.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum TqlErrorClass {
    /// The artifact carries no grammar version.
    MissingGrammarVersion,
    /// The version is not implemented by this parser.
    UnsupportedGrammarVersion,
    /// The text does not match the grammar for the declared version.
    ParseError,
    /// A call names no registry function for the declared version.
    UnknownFunction,
    /// A static arity or argument-type mismatch in a registry call.
    InvalidArgument,
    /// A `$name` reference has no binding.
    UnboundParameter,
    /// A binding is never referenced.
    UnexpectedParameter,
    /// A registry function exists but the engine does not ship the
    /// capability.
    CapabilityUnavailable,
    /// A paging bound violation.
    InvalidBound,
    /// An identifier fails the shared identifier discipline.
    InvalidIdentifier,
    /// An alias or collection takes a reserved name.
    ReservedName,
    /// The construct parses but the current query seam cannot lower it
    /// exactly; it fails closed instead of degrading.
    UnsupportedForPlanning,
    /// The request context is invalid at the seam.
    InvalidRequest,
}

/// One typed, positioned TQL failure.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct TqlError {
    class: TqlErrorClass,
    grammar_version: Option<GrammarVersion>,
    position: Option<Position>,
    construct: Option<String>,
}

impl TqlError {
    /// Returns the error class.
    #[must_use]
    pub const fn class(&self) -> TqlErrorClass {
        self.class
    }

    /// Returns the grammar version of the artifact, when one was declared.
    #[must_use]
    pub const fn grammar_version(&self) -> Option<GrammarVersion> {
        self.grammar_version
    }

    /// Returns the source position, when one applies.
    #[must_use]
    pub const fn position(&self) -> Option<Position> {
        self.position
    }

    /// Returns the offending construct, when the class names one.
    #[must_use]
    pub fn construct(&self) -> Option<&str> {
        self.construct.as_deref()
    }

    /// Builds a positioned error naming one construct.
    pub(super) fn at(class: TqlErrorClass, construct: impl Into<String>, span: Span) -> Self {
        Self {
            class,
            grammar_version: None,
            position: Some(span.start()),
            construct: Some(construct.into()),
        }
    }

    /// Builds a positioned error without a construct.
    pub(super) fn plain(class: TqlErrorClass, span: Span) -> Self {
        Self { class, grammar_version: None, position: Some(span.start()), construct: None }
    }

    /// Builds an unpositioned error over one out-of-band name.
    pub(super) fn binding(class: TqlErrorClass, name: &str) -> Self {
        Self { class, grammar_version: None, position: None, construct: Some(name.to_owned()) }
    }

    /// Builds a positioned error at one exact position.
    pub(super) fn positioned(class: TqlErrorClass, position: Position) -> Self {
        Self { class, grammar_version: None, position: Some(position), construct: None }
    }

    /// Builds a version error, carrying the version when one was parsed.
    pub(super) fn version(class: TqlErrorClass, version: Option<GrammarVersion>) -> Self {
        Self { class, grammar_version: version, position: None, construct: None }
    }

    /// Attaches the accepted grammar version when the error carries none.
    pub(super) fn with_version(mut self, version: GrammarVersion) -> Self {
        if self.grammar_version.is_none() {
            self.grammar_version = Some(version);
        }
        self
    }
}

/// Reports whether one name passes the shared identifier discipline.
pub(super) fn valid_identifier(name: &str) -> bool {
    !name.is_empty()
        && name.len() <= MAX_FIELD_BYTES
        && !name.chars().any(char::is_control)
        && name == name.trim()
}

/// Parses one TQL text under an explicitly supplied grammar version.
///
/// Runs the full pipeline: tokenize, parse to the typed AST, and statically
/// check against the closed registry, the parameter bindings, and the page
/// bounds. Gated predicates fail closed: this entry reports no capability.
///
/// # Errors
///
/// Returns one typed [`TqlError`]; see [`TqlErrorClass`] for the classes.
pub fn parse(
    version: GrammarVersion,
    text: &str,
    parameters: &TqlParameters,
) -> Result<TqlQuery, TqlError> {
    parse_with(version, text, parameters, &TqlCapabilities::none())
}

/// Parses one TQL text under an explicitly supplied version and capability
/// set.
///
/// # Errors
///
/// Returns one typed [`TqlError`]; see [`TqlErrorClass`] for the classes.
#[allow(
    clippy::trivially_copy_pass_by_ref,
    reason = "the capability set is read-only context and every parse entry shares the reference form"
)]
pub fn parse_with(
    version: GrammarVersion,
    text: &str,
    parameters: &TqlParameters,
    capabilities: &TqlCapabilities,
) -> Result<TqlQuery, TqlError> {
    if !version.is_implemented() {
        return Err(TqlError::version(TqlErrorClass::UnsupportedGrammarVersion, Some(version)));
    }
    parse_text(version, text, parameters, capabilities)
}

/// Parses one TQL artifact whose version arrives as a declared field.
///
/// This is the artifact entry of Section 5.2 of the specification: the
/// version is required and never guessed. A missing declaration is
/// [`TqlErrorClass::MissingGrammarVersion`] and a malformed or unparseable
/// one is [`TqlErrorClass::UnsupportedGrammarVersion`].
///
/// # Errors
///
/// Returns one typed [`TqlError`]; see [`TqlErrorClass`] for the classes.
#[allow(
    clippy::trivially_copy_pass_by_ref,
    reason = "the capability set is read-only context and every parse entry shares the reference form"
)]
pub fn parse_artifact(
    declared: Option<&str>,
    text: &str,
    parameters: &TqlParameters,
    capabilities: &TqlCapabilities,
) -> Result<TqlQuery, TqlError> {
    let Some(declared) = declared else {
        return Err(TqlError::version(TqlErrorClass::MissingGrammarVersion, None));
    };
    let Some(version) = GrammarVersion::parse(declared) else {
        return Err(TqlError::version(TqlErrorClass::UnsupportedGrammarVersion, None));
    };
    parse_with(version, text, parameters, capabilities)
}

/// Runs the bounded pipeline over one text whose version was accepted.
#[allow(
    clippy::trivially_copy_pass_by_ref,
    reason = "the capability set is read-only context and every parse entry shares the reference form"
)]
fn parse_text(
    version: GrammarVersion,
    text: &str,
    parameters: &TqlParameters,
    capabilities: &TqlCapabilities,
) -> Result<TqlQuery, TqlError> {
    let attach = |error: TqlError| error.with_version(version);
    let start = Position::new(0, 1, 1);
    if text.trim().is_empty() {
        return Err(attach(TqlError::positioned(TqlErrorClass::ParseError, start)));
    }
    if text.len() > MAX_QUERY_TEXT_BYTES {
        return Err(attach(TqlError::positioned(TqlErrorClass::ParseError, start)));
    }
    let tokens = lexer::tokenize(text)
        .map_err(|error| attach(TqlError::positioned(TqlErrorClass::ParseError, error.position)))?;
    let query = parser::parse_query(version, &tokens, end_position(text)).map_err(|failure| {
        attach(TqlError::positioned(TqlErrorClass::ParseError, failure.position))
    })?;
    registry::check(&query, parameters, *capabilities).map_err(attach)?;
    Ok(query)
}

/// Computes the position just past the final character of the text.
fn end_position(text: &str) -> Position {
    let mut line = 1_u32;
    let mut column = 1_u32;
    for character in text.chars() {
        if character == '\n' {
            line += 1;
            column = 1;
        } else {
            column += 1;
        }
    }
    Position::new(text.len(), line, column)
}

/// Returns every function name of the closed `tql/1.0` registry, in
/// declaration order.
///
/// The registry is part of the grammar version (Section 7.5 of
/// `docs/tql/spec.md`) and closed by construction, so this enumeration exists
/// for conformance corpora: the golden query vector corpus uses it to fail
/// when a registry function has no vector that invokes it.
pub fn registry_function_names() -> impl Iterator<Item = &'static str> {
    registry::names()
}
