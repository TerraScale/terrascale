use super::shard_split::{RouteCatalog, RouteCatalogError, decode_catalog, encode_catalog};
use crate::shard_split::{
    CutoverContext, DurableSplitState, RouteTable, ShardRoute, ShardSplit, SplitContext,
    SplitEngineAdapter, SplitPhase, SplitStateStore, WriterFence,
};

#[test]
fn primary_file_identity_is_persisted_with_first_source_publication() {
    let (dir, engine, tenant) = super::tests::open_owned_engine("primary-lane");
    let scope = engine.scope_for(&tenant);
    let mut tx = engine
        .begin(
            &scope,
            engine.read_snapshot(&scope).unwrap(),
            super::tests::operation("first-lane-write"),
        )
        .unwrap();
    tx.put(
        crate::types::Document::new(
            crate::types::DocumentId::try_new("one").unwrap(),
            crate::types::CollectionName::try_new("docs").unwrap(),
            crate::types::DocumentValue::String("value".into()),
        ),
        crate::types::ExpectedVersion::Any,
    )
    .unwrap();
    assert!(matches!(
        tx.commit(crate::types::LocalDurability::Synced),
        super::CommitOutcome::Committed(_)
    ));
    let identity = engine.file_shard_identity(&scope).unwrap().expect("primary identity");
    assert_eq!(identity.shard_id, 1);
    assert_eq!(identity.origin_lane_incarnation, engine.replication_lane_origin().incarnation());
    drop(engine);
    std::fs::remove_dir_all(dir).unwrap();
}

#[test]
fn encrypted_target_creation_is_durable_and_exactly_idempotent() {
    let (dir, source, tenant) = super::tests::open_owned_engine("split-target-lifecycle");
    let scope = source.scope_for(&tenant);
    let source = std::sync::Arc::new(source);
    let incarnation = u128::from_be_bytes(source.database_id().as_bytes().to_owned());
    let context = SplitContext::new(7, incarnation, 11, 12, 3, 0);
    let mut split = ShardSplit::plan_with_context(Some(b"a".to_vec()), b"m".to_vec(), context)
        .expect("split plan");
    split.begin_backfill().expect("backfill");
    let mut adapter = super::shard_split::ScopedShardEngine::new(source.clone(), scope).unwrap();
    adapter.save(&split.durable_state()).expect("durable split state");
    adapter.create_split_target(&context).expect("first target create");
    adapter.create_split_target(&context).expect("idempotent target create");
    let target_path = dir.join("shards").join("000000000000000c.sqlite3");
    assert!(target_path.is_dir(), "target is an Engine durable location");
    drop(adapter);
    drop(source);
    let _ = std::fs::remove_dir_all(dir);
}

#[test]
fn catalog_codec_round_trip_and_rejects_bad_inputs() {
    let table = RouteTable::new(vec![
        ShardRoute::new(1, None, Some(b"m".to_vec())).unwrap(),
        ShardRoute::new(2, Some(b"m".to_vec()), None).unwrap(),
    ])
    .unwrap();
    let catalog = RouteCatalog { table: table.with_generation(7), generation: 7 };
    let encoded = encode_catalog(&catalog).unwrap();
    assert_eq!(decode_catalog(&encoded).unwrap(), catalog);
    for bad in [vec![], vec![2], {
        let mut value = encoded.clone();
        value.push(0);
        value
    }] {
        assert_eq!(decode_catalog(&bad), Err(RouteCatalogError::Malformed));
    }
    let mut duplicate = encoded.clone();
    duplicate[30..38].copy_from_slice(&1_u64.to_be_bytes());
    assert_eq!(decode_catalog(&duplicate), Err(RouteCatalogError::Malformed));
}

