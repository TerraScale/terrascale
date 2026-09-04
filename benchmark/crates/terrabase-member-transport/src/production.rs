//! Production Member-to-Member QUIC boundary.
//!
//! This module is deliberately separate from [`crate::quic::QuicLane`]. The
//! lane remains a loopback qualification tool. This boundary binds one real
//! endpoint for one Member process, authenticates both sides with TLS 1.3,
//! derives the peer identity from exactly one SPIFFE URI SAN, checks that
//! identity against the admitted membership epoch, and applies explicit
//! connection, stream, frame, queue, and time bounds.
//!
//! The boundary does not implement replication correctness. In particular it
//! does not turn a successful QUIC delivery into a `stored`, `applied`, or
//! `verified` observation. The production cluster runtime must connect the
//! authenticated frames to the durable replication-core and storage state
//! machines before it may report readiness.

use std::collections::{BTreeMap, VecDeque};
use std::net::SocketAddr;
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use ring::rand::{SecureRandom, SystemRandom};
use rustls::pki_types::{CertificateDer, PrivateKeyDer, pem::PemObject};
use tokio::sync::Notify;
use x509_parser::extensions::{GeneralName, ParsedExtension};
use x509_parser::prelude::{FromDer, X509Certificate};

use crate::identity::MemberId;

/// The application protocol negotiated for production Member links.
pub const PRODUCTION_MEMBER_ALPN: &[u8] = b"terrabase-member/1";

/// The current production Member connection protocol major.
pub const PRODUCTION_PROTOCOL_MAJOR: u16 = 1;

/// The largest frame the production boundary will admit.
pub const PRODUCTION_MAX_FRAME_BYTES: usize = 16 * 1024 * 1024;

/// The largest connection hello, including every length prefix.
pub const PRODUCTION_MAX_HELLO_BYTES: usize = 1024;

const HELLO_MAGIC: [u8; 4] = *b"TBM1";
const NONCE_BYTES: usize = 32;
const MAX_CONNECTIONS_PER_PEER: usize = 2;
const CLOSE_DRAINING: u32 = 0x5442_0001;
const CLOSE_AUTHORIZATION: u32 = 0x5442_0002;

/// A Region in the initial production catalog.
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub enum ProductionRegion {
    /// Sao Paulo.
    Gru,
    /// San Jose, exposed only under `TerraScale`'s stable `SFO` code.
    Sfo,
    /// Frankfurt.
    Fra,
}

impl ProductionRegion {
    /// Parses one canonical initial production Region code.
    ///
    /// # Errors
    ///
    /// Returns [`ProductionTransportError::InvalidRegion`] for lowercase,
    /// aliases, provider names, and any code outside `GRU`, `SFO`, and `FRA`.
    pub fn try_new(value: &str) -> Result<Self, ProductionTransportError> {
        match value {
            "GRU" => Ok(Self::Gru),
            "SFO" => Ok(Self::Sfo),
            "FRA" => Ok(Self::Fra),
            _ => Err(ProductionTransportError::InvalidRegion),
        }
    }

    /// Returns the canonical three-byte product code.
    #[must_use]
    pub const fn as_bytes(self) -> [u8; 3] {
        match self {
            Self::Gru => *b"GRU",
            Self::Sfo => *b"SFO",
            Self::Fra => *b"FRA",
        }
    }

    /// Returns the canonical product text.
    #[must_use]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Gru => "GRU",
            Self::Sfo => "SFO",
            Self::Fra => "FRA",
        }
    }
}

/// One certificate-derived Member workload identity.
///
/// The exact URI form is
/// `spiffe://terrascale.tech/<environment>/terrabase-member/<cluster>/<member>`.
/// Every path component is strict unreserved ASCII. A certificate must carry
/// exactly one URI SAN, and that SAN must parse as this type.
#[derive(Clone, Debug, Eq, Ord, PartialEq, PartialOrd)]
pub struct MemberWorkloadIdentity {
    environment: String,
    cluster: String,
    member: MemberId,
    uri: String,
}

impl MemberWorkloadIdentity {
    /// Builds the canonical SPIFFE identity.
    ///
    /// # Errors
    ///
    /// Returns [`ProductionTransportError::InvalidWorkloadIdentity`] when a
    /// component is empty, contains a non-URI-safe byte, or the Member name
    /// breaks the Member identifier bound.
    pub fn try_new(
        environment: &str,
        cluster: &str,
        member: &str,
    ) -> Result<Self, ProductionTransportError> {
        validate_path_segment(environment)?;
        validate_path_segment(cluster)?;
        validate_path_segment(member)?;
        let member = MemberId::try_new(member)
            .map_err(|_| ProductionTransportError::InvalidWorkloadIdentity)?;
        let uri =
            format!("spiffe://terrascale.tech/{environment}/terrabase-member/{cluster}/{member}");
        Ok(Self { environment: environment.to_owned(), cluster: cluster.to_owned(), member, uri })
    }

    /// Parses the exact Member SPIFFE namespace.
    ///
    /// # Errors
    ///
    /// Returns [`ProductionTransportError::InvalidWorkloadIdentity`] for a
    /// different trust domain, role, path length, or component shape.
    pub fn parse(uri: &str) -> Result<Self, ProductionTransportError> {
        let path = uri
            .strip_prefix("spiffe://terrascale.tech/")
            .ok_or(ProductionTransportError::InvalidWorkloadIdentity)?;
        let components = path.split('/').collect::<Vec<_>>();
        if components.len() != 4 || components[1] != "terrabase-member" {
            return Err(ProductionTransportError::InvalidWorkloadIdentity);
        }
        let identity = Self::try_new(components[0], components[2], components[3])?;
        if identity.uri != uri {
            return Err(ProductionTransportError::InvalidWorkloadIdentity);
        }
        Ok(identity)
    }

    /// Derives a Member identity from exactly one URI SAN in an X.509 leaf.
    ///
    /// # Errors
    ///
    /// Returns [`ProductionTransportError::InvalidPeerCertificate`] when the
    /// certificate cannot be parsed, has zero or multiple URI SANs, or its
    /// only URI SAN is not a canonical Member workload identity.
    pub fn from_leaf_certificate(
        certificate: &CertificateDer<'_>,
    ) -> Result<Self, ProductionTransportError> {
        if certificate.len() > 64 * 1024 {
            return Err(ProductionTransportError::InvalidPeerCertificate);
        }
        let (_, certificate) = X509Certificate::from_der(certificate.as_ref())
            .map_err(|_| ProductionTransportError::InvalidPeerCertificate)?;
        let mut uri_sans = Vec::new();
        for extension in certificate.extensions() {
            let ParsedExtension::SubjectAlternativeName(names) = extension.parsed_extension()
            else {
                continue;
            };
            for name in &names.general_names {
                if let GeneralName::URI(uri) = name {
                    uri_sans.push(*uri);
                }
            }
        }
        if uri_sans.len() != 1 {
            return Err(ProductionTransportError::InvalidPeerCertificate);
        }
        Self::parse(uri_sans[0]).map_err(|_| ProductionTransportError::InvalidPeerCertificate)
    }

    /// Returns the `TerraScale` environment segment.
    #[must_use]
    pub fn environment(&self) -> &str {
        &self.environment
    }

    /// Returns the cluster segment.
    #[must_use]
    pub fn cluster(&self) -> &str {
        &self.cluster
    }

    /// Returns the admitted Member identity.
    #[must_use]
    pub fn member(&self) -> &MemberId {
        &self.member
    }

    /// Returns the complete certificate principal.
    #[must_use]
    pub fn as_str(&self) -> &str {
        &self.uri
    }
}

fn validate_path_segment(value: &str) -> Result<(), ProductionTransportError> {
    if value.is_empty()
        || value.len() > 128
        || matches!(value, "." | "..")
        || value
            .bytes()
            .any(|byte| !(byte.is_ascii_alphanumeric() || matches!(byte, b'-' | b'.' | b'_')))
    {
        return Err(ProductionTransportError::InvalidWorkloadIdentity);
    }
    Ok(())
}

/// One peer admitted under the configured membership epoch.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ProductionPeerAdmission {
    identity: MemberWorkloadIdentity,
    region: ProductionRegion,
    endpoint: SocketAddr,
    server_name: String,
}

