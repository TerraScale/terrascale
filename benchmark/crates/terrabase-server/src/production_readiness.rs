//! Durable replication evidence used by the production readiness adapter.

use std::collections::BTreeMap;
use std::time::{SystemTime, UNIX_EPOCH};

use terrabase_cluster::production::DatabaseRouteScope;

use crate::replication_peers::{PeerFacts, PeerObservations};
use crate::replication_proof::VerifiedReadinessCut;
use crate::replication_runtime::ReplicationBoundary;

/// The accepted assignment facts required to evaluate one database's lanes.
///
/// Callers must derive this value from the durable accepted assignment and the
/// local authenticated member identity. Configuration alone is not evidence.
#[derive(Clone, Debug, Eq, PartialEq)]
pub(crate) struct ExpectedScope {
    pub(crate) scope: DatabaseRouteScope,
    pub(crate) generation: u64,
    pub(crate) epoch: u64,
    pub(crate) local_region: String,
    pub(crate) local_is_writer: bool,
    pub(crate) physical_peer: String,
    pub(crate) remote_writers: BTreeMap<String, String>,
}

/// Replication-specific readiness evidence. Other production facts remain
/// deliberately outside this evaluator.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub(crate) struct ReplicationReadinessFacts {
    pub(crate) accepted: bool,
    pub(crate) physical_ready: bool,
    pub(crate) causal_ready: bool,
    pub(crate) repair_ready: bool,
    pub(crate) durable_progress_available: bool,
}

/// Evaluates exact accepted lanes against live authenticated proof observations.
/// Missing, stale, foreign, or mismatched evidence fails closed.
pub(crate) fn evaluate(
    expected: Option<&BTreeMap<DatabaseRouteScope, ExpectedScope>>,
    observations: &PeerObservations,
    boundaries: &BTreeMap<DatabaseRouteScope, ReplicationBoundary>,
) -> ReplicationReadinessFacts {
    let accepted = expected.is_some();
    let Some(expected) = expected else {
        return ReplicationReadinessFacts { accepted, ..Default::default() };
    };
    if expected.is_empty() {
        return ReplicationReadinessFacts { accepted, ..Default::default() };
    }
    let observed = match observations.lock() {
        Ok(observed) => observed,
        Err(_) => return ReplicationReadinessFacts { accepted, ..Default::default() },
    };
    let mut physical_ready = true;
    let mut causal_ready = true;
    let mut repair_ready = true;
    let mut durable_progress_available = true;
    for (scope, assignment) in expected {
        if assignment.scope != *scope {
            return ReplicationReadinessFacts { accepted, ..Default::default() };
        }
        let Some(boundary) = boundaries.get(scope) else {
            durable_progress_available = false;
            physical_ready = false;
            causal_ready = false;
            repair_ready = false;
            continue;
        };
        if !boundary.regional_observed
            || boundary.regions.keys().any(|region| {
                region != &assignment.local_region
                    && !assignment.remote_writers.contains_key(region)
            })
            || assignment.remote_writers.keys().any(|region| !boundary.regions.contains_key(region))
        {
            durable_progress_available = false;
        }
        let Some(physical) = observed.get(&(assignment.physical_peer.clone(), *scope)) else {
            physical_ready = false;
            causal_ready = false;
            repair_ready = false;
            continue;
        };
        if !physical.connected || !physical_matches(physical, assignment) {
            physical_ready = false;
        }
        if assignment.local_is_writer && !physical.physical_caught_up {
            physical_ready = false;
        }
        for (region, peer) in &assignment.remote_writers {
            let Some(facts) = observed.get(&(peer.clone(), *scope)) else {
                causal_ready = false;
                repair_ready = false;
                continue;
            };
            if !facts.connected {
                causal_ready = false;
                repair_ready = false;
                continue;
            }
            if facts.region != *region || !facts.causal_receipt_observed {
                causal_ready = false;
            }
            if !correlated_repair(facts, peer, assignment, boundary) {
                repair_ready = false;
            }
        }
    }
    ReplicationReadinessFacts {
        accepted,
        physical_ready,
        causal_ready,
        repair_ready,
        durable_progress_available,
    }
}

