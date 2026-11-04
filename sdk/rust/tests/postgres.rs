#![cfg(feature = "postgres")]

use base64::engine::general_purpose::URL_SAFE_NO_PAD;
use base64::Engine;
use rustls::pki_types::{pem::PemObject, CertificateDer, PrivateKeyDer};
use std::io::{self, Cursor};
use std::sync::Arc;
use std::time::Duration;
use terrascale::postgres::{PostgresClient, PostgresConfig, PostgresErrorKind};
use tokio::io::{AsyncRead, AsyncReadExt, AsyncWrite, AsyncWriteExt};
use tokio::net::TcpListener;
use tokio::task::JoinHandle;
use tokio_rustls::LazyConfigAcceptor;

#[tokio::test]
async fn postgres_configuration_fails_before_socket_and_redacts_password() {
    let password = "tbk1.do-not-report-secret";
    let config = PostgresConfig {
        host: String::new(),
        port: 0,
        tls_server_name: String::new(),
        ca_certificate_pem: vec![],
        database: "invalid".to_owned(),
        key_id: "invalid".to_owned(),
        password: password.to_owned(),
        connect_timeout: Duration::from_secs(5),
    };
    assert!(!format!("{config:?}").contains(password));
    let error = match PostgresClient::connect(config).await {
        Ok(_) => panic!("invalid config connected"),
        Err(error) => error,
    };
    assert_eq!(error.kind, PostgresErrorKind::Configuration);
    assert!(!error.to_string().contains(password));
}

// A scripted driver peer tests TLS and future cancellation only. It is not a
// TerraBase executor, native-success fixture, or SQL compatibility proof.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum Action {
    Prepare,
    Execute,
    Begin,
    Commit,
    Rollback,
}

#[derive(Debug, Default)]
struct Observation {
    offered_alpn: Vec<Vec<u8>>,
    startup: bool,
    password: bool,
    closed: bool,
    actions: Vec<Action>,
}

struct Peer {
    port: u16,
    task: Option<JoinHandle<Observation>>,
}

impl Peer {
    fn config(&self) -> PostgresConfig {
        let key_id = URL_SAFE_NO_PAD.encode([1; 16]);
        PostgresConfig {
            host: "127.0.0.1".to_owned(),
            port: self.port,
            tls_server_name: "postgres.test".to_owned(),
            ca_certificate_pem: PEER_CERT.as_bytes().to_vec(),
            database: "11111111-1111-4111-8111-111111111111".to_owned(),
            password: format!("tbk1.{key_id}.{}", URL_SAFE_NO_PAD.encode([2; 32])),
            key_id,
            connect_timeout: Duration::from_secs(2),
        }
    }

    async fn finish(mut self) -> Observation {
        let mut task = self.task.take().expect("owned test peer task");
        match tokio::time::timeout(Duration::from_secs(3), &mut task).await {
            Ok(result) => result.expect("test peer task"),
            Err(_) => {
                task.abort();
                let _ = task.await;
                panic!("test peer did not close");
            }
        }
    }
}

impl Drop for Peer {
    fn drop(&mut self) {
        if let Some(task) = &self.task {
            task.abort();
        }
    }
}

