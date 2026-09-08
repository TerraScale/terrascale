//! Restore selection and execution, and the restore lane's Recovery
//! Receipts (BK-02, PB-6015/PB-6016).
//!
//! This module realizes the restore contract BK-01 declared: selection of
//! which recovery point to restore (and at which chain depth), verified
//! execution into a fresh engine location, fail-closed handling of corrupt
//! or truncated artifacts, and the terminal [`RecoveryReceipt`] — the
//! secret-safe record of what one restore run used, produced, and left
//! open. The public entries are the associated functions on
//! [`crate::backup::RestorePlan`]:
//!
//! - [`RestorePlan::restore_latest`] selects the destination's newest
//!   recovery point and restores the complete ordered chain behind it, and
//! - [`RestorePlan::restore_recovery_point`] selects exactly one named
//!   sealed recovery point and restores the chain **down to and including**
//!   it — an older chain depth, never a longer one.
//!
//! # Execution and fail-closed rules
//!
//! A restore consumes a destination exactly as the backup core sealed it.
//! Chain selection validates sealed manifests and link metadata before
//! materialization. Replay then opens each member once, streams its record
//! frames through a bounded read window — a member is never held in memory —
//! hashes the same bytes as they are decoded, and compares that digest with
//! the manifest before publication. A tampered or truncated member is
//! [`BackupError::ChecksumMismatch`] and a missing member or link is
//! [`BackupError::ChainIncomplete`]. Replay re-publishes records through the
//! storage publication seam (the same seam the `Engine` transaction path
//! lowers to) as batches of bounded write count at `Synced` durability.
//! Per-link decoded counts must reproduce the manifest's bound counts
//! exactly: documents, family records, deletes, and (for incremental links)
//! durability disclosures. Any divergence — including a digest-consistent
//! but forged member whose frames decode to something else — is
//! [`BackupError::ChecksumMismatch`].
//! There is no partial-success outcome anywhere in this module.
//!
//! The target is a fresh engine location, and publication is atomic at
//! directory granularity exactly as a backup recovery point is: the complete
//! engine is built inside a staging sibling of the target and renamed into
//! place as one atomic rename. A restore that fails, is interrupted, or
//! crashes before that rename leaves **no target directory at all** — only
//! a stale staging sibling that no later run reuses — so a target either
//! does not exist or is completely restored. A non-empty or unrelated
//! target is refused with [`BackupError::TargetNotEmpty`] before anything
//! is touched, including a target that already holds a completed restore.
//!
//! Verification reopens durable storage and compares every restored key and
//! canonical document value against a per-key digest independently derived
//! from archive replay. Reads use bounded pages with a full collection/id
//! cursor. The receipt reports a domain-separated logical checksum over
//! tenant, collection, identifier and canonical value, excluding re-minted
//! document versions and storage sequences. Replay retains keys and fixed-size
//! digests, plus bounded write batches and one representative value per tenant.
//! Schema enforcement does not run during internal restore publication.

mod decode;

use std::collections::{BTreeMap, BTreeSet};
use std::fs::File;
use std::io::Read;
use std::path::{Path, PathBuf};

use decode::{decode_member, decode_member_bytes};

use crate::backup::BackupReader;
use crate::backup::manifest::ENCRYPTED_MANIFEST_FILE;
use crate::backup::manifest::point_directory;
use crate::backup::records::Record;
use crate::backup::{
    BackupError, BackupKind, BackupManifest, PlacementLock, RestorePlan, discover_points,
    select_latest_chain, validate_chain_frontier,
};
use crate::engine::{Engine, EngineConfig};
use crate::regions::RegionCode;
use crate::storage::{
    CommitIntent, CommitResult, PrivateMetadataWrite, SqliteStorage, StagedWrite, StoragePort,
    write_set_digest,
};
use crate::types::{
    CollectionName, Document, DocumentId, ExpectedVersion, LocalDurability, OperationId,
    StorageSequence, TenantId,
};

/// The reserved family collection, mirrored from the family contract (the
/// canonical constant stays private to the family state module; the query
/// and streaming seams keep their own spelled copies, as does this one).
const RESERVED_FAMILY_COLLECTION: &str = "terrabase.families";

/// The storage format label the receipt cites. The canonical definition
/// (`FORMAT_MAGIC`/`FORMAT_VERSION`) is private to the file adapter; this
/// spelled copy follows the repository's cross-module constant convention,
/// and the durable open path enforces the real bytes behind it.
const STORAGE_FORMAT_LABEL: &str = "TBV1FORMAT 1";

/// The maximum write count of one replayed publication batch. Records
/// stream from member files into batches of at most this size, so no
/// replayed commit ever holds an unbounded write set.
const MAX_BATCH_WRITES: usize = 256;

/// The page size of the read-back verification window (W4-CA-7's bounded
/// snapshot read path).
const READBACK_PAGE: u32 = 256;

// ---------------------------------------------------------------------------
// Selection
// ---------------------------------------------------------------------------

/// Which recovery point one restore selects.
pub(super) enum Selection {
    /// The destination's newest chain head, with its complete chain.
    Latest,
    /// Exactly the named sealed recovery point, with the chain down to and
    /// including it.
    Point { identity: String },
}

/// Resolves the ordered chain (root first) a selection restores, after
/// strictly validating the sealed manifests and link metadata it names.
/// Member bytes are opened and authenticated during replay.
///
/// # Errors
///
/// Returns the typed failure of chain discovery or member verification:
/// [`BackupError::ChainIncomplete`] for an empty destination or a missing
/// link, [`BackupError::ManifestInvalid`] for a manifest that does not
/// validate, [`BackupError::ChecksumMismatch`] for a member whose length or
/// digest does not verify.
fn resolve_chain(
    destination: &Path,
    selection: &Selection,
) -> Result<Vec<BackupManifest>, BackupError> {
    let chain = match selection {
        // Select from sealed manifest metadata only. Member bytes are opened
        // and authenticated once by replay, avoiding a verify-then-reopen
        // pathname race.
        Selection::Latest => select_latest_chain(destination)?,
        Selection::Point { identity } => {
            let head = crate::backup::load_recovery_point(destination, identity)?;
            // A point directory's name is its identity: a manifest that
            // validates but claims a different identity than the directory
            // it was sealed in is not the recovery point the caller named.
            if head.identity() != identity {
                return Err(BackupError::ManifestInvalid);
            }
            chain_of_head(destination, &head)?
        }
    };
    if chain.is_empty() {
        return Err(BackupError::ChainIncomplete);
    }
    validate_chain_frontier(&chain)?;
    Ok(chain)
}

/// Resolves a chain whose manifest and members use the authenticated current
/// backup format. Encrypted points are deliberately a separate namespace from
/// the legacy plaintext reader, so a mixed destination cannot be accepted.
fn resolve_encrypted_chain(
    destination: &Path,
    cipher: &dyn BackupReader,
    selection: &Selection,
) -> Result<Vec<BackupManifest>, BackupError> {
    let points = discover_encrypted_points(destination, cipher)?;
    resolve_encrypted_points_chain(&points, selection)
}

pub(super) fn resolve_encrypted_points_chain(
    points: &[BackupManifest],
    selection: &Selection,
) -> Result<Vec<BackupManifest>, BackupError> {
    let head = match selection {
        Selection::Latest => points
            .iter()
            .max_by(|left, right| {
                let rank = |point: &BackupManifest| {
                    (
                        point.boundary_sequence().get(),
                        point.captured_unix_millis(),
                        point.recoverable_through_unix_millis(),
                        point.base_sequence().is_some(),
                    )
                };
                rank(left).cmp(&rank(right)).then_with(|| left.identity().cmp(right.identity()))
            })
            .ok_or(BackupError::ChainIncomplete)?,
        Selection::Point { identity } => points
            .iter()
            .find(|point| point.identity() == identity)
            .ok_or(BackupError::ChainIncomplete)?,
    };
    indexed_chain(head, &index_chain_parents(points))
}

/// Empty incrementals add no source state and are never needed as parents.
/// Prefer the newest captured root at a boundary, otherwise one nonempty incremental. This
/// makes each parent step strictly decrease sequence, except a no-op head's
/// first step, and avoids repeated directory scans and no-op cycles.
fn index_chain_parents(points: &[BackupManifest]) -> BTreeMap<u64, &BackupManifest> {
    let mut parents: BTreeMap<u64, &BackupManifest> = BTreeMap::new();
    for point in points {
        let sequence = point.boundary_sequence().get();
        if point.base_sequence().is_some_and(|base| base.get() == sequence) {
            continue;
        }
        let rank = |candidate: &BackupManifest| {
            (candidate.base_sequence().is_none(), candidate.captured_unix_millis())
        };
        let replace = parents.get(&sequence).is_none_or(|prior| {
            rank(point) > rank(prior)
                || rank(point) == rank(prior) && point.identity() < prior.identity()
        });
        if replace {
            parents.insert(sequence, point);
        }
    }
    parents
}

fn indexed_chain(
    head: &BackupManifest,
    parents: &BTreeMap<u64, &BackupManifest>,
) -> Result<Vec<BackupManifest>, BackupError> {
    let mut current = head;
    let mut chain = vec![head.clone()];
    while let Some(base) = current.base_sequence() {
        current = parents.get(&base.get()).copied().ok_or(BackupError::ChainIncomplete)?;
        chain.push(current.clone());
    }
    chain.reverse();
    validate_chain_frontier(&chain)?;
    Ok(chain)
}

fn indexed_root<'a>(
    head: &'a BackupManifest,
    parents: &BTreeMap<u64, &'a BackupManifest>,
    roots: &mut BTreeMap<&'a str, &'a BackupManifest>,
) -> Result<&'a BackupManifest, BackupError> {
    let mut current = head;
    let mut path = Vec::new();
    let root = loop {
        if let Some(root) = roots.get(current.identity()) {
            break *root;
        }
        path.push(current);
        let Some(base) = current.base_sequence() else {
            break current;
        };
        current = parents.get(&base.get()).copied().ok_or(BackupError::ChainIncomplete)?;
    };
    for point in path {
        roots.insert(point.identity(), root);
    }
    Ok(root)
}

fn resolve_timestamp_chain(
    destination: &Path,
    cipher: &dyn BackupReader,
    requested: u64,
) -> Result<Vec<BackupManifest>, BackupError> {
    let points = discover_encrypted_points(destination, cipher)?;
    resolve_timestamp_points_chain(&points, requested)
}

pub(super) fn resolve_timestamp_points_chain(
    points: &[BackupManifest],
    requested: u64,
) -> Result<Vec<BackupManifest>, BackupError> {
    let parents = index_chain_parents(points);
    let mut roots = BTreeMap::new();
    let mut selected = None;
    let mut selected_rank = None;
    for point in points.iter().filter(|point| point.recoverable_through_unix_millis() >= requested)
    {
        let root = indexed_root(point, &parents, &mut roots)?;
        if root.boundary_unix_millis() > requested {
            continue;
        }
        let rank = (
            root.boundary_sequence().get(),
            point.boundary_sequence().get(),
            point.captured_unix_millis(),
            point.identity(),
        );
        if selected_rank.is_none_or(|prior| rank > prior) {
            selected = Some(point);
            selected_rank = Some(rank);
        }
    }
    indexed_chain(selected.ok_or(BackupError::ChainIncomplete)?, &parents)
}

