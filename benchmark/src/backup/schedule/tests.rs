//! In-crate scheduling, retention, and artifact-codec tests (BK-04).
//!
//! `TenantScope` has no public constructor by design (Platform creates it
//! after authorization), so the data-bearing tests live in-crate with the
//! `TenantScope::from_parts` fixture, mirroring the other in-crate suites.
//! Every assertion goes through the `crate::backup::schedule` seam — the
//! typed-operation surface PD-2026-08-26-11 D2 exposes through the backend —
//! and the public `crate::backup` run/verify seam beneath it. No test inspects
//! private codec bytes directly: every integrity fact is read back through
//! the typed open/extract paths.
//!
//! These tests prove observable behavior only. They make no recovery,
//! durability, RPO/RTO, or performance claim: formal Recovery Receipts come
//! exclusively from the exclusive recovery lane (BK-02), and lane fixtures
//! for scheduled chains belong there. Time is injected everywhere — cadence
//! through caller-supplied ticks, retention ages through manifest file
//! mtimes — so no test sleeps or reads a wall clock for its verdict.

// Retention ages are stated in seconds because the larger-unit Duration
// constructors are unstable on the pinned toolchain.
#![allow(
    clippy::duration_suboptimal_units,
    reason = "the larger-unit Duration constructors are unstable on the pinned toolchain"
)]

use std::path::{Path, PathBuf};
use std::time::{Duration, SystemTime};

use super::{
    BackupSchedule, BundleOptions, CompressionError, DestinationConfig, DestinationError,
    DestinationInvalid, DestinationKind, EncryptionError, EncryptionKey, MissedRunPolicy,
    PrunedPoint, RetentionFailure, RetentionPolicy, ScheduleDecision, ScheduleDriver,
    ScheduleError, ScheduleInvalid, SchedulePhase, ScheduleState, compress, decompress,
    enforce_retention, extract_artifact_bundle, open_artifact_bundle, seal_artifact_bundle,
};
use crate::backup::{
    BackupError, BackupKind, BackupPlan, load_recovery_point, run_backup, verify_latest_chain,
};
use crate::engine::{CommitOutcome, Engine, EngineConfig};
use crate::types::{
    CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion, LocalDurability,
    OperationId, TenantId, TenantScope,
};

// ---------------------------------------------------------------------
// Fixtures
// ---------------------------------------------------------------------

fn location(tag: &str) -> PathBuf {
    static COUNTER: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(0);
    let index = COUNTER.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
    let path = std::env::temp_dir()
        .join(format!("tbv2-bk04-schedule-{tag}-{}-{index}", std::process::id()));
    let _ = std::fs::remove_dir_all(&path);
    path
}

fn open_engine(tag: &str) -> (PathBuf, Engine) {
    let dir = location(tag);
    let engine =
        Engine::open(EngineConfig::durable(&dir).expect("durable location")).expect("engine opens");
    (dir, engine)
}

fn scope(engine: &Engine, name: &str) -> TenantScope {
    TenantScope::from_parts(TenantId::try_new(name).expect("valid tenant"), engine.identity())
}

fn put(engine: &Engine, tenant_name: &str, operation_name: &str, id: &str, value: &str) {
    let tenant_scope = scope(engine, tenant_name);
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let mut transaction =
        engine.begin(&tenant_scope, snapshot, operation_id(operation_name)).expect("begins");
    transaction
        .put(
            Document::new(
                DocumentId::try_new(id).expect("valid identifier"),
                CollectionName::try_new("users").expect("valid identifier"),
                DocumentValue::String(value.to_owned()),
            ),
            ExpectedVersion::Any,
        )
        .expect("stages");
    assert!(matches!(transaction.commit(LocalDurability::Synced), CommitOutcome::Committed(_)));
}

fn operation_id(name: &str) -> OperationId {
    OperationId::try_new(name).expect("valid identifier")
}

/// A fresh random key per call: no key material is ever a committed constant.
fn key() -> EncryptionKey {
    EncryptionKey::generate().expect("an OS-backed key generates")
}

/// A key built from caller-owned material, so the same test can scan artifacts
/// for exactly those bytes.
fn key_from(material: &[u8]) -> EncryptionKey {
    EncryptionKey::from_material(material).expect("the material is long enough")
}

fn point_manifest_path(destination: &Path, identity: &str) -> PathBuf {
    destination.join(format!("rp-{identity}")).join("manifest.tbv2m")
}

/// The sealed bundle artifact a schedule leaves beside one recovery point.
fn bundle_path(destination: &Path, identity: &str) -> PathBuf {
    destination.join(format!("bundle-{identity}.tbv2b"))
}

/// Pins the sealed-at fact of one recovery point: the mtime of its manifest
/// file, the last byte written before publication.
fn pin_point_age(destination: &Path, identity: &str, age: Duration) {
    let manifest = point_manifest_path(destination, identity);
    let file = std::fs::File::options().write(true).open(&manifest).expect("opens the manifest");
    let sealed_at = SystemTime::now().checked_sub(age).expect("a representable age");
    file.set_times(std::fs::FileTimes::new().set_modified(sealed_at)).expect("pins the mtime");
}

fn schedule() -> BackupSchedule {
    BackupSchedule::full_every(1_000)
        .with_incremental_every(300)
        .with_missed_run_policy(MissedRunPolicy::RunOneNow)
        .with_retention(RetentionPolicy::disabled())
}

fn driver(destination: &Path, schedule: BackupSchedule) -> ScheduleDriver {
    ScheduleDriver::new(schedule, DestinationConfig::local(destination))
        .expect("a valid schedule and destination build a driver")
}

