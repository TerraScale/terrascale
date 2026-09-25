//! Bounded mTLS gRPC receiver for the signed-authority replication profile.

use std::collections::HashMap;
use std::convert::Infallible;
#[cfg(feature = "replication-test-hooks")]
use std::fs::{self, OpenOptions};
use std::future::Future;
#[cfg(feature = "replication-test-hooks")]
use std::io::Write as _;
use std::io::{self, Read};
#[cfg(feature = "replication-test-hooks")]
use std::path::{Path, PathBuf};
use std::pin::Pin;
use std::sync::OnceLock;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex, MutexGuard};
use std::task::{Context, Poll};
use std::time::Duration;
#[cfg(feature = "replication-test-hooks")]
use std::time::Instant;

use futures_util::{Stream, stream};
use rustls::pki_types::{CertificateDer, pem::PemObject};
use sha2::{Digest, Sha256};
use tb_core::{
    AdmissionError, Attribution, DatabaseId, MembershipEpoch, MemoryBudget, MemoryCategory,
    OriginId, ReplicaId, ShardId, TenantId, WorkReservation, WorkerId,
};
use tb_protocol::{
    AdmissionController, AdmissionLimits, AdmittedSyncFrame, NegotiationPolicy, StreamPermit,
    SyncAdmissionContext, SyncCodec, V1_EVENT_FORMAT, V1_PROTOCOL_MAJOR, V1_PROTOCOL_MINOR,
    decode_transport_group_pre_admitted, negotiate_hello, server_mtls_config, v1,
    v1_transfer_limits,
};
use tb_replication::{
    AcceptedAuthority, AuthorityError, AuthorityVersion, CertifiedShardView, ConfigurationId,
    CursorLimits, CursorScope, DurablePrefix, EventDigest, EventId, LocalAuthorityScope,
    MAX_CURSOR_PENDING, MAX_CURSOR_SEQUENCE_DISTANCE, MAX_EVENT_CHANGES, MAX_WAL_GROUP_BYTES,
    PeerHelloScope, ReplicaReceiver, ReplicationLimits, verify_incoming,
};
use tb_storage::StorageConfig;
use tokio::io::{AsyncRead, AsyncWrite, ReadBuf};
use tokio::net::{TcpListener, TcpStream};
use tokio::sync::{Semaphore, mpsc, oneshot, watch};
use tonic::body::Body;
use tonic::codegen::http::{Request as HttpRequest, Response as HttpResponse};
use tonic::transport::{Server, ServerTlsConfig};
use tonic::{Request, Response, Status, Streaming};
use tower::Service;

use crate::distributed_runtime::{DocumentMemberRuntime, REPLAY_APPLIES_PER_SLICE};
use crate::replication_sender::ReplicationSender;
use crate::replication_sender_tls::ReplicationOutboundTls;
use crate::{
    CgroupMemoryMonitor, LocalServerState, MemberControlIdentity, ReplicationConfig,
    ReplicationConfigError,
};
use tb_document::{DocumentApplyOutcome, DocumentError};

const MAX_CONNECTIONS: usize = 8;
const MAX_GLOBAL_STREAMS: usize = 8;
const MAX_STREAMS_PER_PEER: usize = 2;
const MAX_QUEUED_BATCHES_PER_PEER: usize = 8;
const MAX_PEER_BYTES: usize = 64 * 1024 * 1024;
const CONNECTION_RESERVATION_BYTES: usize = 2 * 1024 * 1024;
const TLS_MATERIAL_RESERVATION_BYTES: usize = 16 * 1024 * 1024;
const SUPPORTED_RECORD_FORMATS: [u32; 2] = [1, 2];
const V1_MERGE_POLICY: u32 = 1;
const AUTHORITY_CERTIFICATE_MAX_BYTES: usize = 4 * 1024 * 1024 + 128;
const PEM_FILE_MAX_BYTES: usize = 2 * 1024 * 1024;
const SYNC_PATH: &str = "/terrabase.v1.Replication/Sync";
const CURSOR_LIMITS: CursorLimits = CursorLimits {
    max_pending: MAX_CURSOR_PENDING,
    max_sequence_distance: MAX_CURSOR_SEQUENCE_DISTANCE,
};
const EVENT_LIMITS: ReplicationLimits = ReplicationLimits {
    max_origins: 64,
    max_changes: MAX_EVENT_CHANGES,
    max_siblings: 64,
    max_value_bytes: tb_core::MAX_VALUE_BYTES,
    max_payload_bytes: MAX_WAL_GROUP_BYTES,
};
const CONNECTIONS_ACTIVE_METRIC: &str = "terrabase_replication_connections_active";
const STREAMS_ACTIVE_METRIC: &str = "terrabase_replication_streams_active";
const APPEND_WORKERS_ACTIVE_METRIC: &str = "terrabase_replication_append_workers_active";
const ACCEPT_ERROR_RETRY_DELAY: Duration = Duration::from_millis(10);
const TLS_HANDSHAKE_TIMEOUT: Duration = Duration::from_secs(10);
const DOCUMENT_REPLAY_RETRY_DELAY: Duration = Duration::from_millis(50);
const DOCUMENT_REPLAY_MAX_RETRY_TICKS: usize = 20;
#[cfg(feature = "replication-test-hooks")]
const APPEND_BARRIER_TIMEOUT: Duration = Duration::from_secs(30);

/// Opened signed authority, mTLS identity, and bounded Sync runtime.
pub struct ReplicationServer {
    runtime: Arc<ReplicationRuntime>,
    tls: ServerTlsConfig,
    member_control_identity: MemberControlIdentity,
    outbound_tls: Option<ReplicationOutboundTls>,
}

struct ReplicationRuntime {
    authority: Arc<AcceptedAuthority>,
    document: Option<Arc<DocumentMemberRuntime>>,
    budget: MemoryBudget,
    profile: crate::ReplicationMemoryProfile,
    monitor: CgroupMemoryMonitor,
    local_scope: LocalAuthorityScope,
    local_identity: v1::PeerIdentity,
    attribution: Attribution,
    origin_inboxes: Mutex<HashMap<OriginId, Arc<OriginInbox>>>,
    peer_admissions: Mutex<HashMap<OriginId, AdmissionController>>,
    connections: Arc<Semaphore>,
    streams: Arc<Semaphore>,
    failure_state: OnceLock<Arc<LocalServerState>>,
    replay_running: AtomicBool,
    replay_pending: AtomicBool,
    replay_shutdown: AtomicBool,
    replay_failed: AtomicBool,
    _tls_material: WorkReservation,
    #[cfg(feature = "replication-test-hooks")]
    append_barrier: Option<Arc<AppendBarrier>>,
    #[cfg(feature = "replication-test-hooks")]
    post_sync_barrier: Option<Arc<AppendBarrier>>,
}

struct OriginInbox {
    serial: Arc<Semaphore>,
    receiver: Mutex<Option<ReplicaReceiver>>,
}

struct StreamLease {
    _global_slot: tokio::sync::OwnedSemaphorePermit,
    _work: WorkReservation,
    _active_gauge: ActiveGauge,
}

/// A detached replay worker must release its scheduler slot and fail closed
/// even if a storage operation unwinds before its normal exit path.
struct ReplayWorkerGuard {
    runtime: Arc<ReplicationRuntime>,
    completed: bool,
}

impl Drop for ReplayWorkerGuard {
    fn drop(&mut self) {
        if !self.completed {
            self.runtime.replay_failed.store(true, Ordering::Release);
            if let Some(state) = self.runtime.failure_state.get() {
                state.clear_document_readiness();
            }
        }
        self.runtime.replay_running.store(false, Ordering::Release);
    }
}

#[derive(Default)]
struct PeerStreamLease(Mutex<Option<StreamPermit>>);

#[derive(Clone)]
struct SyncRoute {
    runtime: Arc<ReplicationRuntime>,
    shutdown: watch::Receiver<bool>,
}

struct SyncHandler {
    runtime: Arc<ReplicationRuntime>,
    context: Arc<SyncAdmissionContext>,
    stream_lease: Arc<StreamLease>,
    peer_stream_lease: Arc<PeerStreamLease>,
    shutdown: watch::Receiver<bool>,
}

struct SyncTaskContext {
    runtime: Arc<ReplicationRuntime>,
    context: Arc<SyncAdmissionContext>,
    stream_lease: Arc<StreamLease>,
    peer_stream_lease: Arc<PeerStreamLease>,
}

struct AuthenticatedHello {
    request: v1::Hello,
    permit: WorkReservation,
    leaf: Vec<u8>,
    peer_scope: PeerHelloScope,
    origin: OriginId,
    admission: AdmissionController,
    inbox: Arc<OriginInbox>,
}

struct PreparedSync {
    origin: OriginId,
    peer_scope: PeerHelloScope,
    leaf: Vec<u8>,
    inbox: Arc<OriginInbox>,
    negotiated_limits: v1::TransferLimits,
    negotiated_codecs: Vec<v1::PayloadCodec>,
    record_format_version: u32,
    applied_updates: Option<watch::Receiver<u64>>,
    disclosed_durable: u64,
    disclosed_applied: u64,
}

#[derive(Clone, Copy)]
struct OriginFrontier {
    durable: DurablePrefix,
    applied: Option<u64>,
}

struct DisclosedFrontier {
    durable: u64,
    applied: u64,
}

#[allow(
    clippy::large_enum_variant,
    reason = "keep admitted gRPC frames inline without an allocation for every frame"
)]
enum SyncInput {
    Frame(Result<Option<AdmittedSyncFrame>, Status>),
    AppliedChanged(Result<(), watch::error::RecvError>),
    Cancelled,
}

struct GuardedResponseStream {
    receiver: mpsc::Receiver<Result<v1::SyncResponse, Status>>,
    disconnect: Option<oneshot::Sender<()>>,
    _stream_lease: Arc<StreamLease>,
    _peer_stream_lease: Arc<PeerStreamLease>,
}

