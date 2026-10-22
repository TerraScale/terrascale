//! Public anonymous health probes and platform-admin exploration.
use super::{
    client::{Client, Req},
    errors::Result,
    ids::{check_name, DocumentId},
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
    /// `GET /health/replication`; sends no credential.
    pub async fn replication_health(&self) -> Result<HealthResponse> {
        self.health(vec!["health", "replication"]).await
    }

    async fn platform<T: DeserializeOwned>(
        &self,
        suffix: &[&str],
        query: &[(&str, String)],
    ) -> Result<T> {
        let mut segments = vec!["v1", "platform", "databases"];
        segments.extend_from_slice(suffix);
        let mut req = Req::new(Method::GET, segments);
        req.query = query.to_vec();
        self.send(req).await?.decode_status(&[StatusCode::OK])
    }
    /// `GET /v1/platform/databases`; requires a platform-admin user token.
    pub async fn list_platform_databases<T: DeserializeOwned>(&self) -> Result<T> {
        self.platform(&[], &[]).await
    }
    /// `GET /v1/platform/databases/{database}/collections`; selector is a name.
    pub async fn list_platform_collections<T: DeserializeOwned>(
        &self,
        database: &str,
        query: &[(&str, String)],
    ) -> Result<T> {
        check_name("database", database)?;
        self.platform(&[database, "collections"], query).await
    }
    /// `GET /v1/platform/databases/{database}/collections/{collection}/documents`.
    pub async fn list_platform_documents<T: DeserializeOwned>(
        &self,
        database: &str,
        collection: &str,
        query: &[(&str, String)],
    ) -> Result<T> {
        check_name("database", database)?;
        check_name("collection", collection)?;
        self.platform(&[database, "collections", collection, "documents"], query)
            .await
    }
    /// `GET .../documents/by-key?id=`; raw keys stay once-encoded; server redacts values.
    pub async fn get_platform_document<T: DeserializeOwned>(
        &self,
        database: &str,
        collection: &str,
        id: DocumentId,
    ) -> Result<T> {
        check_name("database", database)?;
        check_name("collection", collection)?;
        self.platform(
            &[database, "collections", collection, "documents", "by-key"],
            &[("id", id.to_string())],
        )
        .await
    }
}
