//! The spike's proof scenarios and their recorded outcomes.
//!
//! Every function here returns a typed outcome record (no printing, no
//! environment access beyond [`crate::measure::resident_bytes`]) so the
//! bin and the tests observe exactly the same numbers. The records are
//! bytes-on-wire observations under PD-2026-08-26-13 D1 — the seed of
//! SE-07's budgets — and carry no performance claim.

use crate::consumer::ConsumerSession;
use crate::journal::JournalChange;
use crate::journal::JournalEvent;
use crate::journal::JournalGenerator;
use crate::journal::full_body_bytes;
use crate::mapping::Checkpoint;
use crate::mapping::DeltaOutcome;
use crate::mapping::MappingError;
use crate::mapping::ServerDelivery;
use crate::mapping::documents_of;
use crate::measure::resident_bytes;

/// The byte budget one delta batch may carry in the spike scenarios
/// (64 KiB: the working hypothesis SE-07 will replace with a measured,
/// written budget).
pub const BATCH_BUDGET_BYTES: usize = 64 * 1024;

/// The retained-checkpoint window of the spike servers (commits).
pub const CHECKPOINT_WINDOW: usize = 4096;

/// One representative change pattern's bytes-on-wire comparison.
#[derive(Clone, Debug)]
pub struct ChangePattern {
    /// The pattern's name.
    pub name: &'static str,
    /// The changed document's field count before the change.
    pub document_fields: usize,
    /// Bytes a full-document re-send of the changed body would carry.
    pub full_document_bytes: usize,
    /// Bytes the Loro delta for the same change carries.
    pub delta_bytes: usize,
    /// The delta batch's commit count.
    pub commits: u64,
}

impl ChangePattern {
    /// The delta-to-full ratio (`delta_bytes / full_document_bytes`),
    /// rounded to three decimals; `None` when the full-document baseline
    /// is zero.
    #[must_use]
    #[allow(clippy::cast_precision_loss)] // byte counts stay far below 2^52
    pub fn delta_over_full(&self) -> Option<f64> {
        if self.full_document_bytes == 0 {
            None
        } else {
            Some(
                (self.delta_bytes as f64 / self.full_document_bytes as f64 * 1000.0).round()
                    / 1000.0,
            )
        }
    }
}

/// The outcome of the delta-delivery scenario.
#[derive(Clone, Debug)]
pub struct DeltaDeliveryOutcome {
    /// The documents in the scenario scope.
    pub documents: usize,
    /// The fields per document.
    pub fields_per_document: usize,
    /// The full-snapshot bytes for initial sync.
    pub initial_snapshot_bytes: usize,
    /// The sum of full-body bytes of every document (the naive initial
    /// sync baseline).
    pub initial_full_bodies_bytes: usize,
    /// The per-pattern measurements.
    pub patterns: Vec<ChangePattern>,
}

/// Runs the delta-delivery scenario: materializes a document set, then
/// measures one delta per representative change pattern against the
/// full-document re-send of the same change.
///
/// # Panics
///
/// Panics when a single-change delta exceeds the scenario budget (a spike
/// bug, not a workload property).
///
/// # Errors
///
/// Returns [`MappingError`] when a materialization or export step fails.
pub fn delta_delivery(seed: u64) -> Result<DeltaDeliveryOutcome, MappingError> {
    const DOCUMENTS: usize = 64;
    const FIELDS: usize = 24;

    let mut journal = JournalGenerator::new(seed);
    let mut server = ServerDelivery::new(CHECKPOINT_WINDOW)?;
    for index in 0..DOCUMENTS {
        let event = journal.next_put(&format!("doc-{index:04}"), FIELDS);
        server.apply(&event)?;
    }

    let mut consumer = ConsumerSession::new()?;
    consumer.import_snapshot(&server.snapshot_bytes()?)?;
    if documents_of(&consumer.deep_value()) != documents_of(&server.deep_value()) {
        return Err(MappingError::Loro("snapshot sync did not converge the consumer".to_string()));
    }
    let initial_snapshot_bytes = server.snapshot_bytes()?.len();
    let initial_full_bodies_bytes =
        documents_of(&server.deep_value()).values().map(String::len).sum();

    // Pattern A: single-field patch of one 24-field document. The
    // full-document baseline is the changed document's whole body as the
    // consumer holds it (what a full-document re-send would carry).
    let from = consumer.checkpoint().clone();
    let full_document_bytes =
        documents_of(&consumer.deep_value()).get("doc-0007").map_or(0, String::len);
    let patch = journal.next_patch("doc-0007", FIELDS, 11);
    server.apply(&patch)?;
    let patch_pattern =
        measure_one_delta(&server, &from, "single-field patch", FIELDS, full_document_bytes)?;
    sync_to(&mut server, &mut consumer)?;

    // Pattern B: whole-document replace (a full new 24-field body is both
    // the change and the baseline).
    let from = consumer.checkpoint().clone();
    let replace = journal.next_put("doc-0007", FIELDS);
    let full_document_bytes = match &replace.change {
        JournalChange::Put { body } => full_body_bytes(body),
        JournalChange::Delete => 0,
    };
    server.apply(&replace)?;
    let replace_pattern =
        measure_one_delta(&server, &from, "whole-document replace", FIELDS, full_document_bytes)?;
    sync_to(&mut server, &mut consumer)?;

    // Pattern C: delete (the tombstone) — the baseline is the body the
    // consumer holds for the deleted document.
    let from = consumer.checkpoint().clone();
    let deleted_body_bytes =
        documents_of(&consumer.deep_value()).get("doc-0042").map_or(0, String::len);
    let delete = journal.next_delete("doc-0042");
    server.apply(&delete)?;
    let delete_pattern =
        measure_one_delta(&server, &from, "delete (tombstone)", FIELDS, deleted_body_bytes)?;

    Ok(DeltaDeliveryOutcome {
        documents: DOCUMENTS,
        fields_per_document: FIELDS,
        initial_snapshot_bytes,
        initial_full_bodies_bytes,
        patterns: vec![patch_pattern, replace_pattern, delete_pattern],
    })
}

