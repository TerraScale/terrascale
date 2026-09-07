use super::shard_split::{RouteCatalog, RouteCatalogError, decode_catalog, encode_catalog};
use crate::shard_split::{
    CutoverContext, DurableSplitState, RouteTable, ShardRoute, ShardSplit, SplitContext,
    SplitEngineAdapter, SplitPhase, SplitStateStore, WriterFence,
};

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
