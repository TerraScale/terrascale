//! COPS admission over the engine's durable per-origin prefixes.
use super::{Engine, EngineError, ReplicatedRecord, merge};
use crate::types::TenantScope;

impl Engine {
    /// Checks the source predecessor and every causal context dependency.
    /// A false result must retain or retry the complete source publication;
    /// it must never produce a replication receipt.
    ///
    /// # Errors
    /// Returns scope/storage errors or invalid causal metadata.
    pub fn replication_dependencies_ready(
        &self,
        scope: &TenantScope,
        record: &ReplicatedRecord,
    ) -> Result<bool, EngineError> {
        let context = merge::decode_context(&record.context).ok_or(EngineError::InvalidDocument)?;
        if record.origin.is_empty() || record.origin_sequence == 0 {
            return Err(EngineError::InvalidDocument);
        }
        let frontier = self.durable_origin_prefixes(scope)?;
        if frontier.get(&record.origin).copied().unwrap_or(0)
            < record.origin_sequence.saturating_sub(1)
        {
            return Ok(false);
        }
        Ok(context
            .frontier()
            .iter()
            .all(|(origin, sequence)| frontier.get(origin).copied().unwrap_or(0) >= *sequence))
    }
}
