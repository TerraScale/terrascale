//! Restart-safe encrypted export artifacts for explicitly local installations.

use crate::export_api::ExportFuture;
use crate::export_api::{
    ArtifactFact, ArtifactPort, ArtifactWriter, EncryptionAttestation, ExportDestination,
    ExportError, MAX_METADATA_BYTES, MAX_PAGE_BYTES, MAX_PAGE_RECORDS, PlacementLock,
    ensure_no_symlink_components, fact, publish_artifact, safe_member_path,
    sync_directory_ancestors,
};
use crate::export_artifact_crypto::{
    ArtifactCrypto, LocalCrypto, LocalManifest, LocalWriter, bounded_read, encrypted_member,
    io_error,
};
use std::fs::{self, File, OpenOptions};
use std::io::{self, Read, Write};
use std::os::unix::fs::{OpenOptionsExt, PermissionsExt};
use std::path::{Path, PathBuf};
use std::sync::Arc;
use terrabase::encryption::ProviderMode;

fn private_key(root: &Path, name: &str) -> Result<[u8; 32], ExportError> {
    ensure_no_symlink_components(root)?;
    fs::create_dir_all(root).map_err(|error| io_error(&error))?;
    ensure_no_symlink_components(root)?;
    let path = root.join(name);
    ensure_no_symlink_components(&path)?;
    if !path.try_exists().map_err(|error| io_error(&error))? {
        let mut key = [0; 32];
        aws_lc_rs::rand::fill(&mut key).map_err(|_| ExportError::EncryptionUnavailable)?;
        let temporary =
            root.join(format!(".{name}.{}.part", terrabase::identity::DatabaseUlid::generate()));
        let result = (|| -> io::Result<()> {
            let mut file =
                OpenOptions::new().write(true).create_new(true).mode(0o600).open(&temporary)?;
            file.write_all(&key)?;
            file.sync_all()?;
            match fs::hard_link(&temporary, &path) {
                Ok(()) => (),
                Err(error) if error.kind() == io::ErrorKind::AlreadyExists => (),
                Err(error) => return Err(error),
            }
            Ok(())
        })();
        let _ = fs::remove_file(&temporary);
        result.map_err(|error| io_error(&error))?;
    }
    ensure_no_symlink_components(&path)?;
    let file = File::open(&path).map_err(|error| io_error(&error))?;
    let metadata = file.metadata().map_err(|error| io_error(&error))?;
    if !metadata.is_file() || metadata.permissions().mode() & 0o077 != 0 || metadata.len() != 32 {
        return Err(ExportError::EncryptionUnavailable);
    }
    file.sync_all().map_err(|error| io_error(&error))?;
    sync_directory_ancestors(root).map_err(|error| io_error(&error))?;
    fs::read(path)
        .map_err(|error| io_error(&error))?
        .try_into()
        .map_err(|_| ExportError::EncryptionUnavailable)
}

/// Loads or atomically creates the private local resume-cursor signing key.
///
/// # Errors
/// Returns an error for unsafe paths, non-private keys, or failed durability.
pub fn local_cursor_key(root: &Path) -> Result<[u8; 32], ExportError> {
    private_key(root, ".export-cursor-key")
}

/// Opens a local-only artifact provider with a durable private master key.
///
/// Each immutable member uses a content-derived keyed context. A retry emits
/// identical ciphertext; conflicting content uses a distinct context/subkey,
/// including after a process restart, so ordinal zero is never reused across
/// different plaintext under the same derived key.
///
/// # Errors
/// Returns an error if key persistence or context validation fails.
pub fn local_artifacts(
    root: &Path,
    database_id: &str,
    tenant: &str,
) -> Result<Arc<dyn ArtifactPort>, ExportError> {
    let key = private_key(root, ".export-artifact-key")?;
    let crypto = Arc::new(LocalCrypto { key, database: database_id.into(), tenant: tenant.into() });
    crypto.cipher(&[0; 32])?;
    Ok(Arc::new(LocalPort { root: root.into(), crypto }))
}

