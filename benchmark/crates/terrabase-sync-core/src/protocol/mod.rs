//! The `terrascale.sync.v2` client-channel wire protocol: the
//! versioned frame vocabulary spoken between client Sync Sessions and
//! the serving host, and the conformance-vector substrate.
//!
//! The protocol carries **TerraBase-native** wire payloads (the
//! replacement contract recorded in `docs/sync/terrascale-sync-v2.md`
//! and ADR 0024): delta and snapshot payloads ride as named, versioned
//! payload codecs (`terrabase-native/1`, `terrabase-snapshot/1`) inside
//! V2-owned typed frames, encoded by this crate's native CRDT substrate
//! over `terrabase-crdt` and `terrabase-causal`. The frame vocabulary,
//! envelope discipline, session semantics, payload encoding, and error
//! taxonomy are all `TerraBaseV2`'s; no external CRDT engine's payload
//! is spoken, and the superseded codec codes fail closed.
//!
//! Scope limits, stated exactly:
//!
//! - This is the client-channel protocol only. TBEP (SE-14) is a
//!   separate process-to-process protocol that *reuses this envelope
//!   discipline* — version, frame type, correlation/causation,
//!   idempotency key, content digest, fail closed — not these types.
//! - Server-ack-only mutations (PD-2026-08-26-12 D2): the only mutation
//!   frame a client may send is [`SyncV2Body::MutationRequest`], and
//!   the only answer is [`SyncV2Body::MutationAck`]. No frame kind
//!   carries an optimistic patch, a client-side delta, or any
//!   client-encoded payload.
//! - Trigger notifications (PD-2026-08-26-13 D3) are content-free: a
//!   collection scope plus the two commit identifiers. No document
//!   identity, no field names, no values.
//! - The budget constants below are written **definitions** (the
//!   contract each frame class is held to); measured bytes-on-wire
//!   budgets are SE-07/SE-15's. No measurement made here is a claim.

mod body;
mod frame;

pub use body::DocumentWrite;
pub use body::ErrorCode;
pub use body::FrameKind;
pub use body::MutationOutcome;
pub use body::PayloadCodec;
pub use body::ResyncReason;
pub use body::SyncV2Body;
pub use frame::IdempotencyKey;
pub use frame::ProtocolError;
pub use frame::SyncV2Frame;

use crate::FrameId;
use crate::SyncBatch;
use crate::SyncCore;
use crate::SyncCoreError;
use crate::SyncSnapshot;

/// The contract identity of the protocol.
pub const SYNC_PROTOCOL_CONTRACT: &str = "terrascale.sync.v2";

/// The wire version this module speaks. The leading byte of every
/// frame; every other value fails closed on decode — including `1`,
/// the v1 wire this contract deliberately does not speak (no V1 wire
/// compatibility; charter PD-2026-08-26-10).
pub const SYNC_PROTOCOL_VERSION: u8 = 2;

/// The fixed per-frame envelope overhead in bytes: version (1), kind
/// (2), correlation (8), causation (8), idempotency presence (1),
/// digest (8), body length (8), and the body's kind tag (2). A
/// heartbeat frame — the zero-field body — encodes to exactly this
/// many bytes.
pub const FIXED_ENVELOPE_BYTES: usize = 38;

/// The largest frame the decoder accepts. Larger inputs are rejected
/// before any field is parsed.
pub const MAX_FRAME_BYTES: usize = 16 * 1024 * 1024;

/// The largest encoded payload one delivery frame may carry (matches
/// the decode bound; the serving side's effective bound is its
/// configured batch budget).
pub const MAX_PAYLOAD_BYTES: usize = MAX_FRAME_BYTES - FIXED_ENVELOPE_BYTES;

/// The longest idempotency key (bytes).
pub const MAX_KEY_BYTES: usize = 128;

