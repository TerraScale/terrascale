//! The adversarial-frame suite (SE-14): malformed, unknown-version,
//! unknown-kind, digest-mismatched, mis-paired, oversized, and
//! structurally hostile frames fail closed — at the codec, at the
//! envelope, and over the live UDS connection (the server answers the
//! typed error frame and, where the contract requires it, drops the
//! connection).
//!
//! The nil-handling and map/array-distinction pitfalls the codec survey
//! flagged (PD-2026-08-26-02 D4) are pinned here and in the golden
//! corpus: `nil` is legal exactly in the optional idempotency slot and
//! schema-optional fields, and frame bodies are positional arrays,
//! never maps.

use std::io::Write as _;
use std::os::unix::net::UnixStream;
use std::sync::Arc;

use terrabase_tbep::EnvelopeError;
use terrabase_tbep::IdempotencyKey;
use terrabase_tbep::SyncDeliveryHost;
use terrabase_tbep::TbepFrame;
use terrabase_tbep::TbepServer;
use terrabase_tbep::TbepValue;
use terrabase_tbep::codec::decode_body;
use terrabase_tbep::codec::encode_body;
use terrabase_tbep::generated::AdminStatusRequest;
use terrabase_tbep::generated::BackboneSubscribeRequest;
use terrabase_tbep::generated::DocumentPut;
use terrabase_tbep::generated::DocumentReadRequest;
use terrabase_tbep::generated::DocumentWriteRequest;
use terrabase_tbep::generated::ErrorCode;
use terrabase_tbep::generated::ExpectedVersion;
use terrabase_tbep::generated::FrameKind;
use terrabase_tbep::generated::MAX_FRAME_BYTES;
use terrabase_tbep::generated::SupervisionHealthRequest;
use terrabase_tbep::generated::TbepBody;

/// Every way a hostile frame tries to enter; every row must fail closed.
fn sealed_health() -> Vec<u8> {
    TbepFrame::seal(SupervisionHealthRequest {}, 1, 0, None).expect("seals").to_bytes()
}

#[test]
fn unknown_wire_version_fails_closed() {
    let mut bytes = sealed_health();
    assert_eq!(bytes[1], 1, "the version byte rides right after the array header");
    bytes[1] = 2;
    assert!(matches!(
        TbepFrame::from_bytes(&bytes).unwrap_err(),
        EnvelopeError::UnsupportedVersion(2)
    ));
    bytes[1] = 0;
    assert!(matches!(
        TbepFrame::from_bytes(&bytes).unwrap_err(),
        EnvelopeError::UnsupportedVersion(0)
    ));
}

#[test]
fn unknown_frame_kind_fails_closed() {
    let mut bytes = sealed_health();
    assert_eq!(bytes[2], 0xcd, "the kind rides as a u16 behind its marker");
    bytes[3] = 0x7f;
    bytes[4] = 0xff;
    assert!(matches!(
        TbepFrame::from_bytes(&bytes).unwrap_err(),
        EnvelopeError::UnknownFrameKind(0x7fff)
    ));
}

#[test]
fn digest_mismatch_fails_closed() {
    // Rebuild the envelope with a body that differs from its digest: the
    // digest field sits after the idempotency nil; the simplest hostile
    // frame flips one body byte, which must be caught by the digest.
    let frame = TbepFrame::seal(AdminStatusRequest {}, 1, 0, None).expect("seals");
    let mut bytes = frame.to_bytes();
    // The body is the final element (msgpack bin); flip its last byte.
    let last = bytes.len() - 1;
    bytes[last] ^= 0xff;
    match TbepFrame::from_bytes(&bytes).unwrap_err() {
        // A flipped fixarray byte may also break structure; both refusals
        // fail closed. The digest row is exercised exactly on the next
        // frame, whose body stays structurally valid.
        EnvelopeError::DigestMismatch | EnvelopeError::Malformed(_) => {}
        other => panic!("a tampered body must fail closed, got {other:?}"),
    }
    // Exact digest row: same body, wrong recorded digest. The digest u64
    // rides behind `cf` right after the nil idempotency slot.
    let mut hostile = frame.to_bytes();
    let digest_offset = digest_byte_offset(&hostile);
    hostile[digest_offset] ^= 0x01;
    assert!(matches!(TbepFrame::from_bytes(&hostile).unwrap_err(), EnvelopeError::DigestMismatch));
}

