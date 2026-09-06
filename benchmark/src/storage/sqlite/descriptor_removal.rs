//! Atomic extracted descriptor retirement, preserving shared and hidden users.

use rusqlite::{TransactionBehavior, params};

use super::{
    MAX_DESCRIPTOR_FIELD_BYTES, SqliteStorage, numeric_column, quote_identifier, text_column,
};
use crate::storage::IndexDescriptorRemovalError;
use crate::types::CollectionName;

impl SqliteStorage {
    pub(super) fn remove_descriptor(
        &mut self,
        collection: &CollectionName,
        field: &str,
    ) -> Result<bool, IndexDescriptorRemovalError> {
        use IndexDescriptorRemovalError::{InUse, InvalidField, Storage};
        if field.is_empty()
            || field.len() > MAX_DESCRIPTOR_FIELD_BYTES
            || field.chars().any(char::is_control)
            || field != field.trim()
        {
            return Err(InvalidField);
        }
        let mut writer = self.writer.lock().map_err(|_| Storage)?;
        let tx = writer
            .transaction_with_behavior(TransactionBehavior::Immediate)
            .map_err(|_| Storage)?;
        let exists: bool = tx.query_row(
            "SELECT EXISTS(SELECT 1 FROM index_descriptors WHERE collection = ?1 AND field = ?2)",
            params![collection.as_str(), field], |row| row.get(0),
        ).map_err(|_| Storage)?;
        if !exists {
            return Ok(false);
        }
        let canonical_fields = {
            let mut statement = tx.prepare("SELECT fields FROM schema_indexes WHERE collection = ?1")
                .map_err(|_| Storage)?;
            statement.query_map([collection.as_str()], |row| row.get::<_, String>(0))
                .map_err(|_| Storage)?
                .collect::<Result<Vec<_>, _>>().map_err(|_| Storage)?
        };
        if canonical_fields.iter().any(|fields| fields.split('\n').any(|member| member == field)) {
            return Err(InUse);
        }
        if super::invisible::uses_descriptor(&tx, collection.as_str(), field)
            .map_err(|()| Storage)?
        {
            return Err(InUse);
        }
        tx.execute(
            "DELETE FROM index_descriptors WHERE collection = ?1 AND field = ?2",
            params![collection.as_str(), field],
        )
        .map_err(|_| Storage)?;
        let shared: bool = tx
            .query_row(
                "SELECT EXISTS(SELECT 1 FROM index_descriptors WHERE field = ?1)",
                [field],
                |row| row.get(0),
            )
            .map_err(|_| Storage)?;
        if !shared {
            tx.execute_batch(&format!(
                "DROP INDEX IF EXISTS {text_index}; DROP INDEX IF EXISTS {number_index};
                 ALTER TABLE documents DROP COLUMN {text};
                 ALTER TABLE documents DROP COLUMN {number};",
                text_index = quote_identifier(&format!("i:{field}#t")),
                number_index = quote_identifier(&format!("i:{field}#n")),
                text = quote_identifier(&text_column(field)),
                number = quote_identifier(&numeric_column(field)),
            ))
            .map_err(|_| Storage)?;
        }
        // Hold the cache lock before commit: failure to obtain it must not
        // leave a committed deletion paired with the old write statement.
        let mut extracted = self.extracted.lock().map_err(|_| Storage)?;
        tx.commit().map_err(|_| Storage)?;
        extracted.descriptors.remove(&(collection.as_str().to_owned(), field.to_owned()));
        if !shared {
            extracted.fields.retain(|existing| existing != field);
            extracted.refresh_put_sql();
        }
        writer.flush_prepared_statement_cache();
        Ok(true)
    }
}
