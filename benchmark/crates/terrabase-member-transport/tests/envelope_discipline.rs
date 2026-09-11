//! The member-envelope fail-closed discipline, exercised adversarially at
//! the codec seam (SE-06).
//!
//! Every frame crossing a member lane carries the workspace
//! message-contract discipline; these tests corrupt each field in turn and
//! assert the typed rejection, then verify the member boundary enforces the
//! target and expiry rules and that the measured envelope overhead stays at
//! or under the written PD-13 D1 budget for representative lane frames.

mod support;

use support::{bare_commit, frame_of, hostile_envelope, open_member};

use terrabase_member_transport::{
    BUDGET_ENVELOPE_OVERHEAD_BYTES, CollectionName, DocumentId, ENVELOPE_SCHEMA,
    ENVELOPE_VERSION_MINOR, EnvelopeError, FrameKind, FrameSelector, InProcessLane, LaneInstant,
    MAX_CONTEXT_BYTES, MemberTransport, Payload, ReplicatedWrite, decode, encode, peek_kind,
    peek_payload_len,
};

fn base_envelope() -> terrabase_member_transport::Envelope {
    hostile_envelope(
        FrameKind::CatchUpBatch,
        "m-src",
        "m-dst",
        Some("main".to_owned()),
        Some(1),
        Payload::CatchUpBatch {
            tenant: support::tenant("tenant-a"),
            commits: vec![bare_commit("op-1", 1, "doc-1", "v1")],
            through: Some(1),
            tenant_high_water: Some(1),
            servable_from: Some(1),
            gap_below: None,
        },
    )
}

fn replication_envelope() -> terrabase_member_transport::Envelope {
    replication_envelope_with_context(Vec::new())
}

fn replication_envelope_with_context(context: Vec<u8>) -> terrabase_member_transport::Envelope {
    hostile_envelope(
        FrameKind::ReplicationBatch,
        "m-src",
        "m-dst",
        Some("main".to_owned()),
        Some(1),
        Payload::ReplicationBatch {
            tenant: support::tenant("tenant-a"),
            records: vec![ReplicatedWrite {
                collection: CollectionName::try_new("users").expect("valid collection"),
                id: DocumentId::try_new("doc-1").expect("valid document"),
                body: None,
                commit_ulid: "commit-1".to_owned(),
                hlc: 1,
                origin: "r-alpha".to_owned(),
                origin_sequence: 1,
                context,
            }],
        },
    )
}

#[test]
fn every_frame_kind_round_trips_through_the_discipline() {
    let payloads = [
        (
            FrameKind::CatchUpRequest,
            Payload::CatchUpRequest {
                tenant: support::tenant("tenant-a"),
                after: Some(3),
                limit: 8,
            },
        ),
        (FrameKind::CatchUpBatch, base_envelope().payload),
        (
            FrameKind::Push,
            Payload::Push {
                tenant: support::tenant("tenant-a"),
                range: "main".to_owned(),
                epoch: 2,
                commits: vec![bare_commit("op-9", 4, "doc-9", "v9")],
            },
        ),
        (
            FrameKind::AckReport,
            Payload::AckReport {
                tenant: support::tenant("tenant-a"),
                operation: support::operation("op-1"),
                held_synced: true,
            },
        ),
    ];
    for (kind, payload) in payloads {
        let envelope = hostile_envelope(
            kind,
            "m-src",
            "m-dst",
            Some("main".to_owned()),
            Some(1),
            payload.clone(),
        );
        let frame = frame_of(&envelope);
        let (decoded, encoded) = decode(&frame.bytes).expect("the frame decodes");
        assert_eq!(decoded, envelope, "{kind:?} round-trips");
        assert_eq!(encoded.payload_len, frame.payload_len);
        assert_eq!(peek_kind(&frame.bytes).expect("kind peeks"), kind);
        assert_eq!(
            peek_payload_len(&frame.bytes),
            Some(frame.payload_len),
            "the payload length peeks without decoding"
        );
    }
}

