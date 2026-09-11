//! The member driver: the W4 data plane over engine-owned transports.
//!
//! One [`LaneMember`] owns one durable [`MemberJournal`], applies one
//! validated membership view, and moves committed operations between members
//! through encoded member envelopes. The semantics are the accepted W4
//! plane's, re-run over real transports (SE-06):
//!
//! - D-1: admission fencing through topology ownership; apply-time epoch
//!   checks on pushed batches; deposed-owner writes retained as conflict
//!   history that keep their local receipts.
//! - D-2: the acknowledgement wait as a separate, explicitly driven step
//!   after the local commit; a Member contributes only through its own
//!   durable ledger; degenerate levels normalized and recorded; timeouts
//!   reported with the partial acknowledgement.
//! - D-3: a bounded hybrid logical clock per member; deterministic
//!   last-writer-wins convergence; losers retained as content-free
//!   [`ConflictRecord`] history.
//!
//! What the real network adds: the acknowledgement observation crosses the
//! wire — every received push is answered, after its apply pass, by
//! acknowledgement-report envelopes, and the writer counts contributing
//! Members from those reports — and every inbound frame passes the
//! fail-closed envelope discipline (decode, target, expiry) before any
//! protocol logic sees it. Duplicated delivery is still resolved by
//! operation identity through the durable ledger, never by de-duplicating
//! delivery.

use std::collections::{BTreeMap, BTreeSet};

use crate::clock::{ClockStamp, LaneInstant};
use crate::envelope::{
    EncodedFrame, Envelope, EnvelopeError, FrameKind, LaneCommit, LaneDocument, LaneWrite, Payload,
    encode,
};
use crate::identity::{
    DocumentId, MemberId, OperationId, RegionId, TenantId, TopologyView, validate_identifier,
};
use crate::journal::{DurableResolution, JournalError, LaneCursor, MemberJournal, RecordOutcome};
use crate::transport::{FrameSelector, MemberTransport, TransportError};

/// The bounded number of commits one catch-up round requests by default.
pub const DEFAULT_CATCH_UP_BATCH: u32 = 32;

/// The bounded number of tenant-isolation denials one member retains.
pub const MAX_DENIALS: usize = 64;

/// The bounded number of conflict records one member retains.
pub const MAX_CONFLICTS: usize = 64;

/// The bounded number of entries each remaining observation log retains.
pub const MAX_OBSERVATION_LOGS: usize = 64;

/// The clock-skew bound B of the D-3 conflict policy, in lane ticks.
///
/// Derivation (a lane-scoped fact, no production claim): the lane clock
/// advances exactly one tick per pump and a member refreshes its clock at
/// every transport-touching call, so the maximum skew a correct lane member
/// can show against the member observing it is one tick. The bound is four
/// ticks: a stated four-times margin, erring toward classifying genuine
/// reorderings as conflicts rather than trusting a clock that cannot be
/// trusted.
pub const MAX_CLOCK_SKEW_TICKS: u64 = 4;

/// The acknowledgement level of one replicated operation.
#[derive(Clone, Copy, Debug, Eq, Hash, PartialEq)]
pub enum AcknowledgementLevel {
    /// The local journal publication completed; no remote Member confirmed
    /// anything.
    Local,
    /// A quorum of Members in the write's home Region durably recorded the
    /// operation.
    RegionQuorum,
    /// A majority of Regions each durably recorded the operation through a
    /// Region quorum.
    MajorityRegions,
    /// Every Region in the topology durably recorded the operation.
    AllRegions,
}

impl Default for AcknowledgementLevel {
    /// Returns [`AcknowledgementLevel::Local`]: the local save stays the
    /// default and every stronger level an explicit per-request input.
    fn default() -> Self {
        Self::Local
    }
}

/// The observed acknowledgement for one replicated operation.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct AcknowledgementReport {
    /// The level the request named.
    pub requested: AcknowledgementLevel,
    /// The level actually observed and reported, in the topology's canonical
    /// naming.
    pub observed: AcknowledgementLevel,
    /// The Regions and their contributing Members, in canonical order.
    pub contributed: Vec<(RegionId, Vec<MemberId>)>,
    /// The lane instant the observation was taken at.
    pub observed_at: LaneInstant,
    /// The degenerate normalization that produced `observed` from
    /// `requested`, when the topology does not distinguish them (D-4).
    pub normalization: Option<(AcknowledgementLevel, AcknowledgementLevel)>,
}

/// The outcome of one replication request at an explicit acknowledgement
/// level.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum ReplicationOutcome {
    /// The requested acknowledgement level was observed.
    Acknowledged(AcknowledgementReport),
    /// The acknowledgement deadline elapsed. Distinct from any local commit
    /// failure: the partial-ack observation names which Members had durably
    /// recorded the operation when the deadline hit.
    Timeout {
        /// The stable identity of the replicated operation.
        operation_id: OperationId,
        /// The acknowledgement level the caller requested.
        requested: AcknowledgementLevel,
        /// The partial acknowledgement observed at the deadline.
        observed: AcknowledgementReport,
    },
    /// Replication failed with a typed error.
    Failed(ReplicationError),
}

/// Typed replication failure categories.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum ReplicationError {
    /// The addressed Member cannot be reached.
    MemberUnavailable,
    /// The required quorum cannot be reached.
    QuorumUnreachable,
    /// The operation is not replicated on this view.
    NotReplicated,
    /// The supplied topology view is invalid.
    InvalidTopology,
    /// The Member's journal has closed.
    JournalClosed,
}

/// A health observation for the member plane; never a convergence fact.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum MemberStatus {
    /// The member is assigned but not yet active.
    Assigned,
    /// The member is actively serving its assignment.
    Active,
    /// The member answered within its bound.
    Responsive,
    /// The member answered outside its bound.
    Stale,
    /// The member cannot be reached.
    Unavailable,
    /// The member is serving in a degraded mode.
    Degraded,
    /// No observation supports any other state.
    Unknown,
}

/// One fault class a lane scenario exercised (the W4 vocabulary).
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub enum FaultClass {
    /// A link held frames back for several pumps.
    LinkDelay,
    /// A link released held frames newest-first.
    LinkReorder,
    /// A link lost a bounded number of frames in transit.
    LinkDrop,
    /// A link delivered every frame twice.
    LinkDuplicate,
    /// An entire Region was isolated from the others in both directions.
    RegionPartition,
    /// A Member's journal was closed and reopened from its directory.
    MemberRestart,
    /// A restarted Member validated its cursor and rejoined.
    MemberRejoin,
    /// The owning Member was lost, stopping new commits until a new leader
    /// was confirmed.
    LeaderLoss,
    /// An entire Region was lost after a write was acknowledged in it.
    RegionLossAfterAcknowledgedWrite,
    /// Partitioned or faulted links were restored.
    Healing,
    /// A duplicated delivery was replayed idempotently.
    DuplicateReplay,
}

impl FaultClass {
    /// Returns the canonical fault-class name.
    #[must_use]
    pub const fn name(self) -> &'static str {
        match self {
            Self::LinkDelay => "link-delay",
            Self::LinkReorder => "link-reorder",
            Self::LinkDrop => "link-drop",
            Self::LinkDuplicate => "link-duplicate",
            Self::RegionPartition => "region-partition",
            Self::MemberRestart => "member-restart",
            Self::MemberRejoin => "member-rejoin",
            Self::LeaderLoss => "leader-loss",
            Self::RegionLossAfterAcknowledgedWrite => "region-loss-after-acknowledged-write",
            Self::Healing => "healing",
            Self::DuplicateReplay => "duplicate-replay",
        }
    }
}

/// A content-free conflict observation: identities, clocks, and the recorded
/// policy outcome. No conflicting document payload crosses this type.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ConflictRecord {
    /// The conflicting operation identities, in canonical order.
    pub operations: Vec<OperationId>,
    /// The writer identities of the conflicting operations, in canonical
    /// order.
    pub members: Vec<MemberId>,
    /// The clock stamps of the conflicting writes, paired with `operations`
    /// order.
    pub clocks: Vec<ClockStamp>,
    /// The recorded policy outcome.
    pub outcome: ConflictOutcome,
}

/// The recorded policy outcome of one conflict.
#[derive(Clone, Debug, Eq, Hash, PartialEq)]
pub enum ConflictOutcome {
    /// Last-writer-wins kept the write of this Member; the other write is
    /// retained as history.
    Winner(MemberId),
    /// The write came from a deposed owner and is retained as observable
    /// history under D-1.
    DeposedOwnerRetained(MemberId),
    /// The peer's clock exceeded the skew bound, so the pair converged by
    /// the deterministic Member-identity tie-break naming this Member.
    UntrustedClock(MemberId),
}

/// The receipt of one locally committed write.
///
/// Deliberately carries no Region or Member fact: a local commit receipt is
/// a local durability fact and nothing else.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct CommitReceipt {
    /// The committed operation identity.
    pub operation: OperationId,
    /// The journal sequence the commit was recorded at.
    pub sequence: u64,
}

