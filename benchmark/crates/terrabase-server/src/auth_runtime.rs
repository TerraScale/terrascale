//! Encrypted strict auth consensus and public operations, admitted by GRU bootstrap.
//! Customer records never pass through the control journal or a Worker.

use std::collections::{BTreeMap, BTreeSet};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex, RwLock};
use std::time::Duration;

use axum::extract::{DefaultBodyLimit, Path, State};
use axum::http::{HeaderMap, StatusCode};
use axum::response::{IntoResponse, Response};
use axum::{Json, Router};
use serde::{Deserialize, Serialize};
use terrabase_auth::{
    Action, AuthAuthority, AuthNetwork, AuthorityError, Command, Connector, JoinRelation, Mode,
    Operator, Outcome, Predicate, QueryJoin, QuerySort, SortDirection, SqliteStore,
};
use terrabase_cluster::production::{
    ProductionQuicEndpoint, ProductionRegion, ProductionTransportError,
};
use terrabase_member_transport::production::{ProductionConnection, ProductionConnectionClass};
use terrabase_tbep::auth_bootstrap::AuthBootstrapJournal;
use terrabase_tbep::proto::terrabase::v1::AuthGroupBootstrapGrant;
use utoipa::ToSchema;

/// Production BYO-JWT provider and tenant policy composition.
pub mod byo_config;

use crate::api_keys::{KeyStore, Scope, Verbs};
use crate::config::{ProductionConfig, PublicApiConfig};

struct Domain {
    authority: AuthAuthority,
    network: AuthNetwork,
    grant: AuthGroupBootstrapGrant,
}

/// Admitted, active auth groups. An absent grant never creates local auth state.
pub struct AuthRuntime {
    endpoint: Arc<ProductionQuicEndpoint>,
    journal: Arc<AuthBootstrapJournal>,
    production: ProductionConfig,
    domains: RwLock<BTreeMap<String, Arc<Domain>>>,
    keys: KeyStore,
    client_slots: Arc<tokio::sync::Semaphore>,
    consensus_slots: Arc<tokio::sync::Semaphore>,
    client_work: Mutex<tokio::task::JoinSet<()>>,
    consensus_work: Mutex<tokio::task::JoinSet<()>>,
    dispatching: AtomicBool,
    lifecycle_lock: tokio::sync::Mutex<()>,
}

impl AuthRuntime {
    /// Create a runtime with verified transport and explicit customer API key material.
    /// This does not allocate an auth group before its authenticated control grant.
    ///
    /// # Errors
    /// Refuses missing protected credentials or missing encryption delivery configuration.
    pub fn new(
        endpoint: Arc<ProductionQuicEndpoint>,
        journal: Arc<AuthBootstrapJournal>,
        production: ProductionConfig,
        public: &PublicApiConfig,
    ) -> Result<Arc<Self>, String> {
        if production.keys.is_none() {
            return Err("auth database encryption delivery is missing".into());
        }
        let key_path = public
            .bootstrap_key_file
            .as_deref()
            .ok_or("auth public API credential file missing")?;
        let bytes = crate::runtime::read_secret_key(key_path)
            .map_err(|_| "auth public API credential file invalid")?;
        let key =
            String::from_utf8(bytes).map_err(|_| "auth public API credential encoding invalid")?;
        let key = key.trim();
        if key.len() < 32 {
            return Err("auth public API credential is too short".into());
        }
        let keys = KeyStore::empty();
        keys.register(key, "bootstrap", Scope::every_database(Verbs::ALL));
        Ok(Arc::new(Self {
            endpoint,
            journal,
            production,
            domains: RwLock::new(BTreeMap::new()),
            keys,
            client_slots: Arc::new(tokio::sync::Semaphore::new(8)),
            consensus_slots: Arc::new(tokio::sync::Semaphore::new(32)),
            client_work: Mutex::new(tokio::task::JoinSet::new()),
            consensus_work: Mutex::new(tokio::task::JoinSet::new()),
            dispatching: AtomicBool::new(true),
            lifecycle_lock: tokio::sync::Mutex::new(()),
        }))
    }

    /// Run the granted auth groups, public listener, and bounded grant reconciliation.
    /// Any failed serving task stops this auth runtime; it never creates replacement state.
    ///
    /// # Errors
    /// Refuses listener failures or loss of required runtime dependencies.
    pub async fn serve(
        self: Arc<Self>,
        listen: std::net::SocketAddr,
        shutdown: impl std::future::Future<Output = ()> + Send + 'static,
    ) -> Result<(), String> {
        self.reconcile().await?;
        let listener = tokio::net::TcpListener::bind(listen)
            .await
            .map_err(|_| "auth public listener cannot bind")?;
        let (stop, signal) = tokio::sync::oneshot::channel();
        let app = router(Arc::clone(&self));
        let mut http = tokio::spawn(async move {
            axum::serve(listener, app)
                .with_graceful_shutdown(async {
                    let _ = signal.await;
                })
                .await
        });
        let mut receive = tokio::spawn(Arc::clone(&self).receive());
        let mut receive_finished = false;
        let mut interval = tokio::time::interval(Duration::from_secs(1));
        tokio::pin!(shutdown);
        let outcome = loop {
            tokio::select! {
                ()=&mut shutdown=>break Ok(()),
                _=&mut http=>break Err("auth public listener stopped".into()),
                _=&mut receive=> {
                    receive_finished = true;
                    break Err("auth consensus listener stopped".into());
                },
                _=interval.tick()=> {
                    // Grant arrival after control startup activates its exact persisted group.
                    // Refused activation stays unavailable and is retried without reissuing genesis.
                    if self.reconcile().await.is_err() { eprintln!("auth group activation refused; inspect admitted grant, state, and key delivery"); }
                }
            }
        };
        let _ = stop.send(());
        if !http.is_finished() {
            let _ = http.await;
        }
        // In-flight HTTP operations may still need inbound consensus to finish.
        if !receive_finished {
            receive.abort();
            let _ = receive.await;
        }
        self.shutdown().await;
        outcome
    }

