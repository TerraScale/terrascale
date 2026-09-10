use super::{Engine, EngineConfig, EngineOpenError};

#[test]
fn duplicate_engine_open_is_rejected_until_owner_drops() {
    let root =
        std::env::temp_dir().join(format!("terrabase-admission-{}", crate::identity::generate()));
    let config = EngineConfig::durable(&root).expect("config");
    let engine = Engine::open(config.clone()).expect("first engine");
    let lock_path = root
        .parent()
        .expect("parent")
        .join(format!(".{}.engine.lock", root.file_name().expect("basename").to_string_lossy()));
    let first_inode = std::fs::metadata(&lock_path).expect("lock inode").ino();
    assert!(matches!(Engine::open(config.clone()), Err(EngineOpenError::StorageUnavailable)));
    drop(engine);
    let reopened = Engine::open(config).expect("reopen after owner drop");
    assert_eq!(std::fs::metadata(lock_path).expect("reused lock inode").ino(), first_inode);
    drop(reopened);
    std::fs::remove_dir_all(root).expect("cleanup");
}

#[cfg(unix)]
use std::os::unix::fs::MetadataExt;

#[test]
fn rejected_duplicate_does_not_retire_live_sync_frontier() {
    let root = std::env::temp_dir()
        .join(format!("terrabase-admission-sync-{}", crate::identity::generate()));
    let config = EngineConfig::durable(&root).expect("config");
    let engine = Engine::open(config.clone()).expect("first engine");
    engine
        .storage
        .lock()
        .expect("storage lock")
        .port
        .register_consumer_frontier("sync", 7)
        .expect("sync lease");
    assert!(matches!(Engine::open(config), Err(EngineOpenError::StorageUnavailable)));
    let connection = engine.open_sqlite_read_connection().expect("connection");
    let count: i64 = connection
        .query_row("SELECT COUNT(*) FROM consumer_frontiers WHERE name='sync'", [], |row| {
            row.get(0)
        })
        .expect("sync row");
    assert_eq!(count, 1);
    drop(engine);
    std::fs::remove_dir_all(root).expect("cleanup");
}

#[test]
fn storage_clone_keeps_admission_until_clone_drops() {
    let root = std::env::temp_dir()
        .join(format!("terrabase-admission-clone-{}", crate::identity::generate()));
    let config = EngineConfig::durable(&root).expect("config");
    let engine = Engine::open(config.clone()).expect("first engine");
    let storage = std::sync::Arc::clone(&engine.storage);
    drop(engine);
    assert!(matches!(Engine::open(config.clone()), Err(EngineOpenError::StorageUnavailable)));
    drop(storage);
    let reopened = Engine::open(config).expect("reopen after storage clone drop");
    drop(reopened);
    std::fs::remove_dir_all(root).expect("cleanup");
}

#[test]
fn engine_reopen_retires_stale_sync_frontier_after_exclusive_admission() {
    let root = std::env::temp_dir()
        .join(format!("terrabase-admission-restart-{}", crate::identity::generate()));
    let config = EngineConfig::durable(&root).expect("config");
    let engine = Engine::open(config.clone()).expect("first engine");
    engine
        .storage
        .lock()
        .expect("storage lock")
        .port
        .register_consumer_frontier("sync", 7)
        .expect("sync lease");
    drop(engine);
    let reopened = Engine::open(config).expect("reopen");
    let connection = reopened.open_sqlite_read_connection().expect("connection");
    let count: i64 = connection
        .query_row("SELECT COUNT(*) FROM consumer_frontiers WHERE name='sync'", [], |row| {
            row.get(0)
        })
        .expect("sync row");
    assert_eq!(count, 0);
    drop(reopened);
    std::fs::remove_dir_all(root).expect("cleanup");
}

#[test]
fn single_component_relative_target_is_bound_normally() {
    let name = format!("terrabase-admission-relative-{}", crate::identity::generate());
    let config = EngineConfig::durable(&name).expect("config");
    let admission_guard =
        super::admission::EngineAdmissionGuard::acquire(std::path::Path::new(&name))
            .expect("relative target admission");
    let canonical = std::fs::canonicalize(&name).expect("canonical target");
    assert!(admission_guard.matches(std::path::Path::new(&name)));
    assert!(admission_guard.matches(&canonical));
    assert!(!admission_guard.matches(&canonical.with_file_name(format!("{name}-other"))));
    drop(admission_guard);
    let engine = Engine::open(config).expect("relative target opens");
    let canonical_config = EngineConfig::durable(&canonical).expect("canonical config");
    assert!(matches!(
        Engine::open(canonical_config.clone()),
        Err(EngineOpenError::StorageUnavailable)
    ));
    drop(engine);
    let reopened = Engine::open(canonical_config).expect("canonical reopen after relative owner");
    drop(reopened);
    std::fs::remove_dir_all(name).expect("cleanup");
}
