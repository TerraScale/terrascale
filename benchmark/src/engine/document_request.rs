//! Atomic original-request identities for single-document HTTP writes.

use super::{CommitReceipt, Engine, EngineError, Transaction};
use crate::{
    storage,
    types::{DocumentValue, ExpectedVersion, OperationId, TenantScope},
};

fn key(operation: &OperationId) -> String {
    format!("document-request:{}", operation.as_str())
}

impl Transaction<'_> {
    pub(super) fn valid_document_request_binding(&self) -> bool {
        if !self.metadata.iter().any(|write| write.key == key(&self.operation)) {
            return true;
        }
        matches!(
            self.writes.as_slice(),
            [storage::StagedWrite::Put { .. }
                | storage::StagedWrite::Delete { collection: Some(_), .. }]
        )
    }

    /// Binds an original request fingerprint to this single-document mutation.
    /// The identity and result version publish atomically with the document.
    ///
    /// # Errors
    /// Returns an error for anything other than one ordinary document mutation,
    /// an already bound identity, or unavailable storage.
    pub fn bind_document_request(&mut self, fingerprint: [u8; 32]) -> Result<(), EngineError> {
        self.engine.validate_scope(&self.scope)?;
        let key = key(&self.operation);
        if self.read_private_metadata(&key)?.is_some() {
            return Err(EngineError::OperationIdReuse { operation_id: self.operation.clone() });
        }
        let [write] = self.writes.as_mut_slice() else {
            return Err(EngineError::InvalidDocument);
        };
        let version = match write {
            storage::StagedWrite::Put { document, expected } => {
                let prior = self
                    .engine
                    .lock_storage()?
                    .port
                    .read_document_in_collection_at(
                        self.snapshot_sequence,
                        self.scope.tenant(),
                        document.collection(),
                        document.id(),
                    )
                    .map(|document| document.metadata().version());
                // Pin the predicted result version, including expiry between
                // staging and commit. Storage checks this under its write lock.
                if *expected == ExpectedVersion::Any {
                    *expected = prior.map_or(ExpectedVersion::New, ExpectedVersion::Exact);
                }
                DocumentValue::String(
                    prior.map_or(1, |version| version.saturating_add(1)).to_string(),
                )
            }
            storage::StagedWrite::Delete { collection: Some(_), .. } => DocumentValue::Null,
            _ => return Err(EngineError::InvalidDocument),
        };
        self.put_private_metadata(
            &key,
            DocumentValue::Array(vec![DocumentValue::String(hex::encode(fingerprint)), version]),
        )
    }
}

