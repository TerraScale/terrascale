use super::*;
use crate::api::router;
use crate::export_api::{ExportAccepted, ExportRuntime, ExportStatus, ExportStore, JobStatus};
use axum::body::Body;
use axum::http::Request;
use axum::{Router, response::Response};
use std::sync::Arc;
use terrabase::RegionCode;
use tower::util::ServiceExt;

async fn request(app: &Router, method: &str, path: &str, payload: &str) -> Response {
    app.clone()
        .oneshot(
            Request::builder()
                .method(method)
                .uri(path)
                .header("authorization", "Bearer tskey_test_abcdefghijklmnop")
                .header("content-type", "application/json")
                .body(Body::from(payload.to_owned()))
                .unwrap(),
        )
        .await
        .unwrap()
}

async fn decode<T: serde::de::DeserializeOwned>(response: Response) -> T {
    let bytes = axum::body::to_bytes(response.into_body(), 1024 * 1024).await.unwrap();
    serde_json::from_slice(&bytes)
        .unwrap_or_else(|error| panic!("{error}: {}", String::from_utf8_lossy(&bytes)))
}

async fn database(app: &Router, name: &str) {
    let body = serde_json::json!({"name":name,"placement":["GRU"],"residency":null});
    let response = request(app, "POST", "/api/v1/databases", &body.to_string()).await;
    assert_eq!(response.status(), StatusCode::CREATED);
}

async fn complete(app: &Router, path: &str) -> ExportStatus {
    tokio::time::timeout(std::time::Duration::from_secs(30), async {
        loop {
            let response = request(app, "GET", path, "").await;
            assert_eq!(response.status(), StatusCode::OK);
            let status: ExportStatus = decode(response).await;
            assert_ne!(status.status, JobStatus::Failed, "{:?}", status.error);
            if status.status == JobStatus::Complete {
                return status;
            }
            tokio::time::sleep(std::time::Duration::from_millis(10)).await;
        }
    })
    .await
    .expect("export completes")
}