/// Seals one full-plus-incremental chain of the given shape into
/// `destination` through the public run seam, returning the identities in
/// publication order.
fn seed_chain(engine: &Engine, destination: &Path, increments: usize) -> Vec<String> {
    let mut identities = Vec::new();
    put(engine, "alpha", "seed-anchor", "doc-anchor", "value");
    let mut result = run_backup(engine, BackupPlan::full(), destination).expect("full seals");
    identities.push(result.manifest().identity().to_owned());
    for index in 0..increments {
        put(engine, "alpha", &format!("seed-{index}"), &format!("doc-{index}"), "value");
        result = run_backup(
            engine,
            BackupPlan::incremental_since(result.manifest().boundary_sequence()),
            destination,
        )
        .expect("incremental seals");
        identities.push(result.manifest().identity().to_owned());
    }
    identities
}

/// Asserts no 16-byte window of `material` appears anywhere in `bytes`.
fn assert_windows_absent(bytes: &[u8], material: &[u8], what: &str) {
    if material.len() < 16 {
        return;
    }
    for start in 0..=(material.len() - 16) {
        assert!(
            !bytes.windows(16).any(|window| window == &material[start..start + 16]),
            "a 16-byte window of the key material appears in {what}"
        );
    }
}

// ---------------------------------------------------------------------
// Destination configuration (PB-6012, local kind)
// ---------------------------------------------------------------------

#[test]
fn destination_validation_rejects_relative_empty_traversal_and_root_paths() {
    for (path, reason) in [
        ("", DestinationInvalid::EmptyPath),
        ("relative/dest", DestinationInvalid::RelativePath),
        ("/dest/../escape", DestinationInvalid::ParentTraversal),
        ("/", DestinationInvalid::RootPath),
    ] {
        let config = DestinationConfig::local(path);
        assert_eq!(
            config.validate().err(),
            Some(DestinationError::Invalid(reason)),
            "path {path:?} must fail validation with its typed reason"
        );
    }
    let valid = DestinationConfig::local(location("dest-valid"));
    assert!(valid.validate().is_ok(), "an absolute leaf path validates");
    assert_eq!(valid.kind(), DestinationKind::LocalFileSystem);
}

#[test]
fn destination_validation_rejects_an_existing_file_at_the_root() {
    let file = location("dest-file");
    std::fs::write(&file, b"not a directory").expect("writes the blocker");
    let config = DestinationConfig::local(&file);
    assert_eq!(
        config.validate().err(),
        Some(DestinationError::Invalid(DestinationInvalid::NotADirectory)),
        "a file where the destination root should be fails typed"
    );
}

#[test]
fn destination_probe_creates_the_root_and_proves_writability() {
    let root = location("dest-probe").join("nested/root");
    let config = DestinationConfig::local(&root);
    config.probe().expect("the probe creates and writes");
    assert!(root.is_dir(), "the root exists after the probe");
    config.probe().expect("the probe is idempotent");
    assert!(
        root.read_dir().expect("reads the root").next().is_none(),
        "the probe leaves no residue"
    );
}

// ---------------------------------------------------------------------
// Compression and encryption (PB-6013)
// ---------------------------------------------------------------------

#[test]
fn compression_round_trips_a_varied_corpus() {
    let mut state: u64 = 0x2545_F491_4F6C_DD1D;
    let mut next = move || {
        state ^= state << 13;
        state ^= state >> 7;
        state ^= state << 17;
        state
    };
    let pseudo_random: Vec<u8> =
        (0..200_000u32).map(|_| u8::try_from(next() % 256).expect("below 256")).collect();
    let repetitive: Vec<u8> =
        b"the same bytes over and over ".iter().copied().cycle().take(96_000).collect();
    let corpus: Vec<Vec<u8>> =
        vec![Vec::new(), vec![b'a'], repetitive, pseudo_random, vec![b'z'; 70_000]];
    for input in &corpus {
        let compressed = compress(input);
        assert_eq!(
            decompress(&compressed).as_deref(),
            Ok(input.as_slice()),
            "a corpus member of {} bytes round-trips",
            input.len()
        );
    }
    let compressed = compress(&corpus[2]);
    assert!(
        compressed.len() < corpus[2].len(),
        "a repetitive member compresses below its raw length"
    );
    assert_eq!(
        decompress(b"\xff truncated flags").err(),
        Some(CompressionError::Malformed),
        "malformed compressed input fails typed"
    );
    assert_eq!(
        decompress(&[]).expect("an empty stream is the empty literal"),
        Vec::<u8>::new(),
        "the empty member round-trips as an empty stream"
    );
}

#[test]
fn encrypted_bundles_open_only_with_the_right_key() {
    let (_dir, engine) = open_engine("codec-key");
    put(&engine, "alpha", "op-1", "doc-1", "value-one");
    let destination = location("codec-key-dest");
    let result =
        run_backup(&engine, BackupPlan::full(), &destination).expect("the source backs up");
    let identity = result.manifest().identity().to_owned();

    let seal_key = key();
    let bundle =
        seal_artifact_bundle(&destination, &identity, BundleOptions::compressed(), Some(&seal_key))
            .expect("the sealed point bundles");
    assert_eq!(bundle.identity(), identity);
    assert_eq!(bundle.members().len(), result.manifest().members().len());

    let opened = open_artifact_bundle(&destination, &identity, Some(&seal_key))
        .expect("the right key opens the bundle");
    assert_eq!(opened, bundle);

    let wrong = key();
    assert_eq!(
        open_artifact_bundle(&destination, &identity, Some(&wrong)).err(),
        Some(EncryptionError::AuthenticationFailed),
        "a wrong key fails closed without disclosing why"
    );
    assert_eq!(
        open_artifact_bundle(&destination, &identity, None).err(),
        Some(EncryptionError::KeyRequired),
        "a missing key against an encrypted bundle is typed"
    );
}