    /// Reconciles authenticated groups when the production API owns the
    /// shared transport accept loop. This deliberately does not bind a
    /// second listener or start a competing receive loop.
    ///
    /// # Errors
    /// Returns the same failures as `reconcile`.
    pub async fn reconcile_for_shared_listener(&self) -> Result<(), String> {
        self.reconcile().await
    }

    /// Recover every explicit grant once; failures leave that group unavailable.
    ///
    /// # Errors
    /// Refuses lost auth files, mismatched grants, missing keys, or consensus startup failure.
    pub async fn reconcile(&self) -> Result<(), String> {
        let _lifecycle = self.lifecycle_lock.lock().await;
        if !self.dispatching.load(Ordering::Acquire) {
            return Err("auth runtime is shutting down".into());
        }
        let journal = Arc::clone(&self.journal);
        let grants = tokio::task::spawn_blocking(move || journal.grants())
            .await
            .map_err(|_| "auth control reader failed")?
            .map_err(|_| "auth control journal unavailable")?;
        for grant in grants {
            if self
                .domains
                .read()
                .map_err(|_| "auth registry lock failed")?
                .contains_key(&grant.group_id)
            {
                continue;
            }
            let domain = self.recover_domain(&grant).await?;
            self.domains
                .write()
                .map_err(|_| "auth registry lock failed")?
                .insert(grant.group_id.clone(), Arc::new(domain));
        }
        Ok(())
    }

    async fn recover_domain(&self, grant: &AuthGroupBootstrapGrant) -> Result<Domain, String> {
        let local_node = grant
            .voters
            .iter()
            .find(|voter| voter.member == self.production.environment.member.to_text())
            .ok_or("auth local voter missing")?
            .node_id;
        let database = self.database_for(grant)?;
        let store = self.open_store(grant, database).await?;
        let voters = grant
            .voters
            .iter()
            .map(|voter| {
                terrabase_cluster::production::MemberWorkloadIdentity::parse(&voter.spiffe_uri)
                    .map(|identity| (voter.node_id, identity.member().clone()))
                    .map_err(|_| "auth voter identity invalid")
            })
            .collect::<Result<BTreeMap<_, _>, _>>()?;
        let placement = grant
            .regions
            .iter()
            .map(|region| ProductionRegion::try_new(region).map_err(|_| "auth placement invalid"))
            .collect::<Result<BTreeSet<_>, _>>()?;
        let network =
            AuthNetwork::new(Arc::clone(&self.endpoint), &grant.group_id, voters, &placement)
                .map_err(|_| "auth transport admission mismatch")?;
        let authority = AuthAuthority::recover_admitted(
            store,
            network.clone(),
            terrabase_auth::consensus_config(&grant.group_id),
            Duration::from_secs(10),
        )
        .await
        .map_err(|_| "auth consensus recovery failed")?;
        if local_node == grant.genesis_node
            && !authority.is_initialized().await.map_err(|_| "auth consensus status unavailable")?
        {
            authority
                .initialize(network.initial_nodes())
                .await
                .map_err(|_| "auth authorized genesis failed")?;
        }
        Ok(Domain { authority, network, grant: grant.clone() })
    }

    fn database_for(
        &self,
        grant: &AuthGroupBootstrapGrant,
    ) -> Result<crate::config::ProductionDatabase, String> {
        self.production
            .databases
            .iter()
            .find(|database| {
                database.tenant.to_text() == grant.tenant
                    && database.project.to_text() == grant.project
                    && database.database.to_text() == grant.database
                    && database.enabled
            })
            .cloned()
            .ok_or("auth configured database missing".into())
    }

    async fn open_store(
        &self,
        grant: &AuthGroupBootstrapGrant,
        database: crate::config::ProductionDatabase,
    ) -> Result<SqliteStore, String> {
        let group = grant.group_id.clone();
        let journal = Arc::clone(&self.journal);
        let keys = self.production.keys.clone().ok_or("auth key delivery missing")?;
        let environment = self.production.environment.name.as_str().to_owned();
        Ok(tokio::task::spawn_blocking(move || {
            use terrabase::encryption::{KeyPurpose, KeyScope, KeyVersionMetadata};
            use terrabase::encryption::{OciVaultDatabaseKeys, OciVaultKeyDelivery};
            let provider =
                OciVaultKeyDelivery::new(keys.socket, keys.orchestrator_uid, keys.socket_gid)
                    .map_err(|_| "auth key delivery unavailable")?;
            let scope = KeyScope::new_with_project(
                environment,
                database.tenant,
                database.project,
                database.database,
                KeyPurpose::Data,
            )
            .map_err(|_| "auth key scope invalid")?;
            let source = OciVaultDatabaseKeys::new(
                provider,
                KeyVersionMetadata::new(scope, database.encryption_key),
                database.region,
            )
            .map_err(|_| "auth key lease unavailable")?;
            std::fs::create_dir_all(&database.location)
                .map_err(|_| "auth directory unavailable")?;
            let create = journal
                .reserve_state_creation(&group)
                .map_err(|_| "auth state creation reservation failed")?;
            SqliteStore::open_encrypted(
                &database.location.join("auth-raft.sqlite"),
                &group,
                Arc::new(source),
                create,
            )
            .map_err(|_| "auth encrypted state unavailable or identity mismatch")
        })
        .await
        .map_err(|_| "auth state worker unavailable")??)
    }

