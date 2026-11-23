use std::collections::BTreeMap;
use std::io::Write;
use std::os::unix::fs::OpenOptionsExt;
use std::time::{Instant, SystemTime, UNIX_EPOCH};

use base64::{engine::general_purpose::STANDARD, Engine};
use serde_json::{json, Value};
use terrascale::v1::{
    is_valid_id, new_id, Client, CreateDatabaseOptions, Database, DatabasePlacement, Error,
    ForkDatabaseOptions, FunctionDeployment, ManagedDatabase, ScopedKeyAccess, ScopedKeyOptions,
    SqlRecord, SqlSession, SqlStatement, SqlStatementResult, SqlValue,
};
use terrascale::ClientConfig;

type Outcome = Result<(), String>;

struct Runner {
    client: Client,
    ca: Vec<u8>,
    project: String,
    table: String,
    run: String,
    db: Option<Database>,
    session: Option<SqlSession>,
    agent: Option<ManagedDatabase>,
}

fn text(op: &Value, key: &str) -> String {
    op[key].as_str().unwrap_or_default().to_string()
}

fn cell(value: &SqlValue) -> Value {
    match value {
        SqlValue::Null => Value::Null,
        SqlValue::Integer(n) => json!(n),
        SqlValue::Real(r) => json!(r),
        SqlValue::Text(t) => json!(t),
        SqlValue::Blob(b) => json!(STANDARD.encode(b)),
    }
}

fn grid(rows: &[Vec<SqlValue>]) -> Value {
    Value::Array(
        rows.iter()
            .map(|row| Value::Array(row.iter().map(cell).collect()))
            .collect(),
    )
}

fn arg(value: &Value) -> SqlValue {
    match value {
        Value::Null => SqlValue::Null,
        Value::Bool(b) => SqlValue::Integer(i64::from(*b)),
        Value::Number(n) if n.is_i64() => SqlValue::Integer(n.as_i64().unwrap_or_default()),
        Value::Number(n) => SqlValue::Real(n.as_f64().unwrap_or_default()),
        Value::String(s) => SqlValue::Text(s.clone()),
        other => SqlValue::Text(other.to_string()),
    }
}

fn args(op: &Value, key: &str) -> Vec<SqlValue> {
    op[key]
        .as_array()
        .map(|values| values.iter().map(arg).collect())
        .unwrap_or_default()
}

fn same(actual: Value, expected: &Value, what: &str) -> Outcome {
    if &actual == expected {
        Ok(())
    } else {
        Err(format!("{what}: expected {expected}, got {actual}"))
    }
}

fn status(result: Result<impl Sized, Error>) -> Value {
    match result {
        Ok(_) => json!("accepted"),
        Err(error) => json!(error.status()),
    }
}

fn register_secret(name: &str, value: &str) -> Outcome {
    let secrets = std::env::var("E2E_SECRETS").map_err(|error| error.to_string())?;
    std::fs::OpenOptions::new()
        .write(true)
        .create(true)
        .truncate(true)
        .mode(0o600)
        .open(std::path::Path::new(&secrets).join(name))
        .and_then(|mut file| file.write_all(value.as_bytes()))
        .map_err(|error| error.to_string())
}

fn origin_rule(project: &str, file: &str) -> Outcome {
    let operations = std::env::var("E2E_OPERATIONS").map_err(|error| error.to_string())?;
    let path = std::path::Path::new(&operations).with_file_name(file);
    let text = std::fs::read_to_string(path).map_err(|error| error.to_string())?;
    let spec: Value = serde_json::from_str(&text).map_err(|error| error.to_string())?;
    let cases = spec["cases"].as_array().cloned().unwrap_or_default();
    let mut wrong = Vec::new();
    for (index, case) in cases.iter().enumerate() {
        let raw = case[0].as_str().unwrap_or_default();
        let api = Client::builder(raw, "eyJhbGciOiJFUzI1NiJ9.eyJzdWIiOiJ4In0.c2ln")
            .project(project)
            .build()
            .ok()
            .map(|client| client.base_url().origin().ascii_serialization());
        let auth = ClientConfig::builder()
            .auth_base_url(raw)
            .build()
            .ok()
            .and_then(|config| config.auth_base_url().map(str::to_owned));
        for (column, expected, got) in [
            ("api", case[1].as_str(), api),
            ("auth", case[2].as_str(), auth),
        ] {
            if got.as_deref() != expected {
                wrong.push(format!(
                    "#{index} {:?} {column}: expected {}, got {}",
                    raw.chars().take(60).collect::<String>(),
                    expected.unwrap_or("refused"),
                    got.as_deref().unwrap_or("refused")
                ));
            }
        }
    }
    if wrong.is_empty() {
        Ok(())
    } else {
        Err(format!(
            "origin rule: {} of {} checks differ: {}",
            wrong.len(),
            2 * cases.len(),
            wrong.join("; ")
        ))
    }
}

fn records(records: &[SqlRecord]) -> Value {
    Value::Array(
        records
            .iter()
            .map(|record| Value::Array(record.values().iter().map(cell).collect()))
            .collect(),
    )
}

fn last(results: &[SqlStatementResult]) -> Value {
    results
        .last()
        .map(|result| grid(&result.rows))
        .unwrap_or(Value::Null)
}

fn unix_now() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_secs() as i64)
        .unwrap_or_default()
}

