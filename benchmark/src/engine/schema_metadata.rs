//! Durable public schema envelopes, outside document storage and metering.

use super::{Engine, EngineError};

impl Engine {
    /// Persists the schema-declared history window used by the retention
    /// worker. This is deliberately a narrow storage seam so schema runtime
    /// cannot mutate SQLite tables directly.
    pub fn set_collection_history_retention(
        &self,
        collection: &crate::types::CollectionName,
        days: u32,
    ) -> Result<(), EngineError> {
        self.lock_storage()?
            .port
            .set_collection_history(collection, days)
            .map_err(|()| EngineError::StorageFailure)
    }

    /// Removes a retired declared full-text descriptor and its term rows.
    ///
    /// # Errors
    /// Returns a storage failure if the durable removal fails.
    pub fn remove_schema_fts(
        &self,
        collection: &crate::types::CollectionName,
        field: &str,
    ) -> Result<(), EngineError> {
        self.lock_storage()?
            .port
            .remove_schema_fts(collection, field)
            .map_err(|()| EngineError::StorageFailure)
    }

    /// Installs TTL extraction for future writes and advances at most `batch` retained rows.
    /// Progress is durable across restart; true means the retained-row backfill completed.
    ///
    /// # Errors
    /// Returns a storage failure for invalid batch sizes or failed durable operations.
    pub fn advance_schema_ttl(
        &self,
        collection: &crate::types::CollectionName,
        field: Option<&str>,
        batch: u32,
    ) -> Result<bool, EngineError> {
        self.lock_storage()?
            .port
            .advance_schema_ttl(collection, field, batch)
            .map_err(|()| EngineError::StorageFailure)
    }

    /// Reports whether a durable full-text descriptor has completed its backfill.
    ///
    /// # Errors
    /// Returns a storage failure if the engine storage is unavailable.
    pub fn schema_fts_ready(
        &self,
        collection: &crate::types::CollectionName,
        field: &str,
    ) -> Result<bool, EngineError> {
        Ok(self.lock_storage()?.port.fts_descriptor_state(collection, field) == Some(true))
    }

    /// Loads the complete schema envelope for one database, branch, and collection.
    ///
    /// The authenticated server adapter supplies the target. These bytes are
    /// internal metadata and never become customer documents or usage facts.
    ///
    /// # Errors
    /// Returns an engine closure or durable storage failure.
    pub fn schema_metadata_load(
        &self,
        database: &str,
        branch: &str,
        collection: &str,
    ) -> Result<Option<Vec<u8>>, EngineError> {
        self.lock_storage()?
            .port
            .schema_metadata_load(database, branch, collection)
            .map_err(|()| EngineError::StorageFailure)
    }

