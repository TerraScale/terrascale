//! The TBEP golden-vector runner (SE-14): executes the committed
//! `terrabase.engine.protocol` conformance corpus against the real
//! envelope and codec, asserting byte-exact wire frames in both
//! directions — decode(bytes) == frame header and encode == bytes.
//!
//! The corpus lives as JSON data files under `vectors/tbep/` so the C#
//! protocol client's conformance suite consumes the same data files
//! (PD-2026-08-26-02 D4: cross-language conformance vectors are
//! mandatory). Every recorded frame carries its full envelope bytes
//! (hex), its body bytes (hex), its envelope identity (kind,
//! correlation, causation, idempotency), and its measured
//! envelope-overhead; adversarial rows carry hostile bytes and the
//! failure they must produce.
//!
//! Regeneration: `cargo test --test vectors_runner -- --ignored
//! regenerate_corpus` rewrites the corpus from the typed scenarios at
//! the bottom of this file. The committed corpus is the golden record:
//! if the codec changes any byte, the replay below fails and the change
//! is a contract change, not noise.

use std::fs;
use std::path::PathBuf;

use serde_json::Value;
use serde_json::json;
use terrabase_tbep::EnvelopeError;
use terrabase_tbep::IdempotencyKey;
use terrabase_tbep::TbepFrame;
use terrabase_tbep::TbepValue;
use terrabase_tbep::envelope::envelope_overhead;
use terrabase_tbep::envelope::stream_wire_bytes;
use terrabase_tbep::generated::Acknowledgement;
use terrabase_tbep::generated::AdminStatusRequest;
use terrabase_tbep::generated::BUDGETS;
use terrabase_tbep::generated::BackboneSubscribeRequest;
use terrabase_tbep::generated::ChannelBindRequest;
use terrabase_tbep::generated::DocumentPut;
use terrabase_tbep::generated::DocumentReadRequest;
use terrabase_tbep::generated::DocumentWriteRequest;
use terrabase_tbep::generated::ExpectedVersion;
use terrabase_tbep::generated::QueryCancelRequest;
use terrabase_tbep::generated::ReadLevel;
use terrabase_tbep::generated::ShutdownReason;
use terrabase_tbep::generated::SubscriptionStart;
use terrabase_tbep::generated::SupervisionHealthRequest;
use terrabase_tbep::generated::SupervisionShutdownRequest;
use terrabase_tbep::generated::SyncChange;
use terrabase_tbep::generated::SyncField;
use terrabase_tbep::generated::SyncMutationSubmitRequest;
use terrabase_tbep::generated::SyncSessionSetupRequest;
use terrabase_tbep::generated::TopologyReadRequest;
use terrabase_tbep::generated::TriggerNotification;

/// The corpus directory (committed, machine-readable).
fn corpus_dir() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("vectors/tbep")
}

/// The categories every accepted corpus file must cover exactly once.
const CATEGORIES: &[&str] = &[
    "channel-bind",
    "document-frames",
    "query-cancellation",
    "sync-session",
    "trigger-notification",
    "schema-pitfalls",
    "adversarial-rejection",
];

#[test]
fn the_corpus_is_complete_and_replays_byte_exact() {
    let dir = corpus_dir();
    let mut files: Vec<PathBuf> = fs::read_dir(&dir)
        .unwrap_or_else(|e| panic!("the corpus directory must exist and be readable: {e}"))
        .map(|entry| entry.expect("entry").path())
        .filter(|path| path.extension().is_some_and(|e| e == "json"))
        .collect();
    files.sort();
    assert_eq!(
        files.len(),
        CATEGORIES.len(),
        "the corpus must hold exactly one file per category, found {files:?}"
    );
    let mut seen = Vec::new();
    for path in &files {
        let text = fs::read_to_string(path).expect("the corpus file reads");
        let document: Value = serde_json::from_str(&text).expect("the corpus file is JSON");
        assert_eq!(
            document["protocol"], "terrabase.engine.protocol",
            "corpus file {path:?} names the wrong protocol"
        );
        assert_eq!(
            document["wire_version"], 1,
            "corpus file {path:?} names the wrong wire version"
        );
        let category = document["category"].as_str().expect("category").to_string();
        assert!(CATEGORIES.contains(&category.as_str()), "unknown category {category}");
        assert!(!seen.contains(&category), "duplicate category {category}");
        seen.push(category);
        replay_file(&document);
    }
    assert_eq!(seen.len(), CATEGORIES.len());
}

