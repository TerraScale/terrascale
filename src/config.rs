//! Isolated ts-auth credentials and bounded read retries.

use crate::error::{Error, ErrorKind, Result};
use crate::regions::normalize_base_url;

#[derive(Clone)]
pub struct ClientConfig {
    auth_base_url: Option<String>,
    auth_api_key: Option<String>,
    max_retry_attempts: u8,
    retry_delay_ms: u32,
}

impl std::fmt::Debug for ClientConfig {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ClientConfig")
            .field("auth_base_url", &self.auth_base_url)
            .field("auth_api_key", &"[REDACTED]")
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
    pub(crate) fn auth_api_key(&self) -> Option<&str> {
        self.auth_api_key.as_deref()
    }
}

#[derive(Default)]
pub struct ClientConfigBuilder {
    auth_base_url: Option<String>,
    auth_api_key: Option<String>,
    max_retry_attempts: u8,
    retry_delay_ms: Option<u32>,
}

impl std::fmt::Debug for ClientConfigBuilder {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ClientConfigBuilder")
            .field("auth_base_url", &self.auth_base_url)
            .field("auth_api_key", &"[REDACTED]")
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
    #[must_use]
    pub fn auth_api_key(mut self, value: impl Into<String>) -> Self {
        self.auth_api_key = Some(value.into());
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
        let auth_api_key = self
            .auth_api_key
            .as_deref()
            .map(normalize_api_key)
            .transpose()?;
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
            .map(normalize_base_url)
            .transpose()?;
        Ok(ClientConfig {
            auth_base_url,
            auth_api_key,
            max_retry_attempts: self.max_retry_attempts,
            retry_delay_ms,
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
