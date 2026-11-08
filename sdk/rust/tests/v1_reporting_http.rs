//! Database usage and analytics wire parity (HTTP fixture).

#[path = "support/project_http.rs"]
mod http;
use http::{reply, Fixture, PROJECT};
use terrascale::v1::Client;

const USAGE: &str = r#"{"databaseId":"01ARZ3NDEKTSV4RRFFQ69G5FAW","storageBytes":1024,"logicalBytes":1024,"reads":3,"writes":1,"period":{"from":"2026-10-07T00:00:00Z","to":"2026-10-08T00:00:00Z","granularity":"hour","reads":3,"writes":1},"series":[{"start":"2026-10-07T00:00:00Z","reads":3,"writes":1}]}"#;
const ANALYTICS: &str = r#"{"projectId":"01ARZ3NDEKTSV4RRFFQ69G5FAV","range":"24h","from":"2026-10-07T00:00:00Z","to":"2026-10-08T00:00:00Z","bucketSeconds":3600,"items":[{"databaseId":"01ARZ3NDEKTSV4RRFFQ69G5FAW","databaseName":"orders","region":"gru","series":[{"start":"2026-10-07T00:00:00Z","requests":{"GET":2},"operations":{"insert":1},"errors":0,"latency":{"p50Ms":1.5,"p95Ms":null,"p99Ms":null},"storageBytes":null,"clientBandwidth":{"bytesSent":10,"bytesReceived":20},"replicationBandwidth":{"bytesSent":0,"bytesReceived":0}}]}]}"#;

#[tokio::test]
async fn database_usage_and_analytics_read_the_reporting_routes() {
    let fixture = Fixture::new(vec![reply(200, USAGE), reply(200, ANALYTICS)]).await;
    let client = Client::builder(&fixture.url, "fixture-token")
        .project(PROJECT)
        .build()
        .unwrap();
    let database = client.database("orders");
    let usage: serde_json::Value = database
        .get_database_usage(&[("granularity", "hour".to_owned())])
        .await
        .unwrap();
    let analytics: serde_json::Value = database
        .get_database_analytics(&[("range", "24h".to_owned())])
        .await
        .unwrap();
    let seen = fixture.requests.lock().unwrap();
    assert_eq!(seen.len(), 2);
    assert_eq!(
        seen[0].uri,
        format!("/v1/projects/{PROJECT}/databases/orders/usage?granularity=hour")
    );
    assert_eq!(
        seen[1].uri,
        format!("/v1/projects/{PROJECT}/databases/orders/analytics?range=24h")
    );
    assert_eq!(usage["reads"], 3);
    assert_eq!(analytics["bucketSeconds"], 3600);
    assert_eq!(analytics["items"][0]["databaseName"], "orders");
}
