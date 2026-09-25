use std::fmt;
use std::time::Duration;

use bytes::Bytes;
use sha2::{Digest, Sha256};
use tb_core::{DocumentId, WorkReservation};

use crate::ObjectError;

const OBJECT_KEY_PREFIX: &str = "tb-objects/v1/";

/// SHA-256 identity of the exact encoded bytes stored in an object.
#[derive(Clone, Copy, Eq, Hash, PartialEq)]
pub struct ContentDigest([u8; 32]);

impl ContentDigest {
    /// Construct a digest from its fixed-width binary representation.
    #[must_use]
    pub const fn from_bytes(bytes: [u8; 32]) -> Self {
        Self(bytes)
    }

    /// Hash one byte slice with SHA-256.
    #[must_use]
    pub fn hash(bytes: &[u8]) -> Self {
        let digest = Sha256::digest(bytes);
        let mut output = [0; 32];
        output.copy_from_slice(&digest);
        Self(output)
    }

    /// Return the fixed-width digest bytes.
    #[must_use]
    pub const fn as_bytes(&self) -> &[u8; 32] {
        &self.0
    }

    /// Render a lowercase hexadecimal digest for diagnostics and manifests.
    #[must_use]
    pub fn to_hex(self) -> String {
        let mut encoded = String::with_capacity(64);
        for byte in self.0 {
            use fmt::Write as _;
            let _ = write!(encoded, "{byte:02x}");
        }
        encoded
    }
}

impl fmt::Debug for ContentDigest {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(formatter, "ContentDigest({})", self.to_hex())
    }
}

impl fmt::Display for ContentDigest {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(&self.to_hex())
    }
}

/// Unique provider key for one immutable upload.
///
/// The key is generated for each upload and is never reused by this crate. A
/// successful conditional marker PUT reserves it before multipart transfer, so
/// concurrent writers cannot replace one another at the same final path.
#[derive(Clone, Debug, Eq, Hash, PartialEq)]
pub struct ObjectKey(DocumentId);

impl ObjectKey {
    pub(crate) fn fresh() -> Self {
        Self(DocumentId::new())
    }

    /// Parse the canonical object key recorded in an already-validated manifest.
    ///
    /// This checks the key namespace and identifier encoding. The caller remains
    /// responsible for authenticating the manifest that supplied the key.
    ///
    /// # Errors
    ///
    /// Returns [`ObjectError::InvalidObjectKey`] if the namespace or identifier
    /// encoding is not canonical.
    pub fn from_manifest(value: &str) -> Result<Self, ObjectError> {
        let id = value
            .strip_prefix(OBJECT_KEY_PREFIX)
            .ok_or_else(|| ObjectError::InvalidObjectKey(value.to_owned()))?;
        let id = DocumentId::parse_canonical(id)
            .map_err(|_| ObjectError::InvalidObjectKey(value.to_owned()))?;
        Ok(Self(id))
    }

    /// Return the canonical provider path recorded in a manifest.
    #[must_use]
    pub fn as_str(&self) -> String {
        format!("{OBJECT_KEY_PREFIX}{}", self.0)
    }

    pub(crate) fn as_path(&self) -> object_store::path::Path {
        object_store::path::Path::from(self.as_str())
    }
}

impl fmt::Display for ObjectKey {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(formatter, "{OBJECT_KEY_PREFIX}{}", self.0)
    }
}

/// A fully uploaded object whose bytes were read back and verified.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct VerifiedObject {
    key: ObjectKey,
    length: u64,
    sha256: ContentDigest,
}

impl VerifiedObject {
    pub(crate) const fn new(key: ObjectKey, length: u64, sha256: ContentDigest) -> Self {
        Self {
            key,
            length,
            sha256,
        }
    }

    /// Unique immutable object key to store in the manifest reference.
    #[must_use]
    pub const fn key(&self) -> &ObjectKey {
        &self.key
    }

    /// Exact number of bytes verified after upload.
    #[must_use]
    pub const fn length(&self) -> u64 {
        self.length
    }

    /// SHA-256 of the exact bytes verified after upload.
    #[must_use]
    pub const fn sha256(&self) -> ContentDigest {
        self.sha256
    }

    /// Copy the identity fields into a manifest reference.
    #[must_use]
    pub fn as_reference(&self) -> ObjectReference {
        ObjectReference {
            key: self.key.clone(),
            length: self.length,
            sha256: self.sha256,
        }
    }
}

/// Object locator and content digest authenticated by the selected manifest.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ObjectReference {
    key: ObjectKey,
    length: u64,
    sha256: ContentDigest,
}

impl ObjectReference {
    /// Create a reference only after validating the manifest entry that contains it.
    ///
    /// # Errors
    ///
    /// Returns [`ObjectError::InvalidManifestReference`] if an empty object has
    /// a digest other than the SHA-256 of empty content.
    pub fn from_manifest(
        key: ObjectKey,
        length: u64,
        sha256: ContentDigest,
    ) -> Result<Self, ObjectError> {
        if length == 0 && sha256 != ContentDigest::hash(&[]) {
            return Err(ObjectError::InvalidManifestReference(
                "empty object has a nonempty-content digest",
            ));
        }
        Ok(Self {
            key,
            length,
            sha256,
        })
    }

    /// Immutable provider key.
    #[must_use]
    pub const fn key(&self) -> &ObjectKey {
        &self.key
    }

    /// Expected complete object length.
    #[must_use]
    pub const fn length(&self) -> u64 {
        self.length
    }

    /// Expected complete object SHA-256.
    #[must_use]
    pub const fn sha256(&self) -> ContentDigest {
        self.sha256
    }
}

