//! The TBEP engine server over the Unix-domain-socket binding.
//!
//! One process hosts the engine side behind TBEP (PD-2026-08-26-02 D1):
//! this server binds a UDS path, accepts host connections, enforces the
//! channel binding (PD-2026-08-26-11 D1 — the backend-supplied tenant
//! binds the channel before any tenant-scoped operation runs, and no
//! frame ever carries a tenant), and serves every frame family through
//! the [`TbepHost`] seam.
//!
//! The UDS binding is the required local transport; the socket file is
//! created with mode `0600` (owner-only) so only the serving user's C#
//! host can connect — the security model the protocol spec records. The
//! QUIC/mTLS binding stays deferred per PD-2026-08-26-11 D1.
//!
//! Concurrency model: the engine core stays synchronous and
//! zero-dependency; all transport concern lives here. One thread per
//! connection (reader loop), one writer thread per connection, and one
//! drainer thread per backbone subscription. Every outbound path — the
//! connection's answers and the subscription drainers — hands off
//! through one bounded channel per connection
//! ([`OUTBOUND_DEPTH`] frames) with an explicit slow-consumer policy: a
//! consumer a full depth behind is a slow consumer, the connection is
//! closed (and the subscription's stream ends) rather than buffered
//! without bound — never a blocked engine thread, never unbounded
//! serving memory. Server correlation ids are
//! assigned by the connection's reader loop from 1; unsolicited
//! notification frames carry correlation `0` and causation equal to the
//! subscription they belong to (the spec's supervision rule), so frame
//! identity stays deterministic without cross-thread coordination.

use std::io::Write as _;
use std::os::unix::fs::PermissionsExt as _;
use std::os::unix::net::UnixListener;
use std::os::unix::net::UnixStream;
use std::path::Path;
use std::path::PathBuf;
use std::sync::Arc;
use std::sync::atomic::AtomicBool;
use std::sync::atomic::Ordering;
use std::sync::mpsc::SyncSender;
use std::sync::mpsc::TrySendError;
use std::sync::mpsc::sync_channel;
use std::thread::JoinHandle;

use crate::envelope::EnvelopeError;
use crate::envelope::IdempotencyKey;
use crate::envelope::TbepFrame;
use crate::generated::Error;
use crate::generated::ErrorCode;
use crate::generated::TbepBody;
use crate::host::HostingError;
use crate::host::TbepHost;
use crate::host::TbepNotification;

/// The socket-file permission the UDS binding enforces: owner-only.
pub const SOCKET_MODE: u32 = 0o600;

/// The outbound channel depth per connection: the small, explicit bound
/// a consumer may fall behind before the slow-consumer policy applies.
/// A consumer a full depth behind has the connection closed under it
/// (and each subscription's drainer stops) — the verdict stays bounded
/// and typed instead of buffering without bound or blocking the
/// engine's serving thread.
pub const OUTBOUND_DEPTH: usize = 64;

/// A binding failure.
#[derive(Debug)]
pub enum BindError {
    /// The socket path could not be bound.
    Io(std::io::Error),
}

impl std::fmt::Display for BindError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Io(error) => write!(f, "cannot bind the TBEP listener: {error}"),
        }
    }
}

impl std::error::Error for BindError {}

/// The running server handle.
pub struct TbepServerHandle {
    path: PathBuf,
    stopped: Arc<AtomicBool>,
    accept: Option<JoinHandle<()>>,
}

impl TbepServerHandle {
    /// The bound socket path.
    #[must_use]
    pub fn path(&self) -> &Path {
        &self.path
    }

    /// Signals the accept loop to stop and waits for it. Existing
    /// connections finish their in-flight frames and close on the next
    /// supervision shutdown or end of stream.
    pub fn shutdown(&mut self) {
        self.stopped.store(true, Ordering::SeqCst);
        // A probe connect wakes a blocking accept.
        if let Ok(mut stream) = UnixStream::connect(&self.path) {
            let _ = stream.write_all(&[0, 0, 0, 0]);
        }
        if let Some(thread) = self.accept.take() {
            let _ = thread.join();
        }
        let _ = std::fs::remove_file(&self.path);
    }
}

/// The TBEP engine server.
pub struct TbepServer;