    /// Serve authenticated connections when this runtime owns the member accept loop.
    /// After stopping the receiver, the owner must call `shutdown` to drain dispatched work.
    ///
    /// # Errors
    /// Returns a transport failure; no alternate local consensus is created.
    pub async fn receive(self: Arc<Self>) -> Result<(), String> {
        let mut work = tokio::task::JoinSet::new();
        loop {
            if work.len() >= 32 {
                let _ = work.join_next().await;
            }
            let connection = match self.endpoint.accept().await {
                Ok(Some(connection)) => connection,
                Ok(None) | Err(ProductionTransportError::Draining) => break,
                Err(
                    ProductionTransportError::Timeout
                    | ProductionTransportError::ConnectionFailed
                    | ProductionTransportError::PeerNotAdmitted
                    | ProductionTransportError::InvalidPeerCertificate
                    | ProductionTransportError::IdentityMismatch
                    | ProductionTransportError::ProtocolMismatch
                    | ProductionTransportError::StaleMembership
                    | ProductionTransportError::Replay,
                ) => continue,
                Err(ProductionTransportError::Saturated) => {
                    tokio::time::sleep(Duration::from_millis(10)).await;
                    continue;
                }
                Err(_) => return Err("auth consensus listener unavailable".into()),
            };
            let runtime = Arc::clone(&self);
            work.spawn(async move {
                let Ok(bytes) = connection.receive_frame().await else {
                    return;
                };
                let _ = runtime.dispatch_owned(connection, bytes);
            });
            while work.try_join_next().is_some() {}
        }
        while work.join_next().await.is_some() {}
        Ok(())
    }

    async fn drain_dispatchers(&self) {
        let mut client_work =
            self.client_work.lock().map(|mut work| std::mem::take(&mut *work)).ok();
        if let Some(ref mut work) = client_work {
            while work.join_next().await.is_some() {}
        }
        let mut consensus_work =
            self.consensus_work.lock().map(|mut work| std::mem::take(&mut *work)).ok();
        if let Some(ref mut work) = consensus_work {
            while work.join_next().await.is_some() {}
        }
    }

    fn dispatch_owned(
        &self,
        connection: ProductionConnection,
        bytes: Vec<u8>,
    ) -> Result<bool, String> {
        let Ok((group, forwarded_client)) = AuthNetwork::frame_route(&bytes) else {
            return Ok(false);
        };
        let Some(domain) =
            self.domains.read().ok().and_then(|domains| domains.get(&group).cloned())
        else {
            return Ok(false);
        };
        if !self.dispatching.load(Ordering::Acquire) {
            return Err("auth dispatch is shutting down".into());
        }
        let expected = if forwarded_client {
            ProductionConnectionClass::AuthClient
        } else {
            ProductionConnectionClass::AuthConsensus
        };
        if connection.class() != expected {
            return Ok(false);
        }
        let (slots, work) = if forwarded_client {
            (&self.client_slots, &self.client_work)
        } else {
            (&self.consensus_slots, &self.consensus_work)
        };
        let permit = slots
            .clone()
            .try_acquire_owned()
            .map_err(|_| "auth dispatch capacity exhausted".to_owned())?;
        let mut work = work.lock().map_err(|_| "auth dispatch registry unavailable")?;
        if !self.dispatching.load(Ordering::Acquire) {
            return Err("auth dispatch is shutting down".into());
        }
        while work.try_join_next().is_some() {}
        work.spawn(async move {
            let _permit = permit;
            let _ = domain.network.receive_frame(&connection, &domain.authority, &bytes).await;
        });
        Ok(true)
    }

    /// Stop all auth consensus groups before the host closes transport/key resources.
    pub async fn shutdown(&self) {
        let _lifecycle = self.lifecycle_lock.lock().await;
        self.dispatching.store(false, Ordering::Release);
        self.drain_dispatchers().await;
        // Stop publishing domains before shutdown. Authority shutdown closes
        // its shared SQLite store even when callers retain authority clones.
        let domains = self
            .domains
            .write()
            .map(|mut domains| std::mem::take(&mut *domains).into_values().collect::<Vec<_>>())
            .unwrap_or_default();
        for domain in domains {
            let _ = domain.authority.shutdown().await;
        }
    }

    fn domain(
        &self,
        database: &str,
        headers: &HeaderMap,
        verb: Verbs,
    ) -> Result<Arc<Domain>, AuthHttpError> {
        let token = headers
            .get(axum::http::header::AUTHORIZATION)
            .and_then(|value| value.to_str().ok())
            .and_then(|value| value.strip_prefix("Bearer "))
            .ok_or(AuthHttpError { status: StatusCode::UNAUTHORIZED, code: "unauthorized" })?;
        let principal = self.keys.admit(token).map_err(|_| AuthHttpError {
            status: StatusCode::UNAUTHORIZED,
            code: "unauthorized",
        })?;
        if !principal.scope().admits_database(verb, database) {
            return Err(AuthHttpError { status: StatusCode::FORBIDDEN, code: "forbidden" });
        }
        let domains = self.domains.read().map_err(|_| AuthHttpError {
            status: StatusCode::SERVICE_UNAVAILABLE,
            code: "auth_authority_unavailable",
        })?;
        unique_domain(domains.values(), database).ok_or(AuthHttpError {
            status: StatusCode::SERVICE_UNAVAILABLE,
            code: "auth_authority_unavailable",
        })
    }
}

fn unique_domain<'a>(
    domains: impl Iterator<Item = &'a Arc<Domain>>,
    database: &str,
) -> Option<Arc<Domain>> {
    let mut matches = domains.filter(|domain| domain.grant.database == database);
    let domain = matches.next()?.clone();
    matches.next().is_none().then_some(domain)
}

impl crate::replication_peers::PeerControlDispatcher for AuthRuntime {
    fn dispatch<'a>(
        &'a self,
        connection: ProductionConnection,
        bytes: Vec<u8>,
    ) -> std::pin::Pin<Box<dyn std::future::Future<Output = Result<bool, String>> + Send + 'a>>
    {
        Box::pin(async move { self.dispatch_owned(connection, bytes) })
    }
}

struct AuthHttpError {
    status: StatusCode,
    code: &'static str,
}

impl IntoResponse for AuthHttpError {
    fn into_response(self) -> Response {
        error(self.status, self.code)
    }
}

fn error(status: StatusCode, code: &str) -> Response {
    (status,Json(serde_json::json!({"error":{"code":code,"message":"Auth operation could not be completed safely","fix":"Resolve uncertain mutations using the same idempotency key; retry when the admitted authority is available"}}))).into_response()
}

