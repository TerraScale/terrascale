//! One bounded outbound Sync stream for the sole other signed-v2 member.

use std::sync::Arc;
use std::time::Duration;

use futures_util::stream;
use tb_core::{Attribution, MemoryBudget, OriginId, ReplicaId, WorkReservation};
use tb_document::DocumentError;
use tb_protocol::{
    AdmissionController, AdmissionLimits, BatchPermit, CommittedGroupMetadata,
    EncodedCommittedGroup, MAX_ENCODED_APPLICATION_BYTES, MAX_GRPC_MESSAGE_BYTES,
    MAX_IN_FLIGHT_BYTES_PER_PEER, PeerCreditPermit, PeerCreditWindow, ProtocolError,
    V1_EVENT_FORMAT, V1_PROTOCOL_MAJOR, V1_PROTOCOL_MINOR, encode_committed_group, v1,
    v1_transfer_limits, validate_frontiers, validate_peer_identity,
};
use tb_replication::{ReplicationLimits, decode_metadata};
use tokio::sync::{mpsc, watch};
use tokio::time::{Instant, sleep, sleep_until, timeout};
use tonic::{Code, Streaming};

use crate::distributed_runtime::DocumentMemberRuntime;
use crate::{
    CgroupMemoryMonitor, REPLICATION_SENDER_RESERVATION_BYTES, ReplicationMemoryProfile,
    ReplicationOutboundTls,
};

const CONNECT_TIMEOUT: Duration = Duration::from_secs(10);
const HELLO_TIMEOUT: Duration = Duration::from_secs(10);
const PROGRESS_TIMEOUT: Duration = Duration::from_secs(30);
const IDLE_POLL: Duration = Duration::from_millis(200);
const KEEPALIVE_IDLE: Duration = Duration::from_secs(15);
const KEEPALIVE_TIMEOUT: Duration = Duration::from_secs(10);
const BACKOFF_MIN: Duration = Duration::from_millis(250);
const BACKOFF_MAX: Duration = Duration::from_secs(15);
const RECORD_FORMAT: u32 = 2;
const MERGE_POLICY: u32 = 1;
const METADATA_LIMITS: ReplicationLimits = ReplicationLimits {
    max_origins: 64,
    max_changes: tb_replication::MAX_EVENT_CHANGES,
    max_siblings: 64,
    max_value_bytes: tb_core::MAX_VALUE_BYTES,
    max_payload_bytes: tb_replication::MAX_WAL_GROUP_BYTES,
};

/// A sender failure that makes the document/client service unready.
#[derive(Debug, thiserror::Error)]
pub(crate) enum ReplicationSenderFatal {
    #[error("replication source failure: {0}")]
    Source(String),
    #[error("replication peer identity failure: {0}")]
    Identity(String),
    #[error("replication protocol failure: {0}")]
    Protocol(String),
    #[error("replication frontier failure: {0}")]
    Frontier(String),
}

impl ReplicationSenderFatal {
    fn metric_reason(&self) -> &'static str {
        match self {
            Self::Source(_) => "source",
            Self::Identity(_) => "identity",
            Self::Protocol(_) => "protocol",
            Self::Frontier(_) => "frontier",
        }
    }
}

#[derive(Clone, Copy)]
enum RetryReason {
    Connect,
    Stream,
    Timeout,
    Resource,
}

impl RetryReason {
    const fn metric_reason(self) -> &'static str {
        match self {
            Self::Connect => "connect",
            Self::Stream => "stream",
            Self::Timeout => "timeout",
            Self::Resource => "resource",
        }
    }
}

enum AttemptError {
    Retry(RetryReason),
    Fatal(ReplicationSenderFatal),
    Shutdown,
}

struct PreparedBatch {
    sequence: u64,
    encoded: EncodedCommittedGroup,
}

struct InFlight<'a> {
    sequence: u64,
    _batch: BatchPermit,
    _credit: PeerCreditPermit<'a>,
}

struct StreamState {
    durable: u64,
    applied: u64,
    high_water: u64,
    limits: v1::TransferLimits,
    pending_ping: Option<(u64, Instant)>,
    last_activity: Instant,
}

