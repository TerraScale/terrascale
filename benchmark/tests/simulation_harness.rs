//! Wave-0 E-12 deterministic simulation harness checks.
//!
//! The scheduler and assertions here are intentionally bounded and seeded.
//! They validate test mechanics and the reference model until the production
//! replication (E-6) and sync (E-5) seams are public.

#[path = "support/simulation/mod.rs"]
mod simulation;

use simulation::{
    FaultKind, FaultPlan, Link, MAX_CLOCK_SKEW_MS, ReferenceConfig, assert_model_invariants,
    run_reference,
};

use terrabase::engine::{ReplicatedPublication, ReplicatedRecord};
use terrabase::types::CollectionName;
use terrabase::{
    Cancellation, Document, DocumentId, DocumentValue, Engine, EngineConfig, ExpectedVersion,
    LocalDurability, OperationId, TenantId,
};

#[test]
fn seeded_schedule_replays_exactly_and_stays_bounded() {
    let first = FaultPlan::generate(0x0E12, 64, 12, &[0, 1, 2]);
    let second = FaultPlan::generate(0x0E12, 64, 12, &[0, 1, 2]);
    assert_eq!(first, second);
    assert!(first.faults().len() <= 12);
    assert!(
        first
            .faults()
            .iter()
            .all(|fault| { fault.start < fault.end && fault.end <= first.horizon() })
    );
}

#[test]
fn schedule_covers_the_wave_zero_fault_controls() {
    let plan = FaultPlan::generate(7, 80, 12, &[0, 1, 2]);
    let kinds: Vec<_> = plan.faults().iter().map(|fault| fault.kind).collect();
    assert!(kinds.iter().any(|kind| matches!(kind, FaultKind::Drop { .. })));
    assert!(kinds.iter().any(|kind| matches!(kind, FaultKind::Reorder { .. })));
    assert!(kinds.iter().any(|kind| matches!(kind, FaultKind::Duplicate { .. })));
    assert!(kinds.iter().any(|kind| matches!(kind, FaultKind::Partition { .. })));
    assert!(kinds.iter().any(|kind| matches!(kind, FaultKind::Restart { .. })));
    assert!(kinds.iter().any(|kind| matches!(kind, FaultKind::ClockSkew { .. })));
    for fault in plan.faults() {
        match fault.kind {
            FaultKind::Reorder { link, window } => {
                assert!((2..=5).contains(&window));
                assert!(plan.active_at(fault.start).any(|active| active.kind == fault.kind));
                assert!(link.first < link.second);
            }
            FaultKind::Duplicate { copies, .. } => assert!((2..=3).contains(&copies)),
            FaultKind::ClockSkew { offset_ms, .. } => {
                assert!(i64::from(offset_ms.abs()) <= MAX_CLOCK_SKEW_MS)
            }
            _ => {}
        }
    }
}

#[test]
fn reference_run_checks_ack_convergence_and_order_independence_model() {
    let report = run_reference(ReferenceConfig { seed: 19, ..ReferenceConfig::default() });
    assert!(report.deliveries >= report.acknowledged.len());
    assert!(report.plan.faults().len() <= 12);
    for region in [0, 1, 2] {
        assert!(report.world.has_all_rows(region));
    }
    assert_model_invariants(&report.world, &[0, 1, 2], &report.acknowledged)
        .expect("finite reference drain must converge");

    let link = Link::new(2, 0);
    assert_eq!(link, Link::new(0, 2));
}

