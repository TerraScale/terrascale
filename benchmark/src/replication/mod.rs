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
#[cfg(test)]
mod lane;
#[cfg(test)]
mod lane_tests;
mod member;
#[cfg(test)]
mod observation_tests;
#[cfg(test)]
mod repair_tests;
mod retention;
#[cfg(test)]
mod retention_tests;
mod status;
mod topology;
mod transport;

#[cfg(test)]
mod tests;

use crate::types::{OperationId, StorageSequence, TenantId};

// The tombstone retention watermark is module-level public (GAP-R02): the
// backup seam carries it on a `BackupPlan`, so it is reachable as
// `terrabase_v2::replication::*` while staying out of the crate root's
// re-export list, which the integrator owns.
pub use retention::{
    ConsumerObservation, DeletionEvidence, MAX_RETENTION_REFUSALS, RetentionAdvance,
    RetentionCompaction, RetentionConsumer, RetentionHold, RetentionRefusal,
    SharedTombstoneRetention, TombstoneRetention,
};

// The journal-extraction pair a history-bound declaration hands the
// watermark (GAP-R02): crate-visible so the backup suite builds the same
// window the member extracts from its own journal, and so the GAP-S1
// compaction driver (`Engine::compact_with_retention`) pairs the window it
// extracted with the origin clock stamp durable evidence carries.
pub(crate) use transport::StampedCommit;

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
/// content, credential, connection string, or private host path: identities,
/// counts, sequence windows, tick counts, and verdicts only.
///
/// Every measurement is a lane-scoped fact: RPO is a count and a
/// storage-sequence window, RTO and the convergence interval are counted in
/// lane ticks (one per network pump), and replication lag is a sequence
/// distance. A value that a scenario cannot observe is reported as
/// not-measured through a limit naming the reason, never guessed.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ReplicationReceipt {
    /// The scenario identity.
    scenario: String,
    /// The Regions of the lane topology, in canonical order.
    regions: Vec<RegionId>,
    /// Every Member's Region seat, in canonical order.
    members: Vec<(MemberId, RegionId)>,
    /// Each failure domain with its Members.
    failure_domains: Vec<(String, Vec<MemberId>)>,
    /// The fault classes the scenario exercised.
    faults: Vec<FaultClass>,
    /// The acknowledgement facts per operation: identity, observed level,
    /// and the Members that contributed.
    acknowledgements: Vec<(OperationId, AcknowledgementLevel, Vec<MemberId>)>,
    /// The convergence observation, when the scenario produced one.
    convergence: Option<Convergence>,
    /// The condition the convergence observation checked.
    convergence_condition: String,
    /// The measured RPO: a count of acknowledged-but-absent operations and
    /// their storage-sequence window; `None` when not measured.
    rpo: Option<(usize, Option<(StorageSequence, StorageSequence)>)>,
    /// The measured RTO in lane ticks; `None` when not measured.
    rto_ticks: Option<u64>,
    /// The measured convergence interval in lane ticks; `None` when not
    /// measured.
    convergence_ticks: Option<u64>,
    /// The observed replication lag per Member, as a sequence distance.
    lag_distances: Vec<(MemberId, u64)>,
    /// The explicit limits of the scenario.
    limits: Vec<String>,
}

/// The RPO measurement definition (contract-verbatim, lane-scoped).
pub const RPO_DEFINITION: &str = "The set of operations acknowledged at the requested level but \
absent from every surviving Member after the recorded fault, reported as a count and a \
storage-sequence window; never a time guess.";

/// The RTO measurement definition (contract-verbatim, lane clock stated).
pub const RTO_DEFINITION: &str = "The interval from fault injection to the first post-fault \
successful operation satisfying the availability condition for the surviving topology, in lane \
ticks of the lane clock (one tick per network pump); lane time only.";

/// The convergence measurement definition (contract-verbatim, lane clock
/// stated).
pub const CONVERGENCE_DEFINITION: &str = "The interval from the last healing event to the first \
Convergence observation naming every surviving Member, in lane ticks of the lane clock, together \
with the condition checked.";

/// The replication lag measurement definition (contract-verbatim).
pub const LAG_DEFINITION: &str = "The storage-sequence distance between a Member's durable \
replication cursor and the source's published sequence at observation time; a sequence distance, \
not a latency claim.";

