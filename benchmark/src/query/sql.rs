//! SQL text validation for the read-only query endpoint (D-78/D-54).
//!
//! SQL is the only query language. This module parses `SQLite` syntax into an
//! AST, applies the read-only allow-list, and lowers the supported subset into
//! the typed planner before authorization and execution.
//!
//! - [`SqlQueryText`] validation through [`validate_sql_text`]: non-empty,
//!   bounded by [`MAX_SQL_TEXT_BYTES`](crate::operations::MAX_SQL_TEXT_BYTES),
//!   and parsed as one read-only `SELECT`, `WITH ... SELECT`, or
//!   `EXPLAIN [QUERY PLAN] SELECT` statement.
//! - [`SqlParameters`]: named, typed, out-of-band bindings. Values are data,
//!   never parsed text, so a binding can never inject a clause.
//! - [`SqlError`] / [`SqlErrorClass`]: the typed refusals the endpoint and
//!   the planner share.

use std::{collections::BTreeSet, ops::ControlFlow};

use sqlparser::{
    ast::{
        BinaryOperator, DescribeAlias, Expr, GroupByExpr, LimitClause, ObjectName, OrderByKind,
        Query, Select, SelectItem, SetExpr, Statement, TableFactor, UnaryOperator, Value, Visit,
        Visitor,
    },
    dialect::SQLiteDialect,
    parser::Parser,
};

use crate::types::DocumentValue;

/// The SQL dialect marker this front end accepts.
///
/// There is exactly one query language, so there is no version negotiation:
/// this constant exists so error messages and the endpoint can name what was
/// validated.
pub const SQL_DIALECT: &str = "sql/sqlite-readonly";

/// The admitted top-level SQL shape.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum SqlStatementKind {
    /// A `SELECT` without a leading common-table expression.
    Select,
    /// A `WITH ... SELECT` query.
    With,
    /// An `EXPLAIN SELECT` or `EXPLAIN QUERY PLAN SELECT` query.
    Explain,
}

/// The closed SQL subset lowered into the typed document query model.
///
/// This is deliberately private to the query crate: SQL expressions never
/// reach storage and are converted to the same predicate/order/page clauses
/// used by the typed executor.
#[derive(Clone, Debug)]
pub(crate) struct SqlLowered {
    pub(crate) collection: String,
    pub(crate) conditions: Vec<crate::query::QueryCondition>,
    pub(crate) ordering: Vec<crate::query::OrderingKey>,
    pub(crate) projection: Option<Vec<SqlProjection>>,
    pub(crate) offset: u64,
    pub(crate) limit: Option<u32>,
}

/// One SQL projection expression lowered to a document field.  The source
/// field and output label are kept separately: aliases are presentation
/// metadata and must not change the planner's field identity.
#[derive(Clone, Debug, Eq, PartialEq)]
pub(crate) struct SqlProjection {
    pub(crate) field: String,
    pub(crate) output: String,
}

/// Execute-only SQL retained for shapes the typed subscribable model cannot
/// represent (joins, reducers, windows, subqueries, CTEs, and syntax outside
/// the launch lowering).
#[derive(Clone, Debug, PartialEq)]
pub(crate) struct SqlRuntime {
    pub(crate) text: String,
    pub(crate) parameters: SqlParameters,
    pub(crate) tables: Vec<String>,
    /// User identifiers referenced by a direct single-base execute-only
    /// statement. This is transient execution metadata, not a schema.
    pub(crate) referenced_fields: Vec<String>,
    pub(crate) deadline: Option<crate::types::Deadline>,
    pub(crate) cancellation: Option<crate::types::Cancellation>,
    pub(crate) operator: Option<&'static str>,
}

/// Returns the user tables referenced by a validated statement. Empty
/// collections still need a `SQLite` schema so `EXPLAIN` and zero-row selects
/// compile with the same relation semantics as non-empty collections.
pub(crate) fn table_names(parsed: &ParsedSql) -> Vec<String> {
    struct Tables(Vec<String>);
    impl Visitor for Tables {
        type Break = std::convert::Infallible;

        fn pre_visit_relation(&mut self, relation: &ObjectName) -> ControlFlow<Self::Break> {
            if let Some(name) = unqualified_name(relation)
                && !self.0.iter().any(|known| known == name)
            {
                self.0.push(name.to_owned());
            }
            ControlFlow::Continue(())
        }
    }
    let mut tables = Tables(Vec::new());
    let _ = parsed.statement().visit(&mut tables);
    tables.0
}

#[cfg(test)]
#[path = "sql_alias_tests.rs"]
mod sql_alias_tests;

/// Collects user field identifiers for the bounded direct single-base
/// execute-only adapter. Complex relation scopes deliberately return an empty
/// list and continue through the existing SQLite path.
pub(crate) fn direct_single_base_referenced_fields(parsed: &ParsedSql) -> Vec<String> {
    let Some(query) = (match parsed.statement() {
        Statement::Query(query) => Some(query),
        Statement::Explain { statement, .. } => match statement.as_ref() {
            Statement::Query(query) => Some(query),
            _ => None,
        },
        _ => None,
    }) else {
        return Vec::new();
    };
    if query.with.is_some() || !matches!(query.body.as_ref(), SetExpr::Select(_)) {
        return Vec::new();
    }
    let SetExpr::Select(select) = query.body.as_ref() else { return Vec::new() };
    let [table] = select.from.as_slice() else { return Vec::new() };
    if !table.joins.is_empty() || !matches!(&table.relation, TableFactor::Table { .. }) {
        return Vec::new();
    }

    struct NestedQueryCount(usize);
    impl Visitor for NestedQueryCount {
        type Break = std::convert::Infallible;

        fn pre_visit_query(&mut self, _query: &Query) -> ControlFlow<Self::Break> {
            self.0 = self.0.saturating_add(1);
            ControlFlow::Continue(())
        }
    }
    let mut nested = NestedQueryCount(0);
    let _ = parsed.statement().visit(&mut nested);
    if nested.0 != 1 {
        return Vec::new();
    }

    struct Fields<'a>(&'a mut BTreeSet<String>);
    impl Visitor for Fields<'_> {
        type Break = std::convert::Infallible;

        fn pre_visit_expr(&mut self, expression: &Expr) -> ControlFlow<Self::Break> {
            let name = match expression {
                Expr::Identifier(identifier) => Some(identifier.value.clone()),
                Expr::CompoundIdentifier(identifiers) => {
                    identifiers.last().map(|identifier| identifier.value.clone())
                }
                _ => None,
            };
            if let Some(name) = name
                && valid_identifier(&name)
                && !matches!(
                    name.as_str(),
                    "_id" | "body" | "_rev" | "_hlc" | "_created" | "_updated"
                )
            {
                self.0.insert(name);
            }
            ControlFlow::Continue(())
        }
    }
    let mut fields = BTreeSet::new();
    let mut collect = |expression: &Expr| {
        let _ = expression.visit(&mut Fields(&mut fields));
    };
    for item in &select.projection {
        match item {
            SelectItem::UnnamedExpr(expression) => collect(expression),
            SelectItem::ExprWithAlias { expr: expression, .. } => collect(expression),
            SelectItem::ExprWithAliases { expr: expression, .. } => collect(expression),
            SelectItem::Wildcard(_) | SelectItem::QualifiedWildcard(_, _) => {}
        }
    }
    if let Some(selection) = &select.selection {
        collect(selection);
    }
    if let GroupByExpr::Expressions(expressions, _) = &select.group_by {
        for expression in expressions {
            collect(expression);
        }
    }
    if let Some(having) = &select.having {
        collect(having);
    }
    let mut aliases = BTreeSet::new();
    for item in &select.projection {
        match item {
            SelectItem::ExprWithAlias { alias, .. } => {
                aliases.insert(alias.value.as_str());
            }
            SelectItem::ExprWithAliases { aliases: names, .. } => {
                aliases.extend(names.iter().map(|alias| alias.value.as_str()));
            }
            _ => {}
        }
    }
    if let Some(order_by) = &query.order_by {
        let OrderByKind::Expressions(expressions) = &order_by.kind else {
            return fields.into_iter().collect();
        };
        for item in expressions {
            if let Expr::Identifier(identifier) = &item.expr
                && aliases.contains(identifier.value.as_str())
            {
                continue;
            }
            collect(&item.expr);
        }
    }
    fields.into_iter().collect()
}

