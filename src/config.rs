//! Explicit native scope guard, isolated credentials, and bounded read retries.

use crate::discovery::{rtt_header, RegionRtt, ATLAS_BASE_URL};
use crate::error::{Error, ErrorKind, Result};
use crate::regions::normalize_base_url;
use crate::values::TenantId;

#[derive(Clone)]
pub struct ClientConfig {
    api_base_url: Option<String>,
    atlas_base_url: String,
    api_key: Option<String>,
    auth_base_url: Option<String>,
    auth_reverse_proxy: bool,
    auth_api_key: Option<String>,
    tenant_id: Option<TenantId>,
    max_retry_attempts: u8,
    retry_delay_ms: u32,
    region_rtt: Vec<RegionRtt>,
}

impl std::fmt::Debug for ClientConfig {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ClientConfig")
            .field("api_base_url", &self.api_base_url)
            .field("atlas_base_url", &self.atlas_base_url)
            .field("api_key", &"[REDACTED]")
            .field("auth_base_url", &self.auth_base_url)
            .field("auth_reverse_proxy", &self.auth_reverse_proxy)
            .field("auth_api_key", &"[REDACTED]")
            .field("tenant_id", &self.tenant_id)
            .field("max_retry_attempts", &self.max_retry_attempts)
            .field("retry_delay_ms", &self.retry_delay_ms)
            .field("region_rtt", &self.region_rtt)
            .finish()
    }
}

impl ClientConfig {
    #[must_use]
    pub fn builder() -> ClientConfigBuilder {
        ClientConfigBuilder::default()
    }
    /// Reviewed direct native origin; absent requires scoped Atlas admission.
    #[must_use]
    pub fn api_base_url(&self) -> Option<&str> {
        self.api_base_url.as_deref()
    }
    /// Atlas origin. Native discovery admits only the canonical production origin.
    #[must_use]
    pub fn atlas_base_url(&self) -> Option<&str> {
        Some(&self.atlas_base_url)
    }
    #[must_use]
    pub fn auth_base_url(&self) -> Option<&str> {
        self.auth_base_url.as_deref()
    }
    #[must_use]
    pub fn auth_reverse_proxy(&self) -> bool {
        self.auth_reverse_proxy
    }
    /// Optional native tenant guard, compared to every request's typed scope.
    #[must_use]
    pub fn tenant_id(&self) -> Option<TenantId> {
        self.tenant_id
    }
    #[must_use]
    pub fn max_retry_attempts(&self) -> u8 {
        self.max_retry_attempts
    }
    #[must_use]
    pub fn retry_delay_ms(&self) -> u32 {
        self.retry_delay_ms
    }
    #[must_use]
    pub fn region_rtt(&self) -> &[RegionRtt] {
        &self.region_rtt
    }
    pub(crate) fn api_key(&self) -> Option<&str> {
        self.api_key.as_deref()
    }
    pub(crate) fn auth_api_key(&self) -> Option<&str> {
        self.auth_api_key.as_deref()
    }
}

#[derive(Default)]
pub struct ClientConfigBuilder {
    api_base_url: Option<String>,
    atlas_base_url: Option<String>,
    api_key: Option<String>,
    auth_base_url: Option<String>,
    auth_reverse_proxy: bool,
    auth_api_key: Option<String>,
    tenant_id: Option<String>,
    max_retry_attempts: u8,
    retry_delay_ms: Option<u32>,
    region_rtt: Vec<RegionRtt>,
}

impl std::fmt::Debug for ClientConfigBuilder {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ClientConfigBuilder")
            .field("api_base_url", &self.api_base_url)
            .field("atlas_base_url", &self.atlas_base_url)
            .field("api_key", &"[REDACTED]")
            .field("auth_base_url", &self.auth_base_url)
            .field("auth_reverse_proxy", &self.auth_reverse_proxy)
            .field("auth_api_key", &"[REDACTED]")
            .field("tenant_id", &self.tenant_id)
            .field("max_retry_attempts", &self.max_retry_attempts)
            .field("retry_delay_ms", &self.retry_delay_ms)
            .field("region_rtt", &self.region_rtt)
            .finish()
    }
}

