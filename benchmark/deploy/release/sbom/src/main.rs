use serde_json::{Map, Value, json};
use sha2::{Digest, Sha256};
use std::collections::{BTreeMap, BTreeSet, HashMap, HashSet};
use std::env;
use std::fs::{self, File, OpenOptions};
use std::io::{self, Read, Write};
use std::path::{Path, PathBuf};

const MAX_METADATA_BYTES: u64 = 64 * 1024 * 1024;
const MAX_OUTPUT_BYTES: usize = 64 * 1024 * 1024;
const MAX_ARTIFACT_BYTES: u64 = 16 * 1024 * 1024 * 1024;
const MAX_PACKAGES: usize = 100_000;
const MAX_RESOLUTION_NODES: usize = 100_000;
const MAX_DEPENDENCIES: usize = 2_000_000;
const MAX_ARRAY_ITEMS: usize = 1_000_000;
const MAX_STRING_BYTES: usize = 1_048_576;
const MAX_JSON_DEPTH: usize = 128;

#[derive(Debug)]
struct Options {
    metadata: PathBuf,
    root: String,
    artifact: PathBuf,
    target: String,
    output: PathBuf,
}

fn main() {
    if let Err(error) = run(env::args().skip(1)) {
        eprintln!("{error}");
        std::process::exit(1);
    }
}

fn run(arguments: impl Iterator<Item = String>) -> Result<(), String> {
    let options = parse_options(arguments)?;
    let metadata_bytes = read_bounded_regular(&options.metadata, MAX_METADATA_BYTES, "metadata")?;
    let metadata: Value = serde_json::from_slice(&metadata_bytes)
        .map_err(|error| format!("metadata is not valid JSON: {error}"))?;
    validate_json(&metadata, 0, "metadata")?;
    let artifact_name = file_name(&options.artifact, "artifact")?;
    let artifact_digest = digest_regular(&options.artifact)?;
    let document = build_document(
        &metadata,
        &options.root,
        &options.target,
        &artifact_name,
        &artifact_digest,
    )?;
    let mut encoded = encode_limited(&document)?;
    encoded = ascii_json(&encoded)?;
    if encoded.len() == MAX_OUTPUT_BYTES {
        return Err(format!("generated SBOM exceeds {MAX_OUTPUT_BYTES} bytes"));
    }
    encoded.push(b'\n');
    write_output(&options.output, &encoded)
}

struct LimitedWriter {
    bytes: Vec<u8>,
}

impl Write for LimitedWriter {
    fn write(&mut self, bytes: &[u8]) -> io::Result<usize> {
        if self.bytes.len().saturating_add(bytes.len()) > MAX_OUTPUT_BYTES {
            return Err(io::Error::other("generated SBOM exceeds output limit"));
        }
        self.bytes.extend_from_slice(bytes);
        Ok(bytes.len())
    }

    fn flush(&mut self) -> io::Result<()> {
        Ok(())
    }
}

fn encode_limited(document: &Value) -> Result<Vec<u8>, String> {
    let mut writer = LimitedWriter { bytes: Vec::with_capacity(MAX_OUTPUT_BYTES.min(64 * 1024)) };
    serde_json::to_writer_pretty(&mut writer, document).map_err(|error| {
        if error.to_string().contains("output limit") {
            format!("generated SBOM exceeds {MAX_OUTPUT_BYTES} bytes")
        } else {
            format!("cannot encode CycloneDX document: {error}")
        }
    })?;
    Ok(writer.bytes)
}

