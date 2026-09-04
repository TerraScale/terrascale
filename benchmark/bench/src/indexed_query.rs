//! E-13 production-boundary indexed-query benchmark validator.
//!
//! This dependency-free executable consumes one JSON document from each side
//! adapter. It admits a complete comparison only when all three sides,
//! immutable images, launch-member evidence, and logical-work counters exist.
//! Missing/failed adapters are rendered as unavailable/failed and never as
//! fabricated benchmark numbers.

#![allow(dead_code, reason = "the validator is also compiled as a standalone family binary")]

use std::collections::BTreeMap;
use std::env;
use std::fmt::Write as _;
use std::fs;
use std::path::{Path, PathBuf};

const FAMILY: &str = "E-13 indexed query";
const VERSION: &str = "e-13-indexed-query-production-v1";
const REQUIRED_SAMPLES: usize = 5;
const LAUNCH_OCPU: f64 = 2.0;
const LAUNCH_MEMORY_GIB: f64 = 12.0;
const SIDES: [&str; 3] = ["terrabase", "postgresql-jsonb", "ravendb"];

#[derive(Clone, Debug, PartialEq)]
enum Json {
    Null,
    Bool(bool),
    Number(f64),
    String(String),
    Array(Vec<Json>),
    Object(BTreeMap<String, Json>),
}
impl Json {
    fn object(&self) -> Option<&BTreeMap<String, Json>> {
        match self {
            Self::Object(value) => Some(value),
            _ => None,
        }
    }
    fn string(&self, key: &str) -> Option<&str> {
        self.object()?.get(key)?.as_string()
    }
    fn number(&self, key: &str) -> Option<f64> {
        self.object()?.get(key)?.as_number()
    }
    fn as_string(&self) -> Option<&str> {
        match self {
            Self::String(value) => Some(value),
            _ => None,
        }
    }
    fn as_number(&self) -> Option<f64> {
        match self {
            Self::Number(value) if value.is_finite() => Some(*value),
            _ => None,
        }
    }
}