/// A single fixed-peer sender. All reconnects use a fresh signed Welcome.
pub(crate) struct ReplicationSender {
    endpoint: ReplicationOutboundTls,
    document: Arc<DocumentMemberRuntime>,
    local_identity: v1::PeerIdentity,
    remote_identity: v1::PeerIdentity,
    local_origin: OriginId,
    remote_origin: OriginId,
    budget: MemoryBudget,
    monitor: CgroupMemoryMonitor,
    profile: ReplicationMemoryProfile,
    attribution: Attribution,
}

impl ReplicationSender {
    /// Bind an outbound worker to one already certified endpoint and document owner.
    pub(crate) fn new(
        endpoint: ReplicationOutboundTls,
        document: Arc<DocumentMemberRuntime>,
        local_identity: v1::PeerIdentity,
        budget: MemoryBudget,
        monitor: CgroupMemoryMonitor,
        profile: ReplicationMemoryProfile,
        attribution: Attribution,
    ) -> Result<Self, ReplicationSenderFatal> {
        validate_peer_identity(&local_identity)
            .map_err(|error| ReplicationSenderFatal::Identity(error.to_string()))?;
        let local_member: [u8; 16] = local_identity
            .member_id
            .as_ref()
            .try_into()
            .map_err(|_| ReplicationSenderFatal::Identity("local member width".into()))?;
        let local_incarnation: [u8; 16] = local_identity
            .member_incarnation
            .as_ref()
            .try_into()
            .map_err(|_| ReplicationSenderFatal::Identity("local incarnation width".into()))?;
        let local_origin = OriginId {
            replica_id: ReplicaId::from_bytes(local_member),
            incarnation: local_incarnation,
        };
        let remote_origin = endpoint.peer_origin();
        if remote_origin == local_origin {
            return Err(ReplicationSenderFatal::Identity(
                "outbound endpoint points at local member".into(),
            ));
        }
        let mut remote_identity = local_identity.clone();
        remote_identity.member_id = remote_origin.replica_id.to_bytes().to_vec().into();
        remote_identity.member_incarnation = remote_origin.incarnation.to_vec().into();
        Ok(Self {
            endpoint,
            document,
            local_identity,
            remote_identity,
            local_origin,
            remote_origin,
            budget,
            monitor,
            profile,
            attribution,
        })
    }

    /// Run until orderly shutdown or a fatal source/peer invariant failure.
    pub(crate) async fn run(
        self,
        mut shutdown: watch::Receiver<bool>,
    ) -> Result<(), ReplicationSenderFatal> {
        metrics::gauge!("terrabase_replication_sender_connected").set(0.0);
        metrics::counter!("terrabase_replication_sender_source_scans_total").increment(0);
        let mut backoff = BACKOFF_MIN;
        loop {
            if *shutdown.borrow() {
                return Ok(());
            }
            let result = self.run_one_stream(&mut shutdown).await;
            metrics::gauge!("terrabase_replication_sender_connected").set(0.0);
            match result {
                Err(AttemptError::Shutdown) | Ok(()) => return Ok(()),
                Err(AttemptError::Fatal(error)) => {
                    metrics::counter!(
                        "terrabase_replication_sender_fatal_total",
                        "reason" => error.metric_reason()
                    )
                    .increment(1);
                    return Err(error);
                }
                Err(AttemptError::Retry(reason)) => {
                    metrics::counter!(
                        "terrabase_replication_sender_reconnect_total",
                        "reason" => reason.metric_reason()
                    )
                    .increment(1);
                    tokio::select! {
                        () = wait_shutdown(&mut shutdown) => return Ok(()),
                        () = sleep(jitter(backoff)) => {}
                    }
                    backoff = backoff.saturating_mul(2).min(BACKOFF_MAX);
                }
            }
        }
    }

