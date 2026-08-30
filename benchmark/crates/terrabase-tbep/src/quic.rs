//! The TBEP QUIC+TLS network binding (P4): the identical frame layer
//! the UDS binding serves, over an authenticated TLS 1.3 transport.
//!
//! The contract records QUIC+TLS as the supported network binding: the
//! engine process owns its certificate, clients verify it, and the
//! generated, structurally verified and semantically validated `FlatBuffers`
//! frames ride one bidirectional QUIC stream per channel. The legacy UDS
//! positional codec is not reachable through this production listener.
//!
//! Every accepted connection presents a client certificate chaining to
//! an operator-supplied trust anchor. The channel's tenant binding is an
//! additional application identity and never substitutes for mutual TLS.
//! 0-RTT is not enabled for any traffic because mutation replay safety has
//! not been qualified.

use std::net::SocketAddr;
use std::sync::Arc;
use std::sync::atomic::AtomicUsize;

use quinn::crypto::rustls::QuicServerConfig;
use quinn::rustls::pki_types::{CertificateDer, PrivateKeyDer, PrivatePkcs8KeyDer};
use tokio::runtime::Handle;
use tokio::sync::Semaphore;

use crate::flatbuffers_server::serve_flatbuffers_io;
use crate::host::TbepHost;
use crate::server::MAX_INFLIGHT_OPERATIONS;

/// The binding's ALPN identity: every TBEP QUIC endpoint offers and
/// requires exactly this protocol name.
pub const TBEP_ALPN: &[&[u8]] = &[b"terrabase.engine.protocol/1"];

/// Maximum authenticated QUIC channels admitted at one listener. Each
/// channel is sequential and owns at most one engine operation at a time.
pub const MAX_QUIC_CONNECTIONS: usize = MAX_INFLIGHT_OPERATIONS;

/// A QUIC binding failure.
#[derive(Debug)]
pub enum QuicBindError {
    /// The TLS configuration could not be built from the certificate.
    Tls,
    /// The UDP endpoint could not be bound.
    Bind,
    /// The async runtime could not start.
    Runtime,
}

impl std::fmt::Display for QuicBindError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Tls => write!(f, "cannot build the TBEP TLS configuration"),
            Self::Bind => write!(f, "cannot bind the TBEP QUIC listener"),
            Self::Runtime => write!(f, "cannot start the TBEP serving runtime"),
        }
    }
}

impl std::error::Error for QuicBindError {}

/// The running QUIC server handle.
pub struct TbepQuicHandle {
    endpoint: quinn::Endpoint,
    runtime: Option<std::thread::JoinHandle<()>>,
}

impl TbepQuicHandle {
    /// The bound socket address.
    #[must_use]
    pub fn local_addr(&self) -> Option<SocketAddr> {
        self.endpoint.local_addr().ok()
    }

    /// Closes the listener and waits for the serving thread.
    pub fn shutdown(&mut self) {
        self.endpoint.close(0_u32.into(), b"tbep shutdown");
        if let Some(thread) = self.runtime.take() {
            let _ = thread.join();
        }
    }
}

impl Drop for TbepQuicHandle {
    fn drop(&mut self) {
        self.shutdown();
    }
}

/// The receiving half of one TBEP channel's QUIC stream: the std-io
/// bridge the transport-generic serving loop reads through.
struct QuicReader {
    handle: Handle,
    recv: quinn::RecvStream,
}

impl std::io::Read for QuicReader {
    fn read(&mut self, buf: &mut [u8]) -> std::io::Result<usize> {
        match self.handle.block_on(self.recv.read(buf)) {
            Ok(Some(read)) => Ok(read),
            // The peer closed its sending side: a clean end of stream.
            Ok(None) => Ok(0),
            Err(_) => Err(std::io::Error::new(
                std::io::ErrorKind::ConnectionReset,
                "the QUIC stream ended",
            )),
        }
    }
}

