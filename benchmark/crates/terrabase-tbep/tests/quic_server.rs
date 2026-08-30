//! The production QUIC+TLS binding: generated size-prefixed `FlatBuffers` over
//! an authenticated TLS 1.3 stream, backed by the durable engine host.

use std::net::SocketAddr;
use std::sync::Arc;

use flatbuffers::{FlatBufferBuilder, UnionWIPOffset, WIPOffset};
use quinn::rustls::pki_types::{CertificateDer, PrivateKeyDer};
use rcgen::{BasicConstraints, CertificateParams, ExtendedKeyUsagePurpose, IsCa, KeyPair};
use tbep_fbs::decode_envelope;
use tbep_fbs::tbep_generated::terrabase::tbep as fbs;
use terrabase_tbep::engine_host::EngineHost;
use terrabase_tbep::quic::{QuicBindError, TbepQuicServer};

const MUTATION_ULID: &str = "01ARZ3NDEKTSV4RRFFQ69G5FAV";

fn frame_id(sequence: u64) -> [u8; 16] {
    let mut value = [0_u8; 16];
    value[8..].copy_from_slice(&sequence.to_be_bytes());
    value
}

fn finish_request(
    builder: &mut FlatBufferBuilder<'_>,
    operation: fbs::TbepOperation,
    body_type: fbs::TbepFrame,
    body: WIPOffset<UnionWIPOffset>,
    correlation: [u8; 16],
) -> Vec<u8> {
    let envelope = fbs::TbepEnvelope::create(
        builder,
        &fbs::TbepEnvelopeArgs {
            protocol_major: 1,
            protocol_minor: 0,
            required_features: fbs::FeatureBits::empty(),
            operation,
            correlation: Some(&fbs::Ulid(correlation)),
            causation: Some(&fbs::Ulid([0; 16])),
            body_type,
            body: Some(body),
        },
    );
    fbs::finish_size_prefixed_tbep_envelope_buffer(builder, envelope);
    builder.finished_data().to_vec()
}

fn negotiation_request(correlation: [u8; 16]) -> Vec<u8> {
    let mut builder = FlatBufferBuilder::new();
    let versions = builder.create_vector(&[1_u16]);
    let capabilities: Vec<_> = ["document.read", "document.write", "document.delete"]
        .into_iter()
        .map(|capability| builder.create_string(capability))
        .collect();
    let capabilities = builder.create_vector(&capabilities);
    let body = fbs::NegotiationRequestFrame::create(
        &mut builder,
        &fbs::NegotiationRequestFrameArgs {
            supported_versions: Some(versions),
            capabilities: Some(capabilities),
        },
    );
    finish_request(
        &mut builder,
        fbs::TbepOperation::NEGOTIATION_REQUEST,
        fbs::TbepFrame::NegotiationRequestFrame,
        body.as_union_value(),
        correlation,
    )
}

fn bind_request(correlation: [u8; 16]) -> Vec<u8> {
    let mut builder = FlatBufferBuilder::new();
    let principal = builder.create_string("backend");
    let tenant = builder.create_string("tenant-a");
    let body = fbs::ChannelBindRequest::create(
        &mut builder,
        &fbs::ChannelBindRequestArgs { principal: Some(principal), tenant: Some(tenant) },
    );
    finish_request(
        &mut builder,
        fbs::TbepOperation::CHANNEL_BIND,
        fbs::TbepFrame::ChannelBindRequest,
        body.as_union_value(),
        correlation,
    )
}

fn document_write_request(correlation: [u8; 16]) -> Vec<u8> {
    let mut builder = FlatBufferBuilder::new();
    let value = builder.create_string("bookshelf");
    let value =
        fbs::ValueString::create(&mut builder, &fbs::ValueStringArgs { value: Some(value) });
    let id = builder.create_string("order-1");
    let collection = builder.create_string("orders");
    let document = fbs::DocumentPut::create(
        &mut builder,
        &fbs::DocumentPutArgs {
            id: Some(id),
            collection: Some(collection),
            body_type: fbs::TbepValue::ValueString,
            body: Some(value.as_union_value()),
        },
    );
    let expected =
        fbs::ExpectedVersionAbsent::create(&mut builder, &fbs::ExpectedVersionAbsentArgs {});
    let operation_id = builder.create_string(MUTATION_ULID);
    let body = fbs::DocumentWriteRequest::create(
        &mut builder,
        &fbs::DocumentWriteRequestArgs {
            document: Some(document),
            expected_type: fbs::ExpectedVersion::ExpectedVersionAbsent,
            expected: Some(expected.as_union_value()),
            operation_id: Some(operation_id),
            ack: fbs::Acknowledgement::NONE,
        },
    );
    finish_request(
        &mut builder,
        fbs::TbepOperation::DOCUMENT_WRITE,
        fbs::TbepFrame::DocumentWriteRequest,
        body.as_union_value(),
        correlation,
    )
}

