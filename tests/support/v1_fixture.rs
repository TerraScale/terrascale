//! An in-memory TerraBase v1 server speaking the frozen wire contracts in
//! terrabase `docs/{api-v1,query-v1,control-plane-v1}.md` over real HTTPS
//! (TLS 1.3, a throwaway CA). Ported from ts-go `v1/fixture_*_test.go`.
//!
//! Scope: route-level behaviour the client depends on: key roles and
//! grants, revocation and expiry, preconditions, versions and ETags, 304,
//! 404 with a tombstone ETag, idempotency replay and conflicts, unique
//! constraints, merge and ops patches, transactions with op-indexed errors
//! (assert, assert_count), filters, sort, projection, keyset cursors, counts,
//! and the control-plane subset. TTL, row filters and real index planning are
//! out of scope; faults (429, 503, 500) are injected explicitly.
//!
//! It deliberately does not reuse the client's types, so encoding mistakes
//! are not mirrored. Document bodies are stored as the exact text received;
//! patches re-encode through `serde_json::Value`.

#![allow(dead_code)]

use std::collections::HashMap;
use std::net::SocketAddr;
use std::sync::{Arc, Mutex};
use std::time::{SystemTime, UNIX_EPOCH};

use base64::Engine as _;
use http_body_util::{BodyExt, Full, Limited};
use hyper::body::{Bytes, Incoming};
use hyper::service::service_fn;
use hyper_util::rt::{TokioExecutor, TokioIo};
use serde_json::{json, Map, Value};
use sha2::{Digest, Sha256};

const B64: base64::engine::GeneralPurpose = base64::engine::general_purpose::URL_SAFE_NO_PAD;

#[derive(Clone, Debug)]
pub struct Reply {
    pub status: u16,
    pub headers: Vec<(String, String)>,
    pub body: Option<String>,
}

fn reply(status: u16, body: Value) -> Reply {
    Reply {
        status,
        headers: Vec::new(),
        body: Some(body.to_string()),
    }
}

fn err(status: u16, code: &str) -> Reply {
    err_with(status, code, Map::new())
}

fn err_op(status: u16, code: &str, op: usize) -> Reply {
    let mut m = Map::new();
    m.insert("op".into(), json!(op));
    err_with(status, code, m)
}

fn err_with(status: u16, code: &str, mut extra: Map<String, Value>) -> Reply {
    extra.insert("code".into(), json!(code));
    reply(status, json!({ "error": extra }))
}

fn set_op(mut r: Reply, op: usize) -> Reply {
    if let Some(body) = &r.body {
        let mut v: Value = serde_json::from_str(body).unwrap();
        v["error"]["op"] = json!(op);
        r.body = Some(v.to_string());
    }
    r
}

#[derive(Clone, Debug)]
pub struct Key {
    pub id: String,
    pub token: String,
    pub name: String,
    pub role: String,
    pub tenant_id: String,
    pub grants: Value,
    pub row_filter: Value,
    pub rate: Value,
    pub created_at: u64,
    pub expires_at: Option<u64>,
    pub revoked_at: Option<u64>,
}

#[derive(Debug)]
struct Tenant {
    id: String,
    name: String,
    external_ref: String,
    status: String,
    limits: Value,
    advisor_external: bool,
    created_at: u64,
    dbs: HashMap<String, Db>,
    db_order: Vec<String>,
}

#[derive(Debug)]
struct Db {
    id: String,
    name: String,
    tenant_id: String,
    home_region: String,
    status: String,
    durability: String,
    retention: Value,
    created_at: u64,
    colls: HashMap<String, Coll>,
    coll_order: Vec<String>,
    head: u64,
}

#[derive(Debug, Clone)]
struct Unique {
    name: String,
    paths: Vec<String>,
    lower: bool,
}

#[derive(Debug)]
struct Coll {
    id: String,
    name: String,
    generation: u64,
    schema: Value,
    ttl_path: Option<String>,
    unique: Vec<Unique>,
    unique_json: Value,
    advisor: String,
    created_at: u64,
    docs: HashMap<String, Doc>,
}

#[derive(Debug, Clone)]
struct Sibling {
    sequence: u64,
    deleted: bool,
    raw: Option<String>,
}

#[derive(Debug, Clone)]
struct Doc {
    /// `None` when deleted or conflicted.
    raw: Option<String>,
    deleted: bool,
    version: String,
    siblings: Option<Vec<Sibling>>,
}

impl Doc {
    fn live(d: Option<&Doc>) -> bool {
        d.is_some_and(|d| !d.deleted)
    }
}

#[derive(Debug, Clone)]
pub struct Fault {
    pub method: &'static str,
    pub path_prefix: String,
    pub status: u16,
    pub code: &'static str,
    pub retry_after: Option<&'static str>,
    /// Apply the request, then answer with the fault.
    pub after_commit: bool,
    pub remaining: u32,
}

impl Fault {
    pub fn new(method: &'static str, path_prefix: &str, status: u16, code: &'static str) -> Self {
        Self {
            method,
            path_prefix: path_prefix.to_owned(),
            status,
            code,
            retry_after: None,
            after_commit: false,
            remaining: 1,
        }
    }
}

#[derive(Debug)]
struct Operation {
    id: String,
    kind: String,
    resource: String,
    created_at: u64,
    polls: u32,
}

#[derive(Default, Debug)]
struct State {
    keys: HashMap<String, Key>,
    tenants: HashMap<String, Tenant>,
    tenant_order: Vec<String>,
    idem: HashMap<String, ([u8; 32], Reply)>,
    faults: Vec<Fault>,
    ops: HashMap<String, Operation>,
    seq: u64,
    requests: HashMap<String, usize>,
}

pub struct Fixture {
    state: Arc<Mutex<State>>,
    pub url: String,
    pub ca_pem: String,
    pub control_token: String,
    pub data_token: String,
    pub tenant_id: String,
    shutdown: Option<tokio::sync::oneshot::Sender<()>>,
}

impl Drop for Fixture {
    fn drop(&mut self) {
        if let Some(tx) = self.shutdown.take() {
            let _ = tx.send(());
        }
    }
}

pub fn now_ms() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap()
        .as_millis() as u64
}

fn rand_bytes<const N: usize>() -> [u8; N] {
    let mut out = [0u8; N];
    for chunk in out.chunks_mut(16) {
        let u = uuid::Uuid::new_v4();
        chunk.copy_from_slice(&u.as_bytes()[..chunk.len()]);
    }
    out
}

fn new_id() -> String {
    uuid::Uuid::now_v7().to_string()
}

fn is_uuid(s: &str) -> bool {
    s.len() == 36
        && s.bytes().enumerate().all(|(i, c)| {
            if matches!(i, 8 | 13 | 18 | 23) {
                c == b'-'
            } else {
                c.is_ascii_digit() || (b'a'..=b'f').contains(&c)
            }
        })
}

impl State {
    fn add_key(&mut self, mut k: Key) -> String {
        k.id = B64.encode(rand_bytes::<16>());
        let secret = B64.encode(rand_bytes::<32>());
        k.token = format!("tbk1.{}.{}", k.id, secret);
        k.created_at = now_ms();
        let token = k.token.clone();
        self.keys.insert(token.clone(), k);
        token
    }

    fn next_version(&mut self, id: &str) -> String {
        self.seq += 1;
        let sum = Sha256::digest(format!("{id}:{}", self.seq).as_bytes());
        format!("tbv1-{}", hex(&sum[..16]))
    }

    fn take_fault(&mut self, method: &str, path: &str, after_commit: bool) -> Option<Reply> {
        let f = self.faults.iter_mut().find(|f| {
            f.remaining > 0
                && f.after_commit == after_commit
                && f.method == method
                && path.starts_with(&f.path_prefix)
        })?;
        f.remaining -= 1;
        let mut r = err(f.status, f.code);
        if let Some(ra) = f.retry_after {
            r.headers.push(("Retry-After".into(), ra.into()));
        }
        Some(r)
    }
}

fn hex(b: &[u8]) -> String {
    b.iter().map(|x| format!("{x:02x}")).collect()
}

fn new_db(t: &mut Tenant, name: &str, region: &str, retention: Option<Value>) {
    let db = Db {
        id: new_id(),
        name: name.into(),
        tenant_id: t.id.clone(),
        home_region: region.into(),
        status: "ready".into(),
        durability: "local_durable".into(),
        retention: retention.unwrap_or_else(
            || json!({"shape_log_bytes":"1073741824","shape_log_age_seconds":"604800"}),
        ),
        created_at: now_ms(),
        colls: HashMap::new(),
        coll_order: Vec::new(),
        head: 0,
    };
    t.dbs.insert(name.into(), db);
    t.db_order.push(name.into());
}

fn parse_unique(v: &Value) -> Vec<Unique> {
    v.as_array()
        .map(|a| {
            a.iter()
                .map(|u| Unique {
                    name: u["name"].as_str().unwrap_or_default().into(),
                    paths: u["paths"]
                        .as_array()
                        .map(|p| {
                            p.iter()
                                .filter_map(|x| x.as_str().map(String::from))
                                .collect()
                        })
                        .unwrap_or_default(),
                    lower: u["transform"] == "lower",
                })
                .collect()
        })
        .unwrap_or_default()
}

