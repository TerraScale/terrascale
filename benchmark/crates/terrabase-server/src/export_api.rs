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
use std::io::{self, Write};
use std::path::{Component, Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use std::time::{SystemTime, UNIX_EPOCH};

use axum::Router;
use axum::body::Body;
use axum::extract::{Path as AxumPath, State};
use axum::http::{Response as HttpResponse, StatusCode, header};
use axum::response::IntoResponse;
use axum::routing::{get, post};
use serde::{Deserialize, Serialize};
use terrabase::Engine;
use terrabase::encryption::ArtifactCipher;
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
/// Route exposed by [`http_router`].
pub const EXPORT_ROUTE: &str = "/api/v1/databases/{name}/exports";

static NEXT_JOB: AtomicU64 = AtomicU64::new(1);

mod region_code_serde {
    use serde::{Deserialize, Deserializer, Serializer};
    use std::str::FromStr;
    use terrabase::regions::RegionCode;

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
#[derive(Clone, Copy, Debug, Eq, PartialEq, Serialize, Deserialize)]
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
        /// Revision identity.
        revision: u64,
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
    fn snapshot(&self, database: &str) -> Result<ExportSnapshot, ExportSourceError>;
    /// Returns one bounded page after the opaque adapter cursor.
    fn page(
        &self,
        database: &str,
        snapshot: &ExportSnapshot,
        phase: ExportPhase,
        after: Option<&[u8]>,
        limit: usize,
    ) -> Result<ExportPage, ExportSourceError>;
}

/// The production Engine read seam used by [`EngineExportSource`].
///
/// The server crate cannot reach the Engine's crate-private bounded snapshot
/// methods directly.  A member adapter must therefore implement this seam at
/// the Engine composition boundary and delegate it to the real Engine,
/// schema catalog, index catalog, retained-history, and branch registry.  A
/// production adapter must never implement these calls by materialising the
/// whole database or by maintaining a second in-memory database.
pub trait EngineExportReadPort: Send + Sync + 'static {
    /// Captures the fixed boundary and all logical metadata for one database.
    fn snapshot(&self, database: &str) -> Result<ExportSnapshot, ExportSourceError>;

    /// Reads one bounded page at the pinned boundary.  `after` is an opaque
    /// adapter cursor; returned canonical bytes remain opaque to this crate.
    fn page(
        &self,
        database: &str,
        snapshot: &ExportSnapshot,
        phase: ExportPhase,
        after: Option<&[u8]>,
        limit: usize,
    ) -> Result<ExportPage, ExportSourceError>;
}

/// Production source adapter backed by the member's Engine composition.
///
/// This type intentionally contains no fallback source.  The member must
/// pass an adapter that reads the durable Engine boundary and metadata; a
/// missing adapter is rejected by [`Self::try_new`].
pub struct EngineExportSource {
    read: Arc<dyn EngineExportReadPort>,
    engine_database_id: Option<String>,
    _engine: Option<Arc<Engine>>,
}

impl EngineExportSource {
    /// Constructs a source from the member's real Engine read adapter.
    pub fn try_new(read: Option<Arc<dyn EngineExportReadPort>>) -> Result<Self, ExportError> {
        Ok(Self {
            read: read.ok_or(ExportError::DependencyUnavailable)?,
            engine_database_id: None,
            _engine: None,
        })
    }

    /// Constructs a source bound to a real durable Engine. The read port is
    /// still mandatory because bounded page, schema/index, history, and
    /// branch methods are currently exposed only at the Engine composition
    /// boundary.
    pub fn from_engine(
        engine: Arc<Engine>,
        read: Option<Arc<dyn EngineExportReadPort>>,
    ) -> Result<Self, ExportError> {
        Ok(Self {
            read: read.ok_or(ExportError::DependencyUnavailable)?,
            engine_database_id: Some(engine.database_id().to_text()),
            _engine: Some(engine),
        })
    }
}

impl ExportSource for EngineExportSource {
    fn snapshot(&self, database: &str) -> Result<ExportSnapshot, ExportSourceError> {
        if database.trim().is_empty() {
            return Err(ExportSourceError::InvalidPage("database is empty"));
        }
        let snapshot = self.read.snapshot(database)?;
        if self
            .engine_database_id
            .as_deref()
            .is_some_and(|database_id| database_id != snapshot.database_id)
        {
            return Err(ExportSourceError::Storage("engine database identity mismatch".into()));
        }
        Ok(snapshot)
    }

