//! Production composition for tenant pinned BYO-JWT admission.
//!
//! This module supplies the real HTTPS/TLS and AWS-LC crypto boundaries used
//! by [`crate::byo_jwt`].  It deliberately does not turn claims into a
//! database scope; the API parent remains responsible for membership and
//! request permission checks.

use std::future::Future;
use std::net::IpAddr;
use std::pin::Pin;
use std::sync::Arc;
use std::time::Duration;

use bytes::Bytes;
use http_body_util::{BodyExt as _, Full, Limited};
use hyper::Uri;
use hyper_util::rt::TokioIo;
use rustls::pki_types::ServerName;
use serde::Deserialize;
use tokio_rustls::TlsConnector;

use crate::api_keys::{Principal, Scope, Verbs};
use crate::byo_jwt::{
    Algorithm, AuthorizationContext, ByoJwtAuthenticator, JwkKey, JwksEndpoint, JwksFetchError,
    JwksFetcher, JwtAuthenticationError, JwtSignatureVerifier, ParentAuthenticationHook,
    TenantJwtConfig,
};

const FETCH_TIMEOUT: Duration = Duration::from_secs(5);

/// Object-safe handoff used by the public API middleware.
pub trait ByoJwtAdmission: Send + Sync {
    /// Cryptographically authenticates a bearer and runs its parent hook.
    fn authenticate<'a>(
        &'a self,
        token: &'a str,
        now: u64,
    ) -> Pin<
        Box<dyn Future<Output = Result<AuthorizationContext, JwtAuthenticationError>> + Send + 'a>,
    >;
}

/// Signature and claim validation stage. This deliberately has no membership
/// semantics; callers must run the live producer check before creating a
/// request principal.
pub trait ByoJwtSignatureAdmission: Send + Sync {
    /// Validates a bearer and returns its bounded verified context.
    fn authenticate<'a>(
        &'a self,
        token: &'a str,
        now: u64,
    ) -> Pin<
        Box<dyn Future<Output = Result<AuthorizationContext, JwtAuthenticationError>> + Send + 'a>,
    >;
    /// Rechecks revocation/configuration generation after live membership.
    fn generation_current(&self, context: &AuthorizationContext) -> bool;
}

impl<F, V> ByoJwtSignatureAdmission for crate::byo_jwt::ByoJwtService<F, V>
where
    F: JwksFetcher + 'static,
    V: JwtSignatureVerifier + 'static,
{
    fn authenticate<'a>(
        &'a self,
        token: &'a str,
        now: u64,
    ) -> Pin<
        Box<dyn Future<Output = Result<AuthorizationContext, JwtAuthenticationError>> + Send + 'a>,
    > {
        Box::pin(crate::byo_jwt::ByoJwtService::authenticate(self, token, now))
    }

    fn generation_current(&self, context: &AuthorizationContext) -> bool {
        crate::byo_jwt::ByoJwtService::generation_current(self, context)
    }
}

/// Binds an authenticated subject to the request scope selected by the API.
/// The database and collection are supplied by the route, never by claims.
pub(crate) fn principal_for(
    context: &AuthorizationContext,
    database: Option<&str>,
    collection: Option<&str>,
    verb: Verbs,
    project_admin: bool,
) -> Option<Principal> {
    let required = if project_admin || verb == Verbs::ADMIN {
        "admin"
    } else if verb == Verbs::WRITE {
        "write"
    } else {
        "read"
    };
    if !context.operations().contains(required) {
        return None;
    }
    let scope = if project_admin {
        Scope::every_database(Verbs::ADMIN)
    } else {
        let database = database?;
        match collection {
            Some(collection) => Scope::database_collection(database, collection, verb),
            None => Scope::database(database, verb),
        }
    };
    let expires_at = context.expires_at().checked_mul(1_000)?;
    Some(Principal::delegated(context.subject().to_owned(), scope, expires_at))
}

impl<F, V, H> ByoJwtAdmission for ByoJwtAuthenticator<F, V, H>
where
    F: JwksFetcher + 'static,
    V: JwtSignatureVerifier + 'static,
    H: ParentAuthenticationHook + 'static,
{
    fn authenticate<'a>(
        &'a self,
        token: &'a str,
        now: u64,
    ) -> Pin<
        Box<dyn Future<Output = Result<AuthorizationContext, JwtAuthenticationError>> + Send + 'a>,
    > {
        Box::pin(ByoJwtAuthenticator::authenticate(self, token, now))
    }
}

/// File configuration for one tenant-pinned issuer.
#[derive(Clone, Debug, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct FileTenantJwtConfig {
    pub tenant: String,
    pub issuer: String,
    pub audience: String,
    pub jwks_endpoint: String,
    pub algorithms: Vec<String>,
    pub clock_skew_secs: u64,
    pub cache_ttl_secs: u64,
    pub generation: u64,
}