/// A verified block that retains its memory-budget reservation while its bytes live.
pub struct VerifiedBlock {
    bytes: Bytes,
    reservation: WorkReservation,
}

impl VerifiedBlock {
    pub(crate) const fn new(bytes: Bytes, reservation: WorkReservation) -> Self {
        Self { bytes, reservation }
    }

    /// Borrow the verified block bytes without releasing their budget charge.
    #[must_use]
    pub fn as_bytes(&self) -> &Bytes {
        &self.bytes
    }

    /// Return the bytes and the reservation that must remain alive with them.
    pub fn into_parts(self) -> (Bytes, WorkReservation) {
        (self.bytes, self.reservation)
    }
}

impl fmt::Debug for VerifiedBlock {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter
            .debug_struct("VerifiedBlock")
            .field("length", &self.bytes.len())
            .finish_non_exhaustive()
    }
}

/// Tenant and worker attribution used for transfer memory reservations.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct TransferOwner {
    /// Tenant charged for transfer buffers.
    pub tenant: tb_core::TenantId,
    /// Worker charged for transfer buffers.
    pub worker: tb_core::WorkerId,
}

/// Hard byte, request, and time bounds for the object adapter.
#[derive(Clone, Debug)]
pub struct ObjectLimits {
    /// Maximum object size accepted by this adapter (default: 80 GiB).
    pub max_object_bytes: u64,
    /// Fixed upload part size; every non-final S3 part must be at least 5 MiB.
    pub upload_part_bytes: usize,
    /// Maximum bytes materialized by one verified range request.
    pub max_range_bytes: usize,
    /// Maximum number of admitted object requests and upload sessions.
    pub max_active_requests: usize,
    /// Maximum number of open multipart upload sessions.
    pub max_upload_sessions: usize,
    /// Deadline for each provider request and each input read.
    pub request_timeout: Duration,
}

impl ObjectLimits {
    /// Validate limits before creating request or buffer admission state.
    ///
    /// # Errors
    ///
    /// Returns [`ObjectError::InvalidLimits`] if any byte, request, upload, or
    /// timeout bound is invalid.
    pub fn validate(&self) -> Result<(), ObjectError> {
        const MIN_MULTIPART_PART_BYTES: usize = 5 * 1024 * 1024;
        const MAX_MULTIPART_PART_BYTES: u64 = 5 * 1024 * 1024 * 1024;
        const MAX_MULTIPART_PARTS: u64 = 10_000;
        if self.max_object_bytes == 0 {
            return Err(ObjectError::InvalidLimits(
                "maximum object size must be nonzero",
            ));
        }
        if self.upload_part_bytes < MIN_MULTIPART_PART_BYTES {
            return Err(ObjectError::InvalidLimits(
                "multipart part size must be at least 5 MiB",
            ));
        }
        let part_bytes = u64::try_from(self.upload_part_bytes)
            .map_err(|_| ObjectError::InvalidLimits("multipart part size does not fit u64"))?;
        if part_bytes > MAX_MULTIPART_PART_BYTES {
            return Err(ObjectError::InvalidLimits(
                "multipart part size must not exceed 5 GiB",
            ));
        }
        let parts = self
            .max_object_bytes
            .checked_add(part_bytes - 1)
            .ok_or(ObjectError::InvalidLimits("multipart part count overflow"))?
            / part_bytes;
        if parts > MAX_MULTIPART_PARTS {
            return Err(ObjectError::InvalidLimits(
                "maximum object size exceeds the 10,000 multipart part limit",
            ));
        }
        if self.max_range_bytes == 0 {
            return Err(ObjectError::InvalidLimits(
                "maximum range size must be nonzero",
            ));
        }
        if self.max_active_requests == 0 {
            return Err(ObjectError::InvalidLimits(
                "maximum active request count must be nonzero",
            ));
        }
        if self.max_active_requests > tokio::sync::Semaphore::MAX_PERMITS {
            return Err(ObjectError::InvalidLimits(
                "maximum active request count exceeds the semaphore limit",
            ));
        }
        if self.max_upload_sessions == 0 {
            return Err(ObjectError::InvalidLimits(
                "maximum upload session count must be nonzero",
            ));
        }
        if self.max_upload_sessions > tokio::sync::Semaphore::MAX_PERMITS {
            return Err(ObjectError::InvalidLimits(
                "maximum upload session count exceeds the semaphore limit",
            ));
        }
        if self.request_timeout.is_zero() {
            return Err(ObjectError::InvalidLimits(
                "request timeout must be nonzero",
            ));
        }
        Ok(())
    }
}

impl Default for ObjectLimits {
    fn default() -> Self {
        Self {
            max_object_bytes: 80 * 1024 * 1024 * 1024,
            upload_part_bytes: 16 * 1024 * 1024,
            max_range_bytes: 3 * 1024 * 1024,
            max_active_requests: 32,
            max_upload_sessions: 16,
            request_timeout: Duration::from_secs(30),
        }
    }
}

/// Results of live provider behavior probes. A false value means the client has
/// not observed the required semantics in a bounded probe.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
// These independent probe results are intentionally represented as booleans:
// providers may support any combination of the four behaviors.
#[allow(clippy::struct_excessive_bools)]
pub struct ProviderCapabilities {
    /// `PutMode::Create` has single-winner behavior under concurrent writes.
    pub conditional_create: bool,
    /// Range reads returned the exact requested interval and object size.
    pub range_reads: bool,
    /// A multipart request can be explicitly aborted before completion.
    pub multipart_abort: bool,
    /// `PutMode::Update` rejects a stale version during a concurrent race.
    pub conditional_manifest_update: bool,
}
