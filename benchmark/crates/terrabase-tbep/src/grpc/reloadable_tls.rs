//! Live TBEP mTLS credentials and bounded authenticated connection lifetimes.
use std::{
    io,
    pin::Pin,
    sync::{
        Arc, RwLock,
        atomic::{AtomicU64, Ordering},
    },
    task::{Context, Poll},
    time::{Duration, SystemTime, UNIX_EPOCH},
};
use tokio::io::{AsyncRead, AsyncWrite, ReadBuf};
use tokio_rustls::{TlsAcceptor, server::TlsStream};
use tonic::transport::server::Connected;

/// Validated replacement TLS identity and client trust policy.
#[derive(Clone)]
pub struct PreparedTls {
    config: Arc<tokio_rustls::rustls::ServerConfig>,
    expires_at: u64,
    names: Vec<String>,
}
impl PreparedTls {
    /// Effective credential deadline, bounded by the leaf certificate expiry.
    #[must_use]
    pub fn valid_until(&self) -> u64 {
        self.expires_at
    }
}
/// Atomic active TLS policy shared by the actual TBEP listeners.
#[derive(Clone)]
pub struct ReloadableTls {
    state: Arc<RwLock<Option<PreparedTls>>>,
    epoch: Arc<AtomicU64>,
    names: Arc<Vec<String>>,
}
impl ReloadableTls {
    /// Validates initial certificate, matching private key, and mandatory client CA.
    ///
    /// # Errors
    /// Returns invalid or expired TLS material.
    pub fn new(certificate: &str, key: &str, trust: &str) -> io::Result<Self> {
        let prepared = Self::prepare(certificate, key, trust, u64::MAX)?;
        Ok(Self {
            names: Arc::new(prepared.names.clone()),
            state: Arc::new(RwLock::new(Some(prepared))),
            epoch: Arc::new(AtomicU64::new(0)),
        })
    }
    /// Validates staged TLS 1.3 mutual authentication material without activation.
    ///
    /// # Errors
    /// Rejects malformed material, mismatched keys, empty trust, or elapsed expiry.
    pub fn prepare(
        certificate: &str,
        key: &str,
        trust: &str,
        expires_at: u64,
    ) -> io::Result<PreparedTls> {
        if expires_at <= now() {
            return Err(io::Error::other("expired TLS material"));
        }
        use rustls_pki_types::{CertificateDer, pem::PemObject};
        let der = CertificateDer::pem_slice_iter(certificate.as_bytes())
            .next()
            .ok_or_else(|| io::Error::other("missing TLS certificate"))?
            .map_err(io::Error::other)?;
        let chain = CertificateDer::pem_slice_iter(certificate.as_bytes())
            .collect::<Result<Vec<_>, _>>()
            .map_err(io::Error::other)?;
        let roots = CertificateDer::pem_slice_iter(trust.as_bytes())
            .collect::<Result<Vec<_>, _>>()
            .map_err(io::Error::other)?;
        let anchors = roots
            .iter()
            .map(webpki::anchor_from_trusted_cert)
            .collect::<Result<Vec<_>, _>>()
            .map_err(io::Error::other)?;
        webpki::EndEntityCert::try_from(&der)
            .map_err(io::Error::other)?
            .verify_for_usage(
                tokio_rustls::rustls::crypto::ring::default_provider()
                    .signature_verification_algorithms
                    .all,
                &anchors,
                &chain[1..],
                rustls_pki_types::UnixTime::now(),
                webpki::KeyUsage::server_auth(),
                None,
                None,
            )
            .map_err(io::Error::other)?;
        let (_, parsed) = x509_parser::parse_x509_certificate(&der)
            .map_err(|_| io::Error::other("invalid TLS certificate"))?;
        if !parsed.validity().is_valid() {
            return Err(io::Error::other("TLS certificate outside validity interval"));
        }
        let cert_expiry =
            u64::try_from(parsed.validity().not_after.timestamp()).map_err(io::Error::other)?;
        let san = parsed
            .subject_alternative_name()
            .map_err(|_| io::Error::other("invalid TLS identity"))?
            .ok_or_else(|| io::Error::other("TLS identity requires SAN"))?;
        let mut names =
            san.value.general_names.iter().map(|name| format!("{name:?}")).collect::<Vec<_>>();
        names.sort();
        Ok(PreparedTls {
            config: super::tls_server_config(certificate, key, trust)?,
            expires_at: expires_at.min(cert_expiry),
            names,
        })
    }
    /// Checks staged credentials against the configured listener identity boundary.
    ///
    /// # Errors
    /// Rejects expiry or changed subject alternative names.
    pub fn validate(&self, prepared: &PreparedTls) -> io::Result<()> {
        if prepared.expires_at <= now() || &prepared.names != self.names.as_ref() {
            return Err(io::Error::other("TLS identity or validity outside its boundary"));
        }
        Ok(())
    }
    /// Publishes a validated snapshot and invalidates existing authenticated sessions.
    ///
    /// # Errors
    /// Returns poisoned state or expired credentials.
    pub fn install(&self, prepared: PreparedTls) -> io::Result<()> {
        self.validate(&prepared)?;
        *self.state.write().map_err(|_| io::Error::other("TLS state unavailable"))? =
            Some(prepared);
        self.epoch.fetch_add(1, Ordering::AcqRel);
        Ok(())
    }
    /// Stops new handshakes and invalidates existing sessions.
    pub fn fence(&self) {
        if let Ok(mut state) = self.state.write() {
            *state = None;
        }
        self.epoch.fetch_add(1, Ordering::AcqRel);
    }
    pub(super) async fn accept(&self, stream: tokio::net::TcpStream) -> io::Result<RotatingStream> {
        let (prepared, epoch) = {
            let guard = self.state.read().map_err(|_| io::Error::other("TLS state unavailable"))?;
            (
                guard.clone().ok_or_else(|| io::Error::other("TLS credentials fenced"))?,
                self.epoch.load(Ordering::Acquire),
            )
        };
        if prepared.expires_at <= now() {
            return Err(io::Error::other("TLS credentials expired"));
        }
        let stream = TlsAcceptor::from(prepared.config).accept(stream).await?;
        if epoch != self.epoch.load(Ordering::Acquire) {
            return Err(io::Error::other("TLS credentials changed during handshake"));
        }
        Ok(RotatingStream {
            stream,
            epoch,
            active_epoch: self.epoch.clone(),
            deadline: Box::pin(tokio::time::sleep(Duration::from_secs(
                prepared.expires_at.saturating_sub(now()).min(60),
            ))),
        })
    }
}
fn now() -> u64 {
    SystemTime::now().duration_since(UNIX_EPOCH).map_or(u64::MAX, |v| v.as_secs())
}
/// A TLS stream that reconnects within sixty seconds or immediately on subsequent I/O after rotation.
pub(super) struct RotatingStream {
    stream: TlsStream<tokio::net::TcpStream>,
    epoch: u64,
    active_epoch: Arc<AtomicU64>,
    deadline: Pin<Box<tokio::time::Sleep>>,
}
impl RotatingStream {
    fn check(&mut self, cx: &mut Context<'_>) -> io::Result<()> {
        use std::future::Future;
        if self.epoch != self.active_epoch.load(Ordering::Acquire)
            || self.deadline.as_mut().poll(cx).is_ready()
        {
            Err(io::Error::new(
                io::ErrorKind::ConnectionAborted,
                "TLS session requires authenticated reconnect",
            ))
        } else {
            Ok(())
        }
    }
}
impl Connected for RotatingStream {
    type ConnectInfo = <TlsStream<tokio::net::TcpStream> as Connected>::ConnectInfo;
    fn connect_info(&self) -> Self::ConnectInfo {
        self.stream.connect_info()
    }
}
impl AsyncRead for RotatingStream {
    fn poll_read(
        mut self: Pin<&mut Self>,
        cx: &mut Context<'_>,
        buf: &mut ReadBuf<'_>,
    ) -> Poll<io::Result<()>> {
        if let Err(e) = self.check(cx) {
            return Poll::Ready(Err(e));
        }
        Pin::new(&mut self.stream).poll_read(cx, buf)
    }
}
impl AsyncWrite for RotatingStream {
    fn poll_write(
        mut self: Pin<&mut Self>,
        cx: &mut Context<'_>,
        buf: &[u8],
    ) -> Poll<io::Result<usize>> {
        if let Err(e) = self.check(cx) {
            return Poll::Ready(Err(e));
        }
        Pin::new(&mut self.stream).poll_write(cx, buf)
    }
    fn poll_flush(mut self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<io::Result<()>> {
        if let Err(e) = self.check(cx) {
            return Poll::Ready(Err(e));
        }
        Pin::new(&mut self.stream).poll_flush(cx)
    }
    fn poll_shutdown(mut self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<io::Result<()>> {
        Pin::new(&mut self.stream).poll_shutdown(cx)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use rustls_pki_types::{CertificateDer, PrivateKeyDer, ServerName, pem::PemObject};
    use tokio_rustls::{TlsConnector, rustls};
    fn material() -> (String, String, String, String, String, Vec<u8>) {
        use rcgen::{BasicConstraints, CertificateParams, ExtendedKeyUsagePurpose, IsCa, KeyPair};
        let ca_key = KeyPair::generate().unwrap();
        let mut params = CertificateParams::new(Vec::new()).unwrap();
        params.is_ca = IsCa::Ca(BasicConstraints::Unconstrained);
        let ca = params.self_signed(&ca_key).unwrap();
        let key = KeyPair::generate().unwrap();
        let mut params = CertificateParams::new(vec!["localhost".into()]).unwrap();
        params.extended_key_usages = vec![ExtendedKeyUsagePurpose::ServerAuth];
        let server = params.signed_by(&key, &ca, &ca_key).unwrap();
        let client_key = KeyPair::generate().unwrap();
        let mut params = CertificateParams::new(vec!["client".into()]).unwrap();
        params.extended_key_usages = vec![ExtendedKeyUsagePurpose::ClientAuth];
        let client = params.signed_by(&client_key, &ca, &ca_key).unwrap();
        (
            ca.pem(),
            server.pem(),
            key.serialize_pem(),
            client.pem(),
            client_key.serialize_pem(),
            server.der().to_vec(),
        )
    }
    fn connector(ca: &str, certificate: &str, key: &str) -> TlsConnector {
        let mut roots = rustls::RootCertStore::empty();
        for cert in CertificateDer::pem_slice_iter(ca.as_bytes()) {
            roots.add(cert.unwrap()).unwrap();
        }
        let mut config = rustls::ClientConfig::builder_with_provider(Arc::new(
            rustls::crypto::ring::default_provider(),
        ))
        .with_protocol_versions(&[&rustls::version::TLS13])
        .unwrap()
        .with_root_certificates(roots)
        .with_client_auth_cert(
            CertificateDer::pem_slice_iter(certificate.as_bytes()).map(Result::unwrap).collect(),
            PrivateKeyDer::from_pem_slice(key.as_bytes()).unwrap(),
        )
        .unwrap();
        config.alpn_protocols = vec![b"h2".to_vec()];
        TlsConnector::from(Arc::new(config))
    }
    #[tokio::test]
    async fn real_listener_reloads_identity_trust_and_fences_old_sessions() {
        use tokio::io::AsyncReadExt;
        let first = material();
        let second = material();
        assert!(ReloadableTls::prepare(&first.1, &first.2, &second.0, now() + 600).is_err());
        assert!(ReloadableTls::prepare(&first.1, &first.2, &first.0, now()).is_err());
        assert!(
            ReloadableTls::prepare(&first.1, &first.2, &first.0, u64::MAX).unwrap().valid_until()
                < u64::MAX
        );
        let tls = ReloadableTls::new(&first.1, &first.2, &first.0).unwrap();
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let address = listener.local_addr().unwrap();
        let c1 = connector(&first.0, &first.3, &first.4);
        let client = async {
            let tcp = tokio::net::TcpStream::connect(address).await.unwrap();
            c1.connect(ServerName::try_from("localhost").unwrap(), tcp).await.unwrap()
        };
        let server = async { tls.accept(listener.accept().await.unwrap().0).await.unwrap() };
        let (client, mut old_server) = tokio::join!(client, server);
        assert_eq!(client.get_ref().1.peer_certificates().unwrap()[0].as_ref(), first.5);
        let next = ReloadableTls::prepare(&second.1, &second.2, &second.0, now() + 600).unwrap();
        tls.install(next).unwrap();
        assert!(old_server.read_u8().await.is_err());
        let c2 = connector(&second.0, &second.3, &second.4);
        let client = async {
            let tcp = tokio::net::TcpStream::connect(address).await.unwrap();
            c2.connect(ServerName::try_from("localhost").unwrap(), tcp).await.unwrap()
        };
        let server = async { tls.accept(listener.accept().await.unwrap().0).await.unwrap() };
        let (client, _server) = tokio::join!(client, server);
        assert_eq!(client.get_ref().1.peer_certificates().unwrap()[0].as_ref(), second.5);
        // Old client identity cannot chain to the newly installed trust policy.
        let c_old = connector(&second.0, &first.3, &first.4);
        let client = async {
            let tcp = tokio::net::TcpStream::connect(address).await.unwrap();
            let _ = c_old.connect(ServerName::try_from("localhost").unwrap(), tcp).await;
        };
        let server = async {
            assert!(tls.accept(listener.accept().await.unwrap().0).await.is_err());
        };
        tokio::join!(client, server);
        tls.fence();
        let tcp = tokio::net::TcpStream::connect(address).await.unwrap();
        assert!(tls.accept(listener.accept().await.unwrap().0).await.is_err());
        drop(tcp);
    }
}
