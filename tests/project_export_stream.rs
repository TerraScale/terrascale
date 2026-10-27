#[path = "support/project_http.rs"]
mod http;
use futures_util::StreamExt;
use http::{reply, Fixture, PROJECT};
use serde_json::value::RawValue;
use terrascale::v1::Client;

fn line(collection: &str, document: &str) -> String {
    format!("{{\"collection\":\"{collection}\",\"id\":\"{PROJECT}\",\"document\":{document}}}\n")
}

#[tokio::test]
async fn stream_yields_exact_documents_and_resumes_explicitly() {
    let text = line("items", r#"{"n":9007199254740993}"#) + &line("users", "{}");
    let fixture = Fixture::with_type(
        vec![reply(200, &text), reply(200, "")],
        "application/x-ndjson",
    )
    .await;
    let client = fixture.client();
    let db = client.database("orders");
    let docs: Vec<_> = db
        .stream_export::<Box<RawValue>>(None)
        .await
        .unwrap()
        .collect()
        .await;
    let docs: Vec<_> = docs.into_iter().map(Result::unwrap).collect();
    assert_eq!(docs.len(), 2);
    assert_eq!(docs[0].document.get(), r#"{"n":9007199254740993}"#);
    assert_eq!(docs[1].collection, "users");
    let last = &docs[1];
    let resumed = db
        .stream_export::<Box<RawValue>>(Some((&last.collection, last.id)))
        .await
        .unwrap();
    assert_eq!(resumed.count().await, 0);
    let seen = fixture.requests.lock().unwrap();
    assert_eq!(
        seen[0].uri,
        format!("/v1/projects/{PROJECT}/databases/orders/export")
    );
    assert_eq!(
        seen[1].uri,
        format!("/v1/projects/{PROJECT}/databases/orders/export?after_collection=users&after_id={PROJECT}")
    );
    assert_eq!(seen[0].headers["authorization"], "Bearer fixture-token");
    assert_eq!(seen[0].headers["accept"], "application/x-ndjson");
}

#[tokio::test]
async fn malformed_or_truncated_lines_end_with_one_error_after_complete_items() {
    for tail in ["{", "{}\n", "\n"] {
        let text = line("items", "{}") + tail;
        let fixture = Fixture::with_type(vec![reply(200, &text)], "application/x-ndjson").await;
        let items: Vec<_> = fixture
            .client()
            .database("orders")
            .stream_export::<Box<RawValue>>(None)
            .await
            .unwrap()
            .collect()
            .await;
        assert_eq!(items.len(), 2, "tail {tail:?}");
        assert!(items[0].is_ok());
        let error = items[1].as_ref().unwrap_err();
        assert_eq!(error.status(), Some(200));
        assert!(!error.is_uncertain());
    }
}

#[tokio::test]
async fn refusals_bounds_and_tenant_scope_are_enforced() {
    let fixture = Fixture::new(vec![
        reply(404, r#"{"error":"not_found"}"#),
        reply(200, &"x".repeat(1025)),
    ])
    .await;
    let refused = fixture
        .client()
        .database("orders")
        .stream_export::<Box<RawValue>>(None)
        .await
        .err()
        .unwrap();
    assert_eq!(refused.status(), Some(404));
    let client = Client::builder(&fixture.url, "fixture-token")
        .project(PROJECT)
        .max_response_bytes(1024)
        .build()
        .unwrap();
    let mut oversized = Box::pin(
        client
            .database("orders")
            .stream_export::<Box<RawValue>>(None)
            .await
            .unwrap(),
    );
    assert!(oversized.next().await.unwrap().is_err());
    assert!(oversized.next().await.is_none());
    assert!(client
        .database("../bad")
        .stream_export::<Box<RawValue>>(None)
        .await
        .is_err());
    assert!(client
        .database("orders")
        .stream_export::<Box<RawValue>>(Some(("../bad", PROJECT.parse().unwrap())))
        .await
        .is_err());
    let platform = Client::for_platform(&fixture.url, "fixture-token")
        .build()
        .unwrap();
    assert!(platform
        .database("orders")
        .stream_export::<Box<RawValue>>(None)
        .await
        .is_err());
    assert_eq!(fixture.requests.lock().unwrap().len(), 2);
}
