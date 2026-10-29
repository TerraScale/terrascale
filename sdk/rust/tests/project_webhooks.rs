#[path = "support/project_http.rs"]
mod http;
use http::{reply, Fixture, PROJECT};
use serde_json::{json, Value};
use terrascale::v1::{Client, Error, WebhookDeliveryError, WebhookDeliveryStatus, WebhookStatus};

const HOOK: &str = "01ARZ3NDEKTSV4RRFFQ69G5FAX";
const DELIVERY: &str = "01ARZ3NDEKTSV4RRFFQ69G5FAY";
fn metadata() -> Value {
    json!({"id":HOOK,"url":"https://receiver.example/events","createdAt":"2026-10-23T12:00:00Z","status":"active"})
}

#[tokio::test]
async fn webhook_contract_uses_collection_routes_direct_arrays_and_creation_only_secret() {
    let mut created = metadata();
    created["secret"] = json!("fixture-base64url-key");
    let mut gap = metadata();
    gap["status"] = json!("feed_gap");
    let pending = json!({"id":DELIVERY,"createdAt":"2026-10-23T12:00:01Z","status":"pending","attempts":9007199254740993i64,"lastAttemptAt":null,"nextAttemptAt":null,"httpStatus":null,"errorCode":null});
    let mut failed = pending.clone();
    failed["lastAttemptAt"] = json!("2026-10-23T12:00:02Z");
    failed["nextAttemptAt"] = json!("2026-10-23T12:00:03Z");
    failed["httpStatus"] = json!(503);
    failed["errorCode"] = json!("http_error");
    let fixture = Fixture::new(vec![
        reply(201, &created.to_string()),
        reply(200, &json!([gap]).to_string()),
        reply(200, &json!([pending, failed]).to_string()),
        reply(200, "[]"),
        reply(204, ""),
    ])
    .await;
    let items = fixture.client().database("orders").collection("items");
    let registration = items
        .create_webhook("https://receiver.example/events")
        .await
        .unwrap();
    assert_eq!(registration.webhook.id, HOOK);
    assert_eq!(registration.webhook.created_at, "2026-10-23T12:00:00Z");
    assert_eq!(registration.webhook.status, WebhookStatus::Active);
    assert_eq!(registration.secret, "fixture-base64url-key");
    assert!(!format!("{registration:?}").contains(&registration.secret));
    let hooks = items.list_webhooks(Some(1), Some(HOOK)).await.unwrap();
    assert_eq!(hooks[0].status, WebhookStatus::FeedGap);
    let logs = items
        .list_webhook_deliveries(HOOK, Some(100), Some(DELIVERY))
        .await
        .unwrap();
    assert_eq!(logs[0].status, WebhookDeliveryStatus::Pending);
    assert_eq!(logs[0].attempts, 9007199254740993);
    assert_eq!(
        (
            logs[0].last_attempt_at.as_deref(),
            logs[0].next_attempt_at.as_deref(),
            logs[0].http_status,
            logs[0].error_code
        ),
        (None, None, None, None)
    );
    assert_eq!(logs[1].http_status, Some(503));
    assert_eq!(logs[1].error_code, Some(WebhookDeliveryError::HttpError));
    assert_eq!(
        logs[1].last_attempt_at.as_deref(),
        Some("2026-10-23T12:00:02Z")
    );
    assert_eq!(
        logs[1].next_attempt_at.as_deref(),
        Some("2026-10-23T12:00:03Z")
    );
    assert!(items.list_webhooks(None, None).await.unwrap().is_empty());
    items.delete_webhook(HOOK).await.unwrap();
    let requests = fixture.requests.lock().unwrap();
    let base = format!("/v1/projects/{PROJECT}/databases/orders/collections/items/webhooks");
    for (i, method, suffix) in [
        (0, "POST", "".to_owned()),
        (1, "GET", format!("?limit=1&after={HOOK}")),
        (
            2,
            "GET",
            format!("/{HOOK}/deliveries?limit=100&after={DELIVERY}"),
        ),
        (3, "GET", "".to_owned()),
        (4, "DELETE", format!("/{HOOK}")),
    ] {
        assert_eq!(requests[i].method, method);
        assert_eq!(requests[i].uri, format!("{base}{suffix}"));
        assert_eq!(requests[i].headers["authorization"], "Bearer fixture-token");
        assert!(!requests[i].headers.contains_key("idempotency-key"));
    }
    assert_eq!(
        serde_json::from_slice::<Value>(&requests[0].body).unwrap(),
        json!({"url":"https://receiver.example/events"})
    );
    assert!(requests[1..].iter().all(|r| r.body.is_empty()));
    for code in ["transport_error", "target_rejected"] {
        let mut record = pending.clone();
        record["errorCode"] = json!(code);
        assert!(serde_json::from_value::<terrascale::v1::WebhookDelivery>(record).is_ok());
    }
    let mut delivered = pending;
    delivered["status"] = json!("delivered");
    assert_eq!(
        serde_json::from_value::<terrascale::v1::WebhookDelivery>(delivered)
            .unwrap()
            .status,
        WebhookDeliveryStatus::Delivered
    );
}