#[test]
fn an_unknown_schema_fails_closed() {
    let frame = frame_of(&base_envelope());
    let mut bytes = frame.bytes.clone();
    // Corrupt the schema text inside its declared length (the digest would
    // also catch this later; the schema check fails first).
    bytes[1] = b'X';
    assert_eq!(decode(&bytes), Err(EnvelopeError::UnknownSchema));
}

#[test]
fn an_unsupported_version_fails_closed() {
    let mut major = frame_of(&base_envelope()).bytes;
    let major_at = 1 + ENVELOPE_SCHEMA.len();
    major[major_at] = 0xff;
    assert_eq!(decode(&major), Err(EnvelopeError::UnsupportedMajorVersion));

    let mut minor = frame_of(&base_envelope()).bytes;
    minor[major_at + 2..major_at + 4].copy_from_slice(&(ENVELOPE_VERSION_MINOR + 1).to_le_bytes());
    assert_eq!(decode(&minor), Err(EnvelopeError::UnsupportedMinorVersion));
}

#[test]
fn encoded_frames_use_the_minimum_minor_for_their_kind() {
    let payloads = [
        (
            FrameKind::CatchUpRequest,
            Payload::CatchUpRequest {
                tenant: support::tenant("tenant-a"),
                after: Some(3),
                limit: 8,
            },
            0,
        ),
        (FrameKind::CatchUpBatch, base_envelope().payload, 0),
        (
            FrameKind::Push,
            Payload::Push {
                tenant: support::tenant("tenant-a"),
                range: "main".to_owned(),
                epoch: 2,
                commits: vec![bare_commit("op-9", 4, "doc-9", "v9")],
            },
            0,
        ),
        (
            FrameKind::AckReport,
            Payload::AckReport {
                tenant: support::tenant("tenant-a"),
                operation: support::operation("op-1"),
                held_synced: true,
            },
            0,
        ),
        (
            FrameKind::AckBatch,
            Payload::AckBatch {
                tenant: support::tenant("tenant-a"),
                reports: vec![(support::operation("op-1"), true)],
            },
            1,
        ),
        (FrameKind::ReplicationBatch, replication_envelope().payload, 4),
        (
            FrameKind::ReplicationPublication,
            Payload::ReplicationPublication {
                tenant: support::tenant("tenant-a"),
                publication: vec![0, 1, 2, 3],
            },
            5,
        ),
    ];
    let minor_at = 1 + ENVELOPE_SCHEMA.len() + 2;
    for (kind, payload, expected_minor) in payloads {
        let envelope =
            hostile_envelope(kind, "m-src", "m-dst", Some("main".to_owned()), Some(1), payload);
        let frame = frame_of(&envelope);
        let minor = u16::from_le_bytes([frame.bytes[minor_at], frame.bytes[minor_at + 1]]);
        assert_eq!(minor, expected_minor, "{kind:?} uses its minimum minor");
        assert_eq!(decode(&frame.bytes).expect("the frame decodes").0, envelope);
    }
    assert_eq!(ENVELOPE_VERSION_MINOR, 5);
}

#[test]
fn a_legacy_non_replication_frame_at_minor_two_still_decodes() {
    use terrabase_member_transport::envelope::DIGEST_BYTES;

    let expected = base_envelope();
    let frame = frame_of(&expected);
    let minor_at = 1 + ENVELOPE_SCHEMA.len() + 2;
    let mut legacy = frame.bytes;
    legacy[minor_at..minor_at + 2].copy_from_slice(&2u16.to_le_bytes());
    let signed_len = legacy.len() - DIGEST_BYTES;
    let digest = ring::digest::digest(&ring::digest::SHA256, &legacy[..signed_len]);
    legacy[signed_len..].copy_from_slice(digest.as_ref());

    assert_eq!(decode(&legacy).expect("the legacy frame decodes").0, expected);
}

