use super::*;

use axum::body::Body;
use axum::http::header::CACHE_CONTROL;
use axum::http::{HeaderValue, Request};
use http_body_util::BodyExt;
use std::process::{Command as ProcessCommand, Stdio};
use std::time::Instant;
use terrabase_tbep::proto::terrabase::v1::AuthGroupBootstrapGrant;

// The transport crate intentionally keeps its certificate fixture private to
// that crate. Keep this server test fixture local so the HTTP test exercises
// the same public production constructors and mTLS boundary.
#[cfg(target_os = "linux")]
pub(crate) mod admitted_fixture {
    use super::*;
    use crate::config::{
        DatabaseQuotas, ProductionConfig, ProductionDatabase, ProductionEnvironment,
        ProductionKeys, ProductionPlacement, ProductionTransport, PublicApiConfig, ReleaseBinding,
    };
    use axum::body::Body;
    use axum::http::{Request, StatusCode};
    use rcgen::{
        BasicConstraints, Certificate, CertificateParams, ExtendedKeyUsagePurpose, Ia5String, IsCa,
        KeyPair, KeyUsagePurpose, SanType,
    };
    use rustls::pki_types::{CertificateDer, PrivateKeyDer, PrivatePkcs8KeyDer};
    use std::net::{Ipv4Addr, UdpSocket};
    use std::path::PathBuf;
    use std::time::Duration;
    use terrabase::encryption::{
        DatabaseKeyVersion, KeyPurpose, KeyScope, KeyVersionMetadata, OciVaultDatabaseKeys,
    };
    use terrabase::identity::{
        ClusterUlid, DatabaseUlid, MemberUlid, ProjectUlid, TenantUlid, Ulid,
    };
    use terrabase::member::EnvironmentName;
    use terrabase::regions::RegionCode;
    use terrabase_auth::{AuthAuthority, AuthNetwork, SqliteStore};
    use terrabase_cluster::production::{
        ProductionPeerAdmission, ProductionQuicEndpoint, ProductionRegion, ProductionTlsMaterial,
        ProductionTransportConfig, ProductionTransportError, ProductionTransportLimits,
    };
    use terrabase_tbep::auth_bootstrap::{AuthBootstrapBoundary, AuthBootstrapJournal};
    use terrabase_tbep::proto::terrabase::v1::{AuthGroupBootstrapGrant, AuthGroupVoter};
    use tower::ServiceExt;

    pub struct Fixture {
        pub runtime: Arc<AuthRuntime>,
        pub database: String,
        pub key: &'static str,
        pub remote: AuthAuthority,
        pub remote_endpoint: Arc<ProductionQuicEndpoint>,
        pub local_endpoint: Arc<ProductionQuicEndpoint>,
        pub local_receive: tokio::task::JoinHandle<()>,
        pub remote_receive: tokio::task::JoinHandle<()>,
        pub root: PathBuf,
        pub keys: crate::test_keys::KeyDeliveryFixture,
    }

    fn limits(max_frame_bytes: usize) -> ProductionTransportLimits {
        ProductionTransportLimits::try_new(
            max_frame_bytes,
            8,
            8,
            128 * 1024 * 1024,
            Duration::from_secs(5),
            Duration::from_secs(5),
            Duration::from_secs(10),
            Duration::from_secs(5),
        )
        .unwrap()
    }

    fn member_tls(
        identity: &terrabase_cluster::production::MemberWorkloadIdentity,
        name: &str,
        issuer: &Certificate,
        issuer_key: &KeyPair,
        anchor: &CertificateDer<'static>,
    ) -> ProductionTlsMaterial {
        let key = KeyPair::generate().unwrap();
        let mut params = CertificateParams::new(vec![name.to_owned()]).unwrap();
        params
            .subject_alt_names
            .push(SanType::URI(Ia5String::try_from(identity.as_str()).unwrap()));
        params.extended_key_usages =
            vec![ExtendedKeyUsagePurpose::ServerAuth, ExtendedKeyUsagePurpose::ClientAuth];
        let cert = params.signed_by(&key, issuer, issuer_key).unwrap();
        ProductionTlsMaterial::try_new(
            vec![CertificateDer::from(cert.der().to_vec())],
            PrivateKeyDer::Pkcs8(PrivatePkcs8KeyDer::from(key.serialize_der())),
            vec![anchor.clone()],
        )
        .unwrap()
    }

    fn endpoints(
        first: &terrabase_cluster::production::MemberWorkloadIdentity,
        second: &terrabase_cluster::production::MemberWorkloadIdentity,
        transport_limits: ProductionTransportLimits,
    ) -> (ProductionQuicEndpoint, ProductionQuicEndpoint) {
        let a = UdpSocket::bind((Ipv4Addr::LOCALHOST, 0)).unwrap();
        let b = UdpSocket::bind((Ipv4Addr::LOCALHOST, 0)).unwrap();
        let aa = a.local_addr().unwrap();
        let bb = b.local_addr().unwrap();
        drop((a, b));
        let issuer_key = KeyPair::generate().unwrap();
        let mut issuer_params = CertificateParams::new(Vec::new()).unwrap();
        issuer_params.is_ca = IsCa::Ca(BasicConstraints::Unconstrained);
        issuer_params.key_usages =
            vec![KeyUsagePurpose::DigitalSignature, KeyUsagePurpose::KeyCertSign];
        let issuer = issuer_params.self_signed(&issuer_key).unwrap();
        let anchor = CertificateDer::from(issuer.der().to_vec());
        let atls = member_tls(first, "member-a.internal", &issuer, &issuer_key, &anchor);
        let btls = member_tls(second, "member-b.internal", &issuer, &issuer_key, &anchor);
        let ac = ProductionTransportConfig::try_new(
            aa,
            first.clone(),
            ProductionRegion::Gru,
            1,
            1,
            vec![
                ProductionPeerAdmission::try_new(
                    second.clone(),
                    ProductionRegion::Gru,
                    bb,
                    "member-b.internal",
                )
                .unwrap(),
            ],
            transport_limits.clone(),
        )
        .unwrap();
        let bc = ProductionTransportConfig::try_new(
            bb,
            second.clone(),
            ProductionRegion::Gru,
            1,
            1,
            vec![
                ProductionPeerAdmission::try_new(
                    first.clone(),
                    ProductionRegion::Gru,
                    aa,
                    "member-a.internal",
                )
                .unwrap(),
            ],
            transport_limits,
        )
        .unwrap();
        (
            ProductionQuicEndpoint::bind(ac, atls).unwrap(),
            ProductionQuicEndpoint::bind(bc, btls).unwrap(),
        )
    }

    #[tokio::test]
    async fn auth_network_requires_supported_frame_ceiling() {
        let cluster = ClusterUlid::generate();
        let first = MemberUlid::generate();
        let second = MemberUlid::generate();
        let first_identity = terrabase_cluster::production::MemberWorkloadIdentity::try_new(
            "production",
            &cluster.to_text(),
            &first.to_text(),
        )
        .unwrap();
        let second_identity = terrabase_cluster::production::MemberWorkloadIdentity::try_new(
            "production",
            &cluster.to_text(),
            &second.to_text(),
        )
        .unwrap();
        let voters = BTreeMap::from([
            (1, first_identity.member().clone()),
            (2, second_identity.member().clone()),
        ]);
        let placement = BTreeSet::from([ProductionRegion::Gru]);
        let (undersized, supported) =
            endpoints(&first_identity, &second_identity, limits(1024 * 1024));
        assert!(matches!(
            AuthNetwork::new(Arc::new(undersized), "group", voters.clone(), &placement),
            Err(terrabase_auth::AuthorityError::InvalidConfiguration)
        ));
        drop(supported);

        let (supported, peer) =
            endpoints(&first_identity, &second_identity, limits(2 * 1024 * 1024));
        assert!(AuthNetwork::new(Arc::new(supported), "group", voters, &placement).is_ok());
        drop(peer);
    }

