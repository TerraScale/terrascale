//! Physical replication routing for one explicitly assigned regional writer.
//!
//! The composition supplies an accepted placement and database writer assignment,
//! and must fence the binding when either is fenced. Seat ordering is not a
//! writer election. Authenticated identities come from the QUIC connection.

use crate::production::{DatabaseRouteScope, MemberWorkloadIdentity, ProductionPlacement};
use crate::wal_follower::{WalFollower, WalFollowerError, WalTail};
use crate::wal_wire::{ScopedWalAck, ScopedWalBatch, WalWireError};
use std::sync::{Arc, Mutex, MutexGuard, TryLockError};
use terrabase::identity::MemberUlid;

/// One database's durably accepted regional writer assignment.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct WalAssignment {
    /// Immutable database routing scope.
    pub scope: DatabaseRouteScope,
    /// Accepted placement generation.
    pub generation: u64,
    /// Accepted replica-set epoch.
    pub epoch: u64,
    /// Exact writer workload identity.
    pub writer: MemberWorkloadIdentity,
    /// Exact same-region follower workload identity.
    pub follower: MemberWorkloadIdentity,
}

/// Coordinated authority for physical database writer roles. Implementations
/// must verify durable acceptance; health, seat order, and leases cannot grant it.
pub trait DurableWalAssignmentSource: Send + Sync {
    /// Loads and verifies the accepted assignment for exactly this scope.
    ///
    /// # Errors
    /// Distinguishes temporary inability to read from invalid or revoked authority.
    fn load_assignment(
        &self,
        scope: DatabaseRouteScope,
    ) -> Result<WalAssignment, WalAssignmentError>;
}

/// Failure to establish the database's accepted writer authority.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum WalAssignmentError {
    /// The authority store could not be read; prior acceptance remains valid.
    Unavailable,
    /// Missing, revoked, or invalid acceptance proof; prior bindings must be fenced.
    InvalidProof,
}

/// Database-specific physical replication binding to exact workload identities.
#[derive(Clone)]
pub struct WalReplicaBinding {
    active: Arc<Mutex<bool>>,
    worker: Arc<tokio::sync::Semaphore>,
    scope: DatabaseRouteScope,
    epoch: u64,
    generation: u64,
    local_is_writer: bool,
    writer: MemberWorkloadIdentity,
    follower: MemberWorkloadIdentity,
}

/// A routing or storage refusal; no successful receipt is issued on failure.
#[derive(Debug)]
pub enum WalRouteError {
    /// Wrong workload, region, role, database, or epoch.
    Unauthorized,
    /// The assignment was fenced or its admission lock was poisoned.
    Fenced,
    /// Another operation holds admission; retry without blocking the async runtime.
    Busy,
    /// The coordinated assignment source failed.
    Authority(WalAssignmentError),
    /// The WAL message or receipt is invalid.
    Wire(WalWireError),
    /// The follower could not validate or durably persist the batch.
    Storage(WalFollowerError),
}

impl WalReplicaBinding {
    /// Binds an explicitly assigned writer and its follower to accepted placement.
    /// The caller must prove the database scope belongs to this placement and
    /// the writer assignment was durably accepted; this does not elect a writer.
    ///
    /// # Errors
    /// Rejects cross-cluster identities, foreign seats, and cross-region copies.
    pub fn new(
        scope: DatabaseRouteScope,
        placement: &ProductionPlacement,
        writer: MemberWorkloadIdentity,
        follower: MemberWorkloadIdentity,
    ) -> Result<Self, WalRouteError> {
        let writer_id = MemberUlid::from_text(writer.member().as_str())
            .map_err(|_| WalRouteError::Unauthorized)?;
        let follower_id = MemberUlid::from_text(follower.member().as_str())
            .map_err(|_| WalRouteError::Unauthorized)?;
        if writer.environment() != follower.environment()
            || writer.cluster() != follower.cluster()
            || writer_id == follower_id
            || !placement.regions().any(|region| {
                region.region() == placement.local_region()
                    && region.members().contains(&writer_id)
                    && region.members().contains(&follower_id)
            })
        {
            return Err(WalRouteError::Unauthorized);
        }
        Ok(Self {
            active: Arc::new(Mutex::new(true)),
            worker: Arc::new(tokio::sync::Semaphore::new(1)),
            scope,
            epoch: placement.epoch(),
            generation: placement.generation(),
            local_is_writer: placement.local_member() == writer_id,
            writer,
            follower,
        })
    }

