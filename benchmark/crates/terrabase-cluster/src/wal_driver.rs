//! Persistent physical RF=2 sessions for the production endpoint dispatcher.
//!
//! The dispatcher demultiplexes scoped frames and supplies identity captured
//! from its authenticated connection. Drivers never infer authority from seat
//! order or report serving readiness. A generation change requires installing
//! a fresh physical snapshot before constructing a replacement driver.

use crate::production::{DatabaseRouteScope, MemberWorkloadIdentity};
use crate::wal_follower::{WalCheckpoint, WalFollower, WalTail};
use crate::wal_quic::{self, WalCaptureStep, WalQuicError};
use crate::wal_route::{WalReplicaBinding, WalRouteError};
use crate::wal_wire::{ScopedWalAck, ScopedWalBatch, WalWireError};
use std::sync::{Arc, Mutex};
use terrabase_member_transport::production::ProductionConnection;

/// Keeps the production catalog lease alive for the entire physical session.
pub trait WalEngineHandle: Send + Sync {
    /// Borrows the live engine without releasing its admission lease.
    fn engine(&self) -> &terrabase::Engine;
}

impl WalEngineHandle for terrabase::Engine {
    fn engine(&self) -> &terrabase::Engine {
        self
    }
}

impl WalEngineHandle for crate::production::ProductionDatabaseLease {
    fn engine(&self) -> &terrabase::Engine {
        self
    }
}

/// Immutable authentication evidence captured from the actual admitted socket.
/// Construction is restricted to a production connection; wire identities are
/// never accepted as authentication evidence.
#[derive(Clone)]
pub struct WalAuthenticatedPeer {
    identity: MemberWorkloadIdentity,
    generation: u64,
    epoch: u64,
}

impl WalAuthenticatedPeer {
    /// Captures the connection's verified workload and admitted placement.
    #[must_use]
    pub fn from_connection(connection: &ProductionConnection) -> Self {
        Self {
            identity: connection.peer().clone(),
            generation: connection.placement_generation(),
            epoch: connection.replica_set_epoch(),
        }
    }

    pub(crate) fn authorize(
        &self,
        binding: &WalReplicaBinding,
        sending: bool,
    ) -> Result<(), WalQuicError> {
        binding
            .authorize_peer(&self.identity, self.generation, self.epoch, sending)
            .map_err(WalQuicError::Route)
    }
}

/// Scoped restart handshake. `None` requests the follower's durable checkpoint;
/// `Some` is the authenticated follower response. This proves WAL position only,
/// and must never substitute for establishing a shared physical base snapshot.
#[derive(Clone, Debug)]
pub struct WalCheckpointMessage {
    /// Immutable database identity.
    pub scope: DatabaseRouteScope,
    /// Accepted replica epoch.
    pub replica_epoch: u64,
    /// Durable follower position, or a request for that position.
    pub checkpoint: Option<WalCheckpoint>,
}

impl WalCheckpointMessage {
    /// Encodes the bounded restart control frame.
    ///
    /// # Errors
    /// Rejects invalid epochs or serialization failure.
    pub fn encode(&self) -> Result<Vec<u8>, WalWireError> {
        if self.replica_epoch == 0 {
            return Err(WalWireError::InvalidScope);
        }
        let mut bytes = b"TBWCP\0\0\x01".to_vec();
        for id in [
            self.scope.tenant().to_text(),
            self.scope.project().to_text(),
            self.scope.database().to_text(),
        ] {
            bytes.extend_from_slice(id.as_bytes());
        }
        bytes.extend_from_slice(&self.replica_epoch.to_be_bytes());
        bytes.extend(serde_json::to_vec(&self.checkpoint).map_err(|_| WalWireError::InvalidFrame)?);
        Ok(bytes)
    }

