//! The gRPC serving contract rows (SE-14, ADR 0028): the tonic client
//! against the tonic server over a local Unix-domain socket.
//!
//! Coverage follows the retired UDS conformance family's core rows:
//! the identity discipline (unbound channels fail closed), the
//! idempotency pairing of mutations, the backbone stream lifecycle,
//! fail-closed bounds, and the supervision shutdown — plus the
//! `TbepError` status-detail contract every client maps codes through.

use std::path::PathBuf;
use std::sync::Arc;
use std::sync::Mutex;

use prost::Message as _;
use terrabase_tbep::TbepHost;
use terrabase_tbep::grpc::TbepGrpcServer;
use terrabase_tbep::host::HostingError;
use terrabase_tbep::host::TbepNotification;
use terrabase_tbep::proto::terrabase::v1 as pb;
use terrabase_tbep::proto::terrabase::v1::backbone_service_client::BackboneServiceClient;
use terrabase_tbep::proto::terrabase::v1::channel_service_client::ChannelServiceClient;
use terrabase_tbep::proto::terrabase::v1::maintenance_service_client::MaintenanceServiceClient;
use terrabase_tbep::proto::terrabase::v1::schema_service_client::SchemaServiceClient;
use terrabase_tbep::registry;
use tonic::Request;
use tonic::transport::Endpoint;

/// A minimal fixed-answer host: one subscription feed.
struct FixedHost {
    feed: Mutex<Option<std::sync::mpsc::Sender<TbepNotification>>>,
}

impl TbepHost for FixedHost {
    fn bind_channel(
        &self,
        request: &registry::ChannelBindRequest,
    ) -> Result<registry::ChannelBindResponse, HostingError> {
        Ok(registry::ChannelBindResponse { scope: request.scope.clone(), serving_instance: 7 })
    }

    fn backbone_subscribe(
        &self,
        _scope: &registry::BoundScope,
        request: &registry::BackboneSubscribeRequest,
    ) -> Result<
        (registry::BackboneSubscribeResponse, std::sync::mpsc::Receiver<TbepNotification>),
        HostingError,
    > {
        let (sender, receiver) = std::sync::mpsc::channel();
        *self.feed.lock().expect("the fixture lock is sound") = Some(sender);
        Ok((
            registry::BackboneSubscribeResponse {
                subscription_id: request.subscription_id,
                start_sequence: 5,
            },
            receiver,
        ))
    }

    fn backbone_acknowledge(
        &self,
        _scope: &registry::BoundScope,
        request: &registry::BackboneAcknowledgeRequest,
    ) -> Result<registry::BackboneAcknowledgeResponse, HostingError> {
        Ok(registry::BackboneAcknowledgeResponse {
            subscription_id: request.subscription_id,
            acknowledged_through: request.through_sequence,
        })
    }

    fn backbone_unsubscribe(
        &self,
        _scope: &registry::BoundScope,
        request: &registry::BackboneUnsubscribeRequest,
    ) -> Result<registry::BackboneUnsubscribeResponse, HostingError> {
        Ok(registry::BackboneUnsubscribeResponse { subscription_id: request.subscription_id })
    }

    fn topology_read(
        &self,
        _scope: &registry::BoundScope,
    ) -> Result<registry::TopologyReadResponse, HostingError> {
        Ok(registry::TopologyReadResponse {
            binding: registry::BindingKind::LocalUds,
            subsystems: vec!["engine".into()],
            engine_topology_available: false,
            cluster_mode: None,
        })
    }

    fn admin_status(&self) -> Result<registry::AdminStatusResponse, HostingError> {
        Ok(registry::AdminStatusResponse {
            registry_version: registry::REGISTRY_VERSION.to_string(),
            frames_served: 0,
            sessions: 0,
            subscriptions: 0,
            scopes: 0,
        })
    }

    fn health(&self) -> Result<registry::SupervisionHealthResponse, HostingError> {
        Ok(registry::SupervisionHealthResponse {
            state: registry::HealthState::Serving,
            sessions: 0,
            subscriptions: 1,
            frames_served: 1,
        })
    }

