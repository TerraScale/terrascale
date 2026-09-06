use super::*;

use crate::api::{Document, DocumentId, DocumentValue, ExpectedVersion, RegionCode};
use terrabase::types::{CollectionName, LocalDurability, OperationId};

/// Exercises the production schema tick rather than calling the Engine pump
/// directly.  A collection above the inline threshold must progress even
/// when it has no pending schema envelope, because maintenance runs before
/// the metadata early exit in `drive_database`.
#[tokio::test]
async fn production_tick_completes_large_descriptor_without_schema_record() {
    const ROWS: usize = 10_001;

    let state = super::super::tests::state_with_keys_and_control();
    state.control.as_ref().unwrap().create("demo", &[RegionCode::GRU], None).unwrap();
    let host = document_host(&state, "demo").unwrap();
    let engine = host.engine();
    let scope = document_scope(engine).unwrap();
    let collection = CollectionName::try_new("orders").unwrap();
    let mut transaction = engine
        .begin(
            &scope,
            engine.read_snapshot(&scope).unwrap(),
            OperationId::try_new("runtime-index-seed").unwrap(),
        )
        .unwrap();
    for index in 0..ROWS {
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

    state.catalog.as_ref().unwrap().close_database(&state.scope.0, &state.scope.1, "demo");
    let reopened = document_host(&state, "demo").unwrap();
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
}
