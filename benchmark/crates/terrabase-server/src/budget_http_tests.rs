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

impl AllocationBindingResolver for std::sync::Mutex<Binding> {
    fn binding(&self, database: &str) -> Result<(String, String, String, u64), String> {
        self.lock().unwrap().binding(database)
    }
}

struct Fixture {
    root: PathBuf,
    journal: Arc<UsageAdmissionJournal>,
    vector: Value,
    placement: Arc<PlacementJournal>,
    member: String,
}

impl Fixture {
    fn new() -> Self {
        Self::new_with_holder("writer-vector-1")
    }

    fn new_with_holder(holder: &str) -> Self {
        Self::with_verifier(holder, None)
    }

    fn with_verifier(holder: &str, verifier: Option<AllocationVerifierConfig>) -> Self {
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
            placement.clone(),
        )
        .unwrap()
        .with_allocation_verifier(verifier.unwrap_or_else(|| {
            AllocationVerifierConfig {
                key_id: serde_json::from_str::<Value>(VECTORS).unwrap()["keyID"]
                    .as_str()
                    .unwrap()
                    .into(),
                public_key_spki: STANDARD
                    .decode(
                        serde_json::from_str::<Value>(VECTORS).unwrap()["publicSPKI"]
                            .as_str()
                            .unwrap(),
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
            }
        }))
        .unwrap();
        Self { root, journal: Arc::new(journal), vector, placement, member: member.to_text() }
    }

