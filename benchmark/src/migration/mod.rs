//! Declaration-only Logical Migration seam: manifest-verified V1 export
//! records transformed through the Engine transaction seam.
//!
//! This module declares the migration vocabulary of the W4 design contract
//! (`docs/design/w4-transfer-replication-contract.md`, "Public type
//! ownership"). No migration behavior exists here: V1 record parsing, resume
//! journaling, and transformation are implemented by the W4-BACKUP packet.
//!
//! Contract facts the declarations state: input and output are checked
//! against a manifest before any write; physical V1 layout never crosses this
//! seam — public types are logical records only; migration writes V2 data
//! exclusively through the Engine transaction seam, never storage files
//! directly; each run is restartable and idempotent by `OperationId`; success
//! reports an explicit loss matrix and never means "everything in V1 arrived";
//! and no claim is made that V2 opens V1 files directly.

#![allow(dead_code)]

use std::marker::PhantomData;

/// A verified V1-export identity (PB-6022).
///
/// Binds the format version, record counts per item type, and per-segment
/// digests. The manifest is validated before any write.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct MigrationManifest {
    manifest: PhantomData<()>,
}

/// One logical input record with its kind and identity (PB-6019).
///
/// Documents, revisions, tombstones, attachments, counters, time-series
/// values and deleted ranges, identities, and compare-exchange values are
/// logical records; the physical V1 layout never crosses this type.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct MigrationRecord {
    record: PhantomData<()>,
}

/// Migration request options: item selection, revision policy, batch and
/// throttle bounds.
///
/// Tombstone retention and index re-derivation policy are stated here, never
/// decided silently.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct MigrationOptions {
    options: PhantomData<()>,
}

/// The terminal report of one migration run.
///
/// Carries converted counts, the per-category loss matrix, the resume
/// position, and verification verdicts. Every dropped category appears with a
/// count and its reason; nothing is dropped silently (PB-6021, PB-6018).
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct MigrationReport {
    report: PhantomData<()>,
}

/// The explicit converted/dropped table for one migration run.
///
/// Each category — documents and revisions, referenced attachments,
/// tombstones and deleted ranges, counters and time-series values, identities
/// and compare-exchange values, V1 storage-internal structures, unreferenced
/// blobs, Platform-owned configuration, and index definitions — appears as
/// converted or dropped with a reason per dropped category.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct MigrationLossMatrix {
    matrix: PhantomData<()>,
}

/// Typed Logical Migration failure categories.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum MigrationError {
    /// The export manifest failed validation before any write.
    ManifestInvalid,
    /// An input record is corrupt.
    RecordCorrupt,
    /// An input record is truncated.
    RecordTruncated,
    /// The export's format version is unsupported.
    UnsupportedVersion,
    /// The run cannot resume from its recorded position.
    NotResumable,
    /// The target state is incompatible with the requested migration.
    TargetIncompatible,
    /// The operation was cancelled.
    Cancelled,
}
