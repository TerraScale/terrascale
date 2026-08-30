//! The TBEP FlatBuffers golden-vector runner (gap-sweep row 27).
//!
//! `vectors/tbep/fbs/` is the committed corpus for the private generated
//! FlatBuffers surface of ADR 0025, beside (and deliberately disjoint
//! from) the positional-array corpus in `vectors/tbep/*.json`, which
//! belongs to the codec still served today. Two files:
//!
//! - `hostile.json` — one row per named hostile class, each pinned as
//!   exact bytes and each naming the typed refusal it must produce;
//! - `valid.json` — golden valid request vectors for every operation the
//!   production decoder serves, grouped into channels so the live QUIC
//!   lane (`tests/quic_server.rs` in `terrabase-tbep`) can exchange every
//!   one of them in order.
//!
//! Both files record [`PINNED_SCHEMA_DIGEST`] in their header, so the
//! golden bytes and the generated bindings are bound to one exact schema
//! revision: a corpus recorded against any other revision refuses to
//! replay before a single byte is decoded.
//!
//! Regeneration: `cargo test --test fbs_vectors -- --ignored
//! regenerate_corpus` rewrites both files from the typed scenarios at the
//! bottom of this file. The committed files are the golden record.

use std::fs;
use std::path::PathBuf;

use serde_json::{Value, json};
use tbep_fbs::hand_encoding::{
    DOCUMENT_WRITE_RESPONSE_TAG, ERROR_FRAME_TAG, HandEnvelope, encode_document_write_response,
    encode_error_frame,
};
use tbep_fbs::production::decode_request;
use tbep_fbs::schema_identity::{PINNED_SCHEMA_DIGEST, PinnedInputs};
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

fn crate_root() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../..")
}

fn corpus_dir() -> PathBuf {
    crate_root().join("vectors/tbep/fbs")
}

// ---------------------------------------------------------------------------
// Corpus loading and header discipline
// ---------------------------------------------------------------------------

fn load(name: &str) -> Value {
    let text = fs::read_to_string(corpus_dir().join(name))
        .unwrap_or_else(|error| panic!("the corpus file {name} reads: {error}"));
    serde_json::from_str(&text)
        .unwrap_or_else(|error| panic!("the corpus file {name} is JSON: {error}"))
}

/// Every corpus file names the schema revision it was recorded against,
/// and that name must be the pinned revision of this working tree.
fn assert_bound_to_the_pinned_revision(document: &Value, name: &str) {
    let live = PinnedInputs::load(&crate_root()).expect("the pinned inputs read");
    let recorded = document["schema_digest"].as_str().unwrap_or_default();
    assert_eq!(
        recorded,
        PINNED_SCHEMA_DIGEST,
        "{name} was recorded against schema revision {recorded}; this tree pins {}",
        live.digest()
    );
}

// ---------------------------------------------------------------------------
// The hostile matrix
// ---------------------------------------------------------------------------

/// Names the typed refusal one hostile row must produce.
fn refusal(error: &DecodeError) -> &'static str {
    match error {
        DecodeError::Empty => "empty",
        DecodeError::Oversized { .. } => "oversized",
        DecodeError::Truncated { .. } => "truncated",
        DecodeError::Trailing { .. } => "trailing",
        DecodeError::InvalidIdentifier => "invalid_identifier",
        DecodeError::Structural(_) => "structural",
        DecodeError::VersionSkew { .. } => "version_skew",
        DecodeError::UnsupportedFeature { .. } => "unsupported_feature",
        DecodeError::MissingCorrelation => "missing_correlation",
        DecodeError::MissingCausation => "missing_causation",
        DecodeError::UnknownOperation(_) => "unknown_operation",
        DecodeError::MissingBody => "missing_body",
        DecodeError::UnknownBody(_) => "unknown_body",
        DecodeError::OperationBodyMismatch { .. } => "operation_body_mismatch",
    }
}

