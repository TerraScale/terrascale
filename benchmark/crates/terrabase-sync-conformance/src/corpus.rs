//! The corpus step interpreter: executes one golden-vector file of the
//! SE-04 `terrascale.sync.v2` corpus against the engine's delivery core
//! (the real [`terrabase_sync_core::SyncCore`] and protocol codec),
//! recording every check it makes instead of panicking on disagreement.
//!
//! This module is the reference semantics of "running the corpus": the
//! per-language harness contract documented in
//! `docs/sync/terrascale-sync-v2-conformance.md` describes exactly these
//! steps and check names, so a TypeScript or C# harness that makes the
//! same checks on the same data is conformance-equivalent to this one.

use std::collections::BTreeMap;
use std::fs;
use std::path::Path;

use serde_json::Value;
use terrabase_sync_core::FrameId;
use terrabase_sync_core::JournalChange;
use terrabase_sync_core::JournalCommit;
use terrabase_sync_core::SyncCheckpoint;
use terrabase_sync_core::SyncCore;
use terrabase_sync_core::SyncCoreConfig;
use terrabase_sync_core::SyncScope;
use terrabase_sync_core::SyncValue;
use terrabase_sync_core::protocol::DocumentWrite;
use terrabase_sync_core::protocol::ErrorCode;
use terrabase_sync_core::protocol::FrameClass;
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

use crate::CORPUS_SCHEMA;
use crate::Failure;
use crate::PROTOCOL;
use crate::PROTOCOL_VERSION;
use crate::VectorResult;

/// The step keys the corpus vocabulary accepts. A step object carries
/// at most one executable key (plus an optional `note` annotation);
/// anything else fails closed as `step_kind_known`.
const STEP_KINDS: [&str; 9] = [
    "apply",
    "client_frame",
    "server_frame",
    "serve_delta",
    "serve_snapshot",
    "serve_trigger",
    "decode_rejects",
    "budget",
    "note",
];

/// The structured trigger description's exact key set: the envelope
/// fields plus the collection scope and the two commit identifiers —
/// the content-free rule (PD-2026-08-26-13 D3) as data.
const TRIGGER_KEYS: [&str; 6] =
    ["causation", "collection", "correlation", "kind", "operation_id", "storage_sequence"];

/// Interprets one corpus file into a [`VectorResult`]. Never panics:
/// every disagreement is a recorded failure.
pub(super) fn interpret(file: &str, path: &Path) -> VectorResult {
    let mut runner = VectorRunner::new(file);
    let Ok(text) = fs::read_to_string(path) else {
        runner.fail(None, "corpus_file_parses", "the vector file could not be read");
        return runner.finish();
    };
    let Ok(corpus) = serde_json::from_str::<Value>(&text) else {
        runner.fail(None, "corpus_file_parses", "the vector file is not valid JSON".to_string());
        return runner.finish();
    };
    let category = corpus.get("category").and_then(Value::as_str).map(str::to_string);
    let Some(steps) = corpus.get("steps").and_then(Value::as_array) else {
        runner.fail(None, "corpus_file_parses", "the vector file carries no step list");
        return runner.finish();
    };
    let steps = steps.clone();
    // Header identity: the corpus declares exactly this contract.
    let schema_ok = corpus.get("schema").and_then(Value::as_str) == Some(CORPUS_SCHEMA);
    runner.check(
        None,
        "corpus_schema_identity",
        schema_ok,
        format!("the corpus schema identity must be {CORPUS_SCHEMA}"),
    );
    let protocol_ok = corpus.get("protocol").and_then(Value::as_str) == Some(PROTOCOL)
        && corpus.get("protocol_version").and_then(Value::as_u64) == Some(PROTOCOL_VERSION);
    runner.check(
        None,
        "corpus_protocol_identity",
        protocol_ok,
        format!("the corpus must declare {PROTOCOL} version {PROTOCOL_VERSION}"),
    );
    if runner.aborted() {
        return runner.finish();
    }
    // The server section builds the live delivery core.
    match corpus.get("server") {
        None => {
            runner.fail(None, "corpus_file_parses", "the vector file carries no server section");
        }
        Some(server) => match build_core(server) {
            Ok(core) => runner.core = Some(core),
            Err(reason) => runner.fail(None, "corpus_file_parses", reason),
        },
    }
    runner.category = category;
    for (index, step) in steps.iter().enumerate() {
        if runner.aborted() {
            break;
        }
        runner.step(index, step);
    }
    runner.finish()
}

