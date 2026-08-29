//! The QUIC+TLS binding (P4): the identical frame layer over an
//! authenticated TLS 1.3 transport — the engine-backed host serves the
//! document spine over QUIC exactly as over the local socket.

use std::net::SocketAddr;
use std::sync::Arc;

use quinn::rustls::pki_types::{CertificateDer, PrivateKeyDer};
use rcgen::CertifiedKey;
use terrabase_tbep::TbepFrame;
use terrabase_tbep::engine_host::EngineHost;
use terrabase_tbep::generated::{
    ChannelBindRequest, DocumentPut, DocumentReadRequest, DocumentWriteRequest, ExpectedVersion,
    WriteOutcome,
};
use terrabase_tbep::quic::TbepQuicServer;
use terrabase_tbep::{IdempotencyKey, TbepBody, TbepValue};

/// One self-signed server identity for the row (tests provision their
/// own; operators provision production's).
fn identity() -> (CertificateDer<'static>, PrivateKeyDer<'static>) {
    let CertifiedKey { cert, key_pair, .. } =
        rcgen::generate_simple_self_signed(vec!["localhost".into()])
            .expect("the self-signed identity generates");
    (
        CertificateDer::from(cert.der().to_vec()),
        PrivateKeyDer::Pkcs8(key_pair.serialize_der().into()),
    )
}

/// The client side of one TBEP channel over QUIC.
struct QuicClient {
    runtime: tokio::runtime::Runtime,
    connection: quinn::Connection,
}

impl QuicClient {
    fn connect(
        address: SocketAddr,
        certificate: CertificateDer<'static>,
    ) -> Result<Self, Box<dyn std::error::Error>> {
        let runtime =
            tokio::runtime::Builder::new_multi_thread().worker_threads(1).enable_all().build()?;
        let client = runtime.block_on(async move {
            let mut roots = quinn::rustls::RootCertStore::empty();
            roots.add(certificate).expect("the server identity is trusted");
            let mut client_config = quinn::rustls::ClientConfig::builder()
                .with_root_certificates(roots)
                .with_no_client_auth();
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
    fn call(
        &mut self,
        body: impl Into<TbepBody>,
        idempotency: Option<&IdempotencyKey>,
    ) -> TbepFrame {
        self.correlation += 1;
        let frame = TbepFrame::seal(body, self.correlation, 0, idempotency).expect("seals");
        // The stream prefix is the frame's little-endian length — the
        // same framing the UDS stream path uses.
        let bytes = frame.to_bytes();
        let length = u32::try_from(bytes.len()).expect("the frame fits the prefix");
        let mut wire = length.to_le_bytes().to_vec();
        wire.extend_from_slice(&bytes);
        self.handle.block_on(self.send.write_all(&wire)).expect("the client writes");
        let answer = self.handle.block_on(async { read_frame(&mut self.recv).await });
        let answer = answer.expect("the answer arrives");
        assert_eq!(answer.causation(), self.correlation);
        answer
    }
}

/// Reads one length-prefixed frame off a QUIC receive stream.
async fn read_frame(recv: &mut quinn::RecvStream) -> std::io::Result<TbepFrame> {
    let mut prefix = [0u8; 4];
    recv.read_exact(&mut prefix).await.map_err(|_| {
        std::io::Error::new(std::io::ErrorKind::ConnectionReset, "the stream ended")
    })?;
    let length = u32::from_le_bytes(prefix) as usize;
    let mut bytes = vec![0u8; length];
    recv.read_exact(&mut bytes)
        .await
        .map_err(|_| std::io::Error::new(std::io::ErrorKind::ConnectionReset, "the body ended"))?;
    TbepFrame::from_bytes(&bytes).map_err(|_| {
        std::io::Error::new(std::io::ErrorKind::InvalidData, "the answer did not decode")
    })
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

    let (certificate, key) = identity();
    let host = Arc::new(EngineHost::open(&dir, 1).expect("the engine host opens"));
    let mut server =
        TbepQuicServer::bind(SocketAddr::from(([127, 0, 0, 1], 0)), certificate.clone(), key, host)
            .expect("the QUIC listener binds");
    let address = server.local_addr().expect("the listener names its address");

    let client = QuicClient::connect(address, certificate).expect("the client connects");
    let mut channel = client.channel();

    // The channel binds its tenant exactly as over the socket.
    let answer = channel
        .call(ChannelBindRequest { principal: "backend".into(), tenant: "tenant-a".into() }, None);
    assert!(matches!(answer.body(), TbepBody::ChannelBindResponse(_)));

    // A mutation commits with its receipt, over TLS.
    let answer = channel.call(
        DocumentWriteRequest {
            document: DocumentPut {
                id: "order-1".into(),
                collection: "orders".into(),
                body: TbepValue::Object(vec![
                    ("item".into(), TbepValue::String("bookshelf".into())),
                    ("quantity".into(), TbepValue::Integer(2)),
                ]),
            },
            expected: ExpectedVersion::Absent,
            operation_id: "op-quic-1".into(),
            ack: None,
        },
        Some(&IdempotencyKey::new("op-quic-1").expect("valid key")),
    );
    let TbepBody::DocumentWriteResponse(response) = answer.body() else {
        panic!("the write answered over QUIC, got {:?}", answer.kind());
    };
    let WriteOutcome::Committed(receipt) = &response.outcome else {
        panic!("the QUIC write commits, got {:?}", response.outcome);
    };
    assert_eq!(receipt.version, 1);

    // The read serves the published body over the same channel.
    let answer = channel.call(
        DocumentReadRequest {
            collection: "orders".into(),
            document: "order-1".into(),
            level: None,
        },
        None,
    );
    let TbepBody::DocumentReadResponse(response) = answer.body() else {
        panic!("the read answered over QUIC");
    };
    assert!(response.found);
    assert_eq!(response.document.as_ref().expect("served").version, 1);

    server.shutdown();
    let _ = std::fs::remove_dir_all(&dir);
}
