//! One frozen readiness cut shared by all database lanes in a member process.
//!
//! The first selected Region supplies the comparison cut. This only selects
//! which measured publication prefix to compare; it confers no writer authority.

use crate::replication_peers::PeerObservations;
use crate::replication_runtime::ReplicationProgress;
use std::collections::BTreeMap;
use std::sync::Mutex;
use std::time::Instant;
use terrabase_cluster::production::{DatabaseRouteScope, ProductionMemberRuntime};
use terrabase_member_transport::identity::MemberId;
use terrabase_tbep::production_repair::{LocalRepairCut, PublicationIndex, RepairDriver};
use terrabase_tbep::registry::BoundScope;

#[derive(Clone)]
struct SelectedCut {
    cut: LocalRepairCut,
    source_session: Option<(String, Instant)>,
}

/// Bounded, shared cut selection for every admitted database's peer workers.
/// Opaque descriptors are measured locally or received through verified repair.
#[derive(Default)]
pub struct ReplicationCuts {
    selected: Mutex<BTreeMap<DatabaseRouteScope, SelectedCut>>,
}

impl ReplicationCuts {
    /// Selects the immutable cut used for physical challenges and every repair lane.
    /// Call from a bounded blocking worker because the canonical source captures
    /// durable engine summary metadata. The peer identity must come from the
    /// current admitted connection. A non-source waits for actual equality with
    /// the source Region and discards the old selection after its reconnection.
    ///
    /// # Errors
    /// Refuses fenced scope, missing engine state, poisoned evidence, excess
    /// database cardinality, or a cut from another accepted placement.
    pub fn select(
        &self,
        runtime: &ProductionMemberRuntime,
        progress: &ReplicationProgress,
        observations: &PeerObservations,
        scope: DatabaseRouteScope,
        peer: &MemberId,
        generation: u64,
        epoch: u64,
    ) -> Result<Option<LocalRepairCut>, String> {
        let regions = progress.selected_regions(scope).map_err(failure)?;
        let source = regions.first().ok_or("empty readiness Region selection")?;
        let endpoint = runtime.transport();
        if endpoint.config().peer(peer).is_none() || generation == 0 || epoch == 0 {
            return Err("unadmitted readiness cut connection".into());
        }
        let local_region = endpoint.config().local_region().as_str();
        if !regions.contains(local_region) {
            return Err("local readiness Region is not selected".into());
        }
        let mut selected = self.selected.lock().map_err(failure)?;
        if selected.get(&scope).is_some_and(|old| {
            old.cut.placement_generation() != generation || old.cut.replica_epoch() != epoch
        }) {
            selected.remove(&scope);
        }
        if local_region == source {
            if let Some(old) = selected.get(&scope) {
                return Ok(Some(old.cut.clone()));
            }
            if selected.len() >= terrabase::member::MAX_DATABASES {
                return Err("readiness cut database bound exceeded".into());
            }
            let engine = runtime.acquire_replication(&scope).map_err(failure)?;
            let bound = BoundScope {
                principal: endpoint.config().local_identity().as_str().to_owned(),
                tenant: scope.tenant().to_text(),
                project: scope.project().to_text(),
                database: scope.database().to_text(),
            };
            let driver = RepairDriver::new(
                endpoint.config().local_identity().member().clone(),
                peer.clone(),
                bound.clone(),
                generation,
                epoch,
            )
            .map_err(failure)?;
            let mut index = PublicationIndex::default();
            index.refresh(&engine, &bound).map_err(failure)?;
            let cut = driver.local_cut(&engine, &index).map_err(failure)?;
            selected.insert(scope, SelectedCut { cut: cut.clone(), source_session: None });
            return Ok(Some(cut));
        }
        let observed = observations.lock().map_err(failure)?;
        // There is one causal writer peer per selected Region. Multiple source
        // candidates are refused rather than selecting by map insertion order.
        let mut candidates = observed.iter().filter(|((_, database), facts)| {
            *database == scope && facts.connected && &facts.region == source
        });
        let Some(((source_peer, _), facts)) = candidates.next() else {
            selected.remove(&scope);
            return Ok(None);
        };
        if candidates.next().is_some() {
            return Err("ambiguous readiness cut source Region".into());
        }
        let Some(started) = facts.session_started else {
            selected.remove(&scope);
            return Ok(None);
        };
        let session = (source_peer.clone(), started);
        if let Some(old) = selected.get(&scope)
            && old.source_session.as_ref() == Some(&session)
        {
            return Ok(Some(old.cut.clone()));
        }
        selected.remove(&scope);
        let Some(verified) = &facts.repair else { return Ok(None) };
        if verified.scope() != scope
            || verified.placement_generation() != generation
            || verified.replica_epoch() != epoch
            || verified.peer() != source_peer
            || verified.frontier().keys().any(|origin| !regions.contains(origin))
        {
            return Err("readiness source cut binding mismatch".into());
        }
        if selected.len() >= terrabase::member::MAX_DATABASES {
            return Err("readiness cut database bound exceeded".into());
        }
        let cut = verified.local_cut().clone();
        selected.insert(scope, SelectedCut { cut: cut.clone(), source_session: Some(session) });
        Ok(Some(cut))
    }

    /// Removes a retired database after its assignment has been fenced.
    /// # Errors
    /// Refuses poisoned state instead of retaining an untracked descriptor.
    pub fn forget(&self, scope: DatabaseRouteScope) -> Result<(), String> {
        self.selected.lock().map_err(failure)?.remove(&scope);
        Ok(())
    }
}

fn failure(error: impl std::fmt::Debug) -> String {
    format!("replication cut: {error:?}")
}
