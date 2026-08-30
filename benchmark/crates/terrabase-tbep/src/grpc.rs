//! The tonic serving layer of TBEP (ADR 0025): the gRPC contract in
//! `proto/terrabase/v1/engine.proto`, served over the hosting seam.
//!
//! Responsibilities, in the order a request meets them:
//!
//! 1. **Identity.** Every RPC except `Channel/Bind` requires the complete
//!    `x-tb-principal`, `x-tb-tenant`, `x-tb-project`, and `x-tb-database`
//!    scope. Missing, duplicated, or invalid metadata fails closed with the
//!    `unbound_channel` error code. Production mTLS authenticates the gateway
//!    principal; metadata carries the immutable database routing scope.
//! 2. **Idempotency.** Mutation RPCs require the
//!    `x-tb-idempotency-key` metadata (1..=128 bytes, no control
//!    characters); the engine deduplicates retries within its retained
//!    horizon.
//! 3. **Bounds.** Prost messages convert into the hand-maintained
//!    domain types of [`crate::registry`], whose `validate` methods
//!    enforce the declared bounds fail-closed before the host runs.
//! 4. **Overload.** One bounded in-flight gate wraps every host call;
//!    refusal answers `RESOURCE_EXHAUSTED` with the `overloaded` error
//!    code.
//! 5. **Errors.** Host failures return a gRPC status whose details
//!    carry one [`crate::proto::terrabase::v1::TbepError`] naming the
//!    closed error code; clients map codes, never string-match
//!    details.
//! 6. **Streams.** `Backbone/Subscribe` opens the subscription on the
//!    host and pumps its feed into the response stream until the close
//!    frame or disconnect.
//!
//! The sync host seam is synchronous, so every host call rides
//! `spawn_blocking`; the tonic runtime never blocks on engine work.

use std::collections::BTreeSet;
use std::net::SocketAddr;
use std::path::Path;
use std::pin::Pin;
use std::sync::Arc;
use std::sync::atomic::AtomicU64;
use std::sync::atomic::AtomicUsize;
use std::sync::atomic::Ordering;
use std::task::{Context, Poll};
use std::time::Duration;
use std::time::Instant;

use prost::Message as _;
use tokio::sync::mpsc;
use tokio_stream::Stream;
use tokio_stream::StreamExt as _;
use tokio_stream::wrappers::ReceiverStream;

use tonic::Code;
use tonic::Request;
use tonic::Response;
use tonic::Status;
use tonic::transport::ServerTlsConfig;
use x509_parser::extensions::GeneralName;
use x509_parser::extensions::ParsedExtension;
use x509_parser::prelude::FromDer;
use x509_parser::prelude::X509Certificate;

use tokio_rustls::TlsAcceptor;
use tokio_rustls::rustls;
use tokio_rustls::rustls::pki_types::{CertificateDer, PrivateKeyDer};

use terrabase::observability::{
    MetricsRegistry, Operation as MetricOperation, Outcome as MetricOutcome, Region as MetricRegion,
};

use crate::host::HostingError;
use crate::host::TbepHost;
use crate::host::TbepNotification;
use crate::proto::terrabase::v1 as pb;
use crate::proto::terrabase::v1::backbone_service_server::BackboneService;
use crate::proto::terrabase::v1::backbone_service_server::BackboneServiceServer;
use crate::proto::terrabase::v1::channel_service_server::ChannelService;
use crate::proto::terrabase::v1::channel_service_server::ChannelServiceServer;
use crate::proto::terrabase::v1::engine_service_server::EngineService;
use crate::proto::terrabase::v1::engine_service_server::EngineServiceServer;
use crate::proto::terrabase::v1::maintenance_service_server::MaintenanceService;
use crate::proto::terrabase::v1::maintenance_service_server::MaintenanceServiceServer;
use crate::proto::terrabase::v1::replication_service_server::ReplicationService;
use crate::proto::terrabase::v1::replication_service_server::ReplicationServiceServer;
use crate::proto::terrabase::v1::schema_service_server::SchemaService;
use crate::proto::terrabase::v1::schema_service_server::SchemaServiceServer;
use crate::proto::terrabase::v1::sync_service_server::SyncService;
use crate::proto::terrabase::v1::sync_service_server::SyncServiceServer;
use crate::proto::terrabase::v1::tier_service_server::TierService;
use crate::proto::terrabase::v1::tier_service_server::TierServiceServer;
use crate::proto::terrabase::v1::usage_service_server::UsageService;
use crate::proto::terrabase::v1::usage_service_server::UsageServiceServer;
use crate::registry;
use crate::value::TbepValue;

/// The metadata key carrying the backend-supplied principal.
pub const PRINCIPAL_METADATA: &str = "x-tb-principal";
/// The metadata key carrying the backend-supplied tenant.
pub const TENANT_METADATA: &str = "x-tb-tenant";
/// The metadata key carrying the bound Project identity.
pub const PROJECT_METADATA: &str = "x-tb-project";
/// The metadata key carrying the bound Database identity.
pub const DATABASE_METADATA: &str = "x-tb-database";
/// The metadata key carrying a mutation's retry identity.
pub const IDEMPOTENCY_METADATA: &str = "x-tb-idempotency-key";

/// The idempotency-key bound in bytes.
pub const MAX_IDEMPOTENCY_BYTES: usize = 128;

/// The maximum UTF-8 byte length of the principal metadata value.
pub const MAX_PRINCIPAL_BYTES: usize = 128;

/// The maximum UTF-8 byte length of the tenant metadata value.
pub const MAX_TENANT_BYTES: usize = 64;
/// The maximum UTF-8 byte length of Project and Database identities.
pub const MAX_SCOPE_ID_BYTES: usize = 255;

/// Default maximum in-flight host operations admitted by the overload gate.
pub const MAX_INFLIGHT_OPERATIONS: usize = 128;

/// The depth of one subscription's outbound queue: a bounded backlog
/// that keeps a slow stream consumer from pinning engine memory.
pub const OUTBOUND_DEPTH: usize = 64;

const MAX_SYNC_TOKEN_BYTES: usize = 1057;
const MAX_PEER_CERT_BYTES: usize = 1 << 20;

/// Validated resource and authenticated-gateway policy for one serving
/// instance. Gateway names are exact SPIFFE URI SAN values.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct TbepGrpcConfig {
    max_message_bytes: usize,
    max_inflight: usize,
    request_timeout: Duration,
    tls_handshake_timeout: Duration,
    shutdown_grace: Duration,
    gateway_principals: BTreeSet<String>,
}

impl TbepGrpcConfig {
    /// Creates a validated serving policy.
    ///
    /// # Errors
    ///
    /// Returns `InvalidInput` when a bound is zero, outside the protocol
    /// ceiling, or a gateway principal is outside its metadata bound.
    pub fn try_new(
        max_message_bytes: usize,
        max_inflight: usize,
        request_timeout: Duration,
        tls_handshake_timeout: Duration,
        shutdown_grace: Duration,
        gateway_principals: BTreeSet<String>,
    ) -> std::io::Result<Self> {
        let valid_principals =
            gateway_principals.iter().all(|principal| valid_gateway_spiffe_uri(principal));
        if !(1024..=registry::MAX_FRAME_BYTES).contains(&max_message_bytes)
            || !(1..=4096).contains(&max_inflight)
            || request_timeout.is_zero()
            || tls_handshake_timeout.is_zero()
            || shutdown_grace.is_zero()
            || !valid_principals
        {
            return Err(std::io::Error::new(
                std::io::ErrorKind::InvalidInput,
                "the TBEP serving policy is outside its bounds",
            ));
        }
        Ok(Self {
            max_message_bytes,
            max_inflight,
            request_timeout,
            tls_handshake_timeout,
            shutdown_grace,
            gateway_principals,
        })
    }

    /// Returns the configured message bound in bytes.
    #[must_use]
    pub const fn max_message_bytes(&self) -> usize {
        self.max_message_bytes
    }

    /// Returns the global and per-connection in-flight bound.
    #[must_use]
    pub const fn max_inflight(&self) -> usize {
        self.max_inflight
    }

    /// Returns the host-call deadline.
    #[must_use]
    pub const fn request_timeout(&self) -> Duration {
        self.request_timeout
    }

    /// Returns the TLS handshake deadline.
    #[must_use]
    pub const fn tls_handshake_timeout(&self) -> Duration {
        self.tls_handshake_timeout
    }

    /// Returns the listener drain deadline.
    #[must_use]
    pub const fn shutdown_grace(&self) -> Duration {
        self.shutdown_grace
    }
}

/// Returns whether `value` is one canonical Database-gateway workload
/// identity admitted by ADR 0025.
#[must_use]
pub fn valid_gateway_spiffe_uri(value: &str) -> bool {
    const PREFIX: &str = "spiffe://terrascale.tech/";
    const GATEWAY_SEGMENT: &str = "/database-gateway/";

    if value.len() > MAX_PRINCIPAL_BYTES || value != value.trim() {
        return false;
    }
    let Some(remainder) = value.strip_prefix(PREFIX) else {
        return false;
    };
    let Some((environment, instance)) = remainder.split_once(GATEWAY_SEGMENT) else {
        return false;
    };
    valid_environment(environment)
        && !instance.contains('/')
        && terrabase::identity::Ulid::from_text(instance).is_ok()
}

fn valid_environment(value: &str) -> bool {
    !value.is_empty()
        && value.len() <= 32
        && !value.starts_with('-')
        && !value.ends_with('-')
        && value
            .bytes()
            .all(|byte| byte.is_ascii_lowercase() || byte.is_ascii_digit() || byte == b'-')
}

impl Default for TbepGrpcConfig {
    fn default() -> Self {
        Self {
            max_message_bytes: registry::MAX_FRAME_BYTES,
            max_inflight: MAX_INFLIGHT_OPERATIONS,
            request_timeout: Duration::from_secs(30),
            tls_handshake_timeout: Duration::from_secs(5),
            shutdown_grace: Duration::from_secs(5),
            gateway_principals: BTreeSet::new(),
        }
    }
}

#[cfg(unix)]
struct SameUidUnixIncoming {
    inner: tokio_stream::wrappers::UnixListenerStream,
    owner_uid: u32,
}

#[cfg(unix)]
impl Stream for SameUidUnixIncoming {
    type Item = std::io::Result<tokio::net::UnixStream>;

    fn poll_next(mut self: Pin<&mut Self>, context: &mut Context<'_>) -> Poll<Option<Self::Item>> {
        loop {
            match Pin::new(&mut self.inner).poll_next(context) {
                Poll::Ready(Some(Ok(stream))) => match stream.peer_cred() {
                    Ok(credentials) if credentials.uid() == self.owner_uid => {
                        return Poll::Ready(Some(Ok(stream)));
                    }
                    Ok(_) => continue,
                    Err(error) => return Poll::Ready(Some(Err(error))),
                },
                other => return other,
            }
        }
    }
}

/// The running gRPC listener handle.
pub struct TbepGrpcHandle {
    local_addr: Option<SocketAddr>,
    socket_path: Option<std::path::PathBuf>,
    socket_identity: Option<(u64, u64)>,
    shutdown: Option<tokio::sync::oneshot::Sender<()>>,
    completion: Option<std::sync::mpsc::Receiver<std::io::Result<()>>>,
    shutdown_grace: Duration,
    inflight: Arc<AtomicUsize>,
    draining: Arc<std::sync::atomic::AtomicBool>,
}

impl TbepGrpcHandle {
    /// The bound TCP address, when serving TLS.
    #[must_use]
    pub fn local_addr(&self) -> Option<SocketAddr> {
        self.local_addr
    }

    /// The bound socket path, when serving UDS.
    #[must_use]
    pub fn socket_path(&self) -> Option<&Path> {
        self.socket_path.as_deref()
    }

    /// Reports a listener failure without blocking. Returns `true` after the
    /// serving task has completed.
    ///
    /// # Errors
    ///
    /// Returns the listener task's serving failure.
    pub fn try_wait(&mut self) -> std::io::Result<bool> {
        let Some(completion) = self.completion.as_ref() else {
            return Ok(true);
        };
        match completion.try_recv() {
            Ok(result) => {
                self.completion = None;
                self.cleanup_socket();
                result.map(|()| true)
            }
            Err(std::sync::mpsc::TryRecvError::Empty) => Ok(false),
            Err(std::sync::mpsc::TryRecvError::Disconnected) => {
                self.completion = None;
                self.cleanup_socket();
                Err(std::io::Error::other("the TBEP serving task stopped without a result"))
            }
        }
    }

    /// Signals the serving loop and waits for the configured graceful-drain
    /// deadline.
    ///
    /// # Errors
    ///
    /// Returns the serving failure or `TimedOut` when drain exceeds the bound.
    pub fn shutdown_and_wait(&mut self) -> std::io::Result<()> {
        if let Some(sender) = self.shutdown.take() {
            let _ = sender.send(());
        }
        let Some(completion) = self.completion.as_ref() else {
            self.cleanup_socket();
            return Ok(());
        };
        let deadline = Instant::now() + self.shutdown_grace;
        match completion.recv_timeout(self.shutdown_grace) {
            Ok(result) => {
                self.completion = None;
                self.cleanup_socket();
                result.and_then(|()| self.wait_for_inflight(deadline))
            }
            Err(std::sync::mpsc::RecvTimeoutError::Timeout) => Err(std::io::Error::new(
                std::io::ErrorKind::TimedOut,
                "the TBEP listener exceeded its graceful shutdown deadline",
            )),
            Err(std::sync::mpsc::RecvTimeoutError::Disconnected) => {
                self.completion = None;
                self.cleanup_socket();
                Err(std::io::Error::other("the TBEP serving task stopped without a result"))
            }
        }
    }

    fn wait_for_inflight(&self, deadline: Instant) -> std::io::Result<()> {
        while self.inflight.load(Ordering::Acquire) != 0 {
            let now = Instant::now();
            if now >= deadline {
                return Err(std::io::Error::new(
                    std::io::ErrorKind::TimedOut,
                    "the TBEP engine work exceeded its graceful shutdown deadline",
                ));
            }
            std::thread::sleep((deadline - now).min(Duration::from_millis(5)));
        }
        Ok(())
    }

    /// Stops new RPC admission before listener shutdown begins.
    pub fn begin_drain(&self) {
        self.draining.store(true, Ordering::Release);
    }

    /// Signals shutdown and discards the listener result. Production callers
    /// should use [`Self::shutdown_and_wait`].
    pub fn shutdown(&mut self) {
        let _ = self.shutdown_and_wait();
    }

    fn cleanup_socket(&mut self) {
        let (Some(path), Some((device, inode))) = (&self.socket_path, self.socket_identity) else {
            return;
        };
        #[cfg(unix)]
        {
            use std::os::unix::fs::FileTypeExt as _;
            use std::os::unix::fs::MetadataExt as _;

            if let Ok(metadata) = std::fs::symlink_metadata(path)
                && metadata.file_type().is_socket()
                && metadata.dev() == device
                && metadata.ino() == inode
            {
                let _ = std::fs::remove_file(path);
            }
        }
        self.socket_identity = None;
    }
}

