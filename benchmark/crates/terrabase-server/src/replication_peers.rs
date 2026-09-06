//! Bounded authenticated peer sessions shared by all assigned databases.
use crate::replication_cut::ReplicationCuts;
use crate::replication_proof::{ReadinessCutFrame, VerifiedReadinessCut};
use crate::replication_runtime::ReplicationProgress;
use std::collections::BTreeMap;
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::time::Duration;
use terrabase_cluster::production::{DatabaseRouteScope, ProductionMemberRuntime};
use terrabase_cluster::wal_driver::{
    WalAuthenticatedPeer, WalCheckpointMessage, WalFollowerDriver, WalPhysicalBoundary,
    WalWriterDriver,
};
use terrabase_cluster::wal_quic::WalCaptureStep;
use terrabase_cluster::wal_route::WalReplicaBinding;
use terrabase_cluster::wal_snapshot::{
    WalSnapshotAck, WalSnapshotChunk, WalSnapshotManifest, WalSnapshotRequest, WalSnapshotSink,
    WalSnapshotSource,
};
use terrabase_cluster::wal_wire::{ScopedWalAck, ScopedWalBatch};
use terrabase_member_transport::identity::MemberId;
use terrabase_member_transport::production::{ProductionConnection, ProductionConnectionClass};
use terrabase_tbep::production_causal::{CausalDriver, CausalReceive};
use terrabase_tbep::production_repair::{PublicationIndex, RepairDriver, VerifiedRepairCut};
use terrabase_tbep::registry::BoundScope;
use tokio::sync::{mpsc, watch};

#[cfg(test)]
mod admission_tests;
mod retirement;
#[cfg(test)]
pub(crate) use admission_tests::failed_peer_owner;

pub use retirement::{RetiredPeerLane, RetiredPhysicalLane, RetirementEvidence};

/// Application control frames multiplexed on the same authenticated endpoint.
pub trait PeerControlDispatcher: Send + Sync {
    /// Handles one bounded authenticated control frame; false means unknown protocol.
    fn dispatch<'a>(
        &'a self,
        connection: ProductionConnection,
        bytes: Vec<u8>,
    ) -> std::pin::Pin<Box<dyn std::future::Future<Output = Result<bool, String>> + Send + 'a>>;
}

/// Atomically admitted replication lanes and their accepted residency metadata.
pub struct PeerDatabaseBatch {
    /// Lanes grouped by authenticated peer.
    pub lanes: BTreeMap<String, Vec<PeerDatabase>>,
    /// Exact selected Regions for every lane scope.
    pub selected_regions: BTreeMap<DatabaseRouteScope, std::collections::BTreeSet<String>>,
}

impl PeerDatabaseBatch {
    fn validate(&self) -> Result<(), String> {
        let scopes = self
            .lanes
            .values()
            .flat_map(|lanes| lanes.iter().map(|lane| lane.scope))
            .collect::<std::collections::BTreeSet<_>>();
        if scopes != self.selected_regions.keys().copied().collect() {
            return Err("replication batch metadata does not match lanes".into());
        }
        Ok(())
    }
}
const PEER_HELLO: &[u8] = b"TBPEER\0\x01";

fn accepts_peer_frame(class: ProductionConnectionClass, bytes: &[u8]) -> bool {
    if bytes == PEER_HELLO {
        class == ProductionConnectionClass::Replication
    } else {
        class != ProductionConnectionClass::Replication
    }
}
/// A physical assignment with an independently established shared snapshot.
pub enum PhysicalDriver {
    /// Captured immutable base retained until the follower confirms installation.
    BootstrapWriter {
        /// Persistent writer tail.
        writer: WalWriterDriver,
        /// Captured physical files.
        source: Box<WalSnapshotSource>,
    },
    /// An empty physical seat awaiting an authenticated snapshot.
    BootstrapFollower(Box<SnapshotReceiver>),
    /// Installed follower retaining replacement authorization for WAL rotation.
    InstalledFollower {
        /// Exclusive physical follower.
        driver: WalFollowerDriver,
        /// Fixed catalog destination and key access.
        receiver: Box<SnapshotReceiver>,
    },
    /// The engine lease is acquired from the production Member.
    Writer(WalWriterDriver),
    /// Raw physical files; no `SQLite` engine may open these files concurrently.
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
    /// Database key access for validating encrypted physical files.
    pub keys: Option<Arc<dyn terrabase::encryption::DatabaseKeySource>>,
    /// Recovered exclusive physical files to atomically replace after validation.
    pub previous: Option<WalFollowerDriver>,
    sink: Option<WalSnapshotSink>,
    manifest: Option<WalSnapshotManifest>,
    offsets: [u64; 3],
}
impl SnapshotReceiver {
    /// Creates a bounded receive assignment without opening `SQLite`.
    #[must_use]
    pub fn new(
        binding: WalReplicaBinding,
        destination: std::path::PathBuf,
        max_bytes: u64,
    ) -> Self {
        Self {
            binding,
            destination,
            max_bytes,
            keys: None,
            previous: None,
            sink: None,
            manifest: None,
            offsets: [0; 3],
        }
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
#[derive(Clone, Debug, Default)]
#[expect(
    clippy::struct_excessive_bools,
    reason = "Independent replication observations are combined by readiness proofs"
)]
pub struct PeerFacts {
    /// An authenticated session is currently active.
    pub connected: bool,
    /// Owner token of the worker that published this observation.
    pub session_id: u64,
    /// Start of this authenticated session, stable across periodic comparisons.
    pub session_started: Option<std::time::Instant>,
    /// Region authenticated by the live peer connection.
    pub region: String,
    /// Sealed installed-base and durable WAL evidence.
    pub physical: Option<WalPhysicalBoundary>,
    /// Correlated equality proof for the exact source cut.
    pub repair: Option<VerifiedRepairCut>,
    /// Logical source cut authenticated against the actual local RF2 files.
    pub readiness: Option<VerifiedReadinessCut>,
    /// Monotonic observation time of the last newly verified cut.
    pub repair_observed_at: Option<std::time::Instant>,
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
    cuts: Arc<ReplicationCuts>,
    progress: Arc<ReplicationProgress>,
    admissions: BTreeMap<String, mpsc::Sender<PeerCommand>>,
    admitted: std::sync::Mutex<std::collections::BTreeSet<(String, DatabaseRouteScope)>>,
    observations: PeerObservations,
    live_workers: Arc<std::sync::Mutex<std::collections::BTreeSet<String>>>,
    stop: watch::Sender<bool>,
    draining: AtomicBool,
    retiring: AtomicBool,
    retirement_result: std::sync::Mutex<Option<Result<RetirementEvidence, String>>>,
    // Observe completion without contending with join ownership; never abort here.
    worker_status: tokio::task::AbortHandle,
    join: tokio::sync::Mutex<Option<tokio::task::JoinHandle<Result<(), String>>>>,
    drain_result: std::sync::Mutex<Option<Result<(), String>>>,
}
fn failure(error: impl std::fmt::Debug) -> String {
    format!("replication peer: {error:?}")
}
impl ReplicationPeers {
    /// Starts workers after atomically validating and admitting one batch.
    ///
    /// # Errors
    /// Returns an admission or worker startup error without partially starting
    /// the requested batch.
    pub fn start(
        runtime: Arc<ProductionMemberRuntime>,
        progress: Arc<ReplicationProgress>,
        batch: PeerDatabaseBatch,
    ) -> Result<Self, String> {
        batch.validate()?;
        Self::start_inner(runtime, progress, batch.lanes, batch.selected_regions, None)
    }

