//! Bounded native HTTPS egress with certificate verification and pinned DNS.

use std::collections::BTreeMap;
use std::future::Future;
use std::net::{IpAddr, SocketAddr, ToSocketAddrs};
use std::sync::{Arc, OnceLock, mpsc};
use std::time::Duration;

use bytes::Bytes;
use http_body_util::Full;
use hyper::http::{HeaderName, HeaderValue, Uri};
use hyper_util::rt::TokioIo;
use rustls::pki_types::ServerName;
use tokio::runtime::Handle;
use tokio::sync::Semaphore;
use tokio_rustls::TlsConnector;

use super::{DeliveryError, DeliveryResponse, HttpsTransport, MAX_DELIVERY_BYTES};

const MAX_ADDRESSES: usize = 16;
const MAX_INFLIGHT: usize = 16;
const MAX_HEADER_BYTES: usize = 16 * 1024;
const ATTEMPT_TIMEOUT: Duration = Duration::from_secs(10);
static ADMISSION: OnceLock<Arc<Semaphore>> = OnceLock::new();

/// Native webhook transport using Mozilla trust roots and the member's runtime.
///
/// Call its synchronous methods from blocking workers (as the webhook router
/// and dispatcher do). DNS workers retain their admission permit until the
/// system resolver finishes, even when the caller times out. This bounds work
/// during a stalled resolver. Every HTTPS attempt has one total deadline,
/// validates the original hostname, and connects only to supplied addresses.
/// Proxy environment variables and redirects are never used.
pub struct NativeHttpsTransport {
    runtime: Handle,
    admission: Arc<Semaphore>,
    tls: Arc<rustls::ClientConfig>,
    timeout: Duration,
}

impl NativeHttpsTransport {
    /// Creates production HTTPS egress. No insecure TLS mode is available.
    ///
    /// # Errors
    ///
    /// Returns a redacted permanent failure if the TLS provider cannot initialize.
    pub fn new(runtime: Handle) -> Result<Self, DeliveryError> {
        let roots: rustls::RootCertStore = webpki_roots::TLS_SERVER_ROOTS.iter().cloned().collect();
        let tls = rustls::ClientConfig::builder_with_provider(Arc::new(
            rustls::crypto::aws_lc_rs::default_provider(),
        ))
        .with_safe_default_protocol_versions()
        .map_err(|_| DeliveryError::Permanent)?
        .with_root_certificates(roots)
        .with_no_client_auth();
        Ok(Self {
            runtime,
            admission: Arc::clone(ADMISSION.get_or_init(|| Arc::new(Semaphore::new(MAX_INFLIGHT)))),
            tls: Arc::new(tls),
            timeout: ATTEMPT_TIMEOUT,
        })
    }

    fn wait<T: Send + 'static>(
        &self,
        work: impl Future<Output = Result<T, DeliveryError>> + Send + 'static,
    ) -> Result<T, DeliveryError> {
        let permit =
            self.admission.clone().try_acquire_owned().map_err(|_| DeliveryError::Retryable)?;
        let (sender, receiver) = mpsc::sync_channel(1);
        let timeout = self.timeout;
        let task = self.runtime.spawn(async move {
            let result =
                tokio::time::timeout(timeout, work).await.unwrap_or(Err(DeliveryError::Retryable));
            drop(permit);
            let _ = sender.send(result);
        });
        let result = receiver.recv_timeout(timeout + Duration::from_secs(1));
        task.abort();
        result.map_err(|_| DeliveryError::Retryable)?
    }
}

fn target(endpoint: &str) -> Result<Uri, DeliveryError> {
    let endpoint = super::validate_endpoint(endpoint).map_err(|_| DeliveryError::TargetRejected)?;
    let uri: Uri = endpoint.parse().map_err(|_| DeliveryError::TargetRejected)?;
    if uri.host().is_none() {
        return Err(DeliveryError::TargetRejected);
    }
    Ok(uri)
}

impl HttpsTransport for NativeHttpsTransport {
    fn resolve(&self, endpoint: &str) -> Result<Vec<IpAddr>, DeliveryError> {
        let uri = target(endpoint)?;
        let host =
            uri.host().ok_or(DeliveryError::TargetRejected)?.trim_matches(['[', ']']).to_owned();
        let port = uri.port_u16().unwrap_or(443);
        let permit =
            self.admission.clone().try_acquire_owned().map_err(|_| DeliveryError::Retryable)?;
        let (sender, receiver) = mpsc::sync_channel(1);
        self.runtime.spawn_blocking(move || {
            // Keep the permit inside the resolver work, not the waiting caller.
            let _permit = permit;
            let result = (host.as_str(), port)
                .to_socket_addrs()
                .map_err(|_| DeliveryError::Retryable)
                .and_then(|addresses| {
                    let mut ips = Vec::new();
                    for address in addresses.take(MAX_ADDRESSES + 1) {
                        ips.push(address.ip());
                    }
                    if ips.is_empty() || ips.len() > MAX_ADDRESSES {
                        return Err(DeliveryError::TargetRejected);
                    }
                    ips.sort_unstable();
                    ips.dedup();
                    Ok(ips)
                });
            let _ = sender.send(result);
        });
        receiver.recv_timeout(self.timeout).map_err(|_| DeliveryError::Retryable)?
    }