impl ProductionPeerAdmission {
    /// Creates one admitted peer route.
    ///
    /// # Errors
    ///
    /// Returns [`ProductionTransportError::InvalidConfiguration`] for an
    /// unusable endpoint or TLS server name.
    pub fn try_new(
        identity: MemberWorkloadIdentity,
        region: ProductionRegion,
        endpoint: SocketAddr,
        server_name: &str,
    ) -> Result<Self, ProductionTransportError> {
        if endpoint.port() == 0
            || server_name.is_empty()
            || server_name.len() > 253
            || server_name != server_name.trim()
            || server_name.chars().any(char::is_control)
            || rustls::pki_types::ServerName::try_from(server_name).is_err()
        {
            return Err(ProductionTransportError::InvalidConfiguration);
        }
        Ok(Self { identity, region, endpoint, server_name: server_name.to_owned() })
    }

    /// Returns the certificate identity this route admits.
    #[must_use]
    pub fn identity(&self) -> &MemberWorkloadIdentity {
        &self.identity
    }

    /// Returns the peer's canonical Region.
    #[must_use]
    pub const fn region(&self) -> ProductionRegion {
        self.region
    }

    /// Returns the peer's private QUIC endpoint.
    #[must_use]
    pub const fn endpoint(&self) -> SocketAddr {
        self.endpoint
    }

    /// Returns the name rustls verifies on the peer's server certificate.
    #[must_use]
    pub fn server_name(&self) -> &str {
        &self.server_name
    }
}

/// Explicit memory and time policy for production Member links.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ProductionTransportLimits {
    max_frame_bytes: usize,
    max_inflight: usize,
    max_queued_frames: usize,
    max_queued_bytes: usize,
    handshake_timeout: Duration,
    frame_timeout: Duration,
    idle_timeout: Duration,
    drain_timeout: Duration,
}

impl ProductionTransportLimits {
    /// Creates a bounded policy.
    ///
    /// # Errors
    ///
    /// Returns [`ProductionTransportError::InvalidLimits`] for zero values,
    /// an oversized frame or queue, a byte queue smaller than one frame, or
    /// a timeout above its hard ceiling.
    #[allow(clippy::too_many_arguments)]
    pub fn try_new(
        max_frame_bytes: usize,
        max_inflight: usize,
        max_queued_frames: usize,
        max_queued_bytes: usize,
        handshake_timeout: Duration,
        frame_timeout: Duration,
        idle_timeout: Duration,
        drain_timeout: Duration,
    ) -> Result<Self, ProductionTransportError> {
        let invalid = !(1024..=PRODUCTION_MAX_FRAME_BYTES).contains(&max_frame_bytes)
            || !(1..=1024).contains(&max_inflight)
            || !(1..=4096).contains(&max_queued_frames)
            || max_queued_bytes < max_frame_bytes
            || max_queued_bytes > 1024 * 1024 * 1024
            || handshake_timeout.is_zero()
            || handshake_timeout > Duration::from_mins(1)
            || frame_timeout.is_zero()
            || frame_timeout > Duration::from_mins(2)
            || idle_timeout.is_zero()
            || idle_timeout > Duration::from_mins(10)
            || drain_timeout.is_zero()
            || drain_timeout > Duration::from_mins(5);
        if invalid {
            return Err(ProductionTransportError::InvalidLimits);
        }
        Ok(Self {
            max_frame_bytes,
            max_inflight,
            max_queued_frames,
            max_queued_bytes,
            handshake_timeout,
            frame_timeout,
            idle_timeout,
            drain_timeout,
        })
    }

    /// A conservative production policy suitable for initial integration.
    /// Qualification may tighten it, but no runtime path may make it
    /// unbounded.
    #[must_use]
    pub fn conservative() -> Self {
        Self {
            max_frame_bytes: 4 * 1024 * 1024,
            max_inflight: 64,
            max_queued_frames: 256,
            max_queued_bytes: 64 * 1024 * 1024,
            handshake_timeout: Duration::from_secs(10),
            frame_timeout: Duration::from_secs(30),
            idle_timeout: Duration::from_mins(1),
            drain_timeout: Duration::from_secs(30),
        }
    }

    /// Returns the encoded frame ceiling.
    #[must_use]
    pub const fn max_frame_bytes(&self) -> usize {
        self.max_frame_bytes
    }

    /// Returns the per-endpoint operation and per-connection stream ceiling.
    #[must_use]
    pub const fn max_inflight(&self) -> usize {
        self.max_inflight
    }

    /// Returns the application queue frame ceiling.
    #[must_use]
    pub const fn max_queued_frames(&self) -> usize {
        self.max_queued_frames
    }

    /// Returns the application queue byte ceiling.
    #[must_use]
    pub const fn max_queued_bytes(&self) -> usize {
        self.max_queued_bytes
    }

    /// Returns the complete TLS and application-handshake deadline.
    #[must_use]
    pub const fn handshake_timeout(&self) -> Duration {
        self.handshake_timeout
    }

    /// Returns the deadline for one frame operation.
    #[must_use]
    pub const fn frame_timeout(&self) -> Duration {
        self.frame_timeout
    }

    /// Returns the QUIC idle deadline.
    #[must_use]
    pub const fn idle_timeout(&self) -> Duration {
        self.idle_timeout
    }

    /// Returns the graceful-drain deadline.
    #[must_use]
    pub const fn drain_timeout(&self) -> Duration {
        self.drain_timeout
    }
}

/// Complete production transport configuration for one local Member.
#[derive(Clone, Debug)]
pub struct ProductionTransportConfig {
    bind: SocketAddr,
    local_identity: MemberWorkloadIdentity,
    local_region: ProductionRegion,
    placement_generation: u64,
    replica_set_epoch: u64,
    peers: BTreeMap<MemberId, ProductionPeerAdmission>,
    limits: ProductionTransportLimits,
}

impl ProductionTransportConfig {
    /// Validates one endpoint against its admitted peer set.
    ///
    /// # Errors
    ///
    /// Returns [`ProductionTransportError::InvalidConfiguration`] when the
    /// bind port is zero, the membership version is zero, the peer set is
    /// empty, identities cross environment or cluster boundaries, or a peer
    /// duplicates the local Member or another admitted Member.
    #[allow(clippy::too_many_arguments)]
    pub fn try_new(
        bind: SocketAddr,
        local_identity: MemberWorkloadIdentity,
        local_region: ProductionRegion,
        placement_generation: u64,
        replica_set_epoch: u64,
        peers: Vec<ProductionPeerAdmission>,
        limits: ProductionTransportLimits,
    ) -> Result<Self, ProductionTransportError> {
        if bind.port() == 0
            || placement_generation == 0
            || replica_set_epoch == 0
            || peers.is_empty()
        {
            return Err(ProductionTransportError::InvalidConfiguration);
        }
        let mut admitted = BTreeMap::new();
        for peer in peers {
            if peer.identity.member == local_identity.member
                || peer.identity.environment != local_identity.environment
                || peer.identity.cluster != local_identity.cluster
                || admitted.insert(peer.identity.member.clone(), peer).is_some()
            {
                return Err(ProductionTransportError::InvalidConfiguration);
            }
        }
        Ok(Self {
            bind,
            local_identity,
            local_region,
            placement_generation,
            replica_set_epoch,
            peers: admitted,
            limits,
        })
    }

    /// Returns the requested private socket binding.
    #[must_use]
    pub const fn bind_address(&self) -> SocketAddr {
        self.bind
    }

    /// Returns the local certificate identity.
    #[must_use]
    pub fn local_identity(&self) -> &MemberWorkloadIdentity {
        &self.local_identity
    }

    /// Returns the local canonical Region.
    #[must_use]
    pub const fn local_region(&self) -> ProductionRegion {
        self.local_region
    }

    /// Returns the admitted placement generation.
    #[must_use]
    pub const fn placement_generation(&self) -> u64 {
        self.placement_generation
    }

    /// Returns the admitted replica-set epoch.
    #[must_use]
    pub const fn replica_set_epoch(&self) -> u64 {
        self.replica_set_epoch
    }

    /// Returns the configured policy.
    #[must_use]
    pub const fn limits(&self) -> &ProductionTransportLimits {
        &self.limits
    }

    /// Returns admitted peers in canonical Member order.
    pub fn peers(&self) -> impl Iterator<Item = &ProductionPeerAdmission> {
        self.peers.values()
    }

    /// Returns one admitted peer.
    #[must_use]
    pub fn peer(&self, member: &MemberId) -> Option<&ProductionPeerAdmission> {
        self.peers.get(member)
    }
}

