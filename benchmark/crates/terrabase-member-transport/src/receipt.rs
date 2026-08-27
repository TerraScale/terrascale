//! Lane-scoped, secret-safe replication receipts.
//!
//! One [`LaneReceipt`] records one SE-06 lane scenario run over an
//! engine-owned transport: scenario identity, topology (Regions, Members,
//! failure domains), fault classes, acknowledgement facts per operation, the
//! convergence observation and its condition, measured RPO/RTO/convergence/
//! lag with their definitions, the measured bytes-on-wire facts of
//! PD-2026-08-26-13 D1, and explicit limits. It carries no document content,
//! credential, connection string, or private host path: identities, counts,
//! sequence windows, tick counts, byte counts, and verdicts only.
//!
//! Every measurement is a lane-scoped fact. A value a scenario cannot
//! observe is reported as not-measured through a limit naming the reason,
//! never guessed.

use std::fmt::Write as _;

use crate::identity::{MemberId, RegionId};
use crate::member::{
    AcknowledgementLevel, AcknowledgementReport, Convergence, FaultClass, ReplicationOutcome,
};

/// The RPO measurement definition (contract-verbatim, lane-scoped).
pub const RPO_DEFINITION: &str = "The set of operations acknowledged at the requested level but \
absent from every surviving Member after the recorded fault, reported as a count and a \
journal-sequence window; never a time guess.";

/// The RTO measurement definition (contract-verbatim, lane clock stated).
pub const RTO_DEFINITION: &str = "The interval from fault injection to the first post-fault \
successful operation satisfying the availability condition for the surviving topology, in lane \
ticks of the lane clock (one tick per transport pump); lane time only.";

/// The convergence measurement definition (contract-verbatim, lane clock
/// stated).
pub const CONVERGENCE_DEFINITION: &str = "The interval from the last healing event to the first \
Convergence observation naming every surviving Member, in lane ticks of the lane clock, together \
with the condition checked.";

/// The replication lag measurement definition (contract-verbatim).
pub const LAG_DEFINITION: &str = "The journal-sequence distance between a Member's durable \
replication cursor and the source's published sequence at observation time; a sequence distance, \
not a latency claim.";

/// The bytes-on-wire measurement definition (PD-2026-08-26-13 D1).
pub const BYTES_ON_WIRE_DEFINITION: &str = "Encoded envelope bytes counted at the transport's \
delivery boundary: per frame class, the frames delivered, their payload bytes, total bytes, and \
envelope overhead (total minus payload), measured from the lane's per-direction counters; a byte \
count, not a bandwidth or latency claim.";

/// Which engine-owned lane a receipt ran on.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum LaneKind {
    /// The in-process lane: Members share a process boundary; bounded
    /// encoded-frame queues; fully deterministic (one tick per pump, no
    /// wall time anywhere).
    InProcess,
    /// The engine-socket lane: real QUIC sockets between member endpoints on
    /// loopback; the lane clock stays tick-based and every protocol
    /// vocabulary remains tick-valued, while each pump drains real socket
    /// traffic to a causal completion condition — every flushed frame has
    /// been read off the wire (a fixture fact, never a timing claim).
    EngineSocket,
}

impl LaneKind {
    /// The canonical lane name.
    #[must_use]
    pub const fn name(self) -> &'static str {
        match self {
            Self::InProcess => "in-process",
            Self::EngineSocket => "engine-socket",
        }
    }
}

/// The measured bytes-on-wire facts of one frame class.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct FrameClassBytes {
    /// The frames delivered.
    pub frames: u64,
    /// The payload bytes delivered.
    pub payload_bytes: u64,
    /// The total encoded bytes delivered.
    pub total_bytes: u64,
    /// The envelope overhead bytes (total minus payload).
    pub overhead_bytes: u64,
}