    fn begin_shutdown(
        &self,
        _request: &registry::SupervisionShutdownRequest,
    ) -> Result<registry::SupervisionShutdownResponse, HostingError> {
        Ok(registry::SupervisionShutdownResponse {
            state: registry::HealthState::Draining,
            in_flight: 2,
        })
    }
}

/// Decodes the `TbepError` code out of a status's detail envelope.
fn tbep_code(status: &tonic::Status) -> Option<pb::ErrorCode> {
    let rpc_status = pb::RpcStatus::decode(status.details()).ok()?;
    let any = rpc_status.details.first()?;
    if any.type_url != "type.googleapis.com/terrabase.v1.TbepError" {
        return None;
    }
    let error = pb::TbepError::decode(any.value.as_slice()).ok()?;
    pb::ErrorCode::try_from(error.code).ok()
}

async fn connect(socket: PathBuf) -> tonic::transport::Channel {
    async fn bridge(
        socket: PathBuf,
    ) -> std::io::Result<hyper_util::rt::TokioIo<tokio::net::UnixStream>> {
        let stream = tokio::net::UnixStream::connect(socket).await?;
        Ok(hyper_util::rt::TokioIo::new(stream))
    }
    Endpoint::from_shared("http://localhost".to_string())
        .expect("the endpoint parses")
        .connect_with_connector(tower::service_fn(move |_uri| bridge(socket.clone())))
        .await
        .expect("the client connects over the local socket")
}

fn identified<T>(message: T) -> Request<T> {
    let mut request = Request::new(message);
    request.metadata_mut().insert("x-tb-principal", "operator".parse().unwrap());
    request.metadata_mut().insert("x-tb-tenant", "tenant-a".parse().unwrap());
    request.metadata_mut().insert("x-tb-project", "project-a".parse().unwrap());
    request.metadata_mut().insert("x-tb-database", "database-a".parse().unwrap());
    request
}

fn mutation<T>(message: T) -> Request<T> {
    let mut request = identified(message);
    request.metadata_mut().insert("x-tb-idempotency-key", "op-1".parse().unwrap());
    request
}

fn socket_path(name: &str) -> PathBuf {
    std::env::temp_dir().join(format!("tbep-grpc-{name}-{}.sock", std::process::id()))
}

#[tokio::test]
async fn unbound_calls_fail_closed_with_the_channel_code() {
    let socket = socket_path("unbound");
    let server = Arc::new(TbepGrpcServer::new(
        Arc::new(FixedHost { feed: Mutex::new(None) }) as Arc<dyn TbepHost>,
        pb::BindingKind::Local,
    ));
    let mut listener = server.serve_uds(&socket).expect("the server binds");
    let channel = connect(socket.clone()).await;
    let mut maintenance = MaintenanceServiceClient::new(channel);

    let status = maintenance
        .topology_read(Request::new(pb::MaintenanceServiceTopologyReadRequest {}))
        .await
        .expect_err("an unbound call fails");
    assert_eq!(status.code(), tonic::Code::Unauthenticated);
    assert_eq!(tbep_code(&status), Some(pb::ErrorCode::UnboundChannel));
    listener.shutdown();
}