struct LocalPort {
    root: PathBuf,
    crypto: Arc<dyn ArtifactCrypto>,
}
impl ArtifactPort for LocalPort {
    fn attestation(&self) -> Option<EncryptionAttestation> {
        Some(EncryptionAttestation {
            provider: ProviderMode::LocalFile.identity().into(),
            key_version: "local-export-v1".into(),
        })
    }
    fn read_member<'a>(
        &'a self,
        job_id: &'a str,
        database: &'a str,
        member: &'a str,
        manifest: &'a ArtifactFact,
    ) -> ExportFuture<'a, Vec<u8>> {
        Box::pin(async move {
            if !safe_member_path(job_id)
                || Path::new(job_id).components().count() != 1
                || !safe_member_path(database)
                || Path::new(database).components().count() != 1
                || !safe_member_path(member)
                || member == "COMMITTED"
                || manifest.path != "manifest.json"
            {
                return Err(ExportError::DestinationUnavailable);
            }
            let directory = self.root.join(database).join(job_id);
            let expected =
                format!("{}\t{}\t{}\n", manifest.path, manifest.length, manifest.checksum);
            if bounded_read(&directory.join("COMMITTED"), 1024)? != expected.as_bytes() {
                return Err(ExportError::IdempotencyConflict);
            }
            let manifest_bytes = encrypted_member(&directory.join("manifest.json"))?;
            if fact("manifest.json", &manifest_bytes) != *manifest {
                return Err(ExportError::IdempotencyConflict);
            }
            let binding = format!("{database}/{job_id}");
            let manifest_plaintext =
                self.crypto.open(&binding, "manifest.json", &manifest_bytes)?;
            if member == "manifest.json" {
                return Ok(manifest_plaintext);
            }
            let manifest: LocalManifest = serde_json::from_slice(&manifest_plaintext)
                .map_err(|_| ExportError::IdempotencyConflict)?;
            let expected = manifest
                .members
                .iter()
                .find(|fact| fact.path == member)
                .ok_or(ExportError::DestinationUnavailable)?;
            let encrypted = encrypted_member(&directory.join(member))?;
            if fact(member, &encrypted) != *expected {
                return Err(ExportError::IdempotencyConflict);
            }
            self.crypto.open(&binding, member, &encrypted)
        })
    }
    fn open<'a>(
        &'a self,
        job_id: &'a str,
        database: &'a str,
        destination: &'a ExportDestination,
        placement: &'a PlacementLock,
    ) -> ExportFuture<'a, Box<dyn ArtifactWriter>> {
        Box::pin(async move {
            let ExportDestination::File { root, region } = destination else {
                return Err(ExportError::DestinationUnavailable);
            };
            if !placement.allows(*region) {
                return Err(ExportError::PlacementMismatch);
            }
            if !safe_member_path(job_id)
                || Path::new(job_id).components().count() != 1
                || !safe_member_path(database)
                || Path::new(database).components().count() != 1
            {
                return Err(ExportError::DestinationUnavailable);
            }
            if root != &self.root {
                return Err(ExportError::DestinationUnavailable);
            }
            let directory = self.root.join(database).join(job_id);
            ensure_no_symlink_components(&directory)?;
            fs::create_dir_all(&directory).map_err(|error| io_error(&error))?;
            ensure_no_symlink_components(&directory)?;
            Ok(Box::new(LocalWriter {
                directory,
                job_id: format!("{database}/{job_id}"),
                crypto: Arc::clone(&self.crypto),
            }))
        })
    }
}
impl LocalWriter {
    fn verify_member(&self, path: &str, plaintext: &[u8]) -> Result<ArtifactFact, ExportError> {
        let target = self.directory.join(path);
        ensure_no_symlink_components(&target)?;
        let encrypted = encrypted_member(&target)?;

        if self
            .crypto
            .open(&self.job_id, path, &encrypted)
            .map_err(|_| ExportError::IdempotencyConflict)?
            != plaintext
        {
            return Err(ExportError::IdempotencyConflict);
        }
        sync_directory_ancestors(target.parent().ok_or(ExportError::DestinationUnavailable)?)
            .map_err(|error| ExportError::Io(error.to_string()))?;
        Ok(fact(path, &encrypted))
    }
}

