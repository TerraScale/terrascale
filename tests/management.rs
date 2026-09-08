use std::collections::VecDeque;
use std::sync::{Arc, Mutex};

use async_trait::async_trait;
use serde_json::json;
use terrascale::management::{
    CancellationToken, CreateDatabaseRequest, CreateIndexRequest, OperationListOptions,
    OperationOptions, PageOptions, PlacementLock, RestoreRequest,
};
use terrascale::{
    ClientConfig, HttpTransport, Method, RegionCode, Request, Response, Result, TerraScaleClient,
};

#[derive(Debug)]
struct Mock {
    requests: Mutex<Vec<Request>>,
    responses: Mutex<VecDeque<Response>>,
}

impl Mock {
    fn new(responses: Vec<Response>) -> Arc<Self> {
        Arc::new(Self {
            requests: Mutex::new(Vec::new()),
            responses: Mutex::new(responses.into()),
        })
    }
    fn last(&self) -> Request {
        self.requests
            .lock()
            .expect("requests mutex")
            .last()
            .expect("request")
            .clone()
    }
}

#[async_trait]
impl HttpTransport for Mock {
    async fn send(&self, request: Request) -> Result<Response> {
        self.requests.lock().expect("requests mutex").push(request);
        self.responses
            .lock()
            .expect("responses mutex")
            .pop_front()
            .ok_or_else(|| terrascale::Error::transport("mock exhausted"))
    }
}

fn client(mock: Arc<Mock>) -> TerraScaleClient {
    TerraScaleClient::with_transport(
        ClientConfig::builder()
            .api_key("test")
            .project_id("ignored")
            .build()
            .expect("config"),
        mock,
    )
}

#[tokio::test]
async fn databases_use_direct_contract_routes_and_metadata() {
    let mock = Mock::new(vec![
        Response::json(200, &json!({"databases": []})),
        Response::json(
            201,
            &json!({"name":"orders","placement":"GRU","residency":"br-only","deleted":false,"created_at":1,"parent":null}),
        ),
        Response::json(
            200,
            &json!({"name":"orders","placement":"GRU","residency":"br-only","deleted":false,"created_at":1,"parent":null}),
        ),
    ]);
    let sdk = client(Arc::clone(&mock));
    sdk.management().databases().list(None).await.expect("list");
    assert_eq!(
        mock.last().url,
        "https://api.terrascale.tech/api/v1/databases"
    );

    let request = CreateDatabaseRequest::new("orders", vec![RegionCode::Gru])
        .expect("placement")
        .operation_id("op")
        .expect("operation id")
        .residency("br-only")
        .expect("residency");
    sdk.management()
        .databases()
        .create_with_placement(
            request,
            None,
            OperationOptions {
                idempotency_key: Some("once".into()),
                cancellation: None,
            },
        )
        .await
        .expect("create");
    let request = mock.last();
    assert_eq!(request.method, Method::Post);
    assert_eq!(request.url, "https://api.terrascale.tech/api/v1/databases");
    assert!(request
        .headers
        .iter()
        .any(|(name, value)| name == "idempotency-key" && value == "once"));
    assert_eq!(
        serde_json::from_slice::<serde_json::Value>(&request.body.expect("body")).expect("json")
            ["placement"],
        json!(["GRU"])
    );

    sdk.management()
        .databases()
        .get("orders", None)
        .await
        .expect("get");
    assert_eq!(
        mock.last().url,
        "https://api.terrascale.tech/api/v1/databases/orders"
    );
}

#[test]
fn placement_and_residency_fail_closed_before_transport() {
    assert!(CreateDatabaseRequest::new("orders", vec![]).is_err());
    assert!(CreateDatabaseRequest::new("orders", vec![RegionCode::Gru])
        .expect("placement")
        .placement_lock(PlacementLock::EuOnly)
        .is_err());
    assert!(CreateDatabaseRequest::new("orders", vec![RegionCode::Gru, RegionCode::Gru]).is_err());
}

#[tokio::test]
async fn schema_is_one_collection_schema_and_direct_path() {
    let mock = Mock::new(vec![Response::json(
        200,
        &json!({"name":"orders","fields":[]}),
    )]);
    let sdk = client(Arc::clone(&mock));
    let schema = terrascale::CollectionSchema::new("orders");
    sdk.management()
        .schemas()
        .put("db 1", "orders", schema, None, OperationOptions::default())
        .await
        .expect("schema");
    let request = mock.last();
    assert_eq!(request.method, Method::Put);
    assert_eq!(
        request.url,
        "https://api.terrascale.tech/api/v1/databases/db%201/collections/orders/schema"
    );
    assert!(
        serde_json::from_slice::<serde_json::Value>(&request.body.expect("body"))
            .expect("json")
            .get("collections")
            .is_none()
    );
}