fn physical_matches(facts: &PeerFacts, expected: &ExpectedScope) -> bool {
    let Some(physical) = &facts.physical else { return false };
    let Some(readiness) = &facts.readiness else { return false };
    physical.scope() == expected.scope
        && physical.generation() == expected.generation
        && physical.epoch() == expected.epoch
        && readiness.scope() == expected.scope
        && readiness.generation() == expected.generation
        && readiness.epoch() == expected.epoch
        && readiness.is_local_source() == expected.local_is_writer
        && readiness.cut_sequence() <= physical.sequence()
        && fresh(readiness)
}

fn correlated_repair(
    facts: &PeerFacts,
    peer: &str,
    expected: &ExpectedScope,
    boundary: &ReplicationBoundary,
) -> bool {
    let (Some(repair), Some(readiness)) = (&facts.repair, &facts.readiness) else { return false };
    repair.scope() == expected.scope
        && repair.placement_generation() == expected.generation
        && repair.replica_epoch() == expected.epoch
        && repair.peer() == peer
        && readiness.scope() == expected.scope
        && readiness.generation() == expected.generation
        && readiness.epoch() == expected.epoch
        && repair.local_sequence() <= readiness.cut_sequence()
        && readiness.cut_sequence() <= boundary.regional_sequence
        && hex::encode(repair.cut_digest()) == readiness.cut_digest()
        && hex::encode(repair.root_digest()) == readiness.root_digest()
        && repair.frontier() == readiness.frontier()
        && fresh(readiness)
}

fn fresh(readiness: &VerifiedReadinessCut) -> bool {
    let Ok(now) = SystemTime::now().duration_since(UNIX_EPOCH) else { return false };
    let now = u64::try_from(now.as_millis()).unwrap_or(u64::MAX);
    readiness.observed_at_unix_ms() <= now.saturating_add(5_000)
        && now.saturating_sub(readiness.observed_at_unix_ms()) <= 60_000
}

#[cfg(test)]
mod tests {
    use super::*;

    fn scope() -> DatabaseRouteScope {
        let id = terrabase::identity::DatabaseUlid::generate();
        DatabaseRouteScope::new(
            terrabase::identity::TenantUlid::generate(),
            terrabase::identity::ProjectUlid::generate(),
            id,
        )
    }

    #[test]
    fn empty_inventory_is_not_vacuously_ready() {
        let observations = PeerObservations::default();
        let facts = evaluate(Some(&BTreeMap::new()), &observations, &BTreeMap::new());
        assert_eq!(facts, ReplicationReadinessFacts { accepted: true, ..Default::default() });
    }

    #[test]
    fn missing_observation_fails_closed() {
        let scope = scope();
        let expected = ExpectedScope {
            scope,
            generation: 1,
            epoch: 1,
            local_region: "GRU".into(),
            local_is_writer: true,
            physical_peer: "follower".into(),
            remote_writers: BTreeMap::from([(String::from("SFO"), String::from("writer-sfo"))]),
        };
        let observations = PeerObservations::default();
        let boundaries = BTreeMap::from([(
            scope,
            ReplicationBoundary {
                regional_sequence: 1,
                regional_observed: true,
                regions: BTreeMap::from([
                    (String::from("GRU"), BTreeMap::new()),
                    (String::from("SFO"), BTreeMap::new()),
                ]),
            },
        )]);
        let facts =
            evaluate(Some(&BTreeMap::from([(scope, expected)])), &observations, &boundaries);
        assert!(!facts.physical_ready);
        assert!(!facts.causal_ready);
        assert!(!facts.repair_ready);
    }
}
