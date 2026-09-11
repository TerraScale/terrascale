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
    selected_regions: Vec<String>,
}

impl AllocationBindingResolver for Binding {
    fn binding(&self, _: &str) -> Result<(String, String, String, u64, Vec<String>), String> {
        Ok((
            self.organization.clone(),
            self.holder.clone(),
            self.placement.clone(),
            self.writer_epoch,
            self.selected_regions.clone(),
        ))
    }
}

impl AllocationBindingResolver for std::sync::Mutex<Binding> {
    fn binding(
        &self,
        database: &str,
    ) -> Result<(String, String, String, u64, Vec<String>), String> {
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
        Self::with_verifier_regions(holder, verifier, vec!["GRU".into(), "SFO".into()])
    }

    fn with_regions(regions: Vec<String>) -> Self {
        Self::with_verifier_regions("writer-vector-1", None, regions)
    }

    fn with_verifier_regions(
        holder: &str,
        verifier: Option<AllocationVerifierConfig>,
        regions: Vec<String>,
    ) -> Self {
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
                regions.iter().cloned().collect(),
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
                    selected_regions: regions,
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
fn signed_for(
    tenant: &str,
    database: &str,
    operation: &str,
    paid: bool,
) -> (Value, Vec<u8>, EcdsaKeyPair) {
    signed_for_regions(tenant, database, operation, paid, vec!["GRU"])
}

fn signed_for_regions(
    tenant: &str,
    database: &str,
    operation: &str,
    paid: bool,
    regions: Vec<&str>,
) -> (Value, Vec<u8>, EcdsaKeyPair) {
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
    let region_count = i64::try_from(regions.len()).unwrap();
    let payload = serde_json::to_vec(&terrabase::usage::AllocationEnvelope {
        schema_version: 2,
        grant: terrabase::usage::AllocationGrant {
            schema_version: 2,
            kind: "regional_write_pool_refill".into(),
            refill_operation_id: format!("refill-{operation}"),
            pool_id: format!("pool-{database}"),
            cycle_id: "cycle".into(),
            rate_nano_dollar: 1000,
            organization_id: tenant.into(),
            database_id: database.into(),
            branch_id: "main".into(),
            holder_id: "holder".into(),
            region: "gru".into(),
            // The production refill wire contract uses lowercase DNS-style
            // region tokens; the resolver binding above remains canonical.
            regions: regions.into_iter().map(str::to_ascii_lowercase).collect(),
            placement: "db-placement".into(),
            local_units: 1,
            aggregate_units: region_count,
            included_local_units: i64::from(!paid),
            paid_local_units: i64::from(paid),
            included_aggregate_units: region_count * i64::from(!paid),
            paid_aggregate_units: region_count * i64::from(paid),
            pool_sequence: 1,
            policy_epoch: 1,
            consent_epoch: 1,
            writer_epoch: 1,
            issued_at: "2026-01-01T00:00:00Z".into(),
            expires_at: "2099-01-01T00:00:00Z".into(),
        },
    })
    .unwrap();
    (sign_allocation(&key, &key_id, &payload), spki, key)
}

#[allow(clippy::cast_possible_truncation, reason = "ECDSA signatures are far below u16::MAX")]
fn sign_allocation(key: &EcdsaKeyPair, key_id: &str, payload: &[u8]) -> Value {
    let mut message = terrabase::usage::ALLOCATION_SIGNING_DOMAIN.to_vec();
    message.extend_from_slice(payload);
    let signature = key.sign(&SystemRandom::new(), &message).unwrap();
    let mut wrapped = b"TSCS\x01\x01".to_vec();
    wrapped.extend_from_slice(&hex::decode(key_id).unwrap());
    wrapped.extend_from_slice(&(signature.as_ref().len() as u16).to_be_bytes());
    wrapped.extend_from_slice(signature.as_ref());
    serde_json::json!({"payload":STANDARD.encode(payload),"signature":STANDARD.encode(wrapped),"key_id":key_id})
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
    assert_eq!(operation, "refill-vector-1");
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
async fn three_region_origin_fact_reconciles_local_debit_after_reopen() {
    let fixture = Fixture::with_regions(vec!["GRU".into(), "SFO".into(), "FRA".into()]);
    let mut state = super::state_with_keys_and_control();
    let response = super::router(state.clone())
        .oneshot(
            Request::post("/api/v1/databases")
                .header("authorization", "Bearer tskey_test_abcdefghijklmnop")
                .header("x-terrascale-tenant-id", "terrabase-local")
                .header("x-terrascale-project-id", "local")
                .body(Body::from(
                    r#"{"name":"prod","placement":["GRU","SFO","FRA"],"residency":null}"#,
                ))
                .unwrap(),
        )
        .await
        .unwrap();
    assert_eq!(response.status(), StatusCode::CREATED);
    let host = super::super::document_host(&state, "prod").unwrap();
    let database = host.engine().database_owner().database().to_text();
    let operation = "three-region-replay";
    let (signed, spki, _) = signed_for_regions(
        "terrabase-local",
        &database,
        operation,
        false,
        vec!["GRU", "SFO", "FRA"],
    );
    let binding = Arc::new(std::sync::Mutex::new(Binding {
        organization: "terrabase-local".into(),
        holder: "holder".into(),
        placement: "db-placement".into(),
        writer_epoch: 1,
        selected_regions: vec!["GRU".into(), "SFO".into(), "FRA".into()],
    }));
    let verifier = AllocationVerifierConfig {
        key_id: signed["key_id"].as_str().unwrap().into(),
        public_key_spki: spki,
        policy_epoch: 1,
        consent_epoch: 1,
        resolver: binding,
    };
    let deliver = || {
        Request::post("/internal/v1/databases/usage/allocation")
            .header("content-type", "application/json")
            .body(Body::from(serde_json::to_vec(&signed).unwrap()))
            .unwrap()
    };
    let admitted = fixture.reopen(verifier.clone());
    let response = admitted.clone().router().oneshot(deliver()).await.unwrap();
    assert_eq!(response.status(), StatusCode::OK);
    Arc::get_mut(&mut state).unwrap().budget =
        Some(Arc::new(crate::production_budget::ProductionBudgetRuntime::new(admitted)));
    let mut app = super::router(state.clone());
    let put = || public_write(false, operation, "Ada");
    assert_eq!(app.clone().oneshot(put()).await.unwrap().status(), StatusCode::OK);

    drop(app);
    Arc::get_mut(&mut state).unwrap().budget = Some(Arc::new(
        crate::production_budget::ProductionBudgetRuntime::new(fixture.reopen(verifier)),
    ));
    app = super::router(state.clone());
    assert_eq!(app.clone().oneshot(put()).await.unwrap().status(), StatusCode::OK);

    let path = state
        .catalog
        .as_ref()
        .unwrap()
        .location_for("terrabase-local", "local", "prod")
        .join("terrabase.sqlite3");
    let connection = rusqlite::Connection::open(path).unwrap();
    let (local_units, body): (i64, Vec<u8>) = connection
        .query_row(
            "SELECT units_total, (SELECT body FROM usage_facts WHERE fact_id=?1)
             FROM usage_budget_allocations WHERE operation=?1",
            [operation],
            |row| Ok((row.get(0)?, row.get(1)?)),
        )
        .unwrap();
    let fact = terrabase::usage::DurableUsageFact::decode(&body).unwrap();
    assert_eq!(local_units, 1, "allowance debit remains local allocation units");
    assert_eq!(fact.fact().delta(), 3, "origin fact is multiplied by three regions");
}

fn public_write(batch: bool, key: &str, name: &str) -> Request<Body> {
    let value = serde_json::json!({"name": name});
    let (request, body) = if batch {
        (
            Request::post("/api/v1/databases/prod/collections/users/documents/batch"),
            serde_json::json!({"operations":[{"op":"put","id":"doc-1","value":value}]}),
        )
    } else {
        (Request::put("/api/v1/databases/prod/collections/users/documents/doc-1"), value)
    };
    request
        .header("authorization", "Bearer tskey_test_abcdefghijklmnop")
        .header("idempotency-key", key)
        .body(Body::from(body.to_string()))
        .unwrap()
}

#[tokio::test]
#[allow(clippy::too_many_lines)]
async fn public_writes_consume_signed_included_and_paid_allocations_once() {
    for (paid, batch) in [(false, false), (true, false), (false, true), (true, true)] {
        let operation = if batch { "api-batch-idem-budget" } else { "api-document-idem-budget" };
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
        let (signed, spki, signing_key) = signed_for("terrabase-local", &database, operation, paid);
        let binding = Arc::new(std::sync::Mutex::new(Binding {
            organization: "terrabase-local".into(),
            holder: "holder".into(),
            placement: "db-placement".into(),
            writer_epoch: 1,
            selected_regions: vec!["GRU".into()],
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
        let put = || public_write(batch, "budget", "Ada");
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
                "paid={paid}, batch={batch}, attempt={attempt}: {}",
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
        let changed = public_write(batch, "budget", "Grace");
        assert_eq!(app.clone().oneshot(changed).await.unwrap().status(), StatusCode::CONFLICT);
        binding.lock().unwrap().holder = "another-writer".into();
        assert_eq!(
            fixture.router().oneshot(deliver()).await.unwrap().status(),
            StatusCode::CONFLICT
        );
        // A stale spending grant must not hide an already committed result.
        // The API caller and database serving assignment remain authorized.
        assert_eq!(app.clone().oneshot(put()).await.unwrap().status(), StatusCode::OK);
        binding.lock().unwrap().holder = "holder".into();
        let mut payload: Value =
            serde_json::from_slice(&STANDARD.decode(signed["payload"].as_str().unwrap()).unwrap())
                .unwrap();
        payload["grant"]["expires_at"] = "2026-01-01T00:00:01Z".into();
        let expired = sign_allocation(
            &signing_key,
            signed["key_id"].as_str().unwrap(),
            &serde_json::to_vec(&payload).unwrap(),
        );
        assert!(matches!(
            terrabase::usage::VerifiedAllocation::verify(
                &serde_json::to_vec(&expired).unwrap(),
                &verifier.key_id,
                &verifier.public_key_spki,
                "GRU",
                "db-placement",
                "terrabase-local",
                &database,
                "holder",
                1,
                1,
                1,
                u64::MAX,
            ),
            Err(terrabase::usage::AllocationError::Expired)
        ));
        let journal =
            rusqlite::Connection::open(fixture.root.join("usage-admissions.sqlite")).unwrap();
        journal
            .execute(
                "UPDATE allocations SET payload=?1 WHERE operation=?2",
                rusqlite::params![
                    serde_json::to_vec(&expired).unwrap(),
                    format!("refill-{operation}")
                ],
            )
            .unwrap();
        assert!(
            fixture.journal.resolve_allocation(&format!("refill-{operation}"), &database).is_err(),
            "expired signed grant cannot authorize spending"
        );
        assert_eq!(app.clone().oneshot(put()).await.unwrap().status(), StatusCode::OK);
        let fresh = public_write(batch, "fresh-budget", "Ada");
        assert_eq!(
            app.clone().oneshot(fresh).await.unwrap().status(),
            StatusCode::TOO_MANY_REQUESTS
        );
        let path = state
            .catalog
            .as_ref()
            .unwrap()
            .location_for("terrabase-local", "local", "prod")
            .join("terrabase.sqlite3");
        let connection = rusqlite::Connection::open(path).unwrap();
        let refill_operation = format!("refill-{operation}");
        let consumption: (i64, i64, i64) = connection.query_row(
            "SELECT COUNT(*), SUM(units_total), SUM(units_remaining) FROM usage_budget_allocations WHERE operation=?1",
            [&refill_operation],
            |row| Ok((row.get(0)?, row.get(1)?, row.get(2)?)),
        ).unwrap();
        assert_eq!(consumption, (1, 1, 0), "paid={paid}, batch={batch}");
        let journal_connection =
            rusqlite::Connection::open(fixture.root.join("usage-admissions.sqlite")).unwrap();
        let admitted_at: i64 = journal_connection
            .query_row(
                "SELECT accepted_at_millis FROM operation_admissions WHERE operation=?1",
                [operation],
                |row| row.get(0),
            )
            .unwrap();
        let fact_at: i64 = connection
            .query_row(
                "SELECT start_millis FROM usage_facts WHERE fact_id=?1",
                [operation],
                |row| row.get(0),
            )
            .unwrap();
        assert_eq!(fact_at, admitted_at, "the retry retains the first local accounting window");
        let facts: i64 = connection
            .query_row("SELECT COUNT(*) FROM usage_facts WHERE fact_id=?1", [operation], |row| {
                row.get(0)
            })
            .unwrap();
        assert_eq!(facts, 1, "retry must not duplicate billed usage; paid={paid}, batch={batch}");
    }
}

#[tokio::test]
async fn schema_two_refills_install_independently_and_replay_after_journal_reopen() {
    let (first, spki, key) =
        signed_for_regions("org", "db", "refill-first", false, vec!["GRU", "SFO"]);
    let verifier = AllocationVerifierConfig {
        key_id: first["key_id"].as_str().unwrap().into(),
        public_key_spki: spki,
        policy_epoch: 1,
        consent_epoch: 1,
        resolver: Arc::new(Binding {
            organization: "org".into(),
            holder: "holder".into(),
            placement: "db-placement".into(),
            writer_epoch: 9,
            selected_regions: vec!["GRU".into(), "SFO".into()],
        }),
    };
    let fixture = Fixture::with_verifier("holder", Some(verifier.clone()));
    let key_id = first["key_id"].as_str().unwrap();
    let mut payload: Value =
        serde_json::from_slice(&STANDARD.decode(first["payload"].as_str().unwrap()).unwrap())
            .unwrap();
    payload["grant"]["refill_operation_id"] = Value::String("refill-second".into());
    payload["grant"]["pool_sequence"] = Value::from(2u64);
    let second = sign_allocation(&key, key_id, &serde_json::to_vec(&payload).unwrap());
    let app = fixture.router();
    for signed in [first, second] {
        let response = app
            .clone()
            .oneshot(
                Request::post("/internal/v1/databases/usage/allocation")
                    .header("content-type", "application/json")
                    .body(Body::from(serde_json::to_vec(&signed).unwrap()))
                    .unwrap(),
            )
            .await
            .unwrap();
        assert_eq!(response.status(), StatusCode::OK);
    }
    let connection =
        rusqlite::Connection::open(fixture.root.join("usage-admissions.sqlite")).unwrap();
    let count: i64 =
        connection.query_row("SELECT COUNT(*) FROM allocations", [], |row| row.get(0)).unwrap();
    assert_eq!(count, 2, "each refill has its own durable identity");
    let reopened = fixture.reopen(verifier);
    assert!(reopened.resolve_allocation("refill-second", "db").unwrap().is_some());
}
