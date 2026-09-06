//! Encrypted strict auth consensus and public operations, admitted by GRU bootstrap.
//! Customer records never pass through the control journal or a Worker.

use std::collections::{BTreeMap,BTreeSet};
use std::sync::{Arc,RwLock};
use std::time::Duration;

use axum::{Router,Json};
use axum::extract::{State,Path,DefaultBodyLimit};
use axum::http::{HeaderMap,StatusCode};
use axum::response::{IntoResponse,Response};
use serde::{Serialize,Deserialize};
use utoipa::ToSchema;
use terrabase_auth::{Action,AuthAuthority,AuthNetwork,AuthorityError,Command,Outcome,SqliteStore};
use terrabase_cluster::production::{ProductionQuicEndpoint,ProductionRegion};
use terrabase_tbep::auth_bootstrap::AuthBootstrapJournal;
use terrabase_tbep::proto::terrabase::v1::AuthGroupBootstrapGrant;

use crate::api_keys::{KeyStore,Scope,Verbs};
use crate::config::{ProductionConfig,PublicApiConfig};

struct Domain {
    authority:AuthAuthority,
    network:AuthNetwork,
    grant:AuthGroupBootstrapGrant,
}

/// Admitted, active auth groups. An absent grant never creates local auth state.
pub struct AuthRuntime {
    endpoint:Arc<ProductionQuicEndpoint>,
    journal:Arc<AuthBootstrapJournal>,
    production:ProductionConfig,
    domains:RwLock<BTreeMap<String,Arc<Domain>>>,
    keys:KeyStore,
}

impl AuthRuntime {
    /// Create a runtime with verified transport and explicit customer API key material.
    /// This does not allocate an auth group before its authenticated control grant.
    ///
    /// # Errors
    /// Refuses missing protected credentials or missing encryption delivery configuration.
    pub fn new(endpoint:Arc<ProductionQuicEndpoint>,journal:Arc<AuthBootstrapJournal>,production:ProductionConfig,public:&PublicApiConfig)->Result<Arc<Self>,String> {
        if production.keys.is_none() { return Err("auth database encryption delivery is missing".into()); }
        let key_path=public.bootstrap_key_file.as_deref().ok_or("auth public API credential file missing")?;
        let bytes=crate::runtime::read_secret_key(key_path).map_err(|_|"auth public API credential file invalid")?;
        let key=String::from_utf8(bytes).map_err(|_|"auth public API credential encoding invalid")?;
        let key=key.trim();
        if key.len()<32 { return Err("auth public API credential is too short".into()); }
        let keys=KeyStore::empty();
        keys.register(key,"bootstrap",Scope::every_database(Verbs::ALL));
        Ok(Arc::new(Self { endpoint,journal,production,domains:RwLock::new(BTreeMap::new()),keys }))
    }

    /// Run the granted auth groups, public listener, and bounded grant reconciliation.
    /// Any failed serving task stops this auth runtime; it never creates replacement state.
    ///
    /// # Errors
    /// Refuses listener failures or loss of required runtime dependencies.
    pub async fn serve(self:Arc<Self>,listen:std::net::SocketAddr,shutdown:impl std::future::Future<Output=()>+Send+'static)->Result<(),String> {
        self.reconcile().await?;
        let listener=tokio::net::TcpListener::bind(listen).await.map_err(|_|"auth public listener cannot bind")?;
        let (stop,signal)=tokio::sync::oneshot::channel();
        let app=router(Arc::clone(&self));
        let mut http=tokio::spawn(async move { axum::serve(listener,app).with_graceful_shutdown(async { let _=signal.await; }).await });
        let mut receive=tokio::spawn(Arc::clone(&self).receive());
        let mut interval=tokio::time::interval(Duration::from_secs(1));
        tokio::pin!(shutdown);
        let outcome=loop {
            tokio::select! {
                ()=&mut shutdown=>break Ok(()),
                _=&mut http=>break Err("auth public listener stopped".into()),
                _=&mut receive=>break Err("auth consensus listener stopped".into()),
                _=interval.tick()=> {
                    // Grant arrival after control startup activates its exact persisted group.
                    // Refused activation stays unavailable and is retried without reissuing genesis.
                    if self.reconcile().await.is_err() { eprintln!("auth group activation refused; inspect admitted grant, state, and key delivery"); }
                }
            }
        };
        let _=stop.send(());
        receive.abort();
        self.shutdown().await;
        if !http.is_finished() { let _=http.await; }
        outcome
    }

