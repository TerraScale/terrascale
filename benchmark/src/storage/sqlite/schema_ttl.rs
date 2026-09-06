//! Durable TTL descriptor installation and resumable retained-row backfill.
use super::{SqliteStorage, decode_document_body, extract_expires_at};
use crate::types::CollectionName;
use rusqlite::{OptionalExtension, params};

impl SqliteStorage {
    pub(super) fn advance_ttl(
        &self,
        collection: &CollectionName,
        field: Option<&str>,
        batch: u32,
    ) -> Result<bool, ()> {
        if !(1..=1024).contains(&batch) {
            return Err(());
        }
        let mut writer = self.writer.lock().map_err(|_| ())?;
        let transaction = writer.transaction().map_err(|_| ())?;
        let current: Option<(Option<String>, String, String, i64, bool)> = transaction.query_row(
            "SELECT field, cursor_tenant, cursor_id, cursor_seq, complete FROM schema_ttl WHERE collection = ?1",
            [collection.as_str()], |row| Ok((row.get(0)?, row.get(1)?, row.get(2)?, row.get(3)?, row.get(4)?)),
        ).optional().map_err(|_| ())?;
        let (tenant, id, seq) = match current {
            Some((stored, tenant, id, seq, complete)) if stored.as_deref() == field => {
                if complete {
                    return Ok(true);
                }
                (tenant, id, seq)
            }
            _ => {
                transaction.execute("INSERT INTO schema_ttl(collection, field) VALUES (?1, ?2) ON CONFLICT(collection) DO UPDATE SET field = excluded.field, cursor_tenant = '', cursor_id = '', cursor_seq = -1, complete = 0", params![collection.as_str(), field]).map_err(|_| ())?;
                (String::new(), String::new(), -1)
            }
        };
        let rows = {
            let mut statement = transaction.prepare("SELECT tenant, id, commit_seq, body, deleted FROM documents WHERE collection = ?1 AND (tenant, id, commit_seq) > (?2, ?3, ?4) ORDER BY tenant, id, commit_seq LIMIT ?5").map_err(|_| ())?;
            statement
                .query_map(params![collection.as_str(), tenant, id, seq, batch], |row| {
                    Ok((
                        row.get::<_, String>(0)?,
                        row.get::<_, String>(1)?,
                        row.get::<_, i64>(2)?,
                        row.get::<_, Vec<u8>>(3)?,
                        row.get::<_, bool>(4)?,
                    ))
                })
                .map_err(|_| ())?
                .collect::<Result<Vec<_>, _>>()
                .map_err(|_| ())?
        };
        for (tenant, id, seq, body, deleted) in &rows {
            let expiry = if *deleted {
                None
            } else {
                extract_expires_at(&decode_document_body(body).ok_or(())?, field)
            };
            transaction.execute("UPDATE documents SET expires_at = ?5 WHERE collection = ?1 AND tenant = ?2 AND id = ?3 AND commit_seq = ?4", params![collection.as_str(), tenant, id, seq, expiry]).map_err(|_| ())?;
        }
        let complete = rows.len() < batch as usize;
        if let Some((tenant, id, seq, _, _)) = rows.last() {
            transaction.execute("UPDATE schema_ttl SET cursor_tenant = ?2, cursor_id = ?3, cursor_seq = ?4, complete = ?5 WHERE collection = ?1", params![collection.as_str(), tenant, id, seq, complete]).map_err(|_| ())?;
        } else {
            transaction
                .execute(
                    "UPDATE schema_ttl SET complete = 1 WHERE collection = ?1",
                    [collection.as_str()],
                )
                .map_err(|_| ())?;
        }
        transaction.commit().map_err(|_| ())?;
        Ok(complete)
    }
}
