//! Local, genuinely signed Atlas fixtures; no production network access.
use std::collections::{HashMap, VecDeque};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::time::{SystemTime, UNIX_EPOCH};

use base64::{engine::general_purpose::URL_SAFE_NO_PAD, Engine as _};
use bytes::Bytes;
use futures_util::StreamExt;
use http_body_util::Full;
use hyper::{service::service_fn, Request, Response};
use hyper_util::rt::TokioIo;
use ring::{
    rand::SystemRandom,
    signature::{EcdsaKeyPair, KeyPair, ECDSA_P256_SHA256_FIXED_SIGNING},
};
use serde_json::{json, Value};
use terrascale::v1::{is_valid_name, Error, Result};
use uuid::Uuid;

// Exercise the private resolver directly as well as the parent's public client wiring.
#[path = "../src/v1/atlas.rs"]
mod atlas;
const PROJECT: &str = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
use atlas::AtlasResolver;

struct Fixture {
    url: String,
    response: Arc<Mutex<Value>>,
    keys: Arc<Mutex<Value>>,
    requests: Arc<Mutex<Vec<(String, bool)>>>,
    replacement: Arc<Mutex<Option<Value>>>,
    fault: Arc<Mutex<Option<(u16, usize)>>>,
    disconnected: Arc<AtomicBool>,
    scripted: Arc<Mutex<VecDeque<Reply>>>,
    shape_seen: Arc<Mutex<Vec<ShapeRequest>>>,
    signer: EcdsaKeyPair,
    task: tokio::task::JoinHandle<()>,
}
impl Drop for Fixture {
    fn drop(&mut self) {
        self.task.abort();
    }
}
impl Fixture {
    async fn new() -> Self {
        let rng = SystemRandom::new();
        let pkcs8 = EcdsaKeyPair::generate_pkcs8(&ECDSA_P256_SHA256_FIXED_SIGNING, &rng).unwrap();
        let signer =
            EcdsaKeyPair::from_pkcs8(&ECDSA_P256_SHA256_FIXED_SIGNING, pkcs8.as_ref(), &rng)
                .unwrap();
        let public = signer.public_key().as_ref();
        let keys = Arc::new(Mutex::new(
            json!({"keys":[{"kty":"EC", "crv":"P-256", "alg":"ES256", "use":"sig", "kid":"fixture", "x":URL_SAFE_NO_PAD.encode(&public[1..33]), "y":URL_SAFE_NO_PAD.encode(&public[33..])}]}),
        ));
        let response = Arc::new(Mutex::new(Value::Null));
        let requests = Arc::new(Mutex::new(Vec::new()));
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let url = format!("http://{}", listener.local_addr().unwrap());
        let fault = Arc::new(Mutex::new(None::<(u16, usize)>));
        let disconnected = Arc::new(AtomicBool::new(false));
        let faults = fault.clone();
        let replacement = Arc::new(Mutex::new(None::<Value>));
        let replacements = replacement.clone();
        let scripted = Arc::new(Mutex::new(VecDeque::<Reply>::new()));
        let shape_seen = Arc::new(Mutex::new(Vec::<ShapeRequest>::new()));
        let (sc, sh) = (scripted.clone(), shape_seen.clone());
        let (r, k, q, d) = (
            response.clone(),
            keys.clone(),
            requests.clone(),
            disconnected.clone(),
        );
        let task = tokio::spawn(async move {
            while let Ok((socket, _)) = listener.accept().await {
                let (scripted, shapes) = (sc.clone(), sh.clone());
                let (r, k, q, d, faults, replacements) = (
                    r.clone(),
                    k.clone(),
                    q.clone(),
                    d.clone(),
                    faults.clone(),
                    replacements.clone(),
                );
                tokio::spawn(async move {
                    let service = service_fn(move |request: Request<hyper::body::Incoming>| {
                        q.lock().unwrap().push((
                            request.uri().to_string(),
                            request.headers().contains_key("authorization"),
                        ));
                        let path = request.uri().path();
                        let disconnected = d.load(Ordering::Relaxed);
                        let shape = path.ends_with("/shape").then(|| {
                            shapes.lock().unwrap().push(ShapeRequest::new(&request));
                            scripted
                                .lock()
                                .unwrap()
                                .pop_front()
                                .unwrap_or_else(|| Reply {
                                    status: 404,
                                    headers: Vec::new(),
                                    body: json!({"error":{"code":"unscripted"}}).to_string(),
                                })
                        });
                        let data_reply = path != "/v1/keys" && path != "/v1/resolve";
                        let (status, body) = if let Some(reply) = &shape {
                            (reply.status, reply.body.clone())
                        } else if path == "/v1/keys" {
                            (200, k.lock().unwrap().to_string())
                        } else if path == "/v1/resolve" {
                            (200, r.lock().unwrap().to_string())
                        } else {
                            assert_eq!(
                                request.headers()["authorization"],
                                "Bearer fixture-access-token"
                            );
                            assert_eq!(
                                request.headers()["x-terrascale-region-pin"],
                                "new-region-9"
                            );
                            let mut faults = faults.lock().unwrap();
                            match faults.as_mut() {
                                Some((status, remaining)) if *remaining > 0 => {
                                    *remaining -= 1;
                                    if let Some(next) = replacements.lock().unwrap().take() {
                                        *r.lock().unwrap() = next;
                                    }
                                    (
                                        *status,
                                        json!({"error":{"code":"home_region_required"}})
                                            .to_string(),
                                    )
                                }
                                _ => (404, json!({"error":{"code":"not_found"}}).to_string()),
                            }
                        };
                        async move {
                            if disconnected {
                                return Err(std::io::Error::other("fixture unavailable"));
                            }
                            let mut builder = Response::builder().status(status);
                            match &shape {
                                Some(reply) => {
                                    for (name, value) in &reply.headers {
                                        builder = builder.header(name, value);
                                    }
                                }
                                None => {
                                    if data_reply {
                                        builder =
                                            builder.header("x-terrabase-region", "new-region-9");
                                    }
                                    builder = builder
                                        .header("content-type", "application/json")
                                        .header("location", "https://credential-trap.invalid/");
                                }
                            }
                            Ok::<_, std::io::Error>(
                                builder.body(Full::new(Bytes::from(body))).unwrap(),
                            )
                        }
                    });
                    let _ = hyper::server::conn::http1::Builder::new()
                        .serve_connection(TokioIo::new(socket), service)
                        .await;
                });
            }
        });
        let fixture = Self {
            url,
            response,
            keys,
            requests,
            replacement,
            fault,
            disconnected,
            scripted,
            shape_seen,
            signer,
            task,
        };
        fixture.publish(json!({}), json!({}));
        fixture
    }
    fn publish(&self, response_changes: Value, claim_changes: Value) {
        let now = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_secs();
        let mut response = json!({"v":1,"project":PROJECT,"database":"orders", "region":"new-region-9", "api":self.url, "auth":self.url, "health":"down", "placement":"known", "stale":false, "expires":now+600});
        for (k, v) in response_changes.as_object().unwrap() {
            response[k] = v.clone();
        }
        let mut claims = json!({"iss":"atlas.terrascale.tech","project":response["project"],"database":response["database"],"region":response["region"],"api":response["api"],"iat":now,"exp":response["expires"]});
        for (k, v) in claim_changes.as_object().unwrap() {
            claims[k] = v.clone();
        }
        let header =
            URL_SAFE_NO_PAD.encode(json!({"alg":"ES256","typ":"JWT","kid":"fixture"}).to_string());
        let payload = URL_SAFE_NO_PAD.encode(claims.to_string());
        let message = format!("{header}.{payload}");
        let signature = self
            .signer
            .sign(&SystemRandom::new(), message.as_bytes())
            .unwrap();
        response["pin"] =
            format!("{message}.{}", URL_SAFE_NO_PAD.encode(signature.as_ref())).into();
        *self.response.lock().unwrap() = response;
    }
    fn move_after_wrong_route(&self, target: &Fixture) {
        let original = self.response.lock().unwrap().clone();
        self.publish(json!({"api":target.url}), json!({}));
        *self.replacement.lock().unwrap() = Some(self.response.lock().unwrap().clone());
        *self.response.lock().unwrap() = original;
    }
    fn resolver(&self) -> AtlasResolver {
        AtlasResolver::new(
            reqwest::Client::builder()
                .no_proxy()
                .redirect(reqwest::redirect::Policy::none())
                .build()
                .unwrap(),
            self.url.parse().unwrap(),
        )
    }
}

