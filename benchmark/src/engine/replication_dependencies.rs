//! COPS admission over the engine's durable per-origin prefixes.
use super::{Engine, EngineError, merge};
use crate::types::TenantScope;

impl Engine {
    /// Returns the durable local origin identity used by complete publication tailers.
    /// # Errors
    /// Returns storage failure when the storage lock is unavailable.
    pub fn replication_origin(&self) -> Result<String, EngineError> {
        Ok(self.lock_storage()?.port.local_replication_origin().to_owned())
    }

    /// Checks causal admission for an atomic source publication.
    /// # Errors
    /// Rejects invalid scope, metadata or unavailable durable frontier.
    pub fn replication_publication_dependencies_ready(
        &self,
        scope: &TenantScope,
        publication: &super::ReplicatedPublication,
    ) -> Result<bool, EngineError> {
        self.validate_scope(scope)?;
        let context =
            merge::decode_context(&publication.context).ok_or(EngineError::InvalidDocument)?;
        if publication.origin.is_empty() || publication.origin_sequence == 0 {
            return Err(EngineError::InvalidDocument);
        }
        let frontier = self.durable_origin_prefixes(scope)?;
        Ok(frontier.get(&publication.origin).copied().unwrap_or(0)
            >= publication.origin_sequence.saturating_sub(1)
            && context
                .frontier()
                .iter()
                .all(|(origin, sequence)| frontier.get(origin).copied().unwrap_or(0) >= *sequence))
    }
}