#[tokio::test]
async fn recommendation_source_unavailability_preserves_typed_error() {
    let mock = Mock::new(vec![Response {
        status: 503,
        headers: Vec::new(),
        body: b"{\"code\":\"index_not_ready\",\"message\":\"not ready\"}".to_vec(),
    }]);
    let sdk = client(Arc::clone(&mock));
    let error = sdk
        .management()
        .collections()
        .index_recommendations("db", "orders", None)
        .await
        .expect_err("source unavailable");
    assert_eq!(error.status, Some(503));
    assert_eq!(mock.last().url, "https://api.terrascale.tech/api/v1/databases/db/collections/orders/indexes/recommendations");
}

#[tokio::test]
async fn cancellation_does_not_admit_mutations() {
    let mock = Mock::new(vec![]);
    let sdk = client(Arc::clone(&mock));
    let token = CancellationToken::new();
    token.cancel();
    let error = sdk
        .management()
        .collections()
        .create_index(
            "db",
            "orders",
            CreateIndexRequest { field: "id".into() },
            None,
            OperationOptions {
                idempotency_key: None,
                cancellation: Some(token),
            },
        )
        .await
        .expect_err("cancelled");
    assert_eq!(error.code, "cancelled");
    assert!(mock.requests.lock().expect("requests mutex").is_empty());
}

#[tokio::test]
async fn branch_and_backup_restore_use_restore_request() {
    let record = json!({"name":"copy","placement":"GRU","residency":"br-only","deleted":false,"created_at":1,"parent":"db"});
    let mock = Mock::new(vec![
        Response::json(201, &record),
        Response::json(201, &record),
    ]);
    let sdk = client(Arc::clone(&mock));
    let body = RestoreRequest {
        name: "copy".into(),
        at: Some(10),
    };
    sdk.management()
        .branches()
        .create("db", body.clone(), None, OperationOptions::default())
        .await
        .expect("branch");
    assert_eq!(
        mock.last().url,
        "https://api.terrascale.tech/api/v1/databases/db/branches"
    );
    sdk.management()
        .backups()
        .restore("db", body, None, OperationOptions::default())
        .await
        .expect("restore");
    assert_eq!(
        mock.last().url,
        "https://api.terrascale.tech/api/v1/databases/db/backups/restore"
    );
}

#[test]
fn page_options_are_bounded() {
    assert_eq!(
        PageOptions::new(0, 0).expect_err("zero page").code,
        "invalid_page_size"
    );
    assert!(PageOptions::new(0, 1001).is_err());
}

#[tokio::test]
async fn usage_and_recommendations_preserve_authoritative_fields() {
    let mock = Mock::new(vec![
        Response::json(
            200,
            &json!({"tenant_id":"tenant","database_id":"db","snapshot_sequence":18446744073709551615u64,"storage_complete_through_millis":null,
            "meters":[{"meter":"document_write_units","units":"9007199254740993"},{"meter":"storage_byte_seconds","units":"123"}],
            "reads":{"total_reads":4}}),
        ),
        Response::json(
            200,
            &json!({"generation":9,"truncated":true,"recommendations":[{"candidate_id":"candidate","collection":"orders","fields":["status"],"score":0.75,"maintenance_cost":2,"generation":9,"needs_descriptor":true}]}),
        ),
    ]);
    let sdk = client(Arc::clone(&mock));
    let usage = sdk
        .management()
        .usage()
        .get_database("db", None)
        .await
        .unwrap();
    assert_eq!(usage.snapshot_sequence, u64::MAX);
    assert_eq!(usage.storage_complete_through_millis, None);
    assert_eq!(usage.meters[0].units, "9007199254740993");
    assert_eq!(usage.reads.total_reads, 4);
    let recommendations = sdk
        .management()
        .collections()
        .index_recommendations_with_limit("db", "orders", Some(3))
        .await
        .unwrap();
    assert_eq!(recommendations.generation, 9);
    assert!(recommendations.truncated);
    assert_eq!(recommendations.recommendations[0].fields, ["status"]);
    assert!(mock
        .last()
        .url
        .ends_with("/indexes/recommendations?limit=3"));
    let before = mock.requests.lock().unwrap().len();
    assert!(sdk
        .management()
        .collections()
        .index_recommendations_with_limit("db", "orders", Some(65))
        .await
        .is_err());
    assert_eq!(mock.requests.lock().unwrap().len(), before);
}