#[derive(Deserialize, ToSchema)]
#[serde(deny_unknown_fields, rename_all = "camelCase")]
struct UniqueInsert {
    model: String,
    record: BTreeMap<String, serde_json::Value>,
}
#[derive(Deserialize, ToSchema)]
#[serde(deny_unknown_fields, rename_all = "camelCase")]
struct ConditionalUpdate {
    model: String,
    id: String,
    expected: BTreeMap<String, serde_json::Value>,
    update: BTreeMap<String, serde_json::Value>,
}
#[derive(Deserialize, ToSchema)]
#[serde(deny_unknown_fields, rename_all = "camelCase")]
struct Consume {
    model: String,
    id: String,
    expected: BTreeMap<String, serde_json::Value>,
}
#[derive(Deserialize, ToSchema)]
#[serde(deny_unknown_fields, rename_all = "camelCase")]
struct UniqueIndex {
    model: String,
    fields: Vec<String>,
}
#[derive(Deserialize, ToSchema)]
#[serde(deny_unknown_fields, rename_all = "camelCase")]
struct PredicateInput {
    field: String,
    /// Missing fields are null. Eq/ne against null are null checks; ne against
    /// a non-null value excludes null records. Empty `in`/`not_in` match none/all.
    /// Nonempty `not_in` excludes null records and matches nothing with a null list member.
    value: serde_json::Value,
    #[schema(value_type = Option<PredicateOperatorSchema>)]
    #[serde(default)]
    operator: Option<String>,
    /// All AND terms must match, plus at least one OR term when present,
    /// independent of request order (Better Auth 1.7.2 Kysely/Drizzle grouping).
    #[schema(value_type = Option<PredicateConnectorSchema>)]
    #[serde(default)]
    connector: Option<String>,
    /// Insensitive membership folds strings only for homogeneous string lists.
    #[schema(value_type = Option<PredicateModeSchema>)]
    #[serde(default)]
    mode: Option<String>,
}
#[derive(ToSchema)]
#[allow(dead_code, reason = "OpenAPI-only enum; runtime validates strings into auth predicates")]
#[serde(rename_all = "snake_case")]
enum PredicateOperatorSchema {
    Eq,
    Ne,
    Lt,
    Lte,
    Gt,
    Gte,
    In,
    NotIn,
    Contains,
    StartsWith,
    EndsWith,
}
#[derive(ToSchema)]
#[allow(dead_code, reason = "OpenAPI-only enum; runtime validates strings into auth predicates")]
#[serde(rename_all = "UPPERCASE")]
enum PredicateConnectorSchema {
    And,
    Or,
}
#[derive(ToSchema)]
#[allow(dead_code, reason = "OpenAPI-only enum; runtime validates strings into auth predicates")]
#[serde(rename_all = "lowercase")]
enum PredicateModeSchema {
    Sensitive,
    Insensitive,
}
impl PredicateInput {
    fn into_predicate(self) -> Result<terrabase_auth::Predicate, AuthHttpError> {
        serde_json::from_value(serde_json::json!({
            "field": self.field,
            "value": self.value,
            "operator": self.operator.unwrap_or_else(|| "eq".into()),
            "connector": self.connector.unwrap_or_else(|| "AND".into()),
            "mode": self.mode.unwrap_or_else(|| "sensitive".into()),
        }))
        .map_err(|_| AuthHttpError {
            status: StatusCode::BAD_REQUEST,
            code: "invalid_auth_operation",
        })
    }
}
#[derive(Deserialize, ToSchema)]
#[serde(deny_unknown_fields, rename_all = "camelCase")]
struct PredicateUpdate {
    model: String,
    predicates: Vec<PredicateInput>,
    update: BTreeMap<String, serde_json::Value>,
}
#[derive(Deserialize, ToSchema)]
#[serde(deny_unknown_fields, rename_all = "camelCase")]
struct PredicateOnly {
    model: String,
    predicates: Vec<PredicateInput>,
}
#[derive(Deserialize, ToSchema)]
#[serde(deny_unknown_fields, rename_all = "camelCase")]
struct Increment {
    model: String,
    predicates: Vec<PredicateInput>,
    #[schema(value_type = BTreeMap<String, f64>)]
    #[serde(rename = "increment")]
    increments: BTreeMap<String, serde_json::Number>,
    #[serde(default)]
    set: BTreeMap<String, serde_json::Value>,
}
#[derive(Serialize, ToSchema)]
#[serde(rename_all = "camelCase")]
struct AuthResult {
    record: Option<BTreeMap<String, serde_json::Value>>,
    matched: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    records: Option<Vec<BTreeMap<String, serde_json::Value>>>,
    #[serde(skip_serializing_if = "Option::is_none")]
    count: Option<u64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    transaction_id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    expires_at_ms: Option<u64>,
}
impl AuthResult {
    fn record(record: Option<BTreeMap<String, serde_json::Value>>, matched: bool) -> Self {
        Self {
            record,
            matched,
            records: None,
            count: None,
            transaction_id: None,
            expires_at_ms: None,
        }
    }
}

