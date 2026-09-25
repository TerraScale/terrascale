//! Bounded blocking document caller shared by local-v1 and signed-v2 HTTPS.
//!
//! The service stays on one named OS thread. Signed-v2 shares its recovered
//! distributed owner with replication instead of reopening the shard.
//! Async handlers submit into a bounded channel and never hold a storage lock
//! across an await point.

use std::io;
use std::path::Path;
use std::sync::Arc;
use std::sync::mpsc::{self, SyncSender, TrySendError};
use std::thread::{self, JoinHandle};

use axum::http::StatusCode;
use metrics_exporter_prometheus::PrometheusHandle;
use tb_core::{
    Attribution, CollectionId, DocumentId, MemoryBudget, Reservation, TransactionId,
    WorkReservation,
};
use tb_document::{DocumentRead, DocumentStoreConfig, DocumentWrite, LocalSchemaBinding};
use tb_format::FormatError;
use tb_storage::LocalDocumentMode;
use tokio::net::TcpListener;
use tokio::sync::{oneshot, watch};
use tokio::task::JoinHandle as TokioJoinHandle;

use crate::LocalDurableCommit;
use crate::client_auth::{
    AuthError, AuthenticatedKey, ClientPermission, ClientTarget, KeyAdmission,
};
use crate::client_http::{ClientAdmissionError, ClientAdmissions};
use crate::client_json::{self, ClientWireError, ValidatedCommit};
use crate::distributed_runtime::DocumentMemberRuntime;
use crate::document_service::{DistributedDocumentService, signed_active_scope};
use crate::member_control::{bind_member_control, serve_member_control};
use crate::{CgroupMemoryMonitor, ReplicationConfig, ReplicationServer};
use crate::{
    ClientConfig, ClientGeneration, DocumentServiceError, LocalServerState, LocalV1DocumentService,
    LocalV1Scope, ServerConfig, load_client_config, load_local_v1_config, serve, serve_client,
};

const MAX_WORK_QUEUE: usize = 4;
const MAX_CLIENT_REQUEST_BYTES: usize = 4 * 1024 * 1024;
const MAX_CLIENT_RESPONSE_BYTES: usize = 4 * 1024 * 1024;
const REQUEST_OVERHEAD_BYTES: usize = 256 * 1024;
const RESPONSE_SCRATCH_BYTES: usize = 64 * 1024;

const INVALID_REQUEST_BODY: &[u8] = br#"{"error":{"code":"invalid_request"}}"#;
const INVALID_KEY_BODY: &[u8] = br#"{"error":{"code":"invalid_key"}}"#;
const FORBIDDEN_SCOPE_BODY: &[u8] = br#"{"error":{"code":"forbidden_scope"}}"#;
const RATE_LIMITED_BODY: &[u8] = br#"{"error":{"code":"rate_limited"}}"#;
const SCHEMA_CHANGED_BODY: &[u8] = br#"{"error":{"code":"schema_changed"}}"#;
const IDEMPOTENCY_CONFLICT_BODY: &[u8] = br#"{"error":{"code":"idempotency_conflict"}}"#;
const IDEMPOTENCY_EXPIRED_BODY: &[u8] = br#"{"error":{"code":"idempotency_expired"}}"#;
const CAPACITY_BODY: &[u8] = br#"{"error":{"code":"capacity"}}"#;
const COMMIT_UNCERTAIN_BODY: &[u8] = br#"{"error":{"code":"commit_uncertain"}}"#;

#[cfg(unix)]
type LocalV1ReloadSignal = tokio::signal::unix::Signal;
#[cfg(not(unix))]
type LocalV1ReloadSignal = ();

struct RegisteredLocalV1Signals {
    shutdown: LocalV1ShutdownSignals,
    reload: LocalV1ReloadSignal,
}

struct LocalV1RuntimeSetup {
    client_config: ClientConfig,
    client_listener: TcpListener,
    scope: LocalV1Scope,
    budget: MemoryBudget,
    attribution: Attribution,
    admissions: Arc<ClientAdmissions>,
    server: Arc<LocalServerState>,
    worker: DocumentWorker,
    handshake_timeout: std::time::Duration,
}

struct ClientReloadContext {
    config: ClientConfig,
    scope: LocalV1Scope,
    budget: MemoryBudget,
    attribution: Attribution,
    admissions: Arc<ClientAdmissions>,
    server: Arc<LocalServerState>,
}

#[cfg(unix)]
struct LocalV1ShutdownSignals {
    interrupt: tokio::signal::unix::Signal,
    terminate: tokio::signal::unix::Signal,
}

#[cfg(not(unix))]
struct LocalV1ShutdownSignals {
    interrupt: tokio::signal::windows::CtrlC,
}

/// Register process signals synchronously before any local-v1 readiness or
/// listener exposure. Tokio's Unix signal constructor installs the receiver
/// immediately; constructing the async `ctrl_c()` future does not.
fn register_local_v1_signals() -> Result<RegisteredLocalV1Signals, io::Error> {
    #[cfg(unix)]
    {
        use tokio::signal::unix::{SignalKind, signal};

        Ok(RegisteredLocalV1Signals {
            shutdown: LocalV1ShutdownSignals {
                interrupt: signal(SignalKind::interrupt())?,
                terminate: signal(SignalKind::terminate())?,
            },
            reload: signal(SignalKind::hangup())?,
        })
    }
    #[cfg(not(unix))]
    {
        Ok(RegisteredLocalV1Signals {
            shutdown: LocalV1ShutdownSignals {
                interrupt: tokio::signal::windows::ctrl_c()?,
            },
            reload: (),
        })
    }
}