    /// Decodes a bounded control frame before any disk work is dispatched.
    ///
    /// # Errors
    /// Rejects unknown versions, malformed scope, zero epoch, and excess size.
    pub fn decode(bytes: &[u8]) -> Result<Self, WalWireError> {
        if bytes.len() > 1024 {
            return Err(WalWireError::Oversized);
        }
        if bytes.len() < 98 || &bytes[..8] != b"TBWCP\0\0\x01" {
            return Err(WalWireError::InvalidFrame);
        }
        let id = |start| {
            std::str::from_utf8(&bytes[start..start + 26]).map_err(|_| WalWireError::InvalidScope)
        };
        let scope = DatabaseRouteScope::try_new(id(8)?, id(34)?, id(60)?)
            .map_err(|_| WalWireError::InvalidScope)?;
        let replica_epoch =
            u64::from_be_bytes(bytes[86..94].try_into().map_err(|_| WalWireError::InvalidFrame)?);
        if replica_epoch == 0 {
            return Err(WalWireError::InvalidScope);
        }
        let checkpoint =
            serde_json::from_slice(&bytes[94..]).map_err(|_| WalWireError::InvalidFrame)?;
        Ok(Self { scope, replica_epoch, checkpoint })
    }

    fn authorize(&self, binding: &WalReplicaBinding) -> Result<(), WalQuicError> {
        if self.scope != binding.scope() || self.replica_epoch != binding.epoch() {
            return Err(WalQuicError::Route(WalRouteError::Unauthorized));
        }
        Ok(())
    }
}

/// A writer tail survives transport reconnects and retains unacknowledged bytes.
pub struct WalWriterDriver {
    binding: WalReplicaBinding,
    engine: Arc<dyn WalEngineHandle>,
    tail: Arc<Mutex<WalTail>>,
    needs_install: bool,
    installed: Option<crate::wal_snapshot::WalInstalledSnapshotProof>,
}

impl WalWriterDriver {
    /// Attaches a tail whose base snapshot was installed on the assigned follower.
    #[must_use]
    pub fn new(
        binding: WalReplicaBinding,
        engine: Arc<dyn WalEngineHandle>,
        tail: WalTail,
    ) -> Self {
        Self {
            binding,
            engine,
            tail: Arc::new(Mutex::new(tail)),
            needs_install: false,
            installed: None,
        }
    }

    /// Attaches installation evidence verified against the assigned follower.
    ///
    /// # Errors
    /// Rejects an installation for a different scope or replica epoch.
    pub fn adopt_installed_snapshot(
        &mut self,
        proof: crate::wal_snapshot::WalInstalledSnapshotProof,
    ) -> Result<(), WalQuicError> {
        self.binding.authorize_local(true).map_err(WalQuicError::Route)?;
        if proof.scope() != self.binding.scope()
            || proof.epoch() != self.binding.epoch()
            || proof.generation() != self.binding.generation()
        {
            return Err(WalQuicError::Route(WalRouteError::Unauthorized));
        }
        self.installed = Some(proof);
        self.needs_install = false;
        Ok(())
    }

    /// Combines actual install evidence with the current acknowledged WAL
    /// checkpoint and conservative durable SQLite sequence.
    ///
    /// # Errors
    /// Returns fencing or storage failure. `None` means no installed evidence
    /// or unsent bytes; it never manufactures a serving boundary.
    pub async fn physical_boundary(
        &mut self,
        scope: terrabase::types::TenantScope,
    ) -> Result<Option<WalPhysicalBoundary>, WalQuicError> {
        let Some(installed) = self.installed.clone() else {
            return Ok(None);
        };
        let Some(sequence) = self.durable_sequence(scope).await? else {
            return Ok(None);
        };
        let permit = self.binding.reserve_worker().map_err(WalQuicError::Route)?;
        let binding = self.binding.clone();
        let tail = self.tail.clone();
        tokio::task::spawn_blocking(move || {
            let _permit = permit;
            binding
                .with_admission(|| {
                    let checkpoint = tail.lock().map_err(|_| WalRouteError::Fenced)?.checkpoint();
                    let bytes =
                        serde_json::to_vec(&checkpoint).map_err(|_| WalRouteError::Unauthorized)?;
                    Ok(Some(WalPhysicalBoundary {
                        installed,
                        checkpoint: *blake3::hash(&bytes).as_bytes(),
                        sequence: sequence.get(),
                    }))
                })
                .map_err(WalQuicError::Route)
        })
        .await
        .map_err(|_| WalQuicError::WorkerUnavailable)?
    }