    /// Starts workers with an application-control dispatcher.
    ///
    /// # Errors
    /// Returns an admission or worker startup error without partially starting
    /// the requested batch.
    pub fn start_with_dispatch(
        runtime: Arc<ProductionMemberRuntime>,
        progress: Arc<ReplicationProgress>,
        batch: PeerDatabaseBatch,
        dispatcher: Arc<dyn PeerControlDispatcher>,
    ) -> Result<Self, String> {
        batch.validate()?;
        Self::start_inner(runtime, progress, batch.lanes, batch.selected_regions, Some(dispatcher))
    }
    fn start_inner(
        runtime: Arc<ProductionMemberRuntime>,
        progress: Arc<ReplicationProgress>,
        peers: BTreeMap<String, Vec<PeerDatabase>>,
        selected_regions: BTreeMap<DatabaseRouteScope, std::collections::BTreeSet<String>>,
        dispatcher: Option<Arc<dyn PeerControlDispatcher>>,
    ) -> Result<Self, String> {
        let endpoint = runtime.transport();
        let validated = validate_peer_lanes(&runtime, peers)?;
        let observations: PeerObservations = Arc::default();
        for (id, lanes) in &validated {
            observe(&observations, id.as_str(), lanes, false, 0);
        }
        let mut admitted = std::collections::BTreeSet::new();
        let mut by_peer: BTreeMap<_, _> = validated
            .into_iter()
            .map(|(id, lanes)| (id.as_str().to_owned(), (id, lanes)))
            .collect();
        let mut admissions = BTreeMap::new();
        let mut validated = Vec::new();
        for configured in endpoint.config().peers() {
            let id = configured.identity().member().clone();
            let lanes = by_peer.remove(id.as_str()).map_or_else(Vec::new, |(_, lanes)| lanes);
            for lane in &lanes {
                admitted.insert((id.as_str().to_owned(), lane.scope));
            }
            let (sender, receiver) = mpsc::channel(1);
            admissions.insert(id.as_str().to_owned(), sender);
            validated.push((id, lanes, receiver));
        }
        let cuts = Arc::new(ReplicationCuts::default());
        let live_workers = Arc::new(std::sync::Mutex::new(admissions.keys().cloned().collect()));
        let worker_lifecycle = live_workers.clone();
        let worker_cuts = cuts.clone();
        let retained_progress = progress.clone();
        let worker_observations = observations.clone();
        let (stop, stopped) = watch::channel(false);
        let worker_stop = stop.clone();
        let runtime_handle = tokio::runtime::Handle::try_current()
            .map_err(|_| "replication startup requires an active Tokio runtime")?;
        progress.admit_many(selected_regions).map_err(failure)?;
        let join = spawn_peer_owner(
            PeerOwnerContext {
                runtime,
                progress,
                validated,
                stopped,
                worker_stop,
                worker_lifecycle,
                worker_observations,
                worker_cuts,
                dispatcher,
            },
            runtime_handle,
        );
        Ok(Self {
            stop,
            draining: AtomicBool::new(false),
            retiring: AtomicBool::new(false),
            retirement_result: std::sync::Mutex::new(None),
            worker_status: join.abort_handle(),
            join: tokio::sync::Mutex::new(Some(join)),
            drain_result: std::sync::Mutex::new(None),
            observations,
            live_workers,
            admissions,
            admitted: std::sync::Mutex::new(admitted),
            cuts,
            progress: retained_progress,
        })
    }

    /// Captures one immutable physical/causal boundary from every configured
    /// peer worker. The workers remain parked with their lanes retained until
    /// the normal owner stop path releases them.
    ///
    /// # Errors
    /// Refuses a normal drain, missing worker response, or any lane that cannot
    /// produce its required durable boundary. No partial result is published.
    pub async fn capture_retirement(&self) -> Result<RetirementEvidence, String> {
        if self.draining.load(Ordering::Acquire) || *self.stop.borrow() {
            return Err("replication peer owner is draining".into());
        }
        if let Some(result) = self.retirement_result.lock().map_err(failure)?.clone() {
            return result;
        }
        let peers = {
            let admitted = self.admitted.lock().map_err(failure)?;
            if self.draining.load(Ordering::Acquire) || *self.stop.borrow() {
                return Err("replication peer owner is draining".into());
            }
            self.retiring.store(true, Ordering::Release);
            admitted.clone()
        };
        let mut responses = BTreeMap::new();
        for peer in self.admissions.keys() {
            let (done, response) = tokio::sync::oneshot::channel();
            self.admissions
                .get(peer)
                .ok_or("configured peer worker is missing")?
                .send(PeerCommand::Retire { done })
                .await
                .map_err(failure)?;
            let lanes = response.await.map_err(failure)??;
            for (scope, lane) in lanes {
                if !peers.contains(&(peer.clone(), scope)) {
                    return Err("retirement worker returned an unadmitted lane".into());
                }
                if responses.insert((peer.clone(), scope), lane).is_some() {
                    return Err("retirement worker returned a duplicate lane".into());
                }
            }
        }
        if responses.keys().cloned().collect::<std::collections::BTreeSet<_>>() != peers {
            return Err("retirement response lane set is incomplete".into());
        }
        if self.draining.load(Ordering::Acquire)
            || *self.stop.borrow()
            || self.live_workers.lock().map_err(failure)?.len() != self.admissions.len()
        {
            return Err("replication peer owner stopped during retirement capture".into());
        }
        let result = Ok(responses);
        *self.retirement_result.lock().map_err(failure)? = Some(result.clone());
        result
    }
    /// Adds newly accepted scopes to the bounded persistent peer workers.
    /// # Errors
    /// Rejects duplicate scopes, unadmitted peers, exceeded bounds, or stopped workers.
    pub async fn add_databases(&self, batch: PeerDatabaseBatch) -> Result<(), String> {
        if self.draining.load(Ordering::Acquire)
            || self.retiring.load(Ordering::Acquire)
            || *self.stop.borrow()
        {
            return Err("replication peer owner is draining".into());
        }
        batch.validate()?;
        let PeerDatabaseBatch { lanes: peers, selected_regions } = batch;
        // Reserve every bounded worker queue before publishing any assignment.
        // Cancellation or a closed receiver releases reservations without leaving
        // admission tombstones or a partially delivered multi-peer database.
        let mut reservations = Vec::with_capacity(peers.len());
        for peer in peers.keys() {
            reservations.push(
                self.admissions
                    .get(peer)
                    .ok_or("unadmitted replication peer")?
                    .clone()
                    .reserve_owned()
                    .await
                    .map_err(failure)?,
            );
        }
        let live_workers = self.live_workers.lock().map_err(failure)?;
        if live_workers.len() != self.admissions.len()
            || peers.keys().any(|peer| !live_workers.contains(peer))
        {
            return Err("replication peer worker stopped".into());
        }
        let mut admitted = self.admitted.lock().map_err(failure)?;
        if self.draining.load(Ordering::Acquire)
            || self.retiring.load(Ordering::Acquire)
            || *self.stop.borrow()
        {
            return Err("replication peer owner is draining".into());
        }
        let added = peers.values().map(Vec::len).sum::<usize>();
        if admitted.len().saturating_add(added) > terrabase::member::MAX_DATABASES * 20 {
            return Err("replication lane bound exceeded".into());
        }
        let mut unique = std::collections::BTreeSet::new();
        for (peer, lanes) in &peers {
            if !self.admissions.contains_key(peer) {
                return Err("unadmitted replication peer".into());
            }
            for lane in lanes {
                let key = (peer.clone(), lane.scope);
                if admitted.contains(&key) || !unique.insert(key) {
                    return Err("duplicate replication assignment".into());
                }
            }
        }
        self.progress.admit_many(selected_regions).map_err(failure)?;
        admitted.extend(unique);
        // No await occurs after the admission commit, so cancellation cannot
        // interrupt delivery between peer queues.
        for ((peer, lanes), reservation) in peers.into_iter().zip(reservations) {
            observe(&self.observations, &peer, &lanes, false, 0);
            reservation.send(PeerCommand::Add(lanes));
        }
        drop(admitted);
        drop(live_workers);
        Ok(())
    }