impl Fixture {
    fn script(&self, replies: Vec<Reply>) {
        self.scripted.lock().unwrap().extend(replies);
    }
    fn shapes(&self) -> Vec<ShapeRequest> {
        self.shape_seen.lock().unwrap().clone()
    }
}

#[derive(Clone)]
struct Reply {
    status: u16,
    headers: Vec<(String, String)>,
    body: String,
}

#[derive(Clone)]
struct ShapeRequest {
    uri: String,
    authorization: Option<String>,
    pin: Option<String>,
    resume_header: bool,
}
impl ShapeRequest {
    fn new<B>(request: &Request<B>) -> Self {
        let header = |name: &str| {
            request
                .headers()
                .get(name)
                .and_then(|value| value.to_str().ok())
                .map(str::to_owned)
        };
        Self {
            uri: request.uri().to_string(),
            authorization: header("authorization"),
            pin: header("x-terrascale-region-pin"),
            resume_header: request.headers().contains_key("tb-shape-resume"),
        }
    }
}

// The vector file uses its own project id; retarget it to this file's project.
const VECTOR_PROJECT: &str = "01ARZ3NDEKTSV4RRFFQ69G5FAW";
// Snapshot-last-page cursor_resume from shape-vectors.json.
// Snapshot-first-page next_offset: the offset an update reports after applying that page.
const SNAPSHOT_NEXT: &str = "snap.2.MDFBUlozTkRFS1RTVjRSUkZGUTY5RzVGQjA";
const PROVEN_CURSOR: &str = "AS7CHVJ_DeImFLxQzbY4yUUBAAAAAAAAB9AAAQAAMDkAAwABAAAwOQADAAAAAAAAA-gAAAAAAAAAAPT1kCRe1WfKHZY6Wy9LlDDHzfN4_FYu8vGVMm609kpm";

