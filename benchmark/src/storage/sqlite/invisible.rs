//! Hidden, transaction-maintained candidate tables with resumable key backfill.

use super::{SqliteStorage, durable_watermark, numeric_column, quote_identifier, text_column};
use crate::storage::{InvisibleColumnKind, InvisibleIndexColumn, InvisibleIndexProgress};
use crate::types::{CollectionName, TenantId};
use rusqlite::{Connection, OptionalExtension, params};

const MAX_COLUMNS: usize = 64;
const MAX_BATCH: u32 = 32;

pub(super) fn table_name(tenant: &str, id: &str) -> String {
    let mut hash = blake3::Hasher::new();
    hash.update(&(tenant.len() as u64).to_le_bytes());
    hash.update(tenant.as_bytes());
    hash.update(id.as_bytes());
    format!("hidden_{}", hash.finalize().to_hex())
}

fn literal(value: &str) -> String {
    format!("'{}'", value.replace('\'', "''"))
}

fn scalar_columns(columns: &[InvisibleIndexColumn]) -> Result<Vec<String>, ()> {
    if columns.is_empty() || columns.len() > MAX_COLUMNS {
        return Err(());
    }
    let mut names = std::collections::BTreeSet::new();
    columns
        .iter()
        .map(|column| {
            if column.field.is_empty()
                || column.field.len() > 1024
                || column.field.contains('\0')
                || !names.insert(&column.field)
            {
                return Err(());
            }
            Ok(quote_identifier(&match column.kind {
                InvisibleColumnKind::Text => text_column(&column.field),
                InvisibleColumnKind::Numeric => numeric_column(&column.field),
            }))
        })
        .collect()
}

struct Build {
    collection: String,
    columns: Vec<InvisibleIndexColumn>,
    progress: InvisibleIndexProgress,
    end: (String, i64),
    cursor: (String, i64),
}

fn encode_columns(columns: &[InvisibleIndexColumn]) -> Result<Vec<u8>, ()> {
    scalar_columns(columns)?;
    let mut bytes = vec![u8::try_from(columns.len()).map_err(|_| ())?];
    for column in columns {
        bytes.extend_from_slice(&u16::try_from(column.field.len()).map_err(|_| ())?.to_le_bytes());
        bytes.extend_from_slice(column.field.as_bytes());
        bytes.push(match column.kind {
            InvisibleColumnKind::Text => 1,
            InvisibleColumnKind::Numeric => 2,
        });
    }
    Ok(bytes)
}

fn decode_columns(bytes: &[u8]) -> Result<Vec<InvisibleIndexColumn>, ()> {
    let (&count, mut remaining) = bytes.split_first().ok_or(())?;
    if count == 0 || usize::from(count) > MAX_COLUMNS {
        return Err(());
    }
    let mut columns = Vec::new();
    for _ in 0..count {
        let len = usize::from(u16::from_le_bytes(
            remaining.get(..2).ok_or(())?.try_into().map_err(|_| ())?,
        ));
        if len == 0 || len > 1024 {
            return Err(());
        }
        let field =
            std::str::from_utf8(remaining.get(2..2 + len).ok_or(())?).map_err(|_| ())?.to_owned();
        let kind = match remaining.get(2 + len) {
            Some(1) => InvisibleColumnKind::Text,
            Some(2) => InvisibleColumnKind::Numeric,
            _ => return Err(()),
        };
        columns.push(InvisibleIndexColumn { field, kind });
        remaining = remaining.get(3 + len..).ok_or(())?;
    }
    if !remaining.is_empty() {
        return Err(());
    }
    scalar_columns(&columns)?;
    Ok(columns)
}

fn load(connection: &Connection, tenant: &str, id: &str) -> Result<Option<Build>, ()> {
    connection
        .query_row(
            "SELECT collection, CASE WHEN length(columns) <= 131072 THEN columns END,
         digest, target, end_id, end_seq, cursor_id, cursor_seq, complete
         FROM invisible_index_builds WHERE tenant = ?1 AND id = ?2",
            params![tenant, id],
            |row| {
                let bytes: Vec<u8> = row.get(1)?;
                let digest: Vec<u8> = row.get(2)?;
                if digest != blake3::hash(&bytes).as_bytes() {
                    return Err(rusqlite::Error::InvalidQuery);
                }
                let columns = decode_columns(&bytes).map_err(|_| rusqlite::Error::InvalidQuery)?;
                scalar_columns(&columns).map_err(|()| rusqlite::Error::InvalidQuery)?;
                Ok(Build {
                    collection: row.get(0)?,
                    columns,
                    progress: InvisibleIndexProgress {
                        target_sequence: u64::try_from(row.get::<_, i64>(3)?)
                            .map_err(|_| rusqlite::Error::InvalidQuery)?,
                        complete: row.get(8)?,
                    },
                    end: (row.get(4)?, row.get(5)?),
                    cursor: (row.get(6)?, row.get(7)?),
                })
            },
        )
        .optional()
        .map_err(|_| ())
}

