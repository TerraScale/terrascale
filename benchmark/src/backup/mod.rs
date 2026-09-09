//! The backup core: full, incremental, and snapshot backups with
//! manifest-verified archives (BK-01).
//!
//! This module implements the backup vocabulary of the W4 design contract
//! (`docs/design/w4-transfer-replication-contract.md`, "Public type
//! ownership"). A backup run reads the durable publication history
//! (W4-CA-4), writes ordered archive members into a staging directory,
//! seals them under a manifest that binds ordered names, lengths, and
//! SHA-256 digests, and publishes the recovery point by a single atomic
//! directory rename: a recovery point is either entirely absent or entirely
//! sealed, never present without its manifest. Archive bytes never leave
//! this module family.
//!
//! # The three kinds
//!
//! A **full** backup archives the complete source state at the newest
//! boundary the durable publication history proves complete — the
//! journal's high-water mark. It is the root of a chain.
//!
//! An **incremental** backup names its base recovery point and archives
//! the journal range `(base, boundary]`: every reconstructable put and
//! delete that published after the base, in publication order, with the
//! base boundary covered by an existing recovery point in the same
//! destination. Chains append: [`verify_latest_chain`] walks the complete
//! ordered chain from the newest point to its root and rejects a missing,
//! reordered, truncated, or corrupt link before reporting success.
//!
//! A **snapshot** backup pins an explicit storage-sequence boundary chosen
//! by the caller and archives the complete source state as of exactly that
//! boundary. It is self-contained: it has no base link, verifies
//! standalone, and never depends on other recovery points. It exists for
//! point-in-time recovery points; unlike a full backup, the boundary is
//! the caller's pinned fact, not the run's high-water mark.
//!
//! Every production plan is constructed with a database-bound OCI Vault key
//! lease. Plaintext is available only to `cfg(test)` fixtures or the explicit
//! `test-support` feature; there is no production plaintext default.
//!
//! # Fail-closed and disclosure rules
//!
//! Every artifact is verifiable by manifest and checksum:
//! [`BackupArchive::verify`] and [`verify_latest_chain`] re-digest every
//! bound member, and a wrong length or digest is a typed
//! [`BackupError::ChecksumMismatch`]; a missing member or chain link is
//! [`BackupError::ChainIncomplete`]. Manifest validation rejects path
//! traversal, in-progress manifests, and identity mismatches before any
//! target is touched ([`BackupError::ManifestInvalid`]). Cancellation
//! leaves no usable partial recovery point, and so does a crash at any point
//! before the single publish rename: members and the sealed manifest are
//! assembled inside staging, so an interrupted run leaves no `rp-`
//! directory at all. An `rp-<identity>` directory that exists without a
//! sealed manifest is an unsealed orphan — never a recovery point — and
//! discovery ignores it while the identical retry replaces it.
//!
//! An incremental archives journal history, and journal entries carry the
//! local durability their commit achieved (W4-CA-4 repair F4): an entry
//! below `Synced` may describe bytes the source could have lost, so the
//! run keeps the record **and** records a disclosure — it is never
//! silently dropped and never presented as fully durable. The durability
//! fact itself is durable: every archived record frame carries its commit's
//! achieved local durability, so the member digests bind it and a restore or
//! audit reading the archive sees the same disclosure the run reported. The
//! manifest binds the *count* of disclosures (and, for a state archive, no
//! disclosure count at all — see below); the per-record facts are the
//! frames' and the run result's. A full or snapshot backup archives state,
//! not history, so its manifest carries no durability disclosures, though
//! each of its frames still carries the durability of the last write that
//! produced the archived document.
//!
//! # Memory bound of a state archive
//!
//! A full or snapshot run pages final-state records at the requested boundary
//! through the Engine's `snapshot_records_from` seam. Each page carries the
//! document and the durability of its winning write in stable full-key order,
//! so backup writes directly into the staged member writer without retaining
//! a tenant's complete live state or a second archive bundle. Incremental
//! runs likewise page the journal and write each record as it is classified.
//! The page bound is the Engine seam's hard maximum; the storage indexes and
//! one page of decoded records are the remaining working set.
//!
//! # Limits (focused tests only)
//!
//! This module is proved by focused tests through the public seam. It
//! makes no recovery, durability, RPO/RTO, or performance claim: formal
//! Recovery Receipts come exclusively from the exclusive recovery lane,
//! which belongs to BK-02. Restore selection and execution
//! ([`RestorePlan`], [`RecoveryReceipt`]) are available through the native
//! restore seam and are covered by round-trip tests. The source
//! read path is the publication history only: a source whose history is
//! absent or diverged from its published state fails closed or binds the
//! smaller provable boundary — it never silently invents coverage. Backup
//! admission allows one job at a time per source Engine; a concurrent
//! attempt is a typed error.

mod envelope;
mod manifest;
mod records;
mod restore;
pub use restore::RestoreIdentity;
#[cfg(test)]
mod encrypted_retry_tests;
mod sha256;
#[cfg(test)]
mod tests;
/// Bounded native backup scheduling shared by hosted databases.
pub mod worker;
pub use worker::FleetError;

pub use envelope::{BackupEnvelope, BackupEnvelopeParticipant};
pub use manifest::{BackupManifest, BackupMember, BackupTenantCount};
pub use restore::{
    RecoveryReceipt, RestoreChainLink, RestoreMarkerFacts, RestoredTenant, read_restore_marker,
    verify_latest_chain_encrypted,
};

use std::collections::BTreeMap;
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use manifest::{ENCRYPTED_MANIFEST_FILE, MANIFEST_FILE, ManifestFacts, point_directory};
pub mod object_store;
use records::{MemberWriter, Record};
use sha256::digest_file;

use crate::encryption::{
    ArtifactCipher, ArtifactKind, Compression, DatabaseKeyLease, DatabaseKeyVersion,
    EncryptionContext, EncryptionError, KeyPurpose, KeyScope,
};
use crate::engine::{Engine, MAX_SNAPSHOT_RECORD_PAGE};
use crate::identity::DatabaseUlid;
use crate::regions::{RegionCatalog, RegionCode};
use crate::storage::{PublishedCommit, PublishedWrite};
use crate::types::{Cancellation, LocalDurability, StorageSequence, TenantId};

/// Authenticates backup envelopes against an authorized database key scope.
/// A lease reader reconstructs each artifact context, allowing recovery after
/// restart without retaining an in-memory encryption session.
pub trait BackupReader {
    /// Returns authenticated plaintext, refusing expired or mismatched keys.
    ///
    /// # Errors
    /// Returns the encryption provider or envelope validation refusal.
    fn open_backup_envelope(&self, envelope: &[u8]) -> Result<Vec<u8>, EncryptionError>;
}

impl BackupReader for ArtifactCipher {
    fn open_backup_envelope(&self, envelope: &[u8]) -> Result<Vec<u8>, EncryptionError> {
        self.open(envelope)
    }
}

impl BackupReader for DatabaseKeyLease {
    fn open_backup_envelope(&self, envelope: &[u8]) -> Result<Vec<u8>, EncryptionError> {
        ArtifactCipher::open_envelope_from_database_key_lease(
            self,
            ArtifactKind::Backup,
            3,
            envelope,
        )
    }
}

/// An immutable database residency lock.
///
/// The lock is intentionally represented as the exact admitted Region set;
/// provider names and geography aliases never enter a backup artifact.  A
/// caller can narrow a request before construction, but there is no mutation
/// operation that can widen an existing lock.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct PlacementLock {
    regions: std::collections::BTreeSet<RegionCode>,
}

impl PlacementLock {
    /// Creates an explicit lock over active Regions.
    ///
    /// # Errors
    ///
    /// Returns [`BackupError::PlacementMismatch`] for an empty set or a
    /// Region that is not in the active production catalog.
    pub fn explicit(regions: impl IntoIterator<Item = RegionCode>) -> Result<Self, BackupError> {
        let regions: std::collections::BTreeSet<_> = regions.into_iter().collect();
        if regions.is_empty()
            || regions.iter().any(|region| !RegionCatalog::production().is_registered(region))
        {
            return Err(BackupError::PlacementMismatch);
        }
        Ok(Self { regions })
    }

    /// Creates the Brazil residency lock (`GRU`).
    #[must_use]
    pub fn br_only() -> Self {
        Self { regions: std::collections::BTreeSet::from([RegionCode::GRU]) }
    }

    /// Creates the European residency lock (`FRA`).
    #[must_use]
    pub fn eu_only() -> Self {
        Self { regions: std::collections::BTreeSet::from([RegionCode::FRA]) }
    }

    /// Returns whether a Region is admitted by this lock.
    #[must_use]
    pub fn allows(&self, region: RegionCode) -> bool {
        self.regions.contains(&region)
    }

    /// Returns the exact locked Region set in canonical order.
    #[must_use = "the locked Region set must be used for placement checks"]
    pub fn regions(&self) -> impl Iterator<Item = RegionCode> + '_ {
        self.regions.iter().copied()
    }

    /// Returns whether `candidate` is no wider than this lock.
    #[must_use]
    pub fn admits_lock(&self, candidate: &Self) -> bool {
        candidate.regions.is_subset(&self.regions)
    }
}

/// The reserved family collection, mirrored from the W3 family contract.
///
/// The canonical constant lives in the family-private state module and is
/// not re-exported; the query seam (`src/query/spec.rs`) and the streaming
/// seam (`src/streaming/sources.rs`) keep their own spelled copies of the
/// same name, and this module follows that convention. A document in this
/// collection is a family record: the manifest counts it separately, and a
/// restore re-puts it through the Engine transaction seam, which is the
/// sanctioned family write path.
const RESERVED_FAMILY_COLLECTION: &str = "terrabase.families";