struct AdmittedTcpStream {
    stream: TcpStream,
    _connection_slot: tokio::sync::OwnedSemaphorePermit,
    _work: WorkReservation,
    _active_gauge: ActiveGauge,
}

#[derive(Clone, Copy)]
struct StagedEvent {
    id: EventId,
    durable: DurablePrefix,
    credit_bytes: usize,
}

#[derive(Clone, Copy)]
struct AppendContext<'a> {
    runtime: &'a ReplicationRuntime,
    inbox: &'a OriginInbox,
    origin: OriginId,
    peer_scope: PeerHelloScope,
    peer_leaf: &'a [u8],
    codecs: &'a [v1::PayloadCodec],
    negotiated_limits: &'a v1::TransferLimits,
    record_format_version: u32,
    credit_bytes: usize,
}

struct ActiveGauge(&'static str);

impl ActiveGauge {
    fn new(metric_name: &'static str) -> Self {
        metrics::gauge!(metric_name).increment(1.0);
        Self(metric_name)
    }
}

impl Drop for ActiveGauge {
    fn drop(&mut self) {
        metrics::gauge!(self.0).decrement(1.0);
    }
}

#[cfg(feature = "replication-test-hooks")]
struct AppendBarrier {
    directory: PathBuf,
    result: OnceLock<Result<(), String>>,
}

#[cfg(feature = "replication-test-hooks")]
impl AppendBarrier {
    fn open(directory: &Path) -> Result<Self, io::Error> {
        fs::create_dir_all(directory)?;
        if !fs::metadata(directory)?.is_dir() {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "append barrier path is not a directory",
            ));
        }
        for name in ["entered", "release", "cancelled"] {
            match fs::symlink_metadata(directory.join(name)) {
                Ok(_) => {
                    return Err(io::Error::new(
                        io::ErrorKind::AlreadyExists,
                        format!("append barrier marker {name} already exists"),
                    ));
                }
                Err(error) if error.kind() == io::ErrorKind::NotFound => {}
                Err(error) => return Err(error),
            }
        }
        Ok(Self {
            directory: directory.to_owned(),
            result: OnceLock::new(),
        })
    }

    fn wait(&self) -> Result<(), String> {
        self.result
            .get_or_init(|| self.wait_once().map_err(|error| error.to_string()))
            .clone()
    }

    fn record_cancellation(&self) -> Result<(), io::Error> {
        let cancelled_path = self.directory.join("cancelled");
        let mut cancelled = OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(cancelled_path)?;
        cancelled.write_all(b"cancelled\n")?;
        cancelled.sync_all()
    }

    fn wait_once(&self) -> Result<(), io::Error> {
        let entered_path = self.directory.join("entered");
        let mut entered = OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(entered_path)?;
        entered.write_all(b"entered\n")?;
        entered.sync_all()?;
        drop(entered);

        let release_path = self.directory.join("release");
        let deadline = Instant::now() + APPEND_BARRIER_TIMEOUT;
        loop {
            match fs::symlink_metadata(&release_path) {
                Ok(metadata) if metadata.file_type().is_file() => return Ok(()),
                Ok(_) => {
                    return Err(io::Error::new(
                        io::ErrorKind::InvalidInput,
                        "append barrier release marker is not a regular file",
                    ));
                }
                Err(error) if error.kind() == io::ErrorKind::NotFound => {}
                Err(error) => return Err(error),
            }
            if Instant::now() >= deadline {
                return Err(io::Error::new(
                    io::ErrorKind::TimedOut,
                    "timed out waiting for append barrier release marker",
                ));
            }
            std::thread::sleep(Duration::from_millis(10));
        }
    }
}

