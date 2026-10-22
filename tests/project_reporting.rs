#[path = "support/project_http.rs"]
mod http;
use http::{reply, Fixture, PROJECT};
use serde_json::{json, Value};
const SAVED: &str = r#"{"id":"01ARZ3NDEKTSV4RRFFQ69G5FAX","name":"recent","sql":"SELECT * FROM items LIMIT 1","createdAt":"2026-10-22T12:00:00Z","updatedAt":null}"#;
const QUERY_ID: &str = "01ARZ3NDEKTSV4RRFFQ69G5FAX";

#[tokio::test]
async fn saved_queries_preserve_fields_ulid_paths_and_management_cursors() {
    let fixture = Fixture::new(vec![
        reply(201, SAVED),
        reply(200, &format!(r#"{{"items":[{SAVED}],"next":"after/+"}}"#)),
        reply(200, SAVED),
        reply(200, SAVED),
        reply(204, ""),
    ])
    .await;
    let client = fixture.client();
    let body = json!({"name":"recent","sql":"SELECT * FROM items LIMIT 1"});
    let created: Value = client.create_saved_query(&body).await.unwrap();
    assert_eq!(created["id"], QUERY_ID);
    assert_eq!(created["updatedAt"], Value::Null);
    let page: Value = client
        .list_saved_queries(&[("after", "after/+".into()), ("limit", "2".into())])
        .await
        .unwrap();
    assert_eq!(page["next"], "after/+");
    let fetched: Value = client.get_saved_query(QUERY_ID).await.unwrap();
    assert_eq!(fetched, created);
    let updated: Value = client.update_saved_query(QUERY_ID, &body).await.unwrap();
    assert_eq!(updated["sql"], body["sql"]);
    client.delete_saved_query(QUERY_ID).await.unwrap();
    assert!(client
        .get_saved_query::<Value>("qry_prefixed")
        .await
        .is_err());
    let requests = fixture.requests.lock().unwrap();
    assert_eq!(requests.len(), 5);
    for (i, method, suffix) in [
        (0, "POST", "".into()),
        (1, "GET", "?after=after%2F%2B&limit=2".into()),
        (2, "GET", format!("/{QUERY_ID}")),
        (3, "PUT", format!("/{QUERY_ID}")),
        (4, "DELETE", format!("/{QUERY_ID}")),
    ] {
        assert_eq!(requests[i].method, method);
        assert_eq!(
            requests[i].uri,
            format!("/v1/projects/{PROJECT}/saved-queries{suffix}")
        );
        assert_eq!(requests[i].headers["authorization"], "Bearer fixture-token");
        assert!(!requests[i].headers.contains_key("idempotency-key"));
    }
    assert_eq!(
        serde_json::from_slice::<Value>(&requests[0].body).unwrap(),
        body
    );
    assert_eq!(
        serde_json::from_slice::<Value>(&requests[3].body).unwrap(),
        body
    );
}

#[tokio::test]
async fn reporting_keeps_query_ranges_and_arbitrary_response_fields() {
    let period = json!({"from":"2026-10-22T00:00:00Z","to":"2026-10-22T01:00:00Z","granularity":"hour","reads":123,"writes":7});
    let point = json!({"start":"2026-10-22T00:00:00Z","reads":123,"writes":7});
    let database_usage = json!({"databaseId":"01ARZ3NDEKTSV4RRFFQ69G5FAW","storageBytes":8192,"logicalBytes":128,"collections":1,"documents":2,"reads":123,"writes":7,"period":period,"series":[point]});
    let mut item_usage = database_usage.clone();
    item_usage["series"] = json!([]);
    let usage = json!({"projectId":PROJECT,"databases":1,"storageBytes":8192,"logicalBytes":128,"documents":2,"reads":123,"writes":7,"period":period,"series":[point],"items":[item_usage]});
    let bandwidth = json!({"sourceRegion":"gru","destinationRegion":"fra","kind":"delta","bytesSent":512,"bytesReceived":256});
    let analytics_point = json!({"start":"2026-10-22T00:00:00Z","requests":{"GET":123},"operations":{"read":123,"write":7},"errors":1,"latency":{"p50Ms":1.5,"p95Ms":4.5,"p99Ms":null},"storageBytes":8192,"clientBandwidth":{"bytesSent":1000,"bytesReceived":2000},"replicationBandwidth":{"bytesSent":512,"bytesReceived":256}});
    let analytics = json!({"projectId":PROJECT,"range":"1h","from":"2026-10-22T00:00:00Z","to":"2026-10-22T01:00:00Z","bucketSeconds":60,"items":[{"databaseId":"01ARZ3NDEKTSV4RRFFQ69G5FAW","databaseName":"orders","region":"gru","series":[analytics_point]}]});
    let mut replication = analytics.clone();
    replication["items"] = json!([{"databaseId":"01ARZ3NDEKTSV4RRFFQ69G5FAW","databaseName":"orders","region":"gru","status":"healthy","replicatedOps":7,"lagMs":2.5,"divergence":false,"convergenceMs":4.0,"lastApplied":"2026-10-22T00:00:30Z","bandwidth":[bandwidth],"series":[{"start":"2026-10-22T00:00:00Z","replicatedOps":7,"bandwidth":[bandwidth]}]}]);
    let shapes = json!({"items":[{"id":"a".repeat(64),"collection":"items","shape":"{\"filter\":{\"eq\":{\"score\":\"?\"}}}","queries":3,"totalLatencyMs":4.5,"maxLatencyMs":2.5,"rowsScanned":10,"lastSeenAt":"2026-10-22T00:00:30Z"}]});
    let fixture = Fixture::new(vec![
        reply(200, &usage.to_string()),
        reply(200, &analytics.to_string()),
        reply(200, &replication.to_string()),
        reply(200, &database_usage.to_string()),
        reply(200, &analytics.to_string()),
        reply(200, &replication.to_string()),
        reply(200, &shapes.to_string()),
    ])
    .await;
    let client = fixture.client();
    let db = client.database("orders");
    let query = [
        ("from", "2026-10-22T00:00:00Z".into()),
        ("to", "2026-10-22T01:00:00Z".into()),
        ("granularity", "hour".into()),
    ];
    let range = [("range", "1h".into()), ("region", "gru".into())];
    let values: Vec<Value> = vec![
        client.get_project_usage(&query).await.unwrap(),
        client.get_project_analytics(&range).await.unwrap(),
        client.get_project_replication(&range).await.unwrap(),
        db.get_database_usage(&query).await.unwrap(),
        db.get_database_analytics(&range).await.unwrap(),
        db.get_database_replication(&range).await.unwrap(),
        db.list_query_shapes().await.unwrap(),
    ];
    assert_eq!(values[0], usage);
    assert_eq!(values[3], database_usage);
    for i in [1, 4] {
        assert_eq!(values[i], analytics);
        assert_eq!(values[i]["items"][0]["series"][0]["latency"]["p50Ms"], 1.5);
    }
    for i in [2, 5] {
        assert_eq!(values[i], replication);
        assert_eq!(
            values[i]["items"][0]["series"][0]["bandwidth"][0]["bytesSent"],
            512
        );
    }
    assert_eq!(values[6], shapes);
    let requests = fixture.requests.lock().unwrap();
    for (i, suffix) in [
        "usage",
        "analytics",
        "replication",
        "databases/orders/usage",
        "databases/orders/analytics",
        "databases/orders/replication",
        "databases/orders/query-shapes",
    ]
    .iter()
    .enumerate()
    {
        let parameters = if i == 6 {
            ""
        } else if i != 0 && i != 3 {
            "?range=1h&region=gru"
        } else {
            "?from=2026-10-22T00%3A00%3A00Z&to=2026-10-22T01%3A00%3A00Z&granularity=hour"
        };
        assert_eq!(
            requests[i].uri,
            format!("/v1/projects/{PROJECT}/{suffix}{parameters}")
        );
        assert_eq!(requests[i].method, "GET");
        assert!(requests[i].body.is_empty());
    }
}