    pub(crate) fn production_config(
        scope: (TenantUlid, ProjectUlid, DatabaseUlid),
        cluster: ClusterUlid,
        member: MemberUlid,
        peer: MemberUlid,
        root: &std::path::Path,
        keys: &crate::test_keys::KeyDeliveryFixture,
    ) -> ProductionConfig {
        let (tenant, project, database) = scope;
        let environment = EnvironmentName::try_new("production").unwrap();
        ProductionConfig {
            backup_disk: crate::config::ProductionBackupDisk {
                staging_bytes_per_stream: 8 * 1024 * 1024 * 1024,
                inventory_bytes_per_stream: 16 * 1024 * 1024,
                total_staging_bytes: 8 * 1024 * 1024 * 1024 + 16 * 1024 * 1024,
            },
            environment: ProductionEnvironment { name: environment, cluster, member },
            databases: vec![ProductionDatabase {
                tenant,
                project,
                database,
                region: RegionCode::GRU,
                selected_regions: BTreeSet::from([RegionCode::GRU]),
                location: root.join("database"),
                policy_journal: root.join("policy.sqlite"),
                backup_root: root.join("backup"),
                encryption_provider: terrabase::encryption::KeyProviderIdentity::OciVault,
                encryption_key: DatabaseKeyVersion::from_text("01ARZ3NDEKTSV4RRFFQ69G5FB0")
                    .unwrap(),
                quotas: DatabaseQuotas {
                    max_storage_bytes: 1 << 30,
                    max_documents: 1000,
                    max_inflight: 16,
                },
                enabled: true,
                backup: None,
                byok: None,
            }],
            placement: ProductionPlacement {
                generation: 1,
                epoch: 1,
                regions: BTreeMap::from([(RegionCode::GRU, [member, peer])]),
            },
            transport: ProductionTransport {
                max_inflight: 16,
                credential_rotation: None,
                tbep_listen: "127.0.0.1:0".parse().unwrap(),
                quic_listen: "127.0.0.1:0".parse().unwrap(),
                metrics_listen: "127.0.0.1:0".parse().unwrap(),
                allow_wildcard_tbep: false,
                allow_wildcard_quic: false,
                allow_wildcard_metrics: false,
                server_certificate: root.join("server.pem"),
                server_private_key: root.join("server.key"),
                client_ca: root.join("ca.pem"),
                peers: vec![],
            },
            release: ReleaseBinding { release: "test".into(), capabilities: BTreeSet::new() },
            control: None,
            keys: Some(ProductionKeys {
                socket: keys.socket.clone(),
                orchestrator_uid: keys.uid,
                socket_gid: keys.gid,
            }),
            budget: None,
            member_admission: None,
        }
    }

    pub(crate) fn public_config(root: &std::path::Path, key_path: PathBuf) -> PublicApiConfig {
        PublicApiConfig {
            byo_jwt: None,
            lifecycle_publication: None,
            billing_closure: None,
            credential_rotation: None,
            provisioning: None,
            identity: None,
            listen: "127.0.0.1:0".parse().unwrap(),
            data_root: root.to_owned(),
            bootstrap_key_file: Some(key_path),
            origin_proof_key_file: None,
            origin_proof_previous_key_file: None,
            capability_key_file: None,
            capability_previous_key_file: None,
        }
    }

    pub async fn create() -> Fixture {
        create_with_limits(limits(16 * 1024 * 1024)).await
    }

    pub async fn create_with_production_limits() -> Fixture {
        let limits = crate::runtime::production_transport_limits().expect("production limits");
        create_with_limits(limits).await
    }

