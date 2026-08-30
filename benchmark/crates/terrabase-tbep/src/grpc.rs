//! The tonic serving layer of TBEP (ADR 0025): the gRPC contract in
//! `proto/terrabase/v1/engine.proto`, served over the hosting seam.
//!
//! Responsibilities, in the order a request meets them:
//!
//! 1. **Identity.** Every RPC except `Channel/Bind` requires the
//!    `x-tb-principal` and `x-tb-tenant` metadata; a missing identity
//!    fails closed with the `unbound_channel` error code. The transport
//!    is expected to be mutually authenticated TLS in production — the
//!    metadata names the application identity within that channel, it
//!    does not authenticate it.
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

use std::collections::BTreeMap;
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
use tokio_stream::wrappers::ReceiverStream;

use tonic::Code;
use tonic::Request;
use tonic::Response;
use tonic::Status;
use tonic::transport::Certificate as TransportCertificate;
use tonic::transport::Identity as TransportIdentity;
use tonic::transport::ServerTlsConfig;

use terrabase::observability::{
    MetricsRegistry, Operation as MetricOperation, Outcome as MetricOutcome, Region as MetricRegion,
};

use crate::host::HostingError;
use crate::host::TbepHost;
use crate::host::TbepNotification;
use crate::proto::terrabase::v1 as pb;
use crate::proto::terrabase::v1::backbone_server::Backbone;
use crate::proto::terrabase::v1::backbone_server::BackboneServer;
use crate::proto::terrabase::v1::channel_server::Channel;
use crate::proto::terrabase::v1::channel_server::ChannelServer;
use crate::proto::terrabase::v1::engine_server::Engine;
use crate::proto::terrabase::v1::engine_server::EngineServer;
use crate::proto::terrabase::v1::maintenance_server::Maintenance;
use crate::proto::terrabase::v1::maintenance_server::MaintenanceServer;
use crate::proto::terrabase::v1::replication_server::Replication;
use crate::proto::terrabase::v1::replication_server::ReplicationServer;
use crate::proto::terrabase::v1::schema_server::Schema;
use crate::proto::terrabase::v1::schema_server::SchemaServer;
use crate::proto::terrabase::v1::sync_server::Sync;
use crate::proto::terrabase::v1::sync_server::SyncServer;
use crate::proto::terrabase::v1::tier_server::Tier;
use crate::proto::terrabase::v1::tier_server::TierServer;
use crate::proto::terrabase::v1::usage_server::Usage;
use crate::proto::terrabase::v1::usage_server::UsageServer;
use crate::registry;
use crate::value::TbepValue;

/// The metadata key carrying the backend-supplied principal.
pub const PRINCIPAL_METADATA: &str = "x-tb-principal";
/// The metadata key carrying the backend-supplied tenant.
pub const TENANT_METADATA: &str = "x-tb-tenant";
/// The metadata key carrying a mutation's retry identity.
pub const IDEMPOTENCY_METADATA: &str = "x-tb-idempotency-key";

/// The idempotency-key bound in bytes.
pub const MAX_IDEMPOTENCY_BYTES: usize = 128;

/// The maximum UTF-8 byte length of the principal metadata value.
pub const MAX_PRINCIPAL_BYTES: usize = 128;

/// The maximum UTF-8 byte length of the tenant metadata value.
pub const MAX_TENANT_BYTES: usize = 64;

/// Default maximum in-flight host operations admitted by the overload gate.
pub const MAX_INFLIGHT_OPERATIONS: usize = 128;

/// The depth of one subscription's outbound queue: a bounded backlog
/// that keeps a slow stream consumer from pinning engine memory.
pub const OUTBOUND_DEPTH: usize = 64;

const MAX_SYNC_TOKEN_BYTES: usize = 1057;

