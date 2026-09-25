//! Fixed P04 paired-trial analysis. This CLI never executes a measured scan.

use std::collections::{BTreeMap, HashSet};
use std::fmt::Write as _;
use std::fs::{self, File, OpenOptions};
use std::io::{Cursor, Read, Write};
use std::path::{Component, Path, PathBuf};

use anyhow::{Context, Result, bail, ensure};
use chacha20::ChaCha20Rng;
use clap::Parser;
use flate2::bufread::GzDecoder;
use rand::{RngExt, SeedableRng};
use serde::{Deserialize, Serialize};
use serde_json::Value;
use sha2::{Digest, Sha256};
use tar::Archive;

use super::{hash_file, hex_hash};

const PROTOCOL_SHA256: &str = "138abc0c939335103311d8edf97a1a060dd68f0d728351f4d5380bd82bde0a22";
const BOOTSTRAP_SEED: u64 = 2_026_092_404;
const PAIRS: usize = 8;
const REPLICATES: usize = 10_000;
const WARM_SCANS: usize = 999;
const MANIFEST_LIMIT: u64 = 1 << 20;
const RECEIPT_LIMIT: u64 = 2 << 20;
const HOST_MANIFEST_LIMIT: u64 = 8 << 20;
const SAMPLE_LIMIT: u64 = 4 << 20;
const EVIDENCE_LIMIT: u64 = 8 << 20;
const ARCHIVE_LIMIT: u64 = 512 << 20;
const ARCHIVE_UNCOMPRESSED_LIMIT: u64 = 32 << 20;
const ARCHIVE_RECORD_LIMIT: usize = 64;
const EMPTY_SHA256: &str = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

