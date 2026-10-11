use serde::Deserialize;
use std::{env, fs::File, io::Read, path::PathBuf, process::ExitCode};

const HELP: &str = "bench-client run --workload <file> --region <gru|sfo> --out <results.jsonl>\n\nCredentials: TS_BENCH_CLIENT_ID / TS_BENCH_CLIENT_SECRET (OCI Vault).\nThis SDK revision cannot satisfy TS-761; run fails before authentication or output.";
const SDK_REV: &str = "ded5adb3b63ce4af0e3a26f95139968cdf539501";
const MAX_WORKLOAD_BYTES: u64 = 1024 * 1024;

#[derive(Debug, PartialEq)]
struct Run {
    workload: PathBuf,
    region: String,
    out: PathBuf,
}

fn parse_args(args: impl IntoIterator<Item = String>) -> Result<Option<Run>, String> {
    let mut args = args.into_iter();
    match args.next().as_deref() {
        Some("--help" | "-h") => return Ok(None),
        Some("run") => (),
        _ => return Err(HELP.into()),
    }
    let (mut workload, mut region, mut out) = (None, None, None);
    while let Some(flag) = args.next() {
        if flag == "--help" || flag == "-h" {
            return Ok(None);
        }
        let slot = match flag.as_str() {
            "--workload" => &mut workload,
            "--region" => &mut region,
            "--out" => &mut out,
            _ => return Err("unknown argument; use --help".into()),
        };
        if slot.is_some() {
            return Err(format!("duplicate {flag}"));
        }
        let value = args.next().ok_or_else(|| format!("missing {flag} value"))?;
        if value.is_empty() || value.starts_with("--") {
            return Err(format!("missing {flag} value"));
        }
        *slot = Some(value);
    }
    let region = region.ok_or("missing --region")?;
    if !matches!(region.as_str(), "gru" | "sfo") {
        return Err("--region must be gru or sfo".into());
    }
    Ok(Some(Run {
        workload: workload.ok_or("missing --workload")?.into(),
        region,
        out: out.ok_or("missing --out")?.into(),
    }))
}

#[derive(Debug, Deserialize)]
#[serde(deny_unknown_fields)]
struct Workload {
    name: String,
    duration_s: u64,
    warmup_s: u64,
    concurrency: u64,
    dataset: Dataset,
    mix: Vec<Mix>,
    targets: Targets,
    ramp: Option<Ramp>,
}

#[derive(Debug, Deserialize)]
#[serde(deny_unknown_fields)]
struct Dataset {
    documents: u64,
    doc_size_bytes: u64,
}

#[derive(Debug, Deserialize)]
#[serde(deny_unknown_fields)]
struct Mix {
    op: Op,
    weight: u64,
    doc_size_bytes: Option<u64>,
}

#[derive(Debug, Deserialize, PartialEq)]
enum Op {
    #[serde(rename = "doc.create")]
    Create,
    #[serde(rename = "doc.get")]
    Get,
    #[serde(rename = "doc.update")]
    Update,
    #[serde(rename = "doc.delete")]
    Delete,
    #[serde(rename = "query.simple")]
    SimpleQuery,
    #[serde(rename = "query.indexed")]
    IndexedQuery,
    #[serde(rename = "txn.multi")]
    Transaction,
}

#[derive(Debug, Deserialize)]
#[serde(deny_unknown_fields)]
struct Targets {
    p99_ms: f64,
}

#[derive(Debug, Deserialize)]
#[serde(deny_unknown_fields)]
struct Ramp {
    step_s: u64,
    concurrency: Vec<u64>,
}

impl Workload {
    fn validate(&self) -> Result<(), String> {
        let name = self.name.as_bytes();
        if name.is_empty()
            || name.len() > 63
            || !name[0].is_ascii_lowercase()
            || !name
                .iter()
                .all(|b| b.is_ascii_lowercase() || b.is_ascii_digit() || *b == b'-')
        {
            return Err("name must match [a-z][a-z0-9-]{0,62}".into());
        }
        if !(1..=600).contains(&self.duration_s) || self.warmup_s > 60 {
            return Err("duration_s must be 1..600; warmup_s must be 0..60".into());
        }
        if !(1..=256).contains(&self.concurrency)
            || !(1..=10_000).contains(&self.dataset.documents)
            || !(32..=1_048_576).contains(&self.dataset.doc_size_bytes)
        {
            return Err("concurrency or dataset outside protocol bounds".into());
        }
        if self.mix.is_empty()
            || self.mix.len() > 7
            || self.mix.iter().enumerate().any(|(index, entry)| {
                !(1..=100).contains(&entry.weight)
                    || entry
                        .doc_size_bytes
                        .is_some_and(|size| !(32..=1_048_576).contains(&size))
                    || self.mix[..index]
                        .iter()
                        .any(|previous| previous.op == entry.op)
            })
            || self.mix.iter().map(|entry| entry.weight).sum::<u64>() != 100
        {
            return Err("mix must have distinct ops, integer weights 1..100 summing to 100, and optional doc_size_bytes 32..1048576".into());
        }
        if !self.targets.p99_ms.is_finite() || !(1.0..=30_000.0).contains(&self.targets.p99_ms) {
            return Err("targets.p99_ms must be finite and 1..30000".into());
        }
        if self.name == "ramp" && self.ramp.is_none() {
            return Err("ramp workload requires ramp steps".into());
        }
        if let Some(ramp) = &self.ramp {
            if !(5..=600).contains(&ramp.step_s)
                || !(2..=7).contains(&ramp.concurrency.len())
                || ramp.step_s * ramp.concurrency.len() as u64 != self.duration_s
                || ramp.concurrency[0] != self.concurrency
                || ramp
                    .concurrency
                    .iter()
                    .any(|value| !(1..=256).contains(value))
                || ramp.concurrency.windows(2).any(|pair| pair[0] >= pair[1])
            {
                return Err("ramp needs 2..7 increasing concurrency steps starting at base concurrency, each 5..600 seconds, totaling duration_s".into());
            }
        }
        Ok(())
    }
}

