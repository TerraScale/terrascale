//! Crate-private deterministic transport doubles for the replication data
//! plane.
//!
//! This module never crosses the public seam: it is the controlled in-process
//! link set the W4 qualification lane substitutes for a real network. The
//! engine is synchronous and runtime-neutral, so there are no threads, no
//! async runtimes, and no timers here. Time is driven explicitly: a frame
//! sent while a direction's `delay` control is armed is held back in an
//! ordered queue and reaches its destination only when the caller pumps the
//! network; `reorder` releases the held frames newest-first; `duplicate`
//! delivers every frame twice; `drop_next` loses a bounded number of frames
//! in transit; `partition` loses everything on the direction.
//!
//! Every queue is bounded: a frame offered beyond the bound is rejected with
//! a typed error at send time, and a frame that would overflow a release is
//! counted and dropped, never buffered without bound. All fault effects are
//! observable through the per-direction counters, so a test can prove its
//! fault actually fired.

use std::collections::{BTreeMap, VecDeque};

use super::retention::DeletionEvidence;
use super::{MemberId, ReplicationClockStamp, ReplicationInstant};
use crate::storage::PublishedCommit;
use crate::types::{StorageSequence, TenantId};

/// The maximum number of frames one direction may hold in each queue.
///
/// The bound keeps the double's memory bounded: a hold-back or delivery
/// queue that would exceed it rejects or counts-and-drops the frame instead
/// of growing.
#[allow(
    dead_code,
    reason = "driven and observed by the W4-REPLICATION R-1 lane tests; the production acquisition seam lands with R-2/R-3"
)]
pub(crate) const MAX_LINK_FRAMES: usize = 64;

/// One committed operation paired with its writer's hybrid logical clock
/// stamp.
///
/// The stamp is the D-3 ordering fact of the write: a follower compares it
/// against its own key state to decide last-writer-wins. History the server
/// no longer has a stamp for (for example after a restart) is served at the
/// server's current clock, which the implementation record states as a
/// limit.
#[derive(Clone, Debug)]
pub(crate) struct StampedCommit {
    /// The writer's clock stamp of the commit.
    pub(crate) stamp: ReplicationClockStamp,
    /// The reconstructable published commit.
    pub(crate) commit: PublishedCommit,
}

impl StampedCommit {
    /// Pairs one commit with an explicit stamp.
    pub(crate) fn new(stamp: ReplicationClockStamp, commit: PublishedCommit) -> Self {
        Self { stamp, commit }
    }

    /// Pairs one commit with the zero stamp, the lane's origin clock.
    ///
    /// Fixtures use this for deliveries whose ordering fact is not under
    /// test.
    #[allow(dead_code, reason = "used by the W4-REPLICATION lane fixtures")]
    pub(crate) fn at_origin_clock(commit: PublishedCommit) -> Self {
        Self { stamp: ReplicationClockStamp::new(0, 0), commit }
    }
}