/// Replays one corpus file: every golden frame decodes to its recorded
/// identity and body, re-encodes byte-exact, and respects its written
/// budget; every adversarial row fails closed the recorded way.
fn replay_file(document: &Value) {
    let category = document["category"].as_str().expect("category");
    let mut golden = 0;
    for entry in document["frames"].as_array().expect("frames") {
        let wire = hex(entry["wire"].as_str().expect("wire"));
        let frame = TbepFrame::from_bytes(&wire)
            .unwrap_or_else(|e| panic!("category {category}: the golden frame must decode: {e}"));
        // Identity.
        assert_eq!(
            frame.kind().name(),
            entry["kind"].as_str().expect("kind"),
            "category {category}: kind mismatch"
        );
        assert_eq!(frame.correlation(), entry["correlation"].as_u64().expect("correlation"));
        assert_eq!(frame.causation(), entry["causation"].as_u64().expect("causation"));
        match (&entry["idempotency"], frame.idempotency()) {
            (Value::Null, None) => {}
            (Value::String(key), Some(frame_key)) => {
                assert_eq!(key, frame_key.as_str(), "category {category}: key mismatch");
            }
            other => panic!("category {category}: idempotency pairing mismatch {other:?}"),
        }
        // Body bytes.
        let body = hex(entry["body"].as_str().expect("body"));
        assert_eq!(frame.body_bytes(), body, "category {category}: body bytes mismatch");
        // Byte-exact re-encode.
        assert_eq!(frame.to_bytes(), wire, "category {category}: re-encode must be byte-exact");
        // Overhead accounting and the written budget.
        let overhead = envelope_overhead(&frame);
        assert_eq!(
            usize::try_from(entry["overhead_bytes"].as_u64().expect("overhead")).expect("fits"),
            overhead,
            "category {category}: overhead mismatch"
        );
        let class_budget = BUDGETS
            .iter()
            .find_map(|&(class, bytes)| (class == frame.kind().class()).then_some(bytes))
            .unwrap_or_else(|| panic!("category {category}: no budget for the class"));
        assert!(
            stream_wire_bytes(&frame) <= class_budget,
            "category {category}: frame of class {} exceeds its written budget",
            frame.kind().class()
        );
        golden += 1;
    }
    if category == "adversarial-rejection" {
        // The rejection category records hostile bytes only; every other
        // category carries golden frames.
        assert!(
            document["adversarial"].as_array().is_some_and(|rows| !rows.is_empty()),
            "category {category}: no adversarial rows"
        );
    } else {
        assert!(golden > 0, "category {category}: no golden frames");
        assert!(
            document["adversarial"].as_array().is_none_or(Vec::is_empty),
            "category {category}: adversarial rows belong to the rejection category"
        );
    }

    for entry in document["adversarial"].as_array().unwrap_or(&Vec::new()) {
        let wire = hex(entry["wire"].as_str().expect("wire"));
        let expectation = entry["expect"].as_str().expect("expect");
        let error = match TbepFrame::from_bytes(&wire) {
            Err(error) => error,
            Ok(frame) => {
                panic!("category {category}: the adversarial row decoded as {:?}", frame.kind())
            }
        };
        let actual = match error {
            EnvelopeError::UnsupportedVersion(_) => "unsupported_version",
            EnvelopeError::UnknownFrameKind(_) => "unknown_frame_kind",
            EnvelopeError::DigestMismatch => "digest_mismatch",
            EnvelopeError::IdempotencyPairing(_) => "idempotency_pairing",
            EnvelopeError::Malformed(_) => "malformed_frame",
            EnvelopeError::InvalidIdempotencyKey(_) => "invalid_idempotency_key",
            EnvelopeError::FrameTooLarge(_) => "frame_too_large",
            EnvelopeError::Io(_) => "io",
        };
        assert_eq!(
            actual, expectation,
            "category {category}: adversarial row failed the wrong way"
        );
    }
}

#[test]
fn the_budget_table_is_recorded_per_class() {
    // The written bytes-on-wire budget (PD-2026-08-26-13 D1) is part of
    // the corpus so every language reads the same numbers.
    let manifest = fs::read_to_string(corpus_dir().join("adversarial-rejection.json"))
        .expect("the corpus file reads");
    let document: Value = serde_json::from_str(&manifest).expect("JSON");
    let budgets = document["budgets"].as_array().expect("budgets");
    assert_eq!(budgets.len(), BUDGETS.len());
    for (recorded, (class, bytes)) in budgets.iter().zip(BUDGETS.iter()) {
        assert_eq!(recorded["class"].as_str().expect("class"), *class);
        assert_eq!(
            usize::try_from(recorded["wire_bytes"].as_u64().expect("bytes")).expect("fits"),
            *bytes
        );
    }
}

