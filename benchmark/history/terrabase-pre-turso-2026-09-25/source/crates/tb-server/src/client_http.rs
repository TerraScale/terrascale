//! Bounded direct-HTTPS caller for the local-v1 document worker.
//!
//! The administrative HTTP router and member gRPC/LZ4 receiver are separate
//! listeners. This module accepts one HTTP/1.1 request per TLS connection.

use std::convert::Infallible;
use std::fs::{self, OpenOptions};
use std::io::{self, Read};
use std::net::SocketAddr;
use std::path::Path;
use std::sync::{Arc, Mutex, RwLock};
use std::time::Duration;

use axum::body::Body;
use axum::http::header::{CONTENT_ENCODING, CONTENT_LENGTH, CONTENT_TYPE, TRANSFER_ENCODING};
use axum::http::{HeaderMap, HeaderValue, Method, Request, Response, StatusCode, Version};
use futures_util::StreamExt;
use hyper::body::Incoming;
use hyper::server::conn::http1;
use hyper_util::rt::{TokioIo, TokioTimer};
use hyper_util::service::TowerToHyperService;
use rustls::pki_types::{CertificateDer, PrivateKeyDer, pem::PemObject};
use tb_core::{
    Attribution, CollectionId, DatabaseId, DocumentId, MemoryBudget, Reservation, ShardId,
    TenantId, WorkReservation,
};
use tokio::net::{TcpListener, TcpStream};
use tokio::sync::{OwnedSemaphorePermit, Semaphore, oneshot, watch};
use tokio::task::JoinSet;
use tokio_rustls::TlsAcceptor;
use tower::service_fn;
use zeroize::Zeroize;

use crate::client_auth::{
    AuthError, AuthLoadError, AuthRegistry, AuthenticatedKey, ClientPermission, ClientTarget,
    KeyAdmission,
};
use crate::client_json::{ClientWireError, ValidatedCommit, decode_commit};
use crate::{
    AuthorizedDocumentRequest, DocumentWorkerHandle, PreparedClientResponse, ResponseLease,
    WorkerOperation,
};

const MAX_CONNECTIONS: usize = 8;
const MAX_HANDLERS: usize = 4;
const MAX_HTTP_BUFFER_BYTES: usize = 16 * 1024;
const MAX_HTTP_HEADERS: usize = 32;
const MAX_JSON_BODY_BYTES: usize = 4 * 1024 * 1024;
const HEADER_TIMEOUT: Duration = Duration::from_secs(5);
const BODY_TIMEOUT: Duration = Duration::from_secs(10);
const CONNECTION_TIMEOUT: Duration = Duration::from_secs(30);
const DRAIN_TIMEOUT: Duration = Duration::from_secs(10);
const MAX_TLS_FILE_BYTES: usize = 2 * 1024 * 1024;
const MAX_CERTIFICATES: usize = 32;
const TLS_RESERVATION_BYTES: usize = 8 * 1024 * 1024;

/// One validated TLS/auth generation. Publication is atomic with respect to
/// new admissions and queue-to-worker reauthorization.
pub(crate) struct ClientGeneration {
    tls: Arc<rustls::ServerConfig>,
    auth: AuthRegistry,
    _tls_reservation: WorkReservation,
}