fn unix_time(text: &str) -> Result<i64, String> {
    let (date, time) = text
        .split_once('T')
        .ok_or_else(|| format!("expiry {text} is not RFC 3339"))?;
    let mut date = date.split('-');
    let year: i64 = date
        .next()
        .unwrap_or_default()
        .parse()
        .map_err(|_| format!("expiry {text}"))?;
    let month: i64 = date
        .next()
        .unwrap_or_default()
        .parse()
        .map_err(|_| format!("expiry {text}"))?;
    let day: i64 = date
        .next()
        .unwrap_or_default()
        .parse()
        .map_err(|_| format!("expiry {text}"))?;
    let clock = time.trim_end_matches('Z');
    let mut clock = clock.split(':');
    let hour: i64 = clock
        .next()
        .unwrap_or_default()
        .parse()
        .map_err(|_| format!("expiry {text}"))?;
    let minute: i64 = clock
        .next()
        .unwrap_or_default()
        .parse()
        .map_err(|_| format!("expiry {text}"))?;
    let second: i64 = clock
        .next()
        .unwrap_or_default()
        .split('.')
        .next()
        .unwrap_or_default()
        .parse()
        .map_err(|_| format!("expiry {text}"))?;
    let shifted = month + if month > 2 { -3 } else { 9 };
    let year = year - i64::from(month <= 2);
    let era = year.div_euclid(400);
    let yoe = year - era * 400;
    let doy = (153 * shifted + 2) / 5 + day - 1;
    let doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    Ok((era * 146_097 + doe - 719_468) * 86_400 + hour * 3_600 + minute * 60 + second)
}

fn utc(offset: i64) -> String {
    let seconds = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_secs() as i64)
        .unwrap_or_default()
        + offset;
    let (days, rest) = (seconds.div_euclid(86_400), seconds.rem_euclid(86_400));
    let z = days + 719_468;
    let era = z.div_euclid(146_097);
    let doe = z - era * 146_097;
    let yoe = (doe - doe / 1_460 + doe / 36_524 - doe / 146_096) / 365;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let day = doy - (153 * mp + 2) / 5 + 1;
    let month = if mp < 10 { mp + 3 } else { mp - 9 };
    let year = yoe + era * 400 + i64::from(month <= 2);
    format!(
        "{year:04}-{month:02}-{day:02}T{:02}:{:02}:{:02}Z",
        rest / 3_600,
        rest % 3_600 / 60,
        rest % 60
    )
}

impl Runner {
    fn fill(&self, sql: &str) -> String {
        sql.replace("{table}", &self.table)
            .replace("{sdk}", "rust")
            .replace("{run}", &self.run)
    }

    fn statements(&self, op: &Value, key: &str) -> Vec<SqlStatement> {
        op[key]
            .as_array()
            .into_iter()
            .flatten()
            .map(|sql| SqlStatement::new(self.fill(sql.as_str().unwrap_or_default())))
            .collect()
    }

    fn db(&self) -> &Database {
        self.db.as_ref().expect("groups a and c have a database")
    }

    fn session(&self) -> Result<&SqlSession, String> {
        self.session
            .as_ref()
            .ok_or_else(|| "the session is not connected".to_string())
    }

    async fn count(&self, op: &Value) -> Outcome {
        let rows = self
            .db()
            .sql()
            .query(&self.fill(&text(op, "check")), &[])
            .await
            .map_err(|e| e.to_string())?;
        same(records(&rows), &op["checkRows"], "rows after the call")
    }

