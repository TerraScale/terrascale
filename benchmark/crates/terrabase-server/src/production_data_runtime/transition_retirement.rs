//! Origin fencing for an authenticated whole-host placement transition.

use super::{ProductionDataLifecycle, ProductionMemberRuntime};
use crate::admitted_databases::AdmittedDatabaseSnapshot;
use crate::production_readiness::ExpectedScope;
use std::collections::BTreeMap;
use std::sync::Arc;
use terrabase::types::StorageSequence;
use terrabase_cluster::production::DatabaseRouteScope;
use terrabase_tbep::placement::transition::VerifiedPreparedTransition;

/// Durable origin boundaries captured while beginning retirement.
/// These are not completed drain receipts: replica apply and accepted worker
/// callbacks may still be running. Final frontiers must be read after joining
/// those workers, before the member signs an old-epoch receipt.
pub struct TransitionWriterFences {
    sequences: BTreeMap<DatabaseRouteScope, StorageSequence>,
    hosts: Vec<AdmittedDatabaseSnapshot>,
}

impl TransitionWriterFences {
    /// Published local sequence at each writer's durable origin fence.
    #[must_use]
    pub fn sequences(&self) -> &BTreeMap<DatabaseRouteScope, StorageSequence> {
        &self.sequences
    }

    /// Retained existing writer leases for final frontier inspection after drain.
    /// Retaining these leases prevents storage closure; release them before
    /// completing Member retirement. This does not open any follower Engine.
    #[must_use]
    pub fn hosts(&self) -> &[AdmittedDatabaseSnapshot] {
        &self.hosts
    }
}

impl ProductionDataLifecycle {
    /// Begins retirement of the exact runtime named by a durable prepared intent.
    ///
    /// The sealed descriptor comes from `PlacementJournal::begin_transition_drain`.
    /// Admission and physical bindings are revoked before every retained writer
    /// origin is durably fenced. The caller still owns public-task shutdown,
    /// replication worker joins, final frontier capture, and receipt signing.
    /// The result retains the existing Engine leases through those later steps.
    ///
    /// # Errors
    /// Refuses a foreign/stale runtime, missing inventory, incomplete writer
    /// coverage, or any failed durable fence. Failure never restores admission.
    pub async fn begin_transition_retirement(
        self: &Arc<Self>,
        runtime: Arc<ProductionMemberRuntime>,
        prepared: &VerifiedPreparedTransition,
    ) -> Result<TransitionWriterFences, String> {
        let config = runtime.transport().config();
        let identity = config.local_identity();
        if prepared.scope_id() != format!("{}/{}", identity.environment(), identity.cluster())
            || prepared.prior_epoch() != config.replica_set_epoch()
            || prepared.prior_generation() != config.placement_generation()
        {
            return Err("prepared transition does not name this runtime".into());
        }
        let startup = Arc::clone(&self.startup).lock_owned().await;
        let owner = self
            .get()
            .and_then(|data| data.runtime.upgrade())
            .ok_or("transition has no retained replication runtime")?;
        if !Arc::ptr_eq(&owner, &runtime) {
            return Err("transition runtime does not own this lifecycle".into());
        }
        let expected = self
            .expected
            .lock()
            .map_err(|_| "readiness inventory unavailable")?
            .clone()
            .ok_or("transition has no accepted inventory")?;
        let epoch = prepared.prior_epoch();
        let generation = prepared.prior_generation();
        if expected.iter().any(|(scope, entry)| {
            *scope != entry.scope || entry.epoch != epoch || entry.generation != generation
        }) {
            return Err("transition inventory does not match the retiring epoch".into());
        }
        let lifecycle = Arc::clone(self);
        tokio::task::spawn_blocking(move || {
            let _startup = startup;
            // WAL revocation may wait for a physical persistence critical section.
            // Keep it and the Engine barriers off Tokio's async worker threads.
            runtime.begin_epoch_retirement();
            lifecycle.begin_drain()?;
            let hosts = lifecycle.registry.retained_snapshot()?;
            fence_writer_hosts(&expected, hosts, epoch)
        })
        .await
        .map_err(|_| "transition origin fencing worker failed".to_owned())?
    }
}

