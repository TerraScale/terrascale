#[path = "support/project_http.rs"]
mod http;
use http::{reply, Fixture, PROJECT};
use serde_json::{json, Value};
const JOB: &str = include_str!("fixtures/current-import.json");
const PREVIEW: &str = "01ARZ3NDEKTSV4RRFFQ69G5FAA";

#[tokio::test]
async fn imports_keep_complete_job_fields_and_upload_raw_bytes() {
    let fixture = Fixture::new(
        [201, 200, 200, 202, 200, 202, 202, 202]
            .into_iter()
            .map(|status| reply(status, JOB))
            .collect(),
    )
    .await;
    let db = fixture.client().database("orders");
    let create = json!({"format":"postgres_plain","sourceBytes":8165,"sourceSha256":"9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08","options":{"missingPrimaryKey":"reject","exclude":[]}});
    let options = json!({"options":{"missingPrimaryKey":"generate","exclude":["public.audit"]}});
    let bytes = b"COPY items FROM stdin;\n1\tAda\n\\.\n\x00\xff";
    let expected: Value = serde_json::from_str(JOB).unwrap();
    for job in [
        db.create_import::<Value>(&create).await.unwrap(),
        db.create_import::<Value>(&create).await.unwrap(),
        db.get_import::<Value>().await.unwrap(),
        db.update_import::<Value>(&options).await.unwrap(),
        db.upload_import_source::<Value>(42, bytes).await.unwrap(),
        db.start_import::<Value>(PREVIEW).await.unwrap(),
        db.cancel_import::<Value>().await.unwrap(),
        db.resume_import::<Value>().await.unwrap(),
    ] {
        assert_eq!(job, expected);
        assert_eq!(
            job["preview"]["tables"][0]["constraints"][0]["enforced"],
            false
        );
        assert_eq!(job["source"]["receivedBytes"], 8165);
    }
    let requests = fixture.requests.lock().unwrap();
    for (i, method, suffix) in [
        (0, "POST", ""),
        (1, "POST", ""),
        (2, "GET", ""),
        (3, "PATCH", ""),
        (4, "PUT", "/source?offset=42"),
        (5, "POST", "/start"),
        (6, "POST", "/cancel"),
        (7, "POST", "/resume"),
    ] {
        assert_eq!(requests[i].method, method);
        assert_eq!(
            requests[i].uri,
            format!("/v1/projects/{PROJECT}/databases/orders/import{suffix}")
        );
        assert_eq!(requests[i].headers["authorization"], "Bearer fixture-token");
        assert!(!requests[i].headers.contains_key("idempotency-key"));
    }
    assert_eq!(
        serde_json::from_slice::<Value>(&requests[0].body).unwrap(),
        create
    );
    assert_eq!(
        serde_json::from_slice::<Value>(&requests[3].body).unwrap(),
        options
    );
    assert_eq!(
        serde_json::from_slice::<Value>(&requests[5].body).unwrap(),
        json!({"previewId":PREVIEW})
    );
    assert_eq!(requests[4].body, bytes);
    assert_eq!(
        requests[4].headers["content-type"],
        "application/octet-stream"
    );
    assert_eq!(
        requests[4].headers["content-length"].to_str().unwrap(),
        bytes.len().to_string()
    );
    assert!(requests[6].body.is_empty());
    assert!(requests[7].body.is_empty());
}

#[tokio::test]
async fn import_errors_and_upload_limits_keep_uncertain_mutations_single_attempt() {
    for (status, code) in [
        (403, "forbidden_scope"),
        (409, "offset_mismatch"),
        (413, "payload_too_large"),
        (421, "wrong_region"),
        (503, "write_outcome_unknown"),
    ] {
        let fixture = Fixture::new(vec![reply(
            status,
            &json!({"error":{"code":code,"message":"fixture refusal"}}).to_string(),
        )])
        .await;
        let db = fixture.client().database("orders");
        assert!(db.upload_import_source::<Value>(0, b"").await.is_err());
        assert!(db
            .upload_import_source::<Value>(u64::MAX, b"x")
            .await
            .is_err());
        assert!(db
            .upload_import_source::<Value>(0, &vec![0; 4194305])
            .await
            .is_err());
        assert!(db.start_import::<Value>("preview_prefixed").await.is_err());
        let error = db
            .upload_import_source::<Value>(0, b"source")
            .await
            .unwrap_err();
        assert_eq!(error.status(), Some(status));
        assert_eq!(error.code(), Some(code));
        assert_eq!(error.request_id(), Some("parity-fixture"));
        assert_eq!(error.is_uncertain(), status == 503);
        assert_eq!(fixture.requests.lock().unwrap().len(), 1);
    }
}

#[tokio::test]
async fn import_responses_preserve_non_null_progress_validation_and_errors() {
    let mut expected: Value = serde_json::from_str(JOB).unwrap();
    expected["state"] = json!("failed");
    expected["cancelRequested"] = json!(true);
    expected["confirmedPreviewId"] = json!(PREVIEW);
    expected["source"]["retainedUntil"] = json!("2026-10-23T00:00:00Z");
    expected["startedAt"] = json!("2026-10-22T00:00:00Z");
    expected["finishedAt"] = json!("2026-10-22T00:00:30Z");
    expected["progress"] = json!({"tablesDone":0,"tablesTotal":1,"rowsWritten":2,"rowsTotal":3,"currentTable":"public.orgs"});
    expected["validation"] = json!({"passed":false,"tables":[{"source":"public.orgs","collection":"orgs","sourceRows":3,"documents":2,"sourceChecksum":"a".repeat(64),"documentChecksum":"b".repeat(64),"match":false}]});
    expected["error"] = json!({"code":"validation_failed","message":"read-back mismatch","retryable":true,"table":"public.orgs","row":3,"column":"name"});
    expected["preview"]["tables"][0]["issues"] = json!([{"severity":"warning","code":"excluded_column","column":"name","row":3,"message":"fixture issue"}]);
    let fixture = Fixture::new(vec![reply(200, &expected.to_string())]).await;
    let actual: Value = fixture
        .client()
        .database("orders")
        .get_import()
        .await
        .unwrap();
    assert_eq!(actual, expected);
    assert_eq!(actual["progress"]["rowsWritten"], 2);
    assert_eq!(actual["validation"]["tables"][0]["documents"], 2);
    assert_eq!(actual["error"]["column"], "name");
    assert_eq!(fixture.requests.lock().unwrap().len(), 1);
}
