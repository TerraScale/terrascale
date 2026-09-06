//! The cross-region lanes over the fault-injectable member transport
//! (S-05 slice 2b, D-35/D-36/D-45): one [`RegionLink`] per (member, peer)
//! drives the journal tailer's proven pull/apply path through a real
//! [`MemberTransport`] lane.
//!
//! The semantics are exactly [`crate::replication`]'s (slice 2a, pinned by
//! `tests/replication.rs`): the pull filters the durable journal to
//! `kind='origin'` rows so replicated rows never re-ship (loop prevention),
//! the apply binds each record to the deterministic `repl-{commit_ulid}`
//! identity so a duplicate delivery replays instead of double-writing, and
//! concurrent writes converge by the deterministic `(hlc, origin)` merge.
//! This module adds the transport mechanics around that path:
//!
//! - **One frame per pull** (M1: one frame per pump tick): each pull of
//!   origin records is encoded as one member envelope
//!   ([`FrameKind::ReplicationBatch`], envelope minor 2) — the D-45 "one
//!   frame per group-commit wave per peer" shape, with a bounded record
//!   count per frame.
//! - **Credit window** (M1): a bounded count of in-flight frames per
//!   direction. Sending skips when the window is full or the lane reports
//!   [`TransportError::LinkFull`], and the journal cursor advances only
//!   through the contiguously acknowledged prefix — a frame lost under a
//!   partition or drop control never advances the cursor, so healing
//!   re-pulls it and the apply replays idempotently. Frames carry a
//!   lane-tick expiry; an expired unacknowledged frame releases its window
//!   credit and rolls the outbound state back to it.
//! - **Acknowledgement**: the receiver applies through
//!   [`crate::replication::apply_records`] and answers with the existing
//!   batched report ([`FrameKind::AckBatch`]) correlated to the batch's
//!   message identity; only durable holdings are reported.
//!
//! This fault-injectable lane is used by simulation and loopback tests.
//! Production uses [`crate::production_causal`] on authenticated admitted
//! `ProductionQuicEndpoint` connections supplied by the member runtime.

use std::collections::{BTreeMap, BTreeSet, VecDeque};

use terrabase::engine::ReplicatedRecord;
use terrabase::streaming::ChangeCheckpoint;
use terrabase::types::DocumentValue;
use terrabase_member_transport::clock::LaneInstant;
use terrabase_member_transport::envelope::{
    EncodedFrame, Envelope, EnvelopeError, FrameKind, LaneValue, Payload, ReplicatedWrite, decode,
    encode,
};
use terrabase_member_transport::identity::{
    CollectionName as LaneCollection, DocumentId as LaneDocumentId, MemberId, OperationId,
    TenantId as LaneTenantId,
};
use terrabase_member_transport::transport::{FrameSelector, MemberTransport, TransportError};

use crate::engine_host::EngineHost;
use crate::host::HostingError;
use crate::registry::BoundScope;

/// The bounded record count one replication frame may carry: the M1 form
/// of D-45's one-frame-per-wave shape. The engine's own replay page bound
/// ([`terrabase::streaming::MAX_STREAM_BATCH_ITEMS`]) is the same size, so
/// one pull is at most one frame unless the payload byte bound splits it.
pub const MAX_RECORDS_PER_FRAME: usize = 1024;

/// The credit window (M1): the bounded count of frames one direction may
/// hold in flight before sending skips. D-45's pipelining — never wait for
/// wave *k*'s ack before sending *k+1* — holds up to this bound.
pub const MAX_INFLIGHT_FRAMES: usize = 8;

/// The maximum number of journal pages one ship tick may scan when every
/// page contains only replicated rows. The cursor advances after each such
/// page, so a larger replicated prefix is handled by later bounded ticks.
pub const MAX_FILTERED_PAGE_SCANS: usize = 8;

/// The lane-tick expiry one shipped frame carries. An unacknowledged frame
/// past its expiry is treated as lost: it releases its window credit, the
/// outbound state rolls back to it, and the next pull re-ships its records
/// (the apply replays them idempotently).
pub const FRAME_EXPIRY_TICKS: u64 = 64;