struct Parser<'a> {
    bytes: &'a [u8],
    offset: usize,
}
impl<'a> Parser<'a> {
    fn new(text: &'a str) -> Self {
        Self { bytes: text.as_bytes(), offset: 0 }
    }
    fn parse(mut self) -> Result<Json, String> {
        let value = self.value()?;
        self.space();
        if self.offset != self.bytes.len() {
            return Err("trailing bytes after JSON document".to_owned());
        }
        Ok(value)
    }
    fn value(&mut self) -> Result<Json, String> {
        self.space();
        match self.bytes.get(self.offset).copied() {
            Some(b'n') => self.literal(b"null", Json::Null),
            Some(b't') => self.literal(b"true", Json::Bool(true)),
            Some(b'f') => self.literal(b"false", Json::Bool(false)),
            Some(b'"') => Ok(Json::String(self.string()?)),
            Some(b'[') => self.array(),
            Some(b'{') => self.object(),
            Some(byte) if byte == b'-' || byte.is_ascii_digit() => self.number(),
            _ => Err(format!("invalid JSON at byte {}", self.offset)),
        }
    }
    fn literal(&mut self, expected: &[u8], value: Json) -> Result<Json, String> {
        if self.bytes.get(self.offset..self.offset + expected.len()) != Some(expected) {
            return Err(format!("invalid JSON literal at byte {}", self.offset));
        }
        self.offset += expected.len();
        Ok(value)
    }
    fn string(&mut self) -> Result<String, String> {
        if self.bytes.get(self.offset) != Some(&b'"') {
            return Err("expected JSON string".to_owned());
        }
        self.offset += 1;
        let mut result = String::new();
        while let Some(byte) = self.bytes.get(self.offset).copied() {
            self.offset += 1;
            match byte {
                b'"' => return Ok(result),
                b'\\' => {
                    let escaped = self.bytes.get(self.offset).copied().ok_or("truncated escape")?;
                    self.offset += 1;
                    match escaped {
                        b'"' | b'\\' | b'/' => result.push(escaped as char),
                        b'b' => result.push('\u{0008}'),
                        b'f' => result.push('\u{000c}'),
                        b'n' => result.push('\n'),
                        b'r' => result.push('\r'),
                        b't' => result.push('\t'),
                        _ => return Err("unsupported JSON escape".to_owned()),
                    }
                }
                byte if byte >= 0x20 => result.push(byte as char),
                _ => return Err("control byte in JSON string".to_owned()),
            }
        }
        Err("unterminated JSON string".to_owned())
    }
    fn number(&mut self) -> Result<Json, String> {
        let start = self.offset;
        while self.bytes.get(self.offset).is_some_and(|byte| {
            byte.is_ascii_digit() || matches!(byte, b'-' | b'+' | b'.' | b'e' | b'E')
        }) {
            self.offset += 1;
        }
        let text = std::str::from_utf8(&self.bytes[start..self.offset]).map_err(|_| "number")?;
        let number = text.parse::<f64>().map_err(|_| format!("invalid number {text:?}"))?;
        if !number.is_finite() {
            return Err("non-finite JSON number".to_owned());
        }
        Ok(Json::Number(number))
    }
    fn array(&mut self) -> Result<Json, String> {
        self.offset += 1;
        let mut values = Vec::new();
        self.space();
        if self.bytes.get(self.offset) == Some(&b']') {
            self.offset += 1;
            return Ok(Json::Array(values));
        }
        loop {
            values.push(self.value()?);
            self.space();
            match self.bytes.get(self.offset).copied() {
                Some(b',') => self.offset += 1,
                Some(b']') => {
                    self.offset += 1;
                    return Ok(Json::Array(values));
                }
                _ => return Err("expected comma or closing array bracket".to_owned()),
            }
        }
    }
    fn object(&mut self) -> Result<Json, String> {
        self.offset += 1;
        let mut values = BTreeMap::new();
        self.space();
        if self.bytes.get(self.offset) == Some(&b'}') {
            self.offset += 1;
            return Ok(Json::Object(values));
        }
        loop {
            self.space();
            let key = self.string()?;
            self.space();
            if self.bytes.get(self.offset) != Some(&b':') {
                return Err("expected colon after JSON object key".to_owned());
            }
            self.offset += 1;
            values.insert(key, self.value()?);
            self.space();
            match self.bytes.get(self.offset).copied() {
                Some(b',') => self.offset += 1,
                Some(b'}') => {
                    self.offset += 1;
                    return Ok(Json::Object(values));
                }
                _ => return Err("expected comma or closing object brace".to_owned()),
            }
        }
    }
    fn space(&mut self) {
        while self.bytes.get(self.offset).is_some_and(u8::is_ascii_whitespace) {
            self.offset += 1;
        }
    }
}

#[derive(Clone, Debug, PartialEq)]
struct Sample {
    p50_us: f64,
    p95_us: f64,
    p99_us: f64,
    rows_returned: f64,
    rows_examined: f64,
    pages_read: f64,
    logical_work: f64,
}
#[derive(Clone, Debug, PartialEq)]
struct SideResult {
    side: String,
    status: String,
    identity: Option<String>,
    boundary: Option<String>,
    image: Option<String>,
    run_digest: Option<String>,
    samples: Vec<Sample>,
    error: Option<String>,
}
#[derive(Clone, Debug, PartialEq)]
struct Admission {
    run_digest: String,
    topology: String,
    images: BTreeMap<String, String>,
    evidence_path: PathBuf,
    evidence_instance: String,
    evidence_shape: String,
}