#[tokio::test]
#[allow(clippy::too_many_lines)]
async fn http_export_reads_real_documents_and_recovers_after_runtime_restart() {
    let mut state = super::super::tests::state_with_keys_and_control();
    let root = state.backup_root.with_file_name("exports");
    Arc::get_mut(&mut state).unwrap().exports =
        Some(Arc::new(ExportRuntime::local(&root, RegionCode::GRU).unwrap()));
    let app = router(Arc::clone(&state));
    database(&app, "prod").await;
    database(&app, "other").await;
    seed_export_documents(&app).await;
    let response = request(
        &app,
        "POST",
        "/api/v1/databases/prod/exports",
        r#"{"idempotency_key":"durable-http-job"}"#,
    )
    .await;
    assert_eq!(response.status(), StatusCode::ACCEPTED);
    let accepted: ExportAccepted = decode(response).await;
    assert_eq!(accepted.status_path, format!("/api/v1/databases/prod/exports/{}", accepted.job_id));
    let finished = complete(&app, &accepted.status_path).await;
    assert!(finished.records >= 2, "real documents were exported");
    assert_eq!(finished.progress_basis_points, 10_000);
    let manifest = finished.manifest.as_ref().unwrap();
    let directory = root.join("artifacts/prod").join(&accepted.job_id);
    let encrypted = std::fs::read(directory.join("manifest.json")).unwrap();
    assert_eq!(encrypted.len() as u64, manifest.length);
    assert_eq!(blake3::hash(&encrypted).to_hex().as_str(), manifest.checksum);
    assert!(directory.join("COMMITTED").is_file());
    assert_eq!(
        request(&app, "GET", &format!("/api/v1/databases/other/exports/{}", accepted.job_id), "")
            .await
            .status(),
        StatusCode::NOT_FOUND
    );

    let manifest_response =
        request(&app, "GET", &format!("{}/artifacts/manifest.json", accepted.status_path), "")
            .await;
    assert_eq!(manifest_response.status(), StatusCode::OK);
    let manifest_body: serde_json::Value = decode(manifest_response).await;
    let document_member = manifest_body["members"]
        .as_array()
        .unwrap()
        .iter()
        .find_map(|fact| fact["path"].as_str().filter(|path| path.starts_with("documents/")))
        .expect("document page in manifest");
    let document_fact = manifest_body["members"]
        .as_array()
        .unwrap()
        .iter()
        .find(|fact| fact["path"] == document_member)
        .expect("document artifact fact");
    let encoded_member = document_member.replace('/', "%2F");
    let download_path = format!("{}/artifacts/{encoded_member}", accepted.status_path);
    let page_response = request(&app, "GET", &download_path, "").await;
    assert_eq!(page_response.status(), StatusCode::OK);
    let plaintext_length = page_response
        .headers()
        .get("x-terrascale-artifact-length")
        .unwrap()
        .to_str()
        .unwrap()
        .parse::<usize>()
        .unwrap();
    let plaintext_checksum = page_response
        .headers()
        .get("x-terrascale-artifact-checksum")
        .unwrap()
        .to_str()
        .unwrap()
        .to_owned();
    let page = axum::body::to_bytes(page_response.into_body(), 1024 * 1024).await.unwrap();
    assert_eq!(plaintext_length, page.len());
    assert_eq!(plaintext_checksum, blake3::hash(&page).to_hex().to_string());
    assert_ne!(document_fact["length"].as_u64().unwrap(), plaintext_length as u64);
    assert_ne!(document_fact["checksum"].as_str().unwrap(), plaintext_checksum);
    let lines: Vec<serde_json::Value> = std::str::from_utf8(&page)
        .unwrap()
        .lines()
        .map(|line| serde_json::from_str(line).unwrap())
        .collect();
    assert_eq!(lines.len(), 2);
    assert_eq!(lines[0]["collection"], "orders");
    assert_eq!(lines[0]["id"], "one");
    assert_eq!(lines[1]["id"], "two");
    assert_eq!(lines[0]["value"], serde_json::json!({"value":1}));
    assert_eq!(lines[1]["value"], serde_json::json!({"value":2}));
    let history_member = manifest_body["members"]
        .as_array()
        .unwrap()
        .iter()
        .find_map(|fact| fact["path"].as_str().filter(|path| path.starts_with("history/")))
        .expect("history page in manifest");
    let history_response = request(
        &app,
        "GET",
        &format!("{}/artifacts/{}", accepted.status_path, history_member.replace('/', "%2F")),
        "",
    )
    .await;
    assert_eq!(history_response.status(), StatusCode::OK);
    let history = axum::body::to_bytes(history_response.into_body(), 1024 * 1024).await.unwrap();
    let history: Vec<serde_json::Value> = std::str::from_utf8(&history)
        .unwrap()
        .lines()
        .map(|line| serde_json::from_str(line).unwrap())
        .collect();
    assert_eq!(history.len(), 2);
    assert!(history.iter().all(|line| line["deleted"] == false));
    assert!(
        history
            .iter()
            .any(|line| line["id"] == "one" && line["value"] == serde_json::json!({"value":1}))
    );
    assert_ne!(std::fs::read(directory.join(document_member)).unwrap(), page.as_ref());
    assert!(!serde_json::to_value(&finished).unwrap().to_string().contains("documents/"));
    assert!(
        !request(
            &app,
            "GET",
            &format!(
                "/api/v1/databases/other/exports/{}/artifacts/{encoded_member}",
                accepted.job_id
            ),
            ""
        )
        .await
        .status()
        .is_success()
    );
    state.keys.as_ref().unwrap().register(
        "tskey_test_prod_only_abcdefghijkl",
        "prod-reader",
        crate::api_keys::Scope::database("prod", crate::api_keys::Verbs::READ),
    );
    let forbidden = app
        .clone()
        .oneshot(
            Request::get(format!("/api/v1/databases/other/exports/{}", accepted.job_id))
                .header("authorization", "Bearer tskey_test_prod_only_abcdefghijkl")
                .body(Body::empty())
                .unwrap(),
        )
        .await
        .unwrap();
    assert_eq!(forbidden.status(), StatusCode::FORBIDDEN);
    let unauthenticated = app
        .clone()
        .oneshot(Request::get(&accepted.status_path).body(Body::empty()).unwrap())
        .await
        .unwrap();
    assert_eq!(unauthenticated.status(), StatusCode::UNAUTHORIZED);
    state.keys.as_ref().unwrap().register(
        "tskey_test_collection_admin_abcdef",
        "collection-admin",
        crate::api_keys::Scope::database_collection("prod", "orders", crate::api_keys::Verbs::ALL),
    );
    for (method, path, payload) in [
        ("POST", "/api/v1/databases/prod/exports", r#"{"idempotency_key":"unauthorized"}"#),
        ("GET", accepted.status_path.as_str(), ""),
    ] {
        let response = app
            .clone()
            .oneshot(
                Request::builder()
                    .method(method)
                    .uri(path)
                    .header("authorization", "Bearer tskey_test_collection_admin_abcdef")
                    .body(Body::from(payload))
                    .unwrap(),
            )
            .await
            .unwrap();
        assert_eq!(response.status(), StatusCode::FORBIDDEN);
    }
    drop(app);
    let database_id = document_host(&state, "prod").unwrap().engine().database_id().to_text();
    let identity = blake3::hash(database_id.as_bytes()).to_hex().to_string();
    let store =
        crate::export_store::SqliteExportStore::open(root.join("jobs").join(identity)).unwrap();
    let lease = tokio::time::timeout(std::time::Duration::from_secs(5), async {
        loop {
            if let Some(lease) = store.acquire_job(&accepted.job_id).unwrap() {
                return lease;
            }
            tokio::time::sleep(std::time::Duration::from_millis(1)).await;
        }
    })
    .await
    .unwrap();
    let mut interrupted = store.load(&accepted.job_id).unwrap().unwrap();
    interrupted.status = JobStatus::Running;
    store.save(&interrupted).unwrap();
    drop(lease);
    Arc::get_mut(&mut state).unwrap().exports =
        Some(Arc::new(ExportRuntime::local(&root, RegionCode::GRU).unwrap()));
    let app = router(Arc::clone(&state));
    let interrupted: ExportStatus =
        decode(request(&app, "GET", &accepted.status_path, "").await).await;
    assert_eq!(interrupted.status, JobStatus::Running);
    let recovering = Arc::clone(&state);
    tokio::spawn(async move { super::super::recover_exports(&recovering).await })
        .await
        .unwrap()
        .unwrap();
    let recovered = complete(&app, &accepted.status_path).await;
    assert_eq!(recovered, finished);
    let repeated = request(
        &app,
        "POST",
        "/api/v1/databases/prod/exports",
        r#"{"idempotency_key":"durable-http-job"}"#,
    )
    .await;
    assert_eq!(repeated.status(), StatusCode::ACCEPTED);
    let repeated: ExportAccepted = decode(repeated).await;
    assert_eq!(repeated.job_id, accepted.job_id);
    let resume_path = format!("{}/resume", accepted.status_path);
    assert_eq!(
        request(&app, "POST", &resume_path, r#"{"resume_cursor":"tampered"}"#).await.status(),
        StatusCode::UNPROCESSABLE_ENTITY
    );
    let resumed = request(
        &app,
        "POST",
        &resume_path,
        &serde_json::json!({"resume_cursor":finished.resume_cursor}).to_string(),
    )
    .await;
    assert_eq!(resumed.status(), StatusCode::OK);
    assert_eq!(decode::<ExportStatus>(resumed).await, finished);
    assert_eq!(std::fs::read(directory.join("manifest.json")).unwrap(), encrypted);
}

async fn seed_export_documents(app: &Router) {
    for (id, value) in [("one", 1), ("two", 2)] {
        let response = request(
            app,
            "PUT",
            &format!("/api/v1/databases/prod/collections/orders/documents/{id}"),
            &serde_json::json!({"value":value}).to_string(),
        )
        .await;
        assert_eq!(response.status(), StatusCode::OK);
    }
}

#[tokio::test]
async fn http_export_refuses_destination_outside_database_placement() {
    let mut state = super::super::tests::state_with_keys_and_control();
    let root = state.backup_root.with_file_name("exports");
    Arc::get_mut(&mut state).unwrap().exports =
        Some(Arc::new(ExportRuntime::local(&root, RegionCode::FRA).unwrap()));
    let app = router(state);
    database(&app, "locked").await;
    let response = request(
        &app,
        "POST",
        "/api/v1/databases/locked/exports",
        r#"{"idempotency_key":"must-not-publish"}"#,
    )
    .await;
    assert_eq!(response.status(), StatusCode::MISDIRECTED_REQUEST);
    assert!(!root.join("artifacts/locked").exists());
}
