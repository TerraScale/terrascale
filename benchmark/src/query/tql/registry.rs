//! The `tql/1.0` function registry and the fail-closed static checks.
//!
//! The registry is closed and part of the grammar version (Section 7.5 of
//! `docs/tql/spec.md`): calls outside it are `UnknownFunction`, wrong arities
//! and statically detectable argument-type mismatches are `InvalidArgument`,
//! and the gated search, vector, and spatial functions fail closed with
//! `CapabilityUnavailable` until the engine reports the capability. The
//! checker additionally enforces parameter pairing, page bounds, identifier
//! discipline, reserved names, alias resolution, aggregate placement, and
//! the group-derivation rule; no violation degrades to a warning.

use std::collections::BTreeMap;

use super::{
    Capability, TqlCapabilities, TqlError, TqlErrorClass, TqlParameters,
    ast::{
        BinaryOperator, Expression, ExpressionKind, Literal, PageBound, PageBoundKind, PathRoot,
        PathSegment, ProjectionKind, SourceKind, Span, TqlPath, TqlQuery,
    },
};
use crate::query::spec::MAX_PAGE_LIMIT;
use crate::types::DocumentValue;

/// One registry entry of the closed `tql/1.0` function set.
struct Descriptor {
    /// The dotted function name.
    name: &'static str,
    /// The minimum argument count.
    min_args: usize,
    /// The maximum argument count, or `None` for unbounded.
    max_args: Option<usize>,
    /// Whether the function is an aggregate of Section 7.4.
    aggregate: bool,
    /// The capability the function is gated on, when one is.
    gated: Option<Capability>,
    /// The static argument shape the checker enforces.
    arg_shape: ArgShape,
}

/// The static argument shape of one registry function.
#[derive(Clone, Copy, Eq, PartialEq)]
enum ArgShape {
    /// Any expression is accepted; only literal domains of arithmetic and
    /// sign operators are checked separately.
    Any,
    /// Every literal argument must be a string.
    Strings,
    /// The single argument must be a path.
    Path,
    /// The single argument must be a comparison.
    Comparison,
}

impl Descriptor {
    /// Reports whether one argument count is inside the declared range.
    fn accepts(&self, args: usize) -> bool {
        args >= self.min_args && self.max_args.is_none_or(|max| args <= max)
    }
}

