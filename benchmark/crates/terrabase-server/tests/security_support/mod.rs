//! Shared helpers for the E-17 public-boundary security matrix.

use std::path::PathBuf;
use std::sync::Arc;
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::Duration;

use axum::Router;
use axum::body::Body;
use axum::http::{HeaderValue, Method, Request};
use terrabase::encryption::{
    DatabaseKey, DatabaseKeyLease, DatabaseKeySource, DatabaseKeyVersion, KeyPurpose, KeyScope,
    KeyVersionMetadata,
};
use terrabase::identity::{DatabaseUlid, MemberUlid, ProjectUlid, TenantUlid};
use terrabase::member::{
    AcceptedDatabaseRole, AdmissionLimits, DatabaseRole, DatabaseRoleAuthority, DatabaseScope,
    DatabaseSpec, Member,
};
use terrabase::observability::MetricsRegistry;
use terrabase::regions::RegionCode;
use terrabase_server::admission::AdmissionRecord;
use terrabase_server::admitted_databases::AdmittedDatabaseRegistry;
use terrabase_server::api::{ApiState, router};
use terrabase_server::api_keys::{KeyStore, Scope, Verbs};
use terrabase_server::backup::{BackupStreams, TimeTravelCache};
use terrabase_server::control::ControlPlane;
use terrabase_server::reactive_api::{CapabilityKeyRing, OriginProofKeyRing};
use tower::util::ServiceExt;

static NEXT_ROOT: AtomicU64 = AtomicU64::new(1);

/// An isolated public-router fixture. The root is deleted when the fixture is
/// dropped, including database and control-plane `SQLite` files.
pub struct Fixture {
    root: PathBuf,
    /// The state shared by the router and test callers.
    pub state: Arc<ApiState>,
    admitted_member: Option<Member>,
    admitted_root: Option<PathBuf>,
}

/// A real production registry populated with accepted Member leases.
pub struct AdmittedFixture {
    pub registry: Arc<AdmittedDatabaseRegistry>,
    pub records: Vec<AdmissionRecord>,
    member: Member,
    root: PathBuf,
}

struct Writer;
impl DatabaseRoleAuthority for Writer {
    fn accepted_role(&self, _: &terrabase::member::DatabaseId) -> Option<AcceptedDatabaseRole> {
        Some(AcceptedDatabaseRole { epoch: 1, role: DatabaseRole::Writer })
    }
}

#[derive(Debug)]
struct StaticKey(KeyVersionMetadata);
impl DatabaseKeySource for StaticKey {
    fn current_lease(
        &self,
    ) -> Result<Arc<DatabaseKeyLease>, terrabase::encryption::EncryptionError> {
        self.lease(self.0.version())
    }

    fn lease(
        &self,
        version: DatabaseKeyVersion,
    ) -> Result<Arc<DatabaseKeyLease>, terrabase::encryption::EncryptionError> {
        if version != self.0.version() {
            return Err(terrabase::encryption::EncryptionError::InvalidKeyMetadata);
        }
        DatabaseKeyLease::test_oci_vault(
            self.0.clone(),
            DatabaseKey::from_bytes([29; 32]),
            Duration::from_secs(60),
        )
        .map(Arc::new)
    }
}

impl Fixture {
    /// Creates a router with a real control plane and lazy database catalog.
    pub fn new(registrations: &[(&str, &str, Scope)]) -> Self {
        let number = NEXT_ROOT.fetch_add(1, Ordering::Relaxed);
        let root = std::env::temp_dir()
            .join(format!("terrabase-e17-security-{}-{number}", std::process::id()));
        std::fs::create_dir_all(&root).expect("security fixture root");
        let control = ControlPlane::open(&root).expect("control plane opens");
        let catalog = terrabase_tbep::catalog::HostCatalog::open(
            &root.join("databases"),
            8,
            Duration::from_mins(1),
        )
        .expect("database catalog opens");
        let backups = Arc::new(BackupStreams::new(root.join("missing-litestream")));
        let state = Arc::new(ApiState {
            budget: None,
            native_backups: None,
            native_recovery: None,
            native_restore_target: None,
            admitted_databases: None,
            replication_progress: None,
            standalone_durability: false,
            development: Arc::default(),
            provisioning: None,
            region: RegionCode::GRU,
            member_id: "security-test-member".into(),
            metrics: Arc::new(MetricsRegistry::new()),
            delegated_identity: None,
            byo_jwt: None,
            keys: Some(Arc::new(KeyStore::with_keys(registrations))),
            control: Some(Arc::new(control)),
            catalog: Some(Arc::new(catalog)),
            scope: ("tenant-a".into(), "project-a".into()),
            backups: Some(Arc::clone(&backups)),
            backup_root: root.join("backup"),
            timetravel: Some(Arc::new(TimeTravelCache::new(backups, &root.join("timetravel")))),
            guards: Arc::new(terrabase_server::guards::AbuseGuards::new()),
            origin_proof_keys: Arc::new(
                OriginProofKeyRing::new(
                    b"e17-origin-current-key",
                    Some(b"e17-origin-previous-key"),
                )
                .expect("origin keys"),
            ),
            webhook_keys: None,
            exports: None,
            imports: None,
            capability_keys: Arc::new(
                CapabilityKeyRing::new(b"e17-capability-current", Some(b"e17-capability-previous"))
                    .expect("capability keys"),
            ),
        });
        Self { root, state, admitted_member: None, admitted_root: None }
    }

