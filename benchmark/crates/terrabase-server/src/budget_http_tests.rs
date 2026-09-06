//! HTTP admission coverage for signed regional write allocations.

use crate::usage_admission::{
    AllocationBindingResolver, AllocationVerifierConfig, UsageAdmissionJournal,
};
use axum::{
    Router,
    body::Body,
    http::{Request, StatusCode},
};
use base64::{Engine as _, engine::general_purpose::STANDARD};
use http_body_util::BodyExt;
use serde_json::Value;
use std::{path::PathBuf, sync::Arc};
use terrabase::identity::{ClusterUlid, DatabaseUlid, MemberUlid, ProjectUlid, TenantUlid};
use terrabase_tbep::placement::{PlacementBoundary, PlacementJournal};
use tower::ServiceExt;

const VECTORS: &str = include_str!("../../../testdata/allocation-canonical-vectors.json");

#[derive(Clone)]
struct Binding {
    organization: String,
    holder: String,
    placement: String,
    writer_epoch: u64,
}

impl AllocationBindingResolver for Binding {
    fn binding(&self, _: &str) -> Result<(String, String, String, u64), String> {
        Ok((
            self.organization.clone(),
            self.holder.clone(),
            self.placement.clone(),
            self.writer_epoch,
        ))
    }
}

struct Fixture {
    root: PathBuf,
    journal: Arc<UsageAdmissionJournal>,
    vector: Value,
}

impl Fixture {
    fn new() -> Self {
        Self::new_with_holder("writer-vector-1")
    }

    fn new_with_holder(holder: &str) -> Self {
        let nonce =
            std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_nanos();
        let root = std::env::temp_dir().join(format!("terrabase-budget-http-{nonce}"));
        std::fs::create_dir_all(&root).unwrap();
        #[cfg(unix)]
        {
            use std::os::unix::fs::PermissionsExt;
            std::fs::set_permissions(&root, std::fs::Permissions::from_mode(0o700)).unwrap();
        }
        let tenant = TenantUlid::generate();
        let project = ProjectUlid::generate();
        let database = DatabaseUlid::generate();
        let member = MemberUlid::generate();
        let boundary = PlacementBoundary::new(
            "production",
            &ClusterUlid::generate().to_text(),
            &member.to_text(),
            1,
            1,
            [(
                terrabase_cluster::production::DatabaseRouteScope::new(tenant, project, database),
                Some("GRU".into()),
                std::collections::BTreeSet::from(["GRU".into()]),
            )],
        )
        .unwrap();
        let placement =
            Arc::new(PlacementJournal::open(&root.join("placement.sqlite"), boundary).unwrap());
        let vector = serde_json::from_str::<Value>(VECTORS).unwrap()["vectors"][0].clone();
        let journal = UsageAdmissionJournal::open(
            &root,
            "production".into(),
            member.to_text(),
            "GRU".into(),
            placement,
        )
        .unwrap()
        .with_allocation_verifier(AllocationVerifierConfig {
            key_id: serde_json::from_str::<Value>(VECTORS).unwrap()["keyID"]
                .as_str()
                .unwrap()
                .into(),
            public_key_spki: STANDARD
                .decode(
                    serde_json::from_str::<Value>(VECTORS).unwrap()["publicSPKI"].as_str().unwrap(),
                )
                .unwrap(),
            policy_epoch: 1,
            consent_epoch: 1,
            resolver: Arc::new(Binding {
                organization: "org".into(),
                holder: holder.into(),
                placement: "db-placement".into(),
                writer_epoch: 9,
            }),
        })
        .unwrap();
        Self { root, journal: Arc::new(journal), vector }
    }

    fn router(&self) -> Router {
        self.journal.clone().router()
    }

    fn signed(&self) -> Value {
        serde_json::json!({
            "payload": self.vector["payload"],
            "signature": self.vector["signature"],
            "key_id": serde_json::from_str::<Value>(VECTORS).unwrap()["keyID"],
        })
    }
}

impl Drop for Fixture {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.root);
    }
}

#[tokio::test]
async fn canonical_signed_delivery_is_accepted_over_http_and_persisted() {
    let fixture = Fixture::new();
    let response = fixture
        .router()
        .oneshot(
            Request::post("/internal/v1/databases/usage/allocation")
                .header("content-type", "application/json")
                .body(Body::from(serde_json::to_vec(&fixture.signed()).unwrap()))
                .unwrap(),
        )
        .await
        .unwrap();
    assert_eq!(response.status(), StatusCode::OK);
    let body = response.into_body().collect().await.unwrap().to_bytes();
    assert!(body.is_empty());
    let connection =
        rusqlite::Connection::open(fixture.root.join("usage-admissions.sqlite")).unwrap();
    let (operation, database): (String, String) = connection
        .query_row("SELECT operation,database_id FROM allocations", [], |row| {
            Ok((row.get(0)?, row.get(1)?))
        })
        .unwrap();
    assert_eq!(operation, "vector-1");
    assert_eq!(database, "db");
}

#[tokio::test]
async fn canonical_retry_is_idempotent_and_binding_mismatch_is_denied() {
    let fixture = Fixture::new();
    let request = || {
        Request::post("/internal/v1/databases/usage/allocation")
            .header("content-type", "application/json")
            .body(Body::from(serde_json::to_vec(&fixture.signed()).unwrap()))
            .unwrap()
    };
    assert_eq!(fixture.router().oneshot(request()).await.unwrap().status(), StatusCode::OK);
    assert_eq!(fixture.router().oneshot(request()).await.unwrap().status(), StatusCode::OK);
    let connection =
        rusqlite::Connection::open(fixture.root.join("usage-admissions.sqlite")).unwrap();
    let count: u64 =
        connection.query_row("SELECT COUNT(*) FROM allocations", [], |row| row.get(0)).unwrap();
    assert_eq!(count, 1);
}

#[tokio::test]
async fn canonical_delivery_is_denied_when_holder_binding_changes() {
    let fixture = Fixture::new_with_holder("another-writer");
    let response = fixture
        .router()
        .oneshot(
            Request::post("/internal/v1/databases/usage/allocation")
                .header("content-type", "application/json")
                .body(Body::from(serde_json::to_vec(&fixture.signed()).unwrap()))
                .unwrap(),
        )
        .await
        .unwrap();
    assert_eq!(response.status(), StatusCode::CONFLICT);
}