    /// Stops all peer callbacks for one scope and drops its engine leases.
    /// # Errors
    /// Rejects stopped workers; acknowledgments follow all in-flight disk work.
    pub async fn remove_database(&self, scope: DatabaseRouteScope) -> Result<(), String> {
        if self.draining.load(Ordering::Acquire)
            || self.retiring.load(Ordering::Acquire)
            || *self.stop.borrow()
        {
            return Err("replication peer owner is draining".into());
        }
        self.progress.fence_database(scope);
        self.cuts.forget(scope)?;
        let peers = self
            .admitted
            .lock()
            .map_err(failure)?
            .iter()
            .filter(|(_, database)| *database == scope)
            .map(|(peer, _)| peer.clone())
            .collect::<Vec<_>>();
        for peer in &peers {
            let (done, completed) = tokio::sync::oneshot::channel();
            self.admissions
                .get(peer)
                .ok_or("unadmitted replication peer")?
                .send(PeerCommand::Remove { scope, done })
                .await
                .map_err(failure)?;
            completed.await.map_err(failure)?;
        }
        // Retain admission tombstones: a removed epoch cannot be reintroduced.
        Ok(())
    }

    /// Returns live observations without asserting completed anti-entropy repair.
    #[must_use]
    pub fn observations(&self) -> PeerObservations {
        self.observations.clone()
    }

    /// Refuses a stopped accept owner or a lost peer worker, including between reconciliations.
    /// # Errors
    /// A failed worker requires a fresh runtime; previously verified receipts cannot revive it.
    pub fn ensure_running(&self) -> Result<(), String> {
        if self.draining.load(Ordering::Acquire)
            || self.worker_status.is_finished()
            || *self.stop.borrow()
            || self.live_workers.lock().map_err(failure)?.len() != self.admissions.len()
        {
            return Err("replication peer owner is no longer running".into());
        }
        Ok(())
    }

    /// Fences new lane admission and signals sessions to stop without consuming
    /// the peer owner. Already accepted callbacks may finish; [`Self::drain`]
    /// joins those callbacks and every worker and reports failures.
    pub fn begin_drain(&self) {
        // Serialize with the entire admission commit and reserved-queue dispatch.
        // Repeated callers also wait for that barrier before returning.
        let _admitted = self.admitted.lock();
        self.draining.store(true, Ordering::Release);
        let _ = self.stop.send(true);
    }

    /// Waits for local outbound queues to empty after the caller stops origin writes.
    /// This is a graceful-shutdown observation, never a fenced epoch or repair proof.
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
    /// Cancellation retains the worker join handle so a later call can finish
    /// the same drain and observe the cached result.
    /// # Errors
    /// Reports a worker panic or an endpoint ownership violation.
    pub async fn drain(&self) -> Result<(), String> {
        self.begin_drain();
        if let Some(result) = self.drain_result.lock().map_err(failure)?.clone() {
            return result;
        }
        let mut join = self.join.lock().await;
        if let Some(result) = self.drain_result.lock().map_err(failure)?.clone() {
            return result;
        }
        let result = match join.as_mut() {
            Some(handle) => handle.await.map_err(failure).and_then(|result| result),
            None => Err("replication peer owner has no worker handle".into()),
        };
        *join = None;
        *self.drain_result.lock().map_err(failure)? = Some(result.clone());
        result
    }

    /// Whether the worker join completed, including a terminal worker error.
    /// Unavailable join state never qualifies storage cleanup.
    pub(crate) fn drain_completed(&self) -> bool {
        self.drain_result.lock().is_ok_and(|result| result.is_some())
    }
}

struct PeerOwnerContext {
    runtime: Arc<ProductionMemberRuntime>,
    progress: Arc<ReplicationProgress>,
    validated: Vec<(MemberId, Vec<PeerDatabase>, mpsc::Receiver<PeerCommand>)>,
    stopped: watch::Receiver<bool>,
    worker_stop: watch::Sender<bool>,
    worker_lifecycle: Arc<std::sync::Mutex<std::collections::BTreeSet<String>>>,
    worker_observations: PeerObservations,
    worker_cuts: Arc<ReplicationCuts>,
    dispatcher: Option<Arc<dyn PeerControlDispatcher>>,
}

