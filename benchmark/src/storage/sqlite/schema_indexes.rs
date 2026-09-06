//! Canonical compound indexes and Region-scoped live-head uniqueness.

use super::{SqliteStorage, numeric_column, quote_identifier, text_column, unix_millis};
use crate::types::{CollectionName, TenantId};
use rusqlite::{Connection, OptionalExtension, params};

fn physical_name(tenant: &str, collection: &str, name: &str) -> String {
    let mut digest = blake3::Hasher::new();
    for value in [tenant, collection, name] {
        digest.update(&(value.len() as u64).to_le_bytes());
        digest.update(value.as_bytes());
    }
    format!("schema_{}", digest.finalize().to_hex())
}

fn columns(fields: &[String]) -> Result<Vec<String>, ()> {
    let mut seen = std::collections::BTreeSet::new();
    if fields.is_empty() || fields.len() > 32 {
        return Err(());
    }
    fields.iter().map(|field| {
        if field.is_empty() || field.len() > 1024 || field.trim() != field
            || field.chars().any(char::is_control) || !seen.insert(field) {
            return Err(());
        }
        Ok(format!("{}, {}", quote_identifier(&text_column(field)), quote_identifier(&numeric_column(field))))
    }).collect()
}

fn duplicate_exists(
    connection: &Connection,
    tenant: &str,
    collection: &str,
    fields: &[String],
    sequence: Option<i64>,
    id: Option<&str>,
) -> Result<bool, ()> {
    columns(fields)?;
    let mut predicates = Vec::new();
    for field in fields {
        let text = quote_identifier(&text_column(field));
        let number = quote_identifier(&numeric_column(field));
        // Missing/null scalar keys do not participate, matching SQL UNIQUE.
        predicates.push(format!("(a.{text} IS NOT NULL OR a.{number} IS NOT NULL) AND a.{text} IS b.{text} AND a.{number} IS b.{number}"));
    }
    let sql = format!(
        "SELECT EXISTS(SELECT 1 FROM documents a JOIN documents b
         ON b.tenant = a.tenant AND b.collection = a.collection AND b.id != a.id
         WHERE a.tenant = ?1 AND a.collection = ?2
         AND (?4 IS NULL OR a.commit_seq = ?4)
         AND (?5 IS NULL OR a.id = ?5)
         AND a.deleted = 0 AND b.deleted = 0
         AND (a.expires_at IS NULL OR a.expires_at > ?3)
         AND (b.expires_at IS NULL OR b.expires_at > ?3)
         AND NOT EXISTS(SELECT 1 FROM documents n WHERE n.tenant = a.tenant AND n.collection = a.collection AND n.id = a.id AND n.commit_seq > a.commit_seq)
         AND NOT EXISTS(SELECT 1 FROM documents n WHERE n.tenant = b.tenant AND n.collection = b.collection AND n.id = b.id AND n.commit_seq > b.commit_seq)
         AND {})", predicates.join(" AND "));
    connection.query_row(&sql, params![tenant, collection, unix_millis(), sequence, id], |row| row.get(0)).map_err(|_| ())
}

/// Scans at most 32 retained rows per call and persists the resume key.
/// A changed journal watermark invalidates earlier validation: restart rather
/// than publishing a uniqueness guarantee against an obsolete image.
fn advance_unique_validation(
    connection: &Connection,
    tenant: &str,
    collection: &str,
    name: &str,
    fields: &[String],
) -> Result<bool, ()> {
    let watermark = i64::try_from(super::durable_watermark(connection)?).map_err(|_| ())?;
    let encoded = fields.join("\n");
    let saved: Option<(String, i64, String, i64)> = connection.query_row(
        "SELECT fields, watermark, cursor_id, cursor_seq FROM schema_index_builds WHERE tenant = ?1 AND collection = ?2 AND name = ?3",
        params![tenant, collection, name], |row| Ok((row.get(0)?, row.get(1)?, row.get(2)?, row.get(3)?)),
    ).optional().map_err(|_| ())?;
    let (cursor_id, cursor_seq) = match saved {
        Some((old_fields, old_watermark, id, sequence)) if old_fields == encoded && old_watermark == watermark => (id, sequence),
        _ => (String::new(), -1),
    };
    let rows = {
        let mut statement = connection.prepare(
            "SELECT id, commit_seq FROM documents WHERE tenant = ?1 AND collection = ?2 AND (id, commit_seq) > (?3, ?4) ORDER BY id, commit_seq LIMIT 33",
        ).map_err(|_| ())?;
        statement.query_map(params![tenant, collection, cursor_id, cursor_seq], |row| Ok((row.get::<_, String>(0)?, row.get::<_, i64>(1)?)))
            .map_err(|_| ())?.collect::<Result<Vec<_>, _>>().map_err(|_| ())?
    };
    for (id, sequence) in rows.iter().take(32) {
        if duplicate_exists(connection, tenant, collection, fields, Some(*sequence), Some(id))? {
            return Err(());
        }
    }
    if rows.len() <= 32 {
        return Ok(true);
    }
    let (id, sequence) = &rows[31];
    connection.execute(
        "INSERT INTO schema_index_builds (tenant, collection, name, fields, watermark, cursor_id, cursor_seq) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7) ON CONFLICT (tenant, collection, name) DO UPDATE SET fields = excluded.fields, watermark = excluded.watermark, cursor_id = excluded.cursor_id, cursor_seq = excluded.cursor_seq",
        params![tenant, collection, name, encoded, watermark, id, sequence],
    ).map_err(|_| ())?;
    Ok(false)
}

