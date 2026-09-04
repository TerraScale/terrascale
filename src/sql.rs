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
use crate::transport::{Method, Request};

/// Sort direction for `ORDER BY`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Direction {
    Asc,
    Desc,
}

/// A parameterized statement: SQL text with `$1`-style placeholders plus
/// separately bound parameters.
#[derive(Debug, Clone, PartialEq)]
pub struct Query {
    sql: String,
    params: Vec<serde_json::Value>,
}

impl Query {
    /// Build a raw parameterized statement. Placeholders are bound by
    /// position; values never touch the SQL text.
    #[must_use]
    pub fn raw(sql: impl Into<String>, params: Vec<serde_json::Value>) -> Self {
        Self {
            sql: sql.into(),
            params,
        }
    }

    /// Start a typed `SELECT <columns>` query.
    #[must_use]
    pub fn select(columns: &[&str]) -> SelectBuilder {
        SelectBuilder::new(columns)
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
}

/// Fluent builder for a live-subset `SELECT` query.
#[derive(Debug, Clone)]
pub struct SelectBuilder {
    columns: Vec<String>,
    from: Option<String>,
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
            predicates: Vec::new(),
            order: Vec::new(),
            limit: None,
            offset: None,
            params: Vec::new(),
        }
    }

    /// Collection to query.
    pub fn from(mut self, collection: &str) -> Result<Self> {
        self.from = Some(validate_identifier(collection, "collection")?);
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
            Error::new(ErrorKind::Validation, "missing_from", "A query requires .from(collection).")
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
        if let Some(limit) = self.limit {
            sql.push_str(&format!(" LIMIT {limit}"));
        }
        if let Some(offset) = self.offset {
            sql.push_str(&format!(" OFFSET {offset}"));
        }
        Ok(Query {
            sql,
            params: self.params,
        })
    }
}

fn validate_identifier(value: &str, field: &str) -> Result<String> {
    let valid = !value.is_empty()
        && value
            .chars()
            .all(|c| c.is_ascii_alphanumeric() || c == '_' || c == '.');
    if !valid {
        return Err(Error::new(
            ErrorKind::Validation,
            "invalid_identifier",
            format!("{field} must match [A-Za-z0-9_.]+."),
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
        let url = format!("{}/sql", self.shared.database_root(scope)?);
        let body = serde_json::json!({
            "statement": query.sql(),
            "parameters": query.params(),
        });
        let request = Request::json("sql.execute", Method::Post, url, &body)?;
        self.shared.send_typed(request).await
    }
}
