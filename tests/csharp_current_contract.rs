//! Deterministic fixtures for TerraBase e30ffdc's ULID and by-key contract.

use std::collections::VecDeque;
use std::sync::{Arc, Mutex};
use std::time::Duration;

use bytes::Bytes;
use futures_util::StreamExt;
use http_body_util::{BodyExt, Full, StreamBody};
use hyper::{body::Frame, service::service_fn, HeaderMap, Request, Response};
use hyper_util::rt::TokioIo;
use serde_json::{json, value::RawValue, Value};
use terrascale::v1::*;
use tokio::sync::Notify;

const PROJECT: &str = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
const VERSION: &str = "tbv1-00000000000000000000000000000000";
const KEY: &str = "01ARZ3NDEKTSV4RRFFQ69G5FB0";
const OTHER: &str = "01ARZ3NDEKTSV4RRFFQ69G5FB1";

fn key() -> DocumentId {
    KEY.parse().unwrap()
}

enum Reply {
    Json(u16, String),
    Disconnect,
    PartialBody,
}

struct Observed {
    method: String,
    uri: String,
    headers: HeaderMap,
    body: Vec<u8>,
}

struct Fixture {
    url: String,
    requests: Arc<Mutex<Vec<Observed>>>,
    arrived: Arc<Notify>,
    disconnected: Arc<Notify>,
    task: tokio::task::JoinHandle<()>,
}

impl Drop for Fixture {
    fn drop(&mut self) {
        self.task.abort();
    }
}

impl Fixture {
    async fn new(replies: Vec<Reply>) -> Self {
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let url = format!("http://{}", listener.local_addr().unwrap());
        let requests = Arc::new(Mutex::new(Vec::new()));
        let arrived = Arc::new(Notify::new());
        let disconnected = Arc::new(Notify::new());
        let (responses, received, arrival, disconnect) = (
            Arc::new(Mutex::new(VecDeque::from(replies))),
            requests.clone(),
            arrived.clone(),
            disconnected.clone(),
        );
        let task = tokio::spawn(async move {
            let mut connections = tokio::task::JoinSet::new();
            loop {
                tokio::select! {
                    accepted = listener.accept() => {
                        let (socket, _) = accepted.unwrap();
                        let (responses, received, arrival, disconnect) = (
                            responses.clone(), received.clone(), arrival.clone(), disconnect.clone(),
                        );
                        connections.spawn(async move {
                            let service = service_fn(move |request: Request<hyper::body::Incoming>| {
                                let (responses, received, arrival) = (responses.clone(), received.clone(), arrival.clone());
                                async move {
                                    let (parts, body) = request.into_parts();
                                    let body = body.collect().await.unwrap().to_bytes().to_vec();
                                    received.lock().unwrap().push(Observed {
                                        method: parts.method.to_string(), uri: parts.uri.to_string(), headers: parts.headers, body,
                                    });
                                    let reply = responses.lock().unwrap().pop_front().expect("unexpected request");
                                    arrival.notify_one();
                                    let (status, body) = match reply {
                                        Reply::Disconnect => return Err(std::io::Error::other("fixture disconnect")),
                                        Reply::Json(status, body) => (status, Full::new(Bytes::from(body)).map_err(|never| match never {}).boxed()),
                                        Reply::PartialBody => {
                                            let stream = futures_util::stream::once(async {
                                                Ok::<_, std::io::Error>(Frame::data(Bytes::from_static(b"{")))
                                            }).chain(futures_util::stream::pending());
                                            (200, BodyExt::boxed(StreamBody::new(stream)))
                                        }
                                    };
                                    Ok(Response::builder().status(status)
                                        .header("content-type", "application/json")
                                        .header("x-request-id", "current-fixture-request")
                                        .body(body).unwrap())
                                }
                            });
                            let _ = hyper::server::conn::http1::Builder::new()
                                .serve_connection(TokioIo::new(socket), service).await;
                            disconnect.notify_one();
                        });
                    }
                    _ = connections.join_next(), if !connections.is_empty() => {}
                }
            }
        });
        Self {
            url,
            requests,
            arrived,
            disconnected,
            task,
        }
    }

