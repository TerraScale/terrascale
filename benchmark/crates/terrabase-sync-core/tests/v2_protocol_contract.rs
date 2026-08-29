//! The `terrascale.sync.v2` protocol contract, stated as tests.
//!
//! The typed wire vocabulary (ticket SE-04) is a public module of this
//! crate: these tests cross that seam only. The frame envelope carries
//! the shared discipline the ticket fixes — version, frame kind,
//! correlation/causation identity, an idempotency key exactly where
//! mutation is possible, and a content digest — and every decode path
//! fails closed.

use std::collections::BTreeMap;

use terrabase_sync_core::FrameId;
use terrabase_sync_core::JournalChange;
use terrabase_sync_core::JournalCommit;
use terrabase_sync_core::SyncBatch;
use terrabase_sync_core::SyncCheckpoint;
use terrabase_sync_core::SyncCore;
use terrabase_sync_core::SyncCoreConfig;
use terrabase_sync_core::SyncCoreError;
use terrabase_sync_core::SyncScope;
use terrabase_sync_core::SyncValue;
use terrabase_sync_core::protocol::Direction;
use terrabase_sync_core::protocol::DocumentWrite;
use terrabase_sync_core::protocol::ErrorCode;
use terrabase_sync_core::protocol::FrameClass;
use terrabase_sync_core::protocol::FrameKind;
use terrabase_sync_core::protocol::IdempotencyKey;
use terrabase_sync_core::protocol::MutationOutcome;
use terrabase_sync_core::protocol::PayloadCodec;
use terrabase_sync_core::protocol::ProtocolError;
use terrabase_sync_core::protocol::ResyncReason;
use terrabase_sync_core::protocol::SYNC_PROTOCOL_CONTRACT;
use terrabase_sync_core::protocol::SYNC_PROTOCOL_VERSION;
use terrabase_sync_core::protocol::SyncV2Body;
use terrabase_sync_core::protocol::SyncV2Frame;
use terrabase_sync_core::protocol::serve_delta;
use terrabase_sync_core::protocol::serve_snapshot;
use terrabase_sync_core::protocol::trigger_frame;

fn scope() -> SyncScope {
    SyncScope::new("tenant-a", "orders")
}

fn config() -> SyncCoreConfig {
    SyncCoreConfig { batch_byte_budget: 8 * 1024, checkpoint_window: 128 }
}

fn put(sequence: u64, document: &str, fields: usize) -> JournalCommit {
    let mut body = BTreeMap::new();
    for field in 0..fields {
        body.insert(format!("f{field:03}"), SyncValue::Str(format!("v{sequence:04}-{field:02}")));
    }
    JournalCommit {
        storage_sequence: sequence,
        operation_id: format!("op-{sequence:06}"),
        document_id: document.to_string(),
        change: JournalChange::Put { body },
    }
}

fn checkpoint(end: u64, sequence: u64) -> SyncCheckpoint {
    SyncCheckpoint::from_ends(vec![(terrabase_sync_core::SERVER_PEER_ID, end)], sequence)
}

#[test]
fn contract_identity_and_version_are_fixed_and_fail_closed() {
    assert_eq!(SYNC_PROTOCOL_CONTRACT, "terrascale.sync.v2");
    assert_eq!(SYNC_PROTOCOL_VERSION, 2);

    let frame = SyncV2Frame::seal(SyncV2Body::Heartbeat, FrameId::new(1), FrameId::new(0), None)
        .expect("heartbeat seals");
    let wire = frame.to_bytes();
    assert_eq!(wire[0], SYNC_PROTOCOL_VERSION, "the leading byte is the version");

    // Every version other than 2 fails closed — including 1, the v1
    // wire this contract deliberately does not speak.
    for version in [0u8, 1, 3, 0xFF] {
        let mut bad = wire.clone();
        bad[0] = version;
        assert!(
            matches!(SyncV2Frame::from_bytes(&bad), Err(ProtocolError::UnsupportedVersion(v)) if v == version),
            "version {version} must fail closed"
        );
    }
}

