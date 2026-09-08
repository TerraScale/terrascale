//! Bounded S3 transfers of encrypted sealed recovery points.
//!
//! Each destination is independent. The transfer index is published last and
//! contains the digest of every ciphertext file; it never contains credentials.
//! Restore requires a known point prefix and never consults another provider.
use std::fmt;
use std::fs::{self, File, OpenOptions};
use std::io::{Read, Write};
use std::path::Path;
use std::sync::Arc;
use std::time::Duration;

use futures_util::StreamExt;
use object_store::aws::AmazonS3Builder;
use object_store::path::Path as ObjectPath;
use object_store::{ClientOptions, ObjectStore, PutMode, RetryConfig};

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
}

/// S3 credentials whose formatting never reveals key material.
pub struct Credentials {
    access_key: String,
    secret_key: String,
}
impl Credentials {
    /// Takes ownership of the provider's access and secret keys.
    #[must_use]
    pub fn new(access_key: String, secret_key: String) -> Self {
        Self { access_key, secret_key }
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

impl ObjectDestination {
    /// Publishes one authenticated multi-participant envelope. The envelope
    /// is written before its commit index and read back before success.
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
        self.store.put(&key, bytes.clone().into()).await.map_err(|_| TransferError::Unavailable)?;
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
        validate_config(&config)?;
        let store = AmazonS3Builder::new()
            .with_bucket_name(&config.bucket)
            .with_region(&config.signing_region)
            .with_endpoint(&config.endpoint)
            .with_access_key_id(&config.credentials.access_key)
            .with_secret_access_key(&config.credentials.secret_key)
            .with_virtual_hosted_style_request(false)
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

    /// Constructs an S3 destination against a local HTTP emulator (for
    /// example MinIO or LocalStack). This entry point is available only to
    /// tests and the explicit `test-support` feature; production admission
    /// continues to require HTTPS.
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
        validate_prefix(prefix)?;
        let entries = self.scan_point(point)?;
        let index = render_index(&entries);
        for entry in entries
            .iter()
            .filter(|entry| entry.name != super::manifest::ENCRYPTED_MANIFEST_FILE)
            .chain(
                entries
                    .iter()
                    .filter(|entry| entry.name == super::manifest::ENCRYPTED_MANIFEST_FILE),
            )
        {
            let mut bytes = Vec::new();
            open_regular(&point.join(&entry.name))
                .map_err(|_| TransferError::LocalIo)?
                .take(self.max_artifact_bytes + 1)
                .read_to_end(&mut bytes)
                .map_err(|_| TransferError::LocalIo)?;
            if bytes.len() as u64 != entry.length || blake3::hash(&bytes) != entry.digest {
                return Err(TransferError::Integrity);
            }
            let key = object_key(prefix, &entry.name)?;
            self.store.put(&key, bytes.into()).await.map_err(|_| TransferError::Unavailable)?;
            self.verify_remote(&key, entry).await?;
        }
        let key = object_key(prefix, INDEX)?;
        let entry = Entry {
            name: INDEX.into(),
            length: index.len() as u64,
            digest: blake3::hash(index.as_bytes()),
        };
        self.store
            .put(&key, index.into_bytes().into())
            .await
            .map_err(|_| TransferError::Unavailable)?;
        self.verify_remote(&key, &entry).await
    }

    /// Verifies an existing remote point against the local sealed artifacts.
    /// Upload acknowledgements are not evidence that a provider still holds data.
    /// This streams each remote artifact without adding local staging bytes.
    ///
    /// # Errors
    /// Refuses missing, corrupted, oversized, or malformed point artifacts.
    pub async fn verify_point(&self, point: &Path, prefix: &str) -> Result<(), TransferError> {
        validate_prefix(prefix)?;
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
        validate_prefix(prefix)?;
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

    fn destination(store: Arc<InMemory>) -> ObjectDestination {
        ObjectDestination::for_test(Provider::Oci, store, 1024, 4096)
    }

    #[tokio::test]
    async fn member_create_is_atomic_and_never_overwrites() {
        let destination = destination(Arc::new(InMemory::new()));
        destination.put_member("exports/job/member", b"one".to_vec()).await.unwrap();
        assert!(destination.put_member("exports/job/member", b"one".to_vec()).await.is_err());
        assert!(destination.put_member("exports/job/member", b"two".to_vec()).await.is_err());
        assert_eq!(destination.get_member("exports/job/member").await.unwrap(), b"one");
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