    #[allow(
        clippy::too_many_lines,
        reason = "one stream owns the bounded handshake, send, ACK, and shutdown state machine"
    )]
    async fn run_one_stream(
        &self,
        shutdown: &mut watch::Receiver<bool>,
    ) -> Result<(), AttemptError> {
        if !self
            .monitor
            .can_admit(REPLICATION_SENDER_RESERVATION_BYTES, self.profile)
        {
            return Err(AttemptError::Retry(RetryReason::Resource));
        }
        let _stream_work: WorkReservation = self
            .budget
            .try_reserve_work(self.attribution, REPLICATION_SENDER_RESERVATION_BYTES, 1)
            .map_err(|_| AttemptError::Retry(RetryReason::Resource))?;
        let endpoint = self.endpoint.endpoint();
        let channel = tokio::select! {
            () = wait_shutdown(shutdown) => return Err(AttemptError::Shutdown),
            result = timeout(CONNECT_TIMEOUT, endpoint.connect()) => {
                match result {
                    Ok(Ok(channel)) => channel,
                    Ok(Err(error)) => return Err(classify_transport_error(&error)),
                    Err(_) => return Err(AttemptError::Retry(RetryReason::Timeout)),
                }
            }
        };
        let mut client = v1::replication_client::ReplicationClient::new(channel)
            .max_decoding_message_size(64 * 1024)
            .max_encoding_message_size(MAX_GRPC_MESSAGE_BYTES);
        let (sender, receiver) = mpsc::channel::<v1::SyncRequest>(1);
        let hello = self.hello()?;
        sender
            .try_send(v1::SyncRequest {
                frame: Some(v1::sync_request::Frame::Hello(hello)),
            })
            .map_err(|_| AttemptError::Retry(RetryReason::Stream))?;
        let requests = stream::unfold(receiver, |mut receiver| async move {
            receiver.recv().await.map(|frame| (frame, receiver))
        });
        let hello_deadline = Instant::now() + HELLO_TIMEOUT;
        let mut responses = tokio::select! {
            () = wait_shutdown(shutdown) => return Err(AttemptError::Shutdown),
            () = sleep_until(hello_deadline) => return Err(AttemptError::Retry(RetryReason::Timeout)),
            result = client.sync(requests) => {
                match result {
                    Ok(response) => response.into_inner(),
                    Err(status) => return Err(classify_status(&status)),
                }
            }
        };
        let first = next_response(&mut responses, shutdown, hello_deadline).await?;
        let Some(v1::sync_response::Frame::Welcome(welcome)) = first.frame else {
            return Err(AttemptError::Fatal(ReplicationSenderFatal::Protocol(
                "first Sync response is not Welcome".into(),
            )));
        };
        let mut state = self.validate_welcome(&welcome)?;
        let window = PeerCreditWindow::new(
            usize::try_from(state.limits.byte_credit).map_err(|_| protocol("byte credit width"))?,
            usize::try_from(state.limits.byte_credit).map_err(|_| protocol("byte credit width"))?,
        )
        .map_err(|error| protocol(error.to_string()))?;
        let admission = AdmissionController::new(
            AdmissionLimits::new(MAX_ENCODED_APPLICATION_BYTES, 1, 1)
                .map_err(|error| protocol(error.to_string()))?,
        );
        metrics::gauge!("terrabase_replication_sender_connected").set(1.0);
        let mut pending: Option<PreparedBatch> = None;
        let mut pending_since = Instant::now();
        let mut in_flight: Option<InFlight<'_>> = None;
        let mut in_flight_since = Instant::now();
        loop {
            if *shutdown.borrow() {
                return Err(AttemptError::Shutdown);
            }
            state.high_water = self.high_water()?;
            Self::update_lag(&state);
            if state.durable > state.high_water {
                return Err(frontier("observed durable prefix exceeds local high-water"));
            }
            if in_flight.is_none() && pending.is_none() && state.durable < state.high_water {
                metrics::counter!("terrabase_replication_sender_source_scans_total").increment(1);
                let document = Arc::clone(&self.document);
                let admission = admission.clone();
                let limits = state.limits;
                let identity = self.local_identity.clone();
                let sequence = state.durable;
                let scanned = tokio::task::spawn_blocking(move || {
                    prepare_next(&document, sequence, &identity, &limits, &admission)
                })
                .await
                .map_err(|error| source(format!("source scan worker failed: {error}")))??;
                if *shutdown.borrow() {
                    return Err(AttemptError::Shutdown);
                }
                if scanned.sequence > state.high_water {
                    state.high_water = self.high_water()?;
                    if scanned.sequence > state.high_water {
                        return Err(frontier("source scan returned beyond audited high-water"));
                    }
                }
                pending = Some(scanned);
                pending_since = Instant::now();
            }
            if let Some(batch) = pending.take() {
                match batch.encoded.reserve_peer_credit(&window) {
                    Ok(mut credit) => {
                        let sequence = batch.sequence;
                        let (wire_batch, batch_permit) = batch.encoded.into_parts();
                        let request = v1::SyncRequest {
                            frame: Some(v1::sync_request::Frame::CommittedGroup(wire_batch)),
                        };
                        send_request(&sender, request, shutdown).await?;
                        credit
                            .mark_sent()
                            .map_err(|error| protocol(error.to_string()))?;
                        state.last_activity = Instant::now();
                        in_flight = Some(InFlight {
                            sequence,
                            _batch: batch_permit,
                            _credit: credit,
                        });
                        in_flight_since = Instant::now();
                    }
                    Err(tb_protocol::CreditError::Exhausted) => pending = Some(batch),
                    Err(error) => return Err(protocol(error.to_string())),
                }
            }
            let progress_deadline = if in_flight.is_some() {
                in_flight_since + PROGRESS_TIMEOUT
            } else if pending.is_some() {
                pending_since + PROGRESS_TIMEOUT
            } else {
                Instant::now() + IDLE_POLL
            };
            let deadline = if pending.is_some() {
                progress_deadline
            } else {
                state
                    .pending_ping
                    .map_or(progress_deadline, |(_, sent_at)| {
                        progress_deadline.min(sent_at + KEEPALIVE_TIMEOUT)
                    })
            };
            let response = tokio::select! {
                () = wait_shutdown(shutdown) => return Err(AttemptError::Shutdown),
                () = sleep_until(deadline) => None,
                result = responses.message() => Some(result),
            };
            if let Some(result) = response {
                let frame = match result {
                    Ok(Some(response)) => response
                        .frame
                        .ok_or_else(|| protocol("empty Sync response"))?,
                    Ok(None) => return Err(AttemptError::Retry(RetryReason::Stream)),
                    Err(status) => return Err(classify_status(&status)),
                };
                self.apply_response(frame, &mut state, &window)?;
                if pending
                    .as_ref()
                    .is_some_and(|batch| state.durable >= batch.sequence)
                {
                    pending = None;
                }
                if in_flight
                    .as_ref()
                    .is_some_and(|batch| state.durable >= batch.sequence)
                {
                    in_flight = None;
                }
                continue;
            }
            if let Some(batch) = pending.as_ref()
                && pending_since.elapsed() >= PROGRESS_TIMEOUT
                && batch.encoded.credit_bytes()
                    > usize::try_from(state.limits.byte_credit)
                        .map_err(|_| protocol("byte credit width"))?
            {
                return Err(protocol("encoded source exceeds negotiated stream credit"));
            }
            if state
                .pending_ping
                .is_some_and(|(_, sent_at)| sent_at.elapsed() >= KEEPALIVE_TIMEOUT)
            {
                return Err(AttemptError::Retry(RetryReason::Timeout));
            }
            if in_flight.is_some() || pending.is_some() {
                return Err(AttemptError::Retry(RetryReason::Timeout));
            }
            if state.pending_ping.is_none() && state.last_activity.elapsed() >= KEEPALIVE_IDLE {
                let nonce = random_nonce();
                send_request(
                    &sender,
                    v1::SyncRequest {
                        frame: Some(v1::sync_request::Frame::KeepAlive(v1::KeepAlive { nonce })),
                    },
                    shutdown,
                )
                .await?;
                state.pending_ping = Some((nonce, Instant::now()));
            }
        }
    }

    fn hello(&self) -> Result<v1::Hello, AttemptError> {
        let durable = self
            .document
            .store
            .durable_prefix(self.remote_origin)
            .map_err(|error| classify_document_error(&error))?
            .sequence;
        let applied = self
            .document
            .store
            .applied_prefix(self.remote_origin)
            .map_err(|error| classify_document_error(&error))?;
        if applied > durable {
            return Err(frontier("local applied prefix exceeds local durable inbox"));
        }
        let version = |major, minor| v1::VersionRange {
            minimum: Some(v1::ProtocolVersion { major, minor }),
            maximum: Some(v1::ProtocolVersion { major, minor }),
        };
        Ok(v1::Hello {
            local_identity: Some(self.local_identity.clone()),
            expected_remote_member_id: self.remote_origin.replica_id.to_bytes().to_vec().into(),
            protocol_versions: Some(version(V1_PROTOCOL_MAJOR, V1_PROTOCOL_MINOR)),
            event_format_versions: Some(version(V1_EVENT_FORMAT, 0)),
            record_format_versions: Some(version(RECORD_FORMAT, 0)),
            minimum_merge_policy_version: MERGE_POLICY,
            maximum_merge_policy_version: MERGE_POLICY,
            codecs: vec![v1::PayloadCodec::Lz4BlockV1 as i32],
            limits: Some(
                v1_transfer_limits(MAX_IN_FLIGHT_BYTES_PER_PEER, 1, 1)
                    .map_err(|error| protocol(error.to_string()))?,
            ),
            frontiers: vec![v1::PeerFrontier {
                origin: Some(origin_wire(self.remote_origin)),
                received_sequence: Some(durable),
                durable_sequence: Some(durable),
                applied_sequence: Some(applied),
            }],
        })
    }

    fn validate_welcome(&self, welcome: &v1::Welcome) -> Result<StreamState, AttemptError> {
        if welcome.local_identity.as_ref() != Some(&self.remote_identity) {
            return Err(AttemptError::Fatal(ReplicationSenderFatal::Identity(
                "Welcome identity differs from signed remote".into(),
            )));
        }
        if welcome.protocol_version
            != Some(v1::ProtocolVersion {
                major: V1_PROTOCOL_MAJOR,
                minor: V1_PROTOCOL_MINOR,
            })
            || welcome.event_format_version != V1_EVENT_FORMAT
            || welcome.record_format_version != RECORD_FORMAT
            || welcome.merge_policy_version != MERGE_POLICY
            || welcome.codecs.len() != 1
            || welcome.codecs[0] != v1::PayloadCodec::Lz4BlockV1 as i32
        {
            return Err(protocol("Welcome selected incompatible format or codec"));
        }
        let limits = welcome
            .limits
            .as_ref()
            .ok_or_else(|| protocol("Welcome has no transfer limits"))?;
        let offered = v1_transfer_limits(MAX_IN_FLIGHT_BYTES_PER_PEER, 1, 1)
            .map_err(|error| protocol(error.to_string()))?;
        if !limits_within_offer(limits, &offered) {
            return Err(protocol("Welcome limits exceed the offered bounds"));
        }
        validate_frontiers(&welcome.frontiers).map_err(|error| protocol(error.to_string()))?;
        if welcome.frontiers.len() != 1 {
            return Err(frontier(
                "Welcome must contain exactly one local-origin frontier",
            ));
        }
        let peer_frontier = &welcome.frontiers[0];
        if peer_frontier.origin.as_ref() != Some(&origin_wire(self.local_origin)) {
            return Err(frontier("Welcome frontier has a foreign origin"));
        }
        if (peer_frontier.durable_sequence.is_some() && peer_frontier.received_sequence.is_none())
            || (peer_frontier.applied_sequence.is_some()
                && peer_frontier.durable_sequence.is_none())
        {
            return Err(frontier(
                "Welcome frontier has noncontiguous progress stages",
            ));
        }
        let durable = peer_frontier.durable_sequence.unwrap_or(0);
        let received = peer_frontier.received_sequence.unwrap_or(0);
        let applied = peer_frontier.applied_sequence.unwrap_or(0);
        if received < durable || durable < applied {
            return Err(frontier("Welcome frontier ordering is invalid"));
        }
        let high_water = self.high_water()?;
        if durable > high_water || received > high_water {
            return Err(frontier("Welcome frontier exceeds audited local source"));
        }
        Ok(StreamState {
            durable,
            applied,
            high_water,
            limits: *limits,
            pending_ping: None,
            last_activity: Instant::now(),
        })
    }

    fn apply_response(
        &self,
        frame: v1::sync_response::Frame,
        state: &mut StreamState,
        credit: &PeerCreditWindow,
    ) -> Result<(), AttemptError> {
        state.last_activity = Instant::now();
        match frame {
            v1::sync_response::Frame::Durable(ack) => {
                let sequence = self.validate_ack_context(ack.context.as_ref())?;
                state.high_water = self.high_water()?;
                if sequence == 0
                    || sequence > ack.durable_prefix
                    || ack.durable_prefix < state.durable
                    || ack.durable_prefix > state.high_water
                {
                    return Err(frontier("DurableAck is outside local committed history"));
                }
                state.durable = ack.durable_prefix;
            }
            v1::sync_response::Frame::Applied(ack) => {
                let sequence = self.validate_ack_context(ack.context.as_ref())?;
                state.high_water = self.high_water()?;
                if sequence != ack.applied_prefix
                    || ack.applied_prefix < state.applied
                    || ack.applied_prefix > state.durable
                    || ack.applied_prefix > state.high_water
                {
                    return Err(frontier("AppliedAck is outside observed durable history"));
                }
                state.applied = ack.applied_prefix;
            }
            v1::sync_response::Frame::Received(ack) => {
                let sequence = self.validate_ack_context(ack.context.as_ref())?;
                state.high_water = self.high_water()?;
                if sequence == 0
                    || sequence > ack.received_prefix
                    || ack.received_prefix > state.high_water
                {
                    return Err(frontier("ReceivedAck is outside local committed history"));
                }
            }
            v1::sync_response::Frame::Credit(update) => {
                credit
                    .apply_update(&update)
                    .map_err(|error| protocol(error.to_string()))?;
            }
            v1::sync_response::Frame::KeepAlive(response) => {
                if state.pending_ping.map(|(nonce, _)| nonce) != Some(response.nonce) {
                    return Err(protocol("unsolicited or mismatched KeepAlive response"));
                }
                state.pending_ping = None;
            }
            v1::sync_response::Frame::Error(error) => {
                if error.retryable
                    && error.code == v1::StreamErrorCode::StreamErrorResourceExhausted as i32
                {
                    return Err(AttemptError::Retry(RetryReason::Resource));
                }
                return Err(match v1::StreamErrorCode::try_from(error.code) {
                    Ok(
                        v1::StreamErrorCode::StreamErrorUnauthorized
                        | v1::StreamErrorCode::StreamErrorStaleEpoch,
                    ) => AttemptError::Fatal(ReplicationSenderFatal::Identity(
                        "peer rejected signed authority".into(),
                    )),
                    Ok(v1::StreamErrorCode::StreamErrorResnapshotRequired) => {
                        frontier("peer requires source rebootstrap")
                    }
                    _ => protocol("peer rejected Sync stream"),
                });
            }
            v1::sync_response::Frame::Welcome(_) => {
                return Err(protocol("duplicate Welcome on Sync stream"));
            }
        }
        Self::update_lag(state);
        Ok(())
    }

    fn validate_ack_context(&self, context: Option<&v1::AckContext>) -> Result<u64, AttemptError> {
        let context = context.ok_or_else(|| protocol("ACK lacks context"))?;
        if context.acknowledging_peer.as_ref() != Some(&self.remote_identity)
            || context.membership_epoch != self.local_identity.membership_epoch
            || context.configuration_id != self.local_identity.configuration_id
        {
            return Err(AttemptError::Fatal(ReplicationSenderFatal::Identity(
                "ACK context differs from signed peer or authority".into(),
            )));
        }
        let event = context
            .event_id
            .as_ref()
            .ok_or_else(|| protocol("ACK lacks event identity"))?;
        if event.database_id != self.local_identity.database_id
            || event.shard_id != self.local_identity.shard_id
            || event.origin.as_ref() != Some(&origin_wire(self.local_origin))
        {
            return Err(AttemptError::Fatal(ReplicationSenderFatal::Identity(
                "ACK event has foreign database, shard, or origin".into(),
            )));
        }
        if event.sequence == 0 {
            return Err(frontier("ACK event sequence must be positive"));
        }
        Ok(event.sequence)
    }

    fn high_water(&self) -> Result<u64, AttemptError> {
        self.document
            .store
            .source_high_water()
            .map_err(|error| classify_document_error(&error))
    }

    #[allow(
        clippy::cast_precision_loss,
        reason = "Prometheus gauges use f64; lag precision does not affect protocol decisions"
    )]
    fn update_lag(state: &StreamState) {
        metrics::gauge!("terrabase_replication_sender_durable_lag_events")
            .set(state.high_water.saturating_sub(state.durable) as f64);
        metrics::gauge!("terrabase_replication_sender_applied_lag_events")
            .set(state.high_water.saturating_sub(state.applied) as f64);
    }
}

