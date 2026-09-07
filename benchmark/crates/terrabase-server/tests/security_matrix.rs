//! E-17 public-boundary security matrix.
//!
//! These tests deliberately exercise exported APIs and the Axum router. A
//! route that is still a planned/pending surface is a failing assertion, not
//! a skipped case or synthetic success.

mod security_support;

use std::collections::{BTreeMap, BTreeSet};
use std::time::{SystemTime, UNIX_EPOCH};

use axum::body::Body;
use axum::http::{HeaderMap, HeaderValue, Method, StatusCode, Uri};
use terrabase::Engine;
use terrabase::EngineConfig;
use terrabase::authorization::{AccessRule, AttributeValue, FieldProjection, PrincipalClaims};
use terrabase::regions::RegionCode;
use terrabase::types::{
    CollectionName, Document, DocumentId, DocumentValue, LocalDurability, OperationId, TenantId,
};
use terrabase_server::api_keys::{KeyStore, Scope, Verbs};
use terrabase_server::control::ControlError;
use terrabase_server::reactive_api::{OriginProofKeyRing, origin_proof_for, verify_origin_proof};
use terrabase_server::{
    CapabilityError, CapabilityIssuer, CapabilityVerifier, ViewCapabilityClaims,
};

use security_support::{Fixture, admin_registration, authenticated_request, send};

const ADMIN_KEY: &str = "tskey_e17_admin_abcdefghijkl";
const READ_KEY: &str = "tskey_e17_read_abcdefghijklmnop";

fn read_registration(database: &'static str) -> (&'static str, &'static str, Scope) {
    (READ_KEY, "e17-read", Scope::database(database, Verbs::READ))
}

fn unix_seconds() -> u64 {
    SystemTime::now().duration_since(UNIX_EPOCH).expect("unix clock").as_secs()
}

#[test]
fn tenant_scopes_cannot_cross_read_snapshots() {
    let root = std::env::temp_dir().join(format!("terrabase-e17-engine-{}", std::process::id()));
    let engine =
        Engine::open(EngineConfig::durable(&root).expect("engine config")).expect("engine opens");
    let tenant_a = TenantId::try_new("tenant-a").expect("tenant A");
    let tenant_b = TenantId::try_new("tenant-b").expect("tenant B");
    let scope_a = engine.scope_for(&tenant_a);
    let scope_b = engine.scope_for(&tenant_b);
    let snapshot = engine.read_snapshot(&scope_a).expect("snapshot");
    let mut tx = engine
        .begin(&scope_a, snapshot, OperationId::try_new("e17-tenant-write").expect("operation"))
        .expect("transaction");
    tx.put(
        Document::new(
            DocumentId::try_new("one").expect("document id"),
            CollectionName::try_new("records").expect("collection"),
            DocumentValue::Object(BTreeMap::from([(
                "tenant".to_owned(),
                DocumentValue::String("tenant-a".to_owned()),
            )])),
        ),
        terrabase::types::ExpectedVersion::New,
    )
    .expect("stage document");
    assert!(matches!(tx.commit(LocalDurability::Synced), terrabase::CommitOutcome::Committed(_)));
    let snapshot = engine.read_snapshot(&scope_a).expect("new snapshot");
    let own = engine.snapshot_documents(&scope_a, &snapshot).expect("tenant A read");
    assert_eq!(own.len(), 1);
    assert!(
        engine.snapshot_documents(&scope_b, &snapshot).is_err(),
        "foreign tenant snapshot must fail closed"
    );
    let snapshot_b = engine.read_snapshot(&scope_b).expect("tenant B snapshot");
    assert!(engine.snapshot_documents(&scope_b, &snapshot_b).expect("tenant B read").is_empty());
    let _ = std::fs::remove_dir_all(root);
}

