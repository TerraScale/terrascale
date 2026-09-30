//! Explicit native scope guard, isolated credentials, and bounded read retries.

use crate::discovery::{rtt_header, RegionRtt, ATLAS_BASE_URL};
use crate::error::{Error, ErrorKind, Result};
use crate::regions::normalize_base_url;
use crate::values::TenantId;
use std::time::Duration;

pub const DEFAULT_API_BASE_URL: &str = "https://api.terrascale.tech";

/// Whether a preferred region can fail over to another discovered endpoint.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub enum RegionAffinity {
    #[default]
    Failover,
    Strict,
}

#[derive(Clone)]
pub struct ClientConfig {
    api_base_url: Option<String>,
    discovery_base_url: String,
    api_key: Option<String>,
    auth_base_url: Option<String>,
    auth_reverse_proxy: bool,
    auth_api_key: Option<String>,
    tenant_id: Option<TenantId>,
    max_retry_attempts: u8,
    retry_delay_ms: u32,
    region_rtt: Vec<RegionRtt>,
    preferred_region: Option<String>,
    region_affinity: RegionAffinity,
    discovery_timeout: Duration,
}

impl std::fmt::Debug for ClientConfig {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ClientConfig")
            .field("api_base_url", &self.api_base_url)
            .field("discovery_base_url", &self.discovery_base_url)
            .field("api_key", &"[REDACTED]")
            .field("auth_base_url", &self.auth_base_url)
            .field("auth_reverse_proxy", &self.auth_reverse_proxy)
            .field("auth_api_key", &"[REDACTED]")
            .field("tenant_id", &self.tenant_id)
            .field("max_retry_attempts", &self.max_retry_attempts)
            .field("retry_delay_ms", &self.retry_delay_ms)
            .field("region_rtt", &self.region_rtt)
            .field("preferred_region", &self.preferred_region)
            .field("region_affinity", &self.region_affinity)
            .field("discovery_timeout", &self.discovery_timeout)
            .finish()
    }
}

