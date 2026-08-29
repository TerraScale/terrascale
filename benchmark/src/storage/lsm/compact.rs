//! Flush, merge, and the compaction pass of the LSM engine (P1).
//!
//! The flush moves frozen memtables into immutable level-0 segments,
//! publishes the manifest that names them, and truncates the WAL the
//! checkpoint replaced — the manifest-anchored WAL reclaim the W3-1
//! adjudication anticipated (PD-2026-08-24-03 R6). The merge folds
//! level-0 segments into level 1 once their count crosses the threshold.
//!
//! History reclaim follows the GAP-S1 gates: deletion evidence is extracted
//! into the durable retained-evidence store, the bound is declared durably,
//! and only then does the history floor advance. Physical reclaim of the
//! history entries below the floor happens when their segments merge.

use crate::types::StorageSequence;

use super::{
    LsmStorage,
    internal::{Entry, Keyspace},
    segment::{self, SegmentMeta},
};
use crate::storage::compaction::{
    CompactionDriverFacts, CompactionError, CompactionPhase, CompactionPolicy, CompactionReceipt,
    HistoryReclaimOutcome, StorageCompaction,
};
use crate::storage::faults::FaultPoint;
use crate::storage::retained::RetainedFact;
use crate::types::TenantId;

/// Writes every frozen memtable out as a level-0 segment and publishes the
/// manifest that names them. Returns the segments written.
pub(super) fn flush_frozen(storage: &mut LsmStorage) -> Vec<SegmentMeta> {
    let mut written = Vec::new();
    let dir = storage.dir().to_path_buf();
    while !storage.frozen.is_empty() {
        let frozen = storage.frozen.remove(0);
        let entries: Vec<Entry> = frozen.iter().collect();
        if entries.is_empty() {
            continue;
        }
        let id = storage.manifest.allocate_segment_id();
        let path = super::segment_path(&dir, id);
        if segment::write_segment(&path, id, 0, &entries).is_err() {
            // A failed flush leaves the frozen table in place; the next
            // pass retries. Data is still durable in the WAL.
            storage.frozen.insert(0, frozen);
            break;
        }
        let meta = SegmentMeta {
            id,
            level: 0,
            entry_count: entries.len() as u64,
            byte_len: std::fs::metadata(&path).map_or(0, |meta| meta.len()),
            min_key: entries.first().map_or_else(Vec::new, |entry| entry.key.user_key().to_vec()),
            max_key: entries.last().map_or_else(Vec::new, |entry| entry.key.user_key().to_vec()),
        };
        if let Ok(reader) = segment::SegmentReader::open(&path, id, 0) {
            storage.readers.insert(id, std::cell::RefCell::new(reader));
        }
        storage.manifest.segments.push(meta.clone());
        written.push(meta);
    }
    if !written.is_empty() {
        storage.manifest.epoch += 1;
        storage.manifest.wal_checkpoint = storage.sequence_watermark;
        if storage.manifest.publish(&dir).is_ok() {
            let _ = storage.wal.truncate_after_checkpoint();
        }
    }
    written
}

/// Merges the level-0 segments (and any level-1 segments they overlap)
/// into one level-1 segment once the threshold is crossed. All versions
/// are preserved except exact duplicates; the history floor filters
/// reclaimed history entries physically.
pub(super) fn merge_l0(storage: &mut LsmStorage) -> Option<(u64, u64)> {
    let l0: Vec<SegmentMeta> =
        storage.manifest.segments.iter().filter(|meta| meta.level == 0).cloned().collect();
    if l0.len() < storage.tuning.l0_merge_threshold {
        return None;
    }
    let mut candidates: Vec<SegmentMeta> = l0;
    // Include level-1 segments the L0 key range overlaps so the output
    // stays disjoint per level.
    let min = candidates.iter().map(|meta| meta.min_key.clone()).min()?;
    let max = candidates.iter().map(|meta| meta.max_key.clone()).max()?;
    for meta in &storage.manifest.segments {
        if meta.level == 1 && meta.max_key >= min && meta.min_key <= max {
            candidates.push(meta.clone());
        }
    }
    let mut merged: Vec<Entry> = Vec::new();
    for meta in &candidates {
        if let Some(reader) = storage.readers.get(&meta.id)
            && let Ok(entries) = reader.borrow_mut().read_all()
        {
            merged.extend(entries);
        }
    }
    merged.sort_by(|left, right| left.key.cmp(&right.key));
    merged.dedup_by(|left, right| left.key == right.key);
    let floor = storage.history_floor.unwrap_or(0);
    merged.retain(|entry| {
        entry.key.tag() != Keyspace::History.tag() || entry.key.sequence() >= floor
    });
    let dir = storage.dir().to_path_buf();
    let id = storage.manifest.allocate_segment_id();
    let path = super::segment_path(&dir, id);
    segment::write_segment(&path, id, 1, &merged).ok()?;
    let meta = SegmentMeta {
        id,
        level: 1,
        entry_count: merged.len() as u64,
        byte_len: std::fs::metadata(&path).map_or(0, |metadata| metadata.len()),
        min_key: merged.first().map_or_else(Vec::new, |entry| entry.key.user_key().to_vec()),
        max_key: merged.last().map_or_else(Vec::new, |entry| entry.key.user_key().to_vec()),
    };
    if let Ok(reader) = segment::SegmentReader::open(&path, id, 1) {
        storage.readers.insert(id, std::cell::RefCell::new(reader));
    }
    let removed: Vec<u64> = candidates.iter().map(|meta| meta.id).collect();
    let removed_before = removed.len() as u64;
    storage.manifest.segments.retain(|meta| !removed.contains(&meta.id));
    storage.manifest.segments.push(meta);
    storage.manifest.epoch += 1;
    let publish_ok = storage.manifest.publish(&dir).is_ok();
    if publish_ok {
        for id in removed {
            storage.readers.remove(&id);
            let _ = std::fs::remove_file(super::segment_path(&dir, id));
        }
    }
    Some((removed_before, 1))
}

