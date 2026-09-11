//! Logical, asynchronous database exports (D-60).
//!
//! This module deliberately owns the export protocol and its seams, not the
//! Engine.  The Engine adapter supplies a fixed snapshot and bounded pages of
//! opaque canonical document bytes.  In particular, a document never enters
//! `serde_json::Value`: JSONL is assembled by splicing those bytes into a
//! small envelope.  This keeps the export working set bounded by one page.
//!
//! A caller supplies three durable seams:
//!
//! * [`ExportSource`] reads a consistent snapshot and retained history;
//! * [`ExportStore`] records job state and the opaque source cursor; and
//! * [`ArtifactPort`] encrypts and publishes immutable artifact members.
//!
//! The service refuses to start without all three seams, an encryption
//! attestation, and a destination admitted by the placement lock.  A page is
//! published with an idempotent `put` before its cursor is advanced in the
//! durable store.  Replaying a page after a crash therefore either verifies
//! the same bytes or fails closed; it cannot silently append duplicate data.

use std::collections::BTreeSet;
use std::fmt;
use std::fs::{self, File, OpenOptions};
use std::future::Future;
use std::io::{self, Write};
use std::path::{Component, Path, PathBuf};
use std::pin::Pin;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex};

pub(crate) type ExportFuture<'a, T> =
    Pin<Box<dyn Future<Output = Result<T, ExportError>> + Send + 'a>>;
use std::time::{SystemTime, UNIX_EPOCH};

use crate::export_formats::{ExportScope, TransferFormat};
use axum::body::Body;
use axum::http::{Response as HttpResponse, StatusCode, header};
use serde::{Deserialize, Serialize};
use terrabase::regions::RegionCode;

/// Current logical export bundle format.  It is part of the on-disk contract.
pub const EXPORT_FORMAT_VERSION: u16 = 1;
/// Maximum records retained in one source page.
pub const MAX_PAGE_RECORDS: usize = 2_048;
/// Maximum bytes retained while encoding one source page.
pub const MAX_PAGE_BYTES: usize = 8 * 1024 * 1024;
/// Maximum metadata member size accepted from an Engine adapter.
pub const MAX_METADATA_BYTES: usize = 64 * 1024 * 1024;
/// Maximum opaque source cursor retained in durable job state.
pub const MAX_SOURCE_CURSOR_BYTES: usize = 64 * 1024;
const MAX_PROVIDER_RETRIES: usize = 3;

static NEXT_JOB: AtomicU64 = AtomicU64::new(1);

#[path = "export_runtime.rs"]
mod runtime;
pub use runtime::{ExportArtifactFactory, ExportRuntime};

mod region_code_serde {
    use serde::{Deserialize, Deserializer, Serializer};
    use std::str::FromStr;
    use terrabase::regions::RegionCode;

    #[allow(clippy::trivially_copy_pass_by_ref, reason = "serde with requires a borrowed field")]
    pub fn serialize<S: Serializer>(value: &RegionCode, serializer: S) -> Result<S::Ok, S::Error> {
        serializer.serialize_str(value.as_str())
    }

    pub fn deserialize<'de, D: Deserializer<'de>>(deserializer: D) -> Result<RegionCode, D::Error> {
        let text = String::deserialize(deserializer)?;
        RegionCode::from_str(&text).map_err(|error| serde::de::Error::custom(format!("{error:?}")))
    }
}

mod region_vec_serde {
    use serde::{Deserialize, Deserializer, Serializer};
    use std::str::FromStr;
    use terrabase::regions::RegionCode;

    pub fn serialize<S: Serializer>(
        values: &[RegionCode],
        serializer: S,
    ) -> Result<S::Ok, S::Error> {
        serializer.collect_seq(values.iter().map(RegionCode::as_str))
    }

    pub fn deserialize<'de, D: Deserializer<'de>>(
        deserializer: D,
    ) -> Result<Vec<RegionCode>, D::Error> {
        let values = Vec::<String>::deserialize(deserializer)?;
        values
            .into_iter()
            .map(|text| {
                RegionCode::from_str(&text)
                    .map_err(|error| serde::de::Error::custom(format!("{error:?}")))
            })
            .collect()
    }
}

/// A residency lock copied from the durable database placement record.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct PlacementLock {
    regions: BTreeSet<RegionCode>,
}

impl PlacementLock {
    /// Creates a non-empty lock.  Region validation is intentionally left to
    /// the durable placement catalog, which knows the active catalog.
    ///
    /// # Errors
    ///
    /// Returns `PlacementMismatch` when no Regions are supplied.
    pub fn new(regions: impl IntoIterator<Item = RegionCode>) -> Result<Self, ExportError> {
        let regions = regions.into_iter().collect::<BTreeSet<_>>();
        if regions.is_empty() {
            return Err(ExportError::PlacementMismatch);
        }
        Ok(Self { regions })
    }

    /// Creates the common single-Region lock used by local fixtures.
    #[must_use]
    pub fn single(region: RegionCode) -> Self {
        Self { regions: BTreeSet::from([region]) }
    }

    /// Returns whether a destination Region is admitted.
    #[must_use]
    pub fn allows(&self, region: RegionCode) -> bool {
        self.regions.contains(&region)
    }

    /// Returns the exact locked Regions in canonical order.
    pub fn regions(&self) -> impl Iterator<Item = RegionCode> + '_ {
        self.regions.iter().copied()
    }
}

/// A destination identity.  The actual object-store upload belongs to an
/// [`ArtifactPort`] implementation, so this enum carries no credentials.
#[derive(Clone, Debug, Eq, PartialEq, Serialize, Deserialize)]
pub enum ExportDestination {
    /// A local directory, normally used only by development and tests.
    File {
        /// Destination root.
        root: PathBuf,
        /// Region that owns the destination.
        #[serde(with = "region_code_serde")]
        region: RegionCode,
    },
    /// An OCI S3-compatible destination.  Credentials stay in the provider.
    ObjectStorage {
        /// Provider endpoint.
        endpoint: String,
        /// Bucket name.
        bucket: String,
        /// Object prefix.
        prefix: String,
        /// Region that owns the destination.
        #[serde(with = "region_code_serde")]
        region: RegionCode,
    },
}

impl ExportDestination {
    fn region(&self) -> RegionCode {
        match self {
            Self::File { region, .. } | Self::ObjectStorage { region, .. } => *region,
        }
    }

    fn validate(&self) -> Result<(), ExportError> {
        match self {
            Self::File { root, .. } if root.as_os_str().is_empty() => {
                Err(ExportError::DestinationUnavailable)
            }
            Self::ObjectStorage { endpoint, bucket, prefix, .. }
                if !(endpoint.starts_with("https://") || endpoint.starts_with("http://"))
                    || bucket.trim().is_empty()
                    || prefix.contains("..")
                    || prefix.chars().any(char::is_control) =>
            {
                Err(ExportError::DestinationUnavailable)
            }
            _ => Ok(()),
        }
    }
}

/// One metadata member emitted before document pages.
#[derive(Clone, Debug, Eq, PartialEq, Serialize, Deserialize)]
pub struct MetadataMember {
    /// Stable member name, such as `schema.json`.
    pub name: String,
    /// Canonical bytes owned by the Engine/catalog adapter.
    pub bytes: Vec<u8>,
}

/// A fixed source snapshot.  Metadata is captured once, while documents and
/// history are paged at `boundary` by [`ExportSource::page`].
#[derive(Clone, Debug, Eq, PartialEq, Serialize, Deserialize)]
pub struct ExportSnapshot {
    /// Wall clock captured at admission for stable TTL visibility.
    pub captured_at_millis: u64,
    /// Retained history floor captured at admission.
    pub history_start: u64,
    /// Durable publication boundary of this export.
    pub boundary: u64,
    /// Stable database incarnation identity.
    pub database_id: String,
    /// Schema members.
    pub schemas: Vec<MetadataMember>,
    /// Index definition members.
    pub indexes: Vec<MetadataMember>,
    /// Branch metadata members.
    pub branches: Vec<MetadataMember>,
    /// Optional estimate used only for progress; it is never a completeness
    /// claim and the final count comes from emitted pages.
    pub estimated_records: Option<u64>,
}

/// The two data classes in a logical export.
#[derive(Clone, Copy, Debug, Eq, PartialEq, Serialize, Deserialize, utoipa::ToSchema)]
pub enum ExportPhase {
    /// Current canonical document state.
    Documents,
    /// Retained document history, including tombstones.
    History,
    /// All source records have been emitted.
    Done,
}

/// One opaque logical source record.  The `canonical_json` fields are bytes,
/// not parsed JSON values.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum ExportRecord {
    /// One current document winner.
    Document {
        /// Collection identity.
        collection: String,
        /// Document identity.
        id: String,
        /// Document revision.
        version: u64,
        /// Canonical JSON bytes, copied only for this bounded page.
        canonical_json: Vec<u8>,
    },
    /// One retained revision or tombstone.
    History {
        /// Collection identity.
        collection: String,
        /// Document identity.
        id: String,
        /// Publication operation identifier.
        operation_id: String,
        /// Revision identity.
        revision: u64,
        /// Durability achieved by the publication.
        durability: terrabase::LocalDurability,
        /// Whether this revision is a delete.
        deleted: bool,
        /// Canonical JSON bytes when the revision was a put.
        canonical_json: Option<Vec<u8>>,
    },
}

/// A bounded page returned by an Engine adapter.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ExportPage {
    /// Records in stable source order.
    pub records: Vec<ExportRecord>,
    /// Adapter cursor for the next page.  It is never sent to a caller raw.
    pub next_cursor: Option<Vec<u8>>,
    /// True when this phase has no further records.
    pub done: bool,
}

/// Errors returned by a source adapter.
#[derive(Debug)]
pub enum ExportSourceError {
    /// Durable storage could not provide a snapshot or page.
    Storage(String),
    /// The adapter returned an unsafe or malformed page.
    InvalidPage(&'static str),
}

impl fmt::Display for ExportSourceError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Storage(detail) => write!(f, "source storage: {detail}"),
            Self::InvalidPage(detail) => write!(f, "invalid source page: {detail}"),
        }
    }
}

impl std::error::Error for ExportSourceError {}

/// The Engine-facing read seam.  Implementations must pin `snapshot` and
/// honor its boundary on every subsequent page call.
pub trait ExportSource: Send + Sync + 'static {
    /// Captures a fixed logical boundary and its metadata.
    ///
    /// # Errors
    ///
    /// Returns a source storage or metadata-validation failure.
    fn snapshot(&self, database: &str) -> Result<ExportSnapshot, ExportSourceError>;
    /// Returns one bounded page after the opaque adapter cursor.
    ///
    /// # Errors
    ///
    /// Returns a storage failure or an invalid-page refusal for the requested boundary.
    fn page(
        &self,
        database: &str,
        snapshot: &ExportSnapshot,
        phase: ExportPhase,
        after: Option<&[u8]>,
        limit: usize,
    ) -> Result<ExportPage, ExportSourceError>;
}

/// Encryption/provider metadata attested by an artifact port.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct EncryptionAttestation {
    /// Provider identity, never key material.
    pub provider: String,
    /// Database-key version used for every member of this export.
    pub key_version: String,
}

impl EncryptionAttestation {
    fn validate(&self) -> Result<(), ExportError> {
        if self.provider.trim().is_empty() || self.key_version.trim().is_empty() {
            return Err(ExportError::EncryptionUnavailable);
        }
        Ok(())
    }
}

/// One immutable member fact returned by an artifact writer.
#[derive(Clone, Debug, Eq, PartialEq, Serialize, Deserialize, utoipa::ToSchema)]
pub struct ArtifactFact {
    /// Relative bundle member path.
    pub path: String,
    /// Length of the encrypted bytes published by the port.
    #[schema(format = "uint64", minimum = 0)]
    pub length: u64,
    /// BLAKE3 digest of the encrypted bytes.
    pub checksum: String,
}

/// A committed artifact writer.  `put` must be idempotent: an existing path
/// is accepted only when it contains the exact same plaintext. Dropping a
/// writer must preserve published staging members: a durable job cursor may
/// already refer to them, and retries must verify and reuse those bytes.
pub trait ArtifactWriter: Send {
    /// Publishes or verifies one encrypted member.
    ///
    /// # Errors
    ///
    /// Returns encryption, path, I/O, or conflicting-content errors.
    fn put<'a>(&'a mut self, path: &'a str, plaintext: &'a [u8]) -> ExportFuture<'a, ArtifactFact>;
    /// Returns facts for members already present in a resumable staging
    /// artifact.  A restart uses this to rebuild the manifest without
    /// retaining document bytes in the durable job state.  Ports that do not
    /// support restart recovery must fail closed rather than return an empty
    /// list for a non-empty staging artifact.
    ///
    /// # Errors
    ///
    /// Returns an I/O or verification failure, or `DependencyUnavailable`
    /// when the port does not support recovery.
    fn existing_facts(&mut self) -> ExportFuture<'_, Vec<ArtifactFact>> {
        Box::pin(async { Err(ExportError::DependencyUnavailable) })
    }
    /// Atomically publishes the completed bundle and its commit marker.
    ///
    /// # Errors
    ///
    /// Returns a manifest-verification, publication, or durability failure.
    fn commit(self: Box<Self>, manifest: &ArtifactFact) -> ExportFuture<'static, ()>;
}