#[test]
fn split_cutover_refuses_drain_ahead_of_target_without_changing_route() {
    let (dir, engine) = super::tests::open_engine("split-cutover-watermark");
    let scope = super::tests::tenant_scope(&engine, "split-cutover-watermark");
    let incarnation = u128::from_be_bytes(engine.database_id().as_bytes().to_owned());
    let initial = RouteCatalog { table: RouteTable::single(1), generation: 0 };
    let initial_bytes = encode_catalog(&initial).unwrap();
    engine
        .install_shard_route_catalog(
            &scope,
            incarnation,
            None,
            &initial_bytes,
            &DurableSplitState::new(None, b"m".to_vec(), 0, 0, 0, SplitPhase::Planned),
            super::tests::operation("split-route-init"),
        )
        .unwrap();

    let context = SplitContext::new(1, incarnation, 1, 2, 1, 0);
    let mut split = ShardSplit::plan_with_context(None, b"m".to_vec(), context).unwrap();
    split.begin_backfill().unwrap();
    split.begin_catch_up(1).unwrap();
    split.note_source_high_water(1);
    split.note_acked(1);
    split.note_target_applied(1);
    let mut fence = WriterFence::new();
    fence.begin_drain(5).unwrap();
    let permit = fence.try_fence(5).unwrap();
    let cutover =
        CutoverContext { split: context, route_generation: 0, drained_through: 5, permit };

    assert_eq!(
        engine.install_shard_split_route(
            &scope,
            &cutover,
            &split.durable_state(),
            super::tests::operation("split-route-stale-target"),
        ),
        Err(RouteCatalogError::Conflict)
    );
    assert_eq!(engine.load_shard_router(&scope).unwrap().unwrap().0.route(b"a"), Some(1));

    split.note_target_applied(5);
    let generation = engine
        .install_shard_split_route(
            &scope,
            &cutover,
            &split.durable_state(),
            super::tests::operation("split-route-caught-up"),
        )
        .unwrap();
    assert_eq!(generation, 1);
    assert_eq!(engine.load_shard_router(&scope).unwrap().unwrap().0.route(b"a"), Some(2));
    assert_eq!(engine.load_shard_router(&scope).unwrap().unwrap().0.route(b"z"), Some(1));
    drop(engine);
    let _ = std::fs::remove_dir_all(dir);
}

#[test]
fn supervisor_resumes_committed_route_and_retries_frontier_publication() {
    use super::shard_supervisor::{ShardSplitSupervisor, SplitStep, SplitSupervisorError};
    use crate::shard_split::{ShardRouter, SplitOrchestratorAdapter, SplitRuntimeError};
    use std::sync::{
        Arc,
        atomic::{AtomicUsize, Ordering},
    };

    #[derive(Clone)]
    struct Orchestrator(Arc<AtomicUsize>);
    impl SplitOrchestratorAdapter for Orchestrator {
        type Error = ();
        fn validate_split_placement(&mut self, _: u128, _: u64) -> Result<(), ()> {
            Ok(())
        }
        fn publish_split_frontier(&mut self, _: &SplitContext, generation: u64) -> Result<(), ()> {
            assert_eq!(generation, 1);
            if self.0.fetch_add(1, Ordering::SeqCst) == 0 { Err(()) } else { Ok(()) }
        }
    }

    let (dir, engine, tenant) = super::tests::open_owned_engine("split-resume-publication");
    let engine = Arc::new(engine);
    let scope = engine.scope_for(&tenant);
    let incarnation = u128::from_be_bytes(engine.database_id().as_bytes().to_owned());
    let context = SplitContext::new(81, incarnation, 1, 2, 1, 0);
    let mut split = ShardSplit::plan_with_context(None, b"m".to_vec(), context).unwrap();
    engine
        .install_shard_route_catalog(
            &scope,
            incarnation,
            None,
            &encode_catalog(&RouteCatalog { table: RouteTable::single(1), generation: 0 }).unwrap(),
            &split.durable_state(),
            super::tests::operation("resume-route-init"),
        )
        .unwrap();
    split.begin_backfill().unwrap();
    let mut adapter =
        super::shard_split::ScopedShardEngine::new(engine.clone(), scope.clone()).unwrap();
    adapter.save(&split.durable_state()).unwrap();
    adapter.create_split_target(&context).unwrap();
    let through = engine.published_sequence().unwrap().get();
    split.begin_catch_up(through).unwrap();
    split.note_target_applied(through);
    split.note_acked(through);
    let mut fence = WriterFence::new();
    fence.begin_drain(through).unwrap();
    let permit = fence.try_fence(through).unwrap();
    adapter.prepare_split_verification(&context, through).unwrap();
    adapter.prepare_split_verification(&context, through).unwrap();
    engine
        .install_shard_split_route(
            &scope,
            &CutoverContext {
                split: context,
                route_generation: 0,
                drained_through: through,
                permit,
            },
            &split.durable_state(),
            super::tests::operation("resume-route-cutover"),
        )
        .unwrap();
    // A target write after cutover must not alter the retained verification
    // snapshot, including when completion runs after a restart.
    {
        let target = adapter.open_existing_target(&context).unwrap();
        let target_scope = target.scope_for(&tenant);
        let mut tx = target
            .begin(
                &target_scope,
                target.read_snapshot(&target_scope).unwrap(),
                super::tests::operation("post-cutover-target-write"),
            )
            .unwrap();
        tx.internal_maintenance = true;
        tx.put(
            crate::types::Document::new(
                crate::types::DocumentId::try_new("a").unwrap(),
                crate::types::CollectionName::try_new("docs").unwrap(),
                crate::types::DocumentValue::Integer(1),
            ),
            crate::types::ExpectedVersion::Any,
        )
        .unwrap();
        assert!(matches!(
            tx.commit(crate::types::LocalDurability::Synced),
            super::CommitOutcome::Committed(_)
        ));
    }
    // The atomic route commit persisted CutOver. Simulate losing the process
    // before any frontier publication or completion work ran.
    assert_eq!(adapter.load().unwrap().unwrap().phase(), SplitPhase::CutOver);
    assert_eq!(adapter.load().unwrap().unwrap().source_high_water(), through);
    let publications = Arc::new(AtomicUsize::new(0));
    let orchestrator = Orchestrator(publications.clone());
    let mut supervisor = ShardSplitSupervisor::resume(
        engine.clone(),
        scope.clone(),
        orchestrator.clone(),
        ShardRouter::single(1),
    )
    .unwrap()
    .unwrap();
    assert!(matches!(
        supervisor.step(1, 1),
        Err(SplitSupervisorError::Runtime(SplitRuntimeError::Publication(())))
    ));
    assert_eq!(adapter.load().unwrap().unwrap().phase(), SplitPhase::CutOver);
    assert_eq!(supervisor.step(1, 1), Ok(SplitStep::Complete { generation: 1 }));
    assert_eq!(adapter.load().unwrap().unwrap().phase(), SplitPhase::Complete);
    assert_eq!(publications.load(Ordering::SeqCst), 2);
    drop(supervisor);
    // Complete is persisted before source cleanup. Restart must replay the
    // finalization path, rather than silently declaring the worker finished.
    let mut supervisor =
        ShardSplitSupervisor::resume(engine.clone(), scope, orchestrator, ShardRouter::single(1))
            .unwrap()
            .unwrap();
    assert_eq!(supervisor.step(1, 1), Ok(SplitStep::Complete { generation: 1 }));
    assert_eq!(publications.load(Ordering::SeqCst), 3);
    drop(supervisor);
    drop(adapter);
    drop(engine);
    let _ = std::fs::remove_dir_all(dir);
}

