//! TerraBase-only served index diagnostics. No comparator or shadow verdict is claimed.
use std::path::PathBuf;
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use serde_json::{Value, json};

pub fn run(arguments: Vec<String>) -> Result<PathBuf, String> {
    let mut documents = 1_000_000_usize;
    let mut samples = 100_usize;
    let mut writes = 1_000_usize;
    let mut output = PathBuf::from("bench/results");
    let mut args = arguments.into_iter();
    while let Some(flag) = args.next() {
        let value = args.next().ok_or_else(|| format!("missing value for {flag}"))?;
        match flag.as_str() {
            "--target" if value == "terrabase" => {}
            "--documents" => documents = value.parse().map_err(|_| "invalid documents")?,
            "--samples" => samples = value.parse().map_err(|_| "invalid samples")?,
            "--writes" => writes = value.parse().map_err(|_| "invalid writes")?,
            "--out" => output = value.into(),
            _ => return Err(format!("unknown auto-index option {flag} {value}")),
        }
    }
    if documents < 2 || samples == 0 || writes == 0 {
        return Err("documents must be at least 2; samples and writes must be positive".into());
    }
    let stamp = SystemTime::now().duration_since(UNIX_EPOCH).map_err(|e| e.to_string())?.as_nanos();
    let collection = format!("auto_{stamp}");
    let api = std::env::var("TERRABASE_BENCH_TERRABASE_API").map_err(|e| e.to_string())?;
    let database =
        std::env::var("TERRABASE_BENCH_TERRABASE_DATABASE").map_err(|e| e.to_string())?;
    let auth = std::env::var("TERRABASE_BENCH_TERRABASE_AUTH").map_err(|e| e.to_string())?;
    let agent = ureq::AgentBuilder::new().timeout(Duration::from_secs(300)).build();
    let base = format!("{}/api/v1/databases/{database}", api.trim_end_matches('/'));
    let request = |method: &str, path: &str, body: Option<Value>| -> Result<Value, String> {
        let req = agent.request(method, &format!("{base}{path}")).set("Authorization", &auth);
        let response = match body {
            Some(body) => {
                req.set("Content-Type", "application/json").send_string(&body.to_string())
            }
            None => req.call(),
        }
        .map_err(|e| format!("{method} {path}: {e}"))?;
        let text = response.into_string().map_err(|e| e.to_string())?;
        serde_json::from_str(&text).map_err(|e| format!("{path}: invalid JSON: {e}"))
    };
    let prefix = format!("/collections/{collection}");
    let query = json!({"sql":format!("SELECT _id, f0 FROM {collection} WHERE f0 = :value LIMIT 1"),"parameters":{"value":"value-1"}});
    let mut facts = Vec::<Value>::new();
    let mut report = json!({
        "family":"auto-index-local", "target":"terrabase", "status":"failed",
        "boundary":"diagnostic-qualification", "documents":documents,"samples":samples,"writes":writes,
        "collection":collection,"runtime_digest":std::env::var("TERRABASE_BENCH_CANDIDATE_DIGEST").ok(),
        "limits":["Local served index diagnostics; no comparator, shadow significance, logical-work, or 2x write-budget qualification", "Separate unique collection; seed and index registration are excluded from steady-state write timings"]
    });
    let result = (|| -> Result<(), String> {
        let put = |first: usize, count: usize| -> Result<(), String> {
            for start in (first..first + count).step_by(500) {
                let end = (start + 500).min(first + count);
                let operations = (start..end)
                    .map(|i| {
                        let mut value = serde_json::Map::new();
                        for field in 0..12 {
                            value.insert(format!("f{field}"), json!(format!("value-{i}")));
                        }
                        json!({"op":"put","id":format!("auto-{i}"),"value":value})
                    })
                    .collect::<Vec<_>>();
                let response = request(
                    "POST",
                    &format!("{prefix}/documents/batch"),
                    Some(json!({"operations":operations})),
                )?;
                if response["count"].as_u64() != Some((end - start) as u64)
                    || response["committed"].as_u64().is_none()
                {
                    return Err(format!("batch failed: {response}"));
                }
            }
            Ok(())
        };
        let started = Instant::now();
        put(0, documents)?;
        facts.push(
            json!({"phase":"seed","documents":documents,"seconds":started.elapsed().as_secs_f64()}),
        );
        for indexes in [0, 5, 12] {
            if indexes > 0 {
                let started = Instant::now();
                for field in (if indexes == 5 { 0 } else { 5 })..indexes {
                    request(
                        "POST",
                        &format!("{prefix}/indexes"),
                        Some(json!({"field":format!("f{field}")})),
                    )?;
                }
                loop {
                    let state = request("GET", &format!("{prefix}/indexes"), None)?;
                    let active = state["indexes"]
                        .as_array()
                        .ok_or("missing index list")?
                        .iter()
                        .filter(|index| index["state"] == "active")
                        .count();
                    if active == indexes {
                        break;
                    }
                    if started.elapsed() > Duration::from_secs(300) {
                        return Err("index activation timed out".into());
                    }
                    std::thread::sleep(Duration::from_millis(100));
                }
                facts.push(json!({"phase":"index_activation","indexes":indexes,"seconds":started.elapsed().as_secs_f64()}));
            }
            let started = Instant::now();
            put(documents + indexes * writes, writes)?;
            let seconds = started.elapsed().as_secs_f64();
            facts.push(json!({"phase":"writes","indexes":indexes,"operations":writes,"seconds":seconds,"ops_per_second":writes as f64/seconds}));
            if indexes == 0 {
                let started = Instant::now();
                let response = request("POST", "/sql", Some(query.clone()))?;
                if response["rows"]
                    .as_array()
                    .is_none_or(|rows| rows.len() != 1 || rows[0]["id"] != "auto-1")
                {
                    return Err("first query did not return seeded row".into());
                }
                facts.push(json!({"phase":"first_query","milliseconds":started.elapsed().as_secs_f64()*1000.0}));
            }
        }
        let started = Instant::now();
        let mut latencies = Vec::with_capacity(samples);
        for _ in 0..samples {
            let operation = Instant::now();
            let response = request("POST", "/sql", Some(query.clone()))?;
            if response["rows"]
                .as_array()
                .is_none_or(|rows| rows.len() != 1 || rows[0]["id"] != "auto-1")
            {
                return Err("indexed query did not return seeded row".into());
            }
            latencies.push(operation.elapsed().as_secs_f64() * 1_000_000.0);
        }
        facts.push(json!({"phase":"indexed_query","measurement":crate::metrics::sample_of(samples as u64, started.elapsed(), latencies)}));
        for i in 0..10 {
            let marker = format!("stale-{i}");
            request(
                "PUT",
                &format!("{prefix}/documents/{marker}"),
                Some(json!({"marker":marker})),
            )?;
            let response = request("GET", &format!("{prefix}/documents/{marker}"), None)?;
            if !response.to_string().contains(&marker) {
                return Err("stale point read".into());
            }
        }
        facts.push(json!({"phase":"zero_stale_reads","samples":10}));
        let state = request("GET", &format!("{prefix}/indexes"), None)?;
        for _ in 0..3 {
            request("POST", "/sql", Some(query.clone()))?;
            if request("GET", &format!("{prefix}/indexes"), None)? != state {
                return Err("index registry did not reach fixed point".into());
            }
        }
        facts.push(json!({"phase":"fixed_point","rounds":3}));
        Ok(())
    })();
    report["facts"] = json!(facts);
    match &result {
        Ok(()) => report["status"] = json!("complete"),
        Err(error) => report["error"] = json!(error),
    }
    std::fs::create_dir_all(&output).map_err(|e| e.to_string())?;
    let path = output.join(format!("auto-index-local-{stamp}.json"));
    std::fs::write(&path, serde_json::to_vec_pretty(&report).map_err(|e| e.to_string())?)
        .map_err(|e| e.to_string())?;
    result.map_err(|e| format!("{e}; retained {}", path.display()))?;
    Ok(path)
}
