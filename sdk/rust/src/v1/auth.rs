//! First-party project API-key exchange; credentials never enter diagnostics.
use std::{
    sync::{
        atomic::{AtomicBool, Ordering},
        Arc,
    },
    time::Duration,
};

use base64::{engine::general_purpose::STANDARD, Engine};
use futures_util::StreamExt;
use reqwest::header::{HeaderValue, AUTHORIZATION, RETRY_AFTER};
use serde::Deserialize;
use tokio::{sync::Mutex, time::Instant};

use super::client::{backoff, RetryPolicy};
use super::errors::{Error, Result};

pub(super) enum Credential {
    Bearer(String),
    ApiKey(String),
}

pub(super) enum Auth {
    Static(Arc<HeaderValue>),
    Key(Arc<KeyAuth>),
}

pub(super) struct KeyAuth {
    pub(super) basic: HeaderValue,
    pub(super) project: String,
    pub(super) endpoint: String,
    cached: Mutex<Option<Cached>>,
    /// Set while one background task renews a still-valid token.
    renewing: AtomicBool,
}

pub(super) enum Cached {
    Token {
        header: Arc<HeaderValue>,
        /// From here on one background task renews the token while callers
        /// keep using it, so no request waits for the exchange.
        refresh_at: Instant,
        /// From here on callers wait for a fresh token.
        expires_at: Instant,
    },
    Refused {
        code: &'static str,
        message: Option<String>,
    },
}

fn refusal(code: &'static str, message: Option<String>) -> Error {
    match code {
        "key_revoked" => Error::KeyRevoked { message },
        "key_expired" => Error::KeyExpired { message },
        _ => exchange_error(Some(401), "invalid_key"),
    }
}

fn header(scheme: &str, value: &str) -> Result<HeaderValue> {
    let mut header = HeaderValue::from_str(&format!("{scheme} {value}"))
        .map_err(|_| Error::InvalidInput("invalid credential header".into()))?;
    header.set_sensitive(true);
    Ok(header)
}

/// A failed background renewal is tried again after this pause, so an
/// unavailable issuer sees at most one renewal per second per client.
const RENEW_PAUSE: Duration = Duration::from_secs(1);

impl Auth {
    pub(super) fn new(credential: Credential, project: Option<&str>) -> Result<Self> {
        match credential {
            Credential::Bearer(token) => {
                if token.is_empty() || !token.bytes().all(|b| b.is_ascii_graphic()) {
                    return Err(Error::InvalidInput("invalid bearer token".into()));
                }
                Ok(Self::Static(Arc::new(header("Bearer", &token)?)))
            }
            Credential::ApiKey(key) => {
                let project = project
                    .ok_or_else(|| Error::InvalidInput("API keys require a project".into()))?;
                if !key.is_ascii()
                    || key.len() != 79
                    || !super::ids::valid_entity_id(&key[..26])
                    || key.as_bytes()[26] != b'_'
                    || !key.as_bytes()[27..]
                        .iter()
                        .all(|b| b"0123456789abcdefghjkmnpqrstvwxyz".contains(b))
                {
                    return Err(Error::InvalidInput("invalid TerraScale API key".into()));
                }
                let basic = header("Basic", &STANDARD.encode(format!("{}:{key}", &key[..26])))?;
                Ok(Self::Key(Arc::new(KeyAuth {
                    basic,
                    project: project.to_owned(),
                    endpoint: "https://auth.terrascale.tech/terrascale/oauth/token".into(),
                    cached: Mutex::new(None),
                    renewing: AtomicBool::new(false),
                })))
            }
        }
    }

    pub(super) async fn reject(&self, code: &'static str, message: Option<String>) {
        if let Self::Key(key) = self {
            *key.cached.lock().await = Some(Cached::Refused { code, message });
        }
    }

    pub(super) fn refreshable(&self) -> bool {
        matches!(self, Self::Key(_))
    }

