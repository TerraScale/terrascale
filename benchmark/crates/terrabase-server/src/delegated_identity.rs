//! Live product identity admission through the pinned Better Auth authority.
//! Every request verifies TLS and asks the producer to revalidate credentials,
//! revocation, consent, and current tenant permissions. No admissions are cached.

use crate::api_keys::{Principal, Scope, Verbs};
use bytes::Bytes;
use http_body_util::{BodyExt as _, Full, Limited};
use hyper::http::{HeaderValue, Uri};
use hyper_util::rt::TokioIo;
use rustls::pki_types::ServerName;
use serde::Deserialize;
use std::sync::Arc;
use std::time::Duration;
use tokio_rustls::TlsConnector;

/// Producer-authorized identity verification configuration. Secrets are never debug printed.
pub struct IdentityConfig {
    /// Pinned HTTPS introspection endpoint, independent of credential contents.
    pub endpoint: String,
    /// Exact Better Auth issuer.
    pub issuer: String,
    /// Exact public database API resource/audience.
    pub audience: String,
    /// Private member-to-identity service credential.
    pub service_key: String,
}

/// Project scope verified by the identity producer's live ownership and role checks.
#[derive(Clone, Debug)]
pub struct VerifiedProjectScope {
    /// Owning organization/tenant identity.
    pub tenant_id: String,
    /// Project whose persisted ownership was verified.
    pub project_id: String,
}

/// A bounded native TLS client with no redirects, proxies, or admission cache.
pub struct IdentityVerifier {
    endpoint: Uri,
    issuer: String,
    audience: String,
    service_key: HeaderValue,
    tls: Arc<rustls::ClientConfig>,
    permits: tokio::sync::Semaphore,
}

/// Invalid configuration, authority refusal, or unavailable live authority.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum IdentityError {
    /// The producer refused the credential or requested tenant permission.
    Denied,
    /// A current authoritative result could not be obtained.
    Unavailable,
    /// Invalid pinned endpoint or authority configuration.
    Configuration,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Admission {
    active: bool,
    subject: Option<String>,
    issuer: Option<String>,
    audience: Option<String>,
    organization_id: Option<String>,
    project_id: Option<String>,
    scopes: Option<Vec<String>>,
    database_id: Option<String>,
    collection: Option<String>,
    action: Option<String>,
    expires_at: Option<u64>,
}

impl IdentityVerifier {
    /// Creates a verifier trusting the platform Mozilla certificate roots.
    ///
    /// # Errors
    /// Rejects non-HTTPS, ambiguous endpoints, or missing authority credentials.
    pub fn new(config: IdentityConfig) -> Result<Self, IdentityError> {
        let roots =
            rustls::RootCertStore::from_iter(webpki_roots::TLS_SERVER_ROOTS.iter().cloned());
        let tls = rustls::ClientConfig::builder_with_provider(Arc::new(
            rustls::crypto::aws_lc_rs::default_provider(),
        ))
        .with_safe_default_protocol_versions()
        .map_err(|_| IdentityError::Configuration)?
        .with_root_certificates(roots)
        .with_no_client_auth();
        Self::with_tls(config, Arc::new(tls))
    }

    fn with_tls(
        config: IdentityConfig,
        tls: Arc<rustls::ClientConfig>,
    ) -> Result<Self, IdentityError> {
        let endpoint: Uri = config.endpoint.parse().map_err(|_| IdentityError::Configuration)?;
        if endpoint.scheme_str() != Some("https")
            || endpoint.host().is_none()
            || endpoint.authority().is_none_or(|value| value.as_str().contains('@'))
            || endpoint.query().is_some()
            || config.endpoint.contains('#')
            || config.issuer.is_empty()
            || config.audience.is_empty()
            || config.service_key.len() < 32
        {
            return Err(IdentityError::Configuration);
        }
        let mut service_key =
            HeaderValue::from_str(&config.service_key).map_err(|_| IdentityError::Configuration)?;
        service_key.set_sensitive(true);
        Ok(Self {
            endpoint,
            issuer: config.issuer,
            audience: config.audience,
            service_key,
            tls,
            permits: tokio::sync::Semaphore::new(32),
        })
    }

