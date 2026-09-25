use std::error::Error;
use std::fmt;
use std::io::{self, Read, Write};
use std::time::Duration;

use reqwest::blocking::{Client, Response};
use reqwest::redirect::Policy;
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};

use crate::{RunDirectory, RunDirectoryError, StagedFile};

const MAX_ARTIFACT_BYTES: u64 = 32 * 1024 * 1024 * 1024;
const CONNECT_TIMEOUT: Duration = Duration::from_secs(30);
const REQUEST_TIMEOUT: Duration = Duration::from_secs(60 * 30);
const FIRECRACKER_RELEASE_URL: &str = "https://github.com/firecracker-microvm/firecracker/releases/download/v1.17.0/firecracker-v1.17.0-x86_64.tgz";
const FIRECRACKER_RELEASE_ASSET_HOST: &str = "release-assets.githubusercontent.com";

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct ArtifactPin {
    pub id: String,
    pub url: String,
    pub sha256: String,
    pub max_bytes: u64,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct DownloadReceipt {
    pub id: String,
    pub source_url: String,
    pub final_url: String,
    pub redirect_count: u8,
    pub origin: String,
    pub cache_path: String,
    pub size_bytes: u64,
    pub sha256: String,
    pub http_status: u16,
}

#[derive(Debug)]
pub enum FetchError {
    Invalid(String),
    Http(reqwest::Error),
    Io(io::Error),
    RunDirectory(RunDirectoryError),
}

impl fmt::Display for FetchError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Invalid(error) => write!(formatter, "input verification failed: {error}"),
            Self::Http(error) => write!(formatter, "HTTPS fetch failed: {error}"),
            Self::Io(error) => write!(formatter, "input cache I/O failed: {error}"),
            Self::RunDirectory(error) => write!(formatter, "input cache is unsafe: {error}"),
        }
    }
}

impl Error for FetchError {
    fn source(&self) -> Option<&(dyn Error + 'static)> {
        match self {
            Self::Invalid(_) => None,
            Self::Http(error) => Some(error),
            Self::Io(error) => Some(error),
            Self::RunDirectory(error) => Some(error),
        }
    }
}

impl From<reqwest::Error> for FetchError {
    fn from(error: reqwest::Error) -> Self {
        Self::Http(error)
    }
}

impl From<io::Error> for FetchError {
    fn from(error: io::Error) -> Self {
        Self::Io(error)
    }
}

impl From<RunDirectoryError> for FetchError {
    fn from(error: RunDirectoryError) -> Self {
        Self::RunDirectory(error)
    }
}

/// HTTPS client that does not use ambient proxy settings or follow redirects.
#[derive(Debug)]
pub struct HttpsFetcher {
    client: Client,
}

impl HttpsFetcher {
    /// Build an HTTPS-only client without ambient proxy or redirect behavior.
    ///
    /// # Errors
    ///
    /// Returns an error if the TLS-backed HTTP client cannot be initialized.
    pub fn new() -> Result<Self, FetchError> {
        let client = Client::builder()
            .https_only(true)
            .no_proxy()
            .redirect(Policy::none())
            .connect_timeout(CONNECT_TIMEOUT)
            .timeout(REQUEST_TIMEOUT)
            .build()?;
        Ok(Self { client })
    }

    /// Download one exact HTTPS input into a unique run directory, checking
    /// status, length bounds and SHA-256 before the final filename appears.
    ///
    /// # Errors
    ///
    /// Returns an error if the pin is invalid, the response is not the exact
    /// successful URL, the size/hash differs, or cache I/O fails.
    pub fn fetch_verified(
        &self,
        pin: &ArtifactPin,
        run: &RunDirectory,
    ) -> Result<DownloadReceipt, FetchError> {
        validate_pin(pin)?;
        self.fetch_observed(&pin.id, &pin.url, pin.max_bytes, Some(&pin.sha256), run)
    }