    fn page(
        &self,
        database: &str,
        snapshot: &ExportSnapshot,
        phase: ExportPhase,
        after: Option<&[u8]>,
        limit: usize,
    ) -> Result<ExportPage, ExportSourceError> {
        if limit == 0 || limit > MAX_PAGE_RECORDS {
            return Err(ExportSourceError::InvalidPage("page limit is outside bound"));
        }
        self.read.page(database, snapshot, phase, after, limit)
    }
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
#[derive(Clone, Debug, Eq, PartialEq, Serialize, Deserialize)]
pub struct ArtifactFact {
    /// Relative bundle member path.
    pub path: String,
    /// Length of the encrypted bytes published by the port.
    pub length: u64,
    /// BLAKE3 digest of the encrypted bytes.
    pub checksum: String,
}

/// A committed artifact writer.  `put` must be idempotent: an existing path
/// is accepted only when it contains the exact same plaintext.
pub trait ArtifactWriter: Send {
    /// Publishes or verifies one encrypted member.
    fn put(&mut self, path: &str, plaintext: &[u8]) -> Result<ArtifactFact, ExportError>;
    /// Returns facts for members already present in a resumable staging
    /// artifact.  A restart uses this to rebuild the manifest without
    /// retaining document bytes in the durable job state.  Ports that do not
    /// support restart recovery must fail closed rather than return an empty
    /// list for a non-empty staging artifact.
    fn existing_facts(&mut self) -> Result<Vec<ArtifactFact>, ExportError> {
        Err(ExportError::DependencyUnavailable)
    }
    /// Atomically publishes the completed bundle and its commit marker.
    fn commit(self: Box<Self>, manifest: &ArtifactFact) -> Result<(), ExportError>;
    /// Removes uncommitted staging state after a terminal failure.
    fn abort(self: Box<Self>);
}

/// Destination/provider seam.  `open` must reject an absent key/provider.
pub trait ArtifactPort: Send + Sync + 'static {
    /// Returns provider/key metadata without exposing key material.
    fn attestation(&self) -> Option<EncryptionAttestation>;
    /// Opens a job-scoped resumable writer after placement validation.
    fn open(
        &self,
        job_id: &str,
        database: &str,
        destination: &ExportDestination,
        placement: &PlacementLock,
    ) -> Result<Box<dyn ArtifactWriter>, ExportError>;
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
#[derive(Clone, Debug, Eq, PartialEq, Serialize, Deserialize)]
pub enum JobStatus {
    /// Accepted and waiting for a worker.
    Accepted,
    /// A worker is publishing pages.
    Running,
    /// All members and the commit marker are durable.
    Complete,
    /// The job failed closed; state remains resumable when the error is
    /// transient and callers may submit the same idempotency key.
    Failed,
}

/// Durable state store seam.
pub trait ExportStore: Send + Sync + 'static {
    /// Loads a job, if present.
    fn load(&self, job_id: &str) -> Result<Option<ExportJobState>, ExportError>;
    /// Atomically records a job state.
    fn save(&self, state: &ExportJobState) -> Result<(), ExportError>;
}

/// Service failures.  Messages deliberately avoid paths, credentials, and
/// document content.
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
#[derive(Clone, Debug, Eq, PartialEq, Serialize, Deserialize)]
pub struct ExportAccepted {
    /// Job identity.
    pub job_id: String,
    /// Status polling path supplied to HTTP and CLI callers.
    pub status_path: String,
    /// Opaque cursor valid for this job and service instance.
    pub resume_cursor: String,
}

/// Status view returned by polling.
#[derive(Clone, Debug, Eq, PartialEq, Serialize, Deserialize)]
pub struct ExportStatus {
    /// Job identity.
    pub job_id: String,
    /// Current lifecycle state.
    pub status: JobStatus,
    /// Records emitted so far.
    pub records: u64,
    /// Optional estimate.
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
    /// Exact database placement lock.
    pub placement: PlacementLock,
    /// Optional retry identity. Repeating it returns the original job.
    pub idempotency_key: Option<String>,
}

/// The asynchronous export coordinator.
pub struct ExportService {
    source: Arc<dyn ExportSource>,
    store: Arc<dyn ExportStore>,
    artifacts: Arc<dyn ArtifactPort>,
    cursor_key: [u8; 32],
    active_jobs: Arc<Mutex<BTreeSet<String>>>,
}

