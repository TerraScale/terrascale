//! Classify one registered throughput case against the v2 SQLite floor.
//!
//! The rule is `bench/terrabase-rust-acceptance-v2.md`: a case meets the floor
//! when the lower bound of the paired 95% bootstrap interval for the
//! TerraBase/SQLite throughput ratio is at least 0.40, is below the floor when
//! the upper bound is below 0.40, and is otherwise inconclusive. Ratios above
//! 1.0 are not capped.

use std::fs::OpenOptions;
use std::io::Write as _;
use std::path::PathBuf;

use anyhow::{Context, Result, ensure};
use chacha20::ChaCha20Rng;
use clap::Parser;
use rand::{RngExt, SeedableRng};
use serde::{Deserialize, Serialize};

const FLOOR: f64 = 0.40;
const MIN_PAIRS: usize = 7;
const REPLICATES: usize = 10_000;
const LOWER_INDEX: usize = 249;
const UPPER_INDEX: usize = 9_749;

#[derive(Parser)]
#[command(about = "Classify a paired throughput case against the 0.40x SQLite floor")]
struct Args {
    /// Paired-trial input JSON (schema in bench/terrabase-rust-acceptance-v2.md).
    #[arg(long)]
    input: PathBuf,
    /// Fresh path for the JSON verdict report.
    #[arg(long)]
    output: PathBuf,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct Input {
    schema_version: u32,
    matrix_version: String,
    case_id: String,
    bootstrap_seed: u64,
    pairs: Vec<Pair>,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct Pair {
    id: String,
    sqlite: Lane,
    terrabase: Lane,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct Lane {
    receipt: String,
    operations_completed: u64,
    elapsed_ns: u64,
}

impl Lane {
    #[allow(clippy::cast_precision_loss)] // Throughput is reported as f64.
    fn throughput(&self) -> f64 {
        self.operations_completed as f64 * 1e9 / self.elapsed_ns as f64
    }
}

#[derive(Serialize)]
struct Report<'a> {
    schema_version: u32,
    matrix_version: &'a str,
    case_id: &'a str,
    rule: &'static str,
    floor: f64,
    estimator: &'static str,
    pairs: usize,
    bootstrap_seed: u64,
    bootstrap_replicates: usize,
    prng: &'static str,
    sqlite_mean_ops_per_second: f64,
    terrabase_mean_ops_per_second: f64,
    point_ratio: f64,
    ci95_lower: f64,
    ci95_upper: f64,
    status: &'static str,
    reason: String,
    pair_throughput_ops_per_second: Vec<[f64; 2]>,
    receipts: Vec<[&'a str; 2]>,
}

fn classify(lower: f64, upper: f64) -> (&'static str, &'static str) {
    if lower >= FLOOR {
        ("MEETS_FLOOR", "CI95 lower bound is at least 0.40")
    } else if upper < FLOOR {
        ("BELOW_FLOOR", "CI95 upper bound is below 0.40")
    } else {
        ("INCONCLUSIVE", "CI95 spans 0.40")
    }
}

fn validate(input: &Input) -> Result<()> {
    ensure!(input.schema_version == 1, "schema_version must be 1");
    ensure!(
        input.matrix_version == "v2",
        "only matrix v2 registers the throughput floor"
    );
    ensure!(
        input.case_id.ends_with("/throughput"),
        "the floor applies only to throughput cases; latency, startup and recovery are reported separately"
    );
    ensure!(
        input.pairs.len() >= MIN_PAIRS,
        "at least {MIN_PAIRS} paired trials are required"
    );
    let mut ids = std::collections::HashSet::new();
    for pair in &input.pairs {
        ensure!(ids.insert(pair.id.as_str()), "duplicate pair {}", pair.id);
        for (name, lane) in [("sqlite", &pair.sqlite), ("terrabase", &pair.terrabase)] {
            ensure!(
                !lane.receipt.is_empty(),
                "pair {} {name} lane has no receipt",
                pair.id
            );
            ensure!(
                lane.operations_completed > 0 && lane.elapsed_ns > 0,
                "pair {} {name} lane has no completed work or elapsed time",
                pair.id
            );
        }
        ensure!(
            pair.sqlite.operations_completed == pair.terrabase.operations_completed,
            "pair {} lanes completed different logical work",
            pair.id
        );
    }
    Ok(())
}

fn main() -> Result<()> {
    let args = Args::parse();
    let bytes =
        std::fs::read(&args.input).with_context(|| format!("read {}", args.input.display()))?;
    let input: Input = serde_json::from_slice(&bytes).context("parse paired-trial input")?;
    validate(&input)?;

    let values: Vec<[f64; 2]> = input
        .pairs
        .iter()
        .map(|pair| [pair.sqlite.throughput(), pair.terrabase.throughput()])
        .collect();
    let ratio = |indices: &mut dyn Iterator<Item = usize>| {
        let (sqlite, terrabase) = indices.fold((0.0, 0.0), |(s, t), index| {
            (s + values[index][0], t + values[index][1])
        });
        terrabase / sqlite
    };
    let point_ratio = ratio(&mut (0..values.len()));
    ensure!(point_ratio.is_finite(), "nonfinite point ratio");

    let mut rng = ChaCha20Rng::seed_from_u64(input.bootstrap_seed);
    let mut replicates = Vec::with_capacity(REPLICATES);
    for _ in 0..REPLICATES {
        let draws: Vec<usize> = (0..values.len())
            .map(|_| rng.random_range(0..values.len()))
            .collect();
        let replicate = ratio(&mut draws.into_iter());
        ensure!(replicate.is_finite(), "nonfinite bootstrap ratio");
        replicates.push(replicate);
    }
    replicates.sort_by(f64::total_cmp);
    let (lower, upper) = (replicates[LOWER_INDEX], replicates[UPPER_INDEX]);
    let (status, reason) = classify(lower, upper);

    #[allow(clippy::cast_precision_loss)] // Pair counts are small.
    let mean = |lane: usize| values.iter().map(|row| row[lane]).sum::<f64>() / values.len() as f64;
    let report = Report {
        schema_version: 1,
        matrix_version: &input.matrix_version,
        case_id: &input.case_id,
        rule: "MEETS_FLOOR if CI95 lower >= 0.40; BELOW_FLOOR if CI95 upper < 0.40; else INCONCLUSIVE",
        floor: FLOOR,
        estimator: "mean(T_i)/mean(S_i) of per-pair throughput, whole-pair percentile bootstrap",
        pairs: values.len(),
        bootstrap_seed: input.bootstrap_seed,
        bootstrap_replicates: REPLICATES,
        prng: "chacha20 0.10.2 ChaCha20Rng; rand 0.10.3 unbiased random_range",
        sqlite_mean_ops_per_second: mean(0),
        terrabase_mean_ops_per_second: mean(1),
        point_ratio,
        ci95_lower: lower,
        ci95_upper: upper,
        status,
        reason: reason.to_owned(),
        pair_throughput_ops_per_second: values,
        receipts: input
            .pairs
            .iter()
            .map(|pair| {
                [
                    pair.sqlite.receipt.as_str(),
                    pair.terrabase.receipt.as_str(),
                ]
            })
            .collect(),
    };
    let mut file = OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(&args.output)
        .with_context(|| format!("create fresh report {}", args.output.display()))?;
    serde_json::to_writer_pretty(&mut file, &report)?;
    file.write_all(b"\n")?;
    println!(
        "| {} | {:.3} | {:.3} – {:.3} | {} | {} |",
        report.case_id, point_ratio, lower, upper, status, reason
    );
    Ok(())
}