impl ClientGeneration {
    /// Validate the complete TLS/auth generation before it can be published.
    /// The same call is used before shard open and on SIGHUP reload.
    ///
    /// # Errors
    /// Rejects unreadable, unsafe, oversized, mismatched or invalid material,
    /// invalid key grants, and process-budget admission failure.
    pub(crate) fn load(
        certificate_path: &Path,
        private_key_path: &Path,
        registry_path: &Path,
        scope: &crate::LocalV1Scope,
        budget: &MemoryBudget,
        attribution: Attribution,
    ) -> Result<Self, ClientGenerationError> {
        let tls_reservation = budget
            .try_reserve_work(attribution, TLS_RESERVATION_BYTES, 1)
            .map_err(|_| ClientGenerationError::Capacity)?;
        let certificate_bytes = read_tls_file(certificate_path, false)?;
        let mut certificates: Vec<CertificateDer<'static>> = Vec::new();
        for certificate in CertificateDer::pem_slice_iter(&certificate_bytes) {
            if certificates.len() == MAX_CERTIFICATES {
                return Err(ClientGenerationError::InvalidTls);
            }
            certificates.push(certificate.map_err(|_| ClientGenerationError::InvalidTls)?);
        }
        if certificates.is_empty() {
            return Err(ClientGenerationError::InvalidTls);
        }
        let mut private_key_bytes = read_tls_file(private_key_path, true)?;
        let key_result = PrivateKeyDer::from_pem_slice(&private_key_bytes);
        private_key_bytes.zeroize();
        let private_key = key_result.map_err(|_| ClientGenerationError::InvalidTls)?;
        let mut tls = rustls::ServerConfig::builder_with_provider(Arc::new(
            rustls::crypto::aws_lc_rs::default_provider(),
        ))
        .with_protocol_versions(&[&rustls::version::TLS13])
        .map_err(|_| ClientGenerationError::InvalidTls)?
        .with_no_client_auth()
        .with_single_cert(certificates, private_key)
        .map_err(|_| ClientGenerationError::InvalidTls)?;
        tls.alpn_protocols = vec![b"http/1.1".to_vec()];
        let auth = AuthRegistry::load_generation(registry_path, scope, budget, attribution)?;
        Ok(Self {
            tls: Arc::new(tls),
            auth,
            _tls_reservation: tls_reservation,
        })
    }
}

fn read_tls_file(path: &Path, private: bool) -> Result<Vec<u8>, ClientGenerationError> {
    let path_metadata = fs::symlink_metadata(path).map_err(ClientGenerationError::Io)?;
    if !path_metadata.is_file() || path_metadata.len() > MAX_TLS_FILE_BYTES as u64 {
        return Err(ClientGenerationError::UnsafeTlsFile);
    }
    let mut options = OpenOptions::new();
    options.read(true);
    #[cfg(unix)]
    {
        use std::os::unix::fs::OpenOptionsExt;
        options.custom_flags(libc::O_NOFOLLOW);
    }
    let file = options.open(path).map_err(ClientGenerationError::Io)?;
    let metadata = file.metadata().map_err(ClientGenerationError::Io)?;
    if !metadata.is_file() || metadata.len() > MAX_TLS_FILE_BYTES as u64 {
        return Err(ClientGenerationError::UnsafeTlsFile);
    }
    #[cfg(unix)]
    {
        use std::os::unix::fs::{MetadataExt, PermissionsExt};
        if metadata.dev() != path_metadata.dev()
            || metadata.ino() != path_metadata.ino()
            || (private && metadata.permissions().mode() & 0o077 != 0)
        {
            return Err(ClientGenerationError::UnsafeTlsFile);
        }
    }
    let capacity =
        usize::try_from(metadata.len()).map_err(|_| ClientGenerationError::UnsafeTlsFile)?;
    let mut bytes = Vec::with_capacity(capacity);
    if let Err(error) = file
        .take(MAX_TLS_FILE_BYTES as u64 + 1)
        .read_to_end(&mut bytes)
    {
        if private {
            bytes.zeroize();
        }
        return Err(ClientGenerationError::Io(error));
    }
    if bytes.len() > MAX_TLS_FILE_BYTES {
        bytes.zeroize();
        return Err(ClientGenerationError::UnsafeTlsFile);
    }
    Ok(bytes)
}

