//! Opaque schema envelopes in the member's existing SQLite file.

use rusqlite::{OptionalExtension, params};

use super::SqliteStorage;

impl SqliteStorage {
    pub(super) fn load_schema_metadata(
        &self,
        database: &str,
        branch: &str,
        collection: &str,
    ) -> Result<Option<Vec<u8>>, ()> {
        let reader = self.reader();
        reader.connection().query_row(
            "SELECT envelope FROM schema_metadata WHERE database_id = ?1 AND branch = ?2 AND collection = ?3",
            params![database, branch, collection],
            |row| row.get(0),
        ).optional().map_err(|_| ())
    }

    pub(super) fn compare_exchange_schema_metadata(
        &self,
        database: &str,
        branch: &str,
        collection: &str,
        expected: Option<&[u8]>,
        record: &[u8],
    ) -> Result<bool, ()> {
        let writer = self.writer.lock().map_err(|_| ())?;
        // Each conditional mutation is one SQLite transaction. Comparing the
        // BLOB itself includes idempotency and migration state in the CAS.
        let changed = match expected {
            None => writer.execute(
                "INSERT INTO schema_metadata (database_id, branch, collection, envelope) VALUES (?1, ?2, ?3, ?4) ON CONFLICT DO NOTHING",
                params![database, branch, collection, record],
            ),
            Some(previous) => writer.execute(
                "UPDATE schema_metadata SET envelope = ?4 WHERE database_id = ?1 AND branch = ?2 AND collection = ?3 AND envelope = ?5",
                params![database, branch, collection, record, previous],
            ),
        }.map_err(|_| ())?;
        Ok(changed == 1)
    }
}
