//! Durable public schema envelopes, outside document storage and metering.

use super::{Engine, EngineError};

impl Engine {
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

    /// Atomically replaces an envelope when its complete bytes equal `expected`.
    ///
    /// `None` requires absence. Returns `false` on conflict, including changes
    /// to migration progress that retain the same schema generation.
    ///
    /// # Errors
    /// Returns an engine closure or durable storage failure.
    pub fn schema_metadata_compare_exchange(
        &self,
        database: &str,
        branch: &str,
        collection: &str,
        expected: Option<&[u8]>,
        record: &[u8],
    ) -> Result<bool, EngineError> {
        self.lock_storage()?
            .port
            .schema_metadata_compare_exchange(database, branch, collection, expected, record)
            .map_err(|()| EngineError::StorageFailure)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::EngineConfig;
    use crate::types::{TenantId, TenantScope};

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
        assert!(
            engine.schema_metadata_compare_exchange("db", "main", "items", None, first).unwrap()
        );
        assert!(
            !engine.schema_metadata_compare_exchange("db", "main", "items", None, second).unwrap()
        );
        assert!(
            engine
                .schema_metadata_compare_exchange("db", "main", "items", Some(first), second)
                .unwrap()
        );
        assert!(
            !engine
                .schema_metadata_compare_exchange("db", "main", "items", Some(first), first)
                .unwrap()
        );
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
        assert!(engine.metering_fact(&scope, "db").is_none());
        engine.shutdown().expect("shutdown");
        std::fs::remove_dir_all(directory).expect("cleanup");
    }
}
