//! Provider-neutral database-key metadata and lifecycle contracts.
//!
//! These types contain no OCI resource names or credentials. The
//! Orchestrator-owned adapter resolves a canonical [`RegionCode`] to provider
//! configuration outside `TerraBase`.

use std::fmt;
use std::time::{Duration, Instant};

use crate::identity::{DatabaseUlid, ProjectUlid, TenantUlid, Ulid, UlidError, process_generator};
use crate::regions::RegionCode;

use super::{DatabaseKey, EncryptionError, valid_identity};

/// Maximum authorization lease for an unwrapped database key.
pub const MAX_DATABASE_KEY_LEASE: Duration = Duration::from_mins(30);
/// Maximum opaque wrapped-key bytes accepted from a provider adapter.
pub const MAX_WRAPPED_KEY_BYTES: usize = 64 * 1024;

/// The ULID of one database-key version.
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct DatabaseKeyVersion(Ulid);

impl DatabaseKeyVersion {
    /// Mints a new version through `TerraBase`'s process-wide monotonic ULID
    /// generator.
    #[must_use]
    pub fn generate() -> Self {
        Self(process_generator().generate())
    }

    /// Parses the canonical 26-character uppercase ULID form.
    ///
    /// # Errors
    ///
    /// Returns the underlying ULID parse error for malformed text.
    pub fn from_text(text: &str) -> Result<Self, UlidError> {
        Ok(Self(Ulid::from_text(text)?))
    }

    /// Constructs a version from a validated 16-byte wire value.
    #[must_use]
    pub const fn from_wire_bytes(bytes: [u8; 16]) -> Self {
        Self(Ulid::from_bytes(bytes))
    }

    /// Returns the canonical 16-byte storage and wire value.
    #[must_use]
    pub const fn as_bytes(self) -> [u8; 16] {
        self.0.to_bytes()
    }

    /// Returns the canonical text value.
    #[must_use]
    pub fn to_text(self) -> String {
        self.0.to_text()
    }
}

impl fmt::Display for DatabaseKeyVersion {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        fmt::Display::fmt(&self.0, formatter)
    }
}

/// The isolated key hierarchy selected for a database-key wrapper.
#[derive(Clone, Copy, Debug, Eq, Hash, PartialEq)]
pub enum KeyPurpose {
    /// WAL, segments, indexes, manifests, snapshots, and value chunks.
    Data,
    /// Backup artifacts and restore staging.
    Backup,
    /// Checkpoint tokens and their durable state.
    Checkpoint,
}

impl KeyPurpose {
    /// Returns the stable domain-separation name.
    #[must_use]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Data => "data",
            Self::Backup => "backup",
            Self::Checkpoint => "checkpoint",
        }
    }
}

/// The artifact domain whose key is derived from a database key.
#[derive(Clone, Copy, Debug, Eq, Hash, PartialEq)]
pub enum ArtifactKind {
    /// A write-ahead log artifact.
    Wal,
    /// An immutable data segment.
    Segment,
    /// An immutable index artifact.
    Index,
    /// A storage or catalog manifest.
    Manifest,
    /// A database snapshot.
    Snapshot,
    /// A backup artifact.
    Backup,
    /// A large-value chunk.
    LargeValueChunk,
    /// A checkpoint token or checkpoint-state artifact.
    Checkpoint,
}

impl ArtifactKind {
    /// Returns the stable HKDF and AAD name.
    #[must_use]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Wal => "wal",
            Self::Segment => "segment",
            Self::Index => "index",
            Self::Manifest => "manifest",
            Self::Snapshot => "snapshot",
            Self::Backup => "backup",
            Self::LargeValueChunk => "large-value-chunk",
            Self::Checkpoint => "checkpoint",
        }
    }

    /// Returns the key hierarchy this artifact must use.
    #[must_use]
    pub const fn key_purpose(self) -> KeyPurpose {
        match self {
            Self::Backup => KeyPurpose::Backup,
            Self::Checkpoint => KeyPurpose::Checkpoint,
            Self::Wal
            | Self::Segment
            | Self::Index
            | Self::Manifest
            | Self::Snapshot
            | Self::LargeValueChunk => KeyPurpose::Data,
        }
    }
}

/// Environment, tenant, database, and purpose bound to one key hierarchy.
#[derive(Clone, Debug, Eq, Hash, PartialEq)]
pub struct KeyScope {
    environment: String,
    tenant: TenantUlid,
    project: Option<ProjectUlid>,
    database: DatabaseUlid,
    purpose: KeyPurpose,
}

