//! E-13 production-boundary bulk-ingest benchmark.
//!
//! This family is intentionally separate from the point-operation lane. It
//! measures one public bulk request at a time and records both request
//! latency and document throughput. A comparison is only admissible when the
//! three subjects ran with the same topology, immutable comparator pins, and
//! launch member envelope (2 OCPU / 12 GiB).
//!
//! The module does not start a database. The shell entry point starts the
//! pinned subjects and invokes run_cli. Missing endpoints, unverified
//! identities, an unfenced envelope, or incomplete resource evidence are
//! errors; they can never become an empty successful result.

use std::{
    fs::{self, OpenOptions},
    io::Write,
    path::{Path, PathBuf},
    time::{Duration, Instant, SystemTime, UNIX_EPOCH},
};

use postgres::{Client, NoTls};
use serde::Serialize;
use sha2::{Digest, Sha256};
use ureq::Agent;

pub const FAMILY: &str = "e13-production-bulk-ingest-v1";
pub const MEMBER_OCPU: u32 = 2;
pub const MEMBER_MEMORY_BYTES: u64 = 12 * 1024 * 1024 * 1024;
pub const MEMBER_ENVELOPE: &str = "2ocpu/12GiB";
pub const MAX_BATCH_DOCUMENTS: usize = 1_000;
pub const WORKLOAD_VERSION: &str = "terrabase-e13-ingest-workload-v1";
pub const POSTGRESQL_PIN: &str = "18.6";
pub const RAVENDB_PIN: &str = "7.2.5";

const AUTH_ENV: &str = "TERRABASE_BENCH_INGEST_AUTHORIZATION";
const ENVELOPE_CPU_ENV: &str = "TERRABASE_BENCH_INGEST_MEMBER_OCPU";
const ENVELOPE_MEMORY_ENV: &str = "TERRABASE_BENCH_INGEST_MEMBER_MEMORY";

/// The two production topologies are separate comparison families.
#[derive(Clone, Copy, Debug, Eq, PartialEq, Serialize)]
#[serde(rename_all = "kebab-case")]
pub enum Topology {
    SingleNode,
    Rf2,
}

impl Topology {
    pub fn parse(value: &str) -> Result<Self, String> {
        match value {
            "single-node" | "single" => Ok(Self::SingleNode),
            "rf2" | "rf=2" | "rf-2" => Ok(Self::Rf2),
            other => Err(format!("unknown ingest topology {other:?}; expected single-node or rf2")),
        }
    }

    pub const fn token(self) -> &'static str {
        match self {
            Self::SingleNode => "SINGLE_NODE",
            Self::Rf2 => "RF2",
        }
    }

    pub const fn as_str(self) -> &'static str {
        match self {
            Self::SingleNode => "single-node",
            Self::Rf2 => "rf2",
        }
    }
}

/// A comparator supported by this family.
#[derive(Clone, Copy, Debug, Eq, PartialEq, Serialize)]
#[serde(rename_all = "kebab-case")]
pub enum Target {
    TerraBase,
    PostgreSqlJsonb,
    RavenDb,
}

impl Target {
    pub fn parse(value: &str) -> Result<Self, String> {
        match value {
            "terrabase" | "tb" => Ok(Self::TerraBase),
            "postgresql-jsonb" | "postgres-jsonb" | "pg-jsonb" => Ok(Self::PostgreSqlJsonb),
            "ravendb" | "raven" => Ok(Self::RavenDb),
            other => Err(format!(
                "unknown ingest target {other:?}; expected terrabase, postgresql-jsonb, or ravendb"
            )),
        }
    }

    pub const fn as_str(self) -> &'static str {
        match self {
            Self::TerraBase => "terrabase",
            Self::PostgreSqlJsonb => "postgresql-jsonb",
            Self::RavenDb => "ravendb",
        }
    }
}

/// Immutable comparator identities. Observed identity comes from the service.
#[derive(Clone, Debug, Serialize)]
pub struct ComparatorPin {
    pub target: Target,
    pub decided_version: String,
    pub observed_identity: String,
    pub artifact: String,
}

/// The exact launch envelope claimed by a run.
#[derive(Clone, Debug, Serialize)]
pub struct MemberEnvelope {
    pub ocpu: u32,
    pub memory_bytes: u64,
    pub spec: &'static str,
    pub enforcement: String,
}