/// The fail-closed refusal classes of the SQL front end.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum SqlErrorClass {
    /// The text does not parse as read-only SQL for the accepted dialect.
    ParseError,
    /// The statement is outside the read-only allow-list (`SELECT`, `WITH`,
    /// `EXPLAIN`).
    UnsupportedStatement,
    /// The text carries a forbidden keyword (DDL, DML, pragma, attachment,
    /// or any non-deterministic escape hatch).
    ForbiddenKeyword,
    /// A volatile, connection-specific, or host-sensitive function appeared
    /// anywhere in the query tree.
    ForbiddenFunction,
    /// A qualified name or SQLite-private relation appeared anywhere in the
    /// query tree.
    ForbiddenRelation,
    /// A table-valued function could reach a PRAGMA, extension, virtual
    /// table, or host-specific source.
    ForbiddenTableFunction,
    /// A `$name` reference has no binding.
    UnboundParameter,
    /// A binding is never referenced.
    UnexpectedParameter,
    /// A paging bound violation.
    InvalidBound,
    /// An identifier fails the shared identifier discipline.
    InvalidIdentifier,
}

/// One typed SQL front-end refusal.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct SqlError {
    class: SqlErrorClass,
    position: Option<usize>,
    construct: Option<String>,
}

/// One parsed SQL statement that passed the D-54 allow-list.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ParsedSql {
    text: String,
    kind: SqlStatementKind,
    statement: Statement,
}

impl ParsedSql {
    /// Returns the caller's SQL text unchanged.
    #[must_use]
    pub fn as_str(&self) -> &str {
        &self.text
    }

    /// Returns the admitted top-level statement shape.
    #[must_use]
    pub const fn kind(&self) -> SqlStatementKind {
        self.kind
    }

    /// Returns the parsed tree to the crate-private planner seam.
    #[allow(dead_code, reason = "the E-4 planner is the next consumer of the parsed tree")]
    pub(crate) const fn statement(&self) -> &Statement {
        &self.statement
    }
}

impl SqlError {
    /// Returns the refusal class.
    #[must_use]
    pub const fn class(&self) -> SqlErrorClass {
        self.class
    }

    /// Returns the byte offset the refusal points at, when one applies.
    #[must_use]
    pub const fn position(&self) -> Option<usize> {
        self.position
    }

    /// Returns the offending construct, when the class names one.
    #[must_use]
    pub fn construct(&self) -> Option<&str> {
        self.construct.as_deref()
    }

    /// Builds an unpositioned refusal over one construct name.
    pub(super) fn binding(class: SqlErrorClass, name: &str) -> Self {
        Self { class, position: None, construct: Some(name.to_owned()) }
    }

    /// Builds a refusal at one byte offset.
    pub(super) fn at(class: SqlErrorClass, position: usize, construct: &str) -> Self {
        Self { class, position: Some(position), construct: Some(construct.to_owned()) }
    }

    /// Builds a refusal without a position or construct.
    pub(super) fn plain(class: SqlErrorClass) -> Self {
        Self { class, position: None, construct: None }
    }
}

/// The named, typed, out-of-band parameter bindings of one SQL request.
///
/// Parameter values are data, never parsed text: a binding can never inject
/// a clause or change the statement shape.
#[derive(Clone, Debug, Default, PartialEq)]
pub struct SqlParameters {
    values: std::collections::BTreeMap<String, DocumentValue>,
}

impl SqlParameters {
    /// Creates an empty binding set.
    #[must_use]
    pub const fn new() -> Self {
        Self { values: std::collections::BTreeMap::new() }
    }

    /// Creates a binding set from name-value pairs.
    ///
    /// # Errors
    ///
    /// Returns [`SqlErrorClass::InvalidIdentifier`] for one invalid name.
    pub fn try_new(entries: &[(&str, DocumentValue)]) -> Result<Self, SqlError> {
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
    /// Returns [`SqlErrorClass::InvalidIdentifier`] for an invalid name.
    pub fn insert(&mut self, name: &str, value: DocumentValue) -> Result<(), SqlError> {
        if !valid_identifier(name) {
            return Err(SqlError::binding(SqlErrorClass::InvalidIdentifier, name));
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

/// Reports whether one name passes the shared identifier discipline.
pub(super) fn valid_identifier(name: &str) -> bool {
    use crate::query::spec::MAX_FIELD_BYTES;
    !name.is_empty()
        && name.len() <= MAX_FIELD_BYTES
        && !name.chars().any(char::is_control)
        && name == name.trim()
}

/// Parses one SQL text and applies the read-only allow-list to its full AST.
///
/// The parser accepts `SQLite` placeholders and JSON operators. It does not
/// bind values, resolve collections, authorize rows, or execute the query.
/// Those stages consume the returned tree after this boundary has rejected
/// unsafe statement, relation, and function shapes.
///
/// # Errors
///
/// Returns one typed [`SqlError`] for empty or oversized input, invalid
/// `SQLite` syntax, multiple statements, a top-level statement outside the
/// D-54 allow-list, a volatile function, an internal or qualified relation,
/// or a table-valued function.
pub fn parse(text: &str, max_bytes: usize) -> Result<ParsedSql, SqlError> {
    if text.trim().is_empty() {
        return Err(SqlError::plain(SqlErrorClass::ParseError));
    }
    if text.len() > max_bytes {
        return Err(SqlError::plain(SqlErrorClass::UnsupportedStatement));
    }

    let mut statements = Parser::parse_sql(&SQLiteDialect {}, text)
        .map_err(|_| SqlError::plain(SqlErrorClass::ParseError))?;
    if statements.len() != 1 {
        return Err(SqlError::plain(SqlErrorClass::UnsupportedStatement));
    }
    let Some(statement) = statements.pop() else {
        return Err(SqlError::plain(SqlErrorClass::ParseError));
    };
    let kind = classify_statement(&statement)?;

    if let ControlFlow::Break(error) = statement.visit(&mut AllowListVisitor) {
        return Err(error);
    }

    Ok(ParsedSql { text: text.to_owned(), kind, statement })
}

/// Validates one SQL text through the same AST boundary used by the planner.
///
/// # Errors
///
/// Returns one typed [`SqlError`]; see [`SqlErrorClass`] for the classes.
pub fn validate_text(text: &str, max_bytes: usize) -> Result<(), SqlError> {
    parse(text, max_bytes).map(drop)
}

/// The launch live subset of read-only SQL (D-78, ADR 0044 §3.2).
///
/// A [`LiveSubset::Live`] statement — one collection, scalar `WHERE`
/// predicates over columns or JSON paths, field projection, FTS5 `MATCH`,
/// `ORDER BY`, `LIMIT` — is subscribable: the sync engine maintains it
/// incrementally (D-38/D-46). A [`LiveSubset::ExecuteOnly`] statement carries
/// an operator whose incremental form needs integrated state (joins,
/// aggregates) or composes subqueries (subqueries, window functions at
/// launch): it runs through `execute()` and refuses `subscribe()` with
/// [`NotSubscribable`] naming the operator.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum LiveSubset {
    /// The statement is inside the live subset and may subscribe.
    Live,
    /// The statement is execute-only; the operator names the refusal.
    ExecuteOnly {
        /// The non-live operator: `join`, `aggregate`, `window`, `subquery`,
        /// or `unsupported` when SQL has no live lowering.
        operator: &'static str,
    },
}

impl LiveSubset {
    /// Reports whether the statement may subscribe.
    #[must_use]
    pub const fn is_live(self) -> bool {
        matches!(self, Self::Live)
    }

    /// Returns the non-live operator, or `None` for the live subset.
    #[must_use]
    pub const fn operator(self) -> Option<&'static str> {
        match self {
            Self::Live => None,
            Self::ExecuteOnly { operator } => Some(operator),
        }
    }
}

/// The typed refusal of a subscribe for an execute-only statement.
///
/// The refusal names the non-live operator; [`NotSubscribable::fix`] points
/// at `execute()`. Fail-closed: anything the classifier does not recognize
/// as live refuses rather than subscribing a view the sync engine cannot
/// maintain incrementally.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct NotSubscribable {
    operator: &'static str,
}

impl NotSubscribable {
    pub(crate) const fn new(operator: &'static str) -> Self {
        Self { operator }
    }

