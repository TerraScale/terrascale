//! Root-supervisor-only, local member status over a private Unix socket.

use std::convert::Infallible;
use std::fs;
use std::io::{self, Read};
use std::os::unix::fs::{MetadataExt, PermissionsExt};
use std::path::Path;
use std::sync::Arc;
use std::time::Duration;

use axum::body::Body;
use axum::http::header::{CONNECTION, CONTENT_LENGTH, CONTENT_TYPE, TRANSFER_ENCODING};
use axum::http::{Method, Request, Response, StatusCode};
use hyper::body::Incoming;
use hyper::server::conn::http1;
use hyper::service::service_fn;
use hyper_util::rt::{TokioIo, TokioTimer};
use tb_core::MembershipEpoch;
use tb_replication::{ConfigurationId, LocalAuthorityScope};
use tokio::io::AsyncWriteExt;
use tokio::net::{UnixListener, UnixStream};
use tokio::sync::{Semaphore, watch};
use tokio::task::JoinSet;

use crate::LocalServerState;

const MAX_CONNECTIONS: usize = 8;
const MAX_HTTP_BUFFER_BYTES: usize = 8 * 1024;
const MAX_HTTP_HEADERS: usize = 16;
const HEADER_TIMEOUT: Duration = Duration::from_secs(2);
const MAX_STATUS_BYTES: usize = 4096;
const MAX_SELF_STATUS_BYTES: u64 = 16 * 1024;

/// Identity frozen from a verified signed authority and the local mTLS leaf.
#[derive(Clone)]
pub struct MemberControlIdentity {
    cluster_id: String,
    tenant_id: String,
    database_id: String,
    shard_id: u64,
    member_id: String,
    incarnation: String,
    epoch: u64,
    configuration_id: String,
    certificate_sha256: String,
    tls_leaf_sha256: String,
    start_nonce: String,
    receiver_only: bool,
}

impl MemberControlIdentity {
    /// Capture one process-start nonce after authority and TLS verification.
    ///
    /// # Errors
    /// Fails closed if operating-system randomness is unavailable.
    #[allow(clippy::too_many_arguments)]
    pub(crate) fn from_verified(
        scope: LocalAuthorityScope,
        incarnation: [u8; 16],
        epoch: MembershipEpoch,
        configuration: ConfigurationId,
        certificate_sha256: [u8; 32],
        tls_leaf_sha256: [u8; 32],
        receiver_only: bool,
    ) -> io::Result<Self> {
        let mut nonce = [0_u8; 16];
        getrandom::fill(&mut nonce).map_err(io::Error::other)?;
        Ok(Self {
            cluster_id: hex(&scope.cluster),
            tenant_id: scope.tenant.to_string(),
            database_id: scope.database.to_string(),
            shard_id: scope.shard.get(),
            member_id: scope.member.to_string(),
            incarnation: hex(&incarnation),
            epoch: epoch.get(),
            configuration_id: hex(&configuration.to_bytes()),
            certificate_sha256: hex(&certificate_sha256),
            tls_leaf_sha256: hex(&tls_leaf_sha256),
            start_nonce: hex(&nonce),
            receiver_only,
        })
    }
}

fn hex(bytes: &[u8]) -> String {
    let mut result = String::with_capacity(bytes.len() * 2);
    for byte in bytes {
        use std::fmt::Write as _;
        write!(&mut result, "{byte:02x}").expect("write into String");
    }
    result
}