fn spawn_peer_owner(
    context: PeerOwnerContext,
    runtime_handle: tokio::runtime::Handle,
) -> tokio::task::JoinHandle<Result<(), String>> {
    runtime_handle.spawn(async move {
        let PeerOwnerContext { runtime, progress, validated, stopped, worker_stop, worker_lifecycle, worker_observations, worker_cuts, dispatcher } = context;
        let mut workers = tokio::task::JoinSet::new();
        let mut incoming = BTreeMap::new();
        for (id, lanes, new_lanes) in validated {
            let worker_id = NEXT_WORKER_ID.fetch_add(1, Ordering::Relaxed);
            let (sender, receiver) = mpsc::channel(1);
            incoming.insert(id.as_str().to_owned(), sender);
            workers.spawn(peer_worker(PeerWorkerContext {
                runtime: runtime.clone(), progress: progress.clone(), id: id.clone(), lanes,
                incoming: receiver, stopped: stopped.clone(), observations: worker_observations.clone(),
                new_lanes: PeerInbox { receiver: Some(new_lanes), peer: id.as_str().to_owned(), worker_id, live_workers: worker_lifecycle.clone(), progress: progress.clone(), observations: worker_observations.clone(), retirement: None },
                cuts: worker_cuts.clone(), worker_id,
            }));
        }
        let incoming = Arc::new(incoming);
        let mut classifiers = tokio::task::JoinSet::new();
        let endpoint = runtime.transport();
        let mut stopped = stopped;
        let mut worker_error = None;
        loop {
            tokio::select! {
                _ = stopped.changed() => break,
                result = workers.join_next(), if !workers.is_empty() => {
                    worker_error = Some(match result { Some(Err(error)) => failure(error), _ => "replication peer worker stopped unexpectedly".into() });
                },
                _ = classifiers.join_next(), if !classifiers.is_empty() => {},
                accepted = endpoint.accept() => match accepted {
                    Ok(Some(connection)) => {
                        if classifiers.len() >= 64 { continue; }
                        let incoming = incoming.clone();
                        let dispatcher = dispatcher.clone();
                        let local = endpoint.config().local_identity().member().as_str().to_owned();
                        classifiers.spawn(async move {
                            let Ok(bytes) = connection.receive_frame().await else { return; };
                            if !accepts_peer_frame(connection.class(), &bytes) { return; }
                            if bytes == PEER_HELLO {
                                if connection.peer().member().as_str() < local.as_str() && let Some(sender) = incoming.get(connection.peer().member().as_str()) { let _ = sender.try_send(connection); }
                            } else if let Some(dispatcher) = dispatcher { let _ = dispatcher.dispatch(connection, bytes).await; }
                        });
                    }
                    Ok(None) => break,
                    Err(_) => { tokio::time::sleep(Duration::from_millis(100)).await; }
                }
            }
        }
        let _ = worker_stop.send(true);
        while classifiers.join_next().await.is_some() {}
        drop(incoming);
        while let Some(result) = workers.join_next().await {
            if let Err(error) = result { worker_error = Some(failure(error)); }
        }
        drop(runtime);
        worker_error.map_or(Ok(()), Err)
    })
}

fn validate_peer_lanes(
    runtime: &Arc<ProductionMemberRuntime>,
    peers: BTreeMap<String, Vec<PeerDatabase>>,
) -> Result<Vec<(MemberId, Vec<PeerDatabase>)>, String> {
    let endpoint = runtime.transport();
    if endpoint.config().limits().max_frame_bytes()
        < terrabase_cluster::wal_wire::MAX_WAL_WIRE_BYTES + 1024
    {
        return Err("member frame limit cannot carry physical replication".into());
    }
    if peers.values().map(Vec::len).sum::<usize>() > terrabase::member::MAX_DATABASES * 20 {
        return Err("replication database lane limit exceeded".into());
    }
    peers
        .into_iter()
        .map(|(name, lanes)| {
            let id = MemberId::try_new(&name).map_err(failure)?;
            if endpoint.config().peer(&id).is_none() {
                return Err("replication peer is not admitted".into());
            }
            let mut scopes = std::collections::BTreeSet::new();
            if lanes.iter().any(|lane| !scopes.insert(lane.scope)) {
                return Err("duplicate replication database lane".into());
            }
            Ok((id, lanes))
        })
        .collect()
}

struct PeerWorkerContext {
    runtime: Arc<ProductionMemberRuntime>,
    progress: Arc<ReplicationProgress>,
    id: MemberId,
    lanes: Vec<PeerDatabase>,
    incoming: mpsc::Receiver<ProductionConnection>,
    stopped: watch::Receiver<bool>,
    observations: PeerObservations,
    new_lanes: PeerInbox,
    cuts: Arc<ReplicationCuts>,
    worker_id: u64,
}

static NEXT_WORKER_ID: AtomicU64 = AtomicU64::new(1);

async fn peer_worker(context: PeerWorkerContext) {
    let PeerWorkerContext {
        runtime,
        progress,
        id,
        mut lanes,
        mut incoming,
        mut stopped,
        observations,
        mut new_lanes,
        cuts,
        worker_id,
    } = context;
    let endpoint = runtime.transport();
    let dial = endpoint.config().local_identity().member().as_str() < id.as_str();
    loop {
        let connection = tokio::select! {
            _ = stopped.changed() => return,
            command = new_lanes.recv() => {
                if let Some(command) = command {
                    if let PeerCommand::Retire { done } = command {
                        let result = capture_retirement(&mut lanes, &mut new_lanes).await;
                        let _ = done.send(result);
                        retirement_park(&mut new_lanes, &mut stopped, &mut lanes).await;
                        return;
                    }
                    apply_command(command, &mut lanes, &observations, id.as_str());
                } else { return; }
                continue;
            },
            connection = async {
                if dial { endpoint.connect(&id, ProductionConnectionClass::Replication).await.ok() } else { incoming.recv().await }
            } => connection,
        };
        if let Some(connection) = connection {
            if dial && connection.send_frame(PEER_HELLO).await.is_err() {
                continue;
            }
            observe(&observations, id.as_str(), &lanes, true, worker_id);
            let session_result = session(
                connection,
                &mut lanes,
                SessionContext {
                    runtime: &runtime,
                    progress: &progress,
                    stopped: &stopped,
                    observations: &observations,
                    cuts: &cuts,
                },
                &mut new_lanes,
            )
            .await;
            if matches!(session_result, Err(ref error) if error == "__retire__") {
                retirement_park(&mut new_lanes, &mut stopped, &mut lanes).await;
                return;
            }
            observe(&observations, id.as_str(), &lanes, false, worker_id);
        } else if !dial {
            return;
        }
        tokio::select! {
            _ = stopped.changed() => return,
            () = tokio::time::sleep(Duration::from_secs(1)) => {}
        }
    }
}

