//! Public anonymous health probes and platform-admin exploration.
use super::{
    client::{Client, Req},
    errors::Result,
};
use reqwest::{Method, StatusCode};
use serde::de::DeserializeOwned;
use serde_json::Value;

/// Probe status and body, including a normal readiness/backup 503 response.
#[derive(Debug, Clone)]
pub struct HealthResponse {
    pub status: u16,
    pub body: Value,
}
impl Client {
    async fn health(&self, path: Vec<&str>) -> Result<HealthResponse> {
        let response = self.probe(path).await?;
        response.check_status(&[StatusCode::OK, StatusCode::SERVICE_UNAVAILABLE])?;
        Ok(HealthResponse {
            status: response.status.as_u16(),
            body: response.decode()?,
        })
    }
    /// `GET /healthz`; sends no credential.
    pub async fn healthz(&self) -> Result<HealthResponse> {
        self.health(vec!["healthz"]).await
    }
    /// `GET /readyz`; retains the unavailable body and HTTP 503.
    pub async fn readyz(&self) -> Result<HealthResponse> {
        self.health(vec!["readyz"]).await
    }
    /// `GET /backupz`; retains the unavailable body and HTTP 503.
    pub async fn backup_health(&self) -> Result<HealthResponse> {
        self.health(vec!["backupz"]).await
    }
    /// `GET /v1/platform/databases`; requires a platform-admin user token.
    pub async fn list_platform_databases<T: DeserializeOwned>(&self) -> Result<T> {
        self.send(Req::new(Method::GET, vec!["v1", "platform", "databases"]))
            .await?
            .decode_status(&[StatusCode::OK])
    }
}