fn document_read_request(correlation: [u8; 16]) -> Vec<u8> {
    let mut builder = FlatBufferBuilder::new();
    let collection = builder.create_string("orders");
    let document = builder.create_string("order-1");
    let body = fbs::DocumentReadRequest::create(
        &mut builder,
        &fbs::DocumentReadRequestArgs {
            collection: Some(collection),
            document: Some(document),
            level: fbs::ReadLevel::NONE,
        },
    );
    finish_request(
        &mut builder,
        fbs::TbepOperation::DOCUMENT_READ,
        fbs::TbepFrame::DocumentReadRequest,
        body.as_union_value(),
        correlation,
    )
}

/// One row-local CA with distinct server and client workload identities.
struct TestIdentity {
    root: CertificateDer<'static>,
    server_certificate: CertificateDer<'static>,
    server_key: PrivateKeyDer<'static>,
    client_certificate: CertificateDer<'static>,
    client_key: PrivateKeyDer<'static>,
}

fn identity() -> TestIdentity {
    let issuer_key = KeyPair::generate().expect("the row CA key generates");
    let mut issuer_params =
        CertificateParams::new(Vec::new()).expect("the row CA parameters build");
    issuer_params.is_ca = IsCa::Ca(BasicConstraints::Unconstrained);
    let issuer = issuer_params.self_signed(&issuer_key).expect("the row CA signs itself");

    let server_key = KeyPair::generate().expect("the server key generates");
    let mut server_params =
        CertificateParams::new(vec!["localhost".into()]).expect("the server parameters build");
    server_params.extended_key_usages = vec![ExtendedKeyUsagePurpose::ServerAuth];
    let server = server_params
        .signed_by(&server_key, &issuer, &issuer_key)
        .expect("the row CA signs the server identity");

    let client_key = KeyPair::generate().expect("the client key generates");
    let mut client_params = CertificateParams::new(vec!["database-gateway".into()])
        .expect("the client parameters build");
    client_params.extended_key_usages = vec![ExtendedKeyUsagePurpose::ClientAuth];
    let client = client_params
        .signed_by(&client_key, &issuer, &issuer_key)
        .expect("the row CA signs the client identity");

    TestIdentity {
        root: CertificateDer::from(issuer.der().to_vec()),
        server_certificate: CertificateDer::from(server.der().to_vec()),
        server_key: PrivateKeyDer::Pkcs8(server_key.serialize_der().into()),
        client_certificate: CertificateDer::from(client.der().to_vec()),
        client_key: PrivateKeyDer::Pkcs8(client_key.serialize_der().into()),
    }
}

/// The client side of one TBEP channel over QUIC.
struct QuicClient {
    runtime: tokio::runtime::Runtime,
    connection: quinn::Connection,
}

impl QuicClient {
    fn connect(
        address: SocketAddr,
        server_root: CertificateDer<'static>,
        client_identity: Option<(CertificateDer<'static>, PrivateKeyDer<'static>)>,
    ) -> Result<Self, Box<dyn std::error::Error>> {
        let runtime =
            tokio::runtime::Builder::new_multi_thread().worker_threads(1).enable_all().build()?;
        let client = runtime.block_on(async move {
            let mut roots = quinn::rustls::RootCertStore::empty();
            roots.add(server_root).expect("the server root is trusted");
            let builder = quinn::rustls::ClientConfig::builder().with_root_certificates(roots);
            let mut client_config = match client_identity {
                Some((certificate, key)) => builder
                    .with_client_auth_cert(vec![certificate], key)
                    .expect("the client identity configures"),
                None => builder.with_no_client_auth(),
            };
            client_config.alpn_protocols = vec![b"terrabase.engine.protocol/1".to_vec()];
            let quic_config = quinn::crypto::rustls::QuicClientConfig::try_from(client_config)
                .expect("the client config builds");
            let mut endpoint = quinn::Endpoint::client("127.0.0.1:0".parse().expect("local"))?;
            endpoint.set_default_client_config(quinn::ClientConfig::new(Arc::new(quic_config)));
            let connection = endpoint.connect(address, "localhost")?.await?;
            Ok::<quinn::Connection, Box<dyn std::error::Error>>(connection)
        })?;
        Ok(Self { runtime, connection: client })
    }