#[derive(Parser)]
pub(super) struct AnalyzeArgs {
    /// Explicit, prospectively registered eight-pair campaign manifest.
    #[arg(long)]
    campaign: PathBuf,
    /// Fresh machine-readable output under bench/.
    #[arg(long)]
    json_out: PathBuf,
    /// Fresh Markdown table under bench/.
    #[arg(long)]
    markdown_out: PathBuf,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
#[allow(clippy::struct_field_names)] // Campaign field names are part of the sealed JSON schema.
struct Campaign {
    schema_version: u32,
    campaign_id: String,
    protocol_sha256: String,
    bootstrap_seed: u64,
    measured: Measured,
    pairs: Vec<PairInput>,
    diagnostic: PairInput,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct Measured {
    program_sha256: String,
    source_inventory: PinnedFile,
    artifact_map: PinnedFile,
    benchmark_revision: String,
    engine_revision: String,
    kernel_sha256: String,
    rootfs_sha256: String,
    firecracker_version: String,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct PinnedFile {
    path: PathBuf,
    sha256: String,
}

struct GuestOutputNames {
    receipt: String,
    samples: String,
    manifest: String,
}

#[derive(Clone, Copy, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "snake_case")]
enum Order {
    SqliteThenTerrabase,
    TerrabaseThenSqlite,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct PairInput {
    id: String,
    order: Order,
    sqlite: LaneInput,
    terrabase: LaneInput,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct LaneInput {
    run_id: String,
    vm_id: String,
    volume_id: String,
    cache_policy: String,
    host_cpu_control: String,
    reset_evidence: PathBuf,
    guest_command_log: PathBuf,
    run_manifest: PathBuf,
    guest_manifest: PathBuf,
    receipt: PathBuf,
    samples: PathBuf,
    compiled_binding: PathBuf,
    guest_output_archive: Option<PathBuf>,
}

#[derive(Serialize)]
struct InputHash {
    path: String,
    sha256: String,
}

#[derive(Clone, Copy, Serialize)]
#[allow(clippy::struct_field_names)] // The serialized endpoint names include their ns unit.
struct Endpoints {
    first_after_fixture_ns: u64,
    warm_p50_ns: u64,
    warm_p95_ns: u64,
    warm_p99_ns: u64,
    warm_p999_ns: u64,
}

impl Endpoints {
    fn values(self) -> [u64; 5] {
        [
            self.first_after_fixture_ns,
            self.warm_p50_ns,
            self.warm_p95_ns,
            self.warm_p99_ns,
            self.warm_p999_ns,
        ]
    }
}

#[derive(Serialize)]
struct LaneReport {
    run_id: String,
    vm_id: String,
    volume_id: String,
    cache_policy: String,
    host_cpu_control: String,
    host_cpu_model: Option<String>,
    output_filesystem: Option<String>,
    host_run_status: Option<String>,
    guest_exit_code: Option<i64>,
    started_unix_ms: Option<u64>,
    finished_unix_ms: Option<u64>,
    firecracker_sha256: Option<String>,
    controller_sha256: Option<String>,
    guest_agent_sha256: Option<String>,
    measured_receipt_status: Option<String>,
    measured_receipt_error: Option<String>,
    status: &'static str,
    errors: Vec<String>,
    endpoints: Option<Endpoints>,
    fixture_sha256: Option<String>,
    final_state_sha256: Option<String>,
    receipt_path: String,
    live_benchmark_repository: Option<Value>,
    live_subject_repository: Option<Value>,
    runtime_checkout_observation: Option<Value>,
    input_files: Vec<InputHash>,
}

#[derive(Serialize)]
struct PairReport {
    id: String,
    order: Order,
    sqlite: LaneReport,
    terrabase: LaneReport,
    complete: bool,
}

#[derive(Serialize)]
struct MetricReport {
    metric: &'static str,
    estimator: &'static str,
    sqlite_sum_ns: u128,
    terrabase_sum_ns: u128,
    point_ratio: f64,
    ci95_lower: f64,
    ci95_upper: f64,
    classification: &'static str,
    bootstrap_seed: u64,
    prng: &'static str,
    resample_index_encoding: &'static str,
    resample_index_sha256: String,
    pair_values_ns: Vec<[u64; 2]>,
}

#[derive(Clone, Serialize)]
#[allow(clippy::struct_field_names)] // The serialized identity names specify the SHA-256 algorithm.
struct AnalyzerIdentity {
    binary_sha256: String,
    main_source_sha256: String,
    analysis_source_sha256: String,
    dependency_lock_sha256: String,
    protocol_sha256: &'static str,
    measured_program_sha256: String,
    measured_source_inventory_sha256: String,
    measured_artifact_map_sha256: String,
}

#[derive(Serialize)]
struct Report {
    schema_version: u32,
    campaign_id: String,
    status: &'static str,
    failures: Vec<String>,
    latency_pairs: Vec<PairReport>,
    diagnostic_pair: Option<PairReport>,
    metrics: Vec<MetricReport>,
    estimator: &'static str,
    bootstrap_replicates_per_metric: usize,
    bootstrap_seed: u64,
    acceptance_qualified: bool,
    qualification_limits: Vec<String>,
    analyzer: AnalyzerIdentity,
    campaign_manifest: Option<InputHash>,
    source_inventory: Option<InputHash>,
    artifact_map: Option<InputHash>,
}

pub(super) fn run(args: &AnalyzeArgs) -> Result<()> {
    ensure!(
        args.json_out != args.markdown_out,
        "analysis outputs must differ"
    );
    check_output_under_bench(&args.json_out)?;
    check_output_under_bench(&args.markdown_out)?;
    ensure!(
        !args.json_out.exists() && !args.markdown_out.exists(),
        "analysis output already exists"
    );
    let analyzer = AnalyzerIdentity {
        binary_sha256: hash_file(&std::env::current_exe()?)?,
        main_source_sha256: hex_hash(include_bytes!("main.rs")),
        analysis_source_sha256: hex_hash(include_bytes!("analysis.rs")),
        dependency_lock_sha256: hex_hash(include_bytes!("../Cargo.lock")),
        protocol_sha256: PROTOCOL_SHA256,
        measured_program_sha256: String::new(),
        measured_source_inventory_sha256: String::new(),
        measured_artifact_map_sha256: String::new(),
    };
    let report = match evaluate(&args.campaign, analyzer.clone()) {
        Ok(report) => report,
        Err(error) => Report {
            schema_version: 1,
            campaign_id: "unreadable-campaign".into(),
            status: "invalid",
            failures: vec![format!("campaign input: {error}")],
            latency_pairs: Vec::new(),
            diagnostic_pair: None,
            metrics: Vec::new(),
            estimator: "sum(T_i)/sum(S_i)",
            bootstrap_replicates_per_metric: REPLICATES,
            bootstrap_seed: BOOTSTRAP_SEED,
            acceptance_qualified: false,
            qualification_limits: vec!["campaign input could not be validated".into()],
            analyzer,
            campaign_manifest: None,
            source_inventory: None,
            artifact_map: None,
        },
    };
    let mut json = OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(&args.json_out)?;
    serde_json::to_writer_pretty(&mut json, &report)?;
    json.write_all(b"\n")?;
    json.sync_all()?;
    let mut markdown = OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(&args.markdown_out)?;
    markdown.write_all(render_markdown(&report).as_bytes())?;
    markdown.sync_all()?;
    if report.status != "valid" {
        bail!(
            "P04 campaign invalid; failure report: {}",
            args.json_out.display()
        );
    }
    println!("{}", args.json_out.display());
    Ok(())
}

fn check_output_under_bench(path: &Path) -> Result<()> {
    let parent = path.parent().context("analysis output needs parent")?;
    let parent = fs::canonicalize(parent).context("analysis output parent")?;
    let bench = fs::canonicalize("bench").context("run analyzer from ts-benchmark root")?;
    ensure!(
        parent.starts_with(bench),
        "analysis outputs must live under ts-benchmark/bench"
    );
    Ok(())
}

#[allow(clippy::too_many_lines)] // Keep the campaign validation order visible in one pass.
fn evaluate(path: &Path, mut analyzer: AnalyzerIdentity) -> Result<Report> {
    let base = path
        .parent()
        .filter(|parent| !parent.as_os_str().is_empty())
        .unwrap_or_else(|| Path::new("."));
    let bytes = read_bounded(path, MANIFEST_LIMIT)?;
    let campaign: Campaign =
        serde_json::from_slice(&bytes).context("parse P04 campaign manifest")?;
    let mut report = Report {
        schema_version: 1,
        campaign_id: campaign.campaign_id.clone(),
        status: "invalid",
        failures: Vec::new(),
        latency_pairs: Vec::new(),
        diagnostic_pair: None,
        metrics: Vec::new(),
        estimator: "sum(T_i)/sum(S_i)",
        bootstrap_replicates_per_metric: REPLICATES,
        bootstrap_seed: BOOTSTRAP_SEED,
        acceptance_qualified: false,
        qualification_limits: vec![
            "host cache reset, exclusive host CPU, upstream image provenance and complete resource gates require independent qualification".into(),
            "provisional focused P04 timing cannot close the full P04 matrix row".into(),
            "the hash-pinned build artifact map is an operator assertion, not a cryptographic proof that the measured ELF was compiled from the listed inventory".into(),
        ],
        analyzer: analyzer.clone(),
        campaign_manifest: Some(InputHash {
            path: path.display().to_string(),
            sha256: hex_hash(&bytes),
        }),
        source_inventory: None,
        artifact_map: None,
    };
    validate_campaign(&campaign, &mut report.failures);
    let source_bytes = match read_pinned(base, &campaign.measured.source_inventory, EVIDENCE_LIMIT)
    {
        Ok(bytes) => {
            report.source_inventory = Some(InputHash {
                path: resolve(base, &campaign.measured.source_inventory.path)
                    .display()
                    .to_string(),
                sha256: hex_hash(&bytes),
            });
            bytes
        }
        Err(error) => {
            report
                .failures
                .push(format!("compiled source inventory: {error}"));
            Vec::new()
        }
    };
    let inventory = match parse_inventory(&source_bytes) {
        Ok(inventory) => inventory,
        Err(error) => {
            report
                .failures
                .push(format!("compiled source inventory: {error}"));
            BTreeMap::new()
        }
    };
    let artifact_bytes = match read_pinned(base, &campaign.measured.artifact_map, EVIDENCE_LIMIT) {
        Ok(bytes) => {
            report.artifact_map = Some(InputHash {
                path: resolve(base, &campaign.measured.artifact_map.path)
                    .display()
                    .to_string(),
                sha256: hex_hash(&bytes),
            });
            bytes
        }
        Err(error) => {
            report.failures.push(format!("build artifact map: {error}"));
            Vec::new()
        }
    };
    if !artifact_bytes.is_empty()
        && (!String::from_utf8_lossy(&artifact_bytes).contains(&campaign.measured.program_sha256)
            || !String::from_utf8_lossy(&artifact_bytes)
                .contains(&campaign.measured.source_inventory.sha256))
    {
        report.failures.push(
            "pinned operator artifact map does not name both measured program and source inventory"
                .into(),
        );
    }
    analyzer
        .measured_program_sha256
        .clone_from(&campaign.measured.program_sha256);
    analyzer
        .measured_source_inventory_sha256
        .clone_from(&campaign.measured.source_inventory.sha256);
    analyzer
        .measured_artifact_map_sha256
        .clone_from(&campaign.measured.artifact_map.sha256);
    report.analyzer = analyzer;

    let mut run_ids = HashSet::new();
    let mut vm_ids = HashSet::new();
    let mut volume_ids = HashSet::new();
    for pair in &campaign.pairs {
        let pair_report = analyze_pair(base, pair, false, &campaign.measured, &inventory);
        collect_uniqueness(
            pair,
            &mut run_ids,
            &mut vm_ids,
            &mut volume_ids,
            &mut report.failures,
        );
        if !pair_report.complete {
            report.failures.push(format!(
                "pair {} is incomplete or failed; no re-pairing",
                pair.id
            ));
        }
        report.latency_pairs.push(pair_report);
    }
    report
        .failures
        .extend(check_cross_lane_identity(&report.latency_pairs));
    report
        .failures
        .extend(check_serial_schedule(&report.latency_pairs));
    if report.failures.is_empty() {
        match analyze_metrics(&report.latency_pairs, campaign.bootstrap_seed) {
            Ok(metrics) => report.metrics = metrics,
            Err(error) => report.failures.push(format!("paired inference: {error}")),
        }
    }
    let diagnostic = analyze_pair(
        base,
        &campaign.diagnostic,
        true,
        &campaign.measured,
        &inventory,
    );
    collect_uniqueness(
        &campaign.diagnostic,
        &mut run_ids,
        &mut vm_ids,
        &mut volume_ids,
        &mut report.failures,
    );
    if !diagnostic.complete {
        report
            .failures
            .push("diagnostic pair D01 is incomplete or failed".into());
    }
    if diagnostic.complete {
        if let Some(reference) = report.latency_pairs.first() {
            for lane in [&diagnostic.sqlite, &diagnostic.terrabase] {
                for (label, value, expected) in [
                    (
                        "logical fixture",
                        &lane.fixture_sha256,
                        &reference.sqlite.fixture_sha256,
                    ),
                    (
                        "Firecracker executable",
                        &lane.firecracker_sha256,
                        &reference.sqlite.firecracker_sha256,
                    ),
                    (
                        "controller",
                        &lane.controller_sha256,
                        &reference.sqlite.controller_sha256,
                    ),
                    (
                        "guest agent",
                        &lane.guest_agent_sha256,
                        &reference.sqlite.guest_agent_sha256,
                    ),
                    (
                        "host CPU model",
                        &lane.host_cpu_model,
                        &reference.sqlite.host_cpu_model,
                    ),
                    (
                        "output filesystem",
                        &lane.output_filesystem,
                        &reference.sqlite.output_filesystem,
                    ),
                ] {
                    if value != expected {
                        report.failures.push(format!(
                            "D01 run {} {label} differs from latency cohort",
                            lane.run_id
                        ));
                    }
                }
            }
        }
    }
    if let Some(last) = report.latency_pairs.last() {
        if let (Some(previous_end), Some(diagnostic_start)) = (
            pair_bounds(last).map(|bounds| bounds.1),
            pair_bounds(&diagnostic).map(|bounds| bounds.0),
        ) {
            if previous_end > diagnostic_start {
                report
                    .failures
                    .push("D01 overlaps or precedes the completed latency cohort".into());
            }
        }
    }
    report.diagnostic_pair = Some(diagnostic);
    if report.failures.is_empty() {
        report.status = "valid";
    } else {
        for metric in &mut report.metrics {
            if metric.classification == "provisional_win" {
                metric.classification = "no_campaign_verdict";
            }
        }
    }
    Ok(report)
}

fn validate_campaign(campaign: &Campaign, failures: &mut Vec<String>) {
    if campaign.schema_version != 1 {
        failures.push("campaign schema_version must be 1".into());
    }
    if campaign.campaign_id.is_empty()
        || campaign.campaign_id.len() > 80
        || !campaign
            .campaign_id
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || byte == b'-' || byte == b'_')
    {
        failures.push("campaign_id must be 1..80 ASCII letters/digits/hyphen/underscore".into());
    }
    if campaign.protocol_sha256 != PROTOCOL_SHA256 {
        failures.push("protocol SHA-256 differs from the frozen prospective protocol".into());
    }
    if campaign.bootstrap_seed != BOOTSTRAP_SEED {
        failures.push("bootstrap seed must be 2026092404".into());
    }
    if campaign.pairs.len() != PAIRS {
        failures.push("campaign requires exactly eight latency pairs".into());
    }
    for (index, pair) in campaign.pairs.iter().enumerate() {
        let expected_id = format!("{:02}", index + 1);
        let expected_order = if index % 2 == 0 {
            Order::SqliteThenTerrabase
        } else {
            Order::TerrabaseThenSqlite
        };
        if pair.id != expected_id || pair.order != expected_order {
            failures.push(format!("pair {} violates preassigned ID/order", pair.id));
        }
    }
    if campaign.diagnostic.id != "D01" || campaign.diagnostic.order != Order::SqliteThenTerrabase {
        failures.push("diagnostic must be D01 in SQLite→TerraBase order".into());
    }
    for (name, hash) in [
        ("program", &campaign.measured.program_sha256),
        (
            "source inventory",
            &campaign.measured.source_inventory.sha256,
        ),
        ("artifact map", &campaign.measured.artifact_map.sha256),
        ("kernel", &campaign.measured.kernel_sha256),
        ("rootfs", &campaign.measured.rootfs_sha256),
    ] {
        if !valid_sha256(hash) {
            failures.push(format!("{name} SHA-256 is malformed"));
        }
    }
}

fn collect_uniqueness(
    pair: &PairInput,
    run_ids: &mut HashSet<String>,
    vm_ids: &mut HashSet<String>,
    volume_ids: &mut HashSet<String>,
    failures: &mut Vec<String>,
) {
    for lane in [&pair.sqlite, &pair.terrabase] {
        if lane.run_id.is_empty() || !run_ids.insert(lane.run_id.clone()) {
            failures.push(format!("missing or duplicate run_id {}", lane.run_id));
        }
        if lane.vm_id.is_empty() || !vm_ids.insert(lane.vm_id.clone()) {
            failures.push(format!("missing or reused VM ID for {}", lane.run_id));
        }
        if lane.volume_id.is_empty() || !volume_ids.insert(lane.volume_id.clone()) {
            failures.push(format!("missing or reused volume ID for {}", lane.run_id));
        }
        if lane.cache_policy.is_empty() || lane.host_cpu_control.is_empty() {
            failures.push(format!(
                "missing host cache/CPU control declaration for {}",
                lane.run_id
            ));
        }
    }
}

fn resolve(base: &Path, path: &Path) -> PathBuf {
    if path.is_absolute() {
        path.to_path_buf()
    } else {
        base.join(path)
    }
}

fn checked_resolve(base: &Path, path: &Path) -> Result<PathBuf> {
    ensure!(
        path.components().count() == 1
            && path
                .components()
                .all(|component| matches!(component, Component::Normal(_))),
        "campaign artifact path must be a flat filename beside its manifest: {}",
        path.display()
    );
    let root = fs::canonicalize(base).context("canonicalize campaign artifact directory")?;
    let resolved = fs::canonicalize(base.join(path))
        .with_context(|| format!("resolve campaign artifact {}", path.display()))?;
    ensure!(
        resolved.starts_with(&root),
        "campaign artifact escapes manifest directory: {}",
        path.display()
    );
    ensure!(
        resolved.is_file(),
        "campaign artifact is not a file: {}",
        path.display()
    );
    Ok(resolved)
}

fn read_bounded(path: &Path, limit: u64) -> Result<Vec<u8>> {
    let file = File::open(path).with_context(|| format!("open {}", path.display()))?;
    ensure!(
        file.metadata()?.len() <= limit,
        "{} exceeds {}-byte limit",
        path.display(),
        limit
    );
    let mut bytes = Vec::new();
    file.take(limit + 1).read_to_end(&mut bytes)?;
    ensure!(
        u64::try_from(bytes.len())? <= limit,
        "{} grew past {}-byte limit",
        path.display(),
        limit
    );
    Ok(bytes)
}

fn read_pinned(base: &Path, input: &PinnedFile, limit: u64) -> Result<Vec<u8>> {
    ensure!(
        valid_sha256(&input.sha256),
        "malformed pinned SHA-256 for {}",
        input.path.display()
    );
    let path = checked_resolve(base, &input.path)?;
    let bytes = read_bounded(&path, limit)?;
    ensure!(
        hex_hash(&bytes) == input.sha256,
        "pinned SHA-256 mismatch for {}",
        path.display()
    );
    Ok(bytes)
}

fn valid_sha256(hash: &str) -> bool {
    hash.len() == 64
        && hash
            .bytes()
            .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
}

fn parse_inventory(bytes: &[u8]) -> Result<BTreeMap<String, String>> {
    let text = std::str::from_utf8(bytes).context("source inventory is not UTF-8")?;
    let mut inventory = BTreeMap::new();
    for line in text.lines() {
        let (hash, path) = line
            .split_once("  ")
            .context("malformed source inventory row")?;
        ensure!(
            valid_sha256(hash) && !path.is_empty(),
            "invalid source inventory row"
        );
        ensure!(
            inventory.insert(path.to_owned(), hash.to_owned()).is_none(),
            "duplicate source inventory path {path}"
        );
    }
    ensure!(
        inventory.len() == 81,
        "expected frozen 81-entry compiled source inventory"
    );
    for path in [
        "ts-benchmark/terra-rust/src/main.rs",
        "ts-benchmark/terra-rust/src/terrabase.rs",
        "ts-benchmark/terra-rust/Cargo.lock",
        "ts-benchmark/bench/terrabase-rust-acceptance-v1.md",
        "terrabase/crates/tb-storage/src/local.rs",
        "terrabase/crates/tb-storage/src/run.rs",
        "terrabase/crates/tb-storage/src/work.rs",
        "terrabase/crates/tb-storage/src/floor.rs",
        "terrabase/crates/tb-storage/src/lib.rs",
        "terrabase/crates/tb-core/src/types.rs",
    ] {
        ensure!(
            inventory.contains_key(path),
            "compiled inventory lacks {path}"
        );
    }
    Ok(inventory)
}

fn inventory_hash<'a>(inventory: &'a BTreeMap<String, String>, path: &str) -> Result<&'a str> {
    Ok(inventory
        .get(path)
        .with_context(|| format!("missing compiled source {path}"))?)
}

fn analyze_pair(
    base: &Path,
    input: &PairInput,
    diagnostic: bool,
    measured: &Measured,
    inventory: &BTreeMap<String, String>,
) -> PairReport {
    let trial = if diagnostic {
        1
    } else {
        input.id.parse().unwrap_or(0)
    };
    let mut sqlite = analyze_lane(
        base,
        &input.sqlite,
        "sqlite-embedded",
        diagnostic,
        trial,
        measured,
        inventory,
    );
    let mut terrabase = analyze_lane(
        base,
        &input.terrabase,
        "terrabase-embedded",
        diagnostic,
        trial,
        measured,
        inventory,
    );
    if let (Some(left), Some(right)) = (&sqlite.fixture_sha256, &terrabase.fixture_sha256) {
        if left != right {
            sqlite
                .errors
                .push("logical fixture SHA-256 differs from paired TerraBase lane".into());
            terrabase
                .errors
                .push("logical fixture SHA-256 differs from paired SQLite lane".into());
        }
    }
    if let (Some(left), Some(right)) = (&sqlite.final_state_sha256, &terrabase.final_state_sha256) {
        if left != right {
            sqlite
                .errors
                .push("final logical state SHA-256 differs from pair".into());
            terrabase
                .errors
                .push("final logical state SHA-256 differs from pair".into());
        }
    }
    if let Some(reason) = check_pair_order(input.order, &sqlite, &terrabase) {
        sqlite.errors.push(reason.into());
        terrabase.errors.push(reason.into());
    }
    if !sqlite.errors.is_empty() {
        sqlite.status = "invalid";
        sqlite.endpoints = None;
    }
    if !terrabase.errors.is_empty() {
        terrabase.status = "invalid";
        terrabase.endpoints = None;
    }
    let complete = sqlite.status == "valid" && terrabase.status == "valid";
    PairReport {
        id: input.id.clone(),
        order: input.order,
        sqlite,
        terrabase,
        complete,
    }
}

fn check_pair_order(
    order: Order,
    sqlite: &LaneReport,
    terrabase: &LaneReport,
) -> Option<&'static str> {
    let (first, second) = match order {
        Order::SqliteThenTerrabase => (sqlite, terrabase),
        Order::TerrabaseThenSqlite => (terrabase, sqlite),
    };
    match (first.finished_unix_ms, second.started_unix_ms) {
        (Some(finished), Some(started)) if finished <= started => None,
        (Some(_), Some(_)) => {
            Some("host run timestamps contradict preassigned pair order or overlap")
        }
        _ => Some("host run timestamps cannot establish preassigned pair order"),
    }
}

fn pair_bounds(pair: &PairReport) -> Option<(u64, u64)> {
    let (first, second) = match pair.order {
        Order::SqliteThenTerrabase => (&pair.sqlite, &pair.terrabase),
        Order::TerrabaseThenSqlite => (&pair.terrabase, &pair.sqlite),
    };
    Some((first.started_unix_ms?, second.finished_unix_ms?))
}

fn check_serial_schedule(pairs: &[PairReport]) -> Vec<String> {
    let mut failures = Vec::new();
    for adjacent in pairs.windows(2) {
        if let (Some((_, previous_end)), Some((next_start, _))) =
            (pair_bounds(&adjacent[0]), pair_bounds(&adjacent[1]))
        {
            if previous_end > next_start {
                failures.push(format!(
                    "pair {} overlaps or follows pair {} despite preassigned serial order",
                    adjacent[0].id, adjacent[1].id
                ));
            }
        }
    }
    failures
}

fn analyze_lane(
    base: &Path,
    input: &LaneInput,
    engine: &'static str,
    diagnostic: bool,
    trial: u32,
    measured: &Measured,
    inventory: &BTreeMap<String, String>,
) -> LaneReport {
    let mut report = LaneReport {
        run_id: input.run_id.clone(),
        vm_id: input.vm_id.clone(),
        volume_id: input.volume_id.clone(),
        cache_policy: input.cache_policy.clone(),
        host_cpu_control: input.host_cpu_control.clone(),
        host_cpu_model: None,
        output_filesystem: None,
        host_run_status: None,
        guest_exit_code: None,
        started_unix_ms: None,
        finished_unix_ms: None,
        firecracker_sha256: None,
        controller_sha256: None,
        guest_agent_sha256: None,
        measured_receipt_status: None,
        measured_receipt_error: None,
        status: "invalid",
        errors: Vec::new(),
        endpoints: None,
        fixture_sha256: None,
        final_state_sha256: None,
        receipt_path: resolve(base, &input.receipt).display().to_string(),
        live_benchmark_repository: None,
        live_subject_repository: None,
        runtime_checkout_observation: None,
        input_files: Vec::new(),
    };
    for (label, path) in [
        ("reset evidence", &input.reset_evidence),
        ("guest command log", &input.guest_command_log),
        ("host run manifest", &input.run_manifest),
        ("guest manifest", &input.guest_manifest),
        ("measured receipt", &input.receipt),
        ("raw samples", &input.samples),
        ("compiled binding", &input.compiled_binding),
    ] {
        if let Err(error) = checked_resolve(base, path) {
            report.errors.push(format!("{label}: {error}"));
        }
    }
    match &input.guest_output_archive {
        Some(path) => {
            if let Err(error) = checked_resolve(base, path) {
                report.errors.push(format!("guest output archive: {error}"));
            }
        }
        None => report
            .errors
            .push("guest output archive path is missing".into()),
    }
    if !report.errors.is_empty() {
        return report;
    }
    match validate_lane(
        base,
        input,
        engine,
        diagnostic,
        trial,
        measured,
        inventory,
        &mut report,
    ) {
        Ok(endpoints) => {
            report.endpoints = endpoints;
            report.status = "valid";
        }
        Err(error) => report.errors.push(error.to_string()),
    }
    report
}

fn read_lane_file(
    base: &Path,
    path: &Path,
    limit: u64,
    report: &mut LaneReport,
) -> Result<Vec<u8>> {
    let path = checked_resolve(base, path)?;
    let bytes = read_bounded(&path, limit)?;
    report.input_files.push(InputHash {
        path: path.display().to_string(),
        sha256: hex_hash(&bytes),
    });
    Ok(bytes)
}

fn require_str<'a>(value: &'a Value, field: &str) -> Result<&'a str> {
    value
        .get(field)
        .and_then(Value::as_str)
        .with_context(|| format!("missing string {field}"))
}

