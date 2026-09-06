use super::{PeerDatabase, PhysicalDriver};
use std::collections::{BTreeMap, BTreeSet};
use terrabase::identity::DatabaseUlid;
use terrabase_cluster::production::DatabaseRouteScope;
use terrabase_cluster::wal_driver::WalRetiredPhysicalBoundary;

/// The physical role retained at an epoch retirement barrier.
#[derive(Clone, Debug)]
pub enum RetiredPhysicalLane {
    /// The local engine was the admitted writer; its final frontier is owned
    /// by the external writer capture path.
    Writer,
    /// The local raw follower supplied immutable physical evidence.
    Follower(WalRetiredPhysicalBoundary),
}

/// Durable evidence captured for one peer/database lane.
#[derive(Clone, Debug)]
pub struct RetiredPeerLane {
    /// Physical evidence, when this lane had a physical assignment.
    pub physical: Option<RetiredPhysicalLane>,
    /// Causal peer frontier, when this lane had a causal journal.
    pub causal_frontier: Option<BTreeMap<String, u64>>,
}

/// Complete retirement evidence keyed by exact authenticated peer and scope.
pub type RetirementEvidence = BTreeMap<(String, DatabaseRouteScope), RetiredPeerLane>;

pub(super) async fn capture_lane(lane: &PeerDatabase) -> Result<RetiredPeerLane, String> {
    let physical = match &lane.physical {
        Some(PhysicalDriver::Writer(_)) | Some(PhysicalDriver::BootstrapWriter { .. }) => {
            Some(RetiredPhysicalLane::Writer)
        }
        Some(PhysicalDriver::Follower(follower))
        | Some(PhysicalDriver::InstalledFollower { driver: follower, .. }) => Some(
            RetiredPhysicalLane::Follower(
                follower.retired_physical_boundary().map_err(|error| format!("{error:?}"))?,
            ),
        ),
        Some(PhysicalDriver::BootstrapFollower(_)) | None => None,
    };
    let causal_frontier = lane
        .causal
        .as_ref()
        .map(|causal| causal.lock().map(|driver| driver.durable_peer_frontier().clone()))
        .transpose()
        .map_err(|_| "causal frontier lock poisoned".to_owned())?;
    if physical.is_none() && causal_frontier.is_none() {
        return Err("retirement lane has no physical or causal evidence".into());
    }
    Ok(RetiredPeerLane { physical, causal_frontier })
}

pub(super) fn expected_keys(
    peer: &str,
    lanes: &[PeerDatabase],
) -> BTreeSet<(String, DatabaseRouteScope)> {
    lanes.iter().map(|lane| (peer.to_owned(), lane.scope)).collect()
}
