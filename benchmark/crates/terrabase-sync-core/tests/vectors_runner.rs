//! The golden-vector runner: executes the committed `terrascale.sync.v2`
//! conformance corpus against the real sync core and protocol codec,
//! asserting byte-exact wire frames.
//!
//! The corpus lives as JSON data files under `vectors/terrascale.sync.v2/`
//! so any language can consume it (PD-2026-08-26-12 D1: the same vectors
//! run in every SDK's CI; SE-05 owns the per-language harnesses). Every
//! recorded frame carries both its wire bytes (hex) and its structured
//! description, so an SDK asserts decode(bytes) == frame and
//! encode(frame) == bytes.
//!
//! This is corpus generation 2: the native corpus. Every payload is the
//! TerraBase-native codec (`terrabase-native/1` deltas,
//! `terrabase-snapshot/1` snapshots) over `terrabase-crdt` and
//! `terrabase-causal` state per ADR 0024; the superseded generation-1
//! corpus carried an external CRDT engine's payloads and is historical
//! evidence only. Old payload frames never decode as new ones.
//!
//! ADR 0024 category mapping: fresh-session covers snapshot bootstrap;
//! checkpoint-resume covers delta and gap; repeated-change-delta covers
//! delta efficiency; reconnect-replay covers duplicate and reconnect;
//! resync-required covers reset; slow-consumer, region-route-change,
//! malformed-frame-rejection, and trigger-notification complete the
//! crate's vocabulary. DEFERRED categories (they need engine-side
//! machinery that does not exist yet — view plans, authorized result
//! revisions, AEAD checkpoints): authorization change, plus the full
//! query-view result-operation vocabulary. They land with the ADR 0024
//! view state machine.
//!
//! Regeneration: `cargo test --test vectors_runner -- --ignored
//! regenerate_corpus` rewrites the corpus from the typed scenario
//! definitions at the bottom of this file. The committed corpus is the
//! golden record; if the codec or the native substrate changes any byte,
//! the replay below fails and the change is a contract change, not noise.

use std::collections::BTreeMap;
use std::fs;
use std::path::PathBuf;

use serde_json::Value;
use serde_json::json;
use terrabase_sync_core::FrameId;
use terrabase_sync_core::JournalChange;
use terrabase_sync_core::JournalCommit;
use terrabase_sync_core::SyncCore;
use terrabase_sync_core::SyncCoreConfig;
use terrabase_sync_core::SyncScope;
use terrabase_sync_core::SyncValue;
use terrabase_sync_core::protocol::DocumentWrite;
use terrabase_sync_core::protocol::ErrorCode;
use terrabase_sync_core::protocol::FrameClass;
use terrabase_sync_core::protocol::FrameKind;
use terrabase_sync_core::protocol::IdempotencyKey;
use terrabase_sync_core::protocol::MutationOutcome;
use terrabase_sync_core::protocol::PayloadCodec;
use terrabase_sync_core::protocol::ProtocolError;
use terrabase_sync_core::protocol::ResyncReason;
use terrabase_sync_core::protocol::SyncV2Body;
use terrabase_sync_core::protocol::SyncV2Frame;
use terrabase_sync_core::protocol::serve_delta;
use terrabase_sync_core::protocol::serve_snapshot;
use terrabase_sync_core::protocol::trigger_frame;

/// The corpus directory (committed, machine-readable).
fn corpus_dir() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("vectors/terrascale.sync.v2")
}

/// The categories every accepted corpus file must cover exactly once.
const CATEGORIES: &[&str] = &[
    "fresh-session",
    "checkpoint-resume",
    "reconnect-replay",
    "slow-consumer",
    "region-route-change",
    "resync-required",
    "malformed-frame-rejection",
    "repeated-change-delta",
    "trigger-notification",
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
    let mut seen_categories = Vec::new();
    for path in &files {
        let text =
            fs::read_to_string(path).unwrap_or_else(|e| panic!("read {}: {e}", path.display()));
        let corpus: Value =
            serde_json::from_str(&text).unwrap_or_else(|e| panic!("parse {}: {e}", path.display()));
        let category = corpus["category"]
            .as_str()
            .unwrap_or_else(|| panic!("{} carries no category", path.display()));
        seen_categories.push(category.to_string());
        replay_corpus_file(&path.display().to_string(), &corpus);
    }
    for category in CATEGORIES {
        assert!(
            seen_categories.contains(&category.to_string()),
            "the corpus is missing the {category} category"
        );
    }
}

/// Replays one corpus file: rebuilds the server core, walks the steps,
/// and asserts every expected frame byte-exactly.
fn replay_corpus_file(source: &str, corpus: &Value) {
    let header = format!(
        "schema={}, protocol={}, version={:?}",
        corpus["schema"].as_str().unwrap_or("<missing>"),
        corpus["protocol"].as_str().unwrap_or("<missing>"),
        corpus["protocol_version"],
    );
    assert_eq!(corpus["schema"], json!("terrascale.sync.v2/vector-corpus@2"), "{source}: {header}");
    assert_eq!(corpus["protocol"], json!("terrascale.sync.v2"), "{source}");
    assert_eq!(corpus["protocol_version"], json!(2), "{source}");

    let server = &corpus["server"];
    let tenant = server["scope"]["tenant"].as_str().expect("tenant");
    let collection = server["scope"]["collection"].as_str().expect("collection");
    let budget = usize::try_from(server["config"]["batch_byte_budget"].as_u64().expect("budget"))
        .expect("budget fits");
    let window = usize::try_from(server["config"]["checkpoint_window"].as_u64().expect("window"))
        .expect("window fits");
    let mut core = SyncCore::new(
        SyncScope::new(tenant, collection),
        SyncCoreConfig { batch_byte_budget: budget, checkpoint_window: window },
    )
    .expect("the core builds");
    for commit in server["journal_before"].as_array().unwrap_or(&Vec::new()) {
        apply_journal(&mut core, commit);
    }

    for (index, step) in corpus["steps"].as_array().expect("steps").iter().enumerate() {
        let where_ = format!("{source} step {index}");
        if step["apply"].is_object() {
            apply_journal(&mut core, &step["apply"]);
        } else if step["client_frame"].is_object() {
            check_recorded_frame(&where_, &step["client_frame"]);
        } else if step["server_frame"].is_object() {
            check_recorded_frame(&where_, &step["server_frame"]);
        } else if step["serve_delta"].is_object() {
            check_serve_delta(&mut core, &where_, &step["serve_delta"]);
        } else if step["serve_snapshot"].is_object() {
            check_serve_snapshot(&mut core, &where_, &step["serve_snapshot"]);
        } else if step["serve_trigger"].is_object() {
            check_serve_trigger(&where_, &step["serve_trigger"]);
        } else if step["decode_rejects"].is_object() {
            check_decode_rejects(&where_, &step["decode_rejects"]);
        } else if step["budget"].is_object() {
            check_budget(&where_, &step["budget"]);
        } else if step.get("note").is_some() {
            // Prose annotation; nothing to execute.
        } else {
            panic!("{where_}: unknown step {step}");
        }
    }
}

/// Applies one journal commit description to the core.
fn apply_journal(core: &mut SyncCore, commit: &Value) {
    let sequence = commit["sequence"].as_u64().expect("sequence");
    let operation_id = commit["operation_id"].as_str().expect("operation_id").to_string();
    let document_id = commit["document"].as_str().expect("document").to_string();
    let change = if let Some(fields) = commit["put"].as_object() {
        let mut body = BTreeMap::new();
        for (key, value) in fields {
            body.insert(key.clone(), json_to_sync_value(value));
        }
        JournalChange::Put { body }
    } else if commit["delete"].as_bool().unwrap_or(false) {
        JournalChange::Delete
    } else {
        panic!("journal entry {sequence} carries no put or delete");
    };
    core.apply(&JournalCommit { storage_sequence: sequence, operation_id, document_id, change })
        .expect("the journal applies");
}