/// Builds the delivery core a corpus file's server section declares.
fn build_core(server: &Value) -> Result<SyncCore, String> {
    let tenant = server
        .pointer("/scope/tenant")
        .and_then(Value::as_str)
        .ok_or("the server section carries no tenant scope")?;
    let collection = server
        .pointer("/scope/collection")
        .and_then(Value::as_str)
        .ok_or("the server section carries no collection scope")?;
    let budget = server
        .pointer("/config/batch_byte_budget")
        .and_then(Value::as_u64)
        .ok_or("the server section carries no batch byte budget")?;
    let window = server
        .pointer("/config/checkpoint_window")
        .and_then(Value::as_u64)
        .ok_or("the server section carries no checkpoint window")?;
    let mut core = SyncCore::new(
        SyncScope::new(tenant, collection),
        SyncCoreConfig {
            batch_byte_budget: usize::try_from(budget)
                .map_err(|_| "the budget does not fit the platform".to_string())?,
            checkpoint_window: usize::try_from(window)
                .map_err(|_| "the window does not fit the platform".to_string())?,
        },
    )
    .map_err(|e| format!("the core refused to build: {e}"))?;
    if let Some(journal) = server.get("journal_before").and_then(Value::as_array) {
        for commit in journal {
            apply_journal(&mut core, commit)?;
        }
    }
    Ok(core)
}

/// The per-file interpreter state.
struct VectorRunner {
    file: String,
    category: Option<String>,
    core: Option<SyncCore>,
    checks: BTreeMap<&'static str, u64>,
    failures: Vec<Failure>,
    aborted: bool,
}

impl VectorRunner {
    fn new(file: &str) -> Self {
        Self {
            file: file.to_string(),
            category: None,
            core: None,
            checks: BTreeMap::new(),
            failures: Vec::new(),
            aborted: false,
        }
    }

    fn finish(self) -> VectorResult {
        VectorResult::new(
            self.category.unwrap_or_else(|| self.file.trim_end_matches(".json").to_string()),
            self.file,
            self.checks,
            self.failures,
            self.aborted,
        )
    }

    fn aborted(&self) -> bool {
        self.aborted
    }

    /// Records one check. A failed check aborts the rest of the vector:
    /// later steps would only cascade the same root cause.
    fn check(&mut self, step: Option<usize>, name: &'static str, ok: bool, detail: String) -> bool {
        *self.checks.entry(name).or_insert(0) += 1;
        if ok {
            true
        } else {
            self.failures.push(Failure::new(step, name, detail));
            self.aborted = true;
            false
        }
    }

    fn fail(&mut self, step: Option<usize>, name: &'static str, detail: impl Into<String>) {
        self.check(step, name, false, detail.into());
    }

    /// Dispatches one step by its vocabulary: at most one executable
    /// step key, optionally alongside a `note` prose annotation (a
    /// note-only step is a valid prose step that executes nothing).
    fn step(&mut self, index: usize, step: &Value) {
        let Some(object) = step.as_object() else {
            self.fail(Some(index), "step_kind_known", "a step must be a JSON object");
            return;
        };
        let keys: Vec<&str> = object.keys().map(String::as_str).collect();
        let unknown = keys.iter().any(|key| !STEP_KINDS.contains(key));
        let executable: Vec<&str> = keys.iter().copied().filter(|key| *key != "note").collect();
        if unknown || executable.len() > 1 {
            self.fail(
                Some(index),
                "step_kind_known",
                format!(
                    "a step must carry at most one executable key (plus at most a note), found {keys:?}"
                ),
            );
            return;
        }
        let Some(step_kind) = executable.first().copied() else {
            return; // a prose-only step
        };
        let entry = &object[step_kind];
        match step_kind {
            "apply" => self.apply_step(index, entry),
            "client_frame" | "server_frame" => self.recorded_frame_step(index, entry),
            "serve_delta" => self.serve_delta_step(index, entry),
            "serve_snapshot" => self.serve_snapshot_step(index, entry),
            "serve_trigger" => self.serve_trigger_step(index, entry),
            "decode_rejects" => self.decode_rejects_step(index, entry),
            "budget" => self.budget_step(index, entry),
            "note" => {}
            _ => unreachable!("the executable key set was checked against STEP_KINDS"),
        }
    }

    fn apply_step(&mut self, index: usize, entry: &Value) {
        let Some(core) = self.core.as_mut() else {
            self.fail(Some(index), "apply_completes", "no core was built for this vector");
            return;
        };
        match apply_journal(core, entry) {
            Ok(()) => {
                self.check(Some(index), "apply_completes", true, String::new());
            }
            Err(reason) => {
                self.fail(Some(index), "apply_completes", reason);
            }
        }
    }

