use super::*;

use crate::api::{Document, DocumentId, DocumentValue, ExpectedVersion, RegionCode};
use axum::body::Body;
use terrabase::types::{CollectionName, LocalDurability, OperationId};
use tower::ServiceExt;

const SCHEMA_ROUTE: &str = "/api/v1/databases/demo/collections/orders/schema";
const KEY: &str = "Bearer tskey_test_abcdefghijklmnop";

/// Initial indexed schema declarations need the startup worker to finish
/// their durable reconciliation and publish physical indexes. This exercises the
/// production router and worker caller rather than registering through Engine
/// directly.
#[tokio::test]
async fn production_tick_completes_initial_declared_index_reconciliation() {
    let state = super::super::tests::state_with_keys_and_control();
    state.control.as_ref().unwrap().create("demo", &[RegionCode::GRU], None).unwrap();
    let app = Arc::new(crate::api::router(Arc::clone(&state)));
    let response = app.as_ref().clone()
        .oneshot(
            crate::api::Request::put(SCHEMA_ROUTE)
                .header("authorization", KEY)
                .header("idempotency-key", "initial-index")
                .body(Body::from(
                    r#"{"name":"orders","fields":[{"name":"account","type":"string","indexed":true}]}"#,
                ))
                .unwrap(),
        )
        .await
        .unwrap();
    assert_eq!(response.status(), crate::api::StatusCode::OK);

    assert_eq!(super::drive_database(&state, "demo", None).as_deref(), Some("orders"));
    let host = document_host(&state, "demo").unwrap();
    let engine = host.engine();
    let record: DurableSchemaRecord = serde_json::from_slice(
        &engine.schema_metadata_load("demo", "main", "orders").unwrap().unwrap(),
    )
    .unwrap();
    assert_eq!(record.document.migration.unwrap().state, MigrationState::Complete);
    // A completed envelope must remain the CAS base for the next worker tick;
    // this also catches the no-plan path replaying a mutated record as its
    // expected value and leaving the declaration pending forever.
    assert_eq!(super::drive_database(&state, "demo", None).as_deref(), Some("orders"));
    let record: DurableSchemaRecord = serde_json::from_slice(
        &engine.schema_metadata_load("demo", "main", "orders").unwrap().unwrap(),
    )
    .unwrap();
    assert_eq!(record.document.migration.unwrap().state, MigrationState::Complete);
    let descriptor = engine
        .index_descriptors()
        .unwrap()
        .into_iter()
        .find(|descriptor| descriptor.collection == "orders" && descriptor.field == "account")
        .expect("initial declared index descriptor");
    assert!(descriptor.active, "initial schema index must be published by the worker");
}