fn new_coll(db: &mut Db, name: &str, schema: Value, ttl: Option<String>, unique: Value) {
    let c = Coll {
        id: new_id(),
        name: name.into(),
        generation: 1,
        schema,
        ttl_path: ttl,
        unique: parse_unique(&unique),
        unique_json: if unique.is_null() { json!([]) } else { unique },
        advisor: "auto".into(),
        created_at: now_ms(),
        docs: HashMap::new(),
    };
    db.colls.insert(name.into(), c);
    db.coll_order.push(name.into());
}

struct Ctx<'a> {
    method: &'a str,
    path: &'a str,
    segs: Vec<String>,
    query: Vec<(String, String)>,
    if_match: Option<String>,
    if_none_match: Option<String>,
    content_type: Option<String>,
}

fn unquote(h: &str) -> String {
    h.trim().trim_matches('"').to_owned()
}

impl Fixture {
    /// Starts the fixture with tenant `acme`, database `main`, collections
    /// `users` (unique lower(/email)), `orgs`, `items`, `sessions`, `tokens`,
    /// a control key and a full data key.
    pub async fn start() -> Fixture {
        let mut st = State::default();
        let control_token = st.add_key(Key {
            id: String::new(),
            token: String::new(),
            name: "bootstrap".into(),
            role: "control".into(),
            tenant_id: String::new(),
            grants: json!([]),
            row_filter: Value::Null,
            rate: Value::Null,
            created_at: 0,
            expires_at: None,
            revoked_at: None,
        });
        let mut tenant = Tenant {
            id: new_id(),
            name: "Acme".into(),
            external_ref: "proj_acme".into(),
            status: "active".into(),
            limits: json!({"max_databases":10,"max_storage_bytes":"10737418240","max_rate_per_second":1000}),
            advisor_external: false,
            created_at: now_ms(),
            dbs: HashMap::new(),
            db_order: Vec::new(),
        };
        new_db(&mut tenant, "main", "gru", None);
        let db = tenant.dbs.get_mut("main").unwrap();
        for name in ["users", "orgs", "items", "sessions", "tokens"] {
            let unique = if name == "users" {
                json!([{"name":"email","paths":["/email"],"transform":"lower"}])
            } else {
                Value::Null
            };
            new_coll(db, name, Value::Null, None, unique);
        }
        let tenant_id = tenant.id.clone();
        st.tenant_order.push(tenant_id.clone());
        st.tenants.insert(tenant_id.clone(), tenant);
        let data_token = st.add_key(Key {
            id: String::new(),
            token: String::new(),
            name: "server".into(),
            role: "data".into(),
            tenant_id: tenant_id.clone(),
            grants: json!([{"database":"*","collections":["*"],"permissions":["read","write","shape"]}]),
            row_filter: Value::Null,
            rate: json!({"per_second":100,"burst":200,"max_in_flight":16}),
            created_at: 0,
            expires_at: None,
            revoked_at: None,
        });
        let state = Arc::new(Mutex::new(st));
        let (addr, ca_pem, shutdown) = serve(state.clone()).await;
        Fixture {
            state,
            url: format!("https://localhost:{}", addr.port()),
            ca_pem,
            control_token,
            data_token,
            tenant_id,
            shutdown: Some(shutdown),
        }
    }

    pub fn inject(&self, fault: Fault) {
        self.state.lock().unwrap().faults.push(fault);
    }

    pub fn faults_left(&self) -> u32 {
        self.state
            .lock()
            .unwrap()
            .faults
            .iter()
            .map(|f| f.remaining)
            .sum()
    }

    /// The number of requests seen for `METHOD /path`.
    pub fn count(&self, method: &str, path: &str) -> usize {
        *self
            .state
            .lock()
            .unwrap()
            .requests
            .get(&format!("{method} {path}"))
            .unwrap_or(&0)
    }

    /// Adds a data key on the default tenant.
    pub fn add_data_key(&self, grants: Value) -> String {
        let tenant_id = self.tenant_id.clone();
        self.state.lock().unwrap().add_key(Key {
            id: String::new(),
            token: String::new(),
            name: "extra".into(),
            role: "data".into(),
            tenant_id,
            grants,
            row_filter: Value::Null,
            rate: Value::Null,
            created_at: 0,
            expires_at: None,
            revoked_at: None,
        })
    }

    /// Replaces a document with a conflicted state of two live siblings.
    pub fn seed_conflict(&self, db: &str, coll: &str, id: &str, a: &str, b: &str) -> String {
        let mut st = self.state.lock().unwrap();
        let version = st.next_version(id);
        let tenant_id = self.tenant_id.clone();
        let db = st
            .tenants
            .get_mut(&tenant_id)
            .unwrap()
            .dbs
            .get_mut(db)
            .unwrap();
        db.head += 1;
        db.colls.get_mut(coll).unwrap().docs.insert(
            id.into(),
            Doc {
                raw: None,
                deleted: false,
                version: version.clone(),
                siblings: Some(vec![
                    Sibling {
                        sequence: 12,
                        deleted: false,
                        raw: Some(a.into()),
                    },
                    Sibling {
                        sequence: 14,
                        deleted: false,
                        raw: Some(b.into()),
                    },
                ]),
            },
        );
        version
    }
}

async fn serve(state: Arc<Mutex<State>>) -> (SocketAddr, String, tokio::sync::oneshot::Sender<()>) {
    let mut ca_params = rcgen::CertificateParams::new(Vec::<String>::new()).unwrap();
    ca_params.is_ca = rcgen::IsCa::Ca(rcgen::BasicConstraints::Unconstrained);
    ca_params
        .distinguished_name
        .push(rcgen::DnType::CommonName, "terrascale v1 fixture CA");
    ca_params.key_usages = vec![
        rcgen::KeyUsagePurpose::KeyCertSign,
        rcgen::KeyUsagePurpose::CrlSign,
        rcgen::KeyUsagePurpose::DigitalSignature,
    ];
    let ca_key = rcgen::KeyPair::generate().unwrap();
    let ca = rcgen::CertifiedIssuer::self_signed(ca_params, ca_key).unwrap();
    let leaf_params =
        rcgen::CertificateParams::new(vec!["localhost".to_string(), "127.0.0.1".to_string()])
            .unwrap();
    let leaf_key = rcgen::KeyPair::generate().unwrap();
    let leaf = leaf_params.signed_by(&leaf_key, &ca).unwrap();

    let provider = Arc::new(rustls::crypto::ring::default_provider());
    let mut cfg = rustls::ServerConfig::builder_with_provider(provider)
        .with_protocol_versions(&[&rustls::version::TLS13])
        .unwrap()
        .with_no_client_auth()
        .with_single_cert(
            vec![leaf.der().clone(), ca.der().clone()],
            rustls::pki_types::PrivateKeyDer::Pkcs8(leaf_key.serialize_der().into()),
        )
        .unwrap();
    cfg.alpn_protocols = vec![b"h2".to_vec(), b"http/1.1".to_vec()];
    let acceptor = tokio_rustls::TlsAcceptor::from(Arc::new(cfg));
    let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
    let addr = listener.local_addr().unwrap();
    let (tx, mut rx) = tokio::sync::oneshot::channel::<()>();
    tokio::spawn(async move {
        loop {
            let (tcp, _) = tokio::select! {
                _ = &mut rx => return,
                accepted = listener.accept() => match accepted { Ok(a) => a, Err(_) => continue },
            };
            let acceptor = acceptor.clone();
            let state = state.clone();
            tokio::spawn(async move {
                let Ok(tls) = acceptor.accept(tcp).await else {
                    return;
                };
                let svc = service_fn(move |req| handle(state.clone(), req));
                let _ = hyper_util::server::conn::auto::Builder::new(TokioExecutor::new())
                    .serve_connection(TokioIo::new(tls), svc)
                    .await;
            });
        }
    });
    (addr, ca.pem(), tx)
}

async fn handle(
    state: Arc<Mutex<State>>,
    req: hyper::Request<Incoming>,
) -> Result<hyper::Response<Full<Bytes>>, std::convert::Infallible> {
    let (parts, body) = req.into_parts();
    let header = |n: &str| {
        parts
            .headers
            .get(n)
            .and_then(|v| v.to_str().ok())
            .map(String::from)
    };
    let body = match Limited::new(body, (4 << 20) + 1).collect().await {
        Ok(b) => b.to_bytes(),
        Err(_) => return Ok(render(err(413, "payload_too_large"))),
    };
    let path = parts.uri.path().to_owned();
    let query: Vec<(String, String)> = parts
        .uri
        .query()
        .map(|q| {
            url::form_urlencoded::parse(q.as_bytes())
                .into_owned()
                .collect()
        })
        .unwrap_or_default();
    let ctx = Ctx {
        method: parts.method.as_str(),
        path: &path,
        segs: path
            .trim_start_matches('/')
            .split('/')
            .map(percent_decode)
            .collect(),
        query,
        if_match: header("if-match"),
        if_none_match: header("if-none-match"),
        content_type: header("content-type"),
    };
    let auth = header("authorization").unwrap_or_default();
    let idem = header("idempotency-key");
    let rep = {
        let mut st = state.lock().unwrap();
        wrap(
            &mut st,
            &ctx,
            &auth,
            idem.as_deref(),
            header("content-encoding").is_some(),
            &body,
        )
    };
    Ok(render(rep))
}