/// Fail-closed startup or transport error for networked replication mode.
#[derive(Debug, thiserror::Error)]
pub enum ReplicationServerError {
    /// Replication memory profile or required path was invalid.
    #[error(transparent)]
    Config(#[from] ReplicationConfigError),
    /// The process budget could not reserve bounded server state.
    #[error("replication process memory admission failed: {0}")]
    Admission(#[from] AdmissionError),
    /// Signed shard authority could not be recovered or installed.
    #[error(transparent)]
    Authority(#[from] AuthorityError),
    /// Signed-v2 document or owned inbox recovery failed before readiness.
    #[error(transparent)]
    Document(#[from] DocumentError),
    /// TLS files could not be read or did not match the authority.
    #[error("invalid replication TLS material: {0}")]
    Tls(String),
    /// A bounded authority or TLS file could not be read.
    #[error(transparent)]
    Io(#[from] io::Error),
    /// The Tonic transport server failed.
    #[error(transparent)]
    Transport(#[from] tonic::transport::Error),
    /// A blocking journal worker failed to join.
    #[error("replication journal worker failed: {0}")]
    Worker(String),
    /// The test-only append barrier could not be prepared.
    #[cfg(feature = "replication-test-hooks")]
    #[error("replication test append barrier setup failed: {0}")]
    AppendBarrier(#[source] io::Error),
    /// The configured global stream bound exceeds Tonic's HTTP/2 limit type.
    #[error("global replication stream limit exceeds Tonic's supported range")]
    StreamLimit,
}

impl ReplicationServer {
    /// Validate local mTLS membership and recover accepted authority before local storage opens.
    ///
    /// # Errors
    /// Returns an error when memory admission, authority recovery, local
    /// certificate membership, or Tonic TLS setup fails.
    ///
    #[allow(
        clippy::too_many_lines,
        reason = "server open binds authority, mTLS identity, document recovery, and admission limits before serving"
    )]
    pub fn open(
        config: &ReplicationConfig,
        budget: MemoryBudget,
        monitor: CgroupMemoryMonitor,
    ) -> Result<Self, ReplicationServerError> {
        let local_scope = config.local_scope;
        let attribution = Attribution {
            tenant: local_scope.tenant,
            worker: worker_id(local_scope.member),
            category: MemoryCategory::Replication,
        };
        let tls_material =
            budget.try_reserve_work(attribution, TLS_MATERIAL_RESERVATION_BYTES, 1)?;

        let authority_bytes = read_bounded(
            &config.authority_certificate,
            AUTHORITY_CERTIFICATE_MAX_BYTES,
        )?;
        let public_key_bytes = read_bounded(&config.trusted_public_key, 32)?;
        let trusted_public_key: [u8; 32] =
            public_key_bytes.as_slice().try_into().map_err(|_| {
                ReplicationServerError::Tls("trusted key must contain exactly 32 bytes".into())
            })?;
        let server_certificate = read_bounded(&config.server_certificate, PEM_FILE_MAX_BYTES)?;
        let server_private_key = read_bounded(&config.server_private_key, PEM_FILE_MAX_BYTES)?;
        let client_ca_roots = read_bounded(&config.client_ca_roots, PEM_FILE_MAX_BYTES)?;

        let authority = Arc::new(AcceptedAuthority::open_or_install(
            &config.data_dir,
            &authority_bytes,
            &trusted_public_key,
            local_scope,
            &budget,
            attribution,
        )?);
        let tls = server_mtls_config(&server_certificate, &server_private_key, &client_ca_roots)
            .map_err(|error| ReplicationServerError::Tls(error.to_string()))?
            .timeout(TLS_HANDSHAKE_TIMEOUT);
        // Force Tonic/rustls to parse the server identity and client roots
        // before LocalServerState marks readiness in the process entrypoint.
        drop(Server::builder().tls_config(tls.clone())?);
        let local_leaf = CertificateDer::pem_slice_iter(&server_certificate)
            .next()
            .transpose()
            .map_err(|error| ReplicationServerError::Tls(error.to_string()))?
            .ok_or_else(|| {
                ReplicationServerError::Tls("server certificate chain is empty".into())
            })?;
        let local_peer_scope = PeerHelloScope {
            cluster: local_scope.cluster,
            tenant: local_scope.tenant,
            database: local_scope.database,
            shard: local_scope.shard,
            origin: OriginId {
                replica_id: local_scope.member,
                incarnation: config.local_incarnation,
            },
            epoch: authority.view().epoch(),
            configuration: authority.view().configuration(),
        };
        authority
            .view()
            .certify_peer(local_peer_scope, local_leaf.as_ref())?;
        let outbound_tls = config
            .outbound_peer()
            .map(|peer| {
                let outbound_ca = read_bounded(peer.server_ca_roots(), PEM_FILE_MAX_BYTES)?;
                ReplicationOutboundTls::from_signed_view(
                    authority.view(),
                    peer,
                    &outbound_ca,
                    &server_certificate,
                    &server_private_key,
                )
                .map_err(|error| ReplicationServerError::Tls(error.to_string()))
            })
            .transpose()?;

        // Signed-v2 HTTPS startup also requires the configured outbound peer.
        let receiver_only = outbound_tls.is_none();
        let member_control_identity = MemberControlIdentity::from_verified(
            local_scope,
            config.local_incarnation,
            authority.view().epoch(),
            authority.view().configuration(),
            Sha256::digest(&authority_bytes).into(),
            Sha256::digest(local_leaf.as_ref()).into(),
            receiver_only,
        )?;

        let local_identity = make_peer_identity(
            local_scope.tenant,
            local_scope.database,
            local_scope.shard,
            local_scope.member,
            config.local_incarnation,
            authority.view(),
        );
        let document = if authority.view().version() == AuthorityVersion::V2 {
            Some(Arc::new(DocumentMemberRuntime::open(
                &authority,
                &budget,
                worker_id(local_scope.member),
                EVENT_LIMITS,
                CURSOR_LIMITS,
            )?))
        } else {
            None
        };
        #[cfg(feature = "replication-test-hooks")]
        if config.test_append_barrier_dir.is_some() && config.test_post_sync_barrier_dir.is_some() {
            return Err(ReplicationServerError::AppendBarrier(io::Error::new(
                io::ErrorKind::InvalidInput,
                "replication test barrier stages are mutually exclusive",
            )));
        }
        #[cfg(feature = "replication-test-hooks")]
        let append_barrier = config
            .test_append_barrier_dir
            .as_deref()
            .map(AppendBarrier::open)
            .transpose()
            .map_err(ReplicationServerError::AppendBarrier)?
            .map(Arc::new);
        #[cfg(feature = "replication-test-hooks")]
        let post_sync_barrier = config
            .test_post_sync_barrier_dir
            .as_deref()
            .map(AppendBarrier::open)
            .transpose()
            .map_err(ReplicationServerError::AppendBarrier)?
            .map(Arc::new);
        let runtime = Arc::new(ReplicationRuntime {
            authority,
            document,
            budget,
            profile: config.memory_profile,
            monitor,
            local_scope,
            local_identity,
            attribution,
            origin_inboxes: Mutex::new(HashMap::new()),
            peer_admissions: Mutex::new(HashMap::new()),
            connections: Arc::new(Semaphore::new(MAX_CONNECTIONS)),
            streams: Arc::new(Semaphore::new(MAX_GLOBAL_STREAMS)),
            failure_state: OnceLock::new(),
            replay_running: AtomicBool::new(false),
            replay_pending: AtomicBool::new(false),
            replay_shutdown: AtomicBool::new(false),
            replay_failed: AtomicBool::new(false),
            _tls_material: tls_material,
            #[cfg(feature = "replication-test-hooks")]
            append_barrier,
            #[cfg(feature = "replication-test-hooks")]
            post_sync_barrier,
        });
        Ok(Self {
            runtime,
            tls,
            member_control_identity,
            outbound_tls,
        })
    }

    /// Return the frozen verified local identity for the root-only status socket.
    #[must_use]
    pub fn member_control_identity(&self) -> MemberControlIdentity {
        self.member_control_identity.clone()
    }

    /// Whether signed-v2 document ownership was recovered for this process.
    #[must_use]
    pub fn document_member_mode(&self) -> bool {
        self.runtime.document.is_some()
    }

    /// The authority-bound outbound endpoint, if this process selected the
    /// fixed two-member sender profile.
    #[must_use]
    pub fn outbound_tls(&self) -> Option<&ReplicationOutboundTls> {
        self.outbound_tls.as_ref()
    }

    /// Share the one recovered signed-v2 document owner with the HTTPS worker.
    pub(crate) fn document_runtime(&self) -> Option<Arc<DocumentMemberRuntime>> {
        self.runtime.document.as_ref().map(Arc::clone)
    }

    /// Serve only the bounded `/terrabase.v1.Replication/Sync` route over mTLS.
    ///
    /// # Errors
    /// Returns an error when TLS setup, listener acceptance, the incoming
    /// transport, or a detached bounded journal worker fails during drain.
    #[allow(
        clippy::too_many_lines,
        reason = "serve coordinates sender, receiver, shutdown, and journal drain lifecycles"
    )]
    pub async fn serve(
        self,
        listener: TcpListener,
        mut shutdown: watch::Receiver<bool>,
        ready: oneshot::Sender<()>,
        failure_state: Arc<LocalServerState>,
    ) -> Result<(), ReplicationServerError> {
        self.runtime
            .failure_state
            .set(Arc::clone(&failure_state))
            .map_err(|_| {
                ReplicationServerError::Worker("replication state installed twice".into())
            })?;
        let sender = self
            .outbound_tls
            .map(|endpoint| {
                let document = self.runtime.document.as_ref().ok_or_else(|| {
                    ReplicationServerError::Worker("outbound sender has no document owner".into())
                })?;
                ReplicationSender::new(
                    endpoint,
                    Arc::clone(document),
                    self.runtime.local_identity.clone(),
                    self.runtime.budget.clone(),
                    self.runtime.monitor.clone(),
                    self.runtime.profile,
                    self.runtime.attribution,
                )
                .map_err(|error| ReplicationServerError::Worker(error.to_string()))
            })
            .transpose()
            .inspect_err(|_| failure_state.mark_failed())?;
        let max_global_streams =
            u32::try_from(MAX_GLOBAL_STREAMS).map_err(|_| ReplicationServerError::StreamLimit)?;
        let (accept_failure_sender, mut accept_failure_receiver) = mpsc::unbounded_channel();
        let accept_failure = Arc::new(Mutex::new(None));
        let incoming = admitted_connections(
            listener,
            Arc::clone(&self.runtime),
            Arc::clone(&failure_state),
            Arc::clone(&accept_failure),
            accept_failure_sender,
        );
        let (server_shutdown_sender, server_shutdown_receiver) = watch::channel(false);
        let mut server_shutdown = server_shutdown_receiver.clone();
        let mut sender_future =
            sender.map(|sender| Box::pin(sender.run(server_shutdown_receiver.clone())));
        let service = SyncRoute {
            runtime: Arc::clone(&self.runtime),
            shutdown: server_shutdown_receiver,
        };
        let server = Server::builder()
            .tls_config(self.tls)?
            .initial_connection_window_size(Some(65_535))
            .initial_stream_window_size(Some(65_535))
            .http2_adaptive_window(Some(false))
            .http2_max_header_list_size(Some(16 * 1024))
            .max_frame_size(Some(16_384))
            .max_concurrent_streams(Some(max_global_streams));
        let replay_runtime = Arc::clone(&self.runtime);
        let shutdown_sender = server_shutdown_sender.clone();
        let shutdown_signal = async move {
            tokio::select! {
                () = wait_for_shutdown(&mut shutdown) => {}
                Some(()) = accept_failure_receiver.recv() => {}
            }
            replay_runtime
                .replay_shutdown
                .store(true, Ordering::Release);
            let _ = shutdown_sender.send(true);
        };
        tokio::pin!(shutdown_signal);
        let serving = server.serve_with_incoming_shutdown(service, incoming, async move {
            wait_for_shutdown(&mut server_shutdown).await;
        });
        tokio::pin!(serving);
        let mut sender_finished = false;
        let mut sender_error = None;
        let result = tokio::select! {
            biased;
            () = &mut shutdown_signal => serving.await,
            result = &mut serving => result,
            result = async {
                match sender_future.as_mut() {
                    Some(future) => future.await,
                    None => std::future::pending().await,
                }
            } => {
                sender_finished = true;
                failure_state.mark_failed();
                sender_error = Some(match result {
                    Err(error) => error.to_string(),
                    Ok(()) => "outbound sender stopped before shutdown".into(),
                });
                self.runtime.replay_shutdown.store(true, Ordering::Release);
                let _ = server_shutdown_sender.send(true);
                serving.await
            }
            () = tokio::task::yield_now() => {
                let _ = ready.send(());
                tokio::select! {
                    biased;
                    () = &mut shutdown_signal => serving.await,
                    result = &mut serving => result,
                    result = async {
                        match sender_future.as_mut() {
                            Some(future) => future.await,
                            None => std::future::pending().await,
                        }
                    } => {
                        sender_finished = true;
                        failure_state.mark_failed();
                        sender_error = Some(match result {
                            Err(error) => error.to_string(),
                            Ok(()) => "outbound sender stopped before shutdown".into(),
                        });
                        self.runtime.replay_shutdown.store(true, Ordering::Release);
                        let _ = server_shutdown_sender.send(true);
                        serving.await
                    }
                }
            }
        };

        let _ = server_shutdown_sender.send(true);
        self.runtime.replay_shutdown.store(true, Ordering::Release);
        if !sender_finished
            && let Some(sender_future) = sender_future
            && let Err(error) = sender_future.await
        {
            failure_state.mark_failed();
            sender_error = Some(error.to_string());
        }
        // Each blocking open/append closure holds its stream lease until the
        // journal operation actually returns. Acquiring every global slot
        // after Tonic has closed the request streams drains those detached
        // workers before this server can finish shutdown.
        let _drained = self
            .runtime
            .streams
            .clone()
            .acquire_many_owned(max_global_streams)
            .await
            .map_err(|error| {
                ReplicationServerError::Worker(format!("replication stream drain failed: {error}"))
            })?;
        if let Some(error) = lock_unpoisoned(&accept_failure).take() {
            return Err(ReplicationServerError::Io(error));
        }
        if let Some(error) = sender_error {
            return Err(ReplicationServerError::Worker(error));
        }
        result?;
        Ok(())
    }
}

impl ReplicationRuntime {
    fn origin_inbox(&self, origin: OriginId) -> Arc<OriginInbox> {
        let mut inboxes = lock_unpoisoned(&self.origin_inboxes);
        Arc::clone(inboxes.entry(origin).or_insert_with(|| {
            Arc::new(OriginInbox {
                serial: Arc::new(Semaphore::new(1)),
                receiver: Mutex::new(None),
            })
        }))
    }

    fn peer_admission(&self, origin: OriginId) -> Result<AdmissionController, Status> {
        let mut admissions = lock_unpoisoned(&self.peer_admissions);
        if let Some(admission) = admissions.get(&origin) {
            return Ok(admission.clone());
        }
        let limits = AdmissionLimits::new(
            MAX_PEER_BYTES,
            MAX_QUEUED_BATCHES_PER_PEER,
            MAX_STREAMS_PER_PEER,
        )
        .map_err(|error| Status::resource_exhausted(error.to_string()))?;
        let admission = AdmissionController::new(limits);
        admissions.insert(origin, admission.clone());
        Ok(admission)
    }

    fn reserve_stream(&self) -> Result<Arc<StreamLease>, Status> {
        let global_slot = self
            .streams
            .clone()
            .try_acquire_owned()
            .map_err(|_| Status::resource_exhausted("global Sync stream limit reached"))?;
        if !self
            .monitor
            .can_admit(crate::REPLICATION_STREAM_RESERVATION_BYTES, self.profile)
        {
            return Err(Status::resource_exhausted(
                "observed process memory headroom rejects a new Sync stream",
            ));
        }
        let work = self
            .budget
            .try_reserve_work(
                self.attribution,
                crate::REPLICATION_STREAM_RESERVATION_BYTES,
                1,
            )
            .map_err(|error| Status::resource_exhausted(error.to_string()))?;
        Ok(Arc::new(StreamLease {
            _global_slot: global_slot,
            _work: work,
            _active_gauge: ActiveGauge::new(STREAMS_ACTIVE_METRIC),
        }))
    }

    fn reserve_connection(&self) -> Option<(tokio::sync::OwnedSemaphorePermit, WorkReservation)> {
        if !self
            .monitor
            .can_admit(CONNECTION_RESERVATION_BYTES, self.profile)
        {
            return None;
        }
        let slot = self.connections.clone().try_acquire_owned().ok()?;
        let work = self
            .budget
            .try_reserve_work(self.attribution, CONNECTION_RESERVATION_BYTES, 1)
            .ok()?;
        Some((slot, work))
    }

    fn memory_gate(&self) -> tb_protocol::ReceiveMemoryGate {
        let monitor = self.monitor.clone();
        let profile = self.profile;
        Arc::new(move |bytes| monitor.can_admit(bytes, profile))
    }

    fn ensure_origin_receiver(
        self: &Arc<Self>,
        origin: OriginId,
        inbox: &OriginInbox,
    ) -> Result<OriginFrontier, String> {
        if let Some(document) = &self.document {
            let applied = document.store.applied_prefix(origin).map_err(|error| {
                self.close_document_if_poisoned(origin);
                error.to_string()
            })?;
            let durable = document.store.durable_prefix(origin).map_err(|error| {
                self.close_document_if_poisoned(origin);
                error.to_string()
            })?;
            return Ok(OriginFrontier {
                durable,
                applied: Some(applied),
            });
        }
        let mut receiver = lock_unpoisoned(&inbox.receiver);
        if receiver.is_none() {
            let view = self.authority.view();
            let scope = CursorScope {
                database: view.database(),
                shard: view.shard(),
                origin,
                epoch: view.epoch(),
                configuration: view.configuration(),
            };
            let config = StorageConfig::local(
                view.inbox_path(origin),
                self.local_scope.tenant,
                worker_id(origin.replica_id),
            );
            let opened =
                ReplicaReceiver::open(view, config, self.budget.clone(), scope, CURSOR_LIMITS)
                    .map_err(|error| error.to_string())?;
            *receiver = Some(opened);
        }
        Ok(OriginFrontier {
            durable: receiver
                .as_ref()
                .expect("receiver was initialized above")
                .durable_prefix(),
            applied: None,
        })
    }

    fn can_disclose_applied(&self) -> bool {
        !self.replay_failed.load(Ordering::Acquire)
            && self
                .failure_state
                .get()
                .is_some_and(|state| state.is_ready())
    }

    fn close_document_if_poisoned(&self, origin: OriginId) -> bool {
        // A certified peer can still submit an invalid causal claim. The
        // document owner reports that error as Corrupt before touching stored
        // state, so error shape alone cannot close process readiness.
        let owner_poisoned = self.document.as_ref().is_some_and(|document| {
            matches!(
                document.store.durable_prefix(origin),
                Err(DocumentError::NeedsRecovery | DocumentError::Corrupt(_))
            )
        });
        if owner_poisoned && let Some(state) = self.failure_state.get() {
            state.clear_document_readiness();
        }
        owner_poisoned
    }

    fn schedule_document_replay(self: &Arc<Self>, stream_lease: Arc<StreamLease>) {
        if self.document.is_none()
            || self.replay_shutdown.load(Ordering::Acquire)
            || self.replay_failed.load(Ordering::Acquire)
        {
            return;
        }
        self.replay_pending.store(true, Ordering::Release);
        if self
            .replay_running
            .compare_exchange(false, true, Ordering::AcqRel, Ordering::Acquire)
            .is_err()
        {
            return;
        }
        if self.replay_shutdown.load(Ordering::Acquire)
            || self.replay_failed.load(Ordering::Acquire)
        {
            self.replay_running.store(false, Ordering::Release);
            return;
        }
        let runtime = Arc::clone(self);
        tokio::task::spawn_blocking(move || {
            // This lease makes shutdown join the detached worker. Shutdown
            // stops replay at the next bounded slice; startup resumes backlog.
            let mut guard = ReplayWorkerGuard {
                runtime: Arc::clone(&runtime),
                completed: false,
            };
            let mut retry_ticks: usize = 1;
            'work: loop {
                if runtime.replay_shutdown.load(Ordering::Acquire) {
                    break;
                }
                runtime.replay_pending.store(false, Ordering::Release);
                let Some(document) = &runtime.document else {
                    break;
                };
                loop {
                    if runtime.replay_shutdown.load(Ordering::Acquire) {
                        break 'work;
                    }
                    match document.replay_slice(REPLAY_APPLIES_PER_SLICE, |origin, sequence| {
                        document.publish_applied(origin, sequence)
                    }) {
                        Ok(true) => {
                            retry_ticks = 1;
                            std::thread::yield_now();
                        }
                        Ok(false) => {
                            retry_ticks = 1;
                            break;
                        }
                        Err(error) => {
                            metrics::counter!(
                                "terrabase_replication_document_replay_failures_total"
                            )
                            .increment(1);
                            let owner_poisoned = document
                                .first_origin()
                                .is_some_and(|origin| runtime.close_document_if_poisoned(origin));
                            if owner_poisoned || !retryable_document_replay_error(&error) {
                                tracing::error!(%error, "distributed document replay failed");
                                runtime.replay_failed.store(true, Ordering::Release);
                                if let Some(state) = runtime.failure_state.get() {
                                    state.clear_document_readiness();
                                }
                                break 'work;
                            }
                            // The owner is healthy and this failed before any
                            // uncertain publication. Retry without a new peer
                            // frame, but poll shutdown every 50 ms.
                            if retry_ticks == 1 {
                                tracing::warn!(%error, "distributed document replay will retry");
                            }
                            for _ in 0..retry_ticks {
                                if runtime.replay_shutdown.load(Ordering::Acquire) {
                                    break 'work;
                                }
                                std::thread::sleep(DOCUMENT_REPLAY_RETRY_DELAY);
                            }
                            retry_ticks = retry_ticks
                                .saturating_mul(2)
                                .min(DOCUMENT_REPLAY_MAX_RETRY_TICKS);
                        }
                    }
                }
                if !runtime.replay_pending.swap(false, Ordering::AcqRel) {
                    break;
                }
            }
            guard.completed = true;
            drop(guard);
            if runtime.replay_pending.load(Ordering::Acquire)
                && !runtime.replay_shutdown.load(Ordering::Acquire)
                && !runtime.replay_failed.load(Ordering::Acquire)
            {
                runtime.schedule_document_replay(Arc::clone(&stream_lease));
            }
        });
    }
}

fn retryable_document_replay_error(error: &DocumentError) -> bool {
    matches!(
        error,
        DocumentError::Admission(_)
            | DocumentError::Capacity(_)
            | DocumentError::Core(tb_core::CoreError::CapacityExceeded { .. })
            | DocumentError::Format(tb_format::FormatError::Allocation)
            | DocumentError::Metadata(
                tb_replication::MetadataError::Admission(_)
                    | tb_replication::MetadataError::Allocation
                    | tb_replication::MetadataError::Format(tb_format::FormatError::Allocation)
            )
            | DocumentError::Receive(tb_replication::ReceiveError::DurableRead {
                kind: tb_replication::DurableReadKind::Unavailable,
                ..
            })
            | DocumentError::Storage(
                tb_storage::StorageError::Admission(_)
                    | tb_storage::StorageError::Capacity(_)
                    | tb_storage::StorageError::Io(_)
                    | tb_storage::StorageError::Core(tb_core::CoreError::CapacityExceeded { .. })
                    | tb_storage::StorageError::Format(tb_format::FormatError::Allocation)
            )
    )
}

impl Service<HttpRequest<Body>> for SyncRoute {
    type Response = HttpResponse<Body>;
    type Error = Infallible;
    type Future = Pin<Box<dyn Future<Output = Result<Self::Response, Self::Error>> + Send>>;

    fn poll_ready(&mut self, _context: &mut Context<'_>) -> Poll<Result<(), Self::Error>> {
        Poll::Ready(Ok(()))
    }

    fn call(&mut self, request: HttpRequest<Body>) -> Self::Future {
        if request.uri().path() != SYNC_PATH {
            let response = Status::unimplemented("unknown replication method").into_http::<Body>();
            return Box::pin(async move { Ok(response) });
        }
        if *self.shutdown.borrow() {
            let response =
                Status::unavailable("replication server is shutting down").into_http::<Body>();
            return Box::pin(async move { Ok(response) });
        }
        if self.runtime.document.is_some() && !self.runtime.can_disclose_applied() {
            let response = Status::unavailable("distributed document apply is unavailable")
                .into_http::<Body>();
            return Box::pin(async move { Ok(response) });
        }
        let stream_lease = match self.runtime.reserve_stream() {
            Ok(lease) => lease,
            Err(status) => {
                let response = status.into_http::<Body>();
                return Box::pin(async move { Ok(response) });
            }
        };
        let context = Arc::new(
            SyncAdmissionContext::new(self.runtime.budget.clone(), self.runtime.attribution)
                .with_receive_memory_gate(self.runtime.memory_gate()),
        );
        let handler = SyncHandler {
            runtime: Arc::clone(&self.runtime),
            context: Arc::clone(&context),
            stream_lease,
            peer_stream_lease: Arc::new(PeerStreamLease::default()),
            shutdown: self.shutdown.clone(),
        };
        let mut grpc = tonic::server::Grpc::new(SyncCodec::new(context))
            .max_decoding_message_size(tb_protocol::MAX_GRPC_MESSAGE_BYTES)
            .max_encoding_message_size(64 * 1024);
        Box::pin(async move { Ok(grpc.streaming(handler, request).await) })
    }
}

impl tonic::server::NamedService for SyncRoute {
    const NAME: &'static str = "terrabase.v1.Replication";
}

impl Service<Request<Streaming<AdmittedSyncFrame>>> for SyncHandler {
    type Response = Response<GuardedResponseStream>;
    type Error = Status;
    type Future = Pin<Box<dyn Future<Output = Result<Self::Response, Self::Error>> + Send>>;

    fn poll_ready(&mut self, _context: &mut Context<'_>) -> Poll<Result<(), Self::Error>> {
        Poll::Ready(Ok(()))
    }

    fn call(&mut self, request: Request<Streaming<AdmittedSyncFrame>>) -> Self::Future {
        let peer_leaf = request
            .peer_certs()
            .and_then(|certificates| certificates.first().cloned())
            .map(|certificate| certificate.as_ref().to_vec());
        let mut inbound = request.into_inner();
        let (sender, receiver) = mpsc::channel(2);
        let (disconnect_sender, mut disconnect_receiver) = oneshot::channel();
        let (cancel_sender, cancel_receiver) = oneshot::channel();
        let mut shutdown = self.shutdown.clone();
        tokio::spawn(async move {
            tokio::select! {
                _ = &mut disconnect_receiver => {}
                () = wait_for_shutdown(&mut shutdown) => {}
            }
            let _ = cancel_sender.send(());
        });
        let runtime = Arc::clone(&self.runtime);
        let context = Arc::clone(&self.context);
        let stream_lease = Arc::clone(&self.stream_lease);
        let peer_stream_lease = Arc::clone(&self.peer_stream_lease);
        let task = SyncTaskContext {
            runtime,
            context,
            stream_lease,
            peer_stream_lease,
        };
        let _task = tokio::spawn(async move {
            handle_sync(&mut inbound, peer_leaf, &task, sender, cancel_receiver).await;
        });
        let response_stream = GuardedResponseStream {
            receiver,
            disconnect: Some(disconnect_sender),
            _stream_lease: Arc::clone(&self.stream_lease),
            _peer_stream_lease: Arc::clone(&self.peer_stream_lease),
        };
        Box::pin(async move { Ok(Response::new(response_stream)) })
    }
}

impl Stream for GuardedResponseStream {
    type Item = Result<v1::SyncResponse, Status>;

    fn poll_next(mut self: Pin<&mut Self>, context: &mut Context<'_>) -> Poll<Option<Self::Item>> {
        self.receiver.poll_recv(context)
    }
}

impl Drop for GuardedResponseStream {
    fn drop(&mut self) {
        if let Some(disconnect) = self.disconnect.take() {
            let _ = disconnect.send(());
        }
    }
}

async fn wait_for_shutdown(shutdown: &mut watch::Receiver<bool>) {
    loop {
        if *shutdown.borrow() {
            return;
        }
        if shutdown.changed().await.is_err() {
            return;
        }
    }
}

async fn handle_sync(
    inbound: &mut Streaming<AdmittedSyncFrame>,
    peer_leaf: Option<Vec<u8>>,
    task: &SyncTaskContext,
    sender: mpsc::Sender<Result<v1::SyncResponse, Status>>,
    mut cancellation: oneshot::Receiver<()>,
) {
    let Some((hello, hello_permit)) = receive_hello(inbound, &sender, &mut cancellation).await
    else {
        return;
    };
    let Some(authenticated) = authenticate_hello(
        hello,
        hello_permit,
        peer_leaf,
        task,
        &sender,
        &mut cancellation,
    )
    .await
    else {
        return;
    };
    let Some(mut prepared) = prepare_sync(authenticated, task, &sender, &mut cancellation).await
    else {
        return;
    };
    process_sync_frames(inbound, &mut prepared, task, &sender, &mut cancellation).await;
}

async fn receive_hello(
    inbound: &mut Streaming<AdmittedSyncFrame>,
    sender: &mpsc::Sender<Result<v1::SyncResponse, Status>>,
    cancellation: &mut oneshot::Receiver<()>,
) -> Option<(v1::Hello, WorkReservation)> {
    let first = tokio::select! {
        _ = &mut *cancellation => return None,
        result = inbound.message() => result,
    };
    let frame = match first {
        Ok(Some(frame)) => frame,
        Ok(None) => {
            send_error(
                sender,
                cancellation,
                Status::failed_precondition("Sync requires Hello as its first frame"),
            )
            .await;
            return None;
        }
        Err(status) => {
            send_error(sender, cancellation, status).await;
            return None;
        }
    };
    let AdmittedSyncFrame::Hello { request, permit } = frame else {
        send_error(
            sender,
            cancellation,
            Status::invalid_argument("first Sync frame must be Hello"),
        )
        .await;
        return None;
    };
    Some((request, permit))
}

async fn authenticate_hello(
    request: v1::Hello,
    permit: WorkReservation,
    peer_leaf: Option<Vec<u8>>,
    task: &SyncTaskContext,
    sender: &mpsc::Sender<Result<v1::SyncResponse, Status>>,
    cancellation: &mut oneshot::Receiver<()>,
) -> Option<AuthenticatedHello> {
    let Some(leaf) = peer_leaf else {
        send_error(
            sender,
            cancellation,
            Status::unauthenticated("mTLS peer certificate is missing"),
        )
        .await;
        return None;
    };
    let peer_scope = match peer_hello_scope(&task.runtime, &request) {
        Ok(scope) => scope,
        Err(status) => {
            send_error(sender, cancellation, status).await;
            return None;
        }
    };
    let certified = match task
        .runtime
        .authority
        .view()
        .certify_peer(peer_scope, &leaf)
    {
        Ok(certified) => certified,
        Err(error) => {
            send_error(
                sender,
                cancellation,
                Status::unauthenticated(error.to_string()),
            )
            .await;
            return None;
        }
    };
    let origin = certified.origin();
    let admission = match task.runtime.peer_admission(origin) {
        Ok(admission) => admission,
        Err(status) => {
            send_error(sender, cancellation, status).await;
            return None;
        }
    };
    let peer_stream = match admission.try_open_stream() {
        Ok(permit) => permit,
        Err(error) => {
            send_error(
                sender,
                cancellation,
                Status::resource_exhausted(error.to_string()),
            )
            .await;
            return None;
        }
    };
    *lock_unpoisoned(&task.peer_stream_lease.0) = Some(peer_stream);
    let inbox = task.runtime.origin_inbox(origin);
    Some(AuthenticatedHello {
        request,
        permit,
        leaf,
        peer_scope,
        origin,
        admission,
        inbox,
    })
}

#[allow(
    clippy::too_many_lines,
    reason = "one handshake prepares negotiated limits, frontier, applied watch, and Welcome"
)]
async fn prepare_sync(
    authenticated: AuthenticatedHello,
    task: &SyncTaskContext,
    sender: &mpsc::Sender<Result<v1::SyncResponse, Status>>,
    cancellation: &mut oneshot::Receiver<()>,
) -> Option<PreparedSync> {
    let limits = match v1_transfer_limits(
        MAX_PEER_BYTES,
        MAX_QUEUED_BATCHES_PER_PEER,
        MAX_STREAMS_PER_PEER,
    ) {
        Ok(limits) => limits,
        Err(error) => {
            send_error(sender, cancellation, protocol_status(error)).await;
            return None;
        }
    };
    let policy = NegotiationPolicy {
        local_identity: task.runtime.local_identity.clone(),
        protocol_version: v1::ProtocolVersion {
            major: V1_PROTOCOL_MAJOR,
            minor: V1_PROTOCOL_MINOR,
        },
        event_format_version: V1_EVENT_FORMAT,
        record_format_versions: SUPPORTED_RECORD_FORMATS.to_vec(),
        merge_policy_version: V1_MERGE_POLICY,
        codecs: vec![v1::PayloadCodec::Lz4BlockV1],
        limits,
        frontiers: Vec::new(),
    };
    let mut welcome = match negotiate_hello(&authenticated.request, &policy) {
        Ok(welcome) => welcome,
        Err(error) => {
            send_error(sender, cancellation, protocol_status(error)).await;
            return None;
        }
    };
    let applied_updates = if let Some(document) = &task.runtime.document {
        if !task.runtime.can_disclose_applied() {
            send_error(
                sender,
                cancellation,
                Status::unavailable("distributed document apply is unavailable"),
            )
            .await;
            return None;
        }
        let Some(receiver) = document.subscribe_applied(authenticated.origin) else {
            task.runtime.replay_failed.store(true, Ordering::Release);
            if let Some(state) = task.runtime.failure_state.get() {
                state.clear_document_readiness();
            }
            send_error(
                sender,
                cancellation,
                Status::internal("signed applied origin watch is missing"),
            )
            .await;
            return None;
        };
        Some(receiver)
    } else {
        None
    };
    let frontier = open_origin_inbox(&authenticated, task, sender, cancellation).await?;
    if task.runtime.document.is_some() && !task.runtime.can_disclose_applied() {
        send_error(
            sender,
            cancellation,
            Status::unavailable("distributed document apply is unavailable"),
        )
        .await;
        return None;
    }
    welcome.frontiers = vec![frontier_for(
        authenticated.origin,
        frontier.durable.sequence,
        frontier.applied,
    )];
    if let Err(error) = task.context.authorize_peer(authenticated.admission) {
        send_error(sender, cancellation, protocol_status(error)).await;
        return None;
    }
    drop(authenticated.permit);
    let negotiated_limits = welcome.limits?;
    let record_format_version = welcome.record_format_version;
    let negotiated_codecs = welcome
        .codecs
        .iter()
        .filter_map(|codec| v1::PayloadCodec::try_from(*codec).ok())
        .collect();
    if !send_response(
        sender,
        cancellation,
        v1::SyncResponse {
            frame: Some(v1::sync_response::Frame::Welcome(welcome)),
        },
    )
    .await
    {
        return None;
    }
    Some(PreparedSync {
        origin: authenticated.origin,
        peer_scope: authenticated.peer_scope,
        leaf: authenticated.leaf,
        inbox: authenticated.inbox,
        negotiated_limits,
        negotiated_codecs,
        record_format_version,
        applied_updates,
        disclosed_durable: frontier.durable.sequence,
        disclosed_applied: frontier.applied.unwrap_or(0),
    })
}

