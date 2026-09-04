//! BYO-JWT authentication over a tenant-pinned JWKS endpoint (E-3).
//!
//! This module owns the hostile-input boundary. HTTP/TLS and cryptographic
//! implementations are supplied through JwksFetcher and JwtSignatureVerifier;
//! the fetcher contract requires certificate and hostname verification and a
//! public-IP connection policy. Keeping those effects behind traits makes the
//! parser and cache testable without making a network request.

use std::collections::{BTreeMap, BTreeSet};
use std::future::Future;
use std::net::{IpAddr, Ipv4Addr, Ipv6Addr};
use std::pin::Pin;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex, RwLock};
use std::time::Duration;

use serde::Deserialize;
use serde::de::{MapAccess, Visitor};
use serde_json::{Deserializer, Value};

/// Maximum complete bearer token size.
pub const MAX_TOKEN_BYTES: usize = 16 * 1024;
/// Maximum JWT header size after decoding.
pub const MAX_HEADER_BYTES: usize = 4 * 1024;
/// Maximum JWT payload size after decoding.
pub const MAX_PAYLOAD_BYTES: usize = 16 * 1024;
/// Maximum JWT signature size after decoding.
pub const MAX_SIGNATURE_BYTES: usize = 4 * 1024;
/// Maximum JWKS response size.
pub const MAX_JWKS_BYTES: usize = 1 * 1024 * 1024;
/// Maximum keys retained from one JWKS document.
pub const MAX_JWKS_KEYS: usize = 256;
/// Maximum cache lifetime accepted from configuration.
pub const MAX_CACHE_TTL: Duration = Duration::from_secs(24 * 60 * 60);
/// Maximum clock skew accepted from configuration.
pub const MAX_CLOCK_SKEW: u64 = 5 * 60;
/// Maximum custom claims copied to an authorization context.
pub const MAX_CONTEXT_CLAIMS: usize = 64;
/// Maximum operation names copied to an authorization context.
pub const MAX_CONTEXT_OPERATIONS: usize = 32;
const MAX_TEXT_BYTES: usize = 255;
const MAX_CLAIM_NAME_BYTES: usize = 128;
const MAX_CLAIM_VALUE_BYTES: usize = 4 * 1024;

/// JWT signature algorithms supported by the policy surface.
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub enum Algorithm {
    /// RSA PKCS#1 v1.5 with SHA-256.
    Rs256,
    /// ECDSA P-256 with SHA-256.
    Es256,
    /// Ed25519 (the JOSE EdDSA spelling).
    EdDsa,
}

impl Algorithm {
    fn parse(value: &str) -> Option<Self> {
        match value {
            "RS256" => Some(Self::Rs256),
            "ES256" => Some(Self::Es256),
            "EdDSA" => Some(Self::EdDsa),
            _ => None,
        }
    }

    /// Returns the exact JOSE algorithm spelling.
    #[must_use]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Rs256 => "RS256",
            Self::Es256 => "ES256",
            Self::EdDsa => "EdDSA",
        }
    }
}

/// A validated HTTPS JWKS endpoint. Only HTTPS, port 443, an ASCII public
/// hostname, and a path are accepted. The network adapter must resolve the
/// hostname again and refuse private or special-use addresses at connect time.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct JwksEndpoint(String);

impl JwksEndpoint {
    /// Validates a tenant-pinned JWKS endpoint.
    pub fn parse(value: &str) -> Result<Self, JwtConfigError> {
        if value.len() > 2_048 || !value.is_ascii() || value.contains(['?', '#', '\\', '%']) {
            return Err(JwtConfigError::UnsafeJwksEndpoint);
        }
        let Some(rest) = value.strip_prefix("https://") else {
            return Err(JwtConfigError::UnsafeJwksEndpoint);
        };
        let Some((authority, path)) = rest.split_once('/') else {
            return Err(JwtConfigError::UnsafeJwksEndpoint);
        };
        if authority.is_empty() || path.is_empty() || authority.contains('@') {
            return Err(JwtConfigError::UnsafeJwksEndpoint);
        }
        let host = if let Some(ipv6) = authority.strip_prefix('[') {
            let Some((literal, port)) = ipv6.split_once(']') else {
                return Err(JwtConfigError::UnsafeJwksEndpoint);
            };
            if !port.is_empty() && port != ":443" {
                return Err(JwtConfigError::UnsafeJwksEndpoint);
            }
            let address =
                literal.parse::<Ipv6Addr>().map_err(|_| JwtConfigError::UnsafeJwksEndpoint)?;
            if is_private_ip(IpAddr::V6(address)) {
                return Err(JwtConfigError::UnsafeJwksEndpoint);
            }
            format!("[{literal}]")
        } else {
            if authority.matches(':').count() > 1 {
                return Err(JwtConfigError::UnsafeJwksEndpoint);
            }
            let (host, port) =
                authority.split_once(':').map_or((authority, None), |(h, p)| (h, Some(p)));
            if port.is_some_and(|port| port != "443") || host.is_empty() || host.len() > 253 {
                return Err(JwtConfigError::UnsafeJwksEndpoint);
            }
            if let Ok(address) = host.parse::<Ipv4Addr>() {
                if is_private_ip(IpAddr::V4(address)) {
                    return Err(JwtConfigError::UnsafeJwksEndpoint);
                }
                return Err(JwtConfigError::UnsafeJwksEndpoint);
            }
            if host.eq_ignore_ascii_case("localhost")
                || host.ends_with(".localhost")
                || host.ends_with(".local")
                || host.ends_with(".internal")
                || host.ends_with(".lan")
                || host.eq_ignore_ascii_case("metadata.google.internal")
                || host
                    .bytes()
                    .any(|byte| !(byte.is_ascii_alphanumeric() || byte == b'.' || byte == b'-'))
                || host
                    .split('.')
                    .any(|label| label.is_empty() || label.starts_with('-') || label.ends_with('-'))
            {
                return Err(JwtConfigError::UnsafeJwksEndpoint);
            }
            host.to_ascii_lowercase()
        };
        if path.len() > 1_024
            || path.starts_with('/')
            || path.contains("..")
            || path.chars().any(char::is_control)
        {
            return Err(JwtConfigError::UnsafeJwksEndpoint);
        }
        Ok(Self(format!("https://{host}/{path}")))
    }