    /// Opens one channel and drives it with the standard call
    /// discipline: write a frame, read its answer.
    fn channel(&self) -> QuicChannel {
        let handle = self.runtime.handle().clone();
        let (send, recv) = self
            .runtime
            .block_on(async { self.connection.open_bi().await })
            .expect("the channel opens");
        QuicChannel { handle, send, recv, correlation: 0 }
    }
}

struct QuicChannel {
    handle: tokio::runtime::Handle,
    send: quinn::SendStream,
    recv: quinn::RecvStream,
    correlation: u64,
}

impl QuicChannel {
    fn call(&mut self, build_request: impl FnOnce([u8; 16]) -> Vec<u8>) -> Vec<u8> {
        self.correlation += 1;
        let correlation = frame_id(self.correlation);
        let request = build_request(correlation);
        self.handle.block_on(self.send.write_all(&request)).expect("the client writes");
        let answer = self
            .handle
            .block_on(async {
                tokio::time::timeout(std::time::Duration::from_secs(5), read_frame(&mut self.recv))
                    .await
            })
            .expect("the answer arrives within the channel deadline");
        let answer = answer.expect("the answer arrives");
        let decoded = decode_envelope(&answer).expect("the answer is a verified FlatBuffer");
        let envelope = decoded.envelope;
        assert_eq!(envelope.causation().expect("response causation").0, correlation);
        let response_correlation = envelope.correlation().expect("response correlation").0;
        assert_ne!(response_correlation, [0; 16]);
        assert_ne!(response_correlation, correlation);
        answer
    }
}

/// Reads one standard size-prefixed `FlatBuffer` from a QUIC receive stream.
async fn read_frame(recv: &mut quinn::RecvStream) -> std::io::Result<Vec<u8>> {
    let mut prefix = [0u8; 4];
    recv.read_exact(&mut prefix).await.map_err(|_| {
        std::io::Error::new(std::io::ErrorKind::ConnectionReset, "the stream ended")
    })?;
    let length = u32::from_le_bytes(prefix) as usize;
    let mut bytes = Vec::with_capacity(4 + length);
    bytes.extend_from_slice(&prefix);
    bytes.resize(4 + length, 0);
    recv.read_exact(&mut bytes[4..])
        .await
        .map_err(|_| std::io::Error::new(std::io::ErrorKind::ConnectionReset, "the body ended"))?;
    Ok(bytes)
}

#[test]
fn the_engine_serves_the_document_spine_over_quic_tls() {
    let dir = std::env::temp_dir().join(format!(
        "tbep-quic-{}-{}",
        std::process::id(),
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map_or(0, |elapsed| elapsed.as_nanos())
    ));
    let _ = std::fs::remove_dir_all(&dir);
    std::fs::create_dir_all(&dir).unwrap();

    let identity = identity();
    let host = Arc::new(EngineHost::open(&dir, 1).expect("the engine host opens"));
    let mut server = TbepQuicServer::bind(
        SocketAddr::from(([127, 0, 0, 1], 0)),
        vec![identity.server_certificate],
        identity.server_key,
        std::slice::from_ref(&identity.root),
        host,
    )
    .expect("the QUIC listener binds");
    let address = server.local_addr().expect("the listener names its address");

    let client = QuicClient::connect(
        address,
        identity.root,
        Some((identity.client_certificate, identity.client_key)),
    )
    .expect("the authenticated client connects");
    let mut channel = client.channel();

    let answer = channel.call(negotiation_request);
    let negotiation = decode_envelope(&answer).expect("negotiation envelope").envelope;
    assert_eq!(negotiation.operation(), fbs::TbepOperation::NEGOTIATION_RESPONSE);
    let negotiation =
        negotiation.body_as_negotiation_response_frame().expect("negotiation response body");
    assert_eq!(negotiation.selected_version(), 1);
    let capabilities = negotiation.accepted_capabilities().expect("accepted capabilities");
    assert_eq!(capabilities.len(), 3);

    let answer = channel.call(bind_request);
    let bind = decode_envelope(&answer).expect("bind envelope").envelope;
    assert_eq!(bind.operation(), fbs::TbepOperation::CHANNEL_BIND_RESPONSE);
    let bind = bind.body_as_channel_bind_response().expect("bind response body");
    assert_eq!(bind.tenant(), Some("tenant-a"));

    // A mutation commits with its receipt, over TLS.
    let answer = channel.call(document_write_request);
    let write = decode_envelope(&answer).expect("write envelope").envelope;
    assert_eq!(write.operation(), fbs::TbepOperation::DOCUMENT_WRITE_RESPONSE);
    let write = write.body_as_document_write_response().expect("write response body");
    let committed = write.outcome_as_write_committed().expect("committed outcome");
    let receipt = committed.receipt().expect("commit receipt");
    assert_eq!(receipt.version(), 1);
    assert_eq!(receipt.operation_id(), Some(MUTATION_ULID));

    // The read serves the published body over the same channel.
    let answer = channel.call(document_read_request);
    let read = decode_envelope(&answer).expect("read envelope").envelope;
    assert_eq!(read.operation(), fbs::TbepOperation::DOCUMENT_READ_RESPONSE);
    let read = read.body_as_document_read_response().expect("read response body");
    assert!(read.found());
    let document = read.document().expect("served document");
    assert_eq!(document.version(), 1);
    assert_eq!(document.body_as_value_string().and_then(|value| value.value()), Some("bookshelf"));

    server.shutdown();
    let _ = std::fs::remove_dir_all(&dir);
}