impl Engine {
    /// Resolves an original single-document request without consulting live state.
    /// The operation receipt and its metadata are read under the same storage
    /// lock. Metadata from another Region alone never proves a local commit.
    ///
    /// # Errors
    /// Returns an error for scope/authorization failures, a fingerprint mismatch,
    /// missing committed metadata, or unavailable storage.
    pub fn resolve_document_request(
        &self,
        scope: &TenantScope,
        operation: OperationId,
        fingerprint: [u8; 32],
    ) -> Result<Option<(CommitReceipt, Option<String>)>, EngineError> {
        self.validate_scope(scope)?;
        if self.lifecycle_is_paused(scope).map_err(|_| EngineError::StorageFailure)? {
            return Err(EngineError::StorageFailure);
        }
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
        let [DocumentValue::String(stored), version] = fields.as_slice() else {
            return Err(EngineError::StorageFailure);
        };
        if stored != &hex::encode(fingerprint) {
            return Err(EngineError::OperationIdReuse { operation_id: operation });
        }
        let revision = match version {
            DocumentValue::Null => None,
            DocumentValue::String(version) => Some(format!("{}-{version}", sequence.get())),
            _ => return Err(EngineError::StorageFailure),
        };
        Ok(Some((CommitReceipt::new(operation, sequence, durability), revision)))
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{
        CommitOutcome, EngineConfig,
        types::{CollectionName, Document, DocumentId, LocalDurability},
    };

    #[test]
    fn original_request_survives_restart_delete_and_recreation() {
        let (location, engine) = crate::engine::tests::open_engine("document-request");
        let scope = crate::engine::tests::tenant_scope(&engine, "tenant");
        let collection = CollectionName::try_new("items").unwrap();
        let id = DocumentId::try_new("doc").unwrap();
        let mut receipts = Vec::new();
        for (index, delete) in [false, false, true, false].into_iter().enumerate() {
            let operation = OperationId::try_new(&format!("request-{index}")).unwrap();
            let mut transaction = engine
                .begin(&scope, engine.read_snapshot(&scope).unwrap(), operation.clone())
                .unwrap();
            if delete {
                transaction.delete_in_collection(&collection, id.clone(), ExpectedVersion::Any);
            } else {
                transaction
                    .put(
                        Document::new(id.clone(), collection.clone(), DocumentValue::Null),
                        ExpectedVersion::Any,
                    )
                    .unwrap();
            }
            transaction.bind_document_request([u8::try_from(index).unwrap(); 32]).unwrap();
            let CommitOutcome::Committed(receipt) = transaction.commit(LocalDurability::Synced)
            else {
                panic!("commit");
            };
            let revision = engine
                .resolve_document_request(&scope, operation, [u8::try_from(index).unwrap(); 32])
                .unwrap()
                .unwrap()
                .1;
            let expected_version = [Some(1), Some(2), None, Some(1)][index];
            assert_eq!(
                revision,
                expected_version.map(|version| format!("{}-{version}", receipt.sequence().get()))
            );
            receipts.push((receipt.sequence(), revision));
        }
        engine.shutdown().unwrap();
        let engine = Engine::open(EngineConfig::durable(&location).unwrap()).unwrap();
        let scope = crate::engine::tests::tenant_scope(&engine, "tenant");
        for (index, (sequence, revision)) in receipts.into_iter().enumerate() {
            let operation = OperationId::try_new(&format!("request-{index}")).unwrap();
            let result = engine
                .resolve_document_request(
                    &scope,
                    operation.clone(),
                    [u8::try_from(index).unwrap(); 32],
                )
                .unwrap()
                .unwrap();
            assert_eq!((result.0.sequence(), result.1), (sequence, revision));
            assert!(matches!(
                engine.resolve_document_request(&scope, operation, [99; 32]),
                Err(EngineError::OperationIdReuse { .. })
            ));
        }
        let other = crate::engine::tests::tenant_scope(&engine, "other");
        assert!(
            engine
                .resolve_document_request(
                    &other,
                    OperationId::try_new("request-0").unwrap(),
                    [0; 32]
                )
                .unwrap()
                .is_none()
        );
        engine.shutdown().unwrap();
        std::fs::remove_dir_all(location).unwrap();
    }

    #[test]
    fn aborted_request_metadata_never_resolves_as_committed() {
        let (location, engine) = crate::engine::tests::open_engine("document-request-abort");
        let scope = crate::engine::tests::tenant_scope(&engine, "tenant");
        let operation = OperationId::try_new("aborted").unwrap();
        let mut transaction =
            engine.begin(&scope, engine.read_snapshot(&scope).unwrap(), operation.clone()).unwrap();
        transaction
            .put(
                Document::new(
                    DocumentId::try_new("doc").unwrap(),
                    CollectionName::try_new("items").unwrap(),
                    DocumentValue::Null,
                ),
                ExpectedVersion::Any,
            )
            .unwrap();
        transaction.bind_document_request([0; 32]).unwrap();
        drop(transaction);
        assert!(engine.resolve_document_request(&scope, operation, [0; 32]).unwrap().is_none());
        let operation = OperationId::try_new("appended-write").unwrap();
        let mut transaction =
            engine.begin(&scope, engine.read_snapshot(&scope).unwrap(), operation.clone()).unwrap();
        let document = |id| {
            Document::new(
                DocumentId::try_new(id).unwrap(),
                CollectionName::try_new("items").unwrap(),
                DocumentValue::Null,
            )
        };
        transaction.put(document("doc"), ExpectedVersion::Any).unwrap();
        transaction.bind_document_request([1; 32]).unwrap();
        transaction.put(document("extra"), ExpectedVersion::Any).unwrap();
        assert!(matches!(
            transaction.commit(LocalDurability::Synced),
            CommitOutcome::Rejected(crate::CommitError::Validation)
        ));
        assert!(engine.resolve_document_request(&scope, operation, [1; 32]).unwrap().is_none());
        engine.shutdown().unwrap();
        std::fs::remove_dir_all(location).unwrap();
    }
}
