//! Lowering the typed TQL AST onto the W3-2 typed compile seam.
//!
//! Lowering is exact or fails closed: every construct the current seam cannot
//! express without changing results is rejected with
//! [`TqlErrorClass::UnsupportedForPlanning`] naming the construct and its
//! position. The lowerable surface is the seam's own grammar: one user
//! collection (aliased or not), a flat conjunction of top-level field
//! comparisons, presence tests, inclusive ranges (`between`), membership
//! (`in`, `all in`), and the string pattern functions against literals or
//! bound parameters; an ordering over top-level fields with any explicit
//! nulls placement; a top-level field projection without renaming, extended
//! with load-alias names and fields of loaded references; the reference
//! bindings of a `load` clause; and the page bounds. Everything else in
//! `tql/1.0` parses and checks, then fails closed here until the seam gains
//! the shapes ADR 0009 lists for it; that extension belongs to the seam
//! owner, not this packet.

use super::{
    TqlError, TqlErrorClass, TqlParameters,
    ast::{
        BinaryOperator, Direction, Expression, ExpressionKind, Literal, LogicalOperator,
        NullsOrder, PageBound, PageBoundKind, PathRoot, PathSegment, ProjectionKind, Quantifier,
        SourceKind, TqlPath, TqlQuery,
    },
};
use crate::query::spec::{
    NullsPlacement, OrderDirection, OrderingKey, QueryCondition, QueryLoad, QueryOrdering,
    QueryPaging, QueryPredicate, QueryProjection, QuerySource, StalenessMode, StalenessPolicy,
};
use crate::query::{QueryInput, QueryPlan, QuerySpec, QueryStore};
use crate::types::{Cancellation, CollectionName, Deadline, DocumentValue, TenantId};

/// Lowers one checked TQL query into a typed query specification.
///
/// The specification is the seam's own input form, so the result compiles and
/// executes exactly like a hand-built typed query; execution parity is proved
/// against that equivalence. The tenant claim, deadline, and cancellation are
/// request context the TQL grammar does not carry and the caller supplies.
///
/// # Errors
///
/// Returns [`TqlErrorClass::UnsupportedForPlanning`] for a construct the seam
/// cannot express exactly, [`TqlErrorClass::InvalidIdentifier`] for a name
/// the seam's identifier discipline rejects, and
/// [`TqlErrorClass::InvalidRequest`] for an invalid request context.
pub fn lower_to_spec(
    query: &TqlQuery,
    parameters: &TqlParameters,
    tenant_claim: TenantId,
    deadline: Deadline,
    cancellation: Cancellation,
) -> Result<QuerySpec, TqlError> {
    lower_inner(query, parameters, tenant_claim, deadline, cancellation)
        .map_err(|error| error.with_version(query.grammar_version))
}

/// The lowering body; the public entry attaches the artifact version to
/// every failure, as Section 5.2 of the specification requires.
fn lower_inner(
    query: &TqlQuery,
    parameters: &TqlParameters,
    tenant_claim: TenantId,
    deadline: Deadline,
    cancellation: Cancellation,
) -> Result<QuerySpec, TqlError> {
    let clauses = lower_clauses(query, parameters)?;
    let staleness = StalenessPolicy::try_new(StalenessMode::Strict, Vec::new())
        .map_err(|_| TqlError::plain(TqlErrorClass::UnsupportedForPlanning, query.span))?;
    let spec = QuerySpec::try_new(
        tenant_claim,
        clauses.source,
        clauses.predicate,
        clauses.ordering,
        clauses.projection,
        clauses.paging,
        staleness,
        deadline,
        cancellation,
    )
    .map_err(|_| TqlError::plain(TqlErrorClass::InvalidRequest, query.span))?;
    spec.with_loads(clauses.loads)
        .map_err(|_| TqlError::plain(TqlErrorClass::InvalidRequest, query.span))
}

/// The lowered clauses of one checked TQL query: every grammar clause
/// normalized onto the seam's typed form, independent of the request context
/// (tenant claim, deadline, cancellation) the caller binds afterwards.
pub(in crate::query) struct LoweredClauses {
    /// The lowered single-collection source.
    pub(in crate::query) source: QuerySource,
    /// The reference-resolution `load` bindings.
    pub(in crate::query) loads: Vec<QueryLoad>,
    /// The lowered `where` conjunction.
    pub(in crate::query) predicate: QueryPredicate,
    /// The lowered `order by` keys.
    pub(in crate::query) ordering: QueryOrdering,
    /// The lowered `select` projection.
    pub(in crate::query) projection: QueryProjection,
    /// The lowered page bounds.
    pub(in crate::query) paging: QueryPaging,
}