#[tokio::test]
async fn mutations_require_their_idempotency_key() {
    let socket = socket_path("idempotency");
    let server = Arc::new(TbepGrpcServer::new(
        Arc::new(FixedHost { feed: Mutex::new(None) }) as Arc<dyn TbepHost>,
        pb::BindingKind::Local,
    ));
    let mut listener = server.serve_uds(&socket).expect("the server binds");
    let channel = connect(socket.clone()).await;
    let mut schema = SchemaServiceClient::new(channel);

    let status = schema
        .declare(identified(pb::SchemaServiceDeclareRequest {
            collection: "orders".into(),
            schema: None,
            operation_id: "op-1".into(),
        }))
        .await
        .expect_err("a keyless mutation fails");
    assert_eq!(status.code(), tonic::Code::InvalidArgument);
    assert_eq!(tbep_code(&status), Some(pb::ErrorCode::IdempotencyPairing));

    let mut duplicate = identified(pb::SchemaServiceDeclareRequest {
        collection: "orders".into(),
        schema: None,
        operation_id: "op-1".into(),
    });
    duplicate.metadata_mut().append("x-tb-idempotency-key", "op-1".parse().unwrap());
    duplicate.metadata_mut().append("x-tb-idempotency-key", "op-2".parse().unwrap());
    let status = schema.declare(duplicate).await.expect_err("duplicate keys fail closed");
    assert_eq!(status.code(), tonic::Code::InvalidArgument);
    assert_eq!(tbep_code(&status), Some(pb::ErrorCode::IdempotencyPairing));
    listener.shutdown();
}

#[tokio::test]
async fn bind_answers_the_serving_instance() {
    let socket = socket_path("bind");
    let server = Arc::new(TbepGrpcServer::new(
        Arc::new(FixedHost { feed: Mutex::new(None) }) as Arc<dyn TbepHost>,
        pb::BindingKind::Local,
    ));
    let mut listener = server.serve_uds(&socket).expect("the server binds");
    let channel = connect(socket.clone()).await;

    let mut channels = ChannelServiceClient::new(channel);
    let bound = channels
        .bind(Request::new(pb::ChannelServiceBindRequest {
            principal: "operator".into(),
            tenant: "tenant-a".into(),
            project: "project-a".into(),
            database: "database-a".into(),
        }))
        .await
        .expect("the bind answers");
    assert_eq!(bound.into_inner().serving_instance, 7);
    listener.shutdown();
}

#[tokio::test]
async fn bounds_fail_closed_before_the_host_runs() {
    let socket = socket_path("bounds");
    let server = Arc::new(TbepGrpcServer::new(
        Arc::new(FixedHost { feed: Mutex::new(None) }) as Arc<dyn TbepHost>,
        pb::BindingKind::Local,
    ));
    let mut listener = server.serve_uds(&socket).expect("the server binds");
    let channel = connect(socket.clone()).await;
    let mut schema = SchemaServiceClient::new(channel);

    let status = schema
        .declare(mutation(pb::SchemaServiceDeclareRequest {
            collection: "c".repeat(65),
            schema: None,
            operation_id: "op-1".into(),
        }))
        .await
        .expect_err("an over-bound collection fails");
    assert_eq!(status.code(), tonic::Code::InvalidArgument);
    assert_eq!(tbep_code(&status), Some(pb::ErrorCode::InvalidRequest));
    listener.shutdown();
}

#[tokio::test]
async fn identity_metadata_bounds_fail_closed() {
    let socket = socket_path("identity-bounds");
    let server = Arc::new(TbepGrpcServer::new(
        Arc::new(FixedHost { feed: Mutex::new(None) }) as Arc<dyn TbepHost>,
        pb::BindingKind::Local,
    ));
    let mut listener = server.serve_uds(&socket).expect("the server binds");
    let channel = connect(socket.clone()).await;
    let mut maintenance = MaintenanceServiceClient::new(channel);
    let mut request = Request::new(pb::MaintenanceServiceTopologyReadRequest {});
    request.metadata_mut().insert("x-tb-principal", "p".repeat(129).parse().unwrap());
    request.metadata_mut().insert("x-tb-tenant", "tenant-a".parse().unwrap());
    let status =
        maintenance.topology_read(request).await.expect_err("an oversized principal fails");
    assert_eq!(status.code(), tonic::Code::Unauthenticated);
    assert_eq!(tbep_code(&status), Some(pb::ErrorCode::UnboundChannel));

    let mut duplicate = Request::new(pb::MaintenanceServiceTopologyReadRequest {});
    duplicate.metadata_mut().insert("x-tb-principal", "operator".parse().unwrap());
    duplicate.metadata_mut().insert("x-tb-tenant", "tenant-a".parse().unwrap());
    duplicate.metadata_mut().append("x-tb-tenant", "tenant-b".parse().unwrap());
    let status = maintenance.topology_read(duplicate).await.expect_err("duplicate identity fails");
    assert_eq!(status.code(), tonic::Code::Unauthenticated);
    assert_eq!(tbep_code(&status), Some(pb::ErrorCode::UnboundChannel));
    listener.shutdown();
}

