//! S3-backed backup transport for Studio.
//!
//! TerraBase is not SQLite, so Litestream cannot replicate its WAL. This
//! module keeps the engine's verified logical archive format and gives it the
//! same operating shape: S3 is the durable source of truth, recovery points
//! are uploaded incrementally, and restore hydrates only the selected chain
//! into disposable local scratch space.

use std::collections::BTreeSet;
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use futures_util::{StreamExt, TryStreamExt, stream};
use object_store::aws::AmazonS3Builder;
use object_store::path::Path as ObjectPath;
use object_store::{Error as ObjectStoreError, ObjectStore, ObjectStoreExt, PutMode, PutOptions};
use serde::{Deserialize, Serialize};
use terrabase::backup::{BackupKind, BackupManifest, BackupResult, RecoveryReceipt};

const MANIFEST_FILE: &str = "manifest.tbv2m";
const MANIFEST_LIMIT_BYTES: u64 = 1024 * 1024;
const UPLOAD_CONCURRENCY: usize = 4;
const DOWNLOAD_CONCURRENCY: usize = 8;

/// Persistent, non-secret S3 backup settings.
///
/// Credentials deliberately do not live here. The S3 client reads the
/// standard `AWS_*` variables and supports workload identity, ECS task roles,
/// and EC2 instance roles.
#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(default)]
pub struct S3BackupSettings {
    /// Whether the background scheduler may create recovery points.
    pub enabled: bool,
    /// S3 or S3-compatible bucket name.
    pub bucket: String,
    /// Signing region.
    pub region: String,
    /// Prefix dedicated to this TerraBase database.
    pub prefix: String,
    /// Optional S3-compatible HTTPS endpoint.
    pub endpoint: String,
    /// Use virtual-hosted requests instead of path-style requests.
    pub virtual_hosted_style: bool,
    /// Permit an HTTP endpoint. Intended only for loopback development stores.
    pub allow_http: bool,
    /// Minutes between incremental recovery points.
    pub incremental_interval_minutes: u64,
    /// Hours between self-contained full recovery points.
    pub full_interval_hours: u64,
    /// Optional KMS key identifier. Blank uses the bucket encryption policy.
    pub kms_key_id: String,
}

impl Default for S3BackupSettings {
    fn default() -> Self {
        Self {
            enabled: false,
            bucket: String::new(),
            region: "us-east-1".to_owned(),
            prefix: "terrabase/backups".to_owned(),
            endpoint: String::new(),
            virtual_hosted_style: false,
            allow_http: false,
            incremental_interval_minutes: 5,
            full_interval_hours: 24,
            kms_key_id: String::new(),
        }
    }
}

impl S3BackupSettings {
    /// Applies deployment environment settings when a bucket is present.
    #[must_use]
    pub fn from_environment() -> Option<Self> {
        let bucket = std::env::var("TERRABASE_BACKUP_S3_BUCKET").ok()?;
        if bucket.trim().is_empty() {
            return None;
        }
        let mut settings = Self {
            enabled: environment_bool("TERRABASE_BACKUP_ENABLED", true),
            bucket,
            region: environment_or("TERRABASE_BACKUP_S3_REGION", "us-east-1"),
            prefix: environment_or("TERRABASE_BACKUP_S3_PREFIX", "terrabase/backups"),
            endpoint: environment_or("TERRABASE_BACKUP_S3_ENDPOINT", ""),
            virtual_hosted_style: environment_bool(
                "TERRABASE_BACKUP_S3_VIRTUAL_HOSTED_STYLE",
                false,
            ),
            allow_http: environment_bool("TERRABASE_BACKUP_S3_ALLOW_HTTP", false),
            incremental_interval_minutes: environment_u64(
                "TERRABASE_BACKUP_INCREMENTAL_MINUTES",
                5,
            ),
            full_interval_hours: environment_u64("TERRABASE_BACKUP_FULL_HOURS", 24),
            kms_key_id: environment_or("TERRABASE_BACKUP_S3_KMS_KEY_ID", ""),
        };
        settings.normalize();
        Some(settings)
    }

    /// Normalizes operator-entered whitespace and prefix separators.
    pub fn normalize(&mut self) {
        self.bucket = self.bucket.trim().to_owned();
        self.region = self.region.trim().to_owned();
        self.prefix = self.prefix.trim().trim_matches('/').to_owned();
        self.endpoint = self.endpoint.trim().trim_end_matches('/').to_owned();
        self.kms_key_id = self.kms_key_id.trim().to_owned();
    }

