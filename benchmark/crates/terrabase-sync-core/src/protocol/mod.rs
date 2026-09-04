//! The `terrascale.sync.v1` client-channel wire protocol: the
//! versioned frame vocabulary spoken between client Sync Sessions and
//! the serving host.
//!
//! Per S-04 (decision D-38) the CRDT-era frame kinds are deleted with
//! the per-field materialization they carried: no delta-batch or
//! snapshot-answer frame (their payloads were `terrabase-native/1`
//! CRDT encodings), no checkpoint-carrying session or resync frames
//! (their checkpoints were CRDT vector-clock ends), and no mutation
//! frames (their write model was the per-field scalar document). What
//! survives is the envelope discipline — version, frame kind,
//! correlation/causation identity, idempotency pairing, content
//! digest, fail-closed decode — and the frame kinds that never carried
//! CRDT state: session setup and acceptance, subscription modification,
//! heartbeats, content-free trigger notifications, region route
//! changes, and the closed error vocabulary. The delivery vocabulary
//! over result views is S-04's to rebuild on the [`crate::native`]
//! state machine.
//!
//! Scope limits, stated exactly:
//!
//! - This is the client-channel protocol only. TBEP (SE-14) is a
//!   separate process-to-process protocol that *reuses this envelope
//!   discipline* — version, frame type, correlation/causation,
//!   idempotency key, content digest, fail closed — not these types.
//! - Trigger notifications (PD-2026-08-26-13 D3) are content-free: a
//!   collection scope plus the two commit identifiers. No document
//!   identity, no field names, no values.
//! - The budget constants below are written **definitions** (the
//!   contract each frame class is held to). No measurement made here
//!   is a claim.

mod body;
mod frame;

pub use body::ErrorCode;
pub use body::FrameKind;
pub use body::SyncV1Body;
pub use frame::IdempotencyKey;
pub use frame::ProtocolError;
pub use frame::SyncV1Frame;

use crate::FrameId;

/// The contract identity of the protocol.
pub const SYNC_PROTOCOL_CONTRACT: &str = "terrascale.sync.v1";

/// The wire version this module speaks. The leading byte of every
/// frame; every other value fails closed on decode — including `1`,
/// the v1 wire this contract deliberately does not speak (no V1 wire
/// compatibility; charter PD-2026-08-26-10).
pub const SYNC_PROTOCOL_VERSION: u8 = 1;

/// The fixed per-frame envelope overhead in bytes: version (1), kind
/// (2), correlation (8), causation (8), idempotency presence (1),
/// digest (8), body length (8), and the body's kind tag (2). A
/// heartbeat frame — the zero-field body — encodes to exactly this
/// many bytes.
pub const FIXED_ENVELOPE_BYTES: usize = 38;

/// The largest frame the decoder accepts. Larger inputs are rejected
/// before any field is parsed.
pub const MAX_FRAME_BYTES: usize = 16 * 1024 * 1024;

/// The largest encoded body one frame may carry (matches the decode
/// bound).
pub const MAX_PAYLOAD_BYTES: usize = MAX_FRAME_BYTES - FIXED_ENVELOPE_BYTES;

/// The longest idempotency key (bytes).
pub const MAX_KEY_BYTES: usize = 128;

// --- Bytes-on-wire budget definitions (PD-2026-08-26-13 D1) ---------------
//
// Written definitions: each frame class carries a budget its frames
// must fit. Nothing here is a measured result.

/// The trigger-notification budget: the total wire size one
/// content-free trigger may occupy. Typical frames are ~64 bytes; the
/// budget bounds the worst-case identifier lengths (64 + 64 bytes).
pub const TRIGGER_FRAME_BUDGET_BYTES: usize = 256;

/// The heartbeat budget: the fixed envelope and nothing more.
pub const HEARTBEAT_FRAME_BUDGET_BYTES: usize = 64;