    /// Download a bounded, exact-origin signed metadata object before its
    /// detached or embedded signature has been verified. Callers must not
    /// parse, extract, or otherwise trust the returned bytes until signature
    /// verification succeeds.
    ///
    /// # Errors
    ///
    /// Returns an error if the URL is not locked HTTPS, the response redirects
    /// or is unsuccessful, the payload exceeds the byte bound, or cache I/O
    /// fails.
    pub fn fetch_observed(
        &self,
        id: &str,
        source_url: &str,
        max_bytes: u64,
        expected_sha256: Option<&str>,
        run: &RunDirectory,
    ) -> Result<DownloadReceipt, FetchError> {
        validate_source(id, source_url, max_bytes)?;
        if expected_sha256.is_some_and(|hash| {
            hash.len() != 64 || !hash.bytes().all(|byte| byte.is_ascii_hexdigit())
        }) {
            return Err(FetchError::Invalid(
                "optional artifact digest must be a full SHA-256 value".to_owned(),
            ));
        }
        let url = reqwest::Url::parse(source_url)
            .map_err(|_| FetchError::Invalid("locked URL is malformed".to_owned()))?;
        let response = self.client.get(url.clone()).send()?;
        validate_response(&url, &response, max_bytes)?;
        let status = response.status().as_u16();
        let final_url = response.url().as_str().to_owned();
        let response_origin = origin(response.url());
        let mut staged = run.create_staged_file(id)?;
        let (size_bytes, sha256) = stream_hash(response, max_bytes, &mut staged)?;
        if expected_sha256.is_some_and(|expected| !sha256.eq_ignore_ascii_case(expected)) {
            return Err(FetchError::Invalid(format!(
                "SHA-256 mismatch for {id}: expected {}, received {sha256}",
                expected_sha256.unwrap_or_default()
            )));
        }
        let destination = staged.commit()?;
        Ok(DownloadReceipt {
            id: id.to_owned(),
            source_url: source_url.to_owned(),
            final_url,
            redirect_count: 0,
            origin: response_origin,
            cache_path: destination
                .file_name()
                .ok_or_else(|| FetchError::Invalid("cache output has no filename".to_owned()))?
                .to_string_lossy()
                .into_owned(),
            size_bytes,
            sha256,
            http_status: status,
        })
    }

    /// Fetch the one locked Firecracker release archive through GitHub's
    /// release-assets CDN, allowing only a single HTTPS redirect to its
    /// canonical CDN host. The global fetcher continues to reject redirects.
    ///
    /// # Errors
    ///
    /// Returns an error unless the exact official URL returns either HTTP 200
    /// or one HTTP 302 to `release-assets.githubusercontent.com`, and the
    /// final response has the exact pinned size and digest.
    pub(crate) fn fetch_verified_firecracker_release(
        &self,
        pin: &ArtifactPin,
        expected_size_bytes: u64,
        run: &RunDirectory,
    ) -> Result<DownloadReceipt, FetchError> {
        validate_pin(pin)?;
        if pin.url != FIRECRACKER_RELEASE_URL
            || pin.max_bytes != expected_size_bytes
            || expected_size_bytes == 0
        {
            return Err(FetchError::Invalid(
                "Firecracker release redirect is available only for its exact pinned URL and size"
                    .to_owned(),
            ));
        }
        let initial = reqwest::Url::parse(&pin.url)
            .map_err(|_| FetchError::Invalid("locked release URL is malformed".to_owned()))?;
        let first = self.client.get(initial.clone()).send()?;
        let (response, final_url, redirect_count) = if first.status() == reqwest::StatusCode::OK {
            if first.url() != &initial {
                return Err(FetchError::Invalid(
                    "Firecracker release response changed its URL without a redirect"
                        .to_owned(),
                ));
            }
            (first, initial, 0)
        } else if first.status() == reqwest::StatusCode::FOUND {
            let location = first
                .headers()
                .get(reqwest::header::LOCATION)
                .ok_or_else(|| {
                    FetchError::Invalid("Firecracker release redirect omitted Location".to_owned())
                })?
                .to_str()
                .map_err(|_| {
                    FetchError::Invalid("Firecracker release Location is not valid text".to_owned())
                })?;
            let target = validate_firecracker_redirect_target(&initial, location)?;
            let redirected = self.client.get(target.clone()).send()?;
            if redirected.url() != &target || redirected.status() != reqwest::StatusCode::OK {
                return Err(FetchError::Invalid(
                    "Firecracker release CDN did not return HTTP 200 without another redirect"
                        .to_owned(),
                ));
            }
            (redirected, target, 1)
        } else {
            return Err(FetchError::Invalid(format!(
                "locked Firecracker release returned unexpected HTTP status {}",
                first.status()
            )));
        };
        if response
            .content_length()
            .is_some_and(|length| length != expected_size_bytes)
        {
            return Err(FetchError::Invalid(
                "Firecracker release Content-Length differs from its exact pin".to_owned(),
            ));
        }
        let mut staged = run.create_staged_file(&pin.id)?;
        let (size_bytes, sha256) = stream_hash(response, expected_size_bytes, &mut staged)?;
        if size_bytes != expected_size_bytes || !sha256.eq_ignore_ascii_case(&pin.sha256) {
            return Err(FetchError::Invalid(
                "Firecracker release size or SHA-256 differs from its exact pin".to_owned(),
            ));
        }
        let destination = staged.commit()?;
        Ok(DownloadReceipt {
            id: pin.id.clone(),
            source_url: pin.url.clone(),
            final_url: final_url.as_str().to_owned(),
            redirect_count,
            origin: origin(&final_url),
            cache_path: destination
                .file_name()
                .ok_or_else(|| FetchError::Invalid("release cache output has no filename".to_owned()))?
                .to_string_lossy()
                .into_owned(),
            size_bytes,
            sha256,
            http_status: 200,
        })
    }
}

