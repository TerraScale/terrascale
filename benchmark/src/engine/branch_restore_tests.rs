use super::*;
use crate::types::{
    CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion, LocalDurability,
    OperationId, TenantId,
};

fn assert_branch_contents(engine: &Engine, tenant: &TenantId, database: &str) {
    assert_eq!(
        engine.schema_metadata_load(database, "feature", "users").unwrap(),
        Some(b"schema-envelope".to_vec())
    );
    let scope = engine.scope_for(tenant);
    let snapshot = engine.read_snapshot(&scope).unwrap();
    let transaction = engine
        .begin(&scope, snapshot, OperationId::try_new("read-branch-helper").unwrap())
        .unwrap();
    assert_eq!(
        transaction
            .read_in_collection(
                &CollectionName::try_new("users").unwrap(),
                &DocumentId::try_new("one").unwrap(),
            )
            .unwrap()
            .unwrap()
            .value(),
        &DocumentValue::String("source-value".into())
    );
}

fn seed_branch_source(source: &Engine, tenant: &TenantId) {
    let scope = source.scope_for(tenant);
    let snapshot = source.read_snapshot(&scope).unwrap();
    let mut transaction =
        source.begin(&scope, snapshot, OperationId::try_new("branch-source").unwrap()).unwrap();
    transaction
        .stage_schema_metadata(
            &source.database_id().to_string(),
            "feature",
            "users",
            None,
            b"schema-envelope",
        )
        .unwrap();
    transaction
        .put(
            Document::new(
                DocumentId::try_new("one").unwrap(),
                CollectionName::try_new("users").unwrap(),
                DocumentValue::String("source-value".into()),
            ),
            ExpectedVersion::New,
        )
        .unwrap();
    assert!(matches!(transaction.commit(LocalDurability::Synced), CommitOutcome::Committed(_)));
}

#[test]
fn encrypted_branch_restore_rebinds_owner_and_replays_idempotently() {
    let root = std::env::temp_dir()
        .join(format!("terrabase-branch-restore-{}", crate::identity::generate()));
    let source_path = root.join("source");
    let target_path = root.join("target");
    let source_owner = crate::storage::DatabaseOwner::try_new(
        "branch-test",
        crate::identity::TenantUlid::generate(),
        crate::identity::ProjectUlid::generate(),
        crate::identity::DatabaseUlid::generate(),
        crate::RegionCode::GRU,
    )
    .unwrap();
    let project = source_owner.project();
    let source_keys = test_storage_keys(&source_owner, 141);
    let source = Engine::open(
        EngineConfig::durable(&source_path)
            .unwrap()
            .with_owner(source_owner.clone())
            .with_storage_keys(source_keys),
    )
    .unwrap();
    source.activate_origin_epoch(1).unwrap();
    let tenant = TenantId::try_new(&source_owner.tenant().to_string()).unwrap();
    seed_branch_source(&source, &tenant);

    let child_owner = crate::storage::DatabaseOwner::new(
        source_owner.environment().clone(),
        source_owner.tenant(),
        project,
        crate::identity::DatabaseUlid::generate(),
        source_owner.region(),
    );
    let child_keys = test_storage_keys(&child_owner, 142);
    let child_config = EngineConfig::durable(&target_path)
        .unwrap()
        .with_owner(child_owner.clone())
        .with_storage_keys(child_keys);
    let operation = OperationId::try_new("restore-branch").unwrap();
    let (same_first, same_second) = std::thread::scope(|threads| {
        let first_config = child_config.clone();
        let second_config = child_config.clone();
        let first = threads.spawn(move || {
            Engine::restore_encrypted_sqlite_branch(&source, &first_config, &operation)
        });
        let second = threads.spawn(move || {
            Engine::restore_encrypted_sqlite_branch(&source, &second_config, &operation)
        });
        (first.join().unwrap(), second.join().unwrap())
    });
    let same_first = same_first.unwrap();
    let same_second = same_second.unwrap();
    assert_eq!(same_first, same_second);
    assert!(
        std::fs::read_dir(&root)
            .unwrap()
            .flatten()
            .all(|entry| !entry.file_name().to_string_lossy().starts_with(".branch-restore-"))
    );
    let cut = Engine::restore_encrypted_sqlite_branch(&source, &child_config, &operation).unwrap();
    assert_eq!(cut, same_first);
    assert!(cut.get() > 0);
    let retry =
        Engine::restore_encrypted_sqlite_branch(&source, &child_config, &operation).unwrap();
    assert_eq!(retry, cut);
    let child = Engine::open(child_config).unwrap();
    assert_eq!(child.database_id(), child_owner.database());
    assert_branch_contents(&child, &tenant, &child_owner.database().to_string());

    let distinct_target = root.join("distinct-target");
    let distinct_config = EngineConfig::durable(&distinct_target)
        .unwrap()
        .with_owner(child_owner.clone())
        .with_storage_keys(test_storage_keys(&child_owner, 146));
    let first_operation = OperationId::try_new("distinct-first").unwrap();
    let second_operation = OperationId::try_new("distinct-second").unwrap();
    let (distinct_first, distinct_second) = std::thread::scope(|threads| {
        let first_config = distinct_config.clone();
        let second_config = distinct_config.clone();
        let first = threads.spawn(move || {
            Engine::restore_encrypted_sqlite_branch(&source, &first_config, &first_operation)
        });
        let second = threads.spawn(move || {
            Engine::restore_encrypted_sqlite_branch(&source, &second_config, &second_operation)
        });
        (first.join().unwrap(), second.join().unwrap())
    });
    assert_ne!(distinct_first.is_ok(), distinct_second.is_ok());
    assert!(
        std::fs::read_dir(&root)
            .unwrap()
            .flatten()
            .all(|entry| !entry.file_name().to_string_lossy().starts_with(".branch-restore-"))
    );

    let grandchild_owner = crate::storage::DatabaseOwner::new(
        child_owner.environment().clone(),
        child_owner.tenant(),
        child_owner.project(),
        crate::identity::DatabaseUlid::generate(),
        child_owner.region(),
    );
    let grandchild_path = root.join("grandchild");
    let grandchild_config = EngineConfig::durable(&grandchild_path)
        .unwrap()
        .with_owner(grandchild_owner.clone())
        .with_storage_keys(test_storage_keys(&grandchild_owner, 143));
    let grandchild_operation = OperationId::try_new("restore-grandchild").unwrap();
    let grandchild_cut =
        Engine::restore_encrypted_sqlite_branch(&child, &grandchild_config, &grandchild_operation)
            .unwrap();
    assert_eq!(
        Engine::restore_encrypted_sqlite_branch(&child, &grandchild_config, &grandchild_operation,)
            .unwrap(),
        grandchild_cut
    );
    let grandchild = Engine::open(grandchild_config).unwrap();
    assert_eq!(grandchild.database_id(), grandchild_owner.database());
    assert_branch_contents(&grandchild, &tenant, &grandchild_owner.database().to_string());
    drop(grandchild);
    drop(child);
    drop(source);
    std::fs::remove_dir_all(root).unwrap();
}