    /// Returns the non-live operator: `join`, `aggregate`, `window`,
    /// `subquery`, or `unsupported`.
    #[must_use]
    pub const fn operator(self) -> &'static str {
        self.operator
    }

    /// Returns the one-line remedy (D-56): execute instead of subscribing.
    #[must_use]
    pub const fn fix(self) -> &'static str {
        "run the statement with execute() instead of subscribe(): subscribe serves only the live \
         subset (filter, projection, MATCH, ORDER BY, LIMIT)"
    }
}

/// Classifies one parsed statement into the launch live subset (D-78).
///
/// The tag is the whole `NotSubscribable` decision (ADR 0044 §5): `Live`
/// statements may subscribe, `ExecuteOnly` statements refuse subscribe with
/// the named operator. `EXPLAIN` classifies transparently through its inner
/// query. Priority follows the ADR table order — `join`, `aggregate`,
/// `window`, `subquery` — so a statement carrying several non-live operators
/// names the first.
///
/// A `Live` tag is also a lowering promise: the classifier runs the same
/// lowering shape checks used by the planner with synthetic values for any
/// placeholders. This keeps unsupported predicates and clauses execute-only
/// instead of attaching a view the sync engine cannot update correctly.
#[must_use]
pub fn classify_live_subset(parsed: &ParsedSql) -> LiveSubset {
    let statement = match parsed.statement() {
        Statement::Query(_) => parsed.statement(),
        Statement::Explain { statement, .. } => statement.as_ref(),
        // Unreachable: `parse` admits only `Query` and `Explain` statements.
        _ => return LiveSubset::Live,
    };
    let Statement::Query(query) = statement else {
        return LiveSubset::Live;
    };
    // Set operations compose whole subqueries; parenthesization is
    // transparent.
    let mut body = query.body.as_ref();
    while let SetExpr::Query(inner) = body {
        body = inner.body.as_ref();
    }
    if !matches!(body, SetExpr::Select(_)) {
        return LiveSubset::ExecuteOnly { operator: "subquery" };
    }
    let mut scan = LiveSubsetScan::default();
    let _ = statement.visit(&mut scan);
    if scan.has("join") {
        LiveSubset::ExecuteOnly { operator: "join" }
    } else if scan.has("aggregate") {
        LiveSubset::ExecuteOnly { operator: "aggregate" }
    } else if scan.has("window") {
        LiveSubset::ExecuteOnly { operator: "window" }
    } else if scan.has("subquery") {
        LiveSubset::ExecuteOnly { operator: "subquery" }
    } else if !live_lowering_shape(parsed) {
        LiveSubset::ExecuteOnly { operator: "unsupported" }
    } else {
        LiveSubset::Live
    }
}

/// Classifies one SQL text into the launch live subset without binding values.
///
/// # Errors
///
/// Returns one typed [`SqlError`] for text the read-only front end refuses;
/// see [`parse`].
pub fn classify_sql_text(text: &str, max_bytes: usize) -> Result<LiveSubset, SqlError> {
    parse(text, max_bytes).map(|parsed| classify_live_subset(&parsed))
}

/// Refuses an execute-only statement for the subscribe path.
///
/// # Errors
///
/// Returns [`NotSubscribable`] naming the non-live operator when the
/// statement is outside the live subset.
pub fn check_subscribable(parsed: &ParsedSql) -> Result<(), NotSubscribable> {
    match classify_live_subset(parsed) {
        LiveSubset::Live => Ok(()),
        LiveSubset::ExecuteOnly { operator } => Err(NotSubscribable { operator }),
    }
}

/// The `SQLite` aggregate functions that force the `aggregate` tag.
///
/// A windowed use (`OVER`) tags `window` instead: the `OVER` check runs
/// first. Grouping (`GROUP BY`, `HAVING`) and filtered aggregates
/// (`FILTER (WHERE ...)`) tag `aggregate` through the select and filter
/// checks below, not through this list.
const AGGREGATE_FUNCTIONS: &[&str] =
    &["avg", "count", "group_concat", "max", "min", "sum", "total"];

/// The operator hits of one live-subset scan over a parsed statement.
///
/// One entry per operator present; priority resolution reads this set after
/// the visit, so the walk stays a single pass whatever the shape carries.
#[derive(Default)]
struct LiveSubsetScan {
    hits: Vec<&'static str>,
}

impl LiveSubsetScan {
    /// Records one operator hit.
    fn hit(&mut self, operator: &'static str) {
        if !self.hits.contains(&operator) {
            self.hits.push(operator);
        }
    }

    /// Reports whether one operator was hit.
    fn has(&self, operator: &'static str) -> bool {
        self.hits.contains(&operator)
    }
}

impl Visitor for LiveSubsetScan {
    type Break = std::convert::Infallible;

    fn pre_visit_query(&mut self, query: &Query) -> ControlFlow<Self::Break> {
        // Common-table expressions are named subqueries.
        if query.with.is_some() {
            self.hit("subquery");
        }
        ControlFlow::Continue(())
    }

    fn pre_visit_select(&mut self, select: &Select) -> ControlFlow<Self::Break> {
        // Comma-separated relations are implicit CROSS JOINs and need the
        // same integrated state as explicit JOIN syntax.
        if select.from.len() != 1 || select.from.iter().any(|table| !table.joins.is_empty()) {
            self.hit("join");
        }
        if has_group_by(select) || select.having.is_some() {
            self.hit("aggregate");
        }
        ControlFlow::Continue(())
    }

    fn pre_visit_table_factor(&mut self, factor: &TableFactor) -> ControlFlow<Self::Break> {
        if matches!(factor, TableFactor::Derived { .. }) {
            self.hit("subquery");
        }
        ControlFlow::Continue(())
    }

    fn pre_visit_expr(&mut self, expression: &Expr) -> ControlFlow<Self::Break> {
        match expression {
            Expr::Subquery(_) | Expr::Exists { .. } | Expr::InSubquery { .. } => {
                self.hit("subquery");
            }
            Expr::Function(function) => {
                if function.over.is_some() {
                    self.hit("window");
                } else if let Some(name) = unqualified_name(&function.name)
                    && AGGREGATE_FUNCTIONS.contains(&name.to_ascii_lowercase().as_str())
                {
                    self.hit("aggregate");
                }
                if function.filter.is_some() {
                    self.hit("aggregate");
                }
            }
            _ => {}
        }
        ControlFlow::Continue(())
    }
}

/// Checks that a statement has an exact launch live lowering. Bindings are
/// represented by harmless synthetic values, so their presence or type does
/// not turn a supported shape into execute-only. Literal bound violations and
/// all unsupported syntax still fail closed.
fn live_lowering_shape(parsed: &ParsedSql) -> bool {
    let mut collector = ParameterCollector::default();
    let _ = parsed.statement().visit(&mut collector);
    let mut parameters = SqlParameters::new();
    for name in collector.names {
        // String values satisfy MATCH/LIKE; scalar predicates do not impose
        // a type at this layer. The real caller's values are checked later.
        if parameters.insert(&name, DocumentValue::String("x%".to_owned())).is_err() {
            return false;
        }
    }
    match lower(parsed, &parameters) {
        Ok(_) => true,
        // A placeholder used for LIMIT/OFFSET is represented by a string
        // above because its eventual bound type is caller data.
        Err(error)
            if error.class() == SqlErrorClass::InvalidBound
                && statement_has_paging_placeholder(parsed) =>
        {
            true
        }
        Err(_) => false,
    }
}