impl KeyScope {
    /// Creates a validated key scope.
    ///
    /// # Errors
    ///
    /// Returns [`EncryptionError::InvalidKeyMetadata`] for an invalid
    /// environment identity.
    pub fn new(
        environment: impl Into<String>,
        tenant: TenantUlid,
        database: DatabaseUlid,
        purpose: KeyPurpose,
    ) -> Result<Self, EncryptionError> {
        let environment = environment.into();
        if !valid_identity(&environment) {
            return Err(EncryptionError::InvalidKeyMetadata);
        }
        Ok(Self { environment, tenant, project: None, database, purpose })
    }

    /// Creates a scope bound to the complete durable owner tuple.
    pub fn new_with_project(
        environment: impl Into<String>,
        tenant: TenantUlid,
        project: ProjectUlid,
        database: DatabaseUlid,
        purpose: KeyPurpose,
    ) -> Result<Self, EncryptionError> {
        let mut scope = Self::new(environment, tenant, database, purpose)?;
        scope.project = Some(project);
        Ok(scope)
    }

    /// Returns the environment identity.
    #[must_use]
    pub fn environment(&self) -> &str {
        &self.environment
    }

    /// Returns the tenant ULID.
    #[must_use]
    pub const fn tenant(&self) -> TenantUlid {
        self.tenant
    }

    /// Returns the database ULID.
    #[must_use]
    pub const fn database(&self) -> DatabaseUlid {
        self.database
    }

    /// Returns the optional project identity. Legacy scopes omit it.
    #[must_use]
    pub const fn project(&self) -> Option<ProjectUlid> {
        self.project
    }

    /// Returns the isolated key purpose.
    #[must_use]
    pub const fn purpose(&self) -> KeyPurpose {
        self.purpose
    }
}

/// Durable metadata identifying one database-key version.
#[derive(Clone, Debug, Eq, Hash, PartialEq)]
pub struct KeyVersionMetadata {
    scope: KeyScope,
    version: DatabaseKeyVersion,
}

impl KeyVersionMetadata {
    /// Binds a version to exactly one key scope.
    #[must_use]
    pub const fn new(scope: KeyScope, version: DatabaseKeyVersion) -> Self {
        Self { scope, version }
    }

    /// Returns the key scope.
    #[must_use]
    pub const fn scope(&self) -> &KeyScope {
        &self.scope
    }

    /// Returns the database-key version.
    #[must_use]
    pub const fn version(&self) -> DatabaseKeyVersion {
        self.version
    }
}

/// The only key-provider identities accepted by ADR 0030.
#[derive(Clone, Copy, Debug, Eq, Hash, PartialEq)]
pub enum KeyProviderIdentity {
    /// Owner-only local development key storage. Never production-capable.
    LocalFile,
    /// The Orchestrator-owned OCI Vault adapter.
    OciVault,
}

impl KeyProviderIdentity {
    /// Returns the stable provider name.
    #[must_use]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::LocalFile => "local-file",
            Self::OciVault => "oci-vault",
        }
    }
}

/// Runtime capability reported by a key provider.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ProviderCapability {
    /// The provider is intentionally limited to local development.
    LocalDevelopmentOnly,
    /// No provider adapter or workload authorization is configured.
    Unconfigured,
    /// An adapter is configured, but this fact is not release qualification.
    ConfiguredUnqualified,
}

/// The honest production capability of this process.
///
/// This is runtime status only. It cannot represent external OCI, FIPS,
/// architecture, or release qualification.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ProductionCapabilityStatus {
    /// The binary selected the non-FIPS local development feature.
    LocalCryptoSelected,
    /// The selected AWS-LC FIPS module did not report operational mode.
    FipsModuleUnavailable,
    /// The FIPS module is operational, but OCI Vault is not configured.
    OciVaultUnconfigured,
    /// OCI is configured, but candidate-bound qualification is still absent.
    ConfiguredUnqualified,
}

impl ProductionCapabilityStatus {
    /// Returns whether this status can authorize a production-ready claim.
    ///
    /// No current variant can. Qualification lives outside this runtime seam.
    #[must_use]
    pub const fn is_production_ready(self) -> bool {
        false
    }
}

/// Reports the selected crypto module and provider configuration without
/// turning either into qualification evidence.
#[must_use]
pub fn production_capability_status(
    provider: &dyn KeyWrappingProvider,
) -> ProductionCapabilityStatus {
    #[cfg(feature = "local-crypto")]
    {
        let _ = provider;
        ProductionCapabilityStatus::LocalCryptoSelected
    }
    #[cfg(feature = "production-fips")]
    {
        if aws_lc_rs::try_fips_mode().is_err() {
            return ProductionCapabilityStatus::FipsModuleUnavailable;
        }
        match provider.capability() {
            ProviderCapability::Unconfigured | ProviderCapability::LocalDevelopmentOnly => {
                ProductionCapabilityStatus::OciVaultUnconfigured
            }
            ProviderCapability::ConfiguredUnqualified => {
                ProductionCapabilityStatus::ConfiguredUnqualified
            }
        }
    }
}