/// The default byte bound at which one archive member is sealed and the
/// writer rotates to the next member (records are never split).
const DEFAULT_MEMBER_BOUND_BYTES: u64 = 8 * 1024 * 1024;

/// The page size for publication-history reads.
const JOURNAL_PAGE_LIMIT: u32 = 1024;

/// The number of disclosure facts one progress snapshot carries.
///
/// A run discloses one fact per below-`Synced` record and there is no bound
/// on how many a range holds, while a snapshot is emitted at every observed
/// phase. Snapshots therefore carry the newest window of disclosures plus the
/// exact running total; only the terminal [`BackupResult`] carries the
/// complete list.
const PROGRESS_DISCLOSURE_WINDOW: usize = 64;

/// The kind of one backup (PB-6010).
///
/// The kinds and their contracts are stated in the [module
/// documentation](self); `Snapshot` is the BK-01 disposition of the
/// snapshot mode the W4 contract left to a later decision.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum BackupKind {
    /// A full backup of the selected items at the run's proven boundary.
    Full,
    /// An incremental backup covering the journal-range boundary since its
    /// base recovery point.
    Incremental {
        /// The storage-sequence boundary of the base recovery point this
        /// incremental backup builds on.
        base: StorageSequence,
    },
    /// A snapshot backup of the selected items at a caller-pinned
    /// boundary; self-contained with no base link.
    Snapshot,
}

/// Derives the encryption artifact identity for one logical capture.
///
/// The storage boundary is the operation identity: rebuilding a capability
/// after an uncertain result for the same boundary must reuse the artifact
/// key, while a different boundary must receive a different key so ordinal
/// zero is never reused under one artifact key.
pub(crate) fn capture_artifact_id(
    database: DatabaseUlid,
    kind: BackupKind,
    boundary: StorageSequence,
) -> crate::identity::Ulid {
    let mut hasher = blake3::Hasher::new();
    hasher.update(b"terrabase-backup-artifact-v1\0");
    hasher.update(database.to_text().as_bytes());
    hasher.update(&[0]);
    match kind {
        BackupKind::Full => {
            hasher.update(b"full");
        }
        BackupKind::Snapshot => {
            hasher.update(b"snapshot");
        }
        BackupKind::Incremental { base } => {
            hasher.update(b"incremental");
            hasher.update(&base.get().to_be_bytes());
        }
    }
    hasher.update(&boundary.get().to_be_bytes());
    let mut bytes = *hasher.finalize().as_bytes();
    // ULID's first bit is reserved by its 128-bit textual encoding.
    bytes[0] &= 0x7f;
    crate::identity::Ulid::from_bytes(bytes[..16].try_into().expect("fixed digest prefix"))
}

/// A validated authorization to encrypt backup artifacts for one database.
///
/// Production composition must obtain this capability from an OCI Vault
/// [`DatabaseKeyLease`]. The lease's key scope is retained as non-secret
/// identity metadata and checked again against the opened Engine and its
/// placement Region before any bytes are read. Raw keys and an unbound
/// [`ArtifactCipher`] cannot be supplied to a [`BackupPlan`].
#[derive(Clone, Debug)]
pub struct BackupEncryption {
    cipher: Arc<Mutex<ArtifactCipher>>,
    scope: KeyScope,
    region: RegionCode,
    key_version: DatabaseKeyVersion,
}

/// Refusals while constructing a database-bound backup encryption capability.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum BackupEncryptionError {
    /// The Region is not an active Region in the production catalog.
    InvalidRegion,
    /// The lease does not belong to the backup key hierarchy.
    InvalidKeyPurpose,
    /// The encryption foundation rejected the lease or context.
    Encryption(EncryptionError),
}

impl std::fmt::Display for BackupEncryptionError {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::InvalidRegion => formatter.write_str("backup Region is not active"),
            Self::InvalidKeyPurpose => formatter.write_str("key lease is not for backups"),
            Self::Encryption(error) => error.fmt(formatter),
        }
    }
}

impl std::error::Error for BackupEncryptionError {}

impl BackupEncryption {
    /// Creates a production backup capability from an authorized OCI lease.
    ///
    /// The resulting cipher is bound to the lease's database,
    /// tenant, project, environment, key version, and the backup hierarchy.
    /// The Region is validated against the active production catalog and is
    /// checked against the Engine owner again by [`run_backup`].
    ///
    /// # Errors
    ///
    /// Returns a typed refusal for an inactive Region, a non-backup key
    /// scope, malformed metadata, an expired lease, or crypto failure.
    pub fn from_database_key_lease(
        lease: &DatabaseKeyLease,
        region: RegionCode,
        next_ordinal: u128,
    ) -> Result<Self, BackupEncryptionError> {
        Self::from_database_key_lease_with_artifact_id(
            lease,
            region,
            next_ordinal,
            crate::identity::generate(),
        )
    }

    /// Creates a production backup capability with a caller-supplied artifact
    /// identity. Capture callers must derive this from the immutable logical
    /// capture facts when they need an uncertain retry to reproduce the exact
    /// encrypted bytes (and therefore the same recovery-point identity).
    pub(crate) fn from_database_key_lease_with_artifact_id(
        lease: &DatabaseKeyLease,
        region: RegionCode,
        next_ordinal: u128,
        artifact_id: crate::identity::Ulid,
    ) -> Result<Self, BackupEncryptionError> {
        if !RegionCatalog::production().is_registered(&region) {
            return Err(BackupEncryptionError::InvalidRegion);
        }
        let metadata = lease.metadata();
        let scope = metadata.scope().clone();
        let key_version = metadata.version();
        if scope.purpose() != KeyPurpose::Backup {
            return Err(BackupEncryptionError::InvalidKeyPurpose);
        }
        let context = EncryptionContext::for_artifact(
            &scope,
            ArtifactKind::Backup,
            artifact_id,
            key_version,
            3,
            Compression::None,
        )
        .map_err(BackupEncryptionError::Encryption)?;
        let cipher = ArtifactCipher::from_database_key_lease(lease, context, next_ordinal)
            .map_err(BackupEncryptionError::Encryption)?;
        Ok(Self { cipher: Arc::new(Mutex::new(cipher)), scope, region, key_version })
    }

    /// Returns the database identity authorized by this capability.
    #[must_use]
    pub const fn database(&self) -> DatabaseUlid {
        self.scope.database()
    }

    /// Returns the placement Region authorized by this capability.
    #[must_use]
    pub const fn region(&self) -> RegionCode {
        self.region
    }

    /// Returns the database-key version authorized by this capability.
    #[must_use]
    pub const fn key_version(&self) -> DatabaseKeyVersion {
        self.key_version
    }

    fn validate_for_engine(&self, engine: &Engine) -> Result<(), BackupError> {
        let owner = engine.database_owner();
        let scope = &self.scope;
        if scope.database() != owner.database()
            || scope.tenant() != owner.tenant()
            || scope.project() != Some(owner.project())
            || scope.environment() != owner.environment().as_str()
            || self.region != owner.region()
        {
            return Err(BackupError::EncryptionScopeMismatch);
        }
        Ok(())
    }
}

/// A validated backup request: kind, boundary facts, item selection,
/// member bound, throttling bound, progress observation, and cancellation
/// (PB-6010, PB-6017).
///
/// The plan is immutable after construction; the `with_*` builders return
/// a new plan. `TenantScope` is never carried here: Platform constructs
/// scopes after authorization, and the engine-side run reads only the
/// durable publication history.
#[derive(Clone)]
pub struct BackupPlan {
    kind: BackupKind,
    pinned_boundary: Option<StorageSequence>,
    member_bound_bytes: u64,
    read_ops_per_second: Option<u32>,
    tenants: Option<Vec<TenantId>>,
    progress: Option<Arc<dyn Fn(BackupProgress) + Send + Sync>>,
    cancellation: Option<Arc<Cancellation>>,
    encryption: BackupEncryptionMode,
    placement: Option<PlacementLock>,
    reader: Option<Arc<dyn BackupReader + Send + Sync>>,
    archive_byte_limit: Option<u64>,
    capture_unix_millis: Option<u64>,
    retention_days: u8,
}

#[derive(Clone, Debug)]
enum BackupEncryptionMode {
    Encrypted(BackupEncryption),
    /// Plaintext for test fixtures only.
    #[cfg(any(test, feature = "test-support"))]
    Plaintext,
}

impl BackupPlan {
    /// Binds an immutable retention term to each sealed artifact.
    ///
    /// # Errors
    /// Returns `ManifestInvalid` unless the term is one of the plan limits:
    /// 1, 7, 30, or 35 days. Existing artifacts retain their original deadline.
    pub fn with_retention_days(mut self, days: u8) -> Result<Self, BackupError> {
        if !matches!(days, 1 | 7 | 30 | 35) {
            return Err(BackupError::ManifestInvalid);
        }
        self.retention_days = days;
        Ok(self)
    }

    /// Pins the capture clock for an idempotent retry of the same source boundary.
    /// Supply the original manifest capture time; a newer source commit than this
    /// clock fails closed. New scheduled captures should use the default clock.
    #[must_use]
    pub fn with_capture_unix_millis(mut self, captured_unix_millis: u64) -> Self {
        self.capture_unix_millis = Some(captured_unix_millis);
        self
    }

    /// Plans a full backup of every committed tenant at the run's proven
    /// boundary.
    #[must_use]
    pub fn full(encryption: BackupEncryption) -> Self {
        Self {
            kind: BackupKind::Full,
            pinned_boundary: None,
            member_bound_bytes: DEFAULT_MEMBER_BOUND_BYTES,
            read_ops_per_second: None,
            tenants: None,
            progress: None,
            cancellation: None,
            encryption: BackupEncryptionMode::Encrypted(encryption),
            placement: None,
            reader: None,
            archive_byte_limit: None,
            capture_unix_millis: None,
            retention_days: 1,
        }
    }