    /// Captures a replacement physical base after SQLite rotates past retained
    /// WAL history. The old proof is cleared immediately. The new tail starts
    /// at the captured checkpoint but must not send writes or claim durability
    /// until the assigned follower installs and acknowledges this new snapshot.
    ///
    /// # Errors
    /// Returns fencing, snapshot capture, or generation-validation failure.
    pub async fn refresh_snapshot(
        &mut self,
        staging_parent: &std::path::Path,
    ) -> Result<crate::wal_snapshot::WalSnapshotSource, crate::wal_snapshot::WalSnapshotError> {
        self.installed = None;
        self.needs_install = true;
        let staging = staging_parent
            .join(format!(".wal-source-{}", terrabase::identity::DatabaseUlid::generate()));
        let source = crate::wal_snapshot::WalSnapshotSource::capture(
            self.binding.clone(),
            self.engine.clone(),
            staging,
        )
        .await?;
        let checkpoint = source.manifest().checkpoint();
        let engine = self.engine.clone();
        let tail = self.tail.clone();
        let binding = self.binding.clone();
        let permit = binding.reserve_worker().map_err(WalQuicError::Route)?;
        tokio::task::spawn_blocking(move || {
            let _permit = permit;
            binding
                .with_admission(|| {
                    let replacement = WalTail::resume_engine(engine.engine(), checkpoint)
                        .map_err(WalRouteError::Storage)?;
                    *tail.lock().map_err(|_| WalRouteError::Fenced)? = replacement;
                    Ok(())
                })
                .map_err(WalQuicError::Route)
        })
        .await
        .map_err(|_| WalQuicError::WorkerUnavailable)??;
        Ok(source)
    }

    /// Creates the restart request for this admitted database assignment.
    #[must_use]
    pub fn checkpoint_request(&self) -> WalCheckpointMessage {
        WalCheckpointMessage {
            scope: self.binding.scope(),
            replica_epoch: self.binding.epoch(),
            checkpoint: None,
        }
    }

    /// Restores a restarted writer from the authenticated follower's checkpoint.
    /// Use only when starting a session, before capturing new pending batches.
    /// A shared physical snapshot must already be established independently.
    ///
    /// # Errors
    /// Rejects wrong authority, a request masquerading as a response, and a
    /// checkpoint that does not match the live writer's WAL checksum prefix.
    pub async fn resume(
        &mut self,
        peer: &WalAuthenticatedPeer,
        response: WalCheckpointMessage,
    ) -> Result<(), WalQuicError> {
        peer.authorize(&self.binding, true)?;
        response.authorize(&self.binding)?;
        let checkpoint =
            response.checkpoint.ok_or(WalQuicError::Wire(WalWireError::InvalidFrame))?;
        let permit = self.binding.reserve_worker().map_err(WalQuicError::Route)?;
        let binding = self.binding.clone();
        let engine = self.engine.clone();
        let tail = self.tail.clone();
        tokio::task::spawn_blocking(move || {
            let _permit = permit;
            binding
                .with_admission(|| {
                    let replacement = WalTail::resume_engine(engine.engine(), checkpoint)
                        .map_err(WalRouteError::Storage)?;
                    *tail.lock().map_err(|_| WalRouteError::Fenced)? = replacement;
                    Ok(())
                })
                .map_err(WalQuicError::Route)
        })
        .await
        .map_err(|_| WalQuicError::WorkerUnavailable)?
    }

    /// Captures a bounded batch, retaining it until a verified durable receipt.
    /// `UpToDate` is a point-in-time observation, not a permanent readiness flag.
    ///
    /// # Errors
    /// Returns a fenced assignment, capture, or worker failure.
    pub async fn capture(&mut self) -> Result<WalCaptureStep, WalQuicError> {
        wal_quic::capture_batch(&self.binding, self.engine.clone(), self.tail.clone()).await
    }