fn statement_has_paging_placeholder(parsed: &ParsedSql) -> bool {
    struct PagingPlaceholder(bool);
    impl Visitor for PagingPlaceholder {
        type Break = std::convert::Infallible;

        fn pre_visit_query(&mut self, query: &Query) -> ControlFlow<Self::Break> {
            let Some(clause) = &query.limit_clause else { return ControlFlow::Continue(()) };
            let has_placeholder = match clause {
                LimitClause::LimitOffset { limit, offset, .. } => {
                    limit.as_ref().is_some_and(has_expr_placeholder)
                        || offset.as_ref().is_some_and(|offset| has_expr_placeholder(&offset.value))
                }
                LimitClause::OffsetCommaLimit { offset, limit } => {
                    has_expr_placeholder(offset) || has_expr_placeholder(limit)
                }
            };
            self.0 |= has_placeholder;
            ControlFlow::Continue(())
        }
    }

    let mut visitor = PagingPlaceholder(false);
    let _ = parsed.statement().visit(&mut visitor);
    visitor.0
}

fn has_expr_placeholder(expression: &Expr) -> bool {
    struct Placeholder(bool);
    impl Visitor for Placeholder {
        type Break = std::convert::Infallible;

        fn pre_visit_expr(&mut self, expression: &Expr) -> ControlFlow<Self::Break> {
            if matches!(expression, Expr::Value(value) if matches!(value.value, Value::Placeholder(_)))
            {
                self.0 = true;
            }
            ControlFlow::Continue(())
        }
    }
    let mut visitor = Placeholder(false);
    let _ = expression.visit(&mut visitor);
    visitor.0
}

/// Reports whether one select groups its rows.
fn has_group_by(select: &Select) -> bool {
    !matches!(
        &select.group_by,
        GroupByExpr::Expressions(values, modifiers) if values.is_empty() && modifiers.is_empty()
    )
}

fn classify_statement(statement: &Statement) -> Result<SqlStatementKind, SqlError> {
    match statement {
        Statement::Query(query) if is_select_body(&query.body) => {
            Ok(if query.with.is_some() { SqlStatementKind::With } else { SqlStatementKind::Select })
        }
        Statement::Explain {
            describe_alias: DescribeAlias::Explain,
            analyze: false,
            verbose: false,
            estimate: false,
            statement,
            format: None,
            options: None,
            ..
        } if matches!(statement.as_ref(), Statement::Query(query) if is_select_body(&query.body)) => {
            Ok(SqlStatementKind::Explain)
        }
        statement => {
            Err(SqlError::at(SqlErrorClass::UnsupportedStatement, 0, statement_name(statement)))
        }
    }
}

fn is_select_body(body: &SetExpr) -> bool {
    match body {
        SetExpr::Select(_) => true,
        SetExpr::Query(query) => is_select_body(&query.body),
        SetExpr::SetOperation { left, right, .. } => is_select_body(left) && is_select_body(right),
        SetExpr::Values(_)
        | SetExpr::Insert(_)
        | SetExpr::Update(_)
        | SetExpr::Delete(_)
        | SetExpr::Merge(_)
        | SetExpr::Table(_) => false,
    }
}

fn statement_name(statement: &Statement) -> &'static str {
    match statement {
        Statement::Insert(_) => "insert",
        Statement::Update(_) => "update",
        Statement::Delete(_) => "delete",
        Statement::CreateTable(_) => "create table",
        Statement::Drop { .. } => "drop",
        Statement::AttachDatabase { .. } | Statement::AttachDuckDBDatabase { .. } => "attach",
        Statement::DetachDuckDBDatabase { .. } => "detach",
        Statement::Pragma { .. } => "pragma",
        Statement::Explain { .. } | Statement::ExplainTable { .. } => "explain",
        _ => "statement",
    }
}

struct AllowListVisitor;

impl Visitor for AllowListVisitor {
    type Break = SqlError;

    fn pre_visit_statement(&mut self, statement: &Statement) -> ControlFlow<Self::Break> {
        if matches!(statement, Statement::Query(_) | Statement::Explain { .. }) {
            ControlFlow::Continue(())
        } else {
            ControlFlow::Break(SqlError::at(
                SqlErrorClass::UnsupportedStatement,
                0,
                statement_name(statement),
            ))
        }
    }

    fn pre_visit_select(&mut self, select: &Select) -> ControlFlow<Self::Break> {
        if select.into.is_some() {
            ControlFlow::Break(SqlError::at(SqlErrorClass::ForbiddenKeyword, 0, "into"))
        } else {
            ControlFlow::Continue(())
        }
    }

    fn pre_visit_relation(&mut self, relation: &ObjectName) -> ControlFlow<Self::Break> {
        let Some(identifier) = unqualified_name(relation) else {
            return ControlFlow::Break(SqlError::at(
                SqlErrorClass::ForbiddenRelation,
                0,
                &relation.to_string(),
            ));
        };
        let normalized = identifier.to_ascii_lowercase();
        if normalized.starts_with("sqlite_") || normalized.starts_with("pragma_") {
            return ControlFlow::Break(SqlError::at(
                SqlErrorClass::ForbiddenRelation,
                0,
                identifier,
            ));
        }
        ControlFlow::Continue(())
    }

    fn pre_visit_table_factor(&mut self, factor: &TableFactor) -> ControlFlow<Self::Break> {
        let is_function = match factor {
            TableFactor::Table { args, .. } => args.is_some(),
            TableFactor::TableFunction { .. }
            | TableFactor::Function { .. }
            | TableFactor::UNNEST { .. }
            | TableFactor::JsonTable { .. }
            | TableFactor::OpenJsonTable { .. } => true,
            _ => false,
        };
        if is_function {
            ControlFlow::Break(SqlError::plain(SqlErrorClass::ForbiddenTableFunction))
        } else {
            ControlFlow::Continue(())
        }
    }

    fn pre_visit_expr(&mut self, expression: &Expr) -> ControlFlow<Self::Break> {
        let Expr::Function(function) = expression else {
            return ControlFlow::Continue(());
        };
        let Some(name) = unqualified_name(&function.name) else {
            return ControlFlow::Break(SqlError::at(
                SqlErrorClass::ForbiddenFunction,
                0,
                &function.name.to_string(),
            ));
        };
        let normalized = name.to_ascii_lowercase();
        if FORBIDDEN_FUNCTIONS.contains(&normalized.as_str()) {
            return ControlFlow::Break(SqlError::at(SqlErrorClass::ForbiddenFunction, 0, name));
        }
        // SQLite permits application-defined functions, and its grammar does
        // not carry a volatility marker.  Unknown names therefore cannot be
        // admitted safely: a function could read the host, mutate a
        // connection, or vary between Regions.  Keep this explicit allow-list
        // deliberately small and deterministic; aggregates/window functions
        // remain executable through the bounded SQL adapter, while the live
        // classifier marks them execute-only.
        if !ALLOWED_FUNCTIONS.contains(&normalized.as_str()) {
            return ControlFlow::Break(SqlError::at(SqlErrorClass::ForbiddenFunction, 0, name));
        }
        ControlFlow::Continue(())
    }
}

fn unqualified_name(name: &ObjectName) -> Option<&str> {
    let [part] = name.0.as_slice() else {
        return None;
    };
    part.as_ident().map(|identifier| identifier.value.as_str())
}

/// `SQLite` functions that are volatile, connection-specific, build-specific,
/// or can escape the collection model. `SQLite` does not expose volatility in
/// its grammar, so the public parser refuses these names before a connection
/// sees the statement.
const FORBIDDEN_FUNCTIONS: &[&str] = &[
    "changes",
    "current_date",
    "current_time",
    "current_timestamp",
    "date",
    "datetime",
    "edit",
    "fts3_tokenizer",
    "julianday",
    "last_insert_rowid",
    "load_extension",
    "random",
    "randomblob",
    "readfile",
    "sqlite_compileoption_get",
    "sqlite_compileoption_used",
    "sqlite_offset",
    "sqlite_source_id",
    "sqlite_version",
    "strftime",
    "time",
    "timediff",
    "total_changes",
    "unixepoch",
    "writefile",
];