fn effective_gid() -> io::Result<u32> {
    let mut status = Vec::new();
    fs::File::open("/proc/self/status")?
        .take(MAX_SELF_STATUS_BYTES + 1)
        .read_to_end(&mut status)?;
    if status.len() as u64 > MAX_SELF_STATUS_BYTES {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "process status exceeds limit",
        ));
    }
    let status = String::from_utf8(status)
        .map_err(|error| io::Error::new(io::ErrorKind::InvalidData, error))?;
    let invalid = || io::Error::new(io::ErrorKind::InvalidData, "invalid process Gid record");
    let mut records = status.lines().filter_map(|line| line.strip_prefix("Gid:"));
    let record = records.next().ok_or_else(invalid)?;
    if records.next().is_some() {
        return Err(invalid());
    }
    let values = record
        .split_whitespace()
        .map(str::parse::<u32>)
        .collect::<Result<Vec<_>, _>>()
        .map_err(|_| invalid())?;
    let [_, effective, _, _] = values.as_slice() else {
        return Err(invalid());
    };
    Ok(*effective)
}

/// Bind once inside a root-owned, tenant-group-writable, world-inaccessible
/// per-attempt directory. A pre-existing socket is never removed or replaced.
///
/// # Errors
/// Rejects an unsafe parent or any failed bind or permission change.
pub fn bind_member_control(path: &Path) -> io::Result<UnixListener> {
    if !path.is_absolute() || path.file_name().is_none() {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "member control socket path must be absolute",
        ));
    }
    let parent = path.parent().ok_or_else(|| {
        io::Error::new(
            io::ErrorKind::InvalidInput,
            "member control socket has no parent",
        )
    })?;
    let metadata = fs::symlink_metadata(parent)?;
    let mode = metadata.mode() & 0o777;
    let process_gid = effective_gid()?;
    if !metadata.file_type().is_dir()
        || metadata.uid() != 0
        || metadata.gid() != process_gid
        || mode != 0o730
    {
        return Err(io::Error::new(
            io::ErrorKind::PermissionDenied,
            "member control parent must be root-owned, match process effective GID, tenant-group writable and private",
        ));
    }
    let listener = UnixListener::bind(path)?;
    // Root lacks CAP_DAC_OVERRIDE in the host unit. The private parent blocks
    // everyone outside its tenant group; world-write on this inode permits
    // root to connect, then peer_cred rejects every non-root caller.
    fs::set_permissions(path, fs::Permissions::from_mode(0o602))?;
    Ok(listener)
}

/// Serve one HTTP/1.1 request per authenticated Unix connection until shutdown.
///
/// # Errors
/// Returns an accept or task failure; the caller closes readiness on failure.
pub async fn serve_member_control(
    listener: UnixListener,
    identity: MemberControlIdentity,
    state: Arc<LocalServerState>,
    mut shutdown: watch::Receiver<bool>,
) -> io::Result<()> {
    let identity = Arc::new(identity);
    let slots = Arc::new(Semaphore::new(MAX_CONNECTIONS));
    let mut tasks = JoinSet::new();
    loop {
        tokio::select! {
            changed = shutdown.changed() => {
                if changed.is_err() || *shutdown.borrow() { break; }
            }
            accepted = listener.accept() => {
                let (mut stream, _) = accepted?;
                let credentials = stream.peer_cred()?;
                if credentials.uid() != 0 {
                    let _ = stream.write_all(b"HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n").await;
                    continue;
                }
                let Ok(permit) = Arc::clone(&slots).try_acquire_owned() else {
                    drop(stream);
                    continue;
                };
                let identity = Arc::clone(&identity);
                let state = Arc::clone(&state);
                tasks.spawn(async move {
                    let _permit = permit;
                    serve_connection(stream, identity, state).await;
                });
            }
            result = tasks.join_next(), if !tasks.is_empty() => {
                if let Some(Err(error)) = result {
                    return Err(io::Error::other(error));
                }
            }
        }
    }
    tasks.abort_all();
    while tasks.join_next().await.is_some() {}
    Ok(())
}

