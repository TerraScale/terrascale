//! Signed two-member outbound TLS binding for the future replication sender.

use std::sync::Arc;
use std::time::Duration;

use rustls::client::WebPkiServerVerifier;
use rustls::client::danger::{HandshakeSignatureValid, ServerCertVerified, ServerCertVerifier};
use rustls::pki_types::{CertificateDer, ServerName, UnixTime, pem::PemObject};
use rustls::{
    DigitallySignedStruct, DistinguishedName, RootCertStore, SignatureScheme, crypto::aws_lc_rs,
};
use sha2::{Digest, Sha256};
use subtle::ConstantTimeEq;
use tb_core::OriginId;
use tb_replication::{AuthorityVersion, CertifiedShardView};
use tonic::transport::{ClientTlsConfig, Endpoint, Identity};

use crate::ReplicationOutboundPeer;

const MAX_OUTBOUND_CA_BYTES: usize = 2 * 1024 * 1024;
const MAX_OUTBOUND_CA_CERTIFICATES: usize = 32;
const TLS_HANDSHAKE_TIMEOUT: Duration = Duration::from_secs(10);

/// One endpoint bound to the sole other signed member, independent of the
/// operational IP and port supplied by the local process configuration.
#[derive(Clone)]
pub struct ReplicationOutboundTls {
    peer_origin: OriginId,
    endpoint: Endpoint,
}

impl ReplicationOutboundTls {
    /// Construct a pinned mTLS endpoint after the authority has been verified.
    /// The PEM roots are explicit outbound server roots, separate from the
    /// receiver's inbound client CA roots.
    ///
    /// # Errors
    /// Rejects a non-v2 or non-two-member authority, a foreign peer mapping,
    /// invalid roots or identity, or a Tonic TLS configuration failure.
    pub fn from_signed_view(
        view: &CertifiedShardView,
        peer: &ReplicationOutboundPeer,
        outbound_ca_pem: &[u8],
        local_certificate_pem: &[u8],
        local_private_key_pem: &[u8],
    ) -> Result<Self, ReplicationOutboundTlsError> {
        if view.version() != AuthorityVersion::V2 || view.members().len() != 2 {
            return Err(ReplicationOutboundTlsError::SignedTwoMemberAuthorityRequired);
        }
        if local_certificate_pem.is_empty()
            || local_certificate_pem.len() > MAX_OUTBOUND_CA_BYTES
            || local_private_key_pem.is_empty()
            || local_private_key_pem.len() > MAX_OUTBOUND_CA_BYTES
        {
            return Err(ReplicationOutboundTlsError::TlsMaterial(
                "local outbound identity is empty or exceeds its two MiB bound".into(),
            ));
        }
        let other = view
            .members()
            .find(|member| member.origin != view.local_origin())
            .ok_or(ReplicationOutboundTlsError::SignedTwoMemberAuthorityRequired)?;
        if peer.member() != other.origin.replica_id {
            return Err(ReplicationOutboundTlsError::PeerIsNotSoleSignedRemote);
        }
        let local_leaf = CertificateDer::pem_slice_iter(local_certificate_pem)
            .next()
            .transpose()
            .map_err(|error| ReplicationOutboundTlsError::TlsMaterial(error.to_string()))?
            .ok_or_else(|| {
                ReplicationOutboundTlsError::TlsMaterial(
                    "local client identity has no PEM leaf certificate".into(),
                )
            })?;
        let local_hash: [u8; 32] = Sha256::digest(local_leaf.as_ref()).into();
        let signed_local = view
            .member_leaf_sha256(view.local_origin().replica_id)
            .ok_or(ReplicationOutboundTlsError::SignedTwoMemberAuthorityRequired)?;
        if local_hash.ct_eq(&signed_local).unwrap_u8() != 1 {
            return Err(ReplicationOutboundTlsError::LocalLeafNotSigned);
        }

        let verifier = Arc::new(SignedPeerVerifier {
            webpki: outbound_webpki_verifier(outbound_ca_pem)?,
            expected_leaf: other.leaf_sha256,
        });
        let endpoint = Endpoint::from_shared(format!("https://{}", peer.addr()))?
            .connect_timeout(TLS_HANDSHAKE_TIMEOUT)
            .tls_config_with_verifier(
                ClientTlsConfig::new()
                    .domain_name(other.server_name.to_owned())
                    .identity(Identity::from_pem(
                        local_certificate_pem,
                        local_private_key_pem,
                    ))
                    .timeout(TLS_HANDSHAKE_TIMEOUT),
                verifier,
            )?;
        Ok(Self {
            peer_origin: other.origin,
            endpoint,
        })
    }

    /// Exact signed remote origin to use in the outbound Hello.
    #[must_use]
    pub const fn peer_origin(&self) -> OriginId {
        self.peer_origin
    }