fn json_to_sync_value(value: &Value) -> SyncValue {
    match value {
        Value::Null => SyncValue::Null,
        Value::Bool(b) => SyncValue::Bool(*b),
        Value::Number(n) => n
            .as_i64()
            .map(SyncValue::Int)
            .or_else(|| n.as_f64().map(SyncValue::Float))
            .unwrap_or_else(|| panic!("unsupported number {n}")),
        Value::String(s) => SyncValue::Str(s.clone()),
        other => panic!("unsupported vector value {other}"),
    }
}

/// A recorded frame step (client- or server-sent): the structured
/// description must encode to the recorded bytes, and the bytes must
/// decode back to the description — the same assertion every SDK's
/// conformance harness makes against this corpus.
fn check_recorded_frame(where_: &str, entry: &Value) {
    let expected_wire = hex_to_bytes(entry["wire"].as_str().expect("wire"), where_);
    let frame = frame_from_json(&entry["frame"], where_);
    assert_eq!(
        frame.to_bytes(),
        expected_wire,
        "{where_}: the structured client frame must encode to the recorded bytes"
    );
    let decoded = SyncV2Frame::from_bytes(&expected_wire)
        .unwrap_or_else(|e| panic!("{where_}: the recorded wire bytes must decode: {e}"));
    assert_eq!(
        decoded.kind(),
        frame.kind(),
        "{where_}: decoded kind disagrees with the description"
    );
    assert!(decoded.verify(), "{where_}: the recorded frame verifies");
    assert_eq!(decoded.correlation(), frame.correlation(), "{where_}: correlation");
    assert_eq!(decoded.causation(), frame.causation(), "{where_}: causation");
    assert_eq!(decoded.idempotency(), frame.idempotency(), "{where_}: idempotency");
}

fn check_serve_delta(core: &mut SyncCore, where_: &str, entry: &Value) {
    let from = checkpoint_from_json(&entry["from"], where_);
    let correlation = FrameId::new(entry["correlation"].as_u64().expect("correlation"));
    let causation = FrameId::new(entry["causation"].as_u64().unwrap_or(0));
    let served = serve_delta(core, &from, correlation, causation)
        .unwrap_or_else(|e| panic!("{where_}: serving must not fail: {e}"));
    let expect_wire = entry["expect_wire"].as_str();
    match (served, expect_wire) {
        (None, None) => {}
        (None, Some(_)) => panic!("{where_}: expected a frame, the core served none"),
        (Some(frame), None) => {
            panic!("{where_}: expected no frame, the core served {:?}", frame.kind())
        }
        (Some(frame), Some(wire_hex)) => {
            assert_eq!(
                hex_to_bytes(wire_hex, where_),
                frame.to_bytes(),
                "{where_}: the served frame must be byte-exact"
            );
            let described = frame_from_json(&entry["expect_frame"], where_);
            assert_eq!(frame.to_bytes(), described.to_bytes(), "{where_}: structured expectation");
        }
    }
}

fn check_serve_snapshot(core: &mut SyncCore, where_: &str, entry: &Value) {
    let correlation = FrameId::new(entry["correlation"].as_u64().expect("correlation"));
    let causation = FrameId::new(entry["causation"].as_u64().unwrap_or(0));
    let frame = serve_snapshot(core, correlation, causation).expect("snapshot serves");
    assert_eq!(
        hex_to_bytes(entry["expect_wire"].as_str().expect("wire"), where_),
        frame.to_bytes(),
        "{where_}: the snapshot frame must be byte-exact"
    );
    let described = frame_from_json(&entry["expect_frame"], where_);
    assert_eq!(frame.to_bytes(), described.to_bytes(), "{where_}: structured expectation");
}

fn check_serve_trigger(where_: &str, entry: &Value) {
    let sequence = entry["sequence"].as_u64().expect("sequence");
    let operation_id = entry["operation_id"].as_str().expect("operation_id");
    let collection = entry["collection"].as_str().expect("collection");
    let correlation = FrameId::new(entry["correlation"].as_u64().expect("correlation"));
    let causation = FrameId::new(entry["causation"].as_u64().unwrap_or(0));
    let frame = trigger_frame(collection, sequence, operation_id, correlation, causation)
        .expect("trigger seals");
    assert_eq!(
        hex_to_bytes(entry["expect_wire"].as_str().expect("wire"), where_),
        frame.to_bytes(),
        "{where_}: the trigger frame must be byte-exact"
    );
    // The content-free rule, asserted structurally on the description:
    // a trigger carries exactly the three envelope fields plus a
    // collection scope and the two commit identifiers — no document,
    // field, or value key exists.
    let described = entry["expect_frame"].as_object().expect("structured frame");
    let mut keys: Vec<&String> = described.keys().collect();
    keys.sort_unstable();
    assert_eq!(
        keys,
        vec!["causation", "collection", "correlation", "kind", "operation_id", "storage_sequence",],
        "{where_}: the trigger description must be exactly envelope + scope + the two commit identifiers"
    );
    // And within its written budget.
    assert!(
        frame.to_bytes().len() <= FrameClass::Trigger.budget_bytes(),
        "{where_}: trigger frame exceeds its budget"
    );
}

fn check_decode_rejects(where_: &str, entry: &Value) {
    let wire = hex_to_bytes(entry["wire"].as_str().expect("wire"), where_);
    let expected = entry["error"].as_str().expect("error");
    match SyncV2Frame::from_bytes(&wire) {
        Err(error) => {
            assert_eq!(error_name(&error), expected, "{where_}: wrong rejection");
        }
        Ok(frame) => panic!("{where_}: bytes must be rejected, decoded {:?}", frame.kind()),
    }
}

/// The stable rejection names the corpus records.
fn error_name(error: &ProtocolError) -> &'static str {
    match error {
        ProtocolError::UnsupportedVersion(_) => "unsupported_version",
        ProtocolError::MalformedFrame(_) => "malformed_frame",
        ProtocolError::IdempotencyPairing(_) => "idempotency_pairing",
        ProtocolError::InvalidIdempotencyKey(_) => "invalid_idempotency_key",
    }
}

fn check_budget(where_: &str, entry: &Value) {
    let wire = hex_to_bytes(entry["wire"].as_str().expect("wire"), where_);
    let class = entry["class"].as_str().expect("class");
    let decoded = SyncV2Frame::from_bytes(&wire)
        .unwrap_or_else(|e| panic!("{where_}: budget frames must decode: {e}"));
    assert_eq!(
        decoded.body().frame_class(),
        class_from_name(class),
        "{where_}: the frame's class disagrees with the recorded class"
    );
    let budget = class_from_name(class).budget_bytes();
    let oversize_exception = matches!(
        decoded.body(),
        SyncV2Body::DeltaBatch { single_commit_oversize: true, commits: 1, .. }
    );
    let wire_len = wire.len();
    assert!(
        wire_len <= budget || oversize_exception,
        "{where_}: {wire_len} bytes exceed the {class} budget {budget} without the single-commit oversize exception"
    );
    if let Some(payload) = entry["payload_bytes"].as_u64() {
        let actual = decoded.payload_len() as u64;
        assert_eq!(actual, payload, "{where_}: payload byte count");
        let overhead = (wire.len() as u64) - actual;
        assert_eq!(
            Some(overhead),
            entry["envelope_overhead_bytes"].as_u64(),
            "{where_}: per-frame envelope overhead (wire minus payload)"
        );
    }
    if let Some(per_change) = entry["bytes_per_delivered_change"].as_u64() {
        let SyncV2Body::DeltaBatch { commits, .. } = decoded.body() else {
            panic!("{where_}: bytes-per-change applies to delta batches");
        };
        assert!(*commits > 0, "{where_}: the batch carries changes");
        assert_eq!(wire_len as u64 / commits, per_change, "{where_}: bytes per delivered change");
    }
    if let (Some(delta), Some(snapshot)) =
        (entry["delta_bytes"].as_u64(), entry["snapshot_bytes"].as_u64())
    {
        assert!(
            delta < snapshot,
            "{where_}: the delta path ({delta}) must stay below the snapshot re-send ({snapshot})"
        );
    }
}