/// Deterministic built-ins supported by the read-only SQL contract.  This is
/// an allow-list (rather than a deny-list) because SQLite extensions and
/// application functions are otherwise indistinguishable from safe built-ins
/// at parse time.
const ALLOWED_FUNCTIONS: &[&str] = &[
    "abs",
    "avg",
    "coalesce",
    "count",
    "dense_rank",
    "first_value",
    "group_concat",
    "hex",
    "ifnull",
    "iif",
    "instr",
    "json",
    "json_array",
    "json_array_length",
    "json_extract",
    "json_insert",
    "json_object",
    "json_patch",
    "json_remove",
    "json_replace",
    "json_set",
    "json_type",
    "json_valid",
    "lag",
    "last_value",
    "lead",
    "length",
    "lower",
    "ltrim",
    "max",
    "min",
    "nullif",
    "ntile",
    "printf",
    "quote",
    "rank",
    "replace",
    "round",
    "row_number",
    "rtrim",
    "sign",
    "soundex",
    "substr",
    "substring",
    "sum",
    "total",
    "trim",
    "typeof",
    "unicode",
    "upper",
];

/// Lowers the launch SQL subset (one collection, scalar predicates, ordering,
/// projection, and paging) into the typed query clauses. Anything not
/// representable by that executor is rejected rather than approximated.
pub(crate) fn lower(
    parsed: &ParsedSql,
    parameters: &SqlParameters,
) -> Result<SqlLowered, SqlError> {
    let statement = match parsed.statement() {
        Statement::Query(query) => query,
        Statement::Explain { statement, .. } => match statement.as_ref() {
            Statement::Query(query) => query,
            _ => return Err(SqlError::plain(SqlErrorClass::UnsupportedStatement)),
        },
        _ => return Err(SqlError::plain(SqlErrorClass::UnsupportedStatement)),
    };
    if statement.with.is_some() {
        return Err(SqlError::binding(SqlErrorClass::UnsupportedStatement, "WITH"));
    }
    let SetExpr::Select(select) = statement.body.as_ref() else {
        return Err(SqlError::binding(SqlErrorClass::UnsupportedStatement, "query body"));
    };
    if statement.fetch.is_some()
        || !statement.locks.is_empty()
        || statement.for_clause.is_some()
        || statement.settings.is_some()
        || statement.format_clause.is_some()
        || !statement.pipe_operators.is_empty()
    {
        return Err(SqlError::binding(SqlErrorClass::UnsupportedStatement, "query clauses"));
    }
    if select.distinct.is_some()
        || select.select_modifiers.is_some()
        || select.top.is_some()
        || select.into.is_some()
        || select.exclude.is_some()
        || !select.lateral_views.is_empty()
        || select.prewhere.is_some()
        || !select.connect_by.is_empty()
        || !matches!(select.group_by, sqlparser::ast::GroupByExpr::Expressions(ref values, ref modifiers) if values.is_empty() && modifiers.is_empty())
        || select.having.is_some()
        || !select.cluster_by.is_empty()
        || !select.distribute_by.is_empty()
        || !select.sort_by.is_empty()
        || !select.named_window.is_empty()
        || select.qualify.is_some()
        || select.value_table_mode.is_some()
        || !matches!(select.flavor, sqlparser::ast::SelectFlavor::Standard)
    {
        return Err(SqlError::binding(SqlErrorClass::UnsupportedStatement, "SELECT clauses"));
    }
    let collection = lower_collection(select)?;

    let mut used = std::collections::BTreeSet::new();
    let mut conditions = Vec::new();
    if let Some(selection) = &select.selection {
        lower_condition(selection, parameters, &mut used, &mut conditions)?;
    }

    let projection = lower_projection(&select.projection)?;
    let ordering = lower_ordering(statement.order_by.as_ref())?;
    let (limit, offset) = lower_paging(statement.limit_clause.as_ref(), parameters, &mut used)?;
    for name in parameters.names() {
        if !used.contains(name) {
            return Err(SqlError::binding(SqlErrorClass::UnexpectedParameter, name));
        }
    }
    Ok(SqlLowered { collection, conditions, ordering, projection, offset, limit })
}

/// Resolves the single collection a launch query reads. Anything but one
/// plain table reference is rejected rather than approximated.
fn lower_collection(select: &Select) -> Result<String, SqlError> {
    let [table] = select.from.as_slice() else {
        return Err(SqlError::binding(SqlErrorClass::UnsupportedStatement, "one collection"));
    };
    if !table.joins.is_empty() {
        return Err(SqlError::binding(SqlErrorClass::UnsupportedStatement, "JOIN"));
    }
    let collection = match &table.relation {
        TableFactor::Table {
            name,
            alias,
            args,
            with_hints,
            version,
            with_ordinality,
            partitions,
            json_path,
            sample,
            index_hints,
        } => {
            if alias.is_some()
                || args.is_some()
                || !with_hints.is_empty()
                || version.is_some()
                || *with_ordinality
                || !partitions.is_empty()
                || json_path.is_some()
                || sample.is_some()
                || !index_hints.is_empty()
            {
                return Err(SqlError::binding(
                    SqlErrorClass::UnsupportedStatement,
                    "table modifiers",
                ));
            }
            unqualified_name(name)
                .ok_or_else(|| {
                    SqlError::binding(SqlErrorClass::ForbiddenRelation, &name.to_string())
                })?
                .to_owned()
        }
        _ => {
            return Err(SqlError::binding(
                SqlErrorClass::UnsupportedStatement,
                "collection source",
            ));
        }
    };
    if !valid_identifier(&collection) {
        return Err(SqlError::binding(SqlErrorClass::InvalidIdentifier, &collection));
    }
    Ok(collection)
}

/// Validates every SQL query node's LIMIT/OFFSET before execution. `SQLite`'s
/// `LIMIT -1` means unlimited, which is not acceptable at this bounded seam.
pub(crate) fn validate_paging_bounds(
    parsed: &ParsedSql,
    parameters: &SqlParameters,
) -> Result<(), SqlError> {
    let mut validator = PagingValidator { parameters, error: None };
    let _ = parsed.statement().visit(&mut validator);
    validator.error.map_or(Ok(()), Err)
}

struct PagingValidator<'parameters> {
    parameters: &'parameters SqlParameters,
    error: Option<SqlError>,
}

impl Visitor for PagingValidator<'_> {
    type Break = ();

    fn pre_visit_query(&mut self, query: &Query) -> ControlFlow<Self::Break> {
        if let Some(clause) = &query.limit_clause {
            let result = match clause {
                LimitClause::LimitOffset { limit, offset, limit_by } => {
                    if limit_by.is_empty() {
                        validate_limit_value(limit.as_ref(), self.parameters).and_then(|()| {
                            validate_offset_value(
                                offset.as_ref().map(|offset| &offset.value),
                                self.parameters,
                            )
                        })
                    } else {
                        Err(unsupported("LIMIT BY"))
                    }
                }
                LimitClause::OffsetCommaLimit { offset, limit } => {
                    validate_limit_value(Some(limit), self.parameters)
                        .and_then(|()| validate_offset_value(Some(offset), self.parameters))
                }
            };
            if let Err(error) = result {
                self.error = Some(error);
                return ControlFlow::Break(());
            }
        }
        ControlFlow::Continue(())
    }
}

fn validate_limit_value(
    expression: Option<&Expr>,
    parameters: &SqlParameters,
) -> Result<(), SqlError> {
    let Some(expression) = expression else { return Ok(()) };
    let value = bound_literal(expression, parameters)?;
    let value = parse_u64(&value)?;
    if value == 0 || value > u64::from(crate::query::MAX_PAGE_LIMIT) {
        return Err(SqlError::plain(SqlErrorClass::InvalidBound));
    }
    Ok(())
}

fn validate_offset_value(
    expression: Option<&Expr>,
    parameters: &SqlParameters,
) -> Result<(), SqlError> {
    let Some(expression) = expression else { return Ok(()) };
    let offset = parse_u64(&bound_literal(expression, parameters)?)?;
    // Every SQL response is either a bounded typed page or an execute-only
    // stream consumed through the same retained-window contract.  Refuse a
    // cursor that cannot fit that window before SQLite starts scanning.
    if offset > super::execute::MAX_RETAINED_CANDIDATES as u64 {
        return Err(SqlError::plain(SqlErrorClass::InvalidBound));
    }
    Ok(())
}