fn require_u64(value: &Value, field: &str) -> Result<u64> {
    value
        .get(field)
        .and_then(Value::as_u64)
        .with_context(|| format!("missing u64 {field}"))
}

fn require_bool(value: &Value, field: &str) -> Result<bool> {
    value
        .get(field)
        .and_then(Value::as_bool)
        .with_context(|| format!("missing bool {field}"))
}

fn require_str_eq(value: &Value, field: &str, expected: &str) -> Result<()> {
    ensure!(
        require_str(value, field)? == expected,
        "{field} differs from expected {expected}"
    );
    Ok(())
}

fn require_u64_eq(value: &Value, field: &str, expected: u64) -> Result<()> {
    ensure!(
        require_u64(value, field)? == expected,
        "{field} differs from expected {expected}"
    );
    Ok(())
}

fn require_bool_eq(value: &Value, field: &str, expected: bool) -> Result<()> {
    ensure!(
        require_bool(value, field)? == expected,
        "{field} differs from expected {expected}"
    );
    Ok(())
}

fn string_array<'a>(value: &'a Value, field: &str) -> Result<Vec<&'a str>> {
    value
        .get(field)
        .and_then(Value::as_array)
        .with_context(|| format!("missing array {field}"))?
        .iter()
        .map(|item| {
            item.as_str()
                .with_context(|| format!("{field} contains a non-string"))
        })
        .collect()
}

fn output_basename(path: &str) -> Result<String> {
    let name = path
        .strip_prefix("/root/fc-harness-output/")
        .context("guest output path is outside /root/fc-harness-output")?;
    ensure!(
        !name.is_empty() && name != "." && name != ".." && !name.contains('/'),
        "guest output path is not a flat filename: {path}"
    );
    Ok(name.to_owned())
}