    /// Returns the canonical endpoint passed to the configured fetcher.
    #[must_use]
    pub fn as_str(&self) -> &str {
        &self.0
    }
}

fn is_private_ip(address: IpAddr) -> bool {
    match address {
        IpAddr::V4(ip) => {
            ip.is_private()
                || ip.is_loopback()
                || ip.is_link_local()
                || ip.is_unspecified()
                || ip.is_multicast()
                || (ip.octets()[0] == 100 && (64..=127).contains(&ip.octets()[1]))
                || (ip.octets()[0] == 192 && ip.octets()[1] == 0 && ip.octets()[2] == 0)
                || (ip.octets()[0] == 198 && (18..=19).contains(&ip.octets()[1]))
                || (ip.octets()[0] == 198 && ip.octets()[1] == 51 && ip.octets()[2] == 100)
                || (ip.octets()[0] == 203 && ip.octets()[1] == 0 && ip.octets()[2] == 113)
                || ip.octets()[0] >= 240
        }
        IpAddr::V6(ip) => {
            ip.is_loopback()
                || ip.is_unspecified()
                || ip.is_multicast()
                || (ip.segments()[0] & 0xfe00) == 0xfc00
                || (ip.segments()[0] & 0xffc0) == 0xfe80
        }
    }
}

/// Tenant-pinned policy for one BYO-JWT issuer.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct TenantJwtConfig {
    /// Exact tenant binding required in the token tenant claim.
    pub tenant: String,
    /// Exact issuer required in iss.
    pub issuer: String,
    /// Exact audience required in aud.
    pub audience: String,
    /// Pinned HTTPS JWKS endpoint.
    pub jwks_endpoint: JwksEndpoint,
    /// Explicitly allowed JOSE algorithms. An empty set is invalid.
    pub algorithms: BTreeSet<Algorithm>,
    /// Maximum accepted clock skew for exp, iat, and nbf.
    pub clock_skew_secs: u64,
    /// Maximum age of a JWKS cache entry.
    pub cache_ttl: Duration,
    /// Tenant configuration generation included in the authorization context.
    pub generation: u64,
}

impl TenantJwtConfig {
    /// Validates a complete production tenant policy.
    pub fn validate(&self) -> Result<(), JwtConfigError> {
        for value in [&self.tenant, &self.issuer, &self.audience] {
            if value.is_empty()
                || value.len() > MAX_TEXT_BYTES
                || value.chars().any(char::is_control)
            {
                return Err(JwtConfigError::InvalidText);
            }
        }
        if self.algorithms.is_empty() || self.clock_skew_secs > MAX_CLOCK_SKEW {
            return Err(JwtConfigError::InvalidPolicy);
        }
        if self.cache_ttl.is_zero() || self.cache_ttl > MAX_CACHE_TTL || self.generation == 0 {
            return Err(JwtConfigError::InvalidPolicy);
        }
        Ok(())
    }
}

/// Errors found while loading immutable tenant configuration.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum JwtConfigError {
    /// A text binding was empty, oversized, or contained control characters.
    InvalidText,
    /// A policy value was outside its bound.
    InvalidPolicy,
    /// The endpoint was not a safe pinned HTTPS endpoint.
    UnsafeJwksEndpoint,
}

/// Fetch errors intentionally contain no remote body or endpoint details.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum JwksFetchError {
    /// The fetch failed, timed out, exceeded a bound, or failed TLS policy.
    Failed,
}

/// Async boundary for the HTTPS/TLS JWKS implementation.
pub trait JwksFetcher: Send + Sync {
    /// Fetches one endpoint, enforcing certificate/hostname validation,
    /// redirect refusal, response-size limits, and public-IP connect policy.
    fn fetch<'a>(
        &'a self,
        endpoint: &'a JwksEndpoint,
    ) -> Pin<Box<dyn Future<Output = Result<Vec<u8>, JwksFetchError>> + Send + 'a>>;
}