#[test]
fn tampered_bundle_bytes_fail_closed() {
    let (_dir, engine) = open_engine("codec-tamper");
    put(&engine, "alpha", "op-1", "doc-1", "value-one");
    put(&engine, "alpha", "op-2", "doc-2", "value-two");
    let destination = location("codec-tamper-dest");
    let result =
        run_backup(&engine, BackupPlan::full(), &destination).expect("the source backs up");
    let identity = result.manifest().identity().to_owned();
    let seal_key = key();
    seal_artifact_bundle(&destination, &identity, BundleOptions::compressed(), Some(&seal_key))
        .expect("seals");
    let bundle_path = destination.join(format!("bundle-{identity}.tbv2b"));
    let pristine = std::fs::read(&bundle_path).expect("reads the bundle");

    // Flip one byte in the nonce, the ciphertext body, and the final
    // ciphertext byte: every covered byte is authenticated, so each flip
    // fails closed and never surfaces member bytes.
    for offset in [12usize, 130, pristine.len() - 1] {
        let mut tampered = pristine.clone();
        tampered[offset] ^= 0x01;
        std::fs::write(&bundle_path, &tampered).expect("writes the tampered bundle");
        assert_eq!(
            open_artifact_bundle(&destination, &identity, Some(&seal_key)).err(),
            Some(EncryptionError::AuthenticationFailed),
            "a flipped byte at {offset} fails closed"
        );
    }

    // A container that is not a bundle at all is unsupported, not authenticated.
    std::fs::write(&bundle_path, b"not a bundle").expect("writes a foreign container");
    assert_eq!(
        open_artifact_bundle(&destination, &identity, Some(&seal_key)).err(),
        Some(EncryptionError::UnsupportedFormat)
    );
}

#[test]
fn bundles_extract_into_verifiable_recovery_points() {
    let (_dir, engine) = open_engine("codec-extract");
    put(&engine, "alpha", "op-1", "doc-1", "value-one");
    put(&engine, "alpha", "op-2", "doc-2", "value-two");
    let destination = location("codec-extract-dest");
    let result =
        run_backup(&engine, BackupPlan::full(), &destination).expect("the source backs up");
    let identity = result.manifest().identity().to_owned();
    let seal_key = key();
    seal_artifact_bundle(&destination, &identity, BundleOptions::compressed(), Some(&seal_key))
        .expect("seals");

    let target = location("codec-extract-target");
    let extracted = extract_artifact_bundle(&destination, &identity, Some(&seal_key), &target)
        .expect("the right key extracts");
    assert_eq!(extracted.identity(), identity);

    // The extracted point is a complete, verifiable recovery point: the
    // manifest reloads and the chain verifies from the extraction target.
    let reloaded = load_recovery_point(&target, &identity).expect("reload");
    assert_eq!(reloaded, *result.manifest());
    let chain = verify_latest_chain(&target).expect("the extracted chain verifies");
    assert_eq!(chain.len(), 1);
    assert_eq!(chain[0].identity(), identity);

    let wrong = key();
    let denied = location("codec-extract-denied");
    assert_eq!(
        extract_artifact_bundle(&destination, &identity, Some(&wrong), &denied).err(),
        Some(EncryptionError::AuthenticationFailed),
        "a wrong key extracts nothing"
    );
    assert!(
        !denied.join(format!("rp-{identity}")).exists(),
        "no partial extraction survives a denied open"
    );
}

#[test]
fn compression_without_encryption_bundles_and_opens_keylessly() {
    let (_dir, engine) = open_engine("codec-plain");
    put(&engine, "alpha", "op-1", "doc-1", "value-one");
    let destination = location("codec-plain-dest");
    let result =
        run_backup(&engine, BackupPlan::full(), &destination).expect("the source backs up");
    let identity = result.manifest().identity().to_owned();

    seal_artifact_bundle(&destination, &identity, BundleOptions::compressed(), None)
        .expect("a plaintext-compressed bundle seals without a key");
    let opened = open_artifact_bundle(&destination, &identity, None).expect("no key is needed");
    assert_eq!(opened.identity(), identity);
    let target = location("codec-plain-target");
    extract_artifact_bundle(&destination, &identity, None, &target)
        .expect("extraction needs no key either");
    assert!(load_recovery_point(&target, &identity).is_ok());
}

#[test]
fn sealing_is_nondeterministic_but_every_seal_opens() {
    let (_dir, engine) = open_engine("codec-nonce");
    put(&engine, "alpha", "op-1", "doc-1", "value-one");
    let destination = location("codec-nonce-dest");
    let result =
        run_backup(&engine, BackupPlan::full(), &destination).expect("the source backs up");
    let identity = result.manifest().identity().to_owned();
    let seal_key = key();

    seal_artifact_bundle(&destination, &identity, BundleOptions::plaintext(), Some(&seal_key))
        .expect("the first seal");
    let first = std::fs::read(destination.join(format!("bundle-{identity}.tbv2b"))).expect("reads");
    seal_artifact_bundle(&destination, &identity, BundleOptions::plaintext(), Some(&seal_key))
        .expect("the second seal");
    let second =
        std::fs::read(destination.join(format!("bundle-{identity}.tbv2b"))).expect("reads");
    assert_ne!(first, second, "each seal draws a fresh nonce");
    assert!(
        open_artifact_bundle(&destination, &identity, Some(&seal_key)).is_ok(),
        "both seals open with the same key"
    );
}

