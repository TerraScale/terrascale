//! Production export owner construction.
//!
//! Production exports must use an authenticated artifact factory supplied by
//! the host composition.  This module intentionally has no local fallback.

use crate::export_api::{
    ArtifactFact, ArtifactPort, ArtifactWriter, EncryptionAttestation, ExportArtifactFactory,
    ExportDestination, ExportError, ExportFuture, ExportRuntime, PlacementLock, fact,
    publish_artifact, safe_member_path,
};
use crate::export_artifact_crypto::{ArtifactCrypto, LocalManifest};
use std::path::Path;
use std::path::PathBuf;
use std::sync::Arc;
use terrabase::backup::object_store::ObjectDestination;

/// Redacted production export construction failures.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ProductionExportError {
    /// Cursor key reference was missing, unsafe, or malformed.
    CursorKey,
    /// The export runtime rejected its authenticated destination.
    Runtime,
}

pub(crate) struct ProductionArtifactPort {
    pub(crate) root: PathBuf,
    pub(crate) destination: Arc<ObjectDestination>,
    pub(crate) prefix: String,
    pub(crate) crypto: Arc<dyn ArtifactCrypto>,
}

/// Resolves export artifacts from the already admitted backup owner.
pub(crate) struct ProductionArtifactFactory {
    pub(crate) resolver: Arc<
        dyn Fn(&str) -> Result<crate::native_backup_runtime::NativeBackupInput, String>
            + Send
            + Sync,
    >,
    pub(crate) root: PathBuf,
    pub(crate) prefix: String,
}

impl ExportArtifactFactory for ProductionArtifactFactory {
    fn artifacts(&self, database_id: &str) -> Result<Arc<dyn ArtifactPort>, ExportError> {
        let input = (self.resolver)(database_id).map_err(|_| ExportError::DependencyUnavailable)?;
        let (_, destination) = input.destinations.first().ok_or(ExportError::PlacementMismatch)?;
        if !input.placement.allows(destination.admitted_region()) {
            return Err(ExportError::PlacementMismatch);
        }
        let lease = input.keys.lease().map_err(|_| ExportError::EncryptionUnavailable)?;
        let scope = lease.metadata().scope();
        if scope.database().to_string() != database_id {
            return Err(ExportError::IdempotencyConflict);
        }
        let tenant = scope.tenant().to_string();
        let crypto = Arc::new(crate::export_artifact_crypto::LeaseCrypto::new(
            lease,
            database_id.to_owned(),
            tenant,
        ));
        Ok(Arc::new(ProductionArtifactPort {
            root: self.root.clone(),
            destination: Arc::clone(destination),
            prefix: self.prefix.clone(),
            crypto,
        }))
    }
}

struct ProductionWriter {
    inner: crate::export_artifact_crypto::LocalWriter,
    destination: Arc<ObjectDestination>,
    prefix: String,
    database: String,
    job: String,
}

async fn put_remote_exact(
    destination: &ObjectDestination,
    key: &str,
    bytes: Vec<u8>,
) -> Result<(), ExportError> {
    match destination.put_member(key, bytes.clone()).await {
        Ok(()) => Ok(()),
        Err(_) => {
            let remote = destination
                .get_member(key)
                .await
                .map_err(|_| ExportError::DependencyUnavailable)?;
            if remote == bytes { Ok(()) } else { Err(ExportError::IdempotencyConflict) }
        }
    }
}

async fn reconcile_remote_facts(
    inner: &crate::export_artifact_crypto::LocalWriter,
    destination: &ObjectDestination,
    prefix: &str,
    database: &str,
    job: &str,
    facts: &[ArtifactFact],
) -> Result<(), ExportError> {
    for expected in facts {
        let path = inner.directory.join(&expected.path);
        let bytes = crate::export_artifact_crypto::encrypted_member(&path)?;
        if fact(&expected.path, &bytes) != *expected {
            return Err(ExportError::IdempotencyConflict);
        }
        let key = format!("{prefix}/{database}/{job}/{}", expected.path);
        put_remote_exact(destination, &key, bytes).await?;
    }
    Ok(())
}

