//! HTTP-fake conformance tests; no TerraBase binary is downloaded or run.
use std::collections::VecDeque;
use std::sync::{Arc, Mutex};
use std::time::Duration;

use bytes::Bytes;
use futures_util::{pin_mut, StreamExt};
use http_body_util::Full;
use hyper::{service::service_fn, HeaderMap, Request, Response};
use hyper_util::rt::TokioIo;
use ring::{aead, hkdf};
use serde_json::{json, Value};
use terrascale::v1::{Client, RetryPolicy};

const PROJECT: &str = "01ARZ3NDEKTSV4RRFFQ69G5FAW";
const ID0: &str = "01ARZ3NDEKTSV4RRFFQ69G5FB0";
const ID1: &str = "01ARZ3NDEKTSV4RRFFQ69G5FB1";
const OTHER_HANDLE: &str = "h1-d90dddd41bd8282d";
const OTHER_KEY_ID: &str = "01ARZ3NDEKTSV4RRFFQ69G5FC1";

fn vectors() -> Value {
    serde_json::from_str(include_str!("fixtures/shape-vectors.json")).unwrap()
}
fn vector(id: &str) -> Value {
    vectors()["http_vectors"]
        .as_array()
        .unwrap()
        .iter()
        .find(|v| v["id"] == id)
        .unwrap()
        .clone()
}

#[derive(Clone)]
struct Reply {
    status: u16,
    headers: Vec<(String, String)>,
    body: Vec<u8>,
}
impl Reply {
    fn vector(id: &str) -> Self {
        let v = vector(id);
        let response = &v["response"];
        Self {
            status: response["status"].as_u64().unwrap() as u16,
            headers: response["headers"]
                .as_object()
                .unwrap()
                .iter()
                .map(|(k, v)| (k.clone(), v.as_str().unwrap().to_owned()))
                .collect(),
            body: response
                .get("body_utf8")
                .and_then(Value::as_str)
                .map(str::as_bytes)
                .map(Vec::from)
                .unwrap_or_else(|| {
                    response
                        .get("body")
                        .map(|v| v.to_string().into_bytes())
                        .unwrap_or_default()
                }),
        }
    }
    fn body(body: Value) -> Self {
        let mut headers = vec![("Content-Type".into(), "application/json".into())];
        if let Some(handle) = body["handle"].as_str() {
            headers.push(("TB-Shape-Handle".into(), handle.into()));
        }
        if let Some(offset) = body["next_offset"].as_str() {
            headers.push(("TB-Shape-Next-Offset".into(), offset.into()));
        }
        if let Some(fresh) = body["up_to_date"].as_bool() {
            headers.push(("TB-Shape-Up-To-Date".into(), fresh.to_string()));
        }
        Self {
            status: 200,
            headers,
            body: body.to_string().into_bytes(),
        }
    }
}

struct Observed {
    uri: String,
    headers: HeaderMap,
}
struct Fixture {
    url: String,
    requests: Arc<Mutex<Vec<Observed>>>,
    replies: Arc<Mutex<VecDeque<Reply>>>,
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
        let queue = Arc::new(Mutex::new(VecDeque::from(replies)));
        let (observed, responses) = (requests.clone(), queue.clone());
        let task = tokio::spawn(async move {
            let mut connections = tokio::task::JoinSet::new();
            loop {
                tokio::select! {
                    accepted = listener.accept() => {
                        let (socket, _) = accepted.unwrap();
                        let (observed,responses) = (observed.clone(),responses.clone());
                        connections.spawn(async move {
                            let service = service_fn(move |request: Request<hyper::body::Incoming>| {
                                let (observed,responses) = (observed.clone(),responses.clone());
                                async move {
                                    assert_eq!(request.method(), "GET");
                                    observed.lock().unwrap().push(Observed {
                                        uri: request.uri().to_string(), headers: request.headers().clone(),
                                    });
                                    let reply = responses.lock().unwrap().pop_front().expect("unexpected shape request");
                                    let mut response = Response::builder().status(reply.status)
                                        .header("x-terrabase-region", "fixture-region");
                                    for (name,value) in reply.headers { response = response.header(name,value); }
                                    Ok::<_, std::convert::Infallible>(response.body(Full::new(Bytes::from(reply.body))).unwrap())
                                }
                            });
                            let _ = hyper::server::conn::http1::Builder::new()
                                .serve_connection(TokioIo::new(socket),service).await;
                        });
                    }
                    _ = connections.join_next(), if !connections.is_empty() => {}
                }
            }
        });
        Self {
            url,
            requests,
            replies: queue,
            task,
        }
    }
    fn client(&self) -> Client {
        Client::builder(&self.url, "fixture-access-token")
            .project(PROJECT)
            .region_pin("fixture-region")
            .retry_policy(RetryPolicy {
                base_delay: Duration::from_millis(1),
                max_delay: Duration::from_millis(2),
                ..RetryPolicy::default()
            })
            .timeout(Some(Duration::from_secs(2)))
            .build()
            .unwrap()
    }
    fn query(&self, n: usize) -> std::collections::HashMap<String, String> {
        url::Url::parse(&format!(
            "{}{}",
            self.url,
            self.requests.lock().unwrap()[n].uri
        ))
        .unwrap()
        .query_pairs()
        .into_owned()
        .collect()
    }
    fn assert_auth(&self) {
        for req in self.requests.lock().unwrap().iter() {
            assert!(req.uri.starts_with(&format!(
                "/v1/projects/{PROJECT}/databases/demo/collections/notes/shape"
            )));
            assert_eq!(req.headers["authorization"], "Bearer fixture-access-token");
            assert_eq!(req.headers["x-terrascale-region-pin"], "fixture-region");
        }
        assert!(self.replies.lock().unwrap().is_empty());
    }
}