/// The subscription budget (modify requests and confirmations).
pub const SUBSCRIPTION_FRAME_BUDGET_BYTES: usize = 2048;

/// The session budget (setup and acceptance).
pub const SESSION_FRAME_BUDGET_BYTES: usize = 4096;

/// The control budget (region route changes, errors).
pub const CONTROL_FRAME_BUDGET_BYTES: usize = 256;

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

impl SyncV1Body {
    /// The direction this frame travels on the client channel.
    #[must_use]
    pub fn direction(&self) -> Direction {
        match self {
            SyncV1Body::SessionSetup { .. } | SyncV1Body::SubscriptionModify { .. } => {
                Direction::ClientToServer
            }
            SyncV1Body::SessionAccepted { .. }
            | SyncV1Body::SubscriptionChanged { .. }
            | SyncV1Body::Trigger { .. }
            | SyncV1Body::RegionRouteChange { .. }
            | SyncV1Body::Error { .. } => Direction::ServerToClient,
            SyncV1Body::Heartbeat => Direction::Both,
        }
    }

    /// The frame class whose written budget governs this frame.
    #[must_use]
    pub fn frame_class(&self) -> FrameClass {
        match self {
            SyncV1Body::SessionSetup { .. } | SyncV1Body::SessionAccepted { .. } => {
                FrameClass::Session
            }
            SyncV1Body::SubscriptionModify { .. } | SyncV1Body::SubscriptionChanged { .. } => {
                FrameClass::Subscription
            }
            SyncV1Body::Heartbeat => FrameClass::Heartbeat,
            SyncV1Body::Trigger { .. } => FrameClass::Trigger,
            SyncV1Body::RegionRouteChange { .. } | SyncV1Body::Error { .. } => FrameClass::Control,
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
    /// Heartbeats.
    Heartbeat,
    /// Trigger notifications.
    Trigger,
    /// Region route and error frames.
    Control,
}

impl FrameClass {
    /// The written budget of this class, in wire bytes.
    #[must_use]
    pub fn budget_bytes(self) -> usize {
        match self {
            FrameClass::Session => SESSION_FRAME_BUDGET_BYTES,
            FrameClass::Subscription => SUBSCRIPTION_FRAME_BUDGET_BYTES,
            FrameClass::Heartbeat => HEARTBEAT_FRAME_BUDGET_BYTES,
            FrameClass::Trigger => TRIGGER_FRAME_BUDGET_BYTES,
            FrameClass::Control => CONTROL_FRAME_BUDGET_BYTES,
        }
    }

    /// The class's stable name (the conformance vectors record it).
    #[must_use]
    pub fn name(self) -> &'static str {
        match self {
            FrameClass::Session => "session",
            FrameClass::Subscription => "subscription",
            FrameClass::Heartbeat => "heartbeat",
            FrameClass::Trigger => "trigger",
            FrameClass::Control => "control",
        }
    }
}

impl SyncV1Frame {
    /// Whether the frame fits its class's written budget.
    #[must_use]
    pub fn within_class_budget(&self) -> bool {
        self.to_bytes().len() <= self.body().frame_class().budget_bytes()
    }
}

/// Builds one trigger-notification frame for a published commit: the
/// content-free push hint the client answers by pulling its result-view
/// revisions (PD-2026-08-26-13 D3).
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
) -> Result<SyncV1Frame, ProtocolError> {
    SyncV1Frame::seal(
        SyncV1Body::Trigger {
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
            SyncV1Frame::seal(SyncV1Body::Heartbeat, FrameId::new(1), FrameId::new(0), None)
                .expect("seals");
        assert_eq!(frame.to_bytes().len(), FIXED_ENVELOPE_BYTES);
    }

    #[test]
    fn budget_definitions_cover_their_classes() {
        let heartbeat =
            SyncV1Frame::seal(SyncV1Body::Heartbeat, FrameId::new(1), FrameId::new(0), None)
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
    }
}