async fn mutation(
    runtime: &AuthRuntime,
    database: &str,
    headers: &HeaderMap,
    action: Action,
) -> Response {
    let domain = match runtime.domain(database, headers, Verbs::WRITE) {
        Ok(domain) => domain,
        Err(error) => return error.into_response(),
    };
    let Some(key) = headers
        .get("idempotency-key")
        .and_then(|value| value.to_str().ok())
        .filter(|key| !key.is_empty() && key.len() <= 256)
    else {
        return error(StatusCode::BAD_REQUEST, "idempotency_key_required");
    };
    let action = if let Some(transaction) = headers.get("x-terrascale-auth-transaction") {
        let Ok(transaction) = transaction.to_str() else {
            return error(StatusCode::BAD_REQUEST, "invalid_transaction_identity");
        };
        if transaction.is_empty() || transaction.len() > 128 {
            return error(StatusCode::BAD_REQUEST, "invalid_transaction_identity");
        }
        Action::TransactionOperation {
            transaction_id: transaction.into(),
            action: Box::new(action),
        }
    } else {
        action
    };
    let result = domain
        .authority
        .mutate(Command { idempotency_key: key.into(), observed_at_ms: now_ms(), action })
        .await;
    let response = match result {
        Ok(Outcome::Applied) => {
            (StatusCode::OK, Json(AuthResult::record(None, true))).into_response()
        }
        Ok(Outcome::Record { record }) => {
            (StatusCode::OK, Json(AuthResult::record(Some(record), true))).into_response()
        }
        Ok(Outcome::NotMatched) => {
            (StatusCode::OK, Json(AuthResult::record(None, false))).into_response()
        }
        Ok(Outcome::Records { records }) => {
            let mut result = AuthResult::record(None, true);
            result.records = Some(records);
            Json(result).into_response()
        }
        Ok(Outcome::Count { count }) => {
            let mut result = AuthResult::record(None, true);
            result.count = Some(count);
            Json(result).into_response()
        }
        Ok(Outcome::Transaction { transaction_id, expires_at_ms }) => {
            let mut result = AuthResult::record(None, true);
            result.transaction_id = Some(transaction_id);
            result.expires_at_ms = Some(expires_at_ms);
            Json(result).into_response()
        }
        Ok(Outcome::TransactionConflict) => error(StatusCode::CONFLICT, "transaction_conflict"),
        Ok(Outcome::UniqueConflict) => error(StatusCode::CONFLICT, "unique_conflict"),
        Ok(Outcome::IdempotencyConflict) => error(StatusCode::CONFLICT, "idempotency_conflict"),
        Ok(Outcome::Invalid { .. }) => error(StatusCode::BAD_REQUEST, "invalid_auth_operation"),
        Err(AuthorityError::Deadline) => {
            error(StatusCode::SERVICE_UNAVAILABLE, "auth_outcome_unknown")
        }
        Err(_) => error(StatusCode::SERVICE_UNAVAILABLE, "auth_authority_unavailable"),
    };
    placement_response(response, &domain.grant)
}
fn placement_response(mut response: Response, grant: &AuthGroupBootstrapGrant) -> Response {
    if let Ok(value) = grant.regions.join(",").parse() {
        response.headers_mut().insert("x-terrascale-placement", value);
    }
    response
        .headers_mut()
        .insert("cache-control", axum::http::HeaderValue::from_static("no-store"));
    response
}

fn predicates(
    inputs: Vec<PredicateInput>,
) -> Result<Vec<terrabase_auth::Predicate>, AuthHttpError> {
    inputs.into_iter().map(PredicateInput::into_predicate).collect()
}

async fn query(
    runtime: &AuthRuntime,
    database: &str,
    headers: &HeaderMap,
    action: Action,
) -> Response {
    let domain = match runtime.domain(database, headers, Verbs::READ) {
        Ok(domain) => domain,
        Err(error) => return error.into_response(),
    };
    let result = if let Some(transaction) = headers.get("x-terrascale-auth-transaction") {
        let Ok(transaction) = transaction.to_str() else {
            return placement_response(
                error(StatusCode::BAD_REQUEST, "invalid_transaction_identity"),
                &domain.grant,
            );
        };
        if transaction.is_empty() || transaction.len() > 128 {
            return placement_response(
                error(StatusCode::BAD_REQUEST, "invalid_transaction_identity"),
                &domain.grant,
            );
        }
        let Some(key) = headers
            .get("idempotency-key")
            .and_then(|value| value.to_str().ok())
            .filter(|key| !key.is_empty() && key.len() <= 256)
        else {
            return placement_response(
                error(StatusCode::BAD_REQUEST, "idempotency_key_required"),
                &domain.grant,
            );
        };
        domain
            .authority
            .mutate(Command {
                idempotency_key: key.into(),
                observed_at_ms: now_ms(),
                action: Action::TransactionOperation {
                    transaction_id: transaction.into(),
                    action: Box::new(action),
                },
            })
            .await
    } else {
        domain.authority.query(action).await
    };
    let response = match result {
        Ok(Outcome::Records { records }) => {
            let mut result = AuthResult::record(None, true);
            result.records = Some(records);
            Json(result).into_response()
        }
        Ok(Outcome::Count { count }) => {
            let mut result = AuthResult::record(None, true);
            result.count = Some(count);
            Json(result).into_response()
        }
        Ok(Outcome::TransactionConflict) => error(StatusCode::CONFLICT, "transaction_conflict"),
        Ok(Outcome::IdempotencyConflict) => error(StatusCode::CONFLICT, "idempotency_conflict"),
        Ok(Outcome::Invalid { .. }) => error(StatusCode::BAD_REQUEST, "invalid_auth_operation"),
        Err(AuthorityError::Deadline) => {
            error(StatusCode::SERVICE_UNAVAILABLE, "auth_outcome_unknown")
        }
        Err(_) | Ok(_) => error(StatusCode::SERVICE_UNAVAILABLE, "auth_authority_unavailable"),
    };
    placement_response(response, &domain.grant)
}

