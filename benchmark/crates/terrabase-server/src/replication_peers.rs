//! Bounded authenticated peer sessions shared by all assigned databases.
use crate::replication_runtime::ReplicationProgress;
use std::collections::BTreeMap;
use std::sync::Arc;
use std::time::Duration;
use terrabase_cluster::production::{DatabaseRouteScope, ProductionMemberRuntime};
use terrabase_cluster::wal_driver::{
    WalAuthenticatedPeer, WalCheckpointMessage, WalFollowerDriver, WalWriterDriver,
};
use terrabase_cluster::wal_quic::WalCaptureStep;
use terrabase_cluster::wal_route::WalReplicaBinding;
use terrabase_cluster::wal_snapshot::{
    WalSnapshotChunk, WalSnapshotManifest, WalSnapshotRequest, WalSnapshotSink, WalSnapshotSource,
};
use terrabase_cluster::wal_wire::{ScopedWalAck, ScopedWalBatch};
use terrabase_member_transport::identity::MemberId;
use terrabase_member_transport::production::ProductionConnection;
use terrabase_tbep::production_causal::{CausalDriver, CausalReceive};
use tokio::sync::{mpsc, watch};

/// A physical assignment with an independently established shared snapshot.
pub enum PhysicalDriver {
    /// Captured immutable base retained until the follower confirms installation.
    BootstrapWriter {
        /// Persistent writer tail.
        writer: WalWriterDriver,
        /// Captured physical files.
        source: WalSnapshotSource,
    },
    /// An empty physical seat awaiting an authenticated snapshot.
    BootstrapFollower(SnapshotReceiver),
    /// The engine lease is acquired from the production Member.
    Writer(WalWriterDriver),
    /// Raw physical files; no SQLite engine may open these files concurrently.
    Follower(WalFollowerDriver),
}
/// Local filesystem authorization for initial snapshot installation.
pub struct SnapshotReceiver {
    /// Accepted physical binding.
    pub binding: WalReplicaBinding,
    /// Exclusively owned empty physical database location.
    pub destination: std::path::PathBuf,
    /// Maximum admitted snapshot bytes.
    pub max_bytes: u64,
    sink: Option<WalSnapshotSink>,
    manifest: Option<WalSnapshotManifest>,
    offsets: [u64; 3],
}
impl SnapshotReceiver {
    /// Creates a bounded receive assignment without opening SQLite.
    #[must_use]
    pub fn new(
        binding: WalReplicaBinding,
        destination: std::path::PathBuf,
        max_bytes: u64,
    ) -> Self {
        Self { binding, destination, max_bytes, sink: None, manifest: None, offsets: [0; 3] }
    }
}
/// Persistent database state retained across peer reconnects.
pub struct PeerDatabase {
    /// Accepted database routing scope.
    pub scope: DatabaseRouteScope,
    /// Physical assignment, after snapshot installation.
    pub physical: Option<PhysicalDriver>,
    /// Cross-Region journal lane, restricted to an admitted regional writer.
    pub causal: Option<Arc<std::sync::Mutex<CausalDriver>>>,
}
/// Observations from live authenticated sessions; these do not certify repair completion.
#[derive(Clone, Copy, Debug, Default)]
pub struct PeerFacts {
    /// An authenticated session is currently active.
    pub connected: bool,
    /// The writer observed its physical tail durably caught up in this session.
    pub physical_caught_up: bool,
    /// At least one correlated causal receipt was observed in this session.
    pub causal_receipt_observed: bool,
    /// All local outbound work was acknowledged and no durable receipts remain queued.
    pub local_work_drained: bool,
}
/// Shared bounded observations keyed by peer identity and admitted database.
pub type PeerObservations =
    Arc<std::sync::Mutex<BTreeMap<(String, DatabaseRouteScope), PeerFacts>>>;
