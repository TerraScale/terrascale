// Hrana HTTP smoke using the upstream protocol types and a maintained TLS transport.
use std::{
    hash::{BuildHasher, RandomState},
    time::Duration,
};

use libsql_hrana::proto::{
    Batch, BatchStreamReq, CloseStreamReq, ExecuteStreamReq, PipelineReqBody, PipelineRespBody,
    Stmt, StmtResult, StreamRequest, StreamResponse, StreamResult, Value,
};

type Res<T> = Result<T, Box<dyn std::error::Error>>;

struct Connection {
    http: reqwest::Client,
    url: reqwest::Url,
    token: String,
    baton: Option<String>,
}

impl Connection {
    async fn request(&mut self, request: StreamRequest) -> Res<StreamResponse> {
        let response = self
            .http
            .post(self.url.clone())
            .bearer_auth(&self.token)
            .json(&PipelineReqBody {
                baton: self.baton.clone(),
                requests: vec![request],
            })
            .send()
            .await?
            .error_for_status()?;
        let mut body: PipelineRespBody = response.json().await?;
        self.baton = body.baton;
        check(body.results.len() == 1, "one pipeline response")?;
        match body.results.remove(0) {
            StreamResult::Ok { response } => Ok(response),
            StreamResult::Error { error } => Err(format!("Hrana error: {}", error.code).into()),
            StreamResult::None => Err("missing Hrana response".into()),
        }
    }

    async fn execute(&mut self, stmt: Stmt) -> Res<StmtResult> {
        match self
            .request(StreamRequest::Execute(ExecuteStreamReq { stmt }))
            .await?
        {
            StreamResponse::Execute(response) => Ok(response.result),
            _ => Err("expected execute response".into()),
        }
    }

    async fn sql(&mut self, sql: impl Into<String>) -> Res<StmtResult> {
        self.execute(Stmt::new(sql, true)).await
    }
}

#[tokio::main]
async fn main() -> Res<()> {
    let mut url = reqwest::Url::parse(&std::env::var("LIBSQL_URL")?)?;
    check(
        url.username().is_empty() && url.password().is_none() && url.query().is_none(),
        "URL has no credentials or query",
    )?;
    check(
        url.scheme() == "https"
            || (url.scheme() == "http"
                && matches!(url.host_str(), Some("127.0.0.1" | "[::1]" | "localhost"))),
        "HTTPS or loopback test URL",
    )?;
    url.set_path(&format!("{}/v3/pipeline", url.path().trim_end_matches('/')));
    let token = std::fs::read_to_string(std::env::var("LIBSQL_TOKEN_FILE")?)?
        .trim()
        .to_owned();
    let http = reqwest::Client::builder()
        .timeout(Duration::from_secs(30))
        .redirect(reqwest::redirect::Policy::none())
        .build()?;
    let mut conn = Connection {
        http,
        url,
        token,
        baton: None,
    };
    let t = format!("proof_rust_{:08x}", RandomState::new().hash_one(0u8) as u32);
    let (owners, items) = (format!("{t}_owners"), format!("{t}_items"));
    let result = proof(&mut conn, &owners, &items).await;
    // End a failed interactive transaction before cleanup; DROP runs after every proof outcome.
    let _ = conn.sql("ROLLBACK").await;
    let drop_items = conn.sql(format!("DROP TABLE IF EXISTS {items}")).await;
    let drop_owners = conn.sql(format!("DROP TABLE IF EXISTS {owners}")).await;
    let close = conn.request(StreamRequest::Close(CloseStreamReq {})).await;
    result?;
    drop_items?;
    drop_owners?;
    close?;
    println!("PASS (tables dropped, stream closed)");
    Ok(())
}

fn check(cond: bool, what: &str) -> Res<()> {
    if cond {
        Ok(())
    } else {
        Err(format!("check failed: {what}").into())
    }
}

fn integer(value: i64) -> Value {
    Value::Integer { value }
}
fn text(value: &str) -> Value {
    Value::Text {
        value: value.into(),
    }
}