/// Packaged binary entry seam for the explicit local-v1 mode. Config parsing,
/// listener binding, TLS/auth validation, marker/schema preflight, and worker
/// startup all complete in that order before readiness is opened.
#[doc(hidden)]
pub async fn run_local_v1_process(
    server_config: ServerConfig,
    admin_listener: TcpListener,
    local_config_path: &Path,
    client_config_path: &Path,
    allow_nonloopback: bool,
    prometheus: PrometheusHandle,
) -> Result<(), io::Error> {
    let RegisteredLocalV1Signals {
        shutdown: shutdown_signals,
        reload: reload_signal,
    } = register_local_v1_signals()?;
    let LocalV1RuntimeSetup {
        client_config,
        client_listener,
        scope,
        budget,
        attribution,
        admissions,
        server,
        worker,
        handshake_timeout,
    } = prepare_local_v1_runtime(
        server_config,
        local_config_path,
        client_config_path,
        allow_nonloopback,
    )
    .await?;

    let address = admin_listener.local_addr()?;
    let client_address = client_listener.local_addr()?;
    eprintln!("terrabase-server local-v1 ready on http://{address}");
    eprintln!("terrabase-server client listener on https://{client_address}");

    let (admin_shutdown_sender, admin_shutdown_receiver) = oneshot::channel();
    let (client_shutdown_sender, client_shutdown_receiver) = oneshot::channel();
    let (reload_stop_sender, reload_stop_receiver) = watch::channel(false);
    let signal_state = Arc::clone(&server);
    let signal_task = tokio::spawn(async move {
        wait_for_local_v1_shutdown_signal(shutdown_signals).await;
        signal_state.begin_draining();
        let _ = admin_shutdown_sender.send(());
        let _ = client_shutdown_sender.send(());
        reload_stop_sender.send_modify(|stopped| *stopped = true);
    });
    let reload_task = spawn_client_reload_task(
        ClientReloadContext {
            config: client_config,
            scope,
            budget,
            attribution,
            admissions: Arc::clone(&admissions),
            server: Arc::clone(&server),
        },
        reload_signal,
        reload_stop_receiver,
    );

    let admin = async {
        serve(
            admin_listener,
            Arc::clone(&server),
            prometheus,
            admin_shutdown_receiver,
        )
        .await
        .map_err(io::Error::other)
    };
    let client_worker = worker.handle();
    let client_admissions = Arc::clone(&admissions);
    let client = async move {
        serve_client(
            client_listener,
            client_admissions,
            client_worker,
            handshake_timeout,
            client_shutdown_receiver,
        )
        .await
        .map_err(io::Error::other)
    };
    let result = tokio::try_join!(admin, client).map(|_| ());

    signal_task.abort();
    reload_task.abort();
    server.begin_draining();
    admissions.close();
    let worker_result = tokio::task::spawn_blocking(move || worker.shutdown())
        .await
        .map_err(io::Error::other)?
        .map_err(io::Error::other);
    server.mark_stopped();
    worker_result?;
    result
}

/// Run the signed-v2 HTTPS caller, mTLS receiver and outbound sender over one
/// recovered document owner and one process memory budget.
#[doc(hidden)]
#[allow(clippy::too_many_arguments)]
#[allow(
    clippy::too_many_lines,
    reason = "the signed-v2 process lifecycle keeps startup, readiness and shutdown ordering together"
)]
pub async fn run_distributed_process(
    server_config: ServerConfig,
    admin_listener: TcpListener,
    replication_config: ReplicationConfig,
    client_config_path: &Path,
    allow_nonloopback: bool,
    member_control_socket: Option<&Path>,
    prometheus: PrometheusHandle,
) -> Result<(), io::Error> {
    if replication_config.outbound_peer().is_none() {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "signed-v2 HTTPS requires the outbound replication peer triple",
        ));
    }
    let RegisteredLocalV1Signals {
        shutdown: shutdown_signals,
        reload: reload_signal,
    } = register_local_v1_signals()?;
    let member_control_listener = member_control_socket.map(bind_member_control).transpose()?;
    let replication_listener = TcpListener::bind(replication_config.listen_addr).await?;
    let client_config = load_client_config(client_config_path).map_err(io::Error::other)?;
    client_config
        .validate_bind(allow_nonloopback)
        .map_err(io::Error::other)?;
    let client_listener = TcpListener::bind(client_config.listen_addr()).await?;
    let budget = server_config
        .initialize_memory_budget()
        .map_err(io::Error::other)?;
    let monitor = CgroupMemoryMonitor::open(replication_config.memory_profile)
        .await
        .map_err(io::Error::other)?;
    let (stop_sender, stop_receiver) = watch::channel(false);
    let sampler_task = monitor.spawn_sampler(stop_receiver.clone());
    let replication_budget = budget.clone();
    let replication_server = tokio::task::spawn_blocking(move || {
        ReplicationServer::open(&replication_config, replication_budget, monitor)
    })
    .await
    .map_err(io::Error::other)?
    .map_err(io::Error::other)?;
    let document = replication_server.document_runtime().ok_or_else(|| {
        io::Error::new(
            io::ErrorKind::InvalidData,
            "signed-v2 authority did not recover a document owner",
        )
    })?;
    let scope = signed_active_scope(&document).map_err(io::Error::other)?;
    let attribution = ServerConfig::local_v1_attribution(document.authority.view().tenant());
    let generation = load_client_generation(
        client_config.clone(),
        scope.clone(),
        budget.clone(),
        attribution,
    )
    .await?;
    let admissions = Arc::new(ClientAdmissions::new(generation));
    let server = Arc::new(
        LocalServerState::open_document_member_with_admissions(
            budget.clone(),
            Some(Arc::clone(&admissions)),
        )
        .map_err(io::Error::other)?,
    );
    let worker = DocumentWorker::start_distributed(
        document,
        budget.clone(),
        attribution,
        scope.clone(),
        Arc::clone(&admissions),
        Arc::clone(&server),
    )
    .await
    .map_err(io::Error::other)?;
    let member_identity = replication_server.member_control_identity();
    let admin_address = admin_listener.local_addr()?;
    let client_address = client_listener.local_addr()?;
    eprintln!("terrabase-server starting on http://{admin_address}");
    eprintln!("terrabase-server client listener on https://{client_address}");

    let signal_state = Arc::clone(&server);
    let signal_stop = stop_sender.clone();
    let signal_task = tokio::spawn(async move {
        wait_for_local_v1_shutdown_signal(shutdown_signals).await;
        signal_state.begin_draining();
        signal_stop.send_modify(|stopped| *stopped = true);
    });
    let reload_task = spawn_client_reload_task(
        ClientReloadContext {
            config: client_config.clone(),
            scope,
            budget,
            attribution,
            admissions: Arc::clone(&admissions),
            server: Arc::clone(&server),
        },
        reload_signal,
        stop_receiver.clone(),
    );
    let (admin_stop_sender, admin_stop_receiver) = oneshot::channel();
    let (client_stop_sender, client_stop_receiver) = oneshot::channel();
    let bridge = async {
        let mut stop = stop_receiver.clone();
        while !*stop.borrow() && stop.changed().await.is_ok() {}
        let _ = admin_stop_sender.send(());
        let _ = client_stop_sender.send(());
    };
    let admin_state = Arc::clone(&server);
    let admin_stop = stop_sender.clone();
    let admin = async {
        let result = serve(admin_listener, admin_state, prometheus, admin_stop_receiver)
            .await
            .map_err(io::Error::other);
        if result.is_err() || !*admin_stop.borrow() {
            admin_stop.send_modify(|stopped| *stopped = true);
        }
        result
    };
    let client_admissions = Arc::clone(&admissions);
    let client_stop = stop_sender.clone();
    let client_worker = worker.handle();
    let client = async {
        let result = serve_client(
            client_listener,
            client_admissions,
            client_worker,
            client_config.handshake_timeout(),
            client_stop_receiver,
        )
        .await
        .map_err(io::Error::other);
        if result.is_err() || !*client_stop.borrow() {
            client_stop.send_modify(|stopped| *stopped = true);
        }
        result
    };
    let (ready_sender, ready_receiver) = oneshot::channel();
    let ready_state = Arc::clone(&server);
    let readiness = async move {
        if ready_receiver.await.is_ok() {
            ready_state.mark_replication_ready();
            if ready_state.is_ready() {
                eprintln!("terrabase-server ready on http://{admin_address}");
            }
        }
    };
    let replication_state = Arc::clone(&server);
    let replication_stop = stop_sender.clone();
    let replication_shutdown = stop_receiver.clone();
    let replication = async {
        let result = replication_server
            .serve(
                replication_listener,
                replication_shutdown,
                ready_sender,
                Arc::clone(&replication_state),
            )
            .await
            .map_err(io::Error::other);
        if result.is_err() || !*replication_stop.borrow() {
            replication_state.mark_failed();
            replication_stop.send_modify(|stopped| *stopped = true);
        }
        result
    };
    let member_state = Arc::clone(&server);
    let member_stop = stop_sender.clone();
    let member_control = async {
        let result = if let Some(listener) = member_control_listener {
            serve_member_control(
                listener,
                member_identity,
                member_state,
                stop_receiver.clone(),
            )
            .await
        } else {
            let mut stop = stop_receiver.clone();
            while !*stop.borrow() && stop.changed().await.is_ok() {}
            Ok(())
        };
        if result.is_err() || !*member_stop.borrow() {
            member_stop.send_modify(|stopped| *stopped = true);
        }
        result
    };
    let (admin_result, client_result, replication_result, member_result, (), ()) = tokio::join!(
        admin,
        client,
        replication,
        member_control,
        readiness,
        bridge
    );
    signal_task.abort();
    reload_task.abort();
    sampler_task.abort();
    server.begin_draining();
    admissions.close();
    let worker_result = tokio::task::spawn_blocking(move || worker.shutdown())
        .await
        .map_err(io::Error::other)?
        .map_err(io::Error::other);
    server.mark_stopped();
    replication_result?;
    client_result?;
    admin_result?;
    member_result?;
    worker_result
}

