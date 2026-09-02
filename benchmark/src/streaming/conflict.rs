//! Content-free conflict observations surfaced on the change stream.
//!
//! A conflict record carries identities, clocks, and the recorded policy
//! outcome — deterministic resolution with losers retained as observable
//! history. No conflicting document payload crosses this type. The tenant
//! names the key's owner, so a tenant-scoped observation acquires only its
//! own conflicts.

use crate::types::{MemberId, OperationId, ReplicationClockStamp, TenantId};

/// A content-free conflict observation.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ConflictRecord {
    /// The tenant whose key conflicted.
    tenant: TenantId,
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
        tenant: TenantId,
        operations: Vec<OperationId>,
        members: Vec<MemberId>,
        clocks: Vec<ReplicationClockStamp>,
        outcome: ConflictOutcome,
    ) -> Self {
        Self { tenant, operations, members, clocks, outcome }
    }

    /// Returns the tenant whose key conflicted.
    #[must_use]
    pub fn tenant(&self) -> &TenantId {
        &self.tenant
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

/// The recorded policy outcome of one conflict.
///
/// Every variant names the Member the recorded pair converged to, so a
/// conflict record always states the disposition, never just the detection.
#[derive(Clone, Debug, Eq, Hash, PartialEq)]
pub enum ConflictOutcome {
    /// Last-writer-wins on the bounded hybrid logical clock kept the write
    /// of this Member; the other write is retained as history.
    Winner(MemberId),
    /// The write came from a deposed owner and is retained as observable
    /// history; this Member names the deposed owner whose local receipt
    /// stays real locally while the write converges.
    DeposedOwnerRetained(MemberId),
    /// The peer's clock exceeded the skew bound, so its timestamps were
    /// treated as untrusted and the pair converged by the deterministic
    /// Member-identity tie-break naming this Member.
    UntrustedClock(MemberId),
}
