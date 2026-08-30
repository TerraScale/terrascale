//! S3-backed backup transport for Studio.
//!
//! TerraBase is not SQLite, so Litestream cannot replicate its WAL. This
//! module keeps the engine's verified logical archive format and gives it the
//! same operating shape: S3 is the durable source of truth, recovery points
//! are uploaded incrementally, and restore hydrates only the selected chain
//! into disposable local scratch space.

use std::collections::BTreeSet;
use std::ffi::OsString;
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use futures_util::{StreamExt, TryStreamExt, stream};
use object_store::aws::{AmazonS3Builder, AmazonS3ConfigKey};
use object_store::path::Path as ObjectPath;
use object_store::{
    Error as ObjectStoreError, ObjectStore, ObjectStoreExt, PutMode, PutOptions, PutPayload, TagSet,
};
use serde::{Deserialize, Serialize};
use terrabase::backup::{BackupKind, BackupManifest, BackupResult, RecoveryReceipt};

#[cfg(all(feature = "local-crypto", feature = "production-fips"))]
compile_error!("select exactly one of `local-crypto` or `production-fips`");

#[cfg(not(any(feature = "local-crypto", feature = "production-fips")))]
compile_error!("select one of `local-crypto` or `production-fips`");

const MANIFEST_FILE: &str = "manifest.tbv2m";
const MANIFEST_LIMIT_BYTES: u64 = 1024 * 1024;
const MAX_BUCKET_BYTES: usize = 255;
const MAX_REGION_BYTES: usize = 128;
const MAX_PREFIX_BYTES: usize = 768;
const MAX_ENDPOINT_BYTES: usize = 2_048;
const MAX_KMS_KEY_ID_BYTES: usize = 2_048;
/// Bounds list memory and manifest fetch work for one repository prefix.
const MAX_RECOVERY_POINTS: usize = 100_000;
/// Bounds decoded manifest memory independently of recovery-point cadence.
const MAX_MANIFEST_SET_BYTES: u64 = 256 * 1024 * 1024;
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
            || self.bucket.len() > MAX_BUCKET_BYTES
            || !self
                .bucket
                .bytes()
                .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'.' | b'_' | b'-'))
        {
            return Err(
                "the S3 bucket name must use only letters, digits, dots, underscores, or hyphens"
                    .to_owned(),
            );
        }
        if self.region.is_empty()
            || self.region.len() > MAX_REGION_BYTES
            || !self
                .region
                .bytes()
                .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'.' | b'_' | b'-'))
        {
            return Err(
                "the S3 region must use only letters, digits, dots, underscores, or hyphens"
                    .to_owned(),
            );
        }
        if self.prefix.is_empty()
            || self.prefix.len() > MAX_PREFIX_BYTES
            || self.prefix.split('/').any(|part| part.is_empty() || part == "." || part == "..")
            || self.prefix.chars().any(char::is_control)
        {
            return Err(
                "the S3 prefix must be a bounded object path without empty or dot segments"
                    .to_owned(),
            );
        }
        if self.incremental_interval_minutes == 0 {
            return Err("the incremental interval must be at least one minute".to_owned());
        }
        if self.full_interval_hours == 0 {
            return Err("the full interval must be at least one hour".to_owned());
        }
        if self.endpoint.is_empty() {
            if self.allow_http {
                return Err("allow HTTP requires an explicit loopback S3 endpoint".to_owned());
            }
        } else {
            if self.endpoint.len() > MAX_ENDPOINT_BYTES {
                return Err("the S3 endpoint exceeds the 2048-byte limit".to_owned());
            }
            let endpoint = self
                .endpoint
                .parse::<axum::http::Uri>()
                .map_err(|_| "the S3 endpoint is not a valid HTTP URI".to_owned())?;
            let scheme = endpoint
                .scheme_str()
                .ok_or_else(|| "the S3 endpoint must include a scheme".to_owned())?;
            let authority = endpoint
                .authority()
                .ok_or_else(|| "the S3 endpoint must include an authority".to_owned())?;
            if authority.host().is_empty() || authority.as_str().contains('@') {
                return Err("the S3 endpoint must not contain credentials".to_owned());
            }
            if endpoint.query().is_some() {
                return Err("the S3 endpoint must not contain a query string".to_owned());
            }
            if scheme.eq_ignore_ascii_case("http") {
                if !self.allow_http {
                    return Err(
                        "the S3 endpoint must use HTTPS unless allow HTTP is explicitly enabled"
                            .to_owned(),
                    );
                }
                let host = authority.host().trim_matches(['[', ']']);
                let loopback = host.eq_ignore_ascii_case("localhost")
                    || host.parse::<std::net::IpAddr>().is_ok_and(|address| address.is_loopback());
                if !loopback {
                    return Err(
                        "an HTTP S3-compatible endpoint must use a loopback address".to_owned()
                    );
                }
            } else if !scheme.eq_ignore_ascii_case("https") {
                return Err("the S3 endpoint scheme must be HTTP or HTTPS".to_owned());
            }
        }
        if self.kms_key_id.len() > MAX_KMS_KEY_ID_BYTES
            || self
                .kms_key_id
                .chars()
                .any(|character| character.is_whitespace() || character.is_control())
        {
            return Err(
                "the KMS key identifier cannot exceed 2048 bytes or contain whitespace or controls"
                    .to_owned(),
            );
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
        let builder = configured_s3_builder(settings, std::env::vars_os())?;
        install_rustls_provider()?;
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
            .take(MAX_RECOVERY_POINTS + 1)
            .try_collect::<Vec<_>>()
            .await
            .map_err(|error| safe_store_error("list S3 recovery points", error))?;
        if objects.len() > MAX_RECOVERY_POINTS {
            return Err(format!(
                "the S3 prefix exceeds the {MAX_RECOVERY_POINTS}-recovery-point limit"
            ));
        }
        let manifest_bytes = objects
            .iter()
            .try_fold(0_u64, |total, object| total.checked_add(object.size))
            .ok_or_else(|| "the S3 manifest listing size overflowed".to_owned())?;
        if manifest_bytes > MAX_MANIFEST_SET_BYTES {
            return Err("the S3 manifests exceed the 256 MiB repository limit".to_owned());
        }

        let repository = self.clone();
        // LIST metadata is only a planning hint. Independently account the
        // bytes returned by GET so a corrupt or incompatible object store
        // cannot bypass the repository bound by under-reporting object sizes.
        let fetched_manifest_bytes = Arc::new(AtomicU64::new(0));
        let fetched = stream::iter(objects)
            .map(move |object| {
                let repository = repository.clone();
                let fetched_manifest_bytes = Arc::clone(&fetched_manifest_bytes);
                async move {
                    if object.size > MANIFEST_LIMIT_BYTES {
                        return Err(
                            "an S3 recovery-point manifest exceeds the 1 MiB limit".to_owned()
                        );
                    }
                    let bytes =
                        repository.read_object(&object.location, MANIFEST_LIMIT_BYTES).await?;
                    let bytes_read = u64::try_from(bytes.len())
                        .map_err(|_| "an S3 manifest length does not fit u64".to_owned())?;
                    account_manifest_bytes(fetched_manifest_bytes.as_ref(), bytes_read)?;
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
        let tags = recovery_point_tags(&manifest);
        let members = manifest.members().to_vec();
        let uploads = stream::iter(members)
            .map(move |member| {
                let repository = repository.clone();
                let local_path = local_point.join(member.name());
                let identity = identity.clone();
                let tags = tags.clone();
                async move {
                    let bytes = tokio::fs::read(&local_path)
                        .await
                        .map_err(|error| format!("cannot read staged backup member: {error}"))?;
                    if !member.matches_bytes(&bytes) {
                        return Err(
                            "a staged backup member failed manifest verification before upload"
                                .to_owned(),
                        );
                    }
                    repository
                        .put_immutable(
                            repository.object_path(&identity, member.name()),
                            bytes,
                            tags,
                        )
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
            .put_immutable(
                self.object_path(manifest.identity(), MANIFEST_FILE),
                manifest.encode(),
                recovery_point_tags(&manifest),
            )
            .await?
        {
            uploaded += 1;
        }
        Ok(uploaded)
    }

    async fn put_immutable(
        &self,
        location: ObjectPath,
        bytes: Vec<u8>,
        tags: TagSet,
    ) -> Result<bool, String> {
        let payload = PutPayload::from(bytes);
        let options = PutOptions { mode: PutMode::Create, tags, ..PutOptions::default() };
        match self.store.put_opts(&location, payload.clone(), options).await {
            Ok(_) => Ok(true),
            // A failed HTTP response is not proof that S3 rejected the PUT:
            // the service may have committed the object before the response
            // was lost. Re-read every failure, including AlreadyExists, and
            // accept only the exact immutable payload.
            Err(error) => self.reconcile_failed_put(&location, &payload, error).await,
        }
    }

    async fn reconcile_failed_put(
        &self,
        location: &ObjectPath,
        expected: &PutPayload,
        upload_error: ObjectStoreError,
    ) -> Result<bool, String> {
        let upload_failure = safe_store_error("upload S3 object", upload_error);
        let metadata = match self.store.head(location).await {
            Ok(metadata) => metadata,
            Err(ObjectStoreError::NotFound { .. }) => return Err(upload_failure),
            Err(error) => {
                return Err(format!(
                    "{upload_failure}; the PUT outcome could not be reconciled: {}",
                    safe_store_error("inspect S3 object", error),
                ));
            }
        };
        match self.existing_matches(location, metadata.size, expected).await {
            Ok(true) => Ok(false),
            Ok(false) => Err(format!("refusing to overwrite the existing S3 object {location}")),
            Err(error) => {
                Err(format!("{upload_failure}; the PUT outcome could not be reconciled: {error}"))
            }
        }
    }

    async fn existing_matches(
        &self,
        location: &ObjectPath,
        stored_size: u64,
        expected: &PutPayload,
    ) -> Result<bool, String> {
        let expected_size = u64::try_from(expected.content_length())
            .map_err(|_| "the staged S3 object length does not fit u64".to_owned())?;
        if stored_size != expected_size {
            return Ok(false);
        }
        let existing = self.read_object(location, expected_size).await?;
        if u64::try_from(existing.len()).ok() != Some(expected_size) {
            return Err(format!("S3 object {location} changed length while it was read"));
        }
        Ok(payload_matches(expected, &existing))
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
        if u64::try_from(bytes.len()).unwrap_or(u64::MAX) > limit {
            return Err(format!("S3 object {location} exceeds its manifest bound"));
        }
        Ok(bytes.to_vec())
    }

    fn object_path(&self, identity: &str, file: &str) -> ObjectPath {
        self.prefix.clone().join(format!("rp-{identity}")).join(file)
    }
}

fn configured_s3_builder(
    settings: &S3BackupSettings,
    environment: impl IntoIterator<Item = (OsString, OsString)>,
) -> Result<AmazonS3Builder, String> {
    settings.validate()?;
    let mut builder = credential_environment_builder(environment)?
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
    Ok(builder)
}

/// Copies only AWS credential-provider settings into a clean S3 builder.
///
/// `AmazonS3Builder::from_env` also imports endpoint, HTTP, proxy, certificate,
/// signing, and encryption switches. Those deployment-wide variables must not
/// override settings that Studio has validated and shown to the operator.
fn credential_environment_builder(
    environment: impl IntoIterator<Item = (OsString, OsString)>,
) -> Result<AmazonS3Builder, String> {
    let mut builder = AmazonS3Builder::new();
    for (name, value) in environment {
        let Some(name) = name.to_str() else {
            continue;
        };
        let key = match name {
            "AWS_ACCESS_KEY_ID" => AmazonS3ConfigKey::AccessKeyId,
            "AWS_SECRET_ACCESS_KEY" => AmazonS3ConfigKey::SecretAccessKey,
            "AWS_SESSION_TOKEN" => AmazonS3ConfigKey::Token,
            "AWS_WEB_IDENTITY_TOKEN_FILE" => AmazonS3ConfigKey::WebIdentityTokenFile,
            "AWS_ROLE_ARN" => AmazonS3ConfigKey::RoleArn,
            "AWS_ROLE_SESSION_NAME" => AmazonS3ConfigKey::RoleSessionName,
            "AWS_ENDPOINT_URL_STS" => AmazonS3ConfigKey::StsEndpoint,
            "AWS_CONTAINER_CREDENTIALS_RELATIVE_URI" => {
                AmazonS3ConfigKey::ContainerCredentialsRelativeUri
            }
            "AWS_CONTAINER_CREDENTIALS_FULL_URI" => AmazonS3ConfigKey::ContainerCredentialsFullUri,
            "AWS_CONTAINER_AUTHORIZATION_TOKEN_FILE" => {
                AmazonS3ConfigKey::ContainerAuthorizationTokenFile
            }
            _ => continue,
        };
        let value =
            value.into_string().map_err(|_| format!("{name} must contain valid Unicode"))?;
        validate_credential_environment(name, &value)?;
        builder = builder.with_config(key, value);
    }
    Ok(builder)
}

fn validate_credential_environment(name: &str, value: &str) -> Result<(), String> {
    match name {
        "AWS_ENDPOINT_URL_STS" => validate_credential_endpoint(value, true, name),
        "AWS_CONTAINER_CREDENTIALS_FULL_URI" => validate_credential_endpoint(value, false, name),
        "AWS_CONTAINER_CREDENTIALS_RELATIVE_URI" => {
            if value.is_empty()
                || value.len() > MAX_ENDPOINT_BYTES
                || !value.starts_with('/')
                || value.starts_with("//")
                || value.chars().any(char::is_control)
            {
                return Err(format!(
                    "{name} must be a bounded absolute path on the ECS credential endpoint"
                ));
            }
            Ok(())
        }
        "AWS_WEB_IDENTITY_TOKEN_FILE" | "AWS_CONTAINER_AUTHORIZATION_TOKEN_FILE" => {
            if value.is_empty() || value.len() > MAX_ENDPOINT_BYTES || value.contains('\0') {
                return Err(format!("{name} must be a bounded filesystem path"));
            }
            Ok(())
        }
        "AWS_ROLE_ARN" | "AWS_ROLE_SESSION_NAME" => {
            if value.is_empty()
                || value.len() > MAX_ENDPOINT_BYTES
                || value.chars().any(char::is_control)
            {
                return Err(format!("{name} is empty, oversized, or contains a control character"));
            }
            Ok(())
        }
        _ => Ok(()),
    }
}

fn validate_credential_endpoint(value: &str, https_only: bool, name: &str) -> Result<(), String> {
    if value.is_empty() || value.len() > MAX_ENDPOINT_BYTES {
        return Err(format!("{name} is empty or exceeds the 2048-byte limit"));
    }
    let endpoint =
        value.parse::<axum::http::Uri>().map_err(|_| format!("{name} is not a valid HTTP URI"))?;
    let scheme = endpoint.scheme_str().ok_or_else(|| format!("{name} must include a scheme"))?;
    let authority =
        endpoint.authority().ok_or_else(|| format!("{name} must include an authority"))?;
    if authority.host().is_empty() || authority.as_str().contains('@') || endpoint.query().is_some()
    {
        return Err(format!("{name} must not contain credentials or a query string"));
    }
    if scheme.eq_ignore_ascii_case("https") {
        return Ok(());
    }
    if https_only || !scheme.eq_ignore_ascii_case("http") {
        return Err(format!("{name} must use HTTPS"));
    }

    // AWS permits cleartext container-credential requests only to loopback or
    // its fixed task/pod identity link-local endpoints. The authorization
    // token file must never be sent to an arbitrary HTTP host.
    let host = authority.host().trim_matches(['[', ']']);
    let approved = host.eq_ignore_ascii_case("localhost")
        || host.parse::<std::net::IpAddr>().is_ok_and(|address| {
            address.is_loopback()
                || address == std::net::IpAddr::V4(std::net::Ipv4Addr::new(169, 254, 170, 2))
                || address == std::net::IpAddr::V4(std::net::Ipv4Addr::new(169, 254, 170, 23))
                || address
                    == std::net::IpAddr::V6(
                        "fd00:ec2::23".parse().expect("the EKS credential address is valid"),
                    )
        });
    if !approved {
        return Err(format!(
            "{name} must use HTTPS or an AWS-approved loopback/link-local credential host"
        ));
    }
    Ok(())
}

fn install_rustls_provider() -> Result<(), String> {
    #[cfg(feature = "local-crypto")]
    let provider = rustls::crypto::ring::default_provider();

    #[cfg(feature = "production-fips")]
    let provider = {
        let provider = rustls::crypto::aws_lc_rs::default_provider();
        if !provider.fips() {
            return Err("the production S3 TLS provider is not in FIPS mode".to_owned());
        }
        provider
    };

    if provider.install_default().is_ok() {
        return Ok(());
    }

    let installed = rustls::crypto::CryptoProvider::get_default()
        .ok_or_else(|| "cannot install the S3 TLS crypto provider".to_owned())?;
    #[cfg(feature = "production-fips")]
    if !installed.fips() {
        return Err("the process installed a non-FIPS TLS provider before S3 setup".to_owned());
    }
    #[cfg(feature = "local-crypto")]
    let _ = installed;
    Ok(())
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
    backup_to_s3_with_points(repository, engine, scratch_root, settings, mode, tenants, &points)
        .await
}

async fn backup_to_s3_with_points(
    repository: &S3BackupRepository,
    engine: Arc<terrabase::Engine>,
    scratch_root: &Path,
    settings: &S3BackupSettings,
    mode: BackupRunMode,
    tenants: Vec<terrabase::TenantId>,
    points: &[RemoteRecoveryPoint],
) -> Result<BackupUploadReceipt, String> {
    let scratch = ScratchDirectory::create(scratch_root, "upload")?;
    let fallback_tenants = tenants.clone();
    let (plan, anchor) = select_backup_plan(settings, mode, points, tenants)?;
    if let Some(anchor) = anchor {
        stage_manifests(scratch.path(), std::slice::from_ref(anchor)).await?;
    }
    let destination = scratch.path().to_path_buf();
    let mut result = run_engine_backup(Arc::clone(&engine), plan, destination.clone()).await?;
    if let Some(anchor) = anchor
        && incremental_expands_root_scope(points, anchor, result.manifest())?
    {
        let mut full = terrabase::BackupPlan::full();
        if !fallback_tenants.is_empty() {
            full = full.with_tenants(fallback_tenants);
        }
        result = run_engine_backup(engine, full, destination.clone()).await?;
    }
    let uploaded_objects = repository.upload_point(&destination, &result).await?;
    Ok(BackupUploadReceipt {
        result,
        uploaded_objects,
        destination: repository.destination.clone(),
    })
}

async fn run_engine_backup(
    engine: Arc<terrabase::Engine>,
    plan: terrabase::BackupPlan,
    destination: PathBuf,
) -> Result<BackupResult, String> {
    tokio::task::spawn_blocking(move || {
        terrabase::backup::run_backup(engine.as_ref(), plan, &destination)
    })
    .await
    .map_err(|error| format!("the backup worker stopped unexpectedly: {error}"))?
    .map_err(|error| format!("the engine refused the backup: {error:?}"))
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
    settings.enabled && scheduled_run_delay(settings, points, now).is_zero()
}

/// Runs Studio's background S3 schedule until the process stops.
pub async fn run_scheduler(state: Arc<crate::state::StudioState>) {
    let mut interval = tokio::time::interval(Duration::from_secs(30));
    interval.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Skip);
    let mut shutdown = state.shutdown_receiver();
    let mut last_failure = None;
    let mut observed_settings = None;
    let mut next_repository_check = tokio::time::Instant::now();
    loop {
        tokio::select! {
            changed = shutdown.changed() => {
                if changed.is_err() || *shutdown.borrow() {
                    break;
                }
                continue;
            }
            _ = interval.tick() => {}
        }
        let settings = state.backup_settings();
        if !settings.enabled {
            last_failure = None;
            observed_settings = None;
            continue;
        }
        if observed_settings.as_ref() != Some(&settings) {
            observed_settings = Some(settings.clone());
            next_repository_check = tokio::time::Instant::now();
        }
        if tokio::time::Instant::now() < next_repository_check {
            continue;
        }
        let repository = match S3BackupRepository::from_settings(&settings) {
            Ok(repository) => repository,
            Err(failure) => {
                record_scheduler_failure(&state, &mut last_failure, failure);
                next_repository_check = tokio::time::Instant::now() + Duration::from_secs(30);
                continue;
            }
        };
        let points = match repository.list_recovery_points().await {
            Ok(points) => points,
            Err(failure) => {
                record_scheduler_failure(&state, &mut last_failure, failure);
                next_repository_check = tokio::time::Instant::now() + Duration::from_secs(30);
                continue;
            }
        };
        let now = SystemTime::now();
        if !scheduled_run_due(&settings, &points, now) {
            let delay = scheduled_run_delay(&settings, &points, now);
            next_repository_check = tokio::time::Instant::now() + delay;
            continue;
        }
        let Ok(_job) = state.backup_job.try_lock() else {
            continue;
        };
        match backup_to_s3_with_points(
            &repository,
            Arc::clone(&state.engine),
            &state.config.backup_scratch_dir(),
            &settings,
            BackupRunMode::Automatic,
            Vec::new(),
            &points,
        )
        .await
        {
            Ok(upload) => {
                last_failure = None;
                next_repository_check =
                    tokio::time::Instant::now() + settings.incremental_interval();
                state.record_backup_run(true);
                state.record(
                    "backup/scheduled",
                    format!(
                        "recovery point {} committed to {} ({} object(s) uploaded)",
                        identity_prefix(upload.result.manifest().identity()),
                        upload.destination,
                        upload.uploaded_objects,
                    ),
                    true,
                );
            }
            Err(failure) => {
                record_scheduler_failure(&state, &mut last_failure, failure);
                next_repository_check = tokio::time::Instant::now() + Duration::from_secs(30);
            }
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

fn select_backup_plan<'points>(
    settings: &S3BackupSettings,
    mode: BackupRunMode,
    points: &'points [RemoteRecoveryPoint],
    tenants: Vec<terrabase::TenantId>,
) -> Result<(terrabase::BackupPlan, Option<&'points RemoteRecoveryPoint>), String> {
    let newest_full = points
        .iter()
        .filter(|point| !matches!(point.manifest.kind(), BackupKind::Incremental { .. }))
        .map(RemoteRecoveryPoint::modified_millis)
        .max();
    let full_is_due = newest_full.is_none_or(|last| {
        unix_millis(SystemTime::now()).saturating_sub(last)
            >= duration_millis(settings.full_interval())
    });
    let (mut plan, anchor) = match (mode, points.is_empty(), full_is_due) {
        (BackupRunMode::Full, _, _) | (_, true, _) | (_, _, true) => {
            (terrabase::BackupPlan::full(), None)
        }
        (BackupRunMode::Automatic, false, false) => {
            // Do not extend an arbitrary content-hash winner when the
            // repository contains forked heads at the same boundary.
            let chain = select_chain(points, None)?;
            let identity = chain
                .last()
                .map(|point| point.manifest.identity())
                .ok_or_else(|| "the selected S3 recovery chain is empty".to_owned())?;
            let point = points
                .iter()
                .find(|point| point.manifest.identity() == identity)
                .ok_or_else(|| "the selected S3 recovery chain head disappeared".to_owned())?;
            (
                terrabase::BackupPlan::incremental_since(point.manifest.boundary_sequence()),
                Some(point),
            )
        }
    };
    if !tenants.is_empty() {
        plan = plan.with_tenants(tenants);
    }
    Ok((plan, anchor))
}

fn scheduled_run_delay(
    settings: &S3BackupSettings,
    points: &[RemoteRecoveryPoint],
    now: SystemTime,
) -> Duration {
    let Some(newest) = points.iter().map(RemoteRecoveryPoint::modified_millis).max() else {
        return Duration::ZERO;
    };
    let elapsed_millis = unix_millis(now).saturating_sub(newest).max(0);
    let elapsed = Duration::from_millis(u64::try_from(elapsed_millis).unwrap_or(u64::MAX));
    settings.incremental_interval().saturating_sub(elapsed)
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
        None => select_latest_head(points)?,
    };
    let mut chain = vec![head.clone()];
    let mut visited = BTreeSet::from([head.manifest.identity().to_owned()]);
    while let Some(base) = chain.last().and_then(|point| point.manifest.base_sequence()) {
        let current =
            chain.last().ok_or_else(|| "the selected S3 recovery chain is empty".to_owned())?;
        let required_tenants: BTreeSet<_> = chain
            .iter()
            .flat_map(|point| point.manifest.tenants().iter().map(|entry| entry.tenant()))
            .collect();
        let link = select_predecessor(points, current, base, &visited, &required_tenants)?;
        visited.insert(link.manifest.identity().to_owned());
        chain.push(link.clone());
    }
    chain.reverse();
    validate_chain(&chain)?;
    Ok(chain)
}

/// Selects a unique newest semantic head. An advancing incremental is the
/// substantive frontier at its boundary; a subsequent self-bound
/// incremental is redundant and cannot turn that linear history into a fork.
fn select_latest_head(points: &[RemoteRecoveryPoint]) -> Result<&RemoteRecoveryPoint, String> {
    let newest_boundary = points
        .iter()
        .map(|point| point.manifest.boundary_sequence())
        .max()
        .ok_or_else(|| "S3 holds no recovery point".to_owned())?;
    let at_boundary: Vec<_> = points
        .iter()
        .filter(|point| point.manifest.boundary_sequence() == newest_boundary)
        .collect();
    let incrementals: Vec<_> = at_boundary
        .iter()
        .copied()
        .filter(|point| point.manifest.base_sequence().is_some())
        .collect();
    let advancing: Vec<_> = incrementals
        .iter()
        .copied()
        .filter(|point| point.manifest.base_sequence().is_some_and(|base| base < newest_boundary))
        .collect();
    let candidates = if !advancing.is_empty() {
        &advancing
    } else if !incrementals.is_empty() {
        &incrementals
    } else {
        &at_boundary
    };
    unique_chain_candidate(candidates, "the newest S3 recovery boundary is ambiguous")
}

/// Selects one provable predecessor at `base`. A self-bound incremental is
/// an empty, redundant link and is skipped. A full/snapshot point at the
/// exact boundary is self-contained and therefore preferred over an
/// advancing incremental, matching the engine restore contract.
fn select_predecessor<'points>(
    points: &'points [RemoteRecoveryPoint],
    current: &RemoteRecoveryPoint,
    base: terrabase::StorageSequence,
    visited: &BTreeSet<String>,
    required_tenants: &BTreeSet<&terrabase::TenantId>,
) -> Result<&'points RemoteRecoveryPoint, String> {
    let matching: Vec<_> = points
        .iter()
        .filter(|point| {
            point.manifest.boundary_sequence() == base
                && point.manifest.database_id() == current.manifest.database_id()
                && point.manifest.partition_id() == current.manifest.partition_id()
                && !visited.contains(point.manifest.identity())
        })
        .collect();
    let roots: Vec<_> = matching
        .iter()
        .copied()
        .filter(|point| point.manifest.base_sequence().is_none())
        .filter(|point| root_covers_chain_scope(point, required_tenants))
        .collect();
    if !roots.is_empty() {
        return unique_chain_candidate(
            &roots,
            "the selected S3 recovery chain has ambiguous full roots",
        );
    }
    let advancing: Vec<_> = matching
        .into_iter()
        .filter(|point| point.manifest.base_sequence().is_some_and(|parent| parent < base))
        .collect();
    if advancing.is_empty() {
        return Err("the selected S3 recovery chain is incomplete".to_owned());
    }
    unique_chain_candidate(
        &advancing,
        "the selected S3 recovery chain has ambiguous incremental predecessors",
    )
}

fn root_covers_chain_scope(
    root: &RemoteRecoveryPoint,
    required_tenants: &BTreeSet<&terrabase::TenantId>,
) -> bool {
    let root_tenants: BTreeSet<_> =
        root.manifest.tenants().iter().map(|entry| entry.tenant()).collect();
    required_tenants.iter().all(|tenant| root_tenants.contains(*tenant))
}

fn unique_chain_candidate<'candidate>(
    candidates: &[&'candidate RemoteRecoveryPoint],
    ambiguity: &str,
) -> Result<&'candidate RemoteRecoveryPoint, String> {
    match candidates {
        [candidate] => Ok(*candidate),
        [] => Err("the selected S3 recovery chain is incomplete".to_owned()),
        _ => Err(ambiguity.to_owned()),
    }
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
    let root_tenants: BTreeSet<_> =
        root.manifest.tenants().iter().map(|entry| entry.tenant()).collect();
    for pair in chain.windows(2) {
        let previous = &pair[0].manifest;
        let current = &pair[1].manifest;
        if current.database_id() != root.manifest.database_id()
            || current.partition_id() != root.manifest.partition_id()
            || current.base_sequence() != Some(previous.boundary_sequence())
            || current.boundary_sequence().get() < previous.boundary_sequence().get()
            || current.tenants().iter().any(|entry| !root_tenants.contains(entry.tenant()))
        {
            return Err("the selected S3 recovery chain is reordered or crosses a scope".to_owned());
        }
    }
    Ok(())
}

fn incremental_expands_root_scope(
    points: &[RemoteRecoveryPoint],
    anchor: &RemoteRecoveryPoint,
    candidate: &BackupManifest,
) -> Result<bool, String> {
    let chain = select_chain(points, Some(anchor.manifest.identity()))?;
    let root = chain.first().ok_or_else(|| "the selected S3 recovery chain is empty".to_owned())?;
    let root_tenants: BTreeSet<_> =
        root.manifest.tenants().iter().map(|entry| entry.tenant()).collect();
    Ok(candidate.tenants().iter().any(|entry| !root_tenants.contains(entry.tenant())))
}

fn manifest_order(left: &BackupManifest, right: &BackupManifest) -> std::cmp::Ordering {
    (left.boundary_sequence().get(), usize::from(left.base_sequence().is_some()), left.identity())
        .cmp(&(
            right.boundary_sequence().get(),
            usize::from(right.base_sequence().is_some()),
            right.identity(),
        ))
}

fn recovery_point_tags(manifest: &BackupManifest) -> TagSet {
    let mut tags = TagSet::default();
    let kind = match manifest.kind() {
        BackupKind::Incremental { .. } => "incremental",
        BackupKind::Full | BackupKind::Snapshot => "full",
    };
    tags.push("terrabase-kind", kind);
    tags
}

fn payload_matches(payload: &PutPayload, bytes: &[u8]) -> bool {
    let mut offset = 0usize;
    for chunk in payload {
        let Some(end) = offset.checked_add(chunk.len()) else {
            return false;
        };
        if bytes.get(offset..end) != Some(chunk.as_ref()) {
            return false;
        }
        offset = end;
    }
    offset == bytes.len()
}

fn account_manifest_bytes(total: &AtomicU64, bytes: u64) -> Result<(), String> {
    total
        .fetch_update(Ordering::Relaxed, Ordering::Relaxed, |current| {
            current.checked_add(bytes).filter(|next| *next <= MAX_MANIFEST_SET_BYTES)
        })
        .map(|_| ())
        .map_err(|_| "the fetched S3 manifests exceed the 256 MiB repository limit".to_owned())
}

fn identity_prefix(identity: &str) -> &str {
    identity.get(..12).unwrap_or(identity)
}

fn safe_store_error(operation: &str, error: ObjectStoreError) -> String {
    // S3 error sources can include an untrusted response body. Do not copy the
    // provider's text into Studio receipts or the in-memory action journal: a
    // hostile endpoint could echo a session token that it received in a signed
    // request. Variant names retain the useful operator-facing category.
    let category = match error {
        ObjectStoreError::NotFound { .. } => "the S3 object was not found",
        ObjectStoreError::InvalidPath { .. } => "S3 returned an invalid object path",
        ObjectStoreError::JoinError { .. } => "the S3 request worker stopped",
        ObjectStoreError::NotSupported { .. } => "the S3 operation is not supported",
        ObjectStoreError::AlreadyExists { .. } => "the S3 object already exists",
        ObjectStoreError::Precondition { .. } => "an S3 request precondition failed",
        ObjectStoreError::NotModified { .. } => "S3 reported that the object was not modified",
        ObjectStoreError::NotImplemented { .. } => "the S3 operation is not implemented",
        ObjectStoreError::PermissionDenied { .. } => "the S3 credentials lack permission",
        ObjectStoreError::Unauthenticated { .. } => "the S3 credentials were not accepted",
        ObjectStoreError::UnknownConfigurationKey { .. } => {
            "the S3 client received an unsupported configuration key"
        }
        ObjectStoreError::Generic { .. } => "the S3 request or client configuration failed",
        _ => "the S3 request failed",
    };
    format!("cannot {operation}: {category}")
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

    fn os_environment(values: &[(&'static str, &'static str)]) -> Vec<(OsString, OsString)> {
        values.iter().map(|(name, value)| (OsString::from(name), OsString::from(value))).collect()
    }

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

    fn remote_point(manifest: &BackupManifest) -> RemoteRecoveryPoint {
        RemoteRecoveryPoint {
            manifest: manifest.clone(),
            modified_millis: 0,
            modified_at: "1970-01-01T00:00:00Z".to_owned(),
        }
    }

    fn put_test_document(
        state: &crate::state::StudioState,
        tenant_name: &str,
        document_name: &str,
    ) {
        let tenant = crate::engine_ops::tenant(tenant_name).unwrap();
        let collection = crate::engine_ops::collection("users").unwrap();
        let document = crate::engine_ops::document_id(document_name).unwrap();
        let outcome = crate::engine_ops::put_document(
            state.engine.as_ref(),
            state.next_operation().unwrap(),
            &tenant,
            &collection,
            &document,
            crate::json::from_json(serde_json::json!({"tenant": tenant_name})),
            terrabase::ExpectedVersion::Any,
        )
        .unwrap();
        assert!(matches!(outcome, terrabase::CommitOutcome::Committed(_)));
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
        assert!(settings.validate().is_err());
        settings.endpoint = "http://127.0.0.1:9000".to_owned();
        assert!(settings.validate().is_ok());
        settings.endpoint = "http://localhost:9000".to_owned();
        assert!(settings.validate().is_ok());
        settings.endpoint = "http://[::1]:9000".to_owned();
        assert!(settings.validate().is_ok());
        settings.endpoint = "https://access:secret@s3.example.test".to_owned();
        assert!(settings.validate().is_err());
        settings.endpoint = "https://s3.example.test?destination=elsewhere".to_owned();
        assert!(settings.validate().is_err());
        settings.endpoint.clear();
        assert!(settings.validate().is_err(), "allow HTTP requires an explicit endpoint");

        settings.allow_http = false;
        settings.bucket = "backups?redirect".to_owned();
        assert!(settings.validate().is_err());
        settings.bucket = "compatible_bucket.example".to_owned();
        settings.region = "us/east/1".to_owned();
        assert!(settings.validate().is_err());
        settings.region = "us-east-1".to_owned();
        settings.kms_key_id = "alias/backup\nforwarded-header".to_owned();
        assert!(settings.validate().is_err());
    }

    #[test]
    fn aws_environment_cannot_override_validated_transport_signing_or_encryption() {
        let settings = S3BackupSettings {
            bucket: "studio-backups".to_owned(),
            region: "us-east-2".to_owned(),
            endpoint: "https://trusted-s3.example.test".to_owned(),
            kms_key_id: "alias/terrabase-backups".to_owned(),
            ..S3BackupSettings::default()
        };
        let environment = os_environment(&[
            ("AWS_ACCESS_KEY_ID", "static-access-id"),
            ("AWS_SECRET_ACCESS_KEY", "static-secret"),
            ("AWS_SESSION_TOKEN", "static-session"),
            ("AWS_ENDPOINT_URL_S3", "http://attacker.example.test"),
            ("AWS_SKIP_SIGNATURE", "true"),
            ("AWS_UNSIGNED_PAYLOAD", "true"),
            ("AWS_ALLOW_HTTP", "true"),
            ("AWS_ALLOW_INVALID_CERTIFICATES", "true"),
            ("AWS_PROXY_URL", "http://attacker.example.test"),
            ("AWS_IMDSV1_FALLBACK", "true"),
            ("AWS_METADATA_ENDPOINT", "http://attacker.example.test"),
            ("AWS_SERVER_SIDE_ENCRYPTION", "sse-c"),
            ("AWS_SSE_CUSTOMER_KEY_BASE64", "secret-customer-key"),
        ]);
        let builder = configured_s3_builder(&settings, environment).unwrap();

        assert_eq!(
            builder.get_config_value(&AmazonS3ConfigKey::Endpoint).as_deref(),
            Some("https://trusted-s3.example.test"),
        );
        assert_eq!(builder.get_config_value(&AmazonS3ConfigKey::S3Endpoint), None);
        assert_eq!(
            builder.get_config_value(&AmazonS3ConfigKey::SkipSignature).as_deref(),
            Some("false"),
        );
        assert_eq!(
            builder.get_config_value(&AmazonS3ConfigKey::UnsignedPayload).as_deref(),
            Some("false"),
        );
        for key in ["aws_allow_http", "aws_allow_invalid_certificates"] {
            let key = key.parse::<AmazonS3ConfigKey>().unwrap();
            assert_eq!(builder.get_config_value(&key).as_deref(), Some("false"));
        }
        let proxy = "aws_proxy_url".parse::<AmazonS3ConfigKey>().unwrap();
        assert_eq!(builder.get_config_value(&proxy), None);
        assert_eq!(
            builder.get_config_value(&AmazonS3ConfigKey::ImdsV1Fallback).as_deref(),
            Some("false")
        );
        assert_eq!(builder.get_config_value(&AmazonS3ConfigKey::MetadataEndpoint), None);

        let encryption = "aws_server_side_encryption".parse::<AmazonS3ConfigKey>().unwrap();
        let kms_key = "aws_sse_kms_key_id".parse::<AmazonS3ConfigKey>().unwrap();
        let customer_key = "aws_sse_customer_key_base64".parse::<AmazonS3ConfigKey>().unwrap();
        assert_eq!(builder.get_config_value(&encryption).as_deref(), Some("aws:kms"));
        assert_eq!(builder.get_config_value(&kms_key).as_deref(), Some("alias/terrabase-backups"),);
        assert_eq!(builder.get_config_value(&customer_key), None);
        assert_eq!(
            builder.get_config_value(&AmazonS3ConfigKey::AccessKeyId).as_deref(),
            Some("static-access-id"),
        );
        assert_eq!(
            builder.get_config_value(&AmazonS3ConfigKey::SecretAccessKey).as_deref(),
            Some("static-secret"),
        );
        assert_eq!(
            builder.get_config_value(&AmazonS3ConfigKey::Token).as_deref(),
            Some("static-session"),
        );
    }

    #[test]
    fn standard_aws_credential_provider_inputs_remain_available() {
        install_rustls_provider().unwrap();
        let settings = test_settings();
        let providers = [
            os_environment(&[
                ("AWS_ACCESS_KEY_ID", "access"),
                ("AWS_SECRET_ACCESS_KEY", "secret"),
                ("AWS_SESSION_TOKEN", "session"),
            ]),
            os_environment(&[
                ("AWS_WEB_IDENTITY_TOKEN_FILE", "/var/run/secrets/aws/token"),
                ("AWS_ROLE_ARN", "arn:aws:iam::123456789012:role/backup"),
                ("AWS_ROLE_SESSION_NAME", "terrabase-backup"),
                ("AWS_ENDPOINT_URL_STS", "https://sts.example.test/assume-role"),
            ]),
            os_environment(&[(
                "AWS_CONTAINER_CREDENTIALS_RELATIVE_URI",
                "/v2/credentials/terrabase",
            )]),
            os_environment(&[
                ("AWS_CONTAINER_CREDENTIALS_FULL_URI", "http://169.254.170.23/v1/credentials"),
                ("AWS_CONTAINER_AUTHORIZATION_TOKEN_FILE", "/var/run/secrets/eks/token"),
            ]),
            Vec::new(),
        ];
        for environment in providers {
            configured_s3_builder(&settings, environment).unwrap().build().unwrap();
        }

        assert!(
            configured_s3_builder(
                &settings,
                os_environment(&[("AWS_ENDPOINT_URL_STS", "http://sts.example.test")]),
            )
            .is_err()
        );
        assert!(
            configured_s3_builder(
                &settings,
                os_environment(&[
                    ("AWS_CONTAINER_CREDENTIALS_FULL_URI", "http://attacker.example.test/token"),
                    ("AWS_CONTAINER_AUTHORIZATION_TOKEN_FILE", "/var/run/secrets/eks/token"),
                ]),
            )
            .is_err()
        );
    }

    #[test]
    fn s3_error_receipts_do_not_expose_provider_response_bodies() {
        let marker = "AWS_SESSION_TOKEN=provider-echoed-secret";
        let error = ObjectStoreError::Generic {
            store: "test",
            source: Box::new(std::io::Error::other(marker)),
        };
        let rendered = safe_store_error("test S3", error);
        assert!(!rendered.contains(marker));
        assert_eq!(rendered, "cannot test S3: the S3 request or client configuration failed");
    }

    #[test]
    fn s3_tls_provider_matches_the_studio_crypto_profile() {
        install_rustls_provider().unwrap();
        let installed = rustls::crypto::CryptoProvider::get_default()
            .expect("S3 setup installs a process-wide TLS provider");
        #[cfg(feature = "production-fips")]
        assert!(installed.fips());
        #[cfg(feature = "local-crypto")]
        let _ = installed;
    }

    #[tokio::test]
    async fn immutable_uploads_are_idempotent_and_refuse_replacement() {
        let repository = memory_repository(Arc::new(InMemory::new()));
        let location = repository.prefix.clone().join("object");
        assert!(
            repository
                .put_immutable(location.clone(), b"same".to_vec(), TagSet::default())
                .await
                .unwrap()
        );
        assert!(
            !repository
                .put_immutable(location.clone(), b"same".to_vec(), TagSet::default())
                .await
                .unwrap()
        );
        assert!(
            repository
                .put_immutable(location, b"different".to_vec(), TagSet::default())
                .await
                .is_err()
        );
    }

    #[test]
    fn fetched_manifest_budget_does_not_trust_listing_sizes() {
        let total = AtomicU64::new(MAX_MANIFEST_SET_BYTES - 1);
        account_manifest_bytes(&total, 1).unwrap();
        assert_eq!(total.load(Ordering::Relaxed), MAX_MANIFEST_SET_BYTES);

        assert!(account_manifest_bytes(&total, 1).is_err());
        assert_eq!(total.load(Ordering::Relaxed), MAX_MANIFEST_SET_BYTES);
    }

    #[tokio::test]
    async fn a_lost_put_response_is_reconciled_from_the_committed_object() {
        let store = Arc::new(InMemory::new());
        let repository = memory_repository(Arc::clone(&store));
        let location = repository.prefix.clone().join("ambiguous-object");
        let payload = PutPayload::from(b"committed".to_vec());
        store.put(&location, payload.clone()).await.unwrap();

        let response_lost = ObjectStoreError::Generic {
            store: "test",
            source: Box::new(std::io::Error::other("response lost after commit")),
        };
        assert!(
            !repository.reconcile_failed_put(&location, &payload, response_lost).await.unwrap()
        );
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
        assert_eq!(recovery_point_tags(full.result.manifest()).encoded(), "terrabase-kind=full");

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
        assert_eq!(
            recovery_point_tags(incremental.result.manifest()).encoded(),
            "terrabase-kind=incremental",
        );

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

    #[test]
    fn an_incremental_refuses_ambiguous_same_boundary_roots() {
        let root = test_root("ambiguous-roots");
        let state = sample_state(&root);
        put_test_document(state.as_ref(), "later-tenant", "later-root-document");
        let destination = root.join("staged");
        let demo = crate::engine_ops::tenant("demo").unwrap();
        let all_root = terrabase::backup::run_backup(
            state.engine.as_ref(),
            terrabase::BackupPlan::full(),
            &destination,
        )
        .unwrap();
        let demo_root = terrabase::backup::run_backup(
            state.engine.as_ref(),
            terrabase::BackupPlan::full().with_tenants([demo.clone()]),
            &destination,
        )
        .unwrap();
        assert_eq!(
            all_root.manifest().boundary_sequence(),
            demo_root.manifest().boundary_sequence()
        );

        put_test_document(state.as_ref(), "demo", "after-roots");
        let incremental = terrabase::backup::run_backup(
            state.engine.as_ref(),
            terrabase::BackupPlan::incremental_since(all_root.manifest().boundary_sequence())
                .with_tenants([demo]),
            &destination,
        )
        .unwrap();
        let points = vec![
            remote_point(all_root.manifest()),
            remote_point(demo_root.manifest()),
            remote_point(incremental.manifest()),
        ];

        let failure = select_chain(&points, Some(incremental.manifest().identity())).unwrap_err();
        assert_eq!(failure, "the selected S3 recovery chain has ambiguous full roots");
    }

    #[test]
    fn predecessor_selection_uses_the_only_root_that_covers_the_chain_scope() {
        let root = test_root("compatible-root");
        let state = sample_state(&root);
        put_test_document(state.as_ref(), "later-tenant", "later-root-document");
        let destination = root.join("staged");
        let demo = crate::engine_ops::tenant("demo").unwrap();
        let later = crate::engine_ops::tenant("later-tenant").unwrap();
        let demo_root = terrabase::backup::run_backup(
            state.engine.as_ref(),
            terrabase::BackupPlan::full().with_tenants([demo]),
            &destination,
        )
        .unwrap();
        let later_root = terrabase::backup::run_backup(
            state.engine.as_ref(),
            terrabase::BackupPlan::full().with_tenants([later.clone()]),
            &destination,
        )
        .unwrap();
        assert_eq!(
            demo_root.manifest().boundary_sequence(),
            later_root.manifest().boundary_sequence()
        );

        put_test_document(state.as_ref(), "later-tenant", "after-roots");
        let incremental = terrabase::backup::run_backup(
            state.engine.as_ref(),
            terrabase::BackupPlan::incremental_since(later_root.manifest().boundary_sequence())
                .with_tenants([later]),
            &destination,
        )
        .unwrap();
        let points = vec![
            remote_point(demo_root.manifest()),
            remote_point(later_root.manifest()),
            remote_point(incremental.manifest()),
        ];

        let chain = select_chain(&points, Some(incremental.manifest().identity())).unwrap();
        assert_eq!(chain.len(), 2);
        assert_eq!(chain[0].manifest.identity(), later_root.manifest().identity());
    }

    #[test]
    fn latest_selection_refuses_forked_incrementals_at_one_boundary() {
        let root = test_root("forked-heads");
        let state = sample_state(&root);
        put_test_document(state.as_ref(), "later-tenant", "later-root-document");
        let destination = root.join("staged");
        let full = terrabase::backup::run_backup(
            state.engine.as_ref(),
            terrabase::BackupPlan::full(),
            &destination,
        )
        .unwrap();
        let base = full.manifest().boundary_sequence();
        put_test_document(state.as_ref(), "demo", "demo-fork");
        put_test_document(state.as_ref(), "later-tenant", "later-fork");

        let demo_incremental = terrabase::backup::run_backup(
            state.engine.as_ref(),
            terrabase::BackupPlan::incremental_since(base)
                .with_tenants([crate::engine_ops::tenant("demo").unwrap()]),
            &destination,
        )
        .unwrap();
        let later_incremental = terrabase::backup::run_backup(
            state.engine.as_ref(),
            terrabase::BackupPlan::incremental_since(base)
                .with_tenants([crate::engine_ops::tenant("later-tenant").unwrap()]),
            &destination,
        )
        .unwrap();
        assert_eq!(
            demo_incremental.manifest().boundary_sequence(),
            later_incremental.manifest().boundary_sequence()
        );
        let points = vec![
            remote_point(full.manifest()),
            remote_point(demo_incremental.manifest()),
            remote_point(later_incremental.manifest()),
        ];

        assert_eq!(
            select_chain(&points, None).unwrap_err(),
            "the newest S3 recovery boundary is ambiguous"
        );
        assert!(select_chain(&points, Some(demo_incremental.manifest().identity())).is_ok());
        assert!(select_chain(&points, Some(later_incremental.manifest().identity())).is_ok());
    }

    #[tokio::test]
    async fn a_new_tenant_forces_a_new_full_root() {
        let root = test_root("tenant-expansion");
        let state = sample_state(&root);
        let repository = memory_repository(Arc::new(InMemory::new()));
        let settings = test_settings();
        backup_to_s3(
            &repository,
            Arc::clone(&state.engine),
            &root.join("scratch"),
            &settings,
            BackupRunMode::Full,
            Vec::new(),
        )
        .await
        .unwrap();

        let tenant = crate::engine_ops::tenant("later-tenant").unwrap();
        let collection = crate::engine_ops::collection("users").unwrap();
        let document = crate::engine_ops::document_id("later-document").unwrap();
        let outcome = crate::engine_ops::put_document(
            state.engine.as_ref(),
            state.next_operation().unwrap(),
            &tenant,
            &collection,
            &document,
            crate::json::from_json(serde_json::json!({"new": true})),
            terrabase::ExpectedVersion::Any,
        )
        .unwrap();
        assert!(matches!(outcome, terrabase::CommitOutcome::Committed(_)));

        let next = backup_to_s3(
            &repository,
            Arc::clone(&state.engine),
            &root.join("scratch"),
            &settings,
            BackupRunMode::Automatic,
            Vec::new(),
        )
        .await
        .unwrap();
        assert_eq!(next.result.manifest().kind(), BackupKind::Full);
        restore_from_s3(&repository, &root.join("scratch"), root.join("restored"), None)
            .await
            .unwrap();
    }

    #[tokio::test]
    async fn an_all_tenant_run_rolls_over_a_root_that_excluded_an_unchanged_tenant() {
        let root = test_root("preexisting-tenant-expansion");
        let state = sample_state(&root);
        put_test_document(state.as_ref(), "later-tenant", "existing-before-root");
        let repository = memory_repository(Arc::new(InMemory::new()));
        let settings = test_settings();
        backup_to_s3(
            &repository,
            Arc::clone(&state.engine),
            &root.join("scratch"),
            &settings,
            BackupRunMode::Full,
            vec![crate::engine_ops::tenant("demo").unwrap()],
        )
        .await
        .unwrap();

        // There are no writes after the scoped root. The incremental must
        // still bind the all-tenant selection and force a self-contained
        // replacement root instead of silently omitting later-tenant.
        let next = backup_to_s3(
            &repository,
            Arc::clone(&state.engine),
            &root.join("scratch"),
            &settings,
            BackupRunMode::Automatic,
            Vec::new(),
        )
        .await
        .unwrap();
        assert_eq!(next.result.manifest().kind(), BackupKind::Full);
        assert!(
            next.result
                .manifest()
                .tenants()
                .iter()
                .any(|entry| entry.tenant().as_str() == "later-tenant")
        );
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
    async fn same_length_staging_corruption_never_commits_the_manifest() {
        let root = test_root("manifest-checksum");
        let state = sample_state(&root);
        let destination = root.join("staged");
        let result = terrabase::backup::run_backup(
            state.engine.as_ref(),
            terrabase::BackupPlan::full(),
            &destination,
        )
        .unwrap();
        let member = &result.manifest().members()[0];
        let member_path =
            destination.join(format!("rp-{}", result.manifest().identity())).join(member.name());
        let mut corrupted = std::fs::read(&member_path).unwrap();
        assert!(!corrupted.is_empty());
        corrupted[0] ^= 0xff;
        std::fs::write(member_path, corrupted).unwrap();

        let store = Arc::new(InMemory::new());
        let repository = memory_repository(Arc::clone(&store));
        let failure = repository.upload_point(&destination, &result).await.unwrap_err();
        assert!(failure.contains("failed manifest verification"));
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

        store.put(&location, original.into()).await.unwrap();
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

    #[tokio::test]
    async fn scheduler_stops_promptly_when_studio_shutdown_begins() {
        let state = sample_state(&test_root("scheduler-shutdown"));
        let scheduler = tokio::spawn(run_scheduler(Arc::clone(&state)));
        tokio::task::yield_now().await;
        state.begin_shutdown();
        tokio::time::timeout(Duration::from_secs(1), scheduler)
            .await
            .expect("the scheduler observes shutdown")
            .expect("the scheduler task does not panic");
    }

    #[test]
    fn log_identity_prefix_is_safe_for_short_values() {
        assert_eq!(identity_prefix("short"), "short");
        assert_eq!(identity_prefix("1234567890123"), "123456789012");
    }
}