    /// Recover every explicit grant once; failures leave that group unavailable.
    ///
    /// # Errors
    /// Refuses lost auth files, mismatched grants, missing keys, or consensus startup failure.
    pub async fn reconcile(&self)->Result<(),String> {
        let journal=Arc::clone(&self.journal);
        let grants=tokio::task::spawn_blocking(move ||journal.grants()).await.map_err(|_|"auth control reader failed")?.map_err(|_|"auth control journal unavailable")?;
        for grant in grants {
            if self.domains.read().map_err(|_|"auth registry lock failed")?.contains_key(&grant.group_id) { continue; }
            let local=grant.voters.iter().find(|voter|voter.member==self.production.environment.member.to_text()).ok_or("auth local voter missing")?;
            let local_node=local.node_id;
            let database=self.production.databases.iter().find(|database|database.tenant.to_text()==grant.tenant && database.project.to_text()==grant.project && database.database.to_text()==grant.database && database.enabled).ok_or("auth configured database missing")?.clone();
            let group=grant.group_id.clone();
            let journal=Arc::clone(&self.journal);
            let keys=self.production.keys.clone().ok_or("auth key delivery missing")?;
            let environment=self.production.environment.name.as_str().to_owned();
            let store=tokio::task::spawn_blocking(move || {
                use terrabase::encryption::{KeyPurpose,KeyScope,KeyVersionMetadata};
                use terrabase::encryption::key_delivery::{OciVaultDatabaseKeys,OciVaultKeyDelivery};
                let provider=OciVaultKeyDelivery::new(keys.socket,keys.orchestrator_uid,keys.socket_gid).map_err(|_|"auth key delivery unavailable")?;
                let scope=KeyScope::new_with_project(environment,database.tenant,database.project,database.database,KeyPurpose::Data).map_err(|_|"auth key scope invalid")?;
                let source=OciVaultDatabaseKeys::new(provider,KeyVersionMetadata::new(scope,database.encryption_key),database.region).map_err(|_|"auth key lease unavailable")?;
                let path=database.location.join("auth-raft.sqlite");
                std::fs::create_dir_all(&database.location).map_err(|_|"auth directory unavailable")?;
                let create=journal.reserve_state_creation(&group).map_err(|_|"auth state creation reservation failed")?;
                SqliteStore::open_encrypted(&path,&group,Arc::new(source),create).map_err(|_|"auth encrypted state unavailable or identity mismatch")
            }).await.map_err(|_|"auth state worker unavailable")??;
            let voters=grant.voters.iter().map(|voter| {
                terrabase_cluster::production::MemberWorkloadIdentity::parse(&voter.spiffe_uri).map(|identity|(voter.node_id,identity.member().clone())).map_err(|_|"auth voter identity invalid")
            }).collect::<Result<BTreeMap<_,_>,_>>()?;
            let placement=grant.regions.iter().map(|region|ProductionRegion::try_new(region).map_err(|_|"auth placement invalid")).collect::<Result<BTreeSet<_>,_>>()?;
            let network=AuthNetwork::new(Arc::clone(&self.endpoint),&grant.group_id,voters,&placement).map_err(|_|"auth transport admission mismatch")?;
            let config=terrabase_auth::consensus_config(&grant.group_id);
            let authority=AuthAuthority::recover_admitted(store,network.clone(),config,Duration::from_secs(10)).await.map_err(|_|"auth consensus recovery failed")?;
            if local_node==grant.genesis_node && !authority.is_initialized().await.map_err(|_|"auth consensus status unavailable")? {
                authority.initialize(network.initial_nodes()).await.map_err(|_|"auth authorized genesis failed")?;
            }
            self.domains.write().map_err(|_|"auth registry lock failed")?.insert(grant.group_id.clone(),Arc::new(Domain { authority,network,grant }));
        }
        Ok(())
    }

    /// Serve authenticated consensus connections on the dedicated member lane.
    ///
    /// # Errors
    /// Returns a transport failure; no alternate local consensus is created.
    pub async fn receive(self:Arc<Self>)->Result<(),String> {
        let mut work=tokio::task::JoinSet::new();
        loop {
            if work.len()>=32 { let _=work.join_next().await; }
            let Some(connection)=self.endpoint.accept().await.map_err(|_|"auth consensus listener unavailable")? else { break; };
            let runtime=Arc::clone(&self);
            work.spawn(async move {
                let Ok(bytes)=connection.receive_frame().await else { return; };
                let Ok(group)=AuthNetwork::frame_group(&bytes) else { return; };
                let domain=runtime.domains.read().ok().and_then(|domains|domains.get(&group).cloned());
                if let Some(domain)=domain { let _=domain.network.receive_frame(&connection,&domain.authority,&bytes).await; }
            });
            while work.try_join_next().is_some() {}
        }
        while work.join_next().await.is_some() {}
        Ok(())
    }

