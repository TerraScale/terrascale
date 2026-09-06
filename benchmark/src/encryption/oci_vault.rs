//! Explicitly unavailable OCI Vault wrapping provider.
//!
//! The real adapter is Orchestrator-owned. Host leases use `OciVaultKeyDelivery`.
//! This unavailable wrapping sentinel intentionally has
//! no OCID, endpoint, credential, or plaintext-key configuration path.

use std::time::Duration;

use crate::regions::RegionCode;

use super::provider::{
    DatabaseKeyLease, KeyProviderIdentity, KeyVersionMetadata, KeyWrappingProvider,
    ProviderCapability, WrappedDatabaseKey,
};
use super::{DatabaseKey, EncryptionError};

/// An OCI Vault provider with no configured Orchestrator adapter.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct UnconfiguredOciVaultProvider;

impl UnconfiguredOciVaultProvider {
    /// Creates an explicitly unconfigured OCI wrapping provider.
    #[must_use]
    pub const fn new() -> Self {
        Self
    }

    fn unavailable<T>() -> Result<T, EncryptionError> {
        Err(EncryptionError::KeyProviderUnconfigured)
    }
}

impl KeyWrappingProvider for UnconfiguredOciVaultProvider {
    fn identity(&self) -> KeyProviderIdentity {
        KeyProviderIdentity::OciVault
    }

    fn capability(&self) -> ProviderCapability {
        ProviderCapability::Unconfigured
    }

    fn wrap_database_key(
        &self,
        _metadata: &KeyVersionMetadata,
        _region: RegionCode,
        _key: &DatabaseKey,
    ) -> Result<WrappedDatabaseKey, EncryptionError> {
        Self::unavailable()
    }

    fn unwrap_database_key(
        &self,
        _wrapper: &WrappedDatabaseKey,
        _lease: Duration,
    ) -> Result<DatabaseKeyLease, EncryptionError> {
        Self::unavailable()
    }

    fn rewrap_database_key(
        &self,
        _wrapper: &WrappedDatabaseKey,
        _destination: RegionCode,
    ) -> Result<WrappedDatabaseKey, EncryptionError> {
        Self::unavailable()
    }

    fn recover_database_key(
        &self,
        _expected: &KeyVersionMetadata,
        _candidates: &[WrappedDatabaseKey],
        _lease: Duration,
    ) -> Result<DatabaseKeyLease, EncryptionError> {
        Self::unavailable()
    }

    fn destroy_wrapper(&self, _wrapper: &WrappedDatabaseKey) -> Result<(), EncryptionError> {
        Self::unavailable()
    }
}
