use std::collections::BTreeMap;
use std::time::{Instant, SystemTime, UNIX_EPOCH};

use base64::{engine::general_purpose::STANDARD, Engine};
use serde_json::{json, Value};
use terrascale::v1::{
    is_valid_id, new_id, Client, CreateDatabaseOptions, Database, Error, ManagedDatabase,
    ScopedKeyAccess, ScopedKeyOptions, SqlRecord, SqlSession, SqlStatement, SqlStatementResult,
    SqlValue,
};

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
        self.db.as_ref().expect("group a has a database")
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
                let results = sql
                    .batch(self.statements(op, "statements"))
                    .await
                    .map_err(e)?;
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
            "agent.create_ttl" => {
                let options = CreateDatabaseOptions {
                    ttl_seconds: op["ttlSeconds"].as_u64(),
                    ..Default::default()
                };
                let created = self
                    .client
                    .create_database(&fill("name"), &options)
                    .await
                    .map_err(e)?;
                same(json!(created.name), &json!(fill("name")), "name")?;
                created
                    .expires_at
                    .as_ref()
                    .ok_or("the database has no expiry")?;
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
                same(
                    status(
                        narrow
                            .database(fill("outside"))
                            .sql()
                            .execute(&text(op, "sql"), &[])
                            .await,
                    ),
                    &op["outsideStatus"],
                    "outside its database",
                )?;
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
}

fn main() {
    let runtime = tokio::runtime::Builder::new_multi_thread()
        .enable_all()
        .build()
        .expect("runtime");
    std::process::exit(runtime.block_on(run()));
}

async fn run() -> i32 {
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
    let db = (group == "a").then(|| client.database(env("TERRASCALE_DATABASE_NAME")));
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
    for op in operations
        .iter()
        .filter(|op| op["group"] == group.as_str() && op.get("needs").is_none())
    {
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