#[utoipa::path(post,path="/api/v1/databases/{database}/auth/insertUnique",request_body=UniqueInsert,params(("database"=String,Path),("Idempotency-Key"=String,Header),("X-TerraScale-Auth-Transaction"=Option<String>,Header,description="Optional active transaction identity; at most 128 bytes")),responses((status=200,body=AuthResult),(status=409,description="Unique or idempotency conflict"),(status=503,description="Authority unavailable or uncertain result")))]
async fn insert_unique(
    State(runtime): State<Arc<AuthRuntime>>,
    Path(database): Path<String>,
    headers: HeaderMap,
    Json(input): Json<UniqueInsert>,
) -> Response {
    mutation(
        &runtime,
        &database,
        &headers,
        Action::InsertUnique { model: input.model, record: input.record },
    )
    .await
}
#[utoipa::path(post,path="/api/v1/databases/{database}/auth/compareAndSwap",request_body=ConditionalUpdate,params(("database"=String,Path),("Idempotency-Key"=String,Header),("X-TerraScale-Auth-Transaction"=Option<String>,Header,description="Optional active transaction identity; at most 128 bytes")),responses((status=200,body=AuthResult),(status=409,description="Unique or idempotency conflict"),(status=503,description="Authority unavailable or uncertain result")))]
async fn compare_and_swap(
    State(runtime): State<Arc<AuthRuntime>>,
    Path(database): Path<String>,
    headers: HeaderMap,
    Json(input): Json<ConditionalUpdate>,
) -> Response {
    mutation(
        &runtime,
        &database,
        &headers,
        Action::CompareAndSwap {
            model: input.model,
            id: input.id,
            expected: input.expected,
            update: input.update,
        },
    )
    .await
}
#[utoipa::path(post,path="/api/v1/databases/{database}/auth/consumeOne",request_body=Consume,params(("database"=String,Path),("Idempotency-Key"=String,Header),("X-TerraScale-Auth-Transaction"=Option<String>,Header,description="Optional active transaction identity; at most 128 bytes")),responses((status=200,body=AuthResult),(status=409,description="Idempotency conflict"),(status=503,description="Authority unavailable or uncertain result")))]
async fn consume_one(
    State(runtime): State<Arc<AuthRuntime>>,
    Path(database): Path<String>,
    headers: HeaderMap,
    Json(input): Json<Consume>,
) -> Response {
    mutation(
        &runtime,
        &database,
        &headers,
        Action::ConsumeOne { model: input.model, id: input.id, expected: input.expected },
    )
    .await
}
#[utoipa::path(post,path="/api/v1/databases/{database}/auth/index",request_body=UniqueIndex,params(("database"=String,Path),("Idempotency-Key"=String,Header),("X-TerraScale-Auth-Transaction"=Option<String>,Header,description="Optional active transaction identity; at most 128 bytes")),responses((status=200,body=AuthResult),(status=409,description="Existing records violate uniqueness"),(status=503,description="Authority unavailable or uncertain result")))]
async fn index(
    State(runtime): State<Arc<AuthRuntime>>,
    Path(database): Path<String>,
    headers: HeaderMap,
    Json(input): Json<UniqueIndex>,
) -> Response {
    mutation(
        &runtime,
        &database,
        &headers,
        Action::CreateUniqueIndex { model: input.model, fields: input.fields },
    )
    .await
}
#[utoipa::path(post, path="/api/v1/databases/{database}/auth/updateOne", request_body=PredicateUpdate, params(("database"=String,Path),("Idempotency-Key"=String,Header),("X-TerraScale-Auth-Transaction"=Option<String>,Header)), responses((status=200,body=AuthResult),(status=503,description="Authority unavailable or uncertain result")))]
async fn update_one(
    State(runtime): State<Arc<AuthRuntime>>,
    Path(database): Path<String>,
    headers: HeaderMap,
    Json(input): Json<PredicateUpdate>,
) -> Response {
    let predicates = match predicates(input.predicates) {
        Ok(value) => value,
        Err(error) => return error.into_response(),
    };
    mutation(
        &runtime,
        &database,
        &headers,
        Action::UpdateOne { model: input.model, predicates, update: input.update },
    )
    .await
}
#[utoipa::path(post, path="/api/v1/databases/{database}/auth/updateMany", request_body=PredicateUpdate, params(("database"=String,Path),("Idempotency-Key"=String,Header),("X-TerraScale-Auth-Transaction"=Option<String>,Header)), responses((status=200,body=AuthResult),(status=503,description="Authority unavailable or uncertain result")))]
async fn update_many(
    State(runtime): State<Arc<AuthRuntime>>,
    Path(database): Path<String>,
    headers: HeaderMap,
    Json(input): Json<PredicateUpdate>,
) -> Response {
    let predicates = match predicates(input.predicates) {
        Ok(value) => value,
        Err(error) => return error.into_response(),
    };
    mutation(
        &runtime,
        &database,
        &headers,
        Action::UpdateMany { model: input.model, predicates, update: input.update },
    )
    .await
}
#[utoipa::path(post, path="/api/v1/databases/{database}/auth/deleteOne", request_body=PredicateOnly, params(("database"=String,Path),("Idempotency-Key"=String,Header),("X-TerraScale-Auth-Transaction"=Option<String>,Header)), responses((status=200,body=AuthResult),(status=503,description="Authority unavailable or uncertain result")))]
async fn delete_one(
    State(runtime): State<Arc<AuthRuntime>>,
    Path(database): Path<String>,
    headers: HeaderMap,
    Json(input): Json<PredicateOnly>,
) -> Response {
    let predicates = match predicates(input.predicates) {
        Ok(value) => value,
        Err(error) => return error.into_response(),
    };
    mutation(&runtime, &database, &headers, Action::DeleteOne { model: input.model, predicates })
        .await
}
#[utoipa::path(post, path="/api/v1/databases/{database}/auth/deleteMany", request_body=PredicateOnly, params(("database"=String,Path),("Idempotency-Key"=String,Header),("X-TerraScale-Auth-Transaction"=Option<String>,Header)), responses((status=200,body=AuthResult),(status=503,description="Authority unavailable or uncertain result")))]
async fn delete_many(
    State(runtime): State<Arc<AuthRuntime>>,
    Path(database): Path<String>,
    headers: HeaderMap,
    Json(input): Json<PredicateOnly>,
) -> Response {
    let predicates = match predicates(input.predicates) {
        Ok(value) => value,
        Err(error) => return error.into_response(),
    };
    mutation(&runtime, &database, &headers, Action::DeleteMany { model: input.model, predicates })
        .await
}
#[utoipa::path(post, path="/api/v1/databases/{database}/auth/incrementOne", request_body=Increment, params(("database"=String,Path),("Idempotency-Key"=String,Header),("X-TerraScale-Auth-Transaction"=Option<String>,Header)), responses((status=200,body=AuthResult),(status=503,description="Authority unavailable or uncertain result")))]
async fn increment_one(
    State(runtime): State<Arc<AuthRuntime>>,
    Path(database): Path<String>,
    headers: HeaderMap,
    Json(input): Json<Increment>,
) -> Response {
    let predicates = match predicates(input.predicates) {
        Ok(value) => value,
        Err(error) => return error.into_response(),
    };
    mutation(
        &runtime,
        &database,
        &headers,
        Action::IncrementOne {
            model: input.model,
            predicates,
            increment: input.increments,
            set: input.set,
        },
    )
    .await
}
#[utoipa::path(post, path="/api/v1/databases/{database}/auth/consumeMatching", request_body=PredicateOnly, params(("database"=String,Path),("Idempotency-Key"=String,Header),("X-TerraScale-Auth-Transaction"=Option<String>,Header)), responses((status=200,body=AuthResult),(status=503,description="Authority unavailable or uncertain result")))]
async fn consume_matching(
    State(runtime): State<Arc<AuthRuntime>>,
    Path(database): Path<String>,
    headers: HeaderMap,
    Json(input): Json<PredicateOnly>,
) -> Response {
    let predicates = match predicates(input.predicates) {
        Ok(value) => value,
        Err(error) => return error.into_response(),
    };
    mutation(
        &runtime,
        &database,
        &headers,
        Action::ConsumeMatching { model: input.model, predicates },
    )
    .await
}
#[utoipa::path(post, path="/api/v1/databases/{database}/auth/count", request_body=PredicateOnly, params(("database"=String,Path),("Idempotency-Key"=Option<String>,Header,description="Required only with an active transaction; reuse the same key for retries"),("X-TerraScale-Auth-Transaction"=Option<String>,Header)), responses((status=200,body=AuthResult),(status=503,description="Authority unavailable")))]
async fn count(
    State(runtime): State<Arc<AuthRuntime>>,
    Path(database): Path<String>,
    headers: HeaderMap,
    Json(input): Json<PredicateOnly>,
) -> Response {
    let predicates = match predicates(input.predicates) {
        Ok(value) => value,
        Err(error) => return error.into_response(),
    };
    query(&runtime, &database, &headers, Action::Count { model: input.model, predicates }).await
}
#[utoipa::path(get,path="/api/v1/databases/{database}/auth/records/{model}/{id}",params(("database"=String,Path),("model"=String,Path),("id"=String,Path),("Idempotency-Key"=Option<String>,Header),("X-TerraScale-Auth-Transaction"=Option<String>,Header)),responses((status=200,body=AuthResult),(status=503,description="Quorum-confirmed read unavailable")))]
async fn find_one(
    State(runtime): State<Arc<AuthRuntime>>,
    Path((database, model, id)): Path<(String, String, String)>,
    headers: HeaderMap,
) -> Response {
    let domain = match runtime.domain(&database, &headers, Verbs::READ) {
        Ok(domain) => domain,
        Err(error) => return error.into_response(),
    };
    if let Some(transaction) = headers.get("x-terrascale-auth-transaction") {
        let Ok(transaction) = transaction.to_str() else {
            return placement_response(
                error(StatusCode::BAD_REQUEST, "invalid_transaction_identity"),
                &domain.grant,
            );
        };
        if transaction.is_empty() || transaction.len() > 128 {
            return placement_response(
                error(StatusCode::BAD_REQUEST, "invalid_transaction_identity"),
                &domain.grant,
            );
        }
        let Some(key) = headers
            .get("idempotency-key")
            .and_then(|value| value.to_str().ok())
            .filter(|key| !key.is_empty() && key.len() <= 256)
        else {
            return placement_response(
                error(StatusCode::BAD_REQUEST, "idempotency_key_required"),
                &domain.grant,
            );
        };
        let action = Action::TransactionOperation {
            transaction_id: transaction.into(),
            action: Box::new(Action::Find {
                model,
                predicates: vec![Predicate {
                    field: "id".into(),
                    value: serde_json::Value::String(id),
                    operator: Operator::Eq,
                    connector: Connector::AND,
                    mode: Mode::Sensitive,
                }],
                offset: 0,
                limit: 1,
                select: None,
                sort: None,
                joins: Vec::new(),
            }),
        };
        let response = match domain
            .authority
            .mutate(Command { idempotency_key: key.into(), observed_at_ms: now_ms(), action })
            .await
        {
            Ok(Outcome::Records { mut records }) => {
                let record = records.pop();
                let matched = record.is_some();
                Json(AuthResult::record(record, matched)).into_response()
            }
            Ok(Outcome::TransactionConflict) => error(StatusCode::CONFLICT, "transaction_conflict"),
            Ok(Outcome::IdempotencyConflict) => error(StatusCode::CONFLICT, "idempotency_conflict"),
            Ok(Outcome::Invalid { .. }) => error(StatusCode::BAD_REQUEST, "invalid_auth_operation"),
            Err(AuthorityError::Deadline) => {
                error(StatusCode::SERVICE_UNAVAILABLE, "auth_outcome_unknown")
            }
            Err(_) | Ok(_) => error(StatusCode::SERVICE_UNAVAILABLE, "auth_authority_unavailable"),
        };
        return placement_response(response, &domain.grant);
    }
    let response = match domain.authority.find_one(model, id).await {
        Ok(record) => Json(AuthResult::record(record.clone(), record.is_some())).into_response(),
        Err(_) => error(StatusCode::SERVICE_UNAVAILABLE, "auth_authority_unavailable"),
    };
    placement_response(response, &domain.grant)
}

