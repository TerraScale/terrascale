#[path = "support/project_http.rs"]
mod http;
use http::{reply, Fixture, DATABASE, PROJECT};
use serde_json::{json, Value};
use terrascale::v1::DatabasePlacement;

#[tokio::test]
async fn canonical_database_routes_preserve_placement_and_management_pagination() {
    let fixture = Fixture::new(vec![
        reply(201, DATABASE),
        reply(
            200,
            &format!(r#"{{"items":[{DATABASE}],"next":"opaque/+ cursor"}}"#),
        ),
        reply(200, DATABASE),
        reply(200, DATABASE),
        reply(201, DATABASE),
        reply(204, ""),
    ])
    .await;
    let client = fixture.client();
    let db = client.database("orders");
    let placement = DatabasePlacement {
        home_region: Some("gru".into()),
        replica_regions: Some(vec!["fra".into()]),
        ..Default::default()
    };
    assert_eq!(
        db.create_with_placement(&placement)
            .await
            .unwrap()
            .replica_regions,
        ["fra"]
    );
    let page = client
        .list_databases(&[("limit", "2".into()), ("after", "opaque/+ cursor".into())])
        .await
        .unwrap();
    assert_eq!(page.next.as_deref(), Some("opaque/+ cursor"));
    assert_eq!(page.items[0].project_id, PROJECT);
    assert_eq!(db.get_database().await.unwrap().name, "orders");
    db.update_database(&DatabasePlacement {
        replica_regions: Some(vec![]),
        ..Default::default()
    })
    .await
    .unwrap();
    db.branch_database("orders_copy").await.unwrap();
    db.delete_database().await.unwrap();
    let requests = fixture.requests.lock().unwrap();
    let root = format!("/v1/projects/{PROJECT}/databases");
    for (request, method, suffix) in [
        (&requests[0], "POST", ""),
        (&requests[1], "GET", "?limit=2&after=opaque%2F%2B+cursor"),
        (&requests[2], "GET", "/orders"),
        (&requests[3], "PATCH", "/orders"),
        (&requests[4], "POST", "/orders/branches"),
        (&requests[5], "DELETE", "/orders"),
    ] {
        assert_eq!(request.method, method);
        assert_eq!(request.uri, format!("{root}{suffix}"));
        assert_eq!(request.headers["authorization"], "Bearer fixture-token");
        assert!(!request.headers.contains_key("idempotency-key"));
    }
    assert_eq!(
        serde_json::from_slice::<Value>(&requests[0].body).unwrap(),
        json!({"name":"orders","homeRegion":"gru","replicaRegions":["fra"]})
    );
    assert_eq!(
        serde_json::from_slice::<Value>(&requests[3].body).unwrap(),
        json!({"replicaRegions":[]})
    );
    assert_eq!(
        serde_json::from_slice::<Value>(&requests[4].body).unwrap(),
        json!({"name":"orders_copy"})
    );
}

#[tokio::test]
async fn all_regions_placement_is_sent_only_when_set_and_read_with_default() {
    let all = DATABASE.replace(
        r#""replicaRegions":["fra"]"#,
        r#""replicaRegions":["fra"],"allRegions":true"#,
    );
    let fixture = Fixture::new(vec![
        reply(201, &all),
        reply(200, DATABASE),
        reply(200, DATABASE),
    ])
    .await;
    let client = fixture.client();
    let db = client.database("orders");
    assert!(
        db.create_with_placement(&DatabasePlacement {
            all_regions: Some(true),
            ..Default::default()
        })
        .await
        .unwrap()
        .all_regions
    );
    assert!(!db.get_database().await.unwrap().all_regions);
    db.update_database(&DatabasePlacement {
        all_regions: Some(true),
        ..Default::default()
    })
    .await
    .unwrap();
    let requests = fixture.requests.lock().unwrap();
    assert_eq!(
        serde_json::from_slice::<Value>(&requests[0].body).unwrap(),
        json!({"name":"orders","allRegions":true})
    );
    assert_eq!(
        serde_json::from_slice::<Value>(&requests[2].body).unwrap(),
        json!({"allRegions":true})
    );
}

#[tokio::test]
async fn management_errors_keep_evidence_and_uncertain_mutations_are_not_replayed() {
    for (status, code) in [
        (401, "unauthorized"),
        (403, "forbidden_scope"),
        (404, "unknown_database"),
        (409, "database_replicated"),
        (421, "wrong_region"),
        (503, "write_outcome_unknown"),
    ] {
        let body = json!({"error":{"code":code,"message":"fixture refusal"}}).to_string();
        let fixture = Fixture::new(vec![reply(status, &body)]).await;
        let error = fixture
            .client()
            .database("orders")
            .delete_database()
            .await
            .unwrap_err();
        assert_eq!(error.status(), Some(status));
        assert_eq!(error.code(), Some(code));
        assert_eq!(error.raw_body(), Some(body.as_bytes()));
        assert_eq!(error.request_id(), Some("parity-fixture"));
        assert_eq!(error.is_uncertain(), status == 503);
        assert_eq!(fixture.requests.lock().unwrap().len(), 1);
    }
}

#[tokio::test]
async fn management_rejects_bad_names_ids_and_unexpected_acknowledgements() {
    let fixture = Fixture::new(vec![
        reply(200, DATABASE),
        reply(
            200,
            &DATABASE.replace("01ARZ3NDEKTSV4RRFFQ69G5FAW", "db_prefixed"),
        ),
    ])
    .await;
    assert!(fixture
        .client()
        .database("orders")
        .delete_database()
        .await
        .unwrap_err()
        .is_uncertain());
    assert!(fixture
        .client()
        .database("orders")
        .get_database()
        .await
        .is_err());
    assert!(fixture
        .client()
        .database("Bad Name")
        .get_database()
        .await
        .is_err());
    assert!(fixture
        .client()
        .database("orders")
        .branch_database("../escape")
        .await
        .is_err());
    assert_eq!(fixture.requests.lock().unwrap().len(), 2);
}

// HTTP fixtures validate the SDK contract, not server-side rename invariants.
#[tokio::test]
async fn rename_uses_immutable_id_and_preserves_returned_metadata() {
    let renamed = DATABASE.replace("orders", "orders_new");
    let document = json!({"id":"01ARZ3NDEKTSV4RRFFQ69G5FAW","version":"tbv1-00000000000000000000000000000000","state":"value","document":{"kept":true}}).to_string();
    let fixture = Fixture::new(vec![
        reply(200, &renamed),
        reply(200, &renamed),
        reply(200, &document),
    ])
    .await;
    let client = fixture.client();
    let id = "01ARZ3NDEKTSV4RRFFQ69G5FAW";
    let result = client.rename_database(id, "orders_new").await.unwrap();
    assert_eq!(result.id, id);
    assert_eq!(result.project_id, PROJECT);
    assert_eq!(result.name, "orders_new");
    assert_eq!(result.home_region, "gru");
    assert_eq!(result.replica_regions, ["fra"]);
    assert_eq!(
        client.database(id).get_database().await.unwrap().name,
        "orders_new"
    );
    let doc = client
        .database(id)
        .collection("orders")
        .get::<Value>(id.parse().unwrap())
        .await
        .unwrap();
    assert_eq!(doc.value(), Some(&json!({"kept":true})));
    let requests = fixture.requests.lock().unwrap();
    assert_eq!(requests.len(), 3);
    assert_eq!(requests[1].method, "GET");
    assert_eq!(
        requests[1].uri,
        format!("/v1/projects/{PROJECT}/databases/{id}")
    );
    assert_eq!(requests[2].method, "GET");
    assert_eq!(
        requests[2].uri,
        format!(
            "/v1/projects/{PROJECT}/databases/{id}/collections/orders/documents/by-key?id={id}"
        )
    );
    assert_eq!(requests[0].method, "PATCH");
    assert_eq!(
        requests[0].uri,
        format!("/v1/projects/{PROJECT}/databases/{id}")
    );
    assert_eq!(requests[0].headers["authorization"], "Bearer fixture-token");
    assert_eq!(requests[0].headers["content-type"], "application/json");
    assert!(!requests[0].headers.contains_key("idempotency-key"));
    assert_eq!(
        serde_json::from_slice::<Value>(&requests[0].body).unwrap(),
        json!({"name":"orders_new"})
    );
}

#[tokio::test]
async fn rename_preserves_errors_without_replaying_and_rejects_bad_inputs() {
    for (status, code) in [
        (400, "invalid_request"),
        (401, "unauthorized"),
        (403, "forbidden_scope"),
        (404, "not_found"),
        (409, "name_taken"),
        (421, "wrong_region"),
        (503, "storage_unavailable"),
    ] {
        let body = json!({"error":{"code":code,"message":"fixture refusal"}}).to_string();
        let fixture = Fixture::new(vec![reply(status, &body)]).await;
        // Use the default retry policy to prove PATCH is not replayed on 503.
        let client = terrascale::v1::Client::builder(&fixture.url, "fixture-token")
            .project(PROJECT)
            .build()
            .unwrap();
        let error = client
            .rename_database("01ARZ3NDEKTSV4RRFFQ69G5FAW", "orders_new")
            .await
            .unwrap_err();
        assert_eq!(error.status(), Some(status));
        assert_eq!(error.code(), Some(code));
        assert_eq!(fixture.requests.lock().unwrap().len(), 1);
    }
    let fixture = Fixture::new(vec![]).await;
    let client = fixture.client();
    for (id, name) in [
        ("orders", "orders_new"),
        (PROJECT, "Bad Name"),
        (PROJECT, ""),
    ] {
        assert!(client.rename_database(id, name).await.is_err());
    }
    for selector in [
        "",
        "../escape",
        "Bad Name",
        "81ARZ3NDEKTSV4RRFFQ69G5FAW",
        "01arz3ndektsv4rrffq69g5faw",
    ] {
        let db = client.database(selector);
        assert!(db.get_database().await.is_err());
        assert!(db
            .collection("orders")
            .get::<Value>(PROJECT.parse().unwrap())
            .await
            .is_err());
    }
    assert!(client.database(PROJECT).create().await.is_err());
    assert!(client
        .database(PROJECT)
        .create_with_placement(&DatabasePlacement::default())
        .await
        .is_err());
    assert!(client.rename_database(PROJECT, PROJECT).await.is_err());
    assert!(fixture.requests.lock().unwrap().is_empty());
}