#[test]
fn key_material_never_enters_artifacts_or_debug_output() {
    let (_dir, engine) = open_engine("codec-secrecy");
    put(&engine, "alpha", "op-1", "doc-1", "value-one");
    let destination = location("codec-secrecy-dest");
    let result =
        run_backup(&engine, BackupPlan::full(), &destination).expect("the source backs up");
    let identity = result.manifest().identity().to_owned();
    let mut material = [0u8; 32];
    for (index, byte) in material.iter_mut().enumerate() {
        *byte = u8::try_from((index * 37 + 11) % 256).expect("below 256");
    }
    let seal_key = key_from(&material);

    seal_artifact_bundle(&destination, &identity, BundleOptions::compressed(), Some(&seal_key))
        .expect("seals");
    let bundle_bytes =
        std::fs::read(destination.join(format!("bundle-{identity}.tbv2b"))).expect("reads");
    assert_windows_absent(&bundle_bytes, &material, "the sealed bundle");

    let debug = format!("{seal_key:?}");
    assert!(debug.contains("redacted"), "Debug names the redaction: {debug}");
    assert_windows_absent(debug.as_bytes(), &material, "the key's Debug output");
}

// ---------------------------------------------------------------------
// Retention (PB-6014)
// ---------------------------------------------------------------------

#[test]
fn retention_never_prunes_the_only_chain() {
    let (_dir, engine) = open_engine("retention-only");
    let destination = location("retention-only-dest");
    let identities = seed_chain(&engine, &destination, 2);
    for identity in &identities {
        pin_point_age(&destination, identity, Duration::from_secs(86_400 * 90));
    }

    let receipt = enforce_retention(
        &destination,
        &RetentionPolicy::min_age(Duration::from_secs(86_400 * 30)),
        SystemTime::now(),
    )
    .expect("retention runs");
    assert!(receipt.pruned().is_empty(), "the only chain is never pruned");
    assert_eq!(receipt.protected_roots().len(), 1, "the newest root is protected");
    assert_eq!(verify_latest_chain(&destination).expect("the chain still verifies").len(), 3);
}

#[test]
fn retention_prunes_whole_expired_chains_and_keeps_young_ones() {
    let (_dir, engine) = open_engine("retention-prune");
    let destination = location("retention-prune-dest");
    // Chain one: a full plus one incremental, sealed long ago.
    let old_chain = seed_chain(&engine, &destination, 1);
    // Chain two: the rotating full plus one incremental, sealed now.
    put(&engine, "alpha", "rotate", "doc-r", "value");
    let new_chain = seed_chain(&engine, &destination, 1);
    assert_eq!(old_chain.len() + new_chain.len(), 4);
    for identity in &old_chain {
        pin_point_age(&destination, identity, Duration::from_secs(86_400 * 40));
    }
    for identity in &new_chain {
        pin_point_age(&destination, identity, Duration::from_secs(60));
    }

    let receipt = enforce_retention(
        &destination,
        &RetentionPolicy::min_age(Duration::from_secs(86_400 * 30)),
        SystemTime::now(),
    )
    .expect("retention runs");

    let pruned: Vec<&str> = receipt.pruned().iter().map(PrunedPoint::identity).collect();
    assert_eq!(pruned.len(), old_chain.len(), "the whole expired chain goes");
    for identity in &old_chain {
        assert!(pruned.contains(&identity.as_str()), "the expired member {identity} is receipted");
        assert!(
            !point_manifest_path(&destination, identity).exists(),
            "the expired point directory is gone"
        );
    }
    for identity in &new_chain {
        assert!(!pruned.contains(&identity.as_str()), "the young chain stays");
        assert!(point_manifest_path(&destination, identity).exists());
    }
    let chain = verify_latest_chain(&destination).expect("the surviving chain verifies");
    assert_eq!(chain.len(), new_chain.len());
    assert_eq!(chain[0].identity(), new_chain[0]);

    // The same sweep again prunes nothing: retention is idempotent.
    let again = enforce_retention(
        &destination,
        &RetentionPolicy::min_age(Duration::from_secs(86_400 * 30)),
        SystemTime::now(),
    )
    .expect("retention reruns");
    assert!(again.pruned().is_empty());
}

#[test]
fn retention_prunes_sealed_bundles_with_their_points() {
    let (_dir, engine) = open_engine("retention-bundle");
    let destination = location("retention-bundle-dest");
    let seal_key = key();
    // Chain one: a full plus one incremental, sealed long ago, each point
    // carrying the encrypted, compressed bundle a scheduled run seals.
    let old_chain = seed_chain(&engine, &destination, 1);
    // Chain two: the rotating chain, sealed now.
    put(&engine, "alpha", "rotate", "doc-r", "value");
    let new_chain = seed_chain(&engine, &destination, 1);
    for identity in old_chain.iter().chain(new_chain.iter()) {
        seal_artifact_bundle(&destination, identity, BundleOptions::compressed(), Some(&seal_key))
            .expect("the scheduled shape seals one bundle per point");
    }
    for identity in &old_chain {
        pin_point_age(&destination, identity, Duration::from_secs(86_400 * 40));
    }
    for identity in &new_chain {
        pin_point_age(&destination, identity, Duration::from_secs(60));
    }

    let receipt = enforce_retention(
        &destination,
        &RetentionPolicy::min_age(Duration::from_secs(86_400 * 30)),
        SystemTime::now(),
    )
    .expect("retention runs");

    let pruned: Vec<&str> = receipt.pruned().iter().map(PrunedPoint::identity).collect();
    assert_eq!(pruned.len(), old_chain.len(), "the whole expired chain goes");
    for identity in &old_chain {
        assert!(pruned.contains(&identity.as_str()), "the expired member {identity} is receipted");
        assert!(
            !point_manifest_path(&destination, identity).exists(),
            "the expired point directory is gone"
        );
        assert!(
            !bundle_path(&destination, identity).exists(),
            "the expired point's sealed bundle is reclaimed with it"
        );
        assert_eq!(
            open_artifact_bundle(&destination, identity, Some(&seal_key)).err(),
            Some(EncryptionError::DestinationUnavailable),
            "the pruned point is unrecoverable from its bundle"
        );
        assert!(
            load_recovery_point(&destination, identity).is_err(),
            "the pruned point no longer reloads"
        );
    }
    for identity in &new_chain {
        assert!(
            bundle_path(&destination, identity).is_file(),
            "the kept chain's bundles survive the sweep"
        );
        assert!(
            open_artifact_bundle(&destination, identity, Some(&seal_key)).is_ok(),
            "the kept chain's bundles still open with the right key"
        );
    }

    // The same sweep again prunes nothing: bundle reclamation is idempotent.
    let again = enforce_retention(
        &destination,
        &RetentionPolicy::min_age(Duration::from_secs(86_400 * 30)),
        SystemTime::now(),
    )
    .expect("retention reruns");
    assert!(again.pruned().is_empty());
}