fn bound_literal(expression: &Expr, parameters: &SqlParameters) -> Result<DocumentValue, SqlError> {
    if let Expr::UnaryOp { op: UnaryOperator::Minus, .. } = expression {
        Err(SqlError::plain(SqlErrorClass::InvalidBound))
    } else {
        let mut used = std::collections::BTreeSet::new();
        literal(expression, parameters, &mut used)
    }
}

/// Validates named bindings and the executable SQL subset together. This is
/// the wire-facing check used by adapters before invoking the generic plan
/// seam, so missing and unused bindings retain their typed SQL error class.
///
/// Shapes inside the live subset lower exactly here, bounds included, so a
/// live statement with an out-of-range page or an unbound parameter keeps
/// its typed class. A live shape the typed planner cannot express (a
/// disjunction, an arithmetic projection) is not a binding problem: it
/// falls through to the binding check alone and its planning rides the SQL
/// runtime path instead. Execute-only shapes validate their bindings alone
/// for the same reason.
///
/// # Errors
///
/// Returns a typed SQL refusal when the statement is malformed, has an
/// unbound or unused named parameter, or — for a live shape the typed
/// planner expresses — is unsupported or out of bounds.
pub fn validate_parameters(text: &str, parameters: &SqlParameters) -> Result<(), SqlError> {
    let parsed = parse(text, crate::operations::MAX_SQL_TEXT_BYTES)?;
    // MATCH is also legal in execute-only SQL (for example a join).  Those
    // statements do not go through `lower`, so validate the bound search
    // operand at the SQL boundary as well.  Otherwise an oversized or
    // control-bearing value could reach SQLite's FTS parser directly.
    validate_match_values(&parsed, parameters)?;
    validate_paging_bounds(&parsed, parameters)?;
    let subset = classify_live_subset(&parsed);
    // MATCH is a typed query operator.  Execute-only shapes would otherwise
    // fall through to the SQLite adapter, where the plain collection views
    // have no FTS virtual table and the request could bypass the typed FTS
    // boundary.  Keep execute-only SQL available for ordinary projections,
    // joins, and reducers, but refuse any statement that carries MATCH.
    if matches!(subset, LiveSubset::ExecuteOnly { .. }) && contains_match(&parsed) {
        return Err(SqlError::binding(SqlErrorClass::UnsupportedStatement, "MATCH"));
    }
    if matches!(subset, LiveSubset::Live) {
        match lower(&parsed, parameters) {
            Ok(_) => return Ok(()),
            Err(error) if error.class() != SqlErrorClass::UnsupportedStatement => {
                return Err(error);
            }
            // A live shape the typed planner cannot express is not a
            // binding problem: fall through to the binding check alone.
            Err(_) => {}
        }
    }
    let mut collector = ParameterCollector::default();
    let _ = parsed.statement().visit(&mut collector);
    for name in &collector.names {
        let normalized = name.trim_start_matches(['$', ':', '@']);
        if normalized == "?" || !parameters.contains(normalized) {
            return Err(SqlError::binding(SqlErrorClass::UnboundParameter, normalized));
        }
    }
    for name in parameters.names() {
        if !collector.names.iter().any(|used| used.trim_start_matches(['$', ':', '@']) == name) {
            return Err(SqlError::binding(SqlErrorClass::UnexpectedParameter, name));
        }
    }
    Ok(())
}

/// Reports whether a parsed statement contains a `MATCH` predicate anywhere
/// in its expression tree.
fn contains_match(parsed: &ParsedSql) -> bool {
    struct MatchFinder(bool);

    impl Visitor for MatchFinder {
        type Break = ();

        fn pre_visit_expr(&mut self, expression: &Expr) -> ControlFlow<Self::Break> {
            if matches!(expression, Expr::BinaryOp { op: BinaryOperator::Match, .. }) {
                self.0 = true;
                return ControlFlow::Break(());
            }
            ControlFlow::Continue(())
        }
    }

    let mut finder = MatchFinder(false);
    let _ = parsed.statement().visit(&mut finder);
    finder.0
}

/// Validates every SQL `MATCH` operand before either the typed or SQLite
/// execution path sees it.  Values remain out-of-band parameters; this only
/// checks their type and the shared bounded search-query contract.
fn validate_match_values(parsed: &ParsedSql, parameters: &SqlParameters) -> Result<(), SqlError> {
    struct MatchValidator<'parameters> {
        parameters: &'parameters SqlParameters,
        error: Option<SqlError>,
        next_ordinal: u64,
    }

    impl Visitor for MatchValidator<'_> {
        type Break = ();

        fn pre_visit_expr(&mut self, expression: &Expr) -> ControlFlow<Self::Break> {
            let is_match = matches!(expression, Expr::BinaryOp { op: BinaryOperator::Match, .. });
            if !is_match {
                // SQLite assigns bare `?` markers the next available slot
                // after explicit numbered markers. Keep this cursor in sync
                // while the AST walk visits every non-MATCH placeholder, so
                // a MATCH operand after `?1` uses SQLite's slot too.
                if let Expr::Value(value) = expression
                    && let Value::Placeholder(name) = &value.value
                {
                    let normalized = name.trim_start_matches(['$', ':', '@', '?']);
                    if normalized.is_empty() {
                        self.next_ordinal = self.next_ordinal.saturating_add(1);
                    } else if let Ok(ordinal) = normalized.parse::<u64>() {
                        self.next_ordinal = self.next_ordinal.max(ordinal.saturating_add(1));
                    }
                }
                return ControlFlow::Continue(());
            }
            let Expr::BinaryOp { right, .. } = expression else {
                return ControlFlow::Continue(());
            };
            let value = match right.as_ref() {
                Expr::Value(value) => match &value.value {
                    Value::SingleQuotedString(value) | Value::DoubleQuotedString(value) => {
                        Some(DocumentValue::String(value.clone()))
                    }
                    Value::Placeholder(name) => {
                        let bare = name == "?";
                        let mut name = name.trim_start_matches(['$', ':', '@', '?']).to_owned();
                        if bare {
                            name = self.next_ordinal.to_string();
                        }
                        let Some(value) = self.parameters.get(&name) else {
                            // Leave missing bindings to the normal collector
                            // below so they retain UnboundParameter semantics.
                            return ControlFlow::Continue(());
                        };
                        Some(value.clone())
                    }
                    _ => None,
                },
                _ => None,
            };
            let Some(DocumentValue::String(query)) = value else {
                self.error = Some(SqlError::plain(SqlErrorClass::InvalidBound));
                return ControlFlow::Break(());
            };
            if super::spec::validate_search_query(&query).is_err() {
                self.error = Some(SqlError::plain(SqlErrorClass::InvalidBound));
                return ControlFlow::Break(());
            }
            ControlFlow::Continue(())
        }
    }

    let mut validator = MatchValidator { parameters, error: None, next_ordinal: 1 };
    let _ = parsed.statement().visit(&mut validator);
    validator.error.map_or(Ok(()), Err)
}

#[derive(Default)]
struct ParameterCollector {
    names: Vec<String>,
    next_ordinal: u64,
}

impl Visitor for ParameterCollector {
    type Break = ();

    fn pre_visit_expr(&mut self, expression: &Expr) -> ControlFlow<Self::Break> {
        if let Expr::Value(value) = expression
            && let Value::Placeholder(name) = &value.value
        {
            let normalized = name.trim_start_matches(['$', ':', '@', '?']);
            if normalized.is_empty() {
                let ordinal = self.next_ordinal.max(1);
                self.names.push(ordinal.to_string());
                self.next_ordinal = ordinal.saturating_add(1);
            } else {
                self.names.push(normalized.to_owned());
                if let Ok(ordinal) = normalized.parse::<u64>() {
                    self.next_ordinal = self.next_ordinal.max(ordinal.saturating_add(1));
                }
            }
        }
        ControlFlow::Continue(())
    }
}

fn unsupported(construct: &str) -> SqlError {
    SqlError::binding(SqlErrorClass::UnsupportedStatement, construct)
}