impl SqliteStorage {
    pub(super) fn register_canonical_index(
        &mut self,
        tenant: &TenantId,
        collection: &CollectionName,
        name: &str,
        fields: &[String],
        unique: bool,
    ) -> Result<bool, ()> {
        let expressions = columns(fields)?;
        if name.is_empty() || name.len() > 1024 || name.chars().any(char::is_control) {
            return Err(());
        }
        for field in fields {
            self.register_descriptor(collection, field).map_err(|_| ())?;
        }
        let state = self.extracted.lock().map_err(|_| ())?;
        if fields.iter().any(|field| !state.is_active(collection.as_str(), field)) {
            return Ok(false);
        }
        drop(state);
        let mut writer = self.writer.lock().map_err(|_| ())?;
        let transaction = writer.transaction_with_behavior(rusqlite::TransactionBehavior::Immediate).map_err(|_| ())?;
        let existing: Option<(String, bool)> = transaction.query_row(
            "SELECT fields, is_unique FROM schema_indexes WHERE tenant = ?1 AND collection = ?2 AND name = ?3",
            params![tenant.as_str(), collection.as_str(), name], |row| Ok((row.get(0)?, row.get(1)?)),
        ).optional().map_err(|_| ())?;
        let encoded = fields.join("\n");
        if existing.as_ref().is_some_and(|current| current == &(encoded.clone(), unique)) {
            return Ok(true);
        }
        if unique && !advance_unique_validation(&transaction, tenant.as_str(), collection.as_str(), name, fields)? {
            transaction.commit().map_err(|_| ())?;
            return Ok(false);
        }
        let physical = physical_name(tenant.as_str(), collection.as_str(), name);
        transaction.execute_batch(&format!("DROP INDEX IF EXISTS {}", quote_identifier(&physical))).map_err(|_| ())?;
        transaction.execute_batch(&format!("CREATE INDEX {} ON documents (tenant, collection, {}, id, commit_seq)", quote_identifier(&physical), expressions.join(", "))).map_err(|_| ())?;
        transaction.execute("INSERT INTO schema_indexes (tenant, collection, name, fields, is_unique) VALUES (?1, ?2, ?3, ?4, ?5) ON CONFLICT (tenant, collection, name) DO UPDATE SET fields = excluded.fields, is_unique = excluded.is_unique", params![tenant.as_str(), collection.as_str(), name, encoded, unique]).map_err(|_| ())?;
        transaction.execute("DELETE FROM schema_index_builds WHERE tenant = ?1 AND collection = ?2 AND name = ?3", params![tenant.as_str(), collection.as_str(), name]).map_err(|_| ())?;
        transaction.commit().map_err(|_| ())?;
        Ok(true)
    }

    pub(super) fn remove_canonical_index(&mut self, tenant: &TenantId, collection: &CollectionName, name: &str) -> Result<(), ()> {
        let mut writer = self.writer.lock().map_err(|_| ())?;
        let transaction = writer.transaction_with_behavior(rusqlite::TransactionBehavior::Immediate).map_err(|_| ())?;
        transaction.execute_batch(&format!("DROP INDEX IF EXISTS {}", quote_identifier(&physical_name(tenant.as_str(), collection.as_str(), name)))).map_err(|_| ())?;
        transaction.execute("DELETE FROM schema_indexes WHERE tenant = ?1 AND collection = ?2 AND name = ?3", params![tenant.as_str(), collection.as_str(), name]).map_err(|_| ())?;
        transaction.execute("DELETE FROM schema_index_builds WHERE tenant = ?1 AND collection = ?2 AND name = ?3", params![tenant.as_str(), collection.as_str(), name]).map_err(|_| ())?;
        transaction.commit().map_err(|_| ())
    }
}