/// Maximum number of complete replication frames a receiver may retain while
/// waiting for an earlier per-origin sequence.  Overflow is explicit: the
/// caller must run anti-entropy rather than silently skipping a dependency.
pub const MAX_PENDING_FRAMES: usize = MAX_INFLIGHT_FRAMES * 8;

/// The bounded history used to distinguish a delayed duplicate report from a
/// report fabricated for a message identity this link never issued.
const MAX_RETIRED_MESSAGE_IDS: usize = MAX_INFLIGHT_FRAMES * 8;

/// One frame the link holds in flight toward its peer.
struct InFlightFrame {
    /// The journal cursor after this frame's records: the cursor the link
    /// adopts once every earlier frame is acknowledged.
    checkpoint: Option<ChangeCheckpoint>,
    /// The lane tick after which an unacknowledged frame is lost.
    expiry: u64,
    /// Whether the peer's batched report confirmed the frame.
    acknowledged: bool,
    /// The tenant bound to the frame, used to authenticate its report.
    tenant: LaneTenantId,
    /// The exact ordered operation identities carried by the frame. An ack
    /// is meaningful only when it repeats this list byte-for-byte: the
    /// correlation id alone does not prove which operations were durable.
    expected_operations: Vec<OperationId>,
}

/// The result of attempting one inbound frame.  A deferred frame remains
/// encoded so its authenticated envelope and exact operation set are what we
/// eventually acknowledge.
enum ReceiveFrame {
    Applied(usize),
    Deferred(EncodedFrame),
}

/// The outcome of one outbound shipping tick.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ShipStep {
    /// The journal past the cursor holds no origin records.
    Idle,
    /// The credit window is full or the lane reported it at capacity; no
    /// frame was sent and the cursor did not move.
    WindowFull,
    /// One frame of this many records left the member.
    Shipped(usize),
}

/// One member's replication lane toward one peer region: the journal
/// tailer's shipping side and the inbound apply/ack side for the same
/// directed pair.
///
/// The link keeps its journal cursor in memory only: the engines are
/// durable and the apply is idempotent, so a restarted link re-pulls from
/// the journal's start and every already-applied record replays — the
/// catch-up shape `tests/replication_lanes.rs` pins.
pub struct RegionLink {
    local: MemberId,
    peer: MemberId,
    cursor: Option<ChangeCheckpoint>,
    in_flight: BTreeMap<u64, InFlightFrame>,
    retired_message_ids: BTreeSet<u64>,
    /// Frames that arrived ahead of an earlier sequence for one of their
    /// origins.  Keeping complete frames preserves the sender's exact ack
    /// contract and makes duplicate delivery harmless.
    pending_frames: VecDeque<EncodedFrame>,
    /// Highest contiguous origin sequence applied on this receive lane.
    /// Equal sequence values are valid because one commit may contain more
    /// than one write; a jump is held until the missing sequence arrives.
    received_origin_sequences: BTreeMap<String, u64>,
    received_scope: Option<BoundScope>,
    next_message_id: u64,
}

impl RegionLink {
    /// Creates the directed link from one member identity to one peer.
    ///
    /// # Errors
    ///
    /// Returns [`HostingError::Invalid`] when either identity breaks the
    /// member-identifier rules.
    pub fn new(local: &str, peer: &str) -> Result<Self, HostingError> {
        Ok(Self {
            local: member_id(local)?,
            peer: member_id(peer)?,
            cursor: None,
            in_flight: BTreeMap::new(),
            retired_message_ids: BTreeSet::new(),
            pending_frames: VecDeque::new(),
            received_origin_sequences: BTreeMap::new(),
            received_scope: None,
            next_message_id: 1,
        })
    }

    /// This link's local member identity.
    #[must_use]
    pub fn local(&self) -> &MemberId {
        &self.local
    }

    /// This link's peer member identity.
    #[must_use]
    pub fn peer(&self) -> &MemberId {
        &self.peer
    }