#[cfg(test)]
#[test]
fn indexed_timestamp_roots_handle_long_chains_and_noop_heads() {
    use crate::backup::manifest::ManifestFacts;
    let point = |sequence: u64, kind: BackupKind| {
        BackupManifest::assemble(ManifestFacts {
            database_id: "0123456789abcdef0123456789abcdef".to_owned(),
            partition_id: "partition-000000".to_owned(),
            kind,
            boundary: StorageSequence::new(sequence),
            boundary_unix_millis: sequence,
            captured_unix_millis: sequence + 1,
            retain_until_unix_millis: sequence + 1 + 86_400_000,
            logical_checksum: crate::engine::logical_state_hasher().finalize().to_hex().to_string(),
            logical_documents: 0,
            documents: 0,
            family_records: 0,
            deletes: 0,
            disclosures: 0,
            tenants: Vec::new(),
            members: Vec::new(),
        })
    };
    let mut points = vec![point(0, BackupKind::Full)];
    for sequence in 1..=2048 {
        points.push(point(
            sequence,
            BackupKind::Incremental { base: StorageSequence::new(sequence - 1) },
        ));
    }
    points.push(point(2048, BackupKind::Incremental { base: StorageSequence::new(2048) }));
    let parents = index_chain_parents(&points);
    let mut roots = BTreeMap::new();
    for head in points.iter().rev() {
        assert_eq!(
            indexed_root(head, &parents, &mut roots).unwrap().identity(),
            points[0].identity()
        );
    }
    assert_eq!(roots.len(), points.len());
    let chain = indexed_chain(points.last().unwrap(), &parents).unwrap();
    assert_eq!(chain.len(), 2050);
    assert_eq!(chain.last().unwrap().boundary_sequence().get(), 2048);
}

#[cfg(test)]
#[test]
fn latest_same_boundary_prefers_capture_clock_and_complete_coverage() {
    use crate::backup::manifest::ManifestFacts;
    let point = |kind, captured| {
        BackupManifest::assemble(ManifestFacts {
            database_id: "0123456789abcdef0123456789abcdef".to_owned(),
            partition_id: "partition-000000".to_owned(),
            kind,
            boundary: StorageSequence::new(7),
            boundary_unix_millis: 10,
            captured_unix_millis: captured,
            retain_until_unix_millis: captured + 86_400_000,
            logical_checksum: crate::engine::logical_state_hasher().finalize().to_hex().to_string(),
            logical_documents: 0,
            documents: 0,
            family_records: 0,
            deletes: 0,
            disclosures: 0,
            tenants: Vec::new(),
            members: Vec::new(),
        })
    };
    let root = point(BackupKind::Full, 10);
    let snapshot = point(BackupKind::Snapshot, 20);
    let noop = point(BackupKind::Incremental { base: StorageSequence::new(7) }, 20);
    let later_noop = point(BackupKind::Incremental { base: StorageSequence::new(7) }, 30);
    for points in [vec![root.clone(), snapshot.clone()], vec![snapshot.clone(), root.clone()]] {
        let chain = resolve_encrypted_points_chain(&points, &Selection::Latest).unwrap();
        assert_eq!(chain.last().unwrap().identity(), snapshot.identity());
    }
    for points in [
        vec![root.clone(), snapshot.clone(), noop.clone()],
        vec![noop.clone(), snapshot.clone(), root.clone()],
    ] {
        let chain = resolve_encrypted_points_chain(&points, &Selection::Latest).unwrap();
        assert_eq!(chain.last().unwrap().identity(), noop.identity());
        assert_eq!(chain.last().unwrap().recoverable_through_unix_millis(), 20);
    }
    for points in [
        vec![later_noop.clone(), root.clone(), snapshot.clone(), noop.clone()],
        vec![noop.clone(), snapshot.clone(), root.clone(), later_noop.clone()],
    ] {
        let chain = resolve_encrypted_points_chain(&points, &Selection::Latest).unwrap();
        assert_eq!(chain.last().unwrap().identity(), later_noop.identity());
        assert_eq!(chain.first().unwrap().identity(), snapshot.identity());
        let historical = resolve_encrypted_points_chain(
            &points,
            &Selection::Point { identity: noop.identity().into() },
        )
        .unwrap();
        assert_eq!(historical.first().unwrap().identity(), snapshot.identity());
        assert_eq!(historical.last().unwrap().identity(), noop.identity());
        let timestamp = resolve_timestamp_points_chain(&points, 15).unwrap();
        assert_eq!(timestamp.first().unwrap().identity(), snapshot.identity());
        assert_eq!(timestamp.last().unwrap().identity(), later_noop.identity());
    }
}

pub(super) fn discover_encrypted_points(
    destination: &Path,
    cipher: &dyn BackupReader,
) -> Result<Vec<BackupManifest>, BackupError> {
    let entries = std::fs::read_dir(destination).map_err(|_| BackupError::ChainIncomplete)?;
    let mut points = Vec::new();
    for entry in entries {
        let entry = entry.map_err(|_| BackupError::ChainIncomplete)?;
        let name = entry.file_name();
        let Some(identity) = name.to_str().and_then(|name| name.strip_prefix("rp-")) else {
            continue;
        };
        if crate::backup::manifest::validate_identity(identity).is_err() {
            continue;
        }
        let point = entry.path();
        let metadata =
            std::fs::symlink_metadata(&point).map_err(|_| BackupError::ChainIncomplete)?;
        if !metadata.file_type().is_dir() {
            return Err(BackupError::ChainIncomplete);
        }
        let manifest_path = point.join(ENCRYPTED_MANIFEST_FILE);
        let metadata =
            std::fs::symlink_metadata(&manifest_path).map_err(|_| BackupError::ChainIncomplete)?;
        if !metadata.file_type().is_file() {
            return Err(BackupError::ChainIncomplete);
        }
        let mut envelope = Vec::new();
        File::open(&manifest_path)
            .map_err(|_| BackupError::ChainIncomplete)?
            .take((crate::encryption::MAX_PLAINTEXT_BYTES + 4096) as u64)
            .read_to_end(&mut envelope)
            .map_err(|_| BackupError::ChainIncomplete)?;
        let bytes =
            cipher.open_backup_envelope(&envelope).map_err(|_| BackupError::ChecksumMismatch)?;
        let manifest = BackupManifest::decode(&bytes)?;
        if manifest.identity() != identity {
            return Err(BackupError::ManifestInvalid);
        }
        points.push(manifest);
    }
    Ok(points)
}

/// Verifies the newest encrypted recovery-point chain, including manifest
/// and member authentication, without materializing a restore target.
///
/// # Errors
///
/// Returns [`BackupError::ChainIncomplete`] for missing points or members,
/// [`BackupError::ManifestInvalid`] for malformed manifests, and
/// [`BackupError::ChecksumMismatch`] for a digest or authentication failure.
pub fn verify_latest_chain_encrypted(
    destination: &Path,
    cipher: &dyn BackupReader,
) -> Result<Vec<BackupManifest>, BackupError> {
    let chain = resolve_encrypted_chain(destination, cipher, &Selection::Latest)?;
    for link in &chain {
        for member in link.members() {
            let path = destination.join(point_directory(link.identity())).join(member.name());
            let mut file = open_member(&path, member.length())?;
            if member.length() > (crate::encryption::MAX_PLAINTEXT_BYTES as u64 + 4096) {
                return Err(BackupError::ChecksumMismatch);
            }
            let capacity =
                usize::try_from(member.length()).map_err(|_| BackupError::ChecksumMismatch)?;
            let mut envelope = Vec::with_capacity(capacity);
            file.read_to_end(&mut envelope).map_err(|_| BackupError::ChainIncomplete)?;
            if !member.matches_bytes(&envelope) {
                return Err(BackupError::ChecksumMismatch);
            }
            cipher.open_backup_envelope(&envelope).map_err(|_| BackupError::ChecksumMismatch)?;
        }
    }
    Ok(chain)
}

/// Walks one selected head down to its chain root, preferring a chain root
/// over another link at the same base boundary and breaking remaining ties
/// by identity — the same deterministic choice `verify_latest_chain` makes.
fn chain_of_head(
    destination: &Path,
    head: &BackupManifest,
) -> Result<Vec<BackupManifest>, BackupError> {
    let points = discover_points(destination)?;
    let mut chain = vec![head.clone()];
    let mut visited = BTreeSet::from([head.identity().to_owned()]);
    while let Some(base) = chain.last().and_then(BackupManifest::base_sequence) {
        let link = points
            .iter()
            .filter(|point| {
                point.boundary_sequence() == base && !visited.contains(point.identity())
            })
            .min_by(|left, right| {
                (usize::from(left.base_sequence().is_some()), left.identity())
                    .cmp(&(usize::from(right.base_sequence().is_some()), right.identity()))
            })
            .ok_or(BackupError::ChainIncomplete)?;
        visited.insert(link.identity().to_owned());
        chain.push(link.clone());
    }
    chain.reverse();
    Ok(chain)
}

/// Opens one manifest member for replay after checking the pathname without
/// following a symlink. The returned handle is the only handle used for both
/// decoding and digesting, so replay never verifies one pathname and then
/// reopens it later.
fn open_member(path: &Path, expected_len: u64) -> Result<File, BackupError> {
    let path_metadata =
        std::fs::symlink_metadata(path).map_err(|_| BackupError::ChainIncomplete)?;
    if !path_metadata.file_type().is_file() {
        return Err(BackupError::ChainIncomplete);
    }
    let file = File::open(path).map_err(|_| BackupError::ChainIncomplete)?;
    let file_metadata = file.metadata().map_err(|_| BackupError::ChainIncomplete)?;
    if !file_metadata.file_type().is_file() {
        return Err(BackupError::ChainIncomplete);
    }
    // Confirm that the pathname still names the inode we opened. On Unix
    // this closes the stat/open race even when a member is replaced between
    // the initial symlink check and `File::open`; the replay continues to use
    // the already-open handle in every case.
    let current_path_metadata =
        std::fs::symlink_metadata(path).map_err(|_| BackupError::ChainIncomplete)?;
    if !current_path_metadata.file_type().is_file()
        || !same_file_identity(&path_metadata, &file_metadata)
        || !same_file_identity(&current_path_metadata, &file_metadata)
    {
        return Err(BackupError::ChainIncomplete);
    }
    if file_metadata.len() != expected_len {
        return Err(BackupError::ChecksumMismatch);
    }
    Ok(file)
}

#[cfg(unix)]
fn same_file_identity(left: &std::fs::Metadata, right: &std::fs::Metadata) -> bool {
    use std::os::unix::fs::MetadataExt;

    left.dev() == right.dev() && left.ino() == right.ino()
}

#[cfg(not(unix))]
fn same_file_identity(_left: &std::fs::Metadata, _right: &std::fs::Metadata) -> bool {
    true
}

// ---------------------------------------------------------------------------
// The receipt
// ---------------------------------------------------------------------------

/// One link of the restored chain, root first.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct RestoreChainLink {
    identity: String,
    kind: BackupKind,
    boundary: StorageSequence,
}

impl RestoreChainLink {
    /// Creates one link fact from the sealed manifest it summarizes.
    fn of(manifest: &BackupManifest) -> Self {
        Self {
            identity: manifest.identity().to_owned(),
            kind: manifest.kind(),
            boundary: manifest.boundary_sequence(),
        }
    }