impl ArtifactWriter for ProductionWriter {
    fn put<'a>(&'a mut self, path: &'a str, plaintext: &'a [u8]) -> ExportFuture<'a, ArtifactFact> {
        Box::pin(async move {
            let fact = self.inner.put(path, plaintext).await?;
            let bytes = std::fs::read(self.inner.directory.join(path))
                .map_err(|_| ExportError::DestinationUnavailable)?;
            put_remote_exact(
                &self.destination,
                &format!("{}/{}/{}/{}", self.prefix, self.database, self.job, path),
                bytes,
            )
            .await?;
            Ok(fact)
        })
    }
    fn existing_facts<'a>(&'a mut self) -> ExportFuture<'a, Vec<ArtifactFact>> {
        Box::pin(async move {
            let base = format!("{}/{}/{}", self.prefix, self.database, self.job);
            let remote_keys = self
                .destination
                .list_members(&base)
                .await
                .map_err(|_| ExportError::DependencyUnavailable)?;
            for key in remote_keys {
                let Some(member) = key.strip_prefix(&(base.clone() + "/")) else {
                    continue;
                };
                if member == "COMMITTED" || !safe_member_path(member) {
                    continue;
                }
                let bytes = self
                    .destination
                    .get_member(&key)
                    .await
                    .map_err(|_| ExportError::DependencyUnavailable)?;
                self.inner.crypto.open(&self.inner.job_id, member, &bytes)?;
                let target = self.inner.directory.join(member);
                crate::export_api::ensure_no_symlink_components(&target)?;
                if let Some(parent) = target.parent() {
                    std::fs::create_dir_all(parent)
                        .map_err(|_| ExportError::DestinationUnavailable)?;
                }
                publish_artifact(&target, &bytes)?;
            }
            let facts = self.inner.existing_facts().await?;
            reconcile_remote_facts(
                &self.inner,
                &self.destination,
                &self.prefix,
                &self.database,
                &self.job,
                &facts,
            )
            .await?;
            Ok(facts)
        })
    }
    fn commit(self: Box<Self>, manifest: &ArtifactFact) -> ExportFuture<'static, ()> {
        let manifest = manifest.clone();
        Box::pin(async move {
            let ProductionWriter { inner, destination, prefix, database, job } = *self;
            let mut inner = inner;
            let facts = inner.existing_facts().await?;
            reconcile_remote_facts(&inner, &destination, &prefix, &database, &job, &facts).await?;
            Box::new(inner).commit(&manifest).await?;
            let marker = format!("{}\t{}\t{}\n", manifest.path, manifest.length, manifest.checksum);
            put_remote_exact(
                &destination,
                &format!("{}/{}/{}/COMMITTED", prefix, database, job),
                marker.into_bytes(),
            )
            .await?;
            Ok(())
        })
    }
}