    /// The journal cursor the link resumes from.
    #[must_use]
    pub fn cursor(&self) -> Option<ChangeCheckpoint> {
        self.cursor
    }

    /// Adopts the journal cursor a snapshot bootstrap recovered (see
    /// [`crate::anti_entropy::journal_end`]): the link resumes past the
    /// snapshot instead of re-shipping what the copied file set already
    /// serves. In-flight frames belong to the old cursor and are dropped —
    /// the apply replays them idempotently if they ever arrive.
    pub fn set_cursor(&mut self, cursor: Option<ChangeCheckpoint>) {
        self.cursor = cursor;
        self.in_flight.clear();
        self.retired_message_ids.clear();
        self.pending_frames.clear();
        self.received_origin_sequences.clear();
        self.received_scope = None;
    }

    /// The frames this link currently holds in flight.
    #[must_use]
    pub fn in_flight(&self) -> usize {
        self.in_flight.len()
    }

    /// Whether the directed lane toward the peer is currently partitioned
    /// (the transport's own control): the lane-level liveness fact the
    /// public API's `GlobalDurable` rule (at least two active regions)
    /// consumes once S-09 wires the API to the lanes.
    #[must_use]
    pub fn peer_partitioned(&self, lane: &mut dyn MemberTransport) -> bool {
        lane.link(&self.local, &self.peer).partition
    }

    /// One outbound tick (M1: one frame per pump tick): pull one bounded
    /// page of this member's origin journal rows and ship the first frame
    /// of it toward the peer. The cursor advances only through acknowledged
    /// origin frames. Filtered-only pages may advance it because they hold no
    /// records that could need re-shipping. A skipped, dropped, or expired
    /// frame leaves its origin records to be pulled again.
    ///
    /// # Errors
    ///
    /// Returns [`HostingError`] when the journal pull fails or a record
    /// cannot cross the lane identity or payload bounds.
    pub fn ship(
        &mut self,
        host: &EngineHost,
        scope: &BoundScope,
        lane: &mut dyn MemberTransport,
    ) -> Result<ShipStep, HostingError> {
        self.settle(lane.now().ticks());
        if self.in_flight.len() >= MAX_INFLIGHT_FRAMES {
            return Ok(ShipStep::WindowFull);
        }
        let mut page = Vec::new();
        let mut after =
            self.in_flight.last_key_value().and_then(|(_, frame)| frame.checkpoint).or(self.cursor);
        for _ in 0..MAX_FILTERED_PAGE_SCANS {
            let (candidate, next) = pull_origin_page(host, scope, after)?;
            if !candidate.is_empty() {
                page = candidate;
                break;
            }
            let Some(next) = next else {
                return Ok(ShipStep::Idle);
            };
            if after.is_some_and(|previous| !checkpoint_after(next, previous)) {
                return Err(HostingError::EngineFailure(
                    "the durable journal pull did not advance its checkpoint".into(),
                ));
            }
            if self.in_flight.is_empty() {
                self.cursor = Some(next);
            }
            after = Some(next);
        }
        if page.is_empty() {
            return Ok(ShipStep::Idle);
        }
        let records = page
            .iter()
            .map(|(position, record)| Ok((Some(*position), replicated_write(record)?)))
            .collect::<Result<Vec<_>, HostingError>>()?;
        let now = lane.now().ticks();
        let tenant = LaneTenantId::try_new(&scope.tenant)
            .map_err(|_| HostingError::Invalid("the tenant breaks the lane rules".into()))?;
        let (frame, checkpoint, shipped) = encode_first_frame(
            &self.local,
            &self.peer,
            now,
            self.next_message_id,
            tenant.as_str(),
            &records,
        )?;
        let expected_operations = records[..shipped]
            .iter()
            .map(|(_, write)| {
                OperationId::try_new(&write.commit_ulid).map_err(|_| {
                    HostingError::Invalid(
                        "the replicated operation identity breaks the lane rules".into(),
                    )
                })
            })
            .collect::<Result<Vec<_>, _>>()?;
        match lane.send(&self.local, &self.peer, frame) {
            Ok(()) => {}
            // The lane's bounded queue is the peer-not-drained signal:
            // skip this tick without touching the cursor or the window.
            Err(TransportError::LinkFull) => return Ok(ShipStep::WindowFull),
        }
        let message_id = self.next_message_id;
        self.next_message_id += 1;
        self.in_flight.insert(
            message_id,
            InFlightFrame {
                checkpoint,
                expiry: now.saturating_add(FRAME_EXPIRY_TICKS),
                acknowledged: false,
                tenant,
                expected_operations,
            },
        );
        Ok(ShipStep::Shipped(shipped))
    }

