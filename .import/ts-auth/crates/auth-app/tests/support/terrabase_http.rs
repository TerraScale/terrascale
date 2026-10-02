//! Deterministic HTTP contract fixture for the TerraScale SDK's public document API.
//!
//! These tests exercise the real ts-auth router, TerraScale SDK, and auth-store over
//! loopback sockets. This in-memory fixture is not the TerraBase engine and does not
//! establish live backend compatibility; opt-in local-engine tests are tracked separately.

use std::{
    collections::BTreeMap,
    sync::{
        Arc, Mutex,
        atomic::{AtomicUsize, Ordering},
    },
};
use tokio::sync::oneshot;

use axum::{
    Json, Router,
    body::to_bytes,
    extract::{Path, Query, Request, State},
    http::{HeaderMap, HeaderValue, Method, StatusCode, header},
    response::{IntoResponse, Response},
    routing::{any, get, post},
};
use serde::Deserialize;
use serde_json::{Value, json};
use tokio::{net::TcpListener, task::JoinHandle};

type Key = (String, String, String);

pub const TEST_TERRASCALE_API_KEY: &str = "contract-fixture-key";

#[derive(Clone, Default)]
struct FixtureState {
    documents: Arc<Mutex<BTreeMap<Key, StoredDocument>>>,
    create_failures: Arc<Mutex<Vec<CreateFailure>>>,
    update_failures: Arc<Mutex<Vec<String>>>,
    update_conflicts: Arc<Mutex<Vec<UpdateConflict>>>,
    update_pauses: Arc<Mutex<Vec<UpdatePause>>>,
    delete_failures: Arc<Mutex<Vec<String>>>,
    delete_pauses: Arc<Mutex<Vec<DeletePauseRequest>>>,
    query_failures: Arc<Mutex<Vec<String>>>,
    query_pauses: Arc<Mutex<Vec<QueryPause>>>,
    get_failures: Arc<Mutex<Vec<String>>>,
    health_requests: Arc<AtomicUsize>,
    health_pauses: Arc<Mutex<Vec<HealthPause>>>,
    health_healthy: Arc<Mutex<Option<bool>>>,
}

#[derive(Clone, Copy, PartialEq, Eq)]
enum CreateFailureTiming {
    BeforeCommit,
    AfterCommit,
}

struct CreateFailure {
    collection: String,
    timing: CreateFailureTiming,
}

struct UpdateConflict {
    collection: String,
    remaining: usize,
}

struct UpdatePause {
    collection: String,
    entered: oneshot::Sender<()>,
    resume: oneshot::Receiver<()>,
}

struct QueryPause {
    collection: String,
    entered: oneshot::Sender<()>,
    resume: oneshot::Receiver<()>,
}

struct HealthPause {
    entered: oneshot::Sender<()>,
    resume: oneshot::Receiver<()>,
}

struct DeletePauseRequest {
    collection: String,
    entered: oneshot::Sender<()>,
    resume: oneshot::Receiver<()>,
}

/// Test handle for pausing one conditional PUT until the caller resumes it.
#[allow(dead_code)] // Only http_surface uses the controlled SCIM interleaving.
pub struct CompareAndPutPause {
    entered: Option<oneshot::Receiver<()>>,
    resume: Option<oneshot::Sender<()>>,
}

/// Test handle for pausing a query after its snapshot has been captured.
#[allow(dead_code)] // Only the assigned webhook and checkout race tests use this control.
pub struct QueryResponsePause {
    entered: Option<oneshot::Receiver<()>>,
    resume: Option<oneshot::Sender<()>>,
}

/// Test handle for pausing one TerraBase health response until resumed.
#[allow(dead_code)]
pub struct HealthCheckPause {
    entered: Option<oneshot::Receiver<()>>,
    resume: Option<oneshot::Sender<()>>,
}

/// Test handle for pausing a document DELETE immediately before commit.
#[allow(dead_code)] // The HTTP surface owns deterministic SCIM cleanup-owner races.
pub struct DeleteCommitPause {
    entered: Option<oneshot::Receiver<()>>,
    resume: Option<oneshot::Sender<()>>,
}