async fn retirement_park(
    inbox: &mut PeerInbox,
    stopped: &mut watch::Receiver<bool>,
    lanes: &mut [PeerDatabase],
) {
    loop {
        tokio::select! {
            _ = stopped.changed() => return,
            command = inbox.recv() => match command {
                Some(PeerCommand::Retire { done }) => {
                    let result = capture_retirement(lanes, inbox).await;
                    let _ = done.send(result);
                }
                Some(PeerCommand::Remove { done, .. }) => { let _ = done.send(()); }
                Some(PeerCommand::Add(_)) => {}
                None => return,
            }
        }
    }
}

async fn capture_retirement(
    lanes: &mut [PeerDatabase],
    inbox: &mut PeerInbox,
) -> Result<Vec<(DatabaseRouteScope, RetiredPeerLane)>, String> {
    if let Some(result) = inbox.retirement.clone() {
        return result;
    }
    let result = retirement::capture_lanes(lanes).await;
    if result.is_ok() {
        inbox.retirement = Some(result.clone());
    }
    result
}

struct SessionContext<'a> {
    runtime: &'a Arc<ProductionMemberRuntime>,
    progress: &'a Arc<ReplicationProgress>,
    stopped: &'a watch::Receiver<bool>,
    observations: &'a PeerObservations,
    cuts: &'a Arc<ReplicationCuts>,
}

