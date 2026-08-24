//! Declaration-only replication seam: observable acknowledgement and
//! convergence facts across Regions and Members.
//!
//! This module declares the replication vocabulary of the W4 design contract
//! (`docs/design/w4-transfer-replication-contract.md`, "Public type
//! ownership") under the adjudicated decisions of ADR-0004 (D-1 single leader
//! per data range with cross-Region quorum, D-2 acknowledgement defaults, D-3
//! last-writer-wins on a bounded hybrid logical clock, D-4 three synthetic
//! Regions with no production claims). No replication behavior exists here:
//! the data plane is implemented by the W4-REPLICATION packet.
//!
//! Contract facts the declarations state: `Buffered`, `Written`, and `Synced`
//! remain local durability facts and are never substituted for
//! `AcknowledgementLevel` or `Convergence`; delivery is at-least-once with
//! stable operation identity; `RegionId`/`MemberId` sets are data-plane
//! identity, not authorization; tenant isolation is enforced at every seam;
//! and no transport frame, leader term, message codec, or leader/shard state
//! crosses this seam.
//!
//! ```compile_fail
//! // A local commit receipt carries no acknowledgement fact, so no such
//! // accessor can exist on it.
//! let receipt: terrabase_v2::CommitReceipt = unimplemented!();
//! let _ = receipt.acknowledgement();
//! ```
//!
//! ```compile_fail
//! // Health is never a convergence fact: a `ReplicationStatus` cannot be
//! // turned into a `Convergence` observation (R-2 owns convergence content).
//! let status = terrabase_v2::ReplicationStatus::Active;
//! let _ = status.convergence();
//! ```

mod identifier;
mod member;
mod status;
mod topology;
mod transport;

#[cfg(test)]
mod tests;

use std::marker::PhantomData;

use crate::types::{OperationId, StorageSequence, TenantId};

/// A deterministic instant of the replication lane's clock.
///
/// The lane clock advances exactly one tick per transport pump; nothing in
/// the replication seam reads wall time. An instant is a point in that
/// clock: deadlines, observation times, and timeout bounds are caller-
/// injected values of this type, so every timing behavior in the seam is
/// reproducible by driving the same pumps. It is a lane-scoped fact and
/// carries no wall-clock or production timing claim.
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct ReplicationInstant {
    ticks: u64,
}

impl ReplicationInstant {
    /// Creates the instant at `ticks` lane ticks.
    #[must_use]
    pub const fn at_ticks(ticks: u64) -> Self {
        Self { ticks }
    }

    /// Returns the lane tick of this instant.
    #[must_use]
    pub const fn ticks(self) -> u64 {
        self.ticks
    }

    /// Returns the instant `ticks` pumps later than this one.
    #[must_use]
    pub const fn plus_ticks(self, ticks: u64) -> Self {
        Self { ticks: self.ticks.saturating_add(ticks) }
    }
}

/// One stamp of a Member's bounded hybrid logical clock.
///
/// The stamp pairs the physical component — lane ticks observed from the
/// shared transport clock or an injected instant — with a logical counter
/// that orders writes inside one tick. Stamps order by physical time first
/// and the counter second, which is the ordering the D-3 last-writer-wins
/// policy decides with; the tie past that is the deterministic Member
/// identity order. Lane-scoped: no wall-clock claim.
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct ReplicationClockStamp {
    ticks: u64,
    counter: u64,
}

impl ReplicationClockStamp {
    /// Creates one stamp from its physical ticks and logical counter.
    #[must_use]
    pub const fn new(ticks: u64, counter: u64) -> Self {
        Self { ticks, counter }
    }

    /// Returns the physical component, in lane ticks.
    #[must_use]
    pub const fn ticks(self) -> u64 {
        self.ticks
    }

    /// Returns the logical counter within the physical tick.
    #[must_use]
    pub const fn counter(self) -> u64 {
        self.counter
    }
}

/// A validated logical Region identifier (CONTEXT: Region).
///
/// `Display` renders the canonical identifier. Validation follows the
/// crate's logical-identifier rules; see [`RegionId::try_new`].
#[derive(Clone, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct RegionId {
    identifier: String,
}

/// A validated logical Member identifier (CONTEXT: Member).
///
/// `Display` renders the canonical identifier. Validation follows the
/// crate's logical-identifier rules; see [`MemberId::try_new`].
#[derive(Clone, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct MemberId {
    identifier: String,
}