fn percent_decode(s: &str) -> String {
    url::form_urlencoded::parse(format!("x={}", s.replace('+', "%2B")).as_bytes())
        .next()
        .map(|(_, v)| v.into_owned())
        .unwrap_or_default()
}

fn render(r: Reply) -> hyper::Response<Full<Bytes>> {
    let mut b = hyper::Response::builder().status(r.status);
    for (k, v) in &r.headers {
        b = b.header(k, v);
    }
    match r.body {
        Some(body) => b
            .header("content-type", "application/json")
            .body(Full::new(Bytes::from(body)))
            .unwrap(),
        None => b.body(Full::new(Bytes::new())).unwrap(),
    }
}

fn valid_idem(s: &str) -> bool {
    !s.is_empty()
        && s.len() <= 64
        && s.bytes()
            .all(|b| b.is_ascii_alphanumeric() || matches!(b, b'_' | b'.' | b'-'))
}

fn wrap(
    st: &mut State,
    ctx: &Ctx,
    auth: &str,
    idem: Option<&str>,
    encoded: bool,
    body: &[u8],
) -> Reply {
    *st.requests
        .entry(format!("{} {}", ctx.method, ctx.path))
        .or_default() += 1;
    if body.len() > 4 << 20 {
        return err(413, "payload_too_large");
    }
    if encoded {
        return err(415, "unsupported_media_type");
    }
    let control = ctx.segs.get(1).map(String::as_str) == Some("dashboard");
    let Some(key) = st
        .keys
        .get(auth.strip_prefix("Bearer ").unwrap_or(""))
        .cloned()
    else {
        return err(401, "invalid_key");
    };
    if key.revoked_at.is_some() || key.expires_at.is_some_and(|e| e <= now_ms()) {
        return err(401, "invalid_key");
    }
    if control != (key.role == "control") || key.role == "edge" {
        return err(403, "forbidden_scope");
    }
    if let Some(f) = st.take_fault(ctx.method, ctx.path, false) {
        return f;
    }
    let mutation = ctx.method != "GET" && !ctx.path.ends_with("/query");
    let mut slot = None;
    if mutation {
        match idem {
            None if control => return err(400, "idempotency_key_required"),
            Some(k) if !valid_idem(k) => return err(400, "invalid_request"),
            Some(k) => {
                let s = format!("{} {} {} {}", key.id, ctx.method, ctx.path, k);
                let mut h = Sha256::new();
                for part in [&ctx.content_type, &ctx.if_match, &ctx.if_none_match] {
                    h.update(part.as_deref().unwrap_or("").as_bytes());
                    h.update(b"\n");
                }
                h.update(body);
                let digest: [u8; 32] = h.finalize().into();
                if let Some((prev, rep)) = st.idem.get(&s) {
                    if *prev != digest {
                        return err(409, "idempotency_conflict");
                    }
                    return rep.clone();
                }
                slot = Some((s, digest));
            }
            None => {}
        }
    }
    let rep = route(st, ctx, &key, body);
    if let Some((s, digest)) = slot {
        if rep.status < 500 {
            st.idem.insert(s, (digest, rep.clone()));
        }
    }
    if let Some(f) = st.take_fault(ctx.method, ctx.path, true) {
        return f;
    }
    rep
}

fn route(st: &mut State, ctx: &Ctx, key: &Key, body: &[u8]) -> Reply {
    let s: Vec<&str> = ctx.segs.iter().map(String::as_str).collect();
    match (ctx.method, s.as_slice()) {
        ("GET", ["v1", "databases", db, "collections", c, "documents", id]) => {
            get_doc(st, key, db, c, id, ctx)
        }
        ("PUT", ["v1", "databases", db, "collections", c, "documents", id]) => {
            put_doc(st, key, db, c, id, ctx, body)
        }
        ("POST", ["v1", "databases", db, "collections", c, "documents"]) => {
            create_doc(st, key, db, c, body)
        }
        ("PATCH", ["v1", "databases", db, "collections", c, "documents", id]) => {
            patch_doc(st, key, db, c, id, ctx, body)
        }
        ("DELETE", ["v1", "databases", db, "collections", c, "documents", id]) => {
            delete_doc(st, key, db, c, id, ctx)
        }
        ("POST", ["v1", "databases", db, "transactions"]) => transaction(st, key, db, body),
        ("POST", ["v1", "databases", db, "collections", c, "query"]) => query(st, key, db, c, body),
        (_, ["v1", "dashboard", rest @ ..]) => dashboard(st, ctx, key, rest, body),
        _ => err(404, "unknown_route"),
    }
}

// ---- data plane -----------------------------------------------------------

fn tenant_db<'a>(
    st: &'a mut State,
    key: &Key,
    db: &str,
    coll: &str,
    perm: &str,
) -> Result<&'a mut Db, Reply> {
    let grants = key.grants.as_array().cloned().unwrap_or_default();
    let has = |list: &Value, v: &str| {
        list.as_array()
            .is_some_and(|a| a.iter().any(|x| x == v || x == "*"))
    };
    let grant = grants.iter().find(|g| {
        (g["database"] == db || g["database"] == "*")
            && (coll.is_empty() || has(&g["collections"], coll))
            && g["permissions"]
                .as_array()
                .is_some_and(|p| p.iter().any(|x| x == perm))
    });
    let Some(grant) = grant.cloned() else {
        return Err(err(403, "forbidden_scope"));
    };
    let Some(t) = st.tenants.get_mut(&key.tenant_id) else {
        return Err(err(403, "forbidden_scope"));
    };
    if t.status != "active" {
        return Err(err(403, "forbidden_scope"));
    }
    let Some(d) = t.dbs.get_mut(db) else {
        return Err(if grant["database"] == "*" {
            err(404, "unknown_database")
        } else {
            err(403, "forbidden_scope")
        });
    };
    if !coll.is_empty() && !d.colls.contains_key(coll) {
        let star = grant["collections"]
            .as_array()
            .is_some_and(|a| a.iter().any(|x| x == "*"));
        return Err(if star {
            err(404, "unknown_collection")
        } else {
            err(403, "forbidden_scope")
        });
    }
    Ok(d)
}

fn etag(v: &str) -> (String, String) {
    ("ETag".into(), format!("\"{v}\""))
}

