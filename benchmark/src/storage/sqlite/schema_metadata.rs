//! Opaque schema envelopes in the member's existing SQLite file.

use rusqlite::{OptionalExtension, params};

use super::SqliteStorage;

impl SqliteStorage {
    pub(super) fn set_collection_history(
        &mut self,
        collection: &crate::types::CollectionName,
        days: u32,
    ) -> Result<(), ()> {
        if !(1..=30).contains(&days) {
            return Err(());
        }
        let days = i64::from(days);
        let writer = self.writer.lock().map_err(|_| ())?;
        writer
            .execute(
                "INSERT INTO collection_history (collection, days) VALUES (?1, ?2)
                 ON CONFLICT(collection) DO UPDATE SET days = excluded.days",
                rusqlite::params![collection.as_str(), days],
            )
            .map(|_| ())
            .map_err(|_| ())
    }

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
