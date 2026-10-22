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

