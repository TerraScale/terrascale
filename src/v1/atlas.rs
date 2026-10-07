//! Anonymous Atlas discovery and verified, bounded session routing.

use std::collections::HashMap;
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use base64::{engine::general_purpose::URL_SAFE_NO_PAD, Engine as _};
use futures_util::StreamExt;
use reqwest::Url;
use ring::signature::{UnparsedPublicKey, ECDSA_P256_SHA256_FIXED};
use serde::{de::DeserializeOwned, Deserialize};
use tokio::sync::Mutex;
use uuid::Uuid;

use super::{is_valid_name, Error, Result};

const MAX_BODY: usize = 64 << 10;
const MAX_SCOPES: usize = 128;
const TIMEOUT: Duration = Duration::from_secs(3);
const KEY_TTL: Duration = Duration::from_secs(300);

type Scope = (Uuid, Option<String>);

#[derive(Clone, Debug)]
pub(crate) struct ResolvedEndpoint {
    pub(crate) api: Url,
    pub(crate) region: String,
}

struct Session {
    endpoint: ResolvedEndpoint,
    clock: Lease,
}

struct Lease {
    wall: SystemTime,
    monotonic: Instant,
    expires: SystemTime,
}

impl Lease {
    fn valid(&self, wall: SystemTime, monotonic: Instant) -> bool {
        wall.duration_since(self.wall).is_ok()
            && wall < self.expires
            && monotonic
                .checked_duration_since(self.monotonic)
                .is_some_and(|elapsed| {
                    self.expires
                        .duration_since(self.wall)
                        .is_ok_and(|ttl| elapsed < ttl)
                })
    }
}

#[derive(Default)]
struct State {
    sessions: HashMap<Scope, Session>,
    keys: Option<(Jwks, Lease)>,
}

pub(crate) struct AtlasResolver {
    http: reqwest::Client,
    atlas: Url,
    state: Mutex<State>,
}

impl AtlasResolver {
    /// `http` must have redirects disabled and no default credential headers.
    pub(crate) fn new(http: reqwest::Client, atlas: Url) -> Self {
        Self {
            http,
            atlas,
            state: Mutex::new(State::default()),
        }
    }

    pub(crate) async fn resolve(
        &self,
        project: Uuid,
        database: Option<&str>,
        fresh: bool,
    ) -> Result<ResolvedEndpoint> {
        if database.is_some_and(|name| !is_valid_name(name)) {
            return Err(Error::InvalidInput("invalid Atlas database name".into()));
        }
        if !bare_origin(&self.atlas)
            || !(self.atlas.scheme() == "https"
                || (self.atlas.scheme() == "http" && loopback(&self.atlas)))
        {
            return Err(Error::InvalidInput(
                "Atlas must be an HTTPS or explicit loopback origin".into(),
            ));
        }
        let scope = (project, database.map(str::to_owned));
        // ponytail: serialize discovery per client; use per-scope locks if contention matters.
        let mut state = self.state.lock().await;
        if !fresh {
            if let Some(session) = state
                .sessions
                .get(&scope)
                .filter(|s| s.clock.valid(SystemTime::now(), Instant::now()))
            {
                return Ok(session.endpoint.clone());
            }
        }
        let expired = state
            .sessions
            .get(&scope)
            .is_some_and(|s| !s.clock.valid(SystemTime::now(), Instant::now()));
        let result = self
            .fetch(&mut state, project, database, fresh || expired)
            .await;
        let session = match result {
            Ok(session) => session,
            // An unreachable Atlas must not shorten an already verified session.
            Err(error @ Error::Transport(_)) => {
                if let Some(session) = state
                    .sessions
                    .get(&scope)
                    .filter(|s| s.clock.valid(SystemTime::now(), Instant::now()))
                {
                    return Ok(session.endpoint.clone());
                }
                return Err(error);
            }
            Err(error) => return Err(error),
        };
        state
            .sessions
            .retain(|_, s| s.clock.valid(SystemTime::now(), Instant::now()));
        if state.sessions.len() >= MAX_SCOPES && !state.sessions.contains_key(&scope) {
            if let Some(oldest) = state
                .sessions
                .iter()
                .min_by_key(|(_, s)| s.clock.monotonic)
                .map(|(scope, _)| scope.clone())
            {
                state.sessions.remove(&oldest);
            }
        }
        let endpoint = session.endpoint.clone();
        state.sessions.insert(scope, session);
        Ok(endpoint)
    }