/// Certificate, key, and dedicated Member-CA roots for one endpoint.
pub struct ProductionTlsMaterial {
    certificate_chain: Vec<CertificateDer<'static>>,
    private_key: PrivateKeyDer<'static>,
    member_ca_roots: Vec<CertificateDer<'static>>,
}

impl ProductionTlsMaterial {
    /// Creates one TLS input bundle.
    ///
    /// # Errors
    ///
    /// Returns [`ProductionTransportError::InvalidTlsMaterial`] when any
    /// required collection is empty. Chain, key, trust, validity, and usage
    /// verification happen while the endpoint is built and handshakes run.
    pub fn try_new(
        certificate_chain: Vec<CertificateDer<'static>>,
        private_key: PrivateKeyDer<'static>,
        member_ca_roots: Vec<CertificateDer<'static>>,
    ) -> Result<Self, ProductionTransportError> {
        if certificate_chain.is_empty() || member_ca_roots.is_empty() {
            return Err(ProductionTransportError::InvalidTlsMaterial);
        }
        Ok(Self { certificate_chain, private_key, member_ca_roots })
    }

    /// Parses one bounded PEM bundle for the production endpoint.
    ///
    /// The caller remains responsible for reading files with its own
    /// ownership, permission, and size policy. This helper only parses the
    /// already-read bytes and never accepts ambient/default credentials.
    ///
    /// # Errors
    ///
    /// Returns [`ProductionTransportError::InvalidTlsMaterial`] when any
    /// certificate or private key cannot be parsed, or a required collection
    /// is empty.
    pub fn from_pem(
        certificate_pem: &[u8],
        private_key_pem: &[u8],
        member_ca_pem: &[u8],
    ) -> Result<Self, ProductionTransportError> {
        let certificate_chain = CertificateDer::pem_slice_iter(certificate_pem)
            .collect::<Result<Vec<_>, _>>()
            .map_err(|_| ProductionTransportError::InvalidTlsMaterial)?;
        let private_key = PrivateKeyDer::from_pem_slice(private_key_pem)
            .map_err(|_| ProductionTransportError::InvalidTlsMaterial)?;
        let member_ca_roots = CertificateDer::pem_slice_iter(member_ca_pem)
            .collect::<Result<Vec<_>, _>>()
            .map_err(|_| ProductionTransportError::InvalidTlsMaterial)?;
        Self::try_new(certificate_chain, private_key, member_ca_roots)
    }
}

/// A frame queue whose frame and byte use are both bounded.
#[derive(Debug)]
pub struct ProductionFrameQueue {
    frames: VecDeque<Vec<u8>>,
    bytes: usize,
    max_frames: usize,
    max_bytes: usize,
    max_frame_bytes: usize,
}

impl ProductionFrameQueue {
    /// Creates a queue from the endpoint's validated limits.
    #[must_use]
    pub fn new(limits: &ProductionTransportLimits) -> Self {
        Self {
            frames: VecDeque::new(),
            bytes: 0,
            max_frames: limits.max_queued_frames,
            max_bytes: limits.max_queued_bytes,
            max_frame_bytes: limits.max_frame_bytes,
        }
    }

    /// Enqueues one already-bounded encoded frame.
    ///
    /// # Errors
    ///
    /// Returns [`ProductionTransportError::FrameTooLarge`] for a single
    /// oversized frame and [`ProductionTransportError::QueueFull`] before
    /// either aggregate bound would be crossed.
    pub fn try_push(&mut self, frame: Vec<u8>) -> Result<(), ProductionTransportError> {
        if frame.is_empty() || frame.len() > self.max_frame_bytes {
            return Err(ProductionTransportError::FrameTooLarge);
        }
        if self.frames.len() >= self.max_frames
            || self.bytes.saturating_add(frame.len()) > self.max_bytes
        {
            return Err(ProductionTransportError::QueueFull);
        }
        self.bytes += frame.len();
        self.frames.push_back(frame);
        Ok(())
    }

    /// Removes the oldest frame.
    pub fn pop(&mut self) -> Option<Vec<u8>> {
        let frame = self.frames.pop_front()?;
        self.bytes -= frame.len();
        Some(frame)
    }

    /// Returns the current frame count.
    #[must_use]
    pub fn len(&self) -> usize {
        self.frames.len()
    }

    /// Reports whether the queue is empty.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.frames.is_empty()
    }

    /// Returns the current encoded byte count.
    #[must_use]
    pub const fn bytes(&self) -> usize {
        self.bytes
    }
}

/// Factual state exposed to the owning Member runtime.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
#[allow(clippy::struct_excessive_bools)]
pub struct ProductionTransportFacts {
    /// The UDP socket is bound.
    pub bound: bool,
    /// New application sessions are still admitted.
    pub accepting: bool,
    /// TLS is restricted to version 1.3.
    pub tls13_only: bool,
    /// Both client and server certificates are mandatory.
    pub mutual_tls: bool,
    /// TLS and QUIC early application data are disabled.
    pub zero_rtt_disabled: bool,
    /// The selected TLS provider is installed and enforces TLS 1.3 mTLS.
    pub tls_provider_ready: bool,
    /// Peers admitted by the current membership version.
    pub configured_peers: usize,
    /// Currently authenticated application sessions.
    pub authenticated_connections: usize,
    /// Distinct admitted peers with at least one authenticated session.
    pub authenticated_peers: usize,
    /// Operations currently inside the bounded transport.
    pub inflight_operations: usize,
    /// The placement generation checked by every session hello.
    pub placement_generation: u64,
    /// The replica-set epoch checked by every session hello.
    pub replica_set_epoch: u64,
}

/// Result of a bounded graceful drain.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct ProductionDrainReport {
    /// Existing operations reached zero before the deadline.
    pub operations_drained: bool,
    /// Operations still active when the endpoint closed.
    pub remaining_operations: usize,
    /// Connections still owned when the endpoint close began.
    pub connections_closed: usize,
}

/// A real production-configured QUIC endpoint for one local Member.
pub struct ProductionQuicEndpoint {
    config: Arc<ProductionTransportConfig>,
    endpoint: quinn::Endpoint,
    state: Arc<RuntimeState>,
}