fn doc_json(id: &str, d: &Doc, project: &[String]) -> String {
    match &d.siblings {
        Some(sibs) => {
            let list: Vec<String> = sibs
                .iter()
                .map(|s| {
                    let event = format!(
                        r#"{{"replica":"0192f0c4-7a1e-7c3d-9b2a-5e8f1d2c3b4a","incarnation":"8f14e45fceea167a5a36dedd4bea2543","sequence":"{}"}}"#,
                        s.sequence
                    );
                    match &s.raw {
                        Some(raw) if !s.deleted => format!(r#"{{"event":{event},"deleted":false,"document":{raw}}}"#),
                        _ => format!(r#"{{"event":{event},"deleted":true}}"#),
                    }
                })
                .collect();
            format!(
                r#"{{"id":"{id}","version":"{}","state":"conflict","siblings":[{}]}}"#,
                d.version,
                list.join(",")
            )
        }
        None => {
            let raw = d.raw.clone().unwrap_or_else(|| "{}".into());
            let raw = if project.is_empty() {
                raw
            } else {
                project_doc(&serde_json::from_str(&raw).unwrap(), project).to_string()
            };
            format!(
                r#"{{"id":"{id}","version":"{}","state":"value","document":{raw}}}"#,
                d.version
            )
        }
    }
}

fn write_result(id: &str, version: &str, offset: u64) -> Value {
    json!({"id": id, "version": version, "offset": offset.to_string(), "durability": "local_durable"})
}

fn parse_object(raw: &[u8]) -> Option<Map<String, Value>> {
    match serde_json::from_slice::<Value>(raw) {
        Ok(Value::Object(m)) => Some(m),
        _ => None,
    }
}

/// A transaction's staged writes over one database.
struct Staged {
    writes: Vec<(String, String, Doc)>,
}

impl Staged {
    fn get<'a>(&'a self, db: &'a Db, coll: &str, id: &str) -> Option<&'a Doc> {
        self.writes
            .iter()
            .rev()
            .find(|(c, i, _)| c == coll && i == id)
            .map(|(_, _, d)| d)
            .or_else(|| db.colls.get(coll).and_then(|c| c.docs.get(id)))
    }

    fn unique_violation(
        &self,
        db: &Db,
        coll: &str,
        id: &str,
        obj: &Map<String, Value>,
    ) -> Option<String> {
        let c = db.colls.get(coll)?;
        for u in &c.unique {
            let Some(key) = unique_key(u, obj) else {
                continue;
            };
            let mut ids: Vec<&str> = c.docs.keys().map(String::as_str).collect();
            ids.extend(
                self.writes
                    .iter()
                    .filter(|(cc, _, _)| cc == coll)
                    .map(|(_, i, _)| i.as_str()),
            );
            for other in ids {
                if other == id {
                    continue;
                }
                let Some(d) = self.get(db, coll, other) else {
                    continue;
                };
                if d.deleted {
                    continue;
                }
                let Some(raw) = &d.raw else { continue };
                if let Some(m) = parse_object(raw.as_bytes()) {
                    if unique_key(u, &m).as_ref() == Some(&key) {
                        return Some(u.name.clone());
                    }
                }
            }
        }
        None
    }
}

fn unique_key(u: &Unique, obj: &Map<String, Value>) -> Option<String> {
    let mut parts = Vec::new();
    for p in &u.paths {
        let v = lookup(obj, p)?;
        if v.is_null() {
            return None;
        }
        let v = match (v, u.lower) {
            (Value::String(s), true) => Value::String(s.to_lowercase()),
            (v, _) => v.clone(),
        };
        parts.push(v.to_string());
    }
    Some(parts.join("\u{0}"))
}

type PatchBox = dyn Fn(&mut Map<String, Value>) -> Result<(), Reply>;
type PatchFn<'a> = &'a dyn Fn(&mut Map<String, Value>) -> Result<(), Reply>;

/// Applies one write to the staged view. `kind` is put, patch or delete.
#[allow(clippy::too_many_arguments)]
fn write_doc(
    st_seq: &mut u64,
    db: &Db,
    staged: &Staged,
    coll: &str,
    kind: &str,
    id: &str,
    if_match: Option<&str>,
    if_none_match: Option<&str>,
    raw: Option<&[u8]>,
    patch: Option<PatchFn>,
) -> Result<(Doc, bool), Reply> {
    let cur = staged.get(db, coll, id);
    if let Some(m) = if_match {
        if cur.is_none_or(|c| c.version != m) {
            return Err(err(412, "precondition_failed"));
        }
    }
    if if_none_match == Some("*") && Doc::live(cur) {
        return Err(err(412, "precondition_failed"));
    }
    let created = !Doc::live(cur);
    let mut next_version = || {
        *st_seq += 1;
        let sum = Sha256::digest(format!("{id}:{st_seq}").as_bytes());
        format!("tbv1-{}", hex(&sum[..16]))
    };
    let body: Vec<u8> = match kind {
        "delete" => {
            if !Doc::live(cur) {
                return Err(err(404, "not_found"));
            }
            return Ok((
                Doc {
                    raw: None,
                    deleted: true,
                    version: next_version(),
                    siblings: None,
                },
                false,
            ));
        }
        "patch" => {
            let Some(cur) = cur.filter(|c| !c.deleted) else {
                return Err(err(404, "not_found"));
            };
            if cur.siblings.is_some() {
                return Err(err(409, "conflict_unresolved"));
            }
            let mut obj = parse_object(cur.raw.as_ref().unwrap().as_bytes()).unwrap();
            (patch.unwrap())(&mut obj)?;
            serde_json::to_vec(&obj).unwrap()
        }
        _ => raw.unwrap_or_default().to_vec(),
    };
    let Some(obj) = parse_object(&body) else {
        return Err(err(400, "invalid_request"));
    };
    if let Some(name) = staged.unique_violation(db, coll, id, &obj) {
        let mut m = Map::new();
        m.insert("constraint".into(), json!(name));
        return Err(err_with(409, "unique_violation", m));
    }
    let created = created && kind != "patch";
    Ok((
        Doc {
            raw: Some(String::from_utf8(body).unwrap()),
            deleted: false,
            version: next_version(),
            siblings: None,
        },
        created,
    ))
}

fn commit(db: &mut Db, staged: Staged) -> u64 {
    db.head += 1;
    for (c, id, d) in staged.writes {
        db.colls.get_mut(&c).unwrap().docs.insert(id, d);
    }
    db.head
}

fn get_doc(st: &mut State, key: &Key, db: &str, coll: &str, id: &str, ctx: &Ctx) -> Reply {
    let d = match tenant_db(st, key, db, coll, "read") {
        Ok(d) => d,
        Err(r) => return r,
    };
    if !is_uuid(id) {
        return err(400, "invalid_request");
    }
    let doc = d.colls[coll].docs.get(id);
    match doc {
        Some(doc) if !doc.deleted => {
            if ctx.if_none_match.as_deref().map(unquote).as_deref() == Some(doc.version.as_str()) {
                return Reply {
                    status: 304,
                    headers: vec![etag(&doc.version)],
                    body: None,
                };
            }
            Reply {
                status: 200,
                headers: vec![etag(&doc.version)],
                body: Some(doc_json(id, doc, &[])),
            }
        }
        other => {
            let mut r = err(404, "not_found");
            if let Some(doc) = other {
                r.headers.push(etag(&doc.version));
            }
            r
        }
    }
}

#[allow(clippy::too_many_arguments)]
fn single_write(
    st: &mut State,
    key: &Key,
    db: &str,
    coll: &str,
    id: &str,
    kind: &str,
    if_match: Option<String>,
    if_none_match: Option<String>,
    body: Option<&[u8]>,
    patch: Option<PatchFn>,
) -> Result<(Doc, bool, u64), Reply> {
    let mut seq = st.seq;
    let d = tenant_db(st, key, db, coll, "write")?;
    if !is_uuid(id) {
        return Err(err(400, "invalid_request"));
    }
    let staged = Staged { writes: Vec::new() };
    let (doc, created) = write_doc(
        &mut seq,
        d,
        &staged,
        coll,
        kind,
        id,
        if_match.as_deref(),
        if_none_match.as_deref(),
        body,
        patch,
    )?;
    let offset = commit(
        d,
        Staged {
            writes: vec![(coll.into(), id.into(), doc.clone())],
        },
    );
    st.seq = seq;
    Ok((doc, created, offset))
}

fn put_doc(
    st: &mut State,
    key: &Key,
    db: &str,
    coll: &str,
    id: &str,
    ctx: &Ctx,
    body: &[u8],
) -> Reply {
    let im = ctx.if_match.as_deref().map(unquote);
    match single_write(
        st,
        key,
        db,
        coll,
        id,
        "put",
        im,
        ctx.if_none_match.clone(),
        Some(body),
        None,
    ) {
        Ok((doc, created, offset)) => {
            let mut r = reply(
                if created { 201 } else { 200 },
                write_result(id, &doc.version, offset),
            );
            r.headers.push(etag(&doc.version));
            r
        }
        Err(r) => r,
    }
}

fn create_doc(st: &mut State, key: &Key, db: &str, coll: &str, body: &[u8]) -> Reply {
    let id = new_id();
    match single_write(
        st,
        key,
        db,
        coll,
        &id,
        "put",
        None,
        Some("*".into()),
        Some(body),
        None,
    ) {
        Ok((doc, _, offset)) => {
            let mut r = reply(201, write_result(&id, &doc.version, offset));
            r.headers.push(etag(&doc.version));
            r.headers.push((
                "Location".into(),
                format!("/v1/databases/{db}/collections/{coll}/documents/{id}"),
            ));
            r
        }
        Err(r) => r,
    }
}

fn patch_doc(
    st: &mut State,
    key: &Key,
    db: &str,
    coll: &str,
    id: &str,
    ctx: &Ctx,
    body: &[u8],
) -> Reply {
    let apply: Box<PatchBox> = match ctx.content_type.as_deref() {
        Some("application/merge-patch+json") => {
            let Some(merge) = parse_object(body) else {
                return err(400, "invalid_patch");
            };
            Box::new(move |obj| {
                merge_patch(obj, &merge);
                Ok(())
            })
        }
        Some("application/vnd.terrabase.ops+json") => {
            let Some(p) = parse_object(body) else {
                return err(400, "invalid_patch");
            };
            let Some(ops) = p.get("ops").and_then(Value::as_array).cloned() else {
                return err(400, "invalid_patch");
            };
            if p.len() != 1 || ops.is_empty() || ops.len() > 256 {
                return err(400, "invalid_patch");
            }
            Box::new(move |obj| apply_ops(obj, &ops))
        }
        _ => return err(415, "unsupported_media_type"),
    };
    let im = ctx.if_match.as_deref().map(unquote);
    match single_write(
        st,
        key,
        db,
        coll,
        id,
        "patch",
        im,
        None,
        None,
        Some(&*apply),
    ) {
        Ok((doc, _, offset)) => {
            let mut r = reply(200, write_result(id, &doc.version, offset));
            r.headers.push(etag(&doc.version));
            r
        }
        Err(r) => r,
    }
}

fn delete_doc(st: &mut State, key: &Key, db: &str, coll: &str, id: &str, ctx: &Ctx) -> Reply {
    let im = ctx.if_match.as_deref().map(unquote);
    match single_write(st, key, db, coll, id, "delete", im, None, None, None) {
        Ok((doc, _, offset)) => reply(200, write_result(id, &doc.version, offset)),
        Err(r) => r,
    }
}

fn merge_patch(target: &mut Map<String, Value>, patch: &Map<String, Value>) {
    for (k, v) in patch {
        match v {
            Value::Null => {
                target.remove(k);
            }
            Value::Object(p) => {
                let entry = target.entry(k.clone()).or_insert_with(|| json!({}));
                if !entry.is_object() {
                    *entry = json!({});
                }
                merge_patch(entry.as_object_mut().unwrap(), p);
            }
            other => {
                target.insert(k.clone(), other.clone());
            }
        }
    }
}

fn split_pointer(p: &str) -> Option<Vec<String>> {
    let rest = p.strip_prefix('/')?;
    Some(
        rest.split('/')
            .map(|s| s.replace("~1", "/").replace("~0", "~"))
            .collect(),
    )
}

fn lookup<'a>(obj: &'a Map<String, Value>, pointer: &str) -> Option<&'a Value> {
    let segs = split_pointer(pointer)?;
    let mut cur = obj.get(&segs[0])?;
    for s in &segs[1..] {
        cur = cur.as_object()?.get(s)?;
    }
    Some(cur)
}

