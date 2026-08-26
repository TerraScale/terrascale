//! Smuggler export/import with data-family selection and transforms
//! (BK-03).
//!
//! The smuggler is the V1-parity export/import surface: an export reads the
//! durable publication history and seals a manifest-bound artifact of the
//! selected data families; an import verifies that artifact end to end and
//! writes through the Engine transaction seam under the caller's
//! backend-supplied tenant scope. Family selection is explicit
//! ([`ExportSelection`]), import transforms are typed — collection renames
//! only, never arbitrary code — and both directions bind manifest identity,
//! member digests, and per-family counts (PB-6018..PB-6021).
//!
//! # Artifact
//!
//! One export publishes `destination/sm-<identity>/` by a single atomic
//! directory rename — an artifact is either entirely absent or entirely
//! sealed, exactly like a backup recovery point. The directory holds a
//! `manifest.tbv2s` text manifest and ordered `member-NNNNNN` binaries of
//! logical record frames in the backup family's private record codec (the
//! same encoding backup archives use, since smuggler lives under
//! `src/backup`). The manifest binds the format version
//! (`terrabase-smuggler-export 1`), the selected families, per-family
//! record counts, per-tenant document and family-record counts, and every
//! member's ordered name, exact length, and SHA-256 digest. Its identity is
//! the SHA-256 of all manifest lines except the identity line itself.
//!
//! # Semantics
//!
//! - Export emits the live state at the proven high-water boundary — user
//!   documents and family records in canonical key order per tenant, in
//!   canonical tenant order — and, when tombstones are selected, the
//!   journal's delete records after them, in journal order. Because of
//!   that, an artifact without tombstones round-trips byte-identically
//!   (the same logical state seals the same identity), while tombstone
//!   history is preserved but not byte-identical across engines: a delete
//!   record carries its source journal's version facts.
//! - Import validates the manifest strictly, rejects a selection the
//!   artifact does not carry, verifies every digest and length, and only
//!   then writes. A malformed manifest is [`SmugglerError::ManifestInvalid`],
//!   a wrong digest or count is [`SmugglerError::ChecksumMismatch`], a
//!   truncated record is [`SmugglerError::RecordTruncated`], a corrupt one
//!   is [`SmugglerError::RecordCorrupt`], and an unknown format version is
//!   [`SmugglerError::UnsupportedVersion`] — all before or instead of any
//!   write, never a skipped item.
//! - Every record applies under a deterministic operation identity derived
//!   from the artifact identity and the record's ordinal, so a re-import
//!   is idempotent: already-applied records are resolved from the durable
//!   publication history as duplicates, and the engine's operation ledger
//!   replays an identical write set without a second write.
//! - Tenant isolation is engine-enforced from the backend-supplied scope:
//!   one import run serves exactly the scope's tenant. Another tenant's
//!   records are out of scope — never routed into the authorized tenant's
//!   data — and each run verifies its applied and duplicate counts against
//!   that tenant's own bound counts on the manifest's `tenant` lines.
//!
//! # Visibility
//!
//! This module is private to `src/backup`: its types are `pub(crate)`, and
//! the integrator exposes the seam when the backend drives it
//! (PD-2026-08-26-11 D2). The focused tests live in-crate for that reason.

// No production caller exists until the integrator exposes the seam to the
// backend (PD-2026-08-26-11 D2); the focused tests are today's consumers,
// matching the declaration-only root this module grew from.
#![allow(dead_code)]

mod codec;
#[cfg(test)]
mod tests;

use std::collections::{BTreeMap, BTreeSet};
use std::path::{Path, PathBuf};
use std::sync::Arc;

use super::records::{MemberWriter, Record, SealedMember};
use codec::{identity_of_body, parse_manifest, render_body, with_identity};

use crate::engine::{CommitOutcome, Engine};
use crate::storage::PublishedCommit;
use crate::types::{
    Cancellation, CollectionName, Document, DocumentId, ExpectedVersion, LocalDurability,
    OperationId, StorageSequence, TenantId, TenantScope,
};

/// The smuggler manifest file name inside one artifact directory.
const MANIFEST_FILE: &str = "manifest.tbv2s";
/// The manifest header; the supported format version follows it.
const HEADER: &str = "terrabase-smuggler-export";
/// The only supported artifact format version.
const SUPPORTED_FORMAT_VERSION: u32 = 1;
/// The page size for publication-history reads.
const JOURNAL_PAGE_LIMIT: u32 = 1024;
/// The default byte bound at which one member is sealed.
const DEFAULT_MEMBER_BOUND_BYTES: u64 = 8 * 1024 * 1024;
/// The reserved family collection, spelled as the backup core spells it.
const RESERVED_FAMILY_COLLECTION: &str = "terrabase.families";