    pub(super) async fn bearer(
        &self,
        http: &reqwest::Client,
        rejected: Option<&Arc<HeaderValue>>,
        retry: &RetryPolicy,
    ) -> Result<Arc<HeaderValue>> {
        let key = match self {
            Self::Static(header) => return Ok(header.clone()),
            Self::Key(key) => key,
        };
        // Hold the lock through a blocking exchange so clones share one
        // refresh. Identity prevents a late 401 from invalidating a newer
        // (even identical) token.
        let mut cached = key.cached.lock().await;
        if let Some(Cached::Refused { code, message }) = cached.as_ref() {
            return Err(refusal(code, message.clone()));
        }
        if let Some(Cached::Token {
            header,
            refresh_at,
            expires_at,
        }) = cached.as_ref()
        {
            let now = Instant::now();
            if now < *expires_at && rejected.is_none_or(|old| !Arc::ptr_eq(old, header)) {
                if now >= *refresh_at {
                    key.renew_in_background(http);
                }
                return Ok(header.clone());
            }
        }
        *cached = None;
        let fresh = key.exchange(http, retry).await?;
        let result = match &fresh {
            Cached::Token { header, .. } => Ok(header.clone()),
            Cached::Refused { code, message } => Err(refusal(code, message.clone())),
        };
        *cached = Some(fresh);
        result
    }
}

impl KeyAuth {
    /// Starts at most one background renewal. The caller keeps using the
    /// still-valid token; a failed renewal leaves it in place until expiry.
    fn renew_in_background(self: &Arc<Self>, http: &reqwest::Client) {
        let Ok(runtime) = tokio::runtime::Handle::try_current() else {
            return;
        };
        if self.renewing.swap(true, Ordering::AcqRel) {
            return;
        }
        let (key, http) = (self.clone(), http.clone());
        runtime.spawn(async move {
            let fresh = key.exchange(&http, &RetryPolicy::none()).await;
            let mut cached = key.cached.lock().await;
            match (fresh, cached.as_mut()) {
                // A permanent refusal decided elsewhere stays authoritative.
                (_, Some(Cached::Refused { .. })) => {}
                (Ok(fresh), _) => *cached = Some(fresh),
                (
                    Err(_),
                    Some(Cached::Token {
                        refresh_at,
                        expires_at,
                        ..
                    }),
                ) => *refresh_at = (Instant::now() + RENEW_PAUSE).min(*expires_at),
                (Err(_), None) => {}
            }
            key.renewing.store(false, Ordering::Release);
        });
    }

    /// Exchanges the key, retrying transport failures, `429` and gateway
    /// `502`/`503`/`504` refusals under `retry`. Minting a token has no other
    /// effect, so the exchange is safe to repeat even ahead of a mutation.
    /// `Ok(Cached::Refused)` reports a permanent `401`.
    async fn exchange(&self, http: &reqwest::Client, retry: &RetryPolicy) -> Result<Cached> {
        let mut attempt = 0;
        loop {
            let (error, retry_after) = match self.exchange_once(http).await {
                Ok(fresh) => return Ok(fresh),
                Err(failure) => failure,
            };
            let transient = matches!(error.status(), None | Some(429 | 502 | 503 | 504))
                && error.code()
                    == Some(if error.status().is_some() {
                        "token_exchange_refused"
                    } else {
                        "token_transport_error"
                    });
            if !transient || attempt >= retry.max_retries {
                return Err(error);
            }
            let delay = match retry_after {
                Some(delay) => delay.min(retry.max_retry_after),
                None => backoff(retry, attempt),
            };
            tokio::time::sleep(delay).await;
            attempt += 1;
        }
    }