impl TbepServer {
    /// Binds the UDS listener and starts serving the host.
    ///
    /// A stale socket file at `path` is removed first (an engine process
    /// owns its socket path); the new socket carries mode `0600`.
    ///
    /// # Errors
    ///
    /// Returns [`BindError::Io`] when the listener cannot be bound.
    pub fn bind(
        path: impl AsRef<Path>,
        host: Arc<dyn TbepHost>,
    ) -> Result<TbepServerHandle, BindError> {
        let path = path.as_ref();
        if path.exists() {
            let _ = std::fs::remove_file(path);
        }
        let listener = UnixListener::bind(path).map_err(BindError::Io)?;
        std::fs::set_permissions(path, std::fs::Permissions::from_mode(SOCKET_MODE))
            .map_err(BindError::Io)?;
        let stopped = Arc::new(AtomicBool::new(false));
        let accept_stopped = Arc::clone(&stopped);
        let accept_path = path.to_path_buf();
        let accept = std::thread::Builder::new()
            .name("tbep-accept".into())
            .spawn(move || {
                for stream in listener.incoming() {
                    if accept_stopped.load(Ordering::SeqCst) {
                        break;
                    }
                    match stream {
                        Ok(stream) => {
                            let host = Arc::clone(&host);
                            let _ = std::thread::Builder::new()
                                .name("tbep-connection".into())
                                .spawn(move || serve_connection(stream, host));
                        }
                        Err(_) => break,
                    }
                }
            })
            .map_err(BindError::Io)?;
        Ok(TbepServerHandle { path: accept_path, stopped, accept: Some(accept) })
    }
}

/// One outbound unit for the writer thread.
enum Outbound {
    /// A frame to write.
    Frame(TbepFrame),
    /// Close the stream after writing everything queued before this.
    Close,
}

struct Connection {
    host: Arc<dyn TbepHost>,
    outbound: SyncSender<Outbound>,
    tenant: Option<String>,
    correlation: u64,
    /// The slow-consumer verdict: the outbound channel filled to its
    /// depth; the reader loop closes the connection when it is set.
    slow: bool,
}

impl Connection {
    fn next_correlation(&mut self) -> u64 {
        self.correlation += 1;
        self.correlation
    }

    /// Hands one unit to the writer thread under the slow-consumer
    /// policy.
    ///
    /// A full bounded channel marks the connection a slow consumer (the
    /// reader loop then ends it); a closed channel means the writer
    /// ended first and the connection is done either way. Neither ever
    /// blocks the engine's serving thread.
    fn send_outbound(&mut self, unit: Outbound) {
        if let Err(TrySendError::Full(_)) = self.outbound.try_send(unit) {
            self.slow = true;
        }
    }

    fn send(&mut self, frame: TbepFrame) {
        self.send_outbound(Outbound::Frame(frame));
    }

    /// Queues the graceful close marker (best effort: a slow consumer
    /// with a full channel is closed by the reader loop instead).
    fn close(&mut self) {
        self.send_outbound(Outbound::Close);
    }

    fn error_frame(&mut self, causation: u64, code: ErrorCode, detail: &str) {
        let body = Error {
            code,
            detail: detail.chars().take(crate::generated::MAX_DETAIL_BYTES).collect(),
        };
        if let Ok(frame) = TbepFrame::seal(body, self.next_correlation(), causation, None) {
            self.send(frame);
        }
    }
}