/// The closed `tql/1.0` function registry.
const REGISTRY: &[Descriptor] = &[
    Descriptor {
        name: "id",
        min_args: 0,
        max_args: Some(1),
        aggregate: false,
        gated: None,
        arg_shape: ArgShape::Any,
    },
    Descriptor {
        name: "len",
        min_args: 1,
        max_args: Some(1),
        aggregate: false,
        gated: None,
        arg_shape: ArgShape::Any,
    },
    Descriptor {
        name: "lower",
        min_args: 1,
        max_args: Some(1),
        aggregate: false,
        gated: None,
        arg_shape: ArgShape::Strings,
    },
    Descriptor {
        name: "upper",
        min_args: 1,
        max_args: Some(1),
        aggregate: false,
        gated: None,
        arg_shape: ArgShape::Strings,
    },
    Descriptor {
        name: "concat",
        min_args: 1,
        max_args: None,
        aggregate: false,
        gated: None,
        arg_shape: ArgShape::Strings,
    },
    Descriptor {
        name: "contains",
        min_args: 2,
        max_args: Some(2),
        aggregate: false,
        gated: None,
        arg_shape: ArgShape::Strings,
    },
    Descriptor {
        name: "starts_with",
        min_args: 2,
        max_args: Some(2),
        aggregate: false,
        gated: None,
        arg_shape: ArgShape::Strings,
    },
    Descriptor {
        name: "ends_with",
        min_args: 2,
        max_args: Some(2),
        aggregate: false,
        gated: None,
        arg_shape: ArgShape::Strings,
    },
    Descriptor {
        name: "if",
        min_args: 3,
        max_args: Some(3),
        aggregate: false,
        gated: None,
        arg_shape: ArgShape::Any,
    },
    Descriptor {
        name: "coalesce",
        min_args: 1,
        max_args: None,
        aggregate: false,
        gated: None,
        arg_shape: ArgShape::Any,
    },
    Descriptor {
        name: "exists",
        min_args: 1,
        max_args: Some(1),
        aggregate: false,
        gated: None,
        arg_shape: ArgShape::Path,
    },
    Descriptor {
        name: "missing",
        min_args: 1,
        max_args: Some(1),
        aggregate: false,
        gated: None,
        arg_shape: ArgShape::Path,
    },
    Descriptor {
        name: "count",
        min_args: 0,
        max_args: Some(1),
        aggregate: true,
        gated: None,
        arg_shape: ArgShape::Any,
    },
    Descriptor {
        name: "sum",
        min_args: 1,
        max_args: Some(1),
        aggregate: true,
        gated: None,
        arg_shape: ArgShape::Any,
    },
    Descriptor {
        name: "avg",
        min_args: 1,
        max_args: Some(1),
        aggregate: true,
        gated: None,
        arg_shape: ArgShape::Any,
    },
    Descriptor {
        name: "min",
        min_args: 1,
        max_args: Some(1),
        aggregate: true,
        gated: None,
        arg_shape: ArgShape::Any,
    },
    Descriptor {
        name: "max",
        min_args: 1,
        max_args: Some(1),
        aggregate: true,
        gated: None,
        arg_shape: ArgShape::Any,
    },
    Descriptor {
        name: "exact",
        min_args: 1,
        max_args: Some(1),
        aggregate: false,
        gated: None,
        arg_shape: ArgShape::Comparison,
    },
    Descriptor {
        name: "search",
        min_args: 2,
        max_args: Some(3),
        aggregate: false,
        gated: Some(Capability::Search),
        arg_shape: ArgShape::Any,
    },
    Descriptor {
        name: "boost",
        min_args: 2,
        max_args: Some(2),
        aggregate: false,
        gated: Some(Capability::Search),
        arg_shape: ArgShape::Any,
    },
    Descriptor {
        name: "score",
        min_args: 0,
        max_args: Some(0),
        aggregate: false,
        gated: Some(Capability::Search),
        arg_shape: ArgShape::Any,
    },
    Descriptor {
        name: "vector.search",
        min_args: 2,
        max_args: Some(3),
        aggregate: false,
        gated: Some(Capability::Vector),
        arg_shape: ArgShape::Any,
    },
    Descriptor {
        name: "vector.of",
        min_args: 1,
        max_args: None,
        aggregate: false,
        gated: Some(Capability::Vector),
        arg_shape: ArgShape::Any,
    },
    Descriptor {
        name: "spatial.within",
        min_args: 2,
        max_args: Some(2),
        aggregate: false,
        gated: Some(Capability::Spatial),
        arg_shape: ArgShape::Any,
    },
    Descriptor {
        name: "spatial.contains",
        min_args: 2,
        max_args: Some(2),
        aggregate: false,
        gated: Some(Capability::Spatial),
        arg_shape: ArgShape::Any,
    },
    Descriptor {
        name: "spatial.disjoint",
        min_args: 2,
        max_args: Some(2),
        aggregate: false,
        gated: Some(Capability::Spatial),
        arg_shape: ArgShape::Any,
    },
    Descriptor {
        name: "spatial.intersects",
        min_args: 2,
        max_args: Some(2),
        aggregate: false,
        gated: Some(Capability::Spatial),
        arg_shape: ArgShape::Any,
    },
    Descriptor {
        name: "spatial.circle",
        min_args: 4,
        max_args: Some(4),
        aggregate: false,
        gated: Some(Capability::Spatial),
        arg_shape: ArgShape::Any,
    },
    Descriptor {
        name: "spatial.point",
        min_args: 2,
        max_args: Some(2),
        aggregate: false,
        gated: Some(Capability::Spatial),
        arg_shape: ArgShape::Any,
    },
    Descriptor {
        name: "spatial.wkt",
        min_args: 1,
        max_args: Some(1),
        aggregate: false,
        gated: Some(Capability::Spatial),
        arg_shape: ArgShape::Any,
    },
    Descriptor {
        name: "spatial.distance",
        min_args: 2,
        max_args: Some(2),
        aggregate: false,
        gated: Some(Capability::Spatial),
        arg_shape: ArgShape::Any,
    },
];

