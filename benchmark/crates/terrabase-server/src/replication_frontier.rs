//! Durable retention progress derived from authenticated replication peers.
//!
//! The journal consumer must advance only through the minimum acknowledged
//! sequence of every peer currently required by an admitted assignment.  A
//! single fast peer cannot release history still needed by a missing peer.

use std::collections::{BTreeMap, BTreeSet};
use std::sync::Mutex;

use terrabase::Engine;
use terrabase_cluster::production::DatabaseRouteScope;

/// Durable minimum acknowledged frontier for one set of admitted replication
/// lanes.  The caller supplies authenticated acknowledgements from the live
/// peer worker and the exact Engine owning the scope.
#[derive(Default)]
pub struct ReplicationRetentionFrontier {
    state: Mutex<BTreeMap<DatabaseRouteScope, FrontierState>>,
}

#[derive(Default)]
struct FrontierState {
    required: BTreeSet<String>,
    acknowledged: BTreeMap<String, u64>,
    advanced: u64,
}

impl ReplicationRetentionFrontier {
    /// Replaces the immutable required-peer set for an admitted assignment.
    /// New peers start at zero; they cannot be skipped by a fast survivor.
    pub fn admit(
        &self,
        scope: DatabaseRouteScope,
        required: BTreeSet<String>,
    ) -> Result<(), &'static str> {
        if required.is_empty()
            || required.iter().any(|peer| {
                peer.is_empty()
                    || peer.len() > 128
                    || peer.bytes().any(|byte| byte.is_ascii_control())
            })
        {
            return Err("invalid required replication peers");
        }
        let mut states = self.state.lock().map_err(|_| "frontier unavailable")?;
        let state = states.entry(scope).or_default();
        if !state.required.is_empty() && state.required != required {
            return Err("replication peer assignment changed");
        }
        state.required = required;
        Ok(())
    }

    /// Records one authenticated durable acknowledgement and advances the
    /// Engine's single replication retention consumer to the required-peer
    /// minimum. Unknown or missing peers are refused.
    pub fn acknowledge(
        &self,
        engine: &Engine,
        scope: DatabaseRouteScope,
        peer: &str,
        sequence: u64,
    ) -> Result<Option<u64>, &'static str> {
        let mut states = self.state.lock().map_err(|_| "frontier unavailable")?;
        let state = states.get_mut(&scope).ok_or("replication assignment missing")?;
        if !state.required.contains(peer) {
            return Err("acknowledgement from unrequired peer");
        }
        let value = sequence;
        let previous = state.acknowledged.entry(peer.to_owned()).or_default();
        *previous = (*previous).max(value);
        let Some(minimum) = state
            .required
            .iter()
            .map(|peer| state.acknowledged.get(peer).copied().unwrap_or(0))
            .min()
        else {
            return Err("replication assignment missing");
        };
        if minimum <= state.advanced {
            return Ok(None);
        }
        engine
            .advance_retention_frontier("replication", minimum)
            .map_err(|_| "durable retention frontier failed")?;
        state.advanced = minimum;
        Ok(Some(minimum))
    }

    /// Returns the last minimum durably submitted for a scope.
    pub fn advanced(&self, scope: DatabaseRouteScope) -> Option<u64> {
        self.state.lock().ok()?.get(&scope).map(|state| state.advanced)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn scope() -> DatabaseRouteScope {
        DatabaseRouteScope::try_new(
            "01ARZ3NDEKTSV4RRFFQ69G5FAV",
            "01ARZ3NDEKTSV4RRFFQ69G5FAW",
            "01ARZ3NDEKTSV4RRFFQ69G5FAX",
        )
        .unwrap()
    }

    #[test]
    fn missing_peer_keeps_minimum_at_zero_and_fast_peer_cannot_jump_it() {
        let frontier = ReplicationRetentionFrontier::default();
        frontier
            .admit(scope(), BTreeSet::from(["writer-sfo".into(), "writer-fra".into()]))
            .unwrap();
        assert!(frontier.advanced(scope()).is_some_and(|value| value == 0));
    }

    #[test]
    fn assignment_replacement_is_refused_without_a_new_frontier_owner() {
        let frontier = ReplicationRetentionFrontier::default();
        frontier.admit(scope(), BTreeSet::from(["writer-sfo".into()])).unwrap();
        assert!(frontier.admit(scope(), BTreeSet::from(["writer-fra".into()])).is_err());
    }
}
