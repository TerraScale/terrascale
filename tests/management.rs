use std::collections::VecDeque;
use std::sync::{Arc, Mutex};

use async_trait::async_trait;
use serde_json::json;
use terrascale::management::{
    CancellationToken, CreateApiKey, CreateDatabaseWithPlacement, CreateExport, OperationOptions,
    PageOptions, TransferFormat,
};
use terrascale::{
    ClientConfig, ErrorKind, HttpTransport, RegionCode, Request, Response, Result, TerraScaleClient,
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
    let transport: Arc<dyn HttpTransport> = mock;
    TerraScaleClient::with_transport(
        ClientConfig::builder()
            .api_key("test")
            .project_id("project")
            .build()
            .expect("config"),
        transport,
    )
}

#[tokio::test]
async fn pagination_and_ids_are_validated_before_transport() {
    assert_eq!(
        PageOptions::new(0, 0).expect_err("zero page").kind,
        ErrorKind::Validation
    );
    let mock = Mock::new(vec![]);
    let sdk = client(Arc::clone(&mock));
    let error = sdk
        .management()
        .databases()
        .list_page(None, PageOptions { skip: 0, take: 0 })
        .await
        .expect_err("invalid page");
    assert_eq!(error.code, "invalid_page_size");
    assert!(mock.requests.lock().expect("requests mutex").is_empty());
}

#[tokio::test]
async fn placement_lock_and_idempotency_are_wire_typed() {
    let mock = Mock::new(vec![Response::json(
        201,
        &json!({
            "database_id": "db-1", "project_id": "project", "name": "orders", "regions": ["GRU"], "version": 1
        }),
    )]);
    let sdk = client(Arc::clone(&mock));
    sdk.management()
        .databases()
        .create_with_placement(
            CreateDatabaseWithPlacement::new("orders", vec![RegionCode::Gru])
                .expect("placement")
                .residency("br-only")
                .expect("residency"),
            None,
            OperationOptions {
                idempotency_key: Some("create-once".into()),
                cancellation: None,
            },
        )
        .await
        .expect("create");
    let request = mock.last();
    assert_eq!(
        request
            .headers
            .iter()
            .find(|(name, _)| name == "idempotency-key")
            .map(|(_, value)| value.as_str()),
        Some("create-once")
    );
    assert!(String::from_utf8(request.body.expect("body"))
        .expect("utf8")
        .contains("br-only"));
}

#[tokio::test]
async fn cancellation_does_not_admit_a_management_request() {
    let mock = Mock::new(vec![]);
    let sdk = client(Arc::clone(&mock));
    let token = CancellationToken::new();
    token.cancel();
    let error = sdk
        .management()
        .exports()
        .create(
            "db-1",
            CreateExport {
                format: TransferFormat::Jsonl,
                include_history: false,
            },
            None,
            OperationOptions {
                idempotency_key: Some("export-once".into()),
                cancellation: Some(token),
            },
        )
        .await
        .expect_err("cancelled");
    assert_eq!(error.code, "cancelled");
    assert!(mock.requests.lock().expect("requests mutex").is_empty());
}

#[tokio::test]
async fn api_key_cursor_pagination_and_secret_response_are_typed() {
    let mock = Mock::new(vec![Response::json(
        200,
        &json!({
            "apiKeys": [{"keyId": "key-1", "projectId": "project", "name": "worker", "prefix": "ts_", "isActive": true}],
            "nextCursor": "cursor-2"
        }),
    )]);
    let sdk = client(Arc::clone(&mock));
    let page = sdk
        .management()
        .api_keys()
        .list(None, Some("cursor 1"), 25)
        .await
        .expect("keys");
    assert_eq!(page.api_keys[0].key_id, "key-1");
    assert_eq!(page.next_cursor.as_deref(), Some("cursor-2"));
    assert!(mock
        .last()
        .url
        .ends_with("/auth/api-keys?limit=25&cursor=cursor%201"));

    let _ = CreateApiKey {
        name: "worker".into(),
        scopes: vec!["documents:read".into()],
        database_id: None,
        environment_id: None,
        expires_at: None,
    };
}