/// Validated resource and authenticated-gateway policy for one serving
/// instance. Certificate values are SHA-256 fingerprints of the leaf DER.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct TbepGrpcConfig {
    max_message_bytes: usize,
    max_inflight: usize,
    request_timeout: Duration,
    tls_handshake_timeout: Duration,
    shutdown_grace: Duration,
    gateway_certificates: BTreeMap<String, [u8; 32]>,
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
        gateway_certificates: BTreeMap<String, [u8; 32]>,
    ) -> std::io::Result<Self> {
        let valid_principals = gateway_certificates.keys().all(|principal| {
            !principal.is_empty()
                && principal.len() <= MAX_PRINCIPAL_BYTES
                && principal == principal.trim()
                && !principal.chars().any(char::is_control)
        });
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
            gateway_certificates,
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

impl Default for TbepGrpcConfig {
    fn default() -> Self {
        Self {
            max_message_bytes: registry::MAX_FRAME_BYTES,
            max_inflight: MAX_INFLIGHT_OPERATIONS,
            request_timeout: Duration::from_secs(30),
            tls_handshake_timeout: Duration::from_secs(5),
            shutdown_grace: Duration::from_secs(5),
            gateway_certificates: BTreeMap::new(),
        }
    }
}

/// The running gRPC listener handle.
pub struct TbepGrpcHandle {
    local_addr: Option<SocketAddr>,
    socket_path: Option<std::path::PathBuf>,
    shutdown: Option<tokio::sync::oneshot::Sender<()>>,
    completion: Option<std::sync::mpsc::Receiver<()>>,
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

    /// Signals the serving loop to finish and waits, bounded, for its
    /// graceful drain. A client that stays connected past the bound
    /// cannot pin an operator shutdown; the serving thread then ends on
    /// its own once the connection closes.
    pub fn shutdown(&mut self) {
        if let Some(sender) = self.shutdown.take() {
            let _ = sender.send(());
        }
        if let Some(completion) = self.completion.take() {
            let _ = completion.recv_timeout(std::time::Duration::from_secs(5));
        }
        // Do not unlink the path here.  A pathname can have been replaced
        // after bind, and removing it would race with the owner of the
        // replacement (including a newly active socket).  Operators can
        // remove the now-stale socket entry explicitly; serve_uds refuses
        // every existing path, so it never guesses ownership.
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

    fn identity<T>(&self, request: &Request<T>) -> Result<Identity, Status> {
        let identity = Identity::of(request)?;
        if self.binding == pb::BindingKind::NetworkMtls {
            self.authenticate_gateway(request, &identity.principal)?;
        }
        Ok(identity)
    }

    fn authenticate_gateway<T>(&self, request: &Request<T>, principal: &str) -> Result<(), Status> {
        let expected = self.config.gateway_certificates.get(principal).ok_or_else(|| {
            tbep_status(
                Code::PermissionDenied,
                pb::ErrorCode::OperationRefused,
                "the authenticated client is not an admitted gateway principal",
            )
        })?;
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
        let observed = ring::digest::digest(&ring::digest::SHA256, leaf.as_ref());
        ring::constant_time::verify_slices_are_equal(expected, observed.as_ref()).map_err(|_| {
            tbep_status(
                Code::PermissionDenied,
                pb::ErrorCode::OperationRefused,
                "the gateway principal does not match the authenticated client certificate",
            )
        })
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
        let (done, completion) = std::sync::mpsc::channel::<()>();
        std::thread::Builder::new()
            .name("tbep-grpc-uds".into())
            .spawn(move || {
                let _ = runtime.block_on(async move {
                    let routes = serve_routes(Arc::clone(&server), None)
                        .expect("the unencrypted route table builds");
                    routes
                        .serve_with_incoming_shutdown(
                            tokio_stream::wrappers::UnixListenerStream::new(listener),
                            async {
                                let _ = signal.await;
                            },
                        )
                        .await
                });
                let _ = done.send(());
            })
            .map_err(std::io::Error::other)?;
        Ok(TbepGrpcHandle {
            local_addr: None,
            socket_path: Some(path.to_path_buf()),
            shutdown: Some(shutdown),
            completion: Some(completion),
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
        let Some(client_ca_pem) = client_ca_pem.filter(|bundle| !bundle.trim().is_empty()) else {
            return Err(std::io::Error::new(
                std::io::ErrorKind::InvalidInput,
                "production TLS requires a non-empty client CA bundle for mutual authentication",
            ));
        };
        let identity = TransportIdentity::from_pem(server_pem, key_pem);
        let mut tls =
            ServerTlsConfig::new().identity(identity).timeout(self.config.tls_handshake_timeout());
        tls = tls.client_ca_root(TransportCertificate::from_pem(client_ca_pem));
        let server = Arc::clone(self);
        let (shutdown, signal) = tokio::sync::oneshot::channel::<()>();
        let (addr_tx, addr_rx) = std::sync::mpsc::channel();
        let (done, completion) = std::sync::mpsc::channel::<()>();
        std::thread::Builder::new()
            .name("tbep-grpc-tls".into())
            .spawn(move || {
                let runtime = tokio::runtime::Builder::new_multi_thread()
                    .worker_threads(2)
                    .enable_all()
                    .build()
                    .expect("the TBEP serving runtime starts");
                let _context = runtime.enter();
                runtime.block_on(async move {
                    let Ok(listener) = tokio::net::TcpListener::bind(address).await else {
                        let _ = addr_tx.send(None);
                        return;
                    };
                    let Ok(local) = listener.local_addr() else {
                        let _ = addr_tx.send(None);
                        return;
                    };
                    let Ok(routes) = serve_routes(Arc::clone(&server), Some(tls)) else {
                        let _ = addr_tx.send(None);
                        return;
                    };
                    let _ = addr_tx.send(Some(local));
                    let _ = routes
                        .serve_with_incoming_shutdown(
                            tokio_stream::wrappers::TcpListenerStream::new(listener),
                            async {
                                let _ = signal.await;
                            },
                        )
                        .await;
                });
                let _ = done.send(());
            })
            .map_err(std::io::Error::other)?;
        let local_addr = addr_rx.recv().ok().flatten();
        if local_addr.is_none() {
            let _ = completion.recv_timeout(std::time::Duration::from_secs(5));
            return Err(std::io::Error::new(
                std::io::ErrorKind::AddrNotAvailable,
                "the TLS listener could not bind or configure its routes",
            ));
        }
        Ok(TbepGrpcHandle {
            local_addr,
            socket_path: None,
            shutdown: Some(shutdown),
            completion: Some(completion),
        })
    }
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
            ChannelServer::new(Arc::clone(&server))
                .max_decoding_message_size(message_bytes)
                .max_encoding_message_size(message_bytes),
        )
        .add_service(
            EngineServer::new(Arc::clone(&server))
                .max_decoding_message_size(message_bytes)
                .max_encoding_message_size(message_bytes),
        )
        .add_service(
            BackboneServer::new(Arc::clone(&server))
                .max_decoding_message_size(message_bytes)
                .max_encoding_message_size(message_bytes),
        )
        .add_service(
            SyncServer::new(Arc::clone(&server))
                .max_decoding_message_size(message_bytes)
                .max_encoding_message_size(message_bytes),
        )
        .add_service(
            SchemaServer::new(Arc::clone(&server))
                .max_decoding_message_size(message_bytes)
                .max_encoding_message_size(message_bytes),
        )
        .add_service(
            ReplicationServer::new(Arc::clone(&server))
                .max_decoding_message_size(message_bytes)
                .max_encoding_message_size(message_bytes),
        )
        .add_service(
            MaintenanceServer::new(Arc::clone(&server))
                .max_decoding_message_size(message_bytes)
                .max_encoding_message_size(message_bytes),
        )
        .add_service(
            UsageServer::new(Arc::clone(&server))
                .max_decoding_message_size(message_bytes)
                .max_encoding_message_size(message_bytes),
        )
        .add_service(
            TierServer::new(server)
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
    tenant: String,
}

impl Identity {
    fn of<T>(request: &Request<T>) -> Result<Self, Status> {
        let metadata = request.metadata();
        let principal = metadata_value(metadata, PRINCIPAL_METADATA, MAX_PRINCIPAL_BYTES)?;
        let tenant = metadata_value(metadata, TENANT_METADATA, MAX_TENANT_BYTES)?;
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
const MAX_VALUE_TEXT_BYTES: usize = 1 << 20;
const MAX_VALUE_LIST_ITEMS: usize = 4096;
const MAX_VALUE_MAP_ENTRIES: usize = 1024;
const MAX_VALUE_NAME_BYTES: usize = 64;

fn value_from_pb(value: &pb::Value) -> Result<TbepValue, HostingError> {
    value_from_pb_depth(value, 0)
}

fn value_from_pb_depth(value: &pb::Value, depth: usize) -> Result<TbepValue, HostingError> {
    if depth > MAX_VALUE_DEPTH {
        return Err(invalid("a value exceeds its nesting bound"));
    }
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
            TbepValue::Array(
                list.items
                    .iter()
                    .map(|item| value_from_pb_depth(item, depth + 1))
                    .collect::<Result<Vec<_>, _>>()?,
            )
        }
        pb::value::Kind::Map(map) => {
            if map.entries.len() > MAX_VALUE_MAP_ENTRIES {
                return Err(invalid("a value map exceeds its entry bound"));
            }
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
                    )?,
                ));
            }
            fields.sort_by(|a, b| a.0.cmp(&b.0));
            TbepValue::Object(fields).canonical()
        }
    })
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
        pb::Acknowledgement::AckUnspecified => None,
        pb::Acknowledgement::AckRegionalQuorum => Some(registry::Acknowledgement::RegionalQuorum),
        pb::Acknowledgement::AckGlobalQuorum => Some(registry::Acknowledgement::GlobalQuorum),
        pb::Acknowledgement::AckBackup => Some(registry::Acknowledgement::BackupAck),
    }
}