/// Destination/provider seam.  `open` must reject an absent key/provider.
pub trait ArtifactPort: Send + Sync + 'static {
    /// Removes one expired terminal export's local and remote artifact members.
    /// Implementations must be bounded by the provider's member limit and must
    /// leave the job recoverable when cleanup cannot be confirmed.
    fn delete_job<'a>(&'a self, _job_id: &'a str, _database: &'a str) -> ExportFuture<'a, ()> {
        Box::pin(async { Err(ExportError::DependencyUnavailable) })
    }

    /// Reads and decrypts one immutable member after verifying its commit marker.
    ///
    /// # Errors
    /// Refuses unavailable providers, uncommitted exports, or invalid artifacts.
    fn read_member<'a>(
        &'a self,
        _job_id: &'a str,
        _database: &'a str,
        _member: &'a str,
        _manifest: &'a ArtifactFact,
    ) -> ExportFuture<'a, Vec<u8>> {
        Box::pin(async { Err(ExportError::DependencyUnavailable) })
    }

    /// Returns provider/key metadata without exposing key material.
    fn attestation(&self) -> Option<EncryptionAttestation>;
    /// Opens a job-scoped resumable writer after placement validation.
    ///
    /// # Errors
    ///
    /// Returns a placement, provider, key, destination-validation, or I/O error.
    fn open<'a>(
        &'a self,
        job_id: &'a str,
        database: &'a str,
        destination: &'a ExportDestination,
        placement: &'a PlacementLock,
    ) -> ExportFuture<'a, Box<dyn ArtifactWriter>>;
}

/// The durable state of one export.  Metadata and cursors are small control
/// records; document bytes are never stored in this state.
#[derive(Clone, Debug, Eq, PartialEq, Serialize, Deserialize)]
pub struct ExportJobState {
    /// Stable idempotency/job identity.
    pub job_id: String,
    /// Database identity.
    pub database: String,
    /// Placement used at admission.
    pub destination: ExportDestination,
    /// Encoding selected at admission; changing it cannot resume this job.
    #[serde(default)]
    pub format: TransferFormat,
    /// Logical records included in the artifact.
    #[serde(default)]
    pub scope: ExportScope,
    /// Fixed placement lock text (Region enum is serializable).
    #[serde(with = "region_vec_serde")]
    pub placement: Vec<RegionCode>,
    /// Captured source snapshot.
    pub snapshot: ExportSnapshot,
    /// Current phase.
    pub phase: ExportPhase,
    /// Adapter cursor, not caller-facing.
    pub source_cursor: Option<Vec<u8>>,
    /// Number of records emitted.
    pub records: u64,
    /// Number of pages emitted.
    pub pages: u64,
    /// Job state.
    pub status: JobStatus,
    /// Last durable opaque resume cursor.
    pub resume_cursor: String,
    /// Final manifest fact after commit.
    pub manifest: Option<ArtifactFact>,
    /// Terminal error safe for API display.
    pub error: Option<String>,
}

/// Durable export lifecycle.
#[derive(Clone, Debug, Eq, PartialEq, Serialize, Deserialize, utoipa::ToSchema)]
pub enum JobStatus {
    /// Accepted and waiting for a worker.
    Accepted,
    /// A worker is publishing pages.
    Running,
    /// Cancellation stopped work at a durable chunk boundary; staged output remains resumable.
    Cancelled,
    /// All members and the commit marker are durable.
    Complete,
    /// The job failed closed; state remains resumable when the error is
    /// transient and callers may submit the same idempotency key.
    Failed,
}

/// Exclusive job ownership, released when the guard is dropped.
/// Implementations must retain ownership for the guard's entire lifetime;
/// an expiring lease without fencing of artifact and state writes is insufficient.
pub trait ExportJobLease: Send {}

impl ExportJobLease for File {}
impl ExportJobLease for ActiveJob {}

/// Durable state store seam.
pub trait ExportStore: Send + Sync + 'static {
    /// Reads only the compact public progress projection, without snapshot metadata.
    ///
    /// # Errors
    /// Returns a durable read error or `DependencyUnavailable` when unsupported.
    fn progress(&self, _job_id: &str) -> Result<Option<ExportStatus>, ExportError> {
        Err(ExportError::DependencyUnavailable)
    }

    /// Lists durable jobs in ascending identity order, exclusively after `after`.
    ///
    /// # Errors
    /// Returns an unavailable dependency, invalid limit, or durable read error.
    fn list(&self, _after: Option<&str>, _limit: usize) -> Result<Vec<String>, ExportError> {
        Err(ExportError::DependencyUnavailable)
    }

    /// Persists a cancellation request independently of worker progress writes.
    /// Returns false when the job is absent or already terminal.
    ///
    /// # Errors
    /// Returns a persistence or unsupported-store error.
    fn request_cancel(&self, _job_id: &str) -> Result<bool, ExportError> {
        Err(ExportError::DependencyUnavailable)
    }

    /// Reads the independently durable cancellation request.
    ///
    /// # Errors
    /// Returns a persistence or unsupported-store error.
    fn cancel_requested(&self, _job_id: &str) -> Result<bool, ExportError> {
        Err(ExportError::DependencyUnavailable)
    }

    /// Clears cancellation only for an explicit authenticated resume.
    ///
    /// # Errors
    /// Returns a persistence or unsupported-store error.
    fn clear_cancel(&self, _job_id: &str) -> Result<(), ExportError> {
        Err(ExportError::DependencyUnavailable)
    }

    /// Attempts exclusive worker ownership across every handle and process
    /// sharing this store. Returns `None` when another worker owns the job.
    /// Workers must hold the guard across all state and artifact operations.
    ///
    /// # Errors
    ///
    /// Returns an identity or storage error if ownership cannot be established.
    fn acquire_job(&self, job_id: &str) -> Result<Option<Box<dyn ExportJobLease>>, ExportError>;

    /// Atomically creates a job or returns the record that already owns its ID.
    /// Existing progress must never be overwritten by concurrent admission.
    ///
    /// # Errors
    ///
    /// Returns an identity, encoding, I/O, or durability error. Implementations
    /// must not emulate this operation with an unprotected load followed by save.
    fn create(&self, state: &ExportJobState) -> Result<ExportJobState, ExportError>;
    /// Loads a job, if present.
    ///
    /// # Errors
    ///
    /// Returns an invalid-identity, I/O, or durable-record decoding error.
    fn load(&self, job_id: &str) -> Result<Option<ExportJobState>, ExportError>;
    /// Atomically records a job state. Worker updates require the caller to
    /// retain the job's exclusive [`ExportJobLease`] until this call finishes.
    ///
    /// # Errors
    ///
    /// Returns an invalid-identity, encoding, I/O, or durability error.
    fn save(&self, state: &ExportJobState) -> Result<(), ExportError>;
}

/// Service failures. Diagnostic details may include provider information;
/// HTTP responses and durable status must use the bounded public message.
#[derive(Debug)]
pub enum ExportError {
    /// Required source, store, or artifact provider was not configured.
    DependencyUnavailable,
    /// Key/provider metadata or encryption port is absent.
    EncryptionUnavailable,
    /// Destination is not admitted by placement policy.
    PlacementMismatch,
    /// Destination cannot be opened or has unsafe identity.
    DestinationUnavailable,
    /// Caller supplied malformed input or cursor.
    InvalidRequest(&'static str),
    /// A source, store, or artifact operation failed.
    Io(String),
    /// A crash-safe idempotency replay found different bytes.
    IdempotencyConflict,
    /// The source violated its bounded-page contract.
    Source(ExportSourceError),
}

impl ExportError {
    /// A bounded response message containing no provider-supplied text.
    pub(crate) fn public_message(&self) -> &'static str {
        match self {
            Self::DependencyUnavailable => "export dependency unavailable",
            Self::EncryptionUnavailable => "export encryption provider unavailable",
            Self::PlacementMismatch => "export destination violates placement lock",
            Self::DestinationUnavailable => "export destination unavailable",
            Self::InvalidRequest(_) => "invalid export request or resume cursor",
            Self::Io(_) => "export storage operation failed",
            Self::IdempotencyConflict => "export idempotency conflict",
            Self::Source(_) => "export source operation failed",
        }
    }
}

impl fmt::Display for ExportError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::DependencyUnavailable => f.write_str("export dependency unavailable"),
            Self::EncryptionUnavailable => f.write_str("export encryption provider unavailable"),
            Self::PlacementMismatch => f.write_str("export destination violates placement lock"),
            Self::DestinationUnavailable => f.write_str("export destination unavailable"),
            Self::InvalidRequest(detail) => write!(f, "invalid export request: {detail}"),
            Self::Io(detail) => write!(f, "export I/O failure: {detail}"),
            Self::IdempotencyConflict => f.write_str("export idempotency conflict"),
            Self::Source(error) => error.fmt(f),
        }
    }
}

impl std::error::Error for ExportError {}

/// An accepted asynchronous export.
#[derive(Clone, Debug, Eq, PartialEq, Serialize, Deserialize, utoipa::ToSchema)]
pub struct ExportAccepted {
    /// Job identity.
    pub job_id: String,
    /// Status polling path supplied to HTTP and CLI callers.
    pub status_path: String,
    /// Opaque cursor valid for this job and service instance.
    pub resume_cursor: String,
}

/// Whether cancellation can still stop work at the next durable boundary.
#[derive(Clone, Debug, Eq, PartialEq, Serialize, Deserialize, utoipa::ToSchema)]
#[serde(rename_all = "snake_case")]
pub enum ExportCancellation {
    /// A cooperative cancellation request can be submitted.
    Available,
    /// A durable request is waiting for the next safe boundary.
    Requested,
    /// Work stopped; already emitted records and pages remain staged.
    Complete,
    /// The operation is committed or its store does not support cancellation.
    Unavailable,
}

/// Status view returned by polling.
#[derive(Clone, Debug, Eq, PartialEq, Serialize, Deserialize, utoipa::ToSchema)]
pub struct ExportStatus {
    /// Job identity.
    pub job_id: String,
    /// Current lifecycle state.
    pub status: JobStatus,
    /// Snapshot capture time in Unix milliseconds for operation activity views.
    #[schema(format = "uint64", minimum = 0)]
    pub created_at_millis: u64,
    /// Current bounded work phase.
    pub phase: ExportPhase,
    /// Cooperative cancellation availability and progress.
    pub cancellation: ExportCancellation,
    /// Durably emitted pages retained even when cancellation completes.
    #[schema(format = "uint64", minimum = 0)]
    pub pages: u64,
    /// Records emitted so far.
    #[schema(format = "uint64", minimum = 0)]
    pub records: u64,
    /// Optional estimate.
    #[schema(format = "uint64", minimum = 0)]
    pub estimated_records: Option<u64>,
    /// Progress in the inclusive range 0..=10000 (basis points).
    pub progress_basis_points: u16,
    /// Opaque cursor for retry/resume.
    pub resume_cursor: String,
    /// Final artifact manifest when complete.
    pub manifest: Option<ArtifactFact>,
    /// Safe terminal detail.
    pub error: Option<String>,
}

/// Request accepted by [`ExportService::submit`].
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ExportRequest {
    /// Database name.
    pub database: String,
    /// Destination identity.
    pub destination: ExportDestination,
    /// Encoding selected at admission.
    pub format: TransferFormat,
    /// Logical records included in the artifact.
    pub scope: ExportScope,
    /// Exact database placement lock.
    pub placement: PlacementLock,
    /// Optional retry identity. Repeating it returns the original job.
    pub idempotency_key: Option<String>,
}

/// The asynchronous export coordinator.
#[derive(Clone)]
pub struct ExportService {
    source: Arc<dyn ExportSource>,
    store: Arc<dyn ExportStore>,
    artifacts: Arc<dyn ArtifactPort>,
    cursor_key: [u8; 32],
    active_jobs: Arc<Mutex<BTreeSet<String>>>,
    stop_requested: Arc<AtomicBool>,
}

impl ExportService {
    /// Constructs a service. Missing source/store/port or encryption provider
    /// is rejected before any request can be admitted.
    ///
    /// # Errors
    ///
    /// Returns `DependencyUnavailable` for a missing dependency or
    /// `EncryptionUnavailable` for absent or invalid provider metadata.
    pub fn new(
        source: Option<Arc<dyn ExportSource>>,
        store: Option<Arc<dyn ExportStore>>,
        artifacts: Option<Arc<dyn ArtifactPort>>,
        cursor_key: [u8; 32],
    ) -> Result<Self, ExportError> {
        let source = source.ok_or(ExportError::DependencyUnavailable)?;
        let store = store.ok_or(ExportError::DependencyUnavailable)?;
        let artifacts = artifacts.ok_or(ExportError::DependencyUnavailable)?;
        artifacts.attestation().ok_or(ExportError::EncryptionUnavailable)?.validate()?;
        Ok(Self {
            source,
            store,
            artifacts,
            cursor_key,
            active_jobs: Arc::new(Mutex::new(BTreeSet::new())),
            stop_requested: Arc::new(AtomicBool::new(false)),
        })
    }