/// One write request a member commits locally.
pub struct LaneWriteRequest {
    operation: OperationId,
    range: String,
    writes: Vec<LaneWrite>,
}

impl LaneWriteRequest {
    /// Creates one write request for a data range.
    ///
    /// # Errors
    ///
    /// Returns [`MemberError::InvalidRange`] for invalid range text.
    pub fn new(operation: OperationId, range: &str) -> Result<Self, MemberError> {
        validate_identifier(range).map_err(|_| MemberError::InvalidRange)?;
        Ok(Self { operation, range: range.to_owned(), writes: Vec::new() })
    }

    /// Stages one document put.
    #[must_use]
    pub fn with_put(mut self, document: LaneDocument) -> Self {
        self.writes.push(LaneWrite::Put { document });
        self
    }

    /// Stages one document deletion.
    #[must_use]
    pub fn with_delete(mut self, id: DocumentId) -> Self {
        self.writes.push(LaneWrite::Delete { collection: None, id, version_at_delete: 0 });
        self
    }
}

/// The recorded state of one document key.
#[derive(Clone, Debug, Eq, PartialEq)]
struct KeyState {
    stamp: ClockStamp,
    writer: MemberId,
    operation: OperationId,
}

/// One tenant's advisory progress toward one serving member.
#[derive(Clone, Debug, Eq, PartialEq)]
struct SourceProgress {
    applied_through: Option<u64>,
    tenant_high_water: Option<u64>,
}

/// One observable tenant-isolation denial.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct TenantDenial {
    /// The seam the denial happened at.
    pub seam: DenialSeam,
    /// The tenant the member's serving scope named.
    pub scope_tenant: TenantId,
    /// The tenant the denied attempt named.
    pub attempted_tenant: TenantId,
    /// The member on the other side of the denied attempt.
    pub counterparty: MemberId,
}

/// The seam a tenant-isolation denial happened at.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum DenialSeam {
    /// A catch-up request named a tenant the serving scope does not.
    ServeRequest,
    /// A catch-up delivery named a tenant the applying scope does not.
    ApplyDelivery,
}

/// One observable catch-up fallback to a full scan.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct CatchUpFallback {
    /// The tenant the fallback re-scans.
    pub tenant: TenantId,
    /// The cursor position the source could not serve below.
    pub cursor: u64,
}

/// One observable below-epoch batch this member fenced.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct FencedBatch {
    /// The range the fenced batch claimed.
    pub range: String,
    /// The epoch the batch carried.
    pub batch_epoch: u64,
    /// The highest epoch this member had seen for the range.
    pub highest_epoch: u64,
    /// The member that pushed the fenced batch.
    pub source: MemberId,
}

/// One observable ownership transition that deposed this member.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct Deposition {
    /// The range this member lost ownership of.
    pub range: String,
    /// The epoch the ownership was held at before the transition.
    pub held_epoch: u64,
    /// The epoch the new owner holds the range at.
    pub owner_epoch: u64,
    /// The member that owns the range now.
    pub new_owner: MemberId,
}

/// One observable rejoin rejection.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct RejoinRejection {
    /// The cursor the member rejoined with.
    pub previous: Option<u64>,
    /// The cursor the journal derived after the reopen.
    pub derived: Option<u64>,
    /// The typed rejection reason.
    pub reason: RejoinReason,
}

/// Why a rejoin was refused.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum RejoinReason {
    /// The journal-derived cursor regressed below the cursor the member
    /// rejoined with.
    CursorRegression,
}

/// One observable envelope-discipline rejection at the member boundary.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct EnvelopeRejection {
    /// The reason the frame failed the discipline.
    pub reason: EnvelopeError,
    /// The frame kind, as far as it could be read.
    pub kind: Option<FrameKind>,
}

/// The convergence observation for one operation identity.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct Convergence {
    /// The operation identity the observation is about.
    pub operation: OperationId,
    /// Every observed Member that durably holds the operation, each at its
    /// own acknowledgement state.
    pub holdings: Vec<(MemberId, AcknowledgementLevel)>,
    /// The lane instant the observation was taken at.
    pub observed_at: LaneInstant,
}

/// The observation of one acknowledged write at the loss of entire Regions.
///
/// States durable visibility and the recovery limit; it supports no claim
/// about what the lost Regions held.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct RegionLossObservation {
    /// The operation the observation is about.
    pub operation: OperationId,
    /// The level the write had been acknowledged at before the loss.
    pub acknowledged: AcknowledgementLevel,
    /// The Regions and Members that durably hold the operation, in
    /// canonical order.
    pub durably_held_by: Vec<(RegionId, Vec<MemberId>)>,
    /// The lost Regions whose state the observation cannot state.
    pub unknown_in: Vec<RegionId>,
    /// The lane instant this observation was taken at.
    pub observed_at: LaneInstant,
}

/// The report of one dispatch pass.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct DispatchReport {
    /// The catch-up requests this pass answered.
    pub served: usize,
    /// The commits this pass applied.
    pub applied: usize,
    /// Redelivered operations this pass resolved idempotently through the
    /// durable operation ledger.
    pub duplicates_resolved: usize,
    /// Incoming writes this pass resolved as conflicts under the D-3 policy.
    pub conflicts_recorded: usize,
    /// Acknowledgement reports this pass sent for received pushes
    /// (counted per acknowledged operation; a batched report frame counts
    /// once per operation it carries).
    pub ack_reports_sent: usize,
    /// Sends this pass deferred because the addressed peer's bounded link
    /// sat at capacity (SE-07 budget E-5: a slow consumer is deferred and
    /// observable, never allowed to abort the pass).
    pub slow_consumer_deferrals: usize,
    /// Acknowledgement reports this pass collected.
    pub ack_reports: usize,
    /// Frames this pass rejected at the envelope discipline.
    pub envelope_rejections: usize,
    /// Whether the pass reached the source's last-known high water.
    pub caught_up: bool,
}

/// Typed member-driver failures.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum MemberError {
    /// The member is not seated by the supplied topology view.
    NotInTopology,
    /// The supplied topology view or configuration input is invalid.
    InvalidTopology,
    /// The addressed source member is not seated by this member's view.
    UnknownSource(MemberId),
    /// The named data range is not valid topology input.
    InvalidRange,
    /// This member does not own the named data range's write path.
    NotOwner {
        /// The range the write addressed.
        range: String,
    },
    /// The supplied view lowers or flips an epoch this member holds without
    /// a higher epoch.
    StaleView {
        /// The range whose ownership would regress.
        range: String,
        /// The epoch this member's current view holds.
        current_epoch: u64,
        /// The epoch the offered view names.
        offered_epoch: u64,
    },
    /// A pushed batch is stamped below the highest epoch this member has
    /// seen for its range.
    FencedBatch {
        /// The range the pushed batch claimed.
        range: String,
        /// The epoch the batch carried.
        batch_epoch: u64,
        /// The highest epoch this member has seen for the range.
        highest_epoch: u64,
    },
    /// A batched push named operations whose (range, epoch) fencing
    /// identities do not resolve to one. A single frame fences every
    /// commit it carries under one identity, so a mixed batch is refused
    /// before anything is sent rather than fenced under whichever
    /// identity the sender resolved last.
    MixedFencingIdentity {
        /// The (range, epoch) the first served operation resolved to.
        first: (String, u64),
        /// The divergent (range, epoch) that refused the batch.
        other: (String, u64),
    },
    /// A cross-tenant attempt was denied at the seam.
    CrossTenant {
        /// The tenant the member's scope named.
        scope: TenantId,
        /// The tenant the denied attempt named.
        attempted: TenantId,
    },
    /// A delivered commit was not applied; the reason is typed.
    ApplyFailed {
        /// The operation identity that failed.
        operation: OperationId,
        /// The typed failure.
        reason: ApplyFailure,
    },
    /// The source cannot serve the requested range of history.
    GapUnservable {
        /// The tenant whose history cannot be served.
        tenant: TenantId,
        /// The cursor position that cannot be served below.
        cursor: u64,
        /// The oldest sequence the source's journal can serve.
        servable_from: u64,
    },
    /// The bounded transport link rejected a frame.
    TransportFull,
    /// A frame failed the envelope discipline.
    Envelope(EnvelopeError),
    /// The member's journal is closed.
    JournalClosed,
    /// Rejoin validation refused admission.
    RejoinRejected(RejoinRejection),
    /// The journal returned a typed failure.
    Journal(JournalError),
}

impl From<JournalError> for MemberError {
    fn from(error: JournalError) -> Self {
        match error {
            JournalError::Closed => Self::JournalClosed,
            other => Self::Journal(other),
        }
    }
}

impl From<TransportError> for MemberError {
    fn from(_: TransportError) -> Self {
        Self::TransportFull
    }
}

/// Why a delivered commit did not apply.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ApplyFailure {
    /// The ledger rejected the write set: the operation identity is bound to
    /// a different write set and is never silently skipped.
    OperationIdReuse,
    /// The replayed delete targeted a key whose collection differs from the
    /// one the source recorded.
    KeyMismatch,
}

