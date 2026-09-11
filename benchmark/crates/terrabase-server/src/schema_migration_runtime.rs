//! Member-owned schema migration polling and restart recovery.

use super::{
    ApiState, Arc, CollectionName, LocalDurability, OperationId, database_records, document_host,
    document_scope,
};
use crate::schema_api::{
    DurableSchemaRecord, MigrationState, SchemaJson, canonical_schema_document,
};
use serde::{Deserialize, Serialize};
use terrabase::families::SchemaVersion;
use terrabase::families::migrations::{
    self, FieldTransform, MigrationPlan, MigrationStatus, MigrationStep, PlanOutcome,
    SchemaMigration, TransformCounts,
};

/// Original dry-run evidence. Persisted before `begin`, so a restart never
/// recomputes the source chain from documents already transformed by a page.
#[derive(Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
struct SavedPlan {
    /// The public schema generation that admitted this migration.
    generation: u64,
    manifest: String,
    documents: u64,
    source_digest: String,
    result_digest: String,
    added: u64,
    removed: u64,
    renamed: u64,
    retyped: u64,
}

impl SavedPlan {
    fn capture(generation: u64, plan: &MigrationPlan) -> Result<Vec<u8>, String> {
        let PlanOutcome::Ready { documents, source_digest, result_digest, transform_counts } =
            plan.outcome()
        else {
            return Err(
                "stored documents do not satisfy the migration target; repair them before retrying"
                    .into(),
            );
        };
        serde_json::to_vec(&Self {
            generation,
            manifest: plan.manifest().to_owned(),
            documents: *documents,
            source_digest: source_digest.clone(),
            result_digest: result_digest.clone(),
            added: transform_counts.added,
            removed: transform_counts.removed,
            renamed: transform_counts.renamed,
            retyped: transform_counts.retyped,
        })
        .map_err(|_| "migration evidence encoding failed".into())
    }

    fn restore(
        bytes: &[u8],
        generation: u64,
        contract: &SchemaMigration,
    ) -> Result<MigrationPlan, String> {
        let saved: Self = serde_json::from_slice(bytes)
            .map_err(|_| "saved migration evidence is malformed".to_owned())?;
        if saved.generation != generation {
            return Err("saved migration evidence belongs to another schema generation".into());
        }
        MigrationPlan::restore_ready(
            contract,
            &saved.manifest,
            saved.documents,
            saved.source_digest,
            saved.result_digest,
            TransformCounts {
                added: saved.added,
                removed: saved.removed,
                renamed: saved.renamed,
                retyped: saved.retyped,
            },
        )
        .map_err(|_| "saved migration evidence does not match this contract".into())
    }
}

pub(super) fn contract(record: &DurableSchemaRecord) -> Result<SchemaMigration, String> {
    let previous = record
        .previous_schema
        .as_ref()
        .ok_or_else(|| "rename migration has no previous schema".to_owned())?;
    let from = previous
        .version
        .and_then(|version| SchemaVersion::try_new(version).ok())
        .ok_or_else(|| "previous schema version is invalid".to_owned())?;
    let to = SchemaVersion::try_new(record.document.schema_version)
        .map_err(|_| "target schema version is invalid".to_owned())?;
    let canonical = canonical_schema_document(&SchemaJson {
        collections: vec![record.document.schema.clone()],
        version: None,
    })
    .map_err(|_| "target schema is invalid".to_owned())?;
    let target = canonical
        .collections()
        .first()
        .ok_or_else(|| "target schema is empty".to_owned())?
        .validation_schema(to)
        .map_err(|_| "target validation schema is invalid".to_owned())?;
    let collection = CollectionName::try_new(&record.document.target.collection)
        .map_err(|_| "migration collection is invalid".to_owned())?;
    let transforms = record
        .document
        .schema
        .renames
        .iter()
        .map(|rename| FieldTransform::RenameField {
            from: rename.from.clone(),
            to: rename.to.clone(),
        })
        .collect();
    SchemaMigration::try_new(collection, from, to, target, transforms)
        .map_err(|_| "migration contract is invalid".into())
}

