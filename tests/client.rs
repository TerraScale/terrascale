use std::collections::VecDeque;
use std::sync::{Arc, Mutex};

use async_trait::async_trait;
use serde_json::json;
use terrascale::{
    ClientConfig, DatabaseScope, FileUploadOptions, HttpTransport, Method, Request, Response,
    Result, TerraScaleClient,
};

#[derive(Debug)]
struct Mock {
    requests: Mutex<Vec<Request>>,
    responses: Mutex<VecDeque<Response>>,
}

#[tokio::test]
async fn files_use_database_routes_and_keep_uploads_non_replayable() {
    let metadata = json!({
        "id": "01JFILE",
        "length": 2,
        "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        "createdAt": "2026-09-21T00:00:00Z",
        "contentType": "text/plain",
        "fileName": "hello.txt"
    });
    let mock = Mock::new([
        Response::json(201, &metadata),
        Response::json(200, &json!({ "files": [metadata] })),
        Response::json(200, &metadata),
        Response {
            status: 200,
            headers: vec![("content-type".into(), "text/plain".into())],
            body: bytes::Bytes::from_static(b"hi"),
        },
    ]);
    let sdk = TerraScaleClient::with_transport(
        ClientConfig::builder()
            .api_base_url("https://api.test")
            .build()
            .expect("config"),
        Arc::clone(&mock) as Arc<dyn HttpTransport>,
    );
    let scope = DatabaseScope::new("db with space");
    let options = FileUploadOptions::new()
        .with_content_type("text/plain")
        .with_file_name("hello.txt");
    sdk.files()
        .upload(&scope, b"hi", options)
        .await
        .expect("upload");
    sdk.files().list(&scope).await.expect("list");
    sdk.files()
        .metadata(&scope, "01JFILE")
        .await
        .expect("metadata");
    let body = sdk
        .files()
        .download(&scope, "01JFILE")
        .await
        .expect("download");
    assert_eq!(body, b"hi");

    let requests = mock.requests.lock().expect("request mutex");
    assert_eq!(requests[0].method, Method::Post);
    assert_eq!(
        requests[0].url,
        "https://api.test/api/v1/databases/db%20with%20space/files?fileName=hello.txt"
    );
    assert_eq!(requests[0].body.as_deref(), Some(b"hi".as_slice()));
    assert!(!requests[0]
        .headers
        .iter()
        .any(|(name, _)| name.eq_ignore_ascii_case("idempotency-key")));
    assert_eq!(
        requests[3].url,
        "https://api.test/api/v1/databases/db%20with%20space/files/01JFILE"
    );
}

impl Mock {
    fn new(responses: impl IntoIterator<Item = Response>) -> Arc<Self> {
        Arc::new(Self {
            requests: Mutex::new(Vec::new()),
            responses: Mutex::new(responses.into_iter().collect()),
        })
    }
}

#[async_trait]
impl HttpTransport for Mock {
    async fn send(&self, request: Request) -> Result<Response> {
        self.requests.lock().expect("request mutex").push(request);
        self.responses
            .lock()
            .expect("response mutex")
            .pop_front()
            .ok_or_else(|| terrascale::Error::transport("mock ran out of responses"))
    }
}

fn document_response() -> Response {
    Response::json(200, &json!({"revision": "rev_1"}))
}

#[tokio::test]
async fn api_key_is_normalized_once_before_authorization_header_is_added() {
    for (configured, expected) in [
        ("raw-key", "Bearer raw-key"),
        ("Bearer prefixed-key", "Bearer prefixed-key"),
        ("bEaReR   spaced-key", "Bearer spaced-key"),
        (
            "BEARER\u{2003}unicode-space-key",
            "Bearer unicode-space-key",
        ),
    ] {
        let mock = Mock::new([document_response()]);
        let sdk = TerraScaleClient::with_transport(
            ClientConfig::builder()
                .api_base_url("https://api.test")
                .api_key(configured)
                .build()
                .expect("config"),
            Arc::clone(&mock) as Arc<dyn HttpTransport>,
        );
        sdk.documents()
            .get(&DatabaseScope::new("database"), "users", "doc")
            .await
            .expect("get");
        let requests = mock.requests.lock().expect("request mutex");
        assert_eq!(
            requests[0]
                .headers
                .iter()
                .find(|(name, _)| name.eq_ignore_ascii_case("authorization"))
                .map(|(_, value)| value.as_str()),
            Some(expected)
        );
    }
}

