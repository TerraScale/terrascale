//! Retry classification of admission and gateway failures (HTTP fixtures).
#[path = "support/project_http.rs"]
mod http;
use http::{reply, Fixture, PROJECT};
use std::time::Duration;
use terrascale::v1::{Client, Error, RetryPolicy};

const NOT_FOUND: &str = r#"{"error":{"code":"not_found","message":"fixture"}}"#;
const GATEWAY: &str = "<html><body>Bad Gateway</body></html>";

fn client(fixture: &Fixture) -> Client {
    Client::builder(&fixture.url, "fixture-token")
        .project(PROJECT)
        .retry_policy(RetryPolicy {
            base_delay: Duration::ZERO,
            max_delay: Duration::ZERO,
            ..RetryPolicy::default()
        })
        .build()
        .unwrap()
}

async fn read(fixture: &Fixture) -> Error {
    client(fixture)
        .database("orders")
        .collection("items")
        .get_collection()
        .await
        .unwrap_err()
}

async fn write(fixture: &Fixture) -> Error {
    client(fixture)
        .database("orders")
        .create_collection("items")
        .await
        .unwrap_err()
}

#[tokio::test]
async fn admission_refusals_are_retried_for_reads_and_writes() {
    for code in ["rate_limited", "tenant_busy"] {
        let body = format!(
            r#"{{"error":{{"code":"{code}","message":"organization request budget exceeded"}}}}"#
        );
        for mutation in [false, true] {
            let fixture = Fixture::new(vec![reply(429, &body), reply(404, NOT_FOUND)]).await;
            let error = if mutation {
                write(&fixture).await
            } else {
                read(&fixture).await
            };
            assert_eq!(error.status(), Some(404), "{code} {mutation}");
            assert_eq!(fixture.requests.lock().unwrap().len(), 2, "{code}");
        }
    }
    // An uncoded (edge) 429 is retried only for replay-safe requests.
    let fixture = Fixture::new(vec![reply(429, ""), reply(404, NOT_FOUND)]).await;
    assert_eq!(read(&fixture).await.status(), Some(404));
    assert_eq!(fixture.requests.lock().unwrap().len(), 2);
    let fixture = Fixture::new(vec![reply(429, "")]).await;
    assert_eq!(write(&fixture).await.code(), Some("http_429"));
    assert_eq!(fixture.requests.lock().unwrap().len(), 1);
}

#[tokio::test]
async fn quota_refusals_are_terminal() {
    let body = r#"{"error":{"code":"operation_allowance","message":"fixture allowance"}}"#;
    for mutation in [false, true] {
        let fixture = Fixture::new(vec![reply(429, body)]).await;
        let error = if mutation {
            write(&fixture).await
        } else {
            read(&fixture).await
        };
        assert_eq!(error.status(), Some(429));
        assert_eq!(error.code(), Some("operation_allowance"));
        assert_eq!(fixture.requests.lock().unwrap().len(), 1);
    }
}

#[tokio::test]
async fn uncoded_gateway_failures_are_retried_only_for_replay_safe_requests() {
    for status in [502, 503, 504] {
        let fixture = Fixture::new(vec![reply(status, GATEWAY), reply(404, NOT_FOUND)]).await;
        assert_eq!(read(&fixture).await.status(), Some(404), "{status}");
        assert_eq!(fixture.requests.lock().unwrap().len(), 2);

        let fixture = Fixture::new(vec![reply(status, GATEWAY)]).await;
        let error = write(&fixture).await;
        assert_eq!(error.status(), Some(status));
        assert_eq!(error.code(), Some(format!("http_{status}").as_str()));
        assert!(error.is_uncertain());
        assert_eq!(fixture.requests.lock().unwrap().len(), 1);
    }
    // A coded 503 outside the transient set stays terminal even for reads.
    let fixture = Fixture::new(vec![reply(
        503,
        r#"{"error":{"code":"write_outcome_unknown","message":"fixture"}}"#,
    )])
    .await;
    assert_eq!(read(&fixture).await.status(), Some(503));
    assert_eq!(fixture.requests.lock().unwrap().len(), 1);
}
