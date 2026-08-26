//! The V1→V2 Logical Migration (BK-03).
//!
//! The Logical Migration is the sanctioned customer migration path
//! (PD-2026-08-26-10 D4): it reads an *approved V1 representation* — a
//! manifest-verified export artifact of logical V1 records — transforms the
//! records under the explicit contract below, and writes V2 data
//! exclusively through the Engine transaction seam (document puts and
//! deletes, and typed family writes through the family store, which stages
//! into the same engine transactions). It never writes storage files
//! directly, and it **never claims that V2 opens V1 files directly**: the
//! seam's only input is a representation directory, and any directory
//! without a strictly valid representation manifest fails closed with
//! [`MigrationError::ManifestInvalid`].
//!
//! # The representation
//!
//! A representation directory holds one `manifest.tbv1m` text manifest and
//! ordered `segment-NNNNNN` binaries of length-prefixed logical record
//! frames (see the private codec module for the exact byte format). The
//! manifest binds the format version (`terrabase-v1-export 1`; any other
//! version fails closed as [`MigrationError::UnsupportedVersion`]), the
//! record count of every kind, and every segment's ordered name, exact
//! length, and SHA-256 digest. Its identity is the SHA-256 digest of all
//! manifest lines except the identity line itself, so any edit of a bound
//! fact breaks validation. Validation and digest verification run before
//! any write: a tampered count, digest, or identity writes nothing.
//!
//! The representation is a synthetic, documented format — no live V1 store
//! is opened to produce it, and no physical V1 layout (journal, page, or
//! header bytes) ever crosses this seam.
//!
//! # The transformation contract
//!
//! Every record kind has exactly one disposition, and the terminal
//! [`MigrationReport`] carries the explicit [`MigrationLossMatrix`] —
//! success never means "everything in V1 arrived":
//!
//! - Documents and revisions convert through the Engine transaction seam;
//!   the revision policy states whether every V1 version converts
//!   ([`MigrationRevisionPolicy::AllVersions`]) or only the newest version
//!   of each key ([`MigrationRevisionPolicy::LatestOnly`], with the
//!   collapsed revision count disclosed). V2 assigns its own document
//!   versions; V1 version numbers order the conversion, they are not
//!   re-stamped.
//! - Referenced attachments convert as family records — metadata and
//!   content digest through the typed family seam. Content *bytes* do not
//!   travel in representation format version 1; the loss-matrix row names
//!   that limit for every converted attachment instead of dropping them
//!   silently.
//! - Tombstones follow the stated policy: convert as engine deletes, or
//!   drop with a recorded count.
//! - Counters, time-series values, identities, and compare-exchange values
//!   convert as typed family writes. A counter record carries the V1 value
//!   and applies as one increment, so a target that already holds the value
//!   is a duplicate application, and duplicates are prevented by operation
//!   identity, not by subtraction. Compare-exchange values convert through
//!   the missing-key condition; the V1 raft index is not preserved (the
//!   V1→V2 change-vector and metadata mapping is owned by GAP-D02, #59,
//!   and is consumed here as a recorded dependency once it lands).
//! - V1 storage-internal structures, unreferenced blobs, Platform-owned
//!   configuration, and V1 index definitions are dropped with a count and
//!   a reason — never silently. Unselected kinds are dropped as unselected,
//!   also with a reason.
//!
//! # Restartability and idempotency
//!
//! Every record converts under a deterministic operation identity derived
//! from the representation identity and the record's ordinal, so an
//! interrupted run (cancellation is checked per record) resumes exactly
//! from its last durable record: a resumed run reads the durable
//! publication history — the same source backup reads — to learn which of
//! the run's operation identities already committed, counts those records
//! as duplicates, and applies only the remainder. An uncertain outcome is
//! resolved by operation identity, and a record whose application remains
//! undecided fails closed as [`MigrationError::NotResumable`] rather than
//! double-applying. If the servable history no longer reaches back over
//! the run's operations, a missed duplicate is re-applied under its
//! identical operation identity and write set and the engine's operation
//! ledger replays it without a second write: the data stays idempotent,
//! and only the duplicate accounting loses coverage.
//!
//! # Verification
//!
//! Before success is reported, the run re-verifies that every decoded kind
//! reconciles with the manifest — converted plus duplicates plus collapsed
//! revisions plus dropped equals the manifest count — and the report's
//! [`MigrationVerification`] carries the digest and count verdicts. A
//! manifest that lies about its contents fails closed as
//! [`MigrationError::ManifestInvalid`].
//!
//! # Limits
//!
//! Decoding materializes one segment at a time; a streaming decoder is an
//! open improvement, not a claim. Counter records apply as increments of
//! the V1 value, which is exact on a fresh target and safe on a resumed
//! one only because operation identity prevents double application. The
//! V1→V2 change-vector and metadata mapping (V1 change vectors, etags) is
//! owned by GAP-D02 (#59); until it lands, compare-exchange values convert
//! through the missing-key condition and no V1 raft index or change vector
//! is preserved — the dependency is recorded, not silently dropped. This
//! module is proved by focused tests through the public seam. It makes no
//! recovery, durability, or performance claim: formal Recovery Receipts
//! belong to the exclusive recovery lane.

mod codec;
mod digest;
#[cfg(test)]
mod fixture;
#[cfg(test)]
mod tests;

use std::collections::{BTreeMap, BTreeSet};
use std::path::Path;
use std::sync::Arc;

use codec::kind_of_token;
use digest::{Sha256, digest_file, to_hex};

use crate::engine::{CommitOutcome, DurableResolution, Engine, EngineError};
use crate::families::{
    AttachmentMutation, AttachmentWrite, CompareExchangeCondition, CompareExchangeWrite,
    CounterBatchWrite, CounterOperation, FamilyError, FamilyWriteRequest, IdentityCommand,
    IdentityWrite, TimeSeriesAppend, TimeSeriesBatchWrite,
};
use crate::types::{
    Cancellation, CollectionName, Deadline, Document, DocumentId, DocumentValue, ExpectedVersion,
    LocalDurability, OperationId, TenantId, TenantScope,
};