    /// One inbound tick: drain the replication batches addressed to this
    /// member, apply each through the proven apply path, and answer every
    /// applied batch with one batched report correlated to its message
    /// identity. Returns how many records this pass applied or replayed.
    ///
    /// # Errors
    ///
    /// Returns [`HostingError`] when a frame fails the envelope
    /// discipline, names another member or tenant, or its records fail to
    /// apply.
    pub fn receive(
        &mut self,
        host: &EngineHost,
        scope: &BoundScope,
        lane: &mut dyn MemberTransport,
    ) -> Result<usize, HostingError> {
        let engine_scope = host.engine_scope_of(scope)?;
        if self.received_scope.as_ref().is_some_and(|previous| previous != scope) {
            return Err(HostingError::Refused("the receive link is bound to another scope".into()));
        }
        if self.received_scope.is_none() {
            self.received_origin_sequences =
                host.engine().durable_origin_prefixes(&engine_scope).map_err(|error| {
                    HostingError::EngineFailure(format!(
                        "the receive frontier could not be recovered: {error:?}"
                    ))
                })?;
            self.received_scope = Some(scope.clone());
        }
        // Include frames held from a previous pass before newly delivered
        // frames.  A scan may still bypass a blocked frame so an earlier
        // sequence that arrived later can unblock the queue.
        let mut frames = std::mem::take(&mut self.pending_frames);
        frames.extend(lane.receive(&self.local, FrameSelector::Deliveries));
        let mut applied = 0;
        let mut deferred = VecDeque::new();
        let mut made_progress = true;
        while made_progress && !frames.is_empty() {
            made_progress = false;
            while let Some(frame) = frames.pop_front() {
                match self.receive_frame(host, scope, lane, frame)? {
                    ReceiveFrame::Applied(count) => {
                        applied += count;
                        made_progress = true;
                    }
                    ReceiveFrame::Deferred(frame) => deferred.push_back(frame),
                }
            }
            // A frame deferred in this pass may become ready after a later
            // frame in the same pass applied its predecessor.
            frames.append(&mut deferred);
        }
        // No frame can currently make progress: retain the bounded suffix for
        // a later pump.  Anti-entropy is the recovery path once this bound is
        // exceeded; refusing here is safer than acknowledging a causal skip.
        if !frames.is_empty() {
            if frames.len() > MAX_PENDING_FRAMES {
                return Err(HostingError::Refused(
                    "replication dependency queue is full; reconciliation required".into(),
                ));
            }
            self.pending_frames = frames;
        }
        Ok(applied)
    }