    /// Admits a job and schedules bounded work.  The initial state is durable
    /// before the worker is spawned, so a process crash cannot lose an accept.
    ///
    /// # Errors
    ///
    /// Returns request, placement, idempotency, source snapshot, durable store,
    /// or worker-reservation failures before accepting the request. Returns
    /// `DependencyUnavailable` if the blocking admission task fails.
    pub async fn submit(&self, request: ExportRequest) -> Result<ExportAccepted, ExportError> {
        let service = self.clone();
        tokio::task::spawn_blocking(move || service.submit_blocking(&request))
            .await
            .map_err(|_| ExportError::DependencyUnavailable)?
    }

    fn submit_blocking(&self, request: &ExportRequest) -> Result<ExportAccepted, ExportError> {
        validate_request(request)?;
        let job_id = job_id(request, &self.cursor_key);
        if let Some(existing) = self.store.load(&job_id)? {
            // Another admission may have linked this record just before its
            // directory sync. The atomic create path also confirms durability
            // when the record already exists, without replacing worker progress.
            let existing = self.store.create(&existing)?;
            return self.accept_admitted(request, &job_id, &existing);
        }
        let snapshot = self.source.snapshot(&request.database).map_err(ExportError::Source)?;
        validate_snapshot(&snapshot)?;
        let state = ExportJobState {
            job_id: job_id.clone(),
            database: request.database.clone(),
            destination: request.destination.clone(),
            format: request.format,
            scope: request.scope,
            placement: request.placement.regions().collect(),
            snapshot,
            phase: ExportPhase::Documents,
            source_cursor: None,
            records: 0,
            pages: 0,
            status: JobStatus::Accepted,
            resume_cursor: self.cursor(&job_id, ExportPhase::Documents, None),
            manifest: None,
            error: None,
        };
        let admitted = self.store.create(&state)?;
        self.accept_admitted(request, &job_id, &admitted)
    }

    fn accept_admitted(
        &self,
        request: &ExportRequest,
        job_id: &str,
        state: &ExportJobState,
    ) -> Result<ExportAccepted, ExportError> {
        validate_state(state)?;
        if state.job_id != job_id
            || state.database != request.database
            || state.destination != request.destination
            || state.format != request.format
            || state.scope != request.scope
            || state.placement != request.placement.regions().collect::<Vec<_>>()
        {
            return Err(ExportError::IdempotencyConflict);
        }
        if !matches!(state.status, JobStatus::Complete | JobStatus::Cancelled) {
            self.spawn_worker(job_id)?;
        }
        Ok(accepted(state))
    }

    /// Reads a job's durable control record synchronously. Async callers should
    /// use [`Self::status_async`] to keep filesystem I/O off runtime threads.
    ///
    /// # Errors
    ///
    /// Returns a store read or invalid durable-state error.
    pub fn status(&self, job_id: &str) -> Result<Option<ExportStatus>, ExportError> {
        match self.store.progress(job_id) {
            Ok(Some(mut progress)) => {
                if progress.job_id != job_id {
                    return Err(ExportError::IdempotencyConflict);
                }
                progress.error = progress
                    .error
                    .map(|_| "export failed; retry with the latest resume cursor".into());
                return Ok(Some(progress));
            }
            Ok(None) => return Ok(None),
            Err(ExportError::DependencyUnavailable) => (),
            Err(error) => return Err(error),
        }
        let Some(state) = self.store.load(job_id)? else { return Ok(None) };
        self.public_status(&state).map(Some)
    }

    fn public_status(&self, state: &ExportJobState) -> Result<ExportStatus, ExportError> {
        validate_state(state)?;
        let cancellation = match state.status {
            JobStatus::Complete => ExportCancellation::Unavailable,
            JobStatus::Cancelled => ExportCancellation::Complete,
            _ => match self.store.cancel_requested(&state.job_id) {
                Ok(true) => ExportCancellation::Requested,
                Ok(false) => ExportCancellation::Available,
                Err(ExportError::DependencyUnavailable) => ExportCancellation::Unavailable,
                Err(error) => return Err(error),
            },
        };
        status_from_state(state, cancellation)
    }

    /// Reads a job's durable control record on the blocking pool.
    ///
    /// # Errors
    ///
    /// Returns a store read or invalid durable-state error, or
    /// `DependencyUnavailable` if the blocking task fails.
    pub async fn status_async(&self, job_id: &str) -> Result<Option<ExportStatus>, ExportError> {
        let service = self.clone();
        let job_id = job_id.to_owned();
        tokio::task::spawn_blocking(move || service.status(&job_id))
            .await
            .map_err(|_| ExportError::DependencyUnavailable)?
    }

    /// Lists a bounded page of durable operation status without document data.
    ///
    /// # Errors
    /// Returns an invalid pagination argument, storage error, or unavailable task.
    pub async fn list_async(
        &self,
        after: Option<&str>,
        limit: usize,
    ) -> Result<Vec<ExportStatus>, ExportError> {
        if !(1..=100).contains(&limit)
            || after.is_some_and(|value| {
                value.len() != 32 || !value.bytes().all(|byte| byte.is_ascii_hexdigit())
            })
        {
            return Err(ExportError::InvalidRequest("invalid export pagination"));
        }
        let service = self.clone();
        let after = after.map(str::to_owned);
        tokio::spawn(async move {
            service
                .store
                .list(after.as_deref(), limit)?
                .into_iter()
                .map(|job| service.status(&job)?.ok_or(ExportError::IdempotencyConflict))
                .collect()
        })
        .await
        .map_err(|_| ExportError::DependencyUnavailable)?
    }

    /// Requests cancellation at the next durable chunk boundary.
    /// Already emitted records remain staged; a concurrent final commit can win.
    ///
    /// # Errors
    /// Returns an unknown-job, storage, or unavailable-cancellation error.
    pub async fn cancel(&self, job_id: &str) -> Result<ExportStatus, ExportError> {
        let service = self.clone();
        let job = job_id.to_owned();
        tokio::task::spawn_blocking(move || {
            let state =
                service.store.load(&job)?.ok_or(ExportError::InvalidRequest("unknown job"))?;
            validate_state(&state)?;
            if !matches!(state.status, JobStatus::Complete | JobStatus::Cancelled) {
                service.store.request_cancel(&job)?;
                service.spawn_worker(&job)?;
            }
            service.status(&job)?.ok_or(ExportError::InvalidRequest("unknown job"))
        })
        .await
        .map_err(|_| ExportError::DependencyUnavailable)?
    }

    /// Validates and decodes a caller cursor, returning its phase and source
    /// cursor.  Cursors are authenticated and scoped to one job.
    ///
    /// # Errors
    ///
    /// Returns `InvalidRequest` for malformed, unauthenticated, or wrong-job cursors.
    pub fn decode_resume_cursor(
        &self,
        job_id: &str,
        token: &str,
    ) -> Result<(ExportPhase, Option<Vec<u8>>), ExportError> {
        decode_cursor(&self.cursor_key, job_id, token)
    }

    /// Explicitly retries/resumes a job using its authenticated opaque cursor.
    /// The cursor must be the latest cursor persisted for that job.
    ///
    /// # Errors
    ///
    /// Returns a store or state-validation error, an unknown-job or stale-cursor
    /// refusal, failed cursor authentication, or worker-reservation failure.
    /// Returns `DependencyUnavailable` if the blocking resume task fails.
    pub async fn resume(&self, job_id: &str, token: &str) -> Result<ExportStatus, ExportError> {
        let service = self.clone();
        let job_id = job_id.to_owned();
        let token = token.to_owned();
        tokio::task::spawn_blocking(move || service.resume_blocking(&job_id, &token))
            .await
            .map_err(|_| ExportError::DependencyUnavailable)?
    }

    fn resume_blocking(&self, job_id: &str, token: &str) -> Result<ExportStatus, ExportError> {
        let state = self.store.load(job_id)?.ok_or(ExportError::InvalidRequest("unknown job"))?;
        validate_state(&state)?;
        if state.resume_cursor != token {
            return Err(ExportError::InvalidRequest("resume cursor is stale"));
        }
        let _ = self.decode_resume_cursor(job_id, token)?;
        if state.status == JobStatus::Complete {
            return self.public_status(&state);
        }
        // A cancellation observer may still be publishing its terminal state.
        // Hold the durable worker lease and check the local reservation before
        // clearing its flag, so no accepted resume intent can be overwritten.
        let lease = self.store.acquire_job(job_id)?.ok_or(ExportError::IdempotencyConflict)?;
        if self.active_jobs.lock().map_err(|_| ExportError::DependencyUnavailable)?.contains(job_id)
        {
            return Err(ExportError::IdempotencyConflict);
        }
        let mut state =
            self.store.load(job_id)?.ok_or(ExportError::InvalidRequest("unknown job"))?;
        validate_state(&state)?;
        if state.resume_cursor != token {
            return Err(ExportError::IdempotencyConflict);
        }
        if state.status == JobStatus::Complete {
            return self.public_status(&state);
        }
        match self.store.clear_cancel(job_id) {
            Ok(()) | Err(ExportError::DependencyUnavailable) => (),
            Err(error) => return Err(error),
        }
        state.status = JobStatus::Accepted;
        state.error = None;
        self.store.save(&state)?;
        drop(lease);
        self.spawn_worker(job_id)?;
        self.status(job_id)?.ok_or(ExportError::InvalidRequest("unknown job"))
    }

    /// Reads one completed export member on the blocking pool.
    ///
    /// # Errors
    /// Refuses unknown or incomplete jobs and unverified artifact bytes.
    pub async fn download(&self, job_id: &str, member: &str) -> Result<Vec<u8>, ExportError> {
        Ok(self.download_with_format(job_id, member).await?.0)
    }

    /// Reads one completed export member and returns its durable encoding.
    ///
    /// # Errors
    ///
    /// Refuses unknown or incomplete jobs, invalid member paths, dependency
    /// failures, and artifacts whose bytes or manifest cannot be verified.
    pub async fn download_with_format(
        &self,
        job_id: &str,
        member: &str,
    ) -> Result<(Vec<u8>, TransferFormat), ExportError> {
        let service = self.clone();
        let job = job_id.to_owned();
        let member = member.to_owned();
        let load_job = job.clone();
        let load_member = member.clone();
        let (database, manifest, format) = tokio::task::spawn_blocking(move || {
            if !safe_member_path(&load_member) {
                return Err(ExportError::InvalidRequest("invalid member"));
            }
            let state =
                service.store.load(&load_job)?.ok_or(ExportError::InvalidRequest("unknown job"))?;
            validate_state(&state)?;
            if state.status != JobStatus::Complete {
                return Err(ExportError::InvalidRequest("export incomplete"));
            }
            let manifest = state.manifest.ok_or(ExportError::IdempotencyConflict)?;
            Ok::<_, ExportError>((state.database, manifest, state.format))
        })
        .await
        .map_err(|_| ExportError::DependencyUnavailable)??;
        Ok((service.artifacts.read_member(&job, &database, &member, &manifest).await?, format))
    }

    fn clone_for_worker(&self) -> Worker {
        Worker {
            source: Arc::clone(&self.source),
            store: Arc::clone(&self.store),
            artifacts: Arc::clone(&self.artifacts),
            cursor_key: self.cursor_key,
            stop_requested: Arc::clone(&self.stop_requested),
        }
    }

    fn spawn_worker(&self, job_id: &str) -> Result<(), ExportError> {
        let Some(active) = ActiveJob::acquire(Arc::clone(&self.active_jobs), job_id)? else {
            return Ok(());
        };
        let worker = self.clone_for_worker();
        let job = job_id.to_owned();
        tokio::spawn(async move {
            let _active = active;
            let _ = worker.run_with_retries(&job).await;
        });
        Ok(())
    }

    fn cursor(&self, job_id: &str, phase: ExportPhase, source_cursor: Option<&[u8]>) -> String {
        encode_cursor(&self.cursor_key, job_id, phase, source_cursor)
    }
}

/// Projects durable state into bounded public progress without cloning snapshot metadata.
pub(crate) fn status_from_state(
    state: &ExportJobState,
    cancellation: ExportCancellation,
) -> Result<ExportStatus, ExportError> {
    validate_state(state)?;
    let progress_basis_points = match state.status {
        JobStatus::Complete => 10_000,
        _ => state
            .snapshot
            .estimated_records
            .filter(|estimate| *estimate > 0)
            .map_or(0, |estimate| {
                ((state.records.saturating_mul(10_000) / estimate).min(9_999)) as u16
            }),
    };
    Ok(ExportStatus {
        job_id: state.job_id.clone(),
        status: state.status.clone(),
        phase: state.phase,
        created_at_millis: state.snapshot.captured_at_millis,
        cancellation,
        pages: state.pages,
        records: state.records,
        estimated_records: state.snapshot.estimated_records,
        progress_basis_points,
        resume_cursor: state.resume_cursor.clone(),
        manifest: state.manifest.clone(),
        // Older records may contain raw provider errors. Never reflect
        // those persisted diagnostics through the public status route.
        error: state
            .error
            .as_ref()
            .map(|_| "export failed; retry with the latest resume cursor".into()),
    })
}

struct Worker {
    source: Arc<dyn ExportSource>,
    store: Arc<dyn ExportStore>,
    artifacts: Arc<dyn ArtifactPort>,
    cursor_key: [u8; 32],
    stop_requested: Arc<AtomicBool>,
}

struct ActiveJob {
    jobs: Arc<Mutex<BTreeSet<String>>>,
    id: String,
}