    /// Validates the non-secret settings without contacting S3.
    pub fn validate(&self) -> Result<(), String> {
        if self.bucket.is_empty()
            || self.bucket.len() > 255
            || self.bucket.contains('/')
            || self.bucket.chars().any(char::is_control)
        {
            return Err("the S3 bucket name is invalid".to_owned());
        }
        if self.region.is_empty() || self.region.chars().any(char::is_whitespace) {
            return Err("the S3 region is required and cannot contain whitespace".to_owned());
        }
        if self.prefix.is_empty()
            || self.prefix.split('/').any(|part| part.is_empty() || part == "." || part == "..")
            || self.prefix.chars().any(char::is_control)
        {
            return Err(
                "the S3 prefix must be a non-empty object path without dot segments".to_owned()
            );
        }
        if self.incremental_interval_minutes == 0 {
            return Err("the incremental interval must be at least one minute".to_owned());
        }
        if self.full_interval_hours == 0 {
            return Err("the full interval must be at least one hour".to_owned());
        }
        if !self.endpoint.is_empty() {
            let is_https = self.endpoint.starts_with("https://");
            let permitted_http = self.allow_http && self.endpoint.starts_with("http://");
            if !is_https && !permitted_http {
                return Err(
                    "the S3 endpoint must use HTTPS unless allow HTTP is explicitly enabled"
                        .to_owned(),
                );
            }
            let authority = self
                .endpoint
                .split_once("//")
                .map_or(self.endpoint.as_str(), |(_, rest)| rest)
                .split('/')
                .next()
                .unwrap_or_default();
            if authority.contains('@') {
                return Err("the S3 endpoint must not contain credentials".to_owned());
            }
        }
        Ok(())
    }

    fn incremental_interval(&self) -> Duration {
        Duration::from_secs(self.incremental_interval_minutes.saturating_mul(60))
    }

    fn full_interval(&self) -> Duration {
        Duration::from_secs(self.full_interval_hours.saturating_mul(3_600))
    }
}

/// The requested kind of one S3 backup run.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum BackupRunMode {
    /// Choose a full or incremental point from the configured full interval.
    Automatic,
    /// Force a self-contained full point.
    Full,
}

/// One manifest committed in S3.
#[derive(Clone, Debug)]
pub struct RemoteRecoveryPoint {
    manifest: BackupManifest,
    modified_millis: i64,
    modified_at: String,
}

impl RemoteRecoveryPoint {
    /// Returns the validated manifest.
    pub fn manifest(&self) -> &BackupManifest {
        &self.manifest
    }

    /// Returns the S3 object's last-modified time as Unix milliseconds.
    pub fn modified_millis(&self) -> i64 {
        self.modified_millis
    }

    /// Returns the S3 object's last-modified time as an RFC 3339 timestamp.
    pub fn modified_at(&self) -> &str {
        &self.modified_at
    }
}

/// Terminal facts from one upload.
#[derive(Clone, Debug)]
pub struct BackupUploadReceipt {
    /// The engine result whose objects were committed in S3.
    pub result: BackupResult,
    /// Objects uploaded during this call. Idempotent existing objects are not counted.
    pub uploaded_objects: usize,
    /// Canonical S3 destination without credentials.
    pub destination: String,
}

/// One configured S3 recovery-point repository.
#[derive(Clone)]
pub struct S3BackupRepository {
    store: Arc<dyn ObjectStore>,
    prefix: ObjectPath,
    destination: String,
}

impl S3BackupRepository {
    /// Builds a repository from Studio settings and the standard AWS
    /// credential-provider environment.
    pub fn from_settings(settings: &S3BackupSettings) -> Result<Self, String> {
        settings.validate()?;
        let mut builder = AmazonS3Builder::from_env()
            .with_bucket_name(&settings.bucket)
            .with_region(&settings.region)
            .with_virtual_hosted_style_request(settings.virtual_hosted_style)
            .with_allow_http(settings.allow_http);
        if !settings.endpoint.is_empty() {
            builder = builder.with_endpoint(&settings.endpoint);
        }
        if !settings.kms_key_id.is_empty() {
            builder = builder.with_sse_kms_encryption(&settings.kms_key_id).with_bucket_key(true);
        }
        let store = builder.build().map_err(|error| safe_store_error("configure S3", error))?;
        Self::new(
            Arc::new(store),
            &settings.prefix,
            format!("s3://{}/{}", settings.bucket, settings.prefix),
        )
    }