    /// Returns a conservative SQLite sequence covered by the follower's
    /// verified receipts. Read the sequence before capturing: observing an
    /// empty tail then proves all bytes through that earlier sequence were
    /// acknowledged. Reading the sequence after capture would race a commit.
    /// Requires the base snapshot contract of [`Self::new`].
    ///
    /// # Errors
    /// Returns snapshot, capture, or fencing failures. `None` means pending
    /// frames or a generation change; callers must continue capture/catch-up.
    pub async fn durable_sequence(
        &mut self,
        scope: terrabase::types::TenantScope,
    ) -> Result<Option<terrabase::types::StorageSequence>, WalQuicError> {
        if self.needs_install {
            return Ok(None);
        }
        let engine = self.engine.clone();
        let sequence = tokio::task::spawn_blocking(move || {
            engine
                .engine()
                .read_snapshot(&scope)
                .map(|snapshot| snapshot.sequence())
                .map_err(|_| WalQuicError::WorkerUnavailable)
        })
        .await
        .map_err(|_| WalQuicError::WorkerUnavailable)??;
        match self.capture().await? {
            WalCaptureStep::UpToDate => Ok(Some(sequence)),
            WalCaptureStep::Batch(_) | WalCaptureStep::GenerationChanged => Ok(None),
        }
    }

    /// Accepts a scoped receipt from the actual authenticated follower.
    ///
    /// # Errors
    /// Rejects wrong identity, placement, epoch, or pending batch checksum.
    pub async fn acknowledge(
        &mut self,
        peer: &WalAuthenticatedPeer,
        receipt: ScopedWalAck,
    ) -> Result<(), WalQuicError> {
        peer.authorize(&self.binding, true)?;
        let permit = self.binding.reserve_worker().map_err(WalQuicError::Route)?;
        let binding = self.binding.clone();
        let peer = peer.identity.clone();
        let tail = self.tail.clone();
        tokio::task::spawn_blocking(move || {
            let _permit = permit;
            let mut tail = tail.lock().map_err(|_| WalQuicError::WorkerUnavailable)?;
            binding.acknowledge(&peer, receipt, &mut tail).map_err(WalQuicError::Route)
        })
        .await
        .map_err(|_| WalQuicError::WorkerUnavailable)?
    }

    /// Exchanges at most `batch_budget` batches on an exclusively owned lane.
    /// Returns `Batch` when more work remains, `UpToDate` after catching up, or
    /// `GenerationChanged` when physical snapshot replacement is required.
    ///
    /// # Errors
    /// Returns transport, authenticated receipt, or capture failures. Retry on
    /// a fresh connection; the pending batch survives an interrupted exchange.
    pub async fn catch_up(
        &mut self,
        connection: &mut ProductionConnection,
        batch_budget: usize,
    ) -> Result<WalCaptureStep, WalQuicError> {
        self.binding.authorize_connection(connection, true).map_err(WalQuicError::Route)?;
        for _ in 0..batch_budget {
            match self.capture().await? {
                WalCaptureStep::Batch(batch) => {
                    wal_quic::send_batch(connection, &self.binding, &batch, self.tail.clone())
                        .await?
                }
                step => return Ok(step),
            }
        }
        self.capture().await
    }
}