    /// Validates and applies one frame.  A frame with a missing per-origin
    /// predecessor is returned intact to the bounded receive queue.
    fn receive_frame(
        &mut self,
        host: &EngineHost,
        scope: &BoundScope,
        lane: &mut dyn MemberTransport,
        frame: EncodedFrame,
    ) -> Result<ReceiveFrame, HostingError> {
        let (envelope, _) = decode(&frame.bytes).map_err(lane_error)?;
        if envelope.target != self.local {
            return Err(HostingError::Refused("the frame is addressed to another member".into()));
        }
        if envelope.source != self.peer {
            return Err(HostingError::Refused("the frame is sourced by another member".into()));
        }
        if envelope.expires_at.is_some_and(|at| at <= lane.now()) {
            // Lost in transit by the lane's own clock: never applied,
            // never answered — the sender expires it and re-pulls.
            return Ok(ReceiveFrame::Applied(0));
        }
        let Payload::ReplicationBatch { tenant, records } = envelope.payload else {
            return Err(HostingError::Refused(
                "the deliveries selector drained a non-replication frame".into(),
            ));
        };
        if tenant.as_str() != scope.tenant {
            return Err(HostingError::Refused("the replication batch names another tenant".into()));
        }
        let batch = records
            .iter()
            .map(replicated_record)
            .collect::<Result<Vec<ReplicatedRecord>, HostingError>>()?;
        if batch.iter().any(|record| record.origin.is_empty() || record.origin_sequence == 0) {
            return Err(HostingError::Invalid(
                "the replicated record has no valid origin sequence".into(),
            ));
        }
        if !self.origin_sequences_ready(&batch) {
            return Ok(ReceiveFrame::Deferred(frame));
        }
        let applied = crate::replication::apply_records(host, scope, &batch)?;
        self.record_origin_sequences(&batch);
        let reports = batch
            .iter()
            .map(|record| {
                Ok((
                    OperationId::try_new(&record.commit_ulid).map_err(|_| {
                        HostingError::Invalid("the commit identity breaks the lane rules".into())
                    })?,
                    true,
                ))
            })
            .collect::<Result<Vec<_>, HostingError>>()?;
        let ack = Envelope {
            kind: FrameKind::AckBatch,
            message_id: self.next_message_id,
            correlation_id: Some(envelope.message_id),
            causation_id: None,
            idempotency_key: None,
            source: self.local.clone(),
            target: envelope.source.clone(),
            issued_at: lane.now(),
            expires_at: None,
            range: None,
            epoch: None,
            payload: Payload::AckBatch { tenant, reports },
        };
        self.next_message_id += 1;
        // A lane at capacity defers the report rather than dropping
        // the applied batch: the sender re-pulls and the apply replays.
        let _ = lane.send(&self.local, &envelope.source, encode(&ack).map_err(lane_error)?);
        Ok(ReceiveFrame::Applied(applied))
    }

    /// Returns whether every origin in `batch` is either already applied or
    /// starts exactly at its next sequence.  A commit can contribute multiple
    /// rows with one sequence, hence the non-decreasing comparison.
    fn origin_sequences_ready(&self, batch: &[ReplicatedRecord]) -> bool {
        let mut temporary = self.received_origin_sequences.clone();
        for record in batch {
            if record.origin.is_empty() || record.origin_sequence == 0 {
                return false;
            }
            let current = temporary.get(&record.origin).copied().unwrap_or(0);
            if record.origin_sequence > current.saturating_add(1) {
                return false;
            }
            if record.origin_sequence > current {
                temporary.insert(record.origin.clone(), record.origin_sequence);
            }
        }
        true
    }

    fn record_origin_sequences(&mut self, batch: &[ReplicatedRecord]) {
        for record in batch {
            let current = self.received_origin_sequences.get(&record.origin).copied().unwrap_or(0);
            if record.origin_sequence > current {
                self.received_origin_sequences
                    .insert(record.origin.clone(), record.origin_sequence);
            }
        }
    }