    fn new(store: Arc<dyn ObjectStore>, prefix: &str, destination: String) -> Result<Self, String> {
        let prefix = ObjectPath::parse(prefix)
            .map_err(|error| format!("the S3 prefix is invalid: {error}"))?;
        Ok(Self { store, prefix, destination })
    }

    /// Lists and validates every committed manifest under the configured prefix.
    pub async fn list_recovery_points(&self) -> Result<Vec<RemoteRecoveryPoint>, String> {
        let objects = self
            .store
            .list(Some(&self.prefix))
            .try_filter(|object| {
                futures_util::future::ready(object.location.filename() == Some(MANIFEST_FILE))
            })
            .try_collect::<Vec<_>>()
            .await
            .map_err(|error| safe_store_error("list S3 recovery points", error))?;

        let repository = self.clone();
        let fetched = stream::iter(objects)
            .map(move |object| {
                let repository = repository.clone();
                async move {
                    if object.size > MANIFEST_LIMIT_BYTES {
                        return Err(
                            "an S3 recovery-point manifest exceeds the 1 MiB limit".to_owned()
                        );
                    }
                    let bytes =
                        repository.read_object(&object.location, MANIFEST_LIMIT_BYTES).await?;
                    let manifest = BackupManifest::decode(&bytes).map_err(|error| {
                        format!("S3 recovery-point manifest is invalid: {error:?}")
                    })?;
                    let expected = repository.object_path(manifest.identity(), MANIFEST_FILE);
                    if object.location != expected {
                        return Err(
                            "an S3 recovery-point manifest is stored under the wrong identity"
                                .to_owned(),
                        );
                    }
                    Ok(RemoteRecoveryPoint {
                        manifest,
                        modified_millis: object.last_modified.timestamp_millis(),
                        modified_at: object.last_modified.to_rfc3339(),
                    })
                }
            })
            .buffer_unordered(DOWNLOAD_CONCURRENCY)
            .collect::<Vec<Result<RemoteRecoveryPoint, String>>>()
            .await;
        let mut points = fetched.into_iter().collect::<Result<Vec<_>, _>>()?;
        points.sort_by(|left, right| manifest_order(&left.manifest, &right.manifest));
        validate_repository_scope(&points)?;
        Ok(points)
    }

    /// Performs a low-cost read/list probe and validates every listed manifest.
    pub async fn check(&self) -> Result<usize, String> {
        self.list_recovery_points().await.map(|points| points.len())
    }

    async fn upload_point(
        &self,
        destination: &Path,
        result: &BackupResult,
    ) -> Result<usize, String> {
        let manifest = result.manifest().clone();
        let identity = manifest.identity().to_owned();
        let local_point = destination.join(format!("rp-{identity}"));
        let repository = self.clone();
        let members = manifest.members().to_vec();
        let uploads = stream::iter(members)
            .map(move |member| {
                let repository = repository.clone();
                let local_path = local_point.join(member.name());
                let identity = identity.clone();
                async move {
                    let bytes = tokio::fs::read(&local_path)
                        .await
                        .map_err(|error| format!("cannot read staged backup member: {error}"))?;
                    if u64::try_from(bytes.len()).ok() != Some(member.length()) {
                        return Err("a staged backup member changed before upload".to_owned());
                    }
                    repository
                        .put_immutable(repository.object_path(&identity, member.name()), bytes)
                        .await
                }
            })
            .buffer_unordered(UPLOAD_CONCURRENCY)
            .collect::<Vec<Result<bool, String>>>()
            .await;
        let mut uploaded = uploads
            .into_iter()
            .collect::<Result<Vec<_>, _>>()?
            .into_iter()
            .filter(|created| *created)
            .count();

        // The manifest is the commit marker. It must be the final object made
        // visible for a recovery point.
        if self
            .put_immutable(self.object_path(manifest.identity(), MANIFEST_FILE), manifest.encode())
            .await?
        {
            uploaded += 1;
        }
        Ok(uploaded)
    }