#[test]
fn api_key_rejects_empty_whitespace_and_control_characters() {
    for configured in [
        "",
        "   ",
        "Bearer",
        "Bearer   ",
        "key value",
        "key\u{2003}value",
        "key\nvalue",
        "key\rvalue",
    ] {
        let error = ClientConfig::builder()
            .api_key(configured)
            .build()
            .expect_err("invalid API key");
        assert_eq!(error.kind, terrascale::ErrorKind::Validation);
        assert_eq!(error.code, "invalid_api_key");
    }
}

#[test]
fn api_key_normalization_handles_multibyte_utf8_at_prefix_boundary() {
    ClientConfig::builder()
        .api_key("ééé")
        .build()
        .expect("non-control UTF-8 credential");
}

#[tokio::test]
async fn documents_propagate_authoritative_etag_and_require_json_success_media() {
    let get = Response {
        status: 200,
        headers: vec![
            ("content-type".into(), "application/json".into()),
            ("etag".into(), "\"served-get\"".into()),
        ],
        body: br#"{"name":"Ada"}"#.to_vec().into(),
    };
    let put = Response {
        status: 200,
        headers: vec![
            (
                "content-type".into(),
                "application/json; charset=utf-8".into(),
            ),
            ("etag".into(), "\"served-put\"".into()),
        ],
        body: br#"{"revision":"revision-body"}"#.to_vec().into(),
    };
    let wrong_media = Response {
        status: 200,
        headers: vec![("content-type".into(), "text/plain".into())],
        body: br#"{"name":"Ada"}"#.to_vec().into(),
    };
    let mock = Mock::new([get, put, wrong_media]);
    let sdk = TerraScaleClient::with_transport(
        ClientConfig::builder()
            .api_base_url("https://api.test")
            .build()
            .expect("config"),
        Arc::clone(&mock) as Arc<dyn HttpTransport>,
    );
    let scope = DatabaseScope::new("database");
    let fetched = sdk
        .documents()
        .get(&scope, "users", "doc")
        .await
        .expect("get");
    assert_eq!(fetched.etag.as_deref(), Some("\"served-get\""));
    let written = sdk
        .documents()
        .put(&scope, "users", "doc", json!({"name": "Ada"}))
        .await
        .expect("put");
    assert_eq!(written.revision, "revision-body");
    assert_eq!(written.etag.as_deref(), Some("\"served-put\""));
    let error = sdk
        .documents()
        .get(&scope, "users", "doc")
        .await
        .expect_err("wrong media type");
    assert_eq!(error.kind, terrascale::ErrorKind::InvalidResponse);
}

#[tokio::test]
async fn caller_idempotency_key_is_reused_by_every_shared_client_handle() {
    let mock = Mock::new([document_response(), document_response()]);
    let sdk = TerraScaleClient::with_transport(
        ClientConfig::builder()
            .api_base_url("https://api.test")
            .project_id("project")
            .build()
            .expect("config"),
        Arc::clone(&mock) as Arc<dyn HttpTransport>,
    )
    .with_idempotency_key("logical-write-1")
    .expect("idempotency key");

    let scope = terrascale::DatabaseScope::new("database");
    sdk.documents()
        .put(&scope, "users", "doc_1", json!({"name": "Ada"}))
        .await
        .expect("put");
    sdk.documents()
        .put(&scope, "users", "doc_1", json!({"name": "Ada"}))
        .await
        .expect("put");

    let requests = mock.requests.lock().expect("request mutex");
    assert_eq!(requests.len(), 2);
    for request in requests.iter() {
        assert_eq!(request.method, Method::Put);
        assert_eq!(
            request
                .headers
                .iter()
                .find(|(name, _)| name.eq_ignore_ascii_case("idempotency-key"))
                .map(|(_, value)| value.as_str()),
            Some("logical-write-1")
        );
    }
    assert_eq!(sdk.idempotency_key().as_deref(), Some("logical-write-1"));
}