#[allow(clippy::too_many_lines)] // Command validation follows the recorded argv field order.
fn validate_guest_command(
    binding: &Value,
    receipt: &Value,
    input: &LaneInput,
    engine: &str,
    diagnostic: bool,
    trial: u32,
    measured: &Measured,
) -> Result<GuestOutputNames> {
    let guest_args = string_array(binding, "guest_args")?;
    let host_argv = string_array(binding, "host_command_argv")?;
    let separators: Vec<usize> = host_argv
        .iter()
        .enumerate()
        .filter_map(|(index, arg)| (*arg == "--").then_some(index))
        .collect();
    ensure!(
        separators.len() == 1,
        "host command requires one guest-argument separator"
    );
    let prefix = &host_argv[..separators[0]];
    ensure!(
        &host_argv[separators[0] + 1..] == guest_args.as_slice(),
        "host command guest arguments differ from sidecar guest_args"
    );
    let host_run_ids: Vec<&str> = prefix
        .windows(2)
        .filter_map(|window| (window[0] == "--run-id").then_some(window[1]))
        .collect();
    ensure!(
        host_run_ids == [input.run_id.as_str()],
        "host command run ID differs from campaign lane"
    );
    ensure!(
        prefix.contains(&"--program"),
        "host command does not name a measured program"
    );

    let (selector, argv) = guest_args.split_first().context("guest_args is empty")?;
    ensure!(
        *selector
            == if engine == "sqlite-embedded" {
                "run"
            } else {
                "run-terrabase"
            },
        "guest command selector differs from lane engine"
    );
    let mut values = BTreeMap::new();
    let mut switches = HashSet::new();
    let mut index = 0;
    while index < argv.len() {
        let arg = argv[index];
        if ["--checkpoint-fixture", "--scan-diagnostics"].contains(&arg) {
            ensure!(switches.insert(arg), "duplicate guest switch {arg}");
            index += 1;
        } else {
            ensure!(
                [
                    "--case",
                    "--rows",
                    "--operations",
                    "--body-bytes",
                    "--seed",
                    "--trial",
                    "--database",
                    "--samples",
                    "--receipt",
                    "--guest-manifest",
                    "--benchmark-revision",
                    "--engine-revision",
                ]
                .contains(&arg),
                "unexpected guest argument {arg}"
            );
            let value = argv.get(index + 1).context("guest argument lacks value")?;
            ensure!(
                !value.starts_with("--") && values.insert(arg, *value).is_none(),
                "guest argument {arg} is duplicated or lacks a value"
            );
            index += 2;
        }
    }
    ensure!(
        switches.contains("--checkpoint-fixture"),
        "guest command lacks --checkpoint-fixture"
    );
    ensure!(
        switches.contains("--scan-diagnostics") == diagnostic,
        "guest diagnostic switch differs from pair type"
    );
    for (flag, expected) in [
        ("--case", "deleted-history"),
        ("--rows", "1024"),
        ("--operations", if diagnostic { "1" } else { "1000" }),
        ("--body-bytes", "1"),
        ("--seed", "1"),
        ("--benchmark-revision", measured.benchmark_revision.as_str()),
        ("--engine-revision", measured.engine_revision.as_str()),
    ] {
        ensure!(
            values.get(flag).copied() == Some(expected),
            "guest command {flag} differs from frozen workload"
        );
    }
    let expected_trial = trial.to_string();
    ensure!(
        values.get("--trial").copied() == Some(expected_trial.as_str()),
        "guest command trial differs from pair ID"
    );
    let database = values
        .get("--database")
        .context("guest command lacks database path")?;
    ensure!(
        database.starts_with("/root/fc-bench-data/") && !database.contains("/../"),
        "guest database path is outside fresh data volume"
    );
    require_str_eq(receipt, "database_path", database)?;
    let samples = values
        .get("--samples")
        .context("guest command lacks samples path")?;
    require_str_eq(receipt, "samples_path", samples)?;
    let receipt_path = values
        .get("--receipt")
        .context("guest command lacks receipt path")?;
    let manifest_path = values
        .get("--guest-manifest")
        .context("guest command lacks manifest path")?;
    ensure!(
        *manifest_path == "/root/fc-harness-output/guest-manifest.json",
        "guest manifest argument differs from guest-agent fixed output path"
    );
    let names = GuestOutputNames {
        receipt: output_basename(receipt_path)?,
        samples: output_basename(samples)?,
        manifest: output_basename(manifest_path)?,
    };
    ensure!(
        names.receipt != names.samples
            && names.receipt != names.manifest
            && names.samples != names.manifest,
        "guest output filenames overlap"
    );
    Ok(names)
}

fn archive_member_name(raw: &[u8]) -> Result<Option<String>> {
    if raw == b"./" || raw == b"." {
        return Ok(None);
    }
    let name = raw.strip_prefix(b"./").unwrap_or(raw);
    let name = std::str::from_utf8(name).context("archive member name is not UTF-8")?;
    ensure!(
        !name.is_empty()
            && name != "."
            && name != ".."
            && !name.contains('/')
            && !name.contains('\\')
            && !name.contains('\0'),
        "archive member path is not a flat guest-output file: {name}"
    );
    Ok(Some(name.to_owned()))
}

fn validate_guest_archive(
    compressed: &[u8],
    names: &GuestOutputNames,
    guest_manifest: &[u8],
    receipt: &[u8],
    samples: &[u8],
) -> Result<()> {
    ensure!(
        u64::try_from(compressed.len())? <= ARCHIVE_LIMIT,
        "guest archive exceeds compressed limit"
    );
    let decoder = GzDecoder::new(Cursor::new(compressed));
    let mut limited = decoder.take(ARCHIVE_UNCOMPRESSED_LIMIT + 1);
    let mut expanded = Vec::new();
    limited
        .read_to_end(&mut expanded)
        .context("decode guest gzip archive")?;
    ensure!(
        u64::try_from(expanded.len())? <= ARCHIVE_UNCOMPRESSED_LIMIT,
        "guest archive exceeds total decompressed stream limit"
    );
    ensure!(
        limited.into_inner().into_inner().position() == u64::try_from(compressed.len())?,
        "guest archive has trailing or concatenated gzip data"
    );
    let mut archive = Archive::new(Cursor::new(expanded.as_slice()));
    let mut seen = HashSet::new();
    let mut total = 0_u64;
    let mut records = 0_usize;
    for member in archive.entries()?.raw(true) {
        records += 1;
        ensure!(
            records <= ARCHIVE_RECORD_LIMIT,
            "guest archive has too many physical records"
        );
        let mut member = member.context("read raw guest archive header")?;
        let kind = member.header().entry_type();
        let name = archive_member_name(&member.path_bytes())?;
        if name.is_none() {
            ensure!(
                kind.is_dir() && member.size() == 0,
                "archive root is not an empty directory"
            );
            continue;
        }
        let name = name.expect("checked archive member name");
        ensure!(
            kind.is_file(),
            "archive member {name} is not a regular file"
        );
        ensure!(
            seen.insert(name.clone()),
            "duplicate or aliased archive member {name}"
        );
        let expected = if name == names.manifest {
            Some((guest_manifest, HOST_MANIFEST_LIMIT))
        } else if name == names.receipt {
            Some((receipt, RECEIPT_LIMIT))
        } else if name == names.samples {
            Some((samples, SAMPLE_LIMIT))
        } else {
            None
        };
        let limit = expected.map_or(EVIDENCE_LIMIT, |(_, limit)| limit);
        ensure!(
            member.size() <= limit,
            "archive member {name} exceeds its size limit"
        );
        total = total
            .checked_add(member.size())
            .context("archive size sum overflow")?;
        ensure!(
            total <= ARCHIVE_UNCOMPRESSED_LIMIT,
            "guest archive exceeds total uncompressed size limit"
        );
        if let Some((bytes, _)) = expected {
            let mut archived = Vec::with_capacity(usize::try_from(member.size())?);
            member.read_to_end(&mut archived)?;
            ensure!(
                archived == bytes,
                "archived {name} bytes differ from selected campaign artifact"
            );
        } else {
            std::io::copy(&mut member, &mut std::io::sink())?;
        }
    }
    let position = usize::try_from(archive.into_inner().position())?;
    ensure!(
        position <= expanded.len(),
        "guest archive parser advanced beyond decoded stream"
    );
    ensure!(
        expanded[position..].iter().all(|byte| *byte == 0),
        "guest archive has nonzero data after tar terminator"
    );
    for target in [&names.manifest, &names.receipt, &names.samples] {
        ensure!(
            seen.contains(target),
            "guest archive lacks required member {target}"
        );
    }
    Ok(())
}

fn validate_harness_executable_hashes(host: &Value, guest: &Value) -> Result<()> {
    for field in [
        "firecracker_sha256",
        "controller_sha256",
        "guest_agent_sha256",
    ] {
        let hash = require_str(host, field)?;
        ensure!(valid_sha256(hash), "host {field} is not a SHA-256 digest");
        require_str_eq(guest, field, hash)?;
    }
    Ok(())
}

fn validate_command_log_hash(binding: &Value, command_log: &[u8]) -> Result<()> {
    let hash = require_str(binding, "guest_command_log_sha256")?;
    ensure!(
        valid_sha256(hash) && hex_hash(command_log) == hash,
        "structured guest command log SHA-256 differs from compiled binding"
    );
    if let Some(operator_hash) = binding.get("operator_log_sha256") {
        ensure!(
            operator_hash.as_str().is_some_and(valid_sha256),
            "plain-text host operator log SHA-256 is malformed"
        );
    }
    Ok(())
}