impl Drop for TbepGrpcHandle {
    fn drop(&mut self) {
        self.shutdown();
    }
}

/// The gRPC serving of one host: every service of the contract wired
/// over [`TbepHost`].
pub struct TbepGrpcServer {
    host: Arc<dyn TbepHost>,
    binding: pb::BindingKind,
    config: TbepGrpcConfig,
    metrics: Arc<MetricsRegistry>,
    region: MetricRegion,
    frames_served: AtomicU64,
    inflight: Arc<AtomicUsize>,
    draining: Arc<std::sync::atomic::AtomicBool>,
}

impl TbepGrpcServer {
    /// Creates the serving over one host. `binding` is the transport
    /// this instance serves (local socket or mTLS network) and answers
    /// in topology reads.
    #[must_use]
    pub fn new(host: Arc<dyn TbepHost>, binding: pb::BindingKind) -> Self {
        Self::with_metrics(host, binding, MetricRegion::Unknown, Arc::new(MetricsRegistry::new()))
    }

    /// Creates a server that records requests into the process registry owned
    /// by the production composition boundary.
    #[must_use]
    pub fn with_metrics(
        host: Arc<dyn TbepHost>,
        binding: pb::BindingKind,
        region: MetricRegion,
        metrics: Arc<MetricsRegistry>,
    ) -> Self {
        Self::with_config_and_metrics(host, binding, TbepGrpcConfig::default(), region, metrics)
    }

    /// Creates a server with an explicit validated serving and gateway
    /// policy.
    #[must_use]
    pub fn with_config_and_metrics(
        host: Arc<dyn TbepHost>,
        binding: pb::BindingKind,
        config: TbepGrpcConfig,
        region: MetricRegion,
        metrics: Arc<MetricsRegistry>,
    ) -> Self {
        Self {
            host,
            binding,
            config,
            metrics,
            region,
            frames_served: AtomicU64::new(0),
            inflight: Arc::new(AtomicUsize::new(0)),
            draining: Arc::new(std::sync::atomic::AtomicBool::new(false)),
        }
    }

    /// Returns the same bounded registry used by the request path so the
    /// private metrics adapter can render it without a second collector.
    #[must_use]
    pub fn metrics(&self) -> Arc<MetricsRegistry> {
        Arc::clone(&self.metrics)
    }

    /// The frames (RPCs) served so far.
    #[must_use]
    pub fn frames_served(&self) -> u64 {
        self.frames_served.load(Ordering::Relaxed)
    }

    fn counted(&self) {
        self.frames_served.fetch_add(1, Ordering::Relaxed);
    }

    /// Stops new host work from being admitted while existing work drains.
    pub fn begin_drain(&self) {
        self.draining.store(true, Ordering::Release);
    }

    fn identity<T>(&self, request: &Request<T>) -> Result<Identity, Status> {
        let identity = Identity::of(request)?;
        if self.binding == pb::BindingKind::NetworkMtls {
            self.authenticate_gateway(request, &identity.principal)?;
        }
        Ok(identity)
    }

    fn authenticate_gateway<T>(&self, request: &Request<T>, principal: &str) -> Result<(), Status> {
        if !self.config.gateway_principals.contains(principal) {
            return Err(tbep_status(
                Code::PermissionDenied,
                pb::ErrorCode::OperationRefused,
                "the authenticated client is not an admitted gateway principal",
            ));
        }
        let certificates = request.peer_certs().ok_or_else(|| {
            tbep_status(
                Code::Unauthenticated,
                pb::ErrorCode::UnboundChannel,
                "the network RPC carries no authenticated client certificate",
            )
        })?;
        let leaf = certificates.first().ok_or_else(|| {
            tbep_status(
                Code::Unauthenticated,
                pb::ErrorCode::UnboundChannel,
                "the network RPC carries no authenticated client certificate",
            )
        })?;
        if leaf.len() > MAX_PEER_CERT_BYTES {
            return Err(tbep_status(
                Code::Unauthenticated,
                pb::ErrorCode::UnboundChannel,
                "the authenticated client certificate exceeds its bound",
            ));
        }
        let (_, certificate) = X509Certificate::from_der(leaf.as_ref()).map_err(|_| {
            tbep_status(
                Code::Unauthenticated,
                pb::ErrorCode::UnboundChannel,
                "the authenticated client certificate is not parseable",
            )
        })?;
        let mut uri_sans = Vec::new();
        for extension in certificate.extensions() {
            let ParsedExtension::SubjectAlternativeName(names) = extension.parsed_extension()
            else {
                continue;
            };
            for name in &names.general_names {
                if let GeneralName::URI(uri) = name {
                    if uri.len() > MAX_PRINCIPAL_BYTES {
                        return Err(tbep_status(
                            Code::PermissionDenied,
                            pb::ErrorCode::OperationRefused,
                            "the client certificate URI SAN exceeds its bound",
                        ));
                    }
                    uri_sans.push(*uri);
                }
            }
        }
        if uri_sans.len() != 1 || uri_sans[0] != principal {
            return Err(tbep_status(
                Code::PermissionDenied,
                pb::ErrorCode::OperationRefused,
                "the request principal does not match exactly one authenticated SPIFFE URI SAN",
            ));
        }
        Ok(())
    }

    fn require_mutation_usage(&self, present: bool) -> Result<(), Status> {
        match (self.binding, present) {
            (pb::BindingKind::NetworkMtls, true) | (pb::BindingKind::Local, false) => Ok(()),
            (pb::BindingKind::NetworkMtls, false) => Err(tbep_status(
                Code::FailedPrecondition,
                pb::ErrorCode::UsageUnavailable,
                "a production network mutation requires trusted usage admission",
            )),
            (pb::BindingKind::Local, true) => Err(tbep_status(
                Code::FailedPrecondition,
                pb::ErrorCode::OperationRefused,
                "a local mutation cannot mint authoritative usage",
            )),
            _ => Err(tbep_status(
                Code::FailedPrecondition,
                pb::ErrorCode::OperationRefused,
                "the serving binding is not configured",
            )),
        }
    }

    fn require_usage_rpc<T>(&self, request: &Request<T>) -> Result<Identity, Status> {
        if self.binding != pb::BindingKind::NetworkMtls {
            return Err(tbep_status(
                Code::FailedPrecondition,
                pb::ErrorCode::OperationRefused,
                "authoritative usage RPCs require the production mTLS binding",
            ));
        }
        self.identity(request)
    }

    /// Serves the contract over a local Unix-domain socket.
    ///
    /// Every existing path at `path` is refused. This deliberately avoids
    /// unlinking a socket that may still be owned by another listener; the
    /// mode of a newly bound socket is the process umask's and the directory
    /// holding it is expected to be operator-restricted.
    ///
    /// # Errors
    ///
    /// Returns the bind failure.
    ///
    /// # Panics
    ///
    /// Panics only if the serving runtime fails to start on the dedicated
    /// thread (an operator-level environment failure); bind failures are
    /// returned.
    pub fn serve_uds(self: &Arc<Self>, path: &Path) -> std::io::Result<TbepGrpcHandle> {
        if self.binding != pb::BindingKind::Local {
            return Err(std::io::Error::new(
                std::io::ErrorKind::InvalidInput,
                "a UDS listener requires the local binding kind",
            ));
        }
        // Never unlink an arbitrary path supplied by the operator. Even a
        // socket-shaped entry may be active, and checking then unlinking is
        // inherently racy. Binding itself remains the final race-safe check.
        match std::fs::symlink_metadata(path) {
            Ok(_) => {
                return Err(std::io::Error::new(
                    std::io::ErrorKind::AlreadyExists,
                    "the UDS path already exists; refusing to unlink it",
                ));
            }
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {}
            Err(error) => return Err(error),
        }
        let server = Arc::clone(self);
        let (shutdown, signal) = tokio::sync::oneshot::channel::<()>();
        let runtime = tokio::runtime::Builder::new_multi_thread()
            .worker_threads(2)
            .enable_all()
            .build()
            .map_err(std::io::Error::other)?;
        let _context = runtime.enter();
        // The listener binds before the handle returns, so a client that
        // connects immediately after serving starts cannot race the socket.
        let listener = tokio::net::UnixListener::bind(path)?;
        #[cfg(unix)]
        let (owner_uid, socket_identity) = {
            use std::os::unix::fs::FileTypeExt as _;
            use std::os::unix::fs::MetadataExt as _;

            let metadata = std::fs::symlink_metadata(path)?;
            if !metadata.file_type().is_socket() {
                return Err(std::io::Error::other("the bound UDS path is not a socket"));
            }
            (metadata.uid(), (metadata.dev(), metadata.ino()))
        };
        #[cfg(unix)]
        let incoming = SameUidUnixIncoming {
            inner: tokio_stream::wrappers::UnixListenerStream::new(listener),
            owner_uid,
        };
        let (done, completion) = std::sync::mpsc::channel::<std::io::Result<()>>();
        std::thread::Builder::new()
            .name("tbep-grpc-uds".into())
            .spawn(move || {
                let result = runtime.block_on(async move {
                    let routes =
                        serve_routes(Arc::clone(&server), None).map_err(std::io::Error::other)?;
                    routes
                        .serve_with_incoming_shutdown(incoming, async {
                            let _ = signal.await;
                        })
                        .await
                        .map_err(std::io::Error::other)
                });
                let _ = done.send(result.map_err(std::io::Error::other));
            })
            .map_err(std::io::Error::other)?;
        Ok(TbepGrpcHandle {
            local_addr: None,
            socket_path: Some(path.to_path_buf()),
            socket_identity: Some(socket_identity),
            shutdown: Some(shutdown),
            completion: Some(completion),
            shutdown_grace: self.config.shutdown_grace(),
            inflight: Arc::clone(&self.inflight),
            draining: Arc::clone(&self.draining),
        })
    }

    /// Serves the contract over TLS on `address`.
    ///
    /// `server_pem` is the leaf-first certificate chain and `key_pem`
    /// its private key, both PEM-encoded. `client_ca_pem` is the
    /// operator-provisioned client trust bundle; when present, clients
    /// must present a certificate chaining to it (mutual TLS).
    ///
    /// # Errors
    ///
    /// Returns the TLS configuration or bind failure.
    ///
    /// # Panics
    ///
    /// Panics only if the serving runtime fails to start on the dedicated
    /// thread (an operator-level environment failure); TLS and bind
    /// failures are returned.
    pub fn serve_tls(
        self: &Arc<Self>,
        address: SocketAddr,
        server_pem: &str,
        key_pem: &str,
        client_ca_pem: Option<&str>,
    ) -> std::io::Result<TbepGrpcHandle> {
        if self.binding != pb::BindingKind::NetworkMtls {
            return Err(std::io::Error::new(
                std::io::ErrorKind::InvalidInput,
                "a TLS listener requires the network mTLS binding kind",
            ));
        }
        if self.config.gateway_principals.is_empty() {
            return Err(std::io::Error::new(
                std::io::ErrorKind::InvalidInput,
                "a production TLS listener requires an authenticated gateway mapping",
            ));
        }
        let Some(client_ca_pem) = client_ca_pem.filter(|bundle| !bundle.trim().is_empty()) else {
            return Err(std::io::Error::new(
                std::io::ErrorKind::InvalidInput,
                "production TLS requires a non-empty client CA bundle for mutual authentication",
            ));
        };
        let tls_config = tls_server_config(server_pem, key_pem, client_ca_pem)?;
        let tls_acceptor = TlsAcceptor::from(tls_config);
        let server = Arc::clone(self);
        let startup_timeout = self.config.tls_handshake_timeout();
        let (shutdown, signal) = tokio::sync::oneshot::channel::<()>();
        let (addr_tx, addr_rx) = std::sync::mpsc::channel::<std::io::Result<SocketAddr>>();
        let (done, completion) = std::sync::mpsc::channel::<std::io::Result<()>>();
        std::thread::Builder::new()
            .name("tbep-grpc-tls".into())
            .spawn(move || {
                let runtime = tokio::runtime::Builder::new_multi_thread()
                    .worker_threads(2)
                    .enable_all()
                    .build()
                    .expect("the TBEP serving runtime starts");
                let _context = runtime.enter();
                let result = runtime.block_on(async move {
                    let listener = match tokio::net::TcpListener::bind(address).await {
                        Ok(listener) => listener,
                        Err(error) => {
                            let _ = addr_tx
                                .send(Err(std::io::Error::new(error.kind(), error.to_string())));
                            return Err(error);
                        }
                    };
                    let local = match listener.local_addr() {
                        Ok(local) => local,
                        Err(error) => {
                            let _ = addr_tx
                                .send(Err(std::io::Error::new(error.kind(), error.to_string())));
                            return Err(error);
                        }
                    };
                    let routes = match serve_routes(Arc::clone(&server), None) {
                        Ok(routes) => routes,
                        Err(error) => {
                            let error = std::io::Error::other(error);
                            let _ = addr_tx
                                .send(Err(std::io::Error::new(error.kind(), error.to_string())));
                            return Err(error);
                        }
                    };
                    let _ = addr_tx.send(Ok(local));
                    // `then` would serialize handshakes: a client that sends
                    // only a TCP preface could hold the accept loop until the
                    // timeout and starve every well-behaved client behind it.
                    // `buffer_unordered` keeps at most the configured number
                    // of handshake futures alive while continuing to poll the
                    // listener whenever a slot is available.  The bound is
                    // deliberately the same admission ceiling used by host
                    // work, so this path cannot become an unbounded task or
                    // socket queue.
                    let handshake_limit = server.config.max_inflight();
                    let incoming = tokio_stream::wrappers::TcpListenerStream::new(listener).map(
                        move |accepted| {
                            let acceptor = tls_acceptor.clone();
                            async move {
                                let stream = accepted?;
                                match tokio::time::timeout(startup_timeout, acceptor.accept(stream))
                                    .await
                                {
                                    Ok(Ok(stream)) => Ok(stream),
                                    Ok(Err(error)) => Err(std::io::Error::other(error)),
                                    Err(_) => Err(std::io::Error::new(
                                        std::io::ErrorKind::TimedOut,
                                        "the TLS handshake exceeded its configured deadline",
                                    )),
                                }
                            }
                        },
                    );
                    let incoming =
                        futures_util::StreamExt::buffer_unordered(incoming, handshake_limit);
                    routes
                        .serve_with_incoming_shutdown(incoming, async {
                            let _ = signal.await;
                        })
                        .await
                        .map_err(std::io::Error::other)
                });
                let _ = done.send(result);
            })
            .map_err(std::io::Error::other)?;
        let local_addr = match addr_rx.recv_timeout(startup_timeout) {
            Ok(Ok(local_addr)) => local_addr,
            Ok(Err(error)) => {
                let _ = completion.recv_timeout(std::time::Duration::from_secs(5));
                return Err(error);
            }
            Err(std::sync::mpsc::RecvTimeoutError::Timeout) => {
                let _ = completion.recv_timeout(std::time::Duration::from_secs(5));
                return Err(std::io::Error::new(
                    std::io::ErrorKind::TimedOut,
                    "the TLS listener did not report startup before its deadline",
                ));
            }
            Err(std::sync::mpsc::RecvTimeoutError::Disconnected) => {
                let _ = completion.recv_timeout(std::time::Duration::from_secs(5));
                return Err(std::io::Error::other("the TLS listener stopped during startup"));
            }
        };
        if local_addr.port() == 0 {
            let _ = completion.recv_timeout(std::time::Duration::from_secs(5));
            return Err(std::io::Error::new(
                std::io::ErrorKind::AddrNotAvailable,
                "the TLS listener reported an invalid bound address",
            ));
        }
        Ok(TbepGrpcHandle {
            local_addr: Some(local_addr),
            socket_path: None,
            socket_identity: None,
            shutdown: Some(shutdown),
            completion: Some(completion),
            shutdown_grace: self.config.shutdown_grace(),
            inflight: Arc::clone(&self.inflight),
            draining: Arc::clone(&self.draining),
        })
    }
}