#[tokio::test]
async fn webhook_operations_match_endpoint_and_delivery_contracts() {
    use terrascale::CreateWebhook;
    let endpoint =
        json!({"id":"hook","endpoint":"https://example.com/hook","created_at":10,"deleted":false});
    let delivery = json!({"id":"hook/event","event_id":"event","revision":2,"write_index":1,"retry_started_at":10,"attempts":1,"next_attempt_at":0,"status":204,"delivered":true,"terminal":false});
    let mock = Mock::new(vec![
        Response::json(
            201,
            &json!({"id":"hook","endpoint":"https://example.com/hook","created_at":10,"deleted":false,"secret":"once"}),
        ),
        Response::json(200, &json!({"webhooks":[endpoint.clone()]})),
        Response::json(200, &json!({"deliveries":[delivery.clone()]})),
        Response::json(200, &delivery),
        Response::json(
            200,
            &json!({"deleted":true,"id":"hook","endpoint":"https://example.com/hook","created_at":10}),
        ),
    ]);
    let api = client(Arc::clone(&mock)).management().webhooks();
    assert_eq!(
        api.create(
            "db",
            CreateWebhook {
                endpoint: "https://example.com/hook".into()
            },
            OperationOptions::default()
        )
        .await
        .unwrap()
        .webhook
        .created_at,
        10
    );
    assert_eq!(
        serde_json::from_slice::<serde_json::Value>(&mock.last().body.unwrap()).unwrap(),
        json!({"endpoint":"https://example.com/hook"})
    );
    assert_eq!(api.list("db").await.unwrap().webhooks.len(), 1);
    assert_eq!(
        api.list_deliveries("db", "hook").await.unwrap().deliveries[0].status,
        Some(204)
    );
    assert!(
        api.replay("db", "hook", "hook/event", OperationOptions::default())
            .await
            .unwrap()
            .delivered
    );
    assert_eq!(mock.last().method, Method::Post);
    assert!(mock
        .last()
        .url
        .ends_with("/webhooks/hook/deliveries/hook%2Fevent/replay"));
    assert!(mock.last().body.is_none());
    assert!(
        api.delete("db", "hook", OperationOptions::default())
            .await
            .unwrap()
            .deleted
    );
    assert_eq!(mock.last().method, Method::Delete);
}

#[tokio::test]
async fn exports_send_identity_and_resume_cursor_and_preserve_progress() {
    use terrascale::{CreateExport, ExportJobStatus, ResumeExport};
    let status = json!({"job_id":"job","status":"Complete","created_at_millis":1,"phase":"Done","cancellation":"unavailable","pages":1,"records":5,"estimated_records":null,"progress_basis_points":10000,"resume_cursor":"next","manifest":{"path":"manifest","length":20,"checksum":"abc"},"error":null});
    let mock = Mock::new(vec![
        Response::json(
            202,
            &json!({"job_id":"job","status_path":"/api/v1/databases/db/exports/job","resume_cursor":"cursor"}),
        ),
        Response::json(200, &status),
        Response::json(200, &status),
    ]);
    let api = client(Arc::clone(&mock)).management().exports();
    assert_eq!(
        api.create(
            "db",
            CreateExport {
                idempotency_key: "stable".into()
            },
            None,
            OperationOptions::default()
        )
        .await
        .unwrap()
        .job_id,
        "job"
    );
    assert_eq!(
        serde_json::from_slice::<serde_json::Value>(&mock.last().body.unwrap()).unwrap(),
        json!({"idempotency_key":"stable"})
    );
    let result = api.get("db", "job").await.unwrap();
    assert_eq!(result.status, ExportJobStatus::Complete);
    assert_eq!(result.manifest.unwrap().length, 20);
    assert_eq!(
        api.resume(
            "db",
            "job",
            ResumeExport {
                resume_cursor: "cursor".into()
            },
            OperationOptions::default()
        )
        .await
        .unwrap()
        .progress_basis_points,
        10000
    );
    assert!(mock.last().url.ends_with("/exports/job/resume"));
    assert_eq!(
        serde_json::from_slice::<serde_json::Value>(&mock.last().body.unwrap()).unwrap(),
        json!({"resume_cursor":"cursor"})
    );
}

#[tokio::test]
async fn export_download_preserves_binary_body_and_authentication() {
    let bytes = vec![0, 0xff, b'"', b'\n'];
    let mock = Mock::new(vec![Response {
        status: 200,
        headers: vec![
            ("content-type".into(), "application/octet-stream".into()),
            (
                "x-terrascale-artifact-length".into(),
                bytes.len().to_string(),
            ),
            ("x-terrascale-artifact-checksum".into(), "A".repeat(64)),
        ],
        body: bytes.clone(),
    }]);
    let download = client(Arc::clone(&mock))
        .management()
        .exports()
        .download("db", "job", "part.bin")
        .await
        .unwrap();
    assert_eq!(download.bytes, bytes);
    assert_eq!(download.length, 4);
    assert_eq!(download.checksum, "a".repeat(64));
    let request = mock.last();
    assert_eq!(request.method, Method::Get);
    assert!(request.url.ends_with("/exports/job/artifacts/part.bin"));
    assert!(request
        .headers
        .iter()
        .any(|(name, value)| name.eq_ignore_ascii_case("authorization") && value == "Bearer test"));
    assert!(request
        .headers
        .iter()
        .any(|(name, value)| name.eq_ignore_ascii_case("accept")
            && value == "application/octet-stream"));
}

