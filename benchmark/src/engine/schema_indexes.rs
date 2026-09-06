//! Canonical schema index publication through the Engine storage boundary.

use super::{Engine, EngineError};
use crate::types::{CollectionName, TenantId};

impl Engine {
    /// Publishes a canonical compound index after extracted-field backfill.
    ///
    /// Returns false while the governed backfill is pending. Unique indexes
    /// reject duplicate live heads on publication and subsequent local writes;
    /// replication admission retains Region scope and may introduce conflicts.
    ///
    /// # Errors
    /// Returns a storage failure for invalid definitions, existing duplicates,
    /// a conflicting canonical name, or a durable storage failure.
    pub fn register_schema_index(
        &self,
        tenant: &TenantId,
        collection: &CollectionName,
        name: &str,
        fields: &[String],
        unique: bool,
    ) -> Result<bool, EngineError> {
        self.lock_storage()?
            .port
            .register_schema_index(tenant, collection, name, fields, unique)
            .map_err(|()| EngineError::StorageFailure)
    }

    /// Removes a canonical index and its local unique constraint, if present.
    ///
    /// # Errors
    /// Returns a storage failure when durable removal fails.
    pub fn remove_schema_index(
        &self,
        tenant: &TenantId,
        collection: &CollectionName,
        name: &str,
    ) -> Result<(), EngineError> {
        self.lock_storage()?
            .port
            .remove_schema_index(tenant, collection, name)
            .map_err(|()| EngineError::StorageFailure)
    }
}