/// One selectable data family of a smuggler export (PB-6018).
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub(crate) enum ExportFamily {
    /// User documents outside the reserved family collection.
    Documents,
    /// Family records inside the reserved family collection.
    FamilyRecords,
    /// Journal delete records (tombstones).
    Tombstones,
}

impl ExportFamily {
    /// Returns the manifest token of this family.
    pub(crate) fn token(self) -> &'static str {
        match self {
            Self::Documents => "Documents",
            Self::FamilyRecords => "FamilyRecords",
            Self::Tombstones => "Tombstones",
        }
    }

    /// Parses a manifest token.
    pub(crate) fn of_token(token: &str) -> Option<Self> {
        match token {
            "Documents" => Some(Self::Documents),
            "FamilyRecords" => Some(Self::FamilyRecords),
            "Tombstones" => Some(Self::Tombstones),
            _ => None,
        }
    }
}

/// An explicit export or import family selection.
#[derive(Clone, Debug, Eq, PartialEq)]
pub(crate) struct ExportSelection {
    families: BTreeSet<ExportFamily>,
}

impl ExportSelection {
    /// Selects every family.
    pub(crate) fn all() -> Self {
        Self {
            families: BTreeSet::from([
                ExportFamily::Documents,
                ExportFamily::FamilyRecords,
                ExportFamily::Tombstones,
            ]),
        }
    }

    /// Selects exactly the given families; an empty selection exports
    /// nothing and is rejected by the run.
    pub(crate) fn of(families: impl IntoIterator<Item = ExportFamily>) -> Self {
        Self { families: families.into_iter().collect() }
    }

    /// Returns whether one family is selected.
    pub(crate) fn contains(&self, family: ExportFamily) -> bool {
        self.families.contains(&family)
    }

    /// Returns the selected families in canonical order.
    pub(crate) fn families(&self) -> impl Iterator<Item = ExportFamily> + '_ {
        self.families.iter().copied()
    }

    /// Returns whether the selection is empty.
    pub(crate) fn is_empty(&self) -> bool {
        self.families.is_empty()
    }

    /// Returns the manifest rendering of the selection.
    pub(crate) fn tokens(&self) -> String {
        self.families.iter().map(|family| family.token()).collect::<Vec<_>>().join(",")
    }
}

/// A validated smuggler export request: family selection, tenant
/// selection, and the member byte bound.
#[derive(Clone)]
pub(crate) struct SmugglerExportPlan {
    selection: ExportSelection,
    tenants: Option<Vec<TenantId>>,
    member_bound_bytes: u64,
}

impl SmugglerExportPlan {
    /// Plans an export of exactly the selected families.
    pub(crate) fn for_selection(selection: ExportSelection) -> Self {
        Self { selection, tenants: None, member_bound_bytes: DEFAULT_MEMBER_BOUND_BYTES }
    }

    /// Limits the export to the selected tenants' scopes.
    pub(crate) fn with_tenants(mut self, tenants: impl IntoIterator<Item = TenantId>) -> Self {
        self.tenants = Some(tenants.into_iter().collect());
        self
    }

    /// Bounds each member at `bytes` (records are never split).
    pub(crate) fn with_member_bound_bytes(mut self, bytes: u64) -> Self {
        self.member_bound_bytes = bytes;
        self
    }
}

/// One sealed smuggler artifact's facts: identity, selection, per-family
/// counts, and member bindings.
#[derive(Clone, Debug)]
pub(crate) struct SmugglerExportResult {
    identity: String,
    selection: ExportSelection,
    counts: BTreeMap<ExportFamily, u64>,
    members: Vec<SealedMember>,
}

impl SmugglerExportResult {
    /// Returns the artifact's content-derived identity.
    pub(crate) fn identity(&self) -> &str {
        &self.identity
    }

    /// Returns the selection the artifact carries.
    pub(crate) fn selection(&self) -> &ExportSelection {
        &self.selection
    }

    /// Returns the record count of one family.
    pub(crate) fn count(&self, family: ExportFamily) -> u64 {
        self.counts.get(&family).copied().unwrap_or(0)
    }

    /// Returns the ordered member bindings.
    pub(crate) fn members(&self) -> &[SealedMember] {
        &self.members
    }

    /// Re-digests every bound member in `destination`.
    ///
    /// # Errors
    ///
    /// Returns [`SmugglerError::ManifestInvalid`] when a member is missing
    /// and [`SmugglerError::ChecksumMismatch`] when a length or digest does
    /// not verify.
    pub(crate) fn verify(&self, destination: &Path) -> Result<(), SmugglerError> {
        let artifact = destination.join(format!("sm-{}", self.identity));
        for member in &self.members {
            let path = artifact.join(member.name());
            let metadata = std::fs::metadata(&path).map_err(|_| SmugglerError::ManifestInvalid)?;
            if metadata.len() != member.length() {
                return Err(SmugglerError::ChecksumMismatch);
            }
            let digest =
                super::sha256::digest_file(&path).map_err(|_| SmugglerError::ManifestInvalid)?;
            if digest != member.digest() {
                return Err(SmugglerError::ChecksumMismatch);
            }
        }
        Ok(())
    }
}