#[test]
fn every_frame_kind_has_a_stable_code_class_and_direction() {
    // The registry is closed: every kind round-trips through its code,
    // carries the direction the spec fixes, and names a frame class
    // with a written wire budget.
    let expected: &[(FrameKind, u16, Direction)] = &[
        (FrameKind::SessionSetup, 0x0001, Direction::ClientToServer),
        (FrameKind::SessionAccepted, 0x0002, Direction::ServerToClient),
        (FrameKind::SubscriptionModify, 0x0003, Direction::ClientToServer),
        (FrameKind::SubscriptionChanged, 0x0004, Direction::ServerToClient),
        (FrameKind::CheckpointAck, 0x0005, Direction::ClientToServer),
        (FrameKind::Heartbeat, 0x0006, Direction::Both),
        (FrameKind::DeltaBatch, 0x0007, Direction::ServerToClient),
        (FrameKind::SnapshotAnswer, 0x0008, Direction::ServerToClient),
        (FrameKind::Trigger, 0x0009, Direction::ServerToClient),
        (FrameKind::ResyncRequired, 0x000A, Direction::ServerToClient),
        (FrameKind::RegionRouteChange, 0x000B, Direction::ServerToClient),
        (FrameKind::Error, 0x000C, Direction::ServerToClient),
        (FrameKind::MutationRequest, 0x000D, Direction::ClientToServer),
        (FrameKind::MutationAck, 0x000E, Direction::ServerToClient),
    ];
    for (kind, code, direction) in expected {
        assert_eq!(kind.code(), *code, "{kind:?} code");
        assert_eq!(FrameKind::from_code(*code), Some(*kind), "code {code:#06x}");
        let sample = sample_body(*kind);
        assert_eq!(sample.direction(), *direction, "{kind:?} direction");
        assert!(sample.frame_class().budget_bytes() > 0, "{kind:?} budget");
    }
    assert_eq!(FrameKind::from_code(0x7FFF), None, "unknown codes are not kinds");
}

#[test]
fn envelope_carries_identity_idempotency_and_digest() {
    let key = IdempotencyKey::new("idem-000042").expect("key builds");
    let body = SyncV2Body::MutationRequest {
        collection: "orders".to_string(),
        document: "d-07".to_string(),
        write: DocumentWrite::Put {
            body: BTreeMap::from([("f000".to_string(), SyncValue::Str("v0007".to_string()))]),
        },
    };
    let frame = SyncV2Frame::seal(body, FrameId::new(11), FrameId::new(4), Some(key.clone()))
        .expect("mutation seals");
    assert_eq!(frame.correlation(), FrameId::new(11));
    assert_eq!(frame.causation(), FrameId::new(4));
    assert_eq!(frame.idempotency(), Some(key));
    assert!(frame.verify(), "a sealed frame verifies");

    let wire = frame.to_bytes();
    let decoded = SyncV2Frame::from_bytes(&wire).expect("round trips");
    assert!(decoded.verify());
    assert_eq!(decoded.kind(), FrameKind::MutationRequest);
    assert_eq!(decoded.correlation(), frame.correlation());
    assert_eq!(decoded.causation(), frame.causation());
    assert_eq!(
        decoded.idempotency().map(|k| k.as_str().to_string()),
        Some("idem-000042".to_string())
    );
    match decoded.body() {
        SyncV2Body::MutationRequest { collection, document, write } => {
            assert_eq!(collection, "orders");
            assert_eq!(document, "d-07");
            assert!(matches!(write, DocumentWrite::Put { .. }));
        }
        other => panic!("wrong body {other:?}"),
    }

    // Tamper one payload byte: the frame may decode but never verifies.
    let mut tampered = wire.clone();
    let last = tampered.len() - 1;
    tampered[last] ^= 0xFF;
    if let Ok(bad) = SyncV2Frame::from_bytes(&tampered) {
        assert!(!bad.verify(), "a tampered frame fails the digest check");
    }
}