    async fn put_immutable(&self, location: ObjectPath, bytes: Vec<u8>) -> Result<bool, String> {
        match self.store.head(&location).await {
            Ok(_) => {
                let existing = self.read_object(&location, u64::MAX).await?;
                if existing == bytes {
                    return Ok(false);
                }
                return Err(format!("refusing to overwrite the existing S3 object {location}"));
            }
            Err(ObjectStoreError::NotFound { .. }) => {}
            Err(error) => return Err(safe_store_error("inspect S3 object", error)),
        }
        let options = PutOptions { mode: PutMode::Create, ..PutOptions::default() };
        match self.store.put_opts(&location, bytes.clone().into(), options).await {
            Ok(_) => Ok(true),
            Err(ObjectStoreError::AlreadyExists { .. }) => {
                let existing = self.read_object(&location, u64::MAX).await?;
                if existing == bytes {
                    Ok(false)
                } else {
                    Err(format!("refusing to overwrite the existing S3 object {location}"))
                }
            }
            Err(error) => Err(safe_store_error("upload S3 object", error)),
        }
    }

    async fn read_object(&self, location: &ObjectPath, limit: u64) -> Result<Vec<u8>, String> {
        let object = self
            .store
            .get(location)
            .await
            .map_err(|error| safe_store_error("read S3 object", error))?;
        if object.meta.size > limit {
            return Err(format!("S3 object {location} exceeds its manifest bound"));
        }
        let bytes =
            object.bytes().await.map_err(|error| safe_store_error("stream S3 object", error))?;
        Ok(bytes.to_vec())
    }

    fn object_path(&self, identity: &str, file: &str) -> ObjectPath {
        self.prefix.clone().join(format!("rp-{identity}")).join(file)
    }
}

/// Runs a full or incremental engine backup and commits it to S3.
pub async fn backup_to_s3(
    repository: &S3BackupRepository,
    engine: Arc<terrabase::Engine>,
    scratch_root: &Path,
    settings: &S3BackupSettings,
    mode: BackupRunMode,
    tenants: Vec<terrabase::TenantId>,
) -> Result<BackupUploadReceipt, String> {
    let points = repository.list_recovery_points().await?;
    let scratch = ScratchDirectory::create(scratch_root, "upload")?;
    stage_manifests(scratch.path(), &points).await?;

    let plan = select_backup_plan(settings, mode, &points, tenants);
    let destination = scratch.path().to_path_buf();
    let run_destination = destination.clone();
    let result = tokio::task::spawn_blocking(move || {
        terrabase::backup::run_backup(engine.as_ref(), plan, &run_destination)
    })
    .await
    .map_err(|error| format!("the backup worker stopped unexpectedly: {error}"))?
    .map_err(|error| format!("the engine refused the backup: {error:?}"))?;
    let uploaded_objects = repository.upload_point(&destination, &result).await?;
    Ok(BackupUploadReceipt {
        result,
        uploaded_objects,
        destination: repository.destination.clone(),
    })
}

/// Downloads and verifies one selected S3 chain, then restores it into a new
/// engine location.
pub async fn restore_from_s3(
    repository: &S3BackupRepository,
    scratch_root: &Path,
    target: PathBuf,
    identity: Option<&str>,
) -> Result<RecoveryReceipt, String> {
    let points = repository.list_recovery_points().await?;
    let chain = select_chain(&points, identity)?;
    let scratch = ScratchDirectory::create(scratch_root, "restore")?;
    download_chain(repository, scratch.path(), &chain).await?;
    let source = scratch.path().to_path_buf();
    let selected = chain
        .last()
        .map(|point| point.manifest.identity().to_owned())
        .ok_or_else(|| "S3 holds no recovery point".to_owned())?;
    tokio::task::spawn_blocking(move || {
        terrabase::RestorePlan::restore_recovery_point(&selected, &source, &target)
    })
    .await
    .map_err(|error| format!("the restore worker stopped unexpectedly: {error}"))?
    .map_err(|error| format!("the engine refused the restore: {error:?}"))
}

/// Downloads and verifies the selected S3 chain without creating an engine.
pub async fn verify_s3_chain(
    repository: &S3BackupRepository,
    scratch_root: &Path,
    identity: Option<&str>,
) -> Result<Vec<BackupManifest>, String> {
    let points = repository.list_recovery_points().await?;
    let chain = select_chain(&points, identity)?;
    let scratch = ScratchDirectory::create(scratch_root, "verify")?;
    download_chain(repository, scratch.path(), &chain).await?;
    let source = scratch.path().to_path_buf();
    tokio::task::spawn_blocking(move || terrabase::backup::verify_latest_chain(&source))
        .await
        .map_err(|error| format!("the verification worker stopped unexpectedly: {error}"))?
        .map_err(|error| format!("the downloaded S3 chain failed verification: {error:?}"))
}

