//! Typed SQL builder plus raw parameterized SQL (GOAL D-78).
//!
//! Application queries are live: a builder or raw query ends in
//! [`ReactiveClient::subscribe`](crate::reactive::ReactiveClient::subscribe)
//! (CDN HTTP by default) or [`ReactiveClient::execute`] ("subscribe once").
//! Read-only SQL over HTTP is `POST /api/v1/databases/{id}/sql` (GOAL D-54):
//! `select`/`with`/`explain` only, parameters bound separately, never
//! interpolated.

use std::collections::{BTreeMap, BTreeSet};

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

/// Metadata columns exposed on every collection table (D-78).
pub const METADATA_FIELDS: [&str; 5] = ["_id", "_rev", "_hlc", "_created", "_updated"];

/// Returns a schema descriptor for one immutable metadata column.
pub fn metadata_field(name: &str) -> Result<FieldDef> {
    let field_type = match name {
        "_id" | "_rev" | "_hlc" => FieldType::String,
        "_created" | "_updated" => FieldType::Timestamp,
        _ => {
            return Err(Error::new(
                ErrorKind::Validation,
                "unknown_metadata_field",
                "Metadata fields are _id, _rev, _hlc, _created, and _updated.",
            ))
        }
    };
    Ok(FieldDef::required(name, field_type).indexed())
}

/// Returns descriptors for all immutable metadata columns.
pub fn metadata_fields() -> Result<[FieldDef; 5]> {
    Ok([
        metadata_field("_id")?,
        metadata_field("_rev")?,
        metadata_field("_hlc")?,
        metadata_field("_created")?,
        metadata_field("_updated")?,
    ])
}

fn is_metadata_field(name: &str) -> bool {
    METADATA_FIELDS.contains(&name)
}

/// Start a schema-bound typed `SELECT` query.
pub fn select(schema: &CollectionSchema) -> Result<SelectBuilder> {
    SelectBuilder::from_schema(schema)
}

/// The canonical out-of-band SQL bindings.
///
/// Keys are plain names (`:customer_id`, `$customer_id`, and
/// `@customer_id`) or one-based ordinal strings (`$1`, `?1`, and `?`). The
/// map is serialized as an object by the HTTP SQL endpoint; values are never
/// interpolated into SQL text.
pub type SqlParameters = BTreeMap<String, serde_json::Value>;

/// Bounds shared with the HTTP SQL contract.
pub const MAX_SQL_TEXT_BYTES: usize = 1024 * 1024;
pub const MAX_SQL_TIMEOUT_MS: u64 = 30_000;
pub const MAX_SQL_PAGE_LIMIT: u64 = 1024;

/// Optional controls carried by an SQL execute request.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct SqlExecuteOptions {
    /// Request the query plan instead of rows.
    pub explain: bool,
    /// Bounded server deadline, in milliseconds.
    pub timeout_ms: Option<u64>,
    /// Mark the operation cancelled before execution.
    pub cancelled: bool,
}

impl SqlExecuteOptions {
    /// Validate controls against the HTTP contract.
    pub fn validate(self) -> Result<()> {
        if self
            .timeout_ms
            .is_some_and(|value| value == 0 || value > MAX_SQL_TIMEOUT_MS)
        {
            return Err(sql_error("timeout_ms must be between 1 and 30000."));
        }
        Ok(())
    }
}

/// Input accepted by [`Query::raw`] and [`Query::try_raw`]. Positional
/// vectors remain supported for source compatibility and are normalized to
/// ordinal object keys before validation and transport.
#[derive(Debug, Clone, PartialEq)]
pub enum QueryParameters {
    Positional(Vec<serde_json::Value>),
    Named(SqlParameters),
    Invalid(String),
}

impl From<Vec<serde_json::Value>> for QueryParameters {
    fn from(values: Vec<serde_json::Value>) -> Self {
        Self::Positional(values)
    }
}

impl From<&[serde_json::Value]> for QueryParameters {
    fn from(values: &[serde_json::Value]) -> Self {
        Self::Positional(values.to_vec())
    }
}