impl ClientConfig {
    #[must_use]
    pub fn builder() -> ClientConfigBuilder {
        ClientConfigBuilder::default()
    }
    /// API origin; absent or the default global API origin enables discovery.
    #[must_use]
    pub fn api_base_url(&self) -> Option<&str> {
        self.api_base_url.as_deref()
    }
    /// Atlas origin. Native discovery admits only the canonical production origin.
    #[must_use]
    pub fn discovery_base_url(&self) -> Option<&str> {
        Some(&self.discovery_base_url)
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
    #[must_use]
    pub fn preferred_region(&self) -> Option<&str> {
        self.preferred_region.as_deref()
    }
    #[must_use]
    pub fn region_affinity(&self) -> RegionAffinity {
        self.region_affinity
    }
    #[must_use]
    pub fn discovery_timeout(&self) -> Duration {
        self.discovery_timeout
    }
    pub(crate) fn discovery_enabled(&self) -> bool {
        self.api_base_url()
            .is_none_or(|origin| origin == DEFAULT_API_BASE_URL)
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
    discovery_base_url: Option<String>,
    api_key: Option<String>,
    auth_base_url: Option<String>,
    auth_reverse_proxy: bool,
    auth_api_key: Option<String>,
    tenant_id: Option<String>,
    max_retry_attempts: u8,
    retry_delay_ms: Option<u32>,
    region_rtt: Vec<RegionRtt>,
    preferred_region: Option<String>,
    region_affinity: RegionAffinity,
    discovery_timeout: Option<Duration>,
}

impl std::fmt::Debug for ClientConfigBuilder {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ClientConfigBuilder")
            .field("api_base_url", &self.api_base_url)
            .field("discovery_base_url", &self.discovery_base_url)
            .field("api_key", &"[REDACTED]")
            .field("auth_base_url", &self.auth_base_url)
            .field("auth_reverse_proxy", &self.auth_reverse_proxy)
            .field("auth_api_key", &"[REDACTED]")
            .field("tenant_id", &self.tenant_id)
            .field("max_retry_attempts", &self.max_retry_attempts)
            .field("retry_delay_ms", &self.retry_delay_ms)
            .field("region_rtt", &self.region_rtt)
            .field("preferred_region", &self.preferred_region)
            .field("region_affinity", &self.region_affinity)
            .field("discovery_timeout", &self.discovery_timeout)
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
    /// Set an API origin. A custom origin disables discovery.
    #[must_use]
    pub fn api_base_url(mut self, value: impl Into<String>) -> Self {
        self.api_base_url = Some(value.into());
        self
    }
    #[must_use]
    pub fn preferred_region(mut self, value: impl Into<String>) -> Self {
        self.preferred_region = Some(value.into());
        self
    }
    #[must_use]
    pub fn region_affinity(mut self, value: RegionAffinity) -> Self {
        self.region_affinity = value;
        self
    }
    /// Bound discovery latency before falling back to the Atlas API proxy.
    #[must_use]
    pub fn discovery_timeout(mut self, value: Duration) -> Self {
        self.discovery_timeout = Some(value);
        self
    }
    #[must_use]
    pub fn discovery_base_url(mut self, value: impl Into<String>) -> Self {
        self.discovery_base_url = Some(value.into());
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
        let mut api_base_url = self
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
            if parsed.path() != "/" {
                return Err(Error::new(
                    ErrorKind::Validation,
                    "invalid_base_url",
                    "Native origin must not contain a path.",
                ));
            }
            api_base_url = Some(parsed.origin().ascii_serialization());
        }
        let discovery_timeout = self.discovery_timeout.unwrap_or(Duration::from_secs(3));
        if discovery_timeout.is_zero() || discovery_timeout > Duration::from_secs(30) {
            return Err(Error::new(
                ErrorKind::Validation,
                "invalid_discovery_timeout",
                "Discovery timeout must be greater than zero and at most 30 seconds.",
            ));
        }
        if self
            .preferred_region
            .as_deref()
            .is_some_and(|region| !crate::discovery::valid_region(region))
        {
            return Err(Error::new(
                ErrorKind::Validation,
                "invalid_region",
                "Preferred region must be an admitted uppercase region code.",
            ));
        }
        if self.region_affinity == RegionAffinity::Strict {
            let region = self.preferred_region.as_deref().ok_or_else(|| {
                Error::new(
                    ErrorKind::Validation,
                    "invalid_region",
                    "Strict affinity requires a preferred region.",
                )
            })?;
            let regional = format!(
                "https://{}.api.terrascale.tech",
                region.to_ascii_lowercase()
            );
            if api_base_url
                .as_deref()
                .is_some_and(|origin| origin != DEFAULT_API_BASE_URL && origin != regional)
            {
                return Err(Error::new(
                    ErrorKind::Validation,
                    "invalid_base_url",
                    "Strict affinity requires the default API or selected regional origin.",
                ));
            }
            api_base_url = Some(regional);
        }
        let auth_base_url = self
            .auth_base_url
            .as_deref()
            .map(normalize_base_url)
            .transpose()?;
        let discovery_base_url = self
            .discovery_base_url
            .as_deref()
            .map(normalize_base_url)
            .transpose()?
            .unwrap_or_else(|| ATLAS_BASE_URL.to_owned());
        Ok(ClientConfig {
            api_base_url,
            discovery_base_url,
            api_key,
            auth_base_url,
            auth_reverse_proxy: self.auth_reverse_proxy,
            auth_api_key,
            tenant_id,
            max_retry_attempts: self.max_retry_attempts,
            retry_delay_ms,
            region_rtt: self.region_rtt,
            preferred_region: self.preferred_region,
            region_affinity: self.region_affinity,
            discovery_timeout,
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