impl ReplicationReceipt {
    /// Starts one receipt for a scenario run over a topology view.
    #[allow(dead_code, reason = "assembled by the R-3 lane harness under cfg(test)")]
    pub(crate) fn new(scenario: &str, topology: &TopologyView) -> Self {
        let members: Vec<(MemberId, RegionId)> = topology
            .members()
            .filter_map(|member| {
                topology.region_of(member).map(|region| (member.clone(), region.clone()))
            })
            .collect();
        let failure_domains: Vec<(String, Vec<MemberId>)> = topology
            .failure_domains()
            .into_iter()
            .map(|domain| {
                (
                    domain.to_owned(),
                    topology
                        .members()
                        .filter(|member| topology.failure_domain_of(member) == Some(domain))
                        .cloned()
                        .collect(),
                )
            })
            .collect();
        Self {
            scenario: scenario.to_owned(),
            regions: topology.regions().cloned().collect(),
            members,
            failure_domains,
            faults: Vec::new(),
            acknowledgements: Vec::new(),
            convergence: None,
            convergence_condition: String::new(),
            rpo: None,
            rto_ticks: None,
            convergence_ticks: None,
            lag_distances: Vec::new(),
            limits: Vec::new(),
        }
    }

    /// Names the fault classes the scenario exercised.
    #[allow(dead_code, reason = "assembled by the R-3 lane harness under cfg(test)")]
    pub(crate) fn with_faults(mut self, faults: &[FaultClass]) -> Self {
        self.faults = faults.to_vec();
        self
    }

    /// Records one acknowledgement fact with its contributing Members.
    #[allow(dead_code, reason = "assembled by the R-3 lane harness under cfg(test)")]
    pub(crate) fn with_acknowledgement(
        mut self,
        operation: &OperationId,
        level: AcknowledgementLevel,
        report: &AcknowledgementReport,
    ) -> Self {
        let contributing =
            report.contributed().iter().flat_map(|(_, members)| members.iter().cloned()).collect();
        self.acknowledgements.push((operation.clone(), level, contributing));
        self
    }

    /// Records one acknowledgement fact from its contributing Members
    /// directly, for acknowledgement observations that were not lattice
    /// reports (a local publication).
    #[allow(dead_code, reason = "assembled by the R-3 lane harness under cfg(test)")]
    pub(crate) fn with_acknowledgement_members(
        mut self,
        operation: &OperationId,
        level: AcknowledgementLevel,
        contributing: &[MemberId],
    ) -> Self {
        self.acknowledgements.push((operation.clone(), level, contributing.to_vec()));
        self
    }

    /// Records the convergence observation and the condition it checked.
    #[allow(dead_code, reason = "assembled by the R-3 lane harness under cfg(test)")]
    pub(crate) fn with_convergence(mut self, convergence: Convergence, condition: &str) -> Self {
        self.convergence = Some(convergence);
        self.convergence_condition.clear();
        self.convergence_condition.push_str(condition);
        self
    }

    /// Records the measured RPO as a count and an optional sequence window.
    #[allow(dead_code, reason = "assembled by the R-3 lane harness under cfg(test)")]
    pub(crate) fn with_rpo(
        mut self,
        count: usize,
        window: Option<(StorageSequence, StorageSequence)>,
    ) -> Self {
        self.rpo = Some((count, window));
        self
    }

    /// Records the measured RTO in lane ticks.
    #[allow(dead_code, reason = "assembled by the R-3 lane harness under cfg(test)")]
    pub(crate) fn with_rto_ticks(mut self, ticks: u64) -> Self {
        self.rto_ticks = Some(ticks);
        self
    }

    /// Records the measured convergence interval in lane ticks.
    #[allow(dead_code, reason = "assembled by the R-3 lane harness under cfg(test)")]
    pub(crate) fn with_convergence_ticks(mut self, ticks: u64) -> Self {
        self.convergence_ticks = Some(ticks);
        self
    }

    /// Records the observed replication lag per Member.
    #[allow(dead_code, reason = "assembled by the R-3 lane harness under cfg(test)")]
    pub(crate) fn with_lag(mut self, distances: Vec<(MemberId, u64)>) -> Self {
        self.lag_distances = distances;
        self
    }