/// Sealed combination of installed snapshot and actual durable WAL progress.
#[derive(Clone, Debug)]
pub struct WalPhysicalBoundary {
    installed: crate::wal_snapshot::WalInstalledSnapshotProof,
    checkpoint: [u8; 32],
    sequence: u64,
}
impl WalPhysicalBoundary {
    /// Accepted placement generation.
    #[must_use]
    pub fn generation(&self) -> u64 {
        self.installed.generation()
    }
    /// Exact database scope.
    #[must_use]
    pub fn scope(&self) -> DatabaseRouteScope {
        self.installed.scope()
    }
    /// Accepted replica epoch.
    #[must_use]
    pub fn epoch(&self) -> u64 {
        self.installed.epoch()
    }
    /// Installed base snapshot identity.
    #[must_use]
    pub fn snapshot_digest(&self) -> [u8; 32] {
        self.installed.snapshot_digest()
    }
    /// Current durable WAL checkpoint identity.
    #[must_use]
    pub fn checkpoint_digest(&self) -> [u8; 32] {
        self.checkpoint
    }
    /// Conservative SQLite sequence known included in durable files.
    #[must_use]
    pub fn sequence(&self) -> u64 {
        self.sequence
    }
}

/// An authenticated writer cut whose physical checkpoint exactly matches the
/// follower's installed base and synced WAL. The cut digest is opaque here;
/// its canonical causal/root frame belongs to the readiness protocol.
#[derive(Clone, Debug)]
pub struct WalVerifiedCut {
    boundary: WalPhysicalBoundary,
    cut: [u8; 32],
}
impl WalVerifiedCut {
    /// Accepted placement generation.
    #[must_use]
    pub fn generation(&self) -> u64 {
        self.boundary.generation()
    }
    /// Database scope verified against this follower assignment.
    #[must_use]
    pub fn scope(&self) -> DatabaseRouteScope {
        self.boundary.scope()
    }
    /// Accepted replica epoch.
    #[must_use]
    pub fn epoch(&self) -> u64 {
        self.boundary.epoch()
    }
    /// Installed base snapshot identity.
    #[must_use]
    pub fn snapshot_digest(&self) -> [u8; 32] {
        self.boundary.snapshot_digest()
    }
    /// Synced WAL checkpoint identity verified locally.
    #[must_use]
    pub fn checkpoint_digest(&self) -> [u8; 32] {
        self.boundary.checkpoint_digest()
    }
    /// Writer's sequence asserted at this exact physical cut.
    #[must_use]
    pub fn sequence(&self) -> u64 {
        self.boundary.sequence()
    }
    /// Digest of the full canonical readiness challenge bytes.
    #[must_use]
    pub fn cut_digest(&self) -> [u8; 32] {
        self.cut
    }
}

/// Persistent disk follower owned exclusively by one accepted assignment.
pub struct WalFollowerDriver {
    binding: WalReplicaBinding,
    follower: Arc<Mutex<WalFollower>>,
    installed: Option<crate::wal_snapshot::WalInstalledSnapshotProof>,
}

impl WalFollowerDriver {
    pub(crate) fn retire_for_replacement(
        self,
        binding: &WalReplicaBinding,
        path: &std::path::Path,
    ) -> Result<(), WalRouteError> {
        if self.binding.scope() != binding.scope()
            || self.binding.epoch() != binding.epoch()
            || self.binding.generation() != binding.generation()
        {
            return Err(WalRouteError::Unauthorized);
        }
        let follower = Arc::try_unwrap(self.follower)
            .map_err(|_| WalRouteError::Busy)?
            .into_inner()
            .map_err(|_| WalRouteError::Fenced)?;
        if follower.location() != path {
            return Err(WalRouteError::Unauthorized);
        }
        drop(follower);
        Ok(())
    }
    /// Reopens an existing encrypted physical replica after process restart,
    /// without opening a serving SQLite engine. Recovery does not manufacture
    /// install evidence: a newly authenticated snapshot/checkpoint handshake
    /// must establish the shared base before this driver can prove readiness.
    ///
    /// # Errors
    /// Rejects wrong roles, fences, unavailable keys, corrupt files, or failed sync.
    pub async fn recover(
        binding: WalReplicaBinding,
        location: std::path::PathBuf,
        keys: Option<Arc<dyn terrabase::encryption::DatabaseKeySource>>,
    ) -> Result<Self, WalQuicError> {
        binding.authorize_local(false).map_err(WalQuicError::Route)?;
        let permit = binding.reserve_worker().map_err(WalQuicError::Route)?;
        let worker_binding = binding.clone();
        let follower = tokio::task::spawn_blocking(move || {
            let _permit = permit;
            worker_binding
                .with_admission(|| {
                    if let Some(keys) = &keys {
                        let lease =
                            keys.current_lease().map_err(|_| WalRouteError::Unauthorized)?;
                        let scope = lease.metadata().scope();
                        if scope.tenant() != worker_binding.scope().tenant()
                            || scope.project() != Some(worker_binding.scope().project())
                            || scope.database() != worker_binding.scope().database()
                            || scope.purpose() != terrabase::encryption::KeyPurpose::Data
                        {
                            return Err(WalRouteError::Unauthorized);
                        }
                    }
                    let _registration = keys
                        .map(|keys| terrabase::storage::EncryptedStorage::register(&location, keys))
                        .transpose()
                        .map_err(|error| WalRouteError::Storage(error.into()))?;
                    let (follower, _) =
                        WalFollower::open_existing(&location).map_err(WalRouteError::Storage)?;
                    Ok(follower)
                })
                .map_err(WalQuicError::Route)
        })
        .await
        .map_err(|_| WalQuicError::WorkerUnavailable)??;
        Ok(Self::new(binding, follower))
    }

