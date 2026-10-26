#[path = "support/project_http.rs"]
mod http;
use http::{reply, Fixture, PROJECT};
use serde_json::{json, Value};
use terrascale::v1::{Client, FileObject, FileObjectPage, FileUploadRequest};
const BUCKET: &str = "01ARZ3NDEKTSV4RRFFQ69G5FAC";
const FILE: &str = "01ARZ3NDEKTSV4RRFFQ69G5FAD";
const SHA: &str = "LPJNul+wow4m6DsqxbninhsWHlwfp0JecwQzYpOLmCQ=";
fn object(size: Value) -> Value {
    json!({"id":FILE,"name":"hello.txt","contentType":"text/plain","size":size,"sha256":SHA,
        "metadata":{"userId":"subject"},"status":"ready","uploadExpiresAt":"2026-10-26T12:05:00Z"})
}
fn upload() -> FileUploadRequest {
    FileUploadRequest {
        name: "hello.txt".into(),
        content_type: "text/plain".into(),
        size: 5,
        sha256: SHA.into(),
        metadata: json!({"userId":"subject"}),
    }
}
fn capability() -> Value {
    json!({"id":FILE,"url":"https://storage.example/object?signature=fixture-private",
        "expiresAt":"2026-10-26T12:05:00Z","headers":{"x-storage-token":"fixture-private"}})
}

#[tokio::test]
async fn file_lifecycle_preserves_paths_body_paging_and_private_capabilities() {
    let fixture = Fixture::new(vec![
        reply(
            200,
            &json!({"items":[object(json!(5))],"next":FILE}).to_string(),
        ),
        reply(200, r#"{"items":[],"next":null}"#),
        reply(201, &capability().to_string()),
        reply(200, &object(json!("5")).to_string()),
        reply(200, &capability().to_string()),
        reply(204, ""),
    ])
    .await;
    let client = fixture.client();
    let page = client.list_bucket_files(BUCKET, None).await.unwrap();
    assert_eq!(page.items[0].size, 5);
    assert!(client
        .list_bucket_files(BUCKET, page.next.as_deref())
        .await
        .unwrap()
        .items
        .is_empty());
    let cap = client.create_file_upload(BUCKET, &upload()).await.unwrap();
    assert!(!format!("{cap:?}").contains("fixture-private"));
    assert_eq!(cap.headers["x-storage-token"], "fixture-private");
    assert_eq!(
        client
            .complete_file_upload(BUCKET, FILE)
            .await
            .unwrap()
            .sha256,
        SHA
    );
    client.create_file_download(BUCKET, FILE).await.unwrap();
    client.delete_file(BUCKET, FILE).await.unwrap();
    let seen = fixture.requests.lock().unwrap();
    for (i, method, suffix) in [
        (0, "GET", "".into()),
        (1, "GET", format!("?after={FILE}")),
        (2, "POST", "/upload".into()),
        (3, "POST", format!("/{FILE}/complete")),
        (4, "POST", format!("/{FILE}/download")),
        (5, "DELETE", format!("/{FILE}")),
    ] {
        assert_eq!(seen[i].method, method);
        assert_eq!(
            seen[i].uri,
            format!("/v1/projects/{PROJECT}/buckets/{BUCKET}/files{suffix}")
        );
        assert_eq!(seen[i].headers["authorization"], "Bearer fixture-token");
    }
    assert_eq!(
        serde_json::from_slice::<Value>(&seen[2].body).unwrap(),
        serde_json::to_value(upload()).unwrap()
    );
}

#[test]
fn metadata_sizes_accept_exact_int64_integers_and_decimal_strings_only() {
    for n in [0, 5, 9_007_199_254_740_993, i64::MAX as u64] {
        for size in [json!(n), json!(n.to_string())] {
            assert_eq!(
                serde_json::from_value::<FileObject>(object(size))
                    .unwrap()
                    .size,
                n
            );
        }
    }
    for size in [
        json!(-1),
        json!(1.5),
        json!(true),
        json!("01"),
        json!("+1"),
        json!("1e2"),
        json!(" 1"),
        json!(u64::MAX),
    ] {
        assert!(serde_json::from_value::<FileObject>(object(size)).is_err());
    }
    for page in [
        json!({"items":[]}),
        json!({"items":[],"next":""}),
        json!({"items":[],"next":"not-ulid"}),
    ] {
        assert!(serde_json::from_value::<FileObjectPage>(page).is_err());
    }
}

#[tokio::test]
async fn invalid_reservations_ids_and_platform_scope_fail_before_dispatch() {
    let fixture = Fixture::new(vec![]).await;
    let client = fixture.client();
    for (size, metadata) in [
        (0, json!({})),
        ((100 << 20) + 1, json!({})),
        (5, Value::Null),
        (5, json!([])),
        (5, json!("text")),
    ] {
        let mut body = upload();
        body.size = size;
        body.metadata = metadata;
        assert!(client.create_file_upload(BUCKET, &body).await.is_err());
    }
    let mut body = upload();
    body.sha256 = "a".repeat(64);
    assert!(client.create_file_upload(BUCKET, &body).await.is_err());
    assert!(client
        .list_bucket_files(BUCKET, Some("../files"))
        .await
        .is_err());
    assert!(client.delete_file("../bucket", FILE).await.is_err());
    assert!(client.complete_file_upload(BUCKET, "bad").await.is_err());
    let platform = Client::for_platform(&fixture.url, "fixture-token")
        .build()
        .unwrap();
    assert!(platform.create_file_download(BUCKET, FILE).await.is_err());
    assert!(fixture.requests.lock().unwrap().is_empty());
}

#[tokio::test]
async fn capability_failures_redact_bodies_text_and_extensions_without_replaying() {
    for status in [200, 403, 503] {
        let mut body = capability();
        body["headers"] = json!("fixture-private");
        body["error"] = json!({"code":if status==403 {"forbidden_scope"} else {"storage_busy"},"message":"fixture-private"});
        let fixture = Fixture::new(vec![reply(status, &body.to_string())]).await;
        let client = Client::builder(&fixture.url, "fixture-token")
            .project(PROJECT)
            .build()
            .unwrap();
        let error = client.create_file_download(BUCKET, FILE).await.unwrap_err();
        assert_eq!(error.status(), Some(status));
        assert_eq!(error.is_uncertain(), status != 403);
        assert!(!format!("{error:?} {error}").contains("fixture-private"));
        assert!(!String::from_utf8_lossy(error.raw_body().unwrap()).contains("fixture-private"));
        if status == 403 {
            assert_eq!(error.code(), Some("forbidden_scope"));
        }
        assert_eq!(fixture.requests.lock().unwrap().len(), 1);
    }
}
