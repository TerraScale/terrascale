//! File reservations and metadata. Signed capabilities are private credentials.
use super::{
    client::Req, errors::from_project_response, ids::valid_entity_id, Client, Error, Result,
};
use base64::{engine::general_purpose::STANDARD, Engine};
use reqwest::{Method, StatusCode};
use serde::{Deserialize, Deserializer, Serialize};
use serde_json::Value;
use std::{collections::BTreeMap, fmt};

#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct FileUploadRequest {
    pub name: String,
    pub content_type: String,
    pub size: u64,
    pub sha256: String,
    pub metadata: Value,
}

/// Immutable customer-visible metadata; deletion queues physical cleanup.
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct FileObject {
    #[serde(deserialize_with = "super::ids::entity_id")]
    pub id: String,
    pub name: String,
    pub content_type: String,
    #[serde(deserialize_with = "file_size")]
    pub size: u64,
    pub sha256: String,
    #[serde(deserialize_with = "Value::deserialize")]
    pub metadata: Value,
    pub status: String,
    pub upload_expires_at: String,
    pub delete_after: Option<String>,
}

/// Up to 50 authorized files, ordered by ID with an exclusive continuation.
#[derive(Debug, Clone, Deserialize)]
pub struct FileObjectPage {
    pub items: Vec<FileObject>,
    #[serde(deserialize_with = "file_cursor")]
    pub next: Option<String>,
}

/// A short-lived opaque storage capability. Send its headers exactly; never log it.
#[derive(Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct FileSignedUrl {
    #[serde(deserialize_with = "super::ids::entity_id")]
    pub id: String,
    pub url: String,
    pub expires_at: String,
    pub headers: BTreeMap<String, String>,
}
impl fmt::Debug for FileSignedUrl {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("FileSignedUrl")
            .field("id", &self.id)
            .field("url", &"<redacted>")
            .field("headers", &"<redacted>")
            .finish_non_exhaustive()
    }
}

fn file_size<'de, D: Deserializer<'de>>(d: D) -> std::result::Result<u64, D::Error> {
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
        .ok_or_else(|| serde::de::Error::custom("file size must be a nonnegative int64"))
}
fn file_cursor<'de, D: Deserializer<'de>>(d: D) -> std::result::Result<Option<String>, D::Error> {
    let next = Option::<String>::deserialize(d)?;
    if next.as_deref().is_some_and(|id| !valid_entity_id(id)) {
        return Err(serde::de::Error::custom(
            "file cursor must be a canonical ULID or null",
        ));
    }
    Ok(next)
}
fn file_req<'a>(
    method: Method,
    bucket: &'a str,
    file: Option<&'a str>,
    suffix: Option<&'a str>,
) -> Result<Req<'a>> {
    if !valid_entity_id(bucket) || file.is_some_and(|id| !valid_entity_id(id)) {
        return Err(Error::InvalidInput(
            "bucket and file IDs must be canonical ULIDs".into(),
        ));
    }
    let mut segments = vec!["v1", "buckets", bucket, "files"];
    segments.extend(file);
    segments.extend(suffix);
    Ok(Req::new(method, segments))
}

// Even malformed success bodies can contain a usable capability. Preserve only
// documented error codes and canonical request IDs; never retain upstream text.
fn capability_error(error: Error) -> Error {
    if matches!(error, Error::TokenExchange { .. } | Error::InvalidInput(_)) {
        return error;
    }
    let Some(status) = error.status() else {
        return Error::Transport("file capability request failed".into());
    };
    let known = concat!(
        "invalid_request forbidden_scope unknown_database unknown_route ",
        "not_found conflict tenant_busy tenant_memory storage_busy capacity ",
        "unavailable invalid_key misdirected_request wrong_region"
    );
    let code = error
        .code()
        .filter(|code| known.split_ascii_whitespace().any(|s| s == *code))
        .unwrap_or("file_capability_error");
    let body = serde_json::json!({"error":{"code":code}}).to_string();
    from_project_response(
        status,
        None,
        None,
        error.request_id().filter(|id| valid_entity_id(id)),
        body.as_bytes(),
    )
}

impl Client {
    pub async fn list_bucket_files(
        &self,
        bucket: &str,
        after: Option<&str>,
    ) -> Result<FileObjectPage> {
        let mut request = file_req(Method::GET, bucket, None, None)?;
        if let Some(after) = after {
            if !valid_entity_id(after) {
                return Err(Error::InvalidInput(
                    "file cursor must be a canonical ULID".into(),
                ));
            }
            request.query.push(("after", after.to_owned()));
        }
        self.send_project(request)
            .await?
            .decode_status(&[StatusCode::OK])
    }

    /// Reserves storage; PUT exact bytes to the returned capability, then complete.
    pub async fn create_file_upload(
        &self,
        bucket: &str,
        body: &FileUploadRequest,
    ) -> Result<FileSignedUrl> {
        if !body.metadata.is_object()
            || !(1..=100 << 20).contains(&body.size)
            || !STANDARD
                .decode(&body.sha256)
                .is_ok_and(|bytes| bytes.len() == 32 && STANDARD.encode(bytes) == body.sha256)
        {
            return Err(Error::InvalidInput(
                "upload requires object metadata, 1–100 MiB and a base64 SHA-256 digest".into(),
            ));
        }
        self.file_capability(
            file_req(Method::POST, bucket, None, Some("upload"))?.json(body)?,
            StatusCode::CREATED,
        )
        .await
    }

    /// Verifies checksum and size before exposing the immutable file to reads.
    pub async fn complete_file_upload(&self, bucket: &str, file: &str) -> Result<FileObject> {
        self.send_project(file_req(
            Method::POST,
            bucket,
            Some(file),
            Some("complete"),
        )?)
        .await?
        .decode_status(&[StatusCode::OK])
    }

    /// Issues a 60-second capability. Do not attach project credentials to storage.
    pub async fn create_file_download(&self, bucket: &str, file: &str) -> Result<FileSignedUrl> {
        self.file_capability(
            file_req(Method::POST, bucket, Some(file), Some("download"))?,
            StatusCode::OK,
        )
        .await
    }

    pub async fn delete_file(&self, bucket: &str, file: &str) -> Result<()> {
        self.send_project(file_req(Method::DELETE, bucket, Some(file), None)?)
            .await?
            .check_status(&[StatusCode::NO_CONTENT])
    }

    async fn file_capability(&self, request: Req<'_>, status: StatusCode) -> Result<FileSignedUrl> {
        self.send_project(request)
            .await
            .map_err(capability_error)?
            .decode_status(&[status])
            .map_err(capability_error)
    }
}
