//! Encrypted object backup of the replication driver's complete physical snapshot.
//! Logical SQLite/WAL bytes exist only in bounded memory between the encrypted
//! physical source and per-database backup envelopes. Installation returns a
//! follower driver which must still tail and reconcile before serving.
use std::path::{Path, PathBuf};
use std::sync::Arc;
use terrabase::backup::BackupReader;
use terrabase::backup::object_store::ObjectDestination;
use terrabase::encryption::{
    ArtifactCipher, ArtifactKind, Compression, DatabaseKeyLease, DatabaseKeySource,
    EncryptionContext,
};
use terrabase_cluster::wal_driver::{WalCheckpointMessage, WalFollowerDriver};
use terrabase_cluster::wal_route::WalReplicaBinding;
use terrabase_cluster::wal_snapshot::{
    WalSnapshotChunk, WalSnapshotManifest, WalSnapshotSink, WalSnapshotSource,
};

/// Redacted physical backup failure; an error never grants serving authority.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum SnapshotBackupError {
    /// Key, scope, or envelope authorization failed.
    Encryption,
    /// Local snapshot, artifact integrity, or storage bounds failed.
    Snapshot,
    /// A selected object provider is unavailable.
    Provider,
}

/// Encrypts a complete admitted physical snapshot and independently uploads it
/// to every selected provider. The caller owns durable retry identity `prefix`.
///
/// # Errors
/// Rejects missing providers, expired keys, oversized snapshots and failed uploads.
pub async fn upload_snapshot(
    source: &WalSnapshotSource,
    lease: &DatabaseKeyLease,
    staging: &Path,
    prefix: &str,
    destinations: &[Arc<ObjectDestination>],
    max_bytes: u64,
) -> Result<(), SnapshotBackupError> {
    if destinations.is_empty() || destinations.len() > 2 {
        return Err(SnapshotBackupError::Provider);
    }
    let metadata = lease.metadata();
    if metadata.scope().database() != source.manifest().scope.database()
        || metadata.scope().tenant() != source.manifest().scope.tenant()
        || metadata.scope().project() != Some(source.manifest().scope.project())
    {
        return Err(SnapshotBackupError::Encryption);
    }
    let context = EncryptionContext::for_artifact(
        metadata.scope(),
        ArtifactKind::Backup,
        terrabase::identity::process_generator().generate(),
        metadata.version(),
        3,
        Compression::None,
    )
    .map_err(|_| SnapshotBackupError::Encryption)?;
    let mut cipher = ArtifactCipher::from_database_key_lease(lease, context, 0)
        .map_err(|_| SnapshotBackupError::Encryption)?;
    if staging.exists() {
        let envelope = read_bounded(&staging.join("manifest.tbv3e"), 2 * 1024 * 1024)?;
        let bytes =
            lease.open_backup_envelope(&envelope).map_err(|_| SnapshotBackupError::Encryption)?;
        let prior =
            WalSnapshotManifest::decode(&bytes).map_err(|_| SnapshotBackupError::Snapshot)?;
        if prior.identity() != source.manifest().identity() {
            return Err(SnapshotBackupError::Snapshot);
        }
        let mut failed = false;
        for destination in destinations {
            failed |= destination.upload_point(staging, prefix).await.is_err();
        }
        return if failed { Err(SnapshotBackupError::Provider) } else { Ok(()) };
    }
    std::fs::create_dir(staging).map_err(|_| SnapshotBackupError::Snapshot)?;
    let result = async {
        let mut total = 0_u64;
        for slot in 0..3 {
            let Some(length) = source.manifest().file_bytes(slot) else {
                continue;
            };
            let mut offset = 0;
            while offset < length {
                let chunk = source
                    .read_local(slot, offset)
                    .await
                    .map_err(|_| SnapshotBackupError::Snapshot)?;
                let next = offset
                    .checked_add(chunk.bytes().len() as u64)
                    .ok_or(SnapshotBackupError::Snapshot)?;
                if next <= offset || next > length {
                    return Err(SnapshotBackupError::Snapshot);
                }
                let mut plaintext = chunk.encode().map_err(|_| SnapshotBackupError::Snapshot)?;
                let envelope =
                    cipher.seal(&plaintext).map_err(|_| SnapshotBackupError::Encryption)?;
                plaintext.fill(0);
                total = total
                    .checked_add(envelope.len() as u64)
                    .ok_or(SnapshotBackupError::Snapshot)?;
                if total > max_bytes {
                    return Err(SnapshotBackupError::Snapshot);
                }
                write_synced(&staging.join(format!("chunk-{slot}-{offset:020}")), &envelope)?;
                offset = next;
            }
        }
        let manifest = source.manifest().encode().map_err(|_| SnapshotBackupError::Snapshot)?;
        let envelope = cipher.seal(&manifest).map_err(|_| SnapshotBackupError::Encryption)?;
        if total.saturating_add(envelope.len() as u64) > max_bytes {
            return Err(SnapshotBackupError::Snapshot);
        }
        write_synced(&staging.join("manifest.tbv3e"), &envelope)?;
        std::fs::File::open(staging)
            .and_then(|file| file.sync_all())
            .map_err(|_| SnapshotBackupError::Snapshot)?;
        let mut failed = false;
        for destination in destinations {
            failed |= destination.upload_point(staging, prefix).await.is_err();
        }
        if failed {
            return Err(SnapshotBackupError::Provider);
        }
        Ok(())
    }
    .await;
    // Sealed ciphertext remains for durable retry; incomplete capture may be
    // retried from the same source without accepting a partial object family.
    if result.is_err() && !staging.join("manifest.tbv3e").exists() {
        let _ = std::fs::remove_dir_all(staging);
    }
    result
}

