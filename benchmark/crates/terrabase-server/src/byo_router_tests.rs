use super::*;

use crate::auth_runtime::byo_config::AwsLcJwtVerifier;
use crate::byo_jwt::{
    Algorithm, ByoJwtService, JwksEndpoint, JwksFetchError, JwksFetcher, TenantJwtConfig,
};
use aws_lc_rs::rand::SystemRandom;
use aws_lc_rs::signature::{ECDSA_P256_SHA256_FIXED_SIGNING, EcdsaKeyPair, KeyPair};
use axum::body::Body;
use axum::http::{Request, StatusCode};
use base64::{Engine as _, engine::general_purpose::URL_SAFE_NO_PAD};
use serde_json::json;
use std::collections::BTreeSet;
use std::future::{Future, ready};
use std::pin::Pin;
use std::sync::Arc;
use std::time::{Duration, SystemTime, UNIX_EPOCH};
use tower::ServiceExt;

struct FixtureFetcher {
    jwks: Vec<u8>,
}

impl JwksFetcher for FixtureFetcher {
    fn fetch<'a>(
        &'a self,
        _: &'a JwksEndpoint,
    ) -> Pin<Box<dyn Future<Output = Result<Vec<u8>, JwksFetchError>> + Send + 'a>> {
        Box::pin(ready(Ok(self.jwks.clone())))
    }
}

fn unix_seconds() -> u64 {
    SystemTime::now().duration_since(UNIX_EPOCH).unwrap().as_secs()
}