    async fn do_op(&mut self, op: &Value) -> Outcome {
        let id = text(op, "id");
        let fill = |key: &str| self.fill(&text(op, key));
        let e = |error: Error| error.to_string();
        match id.as_str() {
            "auth.organizations" => {
                let config = ClientConfig::builder()
                    .auth_base_url("https://auth.terrascale.tech")
                    .build()
                    .map_err(|error| error.to_string())?;
                let transport =
                    terrascale::transport::ReqwestTransport::with_root_certificates(&self.ca)
                        .map_err(|error| error.to_string())?;
                let auth = terrascale::TerraScaleClient::with_transport(
                    config,
                    std::sync::Arc::new(transport),
                )
                .projects()
                .map_err(|error| error.to_string())?;
                let read = std::env::var("E2E_AUTH_READ_KEY").map_err(|error| error.to_string())?;
                let write =
                    std::env::var("E2E_AUTH_WRITE_KEY").map_err(|error| error.to_string())?;
                let user = std::env::var("E2E_AUTH_USER").map_err(|error| error.to_string())?;
                let created = auth
                    .create_organization(
                        &write,
                        &self.project,
                        &terrascale::auth_projects::OrganizationRequest {
                            name: "SDK organization".into(),
                            slug: self.table.replace('_', "-"),
                            admin_user_id: Some(user.clone()),
                            image_url: None,
                            public_metadata: None,
                            private_metadata: None,
                            max_members: None,
                            domain_join: "off".into(),
                        },
                    )
                    .await
                    .map_err(|error| error.to_string())?;
                let found = auth
                    .get_organization(&read, &self.project, &created.id)
                    .await
                    .map_err(|error| error.to_string())?;
                same(json!(found.id), &json!(created.id), "organization read")?;
                let listed = auth
                    .list_organizations(&read, &self.project, Some(100), None)
                    .await
                    .map_err(|error| error.to_string())?;
                same(
                    json!(listed.items.iter().any(|item| item.id == created.id)),
                    &json!(true),
                    "organization listed",
                )?;
                let members = auth
                    .list_organization_members(&read, &self.project, &created.id, Some(100), None)
                    .await
                    .map_err(|error| error.to_string())?;
                same(
                    json!(members.items[0].role),
                    &json!("admin"),
                    "initial admin",
                )?;
                auth.create_organization_invitation(
                    &write,
                    &self.project,
                    &created.id,
                    &terrascale::auth_projects::OrganizationInvitationRequest {
                        inviter_name: None,
                        email: format!("org-{}@example.com", self.table),
                        role: "member".into(),
                    },
                )
                .await
                .map_err(|error| error.to_string())?;
                let invites = auth
                    .list_organization_invitations(
                        &read,
                        &self.project,
                        &created.id,
                        Some(100),
                        None,
                    )
                    .await
                    .map_err(|error| error.to_string())?;
                same(
                    json!(invites.items[0].status),
                    &json!("pending"),
                    "invitation listed",
                )?;
                auth.delete_organization(&write, &self.project, &created.id)
                    .await
                    .map_err(|error| error.to_string())
            }
            "auth.manage_user" => {
                let config = ClientConfig::builder()
                    .auth_base_url("https://auth.terrascale.tech")
                    .build()
                    .map_err(|error| error.to_string())?;
                let transport =
                    terrascale::transport::ReqwestTransport::with_root_certificates(&self.ca)
                        .map_err(|error| error.to_string())?;
                let auth = terrascale::TerraScaleClient::with_transport(
                    config,
                    std::sync::Arc::new(transport),
                )
                .projects()
                .map_err(|error| error.to_string())?;
                let read = std::env::var("E2E_AUTH_READ_KEY").map_err(|error| error.to_string())?;
                let write =
                    std::env::var("E2E_AUTH_WRITE_KEY").map_err(|error| error.to_string())?;
                let user = std::env::var("E2E_AUTH_USER").map_err(|error| error.to_string())?;
                let listed = auth
                    .list_users(&read, &self.project, None, None, None)
                    .await
                    .map_err(|error| error.to_string())?;
                if !listed.items.iter().any(|item| item.id == user) {
                    return Err("read key did not list user".into());
                }
                match auth.disable_user(&read, &self.project, &user).await {
                    Err(error) if error.status == Some(403) => (),
                    _ => return Err("read key was not refused".into()),
                }
                let disabled = auth
                    .disable_user(&write, &self.project, &user)
                    .await
                    .map_err(|error| error.to_string())?;
                same(json!(disabled.disabled), &json!(true), "write key disables")?;
                let enabled = auth
                    .enable_user(&write, &self.project, &user)
                    .await
                    .map_err(|error| error.to_string())?;
                same(json!(enabled.disabled), &json!(false), "write key enables")?;
                let key = auth
                    .create_user_api_key(
                        &write,
                        &self.project,
                        &user,
                        &terrascale::auth_projects::CreateUserApiKey {
                            name: "journey".into(),
                            scopes: vec!["invoices:read".into()],
                            expires_at: None,
                        },
                    )
                    .await
                    .map_err(|error| error.to_string())?;
                let secret = key.key.as_deref().ok_or("missing user key secret")?;
                register_secret("user-api-key-rust", secret)?;
                let keys = auth
                    .list_user_api_keys(&read, &self.project, &user)
                    .await
                    .map_err(|error| error.to_string())?;
                if !keys.items.iter().any(|item| item.id == key.id) {
                    return Err("user key not listed".into());
                }
                let verified = auth
                    .verify_user_api_key(&read, &self.project, secret)
                    .await
                    .map_err(|error| error.to_string())?;
                if !verified.valid
                    || verified.user_id.as_deref() != Some(user.as_str())
                    || verified.scopes != Some(vec!["invoices:read".into()])
                {
                    return Err("user key did not verify".into());
                }
                auth.revoke_user_api_key(&write, &self.project, &user, &key.id)
                    .await
                    .map_err(|error| error.to_string())?;
                let invalid = auth
                    .verify_user_api_key(&read, &self.project, secret)
                    .await
                    .map_err(|error| error.to_string())?;
                same(
                    json!(invalid.valid),
                    &json!(false),
                    "revoked user key invalid immediately",
                )
            }
            "auth.user_profiles" => {
                use terrascale::auth_projects::{
                    CreateProjectUser, ImportProjectUser, ImportProjectUsers, UpdateProjectUser,
                    UserProfile,
                };
                let config = ClientConfig::builder()
                    .auth_base_url("https://auth.terrascale.tech")
                    .build()
                    .map_err(|error| error.to_string())?;
                let transport =
                    terrascale::transport::ReqwestTransport::with_root_certificates(&self.ca)
                        .map_err(|error| error.to_string())?;
                let auth = terrascale::TerraScaleClient::with_transport(
                    config,
                    std::sync::Arc::new(transport),
                )
                .projects()
                .map_err(|error| error.to_string())?;
                let write =
                    std::env::var("E2E_AUTH_WRITE_KEY").map_err(|error| error.to_string())?;
                let created = auth
                    .create_user(
                        &write,
                        &self.project,
                        &CreateProjectUser {
                            email: format!("profile-rust-{}@example.com", self.run),
                            profile: UserProfile {
                                first_name: Some("SDK".into()),
                                public_metadata: Some(
                                    serde_json::from_value(json!({"keep": 1, "remove": true}))
                                        .unwrap(),
                                ),
                                ..Default::default()
                            },
                            ..Default::default()
                        },
                    )
                    .await
                    .map_err(|error| error.to_string())?;
                same(json!(created.first_name), &json!("SDK"), "created profile")?;
                let changed = auth
                    .update_user(
                        &write,
                        &self.project,
                        &created.id,
                        &UpdateProjectUser {
                            public_metadata: Some(
                                serde_json::from_value(json!({"remove": null, "added": "yes"}))
                                    .unwrap(),
                            ),
                            private_metadata: Some(
                                serde_json::from_value(json!({"backend": true})).unwrap(),
                            ),
                            ..Default::default()
                        },
                    )
                    .await
                    .map_err(|error| error.to_string())?;
                same(
                    json!(changed.public_metadata),
                    &json!({"keep": 1, "added": "yes"}),
                    "metadata merge",
                )?;
                let imported = auth
                    .import_users(
                        &write,
                        &self.project,
                        &ImportProjectUsers {
                            users: vec![
                                ImportProjectUser {
                                    email: format!("import-rust-{}@example.com", self.run),
                                    email_verified: true,
                                    password_hash: Some(text(op, "password_hash")),
                                    ..Default::default()
                                },
                                ImportProjectUser {
                                    email: format!("invalid-rust-{}@example.com", self.run),
                                    password_hash: Some("malformed".into()),
                                    ..Default::default()
                                },
                            ],
                        },
                    )
                    .await
                    .map_err(|error| error.to_string())?;
                same(json!(imported.items.len()), &json!(1), "import valid row")?;
                same(
                    json!(imported.errors[0].index),
                    &json!(1),
                    "refuse malformed row",
                )?;
                auth.delete_user(&write, &self.project, &imported.items[0].user.id)
                    .await
                    .map_err(|error| error.to_string())?;
                auth.delete_user(&write, &self.project, &created.id)
                    .await
                    .map_err(|error| error.to_string())?;
                Ok(())
            }
            "sql.execute.positional" => {
                let sql = self.db().sql();
                sql.execute(&fill("setup"), &[]).await.map_err(e)?;
                let result = sql
                    .execute(&fill("sql"), &args(op, "args"))
                    .await
                    .map_err(e)?;
                same(
                    json!(result.rows_affected),
                    &op["rowsAffected"],
                    "rows affected",
                )
            }
            "sql.execute.named" => {
                let named: Vec<(String, SqlValue)> = op["named"]
                    .as_object()
                    .into_iter()
                    .flatten()
                    .map(|(k, v)| (k.clone(), arg(v)))
                    .collect();
                let result = self
                    .db()
                    .sql()
                    .execute_named(&fill("sql"), named)
                    .await
                    .map_err(e)?;
                same(
                    json!(result.rows_affected),
                    &op["rowsAffected"],
                    "rows affected",
                )
            }
            "sql.execute.statement" => {
                let statement = SqlStatement::new(fill("sql")).args(args(op, "args"));
                let result = self
                    .db()
                    .sql()
                    .execute_statement(statement)
                    .await
                    .map_err(e)?;
                same(
                    json!(result.rows_affected),
                    &op["rowsAffected"],
                    "rows affected",
                )?;
                self.count(op).await
            }
            "sql.query" => {
                let rows = self.db().sql().query(&fill("sql"), &[]).await.map_err(e)?;
                same(records(&rows), &op["rows"], "rows")
            }
            "sql.query.typed" => {
                let typed: Vec<Value> = self
                    .db()
                    .sql()
                    .query_as(&fill("sql"), &args(op, "args"))
                    .await
                    .map_err(e)?;
                let objects: Vec<Value> = typed
                    .iter()
                    .map(|row| json!({ "id": row["id"], "n": row["n"] }))
                    .collect();
                same(Value::Array(objects), &op["objects"], "typed rows")
            }
            "sql.batch" => {
                let sql = self.db().sql();
                let statements = self.statements(op, "statements");
                let expected = statements.len();
                let results = sql.batch(statements).await.map_err(e)?;
                same(json!(results.len()), &json!(expected), "results")?;
                same(last(&results), &op["lastRows"], "last rows")?;
                let refused = sql.batch(self.statements(op, "failing")).await;
                let error = refused.err().ok_or("the batch was accepted")?;
                let sql_error = error
                    .sql()
                    .ok_or_else(|| format!("not a SqlError: {error}"))?;
                same(
                    json!([sql_error.code, sql_error.statement]),
                    &json!([op["code"], op["statement"]]),
                    "batch refusal",
                )?;
                self.count(op).await
            }
            "sql.refused" => {
                let error = self
                    .db()
                    .sql()
                    .execute(&fill("sql"), &[])
                    .await
                    .err()
                    .ok_or("the statement was accepted")?;
                let sql_error = error
                    .sql()
                    .ok_or_else(|| format!("not a SqlError: {error}"))?;
                same(
                    json!([
                        sql_error.code,
                        sql_error.statement,
                        sql_error.sqlstate,
                        error.status()
                    ]),
                    &json!([op["code"], op["statement"], op["sqlstate"], op["status"]]),
                    "SqlError",
                )
            }
            "values.roundtrip" => {
                let v = &op["values"];
                let sql = self.db().sql();
                sql.execute(&fill("setup"), &[]).await.map_err(e)?;
                let big: i64 = v["big"]
                    .as_str()
                    .unwrap_or_default()
                    .parse()
                    .map_err(|_| "big is not an int64")?;
                let blob = STANDARD
                    .decode(v["b"].as_str().unwrap_or_default())
                    .map_err(|e| e.to_string())?;
                let values = vec![
                    arg(&v["k"]),
                    arg(&v["n"]),
                    SqlValue::Integer(big),
                    arg(&v["r"]),
                    arg(&v["flag"]),
                    arg(&v["t"]),
                    SqlValue::Blob(blob),
                    SqlValue::Null,
                ];
                sql.execute(&fill("sql"), &values).await.map_err(e)?;
                let result = sql.execute(&fill("select"), &[]).await.map_err(e)?;
                let row = result.rows.first().ok_or("no row")?;
                let got: Vec<Value> = row.iter().map(cell).collect();
                let mut actual = got.clone();
                actual[2] = json!(match &row[2] {
                    SqlValue::Integer(n) => n.to_string(),
                    other => format!("{other:?}"),
                });
                same(
                    Value::Array(actual),
                    &json!([
                        v["k"],
                        v["n"],
                        v["big"],
                        v["r"],
                        op["storedFlag"],
                        v["t"],
                        v["b"],
                        v["absent"]
                    ]),
                    "values",
                )
            }
            "ws.connect" => {
                self.session = Some(self.db().connect().await.map_err(e)?);
                Ok(())
            }
            "ws.statements" => {
                let session = self.session()?;
                let result = session.execute(&fill("sql"), &[]).await.map_err(e)?;
                same(json!(result.rows_affected), &json!(1), "rows affected")?;
                let rows = session.query(&fill("query"), &[]).await.map_err(e)?;
                same(records(&rows), &op["rows"], "rows")?;
                let results = session
                    .batch(self.statements(op, "statements"))
                    .await
                    .map_err(e)?;
                same(last(&results), &op["lastRows"], "last rows")
            }
            "ws.begin_commit" | "ws.rollback" => {
                let session = self.session()?;
                session.begin().await.map_err(e)?;
                session.execute(&fill("sql"), &[]).await.map_err(e)?;
                if id == "ws.rollback" {
                    session.rollback().await.map_err(e)?;
                } else {
                    session.commit().await.map_err(e)?;
                }
                self.count(op).await
            }
            "ws.transaction.commit" => {
                let sql = fill("sql");
                self.session()?
                    .transaction(
                        |session| async move { session.execute(&sql, &[]).await.map(|_| ()) },
                    )
                    .await
                    .map_err(e)?;
                self.count(op).await
            }
            "ws.transaction.rethrow" => {
                let sql = fill("sql");
                let message = text(op, "message");
                let result: Result<(), Error> = self
                    .session()?
                    .transaction(|session| async move {
                        session.execute(&sql, &[]).await?;
                        Err(Error::InvalidInput(message))
                    })
                    .await;
                match result {
                    Err(Error::InvalidInput(message)) if message == text(op, "message") => {
                        self.count(op).await
                    }
                    other => Err(format!("the transaction returned {other:?}")),
                }
            }
            "ws.in_transaction" => {
                let session = self.session()?;
                session.begin().await.map_err(e)?;
                let inside = session.in_transaction();
                session.commit().await.map_err(e)?;
                same(
                    json!([inside, session.in_transaction()]),
                    &json!([true, false]),
                    "inTransaction",
                )
            }
            "ws.close" => {
                let session = self.session()?;
                session.close().await;
                match session.execute(&text(op, "sql"), &[]).await {
                    Ok(_) => Err("the closed session answered".into()),
                    Err(_) => Ok(()),
                }
            }
            "conflicts.query" => {
                let window = [
                    SqlValue::Text(self.table.clone()),
                    SqlValue::Text(utc(-3_600)),
                    SqlValue::Text(utc(3_600)),
                ];
                let rows = self
                    .db()
                    .sql()
                    .query(&text(op, "sql"), &window)
                    .await
                    .map_err(e)?;
                same(records(&rows), &op["rows"], "conflict rows")
            }
            "ids.new_validate" => same(
                json!([is_valid_id(&new_id()), is_valid_id(&text(op, "invalid"))]),
                &json!([true, false]),
                "validation",
            ),
            "origin.rule" => origin_rule(&self.project, &text(op, "cases")),
            "rules.manage" => {
                use terrascale::v1::{
                    RulesSet, RulesTable, RulesTestAuth, RulesTestRequest, RulesValues,
                };
                let before = match self.db().get_rules().await {
                    Ok(rules) => Some(rules),
                    Err(error) if error.code() == Some("no_rules") => None,
                    Err(error) => return Err(e(error)),
                };
                let rules = RulesSet {
                    tables: [(
                        self.table.clone(),
                        RulesTable {
                            read: Some("row.id = auth.uid AND auth.claims.plan = 'pro'".into()),
                            create: Some("auth.claims.plan = 'pro'".into()),
                            columns: Some(vec!["id".into(), "label".into()]),
                            writable: Some(vec!["label".into()]),
                            set: Some(RulesValues {
                                create: Some([("id".into(), "auth.uid".into())].into()),
                                ..Default::default()
                            }),
                            ..Default::default()
                        },
                    )]
                    .into(),
                };
                let deployed = self
                    .db()
                    .deploy_rules(&rules, before.as_ref().map(|r| r.version.as_str()))
                    .await
                    .map_err(e)?;
                let got = self.db().get_rules().await.map_err(e)?;
                if got.version != deployed.version
                    || got.tables[&self.table].read.as_deref()
                        != Some("row.id = auth.uid AND auth.claims.plan = 'pro'")
                {
                    return Err("rules read differs".into());
                }
                let config = ClientConfig::builder()
                    .auth_base_url("https://auth.terrascale.tech")
                    .build()
                    .map_err(|error| error.to_string())?;
                let transport =
                    terrascale::transport::ReqwestTransport::with_root_certificates(&self.ca)
                        .map_err(|error| error.to_string())?;
                let auth = terrascale::TerraScaleClient::with_transport(
                    config,
                    std::sync::Arc::new(transport),
                )
                .projects()
                .map_err(|error| error.to_string())?;
                let user = std::env::var("E2E_AUTH_USER").map_err(|error| error.to_string())?;
                let key = std::env::var("E2E_AUTH_MINT_KEY").map_err(|error| error.to_string())?;
                let database =
                    std::env::var("E2E_DATABASE_ID").map_err(|error| error.to_string())?;
                let claims = [("plan".into(), json!("pro"))].into();
                let custom = auth
                    .mint_custom_token(&key, &self.project, &user, Some(&database), Some(&claims))
                    .await
                    .map_err(|error| error.to_string())?;
                register_secret("custom-token-rust", &custom.access_token)?;
                if custom.token_type != "Bearer" || custom.expires_in != 300 {
                    return Err("custom token lifetime differs".into());
                }
                self.db()
                    .sql()
                    .execute(
                        &format!(
                            "INSERT INTO {}(rowid,id,label) VALUES (700002,'other','hidden')",
                            self.table
                        ),
                        &[],
                    )
                    .await
                    .map_err(|error| error.to_string())?;
                let browser = reqwest::Client::builder()
                    .add_root_certificate(
                        reqwest::Certificate::from_pem(&self.ca)
                            .map_err(|error| error.to_string())?,
                    )
                    .build()
                    .map_err(|error| error.to_string())?;
                let path = format!(
                    "https://api.terrascale.tech/v1/projects/{}/databases/{}/tables/{}/rows/",
                    self.project, database, self.table
                );
                let created = browser
                    .post(path.trim_end_matches('/'))
                    .bearer_auth(&custom.access_token)
                    .json(&json!({"row": {"label": "custom-token"}}))
                    .send()
                    .await
                    .map_err(|error| error.to_string())?;
                let status = created.status().as_u16();
                let written: Value = created.json().await.map_err(|error| error.to_string())?;
                if status != 201 || written["row"] != json!({"id": user, "label": "custom-token"}) {
                    return Err("custom token Rules write differs".into());
                }
                let key = written["key"]
                    .as_str()
                    .ok_or("custom token row key missing")?;
                let row = browser
                    .get(format!("{path}{key}"))
                    .bearer_auth(&custom.access_token)
                    .send()
                    .await
                    .map_err(|error| error.to_string())?;
                let status = row.status().as_u16();
                let body: Value = row.json().await.map_err(|error| error.to_string())?;
                if status != 200 || body["row"] != json!({"id": user, "label": "custom-token"}) {
                    return Err("custom token Rules read differs".into());
                }
                let hidden = browser
                    .get(format!("{path}700002"))
                    .bearer_auth(&custom.access_token)
                    .send()
                    .await
                    .map_err(|error| error.to_string())?;
                let status = hidden.status().as_u16();
                let body: Value = hidden.json().await.map_err(|error| error.to_string())?;
                if status != 404 || body["error"]["code"] != "row_not_found" {
                    return Err("custom token revealed another user's row".into());
                }
                let update = browser
                    .patch(format!("{path}{key}"))
                    .bearer_auth(&custom.access_token)
                    .json(&json!({"row": {"label": "changed"}}))
                    .send()
                    .await
                    .map_err(|error| error.to_string())?;
                let status = update.status().as_u16();
                let body: Value = update.json().await.map_err(|error| error.to_string())?;
                if status != 403 || body["error"]["code"] != "rule_denied" {
                    return Err("custom token update was not denied".into());
                }
                self.db()
                    .sql()
                    .execute(
                        &format!(
                            "DELETE FROM {} WHERE rowid = 700002 OR label = 'custom-token'",
                            self.table
                        ),
                        &[],
                    )
                    .await
                    .map_err(|error| error.to_string())?;
                let mut input = RulesTestRequest {
                    operation: "read".into(),
                    table: self.table.clone(),
                    auth: RulesTestAuth {
                        uid: "owner".into(),
                        claims: Some([("plan".into(), json!("pro"))].into()),
                        ..Default::default()
                    },
                    row: Some([("id".into(), json!("owner"))].into()),
                    ..Default::default()
                };
                let allowed = self.db().test_rules(&input).await.map_err(e)?;
                if !allowed.allowed
                    || allowed.columns != ["id", "label"]
                    || !allowed.problems.is_empty()
                {
                    return Err("rules did not allow owner".into());
                }
                input.auth.uid = "other".into();
                let denied = self.db().test_rules(&input).await.map_err(e)?;
                if denied.allowed || denied.decided_by != "read" {
                    return Err("rules did not deny other user".into());
                }
                let invalid = RulesSet {
                    tables: [(
                        self.table.clone(),
                        RulesTable {
                            columns: Some(vec!["missing_column".into()]),
                            ..Default::default()
                        },
                    )]
                    .into(),
                };
                match self
                    .db()
                    .deploy_rules(&invalid, Some(&deployed.version))
                    .await
                {
                    Err(terrascale::v1::Error::Response { error, .. }) if matches!(*error, terrascale::v1::Error::InvalidRules { ref problems } if !problems.is_empty()) =>
                        {}
                    other => return Err(format!("invalid rules diagnostics missing: {other:?}")),
                }
                match self.db().deploy_rules(&rules, None).await {
                    Err(error) if error.code() == Some("version_mismatch") => {}
                    other => return Err(format!("stale rules accepted: {other:?}")),
                }
                let updated = self
                    .db()
                    .deploy_rules(&RulesSet::default(), Some(&deployed.version))
                    .await
                    .map_err(e)?;
                let page = self
                    .db()
                    .list_rules_versions(None, Some(1))
                    .await
                    .map_err(e)?;
                if page.items.len() != 1
                    || page.items[0].version != updated.version
                    || page.next.is_none()
                {
                    return Err("rules first page differs".into());
                }
                let next = self
                    .db()
                    .list_rules_versions(page.next.as_deref(), Some(1))
                    .await
                    .map_err(e)?;
                if next.items.len() != 1 || next.items[0].version != deployed.version {
                    return Err("rules second page differs".into());
                }
                let version = self
                    .db()
                    .get_rules_version(&deployed.version)
                    .await
                    .map_err(e)?;
                if version.version != deployed.version
                    || version.tables[&self.table].read.as_deref()
                        != Some("row.id = auth.uid AND auth.claims.plan = 'pro'")
                {
                    return Err("rules version differs".into());
                }
                let restored = self
                    .db()
                    .restore_rules(&deployed.version, Some(&updated.version))
                    .await
                    .map_err(e)?;
                if restored.version == deployed.version
                    || restored.tables[&self.table].read.as_deref()
                        != Some("row.id = auth.uid AND auth.claims.plan = 'pro'")
                {
                    return Err("rules restore differs".into());
                }
                if let Some(before) = before {
                    self.db()
                        .restore_rules(&before.version, Some(&restored.version))
                        .await
                        .map_err(e)?;
                } else {
                    self.db()
                        .deploy_rules(&RulesSet::default(), Some(&restored.version))
                        .await
                        .map_err(e)?;
                }
                Ok(())
            }
            "functions.stats" => {
                let name = format!("stats-{}", self.run);
                self.db()
                    .deploy_function(
                        &name,
                        &FunctionDeployment {
                            source: "async function handler() { return { ok: true }; }".into(),
                            cron: None,
                        },
                    )
                    .await
                    .map_err(e)?;
                let response = self
                    .db()
                    .invoke_function_response(&name, &json!({}))
                    .await
                    .map_err(e)?;
                same(json!(response.status), &json!(200), "invocation status")?;
                let stats = self
                    .db()
                    .get_function_stats(&name, Some("1h"))
                    .await
                    .map_err(e)?;
                same(
                    json!([stats.window, stats.invocations >= 1, stats.buckets.len()]),
                    &json!(["1h", true, 60]),
                    "stats",
                )?;
                let logs = self.db().list_function_logs(&name).await.map_err(e)?;
                let log = logs.first().ok_or("no invocation log")?;
                if logs.len() != 1 || log.http_status != Some(200) {
                    return Err(format!(
                        "log metrics differ: {} logs, status {:?}",
                        logs.len(),
                        log.http_status
                    ));
                }
                self.db().delete_function(&name).await.map_err(e)
            }
            "agent.create_ttl" => {
                let options = CreateDatabaseOptions {
                    ttl_seconds: op["ttlSeconds"].as_u64(),
                    ..Default::default()
                };
                let before = unix_now();
                let created = self
                    .client
                    .create_database(&fill("name"), &options)
                    .await
                    .map_err(e)?;
                let after = unix_now();
                same(json!(created.name), &json!(fill("name")), "name")?;
                let expiry = unix_time(
                    created
                        .expires_at
                        .as_deref()
                        .ok_or("the database has no expiry")?,
                )?;
                let ttl = op["ttlSeconds"].as_i64().unwrap_or_default();
                if expiry < before + ttl || expiry > after + ttl + 5 {
                    return Err(format!(
                        "expiry the server reports, {expiry}, is outside {before}+{ttl}..{after}+{ttl}"
                    ));
                }
                self.agent = Some(created);
                Ok(())
            }
            "agent.set_ttl" => {
                let agent = self.agent.as_ref().ok_or("no agent database")?;
                let updated = self
                    .client
                    .set_database_ttl(&agent.id, op["ttlSeconds"].as_u64().unwrap_or_default())
                    .await
                    .map_err(e)?;
                if updated.expires_at > agent.expires_at {
                    Ok(())
                } else {
                    Err("the expiry did not move later".into())
                }
            }
            "agent.clear_ttl" => {
                let agent = self.agent.as_ref().ok_or("no agent database")?;
                let cleared = self.client.clear_database_ttl(&agent.id).await.map_err(e)?;
                same(
                    json!(cleared.expires_at.is_none()),
                    &json!(true),
                    "no expiry",
                )
            }
            "agent.scoped_key" => {
                let agent = self.agent.as_ref().ok_or("no agent database")?.id.clone();
                let options = ScopedKeyOptions {
                    project: Some(self.project.clone()),
                    database: agent.clone(),
                    access: ScopedKeyAccess::ReadWrite,
                    ttl_seconds: op["ttlSeconds"].as_u64(),
                };
                let scoped = self.client.create_scoped_key(&options).await.map_err(e)?;
                register_secret("scoped-key-rust", &scoped.key)?;
                let narrow = Client::for_project_api_key(self.project.clone(), scoped.key.clone())
                    .add_root_ca_pem(self.ca.clone())
                    .build()
                    .map_err(e)?;
                narrow
                    .database(agent.clone())
                    .sql()
                    .execute(&text(op, "sql"), &[])
                    .await
                    .map_err(e)?;
                let again = ScopedKeyOptions {
                    project: Some(self.project.clone()),
                    database: agent,
                    access: ScopedKeyAccess::Read,
                    ttl_seconds: None,
                };
                same(
                    status(narrow.create_scoped_key(&again).await),
                    &op["mintStatus"],
                    "minting with a scoped key",
                )
            }
            "agent.fork" => {
                let sql = self.db().sql();
                sql.execute(&fill("setup"), &[]).await.map_err(e)?;
                sql.execute(&fill("sql"), &[]).await.map_err(e)?;
                let forked = self
                    .client
                    .fork_database(
                        &std::env::var("TERRASCALE_DATABASE_NAME")
                            .map_err(|error| error.to_string())?,
                        &fill("name"),
                        &ForkDatabaseOptions::default(),
                    )
                    .await
                    .map_err(e)?;
                same(
                    json!([forked.name, forked.branched_from]),
                    &json!([
                        fill("name"),
                        std::env::var("E2E_DATABASE_ID").map_err(|error| error.to_string())?
                    ]),
                    "fork",
                )?;
                let rows = self
                    .client
                    .database(forked.id.clone())
                    .sql()
                    .query(&fill("check"), &[])
                    .await
                    .map_err(e)?;
                same(records(&rows), &op["checkRows"], "rows of the fork")?;
                self.client.delete_database(&forked.id).await.map_err(e)
            }
            "agent.scoped_key_outside" => {
                let source = std::env::var("E2E_DATABASE_ID").map_err(|error| error.to_string())?;
                let options = ScopedKeyOptions {
                    project: Some(self.project.clone()),
                    database: source.clone(),
                    access: ScopedKeyAccess::Read,
                    ttl_seconds: op["ttlSeconds"].as_u64(),
                };
                let scoped = self.client.create_scoped_key(&options).await.map_err(e)?;
                register_secret("scoped-key-rust-outside", &scoped.key)?;
                let narrow = Client::for_project_api_key(self.project.clone(), scoped.key.clone())
                    .add_root_ca_pem(self.ca.clone())
                    .build()
                    .map_err(e)?;
                narrow
                    .database(source)
                    .sql()
                    .query(&text(op, "sql"), &[])
                    .await
                    .map_err(e)?;
                let other =
                    std::env::var("E2E_OTHER_DATABASE").map_err(|error| error.to_string())?;
                match narrow
                    .database(other)
                    .sql()
                    .query(&text(op, "sql"), &[])
                    .await
                {
                    Ok(_) => Err("the other database accepted the scoped key".into()),
                    Err(error) => same(
                        json!([error.status(), error.code()]),
                        &json!([op["status"], op["code"]]),
                        "the other database",
                    ),
                }
            }
            "placement.create" => self.placement_create().await,
            "placement.set" => self.placement_set().await,
            "agent.delete" => {
                let agent = self.agent.as_ref().ok_or("no agent database")?.id.clone();
                self.client.delete_database(&agent).await.map_err(e)?;
                same(
                    status(
                        self.client
                            .database(agent)
                            .sql()
                            .execute(&text(op, "sql"), &[])
                            .await,
                    ),
                    &op["deletedStatus"],
                    "the deleted database",
                )
            }
            other => Err(format!("no implementation for {other}")),
        }
    }
    async fn placement_create(&self) -> Outcome {
        let created = self
            .client
            .create_database(
                &format!("e2e-rust-{}-placed", self.run),
                &CreateDatabaseOptions {
                    regions: Some(vec!["gru".into(), "fra".into()]),
                    ..Default::default()
                },
            )
            .await
            .map_err(|error| error.to_string())?;
        let result = async {
            same(
                json!(created.regions),
                &json!(["fra", "gru"]),
                "created regions",
            )?;
            let repeated = self
                .client
                .set_database_placement(
                    &created.id,
                    &DatabasePlacement {
                        regions: Some(vec!["fra".into(), "gru".into()]),
                        ..Default::default()
                    },
                )
                .await
                .map_err(|error| error.to_string())?;
            same(
                json!(repeated.placement_version),
                &json!(created.placement_version),
                "unchanged placement version",
            )
        }
        .await;
        self.client
            .delete_database(&created.id)
            .await
            .map_err(|error| error.to_string())?;
        result
    }