/// The representation manifest file name.
const MANIFEST_FILE: &str = "manifest.tbv1m";
/// The manifest header text; the supported format version follows it.
const HEADER: &str = "terrabase-v1-export";
/// The only supported representation format version.
const SUPPORTED_FORMAT_VERSION: u32 = 1;
/// The canonical segment-name prefix.
const SEGMENT_PREFIX: &str = "segment-";
/// The page size for durable publication-history reads.
const JOURNAL_PAGE_LIMIT: u32 = 1024;

/// One logical record kind of a V1 representation (PB-6019 selection).
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub enum MigrationKind {
    /// A document or one revision of a document.
    Document,
    /// Referenced attachment metadata with its content digest.
    Attachment,
    /// A tombstone for a deleted key.
    Tombstone,
    /// One counter value.
    Counter,
    /// One time-series append.
    TimeSeries,
    /// One identity seed value.
    Identity,
    /// One compare-exchange value.
    CompareExchange,
    /// A V1 storage-internal structure (journal, page layout, headers).
    StorageInternal,
    /// An unreferenced blob.
    OrphanBlob,
    /// A Platform-owned configuration record.
    PlatformConfig,
    /// A V1 index definition.
    IndexDefinition,
}

impl MigrationKind {
    /// Returns the loss-matrix category this kind reports under.
    #[must_use]
    pub fn category(self) -> MigrationCategory {
        match self {
            Self::Document => MigrationCategory::DocumentsAndRevisions,
            Self::Attachment => MigrationCategory::ReferencedAttachments,
            Self::Tombstone => MigrationCategory::TombstonesAndDeletedRanges,
            Self::Counter | Self::TimeSeries => MigrationCategory::CountersAndTimeSeries,
            Self::Identity | Self::CompareExchange => {
                MigrationCategory::IdentitiesAndCompareExchange
            }
            Self::StorageInternal => MigrationCategory::V1StorageInternals,
            Self::OrphanBlob => MigrationCategory::UnreferencedBlobs,
            Self::PlatformConfig => MigrationCategory::PlatformOwnedConfiguration,
            Self::IndexDefinition => MigrationCategory::IndexDefinitions,
        }
    }
}

/// One logical input record with its kind and identity (PB-6019).
///
/// The physical V1 layout never crosses this type: these are the logical
/// facts a V1-side producer emits into the representation.
#[derive(Clone, Debug, PartialEq)]
pub enum MigrationRecord {
    /// One document or one revision of a document, with the V1 version that
    /// orders revisions of the same key.
    Document {
        /// The owning tenant.
        tenant: TenantId,
        /// The collection.
        collection: CollectionName,
        /// The document identifier.
        id: DocumentId,
        /// The logical value.
        value: DocumentValue,
        /// The V1 version of this record.
        version: u64,
    },
    /// Referenced attachment metadata and content digest.
    Attachment {
        /// The owning tenant.
        tenant: TenantId,
        /// The owning document.
        document: DocumentId,
        /// The attachment name.
        name: String,
        /// The logical content type.
        content_type: String,
        /// The content size in bytes.
        size: u64,
        /// The content digest.
        hash: String,
    },
    /// A tombstone for a deleted key.
    Tombstone {
        /// The owning tenant.
        tenant: TenantId,
        /// The deleted key's collection.
        collection: CollectionName,
        /// The deleted key's document identifier.
        id: DocumentId,
        /// The version that existed at the deleted key.
        version_at_delete: u64,
    },
    /// One counter value.
    Counter {
        /// The owning tenant.
        tenant: TenantId,
        /// The counter group's owning document.
        document: DocumentId,
        /// The counter name.
        name: String,
        /// The V1 counter value.
        value: i64,
    },
    /// One time-series append.
    TimeSeries {
        /// The owning tenant.
        tenant: TenantId,
        /// The series' owning document.
        document: DocumentId,
        /// The series name.
        name: String,
        /// The append timestamp in epoch milliseconds.
        timestamp: i64,
        /// The appended values.
        values: Vec<f64>,
        /// The optional append tag.
        tag: Option<String>,
    },
    /// One identity seed value.
    Identity {
        /// The owning tenant.
        tenant: TenantId,
        /// The identity name.
        name: String,
        /// The seeded next value.
        value: u64,
    },
    /// One compare-exchange value.
    CompareExchange {
        /// The owning tenant.
        tenant: TenantId,
        /// The compare-exchange key.
        key: String,
        /// The stored value.
        value: DocumentValue,
    },
    /// A V1 storage-internal structure, named without any of its bytes.
    StorageInternal {
        /// A logical name of the structure kind.
        kind: String,
    },
    /// An unreferenced blob, named by digest only.
    OrphanBlob {
        /// The blob digest.
        hash: String,
    },
    /// A Platform-owned configuration record, named without its contents.
    PlatformConfig {
        /// A logical name of the configuration kind.
        kind: String,
    },
    /// A V1 index definition, named without its contents.
    IndexDefinition {
        /// The index name.
        name: String,
    },
}

impl MigrationRecord {
    /// Returns the record's kind.
    #[must_use]
    pub fn kind(&self) -> MigrationKind {
        match self {
            Self::Document { .. } => MigrationKind::Document,
            Self::Attachment { .. } => MigrationKind::Attachment,
            Self::Tombstone { .. } => MigrationKind::Tombstone,
            Self::Counter { .. } => MigrationKind::Counter,
            Self::TimeSeries { .. } => MigrationKind::TimeSeries,
            Self::Identity { .. } => MigrationKind::Identity,
            Self::CompareExchange { .. } => MigrationKind::CompareExchange,
            Self::StorageInternal { .. } => MigrationKind::StorageInternal,
            Self::OrphanBlob { .. } => MigrationKind::OrphanBlob,
            Self::PlatformConfig { .. } => MigrationKind::PlatformConfig,
            Self::IndexDefinition { .. } => MigrationKind::IndexDefinition,
        }
    }
}

/// One bound representation segment: ordered name, exact length, and
/// SHA-256 digest.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct MigrationSegment {
    name: String,
    length: u64,
    digest: String,
}

impl MigrationSegment {
    /// Creates one segment fact from validated parts.
    pub(crate) fn new(name: String, length: u64, digest: String) -> Self {
        Self { name, length, digest }
    }

    /// Returns the segment's canonical name.
    #[must_use]
    pub fn name(&self) -> &str {
        &self.name
    }

    /// Returns the segment's exact byte length.
    #[must_use]
    pub fn length(&self) -> u64 {
        self.length
    }