fn load_workload(path: &PathBuf) -> Result<Workload, String> {
    let file = File::open(path).map_err(|_| "cannot open workload file")?;
    let mut bytes = Vec::new();
    file.take(MAX_WORKLOAD_BYTES + 1)
        .read_to_end(&mut bytes)
        .map_err(|_| "cannot read workload file")?;
    if bytes.len() as u64 > MAX_WORKLOAD_BYTES {
        return Err("workload file exceeds 1 MiB".into());
    }
    // Do not echo user input: workload errors must not disclose credentials.
    let workload: Workload =
        serde_json::from_slice(&bytes).map_err(|_| "invalid workload JSON/schema")?;
    workload.validate()?;
    Ok(workload)
}

fn sdk_blockers(workload: &Workload) -> String {
    let mut gaps = vec![
        "plain ULID document/project IDs (the SDK requires UUIDs)",
        "JWT project-scoped database provisioning and deletion (create uses pre-TS-589 PUT; no public database delete)",
    ];
    if workload
        .mix
        .iter()
        .any(|entry| entry.op == Op::IndexedQuery)
    {
        gaps.push("JWT project-scoped index provisioning for query.indexed");
    }
    format!(
        "SDK_BLOCKED: terrascale 0.1.0 @ {SDK_REV}: {}. See clients/rust/README.md. No credentials read, network requests, database changes or results emitted.",
        gaps.join("; ")
    )
}

fn run(options: Run) -> Result<(), String> {
    let workload = load_workload(&options.workload)?;
    // The pinned SDK has no database cleanup API. Never create a database that
    // this client cannot delete, or silently replace the required plain ULIDs.
    Err(sdk_blockers(&workload))
}