async fn prepare_local_v1_runtime(
    server_config: ServerConfig,
    local_config_path: &Path,
    client_config_path: &Path,
    allow_nonloopback: bool,
) -> Result<LocalV1RuntimeSetup, io::Error> {
    let local_config = load_local_v1_config(local_config_path).map_err(io::Error::other)?;
    let client_config = load_client_config(client_config_path).map_err(io::Error::other)?;
    client_config
        .validate_bind(allow_nonloopback)
        .map_err(io::Error::other)?;
    let client_listener = TcpListener::bind(client_config.listen_addr()).await?;

    let scope = LocalV1Scope::configured(
        local_config.tenant,
        local_config.database,
        local_config.shard,
        local_config
            .collections
            .iter()
            .map(|binding| binding.collection),
    )
    .map_err(io::Error::other)?;
    let budget = server_config
        .initialize_memory_budget()
        .map_err(io::Error::other)?;
    let attribution = ServerConfig::local_v1_attribution(local_config.tenant);
    let initial_generation = load_client_generation(
        client_config.clone(),
        scope.clone(),
        budget.clone(),
        attribution,
    )
    .await?;
    let admissions = Arc::new(ClientAdmissions::new(initial_generation));
    let server = Arc::new(LocalServerState::starting_local_v1(
        budget.clone(),
        Arc::clone(&admissions),
    ));
    let worker = DocumentWorker::start(
        local_config.document_store_config(&server_config),
        budget.clone(),
        attribution,
        scope.clone(),
        local_config.mode(),
        local_config.collections.clone(),
        Arc::clone(&admissions),
        Arc::clone(&server),
    )
    .await
    .map_err(io::Error::other)?;
    server.mark_ready().map_err(io::Error::other)?;
    let handshake_timeout = client_config.handshake_timeout();

    Ok(LocalV1RuntimeSetup {
        client_config,
        client_listener,
        scope,
        budget,
        attribution,
        admissions,
        server,
        worker,
        handshake_timeout,
    })
}

async fn wait_for_local_v1_shutdown_signal(mut signals: LocalV1ShutdownSignals) {
    #[cfg(unix)]
    {
        tokio::select! {
            _ = signals.interrupt.recv() => {}
            _ = signals.terminate.recv() => {}
        }
    }
    #[cfg(not(unix))]
    signals.interrupt.recv().await;
}

async fn load_client_generation(
    config: ClientConfig,
    scope: LocalV1Scope,
    budget: MemoryBudget,
    attribution: Attribution,
) -> Result<ClientGeneration, io::Error> {
    tokio::task::spawn_blocking(move || {
        ClientGeneration::load(
            config.certificate_chain(),
            config.private_key(),
            config.api_key_registry(),
            &scope,
            &budget,
            attribution,
        )
    })
    .await
    .map_err(io::Error::other)?
    .map_err(io::Error::other)
}

fn spawn_client_reload_task(
    context: ClientReloadContext,
    mut signal: LocalV1ReloadSignal,
    mut stop: watch::Receiver<bool>,
) -> TokioJoinHandle<()> {
    tokio::spawn(async move {
        let ClientReloadContext {
            config,
            scope,
            budget,
            attribution,
            admissions,
            server,
        } = context;
        #[cfg(unix)]
        {
            loop {
                tokio::select! {
                    changed = stop.changed() => {
                        if changed.is_err() || *stop.borrow() {
                            break;
                        }
                    }
                    received = signal.recv() => {
                        if received.is_none() {
                            break;
                        }
                        let reload = load_client_generation(
                            config.clone(),
                            scope.clone(),
                            budget.clone(),
                            attribution,
                        );
                        let installed = match reload.await {
                            Ok(generation) => server
                                .with_document_available(|| admissions.install(generation))
                                .is_some(),
                            Err(_) => false,
                        };
                        if installed {
                            eprintln!("terrabase-server: client credentials reloaded");
                        } else {
                            admissions.close();
                            eprintln!("terrabase-server: client reload rejected; admissions closed");
                        }
                    }
                }
            }
        }
        #[cfg(not(unix))]
        {
            let _ = (
                config,
                scope,
                budget,
                attribution,
                admissions,
                server,
                signal,
                &mut stop,
            );
        }
    })
}