    /// Returns the segment's SHA-256 digest as lowercase hex.
    #[must_use]
    pub fn digest(&self) -> &str {
        &self.digest
    }
}

/// The verified identity of one V1 representation (PB-6022).
///
/// Binds the format version, the record count of every kind, and every
/// segment's name, length, and digest. The identity is content-derived —
/// the SHA-256 of all manifest lines except the identity line — and the
/// manifest is validated strictly before any write.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct MigrationManifest {
    format_version: u32,
    identity: String,
    counts: BTreeMap<MigrationKind, u64>,
    members: Vec<MigrationSegment>,
}

impl MigrationManifest {
    /// Loads and strictly validates the representation manifest of
    /// `source`.
    ///
    /// # Errors
    ///
    /// Returns [`MigrationError::UnsupportedVersion`] when the header names
    /// a format version other than the supported one, and
    /// [`MigrationError::ManifestInvalid`] when the manifest is absent,
    /// malformed, unordered, carries an unknown kind or member name, or its
    /// identity does not bind its content.
    pub fn load(source: &Path) -> Result<Self, MigrationError> {
        let text = std::fs::read_to_string(source.join(MANIFEST_FILE))
            .map_err(|_| MigrationError::ManifestInvalid)?;
        Self::parse(&text)
    }

    /// Strictly parses one manifest text.
    ///
    /// # Errors
    ///
    /// As [`MigrationManifest::load`].
    pub fn parse(text: &str) -> Result<Self, MigrationError> {
        let mut lines = text.lines();
        let header = lines.next().ok_or(MigrationError::ManifestInvalid)?;
        let mut header_parts = header.split(' ');
        if header_parts.next() != Some(HEADER) {
            return Err(MigrationError::ManifestInvalid);
        }
        let version = header_parts
            .next()
            .and_then(|value| value.parse::<u32>().ok())
            .ok_or(MigrationError::ManifestInvalid)?;
        if header_parts.next().is_some() {
            return Err(MigrationError::ManifestInvalid);
        }
        if version != SUPPORTED_FORMAT_VERSION {
            return Err(MigrationError::UnsupportedVersion);
        }
        let identity_line = lines.next().ok_or(MigrationError::ManifestInvalid)?;
        let identity =
            identity_line.strip_prefix("identity ").ok_or(MigrationError::ManifestInvalid)?;
        validate_hex_identity(identity)?;

        let mut counts = BTreeMap::new();
        let mut members = Vec::new();
        let mut phase = Phase::Counts;
        for line in lines {
            if line.is_empty() {
                return Err(MigrationError::ManifestInvalid);
            }
            if phase == Phase::Counts && line.starts_with("count ") {
                let rest = line.strip_prefix("count ").ok_or(MigrationError::ManifestInvalid)?;
                let (token, count) = rest.split_once(' ').ok_or(MigrationError::ManifestInvalid)?;
                let kind = kind_of_token(token).ok_or(MigrationError::ManifestInvalid)?;
                let count = count.parse::<u64>().map_err(|_| MigrationError::ManifestInvalid)?;
                if counts.insert(kind, count).is_some() {
                    return Err(MigrationError::ManifestInvalid);
                }
                continue;
            }
            phase = Phase::Members;
            if !line.starts_with("member ") {
                return Err(MigrationError::ManifestInvalid);
            }
            let rest = line.strip_prefix("member ").ok_or(MigrationError::ManifestInvalid)?;
            let mut parts = rest.split(' ');
            let name = parts.next().ok_or(MigrationError::ManifestInvalid)?;
            let length = parts
                .next()
                .and_then(|value| value.parse::<u64>().ok())
                .ok_or(MigrationError::ManifestInvalid)?;
            let digest = parts.next().ok_or(MigrationError::ManifestInvalid)?;
            if parts.next().is_some() {
                return Err(MigrationError::ManifestInvalid);
            }
            validate_segment_name(name)?;
            validate_hex_digest(digest)?;
            members.push(MigrationSegment::new(name.to_owned(), length, digest.to_owned()));
        }
        if members.is_empty() && !counts.values().all(|count| *count == 0) {
            return Err(MigrationError::ManifestInvalid);
        }
        let computed =
            identity_of_lines(text.lines().filter(|line| !line.starts_with("identity ")));
        if computed != identity {
            return Err(MigrationError::ManifestInvalid);
        }
        Ok(Self { format_version: version, identity: identity.to_owned(), counts, members })
    }

    /// Returns the representation's format version.
    #[must_use]
    pub fn format_version(&self) -> u32 {
        self.format_version
    }

    /// Returns the content-derived representation identity.
    #[must_use]
    pub fn identity(&self) -> &str {
        &self.identity
    }

    /// Returns the bound record count of one kind (zero when unbound).
    #[must_use]
    pub fn count(&self, kind: MigrationKind) -> u64 {
        self.counts.get(&kind).copied().unwrap_or(0)
    }

    /// Returns every bound record count, in kind order.
    #[must_use]
    pub fn counts(&self) -> &BTreeMap<MigrationKind, u64> {
        &self.counts
    }

    /// Returns the ordered segment bindings.
    #[must_use]
    pub fn members(&self) -> &[MigrationSegment] {
        &self.members
    }

    /// Verifies every bound segment in `source` against its length and
    /// digest.
    ///
    /// # Errors
    ///
    /// Returns [`MigrationError::ManifestInvalid`] when a segment is
    /// missing, its length differs, or its SHA-256 digest does not match.
    pub fn verify(&self, source: &Path) -> Result<(), MigrationError> {
        for member in &self.members {
            let path = source.join(member.name());
            let metadata = std::fs::metadata(&path).map_err(|_| MigrationError::ManifestInvalid)?;
            if metadata.len() != member.length() {
                return Err(MigrationError::ManifestInvalid);
            }
            let digest = digest_file(&path).map_err(|_| MigrationError::ManifestInvalid)?;
            if digest != member.digest() {
                return Err(MigrationError::ManifestInvalid);
            }
        }
        Ok(())
    }
}

/// The manifest block order.
#[derive(Clone, Copy, Eq, PartialEq)]
enum Phase {
    Counts,
    Members,
}