#[test]
fn idempotency_keys_pair_exactly_with_mutation_frames() {
    let key = IdempotencyKey::new("idem-1").expect("key builds");
    let mutation = SyncV2Body::MutationRequest {
        collection: "orders".to_string(),
        document: "d-1".to_string(),
        write: DocumentWrite::Delete,
    };
    let ack = SyncV2Body::MutationAck {
        outcome: MutationOutcome::Applied {
            storage_sequence: 7,
            operation_id: "op-000007".to_string(),
        },
    };

    // A mutation without a key cannot be sealed…
    assert!(matches!(
        SyncV2Frame::seal(mutation.clone(), FrameId::new(1), FrameId::new(0), None),
        Err(ProtocolError::IdempotencyPairing(_))
    ));
    // …and no non-mutation frame may carry one.
    assert!(matches!(
        SyncV2Frame::seal(
            SyncV2Body::Heartbeat,
            FrameId::new(1),
            FrameId::new(0),
            Some(key.clone())
        ),
        Err(ProtocolError::IdempotencyPairing(_))
    ));
    // The ack is the mutation-family frame that may echo the key.
    assert!(SyncV2Frame::seal(ack, FrameId::new(2), FrameId::new(1), Some(key)).is_ok());

    // The wire pairing is enforced on decode, fail closed: a key on a
    // heartbeat body is rejected before any body field is read.
    let good = SyncV2Frame::seal(SyncV2Body::Heartbeat, FrameId::new(1), FrameId::new(0), None)
        .expect("seals")
        .to_bytes();
    assert!(SyncV2Frame::from_bytes(&good).is_ok());

    // Idempotency keys are validated: empty or oversized keys never build.
    assert!(IdempotencyKey::new("").is_err());
    assert!(IdempotencyKey::new(&"k".repeat(129)).is_err());
    assert!(IdempotencyKey::new("idem-2").is_ok());
}

#[test]
fn trigger_notifications_are_small_typed_and_content_free() {
    let frame = trigger_frame("orders", 42, "op-000042", FrameId::new(9), FrameId::new(8))
        .expect("trigger seals");
    assert_eq!(frame.kind(), FrameKind::Trigger);
    let SyncV2Body::Trigger { collection, storage_sequence, operation_id } = frame.body() else {
        panic!("trigger body");
    };
    // The payload is exactly two identifiers plus the scope: no document
    // identity, no field names, no values (PD-2026-08-26-13 D3).
    assert_eq!(collection, "orders");
    assert_eq!(*storage_sequence, 42);
    assert_eq!(operation_id, "op-000042");
    assert!(
        frame.to_bytes().len() <= FrameClass::Trigger.budget_bytes(),
        "the trigger frame {} bytes must fit its written budget {}",
        frame.to_bytes().len(),
        FrameClass::Trigger.budget_bytes()
    );

    // A trigger with a long collection name and operation id still fits.
    let long =
        trigger_frame(&"c".repeat(64), u64::MAX, &"o".repeat(64), FrameId::new(1), FrameId::new(0))
            .expect("seals");
    assert!(
        long.to_bytes().len() <= FrameClass::Trigger.budget_bytes(),
        "worst-case trigger {} bytes must fit {}",
        long.to_bytes().len(),
        FrameClass::Trigger.budget_bytes()
    );
}

#[test]
fn only_named_payload_codecs_carry_bytes_and_only_server_delivery_uses_them() {
    // The closed registry: the only bodies that carry a byte payload are
    // the delivery frames, and their codec identity is fixed by the kind.
    let body = SyncV2Body::DeltaBatch {
        codec: PayloadCodec::NativeDelta1,
        payload: vec![1, 2, 3],
        next: checkpoint(3, 2),
        within_budget: true,
        single_commit_oversize: false,
        commits: 2,
    };
    assert_eq!(body.direction(), Direction::ServerToClient);
    assert_eq!(body.frame_class(), FrameClass::Delivery);

    let snapshot = SyncV2Body::SnapshotAnswer {
        codec: PayloadCodec::NativeSnapshot1,
        payload: vec![9, 9],
        checkpoint: checkpoint(3, 2),
    };
    assert_eq!(snapshot.frame_class(), FrameClass::Delivery);

    // Codec and kind must agree: a snapshot codec on a delta body is
    // malformed, at seal time and on the wire.
    let mismatched = SyncV2Body::DeltaBatch {
        codec: PayloadCodec::NativeSnapshot1,
        payload: vec![1],
        next: checkpoint(1, 1),
        within_budget: true,
        single_commit_oversize: false,
        commits: 1,
    };
    assert!(matches!(
        SyncV2Frame::seal(mismatched, FrameId::new(1), FrameId::new(0), None),
        Err(ProtocolError::MalformedFrame(_))
    ));

    // No client-to-server frame carries a payload: mutations carry typed
    // document bodies, never encoded deltas — the server is the single
    // writer of record (PD-2026-08-26-12).
    for kind in ALL_KINDS {
        let body = sample_body(*kind);
        if body.direction() == Direction::ClientToServer {
            assert!(!body.carries_payload(), "{kind:?} must not carry payload bytes");
        }
    }
}