    /// Revalidates one credential and its current permission on a server-owned tenant.
    ///
    /// # Errors
    /// Refuses inactive, expired, mismatched, or insufficiently scoped admissions;
    /// network failure always denies admission.
    pub async fn admit(
        &self,
        token: &str,
        organization: &str,
        project: &str,
        database: &str,
        collection: Option<&str>,
        verb: Verbs,
    ) -> Result<Principal, IdentityError> {
        if token.is_empty()
            || token.len() > 16 * 1024
            || organization.is_empty()
            || project.is_empty()
            || database.is_empty()
        {
            return Err(IdentityError::Denied);
        }
        let action = if verb == Verbs::READ {
            "read"
        } else if verb == Verbs::WRITE {
            "write"
        } else {
            "admin"
        };
        let scope = format!("database:{action}");
        let mut input = serde_json::json!({"token":token,"audience":self.audience,
            "organizationId":organization,"projectId":project,"databaseId":database,"action":action});
        if let Some(collection) = collection {
            input["collection"] = collection.into();
        }
        let body = serde_json::to_vec(&input).map_err(|_| IdentityError::Denied)?;
        let _permit = self.permits.try_acquire().map_err(|_| IdentityError::Unavailable)?;
        let admission = tokio::time::timeout(Duration::from_secs(5), self.exchange(body))
            .await
            .map_err(|_| IdentityError::Unavailable)??;
        let now = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map_err(|_| IdentityError::Unavailable)?
            .as_secs();
        let expires = admission.expires_at.ok_or(IdentityError::Denied)?;
        let subject = admission
            .subject
            .filter(|value| !value.is_empty() && value.len() <= 255)
            .ok_or(IdentityError::Denied)?;
        if !admission.active
            || admission.issuer.as_deref() != Some(&self.issuer)
            || admission.audience.as_deref() != Some(&self.audience)
            || admission.organization_id.as_deref() != Some(organization)
            || admission.project_id.as_deref() != Some(project)
            || admission.database_id.as_deref() != Some(database)
            || admission.collection.as_deref() != collection
            || admission.action.as_deref() != Some(action)
            || expires <= now
            || !admission.scopes.as_ref().is_some_and(|scopes| scopes.contains(&scope))
        {
            return Err(IdentityError::Denied);
        }
        let admitted_scope = collection.map_or_else(
            || Scope::database(database, verb),
            |name| Scope::database_collection(database, name, verb),
        );
        Ok(Principal::delegated(
            subject,
            admitted_scope,
            expires.checked_mul(1000).ok_or(IdentityError::Denied)?,
        ))
    }

    /// Verifies project-wide administration against current producer-owned project records.
    ///
    /// # Errors
    /// Rejects invalid selectors, narrowed credentials, and unavailable ownership proof.
    pub async fn admit_project(
        &self,
        token: &str,
        organization: &str,
        project: &str,
    ) -> Result<(Principal, VerifiedProjectScope), IdentityError> {
        let tenant: terrabase::identity::TenantUlid =
            organization.parse().map_err(|_| IdentityError::Denied)?;
        let project_id: terrabase::identity::ProjectUlid =
            project.parse().map_err(|_| IdentityError::Denied)?;
        if tenant.to_string() != organization || project_id.to_string() != project {
            return Err(IdentityError::Denied);
        }
        let principal = self.admit(token, organization, project, "*", None, Verbs::ADMIN).await?;
        let principal = Principal::delegated(
            principal.label().to_owned(),
            Scope::every_database(Verbs::ADMIN),
            principal.expires_at().ok_or(IdentityError::Denied)?,
        );
        Ok((
            principal,
            VerifiedProjectScope {
                tenant_id: organization.to_owned(),
                project_id: project.to_owned(),
            },
        ))
    }