    /// Returns the link's recovery-point identity.
    #[must_use]
    pub fn identity(&self) -> &str {
        &self.identity
    }

    /// Returns the backup kind the link sealed.
    #[must_use]
    pub fn kind(&self) -> BackupKind {
        self.kind
    }

    /// Returns the storage-sequence boundary the link covers.
    #[must_use]
    pub fn boundary_sequence(&self) -> StorageSequence {
        self.boundary
    }
}

/// One restored tenant's final live counts.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct RestoredTenant {
    tenant: TenantId,
    documents: u64,
    family_records: u64,
}

impl RestoredTenant {
    /// Creates one tenant fact from the replay's final key set.
    fn new(tenant: TenantId, documents: u64, family_records: u64) -> Self {
        Self { tenant, documents, family_records }
    }

    /// Returns the restored tenant.
    #[must_use]
    pub fn tenant(&self) -> &TenantId {
        &self.tenant
    }

    /// Returns the tenant's restored user-document count.
    #[must_use]
    pub fn documents(&self) -> u64 {
        self.documents
    }

    /// Returns the tenant's restored family-record count.
    #[must_use]
    pub fn family_records(&self) -> u64 {
        self.family_records
    }
}

/// The terminal result of one restore run: the Recovery Receipt contract
/// realized as typed facts (PB-6016 and the charter's recovery contract).
///
/// The receipt carries no path, no archive content, and no document value —
/// identities, counts, checksum verdicts, duplicate/missing handling, the
/// durability disclosures replayed, the verification the run performed, and
/// the limits that remain. [`RecoveryReceipt::render`] emits the same facts
/// as secret-safe text for the tracked lane receipts under
/// `docs/evidence/recovery/`.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct RecoveryReceipt {
    requested_unix_millis: Option<u64>,
    restored_source_sequence: StorageSequence,
    storage_format: &'static str,
    selected: RestoreChainLink,
    chain: Vec<RestoreChainLink>,
    members_verified: u64,
    member_bytes_verified: u64,
    expected_documents: u64,
    expected_family_records: u64,
    expected_deletes: u64,
    expected_disclosures: u64,
    decoded_documents: u64,
    decoded_family_records: u64,
    decoded_deletes: u64,
    decoded_disclosures: u64,
    restored_tenants: Vec<RestoredTenant>,
    restored_documents: u64,
    restored_family_records: u64,
    duplicate_put_replays: u64,
    missing_key_deletes: u64,
    disclosed_records_restored: u64,
    restore_commits: u64,
    target_sequence: StorageSequence,
    target_root_identity: String,
    tenants_verified: u64,
    documents_read_back: u64,
    representative_values_matched: u64,
    logical_checksum: String,
    largest_tenant_archive_bytes: u64,
}

/// Durable identity binding a restore publication to its admitted operation.
#[derive(Clone, Debug, Eq, PartialEq, serde::Serialize, serde::Deserialize)]
pub struct RestoreIdentity {
    /// Durable caller operation identity.
    pub operation_id: String,
    /// Authenticated source database incarnation.
    pub source_database_id: String,
    /// Authenticated paused target database incarnation.
    pub target_database_id: String,
    /// Independently selected provider identity.
    pub provider: String,
    /// Canonical point or timestamp selection.
    pub selection: String,
    /// Authenticated SHA-256 identity of the selected recovery manifest.
    pub expected_manifest_digest: String,
    /// Authenticated journal boundary of the selected recovery manifest.
    pub expected_journal_boundary: u64,
    /// Accepted authority epoch for this restore operation.
    pub restore_epoch: u64,
}

#[derive(Clone, Debug, Eq, PartialEq, serde::Serialize, serde::Deserialize)]
/// Authenticated facts recovered from a published restore marker.
pub struct RestoreMarkerFacts {
    /// The complete durable identity written by the restore publisher.
    pub identity: RestoreIdentity,
    /// Target sequence at publication.
    pub target_sequence: u64,
    /// Logical checksum at publication.
    pub logical_checksum: String,
    /// Restored document count.
    pub restored_documents: u64,
    /// Restored family record count.
    pub restored_family_records: u64,
    /// Logical checksum authenticated by the selected source manifest.
    pub source_logical_checksum: String,
    /// Logical document count authenticated by the selected source manifest.
    pub source_logical_documents: u64,
    /// Capture clock authenticated by the selected source manifest.
    pub source_captured_unix_millis: u64,
    /// Retention deadline authenticated by the selected source manifest.
    pub source_retain_until_unix_millis: u64,
}

#[derive(Clone, Debug, Eq, PartialEq, serde::Serialize, serde::Deserialize)]
struct RestoreMarker {
    identity: RestoreIdentity,
    target_sequence: u64,
    logical_checksum: String,
    restored_documents: u64,
    restored_family_records: u64,
    source_logical_checksum: String,
    source_logical_documents: u64,
    source_captured_unix_millis: u64,
    source_retain_until_unix_millis: u64,
}

/// Reads the encrypted restore marker from an already published target.
///
/// `None` means the target has no marker for this operation. A malformed
/// marker is reported as a checksum failure so callers cannot treat it as a
/// successful replay.
/// # Errors
/// Returns a backup error when the engine scope, marker bytes, or persisted
/// restore metadata cannot be read or validated.
pub fn read_restore_marker(
    engine: &Engine,
    identity: &RestoreIdentity,
) -> Result<Option<RestoreMarkerFacts>, BackupError> {
    let tenant = TenantId::try_new(engine.database_owner().tenant().to_text().as_str())
        .map_err(|_| BackupError::DestinationUnavailable)?;
    let key = format!("restore/{}", identity.operation_id);
    let guard = engine.storage.lock().map_err(|_| BackupError::DestinationUnavailable)?;
    let snapshot = guard.port.published_state().0;
    let Some(value) = guard
        .port
        .private_metadata_at(&tenant, &key, snapshot)
        .map_err(|()| BackupError::DestinationUnavailable)?
    else {
        return Ok(None);
    };
    let crate::types::DocumentValue::String(value) = value else {
        return Err(BackupError::ChecksumMismatch);
    };
    let marker: RestoreMarker =
        serde_json::from_str(&value).map_err(|_| BackupError::ChecksumMismatch)?;
    if marker.identity != *identity {
        return Err(BackupError::ChecksumMismatch);
    }
    if marker.target_sequence == 0
        || marker.target_sequence > snapshot.get()
        || marker.source_logical_checksum.len() != 64
        || !marker.source_logical_checksum.bytes().all(|byte| byte.is_ascii_hexdigit())
    {
        return Err(BackupError::ChecksumMismatch);
    }
    Ok(Some(RestoreMarkerFacts {
        identity: marker.identity,
        target_sequence: marker.target_sequence,
        logical_checksum: marker.logical_checksum,
        restored_documents: marker.restored_documents,
        restored_family_records: marker.restored_family_records,
        source_logical_checksum: marker.source_logical_checksum,
        source_logical_documents: marker.source_logical_documents,
        source_captured_unix_millis: marker.source_captured_unix_millis,
        source_retain_until_unix_millis: marker.source_retain_until_unix_millis,
    }))
}

impl RecoveryReceipt {
    /// Returns the requested inclusive durable commit-clock cutoff, when restoring by time.
    #[must_use]
    pub fn requested_unix_millis(&self) -> Option<u64> {
        self.requested_unix_millis
    }

    /// Returns the last original source transaction included in the restored prefix.
    #[must_use]
    pub fn restored_source_sequence(&self) -> StorageSequence {
        self.restored_source_sequence
    }

    /// Returns the storage format label of the restored target location.
    #[must_use]
    pub fn storage_format(&self) -> &str {
        self.storage_format
    }

    /// Returns the link facts of the selected recovery point.
    #[must_use]
    pub fn selected(&self) -> &RestoreChainLink {
        &self.selected
    }

    /// Returns the selected recovery point's identity.
    #[must_use]
    pub fn selected_identity(&self) -> &str {
        self.selected.identity()
    }

    /// Returns the backup kind of the selected recovery point.
    #[must_use]
    pub fn selected_kind(&self) -> BackupKind {
        self.selected.kind()
    }

    /// Returns the storage-sequence boundary the selection covers.
    #[must_use]
    pub fn selected_boundary(&self) -> StorageSequence {
        self.selected.boundary_sequence()
    }

    /// Returns the complete restored chain, root first.
    #[must_use]
    pub fn chain(&self) -> &[RestoreChainLink] {
        &self.chain
    }

    /// Returns the chain identities root first.
    #[must_use]
    pub fn chain_identities(&self) -> Vec<&str> {
        self.chain.iter().map(RestoreChainLink::identity).collect()
    }

    /// Returns how many chain links the restore consumed.
    #[must_use]
    pub fn chain_depth(&self) -> usize {
        self.chain.len()
    }

    /// Returns how many archive members were re-digested before replay.
    #[must_use]
    pub fn members_verified(&self) -> u64 {
        self.members_verified
    }

    /// Returns the archive byte count the member digests re-covered.
    #[must_use]
    pub fn member_bytes_verified(&self) -> u64 {
        self.member_bytes_verified
    }

    /// Returns the manifest-bound user-document count the chain claims.
    #[must_use]
    pub fn expected_documents(&self) -> u64 {
        self.expected_documents
    }

    /// Returns the manifest-bound family-record count the chain claims.
    #[must_use]
    pub fn expected_family_records(&self) -> u64 {
        self.expected_family_records
    }

    /// Returns the manifest-bound journal-delete count the chain claims.
    #[must_use]
    pub fn expected_deletes(&self) -> u64 {
        self.expected_deletes
    }

    /// Returns the manifest-bound durability-disclosure count the chain
    /// claims (incremental links only, per the backup contract).
    #[must_use]
    pub fn expected_disclosures(&self) -> u64 {
        self.expected_disclosures
    }

    /// Returns the user-document count the replay decoded.
    #[must_use]
    pub fn decoded_documents(&self) -> u64 {
        self.decoded_documents
    }

    /// Returns the family-record count the replay decoded.
    #[must_use]
    pub fn decoded_family_records(&self) -> u64 {
        self.decoded_family_records
    }

    /// Returns the journal-delete count the replay decoded.
    #[must_use]
    pub fn decoded_deletes(&self) -> u64 {
        self.decoded_deletes
    }

    /// Returns the below-`Synced` frame count the replay decoded.
    #[must_use]
    pub fn decoded_disclosures(&self) -> u64 {
        self.decoded_disclosures
    }

    /// Returns the final live state's per-tenant counts, in canonical
    /// tenant order.
    #[must_use]
    pub fn restored_tenants(&self) -> &[RestoredTenant] {
        &self.restored_tenants
    }

    /// Returns the final live user-document count of the restore.
    #[must_use]
    pub fn restored_documents(&self) -> u64 {
        self.restored_documents
    }

    /// Returns the final live family-record count of the restore.
    #[must_use]
    pub fn restored_family_records(&self) -> u64 {
        self.restored_family_records
    }

    /// Returns how many put replays targeted an already-live key (last
    /// write wins, by publication order).
    #[must_use]
    pub fn duplicate_put_replays(&self) -> u64 {
        self.duplicate_put_replays
    }

    /// Returns how many delete replays targeted an absent key (no-op
    /// deletes, replayed and disclosed, never silently dropped).
    #[must_use]
    pub fn missing_key_deletes(&self) -> u64 {
        self.missing_key_deletes
    }