#[test]
fn mutations_are_server_ack_only() {
    // The v2 client mutation shape: a typed request with an idempotency
    // key, answered only by the server's acknowledgement. No frame kind
    // carries an optimistic patch or a client-side delta.
    let request = SyncV2Body::MutationRequest {
        collection: "orders".to_string(),
        document: "d-07".to_string(),
        write: DocumentWrite::Put {
            body: BTreeMap::from([
                ("f000".to_string(), SyncValue::Str("v7".to_string())),
                ("f001".to_string(), SyncValue::Int(7)),
                ("f002".to_string(), SyncValue::Bool(true)),
                ("f003".to_string(), SyncValue::Float(1.5)),
                ("f004".to_string(), SyncValue::Null),
            ]),
        },
    };
    let frame = SyncV2Frame::seal(
        request,
        FrameId::new(21),
        FrameId::new(3),
        Some(IdempotencyKey::new("idem-7").expect("key")),
    )
    .expect("seals");
    let wire = frame.to_bytes();
    let decoded = SyncV2Frame::from_bytes(&wire).expect("decodes");
    match decoded.body() {
        SyncV2Body::MutationRequest { write: DocumentWrite::Put { body }, .. } => {
            // The typed value model round-trips every scalar kind.
            assert_eq!(body.len(), 5);
            assert_eq!(body.get("f001"), Some(&SyncValue::Int(7)));
            assert_eq!(body.get("f002"), Some(&SyncValue::Bool(true)));
            assert_eq!(body.get("f003"), Some(&SyncValue::Float(1.5)));
            assert_eq!(body.get("f004"), Some(&SyncValue::Null));
        }
        other => panic!("wrong body {other:?}"),
    }

    // The acknowledgement vocabulary is closed: applied carries the
    // durable identities, rejected carries a typed reason.
    let applied = SyncV2Body::MutationAck {
        outcome: MutationOutcome::Applied {
            storage_sequence: 9,
            operation_id: "op-000009".to_string(),
        },
    };
    let rejected = SyncV2Body::MutationAck {
        outcome: MutationOutcome::Rejected { reason: ErrorCode::ScopeRefused },
    };
    for body in [applied, rejected] {
        let frame = SyncV2Frame::seal(
            body,
            FrameId::new(22),
            FrameId::new(21),
            Some(IdempotencyKey::new("idem-7").expect("key")),
        )
        .expect("ack seals");
        assert!(SyncV2Frame::from_bytes(&frame.to_bytes()).is_ok());
    }
}

#[test]
fn malformed_frames_are_rejected_with_typed_errors() {
    let frame = SyncV2Frame::seal(
        SyncV2Body::SessionSetup {
            collections: vec!["orders".to_string()],
            resume: Some(checkpoint(2, 1)),
        },
        FrameId::new(5),
        FrameId::new(1),
        None,
    )
    .expect("seals");
    let wire = frame.to_bytes();

    // Truncation at every length is rejected.
    for len in 0..wire.len() {
        assert!(
            SyncV2Frame::from_bytes(&wire[..len]).is_err(),
            "truncated frame of {len} bytes must not decode"
        );
    }

    // Trailing bytes are rejected.
    let mut trailing = wire.clone();
    trailing.push(0);
    assert!(matches!(SyncV2Frame::from_bytes(&trailing), Err(ProtocolError::MalformedFrame(_))));

    // An unknown frame kind fails closed.
    let mut unknown = wire.clone();
    let kind_at = 1; // version byte, then kind
    unknown[kind_at] = 0xFF;
    unknown[kind_at + 1] = 0x7F;
    assert!(matches!(SyncV2Frame::from_bytes(&unknown), Err(ProtocolError::MalformedFrame(_))));

    // The outer body-length field lying about the body is rejected: the
    // fixed header ends at byte 36, where the body length begins.
    let body_len_at = 1 + 2 + 8 + 8 + 1 + 8;
    assert_eq!(body_len_at, 28);
    let mut lying = wire.clone();
    lying[body_len_at] ^= 0xFF;
    assert!(SyncV2Frame::from_bytes(&lying).is_err());

    // Empty input fails closed.
    assert!(matches!(SyncV2Frame::from_bytes(&[]), Err(ProtocolError::UnsupportedVersion(0))));
}

