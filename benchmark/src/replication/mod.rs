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

use std::{fmt, marker::PhantomData};

use crate::types::OperationId;

/// A validated logical Region identifier (CONTEXT: Region).
///
/// `Display` renders the canonical identifier. Validation belongs to
/// W4-REPLICATION.
#[derive(Clone, Debug, Eq, Hash, PartialEq)]
pub struct RegionId {
    identifier: PhantomData<String>,
}

impl fmt::Display for RegionId {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        let _ = (self, formatter);
        todo!("w4 implementation packet")
    }
}

/// A validated logical Member identifier (CONTEXT: Member).
///
/// `Display` renders the canonical identifier. Validation belongs to
/// W4-REPLICATION.
#[derive(Clone, Debug, Eq, Hash, PartialEq)]
pub struct MemberId {
    identifier: PhantomData<String>,
}

impl fmt::Display for MemberId {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        let _ = (self, formatter);
        todo!("w4 implementation packet")
    }
}

/// A caller-supplied membership view: Regions, Members, Failure Domain
/// grouping, and per-range ownership under the adjudicated algorithm.
///
/// `TopologyView` is an immutable snapshot of a membership view and an input,
/// not a decision: provisioning, placement, routing, lifecycle, membership
/// policy, and control-plane replication of topology itself stay with the
/// Platform/Orchestrator control plane (ADR-0004 D-1: one Member owns each
/// data range's write path).
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct TopologyView {
    view: PhantomData<()>,
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

/// The observed acknowledgement for one replicated operation.
///
/// Carries the observed level, the Region and Member set that contributed,
/// and the observation time; it names Members explicitly (PB-5026, PB-5042).
/// Any normalization of a degenerate level request is recorded here, so
/// reports stay comparable across topologies.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct AcknowledgementReport {
    report: PhantomData<()>,
}

/// The contract-defined converged-state observation for one operation
/// identity (PB-5043).
///
/// Names which Members hold the operation, at which acknowledgement state,
/// and when it was observed. `Convergence` is reported as its own observation
/// and is never inferred from an acknowledgement.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct Convergence {
    observation: PhantomData<()>,
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
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ReplicationReceipt {
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
    record: PhantomData<()>,
}

/// Durable per-Member replication progress bound to storage sequence
/// (PB-5033).
///
/// A lagging or restarted Member resumes from its cursor, validates storage
/// identity and format before rejoining, and catches up without duplicating
/// committed mutations.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ReplicationCursor {
    cursor: PhantomData<()>,
}

/// A validated topology and policy request input.
///
/// `ReplicationConfig` does not authorize, provision, or route; the control
/// plane owns those concerns. It carries no credential or connection string.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ReplicationConfig {
    config: PhantomData<()>,
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
