//! Tenant-scoped durable reads for resumable exports.

use super::{Engine, EngineError, MAX_SNAPSHOT_RECORD_PAGE, SnapshotRecord};
use crate::types::{CollectionName, DocumentId, LocalDurability, StorageSequence, TenantScope};

/// Metadata captured while document publications and schema mutations are excluded.
#[derive(Clone, Debug)]
pub struct ExportMetadataSnapshot {
    /// Fixed document and journal boundary.
    pub boundary: u64,
    /// Wall-clock instant used for all TTL decisions.
    pub captured_at_millis: u64,
    /// Earliest retained publication at capture, if any.
    pub history_start: Option<u64>,
    /// Durable extracted-field index descriptors.
    pub indexes: Vec<crate::storage::DescriptorListing>,
    /// Collection names and internal schema envelopes. The server must decode
    /// their public document and omit internal idempotency data.
    pub schemas: Vec<(String, Vec<u8>)>,
}

/// One retained revision, bounded independently of its transaction size.
#[derive(Clone, Debug, PartialEq)]
pub struct ExportHistoryRow {
    /// Global publication position.
    pub sequence: u64,
    /// Publication operation identifier.
    pub operation_id: String,
    /// Achieved publication durability.
    pub durability: LocalDurability,
    /// Collection key.
    pub collection: String,
    /// Document key.
    pub id: String,
    /// Published or deleted document version.
    pub version: u64,
    /// Whether the revision removes the key.
    pub deleted: bool,
    /// Canonical persisted JSON envelope for a live revision.
    pub body: Vec<u8>,
}

impl Engine {
    /// Captures durable schema envelopes and index descriptors with a fixed
    /// document boundary. The authenticated adapter supplies database and branch.
    /// Metadata is limited to 256 schemas, 256 descriptors, and four MiB of
    /// envelope and descriptor-key bytes.
    ///
    /// # Errors
    /// Rejects foreign scopes, unavailable metadata, and metadata above the cap.
    pub fn capture_export_metadata(
        &self,
        scope: &TenantScope,
        database: &str,
        branch: &str,
    ) -> Result<ExportMetadataSnapshot, EngineError> {
        self.validate_scope(scope)?;
        let storage = self.lock_storage()?;
        let connection =
            storage.port.open_sqlite_read_connection().map_err(|()| EngineError::StorageFailure)?;
        let mut statement = connection.prepare(
            "SELECT collection, length(envelope), envelope FROM schema_metadata WHERE database_id = ?1 AND branch = ?2 ORDER BY collection LIMIT 257"
        ).map_err(|_| EngineError::StorageFailure)?;
        let mut rows = statement
            .query(rusqlite::params![database, branch])
            .map_err(|_| EngineError::StorageFailure)?;
        let mut schemas = Vec::new();
        let mut bytes = 0_usize;
        while let Some(row) = rows.next().map_err(|_| EngineError::StorageFailure)? {
            let length: i64 = row.get(1).map_err(|_| EngineError::StorageFailure)?;
            let length =
                usize::try_from(length).map_err(|_| EngineError::SnapshotPageLimitExceeded)?;
            bytes = bytes.checked_add(length).ok_or(EngineError::SnapshotPageLimitExceeded)?;
            if schemas.len() == 256 || bytes > 4 * 1024 * 1024 {
                return Err(EngineError::SnapshotPageLimitExceeded);
            }
            schemas.push((
                row.get(0).map_err(|_| EngineError::StorageFailure)?,
                row.get(2).map_err(|_| EngineError::StorageFailure)?,
            ));
        }
        let mut statement = connection.prepare(
            "SELECT collection, field, state, backfill_below, backfill_cursor, length(CAST(collection AS BLOB)) + length(CAST(field AS BLOB)) FROM index_descriptors ORDER BY collection, field LIMIT 257"
        ).map_err(|_| EngineError::StorageFailure)?;
        let mut rows = statement.query([]).map_err(|_| EngineError::StorageFailure)?;
        let mut indexes = Vec::new();
        while let Some(row) = rows.next().map_err(|_| EngineError::StorageFailure)? {
            let length = usize::try_from(read_u64(row, 5)?)
                .map_err(|_| EngineError::SnapshotPageLimitExceeded)?;
            bytes = bytes.checked_add(length).ok_or(EngineError::SnapshotPageLimitExceeded)?;
            if indexes.len() == 256 || bytes > 4 * 1024 * 1024 {
                return Err(EngineError::SnapshotPageLimitExceeded);
            }
            let state: i64 = row.get(2).map_err(|_| EngineError::StorageFailure)?;
            indexes.push(crate::storage::DescriptorListing {
                collection: row.get(0).map_err(|_| EngineError::StorageFailure)?,
                field: row.get(1).map_err(|_| EngineError::StorageFailure)?,
                active: state == 1,
                backfill_remaining: read_u64(row, 3)?.saturating_sub(read_u64(row, 4)?),
            });
        }
        Ok(ExportMetadataSnapshot {
            boundary: storage.port.published_state().0.get(),
            captured_at_millis: u64::try_from(
                std::time::SystemTime::now()
                    .duration_since(std::time::UNIX_EPOCH)
                    .map_err(|_| EngineError::StorageFailure)?
                    .as_millis(),
            )
            .map_err(|_| EngineError::StorageFailure)?,
            history_start: storage
                .port
                .servable_history_start()
                .map_err(|()| EngineError::StorageFailure)?
                .map(StorageSequence::get),
            indexes,
            schemas,
        })
    }