/// The member configuration: one validated topology plus the catch-up batch
/// bound.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct LaneConfig {
    /// The validated membership view.
    pub topology: TopologyView,
    /// The bounded number of commits one catch-up round requests.
    pub catch_up_batch: u32,
}

impl LaneConfig {
    /// Creates one configuration from a validated view.
    ///
    /// # Errors
    ///
    /// Returns [`MemberError::InvalidTopology`] when the view is not
    /// servable.
    pub fn try_new(topology: TopologyView) -> Result<Self, MemberError> {
        topology.clone().validated().map_err(|_| MemberError::InvalidTopology)?;
        Ok(Self { topology, catch_up_batch: DEFAULT_CATCH_UP_BATCH })
    }
}

/// One replication member over the engine-owned transport lane.
pub struct LaneMember {
    id: MemberId,
    journal: MemberJournal,
    config: LaneConfig,
    advisory: BTreeMap<(TenantId, MemberId), SourceProgress>,
    hlc: ClockStamp,
    message_ids: u64,
    operation_stamps: BTreeMap<OperationId, ClockStamp>,
    operation_ranges: BTreeMap<OperationId, String>,
    operation_sequences: BTreeMap<OperationId, u64>,
    key_states: BTreeMap<(TenantId, DocumentId), KeyState>,
    highest_epoch_seen: BTreeMap<String, u64>,
    ack_states: BTreeMap<(OperationId, MemberId), bool>,
    conflicts: Vec<ConflictRecord>,
    denials: Vec<TenantDenial>,
    fallbacks: Vec<CatchUpFallback>,
    fenced: Vec<FencedBatch>,
    depositions: Vec<Deposition>,
    envelope_rejections: Vec<EnvelopeRejection>,
    slow_consumers: Vec<MemberId>,
    last_rejection: Option<RejoinRejection>,
    activated: bool,
    answered: bool,
}

impl LaneMember {
    /// Opens one member over a durable journal location under a validated
    /// configuration.
    ///
    /// # Errors
    ///
    /// Returns [`MemberError::NotInTopology`] when the view does not seat
    /// this member and [`MemberError::Journal`] when the location cannot be
    /// opened.
    pub fn try_new(
        config: LaneConfig,
        id: MemberId,
        location: &std::path::Path,
    ) -> Result<Self, MemberError> {
        if !config.topology.contains_member(&id) {
            return Err(MemberError::NotInTopology);
        }
        let journal = MemberJournal::open(location).map_err(MemberError::from)?;
        let mut member = Self {
            id,
            journal,
            config,
            advisory: BTreeMap::new(),
            hlc: ClockStamp::new(0, 0),
            message_ids: 0,
            operation_stamps: BTreeMap::new(),
            operation_ranges: BTreeMap::new(),
            operation_sequences: BTreeMap::new(),
            key_states: BTreeMap::new(),
            highest_epoch_seen: BTreeMap::new(),
            ack_states: BTreeMap::new(),
            conflicts: Vec::new(),
            denials: Vec::new(),
            fallbacks: Vec::new(),
            fenced: Vec::new(),
            depositions: Vec::new(),
            envelope_rejections: Vec::new(),
            slow_consumers: Vec::new(),
            last_rejection: None,
            activated: false,
            answered: false,
        };
        member.record_view_epochs();
        Ok(member)
    }

    /// Returns the member identity.
    #[must_use]
    pub fn id(&self) -> &MemberId {
        &self.id
    }

    /// Returns the member's journal (read-only access for observations).
    #[must_use]
    pub fn journal(&self) -> &MemberJournal {
        &self.journal
    }

    /// Reports whether the member's journal is open.
    #[must_use]
    pub fn is_open(&self) -> bool {
        !self.journal.is_closed()
    }

    /// Commits one write request locally under `tenant`.
    ///
    /// Under D-1 the member admits the write only while its current view
    /// names it owner of the addressed data range. The write is stamped with
    /// the member's bounded hybrid logical clock, and the stamp is recorded
    /// for the range and every key it touches — the facts the D-3 policy
    /// decides with later.
    ///
    /// # Errors
    ///
    /// Returns [`MemberError::NotOwner`] when this member does not own the
    /// addressed range, and the journal's typed failures otherwise.
    pub fn commit_write(
        &mut self,
        tenant: &TenantId,
        write: LaneWriteRequest,
    ) -> Result<CommitReceipt, MemberError> {
        if self.config.topology.owner_of(&write.range) != Some(&self.id) {
            return Err(MemberError::NotOwner { range: write.range });
        }
        let stamp = self.tick_local_clock();
        let commit = LaneCommit {
            operation: write.operation.clone(),
            stamp,
            sequence: 0,
            writes: write.writes.clone(),
        };
        let sequence =
            match self.journal.record_commit(tenant, &commit).map_err(MemberError::from)? {
                RecordOutcome::Recorded(sequence) | RecordOutcome::Duplicate(sequence) => sequence,
            };
        self.operation_stamps.insert(write.operation.clone(), stamp);
        self.operation_ranges.insert(write.operation.clone(), write.range.clone());
        self.operation_sequences.insert(write.operation.clone(), sequence);
        for write_entry in &write.writes {
            let key = write_key(tenant, write_entry);
            self.key_states.insert(
                key,
                KeyState { stamp, writer: self.id.clone(), operation: write.operation.clone() },
            );
        }
        self.activated = true;
        Ok(CommitReceipt { operation: write.operation, sequence })
    }

    /// Requests one tenant's catch-up from `source` through the transport.
    ///
    /// The request carries the member's advisory progress in the source's
    /// sequence space when it has one, and asks for a full scan otherwise.
    ///
    /// # Errors
    ///
    /// Returns [`MemberError::JournalClosed`],
    /// [`MemberError::UnknownSource`], and [`MemberError::TransportFull`]
    /// when the bounded link rejects the frame.
    pub fn request_catch_up(
        &mut self,
        transport: &mut dyn MemberTransport,
        source: &MemberId,
        tenant: &TenantId,
    ) -> Result<(), MemberError> {
        if self.journal.is_closed() {
            return Err(MemberError::JournalClosed);
        }
        if !self.config.topology.contains_member(source) {
            return Err(MemberError::UnknownSource(source.clone()));
        }
        let after = self
            .advisory
            .get(&(tenant.clone(), source.clone()))
            .and_then(|progress| progress.applied_through);
        let envelope = self.build_envelope(
            FrameKind::CatchUpRequest,
            source.clone(),
            None,
            None,
            None,
            None,
            None,
            Payload::CatchUpRequest {
                tenant: tenant.clone(),
                after,
                limit: self.config.catch_up_batch,
            },
        );
        self.send(transport, source, &envelope)
    }