/// The sending half of one TBEP channel's QUIC stream.
struct QuicWriter {
    handle: Handle,
    send: quinn::SendStream,
}

impl std::io::Write for QuicWriter {
    fn write(&mut self, buf: &[u8]) -> std::io::Result<usize> {
        self.handle.block_on(self.send.write_all(buf)).map(|()| buf.len()).map_err(|_| {
            std::io::Error::new(std::io::ErrorKind::ConnectionReset, "the QUIC write failed")
        })
    }

    fn flush(&mut self) -> std::io::Result<()> {
        Ok(())
    }
}

/// The TBEP QUIC+TLS server.
pub struct TbepQuicServer;

impl TbepQuicServer {
    /// Binds the QUIC listener and serves the host over TLS 1.3.
    ///
    /// The leaf-first certificate chain and key are the engine process's
    /// own. `client_trust_anchors` is the operator-provisioned gateway CA
    /// bundle; an empty or invalid bundle fails before the socket binds.
    ///
    /// # Errors
    ///
    /// Returns [`QuicBindError::Tls`], [`QuicBindError::Bind`], or
    /// [`QuicBindError::Runtime`] for the matching failure.
    pub fn bind(
        address: SocketAddr,
        certificate_chain: Vec<CertificateDer<'static>>,
        key: PrivateKeyDer<'static>,
        client_trust_anchors: &[CertificateDer<'static>],
        host: Arc<dyn TbepHost>,
    ) -> Result<TbepQuicHandle, QuicBindError> {
        Self::bind_with_inflight_bound(
            address,
            certificate_chain,
            key,
            client_trust_anchors,
            host,
            MAX_INFLIGHT_OPERATIONS,
        )
    }

    /// Binds with an explicit server-wide in-flight operation bound —
    /// the overload gate's capacity, shared with the UDS binding's
    /// default.
    ///
    /// # Errors
    ///
    /// As [`Self::bind`].
    pub fn bind_with_inflight_bound(
        address: SocketAddr,
        certificate_chain: Vec<CertificateDer<'static>>,
        key: PrivateKeyDer<'static>,
        client_trust_anchors: &[CertificateDer<'static>],
        host: Arc<dyn TbepHost>,
        inflight_bound: usize,
    ) -> Result<TbepQuicHandle, QuicBindError> {
        if client_trust_anchors.is_empty() {
            return Err(QuicBindError::Tls);
        }
        let mut client_roots = rustls::RootCertStore::empty();
        for anchor in client_trust_anchors {
            client_roots.add(anchor.clone()).map_err(|_| QuicBindError::Tls)?;
        }
        let client_verifier = rustls::server::WebPkiClientVerifier::builder(Arc::new(client_roots))
            .build()
            .map_err(|_| QuicBindError::Tls)?;
        let mut rustls_config = rustls::ServerConfig::builder()
            .with_client_cert_verifier(client_verifier)
            .with_single_cert(certificate_chain, key)
            .map_err(|_| QuicBindError::Tls)?;
        rustls_config.alpn_protocols = TBEP_ALPN.iter().map(|name| name.to_vec()).collect();
        let server_crypto =
            QuicServerConfig::try_from(rustls_config).map_err(|_| QuicBindError::Tls)?;
        let mut transport = quinn::TransportConfig::default();
        transport.max_concurrent_bidi_streams(1_u32.into());
        transport.max_concurrent_uni_streams(0_u32.into());
        let mut server_config = quinn::ServerConfig::with_crypto(Arc::new(server_crypto));
        server_config.transport_config(Arc::new(transport));
        // quinn's endpoint registers its socket with the ambient async
        // runtime, so the runtime exists — and its context is entered —
        // before the listener binds.
        let runtime = tokio::runtime::Builder::new_multi_thread()
            .worker_threads(2)
            .enable_all()
            .build()
            .map_err(|_| QuicBindError::Runtime)?;
        let _context = runtime.enter();
        let endpoint =
            quinn::Endpoint::server(server_config, address).map_err(|_| QuicBindError::Bind)?;
        let serving_handle = runtime.handle().clone();
        let inflight = Arc::new(AtomicUsize::new(0));
        let tenant_gates = Arc::new(crate::server::TenantGates::new(
            crate::server::MAX_TENANT_INFLIGHT_OPERATIONS,
        ));
        let connections = Arc::new(Semaphore::new(MAX_QUIC_CONNECTIONS));
        // The endpoint is a cloneable handle: the accept loop takes one,
        // the shutdown handle keeps the other.
        let accept_endpoint = endpoint.clone();
        let serving = std::thread::Builder::new()
            .name("tbep-quic-accept".into())
            .spawn(move || {
                runtime.block_on(async move {
                    while let Some(incoming) = accept_endpoint.accept().await {
                        let Ok(connection_permit) = Arc::clone(&connections).try_acquire_owned()
                        else {
                            incoming.refuse();
                            continue;
                        };
                        let host = Arc::clone(&host);
                        let inflight = Arc::clone(&inflight);
                        let tenant_gates = Arc::clone(&tenant_gates);
                        // Handshake and stream-open waits run independently. An
                        // authenticated client that never opens its channel can
                        // therefore consume one bounded permit, but cannot stop
                        // the accept loop or later clients.
                        serving_handle.spawn(async move {
                            let Ok(connection) = incoming.await else { return };
                            // One bidirectional stream per channel: the same
                            // sequential frame discipline the UDS binding serves.
                            // quinn hands the pair back as (send, recv).
                            let Ok((send, recv)) = connection.accept_bi().await else { return };
                            let handle = Handle::current();
                            let _ = tokio::task::spawn_blocking(move || {
                                let _connection_permit = connection_permit;
                                let reader = QuicReader { handle: handle.clone(), recv };
                                let writer = QuicWriter { handle, send };
                                serve_flatbuffers_io(
                                    reader,
                                    writer,
                                    &host,
                                    &inflight,
                                    inflight_bound,
                                    &tenant_gates,
                                );
                            })
                            .await;
                        });
                    }
                });
            })
            .map_err(|_| QuicBindError::Runtime)?;
        Ok(TbepQuicHandle { endpoint, runtime: Some(serving) })
    }
}

/// Decodes the first PEM `CERTIFICATE` block into its DER certificate.
///
/// # Errors
///
/// Returns `None` when the block is absent or its base64 is invalid.
#[must_use]
pub fn decode_pem_certificate(pem: &[u8]) -> Option<CertificateDer<'static>> {
    decode_pem_certificates(pem)?.into_iter().next()
}

/// Decodes every PEM `CERTIFICATE` block in order.
///
/// # Errors
///
/// Returns `None` when the bundle is empty or any certificate block has
/// invalid base64. The first certificate is the leaf when this bundle is
/// passed to [`TbepQuicServer::bind`].
#[must_use]
pub fn decode_pem_certificates(pem: &[u8]) -> Option<Vec<CertificateDer<'static>>> {
    let text = std::str::from_utf8(pem).ok()?;
    let begin = "-----BEGIN CERTIFICATE-----";
    let end = "-----END CERTIFICATE-----";
    let mut rest = text;
    let mut certificates = Vec::new();
    while let Some(start) = rest.find(begin) {
        let body_start = start + begin.len();
        let body_end = rest[body_start..].find(end)? + body_start;
        let base64: String = rest[body_start..body_end]
            .chars()
            .filter(|character| !character.is_whitespace())
            .collect();
        certificates.push(CertificateDer::from(base64_decode(&base64)?));
        rest = &rest[body_end + end.len()..];
    }
    (!certificates.is_empty()).then_some(certificates)
}

