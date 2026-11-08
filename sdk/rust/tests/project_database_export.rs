#[path = "support/project_http.rs"]
mod http;

use futures_util::StreamExt;
use http::{reply, Fixture, PROJECT};

#[tokio::test]
async fn export_streams_the_sql_dump_from_the_recorded_route() {
    let dump = "BEGIN;\nINSERT INTO \"todos\" VALUES (1,'ship');\nCOMMIT;";
    let fixture = Fixture::with_type(vec![reply(200, dump)], "application/sql").await;
    let client = fixture.client();

    let mut stream = client.database("orders").export().await.unwrap();
    let mut received = Vec::new();
    while let Some(chunk) = stream.next().await {
        received.extend_from_slice(&chunk.unwrap());
    }

    assert_eq!(received, dump.as_bytes());
    let seen = fixture.requests.lock().unwrap();
    assert_eq!(seen.len(), 1);
    assert_eq!(seen[0].method, "GET");
    assert_eq!(
        seen[0].uri,
        format!("/v1/projects/{PROJECT}/databases/orders/export")
    );
    assert_eq!(
        seen[0]
            .headers
            .get("accept")
            .and_then(|value| value.to_str().ok()),
        Some("text/plain")
    );
}
