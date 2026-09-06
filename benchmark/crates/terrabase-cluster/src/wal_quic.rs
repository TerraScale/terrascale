//! One physical WAL batch/receipt exchange over an authenticated connection.
//!
//! The driver exclusively owns this connection's receive lane for the exchange;
//! it must not mix logical replication or other database responses on that lane.
//! Admission contention returns `WalRouteError::Busy` before network I/O; the
//! driver can retry after its current storage operation completes. A shared
//! per-binding permit allows one exchange or detached disk worker at a time;
//! cancellation cannot release its capacity while the disk operation still runs.
//! Failure or cancellation before receipt verification leaves the batch pending.
//! Once receipt verification is dispatched, the blocking worker can advance the
//! checkpoint even if the awaiting task is cancelled; that receipt is durable.
//! A blocking follower apply can finish after cancellation; its exact replay
//! returns the durable receipt without applying the bytes twice.

use std::sync::{Arc, Mutex};

use terrabase_member_transport::production::{ProductionConnection, ProductionTransportError};

use crate::wal_follower::{WalFollower, WalTail, WalTailStep};
use crate::wal_route::{WalReplicaBinding, WalRouteError};
use crate::wal_wire::{ScopedWalAck, ScopedWalBatch, WalWireError};

/// Failure to exchange a physical batch and a verified durable receipt.
#[derive(Debug)]
pub enum WalQuicError {
    /// Peer, role, scope, epoch, or durable storage rejected the operation.
    Route(WalRouteError),
    /// Invalid physical replication message.
    Wire(WalWireError),
    /// QUIC framing, deadline, admission, or connection failure.
    Transport(ProductionTransportError),
    /// The blocking worker or its storage lock failed.
    WorkerUnavailable,
}

/// Result of one bounded live-engine capture.
#[derive(Debug, Eq, PartialEq)]
pub enum WalCaptureStep {
    /// No committed bytes are waiting for delivery.
    UpToDate,
    /// A scoped batch, retained by the tail until its receipt is verified.
    Batch(ScopedWalBatch),
    /// The previous generation is no longer available; resynchronization is required.
    GenerationChanged,
}

/// Captures at most one bounded batch under the live engine's capture barrier.
/// The blocking worker retains its permit even if its awaiting task is cancelled.
/// Network delivery happens after the barrier is released; callers must handle
/// `GenerationChanged` rather than assuming history survives between captures.
///
/// # Errors
/// Returns admission, worker, wrong-engine, I/O, or WAL validation errors.
pub async fn capture_batch<E: crate::wal_driver::WalEngineHandle + ?Sized + 'static>(
    binding: &WalReplicaBinding,
    engine: Arc<E>,
    tail: Arc<Mutex<WalTail>>,
) -> Result<WalCaptureStep, WalQuicError> {
    let permit = binding.reserve_worker().map_err(WalQuicError::Route)?;
    let binding = binding.clone();
    tokio::task::spawn_blocking(move || {
        let _permit = permit;
        let mut tail = tail.lock().map_err(|_| WalQuicError::WorkerUnavailable)?;
        let step =
            binding.capture_frames(engine.engine(), &mut tail).map_err(WalQuicError::Route)?;
        match step {
            WalTailStep::UpToDate => Ok(WalCaptureStep::UpToDate),
            WalTailStep::GenerationChanged => Ok(WalCaptureStep::GenerationChanged),
            WalTailStep::Frames(frames) => binding
                .scope_frames(*frames)
                .map(WalCaptureStep::Batch)
                .map_err(WalQuicError::Route),
        }
    })
    .await
    .map_err(|_| WalQuicError::WorkerUnavailable)?
}

/// Sends an already captured pending batch and verifies its durable receipt.
/// Capture must run on the storage worker; only a matching receipt advances the tail.
///
/// # Errors
/// Returns authorization, framing, transport, worker, or receipt mismatch errors.
pub async fn send_batch(
    connection: &mut ProductionConnection,
    binding: &WalReplicaBinding,
    batch: &ScopedWalBatch,
    tail: Arc<Mutex<WalTail>>,
) -> Result<(), WalQuicError> {
    binding.authorize_connection(connection, true).map_err(WalQuicError::Route)?;
    let permit = binding.reserve_worker().map_err(WalQuicError::Route)?;
    binding.authorize_batch(batch).map_err(WalQuicError::Route)?;
    let bytes = batch.encode().map_err(WalQuicError::Wire)?;
    connection.send_frame(&bytes).await.map_err(WalQuicError::Transport)?;
    let bytes = connection.receive_frame().await.map_err(WalQuicError::Transport)?;
    let receipt = ScopedWalAck::decode(&bytes).map_err(WalQuicError::Wire)?;
    let binding = binding.clone();
    let peer = connection.peer().clone();
    tokio::task::spawn_blocking(move || {
        let _permit = permit;
        let mut tail = tail.lock().map_err(|_| WalQuicError::WorkerUnavailable)?;
        binding.acknowledge(&peer, receipt, &mut tail).map_err(WalQuicError::Route)
    })
    .await
    .map_err(|_| WalQuicError::WorkerUnavailable)?
}

/// Receives one batch, applies it on the blocking pool, then sends the fsynced receipt.
/// Authorization is checked again by the worker, including any intervening fence.
///
/// # Errors
/// Returns authorization, framing, transport, worker, or durable storage errors.
pub async fn receive_batch(
    connection: &mut ProductionConnection,
    binding: &WalReplicaBinding,
    follower: Arc<Mutex<WalFollower>>,
) -> Result<(), WalQuicError> {
    binding.authorize_connection(connection, false).map_err(WalQuicError::Route)?;
    let permit = binding.reserve_worker().map_err(WalQuicError::Route)?;
    let bytes = connection.receive_frame().await.map_err(WalQuicError::Transport)?;
    let batch = ScopedWalBatch::decode(&bytes).map_err(WalQuicError::Wire)?;
    let binding = binding.clone();
    let peer = connection.peer().clone();
    let receipt = tokio::task::spawn_blocking(move || {
        let _permit = permit;
        let mut follower = follower.lock().map_err(|_| WalQuicError::WorkerUnavailable)?;
        binding.apply(&peer, &batch, &mut follower).map_err(WalQuicError::Route)
    })
    .await
    .map_err(|_| WalQuicError::WorkerUnavailable)??;
    connection
        .send_frame(&receipt.encode().map_err(WalQuicError::Wire)?)
        .await
        .map_err(WalQuicError::Transport)
}