    /// Drains the batched reports addressed to this member and advances
    /// the journal cursor through every contiguously acknowledged frame.
    /// Returns how many frames the cursor advanced past.
    ///
    /// # Errors
    ///
    /// Returns [`HostingError`] when a report fails the envelope
    /// discipline or names another member.
    pub fn drain_acks(&mut self, lane: &mut dyn MemberTransport) -> Result<usize, HostingError> {
        let frames = lane.receive(&self.local, FrameSelector::Reports);
        let mut reports = Vec::new();
        for frame in frames {
            let (envelope, _) = decode(&frame.bytes).map_err(lane_error)?;
            if envelope.target != self.local {
                return Err(HostingError::Refused(
                    "the frame is addressed to another member".into(),
                ));
            }
            if envelope.source != self.peer {
                return Err(HostingError::Refused(
                    "the report is sourced by another member".into(),
                ));
            }
            let Payload::AckBatch { tenant, reports: batch } = envelope.payload else {
                return Err(HostingError::Refused(
                    "the reports selector drained a non-report frame".into(),
                ));
            };
            reports.push((envelope.correlation_id, tenant, batch));
        }
        for (correlation, tenant, batch) in reports {
            let Some(message_id) = correlation else {
                return Err(HostingError::Refused(
                    "the acknowledgement has no in-flight frame correlation".into(),
                ));
            };
            let Some(frame_state) = self.in_flight.get_mut(&message_id) else {
                if self.retired_message_ids.contains(&message_id) {
                    // A duplicate of a report already settled is harmless,
                    // but an unknown correlation is not an acknowledgement.
                    continue;
                }
                return Err(HostingError::Refused(
                    "the acknowledgement names no in-flight frame".into(),
                ));
            };
            if tenant != frame_state.tenant {
                return Err(HostingError::Refused("the report names another tenant".into()));
            }
            if batch.is_empty() || batch.len() != frame_state.expected_operations.len() {
                return Err(HostingError::Refused(
                    "the acknowledgement does not cover exactly one in-flight frame".into(),
                ));
            }
            let mut seen = BTreeSet::new();
            if batch.iter().any(|(operation, _)| !seen.insert(operation)) {
                return Err(HostingError::Refused(
                    "the acknowledgement repeats an operation identity".into(),
                ));
            }
            if batch
                .iter()
                .zip(&frame_state.expected_operations)
                .any(|((operation, _), expected)| operation != expected)
            {
                return Err(HostingError::Refused(
                    "the acknowledgement operation identities do not match the frame".into(),
                ));
            }
            // Any non-durable holding voids the frame's advance: the
            // sender treats the frame as lost and re-pulls its records.
            if batch.iter().all(|(_, held)| *held) {
                frame_state.acknowledged = true;
            } else {
                frame_state.expiry = 0;
            }
        }
        Ok(self.settle(lane.now().ticks()))
    }

    /// Releases expired frames (rolling the outbound state back to the
    /// oldest loss), adopts the cursor of the contiguously acknowledged
    /// prefix, and returns how many frames the cursor advanced past.
    fn settle(&mut self, now: u64) -> usize {
        // Roll back to the oldest lost frame: everything at or after it is
        // re-pulled from the un-advanced cursor and re-shipped under new
        // message identities (the apply replays idempotently).
        let lost = self
            .in_flight
            .iter()
            .find(|(_, frame)| frame.expiry <= now && !frame.acknowledged)
            .map(|(message_id, _)| *message_id);
        if let Some(lost) = lost {
            let retired: Vec<u64> =
                self.in_flight.range(lost..).map(|(message_id, _)| *message_id).collect();
            self.in_flight.retain(|message_id, _| *message_id < lost);
            for message_id in retired {
                self.retire_message_id(message_id);
            }
        }
        let mut advanced = 0;
        while let Some((_, frame)) = self.in_flight.first_key_value()
            && frame.acknowledged
        {
            self.cursor = frame.checkpoint;
            let key = *self.in_flight.first_key_value().map(|(key, _)| key).expect("checked");
            self.in_flight.remove(&key);
            self.retire_message_id(key);
            advanced += 1;
        }
        advanced
    }

    fn retire_message_id(&mut self, message_id: u64) {
        self.retired_message_ids.insert(message_id);
        while self.retired_message_ids.len() > MAX_RETIRED_MESSAGE_IDS {
            let oldest = *self.retired_message_ids.first().expect("the set is nonempty");
            self.retired_message_ids.remove(&oldest);
        }
    }
}

/// One bounded origin page: positioned records plus the cursor past them.
type OriginPage = (Vec<(ChangeCheckpoint, ReplicatedRecord)>, Option<ChangeCheckpoint>);

