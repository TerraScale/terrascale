use super::*;
use futures_util::{SinkExt, StreamExt};
use terrabase_sync_core::wire::{Envelope, Message, decode_envelope, encode_envelope};
use tokio_tungstenite::{
    MaybeTlsStream, WebSocketStream,
    tungstenite::{Message as SocketMessage, client::IntoClientRequest},
};

const KEY: &str = "Bearer tskey_test_abcdefghijklmnop";
const ORIGIN_KEY: &[u8] = b"reactive-collection-proof-key-32-bytes";
const SQL: &str = "SELECT total FROM orders LIMIT 10";
type Socket = WebSocketStream<MaybeTlsStream<tokio::net::TcpStream>>;

struct Server {
    state: Arc<ApiState>,
    app: axum::Router,
    address: std::net::SocketAddr,
    task: tokio::task::JoinHandle<()>,
}
impl Drop for Server {
    fn drop(&mut self) {
        self.task.abort();
    }
}
impl Server {
    async fn new() -> Self {
        Self::with_identity(None).await
    }
    async fn with_identity(
        identity: Option<Arc<crate::delegated_identity::IdentityVerifier>>,
    ) -> Self {
        let mut state = state_with_keys_and_control();
        let tenant = if identity.is_some() {
            crate::delegated_identity::tests::TEST_TENANT
        } else {
            "terrabase-local"
        };
        Arc::get_mut(&mut state).unwrap().scope.0 = tenant.into();
        Arc::get_mut(&mut state).unwrap().delegated_identity = identity;
        Arc::get_mut(&mut state).unwrap().origin_proof_keys =
            Arc::new(crate::reactive_api::OriginProofKeyRing::new(ORIGIN_KEY, None).unwrap());
        let app = router(Arc::clone(&state));
        let response = app
            .clone()
            .oneshot(
                Request::post("/api/v1/databases")
                    .header("x-terrascale-tenant-id", tenant)
                    .header("x-terrascale-project-id", "local")
                    .header("authorization", KEY)
                    .body(Body::from(r#"{"name":"prod","placement":["GRU"],"residency":null}"#))
                    .unwrap(),
            )
            .await
            .unwrap();
        assert_eq!(response.status(), StatusCode::CREATED);
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let address = listener.local_addr().unwrap();
        let service = app.clone();
        let task = tokio::spawn(async move {
            axum::serve(listener, service).await.unwrap();
        });
        Self { state, app, address, task }
    }
    async fn connect(&self) -> Socket {
        let mut request = format!("ws://{}/api/v1/databases/prod/reactive", self.address)
            .into_client_request()
            .unwrap();
        request.headers_mut().insert("authorization", KEY.parse().unwrap());
        tokio_tungstenite::connect_async(request).await.unwrap().0
    }
    async fn put(&self, id: &str, total: u32) {
        let response = self
            .app
            .clone()
            .oneshot(
                Request::put(format!("/api/v1/databases/prod/collections/orders/documents/{id}"))
                    .header("authorization", KEY)
                    .body(Body::from(format!(r#"{{"total":{total}}}"#)))
                    .unwrap(),
            )
            .await
            .unwrap();
        assert_eq!(response.status(), StatusCode::OK);
    }
    async fn read(&self, url: &str, capability: &str) -> Vec<u8> {
        let response = self
            .app
            .clone()
            .oneshot(
                Request::get(url)
                    .header("authorization", KEY)
                    .header("x-terrascale-capability", capability)
                    .body(Body::empty())
                    .unwrap(),
            )
            .await
            .unwrap();
        assert_eq!(response.status(), StatusCode::OK);
        axum::body::to_bytes(response.into_body(), 8_388_608).await.unwrap().to_vec()
    }
}
async fn send(socket: &mut Socket, message: Message) {
    socket
        .send(SocketMessage::Binary(
            encode_envelope(&Envelope { features: 0, correlation: 1, causation: 0, message })
                .unwrap()
                .into(),
        ))
        .await
        .unwrap();
}
async fn receive(socket: &mut Socket) -> Vec<u8> {
    let frame = tokio::time::timeout(std::time::Duration::from_secs(3), socket.next())
        .await
        .unwrap()
        .unwrap()
        .unwrap();
    let SocketMessage::Binary(bytes) = frame else { panic!("binary protocol frame: {frame:?}") };
    bytes.to_vec()
}
fn subscribe(subscription: &str, checkpoint: Option<Vec<u8>>) -> Message {
    Message::Subscribe {
        subscription: subscription.into(),
        query: SQL.into(),
        parameters: Vec::new(),
        checkpoint,
    }
}

#[tokio::test]
async fn websocket_streams_the_http_bytes_and_resumes_after_unsubscribe() {
    let server = Server::new().await;
    server.put("one", 1).await;
    let response = server
        .app
        .clone()
        .oneshot(
            Request::post("/api/v1/reactive/views")
                .header("authorization", KEY)
                .body(Body::from(
                    serde_json::json!({"databaseId":"prod","statement":SQL}).to_string(),
                ))
                .unwrap(),
        )
        .await
        .unwrap();
    assert_eq!(response.status(), StatusCode::CREATED);
    let attached: serde_json::Value =
        serde_json::from_slice(&axum::body::to_bytes(response.into_body(), 65_536).await.unwrap())
            .unwrap();
    let view = attached["view"].as_str().unwrap();
    let capability = attached["capability"].as_str().unwrap();
    let mut socket = server.connect().await;
    send(&mut socket, subscribe(view, None)).await;
    let snapshot = receive(&mut socket).await;
    assert_eq!(
        snapshot,
        server.read(attached["snapshot_chunks"][0].as_str().unwrap(), capability).await
    );
    server.put("two", 2).await;
    let delta = receive(&mut socket).await;
    assert_eq!(delta, server.read(attached["log"].as_str().unwrap(), capability).await);
    let Message::Delta { checkpoint, to_revision, .. } = decode_envelope(&delta).unwrap().message
    else {
        panic!("delta")
    };
    send(
        &mut socket,
        Message::CheckpointAck { subscription: view.into(), checkpoint: checkpoint.clone() },
    )
    .await;
    send(&mut socket, Message::Unsubscribe { subscription: view.into() }).await;
    socket.send(SocketMessage::Ping(vec![1].into())).await.unwrap();
    assert!(matches!(
        tokio::time::timeout(std::time::Duration::from_secs(3), socket.next())
            .await
            .unwrap()
            .unwrap()
            .unwrap(),
        SocketMessage::Pong(_)
    ));
    server.put("three", 3).await;
    assert!(
        tokio::time::timeout(std::time::Duration::from_millis(150), socket.next()).await.is_err(),
        "unsubscribe stops delivery"
    );
    send(&mut socket, subscribe(view, Some(checkpoint.clone()))).await;
    let resumed = receive(&mut socket).await;
    let Message::Delta { from_revision, .. } = decode_envelope(&resumed).unwrap().message else {
        panic!("resume delta")
    };
    assert_eq!(from_revision, to_revision);
    assert_eq!(
        resumed,
        server
            .read(&format!("/api/v1/reactive/views/{view}/1/log?offset={to_revision}"), capability)
            .await
    );
    let mut forged = checkpoint;
    forged[0] ^= 1;
    send(&mut socket, Message::CheckpointAck { subscription: view.into(), checkpoint: forged })
        .await;
    assert!(matches!(
        decode_envelope(&receive(&mut socket).await).unwrap().message,
        Message::Error { code: terrabase_sync_core::wire::ErrorCode::CheckpointRefused, .. }
    ));
}

#[tokio::test]
async fn websocket_revocation_stops_an_idle_subscription() {
    let server = Server::new().await;
    let mut socket = server.connect().await;
    send(&mut socket, subscribe("local", None)).await;
    assert!(matches!(
        decode_envelope(&receive(&mut socket).await).unwrap().message,
        Message::Snapshot { .. }
    ));
    server.state.keys.as_ref().unwrap().revoke_all();
    assert!(matches!(
        decode_envelope(&receive(&mut socket).await).unwrap().message,
        Message::Error { code: terrabase_sync_core::wire::ErrorCode::AuthenticationExpired, .. }
    ));
}

#[tokio::test]
async fn websocket_multiplexes_views_and_unsubscribes_only_the_named_view() {
    let server = Server::new().await;
    server.put("one", 1).await;
    let mut socket = server.connect().await;
    send(&mut socket, subscribe("all", None)).await;
    let Message::Snapshot { subscription: all, .. } =
        decode_envelope(&receive(&mut socket).await).unwrap().message
    else {
        panic!("snapshot")
    };
    send(
        &mut socket,
        Message::Subscribe {
            subscription: "filtered".into(),
            query: "SELECT total FROM orders WHERE total > 1 LIMIT 10".into(),
            parameters: Vec::new(),
            checkpoint: None,
        },
    )
    .await;
    let Message::Snapshot { subscription: filtered, snapshot } =
        decode_envelope(&receive(&mut socket).await).unwrap().message
    else {
        panic!("filtered snapshot")
    };
    assert!(snapshot.items.is_empty());
    assert_ne!(all, filtered);
    send(&mut socket, subscribe("same", None)).await;
    let Message::Snapshot { subscription: duplicate, .. } =
        decode_envelope(&receive(&mut socket).await).unwrap().message
    else {
        panic!("shared snapshot")
    };
    assert_eq!(duplicate, all);
    server.put("two", 2).await;
    let mut delivered = std::collections::BTreeSet::new();
    for _ in 0..2 {
        let Message::Delta { subscription, .. } =
            decode_envelope(&receive(&mut socket).await).unwrap().message
        else {
            panic!("delta")
        };
        delivered.insert(subscription);
    }
    assert_eq!(delivered, [all.clone(), filtered].into());
    send(&mut socket, Message::Unsubscribe { subscription: "all".into() }).await;
    send(&mut socket, Message::Unsubscribe { subscription: "filtered".into() }).await;
    socket.send(SocketMessage::Ping(vec![1].into())).await.unwrap();
    assert!(matches!(socket.next().await.unwrap().unwrap(), SocketMessage::Pong(_)));
    server.put("three", 3).await;
    let Message::Delta { subscription, .. } =
        decode_envelope(&receive(&mut socket).await).unwrap().message
    else {
        panic!("remaining view delta")
    };
    assert_eq!(subscription, all);
    assert!(
        tokio::time::timeout(std::time::Duration::from_millis(150), socket.next()).await.is_err()
    );
    socket.close(None).await.unwrap();
}

#[tokio::test]
async fn canonical_websocket_uses_browser_headers_and_refuses_query_widening() {
    let server = Server::new().await;
    server.put("one", 1).await;
    let response = server
        .app
        .clone()
        .oneshot(
            Request::post("/api/v1/reactive/views")
                .header("authorization", KEY)
                .body(Body::from(
                    serde_json::json!({"databaseId":"prod","statement":SQL}).to_string(),
                ))
                .unwrap(),
        )
        .await
        .unwrap();
    let attached: serde_json::Value =
        serde_json::from_slice(&axum::body::to_bytes(response.into_body(), 65_536).await.unwrap())
            .unwrap();
    let view = attached["view"].as_str().unwrap();
    let capability = attached["capability"].as_str().unwrap();
    let url = format!("ws://{}/api/v1/reactive/views/{view}/1/websocket", server.address);
    let mut request = url.clone().into_client_request().unwrap();
    request.headers_mut().insert(
        "sec-websocket-protocol",
        format!("terrascale.sync.v1, terrascale.cap.{capability}").parse().unwrap(),
    );
    assert!(!request.headers().contains_key("authorization"));
    let (mut socket, response) = tokio_tungstenite::connect_async(request).await.unwrap();
    assert_eq!(response.headers()["sec-websocket-protocol"], "terrascale.sync.v1");
    send(&mut socket, subscribe(view, None)).await;
    assert_eq!(
        receive(&mut socket).await,
        server.read(attached["snapshot_chunks"][0].as_str().unwrap(), capability).await
    );
    server.put("two", 2).await;
    assert_eq!(
        receive(&mut socket).await,
        server.read(attached["log"].as_str().unwrap(), capability).await
    );
    send(
        &mut socket,
        Message::Subscribe {
            subscription: view.into(),
            query: "SELECT * FROM secrets LIMIT 10".into(),
            parameters: Vec::new(),
            checkpoint: None,
        },
    )
    .await;
    assert!(matches!(
        decode_envelope(&receive(&mut socket).await).unwrap().message,
        Message::Error { code: terrabase_sync_core::wire::ErrorCode::PermissionDenied, .. }
    ));
    assert!(
        tokio_tungstenite::connect_async(url).await.is_err(),
        "a bare browser upgrade is refused"
    );
    let mut wrong = format!("ws://{}/api/v1/reactive/views/{view}/2/websocket", server.address)
        .into_client_request()
        .unwrap();
    wrong.headers_mut().insert(
        "sec-websocket-protocol",
        format!("terrascale.sync.v1, terrascale.cap.{capability}").parse().unwrap(),
    );
    assert!(
        tokio_tungstenite::connect_async(wrong).await.is_err(),
        "the capability cannot cross generations"
    );
}

#[tokio::test]
#[ignore = "requires TERRASCALE_TYPESCRIPT_SDK with installed Node dependencies"]
async fn typescript_browser_websocket_journey() {
    let server = Server::new().await;
    let sdk = std::env::var("TERRASCALE_TYPESCRIPT_SDK").expect("SDK checkout");
    let origin = format!("http://{}", server.address);
    let output = tokio::task::spawn_blocking(move || {
        std::process::Command::new("node")
            .args([
                "node_modules/vitest/vitest.mjs",
                "run",
                "src/integration/reactive-websocket.test.ts",
            ])
            .current_dir(sdk)
            .env("TERRASCALE_REACTIVE_TEST_ORIGIN", origin)
            .env("TERRASCALE_REACTIVE_TEST_TOKEN", KEY)
            .output()
            .expect("run SDK test")
    })
    .await
    .unwrap();
    assert!(
        output.status.success(),
        "{}\n{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
}

#[tokio::test]
#[ignore = "requires TERRASCALE_TYPESCRIPT_SDK and installed Playwright browsers"]
async fn packed_sdk_cross_origin_browser_journey() {
    let server = Server::new().await;
    let sdk = std::env::var("TERRASCALE_TYPESCRIPT_SDK").expect("SDK checkout");
    let origin = format!("http://{}", server.address);
    let output = tokio::task::spawn_blocking(move || {
        let build = std::process::Command::new("npm")
            .args(["run", "build"])
            .current_dir(&sdk)
            .output()
            .expect("build SDK browser candidate");
        if !build.status.success() {
            return build;
        }
        std::process::Command::new("node")
            .args(["node_modules/@playwright/test/cli.js", "test", "reactive-member.spec.ts"])
            .current_dir(sdk)
            .env("TERRASCALE_REACTIVE_TEST_ORIGIN", origin)
            .env("TERRASCALE_REACTIVE_TEST_TOKEN", KEY)
            .output()
            .expect("run packed SDK browser test")
    })
    .await
    .unwrap();
    assert!(
        output.status.success(),
        "{}\n{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
}

#[tokio::test]
async fn membership_revocation_closes_active_socket_without_delivery_authority_calls() {
    authority_revocation_closes_active_socket("a").await;
}

#[tokio::test]
async fn feature_revocation_closes_active_socket_without_delivery_authority_calls() {
    authority_revocation_closes_active_socket("b").await;
}

async fn authority_revocation_closes_active_socket(revoked_scope: &str) {
    let (identity, reply, authority_task) =
        crate::delegated_identity::tests::authority_for_collection(
            crate::delegated_identity::tests::TEST_TENANT,
            "local",
            "prod",
            "read",
            Some("orders"),
        )
        .await;
    let identity = Arc::new(identity);
    let server = Server::with_identity(Some(identity.clone())).await;
    server.put("one", 1).await;
    let attached_response = server
        .app
        .clone()
        .oneshot(
            Request::post("/api/v1/reactive/views")
                .header("authorization", "Bearer delegated-token")
                .body(Body::from(
                    serde_json::json!({"databaseId":"prod","statement":SQL}).to_string(),
                ))
                .unwrap(),
        )
        .await
        .unwrap();
    assert_eq!(attached_response.status(), StatusCode::CREATED);
    let attached: serde_json::Value = serde_json::from_slice(
        &axum::body::to_bytes(attached_response.into_body(), 65_536).await.unwrap(),
    )
    .unwrap();
    let capability = attached["capability"].as_str().unwrap();
    assert!(capability.split('.').nth(3).unwrap().contains(":authz-"));
    let view = attached["view"].as_str().unwrap();
    let mut request = format!("ws://{}/api/v1/reactive/views/{view}/1/websocket", server.address)
        .into_client_request()
        .unwrap();
    request.headers_mut().insert(
        "sec-websocket-protocol",
        format!("terrascale.sync.v1, terrascale.cap.{capability}").parse().unwrap(),
    );
    let (mut socket, _) = tokio_tungstenite::connect_async(request).await.unwrap();
    send(&mut socket, subscribe(view, None)).await;
    let _ = receive(&mut socket).await;
    crate::delegated_identity::tests::update_lease(
        &reply,
        u64::from(revoked_scope == "a"),
        u64::from(revoked_scope == "b"),
    );
    identity.refresh_authorization().await.unwrap();
    // No authority or Worker is needed for the one-second local socket fence.
    authority_task.abort();
    assert!(matches!(
        decode_envelope(&receive(&mut socket).await).unwrap().message,
        Message::Error { code: terrabase_sync_core::wire::ErrorCode::AuthenticationExpired, .. }
    ));
    let direct = server
        .app
        .clone()
        .oneshot(
            Request::get(attached["snapshot_chunks"][0].as_str().unwrap())
                .header("authorization", KEY)
                .header("x-terrascale-capability", capability)
                .body(Body::empty())
                .unwrap(),
        )
        .await
        .unwrap();
    assert_eq!(direct.status(), StatusCode::UNAUTHORIZED);
}

#[tokio::test]
#[allow(clippy::too_many_lines)]
async fn collection_bearer_attaches_only_its_parsed_query_and_delivers_immutable_results() {
    let (identity, reply, authority_task) =
        crate::delegated_identity::tests::authority_for_collection(
            crate::delegated_identity::tests::TEST_TENANT,
            "local",
            "prod",
            "read",
            Some("orders"),
        )
        .await;
    let now = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_secs();
    let server = Server::with_identity(Some(Arc::new(identity))).await;
    server.put("one", 1).await;
    let attach = |statement: &str| {
        Request::post("/api/v1/reactive/views")
            .header("authorization", "Bearer delegated-token")
            .header("x-terrascale-collection", "orders")
            .body(Body::from(
                serde_json::json!({
                    "databaseId":"prod", "statement":statement, "collection":"orders",
                })
                .to_string(),
            ))
            .unwrap()
    };
    for (statement, status) in [
        ("SELECT total FROM secrets LIMIT 10", StatusCode::UNAUTHORIZED),
        (
            "SELECT orders.total FROM orders JOIN secrets ON orders.id = secrets.id",
            StatusCode::UNPROCESSABLE_ENTITY,
        ),
        (
            "SELECT total FROM orders WHERE id IN (SELECT id FROM secrets)",
            StatusCode::UNPROCESSABLE_ENTITY,
        ),
    ] {
        let refused = server.app.clone().oneshot(attach(statement)).await.unwrap();
        assert_eq!(refused.status(), status, "{statement}");
    }
    let response =
        server.app.clone().oneshot(attach("SELECT total FROM \"orders\" LIMIT 10")).await.unwrap();
    assert_eq!(response.status(), StatusCode::CREATED);
    let attached: serde_json::Value =
        serde_json::from_slice(&axum::body::to_bytes(response.into_body(), 65_536).await.unwrap())
            .unwrap();
    let capability = attached["capability"].as_str().unwrap();
    // Immutable delivery uses the authenticated edge proof and view capability,
    // without consulting the identity authority on the delivery path.
    authority_task.abort();
    let expected_lease = reply.lock().unwrap()["authorityLease"].clone();
    for _ in 0..3 {
        let lease_response = server
            .app
            .clone()
            .oneshot(
                Request::get(format!(
                    "/api/v1/authorization/leases/{}",
                    crate::delegated_identity::tests::TEST_TENANT
                ))
                .body(Body::empty())
                .unwrap(),
            )
            .await
            .unwrap();
        assert_eq!(lease_response.status(), StatusCode::OK);
        assert!(lease_response.headers()["cache-control"].to_str().unwrap().contains("max-age="));
        let lease: serde_json::Value = serde_json::from_slice(
            &axum::body::to_bytes(lease_response.into_body(), 1024).await.unwrap(),
        )
        .unwrap();
        assert_eq!(
            lease, expected_lease,
            "native delivery preserves producer signature and absolute expiry"
        );
    }

    for (field, expected_delta) in [("snapshot_chunks", false), ("log", true)] {
        if expected_delta {
            server.put("two", 2).await;
        }
        let target = if expected_delta {
            attached[field].as_str().unwrap()
        } else {
            attached[field][0].as_str().unwrap()
        };
        let proof = crate::reactive_api::origin_proof_for(
            &axum::http::Method::GET,
            target,
            now / 600,
            ORIGIN_KEY,
            server.state.region,
        );
        let result = server
            .app
            .clone()
            .oneshot(
                Request::get(target)
                    .header(crate::reactive_api::ORIGIN_PROOF_HEADER, proof)
                    .header(crate::reactive_api::VIEW_CAPABILITY_HEADER, capability)
                    .body(Body::empty())
                    .unwrap(),
            )
            .await
            .unwrap();
        assert_eq!(result.status(), StatusCode::OK);
        let bytes = axum::body::to_bytes(result.into_body(), 8_388_608).await.unwrap();
        let message = decode_envelope(&bytes).unwrap().message;
        if expected_delta {
            assert!(matches!(message, Message::Delta { .. }));
        } else {
            assert!(matches!(message, Message::Snapshot { .. }));
        }
    }
    let refused = server.app.clone().oneshot(attach(SQL)).await.unwrap();
    assert_eq!(
        refused.status(),
        StatusCode::UNAUTHORIZED,
        "authority outage fails new admission closed"
    );
}

#[tokio::test]
async fn public_tenant_lease_route_never_refreshes_expired_or_unknown_authority() {
    let (identity, _, authority_task) = crate::delegated_identity::tests::authority_for_collection(
        crate::delegated_identity::tests::TEST_TENANT,
        "local",
        "prod",
        "read",
        Some("orders"),
    )
    .await;
    identity
        .authorization
        .install(
            crate::authorization_leases::signed_test_lease(
                crate::delegated_identity::tests::TEST_TENANT,
                0,
                0,
                100,
            ),
            100,
            true,
        )
        .unwrap();
    let server = Server::with_identity(Some(Arc::new(identity))).await;
    authority_task.abort();
    for tenant in [crate::delegated_identity::tests::TEST_TENANT, "01J00000000000000000000003"] {
        let response = server
            .app
            .clone()
            .oneshot(
                Request::get(format!("/api/v1/authorization/leases/{tenant}"))
                    .body(Body::empty())
                    .unwrap(),
            )
            .await
            .unwrap();
        assert_eq!(response.status(), StatusCode::SERVICE_UNAVAILABLE);
        assert!(response.headers()["cache-control"].to_str().unwrap().contains("no-store"));
    }
}