fn validate_firecracker_redirect_target(
    initial: &reqwest::Url,
    location: &str,
) -> Result<reqwest::Url, FetchError> {
    if initial.as_str() != FIRECRACKER_RELEASE_URL {
        return Err(FetchError::Invalid(
            "Firecracker redirect did not start at the exact pinned URL".to_owned(),
        ));
    }
    let target = initial.join(location).map_err(|_| {
        FetchError::Invalid("Firecracker release Location is malformed".to_owned())
    })?;
    if target.scheme() != "https"
        || target.host_str() != Some(FIRECRACKER_RELEASE_ASSET_HOST)
        || target.port().is_some_and(|port| port != 443)
        || !target.username().is_empty()
        || target.password().is_some()
        || target.fragment().is_some()
    {
        return Err(FetchError::Invalid(
            "Firecracker release redirect target is outside the approved HTTPS CDN origin"
                .to_owned(),
        ));
    }
    Ok(target)
}

pub(crate) fn validate_download_transport(receipt: &DownloadReceipt) -> Result<(), FetchError> {
    if receipt.http_status != 200 {
        return Err(FetchError::Invalid(
            "download receipt does not end in HTTP 200".to_owned(),
        ));
    }
    let source = reqwest::Url::parse(&receipt.source_url)
        .map_err(|_| FetchError::Invalid("download receipt source URL is malformed".to_owned()))?;
    let final_url = reqwest::Url::parse(&receipt.final_url)
        .map_err(|_| FetchError::Invalid("download receipt final URL is malformed".to_owned()))?;
    if receipt.id == "firecracker-release-archive" {
        if receipt.source_url != FIRECRACKER_RELEASE_URL || receipt.redirect_count > 1 {
            return Err(FetchError::Invalid(
                "Firecracker release receipt has an unapproved origin or redirect count"
                    .to_owned(),
            ));
        }
        match receipt.redirect_count {
            0 if receipt.final_url == receipt.source_url => {}
            1 => {
                let expected = validate_firecracker_redirect_target(&source, &receipt.final_url)?;
                if expected != final_url {
                    return Err(FetchError::Invalid(
                        "Firecracker release final URL differs from its approved redirect"
                            .to_owned(),
                    ));
                }
            }
            _ => {
                return Err(FetchError::Invalid(
                    "Firecracker release receipt has an invalid redirect count".to_owned(),
                ));
            }
        }
    } else if receipt.redirect_count != 0 || receipt.final_url != receipt.source_url {
        return Err(FetchError::Invalid(
            "non-release artifact receipt records an unexpected redirect".to_owned(),
        ));
    }
    if receipt.origin != origin(&final_url) {
        return Err(FetchError::Invalid(
            "download receipt origin differs from its final URL".to_owned(),
        ));
    }
    Ok(())
}

fn validate_pin(pin: &ArtifactPin) -> Result<(), FetchError> {
    validate_source(&pin.id, &pin.url, pin.max_bytes)?;
    if pin.sha256.len() != 64 || !pin.sha256.bytes().all(|byte| byte.is_ascii_hexdigit()) {
        return Err(FetchError::Invalid(
            "artifact pin must contain a full SHA-256 digest".to_owned(),
        ));
    }
    let url = reqwest::Url::parse(&pin.url)
        .map_err(|_| FetchError::Invalid("locked URL is malformed".to_owned()))?;
    if url.scheme() != "https"
        || url.host_str().is_none()
        || !url.username().is_empty()
        || url.password().is_some()
        || url.fragment().is_some()
    {
        return Err(FetchError::Invalid(
            "artifact origin must be locked HTTPS without credentials or fragment".to_owned(),
        ));
    }
    Ok(())
}

fn validate_source(id: &str, source_url: &str, max_bytes: u64) -> Result<(), FetchError> {
    if id.is_empty()
        || id.len() > 180
        || !id
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'.' | b'_' | b'-'))
    {
        return Err(FetchError::Invalid(
            "artifact id must be a safe filename".to_owned(),
        ));
    }
    if max_bytes == 0 || max_bytes > MAX_ARTIFACT_BYTES {
        return Err(FetchError::Invalid(format!(
            "artifact size bound must be between 1 and {MAX_ARTIFACT_BYTES} bytes"
        )));
    }
    let url = reqwest::Url::parse(source_url)
        .map_err(|_| FetchError::Invalid("locked URL is malformed".to_owned()))?;
    if url.scheme() != "https"
        || url.host_str().is_none()
        || !url.username().is_empty()
        || url.password().is_some()
        || url.fragment().is_some()
    {
        return Err(FetchError::Invalid(
            "artifact origin must be locked HTTPS without credentials or fragment".to_owned(),
        ));
    }
    Ok(())
}

