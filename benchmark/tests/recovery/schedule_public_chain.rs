//! Public-seam scheduled-cadence chain tests (BK-04).
//!
//! These tests use only public construction — `EngineConfig::durable`,
//! `Engine::open`, and the public `terrabase_v2::backup` run/verify seam —
//! to prove the chain facts a periodic schedule's decisions rest on: a
//! first full anchors the destination, an incremental over the anchored
//! boundary appends as a chain link, and a retried cadence slot is
//! idempotent rather than minting a duplicate recovery point.
//! `TenantScope` has no public constructor by design (Platform creates it
//! after authorization), so the data-bearing cadence behavior — real
//! journal growth, rotation, missed windows, pause, retention, encryption —
//! is proved by the in-crate suite at `src/backup/schedule/tests.rs`; the
//! schedule typed-operation surface becomes backend-drivable when the
//! integrator exposes the module (recorded collision action).
//!
//! No claim here extends beyond observable behavior: formal Recovery
//! Receipts and lane fixtures come exclusively from the exclusive recovery
//! lane (BK-02).

use std::path::PathBuf;

use terrabase_v2::backup::{
    BackupKind, BackupPlan, load_recovery_point, run_backup, verify_latest_chain,
};
use terrabase_v2::{Engine, EngineConfig, StorageSequence};

fn location(tag: &str) -> PathBuf {
    std::env::temp_dir().join(format!("tbv2-bk04-int-{tag}-{}", std::process::id()))
}

fn open_engine(tag: &str) -> (PathBuf, Engine) {
    let dir = location(tag);
    std::fs::remove_dir_all(&dir).ok();
    let engine =
        Engine::open(EngineConfig::durable(&dir).expect("durable location")).expect("engine opens");
    (dir, engine)
}

/// Counts the sealed recovery-point directories in a destination.
fn point_count(destination: &std::path::Path) -> usize {
    destination
        .read_dir()
        .expect("reads the destination")
        .filter(|entry| {
            entry
                .as_ref()
                .map(|entry| {
                    entry.file_name().to_string_lossy().starts_with("rp-")
                        && entry.path().join("manifest.tbv2m").is_file()
                })
                .unwrap_or(false)
        })
        .count()
}

#[test]
fn a_first_full_anchors_and_cadence_links_append_idempotently() {
    let (_source, engine) = open_engine("cadence");
    let destination = location("cadence-dest");
    std::fs::remove_dir_all(&destination).ok();

    // First cadence slot: the anchoring full.
    let full = run_backup(&engine, BackupPlan::full(), &destination).expect("the first full seals");
    assert_eq!(full.manifest().kind(), BackupKind::Full);
    assert_eq!(full.manifest().boundary_sequence(), StorageSequence::default());
    assert!(full.archive().verify(&destination).is_ok());

    // Incremental slot: appends onto the anchored boundary as a chain link.
    let anchored = full.manifest().boundary_sequence();
    let link = run_backup(&engine, BackupPlan::incremental_since(anchored), &destination)
        .expect("the incremental seals onto the anchor");
    assert_eq!(link.manifest().kind(), BackupKind::Incremental { base: anchored });
    assert_eq!(link.manifest().boundary_sequence(), anchored);
    let chain = verify_latest_chain(&destination).expect("the chain verifies");
    assert_eq!(chain.len(), 2);
    assert_eq!(chain[0].kind(), BackupKind::Full);
    assert_eq!(chain[0].identity(), full.manifest().identity());
    assert_eq!(chain[1].identity(), link.manifest().identity());

    // A retried slot is idempotent: the same content-derived identity
    // re-seals exactly one recovery point, never a duplicate chain member.
    let retry = run_backup(&engine, BackupPlan::incremental_since(anchored), &destination)
        .expect("the retry seals");
    assert_eq!(retry.manifest().identity(), link.manifest().identity());
    assert_eq!(point_count(&destination), 2, "no duplicate recovery point was minted");
    let chain = verify_latest_chain(&destination).expect("the chain still verifies");
    assert_eq!(chain.len(), 2);
    assert_eq!(chain[1].identity(), link.manifest().identity());

    // Both cadence artifacts reload with their exact manifests.
    for sealed in [&full, &link] {
        let identity = sealed.manifest().identity();
        assert_eq!(
            load_recovery_point(&destination, identity).expect("reloads"),
            *sealed.manifest(),
            "the point {identity} reloads exactly"
        );
    }
}

#[test]
fn an_unanchored_cadence_link_fails_typed_and_leaves_no_point() {
    let (_source, engine) = open_engine("unanchored");
    let destination = location("unanchored-dest");
    std::fs::remove_dir_all(&destination).ok();

    // A cadence must start with a full: an incremental whose base no sealed
    // recovery point covers is a typed failure that seals nothing — the
    // fact a schedule hitting a wiped or foreign destination surfaces.
    assert_eq!(
        run_backup(
            &engine,
            BackupPlan::incremental_since(StorageSequence::default()),
            &destination
        )
        .err(),
        Some(terrabase_v2::backup::BackupError::ChainIncomplete)
    );
    assert_eq!(point_count(&destination), 0, "the rejected run sealed nothing");

    let full = run_backup(&engine, BackupPlan::full(), &destination).expect("the full anchors");
    assert_eq!(point_count(&destination), 1);
    assert!(verify_latest_chain(&destination).is_ok());
    assert!(
        load_recovery_point(&destination, full.manifest().identity()).is_ok(),
        "the anchor survives the rejected link"
    );
}
