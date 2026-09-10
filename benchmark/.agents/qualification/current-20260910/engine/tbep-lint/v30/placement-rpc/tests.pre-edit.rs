use super::*;
use crate::placement::tests::Fixture;
use pb::placement_control_service_client::PlacementControlServiceClient;
use tonic::transport::{Certificate, ClientTlsConfig, Endpoint, Identity};

const PRINCIPAL: &str = "spiffe://terrascale.tech/production/orchestrator/gru-1";

fn tls_material() -> (String, String, String, String, String) {
    use rcgen::{
        BasicConstraints, CertificateParams, ExtendedKeyUsagePurpose, Ia5String, IsCa, KeyPair,
        SanType,
    };
    let ca_key = KeyPair::generate().unwrap();
    let mut params = CertificateParams::new(Vec::new()).unwrap();
    params.is_ca = IsCa::Ca(BasicConstraints::Unconstrained);
    let ca = params.self_signed(&ca_key).unwrap();
    let server_key = KeyPair::generate().unwrap();
    let mut params = CertificateParams::new(vec!["localhost".into(), "127.0.0.1".into()]).unwrap();
    params.extended_key_usages = vec![ExtendedKeyUsagePurpose::ServerAuth];
    let server = params.signed_by(&server_key, &ca, &ca_key).unwrap();
    let client_key = KeyPair::generate().unwrap();
    let mut params = CertificateParams::new(Vec::new()).unwrap();
    params.subject_alt_names = vec![SanType::URI(Ia5String::try_from(PRINCIPAL).unwrap())];
    params.extended_key_usages = vec![ExtendedKeyUsagePurpose::ClientAuth];
    let client = params.signed_by(&client_key, &ca, &ca_key).unwrap();
    (ca.pem(), server.pem(), server_key.serialize_pem(), client.pem(), client_key.serialize_pem())
}

fn request<T>(message: T, operation: Option<&str>) -> Request<T> {
    let mut request = Request::new(message);
    request.metadata_mut().insert(super::super::PRINCIPAL_METADATA, PRINCIPAL.parse().unwrap());
    if let Some(operation) = operation {
        request
            .metadata_mut()
            .insert(super::super::IDEMPOTENCY_METADATA, operation.parse().unwrap());
    }
    request
}

async fn assert_auth_bootstrap(fixture: &Fixture, channel: tonic::transport::Channel) {
    let mut auth_client =
        pb::auth_group_bootstrap_service_client::AuthGroupBootstrapServiceClient::new(channel);
    let identity = auth_client
        .identity(request(pb::AuthStorageIdentityRequest {}, None))
        .await
        .unwrap()
        .into_inner();
    let grant = crate::auth_bootstrap::tests::grant(fixture, &identity);
    let auth_receipt =
        auth_client.bootstrap(request(grant.clone(), None)).await.unwrap().into_inner();
    assert_eq!(auth_receipt.grant, grant.grant);
    assert_eq!(auth_receipt.status, pb::AuthGroupBootstrapStatus::Persisted as i32);
    assert_eq!(
        auth_client.bootstrap(request(grant.clone(), None)).await.unwrap().into_inner(),
        auth_receipt
    );
    assert_eq!(
        auth_client.bootstrap(Request::new(grant)).await.unwrap_err().code(),
        Code::Unauthenticated
    );
}