#[test]
fn split_verification_rejects_empty_or_missing_target_without_recreating_it() {
    use crate::{
        engine::{CommitOutcome, EngineError},
        shard_split::canonical_shard_key,
        types::{
            CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion, LocalDurability,
        },
    };
    let (dir, engine, tenant) = super::tests::open_owned_engine("split-verify-data");
    let engine = std::sync::Arc::new(engine);
    let scope = engine.scope_for(&tenant);
    let collection = CollectionName::try_new("docs").unwrap();
    let mut tx = engine
        .begin(
            &scope,
            engine.read_snapshot(&scope).unwrap(),
            super::tests::operation("verify-source-row"),
        )
        .unwrap();
    tx.put(
        Document::new(
            DocumentId::try_new("a").unwrap(),
            collection.clone(),
            DocumentValue::String("retained".into()),
        ),
        ExpectedVersion::Any,
    )
    .unwrap();
    assert!(matches!(tx.commit(LocalDurability::Synced), CommitOutcome::Committed(_)));
    let incarnation = u128::from_be_bytes(engine.database_id().as_bytes().to_owned());
    let context = SplitContext::new(82, incarnation, 1, 2, 1, 0);
    let upper = canonical_shard_key(&collection, &DocumentId::try_new("m").unwrap());
    let mut split = ShardSplit::plan_with_context(None, upper, context).unwrap();
    split.begin_backfill().unwrap();
    let mut adapter = super::shard_split::ScopedShardEngine::new(engine.clone(), scope).unwrap();
    adapter.save(&split.durable_state()).unwrap();
    adapter.create_split_target(&context).unwrap();
    let through = engine.published_sequence().unwrap().get();
    assert_eq!(adapter.prepare_split_verification(&context, through), Err(EngineError::Conflict));
    split.begin_catch_up(through).unwrap();
    split.note_source_high_water(through);
    split.note_target_applied(through);
    split.cut_over().unwrap();
    adapter.save(&split.durable_state()).unwrap();
    // Durable ownership and a claimed frontier are insufficient: the source
    // contains a document absent from this otherwise valid target.
    assert_eq!(adapter.verify_split_target(&context), Err(EngineError::Conflict));
    assert_eq!(adapter.load().unwrap().unwrap().phase(), SplitPhase::CutOver);
    let path = dir.join("shards").join("0000000000000002.sqlite3");
    std::fs::remove_dir_all(&path).unwrap();
    assert_eq!(adapter.verify_split_target(&context), Err(EngineError::StorageFailure));
    assert!(!path.exists(), "verification must not recreate a lost target");
    drop(adapter);
    drop(engine);
    let _ = std::fs::remove_dir_all(dir);
}