#[test]
fn an_old_replication_batch_fails_with_an_explicit_version_error() {
    use terrabase_member_transport::envelope::DIGEST_BYTES;

    let frame = frame_of(&replication_envelope());
    let version_at = 1 + ENVELOPE_SCHEMA.len();
    let payload_start = frame.bytes.len() - DIGEST_BYTES - frame.payload_len;
    let payload_len_at = payload_start - 4;
    // Minor 3 carries the source sequence but predates the context field.
    // Remove the canonical context length prefix (empty in this fixture).
    let old_payload_len = frame.payload_len - std::mem::size_of::<u32>();
    let mut legacy = frame.bytes;
    legacy.drain(payload_start + old_payload_len..payload_start + frame.payload_len);
    legacy[version_at + 2..version_at + 4].copy_from_slice(&2u16.to_le_bytes());
    legacy[payload_len_at..payload_len_at + 4].copy_from_slice(
        &u32::try_from(old_payload_len).expect("payload length fits").to_le_bytes(),
    );
    let signed_len = legacy.len() - DIGEST_BYTES;
    let digest = ring::digest::digest(&ring::digest::SHA256, &legacy[..signed_len]);
    legacy[signed_len..].copy_from_slice(digest.as_ref());

    assert_eq!(decode(&legacy), Err(EnvelopeError::UnsupportedMinorVersion));
    assert_eq!(peek_kind(&legacy), Err(EnvelopeError::UnsupportedMinorVersion));
}

#[test]
fn replication_context_has_a_canonical_golden_wire_suffix() {
    use terrabase_member_transport::envelope::DIGEST_BYTES;

    let context = vec![0x00, 0xff, 0x01];
    let frame = frame_of(&replication_envelope_with_context(context.clone()));
    let payload_start = frame.bytes.len() - DIGEST_BYTES - frame.payload_len;
    let context_start = payload_start + frame.payload_len - context.len() - 4;
    assert_eq!(&frame.bytes[context_start..context_start + 7], &[3, 0, 0, 0, 0, 0xff, 1]);
    assert_eq!(
        decode(&frame.bytes).expect("the frame decodes").0.payload,
        replication_envelope_with_context(context).payload
    );
}

#[test]
fn replication_context_is_bounded_and_digest_bound() {
    assert_eq!(
        encode(&replication_envelope_with_context(vec![0; MAX_CONTEXT_BYTES + 1])),
        Err(EnvelopeError::FieldTooLarge)
    );

    let context = vec![1, 2, 3];
    let frame = frame_of(&replication_envelope_with_context(context.clone()));
    let payload_start = frame.bytes.len() - 32 - frame.payload_len;
    let context_start = payload_start + frame.payload_len - context.len();
    let mut corrupted = frame.bytes;
    corrupted[context_start] ^= 1;
    assert_eq!(decode(&corrupted), Err(EnvelopeError::DigestMismatch));
}

#[test]
fn reordered_replication_frames_preserve_opaque_context_bytes() {
    let source = support::member_id("m-src");
    let target = support::member_id("m-dst");
    let mut lane = InProcessLane::new();
    lane.link(&source, &target).reorder();
    for (message_id, context) in [(1, vec![1]), (2, vec![2, 0xff])] {
        let mut envelope = replication_envelope_with_context(context);
        envelope.message_id = message_id;
        lane.send(&source, &target, frame_of(&envelope)).expect("the frame sends");
    }
    lane.pump();
    let received = lane.receive(&target, FrameSelector::All);
    assert_eq!(received.len(), 2);
    let mut contexts = received
        .iter()
        .map(|frame| match &decode(&frame.bytes).expect("the frame decodes").0.payload {
            Payload::ReplicationBatch { records, .. } => records[0].context.clone(),
            _ => unreachable!("the test only sends replication frames"),
        })
        .collect::<Vec<_>>();
    contexts.sort();
    assert_eq!(contexts, vec![vec![1], vec![2, 0xff]]);
}