// The clean end of stream and a transport failure both end the loop;
// the arm duplication keeps the two causes readable.
#[allow(clippy::match_same_arms)]
fn serve_connection(stream: UnixStream, host: Arc<dyn TbepHost>) {
    let Ok(reader) = stream.try_clone() else { return };
    let Ok(writer) = stream.try_clone() else { return };
    // The accepting thread's ownership ends here: dropping the listener
    // side closes the last borrowed fd when the connection ends, so a
    // peer blocked on read observes the close.
    drop(stream);
    let (outbound_sender, outbound_receiver) = sync_channel::<Outbound>(OUTBOUND_DEPTH);
    // A spawn failure ends this connection; the reader sees the closed
    // channel on its next answer and the loop exits.
    let _writer_spawn = std::thread::Builder::new().name("tbep-writer".into()).spawn(move || {
        let mut writer = writer;
        for unit in outbound_receiver {
            match unit {
                Outbound::Frame(frame) => {
                    if frame.write_stream(&mut writer).is_err() {
                        break;
                    }
                }
                Outbound::Close => {
                    let _ = writer.shutdown(std::net::Shutdown::Both);
                    break;
                }
            }
        }
    });
    let mut connection =
        Connection { host, outbound: outbound_sender, tenant: None, correlation: 0, slow: false };
    let mut reader = reader;
    loop {
        let frame = match TbepFrame::read_stream(&mut reader) {
            Ok(Some(frame)) => frame,
            Ok(None) => break,
            Err(EnvelopeError::UnsupportedVersion(_version)) => {
                connection.error_frame(0, ErrorCode::UnsupportedVersion, "unknown wire version");
                connection.close();
                break;
            }
            Err(EnvelopeError::FrameTooLarge(bound)) => {
                // An oversized declared prefix leaves the stream at an
                // unrecoverable position (the peer may never send the
                // declared bytes), so the failure fails closed hard:
                // the typed error frame, then the connection ends.
                connection.error_frame(
                    0,
                    ErrorCode::FrameTooLarge,
                    &format!("frame exceeds {bound}"),
                );
                connection.close();
                break;
            }
            Err(
                ref error @ (EnvelopeError::UnknownFrameKind(_)
                | EnvelopeError::DigestMismatch
                | EnvelopeError::IdempotencyPairing(_)
                | EnvelopeError::Malformed(_)
                | EnvelopeError::InvalidIdempotencyKey(_)),
            ) => {
                // Every decode refusal fails closed with its typed error
                // frame; the connection stays usable for the next frame.
                let (code, detail) = match error {
                    EnvelopeError::UnknownFrameKind(code) => {
                        (ErrorCode::UnknownFrameKind, format!("kind {code}"))
                    }
                    EnvelopeError::DigestMismatch => (ErrorCode::DigestMismatch, "digest".into()),
                    EnvelopeError::IdempotencyPairing(_) => {
                        (ErrorCode::IdempotencyPairing, "pairing".into())
                    }
                    EnvelopeError::Malformed(reason)
                    | EnvelopeError::InvalidIdempotencyKey(reason) => {
                        (ErrorCode::MalformedFrame, (*reason).into())
                    }
                    unsupported => unreachable!("the guard excludes {unsupported:?}"),
                };
                connection.error_frame(0, code, &detail);
                continue;
            }
            Err(EnvelopeError::Io(_)) => break,
        };
        let closing = dispatch(&mut connection, &frame);
        if closing || connection.slow {
            connection.close();
            break;
        }
    }
    if connection.slow {
        // The slow-consumer verdict: the connection ends now, not after
        // a backlog only a faster consumer could drain. The shutdown
        // fails the writer's pending writes immediately, which ends the
        // writer and (through the closed channel) every drainer of this
        // connection.
        let _ = reader.shutdown(std::net::Shutdown::Both);
    }
    drop(connection);
    // Closing the sender ends the writer thread after its queue drains.
}