fn tls_server_config(
    server_pem: &str,
    key_pem: &str,
    client_ca_pem: &str,
) -> std::io::Result<Arc<rustls::ServerConfig>> {
    let mut server_reader = std::io::BufReader::new(server_pem.as_bytes());
    let certificates = rustls_pemfile::certs(&mut server_reader)
        .collect::<Result<Vec<CertificateDer<'static>>, _>>()
        .map_err(std::io::Error::other)?;
    if certificates.is_empty() {
        return Err(std::io::Error::new(
            std::io::ErrorKind::InvalidInput,
            "the TLS server certificate chain is empty",
        ));
    }
    let mut key_reader = std::io::BufReader::new(key_pem.as_bytes());
    let key = rustls_pemfile::private_key(&mut key_reader)
        .map_err(std::io::Error::other)?
        .ok_or_else(|| {
            std::io::Error::new(std::io::ErrorKind::InvalidInput, "the TLS private key is empty")
        })?;

    let mut roots = rustls::RootCertStore::empty();
    let mut ca_reader = std::io::BufReader::new(client_ca_pem.as_bytes());
    let ca_certificates = rustls_pemfile::certs(&mut ca_reader)
        .collect::<Result<Vec<CertificateDer<'static>>, _>>()
        .map_err(std::io::Error::other)?;
    if ca_certificates.is_empty() {
        return Err(std::io::Error::new(
            std::io::ErrorKind::InvalidInput,
            "the TLS client CA bundle is empty",
        ));
    }
    for certificate in ca_certificates {
        roots.add(certificate).map_err(std::io::Error::other)?;
    }

    let provider = Arc::new(rustls::crypto::ring::default_provider());
    let verifier = rustls::server::WebPkiClientVerifier::builder_with_provider(
        Arc::new(roots),
        Arc::clone(&provider),
    )
    .build()
    .map_err(std::io::Error::other)?;
    let mut config = rustls::ServerConfig::builder_with_provider(provider)
        .with_protocol_versions(&[&rustls::version::TLS13])
        .map_err(std::io::Error::other)?
        .with_client_cert_verifier(verifier)
        .with_single_cert(certificates, PrivateKeyDer::from(key))
        .map_err(std::io::Error::other)?;
    // tonic speaks HTTP/2 only; advertise the protocol during the TLS
    // handshake so grpc-go's ALPN enforcement can authenticate the peer.
    config.alpn_protocols = vec![b"h2".to_vec()];
    Ok(Arc::new(config))
}

fn serve_routes(
    server: Arc<TbepGrpcServer>,
    tls: Option<ServerTlsConfig>,
) -> Result<tonic::transport::server::Router, tonic::transport::Error> {
    let message_bytes = server.config.max_message_bytes();
    let max_inflight = server.config.max_inflight();
    let max_streams = u32::try_from(max_inflight).unwrap_or(u32::MAX);
    let mut builder = tonic::transport::Server::builder()
        .concurrency_limit_per_connection(max_inflight)
        .max_concurrent_streams(max_streams)
        .http2_max_pending_accept_reset_streams(Some(max_inflight))
        .load_shed(true)
        .timeout(server.config.request_timeout());
    if let Some(tls) = tls {
        builder = builder.tls_config(tls)?;
    }
    Ok(builder
        .add_service(
            ChannelServiceServer::new(Arc::clone(&server))
                .max_decoding_message_size(message_bytes)
                .max_encoding_message_size(message_bytes),
        )
        .add_service(
            EngineServiceServer::new(Arc::clone(&server))
                .max_decoding_message_size(message_bytes)
                .max_encoding_message_size(message_bytes),
        )
        .add_service(
            BackboneServiceServer::new(Arc::clone(&server))
                .max_decoding_message_size(message_bytes)
                .max_encoding_message_size(message_bytes),
        )
        .add_service(
            SyncServiceServer::new(Arc::clone(&server))
                .max_decoding_message_size(message_bytes)
                .max_encoding_message_size(message_bytes),
        )
        .add_service(
            SchemaServiceServer::new(Arc::clone(&server))
                .max_decoding_message_size(message_bytes)
                .max_encoding_message_size(message_bytes),
        )
        .add_service(
            ReplicationServiceServer::new(Arc::clone(&server))
                .max_decoding_message_size(message_bytes)
                .max_encoding_message_size(message_bytes),
        )
        .add_service(
            MaintenanceServiceServer::new(Arc::clone(&server))
                .max_decoding_message_size(message_bytes)
                .max_encoding_message_size(message_bytes),
        )
        .add_service(
            UsageServiceServer::new(Arc::clone(&server))
                .max_decoding_message_size(message_bytes)
                .max_encoding_message_size(message_bytes),
        )
        .add_service(
            TierServiceServer::new(server)
                .max_decoding_message_size(message_bytes)
                .max_encoding_message_size(message_bytes),
        ))
}

// ---------------------------------------------------------------------------
// Serving discipline: identity, idempotency, overload, errors
// ---------------------------------------------------------------------------

/// Releases one overload-gate slot when dropped.
struct Permit(Arc<AtomicUsize>);

impl Drop for Permit {
    fn drop(&mut self) {
        self.0.fetch_sub(1, Ordering::AcqRel);
    }
}

/// The application identity one RPC carries.
struct Identity {
    principal: String,
    tenant: registry::BoundScope,
}

impl Identity {
    fn of<T>(request: &Request<T>) -> Result<Self, Status> {
        let metadata = request.metadata();
        let principal = metadata_value(metadata, PRINCIPAL_METADATA, MAX_PRINCIPAL_BYTES)?;
        let tenant_name = metadata_value(metadata, TENANT_METADATA, MAX_TENANT_BYTES)?;
        let project = metadata_value(metadata, PROJECT_METADATA, MAX_SCOPE_ID_BYTES)?;
        let database = metadata_value(metadata, DATABASE_METADATA, MAX_SCOPE_ID_BYTES)?;
        let tenant = registry::BoundScope {
            principal: principal.clone(),
            tenant: tenant_name,
            project,
            database,
        };
        tenant.validate().map_err(|_| {
            tbep_status(
                Code::Unauthenticated,
                pb::ErrorCode::UnboundChannel,
                "the RPC bound scope is outside its validated form",
            )
        })?;
        Ok(Self { principal, tenant })
    }
}

fn metadata_value(
    metadata: &tonic::metadata::MetadataMap,
    name: &'static str,
    bound: usize,
) -> Result<String, Status> {
    let mut values = metadata.get_all(name).iter();
    let value = values.next().ok_or_else(|| {
        tbep_status(
            Code::Unauthenticated,
            pb::ErrorCode::UnboundChannel,
            "the RPC carries no bound channel identity",
        )
    })?;
    if values.next().is_some() {
        return Err(tbep_status(
            Code::Unauthenticated,
            pb::ErrorCode::UnboundChannel,
            "the RPC carries duplicate identity metadata",
        ));
    }
    let value = value.to_str().map_err(|_| {
        tbep_status(
            Code::Unauthenticated,
            pb::ErrorCode::UnboundChannel,
            "the RPC identity metadata is not valid UTF-8",
        )
    })?;
    if value.is_empty() || value.len() > bound || value.bytes().any(|byte| byte.is_ascii_control())
    {
        return Err(tbep_status(
            Code::Unauthenticated,
            pb::ErrorCode::UnboundChannel,
            "the RPC identity metadata is outside its bound",
        ));
    }
    Ok(value.to_owned())
}

fn idempotency_key<T>(request: &Request<T>) -> Result<String, Status> {
    let pairing_error = |detail: &str| {
        tbep_status(Code::InvalidArgument, pb::ErrorCode::IdempotencyPairing, detail)
    };
    let mut values = request.metadata().get_all(IDEMPOTENCY_METADATA).iter();
    let Some(value) = values.next() else {
        return Err(pairing_error("the mutation RPC carries no idempotency key"));
    };
    if values.next().is_some() {
        return Err(pairing_error("the mutation RPC carries duplicate idempotency keys"));
    }
    let key = value.to_str().unwrap_or_default();
    if key.is_empty() || key.trim().is_empty() {
        return Err(pairing_error("the mutation RPC carries no idempotency key"));
    }
    if key.len() > MAX_IDEMPOTENCY_BYTES {
        return Err(pairing_error("the idempotency key exceeds its bound"));
    }
    if key.bytes().any(|byte| byte.is_ascii_control()) {
        return Err(pairing_error("the idempotency key carries control characters"));
    }
    Ok(key.to_string())
}

/// Builds the engine-failure status: the closed code in a `TbepError`
/// packed as the status's `google.rpc.Status` detail.
fn tbep_status(code: Code, error_code: pb::ErrorCode, detail: &str) -> Status {
    let detail = truncate_utf8(detail, 512);
    let tbep_error = pb::TbepError { code: error_code as i32, detail: detail.clone() };
    let mut value = Vec::new();
    tbep_error.encode(&mut value).expect("the error detail encodes");
    let any = pb::Any { type_url: "type.googleapis.com/terrabase.v1.TbepError".into(), value };
    let rpc_status =
        pb::RpcStatus { code: code as i32, message: detail.clone(), details: vec![any] };
    let mut details = Vec::new();
    rpc_status.encode(&mut details).expect("the status envelope encodes");
    Status::with_details(code, detail, details.into())
}

fn truncate_utf8(value: &str, max_bytes: usize) -> String {
    let mut end = value.len().min(max_bytes);
    while end > 0 && !value.is_char_boundary(end) {
        end -= 1;
    }
    value[..end].to_owned()
}

#[allow(clippy::needless_pass_by_value)] // map_err hands over ownership
fn host_error(error: HostingError) -> Status {
    let code = match error {
        HostingError::Invalid(_) => Code::InvalidArgument,
        HostingError::EngineFailure(_) => Code::Internal,
        HostingError::UsageUnavailable(_) => Code::Unavailable,
        HostingError::UsageCorrupt(_) => Code::DataLoss,
        HostingError::Refused(_)
        | HostingError::SessionRefused(_)
        | HostingError::SubscriptionRefused(_)
        | HostingError::CheckpointRefused(_)
        | HostingError::ClusterReadOnly(_)
        | HostingError::ClusterImmutable(_) => Code::FailedPrecondition,
    };
    let error_code = match error.error_code() {
        registry::ErrorCode::UnsupportedVersion => pb::ErrorCode::UnsupportedVersion,
        registry::ErrorCode::MalformedFrame => pb::ErrorCode::MalformedRequest,
        registry::ErrorCode::UnknownFrameKind | registry::ErrorCode::UnknownOperation => {
            pb::ErrorCode::UnknownOperation
        }
        registry::ErrorCode::DigestMismatch => pb::ErrorCode::DigestMismatch,
        registry::ErrorCode::IdempotencyPairing => pb::ErrorCode::IdempotencyPairing,
        registry::ErrorCode::UnboundChannel => pb::ErrorCode::UnboundChannel,
        registry::ErrorCode::AlreadyBound => pb::ErrorCode::AlreadyBound,
        registry::ErrorCode::InvalidRequest => pb::ErrorCode::InvalidRequest,
        registry::ErrorCode::OperationRefused => pb::ErrorCode::OperationRefused,
        registry::ErrorCode::EngineFailure => pb::ErrorCode::EngineFailure,
        registry::ErrorCode::SessionRefused => pb::ErrorCode::SessionRefused,
        registry::ErrorCode::SubscriptionRefused => pb::ErrorCode::SubscriptionRefused,
        registry::ErrorCode::CheckpointRefused => pb::ErrorCode::CheckpointRefused,
        registry::ErrorCode::ShuttingDown => pb::ErrorCode::ShuttingDown,
        registry::ErrorCode::FrameTooLarge => pb::ErrorCode::RequestTooLarge,
        registry::ErrorCode::Overloaded => pb::ErrorCode::Overloaded,
        registry::ErrorCode::ProtocolVersionMismatch => pb::ErrorCode::ProtocolVersionMismatch,
        registry::ErrorCode::BackupClusterReadOnly => pb::ErrorCode::BackupClusterReadOnly,
        registry::ErrorCode::BackupClusterImmutable => pb::ErrorCode::BackupClusterImmutable,
        registry::ErrorCode::ColdTierUnavailable => pb::ErrorCode::ColdTierUnavailable,
        registry::ErrorCode::FrontierUnreachable => pb::ErrorCode::FrontierUnreachable,
        registry::ErrorCode::UsageUnavailable => pb::ErrorCode::UsageUnavailable,
        registry::ErrorCode::UsageCorrupt => pb::ErrorCode::UsageCorrupt,
    };
    tbep_status(code, error_code, &error.detail())
}

fn conversion_error(detail: &str) -> Status {
    tbep_status(Code::InvalidArgument, pb::ErrorCode::InvalidRequest, detail)
}