impl ProductionQuicEndpoint {
    /// Binds one endpoint with the single non-FIPS ring provider (D-41
    /// defers FIPS builds past the first customer), TLS 1.3-only mutual
    /// authentication, early data disabled, and bounded QUIC flow control.
    ///
    /// # Errors
    ///
    /// Returns a typed transport error when the
    /// local certificate identity differs from the configured identity, TLS
    /// material cannot be installed, or the UDP address cannot be bound.
    #[allow(clippy::too_many_lines)]
    pub fn bind(
        config: ProductionTransportConfig,
        tls: ProductionTlsMaterial,
    ) -> Result<Self, ProductionTransportError> {
        let leaf =
            tls.certificate_chain.first().ok_or(ProductionTransportError::InvalidTlsMaterial)?;
        let local_identity = MemberWorkloadIdentity::from_leaf_certificate(leaf)?;
        if local_identity != config.local_identity {
            return Err(ProductionTransportError::IdentityMismatch);
        }

        let provider = Arc::new(rustls::crypto::ring::default_provider());
        let mut roots = rustls::RootCertStore::empty();
        for root in &tls.member_ca_roots {
            roots.add(root.clone()).map_err(|_| ProductionTransportError::InvalidTlsMaterial)?;
        }
        let verifier = rustls::server::WebPkiClientVerifier::builder_with_provider(
            Arc::new(roots.clone()),
            Arc::clone(&provider),
        )
        .build()
        .map_err(|_| ProductionTransportError::InvalidTlsMaterial)?;

        let mut rustls_server = rustls::ServerConfig::builder_with_provider(Arc::clone(&provider))
            .with_protocol_versions(&[&rustls::version::TLS13])
            .map_err(|_| ProductionTransportError::InvalidTlsMaterial)?
            .with_client_cert_verifier(verifier)
            .with_single_cert(tls.certificate_chain.clone(), tls.private_key.clone_key())
            .map_err(|_| ProductionTransportError::InvalidTlsMaterial)?;
        rustls_server.alpn_protocols = vec![PRODUCTION_MEMBER_ALPN.to_vec()];
        rustls_server.max_early_data_size = 0;

        let mut rustls_client = rustls::ClientConfig::builder_with_provider(provider)
            .with_protocol_versions(&[&rustls::version::TLS13])
            .map_err(|_| ProductionTransportError::InvalidTlsMaterial)?
            .with_root_certificates(roots)
            .with_client_auth_cert(tls.certificate_chain, tls.private_key)
            .map_err(|_| ProductionTransportError::InvalidTlsMaterial)?;
        rustls_client.alpn_protocols = vec![PRODUCTION_MEMBER_ALPN.to_vec()];
        rustls_client.enable_early_data = false;

        let server_crypto = quinn::crypto::rustls::QuicServerConfig::try_from(rustls_server)
            .map_err(|_| ProductionTransportError::InvalidTlsMaterial)?;
        let client_crypto = quinn::crypto::rustls::QuicClientConfig::try_from(rustls_client)
            .map_err(|_| ProductionTransportError::InvalidTlsMaterial)?;

        let stream_window = quinn::VarInt::try_from(
            u64::try_from(config.limits.max_frame_bytes + 4)
                .map_err(|_| ProductionTransportError::InvalidLimits)?,
        )
        .map_err(|_| ProductionTransportError::InvalidLimits)?;
        let aggregate_window_bytes = config
            .limits
            .max_frame_bytes
            .saturating_mul(config.limits.max_inflight)
            .min(config.limits.max_queued_bytes);
        let aggregate_window = quinn::VarInt::try_from(
            u64::try_from(aggregate_window_bytes)
                .map_err(|_| ProductionTransportError::InvalidLimits)?,
        )
        .map_err(|_| ProductionTransportError::InvalidLimits)?;
        let stream_count = quinn::VarInt::from_u32(
            u32::try_from(config.limits.max_inflight)
                .map_err(|_| ProductionTransportError::InvalidLimits)?,
        );
        let mut transport = quinn::TransportConfig::default();
        transport
            .max_concurrent_bidi_streams(stream_count)
            .max_concurrent_uni_streams(stream_count)
            .stream_receive_window(stream_window)
            .receive_window(aggregate_window)
            .send_window(
                u64::try_from(aggregate_window_bytes)
                    .map_err(|_| ProductionTransportError::InvalidLimits)?,
            )
            .max_idle_timeout(Some(
                config
                    .limits
                    .idle_timeout
                    .try_into()
                    .map_err(|_| ProductionTransportError::InvalidLimits)?,
            ))
            .keep_alive_interval(Some(config.limits.idle_timeout / 3))
            .datagram_receive_buffer_size(None)
            .datagram_send_buffer_size(0);
        let transport = Arc::new(transport);

        let peer_slots = config.peers.len().saturating_mul(MAX_CONNECTIONS_PER_PEER).max(1);
        let incoming_buffer_size = u64::try_from(config.limits.max_queued_bytes)
            .map_err(|_| ProductionTransportError::InvalidLimits)?;
        let incoming_buffer_size_total =
            u64::try_from(config.limits.max_queued_bytes.saturating_mul(peer_slots))
                .map_err(|_| ProductionTransportError::InvalidLimits)?;
        let mut server_config = quinn::ServerConfig::with_crypto(Arc::new(server_crypto));
        server_config
            .max_incoming(peer_slots)
            .incoming_buffer_size(incoming_buffer_size)
            .incoming_buffer_size_total(incoming_buffer_size_total);
        server_config.transport_config(Arc::clone(&transport));
        let mut client_config = quinn::ClientConfig::new(Arc::new(client_crypto));
        client_config.transport_config(transport);

        let socket =
            std::net::UdpSocket::bind(config.bind).map_err(|_| ProductionTransportError::Bind)?;
        socket.set_nonblocking(true).map_err(|_| ProductionTransportError::Bind)?;
        let mut endpoint = quinn::Endpoint::new(
            quinn::EndpointConfig::default(),
            Some(server_config),
            socket,
            Arc::new(quinn::TokioRuntime),
        )
        .map_err(|_| ProductionTransportError::Bind)?;
        endpoint.set_default_client_config(client_config);

        Ok(Self { config: Arc::new(config), endpoint, state: Arc::new(RuntimeState::new()) })
    }

    /// Returns the actual bound socket address.
    ///
    /// # Errors
    ///
    /// Returns [`ProductionTransportError::Bind`] when the socket has become
    /// unavailable.
    pub fn local_address(&self) -> Result<SocketAddr, ProductionTransportError> {
        self.endpoint.local_addr().map_err(|_| ProductionTransportError::Bind)
    }

    /// Returns the validated endpoint configuration.
    #[must_use]
    pub fn config(&self) -> &ProductionTransportConfig {
        &self.config
    }

    /// Returns current transport facts without claiming replication health.
    #[must_use]
    pub fn facts(&self) -> ProductionTransportFacts {
        ProductionTransportFacts {
            bound: true,
            accepting: !self.state.draining.load(Ordering::Acquire),
            tls13_only: true,
            mutual_tls: true,
            zero_rtt_disabled: true,
            tls_provider_ready: true,
            configured_peers: self.config.peers.len(),
            authenticated_connections: self.state.authenticated_connections.load(Ordering::Acquire),
            authenticated_peers: self.state.authenticated_peer_count(),
            inflight_operations: self.state.active_operations.load(Ordering::Acquire),
            placement_generation: self.config.placement_generation,
            replica_set_epoch: self.config.replica_set_epoch,
        }
    }

    /// Dials one admitted peer and completes the post-TLS identity and epoch
    /// handshake. This path awaits the full handshake and never calls
    /// quinn's 0-RTT conversion.
    ///
    /// # Errors
    ///
    /// Returns a typed error for drain, saturation, unknown peers, timeout,
    /// TLS failure, certificate/hello mismatch, stale membership, or the
    /// bounded concurrent-connection ceiling.
    pub async fn connect(
        &self,
        member: &MemberId,
    ) -> Result<ProductionConnection, ProductionTransportError> {
        let _operation = self.state.begin_operation(self.config.limits.max_inflight)?;
        let peer =
            self.config.peer(member).ok_or(ProductionTransportError::PeerNotAdmitted)?.clone();
        let connecting = self
            .endpoint
            .connect(peer.endpoint, &peer.server_name)
            .map_err(|_| ProductionTransportError::ConnectionFailed)?;
        let connection = tokio::time::timeout(self.config.limits.handshake_timeout, connecting)
            .await
            .map_err(|_| ProductionTransportError::Timeout)?
            .map_err(|_| ProductionTransportError::ConnectionFailed)?;
        let certificate_identity = peer_identity(&connection)?;
        if certificate_identity != peer.identity {
            connection.close(CLOSE_AUTHORIZATION.into(), b"member identity mismatch");
            return Err(ProductionTransportError::IdentityMismatch);
        }

        let (mut send, mut receive) =
            tokio::time::timeout(self.config.limits.handshake_timeout, connection.open_bi())
                .await
                .map_err(|_| ProductionTransportError::Timeout)?
                .map_err(|_| ProductionTransportError::ConnectionFailed)?;
        let local_hello = ConnectionHello::new(&self.config)?;
        tokio::time::timeout(
            self.config.limits.handshake_timeout,
            write_hello(&mut send, &local_hello),
        )
        .await
        .map_err(|_| ProductionTransportError::Timeout)??;
        send.finish().map_err(|_| ProductionTransportError::ConnectionFailed)?;
        let remote_hello =
            tokio::time::timeout(self.config.limits.handshake_timeout, read_hello(&mut receive))
                .await
                .map_err(|_| ProductionTransportError::Timeout)??;
        self.validate_hello(&remote_hello, &certificate_identity, &peer)?;
        self.finish_connection(connection, certificate_identity, &remote_hello)
    }

