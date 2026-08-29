#![allow(missing_docs)]

use std::fmt::Write;

use terrabase_causal::{
    ActorId, CausalContext, Dot, DottedVersion, HlcTimestamp, MutationOrdinal, OriginCounter,
    ReplicaEpoch, ReplicaId,
};
use terrabase_crdt::{
    Candidate, DecodeError, DecodeLimits, LwwRegister, MvRegister, RegisterCodec,
};

fn actor(byte: u8) -> ActorId {
    ActorId::new(ReplicaId::from_bytes([byte; 16]), ReplicaEpoch::new(1).unwrap())
}

fn candidate(
    byte: u8,
    counter: u64,
    context: CausalContext,
    hlc: u64,
    value: Option<&str>,
) -> Candidate<String> {
    Candidate::new(
        value.map(str::to_owned),
        DottedVersion::new(context, Dot::new(actor(byte), OriginCounter::new(counter).unwrap())),
        MutationOrdinal::new(0),
        HlcTimestamp::new(hlc, 0),
    )
}

#[test]
fn lww_and_mv_round_trip_their_canonical_bytes() {
    let lww = LwwRegister::from_candidate(candidate(1, 1, CausalContext::new(), 7, Some("ok")));
    let mv = MvRegister::from_candidate(candidate(2, 1, CausalContext::new(), 8, Some("other")))
        .join(&MvRegister::from_candidate(candidate(3, 1, CausalContext::new(), 9, None)));

    let lww_bytes = RegisterCodec::encode_lww(&lww);
    let mv_bytes = RegisterCodec::encode_mv(&mv);
    assert_eq!(
        RegisterCodec::encode_lww(
            &RegisterCodec::decode_lww::<String>(&lww_bytes, DecodeLimits::default()).unwrap()
        ),
        lww_bytes
    );
    assert_eq!(
        RegisterCodec::encode_mv(
            &RegisterCodec::decode_mv::<String>(&mv_bytes, DecodeLimits::default()).unwrap()
        ),
        mv_bytes
    );
    assert_eq!(
        RegisterCodec::decode_lww::<String>(&mv_bytes, DecodeLimits::default()),
        Err(DecodeError::WrongDatatype)
    );
}

#[test]
fn codec_rejects_trailing_bytes_versions_and_decode_limits() {
    let state = LwwRegister::from_candidate(candidate(1, 1, CausalContext::new(), 7, Some("ok")));
    let bytes = RegisterCodec::encode_lww(&state);
    let mut trailing = bytes.clone();
    trailing.push(0);
    assert_eq!(
        RegisterCodec::decode_lww::<String>(&trailing, DecodeLimits::default()),
        Err(DecodeError::TrailingBytes)
    );

    let mut wrong_version = bytes.clone();
    wrong_version[0] = 2;
    assert_eq!(
        RegisterCodec::decode_lww::<String>(&wrong_version, DecodeLimits::default()),
        Err(DecodeError::UnsupportedVersion)
    );

    let limits = DecodeLimits { max_bytes: bytes.len() - 1, ..DecodeLimits::default() };
    assert_eq!(
        RegisterCodec::decode_lww::<String>(&bytes, limits),
        Err(DecodeError::LimitExceeded)
    );

    let mut nonminimal_count = bytes.clone();
    nonminimal_count[3] = 0x81;
    nonminimal_count.insert(4, 0);
    assert_eq!(
        RegisterCodec::decode_lww::<String>(&nonminimal_count, DecodeLimits::default()),
        Err(DecodeError::InvalidLeb128)
    );

    let limits = DecodeLimits { max_value_bytes: 1, ..DecodeLimits::default() };
    assert_eq!(
        RegisterCodec::decode_lww::<String>(&bytes, limits),
        Err(DecodeError::LimitExceeded)
    );
}

#[test]
fn golden_lww_bytes_are_stable() {
    let state = LwwRegister::from_candidate(candidate(1, 1, CausalContext::new(), 7, Some("ok")));
    let canonical = hex(&RegisterCodec::encode_lww(&state));
    let hash = hex(&state.canonical_hash().unwrap());
    assert_eq!(
        canonical,
        "010104010101010101010101010101010101010101010001010101010101010101010101010101010101010000070004026f6b"
    );
    assert_eq!(hash, "866263f04a78d00f2577cf83c3483dfca0b2ccf24a27b11f1e5e8b7d176602ce");
    let vector = std::fs::read_to_string(concat!(
        env!("CARGO_MANIFEST_DIR"),
        "/vectors/registers/lww-string.json"
    ))
    .unwrap();
    assert!(
        vector.contains("terrabase-native/1")
            && vector.contains(&canonical)
            && vector.contains(&hash)
    );
}

#[test]
fn golden_mv_bytes_are_stable() {
    let state = MvRegister::from_candidate(candidate(2, 1, CausalContext::new(), 8, Some("other")))
        .join(&MvRegister::from_candidate(candidate(3, 1, CausalContext::new(), 9, None)));
    let canonical = hex(&RegisterCodec::encode_mv(&state));
    let hash = hex(&state.canonical_hash().unwrap());
    assert_eq!(
        canonical,
        "01020402020202020202020202020202020202020101000303030303030303030303030303030301010002010202020202020202020202020202020201010000080004056f746865720003030303030303030303030303030303010100000900"
    );
    assert_eq!(hash, "38b09207ca08f5346519558cd5abe9cbd79258427af001661caa3bc8d0e5e9f8");
    let vector = std::fs::read_to_string(concat!(
        env!("CARGO_MANIFEST_DIR"),
        "/vectors/registers/mv-string-remove.json"
    ))
    .unwrap();
    assert!(
        vector.contains("terrabase-native/1")
            && vector.contains(&canonical)
            && vector.contains(&hash)
    );
}

fn hex(bytes: &[u8]) -> String {
    let mut output = String::with_capacity(bytes.len() * 2);
    for byte in bytes {
        write!(output, "{byte:02x}").unwrap();
    }
    output
}
