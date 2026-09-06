//! Retained orchestration of final data-plane frontier capture.

use super::{ProductionDataLifecycle, ProductionMemberRuntime, TransitionWriterFences};
use crate::replication_peers::{RetiredPeerLane, RetiredPhysicalLane};
use std::collections::BTreeMap;
use std::sync::Arc;
use terrabase::types::{StorageSequence, TenantId};
use terrabase_cluster::production::DatabaseRouteScope;
use terrabase_tbep::placement::transition::VerifiedPreparedTransition;

/// A writer's durable origin fence and final locally stored causal frontier.
#[derive(Clone, Debug)]
pub struct RetiredWriterFrontier {
    /// Published sequence at the irreversible old-epoch origin fence.
    pub fence_sequence: StorageSequence,
    /// Durable origin prefixes read after all replication workers joined.
    pub origins: BTreeMap<String, u64>,
}

/// Sealed data-plane evidence for one prepared placement transition.
/// Public/API and auth tasks and the shared endpoint still require host-level
/// drainage before signing a completed member fence receipt.
#[derive(Clone, Debug)]
pub struct RetiredReplicationFrontiers {
    prepared: VerifiedPreparedTransition,
    writers: BTreeMap<DatabaseRouteScope, RetiredWriterFrontier>,
    peers: BTreeMap<(String, DatabaseRouteScope), RetiredPeerLane>,
}

impl RetiredReplicationFrontiers {
    /// Exact authenticated transition whose data workers retired.
    #[must_use]
    pub fn prepared(&self) -> &VerifiedPreparedTransition {
        &self.prepared
    }
    /// Final writer frontiers from the retained Engines.
    #[must_use]
    pub fn writers(&self) -> &BTreeMap<DatabaseRouteScope, RetiredWriterFrontier> {
        &self.writers
    }
    /// Exact accepted peer lanes captured before releasing physical owners.
    #[must_use]
    pub fn peers(&self) -> &BTreeMap<(String, DatabaseRouteScope), RetiredPeerLane> {
        &self.peers
    }
}

type RetirementResult = Result<Arc<RetiredReplicationFrontiers>, String>;

#[derive(Default)]
pub(super) struct RetirementState {
    digest: Option<String>,
    task: Option<tokio::task::JoinHandle<RetirementResult>>,
}

impl ProductionDataLifecycle {
    /// Fences origins, captures every accepted peer lane, joins replication,
    /// and reads final writer frontiers without closing the shared Member.
    ///
    /// One retained task owns the transition across caller cancellation. Exact
    /// retries reuse completed evidence; a different prepared grant is refused.
    /// The host supervisor must still drain public/auth work and the endpoint
    /// before a signed member receipt can be produced.
    ///
    /// # Errors
    /// Refuses mismatched authority, incomplete physical/causal evidence, failed
    /// worker cleanup, unavailable durable frontiers, or the runtime deadline.
    pub async fn retire_transition_replication(
        self: &Arc<Self>,
        runtime: Arc<ProductionMemberRuntime>,
        prepared: &VerifiedPreparedTransition,
    ) -> RetirementResult {
        self.validate_retirement_owner(&runtime, prepared)?;
        let mut state = self.retirement.lock().await;
        if state.digest.as_deref().is_some_and(|digest| digest != prepared.proof_digest()) {
            return Err("lifecycle already belongs to another prepared transition".into());
        }
        if let Some(completed) = self.retired_frontiers.get() {
            return Ok(completed.clone());
        }
        if state.task.is_none() {
            // A timed-out task may still own its final blocking capture. Wait
            // through it before deciding whether another attempt is needed.
            let startup = self.startup.clone().lock_owned().await;
            if let Some(completed) = self.retired_frontiers.get() {
                return Ok(completed.clone());
            }
            drop(startup);
            state.digest = Some(prepared.proof_digest().to_owned());
            let lifecycle = self.clone();
            let prepared = prepared.clone();
            let timeout = runtime.transport().config().limits().drain_timeout();
            state.task = Some(tokio::spawn(async move {
                tokio::time::timeout(timeout, lifecycle.capture_transition(runtime, prepared))
                    .await
                    .map_err(|_| "replication retirement deadline exceeded".to_owned())?
            }));
        }
        let result = state
            .task
            .as_mut()
            .ok_or("retirement task unavailable")?
            .await
            .map_err(|_| "replication retirement task failed".to_owned())
            .and_then(|result| result);
        state.task = None;
        self.retired_frontiers.get().map_or(result, |completed| Ok(completed.clone()))
    }

