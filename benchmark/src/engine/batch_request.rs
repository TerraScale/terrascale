//! Atomic original-request identities and receipts for HTTP document batches.

use super::{CommitReceipt, Engine, EngineError, Transaction};
use crate::{
    storage,
    types::{DocumentValue, OperationId, TenantScope},
};

fn key(operation: &OperationId) -> String {
    format!("batch-request:{}", operation.as_str())
}

impl Transaction<'_> {
    pub(super) fn valid_batch_request_binding(&self) -> bool {
        let Some(binding) = self.metadata.iter().find(|write| write.key == key(&self.operation))
        else {
            return true;
        };
        let Some(DocumentValue::Array(fields)) = &binding.value else {
            return false;
        };
        let [DocumentValue::String(_), DocumentValue::String(count)] = fields.as_slice() else {
            return false;
        };
        count == &self.writes.len().to_string()
            && !self.writes.is_empty()
            && self.writes.iter().all(|write| {
                matches!(
                    write,
                    storage::StagedWrite::Put { .. }
                        | storage::StagedWrite::Delete { collection: Some(_), .. }
                )
            })
    }

    /// Binds the original batch identity and operation count atomically with its writes.
    ///
    /// # Errors
    /// Rejects empty, oversized, non-document, or already bound batches.
    pub fn bind_batch_request(
        &mut self,
        fingerprint: [u8; 32],
        count: usize,
    ) -> Result<(), EngineError> {
        self.engine.validate_scope(&self.scope)?;
        let key = key(&self.operation);
        if self.read_private_metadata(&key)?.is_some() {
            return Err(EngineError::OperationIdReuse { operation_id: self.operation.clone() });
        }
        if count == 0
            || count > 1000
            || count != self.writes.len()
            || !self.writes.iter().all(|write| {
                matches!(
                    write,
                    storage::StagedWrite::Put { .. }
                        | storage::StagedWrite::Delete { collection: Some(_), .. }
                )
            })
        {
            return Err(EngineError::InvalidDocument);
        }
        self.put_private_metadata(
            &key,
            DocumentValue::Array(vec![
                DocumentValue::String(hex::encode(fingerprint)),
                DocumentValue::String(count.to_string()),
            ]),
        )
    }
}

