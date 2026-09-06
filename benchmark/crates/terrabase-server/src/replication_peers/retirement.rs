use super::{PeerDatabase, PhysicalDriver};
use std::collections::BTreeMap;
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
        | Some(PhysicalDriver::InstalledFollower { driver: follower, .. }) => {
            Some(RetiredPhysicalLane::Follower(
                follower.retired_physical_boundary().await.map_err(|error| format!("{error:?}"))?,
            ))
        }
        Some(PhysicalDriver::BootstrapFollower(_)) => {
            return Err("bootstrap follower has no installed physical boundary".into());
        }
        None => None,
    };
    let causal = lane.causal.clone();
    let causal_frontier = tokio::task::spawn_blocking(move || {
        causal
            .map(|causal| causal.lock().map(|driver| driver.durable_peer_frontier().clone()))
            .transpose()
            .map_err(|_| "causal frontier lock poisoned".to_owned())
    })
    .await
    .map_err(|error| format!("causal capture task: {error:?}"))??;
    if physical.is_none() && causal_frontier.is_none() {
        return Err("retirement lane has no physical or causal evidence".into());
    }
    Ok(RetiredPeerLane { physical, causal_frontier })
}

pub(super) async fn capture_lanes(
    lanes: &[PeerDatabase],
) -> Result<Vec<(DatabaseRouteScope, RetiredPeerLane)>, String> {
    let mut captured = Vec::with_capacity(lanes.len());
    for lane in lanes {
        captured.push((lane.scope, capture_lane(lane).await?));
    }
    Ok(captured)
}

#[cfg(test)]
mod tests {
    use super::*;
    use terrabase::identity::{DatabaseUlid, ProjectUlid, TenantUlid};

    fn scope() -> DatabaseRouteScope {
        DatabaseRouteScope::new(
            TenantUlid::from_text("01ARZ3NDEKTSV4RRFFQ69G5FAV").expect("tenant"),
            ProjectUlid::from_text("01ARZ3NDEKTSV4RRFFQ69G5FAW").expect("project"),
            DatabaseUlid::from_text("01ARZ3NDEKTSV4RRFFQ69G5FAX").expect("database"),
        )
    }

    #[tokio::test]
    async fn empty_lane_without_physical_or_causal_evidence_is_refused() {
        let lane = PeerDatabase { scope: scope(), physical: None, causal: None };
        assert!(capture_lanes(&[lane]).await.is_err());
    }
}