    /// Reads at most 256 final-state documents at a persisted export boundary.
    /// The full collection and identifier cursor is exclusive. Callers must
    /// also bound encoded response bytes before sending the records.
    ///
    /// # Errors
    /// Rejects foreign scopes, oversized pages, unavailable boundaries, and
    /// storage failures. Retention never silently substitutes current data.
    pub fn export_snapshot_records(
        &self,
        scope: &TenantScope,
        boundary: u64,
        captured_at_millis: u64,
        after: Option<&(CollectionName, DocumentId)>,
        limit: u32,
    ) -> Result<Vec<SnapshotRecord>, EngineError> {
        self.validate_scope(scope)?;
        if limit > MAX_SNAPSHOT_RECORD_PAGE {
            return Err(EngineError::SnapshotPageLimitExceeded);
        }
        let storage = self.lock_storage()?;
        validate_export_boundary(&storage, boundary)?;
        let connection =
            storage.port.open_sqlite_read_connection().map_err(|()| EngineError::StorageFailure)?;
        let mut statement = connection.prepare(
            "SELECT collection, id, requested, length(body), body FROM (SELECT collection, id, requested, body, deleted, expires_at, MAX(commit_seq) FROM documents WHERE tenant = ?1 AND commit_seq <= ?2 AND (collection, id) > (?3, ?4) GROUP BY collection, id) WHERE deleted = 0 AND (collection = 'terrabase.families' OR expires_at IS NULL OR expires_at > ?5) ORDER BY collection, id LIMIT ?6"
        ).map_err(|_| EngineError::StorageFailure)?;
        let mut rows = statement
            .query(rusqlite::params![
                scope.tenant().as_str(),
                i64::try_from(boundary).map_err(|_| EngineError::StorageFailure)?,
                after.as_ref().map_or("", |key| key.0.as_str()),
                after.as_ref().map_or("", |key| key.1.as_str()),
                i64::try_from(captured_at_millis).map_err(|_| EngineError::StorageFailure)?,
                limit
            ])
            .map_err(|_| EngineError::StorageFailure)?;
        let mut result = Vec::new();
        let mut bytes = 0_u64;
        while let Some(row) = rows.next().map_err(|_| EngineError::StorageFailure)? {
            let length = read_u64(row, 3)?;
            if length > 4 * 1024 * 1024 {
                return Err(EngineError::SnapshotPageLimitExceeded);
            }
            if bytes + length > 4 * 1024 * 1024 {
                break;
            }
            bytes += length;
            let body: Vec<u8> = row.get(4).map_err(|_| EngineError::StorageFailure)?;
            let document =
                crate::storage::decode_document_body(&body).ok_or(EngineError::StorageFailure)?;
            let collection: String = row.get(0).map_err(|_| EngineError::StorageFailure)?;
            let id: String = row.get(1).map_err(|_| EngineError::StorageFailure)?;
            if document.collection().as_str() != collection || document.id().as_str() != id {
                return Err(EngineError::StorageFailure);
            }
            let durability = match row.get::<_, u8>(2).map_err(|_| EngineError::StorageFailure)? {
                0 => LocalDurability::Buffered,
                1 => LocalDurability::Written,
                2 => LocalDurability::Synced,
                _ => return Err(EngineError::StorageFailure),
            };
            result.push(SnapshotRecord { document, durability });
        }
        Ok(result)
    }

