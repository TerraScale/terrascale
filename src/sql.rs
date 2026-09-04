//! Typed SQL builder plus raw parameterized SQL (GOAL D-78).
//!
//! Application queries are live: a builder or raw query ends in
//! [`ReactiveClient::subscribe`](crate::reactive::ReactiveClient::subscribe)
//! (CDN HTTP by default) or [`ReactiveClient::execute`] ("subscribe once").
//! Read-only SQL over HTTP is `POST /api/v1/databases/{id}/sql` (GOAL D-54):
//! `select`/`with`/`explain` only, parameters bound separately, never
//! interpolated.

use serde::{Deserialize, Serialize};

use crate::client::SharedClient;
use crate::documents::DatabaseScope;
use crate::error::{Error, ErrorKind, Result};
use crate::schema::{is_valid_identifier, CollectionSchema, FieldDef, FieldType};
use crate::transport::{Method, Request};

/// Sort direction for `ORDER BY`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Direction {
    Asc,
    Desc,
}

/// Start a schema-bound typed `SELECT` query.
pub fn select(schema: &CollectionSchema) -> Result<SelectBuilder> {
    SelectBuilder::from_schema(schema)
}

/// A parameterized statement: SQL text with `$1`-style placeholders plus
/// separately bound parameters.
#[derive(Debug, Clone, PartialEq)]
pub struct Query {
    sql: String,
    params: Vec<serde_json::Value>,
    validation_error: Option<String>,
}

impl Query {
    /// Build a raw parameterized statement.
    ///
    /// Prefer [`Query::try_raw`]. This compatibility constructor keeps the
    /// original infallible SDK surface, but an invalid query is marked and
    /// will be rejected before the SDK sends a request.
    #[must_use]
    pub fn raw(sql: impl Into<String>, params: Vec<serde_json::Value>) -> Self {
        let sql = sql.into();
        match Self::try_raw(sql.clone(), params.clone()) {
            Ok(query) => query,
            Err(error) => Self {
                sql,
                params,
                validation_error: Some(error.message),
            },
        }
    }

    /// Build and validate raw parameterized SQL.
    ///
    /// Only one read-only `SELECT`, `WITH`, or `EXPLAIN` statement is
    /// accepted. SQL values remain in `params`; they are never interpolated.
    pub fn try_raw(sql: impl Into<String>, params: Vec<serde_json::Value>) -> Result<Self> {
        let sql = sql.into();
        validate_sql(&sql, &params)?;
        Ok(Self {
            sql,
            params,
            validation_error: None,
        })
    }

    /// Start a typed `SELECT <columns>` query.
    #[must_use]
    pub fn select(columns: &[&str]) -> SelectBuilder {
        SelectBuilder::new(columns)
    }

    /// Start a schema-bound typed query. Fields passed to the builder must
    /// belong to `schema`; this keeps the happy path tied to schema
    /// descriptors instead of arbitrary SQL fragments.
    pub fn select_schema(schema: &CollectionSchema) -> Result<SelectBuilder> {
        SelectBuilder::from_schema(schema)
    }

    /// Alias for [`Query::select_schema`].
    pub fn from_schema(schema: &CollectionSchema) -> Result<SelectBuilder> {
        Self::select_schema(schema)
    }

    /// Statement text.
    #[must_use]
    pub fn sql(&self) -> &str {
        &self.sql
    }

    /// Bound parameters in placeholder order.
    #[must_use]
    pub fn params(&self) -> &[serde_json::Value] {
        &self.params
    }

    /// Validate a query before handing it to a transport.
    pub fn validate(&self) -> Result<()> {
        if let Some(message) = &self.validation_error {
            return Err(Error::new(ErrorKind::Validation, "invalid_sql", message));
        }
        validate_sql(&self.sql, &self.params)
    }

    /// Whether this query was rejected by the compatibility constructor.
    /// Prefer [`Query::try_raw`] when the caller needs the validation error.
    #[must_use]
    pub fn is_err(&self) -> bool {
        self.validation_error.is_some()
    }
}

/// Fluent builder for a live-subset `SELECT` query.
#[derive(Debug, Clone)]
pub struct SelectBuilder {
    columns: Vec<String>,
    from: Option<String>,
    schema_fields: Option<Vec<FieldDef>>,
    parameterized_pagination: bool,
    predicates: Vec<String>,
    order: Vec<(String, Direction)>,
    limit: Option<u64>,
    offset: Option<u64>,
    params: Vec<serde_json::Value>,
}