/// Alias emphasizing that the fetcher is the service's provider boundary.
pub use JwksFetcher as JwksProvider;

/// A parsed public JWK, with validated key material but no secret bytes.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct JwkKey {
    kid: String,
    algorithm: Algorithm,
    key_type: &'static str,
    curve: Option<&'static str>,
    material: BTreeMap<&'static str, Vec<u8>>,
}

impl JwkKey {
    /// Returns the bounded key identifier.
    #[must_use]
    pub fn kid(&self) -> &str {
        &self.kid
    }
    /// Returns the key declared algorithm.
    #[must_use]
    pub const fn algorithm(&self) -> Algorithm {
        self.algorithm
    }
    /// Returns the JOSE key type.
    #[must_use]
    pub const fn key_type(&self) -> &str {
        self.key_type
    }
    /// Returns validated public key material by JOSE field name.
    #[must_use]
    pub fn material(&self, name: &str) -> Option<&[u8]> {
        self.material.get(name).map(Vec::as_slice)
    }
    /// Returns the EC curve when the key is an EC or OKP key.
    #[must_use]
    pub const fn curve(&self) -> Option<&str> {
        self.curve
    }
}

/// Parsed and bounded JWKS set.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct JwksDocument {
    keys: Vec<JwkKey>,
}

impl JwksDocument {
    /// Parses one response without retaining arbitrary JSON or remote errors.
    pub fn parse(body: &[u8]) -> Result<Self, JwtAuthenticationError> {
        if body.is_empty() || body.len() > MAX_JWKS_BYTES {
            return Err(JwtAuthenticationError::MalformedJwks);
        }
        let object = parse_object(body).map_err(|_| JwtAuthenticationError::MalformedJwks)?;
        let Some(Value::Array(values)) = object.get("keys") else {
            return Err(JwtAuthenticationError::MalformedJwks);
        };
        if values.is_empty() || values.len() > MAX_JWKS_KEYS {
            return Err(JwtAuthenticationError::MalformedJwks);
        }
        let mut keys = Vec::with_capacity(values.len());
        let mut kids = BTreeSet::new();
        for value in values {
            let Value::Object(fields) = value else {
                return Err(JwtAuthenticationError::MalformedJwks);
            };
            let Some(kid) = fields.get("kid").and_then(Value::as_str) else {
                return Err(JwtAuthenticationError::MalformedJwks);
            };
            if kid.is_empty() || kid.len() > 128 || !kid.is_ascii() || !kids.insert(kid.to_owned())
            {
                return Err(JwtAuthenticationError::MalformedJwks);
            }
            let Some(algorithm) =
                fields.get("alg").and_then(Value::as_str).and_then(Algorithm::parse)
            else {
                return Err(JwtAuthenticationError::MalformedJwks);
            };
            if fields.get("use").is_some_and(|value| value.as_str() != Some("sig")) {
                return Err(JwtAuthenticationError::MalformedJwks);
            }
            let mut material = BTreeMap::new();
            let (key_type, curve) = match (fields.get("kty").and_then(Value::as_str), algorithm) {
                (Some("RSA"), Algorithm::Rs256) => {
                    let n = decode_field(fields, "n")?;
                    let e = decode_field(fields, "e")?;
                    if n.is_empty() || e.is_empty() {
                        return Err(JwtAuthenticationError::MalformedJwks);
                    }
                    material.insert("n", n);
                    material.insert("e", e);
                    ("RSA", None)
                }
                (Some("EC"), Algorithm::Es256)
                    if fields.get("crv").and_then(Value::as_str) == Some("P-256") =>
                {
                    let x = decode_field(fields, "x")?;
                    let y = decode_field(fields, "y")?;
                    if x.len() != 32 || y.len() != 32 {
                        return Err(JwtAuthenticationError::MalformedJwks);
                    }
                    material.insert("x", x);
                    material.insert("y", y);
                    ("EC", Some("P-256"))
                }
                (Some("OKP"), Algorithm::EdDsa)
                    if fields.get("crv").and_then(Value::as_str) == Some("Ed25519") =>
                {
                    let x = decode_field(fields, "x")?;
                    if x.len() != 32 {
                        return Err(JwtAuthenticationError::MalformedJwks);
                    }
                    material.insert("x", x);
                    ("OKP", Some("Ed25519"))
                }
                _ => return Err(JwtAuthenticationError::MalformedJwks),
            };
            keys.push(JwkKey { kid: kid.to_owned(), algorithm, key_type, curve, material });
        }
        Ok(Self { keys })
    }

    fn key(&self, kid: &str, algorithm: Algorithm) -> Result<&JwkKey, JwtAuthenticationError> {
        let mut matches =
            self.keys.iter().filter(|key| key.kid == kid && key.algorithm == algorithm);
        let Some(first) = matches.next() else {
            return Err(JwtAuthenticationError::UnknownKey);
        };
        if matches.next().is_some() {
            return Err(JwtAuthenticationError::AmbiguousKey);
        }
        Ok(first)
    }
}

