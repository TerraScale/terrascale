use super::*;
use crate::{
    engine::tests::{open_engine, operation, tenant_scope},
    shard_split::{ShardRouter, WriterFence},
    types::{CollectionName, Document, DocumentId},
};

#[test]
fn source_file_rejects_moved_and_cross_collection_tokens_after_cutover() {
    let (dir, engine) = open_engine("shard-file-admission");
    let scope = tenant_scope(&engine, "shard-file-admission");
    let incarnation = u128::from_be_bytes(engine.database_id().as_bytes());
    let collection = CollectionName::try_new("records").unwrap();
    let other = CollectionName::try_new("other").unwrap();
    let id = DocumentId::try_new("a").unwrap();
    let upper = canonical_shard_key(&collection, &DocumentId::try_new("m").unwrap());
    let context = SplitContext::new(41, incarnation, 11, 12, 1, 0);
    let mut split = ShardSplit::plan_with_context(None, upper, context).unwrap();
    engine
        .install_shard_route_catalog(
            &scope,
            incarnation,
            None,
            &encode_catalog(&RouteCatalog { table: RouteTable::single(11), generation: 0 })
                .unwrap(),
            &split.durable_state(),
            operation("admission-initialize"),
        )
        .unwrap();
    let key = canonical_shard_key(&collection, &id);
    let router = ShardRouter::single(11);
    assert!(engine.shard_admission_for_key(&scope, &router, id.as_str().as_bytes()).is_err());
    let token = engine.shard_admission_for_key(&scope, &router, &key).unwrap();
    let mut tx = engine
        .begin(&scope, engine.read_snapshot(&scope).unwrap(), operation("wrong-collection"))
        .unwrap();
    tx.bind_shard_admission(token.clone()).unwrap();
    tx.put(Document::new(id.clone(), other, DocumentValue::Integer(1)), ExpectedVersion::New)
        .unwrap();
    assert!(matches!(
        tx.commit(LocalDurability::Synced),
        CommitOutcome::Rejected(CommitError::Conflict)
    ));

    let mut tx = engine
        .begin(&scope, engine.read_snapshot(&scope).unwrap(), operation("correct-collection"))
        .unwrap();
    tx.bind_document_shard_admission(&collection, &id).unwrap();
    tx.put(
        Document::new(id.clone(), collection.clone(), DocumentValue::Integer(1)),
        ExpectedVersion::New,
    )
    .unwrap();
    assert!(matches!(tx.commit(LocalDurability::Synced), CommitOutcome::Committed(_)));

    let mut in_flight = engine
        .begin(&scope, engine.read_snapshot(&scope).unwrap(), operation("pre-cutover-in-flight"))
        .unwrap();
    in_flight.bind_document_shard_admission(&collection, &id).unwrap();
    in_flight
        .put(
            Document::new(id.clone(), collection.clone(), DocumentValue::Integer(99)),
            ExpectedVersion::Any,
        )
        .unwrap();

    let through = engine.published_sequence().unwrap().get();
    split.begin_backfill().unwrap();
    split.begin_catch_up(through).unwrap();
    split.note_source_high_water(through);
    split.note_acked(through);
    split.note_target_applied(through);
    let mut fence = WriterFence::new();
    fence.begin_drain(through).unwrap();
    let permit = fence.try_fence(through).unwrap();
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
            operation("admission-cutover"),
        )
        .unwrap();
    assert!(
        matches!(
            in_flight.commit(LocalDurability::Synced),
            CommitOutcome::Rejected(CommitError::Conflict)
        ),
        "a transaction staged before cutover cannot publish on the former owner"
    );
    let (router, _) = engine.load_shard_router(&scope).unwrap().unwrap();
    assert_eq!(router.route(&key), Some(12));
    assert!(engine.shard_admission_for_key(&scope, &router, &key).is_err());
    assert!(engine.validate_document_shard_route(&scope, &collection, &id).is_err());
    for (name, token) in [
        ("stale-source", token),
        ("forged-target", ShardAdmissionToken::for_key(incarnation, 1, 12, &key)),
    ] {
        let mut tx =
            engine.begin(&scope, engine.read_snapshot(&scope).unwrap(), operation(name)).unwrap();
        tx.bind_shard_admission(token).unwrap();
        tx.put(
            Document::new(id.clone(), collection.clone(), DocumentValue::Integer(2)),
            ExpectedVersion::Any,
        )
        .unwrap();
        assert!(matches!(
            tx.commit(LocalDurability::Synced),
            CommitOutcome::Rejected(CommitError::Conflict)
        ));
    }
    let retained = DocumentId::try_new("z").unwrap();
    let mut tx = engine
        .begin(&scope, engine.read_snapshot(&scope).unwrap(), operation("retained-source"))
        .unwrap();
    tx.bind_document_shard_admission(&collection, &retained).unwrap();
    tx.put(Document::new(retained, collection, DocumentValue::Integer(3)), ExpectedVersion::New)
        .unwrap();
    assert!(matches!(tx.commit(LocalDurability::Synced), CommitOutcome::Committed(_)));
    drop(engine);
    let _ = std::fs::remove_dir_all(dir);
}