    /// Accepts and authenticates the next inbound connection.
    ///
    /// # Errors
    ///
    /// Uses the same fail-closed classes as [`Self::connect`]. A closed
    /// endpoint returns `Ok(None)`.
    pub async fn accept(&self) -> Result<Option<ProductionConnection>, ProductionTransportError> {
        let _operation = self.state.begin_operation(self.config.limits.max_inflight)?;
        let Some(incoming) =
            tokio::time::timeout(self.config.limits.handshake_timeout, self.endpoint.accept())
                .await
                .map_err(|_| ProductionTransportError::Timeout)?
        else {
            return Ok(None);
        };
        let connection = tokio::time::timeout(self.config.limits.handshake_timeout, incoming)
            .await
            .map_err(|_| ProductionTransportError::Timeout)?
            .map_err(|_| ProductionTransportError::ConnectionFailed)?;
        let certificate_identity = peer_identity(&connection)?;
        let peer = self
            .config
            .peer(certificate_identity.member())
            .ok_or(ProductionTransportError::PeerNotAdmitted)?
            .clone();
        if certificate_identity != peer.identity {
            connection.close(CLOSE_AUTHORIZATION.into(), b"member identity mismatch");
            return Err(ProductionTransportError::IdentityMismatch);
        }
        let (mut send, mut receive) =
            tokio::time::timeout(self.config.limits.handshake_timeout, connection.accept_bi())
                .await
                .map_err(|_| ProductionTransportError::Timeout)?
                .map_err(|_| ProductionTransportError::ConnectionFailed)?;
        let remote_hello =
            tokio::time::timeout(self.config.limits.handshake_timeout, read_hello(&mut receive))
                .await
                .map_err(|_| ProductionTransportError::Timeout)??;
        self.validate_hello(&remote_hello, &certificate_identity, &peer)?;
        let local_hello = ConnectionHello::new(&self.config)?;
        tokio::time::timeout(
            self.config.limits.handshake_timeout,
            write_hello(&mut send, &local_hello),
        )
        .await
        .map_err(|_| ProductionTransportError::Timeout)??;
        send.finish().map_err(|_| ProductionTransportError::ConnectionFailed)?;
        self.finish_connection(connection, certificate_identity, &remote_hello).map(Some)
    }

    fn validate_hello(
        &self,
        hello: &ConnectionHello,
        certificate_identity: &MemberWorkloadIdentity,
        peer: &ProductionPeerAdmission,
    ) -> Result<(), ProductionTransportError> {
        if hello.protocol_major != PRODUCTION_PROTOCOL_MAJOR {
            return Err(ProductionTransportError::ProtocolMismatch);
        }
        if hello.cluster != self.config.local_identity.cluster
            || hello.member != *certificate_identity.member()
            || hello.member != *peer.identity.member()
            || hello.region != peer.region
        {
            return Err(ProductionTransportError::IdentityMismatch);
        }
        if hello.placement_generation != self.config.placement_generation
            || hello.replica_set_epoch != self.config.replica_set_epoch
        {
            return Err(ProductionTransportError::StaleMembership);
        }
        if hello.maximum_frame_bytes < 1024
            || hello.maximum_frame_bytes > PRODUCTION_MAX_FRAME_BYTES
            || hello.nonce == [0_u8; NONCE_BYTES]
        {
            return Err(ProductionTransportError::ProtocolMismatch);
        }
        self.state.remember_nonce(&hello.member, hello.nonce, self.config.peers.len())
    }

    fn finish_connection(
        &self,
        connection: quinn::Connection,
        peer: MemberWorkloadIdentity,
        hello: &ConnectionHello,
    ) -> Result<ProductionConnection, ProductionTransportError> {
        let registration = self.state.register_connection(peer.member())?;
        Ok(ProductionConnection {
            connection,
            peer,
            peer_region: hello.region,
            placement_generation: hello.placement_generation,
            replica_set_epoch: hello.replica_set_epoch,
            maximum_frame_bytes: self.config.limits.max_frame_bytes.min(hello.maximum_frame_bytes),
            frame_timeout: self.config.limits.frame_timeout,
            max_inflight: self.config.limits.max_inflight,
            state: Arc::clone(&self.state),
            _registration: registration,
        })
    }

    /// Stops admission, waits for active operations up to the configured
    /// deadline, then closes every QUIC connection and the UDP endpoint.
    pub async fn drain(self) -> ProductionDrainReport {
        self.state.draining.store(true, Ordering::Release);
        let deadline = self.config.limits.drain_timeout;
        let drained = tokio::time::timeout(deadline, async {
            loop {
                let notified = self.state.changed.notified();
                tokio::pin!(notified);
                notified.as_mut().enable();
                if self.state.active_operations.load(Ordering::Acquire) == 0 {
                    break;
                }
                notified.await;
            }
        })
        .await
        .is_ok();
        let remaining = self.state.active_operations.load(Ordering::Acquire);
        let connections = self.state.authenticated_connections.load(Ordering::Acquire);
        self.endpoint.close(CLOSE_DRAINING.into(), b"member draining");
        let _ = tokio::time::timeout(deadline, self.endpoint.wait_idle()).await;
        ProductionDrainReport {
            operations_drained: drained,
            remaining_operations: remaining,
            connections_closed: connections,
        }
    }
}

/// One fully authenticated Member application session.
pub struct ProductionConnection {
    connection: quinn::Connection,
    peer: MemberWorkloadIdentity,
    peer_region: ProductionRegion,
    placement_generation: u64,
    replica_set_epoch: u64,
    maximum_frame_bytes: usize,
    frame_timeout: Duration,
    max_inflight: usize,
    state: Arc<RuntimeState>,
    _registration: ConnectionRegistration,
}

impl ProductionConnection {
    /// Returns the certificate-derived peer identity.
    #[must_use]
    pub fn peer(&self) -> &MemberWorkloadIdentity {
        &self.peer
    }

    /// Returns the peer's admitted Region.
    #[must_use]
    pub const fn peer_region(&self) -> ProductionRegion {
        self.peer_region
    }

    /// Returns the admitted placement generation.
    #[must_use]
    pub const fn placement_generation(&self) -> u64 {
        self.placement_generation
    }

    /// Returns the admitted replica-set epoch.
    #[must_use]
    pub const fn replica_set_epoch(&self) -> u64 {
        self.replica_set_epoch
    }

    /// Returns the negotiated encoded-frame ceiling.
    #[must_use]
    pub const fn maximum_frame_bytes(&self) -> usize {
        self.maximum_frame_bytes
    }

    /// Sends one length-prefixed frame after a full authenticated handshake.
    ///
    /// # Errors
    ///
    /// Refuses drain, saturation, empty or oversized frames, timeout, and
    /// connection failures without retaining an unbounded retry queue.
    pub async fn send_frame(&self, frame: &[u8]) -> Result<(), ProductionTransportError> {
        if frame.is_empty() || frame.len() > self.maximum_frame_bytes {
            return Err(ProductionTransportError::FrameTooLarge);
        }
        let _operation = self.state.begin_operation(self.max_inflight)?;
        let mut stream = tokio::time::timeout(self.frame_timeout, self.connection.open_uni())
            .await
            .map_err(|_| ProductionTransportError::Timeout)?
            .map_err(|_| ProductionTransportError::ConnectionFailed)?;
        let length = u32::try_from(frame.len())
            .map_err(|_| ProductionTransportError::FrameTooLarge)?
            .to_be_bytes();
        tokio::time::timeout(self.frame_timeout, async {
            stream.write_all(&length).await?;
            stream.write_all(frame).await?;
            stream.finish()?;
            Ok::<(), quinn::WriteError>(())
        })
        .await
        .map_err(|_| ProductionTransportError::Timeout)?
        .map_err(|_| ProductionTransportError::ConnectionFailed)
    }

    /// Receives one bounded length-prefixed frame.
    ///
    /// # Errors
    ///
    /// Refuses drain, saturation, timeouts, malformed length prefixes, and
    /// frames above the negotiated limit before allocating the body.
    pub async fn receive_frame(&self) -> Result<Vec<u8>, ProductionTransportError> {
        let _operation = self.state.begin_operation(self.max_inflight)?;
        let mut stream = tokio::time::timeout(self.frame_timeout, self.connection.accept_uni())
            .await
            .map_err(|_| ProductionTransportError::Timeout)?
            .map_err(|_| ProductionTransportError::ConnectionFailed)?;
        tokio::time::timeout(self.frame_timeout, async {
            let mut length = [0_u8; 4];
            stream
                .read_exact(&mut length)
                .await
                .map_err(|_| ProductionTransportError::ConnectionFailed)?;
            let length = u32::from_be_bytes(length) as usize;
            if length == 0 || length > self.maximum_frame_bytes {
                stream.stop(CLOSE_AUTHORIZATION.into()).ok();
                return Err(ProductionTransportError::FrameTooLarge);
            }
            let mut frame = vec![0_u8; length];
            stream
                .read_exact(&mut frame)
                .await
                .map_err(|_| ProductionTransportError::ConnectionFailed)?;
            Ok(frame)
        })
        .await
        .map_err(|_| ProductionTransportError::Timeout)?
    }
}

