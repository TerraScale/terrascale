use std::collections::HashMap;
use std::future::Future;
use std::time::{Duration, Instant};

use base64::engine::general_purpose::STANDARD;
use base64::Engine;
use reqwest::header::AUTHORIZATION;
use serde::{Deserialize, Serialize};
use terrascale::v1::{new_id, Client, CreateDatabaseOptions, SqlStatement, SqlValue};

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct ScenarioResult {
    name: String,
    iterations: usize,
    ops_per_sec: f64,
    p50_ms: f64,
    p95_ms: f64,
    p99_ms: f64,
    mean_ms: f64,
    min_ms: f64,
    max_ms: f64,
}

#[derive(Serialize)]
struct SdkReport {
    sdk: &'static str,
    scenarios: Vec<ScenarioResult>,
}

#[derive(Deserialize)]
struct TokenResponse {
    access_token: String,
}

struct Options {
    terrabase_url: String,
    auth_url: String,
    dashboard_url: String,
    api_key: String,
    project: String,
    iterations: usize,
    warmup: usize,
    batch_size: usize,
}

impl Options {
    fn parse() -> Result<Self, String> {
        let mut args = std::env::args().skip(1);
        let mut map = HashMap::new();
        while let Some(arg) = args.next() {
            let Some(rest) = arg.strip_prefix("--") else {
                return Err(format!("unexpected argument {arg}"));
            };
            if let Some((k, v)) = rest.split_once('=') {
                map.insert(k.to_owned(), v.to_owned());
            } else if let Some(v) = args.next() {
                map.insert(rest.to_owned(), v);
            } else {
                return Err(format!("missing value for --{rest}"));
            }
        }
        let req = |k: &str| -> Result<String, String> {
            map.get(k)
                .filter(|v| !v.is_empty())
                .cloned()
                .ok_or_else(|| format!("--{k} is required"))
        };
        let parse_usize = |k: &str, default: usize| -> Result<usize, String> {
            match map.get(k) {
                Some(raw) => raw.parse::<usize>().map_err(|e| format!("--{k}: {e}")),
                None => Ok(default),
            }
        };
        Ok(Self {
            terrabase_url: req("terrabase-url")?.trim_end_matches('/').to_owned(),
            auth_url: req("auth-url")?.trim_end_matches('/').to_owned(),
            dashboard_url: req("dashboard-url")?.trim_end_matches('/').to_owned(),
            api_key: req("api-key")?,
            project: req("project")?,
            iterations: parse_usize("iterations", 50)?,
            warmup: parse_usize("warmup", 5)?,
            batch_size: parse_usize("batch-size", 25)?,
        })
    }
}

async fn measure<F, Fut>(
    name: &str,
    warmup: usize,
    iterations: usize,
    mut f: F,
) -> Result<ScenarioResult, String>
where
    F: FnMut() -> Fut,
    Fut: Future<Output = Result<(), String>>,
{
    for i in 0..warmup {
        f().await.map_err(|e| format!("{name} warmup {i}: {e}"))?;
    }
    let mut samples = Vec::with_capacity(iterations);
    let total_start = Instant::now();
    for i in 0..iterations {
        let start = Instant::now();
        f().await.map_err(|e| format!("{name} iter {i}: {e}"))?;
        samples.push(start.elapsed().as_secs_f64() * 1000.0);
    }
    let total_secs = total_start.elapsed().as_secs_f64();
    samples.sort_by(f64::total_cmp);
    let sum: f64 = samples.iter().sum();
    let ops_per_sec = if total_secs > 0.0 {
        iterations as f64 / total_secs
    } else {
        0.0
    };
    Ok(ScenarioResult {
        name: name.to_owned(),
        iterations,
        ops_per_sec,
        p50_ms: percentile(&samples, 0.50),
        p95_ms: percentile(&samples, 0.95),
        p99_ms: percentile(&samples, 0.99),
        mean_ms: sum / samples.len() as f64,
        min_ms: samples[0],
        max_ms: samples[samples.len() - 1],
    })
}

fn percentile(sorted: &[f64], p: f64) -> f64 {
    if sorted.is_empty() {
        return 0.0;
    }
    let idx = (((sorted.len() - 1) as f64 * p).round() as usize).min(sorted.len() - 1);
    sorted[idx]
}

#[tokio::main]
async fn main() {
    if let Err(err) = run().await {
        eprintln!("ts-bench-rust: {err}");
        std::process::exit(1);
    }
}