fn token(
    audience: &str,
    expires_at: u64,
    operations: Option<&[&str]>,
    key: &EcdsaKeyPair,
) -> String {
    let header = URL_SAFE_NO_PAD.encode(br#"{"alg":"ES256","kid":"router-key","typ":"JWT"}"#);
    let mut claims = json!({
        "iss": "https://issuer.example",
        "sub": "user-1",
        "tenant": "terrabase-local",
        "aud": audience,
        "iat": expires_at.saturating_sub(60),
        "exp": expires_at,
    });
    if let Some(operations) = operations {
        claims["operations"] = json!(operations);
    }
    let payload = URL_SAFE_NO_PAD.encode(serde_json::to_vec(&claims).unwrap());
    let signing_input = format!("{header}.{payload}");
    let signature = key.sign(&SystemRandom::new(), signing_input.as_bytes()).unwrap();
    format!("{signing_input}.{}", URL_SAFE_NO_PAD.encode(signature.as_ref()))
}

fn service() -> (ByoJwtService<FixtureFetcher, AwsLcJwtVerifier>, EcdsaKeyPair) {
    let key_der =
        EcdsaKeyPair::generate_pkcs8(&ECDSA_P256_SHA256_FIXED_SIGNING, &SystemRandom::new())
            .unwrap();
    let key = EcdsaKeyPair::from_pkcs8(&ECDSA_P256_SHA256_FIXED_SIGNING, key_der.as_ref()).unwrap();
    let public = key.public_key().as_ref();
    let jwks = json!({
        "keys": [{
            "kty": "EC", "alg": "ES256", "use": "sig", "key_ops": ["verify"],
            "kid": "router-key", "crv": "P-256",
            "x": URL_SAFE_NO_PAD.encode(&public[1..33]),
            "y": URL_SAFE_NO_PAD.encode(&public[33..65]),
        }]
    });
    let config = TenantJwtConfig {
        tenant: "terrabase-local".into(),
        issuer: "https://issuer.example".into(),
        audience: "https://api.example".into(),
        jwks_endpoint: JwksEndpoint::parse("https://keys.example/jwks.json").unwrap(),
        algorithms: BTreeSet::from([Algorithm::Es256]),
        clock_skew_secs: 0,
        cache_ttl: Duration::from_secs(60),
        generation: 1,
    };
    let service = ByoJwtService::new(
        config,
        Arc::new(FixtureFetcher { jwks: serde_json::to_vec(&jwks).unwrap() }),
        Arc::new(AwsLcJwtVerifier),
    )
    .unwrap();
    (service, key)
}

#[tokio::test]
async fn byo_router_admits_same_owner_route() {
    let (service, key) = service();
    let now = unix_seconds();
    let token = token("https://api.example", now + 60, Some(&["read"]), &key);
    let (verifier, reply, parent_task) = crate::delegated_identity::tests::authority().await;
    reply.lock().unwrap()["issuer"] = json!("https://issuer.example");
    let mut state = super::state();
    let editable = Arc::get_mut(&mut state).unwrap();
    editable.byo_jwt = Some(Arc::new(service));
    editable.delegated_identity = Some(Arc::new(verifier));
    let response = super::router(state)
        .oneshot(
            Request::builder()
                .uri("/api/v1/databases/db-a")
                .header("authorization", format!("Bearer {token}"))
                .body(Body::empty())
                .unwrap(),
        )
        .await
        .unwrap();
    assert_ne!(response.status(), StatusCode::UNAUTHORIZED);
    assert_eq!(response.headers()["x-terrascale-principal"], "user-1");
    parent_task.abort();
}

#[tokio::test]
async fn byo_router_rejects_wrong_audience_expiry_and_missing_operation() {
    let now = unix_seconds();
    for (audience, expires_at, operations) in [
        ("https://wrong.example", now + 60, Some(["read"].as_slice())),
        ("https://api.example", now + 1, Some(["read"].as_slice())),
        ("https://api.example", now + 60, None),
    ] {
        let (service, key) = service();
        let bearer = token(audience, expires_at, operations, &key);
        let delay = if expires_at == now + 1 { 2_000 } else { 0 };
        let response = request_with_service(service, bearer, delay).await;
        assert_eq!(response.status(), StatusCode::UNAUTHORIZED);
    }
}

async fn request_with_service(
    service: ByoJwtService<FixtureFetcher, AwsLcJwtVerifier>,
    token: String,
    delay_ms: u64,
) -> axum::response::Response {
    let (verifier, reply, parent_task) = crate::delegated_identity::tests::authority().await;
    reply.lock().unwrap()["issuer"] = json!("https://issuer.example");
    if delay_ms != 0 {
        reply.lock().unwrap()["delayMs"] = json!(delay_ms);
    }
    let mut state = super::state();
    let editable = Arc::get_mut(&mut state).unwrap();
    editable.byo_jwt = Some(Arc::new(service));
    editable.delegated_identity = Some(Arc::new(verifier));
    let response = super::router(state)
        .oneshot(
            Request::builder()
                .uri("/api/v1/databases/db-a")
                .header("authorization", format!("Bearer {token}"))
                .body(Body::empty())
                .unwrap(),
        )
        .await
        .unwrap();
    parent_task.abort();
    response
}

#[tokio::test]
async fn byo_router_rechecks_current_membership_and_permission() {
    let (service, key) = service();
    let bearer = token("https://api.example", unix_seconds() + 60, Some(&["read"]), &key);
    let (verifier, reply, task) = crate::delegated_identity::tests::authority().await;
    reply.lock().unwrap()["issuer"] = json!("https://issuer.example");
    let mut state = super::state();
    let editable = Arc::get_mut(&mut state).unwrap();
    editable.byo_jwt = Some(Arc::new(service));
    editable.delegated_identity = Some(Arc::new(verifier));
    let app = super::router(state);
    let original = reply.lock().unwrap().clone();
    for patch in [
        None,
        Some(("active", json!(false))),
        Some(("action", json!("write"))),
        Some(("scopes", json!(["database:write"]))),
        Some(("projectId", json!("other"))),
        Some(("membershipScope", json!(null))),
    ] {
        *reply.lock().unwrap() = original.clone();
        if let Some((field, value)) = &patch {
            reply.lock().unwrap()[*field] = value.clone();
        }
        let response = app
            .clone()
            .oneshot(
                Request::builder()
                    .uri("/api/v1/databases/db-a")
                    .header("authorization", format!("Bearer {bearer}"))
                    .body(Body::empty())
                    .unwrap(),
            )
            .await
            .unwrap();
        if patch.is_none() {
            assert_ne!(response.status(), StatusCode::UNAUTHORIZED);
            assert_eq!(response.headers()["x-terrascale-principal"], "user-1");
        } else {
            assert_eq!(response.status(), StatusCode::UNAUTHORIZED, "{patch:?}");
        }
    }
    task.abort();
}