fn prepare_next(
    document: &DocumentMemberRuntime,
    after: u64,
    local: &v1::PeerIdentity,
    limits: &v1::TransferLimits,
    admission: &AdmissionController,
) -> Result<PreparedBatch, AttemptError> {
    let expected = after
        .checked_add(1)
        .ok_or_else(|| source("source sequence overflow"))?;
    let frame = document
        .store
        .next_source_after(after)
        .map_err(|error| classify_document_error(&error))?
        .ok_or_else(|| source("retained source event missing below high-water"))?;
    if frame.event.sequence != expected {
        return Err(source("retained source event has a sequence gap"));
    }
    let metadata = decode_metadata(frame.metadata(), METADATA_LIMITS)
        .map_err(|error| source(error.to_string()))?;
    let header = metadata.header;
    if header.id.sequence() != frame.event.sequence
        || header.id.database().to_bytes().as_slice() != local.database_id.as_ref()
        || header.id.shard().get() != local.shard_id
        || header.id.origin().replica_id.to_bytes().as_slice() != local.member_id.as_ref()
        || header.id.origin().incarnation.as_slice() != local.member_incarnation.as_ref()
        || header.epoch.get() != local.membership_epoch
        || header.configuration.to_bytes().as_slice() != local.configuration_id.as_ref()
        || u32::from(header.record_format) != RECORD_FORMAT
        || u32::from(header.merge_policy) != MERGE_POLICY
    {
        return Err(source(
            "retained source metadata differs from signed local identity",
        ));
    }
    let wire = CommittedGroupMetadata {
        event_id: v1::CommittedEventId {
            database_id: local.database_id.clone(),
            shard_id: local.shard_id,
            origin: Some(origin_wire(header.id.origin())),
            sequence: frame.event.sequence,
        },
        membership_epoch: local.membership_epoch,
        configuration_id: local.configuration_id.clone(),
        transaction_id: header.transaction_id.to_bytes().to_vec().into(),
        event_format_version: V1_EVENT_FORMAT,
        record_format_version: RECORD_FORMAT,
        merge_policy_version: MERGE_POLICY,
        distributed_metadata: frame.metadata().to_vec().into(),
        change_count: metadata.changes.len(),
    };
    let mut output = Vec::new();
    let encoded = encode_committed_group(
        wire,
        frame.wal_frame(),
        &mut output,
        admission,
        &[v1::PayloadCodec::Lz4BlockV1],
        limits,
    )
    .map_err(|error| classify_encode_error(&error))?;
    if encoded.batch().event_digest_sha256.as_ref() != frame.event_digest.as_slice() {
        return Err(source("resend event digest differs from audited source"));
    }
    Ok(PreparedBatch {
        sequence: expected,
        encoded,
    })
}