    /// A recorded frame (`client_frame` / `server_frame`): the structured
    /// description must seal, encode to the recorded bytes, decode back,
    /// verify, and carry the recorded envelope identity.
    fn recorded_frame_step(&mut self, index: usize, entry: &Value) {
        let described = match frame_from_json(entry.get("frame").unwrap_or(&Value::Null)) {
            Ok(frame) => frame,
            Err(reason) => {
                self.fail(Some(index), "frame_description_seals", reason);
                return;
            }
        };
        let wire = match entry.get("wire").and_then(Value::as_str).map(hex_to_bytes) {
            Some(Ok(wire)) => wire,
            Some(Err(reason)) => {
                self.fail(Some(index), "frame_encode_bytes_exact", reason);
                return;
            }
            None => {
                self.fail(
                    Some(index),
                    "frame_encode_bytes_exact",
                    "the step records no wire bytes",
                );
                return;
            }
        };
        let encoded = described.to_bytes();
        self.check(
            Some(index),
            "frame_encode_bytes_exact",
            encoded == wire,
            format!(
                "the description encodes to {} wire bytes, the corpus recorded {}",
                encoded.len(),
                wire.len()
            ),
        );
        if self.aborted() {
            return;
        }
        match SyncV2Frame::from_bytes(&wire) {
            Err(error) => {
                self.fail(
                    Some(index),
                    "frame_decode_roundtrip",
                    format!("the recorded bytes do not decode: {error}"),
                );
            }
            Ok(decoded) => {
                self.check(
                    Some(index),
                    "frame_decode_roundtrip",
                    decoded.kind() == described.kind(),
                    "the decoded kind disagrees with the description".to_string(),
                );
                if self.aborted() {
                    return;
                }
                self.check(
                    Some(index),
                    "frame_digest_verifies",
                    decoded.verify(),
                    "the decoded frame does not verify against its digest".to_string(),
                );
                if self.aborted() {
                    return;
                }
                let identity = decoded.correlation() == described.correlation()
                    && decoded.causation() == described.causation()
                    && decoded.idempotency() == described.idempotency();
                self.check(
                    Some(index),
                    "frame_envelope_identity",
                    identity,
                    "the envelope identity (correlation, causation, idempotency) disagrees"
                        .to_string(),
                );
            }
        }
    }

    fn serve_delta_step(&mut self, index: usize, entry: &Value) {
        let Some(core) = self.core.as_ref() else {
            self.fail(Some(index), "serve_completes", "no core was built for this vector");
            return;
        };
        let from = match checkpoint_from_json(entry.get("from").unwrap_or(&Value::Null)) {
            Ok(from) => from,
            Err(reason) => {
                self.fail(Some(index), "serve_completes", reason);
                return;
            }
        };
        let Some(correlation) = entry.get("correlation").and_then(Value::as_u64) else {
            self.fail(Some(index), "serve_completes", "the step records no correlation id");
            return;
        };
        let causation = entry.get("causation").and_then(Value::as_u64).unwrap_or(0);
        let served =
            match serve_delta(core, &from, FrameId::new(correlation), FrameId::new(causation)) {
                Ok(served) => served,
                Err(error) => {
                    self.fail(Some(index), "serve_completes", format!("serving failed: {error}"));
                    return;
                }
            };
        let expected = entry.get("expect_wire").and_then(Value::as_str);
        match (served, expected) {
            (None, None) => {
                self.check(Some(index), "serve_frame_bytes_exact", true, String::new());
            }
            (None, Some(_)) => {
                self.fail(
                    Some(index),
                    "serve_frame_bytes_exact",
                    "expected a frame, the core served none",
                );
            }
            (Some(frame), None) => {
                self.fail(
                    Some(index),
                    "serve_frame_bytes_exact",
                    format!("expected no frame, the core served a {:?} frame", frame.kind()),
                );
            }
            (Some(frame), Some(hex)) => {
                let wire = match hex_to_bytes(hex) {
                    Ok(wire) => wire,
                    Err(reason) => {
                        self.fail(Some(index), "serve_frame_bytes_exact", reason);
                        return;
                    }
                };
                let bytes = frame.to_bytes();
                self.check(
                    Some(index),
                    "serve_frame_bytes_exact",
                    bytes == wire,
                    format!(
                        "the served frame is {} wire bytes, the corpus recorded {}",
                        bytes.len(),
                        wire.len()
                    ),
                );
                if self.aborted() {
                    return;
                }
                match frame_from_json(entry.get("expect_frame").unwrap_or(&Value::Null)) {
                    Ok(described) => {
                        self.check(
                            Some(index),
                            "serve_frame_matches_description",
                            described.to_bytes() == bytes,
                            "the served frame disagrees with the structured description"
                                .to_string(),
                        );
                    }
                    Err(reason) => {
                        self.fail(Some(index), "serve_frame_matches_description", reason);
                    }
                }
            }
        }
    }

