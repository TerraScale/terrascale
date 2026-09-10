//! Bounded S3 transfers of encrypted sealed recovery points.
//!
//! Each destination is independent. The transfer index is published last and
//! contains the digest of every ciphertext file; it never contains credentials.
//! Restore requires a known point prefix and never consults another provider.
use std::collections::BTreeSet;
use std::fmt;
use std::fs::{self, File, OpenOptions};
use std::io::{Read, Write};
use std::path::Path;
use std::sync::Arc;
use std::time::{Duration, SystemTime};

use futures_util::StreamExt;
use object_store::aws::{AmazonS3Builder, AwsCredential, S3CopyIfNotExists};
use object_store::path::Path as ObjectPath;
use object_store::{ClientOptions, MultipartUpload, ObjectStore, PutMode, RetryConfig};

const INDEX: &str = "transfer-v1.index";
const INDEX_LIMIT: u64 = 1024 * 1024;
const MAX_FILES: usize = 4096;
const ENVELOPE_FILE: &str = "envelope.tbv1";
const ENVELOPE_LIMIT: u64 = 256 * 1024;

/// A destination's independently managed cloud provider.
#[derive(Clone, Copy, Debug, Eq, PartialEq, Ord, PartialOrd)]
pub enum Provider {
    /// OCI S3 compatibility endpoint in the database's OCI Region.
    Oci,
    /// A Tigris bucket with operator-verified geography restrictions.
    Tigris,
    /// Customer-owned S3-compatible destination; workload identity is mandatory.
    CustomerS3,
}

impl Provider {
    /// Returns the canonical wire identity used in durable restore records.
    #[must_use]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Oci => "oci",
            Self::Tigris => "tigris",
            Self::CustomerS3 => "customer-s3",
        }
    }
}

/// S3 credentials whose formatting never reveals key material.
pub struct Credentials {
    access_key: String,
    secret_key: String,
    session_token: Option<String>,
    expires_at: Option<SystemTime>,
}
impl Credentials {
    /// Takes ownership of the provider's access and secret keys.
    #[must_use]
    pub fn new(access_key: String, secret_key: String) -> Self {
        Self { access_key, secret_key, session_token: None, expires_at: None }
    }

    /// Creates short-lived credentials issued by an explicit workload identity.
    #[must_use]
    pub fn temporary(
        access_key: String,
        secret_key: String,
        session_token: String,
        expires_at: SystemTime,
    ) -> Self {
        Self {
            access_key,
            secret_key,
            session_token: Some(session_token),
            expires_at: Some(expires_at),
        }
    }

    fn take_parts(&mut self) -> (String, String, Option<String>) {
        (
            std::mem::take(&mut self.access_key),
            std::mem::take(&mut self.secret_key),
            std::mem::take(&mut self.session_token),
        )
    }
}
impl fmt::Debug for Credentials {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str("Credentials([REDACTED])")
    }
}
impl Drop for Credentials {
    fn drop(&mut self) {
        use zeroize::Zeroize;
        self.access_key.zeroize();
        self.secret_key.zeroize();
        self.session_token.zeroize();
    }
}

/// Redacted failure from a customer workload-identity authority.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum WorkloadIdentityError {
    /// The authority could not issue a currently usable credential.
    Unavailable,
    /// The issued credential was already expired or malformed.
    Invalid,
}

impl fmt::Display for WorkloadIdentityError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str("workload identity credential unavailable")
    }
}
impl std::error::Error for WorkloadIdentityError {}

/// Explicit customer workload-identity seam for S3 credentials.
///
/// Implementations must obtain short-lived credentials from the customer
/// authority. The object store invokes this for each request, so expiry and
/// revocation are observed without ambient environment or static fallback.
#[async_trait::async_trait]
pub trait WorkloadIdentityProvider: fmt::Debug + Send + Sync {
    /// Resolve one currently valid credential.
    async fn credentials(&self) -> Result<Credentials, WorkloadIdentityError>;
}

#[derive(Debug)]
struct S3WorkloadIdentityProvider(Arc<dyn WorkloadIdentityProvider>);

#[async_trait::async_trait]
impl object_store::CredentialProvider for S3WorkloadIdentityProvider {
    type Credential = AwsCredential;

    async fn get_credential(&self) -> object_store::Result<Arc<Self::Credential>> {
        let mut credentials = self.0.credentials().await.map_err(|error| {
            object_store::Error::Generic { store: "S3", source: Box::new(error) }
        })?;
        if credentials.access_key.is_empty()
            || credentials.secret_key.is_empty()
            || credentials.session_token.as_deref().is_some_and(str::is_empty)
            || credentials.expires_at.is_some_and(|expiry| expiry <= SystemTime::now())
        {
            return Err(object_store::Error::Generic {
                store: "S3",
                source: Box::new(WorkloadIdentityError::Invalid),
            });
        }
        let (key_id, secret_key, token) = credentials.take_parts();
        Ok(Arc::new(AwsCredential { key_id, secret_key, token }))
    }
}

/// Operator-owned, admission-validated bucket and resource configuration.
#[derive(Debug)]
pub struct DestinationConfig {
    /// Canonical database Region bound by the admitted provider mapping.
    pub admitted_region: crate::regions::RegionCode,
    /// Cloud provider; separate instances have separate credentials and health.
    pub provider: Provider,
    /// Explicit HTTPS endpoint, without a path or embedded credentials.
    pub endpoint: String,
    /// S3 signing region (OCI Region for OCI; configured Tigris signing region).
    pub signing_region: String,
    /// Database's OCI Region, which must match an OCI destination.
    pub database_region: String,
    /// Bucket whose placement was verified by the operator control plane.
    pub bucket: String,
    /// Bucket's independently verified geography; never inferred from signing region.
    pub verified_geography: String,
    /// Database policy's permitted geographies.
    pub permitted_geographies: Vec<String>,
    /// Nonempty control-plane placement verification revision.
    pub placement_revision: String,
    /// Provider-specific credentials.
    pub credentials: Credentials,
    /// Maximum bytes in any ciphertext artifact, bounding upload memory.
    pub max_artifact_bytes: u64,
    /// Maximum total bytes in a recovery point, bounding restore staging disk.
    pub max_point_bytes: u64,
}

/// Redacted transfer failure. Backend errors and signed URLs are never exposed.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum TransferError {
    /// Endpoint, residency, credential, or resource configuration is invalid.
    Configuration,
    /// Local point is absent, unsealed, malformed, or exceeds resource bounds.
    InvalidPoint,
    /// A file does not match the published length or digest.
    Integrity,
    /// Provider request failed; pending work must be retained and retried.
    Unavailable,
    /// Local filesystem operation failed.
    LocalIo,
}
impl fmt::Display for TransferError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "backup object transfer: {self:?}")
    }
}
impl std::error::Error for TransferError {}

/// Independent S3 destination. Call async methods from the shared backup workers.
///
/// Each transfer holds at most one configured artifact in upload memory and
/// streams downloads. The caller's bounded worker pool limits aggregate usage.
pub struct ObjectDestination {
    identity: String,
    admitted_region: crate::regions::RegionCode,
    provider: Provider,
    store: Arc<dyn ObjectStore>,
    max_artifact_bytes: u64,
    max_point_bytes: u64,
}

#[derive(Debug)]
struct Entry {
    name: String,
    length: u64,
    digest: blake3::Hash,
}

/// Owns an in-flight multipart upload and aborts it if the transfer future is
/// cancelled. S3 cannot clean up parts when the upload handle is simply
/// dropped, so cancellation must schedule the provider-side abort explicitly.
struct MultipartCleanup {
    upload: Option<Box<dyn MultipartUpload>>,
}

impl MultipartCleanup {
    fn new(upload: Box<dyn MultipartUpload>) -> Self {
        Self { upload: Some(upload) }
    }

    async fn put_part(&mut self, data: object_store::PutPayload) -> Result<(), TransferError> {
        self.upload
            .as_mut()
            .expect("multipart upload guard is armed")
            .put_part(data)
            .await
            .map_err(|_| TransferError::Unavailable)
    }

    async fn complete(&mut self) -> Result<(), TransferError> {
        self.upload
            .as_mut()
            .expect("multipart upload guard is armed")
            .complete()
            .await
            .map(|_| ())
            .map_err(|_| TransferError::Unavailable)
    }

    async fn abort(&mut self) {
        if let Some(mut upload) = self.upload.take() {
            let _ = upload.abort().await;
        }
    }

    fn disarm(&mut self) {
        self.upload = None;
    }
}

impl Drop for MultipartCleanup {
    fn drop(&mut self) {
        let Some(mut upload) = self.upload.take() else {
            return;
        };
        // Drop can run while the transfer future is being cancelled. The
        // current runtime remains responsible for the best-effort cleanup;
        // lifecycle rules remain the final fallback for process termination.
        if let Ok(handle) = tokio::runtime::Handle::try_current() {
            handle.spawn(async move {
                let _ = upload.abort().await;
            });
        }
    }
}

impl ObjectDestination {
    /// Publishes one authenticated multi-participant envelope. The envelope
    /// is written before its commit index and read back before success.
    ///
    /// # Errors
    /// Returns [`TransferError`] when validation, publication, or read-back fails.
    pub async fn upload_envelope(
        &self,
        envelope: &Path,
        prefix: &str,
    ) -> Result<(), TransferError> {
        validate_prefix(prefix)?;
        let mut bytes = Vec::new();
        open_regular(envelope)
            .map_err(|_| TransferError::LocalIo)?
            .take(ENVELOPE_LIMIT + 1)
            .read_to_end(&mut bytes)
            .map_err(|_| TransferError::LocalIo)?;
        if bytes.is_empty() || bytes.len() as u64 > ENVELOPE_LIMIT {
            return Err(TransferError::InvalidPoint);
        }
        let key = object_key(prefix, ENVELOPE_FILE)?;
        match self.store.put_opts(&key, bytes.clone().into(), PutMode::Create.into()).await {
            Ok(_) => {}
            Err(object_store::Error::AlreadyExists { .. }) => {
                // A retry may observe the immutable object already published;
                // accept it only when its bytes are exactly the same.
                self.verify_remote(
                    &key,
                    &Entry {
                        name: ENVELOPE_FILE.into(),
                        length: bytes.len() as u64,
                        digest: blake3::hash(&bytes),
                    },
                )
                .await?;
            }
            Err(_) => return Err(TransferError::Unavailable),
        }
        let response = self.store.get(&key).await.map_err(|_| TransferError::Unavailable)?;
        let mut readback = Vec::new();
        let mut stream = response.into_stream();
        while let Some(chunk) = stream.next().await {
            readback.extend_from_slice(&chunk.map_err(|_| TransferError::Unavailable)?);
            if readback.len() as u64 > ENVELOPE_LIMIT {
                return Err(TransferError::Integrity);
            }
        }
        if readback != bytes {
            return Err(TransferError::Integrity);
        }
        Ok(())
    }