/// Retired schema indexes remain authoritative while a replacement unique
/// index is still validating its bounded frontier. This is the publication
/// ordering required for a restart to never expose a partially reconciled
/// schema.
#[tokio::test]
async fn replacement_index_keeps_old_unique_constraint_until_publish() {
    let state = super::super::tests::state_with_keys_and_control();
    state.control.as_ref().unwrap().create("demo", &[RegionCode::GRU], None).unwrap();
    let app = Arc::new(crate::api::router(Arc::clone(&state)));
    let apply = |app: Arc<crate::api::Router>,
                 body: &'static str,
                 key: &'static str,
                 generation| async move {
        let mut request = crate::api::Request::put(SCHEMA_ROUTE)
            .header("authorization", KEY)
            .header("idempotency-key", key);
        if let Some(generation) = generation {
            request = request.header("if-match", format!("\"{generation}\""));
        }
        let response =
            app.as_ref().clone().oneshot(request.body(Body::from(body)).unwrap()).await.unwrap();
        assert_eq!(response.status(), crate::api::StatusCode::OK);
    };
    apply(
        Arc::clone(&app),
        r#"{"name":"orders","fields":[{"name":"account","type":"string"}],"indexes":[{"name":"old_account","fields":["account"],"unique":true}]}"#,
        "old-index",
        None,
    )
    .await;
    assert_eq!(super::drive_database(&state, "demo", None).as_deref(), Some("orders"));

    let host = document_host(&state, "demo").unwrap();
    let engine = host.engine();
    let scope = document_scope(engine).unwrap();
    let collection = CollectionName::try_new("orders").unwrap();
    let mut transaction = engine
        .begin(
            &scope,
            engine.read_snapshot(&scope).unwrap(),
            OperationId::try_new("seed-replacement-index").unwrap(),
        )
        .unwrap();
    for index in 0..33 {
        transaction
            .put(
                Document::new(
                    DocumentId::try_new(&format!("order-{index:02}")).unwrap(),
                    collection.clone(),
                    DocumentValue::Object(std::collections::BTreeMap::from([(
                        "account".into(),
                        DocumentValue::String(format!("account-{index}")),
                    )])),
                ),
                ExpectedVersion::New,
            )
            .unwrap();
    }
    assert!(matches!(
        transaction.commit(LocalDurability::Synced),
        terrabase::CommitOutcome::Committed(_)
    ));

    apply(
        Arc::clone(&app),
        r#"{"name":"orders","fields":[{"name":"account","type":"string"}],"indexes":[{"name":"new_account","fields":["account"],"unique":true}]}"#,
        "new-index",
        Some(1),
    )
    .await;
    assert_eq!(super::drive_database(&state, "demo", None).as_deref(), Some("orders"));

    let mut transaction = engine
        .begin(
            &scope,
            engine.read_snapshot(&scope).unwrap(),
            OperationId::try_new("duplicate-before-replacement-publish").unwrap(),
        )
        .unwrap();
    transaction
        .put(
            Document::new(
                DocumentId::try_new("duplicate").unwrap(),
                collection,
                DocumentValue::Object(std::collections::BTreeMap::from([(
                    "account".into(),
                    DocumentValue::String("account-0".into()),
                )])),
            ),
            ExpectedVersion::New,
        )
        .unwrap();
    let outcome = transaction.commit(LocalDurability::Synced);
    assert!(
        !matches!(outcome, terrabase::CommitOutcome::Committed(_)),
        "duplicate write must not commit while the old unique index remains authoritative: {outcome:?}"
    );
}