/// Migration request options: kind selection, tombstone policy, revision
/// policy, cancellation, and a progress observer (PB-6019, PB-6021).
///
/// Every policy is stated here, never decided silently: unselected kinds
/// and dropped tombstones appear in the loss matrix with counts and
/// reasons.
#[derive(Clone, Default)]
pub struct MigrationOptions {
    selection: Option<BTreeSet<MigrationKind>>,
    tombstone_policy: Option<MigrationTombstonePolicy>,
    revision_policy: Option<MigrationRevisionPolicy>,
    cancellation: Option<Arc<Cancellation>>,
    progress: Option<Arc<dyn Fn(u64) + Send + Sync>>,
}

impl MigrationOptions {
    /// Options with every default: all kinds selected, tombstones
    /// converted, every revision converted, no cancellation, no observer.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Selects only document records.
    #[must_use]
    pub fn only_documents(mut self) -> Self {
        self.selection = Some(BTreeSet::from([MigrationKind::Document]));
        self
    }

    /// Selects exactly the given kinds; unselected kinds convert to
    /// disclosed drops.
    #[must_use]
    pub fn with_selection(mut self, kinds: impl IntoIterator<Item = MigrationKind>) -> Self {
        self.selection = Some(kinds.into_iter().collect());
        self
    }

    /// States the tombstone policy.
    #[must_use]
    pub fn with_tombstone_policy(mut self, policy: MigrationTombstonePolicy) -> Self {
        self.tombstone_policy = Some(policy);
        self
    }

    /// States the revision policy.
    #[must_use]
    pub fn with_revision_policy(mut self, policy: MigrationRevisionPolicy) -> Self {
        self.revision_policy = Some(policy);
        self
    }

    /// Arms a shareable cancellation signal, checked per record.
    #[must_use]
    pub fn with_cancellation(mut self, cancellation: Arc<Cancellation>) -> Self {
        self.cancellation = Some(cancellation);
        self
    }

    /// Observes the run's applied-record count after every durable
    /// application.
    #[must_use]
    pub fn with_progress_observer(mut self, progress: Arc<dyn Fn(u64) + Send + Sync>) -> Self {
        self.progress = Some(progress);
        self
    }

    fn tombstone_policy(&self) -> MigrationTombstonePolicy {
        self.tombstone_policy.unwrap_or(MigrationTombstonePolicy::Convert)
    }

    fn revision_policy(&self) -> MigrationRevisionPolicy {
        self.revision_policy.unwrap_or(MigrationRevisionPolicy::AllVersions)
    }

    fn selects(&self, kind: MigrationKind) -> bool {
        self.selection.as_ref().is_none_or(|selected| selected.contains(&kind))
    }
}

/// Whether tombstones convert as engine deletes or drop with a recorded
/// count.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum MigrationTombstonePolicy {
    /// Convert every tombstone through the Engine transaction seam.
    Convert,
    /// Drop every tombstone, disclosed in the loss matrix with a count.
    DropRecorded,
}

/// Whether every V1 revision converts or only the newest version of each
/// key.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum MigrationRevisionPolicy {
    /// Convert every revision in order; each becomes one V2 version.
    AllVersions,
    /// Convert only the newest version of each key; collapsed revisions
    /// are disclosed in the report.
    LatestOnly,
}

/// The loss-matrix categories of the transformation contract.
#[derive(Clone, Copy, Debug, Eq, Ord, PartialEq, PartialOrd)]
pub enum MigrationCategory {
    /// Documents and revisions.
    DocumentsAndRevisions,
    /// Referenced attachments.
    ReferencedAttachments,
    /// Tombstones and deleted ranges.
    TombstonesAndDeletedRanges,
    /// Counters and time-series values.
    CountersAndTimeSeries,
    /// Identities and compare-exchange values.
    IdentitiesAndCompareExchange,
    /// V1 storage-internal structures.
    V1StorageInternals,
    /// Unreferenced blobs.
    UnreferencedBlobs,
    /// Platform-owned configuration.
    PlatformOwnedConfiguration,
    /// V1 index definitions.
    IndexDefinitions,
}

/// Every category's fixed order, as the loss matrix reports them.
const CATEGORIES: [MigrationCategory; 9] = [
    MigrationCategory::DocumentsAndRevisions,
    MigrationCategory::ReferencedAttachments,
    MigrationCategory::TombstonesAndDeletedRanges,
    MigrationCategory::CountersAndTimeSeries,
    MigrationCategory::IdentitiesAndCompareExchange,
    MigrationCategory::V1StorageInternals,
    MigrationCategory::UnreferencedBlobs,
    MigrationCategory::PlatformOwnedConfiguration,
    MigrationCategory::IndexDefinitions,
];

/// One row of the loss matrix: a category's converted and dropped counts
/// and the reason of every drop.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct MigrationLossRow {
    category: MigrationCategory,
    converted: u64,
    dropped: u64,
    reasons: Vec<&'static str>,
}

impl MigrationLossRow {
    /// Returns the row's category.
    #[must_use]
    pub fn category(&self) -> MigrationCategory {
        self.category
    }

    /// Returns the category's converted count.
    #[must_use]
    pub fn converted(&self) -> u64 {
        self.converted
    }

    /// Returns the category's dropped count.
    #[must_use]
    pub fn dropped(&self) -> u64 {
        self.dropped
    }

    /// Returns the first recorded drop reason, if any drop happened.
    #[must_use]
    pub fn reason(&self) -> Option<&'static str> {
        self.reasons.first().copied()
    }

    /// Returns every recorded drop reason.
    #[must_use]
    pub fn reasons(&self) -> &[&'static str] {
        &self.reasons
    }
}

/// The explicit converted/dropped table of one migration run.
///
/// Every contract category appears exactly once; every dropped category
/// carries a count and a reason. Nothing is dropped silently.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct MigrationLossMatrix {
    rows: Vec<MigrationLossRow>,
}

impl MigrationLossMatrix {
    /// Returns the rows in contract category order.
    #[must_use]
    pub fn rows(&self) -> &[MigrationLossRow] {
        &self.rows
    }

    /// Returns one category's row.
    #[must_use]
    pub fn row(&self, category: MigrationCategory) -> Option<&MigrationLossRow> {
        self.rows.iter().find(|row| row.category() == category)
    }
}

/// The verification verdicts of one migration run.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct MigrationVerification {
    segment_digests_verified: bool,
    counts_reconciled: bool,
}

impl MigrationVerification {
    /// Returns whether every bound segment verified its length and digest.
    #[must_use]
    pub fn segment_digests_verified(&self) -> bool {
        self.segment_digests_verified
    }