    /// Dispatches every inbound frame under one serving tenant: serving
    /// catch-up requests, applying deliveries, answering pushes with
    /// acknowledgement reports, and enforcing the envelope discipline.
    ///
    /// A request or delivery naming any other tenant is denied with a typed
    /// error, recorded in the observable denial log, and answered with
    /// nothing: no cross-tenant data crosses the seam.
    ///
    /// # Errors
    ///
    /// Returns [`MemberError::CrossTenant`], [`MemberError::FencedBatch`],
    /// [`MemberError::GapUnservable`], [`MemberError::ApplyFailed`], and
    /// the journal's typed failures.
    #[allow(clippy::too_many_lines, reason = "one dispatch pass over the W4 delivery semantics")]
    pub fn dispatch_under(
        &mut self,
        transport: &mut dyn MemberTransport,
        scope_tenant: &TenantId,
    ) -> Result<DispatchReport, MemberError> {
        self.refresh_clock_from(transport.now());
        if self.journal.is_closed() {
            return Err(MemberError::JournalClosed);
        }
        let frames = transport.receive(&self.id, FrameSelector::All);
        let mut report = DispatchReport::default();
        let mut requests: Vec<(MemberId, u64, Option<u64>, u32)> = Vec::new();
        let mut deliveries: Vec<(MemberId, Payload)> = Vec::new();
        let mut push_answers: Vec<(MemberId, u64, Vec<OperationId>)> = Vec::new();
        for frame in frames {
            let Some((envelope, _)) = decode_or_reject(&mut self.envelope_rejections, &frame)
            else {
                report.envelope_rejections += 1;
                continue;
            };
            let kind = Some(envelope.kind);
            if envelope.target != self.id {
                self.record_rejection(EnvelopeRejection {
                    reason: EnvelopeError::WrongTarget,
                    kind,
                });
                report.envelope_rejections += 1;
                continue;
            }
            if envelope.expires_at.is_some_and(|at| at <= transport.now()) {
                self.record_rejection(EnvelopeRejection { reason: EnvelopeError::Expired, kind });
                report.envelope_rejections += 1;
                continue;
            }
            self.refresh_clock_from(envelope.issued_at);
            match envelope.payload {
                Payload::CatchUpRequest { tenant, after, limit } => {
                    if tenant != *scope_tenant {
                        self.record_denial(TenantDenial {
                            seam: DenialSeam::ServeRequest,
                            scope_tenant: scope_tenant.clone(),
                            attempted_tenant: tenant.clone(),
                            counterparty: envelope.source.clone(),
                        });
                        return Err(MemberError::CrossTenant {
                            scope: scope_tenant.clone(),
                            attempted: tenant,
                        });
                    }
                    requests.push((envelope.source.clone(), envelope.message_id, after, limit));
                }
                Payload::CatchUpBatch {
                    tenant,
                    commits,
                    through,
                    tenant_high_water,
                    servable_from,
                    gap_below,
                } => {
                    if tenant != *scope_tenant {
                        self.record_denial(TenantDenial {
                            seam: DenialSeam::ApplyDelivery,
                            scope_tenant: scope_tenant.clone(),
                            attempted_tenant: tenant.clone(),
                            counterparty: envelope.source.clone(),
                        });
                        return Err(MemberError::CrossTenant {
                            scope: scope_tenant.clone(),
                            attempted: tenant,
                        });
                    }
                    if let Some(gap_cursor) = gap_below {
                        let advisory_cursor = self
                            .advisory
                            .get(&(tenant.clone(), envelope.source.clone()))
                            .and_then(|progress| progress.applied_through);
                        if advisory_cursor == Some(gap_cursor) && gap_cursor > 0 {
                            self.advisory.remove(&(tenant.clone(), envelope.source.clone()));
                            self.record_fallback(CatchUpFallback {
                                tenant: tenant.clone(),
                                cursor: gap_cursor,
                            });
                            continue;
                        }
                        return Err(MemberError::GapUnservable {
                            tenant,
                            cursor: gap_cursor,
                            servable_from: servable_from.unwrap_or(gap_cursor),
                        });
                    }
                    deliveries.push((
                        envelope.source.clone(),
                        Payload::CatchUpBatch {
                            tenant,
                            commits,
                            through,
                            tenant_high_water,
                            servable_from,
                            gap_below,
                        },
                    ));
                }
                Payload::Push { tenant, range, epoch, commits } => {
                    if tenant != *scope_tenant {
                        self.record_denial(TenantDenial {
                            seam: DenialSeam::ApplyDelivery,
                            scope_tenant: scope_tenant.clone(),
                            attempted_tenant: tenant.clone(),
                            counterparty: envelope.source.clone(),
                        });
                        return Err(MemberError::CrossTenant {
                            scope: scope_tenant.clone(),
                            attempted: tenant,
                        });
                    }
                    // Apply-time fencing (D-1): a batch from a deposed owner
                    // is rejected, and its commits are retained as conflict
                    // history so a local durable fact is never silently
                    // discarded.
                    if let Some(highest) = self.highest_epoch_seen.get(&range).copied()
                        && epoch < highest
                    {
                        for commit in &commits {
                            self.record_conflict(ConflictRecord {
                                operations: vec![commit.operation.clone()],
                                members: vec![envelope.source.clone()],
                                clocks: vec![commit.stamp],
                                outcome: ConflictOutcome::DeposedOwnerRetained(
                                    envelope.source.clone(),
                                ),
                            });
                        }
                        self.record_fenced(FencedBatch {
                            range: range.clone(),
                            batch_epoch: epoch,
                            highest_epoch: highest,
                            source: envelope.source.clone(),
                        });
                        return Err(MemberError::FencedBatch {
                            range,
                            batch_epoch: epoch,
                            highest_epoch: highest,
                        });
                    }
                    self.record_epoch(&range, epoch);
                    push_answers.push((
                        envelope.source.clone(),
                        envelope.message_id,
                        commits.iter().map(|commit| commit.operation.clone()).collect::<Vec<_>>(),
                    ));
                    deliveries.push((
                        envelope.source.clone(),
                        Payload::Push { tenant, range, epoch, commits },
                    ));
                }
                Payload::AckReport { tenant: _, operation, held_synced } => {
                    self.ack_states.insert((operation, envelope.source.clone()), held_synced);
                    report.ack_reports += 1;
                }
                Payload::AckBatch { tenant: _, reports: batch } => {
                    for (operation, held_synced) in batch {
                        self.ack_states.insert((operation, envelope.source.clone()), held_synced);
                        report.ack_reports += 1;
                    }
                }
                // The journal tailer's replicated-record batch (envelope
                // minor 2) is the S-05 cross-region lane's frame, not this
                // W4 protocol's: the driver records the rejection and never
                // applies it — the RegionLink owns that apply path.
                // Record the frame's own kind — a misdelivered minor-5
                // publication must not be logged as a batch.
                Payload::ReplicationBatch { .. } => {
                    self.record_rejection(EnvelopeRejection {
                        reason: EnvelopeError::UnsupportedKind,
                        kind: Some(FrameKind::ReplicationBatch),
                    });
                    report.envelope_rejections += 1;
                }
                Payload::ReplicationPublication { .. } => {
                    self.record_rejection(EnvelopeRejection {
                        reason: EnvelopeError::UnsupportedKind,
                        kind: Some(FrameKind::ReplicationPublication),
                    });
                    report.envelope_rejections += 1;
                }
            }
        }
        // Serve the admitted catch-up requests. A peer whose bounded link
        // sits at capacity is deferred observably (SE-07 budget E-5): the
        // pass continues and serves every healthy peer's request.
        for (requester, correlation, after, limit) in requests {
            let batch = self.build_catch_up_batch(scope_tenant, after, limit)?;
            let reply = self.build_envelope(
                FrameKind::CatchUpBatch,
                requester.clone(),
                Some(correlation),
                None,
                None,
                None,
                None,
                batch,
            );
            if self.send_or_defer(transport, &requester, &reply, &mut report)? {
                report.served += 1;
                self.answered = true;
            }
        }
        // Apply the deliveries, then answer every received push with the
        // durable post-apply acknowledgement: the report crosses the wire,
        // and its held flag reflects the ledger after this pass's applies.
        // A multi-operation push is answered by one batched report frame
        // (SE-07 budgets E-3/E-4); a single-operation push keeps the
        // single-report frame shape.
        self.apply_deliveries(&mut report, scope_tenant, deliveries)?;
        for (peer, correlation, operations) in push_answers {
            let reports: Vec<(OperationId, bool)> = operations
                .iter()
                .map(|operation| {
                    let held = matches!(
                        self.journal.resolve(operation),
                        Ok(DurableResolution::Committed(_))
                    );
                    (operation.clone(), held)
                })
                .collect();
            if reports.len() > 1 {
                let report_envelope = self.build_envelope(
                    FrameKind::AckBatch,
                    peer.clone(),
                    Some(correlation),
                    None,
                    None,
                    None,
                    None,
                    Payload::AckBatch { tenant: scope_tenant.clone(), reports },
                );
                if self.send_or_defer(transport, &peer, &report_envelope, &mut report)? {
                    report.ack_reports_sent += operations.len();
                }
                continue;
            }
            for (operation, held_synced) in reports {
                let report_envelope = self.build_envelope(
                    FrameKind::AckReport,
                    peer.clone(),
                    Some(correlation),
                    None,
                    Some(operation.clone()),
                    None,
                    None,
                    Payload::AckReport { tenant: scope_tenant.clone(), operation, held_synced },
                );
                if self.send_or_defer(transport, &peer, &report_envelope, &mut report)? {
                    report.ack_reports_sent += 1;
                }
            }
        }
        Ok(report)
    }

    /// Applies the pass's deliveries: merged by (source, sequence),
    /// source-matched resumption, self round-trip resolution, and the D-3
    /// policy on competing writes.
    fn apply_deliveries(
        &mut self,
        report: &mut DispatchReport,
        scope_tenant: &TenantId,
        deliveries: Vec<(MemberId, Payload)>,
    ) -> Result<(), MemberError> {
        struct Delivery {
            source: MemberId,
            commits: Vec<LaneCommit>,
            tenant_high_water: Option<u64>,
        }
        let mut batches: Vec<Delivery> = Vec::new();
        for (source, payload) in deliveries {
            match payload {
                Payload::CatchUpBatch { commits, tenant_high_water, .. } => {
                    batches.push(Delivery { source, commits, tenant_high_water });
                }
                Payload::Push { commits, .. } => {
                    let through = commits.last().map(|commit| commit.sequence);
                    batches.push(Delivery { source, commits, tenant_high_water: through });
                }
                _ => {}
            }
        }
        let mut merged: BTreeMap<(MemberId, u64), LaneCommit> = BTreeMap::new();
        for batch in &batches {
            for commit in &batch.commits {
                if merged.insert((batch.source.clone(), commit.sequence), commit.clone()).is_some()
                {
                    report.duplicates_resolved += 1;
                }
            }
        }
        for ((source, sequence), commit) in merged {
            let resumed_from = self
                .advisory
                .get(&(scope_tenant.clone(), source.clone()))
                .and_then(|progress| progress.applied_through);
            if let Some(resumed) = resumed_from
                && sequence <= resumed
            {
                report.duplicates_resolved += 1;
                continue;
            }
            let own_stamp = self.operation_stamps.get(&commit.operation).copied();
            let already_committed = matches!(
                self.journal.resolve(&commit.operation),
                Ok(DurableResolution::Committed(_))
            );
            if own_stamp == Some(commit.stamp) && already_committed {
                report.duplicates_resolved += 1;
            } else {
                match self.resolve_incoming(&source, &commit, scope_tenant) {
                    Incoming::ConflictSkipped => {
                        report.conflicts_recorded += 1;
                    }
                    Incoming::Apply => match self.apply_one(scope_tenant, &commit, &source)? {
                        ApplyStep::Applied => report.applied += 1,
                        ApplyStep::Duplicate => report.duplicates_resolved += 1,
                    },
                }
            }
            let progress = self
                .advisory
                .entry((scope_tenant.clone(), source.clone()))
                .or_insert(SourceProgress { applied_through: None, tenant_high_water: None });
            progress.applied_through = progress.applied_through.max(Some(sequence));
        }
        for batch in &batches {
            let progress = self
                .advisory
                .entry((scope_tenant.clone(), batch.source.clone()))
                .or_insert(SourceProgress { applied_through: None, tenant_high_water: None });
            progress.tenant_high_water = progress.tenant_high_water.max(batch.tenant_high_water);
            if progress.applied_through.is_some()
                && progress.applied_through >= progress.tenant_high_water
            {
                report.caught_up = true;
            }
        }
        if report.applied > 0 {
            self.activated = true;
        }
        Ok(())
    }