#[tokio::test]
async fn export_download_rejects_malformed_metadata_and_length_mismatch() {
    for headers in [
        vec![
            ("x-terrascale-artifact-length".into(), "03".into()),
            ("x-terrascale-artifact-checksum".into(), "0".repeat(64)),
        ],
        vec![
            ("x-terrascale-artifact-length".into(), "3, 3".into()),
            ("x-terrascale-artifact-checksum".into(), "0".repeat(64)),
        ],
        vec![
            ("x-terrascale-artifact-length".into(), "3".into()),
            ("x-terrascale-artifact-length".into(), "3".into()),
            ("x-terrascale-artifact-checksum".into(), "0".repeat(64)),
        ],
        vec![
            ("x-terrascale-artifact-length".into(), "3".into()),
            ("x-terrascale-artifact-checksum".into(), "bad".into()),
        ],
        vec![
            ("x-terrascale-artifact-length".into(), "4".into()),
            ("x-terrascale-artifact-checksum".into(), "0".repeat(64)),
        ],
    ] {
        let mock = Mock::new(vec![Response {
            status: 200,
            headers: {
                let mut all = vec![("content-type".into(), "application/octet-stream".into())];
                all.extend(headers);
                all
            },
            body: vec![1, 2, 3],
        }]);
        let result = client(Arc::clone(&mock))
            .management()
            .exports()
            .download("db", "job", "part.bin")
            .await;
        assert!(result.is_err());
        assert_eq!(
            result.unwrap_err().kind,
            terrascale::ErrorKind::InvalidResponse
        );
    }
}

#[tokio::test]
async fn provisioning_acceptance_is_distinct_from_database_creation() {
    use terrascale::CreateDatabaseResult;
    let operation = json!({"environment":"production","tenant_id":"tenant","project_id":"project","operation_id":"op","database_id":"db-id","parent_database_id":null,"key_version":"key-1","name":"orders","regions":["GRU"],"residency":"br-only","state":"Pending"});
    let mock = Mock::new(vec![
        Response::json(202, &operation),
        Response::json(202, &json!({"name":"orders"})),
        Response::json(201, &json!({"name":"orders"})),
    ]);
    let api = client(Arc::clone(&mock)).management().databases();
    let request = CreateDatabaseRequest::new("orders", vec![RegionCode::Gru])
        .unwrap()
        .residency("br-only")
        .unwrap()
        .operation_id("op")
        .unwrap();
    let result = api
        .create_with_placement(request.clone(), None, OperationOptions::default())
        .await
        .unwrap();
    let CreateDatabaseResult::Accepted(accepted) = result else {
        panic!("202 must remain accepted provisioning")
    };
    assert_eq!(accepted.operation_id, "op");
    assert_eq!(accepted.state, "Pending");
    assert_eq!(
        serde_json::from_slice::<serde_json::Value>(&mock.last().body.unwrap()).unwrap()
            ["operation_id"],
        "op"
    );
    assert!(api
        .create_with_placement(request.clone(), None, OperationOptions::default())
        .await
        .is_err());
    assert!(matches!(
        api.create_with_placement(request, None, OperationOptions::default())
            .await
            .unwrap(),
        CreateDatabaseResult::Created(_)
    ));
}

#[tokio::test]
async fn database_delete_decodes_accepted_provisioning_lifecycle() {
    use terrascale::DeleteDatabaseResult;
    let mock = Mock::new(vec![Response {
        status: 202,
        headers: vec![(
            "location".into(),
            "/api/v1/databases/orders/deletions/op".into(),
        )],
        body: serde_json::to_vec(&json!({
            "request": {"environment":"production","tenant_id":"tenant","project_id":"project","database_id":"orders","operation_id":"op"},
            "requested_at": "2026-09-06T00:00:00Z",
            "state": "preparing",
            "cutoff": "2026-09-06T00:00:00Z",
            "targets": [{"stop":{"environment":"production","organization_id":"tenant","project_id":"project","database_id":"orders","operation_id":"op","region":"GRU","cutoff":"2026-09-06T00:00:00Z"}}]
        }))
        .unwrap(),
    }]);
    let result = client(Arc::clone(&mock))
        .management()
        .databases()
        .delete("orders", true, None)
        .await
        .unwrap();
    let DeleteDatabaseResult::Accepted(accepted) = result else {
        panic!("202 must remain an accepted deletion")
    };
    assert_eq!(accepted.operation_id, "op");
    assert_eq!(accepted.state, "preparing");
    assert_eq!(accepted.location, "/api/v1/databases/orders/deletions/op");
    assert_eq!(accepted.cutoff, "2026-09-06T00:00:00Z");
    assert_eq!(accepted.targets.len(), 1);
}