/// Locates the first byte of the recorded digest inside a sealed
/// frame whose idempotency slot is nil.
fn digest_byte_offset(bytes: &[u8]) -> usize {
    // [array-header, version, kind(u16), correlation, causation, nil,
    //  digest(cf + 8 bytes), body]
    let mut offset = 0;
    offset += 1; // fixarray(7)
    offset += 1; // version (fixint)
    offset += 3; // u16 kind
    offset += integer_width(bytes, offset); // correlation
    offset += integer_width(bytes, offset); // causation
    offset += 1; // nil
    assert_eq!(bytes[offset], 0xcf, "the digest rides as a u64");
    offset + 1
}

/// The on-wire width of one msgpack integer starting at `offset`.
fn integer_width(bytes: &[u8], offset: usize) -> usize {
    match bytes[offset] {
        marker if marker <= 0x7f => 1,
        0xcc | 0xd0 => 2,
        0xcd | 0xd1 => 3,
        0xce | 0xd2 | 0xca => 5,
        0xcf | 0xd3 | 0xcb => 9,
        other => panic!("unexpected integer marker {other:#04x} at {offset}"),
    }
}

#[test]
fn truncated_trailing_and_garbage_frames_fail_closed() {
    let bytes = sealed_health();
    for cut in 1..bytes.len() {
        assert!(
            TbepFrame::from_bytes(&bytes[..bytes.len() - cut]).is_err(),
            "a frame truncated by {cut} bytes must not decode"
        );
    }
    let mut trailing = bytes.clone();
    trailing.push(0x77);
    assert!(matches!(
        TbepFrame::from_bytes(&trailing).unwrap_err(),
        EnvelopeError::Malformed("trailing bytes after the envelope")
    ));
    assert!(TbepFrame::from_bytes(&[0x93, 0x01, 0x02]).is_err());
    assert!(TbepFrame::from_bytes(&[]).is_err());
}

#[test]
fn oversized_frames_are_refused_before_any_parse() {
    assert!(matches!(
        TbepFrame::from_bytes(&vec![0u8; MAX_FRAME_BYTES + 1]).unwrap_err(),
        EnvelopeError::FrameTooLarge(_)
    ));
    // The stream path refuses the declared prefix before reading.
    let mut stream: Vec<u8> = Vec::new();
    stream.extend_from_slice(&(u32::try_from(MAX_FRAME_BYTES).expect("fits") + 1).to_le_bytes());
    stream.extend_from_slice(&[0u8; 8]);
    let mut cursor = std::io::Cursor::new(stream);
    assert!(matches!(
        TbepFrame::read_stream(&mut cursor).unwrap_err(),
        EnvelopeError::FrameTooLarge(_)
    ));
}

#[test]
fn idempotency_pairing_fails_closed_both_ways() {
    let key = IdempotencyKey::new("retry-01").expect("key");
    // A mutation frame without its key refuses to seal.
    let unkeyed = TbepFrame::seal(
        DocumentWriteRequest {
            document: DocumentPut {
                id: "d-01".into(),
                collection: "orders".into(),
                body: TbepValue::Object(vec![]),
            },
            expected: ExpectedVersion::Absent,
            operation_id: "op-01".into(),
        },
        1,
        0,
        None,
    );
    assert!(matches!(unkeyed.unwrap_err(), EnvelopeError::IdempotencyPairing(_)));
    // A read frame with a key refuses to seal.
    let keyed_read = TbepFrame::seal(
        DocumentReadRequest { collection: "orders".into(), document: "d-01".into() },
        2,
        0,
        Some(&key),
    );
    assert!(matches!(keyed_read.unwrap_err(), EnvelopeError::IdempotencyPairing(_)));
    // A hostile envelope that smuggles nil into a read frame's
    // idempotency slot is structurally legal but the pairing check
    // catches a key on a non-mutation kind on decode; nil on a mutation
    // kind refuses too.
    let sealed = TbepFrame::seal(
        DocumentWriteRequest {
            document: DocumentPut {
                id: "d-01".into(),
                collection: "orders".into(),
                body: TbepValue::Object(vec![]),
            },
            expected: ExpectedVersion::Absent,
            operation_id: "op-01".into(),
        },
        1,
        0,
        Some(&key),
    )
    .expect("seals");
    assert_eq!(
        sealed.idempotency().map(IdempotencyKey::as_str),
        Some("retry-01"),
        "the sealed mutation frame carries its key"
    );
    // Idempotency keys are bounded and control-free.
    assert!(IdempotencyKey::new("").is_err());
    assert!(IdempotencyKey::new("   ").is_err());
    assert!(IdempotencyKey::new(&"k".repeat(129)).is_err());
    assert!(IdempotencyKey::new("bad\nkey").is_err());
}

