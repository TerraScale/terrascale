//! Regression tests for resident encrypted targets and shared commit authority.
use super::shard_split::{RouteCatalog, ScopedShardEngine, encode_catalog};
use super::{CommitError, CommitOutcome, Engine, LocalDurability};
use crate::shard_split::{
    CutoverContext, RouteTable, ShardRoute, ShardSplit, SplitContext, SplitEngineAdapter,
    SplitStateStore, WriterFence, canonical_shard_key,
};
use crate::types::{
    CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion, TenantId,
};
use std::{path::PathBuf, sync::Arc};

fn admitted_target(tag: &str) -> (PathBuf, Arc<Engine>, Arc<Engine>, TenantId, ShardRoute) {
    let (dir, source, tenant) = super::tests::open_owned_engine(tag);
    let source = Arc::new(source);
    let scope = source.scope_for(&tenant);
    let incarnation = u128::from_be_bytes(source.database_id().as_bytes());
    let collection = CollectionName::try_new("records").unwrap();
    let upper = canonical_shard_key(&collection, &DocumentId::try_new("m").unwrap());
    let context = SplitContext::new(91, incarnation, 11, 12, 1, 0);
    let mut split = ShardSplit::plan_with_context(None, upper.clone(), context).unwrap();
    source
        .install_shard_route_catalog(
            &scope,
            incarnation,
            None,
            &encode_catalog(&RouteCatalog { table: RouteTable::single(11), generation: 0 })
                .unwrap(),
            &split.durable_state(),
            super::tests::operation("initial-route"),
        )
        .unwrap();
    let mut adapter = ScopedShardEngine::new(source.clone(), scope.clone());
    adapter.save(&split.durable_state()).unwrap();
    adapter.create_split_target(&context).unwrap();
    split.begin_backfill().unwrap();
    let through = source.published_sequence().unwrap().get();
    split.begin_catch_up(through).unwrap();
    split.note_source_high_water(through);
    split.note_acked(through);
    split.note_target_applied(through);
    adapter.save(&split.durable_state()).unwrap();
    let through = source.published_sequence().unwrap().get();
    split.note_source_high_water(through);
    split.note_acked(through);
    split.note_target_applied(through);
    adapter.prepare_split_verification(&context, through).unwrap();
    let mut fence = WriterFence::new();
    fence.begin_drain(through).unwrap();
    let permit = fence.try_fence(through).unwrap();
    source
        .install_shard_split_route(
            &scope,
            &CutoverContext {
                split: context,
                route_generation: 0,
                drained_through: through,
                permit,
            },
            &split.durable_state(),
            super::tests::operation("cutover"),
        )
        .unwrap();
    let target = source.open_shard_participant(12).unwrap().unwrap();
    let route = ShardRoute::new(12, None, Some(upper)).unwrap();
    assert!(
        target.validate_shard_target(&target.scope_for(&tenant), incarnation, &route, 1).unwrap()
    );
    (dir, source, target, tenant, route)
}

#[test]
fn encrypted_target_is_resident_and_keeps_live_sync_frontier() {
    let (dir, source, target, tenant, _) = admitted_target("resident-encrypted-target");
    let path = dir.join("shards/000000000000000c.sqlite3");
    assert!(crate::storage::existing_database_location(&path, true));
    let plaintext = crate::storage::database_file(&path);
    assert!(!plaintext.metadata().is_ok_and(|meta| meta.len() > 0));
    target.register_retention_consumer("sync", target.published_sequence().unwrap()).unwrap();
    for _ in 0..32 {
        let again = source.open_shard_participant(12).unwrap().unwrap();
        assert!(Arc::ptr_eq(&target, &again));
        again
            .advance_retention_frontier("sync", target.published_sequence().unwrap().get())
            .unwrap();
    }
    let cut =
        source.routed_read_snapshots(&tenant, Some(1), &[source.clone(), target.clone()]).unwrap();
    assert_eq!(cut.len(), 2);
    assert!(
        source.routed_read_snapshots(&tenant, Some(0), &[source.clone(), target.clone()]).is_err()
    );
    drop(target);
    drop(source);
    std::fs::remove_dir_all(dir).unwrap();
}

fn staged_write<'a>(
    target: &'a Engine,
    tenant: &TenantId,
    operation: &str,
) -> super::Transaction<'a> {
    let scope = target.scope_for(tenant);
    let collection = CollectionName::try_new("records").unwrap();
    let id = DocumentId::try_new("a").unwrap();
    let mut tx = target
        .begin(&scope, target.read_snapshot(&scope).unwrap(), super::tests::operation(operation))
        .unwrap();
    tx.bind_document_shard_admission(&collection, &id).unwrap();
    tx.put(Document::new(id, collection, DocumentValue::Integer(7)), ExpectedVersion::Any).unwrap();
    tx
}