/// Dispatches one frame. Returns `true` when the connection must close
/// after the answer is written (graceful shutdown).
// One arm per frame kind of the closed registry: the shape mirrors the
// generated frame taxonomy.
#[allow(clippy::too_many_lines)]
fn dispatch(connection: &mut Connection, frame: &TbepFrame) -> bool {
    let causation = frame.correlation();
    match frame.body().clone() {
        TbepBody::ChannelBindRequest(body) => {
            if connection.tenant.is_some() {
                connection.error_frame(causation, ErrorCode::AlreadyBound, "channel bound");
                return false;
            }
            match connection.host.bind_channel(&body) {
                Ok(response) => {
                    // The tenant binds the channel exactly when the host
                    // accepted the binding (PD-2026-08-26-11 D1 fails
                    // closed): a refused bind — a draining host refuses
                    // every new binding — leaves the channel unbound, so
                    // the next tenant-scoped frame is refused, never
                    // served under a tenant the host never accepted.
                    connection.tenant = Some(body.tenant);
                    respond(connection, causation, response);
                }
                Err(error) => {
                    connection.error_frame(causation, error.error_code(), &error.detail());
                }
            }
        }
        TbepBody::SupervisionHealthRequest(_) => match connection.host.health() {
            Ok(response) => respond(connection, causation, response),
            Err(error) => connection.error_frame(causation, error.error_code(), &error.detail()),
        },
        TbepBody::SupervisionShutdownRequest(body) => {
            match connection.host.begin_shutdown(&body) {
                Ok(response) => respond(connection, causation, response),
                Err(error) => {
                    connection.error_frame(causation, error.error_code(), &error.detail());
                    return false;
                }
            }
            return true;
        }
        TbepBody::AdminStatusRequest(_) => match connection.host.admin_status() {
            Ok(response) => respond(connection, causation, response),
            Err(error) => connection.error_frame(causation, error.error_code(), &error.detail()),
        },
        TbepBody::DocumentReadRequest(body) => {
            if let Some(tenant) = bound_tenant(connection, causation) {
                answer(connection, causation, connection.host.document_read(&tenant, &body));
            }
        }
        TbepBody::DocumentWriteRequest(body) => {
            if let Some(tenant) = bound_tenant(connection, causation)
                && let Some(key) = frame_key(frame)
            {
                answer(
                    connection,
                    causation,
                    connection.host.document_write(&tenant, &body, key.as_str()),
                );
            }
        }
        TbepBody::DocumentDeleteRequest(body) => {
            if let Some(tenant) = bound_tenant(connection, causation)
                && let Some(key) = frame_key(frame)
            {
                answer(
                    connection,
                    causation,
                    connection.host.document_delete(&tenant, &body, key.as_str()),
                );
            }
        }
        TbepBody::QueryExecuteRequest(body) => {
            if let Some(tenant) = bound_tenant(connection, causation) {
                answer(connection, causation, connection.host.query_execute(&tenant, &body));
            }
        }
        TbepBody::QueryCancelRequest(body) => {
            if let Some(tenant) = bound_tenant(connection, causation) {
                answer(connection, causation, connection.host.query_cancel(&tenant, &body));
            }
        }
        TbepBody::FamilyReadRequest(body) => {
            if let Some(tenant) = bound_tenant(connection, causation) {
                answer(connection, causation, connection.host.family_read(&tenant, &body));
            }
        }
        TbepBody::FamilyWriteRequest(body) => {
            if let Some(tenant) = bound_tenant(connection, causation)
                && let Some(key) = frame_key(frame)
            {
                answer(
                    connection,
                    causation,
                    connection.host.family_write(&tenant, &body, key.as_str()),
                );
            }
        }
        TbepBody::BackboneSubscribeRequest(body) => {
            if let Some(tenant) = bound_tenant(connection, causation) {
                match connection.host.backbone_subscribe(&tenant, &body) {
                    Ok((response, feed)) => {
                        respond(connection, causation, response);
                        // The drainer's lifetime is the connection's: it
                        // ends when the feed closes or the bounded
                        // outbound channel applies the slow-consumer
                        // policy; the handle detaches on drop.
                        drop(spawn_drainer(
                            connection.outbound.clone(),
                            body.subscription_id,
                            feed,
                        ));
                    }
                    Err(error) => {
                        connection.error_frame(causation, error.error_code(), &error.detail());
                    }
                }
            }
        }
        TbepBody::BackboneAcknowledgeRequest(body) => {
            if let Some(tenant) = bound_tenant(connection, causation) {
                answer(connection, causation, connection.host.backbone_acknowledge(&tenant, &body));
            }
        }
        TbepBody::BackboneUnsubscribeRequest(body) => {
            if let Some(tenant) = bound_tenant(connection, causation) {
                answer(connection, causation, connection.host.backbone_unsubscribe(&tenant, &body));
            }
        }
        TbepBody::SyncSessionSetupRequest(body) => {
            if let Some(tenant) = bound_tenant(connection, causation) {
                answer(connection, causation, connection.host.sync_session_setup(&tenant, &body));
            }
        }
        TbepBody::SyncDeltaPullRequest(body) => {
            if let Some(tenant) = bound_tenant(connection, causation) {
                answer(connection, causation, connection.host.sync_delta_pull(&tenant, &body));
            }
        }
        TbepBody::SyncSnapshotPullRequest(body) => {
            if let Some(tenant) = bound_tenant(connection, causation) {
                answer(connection, causation, connection.host.sync_snapshot_pull(&tenant, &body));
            }
        }
        TbepBody::SyncCheckpointAckRequest(body) => {
            if let Some(tenant) = bound_tenant(connection, causation) {
                answer(connection, causation, connection.host.sync_checkpoint_ack(&tenant, &body));
            }
        }
        TbepBody::SyncMutationSubmitRequest(body) => {
            if let Some(tenant) = bound_tenant(connection, causation)
                && let Some(key) = frame_key(frame)
            {
                answer(
                    connection,
                    causation,
                    connection.host.sync_mutation_submit(&tenant, &body, key.as_str()),
                );
            }
        }
        TbepBody::SyncSessionCloseRequest(body) => {
            if let Some(tenant) = bound_tenant(connection, causation) {
                answer(connection, causation, connection.host.sync_session_close(&tenant, &body));
            }
        }
        TbepBody::TopologyReadRequest(_) => {
            if let Some(tenant) = bound_tenant(connection, causation) {
                answer(connection, causation, connection.host.topology_read(&tenant));
            }
        }
        // The GAP-R03 replication observation requests (PB-5035) are
        // registered reads the shipped hosts do not serve: the wire says
        // so typed, instead of misreporting a request as a direction
        // violation. Serving them from a live engine member is the
        // engine-backed host integrator action the decision record
        // (`docs/replication/gapr03-replication-observations.md`) names.
        TbepBody::ReplicationConflictsRequest(_)
        | TbepBody::ReplicationFencedRequest(_)
        | TbepBody::ReplicationDepositionsRequest(_)
        | TbepBody::ReplicationCursorRequest(_)
        | TbepBody::ReplicationHealthRequest(_)
        | TbepBody::ReplicationDenialsRequest(_) => {
            if bound_tenant(connection, causation).is_some() {
                connection.error_frame(
                    causation,
                    ErrorCode::OperationRefused,
                    "replication observations are not served by this host",
                );
            }
        }
        // Response and notification kinds are engine-to-host frames: a
        // host sending them to the engine is a protocol violation.
        TbepBody::Error(body) => {
            let _ = body;
            connection.error_frame(causation, ErrorCode::MalformedFrame, "wrong direction");
        }
        _ => {
            connection.error_frame(causation, ErrorCode::MalformedFrame, "wrong direction");
        }
    }
    false
}

