use super::*;
use std::collections::{BTreeMap, VecDeque};
use std::net::IpAddr;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex};

#[derive(Default)]
struct Clock(AtomicU64);

impl WebhookClock for Clock {
    fn now(&self) -> u64 {
        self.0.load(Ordering::SeqCst)
    }
}

/// Exercise the production resolver and policy through durable dispatch, so a
/// retry cannot reuse admission-time DNS or leave a rejected target pending.
#[tokio::test]
async fn native_delivery_re_resolves_and_rejects_private_retry_target() {
    let answers = Arc::new(Mutex::new(VecDeque::from([
        Ok(vec!["1.1.1.1".parse::<IpAddr>().expect("public IP")]),
        Err(DeliveryError::Retryable),
        Ok(vec!["1.1.1.1".parse().expect("public IP"), "10.0.0.1".parse().expect("private IP")]),
    ])));
    let resolver_answers = Arc::clone(&answers);
    let tls = Arc::new(
        rustls::ClientConfig::builder_with_provider(Arc::new(
            rustls::crypto::aws_lc_rs::default_provider(),
        ))
        .with_safe_default_protocol_versions()
        .expect("TLS versions")
        .with_root_certificates(rustls::RootCertStore::empty())
        .with_no_client_auth(),
    );
    let transport = Arc::new(https::NativeHttpsTransport::new_for_test(
        tokio::runtime::Handle::current(),
        tls,
        Arc::new(move |_, _| {
            resolver_answers.lock().expect("resolver lock").pop_front().expect("fresh resolution")
        }),
    ));
    exercise_dispatch(Arc::new(HttpsWebhookDelivery::new(transport)), true).await;
    assert!(answers.lock().expect("answers").is_empty());
}

struct RedirectTransport(AtomicU64);

impl HttpsTransport for RedirectTransport {
    fn resolve(&self, _: &str) -> Result<Vec<IpAddr>, DeliveryError> {
        Ok(vec!["1.1.1.1".parse().expect("public IP")])
    }

    fn send_https(
        &self,
        endpoint: &str,
        addresses: &[IpAddr],
        headers: &BTreeMap<String, String>,
        body: &[u8],
    ) -> Result<DeliveryResponse, DeliveryError> {
        assert_eq!(endpoint, "https://hooks.example/events");
        assert_eq!(addresses, &["1.1.1.1".parse::<IpAddr>().expect("public IP")]);
        assert!(headers.contains_key("x-terrascale-webhook-signature"));
        assert_eq!(body, b"payload");
        self.0.fetch_add(1, Ordering::SeqCst);
        Ok(DeliveryResponse { status: 302 })
    }
}

/// Native socket coverage proves Location is not followed; this proves the
/// dispatcher also persists that response as terminal and never retries it.
#[tokio::test]
async fn redirect_delivery_is_terminal_and_not_retried_by_dispatcher() {
    let transport = Arc::new(RedirectTransport(AtomicU64::new(0)));
    exercise_dispatch(Arc::new(HttpsWebhookDelivery::new(Arc::clone(&transport))), false).await;
    assert_eq!(transport.0.load(Ordering::SeqCst), 1);
}

async fn exercise_dispatch(delivery: Arc<dyn WebhookDelivery>, retry_dns: bool) {
    let root = std::env::temp_dir()
        .join(format!("terrascale-webhook-egress-{}", terrabase::identity::generate()));
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
    let ports = Arc::new(delivery_tests::TestPorts::default());
    let clock = Arc::new(Clock::default());
    let service = Arc::new(
        WebhookService::new(
            Arc::new(EngineWebhookStorage::new(Arc::clone(&engine))),
            ports.clone(),
            ports,
            Arc::new(AbuseGuardWebhookGuard::new(Arc::new(AbuseGuards::new()))),
            delivery,
            clock.clone(),
            "GRU",
        )
        .expect("service"),
    );
    let creator = Arc::clone(&service);
    let create_scope = scope.clone();
    let endpoint = tokio::task::spawn_blocking(move || {
        creator.create(
            &create_scope,
            &CreateWebhook { endpoint: "https://hooks.example/events".into() },
        )
    })
    .await
    .expect("create worker")
    .expect("create");
    let first = service.dispatch(&scope, None).await.expect("initial dispatch");
    assert_eq!(first.attempted, 1);
    let log = service.delivery_log(&scope, &endpoint.id, 10).expect("initial log");
    let row = &log.deliveries[0];
    assert_eq!(row.terminal, !retry_dns);
    if retry_dns {
        clock.0.store(row.next_attempt_at, Ordering::SeqCst);
        let retry = service.dispatch(&scope, first.cursor).await.expect("due retry");
        assert_eq!(retry.publications, 0);
        assert_eq!(retry.attempted, 1);
    }
    let log = service.delivery_log(&scope, &endpoint.id, 10).expect("terminal log");
    assert_eq!(log.deliveries.len(), 1);
    let row = &log.deliveries[0];
    assert!(row.terminal);
    assert!(!row.delivered);
    assert_eq!(row.attempts, if retry_dns { 2 } else { 1 });
    assert_eq!(row.status, if retry_dns { None } else { Some(302) });
    clock.0.store(RETRY_HORIZON_SECONDS, Ordering::SeqCst);
    assert_eq!(service.dispatch(&scope, first.cursor).await.expect("terminal skip").attempted, 0);
    drop(service);
    let Ok(engine) = Arc::try_unwrap(engine) else { panic!("sole engine") };
    engine.shutdown().expect("shutdown");
    std::fs::remove_dir_all(root).expect("cleanup");
}
