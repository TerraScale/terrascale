use std::collections::VecDeque;
use std::sync::{Arc, Mutex};

use async_trait::async_trait;
use serde_json::json;
use terrascale::tenants::{CreateTenant, TenantListOptions, TenantPage};
use terrascale::{
    ClientConfig, ErrorKind, HttpTransport, Method, RegionCode, Request, Response, Result,
    TerraScaleClient,
};

#[derive(Debug)]
struct Mock {
    requests: Mutex<Vec<Request>>,
    responses: Mutex<VecDeque<Response>>,
}

impl Mock {
    fn new(responses: impl IntoIterator<Item = Response>) -> Arc<Self> {
        Arc::new(Self {
            requests: Mutex::new(Vec::new()),
            responses: Mutex::new(responses.into_iter().collect()),
        })
    }

    fn last(&self) -> Request {
        self.requests
            .lock()
            .expect("request mutex")
            .last()
            .expect("a request")
            .clone()
    }

    fn count(&self) -> usize {
        self.requests.lock().expect("request mutex").len()
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

fn client(mock: Arc<Mock>) -> TerraScaleClient {
    let transport: Arc<dyn HttpTransport> = mock;
    TerraScaleClient::with_transport(
        ClientConfig::builder()
            .api_key("ts_test_key")
            .project_id("proj_test")
            .build()
            .expect("config"),
        transport,
    )
}

fn tenant(key: &str) -> serde_json::Value {
    json!({
        "key": key,
        "databaseId": format!("db_{key}"),
        "regions": ["GRU"],
        "template": "saas-default"
    })
}

#[tokio::test]
async fn create_inherits_template_and_placement_and_get_escapes_key() {
    let mock = Mock::new([
        Response::json(201, &tenant("acme/blue")),
        Response::json(200, &tenant("acme/blue")),
    ]);
    let sdk = client(Arc::clone(&mock));

    let created = sdk
        .tenants()
        .create(
            CreateTenant {
                key: "acme/blue".to_owned(),
                template: Some("saas-default".to_owned()),
                regions: Some(vec![RegionCode::Gru]),
            },
            None,
        )
        .await
        .expect("create tenant");
    assert_eq!(created.key, "acme/blue");
    assert_eq!(created.template.as_deref(), Some("saas-default"));
    assert_eq!(created.regions, [RegionCode::Gru]);

    let create_request = mock.last();
    assert_eq!(create_request.method, Method::Post);
    assert!(create_request
        .url
        .ends_with("/api/v1/projects/proj_test/tenants"));
    assert!(create_request
        .headers
        .iter()
        .any(|(name, value)| name.eq_ignore_ascii_case("idempotency-key") && !value.is_empty()));
    assert_eq!(
        serde_json::from_slice::<serde_json::Value>(create_request.body.as_ref().expect("body"))
            .expect("json body"),
        json!({"key":"acme/blue", "template":"saas-default", "regions":["GRU"]})
    );

    let fetched = sdk
        .tenants()
        .get("acme/blue", None)
        .await
        .expect("get tenant");
    assert_eq!(fetched.database_id, "db_acme/blue");
    assert!(mock.last().url.ends_with("/tenants/acme%2Fblue"));
}

#[tokio::test]
async fn list_page_sends_cursor_and_limit_and_accepts_envelope() {
    let mock = Mock::new([Response::json(
        200,
        &json!({"items": [tenant("acme")], "nextCursor": "next/page"}),
    )]);
    let sdk = client(Arc::clone(&mock));

    let page: TenantPage = sdk
        .tenants()
        .list_page(
            TenantListOptions::new()
                .with_cursor("previous/page")
                .with_limit(25),
            None,
        )
        .await
        .expect("list page");
    assert_eq!(page.tenants.len(), 1);
    assert_eq!(page.next_cursor.as_deref(), Some("next/page"));
    assert_eq!(mock.last().method, Method::Get);
    assert!(mock
        .last()
        .url
        .ends_with("/tenants?cursor=previous%2Fpage&limit=25"));
}

#[tokio::test]
async fn list_keeps_legacy_vec_surface_for_bare_array_responses() {
    let mock = Mock::new([Response::json(200, &json!([tenant("acme")]))]);
    let sdk = client(Arc::clone(&mock));
    let tenants = sdk.tenants().list(None).await.expect("list tenants");
    assert_eq!(tenants.len(), 1);
    assert_eq!(tenants[0].key, "acme");
    assert!(!mock.last().url.contains('?'));
}

#[tokio::test]
async fn delete_requires_confirmation_and_is_idempotent() {
    let mock = Mock::new([Response {
        status: 204,
        headers: Vec::new(),
        body: Vec::new(),
    }]);
    let sdk = client(Arc::clone(&mock));

    let error = sdk
        .tenants()
        .delete("acme", false, None)
        .await
        .expect_err("unconfirmed deletion");
    assert_eq!(error.kind, ErrorKind::Validation);
    assert_eq!(error.code, "confirm_required");
    assert_eq!(mock.count(), 0);

    sdk.tenants()
        .delete("acme", true, None)
        .await
        .expect("confirmed deletion");
    let request = mock.last();
    assert_eq!(request.method, Method::Delete);
    assert_eq!(
        request
            .headers
            .iter()
            .find(|(name, _)| name.eq_ignore_ascii_case("x-terrascale-delete-confirmation"))
            .map(|(_, value)| value.as_str()),
        Some("acme")
    );
    assert_eq!(
        request
            .headers
            .iter()
            .find(|(name, _)| name.eq_ignore_ascii_case("idempotency-key"))
            .map(|(_, value)| value.as_str()),
        Some("ts_tenant_delete_acme")
    );
}

#[tokio::test]
async fn tenant_key_and_support_branch_are_tenant_scoped_mutations() {
    let mock = Mock::new([
        Response::json(
            201,
            &json!({"keyId":"key_1","token":"secret","tenantKey":"acme"}),
        ),
        Response::json(
            201,
            &json!({"branchId":"branch_1","tenantKey":"acme","status":"ready"}),
        ),
    ]);
    let sdk = client(Arc::clone(&mock));

    let key = sdk
        .tenants()
        .create_key("acme", None)
        .await
        .expect("tenant key");
    assert_eq!(key.tenant_key, "acme");
    let request = mock.last();
    assert_eq!(request.route_name, "tenants.keys.create");
    assert!(request.url.ends_with("/tenants/acme/keys"));
    assert!(request
        .headers
        .iter()
        .any(|(name, value)| name.eq_ignore_ascii_case("idempotency-key") && !value.is_empty()));

    let branch = sdk
        .tenants()
        .create_branch("acme", None)
        .await
        .expect("support branch");
    assert_eq!(branch.branch_id, "branch_1");
    assert_eq!(mock.last().route_name, "tenants.branches.create");
    assert!(mock.last().url.ends_with("/tenants/acme/branches"));
}

#[tokio::test]
async fn blank_tenant_keys_fail_before_transport() {
    let mock = Mock::new([]);
    let sdk = client(Arc::clone(&mock));
    let error = sdk.tenants().get("   ", None).await.expect_err("blank key");
    assert_eq!(error.kind, ErrorKind::Validation);
    assert_eq!(error.code, "invalid_tenant_key");
    assert_eq!(mock.count(), 0);
}