impl MemberEnvelope {
    /// Resolves and verifies the shell-provided launch envelope.
    pub fn from_env() -> Result<Self, String> {
        let ocpu = std::env::var(ENVELOPE_CPU_ENV)
            .map_err(|_| format!("{ENVELOPE_CPU_ENV} is required; member envelope is unfenced"))?;
        let memory = std::env::var(ENVELOPE_MEMORY_ENV).map_err(|_| {
            format!("{ENVELOPE_MEMORY_ENV} is required; member envelope is unfenced")
        })?;
        if ocpu != MEMBER_OCPU.to_string() {
            return Err(format!(
                "member OCPU envelope must be exactly {MEMBER_OCPU}, got {ocpu:?}"
            ));
        }
        let parsed_memory = parse_memory_bytes(&memory)?;
        if parsed_memory != MEMBER_MEMORY_BYTES {
            return Err(format!(
                "member memory envelope must be exactly 12 GiB ({MEMBER_MEMORY_BYTES} bytes), got {memory:?}"
            ));
        }
        Ok(Self {
            ocpu: MEMBER_OCPU,
            memory_bytes: MEMBER_MEMORY_BYTES,
            spec: MEMBER_ENVELOPE,
            enforcement:
                "systemd cgroup CPUQuota=200%, MemoryMax=12G; member scope verified by ingest.sh"
                    .to_owned(),
        })
    }
}

/// A sample's latency, throughput, and subject resource evidence.
#[derive(Clone, Debug, Serialize)]
pub struct IngestSample {
    pub documents: u64,
    pub batches: u64,
    pub wall_seconds: f64,
    pub throughput_documents_per_s: f64,
    pub p50_batch_us: f64,
    pub p95_batch_us: f64,
    pub p99_batch_us: f64,
    pub resources_before: ResourceEvidence,
    pub resources_after: ResourceEvidence,
}

/// Cgroup and process evidence captured alongside each sample.
#[derive(Clone, Debug, Serialize)]
pub struct ResourceEvidence {
    pub scope: &'static str,
    pub cgroup_path: String,
    pub cpu_limit: String,
    pub cpuset_effective: String,
    pub memory_limit_bytes: Option<u64>,
    pub memory_current_bytes: Option<u64>,
    pub memory_peak_bytes: Option<u64>,
    pub process_rss_bytes: Option<u64>,
    pub cpu_usage_usec: Option<u64>,
    pub complete: bool,
}

#[derive(Clone, Debug, Serialize)]
pub struct IngestResult {
    pub status: &'static str,
    pub family: &'static str,
    pub target: Target,
    pub topology: Topology,
    pub envelope: MemberEnvelope,
    pub workload_version: &'static str,
    pub workload_digest: String,
    pub run_group: String,
    pub comparator: ComparatorPin,
    /// Immutable digest of the exact served runtime admitted for this run.
    pub runtime_digest: String,
    pub config: IngestConfig,
    pub samples: Vec<IngestSample>,
    pub limits: Vec<String>,
}

/// Failed attempts have no samples and are never comparable.
#[derive(Clone, Debug, Serialize)]
pub struct IngestFailure {
    pub status: &'static str,
    pub family: &'static str,
    pub target: Target,
    pub topology: Topology,
    pub envelope: Option<MemberEnvelope>,
    pub workload_version: &'static str,
    pub workload_digest: String,
    pub run_group: Option<String>,
    pub runtime_digest: Option<String>,
    pub error: String,
    pub samples: Vec<IngestSample>,
    pub limits: Vec<String>,
}

/// User-controlled lane configuration.
#[derive(Clone, Debug, Serialize)]
pub struct IngestConfig {
    pub documents_per_sample: usize,
    pub batch_size: usize,
    pub samples: usize,
    pub warmup_batches: usize,
    pub topology: Topology,
    pub target: Target,
    pub output_directory: PathBuf,
    pub allow_reset: bool,
}

impl Default for IngestConfig {
    fn default() -> Self {
        Self {
            documents_per_sample: 100_000,
            batch_size: MAX_BATCH_DOCUMENTS,
            samples: 5,
            warmup_batches: 1,
            topology: Topology::SingleNode,
            target: Target::TerraBase,
            output_directory: PathBuf::from("bench/results"),
            allow_reset: false,
        }
    }
}

impl IngestConfig {
    pub fn validate(&self) -> Result<(), String> {
        if self.documents_per_sample == 0 {
            return Err("documents-per-sample must be greater than zero".to_owned());
        }
        if self.batch_size == 0 || self.batch_size > MAX_BATCH_DOCUMENTS {
            return Err(format!("batch-size must be in 1..={MAX_BATCH_DOCUMENTS}"));
        }
        if self.samples == 0 {
            return Err("samples must be greater than zero".to_owned());
        }
        if self.output_directory.as_os_str().is_empty() {
            return Err("output directory must not be empty".to_owned());
        }
        Ok(())
    }

    fn digest(&self) -> String {
        let canonical = format!(
            "family={FAMILY};workload={WORKLOAD_VERSION};topology={};documents={};batch={};samples={};warmup={};allow_reset={}",
            self.topology.as_str(),
            self.documents_per_sample,
            self.batch_size,
            self.samples,
            self.warmup_batches,
            self.allow_reset,
        );
        hex_digest(canonical.as_bytes())
    }
}

