//! Command boundary for the Rust-owned served TerraBase benchmark runner.
//!
//! The runner is deliberately supplied as a direct executable. The reference
//! driver must not wrap it in a shell or `cargo run`, since doing so makes
//! timeout cleanup and provenance ambiguous. The executable owns result JSON
//! retention and Rust-side validation; this module only selects the explicit
//! boundary contract and waits for that process.

use std::error::Error;
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
    let status = Command::new(executable_path)
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
        Err(format!("served TerraBase runner exited with status {status}").into())
    }
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
}