#[test]
fn staged_target_writes_observe_source_pause_revoke_fence_and_placement() {
    for action in ["pause", "revoke", "fence", "placement"] {
        let (dir, source, target, tenant, _) =
            admitted_target(&format!("target-authority-{action}"));
        assert!(matches!(
            staged_write(&target, &tenant, "accepted").commit(LocalDurability::Synced),
            CommitOutcome::Committed(_)
        ));
        let before = target.published_sequence().unwrap();
        let tx = staged_write(&target, &tenant, "staged-before-authority-change");
        let scope = source.scope_for(&tenant);
        match action {
            "pause" => {
                source.lifecycle_pause(&scope, "pause-1").unwrap();
            }
            "revoke" => {
                source.lifecycle_prepare_deletion(&scope, "delete-1").unwrap();
            }
            "fence" => {
                source.fence_origin(1).unwrap();
            }
            "placement" => {
                let owner = source.database_owner();
                source
                    .configure_usage_placement(
                        crate::usage::UsagePlacement::try_new(
                            tenant.clone(),
                            &owner.project().to_string(),
                            &owner.database().to_string(),
                            "main",
                            owner.region(),
                            vec![owner.region()],
                            "placement-1",
                        )
                        .unwrap(),
                    )
                    .unwrap();
            }
            _ => unreachable!(),
        }
        assert!(
            matches!(
                tx.commit(LocalDurability::Synced),
                CommitOutcome::Rejected(CommitError::Conflict)
            ),
            "{action}"
        );
        assert_eq!(
            target.published_sequence().unwrap(),
            before,
            "{action} cannot publish a target write"
        );
        drop(target);
        drop(source);
        std::fs::remove_dir_all(dir).unwrap();
    }
}

#[test]
fn target_admission_rejects_malformed_or_rebound_verification() {
    let (dir, source, target, tenant, route) = admitted_target("target-proof-binding");
    let scope = target.scope_for(&tenant);
    let incarnation = u128::from_be_bytes(source.database_id().as_bytes());
    let key = "control:shard-target/v1/verified-boundary";
    let snapshot = target.read_snapshot(&scope).unwrap();
    let original = target
        .lock_storage()
        .unwrap()
        .port
        .private_metadata_at(scope.tenant(), key, snapshot.sequence())
        .unwrap()
        .unwrap();
    for (index, value) in [
        DocumentValue::String(String::new()),
        DocumentValue::String(hex::encode([0; 55])),
        DocumentValue::String(hex::encode([0; 56])),
        DocumentValue::String(hex::encode([0; 57])),
    ]
    .into_iter()
    .enumerate()
    {
        let mut tx = target
            .begin(
                &scope,
                target.read_snapshot(&scope).unwrap(),
                super::tests::operation(&format!("corrupt-proof-{index}")),
            )
            .unwrap();
        tx.internal_maintenance = true;
        tx.put_private_metadata(key, value).unwrap();
        assert!(matches!(tx.commit(LocalDurability::Synced), CommitOutcome::Committed(_)));
        assert!(!target.validate_shard_target(&scope, incarnation, &route, 1).unwrap());
    }
    let mut tx = target
        .begin(
            &scope,
            target.read_snapshot(&scope).unwrap(),
            super::tests::operation("restore-proof"),
        )
        .unwrap();
    tx.internal_maintenance = true;
    tx.put_private_metadata(key, original).unwrap();
    assert!(matches!(tx.commit(LocalDurability::Synced), CommitOutcome::Committed(_)));
    assert!(target.validate_shard_target(&scope, incarnation, &route, 1).unwrap());
    let catalog_key = format!("control:shard-route/v1/{incarnation:032x}");
    // A valid catalog with a different generation is still not the catalog
    // authenticated by the completed copy proof.
    let catalog = encode_catalog(&RouteCatalog {
        table: RouteTable::new(vec![route.clone()]).unwrap().with_generation(2),
        generation: 2,
    })
    .unwrap();
    let mut tx = target
        .begin(
            &scope,
            target.read_snapshot(&scope).unwrap(),
            super::tests::operation("rebind-catalog"),
        )
        .unwrap();
    tx.internal_maintenance = true;
    tx.put_private_metadata(&catalog_key, DocumentValue::String(hex::encode(catalog))).unwrap();
    assert!(matches!(tx.commit(LocalDurability::Synced), CommitOutcome::Committed(_)));
    assert!(!target.validate_shard_target(&scope, incarnation, &route, 2).unwrap());
    drop(target);
    drop(source);
    std::fs::remove_dir_all(dir).unwrap();
}