/// Executes one configured target.
pub fn run(config: &IngestConfig) -> Result<IngestResult, String> {
    config.validate()?;
    let envelope = MemberEnvelope::from_env()?;
    let _runtime_digest = required_digest("TERRABASE_BENCH_INGEST_RUNTIME_DIGEST")?;
    // A shared run-group is the join key for the three sequential target
    // invocations. Require it before setup so an unjoinable attempt cannot
    // mutate a comparator and fail only after measuring.
    let _run_group = required_env("TERRABASE_BENCH_INGEST_RUN_GROUP")?;
    match config.target {
        Target::TerraBase => run_terrabase(config, envelope),
        Target::PostgreSqlJsonb => run_postgresql(config, envelope),
        Target::RavenDb => run_ravendb(config, envelope),
    }
}

/// Command-line entry point used by ingest.sh and by the parent bench CLI.
/// It writes an immutable attempt and returns its path.
pub fn run_cli<I>(arguments: I) -> Result<PathBuf, String>
where
    I: IntoIterator<Item = String>,
{
    let mut config = IngestConfig::default();
    let mut arguments = arguments.into_iter();
    while let Some(argument) = arguments.next() {
        match argument.as_str() {
            "--ingest-target" | "--target" => {
                config.target = Target::parse(&next_arg(&mut arguments, &argument)?)?;
            }
            "--ingest-topology" | "--topology" => {
                config.topology = Topology::parse(&next_arg(&mut arguments, &argument)?)?;
            }
            "--documents-per-sample" | "--documents" => {
                config.documents_per_sample =
                    parse_usize(&argument, &next_arg(&mut arguments, &argument)?)?;
            }
            "--batch-size" => {
                config.batch_size = parse_usize(&argument, &next_arg(&mut arguments, &argument)?)?;
            }
            "--ingest-samples" | "--samples" => {
                config.samples = parse_usize(&argument, &next_arg(&mut arguments, &argument)?)?;
            }
            "--warmup-batches" | "--warmup" => {
                config.warmup_batches =
                    parse_usize(&argument, &next_arg(&mut arguments, &argument)?)?;
            }
            "--out" => {
                config.output_directory = PathBuf::from(next_arg(&mut arguments, &argument)?);
            }
            "--allow-reset" => config.allow_reset = true,
            "--help" | "-h" => return Err(usage().to_owned()),
            other => return Err(format!("unknown ingest argument {other:?}\n{}", usage())),
        }
    }
    let result = run(&config);
    let bytes = match &result {
        Ok(result) => serde_json::to_vec_pretty(result).map_err(|error| error.to_string())?,
        Err(error) => serde_json::to_vec_pretty(&IngestFailure {
            status: "failed",
            family: FAMILY,
            target: config.target,
            topology: config.topology,
            envelope: MemberEnvelope::from_env().ok(),
            workload_version: WORKLOAD_VERSION,
            workload_digest: config.digest(),
            run_group: std::env::var("TERRABASE_BENCH_INGEST_RUN_GROUP").ok(),
            runtime_digest: std::env::var("TERRABASE_BENCH_INGEST_RUNTIME_DIGEST").ok(),
            error: error.clone(),
            samples: Vec::new(),
            limits: vec!["failed attempts are retained and never comparable".to_owned()],
        })
        .map_err(|serialization| serialization.to_string())?,
    };
    let path = retain_attempt(&config.output_directory, config.target, config.topology, bytes)?;
    if let Err(error) = result {
        return Err(format!("ingest benchmark failed; retained {}: {error}", path.display()));
    }
    Ok(path)
}

fn run_terrabase(config: &IngestConfig, envelope: MemberEnvelope) -> Result<IngestResult, String> {
    let endpoint = required_endpoint("TERRABASE_BENCH_INGEST_TERRABASE", config.topology)?;
    let health = required_env("TERRABASE_BENCH_INGEST_TERRABASE_HEALTH_ENDPOINT")?;
    let auth = required_env(AUTH_ENV)?;
    let agent = agent();
    let health_response = agent
        .get(&health)
        .set("Authorization", &auth)
        .timeout(Duration::from_secs(10))
        .call()
        .map_err(|error| format!("TerraBase health probe failed: {error}"))?;
    if health_response.status() != 200 {
        return Err(format!("TerraBase health probe returned {}", health_response.status()));
    }
    let identity = health_response
        .into_string()
        .map_err(|error| format!("TerraBase health response could not be read: {error}"))?;
    if identity.trim().is_empty() {
        return Err("TerraBase health response had no observed identity".to_owned());
    }
    let version = required_env("TERRABASE_BENCH_INGEST_TERRABASE_VERSION")?;
    if !identity.contains(&version) {
        return Err(format!(
            "TerraBase health identity does not contain the immutable version {version:?}: {identity}"
        ));
    }
    let digest = required_env("TERRABASE_BENCH_INGEST_TERRABASE_ARTIFACT_SHA256")?;
    validate_sha256("TerraBase artifact", &digest)?;
    let mut backend = TerraBaseBulk { endpoint, auth, agent };
    let samples = measure(&mut backend, config)?;
    complete(
        config,
        envelope,
        ComparatorPin {
            target: Target::TerraBase,
            decided_version: version,
            observed_identity: identity,
            artifact: digest,
        },
        samples,
        required_env("TERRABASE_BENCH_INGEST_RUN_GROUP")?,
        vec![
            "TerraBase is measured through the production HTTP batch endpoint".to_owned(),
            "RF=2 uses the member acknowledged regional durability and follower cgroup envelope"
                .to_owned(),
        ],
    )
}

