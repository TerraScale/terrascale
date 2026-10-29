//! Scheduled exports to the project's configured backup destination.
use super::{Database, Result};
use reqwest::{Method, StatusCode};
use serde::Deserialize;

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ExportSchedule {
    #[serde(deserialize_with = "Option::deserialize")]
    pub cron: Option<String>,
    #[serde(deserialize_with = "Option::deserialize")]
    pub updated_at: Option<String>,
    #[serde(deserialize_with = "Option::deserialize")]
    pub last_run: Option<ExportRun>,
}
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ExportRun {
    pub started_at: String,
    pub finished_at: String,
    pub status: String,
    #[serde(deserialize_with = "Option::deserialize")]
    pub key: Option<String>,
    #[serde(deserialize_with = "nonnegative_int64")]
    pub documents: u64,
    #[serde(deserialize_with = "nonnegative_int64")]
    pub bytes: u64,
    #[serde(deserialize_with = "Option::deserialize")]
    pub error: Option<String>,
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
impl Database {
    pub async fn get_export_schedule(&self) -> Result<ExportSchedule> {
        self.management(
            Method::GET,
            &["export-schedule"],
            &[],
            None::<&()>,
            StatusCode::OK,
        )
        .await
    }
    /// Five-field UTC cron. Null or empty stops scheduled exports.
    /// A new schedule requires the project's backup destination (409 otherwise).
    pub async fn set_export_schedule(&self, cron: Option<&str>) -> Result<ExportSchedule> {
        self.management(
            Method::PUT,
            &["export-schedule"],
            &[],
            Some(&serde_json::json!({"cron":cron})),
            StatusCode::OK,
        )
        .await
    }
}
