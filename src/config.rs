//! Client configuration: credentials, project scope, region pinning, retry.

use crate::error::{Error, ErrorKind, Result};
use crate::regions::{normalize_base_url, RegionAffinity, RegionCode, GLOBAL_API_BASE_URL};

/// Runtime configuration for [`crate::TerraScaleClient`].
#[derive(Debug, Clone)]
pub struct ClientConfig {
    api_base_url: String,
    api_key: Option<String>,
    project_id: Option<String>,
    preferred_region: Option<RegionCode>,
    region_affinity: RegionAffinity,
    max_retry_attempts: u8,
    retry_delay_ms: u32,
}

impl ClientConfig {
    /// Start building a config.
    #[must_use]
    pub fn builder() -> ClientConfigBuilder {
        ClientConfigBuilder::default()
    }

    /// Global (first-contact) base URL.
    #[must_use]
    pub fn api_base_url(&self) -> &str {
        &self.api_base_url
    }

    /// Configured project scope, if any.
    #[must_use]
    pub fn project_id(&self) -> Option<&str> {
        self.project_id.as_deref()
    }

    /// Preferred region for pinned REST traffic.
    #[must_use]
    pub fn preferred_region(&self) -> Option<RegionCode> {
        self.preferred_region
    }

    /// Failover or strict regional affinity.
    #[must_use]
    pub fn region_affinity(&self) -> RegionAffinity {
        self.region_affinity
    }

    /// Maximum automatic retries for safe transient failures (0-8).
    #[must_use]
    pub fn max_retry_attempts(&self) -> u8 {
        self.max_retry_attempts
    }

    /// Base delay between retries in milliseconds.
    #[must_use]
    pub fn retry_delay_ms(&self) -> u32 {
        self.retry_delay_ms
    }

    pub(crate) fn api_key(&self) -> Option<&str> {
        self.api_key.as_deref()
    }
}

/// Builder for [`ClientConfig`].
#[derive(Debug, Default)]
pub struct ClientConfigBuilder {
    api_base_url: Option<String>,
    api_key: Option<String>,
    project_id: Option<String>,
    preferred_region: Option<RegionCode>,
    region_affinity: RegionAffinity,
    max_retry_attempts: u8,
    retry_delay_ms: Option<u32>,
}

impl ClientConfigBuilder {
    /// Override the global entry point (default `https://api.terrascale.tech`).
    #[must_use]
    pub fn api_base_url(mut self, base_url: impl Into<String>) -> Self {
        self.api_base_url = Some(base_url.into());
        self
    }

    /// API key sent as `Authorization: Bearer`.
    #[must_use]
    pub fn api_key(mut self, api_key: impl Into<String>) -> Self {
        self.api_key = Some(api_key.into());
        self
    }

    /// Default project scope for project-scoped operations.
    #[must_use]
    pub fn project_id(mut self, project_id: impl Into<String>) -> Self {
        self.project_id = Some(project_id.into());
        self
    }

    /// Preferred region for pinned REST traffic.
    #[must_use]
    pub fn preferred_region(mut self, region: RegionCode) -> Self {
        self.preferred_region = Some(region);
        self
    }

    /// Failover (default) or strict regional affinity.
    #[must_use]
    pub fn region_affinity(mut self, affinity: RegionAffinity) -> Self {
        self.region_affinity = affinity;
        self
    }

    /// Maximum automatic retries for safe transient failures (0-8).
    #[must_use]
    pub fn max_retry_attempts(mut self, attempts: u8) -> Self {
        self.max_retry_attempts = attempts;
        self
    }

    /// Base delay between retries in milliseconds (0-30_000).
    #[must_use]
    pub fn retry_delay_ms(mut self, delay_ms: u32) -> Self {
        self.retry_delay_ms = Some(delay_ms);
        self
    }

    /// Validate and build the config.
    pub fn build(self) -> Result<ClientConfig> {
        if self.max_retry_attempts > 8 {
            return Err(Error::new(
                ErrorKind::Validation,
                "invalid_retry",
                "maxRetryAttempts must be an integer between 0 and 8.",
            ));
        }
        let retry_delay_ms = self.retry_delay_ms.unwrap_or(250);
        if retry_delay_ms > 30_000 {
            return Err(Error::new(
                ErrorKind::Validation,
                "invalid_retry",
                "retryDelayMs must be between 0 and 30000.",
            ));
        }
        if self.region_affinity == RegionAffinity::Strict && self.preferred_region.is_none() {
            return Err(Error::new(
                ErrorKind::Validation,
                "region_required",
                "Strict regional affinity requires a preferredRegion.",
            ));
        }
        let api_base_url =
            normalize_base_url(self.api_base_url.as_deref().unwrap_or(GLOBAL_API_BASE_URL))?;
        Ok(ClientConfig {
            api_base_url,
            api_key: self.api_key,
            project_id: self.project_id,
            preferred_region: self.preferred_region,
            region_affinity: self.region_affinity,
            max_retry_attempts: self.max_retry_attempts,
            retry_delay_ms,
        })
    }
}