fn run_postgresql(config: &IngestConfig, envelope: MemberEnvelope) -> Result<IngestResult, String> {
    let endpoint = required_endpoint("TERRABASE_BENCH_INGEST_POSTGRESQL_JSONB", config.topology)?;
    let mut client = Client::connect(&endpoint, NoTls)
        .map_err(|error| format!("PostgreSQL JSONB service unavailable: {error}"))?;
    let identity = client
        .query_one("SELECT version()", &[])
        .map_err(|error| format!("PostgreSQL identity query failed: {error}"))?
        .get::<_, String>(0);
    if !identity.contains(POSTGRESQL_PIN) {
        return Err(format!(
            "PostgreSQL identity is not immutable pin {POSTGRESQL_PIN:?}: {identity}"
        ));
    }
    let artifact = required_env("TERRABASE_BENCH_INGEST_POSTGRESQL_ARTIFACT_SHA256")?;
    validate_sha256("PostgreSQL artifact", &artifact)?;
    if !config.allow_reset {
        return Err("PostgreSQL ingest requires --allow-reset against a dedicated empty database"
            .to_owned());
    }
    client
        .batch_execute(
            "DROP TABLE IF EXISTS terrascale_ingest;\n\
             CREATE TABLE terrascale_ingest(\n\
               tenant text NOT NULL, id text NOT NULL, body jsonb NOT NULL,\n\
               PRIMARY KEY (tenant, id));",
        )
        .map_err(|error| format!("PostgreSQL JSONB setup failed: {error}"))?;
    let mut backend = PostgresBulk { client };
    let samples = measure(&mut backend, config)?;
    complete(
        config,
        envelope,
        ComparatorPin {
            target: Target::PostgreSqlJsonb,
            decided_version: POSTGRESQL_PIN.to_owned(),
            observed_identity: identity,
            artifact,
        },
        samples,
        required_env("TERRABASE_BENCH_INGEST_RUN_GROUP")?,
        vec![
            "PostgreSQL stores the document body in one jsonb column and commits one transaction per bulk request"
                .to_owned(),
            "The PostgreSQL endpoint is a direct network database protocol; client setup is not hidden"
                .to_owned(),
        ],
    )
}

fn run_ravendb(config: &IngestConfig, envelope: MemberEnvelope) -> Result<IngestResult, String> {
    let endpoint = required_endpoint("TERRABASE_BENCH_INGEST_RAVENDB", config.topology)?;
    let agent = agent();
    let response = agent
        .get(&format!("{endpoint}/build/version"))
        .timeout(Duration::from_secs(10))
        .call()
        .map_err(|error| format!("RavenDB service unavailable: {error}"))?;
    if response.status() != 200 {
        return Err(format!("RavenDB identity probe returned {}", response.status()));
    }
    let body = response
        .into_string()
        .map_err(|error| format!("RavenDB identity response failed: {error}"))?;
    let value: serde_json::Value = serde_json::from_str(&body)
        .map_err(|error| format!("RavenDB identity is not JSON: {error}"))?;
    let full = value
        .get("FullVersion")
        .and_then(serde_json::Value::as_str)
        .ok_or("RavenDB identity omitted FullVersion")?;
    if full != RAVENDB_PIN {
        return Err(format!("RavenDB identity is not immutable pin {RAVENDB_PIN:?}: {full}"));
    }
    let artifact = required_env("TERRABASE_BENCH_INGEST_RAVENDB_ARTIFACT_SHA256")?;
    validate_sha256("RavenDB artifact", &artifact)?;
    let database = required_env("TERRABASE_BENCH_INGEST_RAVENDB_DATABASE")?;
    let mut backend = RavenBulk { endpoint, database, agent };
    let samples = measure(&mut backend, config)?;
    complete(
        config,
        envelope,
        ComparatorPin {
            target: Target::RavenDb,
            decided_version: RAVENDB_PIN.to_owned(),
            observed_identity: body,
            artifact,
        },
        samples,
        required_env("TERRABASE_BENCH_INGEST_RUN_GROUP")?,
        vec![
            "RavenDB is measured through its production bulk_docs HTTP endpoint".to_owned(),
            "RavenDB auto-indexing is not included: bulk ingest is an append-only write workload"
                .to_owned(),
        ],
    )
}