    /// Creates a plaintext full plan for the recovery integration fixture.
    #[cfg(any(test, feature = "test-support"))]
    #[doc(hidden)]
    #[must_use]
    pub fn for_integration_tests_full() -> Self {
        Self {
            kind: BackupKind::Full,
            pinned_boundary: None,
            member_bound_bytes: DEFAULT_MEMBER_BOUND_BYTES,
            read_ops_per_second: None,
            tenants: None,
            progress: None,
            cancellation: None,
            encryption: BackupEncryptionMode::Plaintext,
            placement: None,
            reader: None,
            archive_byte_limit: None,
            capture_unix_millis: None,
            retention_days: 1,
        }
    }

    /// Creates a plaintext full plan for in-crate unit tests only.
    #[cfg(test)]
    pub(crate) fn local_test_full_plaintext() -> Self {
        Self {
            kind: BackupKind::Full,
            pinned_boundary: None,
            member_bound_bytes: DEFAULT_MEMBER_BOUND_BYTES,
            read_ops_per_second: None,
            tenants: None,
            progress: None,
            cancellation: None,
            encryption: BackupEncryptionMode::Plaintext,
            placement: None,
            reader: None,
            archive_byte_limit: None,
            capture_unix_millis: None,
            retention_days: 1,
        }
    }

    /// Plans an incremental backup over the journal range since `base`.
    ///
    /// The destination must already hold the recovery point that covers
    /// `base`; otherwise the run fails closed with
    /// [`BackupError::ChainIncomplete`] rather than minting an unanchored
    /// link.
    #[must_use]
    pub fn incremental_since(base: StorageSequence, encryption: BackupEncryption) -> Self {
        Self { kind: BackupKind::Incremental { base }, ..Self::full(encryption) }
    }

    /// Creates a plaintext incremental plan for the recovery integration fixture.
    #[cfg(any(test, feature = "test-support"))]
    #[doc(hidden)]
    #[must_use]
    pub fn for_integration_tests_incremental_since(base: StorageSequence) -> Self {
        Self { kind: BackupKind::Incremental { base }, ..Self::for_integration_tests_full() }
    }

    /// Creates a plaintext incremental plan for in-crate unit tests only.
    #[cfg(test)]
    pub(crate) fn local_test_incremental_since(base: StorageSequence) -> Self {
        Self { kind: BackupKind::Incremental { base }, ..Self::local_test_full_plaintext() }
    }

    /// Caps staged archive bytes, conservatively accounting for encryption
    /// overhead before writing. Exhaustion preserves the existing chain.
    #[must_use]
    pub fn with_archive_byte_limit(mut self, bytes: u64) -> Self {
        self.archive_byte_limit = Some(bytes);
        self
    }

    /// Uses an authorized reader to validate older artifact contexts when
    /// extending a chain across process restarts or key rotations.
    #[must_use]
    pub fn with_reader(mut self, reader: Arc<dyn BackupReader + Send + Sync>) -> Self {
        self.reader = Some(reader);
        self
    }

    /// Plans a snapshot backup pinned at `boundary`.
    ///
    /// The boundary must be at or below the source's journal high-water
    /// mark; a boundary the source cannot prove complete fails closed.
    #[must_use]
    pub fn snapshot_at(boundary: StorageSequence, encryption: BackupEncryption) -> Self {
        Self {
            kind: BackupKind::Snapshot,
            pinned_boundary: Some(boundary),
            ..Self::full(encryption)
        }
    }

    /// Creates a plaintext snapshot plan for in-crate unit tests only.
    #[cfg(test)]
    pub(crate) fn local_test_snapshot_at(boundary: StorageSequence) -> Self {
        Self {
            kind: BackupKind::Snapshot,
            pinned_boundary: Some(boundary),
            ..Self::local_test_full_plaintext()
        }
    }

    /// Bounds each archive member at `bytes` (records are never split).
    #[must_use]
    pub fn with_member_bound_bytes(mut self, bytes: u64) -> Self {
        self.member_bound_bytes = bytes;
        self
    }

    /// Bounds source reads at `ops_per_second` reads. The bound delays or
    /// rejects reads without reordering or dropping records; a zero bound
    /// rejects the first read.
    #[must_use]
    pub fn with_read_ops_per_second(mut self, ops_per_second: u32) -> Self {
        self.read_ops_per_second = Some(ops_per_second);
        self
    }

    /// Limits the backup to the selected tenants' scopes. Unselected
    /// tenants' records never enter the archive.
    #[must_use]
    pub fn with_tenants(mut self, tenants: impl IntoIterator<Item = TenantId>) -> Self {
        self.tenants = Some(tenants.into_iter().collect());
        self
    }

    /// Observes the run's progress snapshots. The observer is called
    /// synchronously at real phases of the run; a green observation never
    /// hides loss.
    #[must_use]
    pub fn with_progress(mut self, progress: Arc<dyn Fn(BackupProgress) + Send + Sync>) -> Self {
        self.progress = Some(progress);
        self
    }

    /// Arms a shareable cancellation signal. `Cancellation` clones are
    /// independent signals by contract, so the plan carries one shared
    /// handle: cancelling through any handle of the same `Arc` cancels the
    /// run. A cancelled run leaves no usable partial recovery point.
    #[must_use]
    pub fn with_cancellation(mut self, cancellation: Arc<Cancellation>) -> Self {
        self.cancellation = Some(cancellation);
        self
    }

    /// Binds the run to an existing immutable residency lock.
    ///
    /// The lock is checked against the source owner and encryption wrapper
    /// before the first source read.  The lock itself has no widening
    /// operation; callers that need to change placement must create a new
    /// database and key hierarchy.
    #[must_use]
    pub fn with_placement_lock(mut self, placement: PlacementLock) -> Self {
        self.placement = Some(placement);
        self
    }
}

/// One phase of a running backup, as progress snapshots report it.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum BackupStep {
    /// The run was admitted; nothing has been read yet.
    Admitted,
    /// The durable publication history is being read and classified.
    ReadingJournal,
    /// Archive members are being written from the classified records.
    WritingArchive,
    /// The sealed members and manifest are being published as one
    /// recovery point.
    SealingManifest,
}

/// Why one archived record is disclosed rather than presented as fully
/// durable.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum BackupSkipReason {
    /// The record's commit achieved a local durability below `Synced`
    /// (`Buffered` or `Written`), so the source may have lost its bytes. The
    /// record is still archived, its frame carries that durability, and the
    /// manifest binds the count of such disclosures; the per-record fact is
    /// reported in the run's result.
    DurabilityBelowSynced,
}

/// One disclosure fact: an archived record whose durability is below the
/// durable bar, named without any document content.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct BackupSkip {
    tenant: TenantId,
    document: crate::types::DocumentId,
    reason: BackupSkipReason,
}

impl BackupSkip {
    /// Creates one disclosure fact from validated parts.
    pub(crate) fn new(
        tenant: TenantId,
        document: crate::types::DocumentId,
        reason: BackupSkipReason,
    ) -> Self {
        Self { tenant, document, reason }
    }

    /// Returns the tenant owning the disclosed record.
    #[must_use]
    pub fn tenant(&self) -> &TenantId {
        &self.tenant
    }

    /// Returns the disclosed record's document identifier.
    #[must_use]
    pub fn document(&self) -> &crate::types::DocumentId {
        &self.document
    }

    /// Returns the disclosure reason.
    #[must_use]
    pub fn reason(&self) -> BackupSkipReason {
        self.reason
    }
}

/// Non-secret progress facts for one running backup (PB-6021, PB-6035).
///
/// Carries the current step, the record counts read so far, the exact
/// disclosure total, and a bounded window of the newest disclosure facts; a
/// green status cannot hide loss. Snapshots never carry the whole disclosure
/// list — that is bounded to the newest window, because a snapshot is emitted
/// at every observed phase — while the terminal [`BackupResult`] does.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct BackupProgress {
    step: BackupStep,
    documents_read: u64,
    family_records_read: u64,
    deletes_read: u64,
    skipped_total: u64,
    skipped_items: Vec<BackupSkip>,
}

impl BackupProgress {
    /// Returns the run phase this snapshot was taken in.
    #[must_use]
    pub fn step(&self) -> BackupStep {
        self.step
    }

    /// Returns the user documents read so far.
    #[must_use]
    pub fn documents_read(&self) -> u64 {
        self.documents_read
    }

    /// Returns the family records read so far.
    #[must_use]
    pub fn family_records_read(&self) -> u64 {
        self.family_records_read
    }

    /// Returns the journal deletes read so far.
    #[must_use]
    pub fn deletes_read(&self) -> u64 {
        self.deletes_read
    }

    /// Returns the exact number of disclosed records so far. This total is
    /// never truncated by the snapshot window.
    #[must_use]
    pub fn skipped_total(&self) -> u64 {
        self.skipped_total
    }

    /// Returns the newest disclosure facts this snapshot carries, bounded to
    /// the progress window (at most `PROGRESS_DISCLOSURE_WINDOW` items). The
    /// exact count is [`BackupProgress::skipped_total`], and the complete
    /// list is carried by the run's terminal result.
    #[must_use]
    pub fn skipped_items(&self) -> &[BackupSkip] {
        &self.skipped_items
    }
}

/// One staged archive with its digest-bound member facts (PB-6010).
///
/// The archive is a fact set, not a handle: it carries the recovery-point
/// identity and the ordered member bindings so any destination holding
/// those bytes can be verified. Archive internals — the codec, member
/// internals, and staging management — stay private to this module.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct BackupArchive {
    identity: String,
    members: Vec<BackupMember>,
}