/// A caller-supplied membership view: Regions, Members, Failure Domain
/// grouping, and per-range ownership under the adjudicated algorithm.
///
/// `TopologyView` is an immutable snapshot of a membership view and an input,
/// not a decision: provisioning, placement, routing, lifecycle, membership
/// policy, and control-plane replication of topology itself stay with the
/// Platform/Orchestrator control plane (ADR-0004 D-1: one Member owns each
/// data range's write path). A view is built through the validated
/// [`TopologyView::new`] builder chain and becomes servable only after
/// [`TopologyView::validated`] accepts it.
#[derive(Clone, Debug, Default, Eq, PartialEq)]
pub struct TopologyView {
    /// The Regions named by the view, in canonical order.
    regions: Vec<RegionId>,
    /// Every Member's seat: its Region and its Failure Domain.
    members: Vec<MemberSeat>,
    /// The data ranges with an admitted owner, in canonical order.
    owners: Vec<RangeOwnership>,
}

/// One Member's seat in a [`TopologyView`].
#[derive(Clone, Debug, Eq, PartialEq)]
struct MemberSeat {
    member: MemberId,
    region: RegionId,
    failure_domain: String,
}

/// One data range's write-path ownership in a [`TopologyView`].
#[derive(Clone, Debug, Eq, PartialEq)]
struct RangeOwnership {
    range: String,
    owner: MemberId,
    /// The ownership epoch: monotonically raised by every control-plane
    /// transition of this range, so a deposed owner can be fenced (D-1).
    epoch: u64,
}

/// The strongest state a Region or Member has confirmed for a replicated
/// record (CONTEXT: Acknowledgement Level; ADR-0004 D-2).
///
/// - `Local`: the local storage publication rule completed (the local commit
///   receipt fact); no remote Member confirmed anything.
/// - `RegionQuorum`: a quorum of Members in the write's home Region durably
///   recorded the operation.
/// - `MajorityRegions`: a majority of Regions each durably recorded the
///   operation through a Region quorum.
/// - `AllRegions`: every Region in the topology durably recorded the
///   operation.
///
/// The default is `Local` (V1 parity); stronger levels are explicit per
/// operation. A Member contributes to any level above `Local` only after its
/// own local publication reaches `LocalDurability::Synced`. No level implies
/// cross-Region read-your-writes unless the Region is named in the report, and
/// `Convergence` is its own observation, never inferred from an
/// acknowledgement. Lattice degeneracy at small Region counts (with one
/// Region, `RegionQuorum` = `MajorityRegions` = `AllRegions`; with two
/// Regions, `MajorityRegions` = `AllRegions`) is contract fact; the W4 lane
/// exercises the non-degenerate three-Region case (ADR-0004 D-4).
#[derive(Clone, Copy, Debug, Eq, Hash, PartialEq)]
pub enum AcknowledgementLevel {
    /// The local storage publication rule completed; no remote Member
    /// confirmed anything.
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
    /// Returns [`AcknowledgementLevel::Local`]: V1 parity keeps the local
    /// save as the default and every stronger level an explicit per-request
    /// input (ADR-0004 D-2).
    fn default() -> Self {
        Self::Local
    }
}

/// The observed acknowledgement for one replicated operation.
///
/// Carries the observed level, the Region and Member set that contributed,
/// and the observation time; it names Members explicitly (PB-5026, PB-5042).
/// Any normalization of a degenerate level request is recorded here, so
/// reports stay comparable across topologies.
///
/// Contributing means the Member's own local publication of the operation
/// reached [`LocalDurability::Synced`](crate::types::LocalDurability::Synced)
/// — the only local level with a durable-recoverability claim (ADR-0004
/// D-2). A Region appears with the Members of that Region that contributed,
/// both in canonical order.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct AcknowledgementReport {
    /// The level the request named.
    requested: AcknowledgementLevel,
    /// The level actually observed and reported, in the topology's canonical
    /// naming.
    observed: AcknowledgementLevel,
    /// The Regions and their contributing Members, in canonical order.
    contributed: Vec<(RegionId, Vec<MemberId>)>,
    /// The lane instant the observation was taken at.
    observed_at: ReplicationInstant,
    /// The degenerate normalization that produced `observed` from
    /// `requested`, when the topology does not distinguish them (D-4).
    normalization: Option<(AcknowledgementLevel, AcknowledgementLevel)>,
}

impl AcknowledgementReport {
    /// Creates one report from its observed facts.
    pub(crate) fn new(
        requested: AcknowledgementLevel,
        observed: AcknowledgementLevel,
        contributed: Vec<(RegionId, Vec<MemberId>)>,
        observed_at: ReplicationInstant,
        normalization: Option<(AcknowledgementLevel, AcknowledgementLevel)>,
    ) -> Self {
        Self { requested, observed, contributed, observed_at, normalization }
    }