#[tokio::test]
async fn database_routes_are_direct_and_session_header_is_canonical() {
    let first = {
        let mut response = document_response();
        response
            .headers
            .push(("X-TerraScale-Session".to_owned(), "frontier-1".to_owned()));
        response
    };
    let mock = Mock::new([first, document_response()]);
    let sdk = TerraScaleClient::with_transport(
        ClientConfig::builder()
            .api_base_url("https://api.test")
            .project_id("ignored-project")
            .build()
            .expect("config"),
        Arc::clone(&mock) as Arc<dyn HttpTransport>,
    );
    let scope = terrascale::DatabaseScope::new("database/name");
    sdk.documents()
        .get(&scope, "users", "doc_1")
        .await
        .expect("first get");
    sdk.documents()
        .get(&scope, "users", "doc_1")
        .await
        .expect("second get");

    let requests = mock.requests.lock().expect("request mutex");
    assert!(requests[0]
        .url
        .contains("/api/v1/databases/database%2Fname/collections/users/documents/doc_1"));
    assert_eq!(
        requests[1]
            .headers
            .iter()
            .find(|(name, _)| name == terrascale::SESSION_HEADER)
            .map(|(_, value)| value.as_str()),
        Some("frontier-1")
    );
}

#[tokio::test]
async fn retries_are_bounded_and_mutations_need_a_stable_key() {
    let read_mock = Mock::new([
        Response::json(503, &json!({"code": "busy"})),
        document_response(),
    ]);
    let read_sdk = TerraScaleClient::with_transport(
        ClientConfig::builder()
            .api_base_url("https://api.test")
            .max_retry_attempts(1)
            .retry_delay_ms(0)
            .build()
            .expect("config"),
        Arc::clone(&read_mock) as Arc<dyn HttpTransport>,
    );
    read_sdk
        .documents()
        .get(
            &terrascale::DatabaseScope::new("database"),
            "users",
            "doc_1",
        )
        .await
        .expect("read retry");
    assert_eq!(read_mock.requests.lock().expect("request mutex").len(), 2);

    let mutation_mock = Mock::new([
        Response::json(503, &json!({"code": "busy"})),
        document_response(),
    ]);
    let mutation_sdk = TerraScaleClient::with_transport(
        ClientConfig::builder()
            .api_base_url("https://api.test")
            .max_retry_attempts(1)
            .retry_delay_ms(0)
            .build()
            .expect("config"),
        Arc::clone(&mutation_mock) as Arc<dyn HttpTransport>,
    );
    assert!(mutation_sdk
        .documents()
        .put(
            &terrascale::DatabaseScope::new("database"),
            "users",
            "doc_1",
            json!({"name": "Ada"}),
        )
        .await
        .is_err());
    assert_eq!(
        mutation_mock.requests.lock().expect("request mutex").len(),
        1
    );
}

#[tokio::test]
async fn strict_custom_origin_keeps_region_hint_on_one_wire_header() {
    let mock = Mock::new([document_response()]);
    let transport: Arc<dyn HttpTransport> = mock.clone();
    let sdk = TerraScaleClient::with_transport(
        ClientConfig::builder()
            .api_base_url("https://api.test")
            .api_base_url("https://origin.example.test")
            .preferred_region(terrascale::RegionCode::Sfo)
            .region_affinity(terrascale::RegionAffinity::Strict)
            .build()
            .expect("config"),
        transport,
    );
    sdk.documents()
        .get(
            &terrascale::DatabaseScope::new("database"),
            "users",
            "doc_1",
        )
        .await
        .expect("get");
    let request = mock.requests.lock().expect("request mutex")[0].clone();
    assert!(request.url.starts_with("https://origin.example.test"));
    assert_eq!(
        request
            .headers
            .iter()
            .filter(|(name, _)| name.eq_ignore_ascii_case("x-terrascale-preferred-region"))
            .count(),
        1
    );
}

