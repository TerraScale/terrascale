use super::*;
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::Duration;

#[derive(Default)]
struct TestPorts {
    now: AtomicU64,
    sent: AtomicU64,
    destruction_unavailable: std::sync::atomic::AtomicBool,
    provision_gate: std::sync::Mutex<Option<ProvisionGate>>,
}

struct ProvisionGate {
    entered: tokio::sync::oneshot::Sender<()>,
    resume: std::sync::mpsc::Receiver<()>,
}

impl WebhookClock for TestPorts {
    fn now(&self) -> u64 {
        self.now.load(Ordering::SeqCst)
    }
}

impl WebhookJournal for TestPorts {
    fn page(
        &self,
        _: &TenantScope,
        cursor: Option<ChangeCheckpoint>,
        limit: usize,
    ) -> Result<PublicationPage, StorageError> {
        let publications = if journal::checkpoint_order(cursor) < Some((7, 1)) && limit > 0 {
            vec![Publication {
                revision: 7,
                write_index: 1,
                event_id: "event-7".into(),
                body: b"payload".to_vec(),
            }]
        } else {
            Vec::new()
        };
        let next = publications
            .last()
            .map(|row| ChangeCheckpoint::after(row.revision, row.write_index))
            .or(cursor);
        Ok(PublicationPage { publications, next })
    }
}

impl WebhookSecrets for TestPorts {
    fn provision(&self, _: &TenantScope, _: &str, _: &str) -> Result<(), SecretError> {
        if let Some(gate) = self.provision_gate.lock().expect("provision gate").take() {
            gate.entered.send(()).map_err(|()| SecretError::Unavailable)?;
            gate.resume
                .recv_timeout(Duration::from_secs(5))
                .map_err(|_| SecretError::Unavailable)?;
        }
        Ok(())
    }
    fn destroy(&self, _: &TenantScope, _: &str, _: &str) -> Result<(), SecretError> {
        if self.destruction_unavailable.load(Ordering::SeqCst) {
            Err(SecretError::Unavailable)
        } else {
            Ok(())
        }
    }
    fn creation(
        &self,
        scope: &TenantScope,
        id: &str,
        region: &str,
    ) -> Result<Vec<u8>, SecretError> {
        self.get(scope, id, region)
    }
    fn rotate(
        &self,
        _: &TenantScope,
        _: &str,
        _: &str,
        _: u64,
        _: u64,
    ) -> Result<Vec<u8>, SecretError> {
        Ok(b"rotated-test-secret".to_vec())
    }
    fn previous(
        &self,
        _: &TenantScope,
        _: &str,
        _: &str,
        _: u64,
    ) -> Result<Option<Vec<u8>>, SecretError> {
        Ok(None)
    }
    fn get(&self, _: &TenantScope, _: &str, _: &str) -> Result<Vec<u8>, SecretError> {
        Ok(b"test-secret".to_vec())
    }
}

impl WebhookDelivery for TestPorts {
    fn validate_target(&self, _: &str) -> Result<(), DeliveryError> {
        Ok(())
    }
    fn send(
        &self,
        _: &str,
        _: &BTreeMap<String, String>,
        body: &[u8],
    ) -> Result<DeliveryResponse, DeliveryError> {
        assert_eq!(body, b"payload");
        self.sent.fetch_add(1, Ordering::SeqCst);
        Err(DeliveryError::Retryable)
    }
}

fn service(engine: &Arc<Engine>, ports: &Arc<TestPorts>) -> WebhookService {
    WebhookService::new(
        Arc::new(EngineWebhookStorage::new(Arc::clone(engine))),
        ports.clone(),
        ports.clone(),
        Arc::new(AbuseGuardWebhookGuard::new(Arc::new(AbuseGuards::new()))),
        ports.clone(),
        ports.clone(),
        "GRU",
    )
    .expect("test service")
}