/// Downloads from one provider, authenticates the physical manifest/chunks and
/// installs through the accepted follower binding. The resulting checkpoint is
/// a bootstrap cut, not proof of peer catch-up or permission to serve.
///
/// # Errors
/// Refuses unauthorized/corrupt artifacts, foreign binding, and existing targets.
#[allow(clippy::too_many_arguments)]
pub async fn restore_snapshot(
    destination: &ObjectDestination,
    prefix: &str,
    staging: &Path,
    reader: &dyn BackupReader,
    binding: WalReplicaBinding,
    target: PathBuf,
    keys: Arc<dyn DatabaseKeySource>,
    max_bytes: u64,
) -> Result<(WalFollowerDriver, WalCheckpointMessage), SnapshotBackupError> {
    destination
        .download_point_with_byte_limit(prefix, staging, max_bytes)
        .await
        .map_err(|_| SnapshotBackupError::Provider)?;
    let envelope = read_bounded(&staging.join("manifest.tbv3e"), 2 * 1024 * 1024)?;
    let manifest_bytes =
        reader.open_backup_envelope(&envelope).map_err(|_| SnapshotBackupError::Encryption)?;
    let manifest =
        WalSnapshotManifest::decode(&manifest_bytes).map_err(|_| SnapshotBackupError::Snapshot)?;
    let lengths: Vec<_> = (0..3).map(|slot| manifest.file_bytes(slot).unwrap_or(0)).collect();
    let mut sink = WalSnapshotSink::begin_local(binding, manifest, target, max_bytes, Some(keys))
        .await
        .map_err(|_| SnapshotBackupError::Snapshot)?;
    for (slot, length) in lengths.into_iter().enumerate() {
        let mut offset = 0;
        while offset < length {
            let envelope =
                read_bounded(&staging.join(format!("chunk-{slot}-{offset:020}")), 2 * 1024 * 1024)?;
            let mut plaintext = reader
                .open_backup_envelope(&envelope)
                .map_err(|_| SnapshotBackupError::Encryption)?;
            let chunk =
                WalSnapshotChunk::decode(&plaintext).map_err(|_| SnapshotBackupError::Snapshot)?;
            plaintext.fill(0);
            let next = offset
                .checked_add(chunk.bytes().len() as u64)
                .ok_or(SnapshotBackupError::Snapshot)?;
            if next <= offset || next > length {
                return Err(SnapshotBackupError::Snapshot);
            }
            sink.write_local(chunk).await.map_err(|_| SnapshotBackupError::Snapshot)?;
            offset = next;
        }
    }
    sink.finish_local().await.map_err(|_| SnapshotBackupError::Snapshot)
}

fn write_synced(path: &Path, bytes: &[u8]) -> Result<(), SnapshotBackupError> {
    use std::io::Write;
    let mut file = std::fs::OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(path)
        .map_err(|_| SnapshotBackupError::Snapshot)?;
    file.write_all(bytes).and_then(|()| file.sync_all()).map_err(|_| SnapshotBackupError::Snapshot)
}
fn read_bounded(path: &Path, limit: u64) -> Result<Vec<u8>, SnapshotBackupError> {
    use std::io::Read;
    let mut bytes = Vec::new();
    std::fs::File::open(path)
        .map_err(|_| SnapshotBackupError::Snapshot)?
        .take(limit + 1)
        .read_to_end(&mut bytes)
        .map_err(|_| SnapshotBackupError::Snapshot)?;
    if bytes.len() as u64 > limit {
        return Err(SnapshotBackupError::Snapshot);
    }
    Ok(bytes)
}
