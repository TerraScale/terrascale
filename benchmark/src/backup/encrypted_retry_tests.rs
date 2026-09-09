//! Regression coverage for encrypted retry publication.

use super::tests::{committed, location, open_engine, put, text};
use crate::backup::{BackupEncryption, BackupPlan, run_backup};
use crate::encryption::{
    DatabaseKey, DatabaseKeyLease, DatabaseKeyVersion, KeyPurpose, KeyScope, KeyVersionMetadata,
};
use crate::regions::RegionCode;
use crate::storage::DatabaseOwner;
use crate::types::LocalDurability;

fn backup_lease(owner: &DatabaseOwner) -> DatabaseKeyLease {
    let scope = KeyScope::new_with_project(
        owner.environment().as_str(),
        owner.tenant(),
        owner.project(),
        owner.database(),
        KeyPurpose::Backup,
    )
    .expect("backup scope");
    DatabaseKeyLease::new(
        KeyVersionMetadata::new(scope, DatabaseKeyVersion::generate()),
        DatabaseKey::from_bytes([41; 32]),
        std::time::Duration::from_secs(30),
    )
    .expect("backup lease")
}

#[test]
fn encrypted_retry_consumes_a_new_ordinal_but_keeps_published_point() {
    let (_source_dir, engine) = open_engine("encrypted-retry");
    let owner = engine.database_owner();
    committed(put(
        &engine,
        "alpha",
        "encrypted-retry-put",
        "doc-1",
        "users",
        text("one"),
        LocalDurability::Synced,
    ));
    let lease = backup_lease(owner);
    let encryption = BackupEncryption::from_database_key_lease(&lease, owner.region(), 0)
        .expect("backup encryption");
    let destination = location("encrypted-retry-destination");
    let capture_time = u64::try_from(
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .expect("wall clock")
            .as_millis(),
    )
    .expect("timestamp");
    let plan = || BackupPlan::full(encryption.clone()).with_capture_unix_millis(capture_time);

    let first = run_backup(&engine, plan(), &destination).expect("first encrypted backup");
    let point = destination.join(format!("rp-{}", first.manifest().identity()));
    let first_member =
        std::fs::read(point.join(first.manifest().members()[0].name())).expect("first member");
    let first_identity = first.manifest().identity().to_owned();
    let ordinal_after_first = encryption.cipher.lock().expect("cipher lock").next_ordinal();

    let retry = run_backup(&engine, plan(), &destination).expect("encrypted retry");
    let ordinal_after_retry = encryption.cipher.lock().expect("cipher lock").next_ordinal();
    let retry_member =
        std::fs::read(point.join(first.manifest().members()[0].name())).expect("published member");

    assert_eq!(retry.manifest().identity(), first_identity);
    assert_eq!(ordinal_after_retry, ordinal_after_first * 2);
    assert_ne!(ordinal_after_first, 0, "the first seal must consume an ordinal");
    assert_eq!(retry_member, first_member, "retry must leave the published point unchanged");
    assert_eq!(
        destination
            .read_dir()
            .expect("destination")
            .filter(|entry| entry
                .as_ref()
                .is_ok_and(|entry| entry.file_name().to_string_lossy().starts_with("rp-")))
            .count(),
        1
    );
}

#[test]
fn encrypted_retry_uses_retained_reader_for_a_fresh_artifact_context() {
    let (_source_dir, engine) = open_engine("encrypted-retry-fresh-context");
    let owner = engine.database_owner();
    committed(put(
        &engine,
        "alpha",
        "encrypted-retry-fresh-put",
        "doc-1",
        "users",
        text("one"),
        LocalDurability::Synced,
    ));
    let lease = Arc::new(backup_lease(owner));
    let destination = location("encrypted-retry-fresh-context-destination");
    let capture_time = u64::try_from(
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .expect("wall clock")
            .as_millis(),
    )
    .expect("timestamp");
    let first = run_backup(
        &engine,
        BackupPlan::full(
            BackupEncryption::from_database_key_lease(&lease, RegionCode::GRU, 0)
                .expect("first encryption"),
        )
        .with_capture_unix_millis(capture_time),
        &destination,
    )
    .expect("first backup");
    let retry = run_backup(
        &engine,
        BackupPlan::full(
            BackupEncryption::from_database_key_lease(&lease, RegionCode::GRU, 0)
                .expect("fresh encryption"),
        )
        .with_capture_unix_millis(capture_time)
        .with_reader(lease),
        &destination,
    )
    .expect("fresh-context retry");
    assert_eq!(retry.manifest().identity(), first.manifest().identity());
    assert_eq!(
        destination
            .read_dir()
            .expect("destination")
            .filter(|entry| entry
                .as_ref()
                .is_ok_and(|entry| entry.file_name().to_string_lossy().starts_with("rp-")))
            .count(),
        1
    );
}
use std::sync::Arc;
