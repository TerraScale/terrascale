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

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{
        engine::{CommitOutcome, EngineConfig},
        types::{
            CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion, LocalDurability,
            OperationId, TenantId,
        },
    };

    #[test]
    fn retained_publication_outbox_keeps_original_values_and_private_controls() {
        let path = std::env::temp_dir().join(format!(
            "terrabase-outbox-{}-{}",
            std::process::id(),
            std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_nanos()
        ));
        let engine = Engine::open(EngineConfig::durable(&path).unwrap()).unwrap();
        let scope = engine.scope_for(&TenantId::try_new("tenant").unwrap());
        for (operation, value) in [("first", 1), ("second", 2)] {
            let mut transaction = engine
                .begin(
                    &scope,
                    engine.read_snapshot(&scope).unwrap(),
                    OperationId::try_new(operation).unwrap(),
                )
                .unwrap();
            transaction
                .put(
                    Document::new(
                        DocumentId::try_new("id").unwrap(),
                        CollectionName::try_new("users").unwrap(),
                        DocumentValue::Integer(value),
                    ),
                    ExpectedVersion::Any,
                )
                .unwrap();
            transaction.put_private_metadata("control", DocumentValue::Integer(value)).unwrap();
            assert!(matches!(
                transaction.commit(LocalDurability::Synced),
                CommitOutcome::Committed(_)
            ));
        }
        let groups = engine.replication_publications(&scope, 0, 10).unwrap();
        assert_eq!(groups.len(), 2);
        assert_eq!(groups[0].1.records[0].body, DocumentValue::Integer(1));
        assert_eq!(groups[1].1.records[0].body, DocumentValue::Integer(2));
        assert_eq!(groups[1].1.origin_sequence, groups[0].1.origin_sequence + 1);
        let first_controls =
            crate::storage::decode_private_changes(&groups[0].1.private_payload).unwrap();
        assert_eq!(first_controls.0[0].value, Some(DocumentValue::Integer(1)));
        let fingerprint = groups[0].1.fingerprint().unwrap();
        assert!(
            engine
                .replication_publications(
                    &engine.scope_for(&TenantId::try_new("other").unwrap()),
                    0,
                    10
                )
                .unwrap()
                .is_empty()
        );
        assert_eq!(engine.replication_publications(&scope, groups[0].0, 10).unwrap().len(), 1);
        engine.shutdown().unwrap();
        let engine = Engine::open(EngineConfig::durable(&path).unwrap()).unwrap();
        let scope = engine.scope_for(&TenantId::try_new("tenant").unwrap());
        assert_eq!(
            engine.replication_publications(&scope, 0, 10).unwrap()[0].1.fingerprint().unwrap(),
            fingerprint
        );
        engine.shutdown().unwrap();
        std::fs::remove_dir_all(path).unwrap();
    }
}