    /// Fetches one envelope and enforces its bounded authenticated artifact size.
    ///
    /// # Errors
    /// Returns [`TransferError`] when the prefix is invalid, the object is unavailable,
    /// or the downloaded artifact exceeds its bound or fails integrity checks.
    pub async fn download_envelope(&self, prefix: &str) -> Result<Vec<u8>, TransferError> {
        validate_prefix(prefix)?;
        let response = self
            .store
            .get(&object_key(prefix, ENVELOPE_FILE)?)
            .await
            .map_err(|_| TransferError::Unavailable)?;
        let mut bytes = Vec::new();
        let mut stream = response.into_stream();
        while let Some(chunk) = stream.next().await {
            bytes.extend_from_slice(&chunk.map_err(|_| TransferError::Unavailable)?);
            if bytes.len() as u64 > ENVELOPE_LIMIT {
                return Err(TransferError::Integrity);
            }
        }
        if bytes.is_empty() {
            return Err(TransferError::Integrity);
        }
        Ok(bytes)
    }

    /// Lists sealed envelope prefixes directly below one database namespace.
    /// The listing is bounded across the complete nested result.
    ///
    /// # Errors
    /// Returns [`TransferError`] when the database prefix, listing, or discovered
    /// object violates the envelope namespace and bounds.
    pub async fn list_envelope_prefixes(
        &self,
        database: &str,
        max: usize,
    ) -> Result<Vec<String>, TransferError> {
        validate_prefix(database)?;
        if max == 0 || max > MAX_FILES {
            return Err(TransferError::Configuration);
        }
        let root = format!("{database}/envelopes/");
        let path = ObjectPath::parse(&root).map_err(|_| TransferError::InvalidPoint)?;
        let mut stream = self.store.list(Some(&path));
        let mut prefixes = BTreeSet::new();
        let mut scanned = 0_usize;
        while let Some(result) = stream.next().await {
            let object = result.map_err(|_| TransferError::Unavailable)?;
            scanned = scanned.saturating_add(1);
            if scanned > max.saturating_mul(2).saturating_add(1) {
                return Err(TransferError::InvalidPoint);
            }
            let Some(name) = object.location.as_ref().strip_prefix(&root) else {
                return Err(TransferError::InvalidPoint);
            };
            let Some((envelope, member)) = name.split_once('/') else {
                return Err(TransferError::InvalidPoint);
            };
            validate_envelope_name(envelope)?;
            if member != ENVELOPE_FILE {
                return Err(TransferError::InvalidPoint);
            }
            prefixes.insert(format!("{root}{envelope}"));
            if prefixes.len() > max {
                return Err(TransferError::InvalidPoint);
            }
        }
        Ok(prefixes.into_iter().collect())
    }

    /// Deletes exactly one sealed envelope object in the authenticated
    /// envelope namespace. Unknown provider results remain retryable.
    ///
    /// # Errors
    /// Returns [`TransferError`] when the prefix is invalid or the provider request fails.
    pub async fn delete_envelope(&self, prefix: &str) -> Result<(), TransferError> {
        validate_envelope_prefix(prefix)?;
        self.delete_if_present(&object_key(prefix, ENVELOPE_FILE)?).await
    }

    /// Complete bounded physical inventory for this database's participant archives.
    /// Every returned key is exact: callers must never turn it into a prefix delete.
    pub(crate) async fn retention_participant_objects(
        &self,
        database: &str,
        max_entries: usize,
        max_bytes: u64,
    ) -> Result<Vec<(String, u64)>, TransferError> {
        validate_prefix(database)?;
        let root = ObjectPath::parse(format!("{database}/participants/"))
            .map_err(|_| TransferError::InvalidPoint)?;
        let mut listed = self.store.list(Some(&root));
        let mut objects = Vec::new();
        let mut bytes = 0_u64;
        while let Some(item) = listed.next().await {
            let item = item.map_err(|_| TransferError::Unavailable)?;
            validate_retention_participant_key(database, item.location.as_ref())?;
            bytes = bytes.checked_add(item.size).ok_or(TransferError::InvalidPoint)?;
            if objects.len() >= max_entries || bytes > max_bytes {
                return Err(TransferError::InvalidPoint);
            }
            objects.push((item.location.to_string(), item.size));
        }
        objects.sort();
        Ok(objects)
    }

    /// Deletes one immutable, previously journaled retention target, idempotently.
    pub(crate) async fn delete_retention_object(
        &self,
        database: &str,
        key: &str,
    ) -> Result<(), TransferError> {
        if let Some(prefix) = key.strip_suffix("/envelope.tbv1") {
            validate_envelope_prefix(prefix)?;
            if !prefix.starts_with(&format!("{database}/envelopes/")) {
                return Err(TransferError::InvalidPoint);
            }
        } else {
            validate_retention_participant_key(database, key)?;
        }
        self.delete_if_present(&ObjectPath::parse(key).map_err(|_| TransferError::InvalidPoint)?)
            .await
    }

    /// Publishes one bounded opaque member through the authenticated provider.
    /// Export composition uses this seam for resumable artifact members; the
    /// caller retains manifest and completion-marker ordering.
    ///
    /// # Errors
    /// Returns [`TransferError`] when the key or artifact exceeds bounds, the
    /// key is invalid, or the provider rejects the upload.
    pub async fn put_member(&self, key: &str, bytes: Vec<u8>) -> Result<(), TransferError> {
        if key.is_empty() || key.len() > 1024 || bytes.len() as u64 > self.max_artifact_bytes {
            return Err(TransferError::InvalidPoint);
        }
        let key = ObjectPath::parse(key).map_err(|_| TransferError::InvalidPoint)?;
        self.store
            .put_opts(&key, bytes.into(), PutMode::Create.into())
            .await
            .map_err(|_| TransferError::Unavailable)?;
        Ok(())
    }

    /// Reads one bounded opaque member through the authenticated provider.
    ///
    /// # Errors
    /// Returns [`TransferError`] when the key or downloaded artifact exceeds
    /// bounds, the key is invalid, or the provider rejects the download.
    pub async fn get_member(&self, key: &str) -> Result<Vec<u8>, TransferError> {
        if key.is_empty() || key.len() > 1024 {
            return Err(TransferError::InvalidPoint);
        }
        let key = ObjectPath::parse(key).map_err(|_| TransferError::InvalidPoint)?;
        let response = self.store.get(&key).await.map_err(|_| TransferError::Unavailable)?;
        let mut stream = response.into_stream();
        let mut bytes = Vec::new();
        while let Some(chunk) = stream.next().await {
            let chunk = chunk.map_err(|_| TransferError::Unavailable)?;
            if bytes.len() as u64 + chunk.len() as u64 > self.max_artifact_bytes {
                return Err(TransferError::InvalidPoint);
            }
            bytes.extend_from_slice(&chunk);
        }
        Ok(bytes)
    }

    /// Lists opaque export members under a bounded prefix.
    ///
    /// # Errors
    /// Returns [`TransferError`] when the prefix is invalid, the result exceeds
    /// the file bound, or the provider rejects the listing.
    pub async fn list_members(&self, prefix: &str) -> Result<Vec<String>, TransferError> {
        if prefix.is_empty() || prefix.len() > 1024 {
            return Err(TransferError::InvalidPoint);
        }
        let prefix = format!("{prefix}/");
        let prefix = ObjectPath::parse(&prefix).map_err(|_| TransferError::InvalidPoint)?;
        let mut stream = self.store.list(Some(&prefix));
        let mut keys = Vec::new();
        while let Some(item) = stream.next().await {
            let item = item.map_err(|_| TransferError::Unavailable)?;
            keys.push(item.location.to_string());
            if keys.len() > MAX_FILES {
                return Err(TransferError::InvalidPoint);
            }
        }
        Ok(keys)
    }

    /// Builds a SigV4-authenticated HTTPS client with bounded retries and timeouts.
    ///
    /// # Errors
    /// Rejects unverified residency, wrong OCI Region, and non-provider endpoints.
    pub fn new(config: DestinationConfig) -> Result<Self, TransferError> {
        if config.provider == Provider::CustomerS3 {
            return Err(TransferError::Configuration);
        }
        validate_config(&config)?;
        let store = AmazonS3Builder::new()
            .with_bucket_name(&config.bucket)
            .with_region(&config.signing_region)
            .with_endpoint(&config.endpoint)
            .with_access_key_id(&config.credentials.access_key)
            .with_secret_access_key(&config.credentials.secret_key)
            .with_virtual_hosted_style_request(false)
            .with_copy_if_not_exists(S3CopyIfNotExists::Multipart)
            .with_client_options(
                ClientOptions::new().with_allow_http(false).with_timeout(Duration::from_mins(1)),
            )
            .with_retry(RetryConfig {
                max_retries: 2,
                retry_timeout: Duration::from_mins(2),
                ..RetryConfig::default()
            })
            .build()
            .map_err(|_| TransferError::Configuration)?;
        // Keep credential rotations distinct in the historical catalog. The
        // fingerprint is opaque and cannot recover either credential, while
        // allowing fallback to a destination that still has its old key.
        let mut identity_input = blake3::Hasher::new();
        for value in [
            format!("{:?}", config.provider),
            config.endpoint.clone(),
            config.bucket.clone(),
            config.signing_region.clone(),
            config.admitted_region.to_string(),
        ] {
            identity_input.update(value.as_bytes());
            identity_input.update(&[0]);
        }
        identity_input.update(config.credentials.access_key.as_bytes());
        identity_input.update(&[0]);
        identity_input.update(config.credentials.secret_key.as_bytes());
        let identity = identity_input.finalize().to_hex().to_string();
        // Discard and zeroize the admission copy once the client owns its credentials.
        drop(config.credentials);
        Ok(Self {
            identity,
            admitted_region: config.admitted_region,
            provider: config.provider,
            store: Arc::new(store),
            max_artifact_bytes: config.max_artifact_bytes,
            max_point_bytes: config.max_point_bytes,
        })
    }

