//! The ADR 0024 extension-corpus generator: rewrites the committed
//! extension vectors under `vectors/terrascale.sync.v2-adr0024/` from
//! the typed scenario definitions in this file, using the engine's
//! real delivery core and protocol codec — exactly the regeneration
//! discipline `terrabase-sync-core`'s SE-04 runner holds for the base
//! corpus. Run explicitly:
//!
//! ```text
//! cargo test -p terrabase-sync-conformance \
//!   --test generate_extension_corpus -- --ignored
//! ```
//!
//! The committed corpus is the golden record: the kit's normal tests
//! replay it byte-exact against the same core, so a substrate change
//! that moves any byte is a contract change, not noise.

use std::collections::BTreeMap;
use std::fs;
use std::path::PathBuf;

use serde_json::Value;
use serde_json::json;
use terrabase_sync_core::FrameId;
use terrabase_sync_core::JournalChange;
use terrabase_sync_core::JournalCommit;
use terrabase_sync_core::SyncCheckpoint;
use terrabase_sync_core::SyncCore;
use terrabase_sync_core::SyncCoreConfig;
use terrabase_sync_core::SyncScope;
use terrabase_sync_core::SyncValue;
use terrabase_sync_core::protocol::ErrorCode;
use terrabase_sync_core::protocol::SyncV2Body;
use terrabase_sync_core::protocol::SyncV2Frame;
use terrabase_sync_core::protocol::serve_delta;
use terrabase_sync_core::protocol::serve_snapshot;

/// The extension corpus directory this generator owns.
fn corpus_dir() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("vectors/terrascale.sync.v2-adr0024")
}

/// The ignored regenerator.
#[test]
#[ignore = "rewrites the committed ADR 0024 extension corpus; run explicitly after a deliberate change"]
fn regenerate_extension_corpus() {
    fs::create_dir_all(corpus_dir()).expect("create the extension corpus dir");
    for corpus in [authorization_reset_corpus(), slow_consumer_overload_corpus()] {
        let name = corpus["category"].as_str().expect("category");
        let path = corpus_dir().join(format!("{name}.json"));
        fs::write(&path, serde_json::to_string_pretty(&corpus).expect("serialize"))
            .expect("write the corpus file");
        eprintln!("wrote {}", path.display());
    }
}

/// One scenario: scope, config, and the journal committed before the
/// session — the same `server` section shape the interpreter reads.
struct Scenario {
    tenant: &'static str,
    collection: &'static str,
    budget: usize,
    window: usize,
    journal: Vec<Value>,
}

impl Scenario {
    /// Builds the delivery core from the scenario's declared state.
    fn core(&self) -> SyncCore {
        let mut core = SyncCore::new(
            SyncScope::new(self.tenant, self.collection),
            SyncCoreConfig { batch_byte_budget: self.budget, checkpoint_window: self.window },
        )
        .expect("the core builds");
        for commit in &self.journal {
            apply_journal(&mut core, commit);
        }
        core
    }

    /// The corpus file header (the vector-corpus@2 identity).
    fn header(&self, category: &str, description: &str) -> Value {
        json!({
            "schema": "terrascale.sync.v2/vector-corpus@2",
            "protocol": "terrascale.sync.v2",
            "protocol_version": 2,
            "category": category,
            "description": description,
            "server": {
                "scope": { "tenant": self.tenant, "collection": self.collection },
                "config": { "batch_byte_budget": self.budget, "checkpoint_window": self.window },
                "journal_before": self.journal,
            },
            "steps": [],
        })
    }
}

/// Applies one journal commit description to a core (generator-side
/// twin of the interpreter's `apply` step).
fn apply_journal(core: &mut SyncCore, commit: &Value) {
    let change = if let Some(fields) = commit.get("put").and_then(Value::as_object) {
        let mut body = BTreeMap::new();
        for (key, value) in fields {
            body.insert(key.clone(), json_to_sync_value(value));
        }
        JournalChange::Put { body }
    } else {
        JournalChange::Delete
    };
    core.apply(&JournalCommit {
        storage_sequence: commit.get("sequence").and_then(Value::as_u64).expect("sequence"),
        operation_id: commit
            .get("operation_id")
            .and_then(Value::as_str)
            .expect("operation id")
            .to_string(),
        document_id: commit.get("document").and_then(Value::as_str).expect("document").to_string(),
        change,
    })
    .expect("the journal applies");
}

fn json_to_sync_value(value: &Value) -> SyncValue {
    match value {
        Value::Null => SyncValue::Null,
        Value::Bool(flag) => SyncValue::Bool(*flag),
        Value::Number(number) => number
            .as_i64()
            .map(SyncValue::Int)
            .or_else(|| number.as_f64().map(SyncValue::Float))
            .expect("a scalar vector value"),
        Value::String(text) => SyncValue::Str(text.clone()),
        other => panic!("unsupported vector value {other}"),
    }
}