fn limits_within_offer(selected: &v1::TransferLimits, offered: &v1::TransferLimits) -> bool {
    selected.max_encoded_application_bytes > 0
        && selected.max_encoded_application_bytes <= offered.max_encoded_application_bytes
        && selected.max_decoded_application_bytes > 0
        && selected.max_decoded_application_bytes <= offered.max_decoded_application_bytes
        && selected.max_transaction_group_bytes > 0
        && selected.max_transaction_group_bytes <= offered.max_transaction_group_bytes
        && selected.max_event_metadata_bytes > 0
        && selected.max_event_metadata_bytes <= offered.max_event_metadata_bytes
        && selected.max_changes_per_group > 0
        && selected.max_changes_per_group <= offered.max_changes_per_group
        && selected.max_grpc_message_bytes > 0
        && selected.max_grpc_message_bytes <= offered.max_grpc_message_bytes
        && selected.max_encoded_application_bytes
            <= selected.max_grpc_message_bytes.saturating_sub(4_096)
        && selected.max_in_flight_batches == 1
        && selected.max_open_streams == 1
        && selected.byte_credit > 0
        && selected.byte_credit <= offered.byte_credit
}

fn origin_wire(origin: OriginId) -> v1::OriginIdentity {
    v1::OriginIdentity {
        replica_id: origin.replica_id.to_bytes().to_vec().into(),
        incarnation: origin.incarnation.to_vec().into(),
    }
}

