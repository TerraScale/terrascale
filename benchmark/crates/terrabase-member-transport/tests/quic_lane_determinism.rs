//! SE-06 socket-lane determinism: one pump is a complete delivery step.
//!
//! The engine-socket lane's pumps are causally complete, never
//! wall-clock-bounded: a pump returns once every frame it wrote to a socket
//! has been read off the wire by the addressed member, so delivery is
//! signalled by progress, not by elapsed time, and the same script asserts
//! the same deliveries on an idle host and under host load. These tests pin
//! that contract at the transport seam — the property the contract
//! scenarios rely on whenever they drive a fixed number of pumps and then
//! assert an outcome — and pin that the W4 fault controls, not the clock,
//! decide what a pump delivers.

mod support;

use support::{member_id, tenant};
use terrabase_member_transport::{
    EncodedFrame, Envelope, FrameKind, FrameSelector, LaneInstant, MAX_LINK_FRAMES,
    MemberTransport, Payload, QuicLane, decode, encode,
};

/// One identifiable catch-up request frame from `from` to `to`.
fn request_frame(message_id: u64, from: &str, to: &str) -> EncodedFrame {
    let envelope = Envelope {
        kind: FrameKind::CatchUpRequest,
        message_id,
        correlation_id: None,
        causation_id: None,
        idempotency_key: None,
        source: member_id(from),
        target: member_id(to),
        issued_at: LaneInstant::at_ticks(0),
        expires_at: None,
        range: None,
        epoch: None,
        payload: Payload::CatchUpRequest { tenant: tenant("tenant-a"), after: None, limit: 8 },
    };
    encode(&envelope).expect("the request frame encodes")
}

/// The message identities of the delivered frames, in arrival order.
fn message_ids(frames: &[EncodedFrame]) -> Vec<u64> {
    frames
        .iter()
        .map(|frame| {
            let (envelope, _) = decode(&frame.bytes).expect("the delivered frame decodes");
            envelope.message_id
        })
        .collect()
}

/// Sends `count` frames from `from` to `to`, numbered from 1.
fn send_numbered(lane: &mut QuicLane, from: &str, to: &str, count: u64) {
    let (source, target) = (member_id(from), member_id(to));
    for id in 1..=count {
        lane.send(&source, &target, request_frame(id, from, to))
            .expect("the healthy direction accepts the frame");
    }
}

#[test]
fn one_pump_delivers_every_frame_it_accepted_over_a_fresh_connection() {
    // The first delivery of a direction also performs its QUIC handshake.
    // The pump that flushes the frames completes the handshake, the write,
    // and the peer's read inside itself: after one pump the follower holds
    // every frame, and nothing is left to a later pump or dropped to a
    // drain window.
    let mut lane = QuicLane::new(&["m-a1", "m-b1"]).expect("the socket lane binds");
    let (a1, b1) = (member_id("m-a1"), member_id("m-b1"));
    send_numbered(&mut lane, "m-a1", "m-b1", 8);

    lane.pump();

    let delivered = lane.receive(&b1, FrameSelector::All);
    let mut ids = message_ids(&delivered);
    ids.sort_unstable();
    assert_eq!(ids, vec![1, 2, 3, 4, 5, 6, 7, 8], "every accepted frame arrived exactly once");
    assert_eq!(lane.pending(&b1), 0, "nothing is left half-delivered");
    let stats = lane.link_stats(&a1, &b1);
    assert_eq!(stats.dropped, 0, "a healthy lane loses no frame to a drain bound: {stats:?}");
    assert_eq!(stats.queued, 0, "the direction's pipeline is empty: {stats:?}");

    // The step was complete, not partial: a further pump delivers nothing
    // new, so the delivery boundary is the pump, never a window.
    lane.pump();
    assert!(
        lane.receive(&b1, FrameSelector::All).is_empty(),
        "no frame of the first pump strays into a later one"
    );
    lane.close();
}