#[derive(Debug, thiserror::Error)]
pub(crate) enum ClientGenerationError {
    #[error("cannot read client TLS material")]
    Io(#[source] io::Error),
    #[error("client TLS material is not a protected bounded regular file")]
    UnsafeTlsFile,
    #[error("client TLS certificate or private key is invalid")]
    InvalidTls,
    #[error("capacity unavailable for client TLS material")]
    Capacity,
    #[error(transparent)]
    Auth(#[from] AuthLoadError),
}

/// The sole current client admission gate. `None` fails closed after invalid
/// reload; every replacement also cancels sockets still in TLS handshake.
pub(crate) struct ClientAdmissions {
    current: RwLock<Option<Arc<ClientGeneration>>>,
    changed: watch::Sender<u64>,
}

impl ClientAdmissions {
    pub(crate) fn new(initial: ClientGeneration) -> Self {
        let (changed, _) = watch::channel(0);
        Self {
            current: RwLock::new(Some(Arc::new(initial))),
            changed,
        }
    }

    pub(crate) fn install(&self, generation: ClientGeneration) {
        let mut current = self
            .current
            .write()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        *current = Some(Arc::new(generation));
        self.changed
            .send_modify(|epoch| *epoch = epoch.wrapping_add(1));
    }

    pub(crate) fn close(&self) {
        let mut current = self
            .current
            .write()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        *current = None;
        self.changed
            .send_modify(|epoch| *epoch = epoch.wrapping_add(1));
    }

    #[must_use]
    pub(crate) fn is_open(&self) -> bool {
        self.current.read().is_ok_and(|current| current.is_some())
    }

    /// Authenticate and charge admission under one current-generation read.
    /// This is the pre-body admission cut; no lock survives the call.
    fn authenticate_and_authorize(
        &self,
        headers: &HeaderMap,
        target: ClientTarget,
        permission: ClientPermission,
        collections: &[CollectionId],
    ) -> Result<(AuthenticatedKey, KeyAdmission), ClientAdmissionError> {
        let current = self
            .current
            .read()
            .map_err(|_| ClientAdmissionError::Closed)?;
        let generation = current.as_ref().ok_or(ClientAdmissionError::Closed)?;
        let key = generation.auth.authenticate(headers)?;
        let admission = generation
            .auth
            .authorize(key, target, permission, collections)?;
        Ok((key, admission))
    }

    /// The queue-to-worker start cut. If the generation changed, the old
    /// permit is dropped and the current entry's rate and concurrency policy
    /// is charged before document work begins.
    ///
    /// # Errors
    /// Rejects a closed generation or the current entry's identity, grant,
    /// rate or concurrency limit; the previous permit is dropped on error.
    pub(crate) fn reauthorize(
        &self,
        key: AuthenticatedKey,
        target: ClientTarget,
        permission: ClientPermission,
        collections: &[CollectionId],
        previous: KeyAdmission,
    ) -> Result<KeyAdmission, ClientAdmissionError> {
        let current = self
            .current
            .read()
            .map_err(|_| ClientAdmissionError::Closed)?;
        let generation = current.as_ref().ok_or(ClientAdmissionError::Closed)?;
        generation
            .auth
            .reauthorize(key, target, permission, collections, previous)
            .map_err(ClientAdmissionError::from)
    }

    fn handshake_snapshot(&self) -> Option<(Arc<ClientGeneration>, watch::Receiver<u64>)> {
        let current = self.current.read().ok()?;
        let generation = current.as_ref()?;
        let changed = self.changed.subscribe();
        Some((Arc::clone(generation), changed))
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum ClientAdmissionError {
    Closed,
    Auth(AuthError),
}

impl From<AuthError> for ClientAdmissionError {
    fn from(error: AuthError) -> Self {
        Self::Auth(error)
    }
}

/// Serve a prebound TLS listener until shutdown. The caller validates the
/// concrete bind address and TLS 1.3 configuration before opening the shard.
///
/// # Errors
/// Returns invalid listener settings, accept failure or a panicked connection
/// task after closing client admission and draining accepted sockets.
pub(crate) async fn serve(
    listener: TcpListener,
    admissions: Arc<ClientAdmissions>,
    worker: DocumentWorkerHandle,
    handshake_timeout: Duration,
    mut shutdown: oneshot::Receiver<()>,
) -> Result<(), ClientHttpError> {
    if !(Duration::from_secs(1)..=Duration::from_secs(10)).contains(&handshake_timeout) {
        return Err(ClientHttpError::InvalidHandshakeTimeout);
    }
    let address = listener
        .local_addr()
        .map_err(ClientHttpError::ListenerAddress)?;
    if address.ip().is_unspecified() {
        return Err(ClientHttpError::WildcardListener(address));
    }
    let sockets = Arc::new(Semaphore::new(MAX_CONNECTIONS));
    let handlers = Arc::new(Semaphore::new(MAX_HANDLERS));
    let (stop_connections, stop_receiver) = watch::channel(false);
    let mut connections = JoinSet::new();
    let mut failure = None;

    'accept: loop {
        let socket = tokio::select! {
            biased;
            _ = &mut shutdown => break 'accept,
            completed = connections.join_next(), if !connections.is_empty() => {
                if let Some(Err(error)) = completed {
                    failure = Some(ClientHttpError::ConnectionTask(error));
                    break 'accept;
                }
                continue 'accept;
            }
            slot = Arc::clone(&sockets).acquire_owned() => {
                if let Ok(slot) = slot {
                    slot
                } else {
                    failure = Some(ClientHttpError::SocketSlotsClosed);
                    break 'accept;
                }
            }
        };
        let accepted = tokio::select! {
            biased;
            _ = &mut shutdown => {
                drop(socket);
                break 'accept;
            }
            accepted = listener.accept() => accepted,
        };
        match accepted {
            Ok((stream, _peer)) => {
                let admissions = Arc::clone(&admissions);
                let worker = worker.clone();
                let handlers = Arc::clone(&handlers);
                let stop = stop_receiver.clone();
                connections.spawn(async move {
                    serve_connection(
                        stream,
                        admissions,
                        worker,
                        handlers,
                        handshake_timeout,
                        socket,
                        stop,
                    )
                    .await;
                });
            }
            Err(error) if error.kind() == io::ErrorKind::Interrupted => drop(socket),
            Err(error) => {
                drop(socket);
                failure = Some(ClientHttpError::Accept(error));
                break 'accept;
            }
        }
    }

    admissions.close();
    stop_connections.send_modify(|stopped| *stopped = true);
    let drained = tokio::time::timeout(DRAIN_TIMEOUT, async {
        while let Some(completed) = connections.join_next().await {
            completed.map_err(ClientHttpError::ConnectionTask)?;
        }
        Ok::<_, ClientHttpError>(())
    })
    .await;
    let clean_drain = matches!(&drained, Ok(Ok(())));
    if let Ok(Err(error)) = drained
        && failure.is_none()
    {
        failure = Some(error);
    }
    if !clean_drain {
        connections.abort_all();
        while connections.join_next().await.is_some() {}
    }
    match failure {
        Some(error) => Err(error),
        None => Ok(()),
    }
}

async fn serve_connection(
    stream: TcpStream,
    admissions: Arc<ClientAdmissions>,
    worker: DocumentWorkerHandle,
    handlers: Arc<Semaphore>,
    handshake_timeout: Duration,
    _socket: OwnedSemaphorePermit,
    mut stop: watch::Receiver<bool>,
) {
    if *stop.borrow() {
        return;
    }
    // Retain the complete generation, including both budget reservations,
    // while its TLS config and accepted connection can still be in use.
    let Some((generation, mut changed)) = admissions.handshake_snapshot() else {
        return;
    };
    let acceptor = TlsAcceptor::from(Arc::clone(&generation.tls));
    let handshake = tokio::select! {
        biased;
        _ = stop.changed() => return,
        _ = changed.changed() => return,
        result = tokio::time::timeout(handshake_timeout, acceptor.accept(stream)) => result,
    };
    let Ok(Ok(stream)) = handshake else {
        metrics::counter!("terrabase_client_tls_handshakes_total", "result" => "rejected")
            .increment(1);
        return;
    };
    metrics::counter!("terrabase_client_tls_handshakes_total", "result" => "accepted").increment(1);

    let hold = Arc::new(Mutex::new(ConnectionHold::default()));
    let _hold_until_write = Arc::clone(&hold);
    let service = service_fn(move |request: Request<Incoming>| {
        let admissions = Arc::clone(&admissions);
        let worker = worker.clone();
        let handlers = Arc::clone(&handlers);
        let hold = Arc::clone(&hold);
        async move {
            let response = handle_request(request, admissions, worker, handlers, hold).await;
            Ok::<_, Infallible>(response)
        }
    });
    let mut builder = http1::Builder::new();
    builder
        .keep_alive(false)
        .max_buf_size(MAX_HTTP_BUFFER_BYTES)
        .max_headers(MAX_HTTP_HEADERS)
        .timer(TokioTimer::new())
        .header_read_timeout(HEADER_TIMEOUT);
    let connection =
        builder.serve_connection(TokioIo::new(stream), TowerToHyperService::new(service));
    if tokio::time::timeout(CONNECTION_TIMEOUT, connection)
        .await
        .is_err()
    {
        metrics::counter!("terrabase_client_connection_timeouts_total").increment(1);
    }
}

#[derive(Default)]
struct ConnectionHold {
    _handler: Option<OwnedSemaphorePermit>,
    key: Option<KeyAdmission>,
    request: Option<WorkReservation>,
    decode: Option<Reservation>,
    _response: Option<ResponseLease>,
}

impl ConnectionHold {
    #[expect(
        clippy::used_underscore_binding,
        reason = "the permit field is intentionally held until the connection state drops"
    )]
    fn retain_handler(&mut self, handler: OwnedSemaphorePermit) {
        self._handler = Some(handler);
    }

    #[expect(
        clippy::used_underscore_binding,
        reason = "the lease field is intentionally held through the response body write"
    )]
    fn retain_response(&mut self, response: ResponseLease) {
        self._response = Some(response);
    }
}