fn lower_projection(items: &[SelectItem]) -> Result<Option<Vec<SqlProjection>>, SqlError> {
    if items.len() == 1 && matches!(items[0], SelectItem::Wildcard(_)) {
        return Ok(None);
    }
    let mut fields = Vec::with_capacity(items.len());
    for item in items {
        let (expr, output) = match item {
            SelectItem::UnnamedExpr(expr) => (expr, None),
            SelectItem::ExprWithAlias { expr, alias, .. } => (expr, Some(alias.value.as_str())),
            _ => {
                return Err(unsupported("projection"));
            }
        };
        let field = field_name(expr)?;
        let output = output.map_or_else(|| field.clone(), ToOwned::to_owned);
        if !valid_identifier(&output)
            || fields.iter().any(|known: &SqlProjection| known.output == output)
        {
            return Err(SqlError::binding(SqlErrorClass::InvalidIdentifier, &output));
        }
        fields.push(SqlProjection { field, output });
    }
    Ok(Some(fields))
}

fn lower_ordering(
    order_by: Option<&sqlparser::ast::OrderBy>,
) -> Result<Vec<crate::query::OrderingKey>, SqlError> {
    let Some(order_by) = order_by else { return Ok(Vec::new()) };
    let OrderByKind::Expressions(expressions) = &order_by.kind else {
        return Err(unsupported("ORDER BY ALL"));
    };
    expressions
        .iter()
        .map(|item| {
            let field = field_name(&item.expr)?;
            let direction = if item.options.asc == Some(false) {
                crate::query::OrderDirection::Descending
            } else {
                crate::query::OrderDirection::Ascending
            };
            let nulls = match item.options.nulls_first {
                Some(true) => crate::query::NullsPlacement::First,
                Some(false) => crate::query::NullsPlacement::Last,
                None => crate::query::NullsPlacement::Implicit,
            };
            crate::query::OrderingKey::try_new_with_nulls(&field, direction, nulls)
                .map_err(|_| SqlError::binding(SqlErrorClass::InvalidIdentifier, &field))
        })
        .collect()
}

fn lower_paging(
    clause: Option<&LimitClause>,
    parameters: &SqlParameters,
    used: &mut std::collections::BTreeSet<String>,
) -> Result<(Option<u32>, u64), SqlError> {
    let Some(clause) = clause else { return Ok((None, 0)) };
    let (limit_expr, offset_expr) = match clause {
        LimitClause::LimitOffset { limit, offset, limit_by } => {
            if !limit_by.is_empty() {
                return Err(unsupported("LIMIT BY"));
            }
            (limit.as_ref(), offset.as_ref().map(|value| &value.value))
        }
        LimitClause::OffsetCommaLimit { offset, limit } => (Some(limit), Some(offset)),
    };
    let limit = match limit_expr {
        None => None,
        Some(expr) => {
            let value = parse_u32(&literal(expr, parameters, used)?)?;
            if value == 0 || value > crate::query::MAX_PAGE_LIMIT {
                return Err(SqlError::plain(SqlErrorClass::InvalidBound));
            }
            Some(value)
        }
    };
    let offset = match offset_expr {
        None => 0,
        Some(expr) => parse_u64(&literal(expr, parameters, used)?)?,
    };
    Ok((limit, offset))
}

fn parse_u64(value: &DocumentValue) -> Result<u64, SqlError> {
    match value {
        DocumentValue::Integer(v) if *v >= 0 => {
            u64::try_from(*v).map_err(|_| SqlError::plain(SqlErrorClass::InvalidBound))
        }
        _ => Err(SqlError::plain(SqlErrorClass::InvalidBound)),
    }
}
fn parse_u32(value: &DocumentValue) -> Result<u32, SqlError> {
    parse_u64(value)?.try_into().map_err(|_| SqlError::plain(SqlErrorClass::InvalidBound))
}

fn lower_condition(
    expression: &Expr,
    parameters: &SqlParameters,
    used: &mut std::collections::BTreeSet<String>,
    out: &mut Vec<crate::query::QueryCondition>,
) -> Result<(), SqlError> {
    match expression {
        Expr::Nested(inner) => return lower_condition(inner, parameters, used, out),
        Expr::BinaryOp { left, op: BinaryOperator::And, right } => {
            lower_condition(left, parameters, used, out)?;
            return lower_condition(right, parameters, used, out);
        }
        // `sqlparser`'s SQLite precedence currently places `->`/`->>` below
        // comparisons, so `body ->> '$.status' = :status` arrives as
        // `body ->> ('$.status' = :status)`.  Recover the SQLite meaning here,
        // while still requiring the path and RHS to be closed scalar forms.
        Expr::BinaryOp { left, op: BinaryOperator::Arrow | BinaryOperator::LongArrow, right }
            if let Expr::BinaryOp { left: path, op, right: value } = right.as_ref() =>
        {
            let field = json_field_name(left, path)?;
            let value = literal(value, parameters, used)?;
            let condition = match op {
                BinaryOperator::Eq => crate::query::QueryCondition::Equals { field, value },
                BinaryOperator::NotEq => {
                    out.push(crate::query::QueryCondition::Exists { field: field.clone() });
                    crate::query::QueryCondition::NotEquals { field, value }
                }
                BinaryOperator::Gt => crate::query::QueryCondition::GreaterThan { field, value },
                BinaryOperator::GtEq => {
                    crate::query::QueryCondition::GreaterOrEqual { field, value }
                }
                BinaryOperator::Lt => crate::query::QueryCondition::LessThan { field, value },
                BinaryOperator::LtEq => crate::query::QueryCondition::LessOrEqual { field, value },
                _ => return Err(unsupported("predicate operator")),
            };
            out.push(condition);
            return Ok(());
        }
        Expr::BinaryOp { left, op, right } => {
            let field = field_name(left)?;
            let value = literal(right, parameters, used)?;
            let condition = match op {
                BinaryOperator::Eq => crate::query::QueryCondition::Equals { field, value },
                BinaryOperator::NotEq => {
                    out.push(crate::query::QueryCondition::Exists { field: field.clone() });
                    crate::query::QueryCondition::NotEquals { field, value }
                }
                BinaryOperator::Gt => crate::query::QueryCondition::GreaterThan { field, value },
                BinaryOperator::GtEq => {
                    crate::query::QueryCondition::GreaterOrEqual { field, value }
                }
                BinaryOperator::Lt => crate::query::QueryCondition::LessThan { field, value },
                BinaryOperator::LtEq => crate::query::QueryCondition::LessOrEqual { field, value },
                BinaryOperator::Match => {
                    let DocumentValue::String(query) = value else {
                        return Err(SqlError::plain(SqlErrorClass::InvalidBound));
                    };
                    if super::spec::validate_search_query(&query).is_err() {
                        return Err(SqlError::plain(SqlErrorClass::InvalidBound));
                    }
                    crate::query::QueryCondition::Search { field, query, match_any: false }
                }
                _ => return Err(unsupported("predicate operator")),
            };
            out.push(condition);
            return Ok(());
        }
        Expr::IsNull(inner) => out.push(crate::query::QueryCondition::Equals {
            field: field_name(inner)?,
            value: DocumentValue::Null,
        }),
        Expr::IsNotNull(inner) => {
            let field = field_name(inner)?;
            out.push(crate::query::QueryCondition::Exists { field: field.clone() });
            out.push(crate::query::QueryCondition::NotEquals { field, value: DocumentValue::Null });
        }
        Expr::Between { expr, negated, low, high } if !negated => {
            out.push(crate::query::QueryCondition::Between {
                field: field_name(expr)?,
                low: literal(low, parameters, used)?,
                high: literal(high, parameters, used)?,
            });
        }
        Expr::InList { expr, list, negated } if !negated => {
            out.push(crate::query::QueryCondition::In {
                field: field_name(expr)?,
                values: list
                    .iter()
                    .map(|item| literal(item, parameters, used))
                    .collect::<Result<_, _>>()?,
            });
        }
        Expr::Like { negated, expr, pattern, escape_char, .. }
            if !negated && escape_char.is_none() =>
        {
            let field = field_name(expr)?;
            let DocumentValue::String(pattern) = literal(pattern, parameters, used)? else {
                return Err(unsupported("LIKE pattern"));
            };
            let condition =
                if pattern.starts_with('%') && pattern.ends_with('%') && pattern.len() > 2 {
                    crate::query::QueryCondition::Contains {
                        field,
                        value: pattern[1..pattern.len() - 1].to_owned(),
                    }
                } else if let Some(value) = pattern.strip_suffix('%') {
                    crate::query::QueryCondition::StartsWith { field, value: value.to_owned() }
                } else if let Some(value) = pattern.strip_prefix('%') {
                    crate::query::QueryCondition::EndsWith { field, value: value.to_owned() }
                } else {
                    return Err(unsupported("LIKE pattern"));
                };
            out.push(condition);
        }
        _ => return Err(unsupported("WHERE expression")),
    }
    Ok(())
}