#[tokio::test]
async fn database_deletion_status_reads_typed_authoritative_progress() {
    let body = json!({
        "request": {"environment":"production","tenant_id":"tenant","project_id":"project","database_id":"orders","operation_id":"op"},
        "requested_at": "2026-09-06T00:00:00Z",
        "state": "cleanup_pending",
        "cutoff": "2026-09-06T00:00:00Z",
        "targets": [{"stop":{"environment":"production","organization_id":"tenant","project_id":"project","database_id":"orders","operation_id":"op","region":"GRU","cutoff":"2026-09-06T00:00:00Z","retain_until":"2026-09-13T00:00:00Z"},"receipt":{"environment":"production","organization_id":"tenant","project_id":"project","database_id":"orders","operation_id":"op","region":"GRU","cutoff":"2026-09-06T00:00:00Z","access_revoked":true,"usage_stopped":true}}]
    });
    let mock = Mock::new(vec![Response::json(200, &body)]);
    let operation = client(Arc::clone(&mock))
        .management()
        .databases()
        .get_deletion_status("orders", "op", None)
        .await
        .expect("deletion status");
    assert_eq!(operation.state, "cleanup_pending");
    assert_eq!(operation.targets[0].stop.region, "GRU");
    assert!(operation.targets[0].receipt.as_ref().unwrap().usage_stopped);
    assert_eq!(mock.last().method, Method::Get);
    assert_eq!(
        mock.last().url,
        "https://api.terrascale.tech/api/v1/databases/orders/deletions/op"
    );
}

#[tokio::test]
async fn webhook_rotation_returns_one_time_secret_and_bounds_overlap() {
    use terrascale::RotateWebhookSecret;
    let mock = Mock::new(vec![Response::json(
        200,
        &json!({"id":"hook","endpoint":"https://example.com/hook","created_at":10,"deleted":false,"secret":"one-time-secret"}),
    )]);
    let api = client(Arc::clone(&mock)).management().webhooks();
    let response = api
        .rotate_secret(
            "db",
            "hook",
            RotateWebhookSecret::default(),
            OperationOptions::default(),
        )
        .await
        .unwrap();
    assert_eq!(response.secret, "one-time-secret");
    assert!(!format!("{response:?}").contains("one-time-secret"));
    let request = mock.last();
    assert!(request.url.ends_with("/webhooks/hook/rotate-secret"));
    assert_eq!(request.method, Method::Post);
    assert_eq!(request.body.unwrap(), b"{}");
    assert!(api
        .rotate_secret(
            "db",
            "hook",
            RotateWebhookSecret {
                overlap_seconds: Some(3601)
            },
            OperationOptions::default()
        )
        .await
        .is_err());
    assert_eq!(mock.requests.lock().unwrap().len(), 1);
}

#[tokio::test]
async fn database_create_consumes_every_producer_success_status() {
    use terrascale::CreateDatabaseResult;
    let corpus: serde_json::Value =
        serde_json::from_str(include_str!("../contracts/v1/conformance/http.json")).unwrap();
    let operation = corpus["operations"]
        .as_array()
        .unwrap()
        .iter()
        .find(|operation| operation["operationId"] == "create_database")
        .unwrap();
    let responses = operation["responses"]
        .as_array()
        .expect("all producer success responses");
    for response in responses {
        let status = response["status"].as_u64().unwrap() as u16;
        let mock = Mock::new(vec![Response::json(status, &response["body"])]);
        let request = CreateDatabaseRequest::new("orders", vec![RegionCode::Gru])
            .unwrap()
            .residency("br-only")
            .unwrap()
            .operation_id("op")
            .unwrap();
        let result = client(mock)
            .management()
            .databases()
            .create_with_placement(request, None, OperationOptions::default())
            .await
            .unwrap();
        match (status, result) {
            (201, CreateDatabaseResult::Created(database)) => {
                assert_eq!(serde_json::to_value(database).unwrap(), response["body"])
            }
            (202, CreateDatabaseResult::Accepted(operation)) => {
                assert_eq!(serde_json::to_value(operation).unwrap(), response["body"])
            }
            _ => panic!("incorrect database creation status mapping"),
        }
    }
    assert_eq!(
        responses
            .iter()
            .map(|response| response["status"].as_u64().unwrap())
            .collect::<Vec<_>>(),
        [202]
    );
}