/// Checks the final transaction image, so key swaps and delete/reuse are atomic.
/// Replicated commits bypass this local admission constraint at the call site.
pub(super) fn validate_local_writes(connection: &Connection, tenant: &TenantId, sequence: u64) -> Result<bool, ()> {
    let mut statement = connection.prepare("SELECT collection, fields FROM schema_indexes WHERE tenant = ?1 AND is_unique = 1").map_err(|_| ())?;
    let definitions = statement.query_map([tenant.as_str()], |row| Ok((row.get::<_, String>(0)?, row.get::<_, String>(1)?))).map_err(|_| ())?;
    for definition in definitions {
        let (collection, fields) = definition.map_err(|_| ())?;
        let fields = fields.split('\n').map(str::to_owned).collect::<Vec<_>>();
        if duplicate_exists(connection, tenant.as_str(), &collection, &fields, Some(i64::try_from(sequence).map_err(|_| ())?), None)? {
            return Ok(false);
        }
    }
    Ok(true)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn uniqueness_uses_live_heads_and_complete_compound_keys() {
        let db = Connection::open_in_memory().unwrap();
        db.execute_batch("CREATE TABLE documents (tenant TEXT, collection TEXT, id TEXT, commit_seq INTEGER, deleted INTEGER DEFAULT 0, expires_at INTEGER, \"f:a#t\" TEXT, \"f:a#n\" NUMERIC, \"f:b#t\" TEXT, \"f:b#n\" NUMERIC);
            INSERT INTO documents (tenant,collection,id,commit_seq,\"f:a#t\",\"f:b#n\") VALUES ('t','c','one',1,'x',1), ('t','c','two',2,'x',2), ('other','c','one',3,'x',1);").unwrap();
        let fields = vec!["a".to_owned(), "b".to_owned()];
        assert!(!duplicate_exists(&db, "t", "c", &fields, None, None).unwrap());
        db.execute_batch("INSERT INTO documents (tenant,collection,id,commit_seq,\"f:a#t\",\"f:b#n\") VALUES ('t','c','two',4,'x',1)").unwrap();
        assert!(duplicate_exists(&db, "t", "c", &fields, Some(4), None).unwrap());
        db.execute_batch("INSERT INTO documents (tenant,collection,id,commit_seq,deleted) VALUES ('t','c','one',5,1)").unwrap();
        assert!(!duplicate_exists(&db, "t", "c", &fields, None, None).unwrap());
        db.execute_batch("INSERT INTO documents (tenant,collection,id,commit_seq,\"f:a#t\",\"f:b#n\",expires_at) VALUES ('t','c','one',6,'x',1,0)").unwrap();
        assert!(!duplicate_exists(&db, "t", "c", &fields, None, None).unwrap());
        // Reusing a document's own key never conflicts with its retained history.
        db.execute_batch("INSERT INTO documents (tenant,collection,id,commit_seq,\"f:a#t\",\"f:b#n\") VALUES ('t','c','two',7,'x',1)").unwrap();
        assert!(!duplicate_exists(&db, "t", "c", &fields, Some(7), None).unwrap());
    }

    #[test]
    fn local_write_validation_uses_final_transaction_image() {
        let db = Connection::open_in_memory().unwrap();
        db.execute_batch("CREATE TABLE schema_indexes (tenant TEXT, collection TEXT, fields TEXT, is_unique INTEGER);
            INSERT INTO schema_indexes VALUES ('t','c','a',1);
            CREATE TABLE documents (tenant TEXT, collection TEXT, id TEXT, commit_seq INTEGER, deleted INTEGER DEFAULT 0, expires_at INTEGER, \"f:a#t\" TEXT, \"f:a#n\" NUMERIC);
            INSERT INTO documents (tenant,collection,id,commit_seq,\"f:a#t\") VALUES ('t','c','one',1,'x'), ('t','c','two',2,'y'), ('t','c','one',3,'y'), ('t','c','two',3,'x');").unwrap();
        let tenant = TenantId::try_new("t").unwrap();
        assert!(validate_local_writes(&db, &tenant, 3).unwrap());
        db.execute_batch("INSERT INTO documents (tenant,collection,id,commit_seq,\"f:a#t\") VALUES ('t','c','three',4,'x')").unwrap();
        assert!(!validate_local_writes(&db, &tenant, 4).unwrap());
    }
}