    /// Attaches a physically installed or recovered follower file.
    #[must_use]
    pub fn new(binding: WalReplicaBinding, follower: WalFollower) -> Self {
        Self { binding, follower: Arc::new(Mutex::new(follower)), installed: None }
    }

    /// Evidence from this driver's actual durable network/native snapshot install.
    #[must_use]
    pub fn installed_snapshot(&self) -> Option<&crate::wal_snapshot::WalInstalledSnapshotProof> {
        self.installed.as_ref()
    }

    pub(crate) fn with_installed_snapshot(
        mut self,
        proof: crate::wal_snapshot::WalInstalledSnapshotProof,
    ) -> Self {
        self.installed = Some(proof);
        self
    }

    /// Captures the current synced checkpoint plus installed physical base.
    /// Follower sequence remains conservative at the snapshot's sequence until
    /// a separately verified commit-to-WAL mapping is available.
    ///
    /// # Errors
    /// Returns fencing or worker failure; absent installation yields `None`.
    pub async fn physical_boundary(&mut self) -> Result<Option<WalPhysicalBoundary>, WalQuicError> {
        let Some(installed) = self.installed.clone() else {
            return Ok(None);
        };
        let permit = self.binding.reserve_worker().map_err(WalQuicError::Route)?;
        let binding = self.binding.clone();
        let follower = self.follower.clone();
        tokio::task::spawn_blocking(move || {
            let _permit = permit;
            binding
                .with_admission(|| {
                    let checkpoint =
                        follower.lock().map_err(|_| WalRouteError::Fenced)?.checkpoint();
                    let bytes =
                        serde_json::to_vec(&checkpoint).map_err(|_| WalRouteError::Unauthorized)?;
                    Ok(Some(WalPhysicalBoundary {
                        sequence: installed.sequence(),
                        installed,
                        checkpoint: *blake3::hash(&bytes).as_bytes(),
                    }))
                })
                .map_err(WalQuicError::Route)
        })
        .await
        .map_err(|_| WalQuicError::WorkerUnavailable)?
    }

    /// Verifies an authenticated writer's causal/root challenge against the
    /// follower's actual installed snapshot and durable WAL checkpoint. The
    /// higher layer hashes the complete canonical challenge into `cut_digest`.
    ///
    /// # Errors
    /// Rejects wrong writer authority, fences, or a different physical cut.
    pub async fn verify_boundary(
        &mut self,
        peer: &WalAuthenticatedPeer,
        snapshot_digest: [u8; 32],
        checkpoint_digest: [u8; 32],
        sequence: u64,
        cut_digest: [u8; 32],
    ) -> Result<WalVerifiedCut, WalQuicError> {
        peer.authorize(&self.binding, false)?;
        let Some(mut boundary) = self.physical_boundary().await? else {
            return Err(WalQuicError::Route(WalRouteError::Unauthorized));
        };
        if boundary.snapshot_digest() != snapshot_digest
            || boundary.checkpoint_digest() != checkpoint_digest
            || sequence < boundary.sequence()
        {
            return Err(WalQuicError::Route(WalRouteError::Unauthorized));
        }
        boundary.sequence = sequence;
        Ok(WalVerifiedCut { boundary, cut: cut_digest })
    }

