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
async fn pending_routes_return_typed_501_and_do_not_invent_results() {
    let mock = Mock::new(vec![Response {
        status: 501,
        headers: Vec::new(),
        body: b"{\"code\":\"pending\",\"message\":\"not ready\"}".to_vec(),
    }]);
    let sdk = client(Arc::clone(&mock));
    let error = sdk
        .management()
        .collections()
        .index_recommendations("db", "orders", None)
        .await
        .expect_err("pending");
    assert_eq!(error.status, Some(501));
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