#[allow(clippy::too_many_arguments)] // Each field is a separate frozen evidence boundary.
#[allow(clippy::too_many_lines)] // Keep receipt and archive checks in their evidence order.
fn validate_lane(
    base: &Path,
    input: &LaneInput,
    engine: &'static str,
    diagnostic: bool,
    trial: u32,
    measured: &Measured,
    inventory: &BTreeMap<String, String>,
    report: &mut LaneReport,
) -> Result<Option<Endpoints>> {
    let reset = read_lane_file(base, &input.reset_evidence, EVIDENCE_LIMIT, report)?;
    ensure!(!reset.is_empty(), "reset evidence is empty");
    let command_log = read_lane_file(base, &input.guest_command_log, EVIDENCE_LIMIT, report)?;
    ensure!(!command_log.is_empty(), "guest command log is empty");
    let host_bytes = read_lane_file(base, &input.run_manifest, HOST_MANIFEST_LIMIT, report)?;
    let guest_bytes = read_lane_file(base, &input.guest_manifest, HOST_MANIFEST_LIMIT, report)?;
    let receipt_bytes = read_lane_file(base, &input.receipt, RECEIPT_LIMIT, report)?;
    let sample_bytes = read_lane_file(base, &input.samples, SAMPLE_LIMIT, report)?;
    let binding_bytes = read_lane_file(base, &input.compiled_binding, EVIDENCE_LIMIT, report)?;
    let host: Value = serde_json::from_slice(&host_bytes).context("parse host run manifest")?;
    let guest: Value = serde_json::from_slice(&guest_bytes).context("parse guest manifest")?;
    let receipt: Value =
        serde_json::from_slice(&receipt_bytes).context("parse measured receipt")?;
    let binding: Value =
        serde_json::from_slice(&binding_bytes).context("parse compiled binding")?;
    report.fixture_sha256 = receipt
        .get("fixture_sha256")
        .and_then(Value::as_str)
        .map(str::to_owned);
    report.final_state_sha256 = receipt
        .get("final_state_sha256")
        .and_then(Value::as_str)
        .map(str::to_owned);
    report.live_benchmark_repository = Some(repository_summary(&host["benchmark_repository"]));
    report.live_subject_repository = Some(repository_summary(&host["subject_repository"]));
    report.host_cpu_model = host["host_cpu_model"].as_str().map(str::to_owned);
    report.output_filesystem = host["output_filesystem"].as_str().map(str::to_owned);
    report.host_run_status = host["status"].as_str().map(str::to_owned);
    report.guest_exit_code = host["guest_exit_code"].as_i64();
    report.started_unix_ms = host["started_unix_ms"].as_u64();
    report.finished_unix_ms = host["finished_unix_ms"].as_u64();
    report.firecracker_sha256 = host["firecracker_sha256"].as_str().map(str::to_owned);
    report.controller_sha256 = host["controller_sha256"].as_str().map(str::to_owned);
    report.guest_agent_sha256 = host["guest_agent_sha256"].as_str().map(str::to_owned);
    report.measured_receipt_status = receipt["status"].as_str().map(str::to_owned);
    report.measured_receipt_error = receipt["error"].as_str().map(str::to_owned);
    report.runtime_checkout_observation = binding.get("runtime_checkout_observation").cloned();

    require_u64_eq(&binding, "schema_version", 1)?;
    require_str_eq(&binding, "run_id", &input.run_id)?;
    require_str_eq(
        &binding,
        "measured_program_sha256",
        &measured.program_sha256,
    )?;
    require_str_eq(
        &binding,
        "build_artifact_map_sha256",
        &measured.artifact_map.sha256,
    )?;
    require_str_eq(
        &binding,
        "packet_source_inventory_sha256",
        &measured.source_inventory.sha256,
    )?;
    require_str_eq(&binding, "run_manifest_sha256", &hex_hash(&host_bytes))?;
    require_bool_eq(&binding, "staged_source_files", false)?;
    ensure!(
        binding["runtime_checkout_observation"].is_object(),
        "compiled binding lacks runtime checkout observation"
    );
    let guest_outputs = validate_guest_command(
        &binding, &receipt, input, engine, diagnostic, trial, measured,
    )?;
    let command_record: Value =
        serde_json::from_slice(&command_log).context("parse guest command log")?;
    require_str_eq(&command_record, "run_id", &input.run_id)?;
    ensure!(
        string_array(&command_record, "command")? == string_array(&binding, "guest_args")?,
        "guest command log differs from compiled-binding guest_args"
    );
    ensure!(
        string_array(&command_record, "host_command_argv")?
            == string_array(&binding, "host_command_argv")?,
        "guest command log differs from compiled-binding host command"
    );
    require_u64_eq(&command_record, "exit_code", 0)?;
    validate_command_log_hash(&binding, &command_log)?;
    let archive_hash = require_str(&binding, "guest_output_archive_sha256")?;
    ensure!(
        valid_sha256(archive_hash),
        "malformed guest output archive SHA-256"
    );
    let archive_path = input
        .guest_output_archive
        .as_ref()
        .context("every lane requires the archived guest output")?;
    let archive_path = checked_resolve(base, archive_path)?;
    let archive_bytes = read_bounded(&archive_path, ARCHIVE_LIMIT)?;
    let actual = hex_hash(&archive_bytes);
    ensure!(
        actual == archive_hash,
        "guest output archive SHA-256 differs from compiled binding"
    );
    report.input_files.push(InputHash {
        path: archive_path.display().to_string(),
        sha256: actual,
    });
    validate_guest_archive(
        &archive_bytes,
        &guest_outputs,
        &guest_bytes,
        &receipt_bytes,
        &sample_bytes,
    )?;

    require_u64_eq(&host, "schema_version", 1)?;
    require_str_eq(&host, "run_id", &input.run_id)?;
    require_str_eq(&host, "status", "passed")?;
    ensure!(
        require_u64(&host, "started_unix_ms")? < require_u64(&host, "finished_unix_ms")?,
        "host run timestamps are missing or reversed"
    );
    require_u64_eq(&host, "guest_exit_code", 0)?;
    require_str_eq(&host, "purpose", "guest-program")?;
    require_str_eq(&host, "program_sha256", &measured.program_sha256)?;
    require_str_eq(&host, "kernel_sha256", &measured.kernel_sha256)?;
    require_str_eq(&host, "rootfs_sha256", &measured.rootfs_sha256)?;
    require_str_eq(&host, "firecracker_version", &measured.firecracker_version)?;
    require_u64_eq(&host, "guest_vcpu_count", 4)?;
    require_u64_eq(&host, "guest_mem_size_mib", 8192)?;
    require_bool_eq(&host, "guest_swap_disabled", true)?;
    ensure!(
        host["guest_network"]
            .as_str()
            .is_some_and(|network| network.contains("disabled")),
        "host manifest does not declare disabled network"
    );

    require_str_eq(&guest, "run_id", &input.run_id)?;
    require_str_eq(&guest, "program_sha256", &measured.program_sha256)?;
    require_str_eq(&guest, "kernel_sha256", &measured.kernel_sha256)?;
    require_str_eq(&guest, "rootfs_sha256", &measured.rootfs_sha256)?;
    require_str_eq(&guest, "firecracker_version", &measured.firecracker_version)?;
    validate_harness_executable_hashes(&host, &guest)?;
    require_u64_eq(&guest, "vcpu_count", 4)?;
    require_u64_eq(&guest, "mem_size_mib", 8192)?;
    require_bool_eq(&guest, "swap_disabled", true)?;
    require_u64_eq(&guest, "guest_online_cpus", 4)?;
    require_u64_eq(&guest, "guest_swap_entries", 0)?;
    ensure!(
        guest["guest_network"]
            .as_str()
            .is_some_and(|network| network.contains("disabled")),
        "guest manifest does not declare disabled network"
    );
    for field in [
        "host_cpu_affinity",
        "output_filesystem",
        "benchmark_repository",
        "subject_repository",
    ] {
        ensure!(
            host[field] == guest[field],
            "host/guest manifest {field} differs"
        );
    }
    ensure!(
        host["guest_report"]["online_cpus"] == guest["guest_online_cpus"],
        "guest CPU report differs"
    );
    ensure!(
        host["guest_report"]["swap_entries"] == guest["guest_swap_entries"],
        "guest swap report differs"
    );

    require_u64_eq(&receipt, "schema_version", 1)?;
    require_str_eq(&receipt, "engine", engine)?;
    require_str_eq(&receipt, "case", "deleted-history")?;
    require_str_eq(
        &receipt,
        "status",
        if engine == "sqlite-embedded" {
            "sqlite_baseline_only"
        } else {
            "terrabase_candidate_only"
        },
    )?;
    ensure!(
        receipt["error"].is_null(),
        "measured receipt reports an error"
    );
    require_u64_eq(&receipt, "errors", 0)?;
    require_u64_eq(&receipt, "rows", 1024)?;
    require_u64_eq(&receipt, "body_bytes", 1)?;
    require_u64_eq(&receipt, "seed", 1)?;
    require_u64_eq(&receipt, "trial", u64::from(trial))?;
    require_bool_eq(&receipt, "checkpoint_fixture", true)?;
    require_bool_eq(&receipt, "checkpoint_before_deletes", false)?;
    require_bool_eq(&receipt, "sparse_tail", false)?;
    require_bool_eq(&receipt, "scan_diagnostics_enabled", diagnostic)?;
    require_u64_eq(&receipt, "clients", 1)?;
    require_str_eq(&receipt, "durability", "local-durable")?;
    let operation_count = if diagnostic { 1 } else { 1000 };
    require_u64_eq(&receipt, "operations_requested", operation_count)?;
    require_u64_eq(&receipt, "operations_completed", operation_count)?;
    require_u64_eq(&receipt, "observed_checksum", 0)?;
    require_u64_eq(&receipt, "final_row_count", 0)?;
    require_u64_eq(&receipt, "final_revision_sum", 0)?;
    require_str_eq(&receipt, "final_state_sha256", EMPTY_SHA256)?;
    ensure!(
        valid_sha256(require_str(&receipt, "fixture_sha256")?),
        "malformed fixture SHA-256"
    );
    require_str_eq(&receipt, "binary_sha256", &measured.program_sha256)?;
    require_str_eq(&receipt, "benchmark_revision", &measured.benchmark_revision)?;
    require_str_eq(&receipt, "engine_revision", &measured.engine_revision)?;
    require_str_eq(&receipt, "guest_manifest_sha256", &hex_hash(&guest_bytes))?;
    ensure!(
        receipt["guest_manifest"] == guest,
        "embedded guest manifest differs from guest-manifest.json"
    );
    require_str_eq(&receipt, "matrix_version", super::MATRIX_VERSION)?;
    validate_receipt_sources(&receipt, engine, inventory)?;
    if engine == "sqlite-embedded" {
        require_str_eq(&receipt, "integrity_check", "ok")?;
        ensure!(
            require_str(&receipt, "journal_mode")?.eq_ignore_ascii_case("wal"),
            "SQLite journal mode is not WAL"
        );
        require_u64_eq(&receipt, "synchronous", 2)?;
        require_u64_eq(&receipt, "cache_kib", 65_536)?;
        ensure!(
            receipt["resources"]["database_bytes"]
                .as_u64()
                .is_some_and(|bytes| bytes > 0),
            "SQLite database bytes missing"
        );
        require_u64_eq(&receipt["resources"], "wal_bytes", 0)?;
    } else {
        let fixture = &receipt["physical_fixture_state"];
        ensure!(
            fixture.is_object(),
            "TerraBase physical fixture state missing"
        );
        ensure!(
            require_u64(fixture, "immutable_runs")? > 0,
            "TerraBase checkpoint did not produce an immutable run"
        );
        ensure!(
            require_u64(fixture, "checkpoint_sequence")? > 0,
            "TerraBase checkpoint sequence is zero"
        );
        require_u64_eq(fixture, "mutable_bytes", 0)?;
        ensure!(
            receipt["counters"].is_object(),
            "TerraBase counters missing"
        );
    }
    if diagnostic {
        ensure!(
            receipt["scan_diagnostics"].is_object(),
            "D01 scan diagnostics missing"
        );
        require_str_eq(
            &receipt["scan_diagnostics"],
            "cpu_clock",
            "CLOCK_THREAD_CPUTIME_ID",
        )?;
        if engine == "terrabase-embedded" {
            ensure!(
                receipt["scan_work"].is_object(),
                "D01 TerraBase scan-work delta missing"
            );
        }
    } else {
        ensure!(
            receipt["scan_diagnostics"].is_null(),
            "latency run includes diagnostic probes"
        );
        if engine == "terrabase-embedded" {
            ensure!(
                receipt["scan_work"].is_null(),
                "latency run includes scan-work probes"
            );
        }
    }
    validate_samples(&sample_bytes, &receipt, diagnostic)
}