/// Lowers every clause of one checked TQL query onto the seam's typed form.
///
/// This is the one clause-lowering body shared by every entry: the
/// request-bound path ([`lower_to_spec`]) and the grammar-only compile seam
/// (`QueryStore::compile` for `TqlQueryText`) lower identically, so a text
/// behaves the same wherever it enters.
///
/// # Errors
///
/// Returns [`TqlErrorClass::UnsupportedForPlanning`] for a construct the seam
/// cannot express exactly and [`TqlErrorClass::InvalidIdentifier`] for a
/// name the seam's identifier discipline rejects.
pub(in crate::query) fn lower_clauses(
    query: &TqlQuery,
    parameters: &TqlParameters,
) -> Result<LoweredClauses, TqlError> {
    let source_alias = query.source.alias.as_deref();
    let source = lower_source(query)?;
    let loads = lower_loads(query, source_alias)?;
    if let Some(key) = query.grouping.first() {
        return Err(TqlError::at(TqlErrorClass::UnsupportedForPlanning, "group by", key.span));
    }
    if let Some(include) = query.includes.first() {
        return Err(TqlError::at(TqlErrorClass::UnsupportedForPlanning, "include", include.span));
    }
    let predicate = lower_predicate(query, source_alias, parameters)?;
    let ordering = lower_ordering(query, source_alias)?;
    let projection = lower_projection(query, source_alias, &loads)?;
    let paging = lower_paging(query, parameters)?;
    Ok(LoweredClauses { source, loads, predicate, ordering, projection, paging })
}

/// Lowers the `load` clause into reference-resolution bindings. A load path
/// is the seam's top-level field form — a bare member or one member under
/// the source alias — and any other path shape (nested navigation, a
/// metadata root) has no seam plan. The array-alias load of V1 has no
/// `tql/1.0` spelling, so nothing here needs to reject it.
fn lower_loads(query: &TqlQuery, source_alias: Option<&str>) -> Result<Vec<QueryLoad>, TqlError> {
    let mut loads = Vec::new();
    for load in &query.loads {
        let field = path_field(&load.path, source_alias).ok_or_else(|| {
            TqlError::at(TqlErrorClass::UnsupportedForPlanning, "load", load.span)
        })?;
        let binding = QueryLoad::try_new(&field, &load.alias)
            .map_err(|_| TqlError::at(TqlErrorClass::InvalidIdentifier, &load.alias, load.span))?;
        loads.push(binding);
    }
    Ok(loads)
}

/// Lowers and compiles one checked TQL query through the one compile seam.
///
/// # Errors
///
/// Returns the lowering errors of [`lower_to_spec`] plus
/// [`TqlErrorClass::InvalidRequest`] when the seam rejects the compiled
/// specification.
pub fn compile(
    store: &QueryStore<'_>,
    query: &TqlQuery,
    parameters: &TqlParameters,
    tenant_claim: TenantId,
    deadline: Deadline,
    cancellation: Cancellation,
) -> Result<QueryPlan, TqlError> {
    let spec = lower_to_spec(query, parameters, tenant_claim, deadline, cancellation)?;
    store.compile(QueryInput::typed(spec)).map_err(|_| {
        TqlError::plain(TqlErrorClass::InvalidRequest, query.span)
            .with_version(query.grammar_version)
    })
}

/// Lowers the source clause; `@all_docs` is beyond the seam's
/// single-collection source.
fn lower_source(query: &TqlQuery) -> Result<QuerySource, TqlError> {
    let SourceKind::Collection(name) = &query.source.kind else {
        return Err(TqlError::at(
            TqlErrorClass::UnsupportedForPlanning,
            "@all_docs",
            query.source.span,
        ));
    };
    let collection = CollectionName::try_new(name)
        .map_err(|_| TqlError::at(TqlErrorClass::InvalidIdentifier, name, query.source.span))?;
    QuerySource::try_new(collection)
        .map_err(|_| TqlError::at(TqlErrorClass::InvalidIdentifier, name, query.source.span))
}