/// One replication frame crossing a controlled link.
///
/// A frame carries only what the catch-up and push protocols need:
/// identities, the tenant, sequence bounds, ownership epochs, clock stamps,
/// and the reconstructable committed write sets the publication journal
/// serves. It is crate-private by design and never crosses the public seam.
#[derive(Clone, Debug)]
#[allow(
    dead_code,
    reason = "driven and observed by the W4-REPLICATION R-1 lane tests; the production acquisition seam lands with R-2/R-3"
)]
pub(crate) enum ReplicationFrame {
    /// One member asks another for a tenant's published commits.
    CatchUpRequest {
        /// The member requesting the history.
        requester: MemberId,
        /// The tenant whose history is requested.
        tenant: TenantId,
        /// The requester's progress in the source's sequence space, or
        /// `None` for a full scan from the beginning.
        after: Option<StorageSequence>,
        /// The requester's bounded batch size.
        limit: u32,
    },
    /// One member serves a tenant's published commits.
    CatchUpBatch {
        /// The member serving the history.
        source: MemberId,
        /// The tenant the batch belongs to.
        tenant: TenantId,
        /// The served commits with their writer clock stamps, in global
        /// storage-sequence order.
        commits: Vec<StampedCommit>,
        /// The highest source sequence this batch serves.
        through: Option<StorageSequence>,
        /// The source's latest recorded sequence for this tenant.
        tenant_high_water: Option<StorageSequence>,
        /// The oldest sequence the source's journal can serve.
        servable_from: Option<StorageSequence>,
        /// The requester's cursor position the source cannot serve below;
        /// `None` when the served range is complete.
        gap_below: Option<StorageSequence>,
    },
    /// One member pushes its just-committed operations to a peer.
    ///
    /// The push is the leader fan-out of the D-1 data plane: it carries the
    /// ownership epoch of the pushed range, so a follower that has seen a
    /// higher epoch fences the batch (a deposed leader's raced write is
    /// retained as conflict history, never silently dropped).
    Push {
        /// The member pushing the operations.
        source: MemberId,
        /// The data range the pushed operations were admitted under.
        range: String,
        /// The ownership epoch of that range at the pushing member's view.
        epoch: u64,
        /// The tenant the pushed commits belong to.
        tenant: TenantId,
        /// The pushed commits with their writer clock stamps.
        commits: Vec<StampedCommit>,
    },
    /// One member serves the deletion evidence a requester below its
    /// history bound still needs (GAP-R02).
    ///
    /// The batch is the tombstone half of a below-bound catch-up: the
    /// retained facts above the requester's cursor, the bound they were
    /// retained against, the retention floor they are held above, and the
    /// tenant's highest non-deletion sequence below the bound — the fact
    /// that tells the requester whether evidence alone completes its
    /// catch-up or whether it must fall back to a full state transfer.
    DeletionEvidenceBatch {
        /// The member serving the evidence.
        source: MemberId,
        /// The tenant the evidence belongs to.
        tenant: TenantId,
        /// The retained deletion facts above the requester's cursor, in
        /// sequence order.
        evidence: Vec<DeletionEvidence>,
        /// The history bound the evidence was retained below.
        servable_from: StorageSequence,
        /// The source's retention floor.
        retention_floor: StorageSequence,
        /// The tenant's highest non-deletion sequence below the bound, or
        /// `None` when every publication below the bound was a delete.
        non_deletion_through: Option<StorageSequence>,
    },
}

/// Which frames a receive drains from a member's inbound links.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
#[allow(
    dead_code,
    reason = "driven and observed by the W4-REPLICATION R-1 lane tests; the production acquisition seam lands with R-2/R-3"
)]
pub(crate) enum FrameSelector {
    /// Drain catch-up requests, leaving deliveries in place.
    Requests,
    /// Drain catch-up deliveries and pushed commits, leaving requests in
    /// place.
    Deliveries,
}

#[allow(
    dead_code,
    reason = "driven and observed by the W4-REPLICATION R-1 lane tests; the production acquisition seam lands with R-2/R-3"
)]
impl FrameSelector {
    /// Reports whether one frame matches this selector.
    fn matches(self, frame: &ReplicationFrame) -> bool {
        matches!(
            (self, frame),
            (Self::Requests, ReplicationFrame::CatchUpRequest { .. })
                | (
                    Self::Deliveries,
                    ReplicationFrame::CatchUpBatch { .. }
                        | ReplicationFrame::Push { .. }
                        | ReplicationFrame::DeletionEvidenceBatch { .. }
                )
        )
    }
}

/// The per-direction fault controls of one controlled link.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
#[allow(
    clippy::struct_excessive_bools,
    reason = "independent fault toggles are the double's control surface"
)]
#[allow(
    dead_code,
    reason = "driven and observed by the W4-REPLICATION R-1 lane tests; the production acquisition seam lands with R-2/R-3"
)]
struct LinkFaults {
    /// Hold sent frames back for this many pumps; zero delivers immediately.
    delay_pumps: u32,
    /// Deliver every frame twice.
    duplicate: bool,
    /// Release held frames newest-first.
    reorder: bool,
    /// Lose the next frames in transit, counted observably.
    drop_next: Option<u32>,
    /// Lose every frame on this direction.
    partition: bool,
}

/// One directed link between two members.
#[allow(
    dead_code,
    reason = "driven and observed by the W4-REPLICATION R-1 lane tests; the production acquisition seam lands with R-2/R-3"
)]
pub(crate) struct DirectedLink {
    to: MemberId,
    faults: LinkFaults,
    /// The ordered hold-back queue of a delayed direction: each entry waits
    /// for the lane tick named with it.
    held: VecDeque<(u64, ReplicationFrame)>,
    /// The frames waiting to be received.
    inbound: VecDeque<ReplicationFrame>,
    dropped: u64,
    overflowed: u64,
    delivered: u64,
    reordered: u64,
}