/// The typed import transforms: collection renames only.
///
/// V1 smuggler transform scripts are decision D-5's named partial row and
/// stay out of scope: no arbitrary code executes at this seam. A rename
/// names an old and a new collection; the new name must be a valid V2
/// collection and must not target the reserved family collection, or the
/// import fails closed with [`SmugglerError::TransformInvalid`] before any
/// write.
#[derive(Clone, Debug, Default, Eq, PartialEq)]
pub(crate) struct SmugglerTransform {
    renames: BTreeMap<String, String>,
}

impl SmugglerTransform {
    /// No transforms.
    pub(crate) fn none() -> Self {
        Self::default()
    }

    /// Renames one collection during import.
    pub(crate) fn rename_collection(mut self, from: &str, to: &str) -> Self {
        self.renames.insert(from.to_owned(), to.to_owned());
        self
    }

    /// Returns the renamed collection, if this transform renames it.
    pub(crate) fn renamed(&self, collection: &str) -> Option<&str> {
        self.renames.get(collection).map(String::as_str)
    }

    /// Returns whether any rename is defined.
    pub(crate) fn is_empty(&self) -> bool {
        self.renames.is_empty()
    }
}

/// A validated smuggler import request: family selection, transforms,
/// cancellation, and a progress observer.
#[derive(Clone)]
pub(crate) struct SmugglerImportPlan {
    selection: ExportSelection,
    transform: SmugglerTransform,
    cancellation: Option<Arc<Cancellation>>,
    progress: Option<Arc<dyn Fn(u64) + Send + Sync>>,
}

impl SmugglerImportPlan {
    /// Plans an import of exactly the selected families.
    pub(crate) fn importing(selection: ExportSelection) -> Self {
        Self { selection, transform: SmugglerTransform::none(), cancellation: None, progress: None }
    }

    /// Applies the typed transforms to every imported user document.
    pub(crate) fn with_transform(mut self, transform: SmugglerTransform) -> Self {
        self.transform = transform;
        self
    }

    /// Arms a shareable cancellation signal, checked per record.
    pub(crate) fn with_cancellation(mut self, cancellation: Arc<Cancellation>) -> Self {
        self.cancellation = Some(cancellation);
        self
    }

    /// Observes the run's applied-record count after every durable
    /// application.
    pub(crate) fn with_progress_observer(
        mut self,
        progress: Arc<dyn Fn(u64) + Send + Sync>,
    ) -> Self {
        self.progress = Some(progress);
        self
    }
}

/// The terminal result of one smuggler import.
#[derive(Clone, Debug, Eq, PartialEq)]
pub(crate) struct SmugglerImportResult {
    identity: String,
    applied: BTreeMap<ExportFamily, u64>,
    duplicates: BTreeMap<ExportFamily, u64>,
    renames_applied: u64,
    verified: bool,
}

impl SmugglerImportResult {
    /// Returns the identity of the artifact this run consumed.
    pub(crate) fn identity(&self) -> &str {
        &self.identity
    }

    /// Returns the applied count of one family.
    pub(crate) fn applied(&self, family: ExportFamily) -> u64 {
        self.applied.get(&family).copied().unwrap_or(0)
    }

    /// Returns the duplicate count of one family.
    pub(crate) fn duplicates(&self, family: ExportFamily) -> u64 {
        self.duplicates.get(&family).copied().unwrap_or(0)
    }

    /// Returns how many applied records a transform rewrote.
    pub(crate) fn renames_applied(&self) -> u64 {
        self.renames_applied
    }

    /// Returns whether every bound member digest and every family count
    /// verified; success is never reported otherwise.
    pub(crate) fn verified(&self) -> bool {
        self.verified
    }
}

/// Typed smuggler failure categories.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum SmugglerError {
    /// The artifact manifest failed validation before any write: absent,
    /// malformed, unordered, an unknown family or member name, a selection
    /// the artifact does not carry, or a member that is missing.
    ManifestInvalid,
    /// A digest, length, or per-family count verification failed.
    ChecksumMismatch,
    /// A record's frame ends inside its member.
    RecordTruncated,
    /// A record is structurally corrupt: an unknown tag, a value that does
    /// not consume exactly its encoding, or an invalid identifier.
    RecordCorrupt,
    /// The artifact's format version is unsupported.
    UnsupportedVersion,
    /// A transform names an invalid target collection.
    TransformInvalid,
    /// A record's deterministic operation is undecided, so the run cannot
    /// resume safely.
    NotResumable,
    /// The target rejected a record — a commit rejection, a tenant of
    /// another scope, or a closed Engine.
    TargetIncompatible,
    /// The artifact destination could not be created, written, or sealed.
    DestinationUnavailable,
    /// The operation was cancelled; the durable prefix remains resumable.
    Cancelled,
}