impl ClientConfigBuilder {
    /// Supply up to 20 distinct measured RTTs for receiver ranking.
    #[must_use]
    pub fn region_rtt(mut self, hints: Vec<RegionRtt>) -> Self {
        self.region_rtt = hints;
        self
    }
    /// Set a reviewed direct native regional/loopback origin. No global default.
    #[must_use]
    pub fn api_base_url(mut self, value: impl Into<String>) -> Self {
        self.api_base_url = Some(value.into());
        self
    }
    #[must_use]
    pub fn atlas_base_url(mut self, value: impl Into<String>) -> Self {
        self.atlas_base_url = Some(value.into());
        self
    }
    #[must_use]
    pub fn api_key(mut self, value: impl Into<String>) -> Self {
        self.api_key = Some(value.into());
        self
    }
    #[must_use]
    pub fn auth_base_url(mut self, value: impl Into<String>) -> Self {
        self.auth_base_url = Some(value.into());
        self
    }
    #[must_use]
    pub fn auth_reverse_proxy(mut self, value: bool) -> Self {
        self.auth_reverse_proxy = value;
        self
    }
    #[must_use]
    pub fn auth_api_key(mut self, value: impl Into<String>) -> Self {
        self.auth_api_key = Some(value.into());
        self
    }
    /// Require all native requests to use this canonical UUID tenant.
    #[must_use]
    pub fn tenant_id(mut self, value: impl Into<String>) -> Self {
        self.tenant_id = Some(value.into());
        self
    }
    /// Retry only safe read/query/poll/status traffic, never commits or auth mutations.
    #[must_use]
    pub fn max_retry_attempts(mut self, value: u8) -> Self {
        self.max_retry_attempts = value;
        self
    }
    #[must_use]
    pub fn retry_delay_ms(mut self, value: u32) -> Self {
        self.retry_delay_ms = Some(value);
        self
    }

    pub fn build(self) -> Result<ClientConfig> {
        rtt_header(&self.region_rtt)?;
        let api_key = self.api_key.as_deref().map(normalize_api_key).transpose()?;
        let auth_api_key = self
            .auth_api_key
            .as_deref()
            .map(normalize_api_key)
            .transpose()?;
        let tenant_id = self.tenant_id.as_deref().map(str::parse).transpose()?;
        if self.max_retry_attempts > 8 {
            return Err(Error::new(
                ErrorKind::Validation,
                "invalid_retry",
                "Retry attempts must be 0..8.",
            ));
        }
        let retry_delay_ms = self.retry_delay_ms.unwrap_or(250);
        if retry_delay_ms > 30_000 {
            return Err(Error::new(
                ErrorKind::Validation,
                "invalid_retry",
                "Retry delay must be 0..30000 ms.",
            ));
        }
        let api_base_url = self
            .api_base_url
            .as_deref()
            .map(normalize_base_url)
            .transpose()?;
        if let Some(origin) = &api_base_url {
            let parsed = reqwest::Url::parse(origin).map_err(|_| {
                Error::new(
                    ErrorKind::Validation,
                    "invalid_base_url",
                    "Invalid native origin.",
                )
            })?;
            if parsed.host_str().is_some_and(|host| {
                host.trim_end_matches('.')
                    .eq_ignore_ascii_case("api.terrascale.tech")
            }) {
                return Err(Error::new(ErrorKind::Validation, "invalid_native_origin", "Native traffic requires a direct regional origin; the global API test host is not a fallback."));
            }
            if parsed.path() != "/" {
                return Err(Error::new(
                    ErrorKind::Validation,
                    "invalid_base_url",
                    "Native origin must not contain a path.",
                ));
            }
        }
        let auth_base_url = self
            .auth_base_url
            .as_deref()
            .map(normalize_base_url)
            .transpose()?;
        let atlas_base_url = self
            .atlas_base_url
            .as_deref()
            .map(normalize_base_url)
            .transpose()?
            .unwrap_or_else(|| ATLAS_BASE_URL.to_owned());
        Ok(ClientConfig {
            api_base_url,
            atlas_base_url,
            api_key,
            auth_base_url,
            auth_reverse_proxy: self.auth_reverse_proxy,
            auth_api_key,
            tenant_id,
            max_retry_attempts: self.max_retry_attempts,
            retry_delay_ms,
            region_rtt: self.region_rtt,
        })
    }
}

/// Normalize the caller credential once at configuration time so every
/// transport receives the same token and can add its own auth scheme prefix.
pub(crate) fn normalize_api_key(value: &str) -> Result<String> {
    if value.chars().any(char::is_control) {
        return Err(Error::new(
            ErrorKind::Validation,
            "invalid_api_key",
            "API keys cannot contain control characters.",
        ));
    }

    let value = value.trim();
    let value = if value.eq_ignore_ascii_case("bearer") {
        ""
    } else {
        let prefix_matches = value
            .as_bytes()
            .get(..6)
            .is_some_and(|prefix| prefix.eq_ignore_ascii_case(b"bearer"));
        if prefix_matches {
            let rest = value
                .get(6..)
                .expect("an ASCII prefix ends on a UTF-8 character boundary");
            if rest.chars().next().is_some_and(char::is_whitespace) {
                rest.trim()
            } else {
                value
            }
        } else {
            value
        }
    };
    if value.is_empty() {
        return Err(Error::new(
            ErrorKind::Validation,
            "invalid_api_key",
            "API keys cannot be empty.",
        ));
    }
    if value.chars().any(char::is_whitespace) {
        return Err(Error::new(
            ErrorKind::Validation,
            "invalid_api_key",
            "API keys cannot contain whitespace characters.",
        ));
    }
    Ok(value.to_owned())
}