async fn session(
    connection: ProductionConnection,
    lanes: &mut Vec<PeerDatabase>,
    context: SessionContext<'_>,
    new_lanes: &mut PeerInbox,
) -> Result<(), String> {
    let SessionContext { runtime, progress, stopped, observations, cuts } = context;
    let peer = WalAuthenticatedPeer::from_connection(&connection);
    let region = connection.peer_region().as_str();
    if let Ok(mut facts) = observations.lock() {
        for lane in lanes.iter() {
            if let Some(facts) =
                facts.get_mut(&(connection.peer().member().as_str().to_owned(), lane.scope))
            {
                region.clone_into(&mut facts.region);
            }
        }
    }
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
    let mut sent_readiness = BTreeMap::new();
    let mut sent = BTreeMap::<DatabaseRouteScope, tokio::time::Instant>::new();
    let mut receipts = Vec::<(DatabaseRouteScope, u64, Vec<u8>)>::new();
    send_initial_checkpoints(&connection, lanes).await?;
    let repair_lanes = build_repair_lanes(runtime, &connection, lanes)?;
    let started = tokio::time::Instant::now();
    let mut tick = tokio::time::interval(Duration::from_millis(20));
    tick.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Skip);
    loop {
        if *stopped.borrow() {
            return Ok(());
        }
        tokio::select! {
            command = new_lanes.recv() => {
                if handle_session_command(command, lanes, observations, new_lanes, connection.peer().member().as_str()).await? { return Err("__retire__".into()); }
                return Ok(())
            },
            frame = frames.recv() => {
                let bytes = frame.ok_or_else(|| "replication reader closed".to_owned())?.map_err(failure)?;
                let scope = frame_scope(&bytes)?;
                progress.regional_covers(scope, 0).map_err(failure)?;
                let lane = lanes.iter_mut().find(|lane| lane.scope == scope).ok_or_else(|| "unassigned replication scope".to_owned())?;
                if bytes.starts_with(b"TBRCUT") {
                    let frame = ReadinessCutFrame::decode(&bytes).map_err(failure)?;
                    if frame.generation() != connection.placement_generation() || frame.epoch() != connection.replica_set_epoch() { return Err("stale readiness cut".into()); }
                    let Some(PhysicalDriver::Follower(follower) | PhysicalDriver::InstalledFollower { driver: follower, .. }) = lane.physical.as_mut() else { return Err("readiness cut requires physical follower".into()); };
                    let boundary = follower.verify_boundary(&peer, frame.snapshot_digest(), frame.checkpoint_digest(), frame.sequence(), frame.digest().map_err(failure)?).await.map_err(failure)?;
                    let verified = frame.authenticate(&boundary).map_err(failure)?;
                    progress.advance_regional_from_peer(scope, connection.peer().member().as_str(), frame.sequence());
                    progress.advance_region_from_peer(scope, connection.peer().member().as_str(), runtime.transport().config().local_region().as_str(), frame.frontier()).map_err(failure)?;
                    if let Ok(mut facts) = observations.lock() && let Some(facts) = facts.get_mut(&(connection.peer().member().as_str().to_owned(), scope)) { facts.readiness = Some(verified); }
                } else if bytes.starts_with(b"TBRP") {
                    let selected_cut = select_cut(cuts, runtime, progress, observations, scope, &connection).await?;
                    let repair = repair_lanes.get(&scope).cloned().ok_or("unassigned repair lane")?;
                    let worker = runtime.clone();
                    let causal = lane.causal.clone().ok_or("repair requires causal lane")?;
                    let responses = tokio::task::spawn_blocking(move || {
                        let engine = worker.acquire_replication(&scope).map_err(failure)?;
                        let mut repair = repair.lock().map_err(failure)?;
                        let RepairLane { bound, driver, index, requested, indexed } = &mut *repair;
                        *indexed = index.refresh(&engine, bound).map_err(failure)?;
                        if !*indexed { return Ok::<_, String>(Vec::new()); }
                        if let Some(cut) = selected_cut { driver.request_cut(&cut).map_err(failure)?; }
                        let result = driver.receive_frame(&engine, index, &bytes).map_err(failure)?;
                        if result.needs_snapshot { return Err("repair requires retained-history snapshot".into()); }
                        { let mut causal = causal.lock().map_err(failure)?;
                          for (origin, sequence, hash) in result.expected { causal.authorize_repair(origin, sequence, hash).map_err(failure)?; }
                        }
                        for operation in result.requested {
                            if !requested.contains(&operation) {
                                if requested.len() >= 4096 { return Err("repair request window exceeded".into()); }
                                requested.push_back(operation);
                            }
                        }
                        Ok(result.frames)
                    }).await.map_err(failure)??;
                    for response in responses { connection.send_frame(&response).await.map_err(failure)?; }
                } else if bytes.starts_with(b"TBWS") {
                    snapshot_frame(lane, &peer, &connection, &bytes).await?;
                    if let Some(PhysicalDriver::Follower(follower) | PhysicalDriver::InstalledFollower { driver: follower, .. }) = lane.physical.as_mut() {
                        let proof = follower.physical_boundary().await.map_err(failure)?;
                        if let Ok(mut facts) = observations.lock() && let Some(facts) = facts.get_mut(&(connection.peer().member().as_str().to_owned(), scope)) { facts.physical = proof; }
                    }
                } else if bytes.starts_with(b"TBWCP") {
                    let message = WalCheckpointMessage::decode(&bytes).map_err(failure)?;
                    match lane.physical.as_mut() {
                        Some(PhysicalDriver::Writer(writer) | PhysicalDriver::BootstrapWriter { writer, .. }) => { writer.resume(&peer, message).await.map_err(failure)?; resumed.insert(scope); }
                        Some(PhysicalDriver::Follower(follower) | PhysicalDriver::InstalledFollower { driver: follower, .. }) => {
                            let receipt = follower.checkpoint(&peer, message).await.map_err(failure)?;
                            connection.send_frame(&receipt.encode().map_err(failure)?).await.map_err(failure)?;
                        }
                        _ => return Err("unassigned physical lane".into()),
                    }
                } else if bytes.starts_with(b"TBWAL") {
                    let Some(PhysicalDriver::Follower(follower) | PhysicalDriver::InstalledFollower { driver: follower, .. }) = lane.physical.as_mut() else { return Err("physical batch sent to non-follower".into()); };
                    let receipt = follower.apply(&peer, ScopedWalBatch::decode(&bytes).map_err(failure)?).await.map_err(failure)?;
                    connection.send_frame(&receipt.encode().map_err(failure)?).await.map_err(failure)?;
                    let proof = follower.physical_boundary().await.map_err(failure)?;
                    if let Ok(mut facts) = observations.lock() && let Some(facts) = facts.get_mut(&(connection.peer().member().as_str().to_owned(), scope)) { facts.physical = proof; }
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
                        let tenant = engine.scope_for(&terrabase::types::TenantId::try_new(&scope.tenant().to_text()).map_err(failure)?);
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
                        CausalReceive::Fragment => {},
                        CausalReceive::Deferred => {
                            if let Some(repair) = repair_lanes.get(&scope) { repair.lock().map_err(failure)?.driver.trigger(); }
                        },
                        CausalReceive::Receipt { .. } => {
                            progress.advance_region_from_peer(scope, connection.peer().member().as_str(), region, &frontier).map_err(failure)?;
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
                    progress.regional_covers(lane.scope, 0).map_err(failure)?;
                    if let Some(PhysicalDriver::Writer(writer) | PhysicalDriver::BootstrapWriter { writer, .. }) = lane.physical.as_mut() && resumed.contains(&lane.scope) {
                        if sent.get(&lane.scope).is_some_and(|at| at.elapsed() < Duration::from_secs(2)) { continue; }
                        match writer.capture().await.map_err(failure)? {
                            WalCaptureStep::Batch(batch) => {
                                connection.send_frame(&batch.encode().map_err(failure)?).await.map_err(failure)?;
                                sent.insert(lane.scope, tokio::time::Instant::now());
                                if let Ok(mut facts) = observations.lock() && let Some(facts) = facts.get_mut(&(connection.peer().member().as_str().to_owned(), lane.scope)) { facts.physical_caught_up = false; }
                            },
                            WalCaptureStep::GenerationChanged => {
                                resumed.remove(&lane.scope);
                                sent.remove(&lane.scope);
                                sent_readiness.remove(&lane.scope);
                                if let Ok(mut facts) = observations.lock() && let Some(facts) = facts.get_mut(&(connection.peer().member().as_str().to_owned(), lane.scope)) { facts.physical = None; facts.readiness = None; facts.physical_caught_up = false; }
                                let Some(PhysicalDriver::BootstrapWriter { writer, source }) = lane.physical.as_mut() else { return Err("snapshot replacement lacks source authorization".into()); };
                                let parent = source.staging_parent().to_owned();
                                **source = writer.refresh_snapshot(&parent).await.map_err(failure)?;
                                connection.send_frame(&source.manifest().encode().map_err(failure)?).await.map_err(failure)?;
                            },
                            WalCaptureStep::UpToDate => {
                                let scope = lane.scope;
                                let worker = runtime.clone();
                                let (tenant, frontier) = tokio::task::spawn_blocking(move || {
                                    let engine = worker.acquire_replication(&scope).map_err(failure)?;
                                    let tenant = engine.scope_for(&terrabase::types::TenantId::try_new(&scope.tenant().to_text()).map_err(failure)?);
                                    let frontier = engine.durable_origin_prefixes(&tenant).map_err(failure)?;
                                    Ok::<_, String>((tenant, frontier))
                                }).await.map_err(failure)??;
                                if let Some(sequence) = writer.durable_sequence(tenant.clone()).await.map_err(failure)? {
                                    let proof = writer.physical_boundary(tenant).await.map_err(failure)?;
                                    if let Ok(mut facts) = observations.lock() && let Some(facts) = facts.get_mut(&(connection.peer().member().as_str().to_owned(), scope)) { facts.physical = proof; }
                                    if let Ok(mut facts) = observations.lock() && let Some(facts) = facts.get_mut(&(connection.peer().member().as_str().to_owned(), scope)) { facts.physical_caught_up = facts.physical.is_some(); }
                                    progress.advance_regional_from_peer(scope, connection.peer().member().as_str(), sequence.get());
                                    progress.advance_region_from_peer(scope, connection.peer().member().as_str(), runtime.transport().config().local_region().as_str(), &frontier).map_err(failure)?;
                                    let selected_cut = select_cut(cuts, runtime, progress, observations, scope, &connection).await?;
                                    let physical = observations.lock().map_err(failure)?.get(&(connection.peer().member().as_str().to_owned(), scope)).and_then(|facts| facts.physical.clone());
                                    if let Some((physical, cut)) = physical.zip(selected_cut) {
                                        let frame = ReadinessCutFrame::from_local(&cut, &physical).map_err(failure)?;
                                        let digest = frame.digest().map_err(failure)?;
                                        if sent_readiness.get(&scope).is_none_or(|(previous, at): &([u8; 32], tokio::time::Instant)| *previous != digest || at.elapsed() >= Duration::from_secs(10)) {
                                            let verified = frame.authorize_writer(&physical).map_err(failure)?;
                                            connection.send_frame(&frame.encode().map_err(failure)?).await.map_err(failure)?;
                                            if let Ok(mut facts) = observations.lock() && let Some(facts) = facts.get_mut(&(connection.peer().member().as_str().to_owned(), scope)) { facts.readiness = Some(verified); }
                                            sent_readiness.insert(scope, (digest, tokio::time::Instant::now()));
                                        }
                                    }
                                }
                            }
                        }
                    }
                    if let Some(repair) = repair_lanes.get(&lane.scope).cloned() {
                        let selected_cut = select_cut(cuts, runtime, progress, observations, lane.scope, &connection).await?;
                        let worker = runtime.clone();
                        let scope = lane.scope;
                        let causal = lane.causal.clone().ok_or("repair requires causal lane")?;
                        let now = millis(started);
                        let (frames, verified) = tokio::task::spawn_blocking(move || {
                            let engine = worker.acquire_replication(&scope).map_err(failure)?;
                            let mut repair = repair.lock().map_err(failure)?;
                            let RepairLane { bound, driver, index, requested, indexed } = &mut *repair;
                            *indexed = index.refresh(&engine, bound).map_err(failure)?;
                            if !*indexed { return Ok::<_, String>((Vec::new(), None)); }
                            let mut frames = Vec::new();
                            let mut causal = causal.lock().map_err(failure)?;
                            if !causal.has_pending() && let Some((origin, sequence)) = requested.front() {
                                let publication = index.publication(&engine, bound, origin, *sequence).map_err(failure)?;
                                frames.push(causal.publication_frame(&publication, now).map_err(failure)?);
                                requested.pop_front();
                            }
                            if let Some(cut) = selected_cut { driver.request_cut(&cut).map_err(failure)?; }
                            if let Some(frame) = driver.outbound_frame(&engine, index, now).map_err(failure)? { frames.push(frame); }
                            Ok((frames, driver.verified_cut().cloned()))
                        }).await.map_err(failure)??;
                        if let Ok(mut facts) = observations.lock() && let Some(facts) = facts.get_mut(&(connection.peer().member().as_str().to_owned(), scope)) {
                            let changed = facts.repair.as_ref().map(|proof| (proof.cut_digest(), proof.root_digest(), proof.local_sequence())) != verified.as_ref().map(|proof| (proof.cut_digest(), proof.root_digest(), proof.local_sequence()));
                            if changed { facts.repair_observed_at = verified.as_ref().map(|_| std::time::Instant::now()); }
                            facts.repair = verified;
                        }
                        for frame in frames { connection.send_frame(&frame).await.map_err(failure)?; }
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

async fn handle_session_command(
    command: Option<PeerCommand>,
    lanes: &mut Vec<PeerDatabase>,
    observations: &PeerObservations,
    inbox: &mut PeerInbox,
    peer: &str,
) -> Result<bool, String> {
    match command {
        Some(PeerCommand::Retire { done }) => {
            let result = capture_retirement(lanes, inbox).await;
            let _ = done.send(result);
            Ok(true)
        }
        Some(command) => {
            apply_command(command, lanes, observations, peer);
            Ok(false)
        }
        None => Ok(false),
    }
}

async fn send_initial_checkpoints(
    connection: &Arc<ProductionConnection>,
    lanes: &[PeerDatabase],
) -> Result<(), String> {
    for lane in lanes {
        let frame = match &lane.physical {
            Some(PhysicalDriver::Writer(writer)) => {
                writer.checkpoint_request().encode().map_err(failure)?
            }
            Some(PhysicalDriver::BootstrapWriter { source, .. }) => {
                source.manifest().encode().map_err(failure)?
            }
            _ => continue,
        };
        connection.send_frame(&frame).await.map_err(failure)?;
    }
    Ok(())
}

fn build_repair_lanes(
    runtime: &Arc<ProductionMemberRuntime>,
    connection: &Arc<ProductionConnection>,
    lanes: &[PeerDatabase],
) -> Result<BTreeMap<DatabaseRouteScope, Arc<std::sync::Mutex<RepairLane>>>, String> {
    let mut repair_lanes = BTreeMap::new();
    for lane in lanes.iter().filter(|lane| lane.causal.is_some()) {
        let bound = BoundScope {
            principal: runtime.transport().config().local_identity().as_str().to_owned(),
            tenant: lane.scope.tenant().to_text(),
            project: lane.scope.project().to_text(),
            database: lane.scope.database().to_text(),
        };
        let driver = RepairDriver::new(
            runtime.transport().config().local_identity().member().clone(),
            connection.peer().member().clone(),
            bound.clone(),
            connection.placement_generation(),
            connection.replica_set_epoch(),
        )
        .map_err(failure)?;
        repair_lanes.insert(
            lane.scope,
            Arc::new(std::sync::Mutex::new(RepairLane {
                bound,
                driver,
                index: PublicationIndex::default(),
                requested: std::collections::VecDeque::new(),
                indexed: false,
            })),
        );
    }
    Ok(repair_lanes)
}
fn millis(_start: tokio::time::Instant) -> u64 {
    static START: std::sync::OnceLock<std::time::Instant> = std::sync::OnceLock::new();
    u64::try_from(START.get_or_init(std::time::Instant::now).elapsed().as_millis())
        .unwrap_or(u64::MAX)
}

fn frame_scope(bytes: &[u8]) -> Result<DatabaseRouteScope, String> {
    if bytes.starts_with(b"TBCJ") || bytes.starts_with(b"TBRP") {
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
    } else if bytes.starts_with(b"TBRCUT\0\x01")
        || bytes.starts_with(b"TBWSA\0\0\x01")
        || bytes.starts_with(b"TBWSM\0\0\x01")
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

fn observe(
    observations: &PeerObservations,
    peer: &str,
    lanes: &[PeerDatabase],
    connected: bool,
    session_id: u64,
) {
    if let Ok(mut facts) = observations.lock() {
        for lane in lanes {
            if !connected
                && facts
                    .get(&(peer.to_owned(), lane.scope))
                    .is_some_and(|current| current.connected && current.session_id != session_id)
            {
                continue;
            }
            facts.insert(
                (peer.to_owned(), lane.scope),
                PeerFacts {
                    connected,
                    session_id,
                    session_started: connected.then(std::time::Instant::now),
                    ..PeerFacts::default()
                },
            );
        }
    }
}

async fn snapshot_frame(
    lane: &mut PeerDatabase,
    peer: &WalAuthenticatedPeer,
    connection: &ProductionConnection,
    bytes: &[u8],
) -> Result<(), String> {
    if bytes.starts_with(b"TBWSA") {
        let Some(PhysicalDriver::BootstrapWriter { source, writer }) = lane.physical.as_mut()
        else {
            return Err("unassigned snapshot source".into());
        };
        let proof = source
            .authenticate_ack(peer, WalSnapshotAck::decode(bytes).map_err(failure)?)
            .map_err(failure)?;
        return writer.adopt_installed_snapshot(proof).map_err(failure);
    }
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
        let replacing = matches!(lane.physical.as_ref(), Some(PhysicalDriver::InstalledFollower { driver, .. }) if driver.installed_snapshot().is_none_or(|proof| proof.snapshot_digest() != manifest.identity()));
        if replacing {
            let Some(PhysicalDriver::InstalledFollower { driver, mut receiver }) =
                lane.physical.take()
            else {
                return Err("snapshot receiver lost".into());
            };
            receiver.previous = Some(driver);
            receiver.manifest = None;
            receiver.sink = None;
            receiver.offsets = [0; 3];
            lane.physical = Some(PhysicalDriver::BootstrapFollower(receiver));
        }
        if let Some(
            PhysicalDriver::Follower(follower)
            | PhysicalDriver::InstalledFollower { driver: follower, .. },
        ) = lane.physical.as_mut()
        {
            if follower
                .installed_snapshot()
                .is_none_or(|proof| proof.snapshot_digest() != manifest.identity())
            {
                return Err("snapshot replacement lacks receiver authorization".into());
            }
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
            if let Some(proof) = follower.installed_snapshot() {
                connection
                    .send_frame(&proof.acknowledgment().encode().map_err(failure)?)
                    .await
                    .map_err(failure)?;
            }
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
                    receiver.keys.clone(),
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
    finish_snapshot(lane, peer, connection).await
}

async fn finish_snapshot(
    lane: &mut PeerDatabase,
    peer: &WalAuthenticatedPeer,
    connection: &ProductionConnection,
) -> Result<(), String> {
    let Some(PhysicalDriver::BootstrapFollower(receiver)) = lane.physical.as_mut() else {
        return Err("snapshot receiver lost".into());
    };
    let sink = receiver.sink.take().ok_or("snapshot sink required")?;
    let (follower, receipt) = if let Some(previous) = receiver.previous.take() {
        sink.finish_replacing(peer, previous).await.map_err(failure)?
    } else {
        sink.finish(peer).await.map_err(failure)?
    };
    let ack = follower
        .installed_snapshot()
        .ok_or("installed snapshot proof required")?
        .acknowledgment()
        .encode()
        .map_err(failure)?;
    let Some(PhysicalDriver::BootstrapFollower(receiver)) = lane.physical.take() else {
        return Err("snapshot receiver lost".into());
    };
    lane.physical = Some(PhysicalDriver::InstalledFollower { driver: follower, receiver });
    connection.send_frame(&ack).await.map_err(failure)?;
    connection.send_frame(&receipt.encode().map_err(failure)?).await.map_err(failure)
}

struct RepairLane {
    bound: BoundScope,
    driver: RepairDriver,
    index: PublicationIndex,
    requested: std::collections::VecDeque<(String, u64)>,
    indexed: bool,
}

enum PeerCommand {
    Add(Vec<PeerDatabase>),
    Remove {
        scope: DatabaseRouteScope,
        done: tokio::sync::oneshot::Sender<()>,
    },
    Retire {
        done: tokio::sync::oneshot::Sender<
            Result<Vec<(DatabaseRouteScope, RetiredPeerLane)>, String>,
        >,
    },
}

/// Receiver closure and multi-peer admission share one synchronous commit boundary.
struct PeerInbox {
    receiver: Option<mpsc::Receiver<PeerCommand>>,
    peer: String,
    worker_id: u64,
    live_workers: Arc<std::sync::Mutex<std::collections::BTreeSet<String>>>,
    progress: Arc<ReplicationProgress>,
    observations: PeerObservations,
    retirement: Option<Result<Vec<(DatabaseRouteScope, RetiredPeerLane)>, String>>,
}

impl PeerInbox {
    async fn recv(&mut self) -> Option<PeerCommand> {
        self.receiver.as_mut()?.recv().await
    }
}

impl Drop for PeerInbox {
    fn drop(&mut self) {
        let mut live = self.live_workers.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
        live.remove(&self.peer);
        drop(self.receiver.take());
        // Losing a worker cannot leave a host acknowledging receipt waits or
        // advertising observations from a session whose owner no longer exists.
        self.progress.fence_peer(&self.peer);
        if let Ok(mut observations) = self.observations.lock() {
            for ((peer, _), facts) in observations.iter_mut() {
                if peer == &self.peer && facts.session_id == self.worker_id {
                    *facts = PeerFacts::default();
                }
            }
        }
    }
}

fn apply_command(
    command: PeerCommand,
    lanes: &mut Vec<PeerDatabase>,
    observations: &PeerObservations,
    peer: &str,
) {
    match command {
        PeerCommand::Add(added) => lanes.extend(added),
        PeerCommand::Remove { scope, done } => {
            lanes.retain(|lane| lane.scope != scope);
            if let Ok(mut observations) = observations.lock() {
                observations.remove(&(peer.to_owned(), scope));
            }
            let _ = done.send(());
        }
        PeerCommand::Retire { .. } => {}
    }
}

async fn select_cut(
    cuts: &Arc<ReplicationCuts>,
    runtime: &Arc<ProductionMemberRuntime>,
    progress: &Arc<ReplicationProgress>,
    observations: &PeerObservations,
    scope: DatabaseRouteScope,
    connection: &ProductionConnection,
) -> Result<Option<terrabase_tbep::production_repair::LocalRepairCut>, String> {
    let (cuts, runtime, progress, observations) =
        (cuts.clone(), runtime.clone(), progress.clone(), observations.clone());
    let peer = connection.peer().member().clone();
    let generation = connection.placement_generation();
    let epoch = connection.replica_set_epoch();
    tokio::task::spawn_blocking(move || {
        cuts.select(&runtime, &progress, &observations, scope, &peer, (generation, epoch))
    })
    .await
    .map_err(failure)?
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn physical_assignment_does_not_embed_snapshot_receive_buffers() {
        assert!(std::mem::size_of::<PhysicalDriver>() < std::mem::size_of::<SnapshotReceiver>());
        assert!(std::mem::size_of::<PhysicalDriver>() < std::mem::size_of::<WalSnapshotSource>());
    }

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

    #[test]
    fn connection_class_routes_hello_only_to_replication_and_other_frames_to_control() {
        assert!(accepts_peer_frame(ProductionConnectionClass::Replication, PEER_HELLO));
        assert!(!accepts_peer_frame(ProductionConnectionClass::AuthClient, PEER_HELLO));
        assert!(!accepts_peer_frame(ProductionConnectionClass::AuthConsensus, PEER_HELLO));
        assert!(!accepts_peer_frame(ProductionConnectionClass::Replication, b"auth-frame"));
        assert!(accepts_peer_frame(ProductionConnectionClass::AuthClient, b"auth-frame"));
        assert!(accepts_peer_frame(ProductionConnectionClass::AuthConsensus, b"auth-frame"));
    }

    #[test]
    fn stale_worker_cannot_clear_newer_connected_observation() {
        let scope = DatabaseRouteScope::try_new(
            "01ARZ3NDEKTSV4RRFFQ69G5FAV",
            "01ARZ3NDEKTSV4RRFFQ69G5FAW",
            "01ARZ3NDEKTSV4RRFFQ69G5FAX",
        )
        .unwrap();
        let observations: PeerObservations = Arc::default();
        observations.lock().unwrap().insert(
            ("peer".into(), scope),
            PeerFacts { connected: true, session_id: 2, ..PeerFacts::default() },
        );
        let facts = observations.lock().unwrap();
        assert!(facts.iter().any(|((peer, _), facts)| {
            peer == "peer" && facts.connected && facts.session_id != 1
        }));
        assert!(!facts.iter().any(|((peer, _), facts)| {
            peer == "peer" && facts.connected && facts.session_id != 2
        }));
    }
}