fn journal_put(sequence: u64, document: &str, fields: &[(&str, Value)]) -> Value {
    let mut put = serde_json::Map::new();
    for (key, value) in fields {
        put.insert((*key).to_string(), value.clone());
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

// --- Recording helpers (the shapes the interpreter reads) -------------------

/// Records one frame as the `{wire, frame}` pair every harness asserts.
fn recorded(frame: &SyncV2Frame) -> Value {
    json!({
        "wire": bytes_to_hex(&frame.to_bytes()),
        "frame": frame_to_json(frame),
    })
}

/// The structured description of one frame.
fn frame_to_json(frame: &SyncV2Frame) -> Value {
    let mut value = json!({
        "kind": kind_name(frame.body()),
        "correlation": frame.correlation().value(),
        "causation": frame.causation().value(),
    });
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
        SyncV2Body::CheckpointAck { checkpoint } => {
            object.insert("checkpoint".into(), checkpoint_to_json(checkpoint));
        }
        SyncV2Body::DeltaBatch {
            payload,
            next,
            within_budget,
            single_commit_oversize,
            commits,
            ..
        } => {
            object.insert("codec".into(), json!("terrabase-native/1"));
            object.insert("payload".into(), json!(bytes_to_hex(payload)));
            object.insert("next".into(), checkpoint_to_json(next));
            object.insert("within_budget".into(), json!(within_budget));
            object.insert("single_commit_oversize".into(), json!(single_commit_oversize));
            object.insert("commits".into(), json!(commits));
        }
        SyncV2Body::SnapshotAnswer { payload, checkpoint, .. } => {
            object.insert("codec".into(), json!("terrabase-snapshot/1"));
            object.insert("payload".into(), json!(bytes_to_hex(payload)));
            object.insert("checkpoint".into(), checkpoint_to_json(checkpoint));
        }
        SyncV2Body::Error { code, detail } => {
            object.insert("code".into(), json!(code.name()));
            if let Some(text) = detail {
                object.insert("detail".into(), json!(text));
            }
        }
        other => panic!("the extension corpus records only the kinds it uses, found {other:?}"),
    }
    value
}

fn kind_name(body: &SyncV2Body) -> &'static str {
    match body {
        SyncV2Body::SessionSetup { .. } => "session_setup",
        SyncV2Body::SessionAccepted { .. } => "session_accepted",
        SyncV2Body::CheckpointAck { .. } => "checkpoint_ack",
        SyncV2Body::DeltaBatch { .. } => "delta_batch",
        SyncV2Body::SnapshotAnswer { .. } => "snapshot_answer",
        SyncV2Body::Error { .. } => "error",
        other => panic!("unrecorded kind {other:?}"),
    }
}

fn checkpoint_to_json(checkpoint: &SyncCheckpoint) -> Value {
    json!({
        "ends": checkpoint.ends().iter().map(|(peer, end)| json!([peer, end])).collect::<Vec<_>>(),
        "storage_sequence": checkpoint.storage_sequence,
    })
}

/// Records a `serve_delta` step: the served frame plus its exact
/// wire bytes and structured description.
fn delta_step(core: &SyncCore, from: &SyncCheckpoint, correlation: u64, causation: u64) -> Value {
    let frame = serve_delta(core, from, FrameId::new(correlation), FrameId::new(causation))
        .expect("the core serves")
        .expect("a delta batch is due");
    json!({
        "serve_delta": {
            "from": checkpoint_to_json(from),
            "correlation": correlation,
            "causation": causation,
            "expect_wire": bytes_to_hex(&frame.to_bytes()),
            "expect_frame": frame_to_json(&frame),
        }
    })
}

/// Records a `serve_snapshot` step.
fn snapshot_step(core: &SyncCore, correlation: u64, causation: u64) -> Value {
    let frame = serve_snapshot(core, FrameId::new(correlation), FrameId::new(causation))
        .expect("the core serves");
    json!({
        "serve_snapshot": {
            "correlation": correlation,
            "causation": causation,
            "expect_wire": bytes_to_hex(&frame.to_bytes()),
            "expect_frame": frame_to_json(&frame),
        }
    })
}

/// Records a `budget` step over one served frame's wire bytes: the
/// payload and envelope-overhead recount, and — for delta batches —
/// the per-delivered-change recount.
fn budget_step(frame: &SyncV2Frame) -> Value {
    let wire = frame.to_bytes();
    let mut entry = json!({
        "wire": bytes_to_hex(&wire),
        "class": frame.body().frame_class().name(),
        "payload_bytes": frame.body().payload_len() as u64,
        "envelope_overhead_bytes": (wire.len() - frame.body().payload_len()) as u64,
    });
    if let SyncV2Body::DeltaBatch { commits, .. } = frame.body()
        && *commits > 0
    {
        entry["bytes_per_delivered_change"] = json!((wire.len() as u64) / commits);
    }
    json!({ "budget": entry })
}