async fn spawn_peer(
    alpn: &[&[u8]],
    hold: Option<Action>,
    execute_error: Option<&'static str>,
) -> Peer {
    let listener = TcpListener::bind(("127.0.0.1", 0))
        .await
        .expect("test listener");
    let port = listener.local_addr().expect("listener address").port();
    let certificates = CertificateDer::pem_reader_iter(Cursor::new(PEER_CERT))
        .collect::<Result<Vec<_>, _>>()
        .expect("test certificate");
    let key = PrivateKeyDer::from_pem_reader(Cursor::new(PEER_KEY)).expect("test key PEM");
    let mut config = rustls::ServerConfig::builder_with_provider(Arc::new(
        rustls::crypto::ring::default_provider(),
    ))
    .with_protocol_versions(&[&rustls::version::TLS13])
    .expect("TLS1.3")
    .with_no_client_auth()
    .with_single_cert(certificates, key)
    .expect("test server certificate");
    config.alpn_protocols = alpn.iter().map(|value| value.to_vec()).collect();
    let task = tokio::spawn(async move {
        let mut observed = Observation::default();
        let (mut socket, _) = listener.accept().await.expect("test accept");
        socket.set_nodelay(true).expect("test TCP nodelay");
        let mut ssl_request = [0; 8];
        socket
            .read_exact(&mut ssl_request)
            .await
            .expect("SSLRequest");
        assert_eq!(ssl_request, [0, 0, 0, 8, 4, 210, 22, 47]);
        socket.write_all(b"S").await.expect("SSL accepted");
        let handshake = LazyConfigAcceptor::new(rustls::server::Acceptor::default(), socket)
            .await
            .expect("client hello");
        observed.offered_alpn = handshake
            .client_hello()
            .alpn()
            .map(|protocols| protocols.map(<[u8]>::to_vec).collect())
            .unwrap_or_default();
        let Ok(mut stream) = handshake.into_stream(Arc::new(config)).await else {
            observed.closed = true;
            return observed;
        };
        let mut length = [0; 4];
        if stream.read_exact(&mut length).await.is_err() {
            observed.closed = true;
            return observed;
        }
        observed.startup = true;
        let startup_length = usize::try_from(u32::from_be_bytes(length)).expect("startup length");
        assert!((8..=16384).contains(&startup_length));
        let mut startup = vec![0; startup_length - 4];
        stream.read_exact(&mut startup).await.expect("startup body");
        send_frame(&mut stream, b'R', &[0, 0, 0, 3]).await;
        let (kind, _) = read_frame(&mut stream)
            .await
            .expect("password frame")
            .expect("password");
        assert_eq!(kind, b'p');
        observed.password = true;
        send_frame(&mut stream, b'R', &[0, 0, 0, 0]).await;
        send_frame(&mut stream, b'S', b"client_encoding\0UTF8\0").await;
        send_frame(&mut stream, b'Z', b"I").await;
        let mut held = false;
        let mut preparing = false;
        let mut executing = false;
        let mut closing_statement = false;
        let mut status = b'I';
        while let Some((kind, body)) = read_frame(&mut stream).await.expect("driver frame") {
            let action = match kind {
                b'P' => {
                    preparing = true;
                    Some(Action::Prepare)
                }
                b'E' => {
                    executing = true;
                    Some(Action::Execute)
                }
                b'Q' if body == b"START TRANSACTION\0" => Some(Action::Begin),
                b'Q' if body == b"COMMIT\0" => Some(Action::Commit),
                b'Q' if body == b"ROLLBACK\0" => Some(Action::Rollback),
                _ => None,
            };
            if let Some(action) = action {
                observed.actions.push(action);
                if hold == Some(action) {
                    held = true;
                }
            }
            if held {
                continue;
            }
            match kind {
                b'C' => closing_statement = true,
                b'Q' => {
                    status = if action == Some(Action::Begin) {
                        b'T'
                    } else {
                        b'I'
                    };
                    send_frame(&mut stream, b'C', &body).await;
                    send_frame(&mut stream, b'Z', &[status]).await;
                }
                b'S' if closing_statement => {
                    send_frame(&mut stream, b'3', &[]).await;
                    send_frame(&mut stream, b'Z', &[status]).await;
                    closing_statement = false;
                }
                b'S' if preparing => {
                    send_frame(&mut stream, b'1', &[]).await;
                    send_frame(&mut stream, b't', &[0, 0]).await;
                    send_frame(&mut stream, b'n', &[]).await;
                    send_frame(&mut stream, b'Z', &[status]).await;
                    preparing = false;
                }
                b'S' if executing => {
                    if let Some(code) = execute_error {
                        let fields = format!("SERROR\0C{code}\0Mtest rejection\0\0");
                        send_frame(&mut stream, b'E', fields.as_bytes()).await;
                    } else {
                        send_frame(&mut stream, b'2', &[]).await;
                        send_frame(&mut stream, b'C', b"UPDATE 1\0").await;
                    }
                    send_frame(&mut stream, b'Z', &[status]).await;
                    executing = false;
                }
                b'X' => {
                    observed.closed = true;
                    return observed;
                }
                _ => {}
            }
        }
        observed.closed = true;
        observed
    });
    Peer {
        port,
        task: Some(task),
    }
}

async fn read_frame<S: AsyncRead + Unpin>(stream: &mut S) -> io::Result<Option<(u8, Vec<u8>)>> {
    let mut kind = [0];
    match stream.read_exact(&mut kind).await {
        Ok(_) => {}
        Err(error)
            if matches!(
                error.kind(),
                io::ErrorKind::UnexpectedEof | io::ErrorKind::ConnectionReset
            ) =>
        {
            return Ok(None)
        }
        Err(error) => return Err(error),
    }
    let length = stream.read_u32().await?;
    if !(4..=16384).contains(&length) {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "test frame bound",
        ));
    }
    let mut body = vec![0; usize::try_from(length - 4).expect("bounded frame")];
    stream.read_exact(&mut body).await?;
    Ok(Some((kind[0], body)))
}

async fn send_frame<S: AsyncWrite + Unpin>(stream: &mut S, kind: u8, body: &[u8]) {
    stream.write_u8(kind).await.expect("test response kind");
    stream
        .write_u32(u32::try_from(body.len() + 4).expect("test response length"))
        .await
        .expect("test response length write");
    stream.write_all(body).await.expect("test response body");
    stream.flush().await.expect("test response flush");
}