/// One operation admitted by the authenticated HTTP adapter.
pub(crate) enum WorkerOperation {
    PointRead {
        collection: CollectionId,
        document: DocumentId,
    },
    Commit(ValidatedCommit),
}

/// Request state transferred into the bounded worker queue. It contains no
/// bearer secret and retains every memory and key permit through the response.
pub(crate) struct AuthorizedDocumentRequest {
    pub(crate) key: AuthenticatedKey,
    pub(crate) target: ClientTarget,
    pub(crate) admission: KeyAdmission,
    pub(crate) request_reservation: WorkReservation,
    pub(crate) decode_reservation: Option<Reservation>,
    pub(crate) operation: WorkerOperation,
}

/// Async facade used by the TLS/HTTP adapter.
#[derive(Clone)]
pub(crate) struct DocumentWorkerHandle {
    sender: SyncSender<WorkerCommand>,
    budget: MemoryBudget,
    attribution: Attribution,
    // Keep the admission generation alive for every cloned worker handle.
    _admissions: Arc<ClientAdmissions>,
    server: Arc<LocalServerState>,
}

impl DocumentWorkerHandle {
    /// Reserve the bounded raw request buffer and fixed parser/descriptor
    /// overhead before the HTTP adapter collects a body.
    pub(crate) fn try_reserve_request(
        &self,
        declared_bytes: usize,
    ) -> Result<WorkReservation, WorkerCapacityError> {
        let bytes = request_memory_bound(declared_bytes).ok_or(WorkerCapacityError)?;
        self.budget
            .try_reserve_work(self.attribution, bytes, 1)
            .map_err(|_| WorkerCapacityError)
    }

    /// Reserve Format's documented additional owned decoder bound before the
    /// typed request decoder allocates parser and canonical-value storage.
    pub(crate) fn try_reserve_decode(
        &self,
        input_bytes: usize,
    ) -> Result<Reservation, WorkerCapacityError> {
        let bytes = client_json::decode_memory_bound(input_bytes).ok_or(WorkerCapacityError)?;
        self.budget
            .try_reserve(self.attribution, bytes)
            .map_err(|_| WorkerCapacityError)
    }

    /// Submit one already authenticated operation without waiting for queue
    /// capacity. Queue saturation returns a bounded 503 and retains request
    /// permits through that response write.
    pub(crate) async fn execute(
        &self,
        request: AuthorizedDocumentRequest,
    ) -> Result<PreparedClientResponse, WorkerExecuteError> {
        let (sender, receiver) = oneshot::channel();
        let command = WorkerCommand::Execute { request, sender };
        match try_submit_worker_command(&self.sender, command, &self.server) {
            Ok(()) => receive_worker_response(receiver, &self.server).await,
            Err(WorkerSubmitError::Full(command) | WorkerSubmitError::Disconnected(command)) => {
                let WorkerCommand::Execute { request, sender } = command else {
                    unreachable!("only execute commands are submitted through the request handle")
                };
                drop(sender);
                Ok(PreparedClientResponse::error(
                    StatusCode::SERVICE_UNAVAILABLE,
                    CAPACITY_BODY,
                    request,
                    None,
                ))
            }
        }
    }
}

enum WorkerSubmitError<T> {
    Full(T),
    Disconnected(T),
}

fn request_memory_bound(declared_bytes: usize) -> Option<usize> {
    if declared_bytes > MAX_CLIENT_REQUEST_BYTES {
        return None;
    }
    declared_bytes
        .checked_mul(2)?
        .checked_add(REQUEST_OVERHEAD_BYTES)
}

fn try_submit_worker_command<T>(
    sender: &SyncSender<T>,
    command: T,
    server: &LocalServerState,
) -> Result<(), WorkerSubmitError<T>> {
    match sender.try_send(command) {
        Ok(()) => Ok(()),
        Err(TrySendError::Full(command)) => Err(WorkerSubmitError::Full(command)),
        Err(TrySendError::Disconnected(command)) => {
            server.clear_document_readiness();
            Err(WorkerSubmitError::Disconnected(command))
        }
    }
}

async fn receive_worker_response(
    receiver: oneshot::Receiver<PreparedClientResponse>,
    server: &LocalServerState,
) -> Result<PreparedClientResponse, WorkerExecuteError> {
    receiver.await.map_err(|_| {
        server.clear_document_readiness();
        WorkerExecuteError
    })
}

#[expect(
    clippy::large_enum_variant,
    reason = "boxing the bounded commit request would add an allocation outside its existing memory reservation"
)]
enum WorkerCommand {
    Execute {
        request: AuthorizedDocumentRequest,
        sender: oneshot::Sender<PreparedClientResponse>,
    },
    Stop,
}

/// Thread owner plus a cloneable request handle. It is shut down only after
/// both listeners have drained their accepted handlers.
pub(crate) struct DocumentWorker {
    handle: DocumentWorkerHandle,
    thread: Option<JoinHandle<()>>,
}