    #[allow(clippy::too_many_lines)]
    async fn create_with_limits(transport_limits: ProductionTransportLimits) -> Fixture {
        let root = std::env::temp_dir().join(format!("terrabase-auth-http-{}", Ulid::new()));
        std::fs::create_dir_all(&root).unwrap();
        #[cfg(unix)]
        {
            use std::os::unix::fs::PermissionsExt;
            std::fs::set_permissions(&root, std::fs::Permissions::from_mode(0o700)).unwrap();
        }
        let tenant = TenantUlid::generate();
        let project = ProjectUlid::generate();
        let database = DatabaseUlid::generate();
        let first = MemberUlid::generate();
        let second = MemberUlid::generate();
        let cluster = ClusterUlid::generate();
        let first_identity = terrabase_cluster::production::MemberWorkloadIdentity::try_new(
            "production",
            &cluster.to_text(),
            &first.to_text(),
        )
        .unwrap();
        let second_identity = terrabase_cluster::production::MemberWorkloadIdentity::try_new(
            "production",
            &cluster.to_text(),
            &second.to_text(),
        )
        .unwrap();
        let (local, remote_endpoint) =
            endpoints(&first_identity, &second_identity, transport_limits);
        let local = Arc::new(local);
        let remote_endpoint = Arc::new(remote_endpoint);
        let keys = crate::test_keys::key_delivery();
        let group = Ulid::new().to_string();
        let boundary = AuthBootstrapBoundary::new(
            "production",
            &cluster.to_text(),
            &first.to_text(),
            BTreeSet::from([terrabase_cluster::production::DatabaseRouteScope::new(
                tenant, project, database,
            )]),
            BTreeMap::from([(first.to_text(), "GRU".into()), (second.to_text(), "GRU".into())]),
        )
        .unwrap();
        let journal = Arc::new(
            AuthBootstrapJournal::open(&root.join("auth-bootstrap.sqlite"), boundary).unwrap(),
        );
        let local_incarnation = journal.identity().unwrap().storage_incarnation;
        let grant = AuthGroupBootstrapGrant {
            environment: "production".into(),
            cluster: cluster.to_text(),
            group_id: group.clone(),
            tenant: tenant.to_text(),
            project: project.to_text(),
            database: database.to_text(),
            generation: 1,
            regions: vec!["GRU".into()],
            voters: vec![
                AuthGroupVoter {
                    node_id: 1,
                    member: first.to_text(),
                    region: "GRU".into(),
                    spiffe_uri: first_identity.as_str().into(),
                    storage_incarnation: local_incarnation,
                },
                AuthGroupVoter {
                    node_id: 2,
                    member: second.to_text(),
                    region: "GRU".into(),
                    spiffe_uri: second_identity.as_str().into(),
                    storage_incarnation: Ulid::new().to_string(),
                },
            ],
            authority_revision: 1,
            genesis_node: 1,
        };
        journal
            .bootstrap(
                "test",
                terrabase_tbep::proto::terrabase::v1::AuthGroupBootstrapRequest {
                    operation_id: Ulid::new().to_string(),
                    grant: Some(grant),
                },
            )
            .unwrap();
        let scope = terrabase::member::DatabaseScope::new(
            "production",
            tenant,
            project,
            database,
            RegionCode::GRU,
        )
        .unwrap();
        let source = OciVaultDatabaseKeys::new(
            keys.provider.clone(),
            KeyVersionMetadata::new(
                KeyScope::new_with_project(
                    "production",
                    tenant,
                    project,
                    database,
                    KeyPurpose::Data,
                )
                .unwrap(),
                DatabaseKeyVersion::from_text("01ARZ3NDEKTSV4RRFFQ69G5FB0").unwrap(),
            ),
            RegionCode::GRU,
        )
        .unwrap();
        let remote_store = SqliteStore::open_encrypted(
            &root.join("remote.sqlite"),
            &group,
            Arc::new(source),
            true,
        )
        .unwrap();
        let voters = BTreeMap::from([
            (1, first_identity.member().clone()),
            (2, second_identity.member().clone()),
        ]);
        let placement = BTreeSet::from([ProductionRegion::Gru]);
        let remote_network =
            AuthNetwork::new(Arc::clone(&remote_endpoint), &group, voters.clone(), &placement)
                .unwrap();
        let remote = AuthAuthority::recover_admitted(
            remote_store,
            remote_network.clone(),
            terrabase_auth::consensus_config(&group),
            Duration::from_secs(10),
        )
        .await
        .unwrap();
        let remote_for_task = remote.clone();
        let remote_network_for_task = remote_network.clone();
        let remote_listener = Arc::clone(&remote_endpoint);
        let remote_receive = tokio::spawn(async move {
            loop {
                let connection = match remote_listener.accept().await {
                    Ok(Some(connection)) => connection,
                    Ok(None) => break,
                    Err(
                        ProductionTransportError::Timeout
                        | ProductionTransportError::ConnectionFailed
                        | ProductionTransportError::PeerNotAdmitted
                        | ProductionTransportError::InvalidPeerCertificate
                        | ProductionTransportError::IdentityMismatch
                        | ProductionTransportError::ProtocolMismatch
                        | ProductionTransportError::StaleMembership
                        | ProductionTransportError::Replay,
                    ) => continue,
                    Err(ProductionTransportError::Saturated) => {
                        tokio::time::sleep(Duration::from_millis(10)).await;
                        continue;
                    }
                    Err(error) => {
                        let _ = error;
                        break;
                    }
                };
                let authority = remote_for_task.clone();
                let network = remote_network_for_task.clone();
                tokio::spawn(async move {
                    if let Ok(bytes) = connection.receive_frame().await {
                        let _ = network.receive_frame(&connection, &authority, &bytes).await;
                    }
                });
            }
        });
        let key_path = root.join("bootstrap.key");
        std::fs::write(&key_path, "test-bootstrap-key-01234567890123456789").unwrap();
        #[cfg(unix)]
        {
            use std::os::unix::fs::PermissionsExt;
            std::fs::set_permissions(&key_path, std::fs::Permissions::from_mode(0o600)).unwrap();
        }
        let public = public_config(&root, key_path);
        let production =
            production_config((tenant, project, database), cluster, first, second, &root, &keys);
        let runtime =
            AuthRuntime::new(Arc::clone(&local), Arc::clone(&journal), production, &public)
                .unwrap();
        runtime.reconcile().await.unwrap();
        let local_for_task = Arc::clone(&runtime);
        let local_receive = tokio::spawn(async move {
            let _ = local_for_task.receive().await;
        });
        let _ = scope;
        Fixture {
            runtime,
            database: database.to_text(),
            key: "test-bootstrap-key-01234567890123456789",
            remote,
            remote_endpoint,
            local_endpoint: local,
            local_receive,
            remote_receive,
            root,
            keys,
        }
    }

    pub async fn request(
        runtime: &Arc<AuthRuntime>,
        request: Request<Body>,
    ) -> (StatusCode, serde_json::Value, axum::http::HeaderMap) {
        let response = router(Arc::clone(runtime)).oneshot(request).await.unwrap();
        let status = response.status();
        let headers = response.headers().clone();
        let body = response.into_body().collect().await.unwrap().to_bytes();
        let json = serde_json::from_slice(&body).unwrap();
        (status, json, headers)
    }
}

#[test]
fn auth_result_keeps_transaction_identity_and_camel_case_shape() {
    let mut result = AuthResult::record(None, true);
    result.transaction_id = Some("01JTRANSACTION#1".into());
    result.expires_at_ms = Some(1_735_000_000_000);

    let value = serde_json::to_value(result).expect("auth result serializes");
    assert_eq!(value["record"], serde_json::Value::Null);
    assert_eq!(value["matched"], true);
    assert_eq!(value["transactionId"], "01JTRANSACTION#1");
    assert_eq!(value["expiresAtMs"], 1_735_000_000_000u64);
    assert!(value.get("transaction_id").is_none());
    assert!(value.get("records").is_none());
}

#[tokio::test]
async fn error_responses_have_stable_envelope() {
    let response = error(StatusCode::BAD_REQUEST, "idempotency_key_required");
    assert_eq!(response.status(), StatusCode::BAD_REQUEST);

    let body = response.into_body().collect().await.expect("body collects").to_bytes();
    let value: serde_json::Value = serde_json::from_slice(&body).expect("JSON error envelope");
    assert_eq!(value["error"]["code"], "idempotency_key_required");
    assert!(value["error"]["message"].as_str().is_some());
    assert!(value["error"]["fix"].as_str().is_some());
}