/// Lowers the filter into the seam's condition conjunction.
fn lower_predicate(
    query: &TqlQuery,
    source_alias: Option<&str>,
    parameters: &TqlParameters,
) -> Result<QueryPredicate, TqlError> {
    let Some(filter) = &query.filter else {
        return QueryPredicate::all(Vec::new())
            .map_err(|_| TqlError::plain(TqlErrorClass::UnsupportedForPlanning, query.span));
    };
    let mut conjuncts = Vec::new();
    flatten_and(filter, &mut conjuncts);
    let mut conditions = Vec::new();
    for conjunct in conjuncts {
        conditions.push(lower_condition(conjunct, source_alias, parameters)?);
    }
    QueryPredicate::all(conditions)
        .map_err(|_| TqlError::plain(TqlErrorClass::UnsupportedForPlanning, filter.span))
}

/// Flattens one `and` tree into its conjuncts.
fn flatten_and<'expression>(
    expression: &'expression Expression,
    conjuncts: &mut Vec<&'expression Expression>,
) {
    match &expression.kind {
        ExpressionKind::Logical { op: LogicalOperator::And, operands } => {
            for operand in operands {
                flatten_and(operand, conjuncts);
            }
        }
        _ => conjuncts.push(expression),
    }
}

/// Lowers one conjunct into one seam condition.
fn lower_condition(
    conjunct: &Expression,
    source_alias: Option<&str>,
    parameters: &TqlParameters,
) -> Result<QueryCondition, TqlError> {
    match &conjunct.kind {
        ExpressionKind::Binary { op, left, right } => {
            let field =
                simple_field(left, source_alias).ok_or_else(|| unsupported_expression(left))?;
            let value = literal_or_bound(right, parameters)?;
            let condition = match op {
                BinaryOperator::Equals => QueryCondition::Equals { field, value },
                BinaryOperator::NotEquals => QueryCondition::NotEquals { field, value },
                BinaryOperator::Less => QueryCondition::LessThan { field, value },
                BinaryOperator::LessOrEqual => QueryCondition::LessOrEqual { field, value },
                BinaryOperator::Greater => QueryCondition::GreaterThan { field, value },
                BinaryOperator::GreaterOrEqual => QueryCondition::GreaterOrEqual { field, value },
                BinaryOperator::Add
                | BinaryOperator::Subtract
                | BinaryOperator::Multiply
                | BinaryOperator::Divide
                | BinaryOperator::Modulo => {
                    return Err(TqlError::at(
                        TqlErrorClass::UnsupportedForPlanning,
                        "arithmetic",
                        conjunct.span,
                    ));
                }
            };
            Ok(condition)
        }
        ExpressionKind::Call(call) if call.name == "exists" || call.name == "missing" => {
            let argument = call.args.first().ok_or_else(|| {
                TqlError::at(TqlErrorClass::UnsupportedForPlanning, "call", conjunct.span)
            })?;
            let field = simple_field(argument, source_alias)
                .ok_or_else(|| unsupported_expression(argument))?;
            Ok(if call.name == "exists" {
                QueryCondition::Exists { field }
            } else {
                QueryCondition::Missing { field }
            })
        }
        ExpressionKind::Call(call)
            if call.name == "starts_with"
                || call.name == "ends_with"
                || call.name == "contains" =>
        {
            let (field, pattern) = pattern_arguments(call, source_alias, parameters, conjunct)?;
            Ok(match call.name.as_str() {
                "starts_with" => QueryCondition::StartsWith { field, value: pattern },
                "ends_with" => QueryCondition::EndsWith { field, value: pattern },
                _ => QueryCondition::Contains { field, value: pattern },
            })
        }
        ExpressionKind::Between { expr, low, high } => {
            let field =
                simple_field(expr, source_alias).ok_or_else(|| unsupported_expression(expr))?;
            let low = literal_or_bound(low, parameters)?;
            let high = literal_or_bound(high, parameters)?;
            Ok(QueryCondition::Between { field, low, high })
        }
        ExpressionKind::In { quantifier, expr, list } => {
            let field =
                simple_field(expr, source_alias).ok_or_else(|| unsupported_expression(expr))?;
            let mut values = Vec::with_capacity(list.len());
            for item in list {
                values.push(literal_or_bound(item, parameters)?);
            }
            Ok(match quantifier {
                Quantifier::Any => QueryCondition::In { field, values },
                Quantifier::All => QueryCondition::AllIn { field, values },
            })
        }
        _ => Err(unsupported_expression(conjunct)),
    }
}