/// The bytes-on-wire accounting of a lane run.
#[derive(Clone, Debug, Default, Eq, PartialEq)]
pub struct BytesOnWire {
    /// Per frame class, the delivered counts and bytes.
    pub per_class: Vec<(&'static str, FrameClassBytes)>,
    /// The committed changes delivered (the divisor of the per-change
    /// budget).
    pub changes_delivered: u64,
    /// Whether every frame class stayed at or under the written envelope
    /// overhead budget.
    pub overhead_within_budget: bool,
    /// Whether the delivered payload bytes per change stayed at or under the
    /// written per-change budget.
    pub per_change_within_budget: bool,
}

/// A secret-safe record of one lane scenario run.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct LaneReceipt {
    /// The scenario identity.
    pub scenario: String,
    /// The engine-owned lane kind the scenario ran on.
    pub lane: LaneKind,
    /// The Regions of the lane topology, in canonical order.
    pub regions: Vec<RegionId>,
    /// Every Member's Region seat, in canonical order.
    pub members: Vec<(MemberId, RegionId)>,
    /// Each failure domain with its Members.
    pub failure_domains: Vec<(String, Vec<MemberId>)>,
    /// The fault classes the scenario exercised.
    pub faults: Vec<FaultClass>,
    /// The acknowledgement facts per operation.
    pub acknowledgements: Vec<(String, AcknowledgementLevel, Vec<MemberId>)>,
    /// The convergence observation, when the scenario produced one.
    pub convergence: Option<Convergence>,
    /// The condition the convergence observation checked.
    pub convergence_condition: String,
    /// The measured RPO: a count of acknowledged-but-absent operations and
    /// their journal-sequence window; `None` when not measured.
    pub rpo: Option<(usize, Option<(u64, u64)>)>,
    /// The measured RTO in lane ticks; `None` when not measured.
    pub rto_ticks: Option<u64>,
    /// The measured convergence interval in lane ticks; `None` when not
    /// measured.
    pub convergence_ticks: Option<u64>,
    /// The observed replication lag per Member, as a sequence distance.
    pub lag_distances: Vec<(MemberId, u64)>,
    /// The measured bytes-on-wire facts of the member lane.
    pub bytes_on_wire: BytesOnWire,
    /// The explicit limits of the scenario.
    pub limits: Vec<String>,
}

impl LaneReceipt {
    /// Starts one receipt for a scenario run over a topology.
    #[must_use]
    pub fn new(scenario: &str, lane: LaneKind, topology: &crate::identity::TopologyView) -> Self {
        let members: Vec<(MemberId, RegionId)> = topology
            .members()
            .filter_map(|member| {
                topology.region_of(member).map(|region| (member.clone(), region.clone()))
            })
            .collect();
        let failure_domains: Vec<(String, Vec<MemberId>)> = topology
            .failure_domains()
            .into_iter()
            .map(|(name, _seated)| {
                (
                    name.clone(),
                    topology
                        .members()
                        .filter(|member| {
                            topology
                                .failure_domain_of(member)
                                .is_some_and(|domain| domain == name.as_str())
                        })
                        .cloned()
                        .collect(),
                )
            })
            .collect();
        Self {
            scenario: scenario.to_owned(),
            lane,
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
            bytes_on_wire: BytesOnWire::default(),
            limits: Vec::new(),
        }
    }

    /// Records one acknowledgement outcome with its contributing Members.
    #[must_use]
    pub fn with_acknowledgement(
        mut self,
        operation: &crate::identity::OperationId,
        outcome: &ReplicationOutcome,
    ) -> Self {
        if let ReplicationOutcome::Acknowledged(report) = outcome {
            self.record_ack(operation, report);
        }
        self
    }

    /// Records one acknowledgement report directly.
    #[must_use]
    pub fn with_acknowledgement_report(
        mut self,
        operation: &crate::identity::OperationId,
        report: &AcknowledgementReport,
    ) -> Self {
        self.record_ack(operation, report);
        self
    }

    /// Records one acknowledgement fact from its contributing Members
    /// directly, for acknowledgement observations that were not lattice
    /// reports (a local publication).
    #[must_use]
    pub fn with_acknowledgement_members(
        mut self,
        operation: &crate::identity::OperationId,
        level: AcknowledgementLevel,
        contributing: &[MemberId],
    ) -> Self {
        self.acknowledgements.push((operation.as_str().to_owned(), level, contributing.to_vec()));
        self
    }