fn parse_options(mut arguments: impl Iterator<Item = String>) -> Result<Options, String> {
    let mut metadata = None;
    let mut root = None;
    let mut artifact = None;
    let mut target = None;
    let mut output = None;
    while let Some(argument) = arguments.next() {
        let slot = match argument.as_str() {
            "--metadata" => &mut metadata,
            "--root" => &mut root,
            "--artifact" => &mut artifact,
            "--target" => &mut target,
            "--output" => &mut output,
            "--help" | "-h" => {
                println!(
                    "usage: terrabase-release-sbom --metadata FILE --root PACKAGE --artifact FILE --target TARGET --output FILE"
                );
                return Err(String::new());
            }
            _ => return Err(format!("unknown argument: {argument}")),
        };
        let value = arguments.next().ok_or_else(|| format!("{argument} requires a value"))?;
        if slot.is_some() {
            return Err(format!("duplicate argument: {argument}"));
        }
        *slot = Some(value);
    }
    let metadata = PathBuf::from(metadata.ok_or("--metadata is required")?);
    let root = root.ok_or("--root is required")?;
    let artifact = PathBuf::from(artifact.ok_or("--artifact is required")?);
    let target = target.ok_or("--target is required")?;
    let output = PathBuf::from(output.ok_or("--output is required")?);
    valid_cli_string(&root, "root")?;
    valid_cli_string(&target, "target")?;
    Ok(Options { metadata, root, artifact, target, output })
}

fn valid_cli_string(value: &str, name: &str) -> Result<(), String> {
    if value.is_empty() || value.len() > MAX_STRING_BYTES || value.chars().any(char::is_control) {
        return Err(format!("{name} contains an empty, oversized, or control-character value"));
    }
    if name == "root"
        && !value.bytes().all(|byte| byte.is_ascii_alphanumeric() || b"_.-".contains(&byte))
    {
        return Err("root is not a safe Cargo package name".to_owned());
    }
    Ok(())
}

fn read_bounded_regular(path: &Path, limit: u64, label: &str) -> Result<Vec<u8>, String> {
    regular_file(path, label)?;
    let mut file = File::open(path).map_err(|error| format!("cannot open {label}: {error}"))?;
    let mut bytes = Vec::new();
    Read::by_ref(&mut file)
        .take(limit.saturating_add(1))
        .read_to_end(&mut bytes)
        .map_err(|error| format!("cannot read {label}: {error}"))?;
    if bytes.len() as u64 > limit {
        return Err(format!("{label} exceeds {limit} bytes"));
    }
    Ok(bytes)
}

fn regular_file(path: &Path, label: &str) -> Result<(), String> {
    let metadata =
        fs::symlink_metadata(path).map_err(|error| format!("cannot inspect {label}: {error}"))?;
    if !metadata.file_type().is_file() || metadata.file_type().is_symlink() {
        return Err(format!("{label} is not a safe regular file"));
    }
    Ok(())
}

fn file_name(path: &Path, label: &str) -> Result<String, String> {
    let name = path
        .file_name()
        .and_then(|name| name.to_str())
        .ok_or_else(|| format!("{label} name is not valid UTF-8"))?;
    if name.is_empty() || name.len() > MAX_STRING_BYTES || name.chars().any(char::is_control) {
        return Err(format!(
            "{label} name contains an empty, oversized, or control-character value"
        ));
    }
    Ok(name.to_owned())
}

fn digest_regular(path: &Path) -> Result<String, String> {
    regular_file(path, "artifact")?;
    let metadata =
        fs::metadata(path).map_err(|error| format!("cannot inspect artifact: {error}"))?;
    if metadata.len() > MAX_ARTIFACT_BYTES {
        return Err(format!("artifact exceeds {MAX_ARTIFACT_BYTES} bytes"));
    }
    let mut file = File::open(path).map_err(|error| format!("cannot open artifact: {error}"))?;
    let mut hasher = Sha256::new();
    let mut buffer = [0u8; 1024 * 1024];
    loop {
        let count =
            file.read(&mut buffer).map_err(|error| format!("cannot read artifact: {error}"))?;
        if count == 0 {
            break;
        }
        hasher.update(&buffer[..count]);
    }
    Ok(hex_lower(&hasher.finalize()))
}