#[tokio::test]
async fn hostile_values_fail_closed_before_the_host_runs() {
    let socket = socket_path("hostile-values");
    let server = Arc::new(TbepGrpcServer::new(
        Arc::new(FixedHost { feed: Mutex::new(None) }) as Arc<dyn TbepHost>,
        pb::BindingKind::Local,
    ));
    let mut listener = server.serve_uds(&socket).expect("the server binds");
    let channel = connect(socket.clone()).await;
    let mut schema = SchemaServiceClient::new(channel);
    let mut list = Vec::with_capacity(4097);
    for _ in 0..=4096 {
        list.push(pb::Value { kind: Some(pb::value::Kind::Null(pb::NullValue::Null as i32)) });
    }
    let status = schema
        .declare(mutation(pb::SchemaServiceDeclareRequest {
            collection: "orders".into(),
            schema: Some(pb::Value {
                kind: Some(pb::value::Kind::List(pb::ValueList { items: list })),
            }),
            operation_id: "op-1".into(),
        }))
        .await
        .expect_err("an oversized value list fails");
    assert_eq!(status.code(), tonic::Code::InvalidArgument);
    assert_eq!(tbep_code(&status), Some(pb::ErrorCode::InvalidRequest));

    let status = schema
        .declare(mutation(pb::SchemaServiceDeclareRequest {
            collection: "orders".into(),
            schema: Some(pb::Value { kind: Some(pb::value::Kind::Number(f64::NAN)) }),
            operation_id: "op-1".into(),
        }))
        .await
        .expect_err("a non-finite value fails");
    assert_eq!(status.code(), tonic::Code::InvalidArgument);
    assert_eq!(tbep_code(&status), Some(pb::ErrorCode::InvalidRequest));
    listener.shutdown();
}

#[test]
fn uds_does_not_unlink_an_existing_non_socket() {
    let path = socket_path("existing-file");
    std::fs::write(&path, b"operator data").expect("the fixture file writes");
    let server = Arc::new(TbepGrpcServer::new(
        Arc::new(FixedHost { feed: Mutex::new(None) }) as Arc<dyn TbepHost>,
        pb::BindingKind::Local,
    ));
    let Err(error) = server.serve_uds(&path) else {
        panic!("a regular path is refused");
    };
    assert_eq!(error.kind(), std::io::ErrorKind::AlreadyExists);
    assert_eq!(std::fs::read(&path).expect("the fixture remains"), b"operator data");
    std::fs::remove_file(path).expect("the fixture removes");
}

#[cfg(unix)]
#[test]
fn uds_does_not_unlink_an_existing_socket() {
    let path = socket_path("existing-socket");
    let owner = std::os::unix::net::UnixListener::bind(&path).expect("the owner binds");
    let server = Arc::new(TbepGrpcServer::new(
        Arc::new(FixedHost { feed: Mutex::new(None) }) as Arc<dyn TbepHost>,
        pb::BindingKind::Local,
    ));
    let Err(error) = server.serve_uds(&path) else {
        panic!("an existing socket path is refused");
    };
    assert_eq!(error.kind(), std::io::ErrorKind::AlreadyExists);
    assert!(std::os::unix::net::UnixStream::connect(&path).is_ok());
    drop(owner);
    std::fs::remove_file(path).expect("the fixture removes");
}