async fn send_request(
    sender: &mpsc::Sender<v1::SyncRequest>,
    request: v1::SyncRequest,
    shutdown: &mut watch::Receiver<bool>,
) -> Result<(), AttemptError> {
    tokio::select! {
        () = wait_shutdown(shutdown) => Err(AttemptError::Shutdown),
        result = timeout(PROGRESS_TIMEOUT, sender.send(request)) => {
            match result {
                Ok(Ok(())) => Ok(()),
                Ok(Err(_)) => Err(AttemptError::Retry(RetryReason::Stream)),
                Err(_) => Err(AttemptError::Retry(RetryReason::Timeout)),
            }
        }
    }
}

async fn next_response(
    inbound: &mut Streaming<v1::SyncResponse>,
    shutdown: &mut watch::Receiver<bool>,
    deadline: Instant,
) -> Result<v1::SyncResponse, AttemptError> {
    tokio::select! {
        () = wait_shutdown(shutdown) => Err(AttemptError::Shutdown),
        () = sleep_until(deadline) => Err(AttemptError::Retry(RetryReason::Timeout)),
        result = inbound.message() => {
            match result {
                Ok(Some(response)) => Ok(response),
                Ok(None) => Err(AttemptError::Retry(RetryReason::Stream)),
                Err(status) => Err(classify_status(&status)),
            }
        }
    }
}