fn decode_field(
    fields: &serde_json::Map<String, Value>,
    name: &str,
) -> Result<Vec<u8>, JwtAuthenticationError> {
    let Some(value) = fields.get(name).and_then(Value::as_str) else {
        return Err(JwtAuthenticationError::MalformedJwks);
    };
    decode_base64url(value, MAX_JWKS_BYTES).map_err(|_| JwtAuthenticationError::MalformedJwks)
}

/// Crypto boundary for the maintained AWS-LC (or equivalent) implementation.
pub trait JwtSignatureVerifier: Send + Sync {
    /// Verifies the JOSE signing input against one already-selected public key.
    fn verify(
        &self,
        algorithm: Algorithm,
        key: &JwkKey,
        signing_input: &[u8],
        signature: &[u8],
    ) -> bool;
}

/// Alias for applications that call the cryptographic implementation a
/// provider.
pub use JwtSignatureVerifier as JwtCryptoProvider;

/// A bounded scalar claim copied into authorization context.
#[derive(Clone, Debug, Eq, Ord, PartialEq, PartialOrd)]
pub enum ClaimValue {
    /// Boolean claim.
    Boolean(bool),
    /// Signed integer claim.
    Integer(i64),
    /// Bounded string claim.
    String(String),
}

/// The only authorization value emitted by this module. It contains no raw
/// token, signature, or unbounded JSON and is intended for the parent auth
/// hook to bind to its request/engine context.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct AuthorizationContext {
    tenant: String,
    subject: String,
    issuer: String,
    audience: String,
    issued_at: u64,
    not_before: Option<u64>,
    expires_at: u64,
    operations: BTreeSet<String>,
    claims: BTreeMap<String, ClaimValue>,
    generation: u64,
}

impl AuthorizationContext {
    /// Exact configured tenant.
    #[must_use]
    pub fn tenant(&self) -> &str {
        &self.tenant
    }
    /// External subject identifier.
    #[must_use]
    pub fn subject(&self) -> &str {
        &self.subject
    }
    /// Verified issuer.
    #[must_use]
    pub fn issuer(&self) -> &str {
        &self.issuer
    }
    /// Verified audience selected from aud.
    #[must_use]
    pub fn audience(&self) -> &str {
        &self.audience
    }
    /// Issued-at unix timestamp.
    #[must_use]
    pub const fn issued_at(&self) -> u64 {
        self.issued_at
    }
    /// Optional not-before unix timestamp.
    #[must_use]
    pub const fn not_before(&self) -> Option<u64> {
        self.not_before
    }
    /// Expiry unix timestamp.
    #[must_use]
    pub const fn expires_at(&self) -> u64 {
        self.expires_at
    }
    /// Explicitly admitted operation names.
    #[must_use]
    pub fn operations(&self) -> &BTreeSet<String> {
        &self.operations
    }
    /// Strict scalar custom claims.
    #[must_use]
    pub fn claims(&self) -> &BTreeMap<String, ClaimValue> {
        &self.claims
    }
    /// Configuration/revocation generation at admission.
    #[must_use]
    pub const fn generation(&self) -> u64 {
        self.generation
    }
}

/// Authentication failures. Variants intentionally do not carry token or
/// remote response data, making accidental sensitive logging impossible.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum JwtAuthenticationError {
    /// Token shape or bounded JSON was invalid.
    MalformedToken,
    /// A claim was missing, duplicated, or had the wrong type.
    InvalidClaims,
    /// The configured issuer or audience did not match.
    BindingMismatch,
    /// The token algorithm was not explicitly allowed.
    AlgorithmRejected,
    /// The token was outside its validity interval.
    TimeRejected,
    /// JWKS bytes were malformed or exceeded limits.
    MalformedJwks,
    /// No key matched the requested key id and algorithm.
    UnknownKey,
    /// More than one key could verify the token.
    AmbiguousKey,
    /// The signature did not verify.
    InvalidSignature,
    /// JWKS retrieval failed; stale keys are never used after this failure.
    JwksUnavailable,
    /// Revocation/configuration changed while admission was in flight.
    GenerationChanged,
    /// The parent authorization hook refused the context.
    ParentRejected,
}

/// Async parent authorization hook invoked after cryptographic admission.
pub trait ParentAuthenticationHook: Send + Sync {
    /// Binds or rejects the bounded context. Hook errors are collapsed into
    /// JwtAuthenticationError::ParentRejected.
    fn admit<'a>(
        &'a self,
        context: &'a AuthorizationContext,
    ) -> Pin<Box<dyn Future<Output = Result<(), ()>> + Send + 'a>>;
}

struct CachedJwks {
    document: Arc<JwksDocument>,
    fetched_at: u64,
    generation: u64,
}

struct State {
    config: TenantJwtConfig,
    cache: Option<CachedJwks>,
}

/// Production BYO-JWT verifier with a bounded, generation-stamped JWKS cache.
pub struct ByoJwtService<F, V> {
    fetcher: Arc<F>,
    verifier: Arc<V>,
    state: RwLock<State>,
    generation: AtomicU64,
    fetch_lock: Mutex<()>,
}