async fn open_origin_inbox(
    authenticated: &AuthenticatedHello,
    task: &SyncTaskContext,
    sender: &mpsc::Sender<Result<v1::SyncResponse, Status>>,
    cancellation: &mut oneshot::Receiver<()>,
) -> Option<OriginFrontier> {
    let slot = tokio::select! {
        _ = &mut *cancellation => return None,
        result = authenticated.inbox.serial.clone().acquire_owned() => result,
    };
    let Ok(inbox_slot) = slot else {
        send_error(
            sender,
            cancellation,
            Status::unavailable("origin journal worker is closed"),
        )
        .await;
        return None;
    };
    let runtime = Arc::clone(&task.runtime);
    let inbox = Arc::clone(&authenticated.inbox);
    let stream_lease = Arc::clone(&task.stream_lease);
    let peer_lease = Arc::clone(&task.peer_stream_lease);
    let origin = authenticated.origin;
    let result = tokio::select! {
        _ = &mut *cancellation => return None,
        result = tokio::task::spawn_blocking(move || {
            let _stream_lease = stream_lease;
            let _peer_lease = peer_lease;
            let _inbox_slot = inbox_slot;
            runtime.ensure_origin_receiver(origin, &inbox)
        }) => result,
    };
    match result {
        Ok(Ok(prefix)) => Some(prefix),
        Ok(Err(error)) => {
            send_error(
                sender,
                cancellation,
                Status::internal(format!("open origin inbox: {error}")),
            )
            .await;
            None
        }
        Err(error) => {
            send_error(
                sender,
                cancellation,
                Status::internal(format!("origin journal worker failed: {error}")),
            )
            .await;
            None
        }
    }
}

