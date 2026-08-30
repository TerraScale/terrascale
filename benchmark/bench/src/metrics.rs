//! Measurement and results recording.
//!
//! One measured quantity set per sample: the sample's wall-clock
//! throughput and its per-operation latency percentiles. The engine-side
//! adapter consumes the same JSON shape for the TerraBase side, so the
//! SE-15 gates classify both sides from one format.

use std::time::{Duration, SystemTime, UNIX_EPOCH};

use serde::Serialize;

use crate::config::{LaneConfig, Target, Workload};

/// The measured facts of one sample window.
#[derive(Clone, Debug, Serialize)]
pub struct Sample {
    /// Total operations the window completed across all slots.
    pub operations: u64,
    /// The window's wall-clock duration in seconds.
    pub wall_seconds: f64,
    /// Operations per second over the window.
    pub throughput_ops_per_s: f64,
    /// Median per-operation latency in microseconds.
    pub p50_us: f64,
    /// 95th-percentile per-operation latency in microseconds.
    pub p95_us: f64,
    /// 99th-percentile per-operation latency in microseconds.
    pub p99_us: f64,
}

/// Computes one sample from the window's per-operation latencies and wall
/// time. Latencies are exact `Duration`s so percentile math never rounds a
/// nanosecond before it is classified.
pub fn sample_of(operations: u64, wall: Duration, mut latencies_us: Vec<f64>) -> Sample {
    latencies_us.sort_by(|left, right| left.total_cmp(right));
    let percentile = |fraction: f64| -> f64 {
        if latencies_us.is_empty() {
            return 0.0;
        }
        let index = ((latencies_us.len() as f64 - 1.0) * fraction).round() as usize;
        latencies_us[index.min(latencies_us.len() - 1)]
    };
    let wall_seconds = wall.as_secs_f64();
    Sample {
        operations,
        wall_seconds,
        throughput_ops_per_s: operations as f64 / wall_seconds.max(f64::EPSILON),
        p50_us: percentile(0.50),
        p95_us: percentile(0.95),
        p99_us: percentile(0.99),
    }
}

/// The observed identity and environment facts of one driven reference.
#[derive(Clone, Debug, Serialize)]
pub struct Environment {
    /// The identity the reference itself reported at the run: PostgreSQL's
    /// `SELECT version()` output or RavenDB's `/build/version` document,
    /// recorded verbatim.
    pub observed_identity: String,
    /// The endpoint the driver connected to.
    pub endpoint: String,
    /// The client transport this driver used, named so the receipt cannot
    /// mistake it for the engine's in-process boundary.
    pub client_transport: &'static str,
    /// Unix seconds at the run.
    pub unix_seconds: u64,
    /// The host machine's kernel string, `uname -sr` equivalent.
    pub kernel: String,
    /// How many logical CPUs the process saw.
    pub logical_cpus: usize,
}

/// The results document one driven side emits.
#[derive(Debug, Serialize)]
pub struct RunResults {
    /// Which reference this is.
    pub target: &'static str,
    /// The workload measured.
    pub workload: String,
    /// The lane configuration used.
    pub config: LaneConfig,
    /// The observed environment.
    pub environment: Environment,
    /// One entry per measured sample, in run order.
    pub samples: Vec<Sample>,
    /// Honest limits this driver knows about its own side, stated on the
    /// receipt rather than implied.
    pub limits: Vec<String>,
}

/// Captures the host facts every side of a comparison shares.
pub fn host_environment(
    observed_identity: String,
    endpoint: String,
    client_transport: &'static str,
) -> Environment {
    Environment {
        observed_identity,
        endpoint,
        client_transport,
        unix_seconds: SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map(|since| since.as_secs())
            .unwrap_or(0),
        kernel: std::fs::read_to_string("/proc/sys/kernel/osrelease")
            .map(|text| format!("Linux {}", text.trim()))
            .unwrap_or_else(|_| "unknown".to_owned()),
        logical_cpus: std::thread::available_parallelism()
            .map(|count| count.get())
            .unwrap_or(0),
    }
}

/// Renders the per-sample table this driver prints for the operator.
pub fn render_summary(target: Target, workload: Workload, results: &RunResults) -> String {
    let mut text = String::new();
    text.push_str(&format!(
        "target: {} workload: {} samples: {}\n",
        target.as_str(),
        workload.as_str(),
        results.samples.len()
    ));
    for (index, sample) in results.samples.iter().enumerate() {
        text.push_str(&format!(
            "  sample {}: {} ops in {:.3}s = {:.0} ops/s, p50 {:.1}us p95 {:.1}us p99 {:.1}us\n",
            index,
            sample.operations,
            sample.wall_seconds,
            sample.throughput_ops_per_s,
            sample.p50_us,
            sample.p95_us,
            sample.p99_us
        ));
    }
    text
}