#[allow(dead_code)] // The handle is intentionally unused in test binaries without SCIM races.
impl CompareAndPutPause {
    pub async fn wait_until_paused(&mut self) {
        if let Some(entered) = self.entered.take() {
            let _ = entered.await;
        }
    }

    pub fn resume(mut self) {
        if let Some(resume) = self.resume.take() {
            let _ = resume.send(());
        }
    }
}

#[allow(dead_code)]
impl QueryResponsePause {
    pub async fn wait_until_paused(&mut self) {
        if let Some(entered) = self.entered.take() {
            let _ = entered.await;
        }
    }

    pub fn resume(mut self) {
        if let Some(resume) = self.resume.take() {
            let _ = resume.send(());
        }
    }
}

#[allow(dead_code)]
impl HealthCheckPause {
    pub async fn wait_until_paused(&mut self) {
        if let Some(entered) = self.entered.take() {
            let _ = entered.await;
        }
    }

    pub fn resume(mut self) {
        if let Some(resume) = self.resume.take() {
            let _ = resume.send(());
        }
    }
}

#[allow(dead_code)]
impl DeleteCommitPause {
    pub async fn wait_until_paused(&mut self) {
        if let Some(entered) = self.entered.take() {
            let _ = entered.await;
        }
    }

    pub fn resume(mut self) {
        if let Some(resume) = self.resume.take() {
            let _ = resume.send(());
        }
    }
}

#[derive(Clone)]
struct StoredDocument {
    version: u64,
    value: Value,
}

/// One real loopback HTTP server that implements only the SDK contract operations
/// reached by auth-app acceptance tests. Its document map is isolated per instance.
pub struct TerraBaseHttpFixture {
    pub origin: String,
    task: JoinHandle<()>,
    state: FixtureState,
}

impl TerraBaseHttpFixture {
    pub async fn start() -> Self {
        let state = FixtureState::default();
        let app = Router::new()
            .route("/api/v1/health", get(health))
            .route(
                "/api/v1/databases/{database}/collections/{collection}/documents/query",
                post(query_documents),
            )
            .route(
                "/api/v1/databases/{database}/collections/{collection}/documents",
                get(list_documents),
            )
            .route(
                "/api/v1/databases/{database}/collections/{collection}/documents/{id}",
                any(document),
            )
            .with_state(state.clone());
        let listener = TcpListener::bind("127.0.0.1:0")
            .await
            .expect("TerraBase contract fixture should bind loopback");
        let address = listener
            .local_addr()
            .expect("TerraBase contract fixture should have a bound address");
        let task = tokio::spawn(async move {
            axum::serve(listener, app)
                .await
                .expect("TerraBase contract fixture should remain available");
        });

        Self {
            origin: format!("http://{address}"),
            task,
            state,
        }
    }

    /// Make the next create-only PUT for `collection` fail before storing it.
    #[allow(dead_code)] // The shared fixture is compiled into several focused test binaries.
    pub fn fail_next_create_only_before_commit(&self, collection: impl Into<String>) {
        self.arm_create_failure(collection, CreateFailureTiming::BeforeCommit);
    }

    /// Make the next create-only PUT for `collection` store the document, then
    /// return a transient failure to model a lost response after commit.
    #[allow(dead_code)]
    pub fn fail_next_create_only_after_commit(&self, collection: impl Into<String>) {
        self.arm_create_failure(collection, CreateFailureTiming::AfterCommit);
    }

    /// Make the next conditional update for `collection` fail before storing it.
    #[allow(dead_code)] // Only the webhook dispatcher integration target injects this fault.
    pub fn fail_next_compare_and_put_before_commit(&self, collection: impl Into<String>) {
        self.state
            .update_failures
            .lock()
            .expect("fixture update-failure lock is healthy")
            .push(collection.into());
    }

    /// Make the next `attempts` conditional updates for `collection` return a
    /// definitive 412 compare conflict without changing the stored document.
    #[allow(dead_code)] // The HTTP surface owns the deterministic SCIM CAS regressions.
    pub fn conflict_next_compare_and_puts(&self, collection: impl Into<String>, attempts: usize) {
        assert!(
            attempts > 0,
            "at least one conditional conflict is required"
        );
        self.state
            .update_conflicts
            .lock()
            .expect("fixture update-conflict lock is healthy")
            .push(UpdateConflict {
                collection: collection.into(),
                remaining: attempts,
            });
    }

