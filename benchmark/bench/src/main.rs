//! `terrabase-bench-lane` — the operator-side reference driver of the
//! TerraBase benchmark lane.
//!
//! Drives the pinned PostgreSQL, PostgreSQL full-stack, and RavenDB
//! references through the four
//! CRUD-class workloads of the SE-15 lane and emits each run as JSON for
//! the engine-side adapter (`src/benchmarks/`) to classify under its own
//! gates. This binary produces facts only: every verdict, objective
//! status, and receipt lives in the engine-side adapter, so no comparison
//! can be minted outside the audited lane.
//!
//! Usage (repository root):
//!
//! ```text
//! cargo run --release --manifest-path bench/Cargo.toml -- \
//!   --target pg --workload read --out bench/results
//! ```
//!
//! A served TerraBase run requires `TERRABASE_BENCH_SERVED_RUNNER` to name a
//! prebuilt Rust runner executable and an explicit boundary contract:
//!
//! ```text
//! cargo run --release --manifest-path bench/Cargo.toml -- \
//!   --target terrabase --boundary-class diagnostic-qualification \
//!   --workload read --out bench/results
//! ```

mod config;
mod history;
mod lane;
mod metrics;
mod pg;
mod pg_fullstack;
mod raven;
mod served;

use std::path::PathBuf;

use config::{LaneConfig, Target, Workload};
use lane::Backend;

fn main() {
    let mut command_arguments = std::env::args().skip(1);
    if command_arguments.next().as_deref() == Some("--render-history") {
        if let Err(error) = history::run(command_arguments) {
            eprintln!("history rendering failed: {error}");
            std::process::exit(1);
        }
        return;
    }
    let mut target = None;
    let mut workload = None;
    let mut out = PathBuf::from("bench/results");
    let mut lane_config = LaneConfig::RECORD_MINIMUM;
    let mut boundary = served::DIAGNOSTIC_BOUNDARY.to_owned();

    let mut arguments = std::env::args().skip(1);
    while let Some(argument) = arguments.next() {
        match argument.as_str() {
            "--target" => {
                if let Some(value) = arguments.next() {
                    match Target::parse(&value) {
                        Ok(parsed) => target = Some(parsed),
                        Err(bad) => {
                            eprintln!("unknown --target: {bad}");
                            usage();
                            std::process::exit(2);
                        }
                    }
                }
            }
            "--workload" => {
                if let Some(value) = arguments.next() {
                    match Workload::parse(&value) {
                        Ok(parsed) => workload = Some(parsed),
                        Err(bad) => {
                            eprintln!("unknown --workload: {bad}");
                            usage();
                            std::process::exit(2);
                        }
                    }
                }
            }
            "--out" => {
                if let Some(value) = arguments.next() {
                    out = PathBuf::from(value);
                }
            }
            "--boundary-class" => {
                if let Some(value) = arguments.next() {
                    boundary = value;
                }
            }
            "--seed" => {
                if let Some(value) = arguments.next() {
                    lane_config.seed_documents = parse_count("--seed", &value);
                }
            }
            "--ops" => {
                if let Some(value) = arguments.next() {
                    lane_config.operations = parse_count("--ops", &value);
                }
            }
            "--samples" => {
                if let Some(value) = arguments.next() {
                    lane_config.samples = parse_count("--samples", &value);
                }
            }
            "--concurrency" => {
                if let Some(value) = arguments.next() {
                    lane_config.concurrency = parse_count("--concurrency", &value);
                }
            }
            "--warmup" => {
                if let Some(value) = arguments.next() {
                    lane_config.warmup_operations = parse_count("--warmup", &value);
                }
            }
            "--noise-percent" => {
                if let Some(value) = arguments.next() {
                    lane_config.noise_percent = match value.parse() {
                        Ok(value) => value,
                        Err(error) => {
                            eprintln!("invalid --noise-percent value {value:?}: {error}");
                            usage();
                            std::process::exit(2);
                        }
                    };
                }
            }
            other => {
                eprintln!("unknown argument: {other}");
                usage();
                std::process::exit(2);
            }
        }
    }

    let Some(target) = target else {
        usage();
        std::process::exit(2);
    };
    let Some(workload) = workload else {
        usage();
        std::process::exit(2);
    };
    if let Err(error) = lane_config.validate() {
        eprintln!("invalid lane configuration: {error}");
        std::process::exit(2);
    }
    std::fs::create_dir_all(&out).expect("results directory");
    if target == Target::TerraBase {
        match served::run(workload, lane_config, &out, &boundary) {
            Ok(()) => println!("served TerraBase runner completed"),
            Err(error) => {
                eprintln!("the served TerraBase run failed: {error}");
                // The Rust served runner already retained the canonical
                // failed attempt. Do not create a second, less-informative
                // wrapper document for the same invocation.
                if error.to_string().contains("retained canonical failure") {
                    std::process::exit(1);
                }
                let environment = metrics::host_environment(
                    "not captured: served run failed before identity observation".to_owned(),
                    endpoint_for(target),
                    transport_for(target),
                );
                let failure = metrics::FailureResults {
                    status: "failed",
                    target: target.as_str(),
                    workload: workload.as_str().to_owned(),
                    config: lane_config,
                    error: error.to_string(),
                    comparability: metrics::comparability(
                        &lane_config,
                        target.as_str(),
                        &environment,
                        Some(&boundary),
                    ),
                    environment,
                    samples: Vec::new(),
                    limits: vec![
                        "failed or invalid attempts are retained for audit and cannot be compared"
                            .to_owned(),
                    ],
                };
                let json =
                    serde_json::to_string_pretty(&failure).expect("serialize failure results");
                let path = write_retained(&out, target, workload, json.as_bytes())
                    .expect("write failure JSON");
                eprintln!("failed result retained: {}", path.display());
                std::process::exit(1);
            }
        }
        return;
    }
    let started = std::time::Instant::now();
    let result = match target {
        Target::TerraBase => unreachable!("served target returned before reference dispatch"),
        Target::PostgreSql => execute::<pg::PostgreSqlBackend>(workload, lane_config),
        Target::PgFullstack => execute::<pg_fullstack::PgFullstackBackend>(workload, lane_config),
        Target::RavenDb => execute::<raven::RavenDbBackend>(workload, lane_config),
    };
    match result {
        Ok((results, summary)) => {
            println!("{summary}");
            println!("elapsed: {:.1}s", started.elapsed().as_secs_f64());
            let path = write_retained(&out, target, workload, results.as_bytes())
                .expect("write results JSON");
            println!("written: {}", path.display());
        }
        Err(error) => {
            eprintln!("the reference run failed: {error}");
            let environment = metrics::host_environment(
                "not captured: run failed before identity observation".to_owned(),
                endpoint_for(target),
                transport_for(target),
            );
            let failure = metrics::FailureResults {
                status: "failed",
                target: target.as_str(),
                workload: workload.as_str().to_owned(),
                config: lane_config,
                error,
                comparability: metrics::comparability(
                    &lane_config,
                    target.as_str(),
                    &environment,
                    None,
                ),
                environment,
                samples: Vec::new(),
                limits: vec![
                    "failed or invalid attempts are retained for audit and cannot be compared"
                        .to_owned(),
                ],
            };
            let json = serde_json::to_string_pretty(&failure).expect("serialize failure results");
            let path = write_retained(&out, target, workload, json.as_bytes())
                .expect("write failure JSON");
            eprintln!("failed result retained: {}", path.display());
            std::process::exit(1);
        }
    }
}

