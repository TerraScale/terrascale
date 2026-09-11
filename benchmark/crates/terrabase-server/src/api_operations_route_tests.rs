use super::*;
use crate::api::get_provisioning_operation;
use crate::native_recovery::*;
use axum::{Router, body::Body, http::Request};
use std::{future::Future, pin::Pin, sync::Mutex};
use tower::ServiceExt;

struct NoRestore;
impl RestoreExecutor for NoRestore {
    fn reconcile<'a>(
        &'a self,
        _: &'a NativeRestoreRequest,
    ) -> Pin<Box<dyn Future<Output = Result<(), String>> + Send + 'a>> {
        Box::pin(async { Ok(()) })
    }
    fn execute<'a>(
        &'a self,
        _: &'a NativeRestoreRequest,
    ) -> Pin<Box<dyn Future<Output = Result<NativeRestoreOutcome, String>> + Send + 'a>> {
        Box::pin(async { Err("unused restore".into()) })
    }
}

async fn page(
    state: Arc<ApiState>,
    tenant: &str,
    project: &str,
    after: Option<&str>,
) -> (StatusCode, serde_json::Value) {
    // Exercise the production handler/extractors with already verified project
    // identity, exactly as the public admission middleware supplies it.
    let app = Router::new()
        .route("/api/v1/operations", axum::routing::get(list_operations))
        .layer(Extension(crate::delegated_identity::VerifiedProjectScope {
            tenant_id: tenant.into(),
            project_id: project.into(),
        }))
        .with_state(state);
    let uri = format!(
        "/api/v1/operations?limit=1{}",
        after.map(|v| format!("&after={v}")).unwrap_or_default()
    );
    let response =
        app.oneshot(Request::builder().uri(uri).body(Body::empty()).unwrap()).await.unwrap();
    let status = response.status();
    let body = axum::body::to_bytes(response.into_body(), 1024 * 1024).await.unwrap();
    (status, serde_json::from_slice(&body).unwrap())
}

async fn operation(
    state: Arc<ApiState>,
    tenant: &str,
    project: &str,
    operation_id: &str,
) -> (StatusCode, serde_json::Value) {
    let app = Router::new()
        .route("/api/v1/operations/{operation_id}", axum::routing::get(get_provisioning_operation))
        .layer(Extension(crate::delegated_identity::VerifiedProjectScope {
            tenant_id: tenant.into(),
            project_id: project.into(),
        }))
        .with_state(state);
    let response = app
        .oneshot(
            Request::builder()
                .uri(format!("/api/v1/operations/{operation_id}"))
                .body(Body::empty())
                .unwrap(),
        )
        .await
        .unwrap();
    let status = response.status();
    let body = axum::body::to_bytes(response.into_body(), 1024 * 1024).await.unwrap();
    (status, serde_json::from_slice(&body).unwrap())
}