#[tokio::test]
async fn real_mtls_stage_get_retry_and_scope_isolation() {
    use base64::Engine as _;
    use prost::Message as _;
    use ring::signature::KeyPair as _;
    let fixture = Fixture::new();
    let journal = Arc::new(fixture.open());
    let proposal = fixture.request.proposal.as_ref().unwrap();
    let identity = terrabase_cluster::production::MemberWorkloadIdentity::try_new(
        &proposal.environment,
        &proposal.cluster,
        &proposal.assignments[0].writer_member,
    )
    .unwrap()
    .as_str()
    .to_owned();
    let signing_der =
        ring::signature::Ed25519KeyPair::generate_pkcs8(&ring::rand::SystemRandom::new()).unwrap();
    let signing_key = ring::signature::Ed25519KeyPair::from_pkcs8(signing_der.as_ref()).unwrap();
    let signer = crate::placement::transition::TransitionReceiptSigner::from_pkcs8(
        identity.clone(),
        signing_der.as_ref(),
    )
    .unwrap();
    let config = TbepGrpcConfig::try_new(
        256 * 1024,
        8,
        Duration::from_secs(2),
        Duration::from_secs(2),
        Duration::from_secs(2),
        [PRINCIPAL.to_owned()].into(),
    )
    .unwrap();
    let server = Arc::new(
        PlacementControlServer::new(
            journal,
            Arc::new(fixture.open_auth()),
            config,
            BTreeSet::new(),
        )
        .unwrap()
        .with_join_signer(Arc::new(signer)),
    );
    let (ca, certificate, key, client_certificate, client_key) = tls_material();
    let listener =
        server.bind_tls("127.0.0.1:0".parse().unwrap(), &certificate, &key, &ca).await.unwrap();
    let address = listener.local_addr().unwrap();
    let (stop, signal) = tokio::sync::oneshot::channel::<()>();
    let serving = tokio::spawn(listener.serve(async {
        let _ = signal.await;
    }));
    let tls = ClientTlsConfig::new()
        .ca_certificate(Certificate::from_pem(&ca))
        .domain_name("localhost")
        .identity(Identity::from_pem(&client_certificate, &client_key));
    let endpoint =
        Endpoint::from_shared(format!("https://{address}")).unwrap().tls_config(tls).unwrap();
    let channel = endpoint.connect().await.unwrap();
    let mut client = PlacementControlServiceClient::new(channel.clone());
    assert_auth_bootstrap(&fixture, channel.clone()).await;

    assert_eq!(
        client.get(request(pb::PlacementGetRequest {}, None)).await.unwrap_err().code(),
        Code::NotFound
    );
    let response = client
        .stage(request(fixture.request.clone(), Some("operation-one")))
        .await
        .unwrap()
        .into_inner();
    assert_eq!(response.status, pb::PlacementProposalStatus::Pending as i32);
    assert_eq!(response.revision, 1);
    let signed: serde_json::Value = serde_json::from_slice(&response.join_challenge).unwrap();
    assert_eq!(signed["key_id"], identity);
    let payload = base64::engine::general_purpose::STANDARD
        .decode(signed["payload"].as_str().unwrap())
        .unwrap();
    let signature = base64::engine::general_purpose::STANDARD
        .decode(signed["signature"].as_str().unwrap())
        .unwrap();
    let mut input = b"terrabase-placement-transition-v1\0".to_vec();
    input.extend_from_slice(&payload);
    ring::signature::UnparsedPublicKey::new(
        &ring::signature::ED25519,
        signing_key.public_key().as_ref(),
    )
    .verify(&input, &signature)
    .unwrap();
    let challenge: serde_json::Value = serde_json::from_slice(&payload).unwrap();
    assert_eq!(challenge["kind"], "placement_join_challenge");
    assert_eq!(challenge["member_identity"], identity);
    assert_eq!(challenge["proposal_revision"], 1);
    assert_eq!(challenge["bootstrap_nonce"].as_str().unwrap().len(), 64);
    let digest = ring::digest::digest(
        &ring::digest::SHA256,
        &response.proposal.as_ref().unwrap().encode_to_vec(),
    )
    .as_ref()
    .iter()
    .map(|byte| format!("{byte:02x}"))
    .collect::<String>();
    assert_eq!(challenge["stage_proposal_digest"], digest);
    let fetched = client.get(request(pb::PlacementGetRequest {}, None)).await.unwrap().into_inner();
    assert_eq!(fetched.join_challenge, response.join_challenge);
    assert_eq!(fetched.revision, response.revision);
    assert_eq!(fetched.proposal, response.proposal);
    assert_eq!(fetched.status, response.status);
    assert_eq!(
        client
            .stage(request(fixture.request.clone(), Some("operation-one")))
            .await
            .unwrap()
            .into_inner(),
        response
    );

    let mut forbidden = request(fixture.request.clone(), Some("other"));
    forbidden.metadata_mut().insert(super::super::DATABASE_METADATA, "arbitrary".parse().unwrap());
    assert_eq!(client.stage(forbidden).await.unwrap_err().code(), Code::InvalidArgument);
    let mut forged = request(fixture.request.clone(), Some("other"));
    forged.metadata_mut().insert(
        super::super::PRINCIPAL_METADATA,
        "spiffe://terrascale.tech/production/orchestrator/other".parse().unwrap(),
    );
    assert_eq!(client.stage(forged).await.unwrap_err().code(), Code::PermissionDenied);
    assert_eq!(
        client.stage(Request::new(fixture.request.clone())).await.unwrap_err().code(),
        Code::Unauthenticated
    );
    let mut data = pb::maintenance_service_client::MaintenanceServiceClient::new(channel);
    assert_eq!(
        data.admin_status(request(pb::MaintenanceServiceAdminStatusRequest {}, None))
            .await
            .unwrap_err()
            .code(),
        Code::Unauthenticated
    );
    server.begin_drain();
    assert_eq!(
        client.get(request(pb::PlacementGetRequest {}, None)).await.unwrap_err().code(),
        Code::Unavailable
    );
    let _ = stop.send(());
    serving.await.unwrap().unwrap();
    PlacementControlServer::drain(&server).await.unwrap();
}