// ---------------------------------------------------------------------------
// Export
// ---------------------------------------------------------------------------

/// Runs one smuggler export and seals its artifact into `destination`
/// (PB-6018, PB-6020).
///
/// # Errors
///
/// Returns [`SmugglerError::DestinationUnavailable`] when the destination
/// cannot be created, written, or sealed,
/// [`SmugglerError::TargetIncompatible`] when the publication history
/// cannot be read, and [`SmugglerError::ManifestInvalid`] when the
/// selection is empty.
pub(crate) fn run_smuggler_export(
    engine: &Engine,
    plan: &SmugglerExportPlan,
    destination: &Path,
) -> Result<SmugglerExportResult, SmugglerError> {
    if plan.selection.is_empty() {
        return Err(SmugglerError::ManifestInvalid);
    }
    std::fs::create_dir_all(destination).map_err(|_| SmugglerError::DestinationUnavailable)?;
    let staging = StagingDir::create(destination)?;
    let result = export_into(engine, plan, destination, &staging);
    staging.discard();
    result
}

/// Executes the export after the staging directory exists.
fn export_into(
    engine: &Engine,
    plan: &SmugglerExportPlan,
    destination: &Path,
    staging: &StagingDir,
) -> Result<SmugglerExportResult, SmugglerError> {
    let unavailable = |_| SmugglerError::TargetIncompatible;
    let boundary = engine.journaled_high_water().map_err(unavailable)?.unwrap_or_default();
    let mut tenants = engine.committed_tenants().map_err(unavailable)?;
    if let Some(selected) = &plan.tenants {
        tenants.retain(|tenant| selected.contains(tenant));
    }
    // Canonical tenant order makes the artifact bytes a pure function of
    // the logical state, which is what round-trip identity equality needs.
    tenants.sort();

    let mut writer = MemberWriter::new(staging.path().to_owned(), plan.member_bound_bytes);
    let mut counts: BTreeMap<ExportFamily, u64> = BTreeMap::new();
    let mut tenant_counts: BTreeMap<TenantId, (u64, u64, u64)> = BTreeMap::new();
    for tenant in &tenants {
        let live = live_state_at(engine, tenant, boundary)?;
        let entry = tenant_counts.entry(tenant.clone()).or_default();
        for (document, durability) in live.into_values() {
            let family = if document.collection().as_str() == RESERVED_FAMILY_COLLECTION {
                entry.1 += 1;
                ExportFamily::FamilyRecords
            } else {
                entry.0 += 1;
                ExportFamily::Documents
            };
            if plan.selection.contains(family) {
                *counts.entry(family).or_default() += 1;
                writer
                    .write(&Record::Document { tenant: tenant.clone(), document, durability })
                    .map_err(|_| SmugglerError::DestinationUnavailable)?;
            }
        }
        if plan.selection.contains(ExportFamily::Tombstones) {
            let deletes = journal_deletes(engine, tenant, boundary)?;
            let entry = tenant_counts.entry(tenant.clone()).or_default();
            entry.2 = deletes.len() as u64;
            for delete in deletes {
                *counts.entry(ExportFamily::Tombstones).or_default() += 1;
                writer.write(&delete).map_err(|_| SmugglerError::DestinationUnavailable)?;
            }
        }
    }
    let members = writer.finish().map_err(|_| SmugglerError::DestinationUnavailable)?;
    let body = render_body(
        SUPPORTED_FORMAT_VERSION,
        &plan.selection,
        &counts,
        &tenant_counts
            .into_iter()
            .map(|(tenant, (documents, family_records, tombstones))| {
                (tenant, documents, family_records, tombstones)
            })
            .collect::<Vec<_>>(),
        &members,
    );
    let identity = identity_of_body(&body);
    let rendered = with_identity(&body, &identity);
    seal_artifact(destination, staging, &identity, &rendered, &members)?;
    Ok(SmugglerExportResult { identity, selection: plan.selection.clone(), counts, members })
}

/// One tenant's replayed live state in canonical key order.
type LiveState = BTreeMap<(String, String), (Document, LocalDurability)>;