/// Decodes the first PEM `PRIVATE KEY` block (PKCS#8) into its DER key.
///
/// # Errors
///
/// Returns `None` when the block is absent or its base64 is invalid.
#[must_use]
pub fn decode_pem_key(pem: &[u8]) -> Option<PrivateKeyDer<'static>> {
    let der = pem_block(pem, "PRIVATE KEY")?;
    Some(PrivateKeyDer::Pkcs8(PrivatePkcs8KeyDer::from(der)))
}

/// Extracts and base64-decodes the first `-----BEGIN <label>-----`
/// block.
fn pem_block(pem: &[u8], label: &str) -> Option<Vec<u8>> {
    let text = std::str::from_utf8(pem).ok()?;
    let begin = format!("-----BEGIN {label}-----");
    let end = format!("-----END {label}-----");
    let start = text.find(&begin)? + begin.len();
    let stop = text[start..].find(&end)? + start;
    let base64: String = text[start..stop].chars().filter(|c| !c.is_whitespace()).collect();
    base64_decode(&base64)
}

/// Minimal standard base64 decoding (the PEM closure needs no
/// dependency): the alphabet, the padding, nothing else.
fn base64_decode(text: &str) -> Option<Vec<u8>> {
    fn value(byte: u8) -> Option<u32> {
        match byte {
            b'A'..=b'Z' => Some(u32::from(byte - b'A')),
            b'a'..=b'z' => Some(u32::from(byte - b'a') + 26),
            b'0'..=b'9' => Some(u32::from(byte - b'0') + 52),
            b'+' => Some(62),
            b'/' => Some(63),
            _ => None,
        }
    }
    let bytes: Vec<u8> = text.bytes().filter(|byte| *byte != b'=').collect();
    if bytes.len() % 4 == 1 {
        return None;
    }
    let mut out = Vec::with_capacity(bytes.len() * 3 / 4);
    for chunk in bytes.chunks(4) {
        let mut acc = 0u32;
        for (index, byte) in chunk.iter().enumerate() {
            acc |= value(*byte)? << (18 - 6 * index);
        }
        let produced = chunk.len() - 1;
        for index in 0..produced {
            out.push(((acc >> (16 - 8 * index)) & 0xff) as u8);
        }
    }
    Some(out)
}