impl Drop for ProductionConnection {
    fn drop(&mut self) {
        self.connection.close(CLOSE_DRAINING.into(), b"session released");
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
struct ConnectionHello {
    protocol_major: u16,
    cluster: String,
    member: MemberId,
    region: ProductionRegion,
    placement_generation: u64,
    replica_set_epoch: u64,
    maximum_frame_bytes: usize,
    nonce: [u8; NONCE_BYTES],
}

impl ConnectionHello {
    fn new(config: &ProductionTransportConfig) -> Result<Self, ProductionTransportError> {
        let mut nonce = [0_u8; NONCE_BYTES];
        SystemRandom::new()
            .fill(&mut nonce)
            .map_err(|_| ProductionTransportError::RandomUnavailable)?;
        Ok(Self {
            protocol_major: PRODUCTION_PROTOCOL_MAJOR,
            cluster: config.local_identity.cluster.clone(),
            member: config.local_identity.member.clone(),
            region: config.local_region,
            placement_generation: config.placement_generation,
            replica_set_epoch: config.replica_set_epoch,
            maximum_frame_bytes: config.limits.max_frame_bytes,
            nonce,
        })
    }

    fn encode(&self) -> Result<Vec<u8>, ProductionTransportError> {
        let cluster = self.cluster.as_bytes();
        let member = self.member.as_str().as_bytes();
        if cluster.len() > u8::MAX as usize || member.len() > u8::MAX as usize {
            return Err(ProductionTransportError::ProtocolMismatch);
        }
        let mut bytes = Vec::with_capacity(80 + cluster.len() + member.len());
        bytes.extend_from_slice(&HELLO_MAGIC);
        bytes.extend_from_slice(&self.protocol_major.to_be_bytes());
        bytes.extend_from_slice(&self.region.as_bytes());
        bytes.extend_from_slice(&self.placement_generation.to_be_bytes());
        bytes.extend_from_slice(&self.replica_set_epoch.to_be_bytes());
        bytes.extend_from_slice(
            &u32::try_from(self.maximum_frame_bytes)
                .map_err(|_| ProductionTransportError::ProtocolMismatch)?
                .to_be_bytes(),
        );
        bytes.push(
            u8::try_from(cluster.len()).map_err(|_| ProductionTransportError::ProtocolMismatch)?,
        );
        bytes.push(
            u8::try_from(member.len()).map_err(|_| ProductionTransportError::ProtocolMismatch)?,
        );
        bytes.extend_from_slice(cluster);
        bytes.extend_from_slice(member);
        bytes.extend_from_slice(&self.nonce);
        if bytes.len() > PRODUCTION_MAX_HELLO_BYTES {
            return Err(ProductionTransportError::ProtocolMismatch);
        }
        Ok(bytes)
    }

    fn decode(bytes: &[u8]) -> Result<Self, ProductionTransportError> {
        const FIXED: usize = 4 + 2 + 3 + 8 + 8 + 4 + 1 + 1;
        if bytes.len() < FIXED + NONCE_BYTES || bytes.len() > PRODUCTION_MAX_HELLO_BYTES {
            return Err(ProductionTransportError::ProtocolMismatch);
        }
        let mut cursor = 0;
        if take::<4>(bytes, &mut cursor)? != HELLO_MAGIC {
            return Err(ProductionTransportError::ProtocolMismatch);
        }
        let protocol_major = u16::from_be_bytes(take(bytes, &mut cursor)?);
        let region_bytes = take::<3>(bytes, &mut cursor)?;
        let region_text = std::str::from_utf8(&region_bytes)
            .map_err(|_| ProductionTransportError::InvalidRegion)?;
        let region = ProductionRegion::try_new(region_text)?;
        let placement_generation = u64::from_be_bytes(take(bytes, &mut cursor)?);
        let replica_set_epoch = u64::from_be_bytes(take(bytes, &mut cursor)?);
        let maximum_frame_bytes = u32::from_be_bytes(take(bytes, &mut cursor)?) as usize;
        let cluster_len = usize::from(take::<1>(bytes, &mut cursor)?[0]);
        let member_len = usize::from(take::<1>(bytes, &mut cursor)?[0]);
        let variable_end = cursor
            .checked_add(cluster_len)
            .and_then(|end| end.checked_add(member_len))
            .and_then(|end| end.checked_add(NONCE_BYTES))
            .ok_or(ProductionTransportError::ProtocolMismatch)?;
        if variable_end != bytes.len() {
            return Err(ProductionTransportError::ProtocolMismatch);
        }
        let cluster = std::str::from_utf8(&bytes[cursor..cursor + cluster_len])
            .map_err(|_| ProductionTransportError::ProtocolMismatch)?
            .to_owned();
        validate_path_segment(&cluster)?;
        cursor += cluster_len;
        let member_text = std::str::from_utf8(&bytes[cursor..cursor + member_len])
            .map_err(|_| ProductionTransportError::ProtocolMismatch)?;
        let member = MemberId::try_new(member_text)
            .map_err(|_| ProductionTransportError::ProtocolMismatch)?;
        cursor += member_len;
        let nonce = take(bytes, &mut cursor)?;
        Ok(Self {
            protocol_major,
            cluster,
            member,
            region,
            placement_generation,
            replica_set_epoch,
            maximum_frame_bytes,
            nonce,
        })
    }
}

fn take<const N: usize>(
    bytes: &[u8],
    cursor: &mut usize,
) -> Result<[u8; N], ProductionTransportError> {
    let end = cursor.checked_add(N).ok_or(ProductionTransportError::ProtocolMismatch)?;
    let slice = bytes.get(*cursor..end).ok_or(ProductionTransportError::ProtocolMismatch)?;
    let value = slice.try_into().map_err(|_| ProductionTransportError::ProtocolMismatch)?;
    *cursor = end;
    Ok(value)
}

async fn write_hello(
    stream: &mut quinn::SendStream,
    hello: &ConnectionHello,
) -> Result<(), ProductionTransportError> {
    let bytes = hello.encode()?;
    let length = u16::try_from(bytes.len())
        .map_err(|_| ProductionTransportError::ProtocolMismatch)?
        .to_be_bytes();
    stream.write_all(&length).await.map_err(|_| ProductionTransportError::ConnectionFailed)?;
    stream.write_all(&bytes).await.map_err(|_| ProductionTransportError::ConnectionFailed)
}

async fn read_hello(
    stream: &mut quinn::RecvStream,
) -> Result<ConnectionHello, ProductionTransportError> {
    let mut length = [0_u8; 2];
    stream.read_exact(&mut length).await.map_err(|_| ProductionTransportError::ConnectionFailed)?;
    let length = usize::from(u16::from_be_bytes(length));
    if length == 0 || length > PRODUCTION_MAX_HELLO_BYTES {
        return Err(ProductionTransportError::ProtocolMismatch);
    }
    let mut bytes = vec![0_u8; length];
    stream.read_exact(&mut bytes).await.map_err(|_| ProductionTransportError::ConnectionFailed)?;
    ConnectionHello::decode(&bytes)
}

fn peer_identity(
    connection: &quinn::Connection,
) -> Result<MemberWorkloadIdentity, ProductionTransportError> {
    let identity =
        connection.peer_identity().ok_or(ProductionTransportError::InvalidPeerCertificate)?;
    let certificates = identity
        .downcast::<Vec<CertificateDer<'static>>>()
        .map_err(|_| ProductionTransportError::InvalidPeerCertificate)?;
    let leaf = certificates.first().ok_or(ProductionTransportError::InvalidPeerCertificate)?;
    MemberWorkloadIdentity::from_leaf_certificate(leaf)
}

struct RuntimeState {
    draining: AtomicBool,
    active_operations: AtomicUsize,
    authenticated_connections: AtomicUsize,
    connections_by_peer: Mutex<BTreeMap<MemberId, usize>>,
    seen_nonces: Mutex<VecDeque<(MemberId, [u8; NONCE_BYTES])>>,
    changed: Notify,
}

impl RuntimeState {
    fn new() -> Self {
        Self {
            draining: AtomicBool::new(false),
            active_operations: AtomicUsize::new(0),
            authenticated_connections: AtomicUsize::new(0),
            connections_by_peer: Mutex::new(BTreeMap::new()),
            seen_nonces: Mutex::new(VecDeque::new()),
            changed: Notify::new(),
        }
    }

    fn begin_operation(
        self: &Arc<Self>,
        maximum: usize,
    ) -> Result<OperationGuard, ProductionTransportError> {
        if self.draining.load(Ordering::Acquire) {
            return Err(ProductionTransportError::Draining);
        }
        let previous = self.active_operations.fetch_add(1, Ordering::AcqRel);
        if previous >= maximum || self.draining.load(Ordering::Acquire) {
            self.active_operations.fetch_sub(1, Ordering::AcqRel);
            self.changed.notify_waiters();
            return Err(if previous >= maximum {
                ProductionTransportError::Saturated
            } else {
                ProductionTransportError::Draining
            });
        }
        Ok(OperationGuard { state: Arc::clone(self) })
    }

    fn register_connection(
        self: &Arc<Self>,
        peer: &MemberId,
    ) -> Result<ConnectionRegistration, ProductionTransportError> {
        let mut connections = self
            .connections_by_peer
            .lock()
            .map_err(|_| ProductionTransportError::StateUnavailable)?;
        let count = connections.entry(peer.clone()).or_insert(0);
        if *count >= MAX_CONNECTIONS_PER_PEER {
            return Err(ProductionTransportError::Saturated);
        }
        *count += 1;
        self.authenticated_connections.fetch_add(1, Ordering::AcqRel);
        Ok(ConnectionRegistration { state: Arc::clone(self), peer: peer.clone() })
    }

    fn authenticated_peer_count(&self) -> usize {
        match self.connections_by_peer.lock() {
            Ok(connections) => connections.values().filter(|count| **count > 0).count(),
            Err(poisoned) => poisoned.into_inner().values().filter(|count| **count > 0).count(),
        }
    }

    fn remember_nonce(
        &self,
        member: &MemberId,
        nonce: [u8; NONCE_BYTES],
        peers: usize,
    ) -> Result<(), ProductionTransportError> {
        let mut seen =
            self.seen_nonces.lock().map_err(|_| ProductionTransportError::StateUnavailable)?;
        if seen
            .iter()
            .any(|(known_member, known_nonce)| known_member == member && known_nonce == &nonce)
        {
            return Err(ProductionTransportError::Replay);
        }
        let capacity = peers.saturating_mul(MAX_CONNECTIONS_PER_PEER).saturating_mul(4).max(16);
        while seen.len() >= capacity {
            seen.pop_front();
        }
        seen.push_back((member.clone(), nonce));
        Ok(())
    }
}

struct OperationGuard {
    state: Arc<RuntimeState>,
}

impl Drop for OperationGuard {
    fn drop(&mut self) {
        self.state.active_operations.fetch_sub(1, Ordering::AcqRel);
        self.state.changed.notify_waiters();
    }
}

struct ConnectionRegistration {
    state: Arc<RuntimeState>,
    peer: MemberId,
}

impl Drop for ConnectionRegistration {
    fn drop(&mut self) {
        if let Ok(mut connections) = self.state.connections_by_peer.lock()
            && let Some(count) = connections.get_mut(&self.peer)
        {
            *count = count.saturating_sub(1);
            if *count == 0 {
                connections.remove(&self.peer);
            }
        }
        self.state.authenticated_connections.fetch_sub(1, Ordering::AcqRel);
        self.state.changed.notify_waiters();
    }
}

/// Fail-closed production transport errors.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ProductionTransportError {
    /// A Region was not one of the exact initial production codes.
    InvalidRegion,
    /// A SPIFFE workload identity was outside the exact namespace.
    InvalidWorkloadIdentity,
    /// The endpoint, membership version, route, or peer set was invalid.
    InvalidConfiguration,
    /// One of the memory or time bounds was invalid.
    InvalidLimits,
    /// Certificate, key, or CA material could not form the required mTLS policy.
    InvalidTlsMaterial,
    /// The local UDP endpoint could not bind.
    Bind,
    /// A peer was absent from the admitted membership.
    PeerNotAdmitted,
    /// The TLS peer certificate could not yield exactly one Member URI SAN.
    InvalidPeerCertificate,
    /// Certificate, configured, and application identities did not agree.
    IdentityMismatch,
    /// The peer spoke another application protocol major or a malformed hello.
    ProtocolMismatch,
    /// The peer's placement generation or replica-set epoch was stale.
    StaleMembership,
    /// A previously observed connection nonce was replayed.
    Replay,
    /// Provider randomness was unavailable.
    RandomUnavailable,
    /// The transport has stopped admitting work.
    Draining,
    /// A bounded connection or operation ceiling was reached.
    Saturated,
    /// A configured deadline expired.
    Timeout,
    /// QUIC connection or stream work failed.
    ConnectionFailed,
    /// A frame was empty or above its negotiated bound.
    FrameTooLarge,
    /// The explicit application queue reached a frame or byte bound.
    QueueFull,
    /// Internal bounded state could not be acquired.
    StateUnavailable,
}

#[cfg(test)]
mod tests {
    use std::net::{IpAddr, Ipv4Addr, SocketAddr, UdpSocket};
    use std::time::Duration;