impl ArtifactPort for ProductionArtifactPort {
    fn delete_job<'a>(&'a self, job: &'a str, database: &'a str) -> ExportFuture<'a, ()> {
        Box::pin(async move {
            if !safe_member_path(job) || !safe_member_path(database) {
                return Err(ExportError::DestinationUnavailable);
            }
            let prefix = format!("{}/{}/{}", self.prefix, database, job);
            let keys = self
                .destination
                .list_members(&prefix)
                .await
                .map_err(|_| ExportError::DependencyUnavailable)?;
            for key in keys {
                let member = key
                    .strip_prefix(&(prefix.clone() + "/"))
                    .ok_or(ExportError::IdempotencyConflict)?;
                if !safe_member_path(member) {
                    return Err(ExportError::DestinationUnavailable);
                }
                self.destination
                    .delete_member(&key)
                    .await
                    .map_err(|_| ExportError::DependencyUnavailable)?;
            }
            let directory = self.root.join(database).join(job);
            crate::export_api::ensure_no_symlink_components(&directory)?;
            match std::fs::remove_dir_all(directory) {
                Ok(()) => Ok(()),
                Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(()),
                Err(error) => Err(ExportError::Io(error.to_string())),
            }
        })
    }

    fn attestation(&self) -> Option<EncryptionAttestation> {
        Some(EncryptionAttestation {
            provider: "oci-vault".into(),
            key_version: "backup-export-v1".into(),
        })
    }

    fn read_member<'a>(
        &'a self,
        job: &'a str,
        database: &'a str,
        member: &'a str,
        manifest: &'a ArtifactFact,
    ) -> ExportFuture<'a, Vec<u8>> {
        Box::pin(async move {
            if !safe_member_path(job) || !safe_member_path(database) || !safe_member_path(member) {
                return Err(ExportError::DestinationUnavailable);
            }
            let base = format!("{}/{}/{}", self.prefix, database, job);
            let marker = self
                .destination
                .get_member(&format!("{base}/COMMITTED"))
                .await
                .map_err(|_| ExportError::DependencyUnavailable)?;
            let expected =
                format!("{}\t{}\t{}\n", manifest.path, manifest.length, manifest.checksum);
            if marker != expected.as_bytes() {
                return Err(ExportError::IdempotencyConflict);
            }
            let manifest_encrypted = self
                .destination
                .get_member(&format!("{base}/manifest.json"))
                .await
                .map_err(|_| ExportError::DependencyUnavailable)?;
            if fact("manifest.json", &manifest_encrypted) != *manifest {
                return Err(ExportError::IdempotencyConflict);
            }
            let binding = format!("{database}/{job}");
            let manifest_plaintext =
                self.crypto.open(&binding, "manifest.json", &manifest_encrypted)?;
            if member == "manifest.json" {
                return Ok(manifest_plaintext);
            }
            let manifest_members: LocalManifest = serde_json::from_slice(&manifest_plaintext)
                .map_err(|_| ExportError::IdempotencyConflict)?;
            let expected = manifest_members
                .members
                .iter()
                .find(|expected| expected.path == member)
                .ok_or(ExportError::DestinationUnavailable)?;
            let encrypted = self
                .destination
                .get_member(&format!("{base}/{member}"))
                .await
                .map_err(|_| ExportError::DependencyUnavailable)?;
            if fact(member, &encrypted) != *expected {
                return Err(ExportError::IdempotencyConflict);
            }
            self.crypto.open(&binding, member, &encrypted)
        })
    }

    fn open<'a>(
        &'a self,
        job: &'a str,
        database: &'a str,
        destination: &'a ExportDestination,
        placement: &'a PlacementLock,
    ) -> ExportFuture<'a, Box<dyn ArtifactWriter>> {
        Box::pin(async move {
            let ExportDestination::ObjectStorage { region, prefix, .. } = destination else {
                return Err(ExportError::DestinationUnavailable);
            };
            if !placement.allows(*region)
                || prefix != &self.prefix
                || !safe_member_path(job)
                || !safe_member_path(database)
            {
                return Err(ExportError::PlacementMismatch);
            }
            let directory = self.root.join(database).join(job);
            std::fs::create_dir_all(&directory).map_err(|_| ExportError::DestinationUnavailable)?;
            Ok(Box::new(ProductionWriter {
                inner: crate::export_artifact_crypto::LocalWriter {
                    directory,
                    job_id: format!("{database}/{job}"),
                    crypto: Arc::clone(&self.crypto),
                },
                destination: Arc::clone(&self.destination),
                prefix: self.prefix.clone(),
                database: database.into(),
                job: job.into(),
            }) as Box<dyn ArtifactWriter>)
        })
    }
}

