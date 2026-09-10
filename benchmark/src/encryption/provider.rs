//! Provider-neutral database-key metadata and lifecycle contracts.
//!
//! These types contain no OCI resource names or credentials. The
//! Orchestrator-owned adapter resolves a canonical `RegionCode` to provider
//! configuration outside `TerraBase`.

use std::collections::BTreeMap;
use std::fmt;
use std::time::{Duration, Instant};

use zeroize::Zeroize;

use crate::identity::{
    DatabaseUlid, ProjectUlid, TenantUlid, Ulid, UlidError, from_text, generate,
};
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
        Self(generate())
    }

    /// Parses the canonical 26-character uppercase ULID form.
    ///
    /// # Errors
    ///
    /// Returns the underlying ULID parse error for malformed text.
    pub fn from_text(text: &str) -> Result<Self, UlidError> {
        Ok(Self(from_text(text)?))
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
        self.0.to_string()
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
    /// The `SQLite` database file and its durable catalog metadata.
    Database,
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
    /// A customer export produced from the database state.
    Export,
    /// A restore-to-new-database or development branch artifact.
    Branch,
    /// A large-value chunk.
    LargeValueChunk,
    /// A checkpoint token or checkpoint-state artifact.
    Checkpoint,
    /// An endpoint-bound encrypted webhook signing secret.
    WebhookSecret,
}