    /// Reads a bounded page of private schema envelopes in collection order.
    ///
    /// The member selects its admitted database and branch. The cursor is
    /// exclusive, so scanning can continue fairly across collections.
    ///
    /// # Errors
    /// Returns a storage failure or `SnapshotPageLimitExceeded` when a page
    /// exceeds 16 MiB or the requested row limit is outside 1..=64.
    pub fn schema_metadata_list(
        &self,
        database: &str,
        branch: &str,
        after: Option<&str>,
        limit: u32,
    ) -> Result<Vec<(String, Vec<u8>)>, EngineError> {
        if !(1..=64).contains(&limit) {
            return Err(EngineError::SnapshotPageLimitExceeded);
        }
        let storage = self.lock_storage()?;
        let connection =
            storage.port.open_sqlite_read_connection().map_err(|()| EngineError::StorageFailure)?;
        let mut statement = connection.prepare(
            "SELECT collection, length(envelope), envelope FROM schema_metadata WHERE database_id = ?1 AND branch = ?2 AND (?3 IS NULL OR collection > ?3) ORDER BY collection LIMIT ?4"
        ).map_err(|_| EngineError::StorageFailure)?;
        let mut rows = statement
            .query(rusqlite::params![database, branch, after, limit])
            .map_err(|_| EngineError::StorageFailure)?;
        let mut result = Vec::new();
        let mut bytes = 0_u64;
        while let Some(row) = rows.next().map_err(|_| EngineError::StorageFailure)? {
            let length: i64 = row.get(1).map_err(|_| EngineError::StorageFailure)?;
            let length =
                u64::try_from(length).map_err(|_| EngineError::SnapshotPageLimitExceeded)?;
            bytes = bytes.checked_add(length).ok_or(EngineError::SnapshotPageLimitExceeded)?;
            if bytes > 16 * 1024 * 1024 {
                return Err(EngineError::SnapshotPageLimitExceeded);
            }
            result.push((
                row.get(0).map_err(|_| EngineError::StorageFailure)?,
                row.get(2).map_err(|_| EngineError::StorageFailure)?,
            ));
        }
        Ok(result)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::EngineConfig;
    use crate::types::{TenantId, TenantScope};

    fn cas(
        engine: &Engine,
        database: &str,
        branch: &str,
        collection: &str,
        expected: Option<&[u8]>,
        record: &[u8],
    ) -> Result<bool, EngineError> {
        static NEXT: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(0);
        let scope = engine.scope_for(&TenantId::try_new("db").unwrap());
        let operation = crate::types::OperationId::try_new(&format!(
            "schema-cas-{}",
            NEXT.fetch_add(1, std::sync::atomic::Ordering::Relaxed)
        ))
        .unwrap();
        let mut transaction = engine.begin(&scope, engine.read_snapshot(&scope)?, operation)?;
        transaction.stage_schema_metadata(database, branch, collection, expected, record)?;
        match transaction.commit(crate::types::LocalDurability::Synced) {
            crate::engine::CommitOutcome::Committed(_) => Ok(true),
            crate::engine::CommitOutcome::Rejected(crate::engine::CommitError::Conflict) => {
                Ok(false)
            }
            _ => Err(EngineError::StorageFailure),
        }
    }

    #[test]
    fn schema_metadata_restart_cas_and_customer_data_isolation() {
        let directory = std::env::temp_dir().join(format!(
            "terrabase-schema-metadata-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .expect("clock")
                .as_nanos()
        ));
        let open =
            || Engine::open(EngineConfig::durable(&directory).expect("config")).expect("open");
        let engine = open();
        let first = br#"{"generation":1,"migration":"queued"}"#;
        let second = br#"{"generation":1,"migration":"complete"}"#;
        assert_eq!(engine.schema_metadata_load("db", "main", "items").unwrap(), None);
        assert!(cas(&engine, "db", "main", "items", None, first).unwrap());
        assert!(!cas(&engine, "db", "main", "items", None, second).unwrap());
        assert!(cas(&engine, "db", "main", "items", Some(first), second).unwrap());
        assert!(!cas(&engine, "db", "main", "items", Some(first), first).unwrap());
        assert_eq!(
            engine.schema_metadata_list("db", "main", None, 1).unwrap(),
            vec![("items".into(), second.to_vec())]
        );
        assert!(engine.schema_metadata_list("db", "main", Some("items"), 1).unwrap().is_empty());
        assert!(engine.schema_metadata_list("other", "main", None, 1).unwrap().is_empty());
        assert!(engine.schema_metadata_list("db", "main", None, 0).is_err());
        assert!(engine.schema_metadata_list("db", "main", None, 65).is_err());
        for target in [("other", "main", "items"), ("db", "dev", "items"), ("db", "main", "other")]
        {
            assert_eq!(engine.schema_metadata_load(target.0, target.1, target.2).unwrap(), None);
        }
        engine.shutdown().expect("shutdown");
        let engine = open();
        assert_eq!(
            engine.schema_metadata_load("db", "main", "items").unwrap(),
            Some(second.to_vec())
        );
        let scope = TenantScope::from_parts(TenantId::try_new("db").unwrap(), engine.identity());
        let snapshot = engine.read_snapshot(&scope).unwrap();
        assert!(engine.snapshot_documents(&scope, &snapshot).unwrap().is_empty());
        assert_eq!(engine.metering_live_bytes(&scope), 0);
        assert!(engine.metering_fact(&scope, "db").is_none_or(|fact| fact.writes() == 0));
        engine.shutdown().expect("shutdown");
        std::fs::remove_dir_all(directory).expect("cleanup");
    }

    #[test]
    fn schema_history_retention_is_durable_across_restart() {
        let directory = std::env::temp_dir().join(format!(
            "terrabase-schema-history-{}-{}",
            std::process::id(),
            crate::identity::generate()
        ));
        let open =
            || Engine::open(EngineConfig::durable(&directory).expect("config")).expect("open");
        let collection = crate::types::CollectionName::try_new("items").expect("collection");
        let engine = open();
        engine.set_collection_history_retention(&collection, 1).expect("set retention");
        let connection = engine.open_sqlite_read_connection().expect("read connection");
        assert_eq!(
            connection
                .query_row(
                    "SELECT days FROM collection_history WHERE collection = ?1",
                    [&collection.as_str()],
                    |row| row.get::<_, i64>(0),
                )
                .expect("retention row"),
            1
        );
        drop(connection);
        engine.shutdown().expect("shutdown");
        let engine = open();
        let connection = engine.open_sqlite_read_connection().expect("read connection");
        assert_eq!(
            connection
                .query_row(
                    "SELECT days FROM collection_history WHERE collection = ?1",
                    [&collection.as_str()],
                    |row| row.get::<_, i64>(0),
                )
                .expect("retention row after restart"),
            1
        );
        drop(connection);
        engine.shutdown().expect("shutdown");
        std::fs::remove_dir_all(directory).expect("cleanup");
    }
}

#[cfg(test)]
mod ttl_tests {
    use crate::engine::{CommitOutcome, Engine, EngineConfig};
    use crate::types::{
        CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion, LocalDurability,
        OperationId, TenantId,
    };
    use std::collections::BTreeMap;

