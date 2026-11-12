//! Isolated ts-auth credentials and bounded read retries.

use crate::error::{Error, ErrorKind, Result};
use crate::origin::{service_origin, AUTH};

#[derive(Clone)]
pub struct ClientConfig {
    auth_base_url: Option<String>,
    max_retry_attempts: u8,
    retry_delay_ms: u32,
}

impl std::fmt::Debug for ClientConfig {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ClientConfig")
            .field("auth_base_url", &self.auth_base_url)
            .field("max_retry_attempts", &self.max_retry_attempts)
            .field("retry_delay_ms", &self.retry_delay_ms)
            .finish()
    }
}

impl ClientConfig {
    #[must_use]
    pub fn builder() -> ClientConfigBuilder {
        ClientConfigBuilder::default()
    }
    #[must_use]
    pub fn auth_base_url(&self) -> Option<&str> {
        self.auth_base_url.as_deref()
    }
    #[must_use]
    pub fn max_retry_attempts(&self) -> u8 {
        self.max_retry_attempts
    }
    #[must_use]
    pub fn retry_delay_ms(&self) -> u32 {
        self.retry_delay_ms
    }
}

#[derive(Default)]
pub struct ClientConfigBuilder {
    auth_base_url: Option<String>,
    max_retry_attempts: u8,
    retry_delay_ms: Option<u32>,
}

impl std::fmt::Debug for ClientConfigBuilder {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ClientConfigBuilder")
            .field("auth_base_url", &self.auth_base_url)
            .field("max_retry_attempts", &self.max_retry_attempts)
            .field("retry_delay_ms", &self.retry_delay_ms)
            .finish()
    }
}

impl ClientConfigBuilder {
    #[must_use]
    pub fn auth_base_url(mut self, value: impl Into<String>) -> Self {
        self.auth_base_url = Some(value.into());
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
        let auth_base_url = self
            .auth_base_url
            .as_deref()
            .map(|raw| {
                service_origin(raw, "auth_base_url", &AUTH)
                    .map(|url| url.origin().ascii_serialization())
                    .map_err(|message| {
                        Error::new(ErrorKind::Validation, "invalid_auth_base_url", message)
                    })
            })
            .transpose()?;
        Ok(ClientConfig {
            auth_base_url,
            max_retry_attempts: self.max_retry_attempts,
            retry_delay_ms,
        })
    }
}
