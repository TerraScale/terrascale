//! Local, genuinely signed Atlas fixtures; no production network access.
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::time::{SystemTime, UNIX_EPOCH};

use base64::{engine::general_purpose::URL_SAFE_NO_PAD, Engine as _};
use bytes::Bytes;
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
use atlas::AtlasResolver;

struct Fixture {
    url: String,
    response: Arc<Mutex<Value>>,
    keys: Arc<Mutex<Value>>,
    requests: Arc<Mutex<Vec<(String, bool)>>>,
    replacement: Arc<Mutex<Option<Value>>>,
    fault: Arc<Mutex<Option<(u16, usize)>>>,
    disconnected: Arc<AtomicBool>,
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
        let (r, k, q, d) = (
            response.clone(),
            keys.clone(),
            requests.clone(),
            disconnected.clone(),
        );
        let task = tokio::spawn(async move {
            while let Ok((socket, _)) = listener.accept().await {
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
                        let data_reply = path != "/v1/keys" && path != "/v1/resolve";
                        let (status, body) = if path == "/v1/keys" {
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
                            let mut builder = Response::builder();
                            if data_reply {
                                builder = builder.header("x-terrabase-region", "new-region-9");
                            }
                            Ok::<_, std::io::Error>(
                                builder
                                    .status(status)
                                    .header("content-type", "application/json")
                                    .header("location", "https://credential-trap.invalid/")
                                    .body(Full::new(Bytes::from(body)))
                                    .unwrap(),
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
        let mut response = json!({"v":1,"project":Uuid::nil().to_string(),"database":"orders", "region":"new-region-9", "api":self.url, "auth":self.url, "health":"down", "placement":"known", "stale":false, "expires":now+600});
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

#[tokio::test]
async fn verifies_real_es256_pin_reuses_session_and_refreshes_explicitly() {
    let fixture = Fixture::new().await;
    let resolver = fixture.resolver();
    let endpoint = resolver
        .resolve(Uuid::nil(), Some("orders"), false)
        .await
        .unwrap();
    assert_eq!(endpoint.api.as_str().trim_end_matches('/'), fixture.url);
    assert_eq!(endpoint.region, "new-region-9"); // No region allowlist; placed/down is valid.
    resolver
        .resolve(Uuid::nil(), Some("orders"), false)
        .await
        .unwrap();
    assert_eq!(fixture.requests.lock().unwrap().len(), 2);
    resolver
        .resolve(Uuid::nil(), Some("orders"), true)
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
                .resolve(Uuid::nil(), Some("orders"), false)
                .await,
            Err(Error::Decode(_))
        ));
    }
    fixture.publish(json!({"expires":now-1}), json!({"iat":now-600}));
    assert!(matches!(
        fixture
            .resolver()
            .resolve(Uuid::nil(), Some("orders"), false)
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
            .resolve(Uuid::nil(), Some("orders"), false)
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
                    .resolve(Uuid::nil(), Some("orders"), false)
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
            .resolve(Uuid::nil(), Some("orders"), false)
            .await,
        Err(Error::Decode(_))
    ));
    let before = fixture.requests.lock().unwrap().len();
    assert!(matches!(
        fixture
            .resolver()
            .resolve(Uuid::nil(), Some("../secret"), false)
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
        .resolve(Uuid::nil(), Some("orders"), false)
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
    resolver.resolve(Uuid::nil(), None, false).await.unwrap();
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
    let client = terrascale::v1::Client::for_project(Uuid::nil(), "fixture-access-token")
        .atlas_base_url(&fixture.url)
        .retry_policy(terrascale::v1::RetryPolicy::none())
        .build()
        .unwrap();
    let error = client
        .database("orders")
        .collection("items")
        .get::<Value>(Uuid::nil())
        .await
        .unwrap_err();
    assert!(matches!(error, Error::NotFound { .. }), "{error:?}");
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
        .resolve(Uuid::nil(), Some("orders"), false)
        .await
        .unwrap();
    tokio::time::sleep(std::time::Duration::from_secs(2)).await;
    fixture.publish(json!({}), json!({}));
    resolver
        .resolve(Uuid::nil(), Some("orders"), false)
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
        let project = Uuid::from_u128(id);
        fixture.publish(json!({"project":project.to_string()}), json!({}));
        resolver
            .resolve(project, Some("orders"), false)
            .await
            .unwrap();
    }
    let before = fixture.requests.lock().unwrap().len();
    resolver
        .resolve(Uuid::from_u128(128), Some("orders"), false)
        .await
        .unwrap();
    assert_eq!(fixture.requests.lock().unwrap().len(), before);
    fixture.publish(json!({}), json!({}));
    resolver
        .resolve(Uuid::nil(), Some("orders"), false)
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
            .resolve(Uuid::nil(), Some("orders"), false)
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
        fixture.resolver().resolve(Uuid::nil(), None, false).await,
        Err(Error::Decode(_))
    ));
    fixture.publish(json!({"project":Uuid::new_v4().to_string()}), json!({}));
    assert!(matches!(
        fixture
            .resolver()
            .resolve(Uuid::nil(), Some("orders"), false)
            .await,
        Err(Error::Decode(_))
    ));
}

#[tokio::test]
async fn keeps_still_valid_session_when_fresh_atlas_request_cannot_connect() {
    let fixture = Fixture::new().await;
    let resolver = fixture.resolver();
    let first = resolver
        .resolve(Uuid::nil(), Some("orders"), false)
        .await
        .unwrap();
    fixture.disconnected.store(true, Ordering::Relaxed);
    let fallback = resolver
        .resolve(Uuid::nil(), Some("orders"), true)
        .await
        .unwrap();
    assert_eq!(first.api, fallback.api);
    assert_eq!(first.region, fallback.region);
    assert!(matches!(
        resolver.resolve(Uuid::new_v4(), Some("orders"), true).await,
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
        let client = terrascale::v1::Client::for_project(Uuid::nil(), "fixture-access-token")
            .atlas_base_url(&fixture.url)
            .retry_policy(terrascale::v1::RetryPolicy::none())
            .build()
            .unwrap();
        let error = client
            .database("orders")
            .collection("items")
            .get::<Value>(Uuid::nil())
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
        let client = terrascale::v1::Client::for_project(Uuid::nil(), "fixture-access-token")
            .atlas_base_url(&fixture.url)
            .retry_policy(terrascale::v1::RetryPolicy::none())
            .build()
            .unwrap();
        let collection = client.database("orders").collection("items");
        let error = collection
            .put(
                Uuid::nil(),
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
        let error = collection.get::<Value>(Uuid::nil()).await.unwrap_err();
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
        terrascale::v1::Client::for_project(Uuid::nil(), "fixture-access-token")
            .endpoint(&fixture.url)
            .region_pin("new-region-9")
            .build()
            .unwrap(),
        terrascale::v1::Client::builder(&fixture.url, "fixture-access-token")
            .project(Uuid::nil())
            .region_pin("new-region-9")
            .build()
            .unwrap(),
    ];
    for client in clients {
        assert_eq!(
            client
                .database("orders")
                .collection("items")
                .get::<Value>(Uuid::nil())
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
