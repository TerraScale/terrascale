//! Shared helpers for the E-17 public-boundary security matrix.

use std::path::PathBuf;
use std::sync::Arc;
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::Duration;

use axum::Router;
use axum::body::Body;
use axum::http::{HeaderValue, Method, Request};
use terrabase::observability::MetricsRegistry;
use terrabase::regions::RegionCode;
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
        Self { root, state }
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
        let _ = std::fs::remove_dir_all(&self.root);
    }
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