#[test]
fn the_hostile_matrix_replays_against_the_pinned_revision() {
    let document = load("hostile.json");
    assert_bound_to_the_pinned_revision(&document, "hostile.json");
    let rows = document["rows"].as_array().expect("rows");
    assert!(rows.len() >= 20, "the matrix must carry every named class, found {}", rows.len());

    let mut classes: Vec<&str> = rows.iter().map(|row| row["class"].as_str().expect("class")).collect();
    classes.sort_unstable();
    let duplicates = classes.windows(2).any(|pair| pair[0] == pair[1]);
    assert!(!duplicates, "each hostile class appears exactly once: {classes:?}");

    for row in rows {
        let class = row["class"].as_str().expect("class");
        let expected = row["expect"].as_str().expect("expect");
        let bytes = materialize(row, class);
        let result = decode_envelope(&bytes);
        let actual = result
            .as_ref()
            .map(|_| "accepted")
            .map_err(refusal)
            .map_err(str::to_owned)
            .unwrap_err();
        assert_eq!(
            actual, expected,
            "hostile class {class} must fail closed with {expected}"
        );
    }
}

/// Turns one row into bytes. Rows that would not fit in a committed file
/// (`oversized` needs a frame above the 16 MiB ceiling, `structural_depth`
/// needs a frame nested far past the depth ceiling) carry a pinned
/// construction instead of a hex blob; everything else is exact bytes.
fn materialize(row: &Value, class: &str) -> Vec<u8> {
    if let Some(wire) = row["wire"].as_str() {
        return hex(wire);
    }
    let build = row
        .get("build")
        .and_then(Value::as_object)
        .unwrap_or_else(|| panic!("class {class} carries bytes or a build"));
    if let Some(length) = build.get("fill_length").and_then(Value::as_u64) {
        // The oversized class is decided on the *delivered* byte count
        // before any parse, so the row must really deliver that many
        // bytes; it is a pinned construction rather than a hex blob only
        // because a 16 MiB frame does not belong in a committed file.
        let fill = u8::from_str_radix(build.get("fill").and_then(Value::as_str).expect("fill"), 16)
            .expect("fill byte");
        let length = length as usize;
        let mut bytes = Vec::with_capacity(SIZE_PREFIX_BYTES + length);
        bytes.extend_from_slice(&(length as u32).to_le_bytes());
        bytes.extend(b"TBEP");
        bytes.resize(SIZE_PREFIX_BYTES + length, fill);
        return bytes;
    }
    if let Some(depth) = build.get("nesting").and_then(Value::as_u64) {
        return nested_write_request(depth as usize);
    }
    panic!("class {class} carries neither bytes nor a known build");
}

// ---------------------------------------------------------------------------
// The valid vectors
// ---------------------------------------------------------------------------

#[test]
fn every_valid_vector_decodes_to_its_recorded_request() {
    let document = load("valid.json");
    assert_bound_to_the_pinned_revision(&document, "valid.json");
    let channels = document["channels"].as_array().expect("channels");
    assert_eq!(channels.len(), 2, "two channels: two tenants over two connections");
    let mut total = 0_usize;
    let mut kinds: Vec<String> = Vec::new();
    for channel in channels {
        let frames = channel["frames"].as_array().expect("frames");
        assert!(frames.len() >= 2, "a channel negotiates before it binds");
        for (index, frame) in frames.iter().enumerate() {
            let bytes = hex(frame["wire"].as_str().expect("wire"));
            let request = decode_request(&bytes)
                .unwrap_or_else(|error| panic!("frame {index} must decode: {error}"));
            let recorded = frame["expect_request"].as_str().expect("expect_request");
            assert_eq!(
                request_name(&request),
                recorded,
                "frame {index} decodes to the wrong typed request"
            );
            // The envelope's own identity is recoverable and nonzero.
            let envelope = decode_envelope(&bytes).expect("the envelope verifies").envelope;
            let correlation = envelope.correlation().expect("correlation").0;
            assert_ne!(correlation, [0; 16], "frame {index} carries a real correlation");
            assert_eq!(envelope.causation().expect("causation").0, [0; 16]);
            kinds.push(recorded.to_owned());
            total += 1;
        }
    }
    for required in ["negotiation", "channel_bind", "document_read", "document_write", "document_delete"] {
        assert!(
            kinds.iter().any(|kind| kind == required),
            "the corpus must carry a {required} vector; found {kinds:?}"
        );
    }
    assert_eq!(
        total, 9,
        "the corpus carries every supported operation: two channels, four frames each, one delete"
    );
}