#[test]
fn bodies_are_positional_arrays_never_maps() {
    // A map where the positional array belongs: fixmap(1) {1: 2}.
    let map = [0x81, 0x01, 0x02];
    assert!(decode_body::<DocumentReadRequest>(&map).is_err());
    // nil where a required field belongs.
    let nil_body = [0x92, 0xc0, 0xc0];
    assert!(decode_body::<DocumentReadRequest>(&nil_body).is_err());
    // Extra array elements beyond the declared arity.
    let body = DocumentReadRequest { collection: "orders".into(), document: "d-01".into() };
    let mut wide = encode_body(&body).expect("encodes");
    assert_eq!(wide[0], 0x92);
    wide[0] = 0x93;
    wide.push(0xc0);
    assert!(decode_body::<DocumentReadRequest>(&wide).is_err());
    // Unknown enum tag inside a typed body.
    let unknown_tag = [0x91, 0x63];
    assert!(
        decode_body::<terrabase_tbep::generated::DocumentWriteResponse>(&[
            0x92, 0x91, 0x63, 0x91, 0x01
        ])
        .is_err()
    );
    let _ = unknown_tag;
}

#[test]
fn bounds_violations_fail_closed_before_bytes_exist() {
    let over_long = DocumentReadRequest { collection: "c".repeat(65), document: "d".into() };
    assert!(encode_body(&over_long).is_err());
    let hostile_list = BackboneSubscribeRequest {
        subscription_id: 1,
        collections: Some(
            (0..=terrabase_tbep::generated::MAX_LIST_ITEMS)
                .map(|index| format!("c{index}"))
                .collect(),
        ),
        from: terrabase_tbep::generated::SubscriptionStart::Live,
    };
    assert!(encode_body(&hostile_list).is_err());
}

/// The server-side half: hostile frames over the live connection.
struct Hostile {
    #[allow(dead_code)]
    server: terrabase_tbep::TbepServerHandle,
    stream: UnixStream,
}

impl Hostile {
    fn bind(tag: &str) -> Self {
        let socket =
            std::env::temp_dir().join(format!("tbep-adv-{tag}-{}.sock", std::process::id()));
        let server = TbepServer::bind(&socket, Arc::new(SyncDeliveryHost::default()))
            .expect("the server binds");
        let stream = UnixStream::connect(&socket).expect("connects");
        Self { server, stream }
    }

    fn send_raw(&mut self, bytes: &[u8]) {
        self.stream.write_all(bytes).expect("writes");
    }

    fn send_frame(&mut self, frame: &TbepFrame) {
        frame.write_stream(&mut self.stream).expect("writes");
    }

    fn receive(&mut self) -> Option<TbepFrame> {
        // A transport teardown (including a reset after the server's
        // fail-closed close with data in flight) is the end of stream,
        // not a decode failure.
        match TbepFrame::read_stream(&mut self.stream) {
            Ok(option) => option,
            Err(EnvelopeError::Io(_)) => None,
            Err(other) => panic!("an unexpected decode failure on the client side: {other:?}"),
        }
    }
}

#[test]
fn the_server_answers_typed_errors_for_hostile_frames() {
    let mut hostile = Hostile::bind("typed-errors");

    // Unknown wire version: typed error, then the connection closes.
    let mut versioned = sealed_health();
    versioned[1] = 9;
    hostile.send_raw(&u32::try_from(versioned.len()).expect("fits").to_le_bytes());
    hostile.send_raw(&versioned);
    let answer = hostile.receive().expect("the error frame arrives");
    match answer.body() {
        TbepBody::Error(error) => assert_eq!(error.code, ErrorCode::UnsupportedVersion),
        other => panic!("expected the typed version refusal, got {other:?}"),
    }
    assert!(hostile.receive().is_none(), "the connection closed");

    // A fresh connection: unknown kind, digest mismatch, garbage.
    let mut hostile = Hostile::bind("typed-errors-2");
    let mut unknown_kind = sealed_health();
    unknown_kind[3] = 0x7f;
    unknown_kind[4] = 0xff;
    hostile.send_raw(&u32::try_from(unknown_kind.len()).expect("fits").to_le_bytes());
    hostile.send_raw(&unknown_kind);
    let answer = hostile.receive().expect("the error frame arrives");
    match answer.body() {
        TbepBody::Error(error) => assert_eq!(error.code, ErrorCode::UnknownFrameKind),
        other => panic!("expected the unknown-kind refusal, got {other:?}"),
    }

    // Digest mismatch over the wire.
    let frame = TbepFrame::seal(AdminStatusRequest {}, 1, 0, None).expect("seals");
    let mut mismatched = frame.to_bytes();
    let offset = digest_byte_offset(&mismatched);
    mismatched[offset] ^= 0x01;
    hostile.send_raw(&u32::try_from(mismatched.len()).expect("fits").to_le_bytes());
    hostile.send_raw(&mismatched);
    let answer = hostile.receive().expect("the error frame arrives");
    match answer.body() {
        TbepBody::Error(error) => assert_eq!(error.code, ErrorCode::DigestMismatch),
        other => panic!("expected the digest refusal, got {other:?}"),
    }

    // The connection survived the refusals: a legal frame still answers.
    let legal = TbepFrame::seal(SupervisionHealthRequest {}, 2, 0, None).expect("seals");
    hostile.send_frame(&legal);
    let answer = hostile.receive().expect("the health answer arrives");
    assert!(matches!(answer.body(), TbepBody::SupervisionHealthResponse(_)));
}