/// Builds the production object-safe admission handoff for one issuer.
pub fn build_admission<H>(
    file: &FileTenantJwtConfig,
    hook: Arc<H>,
) -> Result<Arc<dyn ByoJwtAdmission>, String>
where
    H: ParentAuthenticationHook + 'static,
{
    let config = file.resolve()?;
    let authenticator = ByoJwtAuthenticator::new(
        config,
        Arc::new(HttpsJwksFetcher::default()),
        Arc::new(AwsLcJwtVerifier),
        hook,
    )
    .map_err(|_| "invalid BYO-JWT policy")?;
    Ok(Arc::new(authenticator))
}

/// Builds the signature-only stage for callers that perform live membership
/// admission in the request path with the original verified context.
pub fn build_signature_admission(
    file: &FileTenantJwtConfig,
) -> Result<Arc<dyn ByoJwtSignatureAdmission>, String> {
    let service = crate::byo_jwt::ByoJwtService::new(
        file.resolve()?,
        Arc::new(HttpsJwksFetcher::default()),
        Arc::new(AwsLcJwtVerifier),
    )
    .map_err(|_| "invalid BYO-JWT policy")?;
    Ok(Arc::new(service))
}

impl FileTenantJwtConfig {
    /// Converts and validates the file representation before readiness.
    pub fn resolve(&self) -> Result<TenantJwtConfig, String> {
        let algorithms = self
            .algorithms
            .iter()
            .map(|value| match value.as_str() {
                "RS256" => Ok(Algorithm::Rs256),
                "ES256" => Ok(Algorithm::Es256),
                "EdDSA" => Ok(Algorithm::EdDsa),
                _ => Err("unsupported BYO-JWT algorithm"),
            })
            .collect::<Result<std::collections::BTreeSet<_>, _>>()?;
        let jwks_endpoint = JwksEndpoint::parse(&self.jwks_endpoint)
            .map_err(|_| "invalid BYO-JWT JWKS endpoint")?;
        let config = TenantJwtConfig {
            tenant: self.tenant.clone(),
            issuer: self.issuer.clone(),
            audience: self.audience.clone(),
            jwks_endpoint,
            algorithms,
            clock_skew_secs: self.clock_skew_secs,
            cache_ttl: Duration::from_secs(self.cache_ttl_secs),
            generation: self.generation,
        };
        config.validate().map_err(|_| String::from("invalid BYO-JWT policy"))?;
        Ok(config)
    }
}

/// HTTPS JWKS provider with certificate and hostname validation.
#[derive(Clone)]
pub struct HttpsJwksFetcher {
    tls: Arc<rustls::ClientConfig>,
}

impl HttpsJwksFetcher {
    /// Uses the platform root store and AWS-LC crypto provider.
    pub fn new() -> Self {
        let roots: rustls::RootCertStore = webpki_roots::TLS_SERVER_ROOTS.iter().cloned().collect();
        let tls = rustls::ClientConfig::builder_with_provider(Arc::new(
            rustls::crypto::aws_lc_rs::default_provider(),
        ))
        .with_safe_default_protocol_versions()
        .expect("AWS-LC protocol defaults")
        .with_root_certificates(roots)
        .with_no_client_auth();
        Self { tls: Arc::new(tls) }
    }
}

impl Default for HttpsJwksFetcher {
    fn default() -> Self {
        Self::new()
    }
}

impl JwksFetcher for HttpsJwksFetcher {
    fn fetch<'a>(
        &'a self,
        endpoint: &'a JwksEndpoint,
    ) -> Pin<Box<dyn Future<Output = Result<Vec<u8>, JwksFetchError>> + Send + 'a>> {
        Box::pin(async move {
            tokio::time::timeout(FETCH_TIMEOUT, async move {
            let uri: Uri = endpoint.as_str().parse().map_err(|_| JwksFetchError::Failed)?;
            let host = uri.host().ok_or(JwksFetchError::Failed)?.to_owned();
            let name = ServerName::try_from(host.clone()).map_err(|_| JwksFetchError::Failed)?;
            let port = uri.port_u16().unwrap_or(443);
            let addresses = tokio::net::lookup_host((host.as_str(), port))
                .await
                .map_err(|_| JwksFetchError::Failed)?
                .collect::<Vec<_>>();
            if addresses.is_empty() || addresses.iter().any(|address| is_private(address.ip())) {
                return Err(JwksFetchError::Failed);
            }
            let stream =
                tokio::time::timeout(FETCH_TIMEOUT, tokio::net::TcpStream::connect(addresses[0]))
                    .await
                    .map_err(|_| JwksFetchError::Failed)?
                    .map_err(|_| JwksFetchError::Failed)?;
            let stream = TlsConnector::from(self.tls.clone())
                .connect(name, stream)
                .await
                .map_err(|_| JwksFetchError::Failed)?;
            let (mut sender, connection) = hyper::client::conn::http1::Builder::new()
                .max_headers(32)
                .max_buf_size(16 * 1024)
                .handshake(TokioIo::new(stream))
                .await
                .map_err(|_| JwksFetchError::Failed)?;
            let request = hyper::Request::builder()
                .method("GET")
                .uri(uri.path_and_query().map_or("/", |value| value.as_str()))
                .header("host", uri.authority().ok_or(JwksFetchError::Failed)?.as_str())
                .body(Full::new(Bytes::new()))
                .map_err(|_| JwksFetchError::Failed)?;
            let response = async {
                let response =
                    sender.send_request(request).await.map_err(|_| JwksFetchError::Failed)?;
                if response.status() != hyper::StatusCode::OK {
                    return Err(JwksFetchError::Failed);
                }
                Limited::new(response.into_body(), crate::byo_jwt::MAX_JWKS_BYTES)
                    .collect()
                    .await
                    .map(|body| body.to_bytes().to_vec())
                    .map_err(|_| JwksFetchError::Failed)
            };
            tokio::pin!(response, connection);
            tokio::select! { result = &mut response => result, _ = &mut connection => response.await }
            })
            .await
            .map_err(|_| JwksFetchError::Failed)?
        })
    }
}