#[tokio::test]
async fn nested_error_envelope_preserves_code_message_and_fix() {
    let mock = Mock::new([Response::json(
        422,
        &json!({
            "error": {
                "code": "invalid_query",
                "message": "The query is not subscribable.",
                "fix": "Use execute() for joins."
            }
        }),
    )]);
    let sdk = TerraScaleClient::with_transport(
        ClientConfig::builder()
            .api_base_url("https://api.test")
            .build()
            .expect("config"),
        Arc::clone(&mock) as Arc<dyn HttpTransport>,
    );
    let error = sdk
        .documents()
        .get(
            &terrascale::DatabaseScope::new("database"),
            "users",
            "doc_1",
        )
        .await
        .expect_err("nested error");
    assert_eq!(error.code, "invalid_query");
    assert_eq!(
        error.message,
        "The query is not subscribable. Fix: Use execute() for joins."
    );
    assert_eq!(error.fix.as_deref(), Some("Use execute() for joins."));
}

#[tokio::test]
async fn not_modified_is_an_empty_reactive_poll_only() {
    let mock = Mock::new([Response {
        status: 304,
        headers: vec![("cache-control".to_owned(), "no-store".to_owned())],
        body: Vec::new().into(),
    }]);
    let sdk = TerraScaleClient::with_transport(
        ClientConfig::builder()
            .api_base_url("https://api.test")
            .build()
            .expect("config"),
        Arc::clone(&mock) as Arc<dyn HttpTransport>,
    );
    let view = terrascale::AttachedView {
        view_id: "view-1".to_owned(),
        generation: 1,
        snapshot: terrascale::ViewSnapshot {
            view_generation: 1,
            authorization_generation: 1,
            revision: "r1".to_owned(),
            items: Vec::new(),
            digest: "digest-r1".to_owned(),
            checkpoint: terrascale::Checkpoint::new(vec![1]),
        },
        checkpoint: terrascale::Checkpoint::new(vec![1]),
        snapshot_urls: Vec::new(),
        log_url: String::new(),
        capability: Some("capability".to_owned()),
    };
    let response = sdk
        .reactive()
        .poll_from(&view, "r1")
        .await
        .expect("304 is an empty poll");
    assert!(response.deltas.is_empty());

    let ordinary = Mock::new([Response {
        status: 304,
        headers: Vec::new(),
        body: Vec::new().into(),
    }]);
    let ordinary_sdk = TerraScaleClient::with_transport(
        ClientConfig::builder()
            .api_base_url("https://api.test")
            .build()
            .expect("config"),
        Arc::clone(&ordinary) as Arc<dyn HttpTransport>,
    );
    let error = ordinary_sdk
        .documents()
        .get(
            &terrascale::DatabaseScope::new("database"),
            "users",
            "doc_1",
        )
        .await
        .expect_err("304 is not ordinary success");
    assert_eq!(error.status, Some(304));
}

#[test]
fn idempotency_key_validation_is_fail_closed() {
    let mock = Mock::new([]);
    let sdk = TerraScaleClient::with_transport(
        ClientConfig::builder().build().expect("config"),
        Arc::clone(&mock) as Arc<dyn HttpTransport>,
    );

    assert!(sdk.clone().with_idempotency_key("   ").is_err());
    assert!(sdk
        .clone()
        .with_idempotency_key("x".repeat(terrascale::MAX_IDEMPOTENCY_KEY_LENGTH + 1))
        .is_err());
    assert_eq!(sdk.idempotency_key(), None);
}