#[tokio::test]
async fn metadata_without_peer_certificate_cannot_reach_journal() {
    let fixture = Fixture::new();
    let journal = Arc::new(fixture.open());
    let config = TbepGrpcConfig::try_new(
        256 * 1024,
        1,
        Duration::from_secs(2),
        Duration::from_secs(2),
        Duration::from_secs(2),
        [PRINCIPAL.to_owned()].into(),
    )
    .unwrap();
    let server = Arc::new(
        PlacementControlServer::new(
            Arc::clone(&journal),
            Arc::new(fixture.open_auth()),
            config,
            BTreeSet::new(),
        )
        .unwrap(),
    );
    let error = PlacementControlService::stage(
        &server,
        request(fixture.request.clone(), Some("operation")),
    )
    .await
    .unwrap_err();
    assert_eq!(error.code(), Code::Unauthenticated);
    assert_eq!(journal.latest(), Err(PlacementError::NotFound));
}

#[tokio::test]
async fn admitted_listener_retains_authenticated_placement_control() {
    let fixture = Fixture::new();
    let config = TbepGrpcConfig::try_new(
        256 * 1024,
        8,
        Duration::from_secs(2),
        Duration::from_secs(2),
        Duration::from_secs(2),
        [PRINCIPAL.to_owned()].into(),
    )
    .unwrap();
    let placement = Arc::new(
        PlacementControlServer::new(
            Arc::new(fixture.open()),
            Arc::new(fixture.open_auth()),
            config.clone(),
            BTreeSet::new(),
        )
        .unwrap(),
    );
    let host = crate::engine_host::EngineHost::open(
        &fixture.root.join("host"),
        1,
        "tenant-a",
        "project-a",
        "database-a",
    )
    .unwrap();
    let server = Arc::new(
        super::super::TbepGrpcServer::with_config_and_metrics(
            Arc::new(host),
            pb::BindingKind::NetworkMtls,
            config,
            terrabase::observability::Region::Unknown,
            Arc::new(terrabase::observability::MetricsRegistry::new()),
        )
        .with_placement_control(Arc::clone(&placement)),
    );
    let (ca, certificate, key, client_certificate, client_key) = tls_material();
    let mut listener =
        server.serve_tls("127.0.0.1:0".parse().unwrap(), &certificate, &key, Some(&ca)).unwrap();
    let tls = ClientTlsConfig::new()
        .ca_certificate(Certificate::from_pem(&ca))
        .domain_name("localhost")
        .identity(Identity::from_pem(&client_certificate, &client_key));
    let channel = Endpoint::from_shared(format!("https://{}", listener.local_addr().unwrap()))
        .unwrap()
        .tls_config(tls)
        .unwrap()
        .connect()
        .await
        .unwrap();
    assert_auth_bootstrap(&fixture, channel.clone()).await;
    let mut client = PlacementControlServiceClient::new(channel);
    let staged = client
        .stage(request(fixture.request.clone(), Some("admitted-stage")))
        .await
        .unwrap()
        .into_inner();
    assert_eq!(staged.revision, 1);
    assert_eq!(
        client.get(request(pb::PlacementGetRequest {}, None)).await.unwrap().into_inner(),
        staged
    );
    assert_eq!(
        client.get(Request::new(pb::PlacementGetRequest {})).await.unwrap_err().code(),
        Code::Unauthenticated
    );
    assert_eq!(
        client
            .readiness(request(
                pb::PlacementReadinessRequest { nonce: "ab".repeat(32), ..Default::default() },
                None,
            ))
            .await
            .unwrap_err()
            .code(),
        Code::FailedPrecondition
    );
    placement.begin_drain();
    assert_eq!(
        client.get(request(pb::PlacementGetRequest {}, None)).await.unwrap_err().code(),
        Code::Unavailable
    );
    drop(client);
    tokio::task::spawn_blocking(move || listener.shutdown_and_wait()).await.unwrap().unwrap();
    PlacementControlServer::drain(&placement).await.unwrap();
}