fn write_output(path: &Path, bytes: &[u8]) -> Result<(), String> {
    if let Ok(metadata) = fs::symlink_metadata(path)
        && (metadata.file_type().is_symlink() || !metadata.file_type().is_file())
    {
        return Err("output is not a safe regular file".to_owned());
    }
    let mut file = OpenOptions::new()
        .create(true)
        .truncate(true)
        .write(true)
        .open(path)
        .map_err(|error| format!("cannot create output: {error}"))?;
    file.write_all(bytes).map_err(|error| format!("cannot write output: {error}"))?;
    file.sync_all().map_err(|error| format!("cannot flush output: {error}"))
}

fn validate_json(value: &Value, depth: usize, location: &str) -> Result<(), String> {
    if depth > MAX_JSON_DEPTH {
        return Err(format!("{location} exceeds JSON nesting depth"));
    }
    match value {
        Value::String(text) => {
            if text.len() > MAX_STRING_BYTES {
                return Err(format!("{location} contains an oversized string"));
            }
        }
        Value::Array(values) => {
            if values.len() > MAX_ARRAY_ITEMS {
                return Err(format!("{location} contains too many array items"));
            }
            for (index, item) in values.iter().enumerate() {
                validate_json(item, depth + 1, &format!("{location}[{index}]"))?;
            }
        }
        Value::Object(values) => {
            for (key, item) in values {
                validate_json(item, depth + 1, &format!("{location}.{key}"))?;
            }
        }
        Value::Null | Value::Bool(_) | Value::Number(_) => {}
    }
    Ok(())
}

fn build_document(
    metadata: &Value,
    root_name: &str,
    target: &str,
    artifact_name: &str,
    artifact_digest: &str,
) -> Result<Value, String> {
    let packages = metadata
        .get("packages")
        .and_then(Value::as_array)
        .ok_or("Cargo metadata packages is not an array")?;
    if packages.len() > MAX_PACKAGES {
        return Err(format!("Cargo metadata contains more than {MAX_PACKAGES} packages"));
    }
    let mut package_by_id = HashMap::with_capacity(packages.len());
    let mut root_indexes = Vec::new();
    for (index, package) in packages.iter().enumerate() {
        let object =
            package.as_object().ok_or_else(|| format!("package {index} is not an object"))?;
        let id = required_string(object, "id", &format!("package {index}"))?;
        if package_by_id.insert(id.to_owned(), index).is_some() {
            return Err(format!("duplicate Cargo package id: {id}"));
        }
        let name = required_string(object, "name", &format!("package {index}"))?;
        required_string(object, "version", &format!("package {index}"))?;
        validate_license(object, name, &format!("package {index}"))?;
        if name == root_name {
            root_indexes.push(index);
        }
        if let Some(source) = object.get("source")
            && !source.is_null()
            && source.as_str().is_none()
        {
            return Err(format!("package {name} source is not a string"));
        }
        if let Some(source) = object.get("source").and_then(Value::as_str)
            && (source.len() > MAX_STRING_BYTES || source.chars().any(char::is_control))
        {
            return Err(format!(
                "package {name} source contains an oversized or control-character string"
            ));
        }
    }
    if root_indexes.len() != 1 {
        return Err(format!(
            "expected one Cargo package named {root_name}, found {}",
            root_indexes.len()
        ));
    }
    let root_index = root_indexes[0];
    let root = packages[root_index].as_object().expect("validated package object");
    let root_id = required_string(root, "id", "root package")?;
    let (nodes, total_dependencies) = resolution_nodes(metadata, &package_by_id)?;
    if total_dependencies > MAX_DEPENDENCIES {
        return Err(format!("resolved graph contains more than {MAX_DEPENDENCIES} dependencies"));
    }
    let included = reachable(root_id, &nodes, &package_by_id)?;

    let mut components = Vec::new();
    let mut included_ids: Vec<_> = included.iter().cloned().collect();
    included_ids.sort();
    for package_id in &included_ids {
        if package_id == root_id {
            continue;
        }
        let package =
            packages[package_by_id[package_id]].as_object().expect("validated package object");
        let reference = cargo_ref(package)?;
        let mut component = Map::new();
        component.insert("bom-ref".to_owned(), Value::String(reference.clone()));
        component.insert("type".to_owned(), Value::String("library".to_owned()));
        component.insert(
            "name".to_owned(),
            Value::String(required_string(package, "name", "package")?.to_owned()),
        );
        component.insert(
            "version".to_owned(),
            Value::String(required_string(package, "version", "package")?.to_owned()),
        );
        component.insert("purl".to_owned(), Value::String(reference));
        component.insert("licenses".to_owned(), license_value(package)?);
        if let Some(source) =
            package.get("source").and_then(Value::as_str).filter(|source| !source.is_empty())
        {
            component.insert(
                "properties".to_owned(),
                json!([{"name":"terrabase:cargo-source","value":source}]),
            );
        }
        components.push(Value::Object(component));
    }
    components.sort_by(|left, right| left["bom-ref"].as_str().cmp(&right["bom-ref"].as_str()));

    let root_reference = format!(
        "pkg:generic/{root_name}@{}?target={}",
        required_string(root, "version", "root package")?,
        percent_encode(target, "/._-")
    );
    let mut dependencies = Vec::new();
    for package_id in included_ids {
        let reference = if package_id == root_id {
            root_reference.clone()
        } else {
            cargo_ref(
                packages[package_by_id[&package_id]].as_object().expect("validated package object"),
            )?
        };
        let mut depends_on = BTreeSet::new();
        for dependency in nodes.get(&package_id).into_iter().flatten() {
            if included.contains(dependency) && dependency != root_id {
                depends_on.insert(cargo_ref(
                    packages[package_by_id[dependency]]
                        .as_object()
                        .expect("validated package object"),
                )?);
            }
        }
        dependencies
            .push(json!({"ref":reference,"dependsOn":depends_on.into_iter().collect::<Vec<_>>() }));
    }
    let root_licenses = license_value(root)?;
    Ok(json!({
        "bomFormat":"CycloneDX",
        "specVersion":"1.6",
        "version":1,
        "metadata":{"component":{
            "bom-ref":root_reference,
            "type":"application",
            "name":root_name,
            "version":required_string(root,"version","root package")?,
            "hashes":[{"alg":"SHA-256","content":artifact_digest}],
            "licenses":root_licenses,
            "properties":[
                {"name":"terrabase:rust-target","value":target},
                {"name":"terrabase:artifact","value":artifact_name}
            ]
        }},
        "components":components,
        "dependencies":dependencies
    }))
}