    async fn placement_set(&self) -> Outcome {
        let created = self
            .client
            .create_database(
                &format!("e2e-rust-{}-move", self.run),
                &CreateDatabaseOptions {
                    regions: Some(vec!["gru".into()]),
                    ..Default::default()
                },
            )
            .await
            .map_err(|error| error.to_string())?;
        let regional = Client::for_project_api_key(
            self.project.clone(),
            std::env::var("TERRASCALE_API_KEY").map_err(|error| error.to_string())?,
        )
        .endpoint("https://fra.api.terrascale.tech")
        .auth_base_url("https://auth.terrascale.tech")
        .add_root_ca_pem(self.ca.clone())
        .build()
        .map_err(|error| error.to_string())?;
        let result = async {
            let home = self.client.database(&created.id);
            let peer = regional.database(&created.id);
            home.sql().execute("CREATE TABLE placement_rows (value INTEGER PRIMARY KEY)", &[]).await.map_err(|error| error.to_string())?;
            home.sql().execute("INSERT INTO placement_rows VALUES (1)", &[]).await.map_err(|error| error.to_string())?;
            let added = self.client.set_database_placement(&created.id,
                &DatabasePlacement { regions: Some(vec!["gru".into(), "fra".into()]), ..Default::default() }).await.map_err(|error| error.to_string())?;
            same(json!(added.regions), &json!(["fra", "gru"]), "added region")?;
            if added.placement_version <= created.placement_version { return Err("membership did not raise placement version".into()); }
            let progress = home.get_database_regions().await.map_err(|error| error.to_string())?;
            same(json!(progress.regions.iter().map(|row| (&row.region, row.healthy)).collect::<Vec<_>>()), &json!([["fra", true], ["gru", true]]), "every active-active region healthy")?;
            if progress.regions.iter().any(|row| row.applied_bookmark.as_deref().is_none_or(str::is_empty)) {
                return Err(format!("a region reports no applied bookmark: {:?}", progress.regions));
            }
            let mut fra_read = peer.sql();
            if let Some(bookmark) = home.bookmark() { fra_read = fra_read.with_bookmark(bookmark); }
            let seen = fra_read.query("SELECT value FROM placement_rows ORDER BY value", &[]).await.map_err(|error| error.to_string())?;
            same(records(&seen), &json!([[1]]), "fra reads the gru write with its bookmark")?;
            peer.sql().execute("INSERT INTO placement_rows VALUES (2)", &[]).await.map_err(|error| error.to_string())?;
            let mut gru_read = home.sql();
            if let Some(bookmark) = peer.bookmark() { gru_read = gru_read.with_bookmark(bookmark); }
            let both = gru_read.query("SELECT value FROM placement_rows ORDER BY value", &[]).await.map_err(|error| error.to_string())?;
            same(records(&both), &json!([[1], [2]]), "gru reads the fra write with its bookmark")?;
            let updated = regional.set_database_placement(&created.id,
                &DatabasePlacement { regions: Some(vec!["fra".into()]), ..Default::default() }).await.map_err(|error| error.to_string())?;
            same(json!(updated.regions), &json!(["fra"]), "removed region")?;
            if updated.placement_version <= added.placement_version { return Err("membership change did not raise placement version".into()); }
            let after = peer.get_database_regions().await.map_err(|error| error.to_string())?;
            same(json!(after.regions.iter().map(|row| (&row.region, row.healthy)).collect::<Vec<_>>()), &json!([["fra", true]]), "remaining region status")?;
            let retained = peer.sql().query("SELECT value FROM placement_rows ORDER BY value", &[]).await.map_err(|error| error.to_string())?;
            same(records(&retained), &json!([[1], [2]]), "data survived placement")?;
            println!("rust placement: fra and gru healthy active-active peers; bookmark reads crossed both ways; rows [[1],[2]]");
            Ok(())
        }.await;
        regional
            .delete_database(&created.id)
            .await
            .map_err(|error| error.to_string())?;
        result
    }
}