#[tokio::test]
async fn retries_survive_cursor_advance_and_restart_then_expire_without_sending() {
    let root = std::env::temp_dir().join(format!(
        "terrascale-webhook-retries-{}",
        terrabase::identity::process_generator().generate().to_text()
    ));
    let open = || {
        Arc::new(
            Engine::open(terrabase::EngineConfig::durable(root.clone()).expect("config"))
                .expect("engine"),
        )
    };
    let engine = open();
    let owner = engine.database_owner();
    let scope = TenantScope::new(
        owner.tenant().to_text(),
        owner.project().to_text(),
        owner.database().to_text(),
    )
    .expect("scope");
    let ports = Arc::new(TestPorts::default());
    ports.now.store(100, Ordering::SeqCst);
    let first = service(&engine, &ports);
    let endpoint = first
        .storage
        .create_endpoint(&scope, "https://hooks.example/events", 100)
        .expect("endpoint");
    assert_eq!(first.dispatch(&scope, None).await.expect("initial dispatch").attempted, 1);
    assert_eq!(
        first
            .dispatch(&scope, Some(ChangeCheckpoint::after(7, 1)))
            .await
            .expect("not due")
            .attempted,
        0
    );
    drop(first);
    let Ok(engine) = Arc::try_unwrap(engine) else { panic!("sole engine") };
    engine.shutdown().expect("shutdown");

    let engine = open();
    let reopened = service(&engine, &ports);
    ports.now.store(101, Ordering::SeqCst);
    let retry = reopened
        .dispatch(&scope, Some(ChangeCheckpoint::after(7, 1)))
        .await
        .expect("retry with advanced cursor");
    assert_eq!(retry.publications, 0);
    assert_eq!(retry.attempted, 1);
    assert_eq!(ports.sent.load(Ordering::SeqCst), 2);
    ports.now.store(100 + RETRY_HORIZON_SECONDS, Ordering::SeqCst);
    let expired = reopened
        .dispatch(&scope, Some(ChangeCheckpoint::after(7, 1)))
        .await
        .expect("expire without publication");
    assert_eq!(expired.attempted, 0);
    assert_eq!(ports.sent.load(Ordering::SeqCst), 2);
    let log = reopened.delivery_log(&scope, &endpoint.id, 10).expect("delivery log");
    assert!(log.deliveries[0].terminal);
    assert_eq!(log.deliveries[0].attempts, 2);
    drop(reopened);
    let Ok(engine) = Arc::try_unwrap(engine) else { panic!("sole reopened engine") };
    engine.shutdown().expect("shutdown reopened");
    std::fs::remove_dir_all(root).expect("cleanup");
}

#[test]
fn durable_adapters_reject_every_foreign_owner_dimension_without_mutation() {
    let root = std::env::temp_dir().join(format!(
        "terrascale-webhook-scope-{}",
        terrabase::identity::process_generator().generate().to_text()
    ));
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
    let storage = EngineWebhookStorage::new(Arc::clone(&engine));
    let journal = EngineWebhookJournal::new(Arc::clone(&engine));
    let endpoint =
        storage.create_endpoint(&scope, "https://hooks.example/events", 100).expect("endpoint");
    let publication = Publication {
        revision: 7,
        write_index: 1,
        event_id: "event-7".into(),
        body: b"payload".to_vec(),
    };
    let delivery =
        storage.ensure_delivery(&scope, &endpoint.id, &publication, 100).expect("delivery");
    let revision = engine.published_sequence().expect("sequence");
    for dimension in 0..3 {
        let mut foreign = scope.clone();
        match dimension {
            0 => foreign.tenant = "foreign-tenant".into(),
            1 => foreign.project = "foreign-project".into(),
            _ => foreign.database = "foreign-database".into(),
        }
        assert_eq!(
            storage.create_endpoint(&foreign, "https://hooks.example/events", 100),
            Err(StorageError::Conflict)
        );
        assert_eq!(storage.endpoint(&foreign, &endpoint.id), Err(StorageError::Conflict));
        assert_eq!(storage.list_endpoints(&foreign, true), Err(StorageError::Conflict));
        assert_eq!(storage.delete_endpoint(&foreign, &endpoint.id), Err(StorageError::Conflict));
        assert_eq!(
            storage.ensure_delivery(&foreign, &endpoint.id, &publication, 100),
            Err(StorageError::Conflict)
        );
        assert_eq!(storage.delivery_log(&foreign, &endpoint.id, 10), Err(StorageError::Conflict));
        assert_eq!(
            storage.replay_delivery(&foreign, &endpoint.id, &delivery.id, 101),
            Err(StorageError::Conflict)
        );
        assert_eq!(
            storage.expire_delivery(&foreign, &delivery, 100 + RETRY_HORIZON_SECONDS),
            Err(StorageError::Conflict)
        );
        assert_eq!(
            storage.record_attempt(
                &foreign,
                &endpoint.id,
                &delivery,
                &DeliveryAttempt {
                    attempts: 1,
                    next_attempt_at: 101,
                    status: None,
                    delivered: false,
                    terminal: false,
                }
            ),
            Err(StorageError::Conflict)
        );
        for limit in [0, 10] {
            assert_eq!(
                storage.due_deliveries(&foreign, &endpoint.id, 101, limit),
                Err(StorageError::Conflict)
            );
            assert_eq!(journal.page(&foreign, None, limit), Err(StorageError::Conflict));
        }
    }
    assert_eq!(engine.published_sequence().expect("unchanged sequence"), revision);
    assert_eq!(storage.endpoint(&scope, &endpoint.id), Ok(Some(endpoint.clone())));
    assert_eq!(
        storage.delivery_log(&scope, &endpoint.id, 10).expect("log").deliveries,
        vec![delivery]
    );
    drop(storage);
    drop(journal);
    let Ok(engine) = Arc::try_unwrap(engine) else { panic!("sole engine") };
    engine.shutdown().expect("shutdown");
    std::fs::remove_dir_all(root).expect("cleanup");
}

