//! Integration coverage for encrypted native snapshot object backup.

use std::sync::Arc;
use std::time::Duration;

use terrabase::backup::{BackupReader, PlacementLock};
use terrabase::encryption::{
    DatabaseKey, DatabaseKeyLease, DatabaseKeySource, DatabaseKeyVersion, EncryptionError,
    KeyPurpose, KeyScope, KeyVersionMetadata,
};
use terrabase::identity::MemberUlid;
use terrabase::regions::RegionCode;
use terrabase_cluster::production::{
    DatabaseRouteScope, MemberWorkloadIdentity, ProductionPlacement, ProductionRegionSeats,
};
use terrabase_cluster::wal_route::WalReplicaBinding;
use terrabase_cluster::wal_snapshot::WalSnapshotSource;

#[derive(Debug)]
struct StaticKeys(Arc<DatabaseKeyLease>);

impl DatabaseKeySource for StaticKeys {
    fn current_lease(&self) -> Result<Arc<DatabaseKeyLease>, EncryptionError> {
        Ok(Arc::clone(&self.0))
    }

    fn lease(&self, version: DatabaseKeyVersion) -> Result<Arc<DatabaseKeyLease>, EncryptionError> {
        if version == self.0.metadata().version() {
            Ok(Arc::clone(&self.0))
        } else {
            Err(EncryptionError::InvalidKeyMetadata)
        }
    }
}

async fn fixture() -> (
    WalSnapshotSource,
    WalReplicaBinding,
    Arc<DatabaseKeyLease>,
    Arc<DatabaseKeyLease>,
    std::path::PathBuf,
) {
    let writer = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
    let follower = "01ARZ3NDEKTSV4RRFFQ69G5FAW";
    let scope = DatabaseRouteScope::try_new(writer, writer, writer).unwrap();
    let placement = ProductionPlacement::try_new(
        MemberUlid::from_text(writer).unwrap(),
        1,
        7,
        [ProductionRegionSeats::try_new(
            RegionCode::GRU,
            [MemberUlid::from_text(writer).unwrap(), MemberUlid::from_text(follower).unwrap()],
        )
        .unwrap()],
    )
    .unwrap();
    let binding = WalReplicaBinding::new(
        scope,
        &placement,
        MemberWorkloadIdentity::try_new("prod", "cluster", writer).unwrap(),
        MemberWorkloadIdentity::try_new("prod", "cluster", follower).unwrap(),
    )
    .unwrap();
    let root = std::env::temp_dir()
        .join(format!("tb-native-snapshot-{}", terrabase::identity::generate()));
    let engine = Arc::new(
        terrabase::Engine::open(terrabase::EngineConfig::durable(root.join("writer")).unwrap())
            .unwrap(),
    );
    let tenant = engine.scope_for(&terrabase::types::TenantId::try_new(writer).unwrap());
    let mut tx = engine
        .begin(
            &tenant,
            engine.read_snapshot(&tenant).unwrap(),
            terrabase::types::OperationId::try_new("snapshot-test").unwrap(),
        )
        .unwrap();
    tx.put(
        terrabase::types::Document::new(
            terrabase::types::DocumentId::try_new("doc").unwrap(),
            terrabase::types::CollectionName::try_new("docs").unwrap(),
            terrabase::types::DocumentValue::String("snapshot-content".into()),
        ),
        terrabase::types::ExpectedVersion::New,
    )
    .unwrap();
    assert!(matches!(
        tx.commit(terrabase::types::LocalDurability::Synced),
        terrabase::CommitOutcome::Committed(_)
    ));
    let key_scope = KeyScope::new_with_project(
        "prod",
        terrabase::identity::TenantUlid::from_text(writer).unwrap(),
        terrabase::identity::ProjectUlid::from_text(writer).unwrap(),
        terrabase::identity::DatabaseUlid::from_text(writer).unwrap(),
        KeyPurpose::Backup,
    )
    .unwrap();
    let metadata = KeyVersionMetadata::new(
        key_scope.clone(),
        DatabaseKeyVersion::from_text("01ARZ3NDEKTSV4RRFFQ69G5FB0").unwrap(),
    );
    let lease = Arc::new(
        DatabaseKeyLease::test_oci_vault(
            metadata,
            DatabaseKey::from_bytes([31; 32]),
            Duration::from_secs(60),
        )
        .unwrap(),
    );
    let data_scope = KeyScope::new_with_project(
        "prod",
        terrabase::identity::TenantUlid::from_text(writer).unwrap(),
        terrabase::identity::ProjectUlid::from_text(writer).unwrap(),
        terrabase::identity::DatabaseUlid::from_text(writer).unwrap(),
        KeyPurpose::Data,
    )
    .unwrap();
    let data_lease = Arc::new(
        DatabaseKeyLease::test_oci_vault(
            KeyVersionMetadata::new(
                data_scope,
                DatabaseKeyVersion::from_text("01ARZ3NDEKTSV4RRFFQ69G5FB1").unwrap(),
            ),
            DatabaseKey::from_bytes([47; 32]),
            Duration::from_secs(60),
        )
        .unwrap(),
    );
    let source =
        WalSnapshotSource::capture(binding.clone(), engine, root.join("source")).await.unwrap();
    (source, binding, lease, data_lease, root)
}