/// Runs one host call behind the overload gate on the blocking pool.
async fn run_host<T, F>(
    self_ref: &TbepGrpcServer,
    operation: MetricOperation,
    work: F,
) -> Result<T, Status>
where
    T: Send + 'static,
    F: FnOnce() -> Result<T, HostingError> + Send + 'static,
{
    let started = Instant::now();
    if self_ref.draining.load(Ordering::Acquire) {
        self_ref.metrics.record_request(
            self_ref.region,
            operation,
            MetricOutcome::Rejected,
            started.elapsed(),
        );
        return Err(tbep_status(
            Code::FailedPrecondition,
            pb::ErrorCode::ShuttingDown,
            "the engine is draining and is not admitting new work",
        ));
    }
    if self_ref.inflight.fetch_add(1, Ordering::AcqRel) >= self_ref.config.max_inflight() {
        self_ref.inflight.fetch_sub(1, Ordering::AcqRel);
        self_ref.metrics.record_request(
            self_ref.region,
            operation,
            MetricOutcome::Rejected,
            started.elapsed(),
        );
        return Err(tbep_status(
            Code::ResourceExhausted,
            pb::ErrorCode::Overloaded,
            "the engine is serving its in-flight bound; retry",
        ));
    }
    let permit = Permit(Arc::clone(&self_ref.inflight));
    let task = tokio::task::spawn_blocking(move || {
        let _permit = permit;
        work()
    });
    match tokio::time::timeout(self_ref.config.request_timeout(), task).await {
        Err(_) => {
            self_ref.metrics.record_request(
                self_ref.region,
                operation,
                MetricOutcome::Error,
                started.elapsed(),
            );
            Err(tbep_status(
                Code::DeadlineExceeded,
                pb::ErrorCode::EngineFailure,
                "the engine request exceeded its configured deadline",
            ))
        }
        Ok(Ok(Ok(value))) => {
            self_ref.metrics.record_request(
                self_ref.region,
                operation,
                MetricOutcome::Success,
                started.elapsed(),
            );
            Ok(value)
        }
        Ok(Ok(Err(error))) => {
            let outcome = match &error {
                HostingError::EngineFailure(_) => MetricOutcome::Error,
                HostingError::UsageCorrupt(_) => MetricOutcome::Error,
                HostingError::Invalid(_)
                | HostingError::Refused(_)
                | HostingError::SessionRefused(_)
                | HostingError::SubscriptionRefused(_)
                | HostingError::CheckpointRefused(_)
                | HostingError::ClusterReadOnly(_)
                | HostingError::ClusterImmutable(_)
                | HostingError::UsageUnavailable(_) => MetricOutcome::Rejected,
            };
            self_ref.metrics.record_request(self_ref.region, operation, outcome, started.elapsed());
            Err(host_error(error))
        }
        Ok(Err(_)) => {
            self_ref.metrics.record_request(
                self_ref.region,
                operation,
                MetricOutcome::Error,
                started.elapsed(),
            );
            Err(tbep_status(
                Code::Internal,
                pb::ErrorCode::EngineFailure,
                "the serving task panicked",
            ))
        }
    }
}

// ---------------------------------------------------------------------------
// Prost ⇄ domain conversion
// ---------------------------------------------------------------------------

fn invalid(detail: &str) -> HostingError {
    HostingError::Invalid(detail.to_string())
}

fn usage_admission(
    value: Option<&pb::UsageAdmissionContext>,
    idempotency_key: &str,
    scope: &registry::BoundScope,
) -> Result<Option<registry::UsageAdmissionContext>, Status> {
    let Some(value) = value else {
        return Ok(None);
    };
    let domain = registry::UsageAdmissionContext {
        project: value.project.clone(),
        database: value.database.clone(),
        partition: value.partition.clone(),
        origin_region: usage_region_from_pb(value.origin_region)?,
        request_id: value.request_id.clone(),
        accepted_at_millis: value.accepted_at_millis,
        meter_version: value.meter_version,
        integer_units: value.integer_units.clone(),
    };
    domain
        .validate_for_key(idempotency_key)
        .map_err(|error| conversion_error(&error.to_string()))?;
    if domain.project != scope.project || domain.database != scope.database {
        return Err(conversion_error(
            "usage admission does not match the bound Project and Database",
        ));
    }
    Ok(Some(domain))
}

fn usage_region_from_pb(value: i32) -> Result<registry::UsageRegion, Status> {
    match pb::UsageRegion::try_from(value) {
        Ok(pb::UsageRegion::Gru) => Ok(registry::UsageRegion::Gru),
        Ok(pb::UsageRegion::Sfo) => Ok(registry::UsageRegion::Sfo),
        Ok(pb::UsageRegion::Fra) => Ok(registry::UsageRegion::Fra),
        _ => Err(conversion_error("usage Region is outside the closed vocabulary")),
    }
}

const fn usage_region_pb(value: registry::UsageRegion) -> i32 {
    match value {
        registry::UsageRegion::Gru => pb::UsageRegion::Gru as i32,
        registry::UsageRegion::Sfo => pb::UsageRegion::Sfo as i32,
        registry::UsageRegion::Fra => pb::UsageRegion::Fra as i32,
    }
}

const MAX_VALUE_DEPTH: usize = 64;
/// Cumulative node budget for one recursive value conversion. Per-container
/// bounds alone still permit an exponential tree (for example, 4,096
/// one-item lists at every level); consume this budget before allocating the
/// corresponding domain node.
const MAX_VALUE_NODES: usize = 1 << 16;
const MAX_VALUE_TEXT_BYTES: usize = 1 << 20;
const MAX_VALUE_LIST_ITEMS: usize = 4096;
const MAX_VALUE_MAP_ENTRIES: usize = 1024;
const MAX_VALUE_NAME_BYTES: usize = 64;

fn value_from_pb(value: &pb::Value) -> Result<TbepValue, HostingError> {
    let mut budget = MAX_VALUE_NODES;
    value_from_pb_depth(value, 0, &mut budget)
}

fn value_from_pb_depth(
    value: &pb::Value,
    depth: usize,
    budget: &mut usize,
) -> Result<TbepValue, HostingError> {
    if depth > MAX_VALUE_DEPTH {
        return Err(invalid("a value exceeds its nesting bound"));
    }
    consume_value_budget(budget, 1)?;
    let kind = value.kind.as_ref().ok_or_else(|| invalid("a value carries no kind"))?;
    Ok(match kind {
        pb::value::Kind::Null(_) => TbepValue::Null,
        pb::value::Kind::Boolean(flag) => TbepValue::Boolean(*flag),
        pb::value::Kind::Integer(number) => TbepValue::Integer(*number),
        pb::value::Kind::Number(number) if number.is_finite() => TbepValue::Number(*number),
        pb::value::Kind::Number(_) => return Err(invalid("a number must be finite")),
        pb::value::Kind::Text(text) if text.len() <= MAX_VALUE_TEXT_BYTES => {
            TbepValue::String(text.clone())
        }
        pb::value::Kind::Text(_) => return Err(invalid("a value text exceeds its bound")),
        pb::value::Kind::List(list) => {
            if list.items.len() > MAX_VALUE_LIST_ITEMS {
                return Err(invalid("a value list exceeds its item bound"));
            }
            consume_value_budget(budget, list.items.len())?;
            TbepValue::Array(
                list.items
                    .iter()
                    .map(|item| value_from_pb_depth(item, depth + 1, budget))
                    .collect::<Result<Vec<_>, _>>()?,
            )
        }
        pb::value::Kind::Map(map) => {
            if map.entries.len() > MAX_VALUE_MAP_ENTRIES {
                return Err(invalid("a value map exceeds its entry bound"));
            }
            consume_value_budget(budget, map.entries.len())?;
            let mut fields = Vec::with_capacity(map.entries.len());
            for entry in &map.entries {
                if entry.name.is_empty()
                    || entry.name.len() > MAX_VALUE_NAME_BYTES
                    || entry.name.bytes().any(|byte| byte.is_ascii_control())
                {
                    return Err(invalid("a value map name is outside its bound"));
                }
                if fields.iter().any(|(name, _)| name == &entry.name) {
                    return Err(invalid("a value map carries duplicate names"));
                }
                fields.push((
                    entry.name.clone(),
                    value_from_pb_depth(
                        entry
                            .value
                            .as_ref()
                            .ok_or_else(|| invalid("a value entry carries no value"))?,
                        depth + 1,
                        budget,
                    )?,
                ));
            }
            fields.sort_by(|a, b| a.0.cmp(&b.0));
            TbepValue::Object(fields).canonical()
        }
    })
}

fn consume_value_budget(budget: &mut usize, amount: usize) -> Result<(), HostingError> {
    if *budget < amount {
        return Err(invalid("a value exceeds its cumulative allocation bound"));
    }
    *budget -= amount;
    Ok(())
}

fn value_to_pb(value: &TbepValue) -> pb::Value {
    pb::Value {
        kind: Some(match value {
            TbepValue::Null => pb::value::Kind::Null(pb::NullValue::Null as i32),
            TbepValue::Boolean(flag) => pb::value::Kind::Boolean(*flag),
            TbepValue::Integer(number) => pb::value::Kind::Integer(*number),
            TbepValue::Number(number) => pb::value::Kind::Number(*number),
            TbepValue::String(text) => pb::value::Kind::Text(text.clone()),
            TbepValue::Array(items) => pb::value::Kind::List(pb::ValueList {
                items: items.iter().map(value_to_pb).collect(),
            }),
            TbepValue::Object(fields) => pb::value::Kind::Map(pb::ValueMap {
                entries: fields
                    .iter()
                    .map(|(name, item)| pb::ValueEntry {
                        name: name.clone(),
                        value: Some(value_to_pb(item)),
                    })
                    .collect(),
            }),
        }),
    }
}

// --- shared closed vocabularies ---

fn acknowledgement(v: i32) -> Option<registry::Acknowledgement> {
    match pb::Acknowledgement::try_from(v).ok()? {
        pb::Acknowledgement::Unspecified => None,
        pb::Acknowledgement::LocalSynced => Some(registry::Acknowledgement::LocalSynced),
        pb::Acknowledgement::RegionalQuorum => Some(registry::Acknowledgement::RegionalQuorum),
        pb::Acknowledgement::GlobalQuorum => Some(registry::Acknowledgement::GlobalQuorum),
        pb::Acknowledgement::Backup => Some(registry::Acknowledgement::BackupAck),
    }
}

fn acknowledgement_pb(value: Option<registry::Acknowledgement>) -> i32 {
    match value {
        None | Some(registry::Acknowledgement::RegionalQuorum) => {
            pb::Acknowledgement::RegionalQuorum as i32
        }
        Some(registry::Acknowledgement::GlobalQuorum) => pb::Acknowledgement::GlobalQuorum as i32,
        Some(registry::Acknowledgement::BackupAck) => pb::Acknowledgement::Backup as i32,
        Some(registry::Acknowledgement::LocalSynced) => pb::Acknowledgement::LocalSynced as i32,
    }
}

fn backup_outcome_pb(value: Option<registry::BackupOutcome>) -> i32 {
    match value {
        None | Some(registry::BackupOutcome::None) => pb::BackupOutcome::None as i32,
        Some(registry::BackupOutcome::Full) => pb::BackupOutcome::Full as i32,
        Some(registry::BackupOutcome::Partial) => pb::BackupOutcome::Partial as i32,
    }
}

fn read_level(v: i32) -> Option<registry::ReadLevel> {
    match pb::ReadLevel::try_from(v).ok()? {
        pb::ReadLevel::Unspecified => None,
        pb::ReadLevel::Local => Some(registry::ReadLevel::Local),
        pb::ReadLevel::RegionalQuorum => Some(registry::ReadLevel::RegionalQuorumRead),
        pb::ReadLevel::GlobalQuorum => Some(registry::ReadLevel::GlobalQuorumRead),
    }
}

fn cluster_mode_pb(value: Option<registry::ClusterMode>) -> i32 {
    match value {
        None | Some(registry::ClusterMode::Standalone) => pb::ClusterMode::Standalone as i32,
        Some(registry::ClusterMode::Active) => pb::ClusterMode::Active as i32,
        Some(registry::ClusterMode::Backup) => pb::ClusterMode::Backup as i32,
    }
}

fn expected_version(
    value: &pb::ExpectedVersion,
) -> Result<registry::ExpectedVersion, HostingError> {
    let discipline = value
        .discipline
        .as_ref()
        .ok_or_else(|| invalid("an expected version carries no discipline"))?;
    Ok(match discipline {
        pb::expected_version::Discipline::Absent(_) => registry::ExpectedVersion::Absent,
        pb::expected_version::Discipline::Any(_) => registry::ExpectedVersion::Any,
        pb::expected_version::Discipline::Exact(version) => {
            registry::ExpectedVersion::Exact(*version)
        }
    })
}

fn commit_receipt(value: &registry::CommitReceipt) -> pb::CommitReceipt {
    pb::CommitReceipt {
        sequence: value.sequence,
        version: value.version,
        operation_id: value.operation_id.clone(),
        acknowledgement: acknowledgement_pb(value.acknowledgement),
        backup: backup_outcome_pb(value.backup),
    }
}

fn write_outcome_pb(value: &registry::WriteOutcome) -> pb::WriteOutcome {
    pb::WriteOutcome {
        outcome: Some(match value {
            registry::WriteOutcome::Committed(receipt) => {
                pb::write_outcome::Outcome::Committed(commit_receipt(receipt))
            }
            registry::WriteOutcome::Conflict => {
                pb::write_outcome::Outcome::Conflict(pb::WriteConflict {})
            }
            registry::WriteOutcome::Rejected(reason) => {
                let pb_reason = match reason {
                    registry::WriteReason::InvalidDocument => pb::WriteReason::InvalidDocument,
                    registry::WriteReason::SchemaRefused => pb::WriteReason::SchemaRefused,
                    registry::WriteReason::NotFound => pb::WriteReason::NotFound,
                };
                pb::write_outcome::Outcome::Rejected(pb::WriteRejected { reason: pb_reason as i32 })
            }
        }),
    }
}

fn document_put(value: &pb::DocumentPut) -> Result<registry::DocumentPut, HostingError> {
    Ok(registry::DocumentPut {
        id: value.id.clone(),
        collection: value.collection.clone(),
        body: value
            .body
            .as_ref()
            .map(value_from_pb)
            .transpose()?
            .ok_or_else(|| invalid("a document put carries no body"))?,
    })
}

fn document_body(value: &registry::DocumentBody) -> pb::DocumentBody {
    pb::DocumentBody {
        id: value.id.clone(),
        collection: value.collection.clone(),
        version: value.version,
        body: Some(value_to_pb(&value.body)),
    }
}

fn subscription_start(
    value: &pb::SubscriptionStart,
) -> Result<registry::SubscriptionStart, HostingError> {
    let start = value
        .start
        .as_ref()
        .ok_or_else(|| invalid("a subscription start carries no discipline"))?;
    Ok(match start {
        pb::subscription_start::Start::Live(_) => registry::SubscriptionStart::Live,
        pb::subscription_start::Start::Sequence(sequence) => {
            registry::SubscriptionStart::Sequence(*sequence)
        }
    })
}

fn mutation_outcome_pb(value: &registry::MutationOutcome) -> pb::MutationOutcome {
    pb::MutationOutcome {
        outcome: Some(match value {
            registry::MutationOutcome::Applied(receipt) => {
                pb::mutation_outcome::Outcome::Applied(commit_receipt(receipt))
            }
            registry::MutationOutcome::Rejected(reason) => {
                let pb_reason = match reason {
                    registry::MutationReason::ScopeRefused => pb::MutationReason::ScopeRefused,
                    registry::MutationReason::InvalidBody => pb::MutationReason::InvalidBody,
                    registry::MutationReason::SequenceOutOfOrder => {
                        pb::MutationReason::SequenceOutOfOrder
                    }
                };
                pb::mutation_outcome::Outcome::Rejected(pb::MutationRejected {
                    reason: pb_reason as i32,
                })
            }
        }),
    }
}

fn sync_field(value: &pb::SyncField) -> Result<registry::SyncField, HostingError> {
    Ok(registry::SyncField {
        name: value.name.clone(),
        value: value
            .value
            .as_ref()
            .map(value_from_pb)
            .transpose()?
            .ok_or_else(|| invalid("a sync field carries no value"))?,
    })
}