impl<F, V> ByoJwtService<F, V>
where
    F: JwksFetcher + 'static,
    V: JwtSignatureVerifier + 'static,
{
    /// Creates a fail-closed service from validated tenant configuration.
    pub fn new(
        config: TenantJwtConfig,
        fetcher: Arc<F>,
        verifier: Arc<V>,
    ) -> Result<Self, JwtConfigError> {
        config.validate()?;
        Ok(Self {
            generation: AtomicU64::new(config.generation),
            state: RwLock::new(State { config, cache: None }),
            fetcher,
            verifier,
            fetch_lock: Mutex::new(()),
        })
    }

    /// Revokes all cached admissions and invalidates in-flight fetches.
    pub fn revoke_all(&self) {
        let generation = self.generation.fetch_add(1, Ordering::AcqRel) + 1;
        let mut state = self.state.write().unwrap_or_else(std::sync::PoisonError::into_inner);
        state.cache = None;
        state.config.generation = generation;
    }

    /// Replaces the pinned tenant policy and advances its generation.
    pub fn replace_config(&self, mut config: TenantJwtConfig) -> Result<(), JwtConfigError> {
        config.validate()?;
        let generation = self.generation.fetch_add(1, Ordering::AcqRel) + 1;
        config.generation = generation;
        let mut state = self.state.write().unwrap_or_else(std::sync::PoisonError::into_inner);
        state.config = config;
        state.cache = None;
        Ok(())
    }

    /// Authenticates one bearer token at a caller-supplied unix timestamp.
    pub async fn authenticate(
        &self,
        token: &str,
        now: u64,
    ) -> Result<AuthorizationContext, JwtAuthenticationError> {
        let (context, _) = self.authenticate_inner(token, now).await?;
        Ok(context)
    }

    /// Authenticates and then invokes the parent authorization hook.
    pub async fn authenticate_with_hook<H: ParentAuthenticationHook>(
        &self,
        token: &str,
        now: u64,
        hook: &H,
    ) -> Result<AuthorizationContext, JwtAuthenticationError> {
        let (context, _) = self.authenticate_inner(token, now).await?;
        hook.admit(&context).await.map_err(|_| JwtAuthenticationError::ParentRejected)?;
        Ok(context)
    }

    async fn authenticate_inner(
        &self,
        token: &str,
        now: u64,
    ) -> Result<(AuthorizationContext, u64), JwtAuthenticationError> {
        let parsed = ParsedToken::parse(token)?;
        let (config, generation) = {
            let state = self.state.read().unwrap_or_else(std::sync::PoisonError::into_inner);
            (state.config.clone(), self.generation.load(Ordering::Acquire))
        };
        if parsed.algorithm.map_or(true, |algorithm| !config.algorithms.contains(&algorithm)) {
            return Err(JwtAuthenticationError::AlgorithmRejected);
        }
        let algorithm = parsed.algorithm.ok_or(JwtAuthenticationError::AlgorithmRejected)?;
        let document = self.jwks(&config, generation, parsed.kid.as_str(), now, false).await?;
        let key = match document.key(&parsed.kid, algorithm) {
            Ok(key) => key,
            Err(JwtAuthenticationError::UnknownKey) => {
                let fresh = self.jwks(&config, generation, parsed.kid.as_str(), now, true).await?;
                fresh.key(&parsed.kid, algorithm)?
            }
            Err(error) => return Err(error),
        };
        let verified = self.verifier.verify(
            algorithm,
            key,
            parsed.signing_input.as_bytes(),
            &parsed.signature,
        );
        if !verified {
            return Err(JwtAuthenticationError::InvalidSignature);
        }
        if self.generation.load(Ordering::Acquire) != generation {
            return Err(JwtAuthenticationError::GenerationChanged);
        }
        let context = parsed.context(&config, generation, now)?;
        Ok((context, generation))
    }

    async fn jwks(
        &self,
        config: &TenantJwtConfig,
        generation: u64,
        _kid: &str,
        now: u64,
        force_refresh: bool,
    ) -> Result<Arc<JwksDocument>, JwtAuthenticationError> {
        if !force_refresh {
            let state = self.state.read().unwrap_or_else(std::sync::PoisonError::into_inner);
            if let Some(cache) = &state.cache
                && cache.generation == generation
                && now.saturating_sub(cache.fetched_at) <= config.cache_ttl.as_secs()
            {
                return Ok(Arc::clone(&cache.document));
            }
        }
        let _fetch_guard =
            self.fetch_lock.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
        if !force_refresh {
            let state = self.state.read().unwrap_or_else(std::sync::PoisonError::into_inner);
            if let Some(cache) = &state.cache
                && cache.generation == generation
                && now.saturating_sub(cache.fetched_at) <= config.cache_ttl.as_secs()
            {
                return Ok(Arc::clone(&cache.document));
            }
        }
        let body = self
            .fetcher
            .fetch(&config.jwks_endpoint)
            .await
            .map_err(|_| JwtAuthenticationError::JwksUnavailable)?;
        let document = Arc::new(JwksDocument::parse(&body)?);
        if self.generation.load(Ordering::Acquire) != generation {
            return Err(JwtAuthenticationError::GenerationChanged);
        }
        let mut state = self.state.write().unwrap_or_else(std::sync::PoisonError::into_inner);
        state.cache =
            Some(CachedJwks { document: Arc::clone(&document), fetched_at: now, generation });
        Ok(document)
    }
}