    /// Builds a production S3 client backed exclusively by workload identity.
    ///
    /// Credentials are resolved lazily for every request. No environment
    /// provider, static secret, or expired credential can be selected.
    ///
    /// # Errors
    /// Returns [`TransferError::Configuration`] when admission or client construction fails.
    pub fn new_with_workload_identity(
        config: &DestinationConfig,
        provider: Arc<dyn WorkloadIdentityProvider>,
    ) -> Result<Self, TransferError> {
        validate_config_for_workload_identity(config)?;
        let store = AmazonS3Builder::new()
            .with_bucket_name(&config.bucket)
            .with_region(&config.signing_region)
            .with_endpoint(&config.endpoint)
            .with_credentials(Arc::new(S3WorkloadIdentityProvider(provider)))
            .with_virtual_hosted_style_request(false)
            .with_copy_if_not_exists(S3CopyIfNotExists::Multipart)
            .with_client_options(
                ClientOptions::new().with_allow_http(false).with_timeout(Duration::from_mins(1)),
            )
            .with_retry(RetryConfig {
                max_retries: 2,
                retry_timeout: Duration::from_mins(2),
                ..RetryConfig::default()
            })
            .build()
            .map_err(|_| TransferError::Configuration)?;
        let mut identity_input = blake3::Hasher::new();
        for value in [
            format!("{:?}", config.provider),
            config.endpoint.clone(),
            config.bucket.clone(),
            config.signing_region.clone(),
            config.admitted_region.to_string(),
        ] {
            identity_input.update(value.as_bytes());
            identity_input.update(&[0]);
        }
        Ok(Self {
            identity: identity_input.finalize().to_hex().to_string(),
            admitted_region: config.admitted_region,
            provider: config.provider,
            store: Arc::new(store),
            max_artifact_bytes: config.max_artifact_bytes,
            max_point_bytes: config.max_point_bytes,
        })
    }

    /// Constructs an S3 destination against a local HTTP emulator (for
    /// example `MinIO` or `LocalStack`). This entry point is available only to
    /// tests and the explicit `test-support` feature; production admission
    /// continues to require HTTPS.
    ///
    /// # Errors
    /// Returns [`TransferError::Configuration`] when test configuration or client construction
    /// fails.
    #[cfg(any(test, feature = "test-support"))]
    pub fn new_for_test(config: DestinationConfig) -> Result<Self, TransferError> {
        validate_config_for_test(&config)?;
        let store = AmazonS3Builder::new()
            .with_bucket_name(&config.bucket)
            .with_region(&config.signing_region)
            .with_endpoint(&config.endpoint)
            .with_access_key_id(&config.credentials.access_key)
            .with_secret_access_key(&config.credentials.secret_key)
            .with_virtual_hosted_style_request(false)
            .with_allow_http(true)
            .with_copy_if_not_exists(S3CopyIfNotExists::Multipart)
            .build()
            .map_err(|_| TransferError::Configuration)?;
        let identity = blake3::hash(config.endpoint.as_bytes()).to_hex().to_string();
        drop(config.credentials);
        Ok(Self {
            identity,
            admitted_region: config.admitted_region,
            provider: config.provider,
            store: Arc::new(store),
            max_artifact_bytes: config.max_artifact_bytes,
            max_point_bytes: config.max_point_bytes,
        })
    }

    /// Constructs an injected object-store fixture for cross-crate regression tests.
    ///
    /// This bypasses provider admission and must never qualify a cloud provider
    /// or be used by production composition. Available only to tests or with
    /// the explicit `test-support` feature.
    #[cfg(any(test, feature = "test-support"))]
    #[must_use]
    pub fn for_test(
        provider: Provider,
        store: Arc<dyn ObjectStore>,
        max_artifact_bytes: u64,
        max_point_bytes: u64,
    ) -> Self {
        Self {
            identity: format!("test-{provider:?}"),
            admitted_region: crate::regions::RegionCode::GRU,
            provider,
            store,
            max_artifact_bytes,
            max_point_bytes,
        }
    }

    /// Stable opaque endpoint/bucket/Region identity, excluding credentials.
    #[must_use]
    pub fn identity(&self) -> &str {
        &self.identity
    }

    /// Returns the canonical database Region bound at destination admission.
    #[must_use]
    pub fn admitted_region(&self) -> crate::regions::RegionCode {
        self.admitted_region
    }

    /// Returns the independently configured provider.
    #[must_use]
    pub fn provider(&self) -> Provider {
        self.provider
    }

    /// Discovers sealed recovery point prefixes from this provider alone.
    ///
    /// # Errors
    /// Rejects invalid prefixes, listing failures, and a result exceeding `max`.
    pub async fn list_points(
        &self,
        prefix: &str,
        max: usize,
    ) -> Result<Vec<String>, TransferError> {
        validate_prefix(prefix)?;
        if max == 0 || max > 1_000_000 {
            return Err(TransferError::Configuration);
        }
        let path =
            ObjectPath::parse(format!("{prefix}/")).map_err(|_| TransferError::InvalidPoint)?;
        let mut stream = self.store.list(Some(&path));
        let mut points = Vec::new();
        let mut scanned = 0_usize;
        while let Some(result) = stream.next().await {
            let object = result.map_err(|_| TransferError::Unavailable)?;
            scanned += 1;
            if scanned > max.saturating_mul(MAX_FILES + 1) {
                return Err(TransferError::InvalidPoint);
            }
            let name = object.location.as_ref();
            if let Some(point) = name.strip_suffix(&format!("/{INDEX}"))
                && point.strip_prefix(prefix).is_some_and(|tail| tail.starts_with('/'))
            {
                validate_prefix(point)?;
                if points.len() == max {
                    return Err(TransferError::InvalidPoint);
                }
                points.push(point.to_owned());
            }
        }
        points.sort();
        Ok(points)
    }

    /// Discovers all direct recovery-point prefixes, including points whose
    /// publication index is missing after an interrupted upload or deletion.
    ///
    /// The returned prefixes are direct children of `prefix`; nested keys are
    /// rejected so callers cannot accidentally truncate a point prefix and
    /// delete a sibling or an unrelated descendant.
    ///
    /// # Errors
    /// Returns [`TransferError`] when the prefix, listing, or discovered object is invalid,
    /// unavailable, or exceeds the configured bound.
    pub async fn list_point_prefixes(
        &self,
        prefix: &str,
        max: usize,
    ) -> Result<Vec<String>, TransferError> {
        validate_prefix(prefix)?;
        if max == 0 || max > 1_000_000 {
            return Err(TransferError::Configuration);
        }
        let path =
            ObjectPath::parse(format!("{prefix}/")).map_err(|_| TransferError::InvalidPoint)?;
        let exact_prefix = format!("{prefix}/");
        let mut stream = self.store.list(Some(&path));
        let mut points = std::collections::BTreeSet::new();
        let mut scanned = 0_usize;
        while let Some(result) = stream.next().await {
            let object = result.map_err(|_| TransferError::Unavailable)?;
            scanned += 1;
            if scanned > max.saturating_mul(MAX_FILES + 1) {
                return Err(TransferError::InvalidPoint);
            }
            let Some(name) = object.location.as_ref().strip_prefix(&exact_prefix) else {
                return Err(TransferError::InvalidPoint);
            };
            let Some((point, member)) = name.split_once('/') else {
                return Err(TransferError::InvalidPoint);
            };
            if member.is_empty() || member.contains('/') {
                return Err(TransferError::InvalidPoint);
            }
            validate_name(point)?;
            let identity = point.strip_prefix("rp-").ok_or(TransferError::InvalidPoint)?;
            super::manifest::validate_identity(identity)
                .map_err(|_| TransferError::InvalidPoint)?;
            points.insert(format!("{prefix}/{point}"));
            if points.len() > max {
                return Err(TransferError::InvalidPoint);
            }
        }
        Ok(points.into_iter().collect())
    }

    /// Uploads every encrypted point artifact and publishes its index last.
    ///
    /// Prefixes must be immutable, unique per recovery-point identity. Retries
    /// of the same sealed point are safe. Success includes read-back verification
    /// of every object, so a successful PUT alone never advances readiness.
    ///
    /// # Errors
    /// Rejects unsealed points, symlinks, oversized artifacts, and partial uploads.
    pub async fn upload_point(&self, point: &Path, prefix: &str) -> Result<(), TransferError> {
        validate_point_prefix(point, prefix)?;
        let entries = self.scan_point(point)?;
        let index = render_index(&entries);
        self.upload_artifacts(point, prefix, &entries).await?;
        let key = object_key(prefix, INDEX)?;
        let entry = Entry {
            name: INDEX.into(),
            length: index.len() as u64,
            digest: blake3::hash(index.as_bytes()),
        };
        let index_bytes = index.into_bytes();
        match self.store.put_opts(&key, index_bytes.clone().into(), PutMode::Create.into()).await {
            Ok(_) => {}
            Err(object_store::Error::AlreadyExists { .. }) => {
                self.verify_remote(&key, &entry).await?;
            }
            Err(_) => return Err(TransferError::Unavailable),
        }
        self.verify_remote(&key, &entry).await
    }

    async fn upload_artifacts(
        &self,
        point: &Path,
        prefix: &str,
        entries: &[Entry],
    ) -> Result<(), TransferError> {
        for entry in entries
            .iter()
            .filter(|entry| entry.name != super::manifest::ENCRYPTED_MANIFEST_FILE)
            .chain(
                entries
                    .iter()
                    .filter(|entry| entry.name == super::manifest::ENCRYPTED_MANIFEST_FILE),
            )
        {
            let key = object_key(prefix, &entry.name)?;
            if entry.length == 0 {
                self.upload_empty_artifact(point, &key, entry).await?;
                continue;
            }
            // Multipart uploads are completed under a unique key and moved
            // into place with an atomic create-if-absent operation. A direct
            // multipart upload to the final key could be completed by a
            // loser after another writer had already published it.
            if self.remote_matches(&key, entry).await? {
                continue;
            }
            let temporary = ObjectPath::parse(format!(
                ".multipart/{prefix}/{}-{}",
                entry.name,
                crate::identity::generate()
            ))
            .map_err(|_| TransferError::InvalidPoint)?;
            let mut file =
                open_regular(&point.join(&entry.name)).map_err(|_| TransferError::LocalIo)?;
            let upload = self
                .store
                .put_multipart(&temporary)
                .await
                .map_err(|_| TransferError::Unavailable)?;
            let mut upload = MultipartCleanup::new(upload);
            let mut remaining = entry.length;
            let mut hasher = blake3::Hasher::new();
            let streamed = async {
                while remaining > 0 {
                    let size = remaining.min(8 * 1024 * 1024) as usize;
                    let mut chunk = vec![0; size];
                    file.read_exact(&mut chunk).map_err(|_| TransferError::LocalIo)?;
                    hasher.update(&chunk);
                    upload.put_part(chunk.into()).await?;
                    remaining -= size as u64;
                }
                if hasher.finalize() != entry.digest {
                    return Err(TransferError::Integrity);
                }
                upload.complete().await
            }
            .await;
            if streamed.is_err() {
                upload.abort().await;
            } else {
                upload.disarm();
            }
            streamed?;
            let publication = self.store.rename_if_not_exists(&temporary, &key).await;
            match publication {
                Ok(()) => {}
                Err(object_store::Error::AlreadyExists { .. }) => {
                    // Another writer won the publication race. Verify its
                    // immutable bytes before accepting the retry.
                    let verified = self.verify_remote(&key, entry).await;
                    let _ = self.store.delete(&temporary).await;
                    verified?;
                }
                Err(_) => {
                    let _ = self.store.delete(&temporary).await;
                    return Err(TransferError::Unavailable);
                }
            }
            self.verify_remote(&key, entry).await?;
        }
        Ok(())
    }