trait DocumentOperations {
    fn scope(&self) -> &LocalV1Scope;
    fn point_read(
        &self,
        collection: CollectionId,
        document: DocumentId,
    ) -> Result<DocumentRead, DocumentServiceError>;
    fn commit_documents(
        &mut self,
        transaction_id: TransactionId,
        writes: &[DocumentWrite<'_>],
    ) -> Result<LocalDurableCommit, DocumentServiceError>;
}

impl DocumentOperations for LocalV1DocumentService {
    fn scope(&self) -> &LocalV1Scope {
        self.scope()
    }
    fn point_read(
        &self,
        collection: CollectionId,
        document: DocumentId,
    ) -> Result<DocumentRead, DocumentServiceError> {
        self.point_read(collection, document)
    }
    fn commit_documents(
        &mut self,
        transaction_id: TransactionId,
        writes: &[DocumentWrite<'_>],
    ) -> Result<LocalDurableCommit, DocumentServiceError> {
        self.commit_documents(transaction_id, writes)
    }
}

impl DocumentOperations for DistributedDocumentService {
    fn scope(&self) -> &LocalV1Scope {
        self.scope()
    }
    fn point_read(
        &self,
        collection: CollectionId,
        document: DocumentId,
    ) -> Result<DocumentRead, DocumentServiceError> {
        self.point_read(collection, document)
    }
    fn commit_documents(
        &mut self,
        transaction_id: TransactionId,
        writes: &[DocumentWrite<'_>],
    ) -> Result<LocalDurableCommit, DocumentServiceError> {
        DistributedDocumentService::commit_documents(self, transaction_id, writes)
    }
}

impl DocumentWorker {
    /// Start the owner thread and wait until strict local marker/catalog open
    /// completes before returning a request handle.
    #[allow(clippy::too_many_arguments)]
    pub(crate) async fn start(
        config: DocumentStoreConfig,
        budget: MemoryBudget,
        attribution: Attribution,
        scope: LocalV1Scope,
        mode: LocalDocumentMode,
        expected: Vec<LocalSchemaBinding>,
        admissions: Arc<ClientAdmissions>,
        server: Arc<LocalServerState>,
    ) -> Result<Self, WorkerStartError> {
        Self::start_with(
            budget,
            attribution,
            admissions,
            server,
            move |thread_budget| {
                LocalV1DocumentService::open(config, thread_budget, scope, mode, &expected)
                    .map(|service| Box::new(service) as Box<dyn DocumentOperations>)
            },
        )
        .await
    }

    pub(crate) async fn start_distributed(
        document: Arc<DocumentMemberRuntime>,
        budget: MemoryBudget,
        attribution: Attribution,
        scope: LocalV1Scope,
        admissions: Arc<ClientAdmissions>,
        server: Arc<LocalServerState>,
    ) -> Result<Self, WorkerStartError> {
        Self::start_with(budget, attribution, admissions, server, move |_| {
            Ok(Box::new(DistributedDocumentService::new(scope, document))
                as Box<dyn DocumentOperations>)
        })
        .await
    }

    async fn start_with(
        budget: MemoryBudget,
        attribution: Attribution,
        admissions: Arc<ClientAdmissions>,
        server: Arc<LocalServerState>,
        open: impl FnOnce(MemoryBudget) -> Result<Box<dyn DocumentOperations>, DocumentServiceError>
        + Send
        + 'static,
    ) -> Result<Self, WorkerStartError> {
        let (sender, receiver) = mpsc::sync_channel(MAX_WORK_QUEUE);
        let (started_sender, started_receiver) = oneshot::channel();
        let thread_budget = budget.clone();
        let thread_attribution = attribution;
        let thread_admissions = Arc::clone(&admissions);
        let thread_server = Arc::clone(&server);
        let thread_failure_state = Arc::clone(&server);
        let thread = thread::Builder::new()
            .name("terrabase-local-v1-document".into())
            .spawn(move || {
                run_worker_with_fatal_detection(&thread_failure_state, move || {
                    let service = match open(thread_budget.clone()) {
                        Ok(service) => service,
                        Err(error) => {
                            let _ = started_sender.send(Err(error));
                            return;
                        }
                    };
                    if started_sender.send(Ok(())).is_err() {
                        return;
                    }
                    run_worker(
                        receiver,
                        service,
                        thread_budget,
                        thread_attribution,
                        thread_admissions,
                        thread_server,
                    );
                });
            })
            .map_err(WorkerStartError::Thread)?;

        match started_receiver.await {
            Ok(Ok(())) => Ok(Self {
                handle: DocumentWorkerHandle {
                    sender,
                    budget,
                    attribution,
                    _admissions: admissions,
                    server,
                },
                thread: Some(thread),
            }),
            Ok(Err(error)) => {
                let _ = thread.join();
                Err(WorkerStartError::Document(error))
            }
            Err(_) => {
                let _ = thread.join();
                Err(WorkerStartError::ExitedDuringStartup)
            }
        }
    }

    pub(crate) fn handle(&self) -> DocumentWorkerHandle {
        self.handle.clone()
    }