fn persist(
    engine: &terrabase::Engine,
    expected: &[u8],
    record: &DurableSchemaRecord,
) -> Result<bool, String> {
    let bytes =
        serde_json::to_vec(record).map_err(|_| "schema status encoding failed".to_owned())?;
    persist_encoded(engine, expected, record, &bytes, false)
}

pub(super) fn persist_with_rollback(
    engine: &terrabase::Engine,
    expected: &[u8],
    record: &DurableSchemaRecord,
    begin_rollback: bool,
) -> Result<bool, String> {
    let bytes =
        serde_json::to_vec(record).map_err(|_| "schema status encoding failed".to_owned())?;
    persist_encoded(engine, expected, record, &bytes, begin_rollback)
}

fn persist_encoded(
    engine: &terrabase::Engine,
    expected: &[u8],
    record: &DurableSchemaRecord,
    bytes: &[u8],
    begin_rollback: bool,
) -> Result<bool, String> {
    let target = &record.document.target;
    let scope = document_scope(engine).map_err(|()| "database scope is unavailable".to_owned())?;
    let snapshot =
        engine.read_snapshot(&scope).map_err(|_| "schema status snapshot failed".to_owned())?;
    let operation =
        OperationId::try_new(&format!("schema-progress-{}", terrabase::identity::generate()))
            .map_err(|_| "schema status operation identity failed".to_owned())?;
    let mut transaction = engine
        .begin(&scope, snapshot, operation.clone())
        .map_err(|_| "schema status transaction failed".to_owned())?;
    if begin_rollback {
        migrations::stage_rollback(&mut transaction, &contract(record)?)
            .map_err(|error| format!("rollback admission refused: {error:?}"))?;
    }
    transaction
        .stage_schema_bundle(
            &target.collection,
            record.document.generation,
            crate::schema_api::schema_bundle_identity(record)
                .map_err(|_| "schema bundle identity encoding failed".to_owned())?,
        )
        .map_err(|_| "schema bundle staging failed".to_owned())?;
    transaction
        .stage_schema_metadata(
            &target.database,
            &target.branch,
            &target.collection,
            Some(expected),
            bytes,
        )
        .map_err(|_| "schema status staging failed".to_owned())?;
    match transaction.commit(LocalDurability::Synced) {
        terrabase::CommitOutcome::Committed(_) => Ok(true),
        terrabase::CommitOutcome::Rejected(terrabase::CommitError::Conflict) => Ok(false),
        terrabase::CommitOutcome::Unknown(_) => match engine.resolve_commit(&scope, operation) {
            Ok(terrabase::DurableResolution::Committed(_)) => Ok(true),
            _ => Err("schema status commit outcome is unresolved".into()),
        },
        terrabase::CommitOutcome::Rejected(_) => Err("schema status persistence failed".into()),
    }
}