    /// Pause the next conditional update for `collection` after its version
    /// check and before commit, returning a handle the test controls.
    #[allow(dead_code)] // The HTTP surface owns the deterministic SCIM race regression.
    pub fn pause_next_compare_and_put(&self, collection: impl Into<String>) -> CompareAndPutPause {
        let (entered_tx, entered_rx) = oneshot::channel();
        let (resume_tx, resume_rx) = oneshot::channel();
        self.state
            .update_pauses
            .lock()
            .expect("fixture update-pause lock is healthy")
            .push(UpdatePause {
                collection: collection.into(),
                entered: entered_tx,
                resume: resume_rx,
            });
        CompareAndPutPause {
            entered: Some(entered_rx),
            resume: Some(resume_tx),
        }
    }

    /// Fail the next document DELETE in `collection` before changing storage.
    #[allow(dead_code)] // The HTTP surface owns the SCIM tombstone failure regression.
    pub fn fail_next_delete_before_commit(&self, collection: impl Into<String>) {
        self.state
            .delete_failures
            .lock()
            .expect("fixture delete-failure lock is healthy")
            .push(collection.into());
    }

    /// Fail the next document GET for `collection` before returning a snapshot.
    #[allow(dead_code)] // The HTTP surface owns the SCIM cleanup-preflight retry regression.
    pub fn fail_next_get(&self, collection: impl Into<String>) {
        self.state
            .get_failures
            .lock()
            .expect("fixture GET-failure lock is healthy")
            .push(collection.into());
    }

    /// Fail the next document query for `collection` before returning results.
    #[allow(dead_code)] // OAuth authentication tests inject client-lookup outages.
    pub fn fail_next_query(&self, collection: impl Into<String>) {
        self.state
            .query_failures
            .lock()
            .expect("fixture query-failure lock is healthy")
            .push(collection.into());
    }

    /// Pause the next document DELETE in `collection` after its version check.
    #[allow(dead_code)] // The HTTP surface owns deterministic SCIM cleanup-owner races.
    pub fn pause_next_delete_before_commit(
        &self,
        collection: impl Into<String>,
    ) -> DeleteCommitPause {
        let (entered_tx, entered_rx) = oneshot::channel();
        let (resume_tx, resume_rx) = oneshot::channel();
        self.state
            .delete_pauses
            .lock()
            .expect("fixture delete-pause lock is healthy")
            .push(DeletePauseRequest {
                collection: collection.into(),
                entered: entered_tx,
                resume: resume_rx,
            });
        DeleteCommitPause {
            entered: Some(entered_rx),
            resume: Some(resume_tx),
        }
    }

    /// Pause the next query response after its result snapshot has been captured.
    #[allow(dead_code)] // The HTTP surface owns deterministic cross-request query races.
    pub fn pause_next_query_response(&self, collection: impl Into<String>) -> QueryResponsePause {
        let (entered_tx, entered_rx) = oneshot::channel();
        let (resume_tx, resume_rx) = oneshot::channel();
        self.state
            .query_pauses
            .lock()
            .expect("fixture query-pause lock is healthy")
            .push(QueryPause {
                collection: collection.into(),
                entered: entered_tx,
                resume: resume_rx,
            });
        QueryResponsePause {
            entered: Some(entered_rx),
            resume: Some(resume_tx),
        }
    }

    #[allow(dead_code)]
    pub fn health_request_count(&self) -> usize {
        self.state.health_requests.load(Ordering::Relaxed)
    }

    #[allow(dead_code)]
    pub fn set_health_healthy(&self, healthy: bool) {
        *self
            .state
            .health_healthy
            .lock()
            .expect("fixture health lock is healthy") = Some(healthy);
    }

    /// Pause the next management health response until the test resumes it.
    #[allow(dead_code)]
    pub fn pause_next_health_check(&self) -> HealthCheckPause {
        let (entered_tx, entered_rx) = oneshot::channel();
        let (resume_tx, resume_rx) = oneshot::channel();
        self.state
            .health_pauses
            .lock()
            .expect("fixture health-pause lock is healthy")
            .push(HealthPause {
                entered: entered_tx,
                resume: resume_rx,
            });
        HealthCheckPause {
            entered: Some(entered_rx),
            resume: Some(resume_tx),
        }
    }