/// The typed-request name the runner and the corpus agree on.
fn request_name(request: &tbep_fbs::production::Request) -> &'static str {
    use tbep_fbs::production::Request;
    match request {
        Request::Negotiation { .. } => "negotiation",
        Request::ChannelBind { .. } => "channel_bind",
        Request::DocumentRead { .. } => "document_read",
        Request::DocumentWrite { .. } => "document_write",
        Request::DocumentDelete { .. } => "document_delete",
    }
}

// ---------------------------------------------------------------------------
// Shared builders used by both the corpus and the depth construction
// ---------------------------------------------------------------------------

fn envelope_table_start(bytes: &[u8]) -> usize {
    let root = u32::from_le_bytes(
        bytes[SIZE_PREFIX_BYTES..SIZE_PREFIX_BYTES + 4]
            .try_into()
            .expect("the hand vector has a root offset"),
    ) as usize;
    SIZE_PREFIX_BYTES + root
}

/// A `document.write` request whose body value is `depth` nested
/// `ValueArray` levels deep.
fn nested_write_request(depth: usize) -> Vec<u8> {
    use flatbuffers::FlatBufferBuilder;

    let mut builder = FlatBufferBuilder::new();
    let leaf = builder.create_string("leaf");
    let leaf = fbs::ValueString::create(&mut builder, &fbs::ValueStringArgs { value: Some(leaf) });
    let leaf_cell = fbs::ValueCell::create(
        &mut builder,
        &fbs::ValueCellArgs {
            value_type: fbs::TbepValue::ValueString,
            value: Some(leaf.as_union_value()),
        },
    );
    let leaf_items = builder.create_vector(&[leaf_cell]);
    let mut inner = fbs::ValueArray::create(
        &mut builder,
        &fbs::ValueArrayArgs { items: Some(leaf_items) },
    )
    .as_union_value();
    for _ in 0..depth {
        let cell = fbs::ValueCell::create(
            &mut builder,
            &fbs::ValueCellArgs {
                value_type: fbs::TbepValue::ValueArray,
                value: Some(inner),
            },
        );
        let items = builder.create_vector(&[cell]);
        let array =
            fbs::ValueArray::create(&mut builder, &fbs::ValueArrayArgs { items: Some(items) });
        inner = array.as_union_value();
    }
    let id = builder.create_string("d-01");
    let collection = builder.create_string("orders");
    let operation_id = builder.create_string("01ARZ3NDEKTSV4RRFFQ69G5FAV");
    let absent =
        fbs::ExpectedVersionAbsent::create(&mut builder, &fbs::ExpectedVersionAbsentArgs {});
    let document = fbs::DocumentPut::create(
        &mut builder,
        &fbs::DocumentPutArgs {
            id: Some(id),
            collection: Some(collection),
            body_type: fbs::TbepValue::ValueArray,
            body: Some(inner),
        },
    );
    let request = fbs::DocumentWriteRequest::create(
        &mut builder,
        &fbs::DocumentWriteRequestArgs {
            document: Some(document),
            expected_type: fbs::ExpectedVersion::ExpectedVersionAbsent,
            expected: Some(absent.as_union_value()),
            operation_id: Some(operation_id),
            ack: fbs::Acknowledgement::NONE,
        },
    );
    let envelope = fbs::TbepEnvelope::create(
        &mut builder,
        &fbs::TbepEnvelopeArgs {
            protocol_major: TBEP_PROTOCOL_MAJOR,
            operation: TbepOperation::DOCUMENT_WRITE,
            correlation: Some(&fbs::Ulid(correlation(1))),
            causation: Some(&fbs::Ulid([0; 16])),
            body_type: fbs::TbepFrame::DocumentWriteRequest,
            body: Some(request.as_union_value()),
            ..Default::default()
        },
    );
    fbs::finish_size_prefixed_tbep_envelope_buffer(&mut builder, envelope);
    builder.finished_data().to_vec()
}