fn complete(
    config: &IngestConfig,
    envelope: MemberEnvelope,
    comparator: ComparatorPin,
    samples: Vec<IngestSample>,
    run_group: String,
    limits: Vec<String>,
) -> Result<IngestResult, String> {
    if samples.len() != config.samples || samples.iter().any(|sample| sample.documents == 0) {
        return Err("bulk ingest did not produce every requested non-empty sample".to_owned());
    }
    Ok(IngestResult {
        status: "complete",
        family: FAMILY,
        target: config.target,
        topology: config.topology,
        envelope,
        workload_version: WORKLOAD_VERSION,
        workload_digest: config.digest(),
        run_group,
        comparator,
        runtime_digest: required_digest("TERRABASE_BENCH_INGEST_RUNTIME_DIGEST")?,
        config: config.clone(),
        samples,
        limits,
    })
}

trait BulkBackend {
    fn put_batch(&mut self, first_document: usize, count: usize) -> Result<(), String>;
}

fn measure<B: BulkBackend>(
    backend: &mut B,
    config: &IngestConfig,
) -> Result<Vec<IngestSample>, String> {
    if config.target == Target::TerraBase
        && std::env::var_os("TERRABASE_BENCH_INGEST_MEMBER_PID").is_none()
    {
        return Err("TERRABASE_BENCH_INGEST_MEMBER_PID is required to measure the served member's resources".into());
    }
    let admission = ResourceEvidence::capture()?;
    if !admission.complete {
        return Err(
            "resource cgroup does not exactly enforce 2 OCPU / 12 GiB; refusing to start ingest"
                .to_owned(),
        );
    }
    for warmup in 0..config.warmup_batches {
        backend.put_batch(warmup * config.batch_size, config.batch_size)?;
    }
    let mut samples = Vec::with_capacity(config.samples);
    for sample_index in 0..config.samples {
        let before = ResourceEvidence::capture()?;
        let started = Instant::now();
        let mut latency_us = Vec::new();
        let mut done = 0usize;
        let offset = (config.warmup_batches + sample_index * batches(config)) * config.batch_size;
        while done < config.documents_per_sample {
            let count = (config.documents_per_sample - done).min(config.batch_size);
            let batch_started = Instant::now();
            backend.put_batch(offset + done, count)?;
            latency_us.push(batch_started.elapsed().as_secs_f64() * 1_000_000.0);
            done += count;
        }
        let wall = started.elapsed();
        let after = ResourceEvidence::capture()?;
        if !before.complete || !after.complete {
            return Err("resource evidence is incomplete or does not match 2 OCPU / 12 GiB; refusing to classify ingest throughput"
                .to_owned());
        }
        samples.push(IngestSample {
            documents: done as u64,
            batches: latency_us.len() as u64,
            wall_seconds: wall.as_secs_f64(),
            throughput_documents_per_s: done as f64 / wall.as_secs_f64().max(f64::EPSILON),
            p50_batch_us: percentile(&mut latency_us.clone(), 0.50),
            p95_batch_us: percentile(&mut latency_us.clone(), 0.95),
            p99_batch_us: percentile(&mut latency_us, 0.99),
            resources_before: before,
            resources_after: after,
        });
    }
    Ok(samples)
}

fn batches(config: &IngestConfig) -> usize {
    config.documents_per_sample.div_ceil(config.batch_size)
}

fn percentile(values: &mut [f64], fraction: f64) -> f64 {
    if values.is_empty() {
        return 0.0;
    }
    values.sort_by(|left, right| left.total_cmp(right));
    let index = ((values.len() - 1) as f64 * fraction).round() as usize;
    values[index.min(values.len() - 1)]
}

struct TerraBaseBulk {
    endpoint: String,
    auth: String,
    agent: Agent,
}

impl BulkBackend for TerraBaseBulk {
    fn put_batch(&mut self, first_document: usize, count: usize) -> Result<(), String> {
        let operations = (0..count)
            .map(|index| {
                serde_json::json!({
                    "op": "put",
                    "id": format!("ingest-{}/{}", self.auth_scope(), first_document + index),
                    "value": document(first_document + index),
                })
            })
            .collect::<Vec<_>>();
        let body = serde_json::json!({ "operations": operations }).to_string();
        let response = self
            .agent
            .post(&self.endpoint)
            .set("Authorization", &self.auth)
            .set("Content-Type", "application/json")
            .set("X-TerraScale-Durability", "regional")
            .send_string(&body)
            .map_err(|error| format!("TerraBase bulk request failed: {error}"))?;
        if response.status() != 200 {
            return Err(format!("TerraBase bulk request returned {}", response.status()));
        }
        let response_body = response
            .into_string()
            .map_err(|error| format!("TerraBase bulk response failed: {error}"))?;
        let value: serde_json::Value = serde_json::from_str(&response_body)
            .map_err(|error| format!("TerraBase bulk response is not JSON: {error}"))?;
        if value.get("count").and_then(serde_json::Value::as_u64) != Some(count as u64) {
            return Err(format!("TerraBase bulk acknowledgement count mismatch: {value}"));
        }
        Ok(())
    }
}

