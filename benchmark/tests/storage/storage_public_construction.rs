//! Public-path storage-facing integration tests.
//!
//! The storage seam is private by contract; these tests prove the public
//! durability configuration surface only. Storage, WAL, and recovery behavior
//! is proved by the in-crate tests in `src/storage`.

use std::path::PathBuf;

use terrabase_v2::{Engine, EngineConfig, engine::EngineConfigError};

fn location(tag: &str) -> PathBuf {
    std::env::temp_dir().join(format!("tbv2-w31-integration-storage-{tag}-{}", std::process::id()))
}

#[test]
fn every_durable_location_is_its_own_store() {
    let first = location("first");
    let second = location("second");
    std::fs::remove_dir_all(&first).ok();
    std::fs::remove_dir_all(&second).ok();
    let first_engine = Engine::open(EngineConfig::durable(&first).expect("durable"))
        .expect("opens the first store");
    first_engine.shutdown().expect("shuts down");
    let second_engine = Engine::open(EngineConfig::durable(&second).expect("durable"))
        .expect("opens the second store");
    second_engine.shutdown().expect("shuts down");
    assert_ne!(first, second);
    std::fs::remove_dir_all(&first).ok();
    std::fs::remove_dir_all(&second).ok();
}

#[test]
fn relative_locations_are_accepted_as_durable_directories() {
    let relative = format!("tbv2-w31-relative-{}", std::process::id());
    std::fs::remove_dir_all(&relative).ok();
    let config = EngineConfig::durable(&relative).expect("a real directory is durable");
    let engine = Engine::open(config).expect("opens");
    engine.shutdown().expect("shuts down");
    std::fs::remove_dir_all(&relative).ok();

    // A file blocking the path keeps the location from becoming durable.
    let blocker = format!("tbv2-w31-blocker-{}", std::process::id());
    std::fs::write(&blocker, b"occupied").expect("writes a file");
    assert_eq!(
        EngineConfig::durable(format!("{blocker}/nested")).err(),
        Some(EngineConfigError::NonDurableLocation)
    );
    std::fs::remove_file(&blocker).ok();
}