fn acknowledgement_pb(value: Option<registry::Acknowledgement>) -> i32 {
    match value {
        None | Some(registry::Acknowledgement::RegionalQuorum) => {
            pb::Acknowledgement::AckRegionalQuorum as i32
        }
        Some(registry::Acknowledgement::GlobalQuorum) => {
            pb::Acknowledgement::AckGlobalQuorum as i32
        }
        Some(registry::Acknowledgement::BackupAck) => pb::Acknowledgement::AckBackup as i32,
    }
}

fn backup_outcome_pb(value: Option<registry::BackupOutcome>) -> i32 {
    match value {
        None | Some(registry::BackupOutcome::None) => pb::BackupOutcome::BackupNone as i32,
        Some(registry::BackupOutcome::Full) => pb::BackupOutcome::BackupFull as i32,
        Some(registry::BackupOutcome::Partial) => pb::BackupOutcome::BackupPartial as i32,
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

fn notification_event(notification: TbepNotification) -> pb::BackboneStreamEvent {
    let event = match notification {
        TbepNotification::Batch(batch) => {
            pb::backbone_stream_event::Event::Batch(pb::SubscriptionBatch {
                subscription_id: batch.subscription_id,
                from_sequence: batch.from_sequence,
                events: batch.events.iter().map(change_event).collect(),
                next_sequence: batch.next_sequence,
            })
        }
        TbepNotification::Trigger(trigger) => {
            pb::backbone_stream_event::Event::Trigger(pb::TriggerNotification {
                subscription_id: trigger.subscription_id,
                collection: trigger.collection.clone(),
                sequence: trigger.sequence,
                operation_id: trigger.operation_id.clone(),
            })
        }
        TbepNotification::Closed(closed) => {
            pb::backbone_stream_event::Event::Closed(pb::SubscriptionClosed {
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
    pb::BackboneStreamEvent { event: Some(event) }
}

fn is_closed(event: &pb::BackboneStreamEvent) -> bool {
    matches!(event.event, Some(pb::backbone_stream_event::Event::Closed(_)))
}

/// The bounded response stream for one backbone subscription.
//
// It owns the stop signal for the synchronous feed pump.
/// Dropping the response (for example, when a client cancels or disconnects)
/// wakes both an idle feed receive and a pump waiting for queue capacity.
pub struct SubscriptionStream {
    receiver: ReceiverStream<Result<pb::BackboneStreamEvent, Status>>,
    stop: Option<std::sync::mpsc::Sender<()>>,
}

impl Stream for SubscriptionStream {
    type Item = Result<pb::BackboneStreamEvent, Status>;

    fn poll_next(mut self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<Option<Self::Item>> {
        Pin::new(&mut self.receiver).poll_next(cx)
    }
}

impl Drop for SubscriptionStream {
    fn drop(&mut self) {
        if let Some(stop) = self.stop.take() {
            let _ = stop.send(());
        }
    }
}

// ---------------------------------------------------------------------------
// Service implementations
// ---------------------------------------------------------------------------

#[tonic::async_trait]
impl Channel for Arc<TbepGrpcServer> {
    async fn bind(
        &self,
        request: Request<pb::ChannelBindRequest>,
    ) -> Result<Response<pb::ChannelBindResponse>, Status> {
        self.counted();
        if self.binding == pb::BindingKind::NetworkMtls {
            self.authenticate_gateway(&request, &request.get_ref().principal)?;
        }
        let message = request.into_inner();
        let domain =
            registry::ChannelBindRequest { principal: message.principal, tenant: message.tenant };
        if domain.principal.is_empty() || domain.tenant.is_empty() {
            return Err(conversion_error("a bind carries no application identity"));
        }
        domain.validate().map_err(|error| conversion_error(&error.to_string()))?;
        let expected_tenant = domain.tenant.clone();
        let host = Arc::clone(&self.host);
        let response =
            run_host(self.as_ref(), MetricOperation::Other, move || host.bind_channel(&domain))
                .await?;
        if response.tenant != expected_tenant || response.validate().is_err() {
            return Err(tbep_status(
                Code::Internal,
                pb::ErrorCode::EngineFailure,
                "the host returned an invalid channel binding",
            ));
        }
        Ok(Response::new(pb::ChannelBindResponse {
            tenant: response.tenant,
            serving_instance: response.serving_instance,
        }))
    }
}

#[tonic::async_trait]
impl Engine for Arc<TbepGrpcServer> {
    async fn document_read(
        &self,
        request: Request<pb::DocumentReadRequest>,
    ) -> Result<Response<pb::DocumentReadResponse>, Status> {
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
        Ok(Response::new(pb::DocumentReadResponse {
            found: response.found,
            document: response.document.as_ref().map(document_body),
            position: response.position.unwrap_or_default(),
        }))
    }

    async fn document_write(
        &self,
        request: Request<pb::DocumentWriteRequest>,
    ) -> Result<Response<pb::DocumentWriteResponse>, Status> {
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
            usage: usage_admission(message.usage.as_ref(), &key)?,
        };
        self.require_mutation_usage(domain.usage.is_some())?;
        domain.validate().map_err(|error| conversion_error(&error.to_string()))?;
        let host = Arc::clone(&self.host);
        let tenant = identity.tenant;
        let response = run_host(self.as_ref(), MetricOperation::Write, move || {
            host.document_write(&tenant, &domain, &key)
        })
        .await?;
        Ok(Response::new(pb::DocumentWriteResponse {
            outcome: Some(write_outcome_pb(&response.outcome)),
        }))
    }

    async fn document_delete(
        &self,
        request: Request<pb::DocumentDeleteRequest>,
    ) -> Result<Response<pb::DocumentDeleteResponse>, Status> {
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
            usage: usage_admission(message.usage.as_ref(), &key)?,
        };
        self.require_mutation_usage(domain.usage.is_some())?;
        domain.validate().map_err(|error| conversion_error(&error.to_string()))?;
        let host = Arc::clone(&self.host);
        let tenant = identity.tenant;
        let response = run_host(self.as_ref(), MetricOperation::Write, move || {
            host.document_delete(&tenant, &domain, &key)
        })
        .await?;
        Ok(Response::new(pb::DocumentDeleteResponse {
            outcome: Some(write_outcome_pb(&response.outcome)),
        }))
    }

    async fn document_batch(
        &self,
        request: Request<pb::DocumentBatchRequest>,
    ) -> Result<Response<pb::DocumentBatchResponse>, Status> {
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
            usage: usage_admission(message.usage.as_ref(), &key)?,
        };
        self.require_mutation_usage(domain.usage.is_some())?;
        domain.validate().map_err(|error| conversion_error(&error.to_string()))?;
        let host = Arc::clone(&self.host);
        let tenant = identity.tenant;
        let response = run_host(self.as_ref(), MetricOperation::Write, move || {
            host.document_batch(&tenant, &domain, &key)
        })
        .await?;
        Ok(Response::new(pb::DocumentBatchResponse {
            outcome: Some(write_outcome_pb(&response.outcome)),
        }))
    }

    async fn query_execute(
        &self,
        request: Request<pb::QueryExecuteRequest>,
    ) -> Result<Response<pb::QueryExecuteResponse>, Status> {
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
        Ok(Response::new(pb::QueryExecuteResponse {
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
        request: Request<pb::QueryCancelRequest>,
    ) -> Result<Response<pb::QueryCancelResponse>, Status> {
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
        Ok(Response::new(pb::QueryCancelResponse {
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
        request: Request<pb::FamilyReadRequest>,
    ) -> Result<Response<pb::FamilyReadResponse>, Status> {
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
        Ok(Response::new(pb::FamilyReadResponse {
            response_codec: response.response_codec,
            response: response.response,
        }))
    }

    async fn family_write(
        &self,
        request: Request<pb::FamilyWriteRequest>,
    ) -> Result<Response<pb::FamilyWriteResponse>, Status> {
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
            usage: usage_admission(message.usage.as_ref(), &key)?,
        };
        self.require_mutation_usage(domain.usage.is_some())?;
        domain.validate().map_err(|error| conversion_error(&error.to_string()))?;
        let host = Arc::clone(&self.host);
        let tenant = identity.tenant;
        let response = run_host(self.as_ref(), MetricOperation::Write, move || {
            host.family_write(&tenant, &domain, &key)
        })
        .await?;
        Ok(Response::new(pb::FamilyWriteResponse {
            response_codec: response.response_codec,
            response: response.response,
        }))
    }
}

#[tonic::async_trait]
impl Backbone for Arc<TbepGrpcServer> {
    type SubscribeStream = SubscriptionStream;

    async fn subscribe(
        &self,
        request: Request<pb::BackboneSubscribeRequest>,
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
        let subscribed = pb::BackboneStreamEvent {
            event: Some(pb::backbone_stream_event::Event::Subscribed(
                pb::BackboneSubscribeResponse {
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
        std::thread::Builder::new()
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
        }))
    }

    async fn acknowledge(
        &self,
        request: Request<pb::BackboneAcknowledgeRequest>,
    ) -> Result<Response<pb::BackboneAcknowledgeResponse>, Status> {
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
        Ok(Response::new(pb::BackboneAcknowledgeResponse {
            subscription_id: response.subscription_id,
            acknowledged_through: response.acknowledged_through,
        }))
    }

    async fn unsubscribe(
        &self,
        request: Request<pb::BackboneUnsubscribeRequest>,
    ) -> Result<Response<pb::BackboneUnsubscribeResponse>, Status> {
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
        Ok(Response::new(pb::BackboneUnsubscribeResponse {
            subscription_id: response.subscription_id,
        }))
    }
}

#[tonic::async_trait]
impl Sync for Arc<TbepGrpcServer> {
    async fn session_setup(
        &self,
        request: Request<pb::SyncSessionSetupRequest>,
    ) -> Result<Response<pb::SyncSessionSetupResponse>, Status> {
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
        Ok(Response::new(pb::SyncSessionSetupResponse {
            session_id: response.session_id,
            checkpoint: response.checkpoint,
            retained_floor: response.retained_floor,
        }))
    }

    async fn delta_pull(
        &self,
        request: Request<pb::SyncDeltaPullRequest>,
    ) -> Result<Response<pb::SyncDeltaPullResponse>, Status> {
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
        Ok(Response::new(pb::SyncDeltaPullResponse {
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
        request: Request<pb::SyncSnapshotPullRequest>,
    ) -> Result<Response<pb::SyncSnapshotPullResponse>, Status> {
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
        Ok(Response::new(pb::SyncSnapshotPullResponse {
            session_id: response.session_id,
            payload_codec: response.payload_codec,
            payload: response.payload,
            checkpoint: response.checkpoint,
        }))
    }

    async fn checkpoint_ack(
        &self,
        request: Request<pb::SyncCheckpointAckRequest>,
    ) -> Result<Response<pb::SyncCheckpointAckResponse>, Status> {
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
        Ok(Response::new(pb::SyncCheckpointAckResponse { session_id: response.session_id }))
    }

    async fn mutation_submit(
        &self,
        request: Request<pb::SyncMutationSubmitRequest>,
    ) -> Result<Response<pb::SyncMutationSubmitResponse>, Status> {
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
            usage: usage_admission(message.usage.as_ref(), &key)?,
        };
        self.require_mutation_usage(domain.usage.is_some())?;
        domain.validate().map_err(|error| conversion_error(&error.to_string()))?;
        let host = Arc::clone(&self.host);
        let tenant = identity.tenant;
        let response = run_host(self.as_ref(), MetricOperation::Write, move || {
            host.sync_mutation_submit(&tenant, &domain, &key)
        })
        .await?;
        Ok(Response::new(pb::SyncMutationSubmitResponse {
            session_id: response.session_id,
            outcome: Some(mutation_outcome_pb(&response.outcome)),
        }))
    }

    async fn session_close(
        &self,
        request: Request<pb::SyncSessionCloseRequest>,
    ) -> Result<Response<pb::SyncSessionCloseResponse>, Status> {
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
        Ok(Response::new(pb::SyncSessionCloseResponse { session_id: response.session_id }))
    }
}

#[tonic::async_trait]
impl Schema for Arc<TbepGrpcServer> {
    async fn declare(
        &self,
        request: Request<pb::SchemaDeclareRequest>,
    ) -> Result<Response<pb::SchemaDeclareResponse>, Status> {
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
        Ok(Response::new(pb::SchemaDeclareResponse {
            enforced_version: response.enforced_version.unwrap_or_default(),
            rule_count: response.rule_count,
        }))
    }

    async fn status(
        &self,
        request: Request<pb::SchemaStatusRequest>,
    ) -> Result<Response<pb::SchemaStatusResponse>, Status> {
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
        Ok(Response::new(pb::SchemaStatusResponse {
            enforced_version: response.enforced_version.unwrap_or_default(),
            rule_count: response.rule_count,
        }))
    }

    async fn validate(
        &self,
        request: Request<pb::SchemaValidateRequest>,
    ) -> Result<Response<pb::SchemaValidateResponse>, Status> {
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
        Ok(Response::new(pb::SchemaValidateResponse {
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
impl Replication for Arc<TbepGrpcServer> {
    async fn conflicts(
        &self,
        request: Request<pb::ReplicationConflictsRequest>,
    ) -> Result<Response<pb::ReplicationConflictsResponse>, Status> {
        self.counted();
        let identity = self.identity(&request)?;
        let host = Arc::clone(&self.host);
        let tenant = identity.tenant;
        let response = run_host(self.as_ref(), MetricOperation::Repair, move || {
            host.replication_conflicts(&tenant)
        })
        .await?;
        Ok(Response::new(pb::ReplicationConflictsResponse {
            member: response.member,
            records: response.records.iter().map(conflict_record).collect(),
        }))
    }

    async fn fenced(
        &self,
        request: Request<pb::ReplicationFencedRequest>,
    ) -> Result<Response<pb::ReplicationFencedResponse>, Status> {
        self.counted();
        let identity = self.identity(&request)?;
        let host = Arc::clone(&self.host);
        let tenant = identity.tenant;
        let response = run_host(self.as_ref(), MetricOperation::Repair, move || {
            host.replication_fenced(&tenant)
        })
        .await?;
        Ok(Response::new(pb::ReplicationFencedResponse {
            member: response.member,
            records: response.records.iter().map(fenced_record).collect(),
        }))
    }

    async fn depositions(
        &self,
        request: Request<pb::ReplicationDepositionsRequest>,
    ) -> Result<Response<pb::ReplicationDepositionsResponse>, Status> {
        self.counted();
        let identity = self.identity(&request)?;
        let host = Arc::clone(&self.host);
        let tenant = identity.tenant;
        let response = run_host(self.as_ref(), MetricOperation::Repair, move || {
            host.replication_depositions(&tenant)
        })
        .await?;
        Ok(Response::new(pb::ReplicationDepositionsResponse {
            member: response.member,
            records: response.records.iter().map(deposition_record).collect(),
        }))
    }

    async fn cursor(
        &self,
        request: Request<pb::ReplicationCursorRequest>,
    ) -> Result<Response<pb::ReplicationCursorResponse>, Status> {
        self.counted();
        let identity = self.identity(&request)?;
        let host = Arc::clone(&self.host);
        let tenant = identity.tenant;
        let response = run_host(self.as_ref(), MetricOperation::Repair, move || {
            host.replication_cursor(&tenant)
        })
        .await?;
        Ok(Response::new(pb::ReplicationCursorResponse {
            member: response.member,
            position: response.position.unwrap_or_default(),
        }))
    }

    async fn health(
        &self,
        request: Request<pb::ReplicationHealthRequest>,
    ) -> Result<Response<pb::ReplicationHealthResponse>, Status> {
        self.counted();
        let identity = self.identity(&request)?;
        let host = Arc::clone(&self.host);
        let tenant = identity.tenant;
        let response = run_host(self.as_ref(), MetricOperation::Repair, move || {
            host.replication_health(&tenant)
        })
        .await?;
        Ok(Response::new(pb::ReplicationHealthResponse {
            member: response.member,
            status: replication_status_pb(response.status),
        }))
    }

    async fn denials(
        &self,
        request: Request<pb::ReplicationDenialsRequest>,
    ) -> Result<Response<pb::ReplicationDenialsResponse>, Status> {
        self.counted();
        let identity = self.identity(&request)?;
        let host = Arc::clone(&self.host);
        let tenant = identity.tenant;
        let response = run_host(self.as_ref(), MetricOperation::Repair, move || {
            host.replication_denials(&tenant)
        })
        .await?;
        Ok(Response::new(pb::ReplicationDenialsResponse {
            member: response.member,
            records: response.records.iter().map(denial_record).collect(),
        }))
    }
}

#[tonic::async_trait]
impl Maintenance for Arc<TbepGrpcServer> {
    async fn topology_read(
        &self,
        request: Request<pb::TopologyReadRequest>,
    ) -> Result<Response<pb::TopologyReadResponse>, Status> {
        self.counted();
        let identity = self.identity(&request)?;
        let host = Arc::clone(&self.host);
        let tenant = identity.tenant;
        let response =
            run_host(self.as_ref(), MetricOperation::Other, move || host.topology_read(&tenant))
                .await?;
        Ok(Response::new(pb::TopologyReadResponse {
            binding: self.binding as i32,
            subsystems: response.subsystems,
            engine_topology_available: response.engine_topology_available,
            cluster_mode: cluster_mode_pb(response.cluster_mode),
        }))
    }

    async fn admin_status(
        &self,
        request: Request<pb::AdminStatusRequest>,
    ) -> Result<Response<pb::AdminStatusResponse>, Status> {
        self.counted();
        let identity = self.identity(&request)?;
        let _ = identity;
        let host = Arc::clone(&self.host);
        let served = self.frames_served();
        let response =
            run_host(self.as_ref(), MetricOperation::Other, move || host.admin_status()).await?;
        Ok(Response::new(pb::AdminStatusResponse {
            registry_version: response.registry_version,
            frames_served: response.frames_served.max(served),
            sessions: response.sessions,
            subscriptions: response.subscriptions,
            scopes: response.scopes,
        }))
    }

    async fn supervision_health(
        &self,
        request: Request<pb::SupervisionHealthRequest>,
    ) -> Result<Response<pb::SupervisionHealthResponse>, Status> {
        self.counted();
        let identity = self.identity(&request)?;
        let _ = identity;
        let host = Arc::clone(&self.host);
        let served = self.frames_served();
        let response =
            run_host(self.as_ref(), MetricOperation::Other, move || host.health()).await?;
        Ok(Response::new(pb::SupervisionHealthResponse {
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
        request: Request<pb::SupervisionShutdownRequest>,
    ) -> Result<Response<pb::SupervisionShutdownResponse>, Status> {
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
        Ok(Response::new(pb::SupervisionShutdownResponse {
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
impl Usage for Arc<TbepGrpcServer> {
    async fn record(
        &self,
        request: Request<pb::UsageRecordRequest>,
    ) -> Result<Response<pb::UsageRecordResponse>, Status> {
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
        Ok(Response::new(pb::UsageRecordResponse {
            sequence: response.sequence,
            replica_epoch: response.replica_epoch,
            already_recorded: response.already_recorded,
        }))
    }

    async fn export(
        &self,
        request: Request<pb::UsageExportRequest>,
    ) -> Result<Response<pb::UsageExportResponse>, Status> {
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
        let expected_tenant = tenant.clone();
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
        Ok(Response::new(pb::UsageExportResponse {
            facts: response.facts.iter().map(usage_fact_pb).collect(),
            continuation: response.continuation,
            snapshot_sequence: response.snapshot_sequence,
        }))
    }
}

#[tonic::async_trait]
impl Tier for Arc<TbepGrpcServer> {
    async fn configure(
        &self,
        request: Request<pb::TierConfigureRequest>,
    ) -> Result<Response<pb::TierConfigureResponse>, Status> {
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
        Ok(Response::new(pb::TierConfigureResponse {
            policy_id: response.policy_id,
            version: response.version,
            paused: response.paused,
        }))
    }

    async fn inspect(
        &self,
        request: Request<pb::TierInspectRequest>,
    ) -> Result<Response<pb::TierInspectResponse>, Status> {
        self.counted();
        let identity = self.identity(&request)?;
        let host = Arc::clone(&self.host);
        let tenant = identity.tenant;
        let response =
            run_host(self.as_ref(), MetricOperation::Read, move || host.tier_inspect(&tenant))
                .await?;
        Ok(Response::new(pb::TierInspectResponse {
            configured: response.configured,
            paused: response.paused,
            manifest_epoch: response.manifest_epoch,
            object_count: response.object_count,
            nvme_bytes: response.nvme_bytes,
        }))
    }

    async fn dry_run(
        &self,
        request: Request<pb::TierDryRunRequest>,
    ) -> Result<Response<pb::TierDryRunResponse>, Status> {
        self.counted();
        let identity = self.identity(&request)?;
        let host = Arc::clone(&self.host);
        let tenant = identity.tenant;
        let response =
            run_host(self.as_ref(), MetricOperation::Other, move || host.tier_dry_run(&tenant))
                .await?;
        Ok(Response::new(pb::TierDryRunResponse {
            receipt: Some(tier_move_receipt(&response.receipt)),
        }))
    }

    async fn explain(
        &self,
        request: Request<pb::TierExplainRequest>,
    ) -> Result<Response<pb::TierExplainResponse>, Status> {
        self.counted();
        let identity = self.identity(&request)?;
        let host = Arc::clone(&self.host);
        let tenant = identity.tenant;
        let response =
            run_host(self.as_ref(), MetricOperation::Read, move || host.tier_explain(&tenant))
                .await?;
        Ok(Response::new(pb::TierExplainResponse {
            receipt: Some(tier_move_receipt(&response.receipt)),
        }))
    }

    async fn run(
        &self,
        request: Request<pb::TierRunRequest>,
    ) -> Result<Response<pb::TierRunResponse>, Status> {
        self.counted();
        let identity = self.identity(&request)?;
        let key = idempotency_key(&request)?;
        let host = Arc::clone(&self.host);
        let tenant = identity.tenant;
        let response = run_host(self.as_ref(), MetricOperation::Compaction, move || {
            host.tier_run(&tenant, &key)
        })
        .await?;
        Ok(Response::new(pb::TierRunResponse {
            receipt: Some(tier_move_receipt(&response.receipt)),
        }))
    }

    async fn pause(
        &self,
        request: Request<pb::TierPauseRequest>,
    ) -> Result<Response<pb::TierPauseResponse>, Status> {
        self.counted();
        let identity = self.identity(&request)?;
        let key = idempotency_key(&request)?;
        let host = Arc::clone(&self.host);
        let tenant = identity.tenant;
        let response =
            run_host(self.as_ref(), MetricOperation::Write, move || host.tier_pause(&tenant, &key))
                .await?;
        Ok(Response::new(pb::TierPauseResponse { paused: response.paused }))
    }

    async fn resume(
        &self,
        request: Request<pb::TierResumeRequest>,
    ) -> Result<Response<pb::TierResumeResponse>, Status> {
        self.counted();
        let identity = self.identity(&request)?;
        let key = idempotency_key(&request)?;
        let host = Arc::clone(&self.host);
        let tenant = identity.tenant;
        let response = run_host(self.as_ref(), MetricOperation::Write, move || {
            host.tier_resume(&tenant, &key)
        })
        .await?;
        Ok(Response::new(pb::TierResumeResponse { paused: response.paused }))
    }
}
