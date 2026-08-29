//! The `terrabase-sync-conformance` binary: runs the Rust reference
//! harness over the `terrascale.sync.v2` golden-vector corpora and
//! emits the versioned conformance report (or the corpus integrity
//! manifest).
//!
//! This is the engine-side reference run. An SDK's CI does not need it
//! — the corpus plus the harness contract
//! (`docs/sync/terrascale-sync-v2-conformance.md`) are the whole
//! dependency of a native harness — but any implementation may diff its
//! own report against this one.

use std::fs;
use std::path::PathBuf;
use std::process::ExitCode;

use terrabase_sync_conformance::ConformanceHarness;
use terrabase_sync_conformance::CorpusManifest;

const USAGE: &str = "terrabase-sync-conformance — the terrascale.sync.v2 conformance kit runner

Usage: terrabase-sync-conformance [OPTIONS]

Options:
  --corpus DIR        Add one corpus directory to the run (base role;
                      repeatable). Default: the committed base corpus
                      plus the ADR 0024 extension corpus this crate owns
  --manifest PATH     Gate the run on the corpus integrity manifest
                      (default: none; every corpus file must match)
  --report PATH       Also write the conformance report to this file
  --emit-manifest     Write the corpus integrity manifest to stdout
                      instead of running, and exit 0
  --quiet             Do not print the report to stdout
  --help              Print this usage

Exit codes: 0 the run passed, 1 a conformance failure, 2 a usage or
I/O error. Reports are deterministic: no timestamps, no host paths, no
payload bytes.";

fn main() -> ExitCode {
    match run() {
        Ok(code) => code,
        Err(problem) => {
            eprintln!("terrabase-sync-conformance: {problem}");
            ExitCode::from(2)
        }
    }
}

fn run() -> Result<ExitCode, String> {
    let mut corpora: Vec<PathBuf> = Vec::new();
    let mut manifest: Option<PathBuf> = None;
    let mut report: Option<PathBuf> = None;
    let mut emit_manifest = false;
    let mut quiet = false;
    let mut arguments = std::env::args().skip(1);
    while let Some(argument) = arguments.next() {
        match argument.as_str() {
            "--corpus" => {
                corpora.push(PathBuf::from(arguments.next().ok_or("--corpus needs a directory")?));
            }
            "--manifest" => {
                manifest = Some(PathBuf::from(arguments.next().ok_or("--manifest needs a path")?));
            }
            "--report" => {
                report = Some(PathBuf::from(arguments.next().ok_or("--report needs a path")?));
            }
            "--emit-manifest" => emit_manifest = true,
            "--quiet" => quiet = true,
            "--help" | "-h" => {
                println!("{USAGE}");
                return Ok(ExitCode::SUCCESS);
            }
            other => return Err(format!("unknown argument {other} (try --help)")),
        }
    }
    // The default run is the kit: the committed base corpus plus this
    // crate's ADR 0024 extension corpus. Explicit --corpus flags
    // replace the default set (each with the base coverage role).
    let harness = if corpora.is_empty() {
        ConformanceHarness::kit()
    } else {
        let mut harness =
            ConformanceHarness::new(corpora.first().cloned().expect("the flag list is nonempty"));
        for dir in corpora.iter().skip(1) {
            harness = harness.with_extension(dir.clone());
        }
        harness
    };
    if emit_manifest {
        if manifest.is_some() || report.is_some() {
            return Err("--emit-manifest runs alone (it reads no manifest and writes no report)"
                .to_string());
        }
        let built = CorpusManifest::build(&harness.corpus_dirs()).map_err(|e| e.to_string())?;
        println!("{}", built.to_json_string());
        return Ok(ExitCode::SUCCESS);
    }

    let outcome = match &manifest {
        Some(path) => {
            let text = fs::read_to_string(path)
                .map_err(|e| format!("the manifest {} is unreadable: {e}", path.display()))?;
            let parsed = CorpusManifest::from_json_str(&text).map_err(|e| e.to_string())?;
            harness.run_with_manifest(&parsed)
        }
        None => harness.run(),
    }
    .map_err(|e| e.to_string())?;
    finish(&outcome, report, quiet)
}

/// Writes and prints the report, then maps the exit code.
fn finish(
    conformance: &terrabase_sync_conformance::ConformanceReport,
    report: Option<PathBuf>,
    quiet: bool,
) -> Result<ExitCode, String> {
    let text = conformance.to_json_string();
    if let Some(path) = report {
        fs::write(&path, format!("{text}\n"))
            .map_err(|e| format!("the report {} is unwritable: {e}", path.display()))?;
    }
    if !quiet {
        println!("{text}");
    }
    if conformance.passed() { Ok(ExitCode::SUCCESS) } else { Ok(ExitCode::FAILURE) }
}
