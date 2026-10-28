#[path = "support/project_http.rs"]
mod http;
use http::{reply, Fixture, PROJECT};
use serde_json::{json, Value};

const INFERRED: &str = r#"{"collection":"items","documents":1,"sampledDocuments":1,"fields":[{"path":"email","present":1,"types":{"string":1}}],"uniqueConstraints":[]}"#;
const CONSTRAINTS: &str = r#"{"constraints":[{"name":"field_82244417f956ac7c","fields":["email"],"normalization":"lowercase"},{"name":"tenant_handle","fields":["tenant","handle"],"normalization":"none"}]}"#;
const INDEX: &str =
    r#"{"name":"items_score","collection":"items","fields":["score"],"state":"active"}"#;
const RECOMMENDATIONS: &str = r#"{"items":[{"id":"items:score","collection":"items","fields":["score"],"reason":"1 queries filtered or sorted on this field without an index","queries":1}]}"#;
const DECLARED: &str =
    r#"{"schema":{"type":"object"},"dialect":"https://json-schema.org/draft/2020-12/schema"}"#;
fn decoded(value: &str) -> Value {
    serde_json::from_str(value).unwrap()
}

#[tokio::test]
async fn schema_and_index_routes_preserve_every_producer_field() {
    let collection_list = r#"{"items":[{"name":"items","documents":1,"bytes":14}]}"#;
    let index_list = format!(r#"{{"items":[{INDEX}]}}"#);
    let applied = format!(r#"{{"status":"applied","index":{INDEX}}}"#);
    let existing = format!(r#"{{"status":"already-applied","index":{INDEX}}}"#);
    let replies = vec![
        reply(200, collection_list),
        reply(200, INFERRED),
        reply(200, CONSTRAINTS),
        reply(200, &index_list),
        reply(201, INDEX),
        reply(204, ""),
        reply(200, RECOMMENDATIONS),
        reply(201, &applied),
        reply(200, DECLARED),
        reply(200, DECLARED),
        reply(200, r#"{"schema":null}"#),
        reply(200, &existing),
    ];
    let fixture = Fixture::new(replies).await;
    let db = fixture.client().database("orders");
    let request = json!({"fields":[{"name":"email","unique":true,"normalization":"lowercase"}],"indexes":[{"name":"tenant_handle","fields":["tenant","handle"],"unique":true,"normalization":"none"}]});
    assert_eq!(
        db.get_database_schema::<Value>(&[]).await.unwrap(),
        decoded(collection_list)
    );
    assert_eq!(
        db.get_collection_schema::<Value>("items").await.unwrap(),
        decoded(INFERRED)
    );
    assert_eq!(
        db.declare_collection_schema::<Value>("items", &request)
            .await
            .unwrap(),
        decoded(CONSTRAINTS)
    );
    assert_eq!(
        db.list_indexes::<Value>(&[("collection", "items".into())])
            .await
            .unwrap(),
        decoded(&index_list)
    );
    let index_request = json!({"name":"items_score","collection":"items","fields":["score"]});
    assert_eq!(
        db.create_index::<Value>(&index_request).await.unwrap(),
        decoded(INDEX)
    );
    db.drop_index("items_score").await.unwrap();
    assert_eq!(
        db.list_index_recommendations::<Value>().await.unwrap(),
        decoded(RECOMMENDATIONS)
    );
    assert_eq!(
        db.apply_index_recommendation::<Value>("items:score")
            .await
            .unwrap(),
        decoded(&applied)
    );
    let coll = db.collection("items");
    assert_eq!(coll.get_schema::<Value>().await.unwrap(), decoded(DECLARED));
    assert_eq!(
        coll.put_schema::<Value>(&json!({"type":"object"}))
            .await
            .unwrap(),
        decoded(DECLARED)
    );
    assert_eq!(
        coll.delete_schema::<Value>().await.unwrap(),
        json!({"schema":null})
    );
    assert_eq!(
        db.apply_index_recommendation::<Value>("items:score")
            .await
            .unwrap(),
        decoded(&existing)
    );
    let requests = fixture.requests.lock().unwrap();
    for (i, method, suffix) in [
        (0, "GET", "schema"),
        (1, "GET", "schema/items"),
        (2, "PUT", "schema/items"),
        (3, "GET", "indexes?collection=items"),
        (4, "POST", "indexes"),
        (5, "DELETE", "indexes/items_score"),
        (6, "GET", "index-recommendations"),
        (7, "POST", "index-recommendations/items:score/apply"),
        (8, "GET", "collections/items/schema"),
        (9, "PUT", "collections/items/schema"),
        (10, "DELETE", "collections/items/schema"),
        (11, "POST", "index-recommendations/items:score/apply"),
    ] {
        assert_eq!(requests[i].method, method);
        assert_eq!(
            requests[i].uri,
            format!("/v1/projects/{PROJECT}/databases/orders/{suffix}")
        );
        assert_eq!(requests[i].headers["authorization"], "Bearer fixture-token");
        assert!(!requests[i].headers.contains_key("idempotency-key"));
    }
    for (i, body) in [
        (2, request),
        (4, index_request),
        (7, json!({"enabled":false})),
        (11, json!({"type":"object"})),
    ] {
        assert_eq!(
            serde_json::from_slice::<Value>(&requests[i].body).unwrap(),
            body
        );
    }
}

#[tokio::test]
async fn tooling_retains_server_denials_and_rejects_bad_path_names() {
    let fixture = Fixture::new(vec![
        reply(
            409,
            r#"{"error":{"code":"unique_violation","message":"duplicates"}}"#,
        ),
        reply(200, "invalid JSON"),
    ])
    .await;
    let db = fixture.client().database("orders");
    let error = db
        .declare_collection_schema::<Value>("items", &json!({"fields":[],"indexes":[]}))
        .await
        .unwrap_err();
    assert_eq!(error.code(), Some("unique_violation"));
    assert_eq!(error.request_id(), Some("parity-fixture"));
    assert!(db
        .create_index::<Value>(&json!({}))
        .await
        .unwrap_err()
        .is_uncertain());
    assert!(db
        .get_collection_schema::<Value>("../escape")
        .await
        .is_err());
    assert!(db
        .collection("Bad Name")
        .get_schema::<Value>()
        .await
        .is_err());
    assert!(db.apply_index_recommendation::<Value>("").await.is_err());
    assert_eq!(fixture.requests.lock().unwrap().len(), 2);
}
