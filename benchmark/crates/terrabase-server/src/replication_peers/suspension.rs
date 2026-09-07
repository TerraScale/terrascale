//! Reversible lane release for the production database replacement barrier.
use super::*;
use std::collections::BTreeSet;

static NEXT_SUSPENSION: AtomicU64 = AtomicU64::new(1);

/// Exact owner-issued reservation for rebuilding one database's replication lanes.
/// Dropping this token does not re-enable access to files being replaced. A canceled
/// caller can recover the reservation by suspending the same scope again.
#[derive(Clone, Debug)]
pub struct ReplicationSuspension {
    scope: DatabaseRouteScope,
    generation: u64,
}

impl ReplicationPeers {
    /// Returns a recoverable in-process barrier after a canceled restore attempt.
    /// A process restart rebuilds lanes from the durable journal instead.
    ///
    /// # Errors
    /// Refuses a poisoned reservation registry.
    pub fn pending_suspension(
        &self,
        scope: DatabaseRouteScope,
    ) -> Result<Option<ReplicationSuspension>, String> {
        Ok(self
            .suspensions
            .lock()
            .map_err(failure)?
            .get(&scope)
            .map(|generation| ReplicationSuspension { scope, generation: *generation }))
    }

    /// Drains callbacks and releases engine/file leases for exactly one database.
    /// Accepted assignment metadata is retained; no epoch is retired.
    ///
    /// # Errors
    /// Refuses stopped workers or a barrier exceeding thirty seconds. A failed or
    /// canceled barrier remains retryable and must complete before replacing files.
    pub async fn suspend_database(
        &self,
        scope: DatabaseRouteScope,
    ) -> Result<ReplicationSuspension, String> {
        tokio::time::timeout(Duration::from_secs(30), self.suspend_inner(scope))
            .await
            .map_err(|_| "replication suspension timed out".to_owned())?
    }

    async fn suspend_inner(
        &self,
        scope: DatabaseRouteScope,
    ) -> Result<ReplicationSuspension, String> {
        let _operation = self.suspension_operation.lock().await;
        self.ensure_suspension_live()?;
        self.progress.selected_regions(scope).map_err(failure)?;
        let peers = self.scope_peers(scope)?;
        let mut reservations = Vec::new();
        for peer in &peers {
            reservations.push(
                self.admissions
                    .get(peer)
                    .ok_or("missing peer")?
                    .clone()
                    .reserve_owned()
                    .await
                    .map_err(failure)?,
            );
        }
        self.ensure_suspension_live()?;
        let admitted = self.admitted.lock().map_err(failure)?;
        let current = admitted
            .iter()
            .filter(|(_, candidate)| *candidate == scope)
            .map(|(peer, _)| peer.clone())
            .collect::<BTreeSet<_>>();
        if current != peers {
            return Err("replication assignment changed during suspension".into());
        }
        let generation = *self
            .suspensions
            .lock()
            .map_err(failure)?
            .entry(scope)
            .or_insert_with(|| NEXT_SUSPENSION.fetch_add(1, Ordering::Relaxed));
        drop(admitted);
        let mut responses = Vec::new();
        // Publish every command without yielding. Cancellation can only leave a
        // complete, retryable barrier, never an undelivered subset of peers.
        for reservation in reservations {
            let (done, response) = tokio::sync::oneshot::channel();
            reservation.send(PeerCommand::Suspend { scope, done });
            responses.push(response);
        }
        for response in responses {
            response.await.map_err(failure)?;
        }
        self.ensure_suspension_live()?;
        self.cuts.forget(scope)?;
        Ok(ReplicationSuspension { scope, generation })
    }

    /// Rebinds the suspended scope using lanes rebuilt from the accepted journal.
    /// The caller must reopen/register the restored engine before building this
    /// batch; its physical binding authenticates the restored epoch.
    ///
    /// # Errors
    /// Refuses stale tokens, changed peers/Regions, or unavailable workers. Queue
    /// reservation is cancellation safe; after commit all peers receive the batch.
    pub async fn resume_database(
        &self,
        token: &ReplicationSuspension,
        batch: PeerDatabaseBatch,
    ) -> Result<(), String> {
        tokio::time::timeout(Duration::from_secs(30), self.resume_inner(token, batch))
            .await
            .map_err(|_| "replication rebind timed out".to_owned())?
    }

    async fn resume_inner(
        &self,
        token: &ReplicationSuspension,
        batch: PeerDatabaseBatch,
    ) -> Result<(), String> {
        let _operation = self.suspension_operation.lock().await;
        self.ensure_suspension_live()?;
        batch.validate()?;
        if batch.selected_regions.len() != 1
            || batch.selected_regions.get(&token.scope)
                != Some(&self.progress.selected_regions(token.scope).map_err(failure)?)
            || batch.lanes.keys().cloned().collect::<BTreeSet<_>>()
                != self.scope_peers(token.scope)?
            || batch.lanes.values().any(|lanes| lanes.len() != 1 || lanes[0].scope != token.scope)
        {
            return Err("rebind differs from suspended assignment".into());
        }
        let mut reservations = Vec::new();
        for peer in batch.lanes.keys() {
            reservations.push(
                self.admissions
                    .get(peer)
                    .ok_or("missing peer")?
                    .clone()
                    .reserve_owned()
                    .await
                    .map_err(failure)?,
            );
        }
        self.ensure_suspension_live()?;
        let mut suspended = self.suspensions.lock().map_err(failure)?;
        if suspended.get(&token.scope) != Some(&token.generation) {
            return Err("stale replication suspension".into());
        }
        self.progress.reset_database_evidence(token.scope).map_err(failure)?;
        for ((peer, lanes), reservation) in batch.lanes.into_iter().zip(reservations) {
            observe(&self.observations, &peer, &lanes, false, 0);
            reservation.send(PeerCommand::Rebind(lanes));
        }
        suspended.remove(&token.scope);
        Ok(())
    }

    fn scope_peers(&self, scope: DatabaseRouteScope) -> Result<BTreeSet<String>, String> {
        Ok(self
            .admitted
            .lock()
            .map_err(failure)?
            .iter()
            .filter(|(_, candidate)| *candidate == scope)
            .map(|(peer, _)| peer.clone())
            .collect())
    }

    fn ensure_suspension_live(&self) -> Result<(), String> {
        if self.draining.load(Ordering::Acquire)
            || self.retiring.load(Ordering::Acquire)
            || *self.stop.borrow()
            || self.live_workers.lock().map_err(failure)?.len() != self.admissions.len()
        {
            return Err("replication owner unavailable for restore".into());
        }
        Ok(())
    }
}
