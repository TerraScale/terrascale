use super::*;
use crate::api::router;
use axum::Router;
use axum::body::Body;
use axum::http::Request;
use std::sync::Arc;
use terrabase::RegionCode;
use tower::util::ServiceExt;

async fn call(app: &Router, method: &str, path: &str, body: &str) -> serde_json::Value {
    let response = app
        .clone()
        .oneshot(
            Request::builder()
                .method(method)
                .uri(path)
                .header("authorization", "Bearer tskey_test_abcdefghijklmnop")
                .header("content-type", "application/json")
                .body(Body::from(body.to_owned()))
                .unwrap(),
        )
        .await
        .unwrap();
    let status = response.status();
    let body = axum::body::to_bytes(response.into_body(), 1024 * 1024).await.unwrap();
    assert!(status.is_success(), "{status}: {}", String::from_utf8_lossy(&body));
    serde_json::from_slice(&body).unwrap()
}

#[tokio::test]
async fn disconnected_client_discovers_cancels_and_resumes_the_same_durable_export() {
    let mut state = super::super::tests::state_with_keys_and_control();
    let root = state.backup_root.with_file_name("exports");
    let runtime = Arc::new(export::ExportRuntime::local(&root, RegionCode::GRU).unwrap());
    // A process shutdown yields accepted workers before their first page.
    // Reconstructing the runtime below exercises the real startup recovery path.
    runtime.stop();
    Arc::get_mut(&mut state).unwrap().exports = Some(runtime);
    let app = router(state.clone());
    call(&app, "POST", "/api/v1/databases", r#"{"name":"prod","placement":["GRU"]}"#).await;
    call(&app, "PUT", "/api/v1/databases/prod/collections/orders/documents/one", r#"{"n":1}"#)
        .await;
    call(&app, "POST", "/api/v1/databases/prod/exports", r#"{"idempotency_key":"lifecycle"}"#)
        .await;
    // The new client needs no saved operation identity to return to its work.
    let list = call(&app, "GET", "/api/v1/databases/prod/exports?limit=1", "").await;
    let jobs = list["exports"].as_array().unwrap();
    assert_eq!(jobs.len(), 1);
    assert!(!list.to_string().contains("canonical_json"));
    assert!(!list.to_string().contains("\"n\":1"));
    let job = jobs[0]["job_id"].as_str().unwrap().to_owned();
    let path = format!("/api/v1/databases/prod/exports/{job}");
    let requested = call(&app, "POST", &format!("{path}/cancel"), "").await;
    assert_eq!(requested["cancellation"], "requested");
    drop(app);
    Arc::get_mut(&mut state).unwrap().exports =
        Some(Arc::new(export::ExportRuntime::local(&root, RegionCode::GRU).unwrap()));
    let recovering = state.clone();
    tokio::task::spawn_blocking(move || recover_exports(&recovering)).await.unwrap().unwrap();
    let app = router(state);
    let cancelled = call(&app, "GET", &path, "").await;
    assert_eq!(cancelled["status"], "Cancelled");
    assert_eq!(cancelled["records"], 0);
    assert_eq!(cancelled["cancellation"], "complete");
    call(
        &app,
        "POST",
        &format!("{path}/resume"),
        &serde_json::json!({
            "resume_cursor": cancelled["resume_cursor"],
        })
        .to_string(),
    )
    .await;
    tokio::time::timeout(std::time::Duration::from_secs(30), async {
        loop {
            let status = call(&app, "GET", &path, "").await;
            assert_ne!(status["status"], "Failed", "{status}");
            if status["status"] == "Complete" {
                assert_eq!(status["job_id"], job);
                assert_eq!(status["records"], 2); // one document and its retained revision
                assert_eq!(status["cancellation"], "unavailable");
                break;
            }
            tokio::time::sleep(std::time::Duration::from_millis(10)).await;
        }
    })
    .await
    .unwrap();
    let terminal = call(&app, "POST", &format!("{path}/cancel"), "").await;
    assert_eq!(terminal["status"], "Complete");
    assert_eq!(terminal["cancellation"], "unavailable");
}
