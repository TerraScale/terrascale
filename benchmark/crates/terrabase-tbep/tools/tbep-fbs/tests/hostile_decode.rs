//! Hostile-decode suite for the generated TBEP FlatBuffers bindings
//! (ADR 0025 phase 1, gap-sweep row 27, Rust half).
//!
//! Every hostile class is refused by [`tbep_fbs::decode_envelope`]
//! BEFORE the corresponding field access: the byte-ceiling and
//! size-prefix checks run before any parse, the structural verifier
//! runs before any generated accessor, and the version/feature gates
//! run before the body union is touched. Acceptance rows use both the
//! hand encoder (byte-level independence from the generator) and the
//! generated builders.

use tbep_fbs::hand_encoding::{
    DOCUMENT_WRITE_RESPONSE_TAG, ERROR_FRAME_TAG, HandEnvelope, encode_document_write_response,
    encode_error_frame,
};
use tbep_fbs::tbep_generated::terrabase::tbep as fbs;
use tbep_fbs::{
    DecodeError, MAX_FRAME_BYTES, SIZE_PREFIX_BYTES, TBEP_PROTOCOL_MAJOR, TbepOperation,
    decode_envelope,
};

/// The registry operation code for `error`.
const OP_ERROR: u16 = 1;
/// The registry operation code for `document.write`.
const OP_DOCUMENT_WRITE: u16 = 258;
/// The registry operation code for `document.write`'s response.
const OP_DOCUMENT_WRITE_RESPONSE: u16 = 514;

fn error_envelope(major: u16) -> Vec<u8> {
    let mut spec = HandEnvelope::new(OP_ERROR, ERROR_FRAME_TAG, encode_error_frame(18, "skew"));
    spec.protocol_major = major;
    spec.encode()
}

fn envelope_table_start(bytes: &[u8]) -> usize {
    let root = u32::from_le_bytes(
        bytes[SIZE_PREFIX_BYTES..SIZE_PREFIX_BYTES + 4]
            .try_into()
            .expect("the hand vector has a root offset"),
    ) as usize;
    SIZE_PREFIX_BYTES + root
}

fn builder_error_envelope_without_identifier() -> Vec<u8> {
    use flatbuffers::FlatBufferBuilder;

    let mut builder = FlatBufferBuilder::new();
    let detail = builder.create_string("identifier required");
    let body = fbs::ErrorFrame::create(
        &mut builder,
        &fbs::ErrorFrameArgs { code: fbs::ErrorCode::MALFORMED_FRAME, detail: Some(detail) },
    );
    let envelope = fbs::TbepEnvelope::create(
        &mut builder,
        &fbs::TbepEnvelopeArgs {
            protocol_major: TBEP_PROTOCOL_MAJOR,
            operation: TbepOperation::ERROR,
            correlation: Some(&fbs::Ulid([1_u8; 16])),
            causation: Some(&fbs::Ulid([2_u8; 16])),
            body_type: fbs::TbepFrame::ErrorFrame,
            body: Some(body.as_union_value()),
            ..Default::default()
        },
    );
    builder.finish_size_prefixed(envelope, None);
    builder.finished_data().to_vec()
}

#[test]
fn accepts_a_hand_encoded_valid_vector() {
    let bytes = HandEnvelope::new(
        OP_ERROR,
        ERROR_FRAME_TAG,
        encode_error_frame(21, "cold tier unavailable"),
    )
    .encode();
    let frame = decode_envelope(&bytes).expect("the hand-encoded error frame decodes");
    let envelope = frame.envelope;
    assert_eq!(envelope.protocol_major(), TBEP_PROTOCOL_MAJOR);
    assert_eq!(envelope.protocol_minor(), 0);
    assert_eq!(envelope.operation(), TbepOperation::ERROR);
    assert_eq!(envelope.correlation().and_then(|ulid| ulid.bytes().iter().next()), Some(0),);
    assert_eq!(envelope.causation().and_then(|ulid| ulid.bytes().iter().next()), Some(7),);
    let body = envelope.body_as_error_frame().expect("error body");
    assert_eq!(body.code(), fbs::ErrorCode::COLD_TIER_UNAVAILABLE);
    assert_eq!(body.detail(), Some("cold tier unavailable"));
}