fn repository_summary(repository: &Value) -> Value {
    serde_json::json!({
        "head": repository["head"],
        "dirty": repository["dirty"],
        "tracked_diff_sha256": repository["tracked_diff_sha256"],
        "untracked_file_count": repository["untracked_files"].as_object().map(serde_json::Map::len),
    })
}

fn validate_receipt_sources(
    receipt: &Value,
    engine: &str,
    inventory: &BTreeMap<String, String>,
) -> Result<()> {
    for (field, path) in [
        (
            "driver_source_sha256",
            "ts-benchmark/terra-rust/src/main.rs",
        ),
        (
            "matrix_sha256",
            "ts-benchmark/bench/terrabase-rust-acceptance-v1.md",
        ),
        (
            "dependency_lock_sha256",
            "ts-benchmark/terra-rust/Cargo.lock",
        ),
    ] {
        require_str_eq(receipt, field, inventory_hash(inventory, path)?)?;
    }
    if engine == "terrabase-embedded" {
        for (field, path) in [
            (
                "adapter_source_sha256",
                "ts-benchmark/terra-rust/src/terrabase.rs",
            ),
            (
                "storage_source_sha256",
                "terrabase/crates/tb-storage/src/local.rs",
            ),
            (
                "storage_run_source_sha256",
                "terrabase/crates/tb-storage/src/run.rs",
            ),
            (
                "storage_work_source_sha256",
                "terrabase/crates/tb-storage/src/work.rs",
            ),
            (
                "storage_floor_source_sha256",
                "terrabase/crates/tb-storage/src/floor.rs",
            ),
            (
                "storage_api_sha256",
                "terrabase/crates/tb-storage/src/lib.rs",
            ),
            ("core_types_sha256", "terrabase/crates/tb-core/src/types.rs"),
        ] {
            require_str_eq(receipt, field, inventory_hash(inventory, path)?)?;
        }
    }
    Ok(())
}

fn validate_samples(bytes: &[u8], receipt: &Value, diagnostic: bool) -> Result<Option<Endpoints>> {
    let text = std::str::from_utf8(bytes).context("samples are not UTF-8")?;
    let expected = if diagnostic { 1 } else { 1000 };
    let mut latencies = Vec::with_capacity(expected);
    for (index, line) in text.lines().enumerate() {
        ensure!(index < expected, "too many ordered samples");
        let sample: Value = serde_json::from_str(line)
            .with_context(|| format!("malformed sample at operation {index}"))?;
        require_u64_eq(&sample, "operation", u64::try_from(index)?)?;
        require_bool_eq(&sample, "success", true)?;
        ensure!(sample["error"].is_null(), "sample {index} reports an error");
        let phase = if diagnostic {
            None
        } else if index == 0 {
            Some("first_after_fixture")
        } else {
            Some("warm_repeat")
        };
        ensure!(
            sample["scan_phase"].as_str() == phase
                && (phase.is_some() || sample["scan_phase"].is_null()),
            "sample {index} has the wrong scan phase"
        );
        let elapsed = require_u64(&sample, "elapsed_ns")?;
        ensure!(elapsed > 0, "sample {index} has zero latency");
        latencies.push(elapsed);
    }
    ensure!(
        latencies.len() == expected,
        "expected {expected} ordered successful samples, found {}",
        latencies.len()
    );
    let mut all = latencies.clone();
    all.sort_unstable();
    check_quantiles(&receipt["latency"], &all, "all-sample latency")?;
    if diagnostic {
        ensure!(
            receipt["warm_scan_samples"].is_null() && receipt["warm_scan_latency"].is_null(),
            "D01 unexpectedly has warm-scan quantiles"
        );
        return Ok(None);
    }
    require_u64_eq(receipt, "warm_scan_samples", WARM_SCANS as u64)?;
    let mut warm = latencies[1..].to_vec();
    warm.sort_unstable();
    check_quantiles(&receipt["warm_scan_latency"], &warm, "warm-scan latency")?;
    Ok(Some(Endpoints {
        first_after_fixture_ns: latencies[0],
        warm_p50_ns: nearest_rank(&warm, 500),
        warm_p95_ns: nearest_rank(&warm, 950),
        warm_p99_ns: nearest_rank(&warm, 990),
        warm_p999_ns: nearest_rank(&warm, 999),
    }))
}

fn nearest_rank(sorted: &[u64], parts_per_thousand: usize) -> u64 {
    sorted[(sorted.len() * parts_per_thousand).div_ceil(1000) - 1]
}

fn check_quantiles(receipt: &Value, sorted: &[u64], label: &str) -> Result<()> {
    ensure!(receipt.is_object(), "{label} quantiles missing");
    for (field, rank) in [
        ("p50_ns", 500),
        ("p95_ns", 950),
        ("p99_ns", 990),
        ("p999_ns", 999),
    ] {
        ensure!(
            require_u64(receipt, field)? == nearest_rank(sorted, rank),
            "{label} {field} differs from ordered raw samples"
        );
    }
    Ok(())
}

fn check_cross_lane_identity(pairs: &[PairReport]) -> Vec<String> {
    let mut failures = Vec::new();
    let Some(first) = pairs.first() else {
        return failures;
    };
    let reference = &first.sqlite;
    for pair in pairs {
        for lane in [&pair.sqlite, &pair.terrabase] {
            for (label, value, expected) in [
                (
                    "fixture SHA-256",
                    &lane.fixture_sha256,
                    &reference.fixture_sha256,
                ),
                (
                    "final-state SHA-256",
                    &lane.final_state_sha256,
                    &reference.final_state_sha256,
                ),
                (
                    "host CPU model",
                    &lane.host_cpu_model,
                    &reference.host_cpu_model,
                ),
                (
                    "output filesystem",
                    &lane.output_filesystem,
                    &reference.output_filesystem,
                ),
                (
                    "Firecracker executable SHA-256",
                    &lane.firecracker_sha256,
                    &reference.firecracker_sha256,
                ),
                (
                    "controller SHA-256",
                    &lane.controller_sha256,
                    &reference.controller_sha256,
                ),
                (
                    "guest agent SHA-256",
                    &lane.guest_agent_sha256,
                    &reference.guest_agent_sha256,
                ),
            ] {
                if value.is_some() && expected.is_some() && value != expected {
                    failures.push(format!(
                        "pair {} run {} {label} differs across latency cohort",
                        pair.id, lane.run_id
                    ));
                }
            }
            if lane.cache_policy != reference.cache_policy {
                failures.push(format!(
                    "pair {} run {} cache policy differs across latency cohort",
                    pair.id, lane.run_id
                ));
            }
            if lane.host_cpu_control != reference.host_cpu_control {
                failures.push(format!(
                    "pair {} run {} host CPU control differs across latency cohort",
                    pair.id, lane.run_id
                ));
            }
        }
    }
    failures
}

#[allow(clippy::cast_precision_loss)] // Ratios and confidence intervals are intentionally reported as f64.
fn analyze_metrics(pairs: &[PairReport], seed: u64) -> Result<Vec<MetricReport>> {
    ensure!(
        pairs.len() == PAIRS && pairs.iter().all(|pair| pair.complete),
        "all eight complete latency pairs are required for inference"
    );
    let names = [
        "first_after_fixture_ns",
        "warm_p50_ns",
        "warm_p95_ns",
        "warm_p99_ns",
        "warm_p999_ns",
    ];
    let mut rng = ChaCha20Rng::seed_from_u64(seed);
    let mut metrics = Vec::with_capacity(names.len());
    for (metric_index, name) in names.into_iter().enumerate() {
        let values: Vec<[u64; 2]> = pairs
            .iter()
            .map(|pair| {
                let sqlite = pair
                    .sqlite
                    .endpoints
                    .expect("complete pair has SQLite endpoints");
                let terrabase = pair
                    .terrabase
                    .endpoints
                    .expect("complete pair has TerraBase endpoints");
                [
                    sqlite.values()[metric_index],
                    terrabase.values()[metric_index],
                ]
            })
            .collect();
        let sqlite_sum: u128 = values.iter().map(|row| u128::from(row[0])).sum();
        let terrabase_sum: u128 = values.iter().map(|row| u128::from(row[1])).sum();
        ensure!(sqlite_sum > 0, "zero SQLite denominator for {name}");
        let point_ratio = terrabase_sum as f64 / sqlite_sum as f64;
        ensure!(point_ratio.is_finite(), "nonfinite point ratio for {name}");
        let mut replicates = Vec::with_capacity(REPLICATES);
        let mut indices = Sha256::new();
        for _ in 0..REPLICATES {
            let mut sqlite_draw = 0_u128;
            let mut terrabase_draw = 0_u128;
            for _ in 0..PAIRS {
                let index: usize = rng.random_range(0..PAIRS);
                indices.update([u8::try_from(index)?]);
                sqlite_draw += u128::from(values[index][0]);
                terrabase_draw += u128::from(values[index][1]);
            }
            ensure!(sqlite_draw > 0, "zero bootstrap denominator for {name}");
            let ratio = terrabase_draw as f64 / sqlite_draw as f64;
            ensure!(ratio.is_finite(), "nonfinite bootstrap ratio for {name}");
            replicates.push(ratio);
        }
        replicates.sort_by(f64::total_cmp);
        let lower = replicates[249];
        let upper = replicates[9_749];
        let classification = if upper < 0.95 {
            "provisional_win"
        } else if lower > 1.05 {
            "provisional_loss"
        } else {
            "inconclusive"
        };
        metrics.push(MetricReport {
            metric: name,
            estimator: "sum(T_i)/sum(S_i)",
            sqlite_sum_ns: sqlite_sum,
            terrabase_sum_ns: terrabase_sum,
            point_ratio,
            ci95_lower: lower,
            ci95_upper: upper,
            classification,
            bootstrap_seed: seed,
            prng: "chacha20 0.10.2 ChaCha20Rng; rand 0.10.3 unbiased random_range; one stream, metrics in report order",
            resample_index_encoding: "one unsigned byte per index (0..7), replicate-major draw order, SHA-256",
            resample_index_sha256: format!("{:x}", indices.finalize()),
            pair_values_ns: values,
        });
    }
    Ok(metrics)
}