#[tokio::test]
#[allow(clippy::too_many_lines)]
async fn production_handler_paginates_authority_export_recovery_after_restart() {
    use bytes::Bytes;
    use http_body_util::Full;
    use hyper_util::rt::TokioIo;
    let (registry, member, records, root) = crate::admitted_databases::tests::fixture();
    let registry = Arc::new(registry);
    let record = &records[0];
    let exports_root = root.join("exports");
    let exports = Arc::new(
        crate::export_api::ExportRuntime::local(&exports_root, terrabase::RegionCode::GRU).unwrap(),
    );
    let job_root = exports_root
        .join("jobs")
        .join(blake3::hash(record.database_id.as_bytes()).to_hex());
    let store = crate::export_store::SqliteExportStore::open(&job_root).unwrap();
    let connection = rusqlite::Connection::open(job_root.join("exports.sqlite")).unwrap();
    for id in ['a', 'b'] {
        let id = id.to_string().repeat(32);
        let progress = crate::export_api::ExportStatus {
            job_id: id.clone(),
            status: crate::export_api::JobStatus::Accepted,
            created_at_millis: 1,
            phase: crate::export_api::ExportPhase::Documents,
            cancellation: crate::export_api::ExportCancellation::Available,
            pages: 0,
            records: 0,
            estimated_records: None,
            progress_basis_points: 0,
            resume_cursor: String::new(),
            manifest: None,
            error: None,
        };
        connection
            .execute(
                "INSERT INTO export_jobs(job_id,state,progress) VALUES(?1,x'00',?2)",
                rusqlite::params![id, serde_json::to_vec(&progress).unwrap()],
            )
            .unwrap();
    }
    drop(connection);
    drop(store);
    let recovery_root = root.join("recovery");
    let fleet = Arc::new(Mutex::new(
        terrabase::backup::worker::NativeBackupFleet::new(terrabase::backup::worker::FleetLimits {
            streams: 2,
            staging_bytes_per_stream: 16 * 1024 * 1024,
            inventory_bytes_per_stream: 16 * 1024 * 1024,
            points_per_stream: 2,
        })
        .unwrap(),
    ));
    let (recovery, worker) = NativeRecoveryRuntime::new(
        &recovery_root,
        fleet,
        registry.clone(),
        NativeRecoveryLimits { pending_operations: 2, retained_operations: 4 },
        Arc::new(NoRestore),
    )
    .unwrap();
    worker.shutdown().await.unwrap();
    let connection = rusqlite::Connection::open(recovery_root.join("recovery.sqlite")).unwrap();
    for id in ["recovery-a", "recovery-b"] {
        connection.execute("INSERT INTO native_recovery_operations(operation_id,source_database_id,phase,record) VALUES(?1,?2,'failed',?3)",rusqlite::params![id,record.database_id,serde_json::to_vec(&NativeRecoveryOperation {operation_id:id.into(),source_database_id:record.database_id.clone(),phase:NativeRecoveryPhase::Failed,facts:None,error:Some("failed".into())}).unwrap()]).unwrap();
    }
    drop(connection);
    let socket = root.join("authority.sock");
    let listener = tokio::net::UnixListener::bind(&socket).unwrap();
    let probe = tokio::net::UnixStream::connect(&socket).await.unwrap();
    let uid = probe.peer_cred().unwrap().uid();
    let _ = listener.accept().await.unwrap();
    drop(probe);
    let authority_body=serde_json::json!({"environment":"production","tenant_id":record.tenant_id,"project_id":record.project_id,"operations":[{"operation_id":"provision","kind":"database_create","database_id":record.database_id,"name":"orders","state":"queued","stage":"accepted","created_at":"2026-01-01T00:00:00Z","revision":1,"cancellable":false,"cancellation_reason":"unsupported"}],"covered_kinds":["database_create","branch_create"]}).to_string();
    let calls = Arc::new(std::sync::atomic::AtomicUsize::new(0));
    let server_calls = calls.clone();
    let server = tokio::spawn(async move {
        loop {
            let (stream, _) = listener.accept().await.unwrap();
            let body = authority_body.clone();
            let calls = server_calls.clone();
            tokio::spawn(async move {
                let _ = hyper::server::conn::http1::Builder::new()
                    .serve_connection(
                        TokioIo::new(stream),
                        hyper::service::service_fn(move |request| {
                            let body = body.clone();
                            calls.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
                            async move {
                                if request.uri().path() == "/internal/v1/databases/status" {
                                    return Ok::<_, std::convert::Infallible>(
                                        hyper::Response::builder()
                                            .status(hyper::StatusCode::NOT_FOUND)
                                            .body(Full::new(Bytes::new()))
                                            .unwrap(),
                                    );
                                }
                                Ok(hyper::Response::new(Full::new(Bytes::from(body))))
                            }
                        }),
                    )
                    .await;
            });
        }
    });
    let mut state = super::super::tests::state_with_keys_and_control();
    {
        let state = Arc::get_mut(&mut state).unwrap();
        state.admitted_databases = Some(registry);
        state.exports = Some(exports);
        state.native_recovery = Some(recovery);
        state.provisioning = Some(Arc::new(
            crate::provisioning::ProvisioningClient::new(socket, uid, "production".into()).unwrap(),
        ));
    }
    let (status, first) = page(state.clone(), &record.tenant_id, &record.project_id, None).await;
    assert_eq!(status, StatusCode::OK, "{first}");
    assert_eq!(first["operations"][0]["operation_id"], "provision");
    let mut cursor = first["next_cursor"]
        .as_str()
        .expect("exact-full authority page must continue to member")
        .to_owned();
    // Reopen actual durable export storage and persistent pagination key.
    Arc::get_mut(&mut state).unwrap().exports = Some(Arc::new(
        crate::export_api::ExportRuntime::local(&exports_root, terrabase::RegionCode::GRU).unwrap(),
    ));
    let (status, result) =
        operation(state.clone(), &record.tenant_id, &record.project_id, &"a".repeat(32)).await;
    assert_eq!(status, StatusCode::OK, "{result}");
    assert_eq!(result["operation_id"], "a".repeat(32));
    assert_eq!(result["kind"], "export");
    let mut ids = Vec::new();
    for _ in 0..8 {
        let (status, result) =
            page(state.clone(), &record.tenant_id, &record.project_id, Some(&cursor)).await;
        assert_eq!(status, StatusCode::OK, "{result}");
        for operation in result["operations"].as_array().unwrap() {
            ids.push(operation["operation_id"].as_str().unwrap().to_owned());
        }
        let Some(next) = result["next_cursor"].as_str() else { break };
        cursor = next.into();
    }
    assert_eq!(ids, vec!["a".repeat(32), "b".repeat(32), "recovery-a".into(), "recovery-b".into()]);
    assert_eq!(
        calls.load(std::sync::atomic::Ordering::Relaxed),
        2,
        "member GET plus continuation should each use the authority"
    );
    // A member-owned operation remains readable after the authority client is
    // unavailable, matching the collection endpoint's journal fallback.
    Arc::get_mut(&mut state).unwrap().provisioning = None;
    let (status, result) =
        operation(state.clone(), &record.tenant_id, &record.project_id, &"a".repeat(32)).await;
    assert_eq!(status, StatusCode::OK, "{result}");
    assert_eq!(result["operation_id"], "a".repeat(32));
    assert_eq!(result["kind"], "export");
    // With no authority fallback, foreign project/member tokens fail closed.
    assert_ne!(
        page(state.clone(), &records[1].tenant_id, &records[1].project_id, Some(&cursor)).await.0,
        StatusCode::OK
    );
    Arc::get_mut(&mut state).unwrap().member_id = "foreign-member".into();
    assert_ne!(
        page(state.clone(), &record.tenant_id, &record.project_id, Some(&cursor)).await.0,
        StatusCode::OK
    );
    server.abort();
    drop(state);
    drop(member);
    let _ = std::fs::remove_dir_all(root);
}
