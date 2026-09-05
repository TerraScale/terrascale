//! Golden and hostile vectors for the public `MessagePack` wire.

use terrabase_sync_core::wire::{
    BackpressureMode, Envelope, ErrorCode, FieldChange, Message, ProjectedField, ResetReason,
    ResultItem, ResultOperation, Snapshot, WireError, decode_envelope, encode_envelope,
};

fn bytes(hex: &str) -> Vec<u8> {
    assert_eq!(hex.len() % 2, 0);
    hex.as_bytes()
        .as_chunks::<2>()
        .0
        .iter()
        .map(|pair| u8::from_str_radix(std::str::from_utf8(pair).expect("hex"), 16).expect("hex"))
        .collect()
}

fn envelope(correlation: u64, causation: u64, message: Message) -> Envelope {
    Envelope { features: 0, correlation, causation, message }
}

fn field(path: &str, value: &[u8]) -> ProjectedField {
    ProjectedField { path: path.to_owned(), value: value.to_vec() }
}

fn snapshot(
    view_generation: u64,
    authorization_generation: u64,
    revision: u64,
    checkpoint: u8,
) -> Snapshot {
    Snapshot {
        view_generation,
        authorization_generation,
        revision,
        items: vec![ResultItem { identity: "d".to_owned(), fields: vec![field("x", b"y")] }],
        digest: "a".repeat(64),
        checkpoint: vec![checkpoint],
    }
}

#[test]
fn checked_in_golden_vectors_match_the_typescript_codec() {
    let vectors = vec![
        (
            1,
            0,
            Message::Subscribe {
                subscription: "s".to_owned(),
                query: "q".to_owned(),
                parameters: vec![1, 2],
                checkpoint: None,
            },
            "9701000001010094a173a171c4020102c0",
        ),
        (2, 1, Message::Unsubscribe { subscription: "s".to_owned() }, "9701000002020191a173"),
        (
            3,
            2,
            Message::Snapshot { subscription: "s".to_owned(), snapshot: snapshot(1, 2, 3, 0x63) },
            "9701000003030292a173960102039192a1649192a178c40179d94061616161616161616161616161616161616161616161616161616161616161616161616161616161616161616161616161616161616161616161616161616161c40163",
        ),
        (
            4,
            3,
            Message::Delta {
                subscription: "s".to_owned(),
                view_generation: 1,
                from_revision: 3,
                to_revision: 4,
                operations: vec![
                    ResultOperation::Insert {
                        identity: "e".to_owned(),
                        position: 1,
                        fields: vec![field("x", b"i")],
                    },
                    ResultOperation::Update {
                        identity: "d".to_owned(),
                        fields: vec![field("x", b"u")],
                    },
                    ResultOperation::Patch {
                        identity: "d".to_owned(),
                        changes: vec![
                            FieldChange { path: "x".to_owned(), value: Some(vec![0x70]) },
                            FieldChange { path: "z".to_owned(), value: None },
                        ],
                    },
                    ResultOperation::Remove { identity: "e".to_owned() },
                    ResultOperation::Move { identity: "d".to_owned(), position: 0 },
                ],
                digest: "b".repeat(64),
                checkpoint: vec![0x64],
            },
            "9701000004040397a173010304959401a165019192a178c401699302a1649192a178c401759303a1649292a178c4017092a17ac09204a1659305a16400d94062626262626262626262626262626262626262626262626262626262626262626262626262626262626262626262626262626262626262626262626262626262c40164",
        ),
        (
            5,
            4,
            Message::Reset {
                subscription: "s".to_owned(),
                reason: ResetReason::GenerationChanged,
                snapshot: snapshot(2, 2, 5, 0x65),
            },
            "9701000005050493a17303960202059192a1649192a178c40179d94061616161616161616161616161616161616161616161616161616161616161616161616161616161616161616161616161616161616161616161616161616161c40165",
        ),
        (6, 5, Message::Heartbeat, "9701000006060590"),
        (
            7,
            6,
            Message::CheckpointAck { subscription: "s".to_owned(), checkpoint: vec![0x66] },
            "9701000007070692a173c40166",
        ),
        (
            8,
            7,
            Message::AuthorizationChanged {
                subscription: "s".to_owned(),
                authorization_generation: 3,
                removed: vec!["d".to_owned()],
                snapshot: Snapshot { items: Vec::new(), ..snapshot(2, 3, 5, 0x67) },
            },
            "9701000008080794a1730391a1649602030590d94061616161616161616161616161616161616161616161616161616161616161616161616161616161616161616161616161616161616161616161616161616161c40167",
        ),
        (9, 8, Message::RegionRouteChange { region: "FRA".to_owned() }, "9701000009090891a3465241"),
        (
            10,
            9,
            Message::Error { code: ErrorCode::Overloaded, detail: Some("retry".to_owned()) },
            "970100000a0a099207a57265747279",
        ),
        (11, 10, Message::Backpressure { mode: BackpressureMode::Pause }, "970100000b0b0a9101"),
    ];
    for (correlation, causation, message, expected) in vectors {
        let actual =
            encode_envelope(&envelope(correlation, causation, message.clone())).expect("encode");
        assert_eq!(actual, bytes(expected), "golden encoding for tag {correlation}");
        assert_eq!(
            decode_envelope(&actual).expect("decode"),
            envelope(correlation, causation, message)
        );
    }
}