    async fn upload_empty_artifact(
        &self,
        point: &Path,
        key: &ObjectPath,
        entry: &Entry,
    ) -> Result<(), TransferError> {
        // Reopen the source at both sides of publication so a mutation after
        // the scan cannot publish a stale empty claim.
        validate_empty_artifact(&point.join(&entry.name), entry)?;
        match self.store.put_opts(key, Vec::<u8>::new().into(), PutMode::Create.into()).await {
            Ok(_) => {}
            Err(object_store::Error::AlreadyExists { .. }) => {
                self.verify_remote(key, entry).await?;
            }
            Err(_) => return Err(TransferError::Unavailable),
        }
        validate_empty_artifact(&point.join(&entry.name), entry)?;
        self.verify_remote(key, entry).await
    }

    /// Verifies an existing remote point against the local sealed artifacts.
    /// Upload acknowledgements are not evidence that a provider still holds data.
    /// This streams each remote artifact without adding local staging bytes.
    ///
    /// # Errors
    /// Refuses missing, corrupted, oversized, or malformed point artifacts.
    pub async fn verify_point(&self, point: &Path, prefix: &str) -> Result<(), TransferError> {
        validate_point_prefix(point, prefix)?;
        let entries = self.scan_point(point)?;
        for entry in &entries {
            self.verify_remote(&object_key(prefix, &entry.name)?, entry).await?;
        }
        let index = render_index(&entries);
        self.verify_remote(
            &object_key(prefix, INDEX)?,
            &Entry {
                name: INDEX.into(),
                length: index.len() as u64,
                digest: blake3::hash(index.as_bytes()),
            },
        )
        .await
    }

    /// Verifies remote artifacts against an authenticated retained manifest.
    /// Streams ciphertext members without recreating local archive history.
    ///
    /// # Errors
    /// Refuses missing, altered, oversized or unpublished artifacts.
    pub async fn verify_manifest(
        &self,
        prefix: &str,
        manifest_path: &Path,
        manifest: &super::BackupManifest,
    ) -> Result<(), TransferError> {
        validate_manifest_prefix(prefix, manifest)?;
        let index = self.read_bounded(&object_key(prefix, INDEX)?, INDEX_LIMIT).await?;
        let entries = parse_index(&index, self.max_artifact_bytes, self.max_point_bytes)?;
        if entries.len() != manifest.members().len() + 1 {
            return Err(TransferError::Integrity);
        }
        let expected = fs::read(manifest_path).map_err(|_| TransferError::LocalIo)?;
        let actual = self
            .read_bounded(
                &object_key(prefix, super::manifest::ENCRYPTED_MANIFEST_FILE)?,
                self.max_artifact_bytes,
            )
            .await?;
        let manifest_entry = entries
            .iter()
            .find(|entry| entry.name == super::manifest::ENCRYPTED_MANIFEST_FILE)
            .ok_or(TransferError::Integrity)?;
        if expected != actual
            || manifest_entry.length != actual.len() as u64
            || manifest_entry.digest != blake3::hash(&actual)
        {
            return Err(TransferError::Integrity);
        }
        for member in manifest.members() {
            let entry = entries
                .iter()
                .find(|entry| entry.name == member.name())
                .ok_or(TransferError::Integrity)?;
            if entry.length != member.length() {
                return Err(TransferError::Integrity);
            }
            let response = self
                .store
                .get(&object_key(prefix, member.name())?)
                .await
                .map_err(|_| TransferError::Unavailable)?;
            let mut chunks = response.into_stream();
            let mut digest = aws_lc_rs::digest::Context::new(&aws_lc_rs::digest::SHA256);
            let mut transport_digest = blake3::Hasher::new();
            let mut length = 0_u64;
            while let Some(chunk) = chunks.next().await {
                let chunk = chunk.map_err(|_| TransferError::Unavailable)?;
                length = length.checked_add(chunk.len() as u64).ok_or(TransferError::Integrity)?;
                if length > member.length() {
                    return Err(TransferError::Integrity);
                }
                digest.update(&chunk);
                transport_digest.update(&chunk);
            }
            if length != member.length()
                || hex::encode(digest.finish().as_ref()) != member.digest()
                || transport_digest.finalize() != entry.digest
            {
                return Err(TransferError::Integrity);
            }
        }
        Ok(())
    }

    /// Removes completed multipart staging objects left by an interrupted
    /// upload. Active multipart sessions are provider-side state and do not
    /// appear in this listing, so they are left untouched. Every candidate is
    /// validated before any deletion; malformed or unexpectedly nested keys
    /// fail closed.
    ///
    /// # Errors
    /// Returns [`TransferError`] when the prefix is invalid, provider listing fails, or a
    /// candidate cannot be safely removed.
    pub async fn cleanup_multipart(
        &self,
        prefix: &str,
        max: usize,
        minimum_age: Duration,
    ) -> Result<usize, TransferError> {
        validate_prefix(prefix)?;
        if max == 0 || max > MAX_FILES {
            return Err(TransferError::Configuration);
        }
        let list_prefix = ObjectPath::parse(format!(".multipart/{prefix}/"))
            .map_err(|_| TransferError::InvalidPoint)?;
        let expected_prefix = format!(".multipart/{prefix}/");
        let mut stream = self.store.list(Some(&list_prefix));
        let mut candidates = Vec::new();
        while let Some(result) = stream.next().await {
            let object = result.map_err(|_| TransferError::Unavailable)?;
            if candidates.len() >= max {
                return Err(TransferError::InvalidPoint);
            }
            let name = object
                .location
                .as_ref()
                .strip_prefix(&expected_prefix)
                .ok_or(TransferError::InvalidPoint)?;
            let (artifact, token) = name.rsplit_once('-').ok_or(TransferError::InvalidPoint)?;
            validate_name(artifact)?;
            if crate::identity::from_text(token).is_err() {
                return Err(TransferError::InvalidPoint);
            }
            let modified: SystemTime = object.last_modified.into();
            if SystemTime::now().duration_since(modified).unwrap_or_default() >= minimum_age {
                candidates.push(object.location);
            }
        }
        for key in &candidates {
            self.store.delete(key).await.map_err(|_| TransferError::Unavailable)?;
        }
        Ok(candidates.len())
    }

    /// Deletes one retired recovery point after the caller validates retention.
    ///
    /// Invalidates its publication index and encrypted manifest before deleting
    /// members. Retries after interruption inspect only this exact point prefix;
    /// they never delete a sibling point or recurse into nested keys.
    ///
    /// # Errors
    /// Rejects malformed indexes, nested keys, or exceeded metadata bounds before
    /// mutation. Provider failures leave remaining objects available for retry.
    pub async fn delete_point(&self, prefix: &str) -> Result<(), TransferError> {
        validate_prefix(prefix)?;
        let index_key = object_key(prefix, INDEX)?;
        let entries = match self.store.get(&index_key).await {
            Ok(response) => {
                let mut stream = response.into_stream();
                let mut bytes = Vec::new();
                while let Some(chunk) = stream.next().await {
                    let chunk = chunk.map_err(|_| TransferError::Unavailable)?;
                    if (bytes.len() as u64).saturating_add(chunk.len() as u64) > INDEX_LIMIT {
                        return Err(TransferError::InvalidPoint);
                    }
                    bytes.extend_from_slice(&chunk);
                }
                Some(parse_index(&bytes, self.max_artifact_bytes, self.max_point_bytes)?)
            }
            Err(object_store::Error::NotFound { .. }) => None,
            Err(_) => return Err(TransferError::Unavailable),
        };
        // Listing the exact prefix also collects orphan artifacts from an
        // interrupted upload or deletion whose publication index is absent.
        let path = ObjectPath::parse(prefix).map_err(|_| TransferError::InvalidPoint)?;
        let exact_prefix = format!("{prefix}/");
        let mut stream = self.store.list(Some(&path));
        let mut keys = std::collections::BTreeSet::new();
        let mut bytes = 0_u64;
        while let Some(result) = stream.next().await {
            let object = result.map_err(|_| TransferError::Unavailable)?;
            let Some(name) = object.location.as_ref().strip_prefix(&exact_prefix) else {
                return Err(TransferError::InvalidPoint);
            };
            if name != INDEX {
                validate_name(name)?;
            }
            if keys.len() > MAX_FILES || object.size > self.max_artifact_bytes.max(INDEX_LIMIT) {
                return Err(TransferError::InvalidPoint);
            }
            bytes = bytes.checked_add(object.size).ok_or(TransferError::InvalidPoint)?;
            if bytes > self.max_point_bytes.saturating_add(INDEX_LIMIT) {
                return Err(TransferError::InvalidPoint);
            }
            keys.insert(object.location);
        }
        if let Some(entries) = entries {
            for entry in entries {
                keys.insert(object_key(prefix, &entry.name)?);
            }
        }
        self.delete_if_present(&index_key).await?;
        let manifest = object_key(prefix, super::manifest::ENCRYPTED_MANIFEST_FILE)?;
        self.delete_if_present(&manifest).await?;
        keys.remove(&index_key);
        keys.remove(&manifest);
        for key in keys {
            self.delete_if_present(&key).await?;
        }
        Ok(())
    }

    async fn delete_if_present(&self, key: &ObjectPath) -> Result<(), TransferError> {
        match self.store.delete(key).await {
            Ok(()) | Err(object_store::Error::NotFound { .. }) => Ok(()),
            Err(_) => Err(TransferError::Unavailable),
        }
    }

    /// Downloads a point into a new directory, verifying every artifact first.
    ///
    /// An incomplete download is removed. The final directory appears only after
    /// complete verification and fsync; existing targets are rejected.
    ///
    /// # Errors
    /// Returns unavailable for missing indexes/artifacts, integrity for corruption,
    /// and invalid point for malformed indexes or exhausted configured limits.
    pub async fn download_point(&self, prefix: &str, target: &Path) -> Result<(), TransferError> {
        self.download_point_with_byte_limit(prefix, target, self.max_point_bytes).await
    }