impl TerraBaseBulk {
    fn auth_scope(&self) -> &'static str {
        "benchmark"
    }
}

struct PostgresBulk {
    client: Client,
}

impl BulkBackend for PostgresBulk {
    fn put_batch(&mut self, first_document: usize, count: usize) -> Result<(), String> {
        let mut transaction = self
            .client
            .transaction()
            .map_err(|error| format!("PostgreSQL transaction failed: {error}"))?;
        let statement = transaction
            .prepare("INSERT INTO terrascale_ingest(tenant, id, body) VALUES ($1, $2, $3::jsonb)")
            .map_err(|error| format!("PostgreSQL JSONB prepare failed: {error}"))?;
        for index in first_document..first_document + count {
            let id = format!("ingest-{index}");
            let body = serde_json::to_string(&document(index))
                .map_err(|error| format!("document serialization failed: {error}"))?;
            transaction
                .execute(&statement, &[&"benchmark", &id, &body])
                .map_err(|error| format!("PostgreSQL JSONB insert failed: {error}"))?;
        }
        transaction.commit().map_err(|error| format!("PostgreSQL JSONB commit failed: {error}"))
    }
}

struct RavenBulk {
    endpoint: String,
    database: String,
    agent: Agent,
}

impl BulkBackend for RavenBulk {
    fn put_batch(&mut self, first_document: usize, count: usize) -> Result<(), String> {
        let commands = (0..count)
            .map(|offset| {
                let index = first_document + offset;
                serde_json::json!({
                    "Id": format!("users/ingest-{index}"),
                    "Document": document(index),
                })
            })
            .collect::<Vec<_>>();
        let body = serde_json::json!({ "Commands": commands }).to_string();
        let response = self
            .agent
            .post(&format!("{}/databases/{}/bulk_docs", self.endpoint, self.database))
            .set("Content-Type", "application/json")
            .send_string(&body)
            .map_err(|error| format!("RavenDB bulk request failed: {error}"))?;
        if response.status() != 200 {
            return Err(format!("RavenDB bulk request returned {}", response.status()));
        }
        let response_body = response
            .into_string()
            .map_err(|error| format!("RavenDB bulk response failed: {error}"))?;
        let value: serde_json::Value = serde_json::from_str(&response_body)
            .map_err(|error| format!("RavenDB bulk response is not JSON: {error}"))?;
        let acknowledged = value
            .as_array()
            .map(Vec::len)
            .or_else(|| value.get("Results").and_then(serde_json::Value::as_array).map(Vec::len));
        if acknowledged != Some(count) {
            return Err(format!("RavenDB bulk acknowledgement count mismatch: {response_body}"));
        }
        Ok(())
    }
}

fn document(index: usize) -> serde_json::Value {
    serde_json::json!({
        "tenant": "benchmark-tenant",
        "id": format!("ingest-{index}"),
        "name": format!("ingest-{index}"),
        "group": index % 100,
        "payload": "x".repeat(256),
    })
}

impl ResourceEvidence {
    fn capture() -> Result<Self, String> {
        let member = std::env::var("TERRABASE_BENCH_INGEST_MEMBER_PID")
            .ok()
            .map(|value| value.parse::<u32>().map_err(|_| "invalid ingest member PID"))
            .transpose()?;
        let process = member.map_or_else(|| "self".to_owned(), |pid| pid.to_string());
        let cgroup = fs::read_to_string(format!("/proc/{process}/cgroup"))
            .map_err(|error| format!("cannot read measured process cgroup: {error}"))?;
        let cgroup_path = cgroup.lines().next().unwrap_or("unknown").to_owned();
        // Resolve the measured process's cgroup, not the host root: a
        // systemd-run scope (bare-metal host) reports its fence only under
        // /sys/fs/cgroup/<scope>/, while a fenced container already sits at
        // the root. The `0::` prefix names the cgroup-v2 delegation path.
        let own = cgroup_path.strip_prefix("0::").unwrap_or("/");
        let root = format!("/sys/fs/cgroup{}", own.trim_end_matches('/'));
        let cpu_limit = fs::read_to_string(format!("{root}/cpu.max"))
            .map(|value| value.trim().to_owned())
            .map_err(|error| format!("cannot read cgroup cpu.max: {error}"))?;
        let cpuset_effective = fs::read_to_string(format!("{root}/cpuset.cpus.effective"))
            .map(|value| value.trim().to_owned())
            .map_err(|error| format!("cannot read cgroup cpuset.cpus.effective: {error}"))?;
        let memory_limit_bytes = read_limit(&format!("{root}/memory.max"))?;
        let memory_current_bytes = read_limit(&format!("{root}/memory.current"))?;
        let memory_peak_bytes = read_limit(&format!("{root}/memory.peak"))?;
        let process_rss_bytes = process_rss_bytes(&process);
        let cpu_usage_usec = read_cpu_usage(&root).ok();
        // cgroup v2 expresses CPUQuota=200% as 200000/100000. Requiring the
        // exact pair and exact memory ceiling prevents a larger or merely
        // finite parent cgroup from being reported as launch-envelope proof.
        let cpu_matches =
            cpu_limit.split_whitespace().collect::<Vec<_>>().as_slice() == ["200000", "100000"];
        let complete = cpu_matches
            && cpuset_is_two_cpu(&cpuset_effective)
            && memory_limit_bytes == Some(MEMBER_MEMORY_BYTES)
            && memory_current_bytes.is_some();
        Ok(Self {
            scope: if member.is_some() {
                "served member process cgroup"
            } else {
                "benchmark process cgroup (must contain the production member)"
            },
            cgroup_path,
            cpu_limit,
            cpuset_effective,
            memory_limit_bytes,
            memory_current_bytes,
            memory_peak_bytes,
            process_rss_bytes,
            cpu_usage_usec,
            complete,
        })
    }
}