    fn client(&self) -> Client {
        Client::builder(&self.url, "fixture-project-token")
            .project(PROJECT)
            .retry_policy(RetryPolicy::none())
            .build()
            .unwrap()
    }

    fn collection(&self) -> Collection {
        self.client().database("main").collection("items")
    }
}

fn ack(id: &str) -> String {
    json!({"id":id,"version":VERSION,"offset":"18446744073709551615","durability":"local_durable"})
        .to_string()
}

fn point(id: &str) -> String {
    format!(
        r#"{{"id":{},"version":"{VERSION}","state":"value","document":{{"exact":123456789012345678901234567890.123456789,"null":null}}}}"#,
        serde_json::to_string(id).unwrap()
    )
}

#[test]
fn project_ids_are_canonical_ulids_before_any_request() {
    for project in [
        "",
        "00000000-0000-0000-0000-000000000000",
        "01arz3ndektsv4rrffq69g5fav",
        "81ARZ3NDEKTSV4RRFFQ69G5FAV",
        "01ARZ3NDEKTSV4RRFFQ69G5FAI",
    ] {
        assert!(matches!(
            Client::builder("http://127.0.0.1:1", "fixture")
                .project(project)
                .build(),
            Err(Error::InvalidInput(_))
        ));
    }
    assert!(Client::for_project(PROJECT, "fixture").build().is_ok());
}

#[tokio::test]
async fn document_ids_are_canonical_ulids_on_the_wire() {
    let id: DocumentId = PROJECT.parse().unwrap();
    let fixture = Fixture::new(vec![
        Reply::Json(200, point(PROJECT)),
        Reply::Json(200, point(&PROJECT.to_lowercase())),
        Reply::Json(200, point("customer:key")),
        Reply::Json(201, ack("01arz3ndektsv4rrffq69g5fav")),
    ])
    .await;
    let collection = fixture.collection();
    let document = collection.get::<Box<RawValue>>(id).await.unwrap();
    assert_eq!(document.id, id);
    for _ in 0..2 {
        assert!(collection.get::<Value>(id).await.is_err());
    }
    assert!(collection
        .create(&json!({}), WriteOptions::new())
        .await
        .is_err());
    let requests = fixture.requests.lock().unwrap();
    assert_eq!(
        requests[0].uri,
        format!(
            "/v1/projects/{PROJECT}/databases/main/collections/items/documents/by-key?id={PROJECT}"
        )
    );
    assert_eq!(
        requests[0].headers["authorization"],
        "Bearer fixture-project-token"
    );
}