/// Exercises the production schema tick rather than calling the Engine pump
/// directly.  A collection above the inline threshold must progress even
/// when it has no pending schema envelope, because maintenance runs before
/// the metadata early exit in `drive_database`.
#[tokio::test]
#[allow(clippy::too_many_lines)]
async fn production_tick_completes_large_descriptor_without_schema_record() {
    const ROWS: usize = 10_001;

    let state = super::super::tests::state_with_keys_and_control();
    state.control.as_ref().unwrap().create("demo", &[RegionCode::GRU], None).unwrap();
    let host = document_host(&state, "demo").unwrap();
    let engine = host.engine();
    let scope = document_scope(engine).unwrap();
    let collection = CollectionName::try_new("orders").unwrap();
    // Backfill cursors advance at commit-sequence boundaries. Split the
    // fixture across several intents so one governed pass cannot drain every
    // row in a single sequence and make the descriptor active immediately.
    for (chunk, start) in (0..ROWS).step_by(4_000).enumerate() {
        let mut transaction = engine
            .begin(
                &scope,
                engine.read_snapshot(&scope).unwrap(),
                OperationId::try_new(&format!("runtime-index-seed-{chunk}")).unwrap(),
            )
            .unwrap();
        for index in start..(start + 4_000).min(ROWS) {
            transaction
                .put(
                    Document::new(
                        DocumentId::try_new(&format!("order-{index:05}")).unwrap(),
                        collection.clone(),
                        DocumentValue::Object(std::collections::BTreeMap::from([(
                            "account".to_owned(),
                            DocumentValue::String(format!("account-{index}")),
                        )])),
                    ),
                    ExpectedVersion::New,
                )
                .unwrap();
        }
        assert!(matches!(
            transaction.commit(LocalDurability::Synced),
            terrabase::CommitOutcome::Committed(_)
        ));
    }
    assert!(matches!(
        engine.register_index_field(&collection, "account"),
        Ok(terrabase::storage::DescriptorRegistration::Building)
    ));

    let worker = spawn(&state).unwrap();
    let mut partial = false;
    for _ in 0..20 {
        tokio::time::sleep(std::time::Duration::from_millis(100)).await;
        partial = engine.index_descriptors().unwrap().into_iter().any(|descriptor| {
            descriptor.collection == "orders"
                && descriptor.field == "account"
                && descriptor.backfill_remaining < ROWS as u64
                && !descriptor.active
        });
        if partial {
            break;
        }
    }
    worker.abort();
    let _ = worker.await;
    assert!(partial, "the production tick must pump large descriptors");

    // Change rows on both sides of the saved cursor before reopening. The
    // resumed descriptor must retain write maintenance for covered rows and
    // backfill the current value of rows it has not reached yet.
    let mut transaction = engine
        .begin(
            &scope,
            engine.read_snapshot(&scope).unwrap(),
            OperationId::try_new("runtime-index-during-backfill").unwrap(),
        )
        .unwrap();
    for index in [0, ROWS - 1] {
        transaction
            .put(
                Document::new(
                    DocumentId::try_new(&format!("order-{index:05}")).unwrap(),
                    collection.clone(),
                    DocumentValue::Object(std::collections::BTreeMap::from([(
                        "account".to_owned(),
                        DocumentValue::String("changed-during-backfill".into()),
                    )])),
                ),
                ExpectedVersion::Any,
            )
            .unwrap();
    }
    assert!(matches!(
        transaction.commit(LocalDurability::Synced),
        terrabase::CommitOutcome::Committed(_)
    ));
    let pending_before_restart = engine
        .index_descriptors()
        .unwrap()
        .into_iter()
        .find(|descriptor| descriptor.collection == "orders" && descriptor.field == "account")
        .unwrap();
    drop(host);
    state.catalog.as_ref().unwrap().close_database(&state.scope.0, &state.scope.1, "demo");
    let reopened = document_host(&state, "demo").unwrap();
    let pending_after_restart = reopened
        .engine()
        .index_descriptors()
        .unwrap()
        .into_iter()
        .find(|descriptor| descriptor.collection == "orders" && descriptor.field == "account")
        .unwrap();
    assert!(!pending_after_restart.active);
    assert_eq!(
        pending_after_restart.backfill_remaining, pending_before_restart.backfill_remaining,
        "reopen must retain the durable cursor"
    );
    let resumed_worker = spawn(&state).unwrap();
    let mut active = false;
    for _ in 0..20 {
        tokio::time::sleep(std::time::Duration::from_millis(100)).await;
        active = reopened.engine().index_descriptors().unwrap().into_iter().any(|descriptor| {
            descriptor.collection == "orders" && descriptor.field == "account" && descriptor.active
        });
        if active {
            break;
        }
    }
    resumed_worker.abort();
    let _ = resumed_worker.await;
    assert!(active, "the restarted production tick must resume the descriptor");
    let engine = reopened.engine();
    let scope = document_scope(engine).unwrap();
    for (account, expected_rows) in
        [("changed-during-backfill", 2), ("account-0", 0), ("account-10000", 0)]
    {
        let plan = engine
            .queries()
            .compile(terrabase::QueryInput::sql(
                terrabase::SqlQueryText::try_new(&format!(
                    "SELECT account FROM orders WHERE account = '{account}'"
                ))
                .unwrap(),
            ))
            .unwrap();
        let result =
            engine.queries().execute(&plan, &engine.read_snapshot(&scope).unwrap()).unwrap();
        assert_eq!(result.rows().len(), expected_rows, "stale index result for {account}");
    }
}