/// Constructs the production export owner from an authenticated artifact
/// provider and an owner-private cursor signing-key reference.
pub fn new_production_export(
    jobs_root: impl Into<std::path::PathBuf>,
    artifact_factory: Arc<dyn ExportArtifactFactory>,
    destination: ExportDestination,
    cursor_key_file: &Path,
) -> Result<ExportRuntime, ProductionExportError> {
    let key = crate::export_artifact_crypto::read_cursor_key(cursor_key_file)
        .map_err(|_| ProductionExportError::CursorKey)?;
    ExportRuntime::new(jobs_root.into(), artifact_factory, destination, key)
        .map_err(|_| ProductionExportError::Runtime)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::export_artifact_crypto::LocalCrypto;
    use object_store::memory::InMemory;
    use terrabase::backup::object_store::Provider;

    #[test]
    fn cursor_key_reference_requires_exact_nonzero_key() {
        let path =
            std::env::temp_dir().join(format!("terrabase-export-key-{}", std::process::id()));
        std::fs::write(&path, [0_u8; 31]).expect("fixture");
        #[cfg(unix)]
        {
            use std::os::unix::fs::PermissionsExt;
            std::fs::set_permissions(&path, std::fs::Permissions::from_mode(0o600))
                .expect("private fixture");
        }
        assert!(crate::export_artifact_crypto::read_cursor_key(&path).is_err());
        std::fs::write(&path, [0_u8; 32]).expect("fixture");
        #[cfg(unix)]
        {
            use std::os::unix::fs::PermissionsExt;
            std::fs::set_permissions(&path, std::fs::Permissions::from_mode(0o600))
                .expect("private fixture");
        }
        assert!(crate::export_artifact_crypto::read_cursor_key(&path).is_err());
        let mut valid = [0_u8; 32];
        valid[0] = 1;
        std::fs::write(&path, valid).expect("fixture");
        assert_eq!(crate::export_artifact_crypto::read_cursor_key(&path).expect("key"), valid);
        #[cfg(unix)]
        {
            use std::os::unix::fs::PermissionsExt;
            std::fs::set_permissions(&path, std::fs::Permissions::from_mode(0o644))
                .expect("public fixture");
            assert!(crate::export_artifact_crypto::read_cursor_key(&path).is_err());
        }
        let _ = std::fs::remove_file(path);
    }

    #[test]
    fn errors_are_redacted() {
        assert_eq!(format!("{:?}", ProductionExportError::CursorKey), "CursorKey");
    }

    #[tokio::test]
    async fn remote_member_requires_manifest_authorization() {
        let destination = Arc::new(ObjectDestination::for_test(
            Provider::Oci,
            Arc::new(InMemory::new()),
            1024 * 1024,
            4096,
        ));
        let crypto =
            Arc::new(LocalCrypto { key: [9; 32], database: "db".into(), tenant: "t".into() });
        let binding = "db/job";
        let page_ciphertext = crypto.seal(binding, "page", b"page-data").unwrap();
        let page_fact = fact("page", &page_ciphertext);
        let manifest_plaintext =
            serde_json::json!({ "members": [page_fact.clone()] }).to_string().into_bytes();
        let manifest_ciphertext =
            crypto.seal(binding, "manifest.json", &manifest_plaintext).unwrap();
        let manifest_fact = fact("manifest.json", &manifest_ciphertext);
        let base = "exports/db/job";
        destination
            .put_member(&format!("{base}/manifest.json"), manifest_ciphertext)
            .await
            .unwrap();
        destination.put_member(&format!("{base}/page"), page_ciphertext).await.unwrap();
        destination
            .put_member(
                &format!("{base}/COMMITTED"),
                format!("manifest.json\t{}\t{}\n", manifest_fact.length, manifest_fact.checksum)
                    .into_bytes(),
            )
            .await
            .unwrap();
        // The object exists and decrypts, but the committed manifest does not
        // authorize it. Denial must therefore enforce membership, not absence.
        let secret_ciphertext = crypto.seal(binding, "secret", b"secret-data").unwrap();
        let secret_fact = fact("secret", &secret_ciphertext);
        destination.put_member(&format!("{base}/secret"), secret_ciphertext).await.unwrap();
        let root =
            std::env::temp_dir().join(format!("terrabase-export-resume-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&root);
        let port = ProductionArtifactPort {
            root: root.clone(),
            destination,
            prefix: "exports".into(),
            crypto,
        };
        assert_eq!(
            port.read_member("job", "db", "page", &manifest_fact).await.unwrap(),
            b"page-data"
        );
        assert!(matches!(
            port.read_member("job", "db", "secret", &manifest_fact).await,
            Err(ExportError::DestinationUnavailable)
        ));
        let destination_config = ExportDestination::ObjectStorage {
            endpoint: "https://example.invalid".into(),
            bucket: "exports".into(),
            prefix: "exports".into(),
            region: terrabase::regions::RegionCode::GRU,
        };
        let placement = PlacementLock::single(terrabase::regions::RegionCode::GRU);
        let mut writer = port.open("job", "db", &destination_config, &placement).await.unwrap();
        assert_eq!(
            writer.existing_facts().await.unwrap(),
            vec![manifest_fact.clone(), page_fact, secret_fact]
        );
        // Resume inventories ciphertext, but does not grant read authorization.
        assert!(matches!(
            port.read_member("job", "db", "secret", &manifest_fact).await,
            Err(ExportError::DestinationUnavailable)
        ));
        assert!(root.join("db/job/page").exists());
        drop(writer);
        let _ = std::fs::remove_dir_all(root);
    }

    #[tokio::test]
    async fn expired_job_cleanup_removes_remote_members_and_local_cache() {
        let root =
            std::env::temp_dir().join(format!("terrabase-export-cleanup-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&root);
        let destination = Arc::new(ObjectDestination::for_test(
            Provider::Oci,
            Arc::new(InMemory::new()),
            1024 * 1024,
            4096,
        ));
        let port = ProductionArtifactPort {
            root: root.clone(),
            destination: Arc::clone(&destination),
            prefix: "exports".into(),
            crypto: Arc::new(LocalCrypto {
                key: [3; 32],
                database: "db".into(),
                tenant: "t".into(),
            }),
        };
        std::fs::create_dir_all(root.join("db/job")).unwrap();
        std::fs::write(root.join("db/job/member"), b"cached").unwrap();
        destination.put_member("exports/db/job/member", b"remote".to_vec()).await.unwrap();
        port.delete_job("job", "db").await.unwrap();
        assert!(destination.list_members("exports/db/job").await.unwrap().is_empty());
        assert!(!root.join("db/job").exists());
        let _ = std::fs::remove_dir_all(root);
    }
}