#[test]
fn accepts_a_builder_built_vector_with_nested_values() {
    use flatbuffers::FlatBufferBuilder;

    let mut builder = FlatBufferBuilder::new();
    let note = builder.create_string("first");
    let note = fbs::ValueString::create(&mut builder, &fbs::ValueStringArgs { value: Some(note) });
    let count = fbs::ValueInteger::create(&mut builder, &fbs::ValueIntegerArgs { value: 2 });
    let ok = fbs::ValueBoolean::create(&mut builder, &fbs::ValueBooleanArgs { value: true });
    let note_cell = fbs::ValueCell::create(
        &mut builder,
        &fbs::ValueCellArgs {
            value_type: fbs::TbepValue::ValueString,
            value: Some(note.as_union_value()),
        },
    );
    let count_cell = fbs::ValueCell::create(
        &mut builder,
        &fbs::ValueCellArgs {
            value_type: fbs::TbepValue::ValueInteger,
            value: Some(count.as_union_value()),
        },
    );
    let ok_cell = fbs::ValueCell::create(
        &mut builder,
        &fbs::ValueCellArgs {
            value_type: fbs::TbepValue::ValueBoolean,
            value: Some(ok.as_union_value()),
        },
    );
    let items = builder.create_vector(&[note_cell, count_cell, ok_cell]);
    let array = fbs::ValueArray::create(&mut builder, &fbs::ValueArrayArgs { items: Some(items) });
    let array_cell = fbs::ValueCell::create(
        &mut builder,
        &fbs::ValueCellArgs {
            value_type: fbs::TbepValue::ValueArray,
            value: Some(array.as_union_value()),
        },
    );
    let id = builder.create_string("d-01");
    let collection = builder.create_string("orders");
    let document = fbs::DocumentPut::create(
        &mut builder,
        &fbs::DocumentPutArgs {
            id: Some(id),
            collection: Some(collection),
            body_type: fbs::TbepValue::ValueArray,
            body: Some(array.as_union_value()),
        },
    );
    let exact = fbs::ExpectedVersionExact::create(
        &mut builder,
        &fbs::ExpectedVersionExactArgs { version: 7 },
    );
    let operation_id = builder.create_string("op-01");
    let request = fbs::DocumentWriteRequest::create(
        &mut builder,
        &fbs::DocumentWriteRequestArgs {
            document: Some(document),
            expected_type: fbs::ExpectedVersion::ExpectedVersionExact,
            expected: Some(exact.as_union_value()),
            operation_id: Some(operation_id),
            ack: fbs::Acknowledgement::REGIONAL_QUORUM,
        },
    );
    let mut correlation = [0_u8; 16];
    for (index, byte) in correlation.iter_mut().enumerate() {
        *byte = 0xA0 + index as u8;
    }
    let envelope = fbs::TbepEnvelope::create(
        &mut builder,
        &fbs::TbepEnvelopeArgs {
            protocol_major: TBEP_PROTOCOL_MAJOR,
            protocol_minor: 0,
            required_features: Default::default(),
            operation: TbepOperation::DOCUMENT_WRITE,
            correlation: Some(&fbs::Ulid(correlation)),
            causation: Some(&fbs::Ulid([9_u8; 16])),
            body_type: fbs::TbepFrame::DocumentWriteRequest,
            body: Some(request.as_union_value()),
        },
    );
    fbs::finish_size_prefixed_tbep_envelope_buffer(&mut builder, envelope);

    let bytes = builder.finished_data();
    let frame = decode_envelope(bytes).expect("the builder-built frame decodes");
    let decoded = frame.envelope;
    assert_eq!(decoded.operation(), TbepOperation::DOCUMENT_WRITE);
    let body = decoded.body_as_document_write_request().expect("write body");
    assert_eq!(body.operation_id(), Some("op-01"));
    assert_eq!(body.ack(), fbs::Acknowledgement::REGIONAL_QUORUM);
    let exact = body.expected_as_expected_version_exact().expect("exact");
    assert_eq!(exact.version(), 7);
    let document = body.document().expect("document");
    assert_eq!(document.id(), Some("d-01"));
    assert_eq!(document.collection(), Some("orders"));
    let array = document.body_as_value_array().expect("array body");
    let items = array.items().expect("items");
    assert_eq!(items.len(), 3);
    assert_eq!(items.get(0).value_as_value_string().and_then(|s| s.value()), Some("first"));
    assert_eq!(items.get(1).value_as_value_integer().map(|i| i.value()), Some(2));
    assert_eq!(items.get(2).value_as_value_boolean().map(|b| b.value()), Some(true));
    // Nothing nested under the array_cell escapes the typed union.
    let _ = array_cell;
}