    /// Returns the acknowledgement level the request named.
    #[must_use]
    pub fn requested(&self) -> AcknowledgementLevel {
        self.requested
    }

    /// Returns the acknowledgement level observed and reported.
    #[must_use]
    pub fn observed(&self) -> AcknowledgementLevel {
        self.observed
    }

    /// Returns the Regions that contributed, each with its contributing
    /// Members, in canonical order.
    #[must_use]
    pub fn contributed(&self) -> &[(RegionId, Vec<MemberId>)] {
        &self.contributed
    }

    /// Returns the lane instant this observation was taken at.
    #[must_use]
    pub fn observed_at(&self) -> ReplicationInstant {
        self.observed_at
    }

    /// Returns the degenerate normalization recorded in this report, as
    /// `(requested, canonical)`, when the topology does not distinguish the
    /// requested level from a weaker name (D-4: with one Region,
    /// `RegionQuorum` = `MajorityRegions` = `AllRegions`; with two,
    /// `MajorityRegions` = `AllRegions`).
    #[must_use]
    pub fn normalization(&self) -> Option<(AcknowledgementLevel, AcknowledgementLevel)> {
        self.normalization
    }
}

/// The contract-defined converged-state observation for one operation
/// identity (PB-5043).
///
/// Names which Members hold the operation, at which acknowledgement state,
/// and when it was observed. `Convergence` is reported as its own observation
/// and is never inferred from an acknowledgement: it is produced by its own
/// seam at a caller-injected lane instant. Each holding is the Member's own
/// local publication fact of that operation, so a holding carries
/// [`AcknowledgementLevel::Local`].
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct Convergence {
    /// The operation identity the observation is about.
    operation: OperationId,
    /// Every observed Member that durably holds the operation, in canonical
    /// Member order, each at its own acknowledgement state.
    holdings: Vec<(MemberId, AcknowledgementLevel)>,
    /// The lane instant the observation was taken at.
    observed_at: ReplicationInstant,
}

#[allow(dead_code, reason = "consumed by the R-2 observation seam and its lane tests")]
impl Convergence {
    /// Creates one convergence observation from its facts.
    pub(crate) fn new(
        operation: OperationId,
        holdings: Vec<(MemberId, AcknowledgementLevel)>,
        observed_at: ReplicationInstant,
    ) -> Self {
        Self { operation, holdings, observed_at }
    }

    /// Returns the operation identity this observation is about.
    #[must_use]
    pub fn operation(&self) -> &OperationId {
        &self.operation
    }

    /// Returns every Member that durably holds the operation, in canonical
    /// Member order, each with its own acknowledgement state.
    #[must_use]
    pub fn holdings(&self) -> &[(MemberId, AcknowledgementLevel)] {
        &self.holdings
    }

    /// Returns the lane instant this observation was taken at.
    #[must_use]
    pub fn observed_at(&self) -> ReplicationInstant {
        self.observed_at
    }
}

/// The outcome of one replication request at an explicit acknowledgement
/// level (PB-5026).
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum ReplicationOutcome {
    /// The requested acknowledgement level was observed; the report names the
    /// Regions and Members that contributed.
    Acknowledged(AcknowledgementReport),
    /// The acknowledgement deadline elapsed. Distinct from any local commit
    /// failure: the partial-ack observation names which Members had durably
    /// recorded the operation when the deadline hit (ADR-0004 D-2).
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

/// A health observation for the replication data plane (PB-5032).
///
/// Health is reported separately from convergence state: `ReplicationStatus`
/// never carries or implies a `Convergence` fact.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ReplicationStatus {
    /// The member or range is assigned but not yet active.
    Assigned,
    /// The member or range is actively serving its assignment.
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

/// A secret-safe record of one qualification scenario (PB-5042..PB-5047).
///
/// Names the scenario identity, topology (Regions, Members, failure
/// domains), fault classes, acknowledgement facts per operation, the
/// convergence observation and its condition, measured RPO/RTO/convergence/
/// lag with their definitions, and explicit limits. It carries no document
/// content, credential, connection string, or private host path.
///
/// R-1 implements no receipt content: the qualification lane and its
/// artifacts belong to the R-3 phase of W4-REPLICATION.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ReplicationReceipt {
    #[allow(dead_code, reason = "reserved for R-3: qualification lane artifacts")]
    receipt: PhantomData<()>,
}

