//! Customer JavaScript functions: deployment, invocation and execution history.
use reqwest::{Method, StatusCode};
use serde::{de::DeserializeOwned, Deserialize, Serialize};

use super::{
    client::Req,
    errors::Error,
    ids::{check_database_selector, check_name},
    Database, Result,
};

/// JavaScript defining `handler(event)`, with an optional UTC cron.
#[derive(Debug, Clone, Serialize)]
pub struct FunctionDeployment {
    pub source: String,
    pub cron: Option<String>,
}

/// A deployed function. Lists omit the source.
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Function {
    pub name: String,
    pub source: Option<String>,
    pub cron: Option<String>,
    pub created_at: String,
    pub updated_at: String,
}

/// One HTTP or cron invocation's bounded console output.
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct FunctionLog {
    pub id: String,
    pub at: String,
    pub trigger: String,
    pub status: String,
    #[serde(deserialize_with = "duration_ms")]
    pub duration_ms: u64,
    pub lines: Vec<String>,
    pub error: Option<String>,
    #[serde(default)]
    pub sql_calls: u64,
    #[serde(default)]
    pub sql_ms: u64,
    #[serde(default)]
    pub rows_read: u64,
    #[serde(default)]
    pub rows_written: u64,
    #[serde(default)]
    pub response_bytes: u64,
    #[serde(default)]
    pub http_status: Option<u16>,
}

/// One UTC stats interval, including empty intervals.
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct FunctionStatsBucket {
    pub at: String,
    pub invocations: u64,
    pub errors: u64,
    pub p50_ms: u64,
    pub p95_ms: u64,
}

/// A function's aggregated invocation metrics over a window.
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct FunctionStats {
    pub window: String,
    pub invocations: u64,
    pub errors: u64,
    pub p50_ms: u64,
    pub p95_ms: u64,
    pub p99_ms: u64,
    pub max_ms: u64,
    pub sql_calls: u64,
    pub rows_read: u64,
    pub rows_written: u64,
    pub buckets: Vec<FunctionStatsBucket>,
}

/// A successful invocation, including a handler's custom status, headers and body.
pub struct FunctionInvocation {
    pub status: u16,
    pub headers: reqwest::header::HeaderMap,
    pub body: Vec<u8>,
}

fn duration_ms<'de, D: serde::Deserializer<'de>>(d: D) -> std::result::Result<u64, D::Error> {
    #[derive(Deserialize)]
    #[serde(untagged)]
    enum Wire {
        Integer(u64),
        Decimal(String),
    }
    match Wire::deserialize(d)? {
        Wire::Integer(n) => Ok(n),
        Wire::Decimal(s) => super::types::u64_string::parse_canonical_u64(&s).ok_or_else(|| {
            serde::de::Error::custom("durationMs must be a nonnegative decimal integer")
        }),
    }
}

impl Database {
    /// Lists functions without their source code.
    pub async fn list_functions(&self) -> Result<Vec<Function>> {
        self.management(
            Method::GET,
            &["functions"],
            &[],
            None::<&()>,
            StatusCode::OK,
        )
        .await
    }

    /// Returns one function including its source code.
    pub async fn get_function(&self, name: &str) -> Result<Function> {
        check_name("function", name)?;
        self.management(
            Method::GET,
            &["functions", name],
            &[],
            None::<&()>,
            StatusCode::OK,
        )
        .await
    }

    /// Deploys or replaces a function. The server validates source and cron.
    pub async fn deploy_function(&self, name: &str, body: &FunctionDeployment) -> Result<Function> {
        check_name("function", name)?;
        self.management(
            Method::PUT,
            &["functions", name],
            &[],
            Some(body),
            StatusCode::OK,
        )
        .await
    }

    /// Deletes a function and stops its schedule.
    pub async fn delete_function(&self, name: &str) -> Result<()> {
        check_database_selector(self.name())?;
        check_name("function", name)?;
        self.client()
            .send_project(Req::new(
                Method::DELETE,
                vec!["v1", "databases", self.name(), "functions", name],
            ))
            .await?
            .check_status(&[StatusCode::NO_CONTENT])
    }

    /// Invokes a function with an arbitrary JSON body. An uncertain invocation is never replayed.
    pub async fn invoke_function<T: DeserializeOwned>(
        &self,
        name: &str,
        body: &impl Serialize,
    ) -> Result<T> {
        check_name("function", name)?;
        let response = self.function_response(name, body).await?;
        let status = response.status;
        response.decode_status(&[status])
    }

    /// Invokes a handler returning text, an empty body or a custom successful status.
    pub async fn invoke_function_response(
        &self,
        name: &str,
        body: &impl Serialize,
    ) -> Result<FunctionInvocation> {
        let response = self.function_response(name, body).await?;
        Ok(FunctionInvocation {
            status: response.status.as_u16(),
            headers: response.headers,
            body: response.body,
        })
    }

    async fn function_response(
        &self,
        name: &str,
        body: &impl Serialize,
    ) -> Result<super::client::Resp> {
        check_database_selector(self.name())?;
        check_name("function", name)?;
        self.client()
            .send_project(
                Req::new(
                    Method::POST,
                    vec!["v1", "databases", self.name(), "functions", name, "invoke"],
                )
                .json(body)?,
            )
            .await
    }

    /// Returns recent invocation logs.
    pub async fn list_function_logs(&self, name: &str) -> Result<Vec<FunctionLog>> {
        check_name("function", name)?;
        self.management(
            Method::GET,
            &["functions", name, "logs"],
            &[],
            None::<&()>,
            StatusCode::OK,
        )
        .await
    }

    /// Returns aggregated invocation metrics. The window is `1h`, `24h` or `7d`;
    /// `None` uses the server default.
    pub async fn get_function_stats(
        &self,
        name: &str,
        window: Option<&str>,
    ) -> Result<FunctionStats> {
        check_name("function", name)?;
        if let Some(window) = window {
            if !matches!(window, "1h" | "24h" | "7d") {
                return Err(Error::InvalidInput("window must be 1h, 24h or 7d".into()));
            }
        }
        let query = window
            .map(|window| vec![("window", window.to_string())])
            .unwrap_or_default();
        self.management(
            Method::GET,
            &["functions", name, "stats"],
            &query,
            None::<&()>,
            StatusCode::OK,
        )
        .await
    }
}
