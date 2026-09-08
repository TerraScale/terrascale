use super::*;
use crate::types::{
    CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion, LocalDurability,
    OperationId, TenantId,
};
use std::{
    process::Command,
    thread,
    time::{Duration, Instant},
};

#[test]
fn branch_restore_reservation_child() {
    let Ok(target) = std::env::var("TERRABASE_BRANCH_RESTORE_LOCK_CHILD") else {
        return;
    };
    let target = std::path::PathBuf::from(target);
    let _reservation = BranchRestoreReservation::acquire(&target).unwrap();
    thread::sleep(Duration::from_millis(750));
}

#[test]
fn branch_restore_reservation_is_cross_process() {
    let root = std::env::temp_dir()
        .join(format!("terrabase-branch-lock-{}", crate::identity::generate()));
    std::fs::create_dir_all(&root).unwrap();
    let target = root.join("target");
    std::fs::create_dir(&target).unwrap();
    let mut child = Command::new(std::env::current_exe().unwrap())
        .args([
            "--exact",
            "engine::branch_restore_tests::branch_restore_reservation_child",
            "--nocapture",
        ])
        .env("TERRABASE_BRANCH_RESTORE_LOCK_CHILD", &target)
        .spawn()
        .unwrap();
    let lock_path = root.join(".target.branch-restore.lock");
    let deadline = Instant::now() + Duration::from_secs(2);
    while !lock_path.exists() {
        assert!(Instant::now() < deadline, "child did not create reservation");
        thread::sleep(Duration::from_millis(10));
    }
    let started = Instant::now();
    let reservation = BranchRestoreReservation::acquire(&target).unwrap();
    assert!(started.elapsed() >= Duration::from_millis(500));
    drop(reservation);
    assert!(child.wait().unwrap().success());
    std::fs::remove_dir_all(root).unwrap();
}

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

fn open_branch_source(root: &std::path::Path) -> (Engine, crate::storage::DatabaseOwner, TenantId) {
    let source_owner = crate::storage::DatabaseOwner::try_new(
        "branch-test",
        crate::identity::TenantUlid::generate(),
        crate::identity::ProjectUlid::generate(),
        crate::identity::DatabaseUlid::generate(),
        crate::RegionCode::GRU,
    )
    .unwrap();
    let source = Engine::open(
        EngineConfig::durable(root.join("source"))
            .unwrap()
            .with_owner(source_owner.clone())
            .with_storage_keys(test_storage_keys(&source_owner, 141)),
    )
    .unwrap();
    source.activate_origin_epoch(1).unwrap();
    // The storage binding is persisted with the canonical identity text. Keep
    // the test scope on that same boundary instead of relying on the display
    // implementation of the typed owner identity.
    let tenant = TenantId::try_new(&source_owner.tenant().to_text()).unwrap();
    source
        .configure_usage_placement(
            crate::usage::UsagePlacement::try_new(
                tenant.clone(),
                source_owner.project().to_text().as_str(),
                source_owner.database().to_text().as_str(),
                "partition",
                source_owner.region(),
                vec![crate::RegionCode::GRU, crate::RegionCode::FRA],
                "branch-placement",
            )
            .unwrap(),
        )
        .unwrap();
    seed_branch_source(&source, &tenant);
    (source, source_owner, tenant)
}

fn restore_concurrently(
    source: &Engine,
    config: &EngineConfig,
    first_operation: &OperationId,
    second_operation: &OperationId,
) -> (
    Result<crate::types::StorageSequence, EngineOpenError>,
    Result<crate::types::StorageSequence, EngineOpenError>,
) {
    std::thread::scope(|threads| {
        let first_config = config.clone();
        let second_config = config.clone();
        let first_operation = first_operation.clone();
        let second_operation = second_operation.clone();
        let first = threads.spawn(move || {
            Engine::restore_encrypted_sqlite_branch(source, &first_config, &first_operation)
        });
        let second = threads.spawn(move || {
            Engine::restore_encrypted_sqlite_branch(source, &second_config, &second_operation)
        });
        (first.join().unwrap(), second.join().unwrap())
    })
}

#[test]
fn encrypted_branch_restore_rebinds_owner_and_replays_idempotently() {
    let root = std::env::temp_dir()
        .join(format!("terrabase-branch-restore-{}", crate::identity::generate()));
    let (source, source_owner, tenant) = open_branch_source(&root);
    let target_path = root.join("target");
    let project = source_owner.project();

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
    let (same_first, same_second) =
        restore_concurrently(&source, &child_config, &operation, &operation);
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
    let child_placement = child.usage_placement(&child.scope_for(&tenant)).unwrap().unwrap();
    assert_eq!(
        child_placement.selected_regions,
        vec![crate::RegionCode::GRU, crate::RegionCode::FRA]
    );
    assert_eq!(child_placement.scope.database_id().as_str(), child_owner.database().to_text());

    let distinct_target = root.join("distinct-target");
    let distinct_config = EngineConfig::durable(&distinct_target)
        .unwrap()
        .with_owner(child_owner.clone())
        .with_storage_keys(test_storage_keys(&child_owner, 146));
    let first_operation = OperationId::try_new("distinct-first").unwrap();
    let second_operation = OperationId::try_new("distinct-second").unwrap();
    let (distinct_first, distinct_second) =
        restore_concurrently(&source, &distinct_config, &first_operation, &second_operation);
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
    let source_config =
        EngineConfig::durable(&source).unwrap().with_owner(source_owner).with_storage_keys(
            test_storage_keys(&crate::storage::DatabaseOwner::local_development(), 143),
        );
    assert!(Engine::open(source_config).is_err());
    assert!(Engine::open(config).is_ok());
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
    assert!(
        std::fs::read_dir(&root)
            .unwrap()
            .flatten()
            .all(|entry| !entry.file_name().to_string_lossy().starts_with(".branch-restore-"))
    );
    drop(source);
    std::fs::remove_dir_all(root).unwrap();
}