    async fn exchange(&self, body: Vec<u8>) -> Result<Admission, IdentityError> {
        let host = self.endpoint.host().ok_or(IdentityError::Configuration)?;
        let port = self.endpoint.port_u16().unwrap_or(443);
        let name =
            ServerName::try_from(host.to_owned()).map_err(|_| IdentityError::Configuration)?;
        let stream = tokio::net::TcpStream::connect((host, port))
            .await
            .map_err(|_| IdentityError::Unavailable)?;
        let stream = TlsConnector::from(self.tls.clone())
            .connect(name, stream)
            .await
            .map_err(|_| IdentityError::Unavailable)?;
        let (mut sender, connection) = hyper::client::conn::http1::Builder::new()
            .max_headers(32)
            .max_buf_size(16 * 1024)
            .handshake(TokioIo::new(stream))
            .await
            .map_err(|_| IdentityError::Unavailable)?;
        let request = hyper::Request::builder()
            .method("POST")
            .uri(self.endpoint.path())
            .header("host", self.endpoint.authority().ok_or(IdentityError::Configuration)?.as_str())
            .header("content-type", "application/json")
            .header("x-terrascale-identity-service-key", self.service_key.clone())
            .body(Full::new(Bytes::from(body)))
            .map_err(|_| IdentityError::Configuration)?;
        let response = async {
            let response =
                sender.send_request(request).await.map_err(|_| IdentityError::Unavailable)?;
            if response.status() != hyper::StatusCode::OK {
                return Err(IdentityError::Unavailable);
            }
            let body = Limited::new(response.into_body(), 16 * 1024)
                .collect()
                .await
                .map_err(|_| IdentityError::Unavailable)?
                .to_bytes();
            serde_json::from_slice(&body).map_err(|_| IdentityError::Unavailable)
        };
        tokio::pin!(response, connection);
        tokio::select! { biased; result = &mut response => result, _ = &mut connection => response.await }
    }
}

#[cfg(test)]
pub(crate) mod tests {
    use super::*;
    use std::sync::Mutex;

    pub(crate) async fn authority()
    -> (IdentityVerifier, Arc<Mutex<serde_json::Value>>, tokio::task::JoinHandle<()>) {
        authority_for("terrabase-local", "local", "db-a", "read").await
    }

    pub(crate) async fn authority_for(
        organization: &str,
        project: &str,
        database: &str,
        action: &str,
    ) -> (IdentityVerifier, Arc<Mutex<serde_json::Value>>, tokio::task::JoinHandle<()>) {
        let cert = rcgen::generate_simple_self_signed(vec!["localhost".into()]).unwrap();
        let provider = Arc::new(rustls::crypto::aws_lc_rs::default_provider());
        let server = rustls::ServerConfig::builder_with_provider(provider.clone())
            .with_safe_default_protocol_versions()
            .unwrap()
            .with_no_client_auth()
            .with_single_cert(
                vec![cert.cert.der().clone()],
                rustls::pki_types::PrivatePkcs8KeyDer::from(cert.key_pair.serialize_der()).into(),
            )
            .unwrap();
        let mut roots = rustls::RootCertStore::empty();
        roots.add(cert.cert.der().clone()).unwrap();
        let client = rustls::ClientConfig::builder_with_provider(provider)
            .with_safe_default_protocol_versions()
            .unwrap()
            .with_root_certificates(roots)
            .with_no_client_auth();
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let endpoint = format!(
            "https://localhost:{}/api/internal/identity/introspect",
            listener.local_addr().unwrap().port()
        );
        let now =
            std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_secs();
        let reply = Arc::new(Mutex::new(
            serde_json::json!({"active":true,"issuer":"https://identity.example","audience":"https://api.example",
            "subject":"user-1","organizationId":organization,"projectId":project,"databaseId":database,
            "action":action,"scopes":[format!("database:{action}")],"expiresAt":now+60}),
        ));
        let responses = reply.clone();
        let expected = serde_json::json!({"token":"delegated-token","organizationId":organization,"projectId":project,
            "databaseId":database,"action":action,"audience":"https://api.example"});
        let task = tokio::spawn(async move {
            loop {
                let (stream, _) = listener.accept().await.unwrap();
                let server = Arc::new(server.clone());
                let responses = responses.clone();
                let expected = expected.clone();
                tokio::spawn(async move {
                    let Ok(stream) = tokio_rustls::TlsAcceptor::from(server).accept(stream).await
                    else {
                        return;
                    };
                    let _ = hyper::server::conn::http1::Builder::new()
                        .serve_connection(
                            TokioIo::new(stream),
                            hyper::service::service_fn(
                                move |request: hyper::Request<hyper::body::Incoming>| {
                                    let responses = responses.clone();
                                    let expected = expected.clone();
                                    async move {
                                        assert_eq!(
                                            request.headers()["x-terrascale-identity-service-key"],
                                            "s".repeat(32)
                                        );
                                        assert_eq!(
                                            request.uri().path(),
                                            "/api/internal/identity/introspect"
                                        );
                                        let body =
                                            request.into_body().collect().await.unwrap().to_bytes();
                                        let input: serde_json::Value =
                                            serde_json::from_slice(&body).unwrap();
                                        assert_eq!(input, expected);
                                        Ok::<_, std::convert::Infallible>(hyper::Response::new(
                                            Full::new(Bytes::from(
                                                responses.lock().unwrap().to_string(),
                                            )),
                                        ))
                                    }
                                },
                            ),
                        )
                        .await;
                });
            }
        });
        let verifier = IdentityVerifier::with_tls(
            IdentityConfig {
                endpoint,
                issuer: "https://identity.example".into(),
                audience: "https://api.example".into(),
                service_key: "s".repeat(32),
            },
            Arc::new(client),
        )
        .unwrap();
        (verifier, reply, task)
    }