/// Owns the shared endpoint, bounded workers, and shutdown signal.
pub struct ReplicationPeers {
    observations: PeerObservations,
    stop: watch::Sender<bool>,
    join: tokio::task::JoinHandle<Result<(), String>>,
}
fn failure(error: impl std::fmt::Debug) -> String {
    format!("replication peer: {error:?}")
}
impl ReplicationPeers {
    /// Starts one persistent worker per admitted peer and one bounded accept loop.
    /// # Errors
    /// Rejects unadmitted peers and duplicate database lanes before spawning.
    pub fn start(
        runtime: Arc<ProductionMemberRuntime>,
        progress: Arc<ReplicationProgress>,
        peers: BTreeMap<String, Vec<PeerDatabase>>,
    ) -> Result<Self, String> {
        let endpoint = runtime.transport();
        if peers.values().map(Vec::len).sum::<usize>() > terrabase::member::MAX_DATABASES * 20 {
            return Err("replication database lane limit exceeded".into());
        }
        let mut validated = Vec::new();
        for (name, lanes) in peers {
            let id = MemberId::try_new(&name).map_err(failure)?;
            if endpoint.config().peer(&id).is_none() {
                return Err("replication peer is not admitted".into());
            }
            let mut scopes = std::collections::BTreeSet::new();
            if lanes.iter().any(|lane| !scopes.insert(lane.scope)) {
                return Err("duplicate replication database lane".into());
            }
            validated.push((id, lanes));
        }
        let observations: PeerObservations = Arc::default();
        for (id, lanes) in &validated {
            observe(&observations, id.as_str(), lanes, false);
        }
        let worker_observations = observations.clone();
        let (stop, stopped) = watch::channel(false);
        let worker_stop = stop.clone();
        let join = tokio::spawn(async move {
            let mut workers = tokio::task::JoinSet::new();
            let mut incoming = BTreeMap::new();
            for (id, lanes) in validated {
                let (sender, receiver) = mpsc::channel(1);
                incoming.insert(id.as_str().to_owned(), sender);
                workers.spawn(peer_worker(
                    runtime.clone(),
                    progress.clone(),
                    id,
                    lanes,
                    receiver,
                    stopped.clone(),
                    worker_observations.clone(),
                ));
            }
            let endpoint = runtime.transport();
            let mut stopped = stopped;
            loop {
                tokio::select! {
                    _ = stopped.changed() => break,
                    accepted = endpoint.accept() => match accepted {
                        Ok(Some(connection)) => {
                            // Only the lexically smaller member dials; duplicates are bounded and dropped.
                            if connection.peer().member().as_str() < endpoint.config().local_identity().member().as_str()
                                && let Some(sender) = incoming.get(connection.peer().member().as_str()) {
                                let _ = sender.try_send(connection);
                            }
                        }
                        Ok(None) => break,
                        Err(_) => { tokio::time::sleep(Duration::from_millis(100)).await; }
                    }
                }
            }
            let _ = worker_stop.send(true);
            drop(incoming);
            // Workers observe the stop signal between bounded operations.
            let mut worker_error = None;
            while let Some(result) = workers.join_next().await {
                if let Err(error) = result {
                    worker_error = Some(failure(error));
                }
            }
            drop(runtime);
            worker_error.map_or(Ok(()), Err)
        });
        Ok(Self { stop, join, observations })
    }
    /// Returns live observations without asserting completed anti-entropy repair.
    #[must_use]
    pub fn observations(&self) -> PeerObservations {
        self.observations.clone()
    }

    /// Waits for local outbound queues to empty after the caller stops origin writes.
    /// # Errors
    /// A timeout leaves the manager running and does not authorize a fence receipt.
    pub async fn wait_caught_up(&self, timeout: Duration) -> Result<(), String> {
        let deadline = tokio::time::Instant::now() + timeout;
        loop {
            if self
                .observations
                .lock()
                .map_err(failure)?
                .values()
                .all(|facts| facts.connected && facts.local_work_drained)
            {
                return Ok(());
            }
            if tokio::time::Instant::now() >= deadline {
                return Err("replication drain catch-up deadline exceeded".into());
            }
            tokio::time::sleep(Duration::from_millis(20)).await;
        }
    }

    /// Stops admission and sessions and releases the runtime owner.
    /// # Errors
    /// Reports a worker panic or an endpoint ownership violation.
    pub async fn drain(self) -> Result<(), String> {
        let _ = self.stop.send(true);
        self.join.await.map_err(failure)?
    }
}

