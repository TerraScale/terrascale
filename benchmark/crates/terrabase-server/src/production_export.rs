//! Production export owner construction.
//!
//! Production exports must use an authenticated artifact factory supplied by
//! the host composition.  This module intentionally has no local fallback.

use crate::export_api::{ExportArtifactFactory, ExportDestination, ExportRuntime};
use std::path::Path;
use std::sync::Arc;

/// Redacted production export construction failures.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ProductionExportError {
    /// Cursor key reference was missing, unsafe, or malformed.
    CursorKey,
    /// The export runtime rejected its authenticated destination.
    Runtime,
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