impl ActiveJob {
    fn acquire(jobs: Arc<Mutex<BTreeSet<String>>>, id: &str) -> Result<Option<Self>, ExportError> {
        let inserted = {
            let mut active =
                jobs.lock().map_err(|_| ExportError::Io("job lock poisoned".into()))?;
            if !active.contains(id) && active.len() >= 16 {
                return Err(ExportError::DependencyUnavailable);
            }
            active.insert(id.to_owned())
        };
        if !inserted {
            return Ok(None);
        }
        Ok(Some(Self { jobs, id: id.to_owned() }))
    }
}

impl Drop for ActiveJob {
    fn drop(&mut self) {
        if let Ok(mut jobs) = self.jobs.lock() {
            jobs.remove(&self.id);
        }
    }
}

impl Worker {
    /// Runs one job with the bounded provider retry policy used by both live
    /// admission and restart recovery.  A dependency failure leaves the
    /// durable job accepted for a later explicit retry when this bound is
    /// exhausted.
    async fn run_with_retries(&self, job_id: &str) -> Result<(), ExportError> {
        for attempt in 0..MAX_PROVIDER_RETRIES {
            let result = self.run(job_id).await;
            if !matches!(result, Err(ExportError::DependencyUnavailable))
                || attempt + 1 == MAX_PROVIDER_RETRIES
            {
                return result;
            }
            tokio::time::sleep(std::time::Duration::from_millis(10 * (attempt as u64 + 1))).await;
        }
        unreachable!("provider retry loop has a non-zero bound")
    }

    async fn run(&self, job_id: &str) -> Result<(), ExportError> {
        if self.stop_requested.load(Ordering::Acquire) {
            return Ok(());
        }
        let Some(_lease) = self.store.acquire_job(job_id)? else {
            return Ok(());
        };
        let mut state =
            self.store.load(job_id)?.ok_or(ExportError::InvalidRequest("unknown job"))?;
        validate_state(&state)?;
        if state.status == JobStatus::Complete {
            return Ok(());
        }
        state.status = JobStatus::Running;
        state.error = None;
        self.store.save(&state)?;
        let result: Result<Option<ArtifactFact>, ExportError> = async {
            if self.cancellation_requested(job_id)? {
                return Ok(None);
            }
            let placement = PlacementLock::new(state.placement.iter().copied())?;
            let mut writer = self
                .artifacts
                .open(job_id, &state.database, &state.destination, &placement)
                .await?;
            let Some(manifest) = self.run_inner(&mut state, &mut writer).await? else {
                return Ok(None);
            };
            if self.cancellation_requested(job_id)? {
                return Ok(None);
            }
            writer.commit(&manifest).await?;
            Ok(Some(manifest))
        }
        .await;
        match result {
            Ok(Some(manifest)) => {
                state.manifest = Some(manifest);
                state.status = JobStatus::Complete;
                state.phase = ExportPhase::Done;
                state.resume_cursor =
                    encode_cursor(&self.cursor_key, job_id, ExportPhase::Done, None);
                self.store.save(&state)
            }
            Ok(None) => {
                if self.stop_requested.load(Ordering::Acquire) {
                    return Ok(());
                }
                state.status = JobStatus::Cancelled;
                state.error = None;
                self.store.save(&state)
            }
            // A provider can return an indeterminate result after accepting
            // a write (for example, when its response is lost).  The durable
            // cursor remains valid, but marking the job failed would make a
            // retry look like a terminal source error and could discard the
            // provider's committed staging state.  Leave the job admitted so
            // recovery or an explicit resume can reconcile the provider.
            Err(ExportError::DependencyUnavailable) => {
                state.status = JobStatus::Accepted;
                state.error = None;
                self.store.save(&state)?;
                Err(ExportError::DependencyUnavailable)
            }
            Err(error) => {
                state.status = JobStatus::Failed;
                state.error = Some(error.public_message().to_owned());
                let _ = self.store.save(&state);
                Err(error)
            }
        }
    }

    fn cancellation_requested(&self, job_id: &str) -> Result<bool, ExportError> {
        if self.stop_requested.load(Ordering::Acquire) {
            return Ok(true);
        }
        match self.store.cancel_requested(job_id) {
            Ok(requested) => Ok(requested),
            Err(ExportError::DependencyUnavailable) => Ok(false),
            Err(error) => Err(error),
        }
    }

    async fn run_inner(
        &self,
        state: &mut ExportJobState,
        writer: &mut Box<dyn ArtifactWriter>,
    ) -> Result<Option<ArtifactFact>, ExportError> {
        let metadata = metadata_members(&state.snapshot)?;
        let (mut facts_by_path, mut facts) = recover_facts(writer, metadata.len() + 4).await?;
        for (path, bytes) in metadata {
            if self.cancellation_requested(&state.job_id)? {
                return Ok(None);
            }
            let fact = put_checked(writer, &path, &bytes).await?;
            if facts_by_path.insert(fact.path.clone()) {
                facts.push(fact);
            }
        }
        loop {
            if self.cancellation_requested(&state.job_id)? {
                return Ok(None);
            }
            if state.phase == ExportPhase::Done {
                break;
            }
            if state.phase == ExportPhase::History && state.scope == ExportScope::Documents {
                state.phase = ExportPhase::Done;
                state.source_cursor = None;
                state.resume_cursor =
                    encode_cursor(&self.cursor_key, &state.job_id, state.phase, None);
                self.store.save(state)?;
                continue;
            }
            // Match the artifact provider recovery bound: an export must
            // never publish a bundle too large to reopen after a restart.
            if facts.len() >= MAX_PAGE_RECORDS.saturating_mul(4).saturating_sub(2) {
                return Err(ExportError::InvalidRequest("export artifact member limit exceeded"));
            }
            let page = self
                .source
                .page(
                    &state.database,
                    &state.snapshot,
                    state.phase,
                    state.source_cursor.as_deref(),
                    MAX_PAGE_RECORDS,
                )
                .map_err(ExportError::Source)?;
            validate_page(&page, state.phase)?;
            if page.records.is_empty() && !page.done {
                return Err(ExportError::Source(ExportSourceError::InvalidPage(
                    "empty non-terminal page",
                )));
            }
            if !page.done && page.next_cursor.is_none() {
                return Err(ExportError::Source(ExportSourceError::InvalidPage(
                    "non-terminal page has no cursor",
                )));
            }
            if !page.records.is_empty() {
                let (jsonl, history) = encode_page(&page.records)?;
                let jsonl =
                    if matches!(state.format, TransferFormat::Native | TransferFormat::Jsonl) {
                        jsonl
                    } else {
                        crate::export_formats::encode_documents(&page.records, state.format)
                            .map_err(|_| {
                                ExportError::Source(ExportSourceError::InvalidPage(
                                    "format encoding failed",
                                ))
                            })?
                    };
                let page_number = state.pages;
                if !jsonl.is_empty() {
                    let suffix = crate::export_formats::extension(state.format);
                    let path = format!("documents/{page_number:012}.{suffix}");
                    let fact = put_checked(writer, &path, &jsonl).await?;
                    if facts_by_path.insert(fact.path.clone()) {
                        facts.push(fact);
                    }
                }
                if !history.is_empty() {
                    let path = format!("history/{page_number:012}.jsonl");
                    let fact = put_checked(writer, &path, &history).await?;
                    if facts_by_path.insert(fact.path.clone()) {
                        facts.push(fact);
                    }
                }
                state.records = state.records.saturating_add(page.records.len() as u64);
                state.pages = state.pages.saturating_add(1);
            }
            state.source_cursor = page.next_cursor;
            if page.done {
                state.phase = match state.phase {
                    ExportPhase::Documents => ExportPhase::History,
                    ExportPhase::History | ExportPhase::Done => ExportPhase::Done,
                };
                state.source_cursor = None;
            }
            state.resume_cursor = encode_cursor(
                &self.cursor_key,
                &state.job_id,
                state.phase,
                state.source_cursor.as_deref(),
            );
            // State advances only after all page puts have succeeded. This is
            // the crash/idempotency ordering guarantee.
            self.store.save(state)?;
        }
        publish_bundle(writer, state, &mut facts_by_path, &mut facts).await.map(Some)
    }
}

async fn recover_facts(
    writer: &mut Box<dyn ArtifactWriter>,
    capacity: usize,
) -> Result<(BTreeSet<String>, Vec<ArtifactFact>), ExportError> {
    let mut paths = BTreeSet::new();
    let mut facts = Vec::with_capacity(capacity);
    for fact in writer.existing_facts().await? {
        validate_fact(&fact)?;
        if matches!(fact.path.as_str(), "checksums.txt" | "manifest.json") {
            continue;
        }
        if !bundle_member_path(&fact.path) || !paths.insert(fact.path.clone()) {
            return Err(ExportError::IdempotencyConflict);
        }
        facts.push(fact);
    }
    Ok((paths, facts))
}

async fn publish_bundle(
    writer: &mut Box<dyn ArtifactWriter>,
    state: &ExportJobState,
    paths: &mut BTreeSet<String>,
    facts: &mut Vec<ArtifactFact>,
) -> Result<ArtifactFact, ExportError> {
    facts.sort_by(|left, right| left.path.cmp(&right.path));
    let mut checksum_bytes = Vec::new();
    for fact in facts.iter() {
        checksum_bytes.extend_from_slice(fact.path.as_bytes());
        checksum_bytes.push(b'\t');
        checksum_bytes.extend_from_slice(fact.length.to_string().as_bytes());
        checksum_bytes.push(b'\t');
        checksum_bytes.extend_from_slice(fact.checksum.as_bytes());
        checksum_bytes.push(b'\n');
    }
    let checksums = put_checked(writer, "checksums.txt", &checksum_bytes).await?;
    if paths.insert(checksums.path.clone()) {
        facts.push(checksums);
    }
    let manifest = manifest_bytes(state, facts)?;
    put_checked(writer, "manifest.json", &manifest).await
}

fn validate_fact(fact: &ArtifactFact) -> Result<(), ExportError> {
    if !safe_member_path(&fact.path)
        || fact.length > (MAX_METADATA_BYTES.max(MAX_PAGE_BYTES) as u64)
        || fact.checksum.len() != 64
        || !fact.checksum.bytes().all(|byte| byte.is_ascii_hexdigit())
    {
        return Err(ExportError::IdempotencyConflict);
    }
    Ok(())
}

fn bundle_member_path(path: &str) -> bool {
    ["schemas/", "indexes/", "branches/", "documents/", "history/"]
        .iter()
        .any(|prefix| path.starts_with(prefix))
}

async fn put_checked(
    writer: &mut Box<dyn ArtifactWriter>,
    path: &str,
    plaintext: &[u8],
) -> Result<ArtifactFact, ExportError> {
    let fact = writer.put(path, plaintext).await?;
    validate_fact(&fact)?;
    if fact.path != path
        || (!matches!(path, "checksums.txt" | "manifest.json") && !bundle_member_path(path))
        || (fact.length == 0 && !plaintext.is_empty())
    {
        return Err(ExportError::IdempotencyConflict);
    }
    Ok(fact)
}

fn metadata_members(snapshot: &ExportSnapshot) -> Result<Vec<(String, Vec<u8>)>, ExportError> {
    let mut members = Vec::new();
    let mut names = BTreeSet::new();
    for (prefix, input) in [
        ("schemas", &snapshot.schemas),
        ("indexes", &snapshot.indexes),
        ("branches", &snapshot.branches),
    ] {
        for member in input {
            if member.bytes.len() > MAX_METADATA_BYTES
                || !safe_member_path(&member.name)
                || !names.insert(format!("{prefix}/{}", member.name))
            {
                return Err(ExportError::InvalidRequest("metadata member is unsafe or too large"));
            }
            members.push((format!("{prefix}/{}", member.name), member.bytes.clone()));
        }
    }
    Ok(members)
}

fn manifest_bytes(state: &ExportJobState, facts: &[ArtifactFact]) -> Result<Vec<u8>, ExportError> {
    let mut out = String::new();
    out.push_str("{\"format\":\"terrascale-logical-export\",\"version\":");
    out.push_str(&EXPORT_FORMAT_VERSION.to_string());
    out.push_str(",\"database\":");
    append_json_string(&mut out, &state.snapshot.database_id)?;
    out.push_str(",\"boundary\":");
    out.push_str(&state.snapshot.boundary.to_string());
    out.push_str(",\"records\":");
    out.push_str(&state.records.to_string());
    out.push_str(",\"members\":[");
    for (index, fact) in facts.iter().enumerate() {
        if index != 0 {
            out.push(',');
        }
        out.push_str("{\"path\":");
        append_json_string(&mut out, &fact.path)?;
        out.push_str(",\"length\":");
        out.push_str(&fact.length.to_string());
        out.push_str(",\"checksum\":");
        append_json_string(&mut out, &fact.checksum)?;
        out.push('}');
    }
    out.push_str("]}");
    Ok(out.into_bytes())
}