impl ExportService {
    /// Constructs a service. Missing source/store/port or encryption provider
    /// is rejected before any request can be admitted.
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
        })
    }

    /// Builds the handler state from explicit production seams.
    ///
    /// Returning an [`Arc`] makes the result directly usable with
    /// [`http_router`]. Every seam remains mandatory; this constructor has no
    /// local, echo, or in-memory production fallback.
    pub fn handler_ready(
        source: Option<Arc<dyn ExportSource>>,
        store: Option<Arc<dyn ExportStore>>,
        artifacts: Option<Arc<dyn ArtifactPort>>,
        cursor_key: [u8; 32],
    ) -> Result<Arc<Self>, ExportError> {
        Ok(Arc::new(Self::new(source, store, artifacts, cursor_key)?))
    }

    /// Admits a job and schedules bounded work.  The initial state is durable
    /// before the worker is spawned, so a process crash cannot lose an accept.
    pub async fn submit(&self, request: ExportRequest) -> Result<ExportAccepted, ExportError> {
        validate_request(&request)?;
        let job_id = job_id(&request);
        if let Some(existing) = self.store.load(&job_id)? {
            validate_state(&existing)?;
            if existing.database != request.database
                || existing.destination != request.destination
                || existing.placement != request.placement.regions().collect::<Vec<_>>()
            {
                return Err(ExportError::IdempotencyConflict);
            }
            if existing.status != JobStatus::Complete {
                self.spawn_worker(&job_id);
            }
            return Ok(accepted(&existing));
        }
        let snapshot = self.source.snapshot(&request.database).map_err(ExportError::Source)?;
        validate_snapshot(&snapshot)?;
        let mut state = ExportJobState {
            job_id: job_id.clone(),
            database: request.database.clone(),
            destination: request.destination.clone(),
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
        self.store.save(&state)?;
        self.spawn_worker(&job_id);
        state.status = JobStatus::Accepted;
        Ok(accepted(&state))
    }

    /// Polls a job.  This is intentionally cheap and reads only the durable
    /// control record, so progress is visible well within five seconds.
    pub fn status(&self, job_id: &str) -> Result<Option<ExportStatus>, ExportError> {
        let Some(state) = self.store.load(job_id)? else { return Ok(None) };
        validate_state(&state)?;
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
        Ok(Some(ExportStatus {
            job_id: state.job_id,
            status: state.status,
            records: state.records,
            estimated_records: state.snapshot.estimated_records,
            progress_basis_points,
            resume_cursor: state.resume_cursor,
            manifest: state.manifest,
            error: state.error,
        }))
    }

    /// Validates and decodes a caller cursor, returning its phase and source
    /// cursor.  Cursors are authenticated and scoped to one job.
    pub fn decode_resume_cursor(
        &self,
        job_id: &str,
        token: &str,
    ) -> Result<(ExportPhase, Option<Vec<u8>>), ExportError> {
        decode_cursor(&self.cursor_key, job_id, token)
    }

    /// Explicitly retries/resumes a job using its authenticated opaque cursor.
    /// The cursor must be the latest cursor persisted for that job.
    pub async fn resume(&self, job_id: &str, token: &str) -> Result<ExportStatus, ExportError> {
        let state = self.store.load(job_id)?.ok_or(ExportError::InvalidRequest("unknown job"))?;
        validate_state(&state)?;
        if state.resume_cursor != token {
            return Err(ExportError::InvalidRequest("resume cursor is stale"));
        }
        let _ = self.decode_resume_cursor(job_id, token)?;
        if state.status != JobStatus::Complete {
            self.spawn_worker(job_id);
        }
        self.status(job_id)?.ok_or(ExportError::InvalidRequest("unknown job"))
    }

    fn clone_for_worker(&self) -> Worker {
        Worker {
            source: Arc::clone(&self.source),
            store: Arc::clone(&self.store),
            artifacts: Arc::clone(&self.artifacts),
            cursor_key: self.cursor_key,
            active_jobs: Arc::clone(&self.active_jobs),
        }
    }

    fn spawn_worker(&self, job_id: &str) {
        let worker = self.clone_for_worker();
        let job = job_id.to_owned();
        tokio::spawn(async move {
            let _ = tokio::task::spawn_blocking(move || worker.run(&job)).await;
        });
    }

    fn cursor(&self, job_id: &str, phase: ExportPhase, source_cursor: Option<&[u8]>) -> String {
        encode_cursor(&self.cursor_key, job_id, phase, source_cursor)
    }
}