    use rcgen::{
        BasicConstraints, Certificate, CertificateParams, ExtendedKeyUsagePurpose, Ia5String, IsCa,
        KeyPair, KeyUsagePurpose, SanType,
    };
    use rustls::pki_types::{CertificateDer, PrivateKeyDer, PrivatePkcs8KeyDer};

    use super::{
        ConnectionHello, MemberWorkloadIdentity, ProductionFrameQueue, ProductionPeerAdmission,
        ProductionQuicEndpoint, ProductionRegion, ProductionTlsMaterial, ProductionTransportConfig,
        ProductionTransportError, ProductionTransportLimits,
    };

    fn limits() -> ProductionTransportLimits {
        ProductionTransportLimits::try_new(
            4096,
            4,
            2,
            6000,
            Duration::from_secs(1),
            Duration::from_secs(1),
            Duration::from_secs(5),
            Duration::from_secs(1),
        )
        .expect("bounded test policy")
    }

    fn config() -> ProductionTransportConfig {
        let local = MemberWorkloadIdentity::try_new("prod", "cluster-a", "member-a")
            .expect("local identity");
        let peer_identity = MemberWorkloadIdentity::try_new("prod", "cluster-a", "member-b")
            .expect("peer identity");
        let peer = ProductionPeerAdmission::try_new(
            peer_identity,
            ProductionRegion::Gru,
            SocketAddr::new(IpAddr::V4(Ipv4Addr::LOCALHOST), 7444),
            "member-b.internal",
        )
        .expect("peer");
        ProductionTransportConfig::try_new(
            SocketAddr::new(IpAddr::V4(Ipv4Addr::LOCALHOST), 7443),
            local,
            ProductionRegion::Gru,
            9,
            12,
            vec![peer],
            limits(),
        )
        .expect("config")
    }

    fn certificate_with_uri_sans(uris: &[&str]) -> CertificateDer<'static> {
        let key = KeyPair::generate().expect("test key");
        let mut params = CertificateParams::default();
        params.subject_alt_names = uris
            .iter()
            .map(|uri| SanType::URI(Ia5String::try_from(*uri).expect("test URI is valid IA5 text")))
            .collect();
        CertificateDer::from(params.self_signed(&key).expect("test certificate").der().to_vec())
    }

    fn member_tls_material(
        identity: &MemberWorkloadIdentity,
        server_name: &str,
        issuer: &Certificate,
        issuer_key: &KeyPair,
        anchor: &CertificateDer<'static>,
    ) -> ProductionTlsMaterial {
        let key = KeyPair::generate().expect("member key");
        let mut params =
            CertificateParams::new(vec![server_name.to_owned()]).expect("member certificate");
        params.subject_alt_names.push(SanType::URI(
            Ia5String::try_from(identity.as_str()).expect("canonical identity is IA5 text"),
        ));
        params.extended_key_usages =
            vec![ExtendedKeyUsagePurpose::ServerAuth, ExtendedKeyUsagePurpose::ClientAuth];
        let certificate = params.signed_by(&key, issuer, issuer_key).expect("signed member leaf");
        ProductionTlsMaterial::try_new(
            vec![CertificateDer::from(certificate.der().to_vec())],
            PrivateKeyDer::Pkcs8(PrivatePkcs8KeyDer::from(key.serialize_der())),
            vec![anchor.clone()],
        )
        .expect("complete member TLS material")
    }

    #[test]
    fn workload_identity_is_an_exact_namespace() {
        let identity = MemberWorkloadIdentity::try_new("prod", "cluster-a", "member-a")
            .expect("canonical identity");
        assert_eq!(
            identity.as_str(),
            "spiffe://terrascale.tech/prod/terrabase-member/cluster-a/member-a"
        );
        assert_eq!(MemberWorkloadIdentity::parse(identity.as_str()), Ok(identity));
        for invalid in [
            "spiffe://terrascale.tech/prod/database-gateway/cluster-a/member-a",
            "spiffe://terrascale.tech/prod/terrabase-member/cluster-a/member-a/extra",
            "spiffe://other.example/prod/terrabase-member/cluster-a/member-a",
            "spiffe://terrascale.tech/../terrabase-member/cluster-a/member-a",
        ] {
            assert_eq!(
                MemberWorkloadIdentity::parse(invalid).err(),
                Some(ProductionTransportError::InvalidWorkloadIdentity)
            );
        }
    }

