use thiserror::Error;
use tonic::Request;
use tonic::transport::{Certificate, CertificateDer, ClientTlsConfig, Identity, ServerTlsConfig};

use crate::v1;

/// Application seam that maps an authenticated client certificate to its
/// claimed stable `TerraBase` member identity and authorization scope.
pub trait PeerIdentityValidator: Send + Sync {
    /// Return true only when the full certificate chain identifies this member.
    fn validates_peer(
        &self,
        claimed_peer: &v1::PeerIdentity,
        certificate_chain: &[CertificateDer<'static>],
    ) -> bool;
}

/// mTLS certificate availability or identity mapping failure.
#[derive(Clone, Copy, Debug, Eq, Error, PartialEq)]
pub enum PeerValidationError {
    /// The server request did not arrive over a TLS connection with a client cert.
    #[error("a mutually authenticated peer certificate is required")]
    MissingCertificate,
    /// The certificate validator rejected the claimed member identity.
    #[error("peer certificate does not authorize the claimed member identity")]
    IdentityRejected,
    /// Trust roots, endpoint name, certificate, or key are missing.
    #[error("explicit mTLS trust roots, endpoint name, certificate, and key are required")]
    MissingTlsMaterial,
}

/// Builds client mTLS settings with explicit trust roots and a peer identity.
///
/// The function never loads operating-system roots. The caller should create a
/// new channel after certificate rotation so existing streams can drain cleanly.
///
/// # Errors
///
/// Returns [`PeerValidationError::MissingTlsMaterial`] when the name, roots,
/// client certificate, or private key is empty.
pub fn client_mtls_config(
    server_name: &str,
    trust_roots_pem: &[u8],
    client_certificate_pem: &[u8],
    client_private_key_pem: &[u8],
) -> Result<ClientTlsConfig, PeerValidationError> {
    if server_name.is_empty()
        || trust_roots_pem.is_empty()
        || client_certificate_pem.is_empty()
        || client_private_key_pem.is_empty()
    {
        return Err(PeerValidationError::MissingTlsMaterial);
    }
    Ok(ClientTlsConfig::new()
        .domain_name(server_name.to_owned())
        .ca_certificate(Certificate::from_pem(trust_roots_pem))
        .identity(Identity::from_pem(
            client_certificate_pem,
            client_private_key_pem,
        )))
}

/// Builds server mTLS settings with explicit client trust roots.
///
/// Tonic requires a verified client certificate because optional client auth is
/// left disabled. The service must still map that certificate to membership and
/// tenant/database authorization through [`PeerIdentityValidator`].
///
/// # Errors
///
/// Returns [`PeerValidationError::MissingTlsMaterial`] when server identity or
/// client trust roots are empty.
pub fn server_mtls_config(
    server_certificate_pem: &[u8],
    server_private_key_pem: &[u8],
    client_trust_roots_pem: &[u8],
) -> Result<ServerTlsConfig, PeerValidationError> {
    if server_certificate_pem.is_empty()
        || server_private_key_pem.is_empty()
        || client_trust_roots_pem.is_empty()
    {
        return Err(PeerValidationError::MissingTlsMaterial);
    }
    Ok(ServerTlsConfig::new()
        .identity(Identity::from_pem(
            server_certificate_pem,
            server_private_key_pem,
        ))
        .client_ca_root(Certificate::from_pem(client_trust_roots_pem)))
}

/// Requires a verified client certificate and invokes the product identity seam.
///
/// # Errors
///
/// Returns [`PeerValidationError::MissingCertificate`] when the request lacks a
/// peer certificate, or [`PeerValidationError::IdentityRejected`] when the
/// validator rejects the claimed identity.
pub fn validate_mtls_peer<T, V>(
    request: &Request<T>,
    claimed_peer: &v1::PeerIdentity,
    validator: &V,
) -> Result<(), PeerValidationError>
where
    V: PeerIdentityValidator + ?Sized,
{
    let certificates = request
        .peer_certs()
        .ok_or(PeerValidationError::MissingCertificate)?;
    if certificates.is_empty() || !validator.validates_peer(claimed_peer, certificates.as_slice()) {
        return Err(PeerValidationError::IdentityRejected);
    }
    Ok(())
}