impl BackupArchive {
    /// Creates the archive fact set of one sealed run.
    pub(crate) fn new(identity: String, members: Vec<BackupMember>) -> Self {
        Self { identity, members }
    }

    /// Returns the recovery-point identity the archive belongs to.
    #[must_use]
    pub fn identity(&self) -> &str {
        &self.identity
    }

    /// Returns the ordered member bindings.
    #[must_use]
    pub fn members(&self) -> &[BackupMember] {
        &self.members
    }

    /// Verifies every bound member in `destination` against its recorded
    /// length and SHA-256 digest.
    ///
    /// A missing member file is [`BackupError::ChainIncomplete`]; a wrong
    /// length or digest is [`BackupError::ChecksumMismatch`]. Verification
    /// streams each member in bounded chunks and never holds a member in
    /// memory.
    ///
    /// # Errors
    ///
    /// Returns the typed verification failure for the first member that
    /// does not verify.
    pub fn verify(&self, destination: &Path) -> Result<(), BackupError> {
        verify_members(destination, &self.identity, &self.members)
    }
}

/// The terminal result of one backup run.
///
/// Carries the manifest and the archive facts; it carries no archive
/// content and no path. A formal `RecoveryReceipt` reference belongs to
/// the exclusive recovery lane (BK-02), not to focused runs.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct BackupResult {
    manifest: BackupManifest,
    archive: BackupArchive,
    skipped: Vec<BackupSkip>,
    source_reads: u64,
}

/// A restore request marker. Restore selection and execution are exposed as
/// associated functions so a request cannot accidentally carry a mutable
/// source or target handle across a crash boundary.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct RestorePlan {
    plan: std::marker::PhantomData<()>,
}

impl BackupResult {
    /// Creates the terminal result of one sealed run.
    pub(crate) fn new(
        manifest: BackupManifest,
        archive: BackupArchive,
        skipped: Vec<BackupSkip>,
        source_reads: u64,
    ) -> Self {
        Self { manifest, archive, skipped, source_reads }
    }

    /// Returns the manifest of the recovery point this run sealed.
    #[must_use]
    pub fn manifest(&self) -> &BackupManifest {
        &self.manifest
    }

    /// Returns the archive facts of this run.
    #[must_use]
    pub fn archive(&self) -> &BackupArchive {
        &self.archive
    }

    /// Returns the number of disclosed records in this run.
    #[must_use]
    pub fn skipped_total(&self) -> u64 {
        self.skipped.len() as u64
    }

    /// Returns the disclosure list of this run.
    #[must_use]
    pub fn skipped_items(&self) -> &[BackupSkip] {
        &self.skipped
    }

    /// Returns the number of source read operations this run spent.
    #[must_use]
    pub fn source_reads(&self) -> u64 {
        self.source_reads
    }
}

/// Typed backup failure categories.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum BackupError {
    /// The manifest failed validation before any target was created.
    ManifestInvalid,
    /// A digest or length verification failed.
    ChecksumMismatch,
    /// The incremental chain is missing, reordered, truncated, or otherwise
    /// incomplete.
    ChainIncomplete,
    /// The restore target is not a fresh, empty location.
    TargetNotEmpty,
    /// The backup source is unavailable, cannot serve the requested
    /// boundary, or no provable history exists for the requested range.
    SourceUnavailable,
    /// The throttle or admission bound rejected the operation: another
    /// backup holds this source's admission slot, or the read bound was
    /// exhausted.
    Throttled,
    /// The operation was cancelled; no usable partial recovery point remains.
    Cancelled,
    /// The `Engine` has closed.
    EngineClosed,
    /// The backup destination could not be created, written, or sealed
    /// (BK-01 extension of the W4 category table: destination I/O failure
    /// is a distinct, typed fail-closed cause).
    DestinationUnavailable,
    /// The plan did not carry the required database encryption capability.
    EncryptionRequired,
    /// The encryption capability names a different database or Region than
    /// the Engine's durable owner tuple.
    EncryptionScopeMismatch,
    /// The encryption provider refused an operation, including an expired
    /// lease or an invalid ordinal reservation.
    EncryptionUnavailable,
    /// The operation's Region is outside the database's immutable placement
    /// lock or names an inactive Region.
    PlacementMismatch,
}

/// The typed family-record input produced from the reserved collection
/// `terrabase.families` (integrator action W4-CA-5).
///
/// Crate-private by contract and never re-exported from the crate root: it
/// lets the families seam feed backup and migration without a public type or
/// a families-owned backup type. Population belongs to W4-CA-5 and
/// consumption to the family-record consumers.
///
/// The record carries the reserved document's identity, the family it
/// belongs to, the record's encoded logical value exactly as one immutable
/// snapshot observed it, and the published version at that boundary — every
/// fact a backup or migration needs to restore family state through the
/// Engine transaction seam, with no collection-layout knowledge of its own.
#[allow(
    dead_code,
    reason = "populated by the families seam (W4-CA-5); consumed with restore and migration"
)]
#[derive(Clone, Debug, PartialEq)]
pub(crate) struct FamilyRecordInput {
    id: crate::types::DocumentId,
    kind: crate::families::FamilyKind,
    value: crate::types::DocumentValue,
    version: u64,
}

#[allow(
    dead_code,
    reason = "the families tests and the restore/migration packets read these facts"
)]
impl FamilyRecordInput {
    /// Creates one record input from the snapshot facts of a reserved
    /// document.
    pub(crate) fn new(
        id: crate::types::DocumentId,
        kind: crate::families::FamilyKind,
        value: crate::types::DocumentValue,
        version: u64,
    ) -> Self {
        Self { id, kind, value, version }
    }

    /// Returns the reserved record's document identifier.
    pub(crate) fn id(&self) -> &crate::types::DocumentId {
        &self.id
    }

    /// Returns the family the record belongs to.
    pub(crate) fn kind(&self) -> crate::families::FamilyKind {
        self.kind
    }

    /// Returns the record's encoded logical value as the snapshot observed
    /// it.
    pub(crate) fn value(&self) -> &crate::types::DocumentValue {
        &self.value
    }

    /// Returns the record's published version at the snapshot boundary.
    pub(crate) fn version(&self) -> u64 {
        self.version
    }
}

// ---------------------------------------------------------------------------
// Backup execution
// ---------------------------------------------------------------------------

/// Runs one backup and seals its recovery point into `destination`
/// (PB-6010, PB-6013's local destination, PB-6017).
///
/// The run reads only the durable publication history of `engine`, writes
/// members into a staging directory, assembles the complete point — members
/// plus the sealed manifest — inside staging, and publishes
/// `destination/rp-<identity>/` by one atomic directory rename. Retrying an
/// identical run is idempotent: the identity is content-derived, so the same
/// logical archive seals exactly one recovery point, and a retry after a
/// crash at any earlier point finds no `rp-` directory to collide with. A
/// directory left unsealed by a crash (or by a writer that did not publish
/// atomically) is replaced by the identical retry rather than rejecting it.
///
/// # Errors
///
/// Returns [`BackupError::Throttled`] when another backup holds this
/// source's admission slot or the read bound rejects the run,
/// [`BackupError::ChainIncomplete`] when an incremental's base is not a
/// sealed recovery point boundary or lies beyond the provable journal,
/// [`BackupError::SourceUnavailable`] when the source cannot prove the
/// requested range, [`BackupError::Cancelled`] when the caller's signal
/// fired (leaving no usable partial recovery point), and
/// [`BackupError::DestinationUnavailable`] on destination I/O failure. A
/// production plan always carries a [`BackupEncryption`] capability; a
/// database or Region mismatch fails before the first source read.
#[allow(clippy::needless_pass_by_value)] // the public seam takes the plan by value
pub fn run_backup(
    engine: &Engine,
    plan: BackupPlan,
    destination: &Path,
) -> Result<BackupResult, BackupError> {
    if plan.read_ops_per_second == Some(0) {
        return Err(BackupError::Throttled);
    }
    validate_placement(engine, &plan)?;
    let _admission = AdmissionGuard::acquire(engine)?;
    std::fs::create_dir_all(destination).map_err(|_| BackupError::DestinationUnavailable)?;
    let staging = StagingDir::create(destination)?;
    let result = execute(engine, &plan, destination, &staging);
    // On success the published point directory was renamed out of staging
    // and the staging tree is empty; on failure nothing was sealed. Either
    // way the staging tree goes away.
    staging.discard();
    result
}

/// Validates the immutable residency lock before creating any destination
/// directory or reading source state.
fn validate_placement(engine: &Engine, plan: &BackupPlan) -> Result<(), BackupError> {
    let owner_region = engine.database_owner().region();
    if !RegionCatalog::production().is_registered(&owner_region) {
        return Err(BackupError::PlacementMismatch);
    }
    if let Some(lock) = &plan.placement {
        if !lock.allows(owner_region) {
            return Err(BackupError::PlacementMismatch);
        }
        if let BackupEncryptionMode::Encrypted(encryption) = &plan.encryption
            && !lock.allows(encryption.region())
        {
            return Err(BackupError::PlacementMismatch);
        }
    }
    Ok(())
}

/// The mutable state of one executing backup run.
struct RunState {
    logical_hasher: blake3::Hasher,
    logical_documents: u64,
    budget: ReadBudget,
    progress: Option<Arc<dyn Fn(BackupProgress) + Send + Sync>>,
    cancellation: Option<Arc<Cancellation>>,
    documents: u64,
    family_records: u64,
    deletes: u64,
    skipped: Vec<BackupSkip>,
    source_reads: u64,
}