/// Runs one compaction pass over the LSM engine.
#[allow(clippy::too_many_lines, reason = "one pass, the GAP-S1 phase order in sequence")]
pub(crate) fn run_compaction(
    storage: &mut LsmStorage,
    request: StorageCompaction,
) -> Result<(CompactionReceipt, CompactionDriverFacts), CompactionError> {
    let StorageCompaction { policy, floor, progress } = request;
    let progress = progress.as_ref();
    let mut phases: Vec<CompactionPhase> = Vec::new();

    let wal_bytes_before = storage.wal_len();
    let journal_records_before = u64::try_from(storage.history_index.len()).unwrap_or(u64::MAX);
    let servable_start_before = storage.history_floor.map(StorageSequence::new);
    let roots_before = u64::try_from(storage.manifest.segments.len()).unwrap_or(u64::MAX);
    let head = storage.journaled_high_water_impl();

    // Phase: checkpoint. Freeze the live memtable, flush every frozen table
    // into segments, publish the naming manifest, then truncate the WAL the
    // checkpoint replaced (D4 ordering: durability before shortening).
    emit(progress, CompactionPhase::Checkpoint, false, 0, 1);
    let frozen_live = std::mem::take(&mut storage.memtable);
    storage.frozen.push(frozen_live);
    flush_frozen(storage);
    if storage.faults_mut().fire(FaultPoint::CompactionCheckpointPublish).is_err() {
        return Err(CompactionError::Storage);
    }
    let checkpoint_sequence = storage.published_sequence_impl();
    phases.push(CompactionPhase::Checkpoint);
    emit(progress, CompactionPhase::Checkpoint, true, 1, 1);

    // Phase: write-ahead log reclaim. Only the published checkpoint makes
    // this safe.
    emit(progress, CompactionPhase::WalReclaim, false, 0, wal_bytes_before);
    if storage.faults_mut().fire(FaultPoint::CompactionWalTruncate).is_err() {
        return Err(CompactionError::Storage);
    }
    let _ = storage.wal_truncate_impl();
    phases.push(CompactionPhase::WalReclaim);
    emit(progress, CompactionPhase::WalReclaim, true, wal_bytes_before, wal_bytes_before);

    // Phase: merge maintenance. Folds level-0 segments past the threshold.
    let merged = merge_l0(storage);
    let roots_removed = merged.map_or(0, |(removed, _added)| removed.saturating_sub(1));
    let _roots_after = u64::try_from(storage.manifest.segments.len()).unwrap_or(u64::MAX);

    // History reclaim under the GAP-S1 gates.
    let mut bound_out: Option<StorageSequence> = None;
    let mut history_reclaim = HistoryReclaimOutcome::NotRequested;
    let mut evidence_extracted = 0_u64;
    let mut evidence_retained = 0_u64;
    let mut evidence_below_floor = 0_u64;
    let mut window: Vec<crate::storage::PublishedCommit> = Vec::new();
    let mut journal_bytes_before = 0_u64;
    for meta in &storage.manifest.segments {
        journal_bytes_before = journal_bytes_before.saturating_add(meta.byte_len);
    }
    if policy == CompactionPolicy::ReclaimHistory {
        let floor_value = floor.map(StorageSequence::get);
        let current_floor = storage.history_floor.unwrap_or(0);
        let candidate_head = head.map_or(0, StorageSequence::get);
        if candidate_head == 0 {
            history_reclaim = HistoryReclaimOutcome::RefusedUnservableWindow;
        } else {
            // The window: every commit below the candidate head and at or
            // above the current floor.
            window = storage
                .history_index
                .range(current_floor..candidate_head)
                .map(|(_, commit)| commit.clone())
                .collect();
            // Extract deletion evidence below the candidate bound, respecting
            // the watermark's retention floor.
            let mut facts: Vec<RetainedFact> = Vec::new();
            for commit in &window {
                for write in commit.writes() {
                    if let crate::storage::PublishedWrite::Delete {
                        collection,
                        id,
                        version_at_delete,
                    } = write
                    {
                        let sequence = commit.sequence().get();
                        evidence_extracted += 1;
                        if let Some(floor_value) = floor_value
                            && sequence < floor_value
                        {
                            evidence_below_floor += 1;
                            continue;
                        }
                        facts.push(RetainedFact {
                            tenant: commit.tenant().clone(),
                            collection: collection.clone(),
                            id: id.clone(),
                            version_at_delete: *version_at_delete,
                            sequence: commit.sequence(),
                            operation: commit.operation_id().clone(),
                            durability: commit.durability(),
                        });
                    }
                }
            }
            emit(progress, CompactionPhase::EvidenceExtraction, false, 0, evidence_extracted);
            // A tenant's non-deletion watermark is the highest window
            // commit that contains a put — the sequence through which the
            // tenant provably held live (non-deleted) state.
            let mut non_deletion: Vec<(TenantId, Option<StorageSequence>)> = Vec::new();
            let mut tenants: Vec<String> =
                window.iter().map(|commit| commit.tenant().as_str().to_owned()).collect();
            tenants.sort_unstable();
            tenants.dedup();
            for tenant in tenants {
                let through = window
                    .iter()
                    .filter(|commit| {
                        commit.tenant().as_str() == tenant
                            && commit.writes().iter().any(|write| {
                                matches!(write, crate::storage::PublishedWrite::Put { .. })
                            })
                    })
                    .map(crate::storage::PublishedCommit::sequence)
                    .max();
                non_deletion.push((TenantId::try_new(&tenant).expect("valid tenant"), through));
            }
            let bound = candidate_head;
            if storage
                .append_retained_facts_impl(&facts, bound, candidate_head, &non_deletion)
                .is_err()
            {
                return Err(CompactionError::Storage);
            }
            if storage.faults_mut().fire(FaultPoint::CompactionEvidenceSync).is_err() {
                return Err(CompactionError::Storage);
            }
            evidence_retained = u64::try_from(facts.len()).unwrap_or(u64::MAX);
            phases.push(CompactionPhase::EvidenceExtraction);
            emit(
                progress,
                CompactionPhase::EvidenceExtraction,
                true,
                evidence_retained,
                evidence_extracted,
            );
            // Declare the bound durably through a WAL meta entry, then
            // advance the in-memory floor.
            if storage.faults_mut().fire(FaultPoint::CompactionJournalRewrite).is_err() {
                return Err(CompactionError::Storage);
            }
            if storage.declare_history_floor_impl(bound).is_ok() {
                history_reclaim =
                    HistoryReclaimOutcome::Reclaimed { bound: StorageSequence::new(bound) };
                bound_out = Some(StorageSequence::new(bound));
                phases.push(CompactionPhase::JournalRewrite);
            }
        }
    }

    // Phase: segment reclamation. Merged-away segments are already deleted
    // by the merge; the kill point covers the final removal ordering.
    if storage.faults_mut().fire(FaultPoint::CompactionRootReclaim).is_err() {
        return Err(CompactionError::Storage);
    }
    phases.push(CompactionPhase::RootReclaim);

    let journal_records_after = u64::try_from(storage.history_index.len()).unwrap_or(u64::MAX);
    let mut journal_bytes_after = 0_u64;
    for meta in &storage.manifest.segments {
        journal_bytes_after = journal_bytes_after.saturating_add(meta.byte_len);
    }
    let receipt = CompactionReceipt::from_parts(
        Some(checkpoint_sequence),
        wal_bytes_before,
        storage.wal_len(),
        journal_bytes_before,
        journal_bytes_after,
        journal_records_before,
        journal_records_after,
        roots_before,
        roots_removed,
        0,
        servable_start_before,
        storage.history_floor.map(StorageSequence::new),
        head,
        u64::try_from(window.len()).unwrap_or(u64::MAX),
        evidence_extracted,
        evidence_retained,
        evidence_below_floor,
        history_reclaim,
        phases,
    );
    let facts = CompactionDriverFacts::from_parts(window, bound_out, head);
    Ok((receipt, facts))
}

/// Emits one bounded progress event when a sink is attached.
fn emit(
    progress: Option<&crate::storage::compaction::CompactionProgressSink>,
    phase: CompactionPhase,
    completed: bool,
    units_done: u64,
    units_total: u64,
) {
    if let Some(sink) = progress {
        sink(&crate::storage::compaction::CompactionProgress::from_parts(
            phase,
            completed,
            units_done,
            units_total,
        ));
    }
}