/// Pulls one bounded page of origin journal rows with the durable cursor
/// after each record. The returned checkpoint is after every row examined,
/// including filtered replicated rows, while each returned record retains its
/// own position for the frame checkpoint. This is
/// [`crate::replication::pull_origin_records`]'s `kind='origin'` filter that
/// prevents shipping loops, extended with the per-record positions the frame
/// checkpoints need.
fn pull_origin_page(
    host: &EngineHost,
    scope: &BoundScope,
    after: Option<ChangeCheckpoint>,
) -> Result<OriginPage, HostingError> {
    let batch = host.replay_sync_journal(scope, after)?;
    let mut page = Vec::new();
    for record in batch.records() {
        let provenance = record.provenance();
        if provenance.kind() != terrabase::streaming::JournalKind::Origin {
            continue;
        }
        let (body, deleted) = match record.mutation() {
            terrabase::streaming::SyncJournalMutation::Put(value) => (value.clone(), false),
            terrabase::streaming::SyncJournalMutation::Delete => (DocumentValue::Null, true),
        };
        page.push((
            record.position(),
            ReplicatedRecord {
                collection: record.collection().clone(),
                id: record.document().clone(),
                body,
                deleted,
                commit_ulid: record.operation_id().as_str().to_owned(),
                hlc: provenance.hlc(),
                origin: provenance.origin().to_owned(),
                origin_sequence: provenance.origin_sequence(),
                // The transport carries the durable frontier opaquely. Keep
                // its canonical bytes unchanged until the receiving engine
                // validates and joins them.
                context: provenance.context().to_vec(),
            },
        ));
    }
    Ok((page, batch.next_checkpoint()))
}

fn checkpoint_after(next: ChangeCheckpoint, previous: ChangeCheckpoint) -> bool {
    (next.sequence(), next.write_index()) > (previous.sequence(), previous.write_index())
}

/// Encodes the first frame of a pulled page: the longest prefix of
/// `records` that fits one envelope (the payload bound is the splitter),
/// returning the encoded frame, the cursor after the prefix's last record,
/// and the record count shipped.
fn encode_first_frame(
    local: &MemberId,
    peer: &MemberId,
    now: u64,
    message_id: u64,
    tenant: &str,
    records: &[(Option<ChangeCheckpoint>, ReplicatedWrite)],
) -> Result<(EncodedFrame, Option<ChangeCheckpoint>, usize), HostingError> {
    let tenant = LaneTenantId::try_new(tenant)
        .map_err(|_| HostingError::Invalid("the tenant breaks the lane rules".into()))?;
    let mut high = records.len();
    let mut low = 1;
    while low < high {
        let middle = (low + high).div_ceil(2);
        if replication_envelope(local, peer, now, message_id, &tenant, &records[..middle]).is_ok() {
            low = middle;
        } else {
            high = middle - 1;
        }
    }
    let frame = replication_envelope(local, peer, now, message_id, &tenant, &records[..low])
        .map_err(|error| match error {
            EnvelopeError::Oversized | EnvelopeError::FieldTooLarge => HostingError::Invalid(
                "one replicated record exceeds the member-envelope payload bound".into(),
            ),
            other => HostingError::EngineFailure(format!("the lane encode failed: {other:?}")),
        })?;
    let checkpoint = records[low - 1].0;
    Ok((frame, checkpoint, low))
}

fn replication_envelope(
    local: &MemberId,
    peer: &MemberId,
    now: u64,
    message_id: u64,
    tenant: &LaneTenantId,
    records: &[(Option<ChangeCheckpoint>, ReplicatedWrite)],
) -> Result<EncodedFrame, EnvelopeError> {
    encode(&Envelope {
        kind: FrameKind::ReplicationBatch,
        message_id,
        correlation_id: None,
        causation_id: None,
        idempotency_key: None,
        source: local.clone(),
        target: peer.clone(),
        issued_at: LaneInstant::at_ticks(now),
        expires_at: Some(LaneInstant::at_ticks(now + FRAME_EXPIRY_TICKS)),
        range: None,
        epoch: None,
        payload: Payload::ReplicationBatch {
            tenant: tenant.clone(),
            records: records.iter().map(|(_, write)| write.clone()).collect(),
        },
    })
}

