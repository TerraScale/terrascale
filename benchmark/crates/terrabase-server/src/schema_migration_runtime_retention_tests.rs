use super::*;
use crate::api::{Document, DocumentId, DocumentValue, ExpectedVersion, RegionCode};

fn seed(state: &ApiState, database: &str, count: usize, expires_at: i64, operation: &str) {
    let host = document_host(state, database).unwrap();
    let engine = host.engine();
    let scope = document_scope(engine).unwrap();
    let mut transaction = engine
        .begin(
            &scope,
            engine.read_snapshot(&scope).unwrap(),
            OperationId::try_new(operation).unwrap(),
        )
        .unwrap();
    for index in 0..count {
        transaction
            .put(
                Document::new(
                    DocumentId::try_new(&format!("session-{index}")).unwrap(),
                    CollectionName::try_new("sessions").unwrap(),
                    DocumentValue::Object(
                        [("expires_at".to_owned(), DocumentValue::Integer(expires_at))]
                            .into_iter()
                            .collect(),
                    ),
                ),
                ExpectedVersion::Any,
            )
            .unwrap();
    }
    assert!(matches!(
        transaction.commit(LocalDurability::Synced),
        terrabase::engine::CommitOutcome::Committed(_)
    ));
}

fn tombstones(state: &ApiState, database: &str, count: usize) -> usize {
    let host = document_host(state, database).unwrap();
    let engine = host.engine();
    let scope = document_scope(engine).unwrap();
    (0..count)
        .map(|index| {
            engine
                .document_history_in_collection(
                    &scope,
                    &CollectionName::try_new("sessions").unwrap(),
                    &DocumentId::try_new(&format!("session-{index}")).unwrap(),
                    16,
                )
                .unwrap()
                .iter()
                .filter(|revision| revision.deleted)
                .count()
        })
        .sum()
}

#[tokio::test]
async fn production_maintenance_bounds_expiry_and_keeps_databases_isolated() {
    let state = super::super::tests::state_with_keys_and_control();
    for database in ["demo", "sibling"] {
        state.control.as_ref().unwrap().create(database, &[RegionCode::GRU], None).unwrap();
    }
    seed(&state, "demo", 17, 1, "expired-sessions");
    seed(&state, "sibling", 1, i64::MAX, "live-session");
    // Neither database has pending schema metadata. The production pass must
    // still perform retention and yield after its expiry budget.
    assert!(drive_database(&state, "demo", None).is_none());
    assert_eq!(tombstones(&state, "demo", 17), 16);
    assert!(drive_database(&state, "sibling", None).is_none());
    assert_eq!(tombstones(&state, "sibling", 1), 0);
    assert!(drive_database(&state, "demo", None).is_none());
    assert_eq!(tombstones(&state, "demo", 17), 17);
}

#[tokio::test]
async fn aborting_periodic_worker_stops_future_expiry_passes() {
    let state = super::super::tests::state_with_keys_and_control();
    state.control.as_ref().unwrap().create("demo", &[RegionCode::GRU], None).unwrap();
    seed(&state, "demo", 1, 1, "first-expiry");
    let worker = spawn(&state).unwrap();
    tokio::time::timeout(std::time::Duration::from_secs(10), async {
        while tombstones(&state, "demo", 1) == 0 {
            tokio::time::sleep(std::time::Duration::from_millis(10)).await;
        }
    })
    .await
    .expect("real periodic worker expires the document");
    worker.abort();
    assert!(worker.await.unwrap_err().is_cancelled());
    // Synchronize with any in-flight database pass before publishing work
    // whose expiry must remain pending after cancellation.
    assert!(drive_database(&state, "demo", None).is_none());
    seed(&state, "demo", 1, 1, "second-expiry");
    tokio::time::sleep(std::time::Duration::from_millis(1200)).await;
    assert_eq!(tombstones(&state, "demo", 1), 1);
    assert!(drive_database(&state, "demo", None).is_none());
    assert_eq!(tombstones(&state, "demo", 1), 2);
}