fn class_from_name(name: &str) -> FrameClass {
    match name {
        "session" => FrameClass::Session,
        "subscription" => FrameClass::Subscription,
        "acknowledgement" => FrameClass::Acknowledgement,
        "heartbeat" => FrameClass::Heartbeat,
        "delivery" => FrameClass::Delivery,
        "trigger" => FrameClass::Trigger,
        "control" => FrameClass::Control,
        "mutation" => FrameClass::Mutation,
        other => panic!("unknown frame class {other}"),
    }
}

fn checkpoint_from_json(value: &Value, where_: &str) -> terrabase_sync_core::SyncCheckpoint {
    if value.is_null() {
        return terrabase_sync_core::SyncCheckpoint::initial();
    }
    let ends = value["ends"]
        .as_array()
        .unwrap_or_else(|| panic!("{where_}: checkpoint ends"))
        .iter()
        .map(|pair| {
            let pair = pair.as_array().expect("pair");
            (pair[0].as_u64().expect("peer"), pair[1].as_u64().expect("end"))
        })
        .collect();
    terrabase_sync_core::SyncCheckpoint::from_ends(
        ends,
        value["storage_sequence"].as_u64().expect("storage_sequence"),
    )
}

/// Builds a frame from its structured JSON description (the same shape
/// every SDK's harness will read).
fn frame_from_json(value: &Value, where_: &str) -> SyncV2Frame {
    let kind = value["kind"].as_str().expect("kind");
    let correlation = FrameId::new(
        value["correlation"].as_u64().unwrap_or_else(|| panic!("{where_}: correlation")),
    );
    let causation = FrameId::new(value["causation"].as_u64().unwrap_or(0));
    let idempotency = value["idempotency"]
        .as_str()
        .map(|key| IdempotencyKey::new(key).expect("vector idempotency key"));
    let body = match kind {
        "session_setup" => SyncV2Body::SessionSetup {
            collections: strings_from(&value["collections"]),
            resume: value["resume"]
                .as_object()
                .map(|c| checkpoint_from_json(&Value::Object(c.clone()), where_)),
        },
        "session_accepted" => SyncV2Body::SessionAccepted {
            session: value["session"].as_u64().expect("session"),
            server: checkpoint_from_json(&value["server_checkpoint"], where_),
            collections: strings_from(&value["collections"]),
        },
        "subscription_modify" => SyncV2Body::SubscriptionModify {
            add: strings_from(&value["add"]),
            remove: strings_from(&value["remove"]),
        },
        "subscription_changed" => {
            SyncV2Body::SubscriptionChanged { active: strings_from(&value["active"]) }
        }
        "checkpoint_ack" => SyncV2Body::CheckpointAck {
            checkpoint: checkpoint_from_json(&value["checkpoint"], where_),
        },
        "heartbeat" => SyncV2Body::Heartbeat,
        "delta_batch" => SyncV2Body::DeltaBatch {
            codec: codec_from(&value["codec"]),
            payload: hex_to_bytes(value["payload"].as_str().expect("payload"), where_),
            next: checkpoint_from_json(&value["next"], where_),
            within_budget: value["within_budget"].as_bool().expect("within_budget"),
            single_commit_oversize: value["single_commit_oversize"]
                .as_bool()
                .expect("single_commit_oversize"),
            commits: value["commits"].as_u64().expect("commits"),
        },
        "snapshot_answer" => SyncV2Body::SnapshotAnswer {
            codec: codec_from(&value["codec"]),
            payload: hex_to_bytes(value["payload"].as_str().expect("payload"), where_),
            checkpoint: checkpoint_from_json(&value["checkpoint"], where_),
        },
        "trigger" => SyncV2Body::Trigger {
            collection: value["collection"].as_str().expect("collection").to_string(),
            storage_sequence: value["storage_sequence"].as_u64().expect("storage_sequence"),
            operation_id: value["operation_id"].as_str().expect("operation_id").to_string(),
        },
        "resync_required" => SyncV2Body::ResyncRequired {
            floor: checkpoint_from_json(&value["floor"], where_),
            reason: match value["reason"].as_str().expect("reason") {
                "behind-retained-floor" => ResyncReason::BehindRetainedFloor,
                "history-compacted" => ResyncReason::HistoryCompacted,
                other => panic!("{where_}: unknown resync reason {other}"),
            },
        },
        "region_route_change" => SyncV2Body::RegionRouteChange {
            region: value["region"].as_str().expect("region").to_string(),
        },
        "error" => SyncV2Body::Error {
            code: error_code_from(&value["code"]),
            detail: value["detail"].as_str().map(str::to_string),
        },
        "mutation_request" => SyncV2Body::MutationRequest {
            collection: value["collection"].as_str().expect("collection").to_string(),
            document: value["document"].as_str().expect("document").to_string(),
            write: if let Some(fields) = value["put"].as_object() {
                let mut body = BTreeMap::new();
                for (key, field) in fields {
                    body.insert(key.clone(), json_to_sync_value(field));
                }
                DocumentWrite::Put { body }
            } else if value["delete"].as_bool().unwrap_or(false) {
                DocumentWrite::Delete
            } else {
                panic!("{where_}: mutation carries no put or delete");
            },
        },
        "mutation_ack" => SyncV2Body::MutationAck {
            outcome: match value["outcome"].as_str().expect("outcome") {
                "applied" => MutationOutcome::Applied {
                    storage_sequence: value["storage_sequence"].as_u64().expect("sequence"),
                    operation_id: value["operation_id"].as_str().expect("operation").to_string(),
                },
                "rejected" => {
                    MutationOutcome::Rejected { reason: error_code_from(&value["rejection"]) }
                }
                other => panic!("{where_}: unknown outcome {other}"),
            },
        },
        other => panic!("{where_}: unknown frame kind {other}"),
    };
    SyncV2Frame::seal(body, correlation, causation, idempotency)
        .unwrap_or_else(|e| panic!("{where_}: the described frame must seal: {e}"))
}

fn codec_from(value: &Value) -> PayloadCodec {
    match value.as_str().expect("codec") {
        "terrabase-native/1" => PayloadCodec::NativeDelta1,
        "terrabase-snapshot/1" => PayloadCodec::NativeSnapshot1,
        other => panic!("unknown payload codec {other}"),
    }
}

fn error_code_from(value: &Value) -> ErrorCode {
    match value.as_str().expect("error code") {
        "unsupported_version" => ErrorCode::UnsupportedVersion,
        "malformed_frame" => ErrorCode::MalformedFrame,
        "checkpoint_refused" => ErrorCode::CheckpointRefused,
        "scope_refused" => ErrorCode::ScopeRefused,
        "session_closed" => ErrorCode::SessionClosed,
        "overloaded" => ErrorCode::Overloaded,
        other => panic!("unknown error code {other}"),
    }
}