    /// Applies a demultiplexed batch on the blocking pool and returns only a
    /// receipt whose WAL bytes have completed `fdatasync`.
    ///
    /// # Errors
    /// Rejects foreign identities, scopes, epochs, corrupt bytes, and failed sync.
    pub async fn apply(
        &mut self,
        peer: &WalAuthenticatedPeer,
        batch: ScopedWalBatch,
    ) -> Result<ScopedWalAck, WalQuicError> {
        peer.authorize(&self.binding, false)?;
        let permit = self.binding.reserve_worker().map_err(WalQuicError::Route)?;
        let binding = self.binding.clone();
        let peer = peer.identity.clone();
        let follower = self.follower.clone();
        tokio::task::spawn_blocking(move || {
            let _permit = permit;
            let mut follower = follower.lock().map_err(|_| WalQuicError::WorkerUnavailable)?;
            binding.apply(&peer, &batch, &mut follower).map_err(WalQuicError::Route)
        })
        .await
        .map_err(|_| WalQuicError::WorkerUnavailable)?
    }

    /// Answers restart recovery from synchronized follower state, under fencing.
    ///
    /// # Errors
    /// Rejects unauthenticated or stale requests and inaccessible follower files.
    pub async fn checkpoint(
        &mut self,
        peer: &WalAuthenticatedPeer,
        request: WalCheckpointMessage,
    ) -> Result<WalCheckpointMessage, WalQuicError> {
        peer.authorize(&self.binding, false)?;
        request.authorize(&self.binding)?;
        if request.checkpoint.is_some() {
            return Err(WalQuicError::Wire(WalWireError::InvalidFrame));
        }
        let permit = self.binding.reserve_worker().map_err(WalQuicError::Route)?;
        let binding = self.binding.clone();
        let follower = self.follower.clone();
        tokio::task::spawn_blocking(move || {
            let _permit = permit;
            let checkpoint = binding
                .with_admission(|| {
                    Ok(follower.lock().map_err(|_| WalRouteError::Fenced)?.checkpoint())
                })
                .map_err(WalQuicError::Route)?;
            Ok(WalCheckpointMessage { checkpoint: Some(checkpoint), ..request })
        })
        .await
        .map_err(|_| WalQuicError::WorkerUnavailable)?
    }