// Replays one shape-vectors.json HTTP case for this test project and the given region.
fn shape_reply(id: &str, region: &str) -> Reply {
    let vectors: Value = serde_json::from_str(include_str!("fixtures/shape-vectors.json")).unwrap();
    let case = vectors["http_vectors"]
        .as_array()
        .unwrap()
        .iter()
        .find(|case| case["id"] == id)
        .unwrap();
    let response = &case["response"];
    let mut headers: Vec<(String, String)> = response["headers"]
        .as_object()
        .map(|headers| {
            headers
                .iter()
                .map(|(name, value)| {
                    (
                        name.clone(),
                        value.as_str().unwrap().replace(VECTOR_PROJECT, PROJECT),
                    )
                })
                .collect()
        })
        .unwrap_or_default();
    headers.push(("x-terrabase-region".to_owned(), region.to_owned()));
    let body = response
        .get("body_utf8")
        .and_then(Value::as_str)
        .map(str::to_owned)
        .or_else(|| (!response["body"].is_null()).then(|| response["body"].to_string()))
        .unwrap_or_default()
        .replace(VECTOR_PROJECT, PROJECT);
    Reply {
        status: response["status"].as_u64().unwrap() as u16,
        headers,
        body,
    }
}

fn misdirected(region: &str) -> Reply {
    Reply {
        status: 421,
        headers: vec![("x-terrabase-region".to_owned(), region.to_owned())],
        body: json!({"error":{"code":"misdirected_request"}}).to_string(),
    }
}

fn shape_query(uri: &str) -> HashMap<String, String> {
    url::Url::parse(&format!("http://fixture{uri}"))
        .unwrap()
        .query_pairs()
        .into_owned()
        .collect()
}

fn failover_client(atlas: &Fixture) -> terrascale::v1::Client {
    terrascale::v1::Client::for_project(PROJECT, "fixture-access-token")
        .atlas_base_url(&atlas.url)
        .retry_policy(terrascale::v1::RetryPolicy::none())
        .build()
        .unwrap()
}

fn assert_routed(requests: &[ShapeRequest], region: &str) {
    assert!(!requests.is_empty());
    for request in requests {
        assert_eq!(
            request.authorization.as_deref(),
            Some("Bearer fixture-access-token")
        );
        assert_eq!(request.pin.as_deref(), Some(region));
    }
}

fn assert_atlas_unauthenticated(atlas: &Fixture) {
    let requests = atlas.requests.lock().unwrap();
    assert!(requests
        .iter()
        .filter(|(path, _)| path.starts_with("/v1/resolve") || path == "/v1/keys")
        .all(|(_, auth)| !auth));
}

