use std::collections::VecDeque;
use std::sync::{Arc, Mutex};

use async_trait::async_trait;
use serde_json::json;
use terrascale::tenants::{
    CreateTenant, CreateTenantBranch, CreateTenantKey, TenantListOptions, TenantOperationOptions,
    TenantPlacementLock,
};
use terrascale::{
    ClientConfig, ErrorKind, HttpTransport, RegionCode, Request, Response, Result, TerraScaleClient,
};

#[derive(Debug, Default)]
struct Mock {
    requests: Mutex<Vec<Request>>,
    responses: Mutex<VecDeque<Response>>,
}

impl Mock {
    fn new() -> Arc<Self> {
        Arc::new(Self::default())
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

#[tokio::test]
async fn valid_tenant_create_fails_closed_without_project_route_or_http() {
    let mock = Mock::new();
    let sdk = client(Arc::clone(&mock));
    let error = sdk
        .tenants()
        .create(
            CreateTenant::new("acme", "saas-default")
                .placement_lock(TenantPlacementLock::Regions(vec![RegionCode::Gru])),
            Some("wrong-project-route"),
        )
        .await
        .expect_err("tenant route is unpublished");

    assert_eq!(error.kind, ErrorKind::Domain);
    assert_eq!(error.code, "contract_gap");
    assert!(error.message.contains("no HTTP request was sent"));
    assert_eq!(mock.count(), 0);
}

#[test]
fn placement_lock_serializes_to_the_pinned_wire_shape_and_rejects_empty_lists() {
    assert_eq!(
        serde_json::to_value(TenantPlacementLock::EuOnly).unwrap(),
        json!("eu-only")
    );
    assert_eq!(
        serde_json::to_value(TenantPlacementLock::Regions(vec![
            RegionCode::Fra,
            RegionCode::Gru
        ]))
        .unwrap(),
        json!(["FRA", "GRU"])
    );
    assert_eq!(
        serde_json::from_value::<TenantPlacementLock>(json!("br-only")).unwrap(),
        TenantPlacementLock::BrOnly
    );
    assert_eq!(
        serde_json::to_value(
            CreateTenant::new("acme", "default")
                .placement_lock(TenantPlacementLock::Regions(vec![RegionCode::Gru]),)
        )
        .unwrap(),
        json!({"key":"acme", "template":"default", "placementLock":["GRU"]})
    );
    assert!(TenantPlacementLock::regions(Vec::new()).is_err());
    assert!(serde_json::from_value::<TenantPlacementLock>(json!([])).is_err());
    assert!(serde_json::from_value::<TenantPlacementLock>(json!("everywhere")).is_err());
}

#[tokio::test]
async fn required_fields_are_validated_before_contract_gap() {
    let mock = Mock::new();
    let sdk = client(Arc::clone(&mock));

    let error = sdk
        .tenants()
        .create(
            CreateTenant {
                key: "  ".to_owned(),
                template: Some("default".to_owned()),
                placement_lock: None,
            },
            None,
        )
        .await
        .expect_err("blank key");
    assert_eq!(error.kind, ErrorKind::Validation);
    assert_eq!(error.code, "invalid_tenant_key");

    let error = sdk
        .tenants()
        .create(CreateTenant::new("acme", "  "), None)
        .await
        .expect_err("blank template");
    assert_eq!(error.kind, ErrorKind::Validation);
    assert_eq!(error.code, "invalid_required_field");
    assert_eq!(mock.count(), 0);
}

#[tokio::test]
async fn delete_keeps_confirmation_and_idempotency_validation() {
    let mock = Mock::new();
    let sdk = client(Arc::clone(&mock));

    let error = sdk
        .tenants()
        .delete("acme", false, None)
        .await
        .expect_err("unconfirmed deletion");
    assert_eq!(error.kind, ErrorKind::Validation);
    assert_eq!(error.code, "confirm_required");

    let error = sdk
        .tenants()
        .delete_with_options(
            "acme",
            true,
            TenantOperationOptions {
                idempotency_key: Some("  ".to_owned()),
            },
            None,
        )
        .await
        .expect_err("blank idempotency key");
    assert_eq!(error.kind, ErrorKind::Validation);
    assert_eq!(error.code, "invalid_required_field");

    let error = sdk
        .tenants()
        .delete_with_options(
            "acme",
            true,
            TenantOperationOptions {
                idempotency_key: Some("delete-once".to_owned()),
            },
            Some("project-that-must-not-be-used"),
        )
        .await
        .expect_err("tenant route is unpublished");
    assert_eq!(error.code, "contract_gap");
    assert_eq!(mock.count(), 0);
}

#[tokio::test]
async fn list_and_nested_mutations_validate_then_fail_closed() {
    let mock = Mock::new();
    let sdk = client(Arc::clone(&mock));

    let error = sdk
        .tenants()
        .list_page(TenantListOptions::new().with_limit(0), None)
        .await
        .expect_err("zero limit");
    assert_eq!(error.code, "invalid_page_size");

    let error = sdk
        .tenants()
        .create_key_with_request(
            "acme",
            CreateTenantKey {
                name: Some("  ".to_owned()),
                scopes: None,
            },
            None,
        )
        .await
        .expect_err("blank key name");
    assert_eq!(error.code, "invalid_required_field");

    let error = sdk
        .tenants()
        .create_branch_with_request(
            "acme",
            CreateTenantBranch {
                name: Some("support".to_owned()),
                reason: Some("  ".to_owned()),
            },
            None,
        )
        .await
        .expect_err("blank branch reason");
    assert_eq!(error.code, "invalid_required_field");

    let error = sdk
        .tenants()
        .get("acme", None)
        .await
        .expect_err("tenant route is unpublished");
    assert_eq!(error.code, "contract_gap");
    assert_eq!(mock.count(), 0);
}