/// Replays one tenant's live state at `boundary` in canonical key order.
fn live_state_at(
    engine: &Engine,
    tenant: &TenantId,
    boundary: StorageSequence,
) -> Result<LiveState, SmugglerError> {
    let unavailable = |_| SmugglerError::TargetIncompatible;
    let mut live = BTreeMap::new();
    let mut after = StorageSequence::default();
    loop {
        let page = engine.tenant_commits(tenant, after, JOURNAL_PAGE_LIMIT).map_err(unavailable)?;
        if page.is_empty() {
            break;
        }
        let reached = page.last().is_none_or(|commit| commit.sequence().get() >= boundary.get());
        for commit in &page {
            replay_commit(&mut live, commit, boundary);
        }
        after = page.last().map_or(after, PublishedCommit::sequence);
        if reached {
            break;
        }
    }
    Ok(live)
}

/// Collects one tenant's journal delete records up to `boundary`, in
/// journal order.
fn journal_deletes(
    engine: &Engine,
    tenant: &TenantId,
    boundary: StorageSequence,
) -> Result<Vec<Record>, SmugglerError> {
    let unavailable = |_| SmugglerError::TargetIncompatible;
    let mut deletes = Vec::new();
    let mut after = StorageSequence::default();
    loop {
        let page = engine.tenant_commits(tenant, after, JOURNAL_PAGE_LIMIT).map_err(unavailable)?;
        if page.is_empty() {
            break;
        }
        let reached = page.last().is_none_or(|commit| commit.sequence().get() >= boundary.get());
        for commit in &page {
            if commit.sequence().get() > boundary.get() {
                continue;
            }
            for write in commit.writes() {
                if let crate::storage::PublishedWrite::Delete {
                    collection,
                    id,
                    version_at_delete,
                } = write
                {
                    deletes.push(Record::Delete {
                        tenant: commit.tenant().clone(),
                        collection: collection.as_ref().map(|name| name.as_str().to_owned()),
                        id: id.as_str().to_owned(),
                        version_at_delete: *version_at_delete,
                        durability: commit.durability(),
                    });
                }
            }
        }
        after = page.last().map_or(after, PublishedCommit::sequence);
        if reached {
            break;
        }
    }
    Ok(deletes)
}

/// Applies one journal commit's writes to the replayed live state.
fn replay_commit(
    live: &mut BTreeMap<(String, String), (Document, LocalDurability)>,
    commit: &PublishedCommit,
    boundary: StorageSequence,
) {
    if commit.sequence().get() > boundary.get() {
        return;
    }
    for write in commit.writes() {
        match write {
            crate::storage::PublishedWrite::Put { document } => {
                live.insert(
                    (document.collection().as_str().to_owned(), document.id().as_str().to_owned()),
                    (document.clone(), commit.durability()),
                );
            }
            crate::storage::PublishedWrite::Delete { collection, id, .. } => {
                if let Some(collection) = collection {
                    live.remove(&(collection.as_str().to_owned(), id.as_str().to_owned()));
                }
            }
        }
    }
}

/// Publishes one sealed artifact by a single atomic directory rename.
fn seal_artifact(
    destination: &Path,
    staging: &StagingDir,
    identity: &str,
    rendered: &str,
    members: &[SealedMember],
) -> Result<(), SmugglerError> {
    let artifact = destination.join(format!("sm-{identity}"));
    if artifact.join(MANIFEST_FILE).is_file() {
        // Idempotent retry: the identical artifact is already sealed.
        let existing = std::fs::read_to_string(artifact.join(MANIFEST_FILE))
            .map_err(|_| SmugglerError::DestinationUnavailable)?;
        if existing == rendered {
            return Ok(());
        }
        return Err(SmugglerError::ManifestInvalid);
    }
    if artifact.is_dir() {
        // An unsealed orphan from an interrupted run: publication is
        // directory-atomic, so nothing sealed ever lived there.
        std::fs::remove_dir_all(&artifact).map_err(|_| SmugglerError::DestinationUnavailable)?;
    }
    let candidate = staging.candidate();
    std::fs::create_dir(&candidate).map_err(|_| SmugglerError::DestinationUnavailable)?;
    for member in members {
        std::fs::rename(staging.path().join(member.name()), candidate.join(member.name()))
            .map_err(|_| SmugglerError::DestinationUnavailable)?;
    }
    let manifest_path = candidate.join(MANIFEST_FILE);
    std::fs::write(&manifest_path, rendered)
        .and_then(|()| {
            let file = std::fs::File::open(&manifest_path)?;
            file.sync_all()
        })
        .map_err(|_| SmugglerError::DestinationUnavailable)?;
    std::fs::rename(&candidate, &artifact).map_err(|_| SmugglerError::DestinationUnavailable)?;
    let _ = std::fs::File::open(destination).and_then(|directory| directory.sync_all());
    Ok(())
}

/// One staging directory for an export run, removed when the run ends.
struct StagingDir {
    path: PathBuf,
}

