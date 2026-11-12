use super::{Database, Result};
use reqwest::{Method, StatusCode};
use serde::Deserialize;

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DatabaseRegions {
    pub database_id: String,
    pub observed_at: String,
    pub regions: Vec<ReplicaStatus>,
}
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ReplicaStatus {
    pub region: String,
    #[serde(default)]
    pub role: Option<String>,
    pub lag_ms: Option<f64>,
    pub last_pulled_at: Option<String>,
    pub last_verified: Option<String>,
    pub revision: Option<String>,
}
impl Database {
    pub async fn get_database_regions(&self) -> Result<DatabaseRegions> {
        self.management(Method::GET, &["regions"], &[], None::<&()>, StatusCode::OK)
            .await
    }
}