    /// Downloads a point while enforcing the caller's remaining staging budget.
    ///
    /// # Errors
    /// Rejects a point exceeding the remaining byte limit before creating files;
    /// otherwise returns the same validation and transfer errors as `download_point`.
    pub async fn download_point_with_byte_limit(
        &self,
        prefix: &str,
        target: &Path,
        remaining_bytes: u64,
    ) -> Result<(), TransferError> {
        validate_prefix(prefix)?;
        if target.try_exists().map_err(|_| TransferError::LocalIo)? {
            return Err(TransferError::InvalidPoint);
        }
        let bytes = self.read_bounded(&object_key(prefix, INDEX)?, INDEX_LIMIT).await?;
        let entries = parse_index(
            &bytes,
            self.max_artifact_bytes,
            self.max_point_bytes.min(remaining_bytes),
        )?;
        let parent = target.parent().ok_or(TransferError::InvalidPoint)?;
        let stage = super::StagingDir::create(parent).map_err(|_| TransferError::LocalIo)?;
        let result = async {
            for entry in &entries {
                let mut file = OpenOptions::new()
                    .write(true)
                    .create_new(true)
                    .open(stage.path().join(&entry.name))
                    .map_err(|_| TransferError::LocalIo)?;
                let response = self
                    .store
                    .get(&object_key(prefix, &entry.name)?)
                    .await
                    .map_err(|_| TransferError::Unavailable)?;
                let mut stream = response.into_stream();
                let mut digest = blake3::Hasher::new();
                let mut length = 0_u64;
                while let Some(chunk) = stream.next().await {
                    let chunk = chunk.map_err(|_| TransferError::Unavailable)?;
                    length =
                        length.checked_add(chunk.len() as u64).ok_or(TransferError::Integrity)?;
                    if length > entry.length {
                        return Err(TransferError::Integrity);
                    }
                    digest.update(&chunk);
                    file.write_all(&chunk).map_err(|_| TransferError::LocalIo)?;
                }
                if length != entry.length || digest.finalize() != entry.digest {
                    return Err(TransferError::Integrity);
                }
                file.sync_all().map_err(|_| TransferError::LocalIo)?;
            }
            File::open(stage.path())
                .and_then(|file| file.sync_all())
                .map_err(|_| TransferError::LocalIo)?;
            // renameat2 NOREPLACE prevents a concurrent caller's target being overwritten.
            rustix::fs::renameat_with(
                rustix::fs::CWD,
                stage.path(),
                rustix::fs::CWD,
                target,
                rustix::fs::RenameFlags::NOREPLACE,
            )
            .map_err(|_| TransferError::LocalIo)?;
            File::open(parent)
                .and_then(|file| file.sync_all())
                .map_err(|_| TransferError::LocalIo)?;
            Ok(())
        }
        .await;
        stage.discard();
        result
    }

    fn scan_point(&self, point: &Path) -> Result<Vec<Entry>, TransferError> {
        if !fs::symlink_metadata(point)
            .map_err(|_| TransferError::InvalidPoint)?
            .file_type()
            .is_dir()
        {
            return Err(TransferError::InvalidPoint);
        }
        let mut entries = Vec::new();
        let mut total = 0_u64;
        for file in fs::read_dir(point).map_err(|_| TransferError::LocalIo)? {
            let file = file.map_err(|_| TransferError::LocalIo)?;
            let name = file.file_name().into_string().map_err(|_| TransferError::InvalidPoint)?;
            validate_name(&name)?;
            let metadata = fs::symlink_metadata(file.path()).map_err(|_| TransferError::LocalIo)?;
            if !metadata.file_type().is_file()
                || metadata.len() > self.max_artifact_bytes
                || entries.len() == MAX_FILES
            {
                return Err(TransferError::InvalidPoint);
            }
            total = total.checked_add(metadata.len()).ok_or(TransferError::InvalidPoint)?;
            if total > self.max_point_bytes {
                return Err(TransferError::InvalidPoint);
            }
            let mut digest = blake3::Hasher::new();
            let mut source = open_regular(&file.path()).map_err(|_| TransferError::LocalIo)?;
            let mut buffer = vec![0_u8; 65536].into_boxed_slice();
            let mut read_length = 0_u64;
            loop {
                let size = source.read(&mut buffer).map_err(|_| TransferError::LocalIo)?;
                if size == 0 {
                    break;
                }
                read_length =
                    read_length.checked_add(size as u64).ok_or(TransferError::InvalidPoint)?;
                if read_length > metadata.len() {
                    return Err(TransferError::Integrity);
                }
                digest.update(&buffer[..size]);
            }
            if read_length != metadata.len() {
                return Err(TransferError::Integrity);
            }
            entries.push(Entry { name, length: metadata.len(), digest: digest.finalize() });
        }
        entries.sort_by(|a, b| a.name.cmp(&b.name));
        validate_entries(&entries)?;
        Ok(entries)
    }

    async fn read_bounded(&self, key: &ObjectPath, bound: u64) -> Result<Vec<u8>, TransferError> {
        let mut stream =
            self.store.get(key).await.map_err(|_| TransferError::Unavailable)?.into_stream();
        let mut bytes = Vec::new();
        while let Some(chunk) = stream.next().await {
            let chunk = chunk.map_err(|_| TransferError::Unavailable)?;
            if (bytes.len() as u64).saturating_add(chunk.len() as u64) > bound {
                return Err(TransferError::Integrity);
            }
            bytes.extend_from_slice(&chunk);
        }
        Ok(bytes)
    }

    async fn verify_remote(&self, key: &ObjectPath, expected: &Entry) -> Result<(), TransferError> {
        let mut stream =
            self.store.get(key).await.map_err(|_| TransferError::Unavailable)?.into_stream();
        let mut digest = blake3::Hasher::new();
        let mut length = 0_u64;
        while let Some(chunk) = stream.next().await {
            let chunk = chunk.map_err(|_| TransferError::Unavailable)?;
            length = length.checked_add(chunk.len() as u64).ok_or(TransferError::Integrity)?;
            if length > expected.length {
                return Err(TransferError::Integrity);
            }
            digest.update(&chunk);
        }
        if length != expected.length || digest.finalize() != expected.digest {
            return Err(TransferError::Integrity);
        }
        Ok(())
    }

    async fn remote_matches(
        &self,
        key: &ObjectPath,
        expected: &Entry,
    ) -> Result<bool, TransferError> {
        match self.store.get(key).await {
            Ok(response) => {
                let mut stream = response.into_stream();
                let mut digest = blake3::Hasher::new();
                let mut length = 0_u64;
                while let Some(chunk) = stream.next().await {
                    let chunk = chunk.map_err(|_| TransferError::Unavailable)?;
                    length =
                        length.checked_add(chunk.len() as u64).ok_or(TransferError::Integrity)?;
                    if length > expected.length {
                        return Err(TransferError::Integrity);
                    }
                    digest.update(&chunk);
                }
                if length != expected.length || digest.finalize() != expected.digest {
                    return Err(TransferError::Integrity);
                }
                Ok(true)
            }
            Err(object_store::Error::NotFound { .. }) => Ok(false),
            Err(_) => Err(TransferError::Unavailable),
        }
    }
}

fn open_regular(path: &Path) -> std::io::Result<File> {
    let descriptor = rustix::fs::open(
        path,
        rustix::fs::OFlags::RDONLY | rustix::fs::OFlags::NOFOLLOW | rustix::fs::OFlags::CLOEXEC,
        rustix::fs::Mode::empty(),
    )?;
    let file = File::from(descriptor);
    if !file.metadata()?.is_file() {
        return Err(std::io::Error::other("not a regular artifact"));
    }
    Ok(file)
}

fn validate_empty_artifact(path: &Path, expected: &Entry) -> Result<(), TransferError> {
    let file = open_regular(path).map_err(|_| TransferError::LocalIo)?;
    if expected.length != 0 || file.metadata().map_err(|_| TransferError::LocalIo)?.len() != 0 {
        return Err(TransferError::Integrity);
    }
    Ok(())
}

fn validate_config(config: &DestinationConfig) -> Result<(), TransferError> {
    let host = config.endpoint.strip_prefix("https://").ok_or(TransferError::Configuration)?;
    let endpoint_valid = match config.provider {
        Provider::Oci => {
            config.signing_region == config.database_region
                && host.ends_with(&format!(
                    ".compat.objectstorage.{}.oraclecloud.com",
                    config.database_region
                ))
        }
        Provider::Tigris => host == "fly.storage.tigris.dev",
        Provider::CustomerS3 => false,
    };
    if !crate::regions::RegionCatalog::production().is_registered(&config.admitted_region)
        || !endpoint_valid
        || host.bytes().any(|b| !(b.is_ascii_alphanumeric() || b == b'.' || b == b'-'))
        || config.signing_region.is_empty()
        || config.database_region.is_empty()
        || config.bucket.is_empty()
        || config.bucket.bytes().any(|b| !(b.is_ascii_alphanumeric() || b == b'.' || b == b'-'))
        || config.credentials.access_key.is_empty()
        || config.credentials.secret_key.is_empty()
        || config.verified_geography.is_empty()
        || config.placement_revision.is_empty()
        || !config.permitted_geographies.contains(&config.verified_geography)
        || config.max_artifact_bytes == 0
        || config.max_artifact_bytes > 256 * 1024 * 1024
        || config.max_point_bytes > 4 * 1024 * 1024 * 1024
        || config.max_point_bytes < config.max_artifact_bytes
    {
        return Err(TransferError::Configuration);
    }
    Ok(())
}

fn validate_config_for_workload_identity(config: &DestinationConfig) -> Result<(), TransferError> {
    let host = config.endpoint.strip_prefix("https://").ok_or(TransferError::Configuration)?;
    let endpoint_valid = match config.provider {
        Provider::Oci => {
            config.signing_region == config.database_region
                && host.ends_with(&format!(
                    ".compat.objectstorage.{}.oraclecloud.com",
                    config.database_region
                ))
        }
        Provider::Tigris => host == "fly.storage.tigris.dev",
        Provider::CustomerS3 => true,
    };
    if !crate::regions::RegionCatalog::production().is_registered(&config.admitted_region)
        || !endpoint_valid
        || host.bytes().any(|b| !(b.is_ascii_alphanumeric() || b == b'.' || b == b'-'))
        || config.signing_region.is_empty()
        || config.database_region.is_empty()
        || config.bucket.is_empty()
        || config.bucket.bytes().any(|b| !(b.is_ascii_alphanumeric() || b == b'.' || b == b'-'))
        || config.verified_geography.is_empty()
        || config.placement_revision.is_empty()
        || !config.permitted_geographies.contains(&config.verified_geography)
        || config.max_artifact_bytes == 0
        || config.max_artifact_bytes > 256 * 1024 * 1024
        || config.max_point_bytes > 4 * 1024 * 1024 * 1024
        || config.max_point_bytes < config.max_artifact_bytes
    {
        return Err(TransferError::Configuration);
    }
    Ok(())
}