/// A nonzero, per-frame-distinct correlation.
fn correlation(sequence: u64) -> [u8; 16] {
    let mut value = [0_u8; 16];
    value[8..].copy_from_slice(&sequence.to_be_bytes());
    value
}

fn hex(text: &str) -> Vec<u8> {
    assert!(text.len().is_multiple_of(2), "hex strings carry whole bytes: {text}");
    (0..text.len())
        .step_by(2)
        .map(|index| {
            u8::from_str_radix(&text[index..index + 2], 16)
                .unwrap_or_else(|error| panic!("bad hex {text}: {error}"))
        })
        .collect()
}

fn to_hex(bytes: &[u8]) -> String {
    bytes.iter().fold(String::new(), |mut out, byte| {
        use std::fmt::Write as _;
        let _ = write!(out, "{byte:02x}");
        out
    })
}

// ---------------------------------------------------------------------------
// Regeneration: the typed scenarios behind the committed corpus
// ---------------------------------------------------------------------------

fn hostile_corpus() -> Value {
    let valid = || {
        let mut spec = HandEnvelope::new(OP_ERROR, ERROR_FRAME_TAG, encode_error_frame(21, "cold tier unavailable"));
        spec.correlation = correlation(1);
        spec.causation = [0; 16];
        spec.encode()
    };

    let mut truncated = valid();
    truncated.truncate(truncated.len() - 4);

    let mut trailing = valid();
    trailing.extend_from_slice(&[0xDE, 0xAD, 0xBE, 0xEF]);

    let mut bad_identifier = valid();
    bad_identifier[SIZE_PREFIX_BYTES + 4] = b'X';

    let mut bad_root = valid();
    bad_root[SIZE_PREFIX_BYTES..SIZE_PREFIX_BYTES + 4]
        .copy_from_slice(&0xFFFF_0000_u32.to_le_bytes());

    let mut bad_body_offset = valid();
    let field = envelope_table_start(&bad_body_offset) + 52;
    bad_body_offset[field..field + 4].copy_from_slice(&0x7FFF_0000_u32.to_le_bytes());

    let mut unknown_union = valid();
    let union_tag_at = envelope_table_start(&unknown_union) + 50;
    unknown_union[union_tag_at] = 0xC7;

    let mut skew = HandEnvelope::new(OP_ERROR, ERROR_FRAME_TAG, encode_error_frame(18, "skew"));
    skew.protocol_major = 9;
    skew.correlation = correlation(1);
    skew.causation = [0; 16];

    let mut feature =
        HandEnvelope::new(OP_ERROR, ERROR_FRAME_TAG, encode_error_frame(1, "feature"));
    feature.required_features = 1_u64 << 63;
    feature.correlation = correlation(1);
    feature.causation = [0; 16];

    let mut no_correlation =
        HandEnvelope::new(OP_ERROR, ERROR_FRAME_TAG, encode_error_frame(1, "correlation"));
    no_correlation.correlation_present = false;
    no_correlation.causation = [0; 16];

    let mut no_causation =
        HandEnvelope::new(OP_ERROR, ERROR_FRAME_TAG, encode_error_frame(1, "causation"));
    no_causation.correlation = correlation(1);
    no_causation.causation_present = false;

    let mut no_body = HandEnvelope::new(OP_ERROR, ERROR_FRAME_TAG, encode_error_frame(1, "body"));
    no_body.body = None;
    no_body.correlation = correlation(1);
    no_body.causation = [0; 16];

    let mut mismatch =
        HandEnvelope::new(OP_DOCUMENT_WRITE, ERROR_FRAME_TAG, encode_error_frame(1, "mismatch"));
    mismatch.correlation = correlation(1);
    mismatch.causation = [0; 16];

    let unknown_operation =
        HandEnvelope::new(4000, ERROR_FRAME_TAG, encode_error_frame(1, "operation"));

    let mut misaligned = HandEnvelope::new(
        OP_DOCUMENT_WRITE_RESPONSE,
        DOCUMENT_WRITE_RESPONSE_TAG,
        encode_document_write_response(1, 1),
    );
    misaligned.correlation = correlation(1);
    misaligned.causation = [0; 16];

    let mut out_of_bounds_vtable = HandEnvelope::new(
        OP_DOCUMENT_WRITE_RESPONSE,
        DOCUMENT_WRITE_RESPONSE_TAG,
        encode_document_write_response(1, 8),
    );
    out_of_bounds_vtable.correlation = correlation(1);
    out_of_bounds_vtable.causation = [0; 16];
    let body = out_of_bounds_vtable.body.clone().unwrap_or_default();
    let mut patched = body;
    patched[8..12].copy_from_slice(&i32::MAX.to_le_bytes());
    out_of_bounds_vtable.body = Some(patched);

    // Unterminated string: replace the detail terminator with text.
    let mut unterminated = valid();
    let needle = b"cold tier unavailable\0";
    let position = unterminated
        .windows(needle.len())
        .position(|window| window == needle)
        .expect("the detail string and its terminator are present");
    unterminated[position + needle.len() - 1] = b'X';

    // Vector-length overrun: a two-element `[ushort]` declaring 65535.
    let mut vector_overrun = negotiation_request_bytes(&[0x11_11, 0x22_22]);
    let pattern = [2, 0, 0, 0, 0x11, 0x11, 0x22, 0x22];
    let position = vector_overrun
        .windows(pattern.len())
        .position(|window| window == pattern)
        .expect("the pinned vector layout is present");
    vector_overrun[position..position + 4].copy_from_slice(&[0xff, 0xff, 0, 0]);

    let row = |class: &str, wire: &[u8], expect: &str| {
        json!({"class": class, "wire": to_hex(wire), "expect": expect})
    };

    json!({
        "protocol": "terrabase.tbep.flatbuffers",
        "wire_version": 1,
        "category": "fbs-hostile-rejection",
        "schema_digest": PINNED_SCHEMA_DIGEST,
        "description": "One row per named hostile class against the production FlatBuffers decoder (decode_envelope): truncated, trailing, oversized, corrupt identifier, invalid root and body offsets, misaligned and out-of-bounds vtables, unterminated string, vector-length overrun, unbounded nesting, unknown union tag, version skew, unknown required feature bit, absent correlation or causation, absent body, unknown operation code, and an operation/body mismatch. Every row names the typed refusal it must produce before any public value is constructed.",
        "rows": [
            row("empty", &[], "empty"),
            row("truncated", &truncated, "truncated"),
            row("trailing", &trailing, "trailing"),
            json!({"class": "oversized",
                   "build": {"fill": "aa", "fill_length": (MAX_FRAME_BYTES as u64) + 1},
                   "expect": "oversized"}),
            row("invalid_identifier", &bad_identifier, "invalid_identifier"),
            row("invalid_root_offset", &bad_root, "structural"),
            row("invalid_body_offset", &bad_body_offset, "structural"),
            row("misaligned_nested_union_offset", &misaligned.encode(), "structural"),
            row("out_of_bounds_vtable_soffset", &out_of_bounds_vtable.encode(), "structural"),
            row("unterminated_string", &unterminated, "structural"),
            row("vector_length_overrun", &vector_overrun, "structural"),
            json!({"class": "unbounded_nesting",
                   "build": {"nesting": 200},
                   "expect": "structural"}),
            row("unknown_union_tag", &unknown_union, "unknown_body"),
            row("version_skew", &skew.encode(), "version_skew"),
            row("unknown_required_feature_bit", &feature.encode(), "unsupported_feature"),
            row("missing_correlation", &no_correlation.encode(), "missing_correlation"),
            row("missing_causation", &no_causation.encode(), "missing_causation"),
            row("missing_body", &no_body.encode(), "missing_body"),
            row("unknown_operation_code", &unknown_operation.encode(), "unknown_operation"),
            row("operation_body_mismatch", &mismatch.encode(), "operation_body_mismatch"),
        ]
    })
}