    #[test]
    fn declared_ttl_backfill_resumes_and_future_writes_use_descriptor() {
        let directory = std::env::temp_dir().join(format!(
            "terrabase-declared-ttl-{}-{}",
            std::process::id(),
            crate::identity::generate()
        ));
        let open = || Engine::open(EngineConfig::durable(&directory).unwrap()).unwrap();
        let collection = CollectionName::try_new("items").unwrap();
        let owner = TenantId::try_new("owner").unwrap();
        let engine = open();
        let scope = engine.scope_for(&owner);
        let mut transaction = engine
            .begin(
                &scope,
                engine.read_snapshot(&scope).unwrap(),
                OperationId::try_new("seed").unwrap(),
            )
            .unwrap();
        for id in ["a", "b"] {
            transaction
                .put(
                    Document::new(
                        DocumentId::try_new(id).unwrap(),
                        collection.clone(),
                        DocumentValue::Object(BTreeMap::from([(
                            "deadline".into(),
                            DocumentValue::Integer(1),
                        )])),
                    ),
                    ExpectedVersion::New,
                )
                .unwrap();
        }
        assert!(matches!(transaction.commit(LocalDurability::Synced), CommitOutcome::Committed(_)));
        assert!(!engine.advance_schema_ttl(&collection, Some("deadline"), 1).unwrap());
        engine.shutdown().unwrap();
        let engine = open();
        assert!(!engine.advance_schema_ttl(&collection, Some("deadline"), 1).unwrap());
        // Shutdown maintenance can append expiry tombstones. They are retained
        // rows too, so resume through those rows before expecting completion.
        assert!(
            (0..4).any(|_| engine.advance_schema_ttl(&collection, Some("deadline"), 1).unwrap())
        );
        let scope = engine.scope_for(&owner);
        assert!(
            engine
                .snapshot_documents(&scope, &engine.read_snapshot(&scope).unwrap())
                .unwrap()
                .is_empty()
        );
        let mut transaction = engine
            .begin(
                &scope,
                engine.read_snapshot(&scope).unwrap(),
                OperationId::try_new("later").unwrap(),
            )
            .unwrap();
        transaction
            .put(
                Document::new(
                    DocumentId::try_new("c").unwrap(),
                    collection.clone(),
                    DocumentValue::Object(BTreeMap::from([(
                        "deadline".into(),
                        DocumentValue::Integer(1),
                    )])),
                ),
                ExpectedVersion::New,
            )
            .unwrap();
        assert!(matches!(transaction.commit(LocalDurability::Synced), CommitOutcome::Committed(_)));
        assert!(
            engine
                .snapshot_documents(&scope, &engine.read_snapshot(&scope).unwrap())
                .unwrap()
                .is_empty()
        );
        // Removing the descriptor clears expiry fields, but never resurrects
        // documents already tombstoned by shutdown maintenance.
        let connection = engine.open_sqlite_read_connection().unwrap();
        let tombstones: i64 = connection.query_row(
            "SELECT COUNT(*) FROM documents d WHERE collection='items' AND deleted=1 AND NOT EXISTS (SELECT 1 FROM documents newer WHERE newer.tenant=d.tenant AND newer.collection=d.collection AND newer.id=d.id AND newer.commit_seq>d.commit_seq)",
            [], |row| row.get(0),
        ).unwrap();
        drop(connection);
        assert!(engine.advance_schema_ttl(&collection, None, 8).unwrap());
        assert_eq!(
            engine
                .snapshot_documents(&scope, &engine.read_snapshot(&scope).unwrap())
                .unwrap()
                .len(),
            3 - usize::try_from(tombstones).unwrap()
        );
        engine.shutdown().unwrap();
        std::fs::remove_dir_all(directory).unwrap();
    }
}