impl StagingDir {
    /// Creates a fresh staging directory inside the destination.
    ///
    /// # Errors
    ///
    /// Returns [`SmugglerError::DestinationUnavailable`] when the directory
    /// cannot be created.
    fn create(destination: &Path) -> Result<Self, SmugglerError> {
        static COUNTER: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(0);
        let index = COUNTER.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
        let path = destination.join(format!(".staging-smug-{}-{index}", std::process::id()));
        std::fs::create_dir(&path).map_err(|_| SmugglerError::DestinationUnavailable)?;
        Ok(Self { path })
    }

    /// Returns the staging directory path.
    fn path(&self) -> &Path {
        &self.path
    }

    /// Returns the in-staging candidate path for one artifact.
    fn candidate(&self) -> PathBuf {
        self.path.join("artifact")
    }

    /// Removes the staging tree.
    fn discard(self) {
        let _ = std::fs::remove_dir_all(&self.path);
    }
}

// ---------------------------------------------------------------------------
// Import
// ---------------------------------------------------------------------------

/// Runs one smuggler import of the artifact in `source` into `target`
/// under the backend-supplied `scope` (PB-6019, PB-6021).
///
/// # Errors
///
/// Returns the typed failure of the first fail-closed cause; verification
/// failures surface before any write, and no partial import is ever
/// reported as success.
pub(crate) fn run_smuggler_import(
    target: &Engine,
    scope: &TenantScope,
    plan: &SmugglerImportPlan,
    source: &Path,
) -> Result<SmugglerImportResult, SmugglerError> {
    let manifest = load_artifact(source)?;
    for family in plan.selection.families() {
        if !manifest.selection.contains(family) {
            return Err(SmugglerError::ManifestInvalid);
        }
    }
    validate_transforms(&plan.transform)?;
    manifest.verify(source)?;

    let prefix = format!("smug-{}-", manifest.identity.chars().take(12).collect::<String>());
    let already_applied = applied_ordinals(target, scope, &prefix)?;
    let mut run = ImportRun {
        identity: manifest.identity().to_owned(),
        plan,
        applied: BTreeMap::new(),
        duplicates: BTreeMap::new(),
        renames_applied: 0,
        ordinal: 0,
        applied_total: 0,
        already_applied,
    };
    for member in manifest.members() {
        let bytes = std::fs::read(source.join(member.name()))
            .map_err(|_| SmugglerError::ManifestInvalid)?;
        let mut offset = 0usize;
        while offset < bytes.len() {
            let (record, next) = codec::decode_record(&bytes, offset)?;
            run.record(target, scope, &record)?;
            offset += next;
        }
    }
    run.finish(&manifest, scope)
}

/// The mutable state of one executing import run.
struct ImportRun<'plan> {
    identity: String,
    plan: &'plan SmugglerImportPlan,
    applied: BTreeMap<ExportFamily, u64>,
    duplicates: BTreeMap<ExportFamily, u64>,
    renames_applied: u64,
    ordinal: u64,
    applied_total: u64,
    already_applied: BTreeSet<u64>,
}

impl ImportRun<'_> {
    fn check_cancellation(&self) -> Result<(), SmugglerError> {
        if self.plan.cancellation.as_ref().is_some_and(|token| token.is_cancelled()) {
            return Err(SmugglerError::Cancelled);
        }
        Ok(())
    }

    /// Applies one decoded record.
    fn record(
        &mut self,
        target: &Engine,
        scope: &TenantScope,
        record: &Record,
    ) -> Result<(), SmugglerError> {
        self.check_cancellation()?;
        let family = family_of(record);
        if !self.plan.selection.contains(family) {
            return Ok(());
        }
        let ordinal = self.ordinal;
        self.ordinal += 1;
        if tenant_of(record) != scope.tenant() {
            // Another tenant's record is out of this scope, not an error:
            // it is never routed into the authorized tenant's data, and the
            // run verifies against the scope tenant's own manifest counts.
            return Ok(());
        }
        let application = stage(record, &self.plan.transform)?;
        if self.already_applied.contains(&ordinal) {
            *self.duplicates.entry(family).or_default() += 1;
            return Ok(());
        }
        let renamed = application.renamed();
        let operation = deterministic_operation(&self.identity, ordinal);
        let outcome = apply_record(target, scope, operation, application);
        match outcome {
            Application::Applied => {
                *self.applied.entry(family).or_default() += 1;
                self.applied_total += 1;
                if renamed {
                    self.renames_applied += 1;
                }
                if let Some(observer) = &self.plan.progress {
                    observer(self.applied_total);
                }
                Ok(())
            }
            Application::Duplicate => {
                *self.duplicates.entry(family).or_default() += 1;
                Ok(())
            }
            Application::Failed(error) => Err(error),
        }
    }

    /// Verifies the scope tenant's counts against the manifest before
    /// success.
    fn finish(
        &mut self,
        manifest: &ArtifactManifest,
        scope: &TenantScope,
    ) -> Result<SmugglerImportResult, SmugglerError> {
        let (documents, family_records, tombstones) = manifest.tenant_counts(scope.tenant());
        let accounted = |family: ExportFamily| {
            self.applied.get(&family).copied().unwrap_or(0)
                + self.duplicates.get(&family).copied().unwrap_or(0)
        };
        if self.plan.selection.contains(ExportFamily::Documents)
            && accounted(ExportFamily::Documents) != documents
        {
            return Err(SmugglerError::ChecksumMismatch);
        }
        if self.plan.selection.contains(ExportFamily::FamilyRecords)
            && accounted(ExportFamily::FamilyRecords) != family_records
        {
            return Err(SmugglerError::ChecksumMismatch);
        }
        if self.plan.selection.contains(ExportFamily::Tombstones)
            && accounted(ExportFamily::Tombstones) != tombstones
        {
            return Err(SmugglerError::ChecksumMismatch);
        }
        Ok(SmugglerImportResult {
            identity: manifest.identity().to_owned(),
            applied: self.applied.clone(),
            duplicates: self.duplicates.clone(),
            renames_applied: self.renames_applied,
            verified: true,
        })
    }
}

