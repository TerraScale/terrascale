//! Command boundary for the Rust-owned served TerraBase benchmark runner.
//!
//! The runner is deliberately supplied as a direct executable. The reference
//! driver must not wrap it in a shell or `cargo run`, since doing so makes
//! timeout cleanup and provenance ambiguous. The executable owns result JSON
//! retention and Rust-side validation; this module only selects the explicit
//! boundary contract and waits for that process.

use std::collections::HashSet;
use std::error::Error;
use std::fs;
use std::path::Path;
use std::process::{Command, Stdio};

use crate::config::{LaneConfig, Workload};

pub const DIAGNOSTIC_BOUNDARY: &str = "diagnostic-qualification";
pub const PRODUCTION_BOUNDARY: &str = "production-candidate";

pub fn run(
    workload: Workload,
    config: LaneConfig,
    output: &Path,
    boundary: &str,
) -> Result<(), Box<dyn Error + Send + Sync>> {
    if boundary != DIAGNOSTIC_BOUNDARY && boundary != PRODUCTION_BOUNDARY {
        return Err(format!("unknown TerraBase benchmark boundary contract {boundary:?}").into());
    }
    if boundary == PRODUCTION_BOUNDARY {
        return Err("production-candidate benchmark admission is unavailable".into());
    }
    let executable = std::env::var_os("TERRABASE_BENCH_SERVED_RUNNER")
        .ok_or("TERRABASE_BENCH_SERVED_RUNNER is required for --target terrabase")?;
    let executable_path = Path::new(&executable);
    if !executable_path.is_file() {
        return Err(
            format!("served runner executable is missing: {}", executable_path.display()).into()
        );
    }
    let prior_attempts = retained_attempt_names(output);
    let status = Command::new(executable_path)
        .env("TERRABASE_BENCH_BOUNDARY_CLASS", boundary)
        .env("TERRABASE_BENCH_OUTPUT_DIR", output)
        .env("TERRABASE_BENCH_SEED_DOCUMENTS", config.seed_documents.to_string())
        .env("TERRABASE_BENCH_OPERATIONS", config.operations.to_string())
        .env("TERRABASE_BENCH_SAMPLES", config.samples.to_string())
        .env("TERRABASE_BENCH_CONCURRENCY", config.concurrency.to_string())
        .env("TERRABASE_BENCH_WARMUP_OPERATIONS", config.warmup_operations.to_string())
        .env("TERRABASE_BENCH_NOISE_PERCENT", config.noise_percent.to_string())
        .arg("--boundary-class")
        .arg(boundary)
        .arg("--workload")
        .arg(workload.as_str())
        .arg("--output-directory")
        .arg(output)
        .arg("--seed-documents")
        .arg(config.seed_documents.to_string())
        .arg("--operations")
        .arg(config.operations.to_string())
        .arg("--samples")
        .arg(config.samples.to_string())
        .arg("--concurrency")
        .arg(config.concurrency.to_string())
        .arg("--warmup")
        .arg(config.warmup_operations.to_string())
        .arg("--noise-percent")
        .arg(config.noise_percent.to_string())
        .stdin(Stdio::null())
        .status()
        .map_err(|error| format!("start served TerraBase runner: {error}"))?;
    if status.success() {
        Ok(())
    } else {
        let retained = newly_retained_failure(output, workload, &prior_attempts);
        match retained {
            Some(path) => Err(format!(
                "served TerraBase runner exited with status {status}; retained canonical failure {}",
                path.display()
            )
            .into()),
            None => Err(format!("served TerraBase runner exited with status {status}").into()),
        }
    }
}

fn retained_attempt_names(directory: &Path) -> HashSet<String> {
    fs::read_dir(directory)
        .into_iter()
        .flatten()
        .flatten()
        .filter_map(|entry| entry.file_name().into_string().ok())
        .collect()
}

fn newly_retained_failure(
    directory: &Path,
    workload: Workload,
    prior_attempts: &HashSet<String>,
) -> Option<std::path::PathBuf> {
    let prefix = format!("terrabase-{}-", workload.as_str());
    let mut candidates = fs::read_dir(directory)
        .ok()?
        .flatten()
        .filter_map(|entry| {
            let name = entry.file_name().into_string().ok()?;
            if prior_attempts.contains(&name)
                || !name.starts_with(&prefix)
                || !name.ends_with(".json")
            {
                return None;
            }
            let path = entry.path();
            let text = fs::read_to_string(&path).ok()?;
            let value: serde_json::Value = serde_json::from_str(&text).ok()?;
            (value.get("status").and_then(serde_json::Value::as_str) == Some("failed"))
                .then_some(path)
        })
        .collect::<Vec<_>>();
    candidates.sort();
    candidates.pop()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn contract_names_are_closed() {
        assert_ne!(DIAGNOSTIC_BOUNDARY, PRODUCTION_BOUNDARY);
    }

    #[test]
    fn unknown_contracts_fail_before_starting_a_process() {
        let error =
            run(Workload::Read, LaneConfig::RECORD_MINIMUM, Path::new("/tmp/unused"), "in-process")
                .expect_err("unknown contract must be rejected");
        assert!(error.to_string().contains("unknown TerraBase benchmark boundary"));
    }

    #[test]
    fn retained_failure_scan_ignores_old_and_nonfailed_attempts() {
        let directory = std::env::temp_dir()
            .join(format!("terrabase-served-failure-scan-{}", std::process::id()));
        fs::create_dir_all(&directory).expect("failure scan directory");
        fs::write(directory.join("terrabase-read-old.json"), br#"{"status":"failed"}"#)
            .expect("old failure");
        fs::write(directory.join("terrabase-read-new.json"), br#"{"status":"failed"}"#)
            .expect("new failure");
        fs::write(directory.join("terrabase-read-complete.json"), br#"{"status":"complete"}"#)
            .expect("complete attempt");
        let mut prior = HashSet::new();
        prior.insert("terrabase-read-old.json".to_owned());
        let selected = newly_retained_failure(&directory, Workload::Read, &prior)
            .expect("new failure is selected");
        assert!(selected.ends_with("terrabase-read-new.json"));
        let _ = fs::remove_dir_all(directory);
    }
}