async fn peer_worker(
    runtime: Arc<ProductionMemberRuntime>,
    progress: Arc<ReplicationProgress>,
    id: MemberId,
    mut lanes: Vec<PeerDatabase>,
    mut incoming: mpsc::Receiver<ProductionConnection>,
    mut stopped: watch::Receiver<bool>,
    observations: PeerObservations,
) {
    let endpoint = runtime.transport();
    let dial = endpoint.config().local_identity().member().as_str() < id.as_str();
    loop {
        let connection = tokio::select! {
            _ = stopped.changed() => return,
            connection = async {
                if dial { endpoint.connect(&id).await.ok() } else { incoming.recv().await }
            } => connection,
        };
        if let Some(connection) = connection {
            observe(&observations, id.as_str(), &lanes, true);
            let _ =
                session(connection, &runtime, &progress, &mut lanes, &stopped, &observations).await;
            observe(&observations, id.as_str(), &lanes, false);
        } else if !dial {
            return;
        }
        tokio::select! {
            _ = stopped.changed() => return,
            () = tokio::time::sleep(Duration::from_secs(1)) => {}
        }
    }
}

async fn session(
    connection: ProductionConnection,
    runtime: &Arc<ProductionMemberRuntime>,
    progress: &Arc<ReplicationProgress>,
    lanes: &mut [PeerDatabase],
    stopped: &watch::Receiver<bool>,
    observations: &PeerObservations,
) -> Result<(), String> {
    let peer = WalAuthenticatedPeer::from_connection(&connection);
    let region = connection.peer_region().as_str();
    let connection = Arc::new(connection);
    let (sender, mut frames) = mpsc::channel(8);
    let mut reader = tokio::task::JoinSet::new();
    let receive = connection.clone();
    reader.spawn(async move {
        loop {
            let frame = receive.receive_frame().await;
            let failed = frame.is_err();
            if sender.send(frame).await.is_err() || failed {
                break;
            }
        }
    });
    let mut resumed = std::collections::BTreeSet::new();
    let mut sent = BTreeMap::<DatabaseRouteScope, tokio::time::Instant>::new();
    let mut receipts = Vec::<(DatabaseRouteScope, u64, Vec<u8>)>::new();
    for lane in lanes.iter() {
        match &lane.physical {
            Some(PhysicalDriver::Writer(writer)) => connection
                .send_frame(&writer.checkpoint_request().encode().map_err(failure)?)
                .await
                .map_err(failure)?,
            Some(PhysicalDriver::BootstrapWriter { source, .. }) => connection
                .send_frame(&source.manifest().encode().map_err(failure)?)
                .await
                .map_err(failure)?,
            _ => {}
        }
    }
    let started = tokio::time::Instant::now();
    let mut tick = tokio::time::interval(Duration::from_millis(20));
    tick.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Skip);
    loop {
        if *stopped.borrow() {
            return Ok(());
        }
        tokio::select! {
            frame = frames.recv() => {
                let bytes = frame.ok_or_else(|| "replication reader closed".to_owned())?.map_err(failure)?;
                let scope = frame_scope(&bytes)?;
                let lane = lanes.iter_mut().find(|lane| lane.scope == scope).ok_or_else(|| "unassigned replication scope".to_owned())?;
                if bytes.starts_with(b"TBWS") {
                    snapshot_frame(lane, &peer, &connection, &bytes).await?;
                } else if bytes.starts_with(b"TBWCP") {
                    let message = WalCheckpointMessage::decode(&bytes).map_err(failure)?;
                    match lane.physical.as_mut() {
                        Some(PhysicalDriver::Writer(writer) | PhysicalDriver::BootstrapWriter { writer, .. }) => { writer.resume(&peer, message).await.map_err(failure)?; resumed.insert(scope); }
                        Some(PhysicalDriver::Follower(follower)) => {
                            let receipt = follower.checkpoint(&peer, message).await.map_err(failure)?;
                            connection.send_frame(&receipt.encode().map_err(failure)?).await.map_err(failure)?;
                        }
                        _ => return Err("unassigned physical lane".into()),
                    }
                } else if bytes.starts_with(b"TBWAL") {
                    let Some(PhysicalDriver::Follower(follower)) = lane.physical.as_mut() else { return Err("physical batch sent to non-follower".into()); };
                    let receipt = follower.apply(&peer, ScopedWalBatch::decode(&bytes).map_err(failure)?).await.map_err(failure)?;
                    connection.send_frame(&receipt.encode().map_err(failure)?).await.map_err(failure)?;
                } else if bytes.starts_with(b"TBWACK") {
                    let Some(PhysicalDriver::Writer(writer) | PhysicalDriver::BootstrapWriter { writer, .. }) = lane.physical.as_mut() else { return Err("physical receipt sent to non-writer".into()); };
                    writer.acknowledge(&peer, ScopedWalAck::decode(&bytes).map_err(failure)?).await.map_err(failure)?;
                    sent.remove(&scope);
                } else {
                    let causal = lane.causal.clone().ok_or_else(|| "unassigned causal lane".to_owned())?;
                    let runtime = runtime.clone();
                    let (result, sequence, frontier) = tokio::task::spawn_blocking(move || {
                        let engine = runtime.acquire_replication(&scope).map_err(failure)?;
                        let mut causal = causal.lock().map_err(failure)?;
                        let result = causal.receive_frame(&engine, &bytes, 0).map_err(failure)?;
                        let tenant = engine.scope_for(&terrabase::types::TenantId::try_new(scope.tenant().to_text()).map_err(failure)?);
                        let sequence = engine.read_snapshot(&tenant).map_err(failure)?.sequence().get();
                        Ok::<_, String>((result, sequence, causal.durable_peer_frontier().clone()))
                    }).await.map_err(failure)??;
                    match result {
                        CausalReceive::Applied { receipt, .. } => {
                            if !receipts.iter().any(|(queued_scope, _, bytes)| *queued_scope == scope && *bytes == receipt) {
                                if receipts.iter().filter(|(queued_scope, _, _)| *queued_scope == scope).count() >= 8 { return Err("causal durable receipt queue full".into()); }
                                receipts.push((scope, sequence, receipt));
                            }
                        }
                        CausalReceive::Deferred => {},
                        CausalReceive::Receipt { .. } => {
                            progress.advance_region(scope, region, &frontier).map_err(failure)?;
                            if let Ok(mut facts) = observations.lock() && let Some(facts) = facts.get_mut(&(connection.peer().member().as_str().to_owned(), scope)) { facts.causal_receipt_observed = true; }
                        },
                    }
                }
            }
            _ = tick.tick() => {
                let mut index = 0;
                while index < receipts.len() {
                    let (scope, sequence, bytes) = &receipts[index];
                    if progress.regional_covers(*scope, *sequence).map_err(failure)? {
                        connection.send_frame(bytes).await.map_err(failure)?;
                        receipts.swap_remove(index);
                    } else { index += 1; }
                }
                for lane in lanes.iter_mut() {
                    if let Some(PhysicalDriver::Writer(writer) | PhysicalDriver::BootstrapWriter { writer, .. }) = lane.physical.as_mut() && resumed.contains(&lane.scope) {
                        if sent.get(&lane.scope).is_some_and(|at| at.elapsed() < Duration::from_secs(2)) { continue; }
                        match writer.capture().await.map_err(failure)? {
                            WalCaptureStep::Batch(batch) => {
                                connection.send_frame(&batch.encode().map_err(failure)?).await.map_err(failure)?;
                                sent.insert(lane.scope, tokio::time::Instant::now());
                                if let Ok(mut facts) = observations.lock() && let Some(facts) = facts.get_mut(&(connection.peer().member().as_str().to_owned(), lane.scope)) { facts.physical_caught_up = false; }
                            },
                            WalCaptureStep::GenerationChanged => return Err("physical generation requires a new shared snapshot".into()),
                            WalCaptureStep::UpToDate => {
                                let scope = lane.scope;
                                let worker = runtime.clone();
                                let (tenant, frontier) = tokio::task::spawn_blocking(move || {
                                    let engine = worker.acquire_replication(&scope).map_err(failure)?;
                                    let tenant = engine.scope_for(&terrabase::types::TenantId::try_new(scope.tenant().to_text()).map_err(failure)?);
                                    let frontier = engine.durable_origin_prefixes(&tenant).map_err(failure)?;
                                    Ok::<_, String>((tenant, frontier))
                                }).await.map_err(failure)??;
                                if let Some(sequence) = writer.durable_sequence(tenant).await.map_err(failure)? {
                                    if let Ok(mut facts) = observations.lock() && let Some(facts) = facts.get_mut(&(connection.peer().member().as_str().to_owned(), scope)) { facts.physical_caught_up = true; }
                                    progress.advance_regional(scope, sequence.get());
                                    progress.advance_region(scope, runtime.transport().config().local_region().as_str(), &frontier).map_err(failure)?;
                                }
                            }
                        }
                    }
                    if let Some(causal) = lane.causal.clone() {
                        let worker = runtime.clone();
                        let scope = lane.scope;
                        let now = millis(started);
                        let (bytes, caught_up) = tokio::task::spawn_blocking(move || {
                            let engine = worker.acquire_replication(&scope).map_err(failure)?;
                            let mut causal = causal.lock().map_err(failure)?;
                            let bytes = causal.outbound_frame(&engine, now).map_err(failure)?;
                            let caught_up = causal.is_caught_up(&engine).map_err(failure)?;
                            Ok::<_, String>((bytes, caught_up))
                        }).await.map_err(failure)??;
                        if let Some(bytes) = bytes { connection.send_frame(&bytes).await.map_err(failure)?; }
                        if let Ok(mut facts) = observations.lock() && let Some(facts) = facts.get_mut(&(connection.peer().member().as_str().to_owned(), scope)) {
                            facts.local_work_drained = caught_up && !receipts.iter().any(|(scope, _, _)| *scope == lane.scope) && (lane.physical.is_none() || facts.physical_caught_up);
                        }
                    } else if let Ok(mut facts) = observations.lock() && let Some(facts) = facts.get_mut(&(connection.peer().member().as_str().to_owned(), lane.scope)) {
                        facts.local_work_drained = !matches!(lane.physical, Some(PhysicalDriver::Writer(_) | PhysicalDriver::BootstrapWriter { .. } | PhysicalDriver::BootstrapFollower(_))) || facts.physical_caught_up;
                    }
                }
            }
        }
    }
}
fn millis(start: tokio::time::Instant) -> u64 {
    u64::try_from(start.elapsed().as_millis()).unwrap_or(u64::MAX)
}
fn frame_scope(bytes: &[u8]) -> Result<DatabaseRouteScope, String> {
    if bytes.starts_with(b"TBCJ") {
        let mut remaining = &bytes[4..];
        let mut ids = Vec::with_capacity(3);
        for _ in 0..3 {
            let length = remaining.get(..4).ok_or("truncated causal scope")?;
            let length = u32::from_be_bytes(length.try_into().map_err(failure)?) as usize;
            if length != 26 {
                return Err("invalid causal scope length".into());
            }
            let value = remaining.get(4..4 + length).ok_or("truncated causal scope")?;
            ids.push(std::str::from_utf8(value).map_err(failure)?);
            remaining = &remaining[4 + length..];
        }
        DatabaseRouteScope::try_new(ids[0], ids[1], ids[2]).map_err(failure)
    } else if bytes.starts_with(b"TBWSM\0\0\x01")
        || bytes.starts_with(b"TBWSR\0\0\x01")
        || bytes.starts_with(b"TBWSC\0\0\x01")
        || bytes.starts_with(b"TBWAL\0\0\x01")
        || bytes.starts_with(b"TBWACK\0\x01")
        || bytes.starts_with(b"TBWCP\0\0\x01")
    {
        let id = |start| {
            std::str::from_utf8(bytes.get(start..start + 26).ok_or("truncated WAL scope")?)
                .map_err(failure)
        };
        DatabaseRouteScope::try_new(id(8)?, id(34)?, id(60)?).map_err(failure)
    } else {
        Err("unknown replication frame".into())
    }
}