#[tokio::test]
async fn invalid_selectors_ids_and_pagination_never_dispatch() {
    let fixture = Fixture::new(vec![]).await;
    let client = fixture.client();
    let items = client.database("orders").collection("items");
    for bad in [
        "hook_prefixed",
        "../deliveries",
        "01arz3ndektsv4rrffq69g5fax",
        "81ARZ3NDEKTSV4RRFFQ69G5FAX",
    ] {
        assert!(items.delete_webhook(bad).await.is_err());
        assert!(items
            .list_webhook_deliveries(bad, None, None)
            .await
            .is_err());
        assert!(items.list_webhooks(None, Some(bad)).await.is_err());
        assert!(items
            .list_webhook_deliveries(HOOK, None, Some(bad))
            .await
            .is_err());
    }
    for limit in [0, 101] {
        assert!(items.list_webhooks(Some(limit), None).await.is_err());
        assert!(items
            .list_webhook_deliveries(HOOK, Some(limit), None)
            .await
            .is_err());
    }
    for collection in [
        client.database("Bad Name").collection("items"),
        client.database("orders").collection("../other"),
    ] {
        assert!(collection
            .create_webhook("https://receiver.example")
            .await
            .is_err());
        assert!(collection.list_webhooks(None, None).await.is_err());
        assert!(collection.delete_webhook(HOOK).await.is_err());
        assert!(collection
            .list_webhook_deliveries(HOOK, None, None)
            .await
            .is_err());
    }
    assert!(fixture.requests.lock().unwrap().is_empty());
}

#[tokio::test]
async fn errors_preserve_http_diagnostics_and_uncertain_creates_are_not_replayed() {
    for status in [400, 401, 403, 404, 409, 503] {
        let body = json!({"error":{"code":"fixture_refusal","message":"refused"}}).to_string();
        let fixture = Fixture::new(vec![reply(status, &body)]).await;
        let client = Client::builder(&fixture.url, "fixture-token")
            .project(PROJECT)
            .build()
            .unwrap();
        let error = client
            .database("orders")
            .collection("items")
            .create_webhook("https://receiver.example/events")
            .await
            .unwrap_err();
        assert_eq!(error.status(), Some(status));
        assert_eq!(error.code(), Some("fixture_refusal"));
        assert_eq!(error.raw_body(), Some([].as_slice()));
        assert_eq!(error.request_id(), Some("parity-fixture"));
        assert_eq!(error.is_uncertain(), status == 503);
        assert_eq!(fixture.requests.lock().unwrap().len(), 1);
    }
    let fixture = Fixture::new(vec![reply(201, &metadata().to_string()), reply(200, &json!([{"id":"hook_prefixed","url":"https://receiver.example","createdAt":"now","status":"active"}]).to_string())]).await;
    let items = fixture.client().database("orders").collection("items");
    assert!(items
        .create_webhook("https://receiver.example")
        .await
        .unwrap_err()
        .is_uncertain());
    let error = items.list_webhooks(None, None).await.unwrap_err();
    assert_eq!(error.status(), Some(200));
    assert_eq!(error.request_id(), Some("parity-fixture"));
    assert!(matches!(error, Error::Response { error, body, .. }
        if matches!(*error, Error::Decode(_)) && !body.is_empty()));
}

const SYNTHETIC_SECRET: &str = "synthetic-signing-key-never-in-diagnostics";
fn assert_creation_redacted(error: &Error) {
    assert_eq!(error.raw_body(), Some([].as_slice()));
    assert!(!format!("{error:?}").contains(SYNTHETIC_SECRET));
    assert!(!error.to_string().contains(SYNTHETIC_SECRET));
    assert_eq!(error.request_id(), Some("parity-fixture"));
}