    /// Returns how many below-`Synced` records were restored as disclosed.
    #[must_use]
    pub fn disclosed_records_restored(&self) -> u64 {
        self.disclosed_records_restored
    }

    /// Returns how many publication commits the replay issued at `Synced`.
    #[must_use]
    pub fn restore_commits(&self) -> u64 {
        self.restore_commits
    }

    /// Returns the restored target's final storage sequence.
    #[must_use]
    pub fn target_sequence(&self) -> StorageSequence {
        self.target_sequence
    }

    /// Returns the restored target's durable root identity.
    #[must_use]
    pub fn target_root_identity(&self) -> &str {
        &self.target_root_identity
    }

    /// Returns how many tenants the verification read back from the
    /// reopened target.
    #[must_use]
    pub fn tenants_verified(&self) -> u64 {
        self.tenants_verified
    }

    /// Returns how many live documents the verification read back.
    #[must_use]
    pub fn documents_read_back(&self) -> u64 {
        self.documents_read_back
    }

    /// Returns the BLAKE3 checksum of all restored logical keys and values.
    #[must_use]
    pub fn logical_checksum(&self) -> &str {
        &self.logical_checksum
    }

    /// Returns how many representative values compared byte-exactly.
    #[must_use]
    pub fn representative_values_matched(&self) -> u64 {
        self.representative_values_matched
    }

    /// Returns the largest tenant's archive byte weight this run decoded:
    /// the sized form of the backup side's uncapped memory bound (BK-01
    /// repair F3) this receipt inherits and discloses.
    #[must_use]
    pub fn largest_tenant_archive_bytes(&self) -> u64 {
        self.largest_tenant_archive_bytes
    }

    /// Renders the receipt as deterministic, secret-safe text: identities,
    /// counts, checksum verdicts, duplicate and missing handling, the
    /// durability disclosures, the verification performed, and the limits
    /// that remain — never a path, a credential, or a document value.
    #[must_use]
    pub fn render(&self) -> String {
        let mut lines = Vec::new();
        lines.push("terrabase restore recovery receipt".to_owned());
        lines.push(format!("storage format: {}", self.storage_format));
        lines.push(format!(
            "selected recovery point: {} (kind {}, boundary {})",
            self.selected.identity,
            kind_text(self.selected.kind),
            self.selected.boundary.get()
        ));
        lines.push(format!("chain depth: {} (root first)", self.chain.len()));
        for (index, link) in self.chain.iter().enumerate() {
            lines.push(format!(
                "  link {}: {} kind={} boundary={}",
                index + 1,
                link.identity,
                kind_text(link.kind),
                link.boundary.get()
            ));
        }
        lines.push(format!(
            "checksum results: {} members re-digested, {} archive bytes verified",
            self.members_verified, self.member_bytes_verified
        ));
        lines.push(format!(
            "expected counts (manifest-bound): documents={} family_records={} deletes={} disclosures={}",
            self.expected_documents,
            self.expected_family_records,
            self.expected_deletes,
            self.expected_disclosures
        ));
        lines.push(format!(
            "decoded counts (replayed frames): documents={} family_records={} deletes={} disclosures={}",
            self.decoded_documents,
            self.decoded_family_records,
            self.decoded_deletes,
            self.decoded_disclosures
        ));
        lines.push(format!(
            "restored live state: documents={} family_records={} tenants={}",
            self.restored_documents,
            self.restored_family_records,
            self.restored_tenants.len()
        ));
        for tenant in &self.restored_tenants {
            lines.push(format!(
                "  tenant {}:{} documents={} family_records={}",
                tenant.tenant().as_str().len(),
                tenant.tenant(),
                tenant.documents(),
                tenant.family_records()
            ));
        }
        lines.push(format!(
            "duplicate handling: {} put replays onto already-live keys (last write wins, publication order)",
            self.duplicate_put_replays
        ));
        lines.push(format!(
            "missing handling: {} deletes replayed against absent keys (no-op deletes, kept and disclosed)",
            self.missing_key_deletes
        ));
        lines.push(format!(
            "durability disclosure: {} records below Synced restored as disclosed",
            self.disclosed_records_restored
        ));
        lines.push(format!(
            "publication: {} commits at Synced, target sequence {}, root {}",
            self.restore_commits,
            self.target_sequence.get(),
            self.target_root_identity
        ));
        lines.push(format!(
            "verification: target reopened durably; {} tenants, {} documents read back, {} representative values matched byte-exactly",
            self.tenants_verified, self.documents_read_back, self.representative_values_matched
        ));
        lines.push(format!(
            "logical checksum: {} (BLAKE3; every key and value verified)",
            self.logical_checksum
        ));
        lines.push(format!(
            "memory bound: restore streams members through a {}-byte window and replays in batches of at most {} writes; restore retains the restored live key set and one fixed-size content digest per key. the backup side's uncapped bound (BK-01 repair F3) applies to the source archive: the largest tenant's archive byte weight in this run was {} bytes",
            decode::READ_CHUNK_BYTES,
            MAX_BATCH_WRITES,
            self.largest_tenant_archive_bytes
        ));
        lines.push("limits:".to_owned());
        lines.push(
            "- document versions and storage sequences are re-minted by the target's own publication order; values, keys, counts, tenant scopes, and durability disclosures are the reproduced facts."
                .to_owned(),
        );
        lines.push(
            "- verification proves counts, key sets, and the representative value the chain leaves live per tenant through a fresh durable reopen; it is not a whole-value digest, and a tenant whose newest decoded document the chain deleted retains no representative value for that run."
                .to_owned(),
        );
        lines.push(
            "- an interrupted or failed restore leaves no target directory, only a stale staging sibling no later run reuses; restore never merges into an existing engine location."
                .to_owned(),
        );
        let mut text = lines.join("\n");
        text.push('\n');
        text
    }
}

/// Renders one backup kind's label for the receipt text.
fn kind_text(kind: BackupKind) -> &'static str {
    match kind {
        BackupKind::Full => "full",
        BackupKind::Snapshot => "snapshot",
        BackupKind::Incremental { .. } => "incremental",
    }
}

// ---------------------------------------------------------------------------
// Public entries on the crate-root restore plan type
// ---------------------------------------------------------------------------

impl RestorePlan {
    /// Restores the latest encrypted chain into an explicitly owned, encrypted target.
    ///
    /// # Errors
    /// Rejects missing target owner/keys, invalid archives, or unavailable storage.
    pub fn restore_latest_encrypted_with_config(
        destination: &Path,
        target: &EngineConfig,
        cipher: &dyn BackupReader,
        identity: &RestoreIdentity,
    ) -> Result<RecoveryReceipt, BackupError> {
        let chain = resolve_encrypted_chain(destination, cipher, &Selection::Latest)?;
        execute_configured_restore(destination, target, cipher, &chain, None, identity)
    }

    /// Restores a named encrypted chain into an explicitly owned, encrypted target.
    ///
    /// # Errors
    /// Rejects invalid identities, missing target owner/keys, or invalid archives.
    pub fn restore_recovery_point_encrypted_with_config(
        point_identity: &str,
        destination: &Path,
        target: &EngineConfig,
        cipher: &dyn BackupReader,
        identity: &RestoreIdentity,
    ) -> Result<RecoveryReceipt, BackupError> {
        crate::backup::manifest::validate_identity(point_identity)?;
        let chain = resolve_encrypted_chain(
            destination,
            cipher,
            &Selection::Point { identity: point_identity.to_owned() },
        )?;
        execute_configured_restore(destination, target, cipher, &chain, None, identity)
    }

    /// Restores through an inclusive timestamp into an owned, encrypted target.
    /// The caller must enforce placement before invoking this materialization seam.
    ///
    /// # Errors
    /// Rejects uncovered timestamps, missing target owner/keys, or invalid archives.
    pub fn restore_at_unix_millis_encrypted_with_config(
        requested_unix_millis: u64,
        destination: &Path,
        target: &EngineConfig,
        cipher: &dyn BackupReader,
        identity: &RestoreIdentity,
    ) -> Result<RecoveryReceipt, BackupError> {
        let chain = resolve_timestamp_chain(destination, cipher, requested_unix_millis)?;
        execute_configured_restore(
            destination,
            target,
            cipher,
            &chain,
            Some(requested_unix_millis),
            identity,
        )
    }

    /// Restores the destination's newest recovery point, consuming the
    /// complete ordered chain behind it (PB-6016).
    ///
    /// Selection is the same deterministic head choice
    /// [`crate::backup::verify_latest_chain`] makes, and the full chain —
    /// every link's bound member — is authenticated during replay before the
    /// target is published. See the restore module's documentation for the
    /// execution, fail-closed, and memory-bound contracts.
    ///
    /// # Errors
    ///
    /// Returns the typed restore failure: [`BackupError::ChainIncomplete`]
    /// for an empty destination or a missing link or member,
    /// [`BackupError::ManifestInvalid`] for a manifest that does not
    /// validate, [`BackupError::ChecksumMismatch`] for a member that fails
    /// its digest or a replay that does not reproduce its manifest's bound
    /// facts, [`BackupError::TargetNotEmpty`] for a non-empty or unrelated
    /// target, and [`BackupError::DestinationUnavailable`] when the target
    /// location cannot be built, written, or published.
    pub fn restore_latest(
        destination: &Path,
        target: &Path,
    ) -> Result<RecoveryReceipt, BackupError> {
        execute_restore(destination, target, &Selection::Latest)
    }

    /// Restores the newest point when the caller has already resolved the
    /// immutable placement lock for `target_region`.
    ///
    /// The member/orchestrator integration supplies the durable source
    /// placement and must call this checked entry point for PITR and branch
    /// creation.  This native seam verifies the requested target Region
    /// before admitting the target directory.
    ///
    /// # Errors
    ///
    /// Returns [`BackupError::PlacementMismatch`] when `target_region` is not
    /// admitted by `placement`, and the same typed failures as
    /// [`RestorePlan::restore_latest`] afterwards.
    pub fn restore_latest_in_region(
        destination: &Path,
        target: &Path,
        placement: &PlacementLock,
        target_region: RegionCode,
    ) -> Result<RecoveryReceipt, BackupError> {
        if !placement.allows(target_region) {
            return Err(BackupError::PlacementMismatch);
        }
        execute_restore(destination, target, &Selection::Latest)
    }

    /// Restores the newest encrypted recovery-point chain using the caller's
    /// authenticated backup cipher. Plaintext and encrypted point namespaces
    /// are intentionally not interchangeable.
    ///
    /// # Errors
    ///
    /// Returns the same validation, chain, target, and destination failures
    /// as [`Self::restore_latest`], plus [`BackupError::ChecksumMismatch`] when
    /// an encrypted member fails authentication.
    pub fn restore_latest_encrypted(
        destination: &Path,
        target: &Path,
        cipher: &dyn BackupReader,
    ) -> Result<RecoveryReceipt, BackupError> {
        execute_restore_encrypted(destination, target, &Selection::Latest, cipher)
    }