#[tokio::test]
async fn verifies_real_es256_pin_reuses_session_and_refreshes_explicitly() {
    let fixture = Fixture::new().await;
    let resolver = fixture.resolver();
    let endpoint = resolver
        .resolve(PROJECT, Some("orders"), false)
        .await
        .unwrap();
    assert_eq!(endpoint.api.as_str().trim_end_matches('/'), fixture.url);
    assert_eq!(endpoint.region, "new-region-9"); // No region allowlist; placed/down is valid.
    resolver
        .resolve(PROJECT, Some("orders"), false)
        .await
        .unwrap();
    assert_eq!(fixture.requests.lock().unwrap().len(), 2);
    resolver
        .resolve(PROJECT, Some("orders"), true)
        .await
        .unwrap();
    let requests = fixture.requests.lock().unwrap();
    assert_eq!(requests.len(), 3); // Cached JWKS; fresh resolve.
    assert!(requests[2].0.contains("fresh=1"));
    assert!(requests.iter().all(|(_, authorization)| !authorization));
}

#[tokio::test]
async fn rejects_expired_future_overlong_and_mismatched_signed_claims() {
    let fixture = Fixture::new().await;
    let now = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap()
        .as_secs();
    for claims in [
        json!({"iss":"evil.example"}),
        json!({"project":Uuid::new_v4().to_string()}),
        json!({"database":null}),
        json!({"region":"another-1"}),
        json!({"api":"https://evil.example"}),
        json!({"exp":now+599}),
        json!({"iat":now+10}),
        json!({"iat":now-1}),
        json!({"exp":now-1}),
    ] {
        fixture.publish(json!({}), claims);
        assert!(matches!(
            fixture
                .resolver()
                .resolve(PROJECT, Some("orders"), false)
                .await,
            Err(Error::Decode(_))
        ));
    }
    fixture.publish(json!({"expires":now-1}), json!({"iat":now-600}));
    assert!(matches!(
        fixture
            .resolver()
            .resolve(PROJECT, Some("orders"), false)
            .await,
        Err(Error::Decode(_))
    ));
    fixture.publish(json!({}), json!({}));
    {
        let mut response = fixture.response.lock().unwrap();
        let pin = response["pin"].as_str().unwrap().to_owned();
        let last = pin.rsplit('.').next().unwrap();
        response["pin"] = pin.replace(last, &URL_SAFE_NO_PAD.encode([0u8; 64])).into();
    }
    assert!(matches!(
        fixture
            .resolver()
            .resolve(PROJECT, Some("orders"), false)
            .await,
        Err(Error::Decode(_))
    ));
}

#[tokio::test]
async fn rejects_malicious_origins_and_invalid_scopes() {
    let fixture = Fixture::new().await;
    for origin in [
        "https://evil.example",
        "https://terrascale.tech.evil.example",
        "https://user:secret@api.terrascale.tech",
        "https://api.terrascale.tech/path",
        "https://api.terrascale.tech?secret=1",
        "https://api.terrascale.tech#fragment",
        "https://api.terrascale.tech:444",
        "http://api.terrascale.tech",
        "http://127.0.0.1.evil.example",
        "https://api.terrascale.tech\\@evil.example",
    ] {
        fixture.publish(json!({"api":origin}), json!({}));
        assert!(
            matches!(
                fixture
                    .resolver()
                    .resolve(PROJECT, Some("orders"), false)
                    .await,
                Err(Error::Decode(_))
            ),
            "{origin}"
        );
    }
    fixture.publish(json!({"auth":"https://evil.example"}), json!({}));
    assert!(matches!(
        fixture
            .resolver()
            .resolve(PROJECT, Some("orders"), false)
            .await,
        Err(Error::Decode(_))
    ));
    let before = fixture.requests.lock().unwrap().len();
    assert!(matches!(
        fixture
            .resolver()
            .resolve(PROJECT, Some("../secret"), false)
            .await,
        Err(Error::InvalidInput(_))
    ));
    assert_eq!(fixture.requests.lock().unwrap().len(), before);
}