fn encode_page(records: &[ExportRecord]) -> Result<(Vec<u8>, Vec<u8>), ExportError> {
    let mut documents = Vec::new();
    let mut history = Vec::new();
    for record in records {
        match record {
            ExportRecord::Document { collection, id, version, canonical_json } => {
                if canonical_json.len() > MAX_PAGE_BYTES {
                    return Err(ExportError::Source(ExportSourceError::InvalidPage(
                        "document exceeds page bound",
                    )));
                }
                append_json_line(
                    &mut documents,
                    collection,
                    id,
                    *version,
                    canonical_json,
                    None,
                    None,
                )?;
            }
            ExportRecord::History {
                collection,
                id,
                operation_id,
                revision,
                durability,
                deleted,
                canonical_json,
            } => {
                if canonical_json.as_ref().is_some_and(|bytes| bytes.len() > MAX_PAGE_BYTES) {
                    return Err(ExportError::Source(ExportSourceError::InvalidPage(
                        "history value exceeds page bound",
                    )));
                }
                append_json_line(
                    &mut history,
                    collection,
                    id,
                    *revision,
                    canonical_json.as_deref().unwrap_or(b"null"),
                    Some(*deleted),
                    Some((operation_id, *durability)),
                )?;
            }
        }
        if documents.len().saturating_add(history.len()) > MAX_PAGE_BYTES {
            return Err(ExportError::Source(ExportSourceError::InvalidPage(
                "page exceeds byte bound",
            )));
        }
    }
    Ok((documents, history))
}

fn append_json_line(
    output: &mut Vec<u8>,
    collection: &str,
    id: &str,
    version: u64,
    value: &[u8],
    deleted: Option<bool>,
    history: Option<(&str, terrabase::LocalDurability)>,
) -> Result<(), ExportError> {
    let mut line = String::new();
    line.push_str("{\"collection\":");
    append_json_string(&mut line, collection)?;
    line.push_str(",\"id\":");
    append_json_string(&mut line, id)?;
    line.push_str(",\"version\":");
    line.push_str(&version.to_string());
    if let Some(deleted) = deleted {
        line.push_str(",\"deleted\":");
        line.push_str(if deleted { "true" } else { "false" });
    }
    if let Some((operation_id, durability)) = history {
        line.push_str(",\"operation_id\":");
        append_json_string(&mut line, operation_id)?;
        line.push_str(",\"durability\":");
        append_json_string(&mut line, durability_name(durability))?;
    }
    line.push_str(",\"value\":");
    if value.is_empty() {
        return Err(ExportError::Source(ExportSourceError::InvalidPage("empty canonical JSON")));
    }
    // The adapter owns canonical JSON validation. This path only splices its
    // bytes and therefore never allocates a parsed JSON representation.
    line.push_str(std::str::from_utf8(value).map_err(|_| {
        ExportError::Source(ExportSourceError::InvalidPage("canonical JSON is not UTF-8"))
    })?);
    line.push_str("}\n");
    output.extend_from_slice(line.as_bytes());
    Ok(())
}

fn durability_name(durability: terrabase::LocalDurability) -> &'static str {
    match durability {
        terrabase::LocalDurability::Buffered => "buffered",
        terrabase::LocalDurability::Written => "written",
        terrabase::LocalDurability::Synced => "synced",
    }
}

fn append_json_string(out: &mut String, value: &str) -> Result<(), ExportError> {
    out.push('"');
    for character in value.chars() {
        match character {
            '"' => out.push_str("\\\""),
            '\\' => out.push_str("\\\\"),
            '\n' => out.push_str("\\n"),
            '\r' => out.push_str("\\r"),
            '\t' => out.push_str("\\t"),
            character if character.is_control() => {
                use std::fmt::Write as _;
                write!(out, "\\u{:04x}", character as u32)
                    .map_err(|_| ExportError::InvalidRequest("JSON encoding failed"))?;
            }
            character => out.push(character),
        }
    }
    out.push('"');
    Ok(())
}

fn validate_page(page: &ExportPage, phase: ExportPhase) -> Result<(), ExportError> {
    if page.records.len() > MAX_PAGE_RECORDS {
        return Err(ExportError::Source(ExportSourceError::InvalidPage("too many records")));
    }
    if page.next_cursor.as_ref().is_some_and(|cursor| cursor.len() > MAX_SOURCE_CURSOR_BYTES) {
        return Err(ExportError::Source(ExportSourceError::InvalidPage("cursor exceeds bound")));
    }
    if page.done && page.next_cursor.is_some() {
        return Err(ExportError::Source(ExportSourceError::InvalidPage(
            "terminal page has a cursor",
        )));
    }
    if phase == ExportPhase::Documents
        && page.records.iter().any(|record| !matches!(record, ExportRecord::Document { .. }))
    {
        return Err(ExportError::Source(ExportSourceError::InvalidPage(
            "document page contains history",
        )));
    }
    if phase == ExportPhase::History
        && page.records.iter().any(|record| !matches!(record, ExportRecord::History { .. }))
    {
        return Err(ExportError::Source(ExportSourceError::InvalidPage(
            "history page contains document",
        )));
    }
    Ok(())
}

fn validate_snapshot(snapshot: &ExportSnapshot) -> Result<(), ExportError> {
    if snapshot.database_id.trim().is_empty()
        || snapshot
            .schemas
            .iter()
            .chain(&snapshot.indexes)
            .chain(&snapshot.branches)
            .any(|member| member.bytes.len() > MAX_METADATA_BYTES)
    {
        return Err(ExportError::InvalidRequest("invalid snapshot metadata"));
    }
    Ok(())
}

fn validate_state(state: &ExportJobState) -> Result<(), ExportError> {
    if state.job_id.len() != 32 || !state.job_id.bytes().all(|byte| byte.is_ascii_hexdigit()) {
        return Err(ExportError::Io("durable export state is invalid".into()));
    }
    if state.database.trim().is_empty()
        || state.database.contains('/')
        || state.database.contains('\\')
        || state.database.chars().any(char::is_control)
        || state.placement.is_empty()
        || state.source_cursor.as_ref().is_some_and(|cursor| cursor.len() > MAX_SOURCE_CURSOR_BYTES)
    {
        return Err(ExportError::Io("durable export state is invalid".into()));
    }
    state.destination.validate()?;
    let placement = PlacementLock::new(state.placement.iter().copied())?;
    if !placement.allows(state.destination.region()) {
        return Err(ExportError::PlacementMismatch);
    }
    validate_snapshot(&state.snapshot)
}

fn validate_request(request: &ExportRequest) -> Result<(), ExportError> {
    if request.format != TransferFormat::Native && request.scope != ExportScope::Documents {
        return Err(ExportError::InvalidRequest(
            "converted export formats require documents-only scope",
        ));
    }
    if request.database.trim().is_empty()
        || request.database.contains('/')
        || request.database.contains('\\')
        || request.database.chars().any(char::is_control)
    {
        return Err(ExportError::InvalidRequest("database is invalid"));
    }
    request.destination.validate()?;
    if !request.placement.allows(request.destination.region()) {
        return Err(ExportError::PlacementMismatch);
    }
    if request
        .idempotency_key
        .as_deref()
        .is_some_and(|key| key.len() > 512 || key.chars().any(char::is_control))
    {
        return Err(ExportError::InvalidRequest("idempotency key is invalid"));
    }
    Ok(())
}

fn accepted(state: &ExportJobState) -> ExportAccepted {
    ExportAccepted {
        job_id: state.job_id.clone(),
        status_path: format!("/api/v1/databases/{}/exports/{}", state.database, state.job_id),
        resume_cursor: state.resume_cursor.clone(),
    }
}

fn job_id(request: &ExportRequest, key: &[u8; 32]) -> String {
    let mut hasher = blake3::Hasher::new_keyed(key);
    hasher.update(request.database.as_bytes());
    hasher.update(&[0]);
    if let Some(key) = &request.idempotency_key {
        hasher.update(key.as_bytes());
    } else {
        hasher.update(&NEXT_JOB.fetch_add(1, Ordering::Relaxed).to_be_bytes());
        hasher.update(
            &SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .unwrap_or_default()
                .as_nanos()
                .to_be_bytes(),
        );
    }
    hasher.finalize().to_hex()[..32].to_owned()
}

fn encode_cursor(
    key: &[u8; 32],
    job_id: &str,
    phase: ExportPhase,
    source: Option<&[u8]>,
) -> String {
    let phase = match phase {
        ExportPhase::Documents => 0,
        ExportPhase::History => 1,
        ExportPhase::Done => 2,
    };
    let source = source.unwrap_or_default();
    let mut payload = Vec::with_capacity(2 + source.len());
    payload.push(1);
    payload.push(phase);
    payload.extend_from_slice(source);
    let mut signed = Vec::with_capacity(job_id.len() + payload.len());
    signed.extend_from_slice(job_id.as_bytes());
    signed.extend_from_slice(&payload);
    let mac = blake3::keyed_hash(key, &signed);
    format!("ex1.{}.{}.{}", phase, hex::encode(source), hex::encode(&mac.as_bytes()[..16]))
}

fn decode_cursor(
    key: &[u8; 32],
    job_id: &str,
    token: &str,
) -> Result<(ExportPhase, Option<Vec<u8>>), ExportError> {
    if token.len() > 2 * MAX_SOURCE_CURSOR_BYTES + 64 {
        return Err(ExportError::InvalidRequest("resume cursor is too large"));
    }
    let mut parts = token.split('.');
    if parts.next() != Some("ex1") {
        return Err(ExportError::InvalidRequest("resume cursor is malformed"));
    }
    let phase = match parts.next() {
        Some("0") => ExportPhase::Documents,
        Some("1") => ExportPhase::History,
        Some("2") => ExportPhase::Done,
        _ => return Err(ExportError::InvalidRequest("resume cursor phase is invalid")),
    };
    let source =
        hex::decode(parts.next().ok_or(ExportError::InvalidRequest("resume cursor is malformed"))?)
            .map_err(|_| ExportError::InvalidRequest("resume cursor encoding is invalid"))?;
    if source.len() > MAX_SOURCE_CURSOR_BYTES {
        return Err(ExportError::InvalidRequest("resume cursor is too large"));
    }
    let provided =
        hex::decode(parts.next().ok_or(ExportError::InvalidRequest("resume cursor is malformed"))?)
            .map_err(|_| ExportError::InvalidRequest("resume cursor encoding is invalid"))?;
    if parts.next().is_some() || provided.len() != 16 {
        return Err(ExportError::InvalidRequest("resume cursor is malformed"));
    }
    let expected = encode_cursor(key, job_id, phase, Some(&source));
    if expected != token {
        return Err(ExportError::InvalidRequest("resume cursor authentication failed"));
    }
    Ok((phase, (!source.is_empty()).then_some(source)))
}

pub(crate) fn safe_member_path(path: &str) -> bool {
    let path = Path::new(path);
    !path.as_os_str().is_empty()
        && path.components().all(|component| matches!(component, Component::Normal(_)))
}

/// A crash-safe filesystem control store with OS-locked worker ownership.
/// The root must be a trusted directory on a filesystem supporting exclusive
/// file locks. Job lock files must never be removed while the store is in use.
pub struct FileExportStore {
    root: PathBuf,
}

impl FileExportStore {
    /// Opens a store rooted at `root`, creating it if necessary.
    ///
    /// # Errors
    ///
    /// Returns a filesystem error if creation or inspection fails, or refuses
    /// a root containing a symbolic link.
    pub fn open(root: impl Into<PathBuf>) -> Result<Self, ExportError> {
        let root = root.into();
        fs::create_dir_all(&root).map_err(|error| ExportError::Io(error.to_string()))?;
        ensure_no_symlink_components(&root)?;
        Ok(Self { root })
    }

    fn path(&self, job_id: &str) -> Result<PathBuf, ExportError> {
        if job_id.len() != 32 || !job_id.bytes().all(|byte| byte.is_ascii_hexdigit()) {
            return Err(ExportError::InvalidRequest("job identity is invalid"));
        }
        Ok(self.root.join(format!("{job_id}.json")))
    }
}

impl FileExportStore {
    fn publish(&self, state: &ExportJobState, replace: bool) -> Result<bool, ExportError> {
        let path = self.path(&state.job_id)?;
        let tmp = path
            .with_extension(format!("json.tmp.{}", terrabase::identity::DatabaseUlid::generate()));
        let bytes =
            serde_json::to_vec(state).map_err(|error| ExportError::Io(error.to_string()))?;
        let mut file = OpenOptions::new()
            .create_new(true)
            .write(true)
            .open(&tmp)
            .map_err(|error| ExportError::Io(error.to_string()))?;
        let result = (|| -> io::Result<bool> {
            file.write_all(&bytes)?;
            file.sync_all()?;
            drop(file);
            let created = if replace {
                fs::rename(&tmp, &path)?;
                true
            } else {
                match fs::hard_link(&tmp, &path) {
                    Ok(()) => true,
                    Err(error) if error.kind() == io::ErrorKind::AlreadyExists => false,
                    Err(error) => return Err(error),
                }
            };
            if !replace {
                fs::remove_file(&tmp)?;
            }
            // The new contents and the directory entry must both survive
            // a crash before the accepted state can be reported as durable.
            sync_directory_ancestors(&self.root)?;
            Ok(created)
        })();
        if result.is_err() {
            // A rename followed by failed directory sync leaves the installed
            // record intact; only this save's unpublished temporary is removed.
            let _ = fs::remove_file(&tmp);
        }
        result.map_err(|error| ExportError::Io(error.to_string()))
    }
}

