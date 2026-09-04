//! Qualification cases for durable journal-row range reconciliation.

use terrabase::engine::CommitOutcome;
use terrabase::types::{
    CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion, LocalDurability,
    OperationId, TenantId,
};
use terrabase_replication_core::anti_entropy::{
    JournalRowId, RepairError, RowDifference, RowSummary, row_hash,
};
use terrabase_tbep::anti_entropy::{
    DurableJournalRow, JournalRowSource, reconcile_journal_rows, repair_once_sources_bounded,
};
use terrabase_tbep::engine_host::EngineHost;
use terrabase_tbep::host::HostingError;
use terrabase_tbep::registry::BoundScope;
use terrabase_tbep::replication::replicate_once;

fn row(origin: &str, sequence: u64, bytes: &[u8]) -> DurableJournalRow {
    DurableJournalRow::new(JournalRowId::new(origin, sequence).unwrap(), row_hash(bytes))
}

fn scope() -> BoundScope {
    BoundScope {
        principal: "spiffe://local/anti-entropy-test".into(),
        tenant: "tenant-a".into(),
        project: "project-a".into(),
        database: "database-a".into(),
    }
}

struct FailingJournal;

impl JournalRowSource for FailingJournal {
    fn durable_journal_rows(
        &self,
        _scope: &BoundScope,
    ) -> Result<Vec<DurableJournalRow>, HostingError> {
        Err(HostingError::EngineFailure("injected journal read failure".into()))
    }
}

#[test]
fn injected_journal_read_failure_never_converges_as_empty() {
    let source = FailingJournal;
    let target = FailingJournal;
    let result = repair_once_sources_bounded(&source, &target, &scope(), 1);
    assert_eq!(result, Err(HostingError::EngineFailure("injected journal read failure".into())));
}

#[test]
fn engine_source_exports_real_rows_and_tombstones() {
    let location =
        std::env::temp_dir().join(format!("terrabase-anti-entropy-source-{}", std::process::id()));
    let host = EngineHost::open(&location, 1, "tenant-a", "project-a", "database-a")
        .expect("the host opens");
    let tenant = TenantId::try_new("tenant-a").unwrap();
    let engine_scope = host.engine().scope_for(&tenant);
    let (operation, value) = ("source-put", 1_i64);
    let snapshot = host.engine().read_snapshot(&engine_scope).unwrap();
    let mut transaction = host
        .engine()
        .begin(&engine_scope, snapshot, OperationId::try_new(operation).unwrap())
        .unwrap();
    transaction
        .put(
            Document::new(
                DocumentId::try_new("doc-1").unwrap(),
                CollectionName::try_new("orders").unwrap(),
                DocumentValue::Integer(value),
            ),
            ExpectedVersion::Any,
        )
        .unwrap();
    assert!(matches!(transaction.commit(LocalDurability::Synced), CommitOutcome::Committed(_)));
    let snapshot = host.engine().read_snapshot(&engine_scope).unwrap();
    let mut transaction = host
        .engine()
        .begin(&engine_scope, snapshot, OperationId::try_new("source-delete").unwrap())
        .unwrap();
    transaction.delete(DocumentId::try_new("doc-1").unwrap(), ExpectedVersion::Any).unwrap();
    assert!(matches!(transaction.commit(LocalDurability::Synced), CommitOutcome::Committed(_)));

    let rows = host.durable_journal_rows(&scope()).unwrap();
    assert_eq!(rows.len(), 2);
    assert_eq!(rows[0].id.origin(), rows[1].id.origin());
    assert_eq!(rows[0].id.sequence(), 1);
    assert_eq!(rows[1].id.sequence(), 2);
    assert_ne!(rows[0].row_hash, rows[1].row_hash);
    drop(host);
    let _ = std::fs::remove_dir_all(location);
}