/// Reports whether the scheduler should start another run now.
pub fn scheduled_run_due(
    settings: &S3BackupSettings,
    points: &[RemoteRecoveryPoint],
    now: SystemTime,
) -> bool {
    if !settings.enabled || points.is_empty() {
        return settings.enabled && points.is_empty();
    }
    let newest = points.iter().map(RemoteRecoveryPoint::modified_millis).max().unwrap_or(0);
    let now_millis = unix_millis(now);
    now_millis.saturating_sub(newest) >= duration_millis(settings.incremental_interval())
}

/// Runs Studio's background S3 schedule until the process stops.
pub async fn run_scheduler(state: Arc<crate::state::StudioState>) {
    let mut interval = tokio::time::interval(Duration::from_secs(30));
    interval.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Skip);
    let mut last_failure = None;
    loop {
        interval.tick().await;
        let settings = state.backup_settings();
        if !settings.enabled {
            last_failure = None;
            continue;
        }
        let repository = match S3BackupRepository::from_settings(&settings) {
            Ok(repository) => repository,
            Err(failure) => {
                record_scheduler_failure(&state, &mut last_failure, failure);
                continue;
            }
        };
        let points = match repository.list_recovery_points().await {
            Ok(points) => points,
            Err(failure) => {
                record_scheduler_failure(&state, &mut last_failure, failure);
                continue;
            }
        };
        if !scheduled_run_due(&settings, &points, SystemTime::now()) {
            continue;
        }
        let Ok(_job) = state.backup_job.try_lock() else {
            continue;
        };
        match backup_to_s3(
            &repository,
            Arc::clone(&state.engine),
            &state.config.backup_scratch_dir(),
            &settings,
            BackupRunMode::Automatic,
            Vec::new(),
        )
        .await
        {
            Ok(upload) => {
                last_failure = None;
                state.record_backup_run(true);
                state.record(
                    "backup/scheduled",
                    format!(
                        "recovery point {} committed to {} ({} object(s) uploaded)",
                        &upload.result.manifest().identity()[..12],
                        upload.destination,
                        upload.uploaded_objects,
                    ),
                    true,
                );
            }
            Err(failure) => record_scheduler_failure(&state, &mut last_failure, failure),
        }
    }
}

fn record_scheduler_failure(
    state: &crate::state::StudioState,
    last_failure: &mut Option<String>,
    failure: String,
) {
    state.record_backup_run(false);
    if last_failure.as_ref() == Some(&failure) {
        return;
    }
    state.record("backup/scheduled", failure.clone(), false);
    *last_failure = Some(failure);
}

fn select_backup_plan(
    settings: &S3BackupSettings,
    mode: BackupRunMode,
    points: &[RemoteRecoveryPoint],
    tenants: Vec<terrabase::TenantId>,
) -> terrabase::BackupPlan {
    let newest =
        points.iter().max_by(|left, right| manifest_order(&left.manifest, &right.manifest));
    let newest_full = points
        .iter()
        .filter(|point| !matches!(point.manifest.kind(), BackupKind::Incremental { .. }))
        .map(RemoteRecoveryPoint::modified_millis)
        .max();
    let full_is_due = newest_full.is_none_or(|last| {
        unix_millis(SystemTime::now()).saturating_sub(last)
            >= duration_millis(settings.full_interval())
    });
    let mut plan = match (mode, newest, full_is_due) {
        (BackupRunMode::Full, _, _) | (_, None, _) | (_, _, true) => terrabase::BackupPlan::full(),
        (BackupRunMode::Automatic, Some(point), false) => {
            terrabase::BackupPlan::incremental_since(point.manifest.boundary_sequence())
        }
    };
    if !tenants.is_empty() {
        plan = plan.with_tenants(tenants);
    }
    plan
}

async fn stage_manifests(destination: &Path, points: &[RemoteRecoveryPoint]) -> Result<(), String> {
    for point in points {
        let directory = destination.join(format!("rp-{}", point.manifest.identity()));
        tokio::fs::create_dir_all(&directory)
            .await
            .map_err(|error| format!("cannot create transfer scratch space: {error}"))?;
        tokio::fs::write(directory.join(MANIFEST_FILE), point.manifest.encode())
            .await
            .map_err(|error| format!("cannot stage an S3 manifest: {error}"))?;
    }
    Ok(())
}