fn fence_writer_hosts(
    expected: &BTreeMap<DatabaseRouteScope, ExpectedScope>,
    hosts: Vec<AdmittedDatabaseSnapshot>,
    epoch: u64,
) -> Result<TransitionWriterFences, String> {
    let mut indexed = BTreeMap::new();
    for (index, snapshot) in hosts.iter().enumerate() {
        let record = &snapshot.database.record;
        let scope =
            DatabaseRouteScope::try_new(&record.tenant_id, &record.project_id, &record.database_id)
                .map_err(|_| "transition writer scope is invalid")?;
        let assignment =
            expected.get(&scope).ok_or("transition writer is outside accepted inventory")?;
        let engine = snapshot.database.host.engine();
        let owner = engine.database_owner();
        if !assignment.local_is_writer
            || assignment.scope != scope
            || assignment.epoch != epoch
            || record.region != assignment.local_region
            || !engine.owner_scope_enforced()
            || owner.environment().as_str() != record.environment
            || owner.tenant() != scope.tenant()
            || owner.project() != scope.project()
            || owner.database() != scope.database()
            || owner.region().as_str() != assignment.local_region
            || engine.origin_epoch().map_err(|_| "transition writer epoch unavailable")?.0 != epoch
            || indexed.insert(scope, index).is_some()
        {
            return Err("transition writer does not match accepted authority".into());
        }
    }
    if expected
        .values()
        .filter(|entry| entry.local_is_writer)
        .any(|entry| !indexed.contains_key(&entry.scope))
    {
        return Err("transition is missing an accepted writer host".into());
    }
    let mut sequences = BTreeMap::new();
    for (scope, index) in indexed {
        let sequence = hosts[index]
            .database
            .host
            .engine()
            .fence_origin(epoch)
            .map_err(|_| "transition writer origin fence failed")?;
        sequences.insert(scope, sequence);
    }
    Ok(TransitionWriterFences { sequences, hosts })
}

#[cfg(test)]
mod tests {
    use super::*;
    use terrabase::engine::{CommitError, CommitOutcome};
    use terrabase::types::{LocalDurability, OperationId, TenantId};

    fn expected(scope: DatabaseRouteScope) -> BTreeMap<DatabaseRouteScope, ExpectedScope> {
        BTreeMap::from([(
            scope,
            ExpectedScope {
                scope,
                generation: 1,
                epoch: 1,
                local_region: "GRU".into(),
                local_is_writer: true,
                physical_peer: "peer".into(),
                remote_writers: BTreeMap::new(),
            },
        )])
    }

    #[test]
    fn fences_real_retained_writer_and_preadmitted_transaction() {
        let (snapshot, scope, member, root) = super::super::host_readiness::tests::writer_fixture();
        let database = Arc::clone(&snapshot.database);
        let engine = database.host.engine();
        engine.activate_origin_epoch(1).unwrap();
        let tenant = TenantId::try_new(&scope.tenant().to_text()).unwrap();
        let tenant_scope = engine.scope_for(&tenant);
        let tx = engine
            .begin(
                &tenant_scope,
                engine.read_snapshot(&tenant_scope).unwrap(),
                OperationId::try_new("before-fence").unwrap(),
            )
            .unwrap();
        let fences = fence_writer_hosts(&expected(scope), vec![snapshot], 1).unwrap();
        assert_eq!(fences.sequences().len(), 1);
        assert_eq!(engine.origin_epoch().unwrap(), (1, true));
        assert_eq!(
            tx.commit(LocalDurability::Synced),
            CommitOutcome::Rejected(CommitError::Conflict)
        );
        // A retry reaches the same Engine and preserves the irreversible fence.
        let repeated = fence_writer_hosts(
            &expected(scope),
            vec![AdmittedDatabaseSnapshot { database: Arc::clone(&database), activated: false }],
            1,
        )
        .unwrap();
        assert_eq!(repeated.sequences(), fences.sequences());
        assert!(engine.activate_origin_epoch(1).is_err());
        drop(repeated);
        drop(fences);
        drop(database);
        member.close(std::time::Duration::from_secs(2)).unwrap();
        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn missing_or_wrong_epoch_inventory_never_claims_writer_fence() {
        let (snapshot, scope, member, root) = super::super::host_readiness::tests::writer_fixture();
        snapshot.database.host.engine().activate_origin_epoch(1).unwrap();
        assert!(fence_writer_hosts(&expected(scope), Vec::new(), 1).is_err());
        let database = Arc::clone(&snapshot.database);
        assert!(fence_writer_hosts(&expected(scope), vec![snapshot], 2).is_err());
        assert_eq!(database.host.engine().origin_epoch().unwrap(), (1, false));
        drop(database);
        member.close(std::time::Duration::from_secs(2)).unwrap();
        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn follower_inventory_opens_no_writer_and_needs_no_origin_fence() {
        let scope = DatabaseRouteScope::new(
            terrabase::identity::TenantUlid::generate(),
            terrabase::identity::ProjectUlid::generate(),
            terrabase::identity::DatabaseUlid::generate(),
        );
        let mut inventory = expected(scope);
        inventory.get_mut(&scope).unwrap().local_is_writer = false;
        let fences = fence_writer_hosts(&inventory, Vec::new(), 1).unwrap();
        assert!(fences.sequences().is_empty());
        assert!(fences.hosts().is_empty());
    }
}