/// The served frame of a serve step, re-derived for the budget
/// recount (serve steps carry their wire under their kind key).
fn frame_of(step: &Value) -> SyncV2Frame {
    let wire = step
        .as_object()
        .and_then(|object| object.values().next().and_then(|inner| inner.get("expect_wire")))
        .and_then(Value::as_str)
        .expect("the serve step's wire");
    SyncV2Frame::from_bytes(&bytes_from_hex(wire)).expect("the recorded wire decodes")
}

fn bytes_to_hex(bytes: &[u8]) -> String {
    use std::fmt::Write as _;
    let mut out = String::with_capacity(bytes.len() * 2);
    for byte in bytes {
        let _ = write!(out, "{byte:02x}");
    }
    out
}

fn bytes_from_hex(hex: &str) -> Vec<u8> {
    (0..hex.len())
        .step_by(2)
        .map(|index| u8::from_str_radix(&hex[index..index + 2], 16).expect("hex"))
        .collect()
}

fn note(text: &str) -> Value {
    json!({ "note": text })
}

// --- The ADR 0024 scenarios -------------------------------------------------

/// The `authorization-reset` vector: the authorization-generation
/// change / reset state ADR 0024 requires and the base corpus defers.
///
/// The delivery substrate's authorization surface is the scope (one
/// tenant's collection). The vector pins, in wire frames: the typed
/// scope refusal that closes a live session when the grant over the
/// scope is revoked; the reconnect that presents the stale checkpoint
/// and is answered by a fresh authorized bootstrap (session acceptance
/// at the server's current checkpoint plus a full snapshot answer),
/// never a delta replay from the revoked position; and the client's
/// checkpoint acknowledgement of the re-bootstrap.
fn authorization_reset_corpus() -> Value {
    let scenario = Scenario {
        tenant: "tenant-a",
        collection: "orders",
        budget: 8192,
        window: 128,
        journal: vec![
            journal_put(1, "d-01", &[("f000", json!("v0001"))]),
            journal_put(2, "d-02", &[("f000", json!("v0002"))]),
            journal_put(3, "d-03", &[("f000", json!("v0003")), ("f001", json!(true))]),
            journal_delete(4, "d-02"),
        ],
    };
    let core = scenario.core();
    let live = SyncCheckpoint::from_ends(vec![(1, 3)], 3);
    let current = core.checkpoint();
    let served_delta = delta_step(&core, &live, 1, 0);
    let served = frame_of(&served_delta);
    let mut steps = vec![
        note(
            "the session is live and authorized at checkpoint (1,3): the delta suffix is served normally",
        ),
        served_delta,
        budget_step(&served),
    ];
    steps.push(note("the authorization generation changes mid-session: the principal's grant over this scope is revoked, and the server closes the session with the typed scope refusal"));
    steps.push(json!({ "server_frame": recorded(&SyncV2Frame::seal(
        SyncV2Body::Error {
            code: ErrorCode::ScopeRefused,
            detail: Some("authorization-generation-change".to_string()),
        },
        FrameId::new(2),
        FrameId::new(1),
        None,
    ).expect("the refusal seals")) }));
    steps.push(note("ADR 0024 authorization transitions: reconnect with the stale checkpoint re-evaluates current policy; the revoked generation is never resumed as if its permissions were current"));
    steps.push(json!({ "client_frame": recorded(&SyncV2Frame::seal(
        SyncV2Body::SessionSetup {
            collections: vec!["orders".to_string()],
            resume: Some(live.clone()),
        },
        FrameId::new(3),
        FrameId::new(0),
        None,
    ).expect("the resume setup seals")) }));
    steps.push(note("the answer is a fresh authorized bootstrap: acceptance at the server's current checkpoint, then a full snapshot answer — never a delta from the revoked position"));
    steps.push(json!({ "server_frame": recorded(&SyncV2Frame::seal(
        SyncV2Body::SessionAccepted {
            session: 41,
            server: current.clone(),
            collections: vec!["orders".to_string()],
        },
        FrameId::new(4),
        FrameId::new(3),
        None,
    ).expect("the acceptance seals")) }));
    steps.push(snapshot_step(&core, 5, 3));
    steps.push(json!({ "client_frame": recorded(&SyncV2Frame::seal(
        SyncV2Body::CheckpointAck { checkpoint: current },
        FrameId::new(6),
        FrameId::new(4),
        None,
    ).expect("the acknowledgement seals")) }));
    steps.push(note("honest boundary: authorization-generation evaluation is engine-side machinery above this seam (the ADR 0024 view state machine); this vector pins the wire behavior every client must handle — typed refusal, refused stale resume, fresh-snapshot re-bootstrap"));
    let mut corpus = scenario.header(
        "authorization-reset",
        "ADR 0024 authorization-generation change: a revoked scope closes the session with a typed refusal, the stale checkpoint never resumes, and the re-bootstrap is a fresh authorized snapshot.",
    );
    corpus["steps"] = json!(steps);
    corpus
}