/// Looks one name up in the registry.
fn descriptor(name: &str) -> Option<&'static Descriptor> {
    REGISTRY.iter().find(|descriptor| descriptor.name == name)
}

/// Returns every name of the closed registry, in declaration order.
///
/// The registry is part of the grammar version, so conformance corpora use
/// this enumeration to prove they invoke every entry; see the golden query
/// vector corpus in `tests/query/tql_golden_vectors.rs`.
pub(super) fn names() -> impl Iterator<Item = &'static str> {
    REGISTRY.iter().map(|descriptor| descriptor.name)
}

/// The engine-reserved collection that is never a TQL source: family state
/// is read only through the family seam.
const RESERVED_FAMILY_COLLECTION: &str = "terrabase.families";

/// Runs every static check over one parsed query.
///
/// The query is structurally parsed already; this pass rejects everything the
/// grammar accepts but the closed registry, the parameter-pairing rules, the
/// page bounds, the identifier discipline, the reserved names, and the
/// group-derivation rule do not.
pub(super) fn check(
    query: &TqlQuery,
    parameters: &TqlParameters,
    capabilities: TqlCapabilities,
) -> Result<(), TqlError> {
    let context = CheckContext {
        parameters,
        capabilities,
        source_alias: query.source.alias.clone(),
        load_aliases: query.loads.iter().map(|load| (load.alias.clone(), load.span)).collect(),
        projection_aliases: projection_aliases(query),
        group_names: group_names(query),
    };
    check_source(query)?;
    check_distinct_aliases(&context)?;
    let mut references = Vec::new();
    if let Some(filter) = &query.filter {
        check_expression(filter, &context, Location::Filter)?;
        collect_references(filter, &mut references);
    }
    if let ProjectionKind::Items(items) = &query.projection.kind {
        for item in items {
            check_expression(&item.expr, &context, Location::Projection)?;
            collect_references(&item.expr, &mut references);
        }
    }
    for key in &query.grouping {
        check_expression(&key.expr, &context, Location::Grouping)?;
        collect_references(&key.expr, &mut references);
    }
    for key in &query.ordering {
        check_expression(&key.expr, &context, Location::Ordering)?;
        collect_references(&key.expr, &mut references);
    }
    for bound in [query.paging.limit.as_ref(), query.paging.offset.as_ref()].into_iter().flatten() {
        if let PageBoundKind::Parameter(name) = &bound.kind {
            references.push((name.clone(), bound.span));
        }
    }
    check_parameters(&context, &references)?;
    check_bounds(query, &context)?;
    check_group_derivation(query, &context)?;
    Ok(())
}

/// Where one expression is being checked.
#[derive(Clone, Copy)]
enum Location {
    /// The `where` filter.
    Filter,
    /// A projection item.
    Projection,
    /// A grouping key.
    Grouping,
    /// An ordering key.
    Ordering,
}

/// The names and bindings one expression may resolve against.
struct CheckContext<'a> {
    parameters: &'a TqlParameters,
    capabilities: TqlCapabilities,
    source_alias: Option<String>,
    load_aliases: BTreeMap<String, Span>,
    projection_aliases: BTreeMap<String, Expression>,
    group_names: BTreeMap<String, Expression>,
}

/// Returns the projection aliases addressable in `order by`.
fn projection_aliases(query: &TqlQuery) -> BTreeMap<String, Expression> {
    let mut aliases = BTreeMap::new();
    if let ProjectionKind::Items(items) = &query.projection.kind {
        for item in items {
            if let Some(alias) = &item.alias {
                aliases.insert(alias.clone(), item.expr.clone());
            }
        }
    }
    aliases
}

/// Returns the group-key names addressable in `select` and `order by`: the
/// explicit alias, or the last member of the key's path.
fn group_names(query: &TqlQuery) -> BTreeMap<String, Expression> {
    let mut names = BTreeMap::new();
    for key in &query.grouping {
        let name = key.alias.clone().or_else(|| last_member_name(&key.expr));
        if let Some(name) = name {
            names.insert(name, key.expr.clone());
        }
    }
    names
}