impl SelectBuilder {
    fn new(columns: &[&str]) -> Self {
        Self {
            columns: columns.iter().map(ToString::to_string).collect(),
            from: None,
            schema_fields: None,
            parameterized_pagination: false,
            predicates: Vec::new(),
            order: Vec::new(),
            limit: None,
            offset: None,
            params: Vec::new(),
        }
    }

    /// Start a query whose collection and field descriptors come from a
    /// declared schema.
    pub fn from_schema(schema: &CollectionSchema) -> Result<Self> {
        let collection = validate_identifier(&schema.name, "collection")?;
        if schema.fields.iter().any(|field| {
            field.name.is_empty() || validate_identifier(&field.name, "field").is_err()
        }) {
            return Err(Error::new(
                ErrorKind::Validation,
                "invalid_schema_field",
                "Schema field names must match [A-Za-z_][A-Za-z0-9_]*.",
            ));
        }
        Ok(Self {
            columns: vec!["*".to_owned()],
            from: Some(collection),
            schema_fields: Some(schema.fields.clone()),
            parameterized_pagination: true,
            predicates: Vec::new(),
            order: Vec::new(),
            limit: None,
            offset: None,
            params: Vec::new(),
        })
    }

    /// Add a selected schema field. The field must be declared by the
    /// schema-bound collection.
    pub fn column(mut self, field: &FieldDef) -> Result<Self> {
        let name = self.checked_field(field, "column")?;
        if self.columns == ["*"] {
            self.columns.clear();
        }
        self.columns.push(name);
        Ok(self)
    }

    /// Add several selected schema fields.
    pub fn columns(mut self, fields: &[&FieldDef]) -> Result<Self> {
        for field in fields {
            self = self.column(field)?;
        }
        Ok(self)
    }

    /// Add an equality predicate over a declared field.
    pub fn where_eq<T: Serialize>(self, field: &FieldDef, value: T) -> Result<Self> {
        self.where_comparison(field, "=", serialize_param(value)?)
    }

    /// Add an inequality predicate over a declared field.
    pub fn where_ne<T: Serialize>(self, field: &FieldDef, value: T) -> Result<Self> {
        self.where_comparison(field, "<>", serialize_param(value)?)
    }

    /// Add a greater-than predicate over a declared field.
    pub fn where_gt<T: Serialize>(self, field: &FieldDef, value: T) -> Result<Self> {
        self.where_comparison(field, ">", serialize_param(value)?)
    }

    /// Add a greater-than-or-equal predicate over a declared field.
    pub fn where_gte<T: Serialize>(self, field: &FieldDef, value: T) -> Result<Self> {
        self.where_comparison(field, ">=", serialize_param(value)?)
    }

    /// Add a less-than predicate over a declared field.
    pub fn where_lt<T: Serialize>(self, field: &FieldDef, value: T) -> Result<Self> {
        self.where_comparison(field, "<", serialize_param(value)?)
    }

    /// Add a less-than-or-equal predicate over a declared field.
    pub fn where_lte<T: Serialize>(self, field: &FieldDef, value: T) -> Result<Self> {
        self.where_comparison(field, "<=", serialize_param(value)?)
    }

    fn where_comparison(
        mut self,
        field: &FieldDef,
        operator: &str,
        value: serde_json::Value,
    ) -> Result<Self> {
        let name = self.checked_field(field, "predicate")?;
        self.predicates
            .push(format!("{name} {operator} ${}", self.params.len() + 1));
        self.params.push(value);
        Ok(self)
    }

    /// Add an FTS5 `MATCH` predicate over a declared string field.
    pub fn where_match(self, field: &FieldDef, query: impl Into<String>) -> Result<Self> {
        if !matches!(field.field_type, FieldType::String | FieldType::Text) {
            return Err(Error::new(
                ErrorKind::Validation,
                "invalid_match_field",
                "FTS MATCH requires a string field.",
            ));
        }
        self.where_comparison(field, "MATCH", serde_json::Value::String(query.into()))
    }

    /// Alias for [`SelectBuilder::where_match`].
    pub fn match_(self, field: &FieldDef, query: impl Into<String>) -> Result<Self> {
        self.where_match(field, query)
    }

    /// Append an `ORDER BY` key from a declared field.
    pub fn order_by_field(self, field: &FieldDef, direction: Direction) -> Result<Self> {
        let name = self.checked_field(field, "order_by")?;
        self.order_by(&name, direction)
    }

    fn checked_field(&self, field: &FieldDef, context: &str) -> Result<String> {
        let name = validate_identifier(&field.name, context)?;
        match &self.schema_fields {
            Some(fields) if fields.iter().any(|declared| declared == field) => Ok(name),
            Some(_) => Err(Error::new(
                ErrorKind::Validation,
                "unknown_schema_field",
                format!("{context} field is not declared by this collection."),
            )),
            None => Err(Error::new(
                ErrorKind::Validation,
                "schema_required",
                "Use Query::select_schema before passing schema field descriptors.",
            )),
        }
    }