impl RunState {
    /// Creates the run state from the plan's bounds and observers.
    fn new(plan: &BackupPlan) -> Self {
        Self {
            logical_hasher: crate::engine::logical_state_hasher(),
            logical_documents: 0,
            budget: ReadBudget::new(plan.read_ops_per_second),
            progress: plan.progress.clone(),
            cancellation: plan.cancellation.clone(),
            documents: 0,
            family_records: 0,
            deletes: 0,
            skipped: Vec::new(),
            source_reads: 0,
        }
    }

    /// Emits one progress snapshot and then checks cancellation.
    ///
    /// The snapshot carries the newest disclosure window, never the whole
    /// list: cloning every fact observed so far at every phase would grow
    /// with the square of the disclosed records.
    fn observe(&mut self, step: BackupStep) -> Result<(), BackupError> {
        if let Some(observer) = &self.progress {
            let window_start = self.skipped.len().saturating_sub(PROGRESS_DISCLOSURE_WINDOW);
            observer(BackupProgress {
                step,
                documents_read: self.documents,
                family_records_read: self.family_records,
                deletes_read: self.deletes,
                skipped_total: self.skipped.len() as u64,
                skipped_items: self.skipped[window_start..].to_vec(),
            });
        }
        if self.cancellation.as_ref().is_some_and(|token| token.is_cancelled()) {
            return Err(BackupError::Cancelled);
        }
        Ok(())
    }

    /// Spends one source read through the throttle budget.
    fn spend_read(&mut self) -> Result<(), BackupError> {
        if self.cancellation.as_ref().is_some_and(|token| token.is_cancelled()) {
            return Err(BackupError::Cancelled);
        }
        self.source_reads += 1;
        self.budget.spend(self.cancellation.as_deref())
    }
}

/// Executes the planned run after admission and destination setup.
fn execute(
    engine: &Engine,
    plan: &BackupPlan,
    destination: &Path,
    staging: &StagingDir,
) -> Result<BackupResult, BackupError> {
    let mut state = RunState::new(plan);
    state.observe(BackupStep::Admitted)?;

    let encryption = match &plan.encryption {
        BackupEncryptionMode::Encrypted(encryption) => {
            encryption.validate_for_engine(engine)?;
            Some(Arc::clone(&encryption.cipher))
        }
        #[cfg(any(test, feature = "test-support"))]
        BackupEncryptionMode::Plaintext => None,
    };
    let mut writer = match &encryption {
        Some(cipher) => MemberWriter::new_encrypted(
            staging.path().to_owned(),
            plan.member_bound_bytes,
            Arc::clone(cipher),
        ),
        None => MemberWriter::new(staging.path().to_owned(), plan.member_bound_bytes),
    };

    writer.set_byte_limit(plan.archive_byte_limit);
    let now_unix_millis = u64::try_from(
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map_err(|_| BackupError::SourceUnavailable)?
            .as_millis(),
    )
    .map_err(|_| BackupError::SourceUnavailable)?;
    if plan.capture_unix_millis.is_some_and(|captured| captured > now_unix_millis) {
        return Err(BackupError::SourceUnavailable);
    }
    // Sample the clock before taking the source high-water snapshot. The
    // snapshot includes everything committed by that instant; a racing newer
    // commit clock causes refusal below instead of overstating idle coverage.
    let boundary = resolve_boundary(engine, &mut state, plan, destination)?;
    let boundary_unix_millis =
        engine.backup_boundary_unix_millis(boundary).map_err(|_| BackupError::SourceUnavailable)?;
    // The default capture time is the sampled wall clock. Callers retrying an
    // uncertain result must carry the original explicit capture time so the
    // manifest identity remains stable.
    let captured_unix_millis = plan.capture_unix_millis.unwrap_or(now_unix_millis);
    if captured_unix_millis < boundary_unix_millis {
        return Err(BackupError::SourceUnavailable);
    }
    // Enumerate after the fixed boundary so a newly committed tenant cannot
    // be omitted from the source frontier captured above.
    let tenants = selected_tenants(engine, &mut state, plan)?;
    // Bind the complete selected scope even when a tenant contributes no
    // records to this particular range. Object-store chain planners use the
    // manifest scope to detect a requested tenant that is absent from the
    // chain root; omitting zero-count tenants would let an empty incremental
    // silently claim an expanded scope its root cannot restore.
    let mut tenant_counts: BTreeMap<TenantId, (u64, u64)> =
        tenants.iter().cloned().map(|tenant| (tenant, (0, 0))).collect();
    match plan.kind {
        BackupKind::Incremental { base } => {
            // The journal range below the effective history bound is no
            // longer provable once the journal has given it up: the run
            // fails closed from `effective_start` (M0-2 deleted the
            // retained-evidence store that proved discarded windows).
            capture_logical_state_at(engine, &mut state, &tenants, boundary)?;
            archive_journal_range(
                engine,
                &mut state,
                &mut writer,
                &mut tenant_counts,
                &tenants,
                base,
                boundary,
            )?;
        }
        BackupKind::Full | BackupKind::Snapshot => {
            archive_state_at(
                engine,
                &mut state,
                &mut writer,
                &mut tenant_counts,
                &tenants,
                boundary,
            )?;
        }
    }
    seal_run(
        &mut state,
        writer,
        tenant_counts,
        boundary,
        plan.kind,
        boundary_unix_millis,
        captured_unix_millis,
        captured_unix_millis
            .checked_add(u64::from(plan.retention_days) * 86_400_000)
            .ok_or(BackupError::ManifestInvalid)?,
        engine.database_id().to_text(),
        destination,
        staging,
        encryption.as_ref(),
        plan.reader.as_ref().map(|reader| reader.as_ref() as &dyn BackupReader),
    )
}

/// Resolves the boundary this run covers and proves it, failing closed when
/// the requested boundary or range is not provable from the journal.
fn resolve_boundary(
    engine: &Engine,
    state: &mut RunState,
    plan: &BackupPlan,
    destination: &Path,
) -> Result<StorageSequence, BackupError> {
    let unavailable = |_| BackupError::SourceUnavailable;
    state.spend_read()?;
    let high_water = engine.journaled_high_water().map_err(unavailable)?;
    state.spend_read()?;
    let servable_start = engine.servable_history_start().map_err(unavailable)?;
    match plan.kind {
        BackupKind::Full => Ok(high_water.unwrap_or_default()),
        BackupKind::Snapshot => {
            let pinned = plan.pinned_boundary.expect("a snapshot plan pins its boundary");
            // A pinned PITR cut must still be covered by the servable journal
            // window.  Checking only the high-water mark is unsafe after
            // compaction: a request for sequence two can otherwise start at
            // the retained sequence four, skip that page as newer than the
            // requested cut, and seal an empty/incomplete snapshot.
            let below_retained_window =
                servable_start.is_some_and(|start| pinned.get() < start.get());
            if pinned.get() > high_water.unwrap_or_default().get() || below_retained_window {
                return Err(BackupError::SourceUnavailable);
            }
            Ok(pinned)
        }
        BackupKind::Incremental { base } => {
            // The base must be a sealed recovery-point boundary in this
            // destination, and the range must be provable.
            let points = match &plan.encryption {
                #[cfg(any(test, feature = "test-support"))]
                BackupEncryptionMode::Plaintext => discover_points(destination)?,
                BackupEncryptionMode::Encrypted(encryption) => {
                    if let Some(reader) = &plan.reader {
                        restore::discover_encrypted_points(destination, reader.as_ref())?
                    } else {
                        let cipher =
                            encryption.cipher.lock().map_err(|_| BackupError::SourceUnavailable)?;
                        restore::discover_encrypted_points(destination, &*cipher)?
                    }
                }
            };
            let anchored = points.iter().any(|point| point.boundary_sequence() == base);
            if !anchored {
                return Err(BackupError::ChainIncomplete);
            }
            let proven = high_water.unwrap_or_default();
            if base.get() > proven.get() {
                return Err(BackupError::ChainIncomplete);
            }
            // The effective history bound is the journal's own servable
            // bound. A base below it is not provable: the run fails closed
            // rather than silently skipping the given-up window.
            match servable_start {
                // Sequence zero is the implicit empty root before the first
                // journal publication.  It remains a valid incremental base
                // even when the first retained row starts at sequence one;
                // rejecting it would make an initial full backup impossible
                // to extend after the first write.
                Some(start) if base.get() != 0 && base.get() < start.get() => {
                    Err(BackupError::SourceUnavailable)
                }
                None if base.get() > 0 => Err(BackupError::SourceUnavailable),
                _ => Ok(proven),
            }
        }
    }
}

/// Enumerates the committed tenants this run archives, narrowed by the
/// plan's selection.
fn selected_tenants(
    engine: &Engine,
    state: &mut RunState,
    plan: &BackupPlan,
) -> Result<Vec<TenantId>, BackupError> {
    state.spend_read()?;
    let mut tenants = engine.committed_tenants().map_err(|_| BackupError::SourceUnavailable)?;
    if let Some(selected) = &plan.tenants {
        tenants.retain(|tenant| selected.contains(tenant));
    }
    tenants.sort();
    state.observe(BackupStep::ReadingJournal)?;
    Ok(tenants)
}