#[test]
fn tls_requires_a_client_ca_for_production() {
    let config = terrabase_tbep::grpc::TbepGrpcConfig::try_new(
        registry::MAX_FRAME_BYTES,
        terrabase_tbep::grpc::MAX_INFLIGHT_OPERATIONS,
        std::time::Duration::from_secs(30),
        std::time::Duration::from_secs(5),
        std::time::Duration::from_secs(5),
        ["spiffe://terrascale.tech/test/gateway/01ARZ3NDEKTSV4RRFFQ69G5FAV".to_owned()].into(),
    )
    .expect("the test gateway policy is valid");
    let server = Arc::new(TbepGrpcServer::with_config_and_metrics(
        Arc::new(FixedHost { feed: Mutex::new(None) }) as Arc<dyn TbepHost>,
        pb::BindingKind::NetworkMtls,
        config,
        terrabase::observability::Region::Unknown,
        Arc::new(terrabase::observability::MetricsRegistry::new()),
    ));
    let Err(error) = server.serve_tls(
        std::net::SocketAddr::from(([127, 0, 0, 1], 0)),
        "server-certificate",
        "server-key",
        None,
    ) else {
        panic!("TCP TLS without a client CA must fail closed");
    };
    assert_eq!(error.kind(), std::io::ErrorKind::InvalidInput);

    let Err(error) = server.serve_tls(
        std::net::SocketAddr::from(([127, 0, 0, 1], 0)),
        "server-certificate",
        "server-key",
        Some("  \n"),
    ) else {
        panic!("an empty client CA must fail closed");
    };
    assert_eq!(error.kind(), std::io::ErrorKind::InvalidInput);
}

