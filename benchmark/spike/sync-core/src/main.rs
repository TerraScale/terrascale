//! The SE-02 spike bin: runs every proof scenario once and prints the
//! recorded outcomes (human summary first, then a machine-readable block).
//!
//! Output is secret-safe by construction: every number comes from
//! synthetic data through [`terrabase_sync_core_spike::scenario`].

use terrabase_sync_core_spike::scenario;

fn main() {
    let delta = scenario::delta_delivery(0xD1CE).expect("delta delivery scenario");
    println!("== SE-02 spike: Loro as the engine sync core ==");
    println!(
        "delta delivery: {} docs x {} fields, initial snapshot {} B vs naive full-bodies {} B",
        delta.documents,
        delta.fields_per_document,
        delta.initial_snapshot_bytes,
        delta.initial_full_bodies_bytes
    );
    for pattern in &delta.patterns {
        println!(
            "  {:<22} full-document {:>6} B -> delta {:>5} B ({}/commit, ratio {})",
            pattern.name,
            pattern.full_document_bytes,
            pattern.delta_bytes,
            pattern.commits,
            pattern.delta_over_full().map_or("n/a".to_string(), |r| format!("{r:.3}"))
        );
    }

    let resume = scenario::checkpoint_resume(0xFEED).expect("resume scenario");
    println!(
        "checkpoint resume: {} commits before disconnect, {} while disconnected -> {} batches, {} B; converged={}, duplicate-safe={}, floor-answered={}, resync-converged={}, deltas-after-resync={}",
        resume.commits_before_disconnect,
        resume.commits_while_disconnected,
        resume.reconnect_batches,
        resume.reconnect_delta_bytes,
        resume.converged_after_resume,
        resume.converged_after_duplicate,
        resume.floor_answered,
        resume.converged_after_resync,
        resume.deltas_serve_after_resync
    );

    let burst = scenario::bounded_memory_burst(0xB057).expect("burst scenario");
    println!(
        "bounded memory burst: {} commits over {} docs, budget {} B -> {} batches, {} B total, max batch {} B, bounded={}, ops retained {}",
        burst.commits,
        burst.documents,
        burst.budget_bytes,
        burst.batches,
        burst.delta_bytes_total,
        burst.max_batch_bytes,
        burst.all_batches_bounded,
        burst.ops_retained
    );
    let resident = |v: &Option<u64>| v.map_or("unrun".to_string(), |b| format!("{b} B"));
    println!(
        "  resident: before={}, after-materialize={}, after-drain={}, after-free={}",
        resident(&burst.resident_before),
        resident(&burst.resident_after_materialize),
        resident(&burst.resident_after_drain),
        resident(&burst.resident_after_free)
    );

    let determinism = scenario::deterministic_replay(0x0DD0).expect("determinism scenario");
    println!(
        "deterministic replay: snapshots={}, deep-values={}, consumers={}, consumer-snapshot={}, incremental={}",
        determinism.snapshots_identical,
        determinism.deep_values_identical,
        determinism.consumers_observe_identically,
        determinism.consumer_snapshot_matches_server,
        determinism.incremental_pull_identical
    );

    println!("== machine block ==");
    println!(
        "{{\"delta\":{delta:?},\"resume\":{resume:?},\"burst\":{burst:?},\"determinism\":{determinism:?}}}"
    );
}
