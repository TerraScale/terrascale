//! In-crate storage compaction drills (GAP-S1).
//!
//! These tests construct storage state directly because the kill points they
//! prove live below the public Engine seam, and because the public seam has
//! no data path by design (`TenantScope` is Platform-created). Every drill
//! follows the decision record's kill-point discipline
//! (`docs/storage/gaps1-storage-compaction.md` D4): arm one fault inside the
//! compaction sequence, drop the storage without flushing — the process-kill
//! model this engine's durability is observed through — reopen, and prove
//! that no document, no resolution, and no below-bound coverage was lost.

use std::{
    path::PathBuf,
    sync::atomic::{AtomicU64, Ordering},
};

use crate::types::{
    CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion, LocalDurability,
    OperationId, StorageSequence, TenantId,
};

use super::faults::FaultPoint;
use super::{
    CompactionPolicy, FileStorage, HistoryReclaimOutcome, StoragePort,
    frontier::{
        LedgerFrontierActivationIdentity, LedgerFrontierActivationRequest, LedgerFrontierStatus,
    },
    intent::{CommitIntent, CommitResult, StagedWrite},
    write_set_digest,
};

fn location(tag: &str) -> PathBuf {
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    let index = COUNTER.fetch_add(1, Ordering::Relaxed);
    std::env::temp_dir()
        .join(format!("terrabase-gaps1-compaction-{tag}-{}-{index}", std::process::id()))
}

fn tenant() -> TenantId {
    TenantId::try_new("tenant-a").expect("valid tenant")
}

fn document(id: &str, collection: &str, text: &str) -> Document {
    Document::new(
        DocumentId::try_new(id).expect("valid identifier"),
        CollectionName::try_new(collection).expect("valid identifier"),
        DocumentValue::String(text.to_owned()),
    )
}

fn put(document: Document, expected: ExpectedVersion) -> StagedWrite {
    StagedWrite::Put { document, expected }
}

fn delete(collection: &str, id: &str, expected: ExpectedVersion) -> StagedWrite {
    StagedWrite::Delete {
        collection: Some(CollectionName::try_new(collection).expect("valid identifier")),
        id: DocumentId::try_new(id).expect("valid identifier"),
        expected,
    }
}

fn fresh_intent(
    storage: &FileStorage,
    operation: &str,
    writes: Vec<StagedWrite>,
    durability: LocalDurability,
) -> CommitIntent {
    let tenant = tenant();
    CommitIntent {
        digest: write_set_digest(&tenant, &writes),
        tenant,
        operation: OperationId::try_new(operation).expect("valid identifier"),
        snapshot_sequence: storage.published_state().0,
        durability,
        writes,
    }
}

fn open_storage(tag: &str) -> (PathBuf, FileStorage) {
    let dir = location(tag);
    std::fs::remove_dir_all(&dir).ok();
    let storage = FileStorage::open(&dir).expect("opens a fresh location");
    (dir, storage)
}

fn activate_frontier(storage: &mut FileStorage, activation: &str) {
    let expected = match storage.ledger_frontier_status() {
        LedgerFrontierStatus::Inactive(observation) => observation,
        status => panic!("root store is inactive before activation, got {status:?}"),
    };
    storage
        .activate_ledger_frontier(&LedgerFrontierActivationRequest {
            identity: LedgerFrontierActivationIdentity {
                activation_id: OperationId::try_new(activation).expect("valid activation"),
                external_fence_generation: 1,
                external_record_digest: [0x44; 32],
            },
            expected,
        })
        .expect("frontier activates");
}