#[tokio::test]
async fn creation_redacts_malformed_acknowledgements_and_server_error_details() {
    let mut missing_time = metadata();
    missing_time.as_object_mut().unwrap().remove("createdAt");
    missing_time["secret"] = json!(SYNTHETIC_SECRET);
    let mut invalid_status = metadata();
    invalid_status["status"] = json!(SYNTHETIC_SECRET);
    invalid_status["secret"] = json!(SYNTHETIC_SECRET);
    for status in [200, 201] {
        for body in [&missing_time, &invalid_status] {
            let fixture = Fixture::new(vec![reply(status, &body.to_string())]).await;
            let error = fixture
                .client()
                .database("orders")
                .collection("items")
                .create_webhook("https://receiver.example/events")
                .await
                .unwrap_err();
            assert_creation_redacted(&error);
            assert_eq!(error.status(), Some(status));
            assert!(error.is_uncertain());
            assert!(matches!(error, Error::Response { error, .. }
                if matches!(*error, Error::UndecodableAck { .. })));
        }
    }
    let body = json!({"error":{"code":"fixture_refusal","message":SYNTHETIC_SECRET,"secret":SYNTHETIC_SECRET}});
    let fixture = Fixture::new(vec![reply(403, &body.to_string())]).await;
    let error = fixture
        .client()
        .database("orders")
        .collection("items")
        .create_webhook("https://receiver.example/events")
        .await
        .unwrap_err();
    assert_creation_redacted(&error);
    assert_eq!(error.status(), Some(403));
    assert_eq!(error.code(), Some("fixture_refusal"));
    assert!(!error.is_uncertain());
    assert!(matches!(error, Error::Response { error, .. }
        if matches!(*error, Error::Api { message: None, details: None, .. })));
}

#[tokio::test]
async fn creation_redacts_oversized_and_truncated_response_bytes() {
    use std::time::Duration;
    use tokio::io::{AsyncReadExt, AsyncWriteExt};
    let body = format!(
        "{{\"secret\":\"{SYNTHETIC_SECRET}\",\"padding\":\"{}\"}}",
        "x".repeat(512)
    );
    let fixture = Fixture::new(vec![reply(201, &body)]).await;
    let client = Client::builder(&fixture.url, "fixture-token")
        .project(PROJECT)
        .max_response_bytes(128)
        .build()
        .unwrap();
    let error = client
        .database("orders")
        .collection("items")
        .create_webhook("https://receiver.example/events")
        .await
        .unwrap_err();
    assert_creation_redacted(&error);
    assert_eq!(error.status(), Some(201));
    assert!(error.is_uncertain());
    assert!(matches!(error, Error::Response { error, .. }
        if matches!(*error, Error::ResponseTooLarge { .. })));

    let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
    let client = Client::builder(
        format!("http://{}", listener.local_addr().unwrap()),
        "fixture-token",
    )
    .project(PROJECT)
    .build()
    .unwrap();
    let server = tokio::spawn(async move {
        let (mut socket, _) = listener.accept().await.unwrap();
        let mut head = Vec::new();
        while !head.ends_with(b"\r\n\r\n") {
            head.push(socket.read_u8().await.unwrap());
        }
        let length = String::from_utf8_lossy(&head)
            .lines()
            .find_map(|line| {
                line.to_ascii_lowercase()
                    .strip_prefix("content-length:")
                    .map(|value| value.trim().parse::<usize>().unwrap())
            })
            .unwrap();
        socket.read_exact(&mut vec![0; length]).await.unwrap();
        let partial = format!("{{\"secret\":\"{SYNTHETIC_SECRET}\",");
        let response = format!("HTTP/1.1 201 Created\r\nContent-Type: application/json\r\nX-Request-ID: parity-fixture\r\nTransfer-Encoding: chunked\r\n\r\n{:x}\r\n{partial}\r\n", partial.len());
        socket.write_all(response.as_bytes()).await.unwrap();
        socket.shutdown().await.unwrap(); // No terminating chunk: fail after credential bytes.
    });
    let error = tokio::time::timeout(
        Duration::from_secs(2),
        client
            .database("orders")
            .collection("items")
            .create_webhook("https://receiver.example/events"),
    )
    .await
    .unwrap()
    .unwrap_err();
    server.await.unwrap();
    assert_creation_redacted(&error);
    assert_eq!(error.status(), Some(201));
    assert!(error.is_uncertain());
    assert!(matches!(error, Error::Response { error, .. }
        if matches!(*error, Error::Transport(_))));
}

#[tokio::test]
async fn dropping_a_webhook_future_cancels_the_http_request() {
    use std::time::Duration;
    use tokio::io::{AsyncReadExt, AsyncWriteExt};
    let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
    let client = Client::builder(
        format!("http://{}", listener.local_addr().unwrap()),
        "fixture-token",
    )
    .project(PROJECT)
    .build()
    .unwrap();
    let task = tokio::spawn(async move {
        client
            .database("orders")
            .collection("items")
            .list_webhooks(None, None)
            .await
    });
    let server = async {
        let (mut socket, _) = listener.accept().await.unwrap();
        let mut head = Vec::new();
        while !head.ends_with(b"\r\n\r\n") {
            head.push(socket.read_u8().await.unwrap());
        }
        socket
            .write_all(
                b"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 2\r\n\r\n[",
            )
            .await
            .unwrap();
        task.abort();
        assert!(task.await.unwrap_err().is_cancelled());
        assert_eq!(socket.read(&mut [0u8]).await.unwrap(), 0);
    };
    tokio::time::timeout(Duration::from_secs(2), server)
        .await
        .unwrap();
}