/// Returns the last member name of a path expression, when the expression
/// is one.
fn last_member_name(expression: &Expression) -> Option<String> {
    let ExpressionKind::Path(TqlPath { root, segments, .. }) = &expression.kind else {
        return None;
    };
    match segments.last() {
        Some(PathSegment::Member(member)) => Some(member.clone()),
        Some(_) => None,
        None => match root {
            PathRoot::Member(member) => Some(member.clone()),
            PathRoot::Metadata(_) => None,
        },
    }
}

/// Checks the source, alias, and load-clause names.
fn check_source(query: &TqlQuery) -> Result<(), TqlError> {
    if let SourceKind::Collection(name) = &query.source.kind {
        reject_reserved_name(name, query.source.span)?;
        check_identifier(name, query.source.span)?;
    }
    if let Some(alias) = &query.source.alias {
        reject_reserved_name(alias, query.source.span)?;
        check_identifier(alias, query.source.span)?;
    }
    for load in &query.loads {
        reject_reserved_name(&load.alias, load.span)?;
        check_identifier(&load.alias, load.span)?;
        check_path(&load.path)?;
    }
    for include in &query.includes {
        check_path(include)?;
    }
    Ok(())
}

/// Rejects the reserved `vector` and `spatial` names and the engine-reserved
/// family collection.
fn reject_reserved_name(name: &str, span: Span) -> Result<(), TqlError> {
    if name.eq_ignore_ascii_case("vector")
        || name.eq_ignore_ascii_case("spatial")
        || name == RESERVED_FAMILY_COLLECTION
    {
        return Err(TqlError::at(TqlErrorClass::ReservedName, name, span));
    }
    Ok(())
}

/// Rejects one load alias that collides with the source alias. Two load
/// items cannot collide: the alias map keys are unique by construction.
fn check_distinct_aliases(context: &CheckContext<'_>) -> Result<(), TqlError> {
    if let Some(source_alias) = &context.source_alias
        && let Some((alias, span)) = context.load_aliases.get_key_value(source_alias)
    {
        return Err(TqlError::at(TqlErrorClass::InvalidArgument, alias, *span));
    }
    Ok(())
}

/// Checks one expression subtree.
fn check_expression(
    expression: &Expression,
    context: &CheckContext<'_>,
    location: Location,
) -> Result<(), TqlError> {
    match &expression.kind {
        ExpressionKind::Literal(_) | ExpressionKind::ParameterRef(_) => Ok(()),
        ExpressionKind::Path(path) => check_path(path),
        ExpressionKind::Call(call) => check_call(call, context, location),
        ExpressionKind::Unary { op, operand } => {
            if let ExpressionKind::Literal(literal) = &operand.kind
                && !matches!(literal, Literal::Integer(_) | Literal::Number(_))
            {
                return Err(TqlError::at(
                    TqlErrorClass::InvalidArgument,
                    op.symbol(),
                    operand.span,
                ));
            }
            check_expression(operand, context, location)
        }
        ExpressionKind::Binary { op, left, right } => {
            if matches!(
                op,
                BinaryOperator::Add
                    | BinaryOperator::Subtract
                    | BinaryOperator::Multiply
                    | BinaryOperator::Divide
                    | BinaryOperator::Modulo
            ) {
                for operand in [left, right] {
                    if matches!(&operand.kind, ExpressionKind::Literal(literal) if !matches!(literal, Literal::Integer(_) | Literal::Number(_)))
                    {
                        return Err(TqlError::at(
                            TqlErrorClass::InvalidArgument,
                            op.symbol(),
                            operand.span,
                        ));
                    }
                }
            }
            check_expression(left, context, location)?;
            check_expression(right, context, location)
        }
        ExpressionKind::Between { expr, low, high } => {
            check_expression(expr, context, location)?;
            check_expression(low, context, location)?;
            check_expression(high, context, location)
        }
        ExpressionKind::In { expr, list, .. } => {
            check_expression(expr, context, location)?;
            for item in list {
                check_expression(item, context, location)?;
            }
            Ok(())
        }
        ExpressionKind::Logical { operands, .. } => {
            for operand in operands {
                check_expression(operand, context, location)?;
            }
            Ok(())
        }
        ExpressionKind::Not { operand } => check_expression(operand, context, location),
        ExpressionKind::ObjectCtor { members } => {
            for member in members {
                check_identifier(&member.key, member.span)?;
                check_expression(&member.value, context, location)?;
            }
            Ok(())
        }
        ExpressionKind::ArrayCtor { elements } => {
            for element in elements {
                check_expression(element, context, location)?;
            }
            Ok(())
        }
    }
}