#[test]
fn retention_keeps_the_newest_chain_s_bundles_whatever_their_age() {
    let (_dir, engine) = open_engine("retention-bundle-keep");
    let destination = location("retention-bundle-keep-dest");
    let identities = seed_chain(&engine, &destination, 2);
    let seal_key = key();
    for identity in &identities {
        seal_artifact_bundle(&destination, identity, BundleOptions::compressed(), Some(&seal_key))
            .expect("seals");
        pin_point_age(&destination, identity, Duration::from_secs(86_400 * 90));
    }

    let receipt = enforce_retention(
        &destination,
        &RetentionPolicy::min_age(Duration::from_secs(86_400 * 30)),
        SystemTime::now(),
    )
    .expect("retention runs");
    assert!(receipt.pruned().is_empty(), "the newest chain is never pruned");
    assert_eq!(receipt.protected_roots().len(), 1);
    for identity in &identities {
        assert!(point_manifest_path(&destination, identity).exists());
        assert!(
            bundle_path(&destination, identity).is_file(),
            "the protected chain keeps its bundle artifacts"
        );
    }
    assert!(
        open_artifact_bundle(&destination, &identities[0], Some(&seal_key)).is_ok(),
        "the protected chain's root bundle still opens"
    );
}

#[test]
fn retention_keeps_an_expired_shape_below_the_minimum_age() {
    let (_dir, engine) = open_engine("retention-young");
    let destination = location("retention-young-dest");
    let old_chain = seed_chain(&engine, &destination, 1);
    put(&engine, "alpha", "rotate", "doc-r", "value");
    let new_chain = seed_chain(&engine, &destination, 0);
    for identity in &old_chain {
        pin_point_age(&destination, identity, Duration::from_secs(86_400 * 10));
    }

    let receipt = enforce_retention(
        &destination,
        &RetentionPolicy::min_age(Duration::from_secs(86_400 * 30)),
        SystemTime::now(),
    )
    .expect("retention runs");
    assert!(
        receipt.pruned().is_empty(),
        "a chain younger than the minimum age is kept however it compares"
    );
    assert!(point_manifest_path(&destination, &old_chain[0]).exists());
    assert!(point_manifest_path(&destination, &new_chain[0]).exists());
}

#[test]
fn retention_fails_closed_on_an_invalid_destination_manifest() {
    let (_dir, engine) = open_engine("retention-invalid");
    let destination = location("retention-invalid-dest");
    seed_chain(&engine, &destination, 0);
    let broken = destination.join(format!("rp-{}", "ab".repeat(32)));
    std::fs::create_dir_all(&broken).expect("creates the point directory");
    std::fs::write(broken.join("manifest.tbv2m"), b"not a manifest\n").expect("breaks it");

    let failure = enforce_retention(
        &destination,
        &RetentionPolicy::min_age(Duration::from_secs(1)),
        SystemTime::now(),
    )
    .expect_err("an invalid sealed manifest fails the sweep");
    assert_eq!(failure.cause(), RetentionFailure::ManifestInvalid);
    assert!(failure.pruned_before_failure().is_empty(), "nothing was deleted");
    assert!(broken.exists(), "the failing destination is untouched");
}

#[test]
fn disabled_retention_prunes_nothing() {
    let (_dir, engine) = open_engine("retention-disabled");
    let destination = location("retention-disabled-dest");
    let identities = seed_chain(&engine, &destination, 1);
    put(&engine, "alpha", "rotate", "doc-r", "value");
    seed_chain(&engine, &destination, 0);
    let receipt = enforce_retention(&destination, &RetentionPolicy::disabled(), SystemTime::now())
        .expect("the sweep runs");
    assert!(receipt.pruned().is_empty());
    for identity in &identities {
        assert!(point_manifest_path(&destination, identity).exists());
    }
}

// ---------------------------------------------------------------------
// Scheduling (PB-6011)
// ---------------------------------------------------------------------

#[test]
fn a_new_schedule_requires_a_full_before_any_incremental() {
    let (_dir, engine) = open_engine("sched-first");
    let destination = location("sched-first-dest");
    let driver = driver(&destination, schedule());
    let mut state = ScheduleState::new(0);

    put(&engine, "alpha", "op-1", "doc-1", "value-one");
    assert_eq!(driver.evaluate(&state, &engine, 50), ScheduleDecision::Run(BackupKind::Full));
    assert_eq!(driver.status(&state, 0).phase(), SchedulePhase::NeedsFirstFull);

    let outcome = driver
        .run_due(&engine, &mut state, 50, None)
        .expect("the first due run executes")
        .expect("a run was due");
    assert_eq!(outcome.run().kind(), BackupKind::Full);
    assert_eq!(state.last_point().expect("recorded").kind(), BackupKind::Full);
    assert_eq!(state.completed_runs(), 1);
    assert_eq!(verify_latest_chain(&destination).expect("the chain verifies").len(), 1);
}