async fn process_sync_frames(
    inbound: &mut Streaming<AdmittedSyncFrame>,
    prepared: &mut PreparedSync,
    task: &SyncTaskContext,
    sender: &mpsc::Sender<Result<v1::SyncResponse, Status>>,
    cancellation: &mut oneshot::Receiver<()>,
) {
    let mut disclosed = DisclosedFrontier {
        durable: prepared.disclosed_durable,
        applied: prepared.disclosed_applied,
    };
    loop {
        let input = if let Some(updates) = prepared.applied_updates.as_mut() {
            tokio::select! {
                biased;
                _ = &mut *cancellation => SyncInput::Cancelled,
                result = inbound.message() => SyncInput::Frame(result),
                changed = updates.changed() => SyncInput::AppliedChanged(changed),
            }
        } else {
            tokio::select! {
                _ = &mut *cancellation => SyncInput::Cancelled,
                result = inbound.message() => SyncInput::Frame(result),
            }
        };
        let frame = match input {
            SyncInput::Cancelled => break,
            SyncInput::Frame(Ok(Some(frame))) => frame,
            SyncInput::Frame(Ok(None)) => {
                let _ = flush_applied(prepared, &mut disclosed, task, sender, cancellation).await;
                break;
            }
            SyncInput::Frame(Err(status)) => {
                let _ = send_error(sender, cancellation, status).await;
                break;
            }
            SyncInput::AppliedChanged(Ok(())) => {
                if !flush_applied(prepared, &mut disclosed, task, sender, cancellation).await {
                    break;
                }
                continue;
            }
            SyncInput::AppliedChanged(Err(_)) => {
                let _ = send_error(
                    sender,
                    cancellation,
                    Status::internal("distributed applied watch closed"),
                )
                .await;
                break;
            }
        };
        if prepared.applied_updates.is_some() && !task.runtime.can_disclose_applied() {
            let _ = send_error(
                sender,
                cancellation,
                Status::unavailable("distributed document apply is unavailable"),
            )
            .await;
            break;
        }
        if !process_sync_frame(frame, prepared, &mut disclosed, task, sender, cancellation).await {
            break;
        }
        if !flush_applied(prepared, &mut disclosed, task, sender, cancellation).await {
            break;
        }
    }
}