async fn handle_request(
    request: Request<Incoming>,
    admissions: Arc<ClientAdmissions>,
    worker: DocumentWorkerHandle,
    handlers: Arc<Semaphore>,
    hold: Arc<Mutex<ConnectionHold>>,
) -> Response<Body> {
    let Ok(handler) = handlers.try_acquire_owned() else {
        return recorded_error(StatusCode::SERVICE_UNAVAILABLE, "capacity");
    };
    hold.lock()
        .unwrap_or_else(std::sync::PoisonError::into_inner)
        .retain_handler(handler);
    if request.version() != Version::HTTP_11 {
        return recorded_error(StatusCode::BAD_REQUEST, "invalid_request");
    }

    let route = match parse_route(
        request.method(),
        request.uri().path(),
        request.uri().query(),
    ) {
        Ok(Some(route)) => route,
        Ok(None) => return recorded_error(StatusCode::NOT_FOUND, "unknown_route"),
        Err(()) => return recorded_error(StatusCode::BAD_REQUEST, "invalid_request"),
    };
    let (target, permission) = route.target_and_permission();
    let first_collection = route.first_collection();
    let admission = admissions.authenticate_and_authorize(
        request.headers(),
        target,
        permission,
        first_collection.as_slice(),
    );
    let (key, key_admission) = match admission {
        Ok(admission) => admission,
        Err(error) => return admission_error(error),
    };
    hold.lock()
        .unwrap_or_else(std::sync::PoisonError::into_inner)
        .key = Some(key_admission);

    if !valid_body_headers(request.headers(), &route) {
        return recorded_error(StatusCode::BAD_REQUEST, "invalid_request");
    }
    let operation = match route {
        Route::Point {
            collection,
            document,
            ..
        } => WorkerOperation::PointRead {
            collection,
            document,
        },
        Route::Commit { .. } => {
            return handle_commit_request(
                request, admissions, worker, hold, key, target, permission,
            )
            .await;
        }
    };
    let Ok(reservation) = worker.try_reserve_request(0) else {
        return recorded_error(StatusCode::SERVICE_UNAVAILABLE, "capacity");
    };
    let key_admission = hold
        .lock()
        .unwrap_or_else(std::sync::PoisonError::into_inner)
        .key
        .take()
        .expect("authenticated request retained its key admission");
    finish_worker(
        worker,
        AuthorizedDocumentRequest {
            key,
            target,
            admission: key_admission,
            request_reservation: reservation,
            decode_reservation: None,
            operation,
        },
        hold,
    )
    .await
}

