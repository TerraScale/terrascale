#[path = "support/project_http.rs"]
mod http;
use futures_util::StreamExt;
use http::{reply, Fixture, PROJECT};
use serde_json::json;
use std::time::Duration;
use terrascale::v1::{ChangeEvent, ChangeOp, ChangesOptions, Client};

#[tokio::test]
async fn changes_are_sse_with_once_encoded_filters_and_last_event_id() {
    let body = "id: cursor-after\nevent: cursor\ndata: {\"cursor\":\"cursor-after\"}\n\n";
    let fixture =
        Fixture::with_type(vec![reply(200, body)], "text/event-stream; charset=utf-8").await;
    let mut stream = fixture
        .client()
        .database("orders")
        .change_bytes(&ChangesOptions {
            cursor: Some("opaque/+".into()),
            last_event_id: Some("header-cursor".into()),
            collections: vec!["items".into(), "users".into()],
        })
        .await
        .unwrap();
    let mut received = Vec::new();
    while let Some(chunk) = stream.next().await {
        received.extend_from_slice(&chunk.unwrap());
    }
    assert_eq!(received, body.as_bytes());
    let requests = fixture.requests.lock().unwrap();
    assert_eq!(requests.len(), 1);
    assert_eq!(requests[0].uri,format!("/v1/projects/{PROJECT}/databases/orders/changes?cursor=opaque%2F%2B&collections=items%2Cusers"));
    assert_eq!(requests[0].headers["authorization"], "Bearer fixture-token");
    assert_eq!(requests[0].headers["accept"], "text/event-stream");
    assert_eq!(requests[0].headers["last-event-id"], "header-cursor");
    assert!(!requests[0].headers.contains_key("idempotency-key"));
}

#[tokio::test]
async fn changes_preserve_refusals_and_reject_non_sse_successes() {
    for (status, code) in [
        (401, "unauthorized"),
        (403, "forbidden_scope"),
        (404, "unknown_database"),
        (410, "cursor_expired"),
        (421, "cursor_elsewhere"),
        (429, "changes_busy"),
    ] {
        let body = json!({"error":{"code":code,"message":"fixture refusal"}}).to_string();
        let fixture = Fixture::new(vec![reply(status, &body)]).await;
        let client = Client::builder(&fixture.url, "fixture-token")
            .project(PROJECT)
            .build()
            .unwrap();
        let error = client
            .database("orders")
            .change_bytes(&ChangesOptions::default())
            .await
            .err()
            .unwrap();
        assert_eq!(error.status(), Some(status));
        assert_eq!(error.code(), Some(code));
        assert_eq!(error.raw_body(), Some(body.as_bytes()));
        assert_eq!(error.request_id(), Some("parity-fixture"));
        assert_eq!(fixture.requests.lock().unwrap().len(), 1);
    }
    let fixture = Fixture::new(vec![reply(200, "{}")]).await;
    assert_eq!(
        fixture
            .client()
            .database("orders")
            .change_bytes(&ChangesOptions::default())
            .await
            .err()
            .unwrap()
            .status(),
        Some(200)
    );
}

#[tokio::test]
async fn change_stream_delivers_before_close_and_drop_cancels_delivery() {
    use tokio::io::{AsyncReadExt, AsyncWriteExt};
    let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
    let url = format!("http://{}", listener.local_addr().unwrap());
    let task = tokio::spawn(async move {
        let (mut socket, _) = listener.accept().await.unwrap();
        let mut head = Vec::new();
        while !head.ends_with(b"\r\n\r\n") {
            head.push(socket.read_u8().await.unwrap());
        }
        socket.write_all(b"HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nTransfer-Encoding: chunked\r\n\r\n1\r\nx\r\n").await.unwrap();
        let mut byte = [0u8];
        assert_eq!(socket.read(&mut byte).await.unwrap(), 0);
    });
    let client = Client::builder(url, "fixture-token")
        .project(PROJECT)
        .build()
        .unwrap();
    let mut stream = client
        .database("orders")
        .change_bytes(&ChangesOptions::default())
        .await
        .unwrap();
    assert_eq!(
        &tokio::time::timeout(Duration::from_secs(2), stream.next())
            .await
            .unwrap()
            .unwrap()
            .unwrap()[..],
        b"x"
    );
    drop(stream);
    tokio::time::timeout(Duration::from_secs(2), task)
        .await
        .unwrap()
        .unwrap();
}