#[tokio::test]
async fn nullable_database_scope_is_bound_and_key_rotation_refetches() {
    let fixture = Fixture::new().await;
    let resolver = fixture.resolver();
    resolver
        .resolve(PROJECT, Some("orders"), false)
        .await
        .unwrap();
    fixture.keys.lock().unwrap()["keys"][0]["kid"] = "rotated".into();
    fixture.publish(json!({"database":null}), json!({}));
    {
        let mut response = fixture.response.lock().unwrap();
        let pin = response["pin"].as_str().unwrap();
        let parts: Vec<_> = pin.split('.').collect();
        let header =
            URL_SAFE_NO_PAD.encode(json!({"alg":"ES256","typ":"JWT","kid":"rotated"}).to_string());
        let message = format!("{header}.{}", parts[1]);
        let signature = fixture
            .signer
            .sign(&SystemRandom::new(), message.as_bytes())
            .unwrap();
        response["pin"] =
            format!("{message}.{}", URL_SAFE_NO_PAD.encode(signature.as_ref())).into();
    }
    resolver.resolve(PROJECT, None, false).await.unwrap();
    let requests = fixture.requests.lock().unwrap();
    assert_eq!(
        requests
            .iter()
            .filter(|(path, _)| path == "/v1/keys")
            .count(),
        2
    );
    assert!(!requests[2].0.contains("database="));
}

#[tokio::test]
async fn public_project_client_routes_to_verified_endpoint_with_only_region_pin() {
    let fixture = Fixture::new().await;
    let client = terrascale::v1::Client::for_project(PROJECT, "fixture-access-token")
        .atlas_base_url(&fixture.url)
        .retry_policy(terrascale::v1::RetryPolicy::none())
        .build()
        .unwrap();
    let error = client
        .database("orders")
        .collection("items")
        .get::<Value>(terrascale::v1::DocumentId::nil())
        .await
        .unwrap_err();
    assert!(error.is_not_found(), "{error:?}");
    let requests = fixture.requests.lock().unwrap();
    assert!(requests
        .iter()
        .filter(|(path, _)| path.starts_with("/v1/resolve") || path == "/v1/keys")
        .all(|(_, auth)| !auth));
    assert!(requests
        .iter()
        .any(|(path, auth)| path.starts_with("/v1/projects/") && *auth));
}

#[tokio::test]
async fn expires_session_refreshes_atlas_and_bounds_cached_scopes() {
    let fixture = Fixture::new().await;
    let resolver = fixture.resolver();
    let now = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap()
        .as_secs();
    fixture.publish(json!({"expires":now+2}), json!({}));
    resolver
        .resolve(PROJECT, Some("orders"), false)
        .await
        .unwrap();
    tokio::time::sleep(std::time::Duration::from_secs(2)).await;
    fixture.publish(json!({}), json!({}));
    resolver
        .resolve(PROJECT, Some("orders"), false)
        .await
        .unwrap();
    assert!(fixture
        .requests
        .lock()
        .unwrap()
        .last()
        .unwrap()
        .0
        .contains("fresh=1"));
    for id in 1..=128 {
        let project = format!("{id:026}");
        fixture.publish(json!({"project":project.to_string()}), json!({}));
        resolver
            .resolve(&project, Some("orders"), false)
            .await
            .unwrap();
    }
    let before = fixture.requests.lock().unwrap().len();
    resolver
        .resolve(&format!("{:026}", 128), Some("orders"), false)
        .await
        .unwrap();
    assert_eq!(fixture.requests.lock().unwrap().len(), before);
    fixture.publish(json!({}), json!({}));
    resolver
        .resolve(PROJECT, Some("orders"), false)
        .await
        .unwrap();
    assert_eq!(fixture.requests.lock().unwrap().len(), before + 1); // Oldest scope was evicted.
}

#[tokio::test]
async fn rejects_oversized_and_incomplete_responses_before_using_an_endpoint() {
    let fixture = Fixture::new().await;
    fixture.response.lock().unwrap()["padding"] = "x".repeat(64 << 10).into();
    assert!(matches!(
        fixture
            .resolver()
            .resolve(PROJECT, Some("orders"), false)
            .await,
        Err(Error::Decode(_))
    ));
    fixture.publish(json!({}), json!({}));
    fixture
        .response
        .lock()
        .unwrap()
        .as_object_mut()
        .unwrap()
        .remove("database");
    assert!(matches!(
        fixture.resolver().resolve(PROJECT, None, false).await,
        Err(Error::Decode(_))
    ));
    fixture.publish(json!({"project":Uuid::new_v4().to_string()}), json!({}));
    assert!(matches!(
        fixture
            .resolver()
            .resolve(PROJECT, Some("orders"), false)
            .await,
        Err(Error::Decode(_))
    ));
}