fn cell(result: &StmtResult, row: usize, column: usize) -> Res<&Value> {
    result
        .rows
        .get(row)
        .and_then(|r| r.values.get(column))
        .ok_or_else(|| "missing result cell".into())
}

async fn proof(conn: &mut Connection, owners: &str, items: &str) -> Res<()> {
    conn.sql(format!(
        "CREATE TABLE {owners} (id INTEGER PRIMARY KEY, name TEXT NOT NULL)"
    ))
    .await?;
    conn.sql(format!(
        "CREATE TABLE {items} (sku TEXT PRIMARY KEY, owner_id INTEGER NOT NULL, qty INTEGER)"
    ))
    .await?;

    let mut insert = Stmt::new(
        format!("INSERT INTO {owners} (id, name) VALUES (?1, ?2)"),
        false,
    );
    insert.bind(integer(1));
    insert.bind(text("ann"));
    check(
        conn.execute(insert).await?.affected_row_count == 1,
        "insert affected 1 row",
    )?;
    let mut named = Stmt::new(
        format!("INSERT INTO {owners} (id, name) VALUES (:id, :name)"),
        false,
    );
    named.bind_named("id".into(), integer(2));
    named.bind_named("name".into(), text("bob"));
    conn.execute(named).await?;
    println!("positional and named parameters");

    let stmts = [("a-1", 1, 3), ("a-2", 1, 5), ("b-1", 2, 7)].map(|(sku, owner, qty)| {
        let mut stmt = Stmt::new(
            format!("INSERT INTO {items} (sku, owner_id, qty) VALUES (?1, ?2, ?3)"),
            false,
        );
        stmt.args = vec![text(sku), integer(owner), integer(qty)];
        stmt
    });
    let batch = Batch::transactional(stmts);
    match conn
        .request(StreamRequest::Batch(BatchStreamReq { batch }))
        .await?
    {
        StreamResponse::Batch(response) => {
            check(
                response.result.step_errors.iter().all(Option::is_none),
                "transactional batch has no errors",
            )?;
            check(
                response.result.step_results.len() == 6
                    && response
                        .result
                        .step_results
                        .iter()
                        .take(5)
                        .all(Option::is_some),
                "batch committed all inserts",
            )?;
        }
        _ => return Err("expected batch response".into()),
    }
    println!("batch (transactional): 3 statements");

    let got = conn.sql(format!("SELECT o.name, i.sku, i.qty FROM {items} i JOIN {owners} o ON o.id = i.owner_id ORDER BY i.sku")).await?;
    check(got.rows.len() == 3, "join returned 3 rows")?;
    check(
        matches!(cell(&got, 2, 0)?, Value::Text { value } if value.as_ref() == "bob"),
        "last row owned by bob",
    )?;
    println!("select with join");

    for (sku, commit) in [("tx-1", true), ("tx-2", false)] {
        conn.sql("BEGIN TRANSACTION").await?;
        let mut insert = Stmt::new(
            format!("INSERT INTO {items} (sku, owner_id, qty) VALUES (?1, 2, 1)"),
            false,
        );
        insert.bind(text(sku));
        conn.execute(insert).await?;
        let mut query = Stmt::new(format!("SELECT count(*) FROM {items} WHERE sku = ?1"), true);
        query.bind(text(sku));
        check(
            matches!(
                cell(&conn.execute(query).await?, 0, 0)?,
                Value::Integer { value: 1 }
            ),
            "transaction sees its insert",
        )?;
        conn.sql(if commit { "COMMIT" } else { "ROLLBACK" }).await?;
        check(
            matches!(
                cell(
                    &conn.sql(format!("SELECT count(*) FROM {items}")).await?,
                    0,
                    0
                )?,
                Value::Integer { value: 4 }
            ),
            "count after commit/rollback",
        )?;
        println!("transaction {}", if commit { "commit" } else { "rollback" });
    }
    Ok(())
}