    fn serve_snapshot_step(&mut self, index: usize, entry: &Value) {
        let Some(core) = self.core.as_ref() else {
            self.fail(Some(index), "serve_completes", "no core was built for this vector");
            return;
        };
        let Some(correlation) = entry.get("correlation").and_then(Value::as_u64) else {
            self.fail(Some(index), "serve_completes", "the step records no correlation id");
            return;
        };
        let causation = entry.get("causation").and_then(Value::as_u64).unwrap_or(0);
        let frame = match serve_snapshot(core, FrameId::new(correlation), FrameId::new(causation)) {
            Ok(frame) => frame,
            Err(error) => {
                self.fail(Some(index), "serve_completes", format!("serving failed: {error}"));
                return;
            }
        };
        let Some(hex) = entry.get("expect_wire").and_then(Value::as_str) else {
            self.fail(
                Some(index),
                "serve_frame_bytes_exact",
                "the step records no expected wire bytes",
            );
            return;
        };
        let wire = match hex_to_bytes(hex) {
            Ok(wire) => wire,
            Err(reason) => {
                self.fail(Some(index), "serve_frame_bytes_exact", reason);
                return;
            }
        };
        let bytes = frame.to_bytes();
        self.check(
            Some(index),
            "serve_frame_bytes_exact",
            bytes == wire,
            format!(
                "the snapshot frame is {} wire bytes, the corpus recorded {}",
                bytes.len(),
                wire.len()
            ),
        );
        if self.aborted() {
            return;
        }
        match frame_from_json(entry.get("expect_frame").unwrap_or(&Value::Null)) {
            Ok(described) => {
                self.check(
                    Some(index),
                    "serve_frame_matches_description",
                    described.to_bytes() == bytes,
                    "the served frame disagrees with the structured description".to_string(),
                );
            }
            Err(reason) => {
                self.fail(Some(index), "serve_frame_matches_description", reason);
            }
        }
    }

    fn serve_trigger_step(&mut self, index: usize, entry: &Value) {
        let Some(collection) = entry.get("collection").and_then(Value::as_str) else {
            self.fail(Some(index), "serve_completes", "the step records no collection");
            return;
        };
        let Some(sequence) = entry.get("sequence").and_then(Value::as_u64) else {
            self.fail(Some(index), "serve_completes", "the step records no storage sequence");
            return;
        };
        let Some(operation_id) = entry.get("operation_id").and_then(Value::as_str) else {
            self.fail(Some(index), "serve_completes", "the step records no operation id");
            return;
        };
        let Some(correlation) = entry.get("correlation").and_then(Value::as_u64) else {
            self.fail(Some(index), "serve_completes", "the step records no correlation id");
            return;
        };
        let causation = entry.get("causation").and_then(Value::as_u64).unwrap_or(0);
        let frame = match trigger_frame(
            collection,
            sequence,
            operation_id,
            FrameId::new(correlation),
            FrameId::new(causation),
        ) {
            Ok(frame) => frame,
            Err(error) => {
                self.fail(
                    Some(index),
                    "serve_completes",
                    format!("the trigger does not seal: {error}"),
                );
                return;
            }
        };
        let Some(hex) = entry.get("expect_wire").and_then(Value::as_str) else {
            self.fail(
                Some(index),
                "serve_frame_bytes_exact",
                "the step records no expected wire bytes",
            );
            return;
        };
        let wire = match hex_to_bytes(hex) {
            Ok(wire) => wire,
            Err(reason) => {
                self.fail(Some(index), "serve_frame_bytes_exact", reason);
                return;
            }
        };
        let bytes = frame.to_bytes();
        self.check(
            Some(index),
            "serve_frame_bytes_exact",
            bytes == wire,
            format!(
                "the trigger frame is {} wire bytes, the corpus recorded {}",
                bytes.len(),
                wire.len()
            ),
        );
        if self.aborted() {
            return;
        }
        // The content-free rule as data: the description's key set is
        // exactly envelope + scope + the two commit identifiers.
        let described = entry.get("expect_frame").and_then(Value::as_object);
        let mut keys: Vec<&str> =
            described.map_or(Vec::new(), |object| object.keys().map(String::as_str).collect());
        keys.sort_unstable();
        self.check(
            Some(index),
            "trigger_content_free",
            keys == TRIGGER_KEYS,
            format!("the trigger description must hold exactly {TRIGGER_KEYS:?}, found {keys:?}"),
        );
        if self.aborted() {
            return;
        }
        let budget = FrameClass::Trigger.budget_bytes();
        self.check(
            Some(index),
            "trigger_within_budget",
            bytes.len() <= budget,
            format!(
                "the trigger frame is {} wire bytes, over its {}-byte budget",
                bytes.len(),
                budget
            ),
        );
    }