    /// Stop all auth consensus groups before the host closes transport/key resources.
    pub async fn shutdown(&self) {
        let domains=self.domains.read().map(|domains|domains.values().cloned().collect::<Vec<_>>()).unwrap_or_default();
        for domain in domains { let _=domain.authority.shutdown().await; }
    }

    fn domain(&self,database:&str,headers:&HeaderMap,verb:Verbs)->Result<Arc<Domain>,Response> {
        let token=headers.get(axum::http::header::AUTHORIZATION).and_then(|value|value.to_str().ok()).and_then(|value|value.strip_prefix("Bearer ")).ok_or_else(||error(StatusCode::UNAUTHORIZED,"unauthorized"))?;
        let principal=self.keys.admit(token).map_err(|_|error(StatusCode::UNAUTHORIZED,"unauthorized"))?;
        if !principal.scope().admits_database(verb,database) { return Err(error(StatusCode::FORBIDDEN,"forbidden")); }
        self.domains.read().map_err(|_|error(StatusCode::SERVICE_UNAVAILABLE,"auth_authority_unavailable"))?.values().find(|domain|domain.grant.database==database).cloned().ok_or_else(||error(StatusCode::SERVICE_UNAVAILABLE,"auth_authority_unavailable"))
    }
}

fn error(status:StatusCode,code:&str)->Response {
    (status,Json(serde_json::json!({"error":{"code":code,"message":"Auth operation could not be completed safely","fix":"Resolve uncertain mutations using the same idempotency key; retry when the admitted authority is available"}}))).into_response()
}

#[derive(Deserialize,ToSchema)]
#[serde(deny_unknown_fields,rename_all="camelCase")]
struct UniqueInsert { model:String,record:BTreeMap<String,serde_json::Value> }
#[derive(Deserialize,ToSchema)]
#[serde(deny_unknown_fields,rename_all="camelCase")]
struct ConditionalUpdate { model:String,id:String,expected:BTreeMap<String,serde_json::Value>,update:BTreeMap<String,serde_json::Value> }
#[derive(Deserialize,ToSchema)]
#[serde(deny_unknown_fields,rename_all="camelCase")]
struct Consume { model:String,id:String,expected:BTreeMap<String,serde_json::Value> }
#[derive(Deserialize,ToSchema)]
#[serde(deny_unknown_fields,rename_all="camelCase")]
struct UniqueIndex { model:String,fields:Vec<String> }
#[derive(Serialize,ToSchema)]
#[serde(rename_all="camelCase")]
struct AuthResult { record:Option<BTreeMap<String,serde_json::Value>>,matched:bool }

async fn mutation(runtime:&AuthRuntime,database:&str,headers:&HeaderMap,action:Action)->Response {
    let domain=match runtime.domain(database,headers,Verbs::WRITE) { Ok(domain)=>domain,Err(response)=>return response };
    let Some(key)=headers.get("idempotency-key").and_then(|value|value.to_str().ok()).filter(|key|!key.is_empty() && key.len()<=256) else { return error(StatusCode::BAD_REQUEST,"idempotency_key_required"); };
    let result=domain.authority.mutate(Command { idempotency_key:key.into(),observed_at_ms:now_ms(),action }).await;
    let response=match result {
        Ok(Outcome::Applied)=>(StatusCode::OK,Json(AuthResult { record:None,matched:true })).into_response(),
        Ok(Outcome::Record { record })=>(StatusCode::OK,Json(AuthResult { record:Some(record),matched:true })).into_response(),
        Ok(Outcome::NotMatched)=>(StatusCode::OK,Json(AuthResult { record:None,matched:false })).into_response(),
        Ok(Outcome::UniqueConflict)=>error(StatusCode::CONFLICT,"unique_conflict"),
        Ok(Outcome::IdempotencyConflict)=>error(StatusCode::CONFLICT,"idempotency_conflict"),
        Ok(Outcome::Invalid { .. })=>error(StatusCode::BAD_REQUEST,"invalid_auth_operation"),
        Err(AuthorityError::Deadline)=>error(StatusCode::SERVICE_UNAVAILABLE,"auth_outcome_unknown"),
        Err(_)=>error(StatusCode::SERVICE_UNAVAILABLE,"auth_authority_unavailable"),
    };
    placement_response(response,&domain.grant)
}
fn placement_response(mut response:Response,grant:&AuthGroupBootstrapGrant)->Response {
    if let Ok(value)=grant.regions.join(",").parse() { response.headers_mut().insert("x-terrascale-placement",value); }
    response.headers_mut().insert("cache-control",axum::http::HeaderValue::from_static("no-store"));
    response
}