fn strings_from(value: &Value) -> Vec<String> {
    value
        .as_array()
        .expect("string array")
        .iter()
        .map(|s| s.as_str().expect("string").to_string())
        .collect()
}

fn hex_to_bytes(hex: &str, where_: &str) -> Vec<u8> {
    assert!(hex.len().is_multiple_of(2), "{where_}: odd hex length");
    let mut bytes = Vec::with_capacity(hex.len() / 2);
    let chars: Vec<char> = hex.chars().collect();
    for pair in chars.chunks(2) {
        let byte = u8::from_str_radix(&pair.iter().collect::<String>(), 16)
            .unwrap_or_else(|e| panic!("{where_}: bad hex: {e}"));
        bytes.push(byte);
    }
    bytes
}

fn bytes_to_hex(bytes: &[u8]) -> String {
    let mut out = String::with_capacity(bytes.len() * 2);
    for byte in bytes {
        use std::fmt::Write as _;
        let _ = write!(out, "{byte:02x}");
    }
    out
}

// ---------------------------------------------------------------------------
// Regeneration: typed scenario definitions -> the committed corpus.
// ---------------------------------------------------------------------------

/// The ignored regenerator. Run explicitly:
/// `cargo test --test vectors_runner -- --ignored regenerate_corpus`.
#[test]
#[ignore = "rewrites the committed golden corpus; run explicitly after a deliberate codec change"]
fn regenerate_corpus() {
    let dir = corpus_dir();
    fs::create_dir_all(&dir).expect("create corpus dir");
    for category in CATEGORIES {
        let corpus = build_corpus(category);
        let path = dir.join(format!("{category}.json"));
        fs::write(&path, serde_json::to_string_pretty(&corpus).expect("serialize"))
            .expect("write corpus file");
        eprintln!("wrote {}", path.display());
    }
}

fn build_corpus(category: &str) -> Value {
    match category {
        "fresh-session" => fresh_session_corpus(),
        "checkpoint-resume" => checkpoint_resume_corpus(),
        "reconnect-replay" => reconnect_replay_corpus(),
        "slow-consumer" => slow_consumer_corpus(),
        "region-route-change" => region_route_change_corpus(),
        "resync-required" => resync_required_corpus(),
        "malformed-frame-rejection" => malformed_frame_rejection_corpus(),
        "repeated-change-delta" => repeated_change_delta_corpus(),
        "trigger-notification" => trigger_notification_corpus(),
        other => panic!("unknown category {other}"),
    }
}

/// Shared scenario shape: scope, config, initial journal.
struct Scenario {
    tenant: &'static str,
    collection: &'static str,
    budget: usize,
    window: usize,
    journal: Vec<Value>,
}

impl Scenario {
    fn core(&self) -> SyncCore {
        let mut core = SyncCore::new(
            SyncScope::new(self.tenant, self.collection),
            SyncCoreConfig { batch_byte_budget: self.budget, checkpoint_window: self.window },
        )
        .expect("core builds");
        for commit in &self.journal {
            apply_journal(&mut core, commit);
        }
        core
    }

    fn header(&self) -> Value {
        json!({
            "schema": "terrascale.sync.v2/vector-corpus@2",
            "protocol": "terrascale.sync.v2",
            "protocol_version": 2,
            "category": "",
            "description": "",
            "server": {
                "scope": { "tenant": self.tenant, "collection": self.collection },
                "config": { "batch_byte_budget": self.budget, "checkpoint_window": self.window },
                "journal_before": self.journal,
            },
            "steps": [],
        })
    }
}

fn journal_put(sequence: u64, document: &str, fields: &[(&str, &str)]) -> Value {
    let mut put = serde_json::Map::new();
    for (key, value) in fields {
        put.insert((*key).to_string(), json!(value));
    }
    json!({
        "sequence": sequence,
        "operation_id": format!("op-{sequence:06}"),
        "document": document,
        "put": put,
    })
}

fn journal_delete(sequence: u64, document: &str) -> Value {
    json!({
        "sequence": sequence,
        "operation_id": format!("op-{sequence:06}"),
        "document": document,
        "delete": true,
    })
}

/// Records a frame as the {wire, frame} pair every SDK harness asserts.
fn recorded(frame: &SyncV2Frame) -> Value {
    json!({
        "wire": bytes_to_hex(&frame.to_bytes()),
        "frame": frame_to_json(frame),
    })
}

/// The envelope fields every structured frame description carries.
fn envelope_json(frame: &SyncV2Frame) -> Value {
    let mut value = json!({
        "kind": kind_name(frame.kind()),
        "correlation": frame.correlation().value(),
        "causation": frame.causation().value(),
    });
    if let Some(key) = frame.idempotency() {
        value
            .as_object_mut()
            .expect("object")
            .insert("idempotency".to_string(), json!(key.as_str()));
    }
    value
}

fn frame_to_json(frame: &SyncV2Frame) -> Value {
    let mut value = envelope_json(frame);
    let object = value.as_object_mut().expect("object");
    match frame.body() {
        SyncV2Body::SessionSetup { collections, resume } => {
            object.insert("collections".into(), json!(collections));
            object.insert("resume".into(), resume.as_ref().map_or(Value::Null, checkpoint_to_json));
        }
        SyncV2Body::SessionAccepted { session, server, collections } => {
            object.insert("session".into(), json!(session));
            object.insert("server_checkpoint".into(), checkpoint_to_json(server));
            object.insert("collections".into(), json!(collections));
        }
        SyncV2Body::SubscriptionModify { add, remove } => {
            object.insert("add".into(), json!(add));
            object.insert("remove".into(), json!(remove));
        }
        SyncV2Body::SubscriptionChanged { active } => {
            object.insert("active".into(), json!(active));
        }
        SyncV2Body::CheckpointAck { checkpoint } => {
            object.insert("checkpoint".into(), checkpoint_to_json(checkpoint));
        }
        SyncV2Body::Heartbeat => {}
        SyncV2Body::DeltaBatch {
            codec,
            payload,
            next,
            within_budget,
            single_commit_oversize,
            commits,
        } => {
            object.insert("codec".into(), json!(codec_name(*codec)));
            object.insert("payload".into(), json!(bytes_to_hex(payload)));
            object.insert("next".into(), checkpoint_to_json(next));
            object.insert("within_budget".into(), json!(within_budget));
            object.insert("single_commit_oversize".into(), json!(single_commit_oversize));
            object.insert("commits".into(), json!(commits));
        }
        SyncV2Body::SnapshotAnswer { codec, payload, checkpoint } => {
            object.insert("codec".into(), json!(codec_name(*codec)));
            object.insert("payload".into(), json!(bytes_to_hex(payload)));
            object.insert("checkpoint".into(), checkpoint_to_json(checkpoint));
        }
        SyncV2Body::Trigger { collection, storage_sequence, operation_id } => {
            object.insert("collection".into(), json!(collection));
            object.insert("storage_sequence".into(), json!(storage_sequence));
            object.insert("operation_id".into(), json!(operation_id));
        }
        SyncV2Body::ResyncRequired { floor, reason } => {
            object.insert("floor".into(), checkpoint_to_json(floor));
            object.insert(
                "reason".into(),
                json!(match reason {
                    ResyncReason::BehindRetainedFloor => "behind-retained-floor",
                    ResyncReason::HistoryCompacted => "history-compacted",
                }),
            );
        }
        SyncV2Body::RegionRouteChange { region } => {
            object.insert("region".into(), json!(region));
        }
        SyncV2Body::Error { code, detail } => {
            object.insert("code".into(), json!(error_code_name(*code)));
            object.insert("detail".into(), detail.clone().map_or(Value::Null, |d| json!(d)));
        }
        SyncV2Body::MutationRequest { collection, document, write } => {
            object.insert("collection".into(), json!(collection));
            object.insert("document".into(), json!(document));
            match write {
                DocumentWrite::Put { body } => {
                    let mut put = serde_json::Map::new();
                    for (key, field) in body {
                        put.insert(key.clone(), sync_value_to_json(field));
                    }
                    object.insert("put".into(), Value::Object(put));
                }
                DocumentWrite::Delete => {
                    object.insert("delete".into(), json!(true));
                }
            }
        }
        SyncV2Body::MutationAck { outcome } => match outcome {
            MutationOutcome::Applied { storage_sequence, operation_id } => {
                object.insert("outcome".into(), json!("applied"));
                object.insert("storage_sequence".into(), json!(storage_sequence));
                object.insert("operation_id".into(), json!(operation_id));
            }
            MutationOutcome::Rejected { reason } => {
                object.insert("outcome".into(), json!("rejected"));
                object.insert("rejection".into(), json!(error_code_name(*reason)));
            }
        },
    }
    value
}