    fn decode_rejects_step(&mut self, index: usize, entry: &Value) {
        let wire = match entry.get("wire").and_then(Value::as_str).map(hex_to_bytes) {
            Some(Ok(wire)) => wire,
            Some(Err(reason)) => {
                self.fail(Some(index), "decode_rejects_typed_error", reason);
                return;
            }
            None => {
                self.fail(
                    Some(index),
                    "decode_rejects_typed_error",
                    "the step records no wire bytes",
                );
                return;
            }
        };
        let Some(expected) = entry.get("error").and_then(Value::as_str) else {
            self.fail(
                Some(index),
                "decode_rejects_typed_error",
                "the step records no expected rejection",
            );
            return;
        };
        match SyncV2Frame::from_bytes(&wire) {
            Err(error) => {
                let actual = error_name(&error);
                self.check(
                    Some(index),
                    "decode_rejects_typed_error",
                    actual == expected,
                    format!("rejected with {actual}, the corpus records {expected}"),
                );
            }
            Ok(frame) => {
                self.fail(
                    Some(index),
                    "decode_rejects_typed_error",
                    format!(
                        "the bytes decoded as a {:?} frame instead of being rejected",
                        frame.kind()
                    ),
                );
            }
        }
    }

    fn budget_step(&mut self, index: usize, entry: &Value) {
        let wire = match entry.get("wire").and_then(Value::as_str).map(hex_to_bytes) {
            Some(Ok(wire)) => wire,
            Some(Err(reason)) => {
                self.fail(Some(index), "budget_frame_decodes", reason);
                return;
            }
            None => {
                self.fail(Some(index), "budget_frame_decodes", "the step records no wire bytes");
                return;
            }
        };
        let decoded = match SyncV2Frame::from_bytes(&wire) {
            Ok(frame) => frame,
            Err(error) => {
                self.fail(
                    Some(index),
                    "budget_frame_decodes",
                    format!("the frame does not decode: {error}"),
                );
                return;
            }
        };
        let Some(class_name) = entry.get("class").and_then(Value::as_str) else {
            self.fail(Some(index), "budget_class_matches", "the step records no frame class");
            return;
        };
        let class = match class_from_name(class_name) {
            Ok(class) => class,
            Err(reason) => {
                self.fail(Some(index), "budget_class_matches", reason);
                return;
            }
        };
        self.check(
            Some(index),
            "budget_class_matches",
            decoded.body().frame_class() == class,
            format!(
                "the recorded class {class_name} disagrees with the frame's class {}",
                decoded.body().frame_class().name()
            ),
        );
        if self.aborted() {
            return;
        }
        let oversize_exception = matches!(
            decoded.body(),
            SyncV2Body::DeltaBatch { single_commit_oversize: true, commits: 1, .. }
        );
        let budget = class.budget_bytes();
        self.check(
            Some(index),
            "budget_within_budget",
            wire.len() <= budget || oversize_exception,
            format!(
                "{} wire bytes exceed the {} budget {budget} without the single-commit oversize exception",
                wire.len(),
                class.name()
            ),
        );
        if self.aborted() {
            return;
        }
        self.budget_accounting(index, entry, &decoded, &wire);
    }

    /// The recorded budget accounting a `budget` step carries: payload
    /// bytes, envelope overhead, bytes per delivered change, and the
    /// delta-below-snapshot invariant (PD-2026-08-26-13 D1).
    fn budget_accounting(
        &mut self,
        index: usize,
        entry: &Value,
        decoded: &SyncV2Frame,
        wire: &[u8],
    ) {
        if let Some(recorded_payload) = entry.get("payload_bytes").and_then(Value::as_u64) {
            let actual = decoded.payload_len() as u64;
            let overhead = wire.len() - decoded.payload_len();
            let recorded_overhead = entry.get("envelope_overhead_bytes").and_then(Value::as_u64);
            self.check(
                Some(index),
                "budget_payload_accounting",
                actual == recorded_payload && recorded_overhead == Some(overhead as u64),
                format!(
                    "payload or envelope-overhead accounting disagrees (recounted {actual} payload bytes and {overhead} overhead bytes)"
                ),
            );
            if self.aborted() {
                return;
            }
        }
        if let Some(recorded_per_change) =
            entry.get("bytes_per_delivered_change").and_then(Value::as_u64)
        {
            match decoded.body() {
                SyncV2Body::DeltaBatch { commits, .. } if *commits > 0 => {
                    let recounted = (wire.len() as u64) / commits;
                    self.check(
                        Some(index),
                        "budget_bytes_per_delivered_change",
                        recounted == recorded_per_change,
                        format!(
                            "recounted {recounted} wire bytes per delivered change over {commits} commits, the corpus records {recorded_per_change}"
                        ),
                    );
                }
                body => {
                    self.fail(
                        Some(index),
                        "budget_bytes_per_delivered_change",
                        format!(
                            "bytes-per-change applies to delta batches, this frame is {:?}",
                            body.kind()
                        ),
                    );
                }
            }
            if self.aborted() {
                return;
            }
        }
        if let (Some(delta), Some(snapshot)) = (
            entry.get("delta_bytes").and_then(Value::as_u64),
            entry.get("snapshot_bytes").and_then(Value::as_u64),
        ) {
            self.check(
                Some(index),
                "budget_delta_below_snapshot",
                delta < snapshot,
                format!("the delta path ({delta} bytes) must stay below the snapshot re-send ({snapshot} bytes)"),
            );
        }
    }
}