    /// Collection to query.
    pub fn from(mut self, collection: &str) -> Result<Self> {
        let collection = validate_identifier(collection, "collection")?;
        if self.schema_fields.is_some() && self.from.as_deref() != Some(collection.as_str()) {
            return Err(Error::new(
                ErrorKind::Validation,
                "schema_collection_immutable",
                "A schema-bound query cannot change its collection.",
            ));
        }
        self.from = Some(collection);
        Ok(self)
    }

    /// Add a `WHERE` predicate (combined with `AND`). Use `$n`
    /// placeholders and pass values via [`SelectBuilder::param`].
    pub fn where_(mut self, predicate: impl Into<String>) -> Self {
        self.predicates.push(predicate.into());
        self
    }

    /// Append an `ORDER BY` key.
    pub fn order_by(mut self, column: &str, direction: Direction) -> Result<Self> {
        self.order
            .push((validate_identifier(column, "order_by")?, direction));
        Ok(self)
    }

    /// Append `LIMIT`.
    #[must_use]
    pub fn limit(mut self, limit: u64) -> Self {
        self.limit = Some(limit);
        self
    }

    /// Append `OFFSET`.
    #[must_use]
    pub fn offset(mut self, offset: u64) -> Self {
        self.offset = Some(offset);
        self
    }

    /// Bind one parameter for the next `$n` placeholder.
    #[must_use]
    pub fn param(mut self, value: serde_json::Value) -> Self {
        self.params.push(value);
        self
    }

    /// Render the parameterized statement.
    pub fn build(self) -> Result<Query> {
        let from = self.from.ok_or_else(|| {
            Error::new(
                ErrorKind::Validation,
                "missing_from",
                "A query requires .from(collection).",
            )
        })?;
        if self.columns.is_empty() {
            return Err(Error::new(
                ErrorKind::Validation,
                "missing_columns",
                "A query requires at least one selected column.",
            ));
        }
        let mut columns = Vec::with_capacity(self.columns.len());
        for column in &self.columns {
            columns.push(validate_projection(column)?);
        }
        for predicate in &self.predicates {
            validate_compatibility_predicate(predicate)?;
        }
        let mut sql = format!("SELECT {} FROM {from}", columns.join(", "));
        if !self.predicates.is_empty() {
            sql.push_str(&format!(" WHERE {}", self.predicates.join(" AND ")));
        }
        if !self.order.is_empty() {
            let keys: Vec<String> = self
                .order
                .iter()
                .map(|(column, direction)| {
                    format!(
                        "{column} {}",
                        match direction {
                            Direction::Asc => "ASC",
                            Direction::Desc => "DESC",
                        }
                    )
                })
                .collect();
            sql.push_str(&format!(" ORDER BY {}", keys.join(", ")));
        }
        let mut params = self.params;
        if let Some(limit) = self.limit {
            if self.parameterized_pagination {
                sql.push_str(&format!(" LIMIT ${}", params.len() + 1));
                params.push(serde_json::json!(limit));
            } else {
                sql.push_str(&format!(" LIMIT {limit}"));
            }
        }
        if let Some(offset) = self.offset {
            if self.parameterized_pagination {
                sql.push_str(&format!(" OFFSET ${}", params.len() + 1));
                params.push(serde_json::json!(offset));
            } else {
                sql.push_str(&format!(" OFFSET {offset}"));
            }
        }
        Query::try_raw(sql, params)
    }
}

fn validate_identifier(value: &str, field: &str) -> Result<String> {
    if !is_valid_identifier(value) {
        return Err(Error::new(
            ErrorKind::Validation,
            "invalid_identifier",
            format!("{field} must match [A-Za-z_][A-Za-z0-9_]*."),
        ));
    }
    Ok(value.to_owned())
}

fn validate_projection(value: &str) -> Result<String> {
    if value.trim() == "*" {
        return Ok("*".to_owned());
    }
    validate_identifier(value, "column")
}

fn validate_compatibility_predicate(value: &str) -> Result<()> {
    let tokens = tokenize_sql(value)?;
    if tokens.len() != 3 {
        return Err(sql_error(
            "Use a schema field predicate method instead of a free-form SQL fragment.",
        ));
    }
    if !validate_identifier(&tokens[0], "predicate field").is_ok()
        || !matches!(
            tokens[1].to_ascii_uppercase().as_str(),
            "=" | "<>" | "!=" | ">" | ">=" | "<" | "<=" | "MATCH" | "LIKE"
        )
        || !tokens[2].starts_with('$')
    {
        return Err(sql_error(
            "Use a schema field predicate method instead of a free-form SQL fragment.",
        ));
    }
    Ok(())
}