impl ArtifactWriter for LocalWriter {
    fn put<'a>(&'a mut self, path: &'a str, plaintext: &'a [u8]) -> ExportFuture<'a, ArtifactFact> {
        Box::pin(async move {
            if !safe_member_path(path) || plaintext.len() > MAX_METADATA_BYTES.max(MAX_PAGE_BYTES) {
                return Err(ExportError::InvalidRequest("artifact member is unsafe or too large"));
            }
            let target = self.directory.join(path);
            ensure_no_symlink_components(&target)?;
            match fs::symlink_metadata(&target) {
                Ok(_) => return self.verify_member(path, plaintext),
                Err(error) if error.kind() == io::ErrorKind::NotFound => {}
                Err(error) => return Err(ExportError::Io(error.to_string())),
            }
            let encrypted = self.crypto.seal(&self.job_id, path, plaintext)?;
            if publish_artifact(&target, &encrypted)? {
                Ok(fact(path, &encrypted))
            } else {
                self.verify_member(path, plaintext)
            }
        })
    }

    fn existing_facts<'a>(&'a mut self) -> ExportFuture<'a, Vec<ArtifactFact>> {
        Box::pin(async move {
            let mut pending = vec![self.directory.clone()];
            let mut facts = Vec::new();

            while let Some(directory) = pending.pop() {
                let entries =
                    fs::read_dir(&directory).map_err(|error| ExportError::Io(error.to_string()))?;
                for entry in entries {
                    let entry = entry.map_err(|error| ExportError::Io(error.to_string()))?;
                    let file_type =
                        entry.file_type().map_err(|error| ExportError::Io(error.to_string()))?;
                    if file_type.is_symlink() {
                        return Err(ExportError::DestinationUnavailable);
                    }
                    let path = entry.path();
                    if file_type.is_dir() {
                        pending.push(path);
                        continue;
                    }
                    if !file_type.is_file()
                        || path.file_name().is_some_and(|name| name == "COMMITTED")
                    {
                        continue;
                    }
                    let relative = path
                        .strip_prefix(&self.directory)
                        .map_err(|_| ExportError::DestinationUnavailable)?;
                    let member = relative.to_str().ok_or(ExportError::DestinationUnavailable)?;
                    // Publication temporaries use this exact, case-sensitive suffix.
                    #[allow(clippy::case_sensitive_file_extension_comparisons)]
                    if member.ends_with(".part") || !safe_member_path(member) {
                        continue;
                    }
                    let encrypted = encrypted_member(&path)?;
                    if encrypted.len() > MAX_METADATA_BYTES.max(MAX_PAGE_BYTES) + 4096 {
                        return Err(ExportError::DestinationUnavailable);
                    }
                    self.crypto
                        .open(&self.job_id, member, &encrypted)
                        .map_err(|_| ExportError::IdempotencyConflict)?;
                    facts.push(fact(member, &encrypted));
                    if facts.len() > MAX_PAGE_RECORDS.saturating_mul(4) {
                        return Err(ExportError::DestinationUnavailable);
                    }
                }
            }
            facts.sort_by(|left, right| left.path.cmp(&right.path));
            Ok(facts)
        })
    }

    fn commit(self: Box<Self>, manifest: &ArtifactFact) -> ExportFuture<'static, ()> {
        Box::pin(async move {
            if manifest.path != "manifest.json" {
                return Err(ExportError::IdempotencyConflict);
            }
            let manifest_path = self.directory.join(&manifest.path);
            ensure_no_symlink_components(&manifest_path)?;
            let encrypted = encrypted_member(&manifest_path)?;
            if fact(&manifest.path, &encrypted) != *manifest {
                return Err(ExportError::IdempotencyConflict);
            }
            self.crypto.open(&self.job_id, &manifest.path, &encrypted)?;
            let marker = self.directory.join("COMMITTED");
            ensure_no_symlink_components(&marker)?;
            let bytes = format!("{}\t{}\t{}\n", manifest.path, manifest.length, manifest.checksum);
            if !publish_artifact(&marker, bytes.as_bytes())?
                && bounded_read(&marker, 1024)? != bytes.as_bytes()
            {
                return Err(ExportError::IdempotencyConflict);
            }
            Ok(())
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use terrabase::regions::RegionCode;

    #[tokio::test]
    async fn restart_recovers_encrypted_members_and_idempotent_commit() {
        let root = std::env::temp_dir()
            .join(format!("export-local-{}", terrabase::identity::DatabaseUlid::generate()));
        let destination = ExportDestination::File { root: root.clone(), region: RegionCode::GRU };
        let placement = PlacementLock::single(RegionCode::GRU);
        let first = local_artifacts(&root, "database-id", "tenant").unwrap();
        let mut writer = first.open("job", "database", &destination, &placement).await.unwrap();
        let page = writer.put("documents/page.json", b"private-document").await.unwrap();
        let ciphertext = fs::read(root.join("database/job/documents/page.json")).unwrap();
        assert!(!ciphertext.windows(16).any(|window| window == b"private-document"));
        drop(writer);
        drop(first);
        let restarted = local_artifacts(&root, "database-id", "tenant").unwrap();
        let mut writer = restarted.open("job", "database", &destination, &placement).await.unwrap();
        assert_eq!(writer.existing_facts().await.unwrap(), vec![page.clone()]);
        assert_eq!(writer.put("documents/page.json", b"private-document").await.unwrap(), page);
        assert!(matches!(
            writer.put("documents/page.json", b"different").await,
            Err(ExportError::IdempotencyConflict)
        ));
        let manifest_plaintext =
            serde_json::to_vec(&serde_json::json!({"members":[page]})).unwrap();
        let manifest = writer.put("manifest.json", &manifest_plaintext).await.unwrap();
        writer.commit(&manifest).await.unwrap();
        let restarted = local_artifacts(&root, "database-id", "tenant").unwrap();
        let writer = restarted.open("job", "database", &destination, &placement).await.unwrap();
        writer.commit(&manifest).await.unwrap();
        assert_eq!(
            restarted
                .read_member("job", "database", "documents/page.json", &manifest)
                .await
                .unwrap(),
            b"private-document"
        );
        assert_eq!(
            restarted.read_member("job", "database", "manifest.json", &manifest).await.unwrap(),
            manifest_plaintext
        );
        assert!(
            restarted
                .read_member("job", "database", "../.export-artifact-key", &manifest)
                .await
                .is_err()
        );
        assert_eq!(fs::read(root.join("database/job/documents/page.json")).unwrap(), ciphertext);
        assert_eq!(local_cursor_key(&root).unwrap(), local_cursor_key(&root).unwrap());
        fs::write(root.join("database/job/COMMITTED"), b"tampered").unwrap();
        assert!(matches!(
            restarted.read_member("job", "database", "documents/page.json", &manifest).await,
            Err(ExportError::IdempotencyConflict)
        ));
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn changed_content_and_member_have_distinct_authenticated_contexts() {
        let crypto = LocalCrypto { key: [7; 32], database: "db".into(), tenant: "tenant".into() };
        let first = crypto.seal("job", "page", b"one").unwrap();
        let second = crypto.seal("job", "page", b"two").unwrap();
        assert_ne!(&first[..32], &second[..32]);
        assert_eq!(first, crypto.seal("job", "page", b"one").unwrap());
        assert!(crypto.open("job", "other", &first).is_err());
        assert!(crypto.open("other", "page", &first).is_err());
        let mut tampered = first;
        tampered[..32].copy_from_slice(&second[..32]);
        assert!(crypto.open("job", "page", &tampered).is_err());
    }

    #[test]
    fn concurrent_initialization_uses_one_durable_key() {
        let root = std::env::temp_dir()
            .join(format!("export-local-{}", terrabase::identity::DatabaseUlid::generate()));
        let keys = std::thread::scope(|scope| {
            let threads: Vec<_> = (0..8)
                .map(|_| {
                    let root = &root;
                    scope.spawn(move || private_key(root, ".export-artifact-key").unwrap())
                })
                .collect();
            threads.into_iter().map(|thread| thread.join().unwrap()).collect::<Vec<_>>()
        });
        assert!(keys.iter().all(|key| key == &keys[0]));
        assert_eq!(private_key(&root, ".export-artifact-key").unwrap(), keys[0]);
        fs::remove_dir_all(root).unwrap();
    }

    #[tokio::test]
    async fn oversized_staging_member_fails_before_allocating_its_payload() {
        let root = std::env::temp_dir()
            .join(format!("export-local-{}", terrabase::identity::DatabaseUlid::generate()));
        let port = local_artifacts(&root, "db", "tenant").unwrap();
        let destination = ExportDestination::File { root: root.clone(), region: RegionCode::GRU };
        let mut writer = port
            .open("job", "database", &destination, &PlacementLock::single(RegionCode::GRU))
            .await
            .unwrap();
        let oversized = root.join("database/job/oversized.json");
        File::create(&oversized).unwrap().set_len((MAX_METADATA_BYTES + 4097) as u64).unwrap();
        assert!(matches!(writer.existing_facts().await, Err(ExportError::DestinationUnavailable)));
        assert!(matches!(
            writer.put("oversized.json", b"small").await,
            Err(ExportError::DestinationUnavailable)
        ));
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn rejects_public_key_file() {
        let root = std::env::temp_dir()
            .join(format!("export-local-{}", terrabase::identity::DatabaseUlid::generate()));
        local_cursor_key(&root).unwrap();
        fs::set_permissions(root.join(".export-cursor-key"), fs::Permissions::from_mode(0o644))
            .unwrap();
        assert!(local_cursor_key(&root).is_err());
        fs::remove_dir_all(root).unwrap();
    }
}