fn snapshot() -> Vec<Reply> {
    ["bootstrap", "snapshot-first-page", "snapshot-last-page"]
        .map(Reply::vector)
        .into()
}

#[tokio::test]
async fn eight_mib_materialized_commit_and_snapshot_singleton_allow_response_envelope() {
    for snapshot_page in [false, true] {
        let mut body = vector(if snapshot_page {
            "snapshot-first-page"
        } else {
            "closed-commit-upsert-delete"
        })["response"]["body"]
            .clone();
        body["changes"][0]["ops"]
            .as_array_mut()
            .unwrap()
            .truncate(1);
        body["changes"][0]["ops"][0]["document"] = json!({"padding":""});
        let materialized = if snapshot_page {
            &body["changes"][0]["ops"][0]["document"]
        } else {
            &body["changes"][0]
        };
        let padding = (8 << 20) - serde_json::to_vec(materialized).unwrap().len();
        body["changes"][0]["ops"][0]["document"]["padding"] = json!("x".repeat(padding));
        let reply = Reply::body(body);
        assert!(reply.body.len() > 8 << 20);
        let mut replies = if snapshot_page {
            vec![Reply::vector("bootstrap")]
        } else {
            snapshot()
        };
        replies.push(reply);
        let fixture = Fixture::new(replies).await;
        let stream = fixture
            .client()
            .database("demo")
            .collection("notes")
            .shape()
            .stream();
        pin_mut!(stream);
        if !snapshot_page {
            stream.next().await.unwrap().unwrap();
            stream.next().await.unwrap().unwrap();
        }
        let update = stream.next().await.unwrap().unwrap();
        let document: Value = serde_json::from_str(
            update
                .state
                .get(ID0.parse().unwrap())
                .unwrap()
                .document
                .get(),
        )
        .unwrap();
        assert_eq!(document["padding"].as_str().unwrap().len(), padding);
        fixture.assert_auth();
    }
}

#[tokio::test]
async fn resume_redirect_cannot_enter_snapshot_with_preserved_state() {
    let mut replies = snapshot();
    replies.push(Reply::vector(
        "changed-machine-continuation-tries-resume-once",
    ));
    let mut redirect = Reply::vector("proven-cursor-resume-query");
    for (name, value) in &mut redirect.headers {
        if name == "Location" {
            let mut url = url::Url::parse(&format!("http://localhost{value}")).unwrap();
            let pairs: Vec<_> = url.query_pairs().into_owned().collect();
            url.set_query(None);
            for (name, value) in pairs {
                url.query_pairs_mut()
                    .append_pair(&name, if name == "offset" { "snap.2.0" } else { &value });
            }
            *value = format!("{}?{}", url.path(), url.query().unwrap());
        }
    }
    replies.push(redirect);
    let fixture = Fixture::new(replies).await;
    let stream = fixture
        .client()
        .database("demo")
        .collection("notes")
        .shape()
        .stream();
    pin_mut!(stream);
    stream.next().await.unwrap().unwrap();
    let before = stream.next().await.unwrap().unwrap();
    assert!(stream.next().await.unwrap().is_err());
    assert_eq!(before.state.len(), 2);
    assert!(stream.next().await.is_none());
    fixture.assert_auth();
}

#[tokio::test]
async fn advancing_non_live_response_cannot_claim_to_be_up_to_date() {
    let mut replies = snapshot();
    let mut body = vector("closed-commit-upsert-delete")["response"]["body"].clone();
    body["changes"] = json!([]);
    body["up_to_date"] = json!(true);
    replies.push(Reply::body(body));
    let fixture = Fixture::new(replies).await;
    let stream = fixture
        .client()
        .database("demo")
        .collection("notes")
        .shape()
        .stream();
    pin_mut!(stream);
    stream.next().await.unwrap().unwrap();
    let before = stream.next().await.unwrap().unwrap();
    assert!(stream.next().await.unwrap().is_err());
    assert_eq!(before.offset, "2");
    assert_eq!(before.state.len(), 2);
    fixture.assert_auth();
}

#[tokio::test]
async fn bootstrap_must_refetch_restarts_with_a_bounded_retry_count() {
    let mut replies = vec![Reply::vector("expired-offset")];
    replies.extend(snapshot());
    let fixture = Fixture::new(replies).await;
    let stream = fixture
        .client()
        .database("demo")
        .collection("notes")
        .shape()
        .stream();
    pin_mut!(stream);
    stream.next().await.unwrap().unwrap();
    stream.next().await.unwrap().unwrap();
    assert_eq!(fixture.query(0), fixture.query(1));
    fixture.assert_auth();

    let fixture = Fixture::new(vec![Reply::vector("expired-offset"); 4]).await;
    let stream = fixture
        .client()
        .database("demo")
        .collection("notes")
        .shape()
        .stream();
    pin_mut!(stream);
    assert!(stream.next().await.unwrap().is_err());
    assert!(stream.next().await.is_none());
    for index in 0..4 {
        assert_eq!(fixture.query(index)["offset"], "-1");
        assert_eq!(fixture.query(index).len(), 1);
    }
    fixture.assert_auth();
}