    /// Reads retained revisions in exclusive `(sequence, collection, id)` order.
    /// Each page is capped at 256 rows and four MiB of document bodies, even
    /// when a single transaction published more rows. A row above four MiB
    /// is refused before its body is copied into the page.
    ///
    /// # Errors
    /// Rejects foreign scopes, expired history, unavailable boundaries,
    /// oversized rows or limits, and storage failures.
    pub fn export_history_rows(
        &self,
        scope: &TenantScope,
        boundary: u64,
        after: Option<(u64, String, String)>,
        limit: u32,
    ) -> Result<Vec<ExportHistoryRow>, EngineError> {
        use rusqlite::OptionalExtension as _;
        self.validate_scope(scope)?;
        if limit > MAX_SNAPSHOT_RECORD_PAGE {
            return Err(EngineError::SnapshotPageLimitExceeded);
        }
        let storage = self.lock_storage()?;
        validate_export_boundary(&storage, boundary)?;
        let connection =
            storage.port.open_sqlite_read_connection().map_err(|()| EngineError::StorageFailure)?;
        let transaction =
            connection.unchecked_transaction().map_err(|_| EngineError::StorageFailure)?;
        let cursor = after.unwrap_or_default();
        let pruned: Option<i64> = transaction
            .query_row(
                "SELECT pruned_through FROM journal_pruning WHERE tenant = ?1",
                [scope.tenant().as_str()],
                |row| row.get(0),
            )
            .optional()
            .map_err(|_| EngineError::StorageFailure)?;
        // An in-commit cursor still needs its own sequence's retained rows.
        if pruned.is_some_and(|pruned| {
            cursor.0 < u64::try_from(pruned).unwrap_or(u64::MAX)
                || (cursor.0 == u64::try_from(pruned).unwrap_or(u64::MAX) && !cursor.1.is_empty())
        }) {
            return Err(EngineError::HistoryExpired);
        }
        let mut statement = transaction.prepare(
            "SELECT commit_seq, commit_ulid, collection, id, version, deleted, requested, length(body), body FROM documents WHERE tenant = ?1 AND (commit_seq, collection, id) > (?2, ?3, ?4) AND commit_seq <= ?5 ORDER BY commit_seq, collection, id LIMIT ?6"
        ).map_err(|_| EngineError::StorageFailure)?;
        let mut rows = statement
            .query(rusqlite::params![
                scope.tenant().as_str(),
                i64::try_from(cursor.0).map_err(|_| EngineError::StorageFailure)?,
                cursor.1,
                cursor.2,
                i64::try_from(boundary).map_err(|_| EngineError::StorageFailure)?,
                limit
            ])
            .map_err(|_| EngineError::StorageFailure)?;
        let mut result = Vec::new();
        let mut bytes = 0_i64;
        while let Some(row) = rows.next().map_err(|_| EngineError::StorageFailure)? {
            let length: i64 = row.get(7).map_err(|_| EngineError::StorageFailure)?;
            if length > 4 * 1024 * 1024 {
                return Err(EngineError::SnapshotPageLimitExceeded);
            }
            if bytes + length > 4 * 1024 * 1024 {
                break;
            }
            bytes += length;
            let requested: u8 = row.get(6).map_err(|_| EngineError::StorageFailure)?;
            let durability = match requested {
                0 => LocalDurability::Buffered,
                1 => LocalDurability::Written,
                2 => LocalDurability::Synced,
                _ => return Err(EngineError::StorageFailure),
            };
            let deleted: u8 = row.get(5).map_err(|_| EngineError::StorageFailure)?;
            if deleted > 1 {
                return Err(EngineError::StorageFailure);
            }
            result.push(ExportHistoryRow {
                sequence: read_u64(row, 0)?,
                operation_id: row.get(1).map_err(|_| EngineError::StorageFailure)?,
                collection: row.get(2).map_err(|_| EngineError::StorageFailure)?,
                id: row.get(3).map_err(|_| EngineError::StorageFailure)?,
                version: read_u64(row, 4)?,
                deleted: deleted == 1,
                durability,
                body: row.get(8).map_err(|_| EngineError::StorageFailure)?,
            });
        }
        Ok(result)
    }
}

fn read_u64(row: &rusqlite::Row<'_>, index: usize) -> Result<u64, EngineError> {
    let value: i64 = row.get(index).map_err(|_| EngineError::StorageFailure)?;
    u64::try_from(value).map_err(|_| EngineError::StorageFailure)
}