fn is_sha256(value: &str) -> bool {
    value.len() == 64
        && value.bytes().all(|byte| byte.is_ascii_hexdigit() && !byte.is_ascii_uppercase())
}
fn immutable_image(value: &str) -> Result<(), String> {
    let lowered = value.to_ascii_lowercase();
    if lowered.contains("latest")
        || lowered.contains("localhost")
        || lowered.contains("127.0.0.1")
        || lowered.contains(":local")
        || lowered.contains("placeholder")
    {
        return Err("image must not use a local, placeholder, or latest tag".to_owned());
    }
    let Some((name, digest)) = value.rsplit_once("@sha256:") else {
        return Err("image must be pinned by @sha256:<64 lowercase hex>".to_owned());
    };
    if !is_sha256(digest) {
        return Err("image digest is not a lowercase SHA-256".to_owned());
    }
    if name.is_empty() {
        return Err("image name is empty".to_owned());
    }
    Ok(())
}
fn required_digest(name: &str, value: Option<String>) -> Result<String, String> {
    let value = value.ok_or_else(|| format!("{name} is required"))?;
    if !is_sha256(&value) {
        return Err(format!("{name} must be a lowercase SHA-256 digest"));
    }
    Ok(value)
}
fn read_admission(evidence_path: &Path) -> Result<(String, String), String> {
    let text = fs::read_to_string(evidence_path)
        .map_err(|error| format!("read launch-member resource evidence: {error}"))?;
    let document =
        Parser::new(&text).parse().map_err(|error| format!("resource evidence: {error}"))?;
    let provider = document.string("provider").ok_or("resource evidence provider is required")?;
    let instance =
        document.string("instance_id").ok_or("resource evidence instance_id is required")?;
    let shape = document.string("shape").ok_or("resource evidence shape is required")?;
    if provider != "oci"
        || !instance.starts_with("ocid1.")
        || shape.to_ascii_lowercase().contains("local")
    {
        return Err("resource evidence must identify an OCI launch-member instance".to_owned());
    }
    if document.number("ocpu") != Some(LAUNCH_OCPU)
        || document.number("memory_gib") != Some(LAUNCH_MEMORY_GIB)
    {
        return Err("launch-member resource evidence must be exactly 2 OCPU / 12 GiB".to_owned());
    }
    Ok((instance.to_owned(), shape.to_owned()))
}
fn parse_sample(value: &Json) -> Result<Sample, String> {
    let number = |key: &str| {
        value.number(key).ok_or_else(|| format!("sample field {key} is missing or non-finite"))
    };
    let sample = Sample {
        p50_us: number("p50_us")?,
        p95_us: number("p95_us")?,
        p99_us: number("p99_us")?,
        rows_returned: number("rows_returned")?,
        rows_examined: number("rows_examined")?,
        pages_read: number("pages_read")?,
        logical_work: number("logical_work")?,
    };
    if sample.p50_us < 0.0
        || sample.p50_us > sample.p95_us
        || sample.p95_us > sample.p99_us
        || sample.rows_returned <= 0.0
        || sample.rows_returned.fract() != 0.0
        || sample.rows_examined < sample.rows_returned
        || sample.rows_examined.fract() != 0.0
        || sample.pages_read < 0.0
        || sample.pages_read.fract() != 0.0
        || sample.logical_work.fract() != 0.0
        || sample.logical_work != sample.pages_read + sample.rows_examined
    {
        return Err(
            "sample has invalid percentile, row, page, or logical-work relationship".to_owned()
        );
    }
    Ok(sample)
}
fn parse_side(text: &str, expected_side: &str) -> Result<SideResult, String> {
    let document = Parser::new(text).parse()?;
    let side = document.string("side").ok_or("side result is missing side")?;
    if side != expected_side {
        return Err(format!("side result says {side:?}, expected {expected_side:?}"));
    }
    let status = document.string("status").ok_or("side result is missing status")?.to_owned();
    let samples = match document.object().and_then(|object| object.get("samples")) {
        Some(Json::Array(values)) => {
            values.iter().map(parse_sample).collect::<Result<Vec<_>, _>>()?
        }
        Some(_) => return Err("samples must be an array".to_owned()),
        None => Vec::new(),
    };
    Ok(SideResult {
        side: side.to_owned(),
        status,
        identity: document.string("identity").map(str::to_owned),
        boundary: document.string("boundary").map(str::to_owned),
        image: document.string("image").map(str::to_owned),
        run_digest: document.string("run_digest").map(str::to_owned),
        samples,
        error: document.string("error").map(str::to_owned),
    })
}
fn usable_side(side: &SideResult, admission: &Admission) -> Result<(), String> {
    if side.status != "complete" {
        return Err(side
            .error
            .clone()
            .unwrap_or_else(|| format!("{} side is not complete", side.side)));
    }
    let identity = side.identity.as_deref().ok_or("observed server identity is required")?;
    let identity_lower = identity.to_ascii_lowercase();
    if identity.is_empty()
        || ["unknown", "unassigned", "local", "fake", "diagnostic", "qualification", "placeholder"]
            .iter()
            .any(|word| identity_lower == *word || identity_lower.contains(word))
    {
        return Err("observed server identity is a placeholder".to_owned());
    }
    let boundary = side.boundary.as_deref().ok_or("boundary is required")?;
    let expected_boundary = if side.side == "terrabase" {
        if admission.topology == "rf2" {
            "production-member-rf2"
        } else {
            "production-member-single-node"
        }
    } else {
        "network-reference"
    };
    if boundary != expected_boundary {
        return Err(format!(
            "{} boundary is {boundary:?}, expected {expected_boundary:?}",
            side.side
        ));
    }
    let image = side.image.as_deref().ok_or("immutable image is required")?;
    immutable_image(image)?;
    if admission.images.get(&side.side).map(String::as_str) != Some(image) {
        return Err("side image does not match the immutable invocation input".to_owned());
    }
    if side.run_digest.as_deref() != Some(admission.run_digest.as_str()) {
        return Err("side run digest does not match the invocation digest".to_owned());
    }
    if side.samples.len() < REQUIRED_SAMPLES {
        return Err(format!(
            "{} complete result has fewer than {REQUIRED_SAMPLES} samples",
            side.side
        ));
    }
    Ok(())
}
fn median(values: impl IntoIterator<Item = f64>) -> f64 {
    let mut values: Vec<_> = values.into_iter().collect();
    values.sort_by(f64::total_cmp);
    values[values.len() / 2]
}
fn markdown_number(value: f64) -> String {
    if value.fract() == 0.0 { format!("{value:.0}") } else { format!("{value:.2}") }
}
fn render(
    admission: Option<&Admission>,
    sides: &BTreeMap<String, Result<SideResult, String>>,
    fatal: Option<&str>,
) -> String {
    let complete = fatal.is_none()
        && admission.is_some()
        && sides.len() == SIDES.len()
        && sides.values().all(|side| {
            side.as_ref().is_ok_and(|side| usable_side(side, admission.expect("admission")).is_ok())
        });
    let mut output = String::new();
    let _ = writeln!(output, "# {FAMILY} ({VERSION})\n");
    let _ = writeln!(
        output,
        "Status: **{}**  ",
        if complete { "complete" } else { "failed - fail-closed" }
    );
    let _ = writeln!(
        output,
        "Topology: {} (single-node and RF=2 results are separate comparison groups)  ",
        admission.map_or("unknown", |value| value.topology.as_str())
    );
    let _ = writeln!(
        output,
        "Boundary: production-member-{} (TerraBase served member)  ",
        admission.map_or("unknown", |value| if value.topology == "rf2" {
            "rf2"
        } else {
            "single-node"
        })
    );
    let _ = writeln!(output, "Launch member resource evidence: **2 OCPU / 12 GiB RAM**  ");
    if let Some(admission) = admission {
        let _ = writeln!(output, "Run digest: {}  ", admission.run_digest);
        let _ = writeln!(
            output,
            "Evidence: {} - instance {} - shape {}  ",
            admission.evidence_path.display(),
            admission.evidence_instance,
            admission.evidence_shape
        );
    }
    if let Some(fatal) = fatal {
        let _ = writeln!(output, "Admission error: {}\n", fatal);
    } else {
        output.push('\n');
    }
    output.push_str("| side | status | p50 us | p95 us | p99 us | rows returned | rows examined | pages read | logical work |\n| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |\n");
    for side in SIDES {
        match sides.get(side) {
            None => {
                let _ = writeln!(output, "| {side} | unavailable | - | - | - | - | - | - | - |");
            }
            Some(Err(error)) => {
                let _ = writeln!(
                    output,
                    "| {side} | failed - {} | - | - | - | - | - | - | - |",
                    error.replace('|', "\\|")
                );
            }
            Some(Ok(result)) if !complete => {
                let reason = fatal
                    .map(str::to_owned)
                    .or_else(|| usable_side(result, admission.expect("admission")).err())
                    .unwrap_or_else(|| "comparison admission failed".to_owned());
                let _ = writeln!(
                    output,
                    "| {side} | failed - {} | - | - | - | - | - | - | - |",
                    reason.replace('|', "\\|")
                );
            }
            Some(Ok(result)) => {
                let fields = |field: fn(&Sample) -> f64| median(result.samples.iter().map(field));
                let _ = writeln!(
                    output,
                    "| {side} | complete | {} | {} | {} | {} | {} | {} | {} |",
                    markdown_number(fields(|s| s.p50_us)),
                    markdown_number(fields(|s| s.p95_us)),
                    markdown_number(fields(|s| s.p99_us)),
                    markdown_number(fields(|s| s.rows_returned)),
                    markdown_number(fields(|s| s.rows_examined)),
                    markdown_number(fields(|s| s.pages_read)),
                    markdown_number(fields(|s| s.logical_work))
                );
            }
        }
    }
    output.push_str("\nNumbers are admitted only from side adapters with observed identities, immutable image digests, five or more samples, and logical_work = pages_read + rows_examined. Missing comparators and the served TerraBase member remain failed/unavailable and cannot support a performance claim.\n");
    output
}