#[test]
fn supervisor_finishes_without_chasing_its_own_checkpoint_publications() {
    use super::shard_supervisor::{ShardSplitSupervisor, SplitStep};
    use crate::shard_split::{ShardRouter, SplitOrchestratorAdapter};
    struct Orchestrator;
    impl SplitOrchestratorAdapter for Orchestrator {
        type Error = ();
        fn validate_split_placement(&mut self, _: u128, _: u64) -> Result<(), ()> {
            Ok(())
        }
        fn publish_split_frontier(&mut self, _: &SplitContext, _: u64) -> Result<(), ()> {
            Ok(())
        }
    }
    let (dir, engine, tenant) = super::tests::open_owned_engine("split-checkpoint-tail");
    let engine = std::sync::Arc::new(engine);
    let scope = engine.scope_for(&tenant);
    let incarnation = u128::from_be_bytes(engine.database_id().as_bytes().to_owned());
    let context = SplitContext::new(83, incarnation, 1, 2, 1, 0);
    let split = ShardSplit::plan_with_context(None, b"m".to_vec(), context).unwrap();
    engine
        .install_shard_route_catalog(
            &scope,
            incarnation,
            None,
            &encode_catalog(&RouteCatalog { table: RouteTable::single(1), generation: 0 }).unwrap(),
            &split.durable_state(),
            super::tests::operation("tail-route-init"),
        )
        .unwrap();
    let mut supervisor = ShardSplitSupervisor::start(
        engine.clone(),
        scope,
        split,
        Orchestrator,
        ShardRouter::single(1),
    )
    .unwrap();
    assert_eq!(supervisor.step(256, 1), Ok(SplitStep::Progress));
    let mut complete = false;
    for _ in 0..16 {
        if supervisor.step(256, 1).unwrap() == (SplitStep::Complete { generation: 1 }) {
            complete = true;
            break;
        }
    }
    assert!(complete, "a one-group page must drain a quiescent source");
    drop(supervisor);
    drop(engine);
    let _ = std::fs::remove_dir_all(dir);
}