    /// Builds one catch-up batch from the member's journal.
    fn build_catch_up_batch(
        &mut self,
        tenant: &TenantId,
        after: Option<u64>,
        limit: u32,
    ) -> Result<Payload, MemberError> {
        let servable_from = self.journal.servable_history_start().map_err(MemberError::from)?;
        let gap_below = catch_up_gap(after, servable_from);
        let (commits, through) = if gap_below.is_some() {
            (Vec::new(), None)
        } else {
            let served = self
                .journal
                .tenant_commits(tenant, after.unwrap_or(0), limit)
                .map_err(MemberError::from)?;
            let through = served.last().map(|commit| commit.sequence);
            (served, through)
        };
        let high_water = self.journal.journaled_high_water().map_err(MemberError::from)?;
        let tenant_high_water = high_water
            .map(|at| self.journal.tenant_sequence(tenant, at))
            .transpose()
            .map_err(MemberError::from)?
            .flatten();
        Ok(Payload::CatchUpBatch {
            tenant: tenant.clone(),
            commits,
            through,
            tenant_high_water,
            servable_from,
            gap_below,
        })
    }

    /// Decides the D-3 disposition of one incoming commit against the
    /// member's recorded key states and clock, recording the conflict either
    /// way.
    fn resolve_incoming(
        &mut self,
        source: &MemberId,
        commit: &LaneCommit,
        tenant: &TenantId,
    ) -> Incoming {
        let stamp = commit.stamp;
        if stamp.ticks() > self.hlc.ticks().saturating_add(MAX_CLOCK_SKEW_TICKS) {
            let winner = if (stamp, source.clone()) > (self.hlc, self.id.clone()) {
                source.clone()
            } else {
                self.id.clone()
            };
            self.record_conflict(ConflictRecord {
                operations: vec![commit.operation.clone()],
                members: sorted_members(&[source.clone(), self.id.clone()]),
                clocks: vec![stamp, self.hlc],
                outcome: ConflictOutcome::UntrustedClock(winner.clone()),
            });
            return if winner == self.id { Incoming::ConflictSkipped } else { Incoming::Apply };
        }
        self.hlc = merge_stamp(self.hlc, stamp);
        let keys = written_keys(tenant, commit);
        let competing: Vec<(&(TenantId, DocumentId), &KeyState)> =
            self.key_states.iter().filter(|(key, _)| keys.contains(key)).collect();
        if competing.is_empty() {
            return Incoming::Apply;
        }
        let incoming: (&ClockStamp, &MemberId) = (&stamp, source);
        let beats_every =
            competing.iter().all(|(_, state)| incoming > (&state.stamp, &state.writer));
        let mut operations: Vec<OperationId> =
            competing.iter().map(|(_, state)| state.operation.clone()).collect();
        let mut clocks: Vec<ClockStamp> = competing.iter().map(|(_, state)| state.stamp).collect();
        operations.push(commit.operation.clone());
        clocks.push(stamp);
        let (operations, clocks) = sort_record_facts(operations, clocks);
        if beats_every {
            self.record_conflict(ConflictRecord {
                operations,
                members: sorted_members(&[source.clone(), self.id.clone()]),
                clocks,
                outcome: ConflictOutcome::Winner(source.clone()),
            });
            Incoming::Apply
        } else {
            let winner = competing
                .iter()
                .map(|(_, state)| state)
                .max_by_key(|state| (state.stamp, state.writer.clone()))
                .map(|state| state.writer.clone())
                .expect("competing states are nonempty");
            let mut members = vec![source.clone(), winner.clone()];
            for (_, state) in &competing {
                members.push(state.writer.clone());
            }
            self.record_conflict(ConflictRecord {
                operations,
                members: sorted_members(&members),
                clocks,
                outcome: ConflictOutcome::Winner(winner),
            });
            Incoming::ConflictSkipped
        }
    }

    /// Applies one delivered commit through the durable ledger with its
    /// original operation identity, recording its key state.
    fn apply_one(
        &mut self,
        tenant: &TenantId,
        commit: &LaneCommit,
        source: &MemberId,
    ) -> Result<ApplyStep, MemberError> {
        // The delete-collection replay check: a key that currently lives in
        // a different collection than the source deleted from is a typed
        // mismatch, never a silent removal of another key. The check uses
        // point lookups and only for recorded deletes, so applying one
        // commit costs journal work proportional to its own write set,
        // never to the tenant's derived document count (SE-07 budget E-6).
        for write in &commit.writes {
            if let LaneWrite::Delete { collection: Some(recorded), id, .. } = write
                && let Some(existing) =
                    self.journal.document_collection(tenant, id).map_err(MemberError::from)?
                && existing.as_str() != recorded.as_str()
            {
                return Err(MemberError::ApplyFailed {
                    operation: commit.operation.clone(),
                    reason: ApplyFailure::KeyMismatch,
                });
            }
        }
        let outcome = self.journal.record_commit(tenant, commit).map_err(|error| match error {
            JournalError::OperationIdReuse => MemberError::ApplyFailed {
                operation: commit.operation.clone(),
                reason: ApplyFailure::OperationIdReuse,
            },
            other => MemberError::from(other),
        })?;
        let sequence = match outcome {
            RecordOutcome::Recorded(sequence) | RecordOutcome::Duplicate(sequence) => sequence,
        };
        self.operation_stamps.entry(commit.operation.clone()).or_insert(commit.stamp);
        self.operation_sequences.insert(commit.operation.clone(), sequence);
        for key in written_keys(tenant, commit) {
            self.key_states.insert(
                key,
                KeyState {
                    stamp: commit.stamp,
                    writer: source.clone(),
                    operation: commit.operation.clone(),
                },
            );
        }
        Ok(match outcome {
            RecordOutcome::Recorded(_) => ApplyStep::Applied,
            RecordOutcome::Duplicate(_) => ApplyStep::Duplicate,
        })
    }

    /// Sends one push of a committed operation to a peer (the leader
    /// fan-out of the D-1 data plane).
    ///
    /// # Errors
    ///
    /// Returns [`MemberError::NotOwner`] when the operation is unknown, and
    /// the transport's typed failure.
    pub fn send_push(
        &mut self,
        transport: &mut dyn MemberTransport,
        peer: &MemberId,
        tenant: &TenantId,
        operation: &OperationId,
    ) -> Result<(), MemberError> {
        let Some(range) = self.operation_ranges.get(operation).cloned() else {
            return Err(MemberError::NotOwner { range: String::new() });
        };
        let epoch = self.config.topology.owner_epoch_of(&range).unwrap_or(1);
        let Some(commit) = self.find_commit(tenant, operation) else {
            return Err(MemberError::NotOwner { range });
        };
        let envelope = self.build_envelope(
            FrameKind::Push,
            peer.clone(),
            None,
            None,
            Some(operation.clone()),
            Some(range.clone()),
            Some(epoch),
            Payload::Push { tenant: tenant.clone(), range, epoch, commits: vec![commit] },
        );
        self.send(transport, peer, &envelope)
    }