#[test]
fn objects_canonicalize_across_field_order() {
    // The nil/map-array pitfall set: identical values encode to identical
    // bytes regardless of the in-memory field order, and nil rides only
    // in optional slots.
    let first = TbepValue::Object(vec![
        ("z".into(), TbepValue::Integer(1)),
        ("a".into(), TbepValue::Null),
        ("m".into(), TbepValue::Array(vec![TbepValue::Boolean(true)])),
    ]);
    let second = TbepValue::Object(vec![
        ("m".into(), TbepValue::Array(vec![TbepValue::Boolean(true)])),
        ("z".into(), TbepValue::Integer(1)),
        ("a".into(), TbepValue::Null),
    ]);
    assert_eq!(
        terrabase_tbep::codec::encode_value(&first),
        terrabase_tbep::codec::encode_value(&second),
        "objects canonicalize name-sorted on the wire"
    );
}

fn hex(text: &str) -> Vec<u8> {
    assert!(text.len().is_multiple_of(2), "hex strings carry whole bytes: {text}");
    (0..text.len())
        .step_by(2)
        .map(|index| {
            u8::from_str_radix(&text[index..index + 2], 16)
                .unwrap_or_else(|e| panic!("bad hex {text}: {e}"))
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
// Regeneration: the typed scenarios behind the committed corpus.
// ---------------------------------------------------------------------------

/// One scenario frame: the sealed frame plus its recorded identity.
fn scenario_frame(kind: &str, frame: &TbepFrame) -> Value {
    json!({
        "kind": kind,
        "correlation": frame.correlation(),
        "causation": frame.causation(),
        "idempotency": frame.idempotency().map(IdempotencyKey::as_str),
        "body": to_hex(&frame.body_bytes()),
        "wire": to_hex(&frame.to_bytes()),
        "overhead_bytes": envelope_overhead(frame) as u64,
    })
}

/// Builds the whole corpus from the typed scenarios.
#[allow(clippy::too_many_lines)]
fn build_corpus() -> Vec<(String, Value)> {
    let mut corpus = Vec::new();

    // channel-bind: the supervision-gated channel binding.
    let bind = TbepFrame::seal(
        ChannelBindRequest { principal: "backend".into(), tenant: "tenant-a".into() },
        1,
        0,
        None,
    )
    .expect("seals");
    corpus.push((
        "channel-bind".into(),
        json!({
            "category": "channel-bind",
            "description": "The backend-supplied channel binding gates every tenant-scoped operation (PD-2026-08-26-11 D1): no frame carries a tenant; the bound channel does.",
            "protocol": "terrabase.engine.protocol",
            "wire_version": 1,
            "schema": "terrabase.engine.protocol/vector-corpus@1",
            "frames": [
                scenario_frame("ChannelBindRequest", &bind),
                scenario_frame("ChannelBindRequest",
                    &TbepFrame::seal(
                        ChannelBindRequest { principal: "backend".into(), tenant: "tenant-b".into() },
                        u64::MAX, u64::MAX, None).expect("seals")),
            ],
            "adversarial": [],
        }),
    ));

    // document-frames: read, mutation with its key and expected version.
    let write = TbepFrame::seal(
        DocumentWriteRequest {
            document: DocumentPut {
                id: "d-01".into(),
                collection: "orders".into(),
                body: TbepValue::Object(vec![
                    ("qty".into(), TbepValue::Integer(3)),
                    ("note".into(), TbepValue::String("first".into())),
                ]),
            },
            expected: ExpectedVersion::Exact(7),
            operation_id: "op-01".into(),
            ack: Some(Acknowledgement::GlobalQuorum),
        },
        2,
        0,
        Some(&IdempotencyKey::new("retry-01").expect("key")),
    )
    .expect("seals");
    corpus.push((
        "document-frames".into(),
        json!({
            "category": "document-frames",
            "description": "Typed document operations: the read carries no key; the write carries its idempotency key (the retry identity) and the expected-version contract.",
            "protocol": "terrabase.engine.protocol",
            "wire_version": 1,
            "schema": "terrabase.engine.protocol/vector-corpus@1",
            "frames": [
                scenario_frame("DocumentReadRequest",
                    &TbepFrame::seal(
                        DocumentReadRequest {
                            collection: "orders".into(),
                            document: "d-01".into(),
                            level: Some(ReadLevel::RegionalQuorumRead),
                        },
                        3, 0, None).expect("seals")),
                scenario_frame("DocumentWriteRequest", &write),
            ],
            "adversarial": [],
        }),
    ));

    // query-cancellation: the GAP-Q12 live signal.
    let cancel = TbepFrame::seal(QueryCancelRequest { query_id: 7 }, 4, 0, None).expect("seals");
    corpus.push((
        "query-cancellation".into(),
        json!({
            "category": "query-cancellation",
            "description": "The wire-side query cancellation lifecycle handoff (GAP-Q12, PB-4034): one typed live signal frame keyed by the in-flight query identity.",
            "protocol": "terrabase.engine.protocol",
            "wire_version": 1,
            "schema": "terrabase.engine.protocol/vector-corpus@1",
            "frames": [scenario_frame("QueryCancelRequest", &cancel)],
            "adversarial": [],
        }),
    ));

    // sync-session: setup and a server-ack mutation.
    let mutation = TbepFrame::seal(
        SyncMutationSubmitRequest {
            session_id: 1,
            document: "d-01".into(),
            change: SyncChange::Put(vec![SyncField {
                name: "qty".into(),
                value: TbepValue::Integer(3),
            }]),
        },
        5,
        0,
        Some(&IdempotencyKey::new("op-42").expect("key")),
    )
    .expect("seals");
    // The wire schema declares the full recursive value type, and the
    // serving core's domain is scalar: a composite field value is legal
    // on the wire (both languages must decode and re-encode it
    // byte-exact) and is answered with the typed invalid_body rejection
    // — never silently coerced.
    let composite_mutation = TbepFrame::seal(
        SyncMutationSubmitRequest {
            session_id: 1,
            document: "d-02".into(),
            change: SyncChange::Put(vec![
                SyncField {
                    name: "tags".into(),
                    value: TbepValue::Array(vec![TbepValue::String("x".into())]),
                },
                SyncField {
                    name: "nested".into(),
                    value: TbepValue::Object(vec![("a".into(), TbepValue::Integer(1))]),
                },
            ]),
        },
        13,
        0,
        Some(&IdempotencyKey::new("op-composite").expect("key")),
    )
    .expect("seals");
    corpus.push((
        "sync-session".into(),
        json!({
            "category": "sync-session",
            "description": "The sync-delivery family (PD-2026-08-26-12): session setup and one server-ack mutation whose idempotency key is the at-least-once retry identity; no optimistic patches exist on this channel. The composite-valued mutation is legal on the wire (the value type is recursive) and is rejected by the serving host with the typed invalid_body outcome — the scalar serving domain, never silent coercion.",
            "protocol": "terrabase.engine.protocol",
            "wire_version": 1,
            "schema": "terrabase.engine.protocol/vector-corpus@1",
            "frames": [
                scenario_frame("SyncSessionSetupRequest",
                    &TbepFrame::seal(
                        SyncSessionSetupRequest { collection: "orders".into(), resume: Some(vec![1, 2, 3, 4]) },
                        6, 0, None).expect("seals")),
                scenario_frame("SyncMutationSubmitRequest", &mutation),
                scenario_frame("SyncMutationSubmitRequest", &composite_mutation),
            ],
            "adversarial": [],
        }),
    ));

    // trigger-notification: the content-free trigger (PD-2026-08-26-13 D3).
    let trigger = TbepFrame::seal(
        TriggerNotification {
            subscription_id: 42,
            collection: "orders".into(),
            sequence: 9,
            operation_id: "op-42".into(),
        },
        0,
        42,
        None,
    )
    .expect("seals");
    corpus.push((
        "trigger-notification".into(),
        json!({
            "category": "trigger-notification",
            "description": "The content-free trigger notification (PD-2026-08-26-13 D3): unsolicited pushes carry correlation 0, causation naming their subscription, and never document data — the consumer pulls the delta.",
            "protocol": "terrabase.engine.protocol",
            "wire_version": 1,
            "schema": "terrabase.engine.protocol/vector-corpus@1",
            "frames": [
                scenario_frame("TriggerNotification", &trigger),
                scenario_frame("BackboneSubscribeRequest",
                    &TbepFrame::seal(
                        BackboneSubscribeRequest {
                            subscription_id: 42,
                            collections: Some(vec!["orders".into()]),
                            from: SubscriptionStart::Sequence(9),
                        },
                        7, 0, None).expect("seals")),
            ],
            "adversarial": [],
        }),
    ));

    // schema-pitfalls: nil discipline, optional slots, canonical objects,
    // supervision and topology reads with empty bodies.
    let health = TbepFrame::seal(SupervisionHealthRequest {}, 8, 0, None).expect("seals");
    let topology = TbepFrame::seal(TopologyReadRequest {}, 9, 0, None).expect("seals");
    let shutdown = TbepFrame::seal(
        SupervisionShutdownRequest { reason: ShutdownReason::Operator },
        10,
        0,
        None,
    )
    .expect("seals");
    let status = TbepFrame::seal(AdminStatusRequest {}, 11, 0, None).expect("seals");
    let nested = TbepFrame::seal(
        DocumentWriteRequest {
            document: DocumentPut {
                id: "d-02".into(),
                collection: "orders".into(),
                body: TbepValue::Object(vec![
                    (
                        "z".into(),
                        TbepValue::Array(vec![TbepValue::Null, TbepValue::Boolean(false)]),
                    ),
                    ("a".into(), TbepValue::Number(1.5)),
                    (
                        "m".into(),
                        TbepValue::Object(vec![
                            ("inner".into(), TbepValue::String("value".into())),
                            ("alpha".into(), TbepValue::Integer(-1)),
                        ]),
                    ),
                ]),
            },
            expected: ExpectedVersion::Any,
            operation_id: "op-03".into(),
            ack: None,
        },
        12,
        0,
        Some(&IdempotencyKey::new("retry-03").expect("key")),
    )
    .expect("seals");
    corpus.push((
        "schema-pitfalls".into(),
        json!({
            "category": "schema-pitfalls",
            "description": "The cross-language pitfall set (PD-2026-08-26-02 D4): nil rides only in optional slots, objects canonicalize name-sorted at every nesting level, tagged value arrays carry the type distinction the map/array worlds miss, and empty bodies are fixarray(0).",
            "protocol": "terrabase.engine.protocol",
            "wire_version": 1,
            "schema": "terrabase.engine.protocol/vector-corpus@1",
            "frames": [
                scenario_frame("SupervisionHealthRequest", &health),
                scenario_frame("TopologyReadRequest", &topology),
                scenario_frame("SupervisionShutdownRequest", &shutdown),
                scenario_frame("AdminStatusRequest", &status),
                scenario_frame("DocumentWriteRequest", &nested),
            ],
            "adversarial": [],
        }),
    ));

    // adversarial-rejection: hostile bytes and their recorded failures.
    let good = TbepFrame::seal(SupervisionHealthRequest {}, 1, 0, None).expect("seals").to_bytes();
    let mut unknown_version = good.clone();
    unknown_version[1] = 2;
    let mut unknown_kind = good.clone();
    unknown_kind[3] = 0x7f;
    unknown_kind[4] = 0xff;
    let mut digest_mismatch = good.clone();
    // Locate the digest (cf + 8 bytes) after the nil idempotency slot.
    let digest_offset = 1 + 1 + 3 + 1 + 1 + 1 + 1;
    digest_mismatch[digest_offset] ^= 0x01;
    let read_as_map = vec![0x92u8, 0x81, 0x01, 0x02, 0x92];
    let nil_required = vec![0x92u8, 0xc0, 0xc0];
    let trailing = {
        let mut bytes = good.clone();
        bytes.push(0x77);
        bytes
    };
    corpus.push((
        "adversarial-rejection".into(),
        json!({
            "category": "adversarial-rejection",
            "description": "Hostile frames and the failure each must produce on every side: unknown versions and kinds, digest disagreement, maps where positional arrays belong, nil in required slots, and trailing bytes all fail closed.",
            "protocol": "terrabase.engine.protocol",
            "wire_version": 1,
            "schema": "terrabase.engine.protocol/vector-corpus@1",
            "budgets": BUDGETS.iter().map(|(class, bytes)| json!({"class": class, "wire_bytes": bytes})).collect::<Vec<_>>(),
            "frames": [],
            "adversarial": [
                json!({"wire": to_hex(&unknown_version), "expect": "unsupported_version"}),
                json!({"wire": to_hex(&unknown_kind), "expect": "unknown_frame_kind"}),
                json!({"wire": to_hex(&digest_mismatch), "expect": "digest_mismatch"}),
                json!({"wire": to_hex(&read_as_map), "expect": "malformed_frame"}),
                json!({"wire": to_hex(&nil_required), "expect": "malformed_frame"}),
                json!({"wire": to_hex(&trailing), "expect": "malformed_frame"}),
            ],
        }),
    ));

    corpus
}

#[test]
#[ignore = "regeneration writes the committed corpus"]
fn regenerate_corpus() {
    let dir = corpus_dir();
    fs::create_dir_all(&dir).expect("the corpus directory exists");
    for (category, document) in build_corpus() {
        let path = dir.join(format!("{category}.json"));
        let text = format!("{}\n", serde_json::to_string_pretty(&document).expect("JSON"));
        fs::write(&path, text).expect("the corpus file writes");
        println!("wrote {}", path.display());
    }
}