    /// Returns whether every decoded kind reconciled with the manifest
    /// counts.
    #[must_use]
    pub fn counts_reconciled(&self) -> bool {
        self.counts_reconciled
    }

    /// Returns whether every verdict is green; success is never reported
    /// otherwise.
    #[must_use]
    pub fn all_verdicts(&self) -> bool {
        self.segment_digests_verified && self.counts_reconciled
    }
}

/// The terminal report of one migration run.
///
/// Carries the converted counts per kind, the duplicate count, the loss
/// matrix, the resume position, and the verification verdicts. Success
/// never means "everything in V1 arrived" — the matrix is the disclosure.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct MigrationReport {
    identity: String,
    format_version: u32,
    converted: BTreeMap<MigrationKind, u64>,
    duplicates_by_kind: BTreeMap<MigrationKind, u64>,
    collapsed_revisions: u64,
    loss: MigrationLossMatrix,
    resume_position: u64,
    verification: MigrationVerification,
}

impl MigrationReport {
    /// Returns the representation identity this run consumed.
    #[must_use]
    pub fn identity(&self) -> &str {
        &self.identity
    }

    /// Returns the representation's format version.
    #[must_use]
    pub fn format_version(&self) -> u32 {
        self.format_version
    }

    /// Returns the converted count of one kind.
    #[must_use]
    pub fn converted(&self, kind: MigrationKind) -> u64 {
        self.converted.get(&kind).copied().unwrap_or(0)
    }

    /// Returns the converted document-record count.
    #[must_use]
    pub fn converted_documents(&self) -> u64 {
        self.converted(MigrationKind::Document)
    }

    /// Returns the number of records this run resolved as already durably
    /// applied by an earlier run of the same input.
    #[must_use]
    pub fn duplicates(&self) -> u64 {
        self.duplicates_by_kind.values().copied().sum()
    }

    /// Returns the number of revisions collapsed by
    /// [`MigrationRevisionPolicy::LatestOnly`].
    #[must_use]
    pub fn collapsed_revisions(&self) -> u64 {
        self.collapsed_revisions
    }

    /// Returns the run's loss matrix.
    #[must_use]
    pub fn loss_matrix(&self) -> &MigrationLossMatrix {
        &self.loss
    }

    /// Returns the record ordinal a resumed run continues from.
    #[must_use]
    pub fn resume_position(&self) -> u64 {
        self.resume_position
    }

    /// Returns the run's verification verdicts.
    #[must_use]
    pub fn verification(&self) -> MigrationVerification {
        self.verification
    }
}

/// Typed Logical Migration failure categories.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum MigrationError {
    /// The representation manifest failed validation — including segment
    /// digest verification and count reconciliation — before any write.
    ManifestInvalid,
    /// An input record is structurally corrupt: an unknown tag, a frame
    /// that does not consume exactly its envelope, or an order violation.
    RecordCorrupt,
    /// An input record's frame ends inside the segment.
    RecordTruncated,
    /// The representation's format version is unsupported.
    UnsupportedVersion,
    /// A record's deterministic operation is recorded but undecided (or
    /// durably rejected), so the run cannot resume safely.
    NotResumable,
    /// The target rejected a conversion — a family rule, a commit
    /// rejection, or a closed Engine.
    TargetIncompatible,
    /// The operation was cancelled; the durable prefix remains resumable.
    Cancelled,
}

/// Runs one Logical Migration of the representation in `source` into
/// `target` under the backend-supplied `scope` (PB-6022, PB-6023).
///
/// The scope is the authorized tenant context the Platform constructs;
/// tenant isolation stays engine-enforced, and a record naming any tenant
/// other than the scope's tenant fails closed as
/// [`MigrationError::TargetIncompatible`] — it is never silently routed
/// into another tenant.
///
/// The manifest is loaded and strictly validated and every segment digest
/// is verified before any write. Records then convert under the
/// transformation contract with deterministic operation identities, so the
/// run is restartable and idempotent. Success carries the loss matrix and
/// the verification verdicts.
///
/// # Errors
///
/// Returns the typed failure of the first fail-closed cause; no partial
/// conversion is ever reported as success.
pub fn run_migration(
    target: &Engine,
    scope: &TenantScope,
    source: &Path,
    options: &MigrationOptions,
) -> Result<MigrationReport, MigrationError> {
    let manifest = MigrationManifest::load(source)?;
    manifest.verify(source)?;
    let prefix = operation_prefix(manifest.identity());
    let applied = applied_ordinals(target, scope, &prefix)?;
    let mut run = Run::new(manifest.clone(), scope.clone(), options, applied);
    for member in manifest.members() {
        let bytes = std::fs::read(source.join(member.name()))
            .map_err(|_| MigrationError::ManifestInvalid)?;
        for record in codec::decode_segment(&bytes)? {
            run.record(target, record)?;
        }
    }
    run.finish(target)
}

/// The deterministic operation-identity prefix of one representation.
fn operation_prefix(identity: &str) -> String {
    format!("mig-{}-", identity.chars().take(12).collect::<String>())
}

/// Collects the record ordinals this representation already durably applied
/// to the scope's tenant, from the durable publication history.
///
/// Resume is derived from the same source backup reads: the journal's
/// published commits. When the servable history no longer reaches back over
/// the run's operations, a missed duplicate is re-applied under its
/// identical operation identity and write set — the engine's operation
/// ledger replays it without a second write, so the data stays idempotent
/// and only the duplicate accounting loses coverage, which the module
/// documentation records as a limit.
fn applied_ordinals(
    target: &Engine,
    scope: &TenantScope,
    prefix: &str,
) -> Result<BTreeSet<u64>, MigrationError> {
    let mut after = crate::types::StorageSequence::default();
    let mut found = BTreeSet::new();
    loop {
        let page = target
            .tenant_commits(scope.tenant(), after, JOURNAL_PAGE_LIMIT)
            .map_err(|_| MigrationError::TargetIncompatible)?;
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
        after = page.last().map_or(after, crate::storage::PublishedCommit::sequence);
    }
    Ok(found)
}

/// The document identity a `LatestOnly` deferral tracks.
type DocumentKey = (String, String, String);

