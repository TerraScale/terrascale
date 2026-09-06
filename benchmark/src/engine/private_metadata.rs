//! Transactional schema and migration control records.

use super::{EngineError, Transaction};
use crate::{storage, types::DocumentValue};

impl Transaction<'_> {
    pub(crate) fn read_private_metadata(
        &self,
        key: &str,
    ) -> Result<Option<DocumentValue>, EngineError> {
        if let Some(staged) = self.metadata.iter().rev().find(|change| change.key == key) {
            return Ok(staged.value.clone());
        }
        self.engine
            .lock_storage()?
            .port
            .private_metadata_at(self.scope.tenant(), key, self.snapshot_sequence)
            .map_err(|()| EngineError::StorageFailure)
    }

    pub(crate) fn put_private_metadata(
        &mut self,
        key: &str,
        value: DocumentValue,
    ) -> Result<(), EngineError> {
        if key.is_empty() || key.len() > 4096 || storage::encode_private_value(&value).is_none() {
            return Err(EngineError::InvalidDocument);
        }
        self.stage_private_metadata(key, Some(value));
        Ok(())
    }

    pub(crate) fn delete_private_metadata(&mut self, key: &str) -> Result<(), EngineError> {
        if key.is_empty() || key.len() > 4096 {
            return Err(EngineError::InvalidDocument);
        }
        self.stage_private_metadata(key, None);
        Ok(())
    }

    fn stage_private_metadata(&mut self, key: &str, value: Option<DocumentValue>) {
        if let Some(staged) = self.metadata.iter_mut().find(|change| change.key == key) {
            staged.value = value;
        } else {
            self.metadata.push(storage::PrivateMetadataWrite { key: key.to_owned(), value });
        }
    }
}