/// Advances at most one document transformation page. Dry-run and final
/// evidence verification use the core coordinator's snapshot scans.
fn advance(engine: &terrabase::Engine, record: &mut DurableSchemaRecord) -> Result<bool, String> {
    if record.rollback.is_some() && record.previous_schema.is_some() {
        return super::schema_migration_recovery::advance(engine, record);
    }
    let has_renames =
        record.document.migration.as_ref().is_some_and(|migration| {
            migration.steps.iter().any(|step| step.kind == "field_rename")
        });
    if has_renames {
        let contract = contract(record)?;
        let scope =
            document_scope(engine).map_err(|()| "database scope is unavailable".to_owned())?;
        let Some(bytes) = record.migration_plan.as_deref() else {
            let plan = migrations::dry_run(engine, &scope, &contract)
                .map_err(|error| format!("migration dry run stopped: {error:?}"))?;
            record.migration_plan = Some(SavedPlan::capture(record.document.generation, &plan)?);
            if let Some(migration) = record.document.migration.as_mut() {
                if let PlanOutcome::Ready { documents, .. } = plan.outcome() {
                    migration.total = *documents;
                }
                migration.message = "migration evidence saved; document backfill is queued".into();
            }
            // The caller commits the plan before any core migration mutation.
            return Ok(false);
        };
        let plan = SavedPlan::restore(bytes, record.document.generation, &contract)?;
        if matches!(
            migrations::status(engine, &scope, contract.collection()),
            Ok(MigrationStatus::Stopped { .. } | MigrationStatus::RollingBack { .. })
        ) {
            return Err(
                "migration stopped with durable evidence; repair or roll back before restarting"
                    .into(),
            );
        }
        migrations::begin(
            engine,
            &scope,
            &contract,
            &plan,
            migrations::MigrationOptions::default(),
        )
        .map_err(|error| format!("migration begin stopped: {error:?}"))?;
        if matches!(
            migrations::apply_page(engine, &scope, &contract, &plan)
                .map_err(|error| format!("migration page stopped: {error:?}"))?,
            MigrationStep::ScanComplete
        ) {
            migrations::finalize(engine, &scope, &contract, &plan)
                .map_err(|error| format!("migration verification stopped: {error:?}"))?;
        }
        match migrations::status(engine, &scope, contract.collection())
            .map_err(|error| format!("migration status unavailable: {error:?}"))?
        {
            MigrationStatus::Complete { receipt } => {
                if let Some(migration) = record.document.migration.as_mut() {
                    migration.completed = receipt.documents();
                    migration.total = receipt.documents();
                }
            }
            MigrationStatus::InProgress { documents, pages, .. } => {
                if let Some(migration) = record.document.migration.as_mut() {
                    migration.completed = documents;
                    migration.next_cursor = Some(format!("page:{pages}"));
                    migration.message = "document backfill is running".into();
                }
                return Ok(false);
            }
            _ => {
                return Err(
                    "migration stopped; inspect and repair the collection before retrying".into()
                );
            }
        }
    }
    let scope = document_scope(engine).map_err(|()| "database scope is unavailable".to_owned())?;
    super::schema_index_runtime::advance(engine, &scope, record)
}

/// Reconcile the public progress projection with the core migration record
/// before starting another page.  The core page commit is authoritative after
/// a crash; recording its page count first prevents a replay from advertising
/// an older cursor while the next batch is admitted.
fn checkpoint_before_batch(
    engine: &terrabase::Engine,
    record: &mut DurableSchemaRecord,
) -> Result<(), String> {
    let Some(plan_bytes) = record.migration_plan.as_deref() else {
        return Ok(());
    };
    let Some(migration) = record.document.migration.as_ref() else {
        return Ok(());
    };
    if !migration.steps.iter().any(|step| step.kind == "field_rename") {
        return Ok(());
    }
    let contract = contract(record)?;
    let _plan = SavedPlan::restore(plan_bytes, record.document.generation, &contract)?;
    let scope = document_scope(engine).map_err(|()| "database scope is unavailable".to_owned())?;
    match migrations::status(engine, &scope, contract.collection())
        .map_err(|error| format!("migration status unavailable: {error:?}"))?
    {
        MigrationStatus::InProgress { documents, pages, .. } => {
            if let Some(migration) = record.document.migration.as_mut() {
                migration.completed = documents;
                migration.next_cursor = Some(format!("page:{pages}"));
                migration.message = "document backfill is running".into();
            }
        }
        MigrationStatus::Complete { receipt } => {
            if let Some(migration) = record.document.migration.as_mut() {
                migration.completed = receipt.documents();
                migration.total = receipt.documents();
                migration.next_cursor = None;
            }
        }
        _ => {}
    }
    Ok(())
}