#[tokio::test]
async fn keeps_still_valid_session_when_fresh_atlas_request_cannot_connect() {
    let fixture = Fixture::new().await;
    let resolver = fixture.resolver();
    let first = resolver
        .resolve(PROJECT, Some("orders"), false)
        .await
        .unwrap();
    fixture.disconnected.store(true, Ordering::Relaxed);
    let fallback = resolver
        .resolve(PROJECT, Some("orders"), true)
        .await
        .unwrap();
    assert_eq!(first.api, fallback.api);
    assert_eq!(first.region, fallback.region);
    assert!(matches!(
        resolver
            .resolve("01ARZ3NDEKTSV4RRFFQ69G5FAW", Some("orders"), true)
            .await,
        Err(Error::Transport(_))
    ));
}

#[tokio::test]
async fn public_reads_refresh_on_421_and_redirect_but_retry_only_once() {
    for (status, failures) in [(421, 1), (307, 1), (421, 2), (307, 2)] {
        let fixture = Fixture::new().await;
        let target = Fixture::new().await;
        fixture.move_after_wrong_route(&target);
        *fixture.fault.lock().unwrap() = Some((status, 1));
        *target.fault.lock().unwrap() = Some((status, failures - 1));
        let client = terrascale::v1::Client::for_project(PROJECT, "fixture-access-token")
            .atlas_base_url(&fixture.url)
            .retry_policy(terrascale::v1::RetryPolicy::none())
            .build()
            .unwrap();
        let error = client
            .database("orders")
            .collection("items")
            .get::<Value>(terrascale::v1::DocumentId::nil())
            .await
            .unwrap_err();
        assert_eq!(
            error.status(),
            Some(if failures == 1 { 404 } else { status }),
            "{error:?}"
        );
        let target_requests = target.requests.lock().unwrap();
        assert_eq!(target_requests.len(), 1);
        assert!(target_requests[0].0.starts_with("/v1/projects/") && target_requests[0].1);
        let requests = fixture.requests.lock().unwrap();
        assert_eq!(
            requests
                .iter()
                .filter(|(path, _)| path.starts_with("/v1/projects/"))
                .count(),
            1
        );
        assert_eq!(
            requests
                .iter()
                .filter(|(path, _)| path.starts_with("/v1/resolve"))
                .count(),
            2
        );
        assert_eq!(
            requests
                .iter()
                .filter(|(path, _)| path.contains("fresh=1"))
                .count(),
            1
        );
        assert!(requests
            .iter()
            .filter(|(path, _)| path.starts_with("/v1/resolve") || path == "/v1/keys")
            .all(|(_, auth)| !auth));
    }
}

#[tokio::test]
async fn public_mutations_refresh_on_421_and_redirect_without_replay() {
    for status in [421, 307] {
        let fixture = Fixture::new().await;
        let target = Fixture::new().await;
        fixture.move_after_wrong_route(&target);
        *fixture.fault.lock().unwrap() = Some((status, 1));
        let client = terrascale::v1::Client::for_project(PROJECT, "fixture-access-token")
            .atlas_base_url(&fixture.url)
            .retry_policy(terrascale::v1::RetryPolicy::none())
            .build()
            .unwrap();
        let collection = client.database("orders").collection("items");
        let error = collection
            .put(
                terrascale::v1::DocumentId::nil(),
                &json!({"value":1}),
                terrascale::v1::WriteOptions::new(),
            )
            .await
            .unwrap_err();
        assert_eq!(error.status(), Some(status), "{error:?}");
        {
            let requests = fixture.requests.lock().unwrap();
            assert_eq!(
                requests
                    .iter()
                    .filter(|(path, _)| path.starts_with("/v1/projects/"))
                    .count(),
                1
            );
            assert_eq!(
                requests
                    .iter()
                    .filter(|(path, _)| path.contains("fresh=1"))
                    .count(),
                1
            );
        }
        assert!(target.requests.lock().unwrap().is_empty()); // Mutation never reached the new endpoint.
        let error = collection
            .get::<Value>(terrascale::v1::DocumentId::nil())
            .await
            .unwrap_err();
        assert_eq!(error.status(), Some(404));
        let requests = fixture.requests.lock().unwrap();
        assert_eq!(
            requests
                .iter()
                .filter(|(path, _)| path.starts_with("/v1/resolve"))
                .count(),
            2
        ); // Refreshed pin reused on next operation.
        assert_eq!(
            requests
                .iter()
                .filter(|(path, _)| path.starts_with("/v1/projects/"))
                .count(),
            1
        );
        assert_eq!(target.requests.lock().unwrap().len(), 1); // Next read uses the refreshed endpoint.
    }
}