#[test]
fn scheduled_incrementals_chain_until_the_next_full_rotates() {
    let (_dir, engine) = open_engine("sched-chain");
    let destination = location("sched-chain-dest");
    let driver = driver(&destination, schedule());
    let mut state = ScheduleState::new(0);
    put(&engine, "alpha", "op-1", "doc-1", "value-one");

    driver.run_due(&engine, &mut state, 0, None).expect("the first full runs").expect("due");
    let full_boundary = state.last_point().expect("the full").boundary();

    // First incremental: new journal exists, the base is the full.
    put(&engine, "alpha", "op-2", "doc-2", "value-two");
    assert_eq!(
        driver.evaluate(&state, &engine, 300),
        ScheduleDecision::Run(BackupKind::Incremental { base: full_boundary })
    );
    let first =
        driver.run_due(&engine, &mut state, 300, None).expect("the incremental runs").expect("due");
    assert_eq!(first.run().kind(), BackupKind::Incremental { base: full_boundary });
    let first_boundary = first.run().boundary();

    // Second incremental: chains on the previous incremental's boundary.
    put(&engine, "alpha", "op-3", "doc-3", "value-three");
    assert_eq!(
        driver.evaluate(&state, &engine, 600),
        ScheduleDecision::Run(BackupKind::Incremental { base: first_boundary })
    );
    driver.run_due(&engine, &mut state, 600, None).expect("runs").expect("due");

    // The full interval arrives and rotates the chain.
    put(&engine, "alpha", "op-4", "doc-4", "value-four");
    assert_eq!(driver.evaluate(&state, &engine, 1_000), ScheduleDecision::Run(BackupKind::Full));
    driver.run_due(&engine, &mut state, 1_000, None).expect("runs").expect("due");

    // The next incremental bases on the rotating full, not the old chain.
    put(&engine, "alpha", "op-5", "doc-5", "value-five");
    let rotated_boundary = state.last_point().expect("the rotated full").boundary();
    assert_eq!(
        driver.evaluate(&state, &engine, 1_300),
        ScheduleDecision::Run(BackupKind::Incremental { base: rotated_boundary })
    );
    driver.run_due(&engine, &mut state, 1_300, None).expect("runs").expect("due");

    assert_eq!(state.completed_runs(), 5);
    // The destination holds both chains; the newest is the rotated full
    // plus its one link — the first chain stays behind it.
    let chain = verify_latest_chain(&destination).expect("the full chain verifies");
    assert_eq!(chain.len(), 2, "the newest chain is the rotated full plus one link");
    assert_eq!(chain[0].kind(), BackupKind::Full);
    assert_eq!(chain[0].boundary_sequence(), rotated_boundary);
    assert_eq!(chain[1].kind(), BackupKind::Incremental { base: rotated_boundary });
}

#[test]
fn a_due_incremental_without_new_journal_is_a_noop_that_rearms() {
    let (_dir, engine) = open_engine("sched-noop");
    let destination = location("sched-noop-dest");
    let driver = driver(&destination, schedule());
    let mut state = ScheduleState::new(0);
    put(&engine, "alpha", "op-1", "doc-1", "value-one");
    driver.run_due(&engine, &mut state, 0, None).expect("the first full runs").expect("due");
    let base = state.last_point().expect("the full").boundary();

    // No journal growth: the due window produces no run.
    assert_eq!(
        driver.evaluate(&state, &engine, 300),
        ScheduleDecision::Idle { next_due: Some(1_000) },
        "a due incremental with no new journal is not runnable"
    );
    assert!(
        driver.run_due(&engine, &mut state, 300, None).expect("runs").is_none(),
        "the noop window seals nothing"
    );
    assert_eq!(state.skipped_noop_runs(), 1);
    assert_eq!(state.completed_runs(), 1);

    // New journal arrives inside the rearmed window: not due before it.
    put(&engine, "alpha", "op-2", "doc-2", "value-two");
    assert_eq!(
        driver.evaluate(&state, &engine, 599),
        ScheduleDecision::Idle { next_due: Some(600) }
    );
    assert_eq!(
        driver.evaluate(&state, &engine, 600),
        ScheduleDecision::Run(BackupKind::Incremental { base })
    );
}

#[test]
fn missed_run_policy_run_one_now_executes_a_late_full_immediately() {
    let (_dir, engine) = open_engine("sched-late");
    let destination = location("sched-late-dest");
    let driver = driver(&destination, schedule());
    let mut state = ScheduleState::new(0);
    put(&engine, "alpha", "op-1", "doc-1", "value-one");
    driver.run_due(&engine, &mut state, 0, None).expect("the first full runs").expect("due");

    // The scheduler was not ticking: a whole full window passed.
    put(&engine, "alpha", "op-2", "doc-2", "value-two");
    assert_eq!(driver.evaluate(&state, &engine, 2_500), ScheduleDecision::Run(BackupKind::Full));
    driver
        .run_due(&engine, &mut state, 2_500, None)
        .expect("the missed full runs once, immediately")
        .expect("due");
    assert_eq!(state.completed_runs(), 2, "no backfill mints a second make-up run");
    assert_eq!(
        driver.evaluate(&state, &engine, 2_501),
        ScheduleDecision::Idle { next_due: Some(2_800) },
        "the anchor moved to the completion time"
    );
}

