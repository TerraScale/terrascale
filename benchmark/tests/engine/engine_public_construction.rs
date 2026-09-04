//! Public-path Engine integration tests.
//!
//! These tests use only public construction: `EngineConfig::durable`,
//! `Engine::open`, and validated identifiers. `TenantScope` has no public
//! constructor by design (Platform creates it after authorization), so
//! transaction behavior is proved by the in-crate tests instead.

use std::path::PathBuf;

use terrabase::{Engine, EngineConfig, EngineOpenError, engine::EngineConfigError};

fn location(tag: &str) -> PathBuf {
    std::env::temp_dir()
        .join(format!("terrabase-w31-integration-engine-{tag}-{}", std::process::id()))
}

#[test]
fn durable_config_rejects_empty_and_unusable_locations() {
    assert_eq!(EngineConfig::durable("").err(), Some(EngineConfigError::EmptyLocation));
    let occupied = location("occupied");
    std::fs::write(&occupied, b"occupied").expect("writes a file");
    assert_eq!(EngineConfig::durable(&occupied).err(), Some(EngineConfigError::NonDurableLocation));
    std::fs::remove_file(&occupied).ok();
}

#[test]
fn durable_config_creates_an_absent_directory_and_engine_reopens() {
    let dir = location("fresh");
    std::fs::remove_dir_all(&dir).ok();
    let config = EngineConfig::durable(&dir).expect("creates the directory");
    assert!(dir.is_dir());
    let engine = Engine::open(config).expect("opens");
    engine.shutdown().expect("shuts down");
    // The owned SQLite location opens again.
    let engine = Engine::open(EngineConfig::durable(&dir).expect("still durable"))
        .expect("reopens the same location");
    engine.shutdown().expect("shuts down");
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn engine_open_fails_closed_on_unrelated_files_in_the_location() {
    let dir = location("foreign");
    std::fs::remove_dir_all(&dir).ok();
    EngineConfig::durable(&dir).expect("creates the directory");
    std::fs::write(dir.join("unrelated"), b"foreign bytes").expect("foreign file");
    assert_eq!(
        Engine::open(EngineConfig::durable(&dir).expect("still durable")).err(),
        Some(EngineOpenError::FormatMismatch)
    );
    std::fs::remove_dir_all(&dir).ok();
}