// --- Ports of the SE-04 corpus reader, panic-free --------------------------

/// Applies one journal commit description to the core.
fn apply_journal(core: &mut SyncCore, commit: &Value) -> Result<(), String> {
    let sequence = commit
        .get("sequence")
        .and_then(Value::as_u64)
        .ok_or("the journal entry carries no sequence")?;
    let operation_id = commit
        .get("operation_id")
        .and_then(Value::as_str)
        .ok_or("the journal entry carries no operation id")?
        .to_string();
    let document = commit
        .get("document")
        .and_then(Value::as_str)
        .ok_or("the journal entry carries no document")?
        .to_string();
    let change = if let Some(fields) = commit.get("put").and_then(Value::as_object) {
        let mut body = std::collections::BTreeMap::new();
        for (key, value) in fields {
            body.insert(key.clone(), json_to_sync_value(value)?);
        }
        JournalChange::Put { body }
    } else if commit.get("delete").and_then(Value::as_bool).unwrap_or(false) {
        JournalChange::Delete
    } else {
        return Err(format!("journal entry {sequence} carries no put or delete"));
    };
    core.apply(&JournalCommit {
        storage_sequence: sequence,
        operation_id,
        document_id: document,
        change,
    })
    .map_err(|e| format!("the journal entry {sequence} was refused: {e}"))
    .map(|_| ())
}

fn json_to_sync_value(value: &Value) -> Result<SyncValue, String> {
    match value {
        Value::Null => Ok(SyncValue::Null),
        Value::Bool(b) => Ok(SyncValue::Bool(*b)),
        Value::Number(n) => n
            .as_i64()
            .map(SyncValue::Int)
            .or_else(|| n.as_f64().map(SyncValue::Float))
            .ok_or_else(|| format!("unsupported vector value {n}")),
        Value::String(s) => Ok(SyncValue::Str(s.clone())),
        other => Err(format!("unsupported vector value {other}")),
    }
}

/// Builds a checkpoint from its structured description; `null` is the
/// initial checkpoint.
fn checkpoint_from_json(value: &Value) -> Result<SyncCheckpoint, String> {
    if value.is_null() {
        return Ok(SyncCheckpoint::initial());
    }
    let ends = value
        .get("ends")
        .and_then(Value::as_array)
        .ok_or("the checkpoint carries no ends")?
        .iter()
        .map(|pair| {
            let pair = pair.as_array().ok_or("a checkpoint end is not a pair")?;
            let peer = pair.first().and_then(Value::as_u64).ok_or("the pair carries no peer")?;
            let end =
                pair.get(1).and_then(Value::as_u64).ok_or("the pair carries no end counter")?;
            Ok((peer, end))
        })
        .collect::<Result<Vec<(u64, u64)>, String>>()?;
    let storage_sequence = value
        .get("storage_sequence")
        .and_then(Value::as_u64)
        .ok_or("the checkpoint carries no storage sequence")?;
    Ok(SyncCheckpoint::from_ends(ends, storage_sequence))
}