#[test]
fn missed_run_policy_skip_until_next_window_does_not_backfill() {
    let (_dir, engine) = open_engine("sched-skip");
    let destination = location("sched-skip-dest");
    let schedule = BackupSchedule::full_every(1_000)
        .with_missed_run_policy(MissedRunPolicy::SkipUntilNextWindow);
    let driver = driver(&destination, schedule);
    let mut state = ScheduleState::new(0);
    put(&engine, "alpha", "op-1", "doc-1", "value-one");
    driver.run_due(&engine, &mut state, 0, None).expect("the first full runs").expect("due");

    // A whole window was missed: no backfill, the next aligned slot runs.
    assert_eq!(
        driver.evaluate(&state, &engine, 2_500),
        ScheduleDecision::Idle { next_due: Some(3_000) }
    );
    assert!(
        driver.run_due(&engine, &mut state, 2_500, None).expect("runs").is_none(),
        "the missed window seals nothing"
    );
    assert_eq!(state.completed_runs(), 1);
    assert_eq!(
        driver.evaluate(&state, &engine, 2_999),
        ScheduleDecision::Idle { next_due: Some(3_000) }
    );
    put(&engine, "alpha", "op-2", "doc-2", "value-two");
    assert_eq!(driver.evaluate(&state, &engine, 3_000), ScheduleDecision::Run(BackupKind::Full));
    driver.run_due(&engine, &mut state, 3_000, None).expect("the aligned full runs").expect("due");
    assert_eq!(state.completed_runs(), 2);

    // A late tick inside the window does not backfill under the skip
    // policy: it waits for the next boundary, and the grid never drifts.
    put(&engine, "alpha", "op-3", "doc-3", "value-three");
    assert_eq!(
        driver.evaluate(&state, &engine, 4_100),
        ScheduleDecision::Idle { next_due: Some(5_000) }
    );
    assert!(driver.run_due(&engine, &mut state, 4_100, None).expect("runs").is_none());

    // The same state under RunOneNow runs the late boundary immediately:
    // the policies differ exactly in whether a late boundary is owed.
    let catch_up = ScheduleDriver::new(
        BackupSchedule::full_every(1_000).with_missed_run_policy(MissedRunPolicy::RunOneNow),
        DestinationConfig::local(&destination),
    )
    .expect("a valid catch-up driver builds");
    assert_eq!(catch_up.evaluate(&state, &engine, 4_100), ScheduleDecision::Run(BackupKind::Full));
    catch_up
        .run_due(&engine, &mut state, 4_100, None)
        .expect("the catch-up run executes")
        .expect("due");
    assert_eq!(state.completed_runs(), 3);
}

#[test]
fn pause_blocks_runs_and_resume_reapplies_the_missed_run_policy() {
    let (_dir, engine) = open_engine("sched-pause");
    let destination = location("sched-pause-dest");
    let driver = driver(&destination, schedule());
    let mut state = ScheduleState::new(0);
    put(&engine, "alpha", "op-1", "doc-1", "value-one");
    driver.run_due(&engine, &mut state, 0, None).expect("the first full runs").expect("due");

    driver.pause(&mut state, 500).expect("pauses");
    assert_eq!(driver.evaluate(&state, &engine, 1_200), ScheduleDecision::Paused);
    assert!(
        driver.run_due(&engine, &mut state, 1_200, None).expect("runs").is_none(),
        "a paused schedule never runs"
    );
    assert_eq!(state.completed_runs(), 1);
    assert_eq!(driver.status(&state, 1_200).phase(), SchedulePhase::Paused { since: 500 });

    // Pausing twice keeps the original pause fact; resuming is typed.
    driver.pause(&mut state, 900).expect("pausing twice is idempotent");
    assert_eq!(driver.status(&state, 950).phase(), SchedulePhase::Paused { since: 500 });
    driver.resume(&mut state, 1_300).expect("resumes");
    put(&engine, "alpha", "op-2", "doc-2", "value-two");
    assert_eq!(driver.evaluate(&state, &engine, 1_300), ScheduleDecision::Run(BackupKind::Full));
    driver
        .run_due(&engine, &mut state, 1_300, None)
        .expect("the overdue full runs under RunOneNow after resume")
        .expect("due");
    assert_eq!(state.completed_runs(), 2);
}

#[test]
fn an_invalid_schedule_or_destination_fails_typed_at_construction() {
    assert_eq!(
        ScheduleDriver::new(
            BackupSchedule::full_every(0),
            DestinationConfig::local(location("invalid-zero")),
        )
        .err(),
        Some(ScheduleError::Invalid(ScheduleInvalid::ZeroInterval))
    );
    assert_eq!(
        ScheduleDriver::new(BackupSchedule::full_every(10), DestinationConfig::local("relative"),)
            .err(),
        Some(ScheduleError::Invalid(ScheduleInvalid::Destination(
            DestinationInvalid::RelativePath
        )))
    );
}

#[test]
fn run_failures_are_typed_and_recorded_in_status() {
    let (_dir, engine) = open_engine("sched-failure");
    let destination = location("sched-failure-dest");
    let driver = driver(&destination, schedule());
    let mut state = ScheduleState::new(0);
    put(&engine, "alpha", "op-1", "doc-1", "value-one");
    driver.run_due(&engine, &mut state, 0, None).expect("the first full runs").expect("due");
    let identity = state.last_point().expect("the full").identity().to_owned();

    // External destination damage: the chain base disappears, so the due
    // incremental fails closed at the engine seam and the schedule records
    // the typed failure instead of minting an unanchored point.
    std::fs::remove_dir_all(destination.join(format!("rp-{identity}")))
        .expect("the point directory existed");
    put(&engine, "alpha", "op-2", "doc-2", "value-two");
    let failure = driver
        .run_due(&engine, &mut state, 300, None)
        .expect_err("the unanchored incremental fails");
    assert_eq!(failure.cause(), ScheduleError::Backup(BackupError::ChainIncomplete));
    assert!(failure.completed_run().is_none());
    assert_eq!(
        state.last_error().expect("recorded").cause(),
        ScheduleError::Backup(BackupError::ChainIncomplete)
    );
    assert_eq!(
        driver.status(&state, 301).last_error().expect("recorded").cause(),
        ScheduleError::Backup(BackupError::ChainIncomplete)
    );
}