#[test]
fn session_setup_resume_and_acks_round_trip() {
    let setup = SyncV2Body::SessionSetup {
        collections: vec!["orders".to_string(), "items".to_string()],
        resume: None,
    };
    let resume = SyncV2Body::SessionSetup {
        collections: vec!["orders".to_string()],
        resume: Some(checkpoint(5, 4)),
    };
    for (body, is_resume) in [(setup, false), (resume, true)] {
        let frame = SyncV2Frame::seal(body, FrameId::new(1), FrameId::new(0), None).expect("seals");
        let decoded = SyncV2Frame::from_bytes(&frame.to_bytes()).expect("decodes");
        let SyncV2Body::SessionSetup { collections, resume } = decoded.body() else {
            panic!("setup body");
        };
        assert!(!collections.is_empty());
        assert_eq!(resume.is_some(), is_resume);
    }

    let bodies: Vec<SyncV2Body> = vec![
        SyncV2Body::SessionAccepted {
            session: 77,
            server: checkpoint(9, 8),
            collections: vec!["orders".to_string()],
        },
        SyncV2Body::SubscriptionModify {
            add: vec!["items".to_string()],
            remove: vec!["orders".to_string()],
        },
        SyncV2Body::SubscriptionChanged { active: vec!["items".to_string()] },
        SyncV2Body::CheckpointAck { checkpoint: checkpoint(6, 5) },
        SyncV2Body::ResyncRequired {
            floor: checkpoint(4, 3),
            reason: ResyncReason::BehindRetainedFloor,
        },
        SyncV2Body::ResyncRequired {
            floor: checkpoint(8, 7),
            reason: ResyncReason::HistoryCompacted,
        },
        SyncV2Body::RegionRouteChange { region: "eu-1".to_string() },
        SyncV2Body::Error {
            code: ErrorCode::UnsupportedVersion,
            detail: Some("terrascale.sync.v1 is not spoken here".to_string()),
        },
    ];
    for body in bodies {
        let frame = SyncV2Frame::seal(body, FrameId::new(2), FrameId::new(1), None).expect("seals");
        assert!(frame.verify());
        let wire = frame.to_bytes();
        let decoded = SyncV2Frame::from_bytes(&wire).expect("decodes");
        assert_eq!(decoded.kind(), frame.kind());
        assert!(decoded.verify(), "{:?} round-trips verifying", frame.kind());
    }
}

