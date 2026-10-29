//! Observational schema checks; preview never installs a schema.
use super::{client::Req, Collection, Error, Result};
use reqwest::{Method, StatusCode};
use serde::Deserialize;

#[derive(Debug, Clone, Deserialize)]
pub struct CollectionSchemaPreview {
    pub schema_hash: String,
    pub frontier: String,
    #[serde(deserialize_with = "page_count")]
    pub examined: u32,
    #[serde(deserialize_with = "page_count")]
    pub valid: u32,
    #[serde(deserialize_with = "page_count")]
    pub invalid: u32,
    pub complete: bool,
    #[serde(deserialize_with = "Option::deserialize")]
    pub next_cursor: Option<String>,
    pub diagnostics: Vec<SchemaPreviewDiagnostic>,
}
#[derive(Debug, Clone, Deserialize)]
pub struct SchemaPreviewDiagnostic {
    pub id_hash: String,
    pub path_hash: String,
    pub keyword: String,
}
fn page_count<'de, D: serde::Deserializer<'de>>(d: D) -> std::result::Result<u32, D::Error> {
    #[derive(Deserialize)]
    #[serde(untagged)]
    enum Wire {
        Integer(u32),
        Decimal(String),
    }
    let n = match Wire::deserialize(d)? {
        Wire::Integer(n) => Some(n as u64),
        Wire::Decimal(s) => super::types::u64_string::parse_canonical_u64(&s),
    };
    n.filter(|n| *n <= i32::MAX as u64)
        .map(|n| n as u32)
        .ok_or_else(|| serde::de::Error::custom("expected a nonnegative int32 count"))
}
impl Collection {
    /// Sends identical raw JSON schema bytes on each continuation. Requires both
    /// documents:read and documents:write. A stale frontier requires restarting.
    pub async fn preview_schema(
        &self,
        schema: &[u8],
        cursor: Option<&str>,
    ) -> Result<CollectionSchemaPreview> {
        self.check()?;
        if schema.is_empty()
            || schema.len() > 64 << 10
            || cursor.is_some_and(|s| s.is_empty() || s.len() > 2048)
        {
            return Err(Error::InvalidInput(
                "schema preview requires 1–65536 JSON bytes and a nonempty cursor up to 2048 bytes"
                    .into(),
            ));
        }
        serde_json::from_slice::<Box<serde_json::value::RawValue>>(schema)
            .map_err(|_| Error::InvalidInput("schema preview requires valid JSON".into()))?;
        let db = self.database();
        let mut req = Req::new(
            Method::POST,
            vec![
                "v1",
                "databases",
                db.name(),
                "collections",
                self.name(),
                "schema",
                "preview",
            ],
        );
        req.body = Some(schema.to_vec());
        if let Some(cursor) = cursor {
            req.query.push(("cursor", cursor.to_owned()));
        }
        // Cursor identity is process-local. Do not retry a preview automatically.
        let mut response = db.client().send_project(req).await?;
        response.mutation = false;
        response.decode_status(&[StatusCode::OK])
    }
}