/// The `slow-consumer-overload` vector: the tail of ADR 0024's
/// slow-client ladder the base corpus's `slow-consumer` category does
/// not reach — coalesce first (the budgeted batch), then reset, then
/// close with a typed resumable overload result; the client reconnects
/// from its last acknowledged checkpoint and the server serves exactly
/// the suffix after it.
fn slow_consumer_overload_corpus() -> Value {
    let scenario = Scenario {
        tenant: "tenant-a",
        collection: "orders",
        budget: 8192,
        window: 128,
        journal: vec![
            journal_put(1, "d-01", &[("f000", json!("v0001"))]),
            journal_put(2, "d-02", &[("f000", json!("v0002"))]),
            journal_put(3, "d-03", &[("f000", json!("v0003"))]),
            journal_put(4, "d-04", &[("f000", json!("v0004"))]),
            journal_put(5, "d-05", &[("f000", json!("v0005"))]),
            journal_put(6, "d-06", &[("f000", json!("v0006"))]),
        ],
    };
    let core = scenario.core();
    let current = core.checkpoint();
    let acked = SyncCheckpoint::from_ends(vec![(1, 2)], 2);
    let initial_delta = delta_step(&core, &SyncCheckpoint::initial(), 1, 0);
    let initial_batch = frame_of(&initial_delta);
    let snapshot = snapshot_step(&core, 2, 1);
    let snapshot_frame = frame_of(&snapshot);
    let resume_delta = delta_step(&core, &acked, 6, 4);
    let resume_batch = frame_of(&resume_delta);
    let mut steps = vec![
        note(
            "the slow consumer connects at the initial checkpoint: the whole backlog is coalesced into one byte-budgeted native delta batch",
        ),
        initial_delta,
        budget_step(&initial_batch),
        snapshot,
        note("the coalesced delta path stays below the snapshot re-send of the same state"),
        json!({
            "budget": {
                "wire": bytes_to_hex(&initial_batch.to_bytes()),
                "class": "delivery",
                "delta_bytes": (initial_batch.to_bytes().len() as u64),
                "snapshot_bytes": (snapshot_frame.to_bytes().len() as u64),
            }
        }),
        note(
            "the consumer stalls and acknowledges nothing: unacknowledged revisions, bytes, and time pass the session bound, and the server closes the session with the typed resumable overload result",
        ),
    ];
    steps.push(json!({ "server_frame": recorded(&SyncV2Frame::seal(
        SyncV2Body::Error {
            code: ErrorCode::Overloaded,
            detail: Some("slow-consumer-unacknowledged".to_string()),
        },
        FrameId::new(3),
        FrameId::new(1),
        None,
    ).expect("the overload result seals")) }));
    steps.push(note("the overload result is resumable, not fatal: the client reconnects from its last acknowledged checkpoint (1,2)"));
    steps.push(json!({ "client_frame": recorded(&SyncV2Frame::seal(
        SyncV2Body::SessionSetup {
            collections: vec!["orders".to_string()],
            resume: Some(acked.clone()),
        },
        FrameId::new(4),
        FrameId::new(0),
        None,
    ).expect("the resume setup seals")) }));
    steps.push(json!({ "server_frame": recorded(&SyncV2Frame::seal(
        SyncV2Body::SessionAccepted {
            session: 77,
            server: current.clone(),
            collections: vec!["orders".to_string()],
        },
        FrameId::new(5),
        FrameId::new(4),
        None,
    ).expect("the acceptance seals")) }));
    steps.push(resume_delta);
    steps.push(budget_step(&resume_batch));
    steps.push(json!({ "client_frame": recorded(&SyncV2Frame::seal(
        SyncV2Body::CheckpointAck { checkpoint: current },
        FrameId::new(7),
        FrameId::new(5),
        None,
    ).expect("the acknowledgement seals")) }));
    steps.push(note("only the suffix after the acknowledged checkpoint is re-served: the resume batch carries the commits after (1,2), never a replay from zero"));
    let mut corpus = scenario.header(
        "slow-consumer-overload",
        "ADR 0024 slow-client ladder tail: coalesced budgeted batches, then the typed resumable overload close, then resume from the last acknowledged checkpoint.",
    );
    corpus["steps"] = json!(steps);
    corpus
}