/// Opaque provider-wrapped database key plus its durable lookup metadata.
pub struct WrappedDatabaseKey {
    metadata: KeyVersionMetadata,
    region: RegionCode,
    provider: KeyProviderIdentity,
    provider_reference_digest: [u8; 32],
    material: Vec<u8>,
}

impl WrappedDatabaseKey {
    /// Validates bounded opaque wrapper material returned by an adapter.
    ///
    /// # Errors
    ///
    /// Returns [`EncryptionError::InvalidWrappedKey`] for empty or oversized
    /// wrapper material.
    pub fn new(
        metadata: KeyVersionMetadata,
        region: RegionCode,
        provider: KeyProviderIdentity,
        provider_reference_digest: [u8; 32],
        material: Vec<u8>,
    ) -> Result<Self, EncryptionError> {
        if material.is_empty() || material.len() > MAX_WRAPPED_KEY_BYTES {
            return Err(EncryptionError::InvalidWrappedKey);
        }
        Ok(Self { metadata, region, provider, provider_reference_digest, material })
    }

    /// Returns the key-version metadata.
    #[must_use]
    pub const fn metadata(&self) -> &KeyVersionMetadata {
        &self.metadata
    }

    /// Returns the canonical Region holding this wrapper.
    #[must_use]
    pub const fn region(&self) -> RegionCode {
        self.region
    }

    /// Returns the provider identity.
    #[must_use]
    pub const fn provider(&self) -> KeyProviderIdentity {
        self.provider
    }

    /// Returns the provider-reference digest allowed in internal evidence.
    #[must_use]
    pub const fn provider_reference_digest(&self) -> [u8; 32] {
        self.provider_reference_digest
    }

    /// Returns the opaque wrapped bytes for durable catalog encoding.
    #[must_use]
    pub fn material(&self) -> &[u8] {
        &self.material
    }
}

impl fmt::Debug for WrappedDatabaseKey {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter
            .debug_struct("WrappedDatabaseKey")
            .field("metadata", &self.metadata)
            .field("region", &self.region)
            .field("provider", &self.provider)
            .field("wrapped_bytes", &self.material.len())
            .finish_non_exhaustive()
    }
}

/// An unwrapped database key with a bounded authorization lease.
pub struct DatabaseKeyLease {
    metadata: KeyVersionMetadata,
    key: DatabaseKey,
    provider: KeyProviderIdentity,
    expires_at: Instant,
}

impl DatabaseKeyLease {
    /// Creates a local lease for non-production tests and development only.
    ///
    /// Production cipher construction rejects leases created by this method;
    /// an adapter must use [`Self::from_adapter`] after external authorization.
    ///
    /// # Errors
    ///
    /// Returns [`EncryptionError::InvalidKeyLease`] for an empty or oversized
    /// lease duration.
    pub fn new(
        metadata: KeyVersionMetadata,
        key: DatabaseKey,
        duration: Duration,
    ) -> Result<Self, EncryptionError> {
        if duration.is_zero() || duration > MAX_DATABASE_KEY_LEASE {
            return Err(EncryptionError::InvalidKeyLease);
        }
        let expires_at =
            Instant::now().checked_add(duration).ok_or(EncryptionError::InvalidKeyLease)?;
        Ok(Self { metadata, key, provider: KeyProviderIdentity::LocalFile, expires_at })
    }

    /// Creates a lease on behalf of an authorized key-provider adapter.
    ///
    /// The adapter is responsible for establishing authorization and regional
    /// wrapper provenance before calling this constructor.
    ///
    /// # Errors
    ///
    /// Returns an error when the provider is not OCI Vault or the duration is
    /// outside the bounded lease interval.
    #[allow(
        dead_code,
        reason = "reserved for the in-crate OCI adapter; the current provider is fail-closed"
    )]
    pub(in crate::encryption) fn from_adapter(
        metadata: KeyVersionMetadata,
        key: DatabaseKey,
        duration: Duration,
        provider: KeyProviderIdentity,
    ) -> Result<Self, EncryptionError> {
        if provider != KeyProviderIdentity::OciVault {
            return Err(EncryptionError::InvalidKeyMetadata);
        }
        if duration.is_zero() || duration > MAX_DATABASE_KEY_LEASE {
            return Err(EncryptionError::InvalidKeyLease);
        }
        let expires_at =
            Instant::now().checked_add(duration).ok_or(EncryptionError::InvalidKeyLease)?;
        Ok(Self { metadata, key, provider, expires_at })
    }

    /// Returns the authorized key-version metadata.
    #[must_use]
    pub const fn metadata(&self) -> &KeyVersionMetadata {
        &self.metadata
    }

    /// Returns the provider identity that issued this lease.
    #[must_use]
    pub const fn provider(&self) -> KeyProviderIdentity {
        self.provider
    }

    /// Returns whether the authorization lease has expired.
    #[must_use]
    pub fn is_expired(&self) -> bool {
        Instant::now() >= self.expires_at
    }

    pub(super) const fn expires_at(&self) -> Instant {
        self.expires_at
    }

    /// Runs one provider or cipher operation while the lease remains valid.
    ///
    /// The callback receives a borrowed key and cannot retain ownership of
    /// the key material after the call.
    ///
    /// # Errors
    ///
    /// Returns [`EncryptionError::KeyLeaseExpired`] after the lease expires.
    pub fn with_database_key<T>(
        &self,
        operation: impl FnOnce(&DatabaseKey) -> T,
    ) -> Result<T, EncryptionError> {
        if self.is_expired() {
            Err(EncryptionError::KeyLeaseExpired)
        } else {
            Ok(operation(&self.key))
        }
    }
}