/// Checks one registry call: closed registry, capability gate, arity,
/// argument shape, and aggregate placement.
fn check_call(
    call: &super::ast::FunctionCall,
    context: &CheckContext<'_>,
    location: Location,
) -> Result<(), TqlError> {
    let descriptor = descriptor(&call.name)
        .ok_or_else(|| TqlError::at(TqlErrorClass::UnknownFunction, &call.name, call.span))?;
    if let Some(capability) = descriptor.gated
        && !context.capabilities.is_available(capability)
    {
        return Err(TqlError::at(TqlErrorClass::CapabilityUnavailable, &call.name, call.span));
    }
    if !descriptor.accepts(call.args.len()) {
        return Err(TqlError::at(TqlErrorClass::InvalidArgument, call.name.as_str(), call.span));
    }
    if descriptor.arg_shape == ArgShape::Strings {
        for argument in &call.args {
            if matches!(&argument.kind, ExpressionKind::Literal(literal) if !matches!(literal, Literal::String(_)))
            {
                return Err(TqlError::at(
                    TqlErrorClass::InvalidArgument,
                    call.name.as_str(),
                    argument.span,
                ));
            }
        }
    }
    if descriptor.arg_shape == ArgShape::Path
        && !matches!(
            call.args.first().map(|argument| &argument.kind),
            Some(ExpressionKind::Path(_))
        )
    {
        return Err(TqlError::at(TqlErrorClass::InvalidArgument, call.name.as_str(), call.span));
    }
    if descriptor.arg_shape == ArgShape::Comparison {
        let is_comparison = matches!(
            call.args.first().map(|argument| &argument.kind),
            Some(ExpressionKind::Binary {
                op: BinaryOperator::Equals
                    | BinaryOperator::NotEquals
                    | BinaryOperator::Less
                    | BinaryOperator::LessOrEqual
                    | BinaryOperator::Greater
                    | BinaryOperator::GreaterOrEqual,
                ..
            })
        );
        if !is_comparison {
            return Err(TqlError::at(
                TqlErrorClass::InvalidArgument,
                call.name.as_str(),
                call.span,
            ));
        }
    }
    // `id(alias)` resolves its optional argument against the declared
    // aliases; an unknown alias is an invalid argument.
    if call.name == "id"
        && let Some(argument) = call.args.first()
    {
        let is_alias = matches!(
            &argument.kind,
            ExpressionKind::Path(TqlPath { root: PathRoot::Member(name), segments, .. })
                if segments.is_empty()
                    && (Some(name.as_str()) == context.source_alias.as_deref()
                        || context.load_aliases.contains_key(name))
        );
        if !is_alias {
            return Err(TqlError::at(TqlErrorClass::InvalidArgument, "id", argument.span));
        }
    }
    // A group-scope aggregate inside the filter is a static error; the
    // per-document scope over any-element navigation stays legal anywhere.
    if descriptor.aggregate
        && matches!(location, Location::Filter)
        && (call.args.is_empty() || !is_multi_valued(&call.args[0]))
    {
        return Err(TqlError::at(TqlErrorClass::InvalidArgument, &call.name, call.span));
    }
    for argument in &call.args {
        check_expression(argument, context, location)?;
    }
    Ok(())
}

/// Checks one path's member names.
fn check_path(path: &TqlPath) -> Result<(), TqlError> {
    if let PathRoot::Member(name) = &path.root {
        check_identifier(name, path.span)?;
    }
    for segment in &path.segments {
        if let PathSegment::Member(name) = segment {
            check_identifier(name, path.span)?;
        }
    }
    Ok(())
}

/// Checks one identifier against the shared identifier discipline.
fn check_identifier(name: &str, span: Span) -> Result<(), TqlError> {
    if !super::valid_identifier(name) {
        return Err(TqlError::at(TqlErrorClass::InvalidIdentifier, name, span));
    }
    Ok(())
}