#[utoipa::path(post,path="/api/v1/databases/{database}/auth/insertUnique",request_body=UniqueInsert,params(("database"=String,Path),("Idempotency-Key"=String,Header)),responses((status=200,body=AuthResult),(status=409,description="Unique or idempotency conflict"),(status=503,description="Authority unavailable or uncertain result")))]
async fn insert_unique(State(runtime):State<Arc<AuthRuntime>>,Path(database):Path<String>,headers:HeaderMap,Json(input):Json<UniqueInsert>)->Response {
    mutation(&runtime,&database,&headers,Action::InsertUnique { model:input.model,record:input.record }).await
}
#[utoipa::path(post,path="/api/v1/databases/{database}/auth/compareAndSwap",request_body=ConditionalUpdate,params(("database"=String,Path),("Idempotency-Key"=String,Header)),responses((status=200,body=AuthResult),(status=409,description="Unique or idempotency conflict"),(status=503,description="Authority unavailable or uncertain result")))]
async fn compare_and_swap(State(runtime):State<Arc<AuthRuntime>>,Path(database):Path<String>,headers:HeaderMap,Json(input):Json<ConditionalUpdate>)->Response {
    mutation(&runtime,&database,&headers,Action::CompareAndSwap { model:input.model,id:input.id,expected:input.expected,update:input.update }).await
}
#[utoipa::path(post,path="/api/v1/databases/{database}/auth/consumeOne",request_body=Consume,params(("database"=String,Path),("Idempotency-Key"=String,Header)),responses((status=200,body=AuthResult),(status=409,description="Idempotency conflict"),(status=503,description="Authority unavailable or uncertain result")))]
async fn consume_one(State(runtime):State<Arc<AuthRuntime>>,Path(database):Path<String>,headers:HeaderMap,Json(input):Json<Consume>)->Response {
    mutation(&runtime,&database,&headers,Action::ConsumeOne { model:input.model,id:input.id,expected:input.expected }).await
}
#[utoipa::path(post,path="/api/v1/databases/{database}/auth/index",request_body=UniqueIndex,params(("database"=String,Path),("Idempotency-Key"=String,Header)),responses((status=200,body=AuthResult),(status=409,description="Existing records violate uniqueness"),(status=503,description="Authority unavailable or uncertain result")))]
async fn index(State(runtime):State<Arc<AuthRuntime>>,Path(database):Path<String>,headers:HeaderMap,Json(input):Json<UniqueIndex>)->Response {
    mutation(&runtime,&database,&headers,Action::CreateUniqueIndex { model:input.model,fields:input.fields }).await
}
#[utoipa::path(get,path="/api/v1/databases/{database}/auth/records/{model}/{id}",params(("database"=String,Path),("model"=String,Path),("id"=String,Path)),responses((status=200,body=AuthResult),(status=503,description="Quorum-confirmed read unavailable")))]
async fn find_one(State(runtime):State<Arc<AuthRuntime>>,Path((database,model,id)):Path<(String,String,String)>,headers:HeaderMap)->Response {
    let domain=match runtime.domain(&database,&headers,Verbs::READ) { Ok(domain)=>domain,Err(response)=>return response };
    let response=match domain.authority.find_one(model,id).await {
        Ok(record)=>Json(AuthResult { matched:record.is_some(),record }).into_response(),
        Err(_)=>error(StatusCode::SERVICE_UNAVAILABLE,"auth_authority_unavailable"),
    };
    placement_response(response,&domain.grant)
}

/// Actual public routes and their producer-owned OpenAPI document.
pub fn routes()->utoipa_axum::router::OpenApiRouter<Arc<AuthRuntime>> {
    use utoipa_axum::{router::OpenApiRouter,routes};
    OpenApiRouter::new().routes(routes!(insert_unique)).routes(routes!(compare_and_swap)).routes(routes!(consume_one)).routes(routes!(index)).routes(routes!(find_one))
}
/// Build the real public auth router with bounded inputs and no cacheable private output.
pub fn router(runtime:Arc<AuthRuntime>)->Router {
    let (router,_)=routes().split_for_parts();
    router.layer(DefaultBodyLimit::max(1024*1024)).with_state(runtime)
}

fn now_ms()->u64 { std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map(|value|u64::try_from(value.as_millis()).unwrap_or(u64::MAX)).unwrap_or(0) }