    /// Installs a real admitted registry on this public-router fixture.
    pub fn install_admitted(&mut self, admitted: AdmittedFixture) {
        let state = Arc::get_mut(&mut self.state).expect("fixture state is unique");
        state.admitted_databases = Some(admitted.registry);
        self.admitted_member = Some(admitted.member);
        self.admitted_root = Some(admitted.root);
    }

    /// Returns the real Axum public router.
    pub fn router(&self) -> Router {
        router(Arc::clone(&self.state))
    }

    /// Registers a database in the real public control-plane registry.
    pub fn create_database(&self, name: &str, placement: &[RegionCode]) {
        self.state
            .control
            .as_ref()
            .expect("control plane")
            .create(name, placement, None)
            .expect("database registration");
    }
}

impl Drop for Fixture {
    fn drop(&mut self) {
        if let Some(state) = Arc::get_mut(&mut self.state) {
            state.admitted_databases = None;
        }
        if let Some(member) = self.admitted_member.take() {
            let _ = member.close(Duration::from_secs(1));
        }
        if let Some(root) = self.admitted_root.take() {
            let _ = std::fs::remove_dir_all(root);
        }
        let _ = std::fs::remove_dir_all(&self.root);
    }
}

/// Builds two independently owned databases through the production admission path.
pub fn admitted_fixture() -> AdmittedFixture {
    let root =
        std::env::temp_dir().join(format!("terrabase-e17-admitted-{}", DatabaseUlid::generate()));
    std::fs::create_dir_all(&root).expect("admitted fixture root");
    let version = DatabaseKeyVersion::from_text("01ARZ3NDEKTSV4RRFFQ69G5FB0").unwrap();
    let owners: Vec<_> = (0..2)
        .map(|_| (TenantUlid::generate(), ProjectUlid::generate(), DatabaseUlid::generate()))
        .collect();
    let specs: Vec<_> = owners
        .iter()
        .map(|(tenant, project, database)| {
            DatabaseSpec::scoped(
                DatabaseScope::new("production", *tenant, *project, *database, RegionCode::GRU)
                    .unwrap(),
                &database.to_string(),
            )
        })
        .collect();
    let keys = owners
        .iter()
        .map(|(tenant, project, database)| {
            let scope = KeyScope::new_with_project(
                "production",
                *tenant,
                *project,
                *database,
                KeyPurpose::Data,
            )
            .unwrap();
            (
                (*database).into(),
                Arc::new(StaticKey(KeyVersionMetadata::new(scope, version)))
                    as Arc<dyn DatabaseKeySource>,
            )
        })
        .collect();
    let member = Member::open_production(&root, specs, AdmissionLimits::new(8, 4), keys)
        .expect("production member opens");
    for (_, _, database) in &owners {
        member.accept_database_role(&(*database).into(), &Writer).expect("database role accepted");
    }
    let records: Vec<_> = owners
        .iter()
        .map(|(tenant, project, database)| AdmissionRecord {
            created_at: 1,
            environment: "production".into(),
            tenant_id: tenant.to_string(),
            project_id: project.to_string(),
            database_id: database.to_string(),
            parent_database_id: None,
            key_version: version.to_string(),
            name: "same-name".into(),
            regions: vec!["GRU".into()],
            residency: "GRU".into(),
            operation_id: DatabaseUlid::generate().to_string(),
            region: "GRU".into(),
            member_id: MemberUlid::generate().to_string(),
        })
        .collect();
    let registry = Arc::new(AdmittedDatabaseRegistry::new(Arc::new(|| true)));
    for record in &records {
        let id = terrabase::member::DatabaseId::try_new(&record.database_id).unwrap();
        let lease = member.acquire(&id).expect("admitted lease");
        registry
            .publish(record.clone(), lease, root.join(&record.database_id), 1)
            .expect("registry publication");
    }
    registry
        .activate(&records.iter().map(|record| record.database_id.clone()).collect::<Vec<_>>())
        .expect("registry activation");
    AdmittedFixture { registry, records, member, root }
}

/// Builds a request for the real Axum router.
pub fn request(method: Method, uri: &str, body: impl Into<Body>) -> Request<Body> {
    Request::builder().method(method).uri(uri).body(body.into()).expect("request")
}

/// Builds a bearer-authenticated request for the public router.
pub fn authenticated_request(
    method: Method,
    uri: &str,
    body: impl Into<Body>,
    token: impl AsRef<str>,
) -> Request<Body> {
    let mut request = request(method, uri, body);
    request.headers_mut().insert(
        "authorization",
        HeaderValue::from_str(&format!("Bearer {}", token.as_ref())).expect("authorization"),
    );
    request
}

/// Sends one request through the real public router.
pub async fn send(router: Router, request: Request<Body>) -> axum::response::Response {
    router.oneshot(request).await.expect("router answers")
}

/// Returns a stable API key registration used by broad-scope tests.
#[must_use]
pub fn admin_registration() -> (&'static str, &'static str, Scope) {
    ("tskey_e17_admin_abcdefghijkl", "e17-admin", Scope::every_database(Verbs::ALL))
}