    async fn fetch(
        &self,
        state: &mut State,
        project: Uuid,
        database: Option<&str>,
        fresh: bool,
    ) -> Result<Session> {
        let wall = SystemTime::now();
        let monotonic = Instant::now();
        let mut url = self.atlas.join("/v1/resolve").map_err(decode)?;
        url.query_pairs_mut()
            .append_pair("project", &project.to_string());
        if let Some(database) = database {
            url.query_pairs_mut().append_pair("database", database);
        }
        if fresh {
            url.query_pairs_mut().append_pair("fresh", "1");
        }
        let response: ResolveResponse = self.get(url).await?;
        if response.v != 1
            || response.project != project.to_string()
            || response.database.as_deref() != database
            || !valid_region(&response.region)
            || !matches!(response.health.as_str(), "ok" | "down" | "unknown")
            || !matches!(response.placement.as_str(), "known" | "unknown")
        {
            return Err(decode(
                "Atlas response does not match the requested scope or contract",
            ));
        }
        let api = destination(&response.api, loopback(&self.atlas))?;
        destination(&response.auth, loopback(&self.atlas))?;
        let parts: Vec<_> = response.pin.split('.').collect();
        if parts.len() != 3 {
            return Err(decode("Atlas pin is not a compact JWS"));
        }
        let header: PinHeader = serde_json::from_slice(&unbase64(parts[0])?).map_err(decode)?;
        if header.alg != "ES256" || header.typ != "JWT" || header.kid.is_empty() {
            return Err(decode("Atlas pin header is not ES256 JWT"));
        }
        let keys_current = state
            .keys
            .as_ref()
            .is_some_and(|(_, lease)| lease.valid(SystemTime::now(), Instant::now()));
        let known = state
            .keys
            .as_ref()
            .is_some_and(|(keys, _)| keys.keys.iter().any(|key| key.kid == header.kid));
        if !keys_current || !known {
            let key_wall = SystemTime::now();
            let key_monotonic = Instant::now();
            let keys: Jwks = self
                .get(self.atlas.join("/v1/keys").map_err(decode)?)
                .await?;
            if keys.keys.is_empty() || keys.keys.len() > 16 {
                return Err(decode("Atlas JWKS key count is invalid"));
            }
            state.keys = Some((
                keys,
                Lease {
                    wall: key_wall,
                    monotonic: key_monotonic,
                    expires: key_wall + KEY_TTL,
                },
            ));
        }
        let keys = &state
            .keys
            .as_ref()
            .ok_or_else(|| decode("Atlas JWKS is absent"))?
            .0
            .keys;
        let mut matching = keys.iter().filter(|key| key.kid == header.kid);
        let key = matching
            .next()
            .ok_or_else(|| decode("Atlas pin signing key is unknown"))?;
        if matching.next().is_some()
            || key.kty != "EC"
            || key.crv != "P-256"
            || key.alg != "ES256"
            || key.r#use != "sig"
        {
            return Err(decode("Atlas pin signing key is invalid or ambiguous"));
        }
        let x = unbase64(&key.x)?;
        let y = unbase64(&key.y)?;
        if x.len() != 32 || y.len() != 32 {
            return Err(decode("Atlas P-256 key coordinates are invalid"));
        }
        let mut public = vec![4];
        public.extend(x);
        public.extend(y);
        let signature = unbase64(parts[2])?;
        UnparsedPublicKey::new(&ECDSA_P256_SHA256_FIXED, public)
            .verify(format!("{}.{}", parts[0], parts[1]).as_bytes(), &signature)
            .map_err(|_| decode("Atlas pin signature is invalid"))?;
        let claims: PinClaims = serde_json::from_slice(&unbase64(parts[1])?).map_err(decode)?;
        let now = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map_err(decode)?;
        if claims.iss != "atlas.terrascale.tech"
            || claims.project != response.project
            || claims.database != response.database
            || claims.region != response.region
            || claims.api != response.api
            || claims.exp != response.expires
            || claims.iat > now.as_secs()
            || claims.exp <= now.as_secs()
            || claims.exp <= claims.iat
            || claims.exp - claims.iat > 600
        {
            return Err(decode("Atlas pin claims, expiry, or lifetime are invalid"));
        }
        let expires = UNIX_EPOCH
            .checked_add(Duration::from_secs(claims.exp))
            .ok_or_else(|| decode("Atlas pin expiry overflows"))?;
        let clock = Lease {
            wall,
            monotonic,
            expires,
        };
        if !clock.valid(SystemTime::now(), Instant::now()) {
            return Err(decode("Atlas pin expired during verification"));
        }
        Ok(Session {
            endpoint: ResolvedEndpoint {
                api,
                region: response.region,
            },
            clock,
        })
    }

