//! Production export owner construction.
//!
//! Production exports must use an authenticated artifact factory supplied by
//! the host composition.  This module intentionally has no local fallback.

use crate::backup::object_store::ObjectDestination;
use crate::export_api::{
    ArtifactFact, ArtifactPort, ArtifactWriter, EncryptionAttestation, ExportArtifactFactory,
    ExportDestination, ExportError, ExportFuture, ExportRuntime, PlacementLock, fact,
    safe_member_path,
};
use crate::export_artifact_crypto::ArtifactCrypto;
use std::path::Path;
use std::path::PathBuf;
use std::sync::Arc;

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

impl ArtifactPort for ProductionArtifactPort {
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
            let encrypted = self
                .destination
                .get_member(&format!("{base}/{member}"))
                .await
                .map_err(|_| ExportError::DependencyUnavailable)?;
            if fact(member, &encrypted) != *manifest {
                return Err(ExportError::IdempotencyConflict);
            }
            self.crypto.open(&format!("{database}/{job}"), member, &encrypted)
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
            Ok(Box::new(crate::export_artifact_crypto::LocalWriter {
                directory,
                job_id: format!("{database}/{job}"),
                crypto: Arc::clone(&self.crypto),
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

    #[test]
    fn cursor_key_reference_requires_exact_nonzero_key() {
        let path =
            std::env::temp_dir().join(format!("terrabase-export-key-{}", std::process::id()));
        std::fs::write(&path, [0_u8; 31]).expect("fixture");
        assert!(crate::export_artifact_crypto::read_cursor_key(&path).is_err());
        std::fs::write(&path, [0_u8; 32]).expect("fixture");
        assert!(crate::export_artifact_crypto::read_cursor_key(&path).is_err());
        let mut valid = [0_u8; 32];
        valid[0] = 1;
        std::fs::write(&path, valid).expect("fixture");
        assert_eq!(crate::export_artifact_crypto::read_cursor_key(&path).expect("key"), valid);
        let _ = std::fs::remove_file(path);
    }

    #[test]
    fn errors_are_redacted() {
        assert_eq!(format!("{:?}", ProductionExportError::CursorKey), "CursorKey");
    }
}