    /// Records one explicit limit.
    #[allow(dead_code, reason = "assembled by the R-3 lane harness under cfg(test)")]
    pub(crate) fn with_limit(mut self, limit: &str) -> Self {
        self.limits.push(limit.to_owned());
        self
    }

    /// Returns the scenario identity.
    #[must_use]
    pub fn scenario(&self) -> &str {
        &self.scenario
    }

    /// Returns the Regions of the recorded topology, in canonical order.
    #[must_use]
    pub fn regions(&self) -> &[RegionId] {
        &self.regions
    }

    /// Returns every Member's Region seat, in canonical order.
    #[must_use]
    pub fn members(&self) -> &[(MemberId, RegionId)] {
        &self.members
    }

    /// Returns each failure domain with its Members.
    #[must_use]
    pub fn failure_domains(&self) -> &[(String, Vec<MemberId>)] {
        &self.failure_domains
    }

    /// Returns the fault classes the scenario exercised.
    #[must_use]
    pub fn faults(&self) -> &[FaultClass] {
        &self.faults
    }

    /// Returns the acknowledgement facts: per operation, the observed level
    /// and the Members that contributed.
    #[must_use]
    pub fn acknowledgements(&self) -> &[(OperationId, AcknowledgementLevel, Vec<MemberId>)] {
        &self.acknowledgements
    }

    /// Returns the convergence observation, when the scenario produced one.
    #[must_use]
    pub fn convergence(&self) -> Option<&Convergence> {
        self.convergence.as_ref()
    }

    /// Returns the condition the convergence observation checked.
    #[must_use]
    pub fn convergence_condition(&self) -> &str {
        &self.convergence_condition
    }

    /// Returns the measured RPO: a count and an optional storage-sequence
    /// window, or `None` when the scenario did not measure it.
    #[must_use]
    pub fn rpo(&self) -> Option<(usize, Option<(StorageSequence, StorageSequence)>)> {
        self.rpo
    }

