//! Public-construction backup-core integration tests (BK-01).
//!
//! These tests use only public construction: `EngineConfig::durable`,
//! `Engine::open`, and the public `terrabase_v2::backup` seam. `TenantScope`
//! has no public constructor by design (Platform creates it after
//! authorization), so the data-bearing backup behavior is proved by the
//! in-crate suite at `src/backup/tests.rs`; this file proves what an
//! external caller of the backup seam can construct and observe — the
//! empty-source lifecycle, crafted-corruption rejection, and admission —
//! end to end on real destinations.
//!
//! No claim here extends beyond observable behavior: formal Recovery
//! Receipts come exclusively from the exclusive recovery lane.

use std::path::PathBuf;
use std::sync::Arc;
use std::sync::mpsc;

use terrabase_v2::backup::{
    BackupError, BackupKind, BackupPlan, load_recovery_point, run_backup, verify_latest_chain,
};
use terrabase_v2::{Engine, EngineConfig};

fn location(tag: &str) -> PathBuf {
    std::env::temp_dir().join(format!("tbv2-bk01-int-{tag}-{}", std::process::id()))
}

fn open_engine(tag: &str) -> (PathBuf, Engine) {
    let dir = location(tag);
    std::fs::remove_dir_all(&dir).ok();
    let engine =
        Engine::open(EngineConfig::durable(&dir).expect("durable location")).expect("engine opens");
    (dir, engine)
}

#[test]
fn empty_source_full_backup_creates_a_verifiable_recovery_point() {
    let (_source, engine) = open_engine("empty-full");
    let destination = location("empty-full-dest");
    std::fs::remove_dir_all(&destination).ok();

    let result =
        run_backup(&engine, BackupPlan::full(), &destination).expect("an empty source backs up");

    let manifest = result.manifest();
    assert_eq!(manifest.kind(), BackupKind::Full);
    assert_eq!(manifest.boundary_sequence().get(), 0, "an empty source has boundary zero");
    assert_eq!(manifest.documents(), 0);
    assert_eq!(manifest.family_records(), 0);
    assert_eq!(manifest.deletes(), 0);
    assert!(manifest.tenants().is_empty());
    assert!(manifest.members().is_empty(), "no records means no archive members");
    assert_eq!(manifest.identity().len(), 64, "identity is a SHA-256 hex digest");
    assert!(!manifest.identity().starts_with('/'), "no path crosses the seam");

    assert!(result.archive().verify(&destination).is_ok(), "the empty point verifies");

    let reloaded =
        load_recovery_point(&destination, manifest.identity()).expect("the point reloads");
    assert_eq!(reloaded, *manifest);

    let chain = verify_latest_chain(&destination).expect("the chain verifies");
    assert_eq!(chain.len(), 1);
    assert_eq!(chain[0].identity(), manifest.identity());

    // The result carries no path or archive content, only typed facts.
    assert_eq!(result.skipped_total(), 0);
    assert!(result.skipped_items().is_empty());
    assert!(result.source_reads() >= 1, "the enumeration itself was a read");
}

#[test]
fn empty_source_incremental_fails_closed_with_a_typed_error() {
    let (_source, engine) = open_engine("empty-incremental");
    let destination = location("empty-incremental-dest");
    std::fs::remove_dir_all(&destination).ok();

    let empty_range = run_backup(
        &engine,
        BackupPlan::incremental_since(terrabase_v2::StorageSequence::default()),
        &destination,
    );
    assert_eq!(
        empty_range.err(),
        Some(BackupError::ChainIncomplete),
        "an incremental over an empty journal cannot prove its chain"
    );

    assert_eq!(
        verify_latest_chain(&destination).err(),
        Some(BackupError::ChainIncomplete),
        "no recovery point was sealed"
    );
}