#[test]
fn public_sync_journal_seam_is_bounded_and_replayable() {
    let dir =
        std::env::temp_dir().join(format!("terrabase-e12-sync-journal-{}", std::process::id()));
    std::fs::remove_dir_all(&dir).ok();
    let engine =
        Engine::open(EngineConfig::durable(&dir).expect("durable location")).expect("engine opens");
    let tenant = TenantId::try_new("e12-tenant").expect("valid tenant");
    let scope = engine.scope_for(&tenant);
    let snapshot = engine.read_snapshot(&scope).expect("snapshot");
    let mut transaction = engine
        .begin(&scope, snapshot, OperationId::try_new("e12-write-1").expect("valid operation"))
        .expect("transaction");
    transaction
        .put(
            Document::new(
                DocumentId::try_new("doc-1").expect("valid document"),
                CollectionName::try_new("items").expect("valid collection"),
                DocumentValue::String("seeded".to_owned()),
            ),
            ExpectedVersion::New,
        )
        .expect("stage write");
    assert!(matches!(
        transaction.commit(LocalDurability::Synced),
        terrabase::CommitOutcome::Committed(_)
    ));

    let batch = engine
        .streams()
        .replay_sync_journal(&scope, None, 8, &Cancellation::new())
        .expect("journal replay");
    assert_eq!(batch.records().len(), 1);
    assert!(batch.drained());
    assert!(batch.records()[0].storage_sequence() > 0);
    engine.shutdown().expect("shutdown");
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn public_replication_apply_is_idempotent_and_order_stable() {
    let collection = CollectionName::try_new("items").expect("valid collection");
    let tenant = TenantId::try_new("e12-replication").expect("valid tenant");
    let first = ReplicatedRecord {
        collection: collection.clone(),
        id: DocumentId::try_new("doc-1").expect("valid document"),
        body: DocumentValue::String("first".to_owned()),
        deleted: false,
        commit_ulid: "e12-repl-first".to_owned(),
        hlc: 10,
        origin: "gru".to_owned(),
        origin_sequence: 1,
        context: Vec::new(),
    };
    let second = ReplicatedRecord {
        collection: collection.clone(),
        id: first.id.clone(),
        body: DocumentValue::String("second".to_owned()),
        deleted: false,
        commit_ulid: "e12-repl-second".to_owned(),
        hlc: 11,
        origin: "sfo".to_owned(),
        origin_sequence: 1,
        context: Vec::new(),
    };
    // These are two independent source transactions, each with one mutation.
    let publication = |record: ReplicatedRecord| ReplicatedPublication {
        origin: record.origin.clone(),
        origin_sequence: record.origin_sequence,
        hlc: record.hlc,
        commit_ulid: record.commit_ulid.clone(),
        context: record.context.clone(),
        records: vec![record],
        private_payload: Vec::new(),
    };
    let first = publication(first);
    let second = publication(second);
    let dirs: Vec<_> = ["a", "b", "c"]
        .into_iter()
        .map(|suffix| {
            let dir = std::env::temp_dir()
                .join(format!("terrabase-e12-replication-{suffix}-{}", std::process::id()));
            std::fs::remove_dir_all(&dir).ok();
            dir
        })
        .collect();
    let mut engines: Vec<_> = dirs
        .iter()
        .map(|dir| Engine::open(EngineConfig::durable(dir).expect("durable location")))
        .collect::<Result<_, _>>()
        .expect("engines open");
    for (index, engine) in engines.iter().enumerate() {
        let scope = engine.scope_for(&tenant);
        if index == 0 {
            engine.apply_replicated_publication(&scope, &first).expect("older remote row applies");
            engine.apply_replicated_publication(&scope, &second).expect("newer remote row applies");
        } else {
            engine.apply_replicated_publication(&scope, &second).expect("newer remote row applies");
            engine
                .apply_replicated_publication(&scope, &first)
                .expect("older remote row is a deterministic no-op");
        }
        let replay = engine
            .apply_replicated_publication(&scope, &second)
            .expect("duplicate remote row replays");
        assert!(replay.replayed(), "same source identity must be idempotent");
        let snapshot = engine.read_snapshot(&scope).expect("snapshot");
        let documents = engine
            .snapshot_documents_in_collection(&scope, &snapshot, &collection)
            .expect("documents");
        assert_eq!(documents.len(), 1);
        assert_eq!(documents[0].value(), &DocumentValue::String("second".to_owned()));
    }
    for engine in engines.drain(..) {
        engine.shutdown().expect("shutdown");
    }
    for dir in dirs {
        std::fs::remove_dir_all(dir).ok();
    }
}

/// This is the explicit fail-closed seam gate for the real E-12 run.
///
/// E-5 and E-6 have not published adapters implementing the simulation
/// contract. Keeping this ignored makes that absence visible without claiming
/// that the reference model qualifies production replication or sync.
#[test]
#[ignore = "E-5/E-6 public replication and sync seams are absent"]
fn production_replication_and_sync_seed_batch() {
    panic!("E-5/E-6 public replication and sync seams are absent");
}