fn sync_value_to_json(value: &SyncValue) -> Value {
    match value {
        SyncValue::Null => Value::Null,
        SyncValue::Bool(b) => json!(b),
        SyncValue::Int(i) => json!(i),
        SyncValue::Float(x) => json!(x),
        SyncValue::Str(s) => json!(s),
    }
}

fn checkpoint_to_json(checkpoint: &terrabase_sync_core::SyncCheckpoint) -> Value {
    json!({
        "ends": checkpoint
            .ends()
            .iter()
            .map(|(peer, end)| json!([peer, end]))
            .collect::<Vec<_>>(),
        "storage_sequence": checkpoint.storage_sequence,
    })
}

fn kind_name(kind: FrameKind) -> &'static str {
    match kind {
        FrameKind::SessionSetup => "session_setup",
        FrameKind::SessionAccepted => "session_accepted",
        FrameKind::SubscriptionModify => "subscription_modify",
        FrameKind::SubscriptionChanged => "subscription_changed",
        FrameKind::CheckpointAck => "checkpoint_ack",
        FrameKind::Heartbeat => "heartbeat",
        FrameKind::DeltaBatch => "delta_batch",
        FrameKind::SnapshotAnswer => "snapshot_answer",
        FrameKind::Trigger => "trigger",
        FrameKind::ResyncRequired => "resync_required",
        FrameKind::RegionRouteChange => "region_route_change",
        FrameKind::Error => "error",
        FrameKind::MutationRequest => "mutation_request",
        FrameKind::MutationAck => "mutation_ack",
    }
}

fn codec_name(codec: PayloadCodec) -> &'static str {
    match codec {
        PayloadCodec::NativeDelta1 => "terrabase-native/1",
        PayloadCodec::NativeSnapshot1 => "terrabase-snapshot/1",
    }
}

fn error_code_name(code: ErrorCode) -> &'static str {
    match code {
        ErrorCode::UnsupportedVersion => "unsupported_version",
        ErrorCode::MalformedFrame => "malformed_frame",
        ErrorCode::CheckpointRefused => "checkpoint_refused",
        ErrorCode::ScopeRefused => "scope_refused",
        ErrorCode::SessionClosed => "session_closed",
        ErrorCode::Overloaded => "overloaded",
    }
}

/// A `serve_delta` step recorded from the live core.
fn serve_delta_step(
    core: &mut SyncCore,
    from: &terrabase_sync_core::SyncCheckpoint,
    correlation: u64,
) -> Value {
    let served =
        serve_delta(core, from, FrameId::new(correlation), FrameId::new(1)).expect("serves");
    match served {
        None => json!({ "serve_delta": {
            "from": checkpoint_to_json(from), "correlation": correlation, "causation": 1, "expect_wire": null,
        } }),
        Some(frame) => json!({ "serve_delta": {
            "from": checkpoint_to_json(from),
            "correlation": correlation,
            "causation": 1,
            "expect_wire": bytes_to_hex(&frame.to_bytes()),
            "expect_frame": frame_to_json(&frame),
        } }),
    }
}

/// A client frame step.
fn client_frame_step(frame: &SyncV2Frame) -> Value {
    json!({ "client_frame": recorded(frame) })
}

fn budget_step(frame: &SyncV2Frame, extras: Value) -> Value {
    let mut step = json!({
        "budget": {
            "wire": bytes_to_hex(&frame.to_bytes()),
            "class": class_name(frame.body().frame_class()),
        }
    });
    let object = step["budget"].as_object_mut().expect("object");
    let payload = frame.payload_len();
    if payload > 0
        || matches!(frame.body(), SyncV2Body::DeltaBatch { .. } | SyncV2Body::SnapshotAnswer { .. })
    {
        object.insert("payload_bytes".to_string(), json!(payload));
        object
            .insert("envelope_overhead_bytes".to_string(), json!(frame.to_bytes().len() - payload));
    }
    match extras {
        Value::Null => {}
        Value::Object(extra) => {
            for (key, value) in extra {
                object.insert(key, value);
            }
        }
        _ => panic!("extras must be an object"),
    }
    step
}

fn class_name(class: FrameClass) -> &'static str {
    match class {
        FrameClass::Session => "session",
        FrameClass::Subscription => "subscription",
        FrameClass::Acknowledgement => "acknowledgement",
        FrameClass::Heartbeat => "heartbeat",
        FrameClass::Delivery => "delivery",
        FrameClass::Trigger => "trigger",
        FrameClass::Control => "control",
        FrameClass::Mutation => "mutation",
    }
}

// --- The nine scenario definitions ---------------------------------------

fn fresh_session_corpus() -> Value {
    let scenario = Scenario {
        tenant: "tenant-a",
        collection: "orders",
        budget: 8 * 1024,
        window: 128,
        journal: vec![
            journal_put(1, "d-01", &[("f000", "v0001"), ("f001", "10")]),
            journal_put(2, "d-02", &[("f000", "v0002")]),
            journal_delete(3, "d-01"),
        ],
    };
    let mut core = scenario.core();
    let mut corpus = scenario.header();
    corpus["category"] = json!("fresh-session");
    corpus["description"] = json!(
        "A fresh session opens with no checkpoint: the server accepts the session and answers with a full snapshot at the server's current checkpoint."
    );
    let setup = SyncV2Frame::seal(
        SyncV2Body::SessionSetup { collections: vec!["orders".to_string()], resume: None },
        FrameId::new(1),
        FrameId::new(0),
        None,
    )
    .expect("seals");
    let accepted = SyncV2Frame::seal(
        SyncV2Body::SessionAccepted {
            session: 0x5E55_1001,
            server: core.checkpoint(),
            collections: vec!["orders".to_string()],
        },
        FrameId::new(101),
        FrameId::new(1),
        None,
    )
    .expect("seals");
    let snapshot = serve_snapshot(&core, FrameId::new(102), FrameId::new(1)).expect("serves");
    let ack = SyncV2Frame::seal(
        SyncV2Body::CheckpointAck { checkpoint: core.checkpoint() },
        FrameId::new(2),
        FrameId::new(102),
        None,
    )
    .expect("seals");
    let at_current = core.checkpoint();
    let steps = vec![
        client_frame_step(&setup),
        json!({ "note": "the server accepts the session and states its checkpoint" }),
        json!({ "server_frame": recorded(&accepted) }),
        json!({ "note": "a fresh session receives the full state as one snapshot answer" }),
        json!({ "server_frame": recorded(&snapshot) }),
        json!({ "note": "the client acknowledges its resumption position" }),
        client_frame_step(&ack),
        serve_delta_step(&mut core, &at_current, 3),
        budget_step(&snapshot, Value::Null),
    ];
    corpus["steps"] = json!(steps);
    corpus
}