#[test]
fn the_quic_listener_rejects_a_client_without_a_workload_identity() {
    let identity = identity();
    let host = Arc::new(terrabase_tbep::SyncDeliveryHost::new(1));
    let mut server = TbepQuicServer::bind(
        SocketAddr::from(([127, 0, 0, 1], 0)),
        vec![identity.server_certificate],
        identity.server_key,
        std::slice::from_ref(&identity.root),
        host,
    )
    .expect("the QUIC listener binds");
    let address = server.local_addr().expect("the listener names its address");

    let rejected = match QuicClient::connect(address, identity.root, None) {
        Err(_) => true,
        Ok(client) => client.runtime.block_on(async {
            tokio::time::timeout(std::time::Duration::from_secs(5), async {
                let Ok((mut send, mut recv)) = client.connection.open_bi().await else {
                    return true;
                };
                if send.write_all(&bind_request(frame_id(1))).await.is_err() {
                    return true;
                }
                read_frame(&mut recv).await.is_err()
            })
            .await
            .unwrap_or(true)
        }),
    };
    assert!(rejected, "the TLS handshake must require a client identity");

    server.shutdown();
}

#[test]
fn an_authenticated_idle_connection_cannot_stall_a_later_channel() {
    let identity = identity();
    let idle_certificate = identity.client_certificate.clone();
    let idle_key = identity.client_key.clone_key();
    let idle_root = identity.root.clone();
    let active_root = identity.root.clone();
    let host = Arc::new(terrabase_tbep::SyncDeliveryHost::new(1));
    let mut server = TbepQuicServer::bind(
        SocketAddr::from(([127, 0, 0, 1], 0)),
        vec![identity.server_certificate],
        identity.server_key,
        std::slice::from_ref(&identity.root),
        host,
    )
    .expect("the QUIC listener binds");
    let address = server.local_addr().expect("the listener names its address");

    let _idle = QuicClient::connect(address, idle_root, Some((idle_certificate, idle_key)))
        .expect("the first authenticated connection opens without a stream");
    let active = QuicClient::connect(
        address,
        active_root,
        Some((identity.client_certificate, identity.client_key)),
    )
    .expect("the later authenticated client connects");
    let mut channel = active.channel();
    let answer = channel.call(negotiation_request);
    let envelope = decode_envelope(&answer).expect("negotiation envelope").envelope;
    assert_eq!(envelope.operation(), fbs::TbepOperation::NEGOTIATION_RESPONSE);

    server.shutdown();
}

#[test]
fn the_quic_listener_refuses_to_start_without_a_client_trust_anchor() {
    let identity = identity();
    let host = Arc::new(terrabase_tbep::SyncDeliveryHost::new(1));
    let result = TbepQuicServer::bind(
        SocketAddr::from(([127, 0, 0, 1], 0)),
        vec![identity.server_certificate],
        identity.server_key,
        &[],
        host,
    );
    match result {
        Err(QuicBindError::Tls) => {}
        Err(other) => panic!("an empty client trust store returned {other:?}"),
        Ok(mut listener) => {
            listener.shutdown();
            panic!("an empty client trust store must fail closed");
        }
    }
}