fn required_string<'a>(
    object: &'a Map<String, Value>,
    key: &str,
    location: &str,
) -> Result<&'a str, String> {
    let value = object
        .get(key)
        .and_then(Value::as_str)
        .filter(|value| !value.is_empty())
        .ok_or_else(|| format!("{location} {key} is not a non-empty string"))?;
    if value.len() > MAX_STRING_BYTES || value.chars().any(char::is_control) {
        return Err(format!("{location} {key} contains an oversized or control-character string"));
    }
    Ok(value)
}

fn resolution_nodes(
    metadata: &Value,
    package_by_id: &HashMap<String, usize>,
) -> Result<(BTreeMap<String, Vec<String>>, usize), String> {
    let nodes = metadata
        .get("resolve")
        .and_then(|resolve| resolve.get("nodes"))
        .and_then(Value::as_array)
        .ok_or("Cargo metadata did not contain a resolved dependency graph")?;
    if nodes.len() > MAX_RESOLUTION_NODES {
        return Err(format!("resolved graph contains more than {MAX_RESOLUTION_NODES} nodes"));
    }
    let mut result = BTreeMap::new();
    let mut total = 0usize;
    for (index, node) in nodes.iter().enumerate() {
        let object =
            node.as_object().ok_or_else(|| format!("resolved node {index} is not an object"))?;
        let id = required_string(object, "id", &format!("resolved node {index}"))?.to_owned();
        if result.contains_key(&id) {
            return Err(format!("duplicate resolved node id: {id}"));
        }
        let deps = object
            .get("deps")
            .and_then(Value::as_array)
            .ok_or_else(|| format!("resolved node {id} deps is not an array"))?;
        let mut package_ids = Vec::with_capacity(deps.len());
        for (dep_index, dependency) in deps.iter().enumerate() {
            let dep = dependency.as_object().ok_or_else(|| {
                format!("resolved node {id} dependency {dep_index} is not an object")
            })?;
            let package_id =
                required_string(dep, "pkg", &format!("resolved node {id} dependency {dep_index}"))?
                    .to_owned();
            if !package_by_id.contains_key(&package_id) {
                return Err(format!(
                    "resolved dependency references unknown package: {package_id}"
                ));
            }
            package_ids.push(package_id);
            total = total.checked_add(1).ok_or("resolved dependency count overflow")?;
        }
        result.insert(id, package_ids);
    }
    Ok((result, total))
}