#[test]
fn encrypted_branch_restore_rejects_plaintext_source() {
    let root = std::env::temp_dir()
        .join(format!("terrabase-branch-plaintext-{}", crate::identity::generate()));
    let source_owner = crate::storage::DatabaseOwner::local_development();
    let source = root.join("source");
    std::fs::create_dir_all(&source).unwrap();
    std::fs::write(source.join(crate::storage::database_file(&source)), b"plaintext").unwrap();
    let child_owner = crate::storage::DatabaseOwner::new(
        source_owner.environment().clone(),
        source_owner.tenant(),
        source_owner.project(),
        crate::identity::DatabaseUlid::generate(),
        source_owner.region(),
    );
    let target = root.join("target");
    let config = EngineConfig::durable(&target)
        .unwrap()
        .with_owner(child_owner.clone())
        .with_storage_keys(test_storage_keys(&child_owner, 143));
    assert!(Engine::open(config).is_err());
    std::fs::remove_dir_all(root).unwrap();
}

#[test]
fn encrypted_branch_rejects_wrong_owner_keys_before_reading_source() {
    let root = std::env::temp_dir()
        .join(format!("terrabase-branch-key-scope-{}", crate::identity::generate()));
    let source_owner = crate::storage::DatabaseOwner::local_development();
    let source_path = root.join("source");
    let source = Engine::open(
        EngineConfig::durable(&source_path)
            .unwrap()
            .with_owner(source_owner.clone())
            .with_storage_keys(test_storage_keys(&source_owner, 145)),
    )
    .unwrap();
    let child_owner = crate::storage::DatabaseOwner::new(
        source_owner.environment().clone(),
        source_owner.tenant(),
        source_owner.project(),
        crate::identity::DatabaseUlid::generate(),
        source_owner.region(),
    );
    let target = root.join("target");
    let config = EngineConfig::durable(&target)
        .unwrap()
        .with_owner(child_owner)
        .with_storage_keys(test_storage_keys(&source_owner, 144));
    assert!(matches!(
        Engine::restore_encrypted_sqlite_branch(
            &source,
            &config,
            &OperationId::try_new("wrong-key").unwrap(),
        ),
        Err(EngineOpenError::InvalidConfig)
    ));
    assert_eq!(std::fs::read_dir(&target).unwrap().count(), 0);
    assert_eq!(std::fs::read_dir(&root).unwrap().count(), 1);
    drop(source);
    std::fs::remove_dir_all(root).unwrap();
}