    #[test]
    fn certificate_requires_exactly_one_canonical_member_uri_san() {
        let expected = MemberWorkloadIdentity::try_new("prod", "cluster-a", "member-a")
            .expect("canonical identity");
        let valid = certificate_with_uri_sans(&[expected.as_str()]);
        assert_eq!(MemberWorkloadIdentity::from_leaf_certificate(&valid), Ok(expected));

        for invalid in [
            certificate_with_uri_sans(&[]),
            certificate_with_uri_sans(&[
                "spiffe://terrascale.tech/prod/terrabase-member/cluster-a/member-a",
                "spiffe://terrascale.tech/prod/terrabase-member/cluster-a/member-b",
            ]),
            certificate_with_uri_sans(&[
                "spiffe://terrascale.tech/prod/database-gateway/cluster-a/member-a",
            ]),
        ] {
            assert_eq!(
                MemberWorkloadIdentity::from_leaf_certificate(&invalid),
                Err(ProductionTransportError::InvalidPeerCertificate)
            );
        }
    }

    #[test]
    fn only_the_initial_product_region_codes_are_admitted() {
        assert_eq!(ProductionRegion::try_new("GRU"), Ok(ProductionRegion::Gru));
        assert_eq!(ProductionRegion::try_new("SFO"), Ok(ProductionRegion::Sfo));
        assert_eq!(ProductionRegion::try_new("FRA"), Ok(ProductionRegion::Fra));
        for invalid in ["gru", "SJC", "sa-saopaulo-1", "JFK"] {
            assert_eq!(
                ProductionRegion::try_new(invalid),
                Err(ProductionTransportError::InvalidRegion)
            );
        }
    }

    #[test]
    fn hello_round_trip_keeps_membership_fencing_and_nonce() {
        let hello =
            ConnectionHello { nonce: [7; 32], ..ConnectionHello::new(&config()).expect("hello") };
        assert_eq!(ConnectionHello::decode(&hello.encode().expect("encode")), Ok(hello));
    }

    #[test]
    fn application_queue_enforces_frame_and_byte_bounds() {
        let mut queue = ProductionFrameQueue::new(&limits());
        queue.try_push(vec![1; 3000]).expect("first");
        queue.try_push(vec![2; 3000]).expect("second reaches byte bound");
        assert_eq!(queue.try_push(vec![3]).err(), Some(ProductionTransportError::QueueFull));
        assert_eq!(queue.len(), 2);
        assert_eq!(queue.bytes(), 6000);
        assert_eq!(queue.pop().map(|frame| frame.len()), Some(3000));
        assert_eq!(queue.bytes(), 3000);
    }

    #[test]
    fn peer_membership_cannot_cross_cluster_or_environment() {
        let local = MemberWorkloadIdentity::try_new("prod", "cluster-a", "member-a").unwrap();
        for peer_identity in [
            MemberWorkloadIdentity::try_new("stage", "cluster-a", "member-b").unwrap(),
            MemberWorkloadIdentity::try_new("prod", "cluster-b", "member-b").unwrap(),
        ] {
            let peer = ProductionPeerAdmission::try_new(
                peer_identity,
                ProductionRegion::Sfo,
                "127.0.0.1:7444".parse().unwrap(),
                "member-b.internal",
            )
            .unwrap();
            assert_eq!(
                ProductionTransportConfig::try_new(
                    "127.0.0.1:7443".parse().unwrap(),
                    local.clone(),
                    ProductionRegion::Gru,
                    1,
                    1,
                    vec![peer],
                    limits(),
                )
                .err(),
                Some(ProductionTransportError::InvalidConfiguration)
            );
        }
    }

    #[test]
    fn peer_route_requires_a_tls_server_name() {
        let identity = MemberWorkloadIdentity::try_new("prod", "cluster-a", "member-b").unwrap();
        for invalid in ["not a server name", "member/b", "[broken"] {
            assert_eq!(
                ProductionPeerAdmission::try_new(
                    identity.clone(),
                    ProductionRegion::Sfo,
                    "127.0.0.1:7444".parse().unwrap(),
                    invalid,
                ),
                Err(ProductionTransportError::InvalidConfiguration)
            );
        }
    }

    #[tokio::test(flavor = "multi_thread", worker_threads = 2)]
    async fn two_mtls_endpoints_exchange_authenticated_frames() {
        let first_socket = UdpSocket::bind((Ipv4Addr::LOCALHOST, 0)).expect("first port");
        let second_socket = UdpSocket::bind((Ipv4Addr::LOCALHOST, 0)).expect("second port");
        let first_address = first_socket.local_addr().expect("first address");
        let second_address = second_socket.local_addr().expect("second address");
        drop(first_socket);
        drop(second_socket);

        let issuer_key = KeyPair::generate().expect("issuer key");
        let mut issuer_params = CertificateParams::new(Vec::new()).expect("issuer parameters");
        issuer_params.is_ca = IsCa::Ca(BasicConstraints::Unconstrained);
        issuer_params.key_usages =
            vec![KeyUsagePurpose::DigitalSignature, KeyUsagePurpose::KeyCertSign];
        let issuer = issuer_params.self_signed(&issuer_key).expect("issuer certificate");
        let anchor = CertificateDer::from(issuer.der().to_vec());

        let first_identity = MemberWorkloadIdentity::try_new("prod", "cluster-a", "member-a")
            .expect("first identity");
        let second_identity = MemberWorkloadIdentity::try_new("prod", "cluster-a", "member-b")
            .expect("second identity");
        let first_name = "member-a.internal";
        let second_name = "member-b.internal";
        let first_tls =
            member_tls_material(&first_identity, first_name, &issuer, &issuer_key, &anchor);
        let second_tls =
            member_tls_material(&second_identity, second_name, &issuer, &issuer_key, &anchor);

        let limits = ProductionTransportLimits::try_new(
            4096,
            8,
            8,
            32 * 1024,
            Duration::from_secs(5),
            Duration::from_secs(5),
            Duration::from_secs(10),
            Duration::from_secs(5),
        )
        .expect("integration limits");
        let first_config = ProductionTransportConfig::try_new(
            first_address,
            first_identity.clone(),
            ProductionRegion::Gru,
            9,
            12,
            vec![
                ProductionPeerAdmission::try_new(
                    second_identity.clone(),
                    ProductionRegion::Sfo,
                    second_address,
                    second_name,
                )
                .expect("second peer"),
            ],
            limits.clone(),
        )
        .expect("first config");
        let second_config = ProductionTransportConfig::try_new(
            second_address,
            second_identity.clone(),
            ProductionRegion::Sfo,
            9,
            12,
            vec![
                ProductionPeerAdmission::try_new(
                    first_identity.clone(),
                    ProductionRegion::Gru,
                    first_address,
                    first_name,
                )
                .expect("first peer"),
            ],
            limits,
        )
        .expect("second config");

        let first = ProductionQuicEndpoint::bind(first_config, first_tls).expect("first endpoint");
        let second =
            ProductionQuicEndpoint::bind(second_config, second_tls).expect("second endpoint");
        let (outbound, inbound) =
            tokio::join!(first.connect(second_identity.member()), second.accept(),);
        let outbound = outbound.expect("outbound authentication");
        let inbound = inbound.expect("inbound authentication").expect("inbound connection");
        assert_eq!(outbound.peer(), &second_identity);
        assert_eq!(inbound.peer(), &first_identity);
        assert_eq!(first.facts().authenticated_peers, 1);
        assert_eq!(second.facts().authenticated_peers, 1);

        let payload = b"authenticated replication frame";
        let (sent, received) = tokio::join!(outbound.send_frame(payload), inbound.receive_frame());
        sent.expect("frame sent");
        assert_eq!(received.expect("frame received"), payload);

        drop(outbound);
        drop(inbound);
        let (first_drain, second_drain) = tokio::join!(first.drain(), second.drain());
        assert!(first_drain.operations_drained);
        assert!(second_drain.operations_drained);
        assert_eq!(first_drain.remaining_operations, 0);
        assert_eq!(second_drain.remaining_operations, 0);
    }
}