/// Builds a frame from its structured description — the same shape every
/// SDK's harness reads.
fn frame_from_json(value: &Value) -> Result<SyncV2Frame, String> {
    let kind = value.get("kind").and_then(Value::as_str).ok_or("the frame carries no kind")?;
    let correlation = value
        .get("correlation")
        .and_then(Value::as_u64)
        .ok_or("the frame carries no correlation id")?;
    let causation = value.get("causation").and_then(Value::as_u64).unwrap_or(0);
    let idempotency = value
        .get("idempotency")
        .and_then(Value::as_str)
        .map(IdempotencyKey::new)
        .transpose()
        .map_err(|e| format!("the idempotency key is not constructible: {e}"))?;
    let body = match kind {
        "session_setup" => SyncV2Body::SessionSetup {
            collections: strings_from(value.get("collections"))?,
            resume: value
                .get("resume")
                .filter(|resume| !resume.is_null())
                .map(checkpoint_from_json)
                .transpose()?,
        },
        "session_accepted" => SyncV2Body::SessionAccepted {
            session: value
                .get("session")
                .and_then(Value::as_u64)
                .ok_or("the frame carries no session id")?,
            server: checkpoint_from_json(value.get("server_checkpoint").unwrap_or(&Value::Null))?,
            collections: strings_from(value.get("collections"))?,
        },
        "subscription_modify" => SyncV2Body::SubscriptionModify {
            add: strings_from(value.get("add"))?,
            remove: strings_from(value.get("remove"))?,
        },
        "subscription_changed" => {
            SyncV2Body::SubscriptionChanged { active: strings_from(value.get("active"))? }
        }
        "checkpoint_ack" => SyncV2Body::CheckpointAck {
            checkpoint: checkpoint_from_json(value.get("checkpoint").unwrap_or(&Value::Null))?,
        },
        "heartbeat" => SyncV2Body::Heartbeat,
        "delta_batch" => delivery_body_from_json(value, true)?,
        "snapshot_answer" => delivery_body_from_json(value, false)?,
        "trigger" => SyncV2Body::Trigger {
            collection: value
                .get("collection")
                .and_then(Value::as_str)
                .ok_or("the trigger carries no collection")?
                .to_string(),
            storage_sequence: value
                .get("storage_sequence")
                .and_then(Value::as_u64)
                .ok_or("the trigger carries no storage sequence")?,
            operation_id: value
                .get("operation_id")
                .and_then(Value::as_str)
                .ok_or("the trigger carries no operation id")?
                .to_string(),
        },
        "resync_required" => SyncV2Body::ResyncRequired {
            floor: checkpoint_from_json(value.get("floor").unwrap_or(&Value::Null))?,
            reason: match value.get("reason").and_then(Value::as_str) {
                Some("behind-retained-floor") => ResyncReason::BehindRetainedFloor,
                Some("history-compacted") => ResyncReason::HistoryCompacted,
                _ => return Err("the frame carries an unknown resync reason".to_string()),
            },
        },
        "region_route_change" => SyncV2Body::RegionRouteChange {
            region: value
                .get("region")
                .and_then(Value::as_str)
                .ok_or("the frame carries no region")?
                .to_string(),
        },
        "error" => SyncV2Body::Error {
            code: error_code_from(value.get("code"))?,
            detail: value.get("detail").and_then(Value::as_str).map(str::to_string),
        },
        "mutation_request" => mutation_request_from_json(value)?,
        "mutation_ack" => mutation_ack_from_json(value)?,
        other => return Err(format!("the frame carries an unknown kind {other}")),
    };
    SyncV2Frame::seal(body, FrameId::new(correlation), FrameId::new(causation), idempotency)
        .map_err(|e| format!("the described frame does not seal: {e}"))
}

/// Builds a `delta_batch` (delta) or `snapshot_answer` (snapshot) body
/// from its structured description.
fn delivery_body_from_json(value: &Value, delta: bool) -> Result<SyncV2Body, String> {
    let codec = codec_from(value.get("codec"))?;
    let payload = hex_to_bytes(
        value.get("payload").and_then(Value::as_str).ok_or("the frame carries no payload")?,
    )?;
    if delta {
        Ok(SyncV2Body::DeltaBatch {
            codec,
            payload,
            next: checkpoint_from_json(value.get("next").unwrap_or(&Value::Null))?,
            within_budget: value
                .get("within_budget")
                .and_then(Value::as_bool)
                .ok_or("the frame carries no budget flag")?,
            single_commit_oversize: value
                .get("single_commit_oversize")
                .and_then(Value::as_bool)
                .ok_or("the frame carries no oversize flag")?,
            commits: value
                .get("commits")
                .and_then(Value::as_u64)
                .ok_or("the frame carries no commit count")?,
        })
    } else {
        Ok(SyncV2Body::SnapshotAnswer {
            codec,
            payload,
            checkpoint: checkpoint_from_json(value.get("checkpoint").unwrap_or(&Value::Null))?,
        })
    }
}