#[tokio::test]
async fn public_static_endpoint_bypasses_atlas_entirely() {
    let fixture = Fixture::new().await;
    let clients = [
        terrascale::v1::Client::for_project(PROJECT, "fixture-access-token")
            .endpoint(&fixture.url)
            .region_pin("new-region-9")
            .build()
            .unwrap(),
        terrascale::v1::Client::builder(&fixture.url, "fixture-access-token")
            .project(PROJECT)
            .region_pin("new-region-9")
            .build()
            .unwrap(),
    ];
    for client in clients {
        assert_eq!(
            client
                .database("orders")
                .collection("items")
                .get::<Value>(terrascale::v1::DocumentId::nil())
                .await
                .unwrap_err()
                .status(),
            Some(404)
        );
    }
    let requests = fixture.requests.lock().unwrap();
    assert_eq!(requests.len(), 2);
    assert!(requests
        .iter()
        .all(|(path, auth)| path.starts_with("/v1/projects/") && *auth));
}

// Shared setup: Atlas points at `old`, which serves a complete snapshot and saves its cursor.
async fn complete_snapshot_on_old(atlas: &Fixture, old: &Fixture, new: &Fixture) {
    atlas.publish(
        json!({"database": "demo", "api": old.url, "region": "old-region-1"}),
        json!({}),
    );
    old.script(vec![
        shape_reply("bootstrap", "old-region-1"),
        shape_reply("snapshot-first-page", "old-region-1"),
        shape_reply("snapshot-last-page", "old-region-1"),
    ]);
    new.script(vec![
        shape_reply("proven-cursor-resume-query", "new-region-9"),
        shape_reply("resume-header-preserved-on-followup", "new-region-9"),
    ]);
}

#[tokio::test]
async fn region_failover_resumes_complete_snapshot_cursor_at_offset_minus_one() {
    let (atlas, old, new) = (
        Fixture::new().await,
        Fixture::new().await,
        Fixture::new().await,
    );
    complete_snapshot_on_old(&atlas, &old, &new).await;
    let stream = failover_client(&atlas)
        .database("demo")
        .collection("notes")
        .shape()
        .stream();
    futures_util::pin_mut!(stream);
    stream.next().await.unwrap().unwrap();
    stream.next().await.unwrap().unwrap();
    // Atlas now routes to the new region; the next old-region request gets 421.
    atlas.publish(json!({"database": "demo", "api": new.url}), json!({}));
    old.script(vec![misdirected("old-region-1")]);
    let update = stream.next().await.unwrap().unwrap();
    assert_eq!(update.offset, "4");

    let old_requests = old.shapes();
    assert_eq!(old_requests.len(), 4);
    assert_routed(&old_requests, "old-region-1");
    let new_requests = new.shapes();
    assert_eq!(new_requests.len(), 2);
    assert_routed(&new_requests, "new-region-9");
    // The new endpoint gets only the saved cursor at offset=-1: no old handle or offset.
    let resume = shape_query(&new_requests[0].uri);
    assert_eq!(resume.len(), 2);
    assert_eq!(resume["offset"], "-1");
    assert_eq!(resume["cursor"], PROVEN_CURSOR);
    assert!(!new_requests[0].resume_header);
    let continued = shape_query(&new_requests[1].uri);
    assert_eq!(continued["handle"], "h1-d90dddd41bd8282d");
    assert_eq!(continued["offset"], "2");
    assert_atlas_unauthenticated(&atlas);
}

#[tokio::test]
async fn transport_failure_refreshes_route_and_resumes_saved_cursor_once() {
    let (atlas, old, new) = (
        Fixture::new().await,
        Fixture::new().await,
        Fixture::new().await,
    );
    complete_snapshot_on_old(&atlas, &old, &new).await;
    let stream = failover_client(&atlas)
        .database("demo")
        .collection("notes")
        .shape()
        .stream();
    futures_util::pin_mut!(stream);
    stream.next().await.unwrap().unwrap();
    stream.next().await.unwrap().unwrap();
    // The old endpoint drops connections; the fresh Atlas answer names the new one.
    atlas.publish(json!({"database": "demo", "api": new.url}), json!({}));
    old.disconnected.store(true, Ordering::Relaxed);
    let update = stream.next().await.unwrap().unwrap();
    assert_eq!(update.offset, "4");

    assert_routed(&old.shapes(), "old-region-1");
    let new_requests = new.shapes();
    assert_eq!(new_requests.len(), 2);
    assert_routed(&new_requests, "new-region-9");
    let resume = shape_query(&new_requests[0].uri);
    assert_eq!(resume["offset"], "-1");
    assert_eq!(resume["cursor"], PROVEN_CURSOR);
    assert!(!resume.contains_key("handle"));
    assert_atlas_unauthenticated(&atlas);
}

