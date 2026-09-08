//! Measurement and results recording.
//!
//! One measured quantity set per sample: the sample's wall-clock
//! throughput and its per-operation latency percentiles. The engine-side
//! adapter consumes the same JSON shape for the TerraBase side, so the
//! SE-15 gates classify both sides from one format.

use std::time::{Duration, SystemTime, UNIX_EPOCH};

use serde::Serialize;
use sha2::{Digest, Sha256};

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

/// The explicit admission facts needed before a result can join a comparison
/// group.  These are deliberately separate from `Environment`: environment
/// describes what was observed, while this object describes the contract the
/// run claims to share with another side.
#[derive(Clone, Debug, Serialize)]
pub struct Comparability {
    /// Digest of the invocation shared by every side in one run.
    pub run_digest: String,
    /// Digest of the exact candidate artifact under test.
    pub candidate_digest: String,
    /// Digest of the immutable workload manifest.
    pub workload_digest: String,
    /// Digest of the admitted execution topology.
    pub topology_digest: String,
    /// A caller-provided token shared by every side in one run.
    pub run_group: String,
    /// SHA-256 of the canonical lane configuration.
    pub config_digest: String,
    /// Stable identity of the controlled host, not the database server.
    pub controlled_host_identity: String,
    /// A caller-provided window token (hour bucket by default).
    pub time_window: String,
    /// Unix-second bounds observed around this run.
    pub window_start_unix_seconds: u64,
    pub window_end_unix_seconds: u64,
    /// Versioned workload and boundary contracts.
    pub workload_version: String,
    pub boundary_class: String,
    /// Repeated configuration facts make admission auditable without
    /// requiring a consumer to trust the digest alone.
    pub concurrency: usize,
    pub samples: usize,
    pub warmup_operations: usize,
    pub noise_percent: f64,
    /// Deterministic lane policies.  A mismatch in any one is non-comparable.
    pub seed_policy: String,
    pub document_policy: String,
    pub operation_policy: String,
    pub write_id_policy: String,
    /// Durability and server setup are part of the measurement contract.
    pub durability_semantics: String,
    pub server_configuration_provenance: String,
}

const WORKLOAD_VERSION: &str = "terrabase-se15-workload-v1";
const SEED_POLICY: &str = "seed-{index}, tenant benchmark-tenant, index modulo seed count";
const DOCUMENT_POLICY: &str = "seed documents have id seed-{index}, name user-{index}, group index modulo 100, 256-byte payload";
const OPERATION_POLICY: &str = "mixed uses index modulo 10: 0..4 read, 5..7 write, 8..9 query; read/query address index modulo seed count";
const WRITE_ID_POLICY: &str = "measured writes use measured-{global operation index}";

/// Computes the canonical configuration digest shared by all lane drivers.
pub fn config_digest(config: &LaneConfig) -> String {
    let canonical = format!(
        "seed_documents={};operations={};samples={};concurrency={};warmup_operations={};noise_percent={:.17}",
        config.seed_documents,
        config.operations,
        config.samples,
        config.concurrency,
        config.warmup_operations,
        config.noise_percent
    );
    Sha256::digest(canonical.as_bytes()).iter().map(|byte| format!("{byte:02x}")).collect()
}