#[test]
fn encrypted_schedules_seal_a_bundle_per_run_and_report_no_secrets() {
    let (_dir, engine) = open_engine("sched-encrypted");
    let destination = location("sched-encrypted-dest");
    let schedule = schedule()
        .with_encryption(true)
        .with_compression(true)
        .with_retention(RetentionPolicy::disabled());
    let driver = driver(&destination, schedule);
    let mut state = ScheduleState::new(0);
    put(&engine, "alpha", "op-1", "doc-1", "value-one");

    // Encryption on but no key supplied: typed failure, run still recorded.
    let missing = driver
        .run_due(&engine, &mut state, 0, None)
        .expect_err("the bundle cannot seal without the key");
    assert_eq!(missing.cause(), ScheduleError::Invalid(ScheduleInvalid::EncryptionKeyRequired));
    assert!(missing.completed_run().is_some(), "the backup itself sealed");
    assert_eq!(state.completed_runs(), 1);

    let mut material = [0u8; 32];
    for (index, byte) in material.iter_mut().enumerate() {
        *byte = u8::try_from((index * 41 + 7) % 256).expect("below 256");
    }
    let seal_key = key_from(&material);
    put(&engine, "alpha", "op-2", "doc-2", "value-two");
    let outcome = driver
        .run_due(&engine, &mut state, 1_000, Some(&seal_key))
        .expect("the rotating full runs and bundles")
        .expect("due");
    let bundle = outcome.bundle().expect("an encrypted schedule seals a bundle");
    let identity = outcome.run().identity().to_owned();
    assert!(destination.join(format!("bundle-{identity}.tbv2b")).is_file());

    let opened = open_artifact_bundle(&destination, bundle.identity(), Some(&seal_key))
        .expect("the sealed bundle opens with the right key");
    assert_eq!(opened, *bundle);
    let bundle_bytes =
        std::fs::read(destination.join(format!("bundle-{identity}.tbv2b"))).expect("reads");
    assert_windows_absent(&bundle_bytes, &material, "the scheduled bundle");

    let status = driver.status(&state, 1_001);
    assert_eq!(status.phase(), SchedulePhase::Idle { next_due: Some(1_300) });
    assert!(status.encryption_enabled());
    assert!(status.compression_enabled());
    let text = format!("{status:?}");
    let destination_text = destination.to_string_lossy();
    assert!(!text.contains(destination_text.as_ref()), "status carries no destination path");
    assert_windows_absent(text.as_bytes(), &material, "the status readout");
}

#[test]
fn scheduled_retention_reclaims_the_rotated_out_chain_s_bundles() {
    let (_dir, engine) = open_engine("sched-bundle-reclaim");
    let destination = location("sched-bundle-reclaim-dest");
    let schedule = BackupSchedule::full_every(1_000)
        .with_missed_run_policy(MissedRunPolicy::RunOneNow)
        .with_compression(true)
        .with_encryption(true)
        .with_retention(RetentionPolicy::min_age(Duration::from_secs(3_600)));
    let driver = driver(&destination, schedule);
    let mut state = ScheduleState::new(0);
    let seal_key = key();
    put(&engine, "alpha", "op-1", "doc-1", "value-one");

    let first = driver
        .run_due(&engine, &mut state, 0, Some(&seal_key))
        .expect("the first full runs and bundles")
        .expect("due");
    let first_identity = first.run().identity().to_owned();
    assert!(bundle_path(&destination, &first_identity).is_file(), "the first run sealed a bundle");
    pin_point_age(&destination, &first_identity, Duration::from_secs(7_200));

    put(&engine, "alpha", "rotate", "doc-r", "value");
    let rotated = driver
        .run_due(&engine, &mut state, 1_000, Some(&seal_key))
        .expect("the rotating full runs and bundles")
        .expect("due");
    let rotated_identity = rotated.run().identity().to_owned();

    let retention = rotated.retention().expect("the run enforced retention");
    assert_eq!(retention.pruned().len(), 1, "the expired first full was pruned");
    assert!(
        !point_manifest_path(&destination, &first_identity).exists(),
        "the expired point is gone"
    );
    assert!(
        !bundle_path(&destination, &first_identity).exists(),
        "its sealed bundle is reclaimed with it"
    );
    assert_eq!(
        open_artifact_bundle(&destination, &first_identity, Some(&seal_key)).err(),
        Some(EncryptionError::DestinationUnavailable),
        "the pruned point is unrecoverable from its bundle"
    );
    assert!(
        bundle_path(&destination, &rotated_identity).is_file(),
        "the surviving chain keeps its bundle"
    );
    assert!(
        open_artifact_bundle(&destination, &rotated_identity, Some(&seal_key)).is_ok(),
        "the surviving bundle opens with the right key"
    );
}

#[test]
fn schedules_with_retention_enforce_it_after_each_run() {
    let (_dir, engine) = open_engine("sched-retention");
    let destination = location("sched-retention-dest");
    let schedule = BackupSchedule::full_every(1_000)
        .with_missed_run_policy(MissedRunPolicy::RunOneNow)
        .with_retention(RetentionPolicy::min_age(Duration::from_secs(3_600)));
    let driver = driver(&destination, schedule);
    let mut state = ScheduleState::new(0);
    put(&engine, "alpha", "op-1", "doc-1", "value-one");

    driver.run_due(&engine, &mut state, 0, None).expect("the first full runs").expect("due");
    let first_identity = state.last_point().expect("the full").identity().to_owned();
    pin_point_age(&destination, &first_identity, Duration::from_secs(7_200));

    put(&engine, "alpha", "rotate", "doc-r", "value");
    driver.run_due(&engine, &mut state, 1_000, None).expect("the rotating full runs").expect("due");

    let status = driver.status(&state, 1_001);
    let summary = status.last_retention().expect("the run enforced retention");
    assert_eq!(summary.pruned(), 1, "the expired first full was pruned");
    assert!(
        !point_manifest_path(&destination, &first_identity).exists(),
        "the expired point is gone"
    );
    assert!(!verify_latest_chain(&destination).expect("the surviving chain verifies").is_empty());
}