mod session_fault;

fn main() {
    let runtime = tokio::runtime::Builder::new_multi_thread()
        .enable_all()
        .build()
        .expect("runtime");
    std::process::exit(runtime.block_on(run()));
}

async fn run() -> i32 {
    if std::env::var_os("E2E_SESSION_FAULT").is_some() {
        return match session_fault::run().await {
            Ok(()) => 0,
            Err(error) => {
                eprintln!("{error}");
                1
            }
        };
    }
    let env = |name: &str| std::env::var(name).unwrap_or_default();
    let spec: Value = serde_json::from_str(
        &std::fs::read_to_string(env("E2E_OPERATIONS")).expect("operations.json"),
    )
    .expect("operations");
    let ca = std::fs::read(env("E2E_CA_FILE")).expect("the run CA");
    let project = env("TERRASCALE_PROJECT_ID");
    let client = Client::for_project_api_key(project.clone(), env("TERRASCALE_API_KEY"))
        .add_root_ca_pem(ca.clone())
        .build()
        .expect("client");
    let group = env("E2E_GROUP");
    let run = env("E2E_RUN_ID");
    let db =
        (group == "a" || group == "c").then(|| client.database(env("TERRASCALE_DATABASE_NAME")));
    let table = spec["table"]
        .as_str()
        .unwrap_or_default()
        .replace("{sdk}", "rust")
        .replace("{run}", &run);
    let mut runner = Runner {
        client,
        ca,
        project,
        table,
        run,
        db,
        session: None,
        agent: None,
    };
    let mut failed = false;
    let operations = spec["operations"].as_array().cloned().unwrap_or_default();
    for op in operations.iter().filter(|op| {
        op["group"] == group.as_str()
            && op.get("sdks").is_none_or(|sdks| {
                sdks.as_array()
                    .is_some_and(|sdks| sdks.iter().any(|sdk| sdk == "rust"))
            })
            && (op.get("needs").is_none()
                || env("E2E_REGIONS") == "2" && text(op, "id").starts_with("placement."))
    }) {
        let started = Instant::now();
        let mut line = BTreeMap::from([("id", op["id"].clone()), ("ok", json!(true))]);
        if let Err(error) = runner.do_op(op).await {
            failed = true;
            line.insert("ok", json!(false));
            line.insert("error", json!(error));
        }
        line.insert("ms", json!(started.elapsed().as_millis() as u64));
        println!("{}", json!(line));
    }
    i32::from(failed)
}