#[test]
fn replicated_source_identity_survives_restart_and_reconciles() {
    let suffix =
        format!("{}-{}", std::process::id(), std::thread::current().name().unwrap_or("test"));
    let source_location =
        std::env::temp_dir().join(format!("terrabase-anti-entropy-source-{suffix}"));
    let target_location =
        std::env::temp_dir().join(format!("terrabase-anti-entropy-target-{suffix}"));
    let _ = std::fs::remove_dir_all(&source_location);
    let _ = std::fs::remove_dir_all(&target_location);
    let source = EngineHost::open(&source_location, 1, "tenant-a", "project-a", "database-a")
        .expect("the source opens");
    let target = EngineHost::open(&target_location, 2, "tenant-a", "project-a", "database-a")
        .expect("the target opens");
    let tenant = TenantId::try_new("tenant-a").unwrap();
    let engine_scope = source.engine().scope_for(&tenant);
    let snapshot = source.engine().read_snapshot(&engine_scope).unwrap();
    let mut transaction = source
        .engine()
        .begin(&engine_scope, snapshot, OperationId::try_new("source-row").unwrap())
        .unwrap();
    transaction
        .put(
            Document::new(
                DocumentId::try_new("doc-1").unwrap(),
                CollectionName::try_new("orders").unwrap(),
                DocumentValue::Integer(7),
            ),
            ExpectedVersion::Any,
        )
        .unwrap();
    assert!(matches!(transaction.commit(LocalDurability::Synced), CommitOutcome::Committed(_)));

    let mut cursor = None;
    assert_eq!(replicate_once(&source, &target, &scope(), &mut cursor).unwrap(), 1);
    let source_rows = source.durable_journal_rows(&scope()).unwrap();
    let target_rows = target.durable_journal_rows(&scope()).unwrap();
    assert_eq!(source_rows, target_rows);
    assert_eq!(source_rows[0].id.sequence(), 1);
    assert!(!source_rows[0].id.origin().is_empty());

    drop(target);
    let reopened = EngineHost::open(&target_location, 2, "tenant-a", "project-a", "database-a")
        .expect("the target reopens");
    let reopened_rows = reopened.durable_journal_rows(&scope()).unwrap();
    assert_eq!(reopened_rows, source_rows);
    assert!(reconcile_journal_rows(&source_rows, &reopened_rows, 8).unwrap().is_converged());
    drop(reopened);
    drop(source);
    let _ = std::fs::remove_dir_all(&source_location);
    let _ = std::fs::remove_dir_all(&target_location);
}

#[test]
fn stable_journal_identity_is_used_verbatim() {
    let local = [row("gru", 41, b"same")];
    let peer = [row("gru", 41, b"same")];
    assert!(reconcile_journal_rows(&local, &peer, 1).unwrap().is_converged());

    let peer = [row("gru", 42, b"same")];
    let plan = reconcile_journal_rows(&local, &peer, 2).unwrap();
    assert_eq!(plan.differences().len(), 2);
    assert!(plan.differences().iter().any(|difference| {
        matches!(difference, RowDifference::MissingRemote(row) if row.id.sequence() == 41)
    }));
    assert!(plan.differences().iter().any(|difference| {
        matches!(difference, RowDifference::MissingLocal(row) if row.id.sequence() == 42)
    }));
}

#[test]
fn absence_and_tombstone_are_not_reconciled_as_equal() {
    let tombstone = row("gru", 7, b"tombstone");
    let plan = reconcile_journal_rows(&[], std::slice::from_ref(&tombstone), 1).unwrap();
    assert_eq!(
        plan.differences(),
        &[RowDifference::MissingLocal(RowSummary::new(tombstone.id.clone(), tombstone.row_hash,))]
    );
}

#[test]
fn equal_tombstones_are_a_present_durable_row() {
    let tombstone = row("gru", 7, b"tombstone");
    let plan = reconcile_journal_rows(
        std::slice::from_ref(&tombstone),
        std::slice::from_ref(&tombstone),
        1,
    )
    .unwrap();
    assert!(plan.is_converged());
}

#[test]
fn duplicate_durable_identity_fails_closed() {
    let rows = [row("gru", 1, b"first"), row("gru", 1, b"second")];
    assert_eq!(reconcile_journal_rows(&rows, &[], 1), Err(RepairError::DuplicateRowIdentity));
}