#[tokio::test]
async fn postgres_requires_selected_alpn_and_verified_ca_name_before_startup() {
    for (alpn, wrong_ca, wrong_name) in [
        (None, false, false),
        (Some(b"http/1.1".as_slice()), false, false),
        (Some(b"postgresql".as_slice()), true, false),
        (Some(b"postgresql".as_slice()), false, true),
    ] {
        let protocols = alpn.into_iter().collect::<Vec<_>>();
        let peer = spawn_peer(&protocols, None, None).await;
        let mut config = peer.config();
        if wrong_ca {
            config.ca_certificate_pem = OTHER_CERT.as_bytes().to_vec();
        }
        if wrong_name {
            config.tls_server_name = "wrong.test".to_owned();
        }
        let result = PostgresClient::connect(config).await;
        assert!(result.is_err(), "unverified or absent ALPN admitted");
        let observed = peer.finish().await;
        assert_eq!(observed.offered_alpn, [b"postgresql".to_vec()]);
        assert!(!observed.startup);
        assert!(!observed.password);
        assert!(observed.closed);
    }
    let peer = spawn_peer(&[b"postgresql"], None, None).await;
    let client = PostgresClient::connect(peer.config())
        .await
        .expect("verified TLS1.3 and ALPN");
    client.close().await.expect("clean close");
    let observed = peer.finish().await;
    assert_eq!(observed.offered_alpn, [b"postgresql".to_vec()]);
    assert!(observed.startup && observed.password && observed.closed);
}

const PEER_CERT: &str = r"-----BEGIN CERTIFICATE-----
MIIBnjCCAUSgAwIBAgIUDj4jlC5h5wtIocZmKitxhVtx6MAwCgYIKoZIzj0EAwIw
GDEWMBQGA1UEAwwNcG9zdGdyZXMudGVzdDAgFw0yNjA5MzAwNjE2MjdaGA8yMTI2
MDkwNjA2MTYyN1owGDEWMBQGA1UEAwwNcG9zdGdyZXMudGVzdDBZMBMGByqGSM49
AgEGCCqGSM49AwEHA0IABBN/A66JLXXchGxr4PLUkMCthq/UGlj85aHdjopPc3MA
xyP2m6QX31g65BCudI4NAUa/eH/8XeBfPu0tf/v4crejajBoMB0GA1UdDgQWBBTG
mXkx5d99RE52UYimLH+dCrq94zAfBgNVHSMEGDAWgBTGmXkx5d99RE52UYimLH+d
Crq94zAYBgNVHREEETAPgg1wb3N0Z3Jlcy50ZXN0MAwGA1UdEwEB/wQCMAAwCgYI
KoZIzj0EAwIDSAAwRQIhALDfpD45l12H58V4G89g/3vp/OONvnEunwZb7jWg+PYL
AiBjCHmKRsP8C1meW/k8FZTDFyHV+1tGRrRBybRFCP+a+g==
-----END CERTIFICATE-----
";

const PEER_KEY: &str = r"-----BEGIN PRIVATE KEY-----
MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQgV/+znTrTmzPxlTFV
7q4TJCUy/zJ9qtcHx06wJ1BJ1XmhRANCAAQTfwOuiS113IRsa+Dy1JDArYav1BpY
/OWh3Y6KT3NzAMcj9pukF99YOuQQrnSODQFGv3h//F3gXz7tLX/7+HK3
-----END PRIVATE KEY-----
";

const OTHER_CERT: &str = r"-----BEGIN CERTIFICATE-----
MIIBnzCCAUSgAwIBAgIUR4Kb4EhCSfk4qdpRSavfQtTtp0cwCgYIKoZIzj0EAwIw
GDEWMBQGA1UEAwwNcG9zdGdyZXMudGVzdDAgFw0yNjA5MzAwNjE2MjdaGA8yMTI2
MDkwNjA2MTYyN1owGDEWMBQGA1UEAwwNcG9zdGdyZXMudGVzdDBZMBMGByqGSM49
AgEGCCqGSM49AwEHA0IABCP1h01Yj2YmXlWWW/bDz0B26sHVy1CU0QKIVOZnR/qn
UeQs7DhnlnDe+jtdOVXqnmtZidodQQraI+r0FJ8SnZejajBoMB0GA1UdDgQWBBRV
2wYHvrI76HGbew9AoauaZ3oPWzAfBgNVHSMEGDAWgBRV2wYHvrI76HGbew9Aoaua
Z3oPWzAYBgNVHREEETAPgg1wb3N0Z3Jlcy50ZXN0MAwGA1UdEwEB/wQCMAAwCgYI
KoZIzj0EAwIDSQAwRgIhAKBrcFaFYq8RQRE/0T48mP1qXgHlmSJHkizbqC7KH8f3
AiEAywijbHiBGdW70YsWy4kE0Rge++1ijl8z+opqIUsR+SA=
-----END CERTIFICATE-----
";
