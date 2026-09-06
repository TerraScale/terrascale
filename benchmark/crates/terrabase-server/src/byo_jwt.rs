//! BYO-JWT authentication over a tenant-pinned JWKS endpoint (E-3).
//!
//! This module owns the hostile-input boundary. HTTP/TLS and cryptographic
//! implementations are supplied through `JwksFetcher` and `JwtSignatureVerifier`;
//! the fetcher contract requires certificate and hostname verification and a
//! public-IP connection policy. Keeping those effects behind traits makes the
//! parser and cache testable without making a network request.

use std::collections::{BTreeMap, BTreeSet};
use std::future::Future;
use std::net::{IpAddr, Ipv4Addr, Ipv6Addr};
use std::pin::Pin;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, RwLock};
use std::time::Duration;
use tokio::sync::Mutex;

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
pub const MAX_JWKS_BYTES: usize = 1024 * 1024;
/// Maximum keys retained from one JWKS document.
pub const MAX_JWKS_KEYS: usize = 256;
/// Maximum cache lifetime accepted from configuration.
pub const MAX_CACHE_TTL: Duration = Duration::from_hours(24);
/// Maximum clock skew accepted from configuration.
pub const MAX_CLOCK_SKEW: u64 = 5 * 60;
/// Maximum custom claims copied to an authorization context.
pub const MAX_CONTEXT_CLAIMS: usize = 64;
/// Maximum operation names copied to an authorization context.
pub const MAX_CONTEXT_OPERATIONS: usize = 32;
const MAX_TEXT_BYTES: usize = 255;
const MAX_CLAIM_NAME_BYTES: usize = 128;
const MAX_CLAIM_VALUE_BYTES: usize = 4 * 1024;
const MIN_RSA_MODULUS_BYTES: usize = 256;
const MAX_RSA_EXPONENT_BYTES: usize = 8;