fn sync_change(value: &pb::SyncChange) -> Result<registry::SyncChange, HostingError> {
    let change =
        value.change.as_ref().ok_or_else(|| invalid("a sync mutation carries no change"))?;
    Ok(match change {
        pb::sync_change::Change::Put(put) => registry::SyncChange::Put(
            put.fields.iter().map(sync_field).collect::<Result<Vec<_>, _>>()?,
        ),
        pb::sync_change::Change::Delete(_) => registry::SyncChange::Delete,
    })
}

// --- schema and migration conversion ---

fn violation_kind_pb(value: registry::ViolationKind) -> i32 {
    match value {
        registry::ViolationKind::DocumentNotObject => pb::ViolationKind::DocumentNotObject as i32,
        registry::ViolationKind::MissingRequiredField => {
            pb::ViolationKind::MissingRequiredField as i32
        }
        registry::ViolationKind::TypeMismatch => pb::ViolationKind::TypeMismatch as i32,
        registry::ViolationKind::ValueNotAllowed => pb::ViolationKind::ValueNotAllowed as i32,
        registry::ViolationKind::NumberOutOfRange => pb::ViolationKind::NumberOutOfRange as i32,
        registry::ViolationKind::LengthOutOfRange => pb::ViolationKind::LengthOutOfRange as i32,
    }
}

fn schema_violation_pb(value: &registry::SchemaViolation) -> pb::SchemaViolation {
    pb::SchemaViolation { field: value.field.clone(), kind: violation_kind_pb(value.kind) }
}

fn transform_rejection_pb(value: registry::TransformRejection) -> i32 {
    match value {
        registry::TransformRejection::ValueNotCoercible => {
            pb::TransformRejection::ValueNotCoercible as i32
        }
        registry::TransformRejection::TargetFieldOccupied => {
            pb::TransformRejection::TargetFieldOccupied as i32
        }
    }
}

fn transform_literal(
    value: &pb::TransformLiteral,
) -> Result<registry::TransformLiteral, HostingError> {
    let literal =
        value.literal.as_ref().ok_or_else(|| invalid("a transform literal carries no value"))?;
    Ok(match literal {
        pb::transform_literal::Literal::Boolean(flag) => registry::TransformLiteral::Boolean(*flag),
        pb::transform_literal::Literal::Integer(number) => {
            registry::TransformLiteral::Integer(*number)
        }
        pb::transform_literal::Literal::Text(text) => {
            registry::TransformLiteral::Text(text.clone())
        }
    })
}

fn coercion(v: i32) -> Option<registry::Coercion> {
    match pb::Coercion::try_from(v).ok()? {
        pb::Coercion::Unspecified => None,
        pb::Coercion::IntegerToText => Some(registry::Coercion::IntegerToText),
        pb::Coercion::TextToInteger => Some(registry::Coercion::TextToInteger),
        pb::Coercion::IntegerToNumber => Some(registry::Coercion::IntegerToNumber),
        pb::Coercion::NumberToInteger => Some(registry::Coercion::NumberToInteger),
    }
}

fn field_transform(v: i32) -> Result<registry::FieldTransform, HostingError> {
    match pb::FieldTransform::try_from(v).map_err(|_| invalid("an unknown field transform"))? {
        pb::FieldTransform::Unspecified => Err(invalid("a transform step carries no kind")),
        pb::FieldTransform::AddField => Ok(registry::FieldTransform::AddField),
        pb::FieldTransform::RemoveField => Ok(registry::FieldTransform::RemoveField),
        pb::FieldTransform::RenameField => Ok(registry::FieldTransform::RenameField),
        pb::FieldTransform::RetypeField => Ok(registry::FieldTransform::RetypeField),
    }
}

fn field_transform_step(
    value: &pb::FieldTransformStep,
) -> Result<registry::FieldTransformStep, HostingError> {
    Ok(registry::FieldTransformStep {
        kind: field_transform(value.kind)?,
        field: value.field.clone(),
        to: (!value.to.is_empty()).then(|| value.to.clone()),
        default: value.default.as_ref().map(transform_literal).transpose()?,
        coercion: coercion(value.coercion),
    })
}

fn migration_contract(
    value: &pb::SchemaMigrationContract,
) -> Result<registry::SchemaMigrationContract, HostingError> {
    Ok(registry::SchemaMigrationContract {
        collection: value.collection.clone(),
        from_version: value.from_version,
        to_version: value.to_version,
        target: value
            .target
            .as_ref()
            .map(value_from_pb)
            .transpose()?
            .ok_or_else(|| invalid("a migration contract carries no target"))?,
        transforms: value
            .transforms
            .iter()
            .map(field_transform_step)
            .collect::<Result<Vec<_>, _>>()?,
    })
}

fn plan_problem(value: &registry::PlanProblem) -> pb::PlanProblem {
    pb::PlanProblem {
        document: value.document.clone(),
        field: value.field.clone(),
        kind: match value.kind {
            registry::PlanProblemKind::TransformRejected => {
                pb::PlanProblemKind::TransformRejected as i32
            }
            registry::PlanProblemKind::TargetViolations => {
                pb::PlanProblemKind::TargetViolations as i32
            }
        },
        rejection: value.rejection.map(transform_rejection_pb).unwrap_or_default(),
        violations: value.violations.iter().map(schema_violation_pb).collect(),
    }
}

fn transform_counts(value: &registry::TransformCounts) -> pb::TransformCounts {
    pb::TransformCounts {
        added: value.added,
        removed: value.removed,
        renamed: value.renamed,
        retyped: value.retyped,
    }
}

fn migration_plan(value: &registry::MigrationPlan) -> pb::MigrationPlan {
    pb::MigrationPlan {
        manifest: value.manifest.clone(),
        collection: value.collection.clone(),
        from_version: value.from_version,
        to_version: value.to_version,
        ready: value.ready,
        documents: value.documents,
        source_digest: value.source_digest.clone(),
        result_digest: value.result_digest.clone(),
        transform_counts: Some(transform_counts(&value.transform_counts)),
        problems: value.problems.iter().map(plan_problem).collect(),
        problems_truncated: value.problems_truncated,
    }
}

fn migration_receipt(value: &registry::MigrationReceipt) -> pb::MigrationReceipt {
    pb::MigrationReceipt {
        collection: value.collection.clone(),
        manifest: value.manifest.clone(),
        from_version: value.from_version,
        to_version: value.to_version,
        source_digest: value.source_digest.clone(),
        result_digest: value.result_digest.clone(),
        documents: value.documents,
        transformed: value.transformed,
        transform_counts: Some(transform_counts(&value.transform_counts)),
        pages: value.pages,
        rollback_limit: match value.rollback_limit {
            registry::RollbackLimitKind::Reversible => pb::RollbackLimitKind::Reversible as i32,
            registry::RollbackLimitKind::NotReversible => {
                pb::RollbackLimitKind::NotReversible as i32
            }
        },
        acknowledged_not_rollbackable: value.acknowledged_not_rollbackable,
        already_applied: value.already_applied,
    }
}

fn migration_state(value: &registry::MigrationState) -> pb::MigrationState {
    pb::MigrationState {
        kind: match value.kind {
            registry::MigrationStateKind::None => pb::MigrationStateKind::None as i32,
            registry::MigrationStateKind::InProgress => pb::MigrationStateKind::InProgress as i32,
            registry::MigrationStateKind::RollingBack => pb::MigrationStateKind::RollingBack as i32,
            registry::MigrationStateKind::Complete => pb::MigrationStateKind::Complete as i32,
        },
        manifest: value.manifest.clone(),
        from_version: value.from_version,
        to_version: value.to_version,
        documents: value.documents,
        transformed: value.transformed,
        pages: value.pages,
    }
}

// --- replication observation conversion ---

fn conflict_record(value: &registry::ConflictRecord) -> pb::ConflictRecord {
    pb::ConflictRecord {
        tenant: value.tenant.clone(),
        operations: value.operations.clone(),
        members: value.members.clone(),
        clocks: value
            .clocks
            .iter()
            .map(|clock| pb::ClockStamp { ticks: clock.ticks, counter: clock.counter })
            .collect(),
        outcome: Some(pb::ConflictOutcome {
            kind: match value.outcome.kind {
                registry::ConflictOutcomeKind::Winner => pb::ConflictOutcomeKind::Winner as i32,
                registry::ConflictOutcomeKind::DeposedOwnerRetained => {
                    pb::ConflictOutcomeKind::DeposedOwnerRetained as i32
                }
                registry::ConflictOutcomeKind::UntrustedClock => {
                    pb::ConflictOutcomeKind::UntrustedClock as i32
                }
            },
            member: value.outcome.member.clone(),
        }),
    }
}

fn fenced_record(value: &registry::FencedRecord) -> pb::FencedRecord {
    pb::FencedRecord {
        range: value.range.clone(),
        batch_epoch: value.batch_epoch,
        highest_epoch: value.highest_epoch,
        source: value.source.clone(),
    }
}

fn deposition_record(value: &registry::DepositionRecord) -> pb::DepositionRecord {
    pb::DepositionRecord {
        range: value.range.clone(),
        held_epoch: value.held_epoch,
        owner_epoch: value.owner_epoch,
        new_owner: value.new_owner.clone(),
    }
}

fn denial_record(value: &registry::DenialRecord) -> pb::DenialRecord {
    pb::DenialRecord {
        seam: match value.seam {
            registry::DenialSeam::ServeRequest => pb::DenialSeam::ServeRequest as i32,
            registry::DenialSeam::ApplyDelivery => pb::DenialSeam::ApplyDelivery as i32,
        },
        scope_tenant: value.scope_tenant.clone(),
        attempted_tenant: value.attempted_tenant.clone(),
        counterparty: value.counterparty.clone(),
    }
}

fn replication_status_pb(value: registry::ReplicationStatus) -> i32 {
    match value {
        registry::ReplicationStatus::Assigned => pb::ReplicationStatus::Assigned as i32,
        registry::ReplicationStatus::Active => pb::ReplicationStatus::Active as i32,
        registry::ReplicationStatus::Responsive => pb::ReplicationStatus::Responsive as i32,
        registry::ReplicationStatus::Stale => pb::ReplicationStatus::Stale as i32,
        registry::ReplicationStatus::Unavailable => pb::ReplicationStatus::Unavailable as i32,
        registry::ReplicationStatus::Degraded => pb::ReplicationStatus::Degraded as i32,
        registry::ReplicationStatus::Unknown => pb::ReplicationStatus::Unknown as i32,
    }
}

// --- tier conversion ---

fn tier_move_receipt(value: &registry::TierMoveReceipt) -> pb::TierMoveReceipt {
    pb::TierMoveReceipt {
        policy_id: value.policy_id.clone(),
        policy_version: value.policy_version,
        decision: match value.decision {
            registry::TierDecision::Promote => pb::TierDecision::Promote as i32,
            registry::TierDecision::Demote => pb::TierDecision::Demote as i32,
            registry::TierDecision::Split => pb::TierDecision::Split as i32,
            registry::TierDecision::None => pb::TierDecision::None as i32,
            registry::TierDecision::Refused => pb::TierDecision::Refused as i32,
        },
        reasons: value
            .reasons
            .iter()
            .map(|reason| pb::TierMoveReason {
                fact: reason.fact.clone(),
                observed: reason.observed.clone(),
            })
            .collect(),
        segment_id: value.segment_id,
        bytes: value.bytes,
        manifest_before: value.manifest_before,
        manifest_after: value.manifest_after,
        dry_run: value.dry_run,
        manifest_accepted: value.manifest_accepted,
    }
}

// --- usage conversion ---

fn usage_fact_pb(value: &registry::UsageFact) -> pb::UsageFact {
    pb::UsageFact {
        fact_id: value.fact_id.clone(),
        meter: match value.meter {
            registry::UsageMeter::AdmittedRequestUnits => {
                pb::UsageMeter::AdmittedRequestUnits as i32
            }
            registry::UsageMeter::PublicEgressBytes => pb::UsageMeter::PublicEgressBytes as i32,
        },
        meter_version: value.meter_version,
        unit: match value.unit {
            registry::UsageUnit::RequestUnit => pb::UsageUnit::RequestUnit as i32,
            registry::UsageUnit::Byte => pb::UsageUnit::Byte as i32,
        },
        charge_scope: match value.charge_scope {
            registry::UsageChargeScope::AdmittedRequest => {
                pb::UsageChargeScope::AdmittedRequest as i32
            }
            registry::UsageChargeScope::PublicEgress => pb::UsageChargeScope::PublicEgress as i32,
        },
        minting_rule: match value.minting_rule {
            registry::UsageMintingRule::StableAdmittedRequest => {
                pb::UsageMintingRule::StableAdmittedRequest as i32
            }
            registry::UsageMintingRule::PublicResponse => {
                pb::UsageMintingRule::PublicResponse as i32
            }
        },
        tenant: value.tenant.clone(),
        project: value.project.clone(),
        database: value.database.clone(),
        partition: value.partition.clone(),
        region: usage_region_pb(value.region),
        measurement_start_millis: value.measurement_start_millis,
        measurement_end_millis: value.measurement_end_millis,
        integer_delta: value.integer_delta.clone(),
        sequence: value.sequence,
        replica_epoch: value.replica_epoch,
        format_version: value.format_version,
        canonical: value.canonical.clone(),
        checksum: value.checksum.clone(),
    }
}

// --- notification conversion ---

fn change_event(value: &registry::ChangeEvent) -> pb::ChangeEvent {
    pb::ChangeEvent {
        sequence: value.sequence,
        operation_id: value.operation_id.clone(),
        kind: match value.kind {
            registry::ChangeKind::Put => pb::ChangeKind::Put as i32,
            registry::ChangeKind::Delete => pb::ChangeKind::Delete as i32,
        },
        collection: value.collection.clone(),
        document: value.document.clone(),
    }
}

fn notification_event(notification: TbepNotification) -> pb::SubscribeResponse {
    let event = match notification {
        TbepNotification::Batch(batch) => {
            pb::subscribe_response::Event::Batch(pb::SubscriptionBatch {
                subscription_id: batch.subscription_id,
                from_sequence: batch.from_sequence,
                events: batch.events.iter().map(change_event).collect(),
                next_sequence: batch.next_sequence,
            })
        }
        TbepNotification::Trigger(trigger) => {
            pb::subscribe_response::Event::Trigger(pb::TriggerNotification {
                subscription_id: trigger.subscription_id,
                collection: trigger.collection.clone(),
                sequence: trigger.sequence,
                operation_id: trigger.operation_id.clone(),
            })
        }
        TbepNotification::Closed(closed) => {
            pb::subscribe_response::Event::Closed(pb::SubscriptionClosed {
                subscription_id: closed.subscription_id,
                reason: match closed.reason {
                    registry::SubscriptionCloseReason::Replaced => {
                        pb::SubscriptionCloseReason::Replaced as i32
                    }
                    registry::SubscriptionCloseReason::ShuttingDown => {
                        pb::SubscriptionCloseReason::ShuttingDown as i32
                    }
                },
            })
        }
    };
    pb::SubscribeResponse { event: Some(event) }
}

fn is_closed(event: &pb::SubscribeResponse) -> bool {
    matches!(event.event, Some(pb::subscribe_response::Event::Closed(_)))
}

