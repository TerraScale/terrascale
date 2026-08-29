//! The TBEP QUIC+TLS network binding (P4): the identical frame layer
//! the UDS binding serves, over an authenticated TLS 1.3 transport.
//!
//! The contract records QUIC+TLS as the supported network binding: the
//! engine process owns its certificate, clients verify it, and the
//! same bounded, versioned, typed `MessagePack` frames ride one
//! bidirectional QUIC stream per channel. The serving loop is the
//! transport-generic [`crate::server`] core — channel binding, the
//! overload gate, the slow-consumer policy, and every typed operation
//! behave exactly as over the local socket.
//!
//! Honest limits of this landing: client identity is the server's
//! certificate verification plus the channel's tenant binding — the
//! short-lived tenant-scoped client tokens and the rotated mTLS member
//! identities land with the P5 identity machinery; 0-RTT is not
//! enabled for any traffic (the contract forbids it for mutations, and
//! no replay-safe read path is qualified).

use std::net::SocketAddr;
use std::sync::Arc;
use std::sync::atomic::AtomicUsize;

use quinn::crypto::rustls::QuicServerConfig;
use quinn::rustls::pki_types::{CertificateDer, PrivateKeyDer, PrivatePkcs8KeyDer};
use tokio::runtime::Handle;

use crate::host::TbepHost;
use crate::server::{MAX_INFLIGHT_OPERATIONS, serve_io};

/// The binding's ALPN identity: every TBEP QUIC endpoint offers and
/// requires exactly this protocol name.
pub const TBEP_ALPN: &[&[u8]] = &[b"terrabase.engine.protocol/1"];

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
    /// The certificate and key are the engine process's own — the
    /// operator provisions them; no credential crosses the wire.
    ///
    /// # Errors
    ///
    /// Returns [`QuicBindError::Tls`], [`QuicBindError::Bind`], or
    /// [`QuicBindError::Runtime`] for the matching failure.
    pub fn bind(
        address: SocketAddr,
        certificate: CertificateDer<'static>,
        key: PrivateKeyDer<'static>,
        host: Arc<dyn TbepHost>,
    ) -> Result<TbepQuicHandle, QuicBindError> {
        Self::bind_with_inflight_bound(address, certificate, key, host, MAX_INFLIGHT_OPERATIONS)
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
        certificate: CertificateDer<'static>,
        key: PrivateKeyDer<'static>,
        host: Arc<dyn TbepHost>,
        inflight_bound: usize,
    ) -> Result<TbepQuicHandle, QuicBindError> {
        let mut rustls_config = rustls::ServerConfig::builder()
            .with_no_client_auth()
            .with_single_cert(vec![certificate], key)
            .map_err(|_| QuicBindError::Tls)?;
        rustls_config.alpn_protocols = TBEP_ALPN.iter().map(|name| name.to_vec()).collect();
        let server_config =
            QuicServerConfig::try_from(rustls_config).map_err(|_| QuicBindError::Tls)?;
        // quinn's endpoint registers its socket with the ambient async
        // runtime, so the runtime exists — and its context is entered —
        // before the listener binds.
        let runtime = tokio::runtime::Builder::new_multi_thread()
            .worker_threads(2)
            .enable_all()
            .build()
            .map_err(|_| QuicBindError::Runtime)?;
        let _context = runtime.enter();
        let endpoint = quinn::Endpoint::server(
            quinn::ServerConfig::with_crypto(Arc::new(server_config)),
            address,
        )
        .map_err(|_| QuicBindError::Bind)?;
        let serving_handle = runtime.handle().clone();
        let inflight = Arc::new(AtomicUsize::new(0));
        // The endpoint is a cloneable handle: the accept loop takes one,
        // the shutdown handle keeps the other.
        let accept_endpoint = endpoint.clone();
        let serving = std::thread::Builder::new()
            .name("tbep-quic-accept".into())
            .spawn(move || {
                runtime.block_on(async move {
                    while let Some(incoming) = accept_endpoint.accept().await {
                        let Ok(connection) = incoming.await else { continue };
                        // One bidirectional stream per channel — the
                        // same sequential frame discipline the UDS
                        // binding serves.
                        // quinn hands the pair back as (send, recv).
                        let Ok((send, recv)) = connection.accept_bi().await else { continue };
                        let host = Arc::clone(&host);
                        let inflight = Arc::clone(&inflight);
                        let handle = serving_handle.clone();
                        let _ = std::thread::Builder::new()
                            .name("tbep-quic-connection".into())
                            .spawn(move || {
                                let reader = QuicReader { handle: handle.clone(), recv };
                                let writer = QuicWriter { handle, send };
                                serve_io(reader, writer, host, inflight, inflight_bound);
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
    let der = pem_block(pem, "CERTIFICATE")?;
    Some(CertificateDer::from(der))
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
    use super::base64_decode;

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
}