fn parent_mut<'a>(
    obj: &'a mut Map<String, Value>,
    pointer: &str,
    create: bool,
) -> Option<(&'a mut Map<String, Value>, String)> {
    let segs = split_pointer(pointer)?;
    let (last, init) = segs.split_last()?;
    let mut cur = obj;
    for s in init {
        if !cur.contains_key(s) {
            if !create {
                return None;
            }
            cur.insert(s.clone(), json!({}));
        }
        cur = cur.get_mut(s)?.as_object_mut()?;
    }
    Some((cur, last.clone()))
}

fn apply_ops(obj: &mut Map<String, Value>, ops: &[Value]) -> Result<(), Reply> {
    for op in ops {
        let Some(o) = op.as_object() else {
            return Err(err(400, "invalid_patch"));
        };
        let kind = o.get("op").and_then(Value::as_str).unwrap_or("");
        let path = o.get("path").and_then(Value::as_str).unwrap_or("");
        let allowed: &[&str] = match kind {
            "set" | "append" => &["op", "path", "value"],
            "unset" => &["op", "path"],
            "inc" => &["op", "path", "by"],
            _ => return Err(err(400, "invalid_patch")),
        };
        if o.keys().any(|k| !allowed.contains(&k.as_str())) {
            return Err(err(400, "invalid_patch"));
        }
        let Some((parent, last)) = parent_mut(obj, path, kind != "unset") else {
            if kind == "unset" {
                continue;
            }
            return Err(err(409, "patch_conflict"));
        };
        match kind {
            "set" => {
                let Some(v) = o.get("value") else {
                    return Err(err(400, "invalid_patch"));
                };
                parent.insert(last, v.clone());
            }
            "unset" => {
                parent.remove(&last);
            }
            "inc" => {
                let Some(by) = o.get("by").and_then(Value::as_i64) else {
                    return Err(err(400, "invalid_patch"));
                };
                let cur = match parent.get(&last) {
                    None => 0,
                    Some(v) => match v.as_i64() {
                        Some(n) => n,
                        None => return Err(err(409, "patch_conflict")),
                    },
                };
                parent.insert(last, json!(cur + by));
            }
            "append" => {
                let Some(v) = o.get("value") else {
                    return Err(err(400, "invalid_patch"));
                };
                match parent.get_mut(&last) {
                    None => {
                        parent.insert(last, json!([v.clone()]));
                    }
                    Some(Value::Array(a)) => a.push(v.clone()),
                    Some(_) => return Err(err(409, "patch_conflict")),
                }
            }
            _ => unreachable!(),
        }
    }
    Ok(())
}

fn transaction(st: &mut State, key: &Key, db: &str, body: &[u8]) -> Reply {
    let Some(req) = parse_object(body) else {
        return err(400, "invalid_request");
    };
    let Some(ops) = req.get("ops").and_then(Value::as_array).cloned() else {
        return err(400, "invalid_request");
    };
    if req.len() != 1 || ops.is_empty() || ops.len() > 1024 {
        return err(400, "invalid_request");
    }
    let allowed = [
        "op",
        "collection",
        "id",
        "document",
        "if_match",
        "if_none_match",
        "merge",
        "ops",
        "filter",
        "absent",
        "gte",
        "lte",
    ];
    // Authorize every op first, as the server checks grants per collection.
    for (i, op) in ops.iter().enumerate() {
        let Some(o) = op.as_object() else {
            return err_op(400, "invalid_request", i);
        };
        if o.keys().any(|k| !allowed.contains(&k.as_str())) {
            return err_op(400, "invalid_request", i);
        }
        let perm = if matches!(
            o.get("op").and_then(Value::as_str),
            Some("assert" | "assert_count")
        ) {
            "read"
        } else {
            "write"
        };
        let coll = o.get("collection").and_then(Value::as_str).unwrap_or("");
        if let Err(r) = tenant_db(st, key, db, coll, perm) {
            return set_op(r, i);
        }
    }
    let mut seq = st.seq;
    let Some(d) = st
        .tenants
        .get_mut(&key.tenant_id)
        .and_then(|t| t.dbs.get_mut(db))
    else {
        return err(404, "unknown_database");
    };
    let mut staged = Staged { writes: Vec::new() };
    let mut targets = std::collections::HashSet::new();
    let mut results = Vec::new();
    let mut count_ops = 0;
    for (i, op) in ops.iter().enumerate() {
        let o = op.as_object().unwrap();
        let kind = o.get("op").and_then(Value::as_str).unwrap_or("");
        let coll = o
            .get("collection")
            .and_then(Value::as_str)
            .unwrap_or("")
            .to_owned();
        let s = |k: &str| o.get(k).and_then(Value::as_str).map(String::from);
        if kind == "assert_count" {
            count_ops += 1;
            let gte = o.get("gte").and_then(Value::as_u64);
            let lte = o.get("lte").and_then(Value::as_u64);
            let Ok(filter) = Filter::parse(o.get("filter").unwrap_or(&Value::Null)) else {
                return err_op(400, "invalid_query", i);
            };
            if o.contains_key("id") || (gte.is_none() && lte.is_none()) || count_ops > 16 {
                return err_op(400, "invalid_request", i);
            }
            let n = d.colls[&coll]
                .docs
                .iter()
                .filter(|(id, doc)| {
                    !doc.deleted
                        && doc.siblings.is_none()
                        && filter.matches(
                            id,
                            &parse_object(doc.raw.as_ref().unwrap().as_bytes()).unwrap(),
                        )
                })
                .count() as u64;
            if gte.is_some_and(|g| n < g) || lte.is_some_and(|l| n > l) {
                return err_op(412, "precondition_failed", i);
            }
            results.push(json!({"asserted": true, "count": n.to_string()}));
            continue;
        }
        if kind == "create" && (o.contains_key("if_match") || o.contains_key("if_none_match")) {
            return err_op(400, "invalid_request", i);
        }
        let id = match s("id") {
            Some(id) => id,
            None if kind == "create" => new_id(),
            None => return err_op(400, "invalid_request", i),
        };
        if !is_uuid(&id) || !targets.insert(format!("{coll}/{id}")) {
            return err_op(400, "invalid_request", i);
        }
        let raw_doc = o.get("document").map(|v| serde_json::to_vec(v).unwrap());
        let res = match kind {
            "create" => write_doc(
                &mut seq,
                d,
                &staged,
                &coll,
                "put",
                &id,
                None,
                Some("*"),
                raw_doc.as_deref(),
                None,
            ),
            "put" => write_doc(
                &mut seq,
                d,
                &staged,
                &coll,
                "put",
                &id,
                s("if_match").as_deref(),
                s("if_none_match").as_deref(),
                raw_doc.as_deref(),
                None,
            ),
            "delete" => write_doc(
                &mut seq,
                d,
                &staged,
                &coll,
                "delete",
                &id,
                s("if_match").as_deref(),
                None,
                None,
                None,
            ),
            "patch" => {
                let merge = o.get("merge").and_then(Value::as_object).cloned();
                let pops = o.get("ops").and_then(Value::as_array).cloned();
                if merge.is_some() == pops.is_some() {
                    return err_op(400, "invalid_patch", i);
                }
                let apply = move |obj: &mut Map<String, Value>| match (&merge, &pops) {
                    (Some(m), _) => {
                        merge_patch(obj, m);
                        Ok(())
                    }
                    (_, Some(p)) => apply_ops(obj, p),
                    _ => unreachable!(),
                };
                write_doc(
                    &mut seq,
                    d,
                    &staged,
                    &coll,
                    "patch",
                    &id,
                    s("if_match").as_deref(),
                    None,
                    None,
                    Some(&apply),
                )
            }
            "assert" => {
                let cur = staged.get(d, &coll, &id);
                let ok = if o.get("absent") == Some(&json!(true)) {
                    !Doc::live(cur)
                } else {
                    let Ok(filter) = Filter::parse(o.get("filter").unwrap_or(&Value::Null)) else {
                        return err_op(400, "invalid_query", i);
                    };
                    cur.is_some_and(|c| {
                        !c.deleted
                            && c.siblings.is_none()
                            && filter.matches(
                                &id,
                                &parse_object(c.raw.as_ref().unwrap().as_bytes()).unwrap(),
                            )
                    })
                };
                if !ok {
                    return err_op(412, "precondition_failed", i);
                }
                results.push(json!({"asserted": true}));
                continue;
            }
            _ => return err_op(400, "invalid_request", i),
        };
        match res {
            Ok((doc, _)) => {
                results.push(json!({"id": id, "version": doc.version}));
                staged.writes.push((coll, id, doc));
            }
            Err(r) => return set_op(r, i),
        }
    }
    let offset = commit(d, staged);
    st.seq = seq;
    reply(
        200,
        json!({"offset": offset.to_string(), "durability": "local_durable", "results": results}),
    )
}

// ---- query ------------------------------------------------------------------

#[derive(Debug, Clone)]
struct QPath {
    pointer: String,
    lower: bool,
    id: bool,
}