#[test]
fn hostile_shapes_fail_closed_before_allocation() {
    let hostile = [
        ("97010000060100810102", WireError::MapForbidden),
        ("97010000060100d40000", WireError::ExtensionForbidden),
        ("9701000006010091ca3fc00000", WireError::Float32Forbidden),
        ("97010000060100", WireError::Truncated),
        ("9701000006010090c0", WireError::TrailingBytes),
        ("9701010006010090", WireError::UnsupportedMinor(1)),
        ("9701000106010090", WireError::UnknownFeatures(1)),
        ("970100000c010090", WireError::UnknownTag(12)),
        ("970100000a01009263c0", WireError::UnknownEnum("error_code", 99)),
        ("9701000004010097a173010102919107a0c40101", WireError::UnknownVariant(7)),
    ];
    for (hex, expected) in hostile {
        let error = decode_envelope(&bytes(hex)).expect_err(hex);
        assert_eq!(error, expected, "hostile vector {hex}");
    }
}

#[test]
fn alternate_valid_integer_and_array_encodings_decode_but_writer_is_canonical() {
    // The reader accepts valid non-minimal integer/array headers, as does the
    // TypeScript reader, while the writer always emits fix forms where valid.
    let alternate = bytes("97ce00000001cd0000cc00ce00000002cd0001cc00dc0001a173");
    let expected = envelope(1, 0, Message::Unsubscribe { subscription: "s".to_owned() });
    assert_eq!(decode_envelope(&alternate).expect("alternate encoding"), expected);
    assert_eq!(encode_envelope(&expected).expect("canonical"), bytes("9701000002010091a173"));
}

#[test]
fn encode_bounds_reject_overflow_and_invalid_shapes() {
    let too_long = Message::RegionRouteChange { region: "x".repeat(65) };
    assert_eq!(encode_envelope(&envelope(1, 0, too_long)), Err(WireError::StringLimit));
    let empty_path = Message::Snapshot {
        subscription: "s".to_owned(),
        snapshot: Snapshot {
            items: vec![ResultItem { identity: "d".to_owned(), fields: vec![field("", &[])] }],
            ..snapshot(1, 1, 1, 1)
        },
    };
    assert_eq!(encode_envelope(&envelope(1, 0, empty_path)), Err(WireError::StringLimit));
    assert_eq!(
        encode_envelope(&Envelope { features: 1, ..envelope(1, 0, Message::Heartbeat) }),
        Err(WireError::UnknownFeatures(1))
    );
}
