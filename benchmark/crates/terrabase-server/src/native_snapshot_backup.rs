//! Encrypted object backup of the replication driver's complete physical snapshot.
//! Logical SQLite/WAL bytes exist only in bounded memory between the encrypted
//! physical source and per-database backup envelopes. Installation returns a
//! follower driver which must still tail and reconcile before serving.
use std::collections::HashSet;
use std::path::{Path, PathBuf};
use std::sync::Arc;
use terrabase::backup::BackupReader;
use terrabase::backup::PlacementLock;
use terrabase::backup::object_store::ObjectDestination;
use terrabase::encryption::{
    ArtifactCipher, ArtifactKind, Compression, DatabaseKeyLease, DatabaseKeySource,
    EncryptionContext,
};
use terrabase::regions::RegionCode;
use terrabase_cluster::wal_driver::{WalCheckpointMessage, WalFollowerDriver};
use terrabase_cluster::wal_follower::MAX_WAL_BATCH_BYTES;
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

pub(crate) fn validate_residency(
    source_region: RegionCode,
    destination_region: RegionCode,
    placement: &PlacementLock,
) -> Result<(), SnapshotBackupError> {
    if source_region != destination_region || !placement.allows(destination_region) {
        return Err(SnapshotBackupError::Provider);
    }
    Ok(())
}

