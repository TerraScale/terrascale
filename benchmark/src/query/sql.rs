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

use std::ops::ControlFlow;

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
    pub(crate) projection: Option<Vec<String>>,
    pub(crate) offset: u64,
    pub(crate) limit: Option<u32>,
}

/// Execute-only SQL retained for shapes the typed subscribable model cannot
/// represent (joins, reducers, windows, subqueries, and CTEs).
#[derive(Clone, Debug, PartialEq)]
pub(crate) struct SqlRuntime {
    pub(crate) text: String,
    pub(crate) parameters: SqlParameters,
    pub(crate) tables: Vec<String>,
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
        /// The non-live operator: `join`, `aggregate`, `window`, or `subquery`.
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

    /// Returns the non-live operator: `join`, `aggregate`, `window`, or `subquery`.
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
/// A `Live` tag is the subscribe shape, not a lowering promise: the planner
/// still validates the launch lowering exactly, so shapes the executor
/// cannot express refuse with [`SqlErrorClass::UnsupportedStatement`] at
/// compile.
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
        if select.from.iter().any(|table| !table.joins.is_empty()) {
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
    if select.distinct.is_some()
        || select.into.is_some()
        || !select.lateral_views.is_empty()
        || select.prewhere.is_some()
        || !matches!(select.group_by, sqlparser::ast::GroupByExpr::Expressions(ref values, ref modifiers) if values.is_empty() && modifiers.is_empty())
        || select.having.is_some()
        || !select.cluster_by.is_empty()
        || !select.distribute_by.is_empty()
        || !select.sort_by.is_empty()
        || !select.named_window.is_empty()
        || select.qualify.is_some()
    {
        return Err(SqlError::binding(SqlErrorClass::UnsupportedStatement, "SELECT clauses"));
    }
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
    parse_u64(&bound_literal(expression, parameters)?)?;
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
    validate_paging_bounds(&parsed, parameters)?;
    if matches!(classify_live_subset(&parsed), LiveSubset::Live) {
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

fn lower_projection(items: &[SelectItem]) -> Result<Option<Vec<String>>, SqlError> {
    if items.len() == 1 && matches!(items[0], SelectItem::Wildcard(_)) {
        return Ok(None);
    }
    let mut fields = Vec::with_capacity(items.len());
    for item in items {
        let (SelectItem::UnnamedExpr(expr) | SelectItem::ExprWithAlias { expr, .. }) = item else {
            return Err(unsupported("projection"));
        };
        fields.push(field_name(expr)?);
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
                        return Err(unsupported("MATCH value"));
                    };
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
            let root = field_name(left)?;
            let Expr::Value(value) = right.as_ref() else {
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
            format!("{root}.{path}")
        }
        _ => return Err(unsupported("field expression")),
    };
    if !valid_identifier(&field) {
        return Err(SqlError::binding(SqlErrorClass::InvalidIdentifier, &field));
    }
    Ok(field)
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
            let mut name = name.trim_start_matches(['$', ':', '@']).to_owned();
            if name == "?" {
                let mut ordinal = 1_u64;
                while parameters.contains(&ordinal.to_string()) {
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