/// Actual public routes and their producer-owned `OpenAPI` document.
pub fn routes() -> utoipa_axum::router::OpenApiRouter<Arc<AuthRuntime>> {
    use utoipa_axum::{router::OpenApiRouter, routes};
    OpenApiRouter::new()
        .routes(routes!(insert_unique))
        .routes(routes!(compare_and_swap))
        .routes(routes!(consume_one))
        .routes(routes!(index))
        .routes(routes!(update_one))
        .routes(routes!(update_many))
        .routes(routes!(delete_one))
        .routes(routes!(delete_many))
        .routes(routes!(increment_one))
        .routes(routes!(consume_matching))
        .routes(routes!(count))
        .routes(routes!(find_one))
        .routes(routes!(find_page))
        .routes(routes!(begin_transaction))
        .routes(routes!(commit_transaction))
        .routes(routes!(rollback_transaction))
        .layer(axum::middleware::map_response(auth_no_store))
}

async fn auth_no_store(mut response: Response) -> Response {
    // Also covers extractor failures before a handler can attach placement.
    response.headers_mut().insert(
        axum::http::header::CACHE_CONTROL,
        axum::http::HeaderValue::from_static("no-store"),
    );
    response
}
/// Build the real public auth router with bounded inputs and no cacheable private output.
pub fn router(runtime: Arc<AuthRuntime>) -> Router {
    let (router, _) = routes().split_for_parts();
    router.layer(DefaultBodyLimit::max(1024 * 1024)).with_state(runtime)
}

