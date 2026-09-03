//! The engine-owned member-transport seam and its fault vocabulary.
//!
//! One [`MemberTransport`] moves encoded member envelopes between Members on
//! engine-owned lanes — no broker between members (PD-2026-08-26-13 D2):
//! the in-process lane where Members share a process boundary, and the
//! engine-socket lane (QUIC via `quinn`) for networked links. Both lanes
//! carry the W4 fault vocabulary as per-direction controls with observable
//! counters, so a lane test can prove its fault actually fired on real
//! frames, and both count bytes on wire so the PD-13 D1 budgets are measured
//! from real deliveries.
//!
//! The lane clock lives here: it advances exactly one tick per
//! [`MemberTransport::pump`], and every deadline, expiry, and observation
//! instant is a value of that clock.

use crate::clock::LaneInstant;
use crate::envelope::{EncodedFrame, FrameKind};
use crate::identity::MemberId;

/// The maximum number of frames one direction may hold in each queue. A
/// frame offered beyond the bound is rejected with a typed error at send
/// time; a frame that would overflow a release is counted and dropped, never
/// buffered without bound.
pub const MAX_LINK_FRAMES: usize = 64;

/// Which frames a receive drains from a Member's inbound links.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum FrameSelector {
    /// Drain every frame addressed to the Member.
    All,
    /// Drain catch-up requests, leaving deliveries and reports in place.
    Requests,
    /// Drain catch-up batches, pushed commits, and replicated-record
    /// batches, leaving requests in place.
    Deliveries,
    /// Drain acknowledgement reports, leaving everything else in place.
    Reports,
}

impl FrameSelector {
    /// Reports whether one frame kind matches this selector.
    #[must_use]
    pub const fn matches(self, kind: FrameKind) -> bool {
        matches!(
            (self, kind),
            (Self::All, _)
                | (Self::Requests, FrameKind::CatchUpRequest)
                | (Self::Deliveries, FrameKind::CatchUpBatch | FrameKind::Push)
                | (Self::Reports, FrameKind::AckReport | FrameKind::AckBatch)
        ) || matches!((self, kind), (Self::Deliveries, FrameKind::ReplicationBatch))
    }
}

/// The per-direction fault controls of one directed link, carrying the W4
/// fault vocabulary over real lanes.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
#[allow(
    clippy::struct_excessive_bools,
    reason = "independent fault toggles are the control surface"
)]
pub struct LinkControls {
    /// Hold sent frames back for this many pumps; zero delivers immediately.
    pub delay_pumps: u32,
    /// Deliver every frame twice.
    pub duplicate: bool,
    /// Release held frames newest-first.
    pub reorder: bool,
    /// Lose the next frames in transit, counted observably.
    pub drop_next: Option<u32>,
    /// Lose every frame on this direction.
    pub partition: bool,
}

impl LinkControls {
    /// Arms the one-pump delay control.
    pub fn delay(&mut self) {
        self.delay_pumps = 1;
    }

    /// Arms the delay control for several pumps.
    pub fn delay_pumps(&mut self, pumps: u32) {
        self.delay_pumps = pumps;
    }

    /// Arms the duplicate control.
    pub fn duplicate(&mut self) {
        self.duplicate = true;
    }

    /// Arms the reorder control.
    pub fn reorder(&mut self) {
        self.reorder = true;
    }

    /// Arms the bounded drop control.
    pub fn drop_next(&mut self, frames: u32) {
        self.drop_next = Some(frames);
    }

    /// Arms the full partition.
    pub fn partition(&mut self) {
        self.partition = true;
    }

    /// Clears every fault control.
    pub fn clear(&mut self) {
        *self = Self::default();
    }
}

/// The observable counters of one directed link.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct LinkStats {
    /// Frames lost on this direction (partition and bounded drops).
    pub dropped: u64,
    /// Frames this direction dropped because a queue was at its bound.
    pub overflowed: u64,
    /// Frames delivered on this direction.
    pub delivered: u64,
    /// Frames a pump delivered out of send order on this direction.
    pub reordered: u64,
    /// Frames currently held back on this direction.
    pub held: u64,
    /// Frames waiting to be received on this direction.
    pub queued: u64,
    /// Encoded envelope bytes delivered on this direction (payload plus
    /// envelope overhead — the bytes-on-wire fact of PD-13 D1).
    pub bytes_delivered: u64,
    /// Payload bytes delivered on this direction.
    pub payload_bytes_delivered: u64,
}

/// The measured bytes-on-wire facts of one frame class, maintained by every
/// lane at its delivery boundary (PD-2026-08-26-13 D1).
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct FrameClassBytes {
    /// The frames delivered.
    pub frames: u64,
    /// The payload bytes delivered.
    pub payload_bytes: u64,
    /// The total encoded bytes delivered.
    pub total_bytes: u64,
}

impl FrameClassBytes {
    /// Adds one delivered frame's measured lengths.
    pub fn record(&mut self, total: usize, payload: usize) {
        self.frames += 1;
        self.total_bytes += total as u64;
        self.payload_bytes += payload as u64;
    }

    /// Returns the average envelope overhead per delivered frame, rounded
    /// down; zero when no frame was delivered.
    #[must_use]
    pub fn average_overhead_bytes(&self) -> u64 {
        if self.frames == 0 {
            return 0;
        }
        (self.total_bytes - self.payload_bytes) / self.frames
    }

    /// Returns the average payload bytes per delivered frame, rounded down;
    /// zero when no frame was delivered.
    #[must_use]
    pub fn average_payload_bytes(&self) -> u64 {
        if self.frames == 0 {
            return 0;
        }
        self.payload_bytes / self.frames
    }
}

/// Typed transport failure of a controlled link.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum TransportError {
    /// The direction was at its bounded frame capacity.
    LinkFull,
}

/// The engine-owned member-transport seam.
pub trait MemberTransport {
    /// Returns the mutable fault controls of the directed link `from` to
    /// `to`, creating the link as healthy on first use.
    fn link(&mut self, from: &MemberId, to: &MemberId) -> &mut LinkControls;

    /// Returns the observable counters of the directed link `from` to `to`.
    fn link_stats(&self, from: &MemberId, to: &MemberId) -> LinkStats;

    /// Sends one encoded frame from `from` to `to` through the link.
    ///
    /// # Errors
    ///
    /// Returns [`TransportError::LinkFull`] when the direction is at its
    /// frame bound; a frame lost under a partition or drop control is not an
    /// error — it is counted and observable.
    fn send(
        &mut self,
        from: &MemberId,
        to: &MemberId,
        frame: EncodedFrame,
    ) -> Result<(), TransportError>;

    /// Advances the lane clock by one tick and releases every hold-back that
    /// came due, driving the underlying lane (for the socket lane: flushing
    /// pending sends and draining received frames until every flushed frame
    /// has arrived — a causal condition, never an elapsed-time one).
    fn pump(&mut self);

    /// Returns the current instant of the lane clock.
    fn now(&self) -> LaneInstant;

    /// Drains the frames addressed to `member` that match `selector`,
    /// leaving every other frame in place, in deterministic order.
    fn receive(&mut self, member: &MemberId, selector: FrameSelector) -> Vec<EncodedFrame>;

    /// Returns the number of frames waiting to be received by `member`.
    fn pending(&self, member: &MemberId) -> usize;

    /// Returns the per-frame-class bytes-on-wire counters this lane
    /// maintained at its delivery boundary, in canonical kind order.
    fn class_bytes(&self) -> Vec<(FrameKind, FrameClassBytes)>;

    /// Clears every fault control on every direction (healing).
    fn heal(&mut self);
}
