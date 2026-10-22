#[path = "support/project_http.rs"]
mod http;
use http::{reply, Fixture};
use serde_json::{json, Value};
use terrascale::v1::{Client, RetryPolicy};
const STATUS: &str = r#"{"status":"unavailable","version":"0.0.1","region":"gru","storageFormat":"1","revision":"fixture","machine":"fixture-machine"}"#;

#[tokio::test]
async fn public_probes_are_anonymous_unscoped_and_keep_503_status_bodies() {
    let replication = r#"{"region":"gru","status":"degraded","maxLagSeconds":2.5,"peers":[{"region":"fra","status":"degraded","lagSeconds":null}]}"#;
    let fixture = Fixture::new(vec![
        reply(200, &STATUS.replace("unavailable", "ok")),
        reply(503, STATUS),
        reply(503, STATUS),
        reply(200, replication),
    ])
    .await;
    let client = fixture.client();
    assert_eq!(client.healthz().await.unwrap().status, 200);
    let ready = client.readyz().await.unwrap();
    assert_eq!(ready.status, 503);
    assert_eq!(ready.body, serde_json::from_str::<Value>(STATUS).unwrap());
    assert_eq!(client.backup_health().await.unwrap().status, 503);
    assert_eq!(
        client.replication_health().await.unwrap().body,
        serde_json::from_str::<Value>(replication).unwrap()
    );
    for (request, path) in fixture.requests.lock().unwrap().iter().zip([
        "/healthz",
        "/readyz",
        "/backupz",
        "/health/replication",
    ]) {
        assert_eq!(request.uri, path);
        assert_eq!(request.method, "GET");
        assert!(!request.headers.contains_key("authorization"));
        assert!(!request.headers.contains_key("idempotency-key"));
    }
}

#[tokio::test]
async fn platform_routes_accept_user_bearers_and_preserve_redacted_raw_key_documents() {
    let database = r#"{"items":[{"name":"ts-auth","id":"00000000000000000000000003","homeRegion":"gru","replicaRegions":["fra"]}]}"#;
    let collections = r#"{"items":[{"id":"01ARZ3NDEKTSV4RRFFQ69G5FAY","name":"users","created_at":"123"}],"next_cursor":null}"#;
    let doc = r#"{"id":"01ARZ3NDEKTSV4RRFFQ69G5FAV","version":"tbv1-00000000000000000000000000000000","state":"value","document":{"name":"Ada","password":"[redacted]"}}"#;
    let page = format!(r#"{{"items":[{doc}],"next_cursor":"opaque/+"}}"#);
    let fixture = Fixture::new(vec![
        reply(200, database),
        reply(200, collections),
        reply(200, &page),
        reply(200, doc),
        reply(
            403,
            r#"{"error":{"code":"forbidden","message":"platform admin required"}}"#,
        ),
    ])
    .await;
    let client = Client::for_platform(&fixture.url, "fixture-user-access-token")
        .retry_policy(RetryPolicy::none())
        .build()
        .unwrap();
    assert_eq!(
        client.list_platform_databases::<Value>().await.unwrap(),
        serde_json::from_str::<Value>(database).unwrap()
    );
    assert_eq!(
        client
            .list_platform_collections::<Value>("ts-auth", &[("limit", "2".into())])
            .await
            .unwrap(),
        serde_json::from_str::<Value>(collections).unwrap()
    );
    assert_eq!(
        client
            .list_platform_documents::<Value>("ts-auth", "users", &[("cursor", "opaque/+".into())])
            .await
            .unwrap(),
        serde_json::from_str::<Value>(&page).unwrap()
    );
    assert_eq!(
        client
            .get_platform_document::<Value>(
                "ts-auth",
                "users",
                "01ARZ3NDEKTSV4RRFFQ69G5FAV".parse().unwrap()
            )
            .await
            .unwrap(),
        serde_json::from_str::<Value>(doc).unwrap()
    );
    let error = client.list_platform_databases::<Value>().await.unwrap_err();
    assert_eq!(error.code(), Some("forbidden"));
    assert_eq!(error.request_id(), Some("parity-fixture"));
    let requests = fixture.requests.lock().unwrap();
    for (i, path) in [
        "/v1/platform/databases",
        "/v1/platform/databases/ts-auth/collections?limit=2",
        "/v1/platform/databases/ts-auth/collections/users/documents?cursor=opaque%2F%2B",
        "/v1/platform/databases/ts-auth/collections/users/documents/by-key?id=01ARZ3NDEKTSV4RRFFQ69G5FAV",
    ]
    .iter()
    .enumerate()
    {
        assert_eq!(requests[i].uri, *path);
        assert_eq!(
            requests[i].headers["authorization"],
            "Bearer fixture-user-access-token"
        );
        assert!(requests[i].body.is_empty());
    }
    assert_eq!(requests.len(), 5);
    assert_eq!(
        serde_json::from_str::<Value>(doc).unwrap()["document"]["password"],
        json!("[redacted]")
    );
}