async fn download_chain(
    repository: &S3BackupRepository,
    destination: &Path,
    chain: &[RemoteRecoveryPoint],
) -> Result<(), String> {
    stage_manifests(destination, chain).await?;
    let mut downloads = Vec::new();
    for point in chain {
        for member in point.manifest.members() {
            downloads.push((
                point.manifest.identity().to_owned(),
                member.name().to_owned(),
                member.length(),
            ));
        }
    }
    let repository = repository.clone();
    let destination = destination.to_path_buf();
    stream::iter(downloads)
        .map(move |(identity, name, length)| {
            let repository = repository.clone();
            let destination = destination.clone();
            async move {
                let location = repository.object_path(&identity, &name);
                let bytes = repository.read_object(&location, length).await?;
                if u64::try_from(bytes.len()).ok() != Some(length) {
                    return Err(format!("S3 member {location} has the wrong length"));
                }
                let path = destination.join(format!("rp-{identity}")).join(name);
                tokio::fs::write(path, bytes)
                    .await
                    .map_err(|error| format!("cannot write restore scratch data: {error}"))
            }
        })
        .buffer_unordered(DOWNLOAD_CONCURRENCY)
        .try_collect::<Vec<_>>()
        .await?;
    Ok(())
}

fn select_chain(
    points: &[RemoteRecoveryPoint],
    selected_identity: Option<&str>,
) -> Result<Vec<RemoteRecoveryPoint>, String> {
    if points.is_empty() {
        return Err("S3 holds no recovery point".to_owned());
    }
    let head = match selected_identity {
        Some(identity) => points
            .iter()
            .find(|point| point.manifest.identity() == identity)
            .ok_or_else(|| "the requested S3 recovery point does not exist".to_owned())?,
        None => points
            .iter()
            .max_by(|left, right| manifest_order(&left.manifest, &right.manifest))
            .ok_or_else(|| "S3 holds no recovery point".to_owned())?,
    };
    let mut chain = vec![head.clone()];
    let mut visited = BTreeSet::from([head.manifest.identity().to_owned()]);
    while let Some(base) = chain.last().and_then(|point| point.manifest.base_sequence()) {
        let link = points
            .iter()
            .filter(|point| {
                point.manifest.boundary_sequence() == base
                    && !visited.contains(point.manifest.identity())
            })
            .min_by(|left, right| {
                (usize::from(left.manifest.base_sequence().is_some()), left.manifest.identity())
                    .cmp(&(
                        usize::from(right.manifest.base_sequence().is_some()),
                        right.manifest.identity(),
                    ))
            })
            .ok_or_else(|| "the selected S3 recovery chain is incomplete".to_owned())?;
        visited.insert(link.manifest.identity().to_owned());
        chain.push(link.clone());
    }
    chain.reverse();
    validate_chain(&chain)?;
    Ok(chain)
}

fn validate_repository_scope(points: &[RemoteRecoveryPoint]) -> Result<(), String> {
    let Some(first) = points.first() else {
        return Ok(());
    };
    if points.iter().any(|point| {
        point.manifest.database_id() != first.manifest.database_id()
            || point.manifest.partition_id() != first.manifest.partition_id()
    }) {
        return Err(
            "the configured S3 prefix contains recovery points from more than one database"
                .to_owned(),
        );
    }
    Ok(())
}

fn validate_chain(chain: &[RemoteRecoveryPoint]) -> Result<(), String> {
    let Some(root) = chain.first() else {
        return Err("the selected S3 recovery chain is empty".to_owned());
    };
    if root.manifest.base_sequence().is_some() {
        return Err("the selected S3 recovery chain has no full root".to_owned());
    }
    for pair in chain.windows(2) {
        let previous = &pair[0].manifest;
        let current = &pair[1].manifest;
        if current.database_id() != root.manifest.database_id()
            || current.partition_id() != root.manifest.partition_id()
            || current.base_sequence() != Some(previous.boundary_sequence())
            || current.boundary_sequence().get() < previous.boundary_sequence().get()
        {
            return Err("the selected S3 recovery chain is reordered or crosses a scope".to_owned());
        }
    }
    Ok(())
}

fn manifest_order(left: &BackupManifest, right: &BackupManifest) -> std::cmp::Ordering {
    (left.boundary_sequence().get(), usize::from(left.base_sequence().is_some()), left.identity())
        .cmp(&(
            right.boundary_sequence().get(),
            usize::from(right.base_sequence().is_some()),
            right.identity(),
        ))
}