/// Validate the client-side portion of the D-54 SQL contract.
///
/// The member performs the authoritative SQLite AST validation. The SDK
/// still rejects obvious unsafe input before transport, including comments,
/// multiple statements, write/DDL keywords, dangerous functions, and
/// malformed positional bindings.
pub fn validate_sql(text: &str, params: &[serde_json::Value]) -> Result<()> {
    if text.len() > 1_048_576 {
        return Err(sql_error("SQL exceeds the 1 MiB query text limit."));
    }
    let tokens = tokenize_sql(text)?;
    let first = tokens.first().map(String::as_str).ok_or_else(|| {
        Error::new(
            ErrorKind::Validation,
            "invalid_sql",
            "SQL must not be empty.",
        )
    })?;
    let mut body_start = 1;
    match first.to_ascii_lowercase().as_str() {
        "select" => {}
        "with" => {
            if tokens
                .get(1)
                .is_some_and(|token| token.eq_ignore_ascii_case("recursive"))
            {
                return Err(sql_error(
                    "WITH RECURSIVE is not allowed by the read-only SQL contract.",
                ));
            }
        }
        "explain" => {
            if tokens
                .get(1)
                .is_some_and(|token| token.eq_ignore_ascii_case("query"))
            {
                body_start = 3;
                if !tokens
                    .get(2)
                    .is_some_and(|token| token.eq_ignore_ascii_case("plan"))
                {
                    return Err(sql_error(
                        "EXPLAIN may only use EXPLAIN QUERY PLAN or EXPLAIN SELECT.",
                    ));
                }
            }
            if !tokens.get(body_start).is_some_and(|token| {
                token.eq_ignore_ascii_case("select") || token.eq_ignore_ascii_case("with")
            }) {
                return Err(sql_error(
                    "Only EXPLAIN SELECT and EXPLAIN QUERY PLAN SELECT are allowed.",
                ));
            }
        }
        _ => {
            return Err(sql_error(
                "Only SELECT, WITH, and EXPLAIN statements are allowed.",
            ))
        }
    }

    const FORBIDDEN: &[&str] = &[
        "attach",
        "alter",
        "analyze",
        "begin",
        "commit",
        "create",
        "delete",
        "detach",
        "drop",
        "end",
        "insert",
        "pragma",
        "reindex",
        "replace",
        "rollback",
        "savepoint",
        "transaction",
        "truncate",
        "update",
        "vacuum",
        "upsert",
    ];
    for (position, token) in tokens.iter().enumerate().skip(body_start) {
        let lowered = token.to_ascii_lowercase();
        if FORBIDDEN.iter().any(|forbidden| lowered == *forbidden) {
            return Err(sql_error(
                "SQL contains a DDL, DML, or transaction keyword.",
            ));
        }
        if matches!(
            lowered.as_str(),
            "current_date" | "current_time" | "current_timestamp"
        ) {
            return Err(sql_error(
                "Non-deterministic or unsafe SQL functions are not allowed.",
            ));
        }
        if tokens.get(position + 1).is_some_and(|next| next == "(")
            && matches!(
                lowered.as_str(),
                "random"
                    | "randomblob"
                    | "changes"
                    | "total_changes"
                    | "last_insert_rowid"
                    | "load_extension"
                    | "readfile"
                    | "writefile"
                    | "datetime"
                    | "date"
                    | "time"
                    | "strftime"
                    | "julianday"
                    | "unixepoch"
            )
        {
            return Err(sql_error(
                "Non-deterministic or unsafe SQL functions are not allowed.",
            ));
        }
        if lowered.starts_with("sqlite_") || lowered.starts_with("pragma_") {
            return Err(sql_error(
                "Internal SQLite names and pragma functions are not allowed.",
            ));
        }
    }

    validate_bindings(&tokens, params.len())
}

fn sql_error(message: impl Into<String>) -> Error {
    Error::new(ErrorKind::Validation, "invalid_sql", message)
}

fn serialize_param<T: Serialize>(value: T) -> Result<serde_json::Value> {
    serde_json::to_value(value).map_err(|error| {
        Error::new(
            ErrorKind::Validation,
            "invalid_parameter",
            format!("Could not serialize SQL parameter: {error}"),
        )
    })
}