    async fn exchange_once(
        &self,
        http: &reqwest::Client,
    ) -> std::result::Result<Cached, (Error, Option<Duration>)> {
        let started = Instant::now();
        let response = http
            .post(self.endpoint.clone())
            .header(AUTHORIZATION, self.basic.clone())
            .header("Accept", "application/json")
            .form(&[
                ("grant_type", "client_credentials"),
                ("project", self.project.as_str()),
            ])
            .send()
            .await
            .map_err(|error| (transport_error(&error), None))?;
        let status = response.status().as_u16();
        let request_id = response
            .headers()
            .get("X-Request-ID")
            .and_then(|value| value.to_str().ok())
            .filter(|id| {
                !id.is_empty()
                    && id.len() <= 128
                    && id
                        .bytes()
                        .all(|b| b.is_ascii_alphanumeric() || b"-_.:".contains(&b))
            })
            .map(str::to_owned);
        let retry_after = response
            .headers()
            .get(RETRY_AFTER)
            .and_then(|value| value.to_str().ok())
            .and_then(|value| value.trim().parse::<u64>().ok())
            .map(Duration::from_secs);
        let mut body = Vec::new();
        let mut stream = response.bytes_stream();
        while let Some(chunk) = stream.next().await {
            let Ok(chunk) = chunk else {
                let mut error = transport_error_with(Some(status), "response body interrupted");
                set_request_id(&mut error, request_id);
                return Err((error, None));
            };
            if body.len() + chunk.len() > 64 << 10 {
                let mut error = exchange_error(
                    Some(status),
                    if status == 200 {
                        "invalid_token_response"
                    } else {
                        "token_exchange_refused"
                    },
                );
                set_request_id(&mut error, request_id);
                return Err((error, retry_after));
            }
            body.extend_from_slice(&chunk);
        }
        if status != 200 {
            if status == 401 {
                return Ok(Cached::Refused {
                    code: "invalid_key",
                    message: None,
                });
            }
            let mut error = exchange_error(Some(status), "token_exchange_refused");
            if let Error::TokenExchange {
                reason,
                request_id: id,
                ..
            } = &mut error
            {
                *reason = oauth_reason(&body);
                *id = request_id;
            }
            return Err((error, retry_after));
        }
        let invalid = || {
            let mut error = exchange_error(Some(status), "invalid_token_response");
            set_request_id(&mut error, request_id.clone());
            (error, None)
        };
        #[derive(Deserialize)]
        struct Grant {
            access_token: String,
            token_type: String,
            expires_in: u64,
        }
        let grant: Grant = serde_json::from_slice(&body).map_err(|_| invalid())?;
        let token = grant.access_token.trim_end_matches('=');
        if token.is_empty()
            || !token
                .bytes()
                .all(|b| b.is_ascii_alphanumeric() || b"-._~+/".contains(&b))
            || !grant.token_type.eq_ignore_ascii_case("Bearer")
            || grant.expires_in == 0
        {
            return Err(invalid());
        }
        let lifetime = Duration::from_secs(grant.expires_in);
        // Renew in the background from the later of half-life and 60 s before
        // expiry; block only in the last quarter (at most 10 s) of the lifetime.
        let refresh_at = started
            .checked_add(lifetime - Duration::from_secs(60).min(lifetime / 2))
            .filter(|at| *at > Instant::now())
            .ok_or_else(invalid)?;
        let expires_at = started
            .checked_add(lifetime - Duration::from_secs(10).min(lifetime / 4))
            .ok_or_else(invalid)?
            .max(refresh_at);
        let header = Arc::new(header("Bearer", &grant.access_token).map_err(|_| invalid())?);
        Ok(Cached::Token {
            header,
            refresh_at,
            expires_at,
        })
    }
}

/// Maps the RFC 6749 / RFC 8707 `error` member to SDK-owned text. Free-form
/// upstream text (`error_description`) is never echoed.
fn oauth_reason(body: &[u8]) -> Option<&'static str> {
    #[derive(Deserialize)]
    struct OAuthError<'a> {
        #[serde(borrow)]
        error: &'a str,
    }
    let error = serde_json::from_slice::<OAuthError<'_>>(body).ok()?.error;
    Some(match error {
        "invalid_target" => "invalid_target: the API key does not reach this project",
        "invalid_client" => "invalid_client: the API key was not accepted",
        "invalid_scope" => "invalid_scope: the API key may not use the requested scope",
        "invalid_request" => "invalid_request: the issuer rejected the token request",
        "invalid_grant" => "invalid_grant: the issuer rejected the grant",
        "unauthorized_client" => "unauthorized_client: the API key may not use this grant",
        "unsupported_grant_type" => "unsupported_grant_type: the issuer refused client_credentials",
        "temporarily_unavailable" => "temporarily_unavailable: the issuer is overloaded",
        "server_error" => "server_error: the issuer failed",
        _ => return None,
    })
}

fn transport_error(error: &reqwest::Error) -> Error {
    transport_error_with(
        None,
        if error.is_timeout() {
            "timed out"
        } else if error.is_connect() {
            "connection failed"
        } else {
            "request failed"
        },
    )
}

fn transport_error_with(status: Option<u16>, reason: &'static str) -> Error {
    let mut error = exchange_error(status, "token_transport_error");
    if let Error::TokenExchange { reason: slot, .. } = &mut error {
        *slot = Some(reason);
    }
    error
}

fn set_request_id(error: &mut Error, id: Option<String>) {
    if let Error::TokenExchange { request_id, .. } = error {
        *request_id = id;
    }
}

fn exchange_error(status: Option<u16>, code: &'static str) -> Error {
    Error::TokenExchange {
        status,
        code,
        reason: None,
        request_id: None,
    }
}