/// A content-free conflict observation (PB-5022).
///
/// Carries identities, vectors or clocks, and the recorded policy outcome —
/// under ADR-0004 D-3, deterministic last-writer-wins on a bounded hybrid
/// logical clock, with losers retained as observable history. No conflicting
/// document payload crosses this type.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ConflictRecord {
    /// The conflicting operation identities, in canonical order.
    operations: Vec<OperationId>,
    /// The writer identities of the conflicting operations, in canonical
    /// order.
    members: Vec<MemberId>,
    /// The bounded hybrid logical clock stamps of the conflicting writes,
    /// paired with `operations` order.
    clocks: Vec<ReplicationClockStamp>,
    /// The recorded policy outcome.
    outcome: ConflictOutcome,
}

impl ConflictRecord {
    /// Creates one conflict record from its content-free facts.
    pub(crate) fn new(
        operations: Vec<OperationId>,
        members: Vec<MemberId>,
        clocks: Vec<ReplicationClockStamp>,
        outcome: ConflictOutcome,
    ) -> Self {
        Self { operations, members, clocks, outcome }
    }

    /// Returns the conflicting operation identities, in canonical order.
    #[must_use]
    pub fn operations(&self) -> &[OperationId] {
        &self.operations
    }

    /// Returns the writer identities of the conflicting operations.
    #[must_use]
    pub fn members(&self) -> &[MemberId] {
        &self.members
    }

    /// Returns the clock stamps of the conflicting writes.
    #[must_use]
    pub fn clocks(&self) -> &[ReplicationClockStamp] {
        &self.clocks
    }

    /// Returns the recorded policy outcome of the conflict.
    #[must_use]
    pub fn outcome(&self) -> ConflictOutcome {
        self.outcome.clone()
    }
}

/// The recorded policy outcome of one conflict (ADR-0004 D-3, D-1).
///
/// Every variant names the Member the recorded pair converged to, so a
/// conflict record always states the disposition, never just the detection.
#[derive(Clone, Debug, Eq, Hash, PartialEq)]
pub enum ConflictOutcome {
    /// Last-writer-wins on the bounded hybrid logical clock kept the write
    /// of this Member; the other write is retained as history.
    Winner(MemberId),
    /// The write came from a deposed owner and is retained as observable
    /// history under D-1; this Member names the deposed owner whose local
    /// receipt stays real locally while the write converges under D-3.
    DeposedOwnerRetained(MemberId),
    /// The peer's clock exceeded the skew bound, so its timestamps were
    /// treated as untrusted and the pair converged by the deterministic
    /// Member-identity tie-break naming this Member.
    UntrustedClock(MemberId),
}

/// Durable per-Member replication progress bound to storage sequence
/// (PB-5033).
///
/// A lagging or restarted Member resumes from its cursor, validates storage
/// identity and format before rejoining, and catches up without duplicating
/// committed mutations.
///
/// R-1 derives the cursor from the Member's own publication-history journal
/// (the ground truth of what that Member durably applied), so the cursor is
/// expressed in the Member's own storage sequence space and survives an
/// engine restart unchanged.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ReplicationCursor {
    /// The Member whose durable progress this cursor reports.
    member: MemberId,
    /// The last sequence the Member's own journal durably recorded.
    high_water: Option<StorageSequence>,
    /// Each committed tenant's latest journaled sequence at or below the
    /// high water, in canonical tenant order.
    tenants: Vec<(TenantId, StorageSequence)>,
}

/// A validated topology and policy request input.
///
/// `ReplicationConfig` does not authorize, provision, or route; the control
/// plane owns those concerns. It carries no credential or connection string.
/// The acknowledgement level is an explicit request input with a `Local`
/// default (ADR-0004 D-2); R-1 evaluates no stronger level.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ReplicationConfig {
    /// The validated membership view this configuration applies.
    topology: TopologyView,
    /// The acknowledgement level the caller names for its requests.
    acknowledgement: AcknowledgementLevel,
    /// The bounded number of commits one catch-up round requests.
    catch_up_batch: u32,
}

/// Typed replication failure categories.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ReplicationError {
    /// The addressed Member cannot be reached.
    MemberUnavailable,
    /// The required quorum cannot be reached.
    QuorumUnreachable,
    /// A Region was lost; the observation states durable visibility and
    /// recovery limits.
    RegionLost,
    /// A conflict was detected and recorded; see `ConflictRecord`.
    ConflictDetected,
    /// The operation is not replicated on this view.
    NotReplicated,
    /// The supplied topology view is invalid.
    InvalidTopology,
    /// The `Engine` has closed.
    EngineClosed,
    /// The request deadline elapsed before the operation ran.
    Deadline,
    /// The operation was cancelled.
    Cancelled,
}