    fn send_https(
        &self,
        endpoint: &str,
        addresses: &[IpAddr],
        headers: &BTreeMap<String, String>,
        body: &[u8],
    ) -> Result<DeliveryResponse, DeliveryError> {
        let uri = target(endpoint)?;
        if addresses.is_empty()
            || addresses.len() > MAX_ADDRESSES
            || addresses.iter().any(|address| !super::is_public_ip(*address))
        {
            return Err(DeliveryError::TargetRejected);
        }
        let request = request(&uri, headers, body)?;
        let addresses = addresses.to_vec();
        let tls = Arc::clone(&self.tls);
        self.wait(send_pinned(uri, addresses, request, tls))
    }
}

fn request(
    uri: &Uri,
    headers: &BTreeMap<String, String>,
    body: &[u8],
) -> Result<hyper::Request<Full<Bytes>>, DeliveryError> {
    if body.len() > MAX_DELIVERY_BYTES
        || headers.len() > 32
        || headers.iter().map(|(name, value)| name.len() + value.len()).sum::<usize>()
            > MAX_HEADER_BYTES
    {
        return Err(DeliveryError::Permanent);
    }
    let authority = uri.authority().ok_or(DeliveryError::TargetRejected)?;
    let mut request = hyper::Request::builder()
        .method("POST")
        .uri(uri.path_and_query().map_or("/", hyper::http::uri::PathAndQuery::as_str))
        .header("host", authority.as_str())
        .header("content-type", "application/json")
        .header("connection", "close")
        .body(Full::new(Bytes::copy_from_slice(body)))
        .map_err(|_| DeliveryError::Permanent)?;
    for (name, value) in headers {
        let name = HeaderName::from_bytes(name.as_bytes()).map_err(|_| DeliveryError::Permanent)?;
        if matches!(
            name.as_str(),
            "host" | "content-length" | "transfer-encoding" | "connection" | "upgrade" | "expect"
        ) {
            return Err(DeliveryError::Permanent);
        }
        let value = HeaderValue::from_str(value).map_err(|_| DeliveryError::Permanent)?;
        request.headers_mut().insert(name, value);
    }
    Ok(request)
}