impl QPath {
    fn parse(v: &Value) -> Result<Self, ()> {
        match v {
            Value::String(s) if s == "$id" => Ok(Self {
                pointer: String::new(),
                lower: false,
                id: true,
            }),
            Value::String(s) => {
                let n = split_pointer(s).ok_or(())?.len();
                if !(1..=16).contains(&n) {
                    return Err(());
                }
                Ok(Self {
                    pointer: s.clone(),
                    lower: false,
                    id: false,
                })
            }
            Value::Object(m) if m.len() == 1 => {
                let mut p = Self::parse(m.get("lower").ok_or(())?)?;
                p.lower = true;
                Ok(p)
            }
            _ => Err(()),
        }
    }

    fn get(&self, id: &str, obj: &Map<String, Value>) -> Option<Value> {
        let v = if self.id {
            Value::String(id.into())
        } else {
            lookup(obj, &self.pointer)?.clone()
        };
        Some(match (v, self.lower) {
            (Value::String(s), true) => Value::String(s.to_lowercase()),
            (v, _) => v,
        })
    }
}

#[derive(Debug, Clone)]
pub(crate) struct Filter {
    op: String,
    children: Vec<Filter>,
    path: Option<QPath>,
    value: Value,
    values: Vec<Value>,
}

fn rank(v: &Value) -> u8 {
    match v {
        Value::Null => 1,
        Value::Bool(false) => 2,
        Value::Bool(true) => 3,
        Value::Number(_) => 4,
        Value::String(_) => 5,
        _ => 6,
    }
}

fn type_class(v: &Value) -> u8 {
    match rank(v) {
        3 => 2,
        r => r,
    }
}

fn scalar(v: &Value) -> bool {
    !matches!(v, Value::Array(_) | Value::Object(_))
}

fn cmp_values(a: &Value, b: &Value) -> std::cmp::Ordering {
    use std::cmp::Ordering;
    let (ra, rb) = (rank(a), rank(b));
    if ra != rb {
        return ra.cmp(&rb);
    }
    match (a, b) {
        (Value::Number(x), Value::Number(y)) => x
            .as_f64()
            .unwrap()
            .partial_cmp(&y.as_f64().unwrap())
            .unwrap_or(Ordering::Equal),
        (Value::String(x), Value::String(y)) => x.as_bytes().cmp(y.as_bytes()),
        _ => Ordering::Equal,
    }
}

fn lowered(v: Value, lower: bool) -> Value {
    match (v, lower) {
        (Value::String(s), true) => Value::String(s.to_lowercase()),
        (v, _) => v,
    }
}

impl Filter {
    fn parse(v: &Value) -> Result<Option<Filter>, ()> {
        if v.is_null() {
            return Ok(None);
        }
        Self::build(v, 0).map(Some)
    }

    fn build(v: &Value, depth: usize) -> Result<Filter, ()> {
        let m = v.as_object().ok_or(())?;
        if m.len() != 1 || depth > 8 {
            return Err(());
        }
        let (op, arg) = m.iter().next().unwrap();
        let mut f = Filter {
            op: op.clone(),
            children: Vec::new(),
            path: None,
            value: Value::Null,
            values: Vec::new(),
        };
        match op.as_str() {
            "and" | "or" => {
                let list = arg.as_array().ok_or(())?;
                if list.is_empty() || list.len() > 64 {
                    return Err(());
                }
                for c in list {
                    f.children.push(Self::build(c, depth + 1)?);
                }
            }
            "not" => f.children.push(Self::build(arg, depth + 1)?),
            "exists" => f.path = Some(QPath::parse(arg)?),
            "eq" | "ne" | "lt" | "lte" | "gt" | "gte" | "prefix" | "contains" | "in" | "nin" => {
                let pair = arg.as_array().ok_or(())?;
                if pair.len() != 2 {
                    return Err(());
                }
                let p = QPath::parse(&pair[0])?;
                let lower = p.lower;
                f.path = Some(p);
                if op == "in" || op == "nin" {
                    let vals = pair[1].as_array().ok_or(())?;
                    if vals.is_empty() || vals.len() > 256 || !vals.iter().all(scalar) {
                        return Err(());
                    }
                    f.values = vals.iter().cloned().map(|v| lowered(v, lower)).collect();
                } else {
                    if !scalar(&pair[1]) {
                        return Err(());
                    }
                    if op == "prefix" && !pair[1].is_string() {
                        return Err(());
                    }
                    if matches!(op.as_str(), "lt" | "lte" | "gt" | "gte") && pair[1].is_null() {
                        return Err(());
                    }
                    f.value = lowered(pair[1].clone(), lower);
                }
            }
            _ => return Err(()),
        }
        Ok(f)
    }
}

trait Matches {
    fn matches(&self, id: &str, obj: &Map<String, Value>) -> bool;
}

impl Matches for Option<Filter> {
    fn matches(&self, id: &str, obj: &Map<String, Value>) -> bool {
        self.as_ref().is_none_or(|f| f.matches(id, obj))
    }
}

impl Filter {
    fn matches(&self, id: &str, obj: &Map<String, Value>) -> bool {
        use std::cmp::Ordering::*;
        match self.op.as_str() {
            "and" => return self.children.iter().all(|c| c.matches(id, obj)),
            "or" => return self.children.iter().any(|c| c.matches(id, obj)),
            "not" => return !self.children[0].matches(id, obj),
            _ => {}
        }
        let v = self.path.as_ref().unwrap().get(id, obj);
        match (self.op.as_str(), v) {
            ("exists", v) => v.is_some(),
            ("eq", Some(v)) => scalar(&v) && cmp_values(&v, &self.value) == Equal,
            ("ne", None) => true,
            ("ne", Some(v)) => !scalar(&v) || cmp_values(&v, &self.value) != Equal,
            ("lt" | "lte" | "gt" | "gte", Some(v)) => {
                if type_class(&v) != type_class(&self.value) {
                    return false;
                }
                let c = cmp_values(&v, &self.value);
                match self.op.as_str() {
                    "lt" => c == Less,
                    "lte" => c != Greater,
                    "gt" => c == Greater,
                    _ => c != Less,
                }
            }
            ("in", Some(v)) => scalar(&v) && self.values.iter().any(|x| cmp_values(&v, x) == Equal),
            ("nin", None) => true,
            ("nin", Some(v)) => {
                !scalar(&v) || !self.values.iter().any(|x| cmp_values(&v, x) == Equal)
            }
            ("prefix", Some(Value::String(s))) => s.starts_with(self.value.as_str().unwrap()),
            ("contains", Some(Value::Array(a))) => a
                .iter()
                .any(|e| scalar(e) && cmp_values(e, &self.value) == Equal),
            _ => false,
        }
    }
}

fn project_doc(obj: &Value, paths: &[String]) -> Value {
    let mut out = Map::new();
    let src = obj.as_object().unwrap();
    for p in paths {
        if let Some(v) = lookup(src, p) {
            if let Some((parent, last)) = parent_mut(&mut out, p, true) {
                parent.insert(last, v.clone());
            }
        }
    }
    Value::Object(out)
}