impl ExportStore for FileExportStore {
    fn list(&self, after: Option<&str>, limit: usize) -> Result<Vec<String>, ExportError> {
        if !(1..=100).contains(&limit) {
            return Err(ExportError::InvalidRequest("invalid export page limit"));
        }
        if let Some(after) = after {
            self.path(after)?;
        }
        let mut jobs = BTreeSet::new();
        for entry in fs::read_dir(&self.root).map_err(|error| ExportError::Io(error.to_string()))? {
            let entry = entry.map_err(|error| ExportError::Io(error.to_string()))?;
            let path = entry.path();
            if path.extension().is_none_or(|extension| extension != "json") {
                continue;
            }
            let Some(job) = path.file_stem().and_then(|value| value.to_str()) else {
                continue;
            };
            self.path(job)?;
            if after.is_some_and(|after| job <= after) {
                continue;
            }
            jobs.insert(job.to_owned());
            if jobs.len() > limit {
                jobs.pop_last();
            }
        }
        Ok(jobs.into_iter().collect())
    }

    fn request_cancel(&self, job_id: &str) -> Result<bool, ExportError> {
        let Some(state) = self.load(job_id)? else {
            return Ok(false);
        };
        if matches!(state.status, JobStatus::Complete | JobStatus::Cancelled) {
            return Ok(false);
        }
        publish_artifact(&self.path(job_id)?.with_extension("cancel"), b"cancel\n")?;
        Ok(true)
    }

    fn cancel_requested(&self, job_id: &str) -> Result<bool, ExportError> {
        let path = self.path(job_id)?.with_extension("cancel");
        ensure_no_symlink_components(&path)?;
        match fs::symlink_metadata(path) {
            Ok(metadata) if metadata.is_file() && metadata.len() == 7 => Ok(true),
            Ok(_) => Err(ExportError::IdempotencyConflict),
            Err(error) if error.kind() == io::ErrorKind::NotFound => Ok(false),
            Err(error) => Err(ExportError::Io(error.to_string())),
        }
    }

    fn clear_cancel(&self, job_id: &str) -> Result<(), ExportError> {
        let path = self.path(job_id)?.with_extension("cancel");
        ensure_no_symlink_components(&path)?;
        match fs::remove_file(path) {
            Ok(()) => (),
            Err(error) if error.kind() == io::ErrorKind::NotFound => return Ok(()),
            Err(error) => return Err(ExportError::Io(error.to_string())),
        }
        sync_directory_ancestors(&self.root).map_err(|error| ExportError::Io(error.to_string()))
    }

    fn acquire_job(&self, job_id: &str) -> Result<Option<Box<dyn ExportJobLease>>, ExportError> {
        let path = self.path(job_id)?.with_extension("lock");
        ensure_no_symlink_components(&path)?;
        let mut options = OpenOptions::new();
        options.read(true).write(true).create(true).truncate(false);
        #[cfg(target_os = "linux")]
        {
            use std::os::unix::fs::OpenOptionsExt;
            // Linux O_NOFOLLOW also refuses a last-component symlink introduced
            // between the path inspection and open. The store root is trusted.
            options.mode(0o600).custom_flags(0o400_000);
        }
        let file = options.open(path).map_err(|error| ExportError::Io(error.to_string()))?;
        if !file.metadata().map_err(|error| ExportError::Io(error.to_string()))?.is_file() {
            return Err(ExportError::DestinationUnavailable);
        }
        match file.try_lock() {
            Ok(()) => Ok(Some(Box::new(file))),
            Err(fs::TryLockError::WouldBlock) => Ok(None),
            Err(fs::TryLockError::Error(error)) => Err(ExportError::Io(error.to_string())),
        }
        // Never unlink a lock file: a later opener must lock the same inode.
    }

    fn create(&self, state: &ExportJobState) -> Result<ExportJobState, ExportError> {
        if self.publish(state, false)? {
            Ok(state.clone())
        } else {
            self.load(&state.job_id)?
                .ok_or_else(|| ExportError::Io("admitted job disappeared".into()))
        }
    }

    fn load(&self, job_id: &str) -> Result<Option<ExportJobState>, ExportError> {
        let path = self.path(job_id)?;
        match fs::read(path) {
            Ok(bytes) => serde_json::from_slice(&bytes)
                .map(Some)
                .map_err(|error| ExportError::Io(error.to_string())),
            Err(error) if error.kind() == io::ErrorKind::NotFound => Ok(None),
            Err(error) => Err(ExportError::Io(error.to_string())),
        }
    }

    fn save(&self, state: &ExportJobState) -> Result<(), ExportError> {
        self.publish(state, true).map(|_| ())
    }
}

/// In-memory store used only by this module's focused tests.
#[cfg(test)]
#[doc(hidden)]
#[derive(Default)]
pub struct MemoryExportStore {
    states: Mutex<std::collections::BTreeMap<String, ExportJobState>>,
    jobs: Arc<Mutex<BTreeSet<String>>>,
    cancellations: Mutex<BTreeSet<String>>,
}

#[cfg(test)]
impl ExportStore for MemoryExportStore {
    fn list(&self, after: Option<&str>, limit: usize) -> Result<Vec<String>, ExportError> {
        if !(1..=100).contains(&limit) {
            return Err(ExportError::InvalidRequest("invalid export page limit"));
        }
        Ok(self
            .states
            .lock()
            .map_err(|_| ExportError::Io("store lock poisoned".into()))?
            .iter()
            .filter(|(job, _)| after.is_none_or(|after| job.as_str() > after))
            .take(limit)
            .map(|(job, _)| job.clone())
            .collect())
    }

    fn request_cancel(&self, job_id: &str) -> Result<bool, ExportError> {
        let states =
            self.states.lock().map_err(|_| ExportError::Io("store lock poisoned".into()))?;
        let Some(state) = states.get(job_id) else {
            return Ok(false);
        };
        if matches!(state.status, JobStatus::Complete | JobStatus::Cancelled) {
            return Ok(false);
        }
        self.cancellations
            .lock()
            .map_err(|_| ExportError::Io("cancel lock poisoned".into()))?
            .insert(job_id.into());
        Ok(true)
    }

    fn cancel_requested(&self, job_id: &str) -> Result<bool, ExportError> {
        Ok(self
            .cancellations
            .lock()
            .map_err(|_| ExportError::Io("cancel lock poisoned".into()))?
            .contains(job_id))
    }

    fn clear_cancel(&self, job_id: &str) -> Result<(), ExportError> {
        self.cancellations
            .lock()
            .map_err(|_| ExportError::Io("cancel lock poisoned".into()))?
            .remove(job_id);
        Ok(())
    }

    fn acquire_job(&self, job_id: &str) -> Result<Option<Box<dyn ExportJobLease>>, ExportError> {
        Ok(ActiveJob::acquire(Arc::clone(&self.jobs), job_id)?
            .map(|lease| Box::new(lease) as Box<dyn ExportJobLease>))
    }

    fn create(&self, state: &ExportJobState) -> Result<ExportJobState, ExportError> {
        let mut states =
            self.states.lock().map_err(|_| ExportError::Io("store lock poisoned".into()))?;
        Ok(states.entry(state.job_id.clone()).or_insert_with(|| state.clone()).clone())
    }

    fn load(&self, job_id: &str) -> Result<Option<ExportJobState>, ExportError> {
        Ok(self
            .states
            .lock()
            .map_err(|_| ExportError::Io("store lock poisoned".into()))?
            .get(job_id)
            .cloned())
    }

    fn save(&self, state: &ExportJobState) -> Result<(), ExportError> {
        self.states
            .lock()
            .map_err(|_| ExportError::Io("store lock poisoned".into()))?
            .insert(state.job_id.clone(), state.clone());
        Ok(())
    }
}

/// Publishes immutable bytes without replacing a concurrent winner.
pub(crate) fn publish_artifact(path: &Path, bytes: &[u8]) -> Result<bool, ExportError> {
    let parent = path.parent().ok_or(ExportError::DestinationUnavailable)?;
    ensure_no_symlink_components(parent)?;
    fs::create_dir_all(parent).map_err(|error| ExportError::Io(error.to_string()))?;
    ensure_no_symlink_components(parent)?;
    let tmp =
        path.with_extension(format!("{}.part", terrabase::identity::DatabaseUlid::generate()));
    let mut file = OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(&tmp)
        .map_err(|error| ExportError::Io(error.to_string()))?;
    let result = (|| -> io::Result<bool> {
        file.write_all(bytes)?;
        file.sync_all()?;
        drop(file);
        let created = match fs::hard_link(&tmp, path) {
            Ok(()) => true,
            Err(error) if error.kind() == io::ErrorKind::AlreadyExists => false,
            Err(error) => return Err(error),
        };
        fs::remove_file(&tmp)?;
        sync_directory_ancestors(parent)?;
        Ok(created)
    })();
    if result.is_err() {
        let _ = fs::remove_file(&tmp);
    }
    result.map_err(|error| ExportError::Io(error.to_string()))
}

pub(crate) fn sync_directory_ancestors(directory: &Path) -> io::Result<()> {
    // Includes newly created documents/history and job directories, so a
    // durable cursor can never outlive the directory entry containing its page.
    for ancestor in fs::canonicalize(directory)?.ancestors() {
        File::open(ancestor)?.sync_all()?;
    }
    Ok(())
}

pub(crate) fn ensure_no_symlink_components(path: &Path) -> Result<(), ExportError> {
    let mut current = PathBuf::new();
    for component in path.components() {
        current.push(component.as_os_str());
        match fs::symlink_metadata(&current) {
            Ok(metadata) if metadata.file_type().is_symlink() => {
                return Err(ExportError::DestinationUnavailable);
            }
            Ok(_) => {}
            Err(error) if error.kind() == io::ErrorKind::NotFound => {}
            Err(_) => return Err(ExportError::DestinationUnavailable),
        }
    }
    Ok(())
}

pub(crate) fn fact(path: &str, bytes: &[u8]) -> ArtifactFact {
    ArtifactFact {
        path: path.to_owned(),
        length: bytes.len() as u64,
        checksum: blake3::hash(bytes).to_hex().to_string(),
    }
}

pub(crate) fn error_status(error: &ExportError) -> StatusCode {
    match error {
        ExportError::PlacementMismatch => StatusCode::MISDIRECTED_REQUEST,
        ExportError::EncryptionUnavailable
        | ExportError::DependencyUnavailable
        | ExportError::DestinationUnavailable => StatusCode::SERVICE_UNAVAILABLE,
        ExportError::IdempotencyConflict => StatusCode::CONFLICT,
        ExportError::InvalidRequest(_) => StatusCode::UNPROCESSABLE_ENTITY,
        ExportError::Io(_) | ExportError::Source(_) => StatusCode::INTERNAL_SERVER_ERROR,
    }
}

pub(crate) fn json_response<T: Serialize>(status: StatusCode, value: &T) -> HttpResponse<Body> {
    match serde_json::to_vec(value) {
        Ok(bytes) => HttpResponse::builder()
            .status(status)
            .header(header::CONTENT_TYPE, "application/json")
            .header(header::CACHE_CONTROL, "private, no-store")
            .body(Body::from(bytes))
            .expect("valid response"),
        Err(_) => http_error(StatusCode::INTERNAL_SERVER_ERROR, "export response encoding failed"),
    }
}

fn http_error(status: StatusCode, message: &str) -> HttpResponse<Body> {
    let body =
        format!("{{\"error\":{{\"code\":\"export_failed\",\"message\":{}}}}}", json_quote(message));
    HttpResponse::builder()
        .status(status)
        .header(header::CONTENT_TYPE, "application/json")
        .header(header::CACHE_CONTROL, "private, no-store")
        .body(Body::from(body))
        .expect("valid response")
}