fn checkpoint_resume_corpus() -> Value {
    let scenario = Scenario {
        tenant: "tenant-a",
        collection: "orders",
        budget: 8 * 1024,
        window: 128,
        journal: vec![
            journal_put(1, "d-01", &[("f000", "v0001")]),
            journal_put(2, "d-02", &[("f000", "v0002")]),
        ],
    };
    let mut core = scenario.core();
    let mut corpus = scenario.header();
    corpus["category"] = json!("checkpoint-resume");
    corpus["description"] = json!(
        "A resuming session presents its durable checkpoint: the server serves exactly the delta suffix after it, and the next checkpoint advances."
    );
    // Absorb the first commit as the session's known state.
    let at = {
        let first = terrabase_sync_core::SyncCheckpoint::initial();
        match serve_delta(&core, &first, FrameId::new(1), FrameId::new(0))
            .expect("serves")
            .expect("delta")
            .body()
        {
            SyncV2Body::DeltaBatch { next, .. } => next.clone(),
            other => panic!("unexpected {other:?}"),
        }
    };
    let setup = SyncV2Frame::seal(
        SyncV2Body::SessionSetup {
            collections: vec!["orders".to_string()],
            resume: Some(at.clone()),
        },
        FrameId::new(1),
        FrameId::new(0),
        None,
    )
    .expect("seals");
    let ack = SyncV2Frame::seal(
        SyncV2Body::CheckpointAck { checkpoint: at.clone() },
        FrameId::new(2),
        FrameId::new(1),
        None,
    )
    .expect("seals");
    let third = journal_put(3, "d-03", &[("f000", "v0003"), ("f001", "true")]);
    apply_journal(&mut core, &third);
    let mut steps = vec![
        client_frame_step(&setup),
        json!({ "note": "the resumed session acknowledges where it stands" }),
        client_frame_step(&ack),
        json!({ "note": "one further commit lands on the journal" }),
        json!({ "apply": third }),
        serve_delta_step(&mut core, &at, 3),
    ];
    steps.push(budget_step(
        &SyncV2Frame::from_bytes(&hex_to_bytes(
            steps
                .last()
                .and_then(|s| s["serve_delta"]["expect_wire"].as_str())
                .expect("the serve step records its wire bytes"),
            "checkpoint-resume",
        ))
        .expect("decodes"),
        Value::Null,
    ));
    corpus["steps"] = json!(steps);
    corpus
}

fn reconnect_replay_corpus() -> Value {
    let scenario = Scenario {
        tenant: "tenant-a",
        collection: "orders",
        budget: 8 * 1024,
        window: 128,
        journal: vec![
            journal_put(1, "d-01", &[("f000", "v0001")]),
            journal_put(2, "d-02", &[("f000", "v0002")]),
            journal_put(3, "d-01", &[("f000", "v0001b"), ("f001", "3")]),
        ],
    };
    let mut core = scenario.core();
    let mut corpus = scenario.header();
    corpus["category"] = json!("reconnect-replay");
    corpus["description"] = json!(
        "At-least-once delivery: after a reconnect from the same unacknowledged checkpoint, the server re-serves the identical delta batch, and re-served batches are idempotent at the consumer."
    );
    let from = terrabase_sync_core::SyncCheckpoint::initial();
    let first = serve_delta_step(&mut core, &from, 1);
    let replay = serve_delta_step(&mut core, &from, 2);
    let ack = SyncV2Frame::seal(
        SyncV2Body::CheckpointAck { checkpoint: checkpoint_of(&first) },
        FrameId::new(3),
        FrameId::new(2),
        None,
    )
    .expect("seals");
    let after_ack = serve_delta_step(&mut core, &checkpoint_of(&first), 4);
    let steps = vec![
        json!({ "note": "the first delivery is lost with the connection" }),
        first.clone(),
        json!({ "note": "the session reconnects from the same checkpoint: byte-identical re-delivery (different correlation, same content)" }),
        replay,
        json!({ "note": "the consumer acknowledges; retransmission is checkpoint-bounded" }),
        client_frame_step(&ack),
        json!({ "note": "from the acknowledged checkpoint nothing is re-served" }),
        after_ack,
    ];
    corpus["steps"] = json!(steps);
    corpus
}

/// Reads the served checkpoint out of a recorded `serve_delta` step.
fn checkpoint_of(step: &Value) -> terrabase_sync_core::SyncCheckpoint {
    checkpoint_from_json(&step["serve_delta"]["expect_frame"]["next"], "reconnect")
}

fn slow_consumer_corpus() -> Value {
    // A tiny budget forces the backlog of a slow consumer into several
    // budgeted batches; one deliberately oversized single commit rides
    // alone with the oversize flag.
    let scenario = Scenario {
        tenant: "tenant-a",
        collection: "orders",
        budget: 512,
        window: 256,
        journal: Vec::new(),
    };
    let mut core = scenario.core();
    let mut corpus = scenario.header();
    corpus["category"] = json!("slow-consumer");
    corpus["description"] = json!(
        "A slow consumer drains a backlog in byte-budgeted batches: each batch fits the configured budget, and a single commit whose delta alone exceeds the budget is delivered alone and flagged."
    );
    let mut steps: Vec<Value> = Vec::new();
    let mut sequence = 0u64;
    for index in 0..24u64 {
        sequence += 1;
        steps.push(json!({ "apply": journal_put(sequence, &format!("d-{index:02}"), &[
            ("f000", "vvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvv"),
            ("f001", "wwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwww"),
        ]) }));
    }
    // Re-apply all journal entries on the live core.
    for step in &steps {
        if let Some(apply) = step["apply"].as_object() {
            apply_journal(&mut core, &Value::Object(apply.clone()));
        }
    }
    let mut correlation = 1u64;
    let mut from = terrabase_sync_core::SyncCheckpoint::initial();
    let mut batches = 0;
    loop {
        let step = serve_delta_step(&mut core, &from, correlation);
        if step["serve_delta"]["expect_wire"].is_null() {
            steps.push(json!({ "note": "the backlog is drained" }));
            break;
        }
        batches += 1;
        let frame_bytes = hex_to_bytes(
            step["serve_delta"]["expect_wire"].as_str().expect("wire"),
            "slow-consumer",
        );
        let frame = SyncV2Frame::from_bytes(&frame_bytes).expect("decodes");
        let SyncV2Body::DeltaBatch { next, within_budget, single_commit_oversize, .. } =
            frame.body()
        else {
            panic!("expected a delta batch");
        };
        assert!(
            frame_bytes.len() <= 512 || (*single_commit_oversize && !within_budget),
            "batch exceeds budget without the exception"
        );
        let budget = budget_step(&frame, Value::Null);
        steps.push(step);
        steps.push(budget);
        from = next.clone();
        correlation += 1;
    }
    assert!(batches >= 2, "the tiny budget must split the backlog, got {batches}");
    // One oversized single commit: a document with a very large field.
    sequence += 1;
    let big = "x".repeat(700);
    steps.push(json!({ "apply": journal_put(sequence, "d-big", &[("f000", &big)]) }));
    apply_journal(&mut core, &journal_put(sequence, "d-big", &[("f000", &big)]));
    let step = serve_delta_step(&mut core, &from, correlation);
    let frame_bytes =
        hex_to_bytes(step["serve_delta"]["expect_wire"].as_str().expect("wire"), "slow-consumer");
    let frame = SyncV2Frame::from_bytes(&frame_bytes).expect("decodes");
    let SyncV2Body::DeltaBatch { single_commit_oversize, commits, .. } = frame.body() else {
        panic!("expected a delta batch");
    };
    assert!(*single_commit_oversize, "the oversized commit rides alone and flagged");
    assert_eq!(*commits, 1);
    steps.push(step);
    steps.push(budget_step(&frame, json!({ "bytes_per_delivered_change": frame_bytes.len() })));
    corpus["steps"] = json!(steps);
    corpus
}