/// The mutable state of one executing migration run.
struct Run {
    manifest: MigrationManifest,
    scope: TenantScope,
    options: MigrationOptions,
    converted: BTreeMap<MigrationKind, u64>,
    duplicates: BTreeMap<MigrationKind, u64>,
    dropped: BTreeMap<MigrationKind, u64>,
    reasons: BTreeMap<MigrationKind, Vec<&'static str>>,
    collapsed_revisions: u64,
    seen: BTreeMap<MigrationKind, u64>,
    ordinal: u64,
    applied: u64,
    resume_position: u64,
    /// The record ordinals an earlier run of the same representation
    /// already durably applied.
    already_applied: BTreeSet<u64>,
    /// The deferred newest document record of the current key under the
    /// `LatestOnly` policy: its key, its record ordinal, and the record.
    deferred: Option<(DocumentKey, u64, MigrationRecord)>,
}

impl Run {
    fn new(
        manifest: MigrationManifest,
        scope: TenantScope,
        options: &MigrationOptions,
        already_applied: BTreeSet<u64>,
    ) -> Self {
        Self {
            manifest,
            scope,
            options: options.clone(),
            converted: BTreeMap::new(),
            duplicates: BTreeMap::new(),
            dropped: BTreeMap::new(),
            reasons: BTreeMap::new(),
            collapsed_revisions: 0,
            seen: BTreeMap::new(),
            ordinal: 0,
            applied: 0,
            resume_position: 0,
            already_applied,
            deferred: None,
        }
    }

    /// Observes one decoded record.
    fn record(&mut self, target: &Engine, record: MigrationRecord) -> Result<(), MigrationError> {
        let kind = record.kind();
        *self.seen.entry(kind).or_default() += 1;
        self.check_cancellation()?;
        if !self.options.selects(kind) {
            self.drop(kind, "unselected by the migration options");
            return Ok(());
        }
        match kind {
            MigrationKind::Document => self.document(target, record),
            MigrationKind::Tombstone => match self.options.tombstone_policy() {
                MigrationTombstonePolicy::Convert => self.apply(target, &record),
                MigrationTombstonePolicy::DropRecorded => {
                    self.drop(kind, "tombstones dropped by the stated tombstone policy");
                    Ok(())
                }
            },
            MigrationKind::StorageInternal => {
                self.drop(kind, "physical V1 layout is not a V2 Storage Format concept");
                Ok(())
            }
            MigrationKind::OrphanBlob => {
                self.drop(kind, "no logical referent exists in the representation");
                Ok(())
            }
            MigrationKind::PlatformConfig => {
                self.drop(kind, "Platform-owned control-plane record; the engine does not import authorization or secrets");
                Ok(())
            }
            MigrationKind::IndexDefinition => {
                self.drop(kind, "V2 indexes publish from storage publication and Auto-Index selection; V1 index binaries are not portable");
                Ok(())
            }
            MigrationKind::Attachment
            | MigrationKind::Counter
            | MigrationKind::TimeSeries
            | MigrationKind::Identity
            | MigrationKind::CompareExchange => self.apply(target, &record),
        }
    }

    /// Applies the `LatestOnly` deferral to document records.
    fn document(&mut self, target: &Engine, record: MigrationRecord) -> Result<(), MigrationError> {
        let MigrationRecord::Document { tenant, collection, id, .. } = &record else {
            unreachable!("document() receives document records only");
        };
        let key =
            (tenant.as_str().to_owned(), collection.as_str().to_owned(), id.as_str().to_owned());
        if self.options.revision_policy() == MigrationRevisionPolicy::LatestOnly {
            if let Some(deferred) = self.deferred.take() {
                if deferred.0 == key {
                    self.collapsed_revisions += 1;
                } else {
                    self.apply_at(target, deferred)?;
                }
            }
            self.deferred = Some((key, self.ordinal, record));
            self.ordinal += 1;
            return Ok(());
        }
        self.apply(target, &record)
    }

    /// Applies one deferred record under its original record ordinal.
    fn apply_at(
        &mut self,
        target: &Engine,
        deferred: (DocumentKey, u64, MigrationRecord),
    ) -> Result<(), MigrationError> {
        let (_, ordinal, record) = deferred;
        let current = self.ordinal;
        self.ordinal = ordinal;
        let applied = self.apply(target, &record);
        self.ordinal = current;
        applied
    }

    /// Applies one record under its deterministic operation identity.
    fn apply(&mut self, target: &Engine, record: &MigrationRecord) -> Result<(), MigrationError> {
        let kind = record.kind();
        let ordinal = self.ordinal;
        self.ordinal += 1;
        let scope = self.record_scope(record)?;
        let operation = deterministic_operation(self.manifest.identity(), ordinal);
        if self.already_applied.contains(&ordinal) {
            *self.duplicates.entry(kind).or_default() += 1;
            return Ok(());
        }
        self.check_cancellation()?;
        let commit = Self::stage_and_commit(target, &scope, operation.clone(), record);
        match commit {
            Application::Applied => {
                *self.converted.entry(kind).or_default() += 1;
                self.applied += 1;
                self.resume_position = self.resume_position.max(ordinal + 1);
                if let Some(observer) = &self.options.progress {
                    observer(self.applied);
                }
                Ok(())
            }
            Application::Duplicate => {
                *self.duplicates.entry(kind).or_default() += 1;
                Ok(())
            }
            Application::Failed(other) => Err(other),
        }
    }

    /// Stages and commits one record's conversion through the engine or
    /// family seam.
    fn stage_and_commit(
        target: &Engine,
        scope: &TenantScope,
        operation: OperationId,
        record: &MigrationRecord,
    ) -> Application {
        let Ok(snapshot) = target.read_snapshot(scope) else {
            return Application::Failed(MigrationError::TargetIncompatible);
        };
        let mut transaction = match target.begin(scope, snapshot, operation.clone()) {
            Ok(transaction) => transaction,
            Err(EngineError::OperationIdReuse { .. }) => {
                // The identity already resolved durably; treat as a
                // duplicate application.
                return Application::Duplicate;
            }
            Err(_) => return Application::Failed(MigrationError::TargetIncompatible),
        };
        let staged: Result<(), MigrationError> =
            stage_write(target, &mut transaction, scope, &operation, record);
        if let Err(error) = staged {
            return Application::Failed(error);
        }
        match transaction.commit(LocalDurability::Synced) {
            CommitOutcome::Committed(_) => Application::Applied,
            CommitOutcome::Rejected(_) => Application::Failed(MigrationError::TargetIncompatible),
            CommitOutcome::Unknown(_) => match target.resolve_commit(scope, operation) {
                Ok(DurableResolution::Committed(_)) => Application::Applied,
                Ok(_) => Application::Failed(MigrationError::NotResumable),
                Err(_) => Application::Failed(MigrationError::TargetIncompatible),
            },
        }
    }