fn bound_tenant(connection: &mut Connection, causation: u64) -> Option<String> {
    if connection.tenant.is_none() {
        connection.error_frame(causation, ErrorCode::UnboundChannel, "bind the channel first");
        return None;
    }
    connection.tenant.clone()
}

fn frame_key(frame: &TbepFrame) -> Option<IdempotencyKey> {
    frame.idempotency().cloned()
}

fn answer<B: Into<TbepBody>>(
    connection: &mut Connection,
    causation: u64,
    outcome: Result<B, HostingError>,
) {
    match outcome {
        Ok(response) => respond(connection, causation, response),
        Err(error) => connection.error_frame(causation, error.error_code(), &error.detail()),
    }
}

fn respond<B: Into<TbepBody>>(connection: &mut Connection, causation: u64, body: B) {
    let correlation = connection.next_correlation();
    match TbepFrame::seal(body, correlation, causation, None) {
        Ok(frame) => connection.send(frame),
        Err(error) => {
            connection.error_frame(causation, ErrorCode::EngineFailure, &error.to_string());
        }
    }
}

fn spawn_drainer(
    outbound: SyncSender<Outbound>,
    subscription_id: u64,
    feed: std::sync::mpsc::Receiver<TbepNotification>,
) -> JoinHandle<()> {
    std::thread::Builder::new()
        .name("tbep-subscription".into())
        .spawn(move || {
            for notification in feed {
                let frame = match notification {
                    TbepNotification::Batch(body) => {
                        notification_frame(subscription_id, body.into())
                    }
                    TbepNotification::Trigger(body) => {
                        notification_frame(subscription_id, body.into())
                    }
                    TbepNotification::Closed(body) => {
                        notification_frame(subscription_id, body.into())
                    }
                };
                if let Ok(frame) = frame {
                    // The same bounded depth and slow-consumer policy as the
                    // connection's answers: a consumer a full depth behind
                    // ends this subscription's stream (the connection is
                    // closed under it) rather than queueing without bound.
                    if let Err(TrySendError::Full(_) | TrySendError::Disconnected(_)) =
                        outbound.try_send(Outbound::Frame(frame))
                    {
                        break;
                    }
                }
            }
        })
        .expect("the drainer thread spawns")
}

