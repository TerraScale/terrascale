//! Public serving regions and project Prometheus exposition.
use super::{client::Req, Client, Result};
use reqwest::{Method, StatusCode};
use serde::Deserialize;

/// Live serving region IDs, in server order; discovery is cacheable for five seconds.
#[derive(Debug, Clone, Deserialize)]
pub struct ServingRegions {
    pub regions: Vec<String>,
}
impl Client {
    /// `GET /v1/regions`; public discovery, without a bearer or project prefix.
    pub async fn list_regions(&self) -> Result<ServingRegions> {
        self.probe(vec!["v1", "regions"])
            .await?
            .decode_status(&[StatusCode::OK])
    }
    /// `GET P/metrics`; Prometheus 0.0.4 text, requiring databases:read.
    pub async fn get_project_metrics(&self) -> Result<String> {
        let response = self
            .send_project(Req::new(Method::GET, vec!["v1", "metrics"]))
            .await?;
        response.check_status(&[StatusCode::OK])?;
        std::str::from_utf8(&response.body)
            .map(str::to_owned)
            .map_err(|_| response.invalid("metrics must be UTF-8 text".into()))
    }
}
