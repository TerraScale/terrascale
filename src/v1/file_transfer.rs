//! Bounded storage transfers using only a returned capability, never project auth.
use super::{files::FileSignedUrl, Error, Result};
use base64::{engine::general_purpose::STANDARD, Engine};
use bytes::Bytes;
use futures_util::StreamExt;
use reqwest::{
    header::{HeaderMap, HeaderName, HeaderValue},
    Method, StatusCode, Url,
};
use sha2::{Digest, Sha256};
use std::time::Duration;

const MAX_BYTES: usize = 100 << 20;
fn storage_client() -> reqwest::ClientBuilder {
    reqwest::Client::builder()
        .use_rustls_tls()
        .min_tls_version(reqwest::tls::Version::TLS_1_3)
        .https_only(true)
        .redirect(reqwest::redirect::Policy::none())
        .referer(false)
        .no_proxy()
        // Preserve immutable storage bytes even if a consumer enables codecs.
        .no_gzip()
        .no_brotli()
        .no_deflate()
        .no_zstd()
        .retry(reqwest::retry::never())
        .timeout(Duration::from_secs(30))
        .connect_timeout(Duration::from_secs(10))
}
fn invalid() -> Error {
    Error::InvalidInput("invalid storage capability or upload bytes".into())
}
fn transport(_: reqwest::Error) -> Error {
    Error::Transport("storage transfer failed".into())
}
fn status_error(status: StatusCode) -> Error {
    Error::Api {
        status: status.as_u16(),
        code: "file_transfer_error".into(),
        message: None,
        op: None,
        details: None,
    }
}

impl FileSignedUrl {
    /// PUTs exact immutable bytes once; complete the reservation separately after success.
    pub async fn put_bytes(&self, data: impl Into<Bytes>) -> Result<()> {
        self.transfer(
            &storage_client().build().map_err(transport)?,
            Some(data.into()),
        )
        .await
        .map(|_| ())
    }

    /// Fetches at most 100 MiB once; errors redact capabilities. Dropping cancels.
    pub async fn get_bytes(&self) -> Result<Bytes> {
        self.transfer(&storage_client().build().map_err(transport)?, None)
            .await
    }

    async fn transfer(&self, http: &reqwest::Client, data: Option<Bytes>) -> Result<Bytes> {
        let url = Url::parse(&self.url).map_err(|_| invalid())?;
        if url.scheme() != "https"
            || url.host().is_none()
            || !url.username().is_empty()
            || url.password().is_some()
            || url.fragment().is_some()
        {
            return Err(invalid());
        }
        let mut headers = HeaderMap::new();
        for (name, value) in &self.headers {
            let name = HeaderName::from_bytes(name.as_bytes()).map_err(|_| invalid())?;
            // Capability authorization is in the URL. These headers could carry
            // unrelated credentials or alter routing/framing, so reject the grant.
            if matches!(
                name.as_str(),
                "authorization"
                    | "proxy-authorization"
                    | "cookie"
                    | "host"
                    | "connection"
                    | "transfer-encoding"
                    | "trailer"
                    | "upgrade"
                    | "te"
            ) {
                return Err(invalid());
            }
            let mut value = HeaderValue::from_str(value).map_err(|_| invalid())?;
            value.set_sensitive(true);
            if headers.insert(name, value).is_some() {
                return Err(invalid());
            }
        }
        let method = if data.is_some() {
            Method::PUT
        } else {
            Method::GET
        };
        let mut request = http.request(method, url);
        if let Some(data) = data {
            if data.is_empty() || data.len() > MAX_BYTES {
                return Err(invalid());
            }
            if let Some(length) = headers.get("content-length") {
                let length = length.to_str().ok().and_then(|s| s.parse::<usize>().ok());
                if length != Some(data.len()) {
                    return Err(invalid());
                }
            }
            if let Some(checksum) = headers.get("x-amz-checksum-sha256") {
                if checksum.as_bytes() != STANDARD.encode(Sha256::digest(&data)).as_bytes() {
                    return Err(invalid());
                }
            }
            let response = request
                .headers(headers)
                .body(data)
                .send()
                .await
                .map_err(transport)?;
            return if response.status().is_success() {
                Ok(Bytes::new())
            } else {
                Err(status_error(response.status()))
            };
        }
        request = request.headers(headers);
        let response = request.send().await.map_err(transport)?;
        let status = response.status();
        if status != StatusCode::OK {
            return Err(status_error(status));
        }
        let large = || Error::ResponseTooLarge {
            limit: MAX_BYTES,
            status: status.as_u16(),
            may_have_applied: false,
        };
        if response
            .content_length()
            .is_some_and(|n| n > MAX_BYTES as u64)
        {
            return Err(large());
        }
        let mut chunks = response.bytes_stream();
        let mut bytes = Vec::new();
        while let Some(chunk) = chunks.next().await {
            let chunk = chunk.map_err(transport)?;
            if bytes.len().saturating_add(chunk.len()) > MAX_BYTES {
                return Err(large());
            }
            bytes.extend_from_slice(&chunk);
        }
        Ok(bytes.into())
    }
}

#[cfg(test)]
#[path = "file_transfer_tests.rs"]
mod tests;
