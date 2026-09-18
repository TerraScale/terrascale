//! Turso/libSQL primary->replica replication lag benchmark.
//!
//! Emits one JSON line per timed sample plus a summary line per batch size.
//! SMOKE=1 env var shrinks the run to 1 warmup + 3 samples per batch size.

use std::time::{Duration, Instant};

use libsql::Database;
use serde_json::{json, Value};

const PRIMARY_URL: &str = "http://127.0.0.1:8080";
const REPLICA_URL: &str = "http://127.0.0.1:8081";
const BATCH_SIZES: [usize; 3] = [1, 16, 128];
const WARMUP: usize = 5;
const SAMPLES: usize = 60;
const BODY_TARGET_BYTES: usize = 250;
const SCHEMA_TIMEOUT: Duration = Duration::from_secs(60);
const VISIBILITY_TIMEOUT: Duration = Duration::from_secs(120);

fn sql_quote(s: &str) -> String {
    s.replace('\'', "''")
}

fn body_for(id: i64) -> String {
    let mut v = json!({"id": id, "origin": "turso-bench", "pad": ""});
    let base = v.to_string().len();
    let pad = "p".repeat(BODY_TARGET_BYTES.saturating_sub(base));
    v["pad"] = Value::String(pad);
    v.to_string()
}

/// Nearest-rank percentile on ascending-sorted data: index ceil(p/100*N)-1.
fn percentile(sorted: &[f64], p: f64) -> f64 {
    let n = sorted.len();
    let idx = ((p / 100.0 * n as f64).ceil() as usize).clamp(1, n) - 1;
    sorted[idx]
}

fn metric_json(mut ms: Vec<f64>) -> Value {
    ms.sort_by(|a, b| a.partial_cmp(b).unwrap());
    json!({
        "p50": percentile(&ms, 50.0),
        "p95": percentile(&ms, 95.0),
        "p99": percentile(&ms, 99.0),
    })
}

fn us(a: Instant, b: Instant) -> u64 {
    (b - a).as_micros() as u64
}

#[tokio::main]
async fn main() {
    let smoke = std::env::var("SMOKE").map(|s| !s.is_empty()).unwrap_or(false);
    let (warmup, samples) = if smoke { (1, 3) } else { (WARMUP, SAMPLES) };
    eprintln!("running smoke={smoke} warmup={warmup} samples={samples} per batch size");

    let primary = Database::open_remote(PRIMARY_URL, "")
        .expect("open primary")
        .connect()
        .expect("connect primary");
    let replica = Database::open_remote(REPLICA_URL, "")
        .expect("open replica")
        .connect()
        .expect("connect replica");

    primary
        .execute(
            "CREATE TABLE IF NOT EXISTS docs (id INTEGER PRIMARY KEY, body TEXT NOT NULL)",
            (),
        )
        .await
        .expect("create table on primary");

    // Wait until the schema has replicated and the table is queryable on the replica.
    let deadline = Instant::now() + SCHEMA_TIMEOUT;
    loop {
        let queryable = match replica.query("SELECT count(*) FROM docs", ()).await {
            Ok(mut rows) => rows.next().await.unwrap_or(None).is_some(),
            Err(_) => false,
        };
        if queryable {
            break;
        }
        if Instant::now() > deadline {
            panic!("docs table never became queryable on replica");
        }
        tokio::time::sleep(Duration::from_millis(20)).await;
    }
    eprintln!("schema replicated to replica");

    // Continue after any pre-existing rows so ids stay globally distinct.
    let mut rows = primary
        .query("SELECT COALESCE(MAX(id), -1) FROM docs", ())
        .await
        .expect("read max id");
    let row = rows.next().await.unwrap_or(None).expect("max id row");
    let mut next_id: i64 = row.get::<i64>(0).expect("max id value") + 1;
    for &m in &BATCH_SIZES {
        let mut send_ack: Vec<u64> = Vec::with_capacity(samples);
        let mut ack_vis: Vec<u64> = Vec::with_capacity(samples);
        let mut send_vis: Vec<u64> = Vec::with_capacity(samples);
        let mut polls_total: u64 = 0;

        for i in 0..(warmup + samples) {
            let timed = i >= warmup;

            // Build ONE multi-row INSERT, single execute call = single round trip.
            let mut sql = String::with_capacity(96 * m + 64);
            sql.push_str("INSERT INTO docs (id, body) VALUES ");
            for j in 0..m {
                if j > 0 {
                    sql.push(',');
                }
                sql.push_str(&format!("({},'{}')", next_id, sql_quote(&body_for(next_id))));
                next_id += 1;
            }
            let last_id = next_id - 1;

            let t0 = Instant::now();
            primary
                .execute(sql.as_str(), ())
                .await
                .expect("insert batch on primary");
            let ack = Instant::now();

            // Tight-loop poll the replica until the last inserted row is visible.
            let lookup = format!("SELECT 1 FROM docs WHERE id = {last_id}");
            let mut polls: u64 = 0;
            let vis = loop {
                polls += 1;
                let mut found = false;
                if let Ok(mut rows) = replica.query(lookup.as_str(), ()).await
                {
                    if rows.next().await.unwrap_or(None).is_some() {
                        found = true;
                    }
                }
                if found {
                    break Instant::now();
                }
                if ack - t0 > VISIBILITY_TIMEOUT {
                    panic!("replica never observed id {last_id} (batch size {m})");
                }
            };

            if timed {
                polls_total += polls;
                println!(
                    "{}",
                    json!({
                        "batchSize": m,
                        "sample": i - warmup,
                        "sendAckUs": us(t0, ack),
                        "ackToVisibleUs": us(ack, vis),
                        "sendToVisibleUs": us(t0, vis),
                        "polls": polls,
                    })
                );
                send_ack.push(us(t0, ack));
                ack_vis.push(us(ack, vis));
                send_vis.push(us(t0, vis));
            }
        }

        let to_ms = |v: Vec<u64>| v.into_iter().map(|x| x as f64 / 1000.0).collect::<Vec<_>>();
        println!(
            "{}",
            json!({
                "batchSize": m,
                "summary": true,
                "n": samples,
                "sendAckMs": metric_json(to_ms(send_ack)),
                "sendToVisibleMs": metric_json(to_ms(send_vis)),
                "ackToVisibleMs": metric_json(to_ms(ack_vis)),
            })
        );
        eprintln!(
            "batchSize {m} done, mean polls per sample: {:.1}",
            polls_total as f64 / samples as f64
        );
    }
}