fn field_name(expression: &Expr) -> Result<String, SqlError> {
    let field = match expression {
        Expr::Identifier(identifier) => identifier.value.clone(),
        Expr::BinaryOp { left, op: BinaryOperator::Arrow | BinaryOperator::LongArrow, right } => {
            json_field_name(left, right)?
        }
        _ => return Err(unsupported("field expression")),
    };
    if !valid_identifier(&field) {
        return Err(SqlError::binding(SqlErrorClass::InvalidIdentifier, &field));
    }
    Ok(field)
}

/// Lowers a SQLite JSON arrow's scalar path to the dotted field identity used
/// by the typed planner.  Paths are intentionally restricted to identifiers;
/// array subscripts and wildcard paths remain execute-only.
fn json_field_name(root: &Expr, path_expression: &Expr) -> Result<String, SqlError> {
    let root = field_name(root)?;
    let Expr::Value(value) = path_expression else {
        return Err(unsupported("JSON path"));
    };
    let Value::SingleQuotedString(path) = &value.value else {
        return Err(unsupported("JSON path"));
    };
    let path = path
        .strip_prefix('$')
        .and_then(|path| path.strip_prefix('.'))
        .ok_or_else(|| unsupported("JSON path"))?;
    if path.is_empty() || path.split('.').any(|part| !valid_identifier(part)) {
        return Err(SqlError::binding(SqlErrorClass::InvalidIdentifier, path));
    }
    Ok(format!("{root}.{path}"))
}

fn literal(
    expression: &Expr,
    parameters: &SqlParameters,
    used: &mut std::collections::BTreeSet<String>,
) -> Result<DocumentValue, SqlError> {
    let Expr::Value(value) = expression else {
        return Err(unsupported("literal expression"));
    };
    match &value.value {
        Value::Null => Ok(DocumentValue::Null),
        Value::Boolean(value) => Ok(DocumentValue::Boolean(*value)),
        Value::SingleQuotedString(value) | Value::DoubleQuotedString(value) => {
            Ok(DocumentValue::String(value.clone()))
        }
        Value::Number(value, _) => value
            .parse::<i64>()
            .map(DocumentValue::Integer)
            .or_else(|_| value.parse::<f64>().map(DocumentValue::Number))
            .map_err(|_| unsupported("numeric literal")),
        Value::Placeholder(name) => {
            // Keep direct callers of `validate_parameters` and `lower` in
            // agreement with QueryStore's marker normalization: sqlparser
            // preserves the `?` in numbered markers (`?1`), while named
            // markers arrive with their sigil.  All of these names share the
            // same out-of-band binding key.
            let bare = name == "?";
            let mut name = name.trim_start_matches(['$', ':', '@', '?']).to_owned();
            if bare {
                let mut ordinal = 1_u64;
                while used.contains(&ordinal.to_string()) {
                    ordinal = ordinal.saturating_add(1);
                }
                name = ordinal.to_string();
            }
            if name.is_empty() {
                return Err(SqlError::binding(SqlErrorClass::UnboundParameter, name.as_str()));
            }
            let Some(value) = parameters.get(&name) else {
                return Err(SqlError::binding(SqlErrorClass::UnboundParameter, &name));
            };
            used.insert(name);
            Ok(value.clone())
        }
        _ => Err(unsupported("literal")),
    }
}

#[cfg(test)]
mod tests {
    use super::{SqlErrorClass, SqlParameters, validate_parameters};
    use crate::types::DocumentValue;

    #[test]
    fn numbered_question_mark_bindings_use_their_numeric_names() {
        let parameters = SqlParameters::try_new(&[("1", DocumentValue::Integer(30))]).unwrap();
        validate_parameters("SELECT * FROM users WHERE age >= ?1", &parameters).unwrap();
    }

    #[test]
    fn bare_question_mark_bindings_keep_sequential_slot_behavior() {
        let parameters = SqlParameters::try_new(&[
            ("1", DocumentValue::Integer(18)),
            ("2", DocumentValue::Integer(65)),
        ])
        .unwrap();
        validate_parameters("SELECT * FROM users WHERE age >= ? AND age <= ?", &parameters)
            .unwrap();
    }

    #[test]
    fn missing_numbered_binding_remains_a_typed_refusal() {
        let parameters = SqlParameters::new();
        assert_eq!(
            validate_parameters("SELECT * FROM users WHERE age >= ?1", &parameters)
                .unwrap_err()
                .class(),
            SqlErrorClass::UnboundParameter
        );
    }

    #[test]
    fn match_bindings_require_bounded_text_even_for_execute_only_sql() {
        assert_eq!(
            validate_parameters(
                "SELECT * FROM users WHERE body MATCH :query",
                &SqlParameters::new()
            )
            .unwrap_err()
            .class(),
            SqlErrorClass::UnboundParameter
        );

        let numeric = SqlParameters::try_new(&[("query", DocumentValue::Integer(7))]).unwrap();
        assert_eq!(
            validate_parameters("SELECT * FROM users JOIN archive ON users.id = archive.id WHERE users.body MATCH :query", &numeric)
                .unwrap_err()
                .class(),
            SqlErrorClass::InvalidBound
        );

        let oversized = SqlParameters::try_new(&[(
            "query",
            DocumentValue::String("x".repeat(crate::query::MAX_SEARCH_QUERY_BYTES + 1)),
        )])
        .unwrap();
        assert_eq!(
            validate_parameters("SELECT * FROM users WHERE body MATCH :query", &oversized)
                .unwrap_err()
                .class(),
            SqlErrorClass::InvalidBound
        );
    }

    #[test]
    fn match_values_remain_data_when_they_look_like_fts_syntax() {
        let parameters = SqlParameters::try_new(&[(
            "query",
            DocumentValue::String("' OR 1=1 -- NEAR(secret)".to_owned()),
        )])
        .unwrap();
        validate_parameters("SELECT * FROM users WHERE body MATCH :query", &parameters).unwrap();

        let error = validate_parameters(
            "SELECT * FROM users JOIN archive ON users.id = archive.id WHERE users.body MATCH :query",
            &SqlParameters::try_new(&[("query", DocumentValue::String("quick OR secret".into()))])
                .unwrap(),
        )
        .unwrap_err();
        assert_eq!(error.class(), SqlErrorClass::UnsupportedStatement);
    }

    #[test]
    fn execute_only_match_shapes_are_rejected_before_sqlite() {
        let parameters =
            SqlParameters::try_new(&[("query", DocumentValue::String("quick".to_owned()))])
                .unwrap();
        let error = validate_parameters(
            "SELECT users._id FROM users JOIN archive ON users._id = archive._id WHERE users.body MATCH :query",
            &parameters,
        )
        .unwrap_err();
        assert_eq!(error.class(), SqlErrorClass::UnsupportedStatement);
        assert_eq!(error.construct(), Some("MATCH"));
    }

    #[test]
    fn match_guard_uses_sqlite_slot_after_numbered_binding() {
        // SQLite gives the bare marker slot 2 because ?1 already occupies
        // slot 1. The guard must validate the text binding used by MATCH,
        // rather than reusing the numeric scalar binding.
        let parameters = SqlParameters::try_new(&[
            ("1", DocumentValue::Integer(30)),
            ("2", DocumentValue::String("quick".to_owned())),
        ])
        .unwrap();
        validate_parameters("SELECT * FROM users WHERE age >= ?1 AND body MATCH ?", &parameters)
            .unwrap();
    }
}