    /// Reconciles an authority observation without allowing a fenced role to revive.
    /// A temporary outage preserves prior acceptance but is still reported.
    ///
    /// # Errors
    /// Fences on changed or invalid authority and reports unavailable reads.
    pub fn reconcile_assignment(
        &self,
        observed: Result<&WalAssignment, WalAssignmentError>,
    ) -> Result<(), WalRouteError> {
        let mut active = self.active.lock().map_err(|_| WalRouteError::Fenced)?;
        if !*active {
            return Err(WalRouteError::Fenced);
        }
        match observed {
            Ok(assignment)
                if assignment.scope == self.scope
                    && assignment.generation == self.generation
                    && assignment.epoch == self.epoch
                    && assignment.writer == self.writer
                    && assignment.follower == self.follower =>
            {
                Ok(())
            }
            Err(WalAssignmentError::Unavailable) => {
                Err(WalRouteError::Authority(WalAssignmentError::Unavailable))
            }
            _ => {
                *active = false;
                Err(WalRouteError::Fenced)
            }
        }
    }

    pub(crate) fn capture_frames(
        &self,
        engine: &terrabase::Engine,
        tail: &mut WalTail,
    ) -> Result<crate::wal_follower::WalTailStep, WalRouteError> {
        let _admission = self.admission()?;
        if !self.local_is_writer {
            return Err(WalRouteError::Unauthorized);
        }
        tail.poll_engine(engine).map_err(WalRouteError::Storage)
    }

    pub(crate) fn scope_frames(
        &self,
        frames: crate::wal_follower::WalFrames,
    ) -> Result<ScopedWalBatch, WalRouteError> {
        let _admission = self.preflight_admission()?;
        if !self.local_is_writer {
            return Err(WalRouteError::Unauthorized);
        }
        Ok(ScopedWalBatch { scope: self.scope, replica_epoch: self.epoch, frames })
    }

    pub(crate) fn reserve_worker(
        &self,
    ) -> Result<tokio::sync::OwnedSemaphorePermit, WalRouteError> {
        let _admission = self.preflight_admission()?;
        self.worker.clone().try_acquire_owned().map_err(|_| WalRouteError::Busy)
    }

    pub(crate) fn authorize_connection(
        &self,
        connection: &terrabase_member_transport::production::ProductionConnection,
        sending: bool,
    ) -> Result<(), WalRouteError> {
        self.authorize_peer(
            connection.peer(),
            connection.placement_generation(),
            connection.replica_set_epoch(),
            sending,
        )
    }

    pub(crate) fn authorize_peer(
        &self,
        identity: &MemberWorkloadIdentity,
        generation: u64,
        epoch: u64,
        sending: bool,
    ) -> Result<(), WalRouteError> {
        let _admission = self.preflight_admission()?;
        let peer = if sending { &self.follower } else { &self.writer };
        if self.local_is_writer != sending
            || identity != peer
            || generation != self.generation
            || epoch != self.epoch
        {
            return Err(WalRouteError::Unauthorized);
        }
        Ok(())
    }

    pub(crate) fn scope(&self) -> DatabaseRouteScope {
        self.scope
    }

    pub(crate) fn epoch(&self) -> u64 {
        self.epoch
    }

    pub(crate) fn generation(&self) -> u64 {
        self.generation
    }

    pub(crate) fn authorize_local(&self, writer: bool) -> Result<(), WalRouteError> {
        let _admission = self.preflight_admission()?;
        if self.local_is_writer != writer {
            return Err(WalRouteError::Unauthorized);
        }
        Ok(())
    }

    pub(crate) fn authorize_batch(&self, batch: &ScopedWalBatch) -> Result<(), WalRouteError> {
        let _admission = self.preflight_admission()?;
        if batch.scope != self.scope || batch.replica_epoch != self.epoch {
            return Err(WalRouteError::Unauthorized);
        }
        batch.validate().map_err(WalRouteError::Wire)
    }

    /// Permanently fences this binding and all its clones.
    /// Waits for an already admitted apply or acknowledgment to finish. Once
    /// this returns, no operation using the old binding can change storage or
    /// the writer checkpoint. The composition must call this before admitting
    /// a replacement assignment; recreating a binding requires fresh authority.
    pub fn fence(&self) {
        let mut active = self.active.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
        *active = false;
    }

    /// Reports permanent revocation, including a poisoned admission lock.
    #[must_use]
    pub fn is_fenced(&self) -> bool {
        self.active.lock().map_or(true, |active| !*active)
    }