/// The staged application of one decoded record.
enum StagedApplication {
    /// Put this document, noting whether a transform renamed its collection.
    Put { document: Document, renamed: bool },
    /// Delete this document identifier.
    Delete(DocumentId),
}

impl StagedApplication {
    /// Returns whether a transform rewrote this application.
    fn renamed(&self) -> bool {
        matches!(self, Self::Put { renamed: true, .. })
    }
}

/// The outcome of one staged application.
enum Application {
    Applied,
    Duplicate,
    Failed(SmugglerError),
}

/// Returns the family one decoded record belongs to.
fn family_of(record: &Record) -> ExportFamily {
    match record {
        Record::Document { document, .. } => {
            if document.collection().as_str() == RESERVED_FAMILY_COLLECTION {
                ExportFamily::FamilyRecords
            } else {
                ExportFamily::Documents
            }
        }
        Record::Delete { .. } => ExportFamily::Tombstones,
    }
}

/// Returns the tenant one decoded record belongs to.
fn tenant_of(record: &Record) -> &TenantId {
    match record {
        Record::Document { tenant, .. } | Record::Delete { tenant, .. } => tenant,
    }
}

/// Stages one decoded record of the scope's tenant, resolving its
/// transform.
fn stage(
    record: &Record,
    transform: &SmugglerTransform,
) -> Result<StagedApplication, SmugglerError> {
    match record {
        Record::Document { document, .. } => {
            let collection = document.collection();
            if collection.as_str() == RESERVED_FAMILY_COLLECTION {
                return Ok(StagedApplication::Put { document: document.clone(), renamed: false });
            }
            let (target_collection, renamed) = match transform.renamed(collection.as_str()) {
                Some(renamed) => (
                    CollectionName::try_new(renamed).map_err(|_| SmugglerError::RecordCorrupt)?,
                    true,
                ),
                None => (collection.clone(), false),
            };
            if target_collection.as_str() == RESERVED_FAMILY_COLLECTION {
                return Err(SmugglerError::TransformInvalid);
            }
            let document =
                Document::new(document.id().clone(), target_collection, document.value().clone());
            Ok(StagedApplication::Put { document, renamed })
        }
        Record::Delete { id, .. } => {
            let id = DocumentId::try_new(id).map_err(|_| SmugglerError::RecordCorrupt)?;
            Ok(StagedApplication::Delete(id))
        }
    }
}

/// Applies one staged record under its deterministic operation identity.
fn apply_record(
    target: &Engine,
    scope: &TenantScope,
    operation: OperationId,
    application: StagedApplication,
) -> Application {
    let Ok(snapshot) = target.read_snapshot(scope) else {
        return Application::Failed(SmugglerError::TargetIncompatible);
    };
    let Ok(mut transaction) = target.begin(scope, snapshot, operation.clone()) else {
        return Application::Failed(SmugglerError::TargetIncompatible);
    };
    let staged = match application {
        StagedApplication::Put { document, .. } => transaction
            .put(document, ExpectedVersion::Any)
            .map_err(|_| SmugglerError::TargetIncompatible),
        StagedApplication::Delete(id) => transaction
            .delete(id, ExpectedVersion::Any)
            .map_err(|_| SmugglerError::TargetIncompatible),
    };
    if let Err(error) = staged {
        return Application::Failed(error);
    }
    match transaction.commit(LocalDurability::Synced) {
        CommitOutcome::Committed(_) => Application::Applied,
        CommitOutcome::Rejected(_) => Application::Failed(SmugglerError::TargetIncompatible),
        CommitOutcome::Unknown(_) => match target.resolve_commit(scope, operation) {
            Ok(crate::engine::DurableResolution::Committed(_)) => Application::Applied,
            Ok(_) => Application::Failed(SmugglerError::NotResumable),
            Err(_) => Application::Failed(SmugglerError::TargetIncompatible),
        },
    }
}

