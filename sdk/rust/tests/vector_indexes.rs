#[path = "support/project_http.rs"]
mod http;
use http::{reply, Fixture};
use serde_json::{json, Value};
use terrascale::v1::{Index, IndexKind, IndexRequest, ManagementPage};

// Captured from the public API on 2026-10-26 using Q13's own collection.
const CREATED: &str = include_str!("fixtures/current-vector-index.json");

#[tokio::test]
async fn typed_vector_index_round_trip_preserves_live_metadata() {
    let mut active: Value = serde_json::from_str(CREATED).unwrap();
    active["state"] = json!("active");
    let fixture = Fixture::new(vec![
        reply(201, CREATED),
        reply(200, &json!({"items":[active]}).to_string()),
        reply(204, ""),
    ])
    .await;
    let db = fixture.client().database("orders");
    let body = IndexRequest {
        collection: "rs_q13_core".into(),
        fields: vec!["embedding".into()],
        name: Some("rs_q13_vector_fixture".into()),
        kind: Some(IndexKind::Vector),
        dimensions: Some(2),
    };
    let created: Index = db.create_index(&body).await.unwrap();
    assert_eq!(created.kind, IndexKind::Vector);
    assert_eq!(created.dimensions, Some(2));
    assert_eq!(created.state, "building");
    let page: ManagementPage<Index> = db
        .list_indexes(&[("collection", body.collection.clone())])
        .await
        .unwrap();
    assert_eq!(page.items[0].kind, IndexKind::Vector);
    assert_eq!(page.items[0].dimensions, Some(2));
    assert_eq!(page.items[0].state, "active");
    db.drop_index(&created.name).await.unwrap();
    let requests = fixture.requests.lock().unwrap();
    assert_eq!(
        serde_json::from_slice::<Value>(&requests[0].body).unwrap(),
        json!({
            "collection":"rs_q13_core","fields":["embedding"],
            "name":"rs_q13_vector_fixture","kind":"vector","dimensions":2
        })
    );
    assert_eq!(requests[2].method, "DELETE");
    assert!(requests[2].uri.ends_with("/indexes/rs_q13_vector_fixture"));
}

#[test]
fn dimensions_follow_the_openapi_integer_or_string_contract() {
    let mut body: Value = serde_json::from_str(CREATED).unwrap();
    body["dimensions"] = json!("4096");
    assert_eq!(
        serde_json::from_value::<Index>(body.clone())
            .unwrap()
            .dimensions,
        Some(4096)
    );
    for invalid in [json!(0), json!(4097), json!("02"), json!(2.5), json!(true)] {
        body["dimensions"] = invalid;
        assert!(serde_json::from_value::<Index>(body.clone()).is_err());
    }
}