fn main() -> ExitCode {
    match parse_args(env::args().skip(1)).and_then(|options| match options {
        Some(options) => run(options),
        None => {
            println!("{HELP}");
            Ok(())
        }
    }) {
        Ok(()) => ExitCode::SUCCESS,
        Err(error) => {
            eprintln!("{error}");
            ExitCode::FAILURE
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::{json, Value};

    fn fixture() -> Value {
        json!({"name":"point-reads", "duration_s":5, "warmup_s":1,
            "concurrency":2, "dataset":{"documents":10,"doc_size_bytes":128},
            "mix":[{"op":"doc.get","weight":100}], "targets":{"p99_ms":100}})
    }

    #[test]
    fn accepts_all_contract_ops_and_names() {
        for name in [
            "point-reads",
            "write-heavy",
            "mixed-70-30",
            "query",
            "txn",
            "ramp",
        ] {
            for op in [
                "doc.create",
                "doc.get",
                "doc.update",
                "doc.delete",
                "query.simple",
                "query.indexed",
                "txn.multi",
            ] {
                let mut input = fixture();
                input["name"] = json!(name);
                input["mix"][0] = json!({"op":op,"weight":100,"doc_size_bytes":256});
                if name == "ramp" {
                    input["duration_s"] = json!(10);
                    input["ramp"] = json!({"step_s":5, "concurrency":[2,4]});
                }
                serde_json::from_value::<Workload>(input)
                    .unwrap()
                    .validate()
                    .unwrap();
            }
        }
    }

    #[test]
    fn rejects_invalid_values_and_unbounded_runs() {
        for (pointer, value) in [
            ("/name", json!(" ")),
            ("/duration_s", json!(0)),
            ("/duration_s", json!(900)),
            ("/warmup_s", json!(u64::MAX)),
            ("/warmup_s", json!(61)),
            ("/concurrency", json!(0)),
            ("/concurrency", json!(257)),
            ("/dataset/documents", json!(0)),
            ("/dataset/documents", json!(10001)),
            ("/dataset/doc_size_bytes", json!(0)),
            ("/dataset/doc_size_bytes", json!(31)),
            ("/dataset/doc_size_bytes", json!(1048577)),
            ("/mix", json!([])),
            ("/mix/0/weight", json!(0)),
            ("/mix/0/weight", json!(-1)),
            ("/mix/0/weight", json!(1.5)),
            ("/mix/0/weight", json!(99)),
            ("/mix/0/weight", json!(u64::MAX)),
            ("/targets/p99_ms", json!(0)),
            ("/targets/p99_ms", json!(30001)),
        ] {
            let mut input = fixture();
            *input.pointer_mut(pointer).unwrap() = value;
            assert!(
                serde_json::from_value::<Workload>(input)
                    .map_or(true, |workload| workload.validate().is_err()),
                "{pointer}"
            );
        }
        let mut input = fixture();
        input["mix"][0]["doc_size_bytes"] = json!(0);
        assert!(serde_json::from_value::<Workload>(input)
            .unwrap()
            .validate()
            .is_err());
        let mut input = fixture();
        input["duration_s"] = json!(600);
        input["warmup_s"] = json!(60);
        serde_json::from_value::<Workload>(input)
            .unwrap()
            .validate()
            .unwrap();
    }

    #[test]
    fn validates_ramp_and_rejects_duplicate_ops() {
        let mut valid = fixture();
        valid["name"] = json!("ramp");
        valid["duration_s"] = json!(10);
        valid["ramp"] = json!({"step_s":5, "concurrency":[2,4]});
        serde_json::from_value::<Workload>(valid.clone())
            .unwrap()
            .validate()
            .unwrap();
        for ramp in [
            json!(null),
            json!({"step_s":5,"concurrency":[]}),
            json!({"step_s":5,"concurrency":[2]}),
            json!({"step_s":4,"concurrency":[2,4]}),
            json!({"step_s":5,"concurrency":[1,4]}),
            json!({"step_s":5,"concurrency":[2,2]}),
            json!({"step_s":5,"concurrency":[2,1]}),
            json!({"step_s":5,"concurrency":[2,257]}),
            json!({"step_s":u64::MAX,"concurrency":[2,4]}),
            json!({"step_s":5,"concurrency":[2,4,8,16,32,64,128,256]}),
        ] {
            let mut input = valid.clone();
            input["ramp"] = ramp;
            assert!(serde_json::from_value::<Workload>(input)
                .unwrap()
                .validate()
                .is_err());
        }
        valid["mix"] = json!([{"op":"doc.get","weight":50},{"op":"doc.get","weight":50}]);
        assert!(serde_json::from_value::<Workload>(valid)
            .unwrap()
            .validate()
            .is_err());
    }

    #[test]
    fn rejects_unknown_schema_and_operation() {
        for pointer in ["", "/dataset", "/targets", "/mix/0"] {
            let mut input = fixture();
            input.pointer_mut(pointer).unwrap()["secret-field"] = json!("secret");
            assert!(serde_json::from_value::<Workload>(input).is_err());
        }
        let mut input = fixture();
        input["mix"][0]["op"] = json!("query.fake");
        assert!(serde_json::from_value::<Workload>(input).is_err());
    }

    #[test]
    fn validates_cli_flags() {
        let parse = |args: &[&str]| parse_args(args.iter().map(|s| s.to_string()));
        assert_eq!(parse(&["--help"]).unwrap(), None);
        assert_eq!(
            parse(&[
                "run",
                "--out",
                "results.jsonl",
                "--region",
                "sfo",
                "--workload",
                "workload.json"
            ])
            .unwrap(),
            Some(Run {
                workload: "workload.json".into(),
                region: "sfo".into(),
                out: "results.jsonl".into()
            })
        );
        for args in [
            vec![],
            vec!["run"],
            vec!["run", "--region", "fra"],
            vec!["run", "--region", "gru", "--region", "sfo"],
            vec!["run", "--out"],
            vec!["run", "--out", "--region"],
            vec!["run", "--secret", "secret"],
        ] {
            assert!(parse(&args).is_err());
        }
    }

    #[test]
    fn official_sdk_cannot_accept_plain_ulids() {
        assert!("01ARZ3NDEKTSV4RRFFQ69G5FAV"
            .parse::<terrascale::v1::DocumentId>()
            .is_err());
        assert!(terrascale::v1::is_valid_name(
            "bench-01arz3ndektsv4rrffq69g5fav"
        ));
    }

    #[test]
    fn reports_only_relevant_sdk_gaps() {
        let mut input = fixture();
        let basic = sdk_blockers(&serde_json::from_value(input.clone()).unwrap());
        assert!(basic.contains("ULID"));
        assert!(basic.contains("database delete"));
        assert!(!basic.contains("index provisioning"));
        input["mix"][0]["op"] = json!("query.indexed");
        assert!(
            sdk_blockers(&serde_json::from_value(input).unwrap()).contains("index provisioning")
        );
    }
}