#[cfg(target_os = "linux")]
#[tokio::test]
async fn auth_http_errors_keep_status_envelope_and_headers() {
    let fixture = admitted_fixture::create().await;
    let auth = format!("Bearer {}", fixture.key);
    fixture.runtime.keys.register(
        "auth-error-read-only-key-01234567890123456789",
        "read-only",
        Scope::every_database(Verbs::READ),
    );
    let read_only_auth = "Bearer auth-error-read-only-key-01234567890123456789";
    let mut authorized = HeaderMap::new();
    authorized.insert("authorization", auth.parse().unwrap());
    let missing = fixture.runtime.domain(&fixture.database, &HeaderMap::new(), Verbs::READ);
    let Err(missing) = missing else { panic!("missing token admitted") };
    assert_eq!(missing.status, StatusCode::UNAUTHORIZED);
    assert_eq!(missing.code, "unauthorized");
    let mut read_only_headers = HeaderMap::new();
    read_only_headers.insert("authorization", read_only_auth.parse().unwrap());
    let denied = fixture.runtime.domain(&fixture.database, &read_only_headers, Verbs::ADMIN);
    let Err(denied) = denied else { panic!("denied scope admitted") };
    assert_eq!(denied.status, StatusCode::FORBIDDEN);
    assert_eq!(denied.code, "forbidden");
    let domains = fixture.runtime.domains.read().unwrap().clone();
    fixture.runtime.domains.write().unwrap().clear();
    let unavailable = fixture.runtime.domain(&fixture.database, &authorized, Verbs::READ);
    let Err(unavailable) = unavailable else { panic!("missing domain admitted") };
    assert_eq!(unavailable.status, StatusCode::SERVICE_UNAVAILABLE);
    assert_eq!(unavailable.code, "auth_authority_unavailable");
    *fixture.runtime.domains.write().unwrap() = domains;
    for (failure, status, code) in [
        (missing, StatusCode::UNAUTHORIZED, "unauthorized"),
        (denied, StatusCode::FORBIDDEN, "forbidden"),
        (unavailable, StatusCode::SERVICE_UNAVAILABLE, "auth_authority_unavailable"),
    ] {
        let response = failure.into_response();
        assert_eq!(response.status(), status);
        let bytes = response.into_body().collect().await.unwrap().to_bytes();
        let body: serde_json::Value = serde_json::from_slice(&bytes).unwrap();
        assert_eq!(
            body,
            serde_json::json!({"error": {
                "code": code,
                "message": "Auth operation could not be completed safely",
                "fix": "Resolve uncertain mutations using the same idempotency key; retry when the admitted authority is available"
            }})
        );
    }
    let (status, body, headers) = admitted_fixture::request(
        &fixture.runtime,
        Request::post(format!("/api/v1/databases/{}/auth/count", fixture.database))
            .header("authorization", &auth)
            .header("content-type", "application/json")
            .body(Body::from(
                r#"{"model":"users","predicates":[{"field":"x","value":"x","operator":"invalid"}]}"#,
            ))
            .unwrap(),
    )
    .await;
    assert_eq!(status, StatusCode::BAD_REQUEST);
    assert_eq!(body["error"]["code"], "invalid_auth_operation");
    assert_eq!(body["error"]["message"], "Auth operation could not be completed safely");
    assert_eq!(
        body["error"]["fix"],
        "Resolve uncertain mutations using the same idempotency key; retry when the admitted authority is available"
    );
    assert_eq!(headers.get(CACHE_CONTROL).and_then(|value| value.to_str().ok()), Some("no-store"));
    fixture.runtime.shutdown().await;
    fixture.remote.shutdown().await.unwrap();
    fixture.local_receive.abort();
    fixture.remote_receive.abort();
    let _ = fixture.local_receive.await;
    let _ = fixture.remote_receive.await;
    drop(fixture.runtime);
    drop(fixture.local_endpoint);
    drop(fixture.remote_endpoint);
    let _ = std::fs::remove_dir_all(fixture.root);
}

#[cfg(target_os = "linux")]
#[tokio::test]
async fn auth_domain_routing_rejects_ambiguous_database_matches() {
    let fixture = admitted_fixture::create().await;
    let domain = fixture.runtime.domains.read().unwrap().values().next().unwrap().clone();

    assert!(unique_domain(std::iter::once(&domain), &fixture.database).is_some());
    assert!(unique_domain([&domain, &domain].into_iter(), &fixture.database).is_none());

    fixture.runtime.shutdown().await;
    fixture.local_receive.abort();
    fixture.remote_receive.abort();
    fixture.remote.shutdown().await.unwrap();
    let _ = std::fs::remove_dir_all(&fixture.root);
}

#[test]
fn placement_response_marks_private_result_uncacheable() {
    let grant =
        AuthGroupBootstrapGrant { regions: vec!["GRU".into(), "SFO".into()], ..Default::default() };
    let response = placement_response((StatusCode::OK, Body::empty()).into_response(), &grant);
    assert_eq!(response.status(), StatusCode::OK);
    assert_eq!(response.headers()[CACHE_CONTROL], "no-store");
    assert_eq!(response.headers()["x-terrascale-placement"], "GRU,SFO");
}

#[test]
fn openapi_registers_transactional_record_get() {
    let (_, document) = routes().split_for_parts();
    let paths = document.paths.paths;
    let path = paths
        .get("/api/v1/databases/{database}/auth/records/{model}/{id}")
        .expect("transactional record GET is published");
    assert!(path.get.is_some());
}