fn parse_count(flag: &str, value: &str) -> usize {
    match value.parse() {
        Ok(value) => value,
        Err(error) => {
            eprintln!("invalid {flag} value {value:?}: {error}");
            usage();
            std::process::exit(2);
        }
    }
}

fn execute<B: Backend>(
    workload: Workload,
    lane_config: LaneConfig,
) -> Result<(String, String), String> {
    let mut backend =
        B::connect().map_err(|error| format!("cannot connect to the pinned reference: {error}"))?;
    let results =
        lane::run(&mut backend, workload, lane_config).map_err(|error| error.to_string())?;
    let summary = metrics::render_summary(target_of(results.target), workload, &results);
    let json = serde_json::to_string_pretty(&results).expect("serialize results");
    Ok((json, summary))
}

fn endpoint_for(target: Target) -> String {
    let variable = match target {
        Target::TerraBase => "TERRABASE_BENCH_ENDPOINT",
        Target::PostgreSql => "TERRABASE_PG_REFERENCE_ENDPOINT",
        Target::PgFullstack => "TERRABASE_PG_FULLSTACK_ENDPOINT",
        Target::RavenDb => "TERRABASE_RAVEN_REFERENCE_ENDPOINT",
    };
    std::env::var(variable).unwrap_or_else(|_| format!("{variable} is not set"))
}

fn transport_for(target: Target) -> &'static str {
    match target {
        Target::TerraBase => "Rust-owned served runner; network mTLS contract",
        Target::PostgreSql => "postgres reference client; identity unavailable",
        Target::PgFullstack => "pgrest HTTP/JSON reference client; identity unavailable",
        Target::RavenDb => "RavenDB HTTP reference client; identity unavailable",
    }
}

/// Writes each attempt under a unique name. Existing facts are immutable and
/// a later run, including a failed run, cannot overwrite them.
fn write_retained<T: AsRef<[u8]>>(
    directory: &std::path::Path,
    target: Target,
    workload: Workload,
    contents: T,
) -> std::io::Result<std::path::PathBuf> {
    use std::io::Write;
    let contents = contents.as_ref();
    let stamp = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap_or_default()
        .as_nanos();
    for attempt in 0..100_u32 {
        let path = directory.join(format!(
            "{}-{}-{stamp}-{}-{attempt}.json",
            target.as_str(),
            workload.as_str(),
            std::process::id()
        ));
        match std::fs::OpenOptions::new().write(true).create_new(true).open(&path) {
            Ok(mut file) => {
                file.write_all(contents)?;
                return Ok(path);
            }
            Err(error) if error.kind() == std::io::ErrorKind::AlreadyExists => continue,
            Err(error) => return Err(error),
        }
    }
    Err(std::io::Error::new(
        std::io::ErrorKind::AlreadyExists,
        "could not allocate a unique benchmark result name",
    ))
}

fn target_of(name: &str) -> Target {
    match name {
        "terrabase" => Target::TerraBase,
        "postgresql" => Target::PostgreSql,
        "pg-fullstack" => Target::PgFullstack,
        _ => Target::RavenDb,
    }
}

fn usage() {
    eprintln!(
        "usage: terrabase-bench-lane --target <terrabase|pg|pg-fullstack|raven> --workload <read|write|query|mixed> \
         [--boundary-class diagnostic-qualification|production-candidate] [--out DIR] [--seed N] \
         [--ops N] [--samples N] [--concurrency N] [--warmup N] [--noise-percent N]"
    );
}