#[tokio::test]
async fn snapshot_chunks_live_buckets_and_immutable_state_follow_vectors() {
    let mut replies = snapshot();
    replies.push(Reply::vector("closed-commit-upsert-delete"));
    replies.push(Reply::body(
        vector("live-timeout")["response"]["body"].clone(),
    ));
    replies.push(Reply::vector("live-timeout"));
    let mut live = Reply::vector("live-timeout");
    for (name, value) in &mut live.headers {
        if name == "TB-Shape-Cursor" {
            *value = "52".into();
        }
    }
    replies.push(live);
    let fixture = Fixture::new(replies).await;
    let stream = fixture
        .client()
        .database("demo")
        .collection("notes")
        .shape()
        .stream();
    pin_mut!(stream);
    let first = stream.next().await.unwrap().unwrap();
    assert_eq!(first.state.len(), 1);
    assert!(!first.up_to_date);
    let second = stream.next().await.unwrap().unwrap();
    assert_eq!(second.offset, "2");
    assert_eq!(second.state.len(), 2);
    let chunk = stream.next().await.unwrap().unwrap();
    assert_eq!(chunk.offset, "4");
    assert_eq!(chunk.state.len(), 1);
    assert!(chunk.state.get(ID1.parse().unwrap()).is_none());
    assert_eq!(
        serde_json::from_str::<Value>(
            chunk
                .state
                .get(ID0.parse().unwrap())
                .unwrap()
                .document
                .get()
        )
        .unwrap()["read"],
        true
    );
    assert_eq!(first.state.len(), 1);
    assert_eq!(second.state.len(), 2);
    assert!(stream.next().await.unwrap().unwrap().up_to_date);
    assert!(stream.next().await.unwrap().unwrap().up_to_date);
    assert!(stream.next().await.unwrap().unwrap().up_to_date);
    assert_eq!(fixture.query(0).get("offset").unwrap(), "-1");
    assert!(!fixture.query(0).contains_key("handle"));
    assert_eq!(
        fixture.query(2).get("offset").unwrap(),
        first.offset.as_str()
    );
    assert_eq!(fixture.query(5).get("live").unwrap(), "true");
    assert_eq!(fixture.query(6).get("cursor").unwrap(), "51");
    fixture.assert_auth();
}

#[tokio::test]
async fn changed_machine_resumes_once_and_applies_newer_repeated_value() {
    let mut replies = snapshot();
    replies.push(Reply::vector(
        "changed-machine-continuation-tries-resume-once",
    ));
    replies.push(Reply::vector("proven-cursor-resume-query"));
    let body = vectors()["apply_vectors"]
        .as_array()
        .unwrap()
        .iter()
        .find(|v| v["id"] == "cross-lineage-repeat-has-newer-materialized-value")
        .unwrap()["body"]
        .clone();
    replies.push(Reply::body(body));
    let fixture = Fixture::new(replies).await;
    let stream = fixture
        .client()
        .database("demo")
        .collection("notes")
        .shape()
        .stream();
    pin_mut!(stream);
    stream.next().await.unwrap().unwrap();
    let before = stream.next().await.unwrap().unwrap();
    let update = stream.next().await.unwrap().unwrap();
    assert_eq!(update.state.len(), 2); // preserve the untouched second document
    assert_eq!(
        update
            .state
            .get(ID0.parse().unwrap())
            .unwrap()
            .version
            .as_str(),
        "tbv1-b9a54642e86bd83f460ea839d3dc5e51"
    );
    assert_eq!(
        serde_json::from_str::<Value>(
            before
                .state
                .get(ID0.parse().unwrap())
                .unwrap()
                .document
                .get()
        )
        .unwrap()["read"],
        false
    );
    assert_eq!(fixture.query(4).get("offset").unwrap(), "-1");
    assert!(!fixture.query(4).contains_key("handle"));
    assert_eq!(
        fixture.query(4).get("cursor").unwrap(),
        vector("snapshot-last-page")["response"]["body"]["cursor_resume"]
            .as_str()
            .unwrap()
    );
    assert_eq!(
        fixture.query(5).get("handle").unwrap(),
        "h1-d90dddd41bd8282d"
    );
    fixture.assert_auth();
}

#[tokio::test]
async fn second_resume_refetch_discards_state_without_another_attempt() {
    let mut replies = snapshot();
    replies.extend(
        [
            "changed-machine-continuation-tries-resume-once",
            "proven-cursor-resume-query",
            "resume-followup-second-machine-refetch-does-not-loop",
        ]
        .map(Reply::vector),
    );
    replies.push(Reply::vector("bootstrap"));
    let mut body = vector("snapshot-first-page")["response"]["body"].clone();
    body["changes"][0]["ops"] = json!([]);
    replies.push(Reply::body(body));
    let fixture = Fixture::new(replies).await;
    let stream = fixture
        .client()
        .database("demo")
        .collection("notes")
        .shape()
        .stream();
    pin_mut!(stream);
    stream.next().await.unwrap().unwrap();
    assert_eq!(stream.next().await.unwrap().unwrap().state.len(), 2);
    let restarted = stream.next().await.unwrap().unwrap();
    assert!(restarted.state.is_empty());
    assert_eq!(fixture.query(6).get("offset").unwrap(), "-1");
    assert!(!fixture.query(6).contains_key("cursor"));
    assert!(!fixture.requests.lock().unwrap()[6]
        .headers
        .contains_key("TB-Shape-Resume"));
    fixture.assert_auth();
}