impl Engine {
    /// Resolves a batch's original receipt without reading its current documents.
    /// Metadata is read at the local committed receipt's sequence under the same lock.
    ///
    /// # Errors
    /// Rejects unauthorized scopes, changed request identities, or unavailable storage.
    pub fn resolve_batch_request(
        &self,
        scope: &TenantScope,
        operation: OperationId,
        fingerprint: [u8; 32],
    ) -> Result<Option<(CommitReceipt, usize)>, EngineError> {
        self.validate_scope(scope)?;
        if let Some(record) = self.read_cross_file_decision(scope, &operation)? {
            let Some((_, _, receipt)) = self.cross_file_completion(scope, &operation)? else {
                return if record.decision == Some(super::CrossFileDecision::Aborted) {
                    Ok(None)
                } else {
                    Err(EngineError::Conflict)
                };
            };
            let Some(DocumentValue::Array(fields)) = self.cross_file_request(scope, &operation)?
            else {
                return Err(EngineError::OperationIdReuse { operation_id: operation });
            };
            let [DocumentValue::String(stored), DocumentValue::String(count)] = fields.as_slice()
            else {
                return Err(EngineError::StorageFailure);
            };
            if stored != &hex::encode(fingerprint) {
                return Err(EngineError::OperationIdReuse { operation_id: operation });
            }
            let count = count
                .parse::<usize>()
                .ok()
                .filter(|count| (1..=1000).contains(count))
                .ok_or(EngineError::StorageFailure)?;
            return Ok(Some((receipt, count)));
        }
        if self.lifecycle_is_paused(scope).map_err(|_| EngineError::StorageFailure)? {
            return Err(EngineError::StorageFailure);
        }
        self.with_logical_read(scope, || {
            let storage = self.lock_storage()?;
            let Some(storage::OperationResolution::Committed { sequence, durability }) =
                storage.port.resolve_operation(scope.tenant(), &operation)
            else {
                return Ok(None);
            };
            let metadata = storage
                .port
                .private_metadata_at(scope.tenant(), &key(&operation), sequence)
                .map_err(|()| EngineError::StorageFailure)?;
            let Some(DocumentValue::Array(fields)) = metadata else {
                return Err(EngineError::OperationIdReuse { operation_id: operation });
            };
            let [DocumentValue::String(stored), DocumentValue::String(count)] = fields.as_slice()
            else {
                return Err(EngineError::StorageFailure);
            };
            if stored != &hex::encode(fingerprint) {
                return Err(EngineError::OperationIdReuse { operation_id: operation });
            }
            let count = count
                .parse::<usize>()
                .ok()
                .filter(|count| (1..=1000).contains(count))
                .ok_or(EngineError::StorageFailure)?;
            Ok(Some((CommitReceipt::new(operation, sequence, durability), count)))
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{
        CommitOutcome, EngineConfig,
        types::{CollectionName, Document, DocumentId, ExpectedVersion, LocalDurability},
    };

    #[test]
    fn batch_request_survives_restart_and_rejects_changed_identity() {
        let (location, engine) = crate::engine::tests::open_engine("batch-request");
        let scope = crate::engine::tests::tenant_scope(&engine, "tenant");
        let collection = CollectionName::try_new("items").unwrap();
        let operation = OperationId::try_new("batch").unwrap();
        let mut transaction =
            engine.begin(&scope, engine.read_snapshot(&scope).unwrap(), operation.clone()).unwrap();
        for id in ["one", "two"] {
            transaction
                .put(
                    Document::new(
                        DocumentId::try_new(id).unwrap(),
                        collection.clone(),
                        DocumentValue::Null,
                    ),
                    ExpectedVersion::Any,
                )
                .unwrap();
        }
        transaction.bind_batch_request([1; 32], 2).unwrap();
        let CommitOutcome::Committed(receipt) = transaction.commit(LocalDurability::Synced) else {
            panic!("batch commit");
        };
        engine.shutdown().unwrap();
        let engine = Engine::open(EngineConfig::durable(&location).unwrap()).unwrap();
        let scope = crate::engine::tests::tenant_scope(&engine, "tenant");
        let (replayed, count) =
            engine.resolve_batch_request(&scope, operation.clone(), [1; 32]).unwrap().unwrap();
        assert_eq!(replayed.sequence(), receipt.sequence());
        assert_eq!(count, 2);
        assert!(matches!(
            engine.resolve_batch_request(&scope, operation, [2; 32]),
            Err(EngineError::OperationIdReuse { .. })
        ));
        engine.shutdown().unwrap();
        std::fs::remove_dir_all(location).unwrap();
    }

    #[test]
    fn batch_binding_cannot_commit_an_appended_mutation() {
        let (location, engine) = crate::engine::tests::open_engine("batch-request-append");
        let scope = crate::engine::tests::tenant_scope(&engine, "tenant");
        let collection = CollectionName::try_new("items").unwrap();
        let operation = OperationId::try_new("batch").unwrap();
        let mut transaction =
            engine.begin(&scope, engine.read_snapshot(&scope).unwrap(), operation.clone()).unwrap();
        transaction
            .put(
                Document::new(
                    DocumentId::try_new("one").unwrap(),
                    collection.clone(),
                    DocumentValue::Null,
                ),
                ExpectedVersion::Any,
            )
            .unwrap();
        transaction.bind_batch_request([1; 32], 1).unwrap();
        transaction.delete_in_collection(
            &collection,
            DocumentId::try_new("one").unwrap(),
            ExpectedVersion::Any,
        );
        assert!(matches!(transaction.commit(LocalDurability::Synced), CommitOutcome::Rejected(_)));
        assert!(engine.resolve_batch_request(&scope, operation, [1; 32]).unwrap().is_none());
        engine.shutdown().unwrap();
        std::fs::remove_dir_all(location).unwrap();
    }
}