struct ParsedToken {
    signing_input: String,
    signature: Vec<u8>,
    algorithm: Option<Algorithm>,
    kid: String,
    claims: BTreeMap<String, Value>,
}

impl ParsedToken {
    fn parse(token: &str) -> Result<Self, JwtAuthenticationError> {
        if token.is_empty()
            || token.len() > MAX_TOKEN_BYTES
            || !token.is_ascii()
            || token.chars().any(char::is_whitespace)
        {
            return Err(JwtAuthenticationError::MalformedToken);
        }
        let mut parts = token.split('.');
        let (Some(header), Some(payload), Some(signature), None) =
            (parts.next(), parts.next(), parts.next(), parts.next())
        else {
            return Err(JwtAuthenticationError::MalformedToken);
        };
        if header.is_empty() || payload.is_empty() || signature.is_empty() {
            return Err(JwtAuthenticationError::MalformedToken);
        }
        let header_bytes = decode_base64url(header, MAX_HEADER_BYTES)
            .map_err(|_| JwtAuthenticationError::MalformedToken)?;
        let payload_bytes = decode_base64url(payload, MAX_PAYLOAD_BYTES)
            .map_err(|_| JwtAuthenticationError::MalformedToken)?;
        let signature = decode_base64url(signature, MAX_SIGNATURE_BYTES)
            .map_err(|_| JwtAuthenticationError::MalformedToken)?;
        let header =
            parse_object(&header_bytes).map_err(|_| JwtAuthenticationError::MalformedToken)?;
        let claims =
            parse_object(&payload_bytes).map_err(|_| JwtAuthenticationError::MalformedToken)?;
        let Some(kid) = header.get("kid").and_then(Value::as_str) else {
            return Err(JwtAuthenticationError::MalformedToken);
        };
        if kid.is_empty() || kid.len() > 128 || !kid.is_ascii() {
            return Err(JwtAuthenticationError::MalformedToken);
        }
        if header.get("typ").is_some_and(|value| value.as_str() != Some("JWT")) {
            return Err(JwtAuthenticationError::MalformedToken);
        }
        let algorithm = header.get("alg").and_then(Value::as_str).and_then(Algorithm::parse);
        Ok(Self {
            signing_input: header_segment(token).to_owned(),
            signature,
            algorithm,
            kid: kid.to_owned(),
            claims,
        })
    }

    fn context(
        self,
        config: &TenantJwtConfig,
        generation: u64,
        now: u64,
    ) -> Result<AuthorizationContext, JwtAuthenticationError> {
        let issuer = text_claim(&self.claims, "iss")?;
        let subject = text_claim(&self.claims, "sub")?;
        let tenant = text_claim(&self.claims, "tenant")?;
        if issuer != config.issuer || tenant != config.tenant {
            return Err(JwtAuthenticationError::BindingMismatch);
        }
        let audience = audience_claim(&self.claims, &config.audience)?;
        let issued_at = date_claim(&self.claims, "iat")?;
        let expires_at = date_claim(&self.claims, "exp")?;
        let not_before =
            self.claims.get("nbf").map(|_| date_claim(&self.claims, "nbf")).transpose()?;
        let skew = config.clock_skew_secs;
        if expires_at <= now.saturating_sub(skew)
            || issued_at > now.saturating_add(skew)
            || not_before.is_some_and(|value| value > now.saturating_add(skew))
            || expires_at <= issued_at
        {
            return Err(JwtAuthenticationError::TimeRejected);
        }
        let mut operations = BTreeSet::new();
        if let Some(value) = self.claims.get("operations") {
            let Value::Array(values) = value else {
                return Err(JwtAuthenticationError::InvalidClaims);
            };
            if values.is_empty() || values.len() > MAX_CONTEXT_OPERATIONS {
                return Err(JwtAuthenticationError::InvalidClaims);
            }
            for value in values {
                let Some(operation) = value.as_str() else {
                    return Err(JwtAuthenticationError::InvalidClaims);
                };
                if operation.is_empty()
                    || operation.len() > 64
                    || operation.chars().any(char::is_control)
                    || !operations.insert(operation.to_owned())
                {
                    return Err(JwtAuthenticationError::InvalidClaims);
                }
            }
        }
        let mut claims = BTreeMap::new();
        for (name, value) in self.claims {
            if matches!(
                name.as_str(),
                "iss" | "sub" | "tenant" | "aud" | "iat" | "exp" | "nbf" | "operations"
            ) {
                continue;
            }
            if name.len() > MAX_CLAIM_NAME_BYTES
                || name.is_empty()
                || name.chars().any(char::is_control)
                || claims.len() >= MAX_CONTEXT_CLAIMS
            {
                return Err(JwtAuthenticationError::InvalidClaims);
            }
            let scalar = match value {
                Value::Bool(value) => ClaimValue::Boolean(value),
                Value::Number(value) => ClaimValue::Integer(
                    value.as_i64().ok_or(JwtAuthenticationError::InvalidClaims)?,
                ),
                Value::String(value)
                    if !value.is_empty()
                        && value.len() <= MAX_CLAIM_VALUE_BYTES
                        && !value.chars().any(char::is_control) =>
                {
                    ClaimValue::String(value)
                }
                _ => return Err(JwtAuthenticationError::InvalidClaims),
            };
            if claims.insert(name, scalar).is_some() {
                return Err(JwtAuthenticationError::InvalidClaims);
            }
        }
        Ok(AuthorizationContext {
            tenant,
            subject,
            issuer,
            audience,
            issued_at,
            not_before,
            expires_at,
            operations,
            claims,
            generation,
        })
    }
}