#[tokio::test]
async fn incomplete_snapshot_and_same_handle_refetch_restart_empty() {
    for (completed, error) in [
        (false, "changed-machine-continuation-tries-resume-once"),
        (true, "expired-offset"),
    ] {
        let mut replies = snapshot();
        if !completed {
            replies.pop();
        }
        replies.push(Reply::vector(error));
        replies.push(Reply::vector("bootstrap"));
        let mut empty = vector("snapshot-first-page")["response"]["body"].clone();
        empty["changes"][0]["ops"] = json!([]);
        replies.push(Reply::body(empty));
        let fixture = Fixture::new(replies).await;
        let stream = fixture
            .client()
            .database("demo")
            .collection("notes")
            .shape()
            .stream();
        pin_mut!(stream);
        stream.next().await.unwrap().unwrap();
        if completed {
            stream.next().await.unwrap().unwrap();
        }
        assert!(stream.next().await.unwrap().unwrap().state.is_empty());
        let restart = if completed { 4 } else { 3 };
        assert_eq!(fixture.query(restart).get("offset").unwrap(), "-1");
        assert!(!fixture.query(restart).contains_key("cursor"));
        fixture.assert_auth();
    }
}

#[tokio::test]
async fn large_resume_cursor_uses_header_verbatim_and_preserves_state() {
    let large = vector("large-resume-token-uses-header")["request"]["headers"]["TB-Shape-Resume"]
        .as_str()
        .unwrap()
        .to_owned();
    let mut replies = snapshot();
    let mut last = vector("snapshot-last-page")["response"]["body"].clone();
    last["cursor_resume"] = json!(large);
    replies[2] = Reply::body(last);
    replies.extend(
        [
            "changed-machine-continuation-tries-resume-once",
            "large-resume-token-uses-header",
        ]
        .map(Reply::vector),
    );
    let mut fresh = vector("live-timeout")["response"]["body"].clone();
    fresh["handle"] = json!("h1-d90dddd41bd8282d");
    fresh["shape_id"] = vectors()["fixture"]["destination"]["shape_id"].clone();
    replies.push(Reply::body(fresh));
    let fixture = Fixture::new(replies).await;
    let stream = fixture
        .client()
        .database("demo")
        .collection("notes")
        .shape()
        .stream();
    pin_mut!(stream);
    stream.next().await.unwrap().unwrap();
    stream.next().await.unwrap().unwrap();
    let update = stream.next().await.unwrap().unwrap();
    assert_eq!(update.offset, "4");
    assert_eq!(fixture.query(4).len(), 1);
    assert_eq!(fixture.query(4)["offset"], "-1");
    assert_eq!(
        fixture.requests.lock().unwrap()[4].headers["TB-Shape-Resume"],
        large
    );
    fixture.assert_auth();
}

#[tokio::test]
async fn transient_failures_retry_the_saved_offset_and_auth_errors_end_the_stream() {
    for (status, code) in [
        (503, "shape_too_large"),
        (503, "unavailable"),
        (429, "shape_busy"),
    ] {
        let mut replies = snapshot();
        let mut transient = Reply::body(json!({"error":{"code":code}}));
        transient.status = status;
        transient.headers.push(("Retry-After".into(), "0".into()));
        replies.push(transient);
        replies.push(Reply::vector("closed-commit-upsert-delete"));
        let fixture = Fixture::new(replies).await;
        let stream = fixture
            .client()
            .database("demo")
            .collection("notes")
            .shape()
            .stream();
        pin_mut!(stream);
        stream.next().await.unwrap().unwrap();
        stream.next().await.unwrap().unwrap();
        assert_eq!(stream.next().await.unwrap().unwrap().offset, "4");
        assert_eq!(fixture.query(3), fixture.query(4));
        fixture.assert_auth();
    }
    let mut denied = Reply::body(json!({"error":{"code":"forbidden_scope"}}));
    denied.status = 403;
    denied
        .headers
        .push(("X-Request-ID".into(), "shape-denied".into()));
    let raw = denied.body.clone();
    let fixture = Fixture::new(vec![denied]).await;
    let stream = fixture
        .client()
        .database("demo")
        .collection("notes")
        .shape()
        .stream();
    pin_mut!(stream);
    let error = stream.next().await.unwrap().unwrap_err();
    assert_eq!(error.status(), Some(403));
    assert_eq!(error.code(), Some("forbidden_scope"));
    assert_eq!(error.raw_body(), Some(raw.as_slice()));
    assert_eq!(error.request_id(), Some("shape-denied"));
    assert!(stream.next().await.is_none());
    fixture.assert_auth();
}