    /// Flushes the deferred `LatestOnly` record and reconciles the run.
    fn finish(&mut self, target: &Engine) -> Result<MigrationReport, MigrationError> {
        if let Some(deferred) = self.deferred.take() {
            self.apply_at(target, deferred)?;
        }
        for kind in self.seen.keys().copied().collect::<Vec<_>>() {
            let seen = self.seen.get(&kind).copied().unwrap_or(0);
            let bound = self.manifest.count(kind);
            let accounted = self.converted.get(&kind).copied().unwrap_or(0)
                + self.duplicates.get(&kind).copied().unwrap_or(0)
                + self.dropped.get(&kind).copied().unwrap_or(0)
                + u64::from(kind == MigrationKind::Document) * self.collapsed_revisions;
            if seen != bound || accounted != seen {
                return Err(MigrationError::ManifestInvalid);
            }
        }
        // Kinds the manifest bound but the segments never carried are a
        // lying manifest.
        for (kind, bound) in self.manifest.counts() {
            if *bound > 0 && !self.seen.contains_key(kind) {
                return Err(MigrationError::ManifestInvalid);
            }
        }
        let rows = CATEGORIES
            .into_iter()
            .map(|category| {
                let mut converted = 0u64;
                let mut dropped = 0u64;
                let mut reasons: Vec<&'static str> = Vec::new();
                for kind in kinds_of_category(category) {
                    converted += self.converted.get(kind).copied().unwrap_or(0);
                    dropped += self.dropped.get(kind).copied().unwrap_or(0);
                    if let Some(kind_reasons) = self.reasons.get(kind) {
                        for reason in kind_reasons.clone() {
                            if !reasons.contains(&reason) {
                                reasons.push(reason);
                            }
                        }
                    }
                }
                if category == MigrationCategory::ReferencedAttachments && converted > 0 {
                    reasons.push("attachment metadata and content digest converted; content bytes do not travel in representation format version 1");
                }
                MigrationLossRow { category, converted, dropped, reasons }
            })
            .collect();
        Ok(MigrationReport {
            identity: self.manifest.identity().to_owned(),
            format_version: self.manifest.format_version(),
            converted: self.converted.clone(),
            duplicates_by_kind: self.duplicates.clone(),
            collapsed_revisions: self.collapsed_revisions,
            loss: MigrationLossMatrix { rows },
            resume_position: self.resume_position,
            verification: MigrationVerification {
                segment_digests_verified: true,
                counts_reconciled: true,
            },
        })
    }

    fn drop(&mut self, kind: MigrationKind, reason: &'static str) {
        *self.dropped.entry(kind).or_default() += 1;
        let reasons = self.reasons.entry(kind).or_default();
        if !reasons.contains(&reason) {
            reasons.push(reason);
        }
    }

    fn check_cancellation(&self) -> Result<(), MigrationError> {
        if self.options.cancellation.as_ref().is_some_and(|token| token.is_cancelled()) {
            return Err(MigrationError::Cancelled);
        }
        Ok(())
    }

    /// Returns the run's scope when the record names the scope's tenant.
    ///
    /// Tenant isolation is engine-enforced from backend-supplied context:
    /// a record of any other tenant fails closed instead of being routed
    /// into the authorized tenant's data.
    fn record_scope(&self, record: &MigrationRecord) -> Result<TenantScope, MigrationError> {
        let tenant = match record {
            MigrationRecord::Document { tenant, .. }
            | MigrationRecord::Attachment { tenant, .. }
            | MigrationRecord::Tombstone { tenant, .. }
            | MigrationRecord::Counter { tenant, .. }
            | MigrationRecord::TimeSeries { tenant, .. }
            | MigrationRecord::Identity { tenant, .. }
            | MigrationRecord::CompareExchange { tenant, .. } => tenant,
            // Dropped kinds never reach the engine seam: `record` routes
            // them to `drop` before `apply` runs.
            MigrationRecord::StorageInternal { .. }
            | MigrationRecord::OrphanBlob { .. }
            | MigrationRecord::PlatformConfig { .. }
            | MigrationRecord::IndexDefinition { .. } => {
                unreachable!("dropped kinds never reach the engine seam")
            }
        };
        if tenant == self.scope.tenant() {
            return Ok(self.scope.clone());
        }
        Err(MigrationError::TargetIncompatible)
    }
}

/// Stages one record's write into a live transaction: document puts and
/// tombstone deletes directly, family records through the family store's
/// typed write requests.
fn stage_write<'engine>(
    target: &'engine Engine,
    transaction: &mut crate::engine::Transaction<'engine>,
    scope: &TenantScope,
    operation: &OperationId,
    record: &MigrationRecord,
) -> Result<(), MigrationError> {
    match record {
        MigrationRecord::Document { id, collection, value, .. } => transaction
            .put(Document::new(id.clone(), collection.clone(), value.clone()), ExpectedVersion::Any)
            .map_err(|_| MigrationError::TargetIncompatible),
        MigrationRecord::Tombstone { id, .. } => transaction
            .delete(id.clone(), ExpectedVersion::Any)
            .map_err(|_| MigrationError::TargetIncompatible),
        MigrationRecord::Attachment { .. }
        | MigrationRecord::Counter { .. }
        | MigrationRecord::TimeSeries { .. }
        | MigrationRecord::Identity { .. }
        | MigrationRecord::CompareExchange { .. } => {
            let request = family_write(scope, operation, record)?;
            target.families().stage(transaction, request).map_err(family_failure)
        }
        MigrationRecord::StorageInternal { .. }
        | MigrationRecord::OrphanBlob { .. }
        | MigrationRecord::PlatformConfig { .. }
        | MigrationRecord::IndexDefinition { .. } => Ok(()),
    }
}