    /// Clone the pinned endpoint for one bounded connection attempt.
    #[must_use]
    pub fn endpoint(&self) -> Endpoint {
        self.endpoint.clone()
    }
}

/// Outbound authority, PEM, or transport-binding failure.
#[derive(Debug, thiserror::Error)]
pub enum ReplicationOutboundTlsError {
    /// The fixed-peer stage supports only a signed two-member v2 shard.
    #[error("outbound replication requires a signed-v2 authority with exactly two members")]
    SignedTwoMemberAuthorityRequired,
    /// The configured member must be the sole signed remote.
    #[error("configured outbound peer is not the sole other signed member")]
    PeerIsNotSoleSignedRemote,
    /// The local client certificate must be this authority's exact local leaf.
    #[error("local outbound TLS certificate differs from the signed member leaf")]
    LocalLeafNotSigned,
    /// Outbound CA or identity material was missing, malformed, or unbounded.
    #[error("invalid outbound replication TLS material: {0}")]
    TlsMaterial(String),
    /// Tonic could not configure the authenticated endpoint.
    #[error(transparent)]
    Transport(#[from] tonic::transport::Error),
}

fn outbound_webpki_verifier(
    ca_pem: &[u8],
) -> Result<Arc<dyn ServerCertVerifier>, ReplicationOutboundTlsError> {
    if ca_pem.is_empty() || ca_pem.len() > MAX_OUTBOUND_CA_BYTES {
        return Err(ReplicationOutboundTlsError::TlsMaterial(
            "outbound server CA file is empty or exceeds its two MiB bound".into(),
        ));
    }
    let mut roots = RootCertStore::empty();
    for certificate in CertificateDer::pem_slice_iter(ca_pem) {
        if roots.len() == MAX_OUTBOUND_CA_CERTIFICATES {
            return Err(ReplicationOutboundTlsError::TlsMaterial(
                "outbound server CA file has too many certificates".into(),
            ));
        }
        let certificate = certificate
            .map_err(|error| ReplicationOutboundTlsError::TlsMaterial(error.to_string()))?;
        roots
            .add(certificate)
            .map_err(|error| ReplicationOutboundTlsError::TlsMaterial(error.to_string()))?;
    }
    if roots.is_empty() {
        return Err(ReplicationOutboundTlsError::TlsMaterial(
            "outbound server CA file has no valid certificate".into(),
        ));
    }
    let webpki = WebPkiServerVerifier::builder_with_provider(
        Arc::new(roots),
        Arc::new(aws_lc_rs::default_provider()),
    )
    .build()
    .map_err(|error| ReplicationOutboundTlsError::TlsMaterial(error.to_string()))?;
    Ok(webpki)
}

#[derive(Debug)]
struct SignedPeerVerifier {
    webpki: Arc<dyn ServerCertVerifier>,
    expected_leaf: [u8; 32],
}

impl ServerCertVerifier for SignedPeerVerifier {
    fn verify_server_cert(
        &self,
        end_entity: &CertificateDer<'_>,
        intermediates: &[CertificateDer<'_>],
        server_name: &ServerName<'_>,
        ocsp_response: &[u8],
        now: UnixTime,
    ) -> Result<ServerCertVerified, rustls::Error> {
        let verified = self.webpki.verify_server_cert(
            end_entity,
            intermediates,
            server_name,
            ocsp_response,
            now,
        )?;
        let observed: [u8; 32] = Sha256::digest(end_entity.as_ref()).into();
        if observed.ct_eq(&self.expected_leaf).unwrap_u8() != 1 {
            return Err(rustls::Error::General(
                "TLS server leaf differs from signed replication peer".into(),
            ));
        }
        Ok(verified)
    }

    fn verify_tls12_signature(
        &self,
        message: &[u8],
        cert: &CertificateDer<'_>,
        dss: &DigitallySignedStruct,
    ) -> Result<HandshakeSignatureValid, rustls::Error> {
        self.webpki.verify_tls12_signature(message, cert, dss)
    }

    fn verify_tls13_signature(
        &self,
        message: &[u8],
        cert: &CertificateDer<'_>,
        dss: &DigitallySignedStruct,
    ) -> Result<HandshakeSignatureValid, rustls::Error> {
        self.webpki.verify_tls13_signature(message, cert, dss)
    }

    fn supported_verify_schemes(&self) -> Vec<SignatureScheme> {
        self.webpki.supported_verify_schemes()
    }

    fn requires_raw_public_keys(&self) -> bool {
        self.webpki.requires_raw_public_keys()
    }

    fn root_hint_subjects(&self) -> Option<&[DistinguishedName]> {
        self.webpki.root_hint_subjects()
    }
}