fn region_route_change_corpus() -> Value {
    let scenario = Scenario {
        tenant: "tenant-a",
        collection: "orders",
        budget: 8 * 1024,
        window: 128,
        journal: vec![journal_put(1, "d-01", &[("f000", "v0001")])],
    };
    let core = scenario.core();
    let mut corpus = scenario.header();
    corpus["category"] = json!("region-route-change");
    corpus["description"] = json!(
        "A region route change tells the session its serving region moved: a small advisory control frame; the client re-resolves the endpoint through the Platform and resumes from its checkpoint."
    );
    let route = SyncV2Frame::seal(
        SyncV2Body::RegionRouteChange { region: "eu-1".to_string() },
        FrameId::new(201),
        FrameId::new(1),
        None,
    )
    .expect("seals");
    let checkpoint = core.checkpoint();
    let ack = SyncV2Frame::seal(
        SyncV2Body::CheckpointAck { checkpoint: checkpoint.clone() },
        FrameId::new(1),
        FrameId::new(201),
        None,
    )
    .expect("seals");
    let steps = vec![
        json!({ "note": "the serving region changed under the live session" }),
        json!({ "server_frame": recorded(&route) }),
        json!({ "note": "the client carries its checkpoint to the new region" }),
        client_frame_step(&ack),
    ];
    corpus["steps"] = json!(steps);
    corpus
}

fn resync_required_corpus() -> Value {
    // A window of 8 makes the retained floor easy to fall behind.
    let scenario = Scenario {
        tenant: "tenant-a",
        collection: "orders",
        budget: 8 * 1024,
        window: 8,
        journal: Vec::new(),
    };
    let mut core = scenario.core();
    let mut corpus = scenario.header();
    corpus["category"] = json!("resync-required");
    corpus["description"] = json!(
        "A session behind the retained floor is never served a stale delta: the server answers resync-required with the floor, and the session recovers by adopting a full snapshot at the server's current checkpoint."
    );
    let mut steps: Vec<Value> = Vec::new();
    for index in 1..=2u64 {
        let commit = journal_put(index, &format!("d-{index:02}"), &[("f000", "v")]);
        apply_journal(&mut core, &commit);
        steps.push(json!({ "apply": commit }));
    }
    // The session drains the first batch while the window still holds it.
    let stalled = {
        let initial = terrabase_sync_core::SyncCheckpoint::initial();
        match serve_delta(&core, &initial, FrameId::new(1), FrameId::new(0))
            .expect("serves")
            .expect("delta")
            .body()
        {
            SyncV2Body::DeltaBatch { next, .. } => next.clone(),
            other => panic!("unexpected {other:?}"),
        }
    };
    // Overflow the window: the stalled checkpoint falls below the floor.
    for index in 9..=40u64 {
        let commit = journal_put(index, &format!("d-{:02}", index % 8), &[("f000", "vv")]);
        apply_journal(&mut core, &commit);
        steps.push(json!({ "apply": commit }));
    }
    let resync_step = serve_delta_step(&mut core, &stalled, 2);
    steps.push(
        json!({ "note": "the stalled session asks for deltas and is answered resync-required" }),
    );
    steps.push(resync_step);
    let snapshot = serve_snapshot(&core, FrameId::new(3), FrameId::new(2)).expect("serves");
    steps.push(json!({ "note": "the session adopts the snapshot as its state" }));
    steps.push(json!({ "server_frame": recorded(&snapshot) }));
    let at = match snapshot.body() {
        SyncV2Body::SnapshotAnswer { checkpoint, .. } => checkpoint.clone(),
        other => panic!("unexpected {other:?}"),
    };
    steps.push(serve_delta_step(&mut core, &at, 4));
    corpus["steps"] = json!(steps);
    corpus
}

fn malformed_frame_rejection_corpus() -> Value {
    let scenario = Scenario {
        tenant: "tenant-a",
        collection: "orders",
        budget: 8 * 1024,
        window: 128,
        journal: Vec::new(),
    };
    let core = scenario.core();
    let _ = core;
    let mut corpus = scenario.header();
    corpus["category"] = json!("malformed-frame-rejection");
    corpus["description"] = json!(
        "Fail-closed decode: unknown versions (including v1), unknown kinds, truncation, trailing bytes, lying lengths, and idempotency keys on non-mutation frames are all rejected with typed errors."
    );
    let good = SyncV2Frame::seal(
        SyncV2Body::SessionSetup { collections: vec!["orders".to_string()], resume: None },
        FrameId::new(1),
        FrameId::new(0),
        None,
    )
    .expect("seals")
    .to_bytes();
    let mut steps: Vec<Value> = Vec::new();
    // Unknown versions, including the v1 wire this contract does not speak.
    for version in [0u8, 1, 3, 0xFF] {
        let mut bad = good.clone();
        bad[0] = version;
        steps.push(json!({
            "note": format!("version {version} fails closed (v1 included: no wire compatibility)"),
            "decode_rejects": { "wire": bytes_to_hex(&bad), "error": "unsupported_version" },
        }));
    }
    // Unknown kind code.
    let mut unknown_kind = good.clone();
    unknown_kind[1] = 0xFF;
    unknown_kind[2] = 0x7F;
    steps.push(json!({
        "note": "an unknown frame kind fails closed",
        "decode_rejects": { "wire": bytes_to_hex(&unknown_kind), "error": "malformed_frame" },
    }));
    // Truncation.
    steps.push(json!({
        "note": "a truncated frame never decodes",
        "decode_rejects": { "wire": bytes_to_hex(&good[..good.len() / 2]), "error": "malformed_frame" },
    }));
    // Trailing bytes.
    let mut trailing = good.clone();
    trailing.push(0x00);
    steps.push(json!({
        "note": "trailing bytes after the body are rejected",
        "decode_rejects": { "wire": bytes_to_hex(&trailing), "error": "malformed_frame" },
    }));
    // A lying outer body length.
    let mut lying = good.clone();
    let body_len_at = 28;
    lying[body_len_at] ^= 0xFF;
    steps.push(json!({
        "note": "the outer body-length field must agree with the actual bytes",
        "decode_rejects": { "wire": bytes_to_hex(&lying), "error": "malformed_frame" },
    }));
    // Idempotency flag set on a non-mutation frame.
    let mut flagged = good.clone();
    flagged[19] = 0x01;
    steps.push(json!({
        "note": "an idempotency key on a non-mutation frame is a pairing violation",
        "decode_rejects": {
            "wire": bytes_to_hex(&flagged),
            "error": error_name(&SyncV2Frame::from_bytes(&flagged).expect_err("rejected")),
        },
    }));
    // The reverse pairing: a mutation body re-wrapped without its key.
    // The mutation frame is rebuilt byte-wise with the presence flag
    // cleared and the key section removed, so the body itself still
    // decodes and only the pairing rule rejects it.
    let mutation = SyncV2Frame::seal(
        SyncV2Body::MutationRequest {
            collection: "orders".to_string(),
            document: "d-01".to_string(),
            write: DocumentWrite::Delete,
        },
        FrameId::new(1),
        FrameId::new(0),
        Some(IdempotencyKey::new("idem-1").expect("key")),
    )
    .expect("seals")
    .to_bytes();
    let key_len =
        usize::try_from(u64::from_le_bytes(mutation[20..28].try_into().expect("eight bytes")))
            .expect("bounded");
    let mut unkeyed = Vec::with_capacity(mutation.len() - 8 - key_len);
    unkeyed.extend_from_slice(&mutation[..19]);
    unkeyed.push(0);
    unkeyed.extend_from_slice(&mutation[28 + key_len..]);
    steps.push(json!({
        "note": "a mutation frame without its idempotency key is a pairing violation",
        "decode_rejects": {
            "wire": bytes_to_hex(&unkeyed),
            "error": error_name(&SyncV2Frame::from_bytes(&unkeyed).expect_err("rejected")),
        },
    }));
    corpus["steps"] = json!(steps);
    corpus
}

