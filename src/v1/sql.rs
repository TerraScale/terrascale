//! Project-scoped, read-only HTTP SQL and engine plans.

use reqwest::{Method, StatusCode};
use serde::de::DeserializeOwned;
use serde::{Deserialize, Serialize};
use serde_json::value::RawValue;

use super::client::{Req, Resp};
use super::documents::Database;
use super::errors::Result;
use super::ids::{check_database_selector, is_valid_name};

/// A document selected by SQL; `value` is the whole document or its projection.
#[derive(Debug, Serialize, Deserialize)]
pub struct SqlRow<T = Box<RawValue>> {
    pub id: String,
    pub value: T,
}

/// A bounded SQL result. `matched` counts returned rows, not all matching rows.
/// `truncated` means the SQL limit or the server's 8 MiB result bound was reached.
#[derive(Debug, Serialize, Deserialize)]
pub struct SqlResult<T = Box<RawValue>> {
    pub matched: u32,
    pub truncated: bool,
    pub rows: Vec<SqlRow<T>>,
}

/// An unindexed field used by the statement's filter or ordering.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct SqlIndexSuggestion {
    pub field: String,
    pub reason: String,
}

/// SQL validation and the engine plan, without executing the statement.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SqlCheck {
    pub ok: bool,
    pub error: Option<String>,
    pub collection: Option<String>,
    pub has_limit: bool,
    pub plan: Vec<String>,
    pub indexes: Vec<String>,
    pub suggestions: Vec<SqlIndexSuggestion>,
}

impl Database {
    /// Runs the server's read-only SELECT subset on this project database.
    /// The HTTP contract accepts a SQL string only; it has no parameters or cursor.
    pub async fn run_sql<T: DeserializeOwned>(&self, sql: &str) -> Result<SqlResult<T>> {
        let response = self.sql_request(sql, None).await?;
        let result: SqlResult<Box<RawValue>> = response.decode()?;
        if result.matched as usize != result.rows.len()
            || result.rows.len() > 1000
            || result
                .rows
                .iter()
                .any(|row| row.id.is_empty() || !row.value.get().trim_start().starts_with('{'))
        {
            return Err(response.invalid("SQL rows do not match the result contract".into()));
        }
        Ok(SqlResult {
            matched: result.matched,
            truncated: result.truncated,
            rows: result
                .rows
                .into_iter()
                .map(|row| {
                    Ok(SqlRow {
                        id: row.id,
                        value: serde_json::from_str(row.value.get())
                            .map_err(|error| response.invalid(error.to_string()))?,
                    })
                })
                .collect::<Result<_>>()?,
        })
    }

    /// Checks SQL without executing it. Unsupported SQL is an `ok: false`
    /// result; engine work-budget failures remain HTTP errors.
    pub async fn check_sql(&self, sql: &str) -> Result<SqlCheck> {
        self.sql_plan(sql, "check").await
    }

    /// Explains SQL without executing or metering a document read.
    /// Unsupported SQL is an HTTP 400 error on this route.
    pub async fn explain_sql(&self, sql: &str) -> Result<SqlCheck> {
        self.sql_plan(sql, "explain").await
    }

    async fn sql_plan(&self, sql: &str, operation: &str) -> Result<SqlCheck> {
        let response = self.sql_request(sql, Some(operation)).await?;
        let result: SqlCheck = response.decode()?;
        let valid = if result.ok {
            result.error.is_none() && result.collection.as_deref().is_some_and(is_valid_name)
        } else {
            operation == "check"
                && result
                    .error
                    .as_deref()
                    .is_some_and(|error| !error.is_empty())
                && result.collection.is_none()
                && !result.has_limit
                && result.plan.is_empty()
                && result.indexes.is_empty()
                && result.suggestions.is_empty()
        };
        if !valid {
            return Err(response.invalid("SQL check does not match the result contract".into()));
        }
        Ok(result)
    }

    async fn sql_request(&self, sql: &str, operation: Option<&str>) -> Result<Resp> {
        check_database_selector(self.name())?;
        let mut segments = vec!["v1", "databases", self.name(), "sql"];
        if let Some(operation) = operation {
            segments.push(operation);
        }
        let mut request =
            Req::new(Method::POST, segments).json(&serde_json::json!({"sql": sql}))?;
        request.replay_safe = true;
        let response = self.client().send_project(request).await?;
        response.check_status(&[StatusCode::OK])?;
        Ok(response)
    }
}