/// Archives the journal range `(base, boundary]` for an incremental run,
/// paging each tenant's commits in publication order.
#[allow(
    clippy::too_many_arguments,
    reason = "one journal archive pass: the range bounds plus the retained window it must skip"
)]
fn archive_journal_range(
    engine: &Engine,
    state: &mut RunState,
    writer: &mut MemberWriter,
    tenant_counts: &mut BTreeMap<TenantId, (u64, u64)>,
    tenants: &[TenantId],
    base: StorageSequence,
    boundary: StorageSequence,
) -> Result<(), BackupError> {
    let unavailable = |_| BackupError::SourceUnavailable;
    for tenant in tenants {
        let mut after = base;
        loop {
            state.spend_read()?;
            let page =
                engine.tenant_commits(tenant, after, JOURNAL_PAGE_LIMIT).map_err(unavailable)?;
            if page.is_empty() {
                break;
            }
            let reached_boundary =
                page.last().is_none_or(|commit| commit.sequence().get() >= boundary.get());
            state.spend_read()?;
            let sequences: Vec<_> = page.iter().map(PublishedCommit::sequence).collect();
            let clocks = engine.backup_commit_clocks(&sequences).map_err(unavailable)?;
            for (commit, committed_unix_millis) in page.iter().zip(clocks) {
                if commit.sequence().get() > boundary.get() {
                    continue;
                }
                archive_commit(state, writer, tenant_counts, commit, committed_unix_millis)?;
            }
            after = page.last().map_or(after, PublishedCommit::sequence);
            if reached_boundary {
                break;
            }
        }
        state.observe(BackupStep::ReadingJournal)?;
    }
    Ok(())
}

/// Reads complete logical state for an incremental's fixed boundary. Journal
/// member counts describe mutations; this digest describes the restored state.
fn capture_logical_state_at(
    engine: &Engine,
    state: &mut RunState,
    tenants: &[TenantId],
    boundary: StorageSequence,
) -> Result<(), BackupError> {
    for tenant in tenants {
        let mut after = None;
        loop {
            state.spend_read()?;
            let page = engine
                .snapshot_records_from(tenant.clone(), boundary, after, MAX_SNAPSHOT_RECORD_PAGE)
                .map_err(|_| BackupError::SourceUnavailable)?;
            if page.is_empty() {
                break;
            }
            for record in &page {
                state
                    .logical_hasher
                    .update(&crate::engine::logical_document_digest(tenant, record.document()));
                state.logical_documents =
                    state.logical_documents.checked_add(1).ok_or(BackupError::SourceUnavailable)?;
            }
            let last = page.last().expect("nonempty snapshot page").document();
            after = Some((last.collection().clone(), last.id().clone()));
            state.observe(BackupStep::ReadingJournal)?;
        }
    }
    Ok(())
}

/// Archives the source state at `boundary` for a full or snapshot run by
/// paging final-state records directly into the staged member writer.
///
/// # Memory bound
///
/// The Engine page seam bounds backup's working set to one page plus the
/// storage indexes; no complete tenant state or second archive bundle is
/// materialized here.
fn archive_state_at(
    engine: &Engine,
    state: &mut RunState,
    writer: &mut MemberWriter,
    tenant_counts: &mut BTreeMap<TenantId, (u64, u64)>,
    tenants: &[TenantId],
    boundary: StorageSequence,
) -> Result<(), BackupError> {
    for tenant in tenants {
        let mut after = None;
        loop {
            state.spend_read()?;
            let page = engine
                .snapshot_records_from(
                    tenant.clone(),
                    boundary,
                    after.clone(),
                    MAX_SNAPSHOT_RECORD_PAGE,
                )
                .map_err(|_| BackupError::SourceUnavailable)?;
            if page.is_empty() {
                break;
            }
            for record in &page {
                let document = record.document().clone();
                state
                    .logical_hasher
                    .update(&crate::engine::logical_document_digest(tenant, &document));
                state.logical_documents =
                    state.logical_documents.checked_add(1).ok_or(BackupError::SourceUnavailable)?;
                let entry = tenant_counts.entry(tenant.clone()).or_default();
                if is_family_record(document.collection().as_str()) {
                    entry.1 = entry.1.checked_add(1).ok_or(BackupError::DestinationUnavailable)?;
                    state.family_records = state
                        .family_records
                        .checked_add(1)
                        .ok_or(BackupError::DestinationUnavailable)?;
                } else {
                    entry.0 = entry.0.checked_add(1).ok_or(BackupError::DestinationUnavailable)?;
                    state.documents = state
                        .documents
                        .checked_add(1)
                        .ok_or(BackupError::DestinationUnavailable)?;
                }
                writer
                    .write(&Record::Document {
                        tenant: tenant.clone(),
                        sequence: boundary.get(),
                        committed_unix_millis: 0,
                        document,
                        durability: record.durability(),
                    })
                    .map_err(|_| BackupError::DestinationUnavailable)?;
            }
            let last = page.last().expect("non-empty snapshot page").document();
            after = Some((last.collection().clone(), last.id().clone()));
            if page.len() < MAX_SNAPSHOT_RECORD_PAGE as usize {
                break;
            }
        }
        state.observe(BackupStep::ReadingJournal)?;
    }
    Ok(())
}

/// Finishes the member writer and seals the manifest, returning the
/// terminal result of the run.
#[allow(clippy::too_many_arguments)]
fn seal_run(
    state: &mut RunState,
    writer: MemberWriter,
    tenant_counts: BTreeMap<TenantId, (u64, u64)>,
    boundary: StorageSequence,
    kind: BackupKind,
    boundary_unix_millis: u64,
    captured_unix_millis: u64,
    retain_until_unix_millis: u64,
    database_id: String,
    destination: &Path,
    staging: &StagingDir,
    encryption: Option<&Arc<Mutex<ArtifactCipher>>>,
    reader: Option<&dyn BackupReader>,
) -> Result<BackupResult, BackupError> {
    state.observe(BackupStep::WritingArchive)?;
    let sealed = writer.finish().map_err(|_| BackupError::DestinationUnavailable)?;
    let members: Vec<BackupMember> = sealed
        .iter()
        .map(|member| {
            BackupMember::new(member.name().to_owned(), member.length(), member.digest().to_owned())
        })
        .collect();
    let tenant_counts: Vec<BackupTenantCount> = tenant_counts
        .into_iter()
        .map(|(tenant, (documents, family_records))| {
            BackupTenantCount::new(tenant, documents, family_records)
        })
        .collect();
    state.observe(BackupStep::SealingManifest)?;
    let facts = ManifestFacts {
        database_id,
        partition_id: "partition-000000".to_owned(),
        kind,
        boundary,
        boundary_unix_millis,
        captured_unix_millis,
        retain_until_unix_millis,
        logical_checksum: state.logical_hasher.finalize().to_hex().to_string(),
        logical_documents: state.logical_documents,
        documents: state.documents,
        family_records: state.family_records,
        deletes: state.deletes,
        disclosures: state.skipped.len() as u64,
        tenants: tenant_counts,
        members: members.clone(),
    };
    let manifest = BackupManifest::assemble(facts);
    let identity = manifest.identity().to_owned();
    let encrypted_manifest = encryption
        .map(|cipher| {
            cipher
                .lock()
                .map_err(|_| BackupError::DestinationUnavailable)?
                .seal(manifest.render().as_bytes())
                .map_err(|_| BackupError::DestinationUnavailable)
        })
        .transpose()?;
    // Encryption deliberately consumes a fresh ordinal on every attempt, so
    // a retry after an uncertain result has different ciphertext. Resolve an
    // already published capture by its authenticated logical facts before
    // publishing another recovery-point identity. This preserves nonce
    // uniqueness while keeping the recovery-point operation idempotent.
    if let Some(existing) = find_existing_capture(destination, &manifest, encryption, reader)? {
        verify_members(destination, existing.identity(), existing.members())?;
        let archive =
            BackupArchive::new(existing.identity().to_owned(), existing.members().to_vec());
        return Ok(BackupResult::new(existing, archive, state.skipped.clone(), state.source_reads));
    }
    seal_point(destination, staging, &manifest, encrypted_manifest.as_deref())?;
    let archive = BackupArchive::new(identity, members);
    Ok(BackupResult::new(manifest, archive, state.skipped.clone(), state.source_reads))
}

/// Finds a previously published attempt for the same logical capture. Member
/// ciphertext and the encrypted manifest are intentionally excluded: both
/// may differ after a retry because the cipher must consume a new nonce
/// ordinal.
fn find_existing_capture(
    destination: &Path,
    expected: &BackupManifest,
    encryption: Option<&Arc<Mutex<ArtifactCipher>>>,
    reader: Option<&dyn BackupReader>,
) -> Result<Option<BackupManifest>, BackupError> {
    let points = match encryption {
        Some(cipher) => {
            if let Some(reader) = reader {
                restore::discover_encrypted_points(destination, reader)?
            } else {
                let cipher = cipher.lock().map_err(|_| BackupError::DestinationUnavailable)?;
                restore::discover_encrypted_points(destination, &*cipher)?
            }
        }
        None => discover_points(destination)?,
    };
    Ok(points.into_iter().find(|candidate| same_capture_facts(candidate, expected)))
}

fn same_capture_facts(left: &BackupManifest, right: &BackupManifest) -> bool {
    left.database_id() == right.database_id()
        && left.partition_id() == right.partition_id()
        && left.tenant_scope_digest() == right.tenant_scope_digest()
        && left.kind() == right.kind()
        && left.boundary_sequence() == right.boundary_sequence()
        && left.boundary_unix_millis() == right.boundary_unix_millis()
        && left.captured_unix_millis() == right.captured_unix_millis()
        && left.retain_until_unix_millis() == right.retain_until_unix_millis()
        && left.logical_checksum() == right.logical_checksum()
        && left.logical_documents() == right.logical_documents()
        && left.documents() == right.documents()
        && left.family_records() == right.family_records()
        && left.deletes() == right.deletes()
        && left.disclosures() == right.disclosures()
        && left.tenants() == right.tenants()
}

/// Classifies whether a collection name is the reserved family collection.
fn is_family_record(collection: &str) -> bool {
    collection == RESERVED_FAMILY_COLLECTION
}

