//! Public-construction restore integration tests (BK-02).
//!
//! These tests use only public construction: `EngineConfig::durable`,
//! `Engine::open`, the public `terrabase::backup` seam, and the restore
//! entries on the crate-root `terrabase::RestorePlan`. `TenantScope` has
//! no public constructor by design (Platform creates it after
//! authorization), so the data-bearing restore drills are proved by the
//! in-crate suite at `src/backup/restore/tests.rs`; this file proves what an
//! external caller of the restore seam can construct and observe — the
//! empty-source lifecycle end to end on real destinations, restart-after-
//! restore through a real `Engine`, typed fail-closed behavior on crafted
//! corruption, and target refusal.
//!
//! No claim here extends beyond observable behavior; the tracked Recovery
//! Receipts under `docs/evidence/recovery/` record what the lane ran.

use std::path::PathBuf;

use terrabase::backup::{BackupError, BackupPlan, RestorePlan, run_backup};
use terrabase::{Engine, EngineConfig};

fn location(tag: &str) -> PathBuf {
    std::env::temp_dir().join(format!("terrabase-bk02-restore-int-{tag}-{}", std::process::id()))
}

fn open_engine(tag: &str) -> (PathBuf, Engine) {
    let dir = location(tag);
    std::fs::remove_dir_all(&dir).ok();
    let engine =
        Engine::open(EngineConfig::durable(&dir).expect("durable location")).expect("engine opens");
    (dir, engine)
}

#[test]
fn empty_source_full_backup_restores_into_a_fresh_engine_location() {
    let (_source, engine) = open_engine("empty-full");
    let destination = location("empty-full-dest");
    std::fs::remove_dir_all(&destination).ok();
    let target = location("empty-full-target");
    std::fs::remove_dir_all(&target).ok();

    let backup = run_backup(&engine, BackupPlan::full(), &destination).expect("an empty backup");
    let receipt = RestorePlan::restore_latest(&destination, &target).expect("restore runs");

    assert_eq!(receipt.selected_identity(), backup.manifest().identity());
    assert_eq!(receipt.chain_depth(), 1);
    assert_eq!(receipt.restored_documents(), 0);
    assert_eq!(receipt.restore_commits(), 0, "an empty archive publishes nothing");
    assert!(target.is_dir(), "the fresh engine location exists");

    // Restart-after-restore on the public seam: the restored location opens
    // as a real durable Engine and shuts down cleanly.
    let reopened = Engine::open(EngineConfig::durable(&target).expect("durable location"))
        .expect("the restored location reopens");
    reopened.shutdown().expect("the reopened engine shuts down");

    // The rendered receipt stays secret-safe: no private host path.
    let rendered = receipt.render();
    let target_text = target.to_string_lossy().into_owned();
    assert!(!rendered.contains(&target_text), "no path crosses the seam");
}

#[test]
fn restore_refuses_a_non_empty_target_on_the_public_seam() {
    let (_source, engine) = open_engine("refuse");
    let destination = location("refuse-dest");
    std::fs::remove_dir_all(&destination).ok();
    run_backup(&engine, BackupPlan::full(), &destination).expect("an empty backup");

    let target = location("refuse-target");
    std::fs::remove_dir_all(&target).ok();
    std::fs::create_dir_all(&target).expect("creates the target");
    std::fs::write(target.join("unrelated"), b"bytes").expect("seeds unrelated content");
    assert_eq!(
        RestorePlan::restore_latest(&destination, &target).err(),
        Some(BackupError::TargetNotEmpty),
        "restore refuses a non-empty target before touching anything"
    );
    assert!(target.join("unrelated").exists(), "the refused target is untouched");
}

#[test]
fn corrupt_recovery_points_fail_closed_with_typed_errors() {
    let (_source, engine) = open_engine("corrupt");
    let destination = location("corrupt-dest");
    std::fs::remove_dir_all(&destination).ok();
    let backup = run_backup(&engine, BackupPlan::full(), &destination).expect("an empty backup");
    let identity = backup.manifest().identity().to_owned();

    // A forged identity line fails manifest validation before any target is
    // created: the content digest is recomputed and does not match.
    let manifest_path = destination.join(format!("rp-{identity}")).join("manifest.tbv2m");
    let original = std::fs::read(&manifest_path).expect("reads the manifest");
    let forged = original.clone();
    let mut forged = forged;
    patch_identity(&mut forged, &"0".repeat(64));
    std::fs::write(&manifest_path, &forged).expect("writes the forged manifest");
    let target = location("corrupt-target-forged");
    std::fs::remove_dir_all(&target).ok();
    assert_eq!(
        RestorePlan::restore_latest(&destination, &target).err(),
        Some(BackupError::ManifestInvalid)
    );
    assert!(!target.exists(), "no target was created");

    // A removed point directory is an unusable destination, not a silent
    // empty restore.
    std::fs::remove_dir_all(destination.join(format!("rp-{identity}"))).expect("removes the point");
    let target = location("corrupt-target-missing");
    std::fs::remove_dir_all(&target).ok();
    assert_eq!(
        RestorePlan::restore_latest(&destination, &target).err(),
        Some(BackupError::ChainIncomplete),
        "a destination with no recovery point never restores"
    );
    assert!(!target.exists(), "no target was created");
}

/// Rewrites the identity line of serialized manifest bytes.
fn patch_identity(bytes: &mut Vec<u8>, replacement: &str) {
    let content = String::from_utf8_lossy(bytes).into_owned();
    let mut lines = content.split('\n').map(str::to_owned).collect::<Vec<_>>();
    for line in lines.iter_mut() {
        if line.starts_with("identity ") {
            *line = format!("identity {replacement}");
        }
    }
    let joined = lines.join("\n");
    bytes.clear();
    bytes.extend_from_slice(joined.as_bytes());
}

#[test]
fn restore_selection_validates_the_named_identity() {
    let (_source, engine) = open_engine("selection-validate");
    let destination = location("selection-validate-dest");
    std::fs::remove_dir_all(&destination).ok();
    run_backup(&engine, BackupPlan::full(), &destination).expect("an empty backup");
    let target = location("selection-validate-target");
    std::fs::remove_dir_all(&target).ok();

    // A malformed identity never becomes a directory lookup.
    for malformed in ["", "short", "../escape", "Z".repeat(64).as_str()] {
        assert_eq!(
            RestorePlan::restore_recovery_point(malformed, &destination, &target).err(),
            Some(BackupError::ManifestInvalid),
            "the malformed identity {malformed:?} is rejected"
        );
    }
    assert!(!target.exists(), "no target was created");

    // A well-formed but absent identity fails closed: the point cannot be
    // loaded, so it is not a recovery point.
    assert_eq!(
        RestorePlan::restore_recovery_point(&"ab".repeat(32), &destination, &target).err(),
        Some(BackupError::ManifestInvalid)
    );
    assert!(!target.exists(), "no target was created");
}