#[test]
fn supervisor_restart_after_snapshot_replays_original_history_from_zero() {
    use super::shard_supervisor::{ShardSplitSupervisor, SplitStep};
    use crate::{
        engine::{CommitOutcome, Engine, EngineConfig},
        shard_split::{ShardRouter, SplitOrchestratorAdapter},
        types::{
            CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion, LocalDurability,
        },
    };
    use std::sync::Arc;
    struct Orchestrator;
    impl SplitOrchestratorAdapter for Orchestrator {
        type Error = ();
        fn validate_split_placement(&mut self, _: u128, _: u64) -> Result<(), ()> {
            Ok(())
        }
        fn publish_split_frontier(&mut self, _: &SplitContext, _: u64) -> Result<(), ()> {
            Ok(())
        }
    }

    let (dir, source, tenant) = super::tests::open_owned_engine("split-snapshot-cursor-restart");
    let owner = source.database_owner().clone();
    let keys = source.storage_keys.clone().unwrap();
    let source = Arc::new(source);
    let scope = source.scope_for(&tenant);
    let collection = CollectionName::try_new("docs").unwrap();
    let id = DocumentId::try_new("a").unwrap();
    let mut tx = source
        .begin(
            &scope,
            source.read_snapshot(&scope).unwrap(),
            super::tests::operation("before-snapshot-put"),
        )
        .unwrap();
    tx.put(
        Document::new(id.clone(), collection.clone(), DocumentValue::Integer(1)),
        ExpectedVersion::Any,
    )
    .unwrap();
    assert!(matches!(tx.commit(LocalDurability::Synced), CommitOutcome::Committed(_)));
    let mut tx = source
        .begin(
            &scope,
            source.read_snapshot(&scope).unwrap(),
            super::tests::operation("before-snapshot-delete"),
        )
        .unwrap();
    tx.delete_in_collection(&collection, id, ExpectedVersion::Any);
    assert!(matches!(tx.commit(LocalDurability::Synced), CommitOutcome::Committed(_)));
    let originals = source.replication_publications(&scope, 0, 16).unwrap();
    assert_eq!(originals.len(), 2);
    assert!(originals[1].1.records[0].deleted);

    let incarnation = u128::from_be_bytes(source.database_id().as_bytes().to_owned());
    let context = SplitContext::new(84, incarnation, 1, 2, 1, 0);
    let split = ShardSplit::plan_with_context(None, b"m".to_vec(), context).unwrap();
    source
        .install_shard_route_catalog(
            &scope,
            incarnation,
            None,
            &encode_catalog(&RouteCatalog { table: RouteTable::single(1), generation: 0 }).unwrap(),
            &split.durable_state(),
            super::tests::operation("snapshot-cursor-route"),
        )
        .unwrap();
    let mut supervisor = ShardSplitSupervisor::start(
        source.clone(),
        scope.clone(),
        split,
        Orchestrator,
        ShardRouter::single(1),
    )
    .unwrap();
    // The final-state snapshot is empty, but the original put/delete causal
    // history must still reach the target after this crash boundary.
    assert_eq!(supervisor.step(256, 1), Ok(SplitStep::Progress));
    let adapter = super::shard_split::ScopedShardEngine::new(source.clone(), scope).unwrap();
    let state = adapter.load().unwrap().unwrap();
    assert_eq!(state.phase(), SplitPhase::CatchingUp);
    assert_eq!(state.target_applied_through(), 0);
    drop(adapter);
    drop(supervisor);
    drop(source);

    let source = Arc::new(
        Engine::open(
            EngineConfig::existing_durable(&dir).unwrap().with_owner(owner).with_storage_keys(keys),
        )
        .unwrap(),
    );
    let scope = source.scope_for(&tenant);
    let mut supervisor = ShardSplitSupervisor::resume(
        source.clone(),
        scope.clone(),
        Orchestrator,
        ShardRouter::single(1),
    )
    .unwrap()
    .unwrap();
    let mut completed = false;
    for _ in 0..16 {
        if supervisor.step(256, 1).unwrap() == (SplitStep::Complete { generation: 1 }) {
            completed = true;
            break;
        }
    }
    assert!(completed);
    let adapter = super::shard_split::ScopedShardEngine::new(source.clone(), scope).unwrap();
    let target = adapter.open_existing_target(&context).unwrap();
    let target_scope = target.scope_for(&tenant);
    let copied = target.replication_publications(&target_scope, 0, 16).unwrap();
    for (_, original) in originals {
        assert!(
            copied.iter().any(|(_, publication)| publication == &original),
            "the original operation identity and causal tombstone must survive restart"
        );
    }
    drop(target);
    drop(adapter);
    drop(supervisor);
    drop(source);
    let _ = std::fs::remove_dir_all(dir);
}

#[test]
fn shard_admission_matches_collection_and_id_in_canonical_keyspace() {
    use crate::{
        shard_split::{ShardAdmissionToken, canonical_shard_key},
        storage::StagedWrite,
        types::{CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion},
    };
    let (dir, engine) = super::tests::open_engine("split-canonical-admission");
    let scope = super::tests::tenant_scope(&engine, "split-canonical-admission");
    let incarnation = u128::from_be_bytes(engine.database_id().as_bytes().to_owned());
    engine
        .install_shard_route_catalog(
            &scope,
            incarnation,
            None,
            &encode_catalog(&RouteCatalog { table: RouteTable::single(1), generation: 0 }).unwrap(),
            &DurableSplitState::new(None, b"m".to_vec(), 0, 0, 0, SplitPhase::Planned),
            super::tests::operation("canonical-route-init"),
        )
        .unwrap();
    let collection = CollectionName::try_new("docs").unwrap();
    let other = CollectionName::try_new("other").unwrap();
    let id = DocumentId::try_new("same-id").unwrap();
    let key = canonical_shard_key(&collection, &id);
    let token = ShardAdmissionToken::for_key(incarnation, 0, 1, &key);
    let put = |collection| StagedWrite::Put {
        document: Document::new(id.clone(), collection, DocumentValue::Integer(1)),
        expected: ExpectedVersion::Any,
    };
    assert_eq!(engine.validate_shard_admission(&scope, &token, &[put(collection.clone())]), Ok(()));
    assert_eq!(
        engine.validate_shard_admission(&scope, &token, &[put(other)]),
        Err(super::EngineError::Conflict)
    );
    let raw_token = ShardAdmissionToken::for_key(incarnation, 0, 1, id.as_str().as_bytes());
    assert_eq!(
        engine.validate_shard_admission(&scope, &raw_token, &[put(collection.clone())]),
        Err(super::EngineError::Conflict)
    );
    let delete = |collection| StagedWrite::Delete {
        collection,
        id: id.clone(),
        expected: ExpectedVersion::Any,
        ttl_guard: None,
    };
    assert_eq!(
        engine.validate_shard_admission(&scope, &token, &[delete(Some(collection))]),
        Ok(())
    );
    assert_eq!(
        engine.validate_shard_admission(&scope, &token, &[delete(None)]),
        Err(super::EngineError::Conflict)
    );
    drop(engine);
    let _ = std::fs::remove_dir_all(dir);
}