/// Runs the indexed-query validator and writes its family report.
pub fn run_cli<I>(arguments: I) -> Result<PathBuf, String>
where
    I: IntoIterator<Item = String>,
{
    let mut output = PathBuf::from("bench/indexed-query.md");
    let mut evidence = None;
    let mut input = BTreeMap::<String, PathBuf>::new();
    let mut missing = BTreeMap::<String, String>::new();
    let mut arguments = arguments.into_iter();
    while let Some(argument) = arguments.next() {
        match argument.as_str() {
            "--output" => output = PathBuf::from(arguments.next().unwrap_or_default()),
            "--resource-evidence" => {
                evidence = Some(PathBuf::from(arguments.next().unwrap_or_default()))
            }
            "--input" => {
                let value = arguments.next().unwrap_or_default();
                let Some((side, path)) = value.split_once('=') else {
                    return Err("--input requires side=path".to_owned());
                };
                input.insert(side.to_owned(), PathBuf::from(path));
            }
            "--missing" => {
                let value = arguments.next().unwrap_or_default();
                let Some((side, reason)) = value.split_once('=') else {
                    return Err("--missing requires side=reason".to_owned());
                };
                missing.insert(side.to_owned(), reason.to_owned());
            }
            other => {
                return Err(format!("unknown argument {other:?}"));
            }
        }
    }
    let mut sides = BTreeMap::new();
    for side in SIDES {
        if let Some(path) = input.get(side) {
            sides.insert(
                side.to_owned(),
                fs::read_to_string(path)
                    .map_err(|error| error.to_string())
                    .and_then(|text| parse_side(&text, side)),
            );
        } else if let Some(reason) = missing.get(side) {
            sides.insert(side.to_owned(), Err(reason.clone()));
        }
    }
    let admission = (|| {
        let run_digest = required_digest(
            "TERRABASE_BENCH_INDEXED_QUERY_RUN_DIGEST",
            env::var("TERRABASE_BENCH_INDEXED_QUERY_RUN_DIGEST").ok(),
        )?;
        let topology = env::var("TERRABASE_BENCH_INDEXED_QUERY_TOPOLOGY")
            .map_err(|_| "TERRABASE_BENCH_INDEXED_QUERY_TOPOLOGY is required".to_owned())?;
        if topology != "single-node" && topology != "rf2" {
            return Err(
                "TERRABASE_BENCH_INDEXED_QUERY_TOPOLOGY must be single-node or rf2".to_owned()
            );
        }
        let evidence_path = evidence
            .or_else(|| {
                env::var_os("TERRABASE_BENCH_INDEXED_QUERY_RESOURCE_EVIDENCE").map(PathBuf::from)
            })
            .ok_or("launch-member resource evidence path is required")?;
        let (instance, shape) = read_admission(&evidence_path)?;
        let mut images = BTreeMap::new();
        for side in SIDES {
            let variable = format!(
                "TERRABASE_BENCH_INDEXED_QUERY_{}_IMAGE",
                side.to_ascii_uppercase().replace('-', "_")
            );
            let image = env::var(&variable).map_err(|_| format!("{variable} is required"))?;
            immutable_image(&image).map_err(|error| format!("{variable}: {error}"))?;
            images.insert(side.to_owned(), image);
        }
        Ok::<_, String>(Admission {
            run_digest,
            topology,
            images,
            evidence_path,
            evidence_instance: instance,
            evidence_shape: shape,
        })
    })();
    let fatal = admission.as_ref().err().map(String::as_str);
    let rendered = render(admission.as_ref().ok(), &sides, fatal);
    if let Some(parent) = output.parent() {
        let _ = fs::create_dir_all(parent);
    }
    if let Err(error) = fs::write(&output, rendered) {
        return Err(format!("write {}: {error}", output.display()));
    }
    if fatal.is_some()
        || sides.len() != SIDES.len()
        || sides.values().any(|side| side.as_ref().is_err())
    {
        return Err(format!(
            "{FAMILY} admission failed closed; report written to {}",
            output.display()
        ));
    }
    Ok(output)
}