fn observe(observations: &PeerObservations, peer: &str, lanes: &[PeerDatabase], connected: bool) {
    if let Ok(mut facts) = observations.lock() {
        for lane in lanes {
            facts.insert(
                (peer.to_owned(), lane.scope),
                PeerFacts { connected, ..PeerFacts::default() },
            );
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn demultiplexer_rejects_foreign_versions_and_truncated_scopes() {
        let id = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
        let expected = DatabaseRouteScope::try_new(id, id, id).unwrap();
        for magic in [b"TBWAL\0\0\x01", b"TBWACK\0\x01", b"TBWCP\0\0\x01"] {
            let mut bytes = magic.to_vec();
            for _ in 0..3 {
                bytes.extend_from_slice(id.as_bytes());
            }
            assert_eq!(frame_scope(&bytes).unwrap(), expected);
            bytes[7] = 2;
            assert!(frame_scope(&bytes).is_err());
        }
        let mut bytes = b"TBCJ".to_vec();
        for _ in 0..3 {
            bytes.extend_from_slice(&26_u32.to_be_bytes());
            bytes.extend_from_slice(id.as_bytes());
        }
        assert_eq!(frame_scope(&bytes).unwrap(), expected);
        for end in 0..bytes.len() {
            assert!(frame_scope(&bytes[..end]).is_err());
        }
        bytes[4..8].copy_from_slice(&u32::MAX.to_be_bytes());
        assert!(frame_scope(&bytes).is_err());
    }
}

async fn snapshot_frame(
    lane: &mut PeerDatabase,
    peer: &WalAuthenticatedPeer,
    connection: &ProductionConnection,
    bytes: &[u8],
) -> Result<(), String> {
    if bytes.starts_with(b"TBWSR") {
        let Some(PhysicalDriver::BootstrapWriter { source, .. }) = &lane.physical else {
            return Err("unassigned snapshot source".into());
        };
        let chunk = source
            .read(peer, WalSnapshotRequest::decode(bytes).map_err(failure)?)
            .await
            .map_err(failure)?;
        return connection.send_frame(&chunk.encode().map_err(failure)?).await.map_err(failure);
    }
    if bytes.starts_with(b"TBWSM") {
        let manifest = WalSnapshotManifest::decode(bytes).map_err(failure)?;
        if let Some(PhysicalDriver::Follower(follower)) = lane.physical.as_mut() {
            let response = follower
                .checkpoint(
                    peer,
                    WalCheckpointMessage {
                        scope: manifest.scope,
                        replica_epoch: manifest.replica_epoch,
                        checkpoint: None,
                    },
                )
                .await
                .map_err(failure)?;
            return connection
                .send_frame(&response.encode().map_err(failure)?)
                .await
                .map_err(failure);
        }
        let Some(PhysicalDriver::BootstrapFollower(receiver)) = lane.physical.as_mut() else {
            return Err("unassigned snapshot destination".into());
        };
        if receiver
            .manifest
            .as_ref()
            .is_none_or(|current| current.identity() != manifest.identity())
        {
            receiver.sink = Some(
                WalSnapshotSink::begin(
                    receiver.binding.clone(),
                    peer,
                    manifest.clone(),
                    receiver.destination.clone(),
                    receiver.max_bytes,
                )
                .await
                .map_err(failure)?,
            );
            receiver.manifest = Some(manifest);
            receiver.offsets = [0; 3];
        }
    } else {
        let chunk = WalSnapshotChunk::decode(bytes).map_err(failure)?;
        let file = usize::from(chunk.request().file);
        let Some(PhysicalDriver::BootstrapFollower(receiver)) = lane.physical.as_mut() else {
            return Err("unassigned snapshot destination".into());
        };
        let offset = receiver
            .sink
            .as_mut()
            .ok_or("snapshot manifest required")?
            .write(peer, chunk)
            .await
            .map_err(failure)?;
        *receiver.offsets.get_mut(file).ok_or("snapshot file slot invalid")? = offset;
    }
    let Some(PhysicalDriver::BootstrapFollower(receiver)) = lane.physical.as_mut() else {
        return Err("unassigned snapshot destination".into());
    };
    let manifest = receiver.manifest.as_ref().ok_or("snapshot manifest required")?;
    for file in 0..3_u8 {
        if let Some(length) = manifest.file_bytes(file)
            && receiver.offsets[usize::from(file)] < length
        {
            let request =
                WalSnapshotRequest::new(manifest, file, receiver.offsets[usize::from(file)]);
            return connection
                .send_frame(&request.encode().map_err(failure)?)
                .await
                .map_err(failure);
        }
    }
    let (follower, receipt) = receiver
        .sink
        .take()
        .ok_or("snapshot sink required")?
        .finish(peer)
        .await
        .map_err(failure)?;
    lane.physical = Some(PhysicalDriver::Follower(follower));
    connection.send_frame(&receipt.encode().map_err(failure)?).await.map_err(failure)
}