impl Transaction<'_> {
    /// Stages an exact-byte schema envelope CAS with this transaction's validation changes.
    ///
    /// The comparison runs under the SQLite commit transaction. A conflict
    /// rejects the entire transaction, including documents and validation state.
    ///
    /// # Errors
    /// Returns `InvalidDocument` for oversized or duplicate target envelopes.
    pub fn stage_schema_metadata(
        &mut self,
        database: &str,
        branch: &str,
        collection: &str,
        expected: Option<&[u8]>,
        replacement: &[u8],
    ) -> Result<(), EngineError> {
        if database.is_empty()
            || database.len() > 255
            || branch.is_empty()
            || branch.len() > 255
            || collection.is_empty()
            || collection.len() > 255
            || replacement.len() > 16 * 1024 * 1024
            || expected.is_some_and(|bytes| bytes.len() > 16 * 1024 * 1024)
            || self.schema_envelopes.iter().any(|record| {
                record.database == database
                    && record.branch == branch
                    && record.collection == collection
            })
        {
            return Err(EngineError::InvalidDocument);
        }
        self.schema_envelopes.push(storage::SchemaEnvelopeWrite {
            database: database.to_owned(),
            branch: branch.to_owned(),
            collection: collection.to_owned(),
            expected: expected.map(<[u8]>::to_vec),
            replacement: replacement.to_vec(),
        });
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::{CommitError, CommitOutcome, Engine, EngineConfig};
    use crate::families::state::ValidationRecord;
    use crate::families::{SchemaDefinition, SchemaRule, SchemaVersion};
    use crate::types::{
        CollectionName, Document, DocumentId, ExpectedVersion, LocalDurability, OperationId,
        TenantId, TenantScope,
    };

    fn open(tag: &str) -> (std::path::PathBuf, Engine) {
        let path = std::env::temp_dir().join(format!(
            "terrabase-private-{tag}-{}-{}",
            std::process::id(),
            std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_nanos()
        ));
        let engine = Engine::open(EngineConfig::durable(&path).unwrap()).unwrap();
        (path, engine)
    }
    fn begin<'a>(engine: &'a Engine, scope: &TenantScope, operation: &str) -> Transaction<'a> {
        engine
            .begin(
                scope,
                engine.read_snapshot(scope).unwrap(),
                OperationId::try_new(operation).unwrap(),
            )
            .unwrap()
    }
    fn value() -> DocumentValue {
        ValidationRecord {
            schema: SchemaDefinition::try_new(
                SchemaVersion::try_new(1).unwrap(),
                vec![SchemaRule::Required { field: "name".into() }],
            )
            .unwrap(),
        }
        .encode()
    }
    fn document(id: &str) -> Document {
        Document::new(
            DocumentId::try_new(id).unwrap(),
            CollectionName::try_new("users").unwrap(),
            DocumentValue::Object(Default::default()),
        )
    }

    #[test]
    fn private_metadata_atomic_restart_snapshots_and_unbilled_state() {
        let (path, engine) = open("restart");
        let scope = engine.scope_for(&TenantId::try_new("tenant").unwrap());
        let old = begin(&engine, &scope, "old-reader");
        let mut aborted = begin(&engine, &scope, "aborted");
        aborted.put_private_metadata("control", DocumentValue::Integer(1)).unwrap();
        drop(aborted);
        assert_eq!(
            begin(&engine, &scope, "after-abort").read_private_metadata("control").unwrap(),
            None
        );
        let mut transaction = begin(&engine, &scope, "control-write");
        transaction.put_private_metadata("control", DocumentValue::Integer(2)).unwrap();
        transaction.stage_schema_metadata("db", "main", "users", None, b"schema-v1").unwrap();
        assert!(matches!(transaction.commit(LocalDurability::Synced), CommitOutcome::Committed(_)));
        assert_eq!(
            old.read_private_metadata("control").unwrap(),
            None,
            "immutable metadata snapshot"
        );
        drop(old);
        let mut replay = begin(&engine, &scope, "control-write");
        replay.put_private_metadata("control", DocumentValue::Integer(2)).unwrap();
        replay.stage_schema_metadata("db", "main", "users", None, b"schema-v1").unwrap();
        assert!(
            matches!(replay.commit(LocalDurability::Synced), CommitOutcome::Committed(_)),
            "identity replay precedes envelope CAS"
        );
        let mut reuse = begin(&engine, &scope, "control-write");
        reuse.put_private_metadata("control", DocumentValue::Integer(3)).unwrap();
        assert_eq!(
            reuse.commit(LocalDurability::Synced),
            CommitOutcome::Rejected(CommitError::OperationIdReuse)
        );
        assert!(
            engine
                .snapshot_documents(&scope, &engine.read_snapshot(&scope).unwrap())
                .unwrap()
                .is_empty()
        );
        assert!(engine.journal_rows(&scope, 0, 100).unwrap().is_empty());
        assert_eq!(engine.metering_live_bytes(&scope), 0);
        let usage = engine.metering_fact(&scope, "tenant");
        assert!(usage.is_none_or(|fact| fact.writes() == 0));
        engine.shutdown().unwrap();
        let engine = Engine::open(EngineConfig::durable(&path).unwrap()).unwrap();
        let scope = engine.scope_for(&TenantId::try_new("tenant").unwrap());
        assert_eq!(
            begin(&engine, &scope, "reopened").read_private_metadata("control").unwrap(),
            Some(DocumentValue::Integer(2))
        );
        assert_eq!(
            engine.schema_metadata_load("db", "main", "users").unwrap(),
            Some(b"schema-v1".to_vec())
        );
        let other = engine.scope_for(&TenantId::try_new("other").unwrap());
        assert_eq!(
            begin(&engine, &other, "isolated").read_private_metadata("control").unwrap(),
            None
        );
        engine.shutdown().unwrap();
        std::fs::remove_dir_all(path).unwrap();
    }

    #[test]
    fn private_metadata_schema_commit_fence_and_atomic_cas_refusal() {
        let (path, engine) = open("schema-fence");
        let scope = engine.scope_for(&TenantId::try_new("tenant").unwrap());
        let mut stale = begin(&engine, &scope, "stale-document");
        stale.put(document("stale"), ExpectedVersion::New).unwrap();
        let mut rejected = begin(&engine, &scope, "schema-with-invalid-document");
        rejected.put(document("invalid"), ExpectedVersion::New).unwrap();
        rejected.put_private_metadata("family:validation/users", value()).unwrap();
        assert_eq!(
            rejected.commit(LocalDurability::Synced),
            CommitOutcome::Rejected(CommitError::Validation)
        );
        assert_eq!(
            begin(&engine, &scope, "not-installed")
                .read_private_metadata("family:validation/users")
                .unwrap(),
            None
        );
        let mut schema = begin(&engine, &scope, "install-schema");
        schema.put_private_metadata("family:validation/users", value()).unwrap();
        schema.stage_schema_metadata("db", "main", "users", None, b"schema-v1").unwrap();
        assert!(matches!(schema.commit(LocalDurability::Synced), CommitOutcome::Committed(_)));
        assert_eq!(
            stale.commit(LocalDurability::Synced),
            CommitOutcome::Rejected(CommitError::Validation)
        );
        let mut conflict = begin(&engine, &scope, "stale-envelope");
        conflict.delete_private_metadata("family:validation/users").unwrap();
        conflict.stage_schema_metadata("db", "main", "users", None, b"wrong").unwrap();
        assert_eq!(
            conflict.commit(LocalDurability::Synced),
            CommitOutcome::Rejected(CommitError::Conflict)
        );
        assert_eq!(
            begin(&engine, &scope, "still-installed")
                .read_private_metadata("family:validation/users")
                .unwrap(),
            Some(value())
        );
        assert!(
            engine
                .snapshot_documents(&scope, &engine.read_snapshot(&scope).unwrap())
                .unwrap()
                .is_empty()
        );
        engine.shutdown().unwrap();
        std::fs::remove_dir_all(path).unwrap();
    }
}