async fn flush_applied(
    prepared: &mut PreparedSync,
    disclosed: &mut DisclosedFrontier,
    task: &SyncTaskContext,
    sender: &mpsc::Sender<Result<v1::SyncResponse, Status>>,
    cancellation: &mut oneshot::Receiver<()>,
) -> bool {
    let Some(updates) = prepared.applied_updates.as_mut() else {
        return true;
    };
    if !task.runtime.can_disclose_applied() {
        return false;
    }
    let applied = *updates.borrow_and_update();
    if applied <= disclosed.applied {
        return true;
    }
    let view = task.runtime.authority.view();
    let id = match EventId::new(view.database(), view.shard(), prepared.origin, applied) {
        Ok(id) => id,
        Err(error) => {
            let _ = send_error(sender, cancellation, Status::internal(error.to_string())).await;
            return false;
        }
    };
    let durable = DurablePrefix {
        sequence: applied,
        epoch: view.epoch(),
        configuration: view.configuration(),
        origin: prepared.origin,
    };
    if applied > disclosed.durable {
        if !send_response(
            sender,
            cancellation,
            durable_ack(&task.runtime.local_identity, id, durable),
        )
        .await
        {
            return false;
        }
        metrics::counter!("terrabase_replication_acks_total", "stage" => "durable").increment(1);
        disclosed.durable = applied;
    }
    if !task.runtime.can_disclose_applied() {
        return false;
    }
    if !send_response(
        sender,
        cancellation,
        applied_ack(&task.runtime.local_identity, id, durable),
    )
    .await
    {
        return false;
    }
    metrics::counter!("terrabase_replication_acks_total", "stage" => "applied").increment(1);
    disclosed.applied = applied;
    true
}

async fn process_sync_frame(
    frame: AdmittedSyncFrame,
    prepared: &PreparedSync,
    disclosed: &mut DisclosedFrontier,
    task: &SyncTaskContext,
    sender: &mpsc::Sender<Result<v1::SyncResponse, Status>>,
    cancellation: &mut oneshot::Receiver<()>,
) -> bool {
    match frame {
        AdmittedSyncFrame::CommittedGroup(admitted) => {
            process_committed_group(admitted, prepared, disclosed, task, sender, cancellation).await
        }
        AdmittedSyncFrame::CreditUpdate { permit, .. } => {
            drop(permit);
            true
        }
        AdmittedSyncFrame::KeepAlive { request, permit } => {
            drop(permit);
            send_response(
                sender,
                cancellation,
                v1::SyncResponse {
                    frame: Some(v1::sync_response::Frame::KeepAlive(request)),
                },
            )
            .await
        }
        AdmittedSyncFrame::Hello { permit, .. } => {
            drop(permit);
            let _ = send_error(
                sender,
                cancellation,
                Status::invalid_argument("Hello may only be sent as the first Sync frame"),
            )
            .await;
            false
        }
    }
}

async fn process_committed_group(
    admitted: tb_protocol::PreAdmittedBatch,
    prepared: &PreparedSync,
    disclosed: &mut DisclosedFrontier,
    task: &SyncTaskContext,
    sender: &mpsc::Sender<Result<v1::SyncResponse, Status>>,
    cancellation: &mut oneshot::Receiver<()>,
) -> bool {
    if let Err(status) = validate_batch_record_format(
        admitted.batch().record_format_version,
        prepared.record_format_version,
    ) {
        let _ = send_error(sender, cancellation, status).await;
        return false;
    }
    let Some(staged) = append_group(admitted, prepared, task, sender, cancellation).await else {
        return false;
    };
    if staged.durable.origin != prepared.origin
        || staged.durable.epoch != task.runtime.authority.view().epoch()
        || staged.durable.configuration != task.runtime.authority.view().configuration()
    {
        let _ = send_error(
            sender,
            cancellation,
            Status::data_loss("durable inbox returned a foreign frontier"),
        )
        .await;
        return false;
    }
    if staged.durable.sequence >= staged.id.sequence() {
        let response = durable_ack(&task.runtime.local_identity, staged.id, staged.durable);
        if !send_response(sender, cancellation, response).await {
            return false;
        }
        metrics::counter!("terrabase_replication_acks_total", "stage" => "durable").increment(1);
        disclosed.durable = disclosed.durable.max(staged.durable.sequence);
    }
    send_response(
        sender,
        cancellation,
        v1::SyncResponse {
            frame: Some(v1::sync_response::Frame::Credit(v1::CreditUpdate {
                granted_bytes: staged.credit_bytes as u64,
            })),
        },
    )
    .await
}