    #[tokio::test]
    async fn project_administration_requires_verified_owner_scope() {
        let org = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
        let project = "01ARZ3NDEKTSV4RRFFQ69G5FAW";
        let (verifier, reply, task) = authority_for(org, project, "*", "admin").await;
        let (principal, scope) =
            verifier.admit_project("delegated-token", org, project).await.unwrap();
        assert!(principal.scope().admits_global(Verbs::ADMIN));
        assert_eq!(scope.tenant_id, org);
        assert_eq!(scope.project_id, project);
        reply.lock().unwrap()["organizationId"] = serde_json::json!("01ARZ3NDEKTSV4RRFFQ69G5FAX");
        assert_eq!(
            verifier.admit_project("delegated-token", org, project).await.unwrap_err(),
            IdentityError::Denied
        );
        task.abort();
    }

    #[tokio::test]
    async fn authority_certificate_and_hostname_are_verified() {
        let (mut verifier, _, task) = authority().await;
        let roots = rustls::RootCertStore::empty();
        verifier.tls = Arc::new(
            rustls::ClientConfig::builder_with_provider(Arc::new(
                rustls::crypto::aws_lc_rs::default_provider(),
            ))
            .with_safe_default_protocol_versions()
            .unwrap()
            .with_root_certificates(roots)
            .with_no_client_auth(),
        );
        assert_eq!(
            verifier
                .admit("delegated-token", "terrabase-local", "local", "db-a", None, Verbs::READ)
                .await
                .unwrap_err(),
            IdentityError::Unavailable
        );
        task.abort();
        let (mut verifier, _, task) = authority().await;
        verifier.endpoint =
            verifier.endpoint.to_string().replace("localhost", "127.0.0.1").parse().unwrap();
        assert_eq!(
            verifier
                .admit("delegated-token", "terrabase-local", "local", "db-a", None, Verbs::READ)
                .await
                .unwrap_err(),
            IdentityError::Unavailable
        );
        task.abort();
    }

    #[tokio::test]
    async fn native_tls_live_revocation_reduction_expiry_and_tenant_binding() {
        let (verifier, reply, task) = authority().await;
        let admit = || {
            verifier.admit("delegated-token", "terrabase-local", "local", "db-a", None, Verbs::READ)
        };
        assert!(admit().await.is_ok());
        let original = reply.lock().unwrap().clone();
        for (field, value) in [
            ("active", serde_json::json!(false)),
            ("issuer", serde_json::json!("https://evil.example")),
            ("audience", serde_json::json!("https://mcp.example")),
            ("organizationId", serde_json::json!("other-org")),
            ("projectId", serde_json::json!("other-project")),
            ("databaseId", serde_json::json!("other-db")),
            ("expiresAt", serde_json::json!(0)),
            ("scopes", serde_json::json!(["dashboard:read"])),
        ] {
            *reply.lock().unwrap() = original.clone();
            reply.lock().unwrap()[field] = value;
            assert_eq!(admit().await.unwrap_err(), IdentityError::Denied, "{field}");
        }
        task.abort();
        assert_eq!(admit().await.unwrap_err(), IdentityError::Unavailable);
    }
}