#[tokio::test]
async fn invalid_response_never_changes_a_previously_yielded_state() {
    for mutate in [
        "version",
        "unknown_op",
        "handle",
        "next_offset",
        "snapshot_cursor",
        "fresh_snapshot",
    ] {
        let mut replies = snapshot();
        if mutate == "snapshot_cursor" || mutate == "fresh_snapshot" {
            replies.pop();
        }
        let mut body = if mutate == "snapshot_cursor" || mutate == "fresh_snapshot" {
            vector("snapshot-last-page")["response"]["body"].clone()
        } else {
            vector("closed-commit-upsert-delete")["response"]["body"].clone()
        };
        match mutate {
            "version" => {
                body["changes"][0]["ops"][1] =
                    json!({"op":"upsert","id":ID1,"version":"bad","document":{}})
            }
            "unknown_op" => body["changes"][0]["ops"][1]["op"] = json!("touch"),
            "handle" => body["handle"] = json!("h1-d90dddd41bd8282d"),
            "next_offset" => body["next_offset"] = json!("03"),
            "snapshot_cursor" => body["next_offset"] = json!("snap.2._w"),
            "fresh_snapshot" => body["up_to_date"] = json!(true),
            _ => unreachable!(),
        }
        replies.push(Reply::body(body));
        let fixture = Fixture::new(replies).await;
        let stream = fixture
            .client()
            .database("demo")
            .collection("notes")
            .shape()
            .stream();
        pin_mut!(stream);
        let mut before = stream.next().await.unwrap().unwrap();
        if mutate != "snapshot_cursor" && mutate != "fresh_snapshot" {
            before = stream.next().await.unwrap().unwrap();
        }
        let original = before
            .state
            .get(ID0.parse().unwrap())
            .unwrap()
            .document
            .get()
            .to_owned();
        assert!(stream.next().await.unwrap().is_err(), "{mutate}");
        assert_eq!(
            before
                .state
                .get(ID0.parse().unwrap())
                .unwrap()
                .document
                .get(),
            original
        );
        fixture.assert_auth();
    }
}

#[tokio::test]
async fn live_cursors_must_be_canonical_present_and_advance() {
    for cursor in [None, Some("01"), Some("51")] {
        let mut replies = snapshot();
        replies.push(Reply::vector("closed-commit-upsert-delete"));
        replies.push(Reply::body(
            vector("live-timeout")["response"]["body"].clone(),
        ));
        replies.push(Reply::vector("live-timeout"));
        let mut invalid = Reply::vector("live-timeout");
        invalid
            .headers
            .retain(|(name, _)| name != "TB-Shape-Cursor");
        if let Some(cursor) = cursor {
            invalid
                .headers
                .push(("TB-Shape-Cursor".into(), cursor.into()));
        }
        replies.push(invalid);
        let fixture = Fixture::new(replies).await;
        let stream = fixture
            .client()
            .database("demo")
            .collection("notes")
            .shape()
            .stream();
        pin_mut!(stream);
        for _ in 0..5 {
            stream.next().await.unwrap().unwrap();
        }
        assert!(stream.next().await.unwrap().is_err());
        fixture.assert_auth();
    }
}

#[tokio::test]
async fn redirect_targets_cannot_leak_credentials_or_leave_the_shape_route() {
    for location in ["https://object-store.example/shape?handle=h1-f3171a2230109328&offset=snap.2.0", "/v1/projects/01ARZ3NDEKTSV4RRFFQ69G5FAW/databases/other/collections/notes/shape?handle=h1-f3171a2230109328&offset=snap.2.0", "/v1/projects/01ARZ3NDEKTSV4RRFFQ69G5FAW/databases/demo/collections/notes/shape?handle=h1-f3171a2230109328&offset=snap.2.0&handle=h1-f3171a2230109328"] {
        let mut reply = Reply::vector("bootstrap");
        for (name,value) in &mut reply.headers { if name == "Location" { *value = location.into(); } }
        let fixture = Fixture::new(vec![reply]).await;
        let stream = fixture.client().database("demo").collection("notes").shape().stream();
        pin_mut!(stream);
        assert!(stream.next().await.unwrap().is_err());
        assert_eq!(fixture.requests.lock().unwrap().len(),1);
        fixture.assert_auth();
    }
    let mut deferred = Reply::vector("bootstrap");
    deferred.status = 302;
    let fixture = Fixture::new(vec![deferred]).await;
    let stream = fixture
        .client()
        .database("demo")
        .collection("notes")
        .shape()
        .stream();
    pin_mut!(stream);
    assert_eq!(
        stream.next().await.unwrap().unwrap_err().status(),
        Some(302)
    );
    fixture.assert_auth();
}

#[tokio::test]
async fn percent_encoded_filter_limit_is_checked_before_sending() {
    let raw = json!({"eq":["/value", "[\"".repeat(1500)]}).to_string();
    assert!(raw.len() < 8 << 10);
    let fixture = Fixture::new(vec![]).await;
    let stream = fixture
        .client()
        .database("demo")
        .collection("notes")
        .shape()
        .filter_json(&raw)
        .unwrap()
        .stream();
    pin_mut!(stream);
    let error = stream.next().await.unwrap().unwrap_err();
    assert!(matches!(error, terrascale::v1::Error::InvalidInput(_)));
    assert!(stream.next().await.is_none());
    assert!(fixture.requests.lock().unwrap().is_empty());
}