impl From<SqlParameters> for QueryParameters {
    fn from(values: SqlParameters) -> Self {
        Self::Named(values)
    }
}

impl From<serde_json::Map<String, serde_json::Value>> for QueryParameters {
    fn from(values: serde_json::Map<String, serde_json::Value>) -> Self {
        Self::Named(values.into_iter().collect())
    }
}

impl From<serde_json::Value> for QueryParameters {
    fn from(value: serde_json::Value) -> Self {
        match value {
            serde_json::Value::Array(values) => Self::Positional(values),
            serde_json::Value::Object(values) => Self::Named(values.into_iter().collect()),
            _ => Self::Invalid("SQL parameters must be an array or object.".to_owned()),
        }
    }
}

impl From<&serde_json::Value> for QueryParameters {
    fn from(value: &serde_json::Value) -> Self {
        value.clone().into()
    }
}

impl From<&SqlParameters> for QueryParameters {
    fn from(values: &SqlParameters) -> Self {
        Self::Named(values.clone())
    }
}

/// A parameterized statement with canonical named/ordinal object bindings.
#[derive(Debug, Clone, PartialEq)]
pub struct Query {
    sql: String,
    /// Positional values retained for the original `params()` accessor.
    /// Requests use `parameters`, never this compatibility view.
    params: Vec<serde_json::Value>,
    parameters: SqlParameters,
    options: SqlExecuteOptions,
    validation_error: Option<String>,
}

impl Query {
    /// Build a raw parameterized statement.
    ///
    /// Prefer [`Query::try_raw`]. This compatibility constructor keeps the
    /// original infallible SDK surface, but an invalid query is marked and
    /// will be rejected before the SDK sends a request.
    #[must_use]
    pub fn raw<P: Into<QueryParameters>>(sql: impl Into<String>, params: P) -> Self {
        let sql = sql.into();
        let params = params.into();
        match Self::try_raw(sql.clone(), params.clone()) {
            Ok(query) => query,
            Err(error) => Self {
                sql,
                params: compatibility_params(&params),
                parameters: canonical_parameters(&params),
                options: SqlExecuteOptions::default(),
                validation_error: Some(error.message),
            },
        }
    }