fn negotiation_request_bytes(versions: &[u16]) -> Vec<u8> {
    use flatbuffers::FlatBufferBuilder;

    let mut builder = FlatBufferBuilder::new();
    let supported = builder.create_vector(versions);
    let body = fbs::NegotiationRequestFrame::create(
        &mut builder,
        &fbs::NegotiationRequestFrameArgs {
            supported_versions: Some(supported),
            capabilities: None,
        },
    );
    let envelope = fbs::TbepEnvelope::create(
        &mut builder,
        &fbs::TbepEnvelopeArgs {
            protocol_major: TBEP_PROTOCOL_MAJOR,
            operation: TbepOperation::NEGOTIATION_REQUEST,
            correlation: Some(&fbs::Ulid(correlation(1))),
            causation: Some(&fbs::Ulid([0; 16])),
            body_type: fbs::TbepFrame::NegotiationRequestFrame,
            body: Some(body.as_union_value()),
            ..Default::default()
        },
    );
    fbs::finish_size_prefixed_tbep_envelope_buffer(&mut builder, envelope);
    builder.finished_data().to_vec()
}

fn valid_corpus() -> Value {
    json!({
        "protocol": "terrabase.tbep.flatbuffers",
        "wire_version": 1,
        "category": "fbs-valid-requests",
        "schema_digest": PINNED_SCHEMA_DIGEST,
        "description": "Golden valid request vectors for every operation the production decoder serves (negotiation.request, channel.bind, document.read, document.write, document.delete), grouped into channels so the live authenticated QUIC lane can exchange every one of them in order and round-trip decode each answer. The semantic values mirror the positional corpus categories (negotiation, channel-bind, document-frames) so the two codecs carry the same scenarios.",
        "channels": [
            {"description": "backend workload on tenant-a: negotiate, bind, write, read, delete",
             "frames": valid_channel("tenant-a", "d-01")},
            {"description": "a second workload on tenant-b over a fresh connection: negotiate, bind, write, read",
             "frames": valid_channel("tenant-b", "d-02")},
        ]
    })
}