#[tokio::test]
async fn canonical_filter_from_redirect_is_used_for_continuations() {
    let raw = r#"{ "eq" : [ "/read", false ] }"#;
    let canonical = r#"{"eq":["/read",false]}"#;
    let mut replies = snapshot();
    for (name, value) in &mut replies[0].headers {
        if name == "Location" {
            let mut url = url::Url::parse(&format!("http://localhost{value}")).unwrap();
            url.query_pairs_mut().append_pair("where", canonical);
            *value = format!("{}?{}", url.path(), url.query().unwrap());
        }
    }
    let fixture = Fixture::new(replies).await;
    let stream = fixture
        .client()
        .database("demo")
        .collection("notes")
        .shape()
        .filter_json(raw)
        .unwrap()
        .stream();
    pin_mut!(stream);
    stream.next().await.unwrap().unwrap();
    stream.next().await.unwrap().unwrap();
    assert_eq!(fixture.query(0)["where"], raw);
    assert_eq!(fixture.query(1)["where"], canonical);
    assert_eq!(fixture.query(2)["where"], canonical);
    fixture.assert_auth();
}

fn unhex(s: &str) -> Vec<u8> {
    (0..s.len())
        .step_by(2)
        .map(|i| u8::from_str_radix(&s[i..i + 2], 16).unwrap())
        .collect()
}

#[tokio::test]
async fn invalid_epoch_key_responses_exclude_secret_bytes_from_diagnostics() {
    let v = vectors();
    let crypto = &v["tbs1_vectors"][0];
    let object = unhex(crypto["tbs1_hex"].as_str().unwrap());
    for failure in ["identity", "encoding", "length", "json"] {
        let mut body = vector("epoch-key")["response"]["body"].clone();
        match failure {
            "identity" => body["epoch"] = json!("101"),
            "encoding" => body["key"] = json!("secret+invalid"),
            "length" => body["key"] = json!("c2VjcmV0"),
            "json" => {}
            _ => unreachable!(),
        }
        let mut reply = Reply::body(body);
        if failure == "json" {
            reply.body.pop();
        }
        let fixture = Fixture::new(vec![reply]).await;
        let shape = fixture
            .client()
            .database("demo")
            .collection("notes")
            .shape();
        let error = shape
            .decrypt_sealed(
                &object,
                crypto["handle"].as_str().unwrap(),
                crypto["request_offset"].as_str().unwrap(),
                100,
                crypto["key_id"].as_str().unwrap(),
            )
            .await
            .unwrap_err();
        assert_eq!(error.status(), Some(200));
        assert_eq!(error.raw_body(), Some([].as_slice()), "{failure}");
        assert!(!error.to_string().contains("secret"));
        fixture.assert_auth();
    }
}

#[tokio::test]
async fn epoch_key_is_no_store_rotates_and_binds_decryption_to_authorized_offset() {
    use base64::Engine as _;
    let v = vectors();
    let crypto = &v["tbs1_vectors"][0];
    let handle = crypto["handle"].as_str().unwrap();
    let offset = crypto["request_offset"].as_str().unwrap();
    let key_id = crypto["key_id"].as_str().unwrap();
    let object = unhex(crypto["tbs1_hex"].as_str().unwrap());
    let fixture = Fixture::new(vec![Reply::vector("epoch-key"), Reply::vector("epoch-key")]).await;
    let shape = fixture
        .client()
        .database("demo")
        .collection("notes")
        .shape();
    let plaintext = shape
        .decrypt_sealed(&object, handle, offset, 100, key_id)
        .await
        .unwrap();
    assert_eq!(
        plaintext,
        crypto["plaintext_utf8"].as_str().unwrap().as_bytes()
    );
    assert!(shape
        .decrypt_sealed(&object, handle, "3", 100, key_id)
        .await
        .is_err());
    for req in fixture.requests.lock().unwrap().iter() {
        assert_eq!(req.headers["cache-control"], "no-store");
    }
    fixture.assert_auth();
    let mut epoch101 = vector("epoch-key")["response"]["body"].clone();
    epoch101["epoch"] = json!("101");
    epoch101["key"] = json!(base64::engine::general_purpose::URL_SAFE_NO_PAD.encode([0u8; 32]));
    let fixture = Fixture::new(vec![Reply::body(epoch101)]).await;
    let shape = fixture
        .client()
        .database("demo")
        .collection("notes")
        .shape();
    // The server can release a different epoch key, but epoch100 ciphertext cannot pass epoch101.
    assert!(shape
        .decrypt_sealed(&object, handle, offset, 101, key_id)
        .await
        .is_err());
    assert_eq!(fixture.query(0)["epoch"], "101");
    fixture.assert_auth();
}

fn current_epoch() -> u64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap()
        .as_secs()
        / 600
}

fn key_reply(handle: &str, epoch: u64, key_id: &str, key: &[u8]) -> Reply {
    use base64::Engine as _;
    let mut reply = Reply::vector("epoch-key");
    reply.body = json!({
        "handle": handle,
        "epoch": epoch.to_string(),
        "key_id": key_id,
        "key": base64::engine::general_purpose::URL_SAFE_NO_PAD.encode(key),
    })
    .to_string()
    .into_bytes();
    reply
}