/// The outcome of the checkpoint-resume scenario.
#[derive(Clone, Debug)]
#[allow(clippy::struct_excessive_bools)] // each field is one recorded scenario fact
pub struct ResumeOutcome {
    /// Commits before the disconnect.
    pub commits_before_disconnect: usize,
    /// Commits applied while the consumer was disconnected.
    pub commits_while_disconnected: usize,
    /// Delta batches the reconnecting consumer needed.
    pub reconnect_batches: usize,
    /// Total delta bytes across those batches.
    pub reconnect_delta_bytes: usize,
    /// Whether the consumer converged to the server state after resume.
    pub converged_after_resume: bool,
    /// Whether a duplicate re-delivery of the last batch stayed converged
    /// (at-least-once safety).
    pub converged_after_duplicate: bool,
    /// Whether the behind-the-floor consumer was answered with the
    /// retained-floor outcome.
    pub floor_answered: bool,
    /// Whether the behind-the-floor consumer converged after snapshot
    /// resync.
    pub converged_after_resync: bool,
    /// Whether deltas serve again after the resync.
    pub deltas_serve_after_resync: bool,
}

/// Runs the checkpoint-resume scenario: a consumer disconnects, the server
/// commits on, the consumer resumes from its recorded checkpoint; then a
/// second consumer falls behind the retained floor and must resync from a
/// snapshot.
///
/// # Panics
///
/// Panics when a reconnect batch exceeds the scenario budget (a spike
/// bug, not a workload property).
///
/// # Errors
///
/// Returns [`MappingError`] when a materialization or export step fails.
pub fn checkpoint_resume(seed: u64) -> Result<ResumeOutcome, MappingError> {
    const DOCS: usize = 48;
    const FIELDS: usize = 16;

    let mut journal = JournalGenerator::new(seed);
    let mut server = ServerDelivery::new(CHECKPOINT_WINDOW)?;
    let mut events: Vec<JournalEvent> = Vec::new();
    for index in 0..DOCS {
        events.push(journal.next_put(&format!("doc-{index:03}"), FIELDS));
    }
    for index in 0..DOCS {
        events.push(journal.next_patch(&format!("doc-{index:03}"), FIELDS, index % FIELDS));
    }
    for event in &events {
        server.apply(event)?;
    }

    let mut consumer = ConsumerSession::new()?;
    consumer.import_snapshot(&server.snapshot_bytes()?)?;
    sync_to(&mut server, &mut consumer)?;

    // Disconnect: the server commits on without the consumer.
    let mut missed: Vec<JournalEvent> = Vec::new();
    for index in 0..DOCS {
        missed.push(journal.next_patch(&format!("doc-{index:03}"), FIELDS, (index + 3) % FIELDS));
    }
    missed.push(journal.next_delete("doc-010"));
    for event in &missed {
        server.apply(event)?;
    }

    // Reconnect from the recorded checkpoint.
    let mut batches = 0usize;
    let mut bytes_total = 0usize;
    let mut last_bytes = Vec::new();
    let mut last_checkpoint = consumer.checkpoint().clone();
    loop {
        match server.delta_since(consumer.checkpoint(), BATCH_BUDGET_BYTES)? {
            DeltaOutcome::UpToDate => break,
            DeltaOutcome::BehindRetainedFloor { .. } => {
                return Err(MappingError::Loro("floor hit inside the retained window".to_string()));
            }
            DeltaOutcome::Delta { bytes, next, commits, within_budget, .. } => {
                assert!(
                    within_budget,
                    "reconnect batch exceeded the budget with {commits} commits"
                );
                batches += 1;
                bytes_total += bytes.len();
                consumer.apply_delta(&bytes, &next)?;
                last_bytes = bytes;
                last_checkpoint = next;
            }
        }
    }
    let converged_after_resume =
        documents_of(&consumer.deep_value()) == documents_of(&server.deep_value());

    // At-least-once: a duplicate re-delivery of the last batch.
    consumer.apply_delta(&last_bytes, &last_checkpoint)?;
    let converged_after_duplicate =
        documents_of(&consumer.deep_value()) == documents_of(&server.deep_value());

    // A second consumer falls behind the retained floor: the server
    // overflows the checkpoint window, then keeps committing.
    let mut lagging = ConsumerSession::new()?;
    lagging.import_snapshot(&server.snapshot_bytes()?)?;
    let stall_checkpoint = lagging.checkpoint().clone();
    for index in 0..(CHECKPOINT_WINDOW + 64) {
        let document_index = index % DOCS;
        let event = journal.next_patch(&format!("doc-{document_index:03}"), FIELDS, index % FIELDS);
        server.apply(&event)?;
    }
    let floor_answered = matches!(
        server.delta_since(&stall_checkpoint, BATCH_BUDGET_BYTES)?,
        DeltaOutcome::BehindRetainedFloor { .. }
    );
    lagging.import_snapshot(&server.snapshot_bytes()?)?;
    let converged_after_resync =
        documents_of(&lagging.deep_value()) == documents_of(&server.deep_value());
    let deltas_serve_after_resync = matches!(
        server.delta_since(lagging.checkpoint(), BATCH_BUDGET_BYTES)?,
        DeltaOutcome::UpToDate
    );

    Ok(ResumeOutcome {
        commits_before_disconnect: events.len(),
        commits_while_disconnected: missed.len(),
        reconnect_batches: batches,
        reconnect_delta_bytes: bytes_total,
        converged_after_resume,
        converged_after_duplicate,
        floor_answered,
        converged_after_resync,
        deltas_serve_after_resync,
    })
}