    // Called directly on the async runtime. Storage and fencing may hold the
    // same lock across disk I/O, so preflight must never wait for it.
    fn preflight_admission(&self) -> Result<MutexGuard<'_, bool>, WalRouteError> {
        let active = self.active.try_lock().map_err(|error| match error {
            TryLockError::WouldBlock => WalRouteError::Busy,
            TryLockError::Poisoned(_) => WalRouteError::Fenced,
        })?;
        if !*active {
            return Err(WalRouteError::Fenced);
        }
        Ok(active)
    }

    fn admission(&self) -> Result<MutexGuard<'_, bool>, WalRouteError> {
        let active = self.active.lock().map_err(|_| WalRouteError::Fenced)?;
        if !*active {
            return Err(WalRouteError::Fenced);
        }
        Ok(active)
    }

    pub(crate) fn with_admission<T>(
        &self,
        work: impl FnOnce() -> Result<T, WalRouteError>,
    ) -> Result<T, WalRouteError> {
        let _admission = self.admission()?;
        work()
    }

    /// Authorizes a decoded batch before touching the follower's files.
    /// `peer` must be the identity authenticated by the receiving connection.
    /// This synchronous storage operation belongs on the blocking worker pool.
    ///
    /// # Errors
    /// Rejects wrong roles, peers, scopes, epochs, malformed batches, or failed syncs.
    pub fn apply(
        &self,
        peer: &MemberWorkloadIdentity,
        batch: &ScopedWalBatch,
        follower: &mut WalFollower,
    ) -> Result<ScopedWalAck, WalRouteError> {
        let _admission = self.admission()?;
        if self.local_is_writer
            || peer != &self.writer
            || batch.scope != self.scope
            || batch.replica_epoch != self.epoch
        {
            return Err(WalRouteError::Unauthorized);
        }
        batch.validate().map_err(WalRouteError::Wire)?;
        let receipt = follower.apply(&batch.frames).map_err(WalRouteError::Storage)?;
        ScopedWalAck::from_follower(batch, receipt).map_err(WalRouteError::Wire)
    }

    /// Authorizes the authenticated follower before advancing the writer's tail.
    ///
    /// # Errors
    /// Rejects wrong roles or peers and receipts for any other scope, epoch, or batch.
    pub fn acknowledge(
        &self,
        peer: &MemberWorkloadIdentity,
        receipt: ScopedWalAck,
        tail: &mut WalTail,
    ) -> Result<(), WalRouteError> {
        let _admission = self.admission()?;
        if !self.local_is_writer || peer != &self.follower {
            return Err(WalRouteError::Unauthorized);
        }
        receipt.acknowledge(self.scope, self.epoch, tail).map_err(WalRouteError::Wire)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::wal_follower::WalFrames;

    fn binding() -> WalReplicaBinding {
        let id = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
        let scope = DatabaseRouteScope::try_new(id, id, id).unwrap();
        WalReplicaBinding {
            active: Arc::new(Mutex::new(true)),
            worker: Arc::new(tokio::sync::Semaphore::new(1)),
            scope,
            epoch: 7,
            generation: 1,
            local_is_writer: true,
            writer: MemberWorkloadIdentity::try_new("prod", "cluster", id).unwrap(),
            follower: MemberWorkloadIdentity::try_new(
                "prod",
                "cluster",
                "01ARZ3NDEKTSV4RRFFQ69G5FAW",
            )
            .unwrap(),
        }
    }

    #[test]
    fn async_preflight_refuses_contention_and_preserves_permanent_fences() {
        let binding = binding();
        let scope = binding.scope;
        let batch = ScopedWalBatch {
            scope,
            replica_epoch: 7,
            frames: WalFrames {
                header: vec![],
                from_frame: 0,
                bytes: vec![],
                committed_through: None,
            },
        };
        // Holding admission on this thread proves that preflight does not
        // attempt to acquire the blocking mutex used by disk operations.
        let held = binding.active.lock().unwrap();
        assert!(matches!(binding.authorize_batch(&batch), Err(WalRouteError::Busy)));
        drop(held);
        assert!(matches!(binding.authorize_batch(&batch), Err(WalRouteError::Wire(_))));
        binding.fence();
        assert!(matches!(binding.authorize_batch(&batch), Err(WalRouteError::Fenced)));
    }

    #[test]
    fn assignment_changes_fence_and_outages_cannot_revive_old_handles() {
        let template = binding();
        let accepted = WalAssignment {
            scope: template.scope,
            generation: template.generation,
            epoch: template.epoch,
            writer: template.writer.clone(),
            follower: template.follower.clone(),
        };
        template.reconcile_assignment(Ok(&accepted)).unwrap();
        assert!(matches!(
            template.reconcile_assignment(Err(WalAssignmentError::Unavailable)),
            Err(WalRouteError::Authority(WalAssignmentError::Unavailable))
        ));
        assert!(!template.is_fenced());
        template.reconcile_assignment(Ok(&accepted)).unwrap();
        let changes: Vec<fn(&mut WalAssignment)> = vec![
            |a| a.generation += 1,
            |a| a.epoch += 1,
            |a| std::mem::swap(&mut a.writer, &mut a.follower),
            |a| {
                a.scope = DatabaseRouteScope::try_new(
                    "01ARZ3NDEKTSV4RRFFQ69G5FAW",
                    "01ARZ3NDEKTSV4RRFFQ69G5FAV",
                    "01ARZ3NDEKTSV4RRFFQ69G5FAV",
                )
                .unwrap()
            },
        ];
        for change in changes {
            let binding = binding();
            let retained = binding.clone();
            let mut changed = accepted.clone();
            change(&mut changed);
            assert!(matches!(
                binding.reconcile_assignment(Ok(&changed)),
                Err(WalRouteError::Fenced)
            ));
            assert!(retained.is_fenced());
            assert!(matches!(
                retained.reconcile_assignment(Ok(&accepted)),
                Err(WalRouteError::Fenced)
            ));
            assert!(matches!(
                retained.reconcile_assignment(Err(WalAssignmentError::Unavailable)),
                Err(WalRouteError::Fenced)
            ));
        }
        assert!(matches!(
            template.reconcile_assignment(Err(WalAssignmentError::InvalidProof)),
            Err(WalRouteError::Fenced)
        ));
        assert!(template.is_fenced());
    }

    #[test]
    fn detached_disk_worker_retains_capacity_until_it_finishes() {
        let binding = binding();
        let retained = binding.clone();
        let permit = binding.reserve_worker().unwrap();
        let runtime = tokio::runtime::Builder::new_current_thread().build().unwrap();
        let (release, released) = std::sync::mpsc::channel();
        let (finished, completion) = std::sync::mpsc::channel();
        let worker = runtime.spawn_blocking(move || {
            released.recv().unwrap();
            drop(permit);
            finished.send(()).unwrap();
        });
        // Cancellation of the await detaches the disk worker; capacity must
        // remain held even though its original caller no longer awaits it.
        drop(worker);
        for _ in 0..100 {
            assert!(matches!(retained.reserve_worker(), Err(WalRouteError::Busy)));
        }
        release.send(()).unwrap();
        completion.recv_timeout(std::time::Duration::from_secs(5)).unwrap();
        drop(retained.reserve_worker().unwrap());
        binding.fence();
        assert!(matches!(retained.reserve_worker(), Err(WalRouteError::Fenced)));
    }

    #[test]
    fn async_capture_uses_live_engine_barrier_and_retains_pending_batch() {
        use crate::wal_quic::{capture_batch, WalCaptureStep};
        use terrabase::types::{
            CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion, LocalDurability,
            OperationId, TenantId,
        };
        use terrabase::{Engine, EngineConfig};
        let root = std::env::temp_dir().join(format!(
            "terrabase-async-capture-{}-{}",
            std::process::id(),
            std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_nanos()
        ));
        let location = root.join("writer");
        let engine = Arc::new(Engine::open(EngineConfig::durable(&location).unwrap()).unwrap());
        let scope = engine.scope_for(&TenantId::try_new("tenant-a").unwrap());
        let snapshot = engine.read_snapshot(&scope).unwrap();
        let mut tx =
            engine.begin(&scope, snapshot, OperationId::try_new("write").unwrap()).unwrap();
        tx.put(
            Document::new(
                DocumentId::try_new("doc").unwrap(),
                CollectionName::try_new("docs").unwrap(),
                DocumentValue::Integer(42),
            ),
            ExpectedVersion::New,
        )
        .unwrap();
        assert!(matches!(
            tx.commit(LocalDurability::Synced),
            terrabase::engine::CommitOutcome::Committed(_)
        ));
        let tail = Arc::new(Mutex::new(WalTail::new(&location)));
        let binding = binding();
        let runtime = tokio::runtime::Builder::new_current_thread().build().unwrap();
        let first =
            runtime.block_on(capture_batch(&binding, engine.clone(), tail.clone())).unwrap();
        assert!(matches!(first, WalCaptureStep::Batch(_)));
        assert_eq!(
            runtime.block_on(capture_batch(&binding, engine.clone(), tail.clone())).unwrap(),
            first
        );
        let other =
            Arc::new(Engine::open(EngineConfig::durable(root.join("other")).unwrap()).unwrap());
        assert!(runtime.block_on(capture_batch(&binding, other.clone(), tail.clone())).is_err());
        assert_eq!(
            runtime.block_on(capture_batch(&binding, engine.clone(), tail.clone())).unwrap(),
            first
        );
        binding.fence();
        assert!(runtime.block_on(capture_batch(&binding, engine.clone(), tail.clone())).is_err());
        drop(other);
        drop(engine);
        drop(tail);
        drop(runtime);
        std::fs::remove_dir_all(root).unwrap();
    }
}