fn now_ms() -> u64 {
    match std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH) {
        Ok(value) => u64::try_from(value.as_millis()).unwrap_or(u64::MAX),
        Err(_) => 0,
    }
}

#[cfg(test)]
pub(crate) mod tests;

#[cfg(all(test, target_os = "linux"))]
#[path = "auth_runtime/four_node_tests.rs"]
mod four_node_tests;

#[derive(Deserialize, ToSchema)]
#[serde(deny_unknown_fields, rename_all = "camelCase")]
struct TransactionIdentity {
    /// Begin accepts a label of at most 100 UTF-8 bytes. Commit and rollback
    /// require the returned canonical identity, which is at most 128 bytes.
    transaction_id: String,
}
#[derive(Deserialize, ToSchema)]
#[serde(deny_unknown_fields, rename_all = "camelCase")]
struct PageQuery {
    model: String,
    predicates: Vec<PredicateInput>,
    offset: u32,
    limit: u32,
    /// Return these base fields after sorting; absent selects the full record.
    #[serde(default)]
    select: Option<Vec<String>>,
    /// Deterministic scalar order with ascending ID ties.
    #[serde(default)]
    sort_by: Option<PageSort>,
    /// At most eight explicit relations, read from the same authority snapshot.
    #[serde(default)]
    joins: Vec<PageJoin>,
}
#[derive(Deserialize, ToSchema)]
#[serde(deny_unknown_fields, rename_all = "camelCase")]
struct PageJoin {
    model: String,
    output_field: String,
    from_field: String,
    to_field: String,
    relation: PageJoinRelation,
    limit: u32,
}
#[derive(Deserialize, ToSchema)]
#[serde(rename_all = "kebab-case")]
enum PageJoinRelation {
    OneToOne,
    OneToMany,
}
#[derive(Deserialize, ToSchema)]
#[serde(deny_unknown_fields)]
struct PageSort {
    field: String,
    direction: PageSortDirection,
}
#[derive(Deserialize, ToSchema)]
#[serde(rename_all = "lowercase")]
enum PageSortDirection {
    Asc,
    Desc,
}
#[utoipa::path(post,path="/api/v1/databases/{database}/auth/findPage",request_body=PageQuery,params(("database"=String,Path),("Idempotency-Key"=Option<String>,Header,description="Required only with an active transaction; reuse the same key for retries"),("X-TerraScale-Auth-Transaction"=Option<String>,Header)),responses((status=200,body=AuthResult),(status=503,description="Authority unavailable")))]
async fn find_page(
    State(runtime): State<Arc<AuthRuntime>>,
    Path(database): Path<String>,
    headers: HeaderMap,
    Json(input): Json<PageQuery>,
) -> Response {
    let predicates = match predicates(input.predicates) {
        Ok(predicates) => predicates,
        Err(error) => return error.into_response(),
    };
    query(
        &runtime,
        &database,
        &headers,
        Action::Find {
            model: input.model,
            predicates,
            offset: input.offset,
            limit: input.limit,
            select: input.select,
            joins: input
                .joins
                .into_iter()
                .map(|join| QueryJoin {
                    model: join.model,
                    output_field: join.output_field,
                    from_field: join.from_field,
                    to_field: join.to_field,
                    relation: match join.relation {
                        PageJoinRelation::OneToOne => JoinRelation::OneToOne,
                        PageJoinRelation::OneToMany => JoinRelation::OneToMany,
                    },
                    limit: join.limit,
                })
                .collect(),
            sort: input.sort_by.map(|sort| QuerySort {
                field: sort.field,
                direction: match sort.direction {
                    PageSortDirection::Asc => SortDirection::Asc,
                    PageSortDirection::Desc => SortDirection::Desc,
                },
            }),
        },
    )
    .await
}
#[utoipa::path(post,path="/api/v1/databases/{database}/auth/transactions/begin",request_body=TransactionIdentity,params(("database"=String,Path),("Idempotency-Key"=String,Header)),responses((status=200,body=AuthResult),(status=409,description="Transaction identity conflict"),(status=503,description="Authority unavailable")))]
async fn begin_transaction(
    State(runtime): State<Arc<AuthRuntime>>,
    Path(database): Path<String>,
    headers: HeaderMap,
    Json(input): Json<TransactionIdentity>,
) -> Response {
    mutation(
        &runtime,
        &database,
        &headers,
        Action::BeginTransaction { transaction_id: input.transaction_id },
    )
    .await
}
#[utoipa::path(post,path="/api/v1/databases/{database}/auth/transactions/commit",request_body=TransactionIdentity,params(("database"=String,Path),("Idempotency-Key"=String,Header)),responses((status=200,body=AuthResult),(status=409,description="Transaction failed, expired, or conflicted"),(status=503,description="Commit outcome unknown; resolve same identity")))]
async fn commit_transaction(
    State(runtime): State<Arc<AuthRuntime>>,
    Path(database): Path<String>,
    headers: HeaderMap,
    Json(input): Json<TransactionIdentity>,
) -> Response {
    mutation(
        &runtime,
        &database,
        &headers,
        Action::CommitTransaction { transaction_id: input.transaction_id },
    )
    .await
}
#[utoipa::path(post,path="/api/v1/databases/{database}/auth/transactions/rollback",request_body=TransactionIdentity,params(("database"=String,Path),("Idempotency-Key"=String,Header)),responses((status=200,body=AuthResult),(status=503,description="Authority unavailable")))]
async fn rollback_transaction(
    State(runtime): State<Arc<AuthRuntime>>,
    Path(database): Path<String>,
    headers: HeaderMap,
    Json(input): Json<TransactionIdentity>,
) -> Response {
    mutation(
        &runtime,
        &database,
        &headers,
        Action::RollbackTransaction { transaction_id: input.transaction_id },
    )
    .await
}