async fn handle_commit_request(
    request: Request<Incoming>,
    admissions: Arc<ClientAdmissions>,
    worker: DocumentWorkerHandle,
    hold: Arc<Mutex<ConnectionHold>>,
    key: AuthenticatedKey,
    target: ClientTarget,
    permission: ClientPermission,
) -> Response<Body> {
    let declared_bytes = request
        .headers()
        .get(CONTENT_LENGTH)
        .and_then(|value| value.to_str().ok())
        .and_then(|value| value.parse::<usize>().ok())
        .unwrap_or(MAX_JSON_BODY_BYTES);
    let Ok(reservation) = worker.try_reserve_request(declared_bytes) else {
        return recorded_error(StatusCode::SERVICE_UNAVAILABLE, "capacity");
    };
    hold.lock()
        .unwrap_or_else(std::sync::PoisonError::into_inner)
        .request = Some(reservation);
    let body = match tokio::time::timeout(
        BODY_TIMEOUT,
        collect_json_body(Body::new(request.into_body()), declared_bytes),
    )
    .await
    {
        Ok(Ok(body)) => body,
        Ok(Err(BodyCollectError::Capacity)) => {
            return recorded_error(StatusCode::SERVICE_UNAVAILABLE, "capacity");
        }
        Ok(Err(BodyCollectError::Invalid)) | Err(_) => {
            return recorded_error(StatusCode::BAD_REQUEST, "invalid_request");
        }
    };
    let Ok(decode_reservation) = worker.try_reserve_decode(body.len()) else {
        return recorded_error(StatusCode::SERVICE_UNAVAILABLE, "capacity");
    };
    hold.lock()
        .unwrap_or_else(std::sync::PoisonError::into_inner)
        .decode = Some(decode_reservation);
    let commit: ValidatedCommit = match decode_commit(&body) {
        Ok(commit) => commit,
        Err(ClientWireError::Capacity) => {
            return recorded_error(StatusCode::SERVICE_UNAVAILABLE, "capacity");
        }
        Err(_) => return recorded_error(StatusCode::BAD_REQUEST, "invalid_request"),
    };
    let collections: Vec<_> = commit.collections().collect();
    let previous = hold
        .lock()
        .unwrap_or_else(std::sync::PoisonError::into_inner)
        .key
        .take()
        .expect("authenticated request retained its key admission");
    let key_admission =
        match admissions.reauthorize(key, target, permission, &collections, previous) {
            Ok(admission) => admission,
            Err(error) => return admission_error(error),
        };
    let reservation = hold
        .lock()
        .unwrap_or_else(std::sync::PoisonError::into_inner)
        .request
        .take()
        .expect("body reservation retained through decoding");
    let decode_reservation = hold
        .lock()
        .unwrap_or_else(std::sync::PoisonError::into_inner)
        .decode
        .take()
        .expect("decode reservation retained through validation");
    finish_worker(
        worker,
        AuthorizedDocumentRequest {
            key,
            target,
            admission: key_admission,
            request_reservation: reservation,
            decode_reservation: Some(decode_reservation),
            operation: WorkerOperation::Commit(commit),
        },
        hold,
    )
    .await
}