/// Resolves one pattern call's arguments: the seam field and the string
/// pattern. The registry checks the call's shape; this resolves its values.
fn pattern_arguments(
    call: &super::ast::FunctionCall,
    source_alias: Option<&str>,
    parameters: &TqlParameters,
    conjunct: &Expression,
) -> Result<(String, String), TqlError> {
    let field =
        call.args.first().and_then(|argument| simple_field(argument, source_alias)).ok_or_else(
            || TqlError::at(TqlErrorClass::UnsupportedForPlanning, "call", conjunct.span),
        )?;
    let pattern = call
        .args
        .get(1)
        .map(|pattern| literal_or_bound(pattern, parameters))
        .transpose()
        .map_err(|_| TqlError::at(TqlErrorClass::UnsupportedForPlanning, "call", conjunct.span))?
        .and_then(|value| match value {
            DocumentValue::String(text) => Some(text),
            _ => None,
        })
        .ok_or_else(|| {
            TqlError::at(TqlErrorClass::UnsupportedForPlanning, "call", conjunct.span)
        })?;
    Ok((field, pattern))
}

/// Resolves one expression to the seam's top-level field form: a bare member
/// path, or one member under the source alias. Metadata roots, navigation
/// segments, and non-path expressions have no seam field.
fn simple_field(expression: &Expression, source_alias: Option<&str>) -> Option<String> {
    let ExpressionKind::Path(path) = &expression.kind else {
        return None;
    };
    path_field(path, source_alias)
}

/// Resolves one path to the seam's top-level field form: a bare member, or
/// one member under the source alias.
fn path_field(path: &TqlPath, source_alias: Option<&str>) -> Option<String> {
    let PathRoot::Member(name) = &path.root else {
        return None;
    };
    match path.segments.as_slice() {
        [] if Some(name.as_str()) != source_alias => Some(name.clone()),
        [PathSegment::Member(field)] if Some(name.as_str()) == source_alias => Some(field.clone()),
        _ => None,
    }
}

/// Builds the unsupported error for one expression, naming its construct.
fn unsupported_expression(expression: &Expression) -> TqlError {
    let construct = match &expression.kind {
        ExpressionKind::Logical { op: LogicalOperator::Or, .. } => "or",
        ExpressionKind::Logical { .. } => "and",
        ExpressionKind::Not { .. } => "not",
        ExpressionKind::Between { .. } => "between",
        ExpressionKind::In { .. } => "in",
        ExpressionKind::Unary { .. } => "unary",
        ExpressionKind::Binary { op, .. } => match op {
            BinaryOperator::Add
            | BinaryOperator::Subtract
            | BinaryOperator::Multiply
            | BinaryOperator::Divide
            | BinaryOperator::Modulo => "arithmetic",
            _ => "comparison",
        },
        ExpressionKind::Call(_) => "call",
        ExpressionKind::ObjectCtor { .. } => "object constructor",
        ExpressionKind::ArrayCtor { .. } => "array constructor",
        ExpressionKind::Path(path) => match path.root {
            PathRoot::Metadata(_) => "metadata",
            PathRoot::Member(_) => "path",
        },
        ExpressionKind::Literal(_) | ExpressionKind::ParameterRef(_) => "literal",
    };
    TqlError::at(TqlErrorClass::UnsupportedForPlanning, construct, expression.span)
}

/// Resolves one comparison's right-hand side: a literal, or a bound
/// parameter's value.
fn literal_or_bound(
    expression: &Expression,
    parameters: &TqlParameters,
) -> Result<DocumentValue, TqlError> {
    match &expression.kind {
        ExpressionKind::Literal(literal) => Ok(match literal {
            Literal::Null => DocumentValue::Null,
            Literal::Boolean(value) => DocumentValue::Boolean(*value),
            Literal::Integer(value) => DocumentValue::Integer(*value),
            Literal::Number(value) => DocumentValue::Number(*value),
            Literal::String(value) => DocumentValue::String(value.clone()),
        }),
        ExpressionKind::ParameterRef(name) => parameters
            .get(name)
            .cloned()
            .ok_or_else(|| TqlError::binding(TqlErrorClass::UnboundParameter, name)),
        _ => Err(unsupported_expression(expression)),
    }
}

