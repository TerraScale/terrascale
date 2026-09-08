//! Temporary commit-timing probe: one durable engine, N synced commits,
//! eprintln timings. Delete after the per-commit CPU regression is fixed.
use std::time::Instant;

use super::*;
#[test]
fn probe_single_document_commit_timings() {
    let dir = std::env::temp_dir().join(format!("terrabase-commit-probe-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&dir);
    let engine =
        Engine::open(EngineConfig::durable(&dir).expect("durable location")).expect("engine opens");
    let scope = TenantScope::from_parts(
        TenantId::try_new("tenant-a").expect("valid tenant"),
        engine.identity(),
    );
    let payload = "x".repeat(256);
    for index in 0..10u64 {
        let snapshot = engine.read_snapshot(&scope).expect("snapshot");
        let mut transaction = engine
            .begin(&scope, snapshot, OperationId::try_new("probe").expect("valid"))
            .expect("begins");
        transaction
            .put(
                Document::new(
                    DocumentId::try_new(&format!("probe-{index}")).expect("valid"),
                    CollectionName::try_new("users").expect("valid"),
                    crate::types::DocumentValue::String(payload.clone()),
                ),
                crate::types::ExpectedVersion::Any,
            )
            .expect("stages");
        let started = Instant::now();
        let outcome = transaction.commit(LocalDurability::Synced);
        let elapsed = started.elapsed();
        eprintln!("PROBE commit {index}: {elapsed:?} -> {outcome:?}");
    }
    let _ = std::fs::remove_dir_all(&dir);
}