/// Produces SQL only for a complete, correctly scoped hidden build. The
/// metadata is read inside the same transaction as both shadow executions.
pub(super) fn shadow_sql(
    connection: &Connection,
    tenant: &str,
    collection: &str,
    id: &str,
    candidate_scalar: &str,
    conditions: &[String],
) -> Result<String, ()> {
    if id.is_empty() || id.len() > 1024 {
        return Err(());
    }
    let build = load(connection, tenant, id)?.ok_or(())?;
    if !build.progress.complete || build.collection != collection {
        return Err(());
    }
    let scalars = scalar_columns(&build.columns)?;
    if scalars.first().map(String::as_str) != Some(candidate_scalar) {
        return Err(());
    }
    let mut predicates = conditions.to_vec();
    for predicate in &mut predicates {
        for (position, scalar) in scalars.iter().enumerate() {
            *predicate = predicate.replace(scalar, &format!("hidden.c{position}"));
        }
    }
    let name = table_name(tenant, id);
    let table = quote_identifier(&name);
    let lookup = quote_identifier(&format!("{name}_lookup"));
    Ok(super::filtered_page_sql(&predicates).replacen(
        "FROM documents AS current",
        &format!("FROM {table} AS hidden INDEXED BY {lookup}
            CROSS JOIN documents AS current ON current.id = hidden.id AND current.commit_seq = hidden.commit_seq"), 1,
    ))
}

impl SqliteStorage {
    pub(super) fn create_invisible_index(
        &mut self,
        tenant: &TenantId,
        collection: &CollectionName,
        id: &str,
        columns: &[InvisibleIndexColumn],
    ) -> Result<InvisibleIndexProgress, ()> {
        if id.is_empty() || id.len() > 1024 || id.contains('\0') {
            return Err(());
        }
        let scalars = scalar_columns(columns)?;
        let mut writer = self.writer.lock().map_err(|_| ())?;
        let tx = writer.transaction().map_err(|_| ())?;
        if let Some(build) = load(&tx, tenant.as_str(), id)? {
            if build.collection != collection.as_str() || build.columns != columns {
                return Err(());
            }
            return Ok(build.progress);
        }
        // Every trigger is considered on document writes, so this ceiling
        // bounds the whole database, including all tenants and collections.
        let count: u32 = tx
            .query_row("SELECT COUNT(*) FROM invisible_index_builds", [], |row| row.get(0))
            .map_err(|_| ())?;
        if count >= 32 {
            return Err(());
        }
        for column in columns {
            let ready: bool = tx.query_row(
                "SELECT EXISTS(SELECT 1 FROM index_descriptors WHERE collection = ?1 AND field = ?2 AND state = 1)",
                params![collection.as_str(), column.field], |row| row.get(0),
            ).map_err(|_| ())?;
            if !ready {
                return Err(());
            }
        }
        let target = durable_watermark(&tx)?;
        let end: Option<(String, i64)> = tx
            .query_row(
                "SELECT id, commit_seq FROM documents WHERE tenant = ?1 AND collection = ?2
             ORDER BY id DESC, commit_seq DESC LIMIT 1",
                params![tenant.as_str(), collection.as_str()],
                |row| Ok((row.get(0)?, row.get(1)?)),
            )
            .optional()
            .map_err(|_| ())?;
        let complete = end.is_none();
        let end = end.unwrap_or_default();
        let table = table_name(tenant.as_str(), id);
        create_physical(&tx, &table, tenant.as_str(), collection.as_str(), &scalars, columns)?;
        let bytes = encode_columns(columns)?;
        tx.execute(
            "INSERT INTO invisible_index_builds
             (tenant, id, collection, columns, digest, target, end_id, end_seq, complete)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9)",
            params![
                tenant.as_str(),
                id,
                collection.as_str(),
                bytes,
                blake3::hash(&bytes).as_bytes().as_slice(),
                i64::try_from(target).map_err(|_| ())?,
                end.0,
                end.1,
                complete
            ],
        )
        .map_err(|_| ())?;
        tx.commit().map_err(|_| ())?;
        Ok(InvisibleIndexProgress { target_sequence: target, complete })
    }

    pub(super) fn backfill_invisible_index(
        &mut self,
        tenant: &TenantId,
        id: &str,
        batch: u32,
    ) -> Result<InvisibleIndexProgress, ()> {
        if !(1..=MAX_BATCH).contains(&batch) {
            return Err(());
        }
        let mut writer = self.writer.lock().map_err(|_| ())?;
        let tx = writer.transaction().map_err(|_| ())?;
        let build = load(&tx, tenant.as_str(), id)?.ok_or(())?;
        if build.progress.complete {
            return Ok(build.progress);
        }
        let boundary = "tenant = ?1 AND collection = ?2 AND (id, commit_seq) > (?3, ?4)
                        AND (id, commit_seq) <= (?5, ?6)";
        let parameters = params![
            tenant.as_str(),
            build.collection,
            build.cursor.0,
            build.cursor.1,
            build.end.0,
            build.end.1,
            i64::from(batch) + 1
        ];
        let keys = tx
            .prepare(&format!(
                "SELECT id, commit_seq FROM documents WHERE {boundary}
            ORDER BY id, commit_seq LIMIT ?7"
            ))
            .map_err(|_| ())?
            .query_map(parameters, |row| Ok((row.get::<_, String>(0)?, row.get::<_, i64>(1)?)))
            .map_err(|_| ())?
            .collect::<Result<Vec<_>, _>>()
            .map_err(|_| ())?;
        let complete = keys.len() <= batch as usize;
        let count = keys.len().min(batch as usize);
        let cursor = keys.get(count.saturating_sub(1)).unwrap_or(&build.end);
        let table = quote_identifier(&table_name(tenant.as_str(), id));
        let scalars = scalar_columns(&build.columns)?.join(", ");
        let parameters = params![
            tenant.as_str(),
            build.collection,
            build.cursor.0,
            build.cursor.1,
            build.end.0,
            build.end.1,
            i64::from(batch)
        ];
        tx.execute(
            &format!(
                "INSERT OR REPLACE INTO {table}
            SELECT id, commit_seq, {scalars} FROM documents WHERE {boundary}
            ORDER BY id, commit_seq LIMIT ?7"
            ),
            parameters,
        )
        .map_err(|_| ())?;
        tx.execute(
            "UPDATE invisible_index_builds SET cursor_id = ?3, cursor_seq = ?4, complete = ?5
            WHERE tenant = ?1 AND id = ?2",
            params![tenant.as_str(), id, cursor.0, cursor.1, complete],
        )
        .map_err(|_| ())?;
        tx.commit().map_err(|_| ())?;
        Ok(InvisibleIndexProgress { complete, ..build.progress })
    }
}

