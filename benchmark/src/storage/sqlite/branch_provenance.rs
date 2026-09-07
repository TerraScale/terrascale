//! A branch retains the source cut, but never inherits the source's receipts or bills.

use crate::storage::DatabaseOwner;
use rusqlite::{Transaction, params};

fn imported_identity(owner: &DatabaseOwner, tenant: &str, operation: &str) -> String {
    let mut hash = blake3::Hasher::new();
    hash.update(b"terrabase.branch-snapshot-receipt.v1");
    hash.update(owner.database().to_text().as_bytes());
    hash.update(&(tenant.len() as u64).to_le_bytes());
    hash.update(tenant.as_bytes());
    hash.update(operation.as_bytes());
    format!("branch-snapshot:{}", hash.finalize().to_hex())
}

/// Called on the unpublished child in the same transaction as schema and owner
/// rebinding. Digests and roots describe the frozen source cut, not a replay of
/// its writes. Imported receipts are explicitly marked and bill zero writes;
/// retained document bytes begin accruing storage at child initialization.
pub(crate) fn transform_provenance(
    transaction: &Transaction<'_>,
    child_owner: &DatabaseOwner,
) -> Result<u64, ()> {
    super::validate_operation_facts(transaction)?;
    let cut = super::durable_watermark(transaction)?;
    let root: String = transaction
        .query_row("SELECT value FROM meta WHERE key='root_head'", [], |row| row.get(0))
        .map_err(|_| ())?;
    let baseline = format!("branch:{}:{cut}:{root}", child_owner.database().to_text());
    transaction.execute("INSERT INTO meta(key,value) VALUES('branch_snapshot_baseline',?1) ON CONFLICT(key) DO UPDATE SET value=excluded.value", [baseline]).map_err(|_| ())?;
    // Metadata-only and conflict-losing publications also advance causal
    // progress. Materialize their prefix before retiring the source outbox.
    let tenants = {
        let mut statement = transaction
            .prepare(
                "SELECT tenant FROM documents UNION SELECT tenant FROM origin_prefixes
             UNION SELECT tenant FROM replication_publications",
            )
            .map_err(|_| ())?;
        statement
            .query_map([], |row| row.get::<_, String>(0))
            .map_err(|_| ())?
            .collect::<Result<Vec<_>, _>>()
            .map_err(|_| ())?
    };
    for tenant in tenants {
        for entry in super::origin_prefixes_on(transaction, &tenant)? {
            transaction
                .execute(
                    "INSERT INTO origin_prefixes(tenant,origin,seq,hlc) VALUES(?1,?2,?3,?4)
                 ON CONFLICT(tenant,origin) DO UPDATE SET seq=excluded.seq,hlc=excluded.hlc",
                    params![
                        tenant,
                        entry.origin,
                        i64::try_from(entry.origin_sequence).map_err(|_| ())?,
                        i64::from_le_bytes(entry.hlc.to_le_bytes())
                    ],
                )
                .map_err(|_| ())?;
        }
    }
    // Include conflict-only identities, which need not have retained rows.
    let identities = {
        let mut statement = transaction
            .prepare(
                "SELECT tenant,client_request_ulid FROM operation_facts WHERE committed=1
             UNION ALL SELECT tenant,commit_ulid FROM documents
             UNION ALL SELECT tenant,loser_commit_ulid FROM merge_conflicts
             UNION ALL SELECT tenant,winner_commit_ulid FROM merge_conflicts",
            )
            .map_err(|_| ())?;
        statement
            .query_map([], |row| Ok((row.get::<_, String>(0)?, row.get::<_, String>(1)?)))
            .map_err(|_| ())?
            .collect::<Result<Vec<_>, _>>()
            .map_err(|_| ())?
    };
    transaction
        .execute_batch(
            "DELETE FROM branch_snapshot_receipts; DELETE FROM operation_facts WHERE committed=0;",
        )
        .map_err(|_| ())?;
    // The WITHOUT ROWID primary key already enforces this identity. Remove
    // the legacy duplicate index before rewriting the operation identities.
    transaction
        .execute_batch("DROP INDEX IF EXISTS operation_facts_client_request_ulid")
        .map_err(|_| ())?;
    transaction
        .execute_batch(
            "CREATE TEMP TABLE branch_identity_map(
                 tenant TEXT NOT NULL,
                 original TEXT NOT NULL,
                 replacement TEXT NOT NULL,
                 PRIMARY KEY (tenant, original)
             ) WITHOUT ROWID",
        )
        .map_err(|_| ())?;
    {
        let mut insert = transaction
            .prepare_cached(
                "INSERT OR IGNORE INTO branch_identity_map(tenant,original,replacement) VALUES(?1,?2,?3)",
            )
            .map_err(|_| ())?;
        for (tenant, original) in identities {
            let replacement = imported_identity(child_owner, &tenant, &original);
            insert.execute(params![tenant, original, replacement]).map_err(|_| ())?;
        }
    }
    // Keep request and commit replacements independent because a document may
    // reference different source operations. Preserve unmapped legacy request
    // identities while avoiding the redundant third EXISTS probe.
    transaction
        .execute(
            "UPDATE documents
             SET client_request_ulid = COALESCE(
                     (SELECT replacement FROM branch_identity_map
                      WHERE tenant=documents.tenant AND original=documents.client_request_ulid),
                     client_request_ulid),
                 commit_ulid = COALESCE(
                     (SELECT replacement FROM branch_identity_map
                      WHERE tenant=documents.tenant AND original=documents.commit_ulid),
                     commit_ulid)",
            [],
        )
        .map_err(|_| ())?;
    transaction
        .execute(
            "UPDATE merge_conflicts
             SET loser_commit_ulid = COALESCE(
                     (SELECT replacement FROM branch_identity_map
                      WHERE tenant=merge_conflicts.tenant AND original=merge_conflicts.loser_commit_ulid),
                     loser_commit_ulid),
                 winner_commit_ulid = COALESCE(
                     (SELECT replacement FROM branch_identity_map
                      WHERE tenant=merge_conflicts.tenant AND original=merge_conflicts.winner_commit_ulid),
                     winner_commit_ulid)
             WHERE EXISTS (SELECT 1 FROM branch_identity_map
                           WHERE tenant=merge_conflicts.tenant
                             AND (original=merge_conflicts.loser_commit_ulid
                               OR original=merge_conflicts.winner_commit_ulid))",
            [],
        )
        .map_err(|_| ())?;
    transaction
        .execute(
            "DELETE FROM operation_kind_markers
             WHERE EXISTS (SELECT 1 FROM branch_identity_map
                           WHERE tenant=operation_kind_markers.tenant
                             AND original=operation_kind_markers.operation)",
            [],
        )
        .map_err(|_| ())?;
    transaction
        .execute(
            "UPDATE operation_facts
             SET client_request_ulid = (SELECT replacement FROM branch_identity_map
                                        WHERE tenant=operation_facts.tenant
                                          AND original=operation_facts.client_request_ulid),
                 writes=0, bytes=0
             WHERE EXISTS (SELECT 1 FROM branch_identity_map
                           WHERE tenant=operation_facts.tenant
                             AND original=operation_facts.client_request_ulid)",
            [],
        )
        .map_err(|_| ())?;
    transaction
        .execute(
            "INSERT INTO branch_snapshot_receipts(tenant,operation)
             SELECT fact.tenant, fact.client_request_ulid
             FROM operation_facts AS fact
             WHERE fact.committed=1",
            [],
        )
        .map_err(|_| ())?;
    transaction.execute_batch("DROP TABLE branch_identity_map").map_err(|_| ())?;
    // Standalone usage facts may own the highest sequence without a document
    // or operation receipt. Preserve that allocation even after removing bills.
    if cut != 0 {
        let tenant = child_owner.tenant().to_text();
        let operation = format!("branch-cut:{}", child_owner.database().to_text());
        transaction.execute(
            "INSERT INTO operation_facts(tenant,client_request_ulid,digest,committed,sequence,requested,root,writes,bytes)
             SELECT ?1,?2,0,1,?3,1,value,0,0 FROM meta WHERE key='root_head'",
            params![tenant, operation, i64::try_from(cut).map_err(|_| ())?],
        ).map_err(|_| ())?;
        transaction
            .execute(
                "INSERT INTO branch_snapshot_receipts VALUES(?1,?2)",
                params![tenant, operation],
            )
            .map_err(|_| ())?;
    }
    transaction
        .execute_batch(
            "DELETE FROM rejections;
         DELETE FROM replication_bindings;
         DELETE FROM replication_superseded;
         DELETE FROM replication_publication_records;
         DELETE FROM replication_publications;
         DELETE FROM consumer_frontiers;
         DELETE FROM repair_identities;
         DELETE FROM repair_origins;
         DELETE FROM repair_origin_versions;
         DELETE FROM repair_summary_versions;
         DELETE FROM webhook_metadata;
         DELETE FROM database_lifecycle;
         DELETE FROM usage_placement;
         DELETE FROM usage_facts;
         DELETE FROM usage_stream_seals;
         DELETE FROM storage_usage_closures;
         DELETE FROM metering_db_facts;
         DELETE FROM usage_counters;
         DELETE FROM storage_accounting_revisions;
         DELETE FROM storage_accounting_events;
         DELETE FROM storage_accounting_gauges;
         DELETE FROM storage_accounting_stops;
         DELETE FROM storage_accounting_coverage;
         DELETE FROM storage_accounting_clock;
         DELETE FROM meta WHERE key IN ('origin_authority_epoch','local_origin');",
        )
        .map_err(|_| ())?;
    transaction
        .execute(
            "INSERT INTO usage_counters VALUES('ops',?1,0)",
            [i64::try_from(cut).map_err(|_| ())?],
        )
        .map_err(|_| ())?;
    super::accounting::initialize_at(
        transaction,
        u64::try_from(super::unix_millis()).map_err(|_| ())?,
    )?;
    transaction
        .execute(
            "UPDATE repair_history_floor SET local_seq=?1 WHERE singleton=1",
            [i64::try_from(cut).map_err(|_| ())?],
        )
        .map_err(|_| ())?;
    super::reconcile_metering_db_facts(transaction)?;
    super::validate_operation_facts(transaction)?;
    Ok(cut)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn snapshot_receipts_preserve_history_and_restart_accounting() {
        let mut db = rusqlite::Connection::open_in_memory().unwrap();
        db.execute_batch(super::super::SCHEMA).unwrap();
        super::super::repair_summary::initialize(&db).unwrap();
        super::super::storage_closure::initialize(&db).unwrap();
        super::super::accounting::initialize_at(&db, 1).unwrap();
        db.execute_batch("INSERT INTO meta VALUES('root_head','source-root');
            INSERT INTO documents(tenant,collection,id,commit_seq,commit_ulid,revision,requested,digest,version,deleted,body,origin,origin_seq,hlc,client_request_ulid)
            VALUES('tenant','notes','a',1,'parent-one','revision-one',1,7,1,0,X'0102','GRU',1,1,'parent-one'),
                  ('tenant','notes','a',2,'parent-two','revision-two',1,9,2,0,X'030405','GRU',2,2,'parent-two');
            INSERT INTO operation_facts(tenant,client_request_ulid,digest,committed,sequence,root,writes,bytes)
            VALUES('tenant','parent-one',7,1,1,'first-root',1,2),('tenant','parent-two',9,1,2,'source-root',1,3);
            INSERT INTO replication_publications VALUES('tenant','GRU',3,3,'metadata-only',3,3,X'',X'');
            INSERT INTO operation_facts(tenant,client_request_ulid,digest,committed,sequence,root,writes,bytes)
            VALUES('tenant','metadata-only',11,1,3,'source-root',1,0);").unwrap();
        // Restored snapshots can still contain the retired index.
        db.execute_batch(
            "CREATE UNIQUE INDEX operation_facts_client_request_ulid
             ON operation_facts(tenant, client_request_ulid)
             WHERE client_request_ulid <> ''",
        )
        .unwrap();
        let transaction = db.transaction().unwrap();
        assert_eq!(transform_provenance(&transaction, &DatabaseOwner::local_development()), Ok(3));
        transaction.commit().unwrap();
        assert_eq!(
            db.query_row(
                "SELECT COUNT(*) FROM sqlite_master
                 WHERE type='index' AND name='operation_facts_client_request_ulid'",
                [],
                |row| row.get::<_, i64>(0),
            )
            .unwrap(),
            0
        );
        // These are the same reconciliation paths storage open runs.
        super::super::validate_operation_facts(&db).unwrap();
        super::super::reconcile_metering_db_facts(&db).unwrap();
        super::super::reconcile_usage_counters(&db).unwrap();
        assert_eq!(
            db.query_row("SELECT COUNT(*) FROM documents", [], |r| r.get::<_, i64>(0)).unwrap(),
            2
        );
        assert_eq!(
            db.query_row("SELECT SUM(length(body)) FROM documents", [], |r| r.get::<_, i64>(0))
                .unwrap(),
            5
        );
        assert_eq!(
            db.query_row("SELECT SUM(writes) FROM metering_db_facts", [], |r| r.get::<_, i64>(0))
                .unwrap(),
            0
        );
        assert_eq!(
            db.query_row(
                "SELECT seq FROM origin_prefixes WHERE tenant='tenant' AND origin='GRU'",
                [],
                |r| r.get::<_, i64>(0)
            )
            .unwrap(),
            3
        );
        assert_eq!(db.query_row("SELECT COUNT(*) FROM operation_facts WHERE client_request_ulid IN ('parent-one','parent-two','metadata-only')", [], |r| r.get::<_, i64>(0)).unwrap(), 0);
        assert_eq!(
            db.query_row("SELECT SUM(bytes) FROM storage_accounting_revisions", [], |r| r
                .get::<_, i64>(0))
                .unwrap(),
            5
        );
        assert_eq!(
            db.query_row("SELECT value FROM meta WHERE key='root_head'", [], |r| r
                .get::<_, String>(0))
                .unwrap(),
            "source-root"
        );
        assert_eq!(super::super::durable_watermark(&db), Ok(3));
    }

    #[test]
    fn client_chosen_snapshot_prefix_does_not_exempt_zero_write_receipt() {
        let db = rusqlite::Connection::open_in_memory().unwrap();
        db.execute_batch(super::super::SCHEMA).unwrap();
        db.execute_batch("INSERT INTO operation_facts(tenant,client_request_ulid,digest,committed,sequence,root,writes,bytes)
            VALUES('tenant','branch-snapshot:forged',7,1,1,'root',0,0);").unwrap();
        assert_eq!(super::super::validate_operation_facts(&db), Err(()));
    }
}
