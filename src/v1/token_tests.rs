//! HTTP contract fixtures; no production service or credential files.
use super::*;
use base64::{engine::general_purpose::STANDARD, Engine};
use bytes::Bytes;
use http_body_util::{BodyExt, Full};
use hyper::{service::service_fn, Request, Response};
use hyper_util::rt::TokioIo;
use serde_json::json;
use std::sync::{
    atomic::{AtomicUsize, Ordering},
    Mutex,
};

#[path = "token_failure_tests.rs"]
mod failures;

const PROJECT: &str = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
const KEY_ID: &str = "01ARZ3NDEKTSV4RRFFQ69G5FAX";
fn key() -> String {
    format!("{KEY_ID}_{}", "a".repeat(52))
}
fn grant(token: &str, lifetime: u64) -> String {
    json!({"access_token":token,"token_type":"Bearer","expires_in":lifetime}).to_string()
}

struct Seen {
    path: String,
    headers: HeaderMap,
    body: Vec<u8>,
}
struct Fixture {
    url: String,
    seen: Arc<Mutex<Vec<Seen>>>,
    task: tokio::task::JoinHandle<()>,
}
impl Drop for Fixture {
    fn drop(&mut self) {
        self.task.abort();
    }
}
impl Fixture {
    async fn new(
        handler: impl Fn(&Seen) -> (u16, &'static str, String) + Send + Sync + 'static,
    ) -> Self {
        Self::gated(handler, None).await
    }
    async fn gated(
        handler: impl Fn(&Seen) -> (u16, &'static str, String) + Send + Sync + 'static,
        gate: Option<Arc<tokio::sync::Barrier>>,
    ) -> Self {
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let url = format!("http://{}", listener.local_addr().unwrap());
        let seen = Arc::new(Mutex::new(Vec::new()));
        let (requests, handler) = (seen.clone(), Arc::new(handler));
        let task = tokio::spawn(async move {
            let mut connections = tokio::task::JoinSet::new();
            loop {
                tokio::select! {
                    accepted = listener.accept() => {
                        let (socket, _) = accepted.unwrap();
                        let (seen, handler, gate) = (requests.clone(), handler.clone(), gate.clone());
                        connections.spawn(async move {
                            let service = service_fn(move |req: Request<hyper::body::Incoming>| {
                                let (seen, handler, gate) = (seen.clone(), handler.clone(), gate.clone());
                                async move {
                                    let (parts, body) = req.into_parts();
                                    let request = Seen { path: parts.uri.to_string(), headers: parts.headers,
                                        body: body.collect().await.unwrap().to_bytes().to_vec() };
                                    let response = handler(&request);
                                    seen.lock().unwrap().push(request);
                                    if response.0 == 401 {
                                        if let Some(gate) = gate { gate.wait().await; }
                                    }
                                    Ok::<_, std::convert::Infallible>(Response::builder().status(response.0)
                                        .header(CONTENT_TYPE, response.1).body(Full::new(Bytes::from(response.2))).unwrap())
                                }
                            });
                            let _ = hyper::server::conn::http1::Builder::new().serve_connection(TokioIo::new(socket), service).await;
                        });
                    }
                    _ = connections.join_next(), if !connections.is_empty() => {}
                }
            }
        });
        Self { url, seen, task }
    }
    fn client(&self, issuer: &Fixture) -> Client {
        self.project_client(issuer, PROJECT)
    }
    fn project_client(&self, issuer: &Fixture, project: &str) -> Client {
        let mut client = Client::for_project_api_key(project, key())
            .endpoint(&self.url)
            .retry_policy(RetryPolicy::none())
            .build()
            .unwrap();
        let Auth::Key(key) = &mut Arc::get_mut(&mut client.inner).unwrap().auth else {
            unreachable!()
        };
        Arc::get_mut(key).unwrap().endpoint = format!("{}/terrascale/oauth/token", issuer.url)
            .parse()
            .unwrap();
        client
    }
}
fn ok(body: String) -> (u16, &'static str, String) {
    (200, "application/json", body)
}
async fn read(client: &Client) -> Result<Resp> {
    client
        .send(Req::new(Method::GET, vec!["v1", "databases"]))
        .await
}
async fn advance(seconds: u64) {
    tokio::time::pause();
    tokio::time::advance(Duration::from_secs(seconds)).await;
    tokio::time::resume();
}

#[tokio::test]
async fn exchanges_project_key_once_and_caches_across_concurrent_clones() {
    let auth = Fixture::new(|_| ok(grant("access-one", 900))).await;
    let data = Fixture::new(|_| ok("{}".into())).await;
    let client = data.client(&auth);
    let mut tasks = tokio::task::JoinSet::new();
    for _ in 0..16 {
        let client = client.clone();
        tasks.spawn(async move {
            read(&client).await.unwrap();
        });
    }
    while let Some(result) = tasks.join_next().await {
        result.unwrap();
    }
    let seen = auth.seen.lock().unwrap();
    assert_eq!(seen.len(), 1);
    assert_eq!(seen[0].path, "/terrascale/oauth/token");
    assert_eq!(
        seen[0].headers[CONTENT_TYPE],
        "application/x-www-form-urlencoded"
    );
    assert_eq!(
        seen[0].headers[AUTHORIZATION],
        format!("Basic {}", STANDARD.encode(format!("{KEY_ID}:{}", key())))
    );
    assert_eq!(
        String::from_utf8_lossy(&seen[0].body),
        format!("grant_type=client_credentials&project={PROJECT}")
    );
    assert!(data
        .seen
        .lock()
        .unwrap()
        .iter()
        .all(|r| r.path == format!("/v1/projects/{PROJECT}/databases")
            && r.headers[AUTHORIZATION] == "Bearer access-one"));
}

#[tokio::test]
async fn refreshes_before_expiry_and_at_half_life_for_short_tokens() {
    for (lifetime, margin) in [(900, 60), (10, 5)] {
        let auth = Fixture::new(move |_| ok(grant("access", lifetime))).await;
        let data = Fixture::new(|_| ok("{}".into())).await;
        let client = data.client(&auth);
        read(&client).await.unwrap();
        advance(lifetime - margin - 1).await;
        read(&client).await.unwrap();
        assert_eq!(auth.seen.lock().unwrap().len(), 1);
        advance(2).await;
        read(&client).await.unwrap();
        // The renewal runs in the background; the read did not wait for it.
        wait_for_exchanges(&auth, 2).await;
        read(&client).await.unwrap();
        assert_eq!(auth.seen.lock().unwrap().len(), 2);
    }
}

async fn wait_for_exchanges(auth: &Fixture, count: usize) {
    tokio::time::timeout(Duration::from_secs(5), async {
        while auth.seen.lock().unwrap().len() < count {
            tokio::time::sleep(Duration::from_millis(5)).await;
        }
    })
    .await
    .unwrap();
}

#[tokio::test]
async fn renewal_keeps_serving_the_valid_token_and_blocks_only_after_expiry() {
    let count = AtomicUsize::new(0);
    let auth = Fixture::new(move |_| {
        let n = count.fetch_add(1, Ordering::SeqCst);
        ok(grant(["one", "two", "three"][n.min(2)], 60))
    })
    .await;
    let data = Fixture::new(|_| ok("{}".into())).await;
    let client = data.client(&auth);
    read(&client).await.unwrap();
    // 60 s grant: background renewal from 30 s, blocking refresh from 50 s.
    advance(31).await;
    let mut readers = tokio::task::JoinSet::new();
    for _ in 0..8 {
        let client = client.clone();
        readers.spawn(async move { read(&client).await.unwrap() });
    }
    while let Some(result) = readers.join_next().await {
        result.unwrap();
    }
    wait_for_exchanges(&auth, 2).await;
    read(&client).await.unwrap();
    {
        let seen = data.seen.lock().unwrap();
        assert!(seen[..9]
            .iter()
            .all(|r| r.headers[AUTHORIZATION] == "Bearer one"));
        assert_eq!(seen[9].headers[AUTHORIZATION], "Bearer two");
    }
    // Without a successful renewal the token is replaced before it expires.
    advance(51).await;
    read(&client).await.unwrap();
    assert_eq!(auth.seen.lock().unwrap().len(), 3);
    assert_eq!(
        data.seen.lock().unwrap()[10].headers[AUTHORIZATION],
        "Bearer three"
    );
}

#[tokio::test]
async fn failed_background_renewal_keeps_the_token_and_paces_retries() {
    let count = AtomicUsize::new(0);
    let auth = Fixture::new(move |_| match count.fetch_add(1, Ordering::SeqCst) {
        0 => ok(grant("one", 60)),
        1 => (503, "application/json", "{}".into()),
        _ => ok(grant("two", 60)),
    })
    .await;
    let data = Fixture::new(|_| ok("{}".into())).await;
    let client = data.client(&auth);
    read(&client).await.unwrap();
    advance(31).await;
    read(&client).await.unwrap();
    wait_for_exchanges(&auth, 2).await;
    // Paused for one second after the failure: no exchange storm.
    for _ in 0..5 {
        read(&client).await.unwrap();
    }
    assert_eq!(auth.seen.lock().unwrap().len(), 2);
    advance(2).await;
    read(&client).await.unwrap();
    wait_for_exchanges(&auth, 3).await;
    read(&client).await.unwrap();
    let seen = data.seen.lock().unwrap();
    assert!(seen[..8]
        .iter()
        .all(|r| r.headers[AUTHORIZATION] == "Bearer one"));
    assert_eq!(seen[8].headers[AUTHORIZATION], "Bearer two");
}

#[tokio::test]
async fn simultaneous_401s_share_one_refresh_even_if_issuer_reuses_token_text() {
    let auth = Fixture::new(|_| ok(grant("same-token", 900))).await;
    let calls = Arc::new(AtomicUsize::new(0));
    let count = calls.clone();
    let data = Fixture::gated(
        move |_| {
            if count.fetch_add(1, Ordering::SeqCst) < 8 {
                (401, "application/json", "{}".into())
            } else {
                ok("{}".into())
            }
        },
        Some(Arc::new(tokio::sync::Barrier::new(8))),
    )
    .await;
    let client = data.client(&auth);
    let mut tasks = tokio::task::JoinSet::new();
    for _ in 0..8 {
        let client = client.clone();
        tasks.spawn(async move {
            read(&client).await.unwrap();
        });
    }
    tokio::time::timeout(Duration::from_secs(5), async {
        while let Some(result) = tasks.join_next().await {
            result.unwrap();
        }
    })
    .await
    .unwrap();
    assert_eq!(auth.seen.lock().unwrap().len(), 2);
    assert_eq!(calls.load(Ordering::SeqCst), 16);
}

#[tokio::test]
async fn mutation_replay_preserves_body_and_headers_after_401_only() {
    let count = AtomicUsize::new(0);
    let auth = Fixture::new(move |_| {
        ok(grant(
            if count.fetch_add(1, Ordering::SeqCst) == 0 {
                "old"
            } else {
                "new"
            },
            900,
        ))
    })
    .await;
    let data = Fixture::new(|r| {
        if r.headers[AUTHORIZATION] == "Bearer old" {
            (401, "application/json", "{}".into())
        } else {
            ok("{}".into())
        }
    })
    .await;
    let client = data.client(&auth);
    let mut req = Req::new(Method::POST, vec!["v1", "databases"])
        .json(&json!({"name":"orders"}))
        .unwrap();
    req.headers.push(("If-Match", "etag".into()));
    client.send(req).await.unwrap();
    let requests = data.seen.lock().unwrap();
    assert_eq!(requests.len(), 2);
    assert_eq!(requests[0].body, requests[1].body);
    assert_eq!(requests[0].path, requests[1].path);
    assert_eq!(requests[1].headers["if-match"], "etag");
    assert_eq!(requests[1].headers[AUTHORIZATION], "Bearer new");
}

#[tokio::test]
async fn only_one_401_refresh_and_no_403_or_uncertain_write_replay() {
    for (status, exchanges, attempts) in [(401, 2, 2), (403, 1, 1), (503, 1, 1)] {
        let auth = Fixture::new(|_| ok(grant("access", 900))).await;
        let data = Fixture::new(move |_| {
            (
                status,
                "application/json",
                json!({"error":{"code":"commit_uncertain"}}).to_string(),
            )
        })
        .await;
        let mut client = data.client(&auth);
        Arc::get_mut(&mut client.inner).unwrap().retry = RetryPolicy::default();
        let error = client
            .send(Req::new(Method::POST, vec!["v1", "databases"]))
            .await
            .err()
            .unwrap();
        assert_eq!(error.status(), Some(status));
        assert_eq!(error.is_uncertain(), status == 503);
        assert_eq!(data.seen.lock().unwrap().len(), attempts);
        assert_eq!(auth.seen.lock().unwrap().len(), exchanges);
    }
}

#[tokio::test]
async fn static_bearers_and_anonymous_probes_never_exchange() {
    let auth = Fixture::new(|_| panic!("unexpected exchange")).await;
    let data = Fixture::new(|_| (401, "application/json", "{}".into())).await;
    let client = Client::for_project(PROJECT, "static")
        .endpoint(&data.url)
        .build()
        .unwrap();
    assert_eq!(read(&client).await.err().unwrap().status(), Some(401));
    let client = data.client(&auth);
    client.probe(vec!["health"]).await.unwrap();
    assert!(auth.seen.lock().unwrap().is_empty());
    assert!(!data.seen.lock().unwrap()[1]
        .headers
        .contains_key(AUTHORIZATION));
}