    /// Build and validate raw parameterized SQL.
    ///
    /// Only one read-only `SELECT`, `WITH`, or `EXPLAIN` statement is
    /// accepted. SQL values remain in `params`; they are never interpolated.
    pub fn try_raw<P: Into<QueryParameters>>(sql: impl Into<String>, params: P) -> Result<Self> {
        let sql = sql.into();
        let params = params.into();
        let parameters = normalize_parameters(params.clone())?;
        validate_parameters(&sql, &parameters)?;
        Ok(Self {
            sql,
            params: compatibility_params(&params),
            parameters,
            options: SqlExecuteOptions::default(),
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

    /// Canonical named/ordinal object bindings in wire order.
    #[must_use]
    pub fn parameters(&self) -> &SqlParameters {
        &self.parameters
    }

    /// Alias for [`Query::parameters`].
    #[must_use]
    pub fn bindings(&self) -> &SqlParameters {
        self.parameters()
    }

    /// Set request controls for a subsequent SQL execute.
    pub fn with_options(mut self, options: SqlExecuteOptions) -> Result<Self> {
        options.validate()?;
        self.options = options;
        Ok(self)
    }

    /// Request an execution plan rather than result rows.
    pub fn explain(mut self) -> Self {
        self.options.explain = true;
        self
    }

    /// Set the bounded server deadline in milliseconds.
    pub fn timeout_ms(mut self, timeout_ms: u64) -> Result<Self> {
        if timeout_ms == 0 || timeout_ms > MAX_SQL_TIMEOUT_MS {
            return Err(sql_error("timeout_ms must be between 1 and 30000."));
        }
        self.options.timeout_ms = Some(timeout_ms);
        Ok(self)
    }

    /// Mark a query as cancelled before it reaches the server.
    pub fn cancelled(mut self) -> Self {
        self.options.cancelled = true;
        self
    }

    /// Return the execute controls associated with this query.
    #[must_use]
    pub fn options(&self) -> SqlExecuteOptions {
        self.options
    }

    /// Validate a query before handing it to a transport.
    pub fn validate(&self) -> Result<()> {
        if let Some(message) = &self.validation_error {
            return Err(Error::new(ErrorKind::Validation, "invalid_sql", message));
        }
        validate_parameters(&self.sql, &self.parameters)
    }

    /// Whether this query was rejected by the compatibility constructor.
    /// Prefer [`Query::try_raw`] when the caller needs the validation error.
    #[must_use]
    pub fn is_err(&self) -> bool {
        self.validation_error.is_some()
    }
}

fn normalize_parameters(input: QueryParameters) -> Result<SqlParameters> {
    match input {
        QueryParameters::Positional(values) => Ok(values
            .into_iter()
            .enumerate()
            .map(|(index, value)| ((index + 1).to_string(), value))
            .collect()),
        QueryParameters::Named(values) => Ok(values),
        QueryParameters::Invalid(message) => Err(sql_error(message)),
    }
}

fn canonical_parameters(input: &QueryParameters) -> SqlParameters {
    match input {
        QueryParameters::Positional(values) => values
            .iter()
            .cloned()
            .enumerate()
            .map(|(index, value)| ((index + 1).to_string(), value))
            .collect(),
        QueryParameters::Named(values) => values.clone(),
        QueryParameters::Invalid(_) => SqlParameters::new(),
    }
}

fn compatibility_params(input: &QueryParameters) -> Vec<serde_json::Value> {
    match input {
        QueryParameters::Positional(values) => values.clone(),
        QueryParameters::Named(values) => values.values().cloned().collect(),
        QueryParameters::Invalid(_) => Vec::new(),
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
    named_params: SqlParameters,
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
            named_params: SqlParameters::new(),
        }
    }

    /// Start a query whose collection and field descriptors come from a
    /// declared schema.
    pub fn from_schema(schema: &CollectionSchema) -> Result<Self> {
        let collection = validate_identifier(&schema.name, "collection")?;
        let mut names = BTreeSet::new();
        for field in &schema.fields {
            validate_identifier(&field.name, "field")?;
            if is_metadata_field(&field.name) || !names.insert(field.name.as_str()) {
                return Err(Error::new(
                    ErrorKind::Validation,
                    "invalid_schema_field",
                    "Schema fields must be unique plain identifiers and cannot replace metadata fields.",
                ));
            }
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
            named_params: SqlParameters::new(),
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

    /// Filter the JSON document body using SQLite's `->>` operator.
    pub fn where_json(mut self, path: &str, operator: &str, value: impl Serialize) -> Result<Self> {
        if !is_safe_json_path(path)
            || !matches!(operator, "=" | "<>" | "!=" | ">" | ">=" | "<" | "<=")
        {
            return Err(sql_error(
                "JSON paths or comparison operators are not allowed.",
            ));
        }
        let value = serialize_param(value)?;
        self.predicates.push(format!(
            "body ->> '{path}' {operator} ${}",
            self.params.len() + 1
        ));
        self.params.push(value);
        Ok(self)
    }

    /// Equality convenience for a JSON document path.
    pub fn where_json_eq(self, path: &str, value: impl Serialize) -> Result<Self> {
        self.where_json(path, "=", value)
    }

    /// Alias using the explicit path spelling used by other SDKs.
    pub fn where_json_path(
        self,
        path: &str,
        operator: &str,
        value: impl Serialize,
    ) -> Result<Self> {
        self.where_json(path, operator, value)
    }

    /// Append an `ORDER BY` key from a declared field.
    pub fn order_by_field(self, field: &FieldDef, direction: Direction) -> Result<Self> {
        let name = self.checked_field(field, "order_by")?;
        self.order_by(&name, direction)
    }

    fn checked_field(&self, field: &FieldDef, context: &str) -> Result<String> {
        let name = validate_identifier(&field.name, context)?;
        if is_metadata_field(&name) {
            let expected = metadata_field(&name)?;
            if field.field_type != expected.field_type {
                return Err(Error::new(
                    ErrorKind::Validation,
                    "invalid_metadata_field",
                    format!("{context} metadata field has the wrong type."),
                ));
            }
            return Ok(name);
        }
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
        let column = validate_identifier(column, "order_by")?;
        if self.schema_fields.is_some() {
            self.checked_field_name(&column, "order_by")?;
        }
        self.order.push((column, direction));
        Ok(self)
    }

    fn checked_field_name(&self, name: &str, context: &str) -> Result<String> {
        let name = validate_identifier(name, context)?;
        if is_metadata_field(&name) {
            return Ok(name);
        }
        match &self.schema_fields {
            Some(fields) if fields.iter().any(|field| field.name == name) => Ok(name),
            Some(_) => Err(Error::new(
                ErrorKind::Validation,
                "unknown_schema_field",
                format!("{context} field is not declared by this collection."),
            )),
            None => Ok(name),
        }
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

    /// Bind a named SQL parameter for a compatibility `where_` predicate.
    pub fn bind(mut self, name: &str, value: serde_json::Value) -> Result<Self> {
        if !is_valid_parameter_name(name) {
            return Err(sql_error("Named SQL parameters must be plain identifiers."));
        }
        self.named_params.insert(name.to_owned(), value);
        Ok(self)
    }

    /// Render the parameterized statement.
    pub fn build(self) -> Result<Query> {
        let from = self.from.as_ref().ok_or_else(|| {
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
            if self.schema_fields.is_some() && !predicate.trim_start().starts_with("body ->> '") {
                let field = tokenize_sql(predicate)?
                    .first()
                    .cloned()
                    .unwrap_or_default();
                self.checked_field_name(&field, "predicate")?;
            }
        }
        if self
            .limit
            .is_some_and(|limit| limit == 0 || limit > MAX_SQL_PAGE_LIMIT)
        {
            return Err(sql_error("LIMIT must be between 1 and 1024."));
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
        if self.named_params.is_empty() {
            Query::try_raw(sql, params)
        } else {
            let mut bindings: SqlParameters = params
                .into_iter()
                .enumerate()
                .map(|(index, value)| ((index + 1).to_string(), value))
                .collect();
            bindings.extend(self.named_params);
            Query::try_raw(sql, bindings)
        }
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
    if let Some(rest) = value.trim().strip_prefix("body ->> '") {
        if let Some((path, rest)) = rest.split_once("' ") {
            let mut parts = rest.split_whitespace();
            if is_safe_json_path(path)
                && matches!(
                    parts.next(),
                    Some("=" | "<>" | "!=" | ">" | ">=" | "<" | "<=")
                )
                && parts.next().is_some_and(|part| part.starts_with('$'))
                && parts.next().is_none()
            {
                return Ok(());
            }
        }
    }
    let tokens = tokenize_sql(value)?;
    if tokens.len() != 3 {
        return Err(sql_error(
            "Use a schema field predicate method instead of a free-form SQL fragment.",
        ));
    }
    if validate_identifier(&tokens[0], "predicate field").is_err()
        || !matches!(
            tokens[1].to_ascii_uppercase().as_str(),
            "=" | "<>" | "!=" | ">" | ">=" | "<" | "<=" | "MATCH" | "LIKE"
        )
        || !matches!(placeholder_name(&tokens[2], &mut 1usize), Ok(Some(_)))
    {
        return Err(sql_error(
            "Use a schema field predicate method instead of a free-form SQL fragment.",
        ));
    }
    Ok(())
}

fn is_safe_json_path(path: &str) -> bool {
    let bytes = path.as_bytes();
    if bytes.first() != Some(&b'$') {
        return false;
    }
    let mut index = 1;
    while index < bytes.len() {
        if bytes[index] == b'.' {
            index += 1;
            let start = index;
            while index < bytes.len()
                && (bytes[index].is_ascii_alphanumeric() || bytes[index] == b'_')
            {
                index += 1;
            }
            if start == index || !bytes[start].is_ascii_alphabetic() && bytes[start] != b'_' {
                return false;
            }
        } else if bytes[index] == b'[' {
            index += 1;
            let start = index;
            while index < bytes.len() && bytes[index].is_ascii_digit() {
                index += 1;
            }
            if start == index || bytes.get(index) != Some(&b']') {
                return false;
            }
            index += 1;
        } else {
            return false;
        }
    }
    true
}

/// Validate the client-side portion of the D-54 SQL contract.
///
/// The member performs the authoritative SQLite AST validation. The SDK
/// still rejects obvious unsafe input before transport, including comments,
/// multiple statements, write/DDL keywords, dangerous functions, and
/// malformed positional bindings.
pub fn validate_sql(text: &str, params: &[serde_json::Value]) -> Result<()> {
    let parameters: SqlParameters = params
        .iter()
        .cloned()
        .enumerate()
        .map(|(index, value)| ((index + 1).to_string(), value))
        .collect();
    validate_parameters(text, &parameters)
}

/// Validate SQL against canonical named/ordinal bindings.
pub fn validate_parameters(text: &str, parameters: &SqlParameters) -> Result<()> {
    if text.len() > MAX_SQL_TEXT_BYTES {
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
        if token == "."
            && position > 0
            && position + 1 < tokens.len()
            && is_sql_name(&tokens[position - 1])
            && is_sql_name(&tokens[position + 1])
        {
            return Err(sql_error(
                "Qualified SQL relations are not allowed; query one collection at a time.",
            ));
        }
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

    validate_bindings(&tokens, parameters)
}

fn is_sql_name(token: &str) -> bool {
    let mut chars = token.chars();
    matches!(chars.next(), Some(ch) if ch.is_ascii_alphabetic() || ch == '_')
        && chars.all(|ch| ch.is_ascii_alphanumeric() || ch == '_')
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
                while index < bytes.len()
                    && (bytes[index].is_ascii_alphanumeric() || bytes[index] == b'_')
                {
                    index += 1;
                }
                if index == start + 1 {
                    return Err(sql_error("SQL parameter names cannot be empty."));
                }
                tokens.push(text[start..index].to_owned());
            }
            b':' | b'@' => {
                let start = index;
                index += 1;
                while index < bytes.len()
                    && (bytes[index].is_ascii_alphanumeric() || bytes[index] == b'_')
                {
                    index += 1;
                }
                if index == start + 1 {
                    return Err(sql_error("Named SQL parameters must have a name."));
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

fn validate_bindings(tokens: &[String], parameters: &SqlParameters) -> Result<()> {
    let mut next_unnumbered = 1usize;
    let mut seen = BTreeSet::new();
    for token in tokens {
        let Some(binding) = placeholder_name(token, &mut next_unnumbered)? else {
            continue;
        };
        seen.insert(binding);
    }
    if seen.len() != parameters.len() || seen.iter().any(|name| !parameters.contains_key(name)) {
        let missing = seen.iter().find(|name| !parameters.contains_key(*name));
        if let Some(name) = missing {
            return Err(sql_error(format!(
                "SQL parameter '{name}' has no bound value."
            )));
        }
        let unexpected = parameters.keys().find(|name| !seen.contains(name.as_str()));
        if let Some(name) = unexpected {
            return Err(sql_error(format!(
                "SQL parameter '{name}' is not used by the statement."
            )));
        }
        return Err(sql_error(format!(
            "SQL has {} parameter slots but {} values were supplied.",
            seen.len(),
            parameters.len()
        )));
    }
    validate_parameter_bounds(tokens, parameters)?;
    Ok(())
}

fn placeholder_name(token: &str, next_unnumbered: &mut usize) -> Result<Option<String>> {
    let (prefix, name) = if token == "?" {
        let name = next_unnumbered.to_string();
        *next_unnumbered = next_unnumbered.saturating_add(1);
        return Ok(Some(name));
    } else {
        let Some(prefix) = token.chars().next() else {
            return Ok(None);
        };
        if !matches!(prefix, '$' | ':' | '@' | '?') {
            return Ok(None);
        }
        (prefix, &token[prefix.len_utf8()..])
    };
    if name.is_empty() {
        return Err(sql_error("SQL parameter names cannot be empty."));
    }
    if name.chars().all(|ch| ch.is_ascii_digit()) {
        let number = name
            .parse::<usize>()
            .map_err(|_| sql_error("Invalid ordinal SQL parameter."))?;
        if number == 0 {
            return Err(sql_error("Parameter numbering starts at 1."));
        }
        *next_unnumbered = (*next_unnumbered).max(number.saturating_add(1));
        return Ok(Some(number.to_string()));
    }
    if prefix == '?' || !is_valid_parameter_name(name) {
        return Err(sql_error("Named SQL parameters must be plain identifiers."));
    }
    Ok(Some(name.to_owned()))
}

fn is_valid_parameter_name(name: &str) -> bool {
    let mut bytes = name.bytes();
    matches!(bytes.next(), Some(byte) if byte.is_ascii_alphabetic() || byte == b'_')
        && bytes.all(|byte| byte.is_ascii_alphanumeric() || byte == b'_')
}

fn validate_parameter_bounds(tokens: &[String], parameters: &SqlParameters) -> Result<()> {
    for (index, token) in tokens.iter().enumerate() {
        if !token.eq_ignore_ascii_case("limit") && !token.eq_ignore_ascii_case("offset") {
            continue;
        }
        let Some(value) = tokens.get(index + 1) else {
            continue;
        };
        let number = if let Some(name) = placeholder_name(value, &mut 1usize)? {
            parameters
                .get(&name)
                .and_then(serde_json::Value::as_u64)
                .ok_or_else(|| {
                    sql_error("LIMIT and OFFSET parameters must be non-negative integers.")
                })?
        } else {
            value
                .parse::<u64>()
                .map_err(|_| sql_error("LIMIT and OFFSET must be non-negative integers."))?
        };
        if token.eq_ignore_ascii_case("limit") {
            if number == 0 || number > MAX_SQL_PAGE_LIMIT {
                return Err(sql_error("LIMIT must be between 1 and 1024."));
            }
        }
    }
    Ok(())
}

/// One row returned by [`SqlClient::execute`].
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct SqlRow {
    /// Stable document id.
    pub id: String,
    /// Either the complete document or the selected projection object.
    #[serde(default)]
    pub value: serde_json::Value,
}

/// Rows returned by [`SqlClient::execute`].
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct SqlRows {
    /// Number of rows matched before the page is applied.
    #[serde(default)]
    pub matched: u64,
    /// Canonical result rows (`{id, value}`).
    pub rows: Vec<SqlRow>,
    /// Legacy compatibility field; canonical responses do not send columns.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub columns: Vec<String>,
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
        query.options.validate()?;
        let url = format!("{}/sql", self.shared.database_root(scope)?);
        let mut body = serde_json::json!({
            "sql": query.sql(),
            "parameters": query.parameters(),
        });
        if query.options.explain {
            body["explain"] = serde_json::Value::Bool(true);
        }
        if let Some(timeout_ms) = query.options.timeout_ms {
            body["timeout_ms"] = serde_json::json!(timeout_ms);
        }
        if query.options.cancelled {
            body["cancelled"] = serde_json::Value::Bool(true);
        }
        let request = Request::json("sql.execute", Method::Post, url, &body)?;
        self.shared.send_typed(request).await
    }

    /// Execute a query with one-off HTTP controls.
    pub async fn execute_with_options(
        &self,
        scope: &DatabaseScope,
        query: &Query,
        options: SqlExecuteOptions,
    ) -> Result<SqlRows> {
        let query = query.clone().with_options(options)?;
        self.execute(scope, &query).await
    }
}