fn drive_database(state: &ApiState, database: &str, after: Option<&str>) -> Option<String> {
    let host = document_host(state, database).ok()?;
    let engine = host.engine();
    // Index descriptor backfills and lifecycle maintenance share this fair
    // database pass.  Run before reading schema metadata so a database with
    // no pending schema operation still makes progress.  Both Engine calls
    // are bounded; failures are surfaced to the member log and the database
    // remains eligible on the next tick rather than being silently dropped.
    if let Err(error) = engine.maintain_index_backfills() {
        eprintln!("index backfill maintenance failed for database {database}: {error:?}");
    }
    engine.maintain_indexes();
    // Share the existing round-robin database budget. Each pass limits expiry
    // keys and retired publications; cancellation stops scheduling new passes
    // while an already started SQLite transaction finishes atomically.
    if let Err(error) = engine.maintain_retention(16) {
        eprintln!("retention maintenance failed for database {database}: {error:?}");
    }
    let rows = engine.schema_metadata_list(database, "main", after, 1).ok()?;
    let (collection, expected) = rows.into_iter().next()?;
    let Ok(mut record) = serde_json::from_slice::<DurableSchemaRecord>(&expected) else {
        return Some(collection);
    };
    if record.document.target.database != database
        || record.document.target.branch != "main"
        || record.document.target.collection != collection
        || record.document.identity.database_id != engine.database_owner().database().to_text()
        || !record.document.migration.as_ref().is_none_or(|migration| {
            matches!(
                migration.state,
                MigrationState::Pending
                    | MigrationState::Running
                    | MigrationState::Failed
                    | MigrationState::RollingBack
            )
        })
    {
        return Some(collection);
    }
    // Commit the saved generation and the latest core cursor before admitting
    // another page.  If the process died after a core page commit, this first
    // publication repairs the stale envelope before work is retried.
    let mut persisted_expected = expected.clone();
    if record.migration_plan.is_some() {
        if let Err(message) = checkpoint_before_batch(engine, &mut record) {
            if let Some(migration) = record.document.migration.as_mut() {
                migration.state = MigrationState::Failed;
                migration.message = message;
            }
            let _ = persist(engine, &expected, &record);
            return Some(collection);
        }
        let Ok(checkpoint_bytes) = serde_json::to_vec(&record) else {
            return Some(collection);
        };
        if !persist_encoded(engine, &persisted_expected, &record, &checkpoint_bytes, false)
            .is_ok_and(|persisted| persisted)
        {
            return Some(collection);
        }
        persisted_expected = checkpoint_bytes;
    }
    let result = advance(engine, &mut record);
    if let Some(migration) = record.document.migration.as_mut() {
        match result {
            Ok(true) => {
                migration.state = MigrationState::Complete;
                migration.next_cursor = None;
                migration.message = "all schema migration steps completed".into();
                record.previous_schema = None;
                record.migration_plan = None;
            }
            Ok(false) if record.rollback.is_some() => migration.state = MigrationState::RollingBack,
            Ok(false) => migration.state = MigrationState::Running,
            Err(message) => {
                migration.state = if record.rollback.is_some() {
                    MigrationState::RollingBack
                } else {
                    MigrationState::Failed
                };
                migration.message = message;
            }
        }
    }
    // CAS against the envelope bytes that were actually committed by the
    // preceding checkpoint.  In particular, the no-plan path still has the
    // original envelope as its expected value while `advance` mutates the
    // in-memory projection.
    let final_bytes = serde_json::to_vec(&record).unwrap_or_else(|_| expected.clone());
    let _ = persist_encoded(engine, &persisted_expected, &record, &final_bytes, false);
    Some(collection)
}

