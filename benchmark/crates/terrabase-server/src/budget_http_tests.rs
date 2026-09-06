//! HTTP admission coverage for signed regional write allocations.

use crate::usage_admission::{
    AllocationBindingResolver, AllocationVerifierConfig, UsageAdmissionJournal,
};
use aws_lc_rs::{
    digest::{SHA256, digest},
    rand::SystemRandom,
    signature::{ECDSA_P256_SHA256_ASN1_SIGNING, EcdsaKeyPair, KeyPair},
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

/// The public PUT route derives its operation identity from this key.
/// Build the producer envelope with the test-only P256 signer so the test
/// exercises the same wire verifier as production delivery.
fn signed_for(tenant: &str, _project: &str, database: &str) -> (Value, Vec<u8>) {
    let key_der =
        EcdsaKeyPair::generate_pkcs8(&ECDSA_P256_SHA256_ASN1_SIGNING, &SystemRandom::new())
            .unwrap();
    let key = EcdsaKeyPair::from_pkcs8(&ECDSA_P256_SHA256_ASN1_SIGNING, key_der.as_ref()).unwrap();
    let mut spki = vec![
        0x30, 0x59, 0x30, 0x13, 0x06, 0x07, 0x2a, 0x86, 0x48, 0xce, 0x3d, 0x02, 0x01, 0x06, 0x08,
        0x2a, 0x86, 0x48, 0xce, 0x3d, 0x03, 0x01, 0x07, 0x03, 0x42, 0x00,
    ];
    spki.extend_from_slice(key.public_key().as_ref());
    let key_id = hex::encode(digest(&SHA256, &spki).as_ref());
    let payload = serde_json::to_vec(&terrabase::usage::AllocationEnvelope {
        schema_version: 1,
        grant: terrabase::usage::AllocationGrant {
            cycle_id: "cycle".into(),
            rate_nano_dollar: 1000,
            operation_id: "api-put-idem-budget".into(),
            organization_id: tenant.into(),
            database_id: database.into(),
            branch_id: "main".into(),
            holder_id: "holder".into(),
            requested_units: 1,
            included_units: 1,
            paid_units: 0,
            regions: vec!["gru".into()],
            units: 1,
            cost_nano_dollar: 0,
            policy_epoch: 1,
            consent_epoch: 1,
            writer_epoch: 1,
            issued_at: "2026-01-01T00:00:00Z".into(),
            expires_at: "2099-01-01T00:00:00Z".into(),
            revoked: false,
            fenced: false,
        },
        cycle_id: "cycle".into(),
        rate_nano_dollar: 1000,
        placement: "db-placement".into(),
    })
    .unwrap();
    let mut message = terrabase::usage::ALLOCATION_SIGNING_DOMAIN.to_vec();
    message.extend_from_slice(&payload);
    let signature = key.sign(&SystemRandom::new(), &message).unwrap();
    let mut wrapped = b"TSCS\x01\x01".to_vec();
    wrapped.extend_from_slice(&hex::decode(&key_id).unwrap());
    wrapped.extend_from_slice(&(signature.as_ref().len() as u16).to_be_bytes());
    wrapped.extend_from_slice(signature.as_ref());
    (
        serde_json::json!({"payload":STANDARD.encode(payload),"signature":STANDARD.encode(wrapped),"key_id":key_id}),
        spki,
    )
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