fn render_markdown(report: &Report) -> String {
    let mut out = format!(
        "# P04 focused paired repeat — {}\n\nStatus: **{}**. Acceptance qualified: **{}**.\n\nProtocol SHA-256: `{}`. Estimator: `{}`; {} whole-pair bootstrap draws per metric; seed `{}`.\n\n",
        md(&report.campaign_id),
        report.status,
        report.acceptance_qualified,
        report.analyzer.protocol_sha256,
        report.estimator,
        report.bootstrap_replicates_per_metric,
        report.bootstrap_seed,
    );
    out.push_str("| Pair | Order | SQLite first / p50 / p95 / p99 / p99.9 (ns) | TerraBase first / p50 / p95 / p99 / p99.9 (ns) | Status | Receipts |\n| --- | --- | --- | --- | --- | --- |\n");
    for pair in &report.latency_pairs {
        let order = match pair.order {
            Order::SqliteThenTerrabase => "SQLite→TerraBase",
            Order::TerrabaseThenSqlite => "TerraBase→SQLite",
        };
        let _ = writeln!(
            out,
            "| {} | {} | {} | {} | {} | {}; {} |",
            md(&pair.id),
            order,
            endpoint_cell(pair.sqlite.endpoints),
            endpoint_cell(pair.terrabase.endpoints),
            if pair.complete { "complete" } else { "failed" },
            receipt_link(&pair.sqlite),
            receipt_link(&pair.terrabase)
        );
    }
    out.push_str("\n| Metric | Ratio T/S | 95% CI | Provisional classification | Resample-index SHA-256 |\n| --- | ---: | --- | --- | --- |\n");
    for metric in &report.metrics {
        let _ = writeln!(
            out,
            "| {} | {:.6} | [{:.6}, {:.6}] | {} | `{}` |",
            metric.metric,
            metric.point_ratio,
            metric.ci95_lower,
            metric.ci95_upper,
            metric.classification,
            metric.resample_index_sha256
        );
    }
    if report.metrics.is_empty() {
        out.push_str("| All endpoints | — | suppressed | incomplete or non-comparable latency cohort | — |\n");
    }
    out.push_str("\nD01 is a separate diagnostic pair and contributes no latency samples to the intervals.\n\n");
    if let Some(pair) = &report.diagnostic_pair {
        let _ = write!(
            out,
            "D01: **{}**; receipts: {}, {}.\n\n",
            if pair.complete { "complete" } else { "failed" },
            receipt_link(&pair.sqlite),
            receipt_link(&pair.terrabase)
        );
    }
    out.push_str("## Failures and qualification limits\n\n");
    for failure in &report.failures {
        let _ = writeln!(out, "- {}", md(failure));
    }
    for pair in report
        .latency_pairs
        .iter()
        .chain(report.diagnostic_pair.iter())
    {
        for lane in [&pair.sqlite, &pair.terrabase] {
            for error in &lane.errors {
                let _ = writeln!(out, "- Run `{}`: {}", md(&lane.run_id), md(error));
            }
            if lane.status != "valid" {
                let _ = writeln!(
                    out,
                    "- Run `{}` observed host status `{}`, guest exit `{:?}`, measured receipt status `{}`: {}",
                    md(&lane.run_id),
                    md(lane.host_run_status.as_deref().unwrap_or("unavailable")),
                    lane.guest_exit_code,
                    md(lane
                        .measured_receipt_status
                        .as_deref()
                        .unwrap_or("unavailable")),
                    md(lane
                        .measured_receipt_error
                        .as_deref()
                        .unwrap_or("no receipt error text"))
                );
            }
        }
    }
    for limit in &report.qualification_limits {
        let _ = writeln!(out, "- {}", md(limit));
    }
    out.push_str("\nMachine-readable hashes, individual paired values, analyzer identity, and input paths are in the companion JSON report.\n");
    out
}

fn endpoint_cell(endpoints: Option<Endpoints>) -> String {
    endpoints.map_or_else(
        || "—".into(),
        |endpoints| {
            endpoints
                .values()
                .map(|value| value.to_string())
                .join(" / ")
        },
    )
}

fn receipt_link(lane: &LaneReport) -> String {
    format!(
        "[{}](<{}>)",
        md(&lane.run_id),
        lane.receipt_path.replace('>', "%3E").replace('\n', "%0A")
    )
}

