//! Retained original transaction groups for trusted regional replication.

use super::{Engine, EngineError, ReplicatedPublication};
use crate::types::TenantScope;

impl Engine {
    /// Reads complete original source groups, including forwarded peer groups.
    ///
    /// Results carry their local durable cursor, while each publication retains
    /// its source origin, sequence, values, and private controls before merging.
    /// Pages contain at most 1024 groups and 64 MiB of encoded bodies.
    ///
    /// # Errors
    /// Returns a scope, closed-engine, invalid-limit or bounded storage failure.
    pub fn replication_publications(
        &self,
        scope: &TenantScope,
        after_local_sequence: u64,
        limit: u32,
    ) -> Result<Vec<(u64, ReplicatedPublication)>, EngineError> {
        self.validate_scope(scope)?;
        if !(1..=1024).contains(&limit) {
            return Err(EngineError::SnapshotPageLimitExceeded);
        }
        self.lock_storage()?
            .port
            .replication_publications(scope.tenant(), after_local_sequence, limit)
            .map_err(|()| EngineError::StorageFailure)
    }
}
