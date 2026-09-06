use super::*;
use std::sync::Arc;
use std::sync::atomic::Ordering;

struct LargeJournal;
impl WebhookJournal for LargeJournal {
    fn page(
        &self,
        _: &TenantScope,
        cursor: Option<ChangeCheckpoint>,
        limit: usize,
    ) -> Result<PublicationPage, StorageError> {
        let after = cursor.map_or(0, |value| value.write_index());
        let publications = ((after + 1)..=300)
            .take(limit)
            .map(|write_index| Publication {
                revision: 1,
                write_index,
                event_id: format!("event-{write_index}"),
                body: b"payload".to_vec(),
            })
            .collect::<Vec<_>>();
        let next = publications
            .last()
            .map(|row| ChangeCheckpoint::after(row.revision, row.write_index))
            .or(cursor);
        Ok(PublicationPage { publications, next })
    }
}

#[tokio::test]
async fn cleanup_failure_does_not_block_multi_page_cursor_progress() {
    let root = std::env::temp_dir()
        .join(format!("terrascale-webhook-cursor-{}", terrabase::identity::generate()));
    let engine = Arc::new(
        Engine::open(terrabase::EngineConfig::durable(root.clone()).expect("config"))
            .expect("engine"),
    );
    let owner = engine.database_owner();
    let scope = TenantScope::new(
        owner.tenant().to_text(),
        owner.project().to_text(),
        owner.database().to_text(),
    )
    .expect("scope");
    let storage = Arc::new(EngineWebhookStorage::new(Arc::clone(&engine)));
    let secrets = Arc::new(crate::webhook_api::delivery_tests::TestPorts::default());
    secrets.destruction_unavailable.store(true, Ordering::SeqCst);
    let delivery = Arc::new(crate::webhook_api::delivery_tests::BudgetPorts {
        base: crate::webhook_api::delivery_tests::TestPorts::default(),
        stop: Arc::new(std::sync::atomic::AtomicBool::new(false)),
        stop_after_send: false,
    });
    let service = WebhookService::new(
        storage.clone(),
        Arc::new(LargeJournal),
        secrets.clone(),
        Arc::new(AbuseGuardWebhookGuard::new(Arc::new(AbuseGuards::new()))),
        delivery.clone(),
        secrets.clone(),
        "GRU",
    )
    .expect("service");
    let deleted =
        storage.create_endpoint(&scope, "https://hooks.example/deleted", 0).expect("deleted");
    storage.delete_endpoint(&scope, &deleted.id).expect("tombstone");
    let live = storage.create_endpoint(&scope, "https://hooks.example/live", 0).expect("live");

    let first = service.dispatch(&scope, None).await.expect("first page");
    assert!(first.cleanup_pending);
    assert_eq!(first.publications, MAX_JOURNAL_PAGE);
    storage.checkpoint_dispatch(&scope, None, first.cursor).expect("checkpoint first page");
    let second = service.dispatch(&scope, first.cursor).await.expect("second page");
    assert!(second.cleanup_pending);
    assert_eq!(second.publications, 44);
    assert!(second.attempted > 0);
    assert!(delivery.base.sent.load(Ordering::SeqCst) >= 1);
    assert!(
        delivery
            .base
            .seen_events
            .lock()
            .expect("events lock")
            .iter()
            .any(|id| id.ends_with(":event-257"))
    );
    assert!(storage.endpoint(&scope, &live.id).expect("live lookup").is_some());
    drop(service);
    let Ok(engine) = Arc::try_unwrap(engine) else { panic!("sole engine") };
    engine.shutdown().expect("shutdown");
    std::fs::remove_dir_all(root).expect("cleanup");
}