    /// Sends one push of every named committed operation to a peer in a
    /// single frame (SE-07 budgets E-3/E-4: acknowledging K operations
    /// costs one push frame and one batched answer per peer, not K of
    /// each). Operations the member cannot serve from its journal are
    /// skipped; the returned count is the number of commits the frame
    /// carried.
    ///
    /// Fencing invariant: one frame fences every commit it carries under
    /// exactly one (range, epoch) identity, so the named operations must
    /// all resolve to the same (range, epoch) — the receiver's apply-time
    /// fencing (D-1) judges the whole batch by the frame's single claimed
    /// identity. A batch whose operations span ranges or epochs is
    /// refused with [`MemberError::MixedFencingIdentity`] before anything
    /// is sent, never fenced under whichever identity the sender happened
    /// to resolve last; a caller holding such operations sends one batch
    /// per (range, epoch) pair.
    ///
    /// # Errors
    ///
    /// Returns [`MemberError::NotOwner`] when none of the operations are
    /// this member's committed writes,
    /// [`MemberError::MixedFencingIdentity`] when they resolve to more
    /// than one (range, epoch), and the transport's typed failure.
    pub fn send_push_batch(
        &mut self,
        transport: &mut dyn MemberTransport,
        peer: &MemberId,
        tenant: &TenantId,
        operations: &[OperationId],
    ) -> Result<usize, MemberError> {
        let mut commits = Vec::with_capacity(operations.len());
        let mut fencing: Option<(String, u64)> = None;
        for operation in operations {
            let Some(operation_range) = self.operation_ranges.get(operation).cloned() else {
                continue;
            };
            let Some(found) = self.find_commit(tenant, operation) else {
                continue;
            };
            let epoch = self.config.topology.owner_epoch_of(&operation_range).unwrap_or(1);
            let identity = (operation_range, epoch);
            if let Some(sealed) = &fencing
                && *sealed != identity
            {
                return Err(MemberError::MixedFencingIdentity {
                    first: sealed.clone(),
                    other: identity,
                });
            }
            fencing = Some(identity);
            commits.push(found);
        }
        let Some((range, epoch)) = fencing else {
            return Err(MemberError::NotOwner { range: String::new() });
        };
        let pushed = commits.len();
        let envelope = self.build_envelope(
            FrameKind::Push,
            peer.clone(),
            None,
            None,
            None,
            Some(range.clone()),
            Some(epoch),
            Payload::Push { tenant: tenant.clone(), range, epoch, commits },
        );
        self.send(transport, peer, &envelope)?;
        Ok(pushed)
    }

    /// Returns the members whose latest acknowledgement reports confirm they
    /// durably hold the operation, plus this member when its own ledger
    /// does.
    #[must_use]
    pub fn ack_contributors(&self, operation: &OperationId) -> BTreeSet<MemberId> {
        let mut contributing = BTreeSet::new();
        if matches!(self.journal.resolve(operation), Ok(DurableResolution::Committed(_))) {
            contributing.insert(self.id.clone());
        }
        for ((reported, member), held) in &self.ack_states {
            if reported == operation && *held {
                contributing.insert(member.clone());
            }
        }
        contributing
    }

    /// Injects one raw catch-up request toward `target` naming any tenant —
    /// the lane's hostile-requester probe point. The serving member's
    /// discipline denies a request that names a tenant its serving scope
    /// does not, records the denial, and serves nothing.
    ///
    /// # Errors
    ///
    /// Returns the transport's typed failure.
    pub fn send_raw_catch_up_request(
        &mut self,
        transport: &mut dyn MemberTransport,
        target: &MemberId,
        tenant: &TenantId,
        after: Option<u64>,
        limit: u32,
    ) -> Result<(), MemberError> {
        let envelope = self.build_envelope(
            FrameKind::CatchUpRequest,
            target.clone(),
            None,
            None,
            None,
            None,
            None,
            Payload::CatchUpRequest { tenant: tenant.clone(), after, limit },
        );
        self.send(transport, target, &envelope)
    }

    /// Builds one acknowledgement report from the contributing set.
    #[must_use]
    pub fn build_ack_report(
        &self,
        requested: AcknowledgementLevel,
        observed: AcknowledgementLevel,
        normalization: Option<(AcknowledgementLevel, AcknowledgementLevel)>,
        contributing: &BTreeSet<MemberId>,
        at: LaneInstant,
    ) -> AcknowledgementReport {
        let mut grouped: BTreeMap<RegionId, Vec<MemberId>> = BTreeMap::new();
        for member in contributing {
            if let Some(region) = self.config.topology.region_of(member) {
                grouped.entry(region.clone()).or_default().push(member.clone());
            }
        }
        AcknowledgementReport {
            requested,
            observed,
            contributed: grouped.into_iter().collect(),
            observed_at: at,
            normalization,
        }
    }

    /// Returns the topology this member holds.
    #[must_use]
    pub fn topology(&self) -> &TopologyView {
        &self.config.topology
    }

    /// Applies a new membership view (a control-plane transition the member
    /// validates before admitting).
    ///
    /// # Errors
    ///
    /// Returns [`MemberError::InvalidTopology`] for an unservable view,
    /// [`MemberError::NotInTopology`] when the view does not seat this
    /// member, and [`MemberError::StaleView`] when any known range's epoch
    /// would go backwards, or an owner would flip without a raised epoch.
    pub fn apply_view(&mut self, view: TopologyView) -> Result<(), MemberError> {
        let view = view.validated().map_err(|_| MemberError::InvalidTopology)?;
        if !view.contains_member(&self.id) {
            return Err(MemberError::NotInTopology);
        }
        for (range, highest) in self.highest_epoch_seen.clone() {
            if let Some(offered) = view.owner_epoch_of(&range)
                && offered < highest
            {
                return Err(MemberError::StaleView {
                    range,
                    current_epoch: highest,
                    offered_epoch: offered,
                });
            }
        }
        let known_ranges: Vec<(String, MemberId, u64)> = self
            .config
            .topology
            .owned_ranges()
            .filter_map(|range| {
                let owner = self.config.topology.owner_of(range)?.clone();
                let epoch = self.config.topology.owner_epoch_of(range).unwrap_or(1);
                Some((range.to_owned(), owner, epoch))
            })
            .collect();
        for (range, current_owner, current_epoch) in known_ranges {
            let Some(new_owner) = view.owner_of(&range) else { continue };
            let offered_epoch = view.owner_epoch_of(&range).unwrap_or(1);
            let acceptable = if new_owner == &current_owner {
                offered_epoch >= current_epoch
            } else {
                offered_epoch > current_epoch
            };
            if !acceptable {
                return Err(MemberError::StaleView { range, current_epoch, offered_epoch });
            }
            if new_owner != &self.id && current_owner == self.id {
                self.record_deposition(Deposition {
                    range,
                    held_epoch: current_epoch,
                    owner_epoch: offered_epoch,
                    new_owner: new_owner.clone(),
                });
            }
        }
        self.config = LaneConfig::try_new(view)?;
        self.record_view_epochs();
        Ok(())
    }

    /// Observes an injected lane instant into the member's clock.
    pub fn observe_instant(&mut self, at: LaneInstant) {
        self.refresh_clock_from(at);
    }

    /// Returns the member's durable replication cursor, derived from its own
    /// journal.
    ///
    /// # Errors
    ///
    /// Returns the journal's typed failures.
    pub fn cursor(&self) -> Result<LaneCursor, MemberError> {
        self.journal.cursor(&self.id).map_err(MemberError::from)
    }

    /// Closes the member's journal; the durable location keeps its bytes.
    pub fn close(&mut self) {
        self.journal.close();
        self.reset_memory_state();
    }

    /// Reopens the member's journal from its durable location.
    ///
    /// Advisory progress, clock state, and key states do not survive a
    /// restart — they are in-memory facts — while the durable cursor does,
    /// because it is derived from the journal on the other side.
    ///
    /// # Errors
    ///
    /// Returns the reopen's typed failures.
    pub fn restart(&mut self) -> Result<(), MemberError> {
        self.journal.close();
        self.journal.reopen().map_err(MemberError::from)?;
        self.reset_memory_state();
        Ok(())
    }

    /// Rejoins the member after validating its cursor.
    ///
    /// # Errors
    ///
    /// Returns [`MemberError::RejoinRejected`] when validation refuses
    /// admission, and the reopen's typed failures otherwise.
    pub fn rejoin(&mut self, previous: &LaneCursor) -> Result<LaneCursor, MemberError> {
        self.restart()?;
        let derived = self.cursor()?;
        if !cursor_progression_is_valid(previous, &derived) {
            let rejection = RejoinRejection {
                previous: previous.high_water(),
                derived: derived.high_water(),
                reason: RejoinReason::CursorRegression,
            };
            self.last_rejection = Some(rejection.clone());
            self.activated = false;
            return Err(MemberError::RejoinRejected(rejection));
        }
        self.activated = true;
        Ok(derived)
    }

    /// Returns the bounded, observable conflict history of this member.
    #[must_use]
    pub fn conflicts(&self) -> &[ConflictRecord] {
        &self.conflicts
    }

    /// Returns the observable tenant-isolation denial log.
    #[must_use]
    pub fn denials(&self) -> &[TenantDenial] {
        &self.denials
    }

    /// Returns the observable catch-up fallbacks this member performed.
    #[must_use]
    pub fn fallbacks(&self) -> &[CatchUpFallback] {
        &self.fallbacks
    }

    /// Returns the observable below-epoch batches this member fenced.
    #[must_use]
    pub fn fenced(&self) -> &[FencedBatch] {
        &self.fenced
    }