#[test]
fn an_unknown_frame_kind_fails_closed() {
    let mut bytes = frame_of(&base_envelope()).bytes;
    let kind_at = 1 + ENVELOPE_SCHEMA.len() + 4;
    // Kind 5 is the batched acknowledgement report since envelope minor
    // 1 (SE-07); kind 6 is the journal tailer's replicated-record batch
    // (S-05); kind 7 is the complete source publication (minor 5);
    // the first unknown kind byte is 8.
    bytes[kind_at] = 8;
    assert_eq!(decode(&bytes), Err(EnvelopeError::UnsupportedKind));
}

#[test]
fn truncation_trailing_bytes_and_length_lies_fail_closed() {
    let frame = frame_of(&base_envelope());
    let full = frame.bytes.clone();
    // Truncation at every prefix boundary inside the frame.
    for cut in 1..full.len() {
        let result = decode(&full[..cut]);
        assert!(
            matches!(
                result,
                Err(EnvelopeError::Truncated
                    | EnvelopeError::MalformedLength
                    | EnvelopeError::Oversized)
            ) || result.is_ok(),
            "a truncated frame never decodes partially: {result:?} at cut {cut}"
        );
        assert!(result.is_err() || cut == full.len(), "only the whole frame decodes: cut {cut}");
    }
    // Trailing bytes past the digest.
    let mut padded = full.clone();
    padded.push(0);
    assert_eq!(decode(&padded), Err(EnvelopeError::TrailingBytes));
    // A payload-length prefix naming more than the frame holds.
    let mut lied = full.clone();
    let payload_len_at = lied.len() - 32 - frame.payload_len - 4;
    lied[payload_len_at..payload_len_at + 4].copy_from_slice(
        &u32::try_from(frame.payload_len + 1).expect("payload length fits a u32").to_le_bytes(),
    );
    assert!(
        matches!(decode(&lied), Err(EnvelopeError::Truncated)),
        "a length lie fails closed: {:?}",
        decode(&lied)
    );
}

#[test]
fn a_digest_disagreement_fails_closed() {
    let frame = frame_of(&base_envelope());
    let mut bytes = frame.bytes.clone();
    let last = bytes.len() - 1;
    bytes[last] ^= 0xff;
    assert_eq!(decode(&bytes), Err(EnvelopeError::DigestMismatch));
}

#[test]
fn invalid_text_oversized_fields_and_payloads_fail_closed() {
    // Invalid UTF-8 inside a header string field: the source-member text is
    // parsed before the digest is consulted, so a corrupted byte is rejected
    // as invalid text (a payload-text corruption is caught earlier by the
    // digest itself).
    let frame = frame_of(&base_envelope());
    let mut bytes = frame.bytes.clone();
    let source_at = bytes
        .windows(b"m-src".len())
        .position(|window| window == b"m-src")
        .expect("the source member text is in the header");
    bytes[source_at] = 0xff;
    assert!(
        matches!(decode(&bytes), Err(EnvelopeError::InvalidText)),
        "invalid text fails closed: {:?}",
        decode(&bytes)
    );

    // A field over its declared bound fails at encode time.
    let oversized = hostile_envelope(
        FrameKind::Push,
        "m-src",
        "m-dst",
        Some("r".repeat(5000)),
        Some(1),
        Payload::Push {
            tenant: support::tenant("tenant-a"),
            range: "main".to_owned(),
            epoch: 1,
            commits: Vec::new(),
        },
    );
    assert_eq!(
        encode(&oversized),
        Err(EnvelopeError::FieldTooLarge),
        "an oversized field never encodes"
    );

    // A payload over its bound fails at encode time: many individually-valid
    // fields whose sum crosses the payload bound (a single oversized field
    // is the FieldTooLarge case above).
    let commits = (0..600)
        .map(|index| bare_commit(&format!("op-bulk-{index}"), index + 1, "doc-1", &"x".repeat(600)))
        .collect::<Vec<_>>();
    let huge = hostile_envelope(
        FrameKind::Push,
        "m-src",
        "m-dst",
        Some("main".to_owned()),
        Some(1),
        Payload::Push {
            tenant: support::tenant("tenant-a"),
            range: "main".to_owned(),
            epoch: 1,
            commits,
        },
    );
    assert_eq!(encode(&huge), Err(EnvelopeError::Oversized), "an oversized payload never encodes");
}