#[cfg(test)]
mod tests {
    use super::{base64_decode, decode_pem_certificate, decode_pem_certificates};

    #[test]
    fn the_pem_base64_closure_decodes_the_standard_alphabet() {
        assert_eq!(base64_decode(""), Some(Vec::new()));
        assert_eq!(base64_decode("Zg=="), Some(vec![b'f']));
        assert_eq!(base64_decode("Zm8="), Some(vec![b'f', b'o']));
        assert_eq!(base64_decode("Zm9v"), Some(vec![b'f', b'o', b'o']));
        assert_eq!(base64_decode("Zm9vYg=="), Some(vec![b'f', b'o', b'o', b'b']));
        assert_eq!(base64_decode("!!!!"), None);
        assert_eq!(base64_decode("Zm9vY"), None, "an impossible tail length refuses");
    }

    #[test]
    fn certificate_bundles_preserve_leaf_to_root_order_and_fail_closed() {
        let bundle = b"-----BEGIN CERTIFICATE-----\nZg==\n-----END CERTIFICATE-----\n\
                       -----BEGIN CERTIFICATE-----\nZm8=\n-----END CERTIFICATE-----\n";
        let certificates = decode_pem_certificates(bundle).expect("the bundle decodes");
        assert_eq!(certificates.len(), 2);
        assert_eq!(certificates[0].as_ref(), b"f");
        assert_eq!(certificates[1].as_ref(), b"fo");
        assert_eq!(decode_pem_certificate(bundle).expect("the first certificate").as_ref(), b"f");
        assert!(decode_pem_certificates(b"").is_none());
        assert!(
            decode_pem_certificates(
                b"-----BEGIN CERTIFICATE-----\n!!!!\n-----END CERTIFICATE-----"
            )
            .is_none()
        );
    }
}