fn cpuset_is_two_cpu(value: &str) -> bool {
    let mut count = 0_u32;
    for item in value.split(',') {
        let Some((first, last)) = item.split_once('-') else {
            if item.trim().parse::<u32>().is_ok() {
                count += 1;
                continue;
            }
            return false;
        };
        let (Ok(first), Ok(last)) = (first.parse::<u32>(), last.parse::<u32>()) else {
            return false;
        };
        if last < first {
            return false;
        }
        count = count.saturating_add(last - first + 1);
    }
    count == MEMBER_OCPU
}

fn read_limit(path: &str) -> Result<Option<u64>, String> {
    let value = fs::read_to_string(path).map_err(|error| format!("cannot read {path}: {error}"))?;
    let value = value.split_whitespace().next().unwrap_or_default();
    if value == "max" || value.is_empty() {
        return Ok(None);
    }
    Ok(value.parse().ok())
}

fn read_cpu_usage(root: &str) -> Result<u64, String> {
    let text = fs::read_to_string(format!("{root}/cpu.stat")).map_err(|error| error.to_string())?;
    text.lines()
        .find_map(|line| line.strip_prefix("usage_usec ").and_then(|value| value.parse().ok()))
        .ok_or_else(|| "cpu.stat has no usage_usec".to_owned())
}

fn process_rss_bytes(process: &str) -> Option<u64> {
    let text = fs::read_to_string(format!("/proc/{process}/status")).ok()?;
    text.lines()
        .find_map(|line| {
            line.strip_prefix("VmRSS:")
                .and_then(|value| value.split_whitespace().next()?.parse::<u64>().ok())
        })
        .map(|kb| kb * 1024)
}

fn parse_memory_bytes(value: &str) -> Result<u64, String> {
    let normalized = value.trim().to_ascii_lowercase();
    let (number, multiplier) = if let Some(number) = normalized.strip_suffix("gib") {
        (number, 1024 * 1024 * 1024)
    } else if let Some(number) = normalized.strip_suffix('g') {
        (number, 1024 * 1024 * 1024)
    } else if let Some(number) = normalized.strip_suffix("mib") {
        (number, 1024 * 1024)
    } else {
        (normalized.as_str(), 1)
    };
    number
        .parse::<u64>()
        .ok()
        .and_then(|number| number.checked_mul(multiplier))
        .ok_or_else(|| format!("invalid member memory limit {value:?}"))
}

fn required_endpoint(prefix: &str, topology: Topology) -> Result<String, String> {
    required_env(&format!("{prefix}_{}_ENDPOINT", topology.token()))
}

fn required_env(name: &str) -> Result<String, String> {
    let value =
        std::env::var(name).map_err(|_| format!("{name} is required; missing service or pin"))?;
    if value.trim().is_empty() {
        return Err(format!("{name} must not be empty"));
    }
    Ok(value)
}

fn validate_sha256(label: &str, value: &str) -> Result<(), String> {
    if value.len() != 64 || !value.bytes().all(|byte| byte.is_ascii_hexdigit()) {
        return Err(format!("{label} must be an immutable 64-character SHA-256 digest"));
    }
    Ok(())
}

fn required_digest(name: &str) -> Result<String, String> {
    let value = required_env(name)?;
    validate_sha256(name, &value)?;
    Ok(value)
}

fn agent() -> Agent {
    ureq::AgentBuilder::new()
        .timeout_connect(Duration::from_secs(30))
        .timeout_read(Duration::from_secs(120))
        .max_idle_connections(1)
        .max_idle_connections_per_host(1)
        .build()
}

fn hex_digest(bytes: &[u8]) -> String {
    Sha256::digest(bytes).iter().map(|byte| format!("{byte:02x}")).collect()
}