fn reachable(
    root_id: &str,
    nodes: &BTreeMap<String, Vec<String>>,
    package_by_id: &HashMap<String, usize>,
) -> Result<HashSet<String>, String> {
    if !package_by_id.contains_key(root_id) {
        return Err("resolved graph does not contain the root package".to_owned());
    }
    let mut pending = vec![root_id.to_owned()];
    let mut found = HashSet::new();
    while let Some(package_id) = pending.pop() {
        if !found.insert(package_id.clone()) {
            continue;
        }
        if found.len() > MAX_PACKAGES {
            return Err("reachable dependency graph is too large".to_owned());
        }
        pending.extend(nodes.get(&package_id).into_iter().flatten().cloned());
    }
    Ok(found)
}

fn cargo_ref(package: &Map<String, Value>) -> Result<String, String> {
    let name = required_string(package, "name", "package")?;
    let version = required_string(package, "version", "package")?;
    Ok(format!("pkg:cargo/{}@{}", percent_encode(name, "._-"), percent_encode(version, "._-+")))
}

fn license_value(package: &Map<String, Value>) -> Result<Value, String> {
    let expression = required_string(package, "license", "package")?;
    Ok(json!([{"expression":expression}]))
}

fn validate_license(
    object: &Map<String, Value>,
    package_name: &str,
    location: &str,
) -> Result<(), String> {
    let expression = required_string(object, "license", location)?;
    parse_spdx(expression).map_err(|error| {
        format!("package {package_name} has invalid SPDX license expression: {error}")
    })
}

fn parse_spdx(expression: &str) -> Result<(), String> {
    let mut parser = SpdxParser { input: expression.as_bytes(), position: 0 };
    parser.expression(false)?;
    parser.skip_space();
    if parser.position != parser.input.len() {
        return Err("unexpected trailing tokens".to_owned());
    }
    Ok(())
}

struct SpdxParser<'a> {
    input: &'a [u8],
    position: usize,
}