#[tokio::test]
#[allow(
    clippy::too_many_lines,
    reason = "one real listener owns the complete positive and negative mTLS identity matrix"
)]
async fn tls_accepts_valid_mtls_material() {
    use rcgen::{
        BasicConstraints, CertificateParams, ExtendedKeyUsagePurpose, Ia5String, IsCa, KeyPair,
        SanType,
    };
    use tonic::transport::{Certificate, ClientTlsConfig, Identity};

    const GATEWAY_PRINCIPAL: &str =
        "spiffe://terrascale.tech/test/gateway/01ARZ3NDEKTSV4RRFFQ69G5FAV";
    const SECOND_ADMITTED_PRINCIPAL: &str =
        "spiffe://terrascale.tech/test/gateway/01ARZ3NDEKTSV4RRFFQ69G5FAW";
    const UNADMITTED_PRINCIPAL: &str =
        "spiffe://terrascale.tech/test/gateway/01ARZ3NDEKTSV4RRFFQ69G5FAX";

    let issuer_key = KeyPair::generate().expect("the test CA key generates");
    let mut issuer_params = CertificateParams::new(Vec::new()).expect("the CA parameters parse");
    issuer_params.is_ca = IsCa::Ca(BasicConstraints::Unconstrained);
    let issuer = issuer_params.self_signed(&issuer_key).expect("the test CA signs");

    let server_key = KeyPair::generate().expect("the server key generates");
    let server_params =
        CertificateParams::new(vec!["localhost".into()]).expect("the server parameters parse");
    let server_certificate = server_params
        .signed_by(&server_key, &issuer, &issuer_key)
        .expect("the CA signs the server certificate");

    let client_key = KeyPair::generate().expect("the gateway key generates");
    let mut client_params =
        CertificateParams::new(Vec::new()).expect("the gateway parameters parse");
    client_params.subject_alt_names = vec![SanType::URI(
        Ia5String::try_from(GATEWAY_PRINCIPAL).expect("the gateway SPIFFE ID is IA5 text"),
    )];
    client_params.extended_key_usages = vec![ExtendedKeyUsagePurpose::ClientAuth];
    let client_certificate = client_params
        .signed_by(&client_key, &issuer, &issuer_key)
        .expect("the CA signs the gateway certificate");

    let config = terrabase_tbep::grpc::TbepGrpcConfig::try_new(
        registry::MAX_FRAME_BYTES,
        terrabase_tbep::grpc::MAX_INFLIGHT_OPERATIONS,
        std::time::Duration::from_secs(30),
        std::time::Duration::from_secs(5),
        std::time::Duration::from_secs(5),
        [GATEWAY_PRINCIPAL.to_owned(), SECOND_ADMITTED_PRINCIPAL.to_owned()].into(),
    )
    .expect("the test gateway policy is valid");
    let server = Arc::new(TbepGrpcServer::with_config_and_metrics(
        Arc::new(FixedHost { feed: Mutex::new(None) }) as Arc<dyn TbepHost>,
        pb::BindingKind::NetworkMtls,
        config,
        terrabase::observability::Region::Unknown,
        Arc::new(terrabase::observability::MetricsRegistry::new()),
    ));
    let mut listener = server
        .serve_tls(
            std::net::SocketAddr::from(([127, 0, 0, 1], 0)),
            &server_certificate.pem(),
            &server_key.serialize_pem(),
            Some(&issuer.pem()),
        )
        .expect("valid mutual-TLS material binds the production listener");
    let address = listener.local_addr().expect("the TLS listener has an address");

    let no_certificate_channel =
        Endpoint::from_shared(format!("https://localhost:{}", address.port()))
            .expect("the TLS endpoint parses")
            .tls_config(
                ClientTlsConfig::new()
                    .domain_name("localhost")
                    .ca_certificate(Certificate::from_pem(issuer.pem())),
            )
            .expect("the client policy without an identity parses")
            .connect()
            .await
            .expect("the lazy client channel is constructed");
    ChannelServiceClient::new(no_certificate_channel)
        .bind(Request::new(pb::ChannelServiceBindRequest {
            principal: GATEWAY_PRINCIPAL.to_owned(),
            tenant: "tenant-a".into(),
            project: "project-a".into(),
            database: "database-a".into(),
        }))
        .await
        .expect_err("the production listener refuses a client without a certificate");

    let wrong_issuer_key = KeyPair::generate().expect("the wrong CA key generates");
    let mut wrong_issuer_params =
        CertificateParams::new(Vec::new()).expect("the wrong CA parameters parse");
    wrong_issuer_params.is_ca = IsCa::Ca(BasicConstraints::Unconstrained);
    let wrong_issuer =
        wrong_issuer_params.self_signed(&wrong_issuer_key).expect("the wrong CA signs itself");
    let wrong_client_key = KeyPair::generate().expect("the wrong client key generates");
    let mut wrong_client_params =
        CertificateParams::new(Vec::new()).expect("the wrong client parameters parse");
    wrong_client_params.subject_alt_names = vec![SanType::URI(
        Ia5String::try_from(GATEWAY_PRINCIPAL).expect("the gateway SPIFFE ID is IA5 text"),
    )];
    wrong_client_params.extended_key_usages = vec![ExtendedKeyUsagePurpose::ClientAuth];
    let wrong_client_certificate = wrong_client_params
        .signed_by(&wrong_client_key, &wrong_issuer, &wrong_issuer_key)
        .expect("the wrong CA signs the client certificate");
    let wrong_ca_channel = Endpoint::from_shared(format!("https://localhost:{}", address.port()))
        .expect("the TLS endpoint parses")
        .tls_config(
            ClientTlsConfig::new()
                .domain_name("localhost")
                .ca_certificate(Certificate::from_pem(issuer.pem()))
                .identity(Identity::from_pem(
                    format!("{}{}", wrong_client_certificate.pem(), wrong_issuer.pem()),
                    wrong_client_key.serialize_pem(),
                )),
        )
        .expect("the wrong-CA client policy parses")
        .connect()
        .await
        .expect("the lazy wrong-CA channel is constructed");
    ChannelServiceClient::new(wrong_ca_channel)
        .bind(Request::new(pb::ChannelServiceBindRequest {
            principal: GATEWAY_PRINCIPAL.to_owned(),
            tenant: "tenant-a".into(),
            project: "project-a".into(),
            database: "database-a".into(),
        }))
        .await
        .expect_err("the production listener refuses a client signed by the wrong CA");

    let client_identity = Identity::from_pem(
        format!("{}{}", client_certificate.pem(), issuer.pem()),
        client_key.serialize_pem(),
    );
    let channel = Endpoint::from_shared(format!("https://localhost:{}", address.port()))
        .expect("the TLS endpoint parses")
        .tls_config(
            ClientTlsConfig::new()
                .domain_name("localhost")
                .ca_certificate(Certificate::from_pem(issuer.pem()))
                .identity(client_identity),
        )
        .expect("the mTLS client policy is valid")
        .connect()
        .await
        .expect("the gateway completes a mutual-TLS handshake");
    let mut channels = ChannelServiceClient::new(channel.clone());
    let bound = channels
        .bind(Request::new(pb::ChannelServiceBindRequest {
            principal: GATEWAY_PRINCIPAL.to_owned(),
            tenant: "tenant-a".into(),
            project: "project-a".into(),
            database: "database-a".into(),
        }))
        .await
        .expect("the certificate-bound gateway RPC succeeds")
        .into_inner();
    assert_eq!(bound.serving_instance, 7);

    for (principal, expectation) in [
        (SECOND_ADMITTED_PRINCIPAL, "a certificate/principal mismatch is refused"),
        (UNADMITTED_PRINCIPAL, "an unadmitted gateway principal is refused"),
    ] {
        let status = channels
            .bind(Request::new(pb::ChannelServiceBindRequest {
                principal: principal.to_owned(),
                tenant: "tenant-a".into(),
                project: "project-a".into(),
                database: "database-a".into(),
            }))
            .await
            .expect_err(expectation);
        assert_eq!(status.code(), tonic::Code::PermissionDenied);
        assert_eq!(tbep_code(&status), Some(pb::ErrorCode::OperationRefused));
    }

    let network_request = |principal: &'static str| {
        let mut request = Request::new(pb::MaintenanceServiceTopologyReadRequest {});
        request.metadata_mut().insert("x-tb-principal", principal.parse().unwrap());
        request.metadata_mut().insert("x-tb-tenant", "tenant-a".parse().unwrap());
        request.metadata_mut().insert("x-tb-project", "project-a".parse().unwrap());
        request.metadata_mut().insert("x-tb-database", "database-a".parse().unwrap());
        request
    };
    let mut maintenance = MaintenanceServiceClient::new(channel);
    let topology = maintenance
        .topology_read(network_request(GATEWAY_PRINCIPAL))
        .await
        .expect("an ordinary RPC accepts the certificate-bound principal")
        .into_inner();
    assert_eq!(topology.binding, pb::BindingKind::NetworkMtls as i32);
    assert_eq!(topology.subsystems, vec!["engine".to_owned()]);
    let status = maintenance
        .topology_read(network_request(UNADMITTED_PRINCIPAL))
        .await
        .expect_err("ordinary RPC identity cannot escape the certificate binding");
    assert_eq!(status.code(), tonic::Code::PermissionDenied);
    assert_eq!(tbep_code(&status), Some(pb::ErrorCode::OperationRefused));
    drop(channels);
    listener.shutdown();
}