#[allow(
    dead_code,
    reason = "driven and observed by the W4-REPLICATION R-1 lane tests; the production acquisition seam lands with R-2/R-3"
)]
impl DirectedLink {
    /// Arms the delay control: frames sent on this direction are held back
    /// until the next pump releases them.
    pub(crate) fn delay(&mut self) {
        self.faults.delay_pumps = 1;
    }

    /// Arms the delay control for several pumps: frames sent on this
    /// direction are held back until the `pumps`-th pump after their send.
    ///
    /// Delay is an instant-queue model of latency, so a longer hold models a
    /// longer delay — never a timer, and never wall time.
    pub(crate) fn delay_pumps(&mut self, pumps: u32) {
        self.faults.delay_pumps = pumps;
    }

    /// Arms the duplicate control: every frame delivered on this direction
    /// is delivered twice.
    pub(crate) fn duplicate(&mut self) {
        self.faults.duplicate = true;
    }

    /// Arms the reorder control: a pump releases this direction's held
    /// frames newest-first.
    pub(crate) fn reorder(&mut self) {
        self.faults.reorder = true;
    }

    /// Arms the drop control: the next `frames` frames sent on this
    /// direction are lost in transit.
    pub(crate) fn drop_next(&mut self, frames: u32) {
        self.faults.drop_next = Some(frames);
    }

    /// Arms the full partition: every frame sent on this direction is lost.
    pub(crate) fn partition(&mut self) {
        self.faults.partition = true;
    }

    /// Clears every fault control on this direction.
    pub(crate) fn clear_faults(&mut self) {
        self.faults = LinkFaults::default();
    }

    /// Returns the number of frames lost on this direction.
    pub(crate) fn dropped(&self) -> u64 {
        self.dropped
    }

    /// Returns the number of frames this direction dropped because a queue
    /// was at its bound.
    pub(crate) fn overflowed(&self) -> u64 {
        self.overflowed
    }

    /// Returns the number of frames this direction delivered.
    pub(crate) fn delivered(&self) -> u64 {
        self.delivered
    }

    /// Returns the number of frames a pump delivered out of send order on
    /// this direction.
    pub(crate) fn reordered(&self) -> u64 {
        self.reordered
    }

    /// Returns the number of frames currently held back on this direction.
    pub(crate) fn held(&self) -> usize {
        self.held.len()
    }

    /// Returns the number of frames waiting to be received on this
    /// direction.
    pub(crate) fn queued(&self) -> usize {
        self.inbound.len()
    }

    /// Accepts one frame onto the direction, applying the ingress faults.
    fn accept(&mut self, frame: ReplicationFrame, now: u64) -> Result<(), TransportError> {
        if self.faults.partition {
            self.dropped += 1;
            return Ok(());
        }
        if let Some(remaining) = self.faults.drop_next {
            if remaining > 0 {
                self.faults.drop_next = Some(remaining - 1);
                self.dropped += 1;
                return Ok(());
            }
            self.faults.drop_next = None;
        }
        if self.faults.delay_pumps > 0 {
            if self.held.len() >= MAX_LINK_FRAMES {
                self.overflowed += 1;
                return Err(TransportError::LinkFull);
            }
            self.held.push_back((now + u64::from(self.faults.delay_pumps), frame));
            return Ok(());
        }
        self.deliver(&frame);
        Ok(())
    }

    /// Places one frame in the delivery position, applying the duplicate
    /// control.
    fn deliver(&mut self, frame: &ReplicationFrame) {
        let copies = if self.faults.duplicate { 2 } else { 1 };
        for _ in 0..copies {
            if self.inbound.len() >= MAX_LINK_FRAMES {
                self.overflowed += 1;
                continue;
            }
            self.inbound.push_back(frame.clone());
            self.delivered += 1;
        }
    }