fn header_segment(token: &str) -> &str {
    let end = token.find('.').unwrap_or(0);
    let after_header = &token[end + 1..];
    let payload_end = after_header.find('.').unwrap_or(0);
    &token[..end + 1 + payload_end]
}

fn text_claim(
    claims: &BTreeMap<String, Value>,
    name: &str,
) -> Result<String, JwtAuthenticationError> {
    let Some(value) = claims.get(name).and_then(Value::as_str) else {
        return Err(JwtAuthenticationError::InvalidClaims);
    };
    if value.is_empty() || value.len() > MAX_TEXT_BYTES || value.chars().any(char::is_control) {
        return Err(JwtAuthenticationError::InvalidClaims);
    }
    Ok(value.to_owned())
}

fn audience_claim(
    claims: &BTreeMap<String, Value>,
    expected: &str,
) -> Result<String, JwtAuthenticationError> {
    match claims.get("aud") {
        Some(Value::String(value)) if value == expected => Ok(value.clone()),
        Some(Value::Array(values))
            if values.len() <= 8
                && values.iter().filter_map(Value::as_str).any(|value| value == expected) =>
        {
            Ok(expected.to_owned())
        }
        _ => Err(JwtAuthenticationError::BindingMismatch),
    }
}

fn date_claim(claims: &BTreeMap<String, Value>, name: &str) -> Result<u64, JwtAuthenticationError> {
    claims.get(name).and_then(Value::as_u64).ok_or(JwtAuthenticationError::InvalidClaims)
}

fn decode_base64url(value: &str, max_decoded: usize) -> Result<Vec<u8>, ()> {
    if value.is_empty()
        || value.contains('=')
        || !value.bytes().all(|byte| byte.is_ascii_alphanumeric() || byte == b'-' || byte == b'_')
    {
        return Err(());
    }
    if value.len() > max_decoded.saturating_mul(2) {
        return Err(());
    }
    let mut output = Vec::with_capacity(value.len() * 3 / 4);
    let mut accumulator = 0_u32;
    let mut bits = 0_u8;
    for byte in value.bytes() {
        let digit = match byte {
            b'A'..=b'Z' => byte - b'A',
            b'a'..=b'z' => byte - b'a' + 26,
            b'0'..=b'9' => byte - b'0' + 52,
            b'-' => 62,
            b'_' => 63,
            _ => return Err(()),
        };
        accumulator = (accumulator << 6) | u32::from(digit);
        bits += 6;
        if bits >= 8 {
            bits -= 8;
            output.push((accumulator >> bits) as u8);
            if bits == 0 {
                accumulator = 0;
            } else {
                accumulator &= (1_u32 << bits) - 1;
            }
            if output.len() > max_decoded {
                return Err(());
            }
        }
    }
    if bits >= 6 || (bits > 0 && accumulator & ((1_u32 << bits) - 1) != 0) {
        return Err(());
    }
    Ok(output)
}

struct StrictObject(BTreeMap<String, Value>);

impl<'de> Deserialize<'de> for StrictObject {
    fn deserialize<D>(deserializer: D) -> Result<Self, D::Error>
    where
        D: serde::Deserializer<'de>,
    {
        struct ObjectVisitor;
        impl<'de> Visitor<'de> for ObjectVisitor {
            type Value = StrictObject;
            fn expecting(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
                formatter.write_str("a JSON object")
            }
            fn visit_map<A>(self, mut access: A) -> Result<Self::Value, A::Error>
            where
                A: MapAccess<'de>,
            {
                let mut values = BTreeMap::new();
                while let Some((key, value)) = access.next_entry::<String, Value>()? {
                    if values.insert(key, value).is_some() {
                        return Err(serde::de::Error::custom("duplicate JSON member"));
                    }
                }
                Ok(StrictObject(values))
            }
        }
        deserializer.deserialize_map(ObjectVisitor)
    }
}