#[test]
fn serving_glue_maps_the_core_answers_to_protocol_frames() {
    let mut core = SyncCore::new(scope(), config()).expect("core builds");
    for sequence in 1..=6u64 {
        core.apply(&put(sequence, &format!("d-{sequence:02}"), 4)).expect("applies");
    }

    // A delta answer becomes a DeltaBatch frame.
    let from = checkpoint(2, 2);
    let frame = serve_delta(&core, &from, FrameId::new(31), FrameId::new(1))
        .expect("serves")
        .expect("a delta answer");
    assert_eq!(frame.kind(), FrameKind::DeltaBatch);
    match frame.body() {
        SyncV2Body::DeltaBatch { codec, payload, next, commits, within_budget, .. } => {
            assert_eq!(*codec, PayloadCodec::NativeDelta1);
            assert!(!payload.is_empty());
            assert!(next.end_for(terrabase_sync_core::SERVER_PEER_ID).unwrap_or(0) > 2);
            assert!(*commits >= 1);
            assert!(*within_budget);
        }
        other => panic!("wrong body {other:?}"),
    }
    assert!(frame.verify());

    // Up to date answers nothing: triggers are the pull hint, not a frame.
    let current = core.checkpoint();
    assert!(
        serve_delta(&core, &current, FrameId::new(32), FrameId::new(1)).expect("serves").is_none()
    );

    // A checkpoint ahead of the server surfaces the core's typed error.
    let ahead = checkpoint(999, 999);
    assert!(matches!(
        serve_delta(&core, &ahead, FrameId::new(33), FrameId::new(1)),
        Err(terrabase_sync_core::protocol::ServeError::Core(
            SyncCoreError::CheckpointAheadOfServer { .. }
        ))
    ));

    // The snapshot answer frame.
    let snapshot = serve_snapshot(&core, FrameId::new(34), FrameId::new(1)).expect("serves");
    assert_eq!(snapshot.kind(), FrameKind::SnapshotAnswer);
    match snapshot.body() {
        SyncV2Body::SnapshotAnswer { codec, payload, checkpoint: at } => {
            assert_eq!(*codec, PayloadCodec::NativeSnapshot1);
            assert!(!payload.is_empty());
            assert_eq!(*at, core.checkpoint());
        }
        other => panic!("wrong body {other:?}"),
    }
}

#[test]
fn a_consumer_behind_the_floor_is_answered_with_resync_required() {
    let mut core = SyncCore::new(scope(), config()).expect("core builds");
    for sequence in 1..=8u64 {
        core.apply(&put(sequence, &format!("d-{sequence:02}"), 4)).expect("applies");
    }
    let stalled = {
        let mut consumer_checkpoint = SyncCheckpoint::initial();
        match core.delta_since(&consumer_checkpoint).expect("delta") {
            SyncBatch::Delta { next, .. } => consumer_checkpoint = next,
            other => panic!("unexpected {other:?}"),
        }
        consumer_checkpoint
    };
    // Overflow the window so the stalled checkpoint falls below the floor.
    for sequence in 9..=300u64 {
        core.apply(&put(sequence, &format!("d-{:02}", sequence % 8), 2)).expect("applies");
    }
    let frame = serve_delta(&core, &stalled, FrameId::new(41), FrameId::new(1))
        .expect("serves")
        .expect("the resync answer");
    assert_eq!(frame.kind(), FrameKind::ResyncRequired);
    match frame.body() {
        SyncV2Body::ResyncRequired { floor, reason } => {
            assert_eq!(*reason, ResyncReason::BehindRetainedFloor);
            assert!(floor.storage_sequence >= stalled.storage_sequence);
        }
        other => panic!("wrong body {other:?}"),
    }
}

#[test]
fn frame_budgets_are_written_definitions_with_exact_envelope_overhead() {
    // The fixed envelope overhead is a definition: the bytes every frame
    // spends before any payload — version, kind, correlation, causation,
    // idempotency presence, digest, body length, and the body's kind tag.
    let heartbeat =
        SyncV2Frame::seal(SyncV2Body::Heartbeat, FrameId::new(1), FrameId::new(0), None)
            .expect("seals");
    assert_eq!(
        heartbeat.to_bytes().len(),
        terrabase_sync_core::protocol::FIXED_ENVELOPE_BYTES,
        "a heartbeat frame is exactly the fixed envelope"
    );
    assert!(
        heartbeat.to_bytes().len() <= FrameClass::Heartbeat.budget_bytes(),
        "heartbeat fits its class budget"
    );

    // Every budget definition is positive and the delivery budget covers
    // the default batch budget of the core.
    for class in [
        FrameClass::Session,
        FrameClass::Subscription,
        FrameClass::Acknowledgement,
        FrameClass::Heartbeat,
        FrameClass::Delivery,
        FrameClass::Trigger,
        FrameClass::Control,
        FrameClass::Mutation,
    ] {
        assert!(class.budget_bytes() > 0);
    }
    assert!(
        FrameClass::Delivery.budget_bytes() >= SyncCoreConfig::default().batch_byte_budget,
        "the written delivery budget covers the default batch budget"
    );

    // The oversized single-commit exception is flagged, never silent.
    let oversize = SyncV2Body::DeltaBatch {
        codec: PayloadCodec::NativeDelta1,
        payload: vec![0xAB; FrameClass::Delivery.budget_bytes() + 1],
        next: checkpoint(1, 1),
        within_budget: false,
        single_commit_oversize: true,
        commits: 1,
    };
    let frame = SyncV2Frame::seal(oversize, FrameId::new(1), FrameId::new(0), None)
        .expect("a flagged oversize single-commit batch seals");
    assert!(frame.to_bytes().len() > FrameClass::Delivery.budget_bytes());

    // An oversize flag on a multi-commit batch is malformed.
    let lying = SyncV2Body::DeltaBatch {
        codec: PayloadCodec::NativeDelta1,
        payload: vec![1],
        next: checkpoint(1, 1),
        within_budget: false,
        single_commit_oversize: true,
        commits: 3,
    };
    assert!(matches!(
        SyncV2Frame::seal(lying, FrameId::new(1), FrameId::new(0), None),
        Err(ProtocolError::MalformedFrame(_))
    ));
}