#[test]
fn rejects_every_truncation_of_a_valid_vector() {
    let bytes = error_envelope(TBEP_PROTOCOL_MAJOR);
    for cut in 0..bytes.len() {
        let result = decode_envelope(&bytes[..cut]);
        assert!(
            matches!(
                result,
                Err(DecodeError::Empty
                    | DecodeError::Truncated { .. }
                    | DecodeError::Structural(_))
            ),
            "truncation at {cut} bytes must fail closed before field access, got {result:?}"
        );
    }
    let full = decode_envelope(&bytes);
    assert!(full.is_ok(), "the uncut vector still decodes: {full:?}");
}

#[test]
fn rejects_trailing_bytes_after_one_complete_buffer() {
    let mut bytes = error_envelope(TBEP_PROTOCOL_MAJOR);
    bytes.extend_from_slice(&[0xDE, 0xAD, 0xBE, 0xEF]);
    assert_eq!(
        decode_envelope(&bytes).unwrap_err(),
        DecodeError::Trailing { declared: bytes.len() - 4 - 4, actual: bytes.len() - 4 }
    );
}

#[test]
fn rejects_an_oversized_frame_before_any_parse() {
    let oversized = MAX_FRAME_BYTES + SIZE_PREFIX_BYTES + 1;
    let bytes = vec![0xAA; oversized];
    assert_eq!(decode_envelope(&bytes).unwrap_err(), DecodeError::Oversized { len: oversized - 4 });
}

#[test]
fn rejects_an_invalid_root_offset_before_field_access() {
    let mut bytes = error_envelope(TBEP_PROTOCOL_MAJOR);
    // The root offset lives at the first byte after the size prefix.
    bytes[SIZE_PREFIX_BYTES..SIZE_PREFIX_BYTES + 4].copy_from_slice(&0xFFFF_0000_u32.to_le_bytes());
    assert!(matches!(decode_envelope(&bytes), Err(DecodeError::Structural(_))));
}

#[test]
fn rejects_an_invalid_body_offset_before_field_access() {
    let mut bytes = error_envelope(TBEP_PROTOCOL_MAJOR);
    let field = envelope_table_start(&bytes) + 52;
    bytes[field..field + 4].copy_from_slice(&0x7FFF_0000_u32.to_le_bytes());
    assert!(matches!(decode_envelope(&bytes), Err(DecodeError::Structural(_))));
}

#[test]
fn rejects_a_misaligned_nested_union_offset() {
    // The outcome uoffset is relative to its own aligned location. An
    // offset of one targets a misaligned nested table.
    let body = encode_document_write_response(1, 1);
    let bytes =
        HandEnvelope::new(OP_DOCUMENT_WRITE_RESPONSE, DOCUMENT_WRITE_RESPONSE_TAG, body).encode();
    let result = decode_envelope(&bytes);
    assert!(
        matches!(result, Err(DecodeError::Structural(_))),
        "the misaligned nested union table must fail structural verification, got {result:?}"
    );
}

#[test]
fn rejects_an_out_of_bounds_nested_vtable_soffset() {
    let mut body = encode_document_write_response(1, 8);
    // The body table begins at body-relative offset eight. Its signed
    // vtable offset must resolve backward inside the frame.
    body[8..12].copy_from_slice(&i32::MAX.to_le_bytes());
    let bytes =
        HandEnvelope::new(OP_DOCUMENT_WRITE_RESPONSE, DOCUMENT_WRITE_RESPONSE_TAG, body).encode();
    let result = decode_envelope(&bytes);
    assert!(
        matches!(result, Err(DecodeError::Structural(_))),
        "the out-of-bounds nested vtable must fail structural verification, got {result:?}"
    );
}

#[test]
fn rejects_an_unknown_union_tag_before_field_access() {
    let mut bytes = error_envelope(TBEP_PROTOCOL_MAJOR);
    // body_type byte at envelope table offset +50.
    let body_type = envelope_table_start(&bytes) + 50;
    bytes[body_type] = 0xC7;
    assert_eq!(decode_envelope(&bytes).unwrap_err(), DecodeError::UnknownBody(0xC7));
}