#[test]
fn the_member_boundary_rejects_wrong_target_and_expired_frames() {
    let topology = support::two_member_topology("m-src", "m-dst", "main");
    let (dir, mut member) = open_member("boundary", "m-dst", &topology);
    let mut lane = terrabase_member_transport::InProcessLane::new();
    let src = support::member_id("m-src");
    let dst = support::member_id("m-dst");

    // Addressed to another member: rejected at the member boundary and
    // recorded as an observable discipline rejection.
    let wrong_target = hostile_envelope(
        FrameKind::CatchUpRequest,
        "m-src",
        "m-other",
        None,
        None,
        Payload::CatchUpRequest { tenant: support::tenant("tenant-a"), after: None, limit: 4 },
    );
    lane.send(&src, &dst, frame_of(&wrong_target)).expect("the link accepts the frame");
    lane.pump();
    let report =
        member.dispatch_under(&mut lane, &support::tenant("tenant-a")).expect("the pass completes");
    assert_eq!(report.envelope_rejections, 1);
    assert!(matches!(member.envelope_rejections()[0].reason, EnvelopeError::WrongTarget));

    // Expired at the receiver's clock: rejected the same way.
    let mut expired = wrong_target;
    expired.target = dst.clone();
    expired.issued_at = LaneInstant::at_ticks(0);
    expired.expires_at = Some(LaneInstant::at_ticks(1));
    lane.send(&src, &dst, frame_of(&expired)).expect("the link accepts the frame");
    lane.pump();
    // The lane clock is at 1; a frame expiring at 1 is expired at the
    // receiver (expiry is inclusive of the instant).
    let report =
        member.dispatch_under(&mut lane, &support::tenant("tenant-a")).expect("the pass completes");
    assert!(report.envelope_rejections >= 1, "the expired frame is rejected");
    assert!(
        member
            .envelope_rejections()
            .iter()
            .any(|rejection| matches!(rejection.reason, EnvelopeError::Expired)),
        "the expiry rejection is observable"
    );
    assert!(lane.pending(&dst) == 0 || lane.receive(&dst, FrameSelector::All).is_empty());

    member.close();
    support::clean(&dir);
}

#[test]
fn measured_envelope_overhead_stays_within_the_written_budget() {
    // Representative lane frames — one of each class, with lane-shaped
    // identities — stay at or under the provisional PD-13 D1 envelope
    // overhead budget. The budget stays provisional until SE-07 ratifies
    // it; this is the lane's standing check, not a ratified verdict.
    let frames = [
        frame_of(&hostile_envelope(
            FrameKind::CatchUpRequest,
            "m-a1",
            "m-b1",
            None,
            None,
            Payload::CatchUpRequest {
                tenant: support::tenant("tenant-lane"),
                after: Some(41),
                limit: 32,
            },
        )),
        frame_of(&base_envelope()),
        frame_of(&hostile_envelope(
            FrameKind::Push,
            "m-a1",
            "m-b1",
            Some("main".to_owned()),
            Some(3),
            Payload::Push {
                tenant: support::tenant("tenant-lane"),
                range: "main".to_owned(),
                epoch: 3,
                commits: vec![bare_commit("op-9d41c", 41, "doc-1", "lane-value-one")],
            },
        )),
        frame_of(&hostile_envelope(
            FrameKind::AckReport,
            "m-b1",
            "m-a1",
            Some("main".to_owned()),
            Some(3),
            Payload::AckReport {
                tenant: support::tenant("tenant-lane"),
                operation: support::operation("op-9d41c"),
                held_synced: true,
            },
        )),
    ];
    for frame in &frames {
        assert!(
            frame.overhead_bytes() <= BUDGET_ENVELOPE_OVERHEAD_BYTES,
            "the {}-byte overhead exceeds the {}-byte budget",
            frame.overhead_bytes(),
            BUDGET_ENVELOPE_OVERHEAD_BYTES
        );
    }
}