#[tokio::test]
async fn invalid_change_filters_do_not_dispatch() {
    let fixture = Fixture::new(vec![]).await;
    let db = fixture.client().database("orders");
    for options in [
        ChangesOptions {
            collections: vec!["items".into(); 65],
            ..Default::default()
        },
        ChangesOptions {
            collections: vec!["Bad Name".into()],
            ..Default::default()
        },
        ChangesOptions {
            last_event_id: Some("bad\r\nheader".into()),
            ..Default::default()
        },
    ] {
        assert!(db.change_bytes(&options).await.is_err());
    }
    assert!(fixture.requests.lock().unwrap().is_empty());
}

const ID: &str = "01ARZ3NDEKTSV4RRFFQ69G5FAV";

fn change(id: Option<&str>, op: &str, document: &str) -> String {
    let id = id.map(|id| format!("id: {id}\n")).unwrap_or_default();
    format!("{id}event: change\ndata: {{\"collection\":\"items\",\"id\":\"{ID}\",\"op\":\"{op}\",\"version\":\"7\",\"origin\":\"00000000000000a1\",\"document\":{document}}}\n\n")
}

#[tokio::test]
async fn changes_parse_events_resume_from_last_cursor_and_type_cursor_expired() {
    let first = format!(
        ": keepalive\r\n\r\n{}{}id: c2\nevent: cursor\ndata: {{\"cursor\":\"c2\"}}\n\n",
        change(None, "upsert", r#"{"n":1}"#),
        change(Some("c1"), "delete", "null"),
    );
    let expired = json!({"error":{"code":"cursor_expired","message":"gone"}}).to_string();
    let fixture = Fixture::with_type(
        vec![
            reply(200, &first),
            reply(200, &change(Some("c3"), "upsert", "{}")),
            reply(410, &expired),
        ],
        "text/event-stream",
    )
    .await;
    let mut events = fixture
        .client()
        .database("orders")
        .changes(&ChangesOptions {
            cursor: Some("start".into()),
            ..Default::default()
        })
        .await
        .unwrap();
    let mut seen = Vec::new();
    let error = loop {
        match events.next().await.unwrap() {
            Ok(event) => seen.push(event),
            Err(error) => break error,
        }
    };
    assert!(events.next().await.is_none());
    assert!(error.is_cursor_expired(), "{error:?}");
    assert_eq!(error.status(), Some(410));
    let cursors: Vec<_> = seen.iter().map(ChangeEvent::cursor).collect();
    assert_eq!(cursors, [None, Some("c1"), Some("c2"), Some("c3")]);
    let ChangeEvent::Change(upsert) = &seen[0] else {
        panic!("{seen:?}")
    };
    assert_eq!((upsert.op, upsert.version), (ChangeOp::Upsert, 7));
    assert_eq!(upsert.id.to_string(), ID);
    assert_eq!(upsert.document.as_ref().unwrap().get(), r#"{"n":1}"#);
    let ChangeEvent::Change(delete) = &seen[1] else {
        panic!("{seen:?}")
    };
    assert!(delete.op == ChangeOp::Delete && delete.document.is_none());
    let requests = fixture.requests.lock().unwrap();
    let resumed: Vec<_> = requests
        .iter()
        .map(|r| r.headers.get("last-event-id").map(|v| v.to_str().unwrap()))
        .collect();
    assert_eq!(resumed, [None, Some("c2"), Some("c3")]);
}

#[tokio::test]
async fn malformed_change_events_end_the_stream_without_reconnecting() {
    for body in [
        "event: mystery\ndata: {}\n\n".to_owned(),
        "event: change\ndata: {\"collection\":\"items\"}\n\n".to_owned(),
        "id: c1\nevent: cursor\ndata: {\"cursor\":\"other\"}\n\n".to_owned(),
        change(Some("c1"), "upsert", "[1]"),
        change(Some("c1"), "upsert", "{}").replace("\n\n", "\n"),
        change(None, "upsert", "{}"),
    ] {
        let fixture = Fixture::with_type(vec![reply(200, &body)], "text/event-stream").await;
        let mut events = fixture
            .client()
            .database("orders")
            .changes(&ChangesOptions::default())
            .await
            .unwrap();
        let mut results = Vec::new();
        while let Some(result) = events.next().await {
            results.push(result);
        }
        assert!(results.last().unwrap().is_err(), "{body}: {results:?}");
        assert_eq!(fixture.requests.lock().unwrap().len(), 1, "{body}");
    }
}