/// Validates every transform rename before any write.
fn validate_transforms(transform: &SmugglerTransform) -> Result<(), SmugglerError> {
    for to in transform.renames.values() {
        if CollectionName::try_new(to).is_err() || to == RESERVED_FAMILY_COLLECTION {
            return Err(SmugglerError::TransformInvalid);
        }
    }
    Ok(())
}

/// Collects the record ordinals this artifact already durably applied.
fn applied_ordinals(
    target: &Engine,
    scope: &TenantScope,
    prefix: &str,
) -> Result<BTreeSet<u64>, SmugglerError> {
    let mut after = StorageSequence::default();
    let mut found = BTreeSet::new();
    loop {
        let page = target
            .tenant_commits(scope.tenant(), after, JOURNAL_PAGE_LIMIT)
            .map_err(|_| SmugglerError::TargetIncompatible)?;
        if page.is_empty() {
            break;
        }
        for commit in &page {
            if let Some(rest) = commit.operation_id().as_str().strip_prefix(prefix)
                && let Ok(ordinal) = rest.parse::<u64>()
            {
                found.insert(ordinal);
            }
        }
        after = page.last().map_or(after, PublishedCommit::sequence);
    }
    Ok(found)
}

/// Mints the deterministic operation identity of one record ordinal.
fn deterministic_operation(identity: &str, ordinal: u64) -> OperationId {
    let prefix: String = identity.chars().take(12).collect();
    OperationId::try_new(&format!("smug-{prefix}-{ordinal:09}"))
        .expect("the derived identity is a valid operation identifier")
}

/// One bound artifact member: ordered name, exact length, and digest.
#[derive(Clone, Debug, Eq, PartialEq)]
pub(crate) struct SmugglerMember {
    name: String,
    length: u64,
    digest: String,
}

impl SmugglerMember {
    /// Creates one member fact from validated parts.
    pub(super) fn new(name: String, length: u64, digest: String) -> Self {
        Self { name, length, digest }
    }

    /// Returns the member's canonical name.
    pub(crate) fn name(&self) -> &str {
        &self.name
    }

    /// Returns the member's exact byte length.
    pub(crate) fn length(&self) -> u64 {
        self.length
    }

    /// Returns the member's SHA-256 digest as lowercase hex.
    pub(crate) fn digest(&self) -> &str {
        &self.digest
    }
}

/// The loaded artifact manifest with its verification.
struct ArtifactManifest {
    identity: String,
    selection: ExportSelection,
    counts: BTreeMap<ExportFamily, u64>,
    tenants: BTreeMap<TenantId, (u64, u64, u64)>,
    members: Vec<SmugglerMember>,
}

impl ArtifactManifest {
    /// Returns the artifact identity.
    fn identity(&self) -> &str {
        &self.identity
    }

    /// Returns the record count of one family.
    fn count(&self, family: ExportFamily) -> u64 {
        self.counts.get(&family).copied().unwrap_or(0)
    }

    /// Returns the ordered member bindings.
    fn members(&self) -> &[SmugglerMember] {
        &self.members
    }

    /// Returns one tenant's bound document, family-record, and tombstone
    /// counts (all zero when the tenant is absent from the artifact).
    fn tenant_counts(&self, tenant: &TenantId) -> (u64, u64, u64) {
        self.tenants.get(tenant).copied().unwrap_or((0, 0, 0))
    }

    /// Verifies every bound member's length and digest.
    ///
    /// # Errors
    ///
    /// Returns [`SmugglerError::ManifestInvalid`] when a member is missing
    /// and [`SmugglerError::ChecksumMismatch`] when a length or digest does
    /// not verify.
    fn verify(&self, source: &Path) -> Result<(), SmugglerError> {
        for member in &self.members {
            let path = source.join(member.name());
            let metadata = std::fs::metadata(&path).map_err(|_| SmugglerError::ManifestInvalid)?;
            if metadata.len() != member.length() {
                return Err(SmugglerError::ChecksumMismatch);
            }
            let digest =
                super::sha256::digest_file(&path).map_err(|_| SmugglerError::ManifestInvalid)?;
            if digest != member.digest() {
                return Err(SmugglerError::ChecksumMismatch);
            }
        }
        Ok(())
    }
}

/// Loads and strictly validates one artifact's manifest.
fn load_artifact(source: &Path) -> Result<ArtifactManifest, SmugglerError> {
    let text = std::fs::read_to_string(source.join(MANIFEST_FILE))
        .map_err(|_| SmugglerError::ManifestInvalid)?;
    let manifest = parse_manifest(&text)?;
    Ok(manifest)
}