    /// Close the queue and join the one blocking owner thread.
    pub(crate) fn shutdown(self) -> Result<(), WorkerShutdownError> {
        let Self { handle, thread } = self;
        let _ = handle.sender.send(WorkerCommand::Stop);
        drop(handle);
        match thread
            .expect("worker thread remains owned until shutdown")
            .join()
        {
            Ok(()) => Ok(()),
            Err(_) => Err(WorkerShutdownError),
        }
    }
}

fn run_worker_with_fatal_detection(server: &LocalServerState, operation: impl FnOnce()) {
    if let Err(payload) = std::panic::catch_unwind(std::panic::AssertUnwindSafe(operation)) {
        server.clear_document_readiness();
        std::panic::resume_unwind(payload);
    }
}

#[expect(
    clippy::needless_pass_by_value,
    reason = "the dedicated worker loop owns its channel and shared runtime leases for the thread lifetime"
)]
fn run_worker(
    receiver: mpsc::Receiver<WorkerCommand>,
    mut service: Box<dyn DocumentOperations>,
    budget: MemoryBudget,
    attribution: Attribution,
    admissions: Arc<ClientAdmissions>,
    server: Arc<LocalServerState>,
) {
    while let Ok(command) = receiver.recv() {
        match command {
            WorkerCommand::Execute { request, sender } => {
                let response = execute_sync(
                    request,
                    service.as_mut(),
                    &budget,
                    attribution,
                    &admissions,
                    &server,
                );
                let _ = sender.send(response);
            }
            WorkerCommand::Stop => break,
        }
    }
}

fn execute_sync(
    request: AuthorizedDocumentRequest,
    service: &mut dyn DocumentOperations,
    budget: &MemoryBudget,
    attribution: Attribution,
    admissions: &ClientAdmissions,
    server: &LocalServerState,
) -> PreparedClientResponse {
    let AuthorizedDocumentRequest {
        key,
        target,
        admission,
        request_reservation,
        decode_reservation,
        operation,
    } = request;
    let (permission, collections) = match &operation {
        WorkerOperation::PointRead { collection, .. } => {
            (ClientPermission::Read, vec![*collection])
        }
        WorkerOperation::Commit(commit) => (
            ClientPermission::Write,
            commit.collections().collect::<Vec<_>>(),
        ),
    };
    if !service
        .scope()
        .allows_grant(target.tenant, target.database, target.shard, &collections)
    {
        return PreparedClientResponse::error_parts(
            StatusCode::FORBIDDEN,
            FORBIDDEN_SCOPE_BODY,
            request_parts(request_reservation, decode_reservation, admission),
            None,
        );
    }

    let output_bytes = MAX_CLIENT_RESPONSE_BYTES + RESPONSE_SCRATCH_BYTES;
    let Ok(output_reservation) = budget.try_reserve_work(attribution, output_bytes, 1) else {
        return PreparedClientResponse::error_parts(
            StatusCode::SERVICE_UNAVAILABLE,
            CAPACITY_BODY,
            request_parts(request_reservation, decode_reservation, admission),
            None,
        );
    };

    // Reauthorize at the queue-to-work cut. No admission lock survives this
    // call, so the following synchronous document operation cannot hold it.
    let current_admission =
        match admissions.reauthorize(key, target, permission, &collections, admission) {
            Ok(admission) => admission,
            Err(error) => {
                return PreparedClientResponse::admission_error(
                    error,
                    request_reservation,
                    decode_reservation,
                    Some(output_reservation),
                );
            }
        };

    let result = execute_worker_operation(service, operation);

    match result {
        Ok(bytes) => PreparedClientResponse::success(
            bytes,
            output_reservation,
            request_reservation,
            decode_reservation,
            current_admission,
        ),
        Err(ServiceOrWireError::Service(error)) => {
            let (status, _code, body) = service_error_response(&error);
            if closes_data_path(&error) {
                server.clear_document_readiness();
            }
            PreparedClientResponse::error_parts(
                status,
                body,
                request_parts(request_reservation, decode_reservation, current_admission),
                Some(output_reservation),
            )
        }
        Err(ServiceOrWireError::Wire(error)) => {
            let (status, _code, body) = wire_error_response(&error);
            if wire_error_is_stored_corruption(&error) {
                server.clear_document_readiness();
            }
            PreparedClientResponse::error_parts(
                status,
                body,
                request_parts(request_reservation, decode_reservation, current_admission),
                Some(output_reservation),
            )
        }
    }
}

fn execute_worker_operation(
    service: &mut dyn DocumentOperations,
    operation: WorkerOperation,
) -> Result<Vec<u8>, ServiceOrWireError> {
    match operation {
        WorkerOperation::PointRead {
            collection,
            document,
        } => service
            .point_read(collection, document)
            .map_err(ServiceOrWireError::Service)
            .and_then(|read| client_json::encode_point(&read).map_err(ServiceOrWireError::Wire)),
        WorkerOperation::Commit(commit) => {
            let transaction_id = commit.transaction_id();
            commit
                .with_writes(|writes| service.commit_documents(transaction_id, writes))
                .map_err(ServiceOrWireError::Wire)
                .and_then(|result| result.map_err(ServiceOrWireError::Service))
                .and_then(|commit| {
                    client_json::encode_local_commit(&commit).map_err(ServiceOrWireError::Wire)
                })
        }
    }
}

fn closes_data_path(error: &DocumentServiceError) -> bool {
    matches!(
        error,
        DocumentServiceError::UncertainCommit(_) | DocumentServiceError::Corrupt
    )
}

fn wire_error_is_stored_corruption(error: &ClientWireError) -> bool {
    match error {
        ClientWireError::InvalidStoredData(FormatError::Allocation)
        | ClientWireError::Validation(_)
        | ClientWireError::Capacity
        | ClientWireError::Serialization(_) => false,
        ClientWireError::InvalidStoredData(_) => true,
    }
}

// These small structs keep all request resources alive on every response path.
struct RequestParts {
    request: WorkReservation,
    decode: Option<Reservation>,
    admission: Option<KeyAdmission>,
}

fn request_parts(
    request: WorkReservation,
    decode: Option<Reservation>,
    admission: KeyAdmission,
) -> RequestParts {
    RequestParts {
        request,
        decode,
        admission: Some(admission),
    }
}

enum ServiceOrWireError {
    Service(DocumentServiceError),
    Wire(ClientWireError),
}

fn service_error_response(
    error: &DocumentServiceError,
) -> (StatusCode, &'static str, &'static [u8]) {
    match error {
        DocumentServiceError::CollectionOutOfScope | DocumentServiceError::ScopeBindingMismatch => {
            (
                StatusCode::FORBIDDEN,
                "forbidden_scope",
                FORBIDDEN_SCOPE_BODY,
            )
        }
        DocumentServiceError::SchemaChanged => {
            (StatusCode::CONFLICT, "schema_changed", SCHEMA_CHANGED_BODY)
        }
        DocumentServiceError::Validation => (
            StatusCode::BAD_REQUEST,
            "invalid_request",
            INVALID_REQUEST_BODY,
        ),
        DocumentServiceError::RetryConflict => (
            StatusCode::CONFLICT,
            "idempotency_conflict",
            IDEMPOTENCY_CONFLICT_BODY,
        ),
        DocumentServiceError::RetryExpired => (
            StatusCode::CONFLICT,
            "idempotency_expired",
            IDEMPOTENCY_EXPIRED_BODY,
        ),
        DocumentServiceError::UncertainCommit(_) => (
            StatusCode::SERVICE_UNAVAILABLE,
            "commit_uncertain",
            COMMIT_UNCERTAIN_BODY,
        ),
        DocumentServiceError::EmptyCollectionScope
        | DocumentServiceError::TooManyCollectionGrants
        | DocumentServiceError::DuplicateCollectionGrant
        | DocumentServiceError::Capacity
        | DocumentServiceError::Corrupt
        | DocumentServiceError::Document(_) => {
            (StatusCode::SERVICE_UNAVAILABLE, "capacity", CAPACITY_BODY)
        }
    }
}

fn wire_error_response(error: &ClientWireError) -> (StatusCode, &'static str, &'static [u8]) {
    match error {
        ClientWireError::Validation(_) => (
            StatusCode::BAD_REQUEST,
            "invalid_request",
            INVALID_REQUEST_BODY,
        ),
        ClientWireError::Capacity
        | ClientWireError::InvalidStoredData(_)
        | ClientWireError::Serialization(_) => {
            (StatusCode::SERVICE_UNAVAILABLE, "capacity", CAPACITY_BODY)
        }
    }
}

/// A response body and the complete lifetime of its request, output, and key
/// admissions. The HTTP adapter retains this until Hyper finishes the write.
pub(crate) struct PreparedClientResponse {
    status: StatusCode,
    bytes: Vec<u8>,
    lease: ResponseLease,
}

impl PreparedClientResponse {
    fn success(
        bytes: Vec<u8>,
        output: WorkReservation,
        request: WorkReservation,
        decode: Option<Reservation>,
        admission: KeyAdmission,
    ) -> Self {
        Self {
            status: StatusCode::OK,
            bytes,
            lease: ResponseLease {
                _output: Some(output),
                _request: request,
                _decode: decode,
                _admission: Some(admission),
            },
        }
    }