async fn append_group(
    admitted: tb_protocol::PreAdmittedBatch,
    prepared: &PreparedSync,
    task: &SyncTaskContext,
    sender: &mpsc::Sender<Result<v1::SyncResponse, Status>>,
    cancellation: &mut oneshot::Receiver<()>,
) -> Option<StagedEvent> {
    let credit_bytes = usize::try_from(admitted.batch().encoded_application_bytes)
        .ok()
        .and_then(|encoded| {
            usize::try_from(admitted.batch().decoded_application_bytes)
                .ok()
                .and_then(|decoded| encoded.checked_add(decoded))
        });
    let Some(credit_bytes) = credit_bytes else {
        send_error(
            sender,
            cancellation,
            Status::invalid_argument("batch credit byte count overflow"),
        )
        .await;
        return None;
    };
    let slot = tokio::select! {
        _ = &mut *cancellation => return None,
        result = prepared.inbox.serial.clone().acquire_owned() => result,
    };
    let Ok(serial) = slot else {
        send_error(
            sender,
            cancellation,
            Status::unavailable("origin journal worker is closed"),
        )
        .await;
        return None;
    };
    let runtime = Arc::clone(&task.runtime);
    let inbox = Arc::clone(&prepared.inbox);
    let leaf = prepared.leaf.clone();
    let stream_lease = Arc::clone(&task.stream_lease);
    let peer_lease = Arc::clone(&task.peer_stream_lease);
    let origin = prepared.origin;
    let peer_scope = prepared.peer_scope;
    let codecs = prepared.negotiated_codecs.clone();
    let negotiated_limits = prepared.negotiated_limits;
    let record_format_version = prepared.record_format_version;
    #[cfg(feature = "replication-test-hooks")]
    let post_sync_barrier = task.runtime.post_sync_barrier.clone();
    #[cfg(feature = "replication-test-hooks")]
    let cancellation_barrier = post_sync_barrier
        .clone()
        .or_else(|| task.runtime.append_barrier.clone());
    let result = tokio::select! {
        _ = &mut *cancellation => None,
        result = tokio::task::spawn_blocking(move || {
            let stream_lease_guard = stream_lease;
            let _peer_lease = peer_lease;
            let _serial = serial;
            let _append_worker_gauge = ActiveGauge::new(APPEND_WORKERS_ACTIVE_METRIC);
            let result = decode_and_append(
                admitted,
                AppendContext {
                    runtime: &runtime,
                    inbox: &inbox,
                    origin,
                    peer_scope,
                    peer_leaf: &leaf,
                    codecs: &codecs,
                    negotiated_limits: &negotiated_limits,
                    record_format_version,
                    credit_bytes,
                },
            );
            // decode_and_append returns only after the durable store path succeeds.
            // Pause here so a process test can drop the stream before DurableAck.
            #[cfg(feature = "replication-test-hooks")]
            let result = result.and_then(|staged| {
                if let Some(barrier) = &post_sync_barrier {
                    barrier.wait().map_err(|error| {
                        Status::internal(format!(
                            "replication test post-sync barrier failed: {error}"
                        ))
                    })?;
                }
                Ok(staged)
            });
            if result.is_ok() {
                runtime.schedule_document_replay(Arc::clone(&stream_lease_guard));
            }
            result
        }) => Some(result),
    };
    let Some(result) = result else {
        #[cfg(feature = "replication-test-hooks")]
        if let Some(barrier) = cancellation_barrier
            && let Err(error) = barrier.record_cancellation()
        {
            tracing::error!(%error, "failed to write replication append cancellation marker");
        }
        return None;
    };
    match result {
        Ok(Ok(staged)) => Some(staged),
        Ok(Err(status)) => {
            send_error(sender, cancellation, status).await;
            None
        }
        Err(error) => {
            send_error(
                sender,
                cancellation,
                Status::internal(format!("replication journal worker failed: {error}")),
            )
            .await;
            None
        }
    }
}

fn decode_and_append(
    admitted: tb_protocol::PreAdmittedBatch,
    context: AppendContext<'_>,
) -> Result<StagedEvent, Status> {
    let AppendContext {
        runtime,
        inbox,
        origin,
        peer_scope,
        peer_leaf,
        codecs,
        negotiated_limits,
        record_format_version,
        credit_bytes,
    } = context;
    let decoded = decode_transport_group_pre_admitted(
        admitted,
        &runtime.budget,
        runtime.attribution,
        codecs,
        negotiated_limits,
    )
    .map_err(protocol_status)?;
    let digest = EventDigest::from_bytes(decoded.event_digest());
    let proof = verify_incoming(
        decoded.metadata_bytes(),
        decoded.wal_frame_bytes(),
        digest,
        EVENT_LIMITS,
        &runtime.budget,
        runtime.attribution,
    )
    .map_err(|error| Status::invalid_argument(error.to_string()))?;
    let event = proof.event();
    validate_transport_claims(&decoded, event, record_format_version)?;
    if event.id().origin() != origin {
        return Err(Status::permission_denied(
            "event origin differs from authenticated Hello",
        ));
    }
    let member = runtime
        .authority
        .view()
        .certify_peer(peer_scope, peer_leaf)
        .map_err(|error| Status::unauthenticated(error.to_string()))?;
    let certified = runtime
        .authority
        .view()
        .certify_event(&member, &proof)
        .map_err(|error| Status::permission_denied(error.to_string()))?;
    #[cfg(feature = "replication-test-hooks")]
    if let Some(barrier) = &runtime.append_barrier {
        barrier.wait().map_err(|error| {
            Status::internal(format!("replication test append barrier failed: {error}"))
        })?;
    }
    let durable = if let Some(document) = &runtime.document {
        let outcome = document
            .store
            .receive_certified(certified)
            .map_err(|error| {
                runtime.close_document_if_poisoned(origin);
                Status::internal(format!("sync distributed document: {error}"))
            })?;
        let outcome_label = match outcome {
            DocumentApplyOutcome::Applied { .. } => "applied",
            DocumentApplyOutcome::Duplicate { .. } => "duplicate",
            DocumentApplyOutcome::DurableOnly { .. } => "durable_only",
        };
        metrics::counter!(
            "terrabase_replication_document_receive_total",
            "outcome" => outcome_label
        )
        .increment(1);
        let durable = document.store.durable_prefix(origin).map_err(|error| {
            runtime.close_document_if_poisoned(origin);
            Status::internal(format!("read distributed durable prefix: {error}"))
        })?;
        if let DocumentApplyOutcome::Applied { sequence } = outcome {
            document
                .publish_applied(origin, sequence)
                .map_err(|error| {
                    runtime.close_document_if_poisoned(origin);
                    Status::internal(format!("publish distributed applied prefix: {error}"))
                })?;
        }
        durable
    } else {
        let mut receiver = lock_unpoisoned(&inbox.receiver);
        receiver
            .as_mut()
            .ok_or_else(|| Status::internal("origin inbox was not opened during Hello"))?
            .append_certified(certified)
            .map_err(|error| Status::internal(format!("sync origin inbox: {error}")))?
    };
    Ok(StagedEvent {
        id: event.id(),
        durable,
        credit_bytes,
    })
}

fn validate_transport_claims(
    decoded: &tb_protocol::DecodedTransportGroup,
    event: &tb_replication::CommitEvent,
    selected_record_format_version: u32,
) -> Result<(), Status> {
    let id = decoded
        .event_id_claim()
        .ok_or_else(|| Status::invalid_argument("missing transport event identity"))?;
    let origin = id
        .origin
        .as_ref()
        .ok_or_else(|| Status::invalid_argument("missing transport origin identity"))?;
    let event_id = event.id();
    let event_origin = event_id.origin();
    let versions = event.versions();
    let (event_format, record_format, merge_policy) = decoded.format_version_claims();
    if id.database_id.as_ref() != event_id.database().to_bytes().as_slice()
        || id.shard_id != event_id.shard().get()
        || origin.replica_id.as_ref() != event_origin.replica_id.to_bytes().as_slice()
        || origin.incarnation.as_ref() != event_origin.incarnation.as_slice()
        || id.sequence != event_id.sequence()
        || decoded.membership_epoch_claim() != event.epoch().get()
        || decoded.configuration_id_claim() != event.configuration().to_bytes().as_slice()
        || decoded.transaction_id() != event.transaction_id()
        || decoded.event_digest() != event.digest().to_bytes()
        || decoded.change_count_claim() as usize != event.changes().len()
        || event_format != u32::from(versions.event_format)
        || record_format != u32::from(versions.record_format)
        || record_format != selected_record_format_version
        || merge_policy != u32::from(versions.merge_policy)
        || event_format != V1_EVENT_FORMAT
        || merge_policy != V1_MERGE_POLICY
    {
        return Err(Status::invalid_argument(
            "transport header differs from the verified committed event",
        ));
    }
    Ok(())
}

fn validate_batch_record_format(
    record_format_version: u32,
    selected_record_format_version: u32,
) -> Result<(), Status> {
    if record_format_version != selected_record_format_version {
        return Err(Status::invalid_argument(
            "committed group record format differs from selected Welcome",
        ));
    }
    Ok(())
}

fn peer_hello_scope(
    runtime: &ReplicationRuntime,
    hello: &v1::Hello,
) -> Result<PeerHelloScope, Status> {
    let identity = hello
        .local_identity
        .as_ref()
        .ok_or_else(|| Status::unauthenticated("Hello has no peer identity"))?;
    let tenant = exact_array::<16>(&identity.tenant_id, "tenant identity")?;
    let database = exact_array::<16>(&identity.database_id, "database identity")?;
    let member = exact_array::<16>(&identity.member_id, "member identity")?;
    let incarnation = exact_array::<16>(&identity.member_incarnation, "member incarnation")?;
    let configuration = exact_array::<32>(&identity.configuration_id, "configuration identity")?;
    let configuration = ConfigurationId::from_bytes(configuration)
        .map_err(|error| Status::unauthenticated(error.to_string()))?;
    let tenant = TenantId::from_bytes(tenant);
    let database = DatabaseId::from_bytes(database);
    let member = ReplicaId::from_bytes(member);
    let origin = OriginId {
        replica_id: member,
        incarnation,
    };
    if tenant != runtime.local_scope.tenant
        || database != runtime.local_scope.database
        || identity.shard_id != runtime.local_scope.shard.get()
    {
        return Err(Status::permission_denied(
            "Hello is outside the accepted shard scope",
        ));
    }
    Ok(PeerHelloScope {
        cluster: runtime.local_scope.cluster,
        tenant,
        database,
        shard: ShardId::new(identity.shard_id),
        origin,
        epoch: MembershipEpoch::new(identity.membership_epoch),
        configuration,
    })
}

fn make_peer_identity(
    tenant: TenantId,
    database: DatabaseId,
    shard: ShardId,
    member: ReplicaId,
    incarnation: [u8; 16],
    view: &CertifiedShardView,
) -> v1::PeerIdentity {
    v1::PeerIdentity {
        tenant_id: tenant.to_bytes().to_vec().into(),
        database_id: database.to_bytes().to_vec().into(),
        shard_id: shard.get(),
        member_id: member.to_bytes().to_vec().into(),
        member_incarnation: incarnation.to_vec().into(),
        membership_epoch: view.epoch().get(),
        configuration_id: view.configuration().to_bytes().to_vec().into(),
    }
}

fn frontier_for(origin: OriginId, durable: u64, applied: Option<u64>) -> v1::PeerFrontier {
    v1::PeerFrontier {
        origin: Some(v1::OriginIdentity {
            replica_id: origin.replica_id.to_bytes().to_vec().into(),
            incarnation: origin.incarnation.to_vec().into(),
        }),
        received_sequence: Some(durable),
        durable_sequence: Some(durable),
        applied_sequence: applied,
    }
}