    /// Encrypted counterpart of [`Self::restore_latest_in_region`].
    ///
    /// # Errors
    ///
    /// Returns [`BackupError::PlacementMismatch`] when `target_region` is not
    /// admitted by `placement`, and the same typed failures as
    /// [`Self::restore_latest_encrypted`] afterwards.
    pub fn restore_latest_encrypted_in_region(
        destination: &Path,
        target: &Path,
        cipher: &dyn BackupReader,
        placement: &PlacementLock,
        target_region: RegionCode,
    ) -> Result<RecoveryReceipt, BackupError> {
        if !placement.allows(target_region) {
            return Err(BackupError::PlacementMismatch);
        }
        execute_restore_encrypted(destination, target, &Selection::Latest, cipher)
    }

    /// Restores exactly the named sealed recovery point, consuming the
    /// chain down to and including it (PB-6015/PB-6016).
    ///
    /// Selecting an incremental link restores its complete chain; selecting
    /// a chain's root restores only the root's depth. Links sealed after
    /// the named point are never applied.
    ///
    /// # Errors
    ///
    /// Returns [`BackupError::ManifestInvalid`] for a malformed identity or
    /// a point whose manifest does not validate, and the same typed
    /// failures as [`RestorePlan::restore_latest`] afterwards.
    pub fn restore_recovery_point(
        identity: &str,
        destination: &Path,
        target: &Path,
    ) -> Result<RecoveryReceipt, BackupError> {
        crate::backup::manifest::validate_identity(identity)?;
        execute_restore(destination, target, &Selection::Point { identity: identity.to_owned() })
    }

    /// Restores a named point only into a Region admitted by the placement
    /// lock.  This is the PITR/branch integration hook for a selected chain
    /// depth.
    ///
    /// # Errors
    ///
    /// Returns [`BackupError::ManifestInvalid`] for a malformed identity,
    /// [`BackupError::PlacementMismatch`] when `target_region` is not admitted
    /// by `placement`, and the same typed failures as
    /// [`RestorePlan::restore_latest`] afterwards.
    pub fn restore_recovery_point_in_region(
        identity: &str,
        destination: &Path,
        target: &Path,
        placement: &PlacementLock,
        target_region: RegionCode,
    ) -> Result<RecoveryReceipt, BackupError> {
        crate::backup::manifest::validate_identity(identity)?;
        if !placement.allows(target_region) {
            return Err(BackupError::PlacementMismatch);
        }
        execute_restore(destination, target, &Selection::Point { identity: identity.to_owned() })
    }

    /// Restores one named encrypted recovery point and its predecessor chain.
    ///
    /// # Errors
    ///
    /// Returns the same failures as [`Self::restore_recovery_point`], plus
    /// [`BackupError::ChecksumMismatch`] when an encrypted member fails
    /// authentication.
    pub fn restore_recovery_point_encrypted(
        identity: &str,
        destination: &Path,
        target: &Path,
        cipher: &dyn BackupReader,
    ) -> Result<RecoveryReceipt, BackupError> {
        crate::backup::manifest::validate_identity(identity)?;
        execute_restore_encrypted(
            destination,
            target,
            &Selection::Point { identity: identity.to_owned() },
            cipher,
        )
    }

    /// Restores complete source transactions through an inclusive Unix-millisecond cutoff.
    /// The durable source clock is nondecreasing: transactions sharing a millisecond
    /// are included together. A retained full root at or before the cutoff and a
    /// complete chain reaching the cutoff are required. Every consumed archive
    /// member is authenticated, including records later than the cutoff.
    ///
    /// # Errors
    /// Returns `ChainIncomplete` outside the retained, remotely covered interval;
    /// placement, authentication, and replay failures leave no published target.
    pub fn restore_at_unix_millis_encrypted_in_region(
        requested_unix_millis: u64,
        destination: &Path,
        target: &Path,
        cipher: &dyn BackupReader,
        placement: &PlacementLock,
        target_region: RegionCode,
    ) -> Result<RecoveryReceipt, BackupError> {
        if !placement.allows(target_region) {
            return Err(BackupError::PlacementMismatch);
        }
        let chain = resolve_timestamp_chain(destination, cipher, requested_unix_millis)?;
        admit_target(target)?;
        let staging = StagingDir::create(target)?;
        let outcome = build_and_verify_encrypted(
            &staging,
            destination,
            &chain,
            cipher,
            Some(requested_unix_millis),
        )
        .and_then(|run| publish(&staging, target).map(|()| run));
        match outcome {
            Ok(run) => {
                let mut result = receipt(&chain, run);
                result.requested_unix_millis = Some(requested_unix_millis);
                Ok(result)
            }
            Err(error) => {
                staging.discard();
                Err(error)
            }
        }
    }

    /// Encrypted named-point counterpart of
    /// [`Self::restore_recovery_point_in_region`].
    ///
    /// # Errors
    ///
    /// Returns [`BackupError::ManifestInvalid`] for a malformed identity,
    /// [`BackupError::PlacementMismatch`] when `target_region` is not admitted
    /// by `placement`, and the same typed failures as
    /// [`Self::restore_recovery_point_encrypted`] afterwards.
    pub fn restore_recovery_point_encrypted_in_region(
        identity: &str,
        destination: &Path,
        target: &Path,
        cipher: &dyn BackupReader,
        placement: &PlacementLock,
        target_region: RegionCode,
    ) -> Result<RecoveryReceipt, BackupError> {
        crate::backup::manifest::validate_identity(identity)?;
        if !placement.allows(target_region) {
            return Err(BackupError::PlacementMismatch);
        }
        execute_restore_encrypted(
            destination,
            target,
            &Selection::Point { identity: identity.to_owned() },
            cipher,
        )
    }
}

// ---------------------------------------------------------------------------
// Execution
// ---------------------------------------------------------------------------

/// Runs one restore end to end: select and verify, admit the target, build
/// the complete engine in staging, verify it by a fresh durable reopen, and
/// publish by one atomic directory rename.
fn execute_restore(
    destination: &Path,
    target: &Path,
    selection: &Selection,
) -> Result<RecoveryReceipt, BackupError> {
    let chain = resolve_chain(destination, selection)?;
    admit_target(target)?;
    let staging = StagingDir::create(target)?;
    let outcome = build_and_verify(&staging, destination, &chain)
        .and_then(|run| publish(&staging, target).map(|()| run));
    match outcome {
        Ok(run) => Ok(receipt(&chain, run)),
        Err(error) => {
            staging.discard();
            Err(error)
        }
    }
}

/// Encrypted counterpart of [`execute_restore`]. Manifest and member bytes
/// are authenticated before any archive record is parsed or published.
fn execute_restore_encrypted(
    destination: &Path,
    target: &Path,
    selection: &Selection,
    cipher: &dyn BackupReader,
) -> Result<RecoveryReceipt, BackupError> {
    let chain = resolve_encrypted_chain(destination, cipher, selection)?;
    admit_target(target)?;
    let staging = StagingDir::create(target)?;
    let outcome = build_and_verify_encrypted(&staging, destination, &chain, cipher, None)
        .and_then(|run| publish(&staging, target).map(|()| run));
    match outcome {
        Ok(run) => Ok(receipt(&chain, run)),
        Err(error) => {
            staging.discard();
            Err(error)
        }
    }
}

fn execute_configured_restore(
    destination: &Path,
    target: &EngineConfig,
    cipher: &dyn BackupReader,
    chain: &[BackupManifest],
    cutoff: Option<u64>,
    identity: &RestoreIdentity,
) -> Result<RecoveryReceipt, BackupError> {
    let selected = chain.last().ok_or(BackupError::ChainIncomplete)?;
    if identity.expected_manifest_digest != selected.identity()
        || identity.expected_journal_boundary != selected.boundary_sequence().get()
        || identity.restore_epoch == 0
    {
        return Err(BackupError::ChecksumMismatch);
    }
    if !target.has_encrypted_owner() {
        return Err(BackupError::DestinationUnavailable);
    }
    let path = target.location();
    if target.owner().is_none_or(|owner| owner.database().to_text() != identity.target_database_id)
    {
        return Err(BackupError::DestinationUnavailable);
    }
    let replace_existing = path.exists()
        && std::fs::read_dir(path)
            .map_err(|_| BackupError::DestinationUnavailable)?
            .next()
            .is_some();
    if replace_existing {
        let existing =
            Engine::open(target.clone()).map_err(|_| BackupError::DestinationUnavailable)?;
        let tenant = TenantId::try_new(existing.database_owner().tenant().to_text().as_str())
            .map_err(|_| BackupError::DestinationUnavailable)?;
        let state = existing
            .lifecycle_state(&existing.scope_for(&tenant))
            .map_err(|_| BackupError::DestinationUnavailable)?;
        if !state.paused
            || state.access_revoked()
            || read_restore_marker(&existing, identity)?.is_some()
        {
            return Err(BackupError::TargetNotEmpty);
        }
    } else {
        admit_target(path)?;
        if path.exists() {
            std::fs::remove_dir(path).map_err(|_| BackupError::TargetNotEmpty)?;
        }
    }
    let staging = StagingDir::create(path)?;
    let config = target.clone().at_location(staging.path());
    let outcome = (|| {
        let engine =
            Engine::open(config.clone()).map_err(|_| BackupError::DestinationUnavailable)?;
        // Archives describe one database. Never combine foreign tenant scopes
        // into a target or inherit source ownership/authentication metadata.
        if chain.iter().any(|link| {
            link.tenants().iter().any(|tenant| {
                tenant.tenant().as_str() != engine.database_owner().tenant().to_text().as_str()
            })
        }) {
            return Err(BackupError::ManifestInvalid);
        }
        let mut replay = Replay::new();
        replay.cutoff = cutoff;
        replay.chain(&engine, destination, chain, Some(cipher))?;
        drop(engine);
        let reopened = Engine::open(config).map_err(|_| BackupError::DestinationUnavailable)?;
        let guard = reopened.storage.lock().map_err(|_| BackupError::DestinationUnavailable)?;
        let verification = verify_storage(guard.port.as_ref(), &replay)?;
        verify_manifest_logical_facts(chain, cutoff, &verification)?;
        drop(guard);
        let facts = replay.into_facts(verification);
        let selected = chain.last().ok_or(BackupError::ChainIncomplete)?;
        write_restore_metadata(&reopened, identity, &facts, selected)?;
        verify_restore_metadata(&reopened, identity, &facts, selected)?;
        if replace_existing {
            reopened
                .activate_origin_epoch(identity.restore_epoch)
                .map_err(|_| BackupError::DestinationUnavailable)?;
            let tenant = TenantId::try_new(reopened.database_owner().tenant().to_text().as_str())
                .map_err(|_| BackupError::DestinationUnavailable)?;
            reopened
                .lifecycle_pause(
                    &reopened.scope_for(&tenant),
                    &format!("restore-pause-{}", identity.operation_id),
                )
                .map_err(|_| BackupError::DestinationUnavailable)?;
        }
        drop(reopened);
        publish_with_identity(&staging, path, replace_existing)?;
        Ok(facts)
    })();
    match outcome {
        Ok(facts) => {
            if replace_existing {
                staging.discard();
            }
            let mut result = receipt(chain, facts);
            result.requested_unix_millis = cutoff;
            Ok(result)
        }
        Err(error) => {
            staging.discard();
            Err(error)
        }
    }
}

