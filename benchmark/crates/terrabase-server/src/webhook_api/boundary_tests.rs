use super::*;
use std::collections::BTreeMap;
use std::net::{IpAddr, Ipv4Addr};
use std::sync::{Arc, Mutex};

/// Exercises the production NativeHttpsTransport resolver path through the
/// HttpsWebhookDelivery policy. A fake WebhookDelivery would bypass this
/// boundary and cannot detect DNS rebinding regressions.
#[test]
fn native_delivery_re_resolves_and_rejects_private_retry_target() {
    let runtime =
        tokio::runtime::Builder::new_current_thread().enable_all().build().expect("runtime");
    let answers = Arc::new(Mutex::new(vec![
        vec![IpAddr::V4(Ipv4Addr::new(10, 0, 0, 1))],
        vec![IpAddr::V4(Ipv4Addr::new(1, 1, 1, 1))],
    ]));
    let resolver_answers = Arc::clone(&answers);
    let tls = Arc::new(
        rustls::ClientConfig::builder()
            .with_root_certificates(rustls::RootCertStore::empty())
            .with_no_client_auth(),
    );
    let transport = Arc::new(https::NativeHttpsTransport::new_for_test(
        runtime.handle().clone(),
        tls,
        Arc::new(move |_, _| {
            resolver_answers.lock().expect("resolver lock").pop().ok_or(DeliveryError::Retryable)
        }),
    ));
    let delivery = HttpsWebhookDelivery::new(transport);
    let endpoint = "https://hooks.example/events";
    runtime.block_on(async {
        tokio::task::spawn_blocking(move || {
            delivery.validate_target(endpoint).expect("initial public resolution");
            assert_eq!(
                delivery.send(endpoint, &BTreeMap::new(), b"body"),
                Err(DeliveryError::TargetRejected),
                "a retry must reject the newly private DNS answer before connecting"
            );
        })
        .await
        .expect("boundary worker");
    });
}