/// The outcome of the bounded-memory burst scenario.
#[derive(Clone, Debug)]
pub struct BurstOutcome {
    /// Commits materialized in the burst.
    pub commits: usize,
    /// Documents touched by the burst.
    pub documents: usize,
    /// The byte budget each batch was held to.
    pub budget_bytes: usize,
    /// Batches the draining consumer needed.
    pub batches: usize,
    /// Total delta bytes delivered.
    pub delta_bytes_total: usize,
    /// The largest batch actually delivered.
    pub max_batch_bytes: usize,
    /// Whether every delivered batch was within the budget (or a flagged
    /// single oversized commit).
    pub all_batches_bounded: bool,
    /// Loro operations retained after the burst.
    pub ops_retained: usize,
    /// Loro changes retained after the burst.
    pub changes_retained: usize,
    /// Resident bytes before the burst (unrun: `None` off Linux).
    pub resident_before: Option<u64>,
    /// Resident bytes after materializing the burst.
    pub resident_after_materialize: Option<u64>,
    /// Resident bytes after the consumer drained every batch.
    pub resident_after_drain: Option<u64>,
    /// Resident bytes after freeing Loro caches and compacting the change
    /// store.
    pub resident_after_free: Option<u64>,
}

/// Runs the bounded-memory burst scenario: the server materializes a
/// 50_000-commit burst in 500-commit chunks while an attached consumer
/// drains each chunk through byte-budgeted delta batches. Every delivered
/// batch must fit the budget (or be exactly one flagged oversized single
/// commit); batch assembly never touches the whole backlog, and the
/// resident-memory observations are recorded at each phase boundary.
///
/// # Errors
///
/// Returns [`MappingError`] when a materialization or export step fails.
pub fn bounded_memory_burst(seed: u64) -> Result<BurstOutcome, MappingError> {
    const COMMITS: usize = 50_000;
    const DOCS: usize = 512;
    const FIELDS: usize = 24;

    const CHUNK: usize = 500;
    let resident_before = resident_bytes();
    let mut journal = JournalGenerator::new(seed);
    let mut server = ServerDelivery::new(CHECKPOINT_WINDOW)?;
    // The consumer holds an initial snapshot, then stays attached: the
    // burst lands in chunks, and between chunks the consumer drains
    // whatever accumulated through byte-budgeted batches. The backlog at
    // any drain point is a whole chunk (500 commits), so batch assembly
    // faces a real burst-shaped input, never a single commit.
    for index in 0..DOCS {
        let event = journal.next_put(&format!("doc-{index:03}"), FIELDS);
        server.apply(&event)?;
    }
    let mut consumer = ConsumerSession::new()?;
    consumer.import_snapshot(&server.snapshot_bytes()?)?;

    let mut batches = 0usize;
    let mut delta_bytes_total = 0usize;
    let mut max_batch_bytes = 0usize;
    let mut all_batches_bounded = true;
    let mut last_bytes: Option<Vec<u8>> = None;
    let mut last_next = consumer.checkpoint().clone();
    for chunk in 0..(COMMITS / CHUNK) {
        for index in (chunk * CHUNK)..((chunk + 1) * CHUNK) {
            let document_index = index % DOCS;
            let document = format!("doc-{document_index:03}");
            let event = journal.next_patch(&document, FIELDS, index % FIELDS);
            server.apply(&event)?;
        }
        loop {
            match server.delta_since(consumer.checkpoint(), BATCH_BUDGET_BYTES)? {
                DeltaOutcome::UpToDate => break,
                DeltaOutcome::BehindRetainedFloor { floor } => {
                    return Err(MappingError::Loro(format!(
                        "floor {} hit while draining an attached consumer",
                        floor.storage_sequence
                    )));
                }
                DeltaOutcome::Delta {
                    bytes,
                    next,
                    commits,
                    within_budget,
                    single_commit_oversize,
                } => {
                    if !within_budget && (commits > 1 || !single_commit_oversize) {
                        all_batches_bounded = false;
                    }
                    batches += 1;
                    delta_bytes_total += bytes.len();
                    max_batch_bytes = max_batch_bytes.max(bytes.len());
                    consumer.apply_delta(&bytes, &next)?;
                    last_bytes = Some(bytes);
                    last_next = next;
                }
            }
        }
    }
    let resident_after_materialize = resident_bytes();

    // A duplicate of the final batch must stay converged (at-least-once).
    if let Some(bytes) = &last_bytes {
        consumer.apply_delta(bytes, &last_next)?;
    }
    if documents_of(&consumer.deep_value()) != documents_of(&server.deep_value()) {
        return Err(MappingError::Loro("burst drain did not converge the consumer".to_string()));
    }
    let resident_after_drain = resident_bytes();

    let ops_retained = server.doc().len_ops();
    let changes_retained = server.doc().len_changes();
    server.doc().free_history_cache();
    server.doc().free_diff_calculator();
    server.doc().compact_change_store();
    let resident_after_free = resident_bytes();

    Ok(BurstOutcome {
        commits: COMMITS,
        documents: DOCS,
        budget_bytes: BATCH_BUDGET_BYTES,
        batches,
        delta_bytes_total,
        max_batch_bytes,
        all_batches_bounded,
        ops_retained,
        changes_retained,
        resident_before,
        resident_after_materialize,
        resident_after_drain,
        resident_after_free,
    })
}