fn valid_channel(tenant: &str, document: &str) -> Vec<Value> {
    let mut sequence = 0_u64;
    let write_operation = if tenant == "tenant-a" {
        "01ARZ3NDEKTSV4RRFFQ69G5FAV"
    } else {
        "01ARZ3NDEKTSV4RRFFQ69G5FC2"
    };
    let mut frames = vec![
        record(negotiation_vector(correlation(inc(&mut sequence))), "negotiation", "negotiation.response"),
        record(bind_vector(correlation(inc(&mut sequence)), tenant), "channel_bind", "channel.bind.response"),
        record(
            write_vector(correlation(inc(&mut sequence)), document, write_operation),
            "document_write",
            "document.write.response",
        ),
        record(read_vector(correlation(inc(&mut sequence)), document), "document_read", "document.read.response"),
    ];
    if tenant == "tenant-a" {
        frames.push(record(
            delete_vector(correlation(inc(&mut sequence)), document, "01ARZ3NDEKTSV4RRFFQ69G5FB0"),
            "document_delete",
            "document.delete.response",
        ));
    }
    frames
}

/// Advances the channel's correlation counter.
fn inc(sequence: &mut u64) -> u64 {
    *sequence += 1;
    *sequence
}

/// Records one golden frame with its typed request and expected response.
fn record(wire: Vec<u8>, request: &str, response: &str) -> Value {
    json!({
        "wire": to_hex(&wire),
        "expect_request": request,
        "expect_operation": response,
    })
}