impl ArtifactKind {
    /// Returns the stable HKDF and AAD name.
    #[must_use]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Database => "database",
            Self::Wal => "wal",
            Self::Segment => "segment",
            Self::Index => "index",
            Self::Manifest => "manifest",
            Self::Snapshot => "snapshot",
            Self::Backup => "backup",
            Self::Export => "export",
            Self::Branch => "branch",
            Self::LargeValueChunk => "large-value-chunk",
            Self::Checkpoint => "checkpoint",
            Self::WebhookSecret => "webhook-secret",
        }
    }

    /// Returns the key hierarchy this artifact must use.
    #[must_use]
    #[allow(clippy::match_same_arms, reason = "backup kinds stay readable per artifact")]
    pub const fn key_purpose(self) -> KeyPurpose {
        match self {
            Self::Backup => KeyPurpose::Backup,
            Self::Export | Self::Branch => KeyPurpose::Backup,
            Self::Checkpoint => KeyPurpose::Checkpoint,
            Self::Wal
            | Self::Database
            | Self::Segment
            | Self::Index
            | Self::Manifest
            | Self::Snapshot
            | Self::LargeValueChunk
            | Self::WebhookSecret => KeyPurpose::Data,
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
    ///
    /// # Errors
    ///
    /// Returns [`EncryptionError::InvalidKeyMetadata`] for an invalid
    /// environment identity.
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
    /// A customer-selected KMS adapter owned by the host orchestrator.
    CustomerKms,
}

impl KeyProviderIdentity {
    /// Returns whether this identity may authenticate a production key lease.
    #[must_use]
    pub const fn is_production(self) -> bool {
        matches!(self, Self::OciVault | Self::CustomerKms)
    }

    /// Returns the stable provider name.
    #[must_use]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::LocalFile => "local-file",
            Self::OciVault => "oci-vault",
            Self::CustomerKms => "customer-kms",
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
/// This is runtime status only. It cannot represent external OCI,
/// architecture, or release qualification.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ProductionCapabilityStatus {
    /// OCI Vault is not configured.
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
    match provider.capability() {
        ProviderCapability::Unconfigured | ProviderCapability::LocalDevelopmentOnly => {
            ProductionCapabilityStatus::OciVaultUnconfigured
        }
        ProviderCapability::ConfiguredUnqualified => {
            ProductionCapabilityStatus::ConfiguredUnqualified
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
        // A wrapper is a production catalog fact.  Do not allow a provider
        // adapter (or a malformed catalog record) to smuggle an inactive
        // Region or a local-file wrapper into the OCI hierarchy.
        if provider != KeyProviderIdentity::OciVault
            || !crate::regions::RegionCatalog::production().is_registered(&region)
            || material.is_empty()
            || material.len() > MAX_WRAPPED_KEY_BYTES
        {
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

impl Drop for WrappedDatabaseKey {
    fn drop(&mut self) {
        self.material.zeroize();
    }
}

/// The regional wrapper set for one database-key version.
///
/// The DEK itself is deliberately not retained here.  A provider adapter
/// creates it for one wrapping operation and this value retains only the
/// opaque OCI Vault wrappers needed to recover it in the selected Regions.
/// The set is exact: every selected Region has one wrapper and no wrapper
/// from outside the placement lock is admitted.
pub struct DatabaseKeyHierarchy {
    metadata: KeyVersionMetadata,
    wrappers: BTreeMap<RegionCode, WrappedDatabaseKey>,
}

impl fmt::Debug for DatabaseKeyHierarchy {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter
            .debug_struct("DatabaseKeyHierarchy")
            .field("metadata", &self.metadata)
            .field("regions", &self.wrappers.keys().collect::<Vec<_>>())
            .field("crypto_shredded", &self.wrappers.is_empty())
            .finish()
    }
}

impl DatabaseKeyHierarchy {
    /// Builds a hierarchy from wrappers loaded beside a placement record.
    ///
    /// This validates the exact Region set and common key metadata before a
    /// caller can unwrap any key.  It is the catalog-read hook used by the
    /// Orchestrator/member integration; no provider or OCI identifier is
    /// stored in the public value beyond the opaque wrapper bytes.
    ///
    /// # Errors
    ///
    /// Returns [`EncryptionError::InvalidWrappedKey`] for an empty or
    /// unregistered Region set, a wrapper with mismatched metadata, a
    /// non-`OciVault` provider, or a duplicate or incomplete Region set.
    pub fn from_wrappers(
        metadata: KeyVersionMetadata,
        selected_regions: impl IntoIterator<Item = RegionCode>,
        wrappers: impl IntoIterator<Item = WrappedDatabaseKey>,
    ) -> Result<Self, EncryptionError> {
        let regions: std::collections::BTreeSet<_> = selected_regions.into_iter().collect();
        if regions.is_empty()
            || regions
                .iter()
                .any(|region| !crate::regions::RegionCatalog::production().is_registered(region))
        {
            return Err(EncryptionError::InvalidWrappedKey);
        }
        let mut by_region = BTreeMap::new();
        for wrapper in wrappers {
            if wrapper.metadata() != &metadata
                || wrapper.provider() != KeyProviderIdentity::OciVault
                || !regions.contains(&wrapper.region())
                || by_region.insert(wrapper.region(), wrapper).is_some()
            {
                return Err(EncryptionError::InvalidWrappedKey);
            }
        }
        if by_region.len() != regions.len() {
            return Err(EncryptionError::InvalidWrappedKey);
        }
        Ok(Self { metadata, wrappers: by_region })
    }

    /// Creates and wraps a fresh per-database DEK in every selected Region.
    ///
    /// The provider uses its regional OCI Vault KEK. An unavailable adapter
    /// returns [`EncryptionError::KeyProviderUnconfigured`]; production host
    /// provisioning and durable wrappers are owned by the Orchestrator.
    ///
    /// # Errors
    ///
    /// Returns [`EncryptionError::InvalidWrappedKey`] for an empty or
    /// unregistered Region set or a wrapper that fails validation, and
    /// propagates key-generation or provider wrapping failures.
    pub fn create(
        provider: &dyn KeyWrappingProvider,
        scope: KeyScope,
        version: DatabaseKeyVersion,
        selected_regions: impl IntoIterator<Item = RegionCode>,
    ) -> Result<Self, EncryptionError> {
        let regions: Vec<_> = selected_regions.into_iter().collect();
        if regions.is_empty()
            || regions
                .iter()
                .any(|region| !crate::regions::RegionCatalog::production().is_registered(region))
        {
            return Err(EncryptionError::InvalidWrappedKey);
        }
        let metadata = KeyVersionMetadata::new(scope, version);
        let mut dek = DatabaseKey::generate()?;
        let mut wrappers = Vec::with_capacity(regions.len());
        for region in regions {
            let wrapper = provider.wrap_database_key(&metadata, region, &dek)?;
            if wrapper.metadata() != &metadata
                || wrapper.provider() != provider.identity()
                || wrapper.region() != region
            {
                return Err(EncryptionError::InvalidWrappedKey);
            }
            wrappers.push(wrapper);
        }
        dek.destroy();
        let wrapper_regions: Vec<_> = wrappers.iter().map(WrappedDatabaseKey::region).collect();
        Self::from_wrappers(metadata, wrapper_regions, wrappers)
    }

    /// Returns the version metadata shared by every regional wrapper.
    #[must_use]
    pub const fn metadata(&self) -> &KeyVersionMetadata {
        &self.metadata
    }

    /// Returns the exact selected Regions carrying wrappers.
    #[must_use = "the selected Region set must be used for wrapper checks"]
    pub fn regions(&self) -> impl Iterator<Item = RegionCode> + '_ {
        self.wrappers.keys().copied()
    }

    /// Returns the wrapper for one selected Region, if it has not been
    /// crypto-shredded.
    #[must_use]
    pub fn wrapper(&self, region: RegionCode) -> Option<&WrappedDatabaseKey> {
        self.wrappers.get(&region)
    }

    /// Returns whether all regional wrappers have been destroyed.
    #[must_use]
    pub fn is_crypto_shredded(&self) -> bool {
        self.wrappers.is_empty()
    }

    /// Destroys every regional wrapper and removes the DEK recovery path.
    ///
    /// Destruction is incremental and retryable: wrappers successfully
    /// destroyed before a provider error are removed, while the remaining
    /// wrappers stay available for a later repair attempt.  No success is
    /// reported until every selected Region is gone.
    ///
    /// # Errors
    ///
    /// Returns [`EncryptionError::InvalidWrappedKey`] when the provider is not
    /// `OciVault`, and propagates the provider's destruction failure with
    /// already-destroyed wrappers removed for retry.
    ///
    /// # Panics
    ///
    /// Never panics on a consistent hierarchy; the internal lookup expects a
    /// wrapper key collected from the same hierarchy.
    pub fn crypto_shred(
        &mut self,
        provider: &dyn KeyWrappingProvider,
    ) -> Result<(), EncryptionError> {
        if provider.identity() != KeyProviderIdentity::OciVault {
            return Err(EncryptionError::InvalidWrappedKey);
        }
        let regions: Vec<_> = self.wrappers.keys().copied().collect();
        for region in regions {
            let wrapper = self.wrappers.remove(&region).expect("wrapper key exists");
            if let Err(error) = provider.destroy_wrapper(&wrapper) {
                self.wrappers.insert(region, wrapper);
                return Err(error);
            }
        }
        Ok(())
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
    /// production leases are issued only after authenticated provider delivery.
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

    /// Creates a lease on behalf of an authorized key-provider adapter in
    /// test-support fixtures.
    ///
    /// The adapter is responsible for establishing authorization and regional
    /// wrapper provenance before calling this constructor.
    ///
    /// # Errors
    ///
    /// Returns an error when the provider is not OCI Vault or the duration is
    /// outside the bounded lease interval.
    #[cfg(any(test, feature = "test-support"))]
    pub(in crate::encryption) fn from_adapter(
        metadata: KeyVersionMetadata,
        key: DatabaseKey,
        duration: Duration,
        provider: KeyProviderIdentity,
    ) -> Result<Self, EncryptionError> {
        if matches!(provider, KeyProviderIdentity::LocalFile) {
            return Err(EncryptionError::InvalidKeyMetadata);
        }
        if duration.is_zero() || duration > MAX_DATABASE_KEY_LEASE {
            return Err(EncryptionError::InvalidKeyLease);
        }
        let expires_at =
            Instant::now().checked_add(duration).ok_or(EncryptionError::InvalidKeyLease)?;
        Ok(Self { metadata, key, provider, expires_at })
    }

    pub(super) fn from_adapter_until(
        metadata: KeyVersionMetadata,
        key: DatabaseKey,
        expires_at: Instant,
        provider: KeyProviderIdentity,
    ) -> Result<Self, EncryptionError> {
        let remaining = expires_at
            .checked_duration_since(Instant::now())
            .filter(|duration| !duration.is_zero())
            .ok_or(EncryptionError::KeyLeaseExpired)?;
        if remaining > MAX_DATABASE_KEY_LEASE {
            return Err(EncryptionError::InvalidKeyLease);
        }
        if matches!(provider, KeyProviderIdentity::LocalFile) {
            return Err(EncryptionError::InvalidKeyMetadata);
        }
        Ok(Self { metadata, key, provider, expires_at })
    }

    /// Constructs an OCI Vault lease for injected test fixtures.
    ///
    /// # Errors
    ///
    /// Returns an error when the lease duration or metadata is invalid.
    #[cfg(any(test, feature = "test-support"))]
    pub fn test_oci_vault(
        metadata: KeyVersionMetadata,
        key: DatabaseKey,
        duration: Duration,
    ) -> Result<Self, EncryptionError> {
        Self::from_adapter(metadata, key, duration, KeyProviderIdentity::OciVault)
    }

    /// Constructs a customer KMS lease for injected test fixtures.
    ///
    /// # Errors
    ///
    /// Returns an error when the lease duration or metadata is invalid.
    #[cfg(any(test, feature = "test-support"))]
    pub fn test_customer_kms(
        metadata: KeyVersionMetadata,
        key: DatabaseKey,
        duration: Duration,
    ) -> Result<Self, EncryptionError> {
        Self::from_adapter(metadata, key, duration, KeyProviderIdentity::CustomerKms)
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

/// Refreshable leases for one durable database key hierarchy.
///
/// Implementations bind the complete owner scope and selected Region. Readers
/// request the version recorded in their artifact; writers use the version
/// selected by the durable rotation state, never an inferred newest version.
pub trait DatabaseKeySource: fmt::Debug + Send + Sync {
    /// Obtains the admitted version used for new encrypted artifacts.
    ///
    /// # Errors
    /// Returns a redacted refusal when authorization cannot be renewed.
    fn current_lease(&self) -> Result<std::sync::Arc<DatabaseKeyLease>, EncryptionError>;

    /// Obtains exactly one historical version for authenticated reads.
    ///
    /// # Errors
    /// Returns a refusal for missing, retired or unauthorized versions.
    fn lease(
        &self,
        version: DatabaseKeyVersion,
    ) -> Result<std::sync::Arc<DatabaseKeyLease>, EncryptionError>;
}

/// Adapter-owned customer KMS lease delivery.
///
/// The adapter owns provider credentials, resource identifiers, authorization,
/// and revocation checks. TerraBase receives only the already-authorized,
/// bounded lease and its durable request metadata.
pub trait CustomerKmsAdapter: fmt::Debug + Send + Sync {
    /// Returns the provider identity carried by every lease from this adapter.
    fn identity(&self) -> KeyProviderIdentity {
        // Existing host adapters predate provider-neutral customer KMS. They
        // issue OCI leases unless they explicitly opt into another identity.
        KeyProviderIdentity::OciVault
    }
    /// Obtains one authorized lease for exactly this scope, version, and
    /// region. Missing or revoked versions must fail closed.
    ///
    /// # Errors
    ///
    /// Returns an error when authorization, metadata, or provider access fails.
    fn lease(
        &self,
        metadata: &KeyVersionMetadata,
        region: RegionCode,
    ) -> Result<DatabaseKeyLease, EncryptionError>;
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