impl fmt::Debug for DatabaseKeyLease {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter
            .debug_struct("DatabaseKeyLease")
            .field("metadata", &self.metadata)
            .field("expired", &self.is_expired())
            .finish_non_exhaustive()
    }
}

/// One valid rotation from an active version to a new version in the same
/// key scope.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct KeyRotation {
    current: KeyVersionMetadata,
    next: KeyVersionMetadata,
}

impl KeyRotation {
    /// Validates that the versions differ and their scope is unchanged.
    ///
    /// # Errors
    ///
    /// Returns [`EncryptionError::InvalidKeyRotation`] when the scope changes
    /// or the version is reused.
    pub fn new(
        current: KeyVersionMetadata,
        next: KeyVersionMetadata,
    ) -> Result<Self, EncryptionError> {
        if current.scope != next.scope || current.version == next.version {
            return Err(EncryptionError::InvalidKeyRotation);
        }
        Ok(Self { current, next })
    }

    /// Returns the version being replaced.
    #[must_use]
    pub const fn current(&self) -> &KeyVersionMetadata {
        &self.current
    }

    /// Returns the new active version.
    #[must_use]
    pub const fn next(&self) -> &KeyVersionMetadata {
        &self.next
    }
}

/// Provider-neutral key wrapping, rotation, recovery, and destruction seam.
///
/// Provider implementations must keep OCI OCIDs and credentials outside all
/// returned `TerraBase` values. Operations fail closed when authorization or a
/// regional wrapper is unavailable.
pub trait KeyWrappingProvider: fmt::Debug + Send + Sync {
    /// Returns the stable provider identity.
    fn identity(&self) -> KeyProviderIdentity;

    /// Returns runtime configuration status, never qualification evidence.
    fn capability(&self) -> ProviderCapability;

    /// Wraps one database-key version in a canonical Region.
    ///
    /// # Errors
    ///
    /// Returns an error when provider authorization or wrapping fails.
    fn wrap_database_key(
        &self,
        metadata: &KeyVersionMetadata,
        region: RegionCode,
        key: &DatabaseKey,
    ) -> Result<WrappedDatabaseKey, EncryptionError>;

    /// Unwraps one regional wrapper into a bounded authorization lease.
    ///
    /// # Errors
    ///
    /// Returns an error when the wrapper or provider authorization is invalid.
    fn unwrap_database_key(
        &self,
        wrapper: &WrappedDatabaseKey,
        lease: Duration,
    ) -> Result<DatabaseKeyLease, EncryptionError>;

    /// Rewraps a version for key-encryption-key rotation or Region addition
    /// without changing the database-key version.
    ///
    /// # Errors
    ///
    /// Returns an error when provider authorization or the destination is
    /// invalid.
    fn rewrap_database_key(
        &self,
        wrapper: &WrappedDatabaseKey,
        destination: RegionCode,
    ) -> Result<WrappedDatabaseKey, EncryptionError>;

    /// Recovers a version from bounded surviving regional wrappers.
    ///
    /// # Errors
    ///
    /// Returns an error when no authorized wrapper can recover the version.
    fn recover_database_key(
        &self,
        expected: &KeyVersionMetadata,
        candidates: &[WrappedDatabaseKey],
        lease: Duration,
    ) -> Result<DatabaseKeyLease, EncryptionError>;

    /// Destroys one regional wrapper as part of durable crypto-erasure.
    ///
    /// # Errors
    ///
    /// Returns an error when provider authorization or destruction fails.
    fn destroy_wrapper(&self, wrapper: &WrappedDatabaseKey) -> Result<(), EncryptionError>;
}
