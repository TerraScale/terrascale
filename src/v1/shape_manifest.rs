//! Origin-issued mirror manifests. URLs are opaque private capabilities.
use super::{
    errors::from_project_response, ids::valid_entity_id, Collection, Error, Filter, Result,
};
use base64::{engine::general_purpose::URL_SAFE_NO_PAD, Engine};
use reqwest::StatusCode;
use serde::Deserialize;
use std::fmt;

#[derive(Debug, Clone, Deserialize)]
pub struct ShapeMirrorManifest {
    pub handle: String,
    pub objects: Vec<ShapeMirrorObject>,
}
#[derive(Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ShapeMirrorObject {
    pub offset: String,
    pub next_offset: String,
    pub binding: String,
    pub object: String,
    pub url: String,
    pub expires_at: String,
    pub epoch: String,
    pub key_id: String,
    pub sha256: String,
    #[serde(deserialize_with = "size")]
    pub size: u64,
}
impl fmt::Debug for ShapeMirrorObject {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("ShapeMirrorObject")
            .field("url", &"<redacted>")
            .finish_non_exhaustive()
    }
}
fn size<'de, D: serde::Deserializer<'de>>(d: D) -> std::result::Result<u64, D::Error> {
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
        .ok_or_else(|| serde::de::Error::custom("mirror size must be a nonnegative int64"))
}
fn decimal(s: &str) -> bool {
    super::types::u64_string::parse_canonical_u64(s).is_some_and(|n| n <= i64::MAX as u64)
}
fn offset(s: &str) -> bool {
    if let Some(rest) = s.strip_prefix("snap.") {
        let Some((seq, page)) = rest.split_once('.') else {
            return false;
        };
        decimal(seq)
            && (page == "0"
                || URL_SAFE_NO_PAD
                    .decode(page)
                    .ok()
                    .filter(|b| URL_SAFE_NO_PAD.encode(b) == page)
                    .and_then(|b| String::from_utf8(b).ok())
                    .is_some_and(|s| valid_entity_id(&s)))
    } else {
        decimal(s)
    }
}
fn private_error(error: Error) -> Error {
    if matches!(error, Error::TokenExchange { .. } | Error::InvalidInput(_)) {
        return error;
    }
    let Some(status) = error.status() else {
        return Error::Transport("manifest request failed".into());
    };
    let code = error
        .code()
        .filter(|s| {
            matches!(
                *s,
                "must_refetch"
                    | "invalid_request"
                    | "invalid_key"
                    | "forbidden_scope"
                    | "not_found"
                    | "unknown_database"
                    | "conflict"
                    | "tenant_busy"
                    | "tenant_memory"
                    | "storage_busy"
                    | "capacity"
                    | "unavailable"
                    | "misdirected_request"
                    | "wrong_region"
            )
        })
        .unwrap_or("shape_manifest_error");
    from_project_response(
        status,
        None,
        None,
        error.request_id().filter(|s| valid_entity_id(s)),
        serde_json::json!({"error":{"code":code}})
            .to_string()
            .as_bytes(),
    )
}
impl Collection {
    /// Lists up to eight sealed objects. Never fetches a capability or applies data.
    /// Use the origin shape stream on an empty manifest, must_refetch or failed verification.
    pub async fn get_shape_manifest(
        &self,
        handle: &str,
        position: &str,
        filter: Option<&Filter>,
    ) -> Result<Option<ShapeMirrorManifest>> {
        self.check()?;
        if !handle.strip_prefix("h1-").is_some_and(|s| {
            s.len() == 16 && s.bytes().all(|b| matches!(b, b'0'..=b'9' | b'a'..=b'f'))
        }) || !offset(position)
        {
            return Err(Error::InvalidInput(
                "manifest requires a canonical handle and published shape offset".into(),
            ));
        }
        let mut query = vec![
            ("handle", handle.to_owned()),
            ("offset", position.to_owned()),
        ];
        if let Some(filter) = filter {
            filter.validate()?;
            let text = serde_json::to_string(filter)
                .map_err(|_| Error::InvalidInput("invalid manifest filter".into()))?;
            if text.len() > 8192 {
                return Err(Error::InvalidInput("manifest filter exceeds 8 KiB".into()));
            }
            query.push(("where", text));
        }
        let db = self.database();
        let client = db.client();
        let (base, region) = client.shape_endpoint().await?;
        let url = client.shape_url(&base, db.name(), self.name(), Some("manifest"), &query)?;
        let response = client
            .shape_get(
                &url,
                region.as_deref(),
                &[("Cache-Control", "no-store".into())],
            )
            .await
            .map_err(private_error)?;
        let manifest: Option<ShapeMirrorManifest> = response
            .decode_status(&[StatusCode::OK])
            .map_err(private_error)?;
        if manifest
            .as_ref()
            .is_some_and(|m| m.handle != handle || m.objects.len() > 8)
        {
            return Err(private_error(
                response.invalid("manifest identity or object count is invalid".into()),
            ));
        }
        Ok(manifest)
    }
}