/// Test-only sealing of one single-frame TBS1 object (section 12).
fn seal(
    epoch: u64,
    key_id: &str,
    handle: &str,
    offset: &str,
    epoch_key: &[u8],
    salt: &[u8],
    plaintext: &[u8],
) -> Vec<u8> {
    let mut header = b"TBS1".to_vec();
    header.extend_from_slice(&epoch.to_be_bytes());
    header.extend_from_slice(key_id.as_bytes());
    header.extend_from_slice(salt);
    let mut binding = handle.as_bytes().to_vec();
    binding.push(0);
    binding.extend_from_slice(offset.as_bytes());
    let mut object_key = [0u8; 32];
    hkdf::Salt::new(hkdf::HKDF_SHA256, salt)
        .extract(epoch_key)
        .expand(
            &[&b"TBS1 object\0"[..], binding.as_slice()],
            hkdf::HKDF_SHA256,
        )
        .and_then(|okm| okm.fill(&mut object_key))
        .unwrap();
    let word = plaintext.len() as u32 | (1 << 31);
    let mut aad = header.clone();
    aad.extend_from_slice(&0u64.to_be_bytes());
    aad.extend_from_slice(&word.to_be_bytes());
    let key =
        aead::LessSafeKey::new(aead::UnboundKey::new(&aead::AES_256_GCM, &object_key).unwrap());
    let mut body = plaintext.to_vec();
    key.seal_in_place_append_tag(
        aead::Nonce::assume_unique_for_key([0u8; 12]),
        aead::Aad::from(aad.as_slice()),
        &mut body,
    )
    .unwrap();
    let mut object = header;
    object.extend_from_slice(&word.to_be_bytes());
    object.extend_from_slice(&body);
    object
}

#[tokio::test]
async fn current_epoch_key_is_fetched_once_for_clones_and_all_offsets() {
    use base64::Engine as _;
    let v = vectors();
    let crypto = &v["tbs1_vectors"][0];
    let handle = crypto["handle"].as_str().unwrap();
    let key_id = crypto["key_id"].as_str().unwrap();
    let epoch_key = unhex(crypto["epoch_key_hex"].as_str().unwrap());
    let salt = unhex(crypto["object_salt_hex"].as_str().unwrap());
    let plaintext = crypto["plaintext_utf8"].as_str().unwrap().as_bytes();
    // The helper reproduces the pinned epoch-100 object, so its sealing matches section 12.
    assert_eq!(
        seal(100, key_id, handle, "2", &epoch_key, &salt, plaintext),
        unhex(crypto["tbs1_hex"].as_str().unwrap())
    );

    let epoch = current_epoch();
    let at_two = seal(epoch, key_id, handle, "2", &epoch_key, &salt, plaintext);
    let at_four = seal(
        epoch,
        key_id,
        handle,
        "4",
        &epoch_key,
        &[0x44; 32],
        plaintext,
    );
    let fixture = Fixture::new(vec![
        key_reply(handle, epoch, key_id, &epoch_key),
        key_reply(handle, epoch, key_id, &epoch_key),
    ])
    .await;
    let client = fixture.client();
    let shape = client.database("demo").collection("notes").shape();
    let (two, four) = tokio::join!(
        shape.decrypt_sealed(&at_two, handle, "2", epoch, key_id),
        shape.decrypt_sealed(&at_four, handle, "4", epoch, key_id),
    );
    assert_eq!(two.unwrap(), plaintext);
    assert_eq!(four.unwrap(), plaintext);
    let cloned = client.clone().database("demo").collection("notes").shape();
    assert_eq!(
        cloned
            .decrypt_sealed(&at_two, handle, "2", epoch, key_id)
            .await
            .unwrap(),
        plaintext
    );
    // The cached key opens only the object whose binding it was sealed for.
    assert!(shape
        .decrypt_sealed(&at_four, handle, "2", epoch, key_id)
        .await
        .is_err());
    let key_b64 = base64::engine::general_purpose::URL_SAFE_NO_PAD.encode(epoch_key.as_slice());
    assert!(!format!("{client:?}{shape:?}").contains(&key_b64));
    assert_eq!(fixture.requests.lock().unwrap().len(), 1);
    // A separate credential-bearing client must authorize its own key fetch.
    let separate = fixture
        .client()
        .database("demo")
        .collection("notes")
        .shape();
    assert_eq!(
        separate
            .decrypt_sealed(&at_two, handle, "2", epoch, key_id)
            .await
            .unwrap(),
        plaintext
    );
    assert_eq!(fixture.requests.lock().unwrap().len(), 2);
    fixture.assert_auth();
}