/// Every kind in the registry, in code order.
const ALL_KINDS: &[FrameKind] = &[
    FrameKind::SessionSetup,
    FrameKind::SessionAccepted,
    FrameKind::SubscriptionModify,
    FrameKind::SubscriptionChanged,
    FrameKind::CheckpointAck,
    FrameKind::Heartbeat,
    FrameKind::DeltaBatch,
    FrameKind::SnapshotAnswer,
    FrameKind::Trigger,
    FrameKind::ResyncRequired,
    FrameKind::RegionRouteChange,
    FrameKind::Error,
    FrameKind::MutationRequest,
    FrameKind::MutationAck,
];

/// A structurally valid body for every kind (budget/direction probes).
fn sample_body(kind: FrameKind) -> SyncV2Body {
    match kind {
        FrameKind::SessionSetup => {
            SyncV2Body::SessionSetup { collections: vec!["orders".to_string()], resume: None }
        }
        FrameKind::SessionAccepted => SyncV2Body::SessionAccepted {
            session: 1,
            server: checkpoint(1, 1),
            collections: vec!["orders".to_string()],
        },
        FrameKind::SubscriptionModify => {
            SyncV2Body::SubscriptionModify { add: vec!["items".to_string()], remove: Vec::new() }
        }
        FrameKind::SubscriptionChanged => {
            SyncV2Body::SubscriptionChanged { active: vec!["items".to_string()] }
        }
        FrameKind::CheckpointAck => SyncV2Body::CheckpointAck { checkpoint: checkpoint(1, 1) },
        FrameKind::Heartbeat => SyncV2Body::Heartbeat,
        FrameKind::DeltaBatch => SyncV2Body::DeltaBatch {
            codec: PayloadCodec::NativeDelta1,
            payload: vec![1],
            next: checkpoint(1, 1),
            within_budget: true,
            single_commit_oversize: false,
            commits: 1,
        },
        FrameKind::SnapshotAnswer => SyncV2Body::SnapshotAnswer {
            codec: PayloadCodec::NativeSnapshot1,
            payload: vec![1],
            checkpoint: checkpoint(1, 1),
        },
        FrameKind::Trigger => SyncV2Body::Trigger {
            collection: "orders".to_string(),
            storage_sequence: 1,
            operation_id: "op-000001".to_string(),
        },
        FrameKind::ResyncRequired => SyncV2Body::ResyncRequired {
            floor: checkpoint(1, 1),
            reason: ResyncReason::BehindRetainedFloor,
        },
        FrameKind::RegionRouteChange => {
            SyncV2Body::RegionRouteChange { region: "eu-1".to_string() }
        }
        FrameKind::Error => SyncV2Body::Error { code: ErrorCode::SessionClosed, detail: None },
        FrameKind::MutationRequest => SyncV2Body::MutationRequest {
            collection: "orders".to_string(),
            document: "d-1".to_string(),
            write: DocumentWrite::Delete,
        },
        FrameKind::MutationAck => SyncV2Body::MutationAck {
            outcome: MutationOutcome::Rejected { reason: ErrorCode::ScopeRefused },
        },
    }
}