#[test]
fn manifest_validation_rejects_path_traversal_and_in_progress_manifests() {
    let (_source, engine) = open_engine("validation");
    let destination = location("validation-dest");
    std::fs::remove_dir_all(&destination).ok();
    let result = run_backup(&engine, BackupPlan::full(), &destination).expect("seeds a point");
    let identity = result.manifest().identity().to_owned();

    // Traversal: a manifest that names a member outside its point directory
    // is rejected before any file under that name is touched. The public
    // seam cannot seed documents (writing needs a Platform-authorized
    // scope), so the point has zero real members; the probe appends a
    // traversal member line and validation must still reject the manifest
    // before any file under that name is touched.
    let manifest_path = destination.join(format!("rp-{identity}")).join("manifest.tbv2m");
    let original = std::fs::read(&manifest_path).expect("reads the manifest");
    let traversal = append_traversal_member(&original, "../../etc/passwd");
    std::fs::write(&manifest_path, &traversal).expect("writes the traversal manifest");
    assert_eq!(
        load_recovery_point(&destination, &identity).err(),
        Some(BackupError::ManifestInvalid),
        "traversal names fail validation"
    );
    assert_eq!(
        verify_latest_chain(&destination).err(),
        Some(BackupError::ManifestInvalid),
        "discovery fails closed"
    );

    // In-progress: a manifest still being written is never a recovery point,
    // and the identity it claims cannot be loaded. (Header version 2 is the
    // current manifest format.)
    let staging_identity = "ab".repeat(32);
    let partial_dir = destination.join(format!("rp-{staging_identity}"));
    std::fs::create_dir_all(&partial_dir).expect("creates a point directory");
    std::fs::write(
        partial_dir.join("manifest.tbv2m.inprogress"),
        b"terrabase-backup-manifest 2\nin_progress true\n",
    )
    .expect("writes an in-progress manifest");
    assert_eq!(
        load_recovery_point(&destination, &staging_identity).err(),
        Some(BackupError::ManifestInvalid),
        "an in-progress manifest never validates"
    );

    // A tampered identity line is a mismatch: validation recomputes the
    // content digest and rejects the forgery.
    std::fs::write(&manifest_path, &original).expect("restores the manifest");
    let forged_identity = replace_identity(&original, &"0".repeat(64));
    std::fs::write(&manifest_path, &forged_identity).expect("writes the forged identity");
    assert_eq!(
        load_recovery_point(&destination, &identity).err(),
        Some(BackupError::ManifestInvalid),
        "a forged identity line is rejected"
    );

    std::fs::write(&manifest_path, &original).expect("restores the manifest again");
    assert!(load_recovery_point(&destination, &identity).is_ok(), "the honest manifest reloads");
}

/// Appends one member line with a traversal name to serialized manifest
/// bytes, before the trailing newline. The test only needs a traversal name
/// to reach validation; validation must reject the name before touching any
/// file under it, whatever the identity line claims.
fn append_traversal_member(original: &[u8], name: &str) -> Vec<u8> {
    let mut forged = original.to_vec();
    let line = format!("member {name} 5 {}\n", "0".repeat(64));
    // The manifest always ends with exactly one newline.
    forged.pop();
    forged.extend_from_slice(line.as_bytes());
    forged
}

fn replace_identity(original: &[u8], forged: &str) -> Vec<u8> {
    let content = String::from_utf8_lossy(original).into_owned();
    let mut lines = content.split('\n').map(str::to_owned).collect::<Vec<_>>();
    for line in lines.iter_mut() {
        if line.starts_with("identity ") {
            *line = format!("identity {forged}");
        }
    }
    lines.join("\n").into_bytes()
}

/// Creates one unsealed orphan recovery-point directory: the state a crash
/// before an atomic publish leaves behind.
fn create_unsealed_orphan(destination: &std::path::Path, identity: &str) -> std::path::PathBuf {
    let orphan = destination.join(format!("rp-{identity}"));
    std::fs::create_dir_all(&orphan).expect("creates the orphan directory");
    std::fs::write(orphan.join("member-000001"), b"staged bytes").expect("stages a member");
    assert!(!orphan.join("manifest.tbv2m").exists(), "the orphan holds no sealed manifest");
    orphan
}