/// The outcome of the deterministic-replay scenario.
#[derive(Clone, Debug)]
#[allow(clippy::struct_excessive_bools)] // each field is one recorded scenario fact
pub struct DeterminismOutcome {
    /// Whether two servers fed the same journal produced identical
    /// snapshot bytes.
    pub snapshots_identical: bool,
    /// Whether two servers fed the same journal produced identical deep
    /// values.
    pub deep_values_identical: bool,
    /// Whether the batch-wise consumer and the snapshot consumer observed
    /// identical document sets.
    pub consumers_observe_identically: bool,
    /// Whether the drained consumer's own snapshot is byte-identical to
    /// the server's.
    pub consumer_snapshot_matches_server: bool,
    /// Whether replaying the journal a third time, with deltas pulled
    /// after every commit, still ends identical.
    pub incremental_pull_identical: bool,
}

/// Runs the deterministic-replay scenario: the same journal materialized
/// twice must produce identical snapshots and identical state; consumers
/// that arrived by deltas, by one snapshot, and commit-by-commit must
/// observe exactly the same documents.
///
/// # Errors
///
/// Returns [`MappingError`] when a materialization or export step fails.
pub fn deterministic_replay(seed: u64) -> Result<DeterminismOutcome, MappingError> {
    const DOCS: usize = 32;
    const FIELDS: usize = 12;

    let mut journal = JournalGenerator::new(seed);
    let mut events: Vec<JournalEvent> = Vec::new();
    for index in 0..DOCS {
        events.push(journal.next_put(&format!("doc-{index:03}"), FIELDS));
    }
    for index in 0..DOCS {
        events.push(journal.next_patch(&format!("doc-{index:03}"), FIELDS, index % FIELDS));
    }
    events.push(journal.next_delete("doc-005"));

    let mut a = ServerDelivery::new(CHECKPOINT_WINDOW)?;
    let mut b = ServerDelivery::new(CHECKPOINT_WINDOW)?;
    for event in &events {
        a.apply(event)?;
        b.apply(event)?;
    }
    let snapshot_a = a.snapshot_bytes()?;
    let snapshot_b = b.snapshot_bytes()?;
    let snapshots_identical = snapshot_a == snapshot_b;
    let deep_values_identical = documents_of(&a.deep_value()) == documents_of(&b.deep_value());

    // A delta-only consumer: initial state via all-updates delta, then the
    // rest in small budgeted batches.
    let mut delta_consumer = ConsumerSession::new()?;
    let mut incremental = ServerDelivery::new(CHECKPOINT_WINDOW)?;
    let mut snapshot_consumer = ConsumerSession::new()?;
    snapshot_consumer.import_snapshot(&snapshot_a)?;
    for event in &events {
        incremental.apply(event)?;
        loop {
            match incremental.delta_since(delta_consumer.checkpoint(), 2048)? {
                DeltaOutcome::UpToDate => break,
                DeltaOutcome::BehindRetainedFloor { .. } => {
                    return Err(MappingError::Loro(
                        "floor hit while pulling after every commit".to_string(),
                    ));
                }
                DeltaOutcome::Delta { bytes, next, .. } => {
                    delta_consumer.apply_delta(&bytes, &next)?;
                }
            }
        }
    }
    let consumers_observe_identically = documents_of(&delta_consumer.deep_value())
        == documents_of(&snapshot_consumer.deep_value())
        && documents_of(&delta_consumer.deep_value()) == documents_of(&a.deep_value());
    let consumer_snapshot_matches_server = snapshot_a == delta_consumer.doc_snapshot()?;
    let incremental_pull_identical =
        documents_of(&incremental.deep_value()) == documents_of(&a.deep_value());

    Ok(DeterminismOutcome {
        snapshots_identical,
        deep_values_identical,
        consumers_observe_identically,
        consumer_snapshot_matches_server,
        incremental_pull_identical,
    })
}