/// The bounded response stream for one backbone subscription.
//
// It owns the stop signal for the synchronous feed pump.
/// Dropping the response (for example, when a client cancels or disconnects)
/// wakes both an idle feed receive and a pump waiting for queue capacity.
pub struct SubscriptionStream {
    receiver: ReceiverStream<Result<pb::SubscribeResponse, Status>>,
    stop: Option<std::sync::mpsc::Sender<()>>,
    pump: Option<std::thread::JoinHandle<()>>,
}

impl Stream for SubscriptionStream {
    type Item = Result<pb::SubscribeResponse, Status>;

    fn poll_next(mut self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<Option<Self::Item>> {
        Pin::new(&mut self.receiver).poll_next(cx)
    }
}

impl Drop for SubscriptionStream {
    fn drop(&mut self) {
        if let Some(stop) = self.stop.take() {
            let _ = stop.send(());
        }
        // The pump owns the synchronous host feed. Join it before releasing
        // the stream so cancellation and graceful drain cannot leave a
        // thread retaining the host after the RPC has ended.
        if let Some(pump) = self.pump.take() {
            let _ = pump.join();
        }
    }
}

// ---------------------------------------------------------------------------
// Service implementations
// ---------------------------------------------------------------------------

#[tonic::async_trait]
impl ChannelService for Arc<TbepGrpcServer> {
    async fn bind(
        &self,
        request: Request<pb::ChannelServiceBindRequest>,
    ) -> Result<Response<pb::ChannelServiceBindResponse>, Status> {
        self.counted();
        if self.binding == pb::BindingKind::NetworkMtls {
            self.authenticate_gateway(&request, &request.get_ref().principal)?;
        }
        let message = request.into_inner();
        let domain = registry::ChannelBindRequest {
            scope: registry::BoundScope {
                principal: message.principal,
                tenant: message.tenant,
                project: message.project,
                database: message.database,
            },
        };
        domain.validate().map_err(|error| conversion_error(&error.to_string()))?;
        let expected_scope = domain.scope.clone();
        let host = Arc::clone(&self.host);
        let response =
            run_host(self.as_ref(), MetricOperation::Other, move || host.bind_channel(&domain))
                .await?;
        if response.scope != expected_scope || response.validate().is_err() {
            return Err(tbep_status(
                Code::Internal,
                pb::ErrorCode::EngineFailure,
                "the host returned an invalid channel binding",
            ));
        }
        Ok(Response::new(pb::ChannelServiceBindResponse {
            principal: response.scope.principal,
            tenant: response.scope.tenant,
            serving_instance: response.serving_instance,
            project: response.scope.project,
            database: response.scope.database,
        }))
    }
}

#[tonic::async_trait]
impl EngineService for Arc<TbepGrpcServer> {
    async fn document_read(
        &self,
        request: Request<pb::EngineServiceDocumentReadRequest>,
    ) -> Result<Response<pb::EngineServiceDocumentReadResponse>, Status> {
        self.counted();
        let identity = self.identity(&request)?;
        let message = request.into_inner();
        let domain = registry::DocumentReadRequest {
            collection: message.collection,
            document: message.document,
            level: read_level(message.level),
        };
        domain.validate().map_err(|error| conversion_error(&error.to_string()))?;
        let host = Arc::clone(&self.host);
        let tenant = identity.tenant;
        let response = run_host(self.as_ref(), MetricOperation::Read, move || {
            host.document_read(&tenant, &domain)
        })
        .await?;
        Ok(Response::new(pb::EngineServiceDocumentReadResponse {
            found: response.found,
            document: response.document.as_ref().map(document_body),
            position: response.position.unwrap_or_default(),
        }))
    }

    async fn document_write(
        &self,
        request: Request<pb::EngineServiceDocumentWriteRequest>,
    ) -> Result<Response<pb::EngineServiceDocumentWriteResponse>, Status> {
        self.counted();
        let identity = self.identity(&request)?;
        let key = idempotency_key(&request)?;
        let message = request.into_inner();
        let document =
            message.document.as_ref().map(document_put).transpose().map_err(host_error)?;
        let domain = registry::DocumentWriteRequest {
            document: document.ok_or_else(|| conversion_error("a write carries no document"))?,
            expected: message
                .expected
                .as_ref()
                .map(expected_version)
                .transpose()
                .map_err(host_error)?
                .ok_or_else(|| conversion_error("a write carries no expected version"))?,
            operation_id: message.operation_id,
            ack: acknowledgement(message.ack),
            usage: usage_admission(message.usage.as_ref(), &key, &identity.tenant)?,
        };
        self.require_mutation_usage(domain.usage.is_some())?;
        domain.validate().map_err(|error| conversion_error(&error.to_string()))?;
        let host = Arc::clone(&self.host);
        let tenant = identity.tenant;
        let response = run_host(self.as_ref(), MetricOperation::Write, move || {
            host.document_write(&tenant, &domain, &key)
        })
        .await?;
        Ok(Response::new(pb::EngineServiceDocumentWriteResponse {
            outcome: Some(write_outcome_pb(&response.outcome)),
        }))
    }

    async fn document_delete(
        &self,
        request: Request<pb::EngineServiceDocumentDeleteRequest>,
    ) -> Result<Response<pb::EngineServiceDocumentDeleteResponse>, Status> {
        self.counted();
        let identity = self.identity(&request)?;
        let key = idempotency_key(&request)?;
        let message = request.into_inner();
        let domain = registry::DocumentDeleteRequest {
            collection: message.collection,
            document: message.document,
            expected: message
                .expected
                .as_ref()
                .map(expected_version)
                .transpose()
                .map_err(host_error)?
                .ok_or_else(|| conversion_error("a delete carries no expected version"))?,
            operation_id: message.operation_id,
            ack: acknowledgement(message.ack),
            usage: usage_admission(message.usage.as_ref(), &key, &identity.tenant)?,
        };
        self.require_mutation_usage(domain.usage.is_some())?;
        domain.validate().map_err(|error| conversion_error(&error.to_string()))?;
        let host = Arc::clone(&self.host);
        let tenant = identity.tenant;
        let response = run_host(self.as_ref(), MetricOperation::Write, move || {
            host.document_delete(&tenant, &domain, &key)
        })
        .await?;
        Ok(Response::new(pb::EngineServiceDocumentDeleteResponse {
            outcome: Some(write_outcome_pb(&response.outcome)),
        }))
    }

    async fn document_batch(
        &self,
        request: Request<pb::EngineServiceDocumentBatchRequest>,
    ) -> Result<Response<pb::EngineServiceDocumentBatchResponse>, Status> {
        self.counted();
        let identity = self.identity(&request)?;
        let key = idempotency_key(&request)?;
        let message = request.into_inner();
        let mut items = Vec::with_capacity(message.items.len());
        for item in &message.items {
            let document = item
                .document
                .as_ref()
                .map(document_put)
                .transpose()
                .map_err(host_error)?
                .ok_or_else(|| conversion_error("a batch item carries no document"))?;
            items.push(registry::BatchWrite {
                document,
                expected: item
                    .expected
                    .as_ref()
                    .map(expected_version)
                    .transpose()
                    .map_err(host_error)?
                    .ok_or_else(|| conversion_error("a batch item carries no expected version"))?,
                operation_id: item.operation_id.clone(),
            });
        }
        let domain = registry::DocumentBatchRequest {
            items,
            ack: acknowledgement(message.ack),
            usage: usage_admission(message.usage.as_ref(), &key, &identity.tenant)?,
        };
        self.require_mutation_usage(domain.usage.is_some())?;
        domain.validate().map_err(|error| conversion_error(&error.to_string()))?;
        let host = Arc::clone(&self.host);
        let tenant = identity.tenant;
        let response = run_host(self.as_ref(), MetricOperation::Write, move || {
            host.document_batch(&tenant, &domain, &key)
        })
        .await?;
        Ok(Response::new(pb::EngineServiceDocumentBatchResponse {
            outcome: Some(write_outcome_pb(&response.outcome)),
        }))
    }

    async fn query_execute(
        &self,
        request: Request<pb::EngineServiceQueryExecuteRequest>,
    ) -> Result<Response<pb::EngineServiceQueryExecuteResponse>, Status> {
        self.counted();
        let identity = self.identity(&request)?;
        let message = request.into_inner();
        let domain = registry::QueryExecuteRequest {
            query_id: message.query_id,
            text: message.text,
            max_items: message.max_items,
            level: read_level(message.level),
        };
        domain.validate().map_err(|error| conversion_error(&error.to_string()))?;
        let host = Arc::clone(&self.host);
        let tenant = identity.tenant;
        let response = run_host(self.as_ref(), MetricOperation::Query, move || {
            host.query_execute(&tenant, &domain)
        })
        .await?;
        Ok(Response::new(pb::EngineServiceQueryExecuteResponse {
            documents: response.documents.iter().map(document_body).collect(),
            count: response.count,
            terminal: match response.terminal {
                registry::QueryTerminal::Complete => pb::QueryTerminal::Complete as i32,
                registry::QueryTerminal::Truncated => pb::QueryTerminal::Truncated as i32,
            },
            position: response.position.unwrap_or_default(),
        }))
    }

    async fn query_cancel(
        &self,
        request: Request<pb::EngineServiceQueryCancelRequest>,
    ) -> Result<Response<pb::EngineServiceQueryCancelResponse>, Status> {
        self.counted();
        let identity = self.identity(&request)?;
        let message = request.into_inner();
        let domain = registry::QueryCancelRequest { query_id: message.query_id };
        let host = Arc::clone(&self.host);
        let tenant = identity.tenant;
        let response = run_host(self.as_ref(), MetricOperation::Query, move || {
            host.query_cancel(&tenant, &domain)
        })
        .await?;
        Ok(Response::new(pb::EngineServiceQueryCancelResponse {
            outcome: match response.outcome {
                registry::CancelOutcome::Cancelled => pb::CancelOutcome::Cancelled as i32,
                registry::CancelOutcome::AlreadyComplete => {
                    pb::CancelOutcome::AlreadyComplete as i32
                }
                registry::CancelOutcome::UnknownQuery => pb::CancelOutcome::UnknownQuery as i32,
            },
        }))
    }

    async fn family_read(
        &self,
        request: Request<pb::EngineServiceFamilyReadRequest>,
    ) -> Result<Response<pb::EngineServiceFamilyReadResponse>, Status> {
        self.counted();
        let identity = self.identity(&request)?;
        let message = request.into_inner();
        if message.request.len() > self.config.max_message_bytes() {
            return Err(conversion_error("a family request exceeds its byte bound"));
        }
        let domain = registry::FamilyReadRequest {
            family: message.family,
            name: message.name,
            request_codec: message.request_codec,
            request: message.request,
        };
        domain.validate().map_err(|error| conversion_error(&error.to_string()))?;
        let host = Arc::clone(&self.host);
        let tenant = identity.tenant;
        let response = run_host(self.as_ref(), MetricOperation::Read, move || {
            host.family_read(&tenant, &domain)
        })
        .await?;
        Ok(Response::new(pb::EngineServiceFamilyReadResponse {
            response_codec: response.response_codec,
            response: response.response,
        }))
    }

    async fn family_write(
        &self,
        request: Request<pb::EngineServiceFamilyWriteRequest>,
    ) -> Result<Response<pb::EngineServiceFamilyWriteResponse>, Status> {
        self.counted();
        let identity = self.identity(&request)?;
        let key = idempotency_key(&request)?;
        let message = request.into_inner();
        if message.request.len() > self.config.max_message_bytes() {
            return Err(conversion_error("a family request exceeds its byte bound"));
        }
        let domain = registry::FamilyWriteRequest {
            family: message.family,
            name: message.name,
            request_codec: message.request_codec,
            request: message.request,
            operation_id: message.operation_id,
            usage: usage_admission(message.usage.as_ref(), &key, &identity.tenant)?,
        };
        self.require_mutation_usage(domain.usage.is_some())?;
        domain.validate().map_err(|error| conversion_error(&error.to_string()))?;
        let host = Arc::clone(&self.host);
        let tenant = identity.tenant;
        let response = run_host(self.as_ref(), MetricOperation::Write, move || {
            host.family_write(&tenant, &domain, &key)
        })
        .await?;
        Ok(Response::new(pb::EngineServiceFamilyWriteResponse {
            response_codec: response.response_codec,
            response: response.response,
        }))
    }
}

#[tonic::async_trait]
impl BackboneService for Arc<TbepGrpcServer> {
    type SubscribeStream = SubscriptionStream;

    async fn subscribe(
        &self,
        request: Request<pb::BackboneServiceSubscribeRequest>,
    ) -> Result<Response<Self::SubscribeStream>, Status> {
        self.counted();
        let identity = self.identity(&request)?;
        let message = request.into_inner();
        let domain = registry::BackboneSubscribeRequest {
            subscription_id: message.subscription_id,
            collections: (!message.collections.is_empty()).then_some(message.collections),
            from: message
                .from
                .as_ref()
                .map(subscription_start)
                .transpose()
                .map_err(host_error)?
                .ok_or_else(|| conversion_error("a subscription carries no start"))?,
        };
        domain.validate().map_err(|error| conversion_error(&error.to_string()))?;
        let host = Arc::clone(&self.host);
        let tenant = identity.tenant;
        let (response, feed) = run_host(self.as_ref(), MetricOperation::Subscribe, move || {
            host.backbone_subscribe(&tenant, &domain)
        })
        .await?;
        let (sender, receiver) = mpsc::channel(OUTBOUND_DEPTH);
        let (stop, stop_signal) = std::sync::mpsc::channel::<()>();
        let subscribed = pb::SubscribeResponse {
            event: Some(pb::subscribe_response::Event::Subscribed(
                pb::BackboneServiceSubscribeResponse {
                    subscription_id: response.subscription_id,
                    start_sequence: response.start_sequence,
                },
            )),
        };
        sender.send(Ok(subscribed)).await.map_err(|_| {
            tbep_status(
                Code::Internal,
                pb::ErrorCode::EngineFailure,
                "the subscription stream could not start",
            )
        })?;
        // The host feed is a synchronous channel: one dedicated thread
        // bridges it into the async response stream. A closed sender (the
        // client disconnected) ends the pump, which drops the feed and so
        // unsubscribes on the host side.
        let pump = std::thread::Builder::new()
            .name("tbep-subscription".into())
            .spawn(move || {
                loop {
                    // There is no cancellation-aware host feed API yet. A
                    // short timeout bounds how long an idle feed keeps its
                    // bridge thread after the response is dropped.
                    let notification =
                        match feed.recv_timeout(std::time::Duration::from_millis(100)) {
                            Ok(notification) => notification,
                            Err(std::sync::mpsc::RecvTimeoutError::Timeout) => {
                                if stop_signal.try_recv().is_ok() {
                                    break;
                                }
                                continue;
                            }
                            Err(std::sync::mpsc::RecvTimeoutError::Disconnected) => break,
                        };
                    let event = notification_event(notification);
                    let closed = is_closed(&event);
                    loop {
                        match sender.try_send(Ok(event.clone())) {
                            Ok(()) => break,
                            Err(mpsc::error::TrySendError::Full(_)) => {
                                // Keep the queue bounded and periodically
                                // observe cancellation instead of pinning a
                                // thread in blocking_send forever.
                                std::thread::sleep(std::time::Duration::from_millis(10));
                                if stop_signal.try_recv().is_ok() {
                                    return;
                                }
                            }
                            Err(mpsc::error::TrySendError::Closed(_)) => return,
                        }
                    }
                    if closed {
                        break;
                    }
                }
            })
            .map_err(|_| {
                tbep_status(
                    Code::Internal,
                    pb::ErrorCode::EngineFailure,
                    "the subscription pump could not start",
                )
            })?;
        Ok(Response::new(SubscriptionStream {
            receiver: ReceiverStream::new(receiver),
            stop: Some(stop),
            pump: Some(pump),
        }))
    }