async fn run() -> Result<(), String> {
    let opts = Options::parse()?;
    let http = reqwest::Client::builder()
        .redirect(reqwest::redirect::Policy::none())
        .timeout(Duration::from_secs(30))
        .no_proxy()
        .build()
        .map_err(|e| format!("build http client: {e}"))?;

    let client = Client::for_project_api_key(&opts.project, &opts.api_key)
        .endpoint(&opts.terrabase_url)
        .auth_base_url(&opts.auth_url)
        .build()
        .map_err(|e| format!("build TerraScale client: {e}"))?;

    let key_body = opts.api_key.strip_prefix("sk_").unwrap_or(&opts.api_key);
    if key_body.len() < 26 {
        return Err("invalid API key format".to_owned());
    }
    let key_id = &key_body[..26];
    let basic_header = format!(
        "Basic {}",
        STANDARD.encode(format!("{key_id}:{}", opts.api_key))
    );
    let token_url = format!("{}/terrascale/oauth/token", opts.auth_url);
    let run_tag = new_id()[18..].to_ascii_lowercase();
    let mut results = Vec::with_capacity(7);

    results.push(
        measure("auth_token_exchange", opts.warmup, opts.iterations, || {
            let http = http.clone();
            let token_url = token_url.clone();
            let basic_header = basic_header.clone();
            let project = opts.project.clone();
            async move {
                let resp = http
                    .post(&token_url)
                    .header(AUTHORIZATION, &basic_header)
                    .form(&[("grant_type", "client_credentials"), ("project", &project)])
                    .send()
                    .await
                    .map_err(|e| format!("token request: {e}"))?;
                let status = resp.status();
                if !status.is_success() {
                    let body = resp.text().await.unwrap_or_default();
                    return Err(format!("token status {status}: {body}"));
                }
                let parsed: TokenResponse = resp
                    .json()
                    .await
                    .map_err(|e| format!("decode token response: {e}"))?;
                if parsed.access_token.is_empty() {
                    return Err("empty access_token".to_owned());
                }
                Ok(())
            }
        })
        .await?,
    );

    let mut lc_counter = 0u64;
    results.push(
        measure("db_lifecycle", opts.warmup, opts.iterations, || {
            lc_counter += 1;
            let idx = lc_counter;
            let client = client.clone();
            let run_tag = run_tag.clone();
            async move {
                let db_name = format!("b-rs-lc-{run_tag}-{idx}");
                client
                    .create_database(&db_name, &CreateDatabaseOptions::default())
                    .await
                    .map_err(|e| format!("create database: {e}"))?;
                let exec_res = client
                    .database(&db_name)
                    .sql()
                    .execute(
                        "CREATE TABLE items (id INTEGER PRIMARY KEY, label TEXT NOT NULL, val INTEGER NOT NULL)",
                        &[],
                    )
                    .await;
                let del_res = client.delete_database(&db_name).await;
                exec_res.map_err(|e| format!("create table: {e}"))?;
                del_res.map_err(|e| format!("delete database: {e}"))?;
                Ok(())
            }
        })
        .await?,
    );

    let work_db_name = format!("b-rs-work-{run_tag}");
    client
        .create_database(&work_db_name, &CreateDatabaseOptions::default())
        .await
        .map_err(|e| format!("create working database: {e}"))?;

    let work_result =
        run_database_scenarios(&opts, &client, &http, &work_db_name, &mut results).await;
    let delete_result = client.delete_database(&work_db_name).await;
    work_result?;
    delete_result.map_err(|e| format!("delete working database: {e}"))?;

    let report = SdkReport {
        sdk: "rust",
        scenarios: results,
    };
    let json = serde_json::to_string(&report).map_err(|e| format!("serialize report: {e}"))?;
    println!("{json}");
    Ok(())
}

