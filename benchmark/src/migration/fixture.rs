//! The synthetic V1-representation fixture producer (test-only).
//!
//! Builds representation directories from typed [`MigrationRecord`] values
//! so the in-crate migration tests state their contract against a
//! deterministic, documented artifact instead of a live V1 store — no V1
//! database file is ever opened. The producer enforces the format's order
//! contract: document records must arrive sorted by
//! `(tenant, collection, id)` with ascending versions per key.

use std::collections::BTreeMap;
use std::fmt::Write as _;
use std::path::Path;

use super::codec;
use super::{MigrationKind, MigrationRecord};

/// The canonical segment-name prefix (mirrors the production constant).
pub(super) const SEGMENT_PREFIX: &str = "segment-";

/// The number of records per fixture segment, so fixtures exercise
/// multi-segment iteration.
const RECORDS_PER_SEGMENT: usize = 4;

/// The manifest file name (mirrors the production constant).
const MANIFEST_FILE: &str = "manifest.tbv1m";

/// Writes one representation directory and returns its identity.
///
/// # Errors
///
/// Returns [`std::io::Error`] when the directory or a segment cannot be
/// written, and fails when the record order violates the format contract.
pub(super) fn write_export(
    directory: &Path,
    records: &[MigrationRecord],
) -> Result<String, std::io::Error> {
    validate_document_order(records)?;
    std::fs::create_dir_all(directory)?;
    let mut counts: BTreeMap<MigrationKind, u64> = BTreeMap::new();
    for record in records {
        *counts.entry(record.kind()).or_default() += 1;
    }
    let mut members = Vec::new();
    for (index, chunk) in records.chunks(RECORDS_PER_SEGMENT).enumerate() {
        let mut buffer = Vec::new();
        for record in chunk {
            codec::encode_into(&mut buffer, record);
        }
        let name = format!("{SEGMENT_PREFIX}{index:06}");
        let path = directory.join(&name);
        std::fs::write(&path, &buffer)?;
        members.push((name, buffer.len() as u64, identity_of(&buffer)));
    }
    // The identity binds every line except the identity line itself —
    // including the header — exactly as the production parser recomputes it.
    let mut body = String::from("terrabase-v1-export 1\n");
    body.push_str(&manifest_lines(&counts, &members));
    let identity = identity_of(body.as_bytes());
    let mut manifest = String::from("terrabase-v1-export 1\n");
    let _ = writeln!(manifest, "identity {identity}");
    manifest.push_str(&manifest_lines(&counts, &members));
    std::fs::write(directory.join(MANIFEST_FILE), manifest)?;
    Ok(identity)
}

/// Copies every file of one representation directory to another.
///
/// # Errors
///
/// Returns [`std::io::Error`] on any read or write failure.
pub(super) fn copy_export(source: &Path, destination: &Path) -> Result<(), std::io::Error> {
    std::fs::create_dir_all(destination)?;
    for entry in std::fs::read_dir(source)? {
        let entry = entry?;
        let name = entry.file_name();
        std::fs::copy(entry.path(), destination.join(name))?;
    }
    Ok(())
}

/// Computes the lowercase-hex SHA-256 of `bytes`.
pub(super) fn identity_of(bytes: &[u8]) -> String {
    use super::digest::{Sha256, to_hex};
    let mut hasher = Sha256::new();
    hasher.update(bytes);
    to_hex(&hasher.finish())
}

/// Renders the manifest lines the identity binds.
fn manifest_lines(
    counts: &BTreeMap<MigrationKind, u64>,
    members: &[(String, u64, String)],
) -> String {
    let mut text = String::new();
    for (kind, count) in counts {
        let _ = writeln!(text, "count {} {count}", codec::kind_token(*kind));
    }
    for (name, length, digest) in members {
        let _ = writeln!(text, "member {name} {length} {digest}");
    }
    text
}

/// Enforces the format's document-order contract.
fn validate_document_order(records: &[MigrationRecord]) -> Result<(), std::io::Error> {
    let invalid = || {
        std::io::Error::new(
            std::io::ErrorKind::InvalidInput,
            "document records must be sorted by (tenant, collection, id) with ascending versions",
        )
    };
    let mut last: Option<(String, String, String)> = None;
    let mut last_version = 0u64;
    for record in records {
        if let MigrationRecord::Document { tenant, collection, id, version, .. } = record {
            let key = (
                tenant.as_str().to_owned(),
                collection.as_str().to_owned(),
                id.as_str().to_owned(),
            );
            match &last {
                Some(open) if *open == key => {
                    if *version <= last_version {
                        return Err(invalid());
                    }
                }
                Some(open) if *open > key => return Err(invalid()),
                _ => {}
            }
            last_version = *version;
            last = Some(key);
        }
    }
    Ok(())
}