    /// Receives one exchange on an exclusively owned connection lane.
    ///
    /// # Errors
    /// Returns authentication, wire, disk sync, and transport failures.
    pub async fn receive(
        &mut self,
        connection: &mut ProductionConnection,
    ) -> Result<(), WalQuicError> {
        wal_quic::receive_batch(connection, &self.binding, self.follower.clone()).await
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::production::{ProductionPlacement, ProductionRegionSeats};
    use terrabase::identity::MemberUlid;
    use terrabase::types::{
        CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion, LocalDurability,
        OperationId, TenantId,
    };
    use terrabase::{Engine, EngineConfig, RegionCode};

    #[test]
    fn dispatched_receipts_and_restart_handshake_preserve_durable_progress() {
        let writer_id = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
        let follower_id = "01ARZ3NDEKTSV4RRFFQ69G5FAW";
        let writer_identity =
            MemberWorkloadIdentity::try_new("production", "cluster", writer_id).unwrap();
        let follower_identity =
            MemberWorkloadIdentity::try_new("production", "cluster", follower_id).unwrap();
        let scope = DatabaseRouteScope::try_new(writer_id, writer_id, writer_id).unwrap();
        let binding = |local| {
            WalReplicaBinding::new(
                scope,
                &ProductionPlacement::try_new(
                    MemberUlid::from_text(local).unwrap(),
                    1,
                    7,
                    [ProductionRegionSeats::try_new(
                        RegionCode::GRU,
                        [
                            MemberUlid::from_text(writer_id).unwrap(),
                            MemberUlid::from_text(follower_id).unwrap(),
                        ],
                    )
                    .unwrap()],
                )
                .unwrap(),
                writer_identity.clone(),
                follower_identity.clone(),
            )
            .unwrap()
        };
        let writer_binding = binding(writer_id);
        let follower_binding = binding(follower_id);
        let writer_peer =
            WalAuthenticatedPeer { identity: writer_identity, generation: 1, epoch: 7 };
        let follower_peer =
            WalAuthenticatedPeer { identity: follower_identity, generation: 1, epoch: 7 };
        let root = std::env::temp_dir().join(format!(
            "tb-wal-driver-{}-{}",
            std::process::id(),
            std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_nanos()
        ));
        let location = root.join("writer");
        let engine = Arc::new(Engine::open(EngineConfig::durable(&location).unwrap()).unwrap());
        let tenant = engine.scope_for(&TenantId::try_new("tenant-a").unwrap());
        let snapshot = engine.read_snapshot(&tenant).unwrap();
        let mut tx =
            engine.begin(&tenant, snapshot, OperationId::try_new("write").unwrap()).unwrap();
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
        let (replica, _) = WalFollower::bootstrap_engine(&engine, &root.join("follower")).unwrap();
        let mut writer =
            WalWriterDriver::new(writer_binding.clone(), engine.clone(), WalTail::new(&location));
        let mut follower = WalFollowerDriver::new(follower_binding, replica);
        let runtime = tokio::runtime::Builder::new_current_thread().build().unwrap();
        runtime.block_on(async {
            assert!(writer.durable_sequence(tenant.clone()).await.unwrap().is_none());
            let WalCaptureStep::Batch(batch) = writer.capture().await.unwrap() else {
                panic!("expected pending batch")
            };
            assert!(follower.apply(&follower_peer, batch.clone()).await.is_err());
            let receipt = follower.apply(&writer_peer, batch.clone()).await.unwrap();
            assert!(writer.acknowledge(&writer_peer, receipt.clone()).await.is_err());
            assert_eq!(writer.capture().await.unwrap(), WalCaptureStep::Batch(batch));
            writer.acknowledge(&follower_peer, receipt).await.unwrap();
            assert_eq!(
                writer.durable_sequence(tenant.clone()).await.unwrap(),
                Some(engine.read_snapshot(&tenant).unwrap().sequence())
            );
            let request =
                WalCheckpointMessage::decode(&writer.checkpoint_request().encode().unwrap())
                    .unwrap();
            let response = follower.checkpoint(&writer_peer, request).await.unwrap();
            let bytes = response.encode().unwrap();
            let mut restarted = WalWriterDriver::new(
                writer_binding.clone(),
                engine.clone(),
                WalTail::new(&location),
            );
            restarted
                .resume(&follower_peer, WalCheckpointMessage::decode(&bytes).unwrap())
                .await
                .unwrap();
            assert_eq!(restarted.capture().await.unwrap(), WalCaptureStep::UpToDate);
            let mut corrupt = response;
            let mut checkpoint =
                serde_json::to_value(corrupt.checkpoint.as_ref().unwrap()).unwrap();
            checkpoint["chain"][0] = serde_json::json!(0);
            corrupt.checkpoint = Some(serde_json::from_value(checkpoint).unwrap());
            assert!(restarted.resume(&follower_peer, corrupt).await.is_err());
            assert_eq!(restarted.capture().await.unwrap(), WalCaptureStep::UpToDate);
            writer_binding.fence();
            assert!(restarted.capture().await.is_err());
        });
        drop(follower);
        drop(writer);
        drop(engine);
        drop(runtime);
        std::fs::remove_dir_all(root).unwrap();
    }
}