fn repeated_change_delta_corpus() -> Value {
    // The bandwidth pattern PD-2026-08-26-13 D1 names: an established
    // state plus repeated changes to the same documents must ride
    // deltas, not re-sends. The vector records the delta batch for the
    // repeated changes against the snapshot of the same state; the
    // recorded per-change and overhead numbers are exact, deterministic
    // seed values for the SE-07 budget work (no measured claim).
    let mut journal: Vec<Value> = Vec::new();
    let mut sequence = 0u64;
    for document in 0..16u64 {
        sequence += 1;
        let fields: Vec<(String, String)> = (0..8)
            .map(|field| (format!("f{field:03}"), format!("established-{document:02}-{field:02}")))
            .collect();
        let field_refs: Vec<(&str, &str)> =
            fields.iter().map(|(k, v)| (k.as_str(), v.as_str())).collect();
        journal.push(journal_put(sequence, &format!("d-{document:02}"), &field_refs));
    }
    let scenario = Scenario {
        tenant: "tenant-a",
        collection: "orders",
        budget: 64 * 1024,
        window: 4096,
        journal,
    };
    let mut core = scenario.core();
    // The session holds the established state at this checkpoint.
    let established = core.checkpoint();
    let mut corpus = scenario.header();
    corpus["category"] = json!("repeated-change-delta");
    corpus["description"] = json!(
        "Repeated changes to established documents ride deltas, not re-sends: the delta batch for the repeated changes is recorded against the snapshot of the same state, the envelope overhead is exact, and bytes per delivered change are recorded for the SE-07 budget work."
    );
    let mut steps: Vec<Value> = Vec::new();
    let changes = 8u64;
    for index in 1..=changes {
        sequence += 1;
        let field_value = format!("repeated-{index:04}");
        let commit = journal_put(
            sequence,
            &format!("d-{:02}", index % 4),
            &[("f000", &field_value), ("f001", "touched")],
        );
        apply_journal(&mut core, &commit);
        steps.push(json!({ "apply": commit }));
    }
    let snapshot = serve_snapshot(&core, FrameId::new(1), FrameId::new(0)).expect("serves");
    let snapshot_bytes = snapshot.to_bytes().len();
    let delta_step = serve_delta_step(&mut core, &established, 2);
    let delta_wire =
        hex_to_bytes(delta_step["serve_delta"]["expect_wire"].as_str().expect("wire"), "repeated");
    let delta_frame = SyncV2Frame::from_bytes(&delta_wire).expect("decodes");
    let SyncV2Body::DeltaBatch { commits, .. } = delta_frame.body() else {
        panic!("expected a delta batch");
    };
    assert_eq!(*commits, changes, "the batch carries every repeated change");
    assert!(
        delta_wire.len() < snapshot_bytes,
        "the delta path ({}) must stay below the snapshot re-send ({}) for the same state",
        delta_wire.len(),
        snapshot_bytes
    );
    let per_change = delta_wire.len() / usize::try_from(changes).expect("changes fit");
    steps.push(json!({ "note": "the same state as one full snapshot, for comparison" }));
    steps.push(json!({ "server_frame": recorded(&snapshot) }));
    steps.push(delta_step);
    steps.push(budget_step(
        &delta_frame,
        json!({
            "bytes_per_delivered_change": per_change,
            "snapshot_bytes": snapshot_bytes,
            "delta_bytes": delta_wire.len(),
        }),
    ));
    corpus["steps"] = json!(steps);
    corpus
}

fn trigger_notification_corpus() -> Value {
    let scenario = Scenario {
        tenant: "tenant-a",
        collection: "orders",
        budget: 8 * 1024,
        window: 128,
        journal: Vec::new(),
    };
    let mut core = scenario.core();
    let mut corpus = scenario.header();
    corpus["category"] = json!("trigger-notification");
    corpus["description"] = json!(
        "On commit publication the server pushes a small, typed, content-free trigger notification: two commit identifiers plus the collection scope. The client answers by pulling delta batches from its checkpoint (push the hint, pull the delta)."
    );
    let mut steps: Vec<Value> = Vec::new();
    let session = SyncV2Frame::seal(
        SyncV2Body::SessionSetup { collections: vec!["orders".to_string()], resume: None },
        FrameId::new(1),
        FrameId::new(0),
        None,
    )
    .expect("seals");
    steps.push(client_frame_step(&session));
    let commit = journal_put(1, "d-01", &[("f000", "v0001")]);
    apply_journal(&mut core, &commit);
    steps.push(json!({ "apply": commit }));
    let trigger =
        trigger_frame("orders", 1, "op-000001", FrameId::new(11), FrameId::new(1)).expect("seals");
    steps.push(json!({ "note": "the trigger carries no document data: the client pulls" }));
    steps.push(json!({ "server_frame": recorded(&trigger) }));
    steps.push(json!({
        "serve_trigger": {
            "collection": "orders",
            "sequence": 1,
            "operation_id": "op-000001",
            "correlation": 11,
            "causation": 1,
            "expect_wire": bytes_to_hex(&trigger.to_bytes()),
            "expect_frame": frame_to_json(&trigger),
        }
    }));
    // The client pulls the delta the trigger announced.
    steps.push(serve_delta_step(&mut core, &terrabase_sync_core::SyncCheckpoint::initial(), 12));
    // A second commit: the trigger stays within its budget at worst-case
    // identifier lengths.
    let commit = journal_put(2, "d-02", &[("f000", "v0002")]);
    apply_journal(&mut core, &commit);
    steps.push(json!({ "apply": commit }));
    let long_trigger =
        trigger_frame(&"c".repeat(64), 2, &"o".repeat(64), FrameId::new(13), FrameId::new(1))
            .expect("seals");
    steps.push(json!({
        "budget": {
            "wire": bytes_to_hex(&long_trigger.to_bytes()),
            "class": "trigger",
            "payload_bytes": long_trigger.payload_len(),
            "envelope_overhead_bytes": long_trigger.to_bytes().len() - long_trigger.payload_len(),
        }
    }));
    corpus["steps"] = json!(steps);
    corpus
}