// --- Bytes-on-wire budget definitions (PD-2026-08-26-13 D1) ---------------
//
// Written definitions: each frame class carries a budget its frames
// must fit. The one exception is a single commit whose delta alone
// exceeds the delivery budget: it rides alone, flagged — never
// silently, never merged (the SE-02 seam contract). SE-07 measures
// against these definitions; nothing here is a measured result.

/// The delivery budget: the batch byte budget the default
/// [`crate::SyncCoreConfig`] configures, bounding every delta batch
/// and snapshot answer.
pub const DELIVERY_FRAME_BUDGET_BYTES: usize = 64 * 1024;

/// The trigger-notification budget: the total wire size one
/// content-free trigger may occupy. Typical frames are ~64 bytes; the
/// budget bounds the worst-case identifier lengths (64 + 64 bytes).
pub const TRIGGER_FRAME_BUDGET_BYTES: usize = 256;

/// The heartbeat budget: the fixed envelope and nothing more.
pub const HEARTBEAT_FRAME_BUDGET_BYTES: usize = 64;

/// The acknowledgement budget (checkpoint acknowledgements, mutation
/// acknowledgements).
pub const ACK_FRAME_BUDGET_BYTES: usize = 1024;

/// The subscription budget (modify requests and confirmations).
pub const SUBSCRIPTION_FRAME_BUDGET_BYTES: usize = 2048;

/// The session budget (setup and acceptance).
pub const SESSION_FRAME_BUDGET_BYTES: usize = 4096;

/// The control budget (resync-required, region route changes, errors).
pub const CONTROL_FRAME_BUDGET_BYTES: usize = 256;

/// The mutation budget: a typed document write rides a mutation frame.
pub const MUTATION_FRAME_BUDGET_BYTES: usize = DELIVERY_FRAME_BUDGET_BYTES;

/// The direction a frame kind travels.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Direction {
    /// Client to server.
    ClientToServer,
    /// Server to client.
    ServerToClient,
    /// Either direction (heartbeats).
    Both,
}

impl SyncV2Body {
    /// The direction this frame travels on the client channel.
    #[must_use]
    pub fn direction(&self) -> Direction {
        match self {
            SyncV2Body::SessionSetup { .. }
            | SyncV2Body::SubscriptionModify { .. }
            | SyncV2Body::CheckpointAck { .. }
            | SyncV2Body::MutationRequest { .. } => Direction::ClientToServer,
            SyncV2Body::SessionAccepted { .. }
            | SyncV2Body::SubscriptionChanged { .. }
            | SyncV2Body::DeltaBatch { .. }
            | SyncV2Body::SnapshotAnswer { .. }
            | SyncV2Body::Trigger { .. }
            | SyncV2Body::ResyncRequired { .. }
            | SyncV2Body::RegionRouteChange { .. }
            | SyncV2Body::Error { .. }
            | SyncV2Body::MutationAck { .. } => Direction::ServerToClient,
            SyncV2Body::Heartbeat => Direction::Both,
        }
    }

    /// The frame class whose written budget governs this frame.
    #[must_use]
    pub fn frame_class(&self) -> FrameClass {
        match self {
            SyncV2Body::SessionSetup { .. } | SyncV2Body::SessionAccepted { .. } => {
                FrameClass::Session
            }
            SyncV2Body::SubscriptionModify { .. } | SyncV2Body::SubscriptionChanged { .. } => {
                FrameClass::Subscription
            }
            SyncV2Body::CheckpointAck { .. } | SyncV2Body::MutationAck { .. } => {
                FrameClass::Acknowledgement
            }
            SyncV2Body::Heartbeat => FrameClass::Heartbeat,
            SyncV2Body::DeltaBatch { .. } | SyncV2Body::SnapshotAnswer { .. } => {
                FrameClass::Delivery
            }
            SyncV2Body::Trigger { .. } => FrameClass::Trigger,
            SyncV2Body::ResyncRequired { .. }
            | SyncV2Body::RegionRouteChange { .. }
            | SyncV2Body::Error { .. } => FrameClass::Control,
            SyncV2Body::MutationRequest { .. } => FrameClass::Mutation,
        }
    }
}