impl SpdxParser<'_> {
    fn skip_space(&mut self) {
        while self.input.get(self.position).is_some_and(u8::is_ascii_whitespace) {
            self.position += 1;
        }
    }
    fn expression(&mut self, closing: bool) -> Result<(), String> {
        self.term()?;
        loop {
            self.skip_space();
            if closing && self.input.get(self.position) == Some(&b')') {
                return Ok(());
            }
            if self.input.get(self.position) == Some(&b'/') {
                self.position += 1;
                self.term()?;
                continue;
            }
            let save = self.position;
            if self.keyword("AND") || self.keyword("OR") {
                self.term()?;
                continue;
            }
            self.position = save;
            return Ok(());
        }
    }
    fn term(&mut self) -> Result<(), String> {
        self.skip_space();
        if self.input.get(self.position) == Some(&b'(') {
            self.position += 1;
            self.expression(true)?;
            self.skip_space();
            if self.input.get(self.position) != Some(&b')') {
                return Err("unclosed parenthesis".to_owned());
            }
            self.position += 1;
        } else {
            let identifier = self.identifier()?;
            if !spdx_id(identifier) {
                return Err(format!("unknown SPDX identifier {identifier:?}"));
            }
            self.skip_space();
            let save = self.position;
            if self.keyword("WITH") {
                let exception = self.identifier()?;
                if !matches!(exception, "LLVM-exception" | "Classpath-exception-2.0") {
                    return Err(format!("unknown SPDX exception {exception:?}"));
                }
            } else {
                self.position = save;
            }
        }
        Ok(())
    }
    fn keyword(&mut self, expected: &str) -> bool {
        self.skip_space();
        let end = self.position + expected.len();
        if self.input.get(self.position..end) == Some(expected.as_bytes())
            && self
                .input
                .get(end)
                .is_none_or(|byte| !byte.is_ascii_alphanumeric() && *byte != b'-' && *byte != b'.')
        {
            self.position = end;
            true
        } else {
            false
        }
    }
    fn identifier(&mut self) -> Result<&str, String> {
        self.skip_space();
        let start = self.position;
        while self
            .input
            .get(self.position)
            .is_some_and(|byte| byte.is_ascii_alphanumeric() || b".-+".contains(byte))
        {
            self.position += 1;
        }
        if start == self.position {
            return Err("expected SPDX identifier".to_owned());
        }
        std::str::from_utf8(&self.input[start..self.position])
            .map_err(|_| "SPDX identifier is not UTF-8".to_owned())
    }
}

fn spdx_id(value: &str) -> bool {
    matches!(
        value,
        "0BSD"
            | "Apache-2.0"
            | "BSD-2-Clause"
            | "BSD-3-Clause"
            | "BSL-1.0"
            | "CC0-1.0"
            | "CDLA-Permissive-2.0"
            | "ISC"
            | "LGPL-2.1-or-later"
            | "MIT"
            | "MIT-0"
            | "MPL-2.0"
            | "Unicode-3.0"
            | "Unlicense"
            | "Zlib"
    )
}

fn percent_encode(value: &str, safe: &str) -> String {
    let mut encoded = String::with_capacity(value.len());
    for byte in value.as_bytes() {
        if byte.is_ascii_alphanumeric() || safe.as_bytes().contains(byte) {
            encoded.push(*byte as char);
        } else {
            encoded.push('%');
            encoded.push(hex_digit(byte >> 4));
            encoded.push(hex_digit(byte & 0xf));
        }
    }
    encoded
}

fn ascii_json(bytes: &[u8]) -> Result<Vec<u8>, String> {
    let text =
        std::str::from_utf8(bytes).map_err(|_| "JSON encoder produced invalid UTF-8".to_owned())?;
    let mut output = Vec::with_capacity(bytes.len());
    for character in text.chars() {
        if character.is_ascii() {
            if output.len() == MAX_OUTPUT_BYTES {
                return Err(format!("generated SBOM exceeds {MAX_OUTPUT_BYTES} bytes"));
            }
            output.push(character as u8);
        } else {
            let code = character as u32;
            if code <= 0xffff {
                if output.len().saturating_add(6) > MAX_OUTPUT_BYTES {
                    return Err(format!("generated SBOM exceeds {MAX_OUTPUT_BYTES} bytes"));
                }
                output.extend_from_slice(format!("\\u{code:04x}").as_bytes());
            } else {
                let value = code - 0x1_0000;
                let high = 0xd800 + (value >> 10);
                let low = 0xdc00 + (value & 0x3ff);
                if output.len().saturating_add(12) > MAX_OUTPUT_BYTES {
                    return Err(format!("generated SBOM exceeds {MAX_OUTPUT_BYTES} bytes"));
                }
                output.extend_from_slice(format!("\\u{high:04x}\\u{low:04x}").as_bytes());
            }
        }
    }
    Ok(output)
}

fn hex_lower(bytes: &[u8]) -> String {
    let mut result = String::with_capacity(bytes.len() * 2);
    for byte in bytes {
        result.push(hex_digit(byte >> 4));
        result.push(hex_digit(byte & 0xf));
    }
    result
}

