//! Replication health observations, separated from convergence facts.
//!
//! [`ReplicationStatus`] is a health vocabulary only (PB-5032): it reports
//! what a member is doing and whether it is reachable, current, and honest
//! about its own degradation. It never carries or implies a
//! [`Convergence`](super::Convergence) fact, and no health value can be
//! turned into one — R-1 adds no convergence content and no such path.
//!
//! TODO(R-2): the convergence observation — which members hold an operation
//! identity, at which acknowledgement state, observed when — belongs to the
//! R-2 phase of W4-REPLICATION and must be reported as its own type through
//! its own seam, never derived here.

use super::ReplicationStatus;
use super::member::ReplicationMember;

/// Derives one member's health observation from its own facts.
///
/// The precedence is deliberate and each step is a health fact, not a
/// convergence one:
///
/// 1. an absent engine means the member cannot be reached
///    ([`ReplicationStatus::Unavailable`]);
/// 2. a recorded rejoin rejection or tenant-isolation denial means the member
///    is serving in a degraded mode ([`ReplicationStatus::Degraded`]);
/// 3. a tenant the member knows it is behind means it answered outside its
///    bound ([`ReplicationStatus::Stale`]);
/// 4. a member that answered a catch-up request is
///    [`ReplicationStatus::Responsive`];
/// 5. a member that has served or applied anything is
///    [`ReplicationStatus::Active`];
/// 6. a seated member that has done nothing yet is
///    [`ReplicationStatus::Assigned`].
#[allow(
    dead_code,
    reason = "driven and observed by the W4-REPLICATION R-1 lane tests; the production acquisition seam lands with R-2/R-3"
)]
pub(crate) fn member_health(member: &ReplicationMember) -> ReplicationStatus {
    if member.engine().is_none() {
        return ReplicationStatus::Unavailable;
    }
    if member.last_rejection().is_some() || !member.denials().is_empty() {
        return ReplicationStatus::Degraded;
    }
    if member.is_lagging() {
        return ReplicationStatus::Stale;
    }
    if member.answered() {
        return ReplicationStatus::Responsive;
    }
    if member.activated() {
        return ReplicationStatus::Active;
    }
    ReplicationStatus::Assigned
}