    fn arm_create_failure(&self, collection: impl Into<String>, timing: CreateFailureTiming) {
        self.state
            .create_failures
            .lock()
            .expect("fixture create-failure lock is healthy")
            .push(CreateFailure {
                collection: collection.into(),
                timing,
            });
    }
}

impl Drop for TerraBaseHttpFixture {
    fn drop(&mut self) {
        self.task.abort();
    }
}

async fn health(State(state): State<FixtureState>) -> Json<Value> {
    state.health_requests.fetch_add(1, Ordering::Relaxed);
    let pause = state
        .health_pauses
        .lock()
        .expect("fixture health-pause lock is healthy")
        .pop();
    if let Some(pause) = pause {
        let _ = pause.entered.send(());
        let _ = pause.resume.await;
    }
    let healthy = state
        .health_healthy
        .lock()
        .expect("fixture health lock is healthy")
        .unwrap_or(true);
    Json(json!({
        "region": "GRU",
        "healthy": healthy,
        "version": "contract-fixture",
    }))
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct QueryRequest {
    predicates: Vec<Predicate>,
    limit: usize,
    after: Option<String>,
}

#[derive(Deserialize)]
struct Predicate {
    path: String,
    operator: String,
    value: Option<Value>,
}

async fn query_documents(
    State(state): State<FixtureState>,
    Path((database, collection)): Path<(String, String)>,
    headers: HeaderMap,
    Json(query): Json<QueryRequest>,
) -> Response {
    if !authorized(&headers) {
        return error(StatusCode::UNAUTHORIZED, "unauthorized");
    }
    if take_query_failure(&state, &collection) {
        return injected_query_failure();
    }
    if query.limit == 0 || query.limit > 1_000 || query.predicates.len() > 32 {
        return error(StatusCode::BAD_REQUEST, "invalid_query");
    }

    let matches: Vec<_> = state
        .documents
        .lock()
        .expect("fixture document map lock is healthy")
        .iter()
        .filter(|((db, name, id), _)| {
            db == &database
                && name == &collection
                && query.after.as_ref().is_none_or(|cursor| id > cursor)
        })
        .filter(|(_, document)| {
            query
                .predicates
                .iter()
                .all(|predicate| predicate_matches(&document.value, predicate))
        })
        .map(|((_, _, id), document)| (id.clone(), document.clone()))
        .collect();
    let has_more = matches.len() > query.limit;
    let documents: Vec<_> = matches
        .into_iter()
        .take(query.limit)
        .map(|(id, document)| {
            json!({
                "id": id,
                "version": document.version,
                "value": document.value,
            })
        })
        .collect();
    let next_after = if has_more {
        documents.last().and_then(|row| row["id"].as_str())
    } else {
        None
    };

    let response = json!({
        "documents": documents,
        "hasMore": has_more,
        "nextAfter": next_after,
    });
    if let Some(pause) = take_query_pause(&state, &collection) {
        let _ = pause.entered.send(());
        let _ = pause.resume.await;
    }
    Json(response).into_response()
}

fn predicate_matches(document: &Value, predicate: &Predicate) -> bool {
    let actual = predicate
        .path
        .split('.')
        .try_fold(document, |current, segment| current.get(segment));
    let Some(actual) = actual else {
        return false;
    };
    let Some(expected) = predicate.value.as_ref() else {
        return false;
    };

    match predicate.operator.as_str() {
        "equal" => actual == expected,
        "greaterThanOrEqual" => match (actual.as_i64(), expected.as_i64()) {
            (Some(actual), Some(expected)) => actual >= expected,
            _ => match (actual.as_str(), expected.as_str()) {
                (Some(actual), Some(expected)) => actual >= expected,
                _ => false,
            },
        },
        "lessThan" => match (actual.as_i64(), expected.as_i64()) {
            (Some(actual), Some(expected)) => actual < expected,
            _ => match (actual.as_str(), expected.as_str()) {
                (Some(actual), Some(expected)) => actual < expected,
                _ => false,
            },
        },
        _ => false,
    }
}

#[derive(Deserialize)]
struct ListQuery {
    limit: Option<usize>,
    after: Option<String>,
}

async fn list_documents(
    State(state): State<FixtureState>,
    Path((database, collection)): Path<(String, String)>,
    Query(query): Query<ListQuery>,
    headers: HeaderMap,
) -> Response {
    if !authorized(&headers) {
        return error(StatusCode::UNAUTHORIZED, "unauthorized");
    }
    let limit = query.limit.unwrap_or(100).clamp(1, 1_000);
    let documents: Vec<_> = state
        .documents
        .lock()
        .expect("fixture document map lock is healthy")
        .iter()
        .filter(|((db, name, id), _)| {
            db == &database
                && name == &collection
                && query.after.as_ref().is_none_or(|cursor| id > cursor)
        })
        .take(limit)
        .map(|((_, _, id), document)| {
            json!({
                "id": id,
                "version": document.version,
                "value": document.value,
            })
        })
        .collect();

    Json(json!({ "documents": documents })).into_response()
}

async fn document(
    State(state): State<FixtureState>,
    Path((database, collection, id)): Path<(String, String, String)>,
    request: Request,
) -> Response {
    let method = request.method().clone();
    let headers = request.headers().clone();
    let key = (database, collection, id);
    if !authorized(&headers) {
        return error(StatusCode::UNAUTHORIZED, "unauthorized");
    }

    match method {
        Method::GET => {
            if take_get_failure(&state, &key.1) {
                return injected_get_failure();
            }
            let Some(document) = state
                .documents
                .lock()
                .expect("fixture document map lock is healthy")
                .get(&key)
                .cloned()
            else {
                return error(StatusCode::NOT_FOUND, "not_found");
            };
            json_document(document)
        }
        Method::PUT | Method::PATCH => {
            let body = match to_bytes(request.into_body(), 1_048_576).await {
                Ok(body) => body,
                Err(_) => return error(StatusCode::BAD_REQUEST, "invalid_body"),
            };
            let mut value: Value = match serde_json::from_slice(&body) {
                Ok(value) => value,
                Err(_) => return error(StatusCode::BAD_REQUEST, "invalid_json"),
            };
            {
                let documents = state
                    .documents
                    .lock()
                    .expect("fixture document map lock is healthy");
                let current = documents.get(&key);
                if headers
                    .get(header::IF_NONE_MATCH)
                    .and_then(|value| value.to_str().ok())
                    == Some("*")
                    && current.is_some()
                {
                    return error(StatusCode::PRECONDITION_FAILED, "already_exists");
                }
                if let Some(expected) = headers
                    .get(header::IF_MATCH)
                    .and_then(|value| value.to_str().ok())
                    && current.is_none_or(|document| etag(document.version) != expected)
                {
                    return error(StatusCode::PRECONDITION_FAILED, "precondition_failed");
                }
            }
            if method == Method::PUT
                && headers.get(header::IF_MATCH).is_some()
                && let Some(pause) = take_update_pause(&state, &key.1)
            {
                let _ = pause.entered.send(());
                let _ = pause.resume.await;
            }

            let mut documents = state
                .documents
                .lock()
                .expect("fixture document map lock is healthy");
            let current = documents.get(&key);
            if headers
                .get(header::IF_NONE_MATCH)
                .and_then(|value| value.to_str().ok())
                == Some("*")
                && current.is_some()
            {
                return error(StatusCode::PRECONDITION_FAILED, "already_exists");
            }
            let expected_match = headers
                .get(header::IF_MATCH)
                .and_then(|value| value.to_str().ok());
            if let Some(expected) = expected_match
                && current.is_none_or(|document| etag(document.version) != expected)
            {
                return error(StatusCode::PRECONDITION_FAILED, "precondition_failed");
            }
            if method == Method::PUT
                && expected_match.is_some()
                && take_update_conflict(&state, &key.1)
            {
                return error(StatusCode::PRECONDITION_FAILED, "injected_compare_conflict");
            }
            let create_failure = if method == Method::PUT
                && headers
                    .get(header::IF_NONE_MATCH)
                    .and_then(|value| value.to_str().ok())
                    == Some("*")
                && current.is_none()
            {
                take_create_failure(&state, &key.1)
            } else {
                None
            };
            if create_failure == Some(CreateFailureTiming::BeforeCommit) {
                return injected_create_failure();
            }
            if method == Method::PUT
                && headers.get(header::IF_MATCH).is_some()
                && take_update_failure(&state, &key.1)
            {
                return injected_update_failure();
            }
            if method == Method::PATCH {
                let Some(current) = current else {
                    return error(StatusCode::NOT_FOUND, "not_found");
                };
                let mut merged = current.value.clone();
                merge_patch(&mut merged, &value);
                value = merged;
            }

            let version = current.map_or(1, |document| document.version.saturating_add(1));
            documents.insert(key, StoredDocument { version, value });
            if create_failure == Some(CreateFailureTiming::AfterCommit) {
                return injected_create_failure();
            }
            let mut response = Json(json!({ "revision": version.to_string() })).into_response();
            response.headers_mut().insert(
                header::ETAG,
                HeaderValue::from_str(&etag(version)).expect("quoted numeric ETag is valid"),
            );
            response
        }
        Method::DELETE => {
            let expected_match = headers
                .get(header::IF_MATCH)
                .and_then(|value| value.to_str().ok());
            {
                let documents = state
                    .documents
                    .lock()
                    .expect("fixture document map lock is healthy");
                if let Some(expected) = expected_match
                    && documents
                        .get(&key)
                        .is_none_or(|document| etag(document.version) != expected)
                {
                    return error(StatusCode::PRECONDITION_FAILED, "precondition_failed");
                }
            }
            if let Some(pause) = take_delete_pause(&state, &key.1) {
                let _ = pause.entered.send(());
                let _ = pause.resume.await;
            }
            if take_delete_failure(&state, &key.1) {
                return injected_delete_failure();
            }

            let mut documents = state
                .documents
                .lock()
                .expect("fixture document map lock is healthy");
            if let Some(expected) = expected_match
                && documents
                    .get(&key)
                    .is_none_or(|document| etag(document.version) != expected)
            {
                return error(StatusCode::PRECONDITION_FAILED, "precondition_failed");
            }
            let removed = documents.remove(&key).is_some();
            if removed {
                StatusCode::NO_CONTENT.into_response()
            } else {
                error(StatusCode::NOT_FOUND, "not_found")
            }
        }
        _ => StatusCode::METHOD_NOT_ALLOWED.into_response(),
    }
}

fn take_create_failure(state: &FixtureState, collection: &str) -> Option<CreateFailureTiming> {
    let mut failures = state
        .create_failures
        .lock()
        .expect("fixture create-failure lock is healthy");
    let index = failures
        .iter()
        .position(|failure| failure.collection == collection)?;
    Some(failures.remove(index).timing)
}

fn take_update_failure(state: &FixtureState, collection: &str) -> bool {
    let mut failures = state
        .update_failures
        .lock()
        .expect("fixture update-failure lock is healthy");
    let Some(index) = failures.iter().position(|failure| failure == collection) else {
        return false;
    };
    failures.remove(index);
    true
}

fn take_update_conflict(state: &FixtureState, collection: &str) -> bool {
    let mut conflicts = state
        .update_conflicts
        .lock()
        .expect("fixture update-conflict lock is healthy");
    let Some(index) = conflicts
        .iter()
        .position(|conflict| conflict.collection == collection)
    else {
        return false;
    };
    conflicts[index].remaining -= 1;
    if conflicts[index].remaining == 0 {
        conflicts.remove(index);
    }
    true
}

fn take_update_pause(state: &FixtureState, collection: &str) -> Option<UpdatePause> {
    let mut pauses = state
        .update_pauses
        .lock()
        .expect("fixture update-pause lock is healthy");
    let index = pauses
        .iter()
        .position(|pause| pause.collection == collection)?;
    Some(pauses.remove(index))
}

fn take_query_pause(state: &FixtureState, collection: &str) -> Option<QueryPause> {
    let mut pauses = state
        .query_pauses
        .lock()
        .expect("fixture query-pause lock is healthy");
    let index = pauses
        .iter()
        .position(|pause| pause.collection == collection)?;
    Some(pauses.remove(index))
}

fn take_query_failure(state: &FixtureState, collection: &str) -> bool {
    let mut failures = state
        .query_failures
        .lock()
        .expect("fixture query-failure lock is healthy");
    let Some(index) = failures.iter().position(|failure| failure == collection) else {
        return false;
    };
    failures.remove(index);
    true
}

fn take_delete_failure(state: &FixtureState, collection: &str) -> bool {
    let mut failures = state
        .delete_failures
        .lock()
        .expect("fixture delete-failure lock is healthy");
    let Some(index) = failures.iter().position(|failure| failure == collection) else {
        return false;
    };
    failures.remove(index);
    true
}

fn take_get_failure(state: &FixtureState, collection: &str) -> bool {
    let mut failures = state
        .get_failures
        .lock()
        .expect("fixture GET-failure lock is healthy");
    let Some(index) = failures.iter().position(|failure| failure == collection) else {
        return false;
    };
    failures.remove(index);
    true
}

fn take_delete_pause(state: &FixtureState, collection: &str) -> Option<DeletePauseRequest> {
    let mut pauses = state
        .delete_pauses
        .lock()
        .expect("fixture delete-pause lock is healthy");
    let index = pauses
        .iter()
        .position(|pause| pause.collection == collection)?;
    Some(pauses.remove(index))
}

fn injected_create_failure() -> Response {
    let mut response = error(StatusCode::SERVICE_UNAVAILABLE, "injected_create_failure");
    // The SDK treats a retry delay above its 30-second cap as a final error.
    // This keeps the one-shot fault deterministic even for an idempotent PUT.
    response
        .headers_mut()
        .insert(header::RETRY_AFTER, HeaderValue::from_static("31"));
    response
}

fn injected_update_failure() -> Response {
    let mut response = error(StatusCode::SERVICE_UNAVAILABLE, "injected_update_failure");
    response
        .headers_mut()
        .insert(header::RETRY_AFTER, HeaderValue::from_static("31"));
    response
}

fn injected_delete_failure() -> Response {
    let mut response = error(StatusCode::SERVICE_UNAVAILABLE, "injected_delete_failure");
    response
        .headers_mut()
        .insert(header::RETRY_AFTER, HeaderValue::from_static("31"));
    response
}

fn injected_get_failure() -> Response {
    let mut response = error(StatusCode::SERVICE_UNAVAILABLE, "injected_get_failure");
    response
        .headers_mut()
        .insert(header::RETRY_AFTER, HeaderValue::from_static("31"));
    response
}

fn injected_query_failure() -> Response {
    let mut response = error(StatusCode::SERVICE_UNAVAILABLE, "injected_query_failure");
    response
        .headers_mut()
        .insert(header::RETRY_AFTER, HeaderValue::from_static("31"));
    response
}

fn json_document(document: StoredDocument) -> Response {
    let mut response = Json(document.value).into_response();
    response.headers_mut().insert(
        header::ETAG,
        HeaderValue::from_str(&etag(document.version)).expect("quoted numeric ETag is valid"),
    );
    response
}

fn etag(version: u64) -> String {
    format!("\"{version}\"")
}

fn authorized(headers: &HeaderMap) -> bool {
    headers
        .get(header::AUTHORIZATION)
        .and_then(|value| value.to_str().ok())
        .and_then(|value| value.strip_prefix("Bearer "))
        == Some(TEST_TERRASCALE_API_KEY)
}

fn error(status: StatusCode, code: &str) -> Response {
    (
        status,
        Json(json!({ "error": { "code": code, "message": code } })),
    )
        .into_response()
}

fn merge_patch(target: &mut Value, patch: &Value) {
    let Some(patch_object) = patch.as_object() else {
        *target = patch.clone();
        return;
    };
    if !target.is_object() {
        *target = Value::Object(Default::default());
    }
    let target_object = target
        .as_object_mut()
        .expect("object merge target was initialized");
    for (key, value) in patch_object {
        if value.is_null() {
            target_object.remove(key);
        } else {
            merge_patch(
                target_object.entry(key.clone()).or_insert(Value::Null),
                value,
            );
        }
    }
}