#[test]
fn root_reexports_all_published_client_types() {
    use terrascale::{
        ApiKey, ApiKeyPage, ApiKeys, Backup, BackupPlan, BackupPolicy, Backups, Branches,
        CancellationToken, Collections, CollectionsResponse, CreateApiKey, CreateBranch,
        CreateDatabaseRequest, CreateExport, CreateIndexRequest, CreateTenant, DatabaseBranch,
        DatabaseListResponse, DatabaseRecord, DatabaseSchema, Databases, DeletedDatabase,
        ExecuteBackupRequest, ExportStatus, Exports, HealthResponse, IndexCreatedResponse,
        IndexDescriptor, IndexListResponse, LiveSubscription, ManagementCancellationToken,
        OperationOptions, PageOptions, PlacementLock, PlanBackupRequest, PollOptions, PollOutcome,
        PollResponse, RegionCatalogResponse, RegionDetails, ResetFrame, RestoreAt, RestoreJob,
        RestorePoint, RestoreRequest, Schemas, SqlParameters, StartRestore, Tenant, TenantApiKey,
        TenantBranch, TenantListOptions, TenantOperationOptions, TenantPage, TenantPlacementLock,
        TenantTemplate, TenantsClient, TransferFormat, Usage, UsageClient, UsagePoint, UsageRange,
        UsageSummary, UsageWorkload,
    };

    type PublishedClientTypes = (
        CancellationToken,
        ApiKey,
        ApiKeyPage,
        ApiKeys,
        Backup,
        BackupPlan,
        BackupPolicy,
        Backups,
        Branches,
        Collections,
        CollectionsResponse,
        CreateApiKey,
        CreateBranch,
        CreateDatabaseRequest,
        CreateExport,
        CreateIndexRequest,
        DatabaseBranch,
        DatabaseListResponse,
        DatabaseRecord,
        DatabaseSchema,
        Databases,
        DeletedDatabase,
        ExecuteBackupRequest,
        ExportStatus,
        Exports,
        HealthResponse,
        IndexCreatedResponse,
        IndexDescriptor,
        IndexListResponse,
        Tenant,
        TenantApiKey,
        TenantBranch,
        LiveSubscription,
        ManagementCancellationToken,
        OperationOptions,
        PageOptions,
        PlacementLock,
        PlanBackupRequest,
        PollOptions,
        PollOutcome,
        PollResponse,
        RegionCatalogResponse,
        RegionDetails,
        ResetFrame,
        RestoreAt,
        RestoreJob,
        RestorePoint,
        RestoreRequest,
        Schemas,
        SqlParameters,
        StartRestore,
        CreateTenant,
        TenantListOptions,
        TenantOperationOptions,
        TenantPage,
        TenantPlacementLock,
        TenantTemplate,
        TenantsClient,
        TransferFormat,
        Usage,
        UsageClient,
        UsagePoint,
        UsageRange,
        UsageSummary,
        UsageWorkload,
    );

    let _: Option<PublishedClientTypes> = None;
}

#[tokio::test]
async fn strict_affinity_preserves_region_after_placement_responses() {
    for status in [200, 421] {
        let mut response = document_response();
        response.status = status;
        response
            .headers
            .push(("x-terrascale-placement".to_owned(), "FRA".to_owned()));
        response
            .headers
            .push(("x-terrascale-region".to_owned(), "FRA".to_owned()));
        let mock = Mock::new([response, document_response()]);
        let sdk = TerraScaleClient::with_transport(
            ClientConfig::builder()
                .preferred_region(terrascale::RegionCode::Gru)
                .region_affinity(terrascale::RegionAffinity::Strict)
                .build()
                .expect("config"),
            Arc::clone(&mock) as Arc<dyn HttpTransport>,
        );
        let scope = terrascale::DatabaseScope::new("database");
        let result = sdk.documents().get(&scope, "users", "doc_1").await;
        if status == 421 {
            let error = result.expect_err("strict region cannot serve database");
            assert_eq!(error.status, Some(421));
            assert_eq!(error.placement.as_deref(), Some("FRA"));
        } else {
            result.expect("document");
        }
        assert_eq!(sdk.pinned_region(), Some(terrascale::RegionCode::Gru));
        sdk.documents()
            .get(&scope, "users", "doc_1")
            .await
            .expect("followup");
        let requests = mock.requests.lock().expect("request mutex");
        assert_eq!(requests.len(), 2);
        assert!(requests
            .iter()
            .all(|request| request.url.starts_with("https://gru.terrascale.tech")));
    }
}