fn json_quote(value: &str) -> String {
    let mut out = String::new();
    let _ = append_json_string(&mut out, value);
    out
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::atomic::AtomicUsize;

    #[test]
    fn file_store_reopens_saved_state_and_cleans_failed_publication() {
        let root = std::env::temp_dir().join(format!(
            "terrabase-export-store-{}",
            terrabase::identity::DatabaseUlid::generate()
        ));
        let store = FileExportStore::open(&root).expect("store");
        let state = stored_job(&root);
        store.save(&state).expect("durable save");
        drop(store);
        let store = FileExportStore::open(&root).expect("reopen");
        assert_eq!(store.load(&state.job_id).expect("load"), Some(state.clone()));
        let path = store.path(&state.job_id).expect("job path");
        fs::remove_file(&path).expect("prepare rename failure");
        fs::create_dir(&path).expect("occupied destination");
        fs::write(path.join("sentinel"), b"preserve").expect("sentinel");
        assert!(store.save(&state).is_err());
        assert_eq!(fs::read(path.join("sentinel")).expect("destination survives"), b"preserve");
        assert_eq!(
            fs::read_dir(&root).expect("store entries").count(),
            1,
            "failed publication removes only its temporary file"
        );
        fs::remove_dir_all(root).expect("cleanup");
    }

    fn stored_job(root: &Path) -> ExportJobState {
        ExportJobState {
            job_id: "a".repeat(32),
            database: "db".into(),
            destination: ExportDestination::File {
                root: root.join("artifacts"),
                region: RegionCode::GRU,
            },
            format: TransferFormat::Jsonl,
            scope: ExportScope::All,
            placement: vec![RegionCode::GRU],
            snapshot: Source { pages: Mutex::new(Vec::new()) }.snapshot("db").expect("snapshot"),
            phase: ExportPhase::Documents,
            source_cursor: None,
            records: 0,
            pages: 0,
            status: JobStatus::Accepted,
            resume_cursor: "cursor".into(),
            manifest: None,
            error: None,
        }
    }

    #[test]
    fn concurrent_file_admission_preserves_one_snapshot_and_existing_progress() {
        let root = std::env::temp_dir().join(format!(
            "terrabase-export-admission-{}",
            terrabase::identity::DatabaseUlid::generate()
        ));
        FileExportStore::open(&root).expect("store");
        let barrier = std::sync::Barrier::new(8);
        let admitted = std::thread::scope(|threads| {
            let handles = (0..8)
                .map(|boundary| {
                    let root = &root;
                    let barrier = &barrier;
                    threads.spawn(move || {
                        let store = FileExportStore::open(root).expect("independent store");
                        let mut proposed = stored_job(root);
                        proposed.snapshot.boundary = boundary;
                        barrier.wait();
                        store.create(&proposed).expect("atomic admission")
                    })
                })
                .collect::<Vec<_>>();
            handles.into_iter().map(|handle| handle.join().expect("submitter")).collect::<Vec<_>>()
        });
        assert!(admitted.iter().all(|state| state == &admitted[0]), "every caller uses the winner");
        let store = FileExportStore::open(&root).expect("reopen winner");
        let mut running = admitted[0].clone();
        running.status = JobStatus::Running;
        running.pages = 4;
        running.records = 100;
        store.save(&running).expect("worker progress");
        assert_eq!(store.create(&stored_job(&root)).expect("late admission"), running);
        assert_eq!(store.load(&running.job_id).expect("durable progress"), Some(running));
        assert_eq!(
            fs::read_dir(&root).expect("entries").count(),
            1,
            "temporary records are cleaned"
        );
        fs::remove_dir_all(root).expect("cleanup");
    }

    struct Source {
        pages: Mutex<Vec<ExportPage>>,
    }

    impl ExportSource for Source {
        fn snapshot(&self, _database: &str) -> Result<ExportSnapshot, ExportSourceError> {
            Ok(ExportSnapshot {
                captured_at_millis: 0,
                history_start: 0,
                boundary: 7,
                database_id: "db-1".into(),
                schemas: vec![MetadataMember {
                    name: "schema.json".into(),
                    bytes: br#"{\"v\":1}"#.to_vec(),
                }],
                indexes: Vec::new(),
                branches: Vec::new(),
                estimated_records: Some(2),
            })
        }

        fn page(
            &self,
            _database: &str,
            _snapshot: &ExportSnapshot,
            phase: ExportPhase,
            _after: Option<&[u8]>,
            _limit: usize,
        ) -> Result<ExportPage, ExportSourceError> {
            if phase == ExportPhase::History {
                return Ok(ExportPage { records: Vec::new(), next_cursor: None, done: true });
            }
            self.pages
                .lock()
                .expect("pages")
                .pop()
                .ok_or_else(|| ExportSourceError::Storage("no page".into()))
        }
    }

    struct Writer {
        members: Mutex<Vec<(String, Vec<u8>)>>,
        committed: AtomicUsize,
    }
    impl ArtifactWriter for Writer {
        fn put<'a>(
            &'a mut self,
            path: &'a str,
            plaintext: &'a [u8],
        ) -> ExportFuture<'a, ArtifactFact> {
            Box::pin(async move {
                let mut members = self.members.lock().expect("members");
                if let Some((_, existing)) = members.iter().find(|(name, _)| name == path) {
                    if existing != plaintext {
                        return Err(ExportError::IdempotencyConflict);
                    }
                } else {
                    members.push((path.into(), plaintext.to_vec()));
                }
                Ok(fact(path, plaintext))
            })
        }
        fn existing_facts(&mut self) -> ExportFuture<'_, Vec<ArtifactFact>> {
            Box::pin(async move {
                Ok(self
                    .members
                    .lock()
                    .expect("members")
                    .iter()
                    .map(|(path, bytes)| fact(path, bytes))
                    .collect())
            })
        }
        fn commit(self: Box<Self>, _manifest: &ArtifactFact) -> ExportFuture<'static, ()> {
            Box::pin(async move {
                self.committed.fetch_add(1, Ordering::Relaxed);
                Ok(())
            })
        }
    }
    struct Port {
        writer: Arc<Mutex<Option<Writer>>>,
        opens: AtomicUsize,
    }
    impl ArtifactPort for Port {
        fn attestation(&self) -> Option<EncryptionAttestation> {
            Some(EncryptionAttestation { provider: "test".into(), key_version: "v1".into() })
        }
        fn open<'a>(
            &'a self,
            _job: &'a str,
            _database: &'a str,
            destination: &'a ExportDestination,
            lock: &'a PlacementLock,
        ) -> ExportFuture<'a, Box<dyn ArtifactWriter>> {
            Box::pin(async move {
                if !lock.allows(destination.region()) {
                    return Err(ExportError::PlacementMismatch);
                }
                self.opens.fetch_add(1, Ordering::Relaxed);
                Ok(Box::new(self.writer.lock().expect("writer").take().expect("writer"))
                    as Box<dyn ArtifactWriter>)
            })
        }
    }

    struct UnknownProvider {
        opens: AtomicUsize,
    }

    impl ArtifactPort for UnknownProvider {
        fn attestation(&self) -> Option<EncryptionAttestation> {
            Some(EncryptionAttestation { provider: "test".into(), key_version: "v1".into() })
        }

        fn open<'a>(
            &'a self,
            _job: &'a str,
            _database: &'a str,
            _destination: &'a ExportDestination,
            _placement: &'a PlacementLock,
        ) -> ExportFuture<'a, Box<dyn ArtifactWriter>> {
            self.opens.fetch_add(1, Ordering::Relaxed);
            Box::pin(async { Err(ExportError::DependencyUnavailable) })
        }
    }

    fn service(source: Source, port: Port) -> ExportService {
        ExportService::new(
            Some(Arc::new(source)),
            Some(Arc::new(MemoryExportStore::default())),
            Some(Arc::new(port)),
            [9; 32],
        )
        .expect("service")
    }

    #[tokio::test]
    async fn failed_export_resumes_without_losing_persisted_pages() {
        let root = std::env::temp_dir().join(format!(
            "terrabase-export-resume-{}",
            terrabase::identity::DatabaseUlid::generate()
        ));
        let state = stored_job(&root);
        let page = |id: &str, done| ExportPage {
            records: vec![ExportRecord::Document {
                collection: "docs".into(),
                id: id.into(),
                version: 1,
                canonical_json: br#"{"n":1}"#.to_vec(),
            }],
            next_cursor: if done { None } else { Some(vec![1]) },
            done,
        };
        let source = Arc::new(Source { pages: Mutex::new(vec![page("first", false)]) });
        let store = Arc::new(MemoryExportStore::default());
        store.create(&state).expect("admit");
        let port = crate::export_local::local_artifacts(&root.join("artifacts"), "db-1", "tenant")
            .expect("artifact port");
        let worker = Worker {
            stop_requested: Arc::new(AtomicBool::new(false)),
            source: source.clone(),
            store: store.clone(),
            artifacts: port,
            cursor_key: [9; 32],
        };
        assert!(worker.run(&state.job_id).await.is_err(), "source fails after its first page");
        let failed = store.load(&state.job_id).expect("load").expect("job");
        assert_eq!(failed.status, JobStatus::Failed);
        assert_eq!(failed.error.as_deref(), Some("export source operation failed"));
        assert_eq!((failed.pages, failed.records), (1, 1));
        assert_eq!(failed.source_cursor, Some(vec![1]));
        let directory = root.join("artifacts/db").join(&state.job_id);
        let first_path = directory.join("documents/000000000000.jsonl");
        let first = fs::read(&first_path).expect("persisted page survives failure");
        let premature = worker
            .artifacts
            .open(
                &state.job_id,
                &state.database,
                &state.destination,
                &PlacementLock::single(RegionCode::GRU),
            )
            .await
            .expect("reopen staging");
        let fabricated =
            ArtifactFact { path: "manifest.json".into(), length: 0, checksum: "0".repeat(64) };
        assert!(
            premature.commit(&fabricated).await.is_err(),
            "a missing manifest cannot be committed"
        );
        assert!(!directory.join("COMMITTED").exists());

        source.pages.lock().expect("source").push(page("second", true));
        worker.run(&state.job_id).await.expect("resume");
        let complete = store.load(&state.job_id).expect("load").expect("job");
        assert_eq!(complete.status, JobStatus::Complete);
        assert_eq!((complete.pages, complete.records), (2, 2));
        assert_eq!(fs::read(first_path).expect("original page"), first);
        assert!(directory.join("documents/000000000001.jsonl").is_file());
        assert!(directory.join("COMMITTED").is_file());
        fs::remove_dir_all(root).expect("cleanup");
    }

    #[tokio::test]
    async fn unknown_provider_result_keeps_export_pending_for_reconciliation() {
        let root = std::env::temp_dir().join(format!(
            "terrabase-export-provider-{}",
            terrabase::identity::DatabaseUlid::generate()
        ));
        let source = Arc::new(Source { pages: Mutex::new(Vec::new()) });
        let store = Arc::new(MemoryExportStore::default());
        let provider = Arc::new(UnknownProvider { opens: AtomicUsize::new(0) });
        let service =
            ExportService::new(Some(source), Some(store.clone()), Some(provider.clone()), [9; 32])
                .expect("service");
        let accepted = service
            .submit(ExportRequest {
                database: "db".into(),
                destination: ExportDestination::File {
                    root: root.join("artifacts"),
                    region: RegionCode::GRU,
                },
                format: TransferFormat::Jsonl,
                scope: ExportScope::All,
                placement: PlacementLock::single(RegionCode::GRU),
                idempotency_key: Some("provider-timeout".into()),
            })
            .await
            .expect("admitted");
        tokio::time::timeout(std::time::Duration::from_secs(1), async {
            loop {
                if provider.opens.load(Ordering::Relaxed) >= MAX_PROVIDER_RETRIES
                    && store.load(&accepted.job_id).expect("load").expect("job").status
                        == JobStatus::Accepted
                {
                    break;
                }
                tokio::time::sleep(std::time::Duration::from_millis(5)).await;
            }
        })
        .await
        .expect("provider result settles as pending");
        let state = store.load(&accepted.job_id).expect("load").expect("job");
        assert_eq!(state.status, JobStatus::Accepted);
        assert!(state.error.is_none());
        assert!(service.status_async(&accepted.job_id).await.expect("status").is_some());
        fs::remove_dir_all(root).expect("cleanup");
    }

    #[test]
    fn concurrent_artifact_publication_never_replaces_the_winner() {
        let root = std::env::temp_dir().join(format!(
            "terrabase-artifact-publish-{}",
            terrabase::identity::DatabaseUlid::generate()
        ));
        let target = root.join("nested/page.jsonl");
        let barrier = std::sync::Barrier::new(8);
        let outcomes = std::thread::scope(|threads| {
            let handles = (0u8..8)
                .map(|byte| {
                    let target = &target;
                    let barrier = &barrier;
                    threads.spawn(move || {
                        barrier.wait();
                        (byte, publish_artifact(target, &[byte]).expect("publication"))
                    })
                })
                .collect::<Vec<_>>();
            handles.into_iter().map(|handle| handle.join().expect("publisher")).collect::<Vec<_>>()
        });
        let winners = outcomes.iter().filter(|(_, created)| *created).collect::<Vec<_>>();
        assert_eq!(winners.len(), 1);
        assert_eq!(fs::read(&target).expect("winning bytes"), vec![winners[0].0]);
        assert!(!publish_artifact(&target, b"replacement").expect("later publisher"));
        assert_eq!(fs::read(&target).expect("still immutable"), vec![winners[0].0]);
        assert_eq!(fs::read_dir(target.parent().expect("parent")).expect("entries").count(), 1);
        fs::remove_dir_all(root).expect("cleanup");
    }

    const SENSITIVE_PROVIDER_DETAIL: &str =
        "credential=private-token path=/private/customer/database";

    struct SensitiveSource;
    impl ExportSource for SensitiveSource {
        fn snapshot(&self, _database: &str) -> Result<ExportSnapshot, ExportSourceError> {
            Err(ExportSourceError::Storage(SENSITIVE_PROVIDER_DETAIL.into()))
        }
        fn page(
            &self,
            _database: &str,
            _snapshot: &ExportSnapshot,
            _phase: ExportPhase,
            _after: Option<&[u8]>,
            _limit: usize,
        ) -> Result<ExportPage, ExportSourceError> {
            Err(ExportSourceError::Storage(SENSITIVE_PROVIDER_DETAIL.into()))
        }
    }

    #[tokio::test]
    async fn export_http_redacts_provider_errors_and_legacy_job_diagnostics() {
        let mut service = service(
            Source { pages: Mutex::new(Vec::new()) },
            Port { writer: Arc::new(Mutex::new(None)), opens: AtomicUsize::new(0) },
        );
        service.source = Arc::new(SensitiveSource);
        let mut state = stored_job(Path::new("/tmp/export-redaction"));
        state.status = JobStatus::Failed;
        state.error = Some(SENSITIVE_PROVIDER_DETAIL.into());
        service.store.save(&state).expect("legacy status");
        let error = service
            .submit(ExportRequest {
                database: state.database.clone(),
                destination: state.destination.clone(),
                format: TransferFormat::Jsonl,
                scope: ExportScope::All,
                placement: PlacementLock::single(RegionCode::GRU),
                idempotency_key: None,
            })
            .await
            .expect_err("source fails");
        assert_eq!(error.public_message(), "export source operation failed");
        let status = service.status_async(&state.job_id).await.expect("status").expect("job");
        let text = serde_json::to_string(&status).expect("JSON");
        assert!(text.contains("export failed; retry with the latest resume cursor"));
        assert!(!text.contains("private-token") && !text.contains("/private/customer"));
    }

    #[derive(Default)]
    struct CountingStore {
        inner: MemoryExportStore,
        loads: AtomicUsize,
    }

    impl ExportStore for CountingStore {
        fn acquire_job(
            &self,
            job_id: &str,
        ) -> Result<Option<Box<dyn ExportJobLease>>, ExportError> {
            self.inner.acquire_job(job_id)
        }
        fn create(&self, state: &ExportJobState) -> Result<ExportJobState, ExportError> {
            self.inner.create(state)
        }
        fn load(&self, job_id: &str) -> Result<Option<ExportJobState>, ExportError> {
            self.loads.fetch_add(1, Ordering::SeqCst);
            self.inner.load(job_id)
        }
        fn save(&self, state: &ExportJobState) -> Result<(), ExportError> {
            self.inner.save(state)
        }
    }

    struct ThreadCheckedStore {
        runtime_thread: std::thread::ThreadId,
        loads: AtomicUsize,
    }

    impl ExportStore for ThreadCheckedStore {
        fn acquire_job(
            &self,
            _job_id: &str,
        ) -> Result<Option<Box<dyn ExportJobLease>>, ExportError> {
            panic!("the failing source must prevent admission");
        }
        fn create(&self, _state: &ExportJobState) -> Result<ExportJobState, ExportError> {
            panic!("the failing source must prevent admission");
        }

        fn load(&self, _job_id: &str) -> Result<Option<ExportJobState>, ExportError> {
            assert_ne!(std::thread::current().id(), self.runtime_thread);
            self.loads.fetch_add(1, Ordering::SeqCst);
            Ok(None)
        }

        fn save(&self, _state: &ExportJobState) -> Result<(), ExportError> {
            panic!("the failing source must prevent admission");
        }
    }

    #[tokio::test(flavor = "current_thread")]
    async fn export_requests_keep_store_io_off_the_runtime_thread() {
        let mut service = service(
            Source { pages: Mutex::new(Vec::new()) },
            Port { writer: Arc::new(Mutex::new(None)), opens: AtomicUsize::new(0) },
        );
        let store = Arc::new(ThreadCheckedStore {
            runtime_thread: std::thread::current().id(),
            loads: AtomicUsize::new(0),
        });
        service.store = store.clone();
        service.source = Arc::new(SensitiveSource);
        let state = stored_job(Path::new("/tmp/export-runtime"));
        assert!(matches!(
            service
                .submit(ExportRequest {
                    database: state.database,
                    destination: state.destination,
                    format: TransferFormat::Jsonl,
                    scope: ExportScope::All,
                    placement: PlacementLock::single(RegionCode::GRU),
                    idempotency_key: None,
                })
                .await,
            Err(ExportError::Source(_))
        ));
        assert!(matches!(
            service.resume(&state.job_id, "cursor").await,
            Err(ExportError::InvalidRequest("unknown job"))
        ));
        assert!(service.status_async(&state.job_id).await.expect("status").is_none());
        assert_eq!(store.loads.load(Ordering::SeqCst), 3);
    }

    #[test]
    fn file_job_ownership_survives_contention_and_releases_on_process_death() {
        const CHILD_ROOT: &str = "TERRABASE_EXPORT_LEASE_TEST_ROOT";
        struct Child(std::process::Child);
        impl Drop for Child {
            fn drop(&mut self) {
                let _ = self.0.kill();
                let _ = self.0.wait();
            }
        }

        let job_id = "a".repeat(32);
        if let Some(root) = std::env::var_os(CHILD_ROOT) {
            let root = PathBuf::from(root);
            let store = FileExportStore::open(&root).expect("child store");
            let _lease = store.acquire_job(&job_id).expect("child lock").expect("unowned job");
            fs::write(root.join("ready"), b"locked").expect("signal ownership");
            loop {
                std::thread::park();
            }
        }

        let root = std::env::temp_dir().join(format!(
            "terrabase-export-lease-{}",
            terrabase::identity::DatabaseUlid::generate()
        ));
        let store = FileExportStore::open(&root).expect("parent store");
        let mut child = Child(
            std::process::Command::new(std::env::current_exe().expect("test binary"))
                .args([
                    "--exact",
                    "export_api::tests::file_job_ownership_survives_contention_and_releases_on_process_death",
                ])
                .env(CHILD_ROOT, &root)
                .stdout(std::process::Stdio::null())
                .spawn()
                .expect("child worker"),
        );
        let deadline = std::time::Instant::now() + std::time::Duration::from_secs(10);
        while !root.join("ready").exists() {
            assert!(child.0.try_wait().expect("child status").is_none(), "child exited early");
            assert!(std::time::Instant::now() < deadline, "child failed to acquire ownership");
            std::thread::sleep(std::time::Duration::from_millis(10));
        }
        assert!(store.acquire_job(&job_id).expect("contending lock").is_none());
        let other = store.acquire_job(&"b".repeat(32)).expect("different job");
        assert!(other.is_some(), "ownership is job-scoped");
        drop(child);
        let lease = store.acquire_job(&job_id).expect("after crash").expect("released by OS");
        let reopened = FileExportStore::open(&root).expect("independent handle");
        assert!(reopened.acquire_job(&job_id).expect("same-process contender").is_none());
        drop(lease);
        assert!(root.join(format!("{job_id}.lock")).is_file(), "lock inode must remain");
        assert!(reopened.acquire_job(&job_id).expect("after drop").is_some());
        drop(other);
        fs::remove_dir_all(root).expect("cleanup");
    }

    #[tokio::test]
    async fn worker_does_not_read_or_mutate_a_job_owned_by_another_worker() {
        let mut service = service(
            Source { pages: Mutex::new(Vec::new()) },
            Port { writer: Arc::new(Mutex::new(None)), opens: AtomicUsize::new(0) },
        );
        let store = Arc::new(CountingStore::default());
        service.store = store.clone();
        let lease = store.acquire_job("job").expect("lock").expect("unowned");
        let worker = service.clone_for_worker();
        worker.run("job").await.expect("another worker owns the job");
        assert_eq!(store.loads.load(Ordering::SeqCst), 0);
        drop(lease);
        assert!(worker.run("job").await.is_err(), "unowned missing job is read and refused");
        assert_eq!(store.loads.load(Ordering::SeqCst), 1);
    }

    #[test]
    fn duplicate_jobs_reserve_before_entering_a_busy_worker_pool() {
        let runtime = tokio::runtime::Builder::new_current_thread()
            .enable_all()
            .max_blocking_threads(1)
            .build()
            .expect("runtime");
        let mut service = service(
            Source { pages: Mutex::new(Vec::new()) },
            Port { writer: Arc::new(Mutex::new(None)), opens: AtomicUsize::new(0) },
        );
        let store = Arc::new(CountingStore::default());
        service.store = store.clone();
        runtime.block_on(async {
            let (started_tx, started_rx) = std::sync::mpsc::channel();
            let (release_tx, release_rx) = std::sync::mpsc::channel();
            let blocker = tokio::task::spawn_blocking(move || {
                started_tx.send(()).expect("pool occupied");
                let _ = release_rx.recv();
            });
            started_rx.recv().expect("blocking worker started");
            for _ in 0..128 {
                service.spawn_worker("same-job").expect("schedule duplicate");
            }
            release_tx.send(()).expect("release pool");
            blocker.await.expect("blocker finished");
            tokio::task::spawn_blocking(|| {}).await.expect("queued workers drained");
            assert_eq!(
                store.loads.load(Ordering::SeqCst),
                1,
                "only one worker may execute for the queued job"
            );
            // The missing durable job makes the worker fail. Its reservation
            // must still be released so a subsequent attempt can execute.
            service.spawn_worker("same-job").expect("retry failed job");
            tokio::task::spawn_blocking(|| {}).await.expect("retry drained");
            assert_eq!(store.loads.load(Ordering::SeqCst), 2);
        });
    }

    #[test]
    fn cursor_is_opaque_authenticated_and_job_scoped() {
        let source = Source { pages: Mutex::new(Vec::new()) };
        let port = Port {
            writer: Arc::new(Mutex::new(Some(Writer {
                members: Mutex::new(Vec::new()),
                committed: AtomicUsize::new(0),
            }))),
            opens: AtomicUsize::new(0),
        };
        let service = service(source, port);
        let token = service.cursor("job", ExportPhase::Documents, Some(b"cursor"));
        assert_eq!(
            service.decode_resume_cursor("job", &token).expect("cursor").1,
            Some(b"cursor".to_vec())
        );
        assert!(service.decode_resume_cursor("other", &token).is_err());
    }

    #[test]
    fn jsonl_splices_document_bytes_without_value_parser() {
        let (documents, history) = encode_page(&[ExportRecord::Document {
            collection: "a\"b".into(),
            id: "1".into(),
            version: 3,
            canonical_json: br#"{"x":true}"#.to_vec(),
        }])
        .expect("jsonl");
        assert!(history.is_empty());
        assert_eq!(
            std::str::from_utf8(&documents).expect("utf8"),
            "{\"collection\":\"a\\\"b\",\"id\":\"1\",\"version\":3,\"value\":{\"x\":true}}\n"
        );
    }

    #[test]
    fn jsonl_history_retains_operation_and_durability_provenance() {
        let (documents, history) = encode_page(&[
            ExportRecord::History {
                collection: "orders".into(),
                id: "one".into(),
                operation_id: "write-42".into(),
                revision: 7,
                durability: terrabase::LocalDurability::Written,
                deleted: false,
                canonical_json: Some(br#"{"total":3}"#.to_vec()),
            },
            ExportRecord::History {
                collection: "orders".into(),
                id: "one".into(),
                operation_id: "delete-43".into(),
                revision: 8,
                durability: terrabase::LocalDurability::Synced,
                deleted: true,
                canonical_json: None,
            },
        ])
        .expect("jsonl");
        assert!(documents.is_empty());
        assert_eq!(
            std::str::from_utf8(&history).expect("utf8"),
            "{\"collection\":\"orders\",\"id\":\"one\",\"version\":7,\"deleted\":false,\"operation_id\":\"write-42\",\"durability\":\"written\",\"value\":{\"total\":3}}\n{\"collection\":\"orders\",\"id\":\"one\",\"version\":8,\"deleted\":true,\"operation_id\":\"delete-43\",\"durability\":\"synced\",\"value\":null}\n"
        );
    }

    #[test]
    fn metadata_member_collisions_fail_closed() {
        let snapshot = ExportSnapshot {
            captured_at_millis: 0,
            history_start: 0,
            boundary: 1,
            database_id: "db".into(),
            schemas: vec![MetadataMember { name: "same.json".into(), bytes: br"{}".to_vec() }],
            indexes: vec![MetadataMember { name: "same.json".into(), bytes: br"{}".to_vec() }],
            branches: Vec::new(),
            estimated_records: None,
        };
        // Prefixes are part of the member identity, so same names in schema
        // and index namespaces are valid; a traversal name is not.
        assert_eq!(metadata_members(&snapshot).expect("namespaces").len(), 2);
        let mut unsafe_snapshot = snapshot;
        unsafe_snapshot.schemas[0].name = "../escape".into();
        assert!(metadata_members(&unsafe_snapshot).is_err());
    }

    #[test]
    fn placement_and_encryption_are_fail_closed() {
        let source = Source { pages: Mutex::new(Vec::new()) };
        let store = Arc::new(MemoryExportStore::default());
        let port = Arc::new(Port {
            writer: Arc::new(Mutex::new(Some(Writer {
                members: Mutex::new(Vec::new()),
                committed: AtomicUsize::new(0),
            }))),
            opens: AtomicUsize::new(0),
        });
        let service = ExportService::new(Some(Arc::new(source)), Some(store), Some(port), [0; 32])
            .expect("service");
        let request = ExportRequest {
            database: "db".into(),
            destination: ExportDestination::File {
                root: PathBuf::from("/tmp/export"),
                region: RegionCode::FRA,
            },
            format: TransferFormat::Jsonl,
            // JSONL is a converted format and therefore must use the
            // documents-only scope before placement validation is reached.
            // Keep this fixture valid through the earlier request checks so
            // it specifically proves the placement boundary below.
            scope: ExportScope::Documents,
            placement: PlacementLock::single(RegionCode::GRU),
            idempotency_key: None,
        };
        assert!(matches!(validate_request(&request), Err(ExportError::PlacementMismatch)));
        assert!(ExportService::new(None, None, None, [0; 32]).is_err());
        let _ = service;
    }
}

#[cfg(test)]
#[path = "export_cancel_tests.rs"]
mod cancellation_tests;