async fn finish_worker(
    worker: DocumentWorkerHandle,
    request: AuthorizedDocumentRequest,
    hold: Arc<Mutex<ConnectionHold>>,
) -> Response<Body> {
    let Ok(response): Result<PreparedClientResponse, _> = worker.execute(request).await else {
        return recorded_error(StatusCode::SERVICE_UNAVAILABLE, "capacity");
    };
    let (status, bytes, lease) = response.into_parts();
    hold.lock()
        .unwrap_or_else(std::sync::PoisonError::into_inner)
        .retain_response(lease);
    metrics::counter!("terrabase_client_requests_total", "result" => result_class(status))
        .increment(1);
    json_response(status, Body::from(bytes))
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum BodyCollectError {
    Invalid,
    Capacity,
}

/// Keep only one input frame beside the single contiguous decode buffer.
/// The worker's pre-body lease must account for both at the declared upper bound.
async fn collect_json_body(body: Body, declared_bytes: usize) -> Result<Vec<u8>, BodyCollectError> {
    if declared_bytes > MAX_JSON_BODY_BYTES {
        return Err(BodyCollectError::Invalid);
    }
    let mut bytes = Vec::new();
    bytes
        .try_reserve_exact(declared_bytes)
        .map_err(|_| BodyCollectError::Capacity)?;
    let mut frames = body.into_data_stream();
    while let Some(frame) = frames.next().await {
        let frame = frame.map_err(|_| BodyCollectError::Invalid)?;
        if frame.len() > MAX_JSON_BODY_BYTES - bytes.len()
            || frame.len() > declared_bytes - bytes.len()
        {
            return Err(BodyCollectError::Invalid);
        }
        bytes.extend_from_slice(&frame);
    }
    Ok(bytes)
}

fn valid_body_headers(headers: &HeaderMap, route: &Route) -> bool {
    if headers.contains_key(CONTENT_ENCODING) {
        return false;
    }
    let mut transfer_encodings = headers.get_all(TRANSFER_ENCODING).iter();
    let chunked = match transfer_encodings.next() {
        None => false,
        Some(value) if value.as_bytes() == b"chunked" => true,
        Some(_) => return false,
    };
    if transfer_encodings.next().is_some() {
        return false;
    }
    let mut content_lengths = headers.get_all(CONTENT_LENGTH).iter();
    let length = match content_lengths.next() {
        None => None,
        Some(header) => match header
            .to_str()
            .ok()
            .and_then(|value| value.parse::<u64>().ok())
        {
            Some(length) => Some(length),
            None => return false,
        },
    };
    if content_lengths.next().is_some() {
        return false;
    }
    if chunked && length.is_some() {
        return false;
    }
    match route {
        Route::Point { .. } => !chunked && length.is_none_or(|length| length == 0),
        Route::Commit { .. } => {
            let mut content_types = headers.get_all(CONTENT_TYPE).iter();
            let exact_json = content_types
                .next()
                .is_some_and(|value| value.as_bytes() == b"application/json")
                && content_types.next().is_none();
            exact_json && length.is_none_or(|length| length <= MAX_JSON_BODY_BYTES as u64)
        }
    }
}

enum Route {
    Point {
        target: ClientTarget,
        collection: CollectionId,
        document: DocumentId,
    },
    Commit {
        target: ClientTarget,
    },
}

impl Route {
    const fn target_and_permission(&self) -> (ClientTarget, ClientPermission) {
        match self {
            Self::Point { target, .. } => (*target, ClientPermission::Read),
            Self::Commit { target } => (*target, ClientPermission::Write),
        }
    }

    const fn first_collection(&self) -> Option<CollectionId> {
        match self {
            Self::Point { collection, .. } => Some(*collection),
            Self::Commit { .. } => None,
        }
    }
}

fn parse_route(method: &Method, path: &str, query: Option<&str>) -> Result<Option<Route>, ()> {
    let parts: Vec<_> = path.split('/').collect();
    let [
        "",
        "v1",
        "tenants",
        tenant,
        "databases",
        database,
        "shards",
        shard,
        tail @ ..,
    ] = parts.as_slice()
    else {
        return Ok(None);
    };
    let point = method == Method::GET && matches!(tail, ["collections", _, "documents", _]);
    let commit = method == Method::POST && tail == ["transactions"];
    if !point && !commit {
        return Ok(None);
    }
    if query.is_some() {
        return Err(());
    }
    let target = ClientTarget {
        tenant: TenantId::parse_canonical(tenant).map_err(|_| ())?,
        database: DatabaseId::parse_canonical(database).map_err(|_| ())?,
        shard: ShardId::new(parse_canonical_u64(shard).ok_or(())?),
    };
    if point {
        let ["collections", collection, "documents", document] = tail else {
            unreachable!("point shape checked before ID parsing");
        };
        Ok(Some(Route::Point {
            target,
            collection: CollectionId::parse_canonical(collection).map_err(|_| ())?,
            document: DocumentId::parse_canonical(document).map_err(|_| ())?,
        }))
    } else {
        Ok(Some(Route::Commit { target }))
    }
}

fn parse_canonical_u64(value: &str) -> Option<u64> {
    let bytes = value.as_bytes();
    if bytes.is_empty()
        || (bytes.len() > 1 && bytes[0] == b'0')
        || !bytes.iter().all(u8::is_ascii_digit)
    {
        return None;
    }
    value.parse().ok()
}

fn admission_error(error: ClientAdmissionError) -> Response<Body> {
    match error {
        ClientAdmissionError::Closed => recorded_error(StatusCode::SERVICE_UNAVAILABLE, "capacity"),
        ClientAdmissionError::Auth(AuthError::InvalidKey) => {
            recorded_error(StatusCode::UNAUTHORIZED, "invalid_key")
        }
        ClientAdmissionError::Auth(AuthError::ForbiddenScope) => {
            recorded_error(StatusCode::FORBIDDEN, "forbidden_scope")
        }
        ClientAdmissionError::Auth(AuthError::RateLimited) => {
            recorded_error(StatusCode::TOO_MANY_REQUESTS, "rate_limited")
        }
    }
}

fn recorded_error(status: StatusCode, code: &'static str) -> Response<Body> {
    metrics::counter!("terrabase_client_requests_total", "result" => code).increment(1);
    let body = format!("{{\"error\":{{\"code\":\"{code}\"}}}}");
    json_response(status, Body::from(body))
}

fn json_response(status: StatusCode, body: Body) -> Response<Body> {
    let mut response = Response::new(body);
    *response.status_mut() = status;
    response
        .headers_mut()
        .insert(CONTENT_TYPE, HeaderValue::from_static("application/json"));
    response
}

fn result_class(status: StatusCode) -> &'static str {
    match status.as_u16() {
        200 => "success",
        400 => "invalid_request",
        401 => "invalid_key",
        403 => "forbidden_scope",
        409 => "conflict",
        429 => "rate_limited",
        503 => "capacity",
        _ => "other",
    }
}