fn validate_export_boundary(
    storage: &super::StorageState,
    boundary: u64,
) -> Result<(), EngineError> {
    if boundary > storage.port.published_state().0.get()
        || !storage.port.snapshot_boundary_available(StorageSequence::new(boundary))
        || storage
            .port
            .servable_history_start()
            .map_err(|()| EngineError::StorageFailure)?
            .is_some_and(|start| boundary < start.get())
    {
        return Err(EngineError::SnapshotBoundaryUnavailable);
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{
        engine::EngineConfig,
        types::{Document, DocumentValue, ExpectedVersion, OperationId, TenantId},
    };

    fn verify_export_ttl(engine: &Engine, scope: &TenantScope) {
        let snapshot = engine.read_snapshot(scope).expect("snapshot");
        let mut transaction = engine
            .begin(scope, snapshot, OperationId::try_new("ttl").expect("operation"))
            .expect("transaction");
        transaction
            .put(
                Document::new(
                    DocumentId::try_new("expiring").expect("id"),
                    CollectionName::try_new("ttl").expect("collection"),
                    DocumentValue::Object(std::collections::BTreeMap::from([(
                        "expires_at".to_owned(),
                        DocumentValue::Integer(1),
                    )])),
                ),
                ExpectedVersion::New,
            )
            .expect("put");
        assert!(matches!(
            transaction.commit(LocalDurability::Synced),
            super::super::CommitOutcome::Committed(_)
        ));
        assert_eq!(
            engine.export_snapshot_records(scope, 4, 0, None, 256).expect("before expiry").len(),
            5
        );
        assert_eq!(
            engine.export_snapshot_records(scope, 4, 2, None, 256).expect("after expiry").len(),
            4
        );
    }

    #[test]
    fn export_boundary_excludes_later_writes_and_rejects_foreign_scopes() {
        let directory =
            std::env::temp_dir().join(format!("terrabase-export-seam-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&directory);
        let engine =
            Engine::open(EngineConfig::durable(&directory).expect("config")).expect("engine");
        let tenant = TenantId::try_new("export-test").expect("tenant");
        let scope = engine.scope_for(&tenant);
        let collection = CollectionName::try_new("items").expect("collection");
        for id in ["a", "b"] {
            let snapshot = engine.read_snapshot(&scope).expect("snapshot");
            let mut tx = engine
                .begin(&scope, snapshot, OperationId::try_new(id).expect("operation"))
                .expect("transaction");
            tx.put(
                Document::new(
                    DocumentId::try_new(id).expect("id"),
                    collection.clone(),
                    DocumentValue::String(id.to_owned()),
                ),
                ExpectedVersion::New,
            )
            .expect("put");
            assert!(matches!(
                tx.commit(LocalDurability::Synced),
                super::super::CommitOutcome::Committed(_)
            ));
        }
        let boundary = 1;
        let records = engine.export_snapshot_records(&scope, boundary, 0, None, 1).expect("page");
        assert_eq!(records[0].document().id().as_str(), "a");
        assert!(
            engine
                .export_snapshot_records(
                    &scope,
                    boundary,
                    0,
                    Some(&(collection, records[0].document().id().clone())),
                    1
                )
                .expect("last page")
                .is_empty()
        );
        let history = engine.export_history_rows(&scope, boundary, None, 256).expect("history");
        assert_eq!(history.len(), 1);
        assert_eq!(history[0].sequence, boundary);
        let foreign = TenantScope::from_parts(tenant, engine.identity() + 1);
        assert!(matches!(
            engine.export_snapshot_records(&foreign, boundary, 0, None, 1),
            Err(EngineError::ScopeMismatch)
        ));
        assert!(matches!(
            engine.export_history_rows(&scope, boundary, None, 257),
            Err(EngineError::SnapshotPageLimitExceeded)
        ));
        let snapshot = engine.read_snapshot(&scope).expect("snapshot");
        let mut transaction = engine
            .begin(&scope, snapshot, OperationId::try_new("multi").expect("operation"))
            .expect("transaction");
        for id in ["c", "d"] {
            transaction
                .put(
                    Document::new(
                        DocumentId::try_new(id).expect("id"),
                        CollectionName::try_new("items").expect("collection"),
                        DocumentValue::String(id.to_owned()),
                    ),
                    ExpectedVersion::New,
                )
                .expect("put");
        }
        assert!(matches!(
            transaction.commit(LocalDurability::Synced),
            super::super::CommitOutcome::Committed(_)
        ));
        let first = engine
            .export_history_rows(&scope, 3, Some((2, "items".to_owned(), "b".to_owned())), 1)
            .expect("first revision");
        assert_eq!(first[0].id, "c");
        let second = engine
            .export_history_rows(
                &scope,
                3,
                Some((first[0].sequence, first[0].collection.clone(), first[0].id.clone())),
                1,
            )
            .expect("second revision");
        assert_eq!(second[0].id, "d");
        assert_eq!(second[0].sequence, first[0].sequence);
        verify_export_ttl(&engine, &scope);
        engine.shutdown().expect("shutdown");
        std::fs::remove_dir_all(directory).expect("cleanup");
    }
}