fn main() {
    match run_cli(env::args().skip(1)) {
        Ok(output) => println!("{FAMILY} complete report: {}", output.display()),
        Err(error) => {
            eprintln!("{FAMILY} failed closed: {error}");
            std::process::exit(1);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn image_must_be_immutable_and_non_local() {
        assert!(immutable_image("registry/thing:latest").is_err());
        assert!(immutable_image("127.0.0.1/thing@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa").is_err());
        assert!(immutable_image("registry/thing@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa").is_ok());
        assert!(immutable_image("registry/thing@sha256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA").is_err());
    }
    #[test]
    fn logical_work_and_percentiles_are_checked() {
        let json = r#"{"side":"terrabase","status":"complete","samples":[{"p50_us":1,"p95_us":2,"p99_us":3,"rows_returned":1,"rows_examined":4,"pages_read":2,"logical_work":6}]} "#;
        let side = parse_side(json, "terrabase").expect("valid side");
        assert_eq!(side.samples[0].logical_work, 6.0);
        assert!(
            parse_side(&json.replace("\"logical_work\":6", "\"logical_work\":5"), "terrabase")
                .is_err()
        );
    }
    #[test]
    fn diagnostic_boundary_never_passes_production_admission() {
        let json = r#"{"side":"terrabase","status":"complete","identity":"member/rev","boundary":"diagnostic-qualification","image":"registry/t@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","run_digest":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","samples":[]} "#;
        let side = parse_side(json, "terrabase").expect("parse");
        let mut images = BTreeMap::new();
        images.insert("terrabase".to_owned(), side.image.clone().unwrap());
        let admission = Admission {
            run_digest: side.run_digest.clone().unwrap(),
            topology: "rf2".to_owned(),
            images,
            evidence_path: PathBuf::from("evidence"),
            evidence_instance: "ocid1.compute.oc1.x".to_owned(),
            evidence_shape: "VM.Standard.A1.Flex".to_owned(),
        };
        assert!(usable_side(&side, &admission).is_err());
    }

    #[test]
    fn complete_side_requires_the_recorded_sample_floor() {
        let json = r#"{"side":"terrabase","status":"complete","identity":"member/rev","boundary":"production-member-rf2","image":"registry/t@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","run_digest":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","samples":[]}"#;
        let side = parse_side(json, "terrabase").expect("parse");
        let mut images = BTreeMap::new();
        images.insert("terrabase".to_owned(), side.image.clone().unwrap());
        let admission = Admission {
            run_digest: side.run_digest.clone().unwrap(),
            topology: "rf2".to_owned(),
            images,
            evidence_path: PathBuf::from("evidence"),
            evidence_instance: "ocid1.compute.oc1.x".to_owned(),
            evidence_shape: "VM.Standard.A1.Flex".to_owned(),
        };
        let error = usable_side(&side, &admission).expect_err("empty samples cannot be complete");
        assert!(error.contains("fewer than 5 samples"));
    }
}
