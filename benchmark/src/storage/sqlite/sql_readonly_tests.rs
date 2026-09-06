//! Regression coverage for the storage read-only connection seam.

use std::sync::atomic::{AtomicU64, Ordering};

use crate::engine::{Engine, EngineConfig};

fn location(tag: &str) -> std::path::PathBuf {
    static NEXT: AtomicU64 = AtomicU64::new(0);
    std::env::temp_dir().join(format!(
        "terrabase-sql-readonly-{tag}-{}-{}",
        std::process::id(),
        NEXT.fetch_add(1, Ordering::Relaxed)
    ))
}

fn assert_read_only(engine: &Engine) {
    let connection = engine.open_sqlite_read_connection().expect("read connection");
    let _: String = connection
        .query_row("SELECT value FROM meta WHERE key = 'root_head'", [], |row| row.get(0))
        .expect("durable read succeeds");
    connection
        .execute_batch("CREATE TEMP VIEW sql_read_only_probe AS SELECT 1 AS value;")
        .expect("temporary schema remains available");
    assert_eq!(
        connection
            .query_row("SELECT value FROM sql_read_only_probe", [], |row| row.get::<_, i64>(0))
            .expect("temporary view reads"),
        1
    );
    assert!(
        connection
            .execute("INSERT INTO meta (key, value) VALUES ('read-only-probe', 'x')", [])
            .is_err(),
        "durable writes must be rejected"
    );
    assert!(
        connection.execute_batch("PRAGMA journal_mode=DELETE;").is_err(),
        "journal mode changes must be rejected"
    );
}

#[test]
fn plain_read_connection_is_read_only_but_supports_temp_views() {
    let directory = location("plain");
    let engine = Engine::open(EngineConfig::durable(&directory).expect("config")).expect("open");
    assert_read_only(&engine);
    drop(engine);
    std::fs::remove_dir_all(directory).expect("cleanup");
}

#[test]
fn encrypted_read_connection_is_read_only_but_supports_temp_views() {
    let directory = location("encrypted");
    let owner = crate::storage::DatabaseOwner::local_development();
    let keys = crate::engine::test_storage_keys(&owner, 211);
    let config = EngineConfig::durable(&directory)
        .expect("config")
        .with_owner(owner)
        .with_storage_keys(keys);
    let engine = Engine::open(config).expect("encrypted open");
    assert_read_only(&engine);
    drop(engine);
    std::fs::remove_dir_all(directory).expect("cleanup");
}