    /// Returns the observable ownership transitions that deposed this
    /// member.
    #[must_use]
    pub fn depositions(&self) -> &[Deposition] {
        &self.depositions
    }

    /// Returns the observable envelope-discipline rejections.
    #[must_use]
    pub fn envelope_rejections(&self) -> &[EnvelopeRejection] {
        &self.envelope_rejections
    }

    /// Returns the last rejoin rejection, when validation refused admission.
    #[must_use]
    pub fn last_rejection(&self) -> Option<&RejoinRejection> {
        self.last_rejection.as_ref()
    }

    /// Returns the advisory, in-memory progress for one tenant.
    #[must_use]
    pub fn advisory_progress(&self, tenant: &TenantId) -> Option<u64> {
        self.advisory
            .iter()
            .filter(|((recorded, _), _)| recorded == tenant)
            .filter_map(|(_, progress)| progress.applied_through)
            .max()
    }

    /// Returns the member's health observation, which never carries or
    /// implies a convergence fact.
    #[must_use]
    pub fn health(&self) -> MemberStatus {
        if self.journal.is_closed() {
            return MemberStatus::Unavailable;
        }
        if self.last_rejection.is_some() || !self.denials.is_empty() {
            return MemberStatus::Degraded;
        }
        if self.is_lagging() {
            return MemberStatus::Stale;
        }
        if self.answered {
            return MemberStatus::Responsive;
        }
        if self.activated {
            return MemberStatus::Active;
        }
        MemberStatus::Assigned
    }

    /// Reports whether any tracked tenant is behind its last-known source
    /// high water.
    #[must_use]
    pub fn is_lagging(&self) -> bool {
        self.advisory.values().any(|progress| {
            match (progress.applied_through, progress.tenant_high_water) {
                (Some(applied), Some(water)) => applied < water,
                (None, Some(_)) => true,
                (_, None) => false,
            }
        })
    }

    /// Finds one committed operation in this member's journal with its
    /// stamp. The recorded operation sequence positions the read at one
    /// journal page (SE-07 budget E-7); the forward paging scan remains
    /// only as the fallback for a member whose in-memory sequences were
    /// cleared by a restart.
    fn find_commit(&self, tenant: &TenantId, operation: &OperationId) -> Option<LaneCommit> {
        let stamp = self.operation_stamps.get(operation).copied()?;
        if let Some(sequence) = self.operation_sequences.get(operation).copied() {
            let page = self.journal.tenant_commits(tenant, sequence.saturating_sub(1), 1).ok()?;
            if let Some(mut commit) = page.into_iter().next()
                && commit.operation == *operation
            {
                commit.stamp = stamp;
                return Some(commit);
            }
            return None;
        }
        let mut after = 0;
        loop {
            let page = self.journal.tenant_commits(tenant, after, DEFAULT_CATCH_UP_BATCH).ok()?;
            let page_len = page.len();
            let last_sequence = page.last().map(|commit| commit.sequence)?;
            if let Some(mut commit) = page.into_iter().find(|commit| &commit.operation == operation)
            {
                commit.stamp = stamp;
                return Some(commit);
            }
            if page_len < DEFAULT_CATCH_UP_BATCH as usize {
                return None;
            }
            after = last_sequence;
        }
    }

    /// Builds one envelope under the discipline header, minting a fresh
    /// message identity and stamping the issue tick from the member's clock.
    #[allow(
        clippy::too_many_arguments,
        reason = "one builder covering the full envelope discipline"
    )]
    fn build_envelope(
        &mut self,
        kind: FrameKind,
        target: MemberId,
        correlation_id: Option<u64>,
        causation_id: Option<u64>,
        idempotency_key: Option<OperationId>,
        range: Option<String>,
        epoch: Option<u64>,
        payload: Payload,
    ) -> Envelope {
        self.message_ids += 1;
        Envelope {
            kind,
            message_id: self.message_ids,
            correlation_id,
            causation_id,
            idempotency_key,
            source: self.id.clone(),
            target,
            issued_at: LaneInstant::at_ticks(self.hlc.ticks()),
            expires_at: None,
            range,
            epoch,
            payload,
        }
    }

    /// Encodes and sends one envelope through the transport.
    fn send(
        &mut self,
        transport: &mut dyn MemberTransport,
        to: &MemberId,
        envelope: &Envelope,
    ) -> Result<(), MemberError> {
        let frame = encode(envelope).map_err(MemberError::Envelope)?;
        transport.send(&self.id, to, frame).map_err(MemberError::from)
    }

    /// Sends one reply envelope, deferring a slow consumer instead of
    /// failing the dispatch pass: when the addressed peer's bounded link
    /// sits at capacity the deferral is recorded observably and `false`
    /// is returned — every other peer's work in the pass continues
    /// (SE-07 budget E-5). Any other failure propagates.
    fn send_or_defer(
        &mut self,
        transport: &mut dyn MemberTransport,
        to: &MemberId,
        envelope: &Envelope,
        report: &mut DispatchReport,
    ) -> Result<bool, MemberError> {
        match self.send(transport, to, envelope) {
            Ok(()) => Ok(true),
            Err(MemberError::TransportFull) => {
                self.record_slow_consumer(to);
                report.slow_consumer_deferrals += 1;
                Ok(false)
            }
            Err(other) => Err(other),
        }
    }

    /// Records one deferred slow consumer, bounded and deduplicated.
    fn record_slow_consumer(&mut self, member: &MemberId) {
        if self.slow_consumers.contains(member) {
            return;
        }
        if self.slow_consumers.len() >= MAX_OBSERVATION_LOGS {
            self.slow_consumers.remove(0);
        }
        self.slow_consumers.push(member.clone());
    }

    /// Raises the highest epoch seen for one range.
    fn record_epoch(&mut self, range: &str, epoch: u64) {
        let highest = self.highest_epoch_seen.entry(range.to_owned()).or_insert(0);
        if epoch > *highest {
            *highest = epoch;
        }
    }

    /// Records the epochs of every range the current view assigns.
    fn record_view_epochs(&mut self) {
        let stamps: Vec<(String, u64)> = self
            .config
            .topology
            .owned_ranges()
            .filter_map(|range| {
                self.config.topology.owner_epoch_of(range).map(|epoch| (range.to_owned(), epoch))
            })
            .collect();
        for (range, epoch) in stamps {
            self.record_epoch(&range, epoch);
        }
    }

    /// Refreshes the clock's physical component from a lane instant.
    fn refresh_clock_from(&mut self, at: LaneInstant) {
        if at.ticks() > self.hlc.ticks() {
            self.hlc = ClockStamp::new(at.ticks(), self.hlc.counter());
        }
    }

    /// Mints the next local stamp.
    fn tick_local_clock(&mut self) -> ClockStamp {
        self.hlc = ClockStamp::new(self.hlc.ticks(), self.hlc.counter().saturating_add(1));
        self.hlc
    }

    /// Records one conflict, deduplicated and bounded.
    fn record_conflict(&mut self, record: ConflictRecord) {
        if self.conflicts.iter().any(|existing| {
            existing.operations == record.operations && existing.outcome == record.outcome
        }) {
            return;
        }
        if self.conflicts.len() >= MAX_CONFLICTS {
            self.conflicts.remove(0);
        }
        self.conflicts.push(record);
    }

    /// Records one denial, bounded.
    fn record_denial(&mut self, denial: TenantDenial) {
        if self.denials.len() >= MAX_DENIALS {
            self.denials.remove(0);
        }
        self.denials.push(denial);
    }

    /// Records one fallback, bounded.
    fn record_fallback(&mut self, fallback: CatchUpFallback) {
        if self.fallbacks.len() >= MAX_OBSERVATION_LOGS {
            self.fallbacks.remove(0);
        }
        self.fallbacks.push(fallback);
    }

    /// Records one fenced batch, bounded.
    fn record_fenced(&mut self, batch: FencedBatch) {
        if self.fenced.len() >= MAX_OBSERVATION_LOGS {
            self.fenced.remove(0);
        }
        self.fenced.push(batch);
    }

    /// Records one deposition, bounded.
    fn record_deposition(&mut self, deposition: Deposition) {
        if self.depositions.len() >= MAX_OBSERVATION_LOGS {
            self.depositions.remove(0);
        }
        self.depositions.push(deposition);
    }

    /// Records one envelope-discipline rejection, bounded.
    fn record_rejection(&mut self, rejection: EnvelopeRejection) {
        if self.envelope_rejections.len() >= MAX_OBSERVATION_LOGS {
            self.envelope_rejections.remove(0);
        }
        self.envelope_rejections.push(rejection);
    }

    /// Clears the in-memory replication state that never survives a member
    /// restart.
    fn reset_memory_state(&mut self) {
        self.advisory.clear();
        self.operation_stamps.clear();
        self.operation_sequences.clear();
        self.operation_ranges.clear();
        self.key_states.clear();
        self.ack_states.clear();
        self.hlc = ClockStamp::new(0, 0);
        self.activated = false;
        self.answered = false;
    }
}