fn ack_context(
    local_identity: &v1::PeerIdentity,
    id: EventId,
    durable: DurablePrefix,
) -> v1::AckContext {
    v1::AckContext {
        acknowledging_peer: Some(local_identity.clone()),
        membership_epoch: durable.epoch.get(),
        configuration_id: durable.configuration.to_bytes().to_vec().into(),
        event_id: Some(v1::CommittedEventId {
            database_id: id.database().to_bytes().to_vec().into(),
            shard_id: id.shard().get(),
            origin: Some(v1::OriginIdentity {
                replica_id: id.origin().replica_id.to_bytes().to_vec().into(),
                incarnation: id.origin().incarnation.to_vec().into(),
            }),
            sequence: id.sequence(),
        }),
    }
}

fn durable_ack(
    local_identity: &v1::PeerIdentity,
    id: EventId,
    durable: DurablePrefix,
) -> v1::SyncResponse {
    v1::SyncResponse {
        frame: Some(v1::sync_response::Frame::Durable(v1::DurableAck {
            context: Some(ack_context(local_identity, id, durable)),
            durable_prefix: durable.sequence,
        })),
    }
}

fn applied_ack(
    local_identity: &v1::PeerIdentity,
    id: EventId,
    durable: DurablePrefix,
) -> v1::SyncResponse {
    v1::SyncResponse {
        frame: Some(v1::sync_response::Frame::Applied(v1::AppliedAck {
            context: Some(ack_context(local_identity, id, durable)),
            applied_prefix: id.sequence(),
        })),
    }
}

async fn send_error(
    sender: &mpsc::Sender<Result<v1::SyncResponse, Status>>,
    cancellation: &mut oneshot::Receiver<()>,
    status: Status,
) -> bool {
    tokio::select! {
        _ = cancellation => false,
        result = sender.send(Err(status)) => result.is_ok(),
    }
}

async fn send_response(
    sender: &mpsc::Sender<Result<v1::SyncResponse, Status>>,
    cancellation: &mut oneshot::Receiver<()>,
    response: v1::SyncResponse,
) -> bool {
    tokio::select! {
        _ = cancellation => false,
        result = sender.send(Ok(response)) => result.is_ok(),
    }
}

fn protocol_status(error: impl std::fmt::Display) -> Status {
    let message = error.to_string();
    if message.contains("limit") || message.contains("admission") || message.contains("credit") {
        Status::resource_exhausted(message)
    } else {
        Status::invalid_argument(message)
    }
}

fn exact_array<const N: usize>(bytes: &[u8], label: &'static str) -> Result<[u8; N], Status> {
    bytes
        .try_into()
        .map_err(|_| Status::unauthenticated(format!("invalid {label} length")))
}

pub(crate) fn worker_id(replica: ReplicaId) -> WorkerId {
    let bytes = replica.to_bytes();
    let mut prefix = [0_u8; 8];
    prefix.copy_from_slice(&bytes[..8]);
    WorkerId(u64::from_be_bytes(prefix))
}

fn read_bounded(path: &std::path::Path, maximum: usize) -> Result<Vec<u8>, ReplicationServerError> {
    let file = std::fs::File::open(path)?;
    let size = usize::try_from(file.metadata()?.len())
        .map_err(|_| ReplicationServerError::Tls("TLS/authority file is too large".into()))?;
    if size > maximum {
        return Err(ReplicationServerError::Tls(format!(
            "{} exceeds its {} byte limit",
            path.display(),
            maximum
        )));
    }
    let read_limit = u64::try_from(maximum).unwrap_or(u64::MAX).saturating_add(1);
    let mut bytes = Vec::with_capacity(size);
    file.take(read_limit).read_to_end(&mut bytes)?;
    if bytes.len() > maximum {
        return Err(ReplicationServerError::Tls(format!(
            "{} grew beyond its {} byte limit while being read",
            path.display(),
            maximum
        )));
    }
    Ok(bytes)
}

fn lock_unpoisoned<T>(mutex: &Mutex<T>) -> MutexGuard<'_, T> {
    mutex
        .lock()
        .unwrap_or_else(std::sync::PoisonError::into_inner)
}

fn admitted_connections(
    listener: TcpListener,
    runtime: Arc<ReplicationRuntime>,
    failure_state: Arc<LocalServerState>,
    accept_failure: Arc<Mutex<Option<io::Error>>>,
    accept_failure_sender: mpsc::UnboundedSender<()>,
) -> impl Stream<Item = Result<AdmittedTcpStream, io::Error>> + Send + 'static {
    stream::unfold(
        (
            listener,
            runtime,
            failure_state,
            accept_failure,
            accept_failure_sender,
        ),
        |(listener, runtime, failure_state, accept_failure, accept_failure_sender)| async move {
            loop {
                match listener.accept().await {
                    Ok((stream, _address)) => {
                        let Some((connection_slot, work)) = runtime.reserve_connection() else {
                            drop(stream);
                            continue;
                        };
                        return Some((
                            Ok(AdmittedTcpStream {
                                stream,
                                _connection_slot: connection_slot,
                                _work: work,
                                _active_gauge: ActiveGauge::new(CONNECTIONS_ACTIVE_METRIC),
                            }),
                            (
                                listener,
                                runtime,
                                failure_state,
                                accept_failure,
                                accept_failure_sender,
                            ),
                        ));
                    }
                    Err(error) if is_retryable_accept_error(&error) => {
                        tokio::time::sleep(ACCEPT_ERROR_RETRY_DELAY).await;
                    }
                    Err(error) => {
                        report_terminal_accept_failure(
                            error,
                            &accept_failure,
                            &accept_failure_sender,
                            || failure_state.mark_failed(),
                        );
                        return None;
                    }
                }
            }
        },
    )
}

fn report_terminal_accept_failure(
    error: io::Error,
    accept_failure: &Mutex<Option<io::Error>>,
    accept_failure_sender: &mpsc::UnboundedSender<()>,
    mark_failed: impl FnOnce(),
) {
    mark_failed();
    *lock_unpoisoned(accept_failure) = Some(error);
    let _ = accept_failure_sender.send(());
}

fn is_retryable_accept_error(error: &io::Error) -> bool {
    matches!(
        error.kind(),
        io::ErrorKind::ConnectionAborted
            | io::ErrorKind::ConnectionReset
            | io::ErrorKind::BrokenPipe
            | io::ErrorKind::Interrupted
            | io::ErrorKind::WouldBlock
            | io::ErrorKind::TimedOut
    )
}

impl tonic::transport::server::Connected for AdmittedTcpStream {
    type ConnectInfo = tonic::transport::server::TcpConnectInfo;

    fn connect_info(&self) -> Self::ConnectInfo {
        tonic::transport::server::TcpConnectInfo {
            local_addr: self.stream.local_addr().ok(),
            remote_addr: self.stream.peer_addr().ok(),
        }
    }
}

impl AsyncRead for AdmittedTcpStream {
    fn poll_read(
        mut self: Pin<&mut Self>,
        context: &mut Context<'_>,
        buffer: &mut ReadBuf<'_>,
    ) -> Poll<io::Result<()>> {
        Pin::new(&mut self.stream).poll_read(context, buffer)
    }
}

impl AsyncWrite for AdmittedTcpStream {
    fn poll_write(
        mut self: Pin<&mut Self>,
        context: &mut Context<'_>,
        bytes: &[u8],
    ) -> Poll<io::Result<usize>> {
        Pin::new(&mut self.stream).poll_write(context, bytes)
    }

    fn poll_flush(mut self: Pin<&mut Self>, context: &mut Context<'_>) -> Poll<io::Result<()>> {
        Pin::new(&mut self.stream).poll_flush(context)
    }

    fn poll_shutdown(mut self: Pin<&mut Self>, context: &mut Context<'_>) -> Poll<io::Result<()>> {
        Pin::new(&mut self.stream).poll_shutdown(context)
    }
}

#[cfg(test)]
mod tests {
    use std::sync::atomic::{AtomicBool, Ordering};

    use super::*;

    #[test]
    fn accept_error_classification_preserves_tonics_retry_set() {
        for kind in [
            io::ErrorKind::ConnectionAborted,
            io::ErrorKind::ConnectionReset,
            io::ErrorKind::BrokenPipe,
            io::ErrorKind::Interrupted,
            io::ErrorKind::WouldBlock,
            io::ErrorKind::TimedOut,
        ] {
            assert!(
                is_retryable_accept_error(&io::Error::from(kind)),
                "{kind:?} must be retried"
            );
        }
        assert!(!is_retryable_accept_error(&io::Error::other("fatal")));
    }

    #[test]
    fn terminal_accept_failure_marks_unready_before_notifying_shutdown() {
        let readiness = Arc::new(AtomicBool::new(true));
        let readiness_for_failure = Arc::clone(&readiness);
        let accept_failure = Mutex::new(None);
        let (shutdown_sender, mut shutdown_receiver) = mpsc::unbounded_channel();

        report_terminal_accept_failure(
            io::Error::other("listener stopped"),
            &accept_failure,
            &shutdown_sender,
            move || readiness_for_failure.store(false, Ordering::Release),
        );

        assert!(!readiness.load(Ordering::Acquire));
        assert_eq!(shutdown_receiver.try_recv(), Ok(()));
        let error = lock_unpoisoned(&accept_failure)
            .take()
            .expect("terminal listener error is retained");
        assert_eq!(error.to_string(), "listener stopped");
    }

    #[test]
    fn committed_batch_must_match_the_selected_stream_record_format() {
        assert!(validate_batch_record_format(1, 1).is_ok());
        assert!(validate_batch_record_format(2, 2).is_ok());

        for (batch_format, selected_format) in [(1, 2), (2, 1)] {
            let error = validate_batch_record_format(batch_format, selected_format)
                .expect_err("a stream cannot change record format after Welcome");
            assert_eq!(error.code(), tonic::Code::InvalidArgument);
            assert_eq!(
                error.message(),
                "committed group record format differs from selected Welcome"
            );
        }
    }
}
