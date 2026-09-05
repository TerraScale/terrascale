use super::*;
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::Duration;

#[derive(Default)]
struct TestPorts {
    now: AtomicU64,
    sent: AtomicU64,
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
        cursor: u64,
        limit: usize,
    ) -> Result<Vec<Publication>, StorageError> {
        Ok(if cursor < 7 && limit > 0 {
            vec![Publication { revision: 7, event_id: "event-7".into(), body: b"payload".to_vec() }]
        } else {
            Vec::new()
        })
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
        Ok(())
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
    assert_eq!(first.dispatch(&scope, 0).await.expect("initial dispatch").attempted, 1);
    assert_eq!(first.dispatch(&scope, 7).await.expect("not due").attempted, 0);
    drop(first);
    let Ok(engine) = Arc::try_unwrap(engine) else { panic!("sole engine") };
    engine.shutdown().expect("shutdown");

    let engine = open();
    let reopened = service(&engine, &ports);
    ports.now.store(101, Ordering::SeqCst);
    let retry = reopened.dispatch(&scope, 7).await.expect("retry with advanced cursor");
    assert_eq!(retry.publications, 0);
    assert_eq!(retry.attempted, 1);
    assert_eq!(ports.sent.load(Ordering::SeqCst), 2);
    ports.now.store(100 + RETRY_HORIZON_SECONDS, Ordering::SeqCst);
    let expired = reopened.dispatch(&scope, 7).await.expect("expire without publication");
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
    let publication =
        Publication { revision: 7, event_id: "event-7".into(), body: b"payload".to_vec() };
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
            assert_eq!(journal.page(&foreign, 0, limit), Err(StorageError::Conflict));
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