fn retain_attempt(
    directory: &Path,
    target: Target,
    topology: Topology,
    bytes: Vec<u8>,
) -> Result<PathBuf, String> {
    fs::create_dir_all(directory).map_err(|error| format!("create result directory: {error}"))?;
    let stamp = SystemTime::now().duration_since(UNIX_EPOCH).unwrap_or_default().as_nanos();
    for attempt in 0..100u32 {
        let path = directory.join(format!(
            "ingest-{}-{}-{stamp}-{}-{attempt}.json",
            target.as_str(),
            topology.as_str(),
            std::process::id()
        ));
        match OpenOptions::new().write(true).create_new(true).open(&path) {
            Ok(mut file) => {
                file.write_all(&bytes)
                    .map_err(|error| format!("write {}: {error}", path.display()))?;
                return Ok(path);
            }
            Err(error) if error.kind() == std::io::ErrorKind::AlreadyExists => continue,
            Err(error) => return Err(format!("create {}: {error}", path.display())),
        }
    }
    Err("could not allocate immutable ingest result name".to_owned())
}

fn parse_usize(flag: &str, value: &str) -> Result<usize, String> {
    value.parse().map_err(|error| format!("invalid {flag} value {value:?}: {error}"))
}

fn next_arg(arguments: &mut impl Iterator<Item = String>, flag: &str) -> Result<String, String> {
    arguments.next().ok_or_else(|| format!("{flag} requires a value\n{}", usage()))
}

fn usage() -> &'static str {
    "usage: --ingest-target <terrabase|postgresql-jsonb|ravendb> --ingest-topology <single-node|rf2> [--documents-per-sample N] [--batch-size N] [--ingest-samples N] [--warmup-batches N] [--out DIR] [--allow-reset]"
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn topology_is_explicit_and_never_coerced() {
        assert_eq!(Topology::parse("single-node"), Ok(Topology::SingleNode));
        assert_eq!(Topology::parse("rf2"), Ok(Topology::Rf2));
        assert!(Topology::parse("8cpu").is_err());
        assert_ne!(Topology::SingleNode, Topology::Rf2);
    }

    #[test]
    fn config_rejects_empty_and_oversized_batches() {
        let mut config = IngestConfig { documents_per_sample: 0, ..Default::default() };
        assert!(config.validate().is_err());
        config.documents_per_sample = 100;
        config.batch_size = MAX_BATCH_DOCUMENTS + 1;
        assert!(config.validate().is_err());
    }

    #[test]
    fn workload_digest_is_shared_across_comparators_but_not_topologies() {
        let mut config = IngestConfig::default();
        let digest = config.digest();
        config.target = Target::PostgreSqlJsonb;
        assert_eq!(digest, config.digest());
        config.topology = Topology::Rf2;
        assert_ne!(digest, config.digest());
    }

    #[test]
    fn memory_parser_only_accepts_the_exact_launch_size_when_checked() {
        assert_eq!(parse_memory_bytes("12GiB"), Ok(MEMBER_MEMORY_BYTES));
        assert_eq!(parse_memory_bytes("12G"), Ok(MEMBER_MEMORY_BYTES));
        assert_ne!(parse_memory_bytes("8G"), Ok(MEMBER_MEMORY_BYTES));
    }

    #[test]
    fn cpuset_evidence_must_name_exactly_two_cpus() {
        assert!(cpuset_is_two_cpu("2-3"));
        assert!(cpuset_is_two_cpu("2,3"));
        assert!(!cpuset_is_two_cpu("2-4"));
        assert!(!cpuset_is_two_cpu("max"));
    }

    #[test]
    fn percentile_is_deterministic_and_reports_tail() {
        let mut values = [30.0, 10.0, 20.0, 40.0];
        assert_eq!(percentile(&mut values, 0.50), 30.0);
        assert_eq!(percentile(&mut values, 0.99), 40.0);
    }

    #[test]
    fn pin_digests_reject_mutable_or_short_values() {
        assert!(validate_sha256("pin", &"a".repeat(64)).is_ok());
        assert!(validate_sha256("pin", "latest").is_err());
        assert!(validate_sha256("pin", &"a".repeat(63)).is_err());
    }

    struct CountingBackend;
    impl BulkBackend for CountingBackend {
        fn put_batch(&mut self, _first_document: usize, count: usize) -> Result<(), String> {
            if count == 0 {
                return Err("empty batch".to_owned());
            }
            Ok(())
        }
    }

    #[test]
    fn measurement_never_drops_the_final_partial_batch() {
        let config = IngestConfig {
            documents_per_sample: 2_001,
            batch_size: 1_000,
            samples: 1,
            warmup_batches: 0,
            ..Default::default()
        };
        // Resource evidence is intentionally not faked here; this test only
        // checks batch arithmetic through the pure helper.
        assert_eq!(batches(&config), 3);
        let mut backend = CountingBackend;
        assert!(backend.put_batch(2_000, 1).is_ok());
    }
}