fn measure_one_delta(
    server: &ServerDelivery,
    from: &Checkpoint,
    name: &'static str,
    document_fields: usize,
    full_document_bytes: usize,
) -> Result<ChangePattern, MappingError> {
    // A single-change delta far below the budget is a scenario invariant;
    // a violation is a spike bug, so it panics the scenario.
    match server.delta_since(from, BATCH_BUDGET_BYTES)? {
        DeltaOutcome::Delta { bytes, commits, within_budget, .. } => {
            assert!(within_budget, "single-change delta exceeded the budget");
            Ok(ChangePattern {
                name,
                document_fields,
                full_document_bytes,
                delta_bytes: bytes.len(),
                commits,
            })
        }
        DeltaOutcome::UpToDate => {
            Err(MappingError::Loro(format!("no delta produced for pattern {name}")))
        }
        DeltaOutcome::BehindRetainedFloor { .. } => {
            Err(MappingError::Loro(format!("pattern {name} fell behind the retained floor")))
        }
    }
}

fn sync_to(
    server: &mut ServerDelivery,
    consumer: &mut ConsumerSession,
) -> Result<usize, MappingError> {
    let mut batches = 0usize;
    loop {
        match server.delta_since(consumer.checkpoint(), BATCH_BUDGET_BYTES)? {
            DeltaOutcome::UpToDate => return Ok(batches),
            DeltaOutcome::BehindRetainedFloor { .. } => {
                consumer.import_snapshot(&server.snapshot_bytes()?)?;
                return Ok(batches);
            }
            DeltaOutcome::Delta { bytes, next, .. } => {
                batches += 1;
                consumer.apply_delta(&bytes, &next)?;
            }
        }
    }
}