    fn reopen(&self, verifier: AllocationVerifierConfig) -> Arc<UsageAdmissionJournal> {
        Arc::new(
            UsageAdmissionJournal::open(
                &self.root,
                "production".into(),
                self.member.clone(),
                "GRU".into(),
                self.placement.clone(),
            )
            .unwrap()
            .with_allocation_verifier(verifier)
            .unwrap(),
        )
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
fn signed_for(tenant: &str, database: &str, operation: &str, paid: bool) -> (Value, Vec<u8>) {
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
            operation_id: operation.into(),
            organization_id: tenant.into(),
            database_id: database.into(),
            branch_id: "main".into(),
            holder_id: "holder".into(),
            requested_units: 1,
            included_units: i64::from(!paid),
            paid_units: i64::from(paid),
            regions: vec!["GRU".into()],
            units: 1,
            cost_nano_dollar: if paid { 1000 } else { 0 },
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
    let count: i64 =
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

#[tokio::test]
async fn public_put_consumes_signed_included_and_paid_allocations_once() {
    for paid in [false, true] {
        let mut state = super::state_with_keys_and_control();
        let response = super::router(state.clone())
            .oneshot(
                Request::post("/api/v1/databases")
                    .header("authorization", "Bearer tskey_test_abcdefghijklmnop")
                    .header("x-terrascale-tenant-id", "terrabase-local")
                    .header("x-terrascale-project-id", "local")
                    .body(Body::from(r#"{"name":"prod","placement":["GRU"],"residency":null}"#))
                    .unwrap(),
            )
            .await
            .unwrap();
        assert_eq!(response.status(), StatusCode::CREATED);
        let host = super::super::document_host(&state, "prod").unwrap();
        let database = host.engine().database_owner().database().to_text();
        let (signed, spki) = signed_for("terrabase-local", &database, "api-put-idem-budget", paid);
        let binding = Arc::new(std::sync::Mutex::new(Binding {
            organization: "terrabase-local".into(),
            holder: "holder".into(),
            placement: "db-placement".into(),
            writer_epoch: 1,
        }));
        let verifier = AllocationVerifierConfig {
            key_id: signed["key_id"].as_str().unwrap().into(),
            public_key_spki: spki,
            policy_epoch: 1,
            consent_epoch: 1,
            resolver: binding.clone(),
        };
        let fixture = Fixture::with_verifier("holder", Some(verifier.clone()));
        Arc::get_mut(&mut state).unwrap().budget = Some(Arc::new(
            crate::production_budget::ProductionBudgetRuntime::new(fixture.journal.clone()),
        ));
        let mut app = super::router(state.clone());
        let put = || {
            Request::put("/api/v1/databases/prod/collections/users/documents/doc-1")
                .header("authorization", "Bearer tskey_test_abcdefghijklmnop")
                .header("idempotency-key", "budget")
                .body(Body::from(r#"{"name":"Ada"}"#))
                .unwrap()
        };
        assert_eq!(
            app.clone().oneshot(put()).await.unwrap().status(),
            StatusCode::TOO_MANY_REQUESTS
        );
        let deliver = || {
            Request::post("/internal/v1/databases/usage/allocation")
                .header("content-type", "application/json")
                .body(Body::from(serde_json::to_vec(&signed).unwrap()))
                .unwrap()
        };
        for _ in 0..2 {
            let response = fixture.router().oneshot(deliver()).await.unwrap();
            let status = response.status();
            let body = response.into_body().collect().await.unwrap().to_bytes();
            assert_eq!(status, StatusCode::OK, "{}", String::from_utf8_lossy(&body));
        }
        let mut first = None;
        for attempt in 0..2 {
            let response = app.clone().oneshot(put()).await.unwrap();
            let status = response.status();
            let body = response.into_body().collect().await.unwrap().to_bytes();
            assert_eq!(
                status,
                StatusCode::OK,
                "paid={paid}, attempt={attempt}: {}",
                String::from_utf8_lossy(&body)
            );
            if let Some(expected) = &first {
                assert_eq!(&body, expected);
            } else {
                first = Some(body);
                drop(app);
                Arc::get_mut(&mut state).unwrap().budget =
                    Some(Arc::new(crate::production_budget::ProductionBudgetRuntime::new(
                        fixture.reopen(verifier.clone()),
                    )));
                app = super::router(state.clone());
                tokio::time::sleep(std::time::Duration::from_millis(5)).await;
            }
        }
        let changed = Request::put("/api/v1/databases/prod/collections/users/documents/doc-1")
            .header("authorization", "Bearer tskey_test_abcdefghijklmnop")
            .header("idempotency-key", "budget")
            .body(Body::from(r#"{"name":"Grace"}"#))
            .unwrap();
        assert_eq!(app.clone().oneshot(changed).await.unwrap().status(), StatusCode::CONFLICT);
        binding.lock().unwrap().holder = "another-writer".into();
        assert_eq!(
            fixture.router().oneshot(deliver()).await.unwrap().status(),
            StatusCode::CONFLICT
        );
        assert_eq!(
            app.clone().oneshot(put()).await.unwrap().status(),
            StatusCode::TOO_MANY_REQUESTS
        );
        let path = state
            .catalog
            .as_ref()
            .unwrap()
            .location_for("terrabase-local", "local", "prod")
            .join("terrabase.sqlite3");
        let connection = rusqlite::Connection::open(path).unwrap();
        let consumption: (i64, i64, i64) = connection.query_row(
            "SELECT COUNT(*), SUM(units_total), SUM(units_remaining) FROM usage_budget_allocations WHERE operation='api-put-idem-budget'",
            [], |row| Ok((row.get(0)?, row.get(1)?, row.get(2)?))).unwrap();
        assert_eq!(consumption, (1, 1, 0), "paid={paid}");
        let journal_connection =
            rusqlite::Connection::open(fixture.root.join("usage-admissions.sqlite")).unwrap();
        let admitted_at: i64 = journal_connection.query_row(
            "SELECT accepted_at_millis FROM operation_admissions WHERE operation='api-put-idem-budget'",
            [], |row| row.get(0),
        ).unwrap();
        let fact_at: i64 = connection
            .query_row(
                "SELECT start_millis FROM usage_facts WHERE fact_id='api-put-idem-budget'",
                [],
                |row| row.get(0),
            )
            .unwrap();
        assert_eq!(fact_at, admitted_at, "the retry retains the first local accounting window");
        let facts: i64 = connection
            .query_row(
                "SELECT COUNT(*) FROM usage_facts WHERE fact_id='api-put-idem-budget'",
                [],
                |row| row.get(0),
            )
            .unwrap();
        assert_eq!(facts, 1, "retry must not duplicate billed usage; paid={paid}");
    }
}