fn parse_object(bytes: &[u8]) -> Result<BTreeMap<String, Value>, ()> {
    let mut deserializer = Deserializer::from_slice(bytes);
    let object = StrictObject::deserialize(&mut deserializer).map_err(|_| ())?;
    deserializer.end().map_err(|_| ())?;
    Ok(object.0)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::future::ready;
    use std::sync::atomic::{AtomicUsize, Ordering};

    struct Fetcher {
        body: Vec<u8>,
        calls: AtomicUsize,
    }

    impl JwksFetcher for Fetcher {
        fn fetch<'a>(
            &'a self,
            _: &'a JwksEndpoint,
        ) -> Pin<Box<dyn Future<Output = Result<Vec<u8>, JwksFetchError>> + Send + 'a>> {
            self.calls.fetch_add(1, Ordering::Relaxed);
            Box::pin(ready(Ok(self.body.clone())))
        }
    }

    struct Verifier;

    impl JwtSignatureVerifier for Verifier {
        fn verify(&self, _: Algorithm, _: &JwkKey, _: &[u8], signature: &[u8]) -> bool {
            signature == b"ok"
        }
    }

    fn config() -> TenantJwtConfig {
        TenantJwtConfig {
            tenant: "tenant-a".into(),
            issuer: "https://issuer.example".into(),
            audience: "terrabase".into(),
            jwks_endpoint: JwksEndpoint::parse("https://keys.example/.well-known/jwks.json")
                .expect("endpoint"),
            algorithms: BTreeSet::from([Algorithm::Rs256]),
            clock_skew_secs: 5,
            cache_ttl: Duration::from_secs(60),
            generation: 1,
        }
    }

    fn encode(bytes: &[u8]) -> String {
        const ALPHABET: &[u8; 64] =
            b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        let mut out = String::new();
        let mut n = 0_u32;
        let mut bits = 0_u8;
        for byte in bytes {
            n = (n << 8) | u32::from(*byte);
            bits += 8;
            while bits >= 6 {
                bits -= 6;
                out.push(ALPHABET[((n >> bits) & 63) as usize] as char);
            }
        }
        if bits > 0 {
            out.push(ALPHABET[((n << (6 - bits)) & 63) as usize] as char);
        }
        out
    }

    fn token(payload: &str) -> String {
        format!(
            "eyJhbGciOiJSUzI1NiIsImtpZCI6ImsxIiwidHlwIjoiSldUIn0.{}.b2s",
            encode(payload.as_bytes())
        )
    }

    fn jwks() -> Vec<u8> {
        br#"{"keys":[{"kty":"RSA","kid":"k1","alg":"RS256","n":"AQ","e":"AQAB"}]}"#.to_vec()
    }

    fn valid_payload() -> &'static str {
        r#"{"iss":"https://issuer.example","sub":"user-1","tenant":"tenant-a","aud":"terrabase","iat":95,"exp":200,"operations":["read"],"department":"finance"}"#
    }

    #[test]
    fn endpoint_rejects_private_and_non_https_targets() {
        assert_eq!(
            JwksEndpoint::parse("http://keys.example/a"),
            Err(JwtConfigError::UnsafeJwksEndpoint)
        );
        assert_eq!(
            JwksEndpoint::parse("https://127.0.0.1/a"),
            Err(JwtConfigError::UnsafeJwksEndpoint)
        );
        assert_eq!(
            JwksEndpoint::parse("https://keys.example:8443/a"),
            Err(JwtConfigError::UnsafeJwksEndpoint)
        );
        assert!(JwksEndpoint::parse("https://keys.example/a").is_ok());
    }

    #[tokio::test]
    async fn verifies_bounded_context_and_uses_cache_without_network_in_test() {
        let fetcher = Arc::new(Fetcher { body: jwks(), calls: AtomicUsize::new(0) });
        let service =
            ByoJwtService::new(config(), Arc::clone(&fetcher), Arc::new(Verifier)).expect("config");
        let context =
            service.authenticate(&token(valid_payload()), 100).await.expect("valid token");
        assert_eq!(context.tenant(), "tenant-a");
        assert_eq!(context.claims().get("department"), Some(&ClaimValue::String("finance".into())));
        service.authenticate(&token(valid_payload()), 100).await.expect("cached token");
        assert_eq!(fetcher.calls.load(Ordering::Relaxed), 1);
    }

    #[tokio::test]
    async fn expiry_and_generation_are_fail_closed() {
        let fetcher = Arc::new(Fetcher { body: jwks(), calls: AtomicUsize::new(0) });
        let service =
            ByoJwtService::new(config(), Arc::clone(&fetcher), Arc::new(Verifier)).expect("config");
        let expired = token(
            r#"{"iss":"https://issuer.example","sub":"user-1","tenant":"tenant-a","aud":"terrabase","iat":1,"exp":2}"#,
        );
        assert_eq!(
            service.authenticate(&expired, 100).await,
            Err(JwtAuthenticationError::TimeRejected)
        );
        let context =
            service.authenticate(&token(valid_payload()), 100).await.expect("valid token");
        assert_eq!(context.generation(), 1);
        service.revoke_all();
        let context =
            service.authenticate(&token(valid_payload()), 100).await.expect("fresh generation");
        assert_eq!(context.generation(), 2);
        assert_eq!(fetcher.calls.load(Ordering::Relaxed), 2);
    }

    #[test]
    fn duplicate_json_members_and_noncanonical_base64_fail_closed() {
        assert!(parse_object(br#"{"iss":"a","iss":"b"}"#).is_err());
        assert!(decode_base64url("AQ=", 10).is_err());
        assert!(decode_base64url("AB", 10).is_err());
        assert_eq!(decode_base64url("AQ", 10), Ok(vec![1]));
    }
}