/// The dispatch report's delivered-change count: every commit a dispatch
/// received resolves to an applied entry, an idempotent duplicate, or a
/// recorded conflict, so their sum is the lane's delivered-changes divisor
/// for the per-change bytes-on-wire budget.
#[must_use]
pub const fn dispatch_report_deliveries(report: &DispatchReport) -> u64 {
    (report.applied + report.duplicates_resolved + report.conflicts_recorded) as u64
}

/// The D-3 disposition of one incoming commit.
enum Incoming {
    /// The write beats every competing state and applies.
    Apply,
    /// A competing state won; the atomic operation is skipped and the
    /// incoming write retained as history.
    ConflictSkipped,
}

/// The outcome of applying one commit.
enum ApplyStep {
    /// The commit applied through a fresh ledger entry.
    Applied,
    /// The durable operation ledger had already resolved the operation.
    Duplicate,
}

/// Decodes one frame, recording the discipline rejection when it fails.
fn decode_or_reject(
    log: &mut Vec<EnvelopeRejection>,
    frame: &EncodedFrame,
) -> Option<(Envelope, EncodedFrame)> {
    match crate::envelope::decode(&frame.bytes) {
        Ok(decoded) => Some(decoded),
        Err(reason) => {
            let kind = frame.peek_kind().ok();
            if log.len() >= MAX_OBSERVATION_LOGS {
                log.remove(0);
            }
            log.push(EnvelopeRejection { reason, kind });
            None
        }
    }
}

/// Returns the cursor position a source cannot serve below, if any.
///
/// A request is servable incrementally when the journal's oldest servable
/// sequence is at most one past the requester's cursor; anything earlier is
/// a gap that must fall back to a full catch-up, never a silent skip.
#[must_use]
pub fn catch_up_gap(after: Option<u64>, servable_from: Option<u64>) -> Option<u64> {
    let required_from = after.unwrap_or(0);
    let servable = servable_from.unwrap_or(0);
    if servable > required_from.saturating_add(1) { Some(required_from) } else { None }
}

/// Reports whether one derived cursor is at least as advanced as another.
fn cursor_progression_is_valid(previous: &LaneCursor, derived: &LaneCursor) -> bool {
    previous.member() == derived.member()
        && sequence_at_least(derived.high_water(), previous.high_water())
        && previous.tenants().all(|tenant| {
            sequence_at_least(derived.tenant_progress(tenant), previous.tenant_progress(tenant))
        })
}

/// Compares two optional sequences for "derived is not behind previous".
fn sequence_at_least(derived: Option<u64>, previous: Option<u64>) -> bool {
    match (derived, previous) {
        (Some(derived), Some(previous)) => derived >= previous,
        (None, Some(_)) => false,
        (_, None) => true,
    }
}

/// Returns the document keys one commit touches.
fn written_keys(tenant: &TenantId, commit: &LaneCommit) -> Vec<(TenantId, DocumentId)> {
    commit
        .writes
        .iter()
        .map(|write| match write {
            LaneWrite::Put { document } => document.id.clone(),
            LaneWrite::Delete { id, .. } => id.clone(),
        })
        .map(|id| (tenant.clone(), id))
        .collect()
}

/// Returns the key of one staged write under a tenant.
fn write_key(tenant: &TenantId, write: &LaneWrite) -> (TenantId, DocumentId) {
    let id = match write {
        LaneWrite::Put { document } => document.id.clone(),
        LaneWrite::Delete { id, .. } => id.clone(),
    };
    (tenant.clone(), id)
}

/// Merges one trusted peer stamp into the local clock.
fn merge_stamp(local: ClockStamp, peer: ClockStamp) -> ClockStamp {
    ClockStamp::new(local.ticks().max(peer.ticks()), local.counter().saturating_add(1))
}

/// Returns the distinct members in canonical order.
fn sorted_members(members: &[MemberId]) -> Vec<MemberId> {
    let mut sorted = members.to_vec();
    sorted.sort();
    sorted.dedup();
    sorted
}

/// Sorts the facts of one conflict record canonically, keeping clocks paired
/// with operations.
fn sort_record_facts(
    mut operations: Vec<OperationId>,
    mut clocks: Vec<ClockStamp>,
) -> (Vec<OperationId>, Vec<ClockStamp>) {
    let mut paired: Vec<(OperationId, ClockStamp)> =
        operations.drain(..).zip(clocks.drain(..)).collect();
    paired.sort_by(|left, right| left.0.cmp(&right.0));
    paired.into_iter().unzip()
}

/// Normalizes one requested level against the degenerate small-Region
/// lattices (D-4).
#[must_use]
pub fn normalize_level(
    level: AcknowledgementLevel,
    region_count: usize,
) -> (AcknowledgementLevel, Option<(AcknowledgementLevel, AcknowledgementLevel)>) {
    use AcknowledgementLevel::{AllRegions, Local, MajorityRegions, RegionQuorum};
    if level == Local {
        return (level, None);
    }
    if region_count <= 1 {
        let normalization = (level != RegionQuorum).then_some((level, RegionQuorum));
        return (RegionQuorum, normalization);
    }
    if region_count == 2 && level == AllRegions {
        return (MajorityRegions, Some((AllRegions, MajorityRegions)));
    }
    (level, None)
}

/// The quorum size of `total` Members or Regions.
fn quorum_of(total: usize) -> usize {
    total / 2 + 1
}

/// Reports whether the contributing Members satisfy one level for a write by
/// `writer`.
#[must_use]
pub fn level_satisfied(
    level: AcknowledgementLevel,
    topology: &TopologyView,
    writer: &MemberId,
    contributing: &BTreeSet<MemberId>,
) -> bool {
    use AcknowledgementLevel::{AllRegions, Local, MajorityRegions, RegionQuorum};
    if level == Local {
        return true;
    }
    let Some(home) = topology.region_of(writer).cloned() else { return false };
    let home_members = topology.members_of_region(&home);
    let home_quorum = home_members.iter().filter(|member| contributing.contains(*member)).count()
        >= quorum_of(home_members.len());
    match level {
        Local => true,
        RegionQuorum => home_quorum,
        MajorityRegions | AllRegions => {
            let regions: Vec<_> = topology.regions().cloned().collect();
            let regions_with_quorum = regions
                .iter()
                .filter(|region| {
                    let members = topology.members_of_region(region);
                    members.iter().filter(|member| contributing.contains(*member)).count()
                        >= quorum_of(members.len())
                })
                .count();
            if level == MajorityRegions {
                regions_with_quorum >= quorum_of(regions.len())
            } else {
                regions_with_quorum == regions.len()
            }
        }
    }
}

/// Returns the strongest level a contributing set actually achieves for a
/// write by `writer`.
#[must_use]
pub fn strongest_satisfied_level(
    topology: &TopologyView,
    writer: &MemberId,
    contributing: &BTreeSet<MemberId>,
) -> AcknowledgementLevel {
    use AcknowledgementLevel::{AllRegions, Local, MajorityRegions, RegionQuorum};
    for level in [AllRegions, MajorityRegions, RegionQuorum] {
        if level_satisfied(level, topology, writer, contributing) {
            return level;
        }
    }
    Local
}

/// Observes convergence for one operation across a set of members at an
/// injected lane instant: an observation taken by the caller, never a value
/// inferred from an acknowledgement or a health status.
#[must_use]
pub fn observe_convergence(
    operation: &OperationId,
    observers: &[&LaneMember],
    at: LaneInstant,
) -> Convergence {
    let mut holdings: Vec<(MemberId, AcknowledgementLevel)> = observers
        .iter()
        .filter(|member| {
            member.is_open()
                && matches!(
                    member.journal().resolve(operation),
                    Ok(DurableResolution::Committed(_))
                )
        })
        .map(|member| (member.id().clone(), AcknowledgementLevel::Local))
        .collect();
    holdings.sort_by(|left, right| left.0.cmp(&right.0));
    Convergence { operation: operation.clone(), holdings, observed_at: at }
}

/// Observes the loss of entire Regions after an acknowledged write: which
/// Regions and Members durably hold the operation at the loss instant, and
/// the recovery limit — the lost Regions' state is unknown until one of
/// their Members rejoins and validates its cursor. Nothing is guessed.
#[must_use]
pub fn observe_region_loss(
    operation: &OperationId,
    acknowledged: &AcknowledgementReport,
    observers: &[&LaneMember],
    lost: &[RegionId],
    at: LaneInstant,
) -> RegionLossObservation {
    let mut holders: BTreeMap<RegionId, Vec<MemberId>> = BTreeMap::new();
    for member in observers {
        let holds = member.is_open()
            && matches!(member.journal().resolve(operation), Ok(DurableResolution::Committed(_)));
        if holds && let Some(region) = member.topology().region_of(member.id()) {
            holders.entry(region.clone()).or_default().push(member.id().clone());
        }
    }
    RegionLossObservation {
        operation: operation.clone(),
        acknowledged: acknowledged.observed,
        durably_held_by: holders.into_iter().collect(),
        unknown_in: lost.to_vec(),
        observed_at: at,
    }
}
