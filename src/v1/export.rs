//! Complete bounded NDJSON export reads, preserving raw document numbers.
use super::{
    client::Req,
    ids::{check_database_selector, check_name},
    Database, DocumentId, Result,
};
use reqwest::{Method, StatusCode};
use serde::{de::DeserializeOwned, Deserialize};
use serde_json::value::RawValue;

#[derive(Debug, Deserialize)]
pub struct ExportDocument<T = Box<RawValue>> {
    pub collection: String,
    #[serde(deserialize_with = "super::ids::document_id")]
    pub id: DocumentId,
    pub document: T,
}
impl Database {
    /// Reads one complete export within the existing response-size bound.
    /// Resume after the last fully received (collection, id); a resumed request
    /// reads a new snapshot. No partial result escapes a truncated response.
    pub async fn export_database<T: DeserializeOwned>(
        &self,
        after: Option<(&str, DocumentId)>,
    ) -> Result<Vec<ExportDocument<T>>> {
        check_database_selector(self.name())?;
        let mut req = Req::new(Method::GET, vec!["v1", "databases", self.name(), "export"]);
        if let Some((collection, id)) = after {
            check_name("collection", collection)?;
            req.query = vec![
                ("after_collection", collection.to_owned()),
                ("after_id", id.to_string()),
            ];
        }
        let response = self.client().send_project(req).await?;
        response.check_status(&[StatusCode::OK])?;
        if response.body.is_empty() {
            return Ok(Vec::new());
        }
        if !response.body.ends_with(b"\n") {
            return Err(response.invalid("export did not end with a complete NDJSON line".into()));
        }
        response.body[..response.body.len() - 1]
            .split(|b| *b == b'\n')
            .map(|line| {
                serde_json::from_slice(line)
                    .map_err(|_| response.invalid("invalid export NDJSON line".into()))
            })
            .collect()
    }
}