struct Worker {
    source: Arc<dyn ExportSource>,
    store: Arc<dyn ExportStore>,
    artifacts: Arc<dyn ArtifactPort>,
    cursor_key: [u8; 32],
    active_jobs: Arc<Mutex<BTreeSet<String>>>,
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
    fn run(&self, job_id: &str) -> Result<(), ExportError> {
        let Some(_active) = ActiveJob::acquire(Arc::clone(&self.active_jobs), job_id)? else {
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
        let placement = PlacementLock::new(state.placement.iter().copied())?;
        let mut writer =
            self.artifacts.open(job_id, &state.database, &state.destination, &placement)?;
        let result = self.run_inner(&mut state, &mut writer);
        match result {
            Ok(manifest) => {
                writer.commit(&manifest)?;
                state.manifest = Some(manifest);
                state.status = JobStatus::Complete;
                state.phase = ExportPhase::Done;
                state.resume_cursor =
                    encode_cursor(&self.cursor_key, job_id, ExportPhase::Done, None);
                self.store.save(&state)
            }
            Err(error) => {
                state.status = JobStatus::Failed;
                state.error = Some(error.to_string());
                let _ = self.store.save(&state);
                writer.abort();
                Err(error)
            }
        }
    }

    fn run_inner(
        &self,
        state: &mut ExportJobState,
        writer: &mut Box<dyn ArtifactWriter>,
    ) -> Result<ArtifactFact, ExportError> {
        let metadata = metadata_members(&state.snapshot)?;
        let mut facts_by_path = BTreeSet::new();
        let mut facts = Vec::with_capacity(metadata.len() + 4);
        for fact in writer.existing_facts()? {
            validate_fact(&fact)?;
            // Control members are rebuilt after every page commit. They must
            // not be included in their own checksums or manifest.
            if matches!(fact.path.as_str(), "checksums.txt" | "manifest.json") {
                continue;
            }
            if !bundle_member_path(&fact.path) {
                return Err(ExportError::IdempotencyConflict);
            }
            if !facts_by_path.insert(fact.path.clone()) {
                return Err(ExportError::IdempotencyConflict);
            }
            facts.push(fact);
        }
        for (path, bytes) in metadata {
            let fact = put_checked(writer, &path, &bytes)?;
            if facts_by_path.insert(fact.path.clone()) {
                facts.push(fact);
            }
        }
        loop {
            if state.phase == ExportPhase::Done {
                break;
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
                let page_number = state.pages;
                if !jsonl.is_empty() {
                    let path = format!("documents/{page_number:012}.jsonl");
                    let fact = put_checked(writer, &path, &jsonl)?;
                    if facts_by_path.insert(fact.path.clone()) {
                        facts.push(fact);
                    }
                }
                if !history.is_empty() {
                    let path = format!("history/{page_number:012}.jsonl");
                    let fact = put_checked(writer, &path, &history)?;
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
        facts.sort_by(|left, right| left.path.cmp(&right.path));
        let mut checksum_bytes = Vec::new();
        for fact in &facts {
            checksum_bytes.extend_from_slice(fact.path.as_bytes());
            checksum_bytes.push(b'\t');
            checksum_bytes.extend_from_slice(fact.length.to_string().as_bytes());
            checksum_bytes.push(b'\t');
            checksum_bytes.extend_from_slice(fact.checksum.as_bytes());
            checksum_bytes.push(b'\n');
        }
        let checksums = put_checked(writer, "checksums.txt", &checksum_bytes)?;
        if facts_by_path.insert(checksums.path.clone()) {
            facts.push(checksums);
        }
        let manifest_bytes = manifest_bytes(state, &facts)?;
        put_checked(writer, "manifest.json", &manifest_bytes)
    }
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

fn put_checked(
    writer: &mut Box<dyn ArtifactWriter>,
    path: &str,
    plaintext: &[u8],
) -> Result<ArtifactFact, ExportError> {
    let fact = writer.put(path, plaintext)?;
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
                append_json_line(&mut documents, collection, id, *version, canonical_json, None)?;
            }
            ExportRecord::History { collection, id, revision, deleted, canonical_json } => {
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
        status_path: format!("/api/v1/exports/{}", state.job_id),
        resume_cursor: state.resume_cursor.clone(),
    }
}

fn job_id(request: &ExportRequest) -> String {
    let mut hasher = blake3::Hasher::new();
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
    format!("ex1.{}.{}.{}", phase, hex_encode(source), hex_encode(&mac.as_bytes()[..16]))
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
        hex_decode(parts.next().ok_or(ExportError::InvalidRequest("resume cursor is malformed"))?)?;
    if source.len() > MAX_SOURCE_CURSOR_BYTES {
        return Err(ExportError::InvalidRequest("resume cursor is too large"));
    }
    let provided =
        hex_decode(parts.next().ok_or(ExportError::InvalidRequest("resume cursor is malformed"))?)?;
    if parts.next().is_some() || provided.len() != 16 {
        return Err(ExportError::InvalidRequest("resume cursor is malformed"));
    }
    let expected = encode_cursor(key, job_id, phase, Some(&source));
    if expected != token {
        return Err(ExportError::InvalidRequest("resume cursor authentication failed"));
    }
    Ok((phase, (!source.is_empty()).then_some(source)))
}

fn hex_encode(bytes: &[u8]) -> String {
    let mut out = String::with_capacity(bytes.len() * 2);
    for byte in bytes {
        use std::fmt::Write as _;
        let _ = write!(out, "{byte:02x}");
    }
    out
}

fn hex_decode(text: &str) -> Result<Vec<u8>, ExportError> {
    if !text.len().is_multiple_of(2) {
        return Err(ExportError::InvalidRequest("resume cursor encoding is invalid"));
    }
    let mut bytes = Vec::with_capacity(text.len() / 2);
    for pair in text.as_bytes().chunks_exact(2) {
        let high = (pair[0] as char)
            .to_digit(16)
            .ok_or(ExportError::InvalidRequest("resume cursor encoding is invalid"))?;
        let low = (pair[1] as char)
            .to_digit(16)
            .ok_or(ExportError::InvalidRequest("resume cursor encoding is invalid"))?;
        bytes.push(((high << 4) | low) as u8);
    }
    Ok(bytes)
}

fn safe_member_path(path: &str) -> bool {
    let path = Path::new(path);
    !path.as_os_str().is_empty()
        && path.components().all(|component| matches!(component, Component::Normal(_)))
}

/// A simple crash-safe filesystem control store.
pub struct FileExportStore {
    root: PathBuf,
}

impl FileExportStore {
    /// Opens a store rooted at `root`, creating it if necessary.
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

impl ExportStore for FileExportStore {
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
        let path = self.path(&state.job_id)?;
        let tmp =
            path.with_extension(format!("json.tmp.{}", NEXT_JOB.fetch_add(1, Ordering::Relaxed)));
        let bytes =
            serde_json::to_vec(state).map_err(|error| ExportError::Io(error.to_string()))?;
        // A crash between the temporary write and rename leaves the previous
        // control state intact. Unique temporary names also keep concurrent
        // process writers from deleting or truncating one another's state.
        let mut file = OpenOptions::new()
            .create_new(true)
            .write(true)
            .open(&tmp)
            .map_err(|error| ExportError::Io(error.to_string()))?;
        file.write_all(&bytes)
            .and_then(|()| file.sync_all())
            .map_err(|error| ExportError::Io(error.to_string()))?;
        fs::rename(tmp, path).map_err(|error| ExportError::Io(error.to_string()))
    }
}

/// In-memory store used only by this module's focused tests.
#[cfg(test)]
#[doc(hidden)]
#[derive(Default)]
pub struct MemoryExportStore {
    states: Mutex<std::collections::BTreeMap<String, ExportJobState>>,
}

#[cfg(test)]
impl ExportStore for MemoryExportStore {
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

/// A local encrypted artifact port backed by [`ArtifactCipher`].
pub struct EncryptedFileArtifactPort {
    root: PathBuf,
    attestation: EncryptionAttestation,
    cipher: Arc<Mutex<ArtifactCipher>>,
}

impl EncryptedFileArtifactPort {
    /// Constructs a file port.  The caller must provide a cipher bound to the
    /// database export artifact context; no plaintext fallback exists.
    pub fn new(
        root: impl Into<PathBuf>,
        cipher: ArtifactCipher,
        attestation: EncryptionAttestation,
    ) -> Result<Self, ExportError> {
        attestation.validate()?;
        Ok(Self { root: root.into(), attestation, cipher: Arc::new(Mutex::new(cipher)) })
    }
}

struct EncryptedFileWriter {
    directory: PathBuf,
    cipher: Arc<Mutex<ArtifactCipher>>,
}

impl ArtifactWriter for EncryptedFileWriter {
    fn put(&mut self, path: &str, plaintext: &[u8]) -> Result<ArtifactFact, ExportError> {
        if !safe_member_path(path) || plaintext.len() > MAX_METADATA_BYTES.max(MAX_PAGE_BYTES) {
            return Err(ExportError::InvalidRequest("artifact member is unsafe or too large"));
        }
        let target = self.directory.join(path);
        if let Ok(encrypted) = fs::read(&target) {
            let mut cipher =
                self.cipher.lock().map_err(|_| ExportError::Io("cipher lock poisoned".into()))?;
            let existing = cipher.open(&encrypted).map_err(|_| ExportError::IdempotencyConflict)?;
            if existing != plaintext {
                return Err(ExportError::IdempotencyConflict);
            }
            return Ok(fact(path, &encrypted));
        }
        let encrypted = self
            .cipher
            .lock()
            .map_err(|_| ExportError::Io("cipher lock poisoned".into()))?
            .seal(plaintext)
            .map_err(|error| ExportError::Io(error.to_string()))?;
        if let Some(parent) = target.parent() {
            fs::create_dir_all(parent).map_err(|error| ExportError::Io(error.to_string()))?;
        }
        let tmp = target.with_extension("part");
        let mut file = File::create(&tmp).map_err(|error| ExportError::Io(error.to_string()))?;
        file.write_all(&encrypted)
            .and_then(|()| file.sync_all())
            .map_err(|error| ExportError::Io(error.to_string()))?;
        fs::rename(tmp, target).map_err(|error| ExportError::Io(error.to_string()))?;
        Ok(fact(path, &encrypted))
    }

    fn existing_facts(&mut self) -> Result<Vec<ArtifactFact>, ExportError> {
        let mut pending = vec![self.directory.clone()];
        let mut facts = Vec::new();
        let cipher =
            self.cipher.lock().map_err(|_| ExportError::Io("cipher lock poisoned".into()))?;
        while let Some(directory) = pending.pop() {
            let entries =
                fs::read_dir(&directory).map_err(|error| ExportError::Io(error.to_string()))?;
            for entry in entries {
                let entry = entry.map_err(|error| ExportError::Io(error.to_string()))?;
                let file_type =
                    entry.file_type().map_err(|error| ExportError::Io(error.to_string()))?;
                if file_type.is_symlink() {
                    return Err(ExportError::DestinationUnavailable);
                }
                let path = entry.path();
                if file_type.is_dir() {
                    pending.push(path);
                    continue;
                }
                if !file_type.is_file() || path.file_name().is_some_and(|name| name == "COMMITTED")
                {
                    continue;
                }
                let relative = path
                    .strip_prefix(&self.directory)
                    .map_err(|_| ExportError::DestinationUnavailable)?;
                let member = relative.to_str().ok_or(ExportError::DestinationUnavailable)?;
                if member.ends_with(".part") || !safe_member_path(member) {
                    continue;
                }
                let encrypted =
                    fs::read(&path).map_err(|error| ExportError::Io(error.to_string()))?;
                if encrypted.len() > MAX_METADATA_BYTES.max(MAX_PAGE_BYTES) {
                    return Err(ExportError::DestinationUnavailable);
                }
                cipher.open(&encrypted).map_err(|_| ExportError::IdempotencyConflict)?;
                facts.push(fact(member, &encrypted));
                if facts.len() > MAX_PAGE_RECORDS.saturating_mul(4) {
                    return Err(ExportError::DestinationUnavailable);
                }
            }
        }
        facts.sort_by(|left, right| left.path.cmp(&right.path));
        Ok(facts)
    }

    fn commit(self: Box<Self>, manifest: &ArtifactFact) -> Result<(), ExportError> {
        if manifest.path != "manifest.json" {
            return Err(ExportError::IdempotencyConflict);
        }
        let marker = self.directory.join("COMMITTED");
        let bytes = format!("{}\t{}\t{}\n", manifest.path, manifest.length, manifest.checksum);
        if let Ok(existing) = fs::read(&marker) {
            if existing == bytes.as_bytes() {
                return Ok(());
            }
            return Err(ExportError::IdempotencyConflict);
        }
        let tmp = marker.with_extension("part");
        fs::write(&tmp, bytes).map_err(|error| ExportError::Io(error.to_string()))?;
        fs::rename(tmp, marker).map_err(|error| ExportError::Io(error.to_string()))
    }

    fn abort(self: Box<Self>) {
        let _ = fs::remove_dir_all(self.directory);
    }
}

impl ArtifactPort for EncryptedFileArtifactPort {
    fn attestation(&self) -> Option<EncryptionAttestation> {
        Some(self.attestation.clone())
    }

    fn open(
        &self,
        job_id: &str,
        database: &str,
        destination: &ExportDestination,
        placement: &PlacementLock,
    ) -> Result<Box<dyn ArtifactWriter>, ExportError> {
        if !placement.allows(destination.region()) || job_id.is_empty() || database.is_empty() {
            return Err(ExportError::PlacementMismatch);
        }
        let ExportDestination::File { root, .. } = destination else {
            return Err(ExportError::DestinationUnavailable);
        };
        if root != &self.root {
            return Err(ExportError::DestinationUnavailable);
        }
        ensure_no_symlink_components(root)?;
        let directory = self.root.join(database).join(job_id);
        ensure_no_symlink_components(&self.root.join(database))?;
        fs::create_dir_all(&directory).map_err(|error| ExportError::Io(error.to_string()))?;
        ensure_no_symlink_components(&directory)?;
        Ok(Box::new(EncryptedFileWriter { directory, cipher: Arc::clone(&self.cipher) }))
    }
}

fn ensure_no_symlink_components(path: &Path) -> Result<(), ExportError> {
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

fn fact(path: &str, bytes: &[u8]) -> ArtifactFact {
    ArtifactFact {
        path: path.to_owned(),
        length: bytes.len() as u64,
        checksum: blake3::hash(bytes).to_hex().to_string(),
    }
}

/// Builds an Axum router for parent composition. Authentication, placement
/// headers, and authorization remain parent-owned middleware.
pub fn http_router(service: Arc<ExportService>) -> Router {
    Router::new()
        .route(EXPORT_ROUTE, post(create_export_http))
        .route("/api/v1/exports/{job_id}", get(export_status_http))
        .with_state(service)
}

/// Parent-callable handler for `POST /api/v1/databases/{name}/exports`.
pub async fn create_export_http(
    State(service): State<Arc<ExportService>>,
    AxumPath(name): AxumPath<String>,
    body: String,
) -> impl IntoResponse {
    let request = match serde_json::from_str::<HttpExportRequest>(&body) {
        Ok(request) => request.into_request(name),
        Err(_) => return http_error(StatusCode::BAD_REQUEST, "invalid export request body"),
    };
    match request {
        Ok(request) => match service.submit(request).await {
            Ok(accepted) => json_response(StatusCode::ACCEPTED, &accepted),
            Err(error) => http_error(error_status(&error), &error.to_string()),
        },
        Err(error) => http_error(StatusCode::UNPROCESSABLE_ENTITY, &error.to_string()),
    }
}

/// Parent-callable status handler for an accepted export.
pub async fn export_status_http(
    State(service): State<Arc<ExportService>>,
    AxumPath(job_id): AxumPath<String>,
) -> impl IntoResponse {
    match service.status(&job_id) {
        Ok(Some(status)) => json_response(StatusCode::OK, &status),
        Ok(None) => http_error(StatusCode::NOT_FOUND, "export job not found"),
        Err(error) => http_error(error_status(&error), &error.to_string()),
    }
}

#[derive(Deserialize)]
struct HttpExportRequest {
    destination: Option<ExportDestination>,
    #[serde(with = "region_vec_serde")]
    placement: Vec<RegionCode>,
    idempotency_key: Option<String>,
}

impl HttpExportRequest {
    fn into_request(self, database: String) -> Result<ExportRequest, ExportError> {
        Ok(ExportRequest {
            database,
            destination: self
                .destination
                .ok_or(ExportError::InvalidRequest("destination is required"))?,
            placement: PlacementLock::new(self.placement)?,
            idempotency_key: self.idempotency_key,
        })
    }
}

fn error_status(error: &ExportError) -> StatusCode {
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

fn json_response<T: Serialize>(status: StatusCode, value: &T) -> HttpResponse<Body> {
    match serde_json::to_vec(value) {
        Ok(bytes) => HttpResponse::builder()
            .status(status)
            .header(header::CONTENT_TYPE, "application/json")
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

    struct ReadPort;

    impl EngineExportReadPort for ReadPort {
        fn snapshot(&self, _database: &str) -> Result<ExportSnapshot, ExportSourceError> {
            Err(ExportSourceError::Storage("engine seam unavailable".into()))
        }

        fn page(
            &self,
            _database: &str,
            _snapshot: &ExportSnapshot,
            _phase: ExportPhase,
            _after: Option<&[u8]>,
            _limit: usize,
        ) -> Result<ExportPage, ExportSourceError> {
            Err(ExportSourceError::Storage("engine seam unavailable".into()))
        }
    }

    struct Source {
        pages: Mutex<Vec<ExportPage>>,
    }

    impl ExportSource for Source {
        fn snapshot(&self, _database: &str) -> Result<ExportSnapshot, ExportSourceError> {
            Ok(ExportSnapshot {
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
        fn put(&mut self, path: &str, plaintext: &[u8]) -> Result<ArtifactFact, ExportError> {
            let mut members = self.members.lock().expect("members");
            if let Some((_, existing)) = members.iter().find(|(name, _)| name == path) {
                if existing != plaintext {
                    return Err(ExportError::IdempotencyConflict);
                }
            } else {
                members.push((path.into(), plaintext.to_vec()));
            }
            Ok(fact(path, plaintext))
        }
        fn existing_facts(&mut self) -> Result<Vec<ArtifactFact>, ExportError> {
            Ok(self
                .members
                .lock()
                .expect("members")
                .iter()
                .map(|(path, bytes)| fact(path, bytes))
                .collect())
        }
        fn commit(self: Box<Self>, _manifest: &ArtifactFact) -> Result<(), ExportError> {
            self.committed.fetch_add(1, Ordering::Relaxed);
            Ok(())
        }
        fn abort(self: Box<Self>) {}
    }
    struct Port {
        writer: Arc<Mutex<Option<Writer>>>,
        opens: AtomicUsize,
    }
    impl ArtifactPort for Port {
        fn attestation(&self) -> Option<EncryptionAttestation> {
            Some(EncryptionAttestation { provider: "test".into(), key_version: "v1".into() })
        }
        fn open(
            &self,
            _job: &str,
            _database: &str,
            destination: &ExportDestination,
            lock: &PlacementLock,
        ) -> Result<Box<dyn ArtifactWriter>, ExportError> {
            if !lock.allows(destination.region()) {
                return Err(ExportError::PlacementMismatch);
            }
            self.opens.fetch_add(1, Ordering::Relaxed);
            Ok(Box::new(self.writer.lock().expect("writer").take().expect("writer")))
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
    fn production_source_requires_the_real_engine_seam() {
        assert!(matches!(
            EngineExportSource::try_new(None),
            Err(ExportError::DependencyUnavailable)
        ));
        let source = EngineExportSource::try_new(Some(Arc::new(ReadPort))).expect("source");
        assert!(matches!(
            source.snapshot("db"),
            Err(ExportSourceError::Storage(detail)) if detail == "engine seam unavailable"
        ));
        assert!(matches!(
            source.page(
                "db",
                &ExportSnapshot {
                    boundary: 1,
                    database_id: "db".into(),
                    schemas: Vec::new(),
                    indexes: Vec::new(),
                    branches: Vec::new(),
                    estimated_records: None,
                },
                ExportPhase::Documents,
                None,
                MAX_PAGE_RECORDS + 1
            ),
            Err(ExportSourceError::InvalidPage("page limit is outside bound"))
        ));
    }

    #[test]
    fn jsonl_splices_document_bytes_without_value_parser() {
        let (documents, history) = encode_page(&[ExportRecord::Document {
            collection: "a\"b".into(),
            id: "1".into(),
            version: 3,
            canonical_json: br#"{\"x\":true}"#.to_vec(),
        }])
        .expect("jsonl");
        assert!(history.is_empty());
        assert_eq!(
            std::str::from_utf8(&documents).expect("utf8"),
            r#"{\"collection\":\"a\\\"b\",\"id\":\"1\",\"version\":3,\"value\":{\"x\":true}}\n"#
        );
    }

    #[test]
    fn metadata_member_collisions_fail_closed() {
        let snapshot = ExportSnapshot {
            boundary: 1,
            database_id: "db".into(),
            schemas: vec![MetadataMember { name: "same.json".into(), bytes: br#"{}"#.to_vec() }],
            indexes: vec![MetadataMember { name: "same.json".into(), bytes: br#"{}"#.to_vec() }],
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
            placement: PlacementLock::single(RegionCode::GRU),
            idempotency_key: None,
        };
        assert!(matches!(validate_request(&request), Err(ExportError::PlacementMismatch)));
        assert!(ExportService::new(None, None, None, [0; 32]).is_err());
        let _ = service;
    }
}