#[tokio::test]
async fn encrypted_snapshot_upload_round_trip_and_retry_validation() {
    let (source, binding, lease, data_lease, root) = fixture().await;
    let store = Arc::new(object_store::memory::InMemory::new());
    let destination = Arc::new(terrabase::backup::object_store::ObjectDestination::for_test(
        terrabase::backup::object_store::Provider::Oci,
        store,
        4 * 1024 * 1024,
        8 * 1024 * 1024,
    ));
    let staging = root.join("backup");
    let placement = PlacementLock::explicit([RegionCode::GRU]).unwrap();
    let keys = Arc::new(StaticKeys(Arc::clone(&data_lease)));
    super::native_snapshot_backup::upload_snapshot(
        &source,
        RegionCode::GRU,
        &lease,
        &staging,
        "roundtrip",
        std::slice::from_ref(&destination),
        &placement,
        8 * 1024 * 1024,
    )
    .await
    .unwrap();
    let mut chunk = std::fs::read_dir(&staging)
        .unwrap()
        .find(|entry| entry.as_ref().unwrap().file_name().to_string_lossy().starts_with("chunk-"))
        .unwrap()
        .unwrap()
        .path();
    let original = std::fs::read(&chunk).unwrap();
    std::fs::write(&chunk, b"corrupt").unwrap();
    assert_eq!(
        super::native_snapshot_backup::upload_snapshot(
            &source,
            RegionCode::GRU,
            &lease,
            &staging,
            "roundtrip",
            std::slice::from_ref(&destination),
            &placement,
            8 * 1024 * 1024
        )
        .await,
        Err(super::native_snapshot_backup::SnapshotBackupError::Encryption)
    );
    std::fs::write(&chunk, original).unwrap();
    std::fs::remove_file(&chunk).unwrap();
    assert_eq!(
        super::native_snapshot_backup::upload_snapshot(
            &source,
            RegionCode::GRU,
            &lease,
            &staging,
            "roundtrip",
            std::slice::from_ref(&destination),
            &placement,
            8 * 1024 * 1024,
        )
        .await,
        Err(super::native_snapshot_backup::SnapshotBackupError::Snapshot)
    );
    // Recreate a clean retry directory, then ensure unrecognized files are rejected.
    std::fs::remove_dir_all(&staging).unwrap();
    super::native_snapshot_backup::upload_snapshot(
        &source,
        RegionCode::GRU,
        &lease,
        &staging,
        "roundtrip-retry",
        std::slice::from_ref(&destination),
        &placement,
        8 * 1024 * 1024,
    )
    .await
    .unwrap();
    std::fs::write(staging.join("chunk-extra"), b"extra").unwrap();
    assert_eq!(
        super::native_snapshot_backup::upload_snapshot(
            &source,
            RegionCode::GRU,
            &lease,
            &staging,
            "roundtrip-retry",
            std::slice::from_ref(&destination),
            &placement,
            8 * 1024 * 1024,
        )
        .await,
        Err(super::native_snapshot_backup::SnapshotBackupError::Snapshot)
    );
    std::fs::remove_file(staging.join("chunk-extra")).unwrap();
    let target = root.join("target");
    let restore_result = super::native_snapshot_backup::restore_snapshot(
        &destination,
        "roundtrip-retry",
        &root.join("download"),
        &*lease as &dyn BackupReader,
        binding,
        RegionCode::GRU,
        &placement,
        target,
        keys,
        8 * 1024 * 1024,
    )
    .await;
    if let Err(error) = &restore_result {
        println!("restore error: {error:?}");
    }
    let (_follower, receipt) = restore_result.unwrap();
    assert!(receipt.checkpoint.is_some());
}

#[test]
fn snapshot_upload_rejects_wrong_region_before_storage() {
    let placement = PlacementLock::explicit([RegionCode::GRU]).unwrap();
    assert_eq!(
        super::native_snapshot_backup::validate_residency(
            RegionCode::SFO,
            RegionCode::GRU,
            &placement
        ),
        Err(super::native_snapshot_backup::SnapshotBackupError::Provider)
    );
}
