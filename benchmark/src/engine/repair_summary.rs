//! Epoch-bound, immutable local cuts for durable range reconciliation.
use super::{Engine, EngineError, ReplicatedPublication};
use crate::types::{TenantId, TenantScope};
use std::collections::BTreeMap;
use terrabase_replication_core::anti_entropy::RangeFingerprint;

/// One tenant's exact set of source publications at a durable local cut.
///
/// Source frontiers are maximum represented sequences; sequence holes are not
/// evidence of missing writes because sequences can belong to other tenants.
#[derive(Clone, Debug)]
pub struct ReplicationRepairCut {
    tenant: TenantId,
    epoch: u64,
    database: crate::identity::DatabaseUlid,
    local_sequence: u64,
    frontier: BTreeMap<String, u64>,
    baseline: [u8; 32],
    expires: std::time::Instant,
}
impl ReplicationRepairCut {
    /// Durable local journal high-water captured with this cut.
    #[must_use]
    pub const fn local_sequence(&self) -> u64 {
        self.local_sequence
    }
    /// Database incarnation that owns this cut.
    #[must_use]
    pub const fn epoch(&self) -> u64 {
        self.epoch
    }
    /// Maximum represented source sequence for each origin at this cut.
    #[must_use]
    pub fn frontier(&self) -> &BTreeMap<String, u64> {
        &self.frontier
    }
    /// Stable database-lineage marker captured atomically with this cut.
    #[must_use]
    pub const fn baseline_digest(&self) -> [u8; 32] {
        self.baseline
    }
}
impl Engine {
    /// Captures an exact tenant-scoped source set without loading publications.
    ///
    /// # Errors
    /// Rejects invalid scopes, closed storage, or corrupt summary state.
    pub fn replication_repair_cut(
        &self,
        scope: &TenantScope,
    ) -> Result<ReplicationRepairCut, EngineError> {
        self.validate_scope(scope)?;
        let storage = self.lock_storage()?;
        let local_sequence = storage.port.published_state().0.get();
        let (frontier, baseline) = storage
            .port
            .replication_repair_cut(scope.tenant(), local_sequence)
            .map_err(|()| EngineError::StorageFailure)?;
        Ok(ReplicationRepairCut {
            tenant: scope.tenant().clone(),
            epoch: self.session_epoch().get(),
            database: self.database_id(),
            local_sequence,
            frontier,
            baseline,
            expires: std::time::Instant::now() + std::time::Duration::from_mins(5),
        })
    }
    fn validate_repair_cut(
        &self,
        scope: &TenantScope,
        cut: &ReplicationRepairCut,
        origin: &str,
        first: u64,
        last: u64,
    ) -> Result<(), EngineError> {
        self.validate_scope(scope)?;
        if std::time::Instant::now() >= cut.expires
            || cut.tenant != *scope.tenant()
            || cut.epoch != self.session_epoch().get()
            || cut.database != self.database_id()
            || origin.is_empty()
            || origin.len() > 255
            || first == 0
            || first > last
            || last > i64::MAX as u64
        {
            return Err(EngineError::InvalidDocument);
        }
        Ok(())
    }
    /// Summarizes an inclusive source sequence range at an immutable local cut.
    /// Uses at most two partial leaves and fixed-fanout aggregate lookups.
    ///
    /// # Errors
    /// Rejects a foreign scope/incarnation, invalid range, or unavailable history.
    pub fn replication_range_summary(
        &self,
        scope: &TenantScope,
        cut: &ReplicationRepairCut,
        origin: &str,
        first: u64,
        last: u64,
    ) -> Result<RangeFingerprint, EngineError> {
        self.validate_repair_cut(scope, cut, origin, first, last)?;
        self.lock_storage()?
            .port
            .replication_range_summary(scope.tenant(), origin, cut.local_sequence, first, last)
            .map_err(|()| EngineError::StorageFailure)
    }
    /// Loads at most 4096 source positions and revalidates canonical full hashes.
    /// Retention loss or changed source bytes fail closed; summaries never stand
    /// in for the actual transaction delivered through the normal apply path.
    ///
    /// # Errors
    /// Rejects invalid cuts, oversized leaves, lost retained rows, or corruption.
    pub fn replication_range_publications(
        &self,
        scope: &TenantScope,
        cut: &ReplicationRepairCut,
        origin: &str,
        first: u64,
        last: u64,
    ) -> Result<Vec<ReplicatedPublication>, EngineError> {
        self.validate_repair_cut(scope, cut, origin, first, last)?;
        if last - first >= 4096 {
            return Err(EngineError::SnapshotPageLimitExceeded);
        }
        self.lock_storage()?
            .port
            .replication_range_publications(scope.tenant(), origin, cut.local_sequence, first, last)
            .map_err(|()| EngineError::StorageFailure)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{
        engine::{CommitOutcome, EngineConfig},
        types::{DocumentValue, LocalDurability, OperationId},
    };
    #[test]
    fn durable_frozen_cuts_revalidate_retained_original_publications() {
        let path = std::env::temp_dir().join(format!(
            "repair-cut-{}-{}",
            std::process::id(),
            std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_nanos()
        ));
        let engine = Engine::open(EngineConfig::durable(&path).unwrap()).unwrap();
        let tenant = TenantId::try_new("tenant").unwrap();
        let scope = engine.scope_for(&tenant);
        for operation in ["one", "two"] {
            let mut tx = engine
                .begin(
                    &scope,
                    engine.read_snapshot(&scope).unwrap(),
                    OperationId::try_new(operation).unwrap(),
                )
                .unwrap();
            tx.put_private_metadata("key", DocumentValue::String(operation.to_owned())).unwrap();
            assert!(matches!(tx.commit(LocalDurability::Synced), CommitOutcome::Committed(_)));
        }
        let cut = engine.replication_repair_cut(&scope).unwrap();
        let (origin, last) = cut.frontier().iter().next().unwrap();
        let expected = engine.replication_range_summary(&scope, &cut, origin, 1, *last).unwrap();
        assert_eq!(expected.count, 2);
        let mut expired = cut.clone();
        expired.expires = std::time::Instant::now();
        assert!(engine.replication_range_summary(&scope, &expired, origin, 1, *last).is_err());
        let mut foreign = cut.clone();
        foreign.epoch = foreign.epoch.wrapping_add(1);
        assert!(engine.replication_range_summary(&scope, &foreign, origin, 1, *last).is_err());
        assert_eq!(
            engine.replication_range_publications(&scope, &cut, origin, 1, *last).unwrap().len(),
            2
        );
        assert!(
            engine
                .replication_range_summary(
                    &engine.scope_for(&TenantId::try_new("other").unwrap()),
                    &cut,
                    origin,
                    1,
                    *last
                )
                .is_err()
        );
        engine.shutdown().unwrap();
        let engine = Engine::open(EngineConfig::durable(&path).unwrap()).unwrap();
        let scope = engine.scope_for(&tenant);
        assert_eq!(
            engine.replication_range_summary(&scope, &cut, origin, 1, *last).unwrap(),
            expected
        );
        let raw = rusqlite::Connection::open(path.join("terrabase.sqlite3")).unwrap();
        raw.execute("UPDATE replication_publications SET operation='tampered'", []).unwrap();
        assert!(engine.replication_range_publications(&scope, &cut, origin, 1, *last).is_err());
        assert_eq!(
            engine.replication_range_summary(&scope, &cut, origin, 1, *last).unwrap(),
            expected
        );
        raw.execute("DELETE FROM replication_publications", []).unwrap();
        assert!(engine.replication_range_publications(&scope, &cut, origin, 1, *last).is_err());
        drop(raw);
        engine.shutdown().unwrap();
        std::fs::remove_dir_all(path).unwrap();
    }
}
