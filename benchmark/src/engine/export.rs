//! Tenant-scoped durable reads for resumable exports.

use super::{Engine, EngineError, MAX_SNAPSHOT_RECORD_PAGE, SnapshotRecord};
use crate::types::{CollectionName, DocumentId, LocalDurability, StorageSequence, TenantScope};

/// Metadata captured while document publications and schema mutations are excluded.
#[derive(Clone, Debug)]
pub struct ExportMetadataSnapshot {
    /// Fixed document and journal boundary.
    pub boundary: u64,
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

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{
        engine::EngineConfig,
        types::{Document, DocumentValue, ExpectedVersion, OperationId, TenantId},
    };

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
        let records = engine.export_snapshot_records(&scope, boundary, None, 1).expect("page");
        assert_eq!(records[0].document().id().as_str(), "a");
        assert!(
            engine
                .export_snapshot_records(
                    &scope,
                    boundary,
                    Some((collection, records[0].document().id().clone())),
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
            engine.export_snapshot_records(&foreign, boundary, None, 1),
            Err(EngineError::ScopeMismatch)
        ));
        assert!(matches!(
            engine.export_history_rows(&scope, boundary, None, 257),
            Err(EngineError::SnapshotPageLimitExceeded)
        ));
    }
}

impl Engine {
    /// Captures durable schema envelopes and index descriptors with a fixed
    /// document boundary. The authenticated adapter supplies database and branch.
    /// Metadata is limited to 256 schemas and four MiB of envelope bytes.
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
            let length: u64 = row.get(1).map_err(|_| EngineError::StorageFailure)?;
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
        Ok(ExportMetadataSnapshot {
            boundary: storage.port.published_state().0.get(),
            indexes: storage.port.index_descriptors().map_err(|()| EngineError::StorageFailure)?,
            schemas,
        })
    }

    /// Captures a publication boundary and durable index descriptors under
    /// the same storage lock. Persist both before beginning a resumable export.
    /// Schema and branch catalogs require their own durable catalog readers.
    ///
    /// # Errors
    /// Rejects foreign scopes and failures to read durable metadata.
    pub fn capture_export_boundary(
        &self,
        scope: &TenantScope,
    ) -> Result<(u64, Vec<crate::storage::DescriptorListing>), EngineError> {
        self.validate_scope(scope)?;
        let storage = self.lock_storage()?;
        let boundary = storage.port.published_state().0;
        let descriptors =
            storage.port.index_descriptors().map_err(|()| EngineError::StorageFailure)?;
        Ok((boundary.get(), descriptors))
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
        after: Option<(CollectionName, DocumentId)>,
        limit: u32,
    ) -> Result<Vec<SnapshotRecord>, EngineError> {
        self.validate_scope(scope)?;
        self.snapshot_records_from(
            scope.tenant().clone(),
            StorageSequence::new(boundary),
            after,
            limit,
        )
    }

    /// Returns the first retained global publication position.
    ///
    /// # Errors
    /// Rejects foreign scopes and storage failures.
    pub fn export_history_start(&self, scope: &TenantScope) -> Result<Option<u64>, EngineError> {
        self.validate_scope(scope)?;
        self.servable_history_start().map(|start| start.map(StorageSequence::get))
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
        self.snapshot_records_from(
            scope.tenant().clone(),
            StorageSequence::new(boundary),
            None,
            0,
        )?;
        let storage = self.lock_storage()?;
        let connection =
            storage.port.open_sqlite_read_connection().map_err(|()| EngineError::StorageFailure)?;
        let transaction =
            connection.unchecked_transaction().map_err(|_| EngineError::StorageFailure)?;
        let cursor = after.unwrap_or_default();
        let pruned: Option<u64> = transaction
            .query_row(
                "SELECT pruned_through FROM journal_pruning WHERE tenant = ?1",
                [scope.tenant().as_str()],
                |row| row.get(0),
            )
            .optional()
            .map_err(|_| EngineError::StorageFailure)?;
        // An in-commit cursor still needs its own sequence's retained rows.
        if pruned
            .is_some_and(|pruned| cursor.0 < pruned || (cursor.0 == pruned && !cursor.1.is_empty()))
        {
            return Err(EngineError::HistoryExpired);
        }
        let mut statement = transaction.prepare(
            "SELECT commit_seq, commit_ulid, collection, id, version, deleted, requested, length(body), body FROM documents WHERE tenant = ?1 AND (commit_seq, collection, id) > (?2, ?3, ?4) AND commit_seq <= ?5 ORDER BY commit_seq, collection, id LIMIT ?6"
        ).map_err(|_| EngineError::StorageFailure)?;
        let mut rows = statement
            .query(rusqlite::params![
                scope.tenant().as_str(),
                cursor.0,
                cursor.1,
                cursor.2,
                boundary,
                limit
            ])
            .map_err(|_| EngineError::StorageFailure)?;
        let mut result = Vec::new();
        let mut bytes = 0_u64;
        while let Some(row) = rows.next().map_err(|_| EngineError::StorageFailure)? {
            let length: u64 = row.get(7).map_err(|_| EngineError::StorageFailure)?;
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
                sequence: row.get(0).map_err(|_| EngineError::StorageFailure)?,
                operation_id: row.get(1).map_err(|_| EngineError::StorageFailure)?,
                collection: row.get(2).map_err(|_| EngineError::StorageFailure)?,
                id: row.get(3).map_err(|_| EngineError::StorageFailure)?,
                version: row.get(4).map_err(|_| EngineError::StorageFailure)?,
                deleted: deleted == 1,
                durability,
                body: row.get(8).map_err(|_| EngineError::StorageFailure)?,
            });
        }
        Ok(result)
    }
}
