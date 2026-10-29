//! Best-effort tenant-scoped recent request observations.
use super::{Database, Error, Number, Result};
use reqwest::{Method, StatusCode};
use serde::{Deserialize, Deserializer};

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DatabaseRequestLogs {
    pub database_id: String,
    pub items: Vec<DatabaseRequestLog>,
    pub truncated: bool,
    pub recording_limited: bool,
}
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DatabaseRequestLog {
    pub id: String,
    pub time: String,
    pub region: String,
    pub method: String,
    pub route: String,
    #[serde(deserialize_with = "status_code")]
    pub status: u16,
    #[serde(deserialize_with = "latency")]
    pub latency_ms: Number,
    #[serde(deserialize_with = "nonnegative_int64")]
    pub operations: u64,
    pub is_query: bool,
    pub request_id: Option<String>,
}
fn nonnegative_int64<'de, D: serde::Deserializer<'de>>(d: D) -> std::result::Result<u64, D::Error> {
    #[derive(Deserialize)]
    #[serde(untagged)]
    enum Wire {
        Integer(u64),
        Decimal(String),
    }
    let n = match Wire::deserialize(d)? {
        Wire::Integer(n) => Some(n),
        Wire::Decimal(s) => super::types::u64_string::parse_canonical_u64(&s),
    };
    n.filter(|n| *n <= i64::MAX as u64)
        .ok_or_else(|| serde::de::Error::custom("expected a nonnegative int64"))
}
fn status_code<'de, D: Deserializer<'de>>(d: D) -> std::result::Result<u16, D::Error> {
    let n = nonnegative_int64(d)?;
    if (100..=599).contains(&n) {
        Ok(n as u16)
    } else {
        Err(serde::de::Error::custom("expected an HTTP status"))
    }
}
fn latency<'de, D: Deserializer<'de>>(d: D) -> std::result::Result<Number, D::Error> {
    let raw = Box::<serde_json::value::RawValue>::deserialize(d)?;
    let text = if raw.get().starts_with('"') {
        serde_json::from_str::<String>(raw.get()).map_err(serde::de::Error::custom)?
    } else {
        raw.get().to_owned()
    };
    Number::new(text).map_err(|_| serde::de::Error::custom("expected a decimal latency"))
}
impl Database {
    /// Newest 100 observations within 24 hours, optionally filtered by status class.
    pub async fn list_database_logs(&self, status: Option<&str>) -> Result<DatabaseRequestLogs> {
        if status.is_some_and(|s| !matches!(s, "2xx" | "3xx" | "4xx" | "5xx")) {
            return Err(Error::InvalidInput(
                "status must be 2xx, 3xx, 4xx or 5xx".into(),
            ));
        }
        let query: Vec<_> = status
            .map(|s| ("status", s.to_owned()))
            .into_iter()
            .collect();
        self.management(Method::GET, &["logs"], &query, None::<&()>, StatusCode::OK)
            .await
    }
}
