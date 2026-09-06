use std::collections::VecDeque;
use std::sync::{Arc, Mutex};

use async_trait::async_trait;
use serde_json::json;
use terrascale::management::{
    CancellationToken, CreateDatabaseRequest, CreateIndexRequest, OperationOptions, PageOptions,
    PlacementLock, RestoreRequest,
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
            &json!({"tenant_id":"tenant","database_id":"db","snapshot_sequence":18446744073709551615u64,
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
    let delivery = json!({"id":"hook/event","event_id":"event","revision":2,"retry_started_at":10,"attempts":1,"next_attempt_at":0,"status":204,"delivered":true,"terminal":false});
    let mock = Mock::new(vec![
        Response::json(201, &endpoint),
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
    let status = json!({"job_id":"job","status":"Complete","records":5,"estimated_records":null,"progress_basis_points":10000,"resume_cursor":"next","manifest":{"path":"manifest","length":20,"checksum":"abc"},"error":null});
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
        headers: vec![("content-type".into(), "application/octet-stream".into())],
        body: bytes.clone(),
    }]);
    assert_eq!(
        client(Arc::clone(&mock))
            .management()
            .exports()
            .download("db", "job", "part.bin")
            .await
            .unwrap(),
        bytes
    );
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