#[cfg(any(test, feature = "test-support"))]
fn validate_config_for_test(config: &DestinationConfig) -> Result<(), TransferError> {
    if config.endpoint.starts_with("http://") {
        if config.endpoint.contains('@') || config.endpoint.chars().any(char::is_control) {
            return Err(TransferError::Configuration);
        }
        return Ok(());
    }
    validate_config(config)
}
fn validate_retention_participant_key(database: &str, key: &str) -> Result<(), TransferError> {
    let suffix = key
        .strip_prefix(&format!("{database}/participants/"))
        .ok_or(TransferError::InvalidPoint)?;
    let parts = suffix.split('/').collect::<Vec<_>>();
    if parts.len() != 5
        || ![(parts[0], 32), (parts[1], 16), (parts[2], 32)].iter().all(|(part, length)| {
            part.len() == *length && part.bytes().all(|byte| byte.is_ascii_hexdigit())
        })
    {
        return Err(TransferError::InvalidPoint);
    }
    let identity = parts[3].strip_prefix("rp-").ok_or(TransferError::InvalidPoint)?;
    super::manifest::validate_identity(identity).map_err(|_| TransferError::InvalidPoint)?;
    let incarnation =
        u128::from_str_radix(parts[0], 16).map_err(|_| TransferError::InvalidPoint)?;
    if crate::identity::DatabaseUlid::from_wire_bytes(incarnation.to_be_bytes()).to_text()
        != database
    {
        return Err(TransferError::InvalidPoint);
    }
    if parts[4] == INDEX { Ok(()) } else { validate_name(parts[4]) }
}

fn validate_name(name: &str) -> Result<(), TransferError> {
    if name.is_empty()
        || name.len() > 128
        || name == "."
        || name == ".."
        || name == INDEX
        || !name.bytes().all(|b| b.is_ascii_alphanumeric() || matches!(b, b'-' | b'_' | b'.'))
    {
        return Err(TransferError::InvalidPoint);
    }
    Ok(())
}
fn validate_prefix(prefix: &str) -> Result<(), TransferError> {
    if prefix.len() > 1024 || prefix.split('/').any(|part| validate_name(part).is_err()) {
        return Err(TransferError::InvalidPoint);
    }
    Ok(())
}
fn validate_envelope_name(name: &str) -> Result<(), TransferError> {
    if name.len() != 68
        || !name.starts_with("env-")
        || !name[4..].bytes().all(|b| b.is_ascii_hexdigit())
    {
        return Err(TransferError::InvalidPoint);
    }
    Ok(())
}
fn validate_envelope_prefix(prefix: &str) -> Result<(), TransferError> {
    let (database, name) = prefix.rsplit_once("/envelopes/").ok_or(TransferError::InvalidPoint)?;
    validate_prefix(database)?;
    validate_envelope_name(name)
}
fn validate_point_prefix(point: &Path, prefix: &str) -> Result<(), TransferError> {
    validate_prefix(prefix)?;
    let Some(name) = point.file_name().and_then(|name| name.to_str()) else {
        return Ok(());
    };
    // A canonical recovery-point prefix is an identity boundary.  Do not
    // allow a caller to place an arbitrary staging directory under it: the
    // directory basename must carry the same recovery identity.  Opaque
    // transport fixtures retain their existing prefix-only behavior.
    let prefix_name = prefix.rsplit('/').next().ok_or(TransferError::InvalidPoint)?;
    if let Some(prefix_identity) = prefix_name.strip_prefix("rp-")
        && super::manifest::validate_identity(prefix_identity).is_ok()
        && name != prefix_name
    {
        return Err(TransferError::InvalidPoint);
    }
    let Some(identity) = name.strip_prefix("rp-") else {
        return Ok(());
    };
    // Older transport fixtures use opaque point names (for example `rp-1`)
    // without a manifest identity. Enforce the binding whenever the basename
    // is a canonical recovery-point identity; legacy opaque names retain the
    // existing transport-only validation path.
    if super::manifest::validate_identity(identity).is_err() {
        return Ok(());
    }
    if prefix.rsplit('/').next() != Some(name) {
        return Err(TransferError::InvalidPoint);
    }
    Ok(())
}
fn validate_manifest_prefix(
    prefix: &str,
    manifest: &super::BackupManifest,
) -> Result<(), TransferError> {
    validate_prefix(prefix)?;
    if prefix.rsplit('/').next()
        != Some(super::manifest::point_directory(manifest.identity()).as_str())
    {
        return Err(TransferError::InvalidPoint);
    }
    Ok(())
}
fn object_key(prefix: &str, name: &str) -> Result<ObjectPath, TransferError> {
    ObjectPath::parse(format!("{prefix}/{name}")).map_err(|_| TransferError::InvalidPoint)
}
fn validate_entries(entries: &[Entry]) -> Result<(), TransferError> {
    if entries.is_empty()
        || !entries.iter().any(|e| e.name == super::manifest::ENCRYPTED_MANIFEST_FILE)
        || entries.iter().any(|e| e.name == super::manifest::MANIFEST_FILE)
        || entries.windows(2).any(|pair| pair[0].name >= pair[1].name)
    {
        return Err(TransferError::InvalidPoint);
    }
    Ok(())
}
fn render_index(entries: &[Entry]) -> String {
    let mut text = String::from("terrabase-transfer-v1\n");
    for entry in entries {
        use std::fmt::Write as _;
        let _ = writeln!(text, "{} {} {}", entry.name, entry.length, entry.digest);
    }
    text
}
fn parse_index(
    bytes: &[u8],
    artifact_bound: u64,
    total_bound: u64,
) -> Result<Vec<Entry>, TransferError> {
    let text = std::str::from_utf8(bytes).map_err(|_| TransferError::InvalidPoint)?;
    let mut lines = text.lines();
    if lines.next() != Some("terrabase-transfer-v1") {
        return Err(TransferError::InvalidPoint);
    }
    let mut entries = Vec::new();
    let mut total = 0_u64;
    for line in lines {
        let fields: Vec<_> = line.split(' ').collect();
        if fields.len() != 3 || entries.len() == MAX_FILES {
            return Err(TransferError::InvalidPoint);
        }
        validate_name(fields[0])?;
        let length: u64 = fields[1].parse().map_err(|_| TransferError::InvalidPoint)?;
        total = total.checked_add(length).ok_or(TransferError::InvalidPoint)?;
        if length > artifact_bound || total > total_bound {
            return Err(TransferError::InvalidPoint);
        }
        entries.push(Entry {
            name: fields[0].into(),
            length,
            digest: fields[2].parse().map_err(|_| TransferError::InvalidPoint)?,
        });
    }
    validate_entries(&entries)?;
    Ok(entries)
}

#[cfg(test)]
mod tests {
    use super::*;
    use object_store::memory::InMemory;
    use std::sync::atomic::{AtomicUsize, Ordering};

    #[derive(Debug)]
    struct FakeWorkloadIdentity {
        calls: AtomicUsize,
        revoked: std::sync::atomic::AtomicBool,
    }

    #[async_trait::async_trait]
    impl WorkloadIdentityProvider for FakeWorkloadIdentity {
        async fn credentials(&self) -> Result<Credentials, WorkloadIdentityError> {
            self.calls.fetch_add(1, Ordering::Relaxed);
            if self.revoked.load(Ordering::Relaxed) {
                return Err(WorkloadIdentityError::Unavailable);
            }
            Ok(Credentials::temporary(
                "short-lived-access".into(),
                "short-lived-secret".into(),
                "short-lived-token".into(),
                SystemTime::now() + Duration::from_secs(60),
            ))
        }
    }

    fn destination(store: Arc<InMemory>) -> ObjectDestination {
        ObjectDestination::for_test(Provider::Oci, store, 1024, 4096)
    }

    #[tokio::test]
    async fn workload_identity_refreshes_and_fails_closed_after_revocation() {
        let provider = Arc::new(FakeWorkloadIdentity {
            calls: AtomicUsize::new(0),
            revoked: std::sync::atomic::AtomicBool::new(false),
        });
        let adapter = S3WorkloadIdentityProvider(provider.clone());
        let first = object_store::CredentialProvider::get_credential(&adapter)
            .await
            .expect("initial short-lived credential");
        assert_eq!(first.key_id, "short-lived-access");
        provider.revoked.store(true, Ordering::Relaxed);
        let revoked = object_store::CredentialProvider::get_credential(&adapter).await;
        assert!(revoked.is_err(), "revocation must be observed on the next request");
        assert_eq!(provider.calls.load(Ordering::Relaxed), 2);
        assert!(!format!("{revoked:?}").contains("short-lived-secret"));
    }

    #[tokio::test]
    async fn workload_identity_rejects_expired_credentials() {
        #[derive(Debug)]
        struct Expired;
        #[async_trait::async_trait]
        impl WorkloadIdentityProvider for Expired {
            async fn credentials(&self) -> Result<Credentials, WorkloadIdentityError> {
                Ok(Credentials::temporary(
                    "access".into(),
                    "secret".into(),
                    "token".into(),
                    SystemTime::now() - Duration::from_secs(1),
                ))
            }
        }
        let result = object_store::CredentialProvider::get_credential(&S3WorkloadIdentityProvider(
            Arc::new(Expired),
        ))
        .await;
        assert!(result.is_err());
    }