    async fn capture_transition(
        self: Arc<Self>,
        runtime: Arc<ProductionMemberRuntime>,
        prepared: VerifiedPreparedTransition,
    ) -> RetirementResult {
        let writers = self.begin_transition_retirement(runtime, &prepared).await?;
        let startup = self.startup.clone().lock_owned().await;
        let data = self.get().ok_or("replication owner unavailable during retirement")?;
        let expected = self
            .expected
            .lock()
            .map_err(|_| "retirement inventory unavailable")?
            .clone()
            .ok_or("retirement inventory missing")?;
        let peers = data.peers.capture_retirement().await?;
        validate_lanes(&expected, &peers, &prepared)?;
        data.drain().await?;
        let lifecycle = self.clone();
        tokio::task::spawn_blocking(move || {
            let _startup = startup;
            let final_writers = capture_writers(&writers, prepared.prior_epoch())?;
            drop(writers);
            lifecycle.registry.release_drained_hosts()?;
            let completed =
                Arc::new(RetiredReplicationFrontiers { prepared, writers: final_writers, peers });
            if lifecycle.retired_frontiers.set(completed.clone()).is_err() {
                return Err("retired frontier result was installed twice".into());
            }
            Ok(completed)
        })
        .await
        .map_err(|_| "final writer frontier worker failed".to_owned())?
    }
}

fn capture_writers(
    fences: &TransitionWriterFences,
    epoch: u64,
) -> Result<BTreeMap<DatabaseRouteScope, RetiredWriterFrontier>, String> {
    let mut writers = BTreeMap::new();
    for snapshot in fences.hosts() {
        let record = &snapshot.database.record;
        let scope =
            DatabaseRouteScope::try_new(&record.tenant_id, &record.project_id, &record.database_id)
                .map_err(|_| "retired writer owner unavailable")?;
        let engine = snapshot.database.host.engine();
        if engine.origin_epoch().map_err(|_| "retired writer fence unavailable")? != (epoch, true) {
            return Err("retired writer origin is not fenced".into());
        }
        let tenant = TenantId::try_new(&record.tenant_id).map_err(|_| "retired tenant invalid")?;
        let origins = engine
            .durable_origin_prefixes(&engine.scope_for(&tenant))
            .map_err(|_| "retired writer durable frontier unavailable")?;
        let fence_sequence =
            *fences.sequences().get(&scope).ok_or("retired writer fence missing")?;
        if writers.insert(scope, RetiredWriterFrontier { fence_sequence, origins }).is_some() {
            return Err("duplicate retired writer".into());
        }
    }
    if writers.len() != fences.sequences().len() {
        return Err("incomplete retired writer inventory".into());
    }
    Ok(writers)
}

fn validate_lanes(
    expected: &BTreeMap<DatabaseRouteScope, crate::production_readiness::ExpectedScope>,
    peers: &BTreeMap<(String, DatabaseRouteScope), RetiredPeerLane>,
    prepared: &VerifiedPreparedTransition,
) -> Result<(), String> {
    let mut required = BTreeMap::new();
    for (scope, assignment) in expected {
        if *scope != assignment.scope
            || assignment.epoch != prepared.prior_epoch()
            || assignment.generation != prepared.prior_generation()
        {
            return Err("retirement inventory changed authority".into());
        }
        required
            .insert((assignment.physical_peer.clone(), *scope), (true, assignment.local_is_writer));
        if assignment.local_is_writer {
            for peer in assignment.remote_writers.values() {
                if required.insert((peer.clone(), *scope), (false, true)).is_some() {
                    return Err("retirement peer roles overlap".into());
                }
            }
        }
    }
    if peers.len() != required.len() {
        return Err("incomplete retired peer inventory".into());
    }
    for (key, (physical, writer)) in required {
        let lane = peers.get(&key).ok_or("retired peer lane missing")?;
        match (&lane.physical, physical, writer) {
            (Some(RetiredPhysicalLane::Writer), true, true) if lane.causal_frontier.is_none() => {}
            (Some(RetiredPhysicalLane::Follower(proof)), true, false)
                if lane.causal_frontier.is_none()
                    && proof.scope() == key.1
                    && proof.generation() == prepared.prior_generation()
                    && proof.epoch() == prepared.prior_epoch() => {}
            (None, false, true) if lane.causal_frontier.is_some() => {}
            _ => return Err("retired peer evidence does not match accepted role".into()),
        }
    }
    Ok(())
}