fn safe_store_error(operation: &str, error: impl std::fmt::Display) -> String {
    format!("cannot {operation}: {error}")
}

fn environment_or(name: &str, fallback: &str) -> String {
    std::env::var(name).unwrap_or_else(|_| fallback.to_owned())
}

fn environment_bool(name: &str, fallback: bool) -> bool {
    std::env::var(name).ok().map_or(fallback, |value| {
        matches!(value.trim().to_ascii_lowercase().as_str(), "1" | "true" | "yes" | "on")
    })
}

fn environment_u64(name: &str, fallback: u64) -> u64 {
    std::env::var(name).ok().and_then(|value| value.parse().ok()).unwrap_or(fallback)
}

fn duration_millis(duration: Duration) -> i64 {
    i64::try_from(duration.as_millis()).unwrap_or(i64::MAX)
}

fn unix_millis(time: SystemTime) -> i64 {
    time.duration_since(UNIX_EPOCH)
        .ok()
        .and_then(|duration| i64::try_from(duration.as_millis()).ok())
        .unwrap_or(0)
}

struct ScratchDirectory {
    path: PathBuf,
}

impl ScratchDirectory {
    fn create(root: &Path, purpose: &str) -> Result<Self, String> {
        static COUNTER: AtomicU64 = AtomicU64::new(0);
        std::fs::create_dir_all(root)
            .map_err(|error| format!("cannot create backup scratch root: {error}"))?;
        let index = COUNTER.fetch_add(1, Ordering::Relaxed);
        let path = root.join(format!("{purpose}-{}-{index}", std::process::id()));
        std::fs::create_dir(&path)
            .map_err(|error| format!("cannot create backup scratch directory: {error}"))?;
        Ok(Self { path })
    }

    fn path(&self) -> &Path {
        &self.path
    }
}

impl Drop for ScratchDirectory {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.path);
    }
}

#[cfg(test)]
mod tests {
    use object_store::memory::InMemory;

    use super::*;

    fn test_root(name: &str) -> PathBuf {
        static TEST_COUNTER: AtomicU64 = AtomicU64::new(0);
        let index = TEST_COUNTER.fetch_add(1, Ordering::Relaxed);
        std::env::temp_dir()
            .join(format!("terrabase-studio-s3-{name}-{}-{index}", std::process::id(),))
    }

    fn sample_state(root: &Path) -> Arc<crate::state::StudioState> {
        let state = Arc::new(
            crate::state::StudioState::open(crate::state::Config {
                data_dir: root.join("source"),
                addr: crate::state::DEFAULT_ADDR.to_owned(),
            })
            .unwrap(),
        );
        crate::routes::test_seed_sample(state.as_ref()).unwrap();
        state
    }

    fn memory_repository(store: Arc<InMemory>) -> S3BackupRepository {
        S3BackupRepository::new(store, "database-a", "memory://database-a".to_owned()).unwrap()
    }

    fn test_settings() -> S3BackupSettings {
        S3BackupSettings {
            bucket: "backups".to_owned(),
            prefix: "database-a".to_owned(),
            ..S3BackupSettings::default()
        }
    }

    #[test]
    fn settings_reject_secret_bearing_and_insecure_endpoints() {
        let mut settings = S3BackupSettings {
            bucket: "backups".to_owned(),
            endpoint: "http://s3.example.test".to_owned(),
            ..S3BackupSettings::default()
        };
        assert!(settings.validate().is_err());
        settings.allow_http = true;
        assert!(settings.validate().is_ok());
        settings.endpoint = "https://access:secret@s3.example.test".to_owned();
        assert!(settings.validate().is_err());
    }

    #[tokio::test]
    async fn immutable_uploads_are_idempotent_and_refuse_replacement() {
        let repository = memory_repository(Arc::new(InMemory::new()));
        let location = repository.prefix.clone().join("object");
        assert!(repository.put_immutable(location.clone(), b"same".to_vec()).await.unwrap());
        assert!(!repository.put_immutable(location.clone(), b"same".to_vec()).await.unwrap());
        assert!(repository.put_immutable(location, b"different".to_vec()).await.is_err());
    }

