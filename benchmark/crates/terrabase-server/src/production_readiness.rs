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
pub struct ReplicationReadinessFacts {
    /// Whether a durable accepted assignment inventory was supplied.
    pub accepted: bool,
    /// Whether the authenticated physical lane is ready for every local scope.
    pub physical_ready: bool,
    /// Whether required causal receipts were observed for local writers.
    pub causal_ready: bool,
    /// Whether required cross-region repair cuts match the source readiness cut.
    pub repair_ready: bool,
    /// Whether durable regional progress covers every selected local scope.
    pub durable_progress_available: bool,
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
        // An accepted proposal with no local database lanes has no replication
        // evidence to collect.  Other serving gates still decide whether the
        // process may become ready.
        return ReplicationReadinessFacts {
            accepted,
            physical_ready: true,
            causal_ready: true,
            repair_ready: true,
            durable_progress_available: true,
        };
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
            if assignment.local_is_writer {
                causal_ready = false;
                repair_ready = false;
            }
            continue;
        };
        if !boundary.regional_observed || !boundary.regions.contains_key(&assignment.local_region) {
            durable_progress_available = false;
        }
        let Some(physical) = observed.get(&(assignment.physical_peer.clone(), *scope)) else {
            physical_ready = false;
            if assignment.local_is_writer {
                causal_ready = false;
                repair_ready = false;
            }
            continue;
        };
        if !physical.connected
            || !physical_matches(physical, assignment, boundary.regional_sequence)
        {
            physical_ready = false;
        }
        if assignment.local_is_writer && !physical.physical_caught_up {
            physical_ready = false;
        }
        if !assignment.local_is_writer {
            continue;
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
            let correlated = correlated_repair(
                facts,
                peer,
                region,
                assignment,
                boundary,
                physical.readiness.as_ref(),
            );
            let verified_empty = correlated
                && physical
                    .readiness
                    .as_ref()
                    .is_some_and(|proof| proof.frontier().values().all(|sequence| *sequence == 0));
            if facts.region != *region || (!facts.causal_receipt_observed && !verified_empty) {
                causal_ready = false;
            }
            if !boundary.regions.contains_key(region) && !verified_empty {
                durable_progress_available = false;
            }
            if !correlated {
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

fn physical_matches(facts: &PeerFacts, expected: &ExpectedScope, regional_sequence: u64) -> bool {
    let Some(readiness) = &facts.readiness else { return false };
    let physical_matches = match facts.physical.as_ref() {
        Some(physical) => {
            physical.scope() == expected.scope
                && physical.generation() == expected.generation
                && physical.epoch() == expected.epoch
                && readiness.cut_sequence() <= physical.sequence()
        }
        None => !expected.local_is_writer,
    };
    physical_matches
        && facts.region == expected.local_region
        && readiness.scope() == expected.scope
        && readiness.generation() == expected.generation
        && readiness.epoch() == expected.epoch
        && readiness.is_local_source() == expected.local_is_writer
        && readiness.cut_sequence() <= regional_sequence
        && readiness.frontier().keys().all(|origin| {
            origin == &expected.local_region || expected.remote_writers.contains_key(origin)
        })
        && fresh(readiness)
}

fn correlated_repair(
    facts: &PeerFacts,
    peer: &str,
    region: &str,
    expected: &ExpectedScope,
    boundary: &ReplicationBoundary,
    source_readiness: Option<&VerifiedReadinessCut>,
) -> bool {
    let (Some(repair), Some(readiness)) = (&facts.repair, source_readiness) else {
        return false;
    };
    repair.scope() == expected.scope
        && repair.placement_generation() == expected.generation
        && repair.replica_epoch() == expected.epoch
        && repair.peer() == peer
        && facts.region == region
        && facts
            .repair_observed_at
            .is_some_and(|observed| observed.elapsed() <= std::time::Duration::from_secs(60))
        && readiness.scope() == expected.scope
        && readiness.generation() == expected.generation
        && readiness.epoch() == expected.epoch
        && repair.local_sequence() <= boundary.regional_sequence
        && readiness.cut_sequence() <= boundary.regional_sequence
        && fresh(readiness)
        && readiness.frontier().keys().all(|origin| {
            origin == &expected.local_region || expected.remote_writers.contains_key(origin)
        })
        && hex::encode(repair.cut_digest()) == readiness.cut_digest()
        && hex::encode(repair.root_digest()) == readiness.root_digest()
        && repair.frontier() == readiness.frontier()
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
    fn empty_inventory_has_no_replication_lanes() {
        let observations = PeerObservations::default();
        let facts = evaluate(Some(&BTreeMap::new()), &observations, &BTreeMap::new());
        assert_eq!(
            facts,
            ReplicationReadinessFacts {
                accepted: true,
                physical_ready: true,
                causal_ready: true,
                repair_ready: true,
                durable_progress_available: true,
            }
        );
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

    fn boundary(scope: DatabaseRouteScope) -> ReplicationBoundary {
        ReplicationBoundary {
            regional_sequence: 10,
            regional_observed: true,
            regions: BTreeMap::from([
                ("GRU".into(), BTreeMap::new()),
                ("SFO".into(), BTreeMap::new()),
            ]),
        }
    }

    fn observed(scope: DatabaseRouteScope, local_source: bool) -> PeerFacts {
        PeerFacts {
            connected: true,
            region: "GRU".into(),
            readiness: Some(crate::replication_proof::test_verified_readiness_cut(
                scope,
                1,
                1,
                local_source,
                BTreeMap::from([("GRU".into(), 1)]),
            )),
            physical_caught_up: true,
            causal_receipt_observed: true,
            ..Default::default()
        }
    }

    #[test]
    fn follower_readiness_uses_logical_cut_without_physical_or_causal_receipts() {
        let scope = scope();
        let expected = ExpectedScope {
            scope,
            generation: 1,
            epoch: 1,
            local_region: "GRU".into(),
            local_is_writer: false,
            physical_peer: "writer".into(),
            remote_writers: BTreeMap::new(),
        };
        let observations = PeerObservations::default();
        observations.lock().unwrap().insert(("writer".into(), scope), observed(scope, false));
        let facts = evaluate(
            Some(&BTreeMap::from([(scope, expected)])),
            &observations,
            &BTreeMap::from([(scope, boundary(scope))]),
        );
        assert!(
            facts.accepted
                && facts.physical_ready
                && facts.causal_ready
                && facts.repair_ready
                && facts.durable_progress_available
        );
    }

    #[test]
    fn writer_requires_physical_catchup_and_remote_causal_repair_observations() {
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
        observations.lock().unwrap().insert(("follower".into(), scope), observed(scope, true));
        let facts = evaluate(
            Some(&BTreeMap::from([(scope, expected)])),
            &observations,
            &BTreeMap::from([(scope, boundary(scope))]),
        );
        assert!(!facts.physical_ready && !facts.causal_ready && !facts.repair_ready);
    }

    #[test]
    fn missing_boundary_peer_and_foreign_or_stale_cut_fail_closed() {
        let scope = scope();
        let expected = ExpectedScope {
            scope,
            generation: 1,
            epoch: 1,
            local_region: "GRU".into(),
            local_is_writer: false,
            physical_peer: "writer".into(),
            remote_writers: BTreeMap::new(),
        };
        let observations = PeerObservations::default();
        let mut facts = observed(scope, false);
        facts.readiness = Some(crate::replication_proof::test_verified_readiness_cut(
            scope,
            2,
            1,
            false,
            BTreeMap::new(),
        ));
        observations.lock().unwrap().insert(("writer".into(), scope), facts);
        let expected_map = BTreeMap::from([(scope, expected)]);
        let no_boundary = evaluate(Some(&expected_map), &observations, &BTreeMap::new());
        assert!(!no_boundary.physical_ready && !no_boundary.durable_progress_available);
        let stale = evaluate(
            Some(&expected_map),
            &observations,
            &BTreeMap::from([(scope, boundary(scope))]),
        );
        assert!(!stale.physical_ready);
        observations.lock().unwrap().remove(&("writer".into(), scope));
        let missing_peer = evaluate(
            Some(&expected_map),
            &observations,
            &BTreeMap::from([(scope, boundary(scope))]),
        );
        assert!(!missing_peer.physical_ready);
    }

    #[test]
    fn writer_without_matching_remote_repair_cut_is_not_repair_ready() {
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
        observations.lock().unwrap().insert(("follower".into(), scope), observed(scope, true));
        observations.lock().unwrap().insert(
            ("writer-sfo".into(), scope),
            PeerFacts {
                connected: true,
                region: "SFO".into(),
                causal_receipt_observed: true,
                ..Default::default()
            },
        );
        let facts = evaluate(
            Some(&BTreeMap::from([(scope, expected)])),
            &observations,
            &BTreeMap::from([(scope, boundary(scope))]),
        );
        assert!(!facts.repair_ready);
    }
}