/// Converts one engine record into its lane form.
pub(crate) fn replicated_write(record: &ReplicatedRecord) -> Result<ReplicatedWrite, HostingError> {
    Ok(ReplicatedWrite {
        collection: LaneCollection::try_new(record.collection.as_str())
            .map_err(|_| HostingError::Invalid("the collection breaks the lane rules".into()))?,
        id: LaneDocumentId::try_new(record.id.as_str())
            .map_err(|_| HostingError::Invalid("the document breaks the lane rules".into()))?,
        body: if record.deleted { None } else { Some(lane_value(&record.body)?) },
        commit_ulid: record.commit_ulid.clone(),
        hlc: record.hlc,
        origin: record.origin.clone(),
        origin_sequence: record.origin_sequence,
        context: record.context.clone(),
    })
}

/// Converts one lane record back into the engine's form.
pub(crate) fn replicated_record(write: &ReplicatedWrite) -> Result<ReplicatedRecord, HostingError> {
    Ok(ReplicatedRecord {
        collection: terrabase::types::CollectionName::try_new(write.collection.as_str())
            .map_err(|_| HostingError::Invalid("the collection is not valid".into()))?,
        id: terrabase::types::DocumentId::try_new(write.id.as_str())
            .map_err(|_| HostingError::Invalid("the document is not valid".into()))?,
        body: match &write.body {
            Some(value) => document_value(value)?,
            None => DocumentValue::Null,
        },
        deleted: write.body.is_none(),
        commit_ulid: write.commit_ulid.clone(),
        hlc: write.hlc,
        origin: write.origin.clone(),
        origin_sequence: write.origin_sequence,
        context: write.context.clone(),
    })
}

/// Converts one engine value into its lane form.
fn lane_value(value: &DocumentValue) -> Result<LaneValue, HostingError> {
    let converted = match value {
        DocumentValue::Null => LaneValue::Null,
        DocumentValue::Boolean(flag) => LaneValue::Boolean(*flag),
        DocumentValue::Integer(number) => LaneValue::Integer(*number),
        DocumentValue::Number(number) => LaneValue::Number(*number),
        DocumentValue::String(text) => LaneValue::String(text.clone()),
        DocumentValue::Array(items) => {
            LaneValue::Array(items.iter().map(lane_value).collect::<Result<Vec<_>, _>>()?)
        }
        DocumentValue::Object(fields) => LaneValue::Object(
            fields
                .iter()
                .map(|(name, value)| Ok((name.clone(), lane_value(value)?)))
                .collect::<Result<Vec<_>, HostingError>>()?,
        ),
    };
    Ok(converted)
}

/// Converts one lane value back into the engine's form.
fn document_value(value: &LaneValue) -> Result<DocumentValue, HostingError> {
    let converted = match value {
        LaneValue::Null => DocumentValue::Null,
        LaneValue::Boolean(flag) => DocumentValue::Boolean(*flag),
        LaneValue::Integer(number) => DocumentValue::Integer(*number),
        LaneValue::Number(number) => DocumentValue::Number(*number),
        LaneValue::String(text) => DocumentValue::String(text.clone()),
        LaneValue::Array(items) => {
            DocumentValue::Array(items.iter().map(document_value).collect::<Result<Vec<_>, _>>()?)
        }
        LaneValue::Object(fields) => DocumentValue::Object(
            fields
                .iter()
                .map(|(name, value)| Ok((name.clone(), document_value(value)?)))
                .collect::<Result<std::collections::BTreeMap<String, DocumentValue>, HostingError>>(
                )?,
        ),
    };
    Ok(converted)
}

fn member_id(value: &str) -> Result<MemberId, HostingError> {
    MemberId::try_new(value)
        .map_err(|_| HostingError::Invalid("the member identity breaks the lane rules".into()))
}

fn lane_error(error: EnvelopeError) -> HostingError {
    HostingError::Refused(format!("the member frame failed the lane discipline: {error:?}"))
}
