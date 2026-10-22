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

use super::exact_json::{fold, Dec, Obj, J};
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
    /// A raw response body replacing the JSON error object.
    pub body: Option<&'static str>,
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
            body: None,
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
    /// Tenant ID and, for database deletions, the database name.
    target: (String, Option<String>),
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
    times: HashMap<String, Vec<SystemTime>>,
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
        if let Some(body) = f.body {
            r.body = Some(body.to_owned());
        }
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

    /// When each request for `METHOD /path` arrived.
    pub fn request_times(&self, method: &str, path: &str) -> Vec<SystemTime> {
        self.state
            .lock()
            .unwrap()
            .times
            .get(&format!("{method} {path}"))
            .cloned()
            .unwrap_or_default()
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

// ---- data plane -----------------------------------------------------------

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

    fn unique_violation(&self, db: &Db, coll: &str, id: &str, obj: &Obj) -> Option<String> {
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

type PatchBox = dyn Fn(&mut Obj) -> Result<(), Reply>;
type PatchFn<'a> = &'a dyn Fn(&mut Obj) -> Result<(), Reply>;

// ---- query ------------------------------------------------------------------

impl Filter {
    fn parse(v: &J) -> Result<Option<Filter>, ()> {
        if v.is_null() {
            return Ok(None);
        }
        Self::build(v, 0).map(Some)
    }

    fn build(v: &J, depth: usize) -> Result<Filter, ()> {
        let m = v.as_object().ok_or(())?;
        if m.len() != 1 || depth > 8 {
            return Err(());
        }
        let (op, arg) = m.iter().next().unwrap();
        let mut f = Filter {
            op: op.clone(),
            children: Vec::new(),
            path: None,
            value: J::Null,
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
    fn matches(&self, id: &str, obj: &Obj) -> bool;
}

impl Matches for Option<Filter> {
    fn matches(&self, id: &str, obj: &Obj) -> bool {
        self.as_ref().is_none_or(|f| f.matches(id, obj))
    }
}

impl Filter {
    fn matches(&self, id: &str, obj: &Obj) -> bool {
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
            ("prefix", Some(J::Str(s))) => s.starts_with(self.value.as_str().unwrap()),
            ("contains", Some(J::Arr(a))) => a
                .iter()
                .any(|e| scalar(e) && cmp_values(e, &self.value) == Equal),
            _ => false,
        }
    }
}

// ---- control plane ----------------------------------------------------------

#[derive(Clone, Copy)]
enum Kind {
    Str,
    Name,
    Bool,
    Obj,
    Arr,
    OptStr,
    OptObj,
    OneOf(&'static [&'static str]),
}

