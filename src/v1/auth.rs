//! First-party project API-key exchange; credentials never enter diagnostics.
use std::{sync::Arc, time::Duration};

use base64::{engine::general_purpose::STANDARD, Engine};
use futures_util::StreamExt;
use reqwest::header::{HeaderValue, AUTHORIZATION};
use serde::Deserialize;
use tokio::{sync::Mutex, time::Instant};

use super::errors::{Error, Result};

pub(super) enum Credential {
    Bearer(String),
    ApiKey(String),
}

pub(super) enum Auth {
    Static(Arc<HeaderValue>),
    Key {
        basic: HeaderValue,
        project: String,
        endpoint: String,
        cached: Mutex<Option<Cached>>,
    },
}

pub(super) enum Cached {
    Token {
        header: Arc<HeaderValue>,
        refresh_at: Instant,
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
                Ok(Self::Key {
                    basic,
                    project: project.to_owned(),
                    endpoint: "https://auth.terrascale.tech/terrascale/oauth/token".into(),
                    cached: Mutex::new(None),
                })
            }
        }
    }

    pub(super) async fn reject(&self, code: &'static str, message: Option<String>) {
        if let Self::Key { cached, .. } = self {
            *cached.lock().await = Some(Cached::Refused { code, message });
        }
    }

    pub(super) fn refreshable(&self) -> bool {
        matches!(self, Self::Key { .. })
    }

    pub(super) async fn bearer(
        &self,
        http: &reqwest::Client,
        rejected: Option<&Arc<HeaderValue>>,
    ) -> Result<Arc<HeaderValue>> {
        let (basic, project, endpoint, cached) = match self {
            Self::Static(header) => return Ok(header.clone()),
            Self::Key {
                basic,
                project,
                endpoint,
                cached,
            } => (basic, project, endpoint, cached),
        };
        // Hold the lock through exchange so clones share one refresh. Identity
        // prevents a late 401 from invalidating a newer (even identical) token.
        let mut cached = cached.lock().await;
        if let Some(Cached::Refused { code, message }) = cached.as_ref() {
            return Err(refusal(code, message.clone()));
        }
        if let Some(Cached::Token { header, refresh_at }) = cached.as_ref() {
            if Instant::now() < *refresh_at && rejected.is_none_or(|old| !Arc::ptr_eq(old, header))
            {
                return Ok(header.clone());
            }
        }
        *cached = None;
        let started = Instant::now();
        let response = http
            .post(endpoint.clone())
            .header(AUTHORIZATION, basic.clone())
            .header("Accept", "application/json")
            .form(&[
                ("grant_type", "client_credentials"),
                ("project", project.as_str()),
            ])
            .send()
            .await
            .map_err(|_| exchange_error(None, "token_transport_error"))?;
        let status = Some(response.status().as_u16());
        if status != Some(200) {
            if status == Some(401) {
                *cached = Some(Cached::Refused {
                    code: "invalid_key",
                    message: None,
                });
                return Err(refusal("invalid_key", None));
            }
            return Err(exchange_error(status, "token_exchange_refused"));
        }
        let invalid = || exchange_error(status, "invalid_token_response");
        let mut body = Vec::new();
        let mut stream = response.bytes_stream();
        while let Some(chunk) = stream.next().await {
            let chunk = chunk.map_err(|_| exchange_error(status, "token_transport_error"))?;
            if body.len() + chunk.len() > 64 << 10 {
                return Err(invalid());
            }
            body.extend_from_slice(&chunk);
        }
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
        let refresh_at = started
            .checked_add(lifetime - Duration::from_secs(60).min(lifetime / 2))
            .filter(|at| *at > Instant::now())
            .ok_or_else(invalid)?;
        let header = Arc::new(header("Bearer", &grant.access_token).map_err(|_| invalid())?);
        *cached = Some(Cached::Token {
            header: header.clone(),
            refresh_at,
        });
        Ok(header)
    }
}

fn exchange_error(status: Option<u16>, code: &'static str) -> Error {
    Error::TokenExchange { status, code }
}