fn is_private(address: IpAddr) -> bool {
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
                || ip.octets()[0] >= 240
        }
        IpAddr::V6(ip) => {
            ip.to_ipv4_mapped().is_some_and(|mapped| is_private(IpAddr::V4(mapped)))
                || ip.is_loopback()
                || ip.is_unspecified()
                || ip.is_multicast()
                || (ip.segments()[0] & 0xfe00) == 0xfc00
                || (ip.segments()[0] & 0xffc0) == 0xfe80
                || (ip.segments()[0] == 0x2001 && ip.segments()[1] == 0x0db8)
        }
    }
}

/// AWS-LC signature verifier for the three explicitly supported JOSE algorithms.
#[derive(Clone, Copy, Debug, Default)]
pub struct AwsLcJwtVerifier;

impl JwtSignatureVerifier for AwsLcJwtVerifier {
    fn verify(
        &self,
        algorithm: Algorithm,
        key: &JwkKey,
        signing_input: &[u8],
        signature: &[u8],
    ) -> bool {
        let material: Option<Vec<u8>> = match algorithm {
            Algorithm::Rs256 => rsa_spki(key),
            Algorithm::Es256 => key.material("x").and_then(|x| key.material("y")).map(|y| {
                let x = key.material("x").expect("x checked");
                let mut point = Vec::with_capacity(65);
                point.push(4);
                point.extend_from_slice(x);
                point.extend_from_slice(y);
                point
            }),
            Algorithm::EdDsa => key.material("x").map(ToOwned::to_owned),
        };
        let Some(material) = material else { return false };
        match algorithm {
            Algorithm::Rs256 => aws_lc_rs::signature::UnparsedPublicKey::new(
                &aws_lc_rs::signature::RSA_PKCS1_2048_8192_SHA256,
                material,
            )
            .verify(signing_input, signature)
            .is_ok(),
            // JOSE encodes ECDSA as the fixed-width raw R||S form.
            Algorithm::Es256 => aws_lc_rs::signature::UnparsedPublicKey::new(
                &aws_lc_rs::signature::ECDSA_P256_SHA256_FIXED,
                material,
            )
            .verify(signing_input, signature)
            .is_ok(),
            Algorithm::EdDsa => aws_lc_rs::signature::UnparsedPublicKey::new(
                &aws_lc_rs::signature::ED25519,
                material,
            )
            .verify(signing_input, signature)
            .is_ok(),
        }
    }
}

fn der_len(length: usize) -> Vec<u8> {
    if length < 128 {
        vec![length as u8]
    } else {
        let bytes = (length as u32).to_be_bytes();
        let first = bytes.iter().position(|b| *b != 0).unwrap_or(3);
        let mut out = vec![0x80 | (4 - first) as u8];
        out.extend_from_slice(&bytes[first..]);
        out
    }
}

fn der(tag: u8, value: &[u8]) -> Vec<u8> {
    let mut out = vec![tag];
    out.extend(der_len(value.len()));
    out.extend_from_slice(value);
    out
}

fn rsa_spki(key: &JwkKey) -> Option<Vec<u8>> {
    let n = key.material("n")?;
    let e = key.material("e")?;
    let integer = |value: &[u8]| {
        let mut v = if value.first().is_some_and(|b| b & 0x80 != 0) { vec![0] } else { Vec::new() };
        v.extend_from_slice(value);
        der(2, &v)
    };
    let rsa = {
        let mut v = integer(n);
        v.extend(integer(e));
        der(0x30, &v)
    };
    let algorithm = {
        let oid = [0x06, 0x09, 0x2a, 0x86, 0x48, 0x86, 0xf7, 0x0d, 0x01, 0x01, 0x01];
        let null = [0x05, 0];
        let mut v = oid.to_vec();
        v.extend_from_slice(&null);
        der(0x30, &v)
    };
    let mut bit = vec![0];
    bit.extend_from_slice(&rsa);
    let mut value = algorithm;
    value.extend(der(3, &bit));
    Some(der(0x30, &value))
}