async fn serve_connection(
    stream: UnixStream,
    identity: Arc<MemberControlIdentity>,
    state: Arc<LocalServerState>,
) {
    let service = service_fn(move |request: Request<Incoming>| {
        let identity = Arc::clone(&identity);
        let state = Arc::clone(&state);
        async move { Ok::<_, Infallible>(dispatch(&request, &identity, &state)) }
    });
    let mut builder = http1::Builder::new();
    builder
        .keep_alive(false)
        .max_buf_size(MAX_HTTP_BUFFER_BYTES)
        .max_headers(MAX_HTTP_HEADERS)
        .timer(TokioTimer::new())
        .header_read_timeout(HEADER_TIMEOUT);
    if let Err(error) = builder
        .serve_connection(TokioIo::new(stream), service)
        .await
    {
        tracing::debug!(%error, "member control connection closed");
    }
}

fn dispatch(
    request: &Request<Incoming>,
    identity: &MemberControlIdentity,
    state: &LocalServerState,
) -> Response<Body> {
    let path = request.uri().path();
    if matches!(
        path,
        "/v1/member/serving-proof" | "/v1/member/drain" | "/v1/member/drain-status"
    ) {
        return json_response(
            StatusCode::NOT_IMPLEMENTED,
            br#"{"error":"unsupported"}"#.to_vec(),
        );
    }
    if path != "/v1/member/status" {
        return json_response(StatusCode::NOT_FOUND, br#"{"error":"not_found"}"#.to_vec());
    }
    if request.method() != Method::GET {
        return json_response(
            StatusCode::METHOD_NOT_ALLOWED,
            br#"{"error":"method_not_allowed"}"#.to_vec(),
        );
    }
    if request.headers().contains_key(TRANSFER_ENCODING)
        || request
            .headers()
            .get_all(CONTENT_LENGTH)
            .iter()
            .any(|value| value.as_bytes() != b"0")
        || request.headers().get_all(CONTENT_LENGTH).iter().count() > 1
    {
        return json_response(
            StatusCode::BAD_REQUEST,
            br#"{"error":"invalid_request"}"#.to_vec(),
        );
    }
    let Some(nonce) = request
        .uri()
        .query()
        .and_then(|query| query.strip_prefix("nonce="))
    else {
        return json_response(
            StatusCode::BAD_REQUEST,
            br#"{"error":"invalid_nonce"}"#.to_vec(),
        );
    };
    if nonce.len() != 32
        || !nonce
            .bytes()
            .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
    {
        return json_response(
            StatusCode::BAD_REQUEST,
            br#"{"error":"invalid_nonce"}"#.to_vec(),
        );
    }
    let payload = serde_json::json!({
        "schema_version": 1,
        "nonce": nonce,
        "process": {"pid": std::process::id(), "start_nonce": identity.start_nonce},
        "scope": {
            "cluster_id": identity.cluster_id,
            "tenant_id": identity.tenant_id,
            "database_id": identity.database_id,
            "shard_id": identity.shard_id,
            "member_id": identity.member_id,
            "incarnation": identity.incarnation,
        },
        "authority": {
            "epoch": identity.epoch,
            "configuration_id": identity.configuration_id,
            "certificate_sha256": identity.certificate_sha256,
            "tls_leaf_sha256": identity.tls_leaf_sha256,
        },
        "local": {
            "storage_recovered": true,
            "replication_listener_ready": state.replication_listener_ready(),
            "healthy": state.is_live(),
            "ready": state.is_ready(),
        },
        "receiver_only": identity.receiver_only,
        "serving": false,
        "capabilities": {"serving_proof": false, "durable_drain": false},
    });
    let bytes = serde_json::to_vec(&payload).expect("fixed member status serializes");
    if bytes.len() > MAX_STATUS_BYTES {
        return json_response(
            StatusCode::INTERNAL_SERVER_ERROR,
            br#"{"error":"status_too_large"}"#.to_vec(),
        );
    }
    json_response(StatusCode::OK, bytes)
}

fn json_response(status: StatusCode, bytes: Vec<u8>) -> Response<Body> {
    Response::builder()
        .status(status)
        .header(CONTENT_TYPE, "application/json")
        .header(CONTENT_LENGTH, bytes.len().to_string())
        .header(CONNECTION, "close")
        .body(Body::from(bytes))
        .expect("fixed member control response")
}