/// Builds the typed family write request of one family record.
fn family_write(
    scope: &TenantScope,
    operation: &OperationId,
    record: &MigrationRecord,
) -> Result<FamilyWriteRequest, MigrationError> {
    match record {
        MigrationRecord::Attachment { document, name, content_type, size, hash, .. } => {
            let write = AttachmentWrite::try_new(
                scope.clone(),
                document.clone(),
                AttachmentMutation::Store {
                    name: name.clone(),
                    content_type: content_type.clone(),
                    size: *size,
                    hash: hash.clone(),
                    replace: true,
                },
                ExpectedVersion::Any,
                operation.clone(),
                long_deadline(),
                Cancellation::new(),
            )
            .map_err(|_| MigrationError::TargetIncompatible)?;
            Ok(FamilyWriteRequest::attachment_mutation(write))
        }
        MigrationRecord::Counter { document, name, value, .. } => {
            let write = CounterBatchWrite::try_new(
                scope.clone(),
                document.clone(),
                vec![CounterOperation::Increment { name: name.clone(), delta: *value }],
                ExpectedVersion::Any,
                operation.clone(),
                long_deadline(),
                Cancellation::new(),
            )
            .map_err(|_| MigrationError::TargetIncompatible)?;
            Ok(FamilyWriteRequest::counter_batch(write))
        }
        MigrationRecord::TimeSeries { document, name, timestamp, values, tag, .. } => {
            let write = TimeSeriesBatchWrite::try_new(
                scope.clone(),
                document.clone(),
                name.clone(),
                vec![TimeSeriesAppend::new(*timestamp, values.clone(), tag.clone())],
                Vec::new(),
                None,
                ExpectedVersion::Any,
                operation.clone(),
                long_deadline(),
                Cancellation::new(),
            )
            .map_err(|_| MigrationError::TargetIncompatible)?;
            Ok(FamilyWriteRequest::time_series_batch(write))
        }
        MigrationRecord::Identity { name, value, .. } => {
            let command = IdentityCommand::Seed { name: name.clone(), value: *value, force: false };
            let write = IdentityWrite::try_new(
                scope.clone(),
                name.clone(),
                command,
                operation.clone(),
                long_deadline(),
                Cancellation::new(),
            )
            .map_err(|_| MigrationError::TargetIncompatible)?;
            Ok(FamilyWriteRequest::identity_allocation(write))
        }
        MigrationRecord::CompareExchange { key, value, .. } => {
            let write = CompareExchangeWrite::try_new(
                scope.clone(),
                key.clone(),
                CompareExchangeCondition::PutIfMissing { value: value.clone(), expires_at: None },
                operation.clone(),
                long_deadline(),
                Cancellation::new(),
            )
            .map_err(|_| MigrationError::TargetIncompatible)?;
            Ok(FamilyWriteRequest::compare_exchange_condition(write))
        }
        MigrationRecord::Document { .. }
        | MigrationRecord::Tombstone { .. }
        | MigrationRecord::StorageInternal { .. }
        | MigrationRecord::OrphanBlob { .. }
        | MigrationRecord::PlatformConfig { .. }
        | MigrationRecord::IndexDefinition { .. } => {
            unreachable!("only family records build family writes")
        }
    }
}

/// The outcome of one staged application.
enum Application {
    Applied,
    Duplicate,
    Failed(MigrationError),
}

/// Maps a family-seam rejection to the migration's typed failure.
///
/// Every family rule a conversion can break — a missing owner document, a
/// counter overflow, a stale compare-exchange index, a duplicate
/// attachment name — is a target incompatibility of this input and target
/// pair: the record itself is valid, the target refuses it.
fn family_failure(_error: FamilyError) -> MigrationError {
    MigrationError::TargetIncompatible
}

/// Returns the kinds reporting under one loss-matrix category.
fn kinds_of_category(category: MigrationCategory) -> &'static [MigrationKind] {
    match category {
        MigrationCategory::DocumentsAndRevisions => &[MigrationKind::Document],
        MigrationCategory::ReferencedAttachments => &[MigrationKind::Attachment],
        MigrationCategory::TombstonesAndDeletedRanges => &[MigrationKind::Tombstone],
        MigrationCategory::CountersAndTimeSeries => {
            &[MigrationKind::Counter, MigrationKind::TimeSeries]
        }
        MigrationCategory::IdentitiesAndCompareExchange => {
            &[MigrationKind::Identity, MigrationKind::CompareExchange]
        }
        MigrationCategory::V1StorageInternals => &[MigrationKind::StorageInternal],
        MigrationCategory::UnreferencedBlobs => &[MigrationKind::OrphanBlob],
        MigrationCategory::PlatformOwnedConfiguration => &[MigrationKind::PlatformConfig],
        MigrationCategory::IndexDefinitions => &[MigrationKind::IndexDefinition],
    }
}

/// Mints the deterministic operation identity of one record ordinal.
fn deterministic_operation(identity: &str, ordinal: u64) -> OperationId {
    let prefix: String = identity.chars().take(12).collect();
    OperationId::try_new(&format!("mig-{prefix}-{ordinal:09}"))
        .expect("the derived identity is a valid operation identifier")
}

/// The family-request deadline every conversion uses.
fn long_deadline() -> Deadline {
    Deadline::after(std::time::Duration::from_secs(3600)).expect("a positive duration")
}

/// Validates a 64-character lowercase hex identity.
fn validate_hex_identity(value: &str) -> Result<(), MigrationError> {
    if value.len() == 64
        && value.bytes().all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
    {
        return Ok(());
    }
    Err(MigrationError::ManifestInvalid)
}

/// Validates a 64-character lowercase hex digest.
fn validate_hex_digest(value: &str) -> Result<(), MigrationError> {
    validate_hex_identity(value)
}

/// Validates one canonical segment name (`segment-` + six digits).
fn validate_segment_name(name: &str) -> Result<(), MigrationError> {
    let digits = name.strip_prefix(SEGMENT_PREFIX).ok_or(MigrationError::ManifestInvalid)?;
    if digits.len() == 6 && digits.bytes().all(|byte| byte.is_ascii_digit()) {
        return Ok(());
    }
    Err(MigrationError::ManifestInvalid)
}

/// Computes the content-derived identity of the non-identity manifest
/// lines.
fn identity_of_lines<'a>(lines: impl Iterator<Item = &'a str>) -> String {
    let mut hasher = Sha256::new();
    for line in lines {
        hasher.update(line.as_bytes());
        hasher.update(b"\n");
    }
    to_hex(&hasher.finish())
}