fn negotiation_vector(correlation: [u8; 16]) -> Vec<u8> {
    use flatbuffers::FlatBufferBuilder;

    let mut builder = FlatBufferBuilder::new();
    let versions = builder.create_vector(&[1_u16]);
    let capabilities: Vec<_> = ["document.read", "document.write", "document.delete"]
        .into_iter()
        .map(|capability| builder.create_string(capability))
        .collect();
    let capabilities = builder.create_vector(&capabilities);
    let body = fbs::NegotiationRequestFrame::create(
        &mut builder,
        &fbs::NegotiationRequestFrameArgs {
            supported_versions: Some(versions),
            capabilities: Some(capabilities),
        },
    );
    finish(&mut builder, fbs::TbepOperation::NEGOTIATION_REQUEST,
           fbs::TbepFrame::NegotiationRequestFrame, body.as_union_value(), correlation)
}

fn bind_vector(correlation: [u8; 16], tenant: &str) -> Vec<u8> {
    use flatbuffers::FlatBufferBuilder;

    let mut builder = FlatBufferBuilder::new();
    let principal = builder.create_string("backend");
    let tenant = builder.create_string(tenant);
    let body = fbs::ChannelBindRequest::create(
        &mut builder,
        &fbs::ChannelBindRequestArgs { principal: Some(principal), tenant: Some(tenant) },
    );
    finish(&mut builder, fbs::TbepOperation::CHANNEL_BIND,
           fbs::TbepFrame::ChannelBindRequest, body.as_union_value(), correlation)
}

fn write_vector(correlation: [u8; 16], document: &str, operation: &str) -> Vec<u8> {
    use flatbuffers::FlatBufferBuilder;

    let mut builder = FlatBufferBuilder::new();
    let note = builder.create_string("bookshelf");
    let note = fbs::ValueString::create(&mut builder, &fbs::ValueStringArgs { value: Some(note) });
    let quantity = fbs::ValueInteger::create(&mut builder, &fbs::ValueIntegerArgs { value: 3 });
    let note_key = builder.create_string("note");
    let note_entry = fbs::ValueEntry::create(
        &mut builder,
        &fbs::ValueEntryArgs {
            key: Some(note_key),
            value_type: fbs::TbepValue::ValueString,
            value: Some(note.as_union_value()),
        },
    );
    let quantity_key = builder.create_string("qty");
    let quantity_entry = fbs::ValueEntry::create(
        &mut builder,
        &fbs::ValueEntryArgs {
            key: Some(quantity_key),
            value_type: fbs::TbepValue::ValueInteger,
            value: Some(quantity.as_union_value()),
        },
    );
    let entries = builder.create_vector(&[note_entry, quantity_entry]);
    let body_value =
        fbs::ValueObject::create(&mut builder, &fbs::ValueObjectArgs { entries: Some(entries) });
    let id = builder.create_string(document);
    let collection = builder.create_string("orders");
    let document = fbs::DocumentPut::create(
        &mut builder,
        &fbs::DocumentPutArgs {
            id: Some(id),
            collection: Some(collection),
            body_type: fbs::TbepValue::ValueObject,
            body: Some(body_value.as_union_value()),
        },
    );
    let absent =
        fbs::ExpectedVersionAbsent::create(&mut builder, &fbs::ExpectedVersionAbsentArgs {});
    let operation_id = builder.create_string(operation);
    let request = fbs::DocumentWriteRequest::create(
        &mut builder,
        &fbs::DocumentWriteRequestArgs {
            document: Some(document),
            expected_type: fbs::ExpectedVersion::ExpectedVersionAbsent,
            expected: Some(absent.as_union_value()),
            operation_id: Some(operation_id),
            ack: fbs::Acknowledgement::NONE,
        },
    );
    finish(&mut builder, fbs::TbepOperation::DOCUMENT_WRITE,
           fbs::TbepFrame::DocumentWriteRequest, request.as_union_value(), correlation)
}