    fn error(
        status: StatusCode,
        body: &'static [u8],
        request: AuthorizedDocumentRequest,
        output: Option<WorkReservation>,
    ) -> Self {
        let AuthorizedDocumentRequest {
            admission,
            request_reservation,
            decode_reservation,
            ..
        } = request;
        Self::error_parts(
            status,
            body,
            request_parts(request_reservation, decode_reservation, admission),
            output,
        )
    }

    fn admission_error(
        error: ClientAdmissionError,
        request: WorkReservation,
        decode: Option<Reservation>,
        output: Option<WorkReservation>,
    ) -> Self {
        let (status, body) = match error {
            ClientAdmissionError::Closed => (StatusCode::SERVICE_UNAVAILABLE, CAPACITY_BODY),
            ClientAdmissionError::Auth(AuthError::InvalidKey) => {
                (StatusCode::UNAUTHORIZED, INVALID_KEY_BODY)
            }
            ClientAdmissionError::Auth(AuthError::ForbiddenScope) => {
                (StatusCode::FORBIDDEN, FORBIDDEN_SCOPE_BODY)
            }
            ClientAdmissionError::Auth(AuthError::RateLimited) => {
                (StatusCode::TOO_MANY_REQUESTS, RATE_LIMITED_BODY)
            }
        };
        Self::error_parts(
            status,
            body,
            RequestParts {
                request,
                decode,
                admission: None,
            },
            output,
        )
    }

    fn error_parts(
        status: StatusCode,
        body: &'static [u8],
        parts: RequestParts,
        output: Option<WorkReservation>,
    ) -> Self {
        Self {
            status,
            bytes: body.to_vec(),
            lease: ResponseLease {
                _output: output,
                _request: parts.request,
                _decode: parts.decode,
                _admission: parts.admission,
            },
        }
    }