async fn send_pinned(
    uri: Uri,
    addresses: Vec<IpAddr>,
    request: hyper::Request<Full<Bytes>>,
    tls: Arc<rustls::ClientConfig>,
) -> Result<DeliveryResponse, DeliveryError> {
    let port = uri.port_u16().unwrap_or(443);
    let host = uri.host().ok_or(DeliveryError::TargetRejected)?.trim_matches(['[', ']']);
    let name = ServerName::try_from(host.to_owned()).map_err(|_| DeliveryError::TargetRejected)?;
    let mut connected = None;
    for address in addresses {
        if let Ok(stream) = tokio::net::TcpStream::connect(SocketAddr::new(address, port)).await {
            connected = Some(stream);
            break;
        }
    }
    let stream = connected.ok_or(DeliveryError::Retryable)?;
    let stream = TlsConnector::from(tls)
        .connect(name, stream)
        .await
        .map_err(|_| DeliveryError::Retryable)?;
    let (mut sender, connection) = hyper::client::conn::http1::Builder::new()
        .max_headers(32)
        .max_buf_size(MAX_HEADER_BYTES)
        .handshake(TokioIo::new(stream))
        .await
        .map_err(|_| DeliveryError::Retryable)?;
    // Poll the connection in the same future so cancellation always drops the
    // socket. A detached connection task could outlive the attempt deadline.
    let response = async {
        let response = sender.send_request(request).await.map_err(|_| DeliveryError::Retryable)?;
        // We only need status. Dropping the body and connection bounds response
        // work even if the peer streams forever. Redirects remain terminal 3xx.
        Ok(DeliveryResponse { status: response.status().as_u16() })
    };
    tokio::pin!(response, connection);
    tokio::select! {
        biased;
        result = &mut response => result,
        _ = &mut connection => response.await,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use rustls::pki_types::PrivatePkcs8KeyDer;
    use std::convert::Infallible;

    fn test_tls() -> (Arc<rustls::ServerConfig>, Arc<rustls::ClientConfig>) {
        let cert = rcgen::generate_simple_self_signed(vec!["hooks.example".into()]).expect("cert");
        let provider = Arc::new(rustls::crypto::aws_lc_rs::default_provider());
        let server = rustls::ServerConfig::builder_with_provider(Arc::clone(&provider))
            .with_safe_default_protocol_versions()
            .expect("versions")
            .with_no_client_auth()
            .with_single_cert(
                vec![cert.cert.der().clone()],
                PrivatePkcs8KeyDer::from(cert.key_pair.serialize_der()).into(),
            )
            .expect("server TLS");
        let mut roots = rustls::RootCertStore::empty();
        roots.add(cert.cert.der().clone()).expect("test root");
        let client = rustls::ClientConfig::builder_with_provider(provider)
            .with_safe_default_protocol_versions()
            .expect("versions")
            .with_root_certificates(roots)
            .with_no_client_auth();
        (Arc::new(server), Arc::new(client))
    }

    #[tokio::test]
    async fn native_tls_pins_address_preserves_hostname_and_does_not_follow_redirects() {
        let (server, client) = test_tls();
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.expect("listener");
        let port = listener.local_addr().expect("address").port();
        let task = tokio::spawn(async move {
            let (stream, _) = listener.accept().await.expect("accept");
            let stream = tokio_rustls::TlsAcceptor::from(server).accept(stream).await.expect("TLS");
            hyper::server::conn::http1::Builder::new()
                .serve_connection(
                    TokioIo::new(stream),
                    hyper::service::service_fn(
                        move |request: hyper::Request<hyper::body::Incoming>| async move {
                            assert_eq!(request.method(), "POST");
                            assert_eq!(request.uri(), "/events?source=test");
                            assert_eq!(request.headers()["host"], format!("hooks.example:{port}"));
                            assert_eq!(request.headers()["x-test-signature"], "signed");
                            Ok::<_, Infallible>(
                                hyper::Response::builder()
                                    .status(302)
                                    .header("location", "https://127.0.0.1/private")
                                    .body(Full::new(Bytes::new()))
                                    .expect("response"),
                            )
                        },
                    ),
                )
                .await
                .expect("HTTP");
            assert!(
                tokio::time::timeout(Duration::from_millis(50), listener.accept()).await.is_err()
            );
        });
        let uri: Uri =
            format!("https://hooks.example:{port}/events?source=test").parse().expect("uri");
        let headers = BTreeMap::from([("x-test-signature".into(), "signed".into())]);
        let request = request(&uri, &headers, b"payload").expect("request");
        let response = send_pinned(uri, vec!["127.0.0.1".parse().expect("ip")], request, client)
            .await
            .expect("response");
        assert_eq!(response.status, 302);
        task.await.expect("server");
    }

    #[tokio::test]
    async fn native_tls_rejects_a_certificate_for_a_different_hostname() {
        let (server, client) = test_tls();
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.expect("listener");
        let port = listener.local_addr().expect("address").port();
        let task = tokio::spawn(async move {
            let (stream, _) = listener.accept().await.expect("accept");
            assert!(tokio_rustls::TlsAcceptor::from(server).accept(stream).await.is_err());
        });
        let uri: Uri = format!("https://other.example:{port}/events").parse().expect("uri");
        let request = request(&uri, &BTreeMap::new(), b"payload").expect("request");
        assert_eq!(
            send_pinned(uri, vec!["127.0.0.1".parse().expect("ip")], request, client).await,
            Err(DeliveryError::Retryable)
        );
        task.await.expect("server");
    }

    #[tokio::test]
    async fn native_transport_bounds_attempt_time_and_admission() {
        let mut transport = NativeHttpsTransport::new(Handle::current()).expect("transport");
        transport.timeout = Duration::from_millis(10);
        transport.admission = Arc::new(Semaphore::new(MAX_INFLIGHT));
        let transport = Arc::new(transport);
        let worker = Arc::clone(&transport);
        let result = tokio::task::spawn_blocking(move || {
            worker.wait(std::future::pending::<Result<(), DeliveryError>>())
        })
        .await
        .expect("worker");
        assert_eq!(result, Err(DeliveryError::Retryable));
        assert_eq!(transport.admission.available_permits(), MAX_INFLIGHT);
        let _permits = transport
            .admission
            .clone()
            .try_acquire_many_owned(u32::try_from(MAX_INFLIGHT).expect("bound"))
            .expect("permits");
        assert_eq!(
            transport.resolve("https://hooks.example/events"),
            Err(DeliveryError::Retryable)
        );
        assert_eq!(transport.wait(async { Ok(()) }), Err(DeliveryError::Retryable));
    }

    #[tokio::test]
    async fn native_transports_share_one_process_admission_budget() {
        let first = NativeHttpsTransport::new(Handle::current()).expect("first");
        let second = NativeHttpsTransport::new(Handle::current()).expect("second");
        assert!(Arc::ptr_eq(&first.admission, &second.admission));
    }

    #[tokio::test]
    async fn native_transport_refuses_private_pins_and_unsafe_request_headers() {
        let transport = NativeHttpsTransport::new(Handle::current()).expect("transport");
        assert_eq!(
            transport.send_https(
                "https://hooks.example/events",
                &["127.0.0.1".parse().expect("ip")],
                &BTreeMap::new(),
                b"body"
            ),
            Err(DeliveryError::TargetRejected)
        );
        let uri: Uri = "https://hooks.example/events".parse().expect("uri");
        for name in ["Host", "Transfer-Encoding", "Content-Length", "Connection", "Expect"] {
            assert!(
                request(&uri, &BTreeMap::from([(name.into(), "override".into())]), b"body")
                    .is_err()
            );
        }
    }
}