fn query(st: &mut State, key: &Key, db: &str, coll: &str, body: &[u8]) -> Reply {
    let d = match tenant_db(st, key, db, coll, "read") {
        Ok(d) => d,
        Err(r) => return r,
    };
    let Some(q) = parse_object(body) else {
        return err(400, "invalid_query");
    };
    let known = [
        "filter", "sort", "project", "limit", "cursor", "count", "explain",
    ];
    if q.keys().any(|k| !known.contains(&k.as_str())) {
        return err(400, "invalid_query");
    }
    let limit = q.get("limit").map_or(Some(50), Value::as_u64).unwrap_or(0) as usize;
    let sort = q
        .get("sort")
        .and_then(Value::as_array)
        .cloned()
        .unwrap_or_default();
    let project: Vec<String> = q
        .get("project")
        .and_then(Value::as_array)
        .map(|a| {
            a.iter()
                .filter_map(|v| v.as_str().map(String::from))
                .collect()
        })
        .unwrap_or_default();
    if !(1..=1000).contains(&limit) || sort.len() > 4 || project.len() > 64 {
        return err(400, "invalid_query");
    }
    let Ok(filter) = Filter::parse(q.get("filter").unwrap_or(&Value::Null)) else {
        return err(400, "invalid_query");
    };
    let mut paths = Vec::new();
    let mut desc = Vec::new();
    for s in &sort {
        let Ok(p) = QPath::parse(&s["path"]) else {
            return err(400, "invalid_query");
        };
        let order = s["order"].as_str().unwrap_or("");
        if order != "asc" && order != "desc" {
            return err(400, "invalid_query");
        }
        paths.push(p);
        desc.push(order == "desc");
    }
    desc.push(false);
    let cursor = q.get("cursor").and_then(Value::as_str);
    let count = cursor.is_none() && q.get("count") == Some(&json!(true));
    let digest_src = json!([coll, q.get("filter"), q.get("sort"), q.get("project")]).to_string();
    let digest = hex(&Sha256::digest(digest_src.as_bytes())[..8]);

    // Each row: sort keys (None = missing) plus the ID.
    let c = &d.colls[coll];
    let mut rows: Vec<(Vec<Option<Value>>, &String, &Doc)> = Vec::new();
    for (id, doc) in &c.docs {
        if doc.deleted {
            continue;
        }
        let objs: Vec<Map<String, Value>> = match &doc.siblings {
            Some(s) => s
                .iter()
                .filter(|s| !s.deleted)
                .filter_map(|s| parse_object(s.raw.as_ref()?.as_bytes()))
                .collect(),
            None => vec![parse_object(doc.raw.as_ref().unwrap().as_bytes()).unwrap()],
        };
        if !objs.iter().any(|o| filter.matches(id, o)) {
            continue;
        }
        let mut keys: Vec<Option<Value>> = paths.iter().map(|p| p.get(id, &objs[0])).collect();
        keys.push(Some(Value::String(id.clone())));
        rows.push((keys, id, doc));
    }
    let cmp = |a: &[Option<Value>], b: &[Option<Value>]| {
        for i in 0..a.len() {
            let c = match (&a[i], &b[i]) {
                (None, None) => std::cmp::Ordering::Equal,
                (None, _) => std::cmp::Ordering::Less,
                (_, None) => std::cmp::Ordering::Greater,
                (Some(x), Some(y)) => cmp_values(x, y),
            };
            let c = if desc[i] { c.reverse() } else { c };
            if c != std::cmp::Ordering::Equal {
                return c;
            }
        }
        std::cmp::Ordering::Equal
    };
    rows.sort_by(|a, b| cmp(&a.0, &b.0));
    let total = rows.len();
    let mut start = 0;
    if let Some(cur) = cursor {
        let parsed = B64
            .decode(cur)
            .ok()
            .and_then(|raw| serde_json::from_slice::<Value>(&raw).ok());
        let Some(cv) = parsed.filter(|v| v["d"] == digest.as_str()) else {
            return err(400, "invalid_cursor");
        };
        let pos: Vec<Option<Value>> = cv["k"]
            .as_array()
            .cloned()
            .unwrap_or_default()
            .into_iter()
            .map(|v| if v == json!({"m":1}) { None } else { Some(v) })
            .chain(std::iter::once(Some(cv["id"].clone())))
            .collect();
        if pos.len() != paths.len() + 1 {
            return err(400, "invalid_cursor");
        }
        start = rows
            .iter()
            .position(|r| cmp(&r.0, &pos) == std::cmp::Ordering::Greater)
            .unwrap_or(rows.len());
    }
    let end = (start + limit).min(rows.len());
    let items: Vec<String> = rows[start..end]
        .iter()
        .map(|(_, id, doc)| doc_json(id, doc, &project))
        .collect();
    let next = if end < rows.len() {
        let (keys, id, _) = &rows[end - 1];
        let k: Vec<Value> = keys[..paths.len()]
            .iter()
            .map(|v| v.clone().unwrap_or(json!({"m":1})))
            .collect();
        json!(B64.encode(json!({"d": digest, "k": k, "id": id}).to_string()))
    } else {
        Value::Null
    };
    let mut out = format!(r#"{{"items":[{}],"next_cursor":{next}"#, items.join(","));
    if count {
        out.push_str(&format!(r#","count":{{"value":"{total}","exact":true}}"#));
    }
    out.push('}');
    Reply {
        status: 200,
        headers: Vec::new(),
        body: Some(out),
    }
}

// ---- control plane ----------------------------------------------------------

fn page(ctx: &Ctx, items: Vec<Value>, extra: &[&str]) -> Reply {
    let mut limit = 50usize;
    let mut start = 0usize;
    for (k, v) in &ctx.query {
        if v.is_empty() || !(k == "limit" || k == "cursor" || extra.contains(&k.as_str())) {
            return err(400, "invalid_request");
        }
        if k == "limit" {
            match v.parse::<usize>() {
                Ok(n) if (1..=200).contains(&n) => limit = n,
                _ => return err(400, "invalid_request"),
            }
        }
        if k == "cursor" {
            match B64
                .decode(v)
                .ok()
                .and_then(|b| String::from_utf8(b).ok())
                .and_then(|s| s.parse().ok())
            {
                Some(n) if n <= items.len() => start = n,
                _ => return err(400, "invalid_cursor"),
            }
        }
    }
    let end = (start + limit).min(items.len());
    let next = if end < items.len() {
        json!(B64.encode(end.to_string()))
    } else {
        Value::Null
    };
    reply(
        200,
        json!({"items": items[start..end].to_vec(), "next_cursor": next}),
    )
}

fn tenant_json(t: &Tenant) -> Value {
    json!({"id": t.id, "name": t.name, "external_ref": t.external_ref, "status": t.status, "limits": t.limits,
        "advisor_external": t.advisor_external, "created_at": t.created_at.to_string()})
}

fn db_json(d: &Db) -> Value {
    json!({"id": d.id, "tenant_id": d.tenant_id, "name": d.name, "home_region": d.home_region, "status": d.status,
        "durability": d.durability, "retention": d.retention, "created_at": d.created_at.to_string()})
}

fn coll_json(c: &Coll) -> Value {
    json!({"id": c.id, "name": c.name, "generation": c.generation.to_string(), "schema": c.schema,
        "ttl_path": c.ttl_path, "unique": c.unique_json, "advisor": c.advisor, "created_at": c.created_at.to_string()})
}

fn key_json(k: &Key) -> Value {
    let mut v = json!({"id": k.id, "name": k.name, "role": k.role, "created_at": k.created_at.to_string(),
        "last_four": &k.token[k.token.len() - 4..],
        "expires_at": k.expires_at.map(|e| e.to_string()), "revoked_at": k.revoked_at.map(|e| e.to_string())});
    if k.role == "data" {
        v["tenant_id"] = json!(k.tenant_id);
        v["grants"] = k.grants.clone();
        v["row_filter"] = k.row_filter.clone();
        v["rate"] = k.rate.clone();
    }
    v
}

/// Operations finish on their second poll; collection deletions fail, to
/// exercise the failed-operation shape.
fn op_json(op: &Operation) -> Value {
    let done = op.polls >= 2;
    let failed = done && op.kind == "delete_collection";
    let status = match (done, failed) {
        (false, _) => "running",
        (true, false) => "succeeded",
        (true, true) => "failed",
    };
    let error = if failed {
        json!({"code": "storage_unavailable", "message": "object store unreachable"})
    } else {
        Value::Null
    };
    json!({"id": op.id, "kind": op.kind, "status": status, "resource": op.resource, "error": error,
        "created_at": op.created_at.to_string(),
        "finished_at": if done { json!(op.created_at.to_string()) } else { Value::Null }})
}

fn start_op(st: &mut State, kind: &str, resource: &str) -> Reply {
    let op = Operation {
        id: new_id(),
        kind: kind.into(),
        resource: resource.into(),
        created_at: now_ms(),
        polls: 0,
    };
    let r = reply(202, op_json(&op));
    st.ops.insert(op.id.clone(), op);
    r
}

fn strict(body: &[u8], allowed: &[&str]) -> Option<Map<String, Value>> {
    let m = parse_object(body)?;
    m.keys().all(|k| allowed.contains(&k.as_str())).then_some(m)
}

fn dashboard(st: &mut State, ctx: &Ctx, _key: &Key, rest: &[&str], body: &[u8]) -> Reply {
    let not_found = || err(404, "not_found");
    match (ctx.method, rest) {
        ("POST", ["tenants"]) => {
            let Some(b) = strict(
                body,
                &["name", "external_ref", "limits", "advisor_external"],
            ) else {
                return err(400, "invalid_request");
            };
            let (Some(name), Some(ext)) = (
                b.get("name").and_then(Value::as_str),
                b.get("external_ref").and_then(Value::as_str),
            ) else {
                return err(400, "invalid_request");
            };
            if st.tenants.values().any(|t| t.external_ref == ext) {
                return err(409, "name_taken");
            }
            let t = Tenant {
                id: new_id(),
                name: name.into(),
                external_ref: ext.into(),
                status: "active".into(),
                limits: b.get("limits").cloned().unwrap_or_else(
                    || json!({"max_databases":10,"max_storage_bytes":"10737418240","max_rate_per_second":1000}),
                ),
                advisor_external: b.get("advisor_external") == Some(&json!(true)),
                created_at: now_ms(),
                dbs: HashMap::new(),
                db_order: Vec::new(),
            };
            let r = reply(201, tenant_json(&t));
            st.tenant_order.push(t.id.clone());
            st.tenants.insert(t.id.clone(), t);
            r
        }
        ("GET", ["tenants"]) => {
            let ext = ctx
                .query
                .iter()
                .find(|(k, _)| k == "external_ref")
                .map(|(_, v)| v.clone());
            let items = st
                .tenant_order
                .iter()
                .map(|id| &st.tenants[id])
                .filter(|t| ext.as_ref().is_none_or(|e| &t.external_ref == e))
                .map(tenant_json)
                .collect();
            page(ctx, items, &["external_ref"])
        }
        ("GET", ["tenants", t]) => st
            .tenants
            .get(*t)
            .map_or_else(not_found, |t| reply(200, tenant_json(t))),
        ("PATCH", ["tenants", t]) => {
            let Some(b) = strict(body, &["name", "status", "limits", "advisor_external"]) else {
                return err(400, "invalid_request");
            };
            let Some(t) = st.tenants.get_mut(*t) else {
                return not_found();
            };
            if let Some(n) = b.get("name").and_then(Value::as_str) {
                t.name = n.into();
            }
            if let Some(s) = b.get("status").and_then(Value::as_str) {
                if s != "active" && s != "suspended" {
                    return err(400, "invalid_request");
                }
                t.status = s.into();
            }
            if let Some(l) = b.get("limits") {
                t.limits = l.clone();
            }
            reply(200, tenant_json(t))
        }
        ("DELETE", ["tenants", t]) => {
            let Some(t) = st.tenants.get_mut(*t) else {
                return not_found();
            };
            t.status = "deleting".into();
            start_op(st, "delete_tenant", ctx.path)
        }
        ("POST", ["tenants", t, "databases"]) => {
            let Some(b) = strict(body, &["name", "home_region", "durability", "retention"]) else {
                return err(400, "invalid_request");
            };
            let Some(t) = st.tenants.get_mut(*t) else {
                return not_found();
            };
            let Some(name) = b.get("name").and_then(Value::as_str) else {
                return err(400, "invalid_request");
            };
            if t.dbs.contains_key(name) {
                return err(409, "name_taken");
            }
            if t.dbs.len() as u64 >= t.limits["max_databases"].as_u64().unwrap_or(10) {
                return err(409, "limit_exceeded");
            }
            new_db(
                t,
                name,
                b.get("home_region")
                    .and_then(Value::as_str)
                    .unwrap_or("gru"),
                b.get("retention").cloned(),
            );
            let d = t.dbs.get_mut(name).unwrap();
            if let Some(dur) = b.get("durability").and_then(Value::as_str) {
                d.durability = dur.into();
            }
            reply(201, db_json(d))
        }
        ("GET", ["tenants", t, "databases"]) => {
            let Some(t) = st.tenants.get(*t) else {
                return not_found();
            };
            let items = t
                .db_order
                .iter()
                .filter_map(|n| t.dbs.get(n))
                .map(db_json)
                .collect();
            page(ctx, items, &[])
        }
        ("GET", ["tenants", t, "databases", d]) => st
            .tenants
            .get(*t)
            .and_then(|t| t.dbs.get(*d))
            .map_or_else(not_found, |d| reply(200, db_json(d))),
        ("DELETE", ["tenants", t, "databases", d]) => {
            let Some(db) = st.tenants.get_mut(*t).and_then(|t| t.dbs.get_mut(*d)) else {
                return not_found();
            };
            db.status = "deleting".into();
            start_op(st, "delete_database", ctx.path)
        }
        ("GET", ["operations", id]) => {
            let Some(op) = st.ops.get_mut(*id) else {
                return not_found();
            };
            op.polls += 1;
            reply(200, op_json(op))
        }
        ("POST", ["tenants", t, "databases", d, "collections"]) => {
            let Some(b) = strict(
                body,
                &["name", "schema", "ttl_path", "unique", "indexes", "advisor"],
            ) else {
                return err(400, "invalid_request");
            };
            let Some(db) = st.tenants.get_mut(*t).and_then(|t| t.dbs.get_mut(*d)) else {
                return not_found();
            };
            let Some(name) = b.get("name").and_then(Value::as_str) else {
                return err(400, "invalid_request");
            };
            let schema = b.get("schema").cloned().unwrap_or(Value::Null);
            if !schema.is_null() && schema["fields"].as_array().is_none_or(|f| f.is_empty()) {
                return err(400, "invalid_schema");
            }
            if db.colls.contains_key(name) {
                return err(409, "name_taken");
            }
            let ttl = b.get("ttl_path").and_then(Value::as_str).map(String::from);
            new_coll(
                db,
                name,
                schema,
                ttl,
                b.get("unique").cloned().unwrap_or(Value::Null),
            );
            let c = db.colls.get_mut(name).unwrap();
            if let Some(a) = b.get("advisor").and_then(Value::as_str) {
                c.advisor = a.into();
            }
            reply(201, coll_json(c))
        }
        ("GET", ["tenants", t, "databases", d, "collections"]) => {
            let Some(db) = st.tenants.get(*t).and_then(|t| t.dbs.get(*d)) else {
                return not_found();
            };
            let items = db
                .coll_order
                .iter()
                .filter_map(|n| db.colls.get(n))
                .map(coll_json)
                .collect();
            page(ctx, items, &[])
        }
        ("GET", ["tenants", t, "databases", d, "collections", c]) => st
            .tenants
            .get(*t)
            .and_then(|t| t.dbs.get(*d))
            .and_then(|db| db.colls.get(*c))
            .map_or_else(not_found, |c| reply(200, coll_json(c))),
        ("PATCH", ["tenants", t, "databases", d, "collections", c]) => {
            let Some(b) = strict(body, &["schema", "ttl_path", "unique", "advisor"]) else {
                return err(400, "invalid_request");
            };
            let Some(c) = st
                .tenants
                .get_mut(*t)
                .and_then(|t| t.dbs.get_mut(*d))
                .and_then(|db| db.colls.get_mut(*c))
            else {
                return not_found();
            };
            if let Some(s) = b.get("schema") {
                if s.is_null() != c.schema.is_null() {
                    return err(409, "codec_conversion_unsupported");
                }
                if !s.is_null() {
                    c.schema = s.clone();
                    c.generation += 1;
                }
            }
            if let Some(ttl) = b.get("ttl_path") {
                c.ttl_path = ttl.as_str().map(String::from);
            }
            if let Some(u) = b.get("unique") {
                c.unique = parse_unique(u);
                c.unique_json = u.clone();
            }
            if let Some(a) = b.get("advisor").and_then(Value::as_str) {
                c.advisor = a.into();
            }
            reply(200, coll_json(c))
        }
        ("DELETE", ["tenants", t, "databases", d, "collections", c]) => {
            if st
                .tenants
                .get(*t)
                .and_then(|t| t.dbs.get(*d))
                .and_then(|db| db.colls.get(*c))
                .is_none()
            {
                return not_found();
            }
            start_op(st, "delete_collection", ctx.path)
        }
        ("POST", ["tenants", t, "keys"]) => {
            let Some(b) = strict(
                body,
                &["name", "grants", "row_filter", "rate", "ttl_seconds"],
            ) else {
                return err(400, "invalid_request");
            };
            if !st.tenants.contains_key(*t) {
                return not_found();
            }
            let (Some(name), Some(grants)) = (
                b.get("name").and_then(Value::as_str),
                b.get("grants")
                    .filter(|g| g.as_array().is_some_and(|a| !a.is_empty())),
            ) else {
                return err(400, "invalid_request");
            };
            let expires_at = b
                .get("ttl_seconds")
                .and_then(Value::as_u64)
                .map(|s| now_ms() + s * 1000);
            let token = st.add_key(Key {
                id: String::new(),
                token: String::new(),
                name: name.into(),
                role: "data".into(),
                tenant_id: (*t).into(),
                grants: grants.clone(),
                row_filter: b.get("row_filter").cloned().unwrap_or(Value::Null),
                rate: b
                    .get("rate")
                    .cloned()
                    .unwrap_or_else(|| json!({"per_second":100,"burst":200,"max_in_flight":16})),
                created_at: 0,
                expires_at,
                revoked_at: None,
            });
            reply(
                201,
                json!({"key": key_json(&st.keys[&token]), "token": token}),
            )
        }
        ("GET", ["tenants", t, "keys"]) => {
            let mut keys: Vec<&Key> = st
                .keys
                .values()
                .filter(|k| k.tenant_id == *t && k.role == "data")
                .collect();
            keys.sort_by(|a, b| (a.created_at, &a.id).cmp(&(b.created_at, &b.id)));
            page(ctx, keys.into_iter().map(key_json).collect(), &[])
        }
        ("GET", ["tenants", t, "keys", k]) => st
            .keys
            .values()
            .find(|x| x.tenant_id == *t && x.role == "data" && x.id == *k)
            .map_or_else(not_found, |k| reply(200, key_json(k))),
        ("DELETE", ["tenants", t, "keys", k]) => {
            let Some(key) = st
                .keys
                .values_mut()
                .find(|x| x.tenant_id == *t && x.role == "data" && x.id == *k)
            else {
                return not_found();
            };
            key.revoked_at = Some(now_ms());
            reply(200, key_json(key))
        }
        ("POST", ["tenants", t, "keys", k, "rotate"]) => {
            let Some(b) = strict(body, &["grace_seconds"]) else {
                return err(400, "invalid_request");
            };
            let grace = b.get("grace_seconds").map_or(Some(0), Value::as_u64);
            let Some(grace) = grace.filter(|g| *g <= 86400) else {
                return err(400, "invalid_request");
            };
            let Some(old) = st
                .keys
                .values_mut()
                .find(|x| x.tenant_id == *t && x.role == "data" && x.id == *k)
            else {
                return not_found();
            };
            old.expires_at = Some(now_ms() + grace * 1000);
            let mut next = old.clone();
            next.revoked_at = None;
            next.expires_at = None;
            let token = st.add_key(next);
            reply(
                201,
                json!({"key": key_json(&st.keys[&token]), "token": token}),
            )
        }
        ("GET", ["regions"]) => page(
            ctx,
            vec![json!({"code":"gru","name":"São Paulo","status":"available"})],
            &[],
        ),
        _ => err(404, "unknown_route"),
    }
}