#[tokio::test]
async fn current_crud_headers_generated_ulids_and_collection_identity() {
    let key = ulid_for("items", "account/a+b");
    let collection_body =
        json!({"id":PROJECT,"name":"items","created_at":"18446744073709551615"}).to_string();
    let fixture = Fixture::new(vec![
        Reply::Json(201, ack(&key.to_string())),
        Reply::Json(304, String::new()),
        Reply::Json(200, ack(&key.to_string())),
        Reply::Json(200, ack(&key.to_string())),
        Reply::Json(201, ack(PROJECT)),
        Reply::Json(201, collection_body.clone()),
        Reply::Json(200, collection_body),
    ])
    .await;
    let collection = fixture.collection();
    let written = collection
        .put(
            key,
            &json!({"name":"Ada"}),
            WriteOptions::new().if_none_match_any(),
        )
        .await
        .unwrap();
    assert!(written.created);
    assert_eq!(written.offset, u64::MAX);
    assert!(collection
        .get_if_changed::<Value>(key, &written.version)
        .await
        .unwrap()
        .is_none());
    collection
        .patch(
            key,
            &Patch::merge(&json!({"name":"Grace"})).unwrap(),
            WriteOptions::new().if_match(written.version.clone()),
        )
        .await
        .unwrap();
    collection
        .delete(key, WriteOptions::new().if_match(written.version))
        .await
        .unwrap();
    assert_eq!(
        collection
            .create(&json!({}), WriteOptions::new())
            .await
            .unwrap()
            .id
            .to_string(),
        PROJECT
    );
    let info = collection
        .database()
        .create_collection("items")
        .await
        .unwrap();
    assert_eq!(info.id, PROJECT);
    assert_eq!(info.created_at, u64::MAX);
    assert_eq!(
        serde_json::from_str::<ProjectCollectionInfo>(&serde_json::to_string(&info).unwrap())
            .unwrap(),
        info
    );
    assert_eq!(collection.get_collection().await.unwrap(), info);
    let requests = fixture.requests.lock().unwrap();
    assert_eq!(requests[0].headers["if-none-match"], "*");
    assert_eq!(
        requests[1].headers["if-none-match"],
        format!("\"{VERSION}\"")
    );
    for request in &requests[2..4] {
        assert_eq!(request.headers["if-match"], format!("\"{VERSION}\""));
        assert!(!request.headers.contains_key("idempotency-key"));
    }
}

#[tokio::test]
async fn database_create_uses_management_post_and_preserves_placement() {
    let body = json!({"id":PROJECT,"projectId":PROJECT,"name":"main","branchedFrom":null,
        "createdAt":"2026-10-12T22:38:35+00:00","region":"gru","homeRegion":"gru","replicaRegions":["iad"]}).to_string();
    let fixture = Fixture::new(vec![Reply::Json(201, body)]).await;
    let info = fixture.client().database("main").create().await.unwrap();
    assert_eq!(info.project_id, PROJECT);
    assert_eq!(info.replica_regions, ["iad"]);
    assert_eq!(
        serde_json::from_str::<ProjectDatabaseInfo>(&serde_json::to_string(&info).unwrap())
            .unwrap(),
        info
    );
    let requests = fixture.requests.lock().unwrap();
    assert_eq!(requests[0].method, "POST");
    assert_eq!(requests[0].uri, format!("/v1/projects/{PROJECT}/databases"));
    assert_eq!(
        serde_json::from_slice::<Value>(&requests[0].body).unwrap(),
        json!({"name":"main"})
    );
}