fn tokenize_sql(text: &str) -> Result<Vec<String>> {
    let bytes = text.as_bytes();
    let mut tokens = Vec::new();
    let mut index = 0;
    while index < bytes.len() {
        match bytes[index] {
            b' ' | b'\t' | b'\r' | b'\n' => index += 1,
            b';' => {
                if bytes[index + 1..]
                    .iter()
                    .any(|byte| !byte.is_ascii_whitespace())
                {
                    return Err(sql_error("Multiple SQL statements are not allowed."));
                }
                index += 1;
            }
            b'\'' | b'"' | b'`' | b'[' => {
                let quote = bytes[index];
                let end = if quote == b'[' { b']' } else { quote };
                index += 1;
                let mut closed = false;
                while index < bytes.len() {
                    if bytes[index] == end {
                        if index + 1 < bytes.len() && bytes[index + 1] == end && quote != b']' {
                            index += 2;
                        } else {
                            index += 1;
                            closed = true;
                            break;
                        }
                    } else {
                        index += 1;
                    }
                }
                if !closed {
                    return Err(sql_error("SQL contains an unterminated quoted value."));
                }
                // Quoted values are opaque. Keeping a marker prevents words
                // inside strings from being mistaken for SQL keywords.
                tokens.push("<quoted>".to_owned());
            }
            b'-' if bytes.get(index + 1) == Some(&b'-') => {
                return Err(sql_error("SQL comments are not allowed."));
            }
            b'/' if bytes.get(index + 1) == Some(&b'*') => {
                return Err(sql_error("SQL comments are not allowed."));
            }
            b'$' => {
                let start = index;
                index += 1;
                while index < bytes.len() && bytes[index].is_ascii_digit() {
                    index += 1;
                }
                if index == start + 1 {
                    return Err(sql_error("Only numbered $n parameters are allowed."));
                }
                tokens.push(text[start..index].to_owned());
            }
            b'?' => {
                let start = index;
                index += 1;
                while index < bytes.len() && bytes[index].is_ascii_digit() {
                    index += 1;
                }
                tokens.push(text[start..index].to_owned());
            }
            byte if byte.is_ascii_alphanumeric() || byte == b'_' => {
                let start = index;
                index += 1;
                while index < bytes.len()
                    && (bytes[index].is_ascii_alphanumeric() || bytes[index] == b'_')
                {
                    index += 1;
                }
                tokens.push(text[start..index].to_owned());
            }
            _ => {
                tokens.push(text[index..index + 1].to_owned());
                index += 1;
            }
        }
    }
    Ok(tokens)
}

fn validate_bindings(tokens: &[String], parameter_count: usize) -> Result<()> {
    let mut next_unnumbered = 1usize;
    let mut highest = 0usize;
    let mut seen = std::collections::BTreeSet::new();
    for token in tokens {
        let number = if let Some(number) = token.strip_prefix('$') {
            number
                .parse::<usize>()
                .map_err(|_| sql_error("Invalid $n parameter."))?
        } else if token == "?" {
            let number = next_unnumbered;
            next_unnumbered += 1;
            number
        } else if let Some(number) = token.strip_prefix('?') {
            number
                .parse::<usize>()
                .map_err(|_| sql_error("Invalid ?n parameter."))?
        } else {
            continue;
        };
        if number == 0 {
            return Err(sql_error("Parameter numbering starts at 1."));
        }
        seen.insert(number);
        highest = highest.max(number);
    }
    if highest != parameter_count || seen.len() != parameter_count {
        return Err(sql_error(format!(
            "SQL has {highest} positional parameter slots but {} values were supplied.",
            parameter_count
        )));
    }
    Ok(())
}

/// Rows returned by [`SqlClient::execute`].
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct SqlRows {
    /// Column names in row order.
    pub columns: Vec<String>,
    /// One JSON value per row.
    pub rows: Vec<serde_json::Value>,
}

/// Read-only SQL client. Obtain from [`crate::TerraScaleClient::sql`].
#[derive(Debug, Clone)]
pub struct SqlClient {
    shared: SharedClient,
}

impl SqlClient {
    pub(crate) fn new(shared: SharedClient) -> Self {
        Self { shared }
    }

    /// Execute a typed-builder or raw query once ("subscribe once").
    pub async fn execute(&self, scope: &DatabaseScope, query: &Query) -> Result<SqlRows> {
        query.validate()?;
        let url = format!("{}/sql", self.shared.database_root(scope)?);
        let body = serde_json::json!({
            "statement": query.sql(),
            "parameters": query.params(),
        });
        let request = Request::json("sql.execute", Method::Post, url, &body)?;
        self.shared.send_typed(request).await
    }
}