async fn run_database_scenarios(
    opts: &Options,
    client: &Client,
    http: &reqwest::Client,
    work_db_name: &str,
    results: &mut Vec<ScenarioResult>,
) -> Result<(), String> {
    let db = client.database(work_db_name);
    let sql = db.sql();
    sql.execute(
        "CREATE TABLE bench_items (id INTEGER PRIMARY KEY, tag TEXT NOT NULL, amount INTEGER NOT NULL)",
        &[],
    )
    .await
    .map_err(|e| format!("create bench_items table: {e}"))?;

    let mut next_id = 1i64;
    results.push(
        measure("sql_rest_write", opts.warmup, opts.iterations, || {
            let id = next_id;
            next_id += 1;
            let sql = sql.clone();
            async move {
                sql.execute(
                    "INSERT INTO bench_items (id, tag, amount) VALUES (?, ?, ?)",
                    &[id.into(), "rest".into(), (id * 10).into()],
                )
                .await
                .map_err(|e| format!("insert: {e}"))?;
                Ok(())
            }
        })
        .await?,
    );

    let read_max_id = (next_id - 1).max(1);
    let mut read_cursor = 0i64;
    results.push(
        measure("sql_rest_read", opts.warmup, opts.iterations, || {
            let target_id = (read_cursor % read_max_id) + 1;
            read_cursor += 1;
            let sql = sql.clone();
            async move {
                let rows = sql
                    .query(
                        "SELECT id, tag, amount FROM bench_items WHERE id = ?",
                        &[SqlValue::Integer(target_id)],
                    )
                    .await
                    .map_err(|e| format!("query: {e}"))?;
                if rows.len() != 1 {
                    return Err(format!(
                        "expected 1 row for id {target_id}, got {}",
                        rows.len()
                    ));
                }
                Ok(())
            }
        })
        .await?,
    );

    let batch_size = opts.batch_size;
    results.push(
        measure("sql_rest_batch", opts.warmup, opts.iterations, || {
            let mut stmts = Vec::with_capacity(batch_size);
            for _ in 0..batch_size {
                let id = next_id;
                next_id += 1;
                stmts.push(
                    SqlStatement::new("INSERT INTO bench_items (id, tag, amount) VALUES (?, ?, ?)")
                        .args([
                            SqlValue::Integer(id),
                            SqlValue::Text("batch".to_owned()),
                            SqlValue::Integer(id * 10),
                        ]),
                );
            }
            let sql = sql.clone();
            async move {
                let out = sql.batch(stmts).await.map_err(|e| format!("batch: {e}"))?;
                if out.len() != batch_size {
                    return Err(format!(
                        "expected {batch_size} batch results, got {}",
                        out.len()
                    ));
                }
                Ok(())
            }
        })
        .await?,
    );

    let session = db
        .connect()
        .await
        .map_err(|e| format!("connect websocket: {e}"))?;
    let ws_res = measure("sql_ws_transaction", opts.warmup, opts.iterations, || {
        let id = next_id;
        next_id += 1;
        let session = session.clone();
        async move {
            session
                .transaction(|tx| async move {
                    tx.execute(
                        "INSERT INTO bench_items (id, tag, amount) VALUES (?, ?, ?)",
                        &[id.into(), "ws".into(), (id * 10).into()],
                    )
                    .await?;
                    let rows = tx
                        .query(
                            "SELECT id, tag, amount FROM bench_items WHERE id = ?",
                            &[SqlValue::Integer(id)],
                        )
                        .await?;
                    if rows.len() != 1 {
                        return Err(terrascale::v1::Error::InvalidInput(format!(
                            "expected 1 row in ws tx, got {}",
                            rows.len()
                        )));
                    }
                    Ok(())
                })
                .await
                .map_err(|e| format!("ws transaction: {e}"))?;
            Ok(())
        }
    })
    .await;
    session.close().await;
    results.push(ws_res?);

    let health_live_url = format!("{}/health/live", opts.dashboard_url);
    let oauth_meta_url = format!(
        "{}/.well-known/oauth-authorization-server",
        opts.dashboard_url
    );
    results.push(
        measure("dashboard_roundtrip", opts.warmup, opts.iterations, || {
            let http = http.clone();
            let health_live_url = health_live_url.clone();
            let oauth_meta_url = oauth_meta_url.clone();
            async move {
                let health = http
                    .get(&health_live_url)
                    .send()
                    .await
                    .map_err(|e| format!("GET /health/live: {e}"))?;
                if !health.status().is_success() {
                    return Err(format!("GET /health/live status {}", health.status()));
                }
                let meta = http
                    .get(&oauth_meta_url)
                    .send()
                    .await
                    .map_err(|e| format!("GET /.well-known/oauth-authorization-server: {e}"))?;
                if !meta.status().is_success() {
                    return Err(format!(
                        "GET /.well-known/oauth-authorization-server status {}",
                        meta.status()
                    ));
                }
                Ok(())
            }
        })
        .await?,
    );

    Ok(())
}