#[tokio::test]
async fn development_operations_use_branch_confirmation_and_bounded_wire_fields() {
    use terrascale::{DevelopmentFaultKind, DevelopmentFaultRequest};
    let mock = Mock::new(vec![
        Response::json(200, &json!({"active":true,"expiresAtMillis":30000})),
        Response::json(200, &json!({"active":false,"expiresAtMillis":0})),
        Response::json(
            200,
            &json!({"entries":[{"sequence":2,"action":"fault.clear","atMillis":30001}],"truncated":false}),
        ),
    ]);
    let api = client(Arc::clone(&mock)).management().development();
    let input = DevelopmentFaultRequest {
        confirm_branch: "branch".into(),
        fault: DevelopmentFaultKind::Latency,
        duration_ms: 1000,
        max_operations: 2,
        delay_ms: 10,
    };
    assert!(
        api.set_fault("branch", input.clone(), OperationOptions::default())
            .await
            .unwrap()
            .active
    );
    assert_eq!(
        serde_json::from_slice::<serde_json::Value>(&mock.last().body.unwrap()).unwrap(),
        json!({"confirmBranch":"branch","fault":"latency","durationMs":1000,"maxOperations":2,"delayMs":10})
    );
    assert!(
        !api.clear_fault("branch", OperationOptions::default())
            .await
            .unwrap()
            .active
    );
    assert_eq!(mock.last().method, Method::Delete);
    assert_eq!(
        api.logs("branch", Some(2)).await.unwrap().entries[0].at_millis,
        30001
    );
    assert!(mock.last().url.ends_with("/dev/logs?limit=2"));
    assert!(api
        .set_fault("other", input, OperationOptions::default())
        .await
        .is_err());
    assert!(api.logs("branch", Some(101)).await.is_err());
    assert_eq!(mock.requests.lock().unwrap().len(), 3);
}

#[tokio::test]
async fn operations_poll_durable_provisioning_with_get() {
    let mock = Mock::new(vec![Response::json(
        200,
        &json!({"operation_id":"op","kind":"database_create","database_id":"db-id","name":"orders","state":"Ready","stage":"admitted","created_at":null,"revision":null,"cancellable":false,"cancellation_reason":"Provisioning has no safe cancellation action"}),
    )]);
    let operation = client(Arc::clone(&mock))
        .management()
        .operations()
        .get("op")
        .await
        .unwrap();
    assert_eq!(operation.state, "Ready");
    assert_eq!(mock.last().method, Method::Get);
    assert!(mock.last().url.ends_with("/api/v1/operations/op"));
    assert!(mock.last().body.is_none());
}

#[tokio::test]
async fn operations_list_is_project_scoped_paged_and_preserves_u64_revision() {
    let mock = Mock::new(vec![Response::json(
        200,
        &json!({
            "operations": [{
                "operation_id": "op/one",
                "kind": "database_create",
                "database_id": "db-1",
                "name": "demo",
                "state": "running",
                "stage": "admitted",
                "created_at": "2026-09-06T12:00:00Z",
                "revision": u64::MAX,
                "cancellable": false,
                "cancellation_reason": "Provisioning has no safe cancellation action"
            }],
            "next_cursor": "opaque",
            "covered_kinds": ["database_create", "branch_create"]
        }),
    )]);
    let sdk = client(Arc::clone(&mock));
    let page = sdk
        .management()
        .operations()
        .list(Some("cursor previous"), Some(50))
        .await
        .expect("operation page");
    assert_eq!(page.operations[0].revision, Some(u64::MAX));
    assert_eq!(page.next_cursor.as_deref(), Some("opaque"));
    assert_eq!(page.covered_kinds, ["database_create", "branch_create"]);
    assert_eq!(mock.last().method, Method::Get);
    assert_eq!(
        mock.last().url,
        "https://api.terrascale.tech/api/v1/operations?after=cursor%20previous&limit=50"
    );
    assert!(mock
        .last()
        .headers
        .iter()
        .any(|(name, value)| { name == "x-terrascale-project-id" && value == "ignored" }));
}

#[tokio::test]
async fn operations_list_accepts_missing_optional_journal_facts() {
    let mock = Mock::new(vec![Response::json(
        200,
        &json!({
            "operations": [{
                "operation_id": "op-1",
                "kind": "branch_create",
                "database_id": "db-1",
                "name": "copy",
                "state": "queued",
                "stage": "accepted",
                "cancellable": false,
                "cancellation_reason": "Provisioning has no safe cancellation action"
            }],
            "covered_kinds": ["database_create", "branch_create"]
        }),
    )]);
    let page = client(Arc::clone(&mock))
        .management()
        .operations()
        .list(None, None)
        .await
        .expect("operation page");
    assert_eq!(page.operations[0].created_at, None);
    assert_eq!(page.operations[0].revision, None);
}