pub(super) fn spawn(state: &Arc<ApiState>) -> Option<tokio::task::JoinHandle<()>> {
    let state = Arc::downgrade(state);
    Some(tokio::spawn(async move {
        let mut interval = tokio::time::interval(std::time::Duration::from_secs(1));
        interval.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Skip);
        let mut offset = 0;
        let mut cursors = std::collections::BTreeMap::<String, String>::new();
        loop {
            interval.tick().await;
            let Some(state) = state.upgrade() else { break };
            let registry_state = Arc::clone(&state);
            let region = state.region;
            let records = match tokio::task::spawn_blocking(move || {
                database_records(&registry_state)
            })
            .await
            {
                Ok(Ok(records)) => records
                    .into_iter()
                    .filter(|record| !record.deleted && record.placement.contains(&region))
                    .collect::<Vec<_>>(),
                _ => continue,
            };
            cursors.retain(|database, _| records.iter().any(|record| &record.name == database));
            if records.is_empty() {
                continue;
            }
            for index in 0..records.len().min(8) {
                let name = records[(offset + index) % records.len()].name.clone();
                let after = cursors.remove(&name);
                let worker_state = Arc::clone(&state);
                let worker_name = name.clone();
                if let Ok(Some(cursor)) = tokio::task::spawn_blocking(move || {
                    drive_database(&worker_state, &worker_name, after.as_deref())
                })
                .await
                {
                    cursors.insert(name, cursor);
                }
            }
            offset = (offset + 8) % records.len();
        }
    }))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::api::{
        Document, DocumentId, DocumentValue, ExpectedVersion, RegionCode, Request, Router,
        StatusCode, router,
    };
    use axum::body::Body;
    use tower::ServiceExt;

    const ROUTE: &str = "/api/v1/databases/demo/collections/orders/schema";
    const KEY: &str = "Bearer tskey_test_abcdefghijklmnop";

    async fn apply_schema(app: &Router, body: &str, key: &str, generation: Option<u64>) {
        let mut request =
            Request::put(ROUTE).header("authorization", KEY).header("idempotency-key", key);
        if let Some(generation) = generation {
            request = request.header("if-match", format!("\"{generation}\""));
        }
        let response =
            app.clone().oneshot(request.body(Body::from(body.to_owned())).unwrap()).await.unwrap();
        let status = response.status();
        let bytes = axum::body::to_bytes(response.into_body(), 1024 * 1024).await.unwrap();
        assert_eq!(status, StatusCode::OK, "{}", String::from_utf8_lossy(&bytes));
    }

    #[tokio::test]
    #[allow(clippy::too_many_lines)]
    async fn served_rename_resumes_saved_plan_and_core_cursor_after_reopen() {
        let state = super::super::tests::state_with_keys_and_control();
        state.control.as_ref().unwrap().create("demo", &[RegionCode::GRU], None).unwrap();
        let app = router(Arc::clone(&state));
        apply_schema(
            &app,
            r#"{"name":"orders","fields":[{"name":"title","type":"string","indexed":true}]}"#,
            "initial",
            None,
        )
        .await;
        // Initial declared indexes have their own durable reconciliation.
        // Finish it through the production tick before requesting a rename.
        assert_eq!(drive_database(&state, "demo", None).as_deref(), Some("orders"));
        {
            let host = document_host(&state, "demo").unwrap();
            let engine = host.engine();
            let record: DurableSchemaRecord = serde_json::from_slice(
                &engine.schema_metadata_load("demo", "main", "orders").unwrap().unwrap(),
            )
            .unwrap();
            assert_eq!(record.document.generation, 1);
            assert_eq!(record.document.migration.unwrap().state, MigrationState::Complete);
            let scope = document_scope(engine).unwrap();
            let mut transaction = engine
                .begin(
                    &scope,
                    engine.read_snapshot(&scope).unwrap(),
                    OperationId::try_new("migration-seed").unwrap(),
                )
                .unwrap();
            transaction
                .put(
                    Document::new(
                        DocumentId::try_new("order-1").unwrap(),
                        CollectionName::try_new("orders").unwrap(),
                        DocumentValue::Object(std::collections::BTreeMap::from([(
                            "title".into(),
                            DocumentValue::String("preserved".into()),
                        )])),
                    ),
                    ExpectedVersion::New,
                )
                .unwrap();
            assert!(matches!(
                transaction.commit(LocalDurability::Synced),
                terrabase::CommitOutcome::Committed(_)
            ));
        }
        apply_schema(&app, r#"{"name":"orders","fields":[{"name":"headline","type":"string","indexed":true}],"renames":[{"from":"title","to":"headline"}]}"#, "rename", Some(1)).await;
        assert_eq!(drive_database(&state, "demo", None).as_deref(), Some("orders"));
        {
            let host = document_host(&state, "demo").unwrap();
            let engine = host.engine();
            let bytes = engine.schema_metadata_load("demo", "main", "orders").unwrap().unwrap();
            let record: DurableSchemaRecord = serde_json::from_slice(&bytes).unwrap();
            assert!(record.migration_plan.is_some());
            let saved: SavedPlan =
                serde_json::from_slice(record.migration_plan.as_ref().unwrap()).unwrap();
            assert_eq!(saved.generation, record.document.generation);
            let contract = contract(&record).unwrap();
            let plan = SavedPlan::restore(
                record.migration_plan.as_ref().unwrap(),
                record.document.generation,
                &contract,
            )
            .unwrap();
            let scope = document_scope(engine).unwrap();
            migrations::begin(
                engine,
                &scope,
                &contract,
                &plan,
                migrations::MigrationOptions::default(),
            )
            .unwrap();
            migrations::apply_page(engine, &scope, &contract, &plan).unwrap();
            // Simulate a crash after the atomic document+cursor commit and
            // physical index commit, before the public envelope projection.
            let record: DurableSchemaRecord = serde_json::from_slice(
                &engine.schema_metadata_load("demo", "main", "orders").unwrap().unwrap(),
            )
            .unwrap();
            assert!(super::super::schema_index_runtime::advance(engine, &scope, &record).unwrap());
        }
        state.catalog.as_ref().unwrap().close_database(&state.scope.0, &state.scope.1, "demo");
        for _ in 0..4 {
            drive_database(&state, "demo", None);
        }
        let host = document_host(&state, "demo").unwrap();
        let engine = host.engine();
        let bytes = engine.schema_metadata_load("demo", "main", "orders").unwrap().unwrap();
        let record: DurableSchemaRecord = serde_json::from_slice(&bytes).unwrap();
        let progress = record.document.migration.unwrap();
        assert_eq!(progress.state, MigrationState::Complete, "{}", progress.message);
        assert_eq!((progress.total, progress.completed), (1, 1));
        assert!(record.previous_schema.is_none());
        assert!(record.migration_plan.is_none());
        let descriptor = engine
            .index_descriptors()
            .unwrap()
            .into_iter()
            .find(|descriptor| descriptor.collection == "orders" && descriptor.field == "headline")
            .unwrap();
        assert!(descriptor.active, "restart must retain the committed physical index");
        let scope = document_scope(engine).unwrap();
        let snapshot = engine.read_snapshot(&scope).unwrap();
        let documents = engine.snapshot_documents(&scope, &snapshot).unwrap();
        let document =
            documents.iter().find(|document| document.id().as_str() == "order-1").unwrap();
        let DocumentValue::Object(fields) = document.value() else { panic!("object") };
        assert!(!fields.contains_key("title"));
        assert_eq!(fields.get("headline"), Some(&DocumentValue::String("preserved".into())));
        assert!(matches!(
            migrations::status(engine, &scope, &CollectionName::try_new("orders").unwrap())
                .unwrap(),
            MigrationStatus::Complete { .. }
        ));
    }
}

#[cfg(test)]
#[path = "schema_migration_runtime_index_tests.rs"]
mod runtime_index_tests;
