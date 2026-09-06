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
}