    async fn acknowledge(
        &self,
        request: Request<pb::BackboneServiceAcknowledgeRequest>,
    ) -> Result<Response<pb::BackboneServiceAcknowledgeResponse>, Status> {
        self.counted();
        let identity = self.identity(&request)?;
        let message = request.into_inner();
        let domain = registry::BackboneAcknowledgeRequest {
            subscription_id: message.subscription_id,
            through_sequence: message.through_sequence,
        };
        let host = Arc::clone(&self.host);
        let tenant = identity.tenant;
        let response = run_host(self.as_ref(), MetricOperation::Subscribe, move || {
            host.backbone_acknowledge(&tenant, &domain)
        })
        .await?;
        Ok(Response::new(pb::BackboneServiceAcknowledgeResponse {
            subscription_id: response.subscription_id,
            acknowledged_through: response.acknowledged_through,
        }))
    }

    async fn unsubscribe(
        &self,
        request: Request<pb::BackboneServiceUnsubscribeRequest>,
    ) -> Result<Response<pb::BackboneServiceUnsubscribeResponse>, Status> {
        self.counted();
        let identity = self.identity(&request)?;
        let message = request.into_inner();
        let domain =
            registry::BackboneUnsubscribeRequest { subscription_id: message.subscription_id };
        let host = Arc::clone(&self.host);
        let tenant = identity.tenant;
        let response = run_host(self.as_ref(), MetricOperation::Subscribe, move || {
            host.backbone_unsubscribe(&tenant, &domain)
        })
        .await?;
        Ok(Response::new(pb::BackboneServiceUnsubscribeResponse {
            subscription_id: response.subscription_id,
        }))
    }
}

#[tonic::async_trait]
impl SyncService for Arc<TbepGrpcServer> {
    async fn session_setup(
        &self,
        request: Request<pb::SyncServiceSessionSetupRequest>,
    ) -> Result<Response<pb::SyncServiceSessionSetupResponse>, Status> {
        self.counted();
        let identity = self.identity(&request)?;
        let message = request.into_inner();
        if message.resume.len() > MAX_SYNC_TOKEN_BYTES {
            return Err(conversion_error("a sync resume token exceeds its byte bound"));
        }
        let domain = registry::SyncSessionSetupRequest {
            collection: message.collection,
            resume: (!message.resume.is_empty()).then_some(message.resume),
        };
        domain.validate().map_err(|error| conversion_error(&error.to_string()))?;
        let host = Arc::clone(&self.host);
        let tenant = identity.tenant;
        let response = run_host(self.as_ref(), MetricOperation::Subscribe, move || {
            host.sync_session_setup(&tenant, &domain)
        })
        .await?;
        Ok(Response::new(pb::SyncServiceSessionSetupResponse {
            session_id: response.session_id,
            checkpoint: response.checkpoint,
            retained_floor: response.retained_floor,
        }))
    }

    async fn delta_pull(
        &self,
        request: Request<pb::SyncServiceDeltaPullRequest>,
    ) -> Result<Response<pb::SyncServiceDeltaPullResponse>, Status> {
        self.counted();
        let identity = self.identity(&request)?;
        let message = request.into_inner();
        if message.from.len() > MAX_SYNC_TOKEN_BYTES {
            return Err(conversion_error("a sync cursor exceeds its byte bound"));
        }
        let domain =
            registry::SyncDeltaPullRequest { session_id: message.session_id, from: message.from };
        domain.validate().map_err(|error| conversion_error(&error.to_string()))?;
        let host = Arc::clone(&self.host);
        let tenant = identity.tenant;
        let response = run_host(self.as_ref(), MetricOperation::Subscribe, move || {
            host.sync_delta_pull(&tenant, &domain)
        })
        .await?;
        Ok(Response::new(pb::SyncServiceDeltaPullResponse {
            session_id: response.session_id,
            payload_codec: response.payload_codec,
            payload: response.payload,
            next: response.next,
            commits: response.commits,
            single_commit_oversize: response.single_commit_oversize,
        }))
    }

    async fn snapshot_pull(
        &self,
        request: Request<pb::SyncServiceSnapshotPullRequest>,
    ) -> Result<Response<pb::SyncServiceSnapshotPullResponse>, Status> {
        self.counted();
        let identity = self.identity(&request)?;
        let message = request.into_inner();
        let domain = registry::SyncSnapshotPullRequest { session_id: message.session_id };
        let host = Arc::clone(&self.host);
        let tenant = identity.tenant;
        let response = run_host(self.as_ref(), MetricOperation::Subscribe, move || {
            host.sync_snapshot_pull(&tenant, &domain)
        })
        .await?;
        Ok(Response::new(pb::SyncServiceSnapshotPullResponse {
            session_id: response.session_id,
            payload_codec: response.payload_codec,
            payload: response.payload,
            checkpoint: response.checkpoint,
        }))
    }

    async fn checkpoint_ack(
        &self,
        request: Request<pb::SyncServiceCheckpointAckRequest>,
    ) -> Result<Response<pb::SyncServiceCheckpointAckResponse>, Status> {
        self.counted();
        let identity = self.identity(&request)?;
        let message = request.into_inner();
        if message.checkpoint.len() > MAX_SYNC_TOKEN_BYTES {
            return Err(conversion_error("a sync checkpoint exceeds its byte bound"));
        }
        let domain = registry::SyncCheckpointAckRequest {
            session_id: message.session_id,
            checkpoint: message.checkpoint,
        };
        domain.validate().map_err(|error| conversion_error(&error.to_string()))?;
        let host = Arc::clone(&self.host);
        let tenant = identity.tenant;
        let response = run_host(self.as_ref(), MetricOperation::Subscribe, move || {
            host.sync_checkpoint_ack(&tenant, &domain)
        })
        .await?;
        Ok(Response::new(pb::SyncServiceCheckpointAckResponse { session_id: response.session_id }))
    }

    async fn mutation_submit(
        &self,
        request: Request<pb::SyncServiceMutationSubmitRequest>,
    ) -> Result<Response<pb::SyncServiceMutationSubmitResponse>, Status> {
        self.counted();
        let identity = self.identity(&request)?;
        let key = idempotency_key(&request)?;
        let message = request.into_inner();
        let domain = registry::SyncMutationSubmitRequest {
            session_id: message.session_id,
            document: message.document,
            change: message
                .change
                .as_ref()
                .map(sync_change)
                .transpose()
                .map_err(host_error)?
                .ok_or_else(|| conversion_error("a sync mutation carries no change"))?,
            usage: usage_admission(message.usage.as_ref(), &key, &identity.tenant)?,
        };
        self.require_mutation_usage(domain.usage.is_some())?;
        domain.validate().map_err(|error| conversion_error(&error.to_string()))?;
        let host = Arc::clone(&self.host);
        let tenant = identity.tenant;
        let response = run_host(self.as_ref(), MetricOperation::Write, move || {
            host.sync_mutation_submit(&tenant, &domain, &key)
        })
        .await?;
        Ok(Response::new(pb::SyncServiceMutationSubmitResponse {
            session_id: response.session_id,
            outcome: Some(mutation_outcome_pb(&response.outcome)),
        }))
    }

    async fn session_close(
        &self,
        request: Request<pb::SyncServiceSessionCloseRequest>,
    ) -> Result<Response<pb::SyncServiceSessionCloseResponse>, Status> {
        self.counted();
        let identity = self.identity(&request)?;
        let message = request.into_inner();
        let domain = registry::SyncSessionCloseRequest { session_id: message.session_id };
        let host = Arc::clone(&self.host);
        let tenant = identity.tenant;
        let response = run_host(self.as_ref(), MetricOperation::Subscribe, move || {
            host.sync_session_close(&tenant, &domain)
        })
        .await?;
        Ok(Response::new(pb::SyncServiceSessionCloseResponse { session_id: response.session_id }))
    }
}

#[tonic::async_trait]
impl SchemaService for Arc<TbepGrpcServer> {
    async fn declare(
        &self,
        request: Request<pb::SchemaServiceDeclareRequest>,
    ) -> Result<Response<pb::SchemaServiceDeclareResponse>, Status> {
        self.counted();
        let identity = self.identity(&request)?;
        let key = idempotency_key(&request)?;
        let message = request.into_inner();
        let domain = registry::SchemaDeclareRequest {
            collection: message.collection,
            schema: message.schema.as_ref().map(value_from_pb).transpose().map_err(host_error)?,
            operation_id: message.operation_id,
        };
        domain.validate().map_err(|error| conversion_error(&error.to_string()))?;
        let host = Arc::clone(&self.host);
        let tenant = identity.tenant;
        let response = run_host(self.as_ref(), MetricOperation::Write, move || {
            host.schema_declare(&tenant, &domain, &key)
        })
        .await?;
        Ok(Response::new(pb::SchemaServiceDeclareResponse {
            enforced_version: response.enforced_version.unwrap_or_default(),
            rule_count: response.rule_count,
        }))
    }

    async fn status(
        &self,
        request: Request<pb::SchemaServiceStatusRequest>,
    ) -> Result<Response<pb::SchemaServiceStatusResponse>, Status> {
        self.counted();
        let identity = self.identity(&request)?;
        let message = request.into_inner();
        let domain = registry::SchemaStatusRequest { collection: message.collection };
        let host = Arc::clone(&self.host);
        let tenant = identity.tenant;
        let response = run_host(self.as_ref(), MetricOperation::Read, move || {
            host.schema_status(&tenant, &domain)
        })
        .await?;
        Ok(Response::new(pb::SchemaServiceStatusResponse {
            enforced_version: response.enforced_version.unwrap_or_default(),
            rule_count: response.rule_count,
        }))
    }

    async fn validate(
        &self,
        request: Request<pb::SchemaServiceValidateRequest>,
    ) -> Result<Response<pb::SchemaServiceValidateResponse>, Status> {
        self.counted();
        let identity = self.identity(&request)?;
        let message = request.into_inner();
        let domain = registry::SchemaValidateRequest {
            collection: message.collection,
            document: message
                .document
                .as_ref()
                .map(value_from_pb)
                .transpose()
                .map_err(host_error)?
                .ok_or_else(|| conversion_error("a validation carries no document"))?,
        };
        domain.validate().map_err(|error| conversion_error(&error.to_string()))?;
        let host = Arc::clone(&self.host);
        let tenant = identity.tenant;
        let response = run_host(self.as_ref(), MetricOperation::Read, move || {
            host.schema_validate(&tenant, &domain)
        })
        .await?;
        Ok(Response::new(pb::SchemaServiceValidateResponse {
            enforced_version: response.enforced_version.unwrap_or_default(),
            valid: response.valid,
            violations: response.violations.iter().map(schema_violation_pb).collect(),
        }))
    }

    async fn migration_status(
        &self,
        request: Request<pb::MigrationStatusRequest>,
    ) -> Result<Response<pb::MigrationStatusResponse>, Status> {
        self.counted();
        let identity = self.identity(&request)?;
        let message = request.into_inner();
        let domain = registry::MigrationStatusRequest { collection: message.collection };
        let host = Arc::clone(&self.host);
        let tenant = identity.tenant;
        let response = run_host(self.as_ref(), MetricOperation::Read, move || {
            host.migration_status(&tenant, &domain)
        })
        .await?;
        Ok(Response::new(pb::MigrationStatusResponse {
            state: Some(migration_state(&response.state)),
        }))
    }

    async fn migration_dry_run(
        &self,
        request: Request<pb::MigrationDryRunRequest>,
    ) -> Result<Response<pb::MigrationDryRunResponse>, Status> {
        self.counted();
        let identity = self.identity(&request)?;
        let message = request.into_inner();
        let contract = message
            .contract
            .as_ref()
            .map(migration_contract)
            .transpose()
            .map_err(host_error)?
            .ok_or_else(|| conversion_error("a dry run carries no contract"))?;
        let domain = registry::MigrationDryRunRequest { contract };
        domain.validate().map_err(|error| conversion_error(&error.to_string()))?;
        let host = Arc::clone(&self.host);
        let tenant = identity.tenant;
        let response = run_host(self.as_ref(), MetricOperation::Other, move || {
            host.migration_dry_run(&tenant, &domain)
        })
        .await?;
        Ok(Response::new(pb::MigrationDryRunResponse {
            plan: Some(migration_plan(&response.plan)),
        }))
    }

    async fn migration_apply(
        &self,
        request: Request<pb::MigrationApplyRequest>,
    ) -> Result<Response<pb::MigrationApplyResponse>, Status> {
        self.counted();
        let identity = self.identity(&request)?;
        let key = idempotency_key(&request)?;
        let message = request.into_inner();
        let contract = message
            .contract
            .as_ref()
            .map(migration_contract)
            .transpose()
            .map_err(host_error)?
            .ok_or_else(|| conversion_error("an apply carries no contract"))?;
        let domain = registry::MigrationApplyRequest {
            contract,
            acknowledge_not_rollbackable: message.acknowledge_not_rollbackable,
            operation_id: message.operation_id,
        };
        domain.validate().map_err(|error| conversion_error(&error.to_string()))?;
        let host = Arc::clone(&self.host);
        let tenant = identity.tenant;
        let response = run_host(self.as_ref(), MetricOperation::Write, move || {
            host.migration_apply(&tenant, &domain, &key)
        })
        .await?;
        Ok(Response::new(pb::MigrationApplyResponse {
            receipt: Some(migration_receipt(&response.receipt)),
        }))
    }
}

#[tonic::async_trait]
impl ReplicationService for Arc<TbepGrpcServer> {
    async fn conflicts(
        &self,
        request: Request<pb::ReplicationServiceConflictsRequest>,
    ) -> Result<Response<pb::ReplicationServiceConflictsResponse>, Status> {
        self.counted();
        let identity = self.identity(&request)?;
        let host = Arc::clone(&self.host);
        let tenant = identity.tenant;
        let response = run_host(self.as_ref(), MetricOperation::Repair, move || {
            host.replication_conflicts(&tenant)
        })
        .await?;
        Ok(Response::new(pb::ReplicationServiceConflictsResponse {
            member: response.member,
            records: response.records.iter().map(conflict_record).collect(),
        }))
    }