    /// SQLite WAL transport/recovery qualification (not the native backup format). Set
    /// `TERRABASE_MINIO_ENDPOINT` to the emulator endpoint to run it.
    #[test]
    #[ignore = "requires a running S3 emulator"]
    fn minio_round_trip_100k_wal_records() {
        let endpoint = std::env::var("TERRABASE_MINIO_ENDPOINT")
            .expect("set TERRABASE_MINIO_ENDPOINT=http://127.0.0.1:19000");
        let prefix = format!(
            "qualification/100k-{}-{}",
            std::process::id(),
            std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_nanos()
        );
        let root = std::env::temp_dir().join(format!("tb-minio-{}", std::process::id()));
        let _ = fs::remove_dir_all(&root);
        fs::create_dir_all(&root).unwrap();
        let point = root.with_extension("point");
        let _ = fs::remove_dir_all(&point);
        fs::create_dir_all(&point).unwrap();
        let db_path = root.join("source.sqlite3");
        let connection = rusqlite::Connection::open(&db_path).unwrap();
        connection.pragma_update(None, "journal_mode", "WAL").unwrap();
        connection
            .execute_batch("CREATE TABLE items (id INTEGER PRIMARY KEY, value TEXT NOT NULL);")
            .unwrap();
        connection.pragma_update(None, "wal_autocheckpoint", 0).unwrap();
        connection.execute_batch("PRAGMA wal_checkpoint(TRUNCATE);").unwrap();
        fs::copy(&db_path, point.join("snapshot.tbv3e")).unwrap();
        let write_started = std::time::Instant::now();
        let transaction = connection.unchecked_transaction().unwrap();
        for i in 0..100_000_i64 {
            transaction
                .execute(
                    "INSERT INTO items VALUES (?1, ?2)",
                    rusqlite::params![i, format!("value-{i}-{}", "abcdef0123456789".repeat(16))],
                )
                .unwrap();
        }
        transaction.commit().unwrap();
        let write_elapsed = write_started.elapsed();
        let payload = fs::read(root.join("source.sqlite3-wal")).unwrap();
        assert!(payload.len() > 16 * 1024 * 1024, "exercise multiple multipart parts");
        fs::write(point.join("wal.tbv3e"), &payload).unwrap();
        fs::write(
            point.join("manifest.tbv3e"),
            b"transport fixture, not an encrypted native manifest",
        )
        .unwrap();
        drop(connection);
        let config = DestinationConfig {
            admitted_region: crate::regions::RegionCode::GRU,
            provider: Provider::Oci,
            endpoint,
            signing_region: "us-east-1".into(),
            database_region: "GRU".into(),
            bucket: "terrabase".into(),
            verified_geography: "GRU".into(),
            permitted_geographies: vec!["GRU".into()],
            placement_revision: "test".into(),
            credentials: Credentials::new("minioadmin".into(), "minioadmin".into()),
            max_artifact_bytes: 32 * 1024 * 1024,
            max_point_bytes: 64 * 1024 * 1024,
        };
        tokio::runtime::Runtime::new().unwrap().block_on(async {
            let destination = ObjectDestination::new_for_test(config).unwrap();
            let upload_started = std::time::Instant::now();
            destination.upload_point(&point, &prefix).await.unwrap();
            let upload_elapsed = upload_started.elapsed();
            fs::remove_dir_all(&root).unwrap();
            fs::remove_dir_all(&point).unwrap();
            assert!(!db_path.exists());
            let restore_started = std::time::Instant::now();
            destination.download_point(&prefix, &root).await.unwrap();
            let restore_elapsed = restore_started.elapsed();
            let restored = fs::read(root.join("wal.tbv3e")).unwrap();
            assert_eq!(restored, payload);
            fs::rename(root.join("snapshot.tbv3e"), &db_path).unwrap();
            fs::rename(root.join("wal.tbv3e"), root.join("source.sqlite3-wal")).unwrap();
            let recovered = rusqlite::Connection::open(&db_path).unwrap();
            let integrity: String =
                recovered.query_row("PRAGMA integrity_check", [], |row| row.get(0)).unwrap();
            assert_eq!(integrity, "ok");
            verify_minio_rows(&recovered);
            eprintln!("SQLite recovery verified 100000 exact rows; integrity_check=ok");
            verify_minio_corruption(&destination, &prefix, &payload, &root).await;

            eprintln!(
                "sqlite 100k WAL: {} bytes, writes {:?}, upload {:?}, restore {:?}",
                payload.len(),
                write_elapsed,
                upload_elapsed,
                restore_elapsed
            );
        });
        let _ = fs::remove_dir_all(root);
    }

    async fn verify_minio_corruption(
        destination: &ObjectDestination,
        prefix: &str,
        payload: &[u8],
        root: &Path,
    ) {
        let remote_wal = object_key(prefix, "wal.tbv3e").unwrap();
        let rejected = root.with_extension("rejected");
        let mut corrupt = payload.to_vec();
        let middle = corrupt.len() / 2;
        corrupt[middle] ^= 0x80;
        destination.store.put(&remote_wal, corrupt.into()).await.unwrap();
        assert_eq!(
            destination.download_point(prefix, &rejected).await,
            Err(TransferError::Integrity)
        );
        assert!(!rejected.exists());
        destination
            .store
            .put(&remote_wal, payload[..payload.len() / 2].to_vec().into())
            .await
            .unwrap();
        assert_eq!(
            destination.download_point(prefix, &rejected).await,
            Err(TransferError::Integrity)
        );
        assert!(!rejected.exists());
        destination.store.delete(&remote_wal).await.unwrap();
        assert_eq!(
            destination.download_point(prefix, &rejected).await,
            Err(TransferError::Unavailable)
        );
        assert!(!rejected.exists());
        destination.delete_point(prefix).await.unwrap();
        eprintln!(
            "S3 corruption, truncation, and missing WAL rejected; no partial restore published"
        );
    }

    fn verify_minio_rows(connection: &rusqlite::Connection) {
        let mut statement = connection.prepare("SELECT id, value FROM items ORDER BY id").unwrap();
        let mut rows = statement.query([]).unwrap();
        for expected in 0..100_000_i64 {
            let row = rows.next().unwrap().expect("all committed rows must recover");
            assert_eq!(row.get::<_, i64>(0).unwrap(), expected);
            assert_eq!(
                row.get::<_, String>(1).unwrap(),
                format!("value-{expected}-{}", "abcdef0123456789".repeat(16))
            );
        }
        assert!(rows.next().unwrap().is_none(), "no duplicate or unexpected rows");
    }

    #[tokio::test]
    async fn member_create_is_atomic_and_never_overwrites() {
        let destination = destination(Arc::new(InMemory::new()));
        destination.put_member("exports/job/member", b"one".to_vec()).await.unwrap();
        assert!(destination.put_member("exports/job/member", b"one".to_vec()).await.is_err());
        assert!(destination.put_member("exports/job/member", b"two".to_vec()).await.is_err());
        assert_eq!(destination.get_member("exports/job/member").await.unwrap(), b"one");
    }

    #[tokio::test]
    async fn point_upload_binds_rp_directory_and_keeps_remote_bytes_immutable() {
        let store = Arc::new(InMemory::new());
        let destination = destination(store.clone());
        let root = std::env::temp_dir().join(format!("tb-object-bind-{}", std::process::id()));
        let _ = fs::remove_dir_all(&root);
        let identity = "a".repeat(64);
        let point = root.join(format!("rp-{identity}"));
        fs::create_dir_all(&point).unwrap();
        fs::write(point.join(super::super::manifest::ENCRYPTED_MANIFEST_FILE), b"manifest")
            .unwrap();
        fs::write(point.join("member.tbv3e"), b"original").unwrap();

        assert_eq!(
            destination
                .upload_point(
                    &point,
                    "db/rp-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
                )
                .await,
            Err(TransferError::InvalidPoint)
        );
        let arbitrary = root.join("staging");
        fs::create_dir_all(&arbitrary).unwrap();
        assert_eq!(
            destination.upload_point(&arbitrary, &format!("db/rp-{identity}")).await,
            Err(TransferError::InvalidPoint),
            "canonical prefixes must be bound to a canonical point directory"
        );
        destination.upload_point(&point, &format!("db/rp-{identity}")).await.unwrap();
        fs::write(point.join("member.tbv3e"), b"replacement").unwrap();
        assert_eq!(
            destination.upload_point(&point, &format!("db/rp-{identity}")).await,
            Err(TransferError::Integrity)
        );
        assert_eq!(
            destination.get_member(&format!("db/rp-{identity}/member.tbv3e")).await.unwrap(),
            b"original"
        );
        let _ = fs::remove_dir_all(root);
    }

    #[tokio::test]
    async fn concurrent_multipart_publication_keeps_one_immutable_object() {
        let store = Arc::new(InMemory::new());
        let first = destination(store.clone());
        let second = destination(store.clone());
        let root = std::env::temp_dir().join(format!("tb-object-race-{}", std::process::id()));
        let _ = fs::remove_dir_all(&root);
        fs::create_dir_all(&root).unwrap();
        let identity = "1".repeat(64);
        let point = root.join(format!("rp-{identity}"));
        fs::create_dir_all(&point).unwrap();
        fs::write(point.join(super::super::manifest::ENCRYPTED_MANIFEST_FILE), b"manifest")
            .unwrap();
        fs::write(point.join("member.tbv3e"), b"multipart payload").unwrap();
        let object_path = format!("db/rp-{identity}");

        let (left, right) = tokio::join!(
            first.upload_point(&point, &object_path),
            second.upload_point(&point, &object_path),
        );
        assert!(left.is_ok(), "first publisher failed: {left:?}");
        assert!(right.is_ok(), "second publisher failed: {right:?}");
        assert_eq!(
            first.get_member(&format!("{object_path}/member.tbv3e")).await.unwrap(),
            b"multipart payload"
        );

        let mut objects = store.list(None);
        while let Some(object) = objects.next().await {
            assert!(!object.unwrap().location.as_ref().starts_with(".multipart/"));
        }
        fs::remove_dir_all(root).unwrap();
    }

    #[tokio::test]
    async fn multipart_cleanup_is_bounded_and_fails_closed_on_malformed_keys() {
        let store = Arc::new(InMemory::new());
        let destination = destination(store.clone());
        let valid = ObjectPath::parse(".multipart/db/rp-1/member.tbv3e-01ARZ3NDEKTSV4RRFFQ69G5FAV")
            .unwrap();
        let malformed = ObjectPath::parse(".multipart/db/rp-1/member.tbv3e-nested/part").unwrap();
        store.put(&valid, b"stale".to_vec().into()).await.unwrap();
        store.put(&malformed, b"keep".to_vec().into()).await.unwrap();

        assert_eq!(
            destination.cleanup_multipart("db/rp-1", 10, Duration::ZERO).await,
            Err(TransferError::InvalidPoint)
        );
        assert!(store.get(&valid).await.is_ok(), "validation must precede deletion");
        store.delete(&malformed).await.unwrap();
        assert_eq!(destination.cleanup_multipart("db/rp-1", 10, Duration::ZERO).await, Ok(1));
        assert!(store.get(&valid).await.is_err());
    }