#[tokio::test]
async fn operations_list_rejects_unbounded_limit_and_pre_cancel() {
    let mock = Mock::new(vec![]);
    let sdk = client(Arc::clone(&mock));
    assert!(sdk
        .management()
        .operations()
        .list(None, Some(51))
        .await
        .expect_err("limit")
        .code
        .eq("invalid_limit"));
    let token = CancellationToken::new();
    token.cancel();
    let error = sdk
        .management()
        .operations()
        .list_with_options(
            None,
            None,
            OperationListOptions {
                cancellation: Some(token),
            },
        )
        .await
        .expect_err("cancelled");
    assert_eq!(error.code, "cancelled");
    assert!(mock.requests.lock().expect("requests mutex").is_empty());
}

#[tokio::test]
async fn scope_selectors_and_put_create_only_use_canonical_headers() {
    use terrascale::{DatabaseScope, DocumentOperationOptions};
    let mock = Mock::new(vec![Response::json(200, &json!({"revision":"1"}))]);
    let sdk = TerraScaleClient::with_transport(
        ClientConfig::builder()
            .api_key("test")
            .tenant_id("tenant")
            .project_id("project")
            .build()
            .unwrap(),
        mock.clone(),
    );
    let scope = DatabaseScope::new("db");
    let options = DocumentOperationOptions::default().with_create_only();
    sdk.documents()
        .put_with_options(
            &scope,
            "orders",
            "one",
            json!({"status":"paid"}),
            options.clone(),
        )
        .await
        .unwrap();
    let request = mock.last();
    for (name, value) in [
        ("x-terrascale-tenant-id", "tenant"),
        ("x-terrascale-project-id", "project"),
        ("if-none-match", "*"),
    ] {
        assert_eq!(
            request
                .headers
                .iter()
                .filter(|(header, _)| header.eq_ignore_ascii_case(name))
                .map(|(_, value)| value.as_str())
                .collect::<Vec<_>>(),
            [value]
        );
    }
    assert!(sdk
        .documents()
        .put_with_options(
            &scope,
            "orders",
            "one",
            json!({}),
            options.clone().with_if_match("etag")
        )
        .await
        .is_err());
    assert!(sdk
        .documents()
        .patch_with_options(&scope, "orders", "one", json!({}), options.clone())
        .await
        .is_err());
    assert!(sdk
        .documents()
        .delete_with_options(&scope, "orders", "one", options)
        .await
        .is_err());
    assert_eq!(mock.requests.lock().unwrap().len(), 1);
    assert!(ClientConfig::builder()
        .tenant_id("bad\nheader")
        .build()
        .is_err());
}

#[tokio::test]
async fn webhook_secret_rotation_never_retries_while_create_requires_stable_identity() {
    use terrascale::{CreateWebhook, RotateWebhookSecret};
    let unavailable = Response::json(
        503,
        &json!({"error":{"code":"unavailable","message":"retry later"}}),
    );
    let mock = Mock::new(vec![
        unavailable.clone(),
        unavailable.clone(),
        unavailable,
        Response::json(
            201,
            &json!({"id":"hook","endpoint":"https://example.com","created_at":0,"deleted":false,"secret":"stable-secret"}),
        ),
    ]);
    let sdk = TerraScaleClient::with_transport(
        ClientConfig::builder()
            .max_retry_attempts(1)
            .retry_delay_ms(0)
            .build()
            .unwrap(),
        mock.clone(),
    );
    let api = sdk.management().webhooks();
    assert!(api
        .rotate_secret(
            "db",
            "hook",
            RotateWebhookSecret::default(),
            OperationOptions {
                idempotency_key: Some("rotation".into()),
                cancellation: None
            }
        )
        .await
        .is_err());
    assert_eq!(mock.requests.lock().unwrap().len(), 1);
    assert!(api
        .create(
            "db",
            CreateWebhook {
                endpoint: "https://example.com".into()
            },
            OperationOptions::default()
        )
        .await
        .is_err());
    assert_eq!(mock.requests.lock().unwrap().len(), 2);
    assert_eq!(
        api.create(
            "db",
            CreateWebhook {
                endpoint: "https://example.com".into()
            },
            OperationOptions {
                idempotency_key: Some("create".into()),
                cancellation: None
            }
        )
        .await
        .unwrap()
        .secret,
        "stable-secret"
    );
    assert_eq!(mock.requests.lock().unwrap().len(), 4);
}