    #[tokio::test]
    async fn full_then_incremental_points_restore_latest_or_an_older_selection() {
        let root = test_root("round-trip");
        let state = sample_state(&root);
        let repository = memory_repository(Arc::new(InMemory::new()));
        let settings = test_settings();

        let full = backup_to_s3(
            &repository,
            Arc::clone(&state.engine),
            &root.join("scratch"),
            &settings,
            BackupRunMode::Full,
            Vec::new(),
        )
        .await
        .unwrap();
        assert!(full.uploaded_objects >= 2);

        crate::routes::test_seed_sample(state.as_ref()).unwrap();
        let incremental = backup_to_s3(
            &repository,
            Arc::clone(&state.engine),
            &root.join("scratch"),
            &settings,
            BackupRunMode::Automatic,
            Vec::new(),
        )
        .await
        .unwrap();
        assert!(matches!(
            incremental.result.manifest().kind(),
            BackupKind::Incremental { base }
                if base == full.result.manifest().boundary_sequence()
        ));

        let points = repository.list_recovery_points().await.unwrap();
        assert_eq!(points.len(), 2);
        assert_eq!(points[0].manifest(), full.result.manifest());
        assert_eq!(points[1].manifest(), incremental.result.manifest());

        let older = restore_from_s3(
            &repository,
            &root.join("scratch"),
            root.join("restored-full"),
            Some(full.result.manifest().identity()),
        )
        .await
        .unwrap();
        assert_eq!(older.selected_identity(), full.result.manifest().identity());

        let latest =
            restore_from_s3(&repository, &root.join("scratch"), root.join("restored-latest"), None)
                .await
                .unwrap();
        assert_eq!(latest.selected_identity(), incremental.result.manifest().identity());
        assert!(latest.restored_documents() > 0);
    }

    #[tokio::test]
    async fn a_member_failure_never_commits_the_manifest() {
        let root = test_root("manifest-last");
        let state = sample_state(&root);
        let destination = root.join("staged");
        let result = terrabase::backup::run_backup(
            state.engine.as_ref(),
            terrabase::BackupPlan::full(),
            &destination,
        )
        .unwrap();
        let missing = destination
            .join(format!("rp-{}", result.manifest().identity()))
            .join(result.manifest().members()[0].name());
        std::fs::remove_file(missing).unwrap();

        let store = Arc::new(InMemory::new());
        let repository = memory_repository(Arc::clone(&store));
        assert!(repository.upload_point(&destination, &result).await.is_err());
        let objects = store.list(Some(&repository.prefix)).try_collect::<Vec<_>>().await.unwrap();
        assert!(objects.iter().all(|object| object.location.filename() != Some(MANIFEST_FILE)));
        assert!(repository.list_recovery_points().await.unwrap().is_empty());
    }

    #[tokio::test]
    async fn corrupt_or_missing_members_refuse_verification_and_restore() {
        let root = test_root("member-refusal");
        let state = sample_state(&root);
        let store = Arc::new(InMemory::new());
        let repository = memory_repository(Arc::clone(&store));
        let uploaded = backup_to_s3(
            &repository,
            Arc::clone(&state.engine),
            &root.join("scratch"),
            &test_settings(),
            BackupRunMode::Full,
            Vec::new(),
        )
        .await
        .unwrap();
        let member = &uploaded.result.manifest().members()[0];
        let location = repository.object_path(uploaded.result.manifest().identity(), member.name());
        let original = store.get(&location).await.unwrap().bytes().await.unwrap();
        assert!(!original.is_empty());

        let mut corrupted = original.to_vec();
        corrupted[0] ^= 0xff;
        store.put(&location, corrupted.into()).await.unwrap();
        let failure = verify_s3_chain(&repository, &root.join("scratch"), None).await.unwrap_err();
        assert!(failure.contains("ChecksumMismatch"));

        store.put(&location, original).await.unwrap();
        verify_s3_chain(&repository, &root.join("scratch"), None).await.unwrap();
        store.delete(&location).await.unwrap();
        assert!(verify_s3_chain(&repository, &root.join("scratch"), None).await.is_err());

        let target = root.join("missing-member-target");
        assert!(
            restore_from_s3(&repository, &root.join("scratch"), target.clone(), None)
                .await
                .is_err()
        );
        assert!(!target.exists());
    }

    #[test]
    fn a_new_enabled_repository_is_immediately_due() {
        let settings = S3BackupSettings {
            enabled: true,
            bucket: "backups".to_owned(),
            ..S3BackupSettings::default()
        };
        assert!(scheduled_run_due(&settings, &[], SystemTime::now()));
    }
}