#[tokio::test]
#[allow(
    clippy::too_many_lines,
    reason = "one end-to-end stream test covers confirmation, batching, and close ordering"
)]
async fn the_backbone_stream_delivers_confirmation_batches_and_close() {
    let socket = socket_path("backbone");
    let host = Arc::new(FixedHost { feed: Mutex::new(None) });
    let server = Arc::new(TbepGrpcServer::new(
        Arc::clone(&host) as Arc<dyn TbepHost>,
        pb::BindingKind::Local,
    ));
    let mut listener = server.serve_uds(&socket).expect("the server binds");
    let channel = connect(socket.clone()).await;
    let mut backbone = BackboneServiceClient::new(channel);

    let mut stream = backbone
        .subscribe(identified(pb::BackboneServiceSubscribeRequest {
            subscription_id: 11,
            collections: vec!["orders".into()],
            from: Some(pb::SubscriptionStart {
                start: Some(pb::subscription_start::Start::Live(pb::SubscriptionLive {})),
            }),
        }))
        .await
        .expect("the subscription opens")
        .into_inner();

    let first =
        stream.message().await.expect("the confirmation arrives").expect("the stream is open");
    match first.event.expect("the event rides") {
        pb::subscribe_response::Event::Subscribed(response) => {
            assert_eq!(response.subscription_id, 11);
            assert_eq!(response.start_sequence, 5);
        }
        other => panic!("unexpected first event {other:?}"),
    }

    let feed = host.feed.lock().expect("the fixture lock is sound").take();
    let feed = feed.expect("the host holds the feed");
    feed.send(TbepNotification::Batch(registry::SubscriptionBatch {
        subscription_id: 11,
        from_sequence: 5,
        events: vec![registry::ChangeEvent {
            sequence: 5,
            operation_id: "op-9".into(),
            kind: registry::ChangeKind::Put,
            collection: "orders".into(),
            document: "d-1".into(),
        }],
        next_sequence: 6,
    }))
    .expect("the batch delivers");
    feed.send(TbepNotification::Trigger(registry::TriggerNotification {
        subscription_id: 11,
        collection: "orders".into(),
        sequence: 6,
        operation_id: "op-10".into(),
    }))
    .expect("the trigger delivers");
    feed.send(TbepNotification::Closed(registry::SubscriptionClosed {
        subscription_id: 11,
        reason: registry::SubscriptionCloseReason::Replaced,
    }))
    .expect("the close delivers");

    match stream
        .message()
        .await
        .expect("the batch arrives")
        .expect("the stream is open")
        .event
        .expect("the event rides")
    {
        pb::subscribe_response::Event::Batch(batch) => {
            assert_eq!(batch.events.len(), 1);
            assert_eq!(batch.next_sequence, 6);
        }
        other => panic!("unexpected batch event {other:?}"),
    }
    match stream
        .message()
        .await
        .expect("the trigger arrives")
        .expect("the stream is open")
        .event
        .expect("the event rides")
    {
        pb::subscribe_response::Event::Trigger(trigger) => {
            assert_eq!(trigger.sequence, 6);
        }
        other => panic!("unexpected trigger event {other:?}"),
    }
    match stream
        .message()
        .await
        .expect("the close arrives")
        .expect("the stream is open")
        .event
        .expect("the event rides")
    {
        pb::subscribe_response::Event::Closed(closed) => {
            assert_eq!(closed.reason, pb::SubscriptionCloseReason::Replaced as i32);
        }
        other => panic!("unexpected close event {other:?}"),
    }
    assert!(
        stream.message().await.expect("the stream ends cleanly").is_none(),
        "the close frame ends the stream"
    );
    listener.shutdown();
}