#[tokio::test]
async fn exports_discover_and_cancel_with_durable_progress() {
    use terrascale::{ExportCancellation, ExportJobStatus};
    let status = json!({"job_id":"job","status":"Cancelled","created_at_millis":1,"phase":"Documents","cancellation":"complete","pages":2,"records":5,"estimated_records":null,"progress_basis_points":1000,"resume_cursor":"resume","manifest":null,"error":null});
    let mock = Mock::new(vec![
        Response::json(
            200,
            &json!({"exports":[status.clone()],"next_cursor":"job"}),
        ),
        Response::json(200, &status),
    ]);
    let api = client(Arc::clone(&mock)).management().exports();
    let list = api.list("db", Some("job previous"), Some(2)).await.unwrap();
    assert_eq!(list.next_cursor.as_deref(), Some("job"));
    assert_eq!(list.exports[0].pages, 2);
    assert!(mock
        .last()
        .url
        .ends_with("/exports?after=job%20previous&limit=2"));
    let cancelled = api
        .cancel("db", "job", OperationOptions::default())
        .await
        .unwrap();
    assert_eq!(cancelled.status, ExportJobStatus::Cancelled);
    assert_eq!(cancelled.cancellation, ExportCancellation::Complete);
    assert_eq!(mock.last().method, Method::Post);
    assert!(mock.last().url.ends_with("/exports/job/cancel"));
    assert!(mock.last().body.is_none());
    assert!(api.list("db", None, Some(51)).await.is_err());
    assert_eq!(mock.requests.lock().unwrap().len(), 2);
}

#[tokio::test]
async fn auth_persistence_uses_public_named_operations_and_stable_identity() {
    use terrascale::*;
    let response = json!({"record":null,"matched":true,"records":[{"id":"one"}],"transactionId":"tx","expiresAtMs":123});
    let mock = Mock::new((0..9).map(|_| Response::json(200, &response)).collect());
    let api = client(Arc::clone(&mock)).management().auth_persistence();
    let options = AuthOperationOptions {
        idempotency_key: "stable".into(),
        transaction_id: Some("tx".into()),
    };
    let empty = AuthRecord::new();
    assert!(
        api.insert_unique(
            "db",
            AuthUniqueInsert {
                model: "user".into(),
                record: empty.clone()
            },
            options.clone()
        )
        .await
        .unwrap()
        .matched
    );
    assert!(mock.last().url.ends_with("/auth/insertUnique"));
    api.compare_and_swap(
        "db",
        AuthConditionalUpdate {
            model: "user".into(),
            id: "one".into(),
            expected: empty.clone(),
            update: empty.clone(),
        },
        options.clone(),
    )
    .await
    .unwrap();
    assert!(mock.last().url.ends_with("/auth/compareAndSwap"));
    api.consume_one(
        "db",
        AuthConsume {
            model: "user".into(),
            id: "one".into(),
            expected: empty,
        },
        options.clone(),
    )
    .await
    .unwrap();
    api.index(
        "db",
        AuthUniqueIndex {
            model: "user".into(),
            fields: vec!["email".into()],
        },
        options.clone(),
    )
    .await
    .unwrap();
    let page = api
        .find_page(
            "db",
            AuthPageQuery {
                model: "user".into(),
                predicates: vec![AuthPredicate {
                    field: "email".into(),
                    value: json!("a@example.com"),
                    operator: AuthOperator::Eq,
                    connector: AuthConnector::AND,
                    mode: AuthMode::Insensitive,
                }],
                offset: 0,
                limit: 10,
            },
            options.clone(),
        )
        .await
        .unwrap();
    assert_eq!(page.records.unwrap()[0]["id"], "one");
    let request = mock.last();
    assert!(request
        .headers
        .iter()
        .any(|(name, value)| name == "idempotency-key" && value == "stable"));
    assert!(request
        .headers
        .iter()
        .any(|(name, value)| name == "x-terrascale-auth-transaction" && value == "tx"));
    assert_eq!(
        serde_json::from_slice::<serde_json::Value>(&request.body.unwrap()).unwrap()["predicates"]
            [0]["mode"],
        "insensitive"
    );
    api.find_one("db", "user", "one").await.unwrap();
    assert_eq!(mock.last().method, Method::Get);
    assert!(mock.last().url.ends_with("/auth/records/user/one"));
    let identity = AuthTransactionIdentity {
        transaction_id: "tx".into(),
    };
    let result = api
        .begin_transaction("db", identity.clone(), options.clone())
        .await
        .unwrap();
    assert_eq!(result.transaction_id.as_deref(), Some("tx"));
    assert_eq!(result.expires_at_ms, Some(123));
    api.commit_transaction("db", identity.clone(), options.clone())
        .await
        .unwrap();
    assert!(mock.last().url.ends_with("/auth/transactions/commit"));
    api.rollback_transaction("db", identity, options)
        .await
        .unwrap();
    assert_eq!(
        serde_json::from_slice::<serde_json::Value>(&mock.last().body.unwrap()).unwrap(),
        json!({"transactionId":"tx"})
    );
    assert!(api
        .index(
            "db",
            AuthUniqueIndex {
                model: "user".into(),
                fields: vec![]
            },
            AuthOperationOptions {
                idempotency_key: "".into(),
                transaction_id: None
            }
        )
        .await
        .is_err());
    assert_eq!(mock.requests.lock().unwrap().len(), 9);
}