fn read_vector(correlation: [u8; 16], document: &str) -> Vec<u8> {
    use flatbuffers::FlatBufferBuilder;

    let mut builder = FlatBufferBuilder::new();
    let collection = builder.create_string("orders");
    let document = builder.create_string(document);
    let body = fbs::DocumentReadRequest::create(
        &mut builder,
        &fbs::DocumentReadRequestArgs {
            collection: Some(collection),
            document: Some(document),
            level: fbs::ReadLevel::NONE,
        },
    );
    finish(&mut builder, fbs::TbepOperation::DOCUMENT_READ,
           fbs::TbepFrame::DocumentReadRequest, body.as_union_value(), correlation)
}

fn delete_vector(correlation: [u8; 16], document: &str, operation: &str) -> Vec<u8> {
    use flatbuffers::FlatBufferBuilder;

    let mut builder = FlatBufferBuilder::new();
    let collection = builder.create_string("orders");
    let document = builder.create_string(document);
    let any = fbs::ExpectedVersionAny::create(&mut builder, &fbs::ExpectedVersionAnyArgs {});
    let operation_id = builder.create_string(operation);
    let body = fbs::DocumentDeleteRequest::create(
        &mut builder,
        &fbs::DocumentDeleteRequestArgs {
            collection: Some(collection),
            document: Some(document),
            expected_type: fbs::ExpectedVersion::ExpectedVersionAny,
            expected: Some(any.as_union_value()),
            operation_id: Some(operation_id),
            ack: fbs::Acknowledgement::NONE,
        },
    );
    finish(&mut builder, fbs::TbepOperation::DOCUMENT_DELETE,
           fbs::TbepFrame::DocumentDeleteRequest, body.as_union_value(), correlation)
}

fn finish(
    builder: &mut flatbuffers::FlatBufferBuilder<'_>,
    operation: fbs::TbepOperation,
    body_type: fbs::TbepFrame,
    body: flatbuffers::WIPOffset<flatbuffers::UnionWIPOffset>,
    correlation: [u8; 16],
) -> Vec<u8> {
    let envelope = fbs::TbepEnvelope::create(
        builder,
        &fbs::TbepEnvelopeArgs {
            protocol_major: TBEP_PROTOCOL_MAJOR,
            protocol_minor: 0,
            required_features: fbs::FeatureBits::empty(),
            operation,
            correlation: Some(&fbs::Ulid(correlation)),
            causation: Some(&fbs::Ulid([0; 16])),
            body_type,
            body: Some(body),
        },
    );
    fbs::finish_size_prefixed_tbep_envelope_buffer(builder, envelope);
    builder.finished_data().to_vec()
}

#[test]
#[ignore = "regeneration writes the committed corpus"]
fn regenerate_corpus() {
    let dir = corpus_dir();
    fs::create_dir_all(&dir).expect("the corpus directory exists");
    for (name, document) in [("hostile.json", hostile_corpus()), ("valid.json", valid_corpus())] {
        let text = format!("{}\n", serde_json::to_string_pretty(&document).expect("JSON"));
        fs::write(dir.join(name), text).expect("the corpus file writes");
        println!("wrote {}", dir.join(name).display());
    }
}

/// Keeps the request/response frame names honest against the registry.
#[test]
fn the_valid_corpus_names_only_supported_operations() {
    use tbep_fbs::body_kind_for;
    let document = load("valid.json");
    for channel in document["channels"].as_array().expect("channels") {
        for frame in channel["frames"].as_array().expect("frames") {
            let response = frame["expect_operation"].as_str().expect("expect_operation");
            let code = response_code(response).unwrap_or_else(|| {
                panic!("the corpus names an unregistered response operation {response}")
            });
            assert!(
                body_kind_for(fbs::TbepOperation(code)).is_some(),
                "the response operation {response} must be registered"
            );
        }
    }
}

/// The registry codes the corpus's response names map onto.
fn response_code(name: &str) -> Option<u16> {
    let table = [
        ("negotiation.response", 3),
        ("channel.bind.response", 576),
        ("document.read.response", 513),
        ("document.write.response", 514),
        ("document.delete.response", 515),
    ];
    table
        .iter()
        .find(|(candidate, _)| *candidate == name)
        .map(|(_, code)| *code)
}