#[test]
fn unsealed_orphan_directories_do_not_break_discovery_or_retry() {
    let (_source, engine) = open_engine("orphan");
    let destination = location("orphan-dest");
    std::fs::remove_dir_all(&destination).ok();

    let sealed =
        run_backup(&engine, BackupPlan::full(), &destination).expect("an empty source backs up");
    let identity = sealed.manifest().identity().to_owned();
    assert!(sealed.archive().verify(&destination).is_ok());

    // A crash-orphan directory beside the sealed point must not fail the
    // destination: discovery skips what is not a sealed recovery point.
    let bystander = create_unsealed_orphan(&destination, &"cd".repeat(32));
    let chain = verify_latest_chain(&destination).expect("the sealed point still verifies");
    assert_eq!(chain.len(), 1);
    assert_eq!(chain[0].identity(), identity);

    // A crash-orphan of the run's own identity — the exact state a crash
    // before the publish rename leaves — must be replaced by the identical
    // retry, not reject it. Identity is content-derived, so a fresh
    // destination's identical run seals the same recovery point.
    let crashed = location("orphan-crashed-dest");
    std::fs::remove_dir_all(&crashed).ok();
    create_unsealed_orphan(&crashed, &identity);
    let retry = run_backup(&engine, BackupPlan::full(), &crashed).expect("retry over orphan");
    assert_eq!(retry.manifest().identity(), identity, "the retry is idempotent");
    assert!(retry.archive().verify(&crashed).is_ok());
    assert!(
        load_recovery_point(&crashed, &identity).is_ok(),
        "the retried point is sealed and loadable"
    );

    let identities = |root: &std::path::Path| {
        let mut found: Vec<String> = root
            .read_dir()
            .expect("reads the destination")
            .filter_map(|entry| {
                let name = entry.expect("entry").file_name().to_string_lossy().into_owned();
                name.strip_prefix("rp-").map(str::to_owned)
            })
            .collect();
        found.sort();
        found
    };
    let mut both = vec![identity.clone(), "cd".repeat(32)];
    both.sort();
    assert_eq!(
        identities(&crashed),
        vec![identity.clone()],
        "the orphan was replaced, not duplicated"
    );
    assert_eq!(
        identities(&destination),
        both,
        "the sealed point and the bystander orphan both remain"
    );
    assert!(bystander.is_dir(), "the bystander orphan itself is untouched");
}

#[test]
fn concurrent_backup_attempts_on_the_public_seam_reject_the_second() {
    let (_source, engine) = open_engine("admission");
    let engine = Arc::new(engine);
    let destination = location("admission-dest");
    std::fs::remove_dir_all(&destination).ok();

    // The held job blocks inside its progress observer, which fires before
    // any storage read, so the overlap is deterministic without timing.
    let (entered_tx, entered_rx) = mpsc::channel::<()>();
    let release = Arc::new((std::sync::Mutex::new(false), std::sync::Condvar::new()));
    let release_waiter = Arc::clone(&release);
    let observer: Arc<dyn Fn(terrabase_v2::backup::BackupProgress) + Send + Sync> =
        Arc::new(move |_progress: terrabase_v2::backup::BackupProgress| {
            if entered_tx.send(()).is_ok() {
                let (flag, condvar) = &*release_waiter;
                let mut released = flag.lock().expect("release lock");
                while !*released {
                    released = condvar.wait(released).expect("release wait");
                }
            }
        });
    let held_plan = BackupPlan::full().with_progress(observer);
    let engine_for_job = Arc::clone(&engine);
    let job_destination = destination.clone();
    let job = std::thread::spawn(move || {
        run_backup(&engine_for_job, held_plan, &job_destination).expect("the held job completes")
    });

    entered_rx.recv().expect("the first job is running");
    assert_eq!(
        run_backup(&engine, BackupPlan::full(), &destination).err(),
        Some(BackupError::Throttled),
        "admission rejects the concurrent attempt with a typed error"
    );

    let (flag, condvar) = &*release;
    *flag.lock().expect("release lock") = true;
    condvar.notify_all();
    assert!(job.join().expect("the held job completes").archive().verify(&destination).is_ok());
}