/// Reports whether one expression is statically multi-valued: it contains an
/// any-element navigation, which is the per-document aggregate scope of
/// Section 7.4. The judgment never depends on the data.
fn is_multi_valued(expression: &Expression) -> bool {
    match &expression.kind {
        ExpressionKind::Path(path) => {
            path.segments.iter().any(|segment| matches!(segment, PathSegment::AnyElement))
        }
        ExpressionKind::Literal(_) | ExpressionKind::ParameterRef(_) => false,
        ExpressionKind::Call(call) => call.args.iter().any(is_multi_valued),
        ExpressionKind::Unary { operand, .. } | ExpressionKind::Not { operand } => {
            is_multi_valued(operand)
        }
        ExpressionKind::Binary { left, right, .. } => {
            is_multi_valued(left) || is_multi_valued(right)
        }
        ExpressionKind::Between { expr, low, high } => {
            is_multi_valued(expr) || is_multi_valued(low) || is_multi_valued(high)
        }
        ExpressionKind::In { expr, list, .. } => {
            is_multi_valued(expr) || list.iter().any(is_multi_valued)
        }
        ExpressionKind::Logical { operands, .. } => operands.iter().any(is_multi_valued),
        ExpressionKind::ObjectCtor { members } => {
            members.iter().any(|member| is_multi_valued(&member.value))
        }
        ExpressionKind::ArrayCtor { elements } => elements.iter().any(is_multi_valued),
    }
}

/// Collects parameter references in traversal order.
fn collect_references(expression: &Expression, references: &mut Vec<(String, Span)>) {
    match &expression.kind {
        ExpressionKind::ParameterRef(name) => references.push((name.clone(), expression.span)),
        ExpressionKind::Literal(_) | ExpressionKind::Path(_) => {}
        ExpressionKind::Call(call) => {
            for argument in &call.args {
                collect_references(argument, references);
            }
        }
        ExpressionKind::Unary { operand, .. } | ExpressionKind::Not { operand } => {
            collect_references(operand, references);
        }
        ExpressionKind::Binary { left, right, .. } => {
            collect_references(left, references);
            collect_references(right, references);
        }
        ExpressionKind::Between { expr, low, high } => {
            collect_references(expr, references);
            collect_references(low, references);
            collect_references(high, references);
        }
        ExpressionKind::In { expr, list, .. } => {
            collect_references(expr, references);
            for item in list {
                collect_references(item, references);
            }
        }
        ExpressionKind::Logical { operands, .. } => {
            for operand in operands {
                collect_references(operand, references);
            }
        }
        ExpressionKind::ObjectCtor { members } => {
            for member in members {
                collect_references(&member.value, references);
            }
        }
        ExpressionKind::ArrayCtor { elements } => {
            for element in elements {
                collect_references(element, references);
            }
        }
    }
}

/// Enforces strict parameter pairing in both directions.
fn check_parameters(
    context: &CheckContext<'_>,
    references: &[(String, Span)],
) -> Result<(), TqlError> {
    for (name, span) in references {
        check_identifier(name, *span)?;
        if !context.parameters.contains(name) {
            return Err(TqlError::at(TqlErrorClass::UnboundParameter, name, *span));
        }
    }
    for name in context.parameters.names() {
        if !references.iter().any(|(referenced, _)| referenced == name) {
            return Err(TqlError::binding(TqlErrorClass::UnexpectedParameter, name));
        }
    }
    Ok(())
}

/// Validates the page bounds: `limit >= 1` and within the seam's maximum,
/// `offset >= 0`, at parse time for literals and at binding time for
/// parameters.
fn check_bounds(query: &TqlQuery, context: &CheckContext<'_>) -> Result<(), TqlError> {
    if let Some(limit) = &query.paging.limit {
        check_bound(limit, context, true)?;
    }
    if let Some(offset) = &query.paging.offset {
        check_bound(offset, context, false)?;
    }
    Ok(())
}

/// Validates one page bound.
fn check_bound(
    bound: &PageBound,
    context: &CheckContext<'_>,
    is_limit: bool,
) -> Result<(), TqlError> {
    let value = match &bound.kind {
        PageBoundKind::Literal(value) => *value,
        PageBoundKind::Parameter(name) => match context.parameters.get(name) {
            Some(DocumentValue::Integer(value)) if *value >= 0 => u64::try_from(*value)
                .map_err(|_| TqlError::at(TqlErrorClass::InvalidBound, name, bound.span))?,
            _ => return Err(TqlError::at(TqlErrorClass::InvalidBound, name, bound.span)),
        },
    };
    if is_limit && (value == 0 || value > u64::from(MAX_PAGE_LIMIT)) {
        return Err(match &bound.kind {
            PageBoundKind::Parameter(name) => {
                TqlError::at(TqlErrorClass::InvalidBound, name, bound.span)
            }
            PageBoundKind::Literal(_) => TqlError::plain(TqlErrorClass::InvalidBound, bound.span),
        });
    }
    Ok(())
}