#[tokio::test]
async fn cached_keys_are_separated_by_handle_epoch_and_key_id() {
    let v = vectors();
    let crypto = &v["tbs1_vectors"][0];
    let handle = crypto["handle"].as_str().unwrap();
    let key_id = crypto["key_id"].as_str().unwrap();
    let epoch_key = unhex(crypto["epoch_key_hex"].as_str().unwrap());
    let salt = unhex(crypto["object_salt_hex"].as_str().unwrap());
    let plaintext = crypto["plaintext_utf8"].as_str().unwrap().as_bytes();
    let epoch = current_epoch();
    let at_two = seal(epoch, key_id, handle, "2", &epoch_key, &salt, plaintext);
    let other_handle = seal(
        epoch,
        key_id,
        OTHER_HANDLE,
        "2",
        &epoch_key,
        &salt,
        plaintext,
    );
    let other_key_id = seal(
        epoch,
        OTHER_KEY_ID,
        handle,
        "2",
        &epoch_key,
        &salt,
        plaintext,
    );
    let fixture = Fixture::new(vec![
        key_reply(handle, epoch, key_id, &epoch_key),
        key_reply(OTHER_HANDLE, epoch, key_id, &epoch_key),
        key_reply(handle, epoch, OTHER_KEY_ID, &epoch_key),
    ])
    .await;
    let client = fixture.client();
    let shape = client.database("demo").collection("notes").shape();
    shape
        .decrypt_sealed(&at_two, handle, "2", epoch, key_id)
        .await
        .unwrap();
    shape
        .decrypt_sealed(&other_handle, OTHER_HANDLE, "2", epoch, key_id)
        .await
        .unwrap();
    shape
        .decrypt_sealed(&other_key_id, handle, "2", epoch, OTHER_KEY_ID)
        .await
        .unwrap();
    // The original tuple is still cached after the other two were added.
    assert_eq!(
        shape
            .decrypt_sealed(&at_two, handle, "2", epoch, key_id)
            .await
            .unwrap(),
        plaintext
    );
    assert_eq!(fixture.requests.lock().unwrap().len(), 3);
    assert_eq!(fixture.query(1)["handle"], OTHER_HANDLE);
    assert_eq!(fixture.query(2)["key_id"], OTHER_KEY_ID);
    fixture.assert_auth();
}

#[tokio::test]
async fn key_endpoint_conflict_evicts_every_cached_key() {
    let v = vectors();
    let crypto = &v["tbs1_vectors"][0];
    let handle = crypto["handle"].as_str().unwrap();
    let key_id = crypto["key_id"].as_str().unwrap();
    let epoch_key = unhex(crypto["epoch_key_hex"].as_str().unwrap());
    let salt = unhex(crypto["object_salt_hex"].as_str().unwrap());
    let plaintext = crypto["plaintext_utf8"].as_str().unwrap().as_bytes();
    let epoch = current_epoch();
    let at_two = seal(epoch, key_id, handle, "2", &epoch_key, &salt, plaintext);
    let mut conflict = Reply::body(json!({"error":{"code":"must_refetch"}}));
    conflict.status = 409;
    let fixture = Fixture::new(vec![
        key_reply(handle, epoch, key_id, &epoch_key),
        conflict,
        key_reply(handle, epoch, key_id, &epoch_key),
    ])
    .await;
    let client = fixture.client();
    let shape = client.database("demo").collection("notes").shape();
    shape
        .decrypt_sealed(&at_two, handle, "2", epoch, key_id)
        .await
        .unwrap();
    let error = shape
        .decrypt_sealed(&at_two, handle, "2", epoch, OTHER_KEY_ID)
        .await
        .unwrap_err();
    assert_eq!(error.status(), Some(409));
    assert_eq!(
        shape
            .decrypt_sealed(&at_two, handle, "2", epoch, key_id)
            .await
            .unwrap(),
        plaintext
    );
    assert_eq!(fixture.requests.lock().unwrap().len(), 3);
    assert_eq!(fixture.query(2)["key_id"], key_id);
    fixture.assert_auth();
}

#[tokio::test]
async fn stream_conflict_evicts_cached_keys_on_the_same_client() {
    let v = vectors();
    let crypto = &v["tbs1_vectors"][0];
    let handle = crypto["handle"].as_str().unwrap();
    let key_id = crypto["key_id"].as_str().unwrap();
    let epoch_key = unhex(crypto["epoch_key_hex"].as_str().unwrap());
    let salt = unhex(crypto["object_salt_hex"].as_str().unwrap());
    let plaintext = crypto["plaintext_utf8"].as_str().unwrap().as_bytes();
    let epoch = current_epoch();
    let at_two = seal(epoch, key_id, handle, "2", &epoch_key, &salt, plaintext);
    let mut replies = vec![key_reply(handle, epoch, key_id, &epoch_key)];
    replies.extend(snapshot());
    replies.push(Reply::vector("expired-offset"));
    replies.extend(snapshot());
    replies.push(key_reply(handle, epoch, key_id, &epoch_key));
    let fixture = Fixture::new(replies).await;
    let client = fixture.client();
    let shape = client.database("demo").collection("notes").shape();
    shape
        .decrypt_sealed(&at_two, handle, "2", epoch, key_id)
        .await
        .unwrap();
    let stream = client.database("demo").collection("notes").shape().stream();
    pin_mut!(stream);
    // Snapshot, then the 409 restarts it: two updates each.
    for _ in 0..4 {
        stream.next().await.unwrap().unwrap();
    }
    assert_eq!(
        shape
            .decrypt_sealed(&at_two, handle, "2", epoch, key_id)
            .await
            .unwrap(),
        plaintext
    );
    assert_eq!(fixture.requests.lock().unwrap().len(), 9);
    assert_eq!(fixture.query(8)["epoch"], epoch.to_string());
    fixture.assert_auth();
}