/// Refuses a target that is not a fresh, empty engine location.
///
/// # Errors
///
/// Returns [`BackupError::TargetNotEmpty`] when the target exists as a
/// file, or as a directory holding anything at all.
fn admit_target(target: &Path) -> Result<(), BackupError> {
    if !target.exists() {
        return Ok(());
    }
    if !target.is_dir() {
        return Err(BackupError::TargetNotEmpty);
    }
    let empty = target.read_dir().map_err(|_| BackupError::TargetNotEmpty)?.next().is_none();
    if empty { Ok(()) } else { Err(BackupError::TargetNotEmpty) }
}

/// One restore's staging directory: a sibling of the target, never under a
/// recovery-point name, removed when the run ends without publishing.
struct StagingDir {
    path: PathBuf,
}

impl StagingDir {
    /// Creates a fresh staging sibling of `target`.
    ///
    /// # Errors
    ///
    /// Returns [`BackupError::DestinationUnavailable`] when the target's
    /// parent cannot be created or the staging directory cannot be created.
    fn create(target: &Path) -> Result<Self, BackupError> {
        static COUNTER: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(0);
        let index = COUNTER.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
        let parent = target.parent().unwrap_or_else(|| Path::new("."));
        std::fs::create_dir_all(parent).map_err(|_| BackupError::DestinationUnavailable)?;
        let path = parent.join(format!(".restore-staging-{}-{index}", std::process::id()));
        std::fs::create_dir(&path).map_err(|_| BackupError::DestinationUnavailable)?;
        Ok(Self { path })
    }

    /// Returns the staging engine location.
    fn path(&self) -> &Path {
        &self.path
    }

    /// Removes the staging tree: nothing was published from it.
    fn discard(&self) {
        let _ = std::fs::remove_dir_all(&self.path);
    }
}

/// Publishes the staged engine location as the target by one atomic
/// directory rename, then syncs the parent directory.
///
/// # Errors
///
/// Returns [`BackupError::DestinationUnavailable`] when the rename fails;
/// the caller discards the staging tree on that path.
fn publish(staging: &StagingDir, target: &Path) -> Result<(), BackupError> {
    if rustix::fs::renameat_with(
        rustix::fs::CWD,
        staging.path(),
        rustix::fs::CWD,
        target,
        rustix::fs::RenameFlags::NOREPLACE,
    )
    .is_err()
    {
        return Err(BackupError::DestinationUnavailable);
    }
    if let Some(parent) = target.parent() {
        super::sync_directory(parent)?;
    }
    Ok(())
}

fn write_restore_metadata(
    engine: &Engine,
    identity: &RestoreIdentity,
    facts: &RunFacts,
    selected: &BackupManifest,
) -> Result<(), BackupError> {
    let tenant = TenantId::try_new(engine.database_owner().tenant().to_text().as_str())
        .map_err(|_| BackupError::DestinationUnavailable)?;
    let operation = OperationId::try_new(&format!("restore-marker-{}", identity.operation_id))
        .map_err(|_| BackupError::DestinationUnavailable)?;
    let value = serde_json::to_string(&RestoreMarker {
        identity: identity.clone(),
        target_sequence: facts.target_sequence.get(),
        logical_checksum: facts.logical_checksum.clone(),
        restored_documents: facts.restored_documents,
        restored_family_records: facts.restored_family_records,
        source_logical_checksum: selected.logical_checksum().to_owned(),
        source_logical_documents: selected.logical_documents(),
        source_captured_unix_millis: selected.captured_unix_millis(),
        source_retain_until_unix_millis: selected.retain_until_unix_millis(),
    })
    .map_err(|_| BackupError::DestinationUnavailable)?;
    let mut guard = engine.storage.lock().map_err(|_| BackupError::DestinationUnavailable)?;
    let intent = CommitIntent {
        budget_allocation: None,
        tenant: tenant.clone(),
        digest: write_set_digest(&tenant, &[]),
        snapshot_sequence: guard.port.published_state().0,
        durability: LocalDurability::Synced,
        usage_admission: None,
        internal_maintenance: true,
        shard_copy: false,
        expected_origin_epoch: None,
        expected_uniform_context: None,
        merge_conflict: None,
        replication_fingerprint: None,
        replication_publication: None,
        hlc: 0,
        metadata: vec![PrivateMetadataWrite {
            key: format!("restore/{}", identity.operation_id),
            value: Some(crate::types::DocumentValue::String(value)),
        }],
        schema_envelopes: Vec::new(),
        writes: Vec::new(),
        operation,
    };
    match guard.port.commit(intent) {
        CommitResult::Committed(_) => Ok(()),
        CommitResult::Rejected(_) | CommitResult::Unknown(_) => {
            Err(BackupError::DestinationUnavailable)
        }
    }
}

fn verify_restore_metadata(
    engine: &Engine,
    identity: &RestoreIdentity,
    facts: &RunFacts,
    selected: &BackupManifest,
) -> Result<(), BackupError> {
    let tenant = TenantId::try_new(engine.database_owner().tenant().to_text().as_str())
        .map_err(|_| BackupError::DestinationUnavailable)?;
    let key = format!("restore/{}", identity.operation_id);
    let guard = engine.storage.lock().map_err(|_| BackupError::DestinationUnavailable)?;
    let snapshot = guard.port.published_state().0;
    let value = guard
        .port
        .private_metadata_at(&tenant, &key, snapshot)
        .map_err(|()| BackupError::DestinationUnavailable)?
        .ok_or(BackupError::DestinationUnavailable)?;
    let crate::types::DocumentValue::String(value) = value else {
        return Err(BackupError::ChecksumMismatch);
    };
    let marker: RestoreMarker =
        serde_json::from_str(&value).map_err(|_| BackupError::ChecksumMismatch)?;
    if marker.identity != *identity
        || marker.target_sequence != facts.target_sequence.get()
        || marker.logical_checksum != facts.logical_checksum
        || marker.restored_documents != facts.restored_documents
        || marker.restored_family_records != facts.restored_family_records
        || marker.source_logical_checksum != selected.logical_checksum()
        || marker.source_logical_documents != selected.logical_documents()
        || marker.source_captured_unix_millis != selected.captured_unix_millis()
        || marker.source_retain_until_unix_millis != selected.retain_until_unix_millis()
    {
        return Err(BackupError::ChecksumMismatch);
    }
    Ok(())
}

fn publish_with_identity(
    staging: &StagingDir,
    target: &Path,
    replace_existing: bool,
) -> Result<(), BackupError> {
    if rustix::fs::renameat_with(
        rustix::fs::CWD,
        staging.path(),
        rustix::fs::CWD,
        target,
        if replace_existing {
            rustix::fs::RenameFlags::EXCHANGE
        } else {
            rustix::fs::RenameFlags::NOREPLACE
        },
    )
    .is_err()
    {
        return Err(BackupError::DestinationUnavailable);
    }
    if let Some(parent) = target.parent() {
        super::sync_directory(parent)?;
    }
    Ok(())
}

/// The replay's independently derived results.
struct RunFacts {
    applied_disclosures: u64,
    restored_source_sequence: StorageSequence,
    restored_tenants: Vec<RestoredTenant>,
    restored_documents: u64,
    restored_family_records: u64,
    decoded_documents: u64,
    decoded_family_records: u64,
    decoded_deletes: u64,
    decoded_disclosures: u64,
    duplicate_put_replays: u64,
    missing_key_deletes: u64,
    restore_commits: u64,
    largest_tenant_archive_bytes: u64,
    target_sequence: StorageSequence,
    target_root_identity: String,
    tenants_verified: u64,
    documents_read_back: u64,
    representative_values_matched: u64,
    logical_checksum: String,
}

/// Builds the complete engine in staging and verifies it by a fresh durable
/// reopen.
///
/// # Errors
///
/// Returns the typed replay, publication, or verification failure. On any
/// failure the staging tree carries no claim and the caller discards it.
fn build_and_verify(
    staging: &StagingDir,
    destination: &Path,
    chain: &[BackupManifest],
) -> Result<RunFacts, BackupError> {
    let engine = Engine::open(
        EngineConfig::durable(staging.path()).map_err(|_| BackupError::DestinationUnavailable)?,
    )
    .map_err(|_| BackupError::DestinationUnavailable)?;
    let mut replay = Replay::new();
    replay.chain(&engine, destination, chain, None)?;
    drop(engine);
    let verification = verify_staging(staging.path(), &replay)?;
    verify_manifest_logical_facts(chain, None, &verification)?;
    Ok(replay.into_facts(verification))
}

fn build_and_verify_encrypted(
    staging: &StagingDir,
    destination: &Path,
    chain: &[BackupManifest],
    cipher: &dyn BackupReader,
    cutoff: Option<u64>,
) -> Result<RunFacts, BackupError> {
    let engine = Engine::open(
        EngineConfig::durable(staging.path()).map_err(|_| BackupError::DestinationUnavailable)?,
    )
    .map_err(|_| BackupError::DestinationUnavailable)?;
    let mut replay = Replay::new();
    replay.cutoff = cutoff;
    replay.chain(&engine, destination, chain, Some(cipher))?;
    drop(engine);
    let verification = verify_staging(staging.path(), &replay)?;
    verify_manifest_logical_facts(chain, cutoff, &verification)?;
    Ok(replay.into_facts(verification))
}

/// Exact manifest boundaries bind independently captured logical source facts.
/// A timestamp inside a journal link has no whole-link logical state claim.
fn verify_manifest_logical_facts(
    chain: &[BackupManifest],
    cutoff: Option<u64>,
    verification: &VerificationFacts,
) -> Result<(), BackupError> {
    let selected = chain.last().ok_or(BackupError::ChainIncomplete)?;
    if cutoff.is_none_or(|cutoff| cutoff >= selected.boundary_unix_millis())
        && (selected.logical_checksum() != verification.logical_checksum
            || selected.logical_documents() != verification.documents_read_back)
    {
        return Err(BackupError::ChecksumMismatch);
    }
    Ok(())
}

/// The decoded-count tally of one chain link, cross-checked against the
/// link's manifest at the end of the link.
#[derive(Default)]
struct LinkCounts {
    documents: u64,
    family_records: u64,
    deletes: u64,
    disclosures: u64,
    /// Per-tenant document/family counts, used to prevent a forged member
    /// from moving records between otherwise valid tenant scopes while
    /// preserving the global totals.
    tenants: BTreeMap<TenantId, (u64, u64)>,
}

/// The streaming replay of one chain into the staging engine location.
struct Replay {
    applied_disclosures: u64,
    cutoff: Option<u64>,
    incremental: bool,
    commit_clocks: BTreeMap<TenantId, (u64, u64)>,
    link_base: (u64, u64),
    link_head: (u64, u64),
    restored_source_sequence: StorageSequence,
    presence: BTreeMap<TenantId, BTreeSet<(String, String)>>,
    content_hashes: BTreeMap<(TenantId, String, String), [u8; 32]>,
    batches: BTreeMap<TenantId, OpenBatch>,
    /// One retained sample document per tenant: the newest document record
    /// the chain decoded whose key is still live. A delete of the sample's
    /// key retires it (see [`Replay::retire_sample`]), so a retained sample
    /// is always one of the tenant's final live keys.
    samples: BTreeMap<TenantId, Document>,
    tenant_archive_bytes: BTreeMap<TenantId, u64>,
    operation_counter: u64,
    commits: u64,
    duplicate_put_replays: u64,
    missing_key_deletes: u64,
    decoded_documents: u64,
    decoded_family_records: u64,
    decoded_deletes: u64,
    decoded_disclosures: u64,
}