/// Enforces the group-derivation rule: with `group by`, every projection
/// item and ordering key is an aggregate or functionally derived from the
/// group keys.
fn check_group_derivation(query: &TqlQuery, context: &CheckContext<'_>) -> Result<(), TqlError> {
    if query.grouping.is_empty() {
        return Ok(());
    }
    let reject = |span| TqlError::at(TqlErrorClass::InvalidArgument, "group by", span);
    if let ProjectionKind::WholeDocument = query.projection.kind {
        return Err(reject(query.projection.span));
    }
    if let ProjectionKind::Items(items) = &query.projection.kind {
        for item in items {
            if !is_derived(&item.expr, context) {
                return Err(reject(item.expr.span));
            }
        }
    }
    for key in &query.ordering {
        let resolved = resolve_alias(&key.expr, context);
        let expression = resolved.as_ref().unwrap_or(&key.expr);
        if !is_derived(expression, context) {
            return Err(reject(key.expr.span));
        }
    }
    Ok(())
}

/// Resolves a single-member alias reference against the projection and group
/// names addressable in `order by`.
fn resolve_alias<'a>(
    expression: &'a Expression,
    context: &'a CheckContext<'_>,
) -> Option<Expression> {
    let name = single_member_name(expression)?;
    context.projection_aliases.get(&name).or_else(|| context.group_names.get(&name)).cloned()
}

/// Reports whether one expression is functionally derived from the group
/// keys: an aggregate, a group key or its name, a literal or parameter, or a
/// combination of derived parts.
fn is_derived(expression: &Expression, context: &CheckContext<'_>) -> bool {
    match &expression.kind {
        ExpressionKind::Call(call) => {
            descriptor(&call.name).is_some_and(|descriptor| descriptor.aggregate)
                || call.args.iter().all(|argument| is_derived(argument, context))
        }
        ExpressionKind::Literal(_) | ExpressionKind::ParameterRef(_) => true,
        ExpressionKind::Path(path) => {
            if let Some(name) = single_member_name(expression)
                && context.group_names.contains_key(&name)
            {
                return true;
            }
            context.group_names.values().any(|key| {
                matches!(&key.kind, ExpressionKind::Path(key_path) if same_path_shape(key_path, path))
            })
        }
        ExpressionKind::Unary { operand, .. } | ExpressionKind::Not { operand } => {
            is_derived(operand, context)
        }
        ExpressionKind::Binary { left, right, .. } => {
            is_derived(left, context) && is_derived(right, context)
        }
        ExpressionKind::Between { expr, low, high } => {
            is_derived(expr, context) && is_derived(low, context) && is_derived(high, context)
        }
        ExpressionKind::In { expr, list, .. } => {
            is_derived(expr, context) && list.iter().all(|item| is_derived(item, context))
        }
        ExpressionKind::Logical { operands, .. } => {
            operands.iter().all(|operand| is_derived(operand, context))
        }
        ExpressionKind::ObjectCtor { members } => {
            members.iter().all(|member| is_derived(&member.value, context))
        }
        ExpressionKind::ArrayCtor { elements } => {
            elements.iter().all(|element| is_derived(element, context))
        }
    }
}

/// Reports whether two paths address the same members and segments,
/// ignoring the source spans they were parsed from.
fn same_path_shape(left: &TqlPath, right: &TqlPath) -> bool {
    left.root == right.root && left.segments == right.segments
}

/// Returns the single member name of a bare path, when one is addressed.
fn single_member_name(expression: &Expression) -> Option<String> {
    let ExpressionKind::Path(TqlPath { root: PathRoot::Member(name), segments, .. }) =
        &expression.kind
    else {
        return None;
    };
    if segments.is_empty() { Some(name.clone()) } else { None }
}