/// Encrypts a complete admitted physical snapshot and independently uploads it
/// to every selected provider. The caller owns durable retry identity `prefix`.
///
/// # Errors
/// Rejects missing providers, expired keys, oversized snapshots and failed uploads.
#[allow(clippy::too_many_arguments, reason = "snapshot upload takes the full production context")]
#[allow(clippy::too_many_lines, reason = "one provider validation pass, one upload fan-out")]
pub async fn upload_snapshot(
    source: &WalSnapshotSource,
    source_region: RegionCode,
    lease: &DatabaseKeyLease,
    staging: &Path,
    prefix: &str,
    destinations: &[Arc<ObjectDestination>],
    placement: &PlacementLock,
    max_bytes: u64,
) -> Result<(), SnapshotBackupError> {
    if destinations.is_empty()
        || destinations.len() > 2
        || destinations.len() == 2 && destinations[0].provider() == destinations[1].provider()
    {
        return Err(SnapshotBackupError::Provider);
    }
    if destinations.iter().any(|destination| {
        validate_residency(source_region, destination.admitted_region(), placement).is_err()
    }) {
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
        terrabase::identity::generate(),
        metadata.version(),
        3,
        Compression::None,
    )
    .map_err(|_| SnapshotBackupError::Encryption)?;
    let mut cipher = ArtifactCipher::from_database_key_lease(lease, context, 0)
        .map_err(|_| SnapshotBackupError::Encryption)?;
    if staging.exists() {
        if !std::fs::symlink_metadata(staging)
            .map_err(|_| SnapshotBackupError::Snapshot)?
            .file_type()
            .is_dir()
        {
            return Err(SnapshotBackupError::Snapshot);
        }
        let envelope = read_bounded(&staging.join("manifest.tbv3e"), 2 * 1024 * 1024)?;
        let bytes =
            lease.open_backup_envelope(&envelope).map_err(|_| SnapshotBackupError::Encryption)?;
        let prior =
            WalSnapshotManifest::decode(&bytes).map_err(|_| SnapshotBackupError::Snapshot)?;
        if prior.identity() != source.manifest().identity() {
            return Err(SnapshotBackupError::Snapshot);
        }
        validate_retry_staging(source, lease, staging, max_bytes).await?;
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

async fn validate_retry_staging(
    source: &WalSnapshotSource,
    lease: &DatabaseKeyLease,
    staging: &Path,
    max_bytes: u64,
) -> Result<(), SnapshotBackupError> {
    let manifest_path = staging.join("manifest.tbv3e");
    let manifest_envelope = read_bounded(&manifest_path, 2 * 1024 * 1024)?;
    let manifest_bytes = lease
        .open_backup_envelope(&manifest_envelope)
        .map_err(|_| SnapshotBackupError::Encryption)?;
    let manifest =
        WalSnapshotManifest::decode(&manifest_bytes).map_err(|_| SnapshotBackupError::Snapshot)?;
    if manifest.identity() != source.manifest().identity()
        || manifest.scope != source.manifest().scope
        || manifest.replica_epoch != source.manifest().replica_epoch
    {
        return Err(SnapshotBackupError::Snapshot);
    }
    let mut expected = HashSet::new();
    let mut total = manifest_envelope.len() as u64;
    if total > max_bytes {
        return Err(SnapshotBackupError::Snapshot);
    }
    for slot in 0..3_u8 {
        let Some(length) = source.manifest().file_bytes(slot) else { continue };
        let mut offset = 0_u64;
        while offset < length {
            let name = format!("chunk-{slot}-{offset:020}");
            expected.insert(name.clone());
            let path = staging.join(&name);
            let metadata =
                std::fs::symlink_metadata(&path).map_err(|_| SnapshotBackupError::Snapshot)?;
            if !metadata.file_type().is_file() {
                return Err(SnapshotBackupError::Snapshot);
            }
            let envelope = read_bounded(&path, 2 * 1024 * 1024)?;
            total =
                total.checked_add(envelope.len() as u64).ok_or(SnapshotBackupError::Snapshot)?;
            if total > max_bytes {
                return Err(SnapshotBackupError::Snapshot);
            }
            let mut plaintext = lease
                .open_backup_envelope(&envelope)
                .map_err(|_| SnapshotBackupError::Encryption)?;
            let chunk =
                WalSnapshotChunk::decode(&plaintext).map_err(|_| SnapshotBackupError::Snapshot)?;
            if chunk.request().scope != source.manifest().scope
                || chunk.request().replica_epoch != source.manifest().replica_epoch
                || chunk.request().identity != source.manifest().identity()
                || chunk.request().file != slot
                || chunk.request().offset != offset
            {
                plaintext.fill(0);
                return Err(SnapshotBackupError::Snapshot);
            }
            let chunk_len = chunk.bytes().len() as u64;
            let source_chunk =
                source.read_local(slot, offset).await.map_err(|_| SnapshotBackupError::Snapshot)?;
            if source_chunk.bytes() != chunk.bytes() {
                plaintext.fill(0);
                return Err(SnapshotBackupError::Snapshot);
            }
            plaintext.fill(0);
            let next = offset.checked_add(chunk_len).ok_or(SnapshotBackupError::Snapshot)?;
            if next <= offset || next > length || chunk_len > MAX_WAL_BATCH_BYTES as u64 {
                return Err(SnapshotBackupError::Snapshot);
            }
            offset = next;
        }
    }
    let manifest_metadata =
        std::fs::symlink_metadata(&manifest_path).map_err(|_| SnapshotBackupError::Snapshot)?;
    if !manifest_metadata.file_type().is_file() {
        return Err(SnapshotBackupError::Snapshot);
    }
    for entry in std::fs::read_dir(staging).map_err(|_| SnapshotBackupError::Snapshot)? {
        let entry = entry.map_err(|_| SnapshotBackupError::Snapshot)?;
        let name = entry.file_name().into_string().map_err(|_| SnapshotBackupError::Snapshot)?;
        if name != "manifest.tbv3e" && !expected.remove(&name) {
            return Err(SnapshotBackupError::Snapshot);
        }
    }
    if !expected.is_empty() {
        return Err(SnapshotBackupError::Snapshot);
    }
    Ok(())
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
    source_region: RegionCode,
    placement: &PlacementLock,
    target: PathBuf,
    keys: Arc<dyn DatabaseKeySource>,
    max_bytes: u64,
) -> Result<(WalFollowerDriver, WalCheckpointMessage), SnapshotBackupError> {
    validate_residency(source_region, destination.admitted_region(), placement)?;
    let mut staging_cleanup = RestoreStagingCleanup::new(staging);
    let result = async {
        destination
            .download_point_with_byte_limit(prefix, staging, max_bytes)
            .await
            .map_err(|_| SnapshotBackupError::Provider)?;
        staging_cleanup.mark_downloaded();
        let envelope = read_bounded(&staging.join("manifest.tbv3e"), 2 * 1024 * 1024)?;
        let manifest_bytes =
            reader.open_backup_envelope(&envelope).map_err(|_| SnapshotBackupError::Encryption)?;
        let manifest = WalSnapshotManifest::decode(&manifest_bytes)
            .map_err(|_| SnapshotBackupError::Snapshot)?;
        let manifest_scope = manifest.scope;
        let manifest_epoch = manifest.replica_epoch;
        let manifest_identity = manifest.identity();
        let lengths: Vec<_> = (0..3).map(|slot| manifest.file_bytes(slot).unwrap_or(0)).collect();
        let mut sink =
            WalSnapshotSink::begin_local(binding, manifest, target, max_bytes, Some(keys))
                .await
                .map_err(|_| SnapshotBackupError::Snapshot)?;
        for (slot_index, length) in lengths.into_iter().enumerate() {
            let slot = u8::try_from(slot_index).map_err(|_| SnapshotBackupError::Snapshot)?;
            let mut offset = 0;
            while offset < length {
                let envelope = read_bounded(
                    &staging.join(format!("chunk-{slot_index}-{offset:020}")),
                    2 * 1024 * 1024,
                )?;
                let mut plaintext = reader
                    .open_backup_envelope(&envelope)
                    .map_err(|_| SnapshotBackupError::Encryption)?;
                let chunk = WalSnapshotChunk::decode(&plaintext)
                    .map_err(|_| SnapshotBackupError::Snapshot)?;
                if chunk.request().scope != manifest_scope
                    || chunk.request().replica_epoch != manifest_epoch
                    || chunk.request().identity != manifest_identity
                    || chunk.request().file != slot
                    || chunk.request().offset != offset
                {
                    plaintext.fill(0);
                    return Err(SnapshotBackupError::Snapshot);
                }
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
    .await;
    if result.is_ok() {
        staging_cleanup.keep();
    }
    result
}

/// Removes a downloaded restore point if any verification or installation step
/// fails, including cancellation while the restore future is suspended.
struct RestoreStagingCleanup {
    path: PathBuf,
    downloaded: bool,
    keep: bool,
}

impl RestoreStagingCleanup {
    fn new(path: &Path) -> Self {
        Self { path: path.to_owned(), downloaded: false, keep: false }
    }

    fn mark_downloaded(&mut self) {
        self.downloaded = true;
    }

    fn keep(&mut self) {
        self.keep = true;
    }
}

impl Drop for RestoreStagingCleanup {
    fn drop(&mut self) {
        if self.downloaded && !self.keep {
            let _ = std::fs::remove_dir_all(&self.path);
        }
    }
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

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn wrong_region_is_rejected_before_provider_use() {
        let placement = PlacementLock::explicit([RegionCode::GRU]).unwrap();
        assert_eq!(
            validate_residency(RegionCode::SFO, RegionCode::GRU, &placement),
            Err(SnapshotBackupError::Provider)
        );
        assert_eq!(
            validate_residency(RegionCode::GRU, RegionCode::SFO, &placement),
            Err(SnapshotBackupError::Provider)
        );
        assert!(validate_residency(RegionCode::GRU, RegionCode::GRU, &placement).is_ok());
    }
}