    /// Names the fault classes the scenario exercised.
    #[must_use]
    pub fn with_faults(mut self, faults: &[FaultClass]) -> Self {
        self.faults = faults.to_vec();
        self
    }

    /// Records the convergence observation and the condition it checked.
    #[must_use]
    pub fn with_convergence(mut self, convergence: Convergence, condition: &str) -> Self {
        self.convergence = Some(convergence);
        self.convergence_condition.clear();
        self.convergence_condition.push_str(condition);
        self
    }

    fn record_ack(
        &mut self,
        operation: &crate::identity::OperationId,
        report: &AcknowledgementReport,
    ) {
        let contributing =
            report.contributed.iter().flat_map(|(_, members)| members.iter().cloned()).collect();
        self.acknowledgements.push((operation.as_str().to_owned(), report.observed, contributing));
    }

    /// Records the measured RPO.
    #[must_use]
    pub fn with_rpo(mut self, count: usize, window: Option<(u64, u64)>) -> Self {
        self.rpo = Some((count, window));
        self
    }

    /// Records the measured RTO in lane ticks.
    #[must_use]
    pub fn with_rto_ticks(mut self, ticks: u64) -> Self {
        self.rto_ticks = Some(ticks);
        self
    }

    /// Records the measured convergence interval in lane ticks.
    #[must_use]
    pub fn with_convergence_ticks(mut self, ticks: u64) -> Self {
        self.convergence_ticks = Some(ticks);
        self
    }

    /// Records the observed replication lag per Member.
    #[must_use]
    pub fn with_lag(mut self, distances: Vec<(MemberId, u64)>) -> Self {
        self.lag_distances = distances;
        self
    }

    /// Records the bytes-on-wire accounting.
    #[must_use]
    pub fn with_bytes_on_wire(mut self, accounting: BytesOnWire) -> Self {
        self.bytes_on_wire = accounting;
        self
    }

    /// Records one explicit limit.
    #[must_use]
    pub fn with_limit(mut self, limit: &str) -> Self {
        self.limits.push(limit.to_owned());
        self
    }

    /// Renders the receipt as a secret-safe artifact summary: identities,
    /// counts, sequence windows, tick counts, byte counts, definitions, and
    /// limits only.
    #[allow(clippy::too_many_lines, reason = "one artifact rendering, one line per fact group")]
    #[must_use]
    pub fn summary(&self) -> String {
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
        writeln!(out, "lane: {}", self.lane.name()).expect("writing to a String cannot fail");
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
                convergence.operation,
                convergence
                    .holdings
                    .iter()
                    .map(|(member, _)| member.to_string())
                    .collect::<Vec<_>>()
                    .join(", "),
                convergence.observed_at.ticks()
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
                writeln!(out, "rpo: {count} lost, journal-sequence window {low}..={high}")
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
        for (class, bytes) in &self.bytes_on_wire.per_class {
            writeln!(
                out,
                "bytes-on-wire {class}: {} frames, {} payload bytes, {} total bytes, {} overhead bytes",
                bytes.frames, bytes.payload_bytes, bytes.total_bytes, bytes.overhead_bytes
            )
            .expect("writing to a String cannot fail");
        }
        writeln!(out, "bytes-on-wire changes delivered: {}", self.bytes_on_wire.changes_delivered)
            .expect("writing to a String cannot fail");
        writeln!(
            out,
            "bytes-on-wire overhead within budget: {}",
            if self.bytes_on_wire.overhead_within_budget { "yes" } else { "no" }
        )
        .expect("writing to a String cannot fail");
        writeln!(
            out,
            "bytes-on-wire per-change within budget: {}",
            if self.bytes_on_wire.per_change_within_budget { "yes" } else { "no" }
        )
        .expect("writing to a String cannot fail");
        writeln!(out, "bytes-on-wire definition: {BYTES_ON_WIRE_DEFINITION}")
            .expect("writing to a String cannot fail");
        for limit in &self.limits {
            writeln!(out, "limit: {limit}").expect("writing to a String cannot fail");
        }
        writeln!(out, "claim: none beyond the lane; lane-scoped facts only")
            .expect("writing to a String cannot fail");
        out
    }
}