fn validate_response(
    url: &reqwest::Url,
    response: &Response,
    max_bytes: u64,
) -> Result<(), FetchError> {
    if response.url() != url {
        return Err(FetchError::Invalid(
            "HTTP client returned a response from a different URL".to_owned(),
        ));
    }
    if response.status() != reqwest::StatusCode::OK {
        return Err(FetchError::Invalid(format!(
            "locked input returned HTTP status {}",
            response.status()
        )));
    }
    if response
        .content_length()
        .is_some_and(|length| length > max_bytes)
    {
        return Err(FetchError::Invalid(
            "locked input exceeds its declared byte limit".to_owned(),
        ));
    }
    Ok(())
}

fn stream_hash(
    mut response: Response,
    max_bytes: u64,
    output: &mut StagedFile,
) -> Result<(u64, String), FetchError> {
    let mut hasher = Sha256::new();
    let mut total = 0_u64;
    let mut buffer = vec![0_u8; 128 * 1024].into_boxed_slice();
    loop {
        let read = response.read(&mut buffer)?;
        if read == 0 {
            break;
        }
        let next_total = total
            .checked_add(
                u64::try_from(read)
                    .map_err(|_| FetchError::Invalid("download length exceeds u64".to_owned()))?,
            )
            .ok_or_else(|| FetchError::Invalid("download length overflows".to_owned()))?;
        if next_total > max_bytes {
            return Err(FetchError::Invalid(
                "locked input exceeded its byte limit while streaming".to_owned(),
            ));
        }
        output.write_all(&buffer[..read])?;
        hasher.update(&buffer[..read]);
        total = next_total;
    }
    if total == 0 {
        return Err(FetchError::Invalid(
            "locked input is unexpectedly empty".to_owned(),
        ));
    }
    let digest = hasher.finalize();
    Ok((total, digest_hex(&digest)))
}

fn origin(url: &reqwest::Url) -> String {
    let host = url.host_str().unwrap_or_default();
    match url.port() {
        Some(port) => format!("https://{host}:{port}"),
        None => format!("https://{host}"),
    }
}

fn digest_hex(digest: &[u8]) -> String {
    let mut text = String::with_capacity(digest.len() * 2);
    for byte in digest {
        use fmt::Write as _;
        write!(&mut text, "{byte:02x}").expect("writing into String cannot fail");
    }
    text
}

#[cfg(test)]
mod tests {
    use super::{
        ArtifactPin, FetchError, FIRECRACKER_RELEASE_URL, validate_firecracker_redirect_target,
        validate_pin,
    };

    #[test]
    fn locked_input_requires_https_full_hash_and_bounded_size() {
        let valid = ArtifactPin {
            id: "ubuntu-base.tar.gz".to_owned(),
            url: "https://example.invalid/ubuntu-base.tar.gz".to_owned(),
            sha256: "a".repeat(64),
            max_bytes: 1024,
        };
        assert!(validate_pin(&valid).is_ok());

        let insecure = ArtifactPin {
            url: "http://example.invalid/input".to_owned(),
            ..valid.clone()
        };
        assert!(matches!(
            validate_pin(&insecure),
            Err(FetchError::Invalid(_))
        ));
        let traversal = ArtifactPin {
            id: "../outside".to_owned(),
            ..valid.clone()
        };
        assert!(matches!(
            validate_pin(&traversal),
            Err(FetchError::Invalid(_))
        ));
        let unbounded = ArtifactPin {
            max_bytes: u64::MAX,
            ..valid.clone()
        };
        assert!(matches!(
            validate_pin(&unbounded),
            Err(FetchError::Invalid(_))
        ));
        let missing_digest = ArtifactPin {
            sha256: String::new(),
            ..valid
        };
        assert!(matches!(
            validate_pin(&missing_digest),
            Err(FetchError::Invalid(_))
        ));
    }

    #[test]
    fn firecracker_release_redirect_is_limited_to_the_official_cdn_origin() {
        let initial = reqwest::Url::parse(FIRECRACKER_RELEASE_URL).unwrap();
        assert!(validate_firecracker_redirect_target(
            &initial,
            "https://release-assets.githubusercontent.com/asset?token=opaque"
        )
        .is_ok());
        for location in [
            "http://release-assets.githubusercontent.com/asset",
            "https://github.com/other/asset",
            "https://release-assets.githubusercontent.com:444/asset",
            "https://user@release-assets.githubusercontent.com/asset",
        ] {
            assert!(matches!(
                validate_firecracker_redirect_target(&initial, location),
                Err(FetchError::Invalid(_))
            ));
        }
    }
}