    pub(crate) fn into_parts(self) -> (StatusCode, Vec<u8>, ResponseLease) {
        (self.status, self.bytes, self.lease)
    }
}

/// Memory and identity permits retained until the HTTP response is written.
pub(crate) struct ResponseLease {
    _output: Option<WorkReservation>,
    _request: WorkReservation,
    _decode: Option<Reservation>,
    _admission: Option<KeyAdmission>,
}

/// Failure to admit or reserve bounded worker work.
#[derive(Clone, Copy, Debug, Eq, PartialEq, thiserror::Error)]
#[error("local-v1 worker capacity is unavailable")]
pub(crate) struct WorkerCapacityError;

/// Worker queue stopped before it returned its response.
#[derive(Clone, Copy, Debug, Eq, PartialEq, thiserror::Error)]
#[error("local-v1 worker is unavailable")]
pub(crate) struct WorkerExecuteError;

/// Failure while creating or opening the strict local-v1 worker.
#[derive(Debug, thiserror::Error)]
pub(crate) enum WorkerStartError {
    #[error("could not start local-v1 blocking worker")]
    Thread(#[source] io::Error),
    #[error(transparent)]
    Document(#[from] DocumentServiceError),
    #[error("local-v1 worker exited during startup")]
    ExitedDuringStartup,
}

/// The local-v1 owner thread panicked instead of draining normally.
#[derive(Clone, Copy, Debug, Eq, PartialEq, thiserror::Error)]
#[error("local-v1 worker thread failed")]
pub(crate) struct WorkerShutdownError;

#[cfg(test)]
mod tests {
    use std::fs;
    use std::path::PathBuf;
    use std::sync::Barrier;
    use std::sync::atomic::{AtomicU64, Ordering};
    use std::time::{SystemTime, UNIX_EPOCH};

    use rcgen::{CertificateParams, KeyPair};
    use tb_core::{BudgetConfig, DatabaseId, ShardId, TenantId, WorkerId};

    use super::*;
    use tb_document::DocumentError;

    static NEXT_RUNTIME_DIRECTORY: AtomicU64 = AtomicU64::new(0);

    struct RuntimeDirectory(PathBuf);

    impl RuntimeDirectory {
        fn new() -> Self {
            let nanos = SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .expect("system time is after the Unix epoch")
                .as_nanos();
            let ordinal = NEXT_RUNTIME_DIRECTORY.fetch_add(1, Ordering::Relaxed);
            let path = std::env::temp_dir().join(format!(
                "terrabase-local-v1-runtime-{}-{nanos}-{ordinal}",
                std::process::id(),
            ));
            fs::create_dir_all(&path).expect("create runtime test directory");
            Self(path)
        }

        fn path(&self) -> &std::path::Path {
            &self.0
        }
    }

    impl Drop for RuntimeDirectory {
        fn drop(&mut self) {
            if self.0.exists() {
                fs::remove_dir_all(&self.0).expect("remove runtime test directory");
            }
        }
    }

    struct RuntimeFixture {
        _directory: RuntimeDirectory,
        certificate_path: PathBuf,
        private_key_path: PathBuf,
        registry_path: PathBuf,
        scope: LocalV1Scope,
        budget: MemoryBudget,
        attribution: Attribution,
        server: Arc<LocalServerState>,
        admissions: Arc<ClientAdmissions>,
    }

    impl RuntimeFixture {
        fn new() -> Self {
            let directory = RuntimeDirectory::new();
            let key = KeyPair::generate().expect("generate test TLS key");
            let certificate = CertificateParams::default()
                .self_signed(&key)
                .expect("self-sign test TLS certificate");
            let certificate_path = directory.path().join("client.pem");
            let private_key_path = directory.path().join("client-key.pem");
            let registry_path = directory.path().join("keys.json");
            fs::write(&certificate_path, certificate.pem()).expect("write test TLS certificate");
            fs::write(&private_key_path, key.serialize_pem()).expect("write test TLS key");
            fs::write(&registry_path, br#"{"version":1,"keys":[]}"#)
                .expect("write empty test key registry");
            #[cfg(unix)]
            {
                use std::os::unix::fs::PermissionsExt;
                fs::set_permissions(&private_key_path, fs::Permissions::from_mode(0o600))
                    .expect("protect test TLS key");
                fs::set_permissions(&registry_path, fs::Permissions::from_mode(0o600))
                    .expect("protect test key registry");
            }

            let tenant = TenantId::from_bytes([81; 16]);
            let budget = MemoryBudget::new(
                BudgetConfig::new(64 * 1024 * 1024, 8 * 1024 * 1024)
                    .expect("valid runtime test budget"),
            );
            let attribution = Attribution {
                tenant,
                worker: WorkerId(82),
                category: tb_core::MemoryCategory::RuntimeOverhead,
            };
            let scope = LocalV1Scope::configured(
                tenant,
                DatabaseId::from_bytes([83; 16]),
                ShardId::new(84),
                [CollectionId::from_bytes([85; 16])],
            )
            .expect("valid runtime test scope");
            let generation = ClientGeneration::load(
                &certificate_path,
                &private_key_path,
                &registry_path,
                &scope,
                &budget,
                attribution,
            )
            .expect("load test client generation");
            let admissions = Arc::new(ClientAdmissions::new(generation));
            let server = Arc::new(LocalServerState::starting_local_v1(
                budget.clone(),
                Arc::clone(&admissions),
            ));
            server.mark_ready().expect("mark test server ready");

            Self {
                _directory: directory,
                certificate_path,
                private_key_path,
                registry_path,
                scope,
                budget,
                attribution,
                server,
                admissions,
            }
        }

        fn replacement_generation(&self) -> ClientGeneration {
            ClientGeneration::load(
                &self.certificate_path,
                &self.private_key_path,
                &self.registry_path,
                &self.scope,
                &self.budget,
                self.attribution,
            )
            .expect("load replacement test client generation")
        }
    }

    #[test]
    fn data_readiness_closes_only_for_corruption_or_uncertain_durability() {
        assert!(closes_data_path(&DocumentServiceError::Corrupt));
        assert!(closes_data_path(&DocumentServiceError::UncertainCommit(
            Box::new(DocumentError::NeedsRecovery,)
        )));
        assert!(!closes_data_path(&DocumentServiceError::Capacity));
        assert!(!closes_data_path(&DocumentServiceError::Validation));
    }

    #[test]
    fn only_stored_format_corruption_closes_readiness_not_allocation_pressure() {
        assert!(wire_error_is_stored_corruption(
            &ClientWireError::InvalidStoredData(FormatError::Invalid {
                format: "stored document",
                reason: "bad binding",
            })
        ));
        assert!(!wire_error_is_stored_corruption(
            &ClientWireError::InvalidStoredData(FormatError::Allocation)
        ));
        assert!(!wire_error_is_stored_corruption(&ClientWireError::Capacity));
    }

    #[test]
    fn request_reservation_covers_live_frame_overlap_and_fixed_overhead() {
        assert_eq!(request_memory_bound(0), Some(REQUEST_OVERHEAD_BYTES));
        assert_eq!(
            request_memory_bound(MAX_CLIENT_REQUEST_BYTES),
            Some(MAX_CLIENT_REQUEST_BYTES * 2 + REQUEST_OVERHEAD_BYTES)
        );
        assert_eq!(request_memory_bound(MAX_CLIENT_REQUEST_BYTES + 1), None);
    }

    #[test]
    fn queue_full_stays_ready_but_disconnected_worker_closes_admissions() {
        let fixture = RuntimeFixture::new();
        let (full_sender, _full_receiver) = mpsc::sync_channel(1);
        full_sender
            .try_send(WorkerCommand::Stop)
            .expect("fill worker queue");
        assert!(matches!(
            try_submit_worker_command(&full_sender, WorkerCommand::Stop, &fixture.server),
            Err(WorkerSubmitError::Full(WorkerCommand::Stop))
        ));
        assert!(fixture.server.is_ready());
        assert!(fixture.admissions.is_open());

        let (dead_sender, dead_receiver) = mpsc::sync_channel::<WorkerCommand>(1);
        drop(dead_receiver);
        assert!(matches!(
            try_submit_worker_command(&dead_sender, WorkerCommand::Stop, &fixture.server),
            Err(WorkerSubmitError::Disconnected(WorkerCommand::Stop))
        ));
        assert!(!fixture.server.is_ready());
        assert!(!fixture.admissions.is_open());
    }

    #[tokio::test]
    async fn lost_worker_reply_closes_readiness_and_client_admissions() {
        let fixture = RuntimeFixture::new();
        let (sender, receiver) = oneshot::channel();
        drop(sender);

        assert!(
            receive_worker_response(receiver, &fixture.server)
                .await
                .is_err()
        );
        assert!(!fixture.server.is_ready());
        assert!(!fixture.admissions.is_open());
    }

    #[test]
    fn worker_panic_closes_readiness_before_the_thread_exits() {
        let fixture = RuntimeFixture::new();

        let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
            run_worker_with_fatal_detection(&fixture.server, || {
                panic!("simulated local-v1 worker failure");
            });
        }));

        assert!(result.is_err());
        assert!(!fixture.server.is_ready());
        assert!(!fixture.admissions.is_open());
    }

    #[test]
    fn fatal_document_close_wins_against_in_flight_client_reload() {
        let fixture = RuntimeFixture::new();
        let replacement = fixture.replacement_generation();
        let server = Arc::clone(&fixture.server);
        let admissions = Arc::clone(&fixture.admissions);
        let install_entered = Arc::new(Barrier::new(2));
        let allow_install = Arc::new(Barrier::new(2));
        let reload_server = Arc::clone(&server);
        let reload_admissions = Arc::clone(&admissions);
        let reload_entered = Arc::clone(&install_entered);
        let reload_release = Arc::clone(&allow_install);
        let reload = thread::spawn(move || {
            reload_server.with_document_available(|| {
                reload_entered.wait();
                reload_release.wait();
                reload_admissions.install(replacement);
            })
        });

        install_entered.wait();
        let (clear_started_sender, clear_started_receiver) = mpsc::channel();
        let clear_server = Arc::clone(&server);
        let clear = thread::spawn(move || {
            clear_started_sender
                .send(())
                .expect("report fatal-close attempt");
            clear_server.clear_document_readiness();
        });
        clear_started_receiver
            .recv()
            .expect("fatal-close attempt started");
        allow_install.wait();

        assert!(reload.join().expect("reload thread completes").is_some());
        clear.join().expect("fatal-close thread completes");
        assert!(server.with_document_available(|| ()).is_none());
        assert!(!admissions.is_open());
        assert!(!server.is_ready());
    }
}
