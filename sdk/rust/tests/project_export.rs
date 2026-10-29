#[path = "support/project_http.rs"]
mod http;
use http::{reply, Fixture, PROJECT};
use serde_json::value::RawValue;
use terrascale::v1::Client;
#[tokio::test]
async fn export_preserves_exact_document_json_and_explicit_resume() {
    let text=format!("{{\"collection\":\"items\",\"id\":\"{PROJECT}\",\"document\":{{\"n\":9007199254740993,\"decimal\":1.00000000000000001}}}}\n");
    let fixture = Fixture::with_type(
        vec![reply(200, &text), reply(200, "")],
        "application/x-ndjson",
    )
    .await;
    let client = fixture.client();
    let db = client.database("orders");
    let docs = db.export_database::<Box<RawValue>>(None).await.unwrap();
    assert_eq!(docs.len(), 1);
    assert_eq!(
        docs[0].document.get(),
        r#"{"n":9007199254740993,"decimal":1.00000000000000001}"#
    );
    assert!(db
        .export_database::<Box<RawValue>>(Some((&docs[0].collection, docs[0].id)))
        .await
        .unwrap()
        .is_empty());
    let seen = fixture.requests.lock().unwrap();
    assert_eq!(
        seen[0].uri,
        format!("/v1/projects/{PROJECT}/databases/orders/export")
    );
    assert_eq!(seen[1].uri,format!("/v1/projects/{PROJECT}/databases/orders/export?after_collection=items&after_id={PROJECT}"));
    assert_eq!(seen[0].headers["authorization"], "Bearer fixture-token");
}
#[tokio::test]
async fn incomplete_or_malformed_exports_never_return_partial_documents() {
    for text in [
        format!("{{\"collection\":\"items\",\"id\":\"{PROJECT}\",\"document\":{{}}}}\n{{"),
        "{}\n".into(),
        "\n".into(),
    ] {
        let fixture = Fixture::new(vec![reply(200, &text)]).await;
        let error = fixture
            .client()
            .database("orders")
            .export_database::<Box<RawValue>>(None)
            .await
            .unwrap_err();
        assert_eq!(error.status(), Some(200));
        assert!(!error.is_uncertain());
    }
}
#[tokio::test]
async fn export_bounds_and_tenant_scope_are_enforced() {
    let fixture = Fixture::new(vec![reply(200, &"x".repeat(1025))]).await;
    let client = Client::builder(&fixture.url, "fixture-token")
        .project(PROJECT)
        .max_response_bytes(1024)
        .build()
        .unwrap();
    assert!(client
        .database("orders")
        .export_database::<Box<RawValue>>(None)
        .await
        .is_err());
    assert!(client
        .database("../bad")
        .export_database::<Box<RawValue>>(None)
        .await
        .is_err());
    assert!(client
        .database("orders")
        .export_database::<Box<RawValue>>(Some(("../bad", PROJECT.parse().unwrap())))
        .await
        .is_err());
    let platform = Client::for_platform(&fixture.url, "fixture-token")
        .build()
        .unwrap();
    assert!(platform
        .database("orders")
        .export_database::<Box<RawValue>>(None)
        .await
        .is_err());
    assert_eq!(fixture.requests.lock().unwrap().len(), 1);
}