/// Lowers the ordering keys and their explicit nulls placement.
fn lower_ordering(query: &TqlQuery, source_alias: Option<&str>) -> Result<QueryOrdering, TqlError> {
    let mut keys = Vec::new();
    for key in &query.ordering {
        let field = simple_field(&key.expr, source_alias)
            .ok_or_else(|| unsupported_expression(&key.expr))?;
        let direction = match key.direction {
            None | Some(Direction::Ascending) => OrderDirection::Ascending,
            Some(Direction::Descending) => OrderDirection::Descending,
        };
        // An explicit placement is direction-independent — the seam's
        // ordering key carries it for either direction — and an omitted
        // placement stays the seam's own total document order.
        let nulls = key.nulls.map(|nulls| match nulls {
            NullsOrder::First => NullsPlacement::First,
            NullsOrder::Last => NullsPlacement::Last,
        });
        let ordering_key = OrderingKey::try_new_with_nulls(
            &field,
            direction,
            nulls.unwrap_or(NullsPlacement::Implicit),
        )
        .map_err(|_| TqlError::at(TqlErrorClass::InvalidIdentifier, &field, key.span))?;
        keys.push(ordering_key);
    }
    QueryOrdering::try_new(keys)
        .map_err(|_| TqlError::plain(TqlErrorClass::UnsupportedForPlanning, query.span))
}

/// Lowers the projection: the whole document, or a top-level field subset
/// without renaming, where a load alias names the referenced document and
/// `alias.field` names one of its fields.
fn lower_projection(
    query: &TqlQuery,
    source_alias: Option<&str>,
    loads: &[QueryLoad],
) -> Result<QueryProjection, TqlError> {
    if query.projection.distinct {
        return Err(TqlError::at(
            TqlErrorClass::UnsupportedForPlanning,
            "distinct",
            query.projection.span,
        ));
    }
    match &query.projection.kind {
        ProjectionKind::WholeDocument => Ok(QueryProjection::whole_document()),
        ProjectionKind::Items(items) => {
            let load_aliases: Vec<&str> = loads.iter().map(QueryLoad::alias).collect();
            let mut names: Vec<String> = Vec::new();
            for item in items {
                if item.alias.is_some() {
                    return Err(TqlError::at(
                        TqlErrorClass::UnsupportedForPlanning,
                        "alias",
                        item.span,
                    ));
                }
                let field = qualified_field(&item.expr, source_alias, &load_aliases)
                    .ok_or_else(|| unsupported_expression(&item.expr))?;
                names.push(field);
            }
            let borrowed: Vec<&str> = names.iter().map(String::as_str).collect();
            QueryProjection::fields(&borrowed)
                .map_err(|_| TqlError::plain(TqlErrorClass::UnsupportedForPlanning, query.span))
        }
    }
}

/// Resolves one projection item to its projected name: a load alias alone
/// projects the referenced document (`alias`), a member under a load alias
/// projects one of its fields (`alias.field`), and every other path takes
/// the seam's top-level source-field form.
fn qualified_field(
    expression: &Expression,
    source_alias: Option<&str>,
    load_aliases: &[&str],
) -> Option<String> {
    let ExpressionKind::Path(TqlPath { root, segments, .. }) = &expression.kind else {
        return None;
    };
    let PathRoot::Member(name) = root else {
        return None;
    };
    match segments.as_slice() {
        [] if load_aliases.contains(&name.as_str()) => Some(name.clone()),
        [PathSegment::Member(field)] if load_aliases.contains(&name.as_str()) => {
            Some(format!("{name}.{field}"))
        }
        [] if Some(name.as_str()) != source_alias => Some(name.clone()),
        [PathSegment::Member(field)] if Some(name.as_str()) == source_alias => Some(field.clone()),
        _ => None,
    }
}

/// Lowers the page bounds; parameter bounds resolve through their bindings.
fn lower_paging(query: &TqlQuery, parameters: &TqlParameters) -> Result<QueryPaging, TqlError> {
    let bound = |page: &Option<PageBound>| -> Result<Option<u64>, TqlError> {
        match page {
            None => Ok(None),
            Some(bound) => match &bound.kind {
                PageBoundKind::Literal(value) => Ok(Some(*value)),
                PageBoundKind::Parameter(name) => match parameters.get(name) {
                    Some(DocumentValue::Integer(value)) if *value >= 0 => {
                        Ok(u64::try_from(*value).ok())
                    }
                    _ => Err(TqlError::at(TqlErrorClass::InvalidBound, name, bound.span)),
                },
            },
        }
    };
    let offset = bound(&query.paging.offset)?.unwrap_or(0);
    let limit = match bound(&query.paging.limit)? {
        None => None,
        Some(value) => Some(
            u32::try_from(value)
                .ok()
                .ok_or_else(|| TqlError::plain(TqlErrorClass::InvalidBound, query.paging.span))?,
        ),
    };
    QueryPaging::try_new(offset, limit)
        .map_err(|_| TqlError::plain(TqlErrorClass::UnsupportedForPlanning, query.span))
}