#[test]
fn one_pump_completes_every_direction_of_a_fresh_lane_at_once() {
    // Every direction of a fresh lane performs its own first handshake in
    // the same pump, in both directions between the same Members.
    let mut lane = QuicLane::new(&["m-a1", "m-b1", "m-c1"]).expect("the socket lane binds");
    let (a1, b1, c1) = (member_id("m-a1"), member_id("m-b1"), member_id("m-c1"));
    for (from, to) in [
        ("m-a1", "m-b1"),
        ("m-b1", "m-a1"),
        ("m-a1", "m-c1"),
        ("m-c1", "m-a1"),
        ("m-b1", "m-c1"),
        ("m-c1", "m-b1"),
    ] {
        send_numbered(&mut lane, from, to, 2);
    }

    lane.pump();

    // Every Member is the target of two directions, so it holds each
    // direction's two frames: exactly four, all addressed to it.
    for member in [&a1, &b1, &c1] {
        let mut delivered = message_ids(&lane.receive(member, FrameSelector::All));
        delivered.sort_unstable();
        assert_eq!(
            delivered,
            vec![1, 1, 2, 2],
            "{} holds exactly the frames addressed to it",
            member.as_str()
        );
        assert_eq!(lane.pending(member), 0, "nothing is left half-delivered");
    }
    let pairs = [(&a1, &b1), (&b1, &a1), (&a1, &c1), (&c1, &a1), (&b1, &c1), (&c1, &b1)];
    for (from, to) in pairs {
        let stats = lane.link_stats(from, to);
        assert_eq!(
            stats.dropped,
            0,
            "the {} -> {} direction lost nothing",
            from.as_str(),
            to.as_str()
        );
    }
    lane.close();
}

#[test]
fn one_pump_delivers_a_full_direction_bound_in_one_step() {
    // The bound of one direction (MAX_LINK_FRAMES) delivered inside a
    // single pump: the completion condition is every frame arrived, not a
    // sample of them, so a full queue cannot leave a straggler behind.
    let mut lane = QuicLane::new(&["m-a1", "m-b1"]).expect("the socket lane binds");
    let (a1, b1) = (member_id("m-a1"), member_id("m-b1"));
    send_numbered(&mut lane, "m-a1", "m-b1", MAX_LINK_FRAMES as u64);

    lane.pump();

    let delivered = lane.receive(&b1, FrameSelector::All);
    assert_eq!(
        delivered.len(),
        MAX_LINK_FRAMES,
        "the whole bound of frames is inside the addressed member's inbound queue"
    );
    let stats = lane.link_stats(&a1, &b1);
    assert_eq!(stats.dropped, 0, "no frame of the bound is lost: {stats:?}");
    assert_eq!(stats.overflowed, 0, "the bound itself never overflows: {stats:?}");
    lane.close();
}

#[test]
fn fault_controls_decide_what_a_pump_delivers_not_the_clock() {
    // The W4 fault vocabulary decides delivery at the lane boundary, and
    // every decision is a tick fact: a partition loses its frames
    // observably, a bounded drop loses exactly its count, and a hold-back
    // releases on its pump — so a pump's outcome is reproducible by driving
    // the same pumps, with no dependence on how long the host took.
    let mut lane = QuicLane::new(&["m-a1", "m-b1"]).expect("the socket lane binds");
    let (a1, b1) = (member_id("m-a1"), member_id("m-b1"));

    // Partition: three frames lost observably, nothing delivered.
    lane.link(&a1, &b1).partition();
    send_numbered(&mut lane, "m-a1", "m-b1", 3);
    lane.pump();
    assert_eq!(lane.link_stats(&a1, &b1).dropped, 3, "the partition counts every frame it lost");
    assert!(lane.receive(&b1, FrameSelector::All).is_empty());
    assert_eq!(lane.pending(&b1), 0, "the partitioned pump delivers nothing and waits for nothing");

    // Bounded drop on the reverse direction: exactly one frame lost, the
    // rest delivered in the same pump.
    lane.heal();
    lane.link(&b1, &a1).drop_next(1);
    send_numbered(&mut lane, "m-b1", "m-a1", 3);
    lane.pump();
    assert_eq!(lane.link_stats(&b1, &a1).dropped, 1, "the bounded drop loses exactly its count");
    let mut arrived = message_ids(&lane.receive(&a1, FrameSelector::All));
    arrived.sort_unstable();
    assert_eq!(arrived, vec![2, 3], "the surviving frames are all inside one pump");
    assert_eq!(lane.pending(&a1), 0);

    // Hold-back: two pumps of delay, released by the second pump after the
    // send, delivered whole inside that pump.
    lane.heal();
    lane.link(&a1, &b1).delay_pumps(2);
    send_numbered(&mut lane, "m-a1", "m-b1", 2);
    lane.pump();
    assert_eq!(lane.link_stats(&a1, &b1).held, 2, "the hold-back holds every frame");
    assert!(lane.receive(&b1, FrameSelector::All).is_empty(), "nothing leaks out early");
    lane.pump();
    assert_eq!(lane.link_stats(&a1, &b1).held, 0, "the hold-back released on its pump");
    let mut released = message_ids(&lane.receive(&b1, FrameSelector::All));
    released.sort_unstable();
    assert_eq!(released, vec![1, 2], "the released frames are all inside their pump");
    assert_eq!(lane.link_stats(&a1, &b1).dropped, 3, "no later fault erased the partition's count");
    lane.close();
}
