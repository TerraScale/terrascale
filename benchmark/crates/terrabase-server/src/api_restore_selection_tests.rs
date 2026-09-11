use super::*;
use axum::body::Body;
use axum::http::{Request, StatusCode};
use bytes::Bytes;
use http_body_util::{BodyExt as _, Full};
use hyper_util::rt::TokioIo;
use std::sync::{Arc, Mutex};
use tower::ServiceExt;

#[tokio::test]
async fn restore_route_forwards_each_selector_to_provisioning_authority() {
    let (registry, _, records, root) = crate::admitted_databases::tests::fixture();
    let source = records.first().expect("fixture source").clone();
    let socket = root.join("restore-authority.sock");
    let listener = tokio::net::UnixListener::bind(&socket).expect("authority socket");
    let probe = tokio::net::UnixStream::connect(&socket).await.expect("probe");
    let uid = probe.peer_cred().expect("peer credentials").uid();
    let _ = listener.accept().await.expect("probe accept");
    drop(probe);
    let requests = Arc::new(Mutex::new(Vec::<serde_json::Value>::new()));
    let server_requests = Arc::clone(&requests);
    let tenant = source.tenant_id.clone();
    let project = source.project_id.clone();
    let server = tokio::spawn(async move {
        loop {
            let Ok((stream, _)) = listener.accept().await else { break };
            let requests = Arc::clone(&server_requests);
            let tenant = tenant.clone();
            let project = project.clone();
            tokio::spawn(async move {
                let _ = hyper::server::conn::http1::Builder::new()
                    .serve_connection(
                        TokioIo::new(stream),
                        hyper::service::service_fn(
                            move |request: hyper::Request<hyper::body::Incoming>| {
                                let requests = Arc::clone(&requests);
                                let tenant = tenant.clone();
                                let project = project.clone();
                                async move {
                                    let body =
                                        request.into_body().collect().await.unwrap().to_bytes();
                                    let request: serde_json::Value =
                                        serde_json::from_slice(&body).unwrap();
                                    let operation_id = request["operation_id"].clone();
                                    let name = request["name"].clone();
                                    requests.lock().unwrap().push(request);
                                    let response = serde_json::json!({
                                        "environment":"production", "tenant_id":tenant,
                                        "project_id":project, "operation_id":operation_id,
                                        "database_id":"01ARZ3NDEKTSV4RRFFQ69G5FAV",
                                        "key_version":"01ARZ3NDEKTSV4RRFFQ69G5FAV", "name":name,
                                        "regions":["GRU"],
                                        "residency":"global", "state":"accepted"
                                    });
                                    Ok::<_, std::convert::Infallible>(
                                        hyper::Response::builder()
                                            .status(hyper::StatusCode::ACCEPTED)
                                            .body(Full::new(Bytes::from(response.to_string())))
                                            .unwrap(),
                                    )
                                }
                            },
                        ),
                    )
                    .await;
            });
        }
    });
    let mut state = crate::api::tests::state_with_keys_and_control();
    {
        let state = Arc::get_mut(&mut state).expect("unique state");
        state.admitted_databases = Some(Arc::new(registry));
        state.provisioning = Some(Arc::new(
            crate::provisioning::ProvisioningClient::new(socket, uid, "production".into())
                .expect("provisioning client"),
        ));
    }
    let auth = "Bearer tskey_test_abcdefghijklmnop";
    let path = format!("/api/v1/databases/{}/backups/restore", source.database_id);
    for (operation_id, selector) in [
        (
            "op",
            serde_json::json!({"point_id":"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"}),
        ),
        ("op-at", serde_json::json!({"at":100})),
        ("op-latest", serde_json::json!({})),
    ] {
        let mut body = serde_json::json!({"name":"restored", "operation_id":operation_id});
        body.as_object_mut().unwrap().extend(selector.as_object().unwrap().clone());
        let response = crate::api::router(Arc::clone(&state))
            .oneshot(
                Request::post(&path)
                    .header("authorization", auth)
                    .header("x-terrascale-tenant-id", &source.tenant_id)
                    .header("x-terrascale-project-id", &source.project_id)
                    .body(Body::from(body.to_string()))
                    .unwrap(),
            )
            .await
            .unwrap();
        assert_eq!(response.status(), StatusCode::ACCEPTED);
    }
    for body in [
        serde_json::json!({"name":"restored", "operation_id":"bad", "at":100, "point_id":"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"}),
        serde_json::json!({"name":"restored", "operation_id":"bad", "unknown":true}),
    ] {
        let response = crate::api::router(Arc::clone(&state))
            .oneshot(
                Request::post(&path)
                    .header("authorization", auth)
                    .header("x-terrascale-tenant-id", &source.tenant_id)
                    .header("x-terrascale-project-id", &source.project_id)
                    .body(Body::from(body.to_string()))
                    .unwrap(),
            )
            .await
            .unwrap();
        assert_eq!(response.status(), StatusCode::BAD_REQUEST);
    }
    let response = crate::api::router(Arc::clone(&state))
        .oneshot(
            Request::post(&path)
                .header("authorization", auth)
                .header("x-terrascale-tenant-id", "foreign-tenant")
                .header("x-terrascale-project-id", "foreign-project")
                .body(Body::from(r#"{"name":"restored","operation_id":"foreign"}"#))
                .unwrap(),
        )
        .await
        .unwrap();
    assert_eq!(response.status(), StatusCode::FORBIDDEN);
    server.abort();
    let requests = requests.lock().unwrap();
    assert_eq!(requests.len(), 3);
    assert_eq!(requests[0]["selection"]["kind"], "point_id");
    assert_eq!(requests[1]["selection"]["kind"], "unix_millis");
    assert_eq!(requests[2]["selection"]["kind"], "latest");
    for request in requests.iter() {
        assert_eq!(request["source_database_id"], source.database_id);
        assert_eq!(request["tenant_id"], source.tenant_id);
        assert_eq!(request["project_id"], source.project_id);
    }
}

#[tokio::test]
async fn backup_points_route_uses_native_catalog_and_enforces_scope() {
    use crate::native_backup_runtime::{
        AdmittedBackupEngine, NativeBackupInput, NativeBackupRuntime,
    };
    use object_store::ObjectStore;
    use terrabase::backup::object_store::{ObjectDestination, Provider};
    use terrabase::backup::worker::{CustomerKmsBackupKeys, FleetLimits};
    let (registry, _, records, root) = crate::admitted_databases::tests::fixture();
    let source = records.first().expect("fixture source").clone();
    let snapshot = registry
        .retained_snapshot()
        .unwrap()
        .into_iter()
        .find(|entry| entry.database.record.database_id == source.database_id)
        .expect("source snapshot");
    let scope = terrabase::member::DatabaseScope::new(
        &source.environment,
        source.tenant_id.parse().unwrap(),
        source.project_id.parse().unwrap(),
        source.database_id.parse().unwrap(),
        source.region.parse().unwrap(),
    )
    .unwrap();
    let store = Arc::new(object_store::memory::InMemory::new());
    let runtime = NativeBackupRuntime::new(
        FleetLimits {
            streams: 1,
            staging_bytes_per_stream: 16 * 1024 * 1024,
            inventory_bytes_per_stream: 16 * 1024 * 1024,
            points_per_stream: 2,
        },
        [NativeBackupInput {
            engine: Arc::new(AdmittedBackupEngine::new(Arc::clone(&snapshot.database.host))),
            keys: Arc::new(
                CustomerKmsBackupKeys::new(crate::test_keys::backup_keys(&scope)).unwrap(),
            ),
            destinations: vec![(
                Provider::Oci,
                Arc::new(ObjectDestination::for_test(
                    Provider::Oci,
                    Arc::clone(&store) as Arc<dyn ObjectStore>,
                    4 * 1024 * 1024,
                    8 * 1024 * 1024,
                )),
            )],
            historical_destinations: Vec::new(),
            policy_revision: "api-points-test".into(),
            retention_days: 1,
            staging: root.join("points-staging"),
            placement: terrabase::backup::PlacementLock::explicit([source.region.parse().unwrap()])
                .unwrap(),
        }],
    )
    .unwrap();
    let mut state = crate::api::tests::state_with_keys_and_control();
    Arc::get_mut(&mut state).unwrap().admitted_databases = Some(Arc::new(registry));
    Arc::get_mut(&mut state).unwrap().native_backups = Some(Arc::new(runtime));
    let path = format!("/api/v1/databases/{}/backups/points", source.database_id);
    let response = crate::api::router(Arc::clone(&state))
        .oneshot(
            Request::get(&path)
                .header("authorization", "Bearer tskey_test_abcdefghijklmnop")
                .header("x-terrascale-tenant-id", &source.tenant_id)
                .header("x-terrascale-project-id", &source.project_id)
                .body(Body::empty())
                .unwrap(),
        )
        .await
        .unwrap();
    assert_eq!(response.status(), StatusCode::OK);
    let body = axum::body::to_bytes(response.into_body(), 1024 * 1024).await.unwrap();
    let page: serde_json::Value = serde_json::from_slice(&body).unwrap();
    assert!(page["points"].is_array());
    let response = crate::api::router(Arc::clone(&state))
        .oneshot(
            Request::get(&path)
                .header("authorization", "Bearer tskey_test_abcdefghijklmnop")
                .header("x-terrascale-tenant-id", "foreign")
                .header("x-terrascale-project-id", "foreign")
                .body(Body::empty())
                .unwrap(),
        )
        .await
        .unwrap();
    assert_eq!(response.status(), StatusCode::FORBIDDEN);
    for query in ["?unknown=true", "?limit=101", "?cursor=bad"] {
        let response = crate::api::router(Arc::clone(&state))
            .oneshot(
                Request::get(format!("{path}{query}"))
                    .header("authorization", "Bearer tskey_test_abcdefghijklmnop")
                    .header("x-terrascale-tenant-id", &source.tenant_id)
                    .header("x-terrascale-project-id", &source.project_id)
                    .body(Body::empty())
                    .unwrap(),
            )
            .await
            .unwrap();
        assert_eq!(response.status(), StatusCode::BAD_REQUEST, "{query}");
    }
}