/// JWT signature algorithms supported by the policy surface.
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub enum Algorithm {
    /// RSA PKCS#1 v1.5 with SHA-256.
    Rs256,
    /// ECDSA P-256 with SHA-256.
    Es256,
    /// Ed25519 (the JOSE `EdDSA` spelling).
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
    ///
    /// # Errors
    ///
    /// Returns `UnsafeJwksEndpoint` for a non-HTTPS URL, an unsupported authority,
    /// a private hostname, or an ambiguous path.
    #[allow(
        clippy::case_sensitive_file_extension_comparisons,
        reason = "these suffixes classify normalized DNS names, not file extensions"
    )]
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
            let host = host.to_ascii_lowercase();
            if host.eq_ignore_ascii_case("localhost")
                || host.ends_with(".localhost")
                || host.ends_with(".local")
                || host.ends_with(".internal")
                || host.ends_with(".lan")
                || host.eq_ignore_ascii_case("metadata.google.internal")
                || host
                    .bytes()
                    .any(|byte| !(byte.is_ascii_alphanumeric() || byte == b'.' || byte == b'-'))
                || host.split('.').any(|label| {
                    label.is_empty()
                        || label.len() > 63
                        || label.starts_with('-')
                        || label.ends_with('-')
                })
            {
                return Err(JwtConfigError::UnsafeJwksEndpoint);
            }
            host
        };
        if path.len() > 1_024
            || path.starts_with('/')
            || path.contains("..")
            || path.chars().any(|character| character.is_control() || character.is_whitespace())
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
                || (ip.octets()[0] == 192 && ip.octets()[1] == 0)
                || (ip.octets()[0] == 198 && (18..=19).contains(&ip.octets()[1]))
                || (ip.octets()[0] == 198 && ip.octets()[1] == 51 && ip.octets()[2] == 100)
                || (ip.octets()[0] == 203 && ip.octets()[1] == 0 && ip.octets()[2] == 113)
                || ip.octets()[0] >= 240
        }
        IpAddr::V6(ip) => {
            // IPv4-mapped addresses are a common DNS/connection-policy
            // bypass: classify the embedded address, not just the IPv6 form.
            ip.to_ipv4_mapped().is_some_and(|mapped| is_private_ip(IpAddr::V4(mapped)))
                || ip.is_loopback()
                || ip.is_unspecified()
                || ip.is_multicast()
                || (ip.segments()[0] & 0xfe00) == 0xfc00
                || (ip.segments()[0] & 0xffc0) == 0xfe80
                || (ip.segments()[0] == 0x2001 && ip.segments()[1] == 0x0db8)
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
    ///
    /// # Errors
    ///
    /// Returns `InvalidText` for invalid identity fields, or `InvalidPolicy` when
    /// algorithms, clock skew, cache lifetime, or generation violate the bounds.
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
    ///
    /// # Errors
    ///
    /// Returns `MalformedJwks` for oversized or malformed JSON, duplicate key
    /// identifiers, unsupported algorithms, or invalid public key material.
    pub fn parse(body: &[u8]) -> Result<Self, JwtAuthenticationError> {
        if body.is_empty() || body.len() > MAX_JWKS_BYTES {
            return Err(JwtAuthenticationError::MalformedJwks);
        }
        let object = parse_object(body).map_err(|()| JwtAuthenticationError::MalformedJwks)?;
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
            if kid.is_empty()
                || kid.len() > 128
                || !kid.is_ascii()
                || kid.chars().any(char::is_control)
                || !kids.insert(kid.to_owned())
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
            if let Some(value) = fields.get("key_ops") {
                let Value::Array(operations) = value else {
                    return Err(JwtAuthenticationError::MalformedJwks);
                };
                if operations.len() != 1
                    || operations.first().and_then(Value::as_str) != Some("verify")
                {
                    return Err(JwtAuthenticationError::MalformedJwks);
                }
            }
            let mut material = BTreeMap::new();
            let (key_type, curve) = match (fields.get("kty").and_then(Value::as_str), algorithm) {
                (Some("RSA"), Algorithm::Rs256) => {
                    let n = decode_field(fields, "n")?;
                    let e = decode_field(fields, "e")?;
                    // Enforce a 2048-bit RSA floor at the input boundary;
                    // accepting a tiny modulus and deferring the decision to
                    // a verifier creates an algorithm-policy downgrade seam.
                    if n.len() < MIN_RSA_MODULUS_BYTES
                        || n[0] < 0x80
                        || e.is_empty()
                        || e.len() > MAX_RSA_EXPONENT_BYTES
                        || e[0] == 0
                        || e.last().is_none_or(|last| *last % 2 == 0)
                        || (e.len() == 1 && e[0] < 3)
                    {
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
    decode_base64url(value, MAX_JWKS_BYTES).map_err(|()| JwtAuthenticationError::MalformedJwks)
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
    /// `JwtAuthenticationError::ParentRejected`.
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
    generation_exhausted: AtomicBool,
    fetch_lock: Mutex<()>,
}

/// A parent-composed authenticator that always runs the parent admission hook.
///
/// This is the construction seam for an HTTP/API parent: callers retain the
/// service only behind this wrapper and therefore cannot accidentally forget
/// the parent authorization step after cryptographic authentication.  The
/// wrapper contains no token bytes and is safe to share between requests.
pub struct ByoJwtAuthenticator<F, V, H> {
    service: Arc<ByoJwtService<F, V>>,
    hook: Arc<H>,
}

impl<F, V, H> ByoJwtAuthenticator<F, V, H>
where
    F: JwksFetcher + 'static,
    V: JwtSignatureVerifier + 'static,
    H: ParentAuthenticationHook + 'static,
{
    /// Builds a verifier and binds it to the mandatory parent hook.
    ///
    /// # Errors
    ///
    /// Returns the tenant policy validation error before constructing the service.
    pub fn new(
        config: TenantJwtConfig,
        fetcher: Arc<F>,
        verifier: Arc<V>,
        hook: Arc<H>,
    ) -> Result<Self, JwtConfigError> {
        let service = Arc::new(ByoJwtService::new(config, fetcher, verifier)?);
        Ok(Self { service, hook })
    }

    /// Wraps an already-created service with the mandatory parent hook.
    #[must_use]
    pub fn from_service(service: Arc<ByoJwtService<F, V>>, hook: Arc<H>) -> Self {
        Self { service, hook }
    }

    /// Authenticates a bearer token and invokes the parent hook before return.
    ///
    /// # Errors
    ///
    /// Returns an authentication error for rejected token claims or signatures,
    /// unavailable keys, concurrent revocation, or refusal by the parent hook.
    pub async fn authenticate(
        &self,
        token: &str,
        now: u64,
    ) -> Result<AuthorizationContext, JwtAuthenticationError> {
        self.service.authenticate_with_hook(token, now, self.hook.as_ref()).await
    }

    /// Revokes all cached admissions in the wrapped service.
    pub fn revoke_all(&self) {
        self.service.revoke_all();
    }

    /// Replaces the pinned policy while retaining mandatory parent admission.
    ///
    /// # Errors
    ///
    /// Returns the policy validation error, or `InvalidPolicy` if the generation is exhausted.
    pub fn replace_config(&self, config: TenantJwtConfig) -> Result<(), JwtConfigError> {
        self.service.replace_config(config)
    }
}

impl<F, V> ByoJwtService<F, V>
where
    F: JwksFetcher + 'static,
    V: JwtSignatureVerifier + 'static,
{
    /// Creates a fail-closed service from validated tenant configuration.
    ///
    /// # Errors
    ///
    /// Returns the tenant policy validation error before constructing the service.
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
            generation_exhausted: AtomicBool::new(false),
            fetch_lock: Mutex::new(()),
        })
    }

    /// Creates a service and binds it to a mandatory parent authorization hook.
    ///
    /// # Errors
    ///
    /// Returns the tenant policy validation error before constructing the service.
    pub fn with_parent_hook<H>(
        config: TenantJwtConfig,
        fetcher: Arc<F>,
        verifier: Arc<V>,
        hook: Arc<H>,
    ) -> Result<ByoJwtAuthenticator<F, V, H>, JwtConfigError>
    where
        H: ParentAuthenticationHook + 'static,
    {
        ByoJwtAuthenticator::new(config, fetcher, verifier, hook)
    }

    /// Revokes all cached admissions and invalidates in-flight fetches.
    pub fn revoke_all(&self) {
        let mut state = self.state.write().unwrap_or_else(std::sync::PoisonError::into_inner);
        let Some(generation) = state.config.generation.checked_add(1) else {
            // There is no representable generation after u64::MAX.  Keep the
            // service permanently fail-closed rather than wrapping and
            // accidentally re-admitting a token from the old generation.
            self.generation_exhausted.store(true, Ordering::Release);
            state.cache = None;
            return;
        };
        state.cache = None;
        state.config.generation = generation;
        self.generation.store(generation, Ordering::Release);
    }

    /// Replaces the pinned tenant policy and advances its generation.
    ///
    /// # Errors
    ///
    /// Returns the policy validation error, or `InvalidPolicy` if the generation is exhausted.
    /// The previous policy remains installed on failure.
    pub fn replace_config(&self, mut config: TenantJwtConfig) -> Result<(), JwtConfigError> {
        config.validate()?;
        let mut state = self.state.write().unwrap_or_else(std::sync::PoisonError::into_inner);
        let generation =
            state.config.generation.checked_add(1).ok_or(JwtConfigError::InvalidPolicy)?;
        config.generation = generation;
        state.config = config;
        state.cache = None;
        self.generation.store(generation, Ordering::Release);
        Ok(())
    }

    /// Authenticates one bearer token at a caller-supplied unix timestamp.
    ///
    /// # Errors
    ///
    /// Returns an authentication error for rejected token claims or signatures,
    /// unavailable keys, or a configuration generation changed during verification.
    pub async fn authenticate(
        &self,
        token: &str,
        now: u64,
    ) -> Result<AuthorizationContext, JwtAuthenticationError> {
        let (context, _) = self.authenticate_inner(token, now).await?;
        Ok(context)
    }

    /// Authenticates and then invokes the parent authorization hook.
    ///
    /// # Errors
    ///
    /// Returns an authentication error for rejected token claims or signatures,
    /// unavailable keys, concurrent revocation, or refusal by the parent hook.
    pub async fn authenticate_with_hook<H: ParentAuthenticationHook>(
        &self,
        token: &str,
        now: u64,
        hook: &H,
    ) -> Result<AuthorizationContext, JwtAuthenticationError> {
        let (context, generation) = self.authenticate_inner(token, now).await?;
        hook.admit(&context).await.map_err(|()| JwtAuthenticationError::ParentRejected)?;
        if self.generation_exhausted.load(Ordering::Acquire)
            || self.generation.load(Ordering::Acquire) != generation
        {
            return Err(JwtAuthenticationError::GenerationChanged);
        }
        Ok(context)
    }

    async fn authenticate_inner(
        &self,
        token: &str,
        now: u64,
    ) -> Result<(AuthorizationContext, u64), JwtAuthenticationError> {
        let parsed = ParsedToken::parse(token)?;
        if self.generation_exhausted.load(Ordering::Acquire) {
            return Err(JwtAuthenticationError::GenerationChanged);
        }
        let (config, generation) = {
            let state = self.state.read().unwrap_or_else(std::sync::PoisonError::into_inner);
            // The config and its generation are read under one lock.  The
            // atomic is only the invalidation fence used across awaits; using
            // it as the source here could pair a pre-rotation endpoint with a
            // post-rotation generation during the tiny update window.
            (state.config.clone(), state.config.generation)
        };
        if parsed.algorithm.is_none_or(|algorithm| !config.algorithms.contains(&algorithm)) {
            return Err(JwtAuthenticationError::AlgorithmRejected);
        }
        let algorithm = parsed.algorithm.ok_or(JwtAuthenticationError::AlgorithmRejected)?;
        // Claims are still untrusted until the signature check, but their
        // shape and configured bindings can be rejected before touching the
        // network.  This keeps malformed/expired bearer floods from forcing
        // JWKS retrievals.
        parsed.validate_claims(&config, now)?;
        let document = self.jwks(&config, generation, parsed.kid.as_str(), now, false).await?;
        let key = match document.key(&parsed.kid, algorithm) {
            Ok(key) => key.clone(),
            Err(JwtAuthenticationError::UnknownKey) => {
                let fresh = self.jwks(&config, generation, parsed.kid.as_str(), now, true).await?;
                fresh.key(&parsed.kid, algorithm)?.clone()
            }
            Err(error) => return Err(error),
        };
        let verified = self.verifier.verify(
            algorithm,
            &key,
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
        // A revocation can race the check above while claims are being
        // copied.  Re-check after constructing the bounded context so an
        // admission can never escape with a generation that was revoked
        // before this method returns.
        if self.generation.load(Ordering::Acquire) != generation {
            return Err(JwtAuthenticationError::GenerationChanged);
        }
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
        let _fetch_guard = self.fetch_lock.lock().await;
        if !force_refresh {
            let state = self.state.read().unwrap_or_else(std::sync::PoisonError::into_inner);
            if let Some(cache) = &state.cache
                && cache.generation == generation
                && now.saturating_sub(cache.fetched_at) <= config.cache_ttl.as_secs()
            {
                return Ok(Arc::clone(&cache.document));
            }
        }
        let Ok(body) = self.fetcher.fetch(&config.jwks_endpoint).await else {
            // Never fall back to an expired or failed-rotation key set.
            // Clearing here also prevents a later request from treating
            // the old entry as fresh when the supplied clock advances
            // less than one second.
            self.clear_cache(generation);
            return Err(JwtAuthenticationError::JwksUnavailable);
        };
        let document = match JwksDocument::parse(&body) {
            Ok(document) => Arc::new(document),
            Err(error) => {
                self.clear_cache(generation);
                return Err(error);
            }
        };
        if self.generation.load(Ordering::Acquire) != generation {
            return Err(JwtAuthenticationError::GenerationChanged);
        }
        let mut state = self.state.write().unwrap_or_else(std::sync::PoisonError::into_inner);
        state.cache =
            Some(CachedJwks { document: Arc::clone(&document), fetched_at: now, generation });
        Ok(document)
    }

    fn clear_cache(&self, generation: u64) {
        let mut state = self.state.write().unwrap_or_else(std::sync::PoisonError::into_inner);
        if state.config.generation == generation {
            state.cache = None;
        }
    }
}

#[derive(Clone)]
struct ParsedToken {
    signing_input: String,
    signature: Vec<u8>,
    algorithm: Option<Algorithm>,
    kid: String,
    claims: BTreeMap<String, Value>,
}

impl ParsedToken {
    fn validate_claims(
        &self,
        config: &TenantJwtConfig,
        now: u64,
    ) -> Result<(), JwtAuthenticationError> {
        self.clone().context(config, config.generation, now).map(|_| ())
    }
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
            .map_err(|()| JwtAuthenticationError::MalformedToken)?;
        let payload_bytes = decode_base64url(payload, MAX_PAYLOAD_BYTES)
            .map_err(|()| JwtAuthenticationError::MalformedToken)?;
        let signature = decode_base64url(signature, MAX_SIGNATURE_BYTES)
            .map_err(|()| JwtAuthenticationError::MalformedToken)?;
        let header =
            parse_object(&header_bytes).map_err(|()| JwtAuthenticationError::MalformedToken)?;
        let claims =
            parse_object(&payload_bytes).map_err(|()| JwtAuthenticationError::MalformedToken)?;
        let Some(kid) = header.get("kid").and_then(Value::as_str) else {
            return Err(JwtAuthenticationError::MalformedToken);
        };
        if kid.is_empty() || kid.len() > 128 || !kid.is_ascii() || kid.chars().any(char::is_control)
        {
            return Err(JwtAuthenticationError::MalformedToken);
        }
        if header.get("typ").is_some_and(|value| value.as_str() != Some("JWT")) {
            return Err(JwtAuthenticationError::MalformedToken);
        }
        // These members can redirect key selection away from the tenant-pinned
        // JWKS or alter JOSE processing.  The service has no extension
        // registry, so rejecting them is safer than silently ignoring them.
        if ["jku", "jwk", "x5u", "crit"].iter().any(|name| header.contains_key(*name)) {
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
        Some(Value::Array(values)) if !values.is_empty() && values.len() <= 8 => {
            let mut found = false;
            for value in values {
                let Some(value) = value.as_str() else {
                    return Err(JwtAuthenticationError::BindingMismatch);
                };
                if value.is_empty()
                    || value.len() > MAX_TEXT_BYTES
                    || value.chars().any(char::is_control)
                {
                    return Err(JwtAuthenticationError::BindingMismatch);
                }
                found |= value == expected;
            }
            if found {
                Ok(expected.to_owned())
            } else {
                Err(JwtAuthenticationError::BindingMismatch)
            }
        }
        _ => Err(JwtAuthenticationError::BindingMismatch),
    }
}

fn date_claim(claims: &BTreeMap<String, Value>, name: &str) -> Result<u64, JwtAuthenticationError> {
    claims.get(name).and_then(Value::as_u64).ok_or(JwtAuthenticationError::InvalidClaims)
}

fn decode_base64url(value: &str, max_decoded: usize) -> Result<Vec<u8>, ()> {
    use base64::{Engine as _, engine::general_purpose::URL_SAFE_NO_PAD};

    if value.is_empty() || value.len() > max_decoded.saturating_mul(2) {
        return Err(());
    }
    let output = URL_SAFE_NO_PAD.decode(value).map_err(|_| ())?;
    if output.len() > max_decoded {
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

    struct DelayedFetcher {
        calls: AtomicUsize,
        entered: tokio::sync::Notify,
        release: tokio::sync::Notify,
    }

    impl JwksFetcher for DelayedFetcher {
        fn fetch<'a>(
            &'a self,
            _: &'a JwksEndpoint,
        ) -> Pin<Box<dyn Future<Output = Result<Vec<u8>, JwksFetchError>> + Send + 'a>> {
            Box::pin(async move {
                self.calls.fetch_add(1, Ordering::Relaxed);
                self.entered.notify_one();
                self.release.notified().await;
                Ok(jwks())
            })
        }
    }

    fn delayed_fetcher() -> Arc<DelayedFetcher> {
        Arc::new(DelayedFetcher {
            calls: AtomicUsize::new(0),
            entered: tokio::sync::Notify::new(),
            release: tokio::sync::Notify::new(),
        })
    }

    #[tokio::test]
    async fn concurrent_authentication_shares_a_pending_jwks_fetch() {
        let fetcher = delayed_fetcher();
        let service = Arc::new(
            ByoJwtService::new(config(), Arc::clone(&fetcher), Arc::new(Verifier)).expect("config"),
        );
        let mut requests = Vec::new();
        for _ in 0..8 {
            let service = Arc::clone(&service);
            requests.push(tokio::spawn(async move {
                service.authenticate(&token(valid_payload()), 100).await
            }));
        }
        tokio::time::timeout(Duration::from_secs(1), fetcher.entered.notified())
            .await
            .expect("fetch starts without blocking the executor");
        fetcher.release.notify_one();
        for request in requests {
            tokio::time::timeout(Duration::from_secs(1), request)
                .await
                .expect("request completes")
                .expect("task succeeds")
                .expect("token admitted");
        }
        assert_eq!(fetcher.calls.load(Ordering::Relaxed), 1);
    }

    #[tokio::test]
    async fn revocation_during_jwks_fetch_prevents_admission() {
        let fetcher = delayed_fetcher();
        let service = Arc::new(
            ByoJwtService::new(config(), Arc::clone(&fetcher), Arc::new(Verifier)).expect("config"),
        );
        let request_service = Arc::clone(&service);
        let request = tokio::spawn(async move {
            request_service.authenticate(&token(valid_payload()), 100).await
        });
        tokio::time::timeout(Duration::from_secs(1), fetcher.entered.notified())
            .await
            .expect("fetch starts");
        service.revoke_all();
        fetcher.release.notify_one();
        let result = tokio::time::timeout(Duration::from_secs(1), request)
            .await
            .expect("request completes")
            .expect("task succeeds");
        assert_eq!(result, Err(JwtAuthenticationError::GenerationChanged));
    }

    impl JwtSignatureVerifier for Verifier {
        fn verify(&self, _: Algorithm, _: &JwkKey, _: &[u8], signature: &[u8]) -> bool {
            signature == b"ok"
        }
    }

    struct Hook {
        calls: AtomicUsize,
        admit: bool,
    }

    impl ParentAuthenticationHook for Hook {
        fn admit<'a>(
            &'a self,
            _: &'a AuthorizationContext,
        ) -> Pin<Box<dyn Future<Output = Result<(), ()>> + Send + 'a>> {
            self.calls.fetch_add(1, Ordering::Relaxed);
            let result = self.admit;
            Box::pin(ready(if result { Ok(()) } else { Err(()) }))
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
            cache_ttl: Duration::from_mins(1),
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
        let mut modulus = vec![0_u8; MIN_RSA_MODULUS_BYTES];
        modulus[0] = 0x80;
        format!(
            r#"{{"keys":[{{"kty":"RSA","kid":"k1","alg":"RS256","n":"{}","e":"AQAB"}}]}}"#,
            encode(&modulus)
        )
        .into_bytes()
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
            JwksEndpoint::parse("https://[::ffff:127.0.0.1]/a"),
            Err(JwtConfigError::UnsafeJwksEndpoint)
        );
        assert_eq!(
            JwksEndpoint::parse("https://192.0.0.1/a"),
            Err(JwtConfigError::UnsafeJwksEndpoint)
        );
        assert_eq!(
            JwksEndpoint::parse("https://[2001:db8::1]/a"),
            Err(JwtConfigError::UnsafeJwksEndpoint)
        );
        assert_eq!(
            JwksEndpoint::parse("https://keys.example:8443/a"),
            Err(JwtConfigError::UnsafeJwksEndpoint)
        );
        assert!(JwksEndpoint::parse("https://keys.example/a").is_ok());
    }

    #[test]
    fn endpoint_validates_canonical_hostnames_and_rejects_ambiguous_paths() {
        for endpoint in [
            "https://keys.LOCALHOST/a",
            "https://keys.LoCaL/a",
            "https://keys.INTERNAL/a",
            "https://keys.LAN/a",
            "https://keys.example/a b",
            "https://keys.example/a\t",
        ] {
            assert_eq!(
                JwksEndpoint::parse(endpoint),
                Err(JwtConfigError::UnsafeJwksEndpoint),
                "{endpoint:?}"
            );
        }
        assert_eq!(
            JwksEndpoint::parse(&format!("https://{}.example/a", "a".repeat(64))),
            Err(JwtConfigError::UnsafeJwksEndpoint)
        );
        assert_eq!(
            JwksEndpoint::parse("https://KEYS.Example:443/Jwks").unwrap().as_str(),
            "https://keys.example/Jwks"
        );
        assert!(JwksEndpoint::parse(&format!("https://{}.example/a", "a".repeat(63))).is_ok());
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

    #[tokio::test]
    async fn parent_constructor_always_runs_the_parent_hook() {
        let fetcher = Arc::new(Fetcher { body: jwks(), calls: AtomicUsize::new(0) });
        let hook = Arc::new(Hook { calls: AtomicUsize::new(0), admit: true });
        let authenticator = ByoJwtService::with_parent_hook(
            config(),
            Arc::clone(&fetcher),
            Arc::new(Verifier),
            Arc::clone(&hook),
        )
        .expect("config");
        authenticator.authenticate(&token(valid_payload()), 100).await.expect("admitted");
        assert_eq!(hook.calls.load(Ordering::Relaxed), 1);

        let refusing = Arc::new(Hook { calls: AtomicUsize::new(0), admit: false });
        let authenticator = ByoJwtService::with_parent_hook(
            config(),
            fetcher,
            Arc::new(Verifier),
            Arc::clone(&refusing),
        )
        .expect("config");
        assert_eq!(
            authenticator.authenticate(&token(valid_payload()), 100).await,
            Err(JwtAuthenticationError::ParentRejected)
        );
        assert_eq!(refusing.calls.load(Ordering::Relaxed), 1);
    }

    #[test]
    fn duplicate_json_members_and_noncanonical_base64_fail_closed() {
        assert!(parse_object(br#"{"iss":"a","iss":"b"}"#).is_err());
        assert!(decode_base64url("AQ=", 10).is_err());
        assert!(decode_base64url("AB", 10).is_err());
        assert_eq!(decode_base64url("AQ", 10), Ok(vec![1]));
        assert!(decode_base64url("", 10).is_err());
        assert!(decode_base64url("AQID", 2).is_err());
        assert!(decode_base64url("AQ", 0).is_err());
        assert_eq!(decode_base64url("AQID", 3), Ok(vec![1, 2, 3]));
    }

    #[test]
    fn hostile_header_and_audience_shapes_fail_closed() {
        let with_jku = "eyJhbGciOiJSUzI1NiIsImtpZCI6ImsxIiwiamt1IjoiaHR0cHM6Ly9ldmlsIn0.e30.b2s";
        assert!(matches!(
            ParsedToken::parse(with_jku),
            Err(JwtAuthenticationError::MalformedToken)
        ));
        let claims = BTreeMap::from([(
            "aud".to_owned(),
            Value::Array(vec![Value::String("terrabase".into()), Value::Null]),
        )]);
        assert_eq!(
            audience_claim(&claims, "terrabase"),
            Err(JwtAuthenticationError::BindingMismatch)
        );
    }
}
