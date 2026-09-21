//! Native database file storage. Provider credentials stay on the server.

use serde::{de::DeserializeOwned, Deserialize};

use crate::client::{encode_path_segment, SharedClient};
use crate::documents::DatabaseScope;
use crate::error::{Error, ErrorKind, Result};
use crate::regions::RegionCode;
use crate::transport::{Method, Request};

/// Maximum size of one uploaded file (64 MiB).
pub const MAX_FILE_BYTES: usize = 64 * 1024 * 1024;

/// Immutable metadata for a database-owned file.
#[derive(Debug, Clone, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct FileObject {
    pub id: String,
    pub length: u64,
    pub sha256: String,
    pub created_at: String,
    pub content_type: Option<String>,
    pub file_name: Option<String>,
}

/// A bounded list of file metadata.
#[derive(Debug, Clone, Deserialize, PartialEq, Eq)]
pub struct FileObjectsResponse {
    pub files: Vec<FileObject>,
}

/// Optional upload metadata and expected SHA-256 digest.
#[derive(Debug, Clone, Default)]
pub struct FileUploadOptions {
    pub content_type: Option<String>,
    pub file_name: Option<String>,
    pub sha256: Option<String>,
    pub region_hint: Option<RegionCode>,
}

impl FileUploadOptions {
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    #[must_use]
    pub fn with_content_type(mut self, content_type: impl Into<String>) -> Self {
        self.content_type = Some(content_type.into());
        self
    }

    #[must_use]
    pub fn with_file_name(mut self, file_name: impl Into<String>) -> Self {
        self.file_name = Some(file_name.into());
        self
    }

    #[must_use]
    pub fn with_sha256(mut self, sha256: impl Into<String>) -> Self {
        self.sha256 = Some(sha256.into());
        self
    }

    #[must_use]
    pub fn with_region_hint(mut self, region: RegionCode) -> Self {
        self.region_hint = Some(region);
        self
    }
}

/// File operations sharing the root client's authentication and routing.
#[derive(Debug, Clone)]
pub struct FilesClient {
    shared: SharedClient,
}

impl FilesClient {
    pub(crate) fn new(shared: SharedClient) -> Self {
        Self { shared }
    }

    /// Upload bytes. An ambiguous upload failure is never automatically replayed.
    pub async fn upload(
        &self,
        scope: &DatabaseScope,
        content: impl AsRef<[u8]>,
        options: FileUploadOptions,
    ) -> Result<FileObject> {
        let content = content.as_ref().to_vec();
        if content.len() > MAX_FILE_BYTES {
            return Err(invalid_file("File content exceeds the 64 MiB limit."));
        }
        let mut request =
            self.request_in(scope, options.region_hint, Method::Post, "files.upload", "")?;
        if let Some(name) = &options.file_name {
            request.url.push_str("?fileName=");
            request.url.push_str(&encode_path_segment(name));
        }
        request.headers.push((
            "content-type".into(),
            options
                .content_type
                .clone()
                .unwrap_or_else(|| "application/octet-stream".into()),
        ));
        if let Some(hash) = &options.sha256 {
            if hash.len() != 64
                || !hash
                    .bytes()
                    .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
            {
                return Err(invalid_file(
                    "sha256 must be 64 lowercase hexadecimal characters.",
                ));
            }
            request
                .headers
                .push(("x-terrascale-sha256".into(), hash.clone()));
        }
        request.body = Some(content);
        self.send_json(request).await
    }

    /// List up to 100 files in server order.
    pub async fn list(&self, scope: &DatabaseScope) -> Result<FileObjectsResponse> {
        self.send_json(self.request(scope, Method::Get, "files.list", "")?)
            .await
    }

    /// Read metadata for one file.
    pub async fn metadata(&self, scope: &DatabaseScope, id: &str) -> Result<FileObject> {
        let suffix = format!("/{}/metadata", file_id(id)?);
        self.send_json(self.request(scope, Method::Get, "files.metadata", &suffix)?)
            .await
    }

    /// Download the original bytes, regardless of their content type.
    pub async fn download(&self, scope: &DatabaseScope, id: &str) -> Result<Vec<u8>> {
        let suffix = format!("/{}", file_id(id)?);
        let response = self
            .shared
            .send_response(self.request(scope, Method::Get, "files.download", &suffix)?)
            .await?;
        Ok(response.body.to_vec())
    }

    fn request(
        &self,
        scope: &DatabaseScope,
        method: Method,
        route: &str,
        suffix: &str,
    ) -> Result<Request> {
        self.request_in(scope, None, method, route, suffix)
    }

    fn request_in(
        &self,
        scope: &DatabaseScope,
        region_hint: Option<RegionCode>,
        method: Method,
        route: &str,
        suffix: &str,
    ) -> Result<Request> {
        Ok(Request {
            route_name: route.into(),
            method,
            url: format!(
                "{}/files{suffix}",
                self.shared.database_root_in(scope, region_hint)?
            ),
            headers: vec![],
            body: None,
        })
    }

    async fn send_json<T: DeserializeOwned>(&self, request: Request) -> Result<T> {
        let response = self.shared.send_response(request).await?;
        if !response.header("content-type").is_some_and(|value| {
            value
                .split(';')
                .next()
                .is_some_and(|kind| kind.trim().eq_ignore_ascii_case("application/json"))
        }) {
            return Err(Error::invalid_response(
                "File metadata must use application/json.",
            ));
        }
        response.json_body()
    }
}

fn file_id(id: &str) -> Result<String> {
    if id.trim().is_empty() {
        return Err(invalid_file("File ids cannot be blank."));
    }
    Ok(encode_path_segment(id))
}

fn invalid_file(message: &str) -> Error {
    Error::new(ErrorKind::Validation, "invalid_file", message)
}
