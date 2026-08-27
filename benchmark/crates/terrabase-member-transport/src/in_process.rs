//! The in-process member lane: Members that share a process boundary.
//!
//! This is the W4 controlled in-process link set re-incarnated over real
//! encoded envelopes: bounded byte queues replace the moved enum values, so
//! the codec's fail-closed discipline is exercised on every delivery and the
//! per-direction counters measure real bytes on wire. Deterministic
//! throughout: the only clock is the lane clock (one tick per pump), and
//! every fault is an armed link control. No threads, no async runtimes, no
//! sleeps, no wall time anywhere in this lane.

use std::collections::{BTreeMap, VecDeque};

use crate::clock::LaneInstant;
use crate::envelope::{EncodedFrame, FrameKind};
use crate::identity::MemberId;
use crate::transport::{
    FrameClassBytes, FrameSelector, LinkControls, LinkStats, MAX_LINK_FRAMES, MemberTransport,
    TransportError,
};

/// The per-frame-class byte counters every lane maintains at its delivery
/// boundary.
pub(crate) type ClassBytes = BTreeMap<FrameKind, FrameClassBytes>;

/// One directed link between two Members over encoded frames.
struct DirectedLink {
    to: MemberId,
    controls: LinkControls,
    held: VecDeque<(u64, EncodedFrame)>,
    inbound: VecDeque<EncodedFrame>,
    stats: LinkStats,
}

impl DirectedLink {
    fn new(to: MemberId) -> Self {
        Self {
            to,
            controls: LinkControls::default(),
            held: VecDeque::new(),
            inbound: VecDeque::new(),
            stats: LinkStats::default(),
        }
    }

    fn stats(&self) -> LinkStats {
        LinkStats { held: self.held.len() as u64, queued: self.inbound.len() as u64, ..self.stats }
    }

    /// Accepts one frame onto the direction, applying the ingress faults.
    fn accept(
        &mut self,
        frame: EncodedFrame,
        now: u64,
        class_bytes: &mut ClassBytes,
    ) -> Result<(), TransportError> {
        if self.controls.partition {
            self.stats.dropped += 1;
            return Ok(());
        }
        if let Some(remaining) = self.controls.drop_next {
            if remaining > 0 {
                self.controls.drop_next = Some(remaining - 1);
                self.stats.dropped += 1;
                return Ok(());
            }
            self.controls.drop_next = None;
        }
        if self.controls.delay_pumps > 0 {
            if self.held.len() >= MAX_LINK_FRAMES {
                self.stats.overflowed += 1;
                return Err(TransportError::LinkFull);
            }
            self.held.push_back((now + u64::from(self.controls.delay_pumps), frame));
            return Ok(());
        }
        self.deliver(&frame, class_bytes);
        Ok(())
    }

    /// Places one frame in the delivery position, applying the duplicate
    /// control.
    fn deliver(&mut self, frame: &EncodedFrame, class_bytes: &mut ClassBytes) {
        let copies = if self.controls.duplicate { 2 } else { 1 };
        for _ in 0..copies {
            if self.inbound.len() >= MAX_LINK_FRAMES {
                self.stats.overflowed += 1;
                continue;
            }
            if let Ok(kind) = frame.peek_kind() {
                class_bytes.entry(kind).or_default().record(frame.bytes.len(), frame.payload_len);
            }
            self.stats.bytes_delivered += frame.bytes.len() as u64;
            self.stats.payload_bytes_delivered += frame.payload_len as u64;
            self.inbound.push_back(frame.clone());
            self.stats.delivered += 1;
        }
    }

    /// Releases the held frames whose release tick has arrived.
    fn release(&mut self, now: u64, class_bytes: &mut ClassBytes) {
        if self.held.is_empty() {
            return;
        }
        let due: Vec<EncodedFrame> =
            self.held.iter().filter(|(at, _)| *at <= now).map(|(_, frame)| frame.clone()).collect();
        self.held.retain(|(at, _)| *at > now);
        let order: Vec<EncodedFrame> =
            if self.controls.reorder { due.into_iter().rev().collect() } else { due };
        let before = self.stats.delivered;
        for frame in order {
            self.deliver(&frame, class_bytes);
        }
        if self.controls.reorder && self.stats.delivered > before {
            self.stats.reordered += self.stats.delivered - before;
        }
    }
}

/// A bounded set of controlled in-process links between Members. The network
/// is also the lane clock: it advances exactly one tick per
/// [`InProcessLane::pump`].
#[derive(Default)]
pub struct InProcessLane {
    links: BTreeMap<(MemberId, MemberId), DirectedLink>,
    clock: u64,
    class_bytes: ClassBytes,
}

impl InProcessLane {
    /// Creates an empty lane with no links.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }
}

impl MemberTransport for InProcessLane {
    fn link(&mut self, from: &MemberId, to: &MemberId) -> &mut LinkControls {
        &mut self
            .links
            .entry((from.clone(), to.clone()))
            .or_insert_with(|| DirectedLink::new(to.clone()))
            .controls
    }

    fn link_stats(&self, from: &MemberId, to: &MemberId) -> LinkStats {
        self.links
            .get(&(from.clone(), to.clone()))
            .map_or_else(LinkStats::default, DirectedLink::stats)
    }

    fn send(
        &mut self,
        from: &MemberId,
        to: &MemberId,
        frame: EncodedFrame,
    ) -> Result<(), TransportError> {
        let now = self.clock;
        let class_bytes = &mut self.class_bytes;
        self.links
            .entry((from.clone(), to.clone()))
            .or_insert_with(|| DirectedLink::new(to.clone()))
            .accept(frame, now, class_bytes)
    }

    fn pump(&mut self) {
        self.clock = self.clock.saturating_add(1);
        let now = self.clock;
        let class_bytes = &mut self.class_bytes;
        for link in self.links.values_mut() {
            link.release(now, class_bytes);
        }
    }

    fn now(&self) -> LaneInstant {
        LaneInstant::at_ticks(self.clock)
    }

    fn receive(&mut self, member: &MemberId, selector: FrameSelector) -> Vec<EncodedFrame> {
        let mut frames = Vec::new();
        for link in self.links.values_mut() {
            if &link.to != member {
                continue;
            }
            let mut retained = VecDeque::new();
            while let Some(frame) = link.inbound.pop_front() {
                let matches = frame.peek_kind().is_ok_and(|kind| selector.matches(kind));
                if matches {
                    frames.push(frame);
                } else {
                    retained.push_back(frame);
                }
            }
            link.inbound = retained;
        }
        frames
    }

    fn pending(&self, member: &MemberId) -> usize {
        self.links.values().filter(|link| &link.to == member).map(|link| link.inbound.len()).sum()
    }

    fn class_bytes(&self) -> Vec<(FrameKind, FrameClassBytes)> {
        [
            FrameKind::CatchUpRequest,
            FrameKind::CatchUpBatch,
            FrameKind::Push,
            FrameKind::AckReport,
            FrameKind::AckBatch,
        ]
        .into_iter()
        .filter_map(|kind| self.class_bytes.get(&kind).map(|bytes| (kind, *bytes)))
        .collect()
    }

    fn heal(&mut self) {
        for link in self.links.values_mut() {
            link.controls.clear();
        }
    }
}