/// The frame class: the unit the written bytes-on-wire budget
/// definitions attach to.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum FrameClass {
    /// Session setup and acceptance.
    Session,
    /// Subscription modification.
    Subscription,
    /// Checkpoint and mutation acknowledgements.
    Acknowledgement,
    /// Heartbeats.
    Heartbeat,
    /// Delta batches and snapshot answers.
    Delivery,
    /// Trigger notifications.
    Trigger,
    /// Resync, region route, and error frames.
    Control,
    /// Mutation requests.
    Mutation,
}

impl FrameClass {
    /// The written budget of this class, in wire bytes.
    #[must_use]
    pub fn budget_bytes(self) -> usize {
        match self {
            FrameClass::Session => SESSION_FRAME_BUDGET_BYTES,
            FrameClass::Subscription => SUBSCRIPTION_FRAME_BUDGET_BYTES,
            FrameClass::Acknowledgement => ACK_FRAME_BUDGET_BYTES,
            FrameClass::Heartbeat => HEARTBEAT_FRAME_BUDGET_BYTES,
            FrameClass::Delivery => DELIVERY_FRAME_BUDGET_BYTES,
            FrameClass::Trigger => TRIGGER_FRAME_BUDGET_BYTES,
            FrameClass::Control => CONTROL_FRAME_BUDGET_BYTES,
            FrameClass::Mutation => MUTATION_FRAME_BUDGET_BYTES,
        }
    }

    /// The class's stable name (the conformance vectors record it).
    #[must_use]
    pub fn name(self) -> &'static str {
        match self {
            FrameClass::Session => "session",
            FrameClass::Subscription => "subscription",
            FrameClass::Acknowledgement => "acknowledgement",
            FrameClass::Heartbeat => "heartbeat",
            FrameClass::Delivery => "delivery",
            FrameClass::Trigger => "trigger",
            FrameClass::Control => "control",
            FrameClass::Mutation => "mutation",
        }
    }
}

impl SyncV2Frame {
    /// Whether the frame fits its class's written budget. The single
    /// exception is the flagged single-commit oversize delivery.
    #[must_use]
    pub fn within_class_budget(&self) -> bool {
        let wire = self.to_bytes().len();
        if wire <= self.body().frame_class().budget_bytes() {
            return true;
        }
        matches!(
            self.body(),
            SyncV2Body::DeltaBatch { single_commit_oversize: true, commits: 1, .. }
        )
    }
}

/// The error of the serving glue: a core refusal or a codec refusal.
#[derive(Debug)]
pub enum ServeError {
    /// The sync core refused the request (typed checkpoint errors, base
    /// engine errors).
    Core(SyncCoreError),
    /// The protocol codec refused to seal the answer.
    Protocol(ProtocolError),
}

impl std::fmt::Display for ServeError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            ServeError::Core(error) => write!(f, "the sync core refused: {error}"),
            ServeError::Protocol(error) => write!(f, "the codec refused: {error}"),
        }
    }
}

impl std::error::Error for ServeError {}

impl From<SyncCoreError> for ServeError {
    fn from(error: SyncCoreError) -> Self {
        ServeError::Core(error)
    }
}

impl From<ProtocolError> for ServeError {
    fn from(error: ProtocolError) -> Self {
        ServeError::Protocol(error)
    }
}