/// Builds a `mutation_request` body from its structured description.
fn mutation_request_from_json(value: &Value) -> Result<SyncV2Body, String> {
    Ok(SyncV2Body::MutationRequest {
        collection: value
            .get("collection")
            .and_then(Value::as_str)
            .ok_or("the mutation carries no collection")?
            .to_string(),
        document: value
            .get("document")
            .and_then(Value::as_str)
            .ok_or("the mutation carries no document")?
            .to_string(),
        write: if let Some(fields) = value.get("put").and_then(Value::as_object) {
            let mut body = std::collections::BTreeMap::new();
            for (key, field) in fields {
                body.insert(key.clone(), json_to_sync_value(field)?);
            }
            DocumentWrite::Put { body }
        } else if value.get("delete").and_then(Value::as_bool).unwrap_or(false) {
            DocumentWrite::Delete
        } else {
            return Err("the mutation carries no put or delete".to_string());
        },
    })
}

/// Builds a `mutation_ack` body from its structured description.
fn mutation_ack_from_json(value: &Value) -> Result<SyncV2Body, String> {
    Ok(SyncV2Body::MutationAck {
        outcome: match value.get("outcome").and_then(Value::as_str) {
            Some("applied") => MutationOutcome::Applied {
                storage_sequence: value
                    .get("storage_sequence")
                    .and_then(Value::as_u64)
                    .ok_or("the acknowledgement carries no sequence")?,
                operation_id: value
                    .get("operation_id")
                    .and_then(Value::as_str)
                    .ok_or("the acknowledgement carries no operation id")?
                    .to_string(),
            },
            Some("rejected") => {
                MutationOutcome::Rejected { reason: error_code_from(value.get("rejection"))? }
            }
            _ => return Err("the acknowledgement carries an unknown outcome".to_string()),
        },
    })
}

fn strings_from(value: Option<&Value>) -> Result<Vec<String>, String> {
    value
        .and_then(Value::as_array)
        .ok_or("the frame carries no string list")?
        .iter()
        .map(|entry| {
            entry
                .as_str()
                .map(str::to_string)
                .ok_or_else(|| "a string list entry is not a string".to_string())
        })
        .collect()
}

fn codec_from(value: Option<&Value>) -> Result<PayloadCodec, String> {
    match value.and_then(Value::as_str) {
        Some("terrabase-native/1") => Ok(PayloadCodec::NativeDelta1),
        Some("terrabase-snapshot/1") => Ok(PayloadCodec::NativeSnapshot1),
        _ => Err("the frame carries an unknown payload codec".to_string()),
    }
}

fn error_code_from(value: Option<&Value>) -> Result<ErrorCode, String> {
    match value.and_then(Value::as_str) {
        Some("unsupported_version") => Ok(ErrorCode::UnsupportedVersion),
        Some("malformed_frame") => Ok(ErrorCode::MalformedFrame),
        Some("checkpoint_refused") => Ok(ErrorCode::CheckpointRefused),
        Some("scope_refused") => Ok(ErrorCode::ScopeRefused),
        Some("session_closed") => Ok(ErrorCode::SessionClosed),
        Some("overloaded") => Ok(ErrorCode::Overloaded),
        _ => Err("the frame carries an unknown error code".to_string()),
    }
}

fn class_from_name(name: &str) -> Result<FrameClass, String> {
    match name {
        "session" => Ok(FrameClass::Session),
        "subscription" => Ok(FrameClass::Subscription),
        "acknowledgement" => Ok(FrameClass::Acknowledgement),
        "heartbeat" => Ok(FrameClass::Heartbeat),
        "delivery" => Ok(FrameClass::Delivery),
        "trigger" => Ok(FrameClass::Trigger),
        "control" => Ok(FrameClass::Control),
        "mutation" => Ok(FrameClass::Mutation),
        other => Err(format!("unknown frame class {other}")),
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

fn hex_to_bytes(hex: &str) -> Result<Vec<u8>, String> {
    if !hex.len().is_multiple_of(2) {
        return Err("the recorded wire is not valid hex (odd length)".to_string());
    }
    let mut bytes = Vec::with_capacity(hex.len() / 2);
    let chars: Vec<char> = hex.chars().collect();
    for pair in chars.chunks(2) {
        let text: String = pair.iter().collect();
        let byte = u8::from_str_radix(&text, 16)
            .map_err(|_| format!("the recorded wire is not valid hex at '{text}'"))?;
        bytes.push(byte);
    }
    Ok(bytes)
}

#[cfg(test)]
mod tests {
    use super::hex_to_bytes;

    #[test]
    fn hex_round_trips_and_rejects_junk() {
        assert_eq!(hex_to_bytes("00ff10").as_deref(), Ok(&[0x00, 0xff, 0x10][..]));
        assert!(hex_to_bytes("0").is_err());
        assert!(hex_to_bytes("zz").is_err());
    }
}
