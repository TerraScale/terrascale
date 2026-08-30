//! `terrabase-bench-lane` — the operator-side reference driver of the
//! TerraBase benchmark lane.
//!
//! Drives the pinned PostgreSQL and RavenDB references through the four
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

mod config;
mod lane;
mod metrics;
mod pg;
mod raven;

use std::path::PathBuf;

use config::{LaneConfig, Target, Workload};
use lane::Backend;

fn main() {
    let mut target = None;
    let mut workload = None;
    let mut out = PathBuf::from("bench/results");
    let mut lane_config = LaneConfig::RECORD_MINIMUM;

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
            "--seed" => {
                if let Some(value) = arguments.next() {
                    lane_config.seed_documents = value.parse().unwrap_or(lane_config.seed_documents);
                }
            }
            "--ops" => {
                if let Some(value) = arguments.next() {
                    lane_config.operations = value.parse().unwrap_or(lane_config.operations);
                }
            }
            "--samples" => {
                if let Some(value) = arguments.next() {
                    lane_config.samples = value.parse().unwrap_or(lane_config.samples);
                }
            }
            "--concurrency" => {
                if let Some(value) = arguments.next() {
                    lane_config.concurrency = value.parse().unwrap_or(lane_config.concurrency);
                }
            }
            "--warmup" => {
                if let Some(value) = arguments.next() {
                    lane_config.warmup_operations =
                        value.parse().unwrap_or(lane_config.warmup_operations);
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
    if target == Target::PostgreSql && std::env::var("TERRABASE_PG_REFERENCE_ENDPOINT").is_err() {
        eprintln!(
            "TERRABASE_PG_REFERENCE_ENDPOINT is not set; the pinned server is started by \
             bench/scripts/pg-start.sh and the variable names the endpoint actually used"
        );
        std::process::exit(2);
    }
    if target == Target::RavenDb && std::env::var("TERRABASE_RAVEN_REFERENCE_ENDPOINT").is_err() {
        eprintln!(
            "TERRABASE_RAVEN_REFERENCE_ENDPOINT is not set; the pinned server is started by \
             bench/scripts/raven-start.sh and the variable names the endpoint actually used"
        );
        std::process::exit(2);
    }

    let started = std::time::Instant::now();
    let (results, summary) = match target {
        Target::PostgreSql => execute::<pg::PostgreSqlBackend>(workload, lane_config),
        Target::RavenDb => execute::<raven::RavenDbBackend>(workload, lane_config),
    };
    println!("{summary}");
    println!("elapsed: {:.1}s", started.elapsed().as_secs_f64());

    std::fs::create_dir_all(&out).expect("results directory");
    let path = out.join(format!("{}-{}.json", target.as_str(), workload.as_str()));
    std::fs::write(&path, results).expect("write results JSON");
    println!("written: {}", path.display());
}

fn execute<B: Backend>(workload: Workload, lane_config: LaneConfig) -> (String, String) {
    let mut backend = B::connect().unwrap_or_else(|error| {
        eprintln!("cannot connect to the pinned reference: {error}");
        std::process::exit(1);
    });
    let results = lane::run(&mut backend, workload, lane_config)
        .unwrap_or_else(|error| {
            eprintln!("the reference run failed: {error}");
            std::process::exit(1);
        });
    let summary = metrics::render_summary(target_of(&results.target), workload, &results);
    let json = serde_json::to_string_pretty(&results).expect("serialize results");
    (json, summary)
}

fn target_of(name: &str) -> Target {
    if name == "postgresql" { Target::PostgreSql } else { Target::RavenDb }
}

fn usage() {
    eprintln!(
        "usage: terrabase-bench-lane --target <pg|raven> --workload <read|write|query|mixed> \
         [--out DIR] [--seed N] [--ops N] [--samples N] [--concurrency N] [--warmup N]"
    );
}