/// Serves one delivery answer for a session at `from` as a protocol
/// frame: a delta batch when deltas serve, a resync-required answer
/// when the session is behind the retained floor, and nothing when the
/// session is current (triggers are the pull hint, not a frame).
///
/// # Errors
///
/// Returns [`ServeError::Core`] when the core refuses the checkpoint
/// (foreign peer, ahead of the server, base engine) and
/// [`ServeError::Protocol`] if the answer cannot be sealed.
pub fn serve_delta(
    core: &SyncCore,
    from: &crate::SyncCheckpoint,
    correlation: FrameId,
    causation: FrameId,
) -> Result<Option<SyncV2Frame>, ServeError> {
    match core.delta_since(from)? {
        SyncBatch::UpToDate => Ok(None),
        SyncBatch::BehindRetainedFloor { floor } => {
            let frame = SyncV2Frame::seal(
                SyncV2Body::ResyncRequired { floor, reason: ResyncReason::BehindRetainedFloor },
                correlation,
                causation,
                None,
            )?;
            Ok(Some(frame))
        }
        SyncBatch::Delta { bytes, next, within_budget, commits, single_commit_oversize } => {
            let frame = SyncV2Frame::seal(
                SyncV2Body::DeltaBatch {
                    codec: PayloadCodec::NativeDelta1,
                    payload: bytes,
                    next,
                    within_budget,
                    single_commit_oversize,
                    commits,
                },
                correlation,
                causation,
                None,
            )?;
            Ok(Some(frame))
        }
    }
}

/// Serves a full-state answer (initial sync or forced resync) as a
/// snapshot-answer frame.
///
/// # Errors
///
/// Returns [`ServeError::Core`] when the core cannot export and
/// [`ServeError::Protocol`] if the answer cannot be sealed.
pub fn serve_snapshot(
    core: &SyncCore,
    correlation: FrameId,
    causation: FrameId,
) -> Result<SyncV2Frame, ServeError> {
    let SyncSnapshot { bytes, checkpoint } = core.resync_snapshot()?;
    let frame = SyncV2Frame::seal(
        SyncV2Body::SnapshotAnswer {
            codec: PayloadCodec::NativeSnapshot1,
            payload: bytes,
            checkpoint,
        },
        correlation,
        causation,
        None,
    )?;
    Ok(frame)
}

/// Builds one trigger-notification frame for a published commit: the
/// content-free push hint the client answers by pulling delta batches
/// from its checkpoint (PD-2026-08-26-13 D3).
///
/// # Errors
///
/// Returns [`ProtocolError`] when the identifiers violate the trigger
/// bounds.
pub fn trigger_frame(
    collection: &str,
    storage_sequence: u64,
    operation_id: &str,
    correlation: FrameId,
    causation: FrameId,
) -> Result<SyncV2Frame, ProtocolError> {
    SyncV2Frame::seal(
        SyncV2Body::Trigger {
            collection: collection.to_string(),
            storage_sequence,
            operation_id: operation_id.to_string(),
        },
        correlation,
        causation,
        None,
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_fixed_envelope_matches_a_heartbeat() {
        let frame =
            SyncV2Frame::seal(SyncV2Body::Heartbeat, FrameId::new(1), FrameId::new(0), None)
                .expect("seals");
        assert_eq!(frame.to_bytes().len(), FIXED_ENVELOPE_BYTES);
    }

    #[test]
    fn budget_definitions_cover_their_classes() {
        let heartbeat =
            SyncV2Frame::seal(SyncV2Body::Heartbeat, FrameId::new(1), FrameId::new(0), None)
                .expect("seals");
        assert!(heartbeat.to_bytes().len() <= HEARTBEAT_FRAME_BUDGET_BYTES);
        // The worst-case trigger: 64-byte collection and operation ids.
        let worst = trigger_frame(
            &"c".repeat(64),
            u64::MAX,
            &"o".repeat(64),
            FrameId::new(1),
            FrameId::new(0),
        )
        .expect("seals");
        assert!(worst.to_bytes().len() <= TRIGGER_FRAME_BUDGET_BYTES);
        assert!(DELIVERY_FRAME_BUDGET_BYTES >= crate::SyncCoreConfig::default().batch_byte_budget);
    }
}