fn md(value: &str) -> String {
    value
        .replace('\\', "\\\\")
        .replace('|', "\\|")
        .replace('\n', " ")
        .replace('`', "\\`")
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    fn write_archive(entries: &[(&str, &[u8])]) -> Vec<u8> {
        let encoder = flate2::write::GzEncoder::new(Vec::new(), flate2::Compression::default());
        let mut builder = tar::Builder::new(encoder);
        for (name, bytes) in entries {
            let mut header = tar::Header::new_gnu();
            header.set_entry_type(tar::EntryType::Regular);
            header.set_mode(0o644);
            header.set_size(u64::try_from(bytes.len()).expect("small test file"));
            header.set_cksum();
            builder
                .append_data(&mut header, *name, *bytes)
                .expect("append archive member");
        }
        builder
            .into_inner()
            .expect("finish tar")
            .finish()
            .expect("finish gzip")
    }

    fn gzip(bytes: &[u8]) -> Vec<u8> {
        let mut encoder = flate2::write::GzEncoder::new(Vec::new(), flate2::Compression::default());
        encoder.write_all(bytes).expect("write gzip payload");
        encoder.finish().expect("finish gzip payload")
    }

    fn tar_bytes(compressed: &[u8]) -> Vec<u8> {
        let mut tar = Vec::new();
        flate2::read::GzDecoder::new(compressed)
            .read_to_end(&mut tar)
            .expect("decompress test tar");
        tar
    }

    #[test]
    fn authentic_guest_archive_rejects_altered_receipt_and_samples() {
        let names = GuestOutputNames {
            manifest: "guest-manifest.json".into(),
            receipt: "receipt.json".into(),
            samples: "samples.jsonl".into(),
        };
        let archive = write_archive(&[
            ("./guest-manifest.json", b"manifest"),
            ("./receipt.json", b"original receipt"),
            ("./samples.jsonl", b"original samples"),
        ]);
        assert!(
            validate_guest_archive(
                &archive,
                &names,
                b"manifest",
                b"original receipt",
                b"original samples"
            )
            .is_ok()
        );
        assert!(
            validate_guest_archive(
                &archive,
                &names,
                b"manifest",
                b"altered receipt",
                b"altered samples"
            )
            .is_err()
        );
    }

    #[test]
    fn archive_rejects_duplicate_aliases_and_oversized_members() {
        let names = GuestOutputNames {
            manifest: "guest-manifest.json".into(),
            receipt: "receipt.json".into(),
            samples: "samples.jsonl".into(),
        };
        let duplicate = write_archive(&[
            ("./guest-manifest.json", b"manifest"),
            ("./receipt.json", b"receipt"),
            ("receipt.json", b"receipt"),
            ("./samples.jsonl", b"samples"),
        ]);
        assert!(
            validate_guest_archive(&duplicate, &names, b"manifest", b"receipt", b"samples")
                .is_err()
        );
        let oversized = vec![0_u8; usize::try_from(EVIDENCE_LIMIT + 1).expect("small limit")];
        let large = write_archive(&[
            ("./guest-manifest.json", b"manifest"),
            ("./receipt.json", b"receipt"),
            ("./samples.jsonl", b"samples"),
            ("./extra.log", &oversized),
        ]);
        assert!(
            validate_guest_archive(&large, &names, b"manifest", b"receipt", b"samples").is_err()
        );
        assert!(archive_member_name(b"././receipt.json").is_err());
        assert!(archive_member_name(b"../receipt.json").is_err());
    }

    #[test]
    fn archive_rejects_excess_records_expansion_and_hidden_postlude() {
        let names = GuestOutputNames {
            manifest: "guest-manifest.json".into(),
            receipt: "receipt.json".into(),
            samples: "samples.jsonl".into(),
        };
        let mut entries = vec![
            ("guest-manifest.json".to_owned(), b"manifest".as_slice()),
            ("receipt.json".to_owned(), b"receipt".as_slice()),
            ("samples.jsonl".to_owned(), b"samples".as_slice()),
        ];
        for index in 0..ARCHIVE_RECORD_LIMIT {
            entries.push((format!("extra-{index}.log"), b"x".as_slice()));
        }
        let borrowed: Vec<(&str, &[u8])> = entries
            .iter()
            .map(|(name, bytes)| (name.as_str(), *bytes))
            .collect();
        let many = write_archive(&borrowed);
        assert!(
            validate_guest_archive(&many, &names, b"manifest", b"receipt", b"samples").is_err()
        );
        let normal = write_archive(&borrowed[..3]);
        let mut expanded = tar_bytes(&normal);
        expanded.resize(
            usize::try_from(ARCHIVE_UNCOMPRESSED_LIMIT + 1).expect("small limit"),
            0,
        );
        let bomb = gzip(&expanded);
        assert!(
            validate_guest_archive(&bomb, &names, b"manifest", b"receipt", b"samples").is_err()
        );
        let mut hidden = tar_bytes(&normal);
        hidden.extend_from_slice(b"hidden after tar end");
        assert!(
            validate_guest_archive(&gzip(&hidden), &names, b"manifest", b"receipt", b"samples")
                .is_err()
        );
        let mut concatenated = normal;
        concatenated.extend(write_archive(&[]));
        assert!(
            validate_guest_archive(&concatenated, &names, b"manifest", b"receipt", b"samples")
                .is_err()
        );
    }

    #[test]
    #[allow(clippy::too_many_lines)] // One fixture exercises the valid and two invalid bindings.
    fn command_binding_rejects_changed_workload_and_wrong_guest_manifest_path() {
        let args = vec![
            "run",
            "--case",
            "deleted-history",
            "--rows",
            "1024",
            "--operations",
            "1000",
            "--body-bytes",
            "1",
            "--seed",
            "1",
            "--trial",
            "1",
            "--checkpoint-fixture",
            "--database",
            "/root/fc-bench-data/test.sqlite",
            "--samples",
            "/root/fc-harness-output/samples.jsonl",
            "--receipt",
            "/root/fc-harness-output/receipt.json",
            "--guest-manifest",
            "/root/fc-harness-output/guest-manifest.json",
            "--benchmark-revision",
            "bench",
            "--engine-revision",
            "engine",
        ];
        let input = LaneInput {
            run_id: "run-1".into(),
            vm_id: "vm".into(),
            volume_id: "volume".into(),
            cache_policy: "cache".into(),
            host_cpu_control: "cpu".into(),
            reset_evidence: PathBuf::new(),
            guest_command_log: PathBuf::new(),
            run_manifest: PathBuf::new(),
            guest_manifest: PathBuf::new(),
            receipt: PathBuf::new(),
            samples: PathBuf::new(),
            compiled_binding: PathBuf::new(),
            guest_output_archive: None,
        };
        let measured = Measured {
            program_sha256: String::new(),
            source_inventory: PinnedFile {
                path: PathBuf::new(),
                sha256: String::new(),
            },
            artifact_map: PinnedFile {
                path: PathBuf::new(),
                sha256: String::new(),
            },
            benchmark_revision: "bench".into(),
            engine_revision: "engine".into(),
            kernel_sha256: String::new(),
            rootfs_sha256: String::new(),
            firecracker_version: String::new(),
        };
        let receipt = json!({
            "database_path": "/root/fc-bench-data/test.sqlite",
            "samples_path": "/root/fc-harness-output/samples.jsonl",
        });
        let mut host = vec![
            "fc-harness",
            "run",
            "--run-id",
            "run-1",
            "--program",
            "binary",
            "--",
        ];
        host.extend(args.iter().copied());
        let binding = json!({"guest_args": args, "host_command_argv": host});
        assert!(
            validate_guest_command(
                &binding,
                &receipt,
                &input,
                "sqlite-embedded",
                false,
                1,
                &measured
            )
            .is_ok()
        );
        let mut wrong_rows = binding.clone();
        wrong_rows["guest_args"][4] = json!("50000");
        wrong_rows["host_command_argv"][11] = json!("50000");
        assert!(
            validate_guest_command(
                &wrong_rows,
                &receipt,
                &input,
                "sqlite-embedded",
                false,
                1,
                &measured
            )
            .is_err()
        );
        let mut wrong_manifest = binding;
        wrong_manifest["guest_args"][21] =
            json!("/root/fc-harness-output/wrong-guest-manifest.json");
        wrong_manifest["host_command_argv"][28] =
            json!("/root/fc-harness-output/wrong-guest-manifest.json");
        assert!(
            validate_guest_command(
                &wrong_manifest,
                &receipt,
                &input,
                "sqlite-embedded",
                false,
                1,
                &measured
            )
            .is_err()
        );
    }

    #[test]
    fn structured_command_hash_does_not_repurpose_plain_text_operator_hash() {
        let structured = b"{\"run_id\":\"run-1\",\"command\":[\"run\"]}";
        let plain_text = b"host operator log with a different format\n";
        let mut binding = json!({
            "guest_command_log_sha256": hex_hash(structured),
            "operator_log_sha256": hex_hash(plain_text),
        });
        assert!(validate_command_log_hash(&binding, structured).is_ok());
        binding["guest_command_log_sha256"] = binding["operator_log_sha256"].clone();
        assert!(validate_command_log_hash(&binding, structured).is_err());
    }

    fn sample_fixture(diagnostic: bool) -> (Vec<u8>, Value) {
        let count = if diagnostic { 1 } else { 1000 };
        let mut raw = Vec::new();
        for operation in 0..count {
            let phase = if diagnostic {
                Value::Null
            } else if operation == 0 {
                json!("first_after_fixture")
            } else {
                json!("warm_repeat")
            };
            let sample = json!({
                "operation": operation,
                "elapsed_ns": operation + 1,
                "success": true,
                "error": null,
                "scan_phase": phase,
            });
            serde_json::to_writer(&mut raw, &sample).expect("serialize sample");
            raw.push(b'\n');
        }
        let receipt = if diagnostic {
            json!({"latency": {"p50_ns": 1, "p95_ns": 1, "p99_ns": 1, "p999_ns": 1}})
        } else {
            json!({
                "latency": {"p50_ns": 500, "p95_ns": 950, "p99_ns": 990, "p999_ns": 999},
                "warm_scan_samples": 999,
                "warm_scan_latency": {"p50_ns": 501, "p95_ns": 951, "p99_ns": 991, "p999_ns": 1000},
            })
        };
        (raw, receipt)
    }

    #[test]
    fn validates_ordered_first_and_warm_samples_against_receipt() {
        let (raw, receipt) = sample_fixture(false);
        let endpoints = validate_samples(&raw, &receipt, false)
            .expect("valid samples")
            .expect("latency endpoints");
        assert_eq!(endpoints.values(), [1, 501, 951, 991, 1000]);
        let (diagnostic, diagnostic_receipt) = sample_fixture(true);
        assert!(
            validate_samples(&diagnostic, &diagnostic_receipt, true)
                .expect("valid D01")
                .is_none()
        );
    }

    #[test]
    fn rejects_sample_order_phase_and_quantile_mismatch() {
        let (raw, receipt) = sample_fixture(false);
        let changed = String::from_utf8(raw.clone()).expect("UTF-8").replacen(
            "\"operation\":0",
            "\"operation\":1",
            1,
        );
        assert!(validate_samples(changed.as_bytes(), &receipt, false).is_err());
        let changed = String::from_utf8(raw).expect("UTF-8").replacen(
            "first_after_fixture",
            "warm_repeat",
            1,
        );
        assert!(validate_samples(changed.as_bytes(), &receipt, false).is_err());
        let (raw, mut receipt) = sample_fixture(false);
        receipt["warm_scan_latency"]["p999_ns"] = json!(999);
        assert!(validate_samples(&raw, &receipt, false).is_err());
    }

    fn lane(run_id: String, value: u64) -> LaneReport {
        LaneReport {
            run_id,
            vm_id: "vm".into(),
            volume_id: "volume".into(),
            cache_policy: "unchanged".into(),
            host_cpu_control: "pinned".into(),
            host_cpu_model: Some("cpu".into()),
            output_filesystem: Some("ext4".into()),
            host_run_status: Some("passed".into()),
            guest_exit_code: Some(0),
            started_unix_ms: Some(1),
            finished_unix_ms: Some(2),
            firecracker_sha256: Some("vmm".into()),
            controller_sha256: Some("controller".into()),
            guest_agent_sha256: Some("agent".into()),
            measured_receipt_status: Some("passed".into()),
            measured_receipt_error: None,
            status: "valid",
            errors: Vec::new(),
            endpoints: Some(Endpoints {
                first_after_fixture_ns: value,
                warm_p50_ns: value,
                warm_p95_ns: value,
                warm_p99_ns: value,
                warm_p999_ns: value,
            }),
            fixture_sha256: Some("fixture".into()),
            final_state_sha256: Some("final".into()),
            receipt_path: "receipt.json".into(),
            live_benchmark_repository: None,
            live_subject_repository: None,
            runtime_checkout_observation: None,
            input_files: Vec::new(),
        }
    }

    fn pairs() -> Vec<PairReport> {
        (0..PAIRS)
            .map(|index| {
                let order = if index % 2 == 0 {
                    Order::SqliteThenTerrabase
                } else {
                    Order::TerrabaseThenSqlite
                };
                let start = u64::try_from(index).expect("small index") * 4 + 1;
                let mut sqlite = lane(format!("s{index}"), if index == 0 { 10 } else { 100 });
                let mut terrabase = lane(format!("t{index}"), if index == 0 { 9 } else { 80 });
                let (first, second) = if order == Order::SqliteThenTerrabase {
                    (&mut sqlite, &mut terrabase)
                } else {
                    (&mut terrabase, &mut sqlite)
                };
                first.started_unix_ms = Some(start);
                first.finished_unix_ms = Some(start + 1);
                second.started_unix_ms = Some(start + 2);
                second.finished_unix_ms = Some(start + 3);
                PairReport {
                    id: format!("{:02}", index + 1),
                    order,
                    sqlite,
                    terrabase,
                    complete: true,
                }
            })
            .collect()
    }

    #[test]
    #[allow(clippy::float_cmp)] // Identical seeded draws must produce bit-identical results.
    fn bootstrap_uses_pair_blocks_ratio_of_sums_and_one_reproducible_stream() {
        let pairs = pairs();
        let first = analyze_metrics(&pairs, BOOTSTRAP_SEED).expect("metrics");
        let second = analyze_metrics(&pairs, BOOTSTRAP_SEED).expect("repeat metrics");
        assert_eq!(first.len(), 5);
        for (left, right) in first.iter().zip(&second) {
            assert_eq!(left.sqlite_sum_ns, 710);
            assert_eq!(left.terrabase_sum_ns, 569);
            assert_eq!(left.point_ratio, 569.0 / 710.0);
            assert_eq!(left.resample_index_sha256, right.resample_index_sha256);
            assert_eq!(left.ci95_lower, right.ci95_lower);
            assert_eq!(left.ci95_upper, right.ci95_upper);
            assert_eq!(left.classification, "provisional_win");
        }
        assert_ne!(
            first[0].resample_index_sha256,
            first[1].resample_index_sha256
        );
    }

    #[test]
    fn mismatched_fixture_or_failed_pair_suppresses_inference() {
        let mut pairs = pairs();
        pairs[1].terrabase.fixture_sha256 = Some("different".into());
        assert!(!check_cross_lane_identity(&pairs).is_empty());
        pairs[1].complete = false;
        assert!(analyze_metrics(&pairs, BOOTSTRAP_SEED).is_err());
    }

    #[test]
    fn rejects_reordered_or_overlapping_pair_schedule_and_binary_mismatch() {
        let mut pairs = pairs();
        assert!(check_serial_schedule(&pairs).is_empty());
        assert!(check_pair_order(pairs[1].order, &pairs[1].sqlite, &pairs[1].terrabase).is_none());
        pairs[1].terrabase.started_unix_ms = Some(1);
        assert!(!check_serial_schedule(&pairs).is_empty());
        pairs[1].terrabase.started_unix_ms = Some(5);
        pairs[1].sqlite.started_unix_ms = Some(9);
        pairs[1].terrabase.finished_unix_ms = Some(12);
        assert!(check_pair_order(pairs[1].order, &pairs[1].sqlite, &pairs[1].terrabase).is_some());
        pairs[1].terrabase.firecracker_sha256 = Some("other-vmm".into());
        assert!(!check_cross_lane_identity(&pairs).is_empty());
    }

    #[test]
    fn rejects_host_guest_executable_hash_mismatch() {
        let hash = "a".repeat(64);
        let other = "b".repeat(64);
        let host = json!({
            "firecracker_sha256": hash,
            "controller_sha256": hash,
            "guest_agent_sha256": hash,
        });
        let mut guest = host.clone();
        assert!(validate_harness_executable_hashes(&host, &guest).is_ok());
        guest["firecracker_sha256"] = json!(other);
        assert!(validate_harness_executable_hashes(&host, &guest).is_err());
    }

    #[test]
    fn rejects_nonflat_campaign_artifact_paths() {
        for path in [
            "../receipt.json",
            "runs/receipt.json",
            "/tmp/receipt.json",
            ".",
        ] {
            assert!(checked_resolve(Path::new("."), Path::new(path)).is_err());
        }
    }
}
