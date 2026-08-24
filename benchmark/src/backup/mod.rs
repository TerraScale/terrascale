//! Declaration-only backup and restore seam: manifest-verified archives into
//! a clean target.
//!
//! This module declares the backup vocabulary of the W4 design contract
//! (`docs/design/w4-transfer-replication-contract.md`, "Public type
//! ownership"). No backup behavior exists here: archives, manifests, restore
//! verification, and the archive codec are implemented by the W4-BACKUP
//! packet.
//!
//! Contract facts the declarations state: archives and manifests bind ordered
//! member names, lengths, and SHA-256 digests; restore and import verify
//! before any target is materialized; backup admission allows one job at a
//! time per source; a green progress status cannot hide loss; and no archive
//! byte, WAL frame, file offset, manifest path, or private host path crosses
//! this seam.

#![allow(dead_code)]

use std::marker::PhantomData;

use crate::types::StorageSequence;

/// An immutable recovery-point identity plus the ordered archive members it
/// binds (PB-6010, PB-6015, PB-5013..PB-5015).
///
/// The manifest binds ordered archive names, lengths, and SHA-256 digests.
/// Validation rejects path traversal, in-progress manifests, and identity
/// mismatches before any target is created.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct BackupManifest {
    manifest: PhantomData<()>,
}

/// One staged archive with its digest and member counts (PB-6010).
///
/// Archive internals — the codec, incremental chain internals, and staging
/// directory management — stay private to this module; archive bytes never
/// leave it.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct BackupArchive {
    archive: PhantomData<()>,
}

/// The kind of one backup (PB-6010).
///
/// Snapshot-mode backup is a later disposition, not a W4 kind.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum BackupKind {
    /// A full backup of the selected items at the source boundary.
    Full,
    /// An incremental backup covering the journal-range boundary since its
    /// base recovery point.
    Incremental {
        /// The storage-sequence boundary of the base recovery point this
        /// incremental backup builds on.
        base: StorageSequence,
    },
}

/// A validated backup request: source snapshot boundary, kind, item
/// selection, and throttling bound (PB-6010, PB-6017).
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct BackupPlan {
    plan: PhantomData<()>,
}

/// Non-secret progress facts for one running backup (PB-6021, PB-6035).
///
/// Carries counts, the current step, and skipped-item reporting; a green
/// status cannot hide loss.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct BackupProgress {
    progress: PhantomData<()>,
}

/// The terminal result of one backup run.
///
/// Carries the manifest and a reference to the `RecoveryReceipt` produced by
/// the qualification lane; it carries no archive content.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct BackupResult {
    result: PhantomData<()>,
}

/// A validated restore request: manifest identity, clean target location, and
/// verification options (PB-6016).
///
/// Restore refuses a non-empty or unrelated target and consumes the complete
/// ordered incremental chain, rejecting a missing, reordered, truncated, or
/// corrupt member before materializing anything.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct RestorePlan {
    plan: PhantomData<()>,
}

/// The terminal result of one restore run.
///
/// Carries the restored identity, counts, checksum verdicts, and explicit
/// limits. Verification reopens the target as a real durable `Engine` and
/// reads back representative and boundary values — a clean-restore
/// verification, not a trust-me copy.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct RestoreResult {
    result: PhantomData<()>,
}

/// Typed backup and restore failure categories.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum BackupError {
    /// The manifest failed validation before any target was created.
    ManifestInvalid,
    /// A digest or length verification failed.
    ChecksumMismatch,
    /// The incremental chain is missing, reordered, truncated, or otherwise
    /// incomplete.
    ChainIncomplete,
    /// The restore target is non-empty or unrelated.
    TargetNotEmpty,
    /// The backup source is unavailable.
    SourceUnavailable,
    /// The throttle bound delayed or rejected the operation.
    Throttled,
    /// The operation was cancelled; no usable partial recovery point remains.
    Cancelled,
    /// The `Engine` has closed.
    EngineClosed,
}

/// The typed family-record input produced from the reserved collection
/// `terrabase.families` (integrator action W4-CA-5).
///
/// Crate-private by contract and never re-exported from the crate root: it
/// lets the families seam feed backup and migration without a public type or
/// a families-owned backup type. Population belongs to W4-CA-5 and
/// consumption to W4-BACKUP.
#[derive(Clone, Debug, Eq, PartialEq)]
pub(crate) struct FamilyRecordInput {
    record: PhantomData<()>,
}