#[cfg(target_os = "linux")]
#[tokio::test(flavor = "multi_thread", worker_threads = 4)]
#[allow(clippy::too_many_lines)]
async fn admitted_quic_http_transaction_reads_staged_and_committed_state() {
    let fixture = admitted_fixture::create().await;
    let auth = format!("Bearer {}", fixture.key);
    let mut begin = admitted_fixture::request(
        &fixture.runtime,
        Request::post(format!("/api/v1/databases/{}/auth/transactions/begin", fixture.database))
            .header("authorization", &auth)
            .header("idempotency-key", "begin-1")
            .header("content-type", "application/json")
            .body(Body::from(r#"{"transactionId":"http-tx"}"#))
            .unwrap(),
    )
    .await;
    for _ in 0..100 {
        if begin.0 != StatusCode::SERVICE_UNAVAILABLE {
            break;
        }
        tokio::time::sleep(std::time::Duration::from_millis(100)).await;
        begin = admitted_fixture::request(
            &fixture.runtime,
            Request::post(format!(
                "/api/v1/databases/{}/auth/transactions/begin",
                fixture.database
            ))
            .header("authorization", &auth)
            .header("idempotency-key", "begin-1")
            .header("content-type", "application/json")
            .body(Body::from(r#"{"transactionId":"http-tx"}"#))
            .unwrap(),
        )
        .await;
    }
    assert_eq!(begin.0, StatusCode::OK, "begin response: {:?}", begin.1);
    let transaction = begin.1["transactionId"].as_str().unwrap().to_owned();
    let insert = admitted_fixture::request(
        &fixture.runtime,
        Request::post(format!("/api/v1/databases/{}/auth/insertUnique", fixture.database))
            .header("authorization", &auth)
            .header("idempotency-key", "insert-1")
            .header("x-terrascale-auth-transaction", &transaction)
            .header("content-type", "application/json")
            .body(Body::from(r#"{"model":"user","record":{"id":"u1","name":"before"}}"#))
            .unwrap(),
    )
    .await;
    assert_eq!(insert.0, StatusCode::OK);
    let staged = admitted_fixture::request(
        &fixture.runtime,
        Request::get(format!("/api/v1/databases/{}/auth/records/user/u1", fixture.database))
            .header("authorization", &auth)
            .header("idempotency-key", "read-1")
            .header("x-terrascale-auth-transaction", &transaction)
            .body(Body::empty())
            .unwrap(),
    )
    .await;
    assert_eq!(staged.0, StatusCode::OK);
    assert_eq!(staged.1["matched"], true);
    assert_eq!(staged.1["record"]["name"], "before");
    assert_eq!(staged.2["cache-control"], "no-store");
    assert_eq!(staged.2["x-terrascale-placement"], "GRU");
    // Join lookup must see staged records in both models, even when the
    // selected base fields omit the foreign key used by the relation.
    for (id, label, rank) in [("a1", "first", 1), ("a2", "second", 2)] {
        let response = admitted_fixture::request(
            &fixture.runtime,
            Request::post(format!("/api/v1/databases/{}/auth/insertUnique", fixture.database))
                .header("authorization", &auth)
                .header("idempotency-key", format!("insert-{id}"))
                .header("x-terrascale-auth-transaction", &transaction)
                .header("content-type", "application/json")
                .body(Body::from(serde_json::json!({
                    "model": "account", "record": {"id": id, "userId": "u1", "label": label, "rank": rank}
                }).to_string()))
                .unwrap(),
        ).await;
        assert_eq!(response.0, StatusCode::OK, "staged relation insert: {:?}", response.1);
    }
    let relation_query = serde_json::json!({
        "model": "account", "predicates": [], "offset": 0, "limit": 2,
        "select": ["label"], "sortBy": {"field": "rank", "direction": "desc"},
        "joins": [{"model": "user", "outputField": "user", "fromField": "userId", "toField": "id", "relation": "one-to-one", "limit": 1}]
    });
    let relation_page = admitted_fixture::request(
        &fixture.runtime,
        Request::post(format!("/api/v1/databases/{}/auth/findPage", fixture.database))
            .header("authorization", &auth)
            .header("idempotency-key", "read-joined-stage")
            .header("x-terrascale-auth-transaction", &transaction)
            .header("content-type", "application/json")
            .body(Body::from(relation_query.to_string()))
            .unwrap(),
    )
    .await;
    assert_eq!(relation_page.0, StatusCode::OK, "staged relation query: {:?}", relation_page.1);
    assert_eq!(
        relation_page.1["records"],
        serde_json::json!([
            {"label": "second", "user": {"id": "u1", "name": "before"}},
            {"label": "first", "user": {"id": "u1", "name": "before"}}
        ])
    );
    let unstaged_relation_page = admitted_fixture::request(
        &fixture.runtime,
        Request::post(format!("/api/v1/databases/{}/auth/findPage", fixture.database))
            .header("authorization", &auth)
            .header("content-type", "application/json")
            .body(Body::from(relation_query.to_string()))
            .unwrap(),
    )
    .await;
    assert_eq!(unstaged_relation_page.0, StatusCode::OK);
    assert_eq!(unstaged_relation_page.1["records"], serde_json::json!([]));
    let mut malformed_query = relation_query.clone();
    malformed_query["sortBy"]["direction"] = serde_json::json!("sideways");
    let malformed_page = {
        use tower::ServiceExt;
        router(Arc::clone(&fixture.runtime))
            .oneshot(
                Request::post(format!("/api/v1/databases/{}/auth/findPage", fixture.database))
                    .header("authorization", &auth)
                    .header("content-type", "application/json")
                    .body(Body::from(malformed_query.to_string()))
                    .unwrap(),
            )
            .await
            .unwrap()
    };
    assert_eq!(malformed_page.status(), StatusCode::UNPROCESSABLE_ENTITY);
    assert_eq!(malformed_page.headers()["cache-control"], "no-store");
    fixture.runtime.keys.register(
        "read-only-http-key-0123456789",
        "read-only",
        Scope::database(&fixture.database, Verbs::READ),
    );
    let read_auth = "Bearer read-only-http-key-0123456789";
    let mut count = admitted_fixture::request(
        &fixture.runtime,
        Request::post(format!("/api/v1/databases/{}/auth/count", fixture.database))
            .header("authorization", read_auth)
            .header("content-type", "application/json")
            .body(Body::from(r#"{"model":"user","predicates":[]}"#))
            .unwrap(),
    )
    .await;
    for _ in 0..20 {
        if count.0 != StatusCode::SERVICE_UNAVAILABLE {
            break;
        }
        tokio::time::sleep(std::time::Duration::from_millis(100)).await;
        count = admitted_fixture::request(
            &fixture.runtime,
            Request::post(format!("/api/v1/databases/{}/auth/count", fixture.database))
                .header("authorization", read_auth)
                .header("content-type", "application/json")
                .body(Body::from(r#"{"model":"user","predicates":[]}"#))
                .unwrap(),
        )
        .await;
    }
    assert_eq!(count.0, StatusCode::OK, "count response: {:?}", count.1);
    assert_eq!(count.1["count"], 0);
    let page = admitted_fixture::request(
        &fixture.runtime,
        Request::post(format!("/api/v1/databases/{}/auth/findPage", fixture.database))
            .header("authorization", read_auth)
            .header("content-type", "application/json")
            .body(Body::from(r#"{"model":"user","predicates":[],"offset":0,"limit":10}"#))
            .unwrap(),
    )
    .await;
    assert_eq!(page.0, StatusCode::OK);
    assert_eq!(page.1["records"].as_array().unwrap().len(), 0);
    let denied_update = admitted_fixture::request(
        &fixture.runtime,
        Request::post(format!("/api/v1/databases/{}/auth/updateOne", fixture.database))
            .header("authorization", read_auth)
            .header("idempotency-key", "read-only-write")
            .header("content-type", "application/json")
            .body(Body::from(r#"{"model":"user","predicates":[],"update":{"name":"denied"}}"#))
            .unwrap(),
    )
    .await;
    assert_eq!(denied_update.0, StatusCode::FORBIDDEN);
    let malformed_transaction = admitted_fixture::request(&fixture.runtime, Request::get(format!("/api/v1/databases/{}/auth/records/user/u1", fixture.database)).header("authorization", &auth).header("idempotency-key", "malformed-tx").header("x-terrascale-auth-transaction", HeaderValue::from_static("xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx")).body(Body::empty()).unwrap()).await;
    assert_eq!(malformed_transaction.0, StatusCode::BAD_REQUEST);
    let invalid_predicate = admitted_fixture::request(&fixture.runtime, Request::post(format!("/api/v1/databases/{}/auth/findPage", fixture.database)).header("authorization", &auth).header("content-type", "application/json").body(Body::from(r#"{"model":"user","predicates":[{"field":"id","value":"u1","operator":"not-a-real-operator"}],"offset":0,"limit":10}"#)).unwrap()).await;
    assert_eq!(invalid_predicate.0, StatusCode::BAD_REQUEST);
    let ordinary_before_commit = admitted_fixture::request(
        &fixture.runtime,
        Request::get(format!("/api/v1/databases/{}/auth/records/user/u1", fixture.database))
            .header("authorization", &auth)
            .body(Body::empty())
            .unwrap(),
    )
    .await;
    assert_eq!(ordinary_before_commit.0, StatusCode::OK);
    assert_eq!(ordinary_before_commit.1["matched"], false);
    let missing_transaction_key = admitted_fixture::request(
        &fixture.runtime,
        Request::get(format!("/api/v1/databases/{}/auth/records/user/u1", fixture.database))
            .header("authorization", &auth)
            .header("x-terrascale-auth-transaction", &transaction)
            .body(Body::empty())
            .unwrap(),
    )
    .await;
    assert_eq!(missing_transaction_key.0, StatusCode::BAD_REQUEST);
    assert_eq!(missing_transaction_key.1["error"]["code"], "idempotency_key_required");
    let commit = admitted_fixture::request(
        &fixture.runtime,
        Request::post(format!("/api/v1/databases/{}/auth/transactions/commit", fixture.database))
            .header("authorization", &auth)
            .header("idempotency-key", "commit-1")
            .header("content-type", "application/json")
            .body(Body::from(format!(r#"{{"transactionId":"{transaction}"}}"#)))
            .unwrap(),
    )
    .await;
    assert_eq!(commit.0, StatusCode::OK);
    let ordinary_after_commit = admitted_fixture::request(
        &fixture.runtime,
        Request::get(format!("/api/v1/databases/{}/auth/records/user/u1", fixture.database))
            .header("authorization", &auth)
            .body(Body::empty())
            .unwrap(),
    )
    .await;
    assert_eq!(ordinary_after_commit.0, StatusCode::OK);
    assert_eq!(ordinary_after_commit.1["record"]["id"], "u1");
    exercise_concurrent_and_rollback(&fixture, &auth).await;
}

#[allow(clippy::too_many_lines)]
async fn exercise_concurrent_and_rollback(fixture: &admitted_fixture::Fixture, auth: &str) {
    // Exercise both admitted authorities concurrently. The elected follower
    // routes its operations over authenticated member transport.
    let mut forwarded = Vec::new();
    for index in 0..12 {
        let follower = fixture.remote.clone();
        forwarded.push(tokio::spawn(async move {
            tokio::time::timeout(
                std::time::Duration::from_secs(10),
                follower.mutate(Command {
                    idempotency_key: format!("forwarded-{index}"),
                    observed_at_ms: 1,
                    action: Action::InsertUnique {
                        model: "forwarded".into(),
                        record: BTreeMap::from([
                            ("id".into(), serde_json::Value::String(format!("f-{index}"))),
                            ("index".into(), serde_json::Value::from(index)),
                        ]),
                    },
                }),
            )
            .await
        }));
    }
    let mut concurrent = Vec::new();
    for index in 0..16 {
        let runtime = Arc::clone(&fixture.runtime);
        let database = fixture.database.clone();
        let auth = auth.to_owned();
        concurrent.push(tokio::spawn(async move {
            let response = admitted_fixture::request(
                &runtime,
                Request::get(format!("/api/v1/databases/{database}/auth/records/user/u1"))
                    .header("authorization", auth)
                    .body(Body::empty())
                    .unwrap(),
            )
            .await;
            (index, response.0)
        }));
    }
    let mut completed = 0;
    for task in concurrent {
        let (_, status) = task.await.unwrap();
        assert!(
            status.is_success() || status == StatusCode::SERVICE_UNAVAILABLE,
            "unexpected load response: {status}"
        );
        if status.is_success() {
            completed += 1;
        }
    }
    assert!(completed > 0, "reads must make progress under concurrent load");
    let mut forwarded_successes = 0;
    let mut forwarded_failures = Vec::new();
    for task in forwarded {
        let result = task.await.unwrap();
        if matches!(result, Ok(Ok(Outcome::Applied | Outcome::Record { .. }))) {
            forwarded_successes += 1;
        } else {
            forwarded_failures.push(format!("{result:?}"));
        }
    }
    assert!(
        forwarded_successes > 0,
        "follower forwarding must admit at least one bounded mutation: {forwarded_failures:?}"
    );
    let rollback_begin = admitted_fixture::request(
        &fixture.runtime,
        Request::post(format!("/api/v1/databases/{}/auth/transactions/begin", fixture.database))
            .header("authorization", auth)
            .header("idempotency-key", "rollback-begin")
            .header("content-type", "application/json")
            .body(Body::from(r#"{"transactionId":"rollback-tx"}"#))
            .unwrap(),
    )
    .await;
    assert_eq!(rollback_begin.0, StatusCode::OK);
    let rollback_tx = rollback_begin.1["transactionId"].as_str().unwrap();
    let rollback_insert = admitted_fixture::request(
        &fixture.runtime,
        Request::post(format!("/api/v1/databases/{}/auth/insertUnique", fixture.database))
            .header("authorization", auth)
            .header("idempotency-key", "rollback-insert")
            .header("x-terrascale-auth-transaction", rollback_tx)
            .header("content-type", "application/json")
            .body(Body::from(r#"{"model":"user","record":{"id":"u2","name":"rolled-back"}}"#))
            .unwrap(),
    )
    .await;
    assert_eq!(rollback_insert.0, StatusCode::OK);
    let rollback = admitted_fixture::request(
        &fixture.runtime,
        Request::post(format!("/api/v1/databases/{}/auth/transactions/rollback", fixture.database))
            .header("authorization", auth)
            .header("idempotency-key", "rollback-end")
            .header("content-type", "application/json")
            .body(Body::from(format!(r#"{{"transactionId":"{rollback_tx}"}}"#)))
            .unwrap(),
    )
    .await;
    assert_eq!(rollback.0, StatusCode::OK);
    let rolled_back_read = admitted_fixture::request(
        &fixture.runtime,
        Request::get(format!("/api/v1/databases/{}/auth/records/user/u2", fixture.database))
            .header("authorization", auth)
            .body(Body::empty())
            .unwrap(),
    )
    .await;
    assert_eq!(rolled_back_read.0, StatusCode::OK);
    assert_eq!(rolled_back_read.1["matched"], false);
    // Better Auth 1.7.2 requires one owner per compound external identity.
    for (operation, key, body, expected) in [
        (
            "index",
            "account-identity-index",
            serde_json::json!({"model":"account","fields":["issuer","accountId"]}),
            StatusCode::OK,
        ),
        (
            "insertUnique",
            "external-first",
            serde_json::json!({"model":"account","record":{"id":"external-a","issuer":"issuer","accountId":"subject","userId":"u1"}}),
            StatusCode::OK,
        ),
        (
            "insertUnique",
            "external-duplicate",
            serde_json::json!({"model":"account","record":{"id":"external-b","issuer":"issuer","accountId":"subject","userId":"other-user"}}),
            StatusCode::CONFLICT,
        ),
    ] {
        let response = admitted_fixture::request(
            &fixture.runtime,
            Request::post(format!("/api/v1/databases/{}/auth/{operation}", fixture.database))
                .header("authorization", auth)
                .header("idempotency-key", key)
                .header("content-type", "application/json")
                .body(Body::from(body.to_string()))
                .unwrap(),
        )
        .await;
        assert_eq!(response.0, expected, "{operation}: {:?}", response.1);
        assert_eq!(response.2["cache-control"], "no-store");
    }
    let duplicate = admitted_fixture::request(
        &fixture.runtime,
        Request::get(format!(
            "/api/v1/databases/{}/auth/records/account/external-b",
            fixture.database
        ))
        .header("authorization", auth)
        .body(Body::empty())
        .unwrap(),
    )
    .await;
    assert_eq!(duplicate.0, StatusCode::OK);
    assert_eq!(duplicate.1["matched"], false);
    fixture.runtime.shutdown().await;
    fixture.local_receive.abort();
    fixture.remote_receive.abort();
    fixture.remote.shutdown().await.unwrap();
    let _ = std::fs::remove_dir_all(&fixture.root);
}

#[cfg(target_os = "linux")]
#[tokio::test(flavor = "multi_thread", worker_threads = 4)]
async fn admitted_quic_client_response_completes_after_runtime_dispatch() {
    use terrabase_member_transport::production::ProductionConnectionClass;

    let fixture = admitted_fixture::create().await;
    let domain = fixture.runtime.domains.read().unwrap().values().next().unwrap().clone();
    tokio::time::timeout(Duration::from_secs(10), async {
        while domain
            .authority
            .query(Action::Count { model: "user".into(), predicates: Vec::new() })
            .await
            .is_err()
        {
            tokio::time::sleep(Duration::from_millis(10)).await;
        }
    })
    .await
    .expect("initial quorum must be available before testing response completion");
    let group = fixture.runtime.domains.read().unwrap().keys().next().unwrap().clone();
    let command = Command {
        idempotency_key: "quic-client-complete".into(),
        observed_at_ms: 1,
        action: Action::InsertUnique {
            model: "user".into(),
            record: BTreeMap::from([
                ("id".into(), serde_json::Value::String("quic-client".into())),
                ("email".into(), serde_json::Value::String("quic-client@example.test".into())),
            ]),
        },
    };
    let frame = serde_json::to_vec(&serde_json::json!({
        "protocol": "terrabase.auth.raft.v1",
        "group": group,
        "sender": 2,
        "body": { "method": "clientWrite", "request": command },
    }))
    .unwrap();
    let member = fixture.local_endpoint.config().local_identity().member().clone();
    let (_permit, connection) = fixture
        .remote_endpoint
        .connect_request(
            &member,
            ProductionConnectionClass::AuthClient,
            frame.len(),
            Duration::from_secs(5),
        )
        .await
        .unwrap();
    connection.send_frame(&frame).await.unwrap();
    let response = connection.receive_frame().await.unwrap();
    let response: serde_json::Value = serde_json::from_slice(&response).unwrap();
    assert_eq!(response["error"], serde_json::Value::Null);
    assert_eq!(response["outcome"]["status"], "record");
    tokio::time::timeout(Duration::from_secs(2), connection.complete_request())
        .await
        .expect("the runtime must complete the one-request client connection")
        .unwrap();

    fixture.runtime.shutdown().await;
    fixture.local_receive.abort();
    fixture.remote_receive.abort();
    fixture.remote.shutdown().await.unwrap();
    drop(fixture.local_endpoint);
    drop(fixture.remote_endpoint);
    let _ = std::fs::remove_dir_all(fixture.root);
    drop(fixture.keys);
}

#[cfg(target_os = "linux")]
#[tokio::test(flavor = "multi_thread", worker_threads = 4)]
#[allow(clippy::too_many_lines)]
async fn lost_consume_response_replays_the_same_public_operation() {
    use terrabase_member_transport::production::ProductionConnectionClass;

    let fixture = admitted_fixture::create().await;
    let auth = format!("Bearer {}", fixture.key);
    let seed = admitted_fixture::request(
        &fixture.runtime,
        Request::post(format!("/api/v1/databases/{}/auth/insertUnique", fixture.database))
            .header("authorization", &auth)
            .header("idempotency-key", "seed-consume-token")
            .header("content-type", "application/json")
            .body(Body::from(r#"{"model":"token","record":{"id":"single-use","value":"opaque"}}"#))
            .unwrap(),
    )
    .await;
    assert_eq!(seed.0, StatusCode::OK, "seed response: {:?}", seed.1);

    let group = fixture.runtime.domains.read().unwrap().keys().next().unwrap().clone();
    let command = Command {
        idempotency_key: "consume-lost-response".into(),
        observed_at_ms: 1,
        action: Action::ConsumeMatching {
            model: "token".into(),
            predicates: vec![Predicate {
                field: "value".into(),
                value: serde_json::Value::String("opaque".into()),
                operator: Operator::Eq,
                connector: Connector::AND,
                mode: Mode::Sensitive,
            }],
        },
    };
    let frame = serde_json::to_vec(&serde_json::json!({
        "protocol": "terrabase.auth.raft.v1",
        "group": group,
        "sender": 2,
        "body": { "method": "clientWrite", "request": command },
    }))
    .unwrap();
    let member = fixture.local_endpoint.config().local_identity().member().clone();
    let (_permit, connection) = fixture
        .remote_endpoint
        .connect_request(
            &member,
            ProductionConnectionClass::AuthClient,
            frame.len(),
            Duration::from_secs(5),
        )
        .await
        .unwrap();
    connection.send_frame(&frame).await.unwrap();
    // Synchronize on the durable public state transition before closing the
    // client. This proves the request was accepted and applied, so the close
    // models loss of the response rather than a race with transport delivery.
    let domain = fixture.runtime.domains.read().unwrap().values().next().unwrap().clone();
    tokio::time::timeout(Duration::from_secs(5), async {
        loop {
            if domain
                .authority
                .find_one("token".into(), "single-use".into())
                .await
                .unwrap()
                .is_none()
            {
                break;
            }
            tokio::time::sleep(Duration::from_millis(20)).await;
        }
    })
    .await
    .expect("consume must be durably applied before simulating response loss");
    drop(connection);

    let mut replay = None;
    for _ in 0..20 {
        let response = admitted_fixture::request(
            &fixture.runtime,
            Request::post(format!("/api/v1/databases/{}/auth/consumeMatching", fixture.database))
                .header("authorization", &auth)
                .header("idempotency-key", "consume-lost-response")
                .header("content-type", "application/json")
                .body(Body::from(r#"{"model":"token","predicates":[{"field":"value","operator":"eq","value":"opaque"}]}"#))
                .unwrap(),
        )
        .await;
        if response.0 == StatusCode::OK {
            replay = Some(response);
            break;
        }
        tokio::time::sleep(Duration::from_millis(50)).await;
    }
    let replay = replay.expect("same idempotency key resolves the lost consume response");
    assert_eq!(replay.1["record"]["id"], "single-use");

    let second = admitted_fixture::request(
        &fixture.runtime,
        Request::post(format!("/api/v1/databases/{}/auth/consumeMatching", fixture.database))
            .header("authorization", &auth)
            .header("idempotency-key", "consume-second-attempt")
            .header("content-type", "application/json")
            .body(Body::from(r#"{"model":"token","predicates":[{"field":"value","operator":"eq","value":"opaque"}]}"#))
            .unwrap(),
    )
    .await;
    assert_eq!(second.0, StatusCode::OK, "second consume: {:?}", second.1);
    assert!(second.1["record"].is_null(), "token must be single-use: {:?}", second.1);

    fixture.runtime.shutdown().await;
    fixture.local_receive.abort();
    fixture.remote_receive.abort();
    fixture.remote.shutdown().await.unwrap();
    drop(fixture.local_endpoint);
    drop(fixture.remote_endpoint);
    let _ = std::fs::remove_dir_all(fixture.root);
    drop(fixture.keys);
}

#[cfg(target_os = "linux")]
#[tokio::test(flavor = "multi_thread", worker_threads = 4)]
async fn client_capacity_does_not_consume_consensus_capacity() {
    let fixture = admitted_fixture::create().await;
    let client = fixture.runtime.client_slots.clone().try_acquire_many_owned(8).unwrap();
    let group = fixture.runtime.domains.read().unwrap().keys().next().unwrap().clone();
    let frame = serde_json::to_vec(&serde_json::json!({
        "protocol": "terrabase.auth.raft.v1", "group": group, "sender": 2,
        "body": { "method": "vote", "request": {
            "vote": { "leader_id": { "term": 0, "node_id": 2 }, "committed": false },
            "last_log_id": null
        }}
    }))
    .unwrap();
    let member = fixture.local_endpoint.config().local_identity().member().clone();
    tokio::time::timeout(Duration::from_secs(5), async {
        let (_permit, connection) = fixture
            .remote_endpoint
            .connect_request(
                &member,
                ProductionConnectionClass::AuthConsensus,
                frame.len(),
                Duration::from_secs(5),
            )
            .await
            .unwrap();
        connection.send_frame(&frame).await.unwrap();
        let response = connection.receive_frame().await.unwrap();
        let response: serde_json::Value = serde_json::from_slice(&response).unwrap();
        assert!(response["Ok"]["vote_granted"].is_boolean(), "{response}");
        connection.complete_request().await.unwrap();
    })
    .await
    .expect("consensus dispatch must progress while client capacity is full");
    drop(client);
    fixture.runtime.shutdown().await;
    fixture.local_receive.abort();
    fixture.remote_receive.abort();
    fixture.remote.shutdown().await.unwrap();
    drop(fixture.local_endpoint);
    drop(fixture.remote_endpoint);
    let _ = std::fs::remove_dir_all(fixture.root);
    drop(fixture.keys);
}

#[cfg(target_os = "linux")]
#[tokio::test(flavor = "multi_thread", worker_threads = 4)]
async fn shutdown_rejects_new_dispatch_and_waits_tracked_work() {
    use crate::replication_peers::PeerControlDispatcher;
    use terrabase_member_transport::production::ProductionConnectionClass;

    let fixture = admitted_fixture::create().await;
    let (release, wait) = tokio::sync::oneshot::channel();
    fixture.runtime.client_work.lock().unwrap().spawn(async move {
        let _ = wait.await;
    });
    let runtime = Arc::clone(&fixture.runtime);
    let runtime_again = Arc::clone(&fixture.runtime);
    let shutdown = tokio::spawn(async move { runtime.shutdown().await });
    let shutdown_again = tokio::spawn(async move { runtime_again.shutdown().await });
    for _ in 0..100 {
        if !fixture.runtime.dispatching.load(Ordering::Acquire) {
            break;
        }
        tokio::time::sleep(Duration::from_millis(1)).await;
    }
    assert!(!fixture.runtime.dispatching.load(Ordering::Acquire));
    assert!(!shutdown.is_finished(), "shutdown must wait for tracked dispatch work");
    assert!(!shutdown_again.is_finished(), "concurrent shutdown must wait for the lifecycle lock");

    let group = fixture.runtime.domains.read().unwrap().keys().next().unwrap().clone();
    let frame = serde_json::to_vec(&serde_json::json!({
        "protocol": "terrabase.auth.raft.v1",
        "group": group,
        "sender": 2,
        "body": { "method": "clientQuery", "request": Action::Find {
            model: "user".into(), predicates: Vec::<Predicate>::new(), offset: 0, limit: 1,
            select: None, sort: None, joins: Vec::new()
        }},
    }))
    .unwrap();
    let member = fixture.local_endpoint.config().local_identity().member().clone();
    let (_permit, connection) = fixture
        .remote_endpoint
        .connect_request(
            &member,
            ProductionConnectionClass::AuthClient,
            frame.len(),
            Duration::from_secs(5),
        )
        .await
        .unwrap();
    let dispatched = fixture.runtime.dispatch(connection, frame).await;
    assert!(dispatched.is_err(), "shutdown must reject new dispatches");
    release.send(()).unwrap();
    shutdown.await.unwrap();
    shutdown_again.await.unwrap();
    assert!(fixture.runtime.reconcile().await.is_err());

    fixture.local_receive.abort();
    fixture.remote_receive.abort();
    fixture.remote.shutdown().await.unwrap();
    drop(fixture.local_endpoint);
    drop(fixture.remote_endpoint);
    let _ = std::fs::remove_dir_all(fixture.root);
    drop(fixture.keys);
}

#[cfg(target_os = "linux")]
#[tokio::test(flavor = "multi_thread", worker_threads = 4)]
#[ignore = "requires the separately built Node SDK consumer fixture"]
async fn admitted_http_router_serves_external_node_consumer() {
    let executable = std::env::var("TERRASCALE_AUTH_CONSUMER_EXECUTABLE")
        .expect("TERRASCALE_AUTH_CONSUMER_EXECUTABLE is required");
    let script = std::env::var("TERRASCALE_AUTH_CONSUMER_SCRIPT")
        .expect("TERRASCALE_AUTH_CONSUMER_SCRIPT is required");
    assert!(std::path::Path::new(&executable).is_absolute());
    assert!(std::path::Path::new(&script).is_absolute());

    let fixture = admitted_fixture::create_with_production_limits().await;
    let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
    let address = listener.local_addr().unwrap();
    let (stop_tx, stop_rx) = tokio::sync::oneshot::channel();
    let runtime = Arc::clone(&fixture.runtime);
    let mut server = tokio::spawn(async move {
        axum::serve(listener, router(runtime))
            .with_graceful_shutdown(async {
                let _ = stop_rx.await;
            })
            .await
            .unwrap();
    });

    let base_url = format!("http://{address}");
    let database = fixture.database.clone();
    let access_token = fixture.key.to_owned();
    let child = tokio::task::spawn_blocking(move || {
        use rustix::process::{Pid, Signal, kill_process_group};
        use std::os::unix::process::CommandExt;

        let mut process = ProcessCommand::new(executable);
        process.process_group(0);
        process
            .arg(script)
            .env("TERRASCALE_AUTH_TEST_BASE_URL", base_url)
            .env("TERRASCALE_AUTH_TEST_DATABASE", database)
            .env("TERRASCALE_AUTH_TEST_ACCESS_TOKEN", access_token)
            .stdin(Stdio::null())
            .stdout(Stdio::inherit())
            .stderr(Stdio::inherit());
        let mut child = process.spawn().map_err(|error| error.to_string())?;
        // The maintained suites perform thousands of encrypted quorum writes,
        // including schema provisioning and cleanup between cases.
        let deadline = Instant::now() + Duration::from_mins(15);
        loop {
            match child.try_wait() {
                Err(error) => {
                    let _ = kill_process_group(
                        Pid::from_raw(child.id().cast_signed()).unwrap(),
                        Signal::KILL,
                    );
                    let _ = child.kill();
                    let _ = child.wait();
                    break Err(format!("failed waiting for external auth consumer: {error}"));
                }
                Ok(Some(status)) => break Ok(status),
                Ok(None) if Instant::now() >= deadline => {
                    let _ = kill_process_group(
                        Pid::from_raw(child.id().cast_signed()).unwrap(),
                        Signal::KILL,
                    );
                    let _ = child.kill();
                    let _ = child.wait();
                    break Err("external auth consumer exceeded 15 minutes".into());
                }
                Ok(None) => std::thread::sleep(Duration::from_millis(100)),
            }
        }
    });
    let consumer_result = child.await;

    let _ = stop_tx.send(());
    let server_result =
        if let Ok(result) = tokio::time::timeout(Duration::from_secs(10), &mut server).await {
            result.map_err(|error| error.to_string())
        } else {
            server.abort();
            let _ = server.await;
            Err("HTTP router graceful shutdown exceeded 10 seconds".into())
        };
    fixture.runtime.shutdown().await;
    fixture.local_receive.abort();
    fixture.remote_receive.abort();
    let _ = fixture.local_receive.await;
    let _ = fixture.remote_receive.await;
    fixture.remote.shutdown().await.unwrap();
    drop(fixture.local_endpoint);
    drop(fixture.remote_endpoint);
    let _ = std::fs::remove_dir_all(fixture.root);
    drop(fixture.keys);

    assert!(server_result.is_ok(), "HTTP router task failed: {server_result:?}");
    let consumer_status = consumer_result
        .expect("external auth consumer task failed")
        .expect("external auth consumer process failed");
    assert!(consumer_status.success(), "external auth consumer exited with {consumer_status}");
}