#[tokio::test]
async fn explicit_retry_keeps_client_generated_key_and_exact_bytes() {
    let key = ulid_for("items", "stable");
    let fixture = Fixture::new(vec![
        Reply::Disconnect,
        Reply::Json(200, ack(&key.to_string())),
    ])
    .await;
    let collection = fixture.collection();
    let raw =
        RawValue::from_string(r#"{"n":123456789012345678901234567890.123456789}"#.into()).unwrap();
    assert!(collection
        .put(key, &raw, WriteOptions::new())
        .await
        .unwrap_err()
        .is_uncertain());
    assert_eq!(
        fixture.requests.lock().unwrap().len(),
        1,
        "mutation must not replay automatically"
    );
    let result = collection
        .put(key, &raw, WriteOptions::new())
        .await
        .unwrap();
    assert_eq!(result.id, key);
    let requests = fixture.requests.lock().unwrap();
    assert_eq!(requests.len(), 2);
    assert_eq!(requests[0].uri, requests[1].uri);
    assert_eq!(requests[0].body, requests[1].body);
    assert_eq!(requests[0].body, raw.get().as_bytes());
}

#[tokio::test]
async fn malformed_empty_wrong_status_and_identity_keep_diagnostics() {
    for (status, body, mutation) in [
        (304, String::new(), false),
        (200, "".to_owned(), false),
        (200, "{".into(), false),
        (201, point(KEY), false),
        (200, point(OTHER), false),
        (204, String::new(), true),
        (202, ack(KEY), true),
        (200, ack(OTHER), true),
    ] {
        let fixture = Fixture::new(vec![Reply::Json(status, body.clone())]).await;
        let collection = fixture.collection();
        let error = if mutation {
            collection
                .put(key(), &json!({}), WriteOptions::new())
                .await
                .unwrap_err()
        } else {
            collection.get::<Value>(key()).await.unwrap_err()
        };
        assert_eq!(error.status(), Some(status));
        assert_eq!(error.raw_body(), Some(body.as_bytes()));
        assert_eq!(error.request_id(), Some("current-fixture-request"));
        assert_eq!(error.is_uncertain(), mutation);
        assert_eq!(fixture.requests.lock().unwrap().len(), 1);
    }
}

#[tokio::test]
async fn pagination_keeps_empty_intermediate_pages_and_stops_without_extra_requests() {
    let fixture = Fixture::new(vec![
        Reply::Json(200, r#"{"items":[],"next_cursor":"opaque/+"}"#.into()),
        Reply::Json(
            200,
            format!(r#"{{"items":[{}],"next_cursor":null}}"#, point(PROJECT)),
        ),
    ])
    .await;
    let mut pages = fixture
        .collection()
        .pages::<Box<RawValue>>(Query::new().count(true));
    assert!(pages.next_page().await.unwrap().unwrap().items.is_empty());
    assert_eq!(
        pages.next_page().await.unwrap().unwrap().items[0]
            .id
            .to_string(),
        PROJECT
    );
    assert!(pages.next_page().await.unwrap().is_none());
    assert!(pages.next_page().await.unwrap().is_none());
    let requests = fixture.requests.lock().unwrap();
    assert_eq!(requests.len(), 2);
    let second: Value = serde_json::from_slice(&requests[1].body).unwrap();
    assert_eq!(second["cursor"], "opaque/+");
    assert!(second.get("count").is_none());
}

#[tokio::test]
async fn pagination_refuses_missing_empty_and_cyclic_cursors() {
    for body in [r#"{"items":[]}"#, r#"{"items":[],"next_cursor":""}"#] {
        let fixture = Fixture::new(vec![Reply::Json(200, body.into())]).await;
        assert!(fixture
            .collection()
            .pages::<Value>(Query::new())
            .next_page()
            .await
            .is_err());
    }
    let fixture = Fixture::new(vec![
        Reply::Json(200, r#"{"items":[],"next_cursor":"a"}"#.into()),
        Reply::Json(200, r#"{"items":[],"next_cursor":"b"}"#.into()),
        Reply::Json(200, r#"{"items":[],"next_cursor":"a"}"#.into()),
    ])
    .await;
    let mut pages = fixture.collection().pages::<Value>(Query::new());
    pages.next_page().await.unwrap();
    pages.next_page().await.unwrap();
    assert!(matches!(pages.next_page().await, Err(Error::Decode(_))));
    assert_eq!(fixture.requests.lock().unwrap().len(), 3);
}

#[tokio::test]
async fn transaction_keys_and_generated_ulids_match_the_acknowledgement() {
    let body = json!({"offset":"1","durability":"local_durable","results":[{"id":KEY,"version":VERSION},{"id":PROJECT,"version":VERSION}]}).to_string();
    let fixture = Fixture::new(vec![Reply::Json(200, body)]).await;
    let mut tx = Tx::new();
    tx.put("items", key(), &json!({}), None)
        .create("items", None, &json!({}));
    let result = fixture.client().database("main").commit(&tx).await.unwrap();
    assert_eq!(result.results[0].id(), Some(key()));
    assert_eq!(result.results[1].id(), PROJECT.parse().ok());
    let requests = fixture.requests.lock().unwrap();
    let body: Value = serde_json::from_slice(&requests[0].body).unwrap();
    assert_eq!(body["ops"][0]["id"], KEY);
    assert!(body["ops"][1].get("id").is_none());
}

#[tokio::test]
async fn cancellation_during_streamed_body_closes_the_connection() {
    let fixture = Fixture::new(vec![Reply::PartialBody]).await;
    let collection = fixture.collection();
    let request = tokio::spawn(async move { collection.get::<Value>(key()).await });
    tokio::time::timeout(Duration::from_secs(3), fixture.arrived.notified())
        .await
        .unwrap();
    request.abort();
    assert!(request.await.unwrap_err().is_cancelled());
    tokio::time::timeout(Duration::from_secs(3), fixture.disconnected.notified())
        .await
        .unwrap();
    assert_eq!(fixture.requests.lock().unwrap().len(), 1);
}

#[tokio::test]
async fn oversized_project_errors_preserve_a_bounded_body_and_request_id() {
    let fixture = Fixture::new(vec![Reply::Json(503, "123456789".into())]).await;
    let client = Client::builder(&fixture.url, "fixture")
        .project(PROJECT)
        .max_response_bytes(4)
        .build()
        .unwrap();
    let error = client
        .database("main")
        .collection("items")
        .put(key(), &json!({}), WriteOptions::new())
        .await
        .unwrap_err();
    assert_eq!(error.status(), Some(503));
    assert_eq!(error.raw_body(), Some(&b"1234"[..]));
    assert_eq!(error.request_id(), Some("current-fixture-request"));
    assert!(error.is_uncertain());
    assert_eq!(fixture.requests.lock().unwrap().len(), 1);
}

#[tokio::test]
async fn pinned_region_mismatch_keeps_acknowledgement_for_reconciliation() {
    let body = ack(KEY);
    let fixture = Fixture::new(vec![Reply::Json(200, body.clone())]).await;
    let client = Client::builder(&fixture.url, "fixture")
        .project(PROJECT)
        .region_pin("gru")
        .build()
        .unwrap();
    let error = client
        .database("main")
        .collection("items")
        .put(key(), &json!({}), WriteOptions::new())
        .await
        .unwrap_err();
    assert_eq!(error.status(), Some(200));
    assert_eq!(error.raw_body(), Some(body.as_bytes()));
    assert_eq!(error.request_id(), Some("current-fixture-request"));
    assert!(error.is_uncertain());
    assert_eq!(fixture.requests.lock().unwrap().len(), 1);
}

#[tokio::test]
async fn truncated_denial_keeps_status_and_never_retries() {
    use std::sync::atomic::{AtomicUsize, Ordering};
    use tokio::io::{AsyncReadExt, AsyncWriteExt};

    let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
    let url = format!("http://{}", listener.local_addr().unwrap());
    let hits = Arc::new(AtomicUsize::new(0));
    let received = hits.clone();
    let task = tokio::spawn(async move {
        while let Ok((mut socket, _)) = listener.accept().await {
            let mut head = Vec::new();
            while !head.ends_with(b"\r\n\r\n") {
                head.push(socket.read_u8().await.unwrap());
            }
            received.fetch_add(1, Ordering::SeqCst);
            socket.write_all(b"HTTP/1.1 403 Forbidden\r\nContent-Length: 20\r\nX-Request-ID: denied-stream\r\nConnection: close\r\n\r\ndeny").await.unwrap();
            socket.shutdown().await.unwrap();
        }
    });
    let client = Client::builder(&url, "fixture")
        .project(PROJECT)
        .retry_policy(RetryPolicy {
            max_retries: 2,
            base_delay: Duration::ZERO,
            max_delay: Duration::ZERO,
            max_retry_after: Duration::ZERO,
        })
        .build()
        .unwrap();
    let error = client
        .database("main")
        .collection("items")
        .get::<Value>(key())
        .await
        .unwrap_err();
    task.abort();
    assert_eq!(error.status(), Some(403));
    assert_eq!(error.request_id(), Some("denied-stream"));
    assert!(!error.is_uncertain());
    assert_eq!(hits.load(Ordering::SeqCst), 1);
}