fn hex_digit(value: u8) -> char {
    b"0123456789abcdef"[value as usize] as char
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::time::{SystemTime, UNIX_EPOCH};

    fn metadata(license: &str) -> Value {
        json!({
            "packages":[
                {"id":"root 1.0.0 (path+file:///root)","name":"root","version":"1.0.0","license":"MIT"},
                {"id":"dep 2.0.0 (registry+https://example.invalid)","name":"dep","version":"2.0.0","license":license,"source":"registry+https://example.invalid"}
            ],
            "resolve":{"nodes":[
                {"id":"root 1.0.0 (path+file:///root)","deps":[{"pkg":"dep 2.0.0 (registry+https://example.invalid)"}]},
                {"id":"dep 2.0.0 (registry+https://example.invalid)","deps":[]}
            ]}
        })
    }

    #[test]
    fn output_is_stable_and_sorted() {
        let document = build_document(
            &metadata("MIT OR Apache-2.0"),
            "root",
            "x86_64-unknown-linux-gnu",
            "artifact.oci.tar",
            &"a".repeat(64),
        )
        .expect("document");
        let first = ascii_json(&encode_limited(&document).expect("JSON")).expect("ASCII");
        let second = ascii_json(&encode_limited(&document).expect("JSON")).expect("ASCII");
        assert_eq!(first, second);
        let text = String::from_utf8(first).expect("UTF-8");
        assert!(text.contains("\"components\": ["));
        assert!(text.contains("pkg:cargo/dep@2.0.0"));
    }

    #[test]
    fn malformed_license_and_missing_license_are_rejected() {
        for license in ["not an SPDX expression", "DefinitelyNotSPDX"] {
            assert!(
                build_document(&metadata(license), "root", "target", "artifact", &"a".repeat(64))
                    .is_err()
            );
        }
        let mut missing = metadata("MIT");
        missing["packages"][1].as_object_mut().expect("object").remove("license");
        assert!(build_document(&missing, "root", "target", "artifact", &"a".repeat(64)).is_err());
    }

    #[test]
    fn duplicate_packages_and_control_strings_are_rejected() {
        let mut duplicate = metadata("MIT");
        let package = duplicate["packages"][1].clone();
        duplicate["packages"].as_array_mut().expect("array").push(package);
        assert!(build_document(&duplicate, "root", "target", "artifact", &"a".repeat(64)).is_err());
        let mut hostile = metadata("MIT");
        hostile["packages"][1]["name"] = json!("bad\u{0000}value");
        assert!(build_document(&hostile, "root", "target", "artifact", &"a".repeat(64)).is_err());
    }

    #[test]
    fn oversized_input_and_output_limits_are_rejected() {
        let oversized = Value::String("x".repeat(MAX_STRING_BYTES + 1));
        assert!(validate_json(&oversized, 0, "metadata").is_err());
        let output = vec![b'x'; MAX_OUTPUT_BYTES + 1];
        assert!(output.len() > MAX_OUTPUT_BYTES);
        let mut huge = metadata("MIT");
        huge["packages"].as_array_mut().expect("array").extend((0..MAX_PACKAGES + 1).map(|index| json!({"id":format!("id{index}"),"name":format!("dep{index}"),"version":"1.0.0","license":"MIT"})));
        assert!(build_document(&huge, "root", "target", "artifact", &"a".repeat(64)).is_err());
    }

    #[test]
    fn symlink_input_is_rejected() {
        let directory = env::temp_dir().join(format!(
            "terrabase-sbom-test-{}",
            SystemTime::now().duration_since(UNIX_EPOCH).expect("clock").as_nanos()
        ));
        fs::create_dir(&directory).expect("directory");
        let target = directory.join("target");
        fs::write(&target, b"artifact").expect("artifact");
        let link = directory.join("link");
        std::os::unix::fs::symlink(&target, &link).expect("symlink");
        assert!(regular_file(&link, "artifact").is_err());
        fs::remove_file(&link).expect("remove link");
        fs::remove_file(&target).expect("remove target");
        fs::remove_dir(&directory).expect("remove directory");
    }
}