    async fn fenced(
        &self,
        request: Request<pb::ReplicationServiceFencedRequest>,
    ) -> Result<Response<pb::ReplicationServiceFencedResponse>, Status> {
        self.counted();
        let identity = self.identity(&request)?;
        let host = Arc::clone(&self.host);
        let tenant = identity.tenant;
        let response = run_host(self.as_ref(), MetricOperation::Repair, move || {
            host.replication_fenced(&tenant)
        })
        .await?;
        Ok(Response::new(pb::ReplicationServiceFencedResponse {
            member: response.member,
            records: response.records.iter().map(fenced_record).collect(),
        }))
    }

    async fn depositions(
        &self,
        request: Request<pb::ReplicationServiceDepositionsRequest>,
    ) -> Result<Response<pb::ReplicationServiceDepositionsResponse>, Status> {
        self.counted();
        let identity = self.identity(&request)?;
        let host = Arc::clone(&self.host);
        let tenant = identity.tenant;
        let response = run_host(self.as_ref(), MetricOperation::Repair, move || {
            host.replication_depositions(&tenant)
        })
        .await?;
        Ok(Response::new(pb::ReplicationServiceDepositionsResponse {
            member: response.member,
            records: response.records.iter().map(deposition_record).collect(),
        }))
    }

    async fn cursor(
        &self,
        request: Request<pb::ReplicationServiceCursorRequest>,
    ) -> Result<Response<pb::ReplicationServiceCursorResponse>, Status> {
        self.counted();
        let identity = self.identity(&request)?;
        let host = Arc::clone(&self.host);
        let tenant = identity.tenant;
        let response = run_host(self.as_ref(), MetricOperation::Repair, move || {
            host.replication_cursor(&tenant)
        })
        .await?;
        Ok(Response::new(pb::ReplicationServiceCursorResponse {
            member: response.member,
            position: response.position.unwrap_or_default(),
        }))
    }

    async fn health(
        &self,
        request: Request<pb::ReplicationServiceHealthRequest>,
    ) -> Result<Response<pb::ReplicationServiceHealthResponse>, Status> {
        self.counted();
        let identity = self.identity(&request)?;
        let host = Arc::clone(&self.host);
        let tenant = identity.tenant;
        let response = run_host(self.as_ref(), MetricOperation::Repair, move || {
            host.replication_health(&tenant)
        })
        .await?;
        Ok(Response::new(pb::ReplicationServiceHealthResponse {
            member: response.member,
            status: replication_status_pb(response.status),
        }))
    }

    async fn denials(
        &self,
        request: Request<pb::ReplicationServiceDenialsRequest>,
    ) -> Result<Response<pb::ReplicationServiceDenialsResponse>, Status> {
        self.counted();
        let identity = self.identity(&request)?;
        let host = Arc::clone(&self.host);
        let tenant = identity.tenant;
        let response = run_host(self.as_ref(), MetricOperation::Repair, move || {
            host.replication_denials(&tenant)
        })
        .await?;
        Ok(Response::new(pb::ReplicationServiceDenialsResponse {
            member: response.member,
            records: response.records.iter().map(denial_record).collect(),
        }))
    }
}

#[tonic::async_trait]
impl MaintenanceService for Arc<TbepGrpcServer> {
    async fn topology_read(
        &self,
        request: Request<pb::MaintenanceServiceTopologyReadRequest>,
    ) -> Result<Response<pb::MaintenanceServiceTopologyReadResponse>, Status> {
        self.counted();
        let identity = self.identity(&request)?;
        let host = Arc::clone(&self.host);
        let tenant = identity.tenant;
        let response =
            run_host(self.as_ref(), MetricOperation::Other, move || host.topology_read(&tenant))
                .await?;
        Ok(Response::new(pb::MaintenanceServiceTopologyReadResponse {
            binding: self.binding as i32,
            subsystems: response.subsystems,
            engine_topology_available: response.engine_topology_available,
            cluster_mode: cluster_mode_pb(response.cluster_mode),
        }))
    }

    async fn admin_status(
        &self,
        request: Request<pb::MaintenanceServiceAdminStatusRequest>,
    ) -> Result<Response<pb::MaintenanceServiceAdminStatusResponse>, Status> {
        self.counted();
        let identity = self.identity(&request)?;
        let _ = identity;
        let host = Arc::clone(&self.host);
        let served = self.frames_served();
        let response =
            run_host(self.as_ref(), MetricOperation::Other, move || host.admin_status()).await?;
        Ok(Response::new(pb::MaintenanceServiceAdminStatusResponse {
            registry_version: response.registry_version,
            frames_served: response.frames_served.max(served),
            sessions: response.sessions,
            subscriptions: response.subscriptions,
            scopes: response.scopes,
        }))
    }

    async fn supervision_health(
        &self,
        request: Request<pb::MaintenanceServiceSupervisionHealthRequest>,
    ) -> Result<Response<pb::MaintenanceServiceSupervisionHealthResponse>, Status> {
        self.counted();
        let identity = self.identity(&request)?;
        let _ = identity;
        let host = Arc::clone(&self.host);
        let served = self.frames_served();
        let response =
            run_host(self.as_ref(), MetricOperation::Other, move || host.health()).await?;
        Ok(Response::new(pb::MaintenanceServiceSupervisionHealthResponse {
            state: match response.state {
                registry::HealthState::Starting => pb::HealthState::Starting as i32,
                registry::HealthState::Serving => pb::HealthState::Serving as i32,
                registry::HealthState::Draining => pb::HealthState::Draining as i32,
                registry::HealthState::Stopped => pb::HealthState::Stopped as i32,
            },
            sessions: response.sessions,
            subscriptions: response.subscriptions,
            frames_served: response.frames_served.max(served),
        }))
    }

    async fn supervision_shutdown(
        &self,
        request: Request<pb::MaintenanceServiceSupervisionShutdownRequest>,
    ) -> Result<Response<pb::MaintenanceServiceSupervisionShutdownResponse>, Status> {
        self.counted();
        let identity = self.identity(&request)?;
        let _ = identity;
        let message = request.into_inner();
        let domain = registry::SupervisionShutdownRequest {
            reason: match pb::ShutdownReason::try_from(message.reason) {
                Ok(pb::ShutdownReason::Operator) => registry::ShutdownReason::Operator,
                Ok(pb::ShutdownReason::Supervisor) => registry::ShutdownReason::Supervisor,
                _ => {
                    return Err(conversion_error("a shutdown carries no reason"));
                }
            },
        };
        let host = Arc::clone(&self.host);
        let response =
            run_host(self.as_ref(), MetricOperation::Other, move || host.begin_shutdown(&domain))
                .await?;
        Ok(Response::new(pb::MaintenanceServiceSupervisionShutdownResponse {
            state: match response.state {
                registry::HealthState::Starting => pb::HealthState::Starting as i32,
                registry::HealthState::Serving => pb::HealthState::Serving as i32,
                registry::HealthState::Draining => pb::HealthState::Draining as i32,
                registry::HealthState::Stopped => pb::HealthState::Stopped as i32,
            },
            in_flight: response.in_flight,
        }))
    }
}

#[tonic::async_trait]
impl UsageService for Arc<TbepGrpcServer> {
    async fn record(
        &self,
        request: Request<pb::UsageServiceRecordRequest>,
    ) -> Result<Response<pb::UsageServiceRecordResponse>, Status> {
        self.counted();
        let identity = self.require_usage_rpc(&request)?;
        let key = idempotency_key(&request)?;
        let message = request.into_inner();
        let meter = match pb::UsageRecordMeter::try_from(message.meter) {
            Ok(pb::UsageRecordMeter::AdmittedRequestUnits) => {
                registry::UsageRecordMeter::AdmittedRequestUnits
            }
            Ok(pb::UsageRecordMeter::PublicEgressBytes) => {
                registry::UsageRecordMeter::PublicEgressBytes
            }
            _ => return Err(conversion_error("a usage record carries no supported meter")),
        };
        let domain = registry::UsageRecordRequest {
            meter,
            project: message.project,
            database: message.database,
            partition: message.partition,
            origin_region: usage_region_from_pb(message.origin_region)?,
            fact_id: message.fact_id,
            recorded_at_millis: message.recorded_at_millis,
            meter_version: message.meter_version,
            integer_units: message.integer_units,
        };
        domain.validate_for_key(&key).map_err(|error| conversion_error(&error.to_string()))?;
        if domain.project != identity.tenant.project || domain.database != identity.tenant.database
        {
            return Err(conversion_error(
                "the usage record does not match the bound Project and Database",
            ));
        }
        let host = Arc::clone(&self.host);
        let tenant = identity.tenant;
        let response = run_host(self.as_ref(), MetricOperation::Write, move || {
            host.usage_record(&tenant, &domain, &key)
        })
        .await?;
        response.validate().map_err(|error| {
            host_error(HostingError::UsageCorrupt(format!(
                "the host returned an invalid usage receipt: {error}"
            )))
        })?;
        Ok(Response::new(pb::UsageServiceRecordResponse {
            sequence: response.sequence,
            replica_epoch: response.replica_epoch,
            already_recorded: response.already_recorded,
        }))
    }

    async fn export(
        &self,
        request: Request<pb::UsageServiceExportRequest>,
    ) -> Result<Response<pb::UsageServiceExportResponse>, Status> {
        self.counted();
        let identity = self.require_usage_rpc(&request)?;
        let message = request.into_inner();
        let domain = registry::UsageExportRequest {
            window_start_millis: message.window_start_millis,
            window_end_millis: message.window_end_millis,
            page_size: message.page_size,
            continuation: message.continuation,
        };
        domain.validate().map_err(|error| conversion_error(&error.to_string()))?;
        let host = Arc::clone(&self.host);
        let tenant = identity.tenant;
        let expected_tenant = tenant.tenant.clone();
        let response = run_host(self.as_ref(), MetricOperation::Read, move || {
            host.usage_export(&tenant, &domain)
        })
        .await?;
        response.validate().map_err(|error| {
            host_error(HostingError::UsageCorrupt(format!(
                "the host returned an invalid usage page: {error}"
            )))
        })?;
        if response.facts.iter().any(|fact| fact.tenant != expected_tenant) {
            return Err(host_error(HostingError::UsageCorrupt(
                "the host returned a fact outside the bound tenant".into(),
            )));
        }
        Ok(Response::new(pb::UsageServiceExportResponse {
            facts: response.facts.iter().map(usage_fact_pb).collect(),
            continuation: response.continuation,
            snapshot_sequence: response.snapshot_sequence,
        }))
    }
}

#[tonic::async_trait]
impl TierService for Arc<TbepGrpcServer> {
    async fn configure(
        &self,
        request: Request<pb::TierServiceConfigureRequest>,
    ) -> Result<Response<pb::TierServiceConfigureResponse>, Status> {
        self.counted();
        let identity = self.identity(&request)?;
        let key = idempotency_key(&request)?;
        let message = request.into_inner();
        let domain = registry::TierConfigureRequest {
            policy_id: message.policy_id,
            version: message.version,
            nvme_budget_bytes: message.nvme_budget_bytes,
            min_residence_moves: message.min_residence_moves,
            promotion_threshold: message.promotion_threshold,
            pinned_segment_ids: message.pinned_segment_ids,
        };
        domain.validate().map_err(|error| conversion_error(&error.to_string()))?;
        let host = Arc::clone(&self.host);
        let tenant = identity.tenant;
        let response = run_host(self.as_ref(), MetricOperation::Write, move || {
            host.tier_configure(&tenant, &domain, &key)
        })
        .await?;
        Ok(Response::new(pb::TierServiceConfigureResponse {
            policy_id: response.policy_id,
            version: response.version,
            paused: response.paused,
        }))
    }

    async fn inspect(
        &self,
        request: Request<pb::TierServiceInspectRequest>,
    ) -> Result<Response<pb::TierServiceInspectResponse>, Status> {
        self.counted();
        let identity = self.identity(&request)?;
        let host = Arc::clone(&self.host);
        let tenant = identity.tenant;
        let response =
            run_host(self.as_ref(), MetricOperation::Read, move || host.tier_inspect(&tenant))
                .await?;
        Ok(Response::new(pb::TierServiceInspectResponse {
            configured: response.configured,
            paused: response.paused,
            manifest_epoch: response.manifest_epoch,
            object_count: response.object_count,
            nvme_bytes: response.nvme_bytes,
        }))
    }

    async fn dry_run(
        &self,
        request: Request<pb::TierServiceDryRunRequest>,
    ) -> Result<Response<pb::TierServiceDryRunResponse>, Status> {
        self.counted();
        let identity = self.identity(&request)?;
        let host = Arc::clone(&self.host);
        let tenant = identity.tenant;
        let response =
            run_host(self.as_ref(), MetricOperation::Other, move || host.tier_dry_run(&tenant))
                .await?;
        Ok(Response::new(pb::TierServiceDryRunResponse {
            receipt: Some(tier_move_receipt(&response.receipt)),
        }))
    }

    async fn explain(
        &self,
        request: Request<pb::TierServiceExplainRequest>,
    ) -> Result<Response<pb::TierServiceExplainResponse>, Status> {
        self.counted();
        let identity = self.identity(&request)?;
        let host = Arc::clone(&self.host);
        let tenant = identity.tenant;
        let response =
            run_host(self.as_ref(), MetricOperation::Read, move || host.tier_explain(&tenant))
                .await?;
        Ok(Response::new(pb::TierServiceExplainResponse {
            receipt: Some(tier_move_receipt(&response.receipt)),
        }))
    }

    async fn run(
        &self,
        request: Request<pb::TierServiceRunRequest>,
    ) -> Result<Response<pb::TierServiceRunResponse>, Status> {
        self.counted();
        let identity = self.identity(&request)?;
        let key = idempotency_key(&request)?;
        let host = Arc::clone(&self.host);
        let tenant = identity.tenant;
        let response = run_host(self.as_ref(), MetricOperation::Compaction, move || {
            host.tier_run(&tenant, &key)
        })
        .await?;
        Ok(Response::new(pb::TierServiceRunResponse {
            receipt: Some(tier_move_receipt(&response.receipt)),
        }))
    }

    async fn pause(
        &self,
        request: Request<pb::TierServicePauseRequest>,
    ) -> Result<Response<pb::TierServicePauseResponse>, Status> {
        self.counted();
        let identity = self.identity(&request)?;
        let key = idempotency_key(&request)?;
        let host = Arc::clone(&self.host);
        let tenant = identity.tenant;
        let response =
            run_host(self.as_ref(), MetricOperation::Write, move || host.tier_pause(&tenant, &key))
                .await?;
        Ok(Response::new(pb::TierServicePauseResponse { paused: response.paused }))
    }

    async fn resume(
        &self,
        request: Request<pb::TierServiceResumeRequest>,
    ) -> Result<Response<pb::TierServiceResumeResponse>, Status> {
        self.counted();
        let identity = self.identity(&request)?;
        let key = idempotency_key(&request)?;
        let host = Arc::clone(&self.host);
        let tenant = identity.tenant;
        let response = run_host(self.as_ref(), MetricOperation::Write, move || {
            host.tier_resume(&tenant, &key)
        })
        .await?;
        Ok(Response::new(pb::TierServiceResumeResponse { paused: response.paused }))
    }
}