/// Archives one journal commit's writes for an incremental run, recording
/// disclosures for sub-`Synced` durability.
fn archive_commit(
    state: &mut RunState,
    writer: &mut MemberWriter,
    tenant_counts: &mut BTreeMap<TenantId, (u64, u64)>,
    commit: &PublishedCommit,
    committed_unix_millis: u64,
) -> Result<(), BackupError> {
    let disclose =
        matches!(commit.durability(), LocalDurability::Buffered | LocalDurability::Written);
    let entry = tenant_counts.entry(commit.tenant().clone()).or_default();
    for write in commit.writes() {
        match write {
            PublishedWrite::Put { document } => {
                if is_family_record(document.collection().as_str()) {
                    entry.1 += 1;
                    state.family_records += 1;
                } else {
                    entry.0 += 1;
                    state.documents += 1;
                }
                if disclose {
                    state.skipped.push(BackupSkip::new(
                        commit.tenant().clone(),
                        document.id().clone(),
                        BackupSkipReason::DurabilityBelowSynced,
                    ));
                }
                writer
                    .write(&Record::Document {
                        tenant: commit.tenant().clone(),
                        sequence: commit.sequence().get(),
                        committed_unix_millis,
                        document: document.clone(),
                        durability: commit.durability(),
                    })
                    .map_err(|_| BackupError::DestinationUnavailable)?;
            }
            PublishedWrite::Delete { collection, id, version_at_delete } => {
                state.deletes += 1;
                if disclose {
                    state.skipped.push(BackupSkip::new(
                        commit.tenant().clone(),
                        id.clone(),
                        BackupSkipReason::DurabilityBelowSynced,
                    ));
                }
                writer
                    .write(&Record::Delete {
                        tenant: commit.tenant().clone(),
                        sequence: commit.sequence().get(),
                        committed_unix_millis,
                        collection: collection.as_ref().map(|name| name.as_str().to_owned()),
                        id: id.as_str().to_owned(),
                        version_at_delete: *version_at_delete,
                        durability: commit.durability(),
                    })
                    .map_err(|_| BackupError::DestinationUnavailable)?;
            }
        }
    }
    Ok(())
}

// ---------------------------------------------------------------------------
// Destination layout, sealing, and discovery
// ---------------------------------------------------------------------------

/// Publishes one sealed recovery point by a single atomic directory rename.
///
/// The members and the sealed manifest are assembled into a candidate
/// directory *inside staging* — never under an `rp-` name — and the complete
/// directory is then renamed into place as `destination/rp-<identity>`. A
/// crash before that rename leaves no `rp-` directory at all, so a recovery
/// point is either entirely absent or entirely sealed: there is no window in
/// which `rp-<identity>` exists holding members without its manifest. That is
/// what makes the documented identical retry idempotent instead of bricking
/// the destination on `EEXIST`.
fn seal_point(
    destination: &Path,
    staging: &StagingDir,
    manifest: &BackupManifest,
    encrypted_manifest: Option<&[u8]>,
) -> Result<(), BackupError> {
    let point = destination.join(point_directory(manifest.identity()));
    let manifest_file = encrypted_manifest.map_or(MANIFEST_FILE, |_| ENCRYPTED_MANIFEST_FILE);
    if std::fs::symlink_metadata(&point).is_ok_and(|metadata| !metadata.file_type().is_dir()) {
        return Err(BackupError::ManifestInvalid);
    }
    if point.join(manifest_file).is_file() {
        // Idempotent retry: the identical recovery point is already
        // sealed, and a second run must not mint a second one.
        let existing = std::fs::read(point.join(manifest_file))
            .map_err(|_| BackupError::DestinationUnavailable)?;
        let expected =
            encrypted_manifest.map_or_else(|| manifest.render().into_bytes(), <[u8]>::to_vec);
        if existing == expected {
            verify_members(destination, manifest.identity(), manifest.members())?;
            return sync_directory(destination);
        }
        eprintln!("seal_point checksum mismatch point={:?} manifest_file={:?}", point, point.join(manifest_file));
        return Err(BackupError::ChecksumMismatch);
    }
    if point.is_dir() {
        // An `rp-<identity>` directory with no sealed manifest is an unsealed
        // orphan: nothing sealed ever lived there, because publication is
        // directory-atomic and never builds under an `rp-` name. Publishing
        // over it is the documented identical retry, never a loss.
        std::fs::remove_dir_all(&point).map_err(|_| BackupError::DestinationUnavailable)?;
    }
    let candidate = staging.point_candidate();
    std::fs::create_dir(&candidate).map_err(|_| BackupError::DestinationUnavailable)?;
    for member in manifest.members() {
        std::fs::rename(staging.path().join(member.name()), candidate.join(member.name()))
            .map_err(|_| BackupError::DestinationUnavailable)?;
    }
    let candidate_manifest = candidate.join(manifest_file);
    let manifest_bytes =
        encrypted_manifest.map_or_else(|| manifest.render().into_bytes(), <[u8]>::to_vec);
    std::fs::write(&candidate_manifest, manifest_bytes)
        .and_then(|()| {
            let file = std::fs::File::open(&candidate_manifest)?;
            file.sync_all()
        })
        .map_err(|_| BackupError::DestinationUnavailable)?;
    sync_directory(&candidate)?;
    match std::fs::rename(&candidate, &point) {
        Ok(()) => {}
        Err(_) if point.join(manifest_file).is_file() => {
            // Another run sealed the same content-derived identity first;
            // the same identity binds the same facts.
            let existing = std::fs::read(point.join(manifest_file))
                .map_err(|_| BackupError::DestinationUnavailable)?;
            let expected =
                encrypted_manifest.map_or_else(|| manifest.render().into_bytes(), <[u8]>::to_vec);
            if existing == expected {
                verify_members(destination, manifest.identity(), manifest.members())?;
                return sync_directory(destination);
            }
            return Err(BackupError::ChecksumMismatch);
        }
        Err(_) => return Err(BackupError::DestinationUnavailable),
    }
    sync_directory(destination)
}

/// A published name is not durable until its containing directory is synced.
/// Propagate failures even after rename: the caller must treat that outcome as
/// uncertain and verify the recovery point on retry, never report it healthy.
fn sync_directory(path: &Path) -> Result<(), BackupError> {
    std::fs::File::open(path)
        .and_then(|directory| directory.sync_all())
        .map_err(|_| BackupError::DestinationUnavailable)
}

/// Loads and validates the sealed manifest of one point directory.
fn load_point_manifest(point: &Path) -> Result<BackupManifest, BackupError> {
    let point_metadata =
        std::fs::symlink_metadata(point).map_err(|_| BackupError::ManifestInvalid)?;
    if !point_metadata.file_type().is_dir() {
        return Err(BackupError::ManifestInvalid);
    }
    let manifest_path = point.join(MANIFEST_FILE);
    let manifest_metadata =
        std::fs::symlink_metadata(&manifest_path).map_err(|_| BackupError::ManifestInvalid)?;
    if !manifest_metadata.file_type().is_file() {
        return Err(BackupError::ManifestInvalid);
    }
    let bytes = manifest::read_manifest_bytes(&manifest_path)?;
    let text = std::str::from_utf8(&bytes).map_err(|_| BackupError::ManifestInvalid)?;
    manifest::parse_manifest(text)
}

/// Discovers every sealed recovery point in a destination, failing closed
/// on any invalid sealed manifest and ignoring anything that is not one.
fn discover_points(destination: &Path) -> Result<Vec<BackupManifest>, BackupError> {
    let entries = std::fs::read_dir(destination).map_err(|_| BackupError::ChainIncomplete)?;
    let mut points = Vec::new();
    for entry in entries {
        let entry = entry.map_err(|_| BackupError::ManifestInvalid)?;
        let name = entry.file_name();
        let Some(name) = name.to_str() else {
            continue;
        };
        let Some(identity) = name.strip_prefix("rp-") else {
            continue;
        };
        if manifest::validate_identity(identity).is_err() {
            // Not a recovery-point directory; discovery ignores it.
            continue;
        }
        let path = entry.path();
        // Publication is directory-atomic, so a recovery-point directory is
        // either complete or an unsealed orphan (a crash left it behind, or a
        // pre-atomic writer did). An orphan is not a recovery point and must
        // not fail the whole destination: the identical retry replaces it.
        // A directory that *does* hold a sealed manifest still fails closed
        // when that manifest does not validate.
        let Ok(metadata) = std::fs::symlink_metadata(&path) else {
            continue;
        };
        if !metadata.file_type().is_dir() || !path.join(MANIFEST_FILE).is_file() {
            continue;
        }
        points.push(load_point_manifest(&path)?);
    }
    Ok(points)
}

/// Verifies every bound member of one recovery point in `destination`.
fn verify_members(
    destination: &Path,
    identity: &str,
    members: &[BackupMember],
) -> Result<(), BackupError> {
    let point = destination.join(point_directory(identity));
    for member in members {
        let path = point.join(member.name());
        // Do not follow a destination-controlled symlink while verifying a
        // member: the manifest's canonical name binds a regular archive file
        // inside this point, never an arbitrary path elsewhere on disk.
        let metadata =
            std::fs::symlink_metadata(&path).map_err(|_| BackupError::ChainIncomplete)?;
        if !metadata.file_type().is_file() {
            return Err(BackupError::ChainIncomplete);
        }
        if metadata.len() != member.length() {
            return Err(BackupError::ChecksumMismatch);
        }
        if digest_file(&path).map_err(|_| BackupError::ChainIncomplete)? != member.digest() {
            return Err(BackupError::ChecksumMismatch);
        }
    }
    Ok(())
}