async fn wait_shutdown(shutdown: &mut watch::Receiver<bool>) {
    while !*shutdown.borrow() {
        if shutdown.changed().await.is_err() {
            break;
        }
    }
}

fn classify_document_error(error: &DocumentError) -> AttemptError {
    match error {
        DocumentError::Admission(_)
        | DocumentError::Storage(tb_storage::StorageError::Admission(_)) => {
            AttemptError::Retry(RetryReason::Resource)
        }
        _ => source(error.to_string()),
    }
}

fn classify_encode_error(error: &ProtocolError) -> AttemptError {
    match error {
        ProtocolError::Admission(_)
        | ProtocolError::ProcessAdmission(_)
        | ProtocolError::ProcessHeadroomRejected => AttemptError::Retry(RetryReason::Resource),
        _ => source(error.to_string()),
    }
}

fn classify_status(status: &tonic::Status) -> AttemptError {
    match status.code() {
        Code::ResourceExhausted => AttemptError::Retry(RetryReason::Resource),
        Code::DeadlineExceeded => AttemptError::Retry(RetryReason::Timeout),
        Code::Unauthenticated | Code::PermissionDenied => {
            AttemptError::Fatal(ReplicationSenderFatal::Identity(status.to_string()))
        }
        Code::InvalidArgument | Code::DataLoss | Code::FailedPrecondition => {
            protocol(status.to_string())
        }
        _ => AttemptError::Retry(RetryReason::Stream),
    }
}