#[test]
fn rejects_version_skew_before_any_body_access() {
    // A fully valid body under a future major: the version gate fires.
    let bytes = error_envelope(9);
    assert_eq!(
        decode_envelope(&bytes).unwrap_err(),
        DecodeError::VersionSkew { major: 9, minor: 0 }
    );
}

#[test]
fn version_skew_wins_over_body_mismatch_but_not_over_structure() {
    // Version skew with a body member that does not match the
    // operation still reports the version error: the version gate runs
    // before the operation/body pairing.
    let mut spec =
        HandEnvelope::new(OP_DOCUMENT_WRITE, ERROR_FRAME_TAG, encode_error_frame(18, "skew"));
    spec.protocol_major = 9;
    let bytes = spec.encode();
    assert_eq!(
        decode_envelope(&bytes).unwrap_err(),
        DecodeError::VersionSkew { major: 9, minor: 0 }
    );
    // Structural corruption is still found first, because the verifier
    // runs before the version gate.
    let mut corrupt = error_envelope(9);
    let body_offset = envelope_table_start(&corrupt) + 52;
    corrupt[body_offset..body_offset + 4].copy_from_slice(&0x7FFF_0000_u32.to_le_bytes());
    assert!(matches!(decode_envelope(&corrupt), Err(DecodeError::Structural(_))));
}

#[test]
fn rejects_an_unknown_required_feature_bit_before_body_access() {
    let mut spec = HandEnvelope::new(OP_ERROR, ERROR_FRAME_TAG, encode_error_frame(1, "x"));
    spec.required_features = 1_u64 << 63;
    let bytes = spec.encode();
    assert_eq!(
        decode_envelope(&bytes).unwrap_err(),
        DecodeError::UnsupportedFeature { unknown: 1_u64 << 63 }
    );
}

#[test]
fn rejects_a_missing_correlation_ulid() {
    let mut spec = HandEnvelope::new(OP_ERROR, ERROR_FRAME_TAG, encode_error_frame(1, "x"));
    spec.correlation_present = false;
    assert_eq!(decode_envelope(&spec.encode()).unwrap_err(), DecodeError::MissingCorrelation);
}

#[test]
fn rejects_a_missing_causation_ulid() {
    let mut spec = HandEnvelope::new(OP_ERROR, ERROR_FRAME_TAG, encode_error_frame(1, "x"));
    spec.causation_present = false;
    assert_eq!(decode_envelope(&spec.encode()).unwrap_err(), DecodeError::MissingCausation);
}

#[test]
fn rejects_an_absent_body() {
    let mut spec = HandEnvelope::new(OP_ERROR, ERROR_FRAME_TAG, encode_error_frame(1, "x"));
    spec.body = None;
    assert_eq!(decode_envelope(&spec.encode()).unwrap_err(), DecodeError::MissingBody);
}

#[test]
fn rejects_an_unknown_operation_code() {
    let bytes = HandEnvelope::new(4000, ERROR_FRAME_TAG, encode_error_frame(1, "x")).encode();
    assert_eq!(decode_envelope(&bytes).unwrap_err(), DecodeError::UnknownOperation(4000));
}

#[test]
fn rejects_a_body_member_that_does_not_match_the_operation() {
    let bytes =
        HandEnvelope::new(OP_DOCUMENT_WRITE, ERROR_FRAME_TAG, encode_error_frame(1, "x")).encode();
    assert_eq!(
        decode_envelope(&bytes).unwrap_err(),
        DecodeError::OperationBodyMismatch { operation: OP_DOCUMENT_WRITE, body: "ErrorFrame" }
    );
}

#[test]
fn rejects_a_size_prefixed_buffer_without_the_tbep_identifier() {
    let bytes = builder_error_envelope_without_identifier();
    assert_eq!(decode_envelope(&bytes).unwrap_err(), DecodeError::InvalidIdentifier);
}

#[test]
fn rejects_a_corrupt_tbep_identifier() {
    let mut bytes = error_envelope(TBEP_PROTOCOL_MAJOR);
    let identifier = SIZE_PREFIX_BYTES + core::mem::size_of::<u32>();
    assert_eq!(&bytes[identifier..identifier + 4], b"TBEP");
    bytes[identifier] = b'X';
    assert_eq!(decode_envelope(&bytes).unwrap_err(), DecodeError::InvalidIdentifier);
}