/// Loads one recovery point's manifest by identity, validating it strictly
/// before anything is done with it (PB-6015).
///
/// # Errors
///
/// Returns [`BackupError::ManifestInvalid`] when the identity is malformed,
/// the point is absent or still in progress, or the manifest fails strict
/// validation (including the recomputed content digest).
pub fn load_recovery_point(
    destination: &Path,
    identity: &str,
) -> Result<BackupManifest, BackupError> {
    manifest::validate_identity(identity)?;
    let point = destination.join(point_directory(identity));
    load_point_manifest(&point)
}

/// Verifies the complete ordered chain of the destination's newest
/// recovery point: walks every incremental link down to its root, then
/// re-digests every bound member of every point in the chain (PB-6010,
/// PB-6014).
///
/// The returned chain is ordered from the root to the newest point.
///
/// # Errors
///
/// Returns [`BackupError::ChainIncomplete`] when the destination holds no
/// recovery point or a chain link is missing,
/// [`BackupError::ManifestInvalid`] when any discovered manifest fails
/// validation, and [`BackupError::ChecksumMismatch`] when any bound member
/// fails its length or digest check.
pub fn verify_latest_chain(destination: &Path) -> Result<Vec<BackupManifest>, BackupError> {
    let chain = select_latest_chain(destination)?;
    for point in &chain {
        verify_members(destination, point.identity(), point.members())?;
    }
    Ok(chain)
}

/// Selects the newest complete chain using only sealed manifests and their
/// metadata. Restore uses this seam so that member bytes are opened and
/// authenticated exactly once, at replay time, rather than verified and then
/// reopened through a pathname.
pub(crate) fn select_latest_chain(destination: &Path) -> Result<Vec<BackupManifest>, BackupError> {
    let points = discover_points(destination)?;
    if points.is_empty() {
        return Err(BackupError::ChainIncomplete);
    }
    // The newest boundary is the head; an incremental at the same boundary
    // is the later artifact, and identity breaks remaining ties so the
    // choice is deterministic.
    let head = points
        .iter()
        .max_by(|left, right| {
            (
                left.boundary_sequence().get(),
                usize::from(left.base_sequence().is_some()),
                left.identity(),
            )
                .cmp(&(
                    right.boundary_sequence().get(),
                    usize::from(right.base_sequence().is_some()),
                    right.identity(),
                ))
        })
        .ok_or(BackupError::ChainIncomplete)?;
    let mut chain = vec![head.clone()];
    let mut visited = std::collections::BTreeSet::from([head.identity().to_owned()]);
    while let Some(base) = chain.last().and_then(BackupManifest::base_sequence) {
        let link = points
            .iter()
            .filter(|point| {
                point.boundary_sequence() == base && !visited.contains(point.identity())
            })
            .min_by(|left, right| {
                // A chain root at the base boundary is preferred over
                // another link; identity breaks remaining ties.
                (usize::from(left.base_sequence().is_some()), left.identity())
                    .cmp(&(usize::from(right.base_sequence().is_some()), right.identity()))
            })
            .ok_or(BackupError::ChainIncomplete)?;
        visited.insert(link.identity().to_owned());
        chain.push(link.clone());
    }
    chain.reverse();
    validate_chain_frontier(&chain)?;
    Ok(chain)
}

/// Validates the restore frontier represented by an ordered chain.
///
/// A storage sequence is only a verified frontier when every link advances
/// from the exact preceding boundary and all links belong to one database
/// and partition. Incremental links may narrow (but never expand) the root
/// tenant scope. This check deliberately permits an equal
/// boundary: an empty incremental is a valid idempotent publication in the
/// existing backup contract.  It rejects regressions, mixed scopes, and a
/// chain that begins with an incremental link before any root exists.
pub(crate) fn validate_chain_frontier(chain: &[BackupManifest]) -> Result<(), BackupError> {
    let Some(root) = chain.first() else {
        return Err(BackupError::ChainIncomplete);
    };
    if matches!(root.kind(), BackupKind::Incremental { .. }) {
        return Err(BackupError::ChainIncomplete);
    }
    let root_tenants: std::collections::BTreeSet<_> =
        root.tenants().iter().map(manifest::BackupTenantCount::tenant).collect();
    for link in chain.iter().skip(1) {
        let mut link_tenants = link.tenants().iter().map(manifest::BackupTenantCount::tenant);
        if link.database_id() != root.database_id()
            || link.partition_id() != root.partition_id()
            // Incrementals may legitimately be scoped to a subset (or have
            // no records for a no-op run), but can never introduce a tenant
            // absent from the chain root.
            || link_tenants.any(|tenant| !root_tenants.contains(tenant))
        {
            return Err(BackupError::ManifestInvalid);
        }
    }
    for pair in chain.windows(2) {
        let previous = &pair[0];
        let current = &pair[1];
        let Some(base) = current.base_sequence() else {
            return Err(BackupError::ChainIncomplete);
        };
        if current.boundary_unix_millis() < previous.boundary_unix_millis() {
            return Err(BackupError::ManifestInvalid);
        }
        if base != previous.boundary_sequence() || current.boundary_sequence().get() < base.get() {
            return Err(BackupError::ChainIncomplete);
        }
    }
    Ok(())
}

// ---------------------------------------------------------------------------
// Admission, throttling, and staging
// ---------------------------------------------------------------------------

/// The process-wide admission registry: one backup job per source Engine
/// at a time (PB-6010).
static ACTIVE_SOURCES: std::sync::Mutex<std::collections::BTreeSet<usize>> =
    std::sync::Mutex::new(std::collections::BTreeSet::new());

/// The held admission slot of one running backup. Dropping the guard
/// releases the slot, including on panic unwinds.
struct AdmissionGuard {
    source: usize,
}

impl AdmissionGuard {
    /// Acquires the admission slot of `engine`, keying the source by its
    /// instance identity.
    ///
    /// # Errors
    ///
    /// Returns [`BackupError::Throttled`] when another backup already
    /// holds this source's slot.
    fn acquire(engine: &Engine) -> Result<Self, BackupError> {
        let source = std::ptr::from_ref(engine) as usize;
        let mut active = ACTIVE_SOURCES.lock().map_err(|_| BackupError::SourceUnavailable)?;
        if !active.insert(source) {
            return Err(BackupError::Throttled);
        }
        Ok(Self { source })
    }
}

impl Drop for AdmissionGuard {
    fn drop(&mut self) {
        if let Ok(mut active) = ACTIVE_SOURCES.lock() {
            active.remove(&self.source);
        }
    }
}

/// A fixed-window read-rate budget (PB-6017's `MaxReadOpsPerSecond`
/// equivalent).
struct ReadBudget {
    rate: Option<u32>,
    window_start: Option<Instant>,
    spent: u32,
}

impl ReadBudget {
    /// Creates a budget with the plan's read bound; `None` is unbounded.
    fn new(rate: Option<u32>) -> Self {
        Self { rate, window_start: None, spent: 0 }
    }

    /// Spends one read, sleeping into the next window when the bound is
    /// reached. The bound delays reads only: it never reorders or drops
    /// records.
    ///
    /// # Errors
    ///
    /// Returns [`BackupError::Throttled`] when the bound is zero.
    fn spend(&mut self, cancellation: Option<&Cancellation>) -> Result<(), BackupError> {
        let Some(rate) = self.rate else {
            return Ok(());
        };
        if rate == 0 {
            return Err(BackupError::Throttled);
        }
        if cancellation.is_some_and(Cancellation::is_cancelled) {
            return Err(BackupError::Cancelled);
        }
        let now = Instant::now();
        let window_expired = self
            .window_start
            .is_some_and(|start| now.duration_since(start) >= Duration::from_secs(1));
        if self.window_start.is_none() || window_expired {
            self.window_start = Some(now);
            self.spent = 1;
            return Ok(());
        }
        if self.spent < rate {
            self.spent += 1;
            return Ok(());
        }
        let start = self.window_start.unwrap_or(now);
        let wake = start + Duration::from_secs(1);
        while let Some(remaining) = wake.checked_duration_since(Instant::now()) {
            if cancellation.is_some_and(Cancellation::is_cancelled) {
                return Err(BackupError::Cancelled);
            }
            std::thread::sleep(remaining.min(Duration::from_millis(10)));
        }
        self.window_start = Some(Instant::now());
        self.spent = 1;
        Ok(())
    }
}

/// One staging directory for a run's members, removed when the run ends.
///
/// The name never starts with `rp-`, so an interrupted or cancelled run
/// leaves nothing that discovery could mistake for a recovery point.
struct StagingDir {
    path: PathBuf,
}

impl StagingDir {
    /// Creates a fresh staging directory inside the destination.
    ///
    /// # Errors
    ///
    /// Returns [`BackupError::DestinationUnavailable`] when the directory
    /// cannot be created.
    fn create(destination: &Path) -> Result<Self, BackupError> {
        static COUNTER: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(0);
        let index = COUNTER.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
        // The counter is only a readable diagnostic suffix.  A process can
        // restart with the same PID and counter, so include a cryptographic
        // ULID to ensure a crash-left staging directory can never block the
        // next run by name collision.
        let path = destination.join(format!(
            ".staging-{}-{index}-{}",
            std::process::id(),
            crate::identity::generate()
        ));
        std::fs::create_dir(&path).map_err(|_| BackupError::DestinationUnavailable)?;
        Ok(Self { path })
    }

    /// Returns the staging directory path.
    fn path(&self) -> &Path {
        &self.path
    }

    /// Returns the in-staging candidate path for one recovery point.
    ///
    /// The candidate assembles the complete point — every member plus the
    /// sealed manifest — and is published by one atomic directory rename, so
    /// no `rp-` name is ever observable in a partially built state.
    fn point_candidate(&self) -> PathBuf {
        self.path.join("point")
    }

    /// Removes the staging tree. After a successful seal the members have
    /// been moved out and the directory is empty.
    fn discard(self) {
        let _ = std::fs::remove_dir_all(&self.path);
    }
}