fn notification_frame(subscription_id: u64, body: TbepBody) -> Result<TbepFrame, EnvelopeError> {
    // Unsolicited pushes carry correlation 0 and causation naming the
    // subscription they belong to (the spec's notification rule).
    TbepFrame::seal(body, 0, subscription_id, None)
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::mpsc::TrySendError;
    use std::sync::mpsc::channel;
    use std::time::Duration;

    use crate::host::SyncDeliveryHost;

    fn connection_over(outbound: SyncSender<Outbound>) -> Connection {
        Connection {
            host: Arc::new(SyncDeliveryHost::new(1)),
            outbound,
            tenant: None,
            correlation: 0,
            slow: false,
        }
    }

    fn notification() -> TbepNotification {
        TbepNotification::Trigger(crate::generated::TriggerNotification {
            subscription_id: 42,
            collection: "orders".into(),
            sequence: 1,
            operation_id: "op-01".into(),
        })
    }

    fn outbound_frame() -> Outbound {
        let frame = match notification() {
            TbepNotification::Trigger(body) => notification_frame(42, body.into()),
            _ => unreachable!("the fixture builds a trigger"),
        }
        .expect("seals");
        Outbound::Frame(frame)
    }

    #[test]
    fn a_full_outbound_channel_marks_the_connection_a_slow_consumer() {
        // The outbound channel is bounded with a small depth; a writer
        // that falls a full depth behind is a slow consumer and the
        // connection is closed — never buffered without bound, never
        // blocking the engine's serving thread.
        let (outbound, receiver) = sync_channel::<Outbound>(1);
        let mut connection = connection_over(outbound);
        connection.send_outbound(outbound_frame());
        assert!(!connection.slow, "the depth-one channel takes its first frame");

        connection.send_outbound(outbound_frame());
        assert!(connection.slow, "a full outbound channel marks the slow consumer");

        // The queued unit is still delivered to the writer.
        assert!(matches!(receiver.recv_timeout(Duration::from_secs(5)), Ok(Outbound::Frame(_))));
    }

    #[test]
    fn the_drainer_stops_at_the_bounded_depth() {
        // The subscription drainer hands off to the same bounded
        // outbound channel: when the consumer is a full depth behind,
        // the drainer stops (the subscription stream ends for that
        // connection) instead of queueing every notification. The count
        // is taken after the drainer ends, so the row is deterministic.
        let (outbound, receiver) = sync_channel::<Outbound>(2);
        let (feed, feed_receiver) = channel();
        let drainer = spawn_drainer(outbound, 42, feed_receiver);
        for _ in 0..6 {
            feed.send(notification()).expect("the feed accepts the notification");
        }
        drop(feed);
        drainer.join().expect("the drainer ends on the full channel");
        let mut delivered = 0;
        while receiver.recv_timeout(Duration::from_secs(5)).is_ok() {
            delivered += 1;
        }
        assert_eq!(
            delivered, 2,
            "the drainer stops at the bounded depth, not after queueing every notification"
        );
    }

    #[test]
    fn a_disconnected_outbound_channel_is_not_a_slow_consumer() {
        // A closed channel means the writer ended first; the connection
        // is done either way, and the distinct failure is not recorded
        // as the slow-consumer verdict.
        let (outbound, receiver) = sync_channel::<Outbound>(1);
        drop(receiver);
        let mut connection = connection_over(outbound);
        match connection.outbound.try_send(outbound_frame()) {
            Err(TrySendError::Disconnected(_)) => {}
            other => panic!("the dropped receiver disconnects, got {other:?}"),
        }
        connection.send_outbound(outbound_frame());
        assert!(!connection.slow, "disconnected is not the slow-consumer verdict");
    }
}