struct TestAuthorizer(TenantScope);

impl WebhookAuthorizer for TestAuthorizer {
    fn authorize(&self, _: &HeaderMap, _: &Method, _: &str) -> Result<TenantScope, WebhookError> {
        Ok(self.0.clone())
    }
}

#[tokio::test(flavor = "current_thread")]
async fn create_handler_keeps_runtime_responsive_while_secret_provider_blocks() {
    let root = std::env::temp_dir().join(format!(
        "terrascale-webhook-blocking-{}",
        terrabase::identity::process_generator().generate().to_text()
    ));
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
    let ports = Arc::new(TestPorts::default());
    let (entered, waiting) = tokio::sync::oneshot::channel();
    let (resume, receiver) = std::sync::mpsc::channel();
    *ports.provision_gate.lock().expect("gate") = Some(ProvisionGate { entered, resume: receiver });
    let state = Arc::new(WebhookHttpState {
        service: Arc::new(service(&engine, &ports)),
        authorizer: Arc::new(TestAuthorizer(scope.clone())),
    });
    let request = tokio::spawn(create_webhook(
        State(state),
        Path(scope.database),
        HeaderMap::new(),
        Bytes::from_static(br#"{"endpoint":"https://hooks.example/events"}"#),
    ));
    waiting.await.expect("provider entered");
    // This task runs on the sole async worker while the provider waits.
    resume.send(()).expect("runtime releases provider");
    assert_eq!(request.await.expect("handler").status(), StatusCode::CREATED);
    drop(ports);
    let Ok(engine) = Arc::try_unwrap(engine) else { panic!("sole engine") };
    engine.shutdown().expect("shutdown");
    std::fs::remove_dir_all(root).expect("cleanup");
}

#[test]
fn deletion_reports_secret_destruction_failure_and_remains_retryable() {
    let root = std::env::temp_dir().join(format!(
        "terrascale-webhook-delete-{}",
        terrabase::identity::process_generator().generate().to_text()
    ));
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
    let ports = Arc::new(TestPorts::default());
    let service = service(&engine, &ports);
    let endpoint = service
        .create(&scope, &CreateWebhook { endpoint: "https://hooks.example/events".into() })
        .expect("create");
    ports.destruction_unavailable.store(true, Ordering::SeqCst);
    assert_eq!(
        service.delete(&scope, &endpoint.id).expect_err("destruction failed").status(),
        StatusCode::SERVICE_UNAVAILABLE
    );
    assert!(service.list(&scope).expect("list").is_empty());
    ports.destruction_unavailable.store(false, Ordering::SeqCst);
    assert!(service.delete(&scope, &endpoint.id).expect("retry destruction").deleted);
    drop(service);
    let Ok(engine) = Arc::try_unwrap(engine) else { panic!("sole engine") };
    engine.shutdown().expect("shutdown");
    std::fs::remove_dir_all(root).expect("cleanup");
}

#[test]
fn journal_page_resumes_inside_a_large_commit_after_restart() {
    use terrabase::types::{CollectionName, Document, DocumentId, DocumentValue};
    let root = std::env::temp_dir().join(format!(
        "terrascale-webhook-batch-{}",
        terrabase::identity::process_generator().generate().to_text()
    ));
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
    let engine_scope = webhook_engine_scope(&engine, &scope).expect("scope");
    let snapshot = engine.read_snapshot(&engine_scope).expect("snapshot");
    let mut transaction = engine
        .begin(
            &engine_scope,
            snapshot,
            terrabase::OperationId::try_new("large-webhook-batch").expect("operation"),
        )
        .expect("transaction");
    for index in 0..MAX_JOURNAL_PAGE + 1 {
        transaction
            .put(
                Document::new(
                    DocumentId::try_new(&format!("item-{index}")).expect("id"),
                    CollectionName::try_new("orders").expect("collection"),
                    DocumentValue::Integer(index as i64),
                ),
                ExpectedVersion::New,
            )
            .expect("put");
    }
    assert!(matches!(
        transaction.commit(terrabase::LocalDurability::Synced),
        terrabase::CommitOutcome::Committed(_)
    ));
    let journal = EngineWebhookJournal::new(Arc::clone(&engine));
    let page = journal.page(&scope, None, MAX_JOURNAL_PAGE).expect("page");
    assert_eq!(page.publications.len(), MAX_JOURNAL_PAGE);
    assert!(page.publications.iter().all(|row| row.revision == page.publications[0].revision));
    let first = page.publications[0].clone();
    assert_eq!(page.next.expect("cursor").write_index(), MAX_JOURNAL_PAGE as u32);
    let storage = EngineWebhookStorage::new(Arc::clone(&engine));
    storage.checkpoint_dispatch(&scope, None, page.next).expect("checkpoint");
    let cursor = page.next;
    drop(storage);
    drop(journal);
    let Ok(engine) = Arc::try_unwrap(engine) else { panic!("sole engine") };
    engine.shutdown().expect("shutdown");
    let engine = Arc::new(
        Engine::open(terrabase::EngineConfig::durable(root.clone()).expect("config"))
            .expect("reopen"),
    );
    let storage = EngineWebhookStorage::new(Arc::clone(&engine));
    assert_eq!(storage.dispatch_cursor(&scope), Ok(cursor));
    let journal = EngineWebhookJournal::new(Arc::clone(&engine));
    let tail = journal.page(&scope, cursor, MAX_JOURNAL_PAGE).expect("next page");
    assert_eq!(tail.publications.len(), 1);
    assert_eq!(tail.publications[0].write_index, (MAX_JOURNAL_PAGE + 1) as u32);
    assert_eq!(tail.publications[0].revision, first.revision);
    assert_ne!(tail.publications[0].event_id, first.event_id);
    let endpoint =
        storage.create_endpoint(&scope, "https://hooks.example/events", 100).expect("endpoint");
    let pending = storage
        .ensure_delivery(&scope, &endpoint.id, &tail.publications[0], 100)
        .expect("pending tail retry");
    let retry = journal
        .page(&scope, journal::before_publication(&pending).expect("before"), 1)
        .expect("retry exact position");
    assert_eq!(retry.publications, tail.publications);
    let replay = journal.page(&scope, None, 1).expect("first event after restart");
    assert_eq!(replay.publications, vec![first]);
    assert!(
        journal.page(&scope, tail.next, MAX_JOURNAL_PAGE).expect("drained").publications.is_empty()
    );
    drop(journal);
    drop(storage);
    let Ok(engine) = Arc::try_unwrap(engine) else { panic!("sole reopened engine") };
    engine.shutdown().expect("close");
    std::fs::remove_dir_all(root).expect("cleanup");
}

#[test]
fn creation_retry_keeps_the_same_endpoint_and_secret_and_rejects_request_reuse() {
    let root = std::env::temp_dir().join(format!(
        "terrascale-webhook-idempotency-{}",
        terrabase::identity::process_generator().generate().to_text()
    ));
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
    let ports = Arc::new(TestPorts::default());
    let service = service(&engine, &ports);
    let input = CreateWebhook { endpoint: "https://hooks.example/events".into() };
    let first = service.create_with_secret(&scope, &input, Some("create-1")).expect("create");
    let retry = service.create_with_secret(&scope, &input, Some("create-1")).expect("retry");
    assert_eq!(first.webhook, retry.webhook);
    assert_eq!(first.secret, retry.secret);
    assert_eq!(service.list(&scope).expect("list").len(), 1);
    let conflict = service.create_with_secret(
        &scope,
        &CreateWebhook { endpoint: "https://other.example/events".into() },
        Some("create-1"),
    );
    assert!(matches!(conflict, Err(error) if error.status() == StatusCode::CONFLICT));
    drop(service);
    let Ok(engine) = Arc::try_unwrap(engine) else { panic!("sole engine") };
    engine.shutdown().expect("shutdown");
    std::fs::remove_dir_all(root).expect("cleanup");
}

#[test]
fn retained_journal_preserves_overwritten_events_and_pending_retry_after_restart() {
    use terrabase::types::{CollectionName, Document, DocumentId, DocumentValue};
    let root = std::env::temp_dir().join(format!(
        "terrascale-webhook-history-{}",
        terrabase::identity::process_generator().generate().to_text()
    ));
    let open = || {
        Arc::new(
            Engine::open(terrabase::EngineConfig::durable(root.clone()).expect("config"))
                .expect("engine"),
        )
    };
    let engine = open();
    let owner = engine.database_owner();
    let scope = TenantScope::new(
        owner.tenant().to_text(),
        owner.project().to_text(),
        owner.database().to_text(),
    )
    .expect("scope");
    let engine_scope = webhook_engine_scope(&engine, &scope).expect("engine scope");
    for index in 0..3 {
        let mut transaction = engine
            .begin(
                &engine_scope,
                engine.read_snapshot(&engine_scope).expect("snapshot"),
                terrabase::OperationId::try_new(&format!("overwrite-{index}")).expect("operation"),
            )
            .expect("transaction");
        let id = DocumentId::try_new("same-document").expect("id");
        if index == 2 {
            transaction.delete(id, ExpectedVersion::Any).expect("delete");
        } else {
            transaction
                .put(
                    Document::new(
                        id,
                        CollectionName::try_new("orders").expect("collection"),
                        DocumentValue::Integer(index),
                    ),
                    ExpectedVersion::Any,
                )
                .expect("put");
        }
        assert!(matches!(
            transaction.commit(terrabase::LocalDurability::Synced),
            terrabase::CommitOutcome::Committed(_)
        ));
    }
    let journal = EngineWebhookJournal::new(Arc::clone(&engine));
    let expected = journal.page(&scope, None, MAX_JOURNAL_PAGE).expect("all retained events");
    assert_eq!(expected.publications.len(), 3);
    assert!(expected.publications.windows(2).all(|pair| pair[0].revision < pair[1].revision && pair[0].event_id != pair[1].event_id));
    let storage = EngineWebhookStorage::new(Arc::clone(&engine));
    let endpoint =
        storage.create_endpoint(&scope, "https://hooks.example/events", 100).expect("endpoint");
    let pending = storage
        .ensure_delivery(&scope, &endpoint.id, &expected.publications[0], 100)
        .expect("pending original version");
    storage
        .checkpoint_dispatch(&scope, None, expected.next)
        .expect("advance past overwrite and delete");
    drop(storage);
    drop(journal);
    let Ok(engine) = Arc::try_unwrap(engine) else { panic!("sole engine") };
    engine.shutdown().expect("shutdown");
    let engine = open();
    let storage = EngineWebhookStorage::new(Arc::clone(&engine));
    assert_eq!(storage.dispatch_cursor(&scope), Ok(expected.next));
    let due = storage
        .due_deliveries(&scope, &endpoint.id, 100, MAX_JOURNAL_PAGE)
        .expect("durable pending retry");
    assert_eq!(due, vec![pending.clone()]);
    let journal = EngineWebhookJournal::new(Arc::clone(&engine));
    assert_eq!(journal.page(&scope, None, MAX_JOURNAL_PAGE).expect("reopened history"), expected);
    let retry = journal
        .page(&scope, journal::before_publication(&pending).expect("exact predecessor"), 1)
        .expect("retry original body");
    assert_eq!(retry.publications, vec![expected.publications[0].clone()]);
    drop(journal);
    drop(storage);
    let Ok(engine) = Arc::try_unwrap(engine) else { panic!("sole reopened engine") };
    engine.shutdown().expect("shutdown reopened");
    std::fs::remove_dir_all(root).expect("cleanup");
}