/// Builds the admission facts for a lane result.  `boundary` is supplied for
/// the served TerraBase runner; reference drivers use their network class.
pub fn comparability(
    config: &LaneConfig,
    target: &str,
    environment: &Environment,
    boundary: Option<&str>,
) -> Comparability {
    let now = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|since| since.as_secs())
        .unwrap_or(environment.unix_seconds);
    let host = std::env::var("TERRABASE_BENCH_HOST_IDENTITY").unwrap_or_else(|_| {
        format!("{};logical-cpus={}", environment.kernel, environment.logical_cpus)
    });
    let time_window = std::env::var("TERRABASE_BENCH_TIME_WINDOW")
        .unwrap_or_else(|_| (environment.unix_seconds / 3_600).to_string());
    let run_group =
        std::env::var("TERRABASE_BENCH_RUN_GROUP").unwrap_or_else(|_| "unassigned".to_owned());
    let digest = |name: &str| std::env::var(name).unwrap_or_else(|_| "unassigned".to_owned());
    let (boundary_class, durability, server_configuration) = match target {
        "terrabase" => (
            boundary.unwrap_or("unknown").to_owned(),
            "LocalDurability::Synced acknowledged commits".to_owned(),
            "terrabase-runtime-catalog-v1".to_owned(),
        ),
        "postgresql" => (
            "network-reference".to_owned(),
            "synchronous_commit=on; fsync=on; full_page_writes=on; autocommit statement \
             acknowledgement"
                .to_owned(),
            "se15-reference-pin-v1".to_owned(),
        ),
        // The same server-side pin, but the measured boundary includes
        // pgbouncer and the HTTP/JSON service, so the provenance names the
        // whole stack the way the engine side names its runtime catalog.
        "pg-fullstack" => (
            "network-reference".to_owned(),
            "synchronous_commit=on; fsync=on; full_page_writes=on; autocommit statement \
             acknowledgement through pgbouncer transaction pooling and the pgrest HTTP/JSON \
             service"
                .to_owned(),
            "se15-reference-pin-v1; pgbouncer transaction pooling; pgrest HTTP/JSON service"
                .to_owned(),
        ),
        "sqlite" => (
            "embedded-reference".to_owned(),
            "WAL journal with synchronous=FULL and a five-second busy timeout; autocommit \
             statement acknowledgement"
                .to_owned(),
            "bundled sqlite at shipped defaults plus WAL, synchronous=FULL, busy_timeout=5000ms"
                .to_owned(),
        ),
        "turso" => (
            "embedded-reference".to_owned(),
            "WAL journal with synchronous=FULL and a five-second busy timeout; autocommit \
             statement acknowledgement"
                .to_owned(),
            "turso crate at shipped defaults plus WAL, synchronous=FULL, busy_timeout=5000ms"
                .to_owned(),
        ),
        _ => (
            "network-reference".to_owned(),
            "server-acknowledged PUT with shipped durability defaults".to_owned(),
            "se15-reference-pin-v1".to_owned(),
        ),
    };
    Comparability {
        run_digest: digest("TERRABASE_BENCH_RUN_DIGEST"),
        candidate_digest: digest("TERRABASE_BENCH_CANDIDATE_DIGEST"),
        workload_digest: digest("TERRABASE_BENCH_WORKLOAD_DIGEST"),
        topology_digest: digest("TERRABASE_BENCH_TOPOLOGY_DIGEST"),
        run_group,
        config_digest: config_digest(config),
        controlled_host_identity: host,
        time_window,
        window_start_unix_seconds: environment.unix_seconds,
        window_end_unix_seconds: now.max(environment.unix_seconds),
        workload_version: WORKLOAD_VERSION.to_owned(),
        boundary_class,
        concurrency: config.concurrency,
        samples: config.samples,
        warmup_operations: config.warmup_operations,
        noise_percent: config.noise_percent,
        seed_policy: SEED_POLICY.to_owned(),
        document_policy: DOCUMENT_POLICY.to_owned(),
        operation_policy: OPERATION_POLICY.to_owned(),
        write_id_policy: WRITE_ID_POLICY.to_owned(),
        durability_semantics: durability,
        server_configuration_provenance: server_configuration,
    }
}

/// The results document one driven side emits.
#[derive(Debug, Serialize)]
pub struct RunResults {
    /// `complete` for a run that produced every requested sample. Failed and
    /// invalid attempts use a separate failure document and are never parsed
    /// as comparable facts.
    pub status: &'static str,
    /// Which reference this is.
    pub target: &'static str,
    /// The workload measured.
    pub workload: String,
    /// The lane configuration used.
    pub config: LaneConfig,
    /// The observed environment.
    pub environment: Environment,
    /// Facts required for cross-document admission.
    pub comparability: Comparability,
    /// One entry per measured sample, in run order.
    pub samples: Vec<Sample>,
    /// Honest limits this driver knows about its own side, stated on the
    /// receipt rather than implied.
    pub limits: Vec<String>,
}

/// A failed attempt retained beside successful results. Keeping the failure
/// document makes missing samples visible without allowing partial numbers to
/// enter a comparison.
#[derive(Debug, Serialize)]
pub struct FailureResults {
    /// Always `failed` for this document shape.
    pub status: &'static str,
    /// The target that was being driven.
    pub target: &'static str,
    /// The workload that was being driven.
    pub workload: String,
    /// The requested lane configuration.
    pub config: LaneConfig,
    /// The concrete setup or operation failure.
    pub error: String,
    /// Host and endpoint facts captured even when setup failed.
    pub environment: Environment,
    /// Captured even for failures so the retained raw attempt is auditable;
    /// failed documents never enter a complete comparison group.
    pub comparability: Comparability,
    /// No partial sample is ever retained as a fact.
    pub samples: Vec<Sample>,
    /// Why this document cannot be compared.
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
        logical_cpus: std::thread::available_parallelism().map(|count| count.get()).unwrap_or(0),
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