    /// Returns the RPO measurement definition.
    #[must_use]
    pub fn rpo_definition(&self) -> &'static str {
        RPO_DEFINITION
    }

    /// Returns the measured RTO in lane ticks, or `None` when the scenario
    /// did not measure it.
    #[must_use]
    pub fn rto_ticks(&self) -> Option<u64> {
        self.rto_ticks
    }

    /// Returns the RTO measurement definition.
    #[must_use]
    pub fn rto_definition(&self) -> &'static str {
        RTO_DEFINITION
    }

    /// Returns the measured convergence interval in lane ticks, or `None`
    /// when the scenario did not measure it.
    #[must_use]
    pub fn convergence_ticks(&self) -> Option<u64> {
        self.convergence_ticks
    }

    /// Returns the convergence measurement definition.
    #[must_use]
    pub fn convergence_definition(&self) -> &'static str {
        CONVERGENCE_DEFINITION
    }

    /// Returns the observed replication lag per Member, as a sequence
    /// distance.
    #[must_use]
    pub fn lag_distances(&self) -> &[(MemberId, u64)] {
        &self.lag_distances
    }

    /// Returns the replication lag measurement definition.
    #[must_use]
    pub fn lag_definition(&self) -> &'static str {
        LAG_DEFINITION
    }

    /// Returns the explicit limits of the scenario, including any
    /// not-measured measurement with its reason.
    #[must_use]
    pub fn limits(&self) -> &[String] {
        &self.limits
    }

    /// Renders the receipt as a secret-safe artifact summary: identities,
    /// counts, sequence windows, tick counts, definitions, and limits only.
    #[allow(clippy::too_many_lines, reason = "one artifact rendering, one line per fact group")]
    #[must_use]
    pub fn summary(&self) -> String {
        use std::fmt::Write as _;
        let members = self
            .members
            .iter()
            .map(|(member, region)| format!("{member} {region}"))
            .collect::<Vec<_>>()
            .join("; ");
        let domains = self
            .failure_domains
            .iter()
            .map(|(domain, members)| {
                format!(
                    "{} [{}]",
                    domain,
                    members.iter().map(MemberId::as_str).collect::<Vec<_>>().join(", ")
                )
            })
            .collect::<Vec<_>>()
            .join("; ");
        let faults = self.faults.iter().map(|fault| fault.name()).collect::<Vec<_>>().join(", ");
        let lag = self
            .lag_distances
            .iter()
            .map(|(member, distance)| format!("{member} {distance}"))
            .collect::<Vec<_>>()
            .join(", ");
        let mut out = String::new();
        writeln!(out, "scenario: {}", self.scenario).expect("writing to a String cannot fail");
        writeln!(
            out,
            "regions: {}",
            self.regions.iter().map(RegionId::as_str).collect::<Vec<_>>().join(", ")
        )
        .expect("writing to a String cannot fail");
        writeln!(out, "members: {members}").expect("writing to a String cannot fail");
        writeln!(out, "failure domains: {domains}").expect("writing to a String cannot fail");
        writeln!(out, "faults: {faults}").expect("writing to a String cannot fail");
        for (operation, level, members) in &self.acknowledgements {
            writeln!(
                out,
                "acknowledged: {operation} at {level:?} by {}",
                members.iter().map(MemberId::as_str).collect::<Vec<_>>().join(", ")
            )
            .expect("writing to a String cannot fail");
        }
        if let Some(convergence) = &self.convergence {
            writeln!(
                out,
                "convergence: {} held by {} at lane tick {}",
                convergence.operation(),
                convergence
                    .holdings()
                    .iter()
                    .map(|(member, _)| member.to_string())
                    .collect::<Vec<_>>()
                    .join(", "),
                convergence.observed_at().ticks()
            )
            .expect("writing to a String cannot fail");
            writeln!(out, "convergence condition: {}", self.convergence_condition)
                .expect("writing to a String cannot fail");
        } else {
            writeln!(out, "convergence: not observed in this scenario")
                .expect("writing to a String cannot fail");
        }
        match self.rpo {
            Some((count, Some((low, high)))) => {
                writeln!(
                    out,
                    "rpo: {count} lost, storage-sequence window {}..={}",
                    low.get(),
                    high.get()
                )
                .expect("writing to a String cannot fail");
            }
            Some((count, None)) => {
                writeln!(out, "rpo: {count} lost, no sequence window (zero loss)")
                    .expect("writing to a String cannot fail");
            }
            None => {
                writeln!(out, "rpo: not measured in this scenario")
                    .expect("writing to a String cannot fail");
            }
        }
        writeln!(out, "rpo definition: {RPO_DEFINITION}").expect("writing to a String cannot fail");
        match self.rto_ticks {
            Some(ticks) => {
                writeln!(out, "rto: {ticks} lane ticks").expect("writing to a String cannot fail");
            }
            None => {
                writeln!(out, "rto: not measured in this scenario")
                    .expect("writing to a String cannot fail");
            }
        }
        writeln!(out, "rto definition: {RTO_DEFINITION}").expect("writing to a String cannot fail");
        match self.convergence_ticks {
            Some(ticks) => {
                writeln!(out, "convergence interval: {ticks} lane ticks")
                    .expect("writing to a String cannot fail");
            }
            None => {
                writeln!(out, "convergence interval: not measured in this scenario")
                    .expect("writing to a String cannot fail");
            }
        }
        writeln!(out, "convergence definition: {CONVERGENCE_DEFINITION}")
            .expect("writing to a String cannot fail");
        if self.lag_distances.is_empty() {
            writeln!(out, "replication lag: not measured in this scenario")
                .expect("writing to a String cannot fail");
        } else {
            writeln!(out, "replication lag: {lag}").expect("writing to a String cannot fail");
        }
        writeln!(out, "lag definition: {LAG_DEFINITION}").expect("writing to a String cannot fail");
        for limit in &self.limits {
            writeln!(out, "limit: {limit}").expect("writing to a String cannot fail");
        }
        writeln!(out, "claim: none beyond the lane; lane-scoped facts only")
            .expect("writing to a String cannot fail");
        out
    }
}

/// One fault class a lane scenario exercised (PB-5042..PB-5047).
///
/// The class names the fault's kind only: no member identity, link
/// direction, instant, or payload rides on it, so a receipt naming its
/// faults stays secret-safe by construction.
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
    /// A Member's engine was closed and reopened from its directory.
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