    /// Releases the held frames whose release tick has arrived.
    fn release(&mut self, now: u64) {
        if self.held.is_empty() {
            return;
        }
        let due: Vec<ReplicationFrame> =
            self.held.iter().filter(|(at, _)| *at <= now).map(|(_, f)| f.clone()).collect();
        self.held.retain(|(at, _)| *at > now);
        let order: Vec<ReplicationFrame> =
            if self.faults.reorder { due.into_iter().rev().collect() } else { due };
        let before = self.delivered;
        for frame in order {
            self.deliver(&frame);
        }
        if self.faults.reorder && self.delivered > before {
            self.reordered += self.delivered - before;
        }
    }
}

/// A bounded set of controlled in-process links between members.
///
/// The network is also the lane clock: it advances exactly one tick per
/// [`ReplicationNetwork::pump`], and every deadline, timeout bound, and
/// observation instant is a value of that clock. There is no wall time
/// anywhere in the seam.
#[allow(
    dead_code,
    reason = "driven and observed by the W4-REPLICATION R-1 lane tests; the production acquisition seam lands with R-2/R-3"
)]
#[derive(Default)]
pub(crate) struct ReplicationNetwork {
    links: BTreeMap<(MemberId, MemberId), DirectedLink>,
    clock: u64,
}

#[allow(
    dead_code,
    reason = "driven and observed by the W4-REPLICATION R-1 lane tests; the production acquisition seam lands with R-2/R-3"
)]
impl ReplicationNetwork {
    /// Creates an empty network with no links.
    pub(crate) fn new() -> Self {
        Self::default()
    }

    /// Returns the directed link from `from` to `to`, creating it as a
    /// healthy link on first use.
    pub(crate) fn link(&mut self, from: &MemberId, to: &MemberId) -> &mut DirectedLink {
        self.links.entry((from.clone(), to.clone())).or_insert_with(|| DirectedLink {
            to: to.clone(),
            faults: LinkFaults::default(),
            held: VecDeque::new(),
            inbound: VecDeque::new(),
            dropped: 0,
            overflowed: 0,
            delivered: 0,
            reordered: 0,
        })
    }

    /// Sends one frame from `from` to `to` through the controlled link.
    ///
    /// # Errors
    ///
    /// Returns [`TransportError::LinkFull`] when the direction is at its
    /// frame bound; a frame lost under a partition or drop control is not an
    /// error — it is counted and observable.
    pub(crate) fn send(
        &mut self,
        from: &MemberId,
        to: &MemberId,
        frame: ReplicationFrame,
    ) -> Result<(), TransportError> {
        let now = self.clock;
        self.link(from, to).accept(frame, now)
    }

    /// Advances the lane clock by one tick and releases every hold-back that
    /// came due.
    ///
    /// Time moves only here: held frames are released in send order
    /// (newest-first under the reorder control) and duplicated under the
    /// duplicate control.
    pub(crate) fn pump(&mut self) {
        self.clock = self.clock.saturating_add(1);
        let now = self.clock;
        for link in self.links.values_mut() {
            link.release(now);
        }
    }

    /// Returns the current instant of the lane clock.
    pub(crate) fn now(&self) -> ReplicationInstant {
        ReplicationInstant::at_ticks(self.clock)
    }

    /// Drains the frames addressed to `member` that match `selector`,
    /// leaving every other frame in place.
    ///
    /// Links are visited in canonical order, so the drain order is
    /// deterministic.
    pub(crate) fn receive(
        &mut self,
        member: &MemberId,
        selector: FrameSelector,
    ) -> Vec<ReplicationFrame> {
        let mut frames = Vec::new();
        for link in self.links.values_mut() {
            if link.to != *member {
                continue;
            }
            let mut retained = VecDeque::new();
            while let Some(frame) = link.inbound.pop_front() {
                if selector.matches(&frame) {
                    frames.push(frame);
                } else {
                    retained.push_back(frame);
                }
            }
            link.inbound = retained;
        }
        frames
    }

    /// Returns the number of frames waiting to be received by `member`.
    pub(crate) fn pending(&self, member: &MemberId) -> usize {
        self.links.values().filter(|link| link.to == *member).map(DirectedLink::queued).sum()
    }
}

/// Typed transport failure of a controlled link.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
#[allow(
    dead_code,
    reason = "driven and observed by the W4-REPLICATION R-1 lane tests; the production acquisition seam lands with R-2/R-3"
)]
pub(crate) enum TransportError {
    /// The direction was at its bounded frame capacity.
    LinkFull,
}