fn create_physical(
    tx: &Connection,
    name: &str,
    tenant: &str,
    collection: &str,
    scalars: &[String],
    columns: &[InvisibleIndexColumn],
) -> Result<(), ()> {
    let table = quote_identifier(name);
    let definitions = columns
        .iter()
        .enumerate()
        .map(|(i, column)| {
            format!(
                "c{i} {}",
                match column.kind {
                    InvisibleColumnKind::Text => "TEXT",
                    InvisibleColumnKind::Numeric => "NUMERIC",
                }
            )
        })
        .collect::<Vec<_>>()
        .join(", ");
    let keys = (0..columns.len()).map(|i| format!("c{i}")).collect::<Vec<_>>().join(", ");
    tx.execute_batch(&format!(
        "CREATE TABLE {table} (id TEXT NOT NULL, commit_seq INTEGER NOT NULL,
        {definitions}, PRIMARY KEY(id, commit_seq)) WITHOUT ROWID;
        CREATE INDEX {} ON {table} ({keys}, id, commit_seq);",
        quote_identifier(&format!("{name}_lookup"))
    ))
    .map_err(|_| ())?;
    let scope =
        format!("NEW.tenant = {} AND NEW.collection = {}", literal(tenant), literal(collection));
    let values = scalars.iter().map(|name| format!("NEW.{name}")).collect::<Vec<_>>().join(", ");
    let updates = scalars
        .iter()
        .enumerate()
        .map(|(i, name)| format!("c{i} = NEW.{name}"))
        .collect::<Vec<_>>()
        .join(", ");
    tx.execute_batch(&format!(
        "CREATE TRIGGER {} AFTER INSERT ON documents WHEN {scope} BEGIN
           INSERT OR REPLACE INTO {table} VALUES (NEW.id, NEW.commit_seq, {values}); END;
         CREATE TRIGGER {} AFTER UPDATE OF {} ON documents WHEN {scope} BEGIN
           UPDATE {table} SET {updates} WHERE id = NEW.id AND commit_seq = NEW.commit_seq; END;
         CREATE TRIGGER {} AFTER DELETE ON documents
         WHEN OLD.tenant = {} AND OLD.collection = {} BEGIN
           DELETE FROM {table} WHERE id = OLD.id AND commit_seq = OLD.commit_seq; END;",
        quote_identifier(&format!("{name}_insert")),
        quote_identifier(&format!("{name}_update")),
        scalars.join(", "),
        quote_identifier(&format!("{name}_delete")),
        literal(tenant),
        literal(collection),
    ))
    .map_err(|_| ())
}