#[tokio::test]
async fn database_and_global_scope_are_enforced_at_the_router_boundary() {
    let fixture = Fixture::new(&[read_registration("db-a")]);
    fixture.create_database("db-a", &[RegionCode::GRU]);
    fixture.create_database("db-b", &[RegionCode::GRU]);
    let app = fixture.router();
    let auth = format!("Bearer {READ_KEY}");

    let response = send(
        app.clone(),
        authenticated_request(Method::GET, "/api/v1/databases/db-a", Body::empty(), &auth[7..]),
    )
    .await;
    assert_eq!(response.status(), StatusCode::OK);

    let response = send(
        app.clone(),
        authenticated_request(Method::GET, "/api/v1/databases/db-b", Body::empty(), &auth[7..]),
    )
    .await;
    assert_eq!(response.status(), StatusCode::FORBIDDEN, "database scope crossed");

    let response = send(
        app.clone(),
        authenticated_request(Method::GET, "/api/v1/databases", Body::empty(), &auth[7..]),
    )
    .await;
    assert_eq!(
        response.status(),
        StatusCode::FORBIDDEN,
        "database-scoped key cannot enumerate globally"
    );

    let response = send(
        app.clone(),
        authenticated_request(
            Method::POST,
            "/api/v1/databases",
            Body::from(r#"{"name":"must-not-create","placement":["GRU"]}"#),
            &auth[7..],
        ),
    )
    .await;
    assert_eq!(response.status(), StatusCode::FORBIDDEN, "database-scoped key created globally");

    let admin = Fixture::new(&[admin_registration()]);
    admin.create_database("db-c", &[RegionCode::FRA]);
    let response = send(
        admin.router(),
        authenticated_request(Method::GET, "/api/v1/databases/db-c", Body::empty(), ADMIN_KEY),
    )
    .await;
    assert_eq!(
        response.status(),
        StatusCode::MISDIRECTED_REQUEST,
        "an admin key does not widen the database placement"
    );
    assert_eq!(
        response.headers().get("x-terrascale-placement").and_then(|v| v.to_str().ok()),
        Some("FRA")
    );
}

#[test]
fn access_predicates_and_field_projection_never_cross_a_row_or_leak_a_field() {
    let rule = AccessRule::try_compile(
        "resource.owner == request.auth.claims.tenant",
        &BTreeSet::from(["owner".to_owned()]),
    )
    .expect("indexed access rule");
    let claims = PrincipalClaims::try_new([("tenant", AttributeValue::String("a".to_owned()))])
        .expect("claims");
    let row = BTreeMap::from([
        ("owner".to_owned(), AttributeValue::String("a".to_owned())),
        ("secret".to_owned(), AttributeValue::String("do-not-leak".to_owned())),
    ]);
    assert!(rule.evaluate(&row, &claims));
    let foreign = BTreeMap::from([("owner".to_owned(), AttributeValue::String("b".to_owned()))]);
    assert!(!rule.evaluate(&foreign, &claims));

    let projection = FieldProjection::try_allow(["owner", "name"])
        .expect("allow projection")
        .try_deny(["name"])
        .expect("deny projection");
    let projected = projection.apply(&row);
    assert_eq!(projected.len(), 1);
    assert!(projected.contains_key("owner"));
    assert!(!projected.contains_key("secret"));
}

#[test]
fn api_key_cache_is_invalidated_by_revocation_generation() {
    let store =
        KeyStore::with_keys(&[(READ_KEY, "e17-read", Scope::database("db-a", Verbs::READ))]);
    assert_eq!(store.admit(READ_KEY).expect("first admission").label(), "e17-read");
    assert!(store.admit(READ_KEY).is_ok(), "cache hit admission");
    store.revoke_all();
    assert_eq!(store.admit(READ_KEY).expect_err("revoked cache entry").code(), "unauthorized");
    store.register(READ_KEY, "e17-restored", Scope::database("db-a", Verbs::READ));
    assert_eq!(store.admit(READ_KEY).expect("new generation").label(), "e17-restored");
}

#[tokio::test]
async fn hostile_jwt_claims_are_not_accepted_or_reflected() {
    let fixture = Fixture::new(&[admin_registration()]);
    let token = "eyJhbGciOiJub25lIn0.eyJ0ZW5hbnQiOiJldmlsIiwic2NvcGUiOiIqIn0.signature";
    let response = send(
        fixture.router(),
        authenticated_request(Method::GET, "/api/v1/health", Body::empty(), token),
    )
    .await;
    assert_eq!(
        response.status(),
        StatusCode::UNAUTHORIZED,
        "unconfigured JWT authentication must reject the bearer"
    );
    let body = axum::body::to_bytes(response.into_body(), 16 * 1024).await.expect("body");
    let text = String::from_utf8_lossy(&body);
    assert!(
        !text.contains("evil") && !text.contains("\"scope\"") && !text.contains(token),
        "claims must not be reflected: {text}"
    );
}

#[test]
fn origin_proofs_accept_current_previous_and_reject_expired_or_wrong_scope() {
    let current = b"e17-origin-current-key";
    let previous = b"e17-origin-previous-key";
    let keys = OriginProofKeyRing::new(current, Some(previous)).expect("key ring");
    let now = unix_seconds();
    let bucket = now / 600;
    let target = "/api/v1/reactive/views/v1/1/log?offset=0";
    let verify = |proof: String, uri: &str| {
        let mut headers = HeaderMap::new();
        headers.insert(
            "x-terrascale-origin-proof",
            HeaderValue::from_str(&proof).expect("proof header"),
        );
        verify_origin_proof(
            &headers,
            &Method::GET,
            &Uri::try_from(uri).expect("uri"),
            &keys,
            now,
            terrabase::regions::RegionCode::GRU,
        )
    };
    assert!(verify(
        origin_proof_for(
            &Method::GET,
            target,
            bucket,
            current,
            terrabase::regions::RegionCode::GRU
        ),
        target
    ));
    assert!(verify(
        origin_proof_for(
            &Method::GET,
            target,
            bucket.saturating_sub(600),
            previous,
            terrabase::regions::RegionCode::GRU
        ),
        target
    ));
    assert!(!verify(
        origin_proof_for(
            &Method::GET,
            target,
            bucket.saturating_sub(1200),
            current,
            terrabase::regions::RegionCode::GRU
        ),
        target
    ));
    assert!(!verify(
        origin_proof_for(
            &Method::GET,
            target,
            bucket,
            current,
            terrabase::regions::RegionCode::GRU
        ),
        "/api/v1/reactive/views/other/1/log?offset=0"
    ));
}

#[test]
fn origin_proof_cross_runtime_vector_uses_bucket_index_wire() {
    let vector: serde_json::Value =
        serde_json::from_str(include_str!("../../../contracts/conformance/origin-proof.json"))
            .expect("origin proof vector");
    let now = vector["now"].as_u64().expect("vector now");
    let bucket = vector["bucket"].as_u64().expect("vector bucket");
    assert_eq!(now / 600, bucket, "wire bucket is Unix seconds / 600");

    let keys = OriginProofKeyRing::new(
        vector["secret"].as_str().expect("vector secret").as_bytes(),
        None::<&[u8]>,
    )
    .expect("key ring");
    let mut headers = HeaderMap::new();
    headers.insert(
        "x-terrascale-origin-proof",
        HeaderValue::from_str(vector["proof"].as_str().expect("vector proof"))
            .expect("proof header"),
    );
    assert!(verify_origin_proof(
        &headers,
        &Method::GET,
        &Uri::try_from(vector["target"].as_str().expect("vector target")).expect("target URI"),
        &keys,
        now,
        RegionCode::GRU,
    ));
}

#[test]
fn capabilities_bind_route_scope_entitlement_and_rotation_without_secret_debug_output() {
    let current = b"e17-capability-current";
    let previous = b"e17-capability-previous";
    let issuer = CapabilityIssuer::new(current).expect("issuer");
    let verifier = CapabilityVerifier::with_previous(current, previous).expect("verifier");
    let token = issuer
        .issue("view-e17", 7, "tenant-a:project-a:db-a", 11, RegionCode::GRU, unix_seconds())
        .expect("capability");
    let claims = verifier.verify(&token, "view-e17", 7, unix_seconds()).expect("valid capability");
    assert_eq!(claims.entitlement_epoch, 11);
    assert_eq!(
        verifier.verify(&token, "view-other", 7, unix_seconds()),
        Err(CapabilityError::RouteMismatch)
    );
    let expected =
        ViewCapabilityClaims::new("view-e17", 7, "tenant-a:project-a:db-a", 12, RegionCode::GRU);
    assert_eq!(
        verifier.verify_claims(&token, &expected, unix_seconds()),
        Err(CapabilityError::RouteMismatch)
    );
    assert!(!format!("{issuer:?}").contains("e17-capability"));
    assert!(!format!("{verifier:?}").contains("e17-capability"));
}

#[test]
fn cache_purge_does_not_allow_delivery_to_an_evicted_view() {
    let result = terrabase_server::reactive_api::append_message(
        "e17-purged-view",
        1,
        terrabase_sync_core::wire::Message::Heartbeat,
    );
    assert_eq!(result.expect_err("purged view must be absent").code(), "unknown_view");
}

#[tokio::test]
async fn reactive_cache_identity_isolated_by_database_entitlement_and_rule() {
    let fixture = Fixture::new(&[read_registration("db-reactive")]);
    fixture.create_database("db-reactive", &[RegionCode::GRU]);
    let app = fixture.router();
    let auth = format!("Bearer {READ_KEY}");
    let attach = |statement: &str, epoch: u64| {
        let body = format!(
            r#"{{"databaseId":"db-reactive","statement":"{statement}","entitlement_epoch":{epoch}}}"#
        );
        authenticated_request(Method::POST, "/api/v1/reactive/views", Body::from(body), &auth[7..])
    };
    let first = send(app.clone(), attach("SELECT * FROM orders", 1)).await;
    assert_eq!(
        first.status(),
        StatusCode::CREATED,
        "reactive attach is a real route; pending is a failure"
    );
    let first_body = axum::body::to_bytes(first.into_body(), 64 * 1024).await.expect("attach body");
    let first_json: serde_json::Value = serde_json::from_slice(&first_body).expect("attach JSON");
    let first_view = first_json["view"].as_str().expect("view id").to_owned();
    let second = send(app.clone(), attach("SELECT * FROM orders", 2)).await;
    assert_eq!(second.status(), StatusCode::CREATED);
    let second_body =
        axum::body::to_bytes(second.into_body(), 64 * 1024).await.expect("attach body");
    let second_json: serde_json::Value = serde_json::from_slice(&second_body).expect("attach JSON");
    assert_ne!(
        first_view,
        second_json["view"].as_str().expect("second view id"),
        "entitlement must partition cache identity"
    );
    let third = send(app.clone(), attach("SELECT * FROM orders WHERE owner = 'a'", 1)).await;
    assert_eq!(third.status(), StatusCode::CREATED);
    let third_body = axum::body::to_bytes(third.into_body(), 64 * 1024).await.expect("attach body");
    let third_json: serde_json::Value = serde_json::from_slice(&third_body).expect("attach JSON");
    assert_ne!(
        first_view,
        third_json["view"].as_str().expect("third view id"),
        "rule changes must partition cache identity"
    );
}

#[tokio::test]
async fn origin_proof_admits_only_the_real_reactive_delivery_route() {
    let fixture = Fixture::new(&[read_registration("db-proof")]);
    fixture.create_database("db-proof", &[RegionCode::GRU]);
    let auth = format!("Bearer {READ_KEY}");
    let attach = send(
        fixture.router(),
        authenticated_request(
            Method::POST,
            "/api/v1/reactive/views",
            Body::from(r#"{"databaseId":"db-proof","statement":"SELECT * FROM orders"}"#),
            &auth[7..],
        ),
    )
    .await;
    assert_eq!(attach.status(), StatusCode::CREATED);
    let body = axum::body::to_bytes(attach.into_body(), 64 * 1024).await.expect("attach body");
    let response: serde_json::Value = serde_json::from_slice(&body).expect("attach JSON");
    let view = response["view"].as_str().expect("view id");
    let generation = response["generation"].as_u64().expect("generation");
    let path = format!("/api/v1/reactive/views/{view}/{generation}/snapshot/0?cut=0");
    let now = unix_seconds();
    let bucket = now / 600;
    let proof = origin_proof_for(
        &Method::GET,
        &path,
        bucket,
        b"e17-origin-current-key",
        terrabase::regions::RegionCode::GRU,
    );
    let mut request = security_support::request(Method::GET, &path, Body::empty());
    request
        .headers_mut()
        .insert("x-terrascale-origin-proof", HeaderValue::from_str(&proof).expect("proof header"));
    let delivery = send(fixture.router(), request).await;
    assert_eq!(delivery.status(), StatusCode::OK, "edge proof did not admit snapshot delivery");

    let mut wrong = security_support::request(
        Method::GET,
        &format!("/api/v1/reactive/views/{view}/{generation}/snapshot/1?cut=0"),
        Body::empty(),
    );
    wrong
        .headers_mut()
        .insert("x-terrascale-origin-proof", HeaderValue::from_str(&proof).expect("proof header"));
    let rejected = send(fixture.router(), wrong).await;
    assert_eq!(rejected.status(), StatusCode::UNAUTHORIZED, "proof crossed its route scope");
}

#[tokio::test]
async fn security_matrix_does_not_count_pending_public_routes_as_implemented() {
    let fixture = Fixture::new(&[admin_registration()]);
    fixture.create_database("db-pending", &[RegionCode::GRU]);
    let routes = [
        (Method::GET, "/api/v1/databases/db-pending/usage"),
        (Method::GET, "/api/v1/databases/db-pending/collections/orders/schema"),
        (Method::GET, "/api/v1/databases/db-pending/collections/orders/indexes/recommendations"),
        (Method::DELETE, "/api/v1/databases/db-pending/collections/orders/indexes/status"),
        (Method::GET, "/api/v1/databases/db-pending/webhooks"),
        (Method::POST, "/api/v1/databases/db-pending/exports"),
    ];
    for (method, uri) in routes {
        let response =
            send(fixture.router(), authenticated_request(method, uri, Body::empty(), ADMIN_KEY))
                .await;
        assert_ne!(
            response.status(),
            StatusCode::NOT_IMPLEMENTED,
            "public route {uri} is pending and must not count as an E-17 success"
        );
    }
}

#[test]
fn websocket_admission_and_release_are_per_database() {
    let guards = terrabase_server::guards::AbuseGuards::new();
    guards.set_limits(
        "db-a",
        terrabase_server::guards::AbuseLimits {
            read_query_rps: 10,
            direct_read_bandwidth_bytes_per_month: 10,
            distinct_views: 10,
            ws_connections: 1,
            webhook_endpoints: 10,
        },
    );
    guards.set_limits(
        "db-b",
        terrabase_server::guards::AbuseLimits {
            read_query_rps: 10,
            direct_read_bandwidth_bytes_per_month: 10,
            distinct_views: 10,
            ws_connections: 1,
            webhook_endpoints: 10,
        },
    );
    guards.acquire_ws("db-a").expect("first websocket");
    assert!(guards.acquire_ws("db-a").is_err(), "second socket crosses db-a budget");
    assert!(guards.acquire_ws("db-b").is_ok(), "db-b has an isolated budget");
    guards.release_ws("db-a");
    assert_eq!(guards.ws_count("db-a"), 0);
    assert!(guards.acquire_ws("db-a").is_ok(), "release returns capacity");
    guards.release_ws("db-a");
    guards.release_ws("db-b");
}

#[test]
fn placement_lock_rejects_widening_and_secret_material_is_redacted() {
    let fixture = Fixture::new(&[admin_registration()]);
    let control = fixture.state.control.as_ref().expect("control");
    control
        .create("parent-db", &[RegionCode::GRU, RegionCode::FRA], Some("GRU,FRA"))
        .expect("parent");
    assert!(matches!(
        control.create_branch("widened-db", &[RegionCode::SFO], Some("GRU,FRA"), "parent-db"),
        Err(ControlError::PlacementLockWidened { .. })
    ));
    assert!(matches!(
        control.create_branch("wrong-residency", &[RegionCode::GRU], Some("eu-only"), "parent-db"),
        Err(ControlError::PlacementLockWidened { .. })
    ));
    let debug = format!("{:?}", fixture.state.origin_proof_keys);
    assert!(
        !debug.contains("e17-origin-current") && !debug.contains("e17-origin-previous"),
        "origin proof secrets leaked by Debug: {debug}"
    );
}