    #[test]
    fn independent_restore_and_corruption_fail_closed() {
        let runtime = tokio::runtime::Runtime::new().unwrap();
        runtime.block_on(async {
            let root = std::env::temp_dir().join(format!("tb-object-{}", std::process::id()));
            let _ = fs::remove_dir_all(&root);
            fs::create_dir(&root).unwrap();
            let point = root.join("source");
            fs::create_dir(&point).unwrap();
            fs::write(point.join("manifest.tbv3e"), b"encrypted manifest fixture").unwrap();
            fs::write(point.join("member.tbv3e"), b"encrypted member fixture").unwrap();
            let first_store = Arc::new(InMemory::new());
            let second_store = Arc::new(InMemory::new());
            let first = destination(first_store.clone());
            let second = destination(second_store.clone());
            first.upload_point(&point, "db/rp-1").await.unwrap();
            second.upload_point(&point, "db/rp-1").await.unwrap();
            assert_eq!(second.list_points("db", 10).await.unwrap(), vec!["db/rp-1"]);
            first_store.delete(&object_key("db/rp-1", INDEX).unwrap()).await.unwrap();
            assert_eq!(
                first.download_point("db/rp-1", &root.join("absent")).await,
                Err(TransferError::Unavailable)
            );
            assert_eq!(
                second
                    .download_point_with_byte_limit("db/rp-1", &root.join("over-budget"), 1)
                    .await,
                Err(TransferError::InvalidPoint)
            );
            assert!(!root.join("over-budget").exists());
            second.download_point("db/rp-1", &root.join("restored")).await.unwrap();
            assert_eq!(
                fs::read(root.join("restored/member.tbv3e")).unwrap(),
                b"encrypted member fixture"
            );
            second_store
                .put(&object_key("db/rp-1", "member.tbv3e").unwrap(), b"corrupt".to_vec().into())
                .await
                .unwrap();
            assert_eq!(
                second.download_point("db/rp-1", &root.join("corrupt")).await,
                Err(TransferError::Integrity)
            );
            assert!(!root.join("corrupt").exists());
            assert_eq!(second.upload_point(&point, "db/rp-1").await, Err(TransferError::Integrity));
            second.delete_point("db/rp-1").await.unwrap();
            second.upload_point(&point, "db/rp-1").await.unwrap();
            second.download_point("db/rp-1", &root.join("retry")).await.unwrap();
            first.upload_point(&point, "db/rp-1").await.unwrap();
            first.delete_point("db/rp-1").await.unwrap();
            assert!(first.list_points("db", 10).await.unwrap().is_empty());
            assert_eq!(second.list_points("db", 10).await.unwrap(), vec!["db/rp-1"]);
            second_store.delete(&object_key("db/rp-1", "member.tbv3e").unwrap()).await.unwrap();
            assert_eq!(
                second.download_point("db/rp-1", &root.join("partial")).await,
                Err(TransferError::Unavailable)
            );
            assert!(!root.join("partial").exists());
            fs::remove_dir_all(root).unwrap();
        });
    }

    #[test]
    fn deletion_retry_cleans_orphans_without_touching_other_points() {
        tokio::runtime::Runtime::new().unwrap().block_on(async {
            let store = Arc::new(InMemory::new());
            let destination = destination(store.clone());
            let orphan = object_key("db/rp-1", "member.tbv3e").unwrap();
            let sibling = object_key("db/rp-10", "member.tbv3e").unwrap();
            store.put(&orphan, b"orphan".to_vec().into()).await.unwrap();
            store.put(&sibling, b"retained".to_vec().into()).await.unwrap();
            destination.delete_point("db/rp-1").await.unwrap();
            destination.delete_point("db/rp-1").await.unwrap();
            assert!(store.get(&orphan).await.is_err());
            assert!(store.get(&sibling).await.is_ok());
            let nested = ObjectPath::parse("db/rp-2/nested/member.tbv3e").unwrap();
            store.put(&nested, b"nested".to_vec().into()).await.unwrap();
            assert_eq!(destination.delete_point("db/rp-2").await, Err(TransferError::InvalidPoint));
            assert!(store.get(&nested).await.is_ok());
            let malformed = object_key("db/rp-3", INDEX).unwrap();
            store.put(&malformed, b"bad index".to_vec().into()).await.unwrap();
            assert_eq!(destination.delete_point("db/rp-3").await, Err(TransferError::InvalidPoint));
            assert!(store.get(&malformed).await.is_ok());
        });
    }

    #[test]
    fn point_prefix_discovery_includes_orphans_and_rejects_nested_keys() {
        tokio::runtime::Runtime::new().unwrap().block_on(async {
            let store = Arc::new(InMemory::new());
            let destination = destination(store.clone());
            let orphan = object_key(&format!("db/rp-{}", "1a".repeat(32)), "member.tbv3e").unwrap();
            let sibling =
                object_key(&format!("db/rp-{}", "2b".repeat(32)), "member.tbv3e").unwrap();
            store.put(&orphan, b"orphan".to_vec().into()).await.unwrap();
            store.put(&sibling, b"sibling".to_vec().into()).await.unwrap();
            assert_eq!(
                destination.list_point_prefixes("db", 10).await.unwrap(),
                vec![format!("db/rp-{}", "1a".repeat(32)), format!("db/rp-{}", "2b".repeat(32))]
            );

            let nested = ObjectPath::parse("db/rp-2/nested/member.tbv3e").unwrap();
            store.put(&nested, b"nested".to_vec().into()).await.unwrap();
            assert_eq!(
                destination.list_point_prefixes("db", 10).await,
                Err(TransferError::InvalidPoint)
            );
            assert!(store.get(&nested).await.is_ok());
        });
    }

    #[test]
    fn s3_requests_are_sigv4_signed() {
        use std::net::TcpListener;
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let endpoint = format!("http://{}", listener.local_addr().unwrap());
        let server = std::thread::spawn(move || {
            let (mut socket, _) = listener.accept().unwrap();
            socket.set_read_timeout(Some(Duration::from_secs(10))).unwrap();
            let mut request = Vec::new();
            let mut byte = [0_u8];
            while !request.ends_with(b"\r\n\r\n") {
                socket.read_exact(&mut byte).unwrap();
                request.push(byte[0]);
                assert!(request.len() < 16384);
            }
            let headers = String::from_utf8(request).unwrap().to_ascii_lowercase();
            assert!(headers.starts_with("get /backup/db/rp-1/member.tbv3e "));
            assert!(headers.contains("authorization: aws4-hmac-sha256 credential=fixture-access/"));
            assert!(headers.contains("/sa-saopaulo-1/s3/aws4_request"));
            assert!(headers.contains("x-amz-date:"));
            assert!(!headers.contains("fixture-secret"));
            socket.write_all(b"HTTP/1.1 200 OK\r\nContent-Length: 7\r\nLast-Modified: Sat, 05 Sep 2026 00:00:00 GMT\r\nETag: \"fixture\"\r\nConnection: close\r\n\r\ncipher!").unwrap();
        });
        tokio::runtime::Runtime::new().unwrap().block_on(async {
            let store = AmazonS3Builder::new()
                .with_endpoint(endpoint)
                .with_bucket_name("backup")
                .with_region("sa-saopaulo-1")
                .with_access_key_id("fixture-access")
                .with_secret_access_key("fixture-secret")
                .with_allow_http(true)
                .with_retry(RetryConfig { max_retries: 0, ..RetryConfig::default() })
                .build()
                .unwrap();
            let destination = ObjectDestination {
                identity: "test-http".into(),
                admitted_region: crate::regions::RegionCode::GRU,
                provider: Provider::Oci,
                store: Arc::new(store),
                max_artifact_bytes: 1024,
                max_point_bytes: 4096,
            };
            let expected =
                Entry { name: "member.tbv3e".into(), length: 7, digest: blake3::hash(b"cipher!") };
            destination
                .verify_remote(&object_key("db/rp-1", &expected.name).unwrap(), &expected)
                .await
                .unwrap();
        });
        server.join().unwrap();
    }

    #[test]
    fn index_rejects_traversal_plaintext_and_overflow() {
        let hash = blake3::hash(b"x");
        for text in [
            format!("terrabase-transfer-v1\n../x 1 {hash}\n"),
            format!("terrabase-transfer-v1\nmanifest.tbv1m 1 {hash}\n"),
            format!("terrabase-transfer-v1\nmanifest.tbv3e 100000 {hash}\n"),
            format!("terrabase-transfer-v1\nmanifest.tbv3e 1 {hash}\nmanifest.tbv3e 1 {hash}\n"),
        ] {
            assert!(parse_index(text.as_bytes(), 1024, 4096).is_err());
        }
    }

    #[test]
    fn admission_requires_verified_residency_and_redacts_keys() {
        let mut config = DestinationConfig {
            admitted_region: crate::regions::RegionCode::GRU,
            provider: Provider::Oci,
            endpoint: "https://namespace.compat.objectstorage.sa-saopaulo-1.oraclecloud.com".into(),
            signing_region: "sa-saopaulo-1".into(),
            database_region: "sa-saopaulo-1".into(),
            bucket: "backups".into(),
            verified_geography: "BR".into(),
            permitted_geographies: vec!["BR".into()],
            placement_revision: "verified-bucket-revision-1".into(),
            credentials: Credentials::new("secret-access".into(), "secret-key".into()),
            max_artifact_bytes: 1024,
            max_point_bytes: 4096,
        };
        assert!(validate_config(&config).is_ok());
        assert!(!format!("{config:?}").contains("secret-access"));
        assert!(!format!("{config:?}").contains("secret-key"));
        config.signing_region = "us-ashburn-1".into();
        assert_eq!(validate_config(&config), Err(TransferError::Configuration));
        config.provider = Provider::Tigris;
        config.endpoint = "https://fly.storage.tigris.dev".into();
        assert!(validate_config(&config).is_ok());
        config.verified_geography = "US".into();
        assert_eq!(validate_config(&config), Err(TransferError::Configuration));
        config.verified_geography = "BR".into();
        config.endpoint = "http://fly.storage.tigris.dev".into();
        assert_eq!(validate_config(&config), Err(TransferError::Configuration));
        config.endpoint = "https://fly.storage.tigris.dev".into();
        config.provider = Provider::Tigris;
        config.max_point_bytes = 4 * 1024 * 1024 * 1024 + 1;
        assert_eq!(
            validate_config_for_workload_identity(&config),
            Err(TransferError::Configuration)
        );
    }

    #[test]
    fn destination_identity_retains_credential_rotations() {
        let config = || DestinationConfig {
            admitted_region: crate::regions::RegionCode::GRU,
            provider: Provider::Oci,
            endpoint: "https://namespace.compat.objectstorage.sa-saopaulo-1.oraclecloud.com".into(),
            signing_region: "sa-saopaulo-1".into(),
            database_region: "sa-saopaulo-1".into(),
            bucket: "backups".into(),
            verified_geography: "BR".into(),
            permitted_geographies: vec!["BR".into()],
            placement_revision: "verified-bucket-revision-1".into(),
            credentials: Credentials::new("access-a".into(), "secret-a".into()),
            max_artifact_bytes: 1024,
            max_point_bytes: 4096,
        };
        let first = ObjectDestination::new(config()).unwrap();
        let mut rotated = config();
        rotated.credentials = Credentials::new("access-b".into(), "secret-b".into());
        let second = ObjectDestination::new(rotated).unwrap();

        assert_ne!(first.identity(), second.identity());
    }
}