    async fn get<T: DeserializeOwned>(&self, url: Url) -> Result<T> {
        // The shared client owns redirect policy and TLS roots. Atlas receives no TerraBase key.
        let response = self
            .http
            .get(url.clone())
            .timeout(TIMEOUT)
            .send()
            .await
            .map_err(|e| Error::Transport(e.to_string()))?;
        if response.url() != &url {
            return Err(decode("Atlas followed a redirect"));
        }
        let status = response.status();
        if response
            .content_length()
            .is_some_and(|len| len > MAX_BODY as u64)
        {
            return Err(decode("Atlas response exceeds 64 KiB"));
        }
        let mut body = Vec::new();
        let mut stream = response.bytes_stream();
        while let Some(chunk) = stream.next().await {
            let chunk = chunk.map_err(|e| Error::Transport(e.to_string()))?;
            if chunk.len() > MAX_BODY - body.len() {
                return Err(decode("Atlas response exceeds 64 KiB"));
            }
            body.extend_from_slice(&chunk);
        }
        if !status.is_success() {
            let value: serde_json::Value = serde_json::from_slice(&body).unwrap_or_default();
            return Err(Error::Api {
                status: status.as_u16(),
                code: value["error"]["code"]
                    .as_str()
                    .unwrap_or("atlas_error")
                    .into(),
                message: value["error"]["message"].as_str().map(str::to_owned),
                op: None,
                details: None,
            });
        }
        serde_json::from_slice(&body).map_err(decode)
    }
}

fn decode(error: impl std::fmt::Display) -> Error {
    Error::Decode(error.to_string())
}
fn unbase64(text: &str) -> Result<Vec<u8>> {
    URL_SAFE_NO_PAD.decode(text).map_err(decode)
}

fn loopback(url: &Url) -> bool {
    match url.host() {
        Some(url::Host::Domain(host)) => host == "localhost",
        Some(url::Host::Ipv4(ip)) => ip.is_loopback(),
        Some(url::Host::Ipv6(ip)) => ip.is_loopback(),
        None => false,
    }
}

fn bare_origin(url: &Url) -> bool {
    url.host().is_some()
        && url.username().is_empty()
        && url.password().is_none()
        && url.path() == "/"
        && url.query().is_none()
        && url.fragment().is_none()
}

fn destination(raw: &str, dev: bool) -> Result<Url> {
    let url = Url::parse(raw).map_err(decode)?;
    let production = url.scheme() == "https"
        && url.port().is_none()
        && url
            .host_str()
            .is_some_and(|host| host == "terrascale.tech" || host.ends_with(".terrascale.tech"));
    if raw.len() > 256
        || raw.trim() != raw
        || !bare_origin(&url)
        || !(production || (dev && loopback(&url) && matches!(url.scheme(), "http" | "https")))
        || raw.contains('\\')
        || raw.chars().any(char::is_control)
    {
        return Err(decode("Atlas destination is not an allowed bare origin"));
    }
    Ok(url)
}

fn valid_region(region: &str) -> bool {
    !region.is_empty()
        && region.len() <= 63
        && region.as_bytes()[0].is_ascii_lowercase()
        && !region.ends_with('-')
        && region
            .bytes()
            .all(|b| b.is_ascii_lowercase() || b.is_ascii_digit() || b == b'-')
}

// deserialize_with makes the nullable database member required, rather than silently accepting absence.
fn database<'de, D: serde::Deserializer<'de>>(
    deserializer: D,
) -> std::result::Result<Option<String>, D::Error> {
    Option::deserialize(deserializer)
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct ResolveResponse {
    v: u8,
    project: String,
    #[serde(deserialize_with = "database")]
    database: Option<String>,
    region: String,
    api: String,
    auth: String,
    health: String,
    placement: String,
    #[serde(rename = "stale")]
    _stale: bool,
    pin: String,
    expires: u64,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct PinHeader {
    alg: String,
    typ: String,
    kid: String,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct PinClaims {
    iss: String,
    project: String,
    #[serde(deserialize_with = "database")]
    database: Option<String>,
    region: String,
    api: String,
    iat: u64,
    exp: u64,
}

#[derive(Deserialize)]
struct Jwks {
    keys: Vec<Jwk>,
}

#[derive(Deserialize)]
struct Jwk {
    kty: String,
    crv: String,
    alg: String,
    r#use: String,
    kid: String,
    x: String,
    y: String,
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn lease_expires_with_either_clock_and_rejects_wall_regression() {
        let wall = UNIX_EPOCH + Duration::from_secs(1000);
        let monotonic = Instant::now();
        let lease = Lease {
            wall,
            monotonic,
            expires: wall + Duration::from_secs(600),
        };
        assert!(lease.valid(
            wall + Duration::from_secs(599),
            monotonic + Duration::from_secs(599)
        ));
        assert!(!lease.valid(wall + Duration::from_secs(600), monotonic));
        assert!(!lease.valid(wall, monotonic + Duration::from_secs(600)));
        assert!(!lease.valid(wall - Duration::from_secs(1), monotonic));
    }

    #[test]
    fn production_rejects_loopback_and_accepts_new_regional_origin() {
        assert!(destination("http://127.0.0.1:9000", false).is_err());
        assert!(destination("https://future-region.api.terrascale.tech", false).is_ok());
        assert!(destination("http://[::1]:9000", true).is_ok());
    }
}