/// Seeds one store with the drill workload: two live documents, one deleted
/// document whose tombstone is the evidence the drills track, and one
/// trailing put so the delete lands inside the window a pass gives up (the
/// head record itself is always kept, decision record D2).
fn seed(storage: &mut FileStorage) {
    let outcomes = [
        storage.commit(fresh_intent(
            storage,
            "op-put-1",
            vec![put(document("doc-1", "users", "v1"), ExpectedVersion::New)],
            LocalDurability::Synced,
        )),
        storage.commit(fresh_intent(
            storage,
            "op-put-2",
            vec![put(document("doc-2", "users", "v1"), ExpectedVersion::New)],
            LocalDurability::Synced,
        )),
        storage.commit(fresh_intent(
            storage,
            "op-delete-1",
            vec![delete("users", "doc-2", ExpectedVersion::Exact(1))],
            LocalDurability::Synced,
        )),
        storage.commit(fresh_intent(
            storage,
            "op-put-3",
            vec![put(document("doc-3", "users", "v1"), ExpectedVersion::New)],
            LocalDurability::Synced,
        )),
    ];
    for outcome in outcomes {
        assert!(matches!(outcome, CommitResult::Committed(_)));
    }
}

/// The no-loss assertion every kill-point drill reopens with: both live
/// documents readable, the deleted one absent, and every seeded operation
/// resolving as committed.
fn assert_no_loss(storage: &FileStorage) {
    let tenant = tenant();
    let (published, _) = storage.published_state();
    assert!(
        storage.read_document_at(published, &tenant, document("doc-1", "users", "").id()).is_some(),
        "doc-1 survives"
    );
    assert!(
        storage.read_document_at(published, &tenant, document("doc-2", "users", "").id()).is_none(),
        "the deleted doc-2 stays deleted"
    );
    assert!(
        storage.read_document_at(published, &tenant, document("doc-3", "users", "").id()).is_some(),
        "doc-3 survives"
    );
    for operation in ["op-put-1", "op-put-2", "op-delete-1", "op-put-3"] {
        let identity = OperationId::try_new(operation).expect("valid identifier");
        assert!(
            matches!(
                storage.resolve_operation(&tenant, &identity),
                Some(super::intent::OperationResolution::Committed { .. })
            ),
            "{operation} still resolves as committed after the kill and reopen"
        );
    }
}

fn compact_request(policy: CompactionPolicy) -> super::compaction::StorageCompaction {
    super::compaction::StorageCompaction { policy, floor: None, progress: None }
}

