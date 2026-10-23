#[path = "support/project_http.rs"]
mod http;

use http::{reply, Fixture, PROJECT};
use serde_json::{json, Value};
use terrascale::v1::Error;

#[tokio::test]
async fn ttl_methods_set_read_and_clear_the_expiry_path() {
    let fixture = Fixture::new(vec![
        reply(200, r#"{"path":"/expiresAt"}"#),
        reply(200, r#"{"path":"/expiresAt"}"#),
        reply(200, r#"{"path":null}"#),
        reply(204, ""),
    ])
    .await;
    let items = fixture.client().database("orders").collection("items");
    items.set_ttl("/expiresAt").await.unwrap();
    assert_eq!(
        items.get_ttl().await.unwrap().as_deref(),
        Some("/expiresAt")
    );
    assert_eq!(items.get_ttl().await.unwrap(), None);
    items.clear_ttl().await.unwrap();

    let requests = fixture.requests.lock().unwrap();
    let path = format!("/v1/projects/{PROJECT}/databases/orders/collections/items/ttl");
    let path = path.as_str();
    let seen: Vec<_> = requests
        .iter()
        .map(|r| (r.method.as_str(), r.uri.as_str()))
        .collect();
    assert_eq!(
        seen,
        [
            ("PUT", path),
            ("GET", path),
            ("GET", path),
            ("DELETE", path)
        ]
    );
    assert_eq!(
        serde_json::from_slice::<Value>(&requests[0].body).unwrap(),
        json!({"path":"/expiresAt"})
    );
    assert!(requests[1..].iter().all(|r| r.body.is_empty()));
}

#[tokio::test]
async fn ttl_rejects_non_pointer_paths_and_maps_server_errors() {
    let fixture = Fixture::new(vec![reply(
        404,
        r#"{"error":{"code":"unknown_collection","message":"no such collection"}}"#,
    )])
    .await;
    let items = fixture.client().database("orders").collection("items");
    let deep = "/a".repeat(17);
    for bad in ["expiresAt", "", deep.as_str()] {
        let err = items.set_ttl(bad).await.unwrap_err();
        assert!(matches!(err, Error::InvalidInput(_)), "{err:?}");
    }
    assert!(items.get_ttl().await.is_err());
    assert_eq!(fixture.requests.lock().unwrap().len(), 1);
}