fn classify_transport_error(error: &tonic::transport::Error) -> AttemptError {
    let mut source: Option<&(dyn std::error::Error + 'static)> = Some(error);
    while let Some(current) = source {
        if let Some(tls) = current.downcast_ref::<rustls::Error>() {
            return AttemptError::Fatal(ReplicationSenderFatal::Identity(tls.to_string()));
        }
        source = current.source();
    }
    AttemptError::Retry(RetryReason::Connect)
}

fn source(message: impl Into<String>) -> AttemptError {
    AttemptError::Fatal(ReplicationSenderFatal::Source(message.into()))
}

fn protocol(message: impl Into<String>) -> AttemptError {
    AttemptError::Fatal(ReplicationSenderFatal::Protocol(message.into()))
}

fn frontier(message: impl Into<String>) -> AttemptError {
    AttemptError::Fatal(ReplicationSenderFatal::Frontier(message.into()))
}

fn random_nonce() -> u64 {
    let mut bytes = [0; 8];
    if getrandom::fill(&mut bytes).is_ok() {
        u64::from_le_bytes(bytes)
    } else {
        1
    }
}

fn jitter(base: Duration) -> Duration {
    let mut bytes = [0; 8];
    if getrandom::fill(&mut bytes).is_err() {
        return base;
    }
    let percent = 80 + u64::from_le_bytes(bytes) % 41;
    let millis = u64::try_from(base.as_millis()).unwrap_or(15_000);
    Duration::from_millis(millis.saturating_mul(percent) / 100)
}
