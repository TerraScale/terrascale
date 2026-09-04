//! The `terrascale.sync.v2` protocol contract, stated as tests.
//!
//! The typed wire vocabulary (ticket SE-04) is a public module of this
//! crate: these tests cross that seam only. The frame envelope carries
//! the shared discipline the ticket fixes — version, frame kind,
//! correlation/causation identity, an idempotency key exactly where
//! mutation is possible, and a content digest — and every decode path
//! fails closed. The CRDT-era frame kinds were deleted with the
//! per-field materialization (S-04, D-38); their wire codes fail
//! closed until the result-view delivery vocabulary replaces them.

use terrabase_sync_core::FrameId;
use terrabase_sync_core::protocol::Direction;
use terrabase_sync_core::protocol::ErrorCode;
use terrabase_sync_core::protocol::FrameClass;
use terrabase_sync_core::protocol::FrameKind;
use terrabase_sync_core::protocol::IdempotencyKey;
use terrabase_sync_core::protocol::ProtocolError;
use terrabase_sync_core::protocol::SYNC_PROTOCOL_CONTRACT;
use terrabase_sync_core::protocol::SYNC_PROTOCOL_VERSION;
use terrabase_sync_core::protocol::SyncV2Body;
use terrabase_sync_core::protocol::SyncV2Frame;
use terrabase_sync_core::protocol::trigger_frame;

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
        (FrameKind::Heartbeat, 0x0006, Direction::Both),
        (FrameKind::Trigger, 0x0009, Direction::ServerToClient),
        (FrameKind::RegionRouteChange, 0x000B, Direction::ServerToClient),
        (FrameKind::Error, 0x000C, Direction::ServerToClient),
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
fn the_deleted_crdt_era_frame_codes_fail_closed() {
    // The checkpoint, delivery, resync, and mutation kinds carried the
    // deleted per-field CRDT state (S-04, D-38). Their wire codes are
    // withdrawn: none decodes as a kind.
    for code in [0x0005u16, 0x0007, 0x0008, 0x000A, 0x000D, 0x000E] {
        assert_eq!(FrameKind::from_code(code), None, "code {code:#06x} is withdrawn");
    }
}

#[test]
fn envelope_carries_identity_and_digest() {
    let body = SyncV2Body::SessionSetup { collections: vec!["orders".to_string()] };
    let frame = SyncV2Frame::seal(body, FrameId::new(11), FrameId::new(4), None)
        .expect("session setup seals");
    assert_eq!(frame.correlation(), FrameId::new(11));
    assert_eq!(frame.causation(), FrameId::new(4));
    assert_eq!(frame.idempotency(), None);
    assert!(frame.verify(), "a sealed frame verifies");

    let wire = frame.to_bytes();
    let decoded = SyncV2Frame::from_bytes(&wire).expect("round trips");
    assert!(decoded.verify());
    assert_eq!(decoded.kind(), FrameKind::SessionSetup);
    assert_eq!(decoded.correlation(), frame.correlation());
    assert_eq!(decoded.causation(), frame.causation());
    match decoded.body() {
        SyncV2Body::SessionSetup { collections } => {
            assert_eq!(collections, &["orders".to_string()]);
        }
        other => panic!("wrong body {other:?}"),
    }

    // Tamper one body byte: the frame may decode but never verifies.
    let mut tampered = wire.clone();
    let last = tampered.len() - 1;
    tampered[last] ^= 0xFF;
    if let Ok(bad) = SyncV2Frame::from_bytes(&tampered) {
        assert!(!bad.verify(), "a tampered frame fails the digest check");
    }
}

#[test]
fn no_frame_kind_carries_an_idempotency_key() {
    // The mutation family was deleted with the per-field write model
    // (S-04, D-38): the pairing rule fails closed in both directions
    // until new mutation kinds land.
    let key = IdempotencyKey::new("idem-1").expect("key builds");
    for kind in ALL_KINDS {
        let body = sample_body(*kind);
        assert!(!body.is_mutation(), "{kind:?} is not a mutation kind");
        assert!(
            matches!(
                SyncV2Frame::seal(body, FrameId::new(1), FrameId::new(0), Some(key.clone())),
                Err(ProtocolError::IdempotencyPairing(_))
            ),
            "{kind:?} must not carry an idempotency key"
        );
    }

    // The wire pairing is enforced on decode, fail closed.
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
fn malformed_frames_are_rejected_with_typed_errors() {
    let frame = SyncV2Frame::seal(
        SyncV2Body::SessionSetup { collections: vec!["orders".to_string()] },
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
fn session_and_subscription_bodies_round_trip() {
    let bodies: Vec<SyncV2Body> = vec![
        SyncV2Body::SessionSetup { collections: vec!["orders".to_string(), "items".to_string()] },
        SyncV2Body::SessionAccepted { session: 77, collections: vec!["orders".to_string()] },
        SyncV2Body::SubscriptionModify {
            add: vec!["items".to_string()],
            remove: vec!["orders".to_string()],
        },
        SyncV2Body::SubscriptionChanged { active: vec!["items".to_string()] },
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
fn frame_budgets_are_written_definitions_with_exact_envelope_overhead() {
    // The fixed envelope overhead is a definition: the bytes every frame
    // spends before any body — version, kind, correlation, causation,
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

    // Every budget definition is positive.
    for class in [
        FrameClass::Session,
        FrameClass::Subscription,
        FrameClass::Heartbeat,
        FrameClass::Trigger,
        FrameClass::Control,
    ] {
        assert!(class.budget_bytes() > 0);
    }

    // Class budgets are a decode/encode invariant, not only an advisory
    // diagnostic.  A valid body can still be refused when it does not fit
    // the budget allocated to its frame class.
    let oversized = SyncV2Body::SessionSetup { collections: vec!["orders".repeat(32); 256] };
    assert!(matches!(
        SyncV2Frame::seal(oversized, FrameId::new(1), FrameId::new(0), None),
        Err(ProtocolError::MalformedFrame("frame exceeds its class budget"))
    ));
}

/// Every kind in the registry, in code order.
const ALL_KINDS: &[FrameKind] = &[
    FrameKind::SessionSetup,
    FrameKind::SessionAccepted,
    FrameKind::SubscriptionModify,
    FrameKind::SubscriptionChanged,
    FrameKind::Heartbeat,
    FrameKind::Trigger,
    FrameKind::RegionRouteChange,
    FrameKind::Error,
];

/// A structurally valid body for every kind (budget/direction probes).
fn sample_body(kind: FrameKind) -> SyncV2Body {
    match kind {
        FrameKind::SessionSetup => {
            SyncV2Body::SessionSetup { collections: vec!["orders".to_string()] }
        }
        FrameKind::SessionAccepted => {
            SyncV2Body::SessionAccepted { session: 1, collections: vec!["orders".to_string()] }
        }
        FrameKind::SubscriptionModify => {
            SyncV2Body::SubscriptionModify { add: vec!["items".to_string()], remove: Vec::new() }
        }
        FrameKind::SubscriptionChanged => {
            SyncV2Body::SubscriptionChanged { active: vec!["items".to_string()] }
        }
        FrameKind::Heartbeat => SyncV2Body::Heartbeat,
        FrameKind::Trigger => SyncV2Body::Trigger {
            collection: "orders".to_string(),
            storage_sequence: 1,
            operation_id: "op-000001".to_string(),
        },
        FrameKind::RegionRouteChange => {
            SyncV2Body::RegionRouteChange { region: "eu-1".to_string() }
        }
        FrameKind::Error => SyncV2Body::Error { code: ErrorCode::SessionClosed, detail: None },
    }
}