/// One open replay batch for a single tenant.
struct OpenBatch {
    writes: Vec<StagedWrite>,
    keys: BTreeSet<(String, String)>,
}

impl OpenBatch {
    /// Creates an empty open batch.
    fn empty() -> Self {
        Self { writes: Vec::new(), keys: BTreeSet::new() }
    }
}

impl Replay {
    /// Creates the replay state.
    fn new() -> Self {
        Self {
            applied_disclosures: 0,
            cutoff: None,
            incremental: false,
            commit_clocks: BTreeMap::new(),
            link_base: (0, 0),
            link_head: (0, 0),
            restored_source_sequence: StorageSequence::new(0),
            presence: BTreeMap::new(),
            content_hashes: BTreeMap::new(),
            batches: BTreeMap::new(),
            samples: BTreeMap::new(),
            tenant_archive_bytes: BTreeMap::new(),
            operation_counter: 0,
            commits: 0,
            duplicate_put_replays: 0,
            missing_key_deletes: 0,
            decoded_documents: 0,
            decoded_family_records: 0,
            decoded_deletes: 0,
            decoded_disclosures: 0,
        }
    }

    /// Replays every link of the chain, root first, cross-checking each
    /// link's decoded counts against its manifest's bound facts.
    ///
    /// # Errors
    ///
    /// Returns the decode, publication, or count-verification failure; the
    /// first divergence aborts the chain with no success-shaped result.
    fn chain(
        &mut self,
        engine: &Engine,
        destination: &Path,
        chain: &[BackupManifest],
        cipher: Option<&dyn BackupReader>,
    ) -> Result<(), BackupError> {
        for link in chain {
            self.incremental = link.base_sequence().is_some();
            self.link_base = self.link_head;
            self.link_head = (link.boundary_sequence().get(), link.boundary_unix_millis());
            if !self.incremental {
                self.restored_source_sequence = link.boundary_sequence();
            }
            let expected_tenants: BTreeMap<_, _> = link
                .tenants()
                .iter()
                .map(|tenant| {
                    (tenant.tenant().clone(), (tenant.documents(), tenant.family_records()))
                })
                .collect();
            // A manifest binds the complete selected tenant scope, including
            // tenants that contribute no document or family frame to this
            // link. Seed those keys at zero so an empty incremental verifies;
            // the membership check below still rejects every archive record
            // from a tenant the manifest did not declare.
            let mut counts = LinkCounts {
                tenants: expected_tenants.keys().cloned().map(|tenant| (tenant, (0, 0))).collect(),
                ..LinkCounts::default()
            };
            for member in link.members() {
                // Manifest validation admits only canonical member names,
                // so the joined path stays inside the point's directory.
                let path = destination.join(point_directory(link.identity())).join(member.name());
                let mut file = open_member(&path, member.length())?;
                if let Some(cipher) = cipher {
                    // The manifest binds the ciphertext length and digest;
                    // only after those checks does the cipher authenticate
                    // and expose the bounded plaintext member.
                    if member.length() > (crate::encryption::MAX_PLAINTEXT_BYTES as u64 + 4096) {
                        return Err(BackupError::ChecksumMismatch);
                    }
                    let capacity = usize::try_from(member.length())
                        .map_err(|_| BackupError::ChecksumMismatch)?;
                    let mut envelope = Vec::with_capacity(capacity);
                    file.read_to_end(&mut envelope).map_err(|_| BackupError::ChainIncomplete)?;
                    if !member.matches_bytes(&envelope) {
                        return Err(BackupError::ChecksumMismatch);
                    }
                    let plaintext = cipher
                        .open_backup_envelope(&envelope)
                        .map_err(|_| BackupError::ChecksumMismatch)?;
                    decode_member_bytes(plaintext, &mut |record, frame_bytes| {
                        let tenant = match record {
                            Record::Document { tenant, .. } | Record::Delete { tenant, .. } => {
                                tenant
                            }
                        };
                        if !link.tenants().iter().any(|bound| bound.tenant() == tenant) {
                            return Err(BackupError::ChecksumMismatch);
                        }
                        self.apply(engine, record, frame_bytes, &mut counts)
                    })?;
                } else {
                    decode_member(
                        file,
                        member.length(),
                        member.digest(),
                        &mut |record, frame_bytes| {
                            let tenant = match record {
                                Record::Document { tenant, .. } | Record::Delete { tenant, .. } => {
                                    tenant
                                }
                            };
                            if !link.tenants().iter().any(|bound| bound.tenant() == tenant) {
                                return Err(BackupError::ChecksumMismatch);
                            }
                            self.apply(engine, record, frame_bytes, &mut counts)
                        },
                    )?;
                }
            }
            // The decoded frames must reproduce the manifest's bound facts
            // exactly; a divergence is a content failure, never a success.
            if counts.documents != link.documents()
                || counts.family_records != link.family_records()
                || counts.deletes != link.deletes()
            {
                return Err(BackupError::ChecksumMismatch);
            }
            if matches!(link.kind(), BackupKind::Incremental { .. })
                && counts.disclosures != link.disclosures()
            {
                return Err(BackupError::ChecksumMismatch);
            }
            if counts.tenants != expected_tenants {
                return Err(BackupError::ChecksumMismatch);
            }
            self.decoded_documents += counts.documents;
            self.decoded_family_records += counts.family_records;
            self.decoded_deletes += counts.deletes;
            self.decoded_disclosures += counts.disclosures;
        }
        self.flush_all(engine)?;
        Ok(())
    }

    /// Applies one decoded record: tallies, presence, batching, and
    /// publication.
    ///
    /// # Errors
    ///
    /// Returns the typed publication failure of the batch this record
    /// completed, or [`BackupError::ChecksumMismatch`] when a decoded key
    /// is not a valid identifier the target could publish.
    fn apply(
        &mut self,
        engine: &Engine,
        record: &Record,
        frame_bytes: u64,
        counts: &mut LinkCounts,
    ) -> Result<(), BackupError> {
        let (source_sequence, timestamp) = match record {
            Record::Document { sequence, committed_unix_millis, .. }
            | Record::Delete { sequence, committed_unix_millis, .. } => {
                (*sequence, *committed_unix_millis)
            }
        };
        if self.incremental {
            let tenant = match record {
                Record::Document { tenant, .. } | Record::Delete { tenant, .. } => tenant,
            };
            let previous = self.commit_clocks.entry(tenant.clone()).or_insert(self.link_base);
            if source_sequence <= self.link_base.0
                || source_sequence > self.link_head.0
                || timestamp < self.link_base.1
                || timestamp > self.link_head.1
                || source_sequence < previous.0
                || timestamp < previous.1
                || (source_sequence == previous.0 && timestamp != previous.1)
            {
                return Err(BackupError::ChecksumMismatch);
            }
            *previous = (source_sequence, timestamp);
        }
        let include = !self.incremental || self.cutoff.is_none_or(|cutoff| timestamp <= cutoff);
        if include {
            if record_durability(record) != LocalDurability::Synced {
                self.applied_disclosures += 1;
            }
            self.restored_source_sequence =
                StorageSequence::new(self.restored_source_sequence.get().max(source_sequence));
        }
        match record {
            Record::Document { tenant, .. } => {
                self.apply_document(engine, tenant, record, frame_bytes, counts, include)
            }
            Record::Delete { tenant, .. } => {
                self.apply_delete(engine, tenant, record, counts, include)
            }
        }
    }

    fn apply_document(
        &mut self,
        engine: &Engine,
        tenant: &TenantId,
        record: &Record,
        frame_bytes: u64,
        counts: &mut LinkCounts,
        include: bool,
    ) -> Result<(), BackupError> {
        let Record::Document { document, durability, .. } = record else {
            unreachable!("apply_document receives only document records");
        };
        let collection = document.collection().as_str().to_owned();
        let id = document.id().as_str().to_owned();
        if collection == RESERVED_FAMILY_COLLECTION {
            counts.family_records += 1;
            counts.tenants.entry(tenant.clone()).or_default().1 += 1;
        } else {
            counts.documents += 1;
            counts.tenants.entry(tenant.clone()).or_default().0 += 1;
        }
        if *durability != LocalDurability::Synced {
            counts.disclosures += 1;
        }
        if !include {
            return Ok(());
        }
        *self.tenant_archive_bytes.entry(tenant.clone()).or_insert(0) += frame_bytes;
        let keys = self.presence.entry(tenant.clone()).or_default();
        if !keys.insert((collection.clone(), id.clone())) {
            self.duplicate_put_replays += 1;
        }
        self.content_hashes.insert(
            (tenant.clone(), collection.clone(), id.clone()),
            crate::engine::logical_document_digest(tenant, document),
        );
        self.samples.insert(tenant.clone(), document.clone());
        self.stage(
            engine,
            tenant,
            StagedWrite::Put { document: document.clone(), expected: ExpectedVersion::Any },
            (collection, id),
        )
    }

    fn apply_delete(
        &mut self,
        engine: &Engine,
        tenant: &TenantId,
        record: &Record,
        counts: &mut LinkCounts,
        include: bool,
    ) -> Result<(), BackupError> {
        let Record::Delete { collection, id, .. } = record else {
            unreachable!("apply_delete receives only delete records");
        };
        counts.deletes += 1;
        counts.tenants.entry(tenant.clone()).or_default();
        if record_durability(record) != LocalDurability::Synced {
            counts.disclosures += 1;
        }
        if !include {
            return Ok(());
        }
        let keys = self.presence.entry(tenant.clone()).or_default();
        let removed_key = match collection {
            Some(name) => {
                let key = (name.as_str().to_owned(), id.clone());
                keys.remove(&key).then_some(key)
            }
            None => keys
                .iter()
                .find(|(_, document)| document == id)
                .map(|(collection, document)| (collection.clone(), document.clone()))
                .filter(|key| keys.remove(key)),
        };
        match removed_key {
            Some(removed_key) => {
                self.content_hashes.remove(&(
                    tenant.clone(),
                    removed_key.0.clone(),
                    removed_key.1.clone(),
                ));
                self.retire_sample(tenant, &removed_key);
            }
            None => self.missing_key_deletes += 1,
        }
        let staged_collection = match collection {
            Some(name) => {
                Some(CollectionName::try_new(name).map_err(|_| BackupError::ChecksumMismatch)?)
            }
            None => None,
        };
        let staged_id = DocumentId::try_new(id).map_err(|_| BackupError::ChecksumMismatch)?;
        let batch_key = match collection {
            Some(name) => (name.as_str().to_owned(), id.clone()),
            None => (String::new(), id.clone()),
        };
        self.stage(
            engine,
            tenant,
            StagedWrite::Delete {
                collection: staged_collection,
                id: staged_id,
                expected: ExpectedVersion::Any,
                ttl_guard: None,
            },
            batch_key,
        )
    }

    /// Stages one write into the tenant's open batch, flushing first when
    /// the batch already holds the key or has reached its write bound.
    fn stage(
        &mut self,
        engine: &Engine,
        tenant: &TenantId,
        write: StagedWrite,
        key: (String, String),
    ) -> Result<(), BackupError> {
        let flush_needed = self.batches.get(tenant).is_some_and(|batch| {
            batch.keys.contains(&key) || batch.writes.len() >= MAX_BATCH_WRITES
        });
        if flush_needed
            && let Some(batch) = self.batches.remove(tenant)
            && !batch.writes.is_empty()
        {
            self.flush(engine, tenant, batch)?;
        }
        let batch = self.batches.entry(tenant.clone()).or_insert_with(OpenBatch::empty);
        batch.keys.insert(key);
        batch.writes.push(write);
        Ok(())
    }