#[test]
fn reclaim_pass_truncates_the_wal_and_removes_superseded_roots() {
    let (dir, mut storage) = open_storage("reclaim");
    seed(&mut storage);
    let (published, _) = storage.published_state();
    drop(storage);

    let wal_bytes = std::fs::metadata(dir.join("wal")).expect("wal exists").len();
    let roots = std::fs::read_dir(dir.join("roots")).expect("roots dir").count();
    assert!(roots >= 3, "every durable commit wrote a root file: {roots}");

    let mut reopened = FileStorage::open(&dir).expect("reopens");
    let (receipt, _) = reopened
        .compact(compact_request(CompactionPolicy::Reclaim))
        .expect("the reclaim pass runs");
    assert_eq!(receipt.checkpoint_sequence(), Some(published));
    assert_eq!(receipt.wal_bytes_before(), wal_bytes);
    assert_eq!(receipt.wal_bytes_after(), 0, "the checkpointed WAL is truncated");
    assert_eq!(receipt.roots_before(), roots as u64);
    assert!(receipt.roots_removed() >= 2, "superseded roots are reclaimed");
    assert_eq!(receipt.roots_unremovable(), 0);
    assert_eq!(receipt.history_reclaim(), HistoryReclaimOutcome::NotRequested);
    assert_eq!(receipt.servable_start_before(), receipt.servable_start_after());
    assert_no_loss(&reopened);

    // The compacted store still commits and reopens.
    assert!(matches!(
        reopened.commit(fresh_intent(
            &reopened,
            "op-after",
            vec![put(document("doc-4", "users", "v1"), ExpectedVersion::New)],
            LocalDurability::Synced,
        )),
        CommitResult::Committed(_)
    ));
    drop(reopened);
    let again = FileStorage::open(&dir).expect("reopens after post-compaction commits");
    assert_no_loss(&again);
    let tenant = tenant();
    assert!(
        again
            .read_document_at(
                again.published_state().0,
                &tenant,
                document("doc-4", "users", "").id()
            )
            .is_some(),
        "the post-compaction commit's document survives the reopen"
    );
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn reclaim_refuses_before_mutation_while_a_ready_publication_needs_the_wal() {
    let (dir, mut storage) = open_storage("pending-ready");
    storage.arm_test_fault(FaultPoint::PointerPublish);
    assert!(matches!(
        storage.commit(fresh_intent(
            &storage,
            "op-pending",
            vec![put(document("doc-pending", "users", "value"), ExpectedVersion::New)],
            LocalDurability::Synced,
        )),
        CommitResult::Unknown(super::intent::UnknownCause::ResponseInterrupted)
    ));
    let wal_bytes = std::fs::metadata(dir.join("wal")).expect("WAL exists").len();
    assert!(wal_bytes > 0, "the verified commit group awaits recovery");

    assert_eq!(
        storage.compact(compact_request(CompactionPolicy::Reclaim)).err(),
        Some(super::compaction::CompactionError::Storage),
        "compaction cannot discard the only resumable write set"
    );
    assert_eq!(
        std::fs::metadata(dir.join("wal")).expect("WAL remains").len(),
        wal_bytes,
        "the refusal happens before WAL truncation"
    );
    drop(storage);

    let reopened = FileStorage::open(&dir).expect("recovery resumes the publication");
    let tenant = tenant();
    let operation = OperationId::try_new("op-pending").expect("valid operation");
    assert!(matches!(
        reopened.resolve_operation(&tenant, &operation),
        Some(super::intent::OperationResolution::Committed { sequence, .. })
            if sequence == StorageSequence::new(1)
    ));
    let (published, _) = reopened.published_state();
    assert!(
        reopened
            .read_document_at(
                published,
                &tenant,
                &DocumentId::try_new("doc-pending").expect("valid document")
            )
            .is_some(),
        "recovery applies the write set that compaction preserved"
    );
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn reclaim_refuses_when_a_verified_wal_commit_precedes_the_ready_record() {
    let (dir, mut storage) = open_storage("pending-before-ready");
    storage.arm_test_fault(FaultPoint::ReadyLedgerSync);
    assert!(matches!(
        storage.commit(fresh_intent(
            &storage,
            "op-pending",
            vec![put(document("doc-pending", "users", "value"), ExpectedVersion::New)],
            LocalDurability::Synced,
        )),
        CommitResult::Unknown(super::intent::UnknownCause::ResponseInterrupted)
    ));
    let wal_bytes = std::fs::metadata(dir.join("wal")).expect("WAL exists").len();
    assert!(wal_bytes > 0, "the verified commit group awaits recovery");

    assert_eq!(
        storage.compact(compact_request(CompactionPolicy::Reclaim)).err(),
        Some(super::compaction::CompactionError::Storage),
        "compaction cannot rely on the Ready index alone"
    );
    assert_eq!(
        std::fs::metadata(dir.join("wal")).expect("WAL remains").len(),
        wal_bytes,
        "the refusal preserves the pre-Ready commit group"
    );
    drop(storage);

    let reopened = FileStorage::open(&dir).expect("recovery rolls the commit forward");
    let tenant = tenant();
    let operation = OperationId::try_new("op-pending").expect("valid operation");
    assert!(matches!(
        reopened.resolve_operation(&tenant, &operation),
        Some(super::intent::OperationResolution::Committed { sequence, .. })
            if sequence == StorageSequence::new(1)
    ));
    let (published, _) = reopened.published_state();
    assert!(
        reopened
            .read_document_at(
                published,
                &tenant,
                &DocumentId::try_new("doc-pending").expect("valid document")
            )
            .is_some(),
        "recovery applies the preserved write set"
    );
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn history_reclaim_gives_up_the_window_and_holds_the_evidence_durally() {
    let (dir, mut storage) = open_storage("history-reclaim");
    seed(&mut storage);
    let head_before = storage.journaled_high_water();
    assert_eq!(head_before, Some(StorageSequence::new(4)));
    assert_eq!(storage.servable_history_start(), Some(StorageSequence::new(1)));

    let (receipt, facts) = storage
        .compact(compact_request(CompactionPolicy::ReclaimHistory))
        .expect("the history reclaim pass runs");
    let bound = StorageSequence::new(4);
    assert_eq!(receipt.history_reclaim(), HistoryReclaimOutcome::Reclaimed { bound });
    assert_eq!(receipt.journaled_head(), Some(bound));
    assert_eq!(facts.bound, Some(bound));
    // The head record stays servable; everything older is gone.
    assert_eq!(storage.servable_history_start(), Some(bound));
    assert_eq!(storage.journaled_high_water(), Some(bound), "the high-water never regresses");
    let tenant = tenant();
    let served = storage.tenant_commits(&tenant, StorageSequence::new(0), 10);
    assert!(
        served.iter().all(|commit| commit.sequence().get() >= bound.get()),
        "nothing below the bound is served"
    );
    let after_bound = storage.tenant_commits(&tenant, StorageSequence::new(2), 10);
    assert_eq!(after_bound.len(), 1, "the head record is still servable");
    assert_eq!(after_bound[0].sequence().get(), 4);
    assert!(receipt.evidence_extracted() >= 1, "the tombstone was extracted");
    assert_eq!(receipt.evidence_below_floor(), 0);
    assert_eq!(receipt.window_commits(), 3, "the window held the three older commits");
    assert_no_loss(&storage);

    // The evidence is durable: it survives a reopen.
    drop(storage);
    let reopened = FileStorage::open(&dir).expect("reopens with a compacted journal");
    let state = reopened.retained_state();
    assert_eq!(state.bound, Some(bound));
    let facts = state
        .facts
        .iter()
        .filter(|fact| fact.tenant == tenant && fact.id.as_str() == "doc-2")
        .count();
    assert_eq!(facts, 1, "the deleted document's retained fact is durably held");
    let non_deletion = state
        .non_deletion
        .iter()
        .find(|(entry_tenant, _)| entry_tenant == &tenant)
        .map(|(_, through)| *through);
    assert_eq!(
        non_deletion,
        Some(Some(StorageSequence::new(2))),
        "the window's newest put is recorded"
    );
    assert_no_loss(&reopened);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn kill_points_inside_the_compaction_sequence_lose_nothing() {
    for point in [
        FaultPoint::CompactionCheckpointPublish,
        FaultPoint::CompactionWalTruncate,
        FaultPoint::CompactionEvidenceSync,
        FaultPoint::CompactionJournalRewrite,
        FaultPoint::CompactionRootReclaim,
    ] {
        let (dir, mut storage) = open_storage(&format!("kill-{point:?}").to_lowercase());
        seed(&mut storage);
        storage.arm_test_fault(point);
        assert!(
            storage.compact(compact_request(CompactionPolicy::ReclaimHistory)).is_err(),
            "the armed kill point aborts the pass"
        );
        // The process dies here: no flush, no shutdown.
        drop(storage);

        let reopened = FileStorage::open(&dir).expect("the killed location reopens");
        assert_no_loss(&reopened);
        // The coverage invariant: whenever the journal no longer serves a
        // sequence, the retained evidence store covers the window's deletes.
        let start = reopened.servable_history_start();
        let delete_sequence = StorageSequence::new(4);
        if start.is_some_and(|start| start > StorageSequence::new(1)) {
            let state = reopened.retained_state();
            assert!(
                state.facts.iter().any(|fact| fact.id.as_str() == "doc-2"),
                "evidence covers the delete the journal gave up (kill {point:?})"
            );
            assert!(
                state.bound.is_some_and(|bound| bound >= delete_sequence),
                "the durable bound names the given-up window (kill {point:?})"
            );
        }
        // Whatever the state, a retry completes and converges.
        drop(reopened);
        let mut resumed = FileStorage::open(&dir).expect("reopens for the retry");
        let (receipt, _) = resumed
            .compact(compact_request(CompactionPolicy::ReclaimHistory))
            .expect("the retry pass completes");
        assert_eq!(
            receipt.history_reclaim(),
            HistoryReclaimOutcome::Reclaimed { bound: StorageSequence::new(4) },
            "the retry converges (kill {point:?})"
        );
        assert_no_loss(&resumed);
        std::fs::remove_dir_all(&dir).ok();
    }
}

#[test]
fn history_reclaim_refuses_while_evidence_below_the_bound_is_diverged() {
    let (dir, mut storage) = open_storage("divergence-gate");
    assert!(matches!(
        storage.commit(fresh_intent(
            &storage,
            "op-1",
            vec![put(document("doc-1", "users", "v1"), ExpectedVersion::New)],
            LocalDurability::Synced,
        )),
        CommitResult::Committed(_)
    ));
    // One published-but-unjournaled commit (repair F2's divergence): the
    // journal append fails, the publication still succeeds. The document is
    // a fresh one so `assert_no_loss` below stays about the seeded state.
    storage.arm_test_fault(FaultPoint::JournalAppend);
    assert!(matches!(
        storage.commit(fresh_intent(
            &storage,
            "op-diverged",
            vec![put(document("doc-x", "users", "v1"), ExpectedVersion::New)],
            LocalDurability::Synced,
        )),
        CommitResult::Committed(_)
    ));
    // A later healthy commit heals the journaled high-water mark past the
    // skipped sequence, so the divergence sits below the candidate bound
    // exactly as the healed-mark shape names it.
    assert!(matches!(
        storage.commit(fresh_intent(
            &storage,
            "op-3",
            vec![put(document("doc-3", "users", "v1"), ExpectedVersion::New)],
            LocalDurability::Synced,
        )),
        CommitResult::Committed(_)
    ));
    assert_eq!(storage.journal_divergence(), 1);
    assert_eq!(storage.journaled_high_water(), Some(StorageSequence::new(3)));

    let (receipt, _) = storage
        .compact(compact_request(CompactionPolicy::ReclaimHistory))
        .expect("the pass still runs its safe phases");
    assert_eq!(
        receipt.history_reclaim(),
        HistoryReclaimOutcome::RefusedDivergence { diverged: 1 },
        "history is not given up while a below-bound commit was never journaled"
    );
    assert_eq!(storage.servable_history_start(), Some(StorageSequence::new(1)));
    assert!(storage.retained_state().bound.is_none(), "no bound was declared");
    assert!(receipt.wal_bytes_after() < receipt.wal_bytes_before(), "the safe phases still ran");
    // This test's own workload survives the refused pass intact (the shared
    // no-loss helper asserts the seeded workload, which this test does not
    // run).
    let tenant = tenant();
    let (published, _) = storage.published_state();
    for id in ["doc-1", "doc-x", "doc-3"] {
        assert!(
            storage.read_document_at(published, &tenant, document(id, "users", "").id()).is_some(),
            "{id} survives the refused pass"
        );
    }
    for operation in ["op-1", "op-diverged", "op-3"] {
        let identity = OperationId::try_new(operation).expect("valid identifier");
        assert!(
            matches!(
                storage.resolve_operation(&tenant, &identity),
                Some(super::intent::OperationResolution::Committed { .. })
            ),
            "{operation} still resolves as committed after the refused pass"
        );
    }
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn history_reclaim_honors_the_retention_floor() {
    // A floor below the tombstone: the fact is at or above it, so the pass
    // retains the evidence durably.
    let (dir, mut storage) = open_storage("retention-floor-covered");
    seed(&mut storage);
    let request = super::compaction::StorageCompaction {
        policy: CompactionPolicy::ReclaimHistory,
        floor: Some(StorageSequence::new(2)),
        progress: None,
    };
    let (receipt, _) = storage.compact(request).expect("the pass runs");
    assert_eq!(receipt.evidence_below_floor(), 0, "no drill fact sits below this floor");
    assert_eq!(receipt.evidence_retained(), 1);
    drop(storage);

    let reopened = FileStorage::open(&dir).expect("reopens");
    let state = reopened.retained_state();
    assert_eq!(state.facts.len(), 1);
    assert_eq!(state.facts[0].sequence, StorageSequence::new(3));
    assert_no_loss(&reopened);
    std::fs::remove_dir_all(&dir).ok();

    // A floor above the tombstone: the watermark no longer needs the
    // evidence, so the pass skips the fact and discloses the count instead
    // of silently dropping it — and declares nothing it does not hold.
    let (dir, mut storage) = open_storage("retention-floor-above");
    seed(&mut storage);
    let request = super::compaction::StorageCompaction {
        policy: CompactionPolicy::ReclaimHistory,
        floor: Some(StorageSequence::new(4)),
        progress: None,
    };
    let (receipt, _) = storage.compact(request).expect("the pass runs");
    assert_eq!(receipt.evidence_below_floor(), 1, "the below-floor fact is disclosed");
    assert_eq!(receipt.evidence_retained(), 0, "the store keeps only floor-covered facts");
    drop(storage);
    let reopened = FileStorage::open(&dir).expect("reopens");
    assert!(reopened.retained_state().facts.is_empty(), "no below-floor fact was written durably");
    assert_eq!(reopened.retained_state().bound, Some(StorageSequence::new(4)));
    assert_no_loss(&reopened);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn a_torn_retained_store_tail_is_truncated_and_midfile_damage_fails_closed() {
    let (dir, mut storage) = open_storage("store-damage");
    seed(&mut storage);
    assert!(storage.compact(compact_request(CompactionPolicy::ReclaimHistory)).is_ok());
    drop(storage);

    // An interrupted append: trailing garbage bytes.
    let retained = dir.join("retained");
    let mut bytes = std::fs::read(&retained).expect("store reads");
    let verified = bytes.len();
    bytes.extend_from_slice(&[0x01, 0x00, 0x00]);
    std::fs::write(&retained, &bytes).expect("store rewrites");
    let reopened = FileStorage::open(&dir).expect("a torn store tail opens on its prefix");
    assert_eq!(
        reopened.retained_state().bound,
        Some(StorageSequence::new(4)),
        "the verified prefix still names the bound"
    );
    drop(reopened);
    assert_eq!(
        std::fs::metadata(&retained).expect("store exists").len(),
        verified as u64,
        "the torn tail was truncated so later appends stay scannable"
    );

    // Mid-file damage: the evidence the bound rests on is unreadable, so the
    // open fails closed instead of serving a prefix silently.
    let mut damaged = std::fs::read(&retained).expect("store reads");
    damaged[13] = damaged[13].wrapping_add(1);
    std::fs::write(&retained, damaged).expect("store rewrites");
    assert_eq!(
        FileStorage::open(&dir).err(),
        Some(super::StorageOpenError::FormatMismatch),
        "mid-file retained-evidence damage fails the open"
    );
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn an_impossible_declared_length_fails_the_open_and_a_legal_torn_tail_still_truncates() {
    let (dir, mut storage) = open_storage("store-length-overrun");
    seed(&mut storage);
    assert!(storage.compact(compact_request(CompactionPolicy::ReclaimHistory)).is_ok());
    drop(storage);

    let retained = dir.join("retained");
    let good = std::fs::read(&retained).expect("store reads");
    // The compacted store holds at least two records — the fact and the
    // bound — so damaging the first record's length field is the mid-file
    // shape: everything after it (the bound the location's history bound
    // rests on) must not be silently truncated away.
    let mut damaged = good.clone();
    damaged[0..4].copy_from_slice(&u32::MAX.to_le_bytes());
    std::fs::write(&retained, &damaged).expect("store rewrites");
    assert_eq!(
        FileStorage::open(&dir).err(),
        Some(super::StorageOpenError::FormatMismatch),
        "a declared length no legal record could carry is integrity damage, not an interrupted append"
    );

    // The genuine torn tail keeps the gentler reading: a frame whose header
    // declares a legal length but whose payload is cut off by the end of
    // the file is an interrupted append, truncated to the verified prefix
    // exactly like the journal's.
    let mut torn = good.clone();
    torn.extend_from_slice(&24_u32.to_le_bytes());
    torn.extend_from_slice(&[0_u8; 8]);
    torn.extend_from_slice(&[0xa5, 0xa5]);
    std::fs::write(&retained, &torn).expect("store rewrites");
    let reopened = FileStorage::open(&dir).expect("a legal-length torn tail opens on its prefix");
    assert_eq!(
        reopened.retained_state().bound,
        Some(StorageSequence::new(4)),
        "the verified prefix still names the bound"
    );
    drop(reopened);
    assert_eq!(
        std::fs::metadata(&retained).expect("store exists").len(),
        good.len() as u64,
        "the torn tail was truncated so later appends stay scannable"
    );
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn a_truncated_wal_with_a_corrupt_manifest_recovers_from_the_newest_root() {
    let (dir, mut storage) = open_storage("root-fallback");
    seed(&mut storage);
    assert!(storage.compact(compact_request(CompactionPolicy::Reclaim)).is_ok());
    drop(storage);

    // The manifest no longer verifies; with the WAL truncated there are no
    // frames to rebuild from, so recovery must fall back to the newest
    // verifying root file (decision D7).
    let manifest = dir.join("manifest");
    let mut bytes = std::fs::read(&manifest).expect("manifest reads");
    let last = bytes.len() - 1;
    bytes[last] = bytes[last].wrapping_add(1);
    std::fs::write(&manifest, bytes).expect("manifest rewrites");

    let reopened = FileStorage::open(&dir).expect("recovers from the newest verifying root");
    assert_no_loss(&reopened);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn a_checkpoint_makes_a_buffered_tail_durable_and_resolvable() {
    let (dir, mut storage) = open_storage("buffered-tail");
    seed(&mut storage);
    assert!(matches!(
        storage.commit(fresh_intent(
            &storage,
            "op-buffered",
            vec![put(document("doc-4", "users", "v1"), ExpectedVersion::New)],
            LocalDurability::Buffered,
        )),
        CommitResult::Committed(_)
    ));
    assert!(storage.compact(compact_request(CompactionPolicy::Reclaim)).is_ok());
    drop(storage);

    let reopened = FileStorage::open(&dir).expect("reopens");
    let tenant = tenant();
    assert!(
        reopened
            .read_document_at(
                reopened.published_state().0,
                &tenant,
                document("doc-4", "users", "").id()
            )
            .is_some(),
        "the checkpointed Buffered commit's data survived the process exit"
    );
    let identity = OperationId::try_new("op-buffered").expect("valid identifier");
    match reopened.resolve_operation(&tenant, &identity) {
        Some(super::intent::OperationResolution::Committed { durability, .. }) => {
            // The resolution escalates from `Pending` to `Committed` (the
            // root identity the Ready record names is now the published
            // root); the reported level stays the requested one, because
            // `resolution_for` caps every claim at what the operation asked
            // for (`min_level(requested, proven)`).
            assert_eq!(durability, LocalDurability::Buffered);
        }
        other => panic!("the checkpointed Buffered commit must resolve, got {other:?}"),
    }
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn replay_identity_survives_compaction() {
    let (dir, mut storage) = open_storage("replay-identity");
    seed(&mut storage);
    let (before, _) = storage.published_state();
    assert!(storage.compact(compact_request(CompactionPolicy::ReclaimHistory)).is_ok());

    // Same identity, same write set (the same expected-version clause, so
    // the digest binds): the recorded resolution replays.
    assert!(matches!(
        storage.commit(fresh_intent(
            &storage,
            "op-put-1",
            vec![put(document("doc-1", "users", "v1"), ExpectedVersion::New)],
            LocalDurability::Synced,
        )),
        CommitResult::Committed(_)
    ));
    assert_eq!(storage.published_state().0, before, "a replayed resolution republishes nothing");
    // A different write set under the same identity is still reuse.
    assert!(matches!(
        storage.commit(fresh_intent(
            &storage,
            "op-put-1",
            vec![put(document("doc-1", "users", "mutated"), ExpectedVersion::New)],
            LocalDurability::Synced,
        )),
        CommitResult::Rejected(super::intent::CommitRejection::OperationIdReuse)
    ));
    assert_no_loss(&storage);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn a_killed_pass_between_the_store_and_the_journal_is_idempotent_on_retry() {
    let (dir, mut storage) = open_storage("kill-idempotent");
    seed(&mut storage);
    storage.arm_test_fault(FaultPoint::CompactionJournalRewrite);
    assert!(storage.compact(compact_request(CompactionPolicy::ReclaimHistory)).is_err());
    drop(storage);

    let mut reopened = FileStorage::open(&dir).expect("reopens in the redundant state");
    let state = reopened.retained_state();
    assert_eq!(state.bound, Some(StorageSequence::new(4)), "the evidence and bound are durable");
    assert_eq!(
        reopened.servable_history_start(),
        Some(StorageSequence::new(1)),
        "the journal still redundantly serves the window"
    );
    let (receipt, _) = reopened
        .compact(compact_request(CompactionPolicy::ReclaimHistory))
        .expect("the retry completes");
    assert_eq!(receipt.evidence_retained(), 0, "the durable fact was not re-appended");
    assert_eq!(reopened.retained_state().facts.len(), 1, "no duplicate facts landed");
    assert_no_loss(&reopened);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn retained_frontier_failure_poison_stops_before_history_rewrite_and_repairs_on_reopen() {
    let (dir, mut storage) = open_storage("retained-frontier-failure");
    seed(&mut storage);
    activate_frontier(&mut storage, "activate-retained-failure");
    let history_before = std::fs::read(dir.join("history")).expect("history reads");
    storage.arm_test_fault(FaultPoint::RetainedFrontierSync);
    assert!(
        storage.compact(compact_request(CompactionPolicy::ReclaimHistory)).is_err(),
        "frontier failure refuses the pass"
    );
    assert_eq!(
        std::fs::read(dir.join("history")).expect("history rereads"),
        history_before,
        "history is untouched until retained bytes and their frontier both sync"
    );
    assert!(
        storage.compact(compact_request(CompactionPolicy::ReclaimHistory)).is_err(),
        "the poisoned handle performs no later compaction mutation"
    );
    drop(storage);

    let mut reopened = FileStorage::open(&dir).expect("valid retained suffix repairs on reopen");
    assert_eq!(
        reopened.servable_history_start(),
        Some(StorageSequence::new(1)),
        "the redundant history window still serves"
    );
    reopened
        .compact(compact_request(CompactionPolicy::ReclaimHistory))
        .expect("a reopened handle completes the pass");
    assert_eq!(reopened.retained_state().facts.len(), 1, "the scan deduplicates retry evidence");
    assert_no_loss(&reopened);
    std::fs::remove_dir_all(dir).ok();
}