#[tokio::test]
async fn interrupted_snapshot_resets_instead_of_resuming_after_region_failover() {
    let (atlas, old, new) = (
        Fixture::new().await,
        Fixture::new().await,
        Fixture::new().await,
    );
    atlas.publish(
        json!({"database": "demo", "api": old.url, "region": "old-region-1"}),
        json!({}),
    );
    old.script(vec![
        shape_reply("bootstrap", "old-region-1"),
        shape_reply("snapshot-first-page", "old-region-1"),
    ]);
    new.script(vec![
        shape_reply("bootstrap", "new-region-9"),
        shape_reply("snapshot-first-page", "new-region-9"),
    ]);
    let stream = failover_client(&atlas)
        .database("demo")
        .collection("notes")
        .shape()
        .stream();
    futures_util::pin_mut!(stream);
    let first = stream.next().await.unwrap().unwrap();
    assert_eq!(first.offset, SNAPSHOT_NEXT);
    // Mid-snapshot there is no complete-snapshot cursor, so failover must reset.
    atlas.publish(json!({"database": "demo", "api": new.url}), json!({}));
    old.script(vec![misdirected("old-region-1")]);
    let update = stream.next().await.unwrap().unwrap();
    assert_eq!(update.offset, SNAPSHOT_NEXT);

    assert_routed(&old.shapes(), "old-region-1");
    let new_requests = new.shapes();
    assert_eq!(new_requests.len(), 2);
    assert_routed(&new_requests, "new-region-9");
    let bootstrap = shape_query(&new_requests[0].uri);
    assert_eq!(bootstrap.len(), 1);
    assert_eq!(bootstrap["offset"], "-1");
    assert!(!new_requests[0].resume_header);
    let snapshot = shape_query(&new_requests[1].uri);
    assert_eq!(snapshot["handle"], "h1-f3171a2230109328");
    assert_eq!(snapshot["offset"], "snap.2.0");
    assert_atlas_unauthenticated(&atlas);
}

#[tokio::test]
async fn failed_resume_resets_once_without_a_second_cursor_attempt() {
    let (atlas, old, new) = (
        Fixture::new().await,
        Fixture::new().await,
        Fixture::new().await,
    );
    complete_snapshot_on_old(&atlas, &old, &new).await;
    // Replace the successful resume with a must_refetch, then serve a fresh bootstrap.
    new.scripted.lock().unwrap().clear();
    new.script(vec![
        shape_reply(
            "resume-followup-second-machine-refetch-does-not-loop",
            "new-region-9",
        ),
        shape_reply("bootstrap", "new-region-9"),
        shape_reply("snapshot-first-page", "new-region-9"),
    ]);
    let stream = failover_client(&atlas)
        .database("demo")
        .collection("notes")
        .shape()
        .stream();
    futures_util::pin_mut!(stream);
    stream.next().await.unwrap().unwrap();
    stream.next().await.unwrap().unwrap();
    atlas.publish(json!({"database": "demo", "api": new.url}), json!({}));
    old.script(vec![misdirected("old-region-1")]);
    let update = stream.next().await.unwrap().unwrap();
    assert_eq!(update.offset, SNAPSHOT_NEXT);

    let new_requests = new.shapes();
    assert_eq!(new_requests.len(), 3);
    assert_routed(&new_requests, "new-region-9");
    let cursors = new_requests
        .iter()
        .filter(|request| shape_query(&request.uri).contains_key("cursor"))
        .count();
    assert_eq!(cursors, 1);
    let bootstrap = shape_query(&new_requests[1].uri);
    assert_eq!(bootstrap.len(), 1);
    assert_eq!(bootstrap["offset"], "-1");
    assert_eq!(
        shape_query(&new_requests[2].uri)["handle"],
        "h1-f3171a2230109328"
    );
    assert_atlas_unauthenticated(&atlas);
}