    /// Publishes one tenant's open batch as one `Synced` commit.
    ///
    /// # Errors
    ///
    /// Returns [`BackupError::ChecksumMismatch`] when the target refuses
    /// the write set — the replay uses permissive expectations and fresh
    /// deterministic identities, so a refusal means the archive's records
    /// are not publishable as bound — and
    /// [`BackupError::DestinationUnavailable`] when the publication outcome
    /// is unknown or the target's durable bytes cannot be written.
    fn flush(
        &mut self,
        engine: &Engine,
        tenant: &TenantId,
        batch: OpenBatch,
    ) -> Result<(), BackupError> {
        self.operation_counter += 1;
        let operation = OperationId::try_new(&format!("restore-{:06}", self.operation_counter))
            .map_err(|_| BackupError::DestinationUnavailable)?;
        let digest = write_set_digest(tenant, &batch.writes);
        let mut guard = engine.storage.lock().map_err(|_| BackupError::DestinationUnavailable)?;
        let snapshot_sequence = guard.port.published_state().0;
        let intent = CommitIntent {
            budget_allocation: None,
            tenant: tenant.clone(),
            digest,
            snapshot_sequence,
            durability: LocalDurability::Synced,
            // Restore replay is an internal mutation, not an origin request.
            // It must never mint admitted-request usage without authenticated
            // serving-boundary facts.
            usage_admission: None,
            internal_maintenance: true,
            shard_copy: false,
            expected_origin_epoch: None,
            expected_uniform_context: None,
            merge_conflict: None,
            replication_fingerprint: None,
            replication_publication: None,
            hlc: 0,
            metadata: Vec::new(),
            schema_envelopes: Vec::new(),
            writes: batch.writes,
            operation,
        };
        match guard.port.commit(intent) {
            CommitResult::Committed(_) => {
                self.commits += 1;
                Ok(())
            }
            CommitResult::Rejected(_) => Err(BackupError::ChecksumMismatch),
            CommitResult::Unknown(_) => Err(BackupError::DestinationUnavailable),
        }
    }

    /// Publishes every tenant's open batch.
    ///
    /// # Errors
    ///
    /// Returns the first batch's typed publication failure.
    fn flush_all(&mut self, engine: &Engine) -> Result<(), BackupError> {
        let tenants: Vec<TenantId> = self.batches.keys().cloned().collect();
        for tenant in tenants {
            if let Some(batch) = self.batches.remove(&tenant)
                && !batch.writes.is_empty()
            {
                self.flush(engine, &tenant, batch)?;
            }
        }
        Ok(())
    }

    /// Retires the tenant's retained sample when the removed key is the
    /// sample's own key, so verification only ever compares a value the
    /// chain's final state still holds.
    fn retire_sample(&mut self, tenant: &TenantId, removed_key: &(String, String)) {
        if self.samples.get(tenant).is_some_and(|sample| {
            sample.collection().as_str() == removed_key.0 && sample.id().as_str() == removed_key.1
        }) {
            self.samples.remove(tenant);
        }
    }

    /// Derives the final per-tenant live counts from the presence set.
    fn restored_tenants(&self) -> Vec<RestoredTenant> {
        self.presence
            .iter()
            .map(|(tenant, keys)| {
                let family = keys
                    .iter()
                    .filter(|(collection, _)| collection == RESERVED_FAMILY_COLLECTION)
                    .count() as u64;
                RestoredTenant::new(tenant.clone(), keys.len() as u64 - family, family)
            })
            .collect()
    }

    /// Finishes the run facts with the verification outcome.
    fn into_facts(self, verification: VerificationFacts) -> RunFacts {
        let restored_tenants = self.restored_tenants();
        let restored_documents = restored_tenants.iter().map(RestoredTenant::documents).sum();
        let restored_family_records =
            restored_tenants.iter().map(RestoredTenant::family_records).sum();
        RunFacts {
            applied_disclosures: self.applied_disclosures,
            restored_source_sequence: self.restored_source_sequence,
            restored_tenants,
            restored_documents,
            restored_family_records,
            decoded_documents: self.decoded_documents,
            decoded_family_records: self.decoded_family_records,
            decoded_deletes: self.decoded_deletes,
            decoded_disclosures: self.decoded_disclosures,
            duplicate_put_replays: self.duplicate_put_replays,
            missing_key_deletes: self.missing_key_deletes,
            restore_commits: self.commits,
            largest_tenant_archive_bytes: self
                .tenant_archive_bytes
                .values()
                .copied()
                .max()
                .unwrap_or(0),
            target_sequence: verification.sequence,
            target_root_identity: verification.root_identity,
            tenants_verified: verification.tenants_verified,
            documents_read_back: verification.documents_read_back,
            representative_values_matched: verification.representative_values_matched,
            logical_checksum: verification.logical_checksum,
        }
    }
}

/// The durability a decoded record's frame carried.
fn record_durability(record: &Record) -> LocalDurability {
    match record {
        Record::Document { durability, .. } | Record::Delete { durability, .. } => *durability,
    }
}

/// The verified facts of one fresh durable reopen of the staging location.
struct VerificationFacts {
    sequence: StorageSequence,
    root_identity: String,
    tenants_verified: u64,
    documents_read_back: u64,
    representative_values_matched: u64,
    logical_checksum: String,
}

/// Reopens durable storage and verifies every key and value against the
/// archive replay, retaining only one bounded document page at a time.
fn verify_staging(staging: &Path, replay: &Replay) -> Result<VerificationFacts, BackupError> {
    let storage = SqliteStorage::open(staging).map_err(|_| BackupError::DestinationUnavailable)?;
    verify_storage(&storage, replay)
}

fn verify_storage(
    storage: &dyn StoragePort,
    replay: &Replay,
) -> Result<VerificationFacts, BackupError> {
    let (sequence, root) = storage.published_state();
    let mut documents_read_back = 0;
    let mut digest = crate::engine::logical_state_hasher();
    for (tenant, expected_keys) in &replay.presence {
        let mut after: Option<(CollectionName, DocumentId)> = None;
        let mut observed = 0;
        loop {
            let page = storage
                .documents_at_from_key(
                    sequence,
                    tenant,
                    after.as_ref().map(|(collection, id)| (collection, id)),
                    READBACK_PAGE,
                )
                .map_err(|_| BackupError::ChecksumMismatch)?;
            if page.is_empty() {
                break;
            }
            for document in &page {
                let key = (
                    tenant.clone(),
                    document.collection().as_str().to_owned(),
                    document.id().as_str().to_owned(),
                );
                let actual = crate::engine::logical_document_digest(tenant, document);
                if replay.content_hashes.get(&key) != Some(&actual) {
                    return Err(BackupError::ChecksumMismatch);
                }
                digest.update(&actual);
                observed += 1;
            }
            let last = page.last().expect("nonempty page");
            after = Some((last.collection().clone(), last.id().clone()));
        }
        if observed != expected_keys.len() as u64 {
            return Err(BackupError::ChecksumMismatch);
        }
        documents_read_back += observed;
    }
    Ok(VerificationFacts {
        sequence,
        root_identity: root.to_string(),
        tenants_verified: replay.presence.len() as u64,
        documents_read_back,
        representative_values_matched: replay.samples.len() as u64,
        logical_checksum: digest.finalize().to_hex().to_string(),
    })
}

/// Builds the terminal receipt from the resolved chain and the run facts.
fn receipt(chain: &[BackupManifest], run: RunFacts) -> RecoveryReceipt {
    let links: Vec<RestoreChainLink> = chain.iter().map(RestoreChainLink::of).collect();
    let selected = links.last().cloned().expect("a resolved chain is never empty");
    RecoveryReceipt {
        requested_unix_millis: None,
        restored_source_sequence: run.restored_source_sequence,
        storage_format: STORAGE_FORMAT_LABEL,
        selected,
        members_verified: chain.iter().map(|link| link.members().len() as u64).sum(),
        member_bytes_verified: chain.iter().map(BackupManifest::total_bytes).sum(),
        expected_documents: chain.iter().map(BackupManifest::documents).sum(),
        expected_family_records: chain.iter().map(BackupManifest::family_records).sum(),
        expected_deletes: chain.iter().map(BackupManifest::deletes).sum(),
        expected_disclosures: chain.iter().map(BackupManifest::disclosures).sum(),
        decoded_documents: run.decoded_documents,
        decoded_family_records: run.decoded_family_records,
        decoded_deletes: run.decoded_deletes,
        decoded_disclosures: run.decoded_disclosures,
        restored_tenants: run.restored_tenants,
        restored_documents: run.restored_documents,
        restored_family_records: run.restored_family_records,
        duplicate_put_replays: run.duplicate_put_replays,
        missing_key_deletes: run.missing_key_deletes,
        disclosed_records_restored: run.applied_disclosures,
        restore_commits: run.restore_commits,
        target_sequence: run.target_sequence,
        target_root_identity: run.target_root_identity,
        tenants_verified: run.tenants_verified,
        documents_read_back: run.documents_read_back,
        representative_values_matched: run.representative_values_matched,
        logical_checksum: run.logical_checksum,
        largest_tenant_archive_bytes: run.largest_tenant_archive_bytes,
        chain: links,
    }
}

#[cfg(test)]
mod digest_tests {
    use super::*;
    use crate::types::DocumentValue;

    #[test]
    fn readback_rejects_corruption_outside_representative_value() {
        let path = std::env::temp_dir().join(format!(
            "terrabase-readback-digest-{}-{}",
            std::process::id(),
            std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_nanos(),
        ));
        let engine = Engine::open(EngineConfig::durable(&path).unwrap()).unwrap();
        let tenant = TenantId::try_new("digest-test").unwrap();
        let collection = CollectionName::try_new("items").unwrap();
        let mut replay = Replay::new();
        let mut counts = LinkCounts::default();
        for id in ["first", "sample"] {
            let document = Document::new(
                DocumentId::try_new(id).unwrap(),
                collection.clone(),
                DocumentValue::Integer(7),
            );
            replay
                .apply(
                    &engine,
                    &Record::Document {
                        tenant: tenant.clone(),
                        sequence: 1,
                        committed_unix_millis: 1,
                        document,
                        durability: LocalDurability::Synced,
                    },
                    1,
                    &mut counts,
                )
                .unwrap();
        }
        replay.flush_all(&engine).unwrap();
        drop(engine);
        let checksum = verify_staging(&path, &replay).unwrap().logical_checksum;
        assert_eq!(checksum.len(), 64);
        // Change a document other than the retained sample through publication.
        let engine = Engine::open(EngineConfig::durable(&path).unwrap()).unwrap();
        let mut batch = OpenBatch::empty();
        batch.writes.push(StagedWrite::Put {
            document: Document::new(
                DocumentId::try_new("first").unwrap(),
                collection,
                DocumentValue::Integer(8),
            ),
            expected: ExpectedVersion::Any,
        });
        replay.flush(&engine, &tenant, batch).unwrap();
        drop(engine);
        assert!(matches!(verify_staging(&path, &replay), Err(BackupError::ChecksumMismatch)));
        std::fs::remove_dir_all(path).unwrap();
    }
}