#[tokio::test]
async fn maintenance_answers_topology_health_and_shutdown() {
    let socket = socket_path("maintenance");
    let server = Arc::new(TbepGrpcServer::new(
        Arc::new(FixedHost { feed: Mutex::new(None) }) as Arc<dyn TbepHost>,
        pb::BindingKind::Local,
    ));
    let mut listener = server.serve_uds(&socket).expect("the server binds");
    let channel = connect(socket.clone()).await;
    let mut maintenance = MaintenanceServiceClient::new(channel);

    let topology = maintenance
        .topology_read(identified(pb::MaintenanceServiceTopologyReadRequest {}))
        .await
        .expect("the topology answers")
        .into_inner();
    assert_eq!(topology.binding, pb::BindingKind::Local as i32);

    let health = maintenance
        .supervision_health(identified(pb::MaintenanceServiceSupervisionHealthRequest {}))
        .await
        .expect("the health answers")
        .into_inner();
    assert_eq!(health.state, pb::HealthState::Serving as i32);

    let shutdown = maintenance
        .supervision_shutdown(identified(pb::MaintenanceServiceSupervisionShutdownRequest {
            reason: pb::ShutdownReason::Operator as i32,
        }))
        .await
        .expect("the shutdown answers")
        .into_inner();
    assert_eq!(shutdown.state, pb::HealthState::Draining as i32);
    assert_eq!(shutdown.in_flight, 2);
    listener.shutdown();
}