#[derive(Debug, thiserror::Error)]
pub(crate) enum ClientHttpError {
    #[error("client TLS handshake timeout must be between one and ten seconds")]
    InvalidHandshakeTimeout,
    #[error("could not inspect client listener: {0}")]
    ListenerAddress(#[source] io::Error),
    #[error("client listener must use a concrete address, got {0}")]
    WildcardListener(SocketAddr),
    #[error("client socket admission closed unexpectedly")]
    SocketSlotsClosed,
    #[error("client listener failed: {0}")]
    Accept(#[source] io::Error),
    #[error("client connection task failed: {0}")]
    ConnectionTask(#[source] tokio::task::JoinError),
}

#[cfg(test)]
mod tests {
    use super::*;

    #[tokio::test]
    async fn body_collector_handles_fragmentation_and_rejects_overflow() {
        let half = MAX_JSON_BODY_BYTES / 2;
        let body = Body::from_stream(futures_util::stream::iter([
            Ok::<_, io::Error>(vec![b'a'; half]),
            Ok(vec![b'b'; half]),
        ]));
        let collected = collect_json_body(body, MAX_JSON_BODY_BYTES)
            .await
            .expect("two frames at the body limit");
        assert_eq!(collected.len(), MAX_JSON_BODY_BYTES);
        assert_eq!(collected[0], b'a');
        assert_eq!(collected[half], b'b');

        let overflow = Body::from_stream(futures_util::stream::iter([
            Ok::<_, io::Error>(vec![b'a'; half]),
            Ok(vec![b'b'; half]),
            Ok(vec![b'c']),
        ]));
        assert_eq!(
            collect_json_body(overflow, MAX_JSON_BODY_BYTES).await,
            Err(BodyCollectError::Invalid)
        );

        let over_declared = Body::from_stream(futures_util::stream::iter([
            Ok::<_, io::Error>(vec![b'a'; 2]),
            Ok(vec![b'b'; 2]),
        ]));
        assert_eq!(
            collect_json_body(over_declared, 3).await,
            Err(BodyCollectError::Invalid)
        );
    }

    #[test]
    fn route_parser_accepts_only_canonical_local_v1_paths() {
        let tenant = TenantId::from_bytes([1; 16]).to_string();
        let database = DatabaseId::from_bytes([2; 16]).to_string();
        let collection = CollectionId::from_bytes([3; 16]).to_string();
        let document = DocumentId::from_bytes([4; 16]).to_string();
        let point = format!(
            "/v1/tenants/{tenant}/databases/{database}/shards/1/collections/{collection}/documents/{document}"
        );
        assert!(matches!(
            parse_route(&Method::GET, &point, None),
            Ok(Some(Route::Point { .. }))
        ));
        assert!(
            parse_route(
                &Method::GET,
                &point.replace("/shards/1/", "/shards/01/"),
                None
            )
            .is_err()
        );
        assert!(parse_route(&Method::GET, &point, Some("key=secret")).is_err());
        let commit = format!("/v1/tenants/{tenant}/databases/{database}/shards/0/transactions");
        assert!(matches!(
            parse_route(&Method::POST, &commit, None),
            Ok(Some(Route::Commit { .. }))
        ));
    }
}