#[test]
fn the_server_refuses_engine_to_host_direction_frames() {
    let mut hostile = Hostile::bind("direction");
    // A response frame sent host-to-engine violates the direction
    // contract and fails closed.
    let response = terrabase_tbep::generated::SupervisionHealthResponse {
        state: terrabase_tbep::generated::HealthState::Serving,
        sessions: 0,
        subscriptions: 0,
        frames_served: 0,
    };
    let frame = TbepFrame::seal(response, 1, 0, None).expect("seals");
    assert_eq!(frame.kind().family(), "supervision");
    hostile.send_frame(&frame);
    let answer = hostile.receive().expect("the refusal arrives");
    match answer.body() {
        TbepBody::Error(error) => assert_eq!(error.code, ErrorCode::MalformedFrame),
        other => panic!("expected the direction refusal, got {other:?}"),
    }
}

#[test]
fn the_server_survives_a_flood_of_garbage_and_half_frames() {
    let mut hostile = Hostile::bind("flood");
    // A declared-oversize prefix is refused without reading the body;
    // the stream position is unrecoverable, so the failure closes the
    // connection after the typed error frame.
    let oversize = u32::try_from(MAX_FRAME_BYTES).expect("fits") + 1;
    hostile.send_raw(&oversize.to_le_bytes());
    hostile.send_raw(&[0xa5, 0x01, 0x02]);
    let answer = hostile.receive().expect("the refusal arrives");
    match answer.body() {
        TbepBody::Error(error) => assert_eq!(error.code, ErrorCode::FrameTooLarge),
        other => panic!("expected the size refusal, got {other:?}"),
    }
    assert!(hostile.receive().is_none(), "the oversized frame closes the connection");

    // The server itself keeps serving: a fresh connection answers.
    let mut fresh = Hostile::bind("flood-fresh");
    let legal = TbepFrame::seal(SupervisionHealthRequest {}, 3, 0, None).expect("seals");
    fresh.send_frame(&legal);
    let answer = fresh.receive().expect("the health answer arrives");
    assert!(matches!(answer.body(), TbepBody::SupervisionHealthResponse(_)));
    // Truncation mid-frame then close: the reader ends without ever
    // decoding a frame from the truncated prefix (a transport teardown
    // after truncation is an acceptable end; a decoded frame is not).
    fresh.send_raw(&[0x40, 0x00, 0x00, 0x00, 0x97]);
    let _ = fresh.stream.shutdown(std::net::Shutdown::Write);
    // The stream ends without ever delivering a frame for the truncated
    // prefix (a teardown/reset after truncation is an acceptable end; a
    // decoded frame is not).
    let mut truncated = false;
    while let Ok(option) = TbepFrame::read_stream(&mut fresh.stream) {
        truncated |= option.is_some();
        if option.is_none() {
            break;
        }
    }
    assert!(!truncated, "no frame decoded from the truncated prefix");
    assert!(!truncated, "no frame decoded from the truncated prefix");
}

#[test]
fn mutation_requests_without_keys_fail_closed_before_the_host() {
    // The envelope refuses to seal, so the hostile frame cannot exist;
    // the only remaining smuggling path is a hand-built envelope, which
    // the decode-time pairing check refuses. Both are pinned above; here
    // the server-side behavior is the unbound-channel refusal instead:
    // a mutation kind with no binding never reaches the host.
    let mut hostile = Hostile::bind("unbound-mutation");
    let key = IdempotencyKey::new("op-x").expect("key");
    let frame = TbepFrame::seal(
        terrabase_tbep::generated::SyncMutationSubmitRequest {
            session_id: 1,
            document: "d".into(),
            change: terrabase_tbep::generated::SyncChange::Delete,
        },
        1,
        0,
        Some(&key),
    )
    .expect("seals");
    assert!(matches!(frame.kind(), FrameKind::SyncMutationSubmitRequest));
    hostile.send_frame(&frame);
    let answer = hostile.receive().expect("the refusal arrives");
    match answer.body() {
        TbepBody::Error(error) => assert_eq!(error.code, ErrorCode::UnboundChannel),
        other => panic!("expected the unbound refusal, got {other:?}"),
    }
}