#[test]
fn staging_copy_preserves_version_and_rejects_changed_same_provenance() {
    use super::{CommitOutcome, LocalDurability};
    use crate::shard_split::{CopyBatch, CopyEntry, RangeCopyTarget, canonical_shard_key};
    use crate::types::{CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion};
    let (dir, source, tenant) = super::tests::open_owned_engine("split-copy-version");
    let scope = source.scope_for(&tenant);
    let source = std::sync::Arc::new(source);
    let incarnation = u128::from_be_bytes(source.database_id().as_bytes());
    let collection = CollectionName::try_new("records").unwrap();
    let id = DocumentId::try_new("a").unwrap();
    for index in 0..2 {
        let mut tx = source
            .begin(
                &scope,
                source.read_snapshot(&scope).unwrap(),
                super::tests::operation(&format!("version-{index}")),
            )
            .unwrap();
        tx.put(
            Document::new(id.clone(), collection.clone(), DocumentValue::Integer(index)),
            ExpectedVersion::Any,
        )
        .unwrap();
        assert!(matches!(tx.commit(LocalDurability::Synced), CommitOutcome::Committed(_)));
    }
    let tx = source
        .begin(&scope, source.read_snapshot(&scope).unwrap(), super::tests::operation("read-copy"))
        .unwrap();
    let document = tx.read_in_collection(&collection, &id).unwrap().unwrap();
    assert_eq!(document.metadata().version(), 2);
    drop(tx);
    let context = SplitContext::new(73, incarnation, 11, 12, 3, 0);
    let split = ShardSplit::plan_with_context(
        None,
        canonical_shard_key(&collection, &DocumentId::try_new("m").unwrap()),
        context,
    )
    .unwrap();
    let mut adapter =
        super::shard_split::ScopedShardEngine::new(source.clone(), scope.clone()).unwrap();
    adapter.save(&split.durable_state()).unwrap();
    adapter.create_split_target(&context).unwrap();
    let target = adapter.open_existing_target(&context).unwrap();
    let target_scope = target.scope_for(&tenant);
    let mut copier = super::shard_split::ScopedShardRangeTarget::open(target.clone(), &scope);
    let key = canonical_shard_key(&collection, &id);
    let body = crate::storage::encode_document_body(&document);
    let batch = CopyBatch::new(
        vec![CopyEntry::new(key.clone(), body)],
        source.published_sequence().unwrap().get(),
        true,
    );
    copier.apply_copy_batch(&batch).unwrap();
    let first = target.published_sequence().unwrap();
    copier.apply_copy_batch(&batch).unwrap();
    assert_eq!(target.published_sequence().unwrap(), first);
    let tx = target
        .begin(
            &target_scope,
            target.read_snapshot(&target_scope).unwrap(),
            super::tests::operation("read-target-copy"),
        )
        .unwrap();
    let copied_document = tx.read_in_collection(&collection, &id).unwrap().unwrap();
    assert_eq!(copied_document, document);
    drop(tx);
    let mut changed = Document::new(id, collection, DocumentValue::Integer(999));
    changed.set_version(2);
    let changed = CopyBatch::new(
        vec![CopyEntry::new(key, crate::storage::encode_document_body(&changed))],
        first.get(),
        true,
    );
    assert!(copier.apply_copy_batch(&changed).is_err());
    assert_eq!(target.published_sequence().unwrap(), first);
    drop(copier);
    drop(target);
    drop(adapter);
    drop(source);
    std::fs::remove_dir_all(dir).unwrap();
}
